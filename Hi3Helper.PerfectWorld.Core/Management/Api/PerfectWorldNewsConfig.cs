namespace Hi3Helper.PerfectWorld.Core.Management.Api;

/// <summary>
///     Per-game configuration for the news / carousel / social-media provider. These endpoints live on
///     the publisher's marketing website (not the pw_sdk resource CDN) and are game-specific, so a thin
///     plugin supplies them and the core stays reusable across Perfect World titles.
/// </summary>
public sealed class PerfectWorldNewsConfig
{
    /// <summary>
    ///     The launcher news web page that embeds the news list and social-media (QR) sidebar, e.g.
    ///     <c>https://yh.wanmei.com/launcher/launcher_ob.html?expand=1</c>.
    /// </summary>
    public required string NewsPageUrl { get; init; }

    /// <summary>
    ///     Origin used to absolutize the relative news links found on <see cref="NewsPageUrl"/>, e.g.
    ///     <c>https://yh.wanmei.com</c>.
    /// </summary>
    public required string NewsLinkBaseUrl { get; init; }

    /// <summary>
    ///     Optional JS document exposing the carousel/banner data as <c>var yh_data_data = { "lb1": [...] }</c>.
    ///     When null the carousel is left empty.
    /// </summary>
    public string? BannerJsUrl { get; init; }

    /// <summary>
    ///     Name of the JSON array property inside <see cref="BannerJsUrl"/> that holds the launcher carousel entries,
    ///     e.g. <c>lb1</c> for 异环 or <c>PC_Launcher</c> for P5X.
    /// </summary>
    public string BannerJsCarouselKey { get; init; } = "lb1";

    /// <summary>Optional <c>Referer</c> header sent with every request (some CDNs require it).</summary>
    public string? Referer { get; init; }
}
