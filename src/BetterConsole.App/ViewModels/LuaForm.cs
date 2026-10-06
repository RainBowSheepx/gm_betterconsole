using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterConsole.App.ViewModels;

/// <summary>
/// A field of an addon's form (tab:Form): bound to a console variable of the server or holding a value of the
/// addon's own. The companion sends its value, again whenever it changes (also from the console, rcon or other
/// addons, once a second while the tab is shown).
/// </summary>
public sealed partial class FormFieldVm : ObservableObject
{
    public required string Id { get; init; }
    /// <summary>"bool", "number", "text", "choice", "header" or "note".</summary>
    public required string Type { get; init; }
    public string Label { get; init; } = "";
    public string? Help { get; init; }
    public string? Note { get; init; }
    public string? Unit { get; init; }
    public string? Convar { get; init; }
    public string? Confirm { get; init; }
    /// <summary>The default value (of the variable, or the addon's), or null when there is none.</summary>
    public string? Default { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public IReadOnlyList<(string Text, string Value)> Choices { get; init; } = [];
    public bool Editable { get; init; }
    public bool ReadOnly { get; init; }
    /// <summary>The variable is not on this server (the addon asked to show it anyway).</summary>
    public bool Missing { get; init; }
    /// <summary>FCVAR_PROTECTED: its value is not sent and it cannot be changed.</summary>
    public bool Protected { get; init; }

    /// <summary>The value on the server, as text ("1", "500", "on").</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private string? value;

    public bool IsInput => Type is not ("header" or "note");
    public bool CanChange => IsInput && !ReadOnly && !Missing && !Protected;
    /// <summary>Not at its default: marked, with a button to reset it.</summary>
    public bool IsModified => CanChange && Default != null && Value != null && !Same(Value, Default);

    /// <summary>A value came from the server, also the same one again (a change it refused): controls show it.</summary>
    public event Action? ServerValue;

    internal void FromServer(string v)
    {
        Value = v;
        ServerValue?.Invoke();
    }

    /// <summary>Equal as this kind of value: 500 and 500.0, 1 and true.</summary>
    public bool Same(string a, string b) => Type switch
    {
        "number" => LuaJson.TryNumber(a, out var x) && LuaJson.TryNumber(b, out var y) ? Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x)) : a.Trim() == b.Trim(),
        "bool" => Truthy(a) == Truthy(b),
        _ => a == b,
    };

    /// <summary>How a variable is on: anything but "0", "false", "off", "no" and empty (0.0 is off too).</summary>
    public static bool Truthy(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (LuaJson.TryNumber(s, out var n)) return n != 0;
        return !(s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals("off", StringComparison.OrdinalIgnoreCase) || s.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What the user typed or chose, as the companion gets it (a number, true/false, a string); null when it is
    /// good, else what is wrong with it.
    /// </summary>
    public string? Check(string input, out object typed)
    {
        typed = input;
        switch (Type)
        {
            case "bool":
                typed = Truthy(input);
                return null;
            case "number":
                if (!LuaJson.TryNumber(input, out var n)) return "A number is needed.";
                if (Min is { } mn && n < mn || Max is { } mx && n > mx) return RangeText + ".";
                typed = n;
                return null;
            default:
                if (Convar != null && input.IndexOfAny(['"', '\r', '\n']) >= 0) return "Quotes and line breaks cannot be used in a console variable.";
                if (Type == "choice" && !Editable && Choices.Count > 0 && Choices.All(c => c.Value != input)) return "Choose one of the list.";
                return null;
        }
    }

    /// <summary>"between 0 and 20000", "at least 1" …, or empty.</summary>
    public string RangeText => (Min, Max) switch
    {
        ({ } a, { } b) => $"Between {LuaJson.NumberText(a)} and {LuaJson.NumberText(b)}",
        ({ } a, null) => $"At least {LuaJson.NumberText(a)}",
        (null, { } b) => $"At most {LuaJson.NumberText(b)}",
        _ => "",
    };

    /// <summary>How a value reads in the form: a choice's text, else the value.</summary>
    public string Shown(string? v)
    {
        if (v == null) return "";
        foreach (var c in Choices)
            if (Same(c.Value, v)) return c.Text;
        return v;
    }

    /// <summary>The tooltip of its row: the help, the variable, its default and range.</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(Help)) lines.Add(Help!);
            if (Convar != null) lines.Add("Console variable " + Convar + (Missing ? " (not on this server)" : Protected ? " (protected)" : ""));
            if (Default != null && IsInput) lines.Add("Default: " + (Shown(Default) is var d && d != Default ? $"{d} ({Default})" : Default));
            if (Type == "number" && RangeText.Length > 0) lines.Add(RangeText + (Unit != null ? " " + Unit : ""));
            return string.Join("\n", lines);
        }
    }
}

/// <summary>tab:Form: settings of an addon, a row per field.</summary>
public sealed class FormWidgetVm : LuaWidgetVm
{
    private string? _fieldsJson;

    public ObservableCollection<FormFieldVm> Fields { get; } = new();

    public override void Configure(JsonElement opts)
    {
        base.Configure(opts);
        if (opts.ValueKind != JsonValueKind.Object || !opts.TryGetProperty("fields", out var fs)) fs = default;
        // The same fields again (a reconnect, an addon that defines its form again): the rows stay as they are.
        var json = fs.ValueKind == JsonValueKind.Undefined ? "" : fs.GetRawText();
        if (json == _fieldsJson) return;
        _fieldsJson = json;
        var values = Fields.Where(f => f.Value != null).ToDictionary(f => f.Id, f => f.Value!);
        Fields.Clear();
        foreach (var f in LuaJson.Items(fs))
        {
            if (f.ValueKind != JsonValueKind.Object || StatsVm.Str(f, "id") is not { Length: > 0 } id || Fields.Any(x => x.Id == id)) continue;
            var field = new FormFieldVm
            {
                Id = id,
                Type = StatsVm.Str(f, "type") is { } t && t is "bool" or "number" or "text" or "choice" or "header" or "note" ? t : "text",
                Label = StatsVm.Str(f, "text") ?? StatsVm.Str(f, "convar") ?? id,
                Help = StatsVm.Str(f, "help"),
                Note = StatsVm.Str(f, "note"),
                Unit = StatsVm.Str(f, "unit"),
                Convar = StatsVm.Str(f, "convar"),
                Confirm = StatsVm.Str(f, "confirm"),
                Default = f.TryGetProperty("default", out var d) && d.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? LuaJson.Plain(d) : null,
                Min = Finite(StatsVm.Num(f, "min")),
                Max = Finite(StatsVm.Num(f, "max")),
                Step = Finite(StatsVm.Num(f, "step")) is { } st && st > 0 ? st : null,
                Choices = LuaJson.Choices(f, "choices") ?? [],
                Editable = LuaJson.Flag(f, "editable", false),
                ReadOnly = LuaJson.Flag(f, "readonly", false),
                Missing = LuaJson.Flag(f, "missing", false),
                Protected = LuaJson.Flag(f, "protected", false),
            };
            if (values.TryGetValue(id, out var v)) field.Value = v;
            Fields.Add(field);
        }
    }

    private static double? Finite(double v) => double.IsFinite(v) ? v : null;

    public override void Apply(string op, JsonElement data)
    {
        if (op == "clear")
        {
            foreach (var f in Fields) f.Value = null;
            return;
        }
        if (op != "set" || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) return;
        foreach (var p in values.EnumerateObject())
            if (Fields.FirstOrDefault(f => f.Id == p.Name) is { } field) field.FromServer(LuaJson.Plain(p.Value));
    }

    /// <summary>Sends a new value of a field to the companion (it checks it again and sets the variable or calls the addon).</summary>
    public void Change(FormFieldVm field, object value) =>
        Tab?.Send?.Invoke("form", new { tab = Tab.Id, widget = Id, field = field.Id, value });

    /// <summary>For tests and the UI script runner: a field by its id.</summary>
    public FormFieldVm? Field(string id) => Fields.FirstOrDefault(f => f.Id == id);
}
