using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ExileCampaigns.Guide;

// the only seam between the pure advance logic and live game memory. the WorldState struct implements this
// against ExileCore; tests fake it. methods take matchers/patterns and return instantaneous truth or a
// per-current-step progress count (the runtime maintains the counters and resets them on step change).
public interface IWorldState
{
    bool QuestFlagSatisfied(Pattern flag);
    bool InAreaSatisfied(Pattern area);
    bool WaypointPulsed();
    bool JustLoggedIn();
    bool NearTownPortal(float distance);
    bool ProximitySatisfied(IReadOnlyList<EntityMatcher>? entities, IReadOnlyList<Pattern>? tiles, float distance);
    bool LootSatisfied(IReadOnlyList<ItemMatcher>? items);
    int KillProgress(IReadOnlyList<EntityMatcher>? entities);
    int InteractProgress(IReadOnlyList<EntityMatcher>? entities);
    int TalkProgress(IReadOnlyList<EntityMatcher>? entities);
    int SatisfiedFlagCount(IReadOnlyList<Pattern> flags);
}

// evaluates whether the current step is done. one place; replaces the four scattered advance paths.
public static class AdvanceEngine
{
    public const float DefaultProximity = 40f;

    public static bool IsStepComplete(RouteStep step, IWorldState world)
    {
        if (step?.Objectives == null || step.Objectives.Count == 0) return false;
        if (step.CompleteWhen == CompleteWhen.Any)
        {
            foreach (var o in step.Objectives)
                if (ObjectiveComplete(o, world)) return true;
            return false;
        }
        foreach (var o in step.Objectives)
            if (!ObjectiveComplete(o, world)) return false;
        return true;
    }

    public static bool ObjectiveComplete(Objective o, IWorldState world) => o.Type switch
    {
        ObjectiveType.Kill            => CountDone(o, world, world.KillProgress),
        ObjectiveType.Interact        => CountDone(o, world, world.InteractProgress),
        ObjectiveType.Talk            => CountDone(o, world, world.TalkProgress),
        ObjectiveType.Loot            => world.LootSatisfied(o.Items),
        ObjectiveType.Proximity       => world.ProximitySatisfied(o.Entities, null, o.Distance > 0f ? o.Distance : DefaultProximity),
        ObjectiveType.QuestFlag       => o.Flag != null && world.QuestFlagSatisfied(o.Flag),
        ObjectiveType.EnterArea       => o.AreaTarget != null && world.InAreaSatisfied(o.AreaTarget),
        ObjectiveType.ActivateWaypoint => world.WaypointPulsed(),
        ObjectiveType.Login           => world.JustLoggedIn(),
        ObjectiveType.TownPortal      => world.NearTownPortal(o.Distance > 0f ? o.Distance : DefaultProximity),
        ObjectiveType.Manual          => false,
        _                             => false,
    };

    // Kill/Interact/Talk: progress flags win when present (e.g. the 3 obelisks), else the live counter.
    private static bool CountDone(Objective o, IWorldState world, Func<IReadOnlyList<EntityMatcher>?, int> liveCount)
    {
        int needed = o.Count > 0 ? o.Count : 1;
        int done = o.ProgressFlags is { Count: > 0 } ? world.SatisfiedFlagCount(o.ProgressFlags) : liveCount(o.Entities);
        return done >= needed;
    }
}

// how a manual-backtrack hold reacts to the current step's completion each advance poll. pure so the runtime
// and tests share it. the hold exists so auto-advance doesn't snap you forward off a step you deliberately
// stepped back onto - but a step that goes incomplete->complete WHILE you sit on it (a quest flag flipping,
// an area entered) is real progress, so that fresh edge releases the hold. "seed" = was the step already
// complete the moment the hold began (or when the held step last changed); only a false->true edge breaks it.
public static class AdvanceHold
{
    public readonly record struct State(bool Held, string? SeedStepId, bool SeedComplete);
    public readonly record struct Decision(bool Advance, State Next);

    public static Decision Evaluate(State s, string currentStepId, bool complete)
    {
        // not held: normal behaviour, advance whenever the step is complete.
        if (!s.Held) return new Decision(complete, new State(false, null, false));

        // (re)seed on entry, or when the held step changes (double-backtrack, manual move): never advance the
        // poll we seed on, so landing on an already-complete step can't snap forward.
        if (currentStepId != s.SeedStepId)
            return new Decision(false, new State(true, currentStepId, complete));

        // already complete when we landed, or still not complete: keep holding.
        if (!complete || s.SeedComplete) return new Decision(false, s);

        // became complete while we sat here: real progress, release the hold and advance.
        return new Decision(true, new State(false, null, false));
    }
}

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

// parses Radar's targets.json (area RawName -> [ {Name, DisplayName, Rooms[], TargetType, Alternatives[]}, ... ])
// into pickable guidance rows for one area. Radar entries map to the three guidance kinds:
//   - a literal tile design name / Metadata/...tdt path  -> Tile   (ClusterTarget; the far zone-wide paths)
//   - TargetType:"Entity" with a Metadata path           -> Entity (matched by path)
//   - Name "*" + Rooms:["*foo*"]                          -> Room   (AreaGraph room-name filter; one per room)
// the global "*" area block (League Mechanic, Boss Room, Rune, ...) is what Radar draws in EVERY zone, so it's
// merged in after the area-specific rows. a bare "*" Name with no Rooms is unusable (whole-map) and skipped.
// Alternatives are flattened in, sharing the parent display name.
public static class RadarTargetsFile
{
    // one pickable row: a label plus the guidance Target it builds (kind + literal match + match-by). Count is
    // Radar's ExpectedCount for a Tile (how many physical clusters the pattern splits into, e.g. 2 staircases);
    // 1 for everything else. the resolver passes it to ClusterTarget so a multi-instance tile gets real per-cluster
    // centroids instead of one void midpoint.
    public readonly record struct Pick(string Label, TargetKind Kind, string Match, MatchKind MatchBy, int Count = 1);

    public static IReadOnlyList<Pick> ParseArea(string json, string areaId)
    {
        var outp = new List<Pick>();
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(areaId)) return outp;

        JObject root;
        try { root = JObject.Parse(json); } catch { return outp; }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddRow(string? label, TargetKind kind, string? match, MatchKind matchBy, int count = 1)
        {
            if (string.IsNullOrEmpty(match)) return;
            if (!seen.Add(kind + ":" + match)) return;
            outp.Add(new Pick(string.IsNullOrEmpty(label) ? Leaf(match!) : label!, kind, match!, matchBy, count < 1 ? 1 : count));
        }

        void AddEntry(JObject e, string? display)
        {
            var name = (string?)e["Name"];
            // Radar's live-entity target -> Entity, matched by metadata path.
            if (string.Equals((string?)e["TargetType"], "Entity", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(name) && name != "*") AddRow(display, TargetKind.Entity, name, MatchKind.Path);
                return;
            }
            // a real tile name wins (ClusterTarget); the room filter the bridge can't honor is ignored. carry
            // ExpectedCount so the resolver clusters a multi-instance tile the same way Radar does.
            if (!string.IsNullOrEmpty(name) && name != "*")
            {
                AddRow(display, TargetKind.Tile, name, MatchKind.Name, (int?)e["ExpectedCount"] ?? 1);
                return;
            }
            // Name "*" (or none): a room-scoped target -> one Room row per room glob.
            if (e["Rooms"] is JArray rooms)
                foreach (var r in rooms)
                    AddRow(display, TargetKind.Room, StripStars((string?)r), MatchKind.Name);
        }

        void AddBlock(JArray arr)
        {
            foreach (var e in arr.OfType<JObject>())
            {
                var display = (string?)e["DisplayName"];
                AddEntry(e, display);
                if (e["Alternatives"] is JArray alts)
                    foreach (var a in alts.OfType<JObject>())
                        AddEntry(a, display);
            }
        }

        // keys are area RawName (e.g. "G1_1"); the route's _areaId is the lowercased WorldArea.Id -> match loosely
        foreach (var prop in root.Properties())
            if (string.Equals(prop.Name, areaId, StringComparison.OrdinalIgnoreCase) && prop.Value is JArray arr)
            { AddBlock(arr); break; }

        // the global "*" block applies to every area (League Mechanic, Boss Room, Rune, ...); merge it in after.
        if (root["*"] is JArray glob) AddBlock(glob);

        return outp;
    }

    // Radar room filters are globs like "*three_door*"; our Pattern does literal case-insensitive contains, so
    // strip the surrounding "*" down to the literal token.
    private static string StripStars(string? s) => string.IsNullOrEmpty(s) ? "" : s!.Trim('*');

    // last path segment, minus a trailing .tdt, for labelling entries that carry no DisplayName.
    private static string Leaf(string tile)
    {
        var s = tile;
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s.Substring(slash + 1);
        if (s.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
        return s;
    }
}

// one recorded diagnostic event. Json is the inner field fragment (no braces), e.g. "area":"g1_2","act":1
public readonly record struct DiagEvent(DateTime Time, string Kind, string Json);

// fixed-cap rolling buffer of recent events. when full, oldest drops off. no ExileCore dependency so it
// unit-tests offline and stays cheap on the game loop (plain list append).
public sealed class DiagBuffer
{
    private readonly List<DiagEvent> _items;
    private readonly int _cap;

    public DiagBuffer(int cap = 300)
    {
        _cap = cap < 1 ? 1 : cap;
        _items = new List<DiagEvent>(_cap);
    }

    public int Count => _items.Count;

    public void Add(DateTime time, string kind, string json)
    {
        _items.Add(new DiagEvent(time, kind, json ?? ""));
        if (_items.Count > _cap)
            _items.RemoveRange(0, _items.Count - _cap);
    }

    public IReadOnlyList<DiagEvent> Snapshot() => _items.ToArray();

    public void Clear() => _items.Clear();
}

// non-reversible short tag for a profile id, so logs + the shared diagnostics export can tell characters
// apart without ever writing the actual character name. stable per name, can't be turned back into the name.
public static class ProfileMask
{
    public static string Mask(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return "(none)";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(profile.Trim()));
        var sb = new StringBuilder("char-", 11);
        for (int i = 0; i < 3; i++) sb.Append(hash[i].ToString("x2"));   // 3 bytes -> 6 hex
        return sb.ToString();
    }
}
