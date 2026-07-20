using System.Collections.Generic;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using ExileCampaigns.Build;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Enums;
using ImGuiNET;

namespace ExileCampaigns;

// corner markers on inventory items in the build, and outlines on quest rewards in the build. both route
// through BuildIndex, so a reward and an inventory item can never disagree about what is planned.
public partial class ExileCampaigns
{
    private Color IndicatorColor(BuildEntry e)
    {
        var s = Settings.BuildIndicators;
        if (e.Used) return s.UsedColor.Value;
        int delta = e.TargetLevel - _playerLevel;
        if (delta <= 0) return s.EquippableColor.Value;
        if (delta <= s.SoonWindow.Value) return s.SoonColor.Value;
        return s.LaterColor.Value;
    }

    // one gem in the bag can serve several planned copies: the soonest need wins
    private static BuildEntry? Best(IReadOnlyList<BuildMatch> matches)
    {
        BuildEntry? best = null;
        foreach (var m in matches)
        {
            if (m.Entry.Used) continue;
            if (best == null || m.Entry.TargetLevel < best.TargetLevel) best = m.Entry;
        }
        return best ?? (matches.Count > 0 ? matches[0].Entry : null);
    }

    private void DrawBuildIndicators()
    {
        if (!Settings.BuildIndicators.Enable || _build.Sets.Count == 0) return;
        if (Settings.BuildIndicators.MarkInventory) DrawInventoryMarkers();

        if (Settings.BuildIndicators.HighlightQuestRewards)
        {
            var rewards = GameController?.IngameState?.IngameUi?.QuestRewardWindow;
            if (rewards is { IsVisible: true })
            {
                DrawWindowItemHighlights(rewards);
                DrawWindowItemTooltip(rewards);
            }
        }

        if (Settings.BuildIndicators.HighlightVendorItems)
        {
            var vendor = GameController?.IngameState?.IngameUi?.PurchaseWindow;
            if (vendor is { IsVisible: true })
            {
                DrawWindowItemHighlights(vendor);
                DrawWindowItemTooltip(vendor);
            }
        }

        // stash: one path for gems tab + normal tabs, both render items as InventoryItem leaves under StashElement
        if (Settings.BuildIndicators.HighlightStashItems)
        {
            var stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (stash is { IsVisible: true })
            {
                DrawWindowItemHighlights(stash);
                DrawWindowItemTooltip(stash);
            }
        }
    }

    private void DrawInventoryMarkers()
    {
        try
        {
            var panel = GameController?.IngameState?.IngameUi?.InventoryPanel;
            if (panel is not { IsVisible: true }) return;

            var items = panel[InventoryIndex.PlayerInventory]?.VisibleInventoryItems;
            if (items == null) return;

            var dl = ImGui.GetForegroundDrawList();
            float sz = Settings.BuildIndicators.Size.Value;
            uint outline = U32(new Color(10, 10, 12, 220));   // SharpDX.Color is RGBA

            foreach (var item in items)
            {
                var e = item?.Item;
                if (e == null || e.Address == 0 || !e.IsValid) continue;

                var snap = ReadItemSnapshot(e);
                if (!snap.Valid) continue;
                var entry = Best(MatchBuild(snap));
                if (entry == null) continue;

                var rect = item!.GetClientRectCache;
                float right = rect.X + rect.Width;
                float top = rect.Y;

                // top-right corner triangle
                var p1 = new Vector2(right - sz, top);
                var p2 = new Vector2(right, top);
                var p3 = new Vector2(right, top + sz);
                dl.AddTriangleFilled(p1, p2, p3, U32(IndicatorColor(entry)));
                dl.AddTriangle(p1, p2, p3, outline, 1.5f);
            }
        }
        catch { /* inventory mid-teardown */ }
    }

    // outline in-build InventoryItem leaves under a window (quest reward, vendor, later stash). walk the
    // subtree for InventoryItem leaves and read their .Entity: GetPossibleRewards() hands back junk entities
    // (address 7) in this ExileAPI build. items not in the build are left alone, not dimmed: still readable.
    private void DrawWindowItemHighlights(Element window)
    {
        try
        {
            var dl = ImGui.GetForegroundDrawList();
            foreach (var element in WindowItemElements(window))
            {
                var snap = ReadItemSnapshot(element.Entity);
                if (!snap.Valid) continue;
                var entry = Best(MatchBuild(snap));
                if (entry == null) continue;

                var r = element.GetClientRect();
                var min = new Vector2(r.X - 2, r.Y - 2);
                var max = new Vector2(r.X + r.Width + 2, r.Y + r.Height + 2);
                dl.AddRect(min, max, U32(IndicatorColor(entry)), 0f, ImDrawFlags.None, 3f);
            }
        }
        catch { /* window mid-teardown */ }
    }

    // reward/vendor/stash icons sit several levels deep as InventoryItem elements; Element.Entity only reads
    // a real item on those. iterative walk, no dat lookup needed.
    private static IEnumerable<Element> WindowItemElements(Element root)
    {
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var el = stack.Pop();
            if (el == null || el.Address == 0) continue;
            if (el.Type == ElementType.InventoryItem) { yield return el; continue; }
            var kids = el.Children;
            if (kids == null) continue;
            foreach (var k in kids) stack.Push(k);
        }
    }

    // hovering a highlighted item shows which set(s) plan it and at what level. UIHover gives the exact item
    // under the cursor, so no rect hit-testing. one line per set: "usable Lv5  -  Leveling (1-20)".
    private void DrawWindowItemTooltip(Element window)
    {
        try
        {
            if (window is not { IsVisible: true }) return;

            var uiHover = GameController?.Game?.IngameState?.UIHover;
            if (uiHover == null || uiHover.Address == 0) return;
            var hovered = uiHover.AsObject<NormalInventoryItem>();
            if (hovered == null || hovered.Address == 0) return;

            var snap = ReadItemSnapshot(hovered.Item);
            if (!snap.Valid) return;
            var matches = MatchBuild(snap);
            if (matches.Count == 0) return;   // not in the build, so not highlighted either

            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Ascii(snap.Name));
            var seen = new HashSet<string>();
            foreach (var m in matches)
            {
                if (!seen.Add(m.Set.Id)) continue;   // supports feed several skills; collapse to one line per set
                int req = m.Entry.RequiredLevel > 0 ? m.Entry.RequiredLevel : snap.RequiredLevel;
                string flag = m.Entry.Used ? "  [have]" : m.Entry.Optional ? "  [optional]" : "";
                ImGui.TextUnformatted(Ascii($"usable Lv{req}  -  {m.Set.Name} ({m.Set.MinLevel}-{m.Set.MaxLevel}){flag}"));
            }
            ImGui.EndTooltip();
        }
        catch { /* hover/window mid-teardown */ }
    }
}
