using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterConsole.App.Themes;
using BetterConsole.App.ViewModels;
using BetterConsole.Core.Console;
using BetterConsole.Sdk;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;

namespace BetterConsole.App.Views;

/// <summary>Per document line: when it arrived, how it is coloured.</summary>
internal readonly record struct LineMeta(long Id, DateTime Time, ColorSpan[] Spans, ConsoleLineKind Kind, int PrefixLength, bool IsPreview);

public partial class ConsoleView : UserControl
{
    private const string CommandPrefix = "› ";
    private const string AppPrefix = "▸ ";
    private const string AppErrorPrefix = "▲ ";

    private readonly ServerViewModel _vm;
    private readonly TextDocument _doc;
    private readonly List<LineMeta> _meta = new(32768);
    private readonly ConsoleColorizer _colorizer;
    private readonly TimestampMargin _timeMargin;
    private readonly SearchPanel _search;
    private readonly StringBuilder _pending = new(64 * 1024);
    private readonly List<LineMeta> _pendingMeta = new(1024);
    private readonly DispatcherTimer _valueTimer;
    private readonly DispatcherTimer _userScrollTimer;
    private ConsoleLine? _preview;
    private bool _follow = true;
    private bool _userScrolling;
    private int _newWhileAway;

    private int _historyIndex = -1;
    private string _historyDraft = "";
    private bool _suppressSuggest;
    private string _typedBeforeNavigation = "";
    private List<CompletionItem> _suggestions = new();

    public ConsoleView(ServerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        _doc = Output.Document;
        _doc.UndoStack.SizeLimit = 0;

        var s = vm.Settings;
        Output.FontFamily = new FontFamily($"{s.ConsoleFont}, Cascadia Mono, Consolas");
        Output.FontSize = s.ConsoleFontSize;
        Output.Options.EnableHyperlinks = true;
        Output.Options.RequireControlModifierForHyperlinkClick = true;
        Output.Options.EnableEmailHyperlinks = false;
        Output.Options.EnableRectangularSelection = true;
        Output.Options.AllowScrollBelowDocument = false;
        Output.TextArea.SelectionCornerRadius = 2;
        Output.TextArea.SelectionBorder = null;
        Output.TextArea.Caret.CaretBrush = Brushes.Transparent;

        _colorizer = new ConsoleColorizer(GetMeta);
        Output.TextArea.TextView.LineTransformers.Add(_colorizer);
        _timeMargin = new TimestampMargin(GetMeta);
        Output.TextArea.LeftMargins.Insert(0, _timeMargin);
        _search = SearchPanel.Install(Output);
        _search.MarkerCornerRadius = 2;

        Output.TextArea.TextView.ScrollOffsetChanged += OnScrollChanged;
        // A horizontal scroll bar appearing (or a resize) shrinks the view: stay at the end.
        Output.TextArea.TextView.SizeChanged += (_, _) => { if (_follow) Output.ScrollToEnd(); };
        Output.TextArea.TextView.VisualLinesChanged += (_, _) => KeepHorizontalBar();
        Output.PreviewMouseWheel += (_, e) => { if (e.Delta > 0) MarkUserScroll(); };
        Output.PreviewKeyDown += (_, e) => { if (e.Key is Key.Up or Key.PageUp or Key.Home) MarkUserScroll(); };
        Output.TextArea.PreviewMouseLeftButtonDown += (_, _) => MarkUserScroll();
        Output.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => MarkUserScroll()));
        Output.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, e) => { if (e.ScrollEventType != ScrollEventType.EndScroll) MarkUserScroll(); }));

        _userScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _userScrollTimer.Tick += (_, _) => { _userScrollTimer.Stop(); _userScrolling = Mouse.LeftButton == MouseButtonState.Pressed; };
        _valueTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _valueTimer.Tick += (_, _) => { _valueTimer.Stop(); RequestValues(); };

        WrapToggle.IsChecked = s.ConsoleWordWrap;
        Output.WordWrap = s.ConsoleWordWrap;
        ResetHorizontalBar();
        TimeToggle.IsChecked = s.ConsoleTimestamps;
        _timeMargin.Visibility = s.ConsoleTimestamps ? Visibility.Visible : Visibility.Collapsed;

        ApplyTheme();
        ThemeManager.Changed += _ => ApplyTheme();
        vm.ConsoleEvents += OnConsoleEvents;
        vm.SettingsChanged += () =>
        {
            Output.FontFamily = new FontFamily($"{vm.Settings.ConsoleFont}, Cascadia Mono, Consolas");
            Output.FontSize = vm.Settings.ConsoleFontSize;
            _timeMargin.InvalidateMeasure();
        };
        Loaded += (_, _) => FocusInput();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) Dispatcher.BeginInvoke(FocusInput, DispatcherPriority.Input);
            else HideSearchMessage();
        };
        UpdateCount();
    }

    // AvalonEdit's "No matches found!" is a tool tip of its own window: it would stay on screen over
    // another tab, or over another server's window. It is closed when the console goes out of view; the
    // search itself stays.
    private static readonly System.Reflection.FieldInfo? SearchMessage =
        typeof(SearchPanel).GetField("messageView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private void HideSearchMessage()
    {
        if (SearchMessage?.GetValue(_search) is ToolTip tip) tip.IsOpen = false;
        else if (!_search.IsClosed) _search.Close();
    }

    public void FocusInput()
    {
        Input.Focus();
        Keyboard.Focus(Input);
    }

    private LineMeta? GetMeta(int index) => index >= 0 && index < _meta.Count ? _meta[index] : null;

    private void ApplyTheme()
    {
        Output.TextArea.SelectionBrush = ThemeManager.GetBrush("ConsoleSelection");
        Output.TextArea.SelectionForeground = null;
        _colorizer.Reset(ThemeManager.GetColor("ConsoleBackground"));
        _timeMargin.Foreground = ThemeManager.GetBrush("TextMuted");
        // Matches of the search: the warning colour, see-through so the text colours stay readable.
        var w = ThemeManager.GetColor("Warning");
        var marker = new SolidColorBrush(Color.FromArgb(0x55, w.R, w.G, w.B));
        marker.Freeze();
        _search.MarkerBrush = marker;
        Output.TextArea.TextView.Redraw();
        _timeMargin.InvalidateVisual();
    }

    // ------------------------------------------------------------------------------ output

    private void OnConsoleEvents(IReadOnlyList<ConsoleEvent> events)
    {
        bool hadPreview = _meta.Count > 0 && _meta[^1].IsPreview;
        int added = 0;
        _doc.BeginUpdate();
        try
        {
            if (hadPreview) RemoveLastLine();
            foreach (var e in events)
            {
                switch (e)
                {
                    case LineAdded la:
                        AppendPending(la.Id, la.Line, isPreview: false);
                        added++;
                        break;
                    case LineAmended am:
                        FlushPending();
                        Amend(am.Id, am.Line);
                        break;
                    case PreviewChanged pc:
                        _preview = pc.Line;
                        break;
                }
            }
            if (_preview != null) AppendPending(long.MinValue, _preview, isPreview: true);
            FlushPending();
        }
        finally
        {
            _doc.EndUpdate();
        }
        Trim();
        if (added > 0 && !_follow) _newWhileAway += added;
        if (_follow) ScrollToEndIfIdle();
        UpdateJumpButton();
        UpdateCount();
    }

    private void AppendPending(long id, ConsoleLine line, bool isPreview)
    {
        string prefix = line.Kind switch
        {
            ConsoleLineKind.Command => CommandPrefix,
            // A long notice broken into lines: its next lines are indented instead of marked again.
            ConsoleLineKind.App or ConsoleLineKind.AppError when line.Text.StartsWith("  ") => "  ",
            ConsoleLineKind.App => AppPrefix,
            ConsoleLineKind.AppError => AppErrorPrefix,
            _ => "",
        };
        if (_meta.Count + _pendingMeta.Count > 0) _pending.Append('\n');
        _pending.Append(prefix);
        // A stray carriage return or tab would break the one-line-per-entry layout.
        foreach (char c in line.Text) _pending.Append(c < ' ' && c != '\t' ? ' ' : c);
        _pendingMeta.Add(new LineMeta(id, line.Time, line.Spans as ColorSpan[] ?? line.Spans.ToArray(), line.Kind, prefix.Length, isPreview));
    }

    private void FlushPending()
    {
        if (_pendingMeta.Count == 0) return;
        _meta.AddRange(_pendingMeta);
        _doc.Insert(_doc.TextLength, _pending.ToString());
        _pending.Clear();
        _pendingMeta.Clear();
    }

    private void RemoveLastLine()
    {
        if (_meta.Count == 0) return;
        var last = _doc.GetLineByNumber(_doc.LineCount);
        int start = _meta.Count > 1 ? last.Offset - 1 : 0; // include the preceding line break
        _doc.Remove(start, _doc.TextLength - start);
        _meta.RemoveAt(_meta.Count - 1);
    }

    private void Amend(long id, ConsoleLine line)
    {
        for (int i = _meta.Count - 1, n = 0; i >= 0 && n < 1000; i--, n++)
        {
            if (_meta[i].Id != id) continue;
            var m = _meta[i];
            var dl = _doc.GetLineByNumber(i + 1);
            string prefix = _doc.GetText(dl.Offset, m.PrefixLength);
            _doc.Replace(dl.Offset, dl.Length, prefix + line.Text.Replace('\r', ' ').Replace('\n', ' '));
            _meta[i] = m with { Spans = line.Spans as ColorSpan[] ?? line.Spans.ToArray() };
            return;
        }
    }

    private void Trim()
    {
        int max = Math.Max(1000, _vm.Settings.ConsoleMaxLines);
        int count = _meta.Count;
        if (count <= max + max / 10) return;
        // While the user reads older output, keep it unless the buffer gets really big.
        if (!_follow && count < max * 2) return;
        int remove = count - max;
        var firstKept = _doc.GetLineByNumber(remove + 1);
        double removedHeight = 0;
        if (!_follow)
        {
            try { removedHeight = Output.TextArea.TextView.GetVisualTopByDocumentLine(remove + 1); } catch { }
        }
        double offset = Output.VerticalOffset;
        _doc.Remove(0, firstKept.Offset);
        _meta.RemoveRange(0, remove);
        if (!_follow && removedHeight > 0) Output.ScrollToVerticalOffset(Math.Max(0, offset - removedHeight));
    }

    public void Clear()
    {
        _doc.Text = "";
        _meta.Clear();
        _preview = null;
        _newWhileAway = 0;
        _follow = true;
        ResetHorizontalBar();
        UpdateJumpButton();
        UpdateCount();
    }

    /// <summary>
    /// AvalonEdit measures only the lines in view, so an automatic horizontal scroll bar comes and goes as
    /// long lines scroll in and out. Following the end turns that into a layout loop: the bar shrinks the
    /// view, the view scrolls to the end, the long line leaves it, the bar goes, the view grows, and again.
    /// Once a line was wider than the view the bar stays.
    /// </summary>
    private void KeepHorizontalBar()
    {
        if (Output.WordWrap || Output.HorizontalScrollBarVisibility == ScrollBarVisibility.Visible) return;
        var tv = (IScrollInfo)Output.TextArea.TextView;
        if (tv.ExtentWidth > tv.ViewportWidth + 0.5) Output.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
    }

    private void ResetHorizontalBar() =>
        Output.HorizontalScrollBarVisibility = Output.WordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    private void UpdateCount()
    {
        int n = _meta.Count - (_meta.Count > 0 && _meta[^1].IsPreview ? 1 : 0);
        LineCount.Text = n == 0 ? "No output yet" : $"{n:N0} lines";
    }

    // ------------------------------------------------------------------------------ scrolling

    private void MarkUserScroll()
    {
        _userScrolling = true;
        _userScrollTimer.Stop();
        _userScrollTimer.Start();
    }

    private bool IsAtBottom()
    {
        var tv = Output.TextArea.TextView;
        return tv.VerticalOffset + tv.ActualHeight >= tv.DocumentHeight - 4;
    }

    private void OnScrollChanged(object? sender, EventArgs e)
    {
        if (IsAtBottom())
        {
            if (!_follow)
            {
                _follow = true;
                _newWhileAway = 0;
                UpdateJumpButton();
            }
        }
        else if (_userScrolling && _follow)
        {
            _follow = false;
            _newWhileAway = 0;
            UpdateJumpButton();
        }
    }

    private void ScrollToEndIfIdle()
    {
        // Do not yank the view while the user drags a selection.
        if (Mouse.LeftButton == MouseButtonState.Pressed && Output.TextArea.IsMouseCaptured) return;
        Output.ScrollToEnd();
    }

    private void UpdateJumpButton()
    {
        JumpButton.Visibility = _follow ? Visibility.Collapsed : Visibility.Visible;
        JumpText.Text = _newWhileAway > 0 ? $"{_newWhileAway:N0} new line{(_newWhileAway == 1 ? "" : "s")}" : "Follow";
    }

    private void OnJumpToEnd(object sender, RoutedEventArgs e)
    {
        _follow = true;
        _newWhileAway = 0;
        Output.ScrollToEnd();
        UpdateJumpButton();
        FocusInput();
    }

    // ------------------------------------------------------------------------------ toolbar

    private void OnWrapChanged(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        Output.WordWrap = WrapToggle.IsChecked == true;
        _vm.Settings.ConsoleWordWrap = Output.WordWrap;
        ResetHorizontalBar();
    }

    private void OnTimeChanged(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        bool on = TimeToggle.IsChecked == true;
        _timeMargin.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _vm.Settings.ConsoleTimestamps = on;
    }

    private void OnClear(object sender, RoutedEventArgs e) => Clear();

    private void OnFind(object sender, RoutedEventArgs e)
    {
        ApplicationCommands.Find.Execute(null, Output.TextArea);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"console_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder();
        for (int i = 0; i < _meta.Count && i < _doc.LineCount; i++)
        {
            if (_meta[i].IsPreview) continue;
            var dl = _doc.GetLineByNumber(i + 1);
            sb.Append('[').Append(_meta[i].Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ");
            sb.AppendLine(_doc.GetText(dl));
        }
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
            _vm.Notify("Console saved.", NotifyKind.Success);
        }
        catch (Exception ex)
        {
            _vm.Notify("Could not save: " + ex.Message, NotifyKind.Error);
        }
    }

    // ------------------------------------------------------------------------------ input

    private void OnSendClick(object sender, RoutedEventArgs e) => Execute();

    private void Execute()
    {
        var text = Input.Text;
        HideSuggestions();
        if (string.IsNullOrWhiteSpace(text)) return;
        _vm.SendCommand(text);
        SetInput("");
        _historyIndex = -1;
        _follow = true;
        _newWhileAway = 0;
        Output.ScrollToEnd();
        UpdateJumpButton();
    }

    private void SetInput(string text)
    {
        _suppressSuggest = true;
        Input.Text = text;
        Input.CaretIndex = text.Length;
        _suppressSuggest = false;
        Placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>For the UI script runner: type into the input as a user would.</summary>
    public void ScriptInput(string text)
    {
        Input.Text = text;
        Input.CaretIndex = text.Length;
    }

    /// <summary>For the UI script runner: scroll the output by a number of lines (negative = up).</summary>
    public void ScriptScroll(double lines)
    {
        MarkUserScroll();
        Output.ScrollToVerticalOffset(Math.Max(0, Output.VerticalOffset + lines * Output.TextArea.TextView.DefaultLineHeight));
    }

    /// <summary>For the UI script runner: the whole console text.</summary>
    public string ScriptText() => _doc.Text;

    /// <summary>For the UI script runner: open the search box with a text.</summary>
    public void ScriptFind(string text)
    {
        OnFind(this, new RoutedEventArgs());
        _search.SearchPattern = text;
    }

    /// <summary>For the UI script runner: press a key in the input.</summary>
    public void ScriptKey(string key)
    {
        if (Enum.TryParse<Key>(key, true, out var k)) HandleKey(k, ModifierKeys.None);
    }

    private bool HandleKey(Key key, ModifierKeys mods)
    {
        bool popup = SuggestPopup.IsOpen && _suggestions.Count > 0;
        switch (key)
        {
            case Key.Enter:
                Execute();
                return true;
            case Key.Tab:
                if (popup) Accept(Suggestions.SelectedIndex >= 0 ? Suggestions.SelectedIndex : 0);
                else ShowSuggestions(force: true);
                return true;
            case Key.Down when popup:
                MoveSelection(+1);
                return true;
            case Key.Up when popup:
                MoveSelection(-1);
                return true;
            case Key.Up:
                HistoryStep(-1);
                return true;
            case Key.Down:
                HistoryStep(+1);
                return true;
            case Key.Escape:
                if (popup)
                {
                    if (Suggestions.SelectedIndex >= 0 && _typedBeforeNavigation.Length > 0) SetInput(_typedBeforeNavigation);
                    HideSuggestions();
                }
                else SetInput("");
                return true;
            case Key.Space when mods == ModifierKeys.Control:
                ShowSuggestions(force: true);
                return true;
            case Key.PageUp:
                MarkUserScroll();
                Output.ScrollToVerticalOffset(Math.Max(0, Output.VerticalOffset - Output.ViewportHeight * 0.9));
                return true;
            case Key.PageDown:
                Output.ScrollToVerticalOffset(Output.VerticalOffset + Output.ViewportHeight * 0.9);
                return true;
            case Key.L when mods == ModifierKeys.Control:
                Clear();
                return true;
            case Key.F when mods == ModifierKeys.Control:
                OnFind(this, new RoutedEventArgs());
                return true;
        }
        return false;
    }

    private void HistoryStep(int dir)
    {
        var h = _vm.History.Items;
        if (h.Count == 0) return;
        if (_historyIndex == -1)
        {
            if (dir > 0) return;
            _historyDraft = Input.Text;
            _historyIndex = h.Count;
        }
        _historyIndex = Math.Clamp(_historyIndex + dir, 0, h.Count);
        SetInput(_historyIndex == h.Count ? _historyDraft : h[_historyIndex]);
        if (_historyIndex == h.Count) _historyIndex = -1;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_suppressSuggest) return;
        _historyIndex = -1;
        ShowSuggestions(force: false);
    }

    private void ShowSuggestions(bool force)
    {
        var text = Input.Text;
        if (text.Length == 0 && !force)
        {
            HideSuggestions();
            return;
        }
        _suggestions = _vm.Catalog.Suggest(text, _vm.History.Items, 50);
        if (_suggestions.Count == 0)
        {
            HideSuggestions();
            return;
        }
        _typedBeforeNavigation = text;
        Suggestions.ItemsSource = _suggestions;
        Suggestions.SelectedIndex = -1;
        SuggestFooter.Text = (_vm.Catalog.IsLoaded ? "" : "The command list arrives once the companion addon is connected · ")
            + "Tab inserts · ↑/↓ choose · Esc closes";
        SuggestPopup.IsOpen = true;
        Suggestions.ScrollIntoView(_suggestions[0]);
        _valueTimer.Stop();
        _valueTimer.Start();
    }

    private void HideSuggestions()
    {
        SuggestPopup.IsOpen = false;
        _suggestions = new();
        Suggestions.ItemsSource = null;
    }

    private void MoveSelection(int dir)
    {
        int n = _suggestions.Count;
        int i = Suggestions.SelectedIndex;
        i = i < 0 ? (dir > 0 ? 0 : n - 1) : (i + dir + n) % n;
        Suggestions.SelectedIndex = i;
        Suggestions.ScrollIntoView(_suggestions[i]);
        // Like the game console: the chosen entry is put into the input right away.
        var item = _suggestions[i];
        SetInput(item.InsertText ?? item.Name);
    }

    private void Accept(int index)
    {
        if (index < 0 || index >= _suggestions.Count) return;
        var item = _suggestions[index];
        var text = item.InsertText ?? item.Name;
        if (item.Kind is CommandKind.Command or CommandKind.Variable or CommandKind.Map) text += " ";
        HideSuggestions();
        SetInput(text);
        // Right after "map " / "changelevel " offer the maps.
        if (item.Kind == CommandKind.Command && (item.Name == "map" || item.Name == "changelevel")) ShowSuggestions(force: true);
    }

    private void OnSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(Suggestions, d) is ListBoxItem item)
        {
            Accept(Suggestions.ItemContainerGenerator.IndexFromContainer(item));
            FocusInput();
        }
    }

    private void OnInputLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject d && SuggestPopup.Child is { } child && IsDescendant(child, d)) return;
        HideSuggestions();
    }

    private static bool IsDescendant(DependencyObject root, DependencyObject node)
    {
        for (var cur = node; cur != null; cur = Controls.TreeWalk.Parent(cur))
            if (cur == root) return true;
        return false;
    }

    /// <summary>Asks the server for the current values of the variables in view.</summary>
    private void RequestValues()
    {
        if (!_vm.BridgeConnected || _suggestions.Count == 0) return;
        var names = _suggestions.Where(s => s.Kind == CommandKind.Variable).Take(20).Select(s => s.Name).ToArray();
        if (names.Length > 0) _vm.Request("cvals", new { names });
    }
}

/// <summary>Colours console lines from their recorded colour runs.</summary>
internal sealed class ConsoleColorizer(Func<int, LineMeta?> meta) : DocumentColorizingTransformer
{
    private readonly Dictionary<uint, Brush> _brushes = new();
    private Color _background = Colors.Black;
    private Brush _command = Brushes.CornflowerBlue, _app = Brushes.MediumPurple, _appError = Brushes.IndianRed;
    private Typeface? _boldFace;

    public void Reset(Color background)
    {
        _background = background;
        _brushes.Clear();
        _command = ThemeManager.GetBrush("ConsoleCommand");
        _app = ThemeManager.GetBrush("ConsoleApp");
        _appError = ThemeManager.GetBrush("Danger");
    }

    private Brush BrushFor(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var b)) return b;
        var c = ThemeManager.Readable(argb, _background);
        b = new SolidColorBrush(c);
        b.Freeze();
        if (_brushes.Count > 2048) _brushes.Clear();
        _brushes[argb] = b;
        return b;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (meta(line.LineNumber - 1) is not { } m) return;
        int start = line.Offset, end = line.EndOffset;
        switch (m.Kind)
        {
            case ConsoleLineKind.Command:
                ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(_command));
                return;
            case ConsoleLineKind.App:
                ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(_app));
                break;
            case ConsoleLineKind.AppError:
                ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(_appError));
                break;
        }
        if (m.IsPreview)
        {
            ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(ThemeManager.GetBrush("TextMuted")));
            return;
        }
        foreach (var s in m.Spans)
        {
            int a = start + m.PrefixLength + s.Start;
            int b = Math.Min(end, a + s.Length);
            if (a >= b || a < start) continue;
            if (s.Argb != 0)
            {
                var brush = BrushFor(s.Argb);
                ChangeLinePart(a, b, e => e.TextRunProperties.SetForegroundBrush(brush));
            }
            if (s.Bold)
            {
                ChangeLinePart(a, b, e =>
                {
                    var tf = e.TextRunProperties.Typeface;
                    _boldFace = new Typeface(tf.FontFamily, tf.Style, FontWeights.Bold, tf.Stretch);
                    e.TextRunProperties.SetTypeface(_boldFace);
                });
            }
        }
    }
}

/// <summary>The arrival time of each line, left of the text. Not part of the text, so copying skips it.</summary>
internal sealed class TimestampMargin : AbstractMargin
{
    private readonly Func<int, LineMeta?> _meta;
    private Brush _foreground = Brushes.Gray;

    public TimestampMargin(Func<int, LineMeta?> meta)
    {
        _meta = meta;
        Margin = new Thickness(0, 0, 10, 0);
    }

    public Brush Foreground
    {
        get => _foreground;
        set { _foreground = value; InvalidateVisual(); }
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= OnLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null) newTextView.VisualLinesChanged += OnLinesChanged;
        InvalidateMeasure();
    }

    private void OnLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    // The font of the margin itself (inherited from the editor). Not the text view's: AvalonEdit
    // measures its left margins before the text view is connected, which then still reports the default
    // UI font, and the column came out too narrow for the timestamps. A change of the inherited font
    // measures the margin again by itself.
    private FormattedText Format(string s)
    {
        var typeface = new Typeface(GetValue(TextBlock.FontFamilyProperty) as FontFamily ?? new FontFamily("Consolas"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double size = (double)GetValue(TextBlock.FontSizeProperty) * 0.92;
        return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, _foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (TextView == null) return new Size(0, 0);
        return new Size(Math.Ceiling(Format("00:00:00").Width) + 4, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var tv = TextView;
        if (tv is not { VisualLinesValid: true }) return;
        foreach (var vl in tv.VisualLines)
        {
            int index = vl.FirstDocumentLine.LineNumber - 1;
            if (_meta(index) is not { IsPreview: false } m) continue;
            var ft = Format(m.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            double y = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop) - tv.VerticalOffset;
            dc.DrawText(ft, new Point(2, y + 1));
        }
    }
}
