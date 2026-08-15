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

namespace Hi3Helper.Plugin.NTE.Management.PresetConfig;

[GeneratedComClass]
public partial class NteCnPresetConfig : PluginPresetConfigBase
{
    // The real game binary (UE5 client) that the plugin actually installs and launches. The vendor's
    // NTELauncher\NTEGame.exe bootstrapper is NOT part of the game resources, so it must never be used for
    // install-detection.
    private const string ExEcutableName = @"Client\WindowsNoEditor\HT\Binaries\Win64\HTGame.exe";

    // A core packed runtime module: present only after a complete install (it lives inside a manifest Pak, not
    // among the directly-addressed big files), so it distinguishes a finished install from a partial one.
    private const string InstallMarkerName = @"Client\WindowsNoEditor\HT\Binaries\Win64\HTGameBase.dll";

    // The vendor launcher/updater. The plugin installs it (from the launcher self-update manifest) alongside the
    // game, and launch goes THROUGH it: it hosts the Perfect World account-login UI and drives the game process
    // (anti-cheat init + named-pipe token hand-off), which a bare HTGame.exe launch cannot do. This matches the
    // official "异环" shortcut (NTELauncher\NTELauncher.exe, working dir = install root).
    private const string LauncherBootstrapperName = @"NTELauncher\NTELauncher.exe";

    private static readonly PerfectWorldGameConfig NteGameConfig = new()
    {
        AppId                            = "1289",
        GameResBranch                    = "publish_PC",
        Platform                         = "Windows",
        GameResCdnUrls                   = ["https://yhcdn1.wmupd.com/clientRes", "https://yhcdn2.wmupd.com/clientRes"],
        LauncherBranch                   = "publish_ob",
        LauncherCdnUrls                  = ["https://yhcdn1.wmupd.com/hd", "https://yhcdn2.wmupd.com/hd"],
        GameExecutableRelativePath       = ExEcutableName,
        // A finished install requires BOTH the packed game runtime (HTGameBase.dll) and the vendor launcher entry
        // point (NTELauncher.exe); the latter guarantees the account-login path is available before showing "Launch".
        InstallMarkerRelativePaths       = [InstallMarkerName, LauncherBootstrapperName],
        LauncherBootstrapperRelativePath = LauncherBootstrapperName,
        // CRITICAL: do NOT pass "/autoplay" here. NTELauncher.exe forwards its extra command-line args verbatim to
        // NTEGame.exe (the arg string "autoplay" exists only in NTEGame.exe, not in NTELauncher.exe), and NTEGame.exe
        // treats "/autoplay" as "skip the resource updater and launch the game NOW": with it, the log goes straight
        // to `status 0 --> 7` then `GameClientAgent::launchGame` with the `GameResUpdaterAgent` check never running;
        // without it (official flow) NTEGame runs `_initGameResUpdater -> onBeginCheckGameResVersion ->
        // onBaseResCheckFinished -> (download) -> patcherResUpdateFinsh` before launching. That vendor updater is what
        // reconciles the voice/tag resources and hands the in-game updater a valid state — skipping it left every
        // voice language shown WITHOUT a size and made the in-game updater loop on "更新失败" after returning to the
        // login screen, no matter what the on-disk PatcherSDK state said (reproduced on a pristine official install).
        // 异环's launcher already auto-starts the game from the patched INI (autoRun=1), so NO launch arg is needed.
        // (P5X is the opposite — its launcher gates auto-play server-side and REQUIRES /autoplay; see P5xCnPresetConfig.)
        LaunchArguments                  = "",
        // Silent-launch: patch the launcher's own settings so it auto-logs-in, auto-starts the game (no "Start"
        // click) and quits together with the game (no reappear afterwards). Window hiding during start-up is added
        // automatically when Collapse itself runs as administrator (NTEGame.exe is force-elevated).
        SilentLaunch                     = true,
        LauncherSettingsIniRelativePath  = @"NTELauncher\UserData\Config\Config.ini",
        LauncherProcessBaseNames         = ["NTEGame", "NTELauncher", "NTEUpdate", "NTEBrowser", "NTEWebBooster", "NTEErrRep"],
        LauncherStartupRevealTimeoutSeconds = 120,
        // 异环 requires the default (Chinese) voice, pakchunk101, to be present on disk to launch — exactly like the
        // official install, which ships one voice language and offers the rest for on-demand download. Defer the whole
        // TagPatchPaks directory so the plugin doesn't pull all ~5 GB of voice, but KEEP pakchunk101 so the game has a
        // working default voice; the other languages (102/103/104 = JP/EN/KR) stay deferred for in-game download.
        DeferredContentPathMarkers       = ["/TagPatchPaks/"],
        DeferredContentKeepMarkers       = ["/TagPatchPaks/pakchunk101"],
        // Because the plugin downloads the game directly it never runs the vendor pw_sdk patcher that authors the
        // native PatcherSDK\config.xml + ResList.xml. 异环 hands control to that patcher on launch, so without those
        // files it sees local version 0.0, thinks the whole build is missing and loops on "更新失败". Author a
        // finalized state after install/update — base build + the kept default voice recorded as installed, the
        // deferred languages recorded as available — to fix the loop and let the game play/download voice correctly.
        WritePatcherState                = true
    };

    private static readonly PerfectWorldNewsConfig NteNewsConfig = new()
    {
        NewsPageUrl     = "https://yh.wanmei.com/launcher/launcher_ob.html?expand=1",
        NewsLinkBaseUrl = "https://yh.wanmei.com",
        BannerJsUrl     = "https://static.games.wanmei.com/public/commonData/gamesData/gameSwiper/yh-gameSwiper.js",
        Referer         = "https://yh.wanmei.com/"
    };

    [field: AllowNull] [field: MaybeNull] public override string GameName => field ??= "Neverness To Everness";
    [field: AllowNull] [field: MaybeNull] public override string GameExecutableName => field ??= ExEcutableName;

    public override string GameAppDataPath
    {
        get
        {
            string? gamePath = null;
            GameManager?.GetGamePath(out gamePath);
            if (!string.IsNullOrEmpty(gamePath))
                return Path.Combine(gamePath, "Client", "WindowsNoEditor", "HT", "Saved", "Logs");
            return string.Empty;
        }
    }

    [field: AllowNull] [field: MaybeNull] public override string GameLogFileName => field ??= null!;

    [field: AllowNull] [field: MaybeNull] public override string GameVendorName => field ??= "Hotta Studio";
    [field: AllowNull] [field: MaybeNull] public override string GameRegistryKeyName => field ??= "NevernessToEverness";
    [field: AllowNull] [field: MaybeNull] public override string ProfileName => field ??= "NevernessToEverness";

    [field: AllowNull]
    [field: MaybeNull]
    public override string ZoneDescription =>
        field ??= "《异环》(Neverness To Everness) 是一款由完美世界发行、Hotta Studio 开发的都市奇幻开放世界 RPG。";

    [field: AllowNull] [field: MaybeNull] public override string ZoneName => field ??= "Mainland China";
    [field: AllowNull] [field: MaybeNull] public override string ZoneFullName => field ??= "异环 (中国大陆)";
    [field: AllowNull] [field: MaybeNull] public override string ZoneLogoUrl => field ??= "";
    [field: AllowNull] [field: MaybeNull] public override string ZonePosterUrl => field ??= "";

    [field: AllowNull]
    [field: MaybeNull]
    public override string ZoneHomePageUrl => field ??= "https://yh.wanmei.com/";

    public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.Public;

    [field: AllowNull] [field: MaybeNull] public override string GameMainLanguage => field ??= "zh-CN";

    [field: AllowNull]
    [field: MaybeNull]
    public override string LauncherGameDirectoryName => field ??= "Neverness To Everness Game";

    [field: AllowNull] [field: MaybeNull] public override List<string> SupportedLanguages => field ??= ["Chinese"];

    public override ILauncherApiMedia? LauncherApiMedia
    {
        get => field ??= new PerfectWorldLauncherApiMedia(NteGameConfig);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= new PerfectWorldLauncherApiNews(NteNewsConfig);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= new PerfectWorldGameManager(NteGameConfig);
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
