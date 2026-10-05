--[[
BetterConsole companion addon - timer detour.

GMod cannot list existing timers, so to know which timer costs how much the timer functions have to be
wrapped when they are created. timer.Create / timer.Simple / timer.Adjust are detoured here, as early as
possible (from lua/autorun/!betterconsole.lua). While the profiler is off the wrapper just calls the
function; while it is on, BetterConsole.Timers.measure(key, fn) times the call.

Only installed when the server runs under BetterConsole (the loader returns before this otherwise).
]]
local BC = BetterConsole
local T = BC.Timers
if T and T.installed then return end
T = T or {}
BC.Timers = T
T.installed = true

-- Where a function was written: "path/file.lua:line".
function T.FnName(fn)
	local info = debug.getinfo(fn, "S")
	if not info then return tostring(fn) end
	return tostring(info.short_src) .. ":" .. tostring(info.linedefined)
end

local function Key(name, fn)
	local key = tostring(name)
	-- Generated or obfuscated names say nothing: add where the function lives.
	if key:find("[^%w_%.%-:/ %[%]%(%)#,]") or #key > 64 then key = key:sub(1, 64) .. " @" .. T.FnName(fn) end
	return key
end

-- The timer library calls the function without arguments and ignores the result: no varargs here
-- (a vararg wrapper called from C is noticeably more expensive).
local function Wrap(name, fn, simple)
	if not isfunction(fn) then return fn end
	local key = not simple and Key(name, fn) or nil
	return function()
		local m = T.measure
		if not m then return fn() end
		key = m(key, fn)
	end
end
T.Wrap = Wrap

local create, simple, adjust = timer.Create, timer.Simple, timer.Adjust
T.origCreate, T.origSimple, T.origAdjust = create, simple, adjust

timer.Create = function(name, delay, reps, fn, ...)
	return create(name, delay, reps, Wrap(name, fn), ...)
end
timer.Simple = function(delay, fn, ...)
	return simple(delay, Wrap(nil, fn, true), ...)
end
timer.Adjust = function(name, delay, reps, fn, ...)
	if fn ~= nil then fn = Wrap(name, fn) end
	return adjust(name, delay, reps, fn, ...)
end
