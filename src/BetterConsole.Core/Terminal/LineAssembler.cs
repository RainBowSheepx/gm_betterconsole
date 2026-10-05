using System.Text;
using BetterConsole.Sdk;

namespace BetterConsole.Core.Terminal;

/// <summary>A logical line rebuilt from the screen (soft-wrapped rows joined).</summary>
internal readonly record struct AssembledLine(long Id, string Text, ColorSpan[] Spans, bool IsAmend);

/// <summary>
/// Turns screen rows into logical lines. A line is reported when an explicit line feed leaves it;
/// a row that changes after it was reported (ConPTY's soft-wrap trick rewrites the last cell of the
/// previous row) is reported again as an amendment of the same line id.
/// </summary>
internal sealed class LineAssembler : ITerminalListener
{
    private sealed class LineRecord
    {
        public long Id;
        public bool Reported;
        public string LastText = "";
        /// <summary>Cells of rows of this line that already scrolled off the top of the screen.</summary>
        public List<char>? PrefixChars;
        public List<ushort>? PrefixStyles;
    }

    private readonly OemTextRestorer _restorer;
    private readonly List<AssembledLine> _out = new();
    private readonly StringBuilder _text = new(512);
    private readonly List<ushort> _styles = new(512);
    private readonly List<char> _cells = new(512);
    private readonly List<ushort> _cellStyles = new(512);
    private long _nextId = 1;

    public LineAssembler(OemTextRestorer restorer) => _restorer = restorer;

    public TerminalEmulator Terminal { get; set; } = null!;

    public string? Title { get; private set; }
    public bool TitleChanged { get; set; }

    /// <summary>Lines produced since the last call. The list is reused; copy what you keep.</summary>
    public List<AssembledLine> TakeLines() => _out;

    public void OnTitle(string title)
    {
        Title = title;
        TitleChanged = true;
    }

    public void OnLineFeed(TerminalEmulator t, int row)
    {
        int start = row;
        while (start > 0 && t.GetRow(start - 1).Wrapped) start--;
        ReportLine(t, start, row);
    }

    public void OnRowEvicted(TerminalEmulator t, TerminalEmulator.Row row)
    {
        if (!row.Wrapped) return;
        // The logical line continues on the next row: keep the evicted cells as its prefix.
        var rec = row.Tag as LineRecord ?? new LineRecord { Id = _nextId++ };
        rec.PrefixChars ??= new List<char>();
        rec.PrefixStyles ??= new List<ushort>();
        for (int i = 0; i < row.Length; i++)
        {
            rec.PrefixChars.Add(row.Chars[i]);
            rec.PrefixStyles.Add(row.Styles[i]);
        }
        t.GetRow(1).Tag = rec;
    }

    /// <summary>Re-reports lines whose rows changed after they were reported. Call after every fed chunk.</summary>
    public void CheckAmendments(TerminalEmulator t)
    {
        for (int r = 0; r < t.Height; r++)
        {
            var row = t.GetRow(r);
            if (!row.Dirty) continue;
            if (row.Tag is not LineRecord { Reported: true })
            {
                row.Dirty = false;
                continue;
            }
            // Only rows above the cursor are finished; the cursor row is reported by its line feed.
            // A reported line the cursor went back to stays dirty until the cursor has left it.
            int start = r, end = r;
            while (start > 0 && t.GetRow(start - 1).Wrapped) start--;
            while (end < t.Height - 1 && t.GetRow(end).Wrapped) end++;
            if (end >= t.CursorRow) continue;
            ReportLine(t, start, end);
        }
    }

    /// <summary>Text of the unfinished line under the cursor (for the live preview), or null.</summary>
    public (string Text, ColorSpan[] Spans)? Preview(TerminalEmulator t)
    {
        int row = t.CursorRow;
        int start = row;
        while (start > 0 && t.GetRow(start - 1).Wrapped) start--;
        bool any = false;
        for (int r = start; r <= row; r++) any |= t.GetRow(r).Length > 0;
        if (!any) return null;
        Build(t, start, row, t.GetRow(start).Tag as LineRecord);
        return (_text.ToString(), BuildSpans(t));
    }

    private void ReportLine(TerminalEmulator t, int start, int end)
    {
        var rec = t.GetRow(start).Tag as LineRecord;
        if (rec == null)
        {
            rec = new LineRecord { Id = _nextId++ };
        }
        for (int r = start; r <= end; r++)
        {
            var row = t.GetRow(r);
            row.Tag = rec;
            row.Dirty = false;
        }

        Build(t, start, end, rec);
        string text = _text.ToString();
        if (rec.Reported && text == rec.LastText) return;
        var spans = BuildSpans(t);
        _out.Add(new AssembledLine(rec.Id, text, spans, rec.Reported));
        rec.Reported = true;
        rec.LastText = text;
    }

    private void Build(TerminalEmulator t, int start, int end, LineRecord? rec)
    {
        _cells.Clear();
        _cellStyles.Clear();
        if (rec?.PrefixChars != null && start == 0)
        {
            _cells.AddRange(rec.PrefixChars);
            _cellStyles.AddRange(rec.PrefixStyles!);
        }
        for (int r = start; r <= end; r++)
        {
            var row = t.GetRow(r);
            int len = row.Length;
            if (r < end && row.Wrapped)
            {
                // A wrapped row is full width by definition.
                len = t.Width;
            }
            for (int i = 0; i < len; i++)
            {
                _cells.Add(i < row.Length ? row.Chars[i] : ' ');
                _cellStyles.Add(i < row.Length ? row.Styles[i] : (ushort)0);
            }
        }
        // Trailing blanks are an artefact of the screen, not part of the line.
        int n = _cells.Count;
        while (n > 0 && _cells[n - 1] == ' ' && t.GetStyle(_cellStyles[n - 1]).Bg == 0) n--;

        _text.Clear();
        _styles.Clear();
        _restorer.Restore(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_cells)[..n],
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_cellStyles)[..n],
            _text, _styles);
    }

    private ColorSpan[] BuildSpans(TerminalEmulator t)
    {
        if (_styles.Count == 0) return Array.Empty<ColorSpan>();
        var spans = new List<ColorSpan>(4);
        int runStart = 0;
        ushort cur = _styles[0];
        for (int i = 1; i <= _styles.Count; i++)
        {
            if (i < _styles.Count && _styles[i] == cur) continue;
            var st = t.GetStyle(cur);
            uint fg = (st.Flags & CellFlags.Inverse) != 0 ? (st.Bg == 0 ? 0xFF0C0C0C : st.Bg) : st.Fg;
            bool bold = (st.Flags & CellFlags.Bold) != 0;
            if (fg != 0 || bold) spans.Add(new ColorSpan(runStart, i - runStart, fg, bold));
            if (i < _styles.Count)
            {
                runStart = i;
                cur = _styles[i];
            }
        }
        // Merge neighbours with the same colour (common after style interning of equal colours).
        if (spans.Count > 1)
        {
            var merged = new List<ColorSpan>(spans.Count) { spans[0] };
            for (int i = 1; i < spans.Count; i++)
            {
                var last = merged[^1];
                var s = spans[i];
                if (last.Start + last.Length == s.Start && last.Argb == s.Argb && last.Bold == s.Bold)
                    merged[^1] = last with { Length = last.Length + s.Length };
                else merged.Add(s);
            }
            spans = merged;
        }
        return spans.ToArray();
    }
}
