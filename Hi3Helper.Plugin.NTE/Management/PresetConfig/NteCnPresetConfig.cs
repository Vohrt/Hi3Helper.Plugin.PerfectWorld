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

    // The vendor launcher application (Qt5 + CEF): NTEGame.exe hosts the Perfect World account-login UI and drives
    // the game process (anti-cheat init + inherited-env/named-pipe token hand-off), which a bare HTGame.exe launch
    // cannot do. The plugin launches THIS directly rather than the thin NTELauncher\NTELauncher.exe "WmglLauncher"
    // shim, mirroring the P5X plugin (which launches P5XGame.exe directly):
    //   1. The shim only locates the patcher then spawns "NTEGame.exe /launcher /directly" + any forwarded args
    //      (verified in its binary: it hard-codes "/launcher /directly" and appends its own command-line tail; there
    //      is no "/autoplay" string inside it). The plugin already manages game/launcher resources, so the shim's
    //      patcher step is redundant, and NTEGame.exe (the piece that logs in / drives the game) is still used, so
    //      nothing vendor-critical is skipped.
    //   2. Launching NTEGame.exe directly with "/launcher /directly /autoplay" reproduces BYTE-FOR-BYTE the command
    //      line the shim currently produces for a plugin launch — confirmed from NTEGame.log "ARGS :" lines: a plugin
    //      launch (NTELauncher.exe /autoplay) is logged as "NTEGame.exe /launcher /directly /autoplay". This removes
    //      one intermediary process and lets the plugin control the flags directly. Working dir = install root.
    private const string LauncherAppName = @"NTELauncher\NTEGame.exe";

    private static readonly PerfectWorldGameConfig NteGameConfig = new()
    {
        AppId                            = "1289",
        GameResBranch                    = "publish_PC",
        Platform                         = "Windows",
        GameResCdnUrls                   = ["https://yhcdn1.wmupd.com/clientRes", "https://yhcdn2.wmupd.com/clientRes"],
        LauncherBranch                   = "publish_ob",
        LauncherCdnUrls                  = ["https://yhcdn1.wmupd.com/hd", "https://yhcdn2.wmupd.com/hd"],
        GameExecutableRelativePath       = ExEcutableName,
        // A finished install requires BOTH the packed game runtime (HTGameBase.dll) and the vendor launcher app
        // (NTEGame.exe); the latter guarantees the account-login path is available before showing "Launch".
        InstallMarkerRelativePaths       = [InstallMarkerName, LauncherAppName],
        LauncherBootstrapperRelativePath = LauncherAppName,
        // "/autoplay" makes NTEGame.exe SKIP its in-process resource updater (GameResUpdaterAgent) and launch the game
        // immediately — this is what gives the plugin a hands-off auto-start (without it the launcher runs the updater
        // and then just waits for a manual "开始游戏" click). "/launcher /directly" reproduces the shim's normal
        // invocation so NTEGame.exe does not self-relaunch through the shim (which would strip the flag); "/autoplay"
        // is the auto-start override. This trio is exactly what the shim produces today (see LauncherAppName note).
        // Skipping the updater has a trade-off: the vendor/in-game updater is also what would reconcile and fetch the
        // per-language voice packs on demand, so with /autoplay the plugin must instead ship EVERY voice language
        // itself at install time (see below — no deferral, no forged patcher state). An earlier attempt to defer the
        // non-default voices and drop /autoplay so the vendor updater could fetch them on demand was ABANDONED: it
        // left the game looping on "更新失败" after returning to login and showing every voice language without a size
        // (see README "Known issues"). (P5X likewise REQUIRES /autoplay; see P5xCnPresetConfig.)
        LaunchArguments                  = "/launcher /directly /autoplay",
        // Silent-launch: patch the launcher's own settings so it auto-logs-in, auto-starts the game (no "Start"
        // click) and quits together with the game (no reappear afterwards). Window hiding during start-up is added
        // automatically when Collapse itself runs as administrator (NTEGame.exe is force-elevated).
        SilentLaunch                     = true,
        LauncherSettingsIniRelativePath  = @"NTELauncher\UserData\Config\Config.ini",
        LauncherProcessBaseNames         = ["NTEGame", "NTELauncher", "NTEUpdate", "NTEBrowser", "NTEWebBooster", "NTEErrRep"],
        LauncherStartupRevealTimeoutSeconds = 120,
        // ---- DLL-injection auto-click launch path (ON by default; see README "异环 auto-click") ----
        // When enabled AND Collapse runs elevated, the plugin injects PwAutoClick.dll into NTEGame.exe and presses the
        // real "开始游戏" button via Qt meta-object invocation once NTEGame logs it is ready — instead of "/autoplay".
        // This makes NTEGame run its normal in-process resource check first (the step "/autoplay" skips). Set to false
        // to fall back to the older "/autoplay + all voices bundled" behaviour. Also auto-falls-back to "/autoplay" when
        // it cannot activate (not elevated / DLL missing), or to a visible launcher for a manual click if only the
        // injection failed. The context object ("BackgroudStageScheduler"), method ("gameActionBtnClicked") and ready
        // marker ("all ready, wait for start game") use the shared pw_sdk defaults in PerfectWorldGameConfig.
        LauncherAutoClickEnabled            = true,
        LaunchArgumentsAutoClick            = "/launcher /directly",
        LauncherAutoClickSilentSettings     = [("autoLogin", "1"), ("autoRun", "0"), ("quitWithGame", "1"), ("showAfterGameQuit", "0")],
        // NOTE: 异环 ships all voice languages under Content/TagPatchPaks/ (pakchunk101 = Chinese, 102/103/104 =
        // JP/EN/KR). The plugin downloads them ALL at install/update time — there is deliberately no DeferredContent*
        // filtering and no WritePatcherState here. Because /autoplay skips the in-process updater that would fetch a
        // language on demand, bundling every voice up front is what keeps the game from looping on "更新失败"; it costs
        // ~5 GB more than the official one-voice install (trade-off documented in the README "Known issues").
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
