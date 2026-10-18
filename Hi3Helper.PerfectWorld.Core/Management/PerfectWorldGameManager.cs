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
using Hi3Helper.PerfectWorld.Core.Utils;
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

    // The branch the current _remoteConfig was actually read from (see EffectiveGameResBranch): the seed branch from
    // the preset, the vendor's on-disk PatcherConfig.json, or the branch advertised inside config.xml.
    private string? _effectiveGameResBranch;

    // 0 = the installed version has not been resolved from disk yet for the current game path. Reset whenever the path
    // changes, so EnsureLocalVersionProbed() re-reads it for the new location.
    private int _localVersionProbeDone;

    private bool _isInitialized;

    /// <summary>
    ///     Whether the installed vendor launcher is older than the version currently advertised on the CDN. Set during
    ///     <see cref="InitAsyncInner"/> and cleared after a successful launcher install/update. When <see langword="true"/>
    ///     it is folded into <see cref="HasUpdate"/> so the Collapse UI shows "Update available" rather than "Start",
    ///     preventing the launcher from blocking the auto-click path with its own self-update prompt.
    /// </summary>
    private bool _launcherHasUpdate;

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
        IsInstalled && (
            (ApiGameVersion != GameVersion.Empty && CurrentGameVersion != ApiGameVersion) ||
            _launcherHasUpdate
        );

    /// <summary>
    ///     The installed version, resolved from disk on first read when nothing has populated it yet.
    /// </summary>
    /// <remarks>
    ///     The host asks for the install state <em>synchronously</em> (its plugin-side <c>IGameVersion.GetGameState</c>
    ///     never awaits anything) immediately after it hands the game path over. Our initialization cannot be awaited
    ///     from there — it has to fetch the remote <c>config.xml</c> — so on a cold start the host used to observe an
    ///     empty version and report the game as <c>NotInstalled</c> ("安装/定位游戏" on a fully installed game) until the
    ///     user switched to another game and back, which by then reused the values the first initialization had
    ///     committed. Resolving the cheap, local half (the state file / official <c>config.xml</c>) lazily on read
    ///     closes that window; the remote half is primed by the preset's <c>InitAsync</c> at plugin load.
    /// </remarks>
    protected override GameVersion CurrentGameVersion
    {
        get
        {
            EnsureLocalVersionProbed();
            return base.CurrentGameVersion;
        }
        set => base.CurrentGameVersion = value;
    }

    // pw_sdk exposes upcoming versions through the same delta channel; a dedicated preload feed is not used.
    protected override bool HasPreload => false;

    protected override GameVersion ApiGameVersion { get; set; } = GameVersion.Empty;
    protected override GameVersion ApiPreloadGameVersion { get; set; } = GameVersion.Empty;

    /// <summary>
    ///     The game-resource branch the current remote config actually lives on.
    /// </summary>
    /// <remarks>
    ///     Normally <see cref="PerfectWorldGameConfig.GameResBranch"/>, but a <c>pw_sdk</c> title can move its live
    ///     resources to a different branch at a major version bump and leave the old one frozen on its last manifest
    ///     (异环/NTE 1.4: <c>publish_PC</c> → <c>PC_140</c>; a plugin that keeps reading the old branch keeps seeing the
    ///     previous version and never reports the update). Resolved by <see cref="GetRemoteConfigAsync"/> from, in order:
    ///     the vendor's on-disk <c>PatcherConfig.json</c>, and the branch advertised inside the fetched
    ///     <c>config.xml</c>. Every resource URL — and the forged <c>PatcherSDK</c> state's <c>LocalBranch</c> — must be
    ///     built with this value rather than the seed branch.
    /// </remarks>
    internal string EffectiveGameResBranch => _effectiveGameResBranch ?? _config.GameResBranch;

    /// <summary>
    ///     Returns the cached remote config, fetching it once if required.
    /// </summary>
    internal async Task<PerfectWorldRemoteConfig?> GetRemoteConfigAsync(bool forceRefresh, CancellationToken token)
    {
        if (!forceRefresh && _remoteConfig != null) return _remoteConfig;

        // Start from the branch the vendor's own patcher configuration declares (it is rewritten when the title moves
        // to a new resource branch during a launcher update), falling back to the preset's seed branch.
        string branch = ResolveSeedGameResBranch();
        PerfectWorldRemoteConfig? remote = await FetchRemoteConfigAsync(branch, token).ConfigureAwait(false);

        if (remote != null && !string.IsNullOrEmpty(remote.ResourceBranch) &&
            !string.Equals(remote.ResourceBranch, branch, StringComparison.OrdinalIgnoreCase))
        {
            // The config we just read advertises a different live branch. The branch we read from is frozen at the
            // version it was retired on, so re-read config.xml from the advertised branch to get the current version.
            // This mirrors the vendor patcher (its log shows LocalBranch → RemoteBranch) and is what keeps the plugin
            // from reporting a stale "already up to date".
            SharedStatic.InstanceLogger.LogInformation(
                "[PerfectWorldManager] config.xml advertises resource branch {Declared} (read from {Current}); re-reading from the advertised branch.",
                remote.ResourceBranch, branch);

            PerfectWorldRemoteConfig? advertised = await FetchRemoteConfigAsync(remote.ResourceBranch, token)
                .ConfigureAwait(false);
            if (advertised != null)
            {
                branch = remote.ResourceBranch;
                remote = advertised;
            }
        }

        if (remote == null) return _remoteConfig;

        _effectiveGameResBranch = branch;
        _remoteConfig = remote;
        SharedStatic.InstanceLogger.LogInformation(
            "[PerfectWorldManager] Remote config: branch={Branch}, ResVersion={Version}, ResSize={Size}",
            branch, remote.ResVersion, remote.ResSize);
        return _remoteConfig;
    }

    /// <summary>
    ///     Fetches and parses <c>config.xml</c> from <paramref name="branch"/> on every configured CDN in order.
    /// </summary>
    private async Task<PerfectWorldRemoteConfig?> FetchRemoteConfigAsync(string branch, CancellationToken token)
    {
        foreach (string cdnRoot in _config.GameResCdnUrls)
        {
            string url = _config.BuildConfigXmlUrl(cdnRoot, branch);
            try
            {
                string xml = await ApiResponseHttpClient.GetStringAsync(url, token).ConfigureAwait(false);
                return PerfectWorldRemoteConfig.Parse(xml);
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

        return null;
    }

    /// <summary>
    ///     Resolves the branch to read <c>config.xml</c> from: the <c>branchName</c> declared by the vendor's installed
    ///     <c>PatcherConfig.json</c> when present, otherwise the preset's seed branch.
    /// </summary>
    private string ResolveSeedGameResBranch()
    {
        string fallback = _config.GameResBranch;
        if (string.IsNullOrEmpty(CurrentGameInstallPath) ||
            string.IsNullOrEmpty(_config.LauncherPatcherConfigRelativePath))
            return fallback;

        try
        {
            string path = Path.Combine(CurrentGameInstallPath, _config.LauncherPatcherConfigRelativePath);
            if (!File.Exists(path)) return fallback;

            string? declared = PerfectWorldJson.TryGetString(File.ReadAllText(path), "branchName")?.Trim();
            if (!string.IsNullOrEmpty(declared) &&
                !string.Equals(declared, fallback, StringComparison.OrdinalIgnoreCase))
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[PerfectWorldManager] Vendor PatcherConfig.json declares resource branch {Declared} (preset seed: {Seed}).",
                    declared, fallback);
            }

            return string.IsNullOrEmpty(declared) ? fallback : declared;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[PerfectWorldManager] Could not read the vendor PatcherConfig.json (using seed branch {Branch}): {Msg}",
                fallback, ex.Message);
            return fallback;
        }
    }

    internal Task<int> InitAsyncInner(bool forceInit, CancellationToken token)
        => InitAsyncInner(forceInit, token, generation: null);

    private async Task<int> InitAsyncInner(bool forceInit, CancellationToken token, int? generation)
    {
        if (!forceInit && _isInitialized) return 0;

        // Remote available version (the slow part; done outside any lock).
        PerfectWorldRemoteConfig? remote = await GetRemoteConfigAsync(true, token).ConfigureAwait(false);

        // Check whether the installed vendor launcher is up-to-date. This prevents the launcher's own self-update
        // prompt from blocking the auto-click path (which expects the "开始游戏" button, not an update UI).
        bool launcherHasUpdate = false;
        if (!string.IsNullOrEmpty(CurrentGameInstallPath) && _config.LauncherCdnUrls.Length > 0)
            launcherHasUpdate = await CheckLauncherHasUpdateAsync(token).ConfigureAwait(false);

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

            // A path-less initialization (started from the preset's InitAsync at plugin load) has no local version to
            // report; committing an empty one would clobber what a path-bound initialization already resolved.
            if (!string.IsNullOrEmpty(CurrentGameInstallPath))
                CurrentGameVersion = localGameVersion;
            if (apiGameVersion.HasValue)
                ApiGameVersion = apiGameVersion.Value;
            _launcherHasUpdate = launcherHasUpdate;

            _isInitialized = true;
        }

        return 0;
    }

    protected override Task<int> InitAsync(CancellationToken token) => InitAsyncInner(true, token);

    /// <summary>
    ///     Runs the full initialization (remote config + local state) on demand, without waiting for the host to set the
    ///     game path.
    /// </summary>
    /// <remarks>
    ///     Called from the thin presets' <c>InitAsync</c>. The host awaits that once, at plugin load
    ///     (<c>PluginManager</c> → <c>PluginInfo.Initialize</c>), which is the last startup hook that can run before the
    ///     home page performs its first — synchronous — game-state query. Without it the API version is still unknown at
    ///     that point, so even a correctly detected install would render as up-to-date instead of "update available".
    /// </remarks>
    public Task<int> EnsureInitializedAsync(CancellationToken token) => InitAsyncInner(true, token);

    /// <summary>
    ///     Determines whether the installed vendor launcher is older than the build advertised on the CDN by comparing
    ///     the launcher's own local version record (<see cref="PerfectWorldGameConfig.LauncherVersionIniRelativePath"/>,
    ///     i.e. <c>{LauncherRootDirName}\Config\Config.ini</c> → <c>[VERSION]</c>) against the remote <c>Version.ini</c>.
    ///     Returns <see langword="true"/> when the local <c>Version</c>/<c>Build</c> differ from the remote ones. A
    ///     missing local record (launcher not laid down yet), an unreadable version on either side, or any network/other
    ///     transient failure is treated as "no update known" so an unreachable CDN never blocks a launch.
    /// </summary>
    private async Task<bool> CheckLauncherHasUpdateAsync(CancellationToken token)
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath)) return false;

        try
        {
            // Fetch the remote launcher Version.ini (the vendor's own self-update descriptor).
            byte[]? remoteBytes = null;
            foreach (string cdn in _config.LauncherCdnUrls)
            {
                try
                {
                    string url = _config.BuildLauncherVersionIniUrl(cdn);
                    remoteBytes = await ApiResponseHttpClient.GetByteArrayAsync(url, token).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[PerfectWorldManager] Could not fetch launcher Version.ini from {Cdn}: {Msg}", cdn, ex.Message);
                }
            }

            if (remoteBytes == null) return false;

            string remoteIni = System.Text.Encoding.UTF8.GetString(remoteBytes);
            (string? remoteVersion, string? remoteBuild) =
                PerfectWorldLauncherManifestParser.ParseSectionVersionBuild(remoteIni);
            // Without any remote version there is nothing to compare against.
            if (string.IsNullOrEmpty(remoteVersion) && string.IsNullOrEmpty(remoteBuild)) return false;

            // The vendor launcher records its currently-installed build in {LauncherRootDirName}\Config\Config.ini
            // (NOT a top-level Version.ini — that file does not exist for these titles). The plugin downloads this file
            // as part of the launcher self-update manifest (AllFiles.xml) and the vendor patcher rewrites it after a
            // self-update, so it always reflects the launcher that is actually installed on disk.
            string localVersionIniPath = Path.Combine(CurrentGameInstallPath,
                _config.LauncherRootDirName, _config.LauncherVersionIniRelativePath);
            if (!File.Exists(localVersionIniPath))
            {
                // No local record: the launcher has not been laid down yet (pre-first-run). Treat as up-to-date — the
                // installer already fetches the current launcher build.
                return false;
            }

            string localIni = await File.ReadAllTextAsync(localVersionIniPath, token).ConfigureAwait(false);
            (string? localVersion, string? localBuild) =
                PerfectWorldLauncherManifestParser.ParseSectionVersionBuild(localIni);
            // An unreadable local version is treated as up-to-date so a parse quirk can never wedge a permanent
            // "update available" state that the user would be unable to clear.
            if (string.IsNullOrEmpty(localVersion) && string.IsNullOrEmpty(localBuild)) return false;

            bool buildDiffers = !string.IsNullOrEmpty(remoteBuild) &&
                                !string.Equals(localBuild, remoteBuild, StringComparison.OrdinalIgnoreCase);
            bool versionDiffers = !string.IsNullOrEmpty(remoteVersion) &&
                                  !string.Equals(localVersion, remoteVersion, StringComparison.OrdinalIgnoreCase);
            bool hasUpdate = buildDiffers || versionDiffers;

            if (hasUpdate)
                SharedStatic.InstanceLogger.LogInformation(
                    "[PerfectWorldManager] Launcher update available: local={LocalVersion} (build {LocalBuild}) " +
                    "remote={RemoteVersion} (build {RemoteBuild}).",
                    localVersion ?? "(none)", localBuild ?? "(none)",
                    remoteVersion ?? "(none)", remoteBuild ?? "(none)");

            return hasUpdate;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[PerfectWorldManager] Launcher update check failed (treating as up-to-date): {Msg}", ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Clears the launcher-has-update flag after the installer has successfully updated the launcher. Called by
    ///     <see cref="PerfectWorldGameInstaller"/> once the launcher plan has been downloaded and applied.
    /// </summary>
    internal void ClearLauncherHasUpdate()
    {
        lock (_initSync)
        {
            _launcherHasUpdate = false;
        }
    }

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

    /// <summary>
    ///     Resolves the installed version from disk the first time it is read, when nothing has populated it yet.
    /// </summary>
    /// <remarks>
    ///     The host's plugin-side state query is synchronous and happens as soon as it hands the game path over, while
    ///     our initialization still has to fetch the remote <c>config.xml</c>. Reading the local marker (a tiny INI, or
    ///     the official <c>config.xml</c> as a fallback) is pure disk I/O and cannot race anything, so doing it lazily
    ///     keeps the install state correct from the very first query. Only the first caller per install path performs
    ///     the read; the flag is reset by <see cref="SetGamePathInner"/> when the path changes.
    /// </remarks>
    private void EnsureLocalVersionProbed()
    {
        if (Volatile.Read(ref _localVersionProbeDone) != 0) return;
        if (Interlocked.Exchange(ref _localVersionProbeDone, 1) != 0) return;

        // Read through the base accessor: this runs from the property override, which would recurse otherwise.
        if (base.CurrentGameVersion != GameVersion.Empty) return;

        string? localVersion = ReadInstalledResVersion();
        if (string.IsNullOrEmpty(localVersion)) return;

        base.CurrentGameVersion = new GameVersion(localVersion);
        SharedStatic.InstanceLogger?.LogInformation(
            "[PerfectWorldManager] Resolved installed version {Version} from disk while initialization is still in flight.",
            localVersion);
    }

    protected override void SetGamePathInner(string gamePath)
    {
        CurrentGameInstallPath = gamePath;

        // A different path may hold a different install (or none): let the next version read re-probe from disk.
        Volatile.Write(ref _localVersionProbeDone, 0);

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
