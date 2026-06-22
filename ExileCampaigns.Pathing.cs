using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ExileCampaigns.Guide;
using ExileCampaigns.Tracking;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using GameOffsets.Native;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace ExileCampaigns;

// guided path to the current step's objective. pathfinding delegated to Radar over PluginBridge (Radar
// owns the terrain graph); we resolve the target, ask Radar for a live-updating path, draw it on ground + minimap.
//
// drawing math mirrors Radar's own (Radar.cs DrawWorldPaths / DrawTargets):
//   - ground: grid -> world (x*mult, y*mult, height) -> Camera.WorldToScreen
//   - minimap: iso camera projection of the grid delta from the player, scaled by map scale
public partial class ExileCampaigns
{
    // grid units -> world units (Radar: TileToWorld 250 / TileToGrid 23)
    private const float GridToWorldMultiplier = 250f / 23f;
    private const double CameraAngle = 38.7 * Math.PI / 180;
    private static readonly float CameraAngleCos = (float)Math.Cos(CameraAngle);
    private static readonly float CameraAngleSin = (float)Math.Sin(CameraAngle);

    // Radar bridge delegates (null until Radar loads / registers them)
    private Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task>? _radarLookForRoute;
    private Func<string, int, Vector2[]>? _radarClusterTarget;

    private StepTargetResolver _targetResolver = new();
    private CancellationTokenSource _pathCts = new();
    private volatile List<Vector2i>? _currentPath;   // latest path from Radar's callback (background thread)
    private Vector2? _currentTarget;                 // grid target we're currently routing to
    private int _lastStepForTarget = -1;             // step index the current target was resolved for

    // multi-objective room steps (e.g. the 3 Rust obelisks): one Radar route per *remaining* objective room.
    // a path drops when its sub-objective completes -- on each newly-true progress flag we mark the room nearest
    // the player done, so only the not-yet-clicked obelisks keep a line.
    // routes are started ONCE per target and never re-requested: Radar caches each target's distance field for
    // the area's lifetime, so re-asking a known target yields nothing (PathFinder.RunFirstScan -> yield break).
    // completing a sub-objective just hides its line (the bool[] below), it does not cancel/re-add anything.
    private CancellationTokenSource _objCts = new();
    private volatile List<Vector2i>?[]? _objectivePaths;     // slot per target (array: index-set is render-safe)
    private List<Vector2>? _objectiveTargets;                // full target set, immutable once started
    private bool[]? _objectiveDone;                          // parallel to targets: completed -> stop drawing
    private HashSet<string>? _objectiveFlagSnapshot;         // progress flags true as of last poll (null = unseeded)
    private List<Vector2>? _objTileBase;                     // cached tile-cluster centroids for current step (stable baseline)


    // per-area terrain, read on AreaChange (needed to project grid points to screen)
    private float[][]? _heightData;

    private DateTime _lastPathPoll;

    private bool PathsEnabled => Settings.Path.ShowPathOnGround || Settings.Path.ShowPathOnMinimap;

    // authored area-id -> Radar tile-name pattern. path fallback when no live entity resolves (e.g. a
    // {kill|Boss} step whose boss isn't in memory yet: path to the boss arena / zone exit tile). tiles are
    // static geometry, available from area load, so the path shows the moment you enter. patterns must be
    // bridge-usable: a literal tile name or Metadata/...tdt path, NOT a room "*" wildcard (ClusterTarget
    // drops the room filter, so "*" matches the whole map). copied from Radar's targets.json (keyed by area RawName, e.g. G1_1)
    private string AreaTargetsPath => Path.Combine(DirectoryFullName, "Data", "poe2", "area-targets.json");
    private string AreaTransitionsPath => Path.Combine(DirectoryFullName, "Data", "poe2", "area-transitions.json");
    private string AreaObjectivesPath => Path.Combine(DirectoryFullName, "Data", "poe2", "area-objectives.json");

    private void LoadAreaTargets()
    {
        try
        {
            var map = LoadAreaTargetMap();
            var transitions = LoadAreaTransitionMap();
            var objectives = LoadAreaObjectiveMap();
            _targetResolver = new StepTargetResolver(map, transitions, objectives);
            LogMessage($"ExileCampaigns -> area-targets: {map.Count} boss tiles, {transitions.Count} transition areas, {objectives.Count} objective areas.");
        }
        catch (Exception ex)
        {
            LogError($"ExileCampaigns -> area-targets map load failed: {ex.Message}");
            _targetResolver = new StepTargetResolver();
        }
    }

    // area-id -> single boss-arena tile pattern.
    private Dictionary<string, string> LoadAreaTargetMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(AreaTargetsPath))
        {
            LogMessage($"ExileCampaigns -> area-targets map not found at {AreaTargetsPath}; boss path fallback disabled.");
            return map;
        }
        var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(AreaTargetsPath));
        foreach (var prop in root.Properties())
        {
            if (prop.Name.StartsWith("_")) continue;   // skip _comment metadata
            var pattern = (string?)prop.Value;
            if (!string.IsNullOrWhiteSpace(pattern))
                map[prop.Name] = pattern!;
        }
        return map;
    }

    // source area-id -> [{destination match, transition tile}] for "Enter/Exit X" pre-load paths.
    private Dictionary<string, IReadOnlyList<(string Match, string Tile)>> LoadAreaTransitionMap()
    {
        var map = new Dictionary<string, IReadOnlyList<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(AreaTransitionsPath))
        {
            LogMessage($"ExileCampaigns -> area-transitions map not found at {AreaTransitionsPath}; transition path fallback disabled.");
            return map;
        }
        var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(AreaTransitionsPath));
        foreach (var prop in root.Properties())
        {
            if (prop.Name.StartsWith("_")) continue;
            if (prop.Value is not Newtonsoft.Json.Linq.JArray arr) continue;
            var list = new List<(string, string)>();
            foreach (var e in arr)
            {
                var m = (string?)e["match"];
                var tile = (string?)e["tile"];
                if (!string.IsNullOrWhiteSpace(m) && !string.IsNullOrWhiteSpace(tile))
                    list.Add((m!, tile!));
            }
            if (list.Count > 0) map[prop.Name] = list;
        }
        return map;
    }

    // area-id -> [ObjectiveDef] from area-objectives.json (Radar room targets + optional progress flags).
    private Dictionary<string, IReadOnlyList<ObjectiveDef>> LoadAreaObjectiveMap()
    {
        var map = new Dictionary<string, IReadOnlyList<ObjectiveDef>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(AreaObjectivesPath))
        {
            LogMessage($"ExileCampaigns -> area-objectives map not found at {AreaObjectivesPath}; objective path fallback disabled.");
            return map;
        }
        var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(AreaObjectivesPath));
        foreach (var prop in root.Properties())
        {
            if (prop.Name.StartsWith("_")) continue;
            if (prop.Value is not Newtonsoft.Json.Linq.JArray arr) continue;
            var list = new List<ObjectiveDef>();
            foreach (var e in arr)
            {
                var name = (string?)e["name"];
                var rooms = (e["rooms"] as Newtonsoft.Json.Linq.JArray)?
                    .Select(r => (string?)r).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
                    ?? new List<string>();
                var flags = (e["progressFlags"] as Newtonsoft.Json.Linq.JArray)?
                    .Select(r => (string?)r).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
                    ?? new List<string>();
                var entityPath = (string?)e["entityPath"];
                // room-based OR entity-based: one of rooms / entityPath must be present.
                if (!string.IsNullOrWhiteSpace(name) && (rooms.Count > 0 || !string.IsNullOrWhiteSpace(entityPath)))
                    list.Add(new ObjectiveDef(name!, rooms, flags, string.IsNullOrWhiteSpace(entityPath) ? null : entityPath));
            }
            if (list.Count > 0) map[prop.Name] = list;
        }
        return map;
    }

    // lazily grab the Radar bridge methods; retry until present (Radar may init after us)
    private void EnsureRadarBridge()
    {
        if (_radarLookForRoute != null) return;
        try
        {
            _radarLookForRoute = GameController.PluginBridge
                .GetMethod<Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task>>("Radar.LookForRoute");
            _radarClusterTarget = GameController.PluginBridge
                .GetMethod<Func<string, int, Vector2[]>>("Radar.ClusterTarget");
        }
        catch { /* bridge not ready */ }
    }

    // called from Tick (throttled). resolves the current step's target and (re)starts the Radar route on step/target change
    private void UpdatePathTarget()
    {
        if (!PathsEnabled) { CancelPath(); CancelObjectivePaths(); _lastStepForTarget = -1; _currentTarget = null; return; }
        if ((DateTime.Now - _lastPathPoll).TotalSeconds < 0.25) return;
        _lastPathPoll = DateTime.Now;

        EnsureRadarBridge();
        if (_radarLookForRoute == null) return;   // Radar not installed/enabled, no path, no errors

        // step changed: drop stale path and re-resolve
        if (_route.Current != _lastStepForTarget)
        {
            _lastStepForTarget = _route.Current;
            _currentTarget = null;
            CancelPath();
            ResetObjectiveProgress();
        }

        var step = _route.CurrentStep?.Step;
        if (step == null) return;
        var areaId = _route.CurrentAreaId;

        // multi-objective room step (e.g. 3 Rust obelisks): draw a line to each remaining objective room,
        // reacting to per-obelisk progress flags. takes over the path for this step (no single target).
        if (UpdateObjectivePaths(step, areaId)) return;

        if (_currentTarget != null) return;       // already routing for this step
        var target = _targetResolver.Resolve(GameController, step, _radarClusterTarget, areaId);
        if (target == null)
        {
            // single-resolve only honors the FIRST Tile child (via Meta.PathTarget). when it can't resolve
            // (the present staircase is a later variant), try the nearest across ALL the step's Tile children.
            var tiles = _route.CurrentStep?.Model is { } model ? GuidanceView.PathTilePatterns(model) : null;
            if (tiles is { Count: > 0 })
                target = _targetResolver.ResolveTiles(GameController, tiles, _radarClusterTarget);
        }
        if (target == null) return;               // nothing resolvable yet, retry next poll

        _currentTarget = target;
        RecordDiagPathTarget(target.Value);
        StartRoute(target.Value);
    }

    // returns true if the current step draws a line PER target (All mode). Nearest objectives return false so
    // the single-target Resolve()/ResolveTiles flow in UpdatePathTarget draws one line to the nearest target.
    // reads the v2 Paths[] children directly -- the old count>1 / Meta.PathTarget inference is gone.
    private bool UpdateObjectivePaths(ParsedStep step, string? areaId)
    {
        var model = _route.CurrentStep?.Model;
        if (model == null || !GuidanceView.WantsAllPaths(model)) { CancelObjectivePaths(); return false; }

        var tilePats = GuidanceView.PathTilePatterns(model);
        var entPats = GuidanceView.PathEntityPatterns(model).ToList();
        var roomPats = GuidanceView.PathRoomPatterns(model);

        // tile (+ optional entity upgrade): a route per tile cluster across all Tile children, each snapping to
        // its precise live entity when one loads. tiles resolve at unlimited range so every one draws at once.
        if (tilePats.Count > 0 && _radarClusterTarget != null && UpdateAllTileObjectivePaths(tilePats, entPats))
            return true;

        // entity-only: a route per live entity across all Entity children, each dropped when it goes non-interactable.
        if (entPats.Count > 0) return UpdateEntityObjectivePaths(entPats);

        // room: a route to each authored Room center, hidden as per-room progress flags flip.
        if (roomPats.Count > 0) return UpdateRoomObjectivePaths(roomPats, step, areaId);

        CancelObjectivePaths();
        return false;
    }

    // room multi-objective: a route to each authored Room center (static, so start once), hidden as per-room
    // progress flags flip. a single room still routes here (a line straight to it), like the entity flow.
    private bool UpdateRoomObjectivePaths(IReadOnlyList<string> roomPatterns, ParsedStep step, string? areaId)
    {
        var centers = _targetResolver.ResolveRoomCenters(GameController, roomPatterns);
        if (centers.Count < 1) { CancelObjectivePaths(); return false; }   // rooms not in this instance yet
        if (_objectiveTargets == null)
        {
            CancelPath();              // a single route may have started before rooms resolved; drop it
            _currentTarget = null;
            StartObjectiveRoutes(centers);
        }
        UpdateObjectiveDone(step, areaId);
        return true;
    }

    // a route per tile cluster across ALL the step's Tile children, each slot snapping to its live entity when
    // one loads nearby. the tile baseline is cached once per step (KMeans centroids jitter per call); only entity
    // upgrades move a slot. replaces the old count/Meta-gated hybrid: reads the v2 Tile + Entity path children.
    private bool UpdateAllTileObjectivePaths(IReadOnlyList<string> tilePatterns, IReadOnlyList<string> entityPaths)
    {
        if (_objTileBase == null)
        {
            var pts = _targetResolver.ResolveTileClusters(GameController, tilePatterns, _radarClusterTarget);
            if (pts.Count == 0)   // tiles not processed yet: fall back to entity-only, else nothing this poll
                return entityPaths.Count > 0 && UpdateEntityObjectivePaths(entityPaths);
            // stable order so per-slot change detection holds frame to frame
            _objTileBase = pts.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        }

        var ents = entityPaths.Count > 0
            ? _targetResolver.ObjectiveEntityPositions(GameController, entityPaths, liveOnly: false)
            : (IReadOnlyList<Vector2>)System.Array.Empty<Vector2>();

        const float snapSq = 60f * 60f;     // a loaded entity within this of a tile centroid replaces it (precise)
        var resolved = new List<Vector2>(_objTileBase.Count);
        foreach (var c in _objTileBase)
        {
            Vector2 best = c; float bestSq = snapSq; bool found = false;
            foreach (var e in ents)
            {
                float d = Vector2.DistanceSquared(e, c);
                if (d < bestSq) { bestSq = d; best = e; found = true; }
            }
            resolved.Add(found ? best : c);
        }

        if (ResolvedChanged(resolved))
        {
            CancelPath();
            _currentTarget = null;
            StartObjectiveRoutes(resolved);
        }
        return true;
    }

    // true when the new target set differs enough to warrant re-requesting routes (count change or a slot
    // moved > ~5 grid -- i.e. a tile->entity upgrade). entities are static, so this fires once then settles.
    private bool ResolvedChanged(List<Vector2> next)
    {
        var cur = _objectiveTargets;
        if (cur == null || cur.Count != next.Count) return true;
        for (int i = 0; i < cur.Count; i++)
            if (Vector2.DistanceSquared(cur[i], next[i]) > 25f) return true;
        return false;
    }

    // entity-based multi-objective: draw a line to each matching entity, drop it the moment its entity goes
    // non-interactable (Targetable.isTargetable=false on activate). no quest flags -- the entity state IS the
    // per-objective completion signal.
    private bool UpdateEntityObjectivePaths(IReadOnlyList<string> entityPaths)
    {
        var all = _targetResolver.ObjectiveEntityPositions(GameController, entityPaths, liveOnly: false);
        // count 1 still routes here (not via the normal path): the single-target Resolve() is distance-capped
        // and entity-grid only, so a lone far objective (e.g. the Expedition Titan Rune, no Targetable) gets
        // no path from zone entry. an objective-route draws straight to its grid at any distance once loaded.
        if (all.Count < 1) { CancelObjectivePaths(); return false; }   // none loaded yet: let normal path / pathTarget try

        if (_objectiveTargets == null)
        {
            CancelPath();              // a single route may have started before all stakes loaded; drop it
            _currentTarget = null;
            StartObjectiveRoutes(all);
        }
        var live = _targetResolver.ObjectiveEntityPositions(GameController, entityPaths, liveOnly: true);
        MarkObjectiveDoneNotIn(live);
        return true;
    }

    // hide any started target with no live (interactable) entity at its position -- that sub-objective is done.
    private void MarkObjectiveDoneNotIn(IReadOnlyList<Vector2> live)
    {
        var tgts = _objectiveTargets;
        var done = _objectiveDone;
        if (tgts == null || done == null) return;
        for (int i = 0; i < tgts.Count && i < done.Length; i++)
        {
            if (done[i]) continue;
            bool stillLive = false;
            foreach (var l in live)
                if (Vector2.DistanceSquared(l, tgts[i]) < 25f) { stillLive = true; break; }
            if (!stillLive) done[i] = true;
        }
    }

    // when a sub-objective progress flag newly flips true the player is standing on the one they just finished,
    // so mark the objective room nearest them done (its line stops drawing). never cancels a Radar route.
    private void UpdateObjectiveDone(ParsedStep step, string? areaId)
    {
        var flags = _targetResolver.ResolveObjectiveProgressFlags(step, areaId);
        if (flags.Count == 0) return;
        var qf = ReadQuestFlags();
        if (qf == null) return;

        var trueNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in qf) if (kv.Value) trueNow.Add(kv.Key.ToString());

        if (_objectiveFlagSnapshot == null) { _objectiveFlagSnapshot = trueNow; return; }   // seed, no retro-mark

        foreach (var f in flags)
            if (trueNow.Contains(f) && !_objectiveFlagSnapshot.Contains(f))   // a sub-objective just completed
                MarkNearestObjectiveDone();
        _objectiveFlagSnapshot = trueNow;
    }

    private void MarkNearestObjectiveDone()
    {
        var tgts = _objectiveTargets;
        var done = _objectiveDone;
        if (tgts == null || done == null) return;
        var p = PlayerGridXY();
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < tgts.Count && i < done.Length; i++)
        {
            if (done[i]) continue;
            float d = p is { } pp ? Vector2.DistanceSquared(tgts[i], pp) : i;
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best >= 0) done[best] = true;
    }

    private void StartObjectiveRoutes(IReadOnlyList<Vector2> targets)
    {
        CancelObjectivePaths();
        _objectiveTargets = targets.ToList();
        _objectivePaths = new List<Vector2i>?[targets.Count];
        _objectiveDone = new bool[targets.Count];
        _objCts = new CancellationTokenSource();
        var token = _objCts.Token;
        for (int i = 0; i < targets.Count; i++)
        {
            int idx = i;
            try
            {
                _ = _radarLookForRoute!(targets[i], path =>
                {
                    if (token.IsCancellationRequested || path is not { Count: > 0 }) return;
                    var slots = _objectivePaths;
                    // copy: Radar may keep mutating the list it handed us; Render must read a stable snapshot.
                    if (slots != null && idx < slots.Length) slots[idx] = new List<Vector2i>(path);
                }, token);
            }
            catch (Exception ex) { LogError($"ExileCampaigns -> objective path request failed: {ex.Message}"); }
        }
    }

    private void CancelObjectivePaths()
    {
        try { _objCts.Cancel(); } catch { /* ignore */ }
        _objCts = new CancellationTokenSource();
        _objectivePaths = null;
        _objectiveTargets = null;
        _objectiveDone = null;
    }

    // new step: forget objective progress + routes so the next poll re-resolves cleanly.
    private void ResetObjectiveProgress()
    {
        CancelObjectivePaths();   // clears the targets + done arrays too
        _objectiveFlagSnapshot = null;
        _objTileBase = null;
    }

    private Vector2? PlayerGridXY()
    {
        try
        {
            var pos = GameController.Player?.GetComponent<Positioned>();
            return pos == null ? null : new Vector2(pos.GridX, pos.GridY);
        }
        catch { return null; }
    }

    private void StartRoute(Vector2 target)
    {
        try
        {
            _pathCts.Cancel();
            _pathCts = new CancellationTokenSource();
            _currentPath = null;
            var token = _pathCts.Token;
            // Radar keeps invoking the callback with a fresh path as the player moves. copy it: Radar may keep
            // mutating the same list, and Render reads _currentPath concurrently.
            _ = _radarLookForRoute!(target, path =>
            {
                if (!token.IsCancellationRequested && path is { Count: > 0 })
                    _currentPath = new List<Vector2i>(path);
            }, token);
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> path request failed: {ex.Message}"); }
    }

    private void CancelPath()
    {
        try { _pathCts.Cancel(); } catch { /* ignore */ }
        _pathCts = new CancellationTokenSource();
        _currentPath = null;
    }

    // read per-area terrain + reset routing so the next poll re-resolves for the new zone
    private void OnAreaChangedPathing()
    {
        try { _heightData = GameController.IngameState.Data.RawTerrainHeightData; }
        catch { _heightData = null; }
        // Radar's ExpectedCount per multi-instance tile for this area, so ClusterTarget splits a 2-staircase
        // tile the way Radar does instead of averaging both into one unreachable midpoint. only the >1 tiles.
        try
        {
            _targetResolver.RadarTileCounts = RadarFileTargetsList()
                .Where(p => p.Kind == TargetKind.Tile && p.Count > 1)
                .GroupBy(p => p.Match, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Count, StringComparer.OrdinalIgnoreCase);
        }
        catch { _targetResolver.RadarTileCounts = null; }
        _lastStepForTarget = -1;
        _currentTarget = null;
        CancelPath();
        ResetObjectiveProgress();
    }

    // --- drawing (called from Render) ---

    private void DrawStepPath()
    {
        if (!PathsEnabled) return;
        var hd = _heightData;
        if (hd == null) return;

        SubMap? largeMap = null;
        try { largeMap = GameController.Game.IngameState.IngameUi.Map?.LargeMap?.AsObject<SubMap>(); }
        catch { /* map element not ready */ }
        var mapVisible = largeMap is { IsVisible: true };

        DrawOnePath(_currentPath, hd, mapVisible, largeMap);

        // multi-objective steps: a line to each not-yet-completed objective room. array slots (no List version
        // churn from the background Radar callback); skip targets marked done (their sub-objective is finished).
        var objs = _objectivePaths;
        var done = _objectiveDone;
        if (objs != null)
            for (int i = 0; i < objs.Length; i++)
            {
                if (done != null && i < done.Length && done[i]) continue;
                DrawOnePath(objs[i], hd, mapVisible, largeMap);
            }
    }

    private void DrawOnePath(List<Vector2i>? path, float[][] hd, bool mapVisible, SubMap? largeMap)
    {
        if (path == null || path.Count < 2) return;

        if (Settings.Path.ShowPathOnMinimap && mapVisible && largeMap != null)
            DrawPathMinimap(path, hd, largeMap);

        if (Settings.Path.ShowPathOnGround && (!mapVisible || !Settings.Path.ShowGroundPathOnlyWithClosedMap))
            DrawPathGround(path, hd);
    }

    private void DrawPathGround(List<Vector2i> path, float[][] hd)
    {
        var cam = GameController.IngameState.Camera;
        var color = Settings.Path.PathColor.Value;
        var thickness = Settings.Path.PathThickness.Value;
        var nth = Math.Max(1, Settings.Path.DrawEveryNthSegment.Value);
        var wr = GameController.Window.GetWindowRectangle();
        var rect = new RectangleF(0, 0, wr.Width, wr.Height);

        Vector2? prev = null;
        for (var i = 0; i < path.Count; i++)
        {
            if (i % nth != 0 && i != path.Count - 1) continue;
            var e = path[i];
            if (!InBounds(e, hd)) { prev = null; continue; }
            Vector2 screen = cam.WorldToScreen(
                new Vector3(e.X * GridToWorldMultiplier, e.Y * GridToWorldMultiplier, hd[e.Y][e.X]));
            if (prev is { } p && (rect.Contains(p.X, p.Y) || rect.Contains(screen.X, screen.Y)))
                Graphics.DrawLine(p, screen, thickness, color);
            prev = screen;
        }
    }

    private void DrawPathMinimap(List<Vector2i> path, float[][] hd, SubMap largeMap)
    {
        var pos = GameController.Player?.GetComponent<Positioned>();
        var render = GameController.Player?.GetComponent<Render>();
        if (pos == null || render == null) return;

        var playerGrid = new Vector2(pos.GridX, pos.GridY);
        var playerHeight = -render.UnclampedHeight;
        var mapCenter = largeMap.MapCenter;
        var mapScale = largeMap.MapScale;
        var color = Settings.Path.PathColor.Value;
        var thickness = Settings.Path.PathThickness.Value;
        var nth = Math.Max(1, Settings.Path.DrawEveryNthSegment.Value);

        Vector2? prev = null;
        for (var i = 0; i < path.Count; i++)
        {
            if (i % nth != 0 && i != path.Count - 1) continue;
            var e = path[i];
            if (!InBounds(e, hd)) { prev = null; continue; }
            var delta = GridDeltaToMapDelta(new Vector2(e.X, e.Y) - playerGrid, playerHeight + hd[e.Y][e.X], mapScale);
            var screen = mapCenter + delta;
            if (prev is { } p)
                Graphics.DrawLine(p, screen, thickness, color);
            prev = screen;
        }
    }

    private static Vector2 GridDeltaToMapDelta(Vector2 delta, float deltaZ, double mapScale)
    {
        deltaZ /= GridToWorldMultiplier; // z is world units, convert to grid units
        return (float)mapScale * new Vector2(
            (delta.X - delta.Y) * CameraAngleCos,
            (deltaZ - (delta.X + delta.Y)) * CameraAngleSin);
    }

    private static bool InBounds(Vector2i e, float[][] hd)
        => e.Y >= 0 && e.Y < hd.Length && e.X >= 0 && e.X < hd[e.Y].Length;
}
