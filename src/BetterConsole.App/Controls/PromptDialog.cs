using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterConsole.App.Themes;

namespace BetterConsole.App.Controls;

/// <summary>
/// A small themed dialog with a few fields, built in code:
/// <code>
/// var d = new PromptDialog(owner, "Ban player", "Bans Nick for the chosen time.", "Ban", danger: true)
///     .Choice("duration", "Duration", [("1 hour", "60"), ("Permanent", "0")], "60")
///     .Text("reason", "Reason", "Rule violation");
/// if (d.ShowDialog() == true) use(d["duration"], d["reason"]);
/// </code>
/// </summary>
public sealed class PromptDialog : Window
{
    private readonly StackPanel _fields = new();
    private readonly Dictionary<string, Func<string>> _getters = new();
    private readonly Button _ok;
    private Control? _first;

    public PromptDialog(Window? owner, string title, string? description, string okText, bool danger = false)
    {
        Owner = owner;
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Brush.Panel");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        FontFamily = (FontFamily)Application.Current.Resources["Font.Ui"];
        FontSize = 13;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        var head = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        root.Children.Add(head);
        if (!string.IsNullOrWhiteSpace(description))
        {
            var d = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
            d.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            root.Children.Add(d);
        }
        root.Children.Add(_fields);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        _ok = new Button { Content = okText, MinWidth = 90, IsDefault = true };
        _ok.SetResourceReference(StyleProperty, danger ? "Btn.Danger" : "Btn.Primary");
        _ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_ok);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) =>
        {
            if (_first != null) { _first.Focus(); Keyboard.Focus(_first); }
            else _ok.Focus();
            if (_first is TextBox tb) tb.SelectAll();
        };
    }

    public string this[string key] => _getters.TryGetValue(key, out var g) ? g() : "";

    private void Label(string text)
    {
        var l = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 5), FontSize = 12 };
        l.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        _fields.Children.Add(l);
    }

    public PromptDialog Text(string key, string label, string value = "")
    {
        Label(label);
        var tb = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 12) };
        _fields.Children.Add(tb);
        _getters[key] = () => tb.Text;
        _first ??= tb;
        return this;
    }

    /// <summary>A drop-down; <paramref name="editable"/> lets the user type a value of their own.</summary>
    public PromptDialog Choice(string key, string label, IEnumerable<(string Text, string Value)> items, string selected, bool editable = false)
    {
        Label(label);
        var list = items.ToList();
        var cb = new ComboBox { Margin = new Thickness(0, 0, 0, 12), IsEditable = editable, MaxDropDownHeight = 320 };
        foreach (var (text, _) in list) cb.Items.Add(text);
        int idx = list.FindIndex(i => i.Value == selected);
        cb.SelectedIndex = idx >= 0 ? idx : 0;
        if (editable && idx < 0) cb.Text = selected;
        _fields.Children.Add(cb);
        _getters[key] = () =>
        {
            if (cb.SelectedIndex >= 0 && cb.SelectedIndex < list.Count && (!editable || cb.Text == list[cb.SelectedIndex].Text)) return list[cb.SelectedIndex].Value;
            return cb.Text;
        };
        _first ??= cb;
        return this;
    }

    /// <summary>Who the action is for: the Steam avatar (or the coloured initial), the name and the SteamID.</summary>
    public PromptDialog Player(string name, string steamId, ImageSource? avatar, bool bot = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var face = Face(name, avatar, bot, 34);
        face.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(face);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(steamId))
        {
            var sid = new TextBlock { Text = steamId, FontSize = 11.5, FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"] };
            sid.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
            text.Children.Add(sid);
        }
        row.Children.Add(text);
        _fields.Children.Insert(0, row);
        return this;
    }

    /// <summary>The players the action is for: one as <see cref="Player"/>, several as a row of faces and their names.</summary>
    public PromptDialog Players(IReadOnlyList<(string Name, string SteamId, ImageSource? Avatar, bool Bot)> players)
    {
        if (players.Count == 1) return Player(players[0].Name, players[0].SteamId, players[0].Avatar, players[0].Bot);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var faces = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        foreach (var (p, i) in players.Take(5).Select((p, i) => (p, i)))
        {
            // Overlapping, each with a ring of the dialog's colour.
            var ring = new Border { CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(2), Child = Face(p.Name, p.Avatar, p.Bot, 30),
                Margin = new Thickness(i == 0 ? 0 : -10, 0, 0, 0) };
            ring.SetResourceReference(Border.BorderBrushProperty, "Brush.Panel");
            Panel.SetZIndex(ring, 5 - i);
            faces.Children.Add(ring);
        }
        row.Children.Add(faces);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = $"{players.Count} players", FontWeight = FontWeights.SemiBold });
        var names = new TextBlock { Text = string.Join(", ", players.Select(p => p.Name)), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis };
        names.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        text.Children.Add(names);
        row.Children.Add(text);
        _fields.Children.Insert(0, row);
        return this;
    }

    /// <summary>The Steam avatar over the coloured initial (a robot for bots), as in the Players table.</summary>
    private static Grid Face(string name, ImageSource? avatar, bool bot, double size)
    {
        var face = new Grid { Width = size, Height = size };
        var circle = new Border { CornerRadius = new CornerRadius(size / 2), Background = (Brush)new NameToColorConverter().Convert(name, typeof(Brush), null!, System.Globalization.CultureInfo.CurrentCulture) };
        circle.Child = bot
            ? new TextBlock
            {
                Text = "", FontFamily = (FontFamily)Application.Current.Resources["Font.Icons"], Foreground = Brushes.White, FontSize = Math.Round(size * 0.5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }
            : new TextBlock
            {
                Text = (string)new InitialConverter().Convert(name, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture),
                Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = Math.Round(size * 0.44),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        face.Children.Add(circle);
        if (avatar != null) face.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new ImageBrush(avatar) { Stretch = Stretch.UniformToFill } });
        return face;
    }

    public PromptDialog Note(string text)
    {
        var n = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, -4, 0, 8) };
        n.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        _fields.Children.Add(n);
        return this;
    }

    /// <summary>A yes/no question with the dialog's look.</summary>
    public static bool Confirm(Window? owner, string title, string text, string okText = "OK", bool danger = false) =>
        new PromptDialog(owner, title, text, okText, danger).ShowDialog() == true;
}
