using System;
using System.Collections.Generic;
using System.Linq;

namespace ExileCampaigns.Guide;

// one resolved minimap-icon request: which sprite, its tint, where to anchor it, an optional per-icon pixel
// size (null = use the global default), and the owning step's Id (so the renderer can pulse the current one).
public sealed record MinimapIconSpec(string IconKey, uint Tint, Target Anchor, float? Size = null, string StepId = "");

// reads a step's decoupled guidance. the on-screen arrow comes from objectives' Indicators[], the ground
// path from Paths[] - independently, so an indicator can exist with no path and a path with no arrow. keeps
// the indicator resolver off the Paths channel (the old LegacyView synth conflated them).
public static partial class GuidanceView
{
    // entity targets for the golden arrow, gathered from every objective's Indicators[]. Entity kind only -
    // Tile/Room indicators are stored but not drawn yet (no world entity to anchor an arrow).
    public static IReadOnlyList<Target> IndicatorEntityTargets(RouteStep step)
    {
        if (step?.Objectives == null) return new List<Target>();
        return step.Objectives
            .SelectMany(o => o.Indicators ?? new List<Indicator>())
            .Select(i => i.Target)
            .Where(t => t != null && t.Kind == TargetKind.Entity)
            .ToList();
    }

    // every Entity-by-path Path child pattern across the step's objectives (distinct, in order). drives the
    // ground path to each authored entity target, so multiple distinct entities each get a route. parallel to
    // IndicatorEntityTargets but for the Paths[] channel.
    public static IReadOnlyList<string> PathEntityPatterns(RouteStep step)
    {
        var res = new List<string>();
        if (step?.Objectives == null) return res;
        foreach (var o in step.Objectives)
            foreach (var p in o.Paths ?? new List<GuidePath>())
                if (p.Target.Kind == TargetKind.Entity && p.Target.MatchBy == MatchKind.Path)
                {
                    var v = p.Target.Match.Value;
                    if (!string.IsNullOrEmpty(v) && !res.Contains(v)) res.Add(v);
                }
        return res;
    }

    // every Tile Path child pattern across the step's objectives (distinct, in order). the ground path resolves
    // the nearest of these via Radar ClusterTarget, so a step can author several tiles (e.g. two staircase
    // variants) and the path lands on whichever is present. parallel to PathEntityPatterns but for tiles.
    public static IReadOnlyList<string> PathTilePatterns(RouteStep step)
    {
        var res = new List<string>();
        if (step?.Objectives == null) return res;
        foreach (var o in step.Objectives)
            foreach (var p in o.Paths ?? new List<GuidePath>())
                if (p.Target.Kind == TargetKind.Tile)
                {
                    var v = p.Target.Match.Value;
                    if (!string.IsNullOrEmpty(v) && !res.Contains(v)) res.Add(v);
                }
        return res;
    }

    // every Room Path child pattern across the step's objectives (distinct, in order). the ground path draws a
    // line to each matching AreaGraph room center. parallel to PathEntityPatterns / PathTilePatterns - gathers
    // across ALL objectives, so rooms on a second objective aren't dropped.
    public static IReadOnlyList<string> PathRoomPatterns(RouteStep step)
    {
        var res = new List<string>();
        if (step?.Objectives == null) return res;
        foreach (var o in step.Objectives)
            foreach (var p in o.Paths ?? new List<GuidePath>())
                if (p.Target.Kind == TargetKind.Room)
                {
                    var v = p.Target.Match.Value;
                    if (!string.IsNullOrEmpty(v) && !res.Contains(v)) res.Add(v);
                }
        return res;
    }

    // true if any non-EnterArea objective with path children is in All mode. drives whether the pathing draws
    // a line per target (All) or defers to the single-target nearest flow (Nearest, the default). step-level:
    // a step mixing All and Nearest objectives (rare) draws all.
    public static bool WantsAllPaths(RouteStep step)
    {
        if (step?.Objectives == null) return false;
        foreach (var o in step.Objectives)
            if (o.Mode == PathMode.All && o.Type != ObjectiveType.EnterArea && (o.Paths?.Count ?? 0) > 0)
                return true;
        return false;
    }

    // kill entity names the step targets: the first entity of each Kill objective (priority order),
    // matching the single KillFragment LegacyView emitted per kill objective.
    public static IReadOnlyList<string> KillTargets(RouteStep step)
    {
        var res = new List<string>();
        if (step?.Objectives == null) return res;
        foreach (var o in step.Objectives)
            if (o.Type == ObjectiveType.Kill)
            {
                var name = o.Entities != null && o.Entities.Count > 0 ? o.Entities[0].Match.Value : null;
                if (!string.IsNullOrWhiteSpace(name)) res.Add(name!);
            }
        return res;
    }

    // the step's interact-kind label (dialog|chest|proximity|kill), from the first objective whose type
    // implies one. mirrors LegacyView's first-wins mapping.
    public static string? InteractKind(RouteStep step)
    {
        if (step?.Objectives == null) return null;
        foreach (var o in step.Objectives)
            switch (o.Type)
            {
                case ObjectiveType.Kill: return "kill";
                case ObjectiveType.Talk: return "dialog";
                case ObjectiveType.Proximity: return "proximity";
                case ObjectiveType.Interact: return "chest";
            }
        return null;
    }

    // ordered per-sub-objective progress flags from the first objective that declares any. drives the
    // pathing's "drop the line to the room nearest the player when one flips".
    public static IReadOnlyList<string> ProgressFlags(RouteStep step)
    {
        if (step?.Objectives == null) return System.Array.Empty<string>();
        foreach (var o in step.Objectives)
            if (o.ProgressFlags is { Count: > 0 })
                return o.ProgressFlags.Select(p => p.Value).ToList();
        return System.Array.Empty<string>();
    }

    // every objective MinimapIcon for steps in the given area. anchor = the icon's own Target, else the
    // objective's first Indicator target, else its first Path target; skipped when none exist. an objective
    // may carry several icons (each its own sprite/tint/size/target).
    public static IReadOnlyList<MinimapIconSpec> MinimapIconsForArea(IReadOnlyList<RouteStep> steps, string areaId)
    {
        var res = new List<MinimapIconSpec>();
        if (steps == null) return res;
        foreach (var step in steps)
        {
            if (!string.Equals(step.AreaId, areaId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var o in step.Objectives ?? new List<Objective>())
                foreach (var mi in o.MinimapIcons ?? new List<MinimapIcon>())
                {
                    var anchor = mi.Target
                        ?? o.Indicators?.FirstOrDefault()?.Target
                        ?? o.Paths?.FirstOrDefault()?.Target;
                    if (anchor == null) continue;
                    res.Add(new MinimapIconSpec(mi.IconKey, mi.Tint, anchor, mi.Size, step.Id));
                }
        }
        return res;
    }
}
