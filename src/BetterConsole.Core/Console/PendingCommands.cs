namespace BetterConsole.Core.Console;

/// <summary>
/// Commands we typed into srcds, waiting for their echo. srcds prints every line it reads from the
/// console input; the echo of a user command is styled as a command, the echo of an internal one
/// (for example the transport for non-ASCII commands) is hidden.
/// </summary>
internal sealed class PendingCommands
{
    private readonly object _lock = new();
    private readonly LinkedList<(string Text, bool Internal, DateTime At)> _pending = new();

    public void Add(string text, bool isInternal)
    {
        lock (_lock)
        {
            _pending.AddLast((text.TrimEnd(), isInternal, DateTime.UtcNow));
            while (_pending.Count > 64) _pending.RemoveFirst();
        }
    }

    public bool TryMatchEcho(string line, out bool isInternal)
    {
        isInternal = false;
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
                    _pending.Remove(node);
                    return true;
                }
            }
            return false;
        }
    }
}
