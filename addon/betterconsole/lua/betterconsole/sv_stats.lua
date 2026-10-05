--[[
BetterConsole companion addon - server statistics.

Every frame (Think): the time since the previous frame and, with the native module, the CPU time the game
thread spent on it (QueryThreadCycleTime). Every second a summary goes to the app; every one or two seconds
the player list (sv_players.lua) with network numbers from the engine's net channels (the same numbers
"stats" uses).

Cost: two hooks with a few arithmetic operations each; the summary once a second.
]]
local BC = BetterConsole
local N = BC.Native
local SysTime, ThreadTime, NetStats = SysTime, N.ThreadTime, N.NetStats
local math_sqrt, math_max = math.sqrt, math.max
local player_GetAll = player.GetAll

local tickInterval = engine.TickInterval()

local frames, sum, sumSq, maxPeriod = 0, 0, 0, 0
local busySum, busyMax, busyFrames = 0, 0, 0
local ticks = 0
local lastT, lastBusy
local spikes = {}
local lastSummary = SysTime()
local nextPlayers = 0
local netOk = nil       -- nil: not checked yet, true: addresses matched, false: disabled

local function OnThink()
	local now = SysTime()
	local b = ThreadTime()
	if lastT then
		local p = now - lastT
		frames = frames + 1
		sum = sum + p
		sumSq = sumSq + p * p
		if p > maxPeriod then maxPeriod = p end
		local d
		if b and lastBusy then
			d = b - lastBusy
			busySum = busySum + d
			busyFrames = busyFrames + 1
			if d > busyMax then busyMax = d end
		end
		-- A frame that took longer than three ticks is a visible hitch for players.
		if p > tickInterval * 3 and p > 0.05 and #spikes < 20 then
			spikes[#spikes + 1] = { ms = p * 1000, busy = d, time = os.time() }
		end
	end
	lastT, lastBusy = now, b
	if now - lastSummary >= 1 then BC.Summary(now) end
end

local function OnTick()
	ticks = ticks + 1
end

-- Network numbers of one player, checked once against what Lua knows (the engine interface layout
-- could differ in an unknown future build: then the numbers are switched off instead of being wrong).
local function PlayerNet(ply)
	if netOk == false or ply:IsBot() then return nil end
	local s = NetStats(ply:EntIndex())
	if not s then return nil end
	if netOk == nil and not s.loopback then
		local ip = ply:IPAddress()
		if ip ~= "" and ip ~= "loopback" and s.address ~= ip then
			netOk = false
			MsgC(Color(255, 160, 80), "[BetterConsole] network statistics disabled: engine address '", s.address, "' does not match '", ip, "'\n")
			return nil
		end
		netOk = true
	end
	return s
end

BC.PlayerNet = PlayerNet

function BC.Summary(now)
	local wall = now - lastSummary
	lastSummary = now
	if wall <= 0 then return end

	local mean = frames > 0 and sum / frames or 0
	local var = frames > 1 and math_max(sumSq / frames - mean * mean, 0) or 0
	local all = player_GetAll()
	local humans, bots = 0, 0
	local netIn, netOut = 0, 0
	for i = 1, #all do
		local ply = all[i]
		if ply:IsBot() then
			bots = bots + 1
		else
			humans = humans + 1
			local s = PlayerNet(ply)
			if s then
				netIn = netIn + s["in"]
				netOut = netOut + s.out
			end
		end
	end

	BC.Emit({
		t = "stats",
		fps = frames / wall,
		ft = mean * 1000,
		ftmax = maxPeriod * 1000,
		ftsd = math_sqrt(var) * 1000,
		busy = busyFrames > 0 and busySum / busyFrames or nil,
		busymax = busyFrames > 0 and busyMax or nil,
		load = busyFrames > 0 and busySum / (wall * 10) or nil,  -- percent of wall time
		tps = ticks / wall,
		tickrate = 1 / tickInterval,
		players = humans,
		bots = bots,
		maxplayers = game.MaxPlayers(),
		ents = ents.GetCount(),
		edicts = ents.GetEdictCount and ents.GetEdictCount() or nil,
		lua = collectgarbage("count"),
		netin = netOk ~= false and netIn or nil,
		netout = netOk ~= false and netOut or nil,
		map = game.GetMap(),
		uptime = now - BC.StartTime,
		spikes = #spikes > 0 and spikes or nil,
	})

	frames, sum, sumSq, maxPeriod = 0, 0, 0, 0
	busySum, busyMax, busyFrames = 0, 0, 0
	ticks = 0
	if #spikes > 0 then spikes = {} end

	if now >= nextPlayers then
		nextPlayers = now + (BC.PlayersWatched and 1 or 2)
		if BC.SendPlayers then BC.SendPlayers(wall) end
	end
	if BC.ProfilerTick then BC.ProfilerTick(now, wall) end
end

function BC.StartStats()
	hook.Add("Think", "BetterConsole.Frame", function() OnThink() end)
	hook.Add("Tick", "BetterConsole.Tick", function() OnTick() end)
end

-- While the server hibernates there are no frames: betterconsole_poll calls this for the numbers that
-- still mean something.
function BC.IdleSummary()
	if SysTime() - lastSummary < 2 then return end   -- frames are running: the normal summary does it
	BC.Emit({
		t = "stats",
		idle = true,
		players = #player.GetHumans(),
		bots = #player.GetBots(),
		maxplayers = game.MaxPlayers(),
		tickrate = 1 / tickInterval,
		ents = ents.GetCount(),
		edicts = ents.GetEdictCount and ents.GetEdictCount() or nil,
		lua = collectgarbage("count"),
		map = game.GetMap(),
		uptime = SysTime() - BC.StartTime,
	})
	if BC.SendPlayers then BC.SendPlayers() end
end

-- Player joins and leaves are pushed at once instead of waiting for the next list.
function BC.PlayersChanged() nextPlayers = 0 end
hook.Add("PlayerInitialSpawn", "BetterConsole", function() nextPlayers = 0 end)
hook.Add("PlayerDisconnected", "BetterConsole", function() timer.Simple(0, function() nextPlayers = 0 end) end)
