// NOTE: deliberate exception to the "Guide stays Newtonsoft-free" rule. one shared serializer for the
// plugin, the migrate tool, and tests, so tool-written and runtime-read route.json never drift. stays
// ExileCore-free so the test project (which compiles Guide/*.cs) can round-trip it.
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileCampaigns.Guide;

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
