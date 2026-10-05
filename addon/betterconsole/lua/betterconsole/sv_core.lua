--[[
BetterConsole companion addon - transport.

Messages are JSON objects with a "t" (type) field, one per line, sent through gmsv_betterconsole's named
pipe. The module does the I/O on its own thread; here we only encode, decode and dispatch. Incoming
messages are polled every frame (Receive returns nothing when the inbox is empty).
]]
local BC = BetterConsole
local N = BC.Native
local TableToJSON, JSONToTable = util.TableToJSON, util.JSONToTable
local Send, Receive, IsConnected = N.Send, N.Receive, N.Connected
local SysTime = SysTime

BC.Connected = false
BC.Handlers = BC.Handlers or {}
BC.StartTime = BC.StartTime or SysTime()

function BC.Emit(msg)
	if not BC.Connected then return false end
	local json = TableToJSON(msg)
	if not json then return false end
	return Send(json)
end

--- Registers a handler for a message type coming from the app.
function BC.On(msgType, fn)
	BC.Handlers[msgType] = fn
end

local function Dispatch(json)
	local msg = JSONToTable(json)
	if not istable(msg) then return end
	local fn = BC.Handlers[msg.t]
	if not fn then return end
	local ok, err = pcall(fn, msg)
	if not ok then ErrorNoHalt("[BetterConsole] handler '" .. tostring(msg.t) .. "' failed: " .. tostring(err) .. "\n") end
end

local function Hello()
	local hostname = GetConVar("hostname")
	BC.Emit({
		t = "hello",
		v = 1,
		addon = BC.Version,
		module = N.Version(),
		branch = BRANCH or "unknown",
		gmod = VERSIONSTR or tostring(VERSION),
		map = game.GetMap(),
		gamemode = engine.ActiveGamemode(),
		hostname = hostname and hostname:GetString() or "",
		maxplayers = game.MaxPlayers(),
		tickrate = math.floor(1 / engine.TickInterval() + 0.5),
		os = system.IsWindows() and "windows" or system.IsLinux() and "linux" or "other",
		errors = true,
	})
end

local wasConnected = false
local nextCheck = 0

local function OnConnected()
	BC.Connected = true
	Hello()
	if BC.OnConnected then BC.OnConnected() end
	BC.ResendRegistry()
	hook.Run("BetterConsoleReady")
end

-- Every frame: pull what the app sent; twice a second: notice (re)connects.
local function Think()
	local now = SysTime()
	if now >= nextCheck then
		nextCheck = now + 0.5
		local c = IsConnected()
		if c ~= wasConnected then
			wasConnected = c
			if c then OnConnected() else BC.Connected = false end
		end
	end
	if not wasConnected then return end
	local list = Receive()
	if list then
		for i = 1, #list do Dispatch(list[i]) end
	end
end

BC.On("action", BC.HandleAction)
BC.On("custom", function(msg) hook.Run("BetterConsoleMessage", msg.type, msg.data) end)
BC.On("exec", function(msg)
	if isstring(msg.cmd) then N.ServerCommand(msg.cmd, true) end
end)

function BC.Start()
	-- Not a return value: a Think hook that returns something stops the hooks after it.
	hook.Add("Think", "BetterConsole.Poll", function() Think() end)
	if BC.StartStats then BC.StartStats() end
	if BC.StartCommands then BC.StartCommands() end
end
