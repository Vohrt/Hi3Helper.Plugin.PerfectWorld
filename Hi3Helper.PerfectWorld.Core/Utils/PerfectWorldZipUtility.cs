using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Hi3Helper.PerfectWorld.Core.Utils;

/// <summary>
///     Small helper for reading the single-entry <c>*.zip</c> blobs served by the pw_sdk launcher CDN.
///     Each launcher asset (e.g. everything under <c>bgimgs/</c>) is stored zip-compressed and wraps
///     exactly one file.
/// </summary>
internal static class PerfectWorldZipUtility
{
    /// <summary>
    ///     Extracts one entry from an in-memory zip. When <paramref name="entryName"/> is supplied the
    ///     matching entry (compared by file name, case-insensitive) is returned; otherwise the first
    ///     entry in the archive is used.
    /// </summary>
    internal static byte[] ExtractSingleEntry(byte[] zipBytes, string? entryName = null)
    {
        using var ms = new MemoryStream(zipBytes, false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        ZipArchiveEntry? entry = entryName is null
            ? archive.Entries.FirstOrDefault()
            : archive.Entries.FirstOrDefault(e =>
                  string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase))
              ?? archive.Entries.FirstOrDefault();

        if (entry is null)
        {
            throw new InvalidDataException("Zip archive contains no entries.");
        }

        using Stream entryStream = entry.Open();
        using var output = new MemoryStream();
        entryStream.CopyTo(output);
        return output.ToArray();
    }
}
