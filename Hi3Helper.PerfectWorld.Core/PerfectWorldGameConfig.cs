using System;
using System.Globalization;

namespace Hi3Helper.PerfectWorld.Core;

/// <summary>
///     Immutable per-game configuration for a Perfect World <c>pw_sdk</c> title. A thin plugin
///     supplies one of these to describe its game; everything CDN/URL related is derived from it so the
///     core stays game-agnostic and reusable across pw_sdk titles.
/// </summary>
public sealed class PerfectWorldGameConfig
{
    /// <summary>Numeric application id used to derive the manifest AES key (NTE / 异环 = <c>1289</c>).</summary>
    public required string AppId { get; init; }

    /// <summary>Game-resource branch, e.g. <c>publish_PC</c>.</summary>
    public required string GameResBranch { get; init; }

    /// <summary>Platform path segment used in the resource URLs, e.g. <c>Windows</c>.</summary>
    public string Platform { get; init; } = "Windows";

    /// <summary>
    ///     Ordered list of game-resource CDN roots (primary first, backups after), each ending at
    ///     <c>/clientRes</c>, e.g. <c>https://yhcdn1.wmupd.com/clientRes</c>.
    /// </summary>
    public required string[] GameResCdnUrls { get; init; }

    /// <summary>Launcher self-update branch, e.g. <c>publish_ob</c>.</summary>
    public required string LauncherBranch { get; init; }

    /// <summary>
    ///     Ordered list of launcher-distribution CDN roots, each ending at <c>/hd</c>,
    ///     e.g. <c>https://yhcdn1.wmupd.com/hd</c>.
    /// </summary>
    public required string[] LauncherCdnUrls { get; init; }

    /// <summary>
    ///     Path (relative to the install root) of the executable Collapse should launch and use to detect an
    ///     installation, e.g. <c>Client\WindowsNoEditor\HT\Binaries\Win64\HTGame.exe</c>.
    /// </summary>
    public required string GameExecutableRelativePath { get; init; }

    /// <summary>
    ///     Additional files (relative to the install root) that must also exist for the install to be considered
    ///     complete. This guards against a partially-downloaded install (e.g. the main executable present but the
    ///     packed runtime files missing) being mistaken for a finished one. Optional; empty by default.
    /// </summary>
    public string[] InstallMarkerRelativePaths { get; init; } = [];

    /// <summary>
    ///     Optional path (relative to the install root) of the vendor bootstrapper that wraps the real game
    ///     executable, e.g. <c>NTELauncher\NTEGame.exe</c>. When present on disk it is preferred for launching
    ///     (with <see cref="LaunchArguments"/>) so vendor start-up steps such as anti-cheat set-up still run;
    ///     otherwise <see cref="GameExecutableRelativePath"/> is launched directly.
    /// </summary>
    public string LauncherBootstrapperRelativePath { get; init; } = string.Empty;

    /// <summary>Command-line arguments passed to the bootstrapper when it is used, e.g. <c>/launcher</c>.</summary>
    public string LaunchArguments { get; init; } = string.Empty;

    /// <summary>
    ///     When <see langword="true"/>, the plugin drives the vendor launcher "silently": it patches the launcher's
    ///     own settings (auto-login / auto-start game / quit-with-game) before launch and tracks the real game
    ///     process instead of the launcher, so the user does not have to click "Start" and the launcher does not
    ///     reappear after the game exits. Additional window hiding during start-up is applied only when the host
    ///     process is elevated (the vendor launcher requires administrator, so a non-elevated host cannot touch its
    ///     window). Requires <see cref="LauncherBootstrapperRelativePath"/> to be set.
    /// </summary>
    public bool SilentLaunch { get; init; }

    /// <summary>
    ///     Path (relative to the install root) of the vendor launcher's mutable, user-writable settings INI that
    ///     the plugin patches for silent launch, e.g. <c>NTELauncher\UserData\Config\Config.ini</c>. This file is
    ///     NOT integrity-checked by the launcher self-update, so patching it is safe. Empty disables patching.
    /// </summary>
    public string LauncherSettingsIniRelativePath { get; init; } = string.Empty;

    /// <summary>
    ///     Base names (without extension) of the vendor launcher's process tree, used to hide their windows during
    ///     start-up and to clean them up after the game exits, e.g. <c>["NTEGame", "NTELauncher", ...]</c>.
    /// </summary>
    public string[] LauncherProcessBaseNames { get; init; } = [];

    /// <summary>
    ///     While hiding the launcher window during a silent start-up, reveal it after this many seconds if the game
    ///     has still not started (a fallback so first-time/expired-token logins, which need the visible login UI,
    ///     are not left hidden). Only relevant when the host is elevated.
    /// </summary>
    public int LauncherStartupRevealTimeoutSeconds { get; init; } = 120;

    private string PrimaryGameResCdn =>
        GameResCdnUrls is { Length: > 0 } ? GameResCdnUrls[0].TrimEnd('/') : string.Empty;

    /// <summary>Plaintext resource entry point: <c>.../{branch}/Version/{platform}/config.xml?tValue={ms}</c>.</summary>
    public string BuildConfigXmlUrl(string cdnRoot)
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{cdnRoot.TrimEnd('/')}/{GameResBranch}/Version/{Platform}/config.xml?tValue={ms.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Encrypted manifest bundle: <c>.../{branch}/Version/{platform}/version/{resVersion}/ResList.bin.zip</c>.</summary>
    public string BuildResListZipUrl(string cdnRoot, string resVersion)
    {
        return $"{cdnRoot.TrimEnd('/')}/{GameResBranch}/Version/{Platform}/version/{resVersion}/ResList.bin.zip";
    }

    /// <summary>
    ///     Encrypted incremental (delta) manifest: <c>.../{branch}/Version/{platform}/version/{resVersion}/lastdiff.bin</c>.
    ///     Unlike <c>ResList</c> this blob is served raw (not zip-wrapped) but shares the same PatcherXML0 envelope.
    /// </summary>
    public string BuildLastDiffUrl(string cdnRoot, string resVersion)
    {
        return $"{cdnRoot.TrimEnd('/')}/{GameResBranch}/Version/{Platform}/version/{resVersion}/lastdiff.bin";
    }

    /// <summary>
    ///     Content-addressed file URL: <c>.../{branch}/Res/{md5[0]}/{md5}.{size}</c>.
    /// </summary>
    public string BuildContentUrl(string cdnRoot, string md5, long size)
    {
        return $"{cdnRoot.TrimEnd('/')}/{GameResBranch}/Res/{md5[..1]}/{md5}.{size.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Content-addressed file URL on the primary CDN.</summary>
    public string BuildContentUrl(string md5, long size) => BuildContentUrl(PrimaryGameResCdn, md5, size);

    /// <summary>Launcher self-update version descriptor: <c>.../{branch}/launcher/Version.ini</c>.</summary>
    public string BuildLauncherVersionIniUrl(string cdnRoot)
    {
        return $"{cdnRoot.TrimEnd('/')}/{LauncherBranch}/launcher/Version.ini";
    }

    /// <summary>
    ///     Launcher file manifest for a resolved versioned directory:
    ///     <c>.../{branch}/launcher/{versionDir}/AllFiles.xml</c> (e.g. <c>versionDir = 1.0.6.0718_2</c>).
    /// </summary>
    public string BuildLauncherAllFilesUrl(string cdnRoot, string versionDir)
    {
        return $"{cdnRoot.TrimEnd('/')}/{LauncherBranch}/launcher/{versionDir}/AllFiles.xml";
    }

    /// <summary>
    ///     Per-file (zip-wrapped) launcher content URL:
    ///     <c>.../{branch}/launcher/{versionDir}/{relativePath}.zip</c>. Each launcher file is stored individually
    ///     zip-compressed on the CDN, so the download is the <c>.zip</c> and must be inflated locally.
    /// </summary>
    public string BuildLauncherFileZipUrl(string cdnRoot, string versionDir, string relativePath)
    {
        string p = relativePath.Replace('\\', '/').TrimStart('/');
        return $"{cdnRoot.TrimEnd('/')}/{LauncherBranch}/launcher/{versionDir}/{p}.zip";
    }
}
