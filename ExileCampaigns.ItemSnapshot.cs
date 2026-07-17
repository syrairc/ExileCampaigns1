using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace ExileCampaigns;

// reads an item entity into a small comparable struct. every read is wrapped, so a stale or half-written
// item just yields Valid=false rather than throwing into the render loop.
public partial class ExileCampaigns
{
    internal readonly struct ItemSnapshot
    {
        public readonly string Name;
        public readonly string BaseType;
        public readonly string ItemClass;
        public readonly ItemRarity Rarity;
        public readonly int RequiredLevel;
        public readonly bool IsGem;
        public readonly bool IsSupport;
        public readonly int GemLevel;
        public readonly bool Valid;

        public ItemSnapshot(string name, string baseType, string itemClass, ItemRarity rarity,
            int requiredLevel, bool isGem, bool isSupport, int gemLevel)
        {
            Name = name;
            BaseType = baseType;
            ItemClass = itemClass;
            Rarity = rarity;
            RequiredLevel = requiredLevel;
            IsGem = isGem;
            IsSupport = isSupport;
            GemLevel = gemLevel;
            Valid = true;
        }
    }

    private ItemSnapshot ReadItemSnapshot(Entity? entity)
    {
        try
        {
            if (entity == null || entity.Address == 0 || !entity.IsValid) return default;

            var bit = GameController!.Files.BaseItemTypes.Translate(entity.Path);
            var baseType = bit?.BaseName ?? "";
            var itemClass = bit?.ClassName ?? "";

            // name preference: unique title -> Base.Name -> base type
            string name = baseType;
            int reqLvl = 0;
            var rarity = ItemRarity.Normal;
            if (entity.TryGetComponent<Mods>(out var mods) && mods != null)
            {
                reqLvl = mods.RequiredLevel;
                rarity = mods.ItemRarity;
                if (!string.IsNullOrEmpty(mods.UniqueName)) name = mods.UniqueName;
            }
            if (name == baseType && entity.TryGetComponent<Base>(out var b) && b != null
                && !string.IsNullOrEmpty(b.Name))
                name = b.Name;

            bool isGem = false, isSupport = false;
            int gemLevel = 0;
            if (entity.TryGetComponent<SkillGem>(out var gem) && gem != null)
            {
                isGem = true;
                gemLevel = gem.Level;
                if (gem.RequiredLevel > 0) reqLvl = gem.RequiredLevel;
                isSupport = gem.SkillGemDat?.IsSupportGem ?? false;
            }

            return new ItemSnapshot(name, baseType, itemClass, rarity, reqLvl, isGem, isSupport, gemLevel);
        }
        catch { return default; }
    }

    private ItemSnapshot TryCaptureHoveredItem()
    {
        try
        {
            var uiHover = GameController?.Game?.IngameState?.UIHover;
            if (uiHover == null || uiHover.Address == 0) return default;

            var icon = uiHover.AsObject<HoverItemIcon>();
            if (icon == null || icon.Address == 0) return default;
            if (icon.ToolTipType is ToolTipType.ItemInChat or ToolTipType.None) return default;

            var entity = uiHover.AsObject<NormalInventoryItem>()?.Item;
            return ReadItemSnapshot(entity);
        }
        catch { return default; }
    }
}
