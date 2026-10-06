# Changelog

All notable changes are listed here. The release workflow takes the notes of a release from the
section with its tag.

## v0.3.1 - 2026-10-06

- **Right-click anywhere on the Statistics page** to hide numbers, charts and sections you do not
  need — the built-in ones and those of addons and plugins: *Hide "…"* for the one under the
  pointer, the rest in submenus, *Show all you hid* to bring them back. Saved when BetterConsole
  closes.

## v0.3.0 - 2026-10-06

- **Compact mode** (*… → Compact mode*, or *Settings → Appearance*): smaller controls, square
  corners and one plain palette (dark, or light like Windows' apps) instead of the themes; no
  shadows, animations or Steam avatars, charts drawn without smoothing. The least work for the
  processor and the graphics card, which matters most on a VPS without one. Only the look changes;
  the theme button is hidden while it is on.
- **The status bar shows what you choose**: right-click it to hide or show any item — the built-in
  ones and those of addons and plugins. Saved when BetterConsole closes.
- **Addons and plugins on the Statistics tab**: key numbers (a new *Stat* widget) among the built-in
  ones, charts with the charts (they follow the 1 min … 1 hour window), any widget or WPF content
  below them; built-in parts can be hidden (`BetterConsole.Stats`, `IUiHost.Stats`).
- **Addons and plugins in the player menu**: items that run a console command for each selected
  player or Lua with all of them, with a dialog (text, numbers, choices) or a question first, and
  filters for whom an item is (two of them make a toggle such as Freeze / Unfreeze). Addons can hide
  built-in items, plugins can change the whole menu (`BetterConsole.AddPlayerAction`,
  `IUiHost.AddPlayerAction`, `PlayerMenuOpening`).
- Status items of addons and plugins can have a dimmed label like the built-in ones, a name for the
  status bar's menu and an order (`BetterConsole.SetStatus(id, { label = ..., text = ... })`).
- Widgets can be removed (`widget:Remove()`).
- Players: **cl_updaterate** and **cl_cmdrate** columns (hidden at first: *Columns*).
- The example addon and the sample plugin show the new API.

## v0.2.2 - 2026-10-06

- **Lag spikes say what caused them**: each long frame gets chips for what it was made of — the game
  thread waiting (no CPU), the slowest timer, the Lua garbage collector, physics, entities created or
  removed, players joining, a map that just started. **Detailed capture** (a button on the card) adds
  the frame's slowest hooks, timers and net messages (with size and sender) and the engine's own
  profile (vprof): where the engine's time went. The engine reports one long frame a second at most;
  its reports stay out of the console and their files are deleted.
- **Several players at once** in the Players tab: Ctrl+click, Shift+click, Ctrl+A or *Select all*,
  then right-click — kick, ban, group, gag, mute, jail and copy work on all of them, with one dialog.
  Copied names and SteamIDs are separated by spaces.
- **CPU affinity knows the processor**: its name; performance and efficiency cores in groups of their
  own with *P-cores* / *E-cores* buttons; cores grouped by their L3 cache on chips with several (two
  CCDs, X3D); on a VPS or another virtual machine a note on what pinning can and cannot do there.
- The profiler and the capture now include hooks added after they started within a second (was five).
- Fixed: a hook, timer or net message that failed with an error was counted with no time by the
  profiler (and by the lag spikes); it now counts with its time until the error.
- Fixed: clicking the counter of a client error showed an exception.
- Fixed: the gag / mute / jail icons in Players were cramped and blurry; they are small chips now.
- Fixed: the SteamID in the client errors took the whole width of the player's row.
- Fixed: separators in right-click menus were bright lines in the dark themes.

## v0.2.1 - 2026-10-06

- Fixed: the console's timestamps could run into the text (no gap after the time, its last digit
  hidden): the time column was measured with the default UI font instead of the console font.
- Fixed: text boxes had their inner padding twice, so the text of Find (Ctrl+F) started far from the
  magnifier, and typed text did not line up with the hints of the filter boxes and the console input.
  Text boxes are a little more compact now.
- Fixed: Find's "No matches found!" stayed on screen after switching to another tab or server.

## v0.2.0 - 2026-10-06

- **Multi-console**: several servers in one BetterConsole (*Settings → Servers*). The logo in the top
  left corner opens the server list, which slides in over the window: each server with its state,
  hostname, map, players and server fps, unseen errors. A red dot on the logo means another server
  crashed or has new errors. Ctrl+Alt+1…9 switch servers. A server can get a **window of its own**
  (and come back); with several windows every server can be watched at once. Settings are shared,
  server options (folder, start options, CPU, schedule …) are per server. Own windows reopen where
  they were.
- **Always run**: the server is started again whatever stopped it — a crash, a quit from the game or
  rcon, `quit` typed in the console — keeps trying after many crashes (waiting longer each time) and
  starts with BetterConsole. Only Stop, Kill and closing BetterConsole leave it off.
- **Scheduled restarts** at times of day (05:00, 17:30 …), with warnings in the chat a few minutes
  before.
- **Start / stop journal** (… menu): when each server started, stopped, crashed or quit by itself, and
  why — the Start / Stop buttons, a scheduled restart, Always run, `quit` in the console, rcon, a
  crash with its exit code, closing BetterConsole.
- **CPU affinity and priority** of the server process: tick the processors it may run on (grouped by
  core; performance and efficiency cores are marked), choose a priority; applied at once and on every
  start (… menu or Settings).
- **Lua files open in your editor**: double-click a path in a Lua error, its stack trace, or a hook,
  timer or net message of the profiler, and the file opens at its line — VS Code, Notepad++, Sublime
  Text or a command of your own (*Settings → Lua files*). Paths look like the rest of the text; the
  pointer turns into a hand over the ones whose file is on the server.
- **Steam avatars** of players in the Players tab, the client errors and the admin dialogs (can be
  switched off; the coloured initial stays when there is none).
- Errors open their stack trace only with the arrow, so their text can be selected with clicks.
- Players: the Group column sorts by the group hierarchy (superadmin above admin above user, from ULX
  or CAMI); column widths and order are remembered, *Reset columns* in the header menu.
- The profiler shows where timers and net message handlers are defined (tooltip; double-click opens).
- Plugins: each server gets its own instance of every plugin.
- Fixed: "No lag spikes so far" over a list of lag spikes.

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
