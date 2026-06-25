using System;
using System.Collections.Generic;
using System.Globalization;
using SharpDX;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ExileCampaigns;

// Run timer (total + per-act splits), level/XP, current area, level-vs-area gap, route progress, xp/hour.
public partial class ExileCampaigns
{
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

    // Statistics overlay: run timer + act split, level, XP%, current area, level gap, route progress, xp/hour.
    private List<PanelLine> BuildCharStatsLines(OverlayStyle s)
    {
        var lines = new List<PanelLine>();

        var total = (DateTime.Now - _runStart).TotalSeconds;
        var actCur = _actSeconds.GetValueOrDefault(_timerAct < 0 ? _act : _timerAct)
                     + (DateTime.Now - _actStart).TotalSeconds;
        lines.Add(new PanelLine($"timer {Fmt(total)}   -   act {_act}: {Fmt(actCur)}", s.HeaderColor.Value));

        lines.Add(new PanelLine($"Lvl {_playerLevel}", s.TextColor.Value));

        // XP progress to next level (needs the curve and a valid level below max).
        if (_xpCurve.Length >= 100 && _playerLevel is >= 1 and < 100 && _playerXp > 0)
        {
            long cur = _xpCurve[_playerLevel - 1], next = _xpCurve[_playerLevel];
            long into = _playerXp - cur, need = next - cur;
            if (need > 0 && into >= 0)
                lines.Add(new PanelLine($"XP {100.0 * into / need:0.0}%  ->  Lvl {_playerLevel + 1}  ({need - into:N0} to go)",
                    s.TextColor.Value));
        }

        // current area (where you stand) + its level.
        if (!string.IsNullOrEmpty(_zoneName))
            lines.Add(new PanelLine(_areaLevel > 0 ? $"{_zoneName} - Lvl {_areaLevel}" : _zoneName, s.TextColor.Value));

        // level vs area: warn when under-leveled, else a dim on-level note.
        if (_areaLevel > 0 && _playerLevel > 0)
        {
            if (_playerLevel < _areaLevel)
                lines.Add(new PanelLine($"under-leveled (area +{_areaLevel - _playerLevel})", s.OptionalColor.Value));
            else
            {
                var tc = s.TextColor.Value;
                lines.Add(new PanelLine("on level", new Color(tc.R, tc.G, tc.B, (byte)170)));
            }
        }

        // route progress: act-relative step and overall percent.
        var cs = _route.CurrentStep;
        if (cs != null && _route.Steps.Count > 0)
        {
            int actTotal = _route.StepsInAct(cs.Act);
            int overall = (int)Math.Round(100.0 * (_route.Current + 1) / _route.Steps.Count);
            lines.Add(new PanelLine($"Act {cs.Act} - step {cs.StepInAct}/{actTotal}  ({overall}%)", s.TextColor.Value));
        }

        // xp/hour averaged over the trailing window, plus an ETA to the next level from that rate.
        // window needs >=30s of samples before the rate is meaningful.
        if (_xpSamples.Count >= 2)
        {
            var first = _xpSamples[0];
            var last = _xpSamples[^1];
            double spanSec = (last.t - first.t).TotalSeconds;
            if (spanSec >= 30)
            {
                double rate = (last.xp - first.xp) / (spanSec / 3600.0);
                lines.Add(new PanelLine($"{rate:N0} xp/h", s.TextColor.Value));

                // time to level: remaining xp in the current level / windowed rate.
                if (rate > 0 && _xpCurve.Length >= 100 && _playerLevel is >= 1 and < 100)
                {
                    long remaining = _xpCurve[_playerLevel] - _playerXp;
                    if (remaining > 0)
                        lines.Add(new PanelLine($"~{Fmt(remaining / rate * 3600.0)} to Lvl {_playerLevel + 1}", s.TextColor.Value));
                }
            }
        }

        return lines;
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
