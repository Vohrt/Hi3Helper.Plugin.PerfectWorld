using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.PerfectWorld.Core.Management.Api;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.PerfectWorld.Core.Management;

/// <summary>
///     <see cref="IGameManager"/> implementation for Perfect World pw_sdk titles. Handles version
///     detection (local install vs remote <c>config.xml</c>) and install-state discovery.
/// </summary>
[GeneratedComClass]
public partial class PerfectWorldGameManager : GameManagerBase
{
    /// <summary>Plugin-owned marker that records the installed resource version.</summary>
    internal const string StateFileName = "collapse_perfectworld_state.ini";

    /// <summary>
    ///     Legacy marker name used before the Wanmei → PerfectWorld rename. Still read (and cleaned up on the
    ///     next write) so installations created by earlier plugin builds keep their recorded version.
    /// </summary>
    internal const string LegacyStateFileName = "collapse_wanmei_state.ini";

    private readonly PerfectWorldGameConfig _config;
    private PerfectWorldRemoteConfig? _remoteConfig;
    private bool _isInitialized;

    // Coordinates the fire-and-forget re-init started by SetGamePathInner with path changes and disposal.
    //  * _lifetimeCts is cancelled on Dispose so no background task keeps using the (disposed) HttpClient.
    //  * _currentInitCts supersedes the previous in-flight re-init when the game path changes again.
    //  * _initGeneration is the atomic supersession token: a background init only commits its results while it is
    //    still the latest generation, closing the race where a stale init overwrites a newer path's version state.
    // All three, plus _isInitialized, are only mutated under _initSync (never held across an await).
    private readonly object _initSync = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _currentInitCts;
    private int _initGeneration;
    private bool _disposed;

    public PerfectWorldGameManager(PerfectWorldGameConfig config)
    {
        _config = config;
    }

    public PerfectWorldGameConfig Config => _config;

    protected override HttpClient ApiResponseHttpClient { get; set; } = new PluginHttpClientBuilder()
        .SetAllowedDecompression(DecompressionMethods.All)
        .AllowRedirections()
        .AllowUntrustedCert()
        .AllowCookies()
        .Create();

    protected override bool IsInstalled
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentGameInstallPath)) return false;
            if (CurrentGameVersion == GameVersion.Empty) return false;

            string exePath = Path.Combine(CurrentGameInstallPath, _config.GameExecutableRelativePath);
            if (!File.Exists(exePath)) return false;

            // Require the completeness markers too, so a partial install (main executable present but the packed
            // runtime files still missing) is correctly reported as not-yet-installed instead of ready-to-launch.
            foreach (string marker in _config.InstallMarkerRelativePaths)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                if (!File.Exists(Path.Combine(CurrentGameInstallPath, marker))) return false;
            }

            return true;
        }
    }

    protected override bool HasUpdate =>
        IsInstalled && ApiGameVersion != GameVersion.Empty && CurrentGameVersion != ApiGameVersion;

    // pw_sdk exposes upcoming versions through the same delta channel; a dedicated preload feed is not used.
    protected override bool HasPreload => false;

    protected override GameVersion ApiGameVersion { get; set; } = GameVersion.Empty;
    protected override GameVersion ApiPreloadGameVersion { get; set; } = GameVersion.Empty;

    /// <summary>
    ///     Returns the cached remote config, fetching it once if required.
    /// </summary>
    internal async Task<PerfectWorldRemoteConfig?> GetRemoteConfigAsync(bool forceRefresh, CancellationToken token)
    {
        if (!forceRefresh && _remoteConfig != null) return _remoteConfig;

        foreach (string cdnRoot in _config.GameResCdnUrls)
        {
            string url = _config.BuildConfigXmlUrl(cdnRoot);
            try
            {
                string xml = await ApiResponseHttpClient.GetStringAsync(url, token).ConfigureAwait(false);
                _remoteConfig = PerfectWorldRemoteConfig.Parse(xml);
                SharedStatic.InstanceLogger.LogInformation(
                    "[PerfectWorldManager] Remote config: ResVersion={Version}, ResSize={Size}",
                    _remoteConfig.ResVersion, _remoteConfig.ResSize);
                return _remoteConfig;
            }
            // Only a genuine caller cancellation should abort the CDN loop. An HttpClient *timeout* also surfaces as
            // an OperationCanceledException/TaskCanceledException but with the caller's token NOT cancelled, and that
            // must fall through to the next CDN like any other transient failure.
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                SharedStatic.InstanceLogger.LogWarning("[PerfectWorldManager] Failed to fetch config.xml from {Url}: {Msg}",
                    url, ex.Message);
            }
        }

        return _remoteConfig;
    }

    internal Task<int> InitAsyncInner(bool forceInit, CancellationToken token)
        => InitAsyncInner(forceInit, token, generation: null);

    private async Task<int> InitAsyncInner(bool forceInit, CancellationToken token, int? generation)
    {
        if (!forceInit && _isInitialized) return 0;

        // Remote available version (the slow part; done outside any lock).
        PerfectWorldRemoteConfig? remote = await GetRemoteConfigAsync(true, token).ConfigureAwait(false);

        // Read the local installed version AFTER the fetch so a concurrent install/update that finished while the
        // request was in flight is reflected, rather than overwritten by a stale pre-await snapshot.
        string? localVersion = ReadInstalledResVersion();
        GameVersion localGameVersion =
            string.IsNullOrEmpty(localVersion) ? GameVersion.Empty : new GameVersion(localVersion);
        GameVersion? apiGameVersion = remote != null && !string.IsNullOrEmpty(remote.ResVersion)
            ? new GameVersion(remote.ResVersion)
            : null;

        lock (_initSync)
        {
            // Commit nothing if this init was superseded (a newer SetGamePath bumped the generation) or the manager
            // was disposed/cancelled. This is the atomic point that stops a stale background init from clobbering the
            // current path's version state. A null generation means a foreground (host-awaited) init, never superseded.
            token.ThrowIfCancellationRequested();
            if (generation.HasValue && generation.Value != _initGeneration)
                throw new OperationCanceledException();

            CurrentGameVersion = localGameVersion;
            if (apiGameVersion.HasValue)
                ApiGameVersion = apiGameVersion.Value;

            _isInitialized = true;
        }

        return 0;
    }

    protected override Task<int> InitAsync(CancellationToken token) => InitAsyncInner(true, token);

    /// <summary>
    ///     Reads the currently installed resource version. Prefers the plugin-owned state file and falls back
    ///     to the official pw_sdk local <c>config.xml</c> so pre-existing installations are recognised.
    /// </summary>
    internal string? ReadInstalledResVersion()
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath)) return null;

        // Prefer the current marker; fall back to the pre-rename marker so existing installs stay recognised.
        string? recorded = TryReadResVersionFromStateFile(Path.Combine(CurrentGameInstallPath, StateFileName))
                           ?? TryReadResVersionFromStateFile(Path.Combine(CurrentGameInstallPath, LegacyStateFileName));
        if (!string.IsNullOrEmpty(recorded)) return recorded;

        // Fallback: the official launcher stores a plaintext config.xml with <ResVersion>.
        string officialConfig = Path.Combine(CurrentGameInstallPath,
            _config.LauncherRootDirName, "UserData", "Patcher", "PatcherSDK", "config.xml");
        if (File.Exists(officialConfig))
        {
            try
            {
                var parsed = PerfectWorldRemoteConfig.Parse(File.ReadAllText(officialConfig));
                if (!string.IsNullOrEmpty(parsed.ResVersion)) return parsed.ResVersion;
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning("[PerfectWorldManager] Could not read official config.xml: {Msg}",
                    ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    ///     Reads the <c>ResVersion</c> value from a plugin-owned state file, or <c>null</c> if the file is
    ///     missing or does not contain the marker.
    /// </summary>
    private static string? TryReadResVersionFromStateFile(string stateFile)
    {
        if (!File.Exists(stateFile)) return null;

        foreach (string line in File.ReadAllLines(stateFile))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("ResVersion=", StringComparison.OrdinalIgnoreCase))
            {
                string value = trimmed["ResVersion=".Length..].Trim();
                if (!string.IsNullOrEmpty(value)) return value;
            }
        }

        return null;
    }

    /// <summary>
    ///     Persists the installed resource version to the plugin-owned state file.
    /// </summary>
    internal void WriteInstalledResVersion(string resVersion)
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath)) return;

        try
        {
            Directory.CreateDirectory(CurrentGameInstallPath);
            string stateFile = Path.Combine(CurrentGameInstallPath, StateFileName);
            File.WriteAllText(stateFile, $"ResVersion={resVersion}{Environment.NewLine}");

            // Migration cleanup: drop the pre-rename marker once the new one is written.
            string legacyStateFile = Path.Combine(CurrentGameInstallPath, LegacyStateFileName);
            if (File.Exists(legacyStateFile))
            {
                try { File.Delete(legacyStateFile); } catch { /* best effort */ }
            }

            CurrentGameVersion = new GameVersion(resVersion);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning("[PerfectWorldManager] Failed to write state file: {Msg}", ex.Message);
        }
    }

    protected override void SetGamePathInner(string gamePath)
    {
        CurrentGameInstallPath = gamePath;

        lock (_initSync)
        {
            _isInitialized = false;

            // Supersede any previous in-flight background init: bump the generation (so a late commit is rejected)
            // and cancel its token.
            _initGeneration++;
            if (_currentInitCts is { } previous)
            {
                try { previous.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
                _currentInitCts = null;
            }

            if (_disposed || string.IsNullOrEmpty(gamePath)) return;

            CancellationTokenSource cts;
            try
            {
                cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            }
            catch (ObjectDisposedException)
            {
                return; // manager already disposed; nothing to initialize
            }

            _currentInitCts = cts;
            int myGeneration = _initGeneration;
            CancellationToken initToken = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await InitAsyncInner(true, initToken, myGeneration).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer SetGamePath, or the manager was disposed. Nothing to commit.
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogError("[PerfectWorldManager] Re-initialization failed: {Msg}", ex.Message);
                }
            }, initToken);
        }
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
    {
        CurrentGameVersion = gameVersion;
    }

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token)
    {
        return Task.FromResult<string?>(null);
    }

    public override void LoadConfig() { }

    public override void SaveConfig() { }

    public override void Dispose()
    {
        // Stop any fire-and-forget background init before tearing down the HttpClient it uses, so it cannot touch a
        // disposed client. The per-call linked sources are intentionally left for GC rather than disposed here,
        // because the background task may still hold their tokens; they carry no unmanaged handles.
        lock (_initSync)
        {
            _disposed = true;
            _initGeneration++;
            if (_currentInitCts is { } current)
            {
                try { current.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
                _currentInitCts = null;
            }
        }

        try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        _lifetimeCts.Dispose();

        base.Dispose();
        ApiResponseHttpClient?.Dispose();
    }
}
