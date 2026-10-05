--[[
BetterConsole companion addon - Lua errors.

GM:OnLuaError(err, realm, stack, addonTitle, workshopId) reports the server's own errors,
GM:OnClientLuaError(err, ply, stack, addonTitle) the errors clients send to the server. Both are
forwarded to the app with the stack. An error that repeats (one per frame from a broken Think hook) is
sent at most once a second per kind, with the number of repeats.

Errors that happen before the app is connected (server start-up) are kept and sent on connect.
]]
local BC = BetterConsole
local SysTime, tostring, tonumber, istable, ipairs = SysTime, tostring, tonumber, istable, ipairs

local seen = {}         -- [key] = { sent = SysTime of the last send, pending = repeats since, msg = last payload }
local seenCount = 0
local early = {}        -- payloads from before the connection
local inHandler = false -- an error inside our handler must not recurse

local function Frames(stack)
	local out = {}
	if not istable(stack) then return out end
	for _, f in ipairs(stack) do
		-- The profiler's and the timer detour's wrappers are not part of anybody's bug.
		if istable(f) and not tostring(f.File or ""):find("betterconsole/sv_", 1, true) then
			out[#out + 1] = {
				fn = (f.Function ~= nil and f.Function ~= "") and tostring(f.Function) or "unknown",
				src = tostring(f.File or f.short_src or f.source or "?"),
				line = tonumber(f.Line or f.currentline or f.line) or 0,
			}
		end
	end
	return out
end

local function Key(prefix, err, frames)
	local k = prefix .. "\0" .. err
	for i = 1, math.min(#frames, 8) do
		local f = frames[i]
		k = k .. "\0" .. f.src .. ":" .. f.line
	end
	return k
end

local function Deliver(msg)
	if BC.Connected then
		BC.Emit(msg)
	elseif #early < 200 then
		early[#early + 1] = msg
	end
end

local function Report(key, msg)
	local now = SysTime()
	local s = seen[key]
	if s and now - s.sent < 1 then
		s.pending = s.pending + 1
		s.msg = msg
		return
	end
	if not s then
		if seenCount > 4000 then seen, seenCount = {}, 0 end
		s = { pending = 0 }
		seen[key] = s
		seenCount = seenCount + 1
	end
	s.sent = now
	msg.n = 1
	Deliver(msg)
end

-- Once a second: the repeats that were held back.
timer.Create("BetterConsole.ErrorRepeats", 1, 0, function()
	local now = SysTime()
	for key, s in pairs(seen) do
		if s.pending > 0 then
			local msg = s.msg
			msg.n = s.pending
			s.pending = 0
			s.sent = now
			Deliver(msg)
		elseif now - s.sent > 120 then
			seen[key] = nil
			seenCount = seenCount - 1
		end
	end
end)

hook.Add("OnLuaError", "BetterConsole", function(err, realm, stack, addonTitle, workshopId)
	if inHandler then return end
	inHandler = true
	local ok = pcall(function()
		err = tostring(err)
		local frames = Frames(stack)
		Report(Key("sv", err, frames), {
			t = "err",
			realm = "server",
			msg = err,
			stack = frames,
			addon = addonTitle and tostring(addonTitle) or nil,
			wsid = workshopId and tostring(workshopId) or nil,
			time = os.time(),
		})
	end)
	inHandler = false
	if not ok then return end
end)

hook.Add("OnClientLuaError", "BetterConsole", function(err, ply, stack, addonTitle)
	if inHandler then return end
	inHandler = true
	pcall(function()
		err = tostring(err)
		local frames = Frames(stack)
		local p = { name = "?", sid = "", sid64 = "", uid = 0 }
		if IsValid(ply) then
			p.name = ply:Nick()
			p.sid = ply:SteamID()
			p.sid64 = ply:SteamID64() or ""
			p.uid = ply:UserID()
		end
		Report(Key("cl" .. p.sid64, err, frames), {
			t = "err",
			realm = "client",
			msg = err,
			stack = frames,
			addon = addonTitle and addonTitle ~= "ERROR" and tostring(addonTitle) or nil,
			ply = p,
			time = os.time(),
		})
	end)
	inHandler = false
end)

local prevConnected = BC.OnConnected
function BC.OnConnected()
	if prevConnected then prevConnected() end
	for _, msg in ipairs(early) do BC.Emit(msg) end
	early = {}
end
