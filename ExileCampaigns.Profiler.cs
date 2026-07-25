using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SharpDX;

namespace ExileCampaigns;

// in-process section profiler. every timed block is `using (Perf("name")) { ... }`. when the profiler is off
// the scope holds a null owner and Dispose returns immediately, so a disabled section costs one null check
// and no clock read. the overlay ranks sections by ms per SECOND (not per frame) so Tick and Render work are
// directly comparable regardless of how often each runs - 100 ms/s is 10% of one core.
//
// everything behind _perfLock: ExileAPI can run Tick off the render thread, and an unguarded Dictionary
// written from two threads corrupts (or spins forever) rather than just reporting a wrong number.
public partial class ExileCampaigns
{
    private sealed class PerfEntry
    {
        public string Name = "";
        public long Calls;
        public double TotalMs;        // since reset
        public double MaxMs;          // worst single call - the spike finder
        public double FrameMs;        // accumulating within the current Render frame
        public double WorstFrameMs;   // worst per-frame total (a section called many times in one frame)
    }

    // flat copy handed to the readers so they never touch live state
    private readonly record struct PerfRow(string Name, long Calls, double TotalMs, double MaxMs, double WorstFrameMs);

    private const string RenderTotal = "Render (total)";

    private readonly object _perfLock = new();
    private readonly Dictionary<string, PerfEntry> _perf = new();
    private readonly List<PerfEntry> _perfOrder = new();   // insertion order, so the frame reset is alloc-free
    private long _perfFrames;
    private long _perfResetAt = Environment.TickCount64;
    private long _perfFrameStart;
    private double _perfWorstFrameMs;
    private long _perfLastSpikeLog;

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    private bool PerfOn => Settings.Profiler.Enable.Value;

    private PerfScope Perf(string name) => new(PerfOn ? this : null, name);

    // struct + direct IDisposable, so `using` compiles to a constrained call with no boxing
    internal readonly struct PerfScope : IDisposable
    {
        private readonly ExileCampaigns? _p;
        private readonly string _name;
        private readonly long _t0;

        public PerfScope(ExileCampaigns? p, string name)
        {
            _p = p;
            _name = name;
            _t0 = p != null ? Stopwatch.GetTimestamp() : 0L;
        }

        public void Dispose()
        {
            if (_p == null) return;
            _p.PerfAddMs(_name, (Stopwatch.GetTimestamp() - _t0) * MsPerTick);
        }
    }

    private void PerfAddMs(string name, double ms)
    {
        lock (_perfLock)
        {
            if (!_perf.TryGetValue(name, out var e))
            {
                _perf[name] = e = new PerfEntry { Name = name };
                _perfOrder.Add(e);
            }
            e.Calls++;
            e.TotalMs += ms;
            e.FrameMs += ms;
            if (ms > e.MaxMs) e.MaxMs = ms;
        }
    }

    private void PerfBeginFrame()
    {
        lock (_perfLock)
        {
            _perfFrameStart = Stopwatch.GetTimestamp();
            foreach (var e in _perfOrder) e.FrameMs = 0;
        }
    }

    private void PerfEndFrame()
    {
        string? spike;
        lock (_perfLock)
        {
            double ms = (Stopwatch.GetTimestamp() - _perfFrameStart) * MsPerTick;
            _perfFrames++;
            if (ms > _perfWorstFrameMs) _perfWorstFrameMs = ms;

            // inline instead of PerfAddMs: already holding the lock, and Monitor re-entry is just noise here
            if (!_perf.TryGetValue(RenderTotal, out var rt))
            {
                _perf[RenderTotal] = rt = new PerfEntry { Name = RenderTotal };
                _perfOrder.Add(rt);
            }
            rt.Calls++;
            rt.TotalMs += ms;
            rt.FrameMs += ms;
            if (ms > rt.MaxMs) rt.MaxMs = ms;

            foreach (var e in _perfOrder)
                if (e.FrameMs > e.WorstFrameMs) e.WorstFrameMs = e.FrameMs;

            spike = BuildSpikeLine(ms);
        }
        // file write outside the lock: a Tick on another thread shouldn't block on our disk I/O
        if (spike != null)
            try { File.AppendAllText(PerfLogPath, spike + Environment.NewLine); } catch { }
    }

    private void PerfReset()
    {
        lock (_perfLock)
        {
            _perf.Clear();
            _perfOrder.Clear();
            _perfFrames = 0;
            _perfWorstFrameMs = 0;
            _perfResetAt = Environment.TickCount64;
        }
    }

    private double PerfElapsedSeconds => Math.Max(0.001, (Environment.TickCount64 - _perfResetAt) / 1000.0);

    // snapshot for the readers. RenderTotal is split out because it's the denominator, not a row.
    private (List<PerfRow> Rows, PerfRow? Render, long Frames, double WorstFrame) PerfSnapshot()
    {
        lock (_perfLock)
        {
            var rows = new List<PerfRow>(_perfOrder.Count);
            PerfRow? render = null;
            foreach (var e in _perfOrder)
            {
                var row = new PerfRow(e.Name, e.Calls, e.TotalMs, e.MaxMs, e.WorstFrameMs);
                if (e.Name == RenderTotal) render = row;
                else rows.Add(row);
            }
            rows.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
            return (rows, render, _perfFrames, _perfWorstFrameMs);
        }
    }

    // --- spike log ---------------------------------------------------------------------------------------
    // rolling averages are useless for a stall you only see once a zone. any Render frame over the threshold
    // dumps its own per-section breakdown, so the 400ms freeze names itself instead of being averaged away.
    private string PerfLogPath => Path.Combine(ConfigDirectory, "perf-spikes.jsonl");

    // caller must hold _perfLock. returns the line to append, or null when this frame isn't worth logging.
    private string? BuildSpikeLine(double frameMs)
    {
        int threshold = Settings.Profiler.SpikeLogMs.Value;
        if (threshold <= 0 || frameMs < threshold) return null;
        // a sustained bad state would otherwise log every frame and make itself worse
        if (Environment.TickCount64 - _perfLastSpikeLog < 250) return null;
        _perfLastSpikeLog = Environment.TickCount64;

        try
        {
            var hot = new List<PerfEntry>();
            foreach (var e in _perfOrder)
                if (e.Name != RenderTotal && e.FrameMs >= 0.2) hot.Add(e);
            hot.Sort((a, b) => b.FrameMs.CompareTo(a.FrameMs));

            var sb = new StringBuilder(256);
            sb.Append("{\"t\":\"").Append(DateTime.Now.ToString("HH:mm:ss.fff"))
              .Append("\",\"frameMs\":").Append(Num(frameMs))
              .Append(",\"area\":").Append(Quote(_areaId))
              .Append(",\"step\":").Append(_route.Current)
              .Append(",\"sections\":[");
            for (int i = 0; i < hot.Count && i < 8; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"n\":").Append(Quote(hot[i].Name))
                  .Append(",\"ms\":").Append(Num(hot[i].FrameMs)).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
        catch { return null; }   // never let the profiler break the frame it's measuring
    }

    private static string Num(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // full table to a text file, for pasting into a bug report. same ranking as the overlay but uncapped.
    private void PerfWriteReport()
    {
        try
        {
            var path = Path.Combine(ConfigDirectory, "perf-report.txt");
            double secs = PerfElapsedSeconds;
            var (rows, render, frames, worstFrame) = PerfSnapshot();

            var sb = new StringBuilder();
            sb.AppendLine($"ExileCampaigns perf - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"window {secs:0.0}s   render frames {frames}   worst frame {worstFrame:0.0} ms");
            sb.AppendLine($"area {_areaId}   step {_route.Current}   level {_playerLevel}");
            sb.AppendLine();
            sb.AppendLine($"{"section",-26}{"ms/s",9}{"max ms",10}{"worst/frame",13}{"calls",10}");
            if (render is { } r)
                sb.AppendLine($"{r.Name,-26}{r.TotalMs / secs,9:0.00}{r.MaxMs,10:0.0}{r.WorstFrameMs,13:0.0}{r.Calls,10}");
            foreach (var e in rows)
                sb.AppendLine($"{e.Name,-26}{e.TotalMs / secs,9:0.00}{e.MaxMs,10:0.0}{e.WorstFrameMs,13:0.0}{e.Calls,10}");

            File.WriteAllText(path, sb.ToString());
            ShowToast("Perf report written", ToastLevel.Success);
            LogMessage($"ExileCampaigns -> perf report: {path}");
        }
        catch (Exception ex)
        {
            LogError($"ExileCampaigns -> perf report failed: {ex.Message}");
            ShowToast("Perf report failed (see log)", ToastLevel.Error);
        }
    }

    // --- overlay -----------------------------------------------------------------------------------------

    private static readonly Color PerfHot  = new Color(224, 103, 103, 255);   // >= 20 ms/s
    private static readonly Color PerfWarm = new Color(224, 177, 90, 255);    // >= 5 ms/s
    private static readonly Color PerfCalm = new Color(135, 146, 168, 255);

    private List<PanelLine> BuildProfilerLines(ProfilerSettings s)
    {
        var lines = new List<PanelLine>();
        if (!s.Enable || !s.ShowOverlay) return lines;

        double secs = PerfElapsedSeconds;
        var (rows, render, frames, worstFrame) = PerfSnapshot();

        lines.Add(new PanelLine("PROFILER", s.HeaderColor.Value, isHeader: true,
            right: $"{(render?.TotalMs ?? 0) / secs:0.0} ms/s render"));
        lines.Add(new PanelLine($"{frames} frames / {secs:0}s", PerfCalm,
            right: $"worst frame {worstFrame:0.0} ms"));
        lines.Add(new PanelLine("ms/s", PerfCalm, right: "max ms / calls"));

        if (render is { } r)
            lines.Add(Row(r.Name, r.TotalMs / secs, r.MaxMs, r.Calls));

        double renderSections = 0;
        int max = s.MaxRows.Value;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Name.StartsWith("Render.", StringComparison.Ordinal)) renderSections += rows[i].TotalMs;
            if (i < max) lines.Add(Row("  " + rows[i].Name, rows[i].TotalMs / secs, rows[i].MaxMs, rows[i].Calls));
        }

        // whatever Render spent outside our named sections: framework overhead, or a block we forgot to time
        if (render is { } rr)
            lines.Add(new PanelLine("  (render unaccounted)", PerfCalm,
                num: $"{Math.Max(0, rr.TotalMs - renderSections) / secs:0.00}"));

        if (rows.Count > max)
            lines.Add(new PanelLine($"  (+{rows.Count - max} more)", PerfCalm));

        return lines;

        PanelLine Row(string name, double msPerSec, double maxMs, long calls) =>
            new(name, msPerSec >= 20 ? PerfHot : msPerSec >= 5 ? PerfWarm : s.TextColor.Value,
                num: $"{msPerSec:0.00}", right: $"{maxMs:0.0} / {calls}");
    }
}
