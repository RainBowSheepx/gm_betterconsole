# Lua API — tabs, statistics, player menu and status items from server addons

Any server addon can show its own information in BetterConsole: a tab with widgets (key numbers, text,
key/value list, table, log, chart, buttons), numbers and charts on the Statistics tab, items in the
right-click menu of the Players tab and in the status bar. Everything here is server-side Lua.

![A tab made by an addon](images/addon-tab.png)

## Ground rules

- The global table `BetterConsole` exists on every server that has the companion addon, **whether or
  not** BetterConsole is connected. While it is not connected every call does nothing, so you can
  call it unconditionally. Check `if BetterConsole then` only to support servers without the addon.
- Define your tab **once, when your addon loads** (or in `BetterConsoleReady`). BetterConsole remembers
  every tab, widget, the last value of each widget and the last lines of logs and charts, and sends
  them again after a map change or when the app reconnects.
- Updates are cheap (a small JSON message through a pipe on another thread), but there is no point
  in updating faster than people can read: once a second or slower is plenty.
- `BetterConsole.IsActive()` tells you whether the app is connected right now — skip expensive data
  collection while it is not.

## Quick example

```lua
if not BetterConsole then return end

local tab = BetterConsole.AddTab("myjobs", { title = "Jobs", order = 50 })

local summary = tab:KeyValue("summary", { title = "Now", span = 4 })
local chart   = tab:Chart("queue", { title = "Queue length", span = 8,
                                     series = { { name = "waiting", color = "#F2B33D" } } })
local log     = tab:Log("log", { title = "Events", max = 300 })
local buttons = tab:Buttons("actions", { buttons = {
    { id = "flush", text = "Flush the queue", style = "danger", confirm = "Drop every waiting job?" },
} })

tab:OnAction(function(widgetId, actionId)
    if actionId == "flush" then
        Jobs.Flush()
        log:Append("Queue flushed from the console", Color(240, 90, 90))
    end
end)

timer.Create("myjobs_console", 2, 0, function()
    if not BetterConsole.IsActive() then return end
    summary:Set({ Workers = Jobs.Workers(), Waiting = #Jobs.Queue, Done = Jobs.DoneToday })
    chart:Push(#Jobs.Queue)
end)

hook.Add("JobFailed", "myjobs_console", function(job, err)
    log:Append(job.name .. ": " .. err, Color(255, 120, 90))
end)
```

A complete, working example is in [`samples/lua/betterconsole_example`](../samples/lua/betterconsole_example).

## Tabs

### `BetterConsole.AddTab(id, options) → tab`

Creates the tab, or returns the existing one with that id (and updates its title / order / icon).

| Option | Type | Default | |
|---|---|---|---|
| `title` | string | the id | text on the tab |
| `order` | number | 100 | position: lower is more to the left; built-in tabs use 0–40 |
| `icon` | string | a document | the hex code of a [Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font) glyph (`"E7FC"`), or a letter or emoji. When the app shows only the icons of its tabs (a narrow window, or the user's choice), this is all that tells your tab apart |

### `BetterConsole.GetTab(id) → tab | nil`

### `tab:Remove()`

Removes the tab from BetterConsole and forgets it.

### `tab:OnAction(function(widgetId, actionId) end)`

Called when a button of this tab is pressed in BetterConsole. (Each widget may also have its own
`onAction` option, see *Buttons*.) The callback runs on the server; errors in it are reported as Lua
errors.

### `tab:Widget(id) → widget | nil`

## Widgets

Widgets are laid out left to right on a 12-column grid and wrap like words. Options common to every
widget:

| Option | Type | Default | |
|---|---|---|---|
| `title` | string | none | heading of the card |
| `span` | 1–12 | 12 | width in twelfths of the tab (narrow windows show everything full width) |

Every widget has `widget:Clear()` and `widget:Remove()` (it leaves the tab; the constructor adds
it back). Calling a constructor again with the same id updates the widget's options. Ids are unique
per tab: a Stat and a Chart with the same id are one widget, and the second replaces the first.

### Stat — `tab:Stat(id, options)`

A key number: the title in small capitals, a big value, a line under it — the cards at the top of the
Statistics tab. On a tab of your own it is 3 twelfths wide unless `span` says otherwise.

| Option | |
|---|---|
| `tooltip` | shown when the pointer is over the card |
| `order` | on the Statistics tab: its place among the built-in numbers (see below) |

```lua
local money = tab:Stat("money", { title = "Money", tooltip = "In all wallets" })
money:Set("$1.2M", "40 wallets")                                -- value, line under it
money:Set({ value = "$0", sub = "empty", color = Color(240, 90, 90) })
```

### Text — `tab:Text(id, options)`

```lua
local note = tab:Text("note", { title = "Read me" })
note:Set("Any text. It can be selected and copied.")
```

### KeyValue — `tab:KeyValue(id, options)`

Two columns, label and value.

```lua
info:Set({ Map = game.GetMap(), Players = player.GetCount() })   -- sorted by key
info:Set({ { "First", 1 }, { "Second", 2 } })                     -- your order
```

Values may be strings, numbers or booleans.

### Table — `tab:Table(id, options)`

| Option | |
|---|---|
| `columns` | list of column titles |

```lua
local t = tab:Table("top", { title = "Richest", columns = { "Player", "Money", "Job" } })
t:Set({ { "Alice", 52000, "Mayor" }, { "Bob", 1200, "Cook" } })
```

Up to 2000 rows; each `Set` replaces the content.

### Log — `tab:Log(id, options)`

| Option | Default | |
|---|---|---|
| `max` | 300 | lines kept (10–5000) |

```lua
log:Append("Something happened")
log:Append("Something bad happened", Color(255, 90, 90))   -- or "#FF5A5A"
```

The log follows new lines while it is scrolled to the end.

### Chart — `tab:Chart(id, options)`

A time series chart, one value per series and push.

| Option | Default | |
|---|---|---|
| `series` | one series "Value" | list of `{ name = "...", color = Color(...) or "#RRGGBB" }` |
| `unit` | none | shown after values, e.g. `"ms"` |
| `max` | 600 | points kept per series (10–5000) |

```lua
local c = tab:Chart("perf", { title = "Queue", series = { { name = "waiting" }, { name = "running" } }, unit = "jobs" })
c:Push(#waiting, #running)        -- or c:Push({ #waiting, #running })
```

Points are stamped with the time they were pushed; the chart shows the last 10 minutes.

### Buttons — `tab:Buttons(id, options)`

| Button field | | |
|---|---|---|
| `id` | required | passed to your action handler |
| `text` | the id | label |
| `style` | `"default"` | `"primary"` or `"danger"` |
| `confirm` | none | if set, BetterConsole asks this question before sending the action |

A widget can carry its own handler: `tab:Buttons("x", { buttons = {...}, onAction = function(widgetId, actionId) end })`.
`widget:Set({ ...buttons... })` replaces the buttons.

## The Statistics tab

`BetterConsole.Stats` is the built-in Statistics tab with the methods of a tab of your own: Stat cards
join the key numbers at the top, charts join the charts (two to a row unless `span = 12`, and they
follow the tab's 1 min … 1 hour window), any other widget goes below the charts. `order` places a card
or chart among the built-in ones:

| Order | Numbers | Charts |
|---|---|---|
| 10 | `fps` | `chart.frame` |
| 20 | `frame` | `chart.load` |
| 30 | `load` | `chart.rate` |
| 40 | `tick` | `chart.memory` |
| 50 | `cpu` | `chart.network` |
| 60 | `memory` | `chart.players` |
| 70 | `lua` | `chart.entities` |
| 80 | `players` | |
| 90 | `entities` | |
| 100 | `network` | |
| 110 | `uptime` | |

Without `order` your parts come after the built-in ones. The two sections below the charts are
`spikes` (lag spikes) and `profiler`.

A chart keeps what your addon pushed in this Lua state: after a map change it starts again, and
after BetterConsole restarts it gets back the last `max` points (600 by default). For an hour of
history push once a minute or raise `max`.

```lua
local stats = BetterConsole.Stats
local money = stats:Stat("money", { title = "Money", order = 85 })    -- after Players
local chart = stats:Chart("money_chart", { title = "Money", unit = "$" })
timer.Create("myeconomy_stats", 1, 0, function()
    local total = MyEconomy.Total()
    money:Set(string.Comma(total), #player.GetHumans() .. " wallets")
    chart:Push(total)
end)
```

### `BetterConsole.Stats:Hide(id, ...)` / `BetterConsole.Stats:Show(id, ...)`

Hides (shows again) built-in parts by their ids from the table above, for example a number your
addon shows better: `BetterConsole.Stats:Hide("players", "chart.players")`.

## Player menu

Items of your own in the right-click menu of the Players tab. The menu works on the selected players:
a command runs once for each of them, an `onRun` function once with all of them.

### `BetterConsole.AddPlayerAction(id, options) → action`

| Option | Default | |
|---|---|---|
| `text` | the id | the menu item |
| `icon` | none | hex code of a Segoe Fluent Icons glyph (`"E7BA"`) or a letter |
| `order` | 300 | position: kick 100, ban 110, group 120, gag 200, mute 210, jail 220, copy 400–420, profile 430. A separator goes between hundreds, so 300 makes a section of its own |
| `command` | | a console command, run for each player. `{target}` addresses the player (as ULX does, quoted) — use it for targets; `{userid}`, `{steamid}` (write `"{steamid}"`: the engine splits words at `:`), `{steamid64}`, `{name}` (for messages only: a name such as `*` or `@` would be a ULX target of its own), and `{field}` for each field of the dialog (a field cannot have one of these names). Names and typed values lose quotes, semicolons and line breaks. A command without a player placeholder runs once |
| `onRun` | | `function(players, values)` on the server instead (it wins when both are given): the selected players that are still there, the values of the dialog by field id (numbers for `type = "number"`). One of `command` and `onRun` is needed |
| `fields` | none | a dialog before it runs, see below |
| `confirm` | none | a question before it runs; `{players}`, `{name}`, `{count}` are replaced |
| `danger` | false | the dialog's button is red |
| `filter` | none | `function(ply) → bool`: the item is only for the players it accepts. It runs with each player list (every second or two), so keep it cheap. Two items with opposite filters make a toggle |
| `bots` | true | false: not for bots |
| `multi` | true | false: only while a single player is selected |

Fields: `{ id = "reason", text = "Reason", default = "" }` (text),
`{ id = "minutes", text = "Minutes", type = "number", default = 5 }`,
`{ id = "where", text = "Where", choices = { "spawn", "jail" }, default = "spawn" }` (a drop-down;
`{ { "Shown text", "value" }, ... }` for other values than texts, `editable = true` to allow typing).

```lua
-- A toggle: each selected player gets the item that fits.
BetterConsole.AddPlayerAction("freeze", {
    text = "Freeze", icon = "E769",
    filter = function(ply) return not ply:IsFrozen() end,
    onRun = function(plys) for _, p in ipairs(plys) do p:Freeze(true) end end,
})
BetterConsole.AddPlayerAction("unfreeze", {
    text = "Unfreeze", icon = "E768",
    filter = function(ply) return ply:IsFrozen() end,
    onRun = function(plys) for _, p in ipairs(plys) do p:Freeze(false) end end,
})

-- No Lua at all: a command of your admin mod, with a dialog.
BetterConsole.AddPlayerAction("slap", {
    text = "Slap", order = 230,
    fields = { { id = "damage", text = "Damage", choices = { "0", "10", "50" }, default = "0" } },
    command = "ulx slap {target} {damage}",
})
```

### `BetterConsole.RemovePlayerAction(id)` (or `action:Remove()`)

### `BetterConsole.HidePlayerAction(id, ...)` / `BetterConsole.ShowPlayerAction(id, ...)`

Hides built-in items: `kick`, `ban`, `group`, `gag` (and Ungag), `mute` (and Unmute), `jail` (and
Unjail), `copy` (the three copy items), `profile`. A server with its own ban system can hide `ban` and
add its own item in its place (`order = 110`).

## Status bar

### `BetterConsole.SetStatus(id, text [, tooltip [, color]])`

Adds or updates a text item on the right of the status bar.

```lua
BetterConsole.SetStatus("myjobs", "Jobs 12", "Waiting jobs", Color(242, 179, 61))
```

### `BetterConsole.SetStatus(id, options)`

The same with a table, to look like the built-in items: a dimmed `label` before the `text`.

| Option | |
|---|---|
| `text` | the value |
| `label` | dimmed before it ("Jobs") |
| `tooltip`, `color` | as above |
| `name` | what the menu of the status bar calls it (right-click the status bar: the user can hide any item there); default the label, else the id |
| `order` | position among the items of addons (500) and plugins (1000 unless they say otherwise) |

```lua
BetterConsole.SetStatus("myjobs", { label = "Jobs", text = #Jobs.Queue, tooltip = "Waiting jobs", name = "Job queue" })
```

### `BetterConsole.RemoveStatus(id)`

## Notifications

### `BetterConsole.Notify(text [, kind])`

A toast in the corner of the BetterConsole window. `kind`: `"info"` (default), `"success"`,
`"warning"`, `"error"`.

## Talking to C# plugins

### `BetterConsole.SendToApp(type, data)`

Sends any table to the app; C# plugins receive it in `ctx.Bridge.MessageReceived` with that `type`.

### Hook `BetterConsoleMessage(type, data)`

A plugin's `ctx.Bridge.Send(type, data)` arrives here:

```lua
hook.Add("BetterConsoleMessage", "myaddon", function(msgType, data)
    if msgType == "myaddon.reload" then MyAddon.Reload() end
end)
```

### Hook `BetterConsoleReady()`

Called every time BetterConsole (re)connects — after a map change too. Your tabs are already resent
by then; use it to push data that you only compute on request.

### `BetterConsole.IsActive() → bool`

## What the companion addon does by itself

You do not need any of this to use BetterConsole — it is here so you know what runs on your server
while BetterConsole is connected:

- `hook.Add("OnLuaError")` and `hook.Add("OnClientLuaError")` forward errors (repeats of the same
  error at most once a second, with a count).
- A `Think` and a `Tick` hook measure frame and tick times; once a second a summary is sent.
- `timer.Create` / `timer.Simple` / `timer.Adjust` are wrapped from the start so the profiler can
  name timers; while the profiler is off the wrapper just calls your function.
- While the **Players** tab is open: `StartCommand` / `FinishMove` hooks and a `net.Incoming`
  wrapper measure the CPU per player.
- The player list (every one or two seconds) reads `cl_updaterate` and `cl_cmdrate` of each player
  (`GetInfoNum`) and runs the `filter` functions of player menu items.
- While the **profiler** runs: hook functions, `net.Receive` handlers and `net.Start` / `net.Send*`
  are wrapped. Everything is restored when it stops.
- The command `betterconsole_exec <hex>` (server console only) runs commands with non-ASCII text.
- A console variable `betterconsole_probe` marks where the engine's command list starts (for
  auto-completion).

None of it is active when the server runs without BetterConsole.
