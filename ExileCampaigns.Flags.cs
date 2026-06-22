using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;

namespace ExileCampaigns;

// Quest-flag harvester (dev tool). ServerData.QuestFlags: ~2240 booleans for the whole game (PoE2 campaign
// + PoE1-legacy + tutorials/UI). Campaign beats live under names like "G1_2.../VisitedG1_2/WaterLevelLoweredSeen"
// but per-step flag isn't guessable, so when LogQuestFlags is on we snapshot and log every newly-true flag
// with its area+step. A play session then yields raw (flag -> step) pairs to curate into a static map.
public partial class ExileCampaigns
{
    private HashSet<string>? _flagSnapshot;   // names true as of last poll (null = not seeded yet)
    private DateTime _lastFlagPoll;
    private string FlagLogPath => Path.Combine(ConfigDirectory, "quest-flag-harvest.jsonl");

    // recent flag flips with timestamps (newest last, cap 20) for the triage "broken auto-advance" report's
    // "last 10 flags" capture. distinct from _recentHarvestedFlags (no timestamps; editor picker).
    private readonly List<(string Flag, string Time)> _recentFlagsTimed = new();

    // two-pass delayed rescan: some exits spawn on boss death (1.5s), others after loot/follow-up event (4s)
    private DateTime? _pendingRescanAt;   // first pass ~1.5s after flag batch
    private DateTime? _pendingRescan2At;  // second pass ~4s after flag batch
    private string? _rescanAreaId;
    private int _rescanAct;
    private int _rescanStep;

    // onboarding/UI flags that flip while leveling but say nothing about campaign progress, never harvest these
    private static bool IsNoiseFlag(string name) =>
        name.StartsWith("ArticleRead", StringComparison.Ordinal)
        || name.StartsWith("OpenedShop", StringComparison.Ordinal)
        || name.Contains("Tutorial", StringComparison.Ordinal);   // *Tutorial* anywhere: CompletedXTutorial, TutorialPanel...

    private IReadOnlyDictionary<QuestFlag, bool>? ReadQuestFlags()
    {
        try { return GameController?.IngameState?.Data?.ServerData?.QuestFlags; }
        catch { return null; }
    }

    // from Tick when LogQuestFlags on OR the triage panel is open. ~2Hz; first poll seeds snapshot. when only
    // the triage panel is open it maintains the in-memory flag rings only (no jsonl logging, no dump, no rescans).
    private void HarvestQuestFlags()
    {
        if ((DateTime.Now - _lastFlagPoll).TotalSeconds < 0.5) return;
        _lastFlagPoll = DateTime.Now;

        var flags = ReadQuestFlags();
        if (flags == null || flags.Count == 0) return;

        var nowTrue = new HashSet<string>();
        foreach (var kv in flags)
            if (kv.Value) nowTrue.Add(kv.Key.ToString());

        if (_flagSnapshot == null)
        {
            _flagSnapshot = nowTrue;
            if (Settings.LogQuestFlags)   // dump + arm-message are harvest side-effects, skip on triage-only runs
            {
                DumpAllFlagNames(flags);
                LogMessage($"ExileCampaigns -> quest-flag harvest armed ({flags.Count} flags, {nowTrue.Count} true). Logging to {FlagLogPath}");
            }
            return;
        }

        // genuinely-new, non-noise flags this poll
        var fresh = new List<string>();
        foreach (var name in nowTrue)
            if (!_flagSnapshot.Contains(name) && !IsNoiseFlag(name)) fresh.Add(name);

        if (fresh.Count > 0)
        {
            // in-memory rings always (also when only the triage panel is open): editor pickers, capture-current,
            // and the triage recent-flags quick-bind list all read these.
            _lastHarvestedFlag = fresh[fresh.Count - 1];
            // keep recent flags for the editor's click-to-assign list (oldest first, cap 25)
            foreach (var flag in fresh) _recentHarvestedFlags.Add(flag);
            if (_recentHarvestedFlags.Count > 25)
                _recentHarvestedFlags.RemoveRange(0, _recentHarvestedFlags.Count - 25);
            // timestamped copy for the triage advance-report capture + recent-flags quick-bind panel
            foreach (var flag in fresh) _recentFlagsTimed.Add((flag, DateTime.Now.ToString("HH:mm:ss")));
            if (_recentFlagsTimed.Count > 20)
                _recentFlagsTimed.RemoveRange(0, _recentFlagsTimed.Count - 20);

            // jsonl logging + post-flag rescans are harvest-only; a triage-only run stays side-effect-free
            if (Settings.LogQuestFlags)
            {
                // snapshot context once for the batch, rooms + nearby entities don't change per-flag
                var near = NearbyEntitiesJson();
                var rooms = RoomsJson();
                var step = _route.CurrentStep?.DisplayText ?? "";
                foreach (var flag in fresh) AppendFlagEvent(flag, step, near, rooms);

                // rescan at 1.5s (immediate post-kill exits) and 4s (late spawns gated on loot/conversation)
                _pendingRescanAt  = DateTime.Now.AddSeconds(1.5);
                _pendingRescan2At = DateTime.Now.AddSeconds(4.0);
                _rescanAreaId = _areaId;
                _rescanAct = _act;
                _rescanStep = _route.Current;
            }
        }
        _flagSnapshot = nowTrue;

        DoDelayedRescanIfDue(ref _pendingRescanAt,  pass: 1);
        DoDelayedRescanIfDue(ref _pendingRescan2At, pass: 2);
    }

    private void AppendFlagEvent(string flag, string stepText, string nearJson, string roomsJson)
    {
        try
        {
            var line = "{" +
                $"\"t\":\"{DateTime.Now:HH:mm:ss}\"," +
                $"\"flag\":{Quote(flag)}," +
                $"\"area\":{Quote(_areaId)}," +
                $"\"act\":{_act}," +
                $"\"step\":{_route.Current}," +
                $"\"text\":{Quote(stepText)}," +
                $"\"near\":{nearJson}," +
                $"\"rooms\":{roomsJson}" +
                "}";
            File.AppendAllText(FlagLogPath, line + Environment.NewLine);
            LogMessage($"ExileCampaigns -> quest flag set: {flag}  (area {_areaId}, step {_route.Current}: {stepText})");
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> flag log failed: {ex.Message}"); }
    }

    // notable entities near player, nearest-first JSON array: monsters (alive or fresh corpse), NPCs, chests
    // in radius, plus any rare/unique in range. these identify the boss/objective behind an opaque flag.
    // path = real metadata id, RenderName = display name, rarity Rare/Unique = boss.
    private string NearbyEntitiesJson(float radius = 120f, int max = 15)
    {
        var found = new List<(float Dist, string Json)>();
        try
        {
            var ents = GameController?.EntityListWrapper?.Entities;
            if (ents == null) return "[]";
            foreach (var e in ents)
            {
                if (e == null || !e.IsValid) continue;
                var type = e.Type.ToString();
                var rarity = e.Rarity.ToString();
                bool boss = rarity is "Rare" or "Unique";
                bool kind = type is "Monster" or "Npc" or "Chest";
                if (!kind && !boss) continue;
                if (type is "Player") continue;
                var dist = e.DistancePlayer;
                if (dist > radius && !boss) continue;

                var json = "{" +
                    $"\"name\":{Quote(e.RenderName ?? "")}," +
                    $"\"path\":{Quote(e.Path ?? e.Metadata ?? "")}," +
                    $"\"type\":{Quote(type)}," +
                    $"\"rarity\":{Quote(rarity)}," +
                    $"\"dist\":{(int)dist}," +
                    $"\"alive\":{(e.IsAlive ? "true" : "false")}," +
                    $"\"hostile\":{(e.IsHostile ? "true" : "false")}" +
                    "}";
                found.Add((dist, json));
            }
        }
        catch { return "[]"; }

        found.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        var sb = new StringBuilder("[");
        for (int i = 0; i < found.Count && i < max; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(found[i].Json);
        }
        sb.Append(']');
        return sb.ToString();
    }

    // one-time sorted dump of every flag name + value, browse the full enum offline while building the map
    private void DumpAllFlagNames(IReadOnlyDictionary<QuestFlag, bool> flags)
    {
        try
        {
            var path = Path.Combine(ConfigDirectory, "quest-flags-all.txt");
            var names = new List<string>(flags.Count);
            foreach (var kv in flags) names.Add($"{(kv.Value ? "T" : ".")}  {kv.Key}");
            names.Sort(StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(path, names);
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> flag dump failed: {ex.Message}"); }
    }

    // deferred rescan, logs all transitions + rooms at this instant.
    // pass 1 (~1.5s): exits that spawn on boss death. pass 2 (~4s): late exits gated on loot/conversation.
    // nearRadius 400f covers arena-sized rooms where exit is far from the kill point.
    private void DoDelayedRescanIfDue(ref DateTime? timer, int pass)
    {
        if (timer == null || DateTime.Now < timer) return;
        timer = null;

        if (_areaId != _rescanAreaId) return;   // player changed zone, stale

        try
        {
            var transitions = TransitionsJson(nearRadius: 400f);
            var near = NearbyEntitiesJson(radius: 200f, max: 20);
            var rooms = RoomsJson();

            // all unique AreaTransition entries zone-wide (no waypoints, no near filter): forward-exit candidates
            var uniqueExits = UniqueExitsJson(transitions);

            var line = "{" +
                $"\"t\":\"{DateTime.Now:HH:mm:ss}\"," +
                $"\"type\":\"post_flag_rescan\"," +
                $"\"pass\":{pass}," +
                $"\"area\":{Quote(_rescanAreaId ?? _areaId)}," +
                $"\"act\":{_rescanAct}," +
                $"\"step\":{_rescanStep}," +
                $"\"unique_exits\":{uniqueExits}," +
                $"\"transitions\":{transitions}," +
                $"\"near\":{near}," +
                $"\"rooms\":{rooms}" +
                "}";
            File.AppendAllText(FlagLogPath, line + Environment.NewLine);
            LogMessage($"ExileCampaigns -> post-flag rescan pass={pass} (area {_areaId}): unique_exits={uniqueExits}");
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> rescan pass={pass} failed: {ex.Message}"); }
    }

    // filter the already-built transitions JSON to entries with unique=true and AreaTransition path (not Waypoint).
    // avoids re-scanning entities.
    private static string UniqueExitsJson(string transitionsJson)
    {
        // text scan instead of a JSON parse dependency on this hot path: match unique=true + AreaTransition
        if (string.IsNullOrEmpty(transitionsJson) || transitionsJson == "[]") return "[]";

        var found = new List<string>();
        // split on object boundaries, each entry is a {...} block
        int depth = 0, start = -1;
        for (int i = 0; i < transitionsJson.Length; i++)
        {
            if (transitionsJson[i] == '{') { if (depth++ == 0) start = i; }
            else if (transitionsJson[i] == '}')
            {
                if (--depth == 0 && start >= 0)
                {
                    var obj = transitionsJson.Substring(start, i - start + 1);
                    if (obj.Contains("\"unique\":true") && obj.Contains("AreaTransition"))
                        found.Add(obj);
                    start = -1;
                }
            }
        }
        return "[" + string.Join(",", found) + "]";
    }

    // all AreaTransition + Waypoint entities in the area, annotated with:
    //   unique       path appears exactly once in the zone (reliable target discriminator)
    //   near_player  within nearRadius grid units of the player at capture
    // unique=true AND near_player=true = best authored-target candidate (identifies one exit unambiguously).
    // unique=false needs path+direction or a unique room discriminator to resolve.
    private string TransitionsJson(float nearRadius = 150f)
    {
        int pgx = 0, pgy = 0;
        try
        {
            var pos = GameController?.Player?.GetComponent<ExileCore.PoEMemory.Components.Positioned>();
            if (pos != null) { pgx = pos.GridX; pgy = pos.GridY; }
        }
        catch { }

        var rows = new List<(string Path, string Name, int GridX, int GridY)>();
        try
        {
            var ents = GameController?.EntityListWrapper?.Entities;
            if (ents == null) return "[]";
            foreach (var e in ents)
            {
                if (e == null || !e.IsValid) continue;
                var path = e.Path ?? "";
                if (!path.Contains("AreaTransition") && !path.Contains("Waypoint")) continue;
                var pos = e.GetComponent<ExileCore.PoEMemory.Components.Positioned>();
                rows.Add((path, e.RenderName ?? "", pos?.GridX ?? 0, pos?.GridY ?? 0));
            }
        }
        catch { return "[]"; }

        // count path occurrences zone-wide
        var pathCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, _, _, _) in rows)
            pathCounts[path] = pathCounts.TryGetValue(path, out var c) ? c + 1 : 1;

        var items = new List<string>(rows.Count);
        foreach (var r in rows)
        {
            float dx = r.GridX - pgx, dy = r.GridY - pgy;
            bool near = (dx * dx + dy * dy) <= nearRadius * nearRadius;
            items.Add("{" +
                $"\"path\":{Quote(r.Path)}," +
                $"\"name\":{Quote(r.Name)}," +
                $"\"gridX\":{r.GridX}," +
                $"\"gridY\":{r.GridY}," +
                $"\"unique\":{(pathCounts[r.Path] == 1 ? "true" : "false")}," +
                $"\"near_player\":{(near ? "true" : "false")}" +
                "}");
        }
        return "[" + string.Join(",", items) + "]";
    }

    // all AreaGraph rooms for the zone, annotated with:
    //   count  how many rooms with this exact name exist in the zone
    //   near   player's current grid pos is inside this room's bounding box
    // "discriminators" = rooms with count=1 AND near=true: unique-per-zone tile names that identify the
    // kill location. use as Radar.ClusterTarget patterns or StepTargetResolver authored-target keys.
    private string RoomsJson()
    {
        const int TileToGrid = 23;

        int pgx = 0, pgy = 0;
        try
        {
            var pos = GameController?.Player?.GetComponent<ExileCore.PoEMemory.Components.Positioned>();
            if (pos != null) { pgx = pos.GridX; pgy = pos.GridY; }
        }
        catch { }

        // first pass: collect rooms, build name->count map
        var rows = new List<(string Name, int MinX, int MinY, int MaxX, int MaxY)>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (var graph in GameController?.IngameState?.Data?.AreaGraphs ?? [])
            {
                foreach (var room in graph.Rooms)
                {
                    var name = room.Name;
                    if (name == null) continue;
                    int x0 = room.MinCoord.X * TileToGrid, y0 = room.MinCoord.Y * TileToGrid;
                    int x1 = room.MaxCoord.X * TileToGrid, y1 = room.MaxCoord.Y * TileToGrid;
                    rows.Add((name, x0, y0, x1, y1));
                    counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
                }
            }
        }
        catch { return "{}"; }

        // second pass: annotate and collect discriminators
        var items = new List<string>(rows.Count);
        var discriminators = new List<string>();
        foreach (var (name, x0, y0, x1, y1) in rows)
        {
            int count = counts[name];
            bool near = pgx >= x0 && pgx <= x1 && pgy >= y0 && pgy <= y1;
            items.Add("{" +
                $"\"name\":{Quote(name)}," +
                $"\"count\":{count}," +
                $"\"near\":{(near ? "true" : "false")}," +
                $"\"minX\":{x0},\"minY\":{y0},\"maxX\":{x1},\"maxY\":{y1}" +
                "}");
            if (near && count == 1)
                discriminators.Add(Quote(name));
        }

        return "{" +
            $"\"discriminators\":[{string.Join(",", discriminators)}]," +
            $"\"all\":[{string.Join(",", items)}]" +
            "}";
    }

    private static string Quote(string s)
    {
        s ??= "";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    // --- editor live pickers ------------------------------------------------------------------------
    // nearby entities (alive or dead), nearest-first, distinct by metadata path, as (path, name, dist, alive).
    // feeds the editor's click-to-set objective entityPath / kill target list, plus the path/icon/indicator
    // target pickers. dead included so you can bind a just-killed boss as a kill target; ground items (drops)
    // included so you can point guidance at one.
    internal List<(string Path, string Name, int Dist, bool Alive)> NearbyEntitiesList(float radius = 200f, int max = 5)
    {
        var found = new List<(float Dist, string Path, string Name, bool Alive)>();
        try
        {
            var ents = GameController?.EntityListWrapper?.Entities;
            if (ents == null) return new();
            foreach (var e in ents)
            {
                if (e == null || !e.IsValid) continue;
                var type = e.Type.ToString();
                // interactables you might bind as an objective entityPath: mobs/npcs/chests plus on-ground
                // quest objects and ingame icons (summon-ally portals, obelisks), ground items (drops you
                // might point a path/icon/indicator at), and area transitions (zone exits) when loaded near you.
                // terrain only when targetable (doors/levers/runes), to keep scenery out.
                if (type is not ("Monster" or "Npc" or "Chest" or "IngameIcon" or "QuestObject" or "Terrain" or "WorldItem" or "AreaTransition")) continue;
                if (type == "Terrain" && !(e.GetComponent<Targetable>()?.isTargetable ?? false)) continue;
                var dist = e.DistancePlayer;
                if (dist > radius) continue;
                // ground items: name + base-type path live on the held item entity, not the WorldItem shell
                var named = type == "WorldItem" ? (e.GetComponent<WorldItem>()?.ItemEntity ?? e) : e;
                var path = named.Path ?? named.Metadata ?? "";
                if (string.IsNullOrEmpty(path)) continue;
                // RenderName is the "Empty" sentinel for unresolved items; blank it so display falls back to path leaf
                var name = named.RenderName ?? "";
                if (name == "Empty") name = "";
                found.Add((dist, path, name, e.IsAlive));
            }
        }
        catch { return new(); }

        found.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        var outp = new List<(string, string, int, bool)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in found)
        {
            if (!seen.Add(f.Path)) continue;
            outp.Add((f.Path, f.Name, (int)f.Dist, f.Alive));
            if (outp.Count >= max) break;
        }
        return outp;
    }

    // nearby AreaGraph room tile names: rooms whose box contains the player first, then nearest center.
    // distinct, capped. feeds the editor's click-to-set pathTarget list (Radar tile design names).
    internal List<string> NearbyRoomTilesList(int max = 5)
    {
        const int TileToGrid = 23;
        int pgx = 0, pgy = 0;
        try
        {
            var pos = GameController?.Player?.GetComponent<Positioned>();
            if (pos != null) { pgx = pos.GridX; pgy = pos.GridY; }
        }
        catch { }

        var scored = new List<(bool Near, float D, string Name)>();
        try
        {
            foreach (var graph in GameController?.IngameState?.Data?.AreaGraphs ?? [])
                foreach (var room in graph.Rooms)
                {
                    var name = room.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    int x0 = room.MinCoord.X * TileToGrid, y0 = room.MinCoord.Y * TileToGrid;
                    int x1 = room.MaxCoord.X * TileToGrid, y1 = room.MaxCoord.Y * TileToGrid;
                    bool near = pgx >= x0 && pgx <= x1 && pgy >= y0 && pgy <= y1;
                    float cx = (x0 + x1) / 2f, cy = (y0 + y1) / 2f, dx = cx - pgx, dy = cy - pgy;
                    scored.Add((near, dx * dx + dy * dy, name));
                }
        }
        catch { return new(); }

        return scored.OrderByDescending(s => s.Near).ThenBy(s => s.D)
                     .Select(s => s.Name).Distinct(StringComparer.Ordinal).Take(max).ToList();
    }

    // live Radar-pathable targets in the current area: AreaTransitions, IngameIcons, and anything carrying a
    // MinimapIcon. mirrors what tools/harvest-radar-targets.ps1 dumps offline, but in-process. feeds the
    // editor's click-to-set pathTarget / transitionTile / objective entityPath list.
    // Leaf = last path segment minus the @NN spawn-variant suffix (for entityPath substring match);
    // FullPath = e.Path (for pathTarget/transitionTile). nearest-first, distinct by FullPath.
    internal List<(string Leaf, string FullPath, string Name, int Dist, string Kind)> NearbyRadarTargetsList(int max = 30)
    {
        var found = new List<(float Dist, string Leaf, string FullPath, string Name, string Kind)>();
        try
        {
            var ents = GameController?.EntityListWrapper?.Entities;
            if (ents == null) return new();
            foreach (var e in ents)
            {
                if (e == null || !e.IsValid) continue;
                var type = e.Type;
                bool isTransition = type == EntityType.AreaTransition;
                bool isIcon = type == EntityType.IngameIcon;
                bool hasMmi = e.HasComponent<MinimapIcon>();
                if (!isTransition && !isIcon && !hasMmi) continue;
                var path = e.Path ?? e.Metadata ?? "";
                if (string.IsNullOrEmpty(path)) continue;
                var kind = isTransition ? "exit" : isIcon ? "icon" : "mmi";
                found.Add((e.DistancePlayer, LeafOf(path), path, e.RenderName ?? "", kind));
            }
        }
        catch { return new(); }

        found.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        var outp = new List<(string, string, string, int, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in found)
        {
            if (!seen.Add(f.FullPath)) continue;
            outp.Add((f.Leaf, f.FullPath, f.Name, (int)f.Dist, f.Kind));
            if (outp.Count >= max) break;
        }
        return outp;
    }

    // Radar's targets.json entries for the current area plus the global "*" block: zone-wide targets mapped to
    // the right kind (tile -> Tile/ClusterTarget, room-scoped -> Room, entity -> Entity), including far
    // AreaTransitions you aren't standing next to -- the live entity pickers can't surface those
    // (out-of-range exits are IsValid=false / unnamed). feeds the editor's click-to-set target picker.
    // parsing (kind mapping, strip "*", flatten Alternatives, merge the global block) is the pure Guide helper.
    internal List<Guide.RadarTargetsFile.Pick> RadarFileTargetsList()
    {
        try
        {
            var radarFile = Path.Combine(Path.GetDirectoryName(DirectoryFullName)!, "Radar", "targets.json");
            if (!File.Exists(radarFile)) return new();
            return Guide.RadarTargetsFile.ParseArea(File.ReadAllText(radarFile), _areaId).ToList();
        }
        catch { return new(); }
    }

    // last path segment minus a @NN spawn-variant suffix, e.g. ".../SummonAlly@3" -> "SummonAlly".
    private static string LeafOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var leaf = path.Substring(path.LastIndexOf('/') + 1);
        int at = leaf.IndexOf('@');
        return at >= 0 ? leaf.Substring(0, at) : leaf;
    }
}
