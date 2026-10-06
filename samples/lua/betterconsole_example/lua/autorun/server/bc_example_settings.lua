--[[
Example: a "Settings" tab in BetterConsole with the API of version 0.4 of the companion addon.

  * a form: console variables of this addon and of the engine (a switch, a number, a text, a list), a variable
    that is not on the server, a value of the addon's own, a header and a note;
  * a button that asks for values first;
  * a table that is updated in place (sorted by a click on a header) and one with links to Lua files;
  * a profiler of the addon's own instead of the built-in one (switched on from the form);
  * explanations of lag spikes (BetterConsole.Stats:OnSpike), and a button that makes one.

New functions are found by their presence, so this file also loads next to an older companion addon.
]]
if not BetterConsole then return end
local BC = BetterConsole

-- The addon's settings: plain console variables, as any addon has them.
CreateConVar("bc_example_mode", "1", FCVAR_ARCHIVE, "How the example addon sends its updates: 0 off, 1 on, 2 on and checks each one.", 0, 2)
local cvDistance = CreateConVar("bc_example_distance", "3000", FCVAR_ARCHIVE, "How far from a player props count as near (units).", 0, 20000)
local cvEnabled = CreateConVar("bc_example_enabled", "1", FCVAR_ARCHIVE, "Whether the example addon does anything at all.")
CreateConVar("bc_example_log_file", "bc_example.txt", FCVAR_ARCHIVE, "File in data/ the example addon writes its log to.")
local cvProfiler = CreateConVar("bc_example_profiler", "0", FCVAR_ARCHIVE, "Use the example addon's profiler instead of BetterConsole's.")

local tab = BC.AddTab("example_settings", { title = "Settings", order = 51, icon = "E713" })
if not isfunction(tab.Form) then return end   -- a companion addon older than 0.4

local level = 3        -- the form's "Busy level": a value of the addon's own, not a console variable
local spikeMs          -- "Make a lag spike": how long the next frame's work takes

local form = tab:Form("settings", {
	title = "Example addon",
	span = 12,
	fields = {
		{ type = "header", text = "Sending" },
		{ convar = "bc_example_mode", text = "Mode", type = "choice",
		  choices = { { "0 · off", "0" }, { "1 · on", "1" }, { "2 · on, with checks", "2" } } },
		{ convar = "bc_example_distance", text = "Near distance", type = "number", step = 500, unit = "units" },
		{ convar = "bc_example_enabled", text = "Enabled", type = "bool", note = "applies after a map change" },
		{ convar = "bc_example_log_file", text = "Log file", type = "text" },
		-- Another part of the addon may or may not be installed: shown greyed out while its variable is missing.
		{ convar = "bc_example_extra_part", text = "Extra part", type = "bool", missing = "show" },
		{ type = "header", text = "Engine" },
		{ convar = "sv_gravity", text = "Gravity", type = "number", step = 50, unit = "units/s²",
		  confirm = "Set the gravity of everyone to {value}?" },
		{ type = "header", text = "Profiling" },
		{ convar = "bc_example_profiler", text = "Use the example's profiler", type = "bool",
		  help = "Statistics → profiler card and Detailed capture are then this addon's: it measures two of its own hooks." },
		-- Not a console variable: a value of the addon's own.
		{ id = "level", text = "Busy level", type = "number", value = 3, min = 1, max = 6,
		  help = "How much work the example's Think hook does each frame (shows in its profiler)." },
		{ type = "note", text = "Values set here are console commands of the server: they are in the console and its history." },
	},
})

local log = tab:Log("log", { title = "What happened", span = 7, max = 100 })

-- Fields without a console variable: their new value (a number here, true/false for a switch, else a string).
tab:OnChange(function(widgetId, fieldId, value)
	if fieldId == "level" then level = value end
	log:Append(("%s set to %s"):format(fieldId, tostring(value)), Color(120, 200, 255))
end)

-- Every change of the form's variables ends up here too (from the form, the console, rcon or another addon).
cvars.AddChangeCallback("bc_example_mode", function(_, old, new)
	log:Append(("bc_example_mode: %s → %s"):format(old, new))
end, "bc_example_settings")

-- A button that asks for values first: they arrive as the third argument.
tab:Buttons("spawn", {
	title = "Props",
	span = 5,
	buttons = {
		{ id = "spawn", text = "Spawn props", style = "primary",
		  fields = {
			{ id = "count", text = "How many", type = "number", default = 5 },
			{ id = "model", text = "Model", default = "models/props_junk/wood_crate001a.mdl", editable = true,
			  choices = { "models/props_junk/wood_crate001a.mdl", "models/props_c17/oildrum001.mdl", "models/props_borealis/bluebarrel001.mdl" } },
		  } },
		{ id = "spike", text = "Make a lag spike", fields = { { id = "ms", text = "Milliseconds", type = "number", default = 250 } } },
	},
	onAction = function(widgetId, actionId, values)
		if actionId == "spawn" then
			local n = math.Clamp(math.floor(tonumber(values.count) or 5), 1, 50)
			local center = Vector(0, 0, 200)
			for i = 1, n do
				local e = ents.Create("prop_physics")
				if IsValid(e) then
					e:SetModel(tostring(values.model))
					e:SetPos(center + Vector(math.random(-300, 300), math.random(-300, 300), 50 * i))
					e:Spawn()
				end
			end
			log:Append(("Spawned %d × %s"):format(n, tostring(values.model)), Color(90, 220, 140))
		elseif actionId == "spike" then
			spikeMs = math.Clamp(tonumber(values.ms) or 250, 60, 2000)
		end
	end,
})

-- A table updated in place: the rows keep their places, selection and scroll position; a click on a header sorts.
local classes = tab:Table("classes", {
	title = "Entities by class (live)", span = 7, key = 1,
	columns = { "Class", { text = "Count", align = "right", width = 80 }, { text = "Near players", align = "right", width = 110 } },
	sort = { column = 2, descending = true },
})
-- Paths of Lua files open in the editor with a double-click.
local hooks = tab:Table("hooks", {
	title = "Think hooks", span = 5, key = 1, links = true,
	columns = { "Name", { text = "Defined in", width = "2*" } },
})

-- Collected only while the tab is seen: nobody looks at it otherwise.
local function Refresh()
	local count, near = {}, {}
	local plys = player.GetAll()
	local d2 = cvDistance:GetFloat() ^ 2
	for _, e in ents.Iterator() do
		local c = e:GetClass()
		count[c] = (count[c] or 0) + 1
		for _, p in ipairs(plys) do
			if e ~= p and e:GetPos():DistToSqr(p:GetPos()) < d2 then near[c] = (near[c] or 0) + 1 break end
		end
	end
	local rows = {}
	for c, n in pairs(count) do rows[#rows + 1] = { c, n, near[c] or 0 } end
	classes:Set(rows)
	local hrows = {}
	for name, fn in pairs(hook.GetTable().Think or {}) do
		local info = debug.getinfo(fn, "S")
		hrows[#hrows + 1] = { tostring(name), info and (info.short_src .. ":" .. info.linedefined) or "?" }
	end
	hooks:Set(hrows)
end

tab:OnShow(function(shown)
	if shown then
		Refresh()
		timer.Create("bc_example_settings", 1, 0, Refresh)
	else
		timer.Remove("bc_example_settings")
	end
end)

-- ------------------------------------------------------------------------ the example's own work

-- Some work each frame and tick, for the profiler below to measure (and a lag spike when asked for).
local work = {}   -- [name] = { ms, n, max, kb } since profiling started
local profiling, capturing = false, false
local frameWork, nFrame = {}, 0   -- for OnSpike: the last 200 frames' { t = SysTime at the end, ms = work }

-- Times fn; while profiling (or capturing) it also adds up what it costs.
local function Measure(name, fn)
	local kb0, t0 = collectgarbage("count"), SysTime()
	fn()
	local dt = SysTime() - t0
	if profiling or capturing then
		local w = work[name]
		if not w then w = { ms = 0, n = 0, max = 0, kb = 0 } work[name] = w end
		w.ms, w.n, w.kb = w.ms + dt * 1000, w.n + 1, w.kb + math.max(collectgarbage("count") - kb0, 0)
		if dt * 1000 > w.max then w.max = dt * 1000 end
	end
	return dt
end

local function Busy()
	local x = 0
	for i = 1, level * 2000 do x = x + math.sin(i) end
	local t = {}
	for i = 1, level * 20 do t[i] = tostring(i) end   -- some garbage: the KB/s column
	if spikeMs then
		local untilT = SysTime() + spikeMs / 1000
		spikeMs = nil
		while SysTime() < untilT do x = x + 1 end
	end
	return x
end

local busyInfo = debug.getinfo(Busy, "S")
local busySrc = busyInfo.short_src .. ":" .. busyInfo.linedefined

-- Only while its profiler measures it or a lag spike was asked for: an example should not cost anything otherwise.
hook.Add("Think", "bc_example_work", function()
	if not cvEnabled:GetBool() or not (profiling or capturing or spikeMs) then return end
	local dt = Measure("Think / bc_example_work", Busy)
	nFrame = nFrame % 200 + 1
	frameWork[nFrame] = { t = SysTime(), ms = dt * 1000 }
end)
hook.Add("Tick", "bc_example_tick", function()
	if not (profiling or capturing) then return end
	Measure("Tick / bc_example_tick", function() for i = 1, level * 300 do local _ = math.sqrt(i) end end)
end)

-- ------------------------------------------------------------------------ an addon's profiler

if isfunction(BC.SetProfiler) then
	local started
	local provider = {
		name = "Example profiler",
		info = "Measures two hooks of the example addon (its Think and Tick), with the Lua memory they allocate. " ..
			"Costs two SysTime calls per hook call while it runs.",
		-- May refuse: the user gets the reason, nothing changes.
		start = function()
			if not cvEnabled:GetBool() then return false, "The example addon is switched off (bc_example_enabled 0): nothing to measure." end
			work, profiling, started = {}, true, SysTime()
		end,
		stop = function() profiling = false end,
		startCapture = function() capturing = true end,
		stopCapture = function() capturing = false end,
	}
	local function Apply()
		BC.SetProfiler(cvProfiler:GetBool() and provider or nil)
		log:Append(cvProfiler:GetBool() and "The example's profiler has the profiler card now" or "The built-in profiler is back")
	end
	cvars.AddChangeCallback("bc_example_profiler", function() Apply() end, "bc_example_settings")
	if cvProfiler:GetBool() then BC.SetProfiler(provider) end

	timer.Create("bc_example_profiler", 1, 0, function()
		if not profiling or not BC.IsProfiling() then return end
		local since = math.max(SysTime() - started, 1)
		local rows, parts = {}, {}
		local total = 0
		for k, w in pairs(work) do total = total + w.ms end
		for k, w in pairs(work) do
			-- The same rows as the built-in profiler (per second since the start); "kb" is a column of its own.
			rows[#rows + 1] = { k = k, ms = w.ms / since, n = w.n / since, max = w.max, kb = w.kb / since, src = busySrc }
			parts[#parts + 1] = { k, ("%.0f %%"):format(total > 0 and w.ms * 100 / total or 0) }
		end
		table.sort(rows, function(a, b) return a.ms > b.ms end)
		BC.ProfilerReport({
			hooks = rows, since = since,
			extra = { { id = "parts", title = "Example: share of the measured time", columns = { "Hook", { text = "of the time", align = "right" } }, rows = parts } },
		})
	end)
end

hook.Add("BetterConsoleProfiler", "bc_example_settings", function(on)
	log:Append(on and "Profiling started" or "Profiling stopped")
end)

-- ------------------------------------------------------------------------ lag spikes

if isfunction(BC.Stats.OnSpike) then
	BC.Stats:OnSpike("bc_example", function(spike)
		-- The example's work in the long frame: its Think ran between the frame's start and spike.st.
		local from, ms = spike.st - spike.ms / 1000 - 0.001, 0
		for _, f in ipairs(frameWork) do
			if f.t > from and f.t <= spike.st + 0.001 then ms = ms + f.ms end
		end
		if ms >= 1 then
			spike.causes = { { text = ("example work %.0f ms"):format(ms), kind = ms > spike.ms * 0.5 and "danger" or "accent",
				tooltip = "The Think hook of the example addon (bc_example_settings.lua)." } }
			spike.lua = { { text = "example Think", ms = ms, src = busySrc } }
		end
	end)
end

-- The value of the addon's own field, as the addon has it.
form:Set({ level = level })
