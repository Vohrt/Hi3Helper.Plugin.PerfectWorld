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
        // "/autoplay" makes NTELauncher.exe forward the flag verbatim to NTEGame.exe, which then SKIPS its in-process
        // resource updater (GameResUpdaterAgent) and launches the game immediately — this is what gives the plugin a
        // hands-off auto-start (without it the launcher runs the updater and then just waits for a manual "开始游戏"
        // click). Skipping that updater has a trade-off: the vendor/in-game updater is also what would reconcile and
        // fetch the per-language voice packs on demand, so with /autoplay the plugin must instead ship EVERY voice
        // language itself at install time (see below — no deferral, no forged patcher state). An earlier attempt to
        // defer the non-default voices and drop /autoplay so the vendor updater could fetch them on demand was
        // ABANDONED: it left the game looping on "更新失败" after returning to login and showing every voice language
        // without a size (see README "Known issues"). (P5X likewise REQUIRES /autoplay; see P5xCnPresetConfig.)
        LaunchArguments                  = "/autoplay",
        // Silent-launch: patch the launcher's own settings so it auto-logs-in, auto-starts the game (no "Start"
        // click) and quits together with the game (no reappear afterwards). Window hiding during start-up is added
        // automatically when Collapse itself runs as administrator (NTEGame.exe is force-elevated).
        SilentLaunch                     = true,
        LauncherSettingsIniRelativePath  = @"NTELauncher\UserData\Config\Config.ini",
        LauncherProcessBaseNames         = ["NTEGame", "NTELauncher", "NTEUpdate", "NTEBrowser", "NTEWebBooster", "NTEErrRep"],
        LauncherStartupRevealTimeoutSeconds = 120
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
