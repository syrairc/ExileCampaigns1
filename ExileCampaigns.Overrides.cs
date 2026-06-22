// ExileCampaigns/ExileCampaigns.Overrides.cs
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ExileCampaigns;

// globalRules bridge: reads the globalRules section of overrides.json for waypoint-pulse detection.
public partial class ExileCampaigns
{
    private string BundledOverridesPath => Path.Combine(DirectoryFullName, "Data", "poe2", "route", "overrides.json");
    private string UserOverridesPath => Path.Combine(ConfigDirectory, "route", "overrides.json");

    // flag -> rule name for global advance rules parsed from overrides.json globalRules array.
    // "waypointAdvance" -> call AdvanceCurrentStepIfMatch(IsTakeWaypointStep). loaded from bundled
    // overrides (user overrides may also supply entries; both are merged in LoadGlobalRules).
    private readonly List<(string Flag, string Rule)> _globalRules = new();

    // parse globalRules from one overrides.json file into _globalRules. kind=="waypoint" maps to
    // "waypointAdvance"; other kinds are recorded verbatim for future rules. call for bundled then
    // user so user entries append (and can override by shadowing the same flag).
    private void LoadGlobalRules(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var root = JObject.Parse(File.ReadAllText(path));
            if (root["globalRules"] is not JArray arr) return;
            foreach (var t in arr)
            {
                var flag = (string?)t["flag"];
                var kind = (string?)t["kind"] ?? "";
                if (string.IsNullOrEmpty(flag)) continue;
                // waypoint kind -> rule name used by UpdateWaypointPulse to detect wp takes.
                var rule = string.Equals(kind, "waypoint", System.StringComparison.OrdinalIgnoreCase)
                    ? "waypointAdvance" : kind;
                if (!string.IsNullOrEmpty(rule))
                    _globalRules.Add((flag, rule));
            }
        }
        catch (System.Exception ex) { LogError($"ExileCampaigns -> globalRules load failed ({path}): {ex.Message}"); }
    }

}
