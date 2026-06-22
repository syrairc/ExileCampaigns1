// ExileCampaigns/Guide/ObjectiveTextCodec.cs
using System;
using System.Collections.Generic;

namespace ExileCampaigns.Guide;

// bridges the editor's plain-text buffers <-> ObjectiveMeta. rooms and progressFlags edit as one-entry-
// per-line text. pure System.* so it stays in Guide/ and is unit-testable.
public static class ObjectiveTextCodec
{
    // build an ObjectiveMeta from raw editor text; null when nothing meaningful was entered so the
    // caller can prune the annotate (matches IsAnnotateEmpty).
    public static ObjectiveMeta? Parse(string label, string roomsText, int count,
                                       string progressText, string entityPath)
    {
        var lbl = Trimmed(label);
        var ep = Trimmed(entityPath);
        var rooms = SplitLines(roomsText);
        var progress = SplitLines(progressText);
        if (count < 0) count = 0;

        if (lbl == null && ep == null && rooms == null && progress == null && count == 0)
            return null;

        return new ObjectiveMeta(lbl, rooms, count, progress, ep);
    }

    // inverse of Parse: turn a stored objective back into buffer-ready strings.
    public static (string Label, string Rooms, int Count, string Progress, string EntityPath)
        Format(ObjectiveMeta? obj)
    {
        if (obj == null) return ("", "", 0, "", "");
        return (
            obj.Label ?? "",
            Join(obj.Rooms),
            obj.Count,
            Join(obj.ProgressFlags),
            obj.EntityPath ?? "");
    }

    private static string? Trimmed(string? s)
    {
        if (s == null) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    // split on newlines, trim each, drop blanks; null when nothing is left.
    private static IReadOnlyList<string>? SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var outp = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) outp.Add(line);
        }
        return outp.Count == 0 ? null : outp;
    }

    private static string Join(IReadOnlyList<string>? items)
        => items == null ? "" : string.Join("\n", items);
}
