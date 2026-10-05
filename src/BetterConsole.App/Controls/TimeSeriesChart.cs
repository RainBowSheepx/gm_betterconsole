using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BetterConsole.App.Themes;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Controls;

/// <summary>
/// A light-weight line chart for one-second samples: grid, legend, optional reference line (the tick
/// budget), filled first series, hover read-out. Drawn with a few geometries per frame, so a dozen
/// charts redrawn every second cost nothing noticeable.
/// </summary>
public sealed class TimeSeriesChart : FrameworkElement
{
    private sealed record Line(TimeSeries Data, string BrushKey, Brush? Brush, string Name);

    private readonly List<Line> _lines = new();
    private Point? _mouse;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(nameof(WindowSeconds), typeof(double), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinimumMaxProperty = DependencyProperty.Register(nameof(MinimumMax), typeof(double), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReferenceProperty = DependencyProperty.Register(nameof(Reference), typeof(double), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReferenceLabelProperty = DependencyProperty.Register(nameof(ReferenceLabel), typeof(string), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public double MinimumMax { get => (double)GetValue(MinimumMaxProperty); set => SetValue(MinimumMaxProperty, value); }
    public double Reference { get => (double)GetValue(ReferenceProperty); set => SetValue(ReferenceProperty, value); }
    public string ReferenceLabel { get => (string)GetValue(ReferenceLabelProperty); set => SetValue(ReferenceLabelProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }

    public TimeSeriesChart()
    {
        ClipToBounds = true;
        MinHeight = 150;
        ThemeManager.Changed += _ => InvalidateVisual();
    }

    /// <summary>Adds a series; colour by theme key ("Chart1".."Chart6") or a fixed brush.</summary>
    public TimeSeriesChart Add(TimeSeries data, string name, string brushKey = "Chart1", Brush? brush = null)
    {
        _lines.Add(new Line(data, brushKey, brush, name));
        InvalidateVisual();
        return this;
    }

    public void ClearSeries()
    {
        _lines.Clear();
        InvalidateVisual();
    }

    public void Refresh()
    {
        if (IsVisible) InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mouse = null;
        InvalidateVisual();
    }

    private double Dip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)Application.Current.Resources["Font.Ui"], FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, Dip);

    private static double NiceMax(double v)
    {
        if (v <= 0 || double.IsNaN(v) || double.IsInfinity(v)) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / exp;
        double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return nice * exp;
    }

    private string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F" + Math.Max(0, Decimals), CultureInfo.CurrentCulture);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 60) return;
        var bgHit = Brushes.Transparent;
        dc.DrawRectangle(bgHit, null, new Rect(0, 0, w, h));

        var textBrush = ThemeManager.GetBrush("Text");
        var muted = ThemeManager.GetBrush("TextMuted");
        var gridPen = new Pen(ThemeManager.GetBrush("ChartGrid"), 1);
        gridPen.Freeze();

        // header: title + legend with the latest values
        var title = Text(Title, 12.5, textBrush, bold: true);
        dc.DrawText(title, new Point(2, 0));
        double lx = title.Width + 16;
        foreach (var l in _lines)
        {
            var brush = l.Brush ?? ThemeManager.GetBrush(l.BrushKey);
            var label = Text($"{l.Name} {Fmt(l.Data.LastValue)}{(string.IsNullOrEmpty(Unit) ? "" : " " + Unit)}", 11.5, muted);
            if (lx + label.Width + 14 > w) break;
            dc.DrawRoundedRectangle(brush, null, new Rect(lx, 5, 9, 9), 2, 2);
            dc.DrawText(label, new Point(lx + 13, 1));
            lx += label.Width + 28;
        }

        double now = StatsVm.Now();
        double win = Math.Max(10, WindowSeconds);
        double from = now - win;
        double max = MinimumMax;
        foreach (var l in _lines)
        {
            var (_, mx) = l.Data.Range(from);
            if (!double.IsInfinity(mx) && mx > max) max = mx;
        }
        if (!double.IsNaN(Reference) && Reference * 1.1 > max) max = Reference * 1.1;
        max = NiceMax(max * 1.08);

        const double top = 24, bottom = 18;
        double left = 6, right = w - 4;
        // y labels on the left, measure the widest
        var maxLabel = Text(Fmt(max), 10.5, muted);
        left = Math.Max(left, maxLabel.Width + 10);
        double plotH = h - top - bottom, plotW = right - left;
        if (plotH < 20 || plotW < 20) return;

        for (int i = 0; i <= 4; i++)
        {
            double y = top + plotH * i / 4.0;
            dc.DrawLine(gridPen, new Point(left, Math.Round(y) + 0.5), new Point(right, Math.Round(y) + 0.5));
            var v = max * (4 - i) / 4.0;
            var t = Text(Fmt(v), 10.5, muted);
            dc.DrawText(t, new Point(left - t.Width - 6, y - t.Height / 2));
        }
        // x labels
        var lt = Text(win >= 3600 ? $"-{win / 3600:0.#} h" : win >= 60 ? $"-{win / 60:0} min" : $"-{win:0} s", 10.5, muted);
        dc.DrawText(lt, new Point(left, h - bottom + 3));
        var rt = Text("now", 10.5, muted);
        dc.DrawText(rt, new Point(right - rt.Width, h - bottom + 3));

        double X(double t) => left + (t - from) / win * plotW;
        double Y(double v) => top + plotH - Math.Clamp(v / max, 0, 1.02) * plotH;

        if (!double.IsNaN(Reference))
        {
            var refBrush = ThemeManager.GetBrush("Danger");
            var pen = new Pen(refBrush, 1) { DashStyle = new DashStyle([4, 4], 0) };
            pen.Freeze();
            double y = Math.Round(Y(Reference)) + 0.5;
            dc.DrawLine(pen, new Point(left, y), new Point(right, y));
            if (!string.IsNullOrEmpty(ReferenceLabel))
            {
                var rl = Text(ReferenceLabel, 10.5, refBrush);
                dc.DrawText(rl, new Point(right - rl.Width - 2, y - rl.Height - 1));
            }
        }

        dc.PushClip(new RectangleGeometry(new Rect(left, top - 2, plotW, plotH + 4)));
        bool first = true;
        foreach (var l in _lines)
        {
            var brush = l.Brush ?? ThemeManager.GetBrush(l.BrushKey);
            var color = brush is SolidColorBrush sb ? sb.Color : Colors.Gray;
            var geo = new StreamGeometry();
            Point? start = null, last = null;
            using (var ctx = geo.Open())
            {
                double prevT = double.NaN;
                foreach (var (t, v) in l.Data.Since(from - 2))
                {
                    if (double.IsNaN(v)) { prevT = double.NaN; continue; }
                    var p = new Point(X(t), Y(v));
                    // a gap of more than 5 s (server stopped) breaks the line
                    if (start == null || double.IsNaN(prevT) || t - prevT > 5)
                    {
                        ctx.BeginFigure(p, false, false);
                        start ??= p;
                    }
                    else ctx.LineTo(p, true, true);
                    last = p;
                    prevT = t;
                }
            }
            geo.Freeze();
            if (first && start != null && last != null)
            {
                // soft fill under the first series
                var fill = new StreamGeometry();
                using (var ctx = fill.Open())
                {
                    bool begun = false;
                    double prevT = double.NaN;
                    Point lastP = default;
                    foreach (var (t, v) in l.Data.Since(from - 2))
                    {
                        if (double.IsNaN(v)) continue;
                        var p = new Point(X(t), Y(v));
                        if (!begun || t - prevT > 5)
                        {
                            if (begun) { ctx.LineTo(new Point(lastP.X, top + plotH), false, false); }
                            ctx.BeginFigure(new Point(p.X, top + plotH), true, true);
                            begun = true;
                        }
                        ctx.LineTo(p, false, false);
                        lastP = p;
                        prevT = t;
                    }
                    if (begun) ctx.LineTo(new Point(lastP.X, top + plotH), false, false);
                }
                fill.Freeze();
                var grad = new LinearGradientBrush(Color.FromArgb(70, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
                grad.Freeze();
                dc.DrawGeometry(grad, null, fill);
            }
            var linePen = new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round };
            linePen.Freeze();
            dc.DrawGeometry(null, linePen, geo);
            first = false;
        }
        dc.Pop();

        // hover read-out
        if (_mouse is { } m && m.X >= left && m.X <= right && m.Y >= top && m.Y <= top + plotH)
        {
            double t = from + (m.X - left) / plotW * win;
            var linePen = new Pen(muted, 1);
            linePen.Freeze();
            dc.DrawLine(linePen, new Point(m.X, top), new Point(m.X, top + plotH));
            var rows = new List<(Brush, string)>();
            foreach (var l in _lines)
            {
                double bestDt = double.MaxValue, bestV = double.NaN, bestT = 0;
                foreach (var (st, v) in l.Data.Since(t - 3))
                {
                    double dt = Math.Abs(st - t);
                    if (dt < bestDt) { bestDt = dt; bestV = v; bestT = st; }
                    if (st > t + 3) break;
                }
                var brush = l.Brush ?? ThemeManager.GetBrush(l.BrushKey);
                if (bestDt < 3 && !double.IsNaN(bestV))
                {
                    dc.DrawEllipse(brush, null, new Point(X(bestT), Y(bestV)), 3.5, 3.5);
                    rows.Add((brush, $"{l.Name}: {Fmt(bestV)}{(string.IsNullOrEmpty(Unit) ? "" : " " + Unit)}"));
                }
            }
            var when = DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)).LocalDateTime.ToString("HH:mm:ss");
            var texts = rows.Select(r => (r.Item1, Text(r.Item2, 11.5, textBrush))).ToList();
            var head = Text(when, 11, muted);
            double bw = Math.Max(head.Width, texts.Count == 0 ? 0 : texts.Max(x => x.Item2.Width + 14)) + 16;
            double bh = head.Height + texts.Sum(x => x.Item2.Height + 1) + 10;
            double bx = m.X + 12 + bw > right ? m.X - 12 - bw : m.X + 12;
            double by = Math.Clamp(m.Y - bh / 2, top, top + plotH - bh);
            dc.DrawRoundedRectangle(ThemeManager.GetBrush("Surface"), new Pen(ThemeManager.GetBrush("BorderStrong"), 1), new Rect(bx, by, bw, bh), 5, 5);
            double yy = by + 5;
            dc.DrawText(head, new Point(bx + 8, yy));
            yy += head.Height + 1;
            foreach (var (brush, ft) in texts)
            {
                dc.DrawRoundedRectangle(brush, null, new Rect(bx + 8, yy + ft.Height / 2 - 4, 8, 8), 2, 2);
                dc.DrawText(ft, new Point(bx + 22, yy));
                yy += ft.Height + 1;
            }
        }
    }
}
