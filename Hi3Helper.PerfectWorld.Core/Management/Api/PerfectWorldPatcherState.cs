using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Hi3Helper.PerfectWorld.Core.Management.Api;

/// <summary>
///     Builds the finalized state files the native Perfect World <c>pw_sdk PatcherSDK</c> reads on game launch and
///     the in-game resource updater re-checks on return to the login screen (<c>config.xml</c>, <c>ResList.xml</c>
///     and <c>tmp/client.xml</c>), which the plugin must forge because it downloads the game directly and never runs
///     the vendor patcher that would normally author them.
/// </summary>
/// <remarks>
///     <para>
///         Both files describe the <em>installed</em> state. The generated state records the whole base build as
///         installed, plus every voice/tag language whose files are actually present on disk (the plugin keeps the
///         single default language and defers the rest). Each installed language is emitted as a
///         <c>&lt;BaseVerson&gt;</c> section in <c>config.xml</c> and a matching <c>&lt;BaseVersion&gt;</c> block in
///         <c>ResList.xml</c>, exactly as the official launcher leaves behind for a fresh install with one voice
///         selected: the client then plays that voice, shows every other language as available-for-download (with a
///         size) and pulls a chosen one on demand — instead of the broken state where the patcher sees local
///         version 0.0, believes the entire build is missing and loops on "更新失败".
///     </para>
///     <para>
///         The local <c>ResList.xml</c> is built from the same filtered file set the installer actually wrote to
///         disk (so it can never disagree with what is present), enriched with the per-file <c>&lt;Block&gt;</c>
///         checksums copied verbatim from the remote catalog for the large, block-verified files.
///     </para>
/// </remarks>
public static class PerfectWorldPatcherState
{
    /// <summary>Vendor spelling of the installed-tag section element is the misspelled "BaseVerson"; kept as-is.</summary>
    private const string BaseVersonElement = "BaseVerson";

    /// <summary>
    ///     A voice/tag language recorded as installed in the forged state. One is emitted per language whose files
    ///     the plugin actually keeps on disk (the default) — reproducing the <c>&lt;Res section .../&gt;</c> row the
    ///     official launcher writes under <c>config.xml</c>'s <c>&lt;BaseVerson&gt;</c> for that language.
    /// </summary>
    /// <param name="Tag">Language pak tag, e.g. <c>pakchunk101</c>.</param>
    /// <param name="Version">Full section version, e.g. <c>0.101.62</c>.</param>
    /// <param name="Section">First two dotted components of <paramref name="Version"/>, e.g. <c>0.101</c>.</param>
    /// <param name="ResSize">Sum of the section's installed file sizes in bytes.</param>
    /// <param name="ResCount">Number of installed files in the section.</param>
    public sealed record InstalledVoiceSection(string Tag, string Version, string Section, long ResSize, int ResCount);

    /// <summary>A voice/tag <c>&lt;BaseVersion&gt;</c> language parsed from the remote catalog and the set of files it owns.</summary>
    private sealed record CatalogVoiceSection(string Tag, string Version, string Section, HashSet<string> Filenames);

    /// <summary>
    ///     Builds the plaintext <c>config.xml</c> for the installed base build. Every language in
    ///     <paramref name="voiceSections"/> is recorded as installed via a <c>&lt;Res section version Tag ResSize
    ///     ResCount/&gt;</c> row inside <c>&lt;BaseVerson&gt;</c> (matching the official launcher, which writes one row
    ///     for the single voice language it ships); languages absent here are left unrecorded so the client offers them
    ///     for on-demand download. The <c>&lt;Hash&gt;</c> is <c>SHA1(ResVersion)[..6]</c> — the vendor's own
    ///     remote-config formula; it is not validated on read (verified by reverse engineering the patcher, and the
    ///     official local Hash already differs from the remote one), so a version-derived value is accepted and never
    ///     triggers a re-check.
    /// </summary>
    public static string BuildLocalConfigXml(string branch, PerfectWorldRemoteConfig remote, string tag, int resCount,
        IReadOnlyList<InstalledVoiceSection> voiceSections)
    {
        string resCountText = resCount.ToString(CultureInfo.InvariantCulture);
        string hash = Sha1Hex(remote.ResVersion)[..6];

        var sb = new StringBuilder(1024);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n");
        sb.Append("<config>\n");
        AppendLeafElement(sb, 1, "LocalBranch", branch);
        AppendLeafElement(sb, 1, "ResVersion", remote.ResVersion);
        AppendLeafElement(sb, 1, "AppVersion", "0.0");
        AppendLeafElement(sb, 1, "UpdateResVersion", "0.0");
        AppendLeafElement(sb, 1, "Tag", tag);
        AppendLeafElement(sb, 1, "ResSize", remote.ResSize.ToString(CultureInfo.InvariantCulture));
        AppendLeafElement(sb, 1, "ResCount", resCountText);
        // Installed-tag sections: one <Res> per voice/tag language actually present on disk, so the in-game client
        // plays that language and offers the remaining (deferred) ones for on-demand download. Empty when none.
        if (voiceSections.Count == 0)
        {
            sb.Append("    <").Append(BaseVersonElement).Append(" appVersion=\"0.0\">\n");
            sb.Append("    </").Append(BaseVersonElement).Append(">\n");
        }
        else
        {
            sb.Append("    <").Append(BaseVersonElement).Append(" appVersion=\"0.0\">\n");
            foreach (InstalledVoiceSection section in voiceSections)
            {
                sb.Append("        <Res section=\"").Append(Escape(section.Section))
                  .Append("\" version=\"").Append(Escape(section.Version))
                  .Append("\" Tag=\"").Append(Escape(section.Tag))
                  .Append("\" ResSize=\"").Append(section.ResSize.ToString(CultureInfo.InvariantCulture))
                  .Append("\" ResCount=\"").Append(section.ResCount.ToString(CultureInfo.InvariantCulture))
                  .Append("\" />\n");
            }
            sb.Append("    </").Append(BaseVersonElement).Append(">\n");
        }
        sb.Append("    <Extra>\n");
        AppendLeafElement(sb, 2, "diffHash", remote.DiffHash);
        AppendLeafElement(sb, 2, "listHash", remote.ListHash);
        sb.Append("        <BaseTag>\n");
        sb.Append("            <item name=\"").Append(Escape(tag)).Append("\" />\n");
        sb.Append("        </BaseTag>\n");
        AppendLeafElement(sb, 2, "speed", "50");
        sb.Append("    </Extra>\n");
        AppendLeafElement(sb, 1, "Hash", hash);
        AppendLeafElement(sb, 1, "ResCount", resCountText);
        sb.Append("</config>\n");
        return sb.ToString();
    }

    /// <summary>
    ///     Builds the plaintext form of the local <c>ResList.xml</c> (to be PatcherXML0-encoded before writing).
    ///     <paramref name="files"/> and <paramref name="paks"/> are the filtered install set actually written to disk,
    ///     so the emitted list matches on-disk content exactly. Files belonging to an installed voice/tag language are
    ///     grouped into a <c>&lt;BaseVersion&gt;</c> block per language (emitted first, matching the official layout);
    ///     every other file is a base <c>&lt;Res&gt;</c>. Large block-verified files gain their <c>&lt;Block&gt;</c>
    ///     children (and <c>blockSize</c>) copied from the remote catalog; per-file <c>timestamp</c>s come from the
    ///     on-disk modified time so the patcher's fast-path skip engages instead of re-hashing every file.
    /// </summary>
    /// <param name="files">Directly content-addressed installed files.</param>
    /// <param name="paks">Installed pak archives, whose packed entries are expanded to individual files on disk.</param>
    /// <param name="catalogXml">The decoded remote catalog (<c>ResList.bin</c>), source of the block checksums, root tag and voice sections.</param>
    /// <param name="resVersion">Fallback resource version if the catalog root lacks a <c>version</c> attribute.</param>
    /// <param name="installPath">Install root, used to read each file's on-disk modified time for its timestamp.</param>
    /// <param name="resCount">Receives the number of base <c>&lt;Res&gt;</c> entries written (excludes voice, matching the official count).</param>
    /// <param name="tag">Receives the base tag (the catalog root's <c>tag</c> attribute, e.g. <c>baseTag</c>).</param>
    /// <param name="voiceSections">Receives one descriptor per installed voice/tag language, to be recorded in <c>config.xml</c>.</param>
    /// <param name="clientXml">
    ///     Receives the plaintext flat client manifest (to be PatcherXML0-encoded to <c>tmp/client.xml</c>): the same
    ///     ordinal-sorted base <c>&lt;Res&gt;</c> set as the main list (voice/tag files excluded) under a
    ///     <c>&lt;ResList resversion=""&gt;</c> root, each row carrying the client-shape patch attributes. The vendor
    ///     patcher writes this alongside <c>config.xml</c>/<c>ResList.xml</c>; without it the in-game updater reads an
    ///     empty client list, believes nothing is installed and loops on "更新失败".
    /// </param>
    public static string BuildLocalResListXml(
        IReadOnlyList<PerfectWorldResEntry> files,
        IReadOnlyList<PerfectWorldPakEntry> paks,
        string catalogXml,
        string resVersion,
        string installPath,
        out int resCount,
        out string tag,
        out IReadOnlyList<InstalledVoiceSection> voiceSections,
        out string clientXml)
    {
        XElement? root = XDocument.Parse(catalogXml).Root;
        tag = (string?)root?.Attribute("tag") ?? "baseTag";
        string version = (string?)root?.Attribute("version") ?? resVersion;

        // filename (normalized to '/') -> catalog <Res> element carrying <Block> children / a blockSize attribute.
        var blockLookup = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        if (root != null)
        {
            foreach (XElement res in root.Descendants())
            {
                if (!string.Equals(res.Name.LocalName, "Res", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? fn = (string?)res.Attribute("filename");
                if (string.IsNullOrEmpty(fn))
                    continue;

                bool hasBlocks = res.Attribute("blockSize") != null || HasBlockChild(res);
                if (hasBlocks)
                    blockLookup[fn.Replace('\\', '/')] = res;
            }
        }

        // Map every voice/tag file (from the catalog's <BaseVersion> blocks) to its owning language section, so kept
        // files can be split out of the base list and grouped exactly as the official launcher records them.
        List<CatalogVoiceSection> catalogSections = ParseCatalogVoiceSections(root);
        var fileToSection = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < catalogSections.Count; i++)
            foreach (string fn in catalogSections[i].Filenames)
                fileToSection[fn] = i;

        var baseEntries = new List<(string Filename, string Md5, long Size)>();
        var sectionEntries = new List<(string Filename, string Md5, long Size)>[catalogSections.Count];
        for (int i = 0; i < sectionEntries.Length; i++)
            sectionEntries[i] = [];

        void Classify(string filename, string md5, long size)
        {
            if (fileToSection.TryGetValue(filename.Replace('\\', '/'), out int si))
                sectionEntries[si].Add((filename, md5, size));
            else
                baseEntries.Add((filename, md5, size));
        }

        foreach (PerfectWorldResEntry file in files)
            Classify(file.Filename, file.Md5, file.FileSize);
        foreach (PerfectWorldPakEntry pak in paks)
            foreach (PerfectWorldPakFile packed in pak.Files)
                Classify(packed.Filename, packed.Md5, packed.Size);

        // The vendor emits base rows in a single ordinal sort by filename (directly addressed and pak-expanded files
        // interleaved), so match that exact byte layout — both here and in the derived client manifest.
        baseEntries.Sort((a, b) => string.CompareOrdinal(a.Filename, b.Filename));

        var sb = new StringBuilder(128 * 1024);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n");
        sb.Append("<ResList version=\"").Append(Escape(version)).Append("\" tag=\"").Append(Escape(tag)).Append("\">\n");

        // Installed voice/tag languages first, one <BaseVersion> block each (only sections with kept files), ordered
        // by section for determinism. Files within a block are sorted ordinally by filename, matching the official file.
        var installed = new List<InstalledVoiceSection>();
        var orderedSections = new List<int>();
        for (int i = 0; i < catalogSections.Count; i++)
            if (sectionEntries[i].Count > 0)
                orderedSections.Add(i);
        orderedSections.Sort((a, b) => string.CompareOrdinal(catalogSections[a].Section, catalogSections[b].Section));

        foreach (int si in orderedSections)
        {
            CatalogVoiceSection section = catalogSections[si];
            List<(string Filename, string Md5, long Size)> entries = sectionEntries[si];
            entries.Sort((a, b) => string.CompareOrdinal(a.Filename, b.Filename));

            sb.Append("    <BaseVersion version=\"").Append(Escape(section.Version))
              .Append("\" tag=\"").Append(Escape(section.Tag)).Append("\">\n");
            sb.Append("        <ResList>\n");

            long sizeSum = 0;
            foreach ((string Filename, string Md5, long Size) e in entries)
            {
                blockLookup.TryGetValue(e.Filename.Replace('\\', '/'), out XElement? blockSource);
                AppendRes(sb, 3, e.Filename, e.Md5, e.Size, installPath, blockSource);
                sizeSum += e.Size;
            }

            sb.Append("        </ResList>\n");
            sb.Append("    </BaseVersion>\n");
            installed.Add(new InstalledVoiceSection(section.Tag, section.Version, section.Section, sizeSum, entries.Count));
        }

        int count = 0;
        foreach ((string Filename, string Md5, long Size) e in baseEntries)
        {
            blockLookup.TryGetValue(e.Filename.Replace('\\', '/'), out XElement? blockSource);
            AppendRes(sb, 1, e.Filename, e.Md5, e.Size, installPath, blockSource);
            count++;
        }

        sb.Append("</ResList>\n");

        // Derived flat client manifest (tmp/client.xml): identical ordinal-sorted base set, voice/tag files excluded,
        // each row in the client patch shape under a <ResList resversion=""> root.
        var clientSb = new StringBuilder(128 * 1024);
        clientSb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n");
        clientSb.Append("<ResList resversion=\"\">\n");
        foreach ((string Filename, string Md5, long Size) e in baseEntries)
        {
            blockLookup.TryGetValue(e.Filename.Replace('\\', '/'), out XElement? blockSource);
            AppendRes(clientSb, 1, e.Filename, e.Md5, e.Size, installPath, blockSource, version);
        }
        clientSb.Append("</ResList>\n");

        resCount = count;
        voiceSections = installed;
        clientXml = clientSb.ToString();
        return sb.ToString();
    }

    /// <summary>
    ///     Parses the remote catalog's per-language <c>&lt;BaseVersion&gt;</c> blocks into descriptors mapping each
    ///     language (tag/version/section) to the full set of files it owns — both the directly addressed
    ///     <c>&lt;Res&gt;</c> files and the packed <c>&lt;Entry&gt;</c> files inside its <c>&lt;Pak&gt;</c> archives,
    ///     since the installer expands those to individual files on disk.
    /// </summary>
    private static List<CatalogVoiceSection> ParseCatalogVoiceSections(XElement? root)
    {
        var sections = new List<CatalogVoiceSection>();
        if (root == null)
            return sections;

        foreach (XElement bv in root.Descendants())
        {
            if (!string.Equals(bv.Name.LocalName, "BaseVersion", StringComparison.OrdinalIgnoreCase))
                continue;

            string bvTag = (string?)bv.Attribute("tag") ?? "";
            string bvVersion = (string?)bv.Attribute("version") ?? "";
            var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (XElement el in bv.Descendants())
            {
                string local = el.Name.LocalName;
                string? name = string.Equals(local, "Res", StringComparison.OrdinalIgnoreCase) ? (string?)el.Attribute("filename")
                    : string.Equals(local, "Entry", StringComparison.OrdinalIgnoreCase) ? (string?)el.Attribute("name")
                    : null;
                if (!string.IsNullOrEmpty(name))
                    filenames.Add(name.Replace('\\', '/'));
            }

            sections.Add(new CatalogVoiceSection(bvTag, bvVersion, SectionFromVersion(bvVersion), filenames));
        }

        return sections;
    }

    /// <summary>Reduces a section version (e.g. <c>0.101.62</c>) to its first two dotted components (e.g. <c>0.101</c>).</summary>
    private static string SectionFromVersion(string version)
    {
        if (string.IsNullOrEmpty(version))
            return version;

        int firstDot = version.IndexOf('.');
        if (firstDot < 0)
            return version;

        int secondDot = version.IndexOf('.', firstDot + 1);
        return secondDot < 0 ? version : version[..secondDot];
    }

    /// <summary>
    ///     Appends a single <c>&lt;Res&gt;</c> row. When <paramref name="clientResVersion"/> is non-null the row is
    ///     emitted in the flat <c>client.xml</c> shape — the extra <c>ispatch/patchType/from/patchMD5/patchsize/ver</c>
    ///     attributes the vendor writes for a fully-installed (non-patch) file are inserted right after
    ///     <c>filesize</c> and before any <c>blockSize</c> — otherwise the plain <c>ResList.xml</c> shape is used. In
    ///     both shapes the large-file <c>&lt;Block&gt;</c> children (and <c>blockSize</c>) come from the remote catalog.
    /// </summary>
    private static void AppendRes(StringBuilder sb, int indentLevel, string filename, string md5, long fileSize,
        string installPath, XElement? blockSource, string? clientResVersion = null)
    {
        string indent = new string(' ', indentLevel * 4);
        sb.Append(indent).Append("<Res filename=\"").Append(Escape(filename))
          .Append("\" md5=\"").Append(Escape(md5)).Append('"');

        if (TryGetUnixModifiedTime(installPath, filename, out long timestamp))
            sb.Append(" timestamp=\"").Append(timestamp.ToString(CultureInfo.InvariantCulture)).Append('"');

        sb.Append(" filesize=\"").Append(fileSize.ToString(CultureInfo.InvariantCulture)).Append('"');

        // client.xml only: the vendor records every fully-installed file with empty/zero patch fields and the
        // resource version, so the in-game updater treats it as an up-to-date non-patch resource.
        if (clientResVersion != null)
            sb.Append(" ispatch=\"0\" patchType=\"0\" from=\"\" patchMD5=\"\" patchsize=\"0\" ver=\"")
              .Append(Escape(clientResVersion)).Append('"');

        string? blockSize = (string?)blockSource?.Attribute("blockSize");
        List<XElement>? blocks = CollectBlocks(blockSource);

        if (!string.IsNullOrEmpty(blockSize))
            sb.Append(" blockSize=\"").Append(Escape(blockSize)).Append('"');

        if (blocks is { Count: > 0 })
        {
            sb.Append(">\n");
            string blockIndent = new string(' ', (indentLevel + 1) * 4);
            foreach (XElement block in blocks)
            {
                sb.Append(blockIndent).Append("<Block");
                AppendAttrIfPresent(sb, block, "index");
                AppendAttrIfPresent(sb, block, "start");
                AppendAttrIfPresent(sb, block, "size");
                AppendAttrIfPresent(sb, block, "md5");
                sb.Append(" />\n");
            }
            sb.Append(indent).Append("</Res>\n");
        }
        else
        {
            sb.Append(" />\n");
        }
    }

    private static bool HasBlockChild(XElement res)
    {
        foreach (XElement child in res.Elements())
            if (string.Equals(child.Name.LocalName, "Block", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static List<XElement>? CollectBlocks(XElement? res)
    {
        if (res == null) return null;

        List<XElement>? blocks = null;
        foreach (XElement child in res.Elements())
        {
            if (!string.Equals(child.Name.LocalName, "Block", StringComparison.OrdinalIgnoreCase))
                continue;
            (blocks ??= []).Add(child);
        }
        return blocks;
    }

    private static void AppendAttrIfPresent(StringBuilder sb, XElement element, string attributeName)
    {
        string? value = (string?)element.Attribute(attributeName);
        if (value == null) return;
        sb.Append(' ').Append(attributeName).Append("=\"").Append(Escape(value)).Append('"');
    }

    private static void AppendLeafElement(StringBuilder sb, int indentLevels, string name, string value)
    {
        for (int i = 0; i < indentLevels; i++) sb.Append("    ");
        sb.Append('<').Append(name).Append('>').Append(Escape(value)).Append("</").Append(name).Append(">\n");
    }

    private static bool TryGetUnixModifiedTime(string installPath, string filename, out long unixSeconds)
    {
        unixSeconds = 0;
        try
        {
            string diskPath = Path.Combine(installPath, filename.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(diskPath)) return false;
            unixSeconds = new DateTimeOffset(File.GetLastWriteTimeUtc(diskPath)).ToUnixTimeSeconds();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Sha1Hex(string value)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.IndexOfAny(['&', '<', '>', '"']) < 0) return value;

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
