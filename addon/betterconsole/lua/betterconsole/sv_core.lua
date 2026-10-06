--[[
BetterConsole companion addon - transport.

Messages are JSON objects with a "t" (type) field, one per line, sent through gmsv_betterconsole's named
pipe. The module does the I/O on its own thread; here we only encode, decode and dispatch. Incoming
messages are polled every frame (Receive returns nothing when the inbox is empty).

An empty server hibernates (sv_hibernate_think 0): no Think, no timers. The console input is still read
then, so BetterConsole types the hidden command betterconsole_poll to get its requests answered and a
short summary of the sleeping server.
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
		-- An addon's profiler (BetterConsole.SetProfiler): before the app asks for the capture.
		profiler = BC.ProfilerProvider and BC.ProfilerProvider() or nil,
	})
end

local wasConnected = false
local nextCheck = 0

local function OnConnected()
	BC.Connected = true
	-- The app says again which tab it shows.
	if BC.HideAllTabs then BC.HideAllTabs() end
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
			if c then
				OnConnected()
			else
				BC.Connected = false
				if BC.HideAllTabs then BC.HideAllTabs() end
				-- Nobody looks at the numbers any more; a reconnect starts with both off (the app asks again).
				if BC.IsProfiling and BC.IsProfiling() then BC.ProfilerRequest("prof", false) end
				if BC.IsCapturing and BC.IsCapturing() then BC.ProfilerRequest("capture", false) end
			end
		end
	end
	if not wasConnected then return end
	local list = Receive()
	if list then
		for i = 1, #list do Dispatch(list[i]) end
	end
end

BC.On("action", BC.HandleAction)
BC.On("paction", BC.HandlePlayerAction)
BC.On("form", BC.HandleForm)
BC.On("tabshow", BC.HandleTabShow)
-- The profiler and the detailed capture: the built-in ones or an addon's (BetterConsole.SetProfiler).
BC.On("prof", function(msg) BC.ProfilerRequest("prof", msg.on == true) end)
BC.On("capture", function(msg) BC.ProfilerRequest("capture", msg.on == true) end)
BC.On("custom", function(msg) hook.Run("BetterConsoleMessage", msg.type, msg.data) end)
local polling = false
BC.On("exec", function(msg)
	-- Inside betterconsole_poll the engine is already executing commands: only queue it then.
	if isstring(msg.cmd) then N.ServerCommand(msg.cmd, not polling) end
end)

-- A command of BetterConsole's own (a form's variable): true when it ran at once.
function BC.RunCommand(cmd)
	local ok = N.ServerCommand(cmd, not polling)
	if not ok then
		game.ConsoleCommand(cmd .. "\n")
		return false
	end
	return not polling
end

concommand.Add("betterconsole_poll", function(ply)
	if IsValid(ply) then return end
	nextCheck = 0
	polling = true
	local ok, err = pcall(Think)
	polling = false
	if not ok then ErrorNoHalt("[BetterConsole] poll failed: " .. tostring(err) .. "\n") end
	if BC.IdleSummary then BC.IdleSummary() end
end, nil, "Used by BetterConsole while the server hibernates. Not for manual use.", FCVAR_DONTRECORD)

function BC.Start()
	-- Not a return value: a Think hook that returns something stops the hooks after it.
	hook.Add("Think", "BetterConsole.Poll", function() Think() end)
	if BC.StartStats then BC.StartStats() end
	if BC.StartCommands then BC.StartCommands() end
end
