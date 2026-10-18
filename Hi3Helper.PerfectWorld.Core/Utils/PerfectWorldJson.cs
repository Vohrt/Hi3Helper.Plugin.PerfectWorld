using System;
using System.Globalization;
using System.Text;

namespace Hi3Helper.PerfectWorld.Core.Utils;

/// <summary>
///     Minimal, reflection-free reader for the few flat string properties the pw_sdk vendor configs expose: the
///     on-disk <c>PatcherConfig.json</c> and the base64 <c>&lt;ResConfig&gt;</c> blob embedded in <c>config.xml</c>.
/// </summary>
/// <remarks>
///     Hand-rolled instead of <c>System.Text.Json</c> on purpose: the plugins ship ReflectionFree NativeAOT
///     profiles where reflection-based (de)serialisation is unavailable, and the only values ever needed are single,
///     unescaped strings such as <c>"branchName":"PC_140"</c>.
/// </remarks>
internal static class PerfectWorldJson
{
    /// <summary>
    ///     Returns the value of the first <paramref name="propertyName"/> string property in <paramref name="json"/>,
    ///     or <see langword="null"/> when it is missing or not a string value.
    /// </summary>
    public static string? TryGetString(string? json, string propertyName)
    {
        if (string.IsNullOrEmpty(json)) return null;

        string needle = "\"" + propertyName + "\"";
        int index = 0;
        while (true)
        {
            index = json.IndexOf(needle, index, StringComparison.Ordinal);
            if (index < 0) return null;

            int cursor = index + needle.Length;
            SkipWhitespace(json, ref cursor);
            if (cursor >= json.Length || json[cursor] != ':')
            {
                index += needle.Length;
                continue;
            }

            cursor++; // ':'
            SkipWhitespace(json, ref cursor);
            if (cursor >= json.Length || json[cursor] != '"')
            {
                index += needle.Length;
                continue;
            }

            return ReadQuoted(json, ref cursor);
        }
    }

    /// <summary>
    ///     Decodes a base64 vendor blob (UTF-8 JSON) and returns the requested string property.
    /// </summary>
    public static string? TryGetStringFromBase64(string? base64, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;

        try
        {
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            return TryGetString(json, propertyName);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void SkipWhitespace(string json, ref int index)
    {
        while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
    }

    private static string ReadQuoted(string json, ref int index)
    {
        var sb = new StringBuilder(32);
        index++; // opening quote
        while (index < json.Length)
        {
            char c = json[index++];
            switch (c)
            {
                case '"':
                    return sb.ToString();
                case '\\' when index < json.Length:
                    char escaped = json[index++];
                    sb.Append(escaped switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'b' => '\b',
                        'f' => '\f',
                        'u' => ReadUnicodeEscape(json, ref index),
                        _   => escaped
                    });
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        // Unterminated string: hand back what was read rather than failing the whole config parse.
        return sb.ToString();
    }

    private static char ReadUnicodeEscape(string json, ref int index)
    {
        if (index + 4 > json.Length) return '\0';

        string hex = json.Substring(index, 4);
        index += 4;
        return ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code)
            ? (char)code
            : '\0';
    }
}
