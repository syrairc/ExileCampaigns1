using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ExileCampaigns.Guide;
using ExileCampaigns.Rendering;
using ImGuiNET;

namespace ExileCampaigns;

// in-game route editor for the new route.json model. one ImGui window with a Route tab (Act>Area>Step tree +
// step/objective/children detail) and a Reports tab (broken-parameter reports with deep-link). edits mutate
// _routeStore, persist to the user route.json via SaveUserRoute, and rebuild the read-side _route by Id.
// gated on Settings.Editor.Enable. all pure list math lives in Guide/RouteEditing.cs.
public partial class ExileCampaigns
{
    // --- picker state written elsewhere (Flags.cs harvester, CaptureArea), read by the editor pickers ------
    private string? _lastHarvestedFlag;
    private readonly List<string> _recentHarvestedFlags = new();
    private readonly List<(string AreaId, string Name)> _recentAreaTransitions = new();

    // --- editor state -------------------------------------------------------------------------------------
    private int _editorSelectedObjIndex;      // selected objective within the selected step
    private bool _pendingRouteSave;           // drained after the window: SaveUserRoute + reload
    private string? _pendingSelectId;         // select this step id after the deferred reload (e.g. after add)

    // draw the current step's resolved-target crosshair on the large map (read by DrawDevOverlay).
    private bool _editorPreviewTarget;

    // --- edit buffers. step-level resync keyed on _bufForStepId; objective-level on (_bufForStepId,_bufForObjIndex)
    private string? _bufForStepId;
    private int _bufForObjIndex = -1;
    private string _strStepText = "";
    private readonly byte[] _bufAreaId = new byte[64];
    private readonly byte[] _bufAreaName = new byte[128];
    private int _stepAct;
    private bool _stepOptional;
    private bool _stepLeagueStart;
    private int _stepCompleteWhen;
    // objective-level
    private int _objType;
    private int _objCount = 1;
    private float _objDistance;
    private readonly byte[] _bufObjFlag = new byte[128];
    private readonly byte[] _bufObjArea = new byte[64];
    private readonly byte[] _bufObjLabel = new byte[128];
    private string _strObjNote = "";
    // add-row scratch (shared across objectives; one objective edited at a time)
    private readonly byte[] _bufAddEntity = new byte[256];
    private int _addEntityMatchBy;
    private bool _addEntityRegex;
    private readonly byte[] _bufAddItem = new byte[256];
    private int _addItemCount = 1;
    private readonly byte[] _bufAddProg = new byte[128];
    private readonly byte[] _bufAddPath = new byte[256];
    private int _addPathKind;
    private bool _addPathRegex;
    private int _addPathMatchBy;
    private readonly byte[] _bufAddInd = new byte[256];
    private int _addIndKind;
    private bool _addIndRegex;
    private int _addIndMatchBy;
    private bool _addIndLiving;
    private readonly byte[] _bufMmiPat = new byte[256];
    private int _mmiKind;
    private int _mmiMatchBy;
    private bool _mmiRegex;
    private bool _mmiLiving;

    private static readonly string[] CompleteWhenLabels = { "All", "Any" };
    private static readonly string[] MatchByLabels = { "Name", "Path" };
    private static readonly string[] TargetKindLabels = { "Tile", "Entity", "Room" };
    private static readonly string[] PathModeLabels = { "Nearest", "All" };
    private static readonly string[] ObjTypeLabels = Enum.GetNames(typeof(ObjectiveType));

    // window rect captured last frame, so the hide test can run before Begin this frame. see _triageRect.
    private (Vector2 Min, Vector2 Max)? _editorRect;

    private void DrawRouteEditor()
    {
        if (!Settings.Editor.Enable) return;

        // hide while the window would sit under an open side panel (uses last frame's rect)
        if (!(_editorRect is { } r && OverlapsSidePanel(r.Min, r.Max)))
        {
            var e = Settings.Editor;
            ImGui.SetNextWindowPos(new Vector2(e.PosX.Value, e.PosY.Value), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(920f, 760f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(480f, 300f), new Vector2(4000f, 4000f));
            ImGui.SetNextWindowBgAlpha(0.93f);

            bool open = true;
            bool vis = ImGui.Begin("Route Editor##ec_route_editor", ref open, ImGuiWindowFlags.None);
            var wp = ImGui.GetWindowPos();
            _editorRect = (wp, wp + ImGui.GetWindowSize());
            if (!open) Settings.Editor.Enable.Value = false;
            if (vis)
            {
                DrawRouteTab();
            }
            ImGui.End();
        }

        if (_pendingRouteSave)
        {
            _pendingRouteSave = false;
            SaveUserRoute();
            var sel = _pendingSelectId ?? _editorSelectedId;
            ReloadRouteFromStore(sel);
            if (_pendingSelectId != null) { _editorSelectedId = _pendingSelectId; _pendingSelectId = null; _bufForStepId = null; }
        }
    }

    // --- Route tab ----------------------------------------------------------------------------------------

    private void DrawRouteTab()
    {
        if (_routeStore == null || _routeStore.Steps.Count == 0)
        {
            ImGui.TextDisabled("(no route loaded)");
            return;
        }

        ImGui.BeginChild("##ec_tree", new Vector2(330f, 0f), ImGuiChildFlags.Border);
        DrawStepTree();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##ec_detail", new Vector2(0f, 0f), ImGuiChildFlags.Border);
        DrawStepDetail();
        ImGui.EndChild();
    }

    // collapsible Act > Area > Step list. selection is by RouteStep.Id (independent of the tracker cursor).
    private void DrawStepTree()
    {
        var steps = _routeStore!.Steps;
        string selId = EffectiveSelectedId();
        string? curId = _route.CurrentStep?.Model?.Id;

        int i = 0;
        while (i < steps.Count)
        {
            int act = steps[i].Act;
            int start = i;
            while (i < steps.Count && steps[i].Act == act) i++;

            bool actHasCurrent = curId != null;
            if (actHasCurrent)
            {
                actHasCurrent = false;
                for (int k = start; k < i; k++) if (steps[k].Id == curId) { actHasCurrent = true; break; }
            }
            ImGui.SetNextItemOpen(actHasCurrent, ImGuiCond.FirstUseEver);
            if (!ImGui.CollapsingHeader($"Act {act}##act{act}")) continue;

            string lastArea = "";
            for (int k = start; k < i; k++)
            {
                var s = steps[k];
                if (!string.Equals(s.AreaId, lastArea, StringComparison.OrdinalIgnoreCase))
                {
                    lastArea = s.AreaId;
                    ImGui.TextDisabled($"  {(string.IsNullOrEmpty(s.AreaName) ? s.AreaId : s.AreaName)}");
                }

                bool selected = s.Id == selId;
                bool isCurrent = s.Id == curId;
                string label = string.IsNullOrWhiteSpace(s.Text) ? "(no text)" : s.Text.Replace("\n", " ");
                if (s.Optional) label = "(opt) " + label;
                string prefix = isCurrent ? ">> " : "    ";

                if (isCurrent) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.47f, 0.86f, 1f, 1f));
                if (ImGui.Selectable($"{prefix}{label}##step_{s.Id}", selected))
                {
                    _editorSelectedId = s.Id;
                    _editorSelectedObjIndex = 0;
                    _bufForStepId = null;
                }
                if (isCurrent) ImGui.PopStyleColor();

                // right-click -> jump the tracker cursor onto this step (manual progress move)
                if (ImGui.BeginPopupContextItem($"##stepctx_{s.Id}"))
                {
                    if (isCurrent) ImGui.TextDisabled("(current step)");
                    else if (ImGui.MenuItem("Move progress here")) MoveProgressToStep(s.Id);
                    ImGui.EndPopup();
                }
            }
        }
    }

    // selected step id, falling back to the tracker's current step, then the first step.
    private string EffectiveSelectedId()
    {
        var steps = _routeStore?.Steps;
        if (steps == null || steps.Count == 0) return "";
        if (_editorSelectedId != null && steps.Any(s => s.Id == _editorSelectedId)) return _editorSelectedId;
        var cur = _route.CurrentStep?.Model?.Id;
        if (cur != null && steps.Any(s => s.Id == cur)) return cur;
        return steps[0].Id;
    }

    private void DrawStepDetail()
    {
        string selId = EffectiveSelectedId();
        var step = _routeStore!.Steps.FirstOrDefault(s => s.Id == selId);
        if (step == null) { ImGui.TextDisabled("(no step selected)"); return; }

        SyncStepBuffers(step);

        ImGui.TextDisabled($"id {step.Id[..Math.Min(8, step.Id.Length)]}");
        ImGui.SameLine();
        ImGui.Checkbox("Preview target on map", ref _editorPreviewTarget);
        HelpMarker("Draw a crosshair on this step's resolved guidance target on the large minimap, to confirm a path/indicator pick. Open the large map to see it.");

        ImGui.Text("Text");
        ImGui.InputTextMultiline("##step_text", ref _strStepText, 1024, new Vector2(-1f, 50f));
        if (ImGui.IsItemDeactivatedAfterEdit()) UpdateStep(step with { Text = _strStepText.Trim() });

        if (ImGui.Checkbox("Optional", ref _stepOptional)) UpdateStep(step with { Optional = _stepOptional });
        ImGui.SameLine();
        if (ImGui.Checkbox("League start", ref _stepLeagueStart)) UpdateStep(step with { LeagueStart = _stepLeagueStart });
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        if (ImGui.Combo("Complete##cw", ref _stepCompleteWhen, CompleteWhenLabels, CompleteWhenLabels.Length))
            UpdateStep(step with { CompleteWhen = (CompleteWhen)_stepCompleteWhen });

        ImGui.SetNextItemWidth(70f);
        ImGui.InputInt("Act##sa", ref _stepAct, 0);
        if (ImGui.IsItemDeactivatedAfterEdit()) UpdateStep(step with { Act = Math.Max(1, _stepAct) });
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        InputHint("AreaId##sai", "g1_2", _bufAreaId);
        if (ImGui.IsItemDeactivatedAfterEdit()) UpdateStep(step with { AreaId = ReadBuffer(_bufAreaId).ToLowerInvariant() });
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        InputHint("AreaName##san", "The Riverbank", _bufAreaName);
        if (ImGui.IsItemDeactivatedAfterEdit()) UpdateStep(step with { AreaName = ReadBuffer(_bufAreaName) });

        // step ops
        if (ImGui.Button("Add step after"))
        {
            var s = RouteEditing.SkeletonStep(step.Act, step.AreaId, step.AreaName);
            _routeStore.InsertRelative(step.Id, s, before: false);
            _pendingSelectId = s.Id; _editorSelectedObjIndex = 0; _pendingRouteSave = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
        {
            var nid = _routeStore.Duplicate(step.Id);
            if (nid != null) _pendingSelectId = nid;
            _pendingRouteSave = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Move up")) MoveStep(step, -1);
        ImGui.SameLine();
        if (ImGui.Button("Move down")) MoveStep(step, +1);
        ImGui.SameLine();
        PushDanger();
        bool deleted = ImGui.Button("Delete");
        ImGui.PopStyleColor();
        if (deleted)
        {
            _routeStore.Delete(step.Id);
            _editorSelectedId = null; _bufForStepId = null; _pendingRouteSave = true;
            return;
        }

        ImGui.Separator();
        DrawObjectiveList(step);
    }

    private void DrawObjectiveList(RouteStep step)
    {
        ImGui.Text($"Objectives ({step.Objectives.Count})");
        ImGui.SameLine();
        if (ImGui.SmallButton("+ objective"))
        {
            var objs = step.Objectives.ToList();
            objs.Add(RouteEditing.BlankObjective());
            _editorSelectedObjIndex = objs.Count - 1;
            UpdateStep(step with { Objectives = objs });
            return;
        }

        for (int i = 0; i < step.Objectives.Count; i++)
        {
            var o = step.Objectives[i];
            bool sel = i == _editorSelectedObjIndex;
            // full-width selectable's hit box covers the inline ^ v x buttons; let the buttons win the click
            ImGui.SetNextItemAllowOverlap();
            if (ImGui.Selectable($"{i}. {o.Type}: {ObjectiveSummary(o)}##objsel_{i}", sel))
            {
                _editorSelectedObjIndex = i;
                _bufForObjIndex = -1;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"^##oup{i}") && i > 0) { var l = step.Objectives.ToList(); (l[i - 1], l[i]) = (l[i], l[i - 1]); UpdateStep(step with { Objectives = l }); }
            ImGui.SameLine();
            if (ImGui.SmallButton($"v##odn{i}") && i < step.Objectives.Count - 1) { var l = step.Objectives.ToList(); (l[i + 1], l[i]) = (l[i], l[i + 1]); UpdateStep(step with { Objectives = l }); }
            ImGui.SameLine();
            PushDanger();
            bool rm = ImGui.SmallButton($"x##odel{i}");
            ImGui.PopStyleColor();
            if (rm)
            {
                var l = step.Objectives.ToList(); l.RemoveAt(i);
                if (_editorSelectedObjIndex >= l.Count) _editorSelectedObjIndex = Math.Max(0, l.Count - 1);
                UpdateStep(step with { Objectives = l });
                return;
            }
        }

        if (step.Objectives.Count == 0) { ImGui.TextDisabled("  (no objectives)"); return; }
        int idx = Math.Clamp(_editorSelectedObjIndex, 0, step.Objectives.Count - 1);
        ImGui.Separator();
        DrawObjectiveEditor(step, idx);
    }

    private void DrawObjectiveEditor(RouteStep step, int idx)
    {
        var o = step.Objectives[idx];
        SyncObjBuffers(step, idx);

        ImGui.SetNextItemWidth(160f);
        if (ImGui.Combo($"Type##ot", ref _objType, ObjTypeLabels, ObjTypeLabels.Length))
        {
            var nt = (ObjectiveType)_objType;
            var changed = o with { Type = nt };
            // proximity with distance 0 is a dead gate; seed the engine default so it actually fires
            if (nt == ObjectiveType.Proximity && changed.Distance <= 0f)
            {
                changed = changed with { Distance = AdvanceEngine.DefaultProximity };
                _objDistance = AdvanceEngine.DefaultProximity;
            }
            UpdateObjective(step, idx, changed);
        }
        HelpMarker("What completes this objective. Kill/Interact/Talk use Entities + Count; Loot uses Items; Proximity uses Entities + Distance; QuestFlag uses Flag; EnterArea uses AreaTarget.");

        ImGui.SetNextItemWidth(90f);
        ImGui.InputInt("Count##oc", ref _objCount, 0);
        if (ImGui.IsItemDeactivatedAfterEdit()) UpdateObjective(step, idx, o with { Count = Math.Max(0, _objCount) });
        HelpMarker("How many matched kills / interacts / talks complete this (Kill / Interact / Talk).");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        ImGui.InputFloat("Distance##od", ref _objDistance);
        if (ImGui.IsItemDeactivatedAfterEdit()) UpdateObjective(step, idx, o with { Distance = Math.Max(0f, _objDistance) });
        HelpMarker($"Proximity only: trigger radius in world units. Falls back to {AdvanceEngine.DefaultProximity:0} when left at 0.");

        ImGui.SetNextItemWidth(-1f);
        InputHint("Flag##of", "quest flag id (e.g. ExpeditionQuestRedValeFarrowSummoned)", _bufObjFlag);
        if (ImGui.IsItemDeactivatedAfterEdit()) { var v = ReadBuffer(_bufObjFlag); UpdateObjective(step, idx, o with { Flag = v.Length > 0 ? new Pattern(v) : null }); }
        ImGui.SameLine(); HelpMarker("QuestFlag objective: the server-side flag whose flip completes this objective. Pick from the recent-flags list below.");
        if (o.Type == ObjectiveType.QuestFlag) DrawRecentFlagsPicker(step, idx, o, asQuestFlag: true);

        ImGui.SetNextItemWidth(-1f);
        InputHint("AreaTarget##oa", "area id (e.g. g1_7)", _bufObjArea);
        if (ImGui.IsItemDeactivatedAfterEdit()) { var v = ReadBuffer(_bufObjArea); UpdateObjective(step, idx, o with { AreaTarget = v.Length > 0 ? new Pattern(v.ToLowerInvariant()) : null }); }
        ImGui.SameLine(); HelpMarker("EnterArea objective: the area id (e.g. g1_7) that completes this objective on entry.");

        ImGui.SetNextItemWidth(-1f);
        InputHint("Label##ol", "optional label shown in pickers / summaries", _bufObjLabel);
        if (ImGui.IsItemDeactivatedAfterEdit()) { var v = ReadBuffer(_bufObjLabel); UpdateObjective(step, idx, o with { Label = v.Length > 0 ? v : null }); }
        ImGui.SameLine(); HelpMarker("Friendly name for this objective. Cosmetic only, used in summaries and reports.");

        ImGui.Text("Obj note");
        HelpMarker("Freeform author note (snapshot / why). Not shown in-game.");
        ImGui.InputTextMultiline("##obj_note", ref _strObjNote, 1024, new Vector2(-1f, 30f));
        if (ImGui.IsItemDeactivatedAfterEdit()) UpdateObjective(step, idx, o with { Note = _strObjNote.Length > 0 ? _strObjNote : null });

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Entities (Kill / Interact / Talk / Proximity)")) DrawEntityList(step, idx, o);
        if (ImGui.CollapsingHeader("Items (Loot)")) DrawItemList(step, idx, o);
        if (ImGui.CollapsingHeader("Progress flags (multi-objective)")) DrawProgressFlags(step, idx, o);

        int pmSel = (int)o.Mode;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.Combo("Path mode##opm", ref pmSel, PathModeLabels, PathModeLabels.Length) && pmSel != (int)o.Mode)
            UpdateObjective(step, idx, o with { Mode = (PathMode)pmSel });
        ImGui.SameLine(); HelpMarker("Nearest: one path line to the closest target. All: a line to every target (e.g. all 3 Ancient Seals). Default Nearest.");
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Paths (ground / minimap route lines)")) DrawTargetList(step, idx, o, ChildKind.Path);
        if (ImGui.CollapsingHeader("Indicators (on-screen arrows)")) DrawTargetList(step, idx, o, ChildKind.Indicator);
        if (ImGui.CollapsingHeader("Minimap icon")) DrawMinimapSection(step, idx, o);
    }

    private void DrawEntityList(RouteStep step, int idx, Objective o)
    {
        var ents = o.Entities ?? new List<EntityMatcher>();
        for (int i = 0; i < ents.Count; i++)
        {
            ImGui.TextUnformatted($"  [{ents[i].MatchBy}] {ents[i].Match.Value}{(ents[i].Match.Regex ? " (re)" : "")}");
            ImGui.SameLine();
            PushDanger();
            bool rm = ImGui.SmallButton($"x##entrm{i}");
            ImGui.PopStyleColor();
            if (rm) { var l = ents.ToList(); l.RemoveAt(i); UpdateObjective(step, idx, o with { Entities = l }); return; }
        }
        ImGui.SetNextItemWidth(90f);
        ImGui.Combo("##entmb", ref _addEntityMatchBy, MatchByLabels, MatchByLabels.Length);
        ImGui.SameLine(); ImGui.Checkbox("re##entre", ref _addEntityRegex);
        ImGui.SameLine(); ImGui.SetNextItemWidth(-60f);
        InputHint("##entadd", "entity name or metadata path", _bufAddEntity);
        ImGui.SameLine();
        if (ImGui.Button("Add##entadd"))
        {
            var v = ReadBuffer(_bufAddEntity);
            if (v.Length > 0)
            {
                var l = (o.Entities ?? new List<EntityMatcher>()).ToList();
                l.Add(new EntityMatcher(new Pattern(v, _addEntityRegex), (MatchKind)_addEntityMatchBy));
                UpdateObjective(step, idx, o with { Entities = l });
                WriteBuffer(_bufAddEntity, "");
            }
        }

        var rows = new List<(string Label, (string Text, Action Do)[] Buttons)>();
        foreach (var (path, name, dist, alive) in NearbyEntitiesList(200f, 30))
        {
            var disp = string.IsNullOrEmpty(name) ? PathLeaf(path) : $"{name} ({dist})";
            rows.Add((disp, new (string, Action)[]
            {
                ("name", () => { if (!string.IsNullOrEmpty(name)) { WriteBuffer(_bufAddEntity, name); _addEntityMatchBy = (int)MatchKind.Name; } }),
                ("path", () => { WriteBuffer(_bufAddEntity, path); _addEntityMatchBy = (int)MatchKind.Path; }),
            }));
        }
        DrawPickerSection("Nearby entities -> add matcher", rows, 140f,
            "Populates the add row above. Edit the value if needed, then click Add.");
    }

    private void AddEntity(RouteStep step, int idx, Objective o, EntityMatcher m)
    {
        var l = (o.Entities ?? new List<EntityMatcher>()).ToList();
        l.Add(m);
        UpdateObjective(step, idx, o with { Entities = l });
    }

    private void DrawItemList(RouteStep step, int idx, Objective o)
    {
        var items = o.Items ?? new List<ItemMatcher>();
        for (int i = 0; i < items.Count; i++)
        {
            ImGui.TextUnformatted($"  {items[i].Match.Value} x{items[i].Count}");
            ImGui.SameLine();
            PushDanger();
            bool rm = ImGui.SmallButton($"x##itrm{i}");
            ImGui.PopStyleColor();
            if (rm) { var l = items.ToList(); l.RemoveAt(i); UpdateObjective(step, idx, o with { Items = l }); return; }
        }
        ImGui.SetNextItemWidth(80f);
        ImGui.InputInt("##itcount", ref _addItemCount, 0);
        ImGui.SameLine(); ImGui.SetNextItemWidth(-60f);
        InputHint("##itadd", "item name", _bufAddItem);
        ImGui.SameLine();
        if (ImGui.Button("Add##itadd"))
        {
            var v = ReadBuffer(_bufAddItem);
            if (v.Length > 0)
            {
                var l = (o.Items ?? new List<ItemMatcher>()).ToList();
                l.Add(new ItemMatcher(new Pattern(v), Math.Max(1, _addItemCount)));
                UpdateObjective(step, idx, o with { Items = l });
                WriteBuffer(_bufAddItem, "");
            }
        }
    }

    private void DrawProgressFlags(RouteStep step, int idx, Objective o)
    {
        var flags = o.ProgressFlags ?? new List<Pattern>();
        for (int i = 0; i < flags.Count; i++)
        {
            ImGui.TextUnformatted($"  {flags[i].Value}");
            ImGui.SameLine();
            PushDanger();
            bool rm = ImGui.SmallButton($"x##pfrm{i}");
            ImGui.PopStyleColor();
            if (rm) { var l = flags.ToList(); l.RemoveAt(i); UpdateObjective(step, idx, o with { ProgressFlags = l }); return; }
        }
        ImGui.SetNextItemWidth(-60f);
        InputHint("##pfadd", "quest flag id", _bufAddProg);
        ImGui.SameLine();
        if (ImGui.Button("Add##pfadd"))
        {
            var v = ReadBuffer(_bufAddProg);
            if (v.Length > 0) { AddProgressFlag(step, idx, o, v); WriteBuffer(_bufAddProg, ""); }
        }

        // QuestFlag objectives get the recent-flags picker under the Flag field instead (it sets the
        // advance Flag, not a progress flag).
        if (o.Type != ObjectiveType.QuestFlag) DrawRecentFlagsPicker(step, idx, o, asQuestFlag: false);
    }

    private void AddProgressFlag(RouteStep step, int idx, Objective o, string flag)
    {
        var l = (o.ProgressFlags ?? new List<Pattern>()).ToList();
        l.Add(new Pattern(flag));
        UpdateObjective(step, idx, o with { ProgressFlags = l });
    }

    // QuestFlag objectives have a single advance Flag, so "add" = set. write the buffer too -- it only
    // resyncs on selection change, so the Flag input would otherwise show the old value.
    private void SetObjectiveFlag(RouteStep step, int idx, Objective o, string flag)
    {
        UpdateObjective(step, idx, o with { Flag = new Pattern(flag) });
        WriteBuffer(_bufObjFlag, flag);
    }

    // recent harvested flags as a click picker. for a QuestFlag objective Add sets the advance Flag;
    // otherwise it appends a progress flag (multi-objective counting).
    private void DrawRecentFlagsPicker(RouteStep step, int idx, Objective o, bool asQuestFlag)
    {
        var rows = new List<(string Label, (string Text, Action Do)[] Buttons)>();
        for (int i = _recentHarvestedFlags.Count - 1; i >= 0; i--)
        {
            var flag = _recentHarvestedFlags[i];
            rows.Add((flag, new (string, Action)[] { ("Add", asQuestFlag
                ? () => SetObjectiveFlag(step, idx, o, flag)
                : () => AddProgressFlag(step, idx, o, flag)) }));
        }
        DrawPickerSection(
            asQuestFlag ? "Recent flags -> set quest flag" : "Recent flags -> add progress flag",
            rows, 120f,
            asQuestFlag
                ? "Quest flags that flipped recently (newest first). Click Add to set this objective's advance flag."
                : "Quest flags that flipped recently (newest first). Click Add to append as a progress flag.",
            "no flags harvested yet this session");
    }

    // shared Paths / Indicators editor (both are lists of Target wrapped in GuidePath / Indicator).
    private void DrawTargetList(RouteStep step, int idx, Objective o, ChildKind kind)
    {
        var targets = (kind == ChildKind.Path
            ? (o.Paths ?? new List<GuidePath>()).Select(p => p.Target)
            : (o.Indicators ?? new List<Indicator>()).Select(p => p.Target)).ToList();

        // LivingOnly is honored on the Indicator channel only (the arrow); the Paths channel ignores it.
        bool indKind = kind == ChildKind.Indicator;

        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            ImGui.TextUnformatted($"  [{t.Kind}] {t.Match.Value}{(t.Kind == TargetKind.Entity ? $" ({t.MatchBy})" : "")}{(t.LivingOnly ? " (alive)" : "")}{(t.Match.Regex ? " (re)" : "")}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"^##t{kind}{i}") && i > 0) { UpdateObjective(step, idx, RouteEditing.MoveChild(o, kind, i, -1)); return; }
            ImGui.SameLine();
            if (ImGui.SmallButton($"v##t{kind}{i}") && i < targets.Count - 1) { UpdateObjective(step, idx, RouteEditing.MoveChild(o, kind, i, +1)); return; }
            ImGui.SameLine();
            PushDanger();
            bool rm = ImGui.SmallButton($"x##t{kind}{i}");
            ImGui.PopStyleColor();
            if (rm) { UpdateObjective(step, idx, RouteEditing.RemoveAt(o, kind, i)); return; }
            // toggle "alive" (LivingOnly) on an indicator Entity target in place
            if (indKind && t.Kind == TargetKind.Entity)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"{(t.LivingOnly ? "alive*" : "alive")}##tliv{kind}{i}"))
                { UpdateObjective(step, idx, RouteEditing.ReplaceTarget(o, kind, i, t with { LivingOnly = !t.LivingOnly })); return; }
            }
        }

        // add row (per-kind scratch)
        bool isPath = kind == ChildKind.Path;
        ref int akind = ref (isPath ? ref _addPathKind : ref _addIndKind);
        ref int amb = ref (isPath ? ref _addPathMatchBy : ref _addIndMatchBy);
        ref bool are = ref (isPath ? ref _addPathRegex : ref _addIndRegex);
        var abuf = isPath ? _bufAddPath : _bufAddInd;

        ImGui.SetNextItemWidth(80f);
        ImGui.Combo($"##tk{kind}", ref akind, TargetKindLabels, TargetKindLabels.Length);
        ImGui.SameLine(); ImGui.SetNextItemWidth(80f);
        ImGui.Combo($"##tmb{kind}", ref amb, MatchByLabels, MatchByLabels.Length);
        ImGui.SameLine(); ImGui.Checkbox($"re##tre{kind}", ref are);
        // alive (LivingOnly): only meaningful for indicator Entity targets, so only shown there
        if (indKind) { ImGui.SameLine(); ImGui.Checkbox($"alive##tliv{kind}", ref _addIndLiving); }
        ImGui.SameLine(); ImGui.SetNextItemWidth(-60f);
        InputHint($"##tadd{kind}", isPath ? "tile / entity / room pattern" : "indicator target pattern", abuf);
        ImGui.SameLine();
        if (ImGui.Button($"Add##tadd{kind}"))
        {
            var v = ReadBuffer(abuf);
            if (v.Length > 0)
            {
                AddTarget(step, idx, o, kind, new Target((TargetKind)akind, new Pattern(v, are), (MatchKind)amb, indKind && _addIndLiving));
                WriteBuffer(abuf, "");
            }
        }

        // live target pickers (room tiles / Radar targets / nearby entities), shared with the MinimapIcon target.
        // each button populates THIS add row (value + kind + matchBy + regex + living); the user edits then Adds.
        DrawTargetPickers(kind.ToString(), indKind && _addIndLiving, t =>
        {
            WriteBuffer(abuf, t.Match.Value);
            if (isPath) { _addPathKind = (int)t.Kind; _addPathMatchBy = (int)t.MatchBy; _addPathRegex = t.Match.Regex; }
            else { _addIndKind = (int)t.Kind; _addIndMatchBy = (int)t.MatchBy; _addIndRegex = t.Match.Regex; _addIndLiving = t.LivingOnly; }
        });
    }

    private void AddTarget(RouteStep step, int idx, Objective o, ChildKind kind, Target t)
    {
        var updated = kind == ChildKind.Path
            ? RouteEditing.AddPath(o, new GuidePath(t))
            : RouteEditing.AddIndicator(o, new Indicator(t));
        UpdateObjective(step, idx, updated);
    }

    // the three live target pickers (nearby room tiles -> Room; Radar targets -> Tile/Entity; nearby
    // entities -> Entity). shared by the Paths/Indicators list editor and the MinimapIcon target; each
    // button populates the caller's add row (value + kind + matchBy + regex + living), the user edits + Adds.
    private void DrawTargetPickers(string scope, bool living, Action<Target> populate)
    {
        var roomRows = new List<(string, (string, Action)[])>();
        foreach (var name in NearbyRoomTilesList(30))
            roomRows.Add((name, new (string, Action)[] { ("Room", () => populate(new Target(TargetKind.Room, new Pattern(name)))) }));
        DrawPickerSection($"Nearby room tiles -> {scope} (Room)", roomRows, 120f,
            "Populates the add row above with a Room target. Edit then click Add.");

        var radarRows = new List<(string, (string, Action)[])>();
        foreach (var (leaf, fullPath, name, dist, rkind) in NearbyRadarTargetsList(30))
        {
            var disp = string.IsNullOrEmpty(name) ? leaf : name;
            radarRows.Add(($"[{rkind}] {disp} ({dist})", new (string, Action)[]
            {
                ("Tile", () => populate(new Target(TargetKind.Tile, new Pattern(fullPath)))),
                ("Entity", () => populate(new Target(TargetKind.Entity, new Pattern(fullPath), MatchKind.Path, living))),
            }));
        }
        DrawPickerSection($"Radar targets -> {scope} (Tile / Entity)", radarRows, 140f,
            "Populates the add row above. 'Tile' uses the full path for ClusterTarget; 'Entity' uses the full path matched by path. Edit then click Add.");

        // zone-wide Radar targets.json for this area plus the global "*" block: canonical authored targets incl.
        // far transitions you aren't standing next to (the live list above can't see those). each row carries
        // its kind -- tile -> Tile (ClusterTarget), room-scoped -> Room (AreaGraph room), entity -> Entity.
        var fileRows = new List<(string, (string, Action)[])>();
        foreach (var p in RadarFileTargetsList())
        {
            var pick = p;
            fileRows.Add(($"[{pick.Kind}] {pick.Label}", new (string, Action)[]
            {
                (pick.Kind.ToString(), () => populate(new Target(pick.Kind, new Pattern(pick.Match), pick.MatchBy,
                    pick.Kind == TargetKind.Entity && living))),
            }));
        }
        DrawPickerSection($"Radar targets.json (zone-wide) -> {scope} (Tile / Room / Entity)", fileRows, 140f,
            "Populates the add row above from Radar's authored targets: tiles -> Tile, room-scoped -> Room, entity -> Entity. Edit then click Add.",
            "no Radar targets.json entry for this area");

        var entRows = new List<(string, (string, Action)[])>();
        foreach (var (path, name, dist, alive) in NearbyEntitiesList(200f, 30))
        {
            var disp = string.IsNullOrEmpty(name) ? PathLeaf(path) : $"{name} ({dist})";
            entRows.Add((disp, new (string, Action)[]
            {
                ("Entity", () => populate(new Target(TargetKind.Entity, new Pattern(path), MatchKind.Path, living))),
            }));
        }
        DrawPickerSection($"Nearby entities -> {scope} (Entity){(living ? " [alive]" : "")}", entRows, 140f,
            "Populates the add row above with the full metadata path. Edit to a partial match or regex, then click Add.");
    }

    // list editor for an objective's MinimapIcons (multiple, like Paths/Indicators). each row edits one icon's
    // sprite/tint/size/target inline; the add area appends a new default-sprite icon carrying the picked target.
    private void DrawMinimapSection(RouteStep step, int idx, Objective o)
    {
        var icons = o.MinimapIcons ?? new List<MinimapIcon>();
        if (icons.Count == 0) ImGui.TextDisabled("(no icons)");

        for (int i = 0; i < icons.Count; i++)
        {
            var mmi = icons[i];
            var icon = SpriteAtlas.Parse(mmi.IconKey);

            // row: sprite picker + target summary + reorder / remove / clear-target
            IconPicker($"{step.Id}_{idx}_{i}", icon, ic =>
                UpdateObjective(step, idx, RouteEditing.UpdateMinimapIcon(o, i, mmi with { IconKey = ic.ToString() })));
            ImGui.SameLine();
            ImGui.TextUnformatted(mmi.Target != null
                ? $"[{mmi.Target.Kind}] {mmi.Target.Match.Value}{(mmi.Target.Kind == TargetKind.Entity ? $" ({mmi.Target.MatchBy})" : "")}{(mmi.Target.LivingOnly ? " (alive)" : "")}{(mmi.Target.Match.Regex ? " (re)" : "")}"
                : "(no target -> 1st indicator/path)");
            ImGui.SameLine();
            if (ImGui.SmallButton($"^##mmi{i}") && i > 0) { UpdateObjective(step, idx, RouteEditing.MoveChild(o, ChildKind.MinimapIcon, i, -1)); return; }
            ImGui.SameLine();
            if (ImGui.SmallButton($"v##mmi{i}") && i < icons.Count - 1) { UpdateObjective(step, idx, RouteEditing.MoveChild(o, ChildKind.MinimapIcon, i, +1)); return; }
            ImGui.SameLine();
            PushDanger();
            bool rm = ImGui.SmallButton($"x##mmi{i}");
            ImGui.PopStyleColor();
            if (rm) { UpdateObjective(step, idx, RouteEditing.RemoveAt(o, ChildKind.MinimapIcon, i)); return; }
            if (mmi.Target != null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"clear target##mmi{i}"))
                    UpdateObjective(step, idx, RouteEditing.UpdateMinimapIcon(o, i, mmi with { Target = null }));
                // toggle "alive" (LivingOnly) on an Entity icon target in place
                if (mmi.Target.Kind == TargetKind.Entity)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"{(mmi.Target.LivingOnly ? "alive*" : "alive")}##mmiliv{i}"))
                        UpdateObjective(step, idx, RouteEditing.UpdateMinimapIcon(o, i,
                            mmi with { Target = mmi.Target with { LivingOnly = !mmi.Target.LivingOnly } }));
                }
            }

            // tint - commit on any change (the ColorEdit popup doesn't reliably fire IsItemDeactivatedAfterEdit;
            // the per-frame save during a drag coalesces through the editor's deferred-save drain)
            var col = UnpackTint(mmi.Tint);
            ImGui.SetNextItemWidth(200f);
            if (ImGui.ColorEdit4($"Tint##mmitint{i}", ref col))
            {
                var packed = PackTint(col);
                if (packed != mmi.Tint)
                    UpdateObjective(step, idx, RouteEditing.UpdateMinimapIcon(o, i, mmi with { Tint = packed }));
            }

            // size - per-icon override; 0 = use the global MinimapIcons.IconSize default
            float sizeVal = mmi.Size ?? 0f;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.SliderFloat($"Size (0=default)##mmisize{i}", ref sizeVal, 0f, 128f))
            {
                float? newSize = sizeVal >= 1f ? sizeVal : null;
                if (newSize != mmi.Size)
                    UpdateObjective(step, idx, RouteEditing.UpdateMinimapIcon(o, i, mmi with { Size = newSize }));
            }
            ImGui.Separator();
        }

        // add a new icon: manual target row + the shared pickers, each appends a default-sprite icon
        ImGui.TextDisabled("Add icon (default sprite + gold; tweak inline above):");
        ImGui.SetNextItemWidth(80f);
        ImGui.Combo("##mmik2", ref _mmiKind, TargetKindLabels, TargetKindLabels.Length);
        ImGui.SameLine(); ImGui.SetNextItemWidth(80f);
        ImGui.Combo("##mmimb", ref _mmiMatchBy, MatchByLabels, MatchByLabels.Length);
        ImGui.SameLine(); ImGui.Checkbox("re##mmi", ref _mmiRegex);
        ImGui.SameLine(); ImGui.Checkbox("alive##mmiliv", ref _mmiLiving);
        ImGui.SameLine(); ImGui.SetNextItemWidth(-90f);
        InputHint("##mmipat", "target pattern (blank = anchor to 1st indicator/path)", _bufMmiPat);
        ImGui.SameLine();
        if (ImGui.Button("Add icon##mmi"))
        {
            var pat = ReadBuffer(_bufMmiPat);
            Target? t = pat.Length > 0 ? new Target((TargetKind)_mmiKind, new Pattern(pat, _mmiRegex), (MatchKind)_mmiMatchBy, _mmiLiving) : null;
            UpdateObjective(step, idx, RouteEditing.AddMinimapIcon(o, NewMinimapIcon(t)));
            WriteBuffer(_bufMmiPat, "");
        }

        DrawTargetPickers("MinimapIcon", _mmiLiving, t =>
        {
            WriteBuffer(_bufMmiPat, t.Match.Value);
            _mmiKind = (int)t.Kind;
            _mmiMatchBy = (int)t.MatchBy;
            _mmiRegex = t.Match.Regex;
            _mmiLiving = t.LivingOnly;
        });
    }

    // a freshly-added icon: default sprite, gold tint, global size, carrying the picked target (null = fallback anchor).
    private static MinimapIcon NewMinimapIcon(Target? t) =>
        new(SpriteIcon.Exclamation.ToString(), t, MinimapIcon.GoldDefault, null);

    // popup grid icon picker over the desaturated atlas (already loaded as IndicatorTexture).
    private void IconPicker(string id, SpriteIcon current, Action<SpriteIcon> set)
    {
        var tex = Graphics.GetTextureId(IndicatorTexture);
        var white = new Vector4(1, 1, 1, 1);
        var (cu0, cu1) = SpriteAtlas.GetUVPair(current);
        string popup = $"mmipick_{id}";
        if (tex != IntPtr.Zero)
        {
            if (ImGui.ImageButton($"##mmibtn_{id}", tex, new Vector2(20, 20), cu0, cu1, Vector4.Zero, white))
                ImGui.OpenPopup(popup);
        }
        else if (ImGui.Button($"{(int)current}##mmibtn_{id}", new Vector2(24, 20)))
            ImGui.OpenPopup(popup);

        if (ImGui.BeginPopup(popup))
        {
            for (int i = 0; i < 48; i++)
            {
                var ic = (SpriteIcon)i;
                var (u0, u1) = SpriteAtlas.GetUVPair(ic);
                bool cur = ic == current;
                if (cur) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1, 0, 1, 1));
                bool clicked = tex != IntPtr.Zero
                    ? ImGui.ImageButton($"##mmip_{id}_{i}", tex, new Vector2(32, 32), u0, u1, Vector4.Zero, white)
                    : ImGui.Button($"{i}##mmip_{id}_{i}", new Vector2(36, 36));
                if (cur) ImGui.PopStyleColor();
                if (clicked) { set(ic); ImGui.CloseCurrentPopup(); }
                if (i % SpriteAtlas.Columns != SpriteAtlas.Columns - 1) ImGui.SameLine();
            }
            ImGui.EndPopup();
        }
    }

    // packed ARGB (0xAARRGGBB) <-> ImGui RGBA Vector4.
    private static Vector4 UnpackTint(uint argb) =>
        new Vector4(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);
    private static uint PackTint(Vector4 c) =>
        ((uint)(c.W * 255) << 24) | ((uint)(c.X * 255) << 16) | ((uint)(c.Y * 255) << 8) | (uint)(c.Z * 255);

    // --- commit helpers -----------------------------------------------------------------------------------

    private void UpdateStep(RouteStep updated)
    {
        _routeStore!.Update(updated);
        _pendingRouteSave = true;
    }

    private void UpdateObjective(RouteStep step, int idx, Objective o)
    {
        var objs = step.Objectives.ToList();
        if (idx < 0 || idx >= objs.Count) return;
        objs[idx] = o;
        UpdateStep(step with { Objectives = objs });
    }

    // manual progress move from the editor's step tree: re-point the tracker cursor at this step by Id and
    // persist. matching on Model.Id only hits real steps (synth headers carry a null model), so SetCurrent
    // never lands on a zone label. allows moving backward too, unlike the auto-advance gates.
    private void MoveProgressToStep(string stepId)
    {
        var steps = _route.Steps;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Model?.Id != stepId) continue;
            int before = _route.Current;
            _route.SetCurrent(i);
            if (_route.Current < before) _holdAutoAdvanceUntilZone = true;   // backward set sticks until next zone
            MaybeSaveProgress();
            var txt = _route.CurrentStep?.DisplayText ?? "";
            ShowToast(string.IsNullOrEmpty(txt) ? "Progress moved" : $"Progress -> {txt}", ToastLevel.Success);
            return;
        }
    }

    private void MoveStep(RouteStep step, int delta)
    {
        var steps = _routeStore!.Steps;
        int at = -1;
        for (int i = 0; i < steps.Count; i++) if (steps[i].Id == step.Id) { at = i; break; }
        if (at < 0) return;
        int to = at + delta;
        if (to < 0 || to >= steps.Count) return;
        _routeStore.Move(step.Id, to);
        _pendingRouteSave = true;
    }

    // --- buffer sync --------------------------------------------------------------------------------------

    private void SyncStepBuffers(RouteStep step)
    {
        if (_bufForStepId == step.Id) return;
        _bufForStepId = step.Id;
        _bufForObjIndex = -1;
        _strStepText = step.Text ?? "";
        WriteBuffer(_bufAreaId, step.AreaId ?? "");
        WriteBuffer(_bufAreaName, step.AreaName ?? "");
        _stepAct = step.Act;
        _stepOptional = step.Optional;
        _stepLeagueStart = step.LeagueStart;
        _stepCompleteWhen = (int)step.CompleteWhen;
    }

    private void SyncObjBuffers(RouteStep step, int idx)
    {
        if (_bufForStepId == step.Id && _bufForObjIndex == idx) return;
        _bufForObjIndex = idx;
        var o = step.Objectives[idx];
        _objType = (int)o.Type;
        _objCount = o.Count;
        _objDistance = o.Distance > 0f || o.Type != ObjectiveType.Proximity ? o.Distance : AdvanceEngine.DefaultProximity;
        WriteBuffer(_bufObjFlag, o.Flag?.Value ?? "");
        WriteBuffer(_bufObjArea, o.AreaTarget?.Value ?? "");
        WriteBuffer(_bufObjLabel, o.Label ?? "");
        _strObjNote = o.Note ?? "";
    }

    // --- shared imgui helpers ----------------------------------------------------------------------------

    private static bool TabButton(string label, bool active)
    {
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.45f, 0.75f, 1f));
        bool c = ImGui.Button(label);
        if (active) ImGui.PopStyleColor();
        return c;
    }

    private static void PushDanger() => ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f));

    private static string ObjectiveSummary(Objective o) => o.Type switch
    {
        ObjectiveType.Kill or ObjectiveType.Interact or ObjectiveType.Talk or ObjectiveType.Proximity =>
            o.Entities is { Count: > 0 } ? o.Entities[0].Match.Value : "(no entity)",
        ObjectiveType.QuestFlag => o.Flag?.Value ?? "(no flag)",
        ObjectiveType.EnterArea => o.AreaTarget?.Value ?? "(no area)",
        ObjectiveType.Loot => o.Items is { Count: > 0 } ? o.Items[0].Match.Value : "(no item)",
        _ => o.Label ?? "",
    };

    private static string Trunc(string s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n] + "...";
    }

    // last path segment minus a @NN spawn-variant suffix, for entity-path matchers.
    private static string PathLeaf(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        int at = leaf.IndexOf('@');
        return at >= 0 ? leaf[..at] : leaf;
    }

    private static void WriteBuffer(byte[] buf, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        int len = Math.Min(bytes.Length, buf.Length - 1);
        Array.Clear(buf, 0, buf.Length);
        Array.Copy(bytes, buf, len);
    }

    private static string ReadBuffer(byte[] buf)
    {
        int end = Array.IndexOf(buf, (byte)0);
        if (end < 0) end = buf.Length;
        return Encoding.UTF8.GetString(buf, 0, end).Trim();
    }

    // ghost-text placeholder for our byte[] inputs. ImGui.NET 1.90's InputTextWithHint only takes a
    // ref string, so we keep the buffer model and paint the hint ourselves when the field is empty + idle.
    private static bool InputHint(string label, string hint, byte[] buf)
    {
        var pos = ImGui.GetCursorScreenPos();
        bool changed = ImGui.InputText(label, buf, (uint)buf.Length);
        if (buf[0] == 0 && !ImGui.IsItemActive())
        {
            var pad = ImGui.GetStyle().FramePadding;
            ImGui.GetWindowDrawList().AddText(new Vector2(pos.X + pad.X, pos.Y + pad.Y),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), hint);
        }
        return changed;
    }

    private static void ShowTooltip(string desc)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
        ImGui.TextUnformatted(desc);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static void HelpMarker(string desc)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered()) ShowTooltip(desc);
    }

    private static void DrawPickerSection(string title, IReadOnlyList<(string Label, (string Text, Action Do)[] Buttons)> rows, float height, string? help = null, string? emptyHint = null)
    {
        // nested subheader style: indented + a cool muted tint so these read as children of the section
        // above them, not as siblings of the parent (white) child headers.
        ImGui.Indent(12f);
        try
        {
            if (rows.Count == 0)
            {
                if (emptyHint == null) return;
                ImGui.TextDisabled($"{title}  (0)");
                if (help != null && ImGui.IsItemHovered()) ShowTooltip(help);
                ImGui.TextDisabled("  " + emptyHint);
                return;
            }
            ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.18f, 0.30f, 0.40f, 0.55f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.26f, 0.42f, 0.55f, 0.80f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.30f, 0.48f, 0.62f, 0.90f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.68f, 0.84f, 1f, 1f));
            bool open = ImGui.CollapsingHeader($"{title}  ({rows.Count})");
            ImGui.PopStyleColor(4);
            if (help != null && ImGui.IsItemHovered()) ShowTooltip(help);
            if (!open) return;
            ImGui.BeginChild("##picker_" + title, new Vector2(-1f, height), ImGuiChildFlags.Border);
            for (int i = 0; i < rows.Count; i++)
            {
                var btns = rows[i].Buttons;
                for (int b = 0; b < btns.Length; b++)
                {
                    if (ImGui.Button($"{btns[b].Text}##{title}_{i}_{b}")) btns[b].Do();
                    ImGui.SameLine();
                }
                ImGui.TextUnformatted(rows[i].Label);
            }
            ImGui.EndChild();
        }
        finally { ImGui.Unindent(12f); }
    }
}
