using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.PerfectWorld.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.PerfectWorld.Core.Management.Api;

/// <summary>
///     News / carousel / social-media provider for Perfect World launcher pages.
///     <para>
///         pw_sdk titles have no JSON launcher-content API; instead the official web launcher page embeds
///         everything as HTML plus a small companion JS document for the banner carousel. The HTML markup
///         differs per title, so <see cref="PerfectWorldNewsConfig.Layout"/> selects the dialect:
///         <list type="bullet">
///             <item>
///                 <see cref="PerfectWorldNewsLayout.NewsContEwmList"/> (异环 / NTE): three
///                 <c>&lt;div class="news-cont"&gt;</c> news blocks + a <c>&lt;ul class="ewm-list"&gt;</c>
///                 social sidebar of <c>icon-*</c> entries.
///             </item>
///             <item>
///                 <see cref="PerfectWorldNewsLayout.BdNewsShareBox"/> (P5X): one
///                 <c>&lt;div class="bd news"&gt;</c> holding three <c>&lt;ul&gt;</c> news blocks + a
///                 <c>&lt;div class="share-box"&gt;</c> of <c>share-icon-*</c> social entries.
///             </item>
///             <item>Carousel: <c>var *_data_data = { "&lt;key&gt;": [...] }</c> exposing each banner's image + link.</item>
///         </list>
///         All parsing is done with source-generated regex + <see cref="JsonDocument"/> to stay
///         NativeAOT-safe with no third-party HTML/JSON dependencies.
///     </para>
/// </summary>
[GeneratedComClass]
public partial class PerfectWorldLauncherApiNews : LauncherApiNewsBase
{
    private readonly PerfectWorldNewsConfig _config;

    private List<NewsItem>     _newsItems     = [];
    private List<CarouselItem> _carouselItems = [];
    private List<SocialItem>   _socialItems   = [];

    public PerfectWorldLauncherApiNews(PerfectWorldNewsConfig config)
    {
        _config = config;
    }

    [field: AllowNull]
    [field: MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get
        {
            if (field is not null)
            {
                return field;
            }

            PluginHttpClientBuilder builder = new PluginHttpClientBuilder()
                .SetAllowedDecompression(DecompressionMethods.All)
                .AllowRedirections()
                .AllowUntrustedCert()
                .AllowCookies()
                .SetUserAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CollapsePlugin/1.0");

            if (!string.IsNullOrEmpty(_config.Referer))
            {
                builder.AddHeader("Referer", _config.Referer);
            }

            return field = builder.Create();
        }
        set;
    }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        // News + social media come from a single HTML page.
        try
        {
            string html = await ApiResponseHttpClient.GetStringAsync(_config.NewsPageUrl, token).ConfigureAwait(false);
            _newsItems   = ParseNews(html);
            _socialItems = ParseSocial(html);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning("[PerfectWorldNews] Failed to fetch/parse news page: {Msg}", ex.Message);
        }

        // Carousel banners come from a companion JS document.
        if (!string.IsNullOrEmpty(_config.BannerJsUrl))
        {
            try
            {
                string js = await ApiResponseHttpClient.GetStringAsync(_config.BannerJsUrl, token).ConfigureAwait(false);
                _carouselItems = ParseCarousel(js);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning("[PerfectWorldNews] Failed to fetch/parse banner JS: {Msg}", ex.Message);
            }
        }

        SharedStatic.InstanceLogger.LogInformation(
            "[PerfectWorldNews] Parsed News={News}, Carousel={Carousel}, Social={Social}",
            _newsItems.Count, _carouselItems.Count, _socialItems.Count);
        return 0;
    }

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (_newsItems.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = _newsItems.Count;
        PluginDisposableMemory<LauncherNewsEntry> memory = PluginDisposableMemory<LauncherNewsEntry>.Alloc(count);
        for (int i = 0; i < count; i++)
        {
            NewsItem item = _newsItems[i];
            memory[i].Write(item.Title, null, item.Url, item.Date, item.Type);
        }

        handle       = memory.AsSafePointer();
        isDisposable = true;
        isAllocated  = true;
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (_carouselItems.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = _carouselItems.Count;
        PluginDisposableMemory<LauncherCarouselEntry> memory = PluginDisposableMemory<LauncherCarouselEntry>.Alloc(count);
        for (int i = 0; i < count; i++)
        {
            CarouselItem item = _carouselItems[i];
            memory[i].Write(null, item.Image, item.Link);
        }

        handle       = memory.AsSafePointer();
        isDisposable = true;
        isAllocated  = true;
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        if (_socialItems.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = _socialItems.Count;
        PluginDisposableMemory<LauncherSocialMediaEntry> memory =
            PluginDisposableMemory<LauncherSocialMediaEntry>.Alloc(count);
        for (int i = 0; i < count; i++)
        {
            SocialItem item        = _socialItems[i];
            string     displayName = PerfectWorldSocialMediaIcons.ResolveDisplayName(item.Key);

            ref LauncherSocialMediaEntry entry = ref memory[i];
            entry.WriteIcon(PerfectWorldSocialMediaIcons.Resolve(item.Key));
            entry.WriteIconHover(PerfectWorldSocialMediaIcons.Resolve(item.Key));
            entry.WriteDescription(displayName);

            if (!string.IsNullOrWhiteSpace(item.ClickUrl))
            {
                entry.WriteClickUrl(item.ClickUrl);
            }

            if (!string.IsNullOrWhiteSpace(item.QrImage))
            {
                entry.WriteQrImage(item.QrImage);
                entry.WriteQrImageDescription(string.IsNullOrWhiteSpace(item.Tip) ? displayName : item.Tip);
            }
        }

        handle       = memory.AsSafePointer();
        isDisposable = true;
        isAllocated  = true;
    }

    private List<NewsItem> ParseNews(string html)
    {
        return _config.Layout == PerfectWorldNewsLayout.BdNewsShareBox
            ? ParseNewsBdNews(html)
            : ParseNewsNewsCont(html);
    }

    // 异环 / NTE: three separate <div class="news-cont"><ul> blocks (Info / Notice / Event).
    private List<NewsItem> ParseNewsNewsCont(string html)
    {
        var items = new List<NewsItem>();

        MatchCollection blocks = NewsContRegex().Matches(html);
        for (int b = 0; b < blocks.Count; b++)
        {
            // Blocks are rendered in a fixed order: 新闻 (Info) / 公告 (Notice) / 活动 (Event).
            LauncherNewsEntryType type = b switch
            {
                0 => LauncherNewsEntryType.Info,
                1 => LauncherNewsEntryType.Notice,
                _ => LauncherNewsEntryType.Event
            };

            foreach (Match im in NewsItemRegex().Matches(blocks[b].Groups[1].Value))
            {
                string title = WebUtility.HtmlDecode(im.Groups[2].Value).Trim();
                if (string.IsNullOrEmpty(title))
                {
                    continue;
                }

                string href = im.Groups[1].Value.Trim();
                string date = WebUtility.HtmlDecode(im.Groups[3].Value).Trim();
                items.Add(new NewsItem(title, AbsolutizeUrl(href), date, type));
            }
        }

        return items;
    }

    // P5X: one <div class="bd news"> holding three <ul> blocks (Info / Notice / Event). Each item is
    // <li><a href="URL " title="FULL TITLE"><span class="date">MM/DD</span><p>truncated</p></a></li>,
    // so the full title lives in the title attribute and the href carries a trailing space to trim.
    private List<NewsItem> ParseNewsBdNews(string html)
    {
        var items = new List<NewsItem>();

        Match block = BdNewsRegex().Match(html);
        if (!block.Success)
        {
            return items;
        }

        MatchCollection uls = BdNewsUlRegex().Matches(block.Groups[1].Value);
        for (int b = 0; b < uls.Count; b++)
        {
            LauncherNewsEntryType type = b switch
            {
                0 => LauncherNewsEntryType.Info,
                1 => LauncherNewsEntryType.Notice,
                _ => LauncherNewsEntryType.Event
            };

            foreach (Match im in BdNewsItemRegex().Matches(uls[b].Groups[1].Value))
            {
                string title = WebUtility.HtmlDecode(im.Groups[2].Value).Trim();
                if (string.IsNullOrEmpty(title))
                {
                    continue;
                }

                string href = WebUtility.HtmlDecode(im.Groups[1].Value).Trim();
                string date = WebUtility.HtmlDecode(im.Groups[3].Value).Trim();
                items.Add(new NewsItem(title, AbsolutizeUrl(href), date, type));
            }
        }

        return items;
    }

    private List<SocialItem> ParseSocial(string html)
    {
        return _config.Layout == PerfectWorldNewsLayout.BdNewsShareBox
            ? ParseSocialShareBox(html)
            : ParseSocialEwmList(html);
    }

    // 异环 / NTE: a <ul class="ewm-list"> sidebar of <li class="icon-XXX"> entries.
    private List<SocialItem> ParseSocialEwmList(string html)
    {
        var items = new List<SocialItem>();

        Match list = EwmListRegex().Match(html);
        if (!list.Success)
        {
            return items;
        }

        foreach (Match li in EwmItemRegex().Matches(list.Groups[1].Value))
        {
            string  key  = li.Groups[1].Value.Trim();
            string  body = li.Groups[2].Value;

            Match   hrefMatch = EwmHrefRegex().Match(body);
            Match   imgMatch  = EwmImgRegex().Match(body);
            Match   tipMatch  = EwmTipRegex().Match(body);

            string? click = hrefMatch.Success ? WebUtility.HtmlDecode(hrefMatch.Groups[1].Value).Trim() : null;
            string? img   = imgMatch.Success ? imgMatch.Groups[1].Value.Trim() : null;
            string? tip   = tipMatch.Success ? WebUtility.HtmlDecode(tipMatch.Groups[1].Value).Trim() : null;

            items.Add(new SocialItem(key, click, img, tip));
        }

        return items;
    }

    // P5X: a <div class="share-box"><ul> of <li> entries, each carrying a <div class="share-icon share-icon-XXX">
    // plus a float-code-box(-f|-btn) with an optional <a href>, optional QR <img>, and optional <p> tip.
    private List<SocialItem> ParseSocialShareBox(string html)
    {
        var items = new List<SocialItem>();

        Match box = ShareBoxRegex().Match(html);
        if (!box.Success)
        {
            return items;
        }

        foreach (Match li in ShareItemRegex().Matches(box.Groups[1].Value))
        {
            string body = li.Groups[1].Value;

            Match keyMatch = ShareKeyRegex().Match(body);
            if (!keyMatch.Success)
            {
                continue;
            }

            string key = keyMatch.Groups[1].Value.Trim();

            Match   hrefMatch = ShareHrefRegex().Match(body);
            Match   imgMatch  = EwmImgRegex().Match(body);
            Match   tipMatch  = ShareTipRegex().Match(body);

            string? click = hrefMatch.Success ? WebUtility.HtmlDecode(hrefMatch.Groups[1].Value).Trim() : null;
            string? img   = imgMatch.Success ? imgMatch.Groups[1].Value.Trim() : null;
            string? tip   = tipMatch.Success ? WebUtility.HtmlDecode(tipMatch.Groups[1].Value).Trim() : null;

            items.Add(new SocialItem(key, click, img, tip));
        }

        return items;
    }

    private List<CarouselItem> ParseCarousel(string js)
    {
        var items = new List<CarouselItem>();

        int start = js.IndexOf('{');
        int end   = js.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return items;
        }

        using var doc = JsonDocument.Parse(js.Substring(start, end - start + 1));
        if (!doc.RootElement.TryGetProperty(_config.BannerJsCarouselKey, out JsonElement lb1) ||
            lb1.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement el in lb1.EnumerateArray())
        {
            string? big = el.TryGetProperty("bigpic", out JsonElement bp) && bp.ValueKind == JsonValueKind.String
                ? bp.GetString()
                : null;
            if (string.IsNullOrEmpty(big))
            {
                continue;
            }

            string? link = el.TryGetProperty("link", out JsonElement lk) && lk.ValueKind == JsonValueKind.String
                ? lk.GetString()
                : null;
            items.Add(new CarouselItem(big, link ?? string.Empty));
        }

        return items;
    }

    private string AbsolutizeUrl(string href)
    {
        if (string.IsNullOrEmpty(href) || href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return href;
        }

        return _config.NewsLinkBaseUrl.TrimEnd('/') + "/" + href.TrimStart('/');
    }

    private static void InitializeEmpty(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        handle       = nint.Zero;
        count        = 0;
        isDisposable = false;
        isAllocated  = false;
    }

    public override void Dispose()
    {
        if (IsDisposed) return;
        ApiResponseHttpClient?.Dispose();
        base.Dispose();
    }

    private readonly record struct NewsItem(string Title, string Url, string Date, LauncherNewsEntryType Type);

    private readonly record struct CarouselItem(string Image, string Link);

    private readonly record struct SocialItem(string Key, string? ClickUrl, string? QrImage, string? Tip);

    [GeneratedRegex(@"class=""news-cont""[^>]*>\s*<ul>(.*?)</ul>", RegexOptions.Singleline)]
    private static partial Regex NewsContRegex();

    [GeneratedRegex(@"<a\s+href=""([^""]+)""[^>]*>(.*?)</a>\s*<span>([^<]*)</span>", RegexOptions.Singleline)]
    private static partial Regex NewsItemRegex();

    [GeneratedRegex(@"<ul class=""ewm-list"">(.*?)</ul>", RegexOptions.Singleline)]
    private static partial Regex EwmListRegex();

    [GeneratedRegex(@"<li class=""icon-([^""]+)"">(.*?)</li>", RegexOptions.Singleline)]
    private static partial Regex EwmItemRegex();

    [GeneratedRegex(@"class=""ewm-title""\s+href=""([^""]+)""")]
    private static partial Regex EwmHrefRegex();

    [GeneratedRegex(@"<img[^>]+src=""([^""]+)""")]
    private static partial Regex EwmImgRegex();

    [GeneratedRegex(@"class=""ewm-tip"">([^<]*)<")]
    private static partial Regex EwmTipRegex();

    // --- P5X (BdNewsShareBox) ---

    [GeneratedRegex(@"<div class=""bd news"">(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex BdNewsRegex();

    [GeneratedRegex(@"<ul[^>]*>(.*?)</ul>", RegexOptions.Singleline)]
    private static partial Regex BdNewsUlRegex();

    [GeneratedRegex(@"<a\s+href=""([^""]+)""[^>]*title=""([^""]*)""[^>]*>\s*<span[^>]*class=""date""[^>]*>([^<]*)</span>", RegexOptions.Singleline)]
    private static partial Regex BdNewsItemRegex();

    [GeneratedRegex(@"<div class=""share-box"">\s*<ul>(.*?)</ul>", RegexOptions.Singleline)]
    private static partial Regex ShareBoxRegex();

    [GeneratedRegex(@"<li>(.*?)</li>", RegexOptions.Singleline)]
    private static partial Regex ShareItemRegex();

    [GeneratedRegex(@"share-icon-([a-zA-Z0-9]+)")]
    private static partial Regex ShareKeyRegex();

    [GeneratedRegex(@"<a\s+href=""([^""]+)""")]
    private static partial Regex ShareHrefRegex();

    [GeneratedRegex(@"<p>([^<]*)</p>", RegexOptions.Singleline)]
    private static partial Regex ShareTipRegex();
}
