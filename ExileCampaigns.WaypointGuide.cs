using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore.PoEMemory;
using ImGuiNET;
using SharpDX;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileCampaigns;

// Guides the player to a "Waypoint to X" step's destination on the open World Map panel.
// The waypoint icon nodes (texture .../InGame/9.dds) carry no area name in memory - only a hover
// tooltip - and the game lays them out in a STATIC per-area slot order (confirmed live), so a step's
// target areaId maps to a fixed slot index per act via a baked table (built once with the id overlay below).
//
// This file currently hosts the AUTHORING aid: label each visible node with its slot index + the SUSPECTED
// area name (WaypointNamesByAct once verified, else the act's WorldArea list in index order). Where the label
// matches the real area it confirms the slot; where it diverges it reveals an area the map omits as a node.
// The real destination indicator gets added once the areaId -> slot table is confirmed.
//
// Panel layout (verified live): WaypointsRoot only ever aliases ACT 1's icon holder. Acts 2-10 live in a
// nested Part -> Act tree (Part 2 nests deeper than Part 1), so the shown act's holder is found by icon
// texture (SearchVisibleIconHolder) rather than a fixed path. The viewed act is read off the panel tabs -
// selected tab = the child with IsVisible && IsActive - so acts 2-10 can be authored from town by flipping
// tabs, not only while standing in the act (ViewedActFromPanel).
public partial class ExileCampaigns
{
    // cache the derived act-ordered names so the ~2k-entry dict isn't walked every frame (dev overlay only).
    private int _wpNamesAct = -1;
    private List<string> _wpNames = new();

    // verified slot -> area names live in the pure Guide.WaypointSlots (shared with the destination resolver).

    private const string WpIconTex = "Art/Textures/Interface/2D/2DArt/UIImages/InGame/9.dds";

    // the shown act's waypoint icons sit under a per-act holder whose path differs by act (Part 2 nests
    // deeper than Part 1) and WaypointsRoot only ever aliases act 1. so find the holder generically: the
    // element with the most VISIBLE icon children is whichever act tab is open. null when nothing's shown.
    private Element? WaypointNodeHolder()
    {
        // act 1 keeps its verified holder (indices there are already baked); only that alias exists for it.
        var wp1 = Act1IconHolder();
        if (wp1 != null && HasVisibleIcon(wp1)) return wp1;
        return SearchVisibleIconHolder();
    }

    private Element? Act1IconHolder()
    {
        try
        {
            var wp = GameController.IngameState.IngameUi.WorldMap?.WaypointsRoot;
            if (wp == null || !wp.IsVisible) return null;
            return wp.GetChildAtIndex(0);
        }
        catch { return null; }
    }

    private static bool IsIcon(Element? c)
    {
        try { return c != null && c.IsVisible && string.Equals(c.TextureName, WpIconTex, StringComparison.Ordinal); }
        catch { return false; }
    }

    private static bool HasVisibleIcon(Element holder)
    {
        var kids = holder.Children;
        if (kids == null) return false;
        for (int i = 0; i < kids.Count; i++) if (IsIcon(kids[i])) return true;
        return false;
    }

    // DFS the world map subtree for the element with the most visible waypoint-icon children. only the open
    // act's icons read IsVisible, so parked acts score 0 and the shown act's holder wins. dev overlay only.
    private Element? SearchVisibleIconHolder()
    {
        Element? wm;
        try { wm = GameController.IngameState.IngameUi.WorldMap; } catch { return null; }
        if (wm == null || !wm.IsVisible) return null;

        Element? best = null;
        int bestCount = 0, guard = 0;
        var stack = new Stack<(Element el, int depth)>();
        stack.Push((wm, 0));
        while (stack.Count > 0 && guard++ < 4000)
        {
            var (el, depth) = stack.Pop();
            var kids = el?.Children;
            if (kids == null || depth > 16) continue;
            int iconCount = 0;
            for (int i = 0; i < kids.Count; i++)
            {
                var c = kids[i];
                if (c == null) continue;
                if (IsIcon(c)) iconCount++;
                else stack.Push((c, depth + 1));
            }
            if (iconCount > bestCount) { bestCount = iconCount; best = el; }
        }
        return bestCount > 0 ? best : null;
    }

    // the act's zones in WorldArea index order (what the world map slot order mostly follows): skip the
    // non-zone header rows (null id, Character Select, hideouts). positional guess only - verified by eye.
    private List<string> ActAreaNames(int act)
    {
        if (act == _wpNamesAct) return _wpNames;
        var names = new List<string>();
        try
        {
            var dict = GameController.Files.WorldAreas?.AreasIndexDictionary;
            if (dict != null)
                foreach (var kv in dict.OrderBy(k => k.Key))
                {
                    var a = kv.Value;
                    if (a == null || a.Act != act) continue;
                    var id = a.Id;
                    if (string.IsNullOrEmpty(id) || a.IsHideout || id.Contains("Hideout") || id == "CharacterSelect")
                        continue;
                    names.Add(string.IsNullOrEmpty(a.Name) ? id : a.Name);
                }
        }
        catch { }
        _wpNamesAct = act;
        _wpNames = names;
        return names;
    }

    // which act tab is open on the world map panel: the panel browses any unlocked act regardless of where
    // you stand, so authoring acts 2-10 keys off the VIEWED act. derived from the selected Part tab (0=Part1
    // acts 1-5, 1=Part2 acts 6-10, 2=Epilogue) plus the act's index within that part, walked up from the
    // holder. selection = the child with IsVisible && IsActive. returns 0 when unsure (caller falls back).
    private int ViewedActFromPanel(Element? holder)
    {
        if (holder == null) return 0;
        try
        {
            // WaypointsRoot aliases act 1 only, so its holder means act 1 no matter where you stand.
            // (compare by Address - ExileAPI hands back a fresh Element wrapper each access, so ReferenceEquals lies.)
            var a1 = Act1IconHolder();
            if (a1 != null && a1.Address == holder.Address) return 1;

            // Parts container sits at a stable path (3 tabs). its selected child gives the act base.
            var parts = GameController.IngameState.IngameUi.WorldMap?
                .GetChildAtIndex(2)?.GetChildAtIndex(0)?.GetChildAtIndex(1);
            int p = SelectedChildIndex(parts);
            int baseAct = p == 0 ? 1 : p == 1 ? 6 : -1;   // epilogue/unknown -> bail
            if (baseAct < 0) return 0;

            // the act index within the part: nearest ancestor of the holder that is a tab selector
            // (more than one child, exactly one shown). the holder sits under the shown child, so that
            // child's IndexInParent is the act index j.
            var el = holder;
            for (int hops = 0; el != null && hops < 20; hops++)
            {
                var parent = el.Parent;
                if (parent == null) break;
                if (parent.ChildCount > 1)
                {
                    var kids = parent.Children;
                    int vis = 0;
                    if (kids != null)
                        for (int i = 0; i < kids.Count; i++)
                            if (kids[i] is { IsVisible: true }) vis++;
                    if (vis == 1)
                    {
                        int idx = el.IndexInParent ?? -1;
                        if (idx >= 0) return baseAct + idx;
                    }
                }
                el = parent;
            }
        }
        catch { }
        return 0;
    }

    // index of the single shown child (IsVisible && IsActive), or -1.
    private static int SelectedChildIndex(Element? container)
    {
        var kids = container?.Children;
        if (kids == null) return -1;
        for (int i = 0; i < kids.Count; i++)
        {
            var c = kids[i];
            if (c != null && c.IsVisible && c.IsActive) return i;
        }
        return -1;
    }

    // authoring aid: draw each visible waypoint node's slot index + suspected area name, so slots can be
    // mapped by eye. gated on the dev overlay toggle. parked (undiscovered) nodes stack at one offscreen
    // point and read IsVisible=false, so they're skipped - they get mapped later once unlocked.
    private void DrawWaypointNodeIds()
    {
        if (!Settings.Dev.ShowDevOverlay) return;
        var holder = WaypointNodeHolder();
        var kids = holder?.Children;
        if (kids == null) return;

        // viewed act (map lets you browse any unlocked act's tab); fall back to the act you stand in.
        int act = ViewedActFromPanel(holder);
        if (act <= 0) act = GameController.Area?.CurrentArea?.Act ?? 0;
        var mapped = Guide.WaypointSlots.NamesByAct.TryGetValue(act, out var m) ? m : null;
        var names = act > 0 ? ActAreaNames(act) : new List<string>();

        // ImGui foreground list so the text scales - Graphics.DrawText's height arg is a no-op in this
        // ExileAPI build. ~70% of default, since a lot of node labels sit close and overlap at full size.
        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        float baseSize = ImGui.GetFontSize();
        if (baseSize <= 0) baseSize = 16f;
        const float scale = 0.7f;
        float textSize = baseSize * scale;
        uint fg = U32(Color.Yellow), bg = U32(new Color(0, 0, 0, 210));

        for (int i = 0; i < kids.Count; i++)
        {
            var node = kids[i];
            if (node == null || !node.IsVisible) continue;

            RectangleF rect;
            try { rect = node.GetClientRectCache; }   // top-left = the waypoint's anchor; W/H are junk (~1px)
            catch { continue; }
            if (rect.X <= 0 && rect.Y <= 0) continue;

            var name = mapped != null && i < mapped.Length ? mapped[i]
                     : i < names.Count ? names[i] : "?";
            var label = $"{i}  {name}";
            var sz = ImGui.CalcTextSize(label) * scale;
            var pos = new Vector2(rect.X, rect.Y - sz.Y - 3f);   // sit just above the icon
            dl.AddRectFilled(pos - new Vector2(3, 1), pos + sz + new Vector2(3, 1), bg);
            dl.AddText(font, textSize, pos, fg, label);
        }
    }

    // the current step's "Waypoint to X" destination as (act, slot), or null when this isn't a waypoint
    // step / the target area or its slot can't be resolved.
    private (int act, int slot)? CurrentWaypointTarget()
    {
        try
        {
            var model = _route.CurrentStep?.Model;
            if (model == null) return null;
            Guide.Objective? obj = null;
            foreach (var o in model.Objectives)
                if (o.Type == Guide.ObjectiveType.EnterArea && o.AreaTarget != null) { obj = o; break; }
            if (obj == null) return null;

            var area = GameController.Files.WorldAreas?.GetAreaByAreaId(obj.AreaTarget!.Value);
            if (area == null) return null;
            int slot = Guide.WaypointSlots.ResolveSlot(area.Act, area.Name ?? "");
            return slot < 0 ? null : (area.Act, slot);
        }
        catch { return null; }
    }

    // 0..1 pulse for the highlight (alpha + radius). wall-clock driven, no per-frame state.
    private static float WpPulse()
    {
        double t = (Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds * 3.0) + 1.0) / 2.0;
        return (float)t;
    }

    // user-facing: on a "Waypoint to X" step with the world map open, highlight the destination node,
    // or glow the correct Part/Act tab first when that tab isn't the one shown.
    private void DrawWaypointDestination()
    {
        if (!Settings.ShowWaypointHighlight) return;
        var target = CurrentWaypointTarget();
        if (target == null) return;

        var holder = WaypointNodeHolder();
        if (holder == null) return;   // map closed

        int targetPart = target.Value.act <= 5 ? 0 : 1;   // 0 = Part 1 (acts 1-5), 1 = Part 2 (acts 6-10)
        int viewedPart = SelectedChildIndex(PartsContainer());
        int viewedAct = ViewedActFromPanel(holder);

        var dl = ImGui.GetForegroundDrawList();
        float p = WpPulse();
        uint col = U32(new Color(255, 200, 60, (int)(120 + 135 * p)));   // gold, pulsing alpha

        var wm = GameController.IngameState.IngameUi.WorldMap;
        if (viewedPart != targetPart)
        {
            GlowTab(dl, wm?.GetPartButton(targetPart + 1), col, p);   // GetPartButton is 1-based (verify live)
            return;
        }
        if (viewedAct != target.Value.act)
        {
            GlowTab(dl, wm?.GetActButton(target.Value.act), col, p);
            return;
        }

        // right act shown -> ring the node
        var kids = holder.Children;
        if (kids == null || target.Value.slot >= kids.Count) return;
        var node = kids[target.Value.slot];
        if (node == null || !node.IsVisible) return;
        RectangleF rect;
        try { rect = node.GetClientRectCache; } catch { return; }
        if (rect.X <= 0 && rect.Y <= 0) return;

        // the waypoint art has no size, so rect x/y is its top-left anchor and the centre sits down-right of
        // it. the map zooms, so a fixed pixel offset drifts. the nearest visible neighbour's on-screen
        // distance tracks the zoom exactly, so express the offset + radius as fractions of it.
        var anchor = new Vector2(rect.X, rect.Y);
        float d = MedianNodeSpacing(kids);   // map-wide, so every waypoint's ring is sized the same
        var wo = Settings.WaypointOverlay;
        var center = anchor + new Vector2(wo.OffsetX * d, wo.OffsetY * d);
        float radius = (wo.Scale + 0.06f * p) * d;   // base fits the icon, pulse breathes outward
        dl.AddCircle(center, radius, col, 32, 2.5f);

        // step text to the right of the ring (vertically centred) so it's clear why it's flashing.
        var label = _route.CurrentStep?.Model?.Text;
        if (!string.IsNullOrEmpty(label))
        {
            var font = ImGui.GetFont();
            float baseSize = ImGui.GetFontSize();
            if (baseSize <= 0) baseSize = 16f;
            float ts = Math.Clamp(d * 0.5f, 11f, 40f);
            var tsz = ImGui.CalcTextSize(label) * (ts / baseSize);
            float rEdge = (wo.Scale + 0.06f) * d;   // outer edge at full pulse, so the text doesn't jiggle
            var tp = new Vector2(center.X + rEdge + 0.2f * d, center.Y - tsz.Y / 2f);
            dl.AddRectFilled(tp - new Vector2(4, 2), tp + tsz + new Vector2(4, 2), U32(new Color(0, 0, 0, 180)));
            dl.AddText(font, ts, tp, col, label);
        }
    }

    // glow a tab button rect (Part/Act) when it isn't the one selected.
    private static void GlowTab(ImDrawListPtr dl, Element? btn, uint col, float p)
    {
        if (btn == null) return;
        RectangleF r;
        try { r = btn.GetClientRectCache; } catch { return; }
        if (r.Width <= 1 || r.Height <= 1) return;
        float pad = 2f + 3f * p;
        dl.AddRect(new Vector2(r.X - pad, r.Y - pad),
                   new Vector2(r.X + r.Width + pad, r.Y + r.Height + pad),
                   col, 4f, ImDrawFlags.None, 3f);
    }

    // median nearest-neighbour distance across all visible nodes: one map-wide scale for the highlight, so
    // ring size + offset are identical for every waypoint (per-node nearest varies too much). tracks the
    // panel's on-screen scale. falls back to a default when there aren't enough nodes.
    private static float MedianNodeSpacing(System.Collections.Generic.IList<Element> kids)
    {
        var pts = new System.Collections.Generic.List<Vector2>();
        for (int i = 0; i < kids.Count; i++)
        {
            var o = kids[i];
            if (o == null || !o.IsVisible) continue;
            RectangleF r;
            try { r = o.GetClientRectCache; } catch { continue; }
            if (r.X <= 0 && r.Y <= 0) continue;
            pts.Add(new Vector2(r.X, r.Y));
        }
        if (pts.Count < 2) return 40f;

        var nn = new System.Collections.Generic.List<float>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            float best = float.MaxValue;
            for (int j = 0; j < pts.Count; j++)
            {
                if (i == j) continue;
                float dd = Vector2.Distance(pts[i], pts[j]);
                if (dd > 1f && dd < best) best = dd;
            }
            if (best < float.MaxValue) nn.Add(best);
        }
        if (nn.Count == 0) return 40f;
        nn.Sort();
        return nn[nn.Count / 2];
    }

    // the world map's Part-tab container (3 tabs), stable path. reused by the viewed-part check.
    private Element? PartsContainer()
    {
        try
        {
            return GameController.IngameState.IngameUi.WorldMap?
                .GetChildAtIndex(2)?.GetChildAtIndex(0)?.GetChildAtIndex(1);
        }
        catch { return null; }
    }
}
