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

// resolves the current step to one grid-space target coord for the path to aim at. path target is driven
// SOLELY by the objective's explicit Paths children (Tile/Entity/Room) -- step text, objective type, and
// advance entities have no effect. null when nothing explicit resolves yet (tiles not in instance, entity
// not loaded); caller retries.
public sealed class StepTargetResolver
{
    // tile pattern -> Radar's ExpectedCount for the current area (only the >1 ones). set on area change from
    // targets.json. without it a multi-instance tile (e.g. 2 staircases) collapses to one void centroid and the
    // route can't path there. resolve to the same cluster count Radar uses, then take the nearest.
    public IReadOnlyDictionary<string, int>? RadarTileCounts { get; set; }

    private int ClusterCount(string? pattern) =>
        !string.IsNullOrEmpty(pattern) && RadarTileCounts != null
        && RadarTileCounts.TryGetValue(pattern!, out var c) && c > 1 ? c : 1;

    // Radar/ExileMinimap answer ClusterTarget by running KMeans over the area's terrain tiles - it is the most
    // expensive thing this resolver can do, and the answer is static for the area instance (it even jitters
    // slightly per call, which is why the All-mode path caches its own baseline). so resolve each pattern once.
    // a HIT is kept for the area's lifetime; a MISS expires, because early in a zone the provider may simply
    // not have processed terrain yet - but bounded, so a genuinely-absent tile isn't re-clustered every poll.
    private readonly Dictionary<string, (long At, Vector2[] Coords)> _tileClusters = new();
    private const long TileClusterMissMs = 1000;

    private Vector2[] Clusters(string? pattern, Func<string, int, Vector2[]>? clusterTarget)
    {
        if (clusterTarget == null || string.IsNullOrEmpty(pattern)) return Array.Empty<Vector2>();
        if (_tileClusters.TryGetValue(pattern!, out var hit) &&
            (hit.Coords.Length > 0 || Environment.TickCount64 - hit.At < TileClusterMissMs))
            return hit.Coords;

        Vector2[] res;
        try { res = clusterTarget(pattern!, ClusterCount(pattern)) ?? Array.Empty<Vector2>(); }
        catch { res = Array.Empty<Vector2>(); }   // tile not in this instance
        _tileClusters[pattern!] = (Environment.TickCount64, res);
        return res;
    }

    // remember last-seen grid pos of entities resolved as path targets, so the path survives the
    // entity leaving load range. keyed by entity address (stable per area instance), cleared on area change via
    // ResetSeenCache. a cached entry is only re-served when its stored identity still matches the live query, so
    // a step advance (different patterns) drops old entries on its own. off -> exact old live-only behavior.
    // set from the Guidance "Remember target locations" toggle each tick.
    public bool CacheLocations { get; set; } = true;
    private readonly Dictionary<long, SeenEntity> _seen = new();
    private readonly record struct SeenEntity(Vector2 Grid, string? Path, string? RenderName);

    public void ResetSeenCache() { _seen.Clear(); _tileClusters.Clear(); }

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
        bool Match(string? mp) => mp != null &&
            entityPaths.Any(p => !string.IsNullOrEmpty(p) && mp.Contains(p, StringComparison.OrdinalIgnoreCase));
        // caching active for this call only when the toggle is on and the caller wants persistence (not liveOnly).
        var live = CacheLocations && !liveOnly ? new HashSet<long>() : null;
        var entities = gc.EntityListWrapper?.Entities;
        if (entities != null)
            foreach (var e in entities)
            {
                // pattern test before the component fetches, so far/unrelated entities cost the least
                if (e == null || !e.IsValid) continue;
                var mp = MatchPath(e);
                if (!Match(mp)) continue;
                if (!HasGrid(e)) continue;
                // WorldItems vanish when looted; isTargetable doesn't flip. skip the interactable check for them.
                if (liveOnly && e.Type != EntityType.WorldItem && !IsInteractable(e)) continue;
                var g = SnapToWalkable(gc, GridOf(e));
                res.Add(g);
                if (live != null) { _seen[e.Address] = new SeenEntity(g, mp, e.RenderName ?? ""); live.Add(e.Address); }
            }
        // out-of-range fallback: cached spots that still match this query and aren't live right now.
        if (live != null)
            foreach (var kv in _seen)
                if (!live.Contains(kv.Key) && Match(kv.Value.Path))
                    res.Add(kv.Value.Grid);
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
            // walk the ring perimeter directly instead of scanning the whole square and skipping the inside.
            // same order, same first hit, but O(r) per ring - the old form was ~288k iterations at radius 60.
            for (int radius = 1; radius <= 60; radius++)   // expanding ring; obelisk centers sit within a few tiles
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (dy == -radius || dy == radius)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                            if (Pathable(gx + dx, gy + dy)) return new Vector2(gx + dx, gy + dy);
                    }
                    else
                    {
                        if (Pathable(gx - radius, gy + dy)) return new Vector2(gx - radius, gy + dy);
                        if (Pathable(gx + radius, gy + dy)) return new Vector2(gx + radius, gy + dy);
                    }
                }
            return grid;
        }
        catch { return grid; }
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
            if (NearestCluster(Clusters(p, clusterTarget), playerGrid) is not { } c) continue;
            float d = playerGrid is { } pg ? Vector2.DistanceSquared(c, pg) : 0f;
            if (d < bestD) { bestD = d; best = c; }
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
            res.AddRange(Clusters(p, clusterTarget));
        return res;
    }

    private static Vector2? NearestCluster(Vector2[]? coords, Vector2? playerGrid)
    {
        if (coords == null || coords.Length == 0) return null;
        if (playerGrid is not { } p) return coords[0];
        return coords.OrderBy(c => Vector2.DistanceSquared(c, p)).First();
    }

    private static bool HasGrid(Entity e)
        => e.GetComponent<Positioned>() != null;

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
