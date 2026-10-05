--[[
BetterConsole companion addon - Lua profiler (on demand).

Switched on from the Statistics tab ("Profile Lua"). While on:
  * every hook function is wrapped and timed (GMod's, ULib's, DLib's and Srlion's hook libraries);
  * timers are timed (they are wrapped since start-up by sv_timers.lua);
  * net.Receive handlers are timed and incoming messages counted with their size;
  * net.Start / net.Send* are detoured to count outgoing messages and bytes per name;
  * entities are counted by class every 5 seconds.
Once a second the top entries go to the app: averages per second since profiling started, so the
ranking is steady (a timer that runs every 2 s does not blink in and out). Off = every wrapper and
detour is removed again.

Wrappers cost about 1-3 microseconds per call, so a busy server pays a few percent while profiling.
]]
local BC = BetterConsole
local SysTime, pairs, ipairs, tostring, isfunction, istable = SysTime, pairs, ipairs, tostring, isfunction, istable
local T = BC.Timers

local P = { on = false }
BC.Profiler = P

local acc = {}        -- [kind] = { [key] = { ms, calls, max, bytes, src } }
local function Bucket(kind) local b = acc[kind] if not b then b = {} acc[kind] = b end return b end
local function Add(kind, key, dt, bytes, src)
	local b = Bucket(kind)
	local e = b[key]
	if not e then
		e = { ms = 0, calls = 0, max = 0, bytes = 0, src = src }
		b[key] = e
	end
	e.ms = e.ms + dt
	e.calls = e.calls + 1
	if dt > e.max then e.max = dt end
	if bytes then e.bytes = e.bytes + bytes end
end

local function Src(fn)
	local info = isfunction(fn) and debug.getinfo(fn, "S")
	if not info then return nil end
	if info.what == "C" then return "[C]" end
	return tostring(info.short_src) .. ":" .. tostring(info.linedefined)
end

---------------------------------------------------------------------------
-- hooks
---------------------------------------------------------------------------
local function HookLib()
	if hook.Author == "Srlion" or (istable(PRE_HOOK) and istable(POST_HOOK) and isfunction(hook.Debug)) then return "srlion" end
	if hook.GetDLibTable and hook.Reconstruct then return "dlib" end
	if hook.GetULibTable then return "ulib" end
	return "gmod"
end

local wrapped = {}   -- ULib / DLib: [data] = {orig, w, event, dlib}; GMod / Srlion: ["event\0name"] = {event, name, orig, w, prio, srlion}
local ours = setmetatable({}, { __mode = "k" })

-- Fixed number of results instead of "...": hook.Call passes on at most six.
local function Wrapper(key, orig, src)
	local w = function(...)
		local t0 = SysTime()
		local a, b, c, d, e, f = orig(...)
		Add("hooks", key, SysTime() - t0, nil, src)
		return a, b, c, d, e, f
	end
	ours[w] = true
	return w
end

local function Skip(event, name)
	return isstring(name) and name:find("^BetterConsole") ~= nil
end

local function SrlionEvents()
	if not debug.getupvalue then return nil end
	for _, fn in ipairs({ hook.GetTable, hook.Remove, hook.Call }) do
		if isfunction(fn) then
			for i = 1, 60 do
				local name, v = debug.getupvalue(fn, i)
				if name == nil then break end
				if name == "events" and istable(v) then return v end
			end
		end
	end
end

local function KeyOf(event, name)
	return tostring(event) .. " / " .. tostring(name)
end

local function WrapHooks()
	local lib = HookLib()
	if lib == "srlion" then
		local events = SrlionEvents()
		if not events then return end
		local todo = {}
		for event, ev in pairs(events) do
			for name, ht in pairs(ev) do
				if name ~= 0 and istable(ht) and isfunction(ht.real_func) and not ours[ht.real_func] and not Skip(event, name) then
					todo[#todo + 1] = { event, name, ht.real_func, ht.priority }
				end
			end
		end
		for _, x in ipairs(todo) do
			local w = Wrapper(KeyOf(x[1], x[2]), x[3], Src(x[3]))
			wrapped[x[1] .. "\0" .. tostring(x[2])] = { x[1], x[2], x[3], w, x[4], srlion = true }
			hook.Add(x[1], x[2], w, x[4])
		end
		return
	end
	local dlib = lib == "dlib" and hook.GetDLibTable()
	if dlib then
		for event, prios in pairs(dlib) do
			local changed = false
			for _, list in pairs(prios) do
				for name, data in pairs(list) do
					if istable(data) and isfunction(data.callback) and not wrapped[data] and not Skip(event, name) then
						local orig = data.callback
						local w = Wrapper(KeyOf(event, name), orig, Src(orig))
						data.callback = w
						if data.fn == orig then data.fn = w end
						wrapped[data] = { orig, w, event, dlib = true }
						changed = true
					end
				end
			end
			if changed then hook.Reconstruct(event) end
		end
		return
	end
	local ulib = lib == "ulib" and hook.GetULibTable()
	if ulib then
		for event, prios in pairs(ulib) do
			for _, list in pairs(prios) do
				for name, data in pairs(list) do
					if istable(data) and isfunction(data.fn) and not wrapped[data] and not Skip(event, name) then
						local orig = data.fn
						local w = Wrapper(KeyOf(event, name), orig, Src(orig))
						data.fn = w
						wrapped[data] = { orig, w }
					end
				end
			end
		end
		return
	end
	local todo = {}
	for event, list in pairs(hook.GetTable()) do
		for name, fn in pairs(list) do
			if isfunction(fn) and not ours[fn] and not Skip(event, name) then
				todo[#todo + 1] = { event, name, fn }
			end
		end
	end
	for _, x in ipairs(todo) do
		local w = Wrapper(KeyOf(x[1], x[2]), x[3], Src(x[3]))
		wrapped[tostring(x[1]) .. "\0" .. tostring(x[2])] = { x[1], x[2], x[3], w }
		hook.Add(x[1], x[2], w)
	end
end

local function UnwrapHooks()
	local rebuild = {}
	local events = HookLib() == "srlion" and SrlionEvents() or nil
	for key, rec in pairs(wrapped) do
		if rec.srlion then
			local ht = events and events[rec[1]] and events[rec[1]][rec[2]]
			if istable(ht) and ht.real_func == rec[4] then hook.Add(rec[1], rec[2], rec[3], rec[5]) end
		elseif isstring(key) then
			local list = hook.GetTable()[rec[1]]
			if list and list[rec[2]] == rec[4] then hook.Add(rec[1], rec[2], rec[3]) end
		elseif rec.dlib then
			if key.callback == rec[2] then key.callback = rec[1] end
			if key.fn == rec[2] then key.fn = rec[1] end
			rebuild[rec[3]] = true
		elseif key.fn == rec[2] then
			key.fn = rec[1]
		end
	end
	wrapped = {}
	if hook.Reconstruct then
		for event in pairs(rebuild) do hook.Reconstruct(event) end
	end
end

---------------------------------------------------------------------------
-- net messages
---------------------------------------------------------------------------
local netWrapped = {}   -- [lowercase name] = { orig, w }
local origReceive
local origStart, origSend, origBroadcast, origOmit, origPAS, origPVS
local sending

local function WrapReceiver(name, fn)
	if not isfunction(fn) or ours[fn] then return fn end
	local key = tostring(name)
	local src = Src(fn)
	local w = function(len, ply)
		local t0 = SysTime()
		fn(len, ply)
		Add("netin", key, SysTime() - t0, (len or 0) / 8, src)
	end
	ours[w] = true
	netWrapped[name] = { fn, w }
	return w
end

-- How many players a net.Send / SendOmit target stands for: a player, a list of players or a
-- CRecipientFilter. Player has a GetCount too (Sandbox's limit counter), so check the type first.
local function Recipients(target)
	if istable(target) then return #target end
	if type(target) == "CRecipientFilter" then return target:GetCount() end
	return IsValid(target) and 1 or 0
end

-- Accounting must never get in the way of the real send.
local function CountSend(fn, ...)
	if not sending then return end
	local name = sending
	sending = nil
	local ok, recipients = pcall(fn, ...)
	if not ok then return end
	local bytes = net.BytesWritten and net.BytesWritten() or 0
	Add("netout", name, 0, bytes * math.max(recipients, 1))
end

local function Omitted(target) return player.GetCount() - Recipients(target) end
local function Everyone() return player.GetCount() end
local function One() return 1 end

local function InstallNet()
	for name, fn in pairs(net.Receivers) do
		net.Receivers[name] = WrapReceiver(name, fn)
	end
	origReceive = net.Receive
	net.Receive = function(name, fn)
		origReceive(name, fn)
		local lname = string.lower(tostring(name))
		net.Receivers[lname] = WrapReceiver(lname, net.Receivers[lname])
	end
	origStart, origSend, origBroadcast, origOmit, origPAS, origPVS = net.Start, net.Send, net.Broadcast, net.SendOmit, net.SendPAS, net.SendPVS
	net.Start = function(name, unreliable)
		sending = tostring(name)
		return origStart(name, unreliable)
	end
	net.Send = function(ply)
		CountSend(Recipients, ply)
		return origSend(ply)
	end
	net.Broadcast = function()
		CountSend(Everyone)
		return origBroadcast()
	end
	net.SendOmit = function(ply)
		CountSend(Omitted, ply)
		return origOmit(ply)
	end
	net.SendPAS = function(pos) CountSend(One) return origPAS(pos) end
	net.SendPVS = function(pos) CountSend(One) return origPVS(pos) end
end

local function RemoveNet()
	for name, rec in pairs(netWrapped) do
		if net.Receivers[name] == rec[2] then net.Receivers[name] = rec[1] end
	end
	netWrapped = {}
	if origReceive then net.Receive = origReceive end
	if origStart then
		net.Start, net.Send, net.Broadcast, net.SendOmit, net.SendPAS, net.SendPVS = origStart, origSend, origBroadcast, origOmit, origPAS, origPVS
	end
	origReceive, origStart = nil, nil
end

---------------------------------------------------------------------------
-- timers (wrapped by sv_timers.lua since start-up)
---------------------------------------------------------------------------
local function MeasureTimer(key, fn)
	key = key or T.FnName(fn)
	-- BetterConsole's own timers are not what anybody profiles.
	if key:find("^BetterConsole%.") then
		fn()
		return key
	end
	local t0 = SysTime()
	fn()
	Add("timers", key, SysTime() - t0)
	return key
end

---------------------------------------------------------------------------
-- reporting
---------------------------------------------------------------------------
local nextEnts = 0
local entCounts, entTotal = {}, 0

local function Top(kind, wall, limit)
	local b = acc[kind]
	if not b then return {} end
	local list = {}
	for key, e in pairs(b) do
		list[#list + 1] = { k = key, ms = e.ms * 1000 / wall, n = e.calls / wall, max = e.max * 1000, b = e.bytes / wall, src = e.src }
	end
	table.sort(list, function(a, c)
		if a.ms ~= c.ms then return a.ms > c.ms end
		return a.b > c.b
	end)
	for i = limit + 1, #list do list[i] = nil end
	return list
end

function BC.ProfilerTick(now, wall)
	if not P.on then return end
	if now >= nextEnts then
		nextEnts = now + 5
		entCounts, entTotal = {}, 0
		for _, e in ents.Iterator() do
			local c = e:GetClass()
			entCounts[c] = (entCounts[c] or 0) + 1
			entTotal = entTotal + 1
		end
	end
	local entList = {}
	for c, n in pairs(entCounts) do entList[#entList + 1] = { k = c, n = n } end
	table.sort(entList, function(a, b) return a.n > b.n end)
	for i = 31, #entList do entList[i] = nil end

	-- Everything is summed up since profiling started and divided by that time.
	local span = math.max(now - (P.since or now), 1)
	-- Bytes first for outgoing messages: they cost no measurable Lua time.
	local netout = Top("netout", span, 30)
	table.sort(netout, function(a, b) return a.b > b.b end)

	BC.Emit({
		t = "prof",
		hooks = Top("hooks", span, 40),
		timers = Top("timers", span, 30),
		netin = Top("netin", span, 30),
		netout = netout,
		ents = entList,
		entTotal = entTotal,
		since = span,
	})
end

function P.Start()
	if P.on then return end
	P.on = true
	P.since = SysTime()
	acc = {}
	nextEnts = 0
	WrapHooks()
	timer.Create("BetterConsole.RewrapHooks", 5, 0, WrapHooks)
	InstallNet()
	if T then T.measure = MeasureTimer end
end

function P.Stop()
	if not P.on then return end
	P.on = false
	timer.Remove("BetterConsole.RewrapHooks")
	UnwrapHooks()
	RemoveNet()
	if T then T.measure = nil end
	acc = {}
end

BC.On("prof", function(msg)
	if msg.on then P.Start() else P.Stop() end
	BC.Emit({ t = "prof_state", on = P.on })
end)

-- The app went away: nobody looks at the numbers any more.
timer.Create("BetterConsole.ProfilerWatch", 2, 0, function()
	if P.on and not BC.Connected then P.Stop() end
end)
