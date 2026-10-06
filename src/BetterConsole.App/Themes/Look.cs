using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace BetterConsole.App.Themes;

/// <summary>
/// Compact mode: smaller controls, square corners, no shadows, animations or avatars, and one plain palette
/// (dark or light like Windows' apps) instead of the themes, for the least work for the CPU and the
/// graphics card (a VPS often has none and draws everything in software). Only the look changes: every
/// feature stays. The sizes are application resources that the styles use through DynamicResource
/// (Themes/Controls.xaml has the normal values), so the switch is immediate.
/// </summary>
public static class Look
{
    public static bool Compact { get; private set; }

    /// <summary>Raised after the switch (views that draw themselves redraw).</summary>
    public static event Action? Changed;

    /// <summary>Applies the mode; <paramref name="theme"/> is the palette of the normal mode.</summary>
    public static void Apply(bool compact, ThemePalette theme)
    {
        bool changed = Compact != compact;
        Compact = compact;
        var r = Application.Current.Resources;
        r["Radius.Small"] = new CornerRadius(compact ? 0 : 4);
        r["Radius"] = new CornerRadius(compact ? 0 : 6);
        r["Radius.Large"] = new CornerRadius(compact ? 0 : 10);
        r["Radius.Pill"] = new CornerRadius(compact ? 2 : 9);
        r["Size.Header"] = compact ? 34.0 : 46.0;
        r["Size.HeaderButton"] = compact ? 28.0 : 36.0;
        r["Size.StatusBar"] = compact ? 22.0 : 30.0;
        r["Size.Control"] = compact ? 24.0 : 30.0;
        r["Size.ControlSmall"] = compact ? 22.0 : 28.0;
        r["Size.Row"] = compact ? 22.0 : 30.0;
        r["Size.Chart"] = compact ? 170.0 : 210.0;
        r["Size.KpiWidth"] = compact ? 120.0 : 170.0;
        r["Font.KpiSize"] = compact ? 16.0 : 22.0;
        r["Font.Size"] = compact ? 12.0 : 13.0;
        r["Pad.Page"] = compact ? new Thickness(6, 4, 6, 6) : new Thickness(12, 8, 12, 10);
        r["Pad.PageScroll"] = compact ? new Thickness(6, 4, 0, 6) : new Thickness(12, 8, 2, 10);
        r["Pad.ConsoleInput"] = compact ? new Thickness(6, 3, 6, 3) : new Thickness(6, 8, 6, 8);
        r["Pad.Card"] = compact ? new Thickness(8, 6, 8, 6) : new Thickness(14, 12, 14, 12);
        r["Pad.Kpi"] = compact ? new Thickness(8, 4, 8, 5) : new Thickness(14, 10, 14, 10);
        r["Pad.Button"] = compact ? new Thickness(8, 2, 8, 2) : new Thickness(12, 5, 12, 5);
        r["Pad.Ghost"] = compact ? new Thickness(6, 2, 6, 2) : new Thickness(8, 4, 8, 4);
        r["Pad.Input"] = compact ? new Thickness(6, 3, 6, 3) : new Thickness(8, 7, 8, 7);
        r["Pad.Menu"] = compact ? new Thickness(6, 3, 6, 3) : new Thickness(8, 6, 8, 6);
        r["Pad.Header"] = compact ? new Thickness(8, 4, 8, 4) : new Thickness(10, 8, 10, 8);
        r["Pad.Tab"] = compact ? new Thickness(9, 0, 9, 0) : new Thickness(12, 0, 12, 0);
        // A shadow is drawn in software for every frame its element changes in: the costliest part of the look.
        r["Effect.Shadow"] = compact ? null : Shadow(16, 3, 0.35);
        r["Effect.ShadowLarge"] = compact ? null : Shadow(24, 0, 0.45);
        r["Popup.Animation"] = compact ? PopupAnimation.None : PopupAnimation.Fade;
        r["Visibility.NotCompact"] = compact ? Visibility.Collapsed : Visibility.Visible;
        ThemeManager.Apply(compact ? (SystemUsesLightTheme() ? ThemePalette.CompactLight : ThemePalette.Compact) : theme);
        if (changed) Changed?.Invoke();
    }

    private static DropShadowEffect Shadow(double blur, double depth, double opacity)
    {
        var e = new DropShadowEffect { BlurRadius = blur, ShadowDepth = depth, Opacity = opacity };
        e.Freeze();
        return e;
    }

    /// <summary>Windows' "app mode" (Settings → Personalization → Colors).</summary>
    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }
}
