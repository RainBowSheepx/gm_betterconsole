--[[
BetterConsole companion addon - loader.

This file sits in lua/autorun/ (not autorun/server/) and starts with "!" so it runs before almost every
other addon: error capture and the timer detour are in place before other addons start up. It is not
sent to clients (no AddCSLuaFile) and does nothing on them.

Everything stays switched off unless the server was started by BetterConsole.exe: the native module
gmsv_betterconsole is loaded only when it is installed, and it only connects when BetterConsole passed
the name of its pipe in the environment. The public API (BetterConsole.AddTab, SetStatus, ...) exists in
any case and does nothing while inactive, so addons may call it unconditionally.
]]
if CLIENT then return end
if BetterConsole and BetterConsole.Loaded then return end

include("betterconsole/sv_api.lua")

local BC = BetterConsole
BC.Loaded = true

if not (util.IsBinaryModuleInstalled and util.IsBinaryModuleInstalled("betterconsole")) then
	return
end

local ok, err = pcall(require, "betterconsole")
if not ok or not betterconsole then
	MsgC(Color(255, 120, 90), "[BetterConsole] gmsv_betterconsole failed to load: ", tostring(err), "\n")
	return
end

if not betterconsole.Enabled() then
	-- Started without BetterConsole.exe: stay out of the way.
	return
end

BC.Native = betterconsole
include("betterconsole/sv_core.lua")
include("betterconsole/sv_errors.lua")
include("betterconsole/sv_timers.lua")
include("betterconsole/sv_stats.lua")
include("betterconsole/sv_players.lua")
include("betterconsole/sv_profiler.lua")
include("betterconsole/sv_commands.lua")
BC.Start()
