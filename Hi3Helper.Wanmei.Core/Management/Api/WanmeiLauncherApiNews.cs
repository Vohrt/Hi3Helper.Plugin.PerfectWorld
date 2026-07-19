using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Management.Api;

namespace Hi3Helper.Wanmei.Core.Management.Api;

/// <summary>
///     Minimal news provider returning no entries (pw_sdk titles expose no public launcher-news API).
/// </summary>
[GeneratedComClass]
public partial class WanmeiLauncherApiNews : LauncherApiNewsBase
{
    protected override HttpClient ApiResponseHttpClient { get; set; } = new();

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }
}
