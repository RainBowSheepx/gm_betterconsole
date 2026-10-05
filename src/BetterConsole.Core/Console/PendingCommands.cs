namespace BetterConsole.Core.Console;

/// <summary>
/// Commands we typed into srcds, waiting for their echo. srcds prints every line it reads from the
/// console input; the echo of a user command is styled as a command, the echo of an internal one
/// (for example the transport for non-ASCII commands) is hidden.
/// </summary>
internal sealed class PendingCommands
{
    private readonly object _lock = new();

    /// <summary>Raised (on the pipeline thread) when srcds echoed a command we typed.</summary>
    public event Action<string>? EchoMatched;
    private readonly LinkedList<(string Text, bool Internal, string? Display, DateTime At)> _pending = new();

    /// <param name="display">For an internal command: what to show in its place (the command the user typed).</param>
    public void Add(string text, bool isInternal, string? display = null)
    {
        lock (_lock)
        {
            _pending.AddLast((text.TrimEnd(), isInternal, display, DateTime.UtcNow));
            while (_pending.Count > 64) _pending.RemoveFirst();
        }
    }

    public bool TryMatchEcho(string line, out bool isInternal) => TryMatchEcho(line, out isInternal, out _);

    public bool TryMatchEcho(string line, out bool isInternal, out string? display)
    {
        isInternal = false;
        display = null;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            while (_pending.First is { } first && (now - first.Value.At).TotalSeconds > 10) _pending.RemoveFirst();
            if (_pending.Count == 0) return false;
            string text = line.TrimEnd();
            for (var node = _pending.First; node != null; node = node.Next)
            {
                if (node.Value.Text == text)
                {
                    isInternal = node.Value.Internal;
                    display = node.Value.Display;
                    _pending.Remove(node);
                    EchoMatched?.Invoke(text);
                    return true;
                }
            }
            return false;
        }
    }
}
