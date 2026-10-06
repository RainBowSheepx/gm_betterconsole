<p align="center">
  <img src="src/BetterConsole.App/Assets/logo.png" width="96" alt="BetterConsole logo">
</p>

<h1 align="center">BetterConsole</h1>

<p align="center">
  <b>A modern console for Garry's Mod dedicated servers on Windows</b><br>
  Colours, auto-completion, Lua errors in their own tabs, players, live statistics and a profiler — and tabs of your own.
</p>

<p align="center">
  <a href="https://github.com/RainBowSheepx/gm_betterconsole/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/RainBowSheepx/gm_betterconsole?label=download&color=4F8CFF"></a>
  <a href="https://github.com/RainBowSheepx/gm_betterconsole/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/RainBowSheepx/gm_betterconsole/actions/workflows/ci.yml/badge.svg"></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-2563EB">
  <a href="LICENSE"><img alt="MIT" src="https://img.shields.io/badge/license-MIT-3CCB7F"></a>
</p>

<p align="center"><a href="README.ru.md">Читать на русском</a></p>

![The console tab](docs/images/console.png)

The window `srcds` gives you is a 1990s text console: no scrollback worth the name, no search, Lua
errors buried in the output, Cyrillic turned into garbage, and nothing about how the server is doing.
**BetterConsole** starts your server for you and gives it a real console — without changing anything
in your server or its addons.

## Features

- **A console that behaves.** Every colour of `MsgC`, Unicode text, search (Ctrl+F), timestamps,
  word wrap, save to file. Scroll up to read and the view stays put while new lines arrive; scroll to
  the end (or press *Follow*) and it follows the output again. Input lives in its own box, so typing
  is never disturbed by the output.
- **Auto-completion** of every server command and variable — with its current value and help text,
  like the game's own console. Tab inserts, ↑/↓ choose, map names after `map` / `changelevel`,
  command history across sessions. Client-only commands are left out.
- **Commands just work**: text with Cyrillic or other non-ASCII characters (`say Привет`), and even
  commands Lua refuses to run, go straight to the engine.
- **Players** tab: a sortable table with Steam avatars (time on the server, newest at the bottom,
  ping, loss, fps, cl_updaterate / cl_cmdrate, the server CPU each player costs, groups by rank …)
  with columns you can hide, resize and move. Right-click a player, or several selected ones, to
  kick, ban, set a group, gag, mute or jail — ULX compatible, with a dialog for the reason and
  duration.
- **Client Lua errors** grouped by player, newest first; **server Lua errors** oldest first. Same
  errors are counted instead of repeated, the arrow shows the stack trace, text can be selected, a
  double-click on a Lua path opens the file at its line in your editor (VS Code, Notepad++ …), and
  the lists never jump while you read them.
- **Statistics**: server FPS and frame time variance, game thread load, tick rate, CPU (the way
  `stats` computes it), memory, Lua memory, entities and edicts, network — as charts over 1 minute
  to 1 hour; **lag spikes** with what each long frame was made of (the slowest timer, the Lua
  collector, physics; with detailed capture the slowest hooks and net messages too, and the engine's
  own profiler); an on-demand **Lua profiler** for
  hooks, timers, net messages and entity classes.
- **Status bar** with CPU, players, in/out, `sv` fps ± variance, tick, load, entities, Lua and RAM;
  right-click it to choose what it shows.
- **Themes**: Dark, Light, Midnight, Graphite — and your own as a small JSON file. **Compact mode**:
  smaller and plain, without shadows, animations or avatars, for the least CPU and graphics work
  (a VPS without a graphics card draws everything in software); everything still works.
- **Make it yours**: server addons add tabs, numbers and charts on the Statistics tab, items in the
  player menu (a command or Lua, with a dialog), tables, logs, buttons and status items with a few
  lines of Lua, and can hide built-in parts; C# plugins can add anything.
- **Several servers in one app** (multi-console): a server list with state, map, players and fps that
  slides in from the logo, and a window of its own for any server. Settings are shared.
- **Runs the server for you**: start / stop / restart, crash detection with automatic restart,
  *Always run*, restarts at set times with warnings in the chat, a start / stop journal with the
  reason of every start and stop, CPU affinity and priority, import of your existing `start.bat`.

## Screenshots

| | |
|---|---|
| ![Auto-completion](docs/images/autocomplete.png) <br> **Auto-completion** with current values | ![Players](docs/images/players.png) <br> **Players** and admin actions |
| ![Client errors](docs/images/client-errors.png) <br> **Client Lua errors** by player | ![Server errors](docs/images/server-errors.png) <br> **Server Lua errors** with stack traces |
| ![Statistics](docs/images/statistics.png) <br> **Statistics** | ![Profiler](docs/images/profiler.png) <br> **Lua profiler** |
| ![Addon tab](docs/images/addon-tab.png) <br> A **tab made by a server addon** in 60 lines of Lua | ![Light theme](docs/images/theme-light.png) <br> **Light** theme |
| ![Midnight theme](docs/images/theme-midnight.png) <br> **Midnight** theme | ![Settings](docs/images/settings.png) <br> **Settings** |
| ![Server list](docs/images/servers.png) <br> **Several servers**: the server list | ![Journal](docs/images/journal.png) <br> **Start / stop journal** with the reasons |
| ![CPU affinity](docs/images/affinity.png) <br> **CPU affinity** and priority | ![Own window](docs/images/own-window.png) <br> A server in a **window of its own** |

## Quick start

1. Download **`BetterConsole-<version>-win-x64.zip`** from the [latest release](https://github.com/RainBowSheepx/gm_betterconsole/releases/latest) and unpack it anywhere (one folder per server is a good idea).
2. Start `BetterConsole.exe`. The settings open on the first start.
3. Choose your server folder (the one with `srcds.exe`) — or press **Import from start.bat** to take over the options you use today.
4. Press **Start** (F5). That's it.

BetterConsole copies its small companion addon into the server before every start; it is what
provides the Lua errors, statistics, players, auto-completion and addon tabs. When the server is
started without BetterConsole the addon stays idle.

➡ **[Getting started](docs/getting-started.md)** has the details: requirements, the settings, running several servers.

## Add your own tab from a server addon

```lua
if BetterConsole then
    local tab   = BetterConsole.AddTab("shop", { title = "Shop" })
    local stats = tab:KeyValue("stats", { title = "Today", span = 4 })
    local sales = tab:Chart("sales", { title = "Sales", span = 8, series = { { name = "orders" } } })

    timer.Create("shop_console", 5, 0, function()
        stats:Set({ Orders = Shop.Orders, Revenue = Shop.Revenue .. " $" })
        sales:Push(Shop.OrdersLastMinute)
    end)
end
```

Text, key/value lists, tables, logs, charts and buttons are available; everything is resent
automatically after a map change. ➡ **[Lua API](docs/lua-api.md)** · a complete example: [`samples/lua/betterconsole_example`](samples/lua/betterconsole_example).

## Or write a C# plugin

```csharp
public sealed class HelloPlugin : IConsolePlugin
{
    public string Id => "me.hello";
    public string Name => "Hello";

    public void Initialize(IPluginContext ctx)
    {
        ctx.Ui.AddTab("hello", "Hello", () => new TextBlock { Text = "Hi!" });
        ctx.Console.LineReceived += (_, e) => { if (e.Line.Text.Contains("connected")) ctx.Ui.Notify(e.Line.Text); };
    }
}
```

Drop the DLL into `plugins\Hello\` next to BetterConsole.exe. ➡ **[Plugins](docs/plugins.md)** · sample: [`samples/QuickCommandsPlugin`](samples/QuickCommandsPlugin).

## Documentation

| | |
|---|---|
| [Getting started](docs/getting-started.md) | install, first start, settings, several servers |
| [User guide](docs/user-guide.md) | every tab, the status bar, keyboard shortcuts |
| [Lua API](docs/lua-api.md) | tabs and status items from server addons |
| [Plugins](docs/plugins.md) | C# plugins: tabs, status bar, console and bridge events |
| [Themes](docs/themes.md) | colour names and how to make a theme |
| [How it works](docs/how-it-works.md) | pseudo console, the companion addon, the bridge protocol |
| [Building](docs/building.md) | build from source, run the tests, make a release |
| [Troubleshooting](docs/troubleshooting.md) | when something does not work |

## Requirements

- Windows 10 1809 or newer / Windows 11 (64-bit).
- A Garry's Mod dedicated server (SteamCMD app 4020), any branch, 32- or 64-bit.
- Nothing else for the recommended download. The *small* download needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

## Building from source

```powershell
git clone --recursive https://github.com/RainBowSheepx/gm_betterconsole.git
cd gm_betterconsole
.\scripts\build-native.ps1          # the server module (needs Visual Studio 2022 with C++)
dotnet run --project src\BetterConsole.App
```

See [docs/building.md](docs/building.md) for tests and packaging.

## License

[MIT](LICENSE). Uses [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) (MIT),
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT) and
[gmod-module-base](https://github.com/Facepunch/gmod-module-base).
Garry's Mod is a trademark of Facepunch Studios; this project is not affiliated with Facepunch.
