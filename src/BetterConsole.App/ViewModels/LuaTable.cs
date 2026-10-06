using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterConsole.App.ViewModels;

/// <summary>A column of an addon table: its title, alignment ("left", "right", "center") and width ("*", "2*", "auto" or pixels).</summary>
public sealed record TableColumn(string Text, string Align = "left", string Width = "*");

/// <summary>
/// A row of an addon table. Updated in place when the addon sends it again (same key, or the same position), so
/// the table keeps its selection and scroll position; the cells are bound by index ("[0]").
/// </summary>
public sealed class TableRowVm : INotifyPropertyChanged
{
    private string[] _cells = [];
    private double[] _numbers = [];

    public string this[int i] => i >= 0 && i < _cells.Length ? _cells[i] : "";

    /// <summary>The cell as a number for sorting ("12", "41 %", "1.5 ms"), else NaN.</summary>
    public double Number(int i) => i >= 0 && i < _numbers.Length ? _numbers[i] : double.NaN;

    public int CellCount => _cells.Length;

    /// <summary>Its key (the key column's text) or its position, with "#2" … for repeated keys.</summary>
    public string Key { get; internal set; } = "";

    /// <summary>Where the addon put it this time (the order when no column is sorted).</summary>
    public int Index { get; internal set; }

    /// <summary>True when something changed.</summary>
    internal bool Set(string[] cells, double[] numbers)
    {
        if (cells.AsSpan().SequenceEqual(_cells)) return false;
        _cells = cells;
        _numbers = numbers;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => string.Join(" | ", _cells);
}

/// <summary>
/// tab:Table: rows of cells. With a key column the rows the addon sends again are updated in place, new ones are
/// added and missing ones removed; without one, by position. A click on a header sorts by that column (numbers as
/// numbers); while the pointer is over the table (<see cref="Hold"/>) the order stays where it is.
/// </summary>
public sealed partial class TableWidgetVm : LuaWidgetVm
{
    private List<TableRowVm> _data = new();
    private bool _reorderPending;
    private string? _sortSpec;

    [ObservableProperty] private TableColumn[] columns = [];
    /// <summary>The rows in the order shown.</summary>
    public ObservableCollection<TableRowVm> Rows { get; } = new();
    /// <summary>The key column (0-based), or -1: rows are matched by their position.</summary>
    public int KeyColumn { get; private set; } = -1;
    /// <summary>The key column when the options name none (the tables of an addon's profiler: their first one).</summary>
    public int DefaultKey { get; init; } = -1;
    /// <summary>Paths of Lua files in the cells open in the editor (double-click).</summary>
    [ObservableProperty] private bool links;
    /// <summary>The column sorted by (0-based), or -1: the addon's order.</summary>
    [ObservableProperty] private int sortColumn = -1;
    [ObservableProperty] private bool sortDescending;

    /// <summary>The pointer is over the table: the rows keep their places (new ones go to the end) until it leaves.</summary>
    public bool Hold
    {
        get => _hold;
        set
        {
            if (_hold == value) return;
            _hold = value;
            if (!value && _reorderPending) Reorder();
        }
    }
    private bool _hold;

    public override void Configure(JsonElement opts)
    {
        base.Configure(opts);
        if (opts.ValueKind != JsonValueKind.Object) return;
        // The same columns again (each report of a profiler, a table defined again) leave the grid's columns alone.
        if (opts.TryGetProperty("columns", out var cols) && cols.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            && LuaJson.Items(cols).Select(ParseColumn).ToArray() is var parsed && !parsed.SequenceEqual(Columns))
            Columns = parsed;
        var key = StatsVm.Num(opts, "key");
        int keyColumn = double.IsNaN(key) || key < 1 ? DefaultKey : (int)key - 1;
        if (keyColumn != KeyColumn)
        {
            KeyColumn = keyColumn;
            _data.Clear();
            Rows.Clear();
        }
        Links = LuaJson.Flag(opts, "links", false);
        // The sort it starts with; the same one again (each report of a profiler) leaves the user's choice alone.
        if (opts.TryGetProperty("sort", out var sort) && sort.ValueKind == JsonValueKind.Object && sort.GetRawText() != _sortSpec)
        {
            _sortSpec = sort.GetRawText();
            var c = StatsVm.Num(sort, "column");
            SortColumn = double.IsNaN(c) || c < 1 ? -1 : (int)c - 1;
            SortDescending = LuaJson.Flag(sort, "descending", false);
            Reorder();
        }
    }

    private static TableColumn ParseColumn(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return new TableColumn(Text(c));
        var align = (StatsVm.Str(c, "align") ?? "left").ToLowerInvariant();
        var width = c.TryGetProperty("width", out var w) ? w.ValueKind switch
        {
            JsonValueKind.Number => Math.Clamp(w.GetDouble(), 20, 2000).ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => w.GetString() ?? "*",
            _ => "*",
        } : "*";
        return new TableColumn(StatsVm.Str(c, "text") ?? "", align is "right" or "center" ? align : "left", width);
    }

    public override void Apply(string op, JsonElement data)
    {
        if (op == "clear")
        {
            _data.Clear();
            Rows.Clear();
            return;
        }
        if (op == "set" && data.ValueKind is JsonValueKind.Array or JsonValueKind.Object) Update(LuaJson.Items(data).Take(2000));
    }

    /// <summary>The rows as the addon sent them: kept rows are updated in place, the order follows the sort.</summary>
    public void Update(IEnumerable<JsonElement> rows)
    {
        int n = Math.Max(Columns.Length, 1);
        var incoming = rows.Select(r => Cells(r, n)).ToList();
        var next = new List<TableRowVm>(incoming.Count);
        if (KeyColumn >= 0)
        {
            var old = _data.ToDictionary(r => r.Key);
            var repeats = new Dictionary<string, int>();
            foreach (var (cells, numbers) in incoming)
            {
                var key = KeyColumn < cells.Length ? cells[KeyColumn] : "";
                int k = repeats[key] = repeats.GetValueOrDefault(key) + 1;
                if (k > 1) key += "\0#" + k;
                if (!old.Remove(key, out var row)) row = new TableRowVm { Key = key };
                row.Set(cells, numbers);
                next.Add(row);
            }
            foreach (var gone in old.Values) Rows.Remove(gone);
        }
        else
        {
            for (int i = 0; i < incoming.Count; i++)
            {
                var row = i < _data.Count ? _data[i] : new TableRowVm { Key = i.ToString(CultureInfo.InvariantCulture) };
                row.Set(incoming[i].Cells, incoming[i].Numbers);
                next.Add(row);
            }
            for (int i = incoming.Count; i < _data.Count; i++) Rows.Remove(_data[i]);
        }
        for (int i = 0; i < next.Count; i++) next[i].Index = i;
        var shown = new HashSet<TableRowVm>(Rows);
        foreach (var row in next)
            if (!shown.Contains(row)) Rows.Add(row);
        _data = next;
        Reorder();
    }

    /// <summary>A click on a column's header: sorted by it, the second click the other way round.</summary>
    public void SortBy(int column)
    {
        if (column == SortColumn) SortDescending = !SortDescending;
        else
        {
            SortColumn = column;
            // Numbers biggest first, text from A.
            SortDescending = _data.Count(r => !double.IsNaN(r.Number(column))) * 2 > _data.Count;
        }
        Reorder(force: true);
    }

    /// <summary>Moves the rows into the order of the sort (or the addon's); waits while the pointer is over the table.</summary>
    public void Reorder(bool force = false)
    {
        if (Hold && !force)
        {
            _reorderPending = true;
            return;
        }
        _reorderPending = false;
        var wanted = _data.ToList();
        if (SortColumn >= 0)
        {
            int col = SortColumn;
            bool descending = SortDescending;
            wanted.Sort((a, b) =>
            {
                int c = Compare(a, b, col, descending);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            });
        }
        // Moves keep each row (its selection, the scroll position), unlike removing and adding it again.
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], wanted[i])) continue;
            int j = Rows.IndexOf(wanted[i]);
            if (j >= 0) Rows.Move(j, i);
            else Rows.Insert(i, wanted[i]);
        }
        while (Rows.Count > wanted.Count) Rows.RemoveAt(Rows.Count - 1);
    }

    /// <summary>Numbers as numbers, text without regard to case; cells without a number after those with one either way.</summary>
    public static int Compare(TableRowVm a, TableRowVm b, int col, bool descending = false)
    {
        double x = a.Number(col), y = b.Number(col);
        bool nx = !double.IsNaN(x), ny = !double.IsNaN(y);
        if (nx != ny) return nx ? -1 : 1;
        int c = nx ? x.CompareTo(y) : string.Compare(a[col], b[col], StringComparison.CurrentCultureIgnoreCase);
        return descending ? -c : c;
    }

    private static (string[] Cells, double[] Numbers) Cells(JsonElement row, int n)
    {
        var cells = new string[n];
        var numbers = new double[n];
        Array.Fill(numbers, double.NaN);
        if (row.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var cell in row.EnumerateArray())
            {
                if (i >= n) break;
                Cell(i++, cell);
            }
        }
        else if (row.ValueKind == JsonValueKind.Object)
        {
            // A row with a nil in it ({ "Alice", nil, "Mayor" }): TableToJSON keys the cells by their index.
            foreach (var p in row.EnumerateObject())
                if (int.TryParse(p.Name, out var k) && k >= 1 && k <= n) Cell(k - 1, p.Value);
        }
        else
        {
            cells[0] = Text(row);
            numbers[0] = row.ValueKind == JsonValueKind.Number ? row.GetDouble() : LeadingNumber(cells[0]);
        }
        for (int i = 0; i < n; i++) cells[i] ??= "";
        return (cells, numbers);

        void Cell(int i, JsonElement cell)
        {
            cells[i] = Text(cell);
            numbers[i] = cell.ValueKind == JsonValueKind.Number ? cell.GetDouble() : LeadingNumber(cells[i]);
        }
    }

    // A sign, a currency sign, digits in groups of three (string.Comma: "1,234,567") or not, decimals, then the end, a space, % or a unit.
    private static readonly Regex Leading = new(@"^\s*([-+]?)[$€£¥₽]?\s*((?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?|\.\d+)(?:\s|%|$|[a-zA-Zµ/])", RegexOptions.Compiled);

    /// <summary>"41 %", "12.5 ms", "-3", "$1,200", "1,234,567" → their number; text that does not start with one → NaN.</summary>
    public static double LeadingNumber(string s)
    {
        var m = Leading.Match(s);
        return m.Success && double.TryParse(m.Groups[1].Value + m.Groups[2].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }
}
