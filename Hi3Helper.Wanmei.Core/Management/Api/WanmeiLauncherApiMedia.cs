using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Management.Api;

namespace Hi3Helper.Wanmei.Core.Management.Api;

/// <summary>
///     Minimal media provider. pw_sdk titles do not expose a public launcher-media JSON API, so no remote
///     background/logo is served; Collapse falls back to the plugin's embedded poster.
/// </summary>
[GeneratedComClass]
public partial class WanmeiLauncherApiMedia : LauncherApiMediaBase
{
    protected override HttpClient ApiResponseHttpClient { get; set; } = new();

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
    {
        result = LauncherBackgroundFlag.None;
    }

    public override void GetLogoFlag(out LauncherBackgroundFlag result)
    {
        result = LauncherBackgroundFlag.None;
    }

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }
}
