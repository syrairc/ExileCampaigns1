using SharpDX;
using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;

namespace ExileCampaigns;

// route-authoring overlays on the large minimap, off by default
[Submenu]
public class DevSettings
{
    [Menu("Show dev overlay", "Master switch: enable route-authoring overlays on the large minimap")]
    public ToggleNode ShowDevOverlay { get; set; } = new ToggleNode(false);

    [Menu("Room names", "Draw AreaGraph room outlines + name labels (shows tile patterns for ClusterTarget authored fallbacks)")]
    public ToggleNode ShowRoomNames { get; set; } = new ToggleNode(true);

    [Menu("Entity labels", "Label AreaTransition / Waypoint / boss entities with their shortened entity path")]
    public ToggleNode ShowEntityLabels { get; set; } = new ToggleNode(true);

    [Menu("Show quick edit panel", "Floating dev panel for in-game route editing: quick-add step/objective " +
        "seeded from live state, move/delete steps, bind advances, and set Radar paths on the current objective")]
    public ToggleNode ShowTriageButtons { get; set; } = new ToggleNode(false);
}

// placement of the floating triage panel. only drag-set values; no visible menu fields.
[Submenu]
public class TriageSettings
{
    [IgnoreMenu] public RangeNode<int> PosX { get; set; } = new RangeNode<int>(1500, 0, 4000);
    [IgnoreMenu] public RangeNode<int> PosY { get; set; } = new RangeNode<int>(300, 0, 2160);
}

// guided path to the current step's objective. Radar does the pathfinding; these just control the draw.
[Submenu]
public class PathRenderSettings
{
    [Menu("Show path on ground", "Draw a line on the terrain toward the current step's objective (needs the Radar plugin)")]
    public ToggleNode ShowPathOnGround { get; set; } = new ToggleNode(true);

    [Menu("Show path on minimap", "Draw the path on the in-game large map")]
    public ToggleNode ShowPathOnMinimap { get; set; } = new ToggleNode(true);

    [Menu("Ground path only with map closed", "Hide the ground line while the large map is open (the minimap path covers it then)")]
    public ToggleNode ShowGroundPathOnlyWithClosedMap { get; set; } = new ToggleNode(true);

    [Menu("Path color")]
    public ColorNode PathColor { get; set; } = new ColorNode(new Color(80, 200, 255, 255));

    [Menu("Path thickness")]
    public RangeNode<float> PathThickness { get; set; } = new RangeNode<float>(3f, 1f, 20f);

    [Menu("Draw every Nth point", "Thin the path by drawing only every Nth grid point (higher = sparser/faster)")]
    public RangeNode<int> DrawEveryNthSegment { get; set; } = new RangeNode<int>(2, 1, 10);
}

// golden arrow over the step's interact target, plus the interaction auto-advance thresholds
[Submenu]
public class InteractIndicatorSettings
{
    [Menu("Enabled", "Draw a golden pulsing arrow above the NPC/chest/boss the current step wants you to interact with")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Arrow color")]
    public ColorNode IconColor { get; set; } = new ColorNode(new Color(255, 200, 60, 255));

    [Menu("Arrow size", "Icon height in pixels")]
    public RangeNode<float> IconSize { get; set; } = new RangeNode<float>(70f, 8f, 400f);

    [Menu("Bob speed", "How fast the arrow bobs up and down (0 = steady)")]
    public RangeNode<float> BobSpeed { get; set; } = new RangeNode<float>(5f, 0f, 15f);

    [Menu("Bob distance", "How far the arrow travels up/down while bobbing, in pixels")]
    public RangeNode<float> BobDistance { get; set; } = new RangeNode<float>(20f, 0f, 100f);

    [Menu("Height offset", "Extra world units to lift the arrow above the entity's head")]
    public RangeNode<float> HeightOffset { get; set; } = new RangeNode<float>(100f, 0f, 600f);

    [Menu("Target search distance", "Only mark target entities within this distance of the player")]
    public RangeNode<float> MaxDistance { get; set; } = new RangeNode<float>(150f, 20f, 500f);

    [Menu("Proximity advance distance", "For bosses / quest objects, auto-advance when you get this close")]
    public RangeNode<float> NearDistance { get; set; } = new RangeNode<float>(40f, 5f, 200f);
}

// minimap icons drawn on the large map for the current area's authored MinimapIcons
[Submenu]
public class MinimapIconSettings
{
    [Menu("Enabled", "Draw authored minimap icons for the current area's steps on the large map")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Icon size", "Icon size in pixels on the large map")]
    public RangeNode<int> IconSize { get; set; } = new RangeNode<int>(36, 8, 128);

    [Menu("Pulse current step", "Animate the icons belonging to the current objective so they stand out")]
    public ToggleNode PulseCurrent { get; set; } = new ToggleNode(true);
}

// per-overlay placement + styling, one instance each so they move/style/toggle independently.
// drawn via the ImGui foreground draw list for per-call font size (Graphics.DrawText can't here).
[Submenu]
public class OverlayStyle
{
    // -- Enabled --
    [Menu("Enabled")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    // -- Colors --
    [Menu("Text color")] public ColorNode TextColor { get; set; } = new ColorNode(Color.White);
    [Menu("Header color")] public ColorNode HeaderColor { get; set; } = new ColorNode(new Color(235, 200, 110, 255));
    [Menu("Optional color")] public ColorNode OptionalColor { get; set; } = new ColorNode(new Color(150, 150, 150, 255));
    [Menu("Background color", "Alpha controls opacity; alpha 0 = no panel background")]
    public ColorNode BackgroundColor { get; set; } = new ColorNode(new Color(8, 8, 12, 140));
    [Menu("Border color")] public ColorNode BorderColor { get; set; } = new ColorNode(new Color(90, 90, 110, 180));

    // -- Sliders --
    [Menu("Text size", "Font height in pixels")]
    public RangeNode<float> TextSize { get; set; } = new RangeNode<float>(16f, 8f, 48f);
    [Menu("Border thickness", "0 = no border")] public RangeNode<int> BorderThickness { get; set; } = new RangeNode<int>(0, 0, 8);
    [Menu("Padding")] public RangeNode<int> Padding { get; set; } = new RangeNode<int>(6, 0, 40);

    // -- Hidden: set by drag-to-move / right-edge resize, persisted but not in the menu --
    [IgnoreMenu] public RangeNode<int> PosX { get; set; } = new RangeNode<int>(40, 0, 4000);
    [IgnoreMenu] public RangeNode<int> PosY { get; set; } = new RangeNode<int>(300, 0, 2160);
    [IgnoreMenu] public RangeNode<int> MaxWidth { get; set; } = new RangeNode<int>(0, 0, 2000);
}

// steps overlay: OverlayStyle plus prev/current/future window controls and a current-step colour.
// standalone (not inheriting) so the [Menu] reflection sees every field on one type.
[Submenu]
public class StepsOverlayStyle
{
    // -- Enabled --
    [Menu("Enabled")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    // -- Colors --
    [Menu("Text color")] public ColorNode TextColor { get; set; } = new ColorNode(Color.White);
    [Menu("Header color", "Colour of the ACT / INTERLUDES stage header")]
    public ColorNode HeaderColor { get; set; } = new ColorNode(new Color(235, 200, 110, 255));
    [Menu("Current-step color", "Colour of the active step so it stands out")]
    public ColorNode CurrentColor { get; set; } = new ColorNode(new Color(120, 220, 255, 255));
    [Menu("Optional color")] public ColorNode OptionalColor { get; set; } = new ColorNode(new Color(150, 150, 150, 255));
    [Menu("Background color", "Alpha controls opacity; alpha 0 = no panel background")]
    public ColorNode BackgroundColor { get; set; } = new ColorNode(new Color(8, 8, 12, 140));
    [Menu("Border color")] public ColorNode BorderColor { get; set; } = new ColorNode(new Color(90, 90, 110, 180));

    // -- Sliders --
    [Menu("Text size", "Font height in pixels")]
    public RangeNode<float> TextSize { get; set; } = new RangeNode<float>(16f, 8f, 48f);
    [Menu("Border thickness", "0 = no border")] public RangeNode<int> BorderThickness { get; set; } = new RangeNode<int>(0, 0, 8);
    [Menu("Padding")] public RangeNode<int> Padding { get; set; } = new RangeNode<int>(6, 0, 40);
    [Menu("Steps shown behind", "How many completed steps to show above the current one")]
    public RangeNode<int> StepsBehind { get; set; } = new RangeNode<int>(2, 0, 12);
    [Menu("Steps shown ahead", "How many upcoming steps to show below the current one")]
    public RangeNode<int> StepsAhead { get; set; } = new RangeNode<int>(3, 0, 12);

    // -- Hidden: set by drag-to-move / right-edge resize, persisted but not in the menu --
    [IgnoreMenu] public RangeNode<int> PosX { get; set; } = new RangeNode<int>(40, 0, 4000);
    [IgnoreMenu] public RangeNode<int> PosY { get; set; } = new RangeNode<int>(300, 0, 2160);
    [IgnoreMenu] public RangeNode<int> MaxWidth { get; set; } = new RangeNode<int>(0, 0, 2000);
}

// transient top-centre banner shown when the tracker auto-advances on a zone change
[Submenu]
public class BannerStyle
{
    // -- Enabled --
    [Menu("Enabled")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    // -- Checkboxes --
    [Menu("Preview", "Keep the banner on screen with sample text so you can position/resize it " +
        "(needs overlays unlocked to drag). Turn off when done.")]
    public ToggleNode Preview { get; set; } = new ToggleNode(false);

    // -- Colors --
    [Menu("Text color")] public ColorNode TextColor { get; set; } = new ColorNode(Color.White);
    [Menu("Background color", "Alpha controls opacity")]
    public ColorNode BackgroundColor { get; set; } = new ColorNode(new Color(8, 8, 12, 170));
    [Menu("Border color")] public ColorNode BorderColor { get; set; } = new ColorNode(new Color(120, 220, 255, 200));

    // -- Sliders --
    [Menu("Text size", "Font height in pixels")]
    public RangeNode<float> TextSize { get; set; } = new RangeNode<float>(28f, 8f, 96f);
    [Menu("Border thickness", "0 = no border")] public RangeNode<int> BorderThickness { get; set; } = new RangeNode<int>(2, 0, 8);
    [Menu("Padding")] public RangeNode<int> Padding { get; set; } = new RangeNode<int>(12, 0, 60);
    [Menu("Duration (seconds)", "How long the banner stays on screen; it fades out over the last 0.5s")]
    public RangeNode<float> DurationSeconds { get; set; } = new RangeNode<float>(4f, 0.5f, 15f);

    // -- Hidden: set by dragging in Preview mode, persisted but not in the menu --
    [IgnoreMenu] public RangeNode<int> PosX { get; set; } = new RangeNode<int>(960, 0, 4000);
    [IgnoreMenu] public RangeNode<int> PosY { get; set; } = new RangeNode<int>(80, 0, 2160);
    [IgnoreMenu] public RangeNode<int> MaxWidth { get; set; } = new RangeNode<int>(900, 0, 2000);
}

// small transient pop-up messages stacked at a centre anchor. reusable feedback channel (e.g. "no item
// hovered", "added to build"). drag to place via the Preview toggle (overlays must be unlocked).
[Submenu]
public class ToastSettings
{
    [Menu("Enabled")] public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Preview", "Draw a sample toast so you can position it. Turn off when done.")]
    public ToggleNode Preview { get; set; } = new ToggleNode(false);

    [Menu("Text color")] public ColorNode TextColor { get; set; } = new ColorNode(Color.White);
    [Menu("Background color", "Alpha controls opacity")]
    public ColorNode BackgroundColor { get; set; } = new ColorNode(new Color(8, 8, 12, 215));
    [Menu("Border color")] public ColorNode BorderColor { get; set; } = new ColorNode(new Color(120, 120, 160, 160));

    [Menu("Text size", "Font height in pixels")]
    public RangeNode<float> TextSize { get; set; } = new RangeNode<float>(20f, 8f, 72f);
    [Menu("Border thickness", "0 = no border")] public RangeNode<int> BorderThickness { get; set; } = new RangeNode<int>(1, 0, 8);
    [Menu("Padding")] public RangeNode<int> Padding { get; set; } = new RangeNode<int>(8, 0, 40);
    [Menu("Duration (seconds)", "How long each toast stays; it fades out over the last 0.5s")]
    public RangeNode<float> DurationSeconds { get; set; } = new RangeNode<float>(3f, 0.5f, 15f);

    // -- Hidden: set by dragging in Preview mode (centre X / top Y / wrap width), persisted but not in the menu --
    [IgnoreMenu] public RangeNode<int> PosX { get; set; } = new RangeNode<int>(960, 0, 4000);
    [IgnoreMenu] public RangeNode<int> PosY { get; set; } = new RangeNode<int>(880, 0, 2160);
    [IgnoreMenu] public RangeNode<int> MaxWidth { get; set; } = new RangeNode<int>(600, 0, 2000);
}

// in-overlay editor for per-step overrides: draggable/resizable panel, off by default.
[Submenu]
public class EditorSettings
{
    [Menu("Enabled", "Show the route override editor panel in-game (overlays must be unlocked to drag)")]
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    // first-use position of the controls window; ImGui owns drag/resize after that. persisted, not in menu.
    [IgnoreMenu] public RangeNode<int> PosX { get; set; } = new RangeNode<int>(40, 0, 4000);
    [IgnoreMenu] public RangeNode<int> PosY { get; set; } = new RangeNode<int>(500, 0, 2160);
}

// tester-facing diagnostics: opt-in rolling event recorder + a one-shot JSON export for bug reports.
[Submenu]
public class DiagnosticsSettings
{
    [Menu("Record events (for bug reports)", "Opt-in: continuously record recent zone changes, quest-flag " +
        "flips and path-target changes into an in-memory buffer so an export captures what led up to a bug. " +
        "Off = no recording overhead and the export's event list is empty (snapshot still works).")]
    public ToggleNode RecordDiagnostics { get; set; } = new ToggleNode(false);

    [Menu("Export diagnostics (hotkey)", "Write a diagnostic JSON report (recent events + current state) to " +
        "the 'diagnostics' folder under config, then open that folder")]
    public HotkeyNodeV2 ExportKey { get; set; } = new HotkeyNodeV2(System.Windows.Forms.Keys.None);

    [Menu("Export diagnostics now", "Write the diagnostic JSON report now and open its folder")]
    [JsonIgnore]
    public ButtonNode ExportNow { get; set; } = new ButtonNode();
}

public class ExileCampaignsSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Lock overlays", "When off, drag any overlay with the left mouse button to reposition it. " +
        "Turn on once placed so clicks pass through to the game.")]
    public ToggleNode LockOverlays { get; set; } = new ToggleNode(true);

    // auto-switch the active profile to the logged-in character. drawn manually atop the panel
    // by the profile manager; [IgnoreMenu] keeps it out of the reflected list.
    [IgnoreMenu] public ToggleNode AutoSwitchProfile { get; set; } = new ToggleNode(true);

    // ---- Overlays (each independently placed, styled, and toggled) ----
    [Menu("Route guide", "The campaign routing steps: act header, current area (top-right), previous/current/upcoming steps")]
    public StepsOverlayStyle Steps { get; set; } = new StepsOverlayStyle();

    [Menu("Statistics", "Run timer + per-act split, level, XP%, current area, level gap, route progress, xp/hour")]
    public OverlayStyle CharStats { get; set; } = new OverlayStyle { PosX = new RangeNode<int>(40, 0, 4000), PosY = new RangeNode<int>(600, 0, 2160) };

    [Menu("Auto-advance banner", "Large transient banner shown when the tracker auto-advances on a zone change")]
    public BannerStyle Banner { get; set; } = new BannerStyle();

    [Menu("Toasts", "Small transient pop-up messages (e.g. add-to-build feedback)")]
    public ToastSettings Toasts { get; set; } = new ToastSettings();

    [Menu("Route editor", "In-overlay panel for editing per-step overrides (drag to reposition, right-edge to resize)")]
    public EditorSettings Editor { get; set; } = new EditorSettings();

    // ---- Guide behaviour ----
    [Menu("Auto-advance", "Advance the displayed step automatically when you enter the next zone")]
    public ToggleNode AutoAdvance { get; set; } = new ToggleNode(true);

    [Menu("Show optional steps", "Include steps marked (Opt) from the route")]
    public ToggleNode ShowOptional { get; set; } = new ToggleNode(true);

    // ---- Path rendering (Radar-backed) ----
    [Menu("Path to next step", "Render a guided path to the current step's objective on the ground / minimap")]
    public PathRenderSettings Path { get; set; } = new PathRenderSettings();

    // ---- In-world interaction indicator ----
    [Menu("Interaction indicator", "Golden arrow over the entity the current step wants you to interact with")]
    public InteractIndicatorSettings InteractIndicator { get; set; } = new InteractIndicatorSettings();

    [Menu("Minimap icons", "Authored minimap icons drawn on the large map for the current area")]
    public MinimapIconSettings MinimapIcons { get; set; } = new MinimapIconSettings();

    // ---- Dev / route authoring ----
    [Menu("Dev overlay", "Minimap overlays for route authoring: room tile names, entity paths, step target marker")]
    public DevSettings Dev { get; set; } = new DevSettings();

    // placement of the floating triage panel (toggled by Dev.ShowTriageButtons). hidden: drag-set only.
    [IgnoreMenu] public TriageSettings Triage { get; set; } = new TriageSettings();

    [Menu("Diagnostics", "Record recent events and export a diagnostic report for bug reports")]
    public DiagnosticsSettings Diagnostics { get; set; } = new DiagnosticsSettings();

    // ---- Hotkeys ----
    [Menu("Sync to character", "Set the tracker to your character's real progress (from quest flags + current area)")]
    public HotkeyNodeV2 SyncKey { get; set; } = new HotkeyNodeV2(System.Windows.Forms.Keys.None);

    [Menu("Next step", "Manually advance to the next step (pauses auto-advance until the next zone change)")]
    public HotkeyNodeV2 NextStepKey { get; set; } = new HotkeyNodeV2(Keys.OemPeriod);

    [Menu("Previous step", "Manually go back one step")]
    public HotkeyNodeV2 PrevStepKey { get; set; } = new HotkeyNodeV2(Keys.Oemcomma);

    [Menu("Toggle overlay", "Show/hide the whole overlay")]
    public HotkeyNodeV2 ToggleKey { get; set; } = new HotkeyNodeV2(Keys.None);

    // ---- Dev ----
    [Menu("Log quest flags (dev)", "Harvesting tool: record each quest flag as it flips true, tagged with " +
        "the area + route step it happened on, to 'quest-flag-harvest.jsonl' in the config folder. Used to " +
        "build the flag->step map. Also writes a full flag dump to 'quest-flags-all.txt' on first enable.")]
    public ToggleNode LogQuestFlags { get; set; } = new ToggleNode(false);

    [Menu("Sync tracker to character", "Jump the tracker to your character's real progress (quest flags + current area)")]
    [JsonIgnore]
    public ButtonNode SyncToCharacter { get; set; } = new ButtonNode();

    [Menu("Reload routes", "Re-read route files from disk (bundled, or your override under the config folder)")]
    [JsonIgnore]
    public ButtonNode ReloadRoutes { get; set; } = new ButtonNode();
}
