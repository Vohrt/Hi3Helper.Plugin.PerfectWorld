using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace Hi3Helper.Wanmei.Core.Management.Api;

/// <summary>
///     Parsed content of the plaintext game-resource <c>config.xml</c> entry point
///     (<c>.../{branch}/Version/{platform}/config.xml</c>).
/// </summary>
public sealed class WanmeiRemoteConfig
{
    public string ResVersion { get; init; } = string.Empty;
    public long ResSize { get; init; }

    /// <summary>Short version/tag hash (matches the <c>v=</c> attribute of <c>lastdiff</c> patch entries).</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>MD5 of <c>ResList.bin</c> (integrity check for the full manifest).</summary>
    public string ListHash { get; init; } = string.Empty;

    /// <summary>MD5 of <c>lastdiff.bin</c> (integrity check for the incremental manifest).</summary>
    public string DiffHash { get; init; } = string.Empty;

    public static WanmeiRemoteConfig Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root
                   ?? throw new InvalidOperationException("config.xml has no root element.");

        string? extraValue(string name) => FindFirst(root, name)?.Value?.Trim();

        long.TryParse(extraValue("ResSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long resSize);

        return new WanmeiRemoteConfig
        {
            ResVersion = extraValue("ResVersion") ?? string.Empty,
            ResSize    = resSize,
            Hash       = extraValue("Hash") ?? string.Empty,
            ListHash   = extraValue("listHash") ?? string.Empty,
            DiffHash   = extraValue("diffHash") ?? string.Empty
        };
    }

    private static XElement? FindFirst(XElement root, string localName)
    {
        foreach (var element in root.DescendantsAndSelf())
            if (string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                return element;
        return null;
    }
}

/// <summary>
///     A single content-addressed file entry from the decrypted <c>ResList.bin</c> manifest.
/// </summary>
public sealed class WanmeiResEntry
{
    public required string Filename { get; init; }
    public required long FileSize { get; init; }
    public required string Md5 { get; init; }
}

/// <summary>
///     A single incremental patch entry from the decrypted <c>lastdiff.bin</c> manifest.
/// </summary>
public sealed class WanmeiPatchEntry
{
    /// <summary>Content id (<c>md5</c>) of the file to patch from.</summary>
    public required string OldMd5 { get; init; }

    public required long OldSize { get; init; }

    /// <summary>Content id (<c>md5</c>) of the resulting file.</summary>
    public required string NewMd5 { get; init; }

    public required long NewSize { get; init; }

    /// <summary>Content id (<c>md5</c>) of the HDiffPatch delta blob.</summary>
    public required string PatchMd5 { get; init; }

    public required long PatchSize { get; init; }
}

/// <summary>
///     Parsers for the decrypted pw_sdk XML manifests.
/// </summary>
public static class WanmeiManifest
{
    /// <summary>
    ///     Parses a decrypted <c>ResList</c> manifest and returns every content-addressed file entry
    ///     (any <c>&lt;Res&gt;</c> element carrying <c>filename</c>, <c>filesize</c> and <c>md5</c> attributes).
    /// </summary>
    public static List<WanmeiResEntry> ParseResList(string xml)
    {
        var entries = new List<WanmeiResEntry>();
        var root = XDocument.Parse(xml).Root;
        if (root == null) return entries;

        foreach (var element in root.DescendantsAndSelf())
        {
            if (!string.Equals(element.Name.LocalName, "Res", StringComparison.OrdinalIgnoreCase))
                continue;

            string? filename = (string?)element.Attribute("filename");
            string? md5 = (string?)element.Attribute("md5");
            string? sizeText = (string?)element.Attribute("filesize");

            if (string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(md5) || string.IsNullOrEmpty(sizeText))
                continue;

            if (!long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size))
                continue;

            entries.Add(new WanmeiResEntry
            {
                Filename = filename.Replace('\\', '/'),
                FileSize = size,
                Md5      = md5.ToLowerInvariant()
            });
        }

        return entries;
    }

    /// <summary>
    ///     Parses a decrypted <c>lastdiff</c> manifest (<c>&lt;PatchList&gt;&lt;Patch .../&gt;</c>).
    /// </summary>
    public static List<WanmeiPatchEntry> ParsePatchList(string xml)
    {
        var entries = new List<WanmeiPatchEntry>();
        var root = XDocument.Parse(xml).Root;
        if (root == null) return entries;

        foreach (var element in root.DescendantsAndSelf())
        {
            if (!string.Equals(element.Name.LocalName, "Patch", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TrySplitContentId((string?)element.Attribute("oldfile"), out string oldMd5, out long oldSize) ||
                !TrySplitContentId((string?)element.Attribute("newfile"), out string newMd5, out long newSize) ||
                !TrySplitContentId((string?)element.Attribute("patch"), out string patchMd5, out long patchSize))
                continue;

            entries.Add(new WanmeiPatchEntry
            {
                OldMd5    = oldMd5,
                OldSize   = oldSize,
                NewMd5    = newMd5,
                NewSize   = newSize,
                PatchMd5  = patchMd5,
                PatchSize = patchSize
            });
        }

        return entries;
    }

    /// <summary>
    ///     Splits a <c>"&lt;md5&gt;.&lt;size&gt;"</c> content id into its parts.
    /// </summary>
    public static bool TrySplitContentId(string? value, out string md5, out long size)
    {
        md5 = string.Empty;
        size = 0;
        if (string.IsNullOrEmpty(value)) return false;

        int dot = value.IndexOf('.');
        if (dot <= 0 || dot >= value.Length - 1) return false;

        md5 = value[..dot].ToLowerInvariant();
        return long.TryParse(value.AsSpan(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
    }
}
