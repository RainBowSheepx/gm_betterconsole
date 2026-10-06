# User guide

![Console](images/console.png)

## The window

- **Tabs** along the top. Badges show counts: players online, players with client errors, distinct
  server errors (red while there are errors you have not looked at). When the tabs do not fit with
  their titles they show only their icons (the title is in the tooltip); right-click the tab bar (or
  Settings → Appearance) to always show titles or always only icons. Tabs that still do not fit are
  in the list behind the arrow next to them; Ctrl+1…9 and Ctrl+Tab switch tabs too.
- **Server controls** on the right: the state (*Running 1h 12m*), Start / Stop / Restart, the `…`
  menu (kill a hung server, open the server folder, documentation), the theme menu and Settings.
- **Status bar** at the bottom.
- **Toasts** in the bottom right corner for things that need your attention (a crash, a finished
  action).

## Console

The output of the server, with the colours addons print in (`MsgC`).

- **Reading old output**: scroll up with the mouse wheel, the scroll bar or PageUp. The view stays
  where you are while new lines keep arriving; the **N new lines** button in the corner brings you
  back to the end and the console follows the output again. Scrolling to the end by hand does the
  same.
- **Find**: Ctrl+F. Enter / Shift+Enter (or F3 / Shift+F3) jump between matches; the buttons next
  to the box match case, whole words or a regular expression. Esc closes it.
- **Time**: the time each line arrived, in a margin (not part of the text, so copying skips it).
- **Wrap**: break long lines instead of scrolling sideways.
- **Save**: the whole console as a `.log` file, with dates.
- **Clear**: empties the tab (the server is not affected).
- Links are clickable with Ctrl+click. Text can be selected; Ctrl+C copies.

Lua errors are taken out of the console (they would otherwise flood it) and go to the error tabs.
You can keep them in the console too: *Settings → Keep Lua errors out of the console*.

Lines BetterConsole writes itself start with `▸` (notices) or `▲` (problems); commands you ran start
with `›`.

### Running commands

Type into the box below the output and press Enter.

| Key | |
|---|---|
| **Tab** | insert the highlighted suggestion (the first one if you did not choose) |
| **↑ / ↓** | with suggestions open: choose one (it goes into the input right away); otherwise: command history |
| **Esc** | close the suggestions / clear the input |
| **Ctrl+Space** | show suggestions |
| **PageUp / PageDown** | scroll the output without leaving the input |
| **Ctrl+L** | clear the console |

![Auto-completion](images/autocomplete.png)

Suggestions come from the server itself: every command and variable registered by the engine, the
gamemode, Lua and binary modules — with the **current value** of variables (refreshed while the list
is open) and their help text. Names that start with what you typed come first, then names that
contain it. After `map ` or `changelevel ` the maps of the server are suggested.

Commands with characters srcds cannot type (Cyrillic, emoji …) and very long commands are sent
through the companion addon and run by the engine directly, so `say Всем привет` just works. Commands
that GMod's Lua refuses to run (`game.ConsoleCommand blocked!`) are not affected: what you type runs
as if you typed it into the srcds window.

## Players

![Players](images/players.png)

Everyone on the server, updated every second while the tab is open.

- **Sort** by any column (click the header; again to reverse). Default: time on the server, the
  longest first, so new players are at the bottom.
- **Columns**: right-click the header or press *Columns* to show or hide them. Available: Nick,
  SteamID, Group, Load ms, Loss, Ping, FPS, Time, IP, In, Out, Choke, Team, Score.
- **Load ms** is the server CPU time spent on that player per tick: processing his movement
  commands (every `StartCommand` … `FinishMove` hook) and the Lua handlers of the net messages he
  sends. It is measured only while this tab is open. Yellow above 2.5 % of the tick, red above 6 %.
- **FPS** is the frame rate the player's game reports to the server.
- Icons after the name: gagged (voice), muted (chat), jailed.

**Right-click a player** for:

| Action | With ULX | Without ULX |
|---|---|---|
| Kick… (reason) | `ulx kick` | `kickid` |
| Ban… (duration, reason) | `ulx banid` | `banid` + `writeid` |
| Set group | `ulx adduserid` / `removeuserid` (saved) | `SetUserGroup` until he leaves |
| Gag / Mute / Jail… / Unjail | `ulx gag`, `mute`, `jail` | — |
| Copy name / SteamID / SteamID64, open Steam profile | | |

Players are addressed by an id ULX resolves to exactly that player, so two players with the same
name, or names with spaces or Cyrillic, are not a problem.

## Client errors

![Client errors](images/client-errors.png)

Lua errors that happen in players' games (GMod sends them to the server). One row per player, sorted
by name, all collapsed at first; a red dot marks players with errors you have not opened yet.

- Open a player to see his errors, **the most recent first**. The same error happening again only
  raises its counter (×12) and its time.
- Click an error for its stack trace. Select text with the mouse (a click that selects does not
  fold the card), or use the copy button.
- **Nothing jumps**: while the mouse pointer is over the list, errors that happen again only update
  their counters — the cards move to the top only after you leave the list. New errors appear
  without moving what you are looking at, and a newly erroring player does not close the one you
  have open.

## Server errors

![Server errors](images/server-errors.png)

Errors of the server's own Lua, **oldest first**, newest at the bottom (the list follows new errors
while you are at the bottom). Repeats raise the counter of the existing card instead of adding a new
one. Click for the stack trace, the *Timer Failed!* line of timers, the addon and its Workshop id.

*Settings → Count errors that differ only in entity numbers or table addresses as one* merges
`Entity [123][prop_physics]` and `Entity [456][prop_physics]` into one error (default).

Both error tabs have a filter box, *Copy all* and *Clear*.

## Statistics

![Statistics](images/statistics.png)

| Number | Meaning |
|---|---|
| **Server FPS** | frames the server ran per second, and the deviation of the frame time — what `net_graph` shows as `sv` |
| **Frame time** | average and longest time between frames; should stay at the tick interval (the red dashed line) |
| **Game thread load** | how much of the time the main thread was busy, measured with its CPU time. Close to 100 % the server can no longer keep its tick rate |
| **Ticks / second** | ticks actually run per second, against the tick rate |
| **CPU (stats)** | CPU time of srcds over 5 seconds, computed like the engine's `stats` command (100 % = one core) |
| **Memory** | private memory of srcds and its working set |
| **Lua memory** | `collectgarbage("count")` |
| **Entities** | entity count and edicts out of 8192 (the server crashes when edicts run out) |
| **Network** | data from / to all clients |
| **Lag spikes** | frames that took longer than three ticks, with the CPU time inside them: a long frame with little CPU means the thread was waiting (disk, the console, other programs) |

Charts show 1 minute to 1 hour (buttons on the top right); hover them for exact values.

### Lua profiler

![Profiler](images/profiler.png)

Press **Start profiling** to time every hook, timer and net message handler, and to count outgoing
net messages and entities per class. Numbers are averages per second since you started; rows are
updated in place and stay in the table, so the list does not jump. The biggest entries come first
(click a column to sort by it); the order holds still while the pointer is over a table. After
**Stop profiling** the last results stay until the next start. Profiling costs 1–3 microseconds per
call, so stop it when you are done (it also stops when BetterConsole disconnects).

It works next to other profilers (gProfiler and the like): BetterConsole does not wrap a hook twice
when another profiler has wrapped its wrapper, and names timers after the function inside such
wrappers.

## Status bar

`map · Players 12/20 +2 · CPU 18.4% · In 34.2 KB/s · Out 120.5 KB/s · SV 33.0 fps ±0.4 ms · Tick 33/33 · Load 22% · Ents 2310 (812 ed.) · Lua 48 MB · RAM 1240 MB`

Hover an item for its explanation. *SV hibernating* means the server ran no frames for a few
seconds — it is hibernating (no players, `sv_hibernate_think 0`) or frozen. Addons and plugins can
add their own items after the built-in ones. In a narrow window the items that do not fit are left
out, from the end.

## Themes

The sun icon switches between Dark, Light, Midnight and Graphite, plus the themes in the `themes`
folder (two come with the release). How to make one: [themes.md](themes.md).

## Keyboard shortcuts

| Key | |
|---|---|
| F5 / Shift+F5 / Ctrl+F5 | start / stop / restart the server |
| Ctrl+1 … Ctrl+9 | switch tabs |
| Ctrl+Tab | next tab |
| Ctrl+F | find in the console |
