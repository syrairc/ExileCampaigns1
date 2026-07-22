using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCampaigns.Guide;

namespace ExileCampaigns.Tracking;

// how the interaction completes. drives indicator kind + auto-advance trigger.
public enum InteractKind
{
    Dialog,     // NPC: advance when conversation UI opens
    ChestOpen,  // chest: advance when opened
    Proximity,  // quest object/shrine: advance on getting near
    Kill,       // hostile boss: advance only when it dies, never on proximity
}

// live entity the step wants the player to interact with, plus how to detect it.
public sealed class InteractTarget
{
    public required Entity Entity { get; init; }
    public required InteractKind Kind { get; init; }
    public required string MatchedName { get; init; }
}

// resolves the current step to one grid-space target coord for the path to aim at. path target is driven
// SOLELY by the objective's explicit Paths children (Tile/Entity/Room) -- step text, objective type, and
// advance entities have no effect. null when nothing explicit resolves yet (tiles not in instance, entity
// not loaded); caller retries. the indicator resolves its entities via ResolveIndicatorEntities.
public sealed class StepTargetResolver
{
    // tile pattern -> Radar's ExpectedCount for the current area (only the >1 ones). set on area change from
    // targets.json. without it a multi-instance tile (e.g. 2 staircases) collapses to one void centroid and the
    // route can't path there. resolve to the same cluster count Radar uses, then take the nearest.
    public IReadOnlyDictionary<string, int>? RadarTileCounts { get; set; }

    private int ClusterCount(string? pattern) =>
        !string.IsNullOrEmpty(pattern) && RadarTileCounts != null
        && RadarTileCounts.TryGetValue(pattern!, out var c) && c > 1 ? c : 1;

    public Vector2? Resolve(GameController gc, RouteStep step, Func<string, int, Vector2[]>? clusterTarget,
        string? effectiveAreaId = null)
    {
        if (gc == null || step == null) return null;
        var playerGrid = PlayerGrid(gc);

        // path target comes ONLY from the objective's explicit Paths children -- never step text, objective
        // type, or advance entities. order is Tile -> Entity -> Room; all coords walkable-snapped (clusterTarget,
        // ObjectiveEntityPositions, ResolveRoomCenters each snap) so Radar can expand a route. nearest to player.
        var tiles = GuidanceView.PathTilePatterns(step);
        if (tiles.Count > 0 && clusterTarget != null)
        {
            var hit = ResolveTiles(gc, tiles, clusterTarget);
            if (hit != null) return hit;
        }

        var entPats = GuidanceView.PathEntityPatterns(step);
        if (entPats.Count > 0)
        {
            var hit = Nearest(ObjectiveEntityPositions(gc, entPats, liveOnly: false), playerGrid);
            if (hit != null) return hit;
        }

        var roomPats = GuidanceView.PathRoomPatterns(step);
        if (roomPats.Count > 0)
        {
            var hit = Nearest(ResolveRoomCenters(gc, roomPats), playerGrid);
            if (hit != null) return hit;
        }

        return null;
    }

    // nearest of several already-snapped candidate coords to the player (or the first when player pos unknown).
    private static Vector2? Nearest(IReadOnlyList<Vector2> pts, Vector2? playerGrid)
    {
        if (pts == null || pts.Count == 0) return null;
        if (playerGrid is not { } p) return pts[0];
        Vector2 best = pts[0];
        float bestD = Vector2.DistanceSquared(best, p);
        for (int i = 1; i < pts.Count; i++)
        {
            float d = Vector2.DistanceSquared(pts[i], p);
            if (d < bestD) { bestD = d; best = pts[i]; }
        }
        return best;
    }

    // AreaGraph room coords are in tiles; Radar multiplies by this to reach grid units (Radar TileToGridConversion).
    private const int TileToGrid = 23;

    // ALL AreaGraph room centers whose name contains ANY of the given Room Path-child patterns. the decoupled
    // replacement for the Meta.Objective.Rooms path: reads the model's Room children directly (via
    // GuidanceView.PathRoomPatterns), so rooms route for every objective type and across objectives.
    public IReadOnlyList<Vector2> ResolveRoomCenters(GameController gc, IReadOnlyList<string> patterns)
    {
        if (gc == null || patterns == null || patterns.Count == 0) return Array.Empty<Vector2>();
        return AllRoomCenters(gc, name =>
            patterns.Any(p => !string.IsNullOrEmpty(p) && name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // the ordered per-sub-objective progress flags for the objective the step names (e.g. the 3 Rust obelisk
    // flags), or empty. drives "drop the path to the room nearest the player when one flips" in the pathing.
    public IReadOnlyList<string> ResolveObjectiveProgressFlags(RouteStep step, string? effectiveAreaId)
        => step == null ? System.Array.Empty<string>() : GuidanceView.ProgressFlags(step);

    // union form: positions of every entity whose match-path contains ANY of the fragments. lets one step path
    // to several distinct authored entity targets (e.g. two different ground items), each fragment also still
    // matching all its live copies. distinct by entity (an entity matching two fragments is added once).
    public IReadOnlyList<Vector2> ObjectiveEntityPositions(GameController gc, IReadOnlyList<string> entityPaths, bool liveOnly)
    {
        var res = new List<Vector2>();
        if (gc == null || entityPaths == null || entityPaths.Count == 0) return res;
        var entities = gc.EntityListWrapper?.Entities;
        if (entities == null) return res;
        foreach (var e in entities)
        {
            if (e == null || !e.IsValid || !HasGrid(e)) continue;
            var mp = MatchPath(e);
            if (mp == null) continue;
            if (!entityPaths.Any(p => !string.IsNullOrEmpty(p) && mp.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            // WorldItems vanish when looted; isTargetable doesn't flip. skip the interactable check for them.
            if (liveOnly && e.Type != EntityType.WorldItem && !IsInteractable(e)) continue;
            res.Add(SnapToWalkable(gc, GridOf(e)));
        }
        return res;
    }

    // a world object still accepts interaction. PoE flips Targetable.isTargetable=false once a one-shot
    // objective (a nail stake / runic seal) is used, so this is its per-sub-objective completion signal.
    private static bool IsInteractable(Entity e)
    {
        var t = e.GetComponent<Targetable>();
        return t == null || t.isTargetable;
    }

    // every AreaGraphs room center (grid units) whose name satisfies the predicate.
    private static IEnumerable<Vector2> AllRoomCenters(GameController gc, Func<string, bool> nameMatch)
    {
        var list = new List<Vector2>();
        try
        {
            var graphs = gc.IngameState?.Data?.AreaGraphs;
            if (graphs == null) return list;
            foreach (var g in graphs)
            {
                var rooms = g?.Rooms;
                if (rooms == null) continue;
                foreach (var r in rooms)
                {
                    var name = r.Name;
                    if (string.IsNullOrEmpty(name) || !nameMatch(name)) continue;
                    var c = new Vector2(
                        (r.MinCoord.X + r.MaxCoord.X) * 0.5f * TileToGrid,
                        (r.MinCoord.Y + r.MaxCoord.Y) * 0.5f * TileToGrid);
                    list.Add(SnapToWalkable(gc, c));
                }
            }
        }
        catch { }
        return list;
    }

    // nearest AreaGraphs room center (grid units) whose name satisfies the predicate, or null.
    private static Vector2? NearestRoomCenter(GameController gc, Vector2? playerGrid, Func<string, bool> nameMatch)
    {
        try
        {
            var graphs = gc.IngameState?.Data?.AreaGraphs;
            if (graphs == null) return null;
            Vector2? best = null;
            float bestD = float.MaxValue;
            foreach (var g in graphs)
            {
                var rooms = g?.Rooms;
                if (rooms == null) continue;
                foreach (var r in rooms)
                {
                    var name = r.Name;
                    if (string.IsNullOrEmpty(name) || !nameMatch(name)) continue;
                    var c = SnapToWalkable(gc, new Vector2(
                        (r.MinCoord.X + r.MaxCoord.X) * 0.5f * TileToGrid,
                        (r.MinCoord.Y + r.MaxCoord.Y) * 0.5f * TileToGrid));
                    float d = playerGrid is { } p ? Vector2.DistanceSquared(c, p) : 0f;
                    if (d < bestD) { bestD = d; best = c; }
                }
            }
            return best;
        }
        catch { return null; }
    }

    // a room's geometric center can land on a blocked tile (the obelisk, water, decoration); Radar's pathfinder
    // (RunFirstScan) can't reach a target it can't expand from, so the route silently never appears. snap to the
    // nearest pathable tile using the same grid Radar uses (Data.RawPathfindingData, values 1..5 pathable, 0
    // blocked -- mirrors Radar's ClusterTarget snap). grid coords == pathfinding-grid indices.
    private static Vector2 SnapToWalkable(GameController gc, Vector2 grid)
    {
        try
        {
            var data = gc.IngameState?.Data?.RawPathfindingData;
            if (data == null) return grid;
            int gx = (int)grid.X, gy = (int)grid.Y;
            bool Pathable(int x, int y) =>
                y >= 0 && y < data.Length && data[y] != null && x >= 0 && x < data[y].Length
                && data[y][x] is >= 1 and <= 5;
            if (Pathable(gx, gy)) return grid;
            for (int radius = 1; radius <= 60; radius++)   // expanding ring; obelisk centers sit within a few tiles
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;   // ring perimeter only
                        if (Pathable(gx + dx, gy + dy)) return new Vector2(gx + dx, gy + dy);
                    }
            return grid;
        }
        catch { return grid; }
    }

    // EVERY still-interactable entity matching any Indicator Entity target, nearest-first. one
    // arrow per match, so multiple authored targets (or one pattern matching several live drops) all draw.
    // IsInteractable already drops inactive NPC duplicates (non-targetable), so a single talk target doesn't
    // spawn an arrow on each of a story NPC's idle copies.
    public IReadOnlyList<InteractTarget> ResolveIndicatorEntities(GameController gc, IEnumerable<Target> targets, float maxDistance)
    {
        var res = new List<InteractTarget>();
        if (gc == null || targets == null) return res;
        var entities = gc.EntityListWrapper?.Entities;
        if (entities == null) return res;

        var matchers = targets
            .Where(t => t != null && t.Kind == TargetKind.Entity)
            .Select(t => (m: new PatternMatcher(t.Match), byPath: t.MatchBy == MatchKind.Path, living: t.LivingOnly))
            .ToList();
        if (matchers.Count == 0) return res;

        foreach (var e in entities
            .Where(e => e != null && e.IsValid && HasGrid(e))
            .Where(e => e.DistancePlayer <= maxDistance)
            .Where(IsInteractable)
            // a LivingOnly matcher only counts when the entity is alive, so a corpse/prop sharing the name
            // doesn't draw an arrow. a non-living matcher matching is enough.
            .Where(e => matchers.Any(x => x.m.IsMatch(x.byPath ? MatchPath(e) : e.RenderName) && (!x.living || IsLiving(e))))
            .OrderBy(e => e.DistancePlayer))
            res.Add(new InteractTarget { Entity = e, Kind = InteractKind.Proximity, MatchedName = e.RenderName ?? "" });
        return res;
    }

    // grid coords of every live entity matching an Entity target (nearest-first), for drawing a minimap icon at
    // each. plural form of the Entity case in ResolveTargetCoord.
    public IReadOnlyList<Vector2> ResolveEntityCoords(GameController gc, Target t)
    {
        var res = new List<Vector2>();
        if (gc == null || t == null || t.Kind != TargetKind.Entity) return res;
        var entities = gc.EntityListWrapper?.Entities;
        if (entities == null) return res;
        var em = new PatternMatcher(t.Match);
        bool byPath = t.MatchBy == MatchKind.Path;
        foreach (var e in entities
            .Where(e => e != null && e.IsValid && HasGrid(e))
            .Where(e => !t.LivingOnly || IsLiving(e))
            .Where(e => em.IsMatch(byPath ? MatchPath(e) : e.RenderName))
            .OrderBy(e => e.DistancePlayer))
            res.Add(GridOf(e));
        return res;
    }

    // resolve any guidance Target to one grid coord, for drawing a minimap icon. Tile -> Radar ClusterTarget;
    // Room -> nearest matching AreaGraph room center; Entity -> nearest live matching entity. null = unresolved.
    public Vector2? ResolveTargetCoord(GameController gc, Target t, Func<string, int, Vector2[]>? clusterTarget)
    {
        if (gc == null || t == null) return null;
        var playerGrid = PlayerGrid(gc);
        switch (t.Kind)
        {
            case TargetKind.Tile:
                if (clusterTarget == null) return null;
                try { return NearestCluster(clusterTarget(t.Match.Value, ClusterCount(t.Match.Value)), playerGrid); }
                catch { return null; }
            case TargetKind.Room:
            {
                var rm = new PatternMatcher(t.Match);
                return NearestRoomCenter(gc, playerGrid, name => rm.IsMatch(name));
            }
            case TargetKind.Entity:
            {
                var entities = gc.EntityListWrapper?.Entities;
                if (entities == null) return null;
                var em = new PatternMatcher(t.Match);
                bool byPath = t.MatchBy == MatchKind.Path;
                var hit = entities
                    .Where(e => e != null && e.IsValid && HasGrid(e))
                    .Where(e => !t.LivingOnly || IsLiving(e))
                    .Where(e => em.IsMatch(byPath ? MatchPath(e) : e.RenderName))
                    .OrderBy(e => e.DistancePlayer)
                    .FirstOrDefault();
                return hit != null ? GridOf(hit) : (Vector2?)null;
            }
            default:
                return null;
        }
    }

    // nearest Radar cluster across SEVERAL authored tile patterns (e.g. two staircase variants), or null. lets a
    // step path to whichever of its Tile children is actually present in this instance, not just the first.
    public Vector2? ResolveTiles(GameController gc, IReadOnlyList<string> patterns, Func<string, int, Vector2[]>? clusterTarget)
    {
        if (gc == null || clusterTarget == null || patterns == null || patterns.Count == 0) return null;
        var playerGrid = PlayerGrid(gc);
        Vector2? best = null;
        float bestD = float.MaxValue;
        foreach (var p in patterns)
        {
            if (string.IsNullOrEmpty(p)) continue;
            try
            {
                if (NearestCluster(clusterTarget(p, ClusterCount(p)), playerGrid) is not { } c) continue;
                float d = playerGrid is { } pg ? Vector2.DistanceSquared(c, pg) : 0f;
                if (d < bestD) { bestD = d; best = c; }
            }
            catch { /* tile not in this instance, try next */ }
        }
        return best;
    }

    // ALL Radar clusters across SEVERAL authored tile patterns (vs ResolveTiles, which keeps only the nearest
    // one). used by the All-mode multi path so a step with N Tile children draws a line to each. each pattern is
    // clustered by its Radar ExpectedCount (ClusterCount), so a multi-instance tile still splits correctly.
    public IReadOnlyList<Vector2> ResolveTileClusters(GameController gc, IReadOnlyList<string> patterns,
        Func<string, int, Vector2[]>? clusterTarget)
    {
        var res = new List<Vector2>();
        if (gc == null || clusterTarget == null || patterns == null) return res;
        foreach (var p in patterns)
        {
            if (string.IsNullOrEmpty(p)) continue;
            try { var cs = clusterTarget(p, ClusterCount(p)); if (cs != null) res.AddRange(cs); }
            catch { /* tile not in this instance, try next */ }
        }
        return res;
    }

    private static Vector2? NearestCluster(Vector2[]? coords, Vector2? playerGrid)
    {
        if (coords == null || coords.Length == 0) return null;
        if (playerGrid is not { } p) return coords[0];
        return coords.OrderBy(c => Vector2.DistanceSquared(c, p)).First();
    }

    // the LivingOnly gate for guidance Entity targets: entity is actually alive (Life present, CurrentHP > 0).
    // Entity.IsAlive is null-Life-safe (no Life -> false), so a corpse or a lifeless prop sharing the name fails.
    private static bool IsLiving(Entity e)
        => e.IsAlive;

    private static bool HasGrid(Entity e)
        => e.GetComponent<Positioned>() != null;

    // the path to identity-match an entity on. a ground item is a WorldItem shell whose own Path is generic;
    // its base-type identity (what the editor picker captured) lives on the held item entity. position still
    // comes from the shell via GridOf, so the arrow/path/icon lands on the drop.
    private static string? MatchPath(Entity e)
        => e.Type == EntityType.WorldItem
            ? (e.GetComponent<WorldItem>()?.ItemEntity?.Path ?? e.Path)
            : e.Path;

    private static Vector2 GridOf(Entity e)
    {
        var pos = e.GetComponent<Positioned>();
        return new Vector2(pos.GridX, pos.GridY);
    }

    private static Vector2? PlayerGrid(GameController gc)
    {
        var pos = gc.Player?.GetComponent<Positioned>();
        return pos == null ? null : new Vector2(pos.GridX, pos.GridY);
    }
}
