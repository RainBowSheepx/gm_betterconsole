using System.Globalization;
using System.Text.Json;

namespace BetterConsole.App.ViewModels;

/// <summary>Reading what Lua's util.TableToJSON makes of the companion's tables.</summary>
public static class LuaJson
{
    /// <summary>
    /// The items of a Lua list: a JSON array, or an object with the indexes as its keys (TableToJSON makes one of a
    /// list with holes), in their order.
    /// </summary>
    public static List<JsonElement> Items(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Array => e.EnumerateArray().ToList(),
        JsonValueKind.Object => e.EnumerateObject().OrderBy(p => int.TryParse(p.Name, out var n) ? n : int.MaxValue).Select(p => p.Value).ToList(),
        _ => [],
    };

    /// <summary>A value as text, numbers without the culture's decimal comma and without rounding ("0.0625", "true").</summary>
    public static string Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => NumberText(e.GetDouble()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    /// <summary>
    /// A list of choices: values, or pairs { "shown text", value } (or { text = ..., value = ... }).
    /// Null when there is no list.
    /// </summary>
    public static List<(string Text, string Value)>? Choices(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var cs) || cs.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) return null;
        var choices = new List<(string, string)>();
        foreach (var c in Items(cs))
        {
            if (c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 2) choices.Add((Plain(c[0]), Plain(c[1])));
            else if (c.ValueKind == JsonValueKind.Object) choices.Add((Plain(c.TryGetProperty("text", out var t) ? t : default), Plain(c.TryGetProperty("value", out var v) ? v : default)));
            else choices.Add((Plain(c), Plain(c)));
        }
        return choices;
    }

    public static bool Flag(JsonElement m, string name, bool fallback) =>
        m.ValueKind == JsonValueKind.Object && m.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback }
            : fallback;

    private static readonly System.Text.RegularExpressions.Regex Grouped = new(@"^[-+]?\d{1,3}(,\d{3})+(\.\d*)?$");

    /// <summary>
    /// A number typed by the user. A point is the decimal point; a comma is the decimal comma where the culture
    /// writes one ("1,5") and nothing else is in the number, else it groups thousands ("3,000", "1,234.5") —
    /// anything else with a comma is no number. NaN, infinities and "1e400" are none.
    /// </summary>
    public static bool TryNumber(string text, out double n)
    {
        n = double.NaN;
        var s = text.Trim();
        if (s.Contains(','))
        {
            bool decimalComma = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator == ",";
            if (decimalComma && !s.Contains('.') && s.Count(c => c == ',') == 1) s = s.Replace(',', '.');
            else if (Grouped.IsMatch(s)) s = s.Replace(",", "");
            else return false;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n);
    }

    /// <summary>A number for a text box: "500", "0.25".</summary>
    public static string NumberText(double n) => n.ToString("0.##########", CultureInfo.InvariantCulture);
}
