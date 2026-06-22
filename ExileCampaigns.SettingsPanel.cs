using System;
using System.Numerics;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using SharpDX;
using Vector4 = System.Numerics.Vector4;

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

        if (ImGui.BeginTabItem("Guide"))
        {
            Toggle("Auto-advance", Settings.AutoAdvance,
                "Advance the displayed step automatically when you enter the next zone");
            Toggle("Show optional steps", Settings.ShowOptional, "Include steps marked (Opt) from the route");
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Overlays"))
        {
            Toggle("Lock overlays", Settings.LockOverlays,
                "When off, drag any overlay with the left mouse button to reposition it. Turn on once placed so clicks pass through.");
            ImGui.SeparatorText("Route guide (steps)");
            DrawStepsStyle(Settings.Steps);
            ImGui.SeparatorText("Statistics Overlay");
            DrawOverlayStyle(Settings.CharStats, "charstats");
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Path & Indicator"))
        {
            ImGui.SeparatorText("Path to next step");
            var p = Settings.Path;
            Toggle("Show path on ground", p.ShowPathOnGround, "Draw a line on the terrain toward the objective (needs the Radar plugin)");
            Toggle("Show path on minimap", p.ShowPathOnMinimap, "Draw the path on the in-game large map");
            Toggle("Ground path only with map closed", p.ShowGroundPathOnlyWithClosedMap,
                "Hide the ground line while the large map is open");
            ColorEdit("Path color", p.PathColor);
            SliderFloat("Path thickness", p.PathThickness);
            SliderInt("Draw every Nth point", p.DrawEveryNthSegment, "Higher = sparser/faster");

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
            Toggle("Pulse current step##mmi", mi.PulseCurrent, "Animate the icons for the current objective so they stand out");
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Alerts"))
        {
            ImGui.SeparatorText("Auto-advance banner");
            var b = Settings.Banner;
            Toggle("Enabled##banner", b.Enable);
            Toggle("Preview##banner", b.Preview, "Keep the banner on screen with sample text so you can place it (needs overlays unlocked)");
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
            Button(Settings.SyncToCharacter, "Sync tracker to character",
                "Jump the tracker to your character's real progress (quest flags + current area)");
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
        ColorEdit("Header color##" + id, s.HeaderColor, "Colour of the ACT / INTERLUDES stage header");
        ColorEdit("Current-step color##" + id, s.CurrentColor, "Colour of the active step so it stands out");
        ColorEdit("Optional color##" + id, s.OptionalColor);
        ColorEdit("Background color##" + id, s.BackgroundColor, "Alpha controls opacity; alpha 0 = no panel background");
        ColorEdit("Border color##" + id, s.BorderColor);
        SliderFloat("Text size##" + id, s.TextSize, "Font height in pixels");
        SliderInt("Border thickness##" + id, s.BorderThickness, "0 = no border");
        SliderInt("Padding##" + id, s.Padding);
        SliderInt("Steps shown behind##" + id, s.StepsBehind, "How many completed steps to show above the current one");
        SliderInt("Steps shown ahead##" + id, s.StepsAhead, "How many upcoming steps to show below the current one");
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

    private static void SliderFloat(string label, RangeNode<float> n, string? tip = null)
    {
        float v = n.Value;
        if (ImGui.SliderFloat(label, ref v, n.Min, n.Max, "%.1f")) n.Value = v;
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

    private static void Text(string label, TextNode n, string? tip = null)
    {
        string v = n.Value ?? "";
        if (ImGui.InputText(label, ref v, 256)) n.Value = v;
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
}
