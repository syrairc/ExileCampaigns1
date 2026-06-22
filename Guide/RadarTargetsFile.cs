using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExileCampaigns.Guide;

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
