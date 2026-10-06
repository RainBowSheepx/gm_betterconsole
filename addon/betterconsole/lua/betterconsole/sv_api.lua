--[[
BetterConsole public API for other addons (server side).

  if BetterConsole then
      local tab = BetterConsole.AddTab("myaddon", { title = "My addon", order = 50, icon = "E7FC" })
      local info = tab:KeyValue("info", { title = "State", span = 6 })
      info:Set({ Version = "1.2", Jobs = 5 })
      local log = tab:Log("log", { title = "Events", max = 300 })
      log:Append("Something happened", Color(120, 220, 120))
      local chart = tab:Chart("money", { title = "Money in game", series = { { name = "Total", color = "#3B82F6" } }, unit = "$" })
      chart:Push(123456)
      local buttons = tab:Buttons("actions", { buttons = { { id = "reset", text = "Reset economy", style = "danger", confirm = "Really?" } } })
      tab:OnAction(function(widgetId, actionId) print("pressed", widgetId, actionId) end)
      BetterConsole.SetStatus("myaddon", "Jobs: 5", "Jobs in the queue")
  end

Widgets: Stat, Text, KeyValue, Table, Log, Chart, Buttons. Options common to all: title, span (1-12 columns of
the tab's grid, default 12). Everything you send is remembered and sent again when the app reconnects
(map change, app restart), so define tabs once at load time.

  -- The Statistics tab takes the same widgets: Stat cards go to the key numbers, charts to the charts.
  local money = BetterConsole.Stats:Stat("money", { title = "Money", tooltip = "In all wallets" })
  money:Set("$1.2M", "40 wallets")
  BetterConsole.Stats:Hide("chart.entities")   -- a built-in part

  -- An item of the right-click menu of the Players tab.
  BetterConsole.AddPlayerAction("slay", { text = "Slay", icon = "E7BA", command = "ulx slay {target}" })
  BetterConsole.AddPlayerAction("heal", { text = "Heal", onRun = function(plys) for _, p in ipairs(plys) do p:SetHealth(100) end end })

Hooks:
  BetterConsoleReady()               - the app is connected (called again after every reconnect)
  BetterConsoleMessage(type, data)   - a message from a BetterConsole C# plugin (BetterConsole.SendToApp
                                       goes the other way)
]]
BetterConsole = BetterConsole or {}
local BC = BetterConsole

BC.Version = "0.3.0"
BC.Registry = BC.Registry or { tabs = {}, order = {}, status = {} }
local R = BC.Registry
R.statsHidden = R.statsHidden or {}   -- [id] = true: built-in parts of the Statistics tab that are hidden
R.pactions = R.pactions or {}         -- player actions by id
R.paorder = R.paorder or {}
R.pahidden = R.pahidden or {}         -- [id] = true: built-in items of the player menu that are hidden

-- Overridden by sv_core.lua once connected.
BC.Emit = BC.Emit or function() end
function BC.IsActive() return BC.Connected == true end

local function ColorString(c)
	if c == nil then return nil end
	if isstring(c) then return c end
	if istable(c) and c.r then return string.format("#%02X%02X%02X", c.r, c.g, c.b) end
	return nil
end
BC.ColorString = ColorString

-- Colours inside option tables (series colours, button colours) become "#RRGGBB" strings.
local function CleanOpts(opts)
	if not istable(opts) then return {} end
	local out = {}
	for k, v in pairs(opts) do
		if IsColor and IsColor(v) or (istable(v) and v.r and v.g and v.b and not v[1]) then
			out[k] = ColorString(v)
		elseif istable(v) then
			out[k] = CleanOpts(v)
		elseif isfunction(v) then
			-- callbacks stay in Lua
		else
			out[k] = v
		end
	end
	return out
end

local Widget = {}
Widget.__index = Widget

local function SendWidgetData(w, op, data)
	BC.Emit({ t = "wd", tab = w.tab.id, id = w.id, op = op, data = data })
end

--- Replaces the content (Text: string; KeyValue: { key = value } or { {key, value}, ... };
--- Table: { {cell, cell, ...}, ... }; Buttons: a new list of buttons; Stat: value [, sub] or
--- { value = ..., sub = ..., color = ... }).
function Widget:Set(data, sub)
	if self.kind == "stat" then
		local t = istable(data) and not (IsColor and IsColor(data)) and data or { value = data, sub = sub }
		data = {
			value = t.value ~= nil and tostring(t.value) or "",
			sub = t.sub ~= nil and tostring(t.sub) or nil,
			color = ColorString(t.color),
		}
	end
	self.state = data
	SendWidgetData(self, "set", data)
	return self
end

--- Log: adds a line. color is optional (Color or "#RRGGBB").
function Widget:Append(text, color)
	local entry = { text = tostring(text), color = ColorString(color), time = os.time() }
	local buf = self.buffer
	buf[#buf + 1] = entry
	local max = self.max
	if #buf > max then table.remove(buf, 1) end
	SendWidgetData(self, "append", entry)
	return self
end

--- Chart: adds one point per series (in the order of opts.series).
function Widget:Push(...)
	local values = { ... }
	if istable(values[1]) then values = values[1] end
	local entry = { time = os.time() + (SysTime() % 1), values = values }
	local buf = self.buffer
	buf[#buf + 1] = entry
	if #buf > self.max then table.remove(buf, 1) end
	SendWidgetData(self, "push", entry)
	return self
end

function Widget:Clear()
	self.state = nil
	if self.buffer then self.buffer = {} end
	SendWidgetData(self, "clear", nil)
	return self
end

--- Takes the widget off its tab (calling the constructor again with its id adds it back).
function Widget:Remove()
	local tab = self.tab
	if tab.widgets[self.id] ~= self then return end
	tab.widgets[self.id] = nil
	tab.actions[self.id] = nil
	for i, id in ipairs(tab.worder) do
		if id == self.id then table.remove(tab.worder, i) break end
	end
	BC.Emit({ t = "w_rm", tab = tab.id, id = self.id })
end

local Tab = {}
Tab.__index = Tab

local function AddWidget(tab, kind, id, opts)
	id = tostring(id)
	local w = tab.widgets[id]
	if not w then
		w = setmetatable({ tab = tab, id = id }, Widget)
		tab.widgets[id] = w
		tab.worder[#tab.worder + 1] = id
	end
	w.kind = kind
	w.opts = CleanOpts(opts)
	w.max = math.Clamp(tonumber(istable(opts) and opts.max) or (kind == "chart" and 600 or 300), 10, 5000)
	if kind == "log" or kind == "chart" then w.buffer = w.buffer or {} end
	if istable(opts) and isfunction(opts.onAction) then tab.actions[id] = opts.onAction end
	BC.Emit({ t = "w", tab = tab.id, id = id, kind = kind, opts = w.opts })
	return w
end

--- A key number: a big value with its title above and a line under it. opts: title, tooltip, order (on the
--- Statistics tab: position among the built-in numbers, which use 10-110).
function Tab:Stat(id, opts) return AddWidget(self, "stat", id, opts) end
function Tab:Text(id, opts) return AddWidget(self, "text", id, opts) end
function Tab:KeyValue(id, opts) return AddWidget(self, "kv", id, opts) end
function Tab:Table(id, opts) return AddWidget(self, "table", id, opts) end
function Tab:Log(id, opts) return AddWidget(self, "log", id, opts) end
function Tab:Chart(id, opts) return AddWidget(self, "chart", id, opts) end
function Tab:Buttons(id, opts) return AddWidget(self, "buttons", id, opts) end

--- fn(widgetId, actionId) is called when a button of this tab is pressed in BetterConsole.
function Tab:OnAction(fn)
	self.onAction = fn
	return self
end

function Tab:Widget(id) return self.widgets[tostring(id)] end

function Tab:Remove()
	R.tabs[self.id] = nil
	for i, id in ipairs(R.order) do
		if id == self.id then table.remove(R.order, i) break end
	end
	BC.Emit({ t = "tab_rm", id = self.id })
end

--- Adds (or returns the existing) tab. opts: title, order (lower = more to the left; built-in tabs are 0-40),
--- icon (hex code of a Segoe Fluent Icons glyph such as "E7FC", or a letter or emoji; shown alone when the
--- app shows tab icons only).
function BC.AddTab(id, opts)
	id = tostring(id)
	if id == "@stats" then return BC.Stats end
	opts = istable(opts) and opts or {}
	local tab = R.tabs[id]
	if not tab then
		tab = setmetatable({ id = id, widgets = {}, worder = {}, actions = {} }, Tab)
		R.tabs[id] = tab
		R.order[#R.order + 1] = id
	end
	tab.title = tostring(opts.title or tab.title or id)
	tab.order = tonumber(opts.order) or tab.order or 100
	if opts.icon ~= nil then tab.icon = tostring(opts.icon) end
	BC.Emit({ t = "tab", id = id, title = tab.title, order = tab.order, icon = tab.icon })
	return tab
end

function BC.GetTab(id) return R.tabs[tostring(id)] end

-- ---------------------------------------------------------------------------------------- Statistics tab

--[[
BetterConsole.Stats is the built-in Statistics tab, with the methods of a tab of your own: Stat cards join the
key numbers at the top, charts join the charts (and follow the tab's 1 min ... 1 hour window), the other
widgets go below the charts. Stats:Hide(id, ...) / Stats:Show(id, ...) hide built-in parts:
  numbers  fps, frame, load, tick, cpu, memory, lua, players, entities, network, uptime
  charts   chart.frame, chart.load, chart.rate, chart.memory, chart.network, chart.players, chart.entities
  sections spikes, profiler
]]
local StatsTab = setmetatable({}, { __index = Tab })
StatsTab.__index = StatsTab

local function SendStatsHidden()
	local ids = {}
	for id in pairs(R.statsHidden) do ids[#ids + 1] = id end
	BC.Emit({ t = "stats_hide", ids = ids })
end

function StatsTab:Hide(...)
	for _, id in ipairs({ ... }) do R.statsHidden[tostring(id)] = true end
	SendStatsHidden()
	return self
end

function StatsTab:Show(...)
	for _, id in ipairs({ ... }) do R.statsHidden[tostring(id)] = nil end
	SendStatsHidden()
	return self
end

-- The Statistics tab itself stays.
function StatsTab:Remove() end

R.tabs["@stats"] = R.tabs["@stats"] or { id = "@stats", widgets = {}, worder = {}, actions = {} }
BC.Stats = setmetatable(R.tabs["@stats"], StatsTab)

-- ---------------------------------------------------------------------------------------- status bar

--- A text item in the status bar of BetterConsole.
---   BC.SetStatus(id, text [, tooltip [, color]])
---   BC.SetStatus(id, { text = "12", label = "Jobs", tooltip = "...", color = Color(...), name = "Job queue", order = 500 })
--- label: shown dimmed before the text, like the built-in items; name: in the menu of the status bar (right-click),
--- where the user can hide the item; order: the position among the other items of addons and plugins.
function BC.SetStatus(id, text, tooltip, color)
	id = tostring(id)
	local st
	if istable(text) and not (IsColor and IsColor(text)) then
		local o = text
		st = {
			t = "st", id = id, text = tostring(o.text or ""),
			label = o.label ~= nil and tostring(o.label) or nil,
			tip = o.tooltip ~= nil and tostring(o.tooltip) or nil,
			color = ColorString(o.color),
			name = o.name ~= nil and tostring(o.name) or nil,
			order = tonumber(o.order),
		}
	else
		st = { t = "st", id = id, text = tostring(text or ""), tip = tooltip and tostring(tooltip) or nil, color = ColorString(color) }
	end
	R.status[id] = st
	BC.Emit(st)
end

function BC.RemoveStatus(id)
	id = tostring(id)
	R.status[id] = nil
	BC.Emit({ t = "st_rm", id = id })
end

--- A toast in the corner of the BetterConsole window. kind: "info", "success", "warning", "error".
function BC.Notify(text, kind)
	BC.Emit({ t = "notify", text = tostring(text), kind = kind or "info" })
end

--- A custom message for BetterConsole C# plugins (they get it in IPluginContext.Bridge.MessageReceived).
function BC.SendToApp(msgType, data)
	BC.Emit({ t = "custom", type = tostring(msgType), data = data })
end

-- ---------------------------------------------------------------------------------------- player menu

--[[
Items of the right-click menu of the Players tab. The menu works on the selected players: an item runs once
for each of them (a command) or once with all of them (onRun). Built-in items have the order 100-120 (kick,
ban, group), 200-220 (gag, mute, jail) and 400-430 (copy, profile); a separator goes between hundreds, so the
default order 300 makes a section of its own between jail and the copy items.
]]
local PlayerAction = {}
PlayerAction.__index = PlayerAction

function PlayerAction:Remove() BC.RemovePlayerAction(self.id) end

--- opts:
---   text      the menu item ("Slay")
---   icon      hex code of a Segoe Fluent Icons glyph ("E7BA") or a letter
---   order     position (see above), default 300
---   command   a console command run for each player: {target} (ULX's "$id", quoted: use it to address the
---             player), {userid}, {steamid} (write "{steamid}": the engine splits words at ':'), {steamid64},
---             {name} (for messages only: a name like "*" or "@" is a ULX target of its own), and {<field id>}
---             for the values typed in the dialog (a field cannot be called like one of these)
---   onRun     function(players, values) on the server instead (it wins when both are given): the players
---             still on it, the dialog's values
---   fields    a dialog before it runs: { { id = "reason", text = "Reason", default = "" },
---             { id = "minutes", text = "Minutes", type = "number", default = 5 },
---             { id = "where", text = "Where", choices = { "spawn", "jail" }, default = "spawn" } }
---   confirm   a question before it runs ("Slay {players}?")
---   danger    the confirm button is red
---   filter    function(ply) → bool: shown only for the players it accepts (checked with each player list,
---             every second or two): two actions with opposite filters make a toggle (Freeze / Unfreeze)
---   bots      false: not for bots; multi: false: only when one player is selected
function BC.AddPlayerAction(id, opts)
	id = tostring(id)
	opts = istable(opts) and opts or {}
	local a = R.pactions[id]
	if not a then
		a = setmetatable({ id = id }, PlayerAction)
		R.pactions[id] = a
		R.paorder[#R.paorder + 1] = id
	end
	a.onRun = isfunction(opts.onRun) and opts.onRun or nil
	if not a.onRun and opts.command == nil then
		ErrorNoHalt("[BetterConsole] player action '" .. id .. "' has neither a command nor onRun: it does nothing\n")
	end
	local hadFilter = a.filter ~= nil
	a.filter = isfunction(opts.filter) and opts.filter or nil
	a.warned = nil
	a.msg = {
		t = "pa", id = id, text = tostring(opts.text or id),
		icon = opts.icon ~= nil and tostring(opts.icon) or nil,
		order = tonumber(opts.order) or 300,
		command = opts.command ~= nil and tostring(opts.command) or nil,
		fields = istable(opts.fields) and CleanOpts(opts.fields) or nil,
		confirm = opts.confirm ~= nil and tostring(opts.confirm) or nil,
		danger = opts.danger == true or nil,
		bots = opts.bots ~= false,
		multi = opts.multi ~= false,
		filtered = a.filter ~= nil or nil,
		run = a.onRun ~= nil or nil,
	}
	BC.Emit(a.msg)
	-- The players' flags change with the filter: a new list at once.
	if (hadFilter or a.filter) and BC.PlayersChanged then BC.PlayersChanged() end
	return a
end

function BC.RemovePlayerAction(id)
	id = tostring(id)
	if not R.pactions[id] then return end
	R.pactions[id] = nil
	for i, v in ipairs(R.paorder) do
		if v == id then table.remove(R.paorder, i) break end
	end
	BC.Emit({ t = "pa_rm", id = id })
end

local function SendHiddenActions()
	local ids = {}
	for id in pairs(R.pahidden) do ids[#ids + 1] = id end
	BC.Emit({ t = "pa_hide", ids = ids })
end

--- Hides built-in items of the player menu: kick, ban, group, gag (and ungag), mute, jail, copy (the copy
--- items), profile. Your own items are removed with RemovePlayerAction.
function BC.HidePlayerAction(...)
	for _, id in ipairs({ ... }) do R.pahidden[tostring(id)] = true end
	SendHiddenActions()
end

function BC.ShowPlayerAction(...)
	for _, id in ipairs({ ... }) do R.pahidden[tostring(id)] = nil end
	SendHiddenActions()
end

--- For the player list: { [id] = true } for each action with a filter that accepts this player, or nil.
function BC.PlayerActionFlags(ply)
	local flags
	for _, id in ipairs(R.paorder) do
		local a = R.pactions[id]
		if a and a.filter then
			local ok, res = pcall(a.filter, ply)
			if not ok then
				if not a.warned then
					a.warned = true
					ErrorNoHalt("[BetterConsole] filter of player action '" .. id .. "' failed: " .. tostring(res) .. "\n")
				end
			elseif res then
				flags = flags or {}
				flags[id] = true
			end
		end
	end
	return flags
end

-- An item with onRun was chosen in the app: { id, uids = { userid, ... }, values = { field = value } }.
function BC.HandlePlayerAction(msg)
	local a = R.pactions[tostring(msg.id)]
	if not a or not a.onRun then return end
	local wanted = {}
	if istable(msg.uids) then
		for _, u in ipairs(msg.uids) do
			if tonumber(u) then wanted[tonumber(u)] = true end
		end
	end
	local plys = {}
	for _, ply in ipairs(player.GetAll()) do
		if wanted[ply:UserID()] then plys[#plys + 1] = ply end
	end
	if #plys == 0 then return end
	local ok, err = pcall(a.onRun, plys, istable(msg.values) and msg.values or {})
	if not ok then ErrorNoHalt("[BetterConsole] player action '" .. a.id .. "' failed: " .. tostring(err) .. "\n") end
	-- What it did may change the filters (Freeze → Unfreeze): a new player list at once.
	if BC.PlayersChanged then BC.PlayersChanged() end
end

-- ---------------------------------------------------------------------------------------- after a (re)connect

local function ResendWidgets(tab)
	for _, wid in ipairs(tab.worder) do
		local w = tab.widgets[wid]
		BC.Emit({ t = "w", tab = tab.id, id = wid, kind = w.kind, opts = w.opts })
		if w.state ~= nil then SendWidgetData(w, "set", w.state) end
		if w.buffer and #w.buffer > 0 then SendWidgetData(w, "many", w.buffer) end
	end
end

-- Sends everything the registry holds.
function BC.ResendRegistry()
	for _, id in ipairs(R.order) do
		local tab = R.tabs[id]
		if tab then
			BC.Emit({ t = "tab", id = id, title = tab.title, order = tab.order, icon = tab.icon })
			ResendWidgets(tab)
		end
	end
	ResendWidgets(BC.Stats)
	if next(R.statsHidden) then SendStatsHidden() end
	for _, st in pairs(R.status) do BC.Emit(st) end
	for _, id in ipairs(R.paorder) do
		local a = R.pactions[id]
		if a then BC.Emit(a.msg) end
	end
	if next(R.pahidden) then SendHiddenActions() end
end

-- A button was pressed in the app.
function BC.HandleAction(msg)
	local tab = R.tabs[tostring(msg.tab)]
	if not tab then return end
	local widgetFn = tab.actions[tostring(msg.widget)]
	local fn = widgetFn or tab.onAction
	if not fn then return end
	local ok, err = pcall(fn, tostring(msg.widget), tostring(msg.id))
	if not ok then ErrorNoHalt("[BetterConsole] action handler of tab '" .. tab.id .. "' failed: " .. tostring(err) .. "\n") end
end
