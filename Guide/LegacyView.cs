using System.Collections.Generic;
using System.Linq;

namespace ExileCampaigns.Guide;

// builds the legacy ParsedStep (fragments + StepMeta) that StepTargetResolver/Pathing/Indicators consume,
// from a new RouteStep. guidance only - advance is native via AdvanceEngine, so a missing mapping (e.g.
// Loot has no path) just means "no guidance target", never wrong advance. inverse of RouteMigration.
public static class LegacyView
{
    public static ParsedStep ForStep(RouteStep m)
    {
        var ps = new ParsedStep { IsOptional = m.Optional };
        ps.Fragments.Add(new TextFragment(m.Text ?? ""));

        string? flag = null, completeArea = null, pathTile = null, interactKind = null;
        string? entPath = null;
        ObjectiveMeta? objMeta = null;

        foreach (var o in m.Objectives ?? new List<Objective>())
        {
            // guidance is FULLY decoupled from Type: the editor lets any objective carry any Path child, so the
            // synth routes tile/room/entity-path for EVERY type below. the switch handles only the type-specific
            // bits (kill target, advance flag, complete-area, interact-kind label).
            var paths = o.Paths ?? new List<GuidePath>();
            string? tilePath = paths.FirstOrDefault(p => p.Target.Kind == TargetKind.Tile)?.Target.Match.Value;
            var roomList = paths.Where(p => p.Target.Kind == TargetKind.Room)
                                .Select(p => p.Target.Match.Value).ToList();
            string? entPathChild = paths.FirstOrDefault(
                p => p.Target.Kind == TargetKind.Entity && p.Target.MatchBy == MatchKind.Path)?.Target.Match.Value;
            if (entPath == null) entPath = entPathChild;

            switch (o.Type)
            {
                case ObjectiveType.Kill:
                    var killName = o.Entities?.FirstOrDefault()?.Match.Value;
                    if (!string.IsNullOrEmpty(killName)) ps.Fragments.Add(new KillFragment(killName!));
                    interactKind ??= "kill";
                    break;
                case ObjectiveType.Interact:
                case ObjectiveType.Proximity:
                case ObjectiveType.Talk:
                    interactKind ??= o.Type == ObjectiveType.Talk ? "dialog"
                                   : o.Type == ObjectiveType.Proximity ? "proximity" : "chest";
                    break;
                case ObjectiveType.QuestFlag:
                    flag ??= o.Flag?.Value;
                    break;
                case ObjectiveType.EnterArea:
                    completeArea ??= o.AreaTarget?.Value;
                    break;
            }

            // route the guidance children for ANY type. rooms + entity-path build the multi-objective
            // ObjectiveMeta (per-room/per-entity path lines); a tile drives the single-target ground path when
            // rooms aren't driving it. EnterArea is excluded: its tile is the transition exit, resolved via the
            // live AreaTransition match + the direct PathTilePatterns channel, never as a borrowable PathTarget.
            var rooms = roomList.Count > 0 ? roomList : null;
            if (entPathChild != null || rooms != null)
                objMeta ??= new ObjectiveMeta(
                    Label: o.Label, Rooms: rooms, Count: o.Count > 0 ? o.Count : 0,
                    ProgressFlags: o.ProgressFlags?.Select(p => p.Value).ToList(), EntityPath: entPathChild);
            if (pathTile == null && rooms == null && o.Type != ObjectiveType.EnterArea) pathTile = tilePath;
        }

        // safety net: if no objMeta was built above but some objective carried an entity-path child, give it a
        // minimal entity-objective so its ground path still renders. (the loop now builds objMeta for any type,
        // so this rarely fires - kept for the odd case where entPath was captured without one.)
        if (objMeta == null && entPath != null)
            objMeta = new ObjectiveMeta(EntityPath: entPath);

        if (flag != null || completeArea != null || pathTile != null
            || interactKind != null || objMeta != null || !string.IsNullOrEmpty(m.Note))
            ps.Meta = new StepMeta(
                CompletionFlag: flag, CompletionKind: null, Objective: objMeta,
                // TransitionTile retired from the runtime synth: an EnterArea tile is now a plain Tile Path
                // child resolved via PathTilePatterns. the field remains only for RouteMigration of old data.
                PathTarget: pathTile, TransitionTile: null, InteractKind: interactKind,
                Optional: m.Optional ? true : null, Note: string.IsNullOrEmpty(m.Note) ? null : m.Note,
                CompleteOnEnterArea: completeArea);
        return ps;
    }

    public static ParsedStep HeaderFor(string areaId, string areaName)
    {
        var ps = new ParsedStep();
        ps.Fragments.Add(new EnterFragment(areaId ?? ""));
        ps.Fragments.Add(new TextFragment(" " + (areaName ?? "")));
        return ps;
    }
}
