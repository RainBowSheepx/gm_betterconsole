using System.Text;
using BetterConsole.Core.Terminal;
using Xunit;

namespace BetterConsole.Tests;

public class TerminalTests
{
    private sealed class Harness
    {
        public readonly LineAssembler Assembler = new(new OemTextRestorer());
        public readonly TerminalEmulator Terminal;

        public Harness(int width = 80, int height = 10)
        {
            Terminal = new TerminalEmulator(width, height, Assembler);
            Assembler.Terminal = Terminal;
        }

        public List<AssembledLine> Feed(string s)
        {
            Terminal.Feed(s);
            Assembler.CheckAmendments(Terminal);
            var lines = Assembler.TakeLines().ToList();
            Assembler.TakeLines().Clear();
            return lines;
        }
    }

    [Fact]
    public void PlainLinesAreReportedOnLineFeed()
    {
        var h = new Harness();
        var lines = h.Feed("\x1b[?9001h\x1b[?1004h\x1b[?25l\x1b[2J\x1b[m\x1b[H\x1b]0;SOURCE DEDICATED SERVER\x07\x1b[?25h#\r\n#Console initialized.\r\nhalf");
        Assert.Equal(["#", "#Console initialized."], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.False(l.IsAmend));
        Assert.Equal("SOURCE DEDICATED SERVER", h.Assembler.Title);
        Assert.Equal("half", h.Assembler.Preview(h.Terminal)?.Text);
    }

    [Fact]
    public void TrueColorSpansAreKept()
    {
        var h = new Harness();
        var lines = h.Feed("\x1b[38;2;255;0;0mred \x1b[38;2;0;255;0mgreen\x1b[K\x1b[m\r\n");
        var line = Assert.Single(lines);
        Assert.Equal("red green", line.Text);
        Assert.Equal(2, line.Spans.Length);
        Assert.Equal(0xFFFF0000u, line.Spans[0].Argb);
        Assert.Equal((0, 4), (line.Spans[0].Start, line.Spans[0].Length));
        Assert.Equal(0xFF00FF00u, line.Spans[1].Argb);
    }

    [Fact]
    public void ColorSetBeforeCrLfCarriesToTheNextLine()
    {
        var h = new Harness();
        var lines = h.Feed("plain\x1b[38;2;255;90;90m\r\nWARNING: x\r\n\x1b[mnext\r\n");
        Assert.Equal(["plain", "WARNING: x", "next"], lines.Select(l => l.Text));
        Assert.Empty(lines[0].Spans);
        Assert.Equal(0xFFFF5A5Au, Assert.Single(lines[1].Spans).Argb);
        Assert.Empty(lines[2].Spans);
    }

    [Fact]
    public void CursorForwardInsideTextBecomesSpacesAndTrailingIsDropped()
    {
        var h = new Harness();
        var lines = h.Feed("CPU\x1b[3CIn\x1b[7C\r\n");
        Assert.Equal("CPU   In", Assert.Single(lines).Text);
    }

    [Fact]
    public void ConPtySoftWrapTrickJoinsTheLine()
    {
        // What ConPTY sends for a 25-char line on a 10-column screen whose cursor sits on the bottom row:
        // 10 chars, CRLF (scroll), jump back to the last cell of the previous row and rewrite it, go on.
        var h = new Harness(width: 10, height: 3);
        h.Feed("a\r\nb\r\n");
        var all = new List<AssembledLine>();
        all.AddRange(h.Feed("0123456789\r\n"));
        all.AddRange(h.Feed("\x1b[2;10H9ABCDEFGHIJ\r\n"));
        all.AddRange(h.Feed("\x1b[2;10HJKLMN\r\n"));
        var last = all.Last();
        Assert.Equal("0123456789ABCDEFGHIJKLMN", last.Text);
        Assert.True(last.IsAmend);
        Assert.Single(all.Select(l => l.Id).Distinct());
    }

    [Fact]
    public void Cp437MojibakeIsTurnedBackIntoUtf8()
    {
        // srcds wrote the UTF-8 bytes of "Привет"; the console decoded each byte with code page 437.
        var bytes = Encoding.UTF8.GetBytes("Привет, мир! ✓");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var mojibake = Encoding.GetEncoding(437).GetString(bytes);
        var h = new Harness();
        var lines = h.Feed(mojibake + "\r\n");
        Assert.Equal("Привет, мир! ✓", Assert.Single(lines).Text);
    }

    [Fact]
    public void ColoursFollowRestoredCharacters()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp = Encoding.GetEncoding(437);
        var h = new Harness();
        var lines = h.Feed("\x1b[38;2;1;2;3m" + cp.GetString(Encoding.UTF8.GetBytes("Ник")) + "\x1b[m: hi\r\n");
        var line = Assert.Single(lines);
        Assert.Equal("Ник: hi", line.Text);
        var span = Assert.Single(line.Spans);
        Assert.Equal((0, 3), (span.Start, span.Length));
    }

    [Fact]
    public void LinesScrollingOffTheTopAreNotLost()
    {
        var h = new Harness(width: 20, height: 3);
        var all = new List<string>();
        for (int i = 0; i < 10; i++) all.AddRange(h.Feed($"line {i}\r\n").Select(l => l.Text));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"line {i}"), all);
    }

    [Fact]
    public void ClearScreenDoesNotRewriteHistory()
    {
        var h = new Harness();
        var first = h.Feed("hello\r\nworld\r\n");
        var after = h.Feed("\x1b[2J\x1b[H\x1b[Kagain\r\n");
        Assert.Equal(2, first.Count);
        var line = Assert.Single(after);
        Assert.Equal("again", line.Text);
        Assert.False(line.IsAmend);
    }
}
