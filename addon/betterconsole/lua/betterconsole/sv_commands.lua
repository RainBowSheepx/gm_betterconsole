--[[
BetterConsole companion addon - console commands.

  * betterconsole_exec <hex>: srcds' console input drops every non-ASCII character, so BetterConsole sends
    a command like "say Привет" hex-encoded through this command, and it runs through the engine itself
    (IVEngineServer::ServerCommand - no Lua blocklist). Only the server console may use it.
  * The command catalog for auto-completion: every console command and variable the engine knows (walked
    by the native module from a probe variable created here), plus Lua's commands, with the current values
    of variables. The app asks for fresh values of the entries it shows ("cvals").
]]
local BC = BetterConsole
local N = BC.Native

concommand.Add("betterconsole_exec", function(ply, _, _, argStr)
	if IsValid(ply) then return end
	local hex = string.Trim(argStr or "")
	if hex == "" or hex:find("[^%x]") or #hex % 2 ~= 0 then return end
	local cmd = hex:gsub("%x%x", function(h) return string.char(tonumber(h, 16)) end)
	local ok, err = N.ServerCommand(cmd, false)
	if not ok then MsgC(Color(255, 120, 90), "[BetterConsole] command failed: ", tostring(err), "\n") end
end, nil, "Used by BetterConsole to run commands with non-ASCII text. Not for manual use.", FCVAR_DONTRECORD)

-- Variables created after the probe are not reachable from it: remember their names.
local later = BC.LaterConVars or {}
BC.LaterConVars = later
if not BC.ConVarDetour then
	BC.ConVarDetour = true
	local orig = CreateConVar
	CreateConVar = function(name, ...)
		if isstring(name) then later[name] = true end
		return orig(name, ...)
	end
end

local PROBE = "betterconsole_probe"
local probe

local function VarInfo(name)
	local cv = GetConVar(name)
	if not cv then return nil end
	local flags = cv:GetFlags()
	local info = { value = cv:GetString(), def = cv:GetDefault() }
	if bit.band(flags, FCVAR_PROTECTED) ~= 0 then info.value, info.def = "***", "***" end
	local mn, mx = cv:GetMin(), cv:GetMax()
	if mn then info.min = mn end
	if mx then info.max = mx end
	return info, flags, cv:GetHelpText()
end

local function Catalog()
	local entries, native, err = {}, false, nil
	local byName = {}
	if N.ConCommands then
		probe = probe or CreateConVar(PROBE, "0", FCVAR_DONTRECORD, "BetterConsole: marks the newest console variable for the command list")
		local list, e = N.ConCommands(probe, PROBE)
		if list then
			native = true
			for _, c in ipairs(list) do
				if c.name ~= PROBE and not byName[c.name] then
					local entry = { n = c.name, f = c.flags, h = c.help, c = c.cmd }
					byName[c.name] = entry
					entries[#entries + 1] = entry
				end
			end
		else
			err = e
		end
	end
	for name, data in pairs(concommand.GetTable()) do
		local entry = byName[name]
		if not entry then
			entry = { n = name, f = FCVAR_LUA_SERVER or 0 }
			byName[name] = entry
			entries[#entries + 1] = entry
		end
		entry.c = true
	end
	for name in pairs(later) do
		if not byName[name] and name ~= PROBE and ConVarExists(name) then
			local entry = { n = name, c = false }
			byName[name] = entry
			entries[#entries + 1] = entry
		end
	end
	-- Values of variables. GetConVar must not be asked about a command (the engine prints a warning for
	-- each), so only entries known to be variables are looked up.
	for _, e in ipairs(entries) do
		if e.c == false then
			local info, flags, help = VarInfo(e.n)
			if info then
				e.v, e.d, e.mn, e.mx = info.value, info.def, info.min, info.max
				e.f = e.f or flags
				if (not e.h or e.h == "") and help and help ~= "" then e.h = help end
			end
		end
		if e.h then
			e.h = string.Trim(e.h)
			if e.h == "" then e.h = nil elseif #e.h > 400 then e.h = e.h:sub(1, 400) end
		end
	end
	local maps = {}
	for _, f in ipairs(file.Find("maps/*.bsp", "GAME") or {}) do maps[#maps + 1] = f:sub(1, -5) end
	table.sort(maps)
	return entries, maps, native, err
end

local function SendCatalog()
	local entries, maps, native, err = Catalog()
	BC.Emit({ t = "cmds", list = entries, maps = maps, native = native, err = err })
end

BC.On("cmds", SendCatalog)

BC.On("cvals", function(msg)
	if not istable(msg.names) then return end
	local vals = {}
	local n = 0
	for _, name in ipairs(msg.names) do
		n = n + 1
		if n > 200 then break end
		if isstring(name) and ConVarExists(name) and not concommand.GetTable()[name] then
			local info = VarInfo(name)
			if info then vals[name] = info.value end
		end
	end
	BC.Emit({ t = "cvals", vals = vals })
end)

function BC.StartCommands()
	-- Nothing to do at start: the app asks for the catalog once it is connected.
end
