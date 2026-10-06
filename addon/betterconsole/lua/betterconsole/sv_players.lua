--[[
BetterConsole companion addon - the Players tab.

The player list goes to the app every two seconds (every second while the Players tab is open), with the
user group and, when ULX is installed, its groups and the gag / mute / jail state of each player.

"load" is the CPU time of the game thread spent on one player, in milliseconds per tick: from its
StartCommand to its FinishMove (processing its usercmds: movement and every hook in between) plus the
Lua handling of its net messages (net.Incoming). It is only measured while the Players tab is open:
the hooks are added when the app subscribes and removed when it unsubscribes.

Admin actions without ULX: kick / ban go through the engine (kickid, banid); a group is set with
SetUserGroup (not saved). Gag, mute and jail need ULX.
]]
local BC = BetterConsole
local SysTime, IsValid = SysTime, IsValid

local loadAcc = setmetatable({}, { __mode = "k" })   -- [ply] = seconds since the last list
local cmdStart = setmetatable({}, { __mode = "k" })
local ticks = 0
local measuring = false
local origIncoming, ourIncoming

local function HasULX()
	return istable(ulx) and istable(ULib) and istable(ULib.ucl) and isfunction(ulx.command)
end

local function StartLoad()
	if measuring then return end
	measuring = true
	local hiPrio = HOOK_MONITOR_HIGH
	hook.Add("StartCommand", "BetterConsole.Load", function(ply)
		cmdStart[ply] = SysTime()
	end, hiPrio)
	hook.Add("FinishMove", "BetterConsole.Load", function(ply)
		local t = cmdStart[ply]
		if t then
			loadAcc[ply] = (loadAcc[ply] or 0) + (SysTime() - t)
			cmdStart[ply] = nil
		end
	end, HOOK_MONITOR_LOW)
	hook.Add("Tick", "BetterConsole.LoadTicks", function() ticks = ticks + 1 end)
	-- Still in the chain from an earlier subscription (another addon detoured net.Incoming after us).
	if ourIncoming then return end
	local orig = net.Incoming
	origIncoming = orig
	ourIncoming = function(len, client)
		if not measuring then return orig(len, client) end
		local t0 = SysTime()
		orig(len, client)
		if client then loadAcc[client] = (loadAcc[client] or 0) + (SysTime() - t0) end
	end
	net.Incoming = ourIncoming
end

local function StopLoad()
	if not measuring then return end
	measuring = false
	hook.Remove("StartCommand", "BetterConsole.Load")
	hook.Remove("FinishMove", "BetterConsole.Load")
	hook.Remove("Tick", "BetterConsole.LoadTicks")
	-- Only when nobody detoured it after us: then ours stays and passes the calls on.
	if ourIncoming and net.Incoming == ourIncoming then
		net.Incoming = origIncoming
		ourIncoming, origIncoming = nil, nil
	end
end

-- How high each group is: how many groups it inherits from (user 0, admin 1, superadmin 2, a "vip" that
-- inherits user 1 ...), from ULX or else from CAMI (other admin mods register their groups there).
local function Ranks()
	local parent, any = {}, false
	if HasULX() then
		for name, g in pairs(ULib.ucl.groups or {}) do
			parent[name] = istable(g) and g.inherit_from or false
			any = true
		end
	elseif CAMI and isfunction(CAMI.GetUsergroups) then
		for name, g in pairs(CAMI.GetUsergroups() or {}) do
			parent[name] = istable(g) and g.Inherits or false
			any = true
		end
	end
	if not any then return nil end
	local ranks = {}
	for name in pairs(parent) do
		local depth, cur, seen = 0, parent[name], { [name] = true }
		while isstring(cur) and cur ~= "" and not seen[cur] and depth < 32 do
			seen[cur] = true
			depth = depth + 1
			cur = parent[cur]
		end
		ranks[name] = depth
	end
	return ranks
end

-- The groups, highest first (the order of the "Set group" menu).
local function Groups(ranks)
	if not HasULX() then return nil end
	local list = {}
	for name in pairs(ULib.ucl.groups or {}) do list[#list + 1] = name end
	table.sort(list, function(a, b)
		local ra, rb = ranks and ranks[a] or 0, ranks and ranks[b] or 0
		if ra ~= rb then return ra > rb end
		return a < b
	end)
	return list
end

function BC.SendPlayers()
	local list = {}
	local ulxOn = HasULX()
	local perTick = measuring and ticks > 0 and (1000 / ticks) or nil
	for _, ply in ipairs(player.GetAll()) do
		local s = BC.PlayerNet and BC.PlayerNet(ply)
		local p = {
			uid = ply:UserID(),
			name = ply:Nick(),
			sid = ply:SteamID(),
			sid64 = ply:SteamID64() or "",
			bot = ply:IsBot(),
			ip = ply:IsBot() and "" or ply:IPAddress(),
			ping = s and s.ping or ply:Ping(),
			loss = s and s.loss or ply:PacketLoss(),
			choke = s and s.choke or nil,
			["in"] = s and s["in"] or nil,
			out = s and s.out or nil,
			fps = s and s.fps or nil,
			time = ply:TimeConnected(),
			group = ply:GetUserGroup(),
			team = team.GetName(ply:Team()),
			frags = ply:Frags(),
			deaths = ply:Deaths(),
		}
		if perTick then p.load = (loadAcc[ply] or 0) * perTick end
		if not p.bot then
			-- What the client asks for; the server keeps it within sv_min/maxupdaterate and sv_min/maxcmdrate.
			p.upd = ply:GetInfoNum("cl_updaterate", 0)
			p.cmdr = ply:GetInfoNum("cl_cmdrate", 0)
		end
		-- Items of the player menu whose filter accepts this player.
		p.act = BC.PlayerActionFlags(ply)
		if ulxOn then
			p.ulxid = ULib.getUniqueIDForPlayer and ULib.getUniqueIDForPlayer(ply) or nil
			p.gagged = ply.ulx_gagged or ply:GetNWBool("ulx_gagged", false) or nil
			p.muted = ply:GetNWBool("ulx_muted", false) or nil
			p.jailed = ply.jail ~= nil or nil
		end
		list[#list + 1] = p
	end
	if measuring then
		for k in pairs(loadAcc) do loadAcc[k] = nil end
		ticks = 0
	end
	local ranks = Ranks()
	BC.Emit({ t = "players", list = list, maxplayers = game.MaxPlayers(), ulx = ulxOn, groups = Groups(ranks), ranks = ranks, load = measuring })
end

BC.On("sub", function(msg)
	if msg.players ~= nil then
		BC.PlayersWatched = msg.players == true
		if BC.PlayersWatched then StartLoad() else StopLoad() end
		if BC.PlayersChanged then BC.PlayersChanged() end
	end
end)

-- Fallbacks when ULX is not installed.
BC.On("setgroup", function(msg)
	for _, ply in ipairs(player.GetAll()) do
		if ply:SteamID() == msg.sid or ply:UserID() == tonumber(msg.uid) then
			ply:SetUserGroup(tostring(msg.group or "user"))
			if BC.PlayersChanged then BC.PlayersChanged() end
			return
		end
	end
end)

timer.Create("BetterConsole.PlayersWatch", 2, 0, function()
	if measuring and not BC.Connected then
		BC.PlayersWatched = false
		StopLoad()
	end
end)
