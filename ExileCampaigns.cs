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

// one rendered line in an overlay panel. Num (when set) draws in a fixed-width left column so the text
// column stays put as step numbers grow (9 -> 10). IsHeader lines (act/stage labels) skip the number
// column and render flush-left.
internal readonly struct PanelLine
{
    public readonly string Text;
    public readonly Color Color;
    public readonly bool IsHeader;
    public readonly bool IsSeparator;   // area divider: Text is the bare area name, drawn centred in a dashed rule
    public readonly string? Num;
    public readonly string? Right;   // optional right-aligned text on the same row (e.g. area name on header)

    public PanelLine(string text, Color color, bool isHeader = false, string? num = null,
        string? right = null, bool isSeparator = false)
    {
        Text = text;
        Color = color;
        IsHeader = isHeader;
        IsSeparator = isSeparator;
        Num = num;
        Right = right;
    }
}

// campaign-leveling overlay for PoE2. reads act/zone/level/quest from live memory (no screen-reading)
// and drives a routing guide. this file is plugin entry + lifecycle; features live under Guide/ and Tracking/.
public partial class ExileCampaigns : BaseSettingsPlugin<ExileCampaignsSettings>
{
    private readonly RouteRepository _route = new();
    private Guide.RouteStore? _routeStore;

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
        return true;
    }

    // load routes from route.json (user copy under ConfigDirectory\route wins, else bundled under Data).
    private void LoadRoutes()
    {
        var doc = LoadRouteDocument();
        _routeStore = new Guide.RouteStore(doc);
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
        RecordRecentAreaTransition(_areaId, _zoneName);
    }

    // keep recent entered areas (newest last, cap 15) for the editor's click-to-assign
    // completeOnEnterArea list. skip blanks and consecutive repeats.
    private void RecordRecentAreaTransition(string areaId, string name)
    {
        if (string.IsNullOrEmpty(areaId)) return;
        if (_recentAreaTransitions.Count > 0 && _recentAreaTransitions[^1].AreaId == areaId) return;
        _recentAreaTransitions.Add((areaId, name ?? ""));
        if (_recentAreaTransitions.Count > 15)
            _recentAreaTransitions.RemoveRange(0, _recentAreaTransitions.Count - 15);
    }

    private DateTime _lastAdvanceEval = DateTime.MinValue;

    // set when the user manually moves the cursor backward. holds off auto-advance so a deliberate
    // back-step onto already-completed content sticks; cleared on the next real zone change.
    private bool _holdAutoAdvanceUntilZone;

    // single advance gate: the current step's objectives, evaluated against live state. replaces
    // AutoAdvanceFromQuestFlags + MaybeAdvanceOnInteraction + MaybeAdvanceOnObjectiveComplete.
    private void EvaluateAdvance()
    {
        if (!Settings.AutoAdvance) return;
        if (_holdAutoAdvanceUntilZone) return;
        if ((DateTime.UtcNow - _lastAdvanceEval).TotalSeconds < 0.4) return;
        _lastAdvanceEval = DateTime.UtcNow;

        var model = _route.CurrentStep?.Model;
        if (model == null) return;
        UpdateProgressTracker(model);
        if (Guide.AdvanceEngine.IsStepComplete(model, new WorldState(this)))
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

        UpdatePathTarget();
        UpdateWaypointPulse();
        UpdateLoginPulse();
        UpdateInteractTarget();
        // runs after profile switch resolves above, so a swap window never scans the new char against the old build
        DetectBuildUsed();
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

        // cache the open side-panel rects once per frame; every overlay hides where it would overlap one.
        _leftPanelRect = ui.OpenLeftPanel.IsVisible ? SafeClientRect(ui.OpenLeftPanel) : null;
        _rightPanelRect = ui.OpenRightPanel.IsVisible ? SafeClientRect(ui.OpenRightPanel) : null;

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
            DrawOverlay("char", BuildCharStatsLines(Settings.CharStats), Settings.CharStats);

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

        // emit an act header when the window starts or crosses into a new act, an area divider when the
        // zone changes, then the step itself.
        void AddRow(FlatStep st, Color col, bool isCurrent)
        {
            if (st.Act != lastAct)
            {
                lines.Add(new PanelLine(StageLabel(st.Act), s.HeaderColor.Value, isHeader: true,
                    right: firstHeader ? areaName : null));
                firstHeader = false;
                lastAct = st.Act;
                lastArea = null;   // re-print the divider for the first area under the new act
            }
            // every windowed row is a real step (headers are skipped), so Model carries its zone.
            var area = st.Model?.AreaName;
            var areaKey = st.Model?.AreaId ?? area;
            if (!string.IsNullOrEmpty(area) && !string.Equals(areaKey, lastArea, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(new PanelLine(area!, sepColor, isSeparator: true));
                lastArea = areaKey;
            }
            var text = (isCurrent ? "> " : "") + st.DisplayText;
            lines.Add(new PanelLine(text, col, num: st.StepInAct.ToString()));
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

    // point-test variant for per-vertex world draws (path segments, comets).
    private bool PointInSidePanel(Vector2 p) =>
        (_leftPanelRect is { } l && l.Contains(p.X, p.Y)) || (_rightPanelRect is { } rp && rp.Contains(p.X, p.Y));

    private void DrawOverlay(string id, IReadOnlyList<PanelLine> lines, OverlayStyle s) =>
        DrawOverlay(id, lines, s.PosX, s.PosY, s.TextSize.Value, s.MaxWidth, s.Padding.Value,
            s.BackgroundColor.Value, s.BorderColor.Value, s.BorderThickness.Value);

    // draw a panel of lines via the ImGui foreground draw list. unlike Graphics.DrawText this honours a
    // per-call font size (textSize), so each overlay gets its own scale/border/fill. when unlocked, drag the
    // body to move or the right edge to set wrap width; results write back to posX/posY/maxWidth (auto-persist).
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

        Vector2 Measure(string t) => ImGui.CalcTextSize(t) * scale;

        // fixed-width number column so the text column never shifts as step numbers grow.
        float numCol = 0f;
        bool hasNum = false;
        foreach (var l in lines)
            if (l.Num != null) { hasNum = true; numCol = Math.Max(numCol, Measure(l.Num).X); }
        float gap = hasNum ? textSize * 0.4f : 0f;
        float textX = numCol + gap;

        float maxTextW = maxWidth.Value > 0 ? Math.Max(10f, maxWidth.Value - pad * 2 - textX) : float.MaxValue;

        // flatten lines into rows, wrapping over-wide text. continuation rows start under the text column.
        const int sepMinDashes = 3;   // shortest dash run each side of an area-divider label
        var rows = new List<(string? num, string text, uint col, bool header, string? right, bool sep)>();
        foreach (var l in lines)
        {
            var t = Ascii(l.Text);
            var col = U32(l.Color);
            if (l.IsSeparator)
            {
                rows.Add((null, t, col, false, null, true));   // text = bare area name, dashes added at draw
                continue;
            }
            if (l.IsHeader)
            {
                rows.Add((null, t, col, true, l.Right != null ? Ascii(l.Right) : null, false));
                continue;
            }
            if (maxWidth.Value > 0 && Measure(t).X > maxTextW)
            {
                var wrapped = WrapText(t, maxTextW, scale);
                for (int i = 0; i < wrapped.Count; i++)
                    rows.Add((i == 0 ? l.Num : null, wrapped[i], col, false, null, false));
            }
            else rows.Add((l.Num, t, col, false, null, false));
        }

        float lineH = (float)Math.Ceiling(textSize) + 2f;

        // a divider needs room for its label plus the minimum dashes; everything else as before.
        string SepLabel(string name) => " " + name + " ";
        float dashW = Measure("-").X;

        float contentW = 0f;
        foreach (var r in rows)
        {
            float w = r.sep ? Measure(SepLabel(r.text)).X + 2 * sepMinDashes * dashW
                    : r.header ? Measure(r.text).X : textX + Measure(r.text).X;
            if (r.right != null) w += textSize + Measure(r.right).X;   // header + right-aligned area name
            contentW = Math.Max(contentW, w);
        }

        var panelSize = new Vector2(contentW + pad * 2, rows.Count * lineH + pad * 2);
        var origin = new Vector2(posX.Value, posY.Value);

        // hide where this overlay would sit under an open side panel.
        if (OverlapsSidePanel(origin, origin + panelSize))
            return;

        var (hovered, onEdge) = HandleInteract(id, ref origin, panelSize, posX, posY, maxWidth);
        bool active = _dragId == id || _resizeId == id;

        // while unlocked + hovered/active, put an invisible ImGui window under the panel so ImGui captures
        // the mouse and drag/resize clicks don't pass through to the game.
        if (!Settings.LockOverlays.Value && (hovered || active))
            DrawClickBlocker(id, origin, panelSize);

        var min = origin;
        var max = origin + panelSize;

        if (background.A > 0) dl.AddRectFilled(min, max, U32(background));
        if (borderThickness > 0) dl.AddRect(min, max, U32(borderColor), 0f, ImDrawFlags.None, borderThickness);
        // Drag/resize affordance: while unlocked, outline the panel and mark the right resize edge.
        if (!Settings.LockOverlays.Value)
        {
            var hint = (active || hovered) ? new Color(120, 220, 255, 220) : new Color(120, 220, 255, 120);
            dl.AddRect(min, max, U32(hint), 0f, ImDrawFlags.None, 1.5f);
            // brighter grip on the right edge when it's the resize target.
            if (onEdge || _resizeId == id)
                dl.AddLine(new Vector2(max.X, min.Y), new Vector2(max.X, max.Y), U32(new Color(120, 220, 255, 255)), 3f);
        }

        var p = origin + new Vector2(pad, pad);
        foreach (var r in rows)
        {
            if (r.sep)
            {
                // grow the dash runs to fill the panel, label centred: ---- Area Name ----
                var label = SepLabel(r.text);
                int n = sepMinDashes;
                if (dashW > 0) n = Math.Max(sepMinDashes, (int)((contentW - Measure(label).X) / 2f / dashW));
                var run = new string('-', n);
                dl.AddText(font, textSize, p, r.col, run + label + run);
                p.Y += lineH;
                continue;
            }
            if (r.num != null)
                dl.AddText(font, textSize, p, r.col, r.num);
            float tx = r.header ? 0f : textX;
            dl.AddText(font, textSize, p + new Vector2(tx, 0), r.col, r.text);
            if (r.right != null)
            {
                float rx = contentW - Measure(r.right).X;   // align to panel's inner-right edge
                dl.AddText(font, textSize, new Vector2(p.X + rx, p.Y), r.col, r.right);
            }
            p.Y += lineH;
        }
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
        if (Settings.LockOverlays.Value)
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
        RangeNode<int> posX, RangeNode<int> posY, RangeNode<int> maxWidth)
    {
        if (Settings.LockOverlays.Value)
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
        bool preview = b.Preview.Value;

        float alpha = 1f;
        string source;
        if (preview)
        {
            source = _bannerText ?? "ACT 1  -  Sample next-step objective (banner preview)";
        }
        else
        {
            if (_bannerText == null) return;
            var elapsed = (DateTime.Now - _bannerShownAt).TotalSeconds;
            if (elapsed >= b.DurationSeconds.Value) { _bannerText = null; return; }
            const double fade = 0.5;
            var remaining = b.DurationSeconds.Value - elapsed;
            if (remaining < fade) alpha = (float)Math.Clamp(remaining / fade, 0, 1);
            source = _bannerText;
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

        // center-anchored move/resize during preview (only when unlocked), via the shared helper.
        bool hovered = false, onEdge = false, active = false;
        if (preview && !Settings.LockOverlays.Value)
        {
            (hovered, onEdge, active) = HandleCenterInteract("banner", ref min, new Vector2(panelW, panelH),
                b.PosX, b.PosY, b.MaxWidth);
            if (hovered || active) DrawClickBlocker("banner", min, new Vector2(panelW, panelH));
        }

        var max = min + new Vector2(panelW, panelH);

        if (OverlapsSidePanel(min, max)) return;   // hide under an open side panel

        var bg = Fade(b.BackgroundColor.Value, alpha);
        if (bg.A > 0) dl.AddRectFilled(min, max, U32(bg));
        if (b.BorderThickness.Value > 0)
            dl.AddRect(min, max, U32(Fade(b.BorderColor.Value, alpha)), 0f, ImDrawFlags.None, b.BorderThickness.Value);
        if (preview && !Settings.LockOverlays.Value)
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
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.TextDisabled($"Profile: {(string.IsNullOrEmpty(_charName) ? "(no character loaded)" : _charName)}");
        ImGuiNET.ImGui.TextDisabled($"Route: {_route.Status ?? "not loaded"} · step {_route.Current + 1}/{_route.Steps.Count}");
    }
}
