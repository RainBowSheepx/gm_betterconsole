using System.Text;

namespace BetterConsole.Core.Terminal;

/// <summary>
/// A small VT terminal emulator: just enough of xterm/ConPTY to rebuild the line stream that srcds
/// writes. ConPTY does not forward the raw text of the program; it sends a rendering of the console
/// screen (cursor moves, erase, SGR colours, its soft-wrap trick). This class replays that rendering
/// on a screen buffer and reports finished rows to an <see cref="ITerminalListener"/>.
/// </summary>
/// <remarks>Not thread safe: feed it from one thread.</remarks>
internal sealed class TerminalEmulator
{
    internal sealed class Row
    {
        public readonly char[] Chars;
        public readonly ushort[] Styles;
        /// <summary>One past the last written cell.</summary>
        public int Length;
        /// <summary>The text continues on the next row (auto wrap happened at the right margin).</summary>
        public bool Wrapped;
        /// <summary>Changed since the listener last looked at it.</summary>
        public bool Dirty;
        /// <summary>Opaque per-row data owned by the listener (the logical line this row belongs to).</summary>
        public object? Tag;

        public Row(int width)
        {
            Chars = new char[width];
            Styles = new ushort[width];
        }

        public void Clear()
        {
            Length = 0;
            Wrapped = false;
            Dirty = true;
            Tag = null;
        }
    }

    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, OscEscape, String, StringEscape }

    private readonly ITerminalListener _listener;
    private readonly StyleTable _styles = new();
    private readonly Row[] _rows;
    private int _row, _col;
    private bool _pendingWrap;
    private int _savedRow, _savedCol;
    private CellStyle _style = CellStyle.Default;
    private ushort _styleId;

    private State _state = State.Ground;
    private readonly List<int> _params = new(16);
    private int _currentParam = -1;
    private char _privateMarker;
    private readonly StringBuilder _osc = new();

    public TerminalEmulator(int width, int height, ITerminalListener listener)
    {
        if (width < 8 || height < 2) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        _listener = listener;
        _rows = new Row[height];
        for (int i = 0; i < height; i++) _rows[i] = new Row(width);
    }

    public int Width { get; }
    public int Height { get; }
    public int CursorRow => _row;
    public int CursorColumn => _col;

    internal Row GetRow(int index) => _rows[index];
    internal CellStyle GetStyle(ushort id) => _styles[id];

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (char c in text) Step(c);
    }

    private void Step(char c)
    {
        switch (_state)
        {
            case State.Ground:
                if (c >= ' ' && c != '\x7f') Print(c);
                else Control(c);
                break;

            case State.Escape:
                EscapeChar(c);
                break;

            case State.EscapeIntermediate:
                // ESC ( B, ESC # 8 ... : one more byte, then back to normal.
                if (c >= '0' && c <= '~') _state = State.Ground;
                else if (c == '\x1b') _state = State.Escape;
                break;

            case State.Csi:
                CsiChar(c);
                break;

            case State.Osc:
                if (c == '\x07') { OscDone(); }
                else if (c == '\x1b') _state = State.OscEscape;
                else if (_osc.Length < 4096) _osc.Append(c);
                break;

            case State.OscEscape:
                if (c == '\\') OscDone();
                else { _state = State.Osc; if (_osc.Length < 4096) _osc.Append(c); }
                break;

            case State.String:
                // DCS / SOS / PM / APC: ignored until ST.
                if (c == '\x1b') _state = State.StringEscape;
                else if (c == '\x07') _state = State.Ground;
                break;

            case State.StringEscape:
                _state = c == '\\' ? State.Ground : State.String;
                break;
        }
    }

    private void Control(char c)
    {
        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                break;
            case '\r':
                _col = 0;
                _pendingWrap = false;
                break;
            case '\n':
            case '\v':
            case '\f':
                LineFeed(explicitNewLine: true);
                break;
            case '\b':
                if (_pendingWrap) _pendingWrap = false;
                else if (_col > 0) _col--;
                break;
            case '\t':
                _col = Math.Min(Width - 1, (_col / 8 + 1) * 8);
                _pendingWrap = false;
                break;
            // BEL, NUL, SO/SI, DEL...: nothing to draw.
        }
    }

    private void EscapeChar(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _state = State.Csi;
                _params.Clear();
                _currentParam = -1;
                _privateMarker = '\0';
                break;
            case ']':
                _state = State.Osc;
                _osc.Clear();
                break;
            case 'P': case 'X': case '^': case '_':
                _state = State.String;
                break;
            case '7':
                _savedRow = _row; _savedCol = _col;
                break;
            case '8':
                _row = _savedRow; _col = _savedCol; _pendingWrap = false;
                break;
            case 'D':
                LineFeed(explicitNewLine: true);
                break;
            case 'E':
                _col = 0;
                LineFeed(explicitNewLine: true);
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'c':
                Reset();
                break;
            case '(': case ')': case '*': case '+': case '#': case '%':
                _state = State.EscapeIntermediate;
                break;
            case '\x1b':
                _state = State.Escape;
                break;
        }
    }

    private void CsiChar(char c)
    {
        if (c >= '0' && c <= '9')
        {
            if (_currentParam < 0) _currentParam = 0;
            if (_currentParam < 100000) _currentParam = _currentParam * 10 + (c - '0');
            return;
        }
        if (c == ';' || c == ':')
        {
            _params.Add(Math.Max(_currentParam, -1));
            _currentParam = -1;
            return;
        }
        if (c is '?' or '>' or '<' or '=')
        {
            _privateMarker = c;
            return;
        }
        if (c >= ' ' && c <= '/') return; // intermediates: ignored
        if (c == '\x1b') { _state = State.Escape; return; }
        if (c < ' ') { Control(c); return; } // C0 controls are executed inside CSI too

        _params.Add(_currentParam);
        _state = State.Ground;
        if (_privateMarker != '\0')
        {
            // DEC private modes (?25h cursor, ?1004h focus events, ?9001h win32 input, ...): nothing to emulate.
            return;
        }
        Dispatch(c);
    }

    private int Param(int index, int fallback)
    {
        if (index >= _params.Count) return fallback;
        int v = _params[index];
        return v <= 0 ? fallback : v;
    }

    private int RawParam(int index) => index < _params.Count ? _params[index] : -1;

    private void Dispatch(char final)
    {
        switch (final)
        {
            case 'm': Sgr(); break;
            case 'H':
            case 'f':
                MoveTo(Param(0, 1) - 1, Param(1, 1) - 1);
                break;
            case 'A': MoveTo(_row - Param(0, 1), _col); break;
            case 'B': case 'e': MoveTo(_row + Param(0, 1), _col); break;
            case 'C': case 'a': MoveTo(_row, _col + Param(0, 1)); break;
            case 'D': MoveTo(_row, _col - Param(0, 1)); break;
            case 'E': MoveTo(_row + Param(0, 1), 0); break;
            case 'F': MoveTo(_row - Param(0, 1), 0); break;
            case 'G': case '`': MoveTo(_row, Param(0, 1) - 1); break;
            case 'd': MoveTo(Param(0, 1) - 1, _col); break;
            case 'K': EraseInLine(Math.Max(RawParam(0), 0)); break;
            case 'J': EraseInDisplay(Math.Max(RawParam(0), 0)); break;
            case 'X': EraseChars(Param(0, 1)); break;
            case 'P': DeleteChars(Param(0, 1)); break;
            case '@': InsertChars(Param(0, 1)); break;
            case 'L': InsertLines(Param(0, 1)); break;
            case 'M': DeleteLines(Param(0, 1)); break;
            case 'S': for (int i = Math.Min(Param(0, 1), Height); i > 0; i--) ScrollUp(); break;
            case 'T': for (int i = Math.Min(Param(0, 1), Height); i > 0; i--) ScrollDown(); break;
            case 's': _savedRow = _row; _savedCol = _col; break;
            case 'u': _row = _savedRow; _col = _savedCol; _pendingWrap = false; break;
            // 'r' (scroll region), 'h'/'l' (modes), 'n' (reports), 't' (window ops): ignored.
        }
    }

    private void Sgr()
    {
        var s = _style;
        uint fg = s.Fg, bg = s.Bg;
        var flags = s.Flags;
        for (int i = 0; i < _params.Count; i++)
        {
            int p = Math.Max(_params[i], 0);
            switch (p)
            {
                case 0: fg = 0; bg = 0; flags = CellFlags.None; break;
                case 1: flags |= CellFlags.Bold; break;
                case 2: flags |= CellFlags.Dim; break;
                case 3: flags |= CellFlags.Italic; break;
                case 4: flags |= CellFlags.Underline; break;
                case 7: flags |= CellFlags.Inverse; break;
                case 22: flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: flags &= ~CellFlags.Italic; break;
                case 24: flags &= ~CellFlags.Underline; break;
                case 27: flags &= ~CellFlags.Inverse; break;
                case >= 30 and <= 37: fg = Palette.Color16(p - 30); break;
                case 39: fg = 0; break;
                case >= 40 and <= 47: bg = Palette.Color16(p - 40); break;
                case 49: bg = 0; break;
                case >= 90 and <= 97: fg = Palette.Color16(p - 90 + 8); break;
                case >= 100 and <= 107: bg = Palette.Color16(p - 100 + 8); break;
                case 38:
                case 48:
                {
                    uint color = 0;
                    int mode = RawParam(i + 1);
                    if (mode == 5)
                    {
                        color = Palette.Color256(Math.Max(RawParam(i + 2), 0));
                        i += 2;
                    }
                    else if (mode == 2)
                    {
                        color = Palette.Rgb(Math.Max(RawParam(i + 2), 0), Math.Max(RawParam(i + 3), 0), Math.Max(RawParam(i + 4), 0));
                        i += 4;
                    }
                    if (p == 38) fg = color; else bg = color;
                    break;
                }
            }
        }
        _style = new CellStyle(fg, bg, flags);
        _styleId = _styles.Intern(_style);
    }

    private void OscDone()
    {
        _state = State.Ground;
        var s = _osc.ToString();
        int semi = s.IndexOf(';');
        if (semi > 0)
        {
            var code = s.AsSpan(0, semi);
            if (code is "0" or "2") _listener.OnTitle(s[(semi + 1)..]);
        }
    }

    private void Print(char c)
    {
        if (_pendingWrap)
        {
            var row = _rows[_row];
            row.Wrapped = true;
            row.Dirty = true;
            _col = 0;
            _pendingWrap = false;
            LineFeed(explicitNewLine: false);
        }
        var r = _rows[_row];
        r.Chars[_col] = c;
        r.Styles[_col] = _styleId;
        if (_col >= r.Length)
        {
            // Cells between the old end and the cursor (after a cursor jump) become spaces.
            for (int i = r.Length; i < _col; i++) { r.Chars[i] = ' '; r.Styles[i] = 0; }
            r.Length = _col + 1;
        }
        r.Dirty = true;
        if (_col == Width - 1) _pendingWrap = true;
        else _col++;
    }

    private void LineFeed(bool explicitNewLine)
    {
        if (explicitNewLine)
        {
            _pendingWrap = false;
            _listener.OnLineFeed(this, _row);
        }
        if (_row == Height - 1) ScrollUp();
        else _row++;
    }

    private void ReverseIndex()
    {
        if (_row == 0) ScrollDown();
        else _row--;
        _pendingWrap = false;
    }

    private void ScrollUp()
    {
        var top = _rows[0];
        _listener.OnRowEvicted(this, top);
        Array.Copy(_rows, 1, _rows, 0, Height - 1);
        top.Clear();
        _rows[Height - 1] = top;
    }

    private void ScrollDown()
    {
        var bottom = _rows[Height - 1];
        Array.Copy(_rows, 0, _rows, 1, Height - 1);
        bottom.Clear();
        _rows[0] = bottom;
    }

    private void MoveTo(int row, int col)
    {
        _row = Math.Clamp(row, 0, Height - 1);
        _col = Math.Clamp(col, 0, Width - 1);
        _pendingWrap = false;
    }

    private void EraseInLine(int mode)
    {
        var r = _rows[_row];
        switch (mode)
        {
            case 0:
                if (_col < r.Length) { r.Length = _col; r.Dirty = true; }
                r.Wrapped = false;
                break;
            case 1:
                for (int i = 0; i <= _col && i < r.Length; i++) { r.Chars[i] = ' '; r.Styles[i] = 0; }
                r.Dirty = true;
                break;
            default:
                r.Clear();
                break;
        }
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int i = _row + 1; i < Height; i++) _rows[i].Clear();
                break;
            case 1:
                for (int i = 0; i < _row; i++) _rows[i].Clear();
                EraseInLine(1);
                break;
            default:
                foreach (var r in _rows) r.Clear();
                break;
        }
    }

    private void EraseChars(int n)
    {
        var r = _rows[_row];
        int end = Math.Min(r.Length, _col + n);
        for (int i = _col; i < end; i++) { r.Chars[i] = ' '; r.Styles[i] = 0; }
        if (end == r.Length && _col < r.Length)
        {
            // Erasing the tail is the same as shortening the row; keeps lines free of trailing blanks.
            r.Length = _col;
        }
        r.Dirty = true;
    }

    private void DeleteChars(int n)
    {
        var r = _rows[_row];
        if (_col >= r.Length) return;
        n = Math.Min(n, r.Length - _col);
        Array.Copy(r.Chars, _col + n, r.Chars, _col, r.Length - _col - n);
        Array.Copy(r.Styles, _col + n, r.Styles, _col, r.Length - _col - n);
        r.Length -= n;
        r.Dirty = true;
    }

    private void InsertChars(int n)
    {
        var r = _rows[_row];
        if (_col >= r.Length) return;
        int newLen = Math.Min(Width, r.Length + n);
        int move = newLen - _col - n;
        if (move > 0)
        {
            Array.Copy(r.Chars, _col, r.Chars, _col + n, move);
            Array.Copy(r.Styles, _col, r.Styles, _col + n, move);
        }
        for (int i = _col; i < Math.Min(_col + n, Width); i++) { r.Chars[i] = ' '; r.Styles[i] = 0; }
        r.Length = newLen;
        r.Dirty = true;
    }

    private void InsertLines(int n)
    {
        n = Math.Min(n, Height - _row);
        for (int k = 0; k < n; k++)
        {
            var bottom = _rows[Height - 1];
            Array.Copy(_rows, _row, _rows, _row + 1, Height - 1 - _row);
            bottom.Clear();
            _rows[_row] = bottom;
        }
    }

    private void DeleteLines(int n)
    {
        n = Math.Min(n, Height - _row);
        for (int k = 0; k < n; k++)
        {
            var gone = _rows[_row];
            Array.Copy(_rows, _row + 1, _rows, _row, Height - 1 - _row);
            gone.Clear();
            _rows[Height - 1] = gone;
        }
    }

    private void Reset()
    {
        foreach (var r in _rows) r.Clear();
        _row = _col = 0;
        _pendingWrap = false;
        _style = CellStyle.Default;
        _styleId = 0;
    }
}

internal interface ITerminalListener
{
    /// <summary>An explicit line feed is about to leave row <paramref name="row"/> (before any scrolling).</summary>
    void OnLineFeed(TerminalEmulator terminal, int row);

    /// <summary>Row 0 is about to scroll out of the screen.</summary>
    void OnRowEvicted(TerminalEmulator terminal, TerminalEmulator.Row row);

    void OnTitle(string title);
}
