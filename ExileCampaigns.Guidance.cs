using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExileCampaigns.Guide;
using ExileCampaigns.Tracking;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using GameOffsets.Native;
using ImGuiNET;
using SharpDX;
using ExileCampaigns.Rendering;
using ExileCore.PoEMemory;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using RectangleF = SharpDX.RectangleF;
using Vector4 = System.Numerics.Vector4;

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

    // nearest-mode tile step that split into several candidate clusters (e.g. many "stairsup", only one
    // reachable this run). routes to all of them via the objective-route slots; DrawStepPath draws only the
    // closest reachable (shortest path) instead of all. distinct from All-mode, which draws every path.
    private bool _tileCandidateMode;


    // per-area terrain, read on AreaChange (needed to project grid points to screen)
    private float[][]? _heightData;

    private DateTime _lastPathPoll;

    private bool PathsEnabled => Settings.Path.ShowPathOnGround || Settings.Path.ShowPathOnMinimap;

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

        var step = _route.CurrentStep?.Model;
        if (step == null) return;
        var areaId = _route.CurrentAreaId;

        // multi-objective room step (e.g. 3 Rust obelisks): draw a line to each remaining objective room,
        // reacting to per-obelisk progress flags. takes over the path for this step (no single target).
        if (UpdateObjectivePaths(step, areaId)) return;

        if (_currentTarget != null || _tileCandidateMode) return;   // already routing for this step

        // nearest-mode tile step: a Tile child can resolve to several clusters (e.g. many "stairsup", only
        // one reachable in this instance). route to EVERY candidate and draw the closest reachable one -- a
        // geometric-nearest pick can land on an unreachable instance, Radar returns no path, nothing draws.
        var tiles = GuidanceView.PathTilePatterns(step);
        if (tiles.Count > 0 && _radarClusterTarget != null)
        {
            var clusters = _targetResolver.ResolveTileClusters(GameController, tiles, _radarClusterTarget);
            if (clusters.Count > 1)
            {
                CancelPath();
                _currentTarget = null;
                _tileCandidateMode = true;
                StartObjectiveRoutes(clusters);
                RecordDiagPathTarget(clusters[0]);
                return;
            }
        }

        var target = _targetResolver.Resolve(GameController, step, _radarClusterTarget, areaId);
        if (target == null) return;               // nothing resolvable yet, retry next poll

        _currentTarget = target;
        RecordDiagPathTarget(target.Value);
        StartRoute(target.Value);
    }

    // returns true if the current step draws a line PER target (All mode). Nearest objectives return false so
    // the single-target Resolve()/ResolveTiles flow in UpdatePathTarget draws one line to the nearest target.
    // reads the v2 Paths[] children directly -- the old count>1 / Meta.PathTarget inference is gone.
    private bool UpdateObjectivePaths(RouteStep step, string? areaId)
    {
        // tile-candidate routes (Nearest mode, many clusters) live in the same objective-path slots. don't let
        // the Nearest cancel below wipe them -- bail without touching them; UpdatePathTarget's guard returns next.
        if (_tileCandidateMode) return false;

        var model = _route.CurrentStep?.Model;
        if (model == null) { CancelObjectivePaths(); return false; }

        var tilePats = GuidanceView.PathTilePatterns(model);
        var entPats = GuidanceView.PathEntityPatterns(model).ToList();

        // entity-only path children route to matching entities regardless of All/Nearest mode -- they don't use
        // Radar tiles so the mode distinction doesn't apply. WorldItems vanish when looted, so done-marking
        // clears the path naturally.
        if (entPats.Count > 0 && tilePats.Count == 0) return UpdateEntityObjectivePaths(entPats);

        // tile + room multi-objective: All mode only.
        if (!GuidanceView.WantsAllPaths(model)) { CancelObjectivePaths(); return false; }

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
    private bool UpdateRoomObjectivePaths(IReadOnlyList<string> roomPatterns, RouteStep step, string? areaId)
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
    private void UpdateObjectiveDone(RouteStep step, string? areaId)
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
        _tileCandidateMode = false;
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

        // gather every path we'll draw this frame so we can flag the shortest. array slots for the objective
        // paths (no List churn from the background Radar callback); skip targets marked done.
        var live = new List<List<Vector2i>>();
        if (_currentPath is { Count: >= 2 }) live.Add(_currentPath);
        var objs = _objectivePaths;
        var done = _objectiveDone;
        if (objs != null)
            for (int i = 0; i < objs.Length; i++)
            {
                if (done != null && i < done.Length && done[i]) continue;
                if (objs[i] is { Count: >= 2 } op) live.Add(op);
            }

        // tile-candidate step: many clusters were routed; only reachable ones returned a path. draw just the
        // closest reachable (shortest) -- the others are unreachable instances or farther staircases.
        if (_tileCandidateMode)
        {
            if (live.Count == 0) return;
            int pick = 0;
            float bestLen = PathLength(live[0]);
            for (int i = 1; i < live.Count; i++)
            {
                float len = PathLength(live[i]);
                if (len < bestLen) { bestLen = len; pick = i; }
            }
            DrawOnePath(live[pick], hd, mapVisible, largeMap, Settings.Path.PathColor.Value);
            return;
        }

        int shortest = -1;
        if (Settings.Path.HighlightShortest.Value && live.Count > 1)
        {
            float best = float.MaxValue;
            for (int i = 0; i < live.Count; i++)
            {
                float len = PathLength(live[i]);
                if (len < best) { best = len; shortest = i; }
            }
        }

        var normal = Settings.Path.PathColor.Value;
        var hot = Settings.Path.ShortestPathColor.Value;
        for (int i = 0; i < live.Count; i++)
            DrawOnePath(live[i], hd, mapVisible, largeMap, i == shortest ? hot : normal);
    }

    // path length in grid units (Radar hands back grid points). only used to pick the shortest of several.
    private static float PathLength(List<Vector2i> path)
    {
        float d = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            float dx = path[i].X - path[i - 1].X;
            float dy = path[i].Y - path[i - 1].Y;
            d += MathF.Sqrt(dx * dx + dy * dy);
        }
        return d;
    }

    private void DrawOnePath(List<Vector2i>? path, float[][] hd, bool mapVisible, SubMap? largeMap, Color color)
    {
        if (path == null || path.Count < 2) return;

        if (Settings.Path.ShowPathOnMinimap && mapVisible && largeMap != null)
            DrawPathMinimap(path, hd, largeMap, color);

        if (Settings.Path.ShowPathOnGround && (!mapVisible || !Settings.Path.ShowGroundPathOnlyWithClosedMap))
        {
            bool comets = Settings.Path.ShowComets;
            if (!(comets && Settings.Path.CometsOnly))
                DrawPathGround(path, hd, color);
            if (comets)
                DrawPathGroundComets(path, hd);
        }
    }

    private void DrawPathGround(List<Vector2i> path, float[][] hd, Color color)
    {
        var cam = GameController.IngameState.Camera;
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
            if (prev is { } p && (rect.Contains(p.X, p.Y) || rect.Contains(screen.X, screen.Y))
                && !PointInSidePanel(p) && !PointInSidePanel(screen))   // skip segments under a side panel
                Graphics.DrawLine(p, screen, thickness, color);
            prev = screen;
        }
    }

    private void DrawPathMinimap(List<Vector2i> path, float[][] hd, SubMap largeMap, Color color)
    {
        var pos = GameController.Player?.GetComponent<Positioned>();
        var render = GameController.Player?.GetComponent<Render>();
        if (pos == null || render == null) return;

        var playerGrid = new Vector2(pos.GridX, pos.GridY);
        var playerHeight = -render.UnclampedHeight;
        var mapCenter = largeMap.MapCenter;
        var mapScale = largeMap.MapScale;
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
            if (prev is { } p && !PointInSidePanel(p) && !PointInSidePanel(screen))   // skip segments under a side panel
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

    // --- flowing comets ---

    private const string CometTexture = "ExileCampaigns_Comet";
    private bool _cometTexLoaded;

    // load the comet sprite once. white/grayscale png, head pointing +X, tinted at draw time
    private void InitCometTexture()
    {
        if (_cometTexLoaded) return;
        try
        {
            var path = Path.Combine(DirectoryFullName, "textures", "comet_decal.png");
            if (File.Exists(path))
            {
                Graphics.InitImage(CometTexture, path);
                _cometTexLoaded = true;
            }
            else LogError($"ExileCampaigns -> comet texture not found: {path}");
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> comet texture init failed: {ex.Message}"); }
    }

    // slide N comet sprites along the path toward the objective. resample by arc length so spacing is even
    // (Radar points are dense + uneven), then place an oriented quad at each comet's animated distance.
    private void DrawPathGroundComets(List<Vector2i> path, float[][] hd)
    {
        if (!_cometTexLoaded) return;
        int n = path.Count;
        if (n < 2) return;

        // order from the player end -> objective end so comets flow toward the goal
        var player = PlayerGridXY();
        bool rev = player is { } pg &&
            Vector2.DistanceSquared(new Vector2(path[0].X, path[0].Y), pg) >
            Vector2.DistanceSquared(new Vector2(path[n - 1].X, path[n - 1].Y), pg);

        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            var e = rev ? path[n - 1 - i] : path[i];
            pts[i] = new Vector2(e.X, e.Y);
        }
        var cum = new float[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + Vector2.Distance(pts[i], pts[i - 1]);
        float total = cum[n - 1];
        if (total < 1f) return;

        // fixed grid spacing -> comet count scales with path length.
        float spacing = Math.Max(1f, Settings.Path.CometSpacing.Value);
        float speed = Settings.Path.CometSpeed.Value;
        float size = Settings.Path.CometSize.Value;

        var cam = GameController.IngameState.Camera;
        var texId = Graphics.GetTextureId(CometTexture);
        var color = Settings.Path.CometColor.Value;
        // near-plane guard: a quad edge can't sanely exceed half the screen height. catches the WorldToScreen
        // blowup when a corner falls near/behind the camera (foreground comets near the bottom of the screen).
        float maxEdge = GameController.Window.GetWindowRectangle().Height * 0.5f;

        // anchor the comet grid to the objective (fixed) end, not the player end. Radar keeps re-cutting the
        // path behind you as you walk, so measuring distance from the player end makes every comet jump on each
        // update. d here is arc-from-player but the spacing is laid out from `total` (the objective), so each
        // comet stays glued to the same ground spot; they only spawn/despawn at the player end.
        float off = (float)(ImGui.GetTime() * speed) % spacing;
        for (float d = total - spacing + off; d > 0f; d -= spacing)
            DrawOneComet(pts, cum, d, size, hd, cam, texId, color, maxEdge);
    }

    private void DrawOneComet(Vector2[] pts, float[] cum, float d, float size,
        float[][] hd, Camera cam, nint texId, Color color, float maxEdge)
    {
        int seg = 1;
        while (seg < cum.Length - 1 && cum[seg] < d) seg++;
        float segLen = cum[seg] - cum[seg - 1];
        float t = segLen > 0.001f ? (d - cum[seg - 1]) / segLen : 0f;
        Vector2 a = pts[seg - 1], b = pts[seg];
        Vector2 fwd = b - a;
        if (fwd.LengthSquared() < 1e-4f) return;
        fwd = Vector2.Normalize(fwd);
        var right = new Vector2(-fwd.Y, fwd.X);
        Vector2 pos = Vector2.Lerp(a, b, t);

        // head sits at pos; quad runs 0.6 back (tail) + 0.4 forward, half-width each side. matches the sprite
        // layout (head ~60% to the right). corner order a,b,c,d = uv (0,0)(1,0)(1,1)(0,1)
        Vector2 baseB = pos - 0.6f * size * fwd;
        Vector2 g00 = baseB + 0.5f * size * right;
        Vector2 g10 = baseB + size * fwd + 0.5f * size * right;
        Vector2 g11 = baseB + size * fwd - 0.5f * size * right;
        Vector2 g01 = baseB - 0.5f * size * right;

        if (!ProjectGrid(g00, hd, cam, out var s00)) return;
        if (!ProjectGrid(g10, hd, cam, out var s10)) return;
        if (!ProjectGrid(g11, hd, cam, out var s11)) return;
        if (!ProjectGrid(g01, hd, cam, out var s01)) return;

        // drop the comet if projection blew up (corner near/behind camera) -> giant or non-finite quad
        float e0 = Vector2.Distance(s00, s10), e1 = Vector2.Distance(s10, s11);
        float e2 = Vector2.Distance(s11, s01), e3 = Vector2.Distance(s01, s00);
        if (!float.IsFinite(e0) || !float.IsFinite(e1) || !float.IsFinite(e2) || !float.IsFinite(e3)) return;
        if (e0 > maxEdge || e1 > maxEdge || e2 > maxEdge || e3 > maxEdge) return;

        // hide under an open side panel
        if (PointInSidePanel(s00) || PointInSidePanel(s10) || PointInSidePanel(s11) || PointInSidePanel(s01)) return;

        Graphics.DrawQuad(texId, s00, s10, s11, s01, color);
    }

    // grid (float) -> screen, sampling terrain height at the nearest cell. false if out of bounds
    private bool ProjectGrid(Vector2 g, float[][] hd, Camera cam, out Vector2 screen)
    {
        screen = default;
        int gx = (int)MathF.Round(g.X), gy = (int)MathF.Round(g.Y);
        if (gy < 0 || gy >= hd.Length || gx < 0 || gx >= hd[gy].Length) return false;
        screen = cam.WorldToScreen(
            new Vector3(g.X * GridToWorldMultiplier, g.Y * GridToWorldMultiplier, hd[gy][gx]));
        return true;
    }
}

// draws authored MinimapIcons on the large map for every step in the current area.
// anchors are resolved once and cached per area instance; entity anchors are resolved live each frame.
public partial class ExileCampaigns
{
    private string? _mmiCacheKey;
    // Size is the per-icon override (null = global default), applied at draw so the global slider stays live;
    // StepId is the owning step, so the renderer can pulse the current objective's icons each frame.
    private readonly List<(Vector2 Coord, SpriteIcon Icon, uint Tint, float? Size, string StepId)> _mmiStatic = new();

    private void DrawMinimapIcons()
    {
        if (!Settings.MinimapIcons.Enable || _routeStore == null) return;

        SubMap? largeMap = null;
        try { largeMap = GameController.Game.IngameState.IngameUi.Map?.LargeMap?.AsObject<SubMap>(); }
        catch { }
        if (largeMap is not { IsVisible: true }) return;

        var playerPos = GameController.Player?.GetComponent<Positioned>();
        var playerRender = GameController.Player?.GetComponent<Render>();
        if (playerPos == null || playerRender == null) return;

        var playerGrid = new Vector2(playerPos.GridX, playerPos.GridY);
        var playerHeight = -playerRender.UnclampedHeight;
        var mapCenter = largeMap.MapCenter;
        var mapScale = largeMap.MapScale;
        float globalSize = Settings.MinimapIcons.IconSize.Value;   // per-icon Size overrides this when set

        var specs = GuidanceView.MinimapIconsForArea(_routeStore.Steps, _areaId);
        var currentId = _route.CurrentStep?.Model?.Id;   // icons of this step pulse
        var visibleIds = LookaheadStepIds(Settings.MinimapIcons.Lookahead.Value);   // only current + next N steps draw

        var areaKey = AreaInstanceKey();
        if (areaKey != _mmiCacheKey)
        {
            _mmiStatic.Clear();
            foreach (var s in specs)
            {
                if (s.Anchor.Kind == TargetKind.Entity) continue;
                if (_targetResolver.ResolveTargetCoord(GameController, s.Anchor, _radarClusterTarget) is { } c)
                    _mmiStatic.Add((c, SpriteAtlas.Parse(s.IconKey), s.Tint, s.Size, s.StepId));
            }
            _mmiCacheKey = areaKey;
        }

        foreach (var (coord, icon, tint, size, stepId) in _mmiStatic)
        {
            if (!visibleIds.Contains(stepId)) continue;   // outside the lookahead window
            var (ds, dt) = Pulse(stepId == currentId, size ?? globalSize, tint);
            DrawMinimapIcon(coord, icon, dt, ds, playerGrid, playerHeight, mapCenter, mapScale);
        }

        foreach (var s in specs)
        {
            if (s.Anchor.Kind != TargetKind.Entity) continue;
            if (!visibleIds.Contains(s.StepId)) continue;   // outside the lookahead window
            // an entity target can match several live entities (distinct drops / duplicate objects) -- icon each
            var icon = SpriteAtlas.Parse(s.IconKey);
            var (ds, dt) = Pulse(s.StepId == currentId, s.Size ?? globalSize, s.Tint);
            foreach (var c in _targetResolver.ResolveEntityCoords(GameController, s.Anchor))
                DrawMinimapIcon(c, icon, dt, ds, playerGrid, playerHeight, mapCenter, mapScale);
        }
    }

    // step ids whose icons may draw: the current step plus the next `lookahead` real (non-header) steps in
    // sequence. headers are skipped, not counted. combined with the area filter on specs this means an icon
    // shows only when its step is in the current area AND within the lookahead window.
    private HashSet<string> LookaheadStepIds(int lookahead)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steps = _route.Steps;
        if (steps == null || steps.Count == 0) return ids;
        int taken = 0;
        for (int i = _route.Current; i < steps.Count; i++)
        {
            var id = steps[i].Model?.Id;
            if (id == null) continue;   // header row
            ids.Add(id);
            if (++taken > lookahead) break;   // current + lookahead more
        }
        return ids;
    }

    // pulse the current objective's icons: a gentle scale + alpha breathe so they read as "active". returns
    // the (size, tint) to draw with; identity when the icon isn't current or pulsing is disabled.
    private (float Size, uint Tint) Pulse(bool current, float size, uint tint)
    {
        if (!current || !Settings.MinimapIcons.PulseCurrent) return (size, tint);
        float s = MathF.Sin((float)ImGui.GetTime() * 4.5f);       // ~0.7 Hz, -1..1
        float scale = 1f + 0.20f * s;                             // 0.80 .. 1.20
        float alphaMul = 0.6f + 0.4f * (0.5f + 0.5f * s);         // 0.60 .. 1.00
        uint a = (uint)Math.Clamp((int)(((tint >> 24) & 0xFF) * alphaMul), 0, 255);
        return (size * scale, (tint & 0x00FFFFFFu) | (a << 24));
    }

    private void DrawMinimapIcon(Vector2 gridCoord, SpriteIcon icon, uint tint, float size,
        Vector2 playerGrid, float playerHeight, Vector2 mapCenter, float mapScale)
    {
        float z = 0f;
        var hd = _heightData;
        if (hd != null)
        {
            int gy = (int)gridCoord.Y, gx = (int)gridCoord.X;
            if (gy >= 0 && gy < hd.Length && gx >= 0 && gx < hd[gy].Length) z = hd[gy][gx];
        }
        var screen = mapCenter + GridDeltaToMapDelta(gridCoord - playerGrid, playerHeight + z, mapScale);
        float half = size / 2f;
        var rect = new RectangleF(screen.X - half, screen.Y - half, size, size);
        if (OverlapsSidePanel(rect)) return;   // hide under an open side panel
        Graphics.DrawImage(IndicatorTexture, rect, SpriteAtlas.GetUVRect(icon), TintColor(tint));
    }

    // packed ARGB (0xAARRGGBB) -> SharpDX.Color.
    private static Color TintColor(uint argb) =>
        new Color((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    // area-instance identity so revisiting the same area id rebuilds the static cache.
    private string AreaInstanceKey()
    {
        try { return GameController?.Area?.CurrentArea?.Hash.ToString() ?? ""; }
        catch { return ""; }
    }
}

// Guides the player to a "Waypoint to X" step's destination on the open World Map panel.
// The waypoint icon nodes (texture .../InGame/9.dds) carry no area name in memory - only a hover
// tooltip - and the game lays them out in a STATIC per-area slot order (confirmed live), so a step's
// target areaId maps to a fixed slot index per act via a baked table (built once with the id overlay below).
//
// This file currently hosts the AUTHORING aid: label each visible node with its slot index + the SUSPECTED
// area name (WaypointNamesByAct once verified, else the act's WorldArea list in index order). Where the label
// matches the real area it confirms the slot; where it diverges it reveals an area the map omits as a node.
// The real destination indicator gets added once the areaId -> slot table is confirmed.
//
// Panel layout (verified live): WaypointsRoot only ever aliases ACT 1's icon holder. Acts 2-10 live in a
// nested Part -> Act tree (Part 2 nests deeper than Part 1), so the shown act's holder is found by icon
// texture (SearchVisibleIconHolder) rather than a fixed path. The viewed act is read off the panel tabs -
// selected tab = the child with IsVisible && IsActive - so acts 2-10 can be authored from town by flipping
// tabs, not only while standing in the act (ViewedActFromPanel).
public partial class ExileCampaigns
{
    // cache the derived act-ordered names so the ~2k-entry dict isn't walked every frame (dev overlay only).
    private int _wpNamesAct = -1;
    private List<string> _wpNames = new();

    // verified slot -> area names live in the pure Guide.WaypointSlots (shared with the destination resolver).

    private const string WpIconTex = "Art/Textures/Interface/2D/2DArt/UIImages/InGame/9.dds";

    // the shown act's waypoint icons sit under a per-act holder whose path differs by act (Part 2 nests
    // deeper than Part 1) and WaypointsRoot only ever aliases act 1. so find the holder generically: the
    // element with the most VISIBLE icon children is whichever act tab is open. null when nothing's shown.
    private Element? WaypointNodeHolder()
    {
        // act 1 keeps its verified holder (indices there are already baked); only that alias exists for it.
        var wp1 = Act1IconHolder();
        if (wp1 != null && HasVisibleIcon(wp1)) return wp1;
        return SearchVisibleIconHolder();
    }

    private Element? Act1IconHolder()
    {
        try
        {
            var wp = GameController.IngameState.IngameUi.WorldMap?.WaypointsRoot;
            if (wp == null || !wp.IsVisible) return null;
            return wp.GetChildAtIndex(0);
        }
        catch { return null; }
    }

    private static bool IsIcon(Element? c)
    {
        try { return c != null && c.IsVisible && string.Equals(c.TextureName, WpIconTex, StringComparison.Ordinal); }
        catch { return false; }
    }

    private static bool HasVisibleIcon(Element holder)
    {
        var kids = holder.Children;
        if (kids == null) return false;
        for (int i = 0; i < kids.Count; i++) if (IsIcon(kids[i])) return true;
        return false;
    }

    // DFS the world map subtree for the element with the most visible waypoint-icon children. only the open
    // act's icons read IsVisible, so parked acts score 0 and the shown act's holder wins. dev overlay only.
    private Element? SearchVisibleIconHolder()
    {
        Element? wm;
        try { wm = GameController.IngameState.IngameUi.WorldMap; } catch { return null; }
        if (wm == null || !wm.IsVisible) return null;

        Element? best = null;
        int bestCount = 0, guard = 0;
        var stack = new Stack<(Element el, int depth)>();
        stack.Push((wm, 0));
        while (stack.Count > 0 && guard++ < 4000)
        {
            var (el, depth) = stack.Pop();
            var kids = el?.Children;
            if (kids == null || depth > 16) continue;
            int iconCount = 0;
            for (int i = 0; i < kids.Count; i++)
            {
                var c = kids[i];
                if (c == null) continue;
                if (IsIcon(c)) iconCount++;
                else stack.Push((c, depth + 1));
            }
            if (iconCount > bestCount) { bestCount = iconCount; best = el; }
        }
        return bestCount > 0 ? best : null;
    }

    // the act's zones in WorldArea index order (what the world map slot order mostly follows): skip the
    // non-zone header rows (null id, Character Select, hideouts). positional guess only - verified by eye.
    private List<string> ActAreaNames(int act)
    {
        if (act == _wpNamesAct) return _wpNames;
        var names = new List<string>();
        try
        {
            var dict = GameController.Files.WorldAreas?.AreasIndexDictionary;
            if (dict != null)
                foreach (var kv in dict.OrderBy(k => k.Key))
                {
                    var a = kv.Value;
                    if (a == null || a.Act != act) continue;
                    var id = a.Id;
                    if (string.IsNullOrEmpty(id) || a.IsHideout || id.Contains("Hideout") || id == "CharacterSelect")
                        continue;
                    names.Add(string.IsNullOrEmpty(a.Name) ? id : a.Name);
                }
        }
        catch { }
        _wpNamesAct = act;
        _wpNames = names;
        return names;
    }

    // which act tab is open on the world map panel: the panel browses any unlocked act regardless of where
    // you stand, so authoring acts 2-10 keys off the VIEWED act. derived from the selected Part tab (0=Part1
    // acts 1-5, 1=Part2 acts 6-10, 2=Epilogue) plus the act's index within that part, walked up from the
    // holder. selection = the child with IsVisible && IsActive. returns 0 when unsure (caller falls back).
    private int ViewedActFromPanel(Element? holder)
    {
        if (holder == null) return 0;
        try
        {
            // WaypointsRoot aliases act 1 only, so its holder means act 1 no matter where you stand.
            // (compare by Address - ExileAPI hands back a fresh Element wrapper each access, so ReferenceEquals lies.)
            var a1 = Act1IconHolder();
            if (a1 != null && a1.Address == holder.Address) return 1;

            // Parts container sits at a stable path (3 tabs). its selected child gives the act base.
            var parts = GameController.IngameState.IngameUi.WorldMap?
                .GetChildAtIndex(2)?.GetChildAtIndex(0)?.GetChildAtIndex(1);
            int p = SelectedChildIndex(parts);
            int baseAct = p == 0 ? 1 : p == 1 ? 6 : -1;   // epilogue/unknown -> bail
            if (baseAct < 0) return 0;

            // the act index within the part: nearest ancestor of the holder that is a tab selector
            // (more than one child, exactly one shown). the holder sits under the shown child, so that
            // child's IndexInParent is the act index j.
            var el = holder;
            for (int hops = 0; el != null && hops < 20; hops++)
            {
                var parent = el.Parent;
                if (parent == null) break;
                if (parent.ChildCount > 1)
                {
                    var kids = parent.Children;
                    int vis = 0;
                    if (kids != null)
                        for (int i = 0; i < kids.Count; i++)
                            if (kids[i] is { IsVisible: true }) vis++;
                    if (vis == 1)
                    {
                        int idx = el.IndexInParent ?? -1;
                        if (idx >= 0) return baseAct + idx;
                    }
                }
                el = parent;
            }
        }
        catch { }
        return 0;
    }

    // index of the single shown child (IsVisible && IsActive), or -1.
    private static int SelectedChildIndex(Element? container)
    {
        var kids = container?.Children;
        if (kids == null) return -1;
        for (int i = 0; i < kids.Count; i++)
        {
            var c = kids[i];
            if (c != null && c.IsVisible && c.IsActive) return i;
        }
        return -1;
    }

    // authoring aid: draw each visible waypoint node's slot index + suspected area name, so slots can be
    // mapped by eye. gated on the dev overlay toggle. parked (undiscovered) nodes stack at one offscreen
    // point and read IsVisible=false, so they're skipped - they get mapped later once unlocked.
    private void DrawWaypointNodeIds()
    {
        if (!Settings.Dev.ShowDevOverlay) return;
        var holder = WaypointNodeHolder();
        var kids = holder?.Children;
        if (kids == null) return;

        // viewed act (map lets you browse any unlocked act's tab); fall back to the act you stand in.
        int act = ViewedActFromPanel(holder);
        if (act <= 0) act = GameController.Area?.CurrentArea?.Act ?? 0;
        var mapped = Guide.WaypointSlots.NamesByAct.TryGetValue(act, out var m) ? m : null;
        var names = act > 0 ? ActAreaNames(act) : new List<string>();

        // ImGui foreground list so the text scales - Graphics.DrawText's height arg is a no-op in this
        // ExileAPI build. ~70% of default, since a lot of node labels sit close and overlap at full size.
        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        float baseSize = ImGui.GetFontSize();
        if (baseSize <= 0) baseSize = 16f;
        const float scale = 0.7f;
        float textSize = baseSize * scale;
        uint fg = U32(Color.Yellow), bg = U32(new Color(0, 0, 0, 210));

        for (int i = 0; i < kids.Count; i++)
        {
            var node = kids[i];
            if (node == null || !node.IsVisible) continue;

            RectangleF rect;
            try { rect = node.GetClientRectCache; }   // top-left = the waypoint's anchor; W/H are junk (~1px)
            catch { continue; }
            if (rect.X <= 0 && rect.Y <= 0) continue;

            var name = mapped != null && i < mapped.Length ? mapped[i]
                     : i < names.Count ? names[i] : "?";
            var label = $"{i}  {name}";
            var sz = ImGui.CalcTextSize(label) * scale;
            var pos = new Vector2(rect.X, rect.Y - sz.Y - 3f);   // sit just above the icon
            dl.AddRectFilled(pos - new Vector2(3, 1), pos + sz + new Vector2(3, 1), bg);
            dl.AddText(font, textSize, pos, fg, label);
        }
    }

    // the current step's "Waypoint to X" destination as (act, slot), or null when this isn't a waypoint
    // step / the target area or its slot can't be resolved.
    private (int act, int slot)? CurrentWaypointTarget()
    {
        try
        {
            var model = _route.CurrentStep?.Model;
            if (model == null) return null;
            Guide.Objective? obj = null;
            foreach (var o in model.Objectives)
                if (o.Type == Guide.ObjectiveType.EnterArea && o.AreaTarget != null) { obj = o; break; }
            if (obj == null) return null;

            var area = GameController.Files.WorldAreas?.GetAreaByAreaId(obj.AreaTarget!.Value);
            if (area == null) return null;
            int slot = Guide.WaypointSlots.ResolveSlot(area.Act, area.Name ?? "");
            return slot < 0 ? null : (area.Act, slot);
        }
        catch { return null; }
    }

    // 0..1 pulse for the highlight (alpha + radius). wall-clock driven, no per-frame state.
    private static float WpPulse()
    {
        double t = (Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds * 3.0) + 1.0) / 2.0;
        return (float)t;
    }

    // user-facing: on a "Waypoint to X" step with the world map open, highlight the destination node,
    // or glow the correct Part/Act tab first when that tab isn't the one shown.
    private void DrawWaypointDestination()
    {
        if (!Settings.ShowWaypointHighlight) return;
        var target = CurrentWaypointTarget();
        if (target == null) return;

        var holder = WaypointNodeHolder();
        if (holder == null) return;   // map closed

        int targetPart = target.Value.act <= 5 ? 0 : 1;   // 0 = Part 1 (acts 1-5), 1 = Part 2 (acts 6-10)
        int viewedPart = SelectedChildIndex(PartsContainer());
        int viewedAct = ViewedActFromPanel(holder);

        var dl = ImGui.GetForegroundDrawList();
        float p = WpPulse();
        uint col = U32(new Color(255, 200, 60, (int)(120 + 135 * p)));   // gold, pulsing alpha

        var wm = GameController.IngameState.IngameUi.WorldMap;
        if (viewedPart != targetPart)
        {
            GlowTab(dl, wm?.GetPartButton(targetPart + 1), col, p);   // GetPartButton is 1-based (verify live)
            return;
        }
        if (viewedAct != target.Value.act)
        {
            GlowTab(dl, wm?.GetActButton(target.Value.act), col, p);
            return;
        }

        // right act shown -> ring the node
        var kids = holder.Children;
        if (kids == null || target.Value.slot >= kids.Count) return;
        var node = kids[target.Value.slot];
        if (node == null || !node.IsVisible) return;
        RectangleF rect;
        try { rect = node.GetClientRectCache; } catch { return; }
        if (rect.X <= 0 && rect.Y <= 0) return;

        // the waypoint art has no size, so rect x/y is its top-left anchor and the centre sits down-right of
        // it. the map zooms, so a fixed pixel offset drifts. the nearest visible neighbour's on-screen
        // distance tracks the zoom exactly, so express the offset + radius as fractions of it.
        var anchor = new Vector2(rect.X, rect.Y);
        // scale off the map area's on-screen height, not node spacing: the panel is fixed by resolution and
        // does not shift as more waypoints unlock, so the ring stays put over the whole campaign.
        float scale;
        try { scale = holder.GetClientRectCache.Height; } catch { scale = 500f; }
        if (scale < 1f) scale = 500f;

        var wo = Settings.WaypointOverlay;
        var center = anchor + new Vector2(wo.OffsetX * scale, wo.OffsetY * scale);
        float baseR = wo.Scale * scale;
        float radius = baseR * (1f + 0.15f * p);   // breathe outward
        dl.AddCircle(center, radius, col, 32, 2.5f);

        // step text to the right of the ring (vertically centred) so it's clear why it's flashing.
        var label = _route.CurrentStep?.Model?.Text;
        if (!string.IsNullOrEmpty(label))
        {
            var font = ImGui.GetFont();
            float baseSize = ImGui.GetFontSize();
            if (baseSize <= 0) baseSize = 16f;
            float ts = Math.Clamp(baseR * 0.85f, 11f, 44f);
            var tsz = ImGui.CalcTextSize(label) * (ts / baseSize);
            var tp = new Vector2(center.X + baseR * 1.15f + baseR * 0.4f, center.Y - tsz.Y / 2f);
            dl.AddRectFilled(tp - new Vector2(4, 2), tp + tsz + new Vector2(4, 2), U32(new Color(0, 0, 0, 180)));
            dl.AddText(font, ts, tp, col, label);
        }
    }

    // glow a tab button rect (Part/Act) when it isn't the one selected.
    private static void GlowTab(ImDrawListPtr dl, Element? btn, uint col, float p)
    {
        if (btn == null) return;
        RectangleF r;
        try { r = btn.GetClientRectCache; } catch { return; }
        if (r.Width <= 1 || r.Height <= 1) return;
        float pad = 2f + 3f * p;
        dl.AddRect(new Vector2(r.X - pad, r.Y - pad),
                   new Vector2(r.X + r.Width + pad, r.Y + r.Height + pad),
                   col, 4f, ImDrawFlags.None, 3f);
    }

    // the world map's Part-tab container (3 tabs), stable path. reused by the viewed-part check.
    private Element? PartsContainer()
    {
        try
        {
            return GameController.IngameState.IngameUi.WorldMap?
                .GetChildAtIndex(2)?.GetChildAtIndex(0)?.GetChildAtIndex(1);
        }
        catch { return null; }
    }
}

// in-world interaction indicator: golden pulsing down-arrow over the step's resolved target.
// target = ONLY the objective's explicit Indicators[] entity children, decoupled from the ground path
// (Paths[]): an arrow needs no path, a path needs no arrow, and no Indicator means no arrow (no text-pass
// inference). advance is handled by EvaluateAdvance (AdvanceEngine); this file only drives the arrow.
public partial class ExileCampaigns
{
    private const string IndicatorTexture = "ExileCampaigns_Icons";
    private bool _indicatorTexLoaded;

    // current step's resolved interaction targets, refreshed (throttled) in Tick, reused by Render. a list so
    // multiple authored Indicators[] (or one pattern matching several live entities) each get an arrow.
    private IReadOnlyList<InteractTarget> _interactTargets = System.Array.Empty<InteractTarget>();
    private DateTime _lastInteractResolve;


    // load the icon atlas once. DirectoryFullName = Plugins\Temp output dir where csproj Content lands
    private void InitIndicatorTexture()
    {
        if (_indicatorTexLoaded) return;
        try
        {
            var path = Path.Combine(DirectoryFullName, "textures", SpriteAtlas.FileName);
            if (File.Exists(path))
            {
                Graphics.InitImage(IndicatorTexture, path);
                _indicatorTexLoaded = true;
            }
            else LogError($"ExileCampaigns -> indicator texture not found: {path}");
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> indicator texture init failed: {ex.Message}"); }
    }

    private bool InteractFeatureActive => Settings.InteractIndicator.Enable.Value;

    // throttled resolve of the current step's interaction target. called from Tick
    private void UpdateInteractTarget()
    {
        if (!InteractFeatureActive) { _interactTargets = System.Array.Empty<InteractTarget>(); return; }
        if ((DateTime.Now - _lastInteractResolve).TotalSeconds < 0.15) return;
        _lastInteractResolve = DateTime.Now;

        var flat = _route.CurrentStep;
        if (flat?.Model == null) { _interactTargets = System.Array.Empty<InteractTarget>(); return; }

        try
        {
            float maxDist = Settings.InteractIndicator.MaxDistance.Value;
            // arrow source = ONLY the objective's explicit Indicators[] entity children (ALL matching
            // entities, one arrow each). no text-pass fallback: a step with no authored Indicator shows no
            // arrow. advance is unaffected (it runs through AdvanceEngine), so this is purely cosmetic.
            var indTargets = GuidanceView.IndicatorEntityTargets(flat.Model);
            _interactTargets = indTargets.Count > 0
                ? _targetResolver.ResolveIndicatorEntities(GameController, indTargets, maxDist)
                : System.Array.Empty<InteractTarget>();
        }
        catch { _interactTargets = System.Array.Empty<InteractTarget>(); }
    }

    // draw the pulsing golden down-arrow above the resolved target. called from Render after the
    // fullscreen-panel hide guard, so it inherits that suppression
    private void DrawInteractIndicators()
    {
        if (!Settings.InteractIndicator.Enable || !_indicatorTexLoaded) return;
        foreach (var target in _interactTargets)
            DrawOneIndicator(target);
    }

    private void DrawOneIndicator(InteractTarget target)
    {
        var e = target?.Entity;
        if (e == null || !e.IsValid) return;

        // don't keep pointing at an already-opened chest
        if (target!.Kind == InteractKind.ChestOpen && (e.GetComponent<Chest>()?.IsOpened ?? false)) return;

        Vector2 screen;
        try
        {
            var pos = e.Pos;                                   // world-space, at the feet
            var boundsZ = e.GetComponent<Render>()?.Bounds.Z ?? 0f;
            pos.Z -= boundsZ * 2f + Settings.InteractIndicator.HeightOffset.Value;  // lift above the head
            var raw = GameController.IngameState.Camera.WorldToScreen(pos);
            screen = new Vector2(raw.X, raw.Y);
        }
        catch { return; }
        if (screen == Vector2.Zero) return;                   // off-screen / behind camera

        // bob up/down so it reads as nudging at the object below it
        float t = (float)ImGui.GetTime();
        float bob = MathF.Sin(t * Settings.InteractIndicator.BobSpeed.Value) * Settings.InteractIndicator.BobDistance.Value;

        float size = Settings.InteractIndicator.IconSize.Value;
        float half = size / 2f;
        var col = Settings.InteractIndicator.IconColor.Value;

        var dest = new RectangleF(screen.X - half, screen.Y - half + bob, size, size);
        if (OverlapsSidePanel(dest)) return;   // hide under an open side panel
        var uv = SpriteAtlas.GetUVRectFlippedV(SpriteIcon.Arrow);   // up-arrow flipped to point down
        Graphics.DrawImage(IndicatorTexture, dest, uv, col);
    }

}

// Dev/authoring overlay: annotated layers on the large minimap to help author routes.
// All guarded on Settings.Dev.ShowDevOverlay (off by default, zero overhead in production).
//
// Layers (each toggled independently under Dev settings):
//   Room names    AreaGraph room boxes + name labels. these are what Radar's targets.json and
//                 Radar.ClusterTarget match against, so this shows the tile-pattern for a StepTargetResolver fallback.
//   Entity labels AreaTransition / Waypoint / boss entity paths at their grid positions. shows which
//                 entity path StepTargetResolver should match.
//   Target marker crosshair at the current step's resolved target grid coord (validates resolver).
public partial class ExileCampaigns
{
    private const int TileToGrid = 23;
    private const string TerrainPrefix = "Metadata/Terrain/";

    private void DrawDevOverlay()
    {
        // the editor's "Preview target" marker reuses this overlay's map projection, so allow the marker
        // through even when the dev overlay proper is off (room/entity layers stay gated on ShowDevOverlay).
        bool previewMarker = Settings.Editor.Enable && _editorPreviewTarget;
        if (!Settings.Dev.ShowDevOverlay && !previewMarker) return;

        SubMap? largeMap = null;
        try { largeMap = GameController.Game.IngameState.IngameUi.Map?.LargeMap?.AsObject<SubMap>(); }
        catch { }
        if (largeMap is not { IsVisible: true }) return;

        var playerPos = GameController.Player?.GetComponent<Positioned>();
        var playerRender = GameController.Player?.GetComponent<Render>();
        if (playerPos == null || playerRender == null) return;

        var playerGrid = new Vector2(playerPos.GridX, playerPos.GridY);
        var playerHeight = -playerRender.UnclampedHeight;
        var mapCenter = largeMap.MapCenter;
        var mapScale = largeMap.MapScale;

        if (Settings.Dev.ShowDevOverlay && Settings.Dev.ShowRoomNames)
            DrawDevRoomNames(playerGrid, playerHeight, mapCenter, mapScale);

        if (Settings.Dev.ShowDevOverlay && Settings.Dev.ShowEntityLabels)
            DrawDevEntityLabels(playerGrid, playerHeight, mapCenter, mapScale);

        // target marker is editor-preview only now (its own dev toggle was removed)
        if (previewMarker && _currentTarget.HasValue)
            DrawDevTargetMarker(_currentTarget.Value, playerGrid, playerHeight, mapCenter, mapScale);
    }

    // AreaGraph room outlines + name labels. names strip "Metadata/Terrain/" for readability.
    private void DrawDevRoomNames(Vector2 playerGrid, float playerHeight, Vector2 mapCenter, float mapScale)
    {
        try
        {
            foreach (var graph in GameController.IngameState.Data.AreaGraphs)
            {
                foreach (var room in graph.Rooms)
                {
                    var name = room.Name;
                    if (name == null) continue;

                    var minGrid = new Vector2(room.MinCoord.X * TileToGrid, room.MinCoord.Y * TileToGrid);
                    var maxGrid = new Vector2(room.MaxCoord.X * TileToGrid, room.MaxCoord.Y * TileToGrid);

                    var tl = DevMapPoint(minGrid,                              playerGrid, playerHeight, mapCenter, mapScale);
                    var tr = DevMapPoint(new Vector2(maxGrid.X, minGrid.Y),   playerGrid, playerHeight, mapCenter, mapScale);
                    var br = DevMapPoint(maxGrid,                              playerGrid, playerHeight, mapCenter, mapScale);
                    var bl = DevMapPoint(new Vector2(minGrid.X, maxGrid.Y),   playerGrid, playerHeight, mapCenter, mapScale);

                    // hide rooms tucked under an open side panel
                    if (PointInSidePanel(tl) || PointInSidePanel(tr) || PointInSidePanel(br) || PointInSidePanel(bl))
                        continue;

                    var lineColor = Color.YellowGreen;
                    Graphics.DrawLine(tl, tr, 1f, lineColor);
                    Graphics.DrawLine(tr, br, 1f, lineColor);
                    Graphics.DrawLine(br, bl, 1f, lineColor);
                    Graphics.DrawLine(bl, tl, 1f, lineColor);

                    var centerGrid = (minGrid + maxGrid) / 2f;
                    var centerScreen = DevMapPoint(centerGrid, playerGrid, playerHeight, mapCenter, mapScale);
                    var label = name.StartsWith(TerrainPrefix, StringComparison.Ordinal)
                        ? name[TerrainPrefix.Length..]
                        : name;
                    var sz = Graphics.MeasureText(label);
                    Graphics.DrawBox(
                        centerScreen - sz / 2f - new Vector2(2, 1),
                        centerScreen + sz / 2f + new Vector2(2, 1),
                        new Color(0, 0, 0, 180));
                    Graphics.DrawText(label, centerScreen - sz / 2f, Color.YellowGreen);
                }
            }
        }
        catch { /* AreaGraphs not ready */ }
    }

    // entity path labels: AreaTransitions (cyan), Waypoints (gold), boss Rare/Unique (orange-red).
    // paths shortened to last two segments; full path in quest-flag-harvest.jsonl if needed.
    private void DrawDevEntityLabels(Vector2 playerGrid, float playerHeight, Vector2 mapCenter, float mapScale)
    {
        var ents = GameController?.EntityListWrapper?.Entities;
        if (ents == null) return;

        foreach (var e in ents)
        {
            if (e == null || !e.IsValid) continue;
            var path = e.Path ?? "";
            bool isTransition = path.Contains("AreaTransition");
            bool isWaypoint   = path.Contains("Waypoint");
            var rarityStr = e.Rarity.ToString();
            bool isBoss = e.IsHostile && rarityStr is "Rare" or "Unique";
            if (!isTransition && !isWaypoint && !isBoss) continue;

            var pos = e.GetComponent<Positioned>();
            if (pos == null) continue;

            var entityGrid = new Vector2(pos.GridX, pos.GridY);
            var screen = DevMapPoint(entityGrid, playerGrid, playerHeight, mapCenter, mapScale);
            if (PointInSidePanel(screen)) continue;   // hide under an open side panel

            var label = DevShortPath(path);
            var color = isTransition ? Color.Cyan : isWaypoint ? Color.Gold : Color.OrangeRed;
            var sz = Graphics.MeasureText(label);
            Graphics.DrawBox(
                screen - new Vector2(sz.X / 2f + 2, sz.Y / 2f + 1),
                screen + new Vector2(sz.X / 2f + 2, sz.Y / 2f + 1),
                new Color(0, 0, 0, 180));
            Graphics.DrawText(label, screen - sz / 2f, color);
        }
    }

    // crosshair + label at the current step's resolved target grid coord
    private void DrawDevTargetMarker(Vector2 target, Vector2 playerGrid, float playerHeight, Vector2 mapCenter, float mapScale)
    {
        var screen = DevMapPoint(target, playerGrid, playerHeight, mapCenter, mapScale);
        if (PointInSidePanel(screen)) return;   // hide under an open side panel
        const float r = 7f;
        var c = Color.Lime;
        Graphics.DrawLine(screen + new Vector2(-r, 0), screen + new Vector2(r, 0), 2f, c);
        Graphics.DrawLine(screen + new Vector2(0, -r), screen + new Vector2(0, r), 2f, c);
        Graphics.DrawBox(screen - new Vector2(r, r), screen + new Vector2(r, r), new Color(0, 255, 0, 50));

        var label = $"step target ({(int)target.X},{(int)target.Y})";
        var sz = Graphics.MeasureText(label);
        Graphics.DrawBox(
            screen + new Vector2(-sz.X / 2f - 2, r + 1),
            screen + new Vector2(sz.X / 2f + 2, r + sz.Y + 1),
            new Color(0, 0, 0, 160));
        Graphics.DrawText(label, screen + new Vector2(-sz.X / 2f, r + 2f), c);
    }

    // project a grid pos to minimap screen coords, same iso math as DrawPathMinimap
    private Vector2 DevMapPoint(Vector2 gridPos, Vector2 playerGrid, float playerHeight, Vector2 mapCenter, float mapScale)
    {
        var hd = _heightData;
        float z = 0f;
        if (hd != null)
        {
            int gy = (int)gridPos.Y, gx = (int)gridPos.X;
            if (gy >= 0 && gy < hd.Length && gx >= 0 && gx < hd[gy].Length)
                z = hd[gy][gx];
        }
        return mapCenter + GridDeltaToMapDelta(gridPos - playerGrid, playerHeight + z, mapScale);
    }

    // last two path segments, enough to identify entity type without the full Metadata/... prefix
    private static string DevShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var parts = path.Split('/');
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : path;
    }
}
