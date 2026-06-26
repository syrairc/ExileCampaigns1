using System;
using System.Collections.Generic;

namespace ExileCampaigns.Guide;

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
