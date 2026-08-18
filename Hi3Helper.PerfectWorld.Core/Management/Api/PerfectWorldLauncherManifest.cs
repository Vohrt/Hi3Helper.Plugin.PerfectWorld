using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace Hi3Helper.PerfectWorld.Core.Management.Api;

/// <summary>
///     A single entry in the launcher self-update manifest (<c>AllFiles.xml</c>). Every launcher file is stored
///     individually zip-compressed on the CDN, so <see cref="ZipMd5"/>/<see cref="ZipSize"/> describe the blob that
///     is downloaded and <see cref="Md5"/>/<see cref="Size"/> describe the file after inflation.
/// </summary>
public sealed class PerfectWorldLauncherFile
{
    /// <summary>Path relative to the launcher root (<c>NTELauncher\</c>), forward-slashed, without a leading slash.</summary>
    public required string Path { get; init; }

    /// <summary>MD5 of the inflated file.</summary>
    public required string Md5 { get; init; }

    /// <summary>Inflated file size in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>MD5 of the downloaded <c>.zip</c> blob.</summary>
    public required string ZipMd5 { get; init; }

    /// <summary>Downloaded <c>.zip</c> blob size in bytes.</summary>
    public required long ZipSize { get; init; }
}

/// <summary>Parsed launcher self-update manifest.</summary>
public sealed class PerfectWorldLauncherManifest
{
    /// <summary>Versioned directory segment used in file URLs, e.g. <c>1.0.6.0718_2</c>.</summary>
    public required string VersionDir { get; init; }

    /// <summary>Human-readable product version, e.g. <c>1.0.6.0718_2</c>.</summary>
    public required string ProductVersion { get; init; }

    public required IReadOnlyList<PerfectWorldLauncherFile> Files { get; init; }
}

/// <summary>Parsers for the launcher self-update manifests (<c>Version.ini</c> and <c>AllFiles.xml</c>).</summary>
public static class PerfectWorldLauncherManifestParser
{
    /// <summary>
    ///     Parses the <c>[VERSION]</c> section of a launcher <c>Version.ini</c>, returning the <c>FileListURL</c>
    ///     (full URL to <c>AllFiles.xml</c>) plus the informational <c>Version</c>/<c>Build</c> values.
    /// </summary>
    public static (string? FileListUrl, string? Version, string? Build) ParseVersionIni(string ini)
    {
        string? fileListUrl = null, version = null, build = null;

        foreach (string rawLine in ini.TrimStart('\uFEFF').Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '[' or ';' or '#') continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            if (key.Equals("FileListURL", StringComparison.OrdinalIgnoreCase)) fileListUrl = value;
            else if (key.Equals("Version", StringComparison.OrdinalIgnoreCase)) version ??= value;
            else if (key.Equals("Build", StringComparison.OrdinalIgnoreCase)) build ??= value;
        }

        return (fileListUrl, version, build);
    }

    /// <summary>
    ///     Reads the informational <c>Version</c> and <c>Build</c> values from a single INI section (default
    ///     <c>VERSION</c>). Unlike <see cref="ParseVersionIni"/> (which scans the whole file for those keys), this is
    ///     scoped to one section so it can be used on multi-section vendor files such as the launcher's local
    ///     <c>Config\Config.ini</c> without accidentally picking up same-named keys from an unrelated section.
    /// </summary>
    public static (string? Version, string? Build) ParseSectionVersionBuild(string ini, string section = "VERSION")
    {
        string? version = null, build = null;
        bool inSection = false;

        foreach (string rawLine in ini.TrimStart('\uFEFF').Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;

            if (line[0] == '[')
            {
                // Section header, e.g. "[VERSION]" → "VERSION".
                inSection = line.Trim('[', ']', ' ', '\t', '\r').Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            if (key.Equals("Version", StringComparison.OrdinalIgnoreCase)) version ??= value;
            else if (key.Equals("Build", StringComparison.OrdinalIgnoreCase)) build ??= value;
        }

        return (version, build);
    }

    /// <summary>
    ///     Extracts the versioned directory segment (the one immediately before <c>AllFiles.xml</c>) from a
    ///     <c>FileListURL</c>, e.g. <c>https://.../launcher/1.0.6.0718_2/AllFiles.xml</c> → <c>1.0.6.0718_2</c>.
    /// </summary>
    public static string? ExtractVersionDir(string fileListUrl)
    {
        if (string.IsNullOrWhiteSpace(fileListUrl)) return null;
        if (!Uri.TryCreate(fileListUrl, UriKind.Absolute, out Uri? uri)) return null;

        string[] segments = uri.Segments;
        if (segments.Length < 2) return null;

        // segments[^1] == "AllFiles.xml"; the directory just above it is the versioned dir.
        return segments[^2].Trim('/');
    }

    /// <summary>
    ///     Parses an <c>AllFiles.xml</c> launcher manifest into a strongly-typed <see cref="PerfectWorldLauncherManifest"/>.
    /// </summary>
    public static PerfectWorldLauncherManifest ParseAllFiles(string xml, string versionDir)
    {
        var files = new List<PerfectWorldLauncherFile>();
        XElement? root = XDocument.Parse(xml.TrimStart('\uFEFF')).Root;

        string productVersion = versionDir;

        if (root != null)
        {
            foreach (XElement element in root.Elements())
            {
                string local = element.Name.LocalName;

                if (local.Equals("ProductVersion", StringComparison.OrdinalIgnoreCase))
                {
                    string? v = (string?)element.Attribute("Version");
                    if (!string.IsNullOrEmpty(v)) productVersion = v;
                    continue;
                }

                if (!local.Equals("File", StringComparison.OrdinalIgnoreCase)) continue;

                string? path = (string?)element.Attribute("Path");
                string? md5 = (string?)element.Attribute("Checksum");
                string? sizeText = (string?)element.Attribute("Size");
                string? zipMd5 = (string?)element.Attribute("ZipChecksum");
                string? zipSizeText = (string?)element.Attribute("ZipSize");

                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(md5) ||
                    string.IsNullOrEmpty(sizeText) || string.IsNullOrEmpty(zipMd5) ||
                    string.IsNullOrEmpty(zipSizeText))
                    continue;

                if (!long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) ||
                    !long.TryParse(zipSizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long zipSize))
                    continue;

                files.Add(new PerfectWorldLauncherFile
                {
                    Path    = path.Replace('\\', '/').TrimStart('/'),
                    Md5     = md5.ToLowerInvariant(),
                    Size    = size,
                    ZipMd5  = zipMd5.ToLowerInvariant(),
                    ZipSize = zipSize
                });
            }
        }

        return new PerfectWorldLauncherManifest
        {
            VersionDir     = versionDir,
            ProductVersion = productVersion,
            Files          = files
        };
    }
}
