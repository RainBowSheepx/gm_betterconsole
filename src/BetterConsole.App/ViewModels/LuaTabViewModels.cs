using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using BetterConsole.App.Themes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterConsole.App.ViewModels;

/// <summary>A tab defined by a server addon through BetterConsole.AddTab (see docs/lua-api.md).</summary>
public sealed partial class LuaTabVm : ObservableObject
{
    private readonly Dictionary<string, LuaWidgetVm> _widgets = new();

    public LuaTabVm(string id) => Id = id;

    public string Id { get; }
    [ObservableProperty] private string title = "";
    [ObservableProperty] private int order = 100;
    public ObservableCollection<LuaWidgetVm> Widgets { get; } = new();

    /// <summary>Sends an action (button press) back to the server.</summary>
    public Action<string, string, string>? ActionSink { get; set; }

    public void Define(string id, string kind, JsonElement opts)
    {
        if (_widgets.TryGetValue(id, out var existing))
        {
            if (existing.Kind == kind)
            {
                existing.Configure(opts);
                return;
            }
            Widgets.Remove(existing);
        }
        LuaWidgetVm w = kind switch
        {
            "text" => new TextWidgetVm(),
            "kv" => new KeyValueWidgetVm(),
            "table" => new TableWidgetVm(),
            "log" => new LogWidgetVm(),
            "chart" => new ChartWidgetVm(),
            "buttons" => new ButtonsWidgetVm(),
            _ => new TextWidgetVm { Text = $"(unknown widget type \"{kind}\")" },
        };
        w.Id = id;
        w.Kind = kind;
        w.Tab = this;
        w.Configure(opts);
        _widgets[id] = w;
        Widgets.Add(w);
    }

    public void Data(string id, string op, JsonElement data)
    {
        if (_widgets.TryGetValue(id, out var w)) w.Apply(op, data);
    }
}

public abstract partial class LuaWidgetVm : ObservableObject
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public LuaTabVm? Tab { get; set; }
    [ObservableProperty] private string? title;
    /// <summary>Width in twelfths of the tab.</summary>
    [ObservableProperty] private int span = 12;

    public virtual void Configure(JsonElement opts)
    {
        Title = StatsVm.Str(opts, "title");
        var s = StatsVm.Num(opts, "span");
        Span = double.IsNaN(s) ? 12 : Math.Clamp((int)s, 1, 12);
    }

    public abstract void Apply(string op, JsonElement data);

    protected static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetDouble().ToString("0.###"),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => e.GetRawText(),
    };

    protected static Brush? ParseBrush(string? s)
    {
        if (!ThemeManager.TryParse(s, out var c)) return null;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public sealed partial class TextWidgetVm : LuaWidgetVm
{
    [ObservableProperty] private string text = "";

    public override void Apply(string op, JsonElement data)
    {
        if (op == "clear") Text = "";
        else if (op == "set") Text = Text(data);
    }
}

public sealed record KeyValueRow(string Key, string Value);

public sealed class KeyValueWidgetVm : LuaWidgetVm
{
    public ObservableCollection<KeyValueRow> Rows { get; } = new();

    public override void Apply(string op, JsonElement data)
    {
        if (op == "clear") { Rows.Clear(); return; }
        if (op != "set") return;
        var rows = new List<KeyValueRow>();
        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in data.EnumerateObject()) rows.Add(new KeyValueRow(p.Name, Text(p.Value)));
            rows.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase));
        }
        else if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var pair in data.EnumerateArray())
            {
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() >= 2)
                    rows.Add(new KeyValueRow(Text(pair[0]), Text(pair[1])));
            }
        }
        // Update in place when only values changed: no flicker.
        if (rows.Count == Rows.Count && rows.Select(r => r.Key).SequenceEqual(Rows.Select(r => r.Key)))
        {
            for (int i = 0; i < rows.Count; i++) if (Rows[i] != rows[i]) Rows[i] = rows[i];
            return;
        }
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
    }
}

public sealed partial class TableWidgetVm : LuaWidgetVm
{
    [ObservableProperty] private string[] columns = [];
    public ObservableCollection<string[]> Rows { get; } = new();

    public override void Configure(JsonElement opts)
    {
        base.Configure(opts);
        if (opts.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
            Columns = cols.EnumerateArray().Select(Text).ToArray();
    }

    public override void Apply(string op, JsonElement data)
    {
        if (op == "clear") { Rows.Clear(); return; }
        if (op != "set" || data.ValueKind != JsonValueKind.Array) return;
        Rows.Clear();
        int n = Math.Max(Columns.Length, 1);
        foreach (var row in data.EnumerateArray().Take(2000))
        {
            var cells = new string[n];
            if (row.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var cell in row.EnumerateArray())
                {
                    if (i >= n) break;
                    cells[i++] = Text(cell);
                }
            }
            else cells[0] = Text(row);
            for (int i = 0; i < n; i++) cells[i] ??= "";
            Rows.Add(cells);
        }
    }
}

public sealed record LogLine(DateTime Time, string Text, Brush? Color)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

public sealed class LogWidgetVm : LuaWidgetVm
{
    private int _max = 300;
    public ObservableCollection<LogLine> Lines { get; } = new();

    public override void Configure(JsonElement opts)
    {
        base.Configure(opts);
        var m = StatsVm.Num(opts, "max");
        _max = double.IsNaN(m) ? 300 : Math.Clamp((int)m, 10, 5000);
    }

    public override void Apply(string op, JsonElement data)
    {
        switch (op)
        {
            case "clear": Lines.Clear(); break;
            case "append": Append(data); break;
            case "many":
                Lines.Clear();
                if (data.ValueKind == JsonValueKind.Array) foreach (var e in data.EnumerateArray()) Append(e);
                break;
        }
    }

    private void Append(JsonElement e)
    {
        var t = StatsVm.Num(e, "time");
        var time = double.IsNaN(t) ? DateTime.Now : DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime;
        Lines.Add(new LogLine(time, StatsVm.Str(e, "text") ?? "", ParseBrush(StatsVm.Str(e, "color"))));
        while (Lines.Count > _max) Lines.RemoveAt(0);
    }
}

public sealed record ChartSeriesDef(string Name, Brush Brush, TimeSeries Data);

public sealed partial class ChartWidgetVm : LuaWidgetVm
{
    [ObservableProperty] private string? unit;
    public ObservableCollection<ChartSeriesDef> Series { get; } = new();
    public event Action? Updated;

    public override void Configure(JsonElement opts)
    {
        base.Configure(opts);
        Unit = StatsVm.Str(opts, "unit");
        Series.Clear();
        int i = 0;
        if (opts.TryGetProperty("series", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            foreach (var def in s.EnumerateArray())
            {
                i++;
                var brush = ParseBrush(StatsVm.Str(def, "color")) ?? ThemeManager.GetBrush("Chart" + ((i - 1) % 6 + 1));
                Series.Add(new ChartSeriesDef(StatsVm.Str(def, "name") ?? $"Series {i}", brush, new TimeSeries(StatsVm.Str(def, "name") ?? "", 5000)));
            }
        }
        if (Series.Count == 0) Series.Add(new ChartSeriesDef("Value", ThemeManager.GetBrush("Chart1"), new TimeSeries("Value", 5000)));
    }

    public override void Apply(string op, JsonElement data)
    {
        switch (op)
        {
            case "clear":
                foreach (var s in Series) s.Data.Clear();
                break;
            case "push":
                Push(data);
                break;
            case "many":
                foreach (var s in Series) s.Data.Clear();
                if (data.ValueKind == JsonValueKind.Array) foreach (var e in data.EnumerateArray()) Push(e);
                break;
        }
        Updated?.Invoke();
    }

    private void Push(JsonElement e)
    {
        var t = StatsVm.Num(e, "time");
        if (double.IsNaN(t)) t = StatsVm.Now();
        if (!e.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) return;
        int i = 0;
        foreach (var v in values.EnumerateArray())
        {
            if (i >= Series.Count) break;
            Series[i++].Data.Add(t, v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN);
        }
    }
}

public sealed record LuaButton(string Id, string Text, string Style, string? Confirm);

public sealed class ButtonsWidgetVm : LuaWidgetVm
{
    public ObservableCollection<LuaButton> Buttons { get; } = new();

    public override void Configure(JsonElement opts)
    {
        base.Configure(opts);
        if (opts.TryGetProperty("buttons", out var b)) Load(b);
    }

    public override void Apply(string op, JsonElement data)
    {
        if (op == "set") Load(data);
        else if (op == "clear") Buttons.Clear();
    }

    private void Load(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return;
        Buttons.Clear();
        foreach (var b in list.EnumerateArray())
        {
            var id = StatsVm.Str(b, "id");
            if (id == null) continue;
            Buttons.Add(new LuaButton(id, StatsVm.Str(b, "text") ?? id, StatsVm.Str(b, "style") ?? "default", StatsVm.Str(b, "confirm")));
        }
    }

    public void Press(LuaButton b) => Tab?.ActionSink?.Invoke(Tab.Id, Id, b.Id);
}

/// <summary>A text item of the status bar added by an addon or a plugin.</summary>
public sealed partial class StatusItemVm : ObservableObject, BetterConsole.Sdk.IStatusItem
{
    public StatusItemVm(string id, int order) { Id = id; Order = order; }

    public string Id { get; }
    public int Order { get; }
    [ObservableProperty] private string text = "";
    [ObservableProperty] private string? tooltip;
    [ObservableProperty] private bool visible = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Foreground))] private uint argb;

    public Brush? Foreground => Argb == 0 ? null : new SolidColorBrush(Color.FromArgb(255, (byte)(Argb >> 16), (byte)(Argb >> 8), (byte)Argb));
}

public sealed record ToastVm(string Text, BetterConsole.Sdk.NotifyKind Kind)
{
    public string Icon => Kind switch
    {
        BetterConsole.Sdk.NotifyKind.Success => "",
        BetterConsole.Sdk.NotifyKind.Warning => "",
        BetterConsole.Sdk.NotifyKind.Error => "",
        _ => "",
    };
    public string ColorKey => Kind switch
    {
        BetterConsole.Sdk.NotifyKind.Success => "Brush.Success",
        BetterConsole.Sdk.NotifyKind.Warning => "Brush.Warning",
        BetterConsole.Sdk.NotifyKind.Error => "Brush.Danger",
        _ => "Brush.Accent",
    };
}
