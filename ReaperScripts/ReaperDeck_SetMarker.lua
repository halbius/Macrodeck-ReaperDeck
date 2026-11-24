-- ReaperDeck_SetMarker.lua
--
-- "Set to cursor" of the Macro Deck plugin "ReaperDeck" (action "Go to marker", On press = Set).
-- Runs once per press; the plugin first writes the job to ExtState "ReaperControl" / "MarkerSet":
--
--   kind \t id \t new id \t region mode \t edit \t name
--
--   kind         "M" marker, "R" region
--   id           the marker/region the button shows (empty: create a new one)
--   new id       ID for a new one (empty: REAPER picks the next free one)
--   region mode  "move" (start to the cursor, same length), "start" (start to the cursor, end stays),
--                "selection" (to the time selection); a new region always takes the time selection
--   edit         "1": ask for the name afterwards
--   name         name of a new one (e.g. the active filter text)
--
-- Cursor: the play position while playing, the edit cursor otherwise. One undo step.

local job = reaper.GetExtState("ReaperControl", "MarkerSet")
if job == "" then return end
reaper.DeleteExtState("ReaperControl", "MarkerSet", false)

local f = {}
for part in (job .. "\t"):gmatch("([^\t]*)\t") do f[#f + 1] = part end
local isRegion = f[1] == "R"
local id = tonumber(f[2] or "")
local newId = tonumber(f[3] or "") or -1
local regionMode = f[4] or "move"
local edit = f[5] == "1"
local newName = f[6] or ""

local function message(text)
  reaper.ShowMessageBox(text, "ReaperDeck", 0)
end

local cursor = reaper.GetPlayState() ~= 0 and reaper.GetPlayPosition() or reaper.GetCursorPosition()

-- The project index of the marker/region with this ID, and its values.
local function find(wantedId)
  local i = 0
  while true do
    local ok, rgn, pos, rgnEnd, name, number, color = reaper.EnumProjectMarkers3(0, i)
    if ok == 0 then return nil end
    if rgn == isRegion and number == wantedId then
      return i, pos, rgnEnd, name, color
    end
    i = i + 1
  end
end

local function time_selection()
  local s, e = reaper.GetSet_LoopTimeRange(false, false, 0, 0, false)
  if e > s then return s, e end
  return nil
end

local function ask_name(current)
  local ok, name = reaper.GetUserInputs(isRegion and "Region name" or "Marker name", 1,
    "Name:,extrawidth=200", current or "")
  if ok then return name end
  return nil
end

reaper.Undo_BeginBlock()
local description = isRegion and "Set region (Macro Deck)" or "Set marker (Macro Deck)"

if id then
  -- Move the existing one.
  local index, pos, rgnEnd, name, color = find(id)
  if not index then
    reaper.Undo_EndBlock(description, -1)
    message((isRegion and "Region " or "Marker ") .. id .. " not found.")
    return
  end

  local startPos, endPos = cursor, 0
  if isRegion then
    if regionMode == "selection" then
      startPos, endPos = time_selection()
      if not startPos then
        reaper.Undo_EndBlock(description, -1)
        message("No time selection.")
        return
      end
    elseif regionMode == "start" then
      endPos = rgnEnd
      if cursor >= rgnEnd then
        reaper.Undo_EndBlock(description, -1)
        message("The cursor is at or after the end of the region.")
        return
      end
    else -- move
      endPos = cursor + (rgnEnd - pos)
    end
  end

  if edit then name = ask_name(name) or name end
  reaper.SetProjectMarkerByIndex2(0, index, isRegion, startPos, endPos, id, name, color, 0)
else
  -- Create a new one.
  local startPos, endPos = cursor, 0
  if isRegion then
    startPos, endPos = time_selection()
    if not startPos then
      reaper.Undo_EndBlock(description, -1)
      message("A new region needs a time selection.")
      return
    end
  end

  local name = newName
  if edit then
    name = ask_name(name)
    if not name then -- cancelled: nothing created
      reaper.Undo_EndBlock(description, -1)
      return
    end
  end
  reaper.AddProjectMarker2(0, isRegion, startPos, endPos, name, newId, 0)
end

reaper.Undo_EndBlock(description, -1)
reaper.UpdateTimeline()
