using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using ExileCampaigns.Guide;
using ExileCore.PoEMemory.Components;
using System.Globalization;
using System.Linq;
using ExileCore.Shared.Enums;
using SharpDX;
using Newtonsoft.Json.Linq;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;
using RectangleF = SharpDX.RectangleF;

namespace ExileCampaigns;

// tester diagnostics: an opt-in rolling buffer of recent events plus a one-shot JSON export.
// recording is gated on Settings.Diagnostics.RecordDiagnostics so it costs nothing when off.
public partial class ExileCampaigns
{
    private readonly DiagBuffer _diag = new();

    // separate flag snapshot from the dev harvester's, so recording works without "Log quest flags" on.
    private HashSet<string>? _diagFlagSnapshot;
    private DateTime _lastDiagFlagPoll;

    private bool DiagRecording => Settings.Diagnostics.RecordDiagnostics.Value;

    // append one event with the current timestamp. innerJson is a field fragment with no outer braces.
    private void PushDiag(string kind, string innerJson)
    {
        try { _diag.Add(DateTime.Now, kind, innerJson); }
        catch { /* never break the game loop on a diagnostics write */ }
    }

    // from AreaChange after the area snapshot is captured.
    private void RecordDiagArea()
    {
        if (!DiagRecording) return;
        PushDiag("area_change",
            $"\"area\":{Quote(_areaId)}," +
            $"\"zone\":{Quote(_zoneName)}," +
            $"\"act\":{_act}," +
            $"\"town\":{(_isTown ? "true" : "false")}," +
            $"\"hideout\":{(_isHideout ? "true" : "false")}");
    }

    // from Tick when recording on. own snapshot + own throttle, independent of the dev harvester.
    private void RecordDiagFlagChanges()
    {
        if (!DiagRecording) return;
        if ((DateTime.Now - _lastDiagFlagPoll).TotalSeconds < 0.5) return;
        _lastDiagFlagPoll = DateTime.Now;

        var flags = ReadQuestFlags();
        if (flags == null || flags.Count == 0) return;

        var nowTrue = new HashSet<string>();
        foreach (var kv in flags)
            if (kv.Value) nowTrue.Add(kv.Key.ToString());

        if (_diagFlagSnapshot == null) { _diagFlagSnapshot = nowTrue; return; }   // seed silently

        var step = _route.CurrentStep?.DisplayText ?? "";
        foreach (var name in nowTrue)
        {
            if (_diagFlagSnapshot.Contains(name) || IsNoiseFlag(name)) continue;
            PushDiag("flag",
                $"\"flag\":{Quote(name)}," +
                $"\"area\":{Quote(_areaId)}," +
                $"\"step\":{_route.Current}," +
                $"\"text\":{Quote(step)}");
        }
        _diagFlagSnapshot = nowTrue;
    }

    // from UpdatePathTarget when a new grid target is chosen for the current step.
    private void RecordDiagPathTarget(Vector2 target)
    {
        if (!DiagRecording) return;
        var step = _route.CurrentStep?.DisplayText ?? "";
        PushDiag("path_target",
            $"\"area\":{Quote(_areaId)}," +
            $"\"step\":{_route.Current}," +
            $"\"text\":{Quote(step)}," +
            $"\"gridX\":{(int)target.X}," +
            $"\"gridY\":{(int)target.Y}");
    }

    // build one JSON report (meta + settings + recorded events + current snapshot) and open its folder.
    // hand-built JSON to match the Flags.cs style (no Newtonsoft on this path).
    private void ExportDiagnostics()
    {
        try
        {
            var dir = Path.Combine(ConfigDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"exile-diag-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"meta\":").Append(DiagMetaJson()).Append(',');
            sb.Append("\"settings\":").Append(DiagSettingsJson()).Append(',');
            sb.Append("\"events\":").Append(DiagEventsJson()).Append(',');
            sb.Append("\"snapshot\":").Append(DiagSnapshotJson());
            sb.Append('}');

            File.WriteAllText(path, sb.ToString());
            LogMessage($"ExileCampaigns -> diagnostics exported: {path}");
            ShowToast("Diagnostics exported", ToastLevel.Success);
            OpenInExplorer(path);
        }
        catch (Exception ex)
        {
            LogError($"ExileCampaigns -> diagnostics export failed: {ex.Message}");
            ShowToast("Diagnostics export failed (see log)", ToastLevel.Error);
        }
    }

    private string DiagMetaJson()
    {
        var ver = typeof(ExileCampaigns).Assembly.GetName().Version?.ToString() ?? "";
        string cls = "", league = "";
        try { cls = GameController?.Player?.RenderName ?? ""; } catch { }
        try { league = GameController?.IngameState?.ServerData?.League ?? ""; } catch { }
        return "{" +
            $"\"pluginVersion\":{Quote(ver)}," +
            $"\"exportedAt\":{Quote(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))}," +
            $"\"profile\":{Quote(ProfileMask.Mask(_charName))}," +   // masked: this file gets shared
            $"\"charClass\":{Quote(cls)}," +
            $"\"league\":{Quote(league)}," +
            $"\"area\":{Quote(_areaId)}," +
            $"\"zone\":{Quote(_zoneName)}," +
            $"\"act\":{_act}," +
            $"\"town\":{(_isTown ? "true" : "false")}," +
            $"\"hideout\":{(_isHideout ? "true" : "false")}," +
            $"\"level\":{_playerLevel}" +
            "}";
    }

    // curated relevant toggles, not the whole settings object.
    private string DiagSettingsJson() =>
        "{" +
        $"\"recordDiagnostics\":{(Settings.Diagnostics.RecordDiagnostics.Value ? "true" : "false")}," +
        $"\"lockOverlays\":{(Settings.LockOverlays.Value ? "true" : "false")}," +
        $"\"autoAdvance\":{(Settings.AutoAdvance.Value ? "true" : "false")}," +
        $"\"logQuestFlags\":{(Settings.LogQuestFlags.Value ? "true" : "false")}" +
        "}";

    // dump the rolling buffer oldest-first. each stored event's Json is the inner fragment.
    private string DiagEventsJson()
    {
        var items = _diag.Snapshot();
        var sb = new StringBuilder("[");
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('{')
              .Append($"\"t\":{Quote(items[i].Time.ToString("HH:mm:ss"))},")
              .Append($"\"kind\":{Quote(items[i].Kind)},")
              .Append(items[i].Json)
              .Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private string DiagSnapshotJson()
    {
        var step = _route.CurrentStep?.DisplayText ?? "";

        // current radar path target, if any.
        string pathJson = "null";
        if (_currentTarget is { } t)
            pathJson = "{" +
                $"\"gridX\":{(int)t.X},\"gridY\":{(int)t.Y}," +
                $"\"forStep\":{_lastStepForTarget}" +
                "}";

        // count true flags for a quick sanity number; cheap.
        int trueFlags = 0;
        try
        {
            var flags = ReadQuestFlags();
            if (flags != null) foreach (var kv in flags) if (kv.Value) trueFlags++;
        }
        catch { }

        return "{" +
            $"\"step\":{{\"index\":{_route.Current},\"text\":{Quote(step)}}}," +
            $"\"radarPath\":{pathJson}," +
            $"\"near\":{NearbyEntitiesJson(radius: 200f, max: 20)}," +
            $"\"rooms\":{RoomsJson()}," +
            $"\"transitions\":{TransitionsJson(nearRadius: 400f)}," +
            $"\"flags\":{{\"trueCount\":{trueFlags}}}" +
            "}";
    }

    // open the diagnostics folder with the new file selected. swallow failure (export already succeeded).
    private void OpenInExplorer(string filePath)
    {
        try { Process.Start("explorer.exe", $"/select,\"{filePath}\""); }
        catch (Exception ex) { LogError($"ExileCampaigns -> open diagnostics folder failed: {ex.Message}"); }
    }

    // Quest-flag harvester (dev tool). ServerData.QuestFlags: ~2240 booleans for the whole game (campaign
    // + legacy + tutorials/UI). Campaign flags read under names like "G1_2.../VisitedG1_2/WaterLevelLoweredSeen"
    // but per-step flag isn't guessable, so when LogQuestFlags is on we snapshot and log every newly-true flag
    // with its area+step. A play session then yields raw (flag -> step) pairs to curate into a static map.
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
            var pos = GameController?.Player?.GetComponent<Positioned>();
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
                var pos = e.GetComponent<Positioned>();
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
            var pos = GameController?.Player?.GetComponent<Positioned>();
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
                bool hasMmi = e.HasComponent<ExileCore.PoEMemory.Components.MinimapIcon>();
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
    internal List<RadarTargetsFile.Pick> RadarFileTargetsList()
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

    // Run timer (total + per-act splits), level/XP, current area, level-vs-area gap, route progress, xp/hour.
    private DateTime _runStart;
    private DateTime _actStart;
    private int _timerAct = -1;
    private readonly Dictionary<int, double> _actSeconds = new();

    private long _playerXp;
    private long[] _xpCurve = Array.Empty<long>();   // cumulative XP to reach each level; index 0 = level 1

    // trailing XP samples for the windowed xp/hour + time-to-level estimate
    private readonly List<(DateTime t, long xp)> _xpSamples = new();
    private DateTime _lastXpSample = DateTime.MinValue;

    // Load the bundled PoE1 level->cumulative-XP table (source: poedb.tw/us/Experience).
    private void LoadXpCurve()
    {
        try
        {
            var path = Path.Combine(DirectoryFullName, "Data", "poe1", "xp_curve.json");
            if (!File.Exists(path)) return;
            var arr = JObject.Parse(File.ReadAllText(path))["cumulative"] as JArray;
            if (arr != null) _xpCurve = arr.Select(t => (long)t).ToArray();
        }
        catch { /* curve optional; XP%% just won't show */ }
    }

    private void InitStats()
    {
        _runStart = DateTime.Now;
        _actStart = DateTime.Now;
        _timerAct = _act;
        _actSeconds.Clear();
        _xpSamples.Clear();
        _lastXpSample = DateTime.MinValue;
    }

    // Push an XP sample at most every couple seconds, drop anything older than the rate window.
    // Called each tick. Window keeps one sample just past the cutoff so the span covers the full duration.
    private void RecordXpSample()
    {
        if (_playerXp <= 0) return;
        var now = DateTime.Now;
        if ((now - _lastXpSample).TotalSeconds < 2) return;
        _lastXpSample = now;
        _xpSamples.Add((now, _playerXp));

        var cutoff = now - TimeSpan.FromMinutes(Math.Max(1, Settings.XpRateWindowMinutes.Value));
        int drop = 0;
        while (drop + 1 < _xpSamples.Count && _xpSamples[drop + 1].t < cutoff) drop++;
        if (drop > 0) _xpSamples.RemoveRange(0, drop);
    }

    // Bank the previous act's time and start timing the new act. Called from AreaChange.
    private void OnActChangedStats(int newAct)
    {
        if (newAct == _timerAct) return;
        if (_timerAct >= 0)
            _actSeconds[_timerAct] = _actSeconds.GetValueOrDefault(_timerAct) + (DateTime.Now - _actStart).TotalSeconds;
        _timerAct = newAct;
        _actStart = DateTime.Now;
    }

    // XP/stats overlay (redesigned 2a): run/act timers, level + XP bar, rate, ETA, and the XP-penalty
    // safe-zone axis. each block is user-toggleable; rows are dropped (not blanked) when their data isn't
    // ready or their toggle is off, so the panel stays as short as its content.
    private List<PanelLine> BuildCharStatsLines(CharStatsOverlayStyle s)
    {
        var lines = new List<PanelLine>();
        var tc = s.TextColor.Value;
        var muted = new Color(tc.R, tc.G, tc.B, (byte)170);

        // 1. header: RUN total (left) / ACT current-act split (right)
        if (s.ShowTimers.Value)
        {
            var total = (DateTime.Now - _runStart).TotalSeconds;
            var actCur = _actSeconds.GetValueOrDefault(_timerAct < 0 ? _act : _timerAct)
                         + (DateTime.Now - _actStart).TotalSeconds;
            lines.Add(new PanelLine($"RUN {Fmt(total)}", s.HeaderColor.Value, isHeader: true,
                right: $"ACT {_act} - {Fmt(actCur)}"));
        }

        lines.Add(new PanelLine("XP", muted, isSeparator: true));

        // 3. level + XP% into level (needs the curve and a valid level below max).
        bool haveCurve = _xpCurve.Length >= 100 && _playerLevel is >= 1 and < 100 && _playerXp > 0;
        long into = 0, need = 0;
        if (haveCurve)
        {
            long cur = _xpCurve[_playerLevel - 1], next = _xpCurve[_playerLevel];
            into = _playerXp - cur; need = next - cur;
        }
        bool haveXp = haveCurve && need > 0 && into >= 0;
        string lvlText = _playerLevel is >= 1 and < 100 ? $"LVL {_playerLevel} -> {_playerLevel + 1}" : $"LVL {_playerLevel}";
        lines.Add(new PanelLine(lvlText, tc, right: haveXp ? $"{100.0 * into / need:0.0}%" : null));

        // 4. XP bar
        if (s.ShowXpBar.Value && haveXp)
            lines.Add(PanelLine.Bar((float)(into / (double)need), 12f, BarTrack, AccentResting, AccentBright));

        // windowed xp/hour (needs >=30s of samples).
        double rate = 0; bool haveRate = false;
        if (_xpSamples.Count >= 2)
        {
            var first = _xpSamples[0];
            var last = _xpSamples[^1];
            double spanSec = (last.t - first.t).TotalSeconds;
            if (spanSec >= 30) { rate = (last.xp - first.xp) / (spanSec / 3600.0); haveRate = rate > 0; }
        }

        // 5. XP to go (left) + rate (right). when to-go is off but rate is on, rate gets its own row.
        string? rateRight = s.ShowXpRate.Value && haveRate ? FmtRate(rate) : null;
        if (s.ShowXpToGo.Value && haveXp)
            lines.Add(new PanelLine($"{need - into:N0} to go", muted, right: rateRight));
        else if (rateRight != null)
            lines.Add(new PanelLine("Rate", muted, right: rateRight));

        // 6. ETA to next level = remaining xp / windowed rate.
        if (s.ShowEta.Value && haveRate && haveXp)
            lines.Add(new PanelLine("ETA next lvl", muted, right: Fmt((need - into) / rate * 3600.0)));

        // 7. XP penalty: 0 = Bar (label + safe-zone axis + ticks), 1 = Text (label only), 2 = Off.
        int mode = s.PenaltyMode.Value;
        if (mode != 2 && _areaLevel > 0 && _playerLevel > 0)
        {
            int effDiff = EffectiveXpDiff(_playerLevel, _areaLevel);
            var (grade, gradeCol) = PenaltyGrade(_playerLevel, _areaLevel, effDiff);
            lines.Add(new PanelLine("XP Penalty", gradeCol, right: grade));
            if (mode == 0)
            {
                lines.Add(PanelLine.Axis(_playerLevel, _areaLevel, 14f));
                // area level + the safe-zone level range (full XP inside charLevel +/- safe).
                int safe = SafeZone(_playerLevel);
                int lo = Math.Max(1, _playerLevel - safe);
                int hi = _playerLevel + safe;
                lines.Add(new PanelLine($"Area Lvl {_areaLevel}", muted, right: $"Safe {lo}-{hi}"));
            }
        }

        return lines;
    }

    // penalty status text + colour, per the design handoff (green safe -> red at heavy penalty).
    private static (string text, Color color) PenaltyGrade(int charLevel, int areaLevel, int effDiff)
    {
        if (effDiff <= 0) return ("0%  safe", PenaltySafe);
        double mult = XpMultiplier(charLevel, effDiff);
        int pen = (int)Math.Round((1 - mult) * 100);
        string dir = areaLevel > charLevel ? "underleveled" : "overleveled";
        return ($"{pen}%  {dir}", LerpColor(PenaltySafe, PenaltyBad, Math.Clamp(pen / 60f, 0f, 1f)));
    }

    private static readonly Color PenaltySafe = new Color(111, 207, 122, 255);   // #6FCF7A
    private static readonly Color PenaltyBad  = new Color(224, 103, 103, 255);   // #E06767

    private static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Color((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t), (byte)(a.A + (b.A - a.A) * t));
    }

    // xp/hour for the detail row: 248k/h style.
    private static string FmtRate(double xph) =>
        xph >= 1_000_000 ? $"{xph / 1_000_000:0.0}M/h" : xph >= 1000 ? $"{xph / 1000:0}k/h" : $"{xph:0}/h";

    // XP safe zone + penalty, per poewiki "Experience". Campaign range only (player <95, area <70),
    // so the 95+ penalty and the >70 monster-level adjustment are intentionally left out.
    private static int SafeZone(int playerLevel) => 3 + playerLevel / 16;

    // levels of |player-area| gap beyond the safe zone; 0 means full XP.
    private static int EffectiveXpDiff(int playerLevel, int areaLevel) =>
        Math.Max(Math.Abs(playerLevel - areaLevel) - SafeZone(playerLevel), 0);

    // fraction of raw monster XP earned; 1.0 inside the safe zone, floored at 1%.
    private static double XpMultiplier(int playerLevel, int effDiff)
    {
        if (effDiff <= 0) return 1.0;
        double b = playerLevel + 5;
        return Math.Max(Math.Pow(b / (b + Math.Pow(effDiff, 2.5)), 1.5), 0.01);
    }

    private static string Fmt(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }
}
