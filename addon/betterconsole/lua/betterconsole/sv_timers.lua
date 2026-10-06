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

local function FileOf(fn)
	local info = debug.getinfo(fn, "S")
	return info and info.what ~= "C" and info.short_src or nil
end

--[[
The function worth naming. A closure made in another file around exactly one Lua function is a
wrapper: another addon's detour of timer.Simple or hook.Add (gProfiler wraps every timer and, while
it profiles, every hook), or a callback factory. Its own location would put every timer behind that
detour on one line; the function inside is the one somebody wrote.
]]
function T.Inner(fn)
	if not debug.getupvalue then return fn end
	for _ = 1, 3 do
		local file = FileOf(fn)
		local inner
		for i = 1, 40 do
			local name, v = debug.getupvalue(fn, i)
			if name == nil then break end
			if isfunction(v) and FileOf(v) then
				if inner and inner ~= v then return fn end
				inner = v
			end
		end
		if not inner or FileOf(inner) == file then return fn end
		fn = inner
	end
	return fn
end

local function Key(name, fn)
	local key = tostring(name)
	-- Generated or obfuscated names say nothing: add where the function lives.
	if key:find("[^%w_%.%-:/ %[%]%(%)#,]") or #key > 64 then key = key:sub(1, 64) .. " @" .. T.FnName(T.Inner(fn)) end
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
