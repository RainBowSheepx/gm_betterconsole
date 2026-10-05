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
local origIncoming

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
	origIncoming = net.Incoming
	net.Incoming = function(len, client)
		local t0 = SysTime()
		origIncoming(len, client)
		if client then loadAcc[client] = (loadAcc[client] or 0) + (SysTime() - t0) end
	end
end

local function StopLoad()
	if not measuring then return end
	measuring = false
	hook.Remove("StartCommand", "BetterConsole.Load")
	hook.Remove("FinishMove", "BetterConsole.Load")
	hook.Remove("Tick", "BetterConsole.LoadTicks")
	if origIncoming then net.Incoming = origIncoming end
	origIncoming = nil
end

local function Groups()
	if not HasULX() then return nil end
	local list = {}
	for name in pairs(ULib.ucl.groups or {}) do list[#list + 1] = name end
	table.sort(list)
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
	BC.Emit({ t = "players", list = list, maxplayers = game.MaxPlayers(), ulx = ulxOn, groups = Groups(), load = measuring })
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
