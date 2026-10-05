# Getting started

## What you need

- Windows 10 version 1809 or newer, or Windows 11, 64-bit. (BetterConsole uses the Windows
  *pseudo console*, which appeared in 1809.)
- A Garry's Mod **dedicated server** installed with SteamCMD (app 4020). Both branches work, and
  both the 32-bit (`srcds.exe`) and the 64-bit (`srcds_win64.exe`) server.

## Install

1. Download a release from the [Releases page](https://github.com/RainBowSheepx/gm_betterconsole/releases/latest):
   - **`BetterConsole-<version>-win-x64.zip`** — recommended, contains everything.
   - `BetterConsole-<version>-win-x64-small.zip` — the same app, 0.7 MB, but you need the
     [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed.
2. Unpack it into a folder you like, for example `C:\Servers\BetterConsole` or right next to the
   server. BetterConsole keeps its settings in that folder (`settings.json`), so it is portable:
   copy the folder and you copy the setup. If the folder is not writable, settings go to
   `%APPDATA%\BetterConsole`.

There is no installer and nothing is written to the registry.

## First start

Start `BetterConsole.exe`. On the first start the settings window opens:

| Setting | What to put there |
|---|---|
| **Server folder** | The folder that contains `srcds.exe` and the `garrysmod` folder. |
| **Executable** | Leave *auto*. BetterConsole starts `srcds_console_win64.exe` (64-bit) when the server has the 64-bit engine, otherwise `srcds_console.exe`. |
| **Start options** | The same options you give srcds today, for example `-tickrate 33 +maxplayers 20 +gamemode sandbox +map gm_construct +host_workshop_collection 123456`. |

Already have a `start.bat`? Press **Import from start.bat** and pick it: the srcds options are taken
from it (and the server folder, if you have not chosen one).

Press **Save**, then **Start** (or F5). The console fills up, and after a few seconds the status bar
on the bottom right says **Addon: on** — the companion addon inside the server is connected.

> **Why `srcds_console` and not `srcds.exe`?** `srcds.exe` and `srcds_win64.exe` are window programs:
> with `-console` they open a console window of their own. Their `srcds_console` twins (shipped with
> every GMod server) are console programs, which BetterConsole can run invisibly inside its pseudo
> console. They are the same server.

## The companion addon

Before every start BetterConsole copies two things into the server (only when they changed):

- `garrysmod/addons/betterconsole/` — a small Lua addon,
- `garrysmod/lua/bin/gmsv_betterconsole_win32.dll` or `..._win64.dll` — a native module that talks
  to BetterConsole over a named pipe.

They provide the structured Lua errors, statistics, the player list, auto-completion, addon tabs, and
running commands with non-ASCII text. The console itself works without them.

When the server is started **without** BetterConsole (your old `start.bat`), the module finds no
BetterConsole to talk to and the addon does nothing at all: no hooks, no timers.

You can switch the automatic install off in *Settings → Server* and install the files yourself from
`betterconsole-addon-<version>.zip`.

## Settings worth knowing

- **Restart the server when it crashes** — after the delay you set. If the server crashes more than
  five times in ten minutes, BetterConsole stops restarting it and tells you so.
- **Start the server when BetterConsole opens** — put BetterConsole into the Windows *Startup*
  folder (or Task Scheduler) and the server comes up with the machine.
- **Wait for "quit"** — Stop sends `quit` and waits this long before killing the process.
- **Lines kept** — how much console history the tab keeps (20 000 by default).
- **Keep Lua errors out of the console** — they go to their own tabs instead (default).
- **Auto-complete server commands only** — hides commands that only do something on a game client
  (`bind`, `cl_*`, `mat_*`, `snd_*` …).

## Several servers

Each copy of BetterConsole manages one server. Two ways to run more:

- **One folder per server** (simplest): unpack BetterConsole twice.
- **Profiles**: start `BetterConsole.exe --profile tram` — it uses `settings.tram.json` instead of
  `settings.json`. Make a shortcut per profile. Each profile can only be open once.

## Closing BetterConsole

Closing the window stops the server (it asks first, then sends `quit`). This is the same as closing
the srcds console window — the console *is* the server's console. To keep the server running, keep
BetterConsole open; it can be minimised.

## Updating

Unpack the new version over the old one (your `settings.json`, `history.txt`, themes and plugins
stay). The companion addon in the server is updated automatically on the next start.

Next: the [user guide](user-guide.md).
