using System;
using System.Collections.Generic;
using System.Linq;

namespace ExileCampaigns;

// waypoint-pulse detection: globalRules (from overrides.json) flag which quest flags signal a wp take.
public partial class ExileCampaigns
{
    // waypoint flags pulse true on any waypoint take; surface that to the progress tracker so an
    // ActivateWaypoint objective on the current step completes. no step movement here.
    private readonly HashSet<string> _wpFlagSnapshot = new();
    private bool _wpFirstPoll = true;
    private void UpdateWaypointPulse()
    {
        if (_globalRules.Count == 0) return;
        var flags = ReadQuestFlags();
        if (flags == null) return;
        var nowTrue = flags.Where(kv => kv.Value).Select(kv => kv.Key.ToString()).ToHashSet();
        if (_wpFirstPoll) { _wpFirstPoll = false; _wpFlagSnapshot.Clear(); _wpFlagSnapshot.UnionWith(nowTrue); return; }
        foreach (var name in nowTrue)
        {
            if (_wpFlagSnapshot.Contains(name)) continue;
            if (_globalRules.Any(r => string.Equals(r.Flag, name, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(r.Rule, "waypointAdvance", StringComparison.OrdinalIgnoreCase)))
                _progress.WaypointPulsed = true;
        }
        _wpFlagSnapshot.Clear(); _wpFlagSnapshot.UnionWith(nowTrue);
    }
}
