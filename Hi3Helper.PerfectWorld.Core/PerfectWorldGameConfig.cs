using System;
using System.Globalization;
using System.IO;

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

    /// <summary>
    ///     Install-relative directory that holds the vendor launcher (the self-update target of the launcher
    ///     <c>AllFiles.xml</c> manifest and the location of the official <c>PatcherSDK\config.xml</c>), e.g.
    ///     <c>NTELauncher</c> for 异环 or <c>P5XLaunch</c> for P5X.
    /// </summary>
    public string LauncherRootDirName { get; init; } = "NTELauncher";

    /// <summary>
    ///     Install-relative directory that holds the extracted game content, removed on uninstall, e.g.
    ///     <c>Client</c> for 异环 or <c>client</c> for P5X.
    /// </summary>
    public string ContentRootDirName { get; init; } = "Client";

    /// <summary>
    ///     Path (relative to the vendor launcher directory, i.e. <see cref="LauncherRootDirName"/>) of the launcher
    ///     log that is tailed during a silent launch to detect an interactive-login requirement, e.g.
    ///     <c>UserData\Log\NTEGame.log</c>.
    /// </summary>
    public string LauncherLogRelativePath { get; init; } = Path.Combine("UserData", "Log", "NTEGame.log");

    /// <summary>
    ///     Optional install-relative path to the vendor's proprietary <c>qres</c> resource pack from which the
    ///     home-screen background image is extracted when the launcher CDN publishes no dynamic <c>bgimgs/</c>
    ///     folder, e.g. <c>P5XLaunch\ResData\1264.dat</c> for P5X. Left <see langword="null"/> for games (such as
    ///     异环/NTE) that expose a dynamic background via the launcher self-update tree, so those are unaffected.
    /// </summary>
    public string? LocalBackgroundResPackRelativePath { get; init; }

    /// <summary>
    ///     Substrings that, when found in the launcher log, indicate that the cached-token auto-login failed and an
    ///     interactive login UI must be revealed to the user. Used only during a silent launch.
    /// </summary>
    public string[] LoginNeededLogMarkers { get; init; } =
        ["onAutoLoginFailed", "onAutoLoginTimeOut", "autoLoginTokenError", "needLoginFirst"];

    /// <summary>
    ///     Substrings that, when found in the launcher log during a silent launch, indicate the launcher raised a
    ///     modal dialog that blocks the launch and needs the user (e.g. the pw_sdk hardware/software-conflict warning
    ///     such as the MSI Afterburner / RTSS prompt, logged as <c>GameCustomMessageBoxController::showMessageBox</c>
    ///     followed by <c>GameClientAgent::onLaunchGame ... has conflict, skip</c>). Detecting one reveals the hidden
    ///     launcher window immediately so the user can dismiss the dialog, instead of waiting the full
    ///     <see cref="LauncherStartupRevealTimeoutSeconds"/>. The defaults are shared pw_sdk log strings; a game may
    ///     override them. Used only during a silent launch.
    /// </summary>
    public string[] LauncherAttentionLogMarkers { get; init; } =
        ["has conflict", "showMessageBox"];

    /// <summary>
    ///     INI section header (including brackets) inside <see cref="LauncherSettingsIniRelativePath"/> under which
    ///     the silent-launch keys live, e.g. <c>[Setting]</c>.
    /// </summary>
    public string LauncherSettingsSectionName { get; init; } = "[Setting]";

    /// <summary>
    ///     Key/value pairs written into the launcher's <see cref="LauncherSettingsSectionName"/> section to make it
    ///     silence itself (auto-login, auto-start the game, quit together with the game and not reappear afterwards).
    ///     Keys the vendor launcher does not recognise are harmlessly ignored.
    /// </summary>
    public (string Key, string Value)[] LauncherSilentSettings { get; init; } =
        [("autoLogin", "1"), ("autoRun", "1"), ("quitWithGame", "1"), ("showAfterGameQuit", "0")];

    // ---------------------------------------------------------------------------------------------------------------
    // Auto-click (DLL-injection) launch path — an alternative to the vendor "/autoplay" flag.
    //
    //   Background: passing "/autoplay" on the launcher command line makes it SKIP its in-process resource check
    //   (GameClientAgent::beginCheckGameResVersion). For 异环/NTE that skip corrupts the per-language voice state, so
    //   with "/autoplay" the plugin has to bundle every voice language up front (~5 GB extra). Pressing the launcher's
    //   own "开始游戏" button instead runs the normal ready-check first (exactly like a human click), so on-demand voice
    //   works and only the default language need be shipped.
    //
    //   When enabled AND the host is elevated, the plugin injects a tiny helper DLL (PwAutoClick.dll) into the Qt
    //   launcher, waits for the launcher to reach its "ready to start" log marker, then invokes the button's slot via
    //   Qt meta-object reflection. If injection cannot activate (not elevated, DLL missing) the plugin falls back to
    //   the "/autoplay" path below, preserving today's behaviour.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     When <see langword="true"/> and the host runs elevated, the plugin drives the launcher's "开始游戏" button
    ///     programmatically (via injected <c>PwAutoClick.dll</c> + Qt meta-object invocation) instead of relying on the
    ///     vendor "/autoplay" flag. This runs the launcher's normal resource/voice reconciliation before starting the
    ///     game. Requires <see cref="SilentLaunch"/> and <see cref="LauncherBootstrapperRelativePath"/> to point at the
    ///     Qt launcher executable. Falls back to <see cref="LaunchArguments"/> when it cannot activate.
    /// </summary>
    public bool LauncherAutoClickEnabled { get; init; }

    /// <summary>
    ///     Command-line arguments used instead of <see cref="LaunchArguments"/> when the auto-click path is active,
    ///     e.g. <c>/launcher /directly</c> (deliberately WITHOUT <c>/autoplay</c> so the launcher runs its resource
    ///     check and then waits at the "ready to start" state for our injected click).
    /// </summary>
    public string LaunchArgumentsAutoClick { get; init; } = string.Empty;

    /// <summary>
    ///     Substring that, when found in the launcher log during an auto-click launch, means the launcher has finished
    ///     its resource check/login and is parked waiting for the "开始游戏" button — the moment it is safe to fire the
    ///     injected click. Shared pw_sdk marker (<c>GameClientAgent::onGameElementUpdateFinished</c> logs it).
    /// </summary>
    public string LauncherAutoClickReadyLogMarker { get; init; } = "all ready, wait for start game";

    /// <summary>
    ///     Name of the QObject the launcher exposes to its QML UI via <c>QQmlContext::setContextProperty</c> and on
    ///     which the play button's slot lives. The play button QML is <c>onClicked: {obj}.gameActionBtnClicked()</c>.
    ///     Note the vendor's spelling (missing an 'n'): <c>BackgroudStageScheduler</c>.
    /// </summary>
    public string LauncherAutoClickContextObjectName { get; init; } = "BackgroudStageScheduler";

    /// <summary>
    ///     Q_INVOKABLE/slot name on <see cref="LauncherAutoClickContextObjectName"/> that the injected DLL calls to
    ///     press the play button, e.g. <c>gameActionBtnClicked</c>.
    /// </summary>
    public string LauncherAutoClickMethodName { get; init; } = "gameActionBtnClicked";

    /// <summary>
    ///     Launcher settings written for the auto-click path (used instead of <see cref="LauncherSilentSettings"/> when
    ///     auto-click is active). Typically identical except <c>autoRun=0</c>, so ONLY the injected click starts the
    ///     game — deterministic single start, no race with the launcher auto-starting itself. Empty falls back to
    ///     <see cref="LauncherSilentSettings"/>.
    /// </summary>
    public (string Key, string Value)[] LauncherAutoClickSilentSettings { get; init; } = [];

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
