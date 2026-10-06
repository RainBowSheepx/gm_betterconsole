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

Widgets: Text, KeyValue, Table, Log, Chart, Buttons. Options common to all: title, span (1-12 columns of
the tab's grid, default 12). Everything you send is remembered and sent again when the app reconnects
(map change, app restart), so define tabs once at load time.

Hooks:
  BetterConsoleReady()               - the app is connected (called again after every reconnect)
  BetterConsoleMessage(type, data)   - a message from a BetterConsole C# plugin (BetterConsole.SendToApp
                                       goes the other way)
]]
BetterConsole = BetterConsole or {}
local BC = BetterConsole

BC.Version = "0.2.0"
BC.Registry = BC.Registry or { tabs = {}, order = {}, status = {} }
local R = BC.Registry

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
--- Table: { {cell, cell, ...}, ... }; Buttons: a new list of buttons).
function Widget:Set(data)
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

--- A text item in the status bar of BetterConsole. color: Color or "#RRGGBB" (optional).
function BC.SetStatus(id, text, tooltip, color)
	id = tostring(id)
	local st = { t = "st", id = id, text = tostring(text or ""), tip = tooltip and tostring(tooltip) or nil, color = ColorString(color) }
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

-- Sends everything the registry holds (after a (re)connect).
function BC.ResendRegistry()
	for _, id in ipairs(R.order) do
		local tab = R.tabs[id]
		if tab then
			BC.Emit({ t = "tab", id = id, title = tab.title, order = tab.order, icon = tab.icon })
			for _, wid in ipairs(tab.worder) do
				local w = tab.widgets[wid]
				BC.Emit({ t = "w", tab = id, id = wid, kind = w.kind, opts = w.opts })
				if w.state ~= nil then SendWidgetData(w, "set", w.state) end
				if w.buffer and #w.buffer > 0 then SendWidgetData(w, "many", w.buffer) end
			end
		end
	end
	for _, st in pairs(R.status) do BC.Emit(st) end
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
