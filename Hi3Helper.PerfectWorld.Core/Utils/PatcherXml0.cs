using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Hi3Helper.PerfectWorld.Core.Utils;

/// <summary>
///     Codec for Perfect World <c>pw_sdk</c> / <c>PatcherSDK</c> "PatcherXML0" encrypted manifests
///     (e.g. <c>ResList.bin</c>, <c>lastdiff.bin</c>, <c>client.xml</c>). Decodes vendor-authored blobs and
///     (via <see cref="EncodeFromXml"/>) re-encodes the local <c>ResList.xml</c> patcher-state file the plugin
///     forges after a direct download so the native patcher accepts the install.
/// </summary>
/// <remarks>
///     <para>Container layout:</para>
///     <list type="bullet">
///         <item><description><c>"PatcherXML0\0"</c> magic (12 bytes)</description></item>
///         <item><description>decompressed size as <see cref="uint"/> little-endian (4 bytes)</description></item>
///         <item><description>ciphertext (length is always a multiple of 16)</description></item>
///     </list>
///     <para>ciphertext = <c>AES-128-CBC( zlib.deflate(xml) + PKCS7 )</c>. Decode = AES-CBC decrypt → strip
///     PKCS7 → zlib inflate → UTF-8 XML.</para>
///     <para>
///         Key/IV were recovered by reverse engineering <c>PatcherSDK_x64.dll</c>:
///         <c>key = (appId + "@Patcher")</c> right-padded with <c>'0'</c> (0x30) to 16 bytes, and
///         <c>iv  = "PatcherSDK"</c> right-padded with <c>'0'</c> to 16 bytes. The key is therefore a general
///         formula for any pw_sdk title; only the numeric <c>appId</c> differs (NTE / 异环 = <c>1289</c>).
///     </para>
/// </remarks>
public static class PatcherXml0
{
    private const string Magic = "PatcherXML0";
    private const int HeaderLength = 16; // 12 (magic + NUL) + 4 (uint32 size)

    private static readonly byte[] IvBytes = BuildPadded16("PatcherSDK");

    /// <summary>
    ///     Builds the AES-128 key for the given numeric application id.
    /// </summary>
    public static byte[] BuildKey(string appId)
    {
        if (string.IsNullOrEmpty(appId))
            throw new ArgumentException("appId must not be null or empty.", nameof(appId));

        return BuildPadded16(appId + "@Patcher");
    }

    /// <summary>
    ///     Reads and decodes a PatcherXML0 file from disk into a UTF-8 XML string.
    /// </summary>
    public static string DecodeFileToXml(string filePath, string appId)
    {
        return DecodeToXml(File.ReadAllBytes(filePath), appId);
    }

    /// <summary>
    ///     Encodes a UTF-8 XML string into a PatcherXML0 container. This is the exact inverse of
    ///     <see cref="DecodeToXml"/> and is used to write the local <c>ResList.xml</c> patcher-state file the
    ///     native pw_sdk PatcherSDK reads on game launch (which the plugin must forge because it downloads the
    ///     game directly, bypassing the vendor patcher that would normally author it).
    /// </summary>
    public static byte[] EncodeFromXml(string xml, string appId)
    {
        if (xml == null) throw new ArgumentNullException(nameof(xml));
        return EncodeFromBytes(Encoding.UTF8.GetBytes(xml), appId);
    }

    /// <summary>
    ///     Encodes raw payload bytes into a PatcherXML0 container: <c>zlib.deflate(payload)</c> →
    ///     <c>AES-128-CBC</c> encrypt (PKCS7) → prepend the 16-byte header. The stored inflated-size field is the
    ///     length of <paramref name="payload"/>, so a subsequent <see cref="DecodeToBytes"/> validates cleanly.
    /// </summary>
    public static byte[] EncodeFromBytes(byte[] payload, string appId)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));

        byte[] deflated = Deflate(payload);
        byte[] cipher = EncryptAes(deflated, BuildKey(appId));

        var result = new byte[HeaderLength + cipher.Length];
        // 12-byte magic ("PatcherXML0" + NUL) then bytes 12..15 = inflated size (uint32 LE); the rest is ciphertext.
        Encoding.ASCII.GetBytes(Magic, 0, Magic.Length, result, 0);
        result[Magic.Length] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)payload.Length);
        Buffer.BlockCopy(cipher, 0, result, HeaderLength, cipher.Length);
        return result;
    }

    /// <summary>
    ///     Decodes an in-memory PatcherXML0 blob into a UTF-8 XML string.
    /// </summary>
    public static string DecodeToXml(byte[] data, string appId)
    {
        return Encoding.UTF8.GetString(DecodeToBytes(data, appId));
    }

    /// <summary>
    ///     Decodes an in-memory PatcherXML0 blob into the raw (inflated) payload bytes.
    /// </summary>
    public static byte[] DecodeToBytes(byte[] data, string appId)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length < HeaderLength)
            throw new InvalidDataException("Data is too small to be a PatcherXML0 container.");

        if (Encoding.ASCII.GetString(data, 0, Magic.Length) != Magic)
            throw new InvalidDataException("Missing PatcherXML0 magic header.");

        // Bytes 12..15 hold the expected inflated size (uint32 LE). Used only for validation.
        uint expectedInflatedSize = BitConverter.ToUInt32(data, 12);

        int cipherLength = data.Length - HeaderLength;
        if (cipherLength <= 0 || cipherLength % 16 != 0)
            throw new InvalidDataException($"Invalid PatcherXML0 ciphertext length: {cipherLength}.");

        byte[] deflated = DecryptAes(data, HeaderLength, cipherLength, BuildKey(appId));
        byte[] inflated = Inflate(deflated);

        if (expectedInflatedSize != 0 && inflated.Length != expectedInflatedSize)
            throw new InvalidDataException(
                $"PatcherXML0 inflated size mismatch. Expected {expectedInflatedSize}, got {inflated.Length}.");

        return inflated;
    }

    private static byte[] DecryptAes(byte[] buffer, int offset, int length, byte[] key)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key;
        aes.IV = IvBytes;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(buffer, offset, length);
    }

    private static byte[] EncryptAes(byte[] plaintext, byte[] key)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key;
        aes.IV = IvBytes;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private static byte[] Inflate(byte[] zlibData)
    {
        using var input = new MemoryStream(zlibData, false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        // Dispose the ZLibStream (via the using scope) before ToArray so its final block + Adler-32 trailer are
        // flushed; leaveOpen keeps the backing MemoryStream readable afterwards.
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static byte[] BuildPadded16(string value)
    {
        // Right-pad (or truncate) an ASCII string to exactly 16 bytes using '0' (0x30) as the pad byte.
        var result = new byte[16];
        for (var i = 0; i < 16; i++)
            result[i] = i < value.Length ? (byte)value[i] : (byte)'0';
        return result;
    }
}
