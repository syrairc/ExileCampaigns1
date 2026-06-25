using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ExileCampaigns.Guide;
using ImGuiNET;

namespace ExileCampaigns;

// floating dev triage panel (Settings.Dev.ShowTriageButtons). quick-add steps/objectives seeded from live
// state, plus fast-path controls: move/delete the current step, bind EnterArea/quest-flag advances, and
// set paths from the zone's Radar targets.
public partial class ExileCampaigns
{
    private bool _triageDeleteConfirm;
    private string? _triageLastStepId;
    private string _triageRenameBuf = "";
    private string? _triageRenameTargetId;

    // window rect captured last frame, so the hide test can run before Begin this frame.
    private (Vector2 Min, Vector2 Max)? _triageRect;

    private void DrawTriageOverlay()
    {
        if (!Settings.Dev.ShowTriageButtons) return;

        // hide while the window would sit under an open side panel (uses last frame's rect)
        if (_triageRect is { } qr && OverlapsSidePanel(qr.Min, qr.Max)) return;

        var t = Settings.Triage;
        ImGui.SetNextWindowPos(new Vector2(t.PosX.Value, t.PosY.Value), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(260f, 0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.9f);

        bool open = true;
        bool vis = ImGui.Begin("Route Quick Edit Panel##ec_triage", ref open);
        var twp = ImGui.GetWindowPos();
        _triageRect = (twp, twp + ImGui.GetWindowSize());
        if (!open) Settings.Dev.ShowTriageButtons.Value = false;
        if (vis)
        {
            var cur = _route.CurrentStep;
            string? curId = cur?.Model?.Id;

            // reset delete confirm when the tracker moves to a different step
            if (curId != _triageLastStepId) { _triageDeleteConfirm = false; _triageLastStepId = curId; }

            ImGui.TextDisabled(cur != null ? Trunc(cur.DisplayText, 32) : "(no current step)");
            ImGui.Separator();

            bool hasStep = curId != null;
            if (!hasStep) ImGui.BeginDisabled();

            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float avail = ImGui.GetContentRegionAvail().X;
            var hw = new Vector2((avail - spacing) / 2f, 0f);
            var tw = new Vector2((avail - spacing * 2f) / 3f, 0f);

            if (ImGui.Button("Add Step Before", hw)) { _triageDeleteConfirm = false; AddTriageStep(curId!, before: true); }
            ImGui.SameLine();
            if (ImGui.Button("Add Step After", hw)) { _triageDeleteConfirm = false; AddTriageStep(curId!, before: false); }

            if (ImGui.Button("Add Objective", hw)) { _triageDeleteConfirm = false; AddTriageObjective(curId!); }
            ImGui.SameLine();
            if (ImGui.Button("Waypoint Before", hw)) { _triageDeleteConfirm = false; AddWaypointStepBefore(curId!); }

            if (ImGui.Button("Rename##rn", new Vector2(-1f, 0f)))
            {
                _triageDeleteConfirm = false;
                _triageRenameTargetId = curId;
                _triageRenameBuf = _routeStore?.Steps.FirstOrDefault(s => s.Id == curId)?.Text ?? "";
                ImGui.OpenPopup("Rename Step##ec_rnpop");
            }

            if (ImGui.Button("Move Up", tw) && curId != null) { _triageDeleteConfirm = false; MoveCurrentStep(curId, -1); }
            ImGui.SameLine();
            if (ImGui.Button("Move Down", tw) && curId != null) { _triageDeleteConfirm = false; MoveCurrentStep(curId, +1); }
            ImGui.SameLine();
            if (_triageDeleteConfirm)
            {
                PushDanger();
                if (ImGui.Button("Confirm?", tw) && curId != null) { _triageDeleteConfirm = false; DeleteCurrentStep(curId); }
                ImGui.PopStyleColor();
            }
            else
            {
                PushDanger();
                if (ImGui.Button("Delete", tw)) _triageDeleteConfirm = true;
                ImGui.PopStyleColor();
            }

            ImGui.Separator();

            // one-click advance bind: set the current step to EnterArea -> the area you're standing in.
            bool hasArea = !string.IsNullOrEmpty(_areaId);
            if (!hasArea) ImGui.BeginDisabled();
            if (ImGui.Button($"EnterArea -> {Trunc(_areaId, 14)}##ec_enterarea", new Vector2(-1f, 0f)) && curId != null)
                BindCurrentStepEnterArea(curId, _areaId);
            if (!hasArea) ImGui.EndDisabled();
            ImGui.SameLine();
            HelpMarker("Set the current step's advance to EnterArea matching the area you're in now "
                + "(CompleteWhen Any, existing guidance kept).");

            if (!hasStep) ImGui.EndDisabled();

            DrawRadarPathsPanel(curId, hasStep);
            DrawRecentFlagsPanel(curId, hasStep);
            DrawNearbyEntitiesPanel(curId, hasStep);

            DrawRenamePopup();
        }
        ImGui.End();
    }

    private void DrawRenamePopup()
    {
        bool popupOpen = true;
        if (!ImGui.BeginPopupModal("Rename Step##ec_rnpop", ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere(0);
        ImGui.SetNextItemWidth(320f);
        ImGui.InputText("##rnbuf", ref _triageRenameBuf, 512);

        bool confirm = ImGui.Button("OK##rnok") || ImGui.IsKeyPressed(ImGuiKey.Enter);
        ImGui.SameLine();
        bool cancel = ImGui.Button("Cancel##rncancel");

        if (confirm && _triageRenameTargetId != null)
        {
            ApplyStepRename(_triageRenameTargetId, _triageRenameBuf);
            ImGui.CloseCurrentPopup();
        }
        else if (cancel || (!confirm && ImGui.IsKeyPressed(ImGuiKey.Escape)))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    // --- Radar paths picker -------------------------------------------------------------------------------

    // zone-wide Radar targets as path pickers. Add appends to the current objective's Paths; Set replaces them.
    // targets the editor's selected objective index (clamped) within the current step, so pick the right objective
    // in the editor then click Add/Set here.
    private void DrawRadarPathsPanel(string? curId, bool hasStep)
    {
        ImGui.Separator();
        ImGui.TextDisabled("Radar paths (zone)");
        ImGui.SameLine();
        HelpMarker("Radar's authored targets for this area + global ones. 'Add' appends a Path to the "
            + "currently selected objective (editor); 'Set' replaces all its Paths.");

        var picks = RadarFileTargetsList();
        ImGui.BeginChild("##ec_triage_radar", new Vector2(0f, 120f), ImGuiChildFlags.Border);
        if (picks.Count == 0)
        {
            ImGui.TextDisabled("(no Radar targets for this area)");
        }
        else
        {
            if (!hasStep) ImGui.BeginDisabled();
            for (int i = 0; i < picks.Count; i++)
            {
                var p = picks[i];
                bool add = ImGui.SmallButton($"Add##rpa{i}");
                ImGui.SameLine();
                bool set = ImGui.SmallButton($"Set##rps{i}");
                ImGui.SameLine();
                ImGui.TextUnformatted($"[{p.Kind}] {p.Label}");
                if (add && curId != null) AddRadarPathToObjective(curId, p, replace: false);
                if (set && curId != null) AddRadarPathToObjective(curId, p, replace: true);
            }
            if (!hasStep) ImGui.EndDisabled();
        }
        ImGui.EndChild();
    }

    // the editor-selected objective on `step`, seeding a blank one when the step has none so quick-edit
    // add/set actions never no-op on an empty step. returns the working list + chosen index.
    private static (List<Objective> Objs, int Idx) SelectedObjectiveList(RouteStep step, int selIndex)
    {
        var objs = step.Objectives.ToList();
        if (objs.Count == 0) objs.Add(RouteEditing.BlankObjective());
        int idx = System.Math.Clamp(selIndex, 0, objs.Count - 1);
        return (objs, idx);
    }

    // append or replace the current objective's Paths with a Radar pick.
    private void AddRadarPathToObjective(string curId, RadarTargetsFile.Pick pick, bool replace)
    {
        if (_routeStore == null) return;
        var step = _routeStore.Steps.FirstOrDefault(s => s.Id == curId);
        if (step == null) return;
        var (objs, objIdx) = SelectedObjectiveList(step, _editorSelectedObjIndex);
        var o = objs[objIdx];
        var newPath = new GuidePath(new Target(pick.Kind, new Pattern(pick.Match), pick.MatchBy));
        var updated = replace
            ? o with { Paths = new System.Collections.Generic.List<GuidePath> { newPath } }
            : RouteEditing.AddPath(o, newPath);
        objs[objIdx] = updated;
        _routeStore.Update(step with { Objectives = objs });
        SaveUserRoute();
        ReloadRouteFromStore(curId);
        _bufForStepId = null;
        ShowToast(replace ? $"Path set: {Trunc(pick.Label, 22)}" : $"Path added: {Trunc(pick.Label, 20)}");
    }

    // --- recent-flags quick-bind --------------------------------------------------------------------------

    // last 5 quest-flag flips (newest first) with a one-click Set: append a QuestFlag advance objective to the
    // current step (CompleteWhen Any, existing guidance preserved). the ring is kept filled whenever this panel
    // is open (HarvestQuestFlags runs on ShowTriageButtons too), so this works without the Log-quest-flags toggle.
    private void DrawRecentFlagsPanel(string? curId, bool hasStep)
    {
        ImGui.Separator();
        ImGui.TextDisabled("Recent flags");
        ImGui.SameLine();
        HelpMarker("Quest flags that flipped recently (newest first). Set appends a QuestFlag advance objective "
            + "to the current step and switches it to CompleteWhen Any, keeping existing guidance.");

        ImGui.BeginChild("##ec_triage_flags", new Vector2(0f, 118f), ImGuiChildFlags.Border);
        if (_recentFlagsTimed.Count == 0)
        {
            ImGui.TextDisabled("(no flags seen yet)");
        }
        else
        {
            int shown = 0;
            for (int i = _recentFlagsTimed.Count - 1; i >= 0 && shown < 5; i--, shown++)
            {
                var (flag, time) = _recentFlagsTimed[i];
                if (!hasStep) ImGui.BeginDisabled();
                bool set = ImGui.SmallButton($"Set##rf{i}");
                if (!hasStep) ImGui.EndDisabled();
                if (set && curId != null) BindCurrentStepQuestFlag(curId, flag);
                ImGui.SameLine();
                ImGui.TextUnformatted($"{time}  {Trunc(flag, 26)}");
            }
        }
        ImGui.EndChild();
    }

    // --- nearby-entities quick guidance -------------------------------------------------------------------

    // live nearby entities with a one-click Set/Add that writes the entity into BOTH the current objective's
    // indicator and its minimap icon. matched by metadata path, LivingOnly off. acts on the editor's selected
    // objective of the current step, same as the radar/flags panels.
    private void DrawNearbyEntitiesPanel(string? curId, bool hasStep)
    {
        ImGui.Separator();
        ImGui.TextDisabled("Nearby entities");
        ImGui.SameLine();
        HelpMarker("Live entities near you. Set replaces the current objective's indicator + minimap icon with "
            + "this entity; Add appends them. Path+ appends a Path to this entity; Path= replaces all Paths. "
            + "Prox makes the objective a Proximity gate on this entity, radius = your current distance plus 20%. "
            + "Matched by metadata path.");

        ImGui.BeginChild("##ec_triage_ents", new Vector2(0f, 118f), ImGuiChildFlags.Border);
        var ents = NearbyEntitiesList(200f, 15);
        if (ents.Count == 0)
        {
            ImGui.TextDisabled("(no entities nearby)");
        }
        else
        {
            if (!hasStep) ImGui.BeginDisabled();
            for (int i = 0; i < ents.Count; i++)
            {
                var (path, name, dist, _) = ents[i];
                bool set = ImGui.SmallButton($"Set##nea_s{i}");
                ImGui.SameLine();
                bool add = ImGui.SmallButton($"Add##nea_a{i}");
                ImGui.SameLine();
                bool pathAdd = ImGui.SmallButton($"Path+##nea_p{i}");
                ImGui.SameLine();
                bool pathSet = ImGui.SmallButton($"Path=##nea_ps{i}");
                ImGui.SameLine();
                bool prox = ImGui.SmallButton($"Prox##nea_px{i}");
                ImGui.SameLine();
                ImGui.TextUnformatted(string.IsNullOrEmpty(name) ? PathLeaf(path) : $"{name} ({dist})");
                if (set && curId != null) SetEntityGuidance(curId, path, name, replace: true);
                if (add && curId != null) SetEntityGuidance(curId, path, name, replace: false);
                if (pathAdd && curId != null) AddEntityPathToObjective(curId, path, name, replace: false);
                if (pathSet && curId != null) AddEntityPathToObjective(curId, path, name, replace: true);
                if (prox && curId != null) SetProximityGuidance(curId, path, name, dist);
            }
            if (!hasStep) ImGui.EndDisabled();
        }
        ImGui.EndChild();
    }

    // write the picked entity into the current objective's indicator + minimap icon. replace=Set (sole entry
    // on each channel), replace=false=Add (append). entity matched by path, default sprite for the icon.
    private void SetEntityGuidance(string curId, string path, string name, bool replace)
    {
        if (_routeStore == null) return;
        var step = _routeStore.Steps.FirstOrDefault(s => s.Id == curId);
        if (step == null) return;
        var (objs, objIdx) = SelectedObjectiveList(step, _editorSelectedObjIndex);
        var o = objs[objIdx];

        var t = new Target(TargetKind.Entity, new Pattern(path), MatchKind.Path, false);
        var updated = replace
            ? o with { Indicators = new List<Indicator> { new(t) }, MinimapIcons = new List<MinimapIcon> { NewMinimapIcon(t) } }
            : RouteEditing.AddMinimapIcon(RouteEditing.AddIndicator(o, new Indicator(t)), NewMinimapIcon(t));

        objs[objIdx] = updated;
        _routeStore.Update(step with { Objectives = objs });
        SaveUserRoute();
        ReloadRouteFromStore(curId);
        _bufForStepId = null;
        var lbl = string.IsNullOrEmpty(name) ? PathLeaf(path) : name;
        ShowToast(replace ? $"Indicator+icon set: {Trunc(lbl, 20)}" : $"Indicator+icon added: {Trunc(lbl, 20)}");
    }

    // write the picked entity into the current objective's Paths. replace=Path= (sole path), false=Path+ (append).
    // entity matched by path, LivingOnly off. mirrors AddRadarPathToObjective but for a live entity.
    private void AddEntityPathToObjective(string curId, string path, string name, bool replace)
    {
        if (_routeStore == null) return;
        var step = _routeStore.Steps.FirstOrDefault(s => s.Id == curId);
        if (step == null) return;
        var (objs, objIdx) = SelectedObjectiveList(step, _editorSelectedObjIndex);
        var o = objs[objIdx];
        var newPath = new GuidePath(new Target(TargetKind.Entity, new Pattern(path), MatchKind.Path));
        var updated = replace
            ? o with { Paths = new System.Collections.Generic.List<GuidePath> { newPath } }
            : RouteEditing.AddPath(o, newPath);
        objs[objIdx] = updated;
        _routeStore.Update(step with { Objectives = objs });
        SaveUserRoute();
        ReloadRouteFromStore(curId);
        _bufForStepId = null;
        var lbl = string.IsNullOrEmpty(name) ? PathLeaf(path) : name;
        ShowToast(replace ? $"Path set: {Trunc(lbl, 22)}" : $"Path added: {Trunc(lbl, 20)}");
    }

    // turn the current objective into a Proximity gate on the picked entity, radius = current distance + 20%.
    // entity matched by path (consistent with Set/Path), replaces existing entities + distance.
    private void SetProximityGuidance(string curId, string path, string name, int dist)
    {
        if (_routeStore == null) return;
        var step = _routeStore.Steps.FirstOrDefault(s => s.Id == curId);
        if (step == null) return;
        var (objs, objIdx) = SelectedObjectiveList(step, _editorSelectedObjIndex);
        var o = objs[objIdx];

        float radius = Math.Max(1f, dist * 1.2f);
        objs[objIdx] = o with
        {
            Type = ObjectiveType.Proximity,
            Entities = new List<EntityMatcher> { new(new Pattern(path), MatchKind.Path) },
            Distance = radius,
        };
        _routeStore.Update(step with { Objectives = objs });
        SaveUserRoute();
        ReloadRouteFromStore(curId);
        _bufForStepId = null;
        var lbl = string.IsNullOrEmpty(name) ? PathLeaf(path) : name;
        ShowToast($"Proximity {radius:0} set: {Trunc(lbl, 18)}");
    }

    // append a QuestFlag advance objective for `flag` to the current step (pure math in RouteEditing), persist
    // the user route, rebuild, and jump the editor onto the new objective so you can verify it.
    private void BindCurrentStepQuestFlag(string curId, string flag)
    {
        if (_routeStore == null) return;
        var step = _routeStore.Steps.FirstOrDefault(s => s.Id == curId);
        if (step == null) return;
        _routeStore.Update(RouteEditing.AddQuestFlagObjective(step, flag));
        SaveUserRoute();
        ReloadRouteFromStore(curId);
        var reloaded = _routeStore.Steps.FirstOrDefault(s => s.Id == curId);
        _editorSelectedId = curId;
        _editorSelectedObjIndex = (reloaded?.Objectives.Count ?? 1) - 1;
        _bufForStepId = null;
        Settings.Editor.Enable.Value = true;   // open the editor on the new objective
        ShowToast($"Quest flag set: {Trunc(flag, 24)}");
    }

    // set the current step to advance on entering the live area via an EnterArea objective (pure math in
    // RouteEditing), persist the user route, rebuild, and jump the editor onto it so you can verify it.
    private void BindCurrentStepEnterArea(string curId, string areaId)
    {
        if (_routeStore == null || string.IsNullOrEmpty(areaId)) return;
        var step = _routeStore.Steps.FirstOrDefault(s => s.Id == curId);
        if (step == null) return;
        _routeStore.Update(RouteEditing.AddEnterAreaObjective(step, areaId));
        SaveUserRoute();
        ReloadRouteFromStore(curId);
        var reloaded = _routeStore.Steps.FirstOrDefault(s => s.Id == curId);
        _editorSelectedId = curId;
        _editorSelectedObjIndex = (reloaded?.Objectives.Count ?? 1) - 1;
        _bufForStepId = null;
        Settings.Editor.Enable.Value = true;   // open the editor on the new objective
        ShowToast($"EnterArea set: {Trunc(areaId, 20)}");
    }

    // --- quick add ----------------------------------------------------------------------------------------

    private void AddTriageStep(string anchorId, bool before)
    {
        if (_routeStore == null) return;
        var s = RouteEditing.SkeletonStep(_act, _areaId, _zoneName);
        if (!_routeStore.InsertRelative(anchorId, s, before)) _routeStore.Add(s);
        SaveUserRoute();
        ReloadRouteFromStore(s.Id);
        _editorSelectedId = s.Id;
        _editorSelectedObjIndex = 0;
        _bufForStepId = null;
        Settings.Editor.Enable.Value = true;   // open the editor on the new step
        ShowToast(before ? "Step added before" : "Step added after");
    }

    private void AddTriageObjective(string stepId)
    {
        if (_routeStore == null) return;
        var step = _routeStore.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step == null) return;
        var objs = step.Objectives.ToList();
        objs.Add(RouteEditing.BlankObjective());
        _routeStore.Update(step with { Objectives = objs });
        SaveUserRoute();
        ReloadRouteFromStore(stepId);
        _editorSelectedId = stepId;
        _editorSelectedObjIndex = objs.Count - 1;
        _bufForStepId = null;
        Settings.Editor.Enable.Value = true;
        ShowToast("Objective added");
    }

    // add a step immediately before the anchor with "Get Waypoint" text and an ActivateWaypoint objective.
    // inherits the anchor step's act/area so it slots into the route naturally.
    private void AddWaypointStepBefore(string anchorId)
    {
        if (_routeStore == null) return;
        var anchor = _routeStore.Steps.FirstOrDefault(s => s.Id == anchorId);
        var s = RouteEditing.SkeletonStep(anchor?.Act ?? _act, anchor?.AreaId ?? _areaId, anchor?.AreaName ?? _zoneName)
            with { Text = "Get Waypoint", Objectives = new List<Objective> { new(ObjectiveType.ActivateWaypoint) } };
        if (!_routeStore.InsertRelative(anchorId, s, before: true)) _routeStore.Add(s);
        SaveUserRoute();
        ReloadRouteFromStore(s.Id);
        _editorSelectedId = s.Id;
        _editorSelectedObjIndex = 0;
        _bufForStepId = null;
        Settings.Editor.Enable.Value = true;   // open the editor on the new step
        ShowToast("Waypoint step added before");
    }

    // move the current step one position up or down, persist, and toast.
    private void MoveCurrentStep(string curId, int delta)
    {
        if (_routeStore == null) return;
        var steps = _routeStore.Steps;
        int at = -1;
        for (int i = 0; i < steps.Count; i++) if (steps[i].Id == curId) { at = i; break; }
        if (at < 0) return;
        int to = at + delta;
        if (to < 0 || to >= steps.Count) return;
        _routeStore.Move(curId, to);
        SaveUserRoute();
        ReloadRouteFromStore(curId);
        _bufForStepId = null;
        ShowToast(delta < 0 ? "Step moved up" : "Step moved down");
    }

    // delete the current step (no undo - requires the two-click confirm in the panel).
    private void DeleteCurrentStep(string curId)
    {
        if (_routeStore == null) return;
        _routeStore.Delete(curId);
        SaveUserRoute();
        ReloadRouteFromStore(null);
        _editorSelectedId = null;
        _bufForStepId = null;
        ShowToast("Step deleted");
    }

    private void ApplyStepRename(string stepId, string newText)
    {
        if (_routeStore == null) return;
        var step = _routeStore.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step == null) return;
        _routeStore.Update(step with { Text = newText.Trim() });
        SaveUserRoute();
        ReloadRouteFromStore(stepId);
        _bufForStepId = null;   // invalidate editor buffer so it resyncs on next open
        ShowToast($"Renamed: {Trunc(newText.Trim(), 22)}");
    }
}
