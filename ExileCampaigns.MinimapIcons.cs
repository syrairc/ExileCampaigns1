using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCampaigns.Guide;
using ExileCampaigns.Rendering;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ImGuiNET;
using SharpDX;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileCampaigns;

// draws authored MinimapIcons on the large map for every step in the current area.
// anchors are resolved once and cached per area instance; entity anchors are resolved live each frame.
public partial class ExileCampaigns
{
    private string? _mmiCacheKey;
    // Size is the per-icon override (null = global default), applied at draw so the global slider stays live;
    // StepId is the owning step, so the renderer can pulse the current objective's icons each frame.
    private readonly List<(Vector2 Coord, SpriteIcon Icon, uint Tint, float? Size, string StepId)> _mmiStatic = new();

    private void DrawMinimapIcons()
    {
        if (!Settings.MinimapIcons.Enable || _routeStore == null) return;

        SubMap? largeMap = null;
        try { largeMap = GameController.Game.IngameState.IngameUi.Map?.LargeMap?.AsObject<SubMap>(); }
        catch { }
        if (largeMap is not { IsVisible: true }) return;

        var playerPos = GameController.Player?.GetComponent<Positioned>();
        var playerRender = GameController.Player?.GetComponent<Render>();
        if (playerPos == null || playerRender == null) return;

        var playerGrid = new Vector2(playerPos.GridX, playerPos.GridY);
        var playerHeight = -playerRender.UnclampedHeight;
        var mapCenter = largeMap.MapCenter;
        var mapScale = largeMap.MapScale;
        float globalSize = Settings.MinimapIcons.IconSize.Value;   // per-icon Size overrides this when set

        var specs = GuidanceView.MinimapIconsForArea(_routeStore.Steps, _areaId);
        var currentId = _route.CurrentStep?.Model?.Id;   // icons of this step pulse

        var areaKey = AreaInstanceKey();
        if (areaKey != _mmiCacheKey)
        {
            _mmiStatic.Clear();
            foreach (var s in specs)
            {
                if (s.Anchor.Kind == TargetKind.Entity) continue;
                if (_targetResolver.ResolveTargetCoord(GameController, s.Anchor, _radarClusterTarget) is { } c)
                    _mmiStatic.Add((c, SpriteAtlas.Parse(s.IconKey), s.Tint, s.Size, s.StepId));
            }
            _mmiCacheKey = areaKey;
        }

        foreach (var (coord, icon, tint, size, stepId) in _mmiStatic)
        {
            var (ds, dt) = Pulse(stepId == currentId, size ?? globalSize, tint);
            DrawMinimapIcon(coord, icon, dt, ds, playerGrid, playerHeight, mapCenter, mapScale);
        }

        foreach (var s in specs)
        {
            if (s.Anchor.Kind != TargetKind.Entity) continue;
            // an entity target can match several live entities (distinct drops / duplicate objects) -- icon each
            var icon = SpriteAtlas.Parse(s.IconKey);
            var (ds, dt) = Pulse(s.StepId == currentId, s.Size ?? globalSize, s.Tint);
            foreach (var c in _targetResolver.ResolveEntityCoords(GameController, s.Anchor))
                DrawMinimapIcon(c, icon, dt, ds, playerGrid, playerHeight, mapCenter, mapScale);
        }
    }

    // pulse the current objective's icons: a gentle scale + alpha breathe so they read as "active". returns
    // the (size, tint) to draw with; identity when the icon isn't current or pulsing is disabled.
    private (float Size, uint Tint) Pulse(bool current, float size, uint tint)
    {
        if (!current || !Settings.MinimapIcons.PulseCurrent) return (size, tint);
        float s = MathF.Sin((float)ImGui.GetTime() * 4.5f);       // ~0.7 Hz, -1..1
        float scale = 1f + 0.20f * s;                             // 0.80 .. 1.20
        float alphaMul = 0.6f + 0.4f * (0.5f + 0.5f * s);         // 0.60 .. 1.00
        uint a = (uint)Math.Clamp((int)(((tint >> 24) & 0xFF) * alphaMul), 0, 255);
        return (size * scale, (tint & 0x00FFFFFFu) | (a << 24));
    }

    private void DrawMinimapIcon(Vector2 gridCoord, SpriteIcon icon, uint tint, float size,
        Vector2 playerGrid, float playerHeight, Vector2 mapCenter, float mapScale)
    {
        float z = 0f;
        var hd = _heightData;
        if (hd != null)
        {
            int gy = (int)gridCoord.Y, gx = (int)gridCoord.X;
            if (gy >= 0 && gy < hd.Length && gx >= 0 && gx < hd[gy].Length) z = hd[gy][gx];
        }
        var screen = mapCenter + GridDeltaToMapDelta(gridCoord - playerGrid, playerHeight + z, mapScale);
        float half = size / 2f;
        var rect = new RectangleF(screen.X - half, screen.Y - half, size, size);
        Graphics.DrawImage(IndicatorTexture, rect, SpriteAtlas.GetUVRect(icon), TintColor(tint));
    }

    // packed ARGB (0xAARRGGBB) -> SharpDX.Color.
    private static Color TintColor(uint argb) =>
        new Color((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    // area-instance identity so revisiting the same area id rebuilds the static cache.
    private string AreaInstanceKey()
    {
        try { return GameController?.Area?.CurrentArea?.Hash.ToString() ?? ""; }
        catch { return ""; }
    }
}
