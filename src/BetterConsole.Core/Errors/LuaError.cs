using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterConsole.Core.Errors;

public enum LuaRealm { Server, Client }

public sealed record StackFrame(string Function, string Source, int Line)
{
    public override string ToString() => Line > 0 ? $"{Function} - {Source}:{Line}" : $"{Function} - {Source}";
}

/// <summary>One occurrence (or a batch of identical occurrences) of a Lua error.</summary>
public sealed record LuaError
{
    public required LuaRealm Realm { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<StackFrame> Stack { get; init; } = Array.Empty<StackFrame>();
    public DateTime Time { get; init; } = DateTime.Now;
    /// <summary>How many identical errors this record stands for.</summary>
    public int Count { get; init; } = 1;
    public string? AddonTitle { get; init; }
    public string? WorkshopId { get; init; }
    /// <summary>"Timer Failed! [name][source]" that srcds printed after the error, if any.</summary>
    public string? Context { get; init; }
    public PlayerRef? Player { get; init; }
    /// <summary>Where the record came from: the Lua addon (exact) or console text (fallback).</summary>
    public ErrorSource Source { get; init; }
}

public enum ErrorSource { Bridge, ConsoleText }

/// <summary>
/// Lua's own traceback inside an error message ("msg\nstack traceback:\n\t[C]: in function 'error'\n\t..."),
/// as code passes it to ErrorNoHalt. GMod's stack of such an error only shows where ErrorNoHalt was called.
/// </summary>
public static partial class LuaTraceback
{
    [GeneratedRegex(@"^\s+(?<src>\[C\]|\S[^:]*?)(?::(?<line>-?\d+))?: in (?<what>.+?)\s*$")]
    private static partial Regex Line();

    [GeneratedRegex(@"^(?:function|local|upvalue|method|field|global) '(?<name>[^']+)'$")]
    private static partial Regex Name();

    /// <summary>The message without the traceback and the traceback's frames; no frames when there is none.</summary>
    public static (string Message, IReadOnlyList<StackFrame>? Frames) Split(string message)
    {
        int at = message.IndexOf("stack traceback:", StringComparison.Ordinal);
        if (at < 0 || (at > 0 && message[at - 1] != '\n')) return (message, null);
        var frames = new List<StackFrame>();
        foreach (var raw in message[(at + "stack traceback:".Length)..].Split('\n'))
        {
            var l = raw.TrimEnd('\r');
            if (l.Trim().Length == 0) continue;
            var m = Line().Match(l);
            if (!m.Success) break;
            var what = m.Groups["what"].Value;
            var nm = Name().Match(what);
            frames.Add(new StackFrame(nm.Success ? nm.Groups["name"].Value : what == "main chunk" ? "main chunk" : "unknown",
                m.Groups["src"].Value, m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out int n) ? n : 0));
        }
        return frames.Count == 0 ? (message, null) : (message[..at].TrimEnd(), frames);
    }
}

public static partial class ErrorAttribution
{
    /// <summary>The folder the companion addon is installed to, which the engine uses as its title.</summary>
    public const string CompanionAddon = "betterconsole";

    /// <summary>
    /// The engine names the addon of the outermost Lua function of a call. Under the companion's timer
    /// detour and profiler that is the companion's wrapper, so errors of other addons' timers and hooks
    /// come "from betterconsole". Those are given the addon of the code that failed (from its path)
    /// or none.
    /// </summary>
    public static LuaError Fix(LuaError e)
    {
        if (!string.Equals(e.AddonTitle, CompanionAddon, StringComparison.OrdinalIgnoreCase)) return e;
        if (e.Stack.Any(f => f.Source.Contains("addons/" + CompanionAddon + "/", StringComparison.OrdinalIgnoreCase))) return e;
        string? addon = null;
        foreach (var f in e.Stack)
        {
            var m = AddonPath().Match(f.Source);
            if (m.Success)
            {
                addon = m.Groups[1].Value;
                break;
            }
        }
        return e with { AddonTitle = addon, WorkshopId = null };
    }

    [GeneratedRegex(@"^(?:@)?addons/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex AddonPath();
}

public sealed record PlayerRef(string Name, string SteamId, string SteamId64, int UserId)
{
    /// <summary>The key client errors are grouped by.</summary>
    public string Key => IsBot ? "bot:" + UserId + ":" + Name
        : !string.IsNullOrEmpty(SteamId64) && SteamId64 != "0" ? SteamId64
        : !string.IsNullOrEmpty(SteamId) ? SteamId : Name;

    /// <summary>Bots all share the SteamID "BOT" (and one SteamID64): they are told apart by user id.</summary>
    public bool IsBot => SteamId == "BOT";
}

public static partial class ErrorFingerprint
{
    /// <summary>
    /// The identity used to merge duplicates: message + stack. Volatile parts (entity indices, table
    /// addresses) are blanked when <paramref name="mergeSimilar"/> is set, so "Entity [123][prop]"
    /// and "Entity [456][prop]" count as the same bug.
    /// </summary>
    public static string Of(LuaError e, bool mergeSimilar)
    {
        var sb = new StringBuilder(256);
        sb.Append((int)e.Realm).Append('|');
        sb.Append(mergeSimilar ? Normalize(e.Message) : e.Message);
        foreach (var f in e.Stack) sb.Append('|').Append(f.Function).Append('@').Append(f.Source).Append(':').Append(f.Line);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 10);
    }

    public static string Normalize(string message)
    {
        message = EntityIndex().Replace(message, "$1[#]");
        message = Address().Replace(message, "$1: 0x#");
        return message;
    }

    [GeneratedRegex(@"\b(Entity|Player|Weapon|Vehicle|NPC|NextBot) ?\[\d+\]")]
    private static partial Regex EntityIndex();

    [GeneratedRegex(@"\b(table|function|userdata|thread|cdata): ?0x[0-9a-fA-F]+")]
    private static partial Regex Address();
}
