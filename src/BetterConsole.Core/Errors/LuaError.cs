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
