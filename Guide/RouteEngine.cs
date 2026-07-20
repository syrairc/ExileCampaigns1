using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Text.RegularExpressions;

namespace ExileCampaigns.Guide;

// what completes a step's objective. see AdvanceEngine for how each is evaluated.
public enum ObjectiveType { Kill, Interact, Talk, Loot, Proximity, QuestFlag, EnterArea, ActivateWaypoint, Login, TownPortal, Manual }

// when a step with several objectives advances.
public enum CompleteWhen { All, Any }

// how many path lines an objective's guidance draws: Nearest = one, to the closest target (default);
// All = a line to every resolved target (e.g. all 3 Ancient Seals).
public enum PathMode { Nearest, All }

// how an entity matcher matches a live entity: by render name or by metadata path.
public enum MatchKind { Name, Path }

// every value matched against game state. literal (default, case-insensitive contains) or a regex.
public sealed record Pattern(string Value, bool Regex = false);

// a world target (boss by Name, no-name object like NailStake by Path).
public sealed record EntityMatcher(Pattern Match, MatchKind MatchBy = MatchKind.Name);

// a Loot target: an inventory item plus how many must be held.
public sealed record ItemMatcher(Pattern Match, int Count = 1);

// what a guidance child points at. Tile = Radar ClusterTarget pattern / .tdt; Room = AreaGraph room-name
// filter; Entity = live entity by metadata Path or RenderName.
public enum TargetKind { Tile, Entity, Room }

// a guidance target shared by Path / Indicator / MinimapIcon. MatchBy + LivingOnly apply only to Entity.
// LivingOnly: only resolve a live entity that's actually alive (has Life, CurrentHP > 0), so an arrow/icon
// skips a corpse or a lifeless prop sharing the name. honored by Indicators + MinimapIcons (not the Paths channel).
public sealed record Target(TargetKind Kind, Pattern Match, MatchKind MatchBy = MatchKind.Name, bool LivingOnly = false);

// one ground/minimap route line. an Entity target that matches several live entities draws one line each.
public sealed record GuidePath(Target Target);

// one on-screen arrow/marker. Entity targets draw the arrow; Tile/Room are accepted but not drawn yet.
public sealed record Indicator(Target Target);

// single per-objective minimap icon. IconKey = SpriteIcon enum name; Tint = packed ARGB (default gold);
// Size = per-icon pixel size, null = use the global MinimapIcons.IconSize default.
public sealed record MinimapIcon(string IconKey, Target? Target = null, uint Tint = 0xFFFFC83Cu, float? Size = null)  // Tint default = gold
{
    public const uint GoldDefault = 0xFFFFC83Cu;   // gold, matches the interaction arrow
}

// one objective on a step. only the fields relevant to Type are used.
public sealed record Objective(
    ObjectiveType Type,
    IReadOnlyList<EntityMatcher>? Entities = null,   // Kill/Interact/Talk/Proximity (priority order)
    IReadOnlyList<ItemMatcher>? Items = null,        // Loot
    int Count = 1,                                   // Kill/Interact/Talk (Loot uses ItemMatcher.Count)
    float Distance = 0f,                             // Proximity (units; engine applies a default when 0)
    Pattern? Flag = null,                            // QuestFlag
    Pattern? AreaTarget = null,                      // EnterArea (area id)
    IReadOnlyList<Pattern>? ProgressFlags = null,    // optional per-target flags (multi-activate drop-each)
    string? Label = null,
    string? Note = null,
    IReadOnlyList<GuidePath>? Paths = null,          // guidance: ground/minimap route lines (independent of Type)
    IReadOnlyList<Indicator>? Indicators = null,     // guidance: on-screen arrows (independent of Type)
    IReadOnlyList<MinimapIcon>? MinimapIcons = null,// guidance: large-map icons (independent of Type)
    PathMode Mode = PathMode.Nearest);              // path line count: Nearest = one (default), All = per target

// one route step. self-describing: explicit act + area, plus its objectives. Id is the stable identity.
public sealed record RouteStep(
    string Id,
    int Act,
    string AreaId,
    string AreaName,
    string Text,
    bool Optional,
    CompleteWhen CompleteWhen,
    IReadOnlyList<Objective> Objectives,
    string? ImportFp,    // fnv1a of the upstream text this came from (import diff); null = user-created
    bool LeagueStart = false);   // league-start-only chore (crafting recipes, trials); hidden when "Show league-start steps" is off

// the whole route. Steps list order is the canonical sequence.
public sealed record RouteDocument(int Version, IReadOnlyList<RouteStep> Steps)
{
    public const int CurrentVersion = 2;   // v2: guidance decoupled into Paths/Indicators/MinimapIcon children
    public static readonly RouteDocument Empty = new(CurrentVersion, new List<RouteStep>());
}

// NOTE: deliberate exception to the "Guide stays Newtonsoft-free" rule. one shared serializer for the
// plugin, the migrate tool, and tests, so tool-written and runtime-read route.json never drift. stays
// ExileCore-free so the test project (which compiles Guide/*.cs) can round-trip it.
public static class RouteJson
{
    public static string Write(RouteDocument doc)
    {
        var root = new JObject
        {
            ["_doc"] = "Unified route store (ExileCampaigns). Source of truth; edited in-app and merged on import.",
            ["version"] = doc.Version,
            ["steps"] = new JArray(doc.Steps.Select(StepToJson)),
        };
        return root.ToString(Formatting.Indented);
    }

    public static RouteDocument Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return RouteDocument.Empty;
        try
        {
            var root = JObject.Parse(json);
            int version = (int?)root["version"] ?? RouteDocument.CurrentVersion;
            var steps = (root["steps"] as JArray ?? new JArray()).Select(StepFromJson).ToList();
            return new RouteDocument(version, steps);
        }
        catch (JsonException) { return RouteDocument.Empty; }
    }

    private static JObject StepToJson(RouteStep s)
    {
        var j = new JObject
        {
            ["id"] = s.Id,
            ["act"] = s.Act,
            ["areaId"] = s.AreaId,
            ["areaName"] = s.AreaName,
            ["text"] = s.Text,
            ["optional"] = s.Optional,
            ["completeWhen"] = s.CompleteWhen.ToString(),
            ["objectives"] = new JArray(s.Objectives.Select(ObjToJson)),
        };
        if (s.ImportFp != null) j["importFp"] = s.ImportFp;
        if (s.LeagueStart) j["leagueStart"] = true;
        return j;
    }

    private static RouteStep StepFromJson(JToken t)
    {
        var o = (JObject)t;
        var objs = (o["objectives"] as JArray ?? new JArray()).Select(ObjFromJson).ToList();
        return new RouteStep(
            (string?)o["id"] ?? "",
            (int?)o["act"] ?? 0,
            (string?)o["areaId"] ?? "",
            (string?)o["areaName"] ?? "",
            (string?)o["text"] ?? "",
            (bool?)o["optional"] ?? false,
            ParseEnum((string?)o["completeWhen"], CompleteWhen.All),
            objs,
            (string?)o["importFp"],
            (bool?)o["leagueStart"] ?? false);
    }

    private static JObject ObjToJson(Objective o)
    {
        var j = new JObject { ["type"] = o.Type.ToString() };
        if (o.Entities != null) j["entities"] = new JArray(o.Entities.Select(EntToJson));
        if (o.Items != null) j["items"] = new JArray(o.Items.Select(ItemToJson));
        if (o.Count != 1) j["count"] = o.Count;
        if (o.Distance != 0f) j["distance"] = o.Distance;
        if (o.Flag != null) j["flag"] = PatToJson(o.Flag);
        if (o.AreaTarget != null) j["areaTarget"] = PatToJson(o.AreaTarget);
        if (o.ProgressFlags != null) j["progressFlags"] = new JArray(o.ProgressFlags.Select(PatToJson));
        if (o.Label != null) j["label"] = o.Label;
        if (o.Note != null) j["note"] = o.Note;
        if (o.Paths != null && o.Paths.Count > 0) j["paths"] = new JArray(o.Paths.Select(PathToJson));
        if (o.Indicators != null && o.Indicators.Count > 0) j["indicators"] = new JArray(o.Indicators.Select(IndToJson));
        if (o.MinimapIcons != null && o.MinimapIcons.Count > 0) j["minimapIcons"] = new JArray(o.MinimapIcons.Select(MinimapToJson));
        if (o.Mode != PathMode.Nearest) j["mode"] = o.Mode.ToString();
        return j;
    }

    private static Objective ObjFromJson(JToken t)
    {
        var o = (JObject)t;
        var type = ParseEnum((string?)o["type"], ObjectiveType.Manual);
        var entities = (o["entities"] as JArray)?.Select(EntFromJson).ToList();
        var paths = (o["paths"] as JArray)?.Select(PathFromJson).ToList();
        var indicators = (o["indicators"] as JArray)?.Select(IndFromJson).ToList();
        // v1 back-compat: pre-decouple objectives stored pathType + tiles. reconstruct guidance children.
        if (paths == null && indicators == null)
        {
            var legacyTiles = (o["tiles"] as JArray)?.Select(PatFromJson).ToList();
            (paths, indicators) = ReconstructV1Guidance(type, entities, legacyTiles);
        }
        return new Objective(
            type,
            entities,
            (o["items"] as JArray)?.Select(ItemFromJson).ToList(),
            (int?)o["count"] ?? 1,
            (float?)o["distance"] ?? 0f,
            o["flag"] is JObject f ? PatFromJson(f) : null,
            o["areaTarget"] is JObject a ? PatFromJson(a) : null,
            (o["progressFlags"] as JArray)?.Select(PatFromJson).ToList(),
            (string?)o["label"],
            (string?)o["note"],
            paths,
            indicators,
            ReadMinimapIcons(o),
            ParseEnum((string?)o["mode"], PathMode.Nearest));
    }

    // minimap icons: the v2 `minimapIcons` array, or a single legacy `minimapIcon` object wrapped to a list.
    private static IReadOnlyList<MinimapIcon>? ReadMinimapIcons(JObject o)
    {
        if (o["minimapIcons"] is JArray arr) return arr.Select(MinimapFromJson).ToList();
        if (o["minimapIcon"] is JObject mi) return new List<MinimapIcon> { MinimapFromJson(mi) };
        return null;
    }

    // pre-decouple (v1) objectives used pathType + tiles instead of Paths/Indicators children. rebuild the
    // children with the same tile routing the old LegacyView used (Interact/Proximity/Talk tiles -> Room,
    // else Tile; every entity matcher -> Entity path + indicator).
    private static (List<GuidePath>?, List<Indicator>?) ReconstructV1Guidance(
        ObjectiveType type, IReadOnlyList<EntityMatcher>? ents, IReadOnlyList<Pattern>? tiles)
    {
        var paths = new List<GuidePath>();
        var inds = new List<Indicator>();
        if (ents != null)
            foreach (var e in ents)
            {
                var tgt = new Target(TargetKind.Entity, e.Match, e.MatchBy);
                paths.Add(new GuidePath(tgt));
                inds.Add(new Indicator(tgt));
            }
        if (tiles != null)
        {
            var kind = type is ObjectiveType.Interact or ObjectiveType.Proximity or ObjectiveType.Talk
                ? TargetKind.Room : TargetKind.Tile;
            foreach (var t in tiles) paths.Add(new GuidePath(new Target(kind, t)));
        }
        return (paths.Count > 0 ? paths : null, inds.Count > 0 ? inds : null);
    }

    private static JObject PatToJson(Pattern p)
    {
        var j = new JObject { ["value"] = p.Value };
        if (p.Regex) j["regex"] = true;
        return j;
    }
    private static Pattern PatFromJson(JToken t) =>
        new((string?)t["value"] ?? "", (bool?)t["regex"] ?? false);

    private static JObject EntToJson(EntityMatcher e) =>
        new() { ["match"] = PatToJson(e.Match), ["matchBy"] = e.MatchBy.ToString() };
    private static EntityMatcher EntFromJson(JToken t) =>
        new(PatFromJson(t["match"]!), ParseEnum((string?)t["matchBy"], MatchKind.Name));

    private static JObject ItemToJson(ItemMatcher i) =>
        new() { ["match"] = PatToJson(i.Match), ["count"] = i.Count };
    private static ItemMatcher ItemFromJson(JToken t) =>
        new(PatFromJson(t["match"]!), (int?)t["count"] ?? 1);

    private static JObject TargetToJson(Target t)
    {
        var j = new JObject { ["kind"] = t.Kind.ToString(), ["match"] = PatToJson(t.Match) };
        if (t.Kind == TargetKind.Entity) j["matchBy"] = t.MatchBy.ToString();
        if (t.Kind == TargetKind.Entity && t.LivingOnly) j["living"] = true;
        return j;
    }
    private static Target TargetFromJson(JToken t) =>
        new(ParseEnum((string?)t["kind"], TargetKind.Tile),
            t["match"] is JObject m ? PatFromJson(m) : new Pattern(""),
            ParseEnum((string?)t["matchBy"], MatchKind.Name),
            (bool?)t["living"] ?? false);

    private static JObject PathToJson(GuidePath p) => new() { ["target"] = TargetToJson(p.Target) };
    private static GuidePath PathFromJson(JToken t) => new(TargetFromJson(t["target"] ?? t));
    private static JObject IndToJson(Indicator i) => new() { ["target"] = TargetToJson(i.Target) };
    private static Indicator IndFromJson(JToken t) => new(TargetFromJson(t["target"] ?? t));
    private static JObject MinimapToJson(MinimapIcon m)
    {
        var j = new JObject { ["iconKey"] = m.IconKey, ["tint"] = m.Tint };
        if (m.Target != null) j["target"] = TargetToJson(m.Target);
        if (m.Size != null) j["size"] = m.Size.Value;
        return j;
    }
    private static MinimapIcon MinimapFromJson(JToken t) =>
        new((string?)t["iconKey"] ?? "",
            t["target"] is JObject tg ? TargetFromJson(tg) : null,
            (uint?)t["tint"] ?? MinimapIcon.GoldDefault,
            (float?)t["size"]);

    private static T ParseEnum<T>(string? s, T fallback) where T : struct =>
        System.Enum.TryParse<T>(s, true, out var v) ? v : fallback;
}

public enum ChildKind { Path, Indicator, MinimapIcon }

// pure edit helpers over the immutable route records. the ImGui editor is a thin caller; all list math and
// record-rebuilding lives here so it's unit-tested without any game state.
public static class RouteEditing
{
    public static RouteStep SkeletonStep(int act, string areaId, string areaName) =>
        new(Guid.NewGuid().ToString("N"), act, areaId, areaName, "(new step)", false,
            CompleteWhen.All, new List<Objective> { new(ObjectiveType.Manual) }, null);

    public static Objective BlankObjective() => new(ObjectiveType.Manual);

    // triage quick-bind: make the step advance on `flag` via a QuestFlag objective, CompleteWhen.Any.
    // if the step's sole objective is a bare Manual/QuestFlag, retype it in place (guidance children stay,
    // since guidance is decoupled from Type) instead of stacking a second objective; otherwise append one so
    // existing kill/talk/path objectives keep providing guidance.
    public static RouteStep AddQuestFlagObjective(RouteStep step, string flag)
    {
        var pat = new Pattern(flag);
        if (step.Objectives.Count == 1 &&
            step.Objectives[0].Type is ObjectiveType.Manual or ObjectiveType.QuestFlag)
        {
            var only = step.Objectives[0] with { Type = ObjectiveType.QuestFlag, Flag = pat };
            return step with { Objectives = new List<Objective> { only }, CompleteWhen = CompleteWhen.Any };
        }
        var objs = step.Objectives.ToList();
        objs.Add(new Objective(ObjectiveType.QuestFlag, Flag: pat));
        return step with { Objectives = objs, CompleteWhen = CompleteWhen.Any };
    }

    // triage quick-bind: make the step advance on entering `areaId` via an EnterArea objective, CompleteWhen.Any.
    // same in-place-vs-append rule as AddQuestFlagObjective: a sole bare advance objective is retyped (guidance
    // children stay, since guidance is decoupled from Type), otherwise we append so existing objectives keep theirs.
    public static RouteStep AddEnterAreaObjective(RouteStep step, string areaId)
    {
        var pat = new Pattern(areaId);
        if (step.Objectives.Count == 1 &&
            step.Objectives[0].Type is ObjectiveType.Manual or ObjectiveType.QuestFlag or ObjectiveType.EnterArea)
        {
            var only = step.Objectives[0] with { Type = ObjectiveType.EnterArea, AreaTarget = pat, Flag = null };
            return step with { Objectives = new List<Objective> { only }, CompleteWhen = CompleteWhen.Any };
        }
        var objs = step.Objectives.ToList();
        objs.Add(new Objective(ObjectiveType.EnterArea, AreaTarget: pat));
        return step with { Objectives = objs, CompleteWhen = CompleteWhen.Any };
    }

    public static Objective AddPath(Objective o, GuidePath p) => o with { Paths = Append(o.Paths, p) };

    public static Objective AddIndicator(Objective o, Indicator i) => o with { Indicators = Append(o.Indicators, i) };

    public static Objective AddMinimapIcon(Objective o, MinimapIcon icon) => o with { MinimapIcons = Append(o.MinimapIcons, icon) };

    // replace a Path/Indicator's Target in place (e.g. toggle LivingOnly). no-op if out of range.
    public static Objective ReplaceTarget(Objective o, ChildKind kind, int index, Target target)
    {
        if (kind == ChildKind.Path)
        {
            var list = (o.Paths ?? new List<GuidePath>()).ToList();
            if (index < 0 || index >= list.Count) return o;
            list[index] = new GuidePath(target);
            return o with { Paths = list };
        }
        else
        {
            var list = (o.Indicators ?? new List<Indicator>()).ToList();
            if (index < 0 || index >= list.Count) return o;
            list[index] = new Indicator(target);
            return o with { Indicators = list };
        }
    }

    // replace the icon at index in place (sprite/tint/size/target edits). no-op if out of range.
    public static Objective UpdateMinimapIcon(Objective o, int index, MinimapIcon icon)
    {
        var list = (o.MinimapIcons ?? new List<MinimapIcon>()).ToList();
        if (index < 0 || index >= list.Count) return o;
        list[index] = icon;
        return o with { MinimapIcons = list };
    }

    public static Objective RemoveAt(Objective o, ChildKind kind, int index)
    {
        switch (kind)
        {
            case ChildKind.Path:
            {
                var list = (o.Paths ?? new List<GuidePath>()).ToList();
                if (index < 0 || index >= list.Count) return o;
                list.RemoveAt(index);
                return o with { Paths = list };
            }
            case ChildKind.MinimapIcon:
            {
                var list = (o.MinimapIcons ?? new List<MinimapIcon>()).ToList();
                if (index < 0 || index >= list.Count) return o;
                list.RemoveAt(index);
                return o with { MinimapIcons = list };
            }
            default:
            {
                var inds = (o.Indicators ?? new List<Indicator>()).ToList();
                if (index < 0 || index >= inds.Count) return o;
                inds.RemoveAt(index);
                return o with { Indicators = inds };
            }
        }
    }

    public static Objective MoveChild(Objective o, ChildKind kind, int index, int delta)
    {
        switch (kind)
        {
            case ChildKind.Path:
            {
                var list = (o.Paths ?? new List<GuidePath>()).ToList();
                return Move(list, index, delta) ? o with { Paths = list } : o;
            }
            case ChildKind.MinimapIcon:
            {
                var list = (o.MinimapIcons ?? new List<MinimapIcon>()).ToList();
                return Move(list, index, delta) ? o with { MinimapIcons = list } : o;
            }
            default:
            {
                var inds = (o.Indicators ?? new List<Indicator>()).ToList();
                return Move(inds, index, delta) ? o with { Indicators = inds } : o;
            }
        }
    }

    private static List<T> Append<T>(IReadOnlyList<T>? src, T item)
    {
        var list = src != null ? new List<T>(src) : new List<T>();
        list.Add(item);
        return list;
    }

    private static bool Move<T>(List<T> list, int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= list.Count || to < 0 || to >= list.Count) return false;
        (list[index], list[to]) = (list[to], list[index]);
        return true;
    }
}

// compiles a Pattern once and matches candidates. literal = case-insensitive substring (today's behaviour);
// regex = compiled IgnoreCase regex. invalid regex falls back to literal and exposes Error for a diagnostic.
public sealed class PatternMatcher
{
    private readonly Pattern _pattern;
    private readonly Regex? _regex;
    public string? Error { get; }

    public PatternMatcher(Pattern pattern)
    {
        _pattern = pattern ?? new Pattern("");
        if (_pattern.Regex && !string.IsNullOrEmpty(_pattern.Value))
        {
            try { _regex = new Regex(_pattern.Value, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch (ArgumentException ex) { _regex = null; Error = ex.Message; }
        }
    }

    public bool IsMatch(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (_regex != null) return _regex.IsMatch(candidate);
        if (string.IsNullOrEmpty(_pattern.Value)) return false;
        return candidate.IndexOf(_pattern.Value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

// mutable in-memory route: CRUD over steps preserving list order. area grouping for display
// is a rendering concern handled by RouteRepository, not here. pure C#.
public sealed class RouteStore
{
    private readonly List<RouteStep> _steps;

    public RouteStore(RouteDocument doc)
    {
        _steps = new List<RouteStep>(doc?.Steps ?? Array.Empty<RouteStep>());
    }

    public IReadOnlyList<RouteStep> Steps => _steps;

    public void Add(RouteStep step) { _steps.Add(step); }

    public bool Update(RouteStep step)
    {
        for (int i = 0; i < _steps.Count; i++)
            if (_steps[i].Id == step.Id) { _steps[i] = step; return true; }
        return false;
    }

    public bool Delete(string id)
    {
        int removed = _steps.RemoveAll(s => s.Id == id);
        return removed > 0;
    }

    public bool Move(string id, int targetIndex)
    {
        int from = _steps.FindIndex(s => s.Id == id);
        if (from < 0) return false;
        var step = _steps[from];
        _steps.RemoveAt(from);
        targetIndex = Math.Clamp(targetIndex, 0, _steps.Count);
        _steps.Insert(targetIndex, step);
        return true;
    }

    // insert immediately before/after the anchor step. false if anchor absent (no insert).
    public bool InsertRelative(string anchorId, RouteStep step, bool before)
    {
        int at = _steps.FindIndex(s => s.Id == anchorId);
        if (at < 0) return false;
        _steps.Insert(before ? at : at + 1, step);
        return true;
    }

    // clone the step at id with a fresh GUID, insert right after it. returns the new id, or null if absent.
    public string? Duplicate(string id)
    {
        int at = _steps.FindIndex(s => s.Id == id);
        if (at < 0) return null;
        var newId = Guid.NewGuid().ToString("N");
        _steps.Insert(at + 1, _steps[at] with { Id = newId });
        return newId;
    }

    public RouteDocument ToDocument() => new(RouteDocument.CurrentVersion, new List<RouteStep>(_steps));
}

// flattened step + its act and 1-based position within that act, for sequential navigation across the
// campaign (StepInAct drives the steps-tracker number column + act headers). Model is null for area
// header rows; HeaderAreaId/HeaderAreaName carry the zone identity for those rows.
public sealed record FlatStep(int Act, int StepInAct, RouteStep? Model, string? HeaderAreaId = null, string? HeaderAreaName = null)
{
    public bool IsHeader => Model == null;
    public string DisplayText =>
        Model != null ? Model.Text
        : !string.IsNullOrEmpty(HeaderAreaName) ? HeaderAreaName!
        : !string.IsNullOrEmpty(HeaderAreaId) ? $"-> {HeaderAreaId}" : "";
}

// loads per-act route files, flattens to one ordered sequence, tracks the current step. drives memory
// auto-advance: each zone-header step carries an area id mapped to its position, so an AreaChange jumps
// straight to it. pure C# (no ExileCore dep), unit-testable.
public sealed class RouteRepository
{
    private readonly List<FlatStep> _steps = new();
    private readonly Dictionary<string, List<int>> _areaToIndices = new(StringComparer.OrdinalIgnoreCase);
    // effective area id per step: most recent zone-header AreaId at or before that step, so sub-steps in a
    // zone (kill/reward, no AreaId of their own) still know their zone. quest-flag auto-advance confines
    // its search to the flag's area block.
    private readonly List<string> _stepArea = new();
    // cleaned zone label parallel to _stepArea -- only set when the step is a zone header, carried forward
    // so every sub-step in a zone knows the readable name without re-scanning headers.
    private readonly List<string> _stepAreaName = new();

    public IReadOnlyList<FlatStep> Steps => _steps;
    public int Current { get; private set; }
    public string? Status { get; private set; }

    // mirrors Settings.ShowOptional. when false, optional steps are hidden from the overlay AND navigation
    // skips them -- auto/manual advance and back land only on visible steps, never a hidden optional one.
    // default true so position restore / sync / tests behave exactly as before unless the host sets it.
    public bool IncludeOptional { get; set; } = true;

    // mirrors Settings.ShowLeagueStart. when false, league-start steps (crafting recipes, trials) are hidden
    // from the overlay AND navigation skips them, same as a hidden optional. default true.
    public bool IncludeLeagueStart { get; set; } = true;
    public FlatStep? CurrentStep => _steps.Count > 0 && Current < _steps.Count ? _steps[Current] : null;

    // effective area id of the current step: its own AreaId if it has one (zone header), else the most
    // recent header above it. sub-steps (kill/loot/enter-text) carry no AreaId of their own, so the path
    // resolver needs this to key into the authored area->tile map.
    public string CurrentAreaId => Current >= 0 && Current < _stepArea.Count ? _stepArea[Current] : "";

    // readable zone label of the current step's area (cleaned of [tags] and (level) decorations),
    // for the route header's top-right. "" when there is no current step.
    public string CurrentAreaName => Current >= 0 && Current < _stepAreaName.Count ? _stepAreaName[Current] : "";

    // count of real (numbered) steps in an act, for the route-progress stat. headers carry StepInAct 0.
    public int StepsInAct(int act) => _steps.Count(s => s.Act == act && s.StepInAct > 0);

    // a zone-label line: kept in the model for the area->index map, but never an objective. navigation
    // never rests on one and they're excluded from Previous/Upcoming.
    private static bool IsHeaderStep(FlatStep s) => s.IsHeader;

    // a step navigation must never rest on: a zone-label header always, plus -- when optional is hidden --
    // an optional step. headers and (hidden) optionals are stepped over together by advance/back.
    private bool IsSkippable(int i) =>
        IsHeaderStep(_steps[i])
        || (!IncludeOptional && (_steps[i].Model?.Optional ?? false))
        || (!IncludeLeagueStart && (_steps[i].Model?.LeagueStart ?? false));

    // after an advance landed Current on a skippable row (header, or hidden optional), step forward to the
    // next visible objective. falls back to the nearest visible step behind so we never rest on a hidden
    // row when nothing visible lies ahead (e.g. last step is a hidden optional).
    private void SnapForwardVisible()
    {
        if (_steps.Count == 0) { Current = 0; return; }
        if (!IsSkippable(Current)) return;
        for (int i = Current; i < _steps.Count; i++)
            if (!IsSkippable(i)) { Current = i; return; }
        for (int i = Current - 1; i >= 0; i--)
            if (!IsSkippable(i)) { Current = i; return; }
    }

    // if Current landed on a zone-label header, move to the first real step in that zone (forward
    // preferred so entering a zone lands on its first task; fall back to the last real step behind).
    private void SnapOffHeader()
    {
        if (_steps.Count == 0) { Current = 0; return; }
        if (!IsHeaderStep(_steps[Current])) return;
        for (int i = Current; i < _steps.Count; i++)
            if (!IsHeaderStep(_steps[i])) { Current = i; return; }
        for (int i = Current - 1; i >= 0; i--)
            if (!IsHeaderStep(_steps[i])) { Current = i; return; }
    }

    // load the unified RouteDocument: one header row per area boundary, then each model step.
    public bool LoadFromDocument(RouteDocument doc)
    {
        _steps.Clear(); _areaToIndices.Clear(); _stepArea.Clear(); _stepAreaName.Clear();
        Current = 0;
        if (doc == null) { Status = "no route document"; return false; }

        int act = -1, stepInAct = 0;
        string curArea = "";
        foreach (var model in doc.Steps)
        {
            // act split resets the per-act objective number, matching the act-file flatten.
            if (model.Act != act) { act = model.Act; stepInAct = 0; }

            // area boundary: emit a header row (StepInAct 0, Model null).
            if (!string.Equals(model.AreaId, curArea, System.StringComparison.OrdinalIgnoreCase))
            {
                curArea = model.AreaId ?? "";
                int hidx = _steps.Count;
                _steps.Add(new FlatStep(model.Act, 0, null, model.AreaId ?? "", model.AreaName ?? ""));
                if (!string.IsNullOrEmpty(curArea))
                {
                    if (!_areaToIndices.TryGetValue(curArea, out var hlist))
                        _areaToIndices[curArea] = hlist = new List<int>();
                    hlist.Add(hidx);
                }
                _stepArea.Add(curArea);
                _stepAreaName.Add(model.AreaName ?? "");
            }

            int idx = _steps.Count;
            _steps.Add(new FlatStep(model.Act, ++stepInAct, model));
            _stepArea.Add(curArea);
            _stepAreaName.Add(model.AreaName ?? "");
        }

        SnapOffHeader();
        Status = $"{doc.Steps.Count} steps ({_steps.Count} rows)";
        return _steps.Count > 0;
    }

    // how far ahead auto-advance may jump. early arrival at a zone that next appears far ahead
    // shouldn't skip a chunk of the campaign.
    private const int AdvanceWindow = 5;

    // entered a new area: jump to the route step for that area id. forward-only: nearest occurrence at or
    // ahead of current, within AdvanceWindow. a revisit whose steps are all behind us (e.g. TP to town),
    // or whose next step is beyond the window, leaves current unchanged.
    public void OnAreaChanged(string? areaIdLower)
    {
        if (string.IsNullOrEmpty(areaIdLower)) return;
        if (!_areaToIndices.TryGetValue(areaIdLower, out var idxs) || idxs.Count == 0) return;
        int next = int.MaxValue;
        foreach (var i in idxs)
            if (i >= Current && i - Current <= AdvanceWindow && i < next) next = i;
        if (next == int.MaxValue) return;

        // the matched step is the zone-label header. rest the objective on the zone's first VISIBLE task
        // (skips the header and, when optional is hidden, any leading optional steps).
        Current = next;
        SnapForwardVisible();
    }

    // step over zone-label headers (and hidden optionals) so manual next/prev lands only on visible objectives.
    public void Next()
    {
        if (_steps.Count == 0) return;
        int i = Current + 1;
        while (i < _steps.Count && IsSkippable(i)) i++;
        if (i < _steps.Count) Current = i;
    }

    public void Prev()
    {
        if (_steps.Count == 0) return;
        int i = Current - 1;
        while (i >= 0 && IsSkippable(i)) i--;
        if (i >= 0) Current = i;
    }

    // restore a saved position (clamped to the loaded route, snapped off any zone label).
    public void SetCurrent(int index)
    {
        if (_steps.Count == 0) { Current = 0; return; }
        Current = Math.Clamp(index, 0, _steps.Count - 1);
        SnapOffHeader();
    }

    // upcoming steps after current (optionally hiding optional), up to `count`.
    public IEnumerable<FlatStep> Upcoming(int count, bool includeOptional, bool includeLeagueStart = true)
    {
        int taken = 0;
        for (int i = Current + 1; i < _steps.Count && taken < count; i++)
        {
            if (IsHeaderStep(_steps[i])) continue;              // zone labels aren't objectives
            if (!includeOptional && (_steps[i].Model?.Optional ?? false)) continue;
            if (!includeLeagueStart && (_steps[i].Model?.LeagueStart ?? false)) continue;
            taken++;
            yield return _steps[i];
        }
    }

    // `count` steps before current (optionally hiding optional), oldest first so they read
    // top-to-bottom above the current step.
    public IEnumerable<FlatStep> Previous(int count, bool includeOptional, bool includeLeagueStart = true)
    {
        var result = new List<FlatStep>();
        for (int i = Current - 1; i >= 0 && result.Count < count; i--)
        {
            if (IsHeaderStep(_steps[i])) continue;              // zone labels aren't objectives
            if (!includeOptional && (_steps[i].Model?.Optional ?? false)) continue;
            if (!includeLeagueStart && (_steps[i].Model?.LeagueStart ?? false)) continue;
            result.Add(_steps[i]);
        }
        result.Reverse();
        return result;
    }
}

// manual "sync to character": pick the tracker index from live state. quest flags are the only reliable
// retroactive signal (kills/talks/proximity are momentary); the current area nudges forward when the player
// is physically past where the flags confirm. pure - the live flag check + current area are passed in.
public static class RouteSync
{
    // how many real steps ahead of the flag floor we nudge into the current area; bounds revisit/town
    // jumps. counts steps only - header rows are skipped, else a few zone labels between the last flag
    // and the current zone eat the budget and sync stalls in the prior act.
    public const int AreaNudgeWindow = 16;

    // returns the FlatStep index to SetCurrent to, or -1 when there are no steps.
    public static int ResolveSyncTarget(
        IReadOnlyList<FlatStep> steps, Func<Pattern, bool> isFlagTrue, string? currentAreaLower)
    {
        if (steps == null || steps.Count == 0) return -1;

        // cap the flag scan at the act you're physically in. quest flags aren't guaranteed monotonic, so a
        // stray later-act flag reading true would otherwise set flagFloor near the end and the forward-only
        // nudge below couldn't pull back. current act = first route step in the current area; no match -> no cap.
        int currentAct = int.MaxValue;
        if (!string.IsNullOrEmpty(currentAreaLower))
            for (int i = 0; i < steps.Count; i++)
            {
                var m = steps[i].Model;
                if (m != null && string.Equals(m.AreaId, currentAreaLower, StringComparison.OrdinalIgnoreCase))
                { currentAct = steps[i].Act; break; }
            }

        // furthest step (route order) whose QuestFlag objective is satisfied, ignoring later-act flags.
        int flagFloor = -1;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Act > currentAct) break;   // past the player's act, flags here can't be real progress
            var m = steps[i].Model;
            if (m?.Objectives == null) continue;
            foreach (var o in m.Objectives)
                if (o.Type == ObjectiveType.QuestFlag && o.Flag != null && isFlagTrue(o.Flag)) { flagFloor = i; break; }
        }

        int target = NextNonHeader(steps, flagFloor + 1);

        // forward area nudge: first current-area step within the window of real steps ahead (covers
        // "entered a new zone, no flag tripped yet"). header rows don't count toward the window, so a
        // sparse-flag act transition with several zone labels still resolves. never backward.
        if (!string.IsNullOrEmpty(currentAreaLower))
        {
            int budget = AreaNudgeWindow;
            for (int j = target; j < steps.Count && budget > 0; j++)
            {
                var m = steps[j].Model;
                if (m == null) continue;   // zone-label header, not a step
                if (string.Equals(m.AreaId, currentAreaLower, StringComparison.OrdinalIgnoreCase))
                { target = j; break; }
                budget--;
            }
        }
        return target;
    }

    // first non-header (Model != null) at or after `from`; falls back to the last non-header, else 0.
    private static int NextNonHeader(IReadOnlyList<FlatStep> steps, int from)
    {
        for (int i = Math.Max(0, from); i < steps.Count; i++)
            if (steps[i].Model != null) return i;
        for (int i = steps.Count - 1; i >= 0; i--)
            if (steps[i].Model != null) return i;
        return 0;
    }
}
