--[[
Example: a "Server overview" tab in BetterConsole, made by a server addon.

Copy this folder into garrysmod/addons/ of your server. When the server runs under BetterConsole the tab
appears next to the built-in ones; without BetterConsole every call below does nothing.

It shows the widget types KeyValue, Chart, Log, Table, Buttons, Text, a status bar item, a key number and a chart
on the Statistics tab, and items in the right-click menu of the Players tab. bc_example_settings.lua next to it
shows what came with version 0.4: a settings form, buttons with a dialog, live tables, a profiler of its own and
explanations of lag spikes.
]]
if not BetterConsole then return end

local tab = BetterConsole.AddTab("example_overview", { title = "Overview", order = 50, icon = "E80F" })

local info = tab:KeyValue("info", { title = "Server", span = 4 })
local chart = tab:Chart("activity", {
	title = "Props and NPCs",
	span = 8,
	series = {
		{ name = "props", color = "#4F8CFF" },
		{ name = "NPCs", color = "#F2B33D" },
	},
})
local events = tab:Log("events", { title = "Events", span = 7, max = 200 })
local actions = tab:Buttons("actions", {
	title = "Actions",
	span = 5,
	buttons = {
		{ id = "hello", text = "Say hello", style = "primary" },
		{ id = "freeze", text = "Freeze all props" },
		{ id = "cleanup", text = "Clean up the map", style = "danger", confirm = "Remove every prop and NPC on the map?" },
	},
})
local top = tab:Table("owners", { title = "Props by player", span = 7, columns = { "Player", "Props", "Ragdolls" } })
tab:Text("help", { title = "About this tab", span = 5 }):Set(
	"This tab comes from samples/lua/betterconsole_example. Read docs/lua-api.md to build your own: "
	.. "everything here is plain Lua on the server.")

tab:OnAction(function(widget, id)
	if id == "hello" then
		PrintMessage(HUD_PRINTTALK, "Hello from the server console!")
		events:Append("Said hello to everyone", Color(120, 200, 255))
	elseif id == "freeze" then
		local n = 0
		for _, e in ipairs(ents.FindByClass("prop_physics")) do
			local phys = e:GetPhysicsObject()
			if IsValid(phys) then phys:EnableMotion(false) n = n + 1 end
		end
		events:Append(("Froze %d props"):format(n), Color(240, 200, 90))
	elseif id == "cleanup" then
		game.CleanUpMap()
		events:Append("Map cleaned up", Color(240, 90, 90))
	end
end)

-- The Statistics tab: a key number among the built-in ones (after "Entities", which has the order 90) and a chart.
local propsStat = BetterConsole.Stats:Stat("props", { title = "Props", tooltip = "Props and ragdolls on the map (example addon)", order = 95 })
local propsChart = BetterConsole.Stats:Chart("props_chart", {
	title = "Props and NPCs",
	series = { { name = "props", color = "#4F8CFF" }, { name = "NPCs", color = "#F2B33D" } },
})

-- The right-click menu of the Players tab. Two items with opposite filters make a toggle: each player gets the
-- one that fits (with several selected, both, each for its part).
BetterConsole.AddPlayerAction("example_freeze", {
	text = "Freeze", icon = "E769",
	filter = function(ply) return not ply:IsFrozen() end,
	onRun = function(plys) for _, p in ipairs(plys) do p:Freeze(true) end end,
})
BetterConsole.AddPlayerAction("example_unfreeze", {
	text = "Unfreeze", icon = "E768",
	filter = function(ply) return ply:IsFrozen() end,
	onRun = function(plys) for _, p in ipairs(plys) do p:Freeze(false) end end,
})
-- A dialog first; the values arrive in onRun.
BetterConsole.AddPlayerAction("example_health", {
	text = "Set health", icon = "E95E",
	fields = { { id = "hp", text = "Health", type = "number", default = 100 } },
	onRun = function(plys, values)
		local hp = math.Clamp(tonumber(values.hp) or 100, 1, 100000)
		for _, p in ipairs(plys) do p:SetHealth(hp) end
		events:Append(("Health %d for %d player(s)"):format(hp, #plys), Color(90, 220, 140))
	end,
})
-- No Lua at all: a console command for each player. ULX's slap needs ULX.
if ulx then
	BetterConsole.AddPlayerAction("example_slap", {
		text = "Slap", icon = "E945", order = 230,
		fields = { { id = "damage", text = "Damage", choices = { { "None", "0" }, { "10", "10" }, { "50", "50" } }, default = "0" } },
		command = "ulx slap {target} {damage}",
	})
end

local function Owner(ent)
	local o = ent.GetCreator and ent:GetCreator() or nil
	return IsValid(o) and o or nil
end

timer.Create("bc_example_overview", 2, 0, function()
	if not BetterConsole.IsActive() then return end
	local props, npcs = 0, 0
	local byOwner = {}
	for _, e in ents.Iterator() do
		local c = e:GetClass()
		if c == "prop_physics" or c == "prop_ragdoll" then
			props = props + 1
			local o = Owner(e)
			if o then
				local row = byOwner[o] or { o:Nick(), 0, 0 }
				row[c == "prop_ragdoll" and 3 or 2] = row[c == "prop_ragdoll" and 3 or 2] + 1
				byOwner[o] = row
			end
		elseif e:IsNPC() then
			npcs = npcs + 1
		end
	end
	chart:Push(props, npcs)
	local rows = {}
	for _, r in pairs(byOwner) do rows[#rows + 1] = r end
	table.sort(rows, function(a, b) return a[2] > b[2] end)
	top:Set(rows)
	info:Set({
		Map = game.GetMap(),
		Gamemode = engine.ActiveGamemode(),
		Players = ("%d / %d"):format(player.GetCount(), game.MaxPlayers()),
		Uptime = string.NiceTime(SysTime()),
		Props = props,
	})
	BetterConsole.SetStatus("example_props", { label = "Props", text = props, tooltip = "Props on the map (example addon)", name = "Props (example addon)" })
	propsStat:Set(props, ("%d NPCs"):format(npcs))
	propsChart:Push(props, npcs)
end)

hook.Add("PlayerInitialSpawn", "bc_example_overview", function(ply)
	events:Append(ply:Nick() .. " joined", Color(90, 220, 140))
end)
hook.Add("PlayerDisconnected", "bc_example_overview", function(ply)
	events:Append(ply:Nick() .. " left", Color(170, 170, 170))
end)
hook.Add("PlayerSay", "bc_example_overview", function(ply, text)
	events:Append(ply:Nick() .. ": " .. text)
end)
