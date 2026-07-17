using System;
using System.Collections.Generic;
using System.Linq;
using ExileCampaigns.Build;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace ExileCampaigns;

// polls worn gear and its sockets ~1 Hz, sticky-marks matching build entries Used, persists on change.
// PoE1 gems live inside worn items, so one pass covers gear and gems both.
public partial class ExileCampaigns
{
    private readonly BuildIndex _buildIndex = new();
    private DateTime _lastUsedScan;

    // worn slots only. flasks and swap-only slots are not part of a build plan.
    private static readonly HashSet<InventoryNameE> EquipSlots = new()
    {
        InventoryNameE.BodyArmour1, InventoryNameE.Weapon1, InventoryNameE.Offhand1,
        InventoryNameE.Helm1, InventoryNameE.Amulet1, InventoryNameE.Ring1, InventoryNameE.Ring2,
        InventoryNameE.Gloves1, InventoryNameE.Boots1, InventoryNameE.Belt1,
        InventoryNameE.Weapon2, InventoryNameE.Offhand2,
    };

    private IReadOnlyList<BuildMatch> MatchBuild(in ItemSnapshot s) =>
        _buildIndex.Match(s.Name, s.BaseType, s.ItemClass);

    // claim the first unclaimed entry, so a spare copy of a gem still greys the right row out
    private static BuildEntry? ClaimUnused(IReadOnlyList<BuildMatch> matches, Func<BuildEntry, bool> extra)
    {
        foreach (var m in matches)
            if (!m.Entry.Used && extra(m.Entry)) return m.Entry;
        return null;
    }

    private void DetectBuildUsed()
    {
        if (_build.Sets.Count == 0) return;
        if ((DateTime.Now - _lastUsedScan).TotalSeconds < 1.0) return;
        _lastUsedScan = DateTime.Now;

        try
        {
            var holders = GameController?.IngameState?.ServerData?.PlayerInventories;
            if (holders == null) return;

            bool changed = false;
            foreach (var holder in holders)
            {
                if (holder == null || !EquipSlots.Contains(holder.TypeId)) continue;
                var items = holder.Inventory?.Items;
                if (items == null) continue;

                foreach (var e in items)
                {
                    if (e == null || e.Address == 0 || !e.IsValid) continue;
                    changed |= MarkWornItem(e);
                    changed |= MarkSocketedGems(e);
                }
            }

            if (changed) SaveProgress();
        }
        catch { /* server data not ready */ }
    }

    private bool MarkWornItem(Entity item)
    {
        var snap = ReadItemSnapshot(item);
        if (!snap.Valid || snap.IsGem) return false;

        var claim = ClaimUnused(MatchBuild(snap), e => e.Kind == BuildItemKind.Equipment);
        if (claim == null) return false;
        claim.Used = true;
        return true;
    }

    // gems socketed in this item, grouped by link group. a support only counts when it shares a group with
    // the skill its entry names: socketed-but-unlinked stays in the panel, which is the warning.
    private bool MarkSocketedGems(Entity item)
    {
        if (!item.TryGetComponent<Sockets>(out var sockets) || sockets?.SocketInfo == null) return false;

        var groups = new Dictionary<int, List<ItemSnapshot>>();
        foreach (var socket in sockets.SocketInfo)
        {
            var gem = socket?.SocketedGemEntity;
            if (gem == null || gem.Address == 0 || !gem.IsValid) continue;
            var snap = ReadItemSnapshot(gem);
            if (!snap.Valid || !snap.IsGem) continue;

            if (!groups.TryGetValue(socket!.LinkGroup, out var list))
                groups[socket.LinkGroup] = list = new List<ItemSnapshot>();
            list.Add(snap);
        }

        bool changed = false;
        foreach (var group in groups.Values)
        {
            var skills = group.Where(g => !g.IsSupport).ToList();

            foreach (var snap in group)
            {
                if (!snap.IsSupport)
                {
                    var claim = ClaimUnused(MatchBuild(snap), e => e.Kind == BuildItemKind.Gem && !e.IsSupport);
                    if (claim != null) { claim.Used = true; changed = true; }
                    continue;
                }

                // support: needs an entry whose LinkedToId names a skill sitting in this same link group
                var matches = MatchBuild(snap);
                BuildEntry? support = null;
                foreach (var skill in skills)
                {
                    var skillKey = BuildIndex.Normalize(skill.Name);
                    support = ClaimUnused(matches, e =>
                        e.IsSupport &&
                        BuildIndex.Normalize(_build.FindEntry(e.LinkedToId)?.Name) == skillKey);
                    if (support != null) break;
                }

                // unreachable from the editor, but hand-edited json and a future PoB import can leave a
                // support unlinked. an entry that can never grey out is worse than this fallback.
                support ??= ClaimUnused(matches, e => e.IsSupport && e.LinkedToId == null);

                if (support != null) { support.Used = true; changed = true; }
            }
        }

        return changed;
    }
}
