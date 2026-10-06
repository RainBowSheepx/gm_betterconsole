--[[
BetterConsole companion addon - server statistics.

Every frame (Think): the time since the previous frame and, with the native module, the CPU time the game
thread spent on it (QueryThreadCycleTime). Every second a summary goes to the app; every one or two seconds
the player list (sv_players.lua) with network numbers from the engine's net channels (the same numbers
"stats" uses).

A frame longer than three ticks (a lag spike) is reported with what it was made of: CPU time, physics,
the Lua collector, entity and player changes, the slowest timer, and with the detailed capture on
(sv_profiler.lua) the slowest hooks, timers and net messages of that frame.

Cost: two hooks with a few arithmetic operations and C calls each; the summary once a second.
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

-- What a long frame is made of, measured on every frame (a few C calls): the Lua heap at its start (a
-- drop: the collector ran in it), the entity count, the physics simulation, players who joined in it.
local collect, ents_GetCount = collectgarbage, ents.GetCount
local SimTime = physenv and physenv.GetLastSimulationTime
local lastHeap, lastEnts
local joined

local function Spike(p, d, heap, entCount, now)
	local rec = { ms = p * 1000, busy = d, time = os.time(), st = now }
	if SimTime then rec.phys = SimTime() * 1000 end
	rec.heap = heap / 1024
	if lastHeap and lastHeap - heap > 4 * 1024 then rec.gc = (lastHeap - heap) / 1024 end
	if lastEnts and entCount ~= lastEnts then rec.ents = entCount - lastEnts end
	if joined then rec.joined = joined end
	if now - BC.StartTime < 10 then rec.start = true end
	local T = BC.Timers
	if T and T.slowFn and T.slow > 0.0005 then
		local src = T.FnName(T.Inner(T.slowFn))
		rec.timer = { k = T.slowKey or src, ms = T.slow * 1000, src = src }
	end
	local P = BC.Profiler
	if P and P.capture then rec.cbs = P.FrameTop(5) end
	return rec
end

local function OnThink()
	local now = SysTime()
	local b = ThreadTime()
	local heap, entCount = collect("count"), ents_GetCount()
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
			local ok, rec = pcall(Spike, p, d, heap, entCount, now)
			spikes[#spikes + 1] = ok and rec or { ms = p * 1000, busy = d, time = os.time(), st = now }
		end
	end
	-- The next frame starts here.
	lastT, lastBusy, lastHeap, lastEnts, joined = now, b, heap, entCount, nil
	local T = BC.Timers
	if T then T.slow, T.slowFn, T.slowKey = 0, nil, nil end
	local P = BC.Profiler
	if P and P.capture then P.FrameReset() end
	-- Calls ended by an error nobody reported (a pcall caught it): dropped, not counted.
	if P and P.Unwound then P.Unwound(false) end
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
		st = SysTime(),  -- with it the app puts the spikes' st on its own clock
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
hook.Add("PlayerInitialSpawn", "BetterConsole", function(ply)
	nextPlayers = 0
	-- A long frame names who joined in it (spawning a player can take a while).
	joined = joined or {}
	if IsValid(ply) and #joined < 4 then joined[#joined + 1] = ply:Nick() end
end)
hook.Add("PlayerDisconnected", "BetterConsole", function() timer.Simple(0, function() nextPlayers = 0 end) end)
