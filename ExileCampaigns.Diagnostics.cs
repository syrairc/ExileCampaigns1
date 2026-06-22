using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using ExileCampaigns.Guide;
using ExileCore.PoEMemory.Components;

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
}
