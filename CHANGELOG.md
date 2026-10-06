# Changelog

All notable changes are listed here. The release workflow takes the notes of a release from the
section with its tag.

## v0.1.3 - 2026-10-06

- **Find (Ctrl+F) looks like the rest of the app**: a search box with match case, whole words and
  regular expression toggles, previous / next / close buttons; matches are highlighted in the theme.
- **Tabs with icons only.** When the tabs do not fit with their titles they show only their icons
  (the title is in the tooltip); right-click the tab bar or use Settings → Appearance to always show
  titles or always only icons. Tabs that still do not fit are listed behind an arrow next to them,
  and the selected tab is always scrolled into view. Addon tabs can have their own icon
  (`icon = "E7FC"` in `BetterConsole.AddTab`, an overload with an icon for plugins).
- **Narrow windows**: Start / Stop / Restart show only their icons, and status bar items that do not
  fit are left out instead of overlapping each other.
- **Lua profiler**: rows are updated in place instead of being rebuilt every second, so the tables
  keep their scroll position and nothing blinks; entries that drop out of the top list stay. The
  biggest entries come first and the order holds still while the pointer is over a table. After
  stopping, the last results stay. The mouse wheel scrolls the page once a table is at its end, and
  the addon sends the top few hundred entries instead of 30–40.
- **Works next to other profilers and detours** (gProfiler and similar): no hook is wrapped twice
  (before, both re-wrapped each other every few seconds), timers are named after the function
  inside another addon's timer wrapper, net functions are only put back when nobody detoured them
  after us, and leftover wrappers only pass calls on.
- Errors with a Lua traceback in the message (`ErrorNoHalt(debug.traceback())`, errors printed from
  other Lua states) get their stack from the traceback.
- The native module checks every pointer before reading the engine's command list, so a crash guard
  of another module never sees an access violation from it.
- Fixed: selected player rows turned black while their menu was open; a white square between the
  scroll bars; long menus ran off the screen.

## v0.1.2 - 2026-10-06

- **Works while the server hibernates.** An empty server with `sv_hibernate_think 0` runs no Lua,
  so auto-completion, statistics and addon tabs used to wait for the first player. BetterConsole now
  wakes the addon through a hidden console command; the status bar shows *hibernating*.
- **Fixed: the window could freeze (one CPU core at 100%)** when a line wider than the console
  scrolled in and out of view while the console followed the output.
- **Fixed: with the Lua profiler on, `net.Send` to a single player failed** (in Sandbox and
  derived gamemodes). Net message accounting can no longer break a send.
- **Errors in timers are no longer blamed on BetterConsole.** The engine names the addon of the
  outermost function, which under the timer detour is the companion's wrapper; the error now gets
  the addon of the code that failed. Errors of code run with `lua_run` inside such a timer are
  recognised in the console text too.
- Lua errors from the server start-up are no longer counted twice.
- An `[ERROR] ...` line that an addon prints itself (no Lua location, no stack) stays in the console
  instead of being taken for a Lua error together with the lines after it.
- *Timer Failed!* lines name the timer's real function, not the profiler's wrapper.
- Commands typed while srcds loads a map wait for it instead of being dropped.
- `quit` / `exit` typed in the console stop the server (no auto-restart). A server that quits by
  itself with exit code 0 is shown as *Exited*, not as a crash.
- Stop works while the server is still starting; two quick starts no longer start two servers.
- The companion addon install reports files it may not write instead of cancelling the start.
- The live preview of an unfinished line follows the output right away.

## v0.1.1 - 2026-10-06

- **Fixed: commands sent quickly one after another were lost.** srcds reads all pending console
  input at once, runs the first line and drops the rest; commands are now typed one at a time, each
  after srcds took the previous one. This affected plugins, quick buttons and *Ban* without ULX
  (`banid` + `writeid`).
- The echo of commands with non-ASCII text now appears exactly where the command ran.
- The console keeps following the output when a horizontal scroll bar appears.

## v0.1.0 - 2026-10-06

First public version.

- **Console** — srcds runs inside a pseudo console: every colour of `MsgC`, Unicode text, no extra
  console window. The view keeps its place while you read older lines and follows the output again
  when you scroll to the end (or press *Follow*). Find (Ctrl+F), timestamps, word wrap, save to file.
- **Command input** in its own box: history (↑/↓), auto-completion of every server command and
  variable with its current value (Tab inserts, ↑/↓ choose), map names after `map` / `changelevel`.
  Commands with Cyrillic or other non-ASCII text work too.
- **Players** — sortable table (time on the server, ping, loss, fps, server load per player …),
  hideable columns, right-click for kick, ban, group, gag, mute, jail (ULX) with a dialog for
  reason and duration; engine commands when ULX is not installed.
- **Client Lua errors** grouped by player, newest first, duplicates counted; the order stays put
  while the pointer is over the list.
- **Server Lua errors** oldest first, duplicates counted, stack trace on click.
- **Statistics** — server FPS, frame time, game thread load, tick rate, CPU (like `stats`), memory,
  Lua memory, entities and edicts, network, lag spikes, and an on-demand Lua profiler (hooks,
  timers, net messages, entity classes).
- **Status bar** with CPU, players, in/out, SV fps and variance, tick, load, entities, Lua and RAM.
- **Themes**: Dark, Light, Midnight, Graphite, plus your own JSON themes.
- **Extensible**: server addons add tabs and status items from Lua (`BetterConsole.AddTab`),
  C# plugins add anything (`IConsolePlugin`). Samples included.
- Crash detection with automatic restart, start/stop/restart, profiles for several servers.
