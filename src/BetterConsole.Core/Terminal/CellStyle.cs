namespace BetterConsole.Core.Terminal;

/// <summary>Text attributes of one terminal cell. Colours are ARGB; 0 means "terminal default".</summary>
public readonly record struct CellStyle(uint Fg, uint Bg, CellFlags Flags)
{
    public static readonly CellStyle Default = new(0, 0, CellFlags.None);
}

[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
}

/// <summary>Interns styles so every cell stores a 16-bit index instead of the whole struct.</summary>
internal sealed class StyleTable
{
    private readonly List<CellStyle> _styles = [CellStyle.Default];
    private readonly Dictionary<CellStyle, ushort> _index = new() { [CellStyle.Default] = 0 };

    public ushort Intern(CellStyle style)
    {
        if (_index.TryGetValue(style, out var id)) return id;
        if (_styles.Count >= ushort.MaxValue)
        {
            // Pathological output (every cell a new RGB colour). Fall back to the default style
            // instead of growing forever.
            return 0;
        }
        id = (ushort)_styles.Count;
        _styles.Add(style);
        _index[style] = id;
        return id;
    }

    public CellStyle this[ushort id] => _styles[id];
}

/// <summary>The 16 basic colours (Windows Terminal "Campbell" scheme), used for SGR 30-37/90-97 and 256-colour indices 0-15.</summary>
internal static class Palette
{
    private static readonly uint[] Basic =
    [
        0xFF0C0C0C, 0xFFC50F1F, 0xFF13A10E, 0xFFC19C00, 0xFF0037DA, 0xFF881798, 0xFF3A96DD, 0xFFCCCCCC,
        0xFF767676, 0xFFE74856, 0xFF16C60C, 0xFFF9F1A5, 0xFF3B78FF, 0xFFB4009E, 0xFF61D6D6, 0xFFF2F2F2,
    ];

    public static uint Color16(int index) => Basic[index & 15];

    public static uint Color256(int index)
    {
        index &= 255;
        if (index < 16) return Basic[index];
        if (index < 232)
        {
            index -= 16;
            int r = index / 36, g = index / 6 % 6, b = index % 6;
            static int Level(int v) => v == 0 ? 0 : 55 + v * 40;
            return Rgb(Level(r), Level(g), Level(b));
        }
        int gray = 8 + (index - 232) * 10;
        return Rgb(gray, gray, gray);
    }

    public static uint Rgb(int r, int g, int b) =>
        0xFF000000u | ((uint)(r & 255) << 16) | ((uint)(g & 255) << 8) | (uint)(b & 255);
}
