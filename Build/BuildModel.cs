using System;
using System.Collections.Generic;
using ExileCore.Shared.Enums;

namespace ExileCampaigns.Build;

public enum BuildItemKind { Equipment, Gem }

// one planned gem or item. name is not a unique key: the same support appears once per skill it feeds.
public sealed class BuildEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseType { get; set; } = "";
    public string ItemClass { get; set; } = "";
    public ItemRarity Rarity { get; set; } = ItemRarity.Normal;
    public BuildItemKind Kind { get; set; } = BuildItemKind.Equipment;
    public bool IsSupport { get; set; }
    public string? LinkedToId { get; set; }   // -> a gem entry in the same set. required for supports.
    public int TargetLevel { get; set; } = 1;
    public int RequiredLevel { get; set; }
    public string Note { get; set; } = "";
    public bool Used { get; set; }            // sticky once detected
    public DateTime CapturedAt { get; set; }
}

// one level bracket's whole loadout. gear carried across brackets is duplicated per set on purpose.
public sealed class BuildSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New set";
    public int MinLevel { get; set; } = 1;
    public int MaxLevel { get; set; } = 100;
    public List<BuildEntry> Entries { get; set; } = new();
}

// persisted inside the character profile json under the "build" key. Version lets later shapes migrate.
public sealed class BuildPlan
{
    public int Version { get; set; } = 1;
    public List<BuildSet> Sets { get; set; } = new();
    public string? ActiveSetOverrideId { get; set; }   // null = follow character level

    public BuildSet? FindSet(string? id) =>
        string.IsNullOrEmpty(id) ? null : Sets.Find(s => s.Id == id);

    // entry lookup across every set. used to resolve a support's LinkedToId to its skill.
    public BuildEntry? FindEntry(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var s in Sets)
        {
            var e = s.Entries.Find(x => x.Id == id);
            if (e != null) return e;
        }
        return null;
    }
}
