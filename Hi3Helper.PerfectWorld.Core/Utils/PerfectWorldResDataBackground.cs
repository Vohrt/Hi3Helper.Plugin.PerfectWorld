using System;
using System.IO;

namespace Hi3Helper.PerfectWorld.Core.Utils;

/// <summary>
///     Extracts the launcher background image from a Perfect World proprietary <c>qres</c> resource pack
///     (e.g. P5X's <c>P5XLaunch\ResData\1264.dat</c>).
///     <para>
///         Some pw_sdk launchers (P5X) do not publish a dynamic <c>bgimgs/</c> folder on the launcher CDN the
///         way 异环 (NTE) does; instead the home-screen key-visual lives inside the vendor's packed
///         <c>qres</c> resource file that ships with the install. Inside that pack the images are stored as
///         <b>raw, uncompressed PNG blobs</b> (PNG is already deflate-compressed, so the pack does not
///         re-compress it). This lets us recover the background without implementing the proprietary index
///         format: we scan for raw PNG signatures, read each image's dimensions straight from its IHDR, and
///         pick the one that matches the launcher key-visual profile (a large landscape image; the launcher's
///         UI atlases are all much narrower).
///     </para>
///     <para>
///         The routine is fully managed and NativeAOT-safe (no image decoder, no reflection): it copies the
///         chosen PNG's bytes verbatim into the media cache and returns its absolute path. Results are cached
///         and only re-extracted when the source pack changes (size / last-write-time), so the 30&#8211;MB scan
///         runs at most once per launcher update.
///     </para>
/// </summary>
public static class PerfectWorldResDataBackground
{
    // Raw PNG signature (8 bytes) and the trailing IEND chunk (type + CRC), used to bound each embedded blob.
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IendMarker   = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    // Selection profile for the launcher key-visual. The observed P5X UI atlases are all <= 1103px wide and
    // are square/portrait; the key-visual is a wide landscape image (2000x1247). Requiring a wide landscape
    // image cleanly isolates the background from every UI asset. Among qualifying images we keep the largest
    // by byte length (the photographic key-visual is far larger than same-size mask/variant PNGs).
    private const int MinBackgroundWidth = 1600;

    // Guard against dimensions read from an accidental signature match producing an absurd allocation bound.
    private const int MaxReasonableDimension = 20000;

    /// <summary>
    ///     Extracts the launcher background PNG from a <c>qres</c> pack, writing it to <paramref name="cacheDir"/>
    ///     and returning its absolute path, or <see langword="null"/> if the pack is missing/unreadable or holds
    ///     no matching image. The extraction is cached by the pack's size + last-write-time.
    /// </summary>
    /// <param name="datPath">Absolute path to the vendor <c>qres</c> pack (e.g. <c>...\ResData\1264.dat</c>).</param>
    /// <param name="cacheDir">Directory to write the extracted PNG into (created if needed).</param>
    /// <param name="cacheBaseName">Base file name (without extension) for the cached PNG + its meta sidecar.</param>
    public static string? TryExtractBackground(string datPath, string cacheDir, string cacheBaseName)
    {
        if (string.IsNullOrEmpty(datPath) || !File.Exists(datPath))
        {
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(datPath);
        }
        catch
        {
            return null;
        }

        string outPng    = Path.Combine(cacheDir, cacheBaseName + ".png");
        string outMeta   = Path.Combine(cacheDir, cacheBaseName + ".meta");
        string signature = info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                           info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Cache hit: an already-extracted PNG whose meta matches the current pack signature.
        if (File.Exists(outPng) && File.Exists(outMeta))
        {
            try
            {
                if (string.Equals(File.ReadAllText(outMeta), signature, StringComparison.Ordinal))
                {
                    return outPng;
                }
            }
            catch
            {
                // Fall through and re-extract on any cache-read failure.
            }
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(datPath);
        }
        catch
        {
            return null;
        }

        if (!TryFindBackgroundPng(data, out int start, out int length))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(cacheDir);
            using (var fs = new FileStream(outPng, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                fs.Write(data, start, length);
            }

            File.WriteAllText(outMeta, signature);
        }
        catch
        {
            return null;
        }

        return outPng;
    }

    /// <summary>
    ///     Scans <paramref name="data"/> for raw PNG blobs and returns the byte range of the one that best
    ///     matches the launcher key-visual (largest wide-landscape image).
    /// </summary>
    private static bool TryFindBackgroundPng(byte[] data, out int bestStart, out int bestLength)
    {
        bestStart  = -1;
        bestLength = 0;

        long bestScore = -1;
        int  n         = data.Length;
        int  i         = 0;

        while (i <= n - 8)
        {
            if (!IsMatch(data, i, PngSignature))
            {
                i++;
                continue;
            }

            // IHDR layout for a raw PNG: [8 sig][4 len][4 "IHDR"][4 width][4 height]... -> width @ +16, height @ +20.
            if (i + 24 > n)
            {
                break;
            }

            int width  = ReadBigEndianInt32(data, i + 16);
            int height = ReadBigEndianInt32(data, i + 20);

            int iendPos = IndexOf(data, IendMarker, i + 8);
            if (iendPos < 0)
            {
                // No terminator ahead: no further complete PNG can exist.
                break;
            }

            int pngEnd = iendPos + IendMarker.Length; // exclusive
            int pngLen = pngEnd - i;

            bool isLandscape = width > height;
            bool isSaneSize  = width is > 0 and < MaxReasonableDimension &&
                               height is > 0 and < MaxReasonableDimension;

            if (isSaneSize && isLandscape && width >= MinBackgroundWidth && pngLen > bestScore)
            {
                bestScore  = pngLen;
                bestStart  = i;
                bestLength = pngLen;
            }

            // Resume scanning after this PNG (blobs never overlap).
            i = pngEnd;
        }

        return bestStart >= 0;
    }

    private static int ReadBigEndianInt32(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }

    private static bool IsMatch(byte[] data, int offset, byte[] pattern)
    {
        if (offset + pattern.Length > data.Length)
        {
            return false;
        }

        for (int k = 0; k < pattern.Length; k++)
        {
            if (data[offset + k] != pattern[k])
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(byte[] data, byte[] pattern, int start)
    {
        byte first = pattern[0];
        int  limit = data.Length - pattern.Length;

        for (int p = start; p <= limit; p++)
        {
            if (data[p] != first)
            {
                continue;
            }

            int k = 1;
            for (; k < pattern.Length; k++)
            {
                if (data[p + k] != pattern[k])
                {
                    break;
                }
            }

            if (k == pattern.Length)
            {
                return p;
            }
        }

        return -1;
    }
}
