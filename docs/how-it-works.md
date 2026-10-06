# How it works

```
 BetterConsole.exe (WPF)                                     srcds_console(_win64).exe
 ┌───────────────────────────────┐   pseudo console (ConPTY)  ┌──────────────────────────────────┐
 │ console view  ◄── classifier ◄┼── VT emulator ◄── output ──┤ engine console                    │
 │ input box     ──── key events ┼──────────────────► input ──┤                                   │
 │                               │                            │  garrysmod/lua/bin/               │
 │ errors, players, statistics,  │   named pipe (JSON lines)  │   gmsv_betterconsole_win64.dll    │
 │ auto-completion, addon tabs ◄─┼────────────────────────────┤◄─ garrysmod/addons/betterconsole  │
 └───────────────────────────────┘                            └──────────────────────────────────┘
```

## The console: a pseudo console

`srcds` writes its console with the Windows console API and reads typed keys from the console
input. BetterConsole starts it inside a **pseudo console** (ConPTY, Windows 10 1809+): the server
gets a real console, but no window; everything it draws arrives as a stream of VT escape
sequences, and the keys BetterConsole types arrive as real key events. That is why every colour
(`MsgC` uses 24-bit colours) and every command works exactly as in the srcds window.

A pseudo console sends a *rendering of a screen*, not the program's text: cursor moves, erasing,
colour changes, and its own trick for long lines that wrap. A small VT emulator
(`src/BetterConsole.Core/Terminal`) replays that onto a screen buffer and rebuilds the lines.

**Encoding.** srcds writes UTF-8 bytes, but the console decodes them with its code page, which is why
Cyrillic looks broken in the srcds window. BetterConsole sets the pseudo console to code page 437, where
every byte is one character, maps the characters back to the bytes and decodes those as UTF-8 —
lossless. Bytes that are not valid UTF-8 (old addons saved in Windows-1251) are shown in the system's
ANSI code page.

**Input.** Plain text typed into a pseudo console is turned into key presses with the user's current
keyboard layout, so with a Russian layout `{`, `}` and `'` would get lost. BetterConsole sends
explicit key events instead (*win32-input-mode*), so every ASCII character arrives. srcds' console
cannot take non-ASCII characters at all; such commands are sent hex-encoded through
`betterconsole_exec`, which runs them through the engine (see below).

**Lua errors** are recognised in the text — `[ERROR] …`, `[addon] path.lua:12: …`, the stack lines
`  1. fn - file:line`, `Timer Failed!`, the client header `[Nick|userid|SteamID] Lua Error:` — and
taken out of the console view. While the companion addon is connected the error tabs use its exact
data instead.

## The companion addon and module

`garrysmod/addons/betterconsole` (Lua) and `garrysmod/lua/bin/gmsv_betterconsole_win32|win64.dll`
(C++, ~400 lines, no dependencies) are installed by BetterConsole before every start.

- BetterConsole creates a named pipe and passes its name to srcds in the `BETTERCONSOLE_PIPE`
  environment variable. The module connects to it; without the variable it does nothing, and the
  Lua addon returns at once.
- All pipe I/O runs on a thread of the module. Lua only appends to an outgoing buffer and takes
  received lines from an inbox, both under a mutex — a slow or missing app cannot stall a frame.
- The Lua state is rebuilt on every map change; the module closes the pipe, the next state connects
  again, and the addon sends its tabs again.
- The module also reaches into the engine where Lua cannot:
  - `IVEngineServer::ServerCommand` / `ServerExecute` run commands below GMod's Lua command filter;
  - `IVEngineServer::GetPlayerNetInfo` → `INetChannelInfo` gives each client's data rate, packets,
    latency, loss, choke and frame rate (the numbers of `stats` and `net_graph`); the first call
    compares the channel's address with what Lua knows, and turns the numbers off if they do not
    match (a future engine with another layout);
  - the engine's list of console commands (`ConCommandBase`, walked from a variable the addon
    creates; read under structured exception handling) for auto-completion;
  - `QueryThreadCycleTime` for the CPU time of the game thread (*Game thread load*).
- What the addon changes in the server's Lua (only while it runs under BetterConsole):
  - `timer.Create` / `timer.Simple` / `timer.Adjust` wrap their functions (to time them while the
    profiler is on, otherwise they only call them);
  - `CreateConVar` remembers the names of new variables (for auto-completion);
  - `game.ConsoleCommand` and `RunConsoleCommand` are wrapped to notice `quit`, `exit` and
    `_restart` (for the journal) and pass every call on unchanged. Anti-backdoor or anti-cheat addons
    that compare these functions with the originals see a Lua function there, and code that kept
    the originals before BetterConsole's addon loaded is not noticed;
  - while profiling or capturing lag spikes: hooks, `net.Receive` handlers and `net.Start` /
    `net.Send*` are wrapped, and put back when it stops.

## Messages

One JSON object per line, with its type in `t`.

**Server → app**

| `t` | When | Fields |
|---|---|---|
| `hello` | after (re)connecting | `addon`, `module`, `gmod`, `branch`, `map`, `gamemode`, `hostname`, `maxplayers`, `tickrate`, `profiler` (an addon's profiler: `name`, `info`, `vprof`, `capture`; before the app asks for the capture) |
| `stats` | every second | `st` (`SysTime()` when sent), `fps`, `ft` / `ftmax` / `ftsd` (frame time avg / max / deviation, ms), `busy` / `busymax` (CPU ms per frame), `load` (%), `tps`, `tickrate`, `players`, `bots`, `maxplayers`, `ents`, `edicts`, `lua` (KB), `netin` / `netout` (bytes/s), `map`, `uptime`, `spikes`: frames longer than three ticks with `ms`, `busy`, `time`, `st` (`SysTime()` of the next frame), `phys` (ms), `heap` / `gc` (MB), `ents` (change), `joined`, `start`, the slowest `timer` (`k`, `ms`, `src`), with the capture on `cbs` (`kind`, `k`, `ms`, `n`, `b`, `src`, `who`), from addons (`OnSpike`) `causes` (`text`, `kind`, `tooltip`, `src`), `lua` and `engine` (`text`, `ms`, `tooltip`, `src`) |
| `capture_state` | after `capture`, and when the companion stops the capture itself | `on` |
| `prof_provider` | an addon set its profiler (or went back to the built-in one) | `name`, `info`, `vprof`, `capture`; none of them: the built-in one |
| `form_cmd` | a form set a console variable | `cmd`: shown in the console and the command history |
| `players` | every 1–2 s | `list` of `uid`, `name`, `sid`, `sid64`, `bot`, `ip`, `ping`, `loss`, `choke`, `in`, `out`, `fps`, `time`, `group`, `team`, `frags`, `deaths`, `load`, `upd` / `cmdr` (`cl_updaterate` / `cl_cmdrate`), `act` (player menu items whose filter accepts the player), ULX `ulxid` / `gagged` / `muted` / `jailed`; `ulx`, `groups` (highest first), `ranks` (group → how many groups it inherits from, ULX or CAMI), `load` (measured?) |
| `quitcmd` | Lua ran `quit`, `exit` or `_restart` | `cmd`, `ply` (`name`, `sid`: the player in whose call it ran), `src` (where in the code), `via`; the reason in the journal if the server exits right after |
| `err` | on a Lua error (repeats at most once a second, with `n`) | `realm` (`server` / `client`), `msg`, `stack` (`fn`, `src`, `line`), `addon`, `wsid`, `ply`, `n`, `time` |
| `cmds` | on request | `list` of `n` (name), `c` (is a command), `f` (flags), `h` (help), `v` / `d` / `mn` / `mx` (value, default, limits); `maps` |
| `cvals` | on request | `vals`: name → current value |
| `prof`, `prof_state` | while profiling | top `hooks`, `timers`, `netin`, `netout` (`k`, `ms`, `n`, `max`, `b`, `kb`, `src`; a field no row has is not shown), `ents`, `entTotal`, `since`, `extra` (an addon profiler's tables: `id`, `title`, `columns`, `rows`, `key`, `sort`) |
| `tab`, `tab_rm`, `w`, `wd`, `w_rm`, `stats_hide`, `pa`, `pa_rm`, `pa_hide`, `st`, `st_rm`, `notify`, `custom` | Lua API | see [lua-api.md](lua-api.md); widgets of the Statistics tab have `tab = "@stats"`, `pa` is a player menu item (`id`, `text`, `icon`, `order`, `command`, `fields`, `confirm`, `danger`, `filtered`, `run`, `bots`, `multi`) |

**App → server**

| `t` | |
|---|---|
| `cmds`, `cvals` (`names`) | command catalog, current values |
| `sub` (`players`) | the Players tab is open / closed (per-player load measuring) |
| `prof` (`on`) | start / stop the profiler |
| `capture` (`on`) | start / stop the detailed capture of lag spikes (the app also types `vprof_on` / `vprof_dump_spikes` itself, unless an addon's profiler said `vprof = false`) |
| `action` (`tab`, `widget`, `id`, `values`) | a button of an addon tab was pressed (`values`: its dialog's, if it has fields) |
| `form` (`tab`, `widget`, `field`, `value`) | a field of an addon's form was changed (checked again on the server) |
| `tabshow` (`tab`, `on`) | an addon tab (or Statistics, `@stats`) is on screen / left (`tab:OnShow`; forms keep their values fresh only then) |
| `paction` (`id`, `uids`, `values`) | a player menu item with `onRun` was chosen for these players |
| `exec` (`cmd`) | run a long command through the engine |
| `setgroup` (`sid`, `uid`, `group`) | set a group without ULX |
| `custom` (`type`, `data`) | from a C# plugin, raised as the Lua hook `BetterConsoleMessage` |

## Security

- The pipe is created for the current Windows user only and its name is random per run.
- `betterconsole_exec` refuses to run when called by a player; only the server console (which is
  BetterConsole) can use it.
- The module never runs Lua it receives; messages are data that the addon interprets.
- Nothing is sent anywhere except between BetterConsole and its own server process.

## Performance

- The pseudo console output is read on its own thread; the emulator, line assembly and error
  recognition run on another; the UI takes the finished lines in batches about 30 times a second.
  The console view (AvalonEdit) only lays out the lines on screen, so 20 000 lines of history cost
  nothing while scrolling.
- On the server, while connected: two tiny hooks per frame and a summary once a second. Per-player
  load is measured only while the Players tab is open, the profiler only while it runs.
