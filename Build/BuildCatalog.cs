using System;
using System.Collections.Generic;
using System.IO;
using ExileCore;
using Newtonsoft.Json.Linq;

namespace ExileCampaigns.Build;

// one pickable thing. RequiredLevel is a prefill hint only, so 0 is fine.
public sealed class CatalogItem
{
    public string Name { get; init; } = "";
    public string BaseType { get; init; } = "";
    public string ItemClass { get; init; } = "";
    public int RequiredLevel { get; init; }
    public bool IsGem { get; init; }
    public bool IsSupport { get; init; }

    // what the search box matches and renders
    public string Label => IsSupport ? $"{Name}  (support)"
        : IsGem ? $"{Name}  (gem)"
        : $"{Name}  ({BaseType})";
}

// pickable gems, uniques and plain bases. built once, lazily: BaseItemTypes is a few thousand entries.
public sealed class BuildCatalog
{
    public IReadOnlyList<CatalogItem> Items { get; }

    private BuildCatalog(List<CatalogItem> items) => Items = items;

    // equippable classes only. unfiltered, BaseItemTypes is mostly currency, maps and quest items.
    private static readonly HashSet<string> EquipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Helmet", "Body Armour", "Gloves", "Boots", "Shield", "Quiver",
        "Amulet", "Ring", "Belt",
        "Claw", "Dagger", "Rune Dagger", "Wand", "One Hand Sword", "Thrusting One Hand Sword",
        "One Hand Axe", "One Hand Mace", "Sceptre", "Bow", "Staff", "Warstaff",
        "Two Hand Sword", "Two Hand Axe", "Two Hand Mace", "Fishing Rod",
    };

    public static BuildCatalog Load(GameController gc, string dataDir, Action<string> logError)
    {
        var items = new List<CatalogItem>();
        var byBaseName = new Dictionary<string, BaseItemTypeInfo>(StringComparer.OrdinalIgnoreCase);

        // plain bases, and a reverse map so uniques can resolve their class from their base name
        try
        {
            foreach (var kv in gc.Files.BaseItemTypes.Contents)
            {
                var bit = kv.Value;
                if (bit == null || string.IsNullOrEmpty(bit.BaseName)) continue;
                if (!EquipClasses.Contains(bit.ClassName ?? "")) continue;

                byBaseName[bit.BaseName] = new BaseItemTypeInfo(bit.ClassName ?? "", bit.DropLevel);
                items.Add(new CatalogItem
                {
                    Name = bit.BaseName,
                    BaseType = bit.BaseName,
                    ItemClass = bit.ClassName ?? "",
                    RequiredLevel = bit.DropLevel,
                });
            }
        }
        catch (Exception ex) { logError($"build catalog: base items failed: {ex.Message}"); }

        // gems
        try
        {
            foreach (var dat in gc.Files.SkillGems.EntriesList)
            {
                var bit = dat?.ItemType;
                if (bit == null || string.IsNullOrEmpty(bit.BaseName)) continue;
                items.Add(new CatalogItem
                {
                    Name = bit.BaseName,
                    BaseType = bit.BaseName,
                    ItemClass = bit.ClassName ?? "",
                    RequiredLevel = bit.DropLevel,
                    IsGem = true,
                    IsSupport = dat!.IsSupportGem,
                });
            }
        }
        catch (Exception ex) { logError($"build catalog: gems failed: {ex.Message}"); }

        // uniques: names cannot be enumerated from memory, so they ship as data
        try
        {
            var path = Path.Combine(dataDir, "poe1", "uniques.json");
            if (File.Exists(path))
            {
                foreach (var tok in JArray.Parse(File.ReadAllText(path)))
                {
                    var name = (string?)tok["name"] ?? "";
                    var baseName = (string?)tok["base"] ?? "";
                    if (name.Length == 0 || baseName.Length == 0) continue;

                    byBaseName.TryGetValue(baseName, out var info);
                    int req = (int?)tok["req"] ?? 0;
                    if (req == 0) req = info?.DropLevel ?? 0;   // no explicit floor -> fall back to the base

                    items.Add(new CatalogItem
                    {
                        Name = name,
                        BaseType = baseName,
                        ItemClass = info?.ClassName ?? "",
                        RequiredLevel = req,
                    });
                }
            }
            else logError($"build catalog: {path} missing, uniques unavailable in the picker");
        }
        catch (Exception ex) { logError($"build catalog: uniques failed: {ex.Message}"); }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new BuildCatalog(items);
    }

    private sealed record BaseItemTypeInfo(string ClassName, int DropLevel);
}
