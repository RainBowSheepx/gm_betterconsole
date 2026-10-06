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
      tab:OnAction(function(widgetId, actionId, values) print("pressed", widgetId, actionId) end)
      local form = tab:Form("settings", { title = "Settings", fields = { { convar = "myaddon_enabled", type = "bool" } } })
      BetterConsole.SetStatus("myaddon", "Jobs: 5", "Jobs in the queue")
  end

Widgets: Stat, Text, KeyValue, Table, Log, Chart, Buttons, Form. Options common to all: title, span (1-12 columns
of the tab's grid, default 12). Everything you send is remembered and sent again when the app reconnects
(map change, app restart), so define tabs once at load time. Functions added in later versions are found
by their presence: if isfunction(tab.Form) then ... end (docs/lua-api.md says which version has what).

  -- The Statistics tab takes the same widgets: Stat cards go to the key numbers, charts to the charts.
  local money = BetterConsole.Stats:Stat("money", { title = "Money", tooltip = "In all wallets" })
  money:Set("$1.2M", "40 wallets")
  BetterConsole.Stats:Hide("chart.entities")   -- a built-in part

  -- An item of the right-click menu of the Players tab.
  BetterConsole.AddPlayerAction("slay", { text = "Slay", icon = "E7BA", command = "ulx slay {target}" })
  BetterConsole.AddPlayerAction("heal", { text = "Heal", onRun = function(plys) for _, p in ipairs(plys) do p:SetHealth(100) end end })

  -- A profiler of your own instead of the built-in one, explanations of long frames.
  BetterConsole.SetProfiler({ name = "My profiler", start = function() end, stop = function() end })
  BetterConsole.Stats:OnSpike("myaddon", function(spike) spike.causes = { { text = "my part 12 ms" } } end)

Hooks:
  BetterConsoleReady()               - the app is connected (called again after every reconnect)
  BetterConsoleMessage(type, data)   - a message from a BetterConsole C# plugin (BetterConsole.SendToApp
                                       goes the other way)
  BetterConsoleProfiler(on)          - profiling started or stopped (the built-in profiler or yours)
  BetterConsoleCapture(on)           - the detailed capture of lag spikes started or stopped
]]
BetterConsole = BetterConsole or {}
local BC = BetterConsole

BC.Version = "0.4.0"
BC.Registry = BC.Registry or { tabs = {}, order = {}, status = {} }
local R = BC.Registry
R.statsHidden = R.statsHidden or {}   -- [id] = true: built-in parts of the Statistics tab that are hidden
R.pactions = R.pactions or {}         -- player actions by id
R.paorder = R.paorder or {}
R.pahidden = R.pahidden or {}         -- [id] = true: built-in items of the player menu that are hidden
R.shown = R.shown or {}               -- [tab id] = true while the user looks at that tab ("@stats": Statistics)
R.spikeFns = R.spikeFns or {}         -- Stats:OnSpike functions by id
R.spikeOrder = R.spikeOrder or {}

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
		elseif type(v) == "number" and v - v ~= 0 then
			-- NaN and infinities: JSON has no way to write them
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
--- { value = ..., sub = ..., color = ... }; Form: { fieldId = value } for fields without a convar).
function Widget:Set(data, sub)
	if self.kind == "form" then
		BC.SetFormValues(self, data)
		return self
	end
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

-- What the app gets as the widget's options: a form's fields as they are on this server now (BC.FormOpts).
local function WidgetOpts(w)
	if w.kind == "form" then return BC.FormOpts(w) end
	return w.opts
end

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
	if kind == "form" then BC.DefineForm(w, istable(opts) and opts or {}) end
	BC.Emit({ t = "w", tab = tab.id, id = id, kind = kind, opts = WidgetOpts(w) })
	if kind == "form" then BC.SendFormValues(w, true) end
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
--- Settings: fields bound to console variables of the server or to values of your own (see docs/lua-api.md).
function Tab:Form(id, opts) return AddWidget(self, "form", id, opts) end

--- fn(widgetId, actionId, values) is called when a button of this tab is pressed in BetterConsole (values: what
--- the button's dialog asked for, by field id; nil for a button without fields).
function Tab:OnAction(fn)
	self.onAction = fn
	return self
end

--- fn(widgetId, fieldId, value) is called when a field of a form of this tab without a convar is changed in
--- BetterConsole (value: a number for "number", true/false for "bool", else a string).
function Tab:OnChange(fn)
	self.onChange = fn
	return self
end

--- fn(shown) is called when the user opens this tab in BetterConsole (true) and leaves it (false): collect
--- expensive data for its widgets only while it is seen.
function Tab:OnShow(fn)
	self.onShow = fn
	if isfunction(fn) and R.shown[self.id] then BC.CallShow(self, true) end
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
		BC.Emit({ t = "w", tab = tab.id, id = wid, kind = w.kind, opts = WidgetOpts(w) })
		if w.kind == "form" then BC.SendFormValues(w, true)
		elseif w.state ~= nil then SendWidgetData(w, "set", w.state) end
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
	-- The profiler's provider goes with the hello message: the app must know it before it asks for anything.
end

-- A button was pressed in the app.
function BC.HandleAction(msg)
	local tab = R.tabs[tostring(msg.tab)]
	if not tab then return end
	local widgetFn = tab.actions[tostring(msg.widget)]
	local fn = widgetFn or tab.onAction
	if not fn then return end
	-- The values of the button's dialog, if it has fields.
	local values = istable(msg.values) and msg.values or nil
	local ok, err = pcall(fn, tostring(msg.widget), tostring(msg.id), values)
	if not ok then ErrorNoHalt("[BetterConsole] action handler of tab '" .. tab.id .. "' failed: " .. tostring(err) .. "\n") end
end

-- ---------------------------------------------------------------------------------------- forms

--[[
tab:Form(id, { title, span, fields = { ... }, onChange = function(widgetId, fieldId, value) end })

A field is bound to a console variable of the server (convar = "name": its value, default, help text and range
come from the server) or holds a value of your own (id and value; onChange or tab:OnChange hear about changes,
form:Set changes it). The app shows a row per field: its text on the left, a switch, a number, a text box or a
list on the right. A value changed in the app is checked here again; a variable gets "name value" run as a
console command of the server.
]]
local FIELD_TYPES = { bool = true, number = true, text = true, choice = true, header = true, note = true }

-- A number that is neither NaN nor infinite (both give NaN for v - v).
local function Finite(v)
	v = tonumber(v)
	if v and v - v == 0 then return v end
	return nil
end

local function Str(v)
	if v == nil then return nil end
	return tostring(v)
end

-- { "a", "b" } or { { "Shown text", value }, ... } (also { text = ..., value = ... }): pairs of strings.
local function ChoiceList(list)
	if not istable(list) then return nil end
	local out = {}
	for _, c in ipairs(list) do
		local text, value = c, c
		if istable(c) then
			text, value = c[1], c[2]
			if text == nil then text = c.text end
			if value == nil then value = c.value end
			if value == nil then value = text end
			if text == nil then text = value end
		end
		if value ~= nil then out[#out + 1] = { tostring(text), tostring(value) } end
	end
	return out
end

local function FieldType(f)
	if isstring(f.type) and FIELD_TYPES[f.type] then return f.type end
	if istable(f.choices) then return "choice" end
	if isbool(f.value) or isbool(f.default) then return "bool" end
	if isnumber(f.value) or isnumber(f.default) or f.min ~= nil or f.max ~= nil or f.step ~= nil then return "number" end
	return "text"
end

-- Whole numbers without ".0" ("%d" would cut them to 32 bits on LuaJIT 2.0, the main branch: "%.0f" is exact).
local function NumberText(n)
	if n == math.floor(n) and math.abs(n) < 1e15 then return string.format("%.0f", n) end
	return string.format("%.10g", n)
end

-- What a field of the addon's own can hold: a finite number, a string, a boolean (JSON cannot carry NaN).
local function Scalar(v)
	if type(v) == "number" then return Finite(v) end
	if isstring(v) or isbool(v) then return v end
	return nil
end

local function ValueText(v)
	if v == nil then return nil end
	if isbool(v) then return v and "1" or "0" end
	if type(v) == "number" then return NumberText(v) end
	return tostring(v)
end

-- A variable that is there but GMod does not let Lua have (sv_password, rcon_password): shown as protected.
local LOCKED = {}

-- A console variable, not a command (GetConVar warns about each command it is asked about).
local function FindConVar(name)
	if not ConVarExists(name) then return nil end
	local cmds = concommand and concommand.GetTable and concommand.GetTable()
	if cmds and cmds[name] then return nil end
	return GetConVar(name) or LOCKED
end

local function Protected(cv)
	return cv == LOCKED or FCVAR_PROTECTED ~= nil and bit.band(cv:GetFlags(), FCVAR_PROTECTED) ~= 0
end

local function IsInput(f) return f.type ~= "header" and f.type ~= "note" end

-- Remembers the fields as the addon wrote them; what the app gets is made from them by BC.FormOpts.
function BC.DefineForm(w, opts)
	w.values = w.values or {}
	local fields, byId = {}, {}
	for i, f in ipairs(istable(opts.fields) and opts.fields or {}) do
		if istable(f) then
			local t = FieldType(f)
			local convar = t ~= "header" and t ~= "note" and isstring(f.convar) and f.convar ~= "" and f.convar or nil
			local id = f.id ~= nil and tostring(f.id) or convar or ("_" .. i)
			if byId[id] then
				ErrorNoHalt("[BetterConsole] form '" .. w.id .. "' of tab '" .. w.tab.id .. "': a second field '" .. id .. "' is left out\n")
			else
				local def = {
					id = id, type = t, convar = convar,
					text = Str(f.text), help = Str(f.help), note = Str(f.note), unit = Str(f.unit),
					min = Finite(f.min), max = Finite(f.max), step = Finite(f.step),
					choices = ChoiceList(f.choices), editable = f.editable == true or nil,
					readonly = f.readonly == true or nil, confirm = Str(f.confirm),
					default = f.default, missing = f.missing == "show" and "show" or "hide",
				}
				fields[#fields + 1] = def
				byId[id] = def
				-- The value it starts with (later ones come from form:Set and from the app).
				if not convar and IsInput(def) and w.values[id] == nil then w.values[id] = Scalar(f.value) end
			end
		end
	end
	w.fields, w.fieldsById = fields, byId
	w.onChange = isfunction(opts.onChange) and opts.onChange or nil
	w.sent, w.sig = {}, nil
end

-- The form's options for the app: its fields as they are on this server now (texts, defaults and ranges of the
-- variables; variables that are not there left out or marked).
function BC.FormOpts(w)
	local out, sig = {}, {}
	for _, f in ipairs(w.fields or {}) do
		local o = {
			id = f.id, type = f.type, text = f.text, help = f.help, note = f.note, unit = f.unit,
			min = f.min, max = f.max, step = f.step, choices = f.choices, editable = f.editable,
			readonly = f.readonly, confirm = f.confirm,
		}
		local keep = true
		if f.convar then
			o.convar = f.convar
			o.text = o.text or f.convar
			local cv = FindConVar(f.convar)
			f.cv = cv
			if not cv then
				keep = f.missing == "show"
				o.missing = true
				sig[#sig + 1] = "0"
			else
				local prot = Protected(cv)
				sig[#sig + 1] = prot and "p" or "1"
				if o.help == nil and cv ~= LOCKED then
					local h = cv:GetHelpText()
					if isstring(h) then
						h = string.Trim(h)
						if h ~= "" then o.help = #h > 600 and h:sub(1, 600) or h end
					end
				end
				if prot then
					o.protected = true
				else
					o.default = f.default ~= nil and ValueText(f.default) or cv:GetDefault()
				end
				if f.type == "number" and cv ~= LOCKED then
					if o.min == nil then o.min = Finite(cv:GetMin()) end
					if o.max == nil then o.max = Finite(cv:GetMax()) end
				end
			end
		else
			if o.text == nil and IsInput(f) then o.text = f.id end
			o.default = ValueText(f.default)
		end
		f.resolved = o
		if keep then out[#out + 1] = o end
	end
	w.sig = table.concat(sig)
	local opts = {}
	for k, v in pairs(w.opts or {}) do
		if k ~= "fields" then opts[k] = v end
	end
	opts.fields = out
	return opts
end

local function CurrentValues(w)
	local vals = {}
	for _, f in ipairs(w.fields or {}) do
		if f.convar then
			local cv = f.cv
			if cv and not Protected(cv) then vals[f.id] = cv:GetString() end
		elseif IsInput(f) then
			local v = w.values[f.id]
			if v ~= nil then vals[f.id] = v end
		end
	end
	return vals
end

--- Sends the values that changed since the last time (all of them with all = true).
function BC.SendFormValues(w, all)
	local vals = CurrentValues(w)
	local changed, any = {}, false
	for id, v in pairs(vals) do
		if all or w.sent[id] ~= v then
			changed[id] = v
			any = true
		end
	end
	w.sent = vals
	if any then SendWidgetData(w, "set", { values = changed }) end
end

-- One field's value, also when it did not change: the app puts its control back to it.
local function SendField(w, f)
	local v = CurrentValues(w)[f.id]
	if v == nil then return end
	w.sent[f.id] = v
	SendWidgetData(w, "set", { values = { [f.id] = v } })
end

--- form:Set({ fieldId = value }): values of fields without a convar.
function BC.SetFormValues(w, data)
	if not istable(data) then return end
	for id, v in pairs(data) do
		local f = w.fieldsById[tostring(id)]
		if f and not f.convar and IsInput(f) then w.values[f.id] = Scalar(v) end
	end
	BC.SendFormValues(w, false)
end

-- Once a second while its tab is shown: values changed from the console, rcon or other addons. A variable that
-- appeared (another addon created it later) or went makes the form's fields be sent again.
local function PollForm(w)
	local sig = {}
	for _, f in ipairs(w.fields) do
		if f.convar then
			local cv = f.cv or FindConVar(f.convar)
			sig[#sig + 1] = not cv and "0" or Protected(cv) and "p" or "1"
		end
	end
	if table.concat(sig) ~= w.sig then
		BC.Emit({ t = "w", tab = w.tab.id, id = w.id, kind = "form", opts = BC.FormOpts(w) })
		BC.SendFormValues(w, true)
	else
		BC.SendFormValues(w, false)
	end
end

local function FormsOf(tab, fn)
	for _, wid in ipairs(tab.worder) do
		local w = tab.widgets[wid]
		if w and w.kind == "form" then fn(w) end
	end
end

local function PollForms()
	if not BC.Connected then return end
	for id in pairs(R.shown) do
		local tab = R.tabs[id]
		if tab then FormsOf(tab, PollForm) end
	end
end

local function Coerce(f, v)
	local o = f.resolved or f
	if f.type == "bool" then
		if isbool(v) then return true, v end
		local t = string.lower(tostring(v))
		return true, not (t == "0" or t == "false" or t == "" or t == "off" or t == "no")
	elseif f.type == "number" then
		local n = Finite(v)
		if not n then return false, "a number is needed" end
		if (o.min and n < o.min) or (o.max and n > o.max) then
			if o.min and o.max then return false, "between " .. NumberText(o.min) .. " and " .. NumberText(o.max) end
			if o.min then return false, "at least " .. NumberText(o.min) end
			return false, "at most " .. NumberText(o.max)
		end
		return true, n
	end
	local t = v == nil and "" or tostring(v)
	if f.type == "choice" and not f.editable and f.choices then
		for _, c in ipairs(f.choices) do
			if c[2] == t then return true, t end
		end
		return false, "not one of its choices"
	end
	return true, t
end

-- Runs a console command of the server; true when it ran at once (BC.RunCommand of sv_core.lua uses the native
-- module and queues the command while the engine is executing commands already).
BC.RunCommand = BC.RunCommand or function(cmd)
	game.ConsoleCommand(cmd .. "\n")
	return false
end

-- A field was changed in the app: { tab, widget, field, value }.
function BC.HandleForm(msg)
	local tab = R.tabs[tostring(msg.tab)]
	local w = tab and tab.widgets[tostring(msg.widget)]
	if not w or w.kind ~= "form" then return end
	local f = w.fieldsById[tostring(msg.field)]
	if not f or not IsInput(f) then return end
	local name = f.resolved and f.resolved.text or f.id
	if f.readonly then return SendField(w, f) end
	local ok, value = Coerce(f, msg.value)
	if not ok then
		BC.Notify(name .. ": " .. value, "warning")   -- value is what is wrong with it
		return SendField(w, f)
	end
	if f.convar then
		local cv = f.cv or FindConVar(f.convar)
		if not cv then return BC.Notify(f.convar .. " is not on this server.", "warning") end
		if Protected(cv) then return BC.Notify(f.convar .. " is protected: it cannot be changed from here.", "warning") end
		local text = ValueText(value)
		if text:find("[\"\r\n]") then
			BC.Notify(name .. ": quotes and line breaks cannot be used in a console variable.", "warning")
			return SendField(w, f)
		end
		local cmd = f.convar .. " " .. ((text == "" or text:find("[%s;]")) and ('"' .. text .. '"') or text)
		local now = BC.RunCommand(cmd)
		-- The app shows it in the console and its history like a command typed there.
		BC.Emit({ t = "form_cmd", cmd = cmd })
		if now then return SendField(w, f) end
		-- Queued (the server hibernates): what it will be.
		w.sent[f.id] = text
		SendWidgetData(w, "set", { values = { [f.id] = text } })
		return
	end
	w.values[f.id] = value
	local fn = w.onChange or tab.onChange
	if fn then
		local okc, err = pcall(fn, w.id, f.id, value)
		if not okc then ErrorNoHalt("[BetterConsole] onChange of form '" .. w.id .. "' (tab '" .. tab.id .. "') failed: " .. tostring(err) .. "\n") end
	end
	SendField(w, f)
end

-- ---------------------------------------------------------------------------------------- which tab is seen

function BC.CallShow(tab, on)
	local ok, err = pcall(tab.onShow, on)
	if not ok then ErrorNoHalt("[BetterConsole] OnShow of tab '" .. tab.id .. "' failed: " .. tostring(err) .. "\n") end
end

local formTimer = false

-- The app shows a tab (on) or left it: { tab = id, on = bool }. Statistics is "@stats".
function BC.HandleTabShow(msg)
	local id = tostring(msg.tab)
	local on = msg.on == true
	if (R.shown[id] == true) == on then return end
	R.shown[id] = on or nil
	local tab = R.tabs[id]
	if not tab then return end
	if on then
		FormsOf(tab, PollForm)
		if not formTimer then
			formTimer = true
			timer.Create("BetterConsole.Forms", 1, 0, PollForms)
		end
	end
	if isfunction(tab.onShow) then BC.CallShow(tab, on) end
end

--- The app went away (or connects anew): no tab is seen.
function BC.HideAllTabs()
	for id in pairs(R.shown) do
		R.shown[id] = nil
		local tab = R.tabs[id]
		if tab and isfunction(tab.onShow) then BC.CallShow(tab, false) end
	end
end

-- ---------------------------------------------------------------------------------------- profiler

--[[
BetterConsole.SetProfiler(provider) hands the profiler card of the Statistics tab and the detailed capture of
lag spikes to an addon of yours (a native module that samples the game thread, say). While it is set, the
built-in profiler and capture install nothing: no hook, timer or net wrapper of theirs measures your wrappers, and
with vprof = false the app types no vprof command (they would restart the engine's profile every frame).
  provider = { name, info, start, stop, startCapture, stopCapture, vprof }
start / startCapture may return false, "why not": the user gets that as a notification, nothing changes.
Changing the provider (or SetProfiler(nil): the built-in one again) stops what runs; the user starts it again.
]]
R.profOn = R.profOn or false
R.capOn = R.capOn or false

--- The provider's part of the hello message (nil: the built-in profiler).
function BC.ProfilerProvider()
	local p = R.profiler
	if not p then return nil end
	return { name = p.name, info = p.info, vprof = p.vprof, capture = isfunction(p.startCapture) or nil }
end

local function SetProfState(kind, on)
	local key = kind == "prof" and "profOn" or "capOn"
	local was = R[key] == true
	R[key] = on
	BC.Emit({ t = kind .. "_state", on = on })
	if was ~= on and hook and hook.Run then
		local name = kind == "prof" and "BetterConsoleProfiler" or "BetterConsoleCapture"
		local ok, err = pcall(hook.Run, name, on)
		if not ok then ErrorNoHalt("[BetterConsole] a " .. name .. " hook failed: " .. tostring(err) .. "\n") end
	end
end

local function CallProvider(p, name)
	local fn = p[name]
	if not isfunction(fn) then return true end
	local ok, res, reason = pcall(fn)
	if not ok then
		ErrorNoHalt("[BetterConsole] " .. name .. " of the profiler '" .. p.name .. "' failed: " .. tostring(res) .. "\n")
		return false, p.name .. " could not " .. (name:find("Capture") and "start the capture" or "start") .. " (see its Lua error)."
	end
	if res == false then return false, reason end
	return true
end

--- The app (or the companion itself) starts or stops profiling (kind "prof") or the detailed capture ("capture").
function BC.ProfilerRequest(kind, on)
	on = on == true
	local key = kind == "prof" and "profOn" or "capOn"
	local p = R.profiler
	if p then
		if on ~= (R[key] == true) then
			local fname = kind == "prof" and (on and "start" or "stop") or (on and "startCapture" or "stopCapture")
			local ok, reason = CallProvider(p, fname)
			-- A stop that failed still stops: nobody looks at the numbers any more.
			if on and not ok then
				if reason ~= nil then BC.Notify(tostring(reason), "warning") end
				on = false
			end
		end
		return SetProfState(kind, on)
	end
	local P = BC.Profiler
	if P then
		if kind == "prof" then
			if on then P.Start() else P.Stop() end
			on = P.on
		else
			if on then P.StartCapture() else P.StopCapture() end
			on = P.capture
		end
	else
		on = false
	end
	SetProfState(kind, on)
end

function BC.SetProfiler(provider)
	if provider ~= nil and not istable(provider) then error("BetterConsole.SetProfiler: a table or nil expected", 2) end
	-- What runs now stops first: the built-in profiler and capture, or the provider before this one.
	if R.profOn then BC.ProfilerRequest("prof", false) end
	if R.capOn then BC.ProfilerRequest("capture", false) end
	if provider then
		R.profiler = {
			name = tostring(provider.name or "Addon profiler"),
			info = provider.info ~= nil and tostring(provider.info) or nil,
			start = provider.start, stop = provider.stop,
			startCapture = provider.startCapture, stopCapture = provider.stopCapture,
			vprof = provider.vprof ~= false,
		}
	else
		R.profiler = nil
	end
	local msg = BC.ProfilerProvider() or {}
	msg.t = "prof_provider"
	BC.Emit(msg)
end

function BC.IsProfiling() return R.profOn == true end
function BC.IsCapturing() return R.capOn == true end

local PROF_KINDS = { "hooks", "timers", "netin", "netout" }

--- While your profiler runs, about once a second: the same tables the built-in profiler sends (docs/lua-api.md).
function BC.ProfilerReport(r)
	if not R.profiler or not R.profOn or not istable(r) then return end
	local msg = { t = "prof", entTotal = Finite(r.entTotal), since = Finite(r.since) }
	for _, kind in ipairs(PROF_KINDS) do
		local list = r[kind]
		if istable(list) then
			local out = {}
			for _, row in ipairs(list) do
				if #out >= 300 then break end
				if istable(row) and row.k ~= nil then
					out[#out + 1] = {
						k = tostring(row.k), ms = Finite(row.ms), n = Finite(row.n), max = Finite(row.max),
						b = Finite(row.b), kb = Finite(row.kb), src = Str(row.src),
					}
				end
			end
			msg[kind] = out
		end
	end
	if istable(r.ents) then
		local out = {}
		for _, e in ipairs(r.ents) do
			if #out >= 200 then break end
			if istable(e) and e.k ~= nil then out[#out + 1] = { k = tostring(e.k), n = Finite(e.n) or 0 } end
		end
		msg.ents = out
	end
	if istable(r.extra) then
		local out = {}
		for i, x in ipairs(r.extra) do
			if istable(x) then
				local t = CleanOpts(x)
				t.id = tostring(x.id or i)
				if istable(t.rows) then
					for j = #t.rows, 2001, -1 do t.rows[j] = nil end
				end
				out[#out + 1] = t
			end
		end
		msg.extra = out
	end
	BC.Emit(msg)
end

-- ---------------------------------------------------------------------------------------- lag spikes

local SPIKE_KINDS = { danger = true, warning = true, accent = true, muted = true }

--- fn(spike) is called for each long frame when the summary with them goes to the app (once a second), so your
--- own numbers of that frame are ready by then. It adds to the frame's row: spike.causes (chips), spike.lua (after
--- the frame's Lua) and spike.engine (the engine's part, in place of vprof's report). fn = nil removes it.
function StatsTab:OnSpike(id, fn)
	id = tostring(id)
	if fn ~= nil and not isfunction(fn) then error("BetterConsole.Stats:OnSpike: a function or nil expected", 2) end
	if fn then
		if not R.spikeFns[id] then R.spikeOrder[#R.spikeOrder + 1] = id end
		R.spikeFns[id] = fn
	elseif R.spikeFns[id] then
		R.spikeFns[id] = nil
		for i, v in ipairs(R.spikeOrder) do
			if v == id then table.remove(R.spikeOrder, i) break end
		end
	end
	return self
end

local function SpikeParts(into, list, chips)
	if not istable(list) then return end
	for _, x in ipairs(list) do
		if #into >= 12 then return end
		if istable(x) and x.text ~= nil then
			local e = { text = tostring(x.text), tooltip = Str(x.tooltip), src = Str(x.src) }
			if chips then
				e.kind = SPIKE_KINDS[x.kind] and x.kind or "accent"
			else
				e.ms = Finite(x.ms)
			end
			into[#into + 1] = e
		end
	end
end

-- sv_stats.lua, before the summary goes out: what the addons say about each long frame. Each function gets fresh
-- lists to fill, so several addons add to one row without replacing each other's parts.
function BC.RunSpikeFns(spikes)
	if #R.spikeOrder == 0 or not istable(spikes) then return end
	local failed = {}
	for _, rec in ipairs(spikes) do
		local causes, lua, engine = {}, {}, {}
		for _, id in ipairs(R.spikeOrder) do
			local fn = R.spikeFns[id]
			if fn and not failed[id] then
				rec.causes, rec.lua, rec.engine = {}, {}, {}
				local ok, err = pcall(fn, rec)
				if ok then
					SpikeParts(causes, rec.causes, true)
					SpikeParts(lua, rec.lua, false)
					SpikeParts(engine, rec.engine, false)
				else
					failed[id] = true
					ErrorNoHalt("[BetterConsole] OnSpike '" .. id .. "' failed: " .. tostring(err) .. "\n")
				end
			end
		end
		rec.causes = #causes > 0 and causes or nil
		rec.lua = #lua > 0 and lua or nil
		rec.engine = #engine > 0 and engine or nil
	end
end
