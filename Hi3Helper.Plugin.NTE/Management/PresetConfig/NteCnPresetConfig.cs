using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Wanmei.Core;
using Hi3Helper.Wanmei.Core.Management;
using Hi3Helper.Wanmei.Core.Management.Api;

namespace Hi3Helper.Plugin.NTE.Management.PresetConfig;

[GeneratedComClass]
public partial class NteCnPresetConfig : PluginPresetConfigBase
{
    private const string ExEcutableName = @"NTELauncher\NTEGame.exe";

    private static readonly WanmeiGameConfig NteGameConfig = new()
    {
        AppId                      = "1289",
        GameResBranch              = "publish_PC",
        Platform                   = "Windows",
        GameResCdnUrls             = ["https://yhcdn1.wmupd.com/clientRes", "https://yhcdn2.wmupd.com/clientRes"],
        LauncherBranch             = "publish_ob",
        LauncherCdnUrls            = ["https://yhcdn1.wmupd.com/hd", "https://yhcdn2.wmupd.com/hd"],
        GameExecutableRelativePath = ExEcutableName,
        LaunchArguments            = "/launcher"
    };

    private static readonly WanmeiNewsConfig NteNewsConfig = new()
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
                return Path.Combine(gamePath, "NTELauncher", "UserData");
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
        get => field ??= new WanmeiLauncherApiMedia(NteGameConfig);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= new WanmeiLauncherApiNews(NteNewsConfig);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= new WanmeiGameManager(NteGameConfig);
        set;
    }

    public override IGameInstaller? GameInstaller
    {
        get => field ??= new WanmeiGameInstaller(GameManager!);
        set;
    }

    protected override Task<int> InitAsync(CancellationToken token)
    {
        return Task.FromResult(0);
    }
}
