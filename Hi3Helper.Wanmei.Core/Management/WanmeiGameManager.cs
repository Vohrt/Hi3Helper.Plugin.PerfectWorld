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
using Hi3Helper.Wanmei.Core.Management.Api;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Wanmei.Core.Management;

/// <summary>
///     <see cref="IGameManager"/> implementation for Perfect World (Wanmei) pw_sdk titles. Handles version
///     detection (local install vs remote <c>config.xml</c>) and install-state discovery.
/// </summary>
[GeneratedComClass]
public partial class WanmeiGameManager : GameManagerBase
{
    /// <summary>Plugin-owned marker that records the installed resource version.</summary>
    internal const string StateFileName = "collapse_wanmei_state.ini";

    private readonly WanmeiGameConfig _config;
    private WanmeiRemoteConfig? _remoteConfig;
    private bool _isInitialized;

    public WanmeiGameManager(WanmeiGameConfig config)
    {
        _config = config;
    }

    public WanmeiGameConfig Config => _config;

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
            return File.Exists(exePath);
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
    internal async Task<WanmeiRemoteConfig?> GetRemoteConfigAsync(bool forceRefresh, CancellationToken token)
    {
        if (!forceRefresh && _remoteConfig != null) return _remoteConfig;

        foreach (string cdnRoot in _config.GameResCdnUrls)
        {
            string url = _config.BuildConfigXmlUrl(cdnRoot);
            try
            {
                string xml = await ApiResponseHttpClient.GetStringAsync(url, token).ConfigureAwait(false);
                _remoteConfig = WanmeiRemoteConfig.Parse(xml);
                SharedStatic.InstanceLogger.LogInformation(
                    "[WanmeiManager] Remote config: ResVersion={Version}, ResSize={Size}",
                    _remoteConfig.ResVersion, _remoteConfig.ResSize);
                return _remoteConfig;
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning("[WanmeiManager] Failed to fetch config.xml from {Url}: {Msg}",
                    url, ex.Message);
            }
        }

        return _remoteConfig;
    }

    internal async Task<int> InitAsyncInner(bool forceInit, CancellationToken token)
    {
        if (!forceInit && _isInitialized) return 0;

        // Local installed version.
        string? localVersion = ReadInstalledResVersion();
        CurrentGameVersion = string.IsNullOrEmpty(localVersion) ? GameVersion.Empty : new GameVersion(localVersion);

        // Remote available version.
        WanmeiRemoteConfig? remote = await GetRemoteConfigAsync(true, token).ConfigureAwait(false);
        if (remote != null && !string.IsNullOrEmpty(remote.ResVersion))
            ApiGameVersion = new GameVersion(remote.ResVersion);

        _isInitialized = true;
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

        string stateFile = Path.Combine(CurrentGameInstallPath, StateFileName);
        if (File.Exists(stateFile))
        {
            foreach (string line in File.ReadAllLines(stateFile))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("ResVersion=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = trimmed["ResVersion=".Length..].Trim();
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
        }

        // Fallback: the official launcher stores a plaintext config.xml with <ResVersion>.
        string officialConfig = Path.Combine(CurrentGameInstallPath,
            "NTELauncher", "UserData", "Patcher", "PatcherSDK", "config.xml");
        if (File.Exists(officialConfig))
        {
            try
            {
                var parsed = WanmeiRemoteConfig.Parse(File.ReadAllText(officialConfig));
                if (!string.IsNullOrEmpty(parsed.ResVersion)) return parsed.ResVersion;
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning("[WanmeiManager] Could not read official config.xml: {Msg}",
                    ex.Message);
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
            CurrentGameVersion = new GameVersion(resVersion);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning("[WanmeiManager] Failed to write state file: {Msg}", ex.Message);
        }
    }

    protected override void SetGamePathInner(string gamePath)
    {
        CurrentGameInstallPath = gamePath;
        _isInitialized = false;

        if (string.IsNullOrEmpty(gamePath)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await InitAsyncInner(true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogError("[WanmeiManager] Re-initialization failed: {Msg}", ex.Message);
            }
        });
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
        base.Dispose();
        ApiResponseHttpClient?.Dispose();
    }
}
