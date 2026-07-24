using System;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using SharpDX;
using System.IO;
using System.Linq;
using ExileCampaigns.Build;
using System.Collections.Generic;
using Vector4 = System.Numerics.Vector4;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using RectangleF = SharpDX.RectangleF;

namespace ExileCampaigns;

// custom-drawn, tabbed settings panel. replaces the framework's reflected [Menu] list with native
// ImGui tabs + hand-rendered controls. called from DrawSettings instead of base.DrawSettings().
public partial class ExileCampaigns
{
    private void DrawSettingsTabs()
    {
        if (!ImGui.BeginTabBar("##ec_settings", ImGuiTabBarFlags.None))
            return;

        if (ImGui.BeginTabItem("Profiles"))
        {
            DrawProfileSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Route"))
        {
            Toggle("Auto-advance", Settings.AutoAdvance,
                "Advance the displayed step automatically when you enter the next zone");
            Toggle("Show optional steps", Settings.ShowOptional, "Include steps marked (Opt) from the route");
            Toggle("Show league-start steps", Settings.ShowLeagueStart,
                "Include league-start chores (crafting recipes, trials). Turn off on a re-run when you don't need them");

            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.92f, 0.80f, 0.43f, 1f), "Route file");
            DrawRouteFileControls();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Overlays"))
        {
            Toggle("Lock overlays", Settings.LockOverlays,
                "When off, drag any overlay with the left mouse button to reposition it. Turn on once placed so clicks pass through.");
            Combo("Side panel behaviour", Settings.PanelAvoidMode, PanelAvoidModes,
                "What an overlay does when the character/inventory panel opens over it. Big windows (stash, vendor, crafting) always hide it.");
            ImGui.SeparatorText("Route guide (steps)");
            DrawStepsStyle(Settings.Steps);
            ImGui.SeparatorText("Statistics Overlay");
            DrawCharStatsStyle(Settings.CharStats);
            SliderInt("XP rate window (min)", Settings.XpRateWindowMinutes, "Average xp/hour + time-to-level over the last N minutes");
            ImGui.SeparatorText("Build Planner Overlay");
            DrawOverlayStyle(Settings.BuildPanel, "buildpanel");
            ImGui.SeparatorText("Waypoint overlay");
            Toggle("Enabled##wp", Settings.WaypointOverlay.Enable,
                "On a 'Waypoint to X' step, highlight which waypoint to click on the open World Map");
            var wo = Settings.WaypointOverlay;
            DragFloat("Center X offset##wo", wo.OffsetX, 0.0005f, "Ring centre X as a fraction of the map panel height (drag slow or ctrl+click to type)");
            DragFloat("Center Y offset##wo", wo.OffsetY, 0.0005f, "Ring centre Y as a fraction of the map panel height");
            DragFloat("Ring scale##wo", wo.Scale, 0.0005f, "Ring radius as a fraction of the map panel height");
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Build"))
        {
            DrawBuildTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Guidance"))
        {
            ImGui.SeparatorText("Guidance surface");
            bool radarLive = _radarLookForRoute != null;
            bool eminiLive = _eminimapClusterTarget != null;
            ImGui.TextDisabled($"Radar: {(radarLive ? "detected" : "not detected")}    ExileMinimap: {(eminiLive ? "detected" : "not detected")}");
            if (!radarLive && !eminiLive)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.2f, 1f), "No guidance provider detected - install/enable Radar or ExileMinimap.");
            else if (radarLive && eminiLive)
                Combo("Surface##guid", Settings.GuidanceProvider.Surface, GuidanceSurfaces, "where campaign guidance draws (path + minimap icons)");
            else
                ImGui.TextDisabled(radarLive ? "Only Radar live -> drawing on the in-game map." : "Only ExileMinimap live -> drawing on its panel.");

            Toggle("Remember target locations", Settings.GuidanceProvider.RememberTargetLocations,
                "Keep an entity path/icon at its last-seen spot when it leaves load range (per area). Great for towns where nothing moves.");

            ImGui.SeparatorText("Path to next step");
            var p = Settings.Path;
            Toggle("Show path on ground", p.ShowPathOnGround, "Draw a line on the terrain toward the objective (in-game map / Radar mode; ExileMinimap draws its own)");
            Toggle("Show path on minimap", p.ShowPathOnMinimap, "Draw the path on the in-game large map");
            Toggle("Ground path only with map closed", p.ShowGroundPathOnlyWithClosedMap,
                "Hide the ground line while the large map is open");
            ColorEdit("Path color", p.PathColor);
            SliderFloat("Path thickness", p.PathThickness);
            SliderInt("Draw every Nth point", p.DrawEveryNthSegment, "Higher = sparser/faster");
            Toggle("Flowing comets", p.ShowComets, "Slide comet sprites along the ground path toward the objective");
            Toggle("Comets only (hide line)", p.CometsOnly, "When comets are on, don't draw the solid ground line");
            ColorEdit("Comet color", p.CometColor);
            SliderFloat("Comet spacing", p.CometSpacing, "Grid units between comets; count scales with path length");
            SliderFloat("Comet size", p.CometSize, "Comet length in grid units");
            SliderFloat("Comet speed", p.CometSpeed, "Flow speed in grid units per second");

            ImGui.SeparatorText("Interaction indicator");
            var ii = Settings.InteractIndicator;
            Toggle("Enabled##ii", ii.Enable, "Draw a golden pulsing arrow above the NPC/chest/boss the step wants you to interact with");
            ColorEdit("Arrow color##ii", ii.IconColor);
            SliderFloat("Arrow size##ii", ii.IconSize, "Icon height in pixels");
            SliderFloat("Bob speed##ii", ii.BobSpeed, "How fast the arrow bobs (0 = steady)");
            SliderFloat("Bob distance##ii", ii.BobDistance, "How far it bobs, in pixels");
            SliderFloat("Height offset##ii", ii.HeightOffset, "Extra world units above the entity's head");
            SliderFloat("Target search distance##ii", ii.MaxDistance, "Only mark targets within this distance");
            SliderFloat("Proximity advance distance##ii", ii.NearDistance, "For bosses / quest objects, auto-advance when this close");

            ImGui.SeparatorText("Minimap icons");
            var mi = Settings.MinimapIcons;
            Toggle("Enabled##mmi", mi.Enable, "Draw authored minimap icons for the current area on the large map");
            SliderInt("Icon size##mmi", mi.IconSize, "Icon size in pixels");
            SliderInt("Lookahead steps##mmi", mi.Lookahead, "Only show icons for the current step plus this many upcoming steps (same area). 0 = current step only");
            Toggle("Pulse current step##mmi", mi.PulseCurrent, "Animate the icons for the current objective so they stand out");
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Alerts"))
        {
            Toggle("Unlock banner + toast alert", Settings.AlertsMovable, "Drag the banner and toasts to reposition them even when overlays are locked (Alt-drag also works). Shows a sample to grab when none is live.");

            ImGui.SeparatorText("Auto-advance banner");
            var b = Settings.Banner;
            Toggle("Enabled##banner", b.Enable);
            Toggle("Preview##banner", b.Preview, "Keep the banner on screen with sample text so you can place it (needs overlays unlocked)");
            Toggle("Persistent##banner", b.Persistent, "Keep the banner up until the next auto-advance instead of fading after the duration");
            ColorEdit("Text color##banner", b.TextColor);
            ColorEdit("Background color##banner", b.BackgroundColor, "Alpha controls opacity");
            ColorEdit("Border color##banner", b.BorderColor);
            SliderFloat("Text size##banner", b.TextSize, "Font height in pixels");
            SliderInt("Border thickness##banner", b.BorderThickness, "0 = no border");
            SliderInt("Padding##banner", b.Padding);
            SliderFloat("Duration (seconds)##banner", b.DurationSeconds, "Stays this long, fades over the last 0.5s");

            ImGui.SeparatorText("Toasts");
            var t = Settings.Toasts;
            Toggle("Enabled##toast", t.Enable);
            Toggle("Preview##toast", t.Preview, "Draw a sample toast so you can position it. Turn off when done.");
            ColorEdit("Text color##toast", t.TextColor);
            ColorEdit("Background color##toast", t.BackgroundColor, "Alpha controls opacity");
            ColorEdit("Border color##toast", t.BorderColor);
            SliderFloat("Text size##toast", t.TextSize, "Font height in pixels");
            SliderInt("Border thickness##toast", t.BorderThickness, "0 = no border");
            SliderInt("Padding##toast", t.Padding);
            SliderFloat("Duration (seconds)##toast", t.DurationSeconds, "Stays this long, fades over the last 0.5s");

            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Hotkeys"))
        {
            ImGui.TextDisabled("Click a button, then press the key to bind. Right-click to clear.");
            if (ImGui.BeginTable("##hk_table", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX))
            {
                ImGui.TableSetupColumn("##hk_label", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##hk_key", ImGuiTableColumnFlags.WidthFixed);
                HotkeyRow("Next step", Settings.NextStepKey, "Manually advance (pauses auto-advance until the next zone change)");
                HotkeyRow("Previous step", Settings.PrevStepKey, "Manually go back one step");
                HotkeyRow("Toggle overlay", Settings.ToggleKey, "Show/hide the whole overlay");
                HotkeyRow("Add hovered item to build", Settings.AddBuildItemKey, "Adds the item under the cursor to the selected build set");
                ImGui.EndTable();
            }
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Dev & Diagnostics"))
        {
            ImGui.SeparatorText("Dev overlay");
            var d = Settings.Dev;
            Toggle("Show dev overlay", d.ShowDevOverlay, "Master switch: route-authoring overlays on the large minimap");
            Toggle("Room names", d.ShowRoomNames, "Draw AreaGraph room outlines + name labels");
            Toggle("Entity labels", d.ShowEntityLabels, "Label AreaTransition / Waypoint / boss entities with their path");
            Toggle("Log quest flags (dev)", Settings.LogQuestFlags,
                "Harvesting tool: record each quest flag as it flips true, to 'quest-flag-harvest.jsonl' in the config folder");
            Toggle("Show quick edit panel", d.ShowTriageButtons,
                "Floating panel to quick-add steps/objectives, move/delete steps, bind advances, and set Radar paths " +
                "on the current objective from the zone's Radar targets");

            ImGui.SeparatorText("Route editor & routes");
            Toggle("Show route editor", Settings.Editor.Enable,
                "Show the in-game route editor panel (overlays must be unlocked to drag)");
            Button(Settings.ReloadRoutes, "Reload routes",
                "Re-read route files from disk (bundled, or your override under the config folder)");

            ImGui.SeparatorText("Diagnostics");
            var diag = Settings.Diagnostics;
            Toggle("Record events (for bug reports)", diag.RecordDiagnostics,
                "Opt-in: record recent zone changes, flag flips and path-target changes so an export captures what led up to a bug");
            Hotkey("Export diagnostics (hotkey)", diag.ExportKey, "Write a diagnostic JSON report and open its folder");
            Button(diag.ExportNow, "Export diagnostics now", "Write the diagnostic JSON report now and open its folder");
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // -- reused submenu renderers --

    private static void DrawOverlayStyle(OverlayStyle s, string id)
    {
        Toggle("Enabled##" + id, s.Enable);
        ColorEdit("Text color##" + id, s.TextColor);
        ColorEdit("Header color##" + id, s.HeaderColor);
        ColorEdit("Optional color##" + id, s.OptionalColor);
        ColorEdit("Background color##" + id, s.BackgroundColor, "Alpha controls opacity; alpha 0 = no panel background");
        ColorEdit("Border color##" + id, s.BorderColor);
        SliderFloat("Text size##" + id, s.TextSize, "Font height in pixels");
        SliderInt("Border thickness##" + id, s.BorderThickness, "0 = no border");
        SliderInt("Padding##" + id, s.Padding);
    }

    private static void DrawStepsStyle(StepsOverlayStyle s)
    {
        const string id = "steps";
        Toggle("Enabled##" + id, s.Enable);
        ColorEdit("Text color##" + id, s.TextColor);
        ColorEdit("Header color##" + id, s.HeaderColor, "Colour of the ACT stage header");
        ColorEdit("Current-step color##" + id, s.CurrentColor, "Colour of the active step so it stands out");
        ColorEdit("Optional color##" + id, s.OptionalColor);
        ColorEdit("League-start color##" + id, s.LeagueStartColor, "Colour of league-start steps (crafting recipes, trials)");
        ColorEdit("Background color##" + id, s.BackgroundColor, "Alpha controls opacity; alpha 0 = no panel background");
        ColorEdit("Border color##" + id, s.BorderColor);
        SliderFloat("Text size##" + id, s.TextSize, "Font height in pixels");
        SliderInt("Border thickness##" + id, s.BorderThickness, "0 = no border");
        SliderInt("Padding##" + id, s.Padding);
        SliderInt("Steps shown behind##" + id, s.StepsBehind, "How many completed steps to show above the current one");
        SliderInt("Steps shown ahead##" + id, s.StepsAhead, "How many upcoming steps to show below the current one");
        Toggle("Show campaign progress bar##" + id, s.ShowCampaignBar, "Segmented 10-act bar + overall percent above the steps");
        Toggle("Show act progress bar##" + id, s.ShowActBar, "Per-act progress bar + percent above the steps");
    }

    private static readonly string[] PenaltyModes = { "Bar", "Text", "Off" };
    private static readonly string[] GuidanceSurfaces = { "In-game map (Radar)", "ExileMinimap panel" };
    private static readonly string[] PanelAvoidModes = { "Hide overlay", "Offset overlay" };

    // the XP/stats panel: OverlayStyle controls plus the redesigned 2a row toggles + penalty-display mode.
    private static void DrawCharStatsStyle(CharStatsOverlayStyle s)
    {
        const string id = "charstats";
        Toggle("Enabled##" + id, s.Enable);
        ColorEdit("Text color##" + id, s.TextColor);
        ColorEdit("Header color##" + id, s.HeaderColor);
        ColorEdit("Background color##" + id, s.BackgroundColor, "Alpha controls opacity; alpha 0 = no panel background");
        ColorEdit("Border color##" + id, s.BorderColor);
        SliderFloat("Text size##" + id, s.TextSize, "Font height in pixels");
        SliderInt("Border thickness##" + id, s.BorderThickness, "0 = no border");
        SliderInt("Padding##" + id, s.Padding);
        Toggle("Show run/act timers##" + id, s.ShowTimers);
        Toggle("Show XP bar##" + id, s.ShowXpBar);
        Toggle("Show XP/h##" + id, s.ShowXpRate);
        Toggle("Show XP to level##" + id, s.ShowXpToGo);
        Toggle("Show ETA to level##" + id, s.ShowEta);
        Combo("XP penalty display##" + id, s.PenaltyMode, PenaltyModes, "Bar = safe-zone axis + ticks; Text = just the % line; Off = hidden");
    }

    // -- per-node control helpers --

    private static void Tip(string? tip)
    {
        if (!string.IsNullOrEmpty(tip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);
    }

    private static void Toggle(string label, ToggleNode n, string? tip = null)
    {
        bool v = n.Value;
        if (ImGui.Checkbox(label, ref v)) n.Value = v;
        Tip(tip);
    }

    private static void SliderInt(string label, RangeNode<int> n, string? tip = null)
    {
        int v = n.Value;
        if (ImGui.SliderInt(label, ref v, n.Min, n.Max)) n.Value = v;
        Tip(tip);
    }

    // dropdown bound to an int RangeNode (value = selected index into items).
    private static void Combo(string label, RangeNode<int> n, string[] items, string? tip = null)
    {
        int idx = Math.Clamp(n.Value, 0, items.Length - 1);
        if (ImGui.Combo(label, ref idx, items, items.Length)) n.Value = idx;
        Tip(tip);
    }

    private static void SliderFloat(string label, RangeNode<float> n, string? tip = null)
    {
        float v = n.Value;
        if (ImGui.SliderFloat(label, ref v, n.Min, n.Max, "%.1f")) n.Value = v;
        Tip(tip);
    }

    // fine drag for values that need small steps (drag slow, or ctrl+click to type). 3 decimals.
    private static void DragFloat(string label, RangeNode<float> n, float speed, string? tip = null)
    {
        float v = n.Value;
        if (ImGui.DragFloat(label, ref v, speed, n.Min, n.Max, "%.3f")) n.Value = v;
        Tip(tip);
    }

    private static void ColorEdit(string label, ColorNode n, string? tip = null)
    {
        var c = n.Value;
        var v = new Vector4(c.R, c.G, c.B, c.A) / 255f;
        if (ImGui.ColorEdit4(label, ref v, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
            n.Value = new Color((byte)Byte(v.X), (byte)Byte(v.Y), (byte)Byte(v.Z), (byte)Byte(v.W));
        Tip(tip);
    }

    // min text width for a picker button, so every button face is the same width regardless of
    // the bound key. DrawPickerButton has no size arg and auto-fits its label, so we right-pad the
    // visible text with spaces until it reaches this width. wider than any single keybind string.
    private const float HotkeyBtnTextWidth = 90f;

    // the picker renders the id string AS its button label (the visible text before "##"),
    // so we feed it the padded binding then a hidden unique tail. blank "##.." = invisible button.
    private static void DrawHotkeyButton(string id, HotkeyNodeV2 n)
    {
        n.DrawPickerButton($"{PadToWidth(KeyLabel(n))}##hk_{id}");
    }

    // append spaces until the rendered text reaches HotkeyBtnTextWidth (trailing spaces still count
    // toward a button's auto width). proportional font, so we measure rather than count chars.
    private static string PadToWidth(string text)
    {
        while (ImGui.CalcTextSize(text).X < HotkeyBtnTextWidth)
            text += " ";
        return text;
    }

    private static string KeyLabel(HotkeyNodeV2 n)
    {
        var v = n.Value;
        if (v == null) return "(unbound)";
        return v.Mode switch
        {
            HotkeyNodeV2.HotkeyNodeMode.Keyboard => (v.Win ? "Win+" : "") + v.Key,
            HotkeyNodeV2.HotkeyNodeMode.Controller => v.ControllerModifierKey != 0
                ? $"{v.ControllerModifierKey}+{v.ControllerKey}"
                : v.ControllerKey.ToString(),
            _ => "(unbound)",
        };
    }

    // table row: label in the stretch column, fixed-width picker in the key column
    private static void HotkeyRow(string label, HotkeyNodeV2 n, string? tip = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        Tip(tip);
        ImGui.TableNextColumn();
        DrawHotkeyButton(label, n);
    }

    // inline (non-table) hotkey row, e.g. the lone diagnostics export key
    private static void Hotkey(string label, HotkeyNodeV2 n, string? tip = null)
    {
        ImGui.Text(label);
        Tip(tip);
        ImGui.SameLine();
        n.DrawPickerButton($"{PadToWidth(KeyLabel(n))}##hk_{label}");
    }

    private static void Button(ButtonNode n, string label, string? tip = null)
    {
        if (ImGui.Button(label)) n.OnPressed?.Invoke();
        Tip(tip);
    }

    // clamp a 0..1 channel back to a 0..255 byte
    private static int Byte(float f) => Math.Clamp((int)Math.Round(f * 255f), 0, 255);

    // manual profile UI at top of settings: auto-switch toggle + list/load/create/delete per-character profiles under ConfigDirectory\profiles
    private string _newProfileName = "";
    private int _selectedProfileIdx = -1;
    private string? _confirmResetProfile;   // name pending reset confirmation (two-step button)
    private string? _confirmCopyFrom;        // source profile pending copy-into-current confirmation

    // display names = filenames (sans .json). PoE ids have no illegal chars so they survive SanitizeProfile, display == switch key
    private string[] ListProfiles()
    {
        try
        {
            if (!Directory.Exists(ProfilesDir)) return Array.Empty<string>();
            return Directory.GetFiles(ProfilesDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
        }
        catch { return Array.Empty<string>(); }
    }

    // create + activate, writing immediately so it shows in the list at step 0. filesafe up front so display id matches the file
    private void CreateProfileManually(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        name = SanitizeProfile(name);
        SwitchProfile(name);   // banks old profile, loads/zeroes this one
        SaveProgress();        // force-write so the file exists before the first step change
    }

    private void DeleteProfile(string name)
    {
        try
        {
            var path = Path.Combine(ProfilesDir, SanitizeProfile(name) + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> delete profile failed: {ex.Message}"); }

        // deleted the active profile: drop to no-profile state. auto-switch re-creates it next tick if enabled + char loaded
        if (name == _charName)
        {
            _charName = "";
            _route.SetCurrent(0);
            _lastSavedStep = _route.Current;
            _build = new BuildPlan();
            _buildIndex.Rebuild(_build);
        }
    }

    // drawn at top of DrawSettings, above the reflected node list
    private void DrawProfileSettings()
    {
        ImGui.TextColored(new Vector4(0.92f, 0.80f, 0.43f, 1f), "Profiles");
        ImGui.TextDisabled($"Active: {(string.IsNullOrEmpty(_charName) ? "(none)" : _charName)}");

        bool auto = Settings.AutoSwitchProfile.Value;
        if (ImGui.Checkbox("Auto-switch profile by character", ref auto))
            Settings.AutoSwitchProfile.Value = auto;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On: the profile follows the logged-in character (<Name> - <Class> - <League>).\n" +
                             "Off: pin a profile manually below; the game character is ignored.");

        Button(Settings.SyncToCharacter, "Sync tracker to character",
            "Jump the tracker to your character's real progress (quest flags + current area)");

        var profiles = ListProfiles();
        if (profiles.Length > 0)
        {
            // default selection to the active profile when nothing's picked yet
            if (_selectedProfileIdx < 0 || _selectedProfileIdx >= profiles.Length)
                _selectedProfileIdx = Math.Max(0, Array.IndexOf(profiles, _charName));

            ImGui.SetNextItemWidth(340);
            int sel = _selectedProfileIdx;
            if (ImGui.Combo("##ec_profiles", ref sel, profiles, profiles.Length))
                _selectedProfileIdx = sel;

            // loading while auto-switch is on would revert next tick, so disable it then
            ImGui.SameLine();
            ImGui.BeginDisabled(auto);
            if (ImGui.Button("Load") && sel >= 0 && sel < profiles.Length)
                SwitchProfile(profiles[sel]);
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Reset progress") && sel >= 0 && sel < profiles.Length)
                _confirmResetProfile = profiles[sel];

            ImGui.SameLine();
            if (ImGui.Button("Delete") && sel >= 0 && sel < profiles.Length)
            {
                DeleteProfile(profiles[sel]);
                if (_confirmResetProfile == profiles[sel]) _confirmResetProfile = null;
                _selectedProfileIdx = -1;
            }

            // reset step 2: explicit confirm before wiping progress
            if (_confirmResetProfile != null)
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f),
                    $"Reset '{_confirmResetProfile}' back to step 1? This can't be undone.");
                if (ImGui.Button("Yes, reset progress"))
                {
                    ResetProfileProgress(_confirmResetProfile);
                    _confirmResetProfile = null;
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) _confirmResetProfile = null;
            }

            // copy another profile's progress + build into the active one
            bool canCopy = !string.IsNullOrEmpty(_charName) && sel >= 0 && sel < profiles.Length && profiles[sel] != _charName;
            ImGui.BeginDisabled(!canCopy);
            if (ImGui.Button("Copy into current") && canCopy)
                _confirmCopyFrom = profiles[sel];
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.IsNullOrEmpty(_charName)
                    ? "Load a profile first - this copies the selected one into the active profile."
                    : "Copy the selected profile's route position and build into the active profile.");

            if (_confirmCopyFrom != null)
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f),
                    $"Copy '{_confirmCopyFrom}' into '{_charName}'? This overwrites the active profile's progress and build. Can't be undone.");
                if (ImGui.Button("Yes, copy"))
                {
                    CopyProfileInto(_confirmCopyFrom);
                    _confirmCopyFrom = null;
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##copyfrom")) _confirmCopyFrom = null;
            }
        }
        else
        {
            ImGui.TextDisabled("(no saved profiles yet)");
            _confirmResetProfile = null;
            _confirmCopyFrom = null;
        }

        ImGui.SetNextItemWidth(340);
        ImGui.InputTextWithHint("##ec_newprofile", "New profile name", ref _newProfileName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Create") && !string.IsNullOrWhiteSpace(_newProfileName))
        {
            CreateProfileManually(_newProfileName);
            _newProfileName = "";
            _selectedProfileIdx = -1;
        }

        // show the filesafe name we'd actually save when it differs from what was typed
        if (!string.IsNullOrWhiteSpace(_newProfileName))
        {
            var safe = SanitizeProfile(_newProfileName);
            if (safe != _newProfileName.Trim())
                ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f), $"Will be saved as: {safe}");
        }

        ImGui.Separator();
    }

    private bool _confirmResetRoute;   // route reset pending confirmation (two-step button)

    // export / import / reset for the route file, drawn under the Guide tab
    private void DrawRouteFileControls()
    {
        if (ImGui.Button("Export route..."))
            ExportRoute();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Save the current route to a JSON file you pick.");

        ImGui.SameLine();
        if (ImGui.Button("Import route..."))
            ImportRoute();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Load a route from a JSON file. Replaces your configured route.");

        // reset only makes sense with a user copy on disk; without one you're already on the default
        bool hasUserRoute = false;
        try { hasUserRoute = File.Exists(UserRoutePath); } catch { /* config dir unreadable */ }
        if (!hasUserRoute) { _confirmResetRoute = false; return; }

        ImGui.SameLine();
        if (ImGui.Button("Reset to default"))
            _confirmResetRoute = true;

        if (_confirmResetRoute)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f),
                "Reset the route to the plugin default? This is irreversible - export your route first if you want to keep it.");
            if (ImGui.Button("Yes, reset route"))
            {
                ResetRouteToDefault();
                _confirmResetRoute = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##resetroute")) _confirmResetRoute = false;
        }
    }

    // one-shot dialog offered the first time a profile is created (manual Create or auto-switch on a new
    // character). lets the user flip the global Show-league-start setting without digging through settings.
    private bool _leagueStartPromptPending;   // set in SwitchProfile on a brand-new profile; cleared on answer

    private void DrawLeagueStartPrompt()
    {
        const string popupId = "League Start mode?##ec_ls";

        if (_leagueStartPromptPending && !ImGui.IsPopupOpen(popupId))
            ImGui.OpenPopup(popupId);

        // centre on the viewport the first time it appears
        var ds = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new Vector2(ds.X * 0.5f, ds.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        // fixed width, height auto-fits content (0 = auto axis) so the box isn't oversized
        ImGui.SetNextWindowSize(new Vector2(380f, 0f), ImGuiCond.Appearing);

        if (!ImGui.BeginPopupModal(popupId))
            return;

        ImGui.TextWrapped("Enable League Start mode for this run?");
        ImGui.Spacing();
        ImGui.PushTextWrapPos(360f);
        ImGui.TextWrapped("It adds the league-start chores to the route - Trials of Ascendancy and Crafting " +
                          "Recipe pickups. On a later re-run you can turn them off under Show league-start steps.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("Enable", new Vector2(120f, 0f)))
        {
            Settings.ShowLeagueStart.Value = true;
            _leagueStartPromptPending = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Skip", new Vector2(120f, 0f)))
        {
            Settings.ShowLeagueStart.Value = false;
            _leagueStartPromptPending = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // reusable toast notifications: short transient messages stacked at a fixed anchor, each fading out near
    // the end of its life. call ShowToast(...) from anywhere; DrawToasts() runs every frame in Render.
    internal enum ToastLevel { Info, Success, Warning, Error }

    private sealed class Toast
    {
        public string Text = "";
        public ToastLevel Level;
        public DateTime ShownAt;
        public double Duration;
    }

    private readonly List<Toast> _toasts = new();
    private const int MaxToasts = 5;
    private const float ToastAccentW = 4f;   // coloured bar down the left edge

    private void ShowToast(string text, ToastLevel level = ToastLevel.Info, double? seconds = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var dur = seconds ?? Settings.Toasts.DurationSeconds.Value;
        _toasts.Add(new Toast { Text = text, Level = level, ShownAt = DateTime.Now, Duration = dur });
        if (_toasts.Count > MaxToasts) _toasts.RemoveRange(0, _toasts.Count - MaxToasts);
    }

    private static Color ToastAccent(ToastLevel l) => l switch
    {
        ToastLevel.Success => new Color(120, 210, 120, 255),
        ToastLevel.Warning => new Color(230, 180, 90, 255),
        ToastLevel.Error => new Color(230, 110, 100, 255),
        _ => new Color(150, 190, 240, 255),
    };

    private void DrawToasts()
    {
        var t = Settings.Toasts;
        if (!t.Enable) return;

        var now = DateTime.Now;
        _toasts.RemoveAll(x => (now - x.ShownAt).TotalSeconds >= x.Duration);
        bool preview = t.Preview.Value;
        // a sample handle box lets you position toasts without waiting for one; Alt only unlocks a live toast
        bool showSample = preview || Settings.AlertsMovable.Value;
        bool draggable = showSample || (AltHeld && _toasts.Count > 0);
        if (_toasts.Count == 0 && !showSample) return;

        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        if (baseSize <= 0) baseSize = 16f;
        float size = t.TextSize.Value;
        float scale = size / baseSize;
        float pad = t.Padding.Value;
        float lineH = (float)Math.Ceiling(size) + 4f;
        const float gap = 6f;

        // measure a box (wrapping to MaxWidth) -> (size, rows).
        (Vector2 size, List<string> rows) MeasureBox(string text)
        {
            var ascii = Ascii(text);
            float maxTextW = t.MaxWidth.Value > 0 ? Math.Max(10f, t.MaxWidth.Value - pad * 2 - ToastAccentW) : float.MaxValue;
            var rows = t.MaxWidth.Value > 0 && ImGui.CalcTextSize(ascii).X * scale > maxTextW
                ? WrapText(ascii, maxTextW, scale)
                : new List<string> { ascii };
            float contentW = 0f;
            foreach (var r in rows) contentW = Math.Max(contentW, ImGui.CalcTextSize(r).X * scale);
            return (new Vector2(contentW + pad * 2 + ToastAccentW, rows.Count * lineH + pad * 2), rows);
        }

        float anchorX = t.PosX.Value;   // horizontal centre of the stack
        float y = t.PosY.Value;          // top of the stack, grows downward

        // center-anchored move/resize when draggable (preview, alerts-movable toggle, or Alt-drag). the sample
        // box doubles as the drag handle; moving it updates PosX/PosY/MaxWidth for the real toasts too.
        if (draggable)
        {
            var (ssize, _) = MeasureBox("Sample toast (preview)");
            var min = new Vector2(t.PosX.Value - ssize.X / 2f, t.PosY.Value);
            var (hovered, onEdge, active) = HandleCenterInteract("toasts", ref min, ssize, t.PosX, t.PosY, t.MaxWidth, forceMovable: true);
            if (hovered || active) DrawClickBlocker("toasts", min, ssize);
            DrawDragHint(min, min + ssize, active, hovered, onEdge, _resizeId == "toasts");
            anchorX = t.PosX.Value;   // follow a move this frame
            y = t.PosY.Value;
        }

        // one box per toast; returns the y for the next one below it.
        float DrawBox(string text, ToastLevel level, float alpha, float top)
        {
            var (boxSize, rows) = MeasureBox(text);
            var min = new Vector2(anchorX - boxSize.X / 2f, top);
            var max = min + boxSize;

            if (OverlapsSidePanel(min, max)) return max.Y + gap;   // skip under a side panel, keep the stack flowing

            var bg = Fade(t.BackgroundColor.Value, alpha);
            if (bg.A > 0) dl.AddRectFilled(min, max, U32(bg));
            dl.AddRectFilled(min, new Vector2(min.X + ToastAccentW, max.Y), U32(Fade(ToastAccent(level), alpha)));
            if (t.BorderThickness.Value > 0)
                dl.AddRect(min, max, U32(Fade(t.BorderColor.Value, alpha)), 0f, ImDrawFlags.None, t.BorderThickness.Value);

            uint textCol = U32(Fade(t.TextColor.Value, alpha));
            var p = new Vector2(min.X + ToastAccentW + pad, min.Y + pad);
            foreach (var r in rows) { dl.AddText(font, size, p, textCol, r); p.Y += lineH; }

            return max.Y + gap;
        }

        foreach (var toast in _toasts)
        {
            const double fade = 0.5;
            var remaining = toast.Duration - (now - toast.ShownAt).TotalSeconds;
            float alpha = remaining < fade ? (float)Math.Clamp(remaining / fade, 0, 1) : 1f;
            y = DrawBox(toast.Text, toast.Level, alpha, y);
        }

        if (draggable && _toasts.Count == 0)
            DrawBox("Sample toast (preview)", ToastLevel.Info, 1f, y);
    }
}
