using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.PerfectWorld.Core;
using Hi3Helper.PerfectWorld.Core.Management;
using Hi3Helper.PerfectWorld.Core.Management.Api;

namespace Hi3Helper.Plugin.P5X.Management.PresetConfig;

[GeneratedComClass]
public partial class P5xCnPresetConfig : PluginPresetConfigBase
{
    // The real game binary (Unity IL2CPP client) that the plugin actually installs and launches. The vendor's
    // P5XLaunch bootstrapper is NOT part of the game resources, so it must never be used for install-detection.
    private const string ExEcutableName = @"client\pc\P5X.exe";

    // A core Unity/IL2CPP runtime module: present only after a complete install of the client tree, so it
    // distinguishes a finished install from a partial one (it sits next to the real game executable).
    private const string InstallMarkerName = @"client\pc\GameAssembly.dll";

    // The vendor launcher application (Qt5 + CEF): it hosts the Perfect World account-login UI and drives the game
    // process (anti-cheat init + named-pipe token hand-off), which a bare P5X.exe launch cannot do. The plugin
    // launches THIS directly rather than the thin P5XLaunch\P5XLauncher.exe "WmglLauncher" shim, for two reasons:
    //   1. The shim only locates the patcher then ShellExecutes "P5XGame.exe /launcher /directly" with a HARD-CODED
    //      argument list (verified in its binary) — it silently drops anything extra, so it can never carry the
    //      /autoplay flag P5X needs (see LaunchArguments). The plugin already manages game/launcher resources, so the
    //      shim's patcher step is redundant, and P5XGame.exe (the piece that actually logs in / drives the game) is
    //      still used, so nothing vendor-critical is skipped.
    //   2. Launching P5XGame.exe directly (with the same /launcher /directly the shim would pass) lets us append
    //      /autoplay, which is what makes P5X start silently at all.
    // Working dir = install root (matches the official shortcut). The top-level C:\...\P5X\P5X.exe is only a copy of
    // the shim whose process name ("P5X") collides with the real game's, so it is never used here.
    private const string LauncherAppName = @"P5XLaunch\P5XGame.exe";

    private static readonly PerfectWorldGameConfig P5xGameConfig = new()
    {
        AppId                            = "1264",
        GameResBranch                    = "CN_OB_OFFICIAL",
        Platform                         = "Windows",
        GameResCdnUrls                   = ["https://nsywl-client-dev1.wmupd.com/clientRes", "https://nsywl-client-dev2.wmupd.com/clientRes"],
        LauncherBranch                   = "P5XOB",
        LauncherCdnUrls                  = ["https://nsywl-client-dev1.wmupd.com/hd", "https://nsywl-client-dev2.wmupd.com/hd"],
        GameExecutableRelativePath       = ExEcutableName,
        // A finished install requires BOTH the packed Unity runtime (GameAssembly.dll) and the vendor launcher app
        // (P5XGame.exe); the latter guarantees the account-login + auto-play path is available before showing "Launch".
        InstallMarkerRelativePaths       = [InstallMarkerName, LauncherAppName],
        LauncherBootstrapperRelativePath = LauncherAppName,
        // Unlike 异环 (whose launcher auto-starts the game on its own), P5X's launcher gates auto-play OFF server-side
        // (appconfig.canAutoPlay=false): patching the INI autoRun=1 is NOT enough and it stops at the "开始游戏" button.
        // Launching P5XGame.exe directly with "/launcher /directly /autoplay" FORCES the silent auto-play flow
        // (onLoginSuccess -> GameClientAgent::launchGame -> GameLifecycleMgr::startGame -> client\pc\p5x.exe), which
        // overrides canAutoPlay. The "/launcher /directly" pair reproduces the shim's normal invocation (so P5XGame.exe
        // does not self-relaunch and strip the flag); "/autoplay" is the override. Verified end-to-end on the live install.
        LaunchArguments                  = "/launcher /directly /autoplay",
        // Silent-launch ENABLED. The core patches the launcher's own settings (autoLogin=1 so a cached session logs in
        // automatically; quitWithGame=1 / showAfterGameQuit=0 so the launcher exits with the game and never reappears)
        // and, when Collapse runs elevated, hides the launcher window until the game appears. Auto-START comes from the
        // /autoplay flag above (P5X ignores the INI autoRun). The login-needed markers (core defaults) were confirmed
        // present verbatim in P5XGame.exe, so the graceful reveal-on-interactive-login path still works.
        SilentLaunch                     = true,
        LauncherSettingsIniRelativePath  = @"P5XLaunch\UserData\Config\Config.ini",
        LauncherProcessBaseNames         = ["P5XGame", "P5XLauncher", "P5XUpdate", "P5XBrowser", "P5XWebBooster", "P5XErrRep"],
        LauncherStartupRevealTimeoutSeconds = 120,
        // P5X-specific install layout (differs from 异环's NTELauncher/Client):
        LauncherRootDirName              = "P5XLaunch",
        ContentRootDirName               = "client",
        LauncherLogRelativePath          = @"UserData\Log\P5XGame.log"
    };

    private static readonly PerfectWorldNewsConfig P5xNewsConfig = new()
    {
        NewsPageUrl         = "https://p5x.wanmei.com/launcher/launcher_platform.html",
        NewsLinkBaseUrl     = "https://p5x.wanmei.com",
        BannerJsUrl         = "https://static.games.wanmei.com/public/commonData/gamesData/gameSwiper/p5x-gameSwiper.js",
        BannerJsCarouselKey = "PC_Launcher",
        Referer             = "https://p5x.wanmei.com/",
        Layout              = PerfectWorldNewsLayout.BdNewsShareBox
    };

    [field: AllowNull] [field: MaybeNull] public override string GameName => field ??= "Persona 5: The Phantom X";
    [field: AllowNull] [field: MaybeNull] public override string GameExecutableName => field ??= ExEcutableName;

    public override string GameAppDataPath
    {
        get
        {
            // Unity persistent data / logs live under %USERPROFILE%\AppData\LocalLow\<company>\<product>.
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(userProfile))
                return string.Empty;
            return Path.Combine(userProfile, "AppData", "LocalLow", "perfect", "p5x");
        }
    }

    [field: AllowNull] [field: MaybeNull] public override string GameLogFileName => field ??= null!;

    [field: AllowNull] [field: MaybeNull] public override string GameVendorName => field ??= "Black Wings Game Studio";
    [field: AllowNull] [field: MaybeNull] public override string GameRegistryKeyName => field ??= "P5X";
    [field: AllowNull] [field: MaybeNull] public override string ProfileName => field ??= "PersonaFiveThePhantomX";

    [field: AllowNull]
    [field: MaybeNull]
    public override string ZoneDescription =>
        field ??= "《Persona 5: The Phantom X》(P5X) 是一款由 ATLUS 授权、完美世界发行的都市奇幻角色扮演游戏。";

    [field: AllowNull] [field: MaybeNull] public override string ZoneName => field ??= "Mainland China";
    [field: AllowNull] [field: MaybeNull] public override string ZoneFullName => field ??= "Persona 5: The Phantom X (中国大陆)";
    [field: AllowNull] [field: MaybeNull] public override string ZoneLogoUrl => field ??= "";
    [field: AllowNull] [field: MaybeNull] public override string ZonePosterUrl => field ??= "";

    [field: AllowNull]
    [field: MaybeNull]
    public override string ZoneHomePageUrl => field ??= "https://p5x.wanmei.com/";

    public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.Public;

    [field: AllowNull] [field: MaybeNull] public override string GameMainLanguage => field ??= "zh-CN";

    [field: AllowNull]
    [field: MaybeNull]
    public override string LauncherGameDirectoryName => field ??= "Persona 5 The Phantom X Game";

    [field: AllowNull] [field: MaybeNull] public override List<string> SupportedLanguages => field ??= ["Chinese"];

    public override ILauncherApiMedia? LauncherApiMedia
    {
        get => field ??= new PerfectWorldLauncherApiMedia(P5xGameConfig);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= new PerfectWorldLauncherApiNews(P5xNewsConfig);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= new PerfectWorldGameManager(P5xGameConfig);
        set;
    }

    public override IGameInstaller? GameInstaller
    {
        get => field ??= new PerfectWorldGameInstaller(GameManager!);
        set;
    }

    protected override Task<int> InitAsync(CancellationToken token)
    {
        return Task.FromResult(0);
    }
}
