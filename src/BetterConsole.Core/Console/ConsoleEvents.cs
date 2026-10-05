using BetterConsole.Core.Errors;
using BetterConsole.Sdk;

namespace BetterConsole.Core.Console;

/// <summary>What the console pipeline hands to the UI. Consumed in order.</summary>
public abstract record ConsoleEvent;

/// <summary>A new line for the console tab.</summary>
public sealed record LineAdded(long Id, ConsoleLine Line) : ConsoleEvent;

/// <summary>A line that was already added got more text (a wrapped long line was completed).</summary>
public sealed record LineAmended(long Id, ConsoleLine Line) : ConsoleEvent;

/// <summary>
/// The unfinished last line (text printed without a newline yet). Null text = nothing pending.
/// It is replaced by a <see cref="LineAdded"/> once the newline arrives.
/// </summary>
public sealed record PreviewChanged(ConsoleLine? Line) : ConsoleEvent;

/// <summary>The console title changed (srcds puts the hostname there).</summary>
public sealed record TitleChanged(string Title) : ConsoleEvent;

/// <summary>A Lua error was recognised in the console text (and removed from the console tab).</summary>
public sealed record ErrorRecognized(LuaError Error) : ConsoleEvent;
