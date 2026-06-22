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

// a Radar room-based objective: a display name joined to a step by a distinctive word, the room-name filters
// it lives in, and (optional, ordered) the per-sub-objective quest flags that flip as you complete each.
// EntityPath: when set, the objective is ENTITY-based instead of room-based -- match live entities whose
// metadata Path contains this fragment (e.g. g1_4 "Runic Seals" -> "NailStake"), so a step whose objects
// have no textual overlap with the step text still gets an indicator + path. interactable state
// (Targetable.isTargetable) is the per-sub-objective completion signal, so no quest flags are needed.
public sealed record ObjectiveDef(
    string Name,
    IReadOnlyList<string> Rooms,
    IReadOnlyList<string> ProgressFlags,
    string? EntityPath = null);

// resolves the current step to one grid-space target coord for the path to aim at.
// hybrid: live entities first, then authored tile-name fallback via Radar.ClusterTarget. order:
//   1. kill/arena   -> matching hostile (boss) entity
//   2. waypoint     -> nearest Waypoint object
//   3. navigation   -> forward AreaTransition; one unambiguous or authored-disambiguated
//   4. fallback     -> authored tile-name -> ClusterTarget, nearest the player
// null when nothing maps (entities not loaded, no Radar). caller retries.
public sealed class StepTargetResolver
{
    // optional authored map: lowercased step AreaId -> Radar tile-name pattern, for ambiguous zones. empty by default.
    private readonly IReadOnlyDictionary<string, string> _authoredTargets;

    // lowercased source area-id -> navigation transitions out of it (destination match + Radar tile). pre-load
    // path fallback for "Enter/Exit X" when the live AreaTransition entity isn't in render range yet.
    private readonly IReadOnlyDictionary<string, IReadOnlyList<(string Match, string Tile)>> _transitions;

    // lowercased area-id -> Radar room-based objective markers (name + room-name filters + optional ordered
    // progress flags). for an on-the-ground objective whose entity isn't a standard interactable (e.g. g1_5
    // obelisks load as IngameIcon). resolved against AreaGraphs rooms, since the bridge drops the room filter.
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ObjectiveDef>> _objectives;

    public StepTargetResolver(
        IReadOnlyDictionary<string, string>? authoredTargets = null,
        IReadOnlyDictionary<string, IReadOnlyList<(string Match, string Tile)>>? transitions = null,
        IReadOnlyDictionary<string, IReadOnlyList<ObjectiveDef>>? objectives = null)
    {
        _authoredTargets = authoredTargets ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _transitions = transitions ?? new Dictionary<string, IReadOnlyList<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        _objectives = objectives ?? new Dictionary<string, IReadOnlyList<ObjectiveDef>>(StringComparer.OrdinalIgnoreCase);
    }

    // tile pattern -> Radar's ExpectedCount for the current area (only the >1 ones). set on area change from
    // targets.json. without it a multi-instance tile (e.g. 2 staircases) collapses to one void centroid and the
    // route can't path there. resolve to the same cluster count Radar uses, then take the nearest.
    public IReadOnlyDictionary<string, int>? RadarTileCounts { get; set; }

    private int ClusterCount(string? pattern) =>
        !string.IsNullOrEmpty(pattern) && RadarTileCounts != null
        && RadarTileCounts.TryGetValue(pattern!, out var c) && c > 1 ? c : 1;

    // max player-distance (grid units) for an interaction entity to be path-eligible. generous: chests/NPCs
    // only load within render range anyway, so the real gate is whether the entity exists.
    private const float InteractPathMaxDistance = 1000f;

    public Vector2? Resolve(GameController gc, ParsedStep step, Func<string, int, Vector2[]>? clusterTarget,
        string? effectiveAreaId = null)
    {
        if (gc == null || step == null) return null;
        var entities = gc.EntityListWrapper?.Entities;
        var playerGrid = PlayerGrid(gc);

        // 1. kill/arena: aim at the matching hostile (the boss the step names).
        var killNames = step.Fragments.OfType<KillFragment>().Select(f => f.Target)
            .Concat(step.Fragments.OfType<ArenaFragment>().Select(f => f.Target))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        if (killNames.Count > 0 && entities != null)
        {
            var boss = entities
                .Where(e => e != null && e.IsValid && e.IsHostile)
                .Where(IsKillableCreature)   // exclude lifeless arena props that match the boss name by Path
                .Where(e => killNames.Any(n => NameMatches(e, n)))
                .Where(HasGrid)
                .OrderBy(e => e.DistancePlayer)
                .FirstOrDefault();
            if (boss != null) return GridOf(boss);
        }

        // 2. waypoint steps: go to nearest waypoint object.
        var wantsWaypoint = step.Fragments.Any(f =>
            f.Kind is FragmentKind.Waypoint or FragmentKind.WaypointUse or FragmentKind.WaypointGet);
        if (wantsWaypoint && entities != null)
        {
            var wp = NearestByPath(entities, "Waypoint", playerGrid);
            if (wp != null) return wp;
        }

        // 3. interaction target (chest/NPC/quest object) the step names, same entity the indicator points at.
        //    path straight to it. mirrors ResolveEntity's chest fallback.
        var interact = ResolveEntity(gc, step, InteractPathMaxDistance, effectiveAreaId);
        if (interact != null && HasGrid(interact.Entity))
            return GridOf(interact.Entity);

        // 3b. "Enter/Exit X" navigation: aim at the live AreaTransition that leads to X (matched by its
        //     destination render name), or, before that entity is in render range, the authored transition
        //     tile for X via Radar -- so the path shows from zone entry like Radar's own. never the zone boss.
        var trans = ResolveTransition(gc, step, playerGrid, clusterTarget, effectiveAreaId);
        if (trans != null) return trans;

        // 3c. entity not loaded yet, but the step names the room it sits in (e.g. an "Abandoned Stash
        //     (inside the Mysterious Campsite)" chest, dark until you get close). aim at that AreaGraph
        //     room's center so the path shows from zone entry; step 3 snaps to the entity once it spawns.
        var room = ResolveRoom(gc, step, playerGrid);
        if (room != null) return room;

        // 3d. Radar room-based objective: an on-ground objective whose entity isn't a standard interactable and
        //     isn't loaded yet (e.g. the g1_5 obelisks before you're close). aim at the nearest AreaGraphs room
        //     Radar tags for it, so the path shows from zone entry; pass 3 snaps to the IngameIcon once loaded.
        var obj = ResolveObjective(gc, step, playerGrid, effectiveAreaId);
        if (obj != null) return obj;

        // 4. authored target: the zone's single boss arena. only borrow it for the step that's actually
        //    about killing that boss; transitions resolve via live AreaTransition entities (step 3b), never
        //    here, so an "Enter <next zone>" step can't path back to the boss.
        if (!WantsAreaLandmark(step)) return null;
        return ResolveAuthored(step, playerGrid, clusterTarget, effectiveAreaId);
    }

    // is this step's objective the zone's boss arena (all the authored area-target map holds)? kill/arena
    // steps only -- a zone whose authored tile is the boss (g1_2 -> Beira) must never path an "Enter <next
    // zone>" step at the boss.
    private static bool WantsAreaLandmark(ParsedStep step)
    {
        // an explicit per-step pathTarget (override layer) is always honored -- deliberate authoring, including
        // for OPTIONAL far bosses whose arena tile we want a path to (e.g. Balbala / Prison of the Disgraced
        // behind the seal door). without one, an optional step never borrows the area's boss tile.
        if (step.Meta?.PathTarget is { Length: > 0 }) return true;
        if (step.IsOptional) return false;
        return step.Fragments.OfType<KillFragment>().Any() || step.Fragments.OfType<ArenaFragment>().Any();
    }

    // the destination zone a navigation step heads to, or null if it isn't one. "Enter The Grelwood" ->
    // "Grelwood"; "Enter Grim Tangle -> take WP" -> "Grim Tangle"; "Enter Clearfell Encampment behind boss"
    // -> "Clearfell Encampment".
    private static string? TransitionDestination(string text)
    {
        var t = text.TrimStart();
        string? verb = null;
        foreach (var v in new[] { "enter ", "exit to ", "exit " })
            if (t.StartsWith(v, StringComparison.OrdinalIgnoreCase)) { verb = v; break; }
        if (verb == null) return null;
        var rest = TrimAtMarkers(t.Substring(verb.Length)).Trim();
        if (rest.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(4);
        return rest.Length >= 3 ? rest : null;
    }

    // path target for an "Enter/Exit X" step: the live AreaTransition leading to X (matched on render name)
    // when in range. before it loads, the from-entry tile path comes from the EnterArea objective's Tile Path
    // child via the direct PathTilePatterns -> ResolveTiles fallback in Pathing (not from StepMeta anymore).
    private Vector2? ResolveTransition(GameController gc, ParsedStep step, Vector2? playerGrid,
        Func<string, int, Vector2[]>? clusterTarget, string? effectiveAreaId)
    {
        var dest = TransitionDestination(step.PlainText());
        if (string.IsNullOrEmpty(dest)) return null;

        var entities = gc.EntityListWrapper?.Entities;
        // rank loose matches by quality first, then distance: a back-exit whose name is a substring of the
        // destination (e.g. "Deshar" inside "Spires of Deshar") is a WEAKER match than the forward exit named
        // for the destination, and must never win just by being nearer.
        var live = entities?
            .Where(e => e != null && e.IsValid && e.Type == EntityType.AreaTransition && HasGrid(e))
            .Where(e => LooseEqual(e.RenderName, dest))
            .OrderByDescending(e => MatchScore(e.RenderName, dest))
            .ThenBy(e => e.DistancePlayer)
            .FirstOrDefault();
        if (live != null) return GridOf(live);

        // not loaded yet: the from-entry tile path is handled by the direct PathTilePatterns -> ResolveTiles
        // fallback in Pathing (the EnterArea tile is a plain Tile Path child now), so no StepMeta tile read here.
        if (clusterTarget == null) return null;

        // fallback: area-keyed JSON map (retired in Task 11).
        var key = !string.IsNullOrEmpty(effectiveAreaId) ? effectiveAreaId : step.AreaId;
        if (string.IsNullOrEmpty(key) || !_transitions.TryGetValue(key, out var exits)) return null;
        foreach (var (m, tile) in exits)
        {
            if (!LooseEqual(m, dest)) continue;
            try
            {
                var hit = NearestCluster(clusterTarget(tile, ClusterCount(tile)), playerGrid);
                if (hit != null) return hit;
            }
            catch { /* tile not in this instance, try next */ }
        }
        return null;
    }

    // case-insensitive either-direction containment, for matching a route destination ("Grelwood") against a
    // longer label ("The Grelwood" / "Mud Burrow (normally skip this)").
    private static bool LooseEqual(string? a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    // higher = better destination-name match. exact > renderName contains dest > dest contains renderName (the
    // weak case that loose-matches a back-exit like "Deshar" against "Spires of Deshar").
    private static int MatchScore(string? renderName, string dest)
    {
        if (string.IsNullOrEmpty(renderName)) return 0;
        if (string.Equals(renderName, dest, StringComparison.OrdinalIgnoreCase)) return 3;
        if (renderName.Contains(dest, StringComparison.OrdinalIgnoreCase)) return 2;
        if (dest.Contains(renderName, StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    // AreaGraph room coords are in tiles; Radar multiplies by this to reach grid units (Radar TileToGridConversion).
    private const int TileToGrid = 23;

    // location phrases follow these markers in a step line ("... inside the Mysterious Campsite").
    private static readonly string[] LocationMarkers =
        { " inside the ", " inside ", " in the ", " in ", " near the ", " near ", " next to the ", " next to ", " behind the ", " behind ", " at the " };

    // aim at the center of the AreaGraph room the step names, for when the real objective entity (a chest)
    // hasn't loaded. reads the same room set Radar does (IngameState.Data.AreaGraphs); the Radar.ClusterTarget
    // bridge can't be used here because it drops the room filter. nearest matching room to the player wins.
    private static Vector2? ResolveRoom(GameController gc, ParsedStep step, Vector2? playerGrid)
    {
        var hints = RoomHints(step.PlainText());
        if (hints.Count == 0) return null;
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
                    if (string.IsNullOrEmpty(name)) continue;
                    var despaced = name.Replace(" ", "");
                    if (!hints.Any(h => despaced.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;
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

    // significant location words after a positional marker ("inside the Mysterious Campsite" -> Mysterious,
    // Campsite). len>=5 drops connectives and keeps room-distinctive nouns for the AreaGraph name match.
    private static List<string> RoomHints(string text)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return res;
        // punctuation glues to words ("(inside the Campsite)") and hides the space-delimited markers; flatten
        // brackets to spaces first so " inside the " still matches.
        text = " " + text.Replace('(', ' ').Replace(')', ' ').Replace('[', ' ').Replace(']', ' ') + " ";
        foreach (var m in LocationMarkers)
        {
            int idx = text.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var after = TrimAtMarkers(text.Substring(idx + m.Length));
            foreach (var w in after.Split(new[] { ' ', '(', ')', '{', '}', ',', '.', '-', '/' }, StringSplitOptions.RemoveEmptyEntries))
                if (w.Length >= 5 && !res.Contains(w, StringComparer.OrdinalIgnoreCase))
                    res.Add(w);
        }
        return res;
    }

    // generic Radar DisplayName words that must not, on their own, join an objective to a step (they recur
    // across the game and would mis-join). a name needs a *distinctive* word (e.g. "Rust") to match.
    private static readonly HashSet<string> ObjectiveStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "boss", "rare", "chest", "ring", "idol", "rune", "gold", "shrine", "random", "magic", "currency",
        "strongbox", "monster", "reward", "piece", "vault", "essence", "seal", "buff", "skill", "support",
        "spirit", "passive", "flask", "flasks",
    };

    // a Radar room-based objective (e.g. g1_5 obelisks, tagged "Rust" over the "*Encounter*" rooms) for an
    // objective whose entity isn't loaded / isn't a standard interactable. join the objective name to the step
    // by a distinctive word, then aim at the nearest AreaGraphs room Radar tags. mirrors Radar's room target;
    // the Radar.ClusterTarget bridge can't do it (it drops the room filter).
    private Vector2? ResolveObjective(GameController gc, ParsedStep step, Vector2? playerGrid, string? effectiveAreaId)
    {
        // meta-first: step carries an embedded objective directly from the override layer.
        if (step.Meta?.Objective != null)
        {
            var mo = step.Meta.Objective;
            var mdef = new ObjectiveDef(mo.Label ?? "", mo.Rooms ?? new List<string>(), mo.ProgressFlags ?? new List<string>(), mo.EntityPath);
            if (mdef.Rooms.Count > 0)
            {
                var hit = NearestRoomCenter(gc, playerGrid,
                    rname => mdef.Rooms.Any(f => rname.Contains(f, StringComparison.OrdinalIgnoreCase)));
                if (hit != null) return hit;
            }
            return null;
        }

        // fallback: area-keyed JSON map (retired in Task 11).
        var key = !string.IsNullOrEmpty(effectiveAreaId) ? effectiveAreaId : step.AreaId;
        if (string.IsNullOrEmpty(key) || !_objectives.TryGetValue(key, out var objs)) return null;
        var plain = step.PlainText();
        if (string.IsNullOrWhiteSpace(plain)) return null;
        foreach (var o in objs)
        {
            if (!ObjectiveNameInStep(o.Name, plain)) continue;
            var hit = NearestRoomCenter(gc, playerGrid,
                rname => o.Rooms.Any(f => rname.Contains(f, StringComparison.OrdinalIgnoreCase)));
            if (hit != null) return hit;
        }
        return null;
    }

    private static bool ObjectiveNameInStep(string name, string stepPlain)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var w in name.Split(new[] { ' ', '(', ')', '/', '-', ',', '+', '.' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.Length < 4 || ObjectiveStopWords.Contains(w)) continue;
            if (stepPlain.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ALL matching Radar objective room centers for the step (e.g. the 3 Rust-obelisk Encounter rooms), so the
    // pathing can draw a line to each while the step waits on all of them. empty if the step names no objective.
    public IReadOnlyList<Vector2> ResolveObjectiveTargets(GameController gc, ParsedStep step, string? effectiveAreaId)
    {
        var res = new List<Vector2>();
        if (gc == null || step == null) return res;

        // meta-first: step carries rooms directly from the override layer.
        if (step.Meta?.Objective != null)
        {
            var mo = step.Meta.Objective;
            var rooms = mo.Rooms ?? new List<string>();
            if (rooms.Count > 0)
                res.AddRange(AllRoomCenters(gc, rname => rooms.Any(f => rname.Contains(f, StringComparison.OrdinalIgnoreCase))));
            return res;
        }

        // fallback: area-keyed JSON map (retired in Task 11).
        var key = !string.IsNullOrEmpty(effectiveAreaId) ? effectiveAreaId : step.AreaId;
        if (string.IsNullOrEmpty(key) || !_objectives.TryGetValue(key, out var objs)) return res;
        var plain = step.PlainText();
        if (string.IsNullOrWhiteSpace(plain)) return res;
        foreach (var o in objs)
        {
            if (!ObjectiveNameInStep(o.Name, plain)) continue;
            res.AddRange(AllRoomCenters(gc, rname => o.Rooms.Any(f => rname.Contains(f, StringComparison.OrdinalIgnoreCase))));
            if (res.Count > 0) break;   // first matching objective wins
        }
        return res;
    }

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
    public IReadOnlyList<string> ResolveObjectiveProgressFlags(ParsedStep step, string? effectiveAreaId)
    {
        if (step == null) return Array.Empty<string>();

        // meta-first: progress flags come directly from the override layer.
        if (step.Meta?.Objective != null)
        {
            var flags = step.Meta.Objective.ProgressFlags;
            return flags != null && flags.Count > 0 ? flags : Array.Empty<string>();
        }

        // fallback: area-keyed JSON map (retired in Task 11).
        var key = !string.IsNullOrEmpty(effectiveAreaId) ? effectiveAreaId : step.AreaId;
        if (string.IsNullOrEmpty(key) || !_objectives.TryGetValue(key, out var objs)) return Array.Empty<string>();
        var plain = step.PlainText();
        if (string.IsNullOrWhiteSpace(plain)) return Array.Empty<string>();
        foreach (var o in objs)
            if (ObjectiveNameInStep(o.Name, plain) && o.ProgressFlags.Count > 0)
                return o.ProgressFlags;
        return Array.Empty<string>();
    }

    // the metadata-path fragment for an authored ENTITY-based objective the step names (e.g. g1_4 "Runic
    // Seals" -> "NailStake"), or null. drives the entity-objective indicator + multi-path.
    public string? ObjectiveEntityPath(ParsedStep step, string? effectiveAreaId)
    {
        if (step == null) return null;

        // meta-first: entity path comes directly from the override layer.
        if (step.Meta?.Objective?.EntityPath is string ep && !string.IsNullOrEmpty(ep))
            return ep;

        // fallback: area-keyed JSON map (retired in Task 11).
        var key = !string.IsNullOrEmpty(effectiveAreaId) ? effectiveAreaId : step.AreaId;
        if (string.IsNullOrEmpty(key) || !_objectives.TryGetValue(key, out var objs)) return null;
        var plain = step.PlainText();
        if (string.IsNullOrWhiteSpace(plain)) return null;
        foreach (var o in objs)
            if (!string.IsNullOrEmpty(o.EntityPath) && ObjectiveNameInStep(o.Name, plain))
                return o.EntityPath;
        return null;
    }

    // grid positions (snapped to walkable) of the live entities matching an entity-objective path fragment.
    // liveOnly: only those still interactable (Targetable.isTargetable) -- i.e. not yet activated. the
    // pathing draws one route per position and drops it when its entity drops out of the liveOnly set.
    public IReadOnlyList<Vector2> ObjectiveEntityPositions(GameController gc, string entityPath, bool liveOnly)
        => ObjectiveEntityPositions(gc, string.IsNullOrEmpty(entityPath) ? System.Array.Empty<string>() : new[] { entityPath }, liveOnly);

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
            if (liveOnly && !IsInteractable(e)) continue;
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

    // metadata leaf for the summon-ally portal icon shared across zones (.../Objects/SummonAlly).
    private const string SummonAllyPath = "SummonAlly";

    // a "Summon X" step (e.g. "Summon Una", "Find/summon/talk Una"). its real interactable is the SummonAlly
    // portal, not the named NPC.
    private static bool IsSummonStep(ParsedStep step)
    {
        var t = step?.PlainText();
        return !string.IsNullOrEmpty(t)
            && System.Text.RegularExpressions.Regex.IsMatch(t, @"\bsummon\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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

    // entity types a step can ask the player to interact with.
    private static readonly HashSet<EntityType> InteractTypes = new()
    {
        // IngameIcon: some on-ground quest objectives surface as a world icon, not a QuestObject (e.g. the
        // g1_5 "Obelisk of Rust"). treat it as a proximity interactable so "find <X>" can target it.
        // Terrain: interactable map objects (doors/levers/gates) are EntityType.Terrain, not QuestObject --
        // e.g. the g1_7 "Memorial Gate" (Metadata/Terrain/.../GraveyardArenaDoor). gated to ones with a
        // Targetable component below, so scenery and door blockers (no Targetable) don't flood the list.
        EntityType.Npc, EntityType.Chest, EntityType.Monster, EntityType.QuestObject, EntityType.Shrine,
        EntityType.IngameIcon, EntityType.Terrain,
    };

    // verbs that introduce a free-text interaction target. grammar only structures hostiles + waypoints;
    // "Talk to Una" / "Loot Large Chests" / "Open Memorial Gate" live in plain text.
    private static readonly string[] InteractVerbs =
        { "talk", "speak", "loot", "open", "take", "find", "activate", "summon", "use", "click", "reach" };

    // resolve the step to the live entity to interact with (or null). used by the indicator + interaction
    // auto-advance. independent of the grid Resolve() above.
    public InteractTarget? ResolveEntity(GameController gc, ParsedStep step, float maxDistance,
        string? effectiveAreaId = null, bool includeEntityObjective = true)
    {
        if (gc == null || step == null) return null;
        var entities = gc.EntityListWrapper?.Entities;
        if (entities == null) return null;

        // 0. authored entity-objective (e.g. g1_4 "Runic Seals" -> TreeOfSoulsNailStake): a set of on-ground
        //    interactables whose metadata path has no textual overlap with the step. target the nearest one
        //    still interactable, so the path (which reuses this) advances to the next as each is used -- they
        //    flip Targetable.isTargetable=false on activation. this pass is Paths-sourced, so the INDICATOR
        //    opts out (includeEntityObjective:false) and resolves its arrow from Indicators[] instead.
        var entObjPath = includeEntityObjective ? ObjectiveEntityPath(step, effectiveAreaId) : null;
        if (!string.IsNullOrEmpty(entObjPath))
        {
            var seal = entities
                .Where(e => e != null && e.IsValid && HasGrid(e))
                .Where(e => e.DistancePlayer <= maxDistance)
                .Where(e => e.Path?.Contains(entObjPath, StringComparison.OrdinalIgnoreCase) ?? false)
                .Where(IsInteractable)
                .OrderBy(e => e.DistancePlayer)
                .FirstOrDefault();
            if (seal != null)
                return new InteractTarget { Entity = seal, Kind = InteractKind.Proximity, MatchedName = entObjPath! };
        }

        // "Summon X" steps interact with a SummonAlly portal icon (an IngameIcon, e.g.
        // .../Act1/1_6/Objects/SummonAlly), NOT the summoned NPC -- the named object (Una) is the ally, the
        // interactable is the portal. target the nearest interactable one; fall through to normal resolution
        // (the named NPC) if none is loaded.
        if (IsSummonStep(step))
        {
            var ally = entities
                .Where(e => e != null && e.IsValid && HasGrid(e))
                .Where(e => e.DistancePlayer <= maxDistance)
                .Where(e => e.Path?.Contains(SummonAllyPath, StringComparison.OrdinalIgnoreCase) ?? false)
                .Where(IsInteractable)
                .OrderBy(e => e.DistancePlayer)
                .FirstOrDefault();
            if (ally != null)
                return new InteractTarget { Entity = ally, Kind = InteractKind.Proximity, MatchedName = "Summon Ally" };
        }

        var killNames = step.Fragments.OfType<KillFragment>().Select(f => f.Target)
            .Concat(step.Fragments.OfType<ArenaFragment>().Select(f => f.Target))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nearby = entities
            .Where(e => e != null && e.IsValid && InteractTypes.Contains(e.Type))
            // interactable terrain only: doors/gates/levers/runes carry Targetable AND read isTargetable=true
            // while still usable. excludes scenery + blockers (no Targetable) AND already-activated objects
            // (e.g. a clicked Rune of Aldur flips isTargetable=false) so the indicator never sticks on a done one.
            .Where(e => e.Type != EntityType.Terrain || (e.GetComponent<Targetable>()?.isTargetable ?? false))
            .Where(e => e.DistancePlayer <= maxDistance)
            .Where(HasGrid)
            .OrderBy(e => e.DistancePlayer)
            .ToList();

        // 1. kill/arena target: the named boss only. STRICT match against a hostile, so a trash mob that
        //    merely shares a word ("Rotten"/"Pack") can't satisfy a {kill|Beira of the Rotten Pack} step
        //    and trip the death-advance early. advance fires when this hostile dies.
        foreach (var e in nearby)
        {
            if (!e.IsHostile) continue;
            if (!IsKillableCreature(e)) continue;   // skip lifeless arena scaffolding (Metadata/Monsters/.../ArenaObjects/*)
            foreach (var cand in killNames)
                if (NameMatches(e, cand))
                    return new InteractTarget { Entity = e, Kind = InteractKind.Kill, MatchedName = cand };
        }

        // 2. interaction target (talk/loot/open): a non-hostile entity, loose name match. the verb fixes
        //    the expected kind+type, so "Talk to Farrow" only resolves to the Farrow NPC (Dialog) and can
        //    never latch a same-named ground object and proximity-advance before you talk. hostiles are
        //    kill-only (handled above).
        var textCandidates = FreeTextCandidates(step.PlainText())
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .ToList();

        // editor can force the interaction kind per step (meta.interactKind) to correct a wrong inference,
        // e.g. a talk step the verb grammar read as proximity. when set, it overrides the verb-derived kind.
        var forcedKind = ParseInteractKind(step.Meta?.InteractKind);

        // PoE keeps inactive alternate-state copies of a story NPC in the entity list at other spots (Una has
        // 5 in town: UnaAfterIronCount, UnaHoodedOneInjured, Una, ...). they all match the name loosely AND
        // all show on the minimap, so distance (or IsMinMapLabelVisible) picks a blank-spot duplicate. the one
        // the player should talk to carries the overhead quest icon; failing that, the live interactable copy
        // is the targetable one (inactive duplicates are non-targetable). tiered: quest icon, then targetable,
        // then any.
        InteractTarget? MatchInteract(int tier)
        {
            foreach (var e in nearby)
            {
                // some quest NPCs (Una mid-summon) report IsHostile=true; the type gate below keeps real
                // monsters out, so only skip hostile *monsters* here, not hostile-flagged NPCs.
                if (e.IsHostile && e.Type == EntityType.Monster) continue;
                foreach (var (cand, rawKind) in textCandidates)
                {
                    var kind = forcedKind ?? rawKind;
                    if (!EntityAllowsKind(e, kind) || !NameMatchesLoose(e, cand)) continue;
                    if (kind == InteractKind.Dialog && !NpcTierOk(e, tier)) continue;
                    return new InteractTarget { Entity = e, Kind = kind, MatchedName = cand };
                }
            }
            return null;
        }
        var hit = MatchInteract(0) ?? MatchInteract(1) ?? MatchInteract(2);
        if (hit != null) return hit;

        // 2b. named interactable, no verb: the step line is just the object's own name (e.g. "Abandoned
        //     Stash (inside the Mysterious Campsite)" with no Loot/Open word). match a nearby chest or
        //     quest object whose RenderName appears in the step text, so it still gets an indicator + path.
        var plain = step.PlainText();
        foreach (var e in nearby)
        {
            if (e.IsHostile && e.Type == EntityType.Monster) continue;
            if (e.Type is not (EntityType.Chest or EntityType.QuestObject)) continue;
            var rn = e.RenderName;
            if (string.IsNullOrWhiteSpace(rn) || rn.Length < 4) continue;
            if (!plain.Contains(rn, StringComparison.OrdinalIgnoreCase)) continue;
            var kind = e.Type == EntityType.Chest ? InteractKind.ChestOpen : InteractKind.Proximity;
            return new InteractTarget { Entity = e, Kind = kind, MatchedName = rn };
        }

        // no nearest-unopened-chest fallback: it pointed at whatever container happened to be closest, which
        // is essentially random (e.g. the "Ancient Ruins (sarcophagus)" step grabbing a nearby junk chest), so
        // it misleads more than it helps. a step that wants a specific container must name it (matched in 2b).

        return null;
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

    // does this NPC satisfy the preference tier among same-named duplicates? PoE keeps inactive future/alt
    // -state copies of a story NPC in the entity list; they share the name and minimap label, so we lean on
    // the overhead quest icon (the in-game indicator -- only the current quest NPC has it) and, failing that,
    // Targetable (inactive duplicates are non-targetable). tier 0 = has quest icon, 1 = targetable, 2 = any.
    private static bool NpcTierOk(Entity e, int tier)
    {
        if (tier >= 2) return true;
        if (tier == 0) return e.GetComponent<NPC>()?.HasIconOverhead ?? false;
        return e.GetComponent<Targetable>()?.isTargetable ?? true;   // tier 1: the live, interactable variant
    }

    // an entity may satisfy a verb-derived kind only if its type fits: dialog=NPC, chest=Chest,
    // proximity=a world interactable (gate/seal/shrine). keeps a talk step off ground objects.
    // dialog also accepts a non-Npc entity carrying an NPC component: many PoE2 "NPCs" aren't
    // EntityType.Npc (the g1_1 Wounded Man is Metadata/Terrain/.../TutorialNPCZombie -> Terrain;
    // town NPCs vary too), so gating purely on the type hides their talk indicator + path. the
    // name match still gates which NPC, and dialog advance is by NpcDialog name, so this only
    // affects where the arrow points.
    private static bool EntityAllowsKind(Entity e, InteractKind k) => k switch
    {
        InteractKind.Dialog => e.Type == EntityType.Npc || e.GetComponent<NPC>() != null,
        InteractKind.ChestOpen => e.Type == EntityType.Chest,
        InteractKind.Proximity => e.Type is EntityType.QuestObject or EntityType.Shrine or EntityType.IngameIcon or EntityType.Terrain,
        InteractKind.Kill => e.Type == EntityType.Monster,
        _ => false,
    };

    // map a meta.interactKind string (dialog|chest|proximity|kill) to the enum. null/blank/unknown -> null.
    private static InteractKind? ParseInteractKind(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "dialog" or "talk" or "speak" => InteractKind.Dialog,
        "chest" or "loot" or "take" => InteractKind.ChestOpen,
        "proximity" or "interact" or "use" => InteractKind.Proximity,
        "kill" => InteractKind.Kill,
        _ => null,
    };

    // pull interaction-target noun phrases out of a step's plain text. split into clauses, find the
    // right-most interaction verb in each (handles "Find/summon/talk Una"), take the trailing phrase up to
    // the first positional marker.
    private static IEnumerable<(string Name, InteractKind Kind)> FreeTextCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var rawClause in text.Split(',', ';', '.', '\n'))
        {
            var words = rawClause.Replace("/", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int lastVerb = -1;
            for (int i = 0; i < words.Length; i++)
                if (InteractVerbs.Contains(words[i].ToLowerInvariant().TrimEnd('s')))
                    lastVerb = i;
            if (lastVerb < 0 || lastVerb + 1 >= words.Length) continue;

            var rest = string.Join(' ', words.Skip(lastVerb + 1));
            // "Talk to X": drop the connecting "to".
            if (rest.StartsWith("to ", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(3);
            rest = TrimAtMarkers(rest).Trim();
            if (rest.Length >= 3) yield return (rest, VerbKind(words[lastVerb]));
        }
    }

    // the interaction the verb implies: talk -> dialog, loot -> chest, the rest -> walk-up proximity.
    private static InteractKind VerbKind(string verb) => verb.ToLowerInvariant().TrimEnd('s') switch
    {
        "talk" or "speak" => InteractKind.Dialog,
        "loot" or "take" => InteractKind.ChestOpen,
        _ => InteractKind.Proximity,
    };

    // NPC names a "talk to X" step wants a conversation with. drives dialog auto-advance by name, so it
    // works even when X has no resolved world entity (town/field NPCs aren't always a near EntityType.Npc).
    public IReadOnlyList<string> DialogTargetNames(ParsedStep step)
    {
        if (step == null) return Array.Empty<string>();
        return FreeTextCandidates(step.PlainText())
            .Where(c => c.Kind == InteractKind.Dialog && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // does this step describe ANY auto-advance trigger (kill/arena, waypoint, talk/loot/find/interact verb,
    // or an "Enter/Exit X" transition)? purely text/structural, no live entities. used to detect a "dead"
    // reminder step (e.g. "TP back to start of zone") the tracker must not park on. conservative: when in
    // doubt it returns true, so a real objective is never mistaken for a skippable reminder.
    public bool DescribesAdvanceTrigger(ParsedStep step)
    {
        if (step == null) return false;
        if (step.Fragments.Any(f => f.Kind is FragmentKind.Kill or FragmentKind.Arena
            or FragmentKind.Waypoint or FragmentKind.WaypointUse or FragmentKind.WaypointGet)) return true;
        var text = step.PlainText();
        if (FreeTextCandidates(text).Any()) return true;       // talk/loot/find/open/use/...
        if (TransitionDestination(text) != null) return true;  // Enter/Exit X -> area-change advance
        return false;
    }

    private static readonly string[] PositionMarkers =
        { " next to", " near ", " behind", " inside", " at ", " in the", " in ", "->", "(", "{", " each" };

    private static string TrimAtMarkers(string s)
    {
        int cut = s.Length;
        foreach (var m in PositionMarkers)
        {
            int idx = s.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < cut) cut = idx;
        }
        return s.Substring(0, cut);
    }

    // looser than NameMatches: either direction on RenderName, spaced-out form on Path, or any significant
    // candidate word appearing in RenderName (handles plurals/extra qualifiers).
    private static bool NameMatchesLoose(Entity e, string cand)
    {
        var n = cand.Trim();
        if (n.Length < 3) return false;
        var rn = e.RenderName ?? "";
        var path = e.Path ?? "";
        if (rn.Length > 0 && (rn.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(rn, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (path.Length > 0 && path.Contains(n.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            return true;
        if (rn.Length > 0)
            foreach (var w in n.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (w.Length >= 4 && rn.Contains(w, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
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

    private Vector2? ResolveAuthored(ParsedStep step, Vector2? playerGrid, Func<string, int, Vector2[]>? clusterTarget,
        string? effectiveAreaId)
    {
        if (clusterTarget == null) return null;
        // sub-steps (kill/loot/enter-text) carry no AreaId of their own; use the effective area id from the
        // route (most recent zone header) so the boss/exit fallback still keys into the authored map.
        var key = !string.IsNullOrEmpty(effectiveAreaId) ? effectiveAreaId : step.AreaId;

        // meta-first: step carries the tile pattern directly from the override layer.
        string? pattern = step.Meta?.PathTarget;
        if (string.IsNullOrEmpty(pattern))
        {
            // fallback: area-keyed JSON map (retired in Task 11).
            if (string.IsNullOrEmpty(key) || !_authoredTargets.TryGetValue(key, out var p)) return null;
            pattern = p;
        }

        try
        {
            return NearestCluster(clusterTarget(pattern!, ClusterCount(pattern)), playerGrid);
        }
        catch { return null; }
    }

    private static Vector2? NearestCluster(Vector2[]? coords, Vector2? playerGrid)
    {
        if (coords == null || coords.Length == 0) return null;
        if (playerGrid is not { } p) return coords[0];
        return coords.OrderBy(c => Vector2.DistanceSquared(c, p)).First();
    }

    private static Vector2? NearestByPath(IEnumerable<ExileCore.PoEMemory.MemoryObjects.Entity> entities, string pathFragment, Vector2? playerGrid)
    {
        var match = entities
            .Where(e => e != null && e.IsValid && (e.Path?.Contains(pathFragment) ?? false))
            .Where(HasGrid)
            .OrderBy(e => e.DistancePlayer)
            .FirstOrDefault();
        return match != null ? GridOf(match) : (Vector2?)null;
    }

    private static bool NameMatches(ExileCore.PoEMemory.MemoryObjects.Entity e, string name)
    {
        var n = name.Trim();
        if (n.Length == 0) return false;
        return (e.RenderName?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false)
            || (e.Path?.Contains(n.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // a kill target must be an actual creature (has Life). boss arenas spawn lifeless prop entities under
    // Metadata/Monsters/<Boss>/ArenaObjects/* (ArenaCentre, BounceTarget, BossRoomMinimapIcon) that match the
    // boss name by Path and report IsAlive=false -- which would satisfy a kill step's "!IsAlive" advance the
    // instant you walk near them, before the boss even spawns. require Life to exclude the scaffolding.
    private static bool IsKillableCreature(ExileCore.PoEMemory.MemoryObjects.Entity e)
        => e.GetComponent<Life>() != null;

    // the LivingOnly gate for guidance Entity targets: entity is actually alive (Life present, CurrentHP > 0).
    // Entity.IsAlive is null-Life-safe (no Life -> false), so a corpse or a lifeless prop sharing the name fails.
    private static bool IsLiving(ExileCore.PoEMemory.MemoryObjects.Entity e)
        => e.IsAlive;

    private static bool HasGrid(ExileCore.PoEMemory.MemoryObjects.Entity e)
        => e.GetComponent<Positioned>() != null;

    // the path to identity-match an entity on. a ground item is a WorldItem shell whose own Path is generic;
    // its base-type identity (what the editor picker captured) lives on the held item entity. position still
    // comes from the shell via GridOf, so the arrow/path/icon lands on the drop.
    private static string? MatchPath(ExileCore.PoEMemory.MemoryObjects.Entity e)
        => e.Type == EntityType.WorldItem
            ? (e.GetComponent<WorldItem>()?.ItemEntity?.Path ?? e.Path)
            : e.Path;

    private static Vector2 GridOf(ExileCore.PoEMemory.MemoryObjects.Entity e)
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
