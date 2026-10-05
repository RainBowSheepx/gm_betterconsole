using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BetterConsole.App.Themes;

/// <summary>Applies a <see cref="ThemePalette"/> to the application resources and window title bars.</summary>
public static class ThemeManager
{
    public static ThemePalette Current { get; private set; } = ThemePalette.Dark;

    public static event Action<ThemePalette>? Changed;

    /// <summary>Built-in themes followed by the JSON themes found in <paramref name="folder"/>.</summary>
    public static List<ThemePalette> Discover(string folder)
    {
        var list = new List<ThemePalette>(ThemePalette.BuiltIn);
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var p = ThemePalette.Load(file);
                if (p != null && list.All(t => !t.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) list.Add(p);
            }
        }
        return list;
    }

    public static void Apply(ThemePalette palette)
    {
        var res = Application.Current.Resources;
        foreach (var prop in ThemePalette.ColorProperties())
        {
            var value = prop.GetValue(palette) as string;
            if (!TryParse(value, out var color)) continue;
            res["Color." + prop.Name] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res["Brush." + prop.Name] = brush;
        }
        Current = palette;
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke(palette);
    }

    public static bool TryParse(string? s, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(s.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Color GetColor(string name) =>
        Application.Current.Resources["Color." + name] is Color c ? c : Colors.Magenta;

    public static Brush GetBrush(string name) =>
        Application.Current.Resources["Brush." + name] as Brush ?? Brushes.Magenta;

    // ---- native title bar --------------------------------------------------------------------
    // Windows 11 lets an app colour its caption; Windows 10 (1809+) only knows dark / light.

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = Current.IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, 4);
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            int caption = ToColorRef(GetColor("Panel"));
            int text = ToColorRef(GetColor("Text"));
            int border = ToColorRef(GetColor("Border"));
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, 4);
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, 4);
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, 4);
        }
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    // ---- console colours ---------------------------------------------------------------------

    private static readonly Dictionary<(uint, Color), Color> ContrastCache = new();

    /// <summary>
    /// srcds colours are picked for a black console. On a light background (or any background they
    /// do not stand out from) they are darkened or lightened until the text is readable, keeping the hue.
    /// </summary>
    public static Color Readable(uint argb, Color background)
    {
        var key = (argb, background);
        if (ContrastCache.TryGetValue(key, out var cached)) return cached;
        var c = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        double bgL = Luminance(background);
        bool darkBg = bgL < 0.5;
        int guard = 0;
        while (Contrast(Luminance(c), bgL) < 3.2 && guard++ < 20)
        {
            c = darkBg ? Mix(c, Colors.White, 0.15) : Mix(c, Colors.Black, 0.15);
        }
        if (ContrastCache.Count > 4096) ContrastCache.Clear();
        ContrastCache[key] = c;
        return c;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static double Contrast(double l1, double l2) => (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
}
