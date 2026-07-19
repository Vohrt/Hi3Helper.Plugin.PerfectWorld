using System;
using System.Globalization;

namespace Hi3Helper.Wanmei.Core;

/// <summary>
///     Immutable per-game configuration for a Perfect World (Wanmei) <c>pw_sdk</c> title. A thin plugin
///     supplies one of these to describe its game; everything CDN/URL related is derived from it so the
///     core stays game-agnostic and reusable across pw_sdk titles.
/// </summary>
public sealed class WanmeiGameConfig
{
    /// <summary>Numeric application id used to derive the manifest AES key (NTE / 异环 = <c>1289</c>).</summary>
    public required string AppId { get; init; }

    /// <summary>Game-resource branch, e.g. <c>publish_PC</c>.</summary>
    public required string GameResBranch { get; init; }

    /// <summary>Platform path segment used in the resource URLs, e.g. <c>Windows</c>.</summary>
    public string Platform { get; init; } = "Windows";

    /// <summary>
    ///     Ordered list of game-resource CDN roots (primary first, backups after), each ending at
    ///     <c>/clientRes</c>, e.g. <c>https://yhcdn1.wmupd.com/clientRes</c>.
    /// </summary>
    public required string[] GameResCdnUrls { get; init; }

    /// <summary>Launcher self-update branch, e.g. <c>publish_ob</c>.</summary>
    public required string LauncherBranch { get; init; }

    /// <summary>
    ///     Ordered list of launcher-distribution CDN roots, each ending at <c>/hd</c>,
    ///     e.g. <c>https://yhcdn1.wmupd.com/hd</c>.
    /// </summary>
    public required string[] LauncherCdnUrls { get; init; }

    /// <summary>
    ///     Path (relative to the install root) of the executable Collapse should launch,
    ///     e.g. <c>NTELauncher\NTEGame.exe</c>.
    /// </summary>
    public required string GameExecutableRelativePath { get; init; }

    /// <summary>Command-line arguments passed to the launched executable, e.g. <c>/launcher</c>.</summary>
    public string LaunchArguments { get; init; } = string.Empty;

    private string PrimaryGameResCdn =>
        GameResCdnUrls is { Length: > 0 } ? GameResCdnUrls[0].TrimEnd('/') : string.Empty;

    /// <summary>Plaintext resource entry point: <c>.../{branch}/Version/{platform}/config.xml?tValue={ms}</c>.</summary>
    public string BuildConfigXmlUrl(string cdnRoot)
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{cdnRoot.TrimEnd('/')}/{GameResBranch}/Version/{Platform}/config.xml?tValue={ms.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Encrypted manifest bundle: <c>.../{branch}/Version/{platform}/version/{resVersion}/ResList.bin.zip</c>.</summary>
    public string BuildResListZipUrl(string cdnRoot, string resVersion)
    {
        return $"{cdnRoot.TrimEnd('/')}/{GameResBranch}/Version/{Platform}/version/{resVersion}/ResList.bin.zip";
    }

    /// <summary>
    ///     Content-addressed file URL: <c>.../{branch}/Res/{md5[0]}/{md5}.{size}</c>.
    /// </summary>
    public string BuildContentUrl(string cdnRoot, string md5, long size)
    {
        return $"{cdnRoot.TrimEnd('/')}/{GameResBranch}/Res/{md5[..1]}/{md5}.{size.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Content-addressed file URL on the primary CDN.</summary>
    public string BuildContentUrl(string md5, long size) => BuildContentUrl(PrimaryGameResCdn, md5, size);

    /// <summary>Launcher self-update version descriptor: <c>.../{branch}/launcher/Version.ini</c>.</summary>
    public string BuildLauncherVersionIniUrl(string cdnRoot)
    {
        return $"{cdnRoot.TrimEnd('/')}/{LauncherBranch}/launcher/Version.ini";
    }
}
