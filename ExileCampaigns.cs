using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using ExileCampaigns.Build;
using ExileCampaigns.Guide;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Nodes;
using ImGuiNET;

namespace ExileCampaigns;

internal enum PanelRowKind { Text, Bar, Segmented, Axis, Pinned }

// one rendered row in an overlay panel. most rows are Text; the redesigned panels add drawn rows (bars,
// the XP-penalty axis, a highlighted "pinned" objective) built through the static factories below.
// Num (when set) draws in a fixed-width left column so the text column stays put as step numbers grow
// (9 -> 10). IsHeader lines (act/stage labels) skip the number column and render flush-left. Indent shifts
// the text column one step per level (support gems under their skill).
internal readonly struct PanelLine
{
    public readonly PanelRowKind Kind;
    public readonly string Text;
    public readonly Color Color;
    public readonly bool IsHeader;
    public readonly bool IsSeparator;   // area divider: Text is the bare area name, drawn centred in a dashed rule
    public readonly string? Num;
    public readonly string? Right;   // optional right-aligned text on the same row (e.g. area name on header)
    public readonly Action? OnCtrlClick;   // ctrl-click the row to fire this (build: mark have; route: re-pin)
    public readonly int Indent;

    // bar / segmented / axis payload
    public readonly float Fill01;
    public readonly float BarHeight;
    public readonly Color TrackColor;
    public readonly Color FillA;
    public readonly Color FillB;
    public readonly float[]? Cells;   // segmented: per-cell fill 0..1
    public readonly int CharLevel;    // axis
    public readonly int AreaLevel;    // axis

    // pinned payload
    public readonly string? SubText;
    public readonly Color Accent;

    public PanelLine(string text, Color color, bool isHeader = false, string? num = null,
        string? right = null, bool isSeparator = false, Action? onCtrlClick = null, int indent = 0)
    {
        Kind = PanelRowKind.Text;
        Text = text;
        Color = color;
        IsHeader = isHeader;
        IsSeparator = isSeparator;
        Num = num;
        Right = right;
        OnCtrlClick = onCtrlClick;
        Indent = indent;
        Fill01 = 0f; BarHeight = 0f; TrackColor = default; FillA = default; FillB = default;
        Cells = null; CharLevel = 0; AreaLevel = 0; SubText = null; Accent = default;
    }

    // full ctor for the drawn kinds; the factories below are the public surface.
    private PanelLine(PanelRowKind kind, string text, Color color, string? num, int indent, float fill,
        float height, Color track, Color fillA, Color fillB, float[]? cells, int charLevel, int areaLevel,
        string? subText, Color accent, Action? onCtrlClick)
    {
        Kind = kind; Text = text; Color = color; IsHeader = false; IsSeparator = false;
        Num = num; Right = null; OnCtrlClick = onCtrlClick; Indent = indent;
        Fill01 = fill; BarHeight = height; TrackColor = track; FillA = fillA; FillB = fillB;
        Cells = cells; CharLevel = charLevel; AreaLevel = areaLevel; SubText = subText; Accent = accent;
    }

    // full-width track + gradient fill (flat when fillA==fillB). XP bar, per-act bar.
    public static PanelLine Bar(float fill01, float height, Color track, Color fillA, Color fillB) =>
        new(PanelRowKind.Bar, "", default, null, 0, Math.Clamp(fill01, 0f, 1f), height, track, fillA, fillB,
            null, 0, 0, null, default, null);

    // N cells with 2px gaps, one fill each. the 10-act campaign bar.
    public static PanelLine Segmented(float[] cells, float height, Color track, Color fill) =>
        new(PanelRowKind.Segmented, "", default, null, 0, 0f, height, track, fill, fill, cells, 0, 0, null,
            default, null);

    // XP-penalty safe-zone axis. draws its own flanks/safe-band/marker from the two levels.
    public static PanelLine Axis(int charLevel, int areaLevel, float height) =>
        new(PanelRowKind.Axis, "", default, null, 0, 0f, height, default, default, default, null, charLevel,
            areaLevel, null, default, null);

    // highlighted current-objective row: bg box + left accent bar + a dim sub-line beneath.
    public static PanelLine Pinned(string? num, string text, Color color, string? subText, Color accent) =>
        new(PanelRowKind.Pinned, text, color, num, 0, 0f, 0f, default, default, default, null, 0, 0, subText,
            accent, null);
}

// campaign-leveling overlay for PoE1. reads act/zone/level/quest from live memory (no screen-reading)
// and drives a routing guide. this file is plugin entry + lifecycle; features live under Guide/ and Tracking/.
public partial class ExileCampaigns : BaseSettingsPlugin<ExileCampaignsSettings>
{
    private readonly RouteRepository _route = new();
    private RouteStore? _routeStore;

    // the editor's selected step (RouteStep.Id), independent of the tracker's Current. set by the editor tree,
    // the triage quick-add, and the reports-tab deep-link; survives a store reload.
    private string? _editorSelectedId;

    // current-area snapshot, refreshed on AreaChange (seeded in Initialise for mid-area loads).
    private int _act;
    private string _zoneName = "";
    private string _areaId = "";       // lowercased WorldArea.Id, the route join key
    private bool _isTown;
    private bool _isHideout;
    private int _areaLevel;   // RealLevel of the current area, for the level-vs-area stat

    private int _playerLevel;
    private bool _visible = true;

    private BuildPlan _build = new();

    // auto-advance banner: new step's text and when it was shown.
    private string? _bannerText;
    private DateTime _bannerShownAt;

    // drag-to-move: which overlay (by id) is dragged, and grab offset from its origin.
    private string? _dragId;
    private Vector2 _dragOffset;
    // resize: which overlay's right edge is dragged to set wrap width.
    private string? _resizeId;

    public override bool Initialise()
    {
        Name = "Exile Campaigns";   // menu display name, independent of the plugin folder

        foreach (var key in new[] { Settings.NextStepKey, Settings.PrevStepKey, Settings.ToggleKey, Settings.SyncKey, Settings.AddBuildItemKey })
            Input.RegisterKey(key.Value);
        Settings.NextStepKey.OnValueChanged += () => Input.RegisterKey(Settings.NextStepKey.Value);
        Settings.PrevStepKey.OnValueChanged += () => Input.RegisterKey(Settings.PrevStepKey.Value);
        Settings.ToggleKey.OnValueChanged += () => Input.RegisterKey(Settings.ToggleKey.Value);
        Settings.SyncKey.OnValueChanged += () => Input.RegisterKey(Settings.SyncKey.Value);
        Settings.AddBuildItemKey.OnValueChanged += () => Input.RegisterKey(Settings.AddBuildItemKey.Value);
        Input.RegisterKey(Settings.Diagnostics.ExportKey.Value);
        Settings.Diagnostics.ExportKey.OnValueChanged += () => Input.RegisterKey(Settings.Diagnostics.ExportKey.Value);

        Settings.SyncToCharacter.OnPressed += SyncToCharacter;
        Settings.ReloadRoutes.OnPressed += LoadRoutes;
        Settings.Diagnostics.ExportNow.OnPressed += ExportDiagnostics;
        Settings.LogQuestFlags.OnValueChanged += (_, _) => _flagSnapshot = null;   // re-seed on next enable
        Settings.Diagnostics.RecordDiagnostics.OnValueChanged += (_, _) => _diagFlagSnapshot = null;   // re-seed so toggling on doesn't dump a stale flag burst

        LoadRoutes();
        LoadProgress();
        LoadXpCurve();

        // seed from current area so the overlay works when loaded mid-zone.
        try
        {
            if (GameController?.Area?.CurrentArea is { } cur)
            {
                CaptureArea(cur);
                _route.IncludeOptional = Settings.ShowOptional;
                _route.IncludeLeagueStart = Settings.ShowLeagueStart;
                if (Settings.AutoAdvance) _route.OnAreaChanged(_areaId);
                OnAreaChangedPathing();
            }
        }
        catch { /* not in an area yet */ }

        InitStats();
        InitIndicatorTexture();
        InitCometTexture();
        RegisterGuidanceBridge();
        EnsureGuidanceProviders();   // initial detection; Tick re-probes on a throttle
        return true;
    }

    // load routes from route.json (user copy under ConfigDirectory\route wins, else bundled under Data).
    private void LoadRoutes()
    {
        var doc = LoadRouteDocument();
        _routeStore = new RouteStore(doc);
        _route.LoadFromDocument(_routeStore.ToDocument());

        if (_route.Steps.Count == 0)
            LogMessage($"ExileCampaigns -> no steps loaded ({_route.Status}).");
        else
            LogMessage($"ExileCampaigns -> routes loaded: {_route.Status}.");

        // RebuildFlagIndex removed in Task 5; native advance via EvaluateAdvance handles flags now.
    }

    // rebuild the read-side route from the (already-authoritative) edit store after an in-game edit. keeps the
    // tracker on its current step (clamped) and points the editor selection at selectId if it still exists.
    // data-only: no disk re-read, the store is the source.
    private void ReloadRouteFromStore(string? selectId)
    {
        if (_routeStore == null) return;
        int prevCurrent = _route.Current;
        _route.LoadFromDocument(_routeStore.ToDocument());
        _mmiCacheKey = null;
        _lastStepForTarget = -1;   // editing the current step keeps its index, force the path to re-resolve
        if (_route.Steps.Count > 0)
            _route.SetCurrent(Math.Min(prevCurrent, _route.Steps.Count - 1));
        if (selectId != null && _route.Steps.Any(f => f.Model?.Id == selectId))
            _editorSelectedId = selectId;
    }

    public override void AreaChange(AreaInstance area)
    {
        try
        {
            CaptureArea(area);
            _mmiCacheKey = null;
            _holdAutoAdvanceUntilZone = false;   // a real zone change resumes auto-advance
            RecordDiagArea();
            OnActChangedStats(_act);
            var before = _route.Current;
            _route.IncludeOptional = Settings.ShowOptional;   // sync nav-skip before the area-snap advance
            _route.IncludeLeagueStart = Settings.ShowLeagueStart;
            if (Settings.AutoAdvance)
            {
                _route.OnAreaChanged(_areaId);
                // EnterArea objectives are now handled by EvaluateAdvance; zone forward-snap stays.
            }
            // banner only when auto-advance actually moved to a new step.
            if (Settings.AutoAdvance && Settings.Banner.Enable && _route.Current != before && _route.CurrentStep != null)
            {
                _bannerText = _route.CurrentStep.DisplayText;
                _bannerShownAt = DateTime.Now;
            }
            OnAreaChangedPathing();
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> AreaChange failed: {ex.Message}"); }
    }

    private void CaptureArea(AreaInstance area)
    {
        _act = area.Act;
        _zoneName = area.Name ?? "";
        _areaLevel = area.RealLevel;
        _areaId = (area.Area?.Id ?? "").ToLowerInvariant();
        _isTown = area.IsTown;
        _isHideout = area.IsHideout;
    }

    private DateTime _lastAdvanceEval = DateTime.MinValue;

    // set when the user manually moves the cursor backward. holds off auto-advance so a deliberate
    // back-step onto already-completed content sticks; cleared on the next real zone change, OR when the held
    // step becomes freshly complete while you're on it (a quest flag flips) - see AdvanceHold.
    private bool _holdAutoAdvanceUntilZone;
    private string? _heldStepId;        // step the hold was seeded against (re-seeds when it changes)
    private bool _heldSeedComplete;     // was that step already complete when the hold began

    // single advance gate: the current step's objectives, evaluated against live state. replaces
    // AutoAdvanceFromQuestFlags + MaybeAdvanceOnInteraction + MaybeAdvanceOnObjectiveComplete.
    private void EvaluateAdvance()
    {
        if (!Settings.AutoAdvance) return;
        if ((DateTime.UtcNow - _lastAdvanceEval).TotalSeconds < 0.4) return;
        _lastAdvanceEval = DateTime.UtcNow;

        var model = _route.CurrentStep?.Model;
        if (model == null) return;
        UpdateProgressTracker(model);

        var complete = Guide.AdvanceEngine.IsStepComplete(model, new WorldState(this));
        // the hold gates advance after a manual backtrack, but lets a fresh completion edge (quest flag flip)
        // through so real progress still advances. all the branch logic lives in the pure AdvanceHold helper.
        var d = Guide.AdvanceHold.Evaluate(
            new AdvanceHold.State(_holdAutoAdvanceUntilZone, _heldStepId, _heldSeedComplete),
            model.Id, complete);
        _holdAutoAdvanceUntilZone = d.Next.Held;
        _heldStepId = d.Next.SeedStepId;
        _heldSeedComplete = d.Next.SeedComplete;

        if (d.Advance)
        {
            _route.Next();
            if (Settings.Banner.Enable && _route.CurrentStep != null)
            {
                _bannerText = _route.CurrentStep.DisplayText;
                _bannerShownAt = DateTime.Now;
            }
        }
    }

    public override Job Tick()
    {
        // keep nav-skip in sync with the overlay's optional + league-start visibility before any advance (key or auto).
        _route.IncludeOptional = Settings.ShowOptional;
        _route.IncludeLeagueStart = Settings.ShowLeagueStart;

        if (Settings.ToggleKey.PressedOnce()) _visible = !_visible;
        if (Settings.SyncKey.PressedOnce()) SyncToCharacter();
        if (Settings.NextStepKey.PressedOnce()) _route.Next();
        if (Settings.PrevStepKey.PressedOnce()) { _route.Prev(); _holdAutoAdvanceUntilZone = true; }
        if (Settings.Diagnostics.ExportKey.PressedOnce()) ExportDiagnostics();
        if (Settings.AddBuildItemKey.PressedOnce()) OnAddBuildItemPressed();

        DrainPendingRouteFile();   // pick up a finished export/import file dialog (runs off the render thread)

        try
        {
            var pc = GameController?.Player?.GetComponent<Player>();

            // build active profile id "<Character> - <Class> - <League>" from live memory and switch on
            // change (auto-creates on first sight). name/class/league populate together in-game; Character
            // panel backstops the name. skippable so users can pin a profile in settings.
            if (Settings.AutoSwitchProfile)
            {
                var charName = pc?.PlayerName;
                if (string.IsNullOrEmpty(charName)) charName = TryReadCharNameFromPanel();
                if (!string.IsNullOrEmpty(charName))
                {
                    // class = entity RenderName (e.g. "Mercenary"); ServerPlayerData.Class is a stale PoE1
                    // enum, don't use it. league = ServerData.League (entity .League is unrelated).
                    var cls = GameController?.Player?.RenderName;
                    var league = GameController?.IngameState?.ServerData?.League;
                    var profile = ComposeProfileId(charName, cls, league);
                    if (profile != _charName) SwitchProfile(profile);
                }
            }

            var level = pc?.Level ?? 0;
            if (level <= 0) level = GameController?.IngameState?.ServerData?.CharacterLevel ?? 0;
            _playerLevel = level;
            _playerXp = pc?.XP ?? _playerXp;
        }
        catch { /* server data not ready */ }

        RecordXpSample();

        // harvest when logging, or just maintain the in-memory flag rings when the triage panel is open
        if (Settings.LogQuestFlags || Settings.Dev.ShowTriageButtons) HarvestQuestFlags();
        RecordDiagFlagChanges();

        EnsureGuidanceProviders();
        _targetResolver.CacheLocations = Settings.GuidanceProvider.RememberTargetLocations.Value;
        _guidanceMode = ActiveMode();
        _clusterTarget = ActiveClusterTarget(_guidanceMode);
        if (_guidanceMode == GuidanceMode.InGame)
            UpdatePathTarget();
        else if (_currentTarget != null || _currentPath != null || _objectiveTargets != null || _tileCandidateMode)
        {
            // leaving in-game mode: drop the running routes AND clear the "already routing" guard so a later
            // return to in-game re-resolves cleanly. guarded so steady-state non-in-game ticks don't churn CTSes.
            CancelPath();
            CancelObjectivePaths();
            _currentTarget = null;
            _tileCandidateMode = false;
            _lastStepForTarget = -1;
        }
        if (_guidanceMode == GuidanceMode.Panel)
            BuildExportSnapshot();
        else { _exportPathTargets = null; _exportIcons = null; }
        UpdateWaypointPulse();
        UpdateLoginPulse();
        UpdateInteractTarget();
        // runs after profile switch resolves above, so a swap window never scans the new char against the old build
        DetectBuildUsed();
        // safety net for a dirty notes edit that never got its deactivated-after-edit frame (tab switch, header collapse click)
        if (_notesDirty && (DateTime.UtcNow - _notesDirtySince).TotalSeconds > 2) FlushNotes();
        EvaluateAdvance();

        MaybeSaveProgress();
        return null!;
    }

    // profile id shown to the user + used as the progress filename: "<Character> - <Class> - <League>",
    // skipping any part not yet available (in practice all three resolve together in-game).
    private static string ComposeProfileId(string name, string? cls, string? league)
    {
        var parts = new List<string> { name.Trim() };
        if (!string.IsNullOrWhiteSpace(cls)) parts.Add(cls.Trim());
        if (!string.IsNullOrWhiteSpace(league)) parts.Add(league.Trim());
        return string.Join(" - ", parts);
    }

    // fallback character-name source: Character panel (OpenLeftPanel) name label at child path 33->2->2.
    // only populated while the panel is open, so it just backstops PlayerName.
    private string? TryReadCharNameFromPanel()
    {
        try
        {
            var left = GameController?.IngameState?.IngameUi?.OpenLeftPanel;
            if (left is not { IsVisible: true }) return null;
            var txt = left.GetChildFromIndices(new[] { 33, 2, 2 })?.Text;
            return string.IsNullOrWhiteSpace(txt) ? null : txt.Trim();
        }
        catch { return null; }
    }

    public override void Render()
    {
        if (!Settings.Enable || GameController is not { InGame: true })
            return;

        var ui = GameController.IngameState.IngameUi;
        // MTX shop or any full-screen panel (atlas, world map, character/skills fullscreen, etc.) hides
        // everything we draw - gear dialog, toasts, overlays, triage. MTX shop isn't in FullscreenPanels.
        if (ui.MicrotransactionShopWindow.IsVisible || ui.FullscreenPanels.Any(p => p.IsVisible))
            return;

        DrawToasts();       // transient messages, shown regardless of overlay toggle
        DrawLeagueStartPrompt();   // new-profile dialog, shown even when the overlay is toggled off
        DrawBuildDialog();  // add-by-name popup lives in settings, needs to draw even when overlay is off

        if (!_visible)
            return;

        // cache the open panel rects once per frame; every overlay hides (or slides clear) where it overlaps one.
        _leftPanelRect = ui.OpenLeftPanel.IsVisible ? SafeClientRect(ui.OpenLeftPanel) : null;
        _rightPanelRect = ui.OpenRightPanel.IsVisible ? SafeClientRect(ui.OpenRightPanel) : null;
        _leftPanelRect = UnionStashTabList(ui, _leftPanelRect);
        CacheLargePanelRects(ui);

        DrawStepPath();
        DrawInteractIndicators();
        DrawDevOverlay();
        DrawWaypointNodeIds();
        DrawWaypointDestination();
        DrawMinimapIcons();
        DrawBanner();
        DrawBuildIndicators();

        if (Settings.Steps.Enable)
        {
            var s = Settings.Steps;
            DrawOverlay("steps", BuildStepsLines(), s.PosX, s.PosY, s.TextSize.Value, s.MaxWidth,
                s.Padding.Value, s.BackgroundColor.Value, s.BorderColor.Value, s.BorderThickness.Value);
        }

        if (Settings.CharStats.Enable)
        {
            var cs = Settings.CharStats;
            DrawOverlay("char", BuildCharStatsLines(cs), cs.PosX, cs.PosY, cs.TextSize.Value, cs.MaxWidth,
                cs.Padding.Value, cs.BackgroundColor.Value, cs.BorderColor.Value, cs.BorderThickness.Value);
        }

        if (Settings.BuildPanel.Enable)
            DrawOverlay("build", BuildPanelLines(Settings.BuildPanel), Settings.BuildPanel);

        DrawRouteEditor();
        DrawTriageOverlay();
    }

    // stage label for the steps tracker header. poe1 is acts 1-10, no interludes.
    private static string StageLabel(int act) => $"ACT {act}";

    private List<PanelLine> BuildStepsLines()
    {
        var s = Settings.Steps;
        var lines = new List<PanelLine>();

        // no profile active: auto-switch can't read the character yet (prompt to open panel), or
        // auto-switch is off and the user hasn't picked one (prompt to choose in settings).
        if (string.IsNullOrEmpty(_charName))
        {
            lines.Add(new PanelLine("No character profile loaded", s.HeaderColor.Value, isHeader: true));
            lines.Add(new PanelLine(Settings.AutoSwitchProfile
                ? "Press C to open the Character panel so your profile can load."
                : "Auto-switch is off - pick or create a profile in settings.", s.TextColor.Value));
            return lines;
        }

        // objective zone, shown right-aligned on the first (top) act header. this is where the current
        // step sends you, not necessarily where you stand. fall back to the live zone when no route name.
        var place = _isHideout ? "Hideout" : _isTown ? "Town" : $"Act {_act}";
        var objectiveZone = _route.CurrentAreaName;
        var areaName = !string.IsNullOrEmpty(objectiveZone) ? objectiveZone
                     : string.IsNullOrEmpty(_zoneName) ? place : _zoneName;

        var current = _route.CurrentStep;
        if (current == null)
        {
            var zone = string.IsNullOrEmpty(_zoneName) ? "(no route loaded)" : _zoneName;
            lines.Add(new PanelLine(zone, s.TextColor.Value));
            return lines;
        }

        var tc = s.TextColor.Value;
        var hc = s.HeaderColor.Value;
        var dim = new Color(tc.R, tc.G, tc.B, (byte)170);
        var sepColor = new Color(hc.R, hc.G, hc.B, (byte)190);
        int lastAct = -1;
        string? lastArea = null;
        bool firstHeader = true;

        // segmented campaign bar + per-act bar, emitted once under the first act header (redesigned 3b).
        // fill maths per the design handoff: acts 1..10, current act partial, later acts empty.
        void EmitProgressBars()
        {
            int act = current!.Act;
            int total = _route.StepsInAct(act);
            float actFrac = total > 0 ? Math.Clamp(current.StepInAct / (float)total, 0f, 1f) : 0f;

            if (s.ShowCampaignBar.Value)
            {
                const int totalActs = 10;
                var cells = new float[totalActs];
                for (int i = 0; i < totalActs; i++)
                    cells[i] = i < act - 1 ? 1f : i == act - 1 ? actFrac : 0f;
                float campaignPct = ((act - 1) + actFrac) / totalActs * 100f;
                lines.Add(new PanelLine("CAMPAIGN", dim, right: $"{campaignPct:0}%"));
                lines.Add(PanelLine.Segmented(cells, 6f, BarTrack, AccentBright));
            }
            if (s.ShowActBar.Value)
            {
                lines.Add(new PanelLine($"Act {act}  {current.StepInAct}/{total}", dim, right: $"{actFrac * 100f:0}%"));
                lines.Add(PanelLine.Bar(actFrac, 11f, BarTrack, AccentResting, AccentBright));
            }
        }

        // emit an act header when the window starts or crosses into a new act, an area divider when the
        // zone changes, then the step itself. the current step draws as a highlighted pinned row.
        void AddRow(FlatStep st, Color col, bool isCurrent)
        {
            if (st.Act != lastAct)
            {
                bool wasFirst = firstHeader;
                lines.Add(new PanelLine(StageLabel(st.Act), s.HeaderColor.Value, isHeader: true,
                    right: wasFirst ? areaName : null));
                firstHeader = false;
                lastAct = st.Act;
                lastArea = null;   // re-print the divider for the first area under the new act
                if (wasFirst) EmitProgressBars();
            }
            // every windowed row is a real step (headers are skipped), so Model carries its zone.
            var area = st.Model?.AreaName;
            var areaKey = st.Model?.AreaId ?? area;
            if (!string.IsNullOrEmpty(area) && !string.Equals(areaKey, lastArea, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(new PanelLine(area!, sepColor, isSeparator: true));
                lastArea = areaKey;
            }
            if (isCurrent)
            {
                var zone = st.Model?.AreaName ?? areaName;
                lines.Add(PanelLine.Pinned(st.StepInAct.ToString(), "> " + st.DisplayText, col, zone, col));
            }
            else lines.Add(new PanelLine(st.DisplayText, col, num: st.StepInAct.ToString(),
                onCtrlClick: RePinAction(st)));
        }

        // league-start steps win the colour over optional; both yield to the current-step highlight.
        Color RowColor(FlatStep st, Color baseCol) =>
            (st.Model?.LeagueStart ?? false) ? s.LeagueStartColor.Value
            : (st.Model?.Optional ?? false) ? s.OptionalColor.Value
            : baseCol;

        foreach (var st in _route.Previous(s.StepsBehind.Value, Settings.ShowOptional, Settings.ShowLeagueStart))
            AddRow(st, RowColor(st, dim), false);

        AddRow(current, s.CurrentColor.Value, true);

        foreach (var st in _route.Upcoming(s.StepsAhead.Value, Settings.ShowOptional, Settings.ShowLeagueStart))
            AddRow(st, RowColor(st, s.TextColor.Value), false);

        return lines;
    }

    // ctrl-click a listed step to make it the current objective. reuses MoveProgressToStep so a backward
    // re-pin holds until real progress. null when the step isn't a real (non-header) step.
    private Action? RePinAction(FlatStep st)
    {
        if (st.Model == null) return null;
        var id = st.Model.Id;
        return () => MoveProgressToStep(id);
    }

    // overload that pulls placement/style straight from an OverlayStyle.
    // open side-panel rects, refreshed each frame in Render. null = that panel closed.
    private RectangleF? _leftPanelRect;
    private RectangleF? _rightPanelRect;

    private static RectangleF? SafeClientRect(ExileCore.PoEMemory.Element e)
    {
        try { var r = e.GetClientRect(); return r.Width > 1 && r.Height > 1 ? r : (RectangleF?)null; }
        catch { return null; }
    }

    // true when the screen rect overlaps an open side panel, so the caller can skip drawing under it.
    private bool OverlapsSidePanel(RectangleF r) =>
        (_leftPanelRect is { } l && l.Intersects(r)) || (_rightPanelRect is { } rp && rp.Intersects(r));

    private bool OverlapsSidePanel(Vector2 min, Vector2 max) =>
        (_leftPanelRect != null || _rightPanelRect != null) &&
        OverlapsSidePanel(new RectangleF(min.X, min.Y, max.X - min.X, max.Y - min.Y));

    // the stash tab list hangs off the right edge of the stash window and isn't inside its client rect,
    // so offsetting past the stash alone still lands under it. widen the left rect to cover both.
    private static RectangleF? UnionStashTabList(ExileCore.PoEMemory.MemoryObjects.IngameUIElements ui, RectangleF? left)
    {
        if (left is not { } l) return left;
        try
        {
            var list = ui.StashElement?.ViewAllStashPanel;
            if (list is { IsVisible: true } && SafeClientRect(list) is { } r) return RectangleF.Union(l, r);
        }
        catch { }
        return left;
    }

    // open large windows (stash, vendor, crafting, ritual, ...). refreshed each frame in Render.
    private readonly List<RectangleF> _largePanelRects = new();

    private void CacheLargePanelRects(ExileCore.PoEMemory.MemoryObjects.IngameUIElements ui)
    {
        _largePanelRects.Clear();
        try
        {
            foreach (var p in ui.LargePanels)
                if (p is { IsVisible: true } && SafeClientRect(p) is { } r) _largePanelRects.Add(r);
        }
        catch { }
    }

    private bool OverlapsLargePanel(RectangleF r)
    {
        foreach (var p in _largePanelRects)
            if (p.Intersects(r)) return true;
        return false;
    }

    // x offset that slides a rect clear of the side panel it overlaps: right past a left panel, left past a
    // right panel. 0 when it can't clear - caller hides instead.
    private float SidePanelShift(RectangleF r)
    {
        float screenW = GameController.Window.GetWindowRectangle().Width;
        float dx = 0f;
        if (_leftPanelRect is { } l && l.Intersects(r))
        {
            dx = l.Right - r.Left;
            if (r.Right + dx > screenW) return 0f;
        }
        else if (_rightPanelRect is { } rp && rp.Intersects(r))
        {
            dx = rp.Left - r.Right;
            if (r.Left + dx < 0f) return 0f;
        }
        // sliding out from under one panel can land us under the other
        return dx != 0f && OverlapsSidePanel(new RectangleF(r.X + dx, r.Y, r.Width, r.Height)) ? 0f : dx;
    }

    // point-test variant for per-vertex world draws (path segments, comets).
    private bool PointInSidePanel(Vector2 p) =>
        (_leftPanelRect is { } l && l.Contains(p.X, p.Y)) || (_rightPanelRect is { } rp && rp.Contains(p.X, p.Y));

    private void DrawOverlay(string id, IReadOnlyList<PanelLine> lines, OverlayStyle s) =>
        DrawOverlay(id, lines, s.PosX, s.PosY, s.TextSize.Value, s.MaxWidth, s.Padding.Value,
            s.BackgroundColor.Value, s.BorderColor.Value, s.BorderThickness.Value);

    // redesigned-panel signal colours (from the design handoff; hardcoded like the XP severity colours).
    private static readonly Color BarTrack       = new Color(20, 26, 41, 255);    // #141A29
    private static readonly Color BarTrackBorder = new Color(43, 53, 80, 255);    // #2B3550
    private static readonly Color AccentBright   = new Color(77, 166, 255, 255);  // #4DA6FF
    private static readonly Color AccentResting  = new Color(41, 97, 179, 255);   // #2961B3
    private static readonly Color PinnedBg       = new Color(66, 150, 250, 31);   // rgba(66,150,250,0.12)
    private static readonly Color AxisSafeBand   = new Color(111, 207, 122, 51);  // rgba(111,207,122,0.20)
    private static readonly Color AxisSafeEdge   = new Color(111, 207, 122, 255); // #6FCF7A
    private static readonly Color AxisMarker     = new Color(0, 204, 255, 255);   // #00CCFF
    private static readonly Color AxisFlankRed   = new Color(224, 103, 103, 140); // rgba(224,103,103,0.55)
    private static readonly Color AxisFlankAmber = new Color(224, 177, 90, 82);   // rgba(224,177,90,0.32)
    private static readonly Color AxisFlankGrey  = new Color(60, 72, 96, 56);     // rgba(60,72,96,0.22)
    private static readonly Color RowMuted       = new Color(115, 128, 153, 255); // #738099 (pinned sub-line)

    // Alt held anywhere: lets you grab a locked overlay to reposition it (an override, not a lock replacement).
    private static bool AltHeld =>
        Input.GetKeyState(System.Windows.Forms.Keys.LMenu) || Input.GetKeyState(System.Windows.Forms.Keys.RMenu);
    private bool OverlaysMovable => !Settings.LockOverlays.Value || AltHeld;

    // draw a panel of lines via the ImGui foreground draw list. unlike Graphics.DrawText this honours a
    // per-call font size (textSize), so each overlay gets its own scale/border/fill. unlock overlays (or hold
    // Alt) to drag the body to move or the right edge to set wrap width; writes back to posX/posY/maxWidth.
    // hides when its rect would overlap an open side panel (see OverlapsSidePanel).
    private void DrawOverlay(string id, IReadOnlyList<PanelLine> lines, RangeNode<int> posX, RangeNode<int> posY,
        float textSize, RangeNode<int> maxWidth, int padding, Color background, Color borderColor, int borderThickness)
    {
        if (lines.Count == 0) return;

        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        if (baseSize <= 0) baseSize = 16f;
        float scale = textSize / baseSize;
        float pad = padding;
        float lineH = (float)Math.Ceiling(textSize) + 2f;
        float indentW = textSize * 1.25f;   // one support-gem indent step (~20px at 16px font)
        const int sepMinDashes = 3;         // shortest dash run each side of an area-divider label

        Vector2 Measure(string t) => ImGui.CalcTextSize(t) * scale;
        string SepLabel(string name) => " " + name + " ";
        float dashW = Measure("-").X;

        // fixed-width number column so the text column never shifts as step numbers grow.
        float numCol = 0f;
        bool hasNum = false;
        foreach (var l in lines)
            if (l.Num != null) { hasNum = true; numCol = Math.Max(numCol, Measure(l.Num).X); }
        float gap = hasNum ? textSize * 0.4f : 0f;
        float textX = numCol + gap;

        // flatten lines into draw rows, wrapping over-wide plain text. drawn kinds pass through untouched.
        var rows = new List<PanelLine>();
        foreach (var l in lines)
        {
            bool wrappable = l.Kind == PanelRowKind.Text && !l.IsHeader && !l.IsSeparator && l.Right == null;
            float avail = maxWidth.Value > 0 ? Math.Max(10f, maxWidth.Value - pad * 2 - textX - l.Indent * indentW) : float.MaxValue;
            if (wrappable && maxWidth.Value > 0 && Measure(Ascii(l.Text)).X > avail)
            {
                var wrapped = WrapText(Ascii(l.Text), avail, scale);
                for (int i = 0; i < wrapped.Count; i++)
                    rows.Add(new PanelLine(wrapped[i], l.Color, num: i == 0 ? l.Num : null,
                        indent: l.Indent, onCtrlClick: l.OnCtrlClick));
            }
            else rows.Add(l);
        }

        // panel inner width. when MaxWidth is set the panel is fixed to it (long rows wrap into it);
        // otherwise it auto-sizes to the widest text row. bars/axis span whatever width results.
        float contentW = 0f;
        bool hasDrawn = false;
        foreach (var r in rows)
        {
            float w = r.Kind switch
            {
                PanelRowKind.Bar or PanelRowKind.Segmented or PanelRowKind.Axis => 0f,
                PanelRowKind.Pinned => textX + Math.Max(Measure(Ascii(r.Text)).X, Measure(Ascii(r.SubText ?? "")).X),
                _ when r.IsSeparator => Measure(SepLabel(Ascii(r.Text))).X + 2 * sepMinDashes * dashW,
                _ when r.IsHeader => Measure(Ascii(r.Text)).X,
                _ => textX + r.Indent * indentW + Measure(Ascii(r.Text)).X,
            };
            if (r.Right != null && (r.IsHeader || r.Kind == PanelRowKind.Text))
                w += textSize + Measure(Ascii(r.Right)).X;
            if (r.Kind is PanelRowKind.Bar or PanelRowKind.Segmented or PanelRowKind.Axis) hasDrawn = true;
            contentW = Math.Max(contentW, w);
        }
        if (maxWidth.Value > 0) contentW = Math.Max(10f, maxWidth.Value - pad * 2);   // user-set fixed width
        else if (hasDrawn) contentW = Math.Max(contentW, textSize * 12f);             // usable bar width on short panels

        // pinned title wraps into the fixed width (the current step can be long); one line when auto-sized.
        List<string> PinnedTitle(string text)
        {
            var t = Ascii(text);
            float w = contentW - textX;
            return maxWidth.Value > 0 && w > 10f && Measure(t).X > w ? WrapText(t, w, scale) : new List<string> { t };
        }

        // per-row height (drawn kinds are taller than one text line).
        float RowH(in PanelLine r) => r.Kind switch
        {
            PanelRowKind.Bar or PanelRowKind.Segmented => r.BarHeight + 6f,
            PanelRowKind.Axis => r.BarHeight + 10f,
            PanelRowKind.Pinned => (PinnedTitle(r.Text).Count + 1) * lineH + 8f,
            _ => lineH,
        };

        float totalH = pad * 2;
        foreach (var r in rows) totalH += RowH(r);
        var panelSize = new Vector2(contentW + pad * 2, totalH);
        var origin = new Vector2(posX.Value, posY.Value);

        // big windows always hide us; a side panel hides or pushes us aside per Settings.PanelAvoidMode.
        var panelRect = new RectangleF(origin.X, origin.Y, panelSize.X, panelSize.Y);
        if (OverlapsLargePanel(panelRect)) return;

        float shift = 0f;
        if (OverlapsSidePanel(panelRect))
        {
            if (Settings.PanelAvoidMode.Value == 0) return;
            shift = SidePanelShift(panelRect);
            if (shift == 0f) return;   // nowhere to slide, hide instead
        }

        // no drag/resize while offset - the saved position would jump by the shift.
        var (hovered, onEdge) = shift == 0f
            ? HandleInteract(id, ref origin, panelSize, posX, posY, maxWidth)
            : (false, false);
        origin.X += shift;
        bool active = _dragId == id || _resizeId == id;

        var min = origin;
        var max = origin + panelSize;

        // cumulative row tops, for hit-testing and drawing at variable heights.
        var rowTop = new float[rows.Count];
        {
            float acc = origin.Y + pad;
            for (int i = 0; i < rows.Count; i++) { rowTop[i] = acc; acc += RowH(rows[i]); }
        }

        // ctrl-hover a clickable row (build "have", route re-pin) to arm a one-click action. works locked too.
        int hoverRow = -1;
        {
            // ExileAPI never feeds ImGui io.KeyCtrl, so read the modifier off the framework input instead
            bool ctrl = Input.GetKeyState(System.Windows.Forms.Keys.LControlKey)
                     || Input.GetKeyState(System.Windows.Forms.Keys.RControlKey);
            var m = ImGui.GetMousePos();
            if (ctrl && m.X >= min.X && m.X <= max.X)
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i].OnCtrlClick != null && m.Y >= rowTop[i] && m.Y < rowTop[i] + RowH(rows[i]))
                    { hoverRow = i; break; }
        }

        // while movable + hovered/active, put an invisible ImGui window under the panel so ImGui captures
        // the mouse and drag/resize clicks don't pass through to the game. also arm it for a ctrl-click.
        if ((OverlaysMovable && (hovered || active)) || hoverRow >= 0)
            DrawClickBlocker(id, origin, panelSize);

        if (background.A > 0) dl.AddRectFilled(min, max, U32(background));
        if (borderThickness > 0) dl.AddRect(min, max, U32(borderColor), 0f, ImDrawFlags.None, borderThickness);
        // drag/resize affordance. unlocked: outline every panel (faint, bright on hover). locked but Alt held:
        // only the panel you're actually over, so Alt (common in-game) doesn't light up the whole screen.
        if (OverlaysMovable && (!Settings.LockOverlays.Value || hovered || active))
        {
            var hint = (active || hovered) ? new Color(120, 220, 255, 220) : new Color(120, 220, 255, 120);
            dl.AddRect(min, max, U32(hint), 0f, ImDrawFlags.None, 1.5f);
            if (onEdge || _resizeId == id)
                dl.AddLine(new Vector2(max.X, min.Y), new Vector2(max.X, max.Y), U32(new Color(120, 220, 255, 255)), 3f);
        }

        // ctrl-armed row gets a highlight so the target is obvious before you click
        if (hoverRow >= 0)
        {
            float ry = rowTop[hoverRow];
            dl.AddRectFilled(new Vector2(min.X + 1, ry), new Vector2(max.X - 1, ry + RowH(rows[hoverRow])),
                U32(new Color(120, 220, 255, 55)));
        }

        float innerL = origin.X + pad;
        float innerR = max.X - pad;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            float y = rowTop[i];
            switch (r.Kind)
            {
                case PanelRowKind.Bar: DrawBarRow(dl, r, innerL, innerR, y, RowH(r)); break;
                case PanelRowKind.Segmented: DrawSegmentedRow(dl, r, innerL, innerR, y, RowH(r)); break;
                case PanelRowKind.Axis: DrawAxisRow(dl, r, innerL, innerR, y, RowH(r)); break;
                case PanelRowKind.Pinned: DrawPinnedRow(dl, font, textSize, r, min, max, innerL, textX, y, lineH, RowH(r), PinnedTitle(r.Text)); break;
                default: DrawTextRow(dl, font, textSize, r, Measure, SepLabel, dashW, sepMinDashes, contentW,
                    innerL, y, textX, indentW); break;
            }
        }

        // fire after drawing so the mutation lands cleanly, not mid-enumeration
        if (hoverRow >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            rows[hoverRow].OnCtrlClick!.Invoke();
    }

    // one plain text / header / separator row.
    private void DrawTextRow(ImDrawListPtr dl, ImFontPtr font, float textSize, in PanelLine r,
        Func<string, Vector2> measure, Func<string, string> sepLabel, float dashW, int sepMinDashes,
        float contentW, float innerL, float y, float textX, float indentW)
    {
        var col = U32(r.Color);
        var p = new Vector2(innerL, y);
        if (r.IsSeparator)
        {
            var label = sepLabel(Ascii(r.Text));
            int n = sepMinDashes;
            if (dashW > 0) n = Math.Max(sepMinDashes, (int)((contentW - measure(label).X) / 2f / dashW));
            var run = new string('-', n);
            dl.AddText(font, textSize, p, col, run + label + run);
            return;
        }
        if (r.Num != null) dl.AddText(font, textSize, p, col, r.Num);
        float tx = r.IsHeader ? 0f : textX + r.Indent * indentW;
        if (!string.IsNullOrEmpty(r.Text))
            dl.AddText(font, textSize, p + new Vector2(tx, 0), col, Ascii(r.Text));
        if (r.Right != null)
        {
            float rx = contentW - measure(Ascii(r.Right)).X;   // align to the panel's inner-right edge
            dl.AddText(font, textSize, new Vector2(innerL + rx, y), col, Ascii(r.Right));
        }
    }

    // highlighted current-objective row: bg box + 2px left accent bar + (wrapped) title + dim zone sub-line.
    private void DrawPinnedRow(ImDrawListPtr dl, ImFontPtr font, float textSize, in PanelLine r,
        Vector2 min, Vector2 max, float innerL, float textX, float y, float lineH, float rowH, List<string> titleLines)
    {
        var boxMin = new Vector2(min.X + 1, y);
        var boxMax = new Vector2(max.X - 1, y + rowH);
        dl.AddRectFilled(boxMin, boxMax, U32(PinnedBg));
        dl.AddRectFilled(boxMin, new Vector2(boxMin.X + 2f, boxMax.Y), U32(r.Accent));
        var col = U32(r.Color);
        var p = new Vector2(innerL, y + 3f);
        if (r.Num != null) dl.AddText(font, textSize, p, col, r.Num);
        float ty = p.Y;
        foreach (var tl in titleLines) { dl.AddText(font, textSize, new Vector2(innerL + textX, ty), col, tl); ty += lineH; }
        if (r.SubText != null)
            dl.AddText(font, textSize, new Vector2(innerL + textX, ty), U32(RowMuted), Ascii(r.SubText));
    }

    // full-width track + gradient fill (flat when fillA==fillB).
    private void DrawBarRow(ImDrawListPtr dl, in PanelLine r, float innerL, float innerR, float y, float rowH)
    {
        float top = y + (rowH - r.BarHeight) / 2f;
        var a = new Vector2(innerL, top);
        var b = new Vector2(innerR, top + r.BarHeight);
        dl.AddRectFilled(a, b, U32(r.TrackColor));
        float fillW = (innerR - innerL) * r.Fill01;
        if (fillW > 0.5f)
        {
            var fb = new Vector2(innerL + fillW, b.Y);
            if (r.FillA == r.FillB) dl.AddRectFilled(a, fb, U32(r.FillA));
            else dl.AddRectFilledMultiColor(a, fb, U32(r.FillA), U32(r.FillB), U32(r.FillB), U32(r.FillA));
        }
        dl.AddRect(a, b, U32(BarTrackBorder), 0f, ImDrawFlags.None, 1f);
    }

    // N equal cells with 2px gaps, one fill each (the 10-act campaign bar).
    private void DrawSegmentedRow(ImDrawListPtr dl, in PanelLine r, float innerL, float innerR, float y, float rowH)
    {
        var cells = r.Cells;
        if (cells == null || cells.Length == 0) return;
        int n = cells.Length;
        const float cellGap = 2f;
        float top = y + (rowH - r.BarHeight) / 2f;
        float cellW = (innerR - innerL - cellGap * (n - 1)) / n;
        if (cellW <= 0) return;
        for (int i = 0; i < n; i++)
        {
            float cx = innerL + i * (cellW + cellGap);
            var a = new Vector2(cx, top);
            var b = new Vector2(cx + cellW, top + r.BarHeight);
            dl.AddRectFilled(a, b, U32(r.TrackColor));
            float f = Math.Clamp(cells[i], 0f, 1f);
            if (f > 0.01f) dl.AddRectFilled(a, new Vector2(cx + cellW * f, b.Y), U32(r.FillA));
        }
    }

    // XP-penalty safe-zone axis: severity flank bands, a green safe band with edge lines, cyan marker.
    private void DrawAxisRow(ImDrawListPtr dl, in PanelLine r, float innerL, float innerR, float y, float rowH)
    {
        int cl = r.CharLevel, al = r.AreaLevel;
        int safe = SafeZone(cl);
        float half = Math.Max(safe + 5, 8);
        float top = y + (rowH - r.BarHeight) / 2f;
        float w = innerR - innerL;
        var a = new Vector2(innerL, top);
        var b = new Vector2(innerR, top + r.BarHeight);

        void Band(float p0, float p1, Color c0, Color c1) =>
            dl.AddRectFilledMultiColor(new Vector2(innerL + w * p0, a.Y), new Vector2(innerL + w * p1, b.Y),
                U32(c0), U32(c1), U32(c1), U32(c0));
        Band(0f, 0.26f, AxisFlankRed, AxisFlankAmber);
        Band(0.26f, 0.5f, AxisFlankAmber, AxisFlankGrey);
        Band(0.5f, 0.74f, AxisFlankGrey, AxisFlankAmber);
        Band(0.74f, 1f, AxisFlankAmber, AxisFlankRed);

        float safeLeft = 0.5f - safe / half * 0.5f;
        float safeRight = 0.5f + safe / half * 0.5f;
        var sa = new Vector2(innerL + w * safeLeft, a.Y);
        var sb = new Vector2(innerL + w * safeRight, b.Y);
        dl.AddRectFilled(sa, sb, U32(AxisSafeBand));
        dl.AddLine(new Vector2(sa.X, a.Y), new Vector2(sa.X, b.Y), U32(AxisSafeEdge), 1f);
        dl.AddLine(new Vector2(sb.X, a.Y), new Vector2(sb.X, b.Y), U32(AxisSafeEdge), 1f);
        dl.AddRect(a, b, U32(BarTrackBorder), 0f, ImDrawFlags.None, 1f);

        float markerPct = Math.Clamp(0.5f + (al - cl) / half * 0.5f, 0.03f, 0.97f);
        float mx = innerL + w * markerPct;
        dl.AddLine(new Vector2(mx, a.Y - 3f), new Vector2(mx, b.Y + 3f), U32(AxisMarker), 2f);
    }

    // invisible ImGui window sized to the panel. only job: be hovered so ImGui sets WantCaptureMouse
    // (which ExileCore honours), swallowing drag clicks before the game sees them.
    private static void DrawClickBlocker(string id, Vector2 pos, Vector2 size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.Begin($"##ecdrag_{id}", flags))
            ImGui.End();
        else
            ImGui.End();
    }

    // left-drag move (body) + right-edge resize for a panel. returns (hovered, onRightEdge).
    // mutates origin / writes back posX,posY (move) or maxWidth (resize) while held.
    private (bool hovered, bool onEdge) HandleInteract(string id, ref Vector2 origin, Vector2 size,
        RangeNode<int> posX, RangeNode<int> posY, RangeNode<int> maxWidth)
    {
        // locked + no Alt: never movable. (a drag already in flight keeps going even if Alt is released.)
        if (!OverlaysMovable && _dragId != id && _resizeId != id)
        {
            if (_dragId == id) _dragId = null;
            if (_resizeId == id) _resizeId = null;
            return (false, false);
        }

        const float edge = 6f;
        var mouse = ImGui.GetMousePos();
        bool hovered = mouse.X >= origin.X && mouse.X <= origin.X + size.X
                    && mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        bool onEdge = hovered && mouse.X >= origin.X + size.X - edge;

        if (onEdge || _resizeId == id) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

        if (_dragId == null && _resizeId == null && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (onEdge) { _resizeId = id; }
            else { _dragId = id; _dragOffset = mouse - origin; }
        }

        if (_dragId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                origin = mouse - _dragOffset;
                posX.Value = (int)Math.Round(origin.X);
                posY.Value = (int)Math.Round(origin.Y);
            }
            else _dragId = null;
        }

        if (_resizeId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                maxWidth.Value = Math.Clamp((int)Math.Round(mouse.X - origin.X), 60, maxWidth.Max);
            else _resizeId = null;
        }

        return (hovered, onEdge);
    }

    // center-anchored move + right-edge resize, for overlays whose PosX is a horizontal centre (banner,
    // toasts). move writes centre X / top Y; resize sets MaxWidth symmetric about the centre. mutates `min`
    // to follow a move so the caller can keep drawing this frame. returns (hovered, onEdge, active).
    private (bool hovered, bool onEdge, bool active) HandleCenterInteract(string id, ref Vector2 min, Vector2 size,
        RangeNode<int> posX, RangeNode<int> posY, RangeNode<int> maxWidth, bool forceMovable = false)
    {
        if (!OverlaysMovable && !forceMovable && _dragId != id && _resizeId != id)
        {
            if (_dragId == id) _dragId = null;
            if (_resizeId == id) _resizeId = null;
            return (false, false, false);
        }

        const float edge = 6f;
        var mouse = ImGui.GetMousePos();
        bool hovered = mouse.X >= min.X && mouse.X <= min.X + size.X
                    && mouse.Y >= min.Y && mouse.Y <= min.Y + size.Y;
        bool onEdge = hovered && mouse.X >= min.X + size.X - edge;
        if (onEdge || _resizeId == id) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

        if (_dragId == null && _resizeId == null && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (onEdge) _resizeId = id;
            else { _dragId = id; _dragOffset = mouse - new Vector2(posX.Value, posY.Value); }
        }
        if (_dragId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var c = mouse - _dragOffset;
                posX.Value = (int)Math.Round(c.X);
                posY.Value = (int)Math.Round(c.Y);
                min = new Vector2(posX.Value - size.X / 2f, posY.Value);
            }
            else _dragId = null;
        }
        if (_resizeId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                maxWidth.Value = Math.Clamp((int)Math.Round(2 * (mouse.X - posX.Value)), 60, maxWidth.Max);
            else _resizeId = null;
        }

        bool active = _dragId == id || _resizeId == id;
        return (hovered, onEdge, active);
    }

    // outline + right-edge grip drawn around a draggable panel while overlays are unlocked.
    private void DrawDragHint(Vector2 min, Vector2 max, bool active, bool hovered, bool onEdge, bool resizing)
    {
        var hint = (active || hovered) ? new Color(120, 220, 255, 220) : new Color(120, 220, 255, 120);
        var dl = ImGui.GetForegroundDrawList();
        dl.AddRect(min, max, U32(hint), 0f, ImDrawFlags.None, 1.5f);
        if (onEdge || resizing)
            dl.AddLine(new Vector2(max.X, min.Y), new Vector2(max.X, max.Y), U32(new Color(120, 220, 255, 255)), 3f);
    }

    // greedy word wrap. maxWidth is the pixel budget for the text column at the given scale.
    private static List<string> WrapText(string text, float maxWidth, float scale)
    {
        var words = text.Split(' ');
        var lines = new List<string>();
        var cur = "";
        foreach (var w in words)
        {
            var trial = cur.Length == 0 ? w : cur + " " + w;
            if (cur.Length > 0 && ImGui.CalcTextSize(trial).X * scale > maxWidth)
            {
                lines.Add(cur);
                cur = w;
            }
            else cur = trial;
        }
        if (cur.Length > 0) lines.Add(cur);
        if (lines.Count == 0) lines.Add(text);
        return lines;
    }

    private static uint U32(Color c) => ImGui.GetColorU32(new Vector4(c.R, c.G, c.B, c.A) / 255f);

    // top-centre banner shown on auto-advance (fades out over the last 0.5s). Preview keeps it up with
    // sample text for positioning/resizing; draggable when overlays are unlocked.
    private void DrawBanner()
    {
        if (!Settings.Banner.Enable) return;
        var b = Settings.Banner;
        const string sample = "ACT 1  -  Sample next-step objective (banner preview)";
        bool preview = b.Preview.Value;
        // the preview/unlock toggle conjures a placeholder to position; Alt only unlocks a banner already showing
        bool showSample = preview || Settings.AlertsMovable.Value;

        float alpha = 1f;
        string? source = null;
        if (preview)
        {
            source = _bannerText ?? sample;
        }
        else if (b.Persistent.Value)
        {
            source = _route.CurrentStep?.DisplayText;
        }
        else if (_bannerText != null)
        {
            var elapsed = (DateTime.Now - _bannerShownAt).TotalSeconds;
            if (elapsed >= b.DurationSeconds.Value) _bannerText = null;   // expired
            else
            {
                const double fade = 0.5;
                var remaining = b.DurationSeconds.Value - elapsed;
                if (remaining < fade) alpha = (float)Math.Clamp(remaining / fade, 0, 1);
                source = _bannerText;
            }
        }

        // nothing live to show: only conjure a sample when preview/unlock is on (Alt alone won't force it up)
        if (source == null)
        {
            if (!showSample) return;
            source = sample;
        }

        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        if (baseSize <= 0) baseSize = 16f;
        float size = b.TextSize.Value;
        float scale = size / baseSize;
        float pad = b.Padding.Value;

        var text = Ascii(source);
        float maxTextW = b.MaxWidth.Value > 0 ? Math.Max(10f, b.MaxWidth.Value - pad * 2) : float.MaxValue;
        var rows = b.MaxWidth.Value > 0 && ImGui.CalcTextSize(text).X * scale > maxTextW
            ? WrapText(text, maxTextW, scale)
            : new List<string> { text };

        float lineH = (float)Math.Ceiling(size) + 4f;
        float contentW = 0f;
        foreach (var r in rows) contentW = Math.Max(contentW, ImGui.CalcTextSize(r).X * scale);

        float panelW = contentW + pad * 2;
        float panelH = rows.Count * lineH + pad * 2;
        var min = new Vector2(b.PosX.Value - panelW / 2f, b.PosY.Value);

        // center-anchored move/resize: in preview, or any time via the alerts-movable toggle / Alt-drag.
        bool draggable = showSample || AltHeld;
        bool hovered = false, onEdge = false, active = false;
        if (draggable)
        {
            (hovered, onEdge, active) = HandleCenterInteract("banner", ref min, new Vector2(panelW, panelH),
                b.PosX, b.PosY, b.MaxWidth, forceMovable: true);
            if (hovered || active) DrawClickBlocker("banner", min, new Vector2(panelW, panelH));
        }

        var max = min + new Vector2(panelW, panelH);

        if (OverlapsSidePanel(min, max)) return;   // hide under an open side panel

        var bg = Fade(b.BackgroundColor.Value, alpha);
        if (bg.A > 0) dl.AddRectFilled(min, max, U32(bg));
        if (b.BorderThickness.Value > 0)
            dl.AddRect(min, max, U32(Fade(b.BorderColor.Value, alpha)), 0f, ImDrawFlags.None, b.BorderThickness.Value);
        if (draggable)
            DrawDragHint(min, max, active, hovered, onEdge, _resizeId == "banner");

        uint textCol = U32(Fade(b.TextColor.Value, alpha));
        var p = min + new Vector2(pad, pad);
        foreach (var r in rows)
        {
            float w = ImGui.CalcTextSize(r).X * scale;
            dl.AddText(font, size, new Vector2(min.X + (panelW - w) / 2f, p.Y), textCol, r);  // centre each line
            p.Y += lineH;
        }
    }

    private static Color Fade(Color c, float a) => new Color(c.R, c.G, c.B, (byte)Math.Clamp(c.A * a, 0, 255));

    // map common Unicode to ASCII (drop the rest) so text renders in ImGui's ASCII-only default font.
    private static string Ascii(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c < 128) { sb.Append(c); continue; }
            switch (c)
            {
                case '–': case '—': case '−': sb.Append('-'); break;
                case '→': sb.Append("->"); break;
                case '←': sb.Append("<-"); break;
                case '•': case '·': case '●': case '◇': case '▶': sb.Append('*'); break;
                case '✓': case '✔': case '×': sb.Append('x'); break;
                case '“': case '”': sb.Append('"'); break;
                case '‘': case '’': sb.Append('\''); break;
                case '…': sb.Append("..."); break;
                default: sb.Append('?'); break;
            }
        }
        return sb.ToString();
    }

    public override void DrawSettings()
    {
        DrawSettingsTabs();   // custom tabbed panel; profiles live in the first tab now
    }
}
