using System.Collections.Generic;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using ExileCampaigns.Build;
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
        if (Settings.BuildIndicators.HighlightQuestRewards) DrawQuestRewardHighlights();
    }

    private void DrawInventoryMarkers()
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

    // the reward window hands us (entity, element) pairs, so no dat lookup is needed. rewards that are not
    // in the build are left alone, not dimmed: you still want to read them.
    private void DrawQuestRewardHighlights()
    {
        try
        {
            var window = GameController?.IngameState?.IngameUi?.QuestRewardWindow;
            if (window is not { IsVisible: true }) return;

            var rewards = window.GetPossibleRewards();
            if (rewards == null) return;

            var dl = ImGui.GetForegroundDrawList();
            foreach (var (entity, element) in rewards)
            {
                if (element == null || element.Address == 0) continue;

                var snap = ReadItemSnapshot(entity);
                if (!snap.Valid) continue;
                var entry = Best(MatchBuild(snap));
                if (entry == null) continue;

                var r = element.GetClientRect();
                var min = new Vector2(r.X - 2, r.Y - 2);
                var max = new Vector2(r.X + r.Width + 2, r.Y + r.Height + 2);
                dl.AddRect(min, max, U32(IndicatorColor(entry)), 0f, ImDrawFlags.None, 3f);
            }
        }
        catch { /* reward window mid-teardown */ }
    }
}
