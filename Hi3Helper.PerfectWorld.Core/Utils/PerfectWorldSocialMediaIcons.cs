using System;
using System.Text;

namespace Hi3Helper.PerfectWorld.Core.Utils;

/// <summary>
///     Self-contained social-media icon set for Perfect World launcher pages.
///     <para>
///         The official web launcher renders its social icons via a CSS sprite, so no standalone icon
///         image is exposed by the page. To stay dependency-free and NativeAOT-friendly (no embedded
///         resource files, no reflection-based manifest lookup), each platform icon is a small inline
///         SVG baked directly into the assembly. Colours are baked white/dark so the glyphs read on the
///         launcher's dark surface, since Collapse rasterizes the SVG as-is.
///     </para>
///     <para>
///         Icons are served through <c>LauncherSocialMediaEntry.WriteIcon(ReadOnlySpan&lt;byte&gt;)</c> as a
///         Base64 data buffer; Collapse recognises the leading <c>&lt;svg</c> magic and decodes it to a
///         local <c>.svg</c> file.
///     </para>
/// </summary>
internal static class PerfectWorldSocialMediaIcons
{
    /// <summary>Resolve a human-friendly display name for a platform key (the <c>icon-XXX</c> class).</summary>
    internal static string ResolveDisplayName(string? key)
    {
        return key?.Trim().ToLowerInvariant() switch
        {
            "weibo"  => "官方微博",
            "qq"     => "官方 QQ 群",
            "wechat" => "官方微信",
            "taptap" => "TapTap",
            "hykb"   => "好游快爆",
            "tjd"    => "塔吉多",
            "kf"     => "官方客服",
            // P5X (share-box) keys:
            "sina"   => "官方微博",
            "weixin" => "官方微信",
            "bbs"    => "官方网站",
            "pay"    => "官方充值中心",
            _        => string.IsNullOrWhiteSpace(key) ? "官方社区" : key.Trim()
        };
    }

    /// <summary>Resolve the UTF-8 bytes of the inline SVG icon for a platform key.</summary>
    internal static ReadOnlySpan<byte> Resolve(string? key)
    {
        string svg = key?.Trim().ToLowerInvariant() switch
        {
            "weibo"  => Weibo,
            "qq"     => Qq,
            "wechat" => Wechat,
            "taptap" => Taptap,
            "hykb"   => Hykb,
            "tjd"    => Tjd,
            "kf"     => Kf,
            // P5X (share-box) keys reuse the shared glyphs where they overlap:
            "sina"   => Weibo,
            "weixin" => Wechat,
            "bbs"    => Bbs,
            "pay"    => Pay,
            _        => Fallback
        };

        return Encoding.UTF8.GetBytes(svg);
    }

    // Each SVG MUST start with "<svg" (no XML prolog / BOM) so Collapse's magic-byte sniffing maps it to ".svg".

    private const string Weibo =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
        "<ellipse cx=\"12\" cy=\"12\" rx=\"9\" ry=\"6.5\" fill=\"#fff\"/>" +
        "<ellipse cx=\"12\" cy=\"12\" rx=\"4\" ry=\"3.4\" fill=\"#222\"/>" +
        "<circle cx=\"12\" cy=\"12\" r=\"1.4\" fill=\"#fff\"/></svg>";

    private const string Qq =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
        "<path fill=\"#fff\" d=\"M12 2.5c-2.6 0-4.6 2.1-4.6 4.7 0 .5-.3 1.2-.8 2C5.4 10.8 4.4 12.6 4.4 14.6c0 .9.3 1.6.9 2.2-.3.8-.6 1.7-.2 2.3.5.7 1.9.5 3.1-.2.5.2 1.1.3 1.8.3s1.3-.1 1.8-.3c1.2.7 2.6.9 3.1.2.4-.6.1-1.5-.2-2.3.6-.6.9-1.3.9-2.2 0-2-1-3.8-2.2-5.4-.5-.8-.8-1.5-.8-2 0-2.6-2-4.7-4.6-4.7z\"/>" +
        "<circle cx=\"10.2\" cy=\"7.8\" r=\"1\" fill=\"#222\"/>" +
        "<circle cx=\"13.8\" cy=\"7.8\" r=\"1\" fill=\"#222\"/></svg>";

    private const string Wechat =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
        "<path fill=\"#fff\" d=\"M9.2 4C5.2 4 2 6.7 2 10c0 1.9 1.1 3.6 2.8 4.7L4.2 17l2.6-1.3c.8.2 1.6.3 2.4.3h.4a5.7 5.7 0 0 1-.1-1.1c0-3.1 3-5.6 6.8-5.6h.6C16.7 6 13.3 4 9.2 4z\"/>" +
        "<circle cx=\"6.7\" cy=\"9\" r=\"1\" fill=\"#222\"/>" +
        "<circle cx=\"11.7\" cy=\"9\" r=\"1\" fill=\"#222\"/>" +
        "<path fill=\"#fff\" d=\"M22 14.7c0-2.6-2.5-4.7-5.7-4.7s-5.7 2.1-5.7 4.7 2.5 4.7 5.7 4.7c.7 0 1.4-.1 2-.3l2.1 1.1-.6-1.9c1.3-.9 2.2-2.1 2.2-3.4z\"/>" +
        "<circle cx=\"14.3\" cy=\"14.3\" r=\".9\" fill=\"#222\"/>" +
        "<circle cx=\"18.3\" cy=\"14.3\" r=\".9\" fill=\"#222\"/></svg>";

    private const string Taptap =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
        "<rect x=\"3\" y=\"3\" width=\"18\" height=\"18\" rx=\"5.5\" fill=\"#fff\"/>" +
        "<circle cx=\"9.3\" cy=\"11\" r=\"1.7\" fill=\"#222\"/>" +
        "<circle cx=\"14.7\" cy=\"11\" r=\"1.7\" fill=\"#222\"/>" +
        "<path d=\"M9 15.2c.9.8 4.1.8 6 0\" stroke=\"#222\" stroke-width=\"1.4\" fill=\"none\" stroke-linecap=\"round\"/></svg>";

    private const string Hykb =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
        "<path fill=\"#fff\" d=\"M6.5 7.5h11a4 4 0 0 1 4 4.2l-.3 3.3a2.4 2.4 0 0 1-4.3 1.2l-.9-1.2H8l-.9 1.2a2.4 2.4 0 0 1-4.3-1.2l-.3-3.3a4 4 0 0 1 4-4.2zM7 10v1.5H5.5V13H7v1.5h1.5V13H10v-1.5H8.5V10H7zm8.5 1.2a1.1 1.1 0 1 0 0-2.2 1.1 1.1 0 0 0 0 2.2zm2.3 2.3a1.1 1.1 0 1 0 0-2.2 1.1 1.1 0 0 0 0 2.2z\"/></svg>";

    private const string Tjd =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
        "<rect x=\"6\" y=\"2.5\" width=\"12\" height=\"19\" rx=\"2.5\" fill=\"#fff\"/>" +
        "<rect x=\"8\" y=\"5\" width=\"8\" height=\"11\" rx=\"1\" fill=\"#222\"/>" +
        "<circle cx=\"12\" cy=\"18.7\" r=\"1.1\" fill=\"#222\"/>" +
        "<path fill=\"#fff\" d=\"M12 7.2l2.2 2.2h-1.4v2.2h-1.6V9.4H9.8L12 7.2z\"/></svg>";

    private const string Kf =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
        "<path fill=\"#fff\" d=\"M12 3a8 8 0 0 0-8 8v3.5A2.5 2.5 0 0 0 6.5 17H8v-6H6.2A5.8 5.8 0 0 1 12 5.2 5.8 5.8 0 0 1 17.8 11H16v6h1.2c-.4 1.2-1.4 2-3.2 2.3v-1H10v2h4v-.1c3-.3 4.8-1.7 5.4-4.2A2.5 2.5 0 0 0 20 14.5V11a8 8 0 0 0-8-8z\"/></svg>";

    private const string Bbs =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
        "<circle cx=\"12\" cy=\"12\" r=\"9\" fill=\"#fff\"/>" +
        "<path d=\"M3 12h18\" stroke=\"#222\" stroke-width=\"1.2\"/>" +
        "<ellipse cx=\"12\" cy=\"12\" rx=\"4.2\" ry=\"9\" fill=\"none\" stroke=\"#222\" stroke-width=\"1.2\"/></svg>";

    private const string Pay =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
        "<circle cx=\"12\" cy=\"12\" r=\"9\" fill=\"#fff\"/>" +
        "<path fill=\"#222\" d=\"M8.6 6.5l3.4 4.2 3.4-4.2h1.7l-3.6 4.5H17v1.2h-3.4v1.2H17v1.2h-3.4V17.5h-1.7v-2.4H8.6v-1.2h3.3v-1.2H8.6v-1.2h2.4L7.4 6.5h1.2z\"/></svg>";

    private const string Fallback =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" +
        "<path fill=\"#fff\" d=\"M21.7 2.3a1 1 0 0 0-1.05-.23l-18 7a1 1 0 0 0 .08 1.89l7.1 2.37 2.37 7.1a1 1 0 0 0 .92.68h.03a1 1 0 0 0 .92-.61l7.87-17.1a1 1 0 0 0-.24-1.1Z\"/></svg>";
}
