using System;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.P5X;

[GeneratedComClass]
// ReSharper disable once InconsistentNaming
internal partial class SelfUpdate : PluginSelfUpdateBase
{
    private const string ExCdnFileSuffix = "P5X/";

    // The self-updater fetches "<BaseCdnUrl>/manifest.json" plus every asset it lists
    // (e.g. "P5X.dll"). Those files are published to the "release" branch of this
    // repository by the release workflow and served over raw.githubusercontent.com.
    private const string ExCdn1Url =
        "https://raw.githubusercontent.com/Vohrt/Hi3Helper.Plugin.PerfectWorld/release/" + ExCdnFileSuffix;

    // Fallback that resolves to the same content through github.com's redirect.
    private const string ExCdn2Url =
        "https://github.com/Vohrt/Hi3Helper.Plugin.PerfectWorld/raw/release/" + ExCdnFileSuffix;

    protected readonly string[] BaseCdnUrl = [ExCdn1Url, ExCdn2Url];

    internal SelfUpdate()
    {
        UpdateHttpClient = new PluginHttpClientBuilder()
            .AllowRedirections()
            .AllowUntrustedCert()
            .AllowCookies()
            .Create();
    }

    protected override ReadOnlySpan<string> BaseCdnUrlSpan => BaseCdnUrl;
    protected override HttpClient UpdateHttpClient { get; }
}
