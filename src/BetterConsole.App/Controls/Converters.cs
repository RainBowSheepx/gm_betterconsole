using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BetterConsole.App.Controls;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ Invert;
}

/// <summary>Null or empty string / zero -> Collapsed.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool empty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            int i => i == 0,
            _ => false,
        };
        if (Invert) empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>"danger" / "accent" / "muted" -> a theme brush (badge colours).</summary>
public sealed class KindToBrushConverter : IValueConverter
{
    public string Suffix { get; set; } = "";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "danger" => "Danger",
            "accent" => "Accent",
            "warning" => "Warning",
            "success" => "Success",
            _ => Suffix == "Soft" ? "SurfaceActive" : "TextSecondary",
        };
        if (Suffix == "Soft" && key is "Danger" or "Accent" or "Warning" or "Success") key += "Soft";
        return Application.Current.TryFindResource("Brush." + key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Resource key -> brush (for colours chosen in view models).</summary>
public sealed class ResourceBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>First letter of a name, for avatar circles.</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = (value as string)?.Trim();
        if (string.IsNullOrEmpty(s)) return "?";
        var e = StringInfo.GetTextElementEnumerator(s);
        return e.MoveNext() ? e.GetTextElement().ToUpper(culture) : "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A stable colour per name (avatar circles).</summary>
public sealed class NameToColorConverter : IValueConverter
{
    private static readonly string[] Keys = ["Chart1", "Chart2", "Chart3", "Chart4", "Chart5", "Chart6"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        int h = 0;
        foreach (char c in s) h = h * 31 + c;
        var key = Keys[(h & 0x7fffffff) % Keys.Length];
        return Application.Current.TryFindResource("Brush." + key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Span (1-12) and available width -> width of a widget in a 12-column grid.</summary>
public sealed class SpanWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not int span || values[1] is not double width || width <= 0) return 300.0;
        // Narrow windows: everything full width.
        if (width < 700) return Math.Max(100, width - 1);
        // The card's right margin is the gutter; floor keeps a row from overflowing by a pixel.
        return Math.Max(100, Math.Floor(width * span / 12.0 - 0.5));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
