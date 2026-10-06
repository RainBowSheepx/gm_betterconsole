using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Controls;

/// <summary>
/// The rows of an addon's form (tab:Form): the field's text on the left with its help under it, its control on the
/// right — a switch, a number, a text box or a list. Below <see cref="NarrowWidth"/> the control goes under the
/// text. Switches and lists apply at once, numbers and texts on Enter or when they lose the focus; a field that is
/// not at its default is marked and has a reset button. The DataContext is the <see cref="FormWidgetVm"/>.
/// </summary>
public sealed class FormView : StackPanel
{
    public const double NarrowWidth = 460;

    private readonly List<Row> _rows = new();
    private FormWidgetVm? _form;
    private bool _narrow;

    public FormView()
    {
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            bool narrow = e.NewSize.Width < NarrowWidth;
            if (narrow == _narrow) return;
            _narrow = narrow;
            foreach (var r in _rows) r.Layout(narrow);
        };
    }

    // Subscribed while on screen: the form outlives its view (another window shows the server, the tab is rebuilt).
    private void Attach()
    {
        if (!IsLoaded) return;
        var form = DataContext as FormWidgetVm;
        if (form == _form) return;
        Detach();
        _form = form;
        if (form == null) return;
        form.Fields.CollectionChanged += OnFieldsChanged;
        Build();
    }

    private void Detach()
    {
        if (_form == null) return;
        _form.Fields.CollectionChanged -= OnFieldsChanged;
        foreach (var r in _rows) r.Dispose();
        _rows.Clear();
        _form = null;
    }

    private bool _rebuild;

    // A form defined again clears its fields and adds them one by one: one rebuild after all of it.
    private void OnFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rebuild) return;
        _rebuild = true;
        Dispatcher.BeginInvoke(() =>
        {
            _rebuild = false;
            Build();
        });
    }

    private void Build()
    {
        foreach (var r in _rows) r.Dispose();
        _rows.Clear();
        Children.Clear();
        if (_form == null) return;
        _narrow = ActualWidth > 0 && ActualWidth < NarrowWidth;
        bool first = true;
        foreach (var f in _form.Fields)
        {
            var row = new Row(this, _form, f, first);
            first = false;
            _rows.Add(row);
            Children.Add(row.Root);
            row.Layout(_narrow);
        }
    }

    /// <summary>For the UI script runner: the rows as text ("id = value [modified]").</summary>
    public IEnumerable<string> ScriptRows() => _rows.Select(r => r.Describe());

    /// <summary>For the UI script runner: types into a field (or ticks it, or picks a choice) and applies it as the user would.</summary>
    public bool ScriptSet(string id, string value) => _rows.FirstOrDefault(r => r.Field.Id == id)?.ScriptSet(value) ?? false;

    private static TextBlock Muted(string text, double size = 11.5)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        return t;
    }

    /// <summary>One field: its text, help, control, reset button and the line for what is wrong with a typed value.</summary>
    private sealed class Row : IDisposable
    {
        private readonly FormView _view;
        private readonly FormWidgetVm _form;
        public readonly FormFieldVm Field;
        public readonly Grid Root = new();
        // The control fills what is left of the panel; the unit and the reset button are docked on its right.
        private readonly DockPanel _controlPanel = new() { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock? _help;
        private readonly TextBlock _error;
        private readonly Ellipse? _dot;
        private readonly Button? _reset;
        private Control? _control;
        private bool _updating;    // the control is being set from the server's value, not by the user
        private bool _dirty;       // the user typed into the text box since it last showed the server's value
        private bool _applying;    // in Apply (its question takes the focus away from the box)
        private string? _sent;     // sent and not answered yet: the same value is not sent again

        public Row(FormView view, FormWidgetVm form, FormFieldVm field, bool first)
        {
            _view = view;
            _form = form;
            Field = field;
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < 4; i++) Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _error = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
            _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
            Grid.SetRow(_error, 3);
            Grid.SetColumnSpan(_error, 2);

            if (field.Type == "header")
            {
                var h = new TextBlock { Text = field.Label.ToUpperInvariant(), Margin = new Thickness(0, first ? 0 : 12, 0, 4) };
                h.SetResourceReference(FrameworkElement.StyleProperty, "Text.Section");
                Grid.SetColumnSpan(h, 2);
                Root.Children.Add(h);
                return;
            }
            if (field.Type == "note")
            {
                var n = Muted(field.Label, 12);
                n.Margin = new Thickness(0, 2, 0, 6);
                Grid.SetColumnSpan(n, 2);
                Root.Children.Add(n);
                return;
            }

            Root.Margin = new Thickness(0, first ? 0 : 4, 0, 4);
            Root.ToolTip = string.IsNullOrEmpty(field.Tooltip) ? null : field.Tooltip;
            ToolTipService.SetShowOnDisabled(Root, true);

            // the text: a dot when not at its default, the label, a dimmed note after it
            var head = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            _dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            _dot.SetResourceReference(Shape.FillProperty, "Brush.Accent");
            head.Children.Add(_dot);
            var label = new TextBlock { Text = field.Label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, field.CanChange || field.ReadOnly ? "Brush.Text" : "Brush.TextMuted");
            head.Children.Add(label);
            var note = field.Missing ? "not on this server" : field.Protected ? "protected" : field.Note;
            if (!string.IsNullOrWhiteSpace(note))
            {
                var nt = Muted(note!, 11.5);
                nt.Margin = new Thickness(8, 0, 0, 0);
                nt.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(nt);
            }
            Root.Children.Add(head);

            if (!string.IsNullOrWhiteSpace(field.Help))
            {
                // A few lines; all of it in the tooltip.
                _help = Muted(field.Help!);
                _help.MaxHeight = 48;
                _help.TextTrimming = TextTrimming.CharacterEllipsis;
                _help.Margin = new Thickness(0, 1, 12, 0);
                Grid.SetRow(_help, 1);
                Root.Children.Add(_help);
            }

            _control = MakeControl();
            // Every input row has the slot of the reset button, so the controls line up on the right.
            if (field.IsInput)
            {
                _reset = new Button { Content = new TextBlock { Text = "", FontSize = 12 }, Margin = new Thickness(4, 0, 0, 0), Visibility = Visibility.Hidden };
                ((TextBlock)_reset.Content).SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
                _reset.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Icon");
                _reset.ToolTip = field.Default != null ? "Reset to the default: " + field.Shown(field.Default) : null;
                _reset.Click += (_, _) => { if (field.Default != null) Apply(field.Default, fromReset: true); };
                DockPanel.SetDock(_reset, Dock.Right);
                _controlPanel.Children.Add(_reset);
            }
            if (field.Unit != null && field.Type == "number")
            {
                var u = Muted(field.Unit, 12);
                u.VerticalAlignment = VerticalAlignment.Center;
                u.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(u, Dock.Right);
                _controlPanel.Children.Add(u);
            }
            _controlPanel.Children.Add(_control);
            Root.Children.Add(_controlPanel);
            Root.Children.Add(_error);

            field.ServerValue += OnServerValue;
            field.PropertyChanged += OnFieldChanged;
            Show();
        }

        public void Dispose()
        {
            Field.ServerValue -= OnServerValue;
            Field.PropertyChanged -= OnFieldChanged;
        }

        private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FormFieldVm.IsModified)) Mark();
        }

        private void OnServerValue()
        {
            _sent = null;
            // Keep what the user typed and has not applied (also while the window is in the background; Esc puts
            // the server's value back); the rest shows the server's value (also a change it refused).
            if (_control is TextBox && _dirty) return;
            Show();
        }

        public void Layout(bool narrow)
        {
            if (_control == null) return;
            Grid.SetRow(_controlPanel, narrow ? 2 : 0);
            Grid.SetColumn(_controlPanel, narrow ? 0 : 1);
            Grid.SetColumnSpan(_controlPanel, narrow ? 2 : 1);
            _controlPanel.Margin = narrow ? new Thickness(0, 4, 0, 0) : new Thickness(0);
            // Texts and lists take the whole width under the text; numbers and switches stay as they are.
            bool wide = _control is TextBox { Tag: "wide" } or ComboBox;
            _controlPanel.HorizontalAlignment = !narrow ? HorizontalAlignment.Right : wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            if (wide) _control.Width = narrow ? double.NaN : 220;
        }

        private Control MakeControl()
        {
            var f = Field;
            Control c;
            if (f.Protected || f.Missing && f.Type != "bool")
            {
                var tb = new TextBox { Text = f.Protected ? "••••••" : "", IsEnabled = false, Width = 160 };
                return tb;
            }
            switch (f.Type)
            {
                case "bool":
                    var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, IsEnabled = f.CanChange };
                    cb.Click += (_, _) => Apply(cb.IsChecked == true ? "1" : "0");
                    c = cb;
                    break;
                case "choice":
                    var combo = new ComboBox { Width = 220, IsEditable = f.Editable, IsEnabled = f.CanChange, MaxDropDownHeight = 320 };
                    // What is picked in the open list applies when it closes; ↑ / ↓ on the closed list open it instead of
                    // applying every step on the way (each one a console command).
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (_updating || f.Editable || combo.SelectedIndex < 0 || combo.IsDropDownOpen) return;
                        Apply(ChoiceValue(combo));
                    };
                    combo.DropDownClosed += (_, _) => { if (!_updating && (combo.SelectedIndex >= 0 || f.Editable)) Apply(ChoiceValue(combo)); };
                    combo.PreviewKeyDown += (_, e) =>
                    {
                        if (!combo.IsDropDownOpen && e.Key is Key.Up or Key.Down && !f.Editable)
                        {
                            combo.IsDropDownOpen = true;
                            e.Handled = true;
                        }
                    };
                    if (f.Editable)
                    {
                        combo.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(ChoiceValue(combo)); e.Handled = true; } };
                        // Not when the window only went to the background (the focus goes nowhere then).
                        combo.LostKeyboardFocus += (_, e) => { if (e.NewFocus != null && !combo.IsKeyboardFocusWithin && !combo.IsDropDownOpen) Apply(ChoiceValue(combo)); };
                    }
                    c = combo;
                    break;
                default:
                    var box = new TextBox { Width = f.Type == "number" ? 110 : 220, IsEnabled = f.CanChange, Tag = f.Type == "number" ? null : "wide" };
                    if (f.Type == "number") box.HorizontalContentAlignment = HorizontalAlignment.Right;
                    box.TextChanged += (_, _) => { if (!_updating) _dirty = true; };
                    box.KeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Enter)
                        {
                            Apply(box.Text);
                            e.Handled = true;
                        }
                        else if (e.Key == Key.Escape)
                        {
                            Show();
                            e.Handled = true;
                        }
                    };
                    // Leaving the box applies it, not the window going to the background (alt-tab: the focus goes nowhere).
                    box.LostKeyboardFocus += (_, e) => { if (_dirty && e.NewFocus != null) Apply(box.Text); };
                    if (f.Type == "number" && f.CanChange)
                    {
                        // Up / Down and the wheel step the number; Enter (or leaving the box) applies it.
                        box.PreviewKeyDown += (_, e) =>
                        {
                            if (e.Key is Key.Up or Key.Down)
                            {
                                StepBy(box, e.Key == Key.Up ? 1 : -1);
                                e.Handled = true;
                            }
                        };
                        box.PreviewMouseWheel += (_, e) =>
                        {
                            if (!box.IsKeyboardFocusWithin) return;
                            StepBy(box, e.Delta > 0 ? 1 : -1);
                            e.Handled = true;
                        };
                    }
                    c = box;
                    break;
            }
            return c;
        }

        private void StepBy(TextBox box, int sign)
        {
            var f = Field;
            if (!LuaJson.TryNumber(box.Text, out var n)) n = LuaJson.TryNumber(f.Value ?? "", out var v) ? v : 0;
            n += sign * (f.Step ?? 1);
            if (f.Min is { } mn) n = Math.Max(mn, n);
            if (f.Max is { } mx) n = Math.Min(mx, n);
            box.Text = LuaJson.NumberText(Math.Round(n, 10));
            box.CaretIndex = box.Text.Length;
            _dirty = true;
        }

        private string ChoiceValue(ComboBox combo)
        {
            var f = Field;
            if (combo.SelectedItem is ComboBoxItem { Tag: string v } item && (!f.Editable || combo.Text == (string)item.Content)) return v;
            // Typed: the value of a choice with that text, else the text itself.
            foreach (var (text, value) in f.Choices)
                if (text == combo.Text) return value;
            return combo.Text;
        }

        /// <summary>The control shows the server's value.</summary>
        private void Show()
        {
            var f = Field;
            _updating = true;
            try
            {
                SetError(null);
                switch (_control)
                {
                    case CheckBox cb:
                        cb.IsChecked = FormFieldVm.Truthy(f.Value);
                        break;
                    case ComboBox combo:
                        combo.Items.Clear();
                        int selected = -1;
                        foreach (var (text, value) in f.Choices)
                        {
                            if (selected < 0 && f.Value != null && f.Same(value, f.Value)) selected = combo.Items.Count;
                            combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
                        }
                        // A value that is not one of the choices is shown as it is.
                        if (selected < 0 && !string.IsNullOrEmpty(f.Value) && !f.Editable)
                        {
                            selected = combo.Items.Count;
                            combo.Items.Add(new ComboBoxItem { Content = f.Value, Tag = f.Value });
                        }
                        combo.SelectedIndex = selected;
                        if (f.Editable && selected < 0) combo.Text = f.Value ?? "";
                        break;
                    case TextBox box when !f.Protected && !f.Missing:
                        box.Text = f.Value ?? "";
                        _dirty = false;
                        break;
                }
            }
            finally
            {
                _updating = false;
            }
            Mark();
        }

        private void Mark()
        {
            bool modified = Field.IsModified;
            if (_dot != null)
            {
                _dot.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
                _dot.ToolTip = modified ? "Not the default (" + Field.Shown(Field.Default) + ")" : null;
            }
            if (_reset != null) _reset.Visibility = modified ? Visibility.Visible : Visibility.Hidden;
        }

        private void SetError(string? text)
        {
            _error.Text = text ?? "";
            _error.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            if (_control is TextBox tb)
            {
                if (text != null) tb.SetResourceReference(Control.BorderBrushProperty, "Brush.Danger");
                else tb.ClearValue(Control.BorderBrushProperty);
            }
        }

        /// <summary>Checks the new value, asks the field's question, sends it; the server's answer comes back as its value.</summary>
        private void Apply(string input, bool fromReset = false)
        {
            var f = Field;
            if (!f.CanChange || _applying) return;
            if (f.Value != null && f.Same(input, f.Value))
            {
                // Typed the value it has (also "500.0" for 500): only the text is tidied up.
                if (!fromReset) Show();
                return;
            }
            // Sent already (Enter, then the box lost the focus before the server answered).
            if (_sent != null && f.Same(input, _sent)) return;
            if (f.Check(input, out var typed) is { } problem)
            {
                SetError(problem);
                return;
            }
            SetError(null);
            if (!string.IsNullOrWhiteSpace(f.Confirm))
            {
                var question = f.Confirm!.Replace("{value}", f.Shown(input));
                _applying = true;
                bool yes;
                try { yes = PromptDialog.Confirm(Window.GetWindow(_view), f.Label, question, "Apply"); }
                finally { _applying = false; }
                if (!yes)
                {
                    Show();
                    return;
                }
            }
            _dirty = false;
            _sent = input;
            _form.Change(f, typed);
        }

        public string Describe()
        {
            var f = Field;
            if (!f.IsInput) return $"{f.Type}: {f.Label}";
            var shown = _control switch
            {
                CheckBox cb => cb.IsChecked == true ? "on" : "off",
                ComboBox combo => combo.Text,
                TextBox tb => tb.Text,
                _ => "",
            };
            return $"{f.Id} ({f.Type}) = {f.Value ?? "nil"} shown \"{shown}\"" + (f.IsModified ? " [modified]" : "") +
                   (f.Missing ? " [missing]" : "") + (f.Protected ? " [protected]" : "") + (_error.Visibility == Visibility.Visible ? $" error: {_error.Text}" : "") +
                   $" at {(Grid.GetRow(_controlPanel) == 2 ? "under" : "right")}";
        }

        public bool ScriptSet(string value)
        {
            switch (_control)
            {
                case CheckBox cb:
                    cb.IsChecked = FormFieldVm.Truthy(value);
                    Apply(cb.IsChecked == true ? "1" : "0");
                    return true;
                case ComboBox combo when !Field.Editable:
                    foreach (ComboBoxItem item in combo.Items)
                        if ((string)item.Tag == value || (string)item.Content == value) { combo.SelectedItem = item; return true; }
                    return false;
                case ComboBox combo:
                    combo.Text = value;
                    Apply(ChoiceValue(combo));
                    return true;
                case TextBox tb:
                    tb.Text = value;
                    Apply(value);
                    return true;
            }
            return false;
        }
    }
}
