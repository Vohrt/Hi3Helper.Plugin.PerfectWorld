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
///     A single file packed inside a <see cref="WanmeiPakEntry"/> archive. It is extracted by reading exactly
///     <see cref="Size"/> bytes starting at byte <see cref="Offset"/> of the downloaded pak blob; the resulting
///     bytes hash to <see cref="Md5"/>. (<c>start</c> in the manifest points at the per-entry header and is not
///     needed for extraction.)
/// </summary>
public sealed class WanmeiPakFile
{
    /// <summary>Destination path relative to the install root (same convention as <see cref="WanmeiResEntry.Filename"/>).</summary>
    public required string Filename { get; init; }

    /// <summary>Byte offset of the file's payload inside the pak blob.</summary>
    public required long Offset { get; init; }

    /// <summary>Payload length in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>MD5 of the extracted payload.</summary>
    public required string Md5 { get; init; }
}

/// <summary>
///     A content-addressed <c>&lt;Pak&gt;</c> archive from the decrypted <c>ResList.bin</c> manifest. The pak blob
///     is downloaded like any other content file (keyed by <see cref="Md5"/>/<see cref="FileSize"/>) and bundles
///     many small <see cref="Files"/> that are <em>not</em> individually addressable on the CDN — the only way to
///     obtain them is to download the whole pak and slice each entry out of it.
/// </summary>
public sealed class WanmeiPakEntry
{
    /// <summary>Content id (<c>md5</c>) of the pak blob.</summary>
    public required string Md5 { get; init; }

    /// <summary>Size in bytes of the pak blob.</summary>
    public required long FileSize { get; init; }

    /// <summary>The files packed inside this pak.</summary>
    public required IReadOnlyList<WanmeiPakFile> Files { get; init; }
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
    ///     Parses every <c>&lt;Pak&gt;</c> archive in a decrypted <c>ResList</c> manifest (inside any
    ///     <c>&lt;Package&gt;</c>, including those nested under <c>&lt;BaseVersion&gt;</c> tag sections) together with
    ///     the <c>&lt;Entry&gt;</c> files each one packs. These bundle the many small runtime files (native DLLs,
    ///     anti-cheat, IoStore side-cars, …) that are <em>not</em> exposed as individually content-addressed
    ///     <c>&lt;Res&gt;</c> entries, so they must be downloaded as part of their pak and sliced out locally.
    /// </summary>
    public static List<WanmeiPakEntry> ParsePackages(string xml)
    {
        var paks = new List<WanmeiPakEntry>();
        var root = XDocument.Parse(xml).Root;
        if (root == null) return paks;

        foreach (var pakElement in root.DescendantsAndSelf())
        {
            if (!string.Equals(pakElement.Name.LocalName, "Pak", StringComparison.OrdinalIgnoreCase))
                continue;

            string? pakMd5 = (string?)pakElement.Attribute("md5");
            string? pakSizeText = (string?)pakElement.Attribute("filesize");

            if (string.IsNullOrEmpty(pakMd5) || string.IsNullOrEmpty(pakSizeText))
                continue;
            if (!long.TryParse(pakSizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pakSize))
                continue;

            var files = new List<WanmeiPakFile>();
            foreach (var entry in pakElement.Elements())
            {
                if (!string.Equals(entry.Name.LocalName, "Entry", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? name = (string?)entry.Attribute("name");
                string? md5 = (string?)entry.Attribute("md5");
                string? offsetText = (string?)entry.Attribute("offset");
                string? sizeText = (string?)entry.Attribute("size");

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(md5) ||
                    string.IsNullOrEmpty(offsetText) || string.IsNullOrEmpty(sizeText))
                    continue;
                if (!long.TryParse(offsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset) ||
                    !long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size))
                    continue;

                files.Add(new WanmeiPakFile
                {
                    Filename = name.Replace('\\', '/'),
                    Offset   = offset,
                    Size     = size,
                    Md5      = md5.ToLowerInvariant()
                });
            }

            if (files.Count == 0) continue;

            paks.Add(new WanmeiPakEntry
            {
                Md5      = pakMd5.ToLowerInvariant(),
                FileSize = pakSize,
                Files    = files
            });
        }

        return paks;
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
