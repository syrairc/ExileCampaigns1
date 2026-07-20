using System.Collections.Generic;
using System.Linq;
using SharpDX;
using ExileCampaigns.Build;

namespace ExileCampaigns;

// build overlay: from the active set, what you can equip now and what unlocks next. reads the build
// directly, ignoring route steps. used entries drop off.
public partial class ExileCampaigns
{
    // first set whose range covers the character, no pin. overlaps are first-wins by design: validating
    // ranges would buy a modal error in exchange for nothing.
    private BuildSet? LevelSet() =>
        _build.Sets.FirstOrDefault(s => _playerLevel >= s.MinLevel && _playerLevel <= s.MaxLevel);

    // pinned set wins (authoring aid, for editing a bracket you haven't reached yet), else the level set.
    private BuildSet? ActiveSet() => _build.FindSet(_build.ActiveSetOverrideId) ?? LevelSet();

    // dim a bucket colour so support gems read as sub-items of their skill, not peers
    private static Color Dim(Color c) => new Color(c.R, c.G, c.B, (byte)170);

    // optional keeps its own colour; supports get the dimmed bucket; actives full bright
    private Color RowColor(BuildEntry e, Color bucket, OverlayStyle s) =>
        e.Optional ? s.OptionalColor.Value : e.LinkedToId != null ? Dim(bucket) : bucket;

    // "Added Lightning Damage  [Kinetic Blast]" - the [skill] tag tells duplicate support names apart.
    // indent pushes supports in under their skill in the now/next list.
    private string EntryLabel(BuildEntry e, bool indent = false)
    {
        var linked = _build.FindEntry(e.LinkedToId);
        var head = linked != null ? $"{(indent ? "   " : "")}{e.Name}  [{linked.Name}]"
            : e.Kind == BuildItemKind.Gem ? $"{e.Name} (gem)" : e.Name;
        if (e.Optional) head += " (optional)";
        // note (pob socket-group label) is kept in the data + editor, just not shown in the overlay
        return head;
    }

    // ctrl-click an overlay row to mark it equipped/owned, same one-way flag the editor checkbox sets.
    private void MarkEntryHave(BuildEntry e)
    {
        e.Used = true;
        SaveBuild();
        ShowToast($"Marked {e.Name} as have", ToastLevel.Success);
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

        // prefix goes in the Num column, not baked into the text: keeps wrapped continuation rows aligned
        // under the label instead of falling back to the left edge.
        foreach (var e in now)
            lines.Add(new PanelLine(EntryLabel(e, indent: true), RowColor(e, green, s), num: "now",
                onCtrlClick: () => MarkEntryHave(e)));
        foreach (var e in next)
            lines.Add(new PanelLine(EntryLabel(e, indent: true), RowColor(e, yellow, s), num: $"Lvl {e.TargetLevel}",
                onCtrlClick: () => MarkEntryHave(e)));

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
