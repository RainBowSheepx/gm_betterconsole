using System.Text;

namespace BetterConsole.Core.Terminal;

/// <summary>
/// Undoes the console code page. srcds writes UTF-8 bytes with WriteFile, the console decodes them
/// with its single-byte code page (BetterConsole sets 437), so "Привет" reaches us as six pairs of
/// box-drawing characters. Every character of that code page maps back to exactly one byte, which
/// lets us recover the original bytes and decode them as UTF-8.
/// </summary>
internal sealed class OemTextRestorer
{
    private readonly Dictionary<char, byte> _toByte = new();
    private readonly char[] _fallback = new char[256];

    static OemTextRestorer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public OemTextRestorer(int codePage = 437)
    {
        var oem = Encoding.GetEncoding(codePage);
        var one = new byte[1];
        for (int b = 0x80; b < 256; b++)
        {
            one[0] = (byte)b;
            var s = oem.GetString(one);
            if (s.Length == 1) _toByte.TryAdd(s[0], (byte)b);
        }
        // Control bytes are drawn as these glyphs by the console when they are not processed.
        const string glyphs = "\0☺☻♥♦♣♠•◘○◙♂♀♪♫☼►◄↕‼¶§▬↨↑↓→←∟↔▲▼";
        for (int b = 1; b < glyphs.Length; b++) _toByte.TryAdd(glyphs[b], (byte)b);
        _toByte.TryAdd('⌂', 0x7F);

        // Bytes that are not valid UTF-8 are shown in the system ANSI code page (cp1251 on a Russian
        // Windows), which is what old addons that are not saved as UTF-8 usually contain.
        Encoding ansi;
        try
        {
            ansi = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            if (!ansi.IsSingleByte) ansi = Encoding.Latin1;
        }
        catch
        {
            ansi = Encoding.Latin1;
        }
        for (int b = 0; b < 256; b++)
        {
            one[0] = (byte)b;
            var s = ansi.GetString(one);
            _fallback[b] = b < 0x80 ? (char)b : s.Length == 1 ? s[0] : '?';
        }
    }

    /// <summary>
    /// Converts screen cells to text. <paramref name="styles"/> runs parallel to <paramref name="cells"/>;
    /// the style of a decoded character is the style of its first byte. Appends to the outputs.
    /// </summary>
    public void Restore(ReadOnlySpan<char> cells, ReadOnlySpan<ushort> styles, StringBuilder text, List<ushort> textStyles)
    {
        int n = cells.Length;
        int i = 0;
        while (i < n)
        {
            char c = cells[i];
            if (c < 0x80 || !_toByte.TryGetValue(c, out byte lead))
            {
                // ASCII, or a character that did not come from the code page (already proper Unicode).
                text.Append(c);
                textStyles.Add(styles[i]);
                i++;
                continue;
            }

            int need = lead switch
            {
                >= 0xC2 and <= 0xDF => 1,
                >= 0xE0 and <= 0xEF => 2,
                >= 0xF0 and <= 0xF4 => 3,
                _ => -1,
            };
            int cp = need switch { 1 => lead & 0x1F, 2 => lead & 0x0F, 3 => lead & 0x07, _ => 0 };
            bool ok = need > 0 && i + need < n;
            if (ok)
            {
                for (int k = 1; k <= need; k++)
                {
                    if (!_toByte.TryGetValue(cells[i + k], out byte cont) || (cont & 0xC0) != 0x80)
                    {
                        ok = false;
                        break;
                    }
                    cp = (cp << 6) | (cont & 0x3F);
                }
            }
            if (ok)
            {
                // Reject overlong forms and surrogates.
                ok = need switch
                {
                    1 => cp >= 0x80,
                    2 => cp >= 0x800 && (cp < 0xD800 || cp > 0xDFFF),
                    _ => cp >= 0x10000 && cp <= 0x10FFFF,
                };
            }
            if (!ok)
            {
                text.Append(_fallback[lead]);
                textStyles.Add(styles[i]);
                i++;
                continue;
            }

            ushort style = styles[i];
            if (cp >= 0x10000)
            {
                cp -= 0x10000;
                text.Append((char)(0xD800 + (cp >> 10)));
                text.Append((char)(0xDC00 + (cp & 0x3FF)));
                textStyles.Add(style);
                textStyles.Add(style);
            }
            else
            {
                text.Append((char)cp);
                textStyles.Add(style);
            }
            i += need + 1;
        }
    }
}
