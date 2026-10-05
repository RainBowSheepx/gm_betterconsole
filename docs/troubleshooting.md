# Troubleshooting

### "No srcds_console.exe / srcds_console_win64.exe in …"

The folder is not the server's root, or the server is very old. The right folder contains
`srcds.exe` and a `garrysmod` folder. Update the server with SteamCMD (`app_update 4020`) — every
current GMod server ships the `srcds_console` executables.

### The status bar says *Addon: off*

- Right after the start the server is still loading; the addon connects when Lua starts (a few
  seconds, longer with many Workshop addons).
- *Settings → Install the companion addon* is off, or the files could not be copied (look for a red
  `▲` line in the console). With two servers sharing one folder, the module DLL is locked by the
  first one.
- An antivirus may block `gmsv_betterconsole_win64.dll` from being written or loaded.
- Something else on the server breaks Lua early. Look at the *Server errors* tab or the console.

The console itself works without the addon; errors are then read from the console text.

### Lua errors still show in the console

*Settings → Keep Lua errors out of the console* must be on (default). Errors printed with
`ErrorNoHalt` look like normal output and stay in the console; they are listed in the error tab as
well.

### Client errors do not appear

GMod sends client errors to the server only while `sv_kickerrornum` / `sv_log_client_errors` allow
it (the defaults do). GMod also drops a burst of more than five errors per second from one client.

### Auto-completion shows nothing

The command list arrives a few seconds after the addon connects. Only commands that do something
on a server are listed; client commands (`bind`, `cl_*`, `mat_*` …) are hidden — *Settings →
Auto-complete server commands only* shows them again.

### Network numbers are "—"

They come from the engine's net channels through the module; bots have none. If the module finds the
engine layout unexpected (a future GMod update), it switches them off and prints
`[BetterConsole] network statistics disabled` once.

### *SV idle* in the status bar

The server ran no frames for a few seconds: it hibernates without players. `sv_hibernate_think 1`
keeps it running (and keeps the statistics alive).

### The server crashes right after the start

Start it once without BetterConsole (your old start.bat) to see if it is the server. If it only
happens with BetterConsole, switch off *Install the companion addon*, delete
`garrysmod/lua/bin/gmsv_betterconsole_*.dll` and open an issue with the console output.

### Where are the logs?

`logs\betterconsole.log` next to `BetterConsole.exe` (or in `%APPDATA%\BetterConsole`). Crashes of
BetterConsole itself write `crash-*.txt` there.

### Reporting a problem

[Open an issue](https://github.com/RainBowSheepx/gm_betterconsole/issues) with the version (*… →
About*), Windows version, the server branch (32/64-bit), and `logs\betterconsole.log`.
