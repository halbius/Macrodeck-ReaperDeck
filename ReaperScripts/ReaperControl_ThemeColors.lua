-- ReaperControl_ThemeColors.lua
--
-- Publishes the colors REAPER draws markers and regions without a color of their own in (from the
-- current theme) for the Macro Deck plugin "REAPER Control" (marker buttons with "Use marker color").
-- Runs once and ends; the plugin starts it when the values are missing (after REAPER or the plugin
-- started). Run it by hand after switching the theme.
--
-- ExtState "ReaperControl" / "ThemeMarkerColor" and "ThemeRegionColor" (not persisted): "#rrggbb".

if not reaper.GetThemeColor then return end -- REAPER older than 6.x: the plugin uses its default red

local function publish(themeKey, extKey)
  local native = reaper.GetThemeColor(themeKey, 0)
  if native and native ~= -1 then
    local r, g, b = reaper.ColorFromNative(native)
    reaper.SetExtState("ReaperControl", extKey, string.format("#%02x%02x%02x", r, g, b), false)
  end
end

-- Region first: the plugin takes "ThemeMarkerColor" as the sign that the script ran.
publish("region", "ThemeRegionColor")
publish("marker", "ThemeMarkerColor")
