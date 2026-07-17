using System.Collections.Generic;
using System.Linq;
using SharpDX;
using ExileCampaigns.Build;

namespace ExileCampaigns;

// build overlay: from the active set, what you can equip now and what unlocks next. reads the build
// directly, ignoring route steps. used entries drop off.
public partial class ExileCampaigns
{
    // pinned set wins, else the first whose range covers the character. overlaps are first-wins by design:
    // validating ranges would buy a modal error in exchange for nothing.
    private BuildSet? ActiveSet()
    {
        var pinned = _build.FindSet(_build.ActiveSetOverrideId);
        if (pinned != null) return pinned;
        return _build.Sets.FirstOrDefault(s => _playerLevel >= s.MinLevel && _playerLevel <= s.MaxLevel);
    }

    // "Added Lightning Damage (+ Kinetic Blast)" - the suffix is what tells duplicate names apart
    private string EntryLabel(BuildEntry e)
    {
        var linked = _build.FindEntry(e.LinkedToId);
        if (linked != null) return $"{e.Name} (+ {linked.Name})";
        return e.Kind == BuildItemKind.Gem ? $"{e.Name} (gem)" : e.Name;
    }

    private List<PanelLine> BuildPanelLines(OverlayStyle s)
    {
        var set = ActiveSet();
        var header = set == null ? "Build" : $"Build - {set.Name}";
        var lines = new List<PanelLine> { new PanelLine(header, s.HeaderColor.Value, isHeader: true) };

        if (_build.Sets.Count == 0)
        {
            lines.Add(new PanelLine("  (no build sets - add one in the Build tab)", s.OptionalColor.Value));
            return lines;
        }
        if (set == null)
        {
            lines.Add(new PanelLine($"  (no set for level {_playerLevel})", s.OptionalColor.Value));
            return lines;
        }

        // SharpDX.Color is RGBA, not ARGB. see Global Constraints.
        var green = new Color(120, 210, 120, 255);
        var yellow = new Color(230, 200, 70, 255);
        int nextLevel = _playerLevel + 1;

        var pending = set.Entries.Where(e => !e.Used).OrderBy(e => e.TargetLevel).ToList();
        var now = pending.Where(e => e.TargetLevel <= _playerLevel).ToList();
        var next = pending.Where(e => e.TargetLevel == nextLevel).ToList();

        foreach (var e in now)
            lines.Add(new PanelLine($"  now   {EntryLabel(e)}", green));
        foreach (var e in next)
            lines.Add(new PanelLine($"  Lvl {e.TargetLevel}  {EntryLabel(e)}", yellow));

        // nothing actionable: hint the soonest upcoming so the panel still earns its space
        if (now.Count == 0 && next.Count == 0)
        {
            var upcoming = pending.FirstOrDefault(e => e.TargetLevel > _playerLevel);
            lines.Add(upcoming != null
                ? new PanelLine($"  next at Lvl {upcoming.TargetLevel}: {EntryLabel(upcoming)}", s.OptionalColor.Value)
                : new PanelLine("  (set complete)", s.OptionalColor.Value));
        }

        return lines;
    }
}
