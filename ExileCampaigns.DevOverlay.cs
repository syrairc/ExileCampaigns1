using System;
using System.Numerics;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace ExileCampaigns;

// Dev/authoring overlay: annotated layers on the large minimap to help author routes.
// All guarded on Settings.Dev.ShowDevOverlay (off by default, zero overhead in production).
//
// Layers (each toggled independently under Dev settings):
//   Room names    AreaGraph room boxes + name labels. these are what Radar's targets.json and
//                 Radar.ClusterTarget match against, so this shows the tile-pattern for a StepTargetResolver fallback.
//   Entity labels AreaTransition / Waypoint / boss entity paths at their grid positions. shows which
//                 entity path StepTargetResolver should match.
//   Target marker crosshair at the current step's resolved target grid coord (validates resolver).
public partial class ExileCampaigns
{
    private const int TileToGrid = 23;
    private const string TerrainPrefix = "Metadata/Terrain/";

    private void DrawDevOverlay()
    {
        // the editor's "Preview target" marker reuses this overlay's map projection, so allow the marker
        // through even when the dev overlay proper is off (room/entity layers stay gated on ShowDevOverlay).
        bool previewMarker = Settings.Editor.Enable && _editorPreviewTarget;
        if (!Settings.Dev.ShowDevOverlay && !previewMarker) return;

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

        if (Settings.Dev.ShowDevOverlay && Settings.Dev.ShowRoomNames)
            DrawDevRoomNames(playerGrid, playerHeight, mapCenter, mapScale);

        if (Settings.Dev.ShowDevOverlay && Settings.Dev.ShowEntityLabels)
            DrawDevEntityLabels(playerGrid, playerHeight, mapCenter, mapScale);

        // target marker is editor-preview only now (its own dev toggle was removed)
        if (previewMarker && _currentTarget.HasValue)
            DrawDevTargetMarker(_currentTarget.Value, playerGrid, playerHeight, mapCenter, mapScale);
    }

    // AreaGraph room outlines + name labels. names strip "Metadata/Terrain/" for readability.
    private void DrawDevRoomNames(Vector2 playerGrid, float playerHeight, Vector2 mapCenter, float mapScale)
    {
        try
        {
            foreach (var graph in GameController.IngameState.Data.AreaGraphs)
            {
                foreach (var room in graph.Rooms)
                {
                    var name = room.Name;
                    if (name == null) continue;

                    var minGrid = new Vector2(room.MinCoord.X * TileToGrid, room.MinCoord.Y * TileToGrid);
                    var maxGrid = new Vector2(room.MaxCoord.X * TileToGrid, room.MaxCoord.Y * TileToGrid);

                    var tl = DevMapPoint(minGrid,                              playerGrid, playerHeight, mapCenter, mapScale);
                    var tr = DevMapPoint(new Vector2(maxGrid.X, minGrid.Y),   playerGrid, playerHeight, mapCenter, mapScale);
                    var br = DevMapPoint(maxGrid,                              playerGrid, playerHeight, mapCenter, mapScale);
                    var bl = DevMapPoint(new Vector2(minGrid.X, maxGrid.Y),   playerGrid, playerHeight, mapCenter, mapScale);

                    // hide rooms tucked under an open side panel
                    if (PointInSidePanel(tl) || PointInSidePanel(tr) || PointInSidePanel(br) || PointInSidePanel(bl))
                        continue;

                    var lineColor = Color.YellowGreen;
                    Graphics.DrawLine(tl, tr, 1f, lineColor);
                    Graphics.DrawLine(tr, br, 1f, lineColor);
                    Graphics.DrawLine(br, bl, 1f, lineColor);
                    Graphics.DrawLine(bl, tl, 1f, lineColor);

                    var centerGrid = (minGrid + maxGrid) / 2f;
                    var centerScreen = DevMapPoint(centerGrid, playerGrid, playerHeight, mapCenter, mapScale);
                    var label = name.StartsWith(TerrainPrefix, StringComparison.Ordinal)
                        ? name[TerrainPrefix.Length..]
                        : name;
                    var sz = Graphics.MeasureText(label);
                    Graphics.DrawBox(
                        centerScreen - sz / 2f - new Vector2(2, 1),
                        centerScreen + sz / 2f + new Vector2(2, 1),
                        new Color(0, 0, 0, 180));
                    Graphics.DrawText(label, centerScreen - sz / 2f, Color.YellowGreen);
                }
            }
        }
        catch { /* AreaGraphs not ready */ }
    }

    // entity path labels: AreaTransitions (cyan), Waypoints (gold), boss Rare/Unique (orange-red).
    // paths shortened to last two segments; full path in quest-flag-harvest.jsonl if needed.
    private void DrawDevEntityLabels(Vector2 playerGrid, float playerHeight, Vector2 mapCenter, float mapScale)
    {
        var ents = GameController?.EntityListWrapper?.Entities;
        if (ents == null) return;

        foreach (var e in ents)
        {
            if (e == null || !e.IsValid) continue;
            var path = e.Path ?? "";
            bool isTransition = path.Contains("AreaTransition");
            bool isWaypoint   = path.Contains("Waypoint");
            var rarityStr = e.Rarity.ToString();
            bool isBoss = e.IsHostile && rarityStr is "Rare" or "Unique";
            if (!isTransition && !isWaypoint && !isBoss) continue;

            var pos = e.GetComponent<Positioned>();
            if (pos == null) continue;

            var entityGrid = new Vector2(pos.GridX, pos.GridY);
            var screen = DevMapPoint(entityGrid, playerGrid, playerHeight, mapCenter, mapScale);
            if (PointInSidePanel(screen)) continue;   // hide under an open side panel

            var label = DevShortPath(path);
            var color = isTransition ? Color.Cyan : isWaypoint ? Color.Gold : Color.OrangeRed;
            var sz = Graphics.MeasureText(label);
            Graphics.DrawBox(
                screen - new Vector2(sz.X / 2f + 2, sz.Y / 2f + 1),
                screen + new Vector2(sz.X / 2f + 2, sz.Y / 2f + 1),
                new Color(0, 0, 0, 180));
            Graphics.DrawText(label, screen - sz / 2f, color);
        }
    }

    // crosshair + label at the current step's resolved target grid coord
    private void DrawDevTargetMarker(Vector2 target, Vector2 playerGrid, float playerHeight, Vector2 mapCenter, float mapScale)
    {
        var screen = DevMapPoint(target, playerGrid, playerHeight, mapCenter, mapScale);
        if (PointInSidePanel(screen)) return;   // hide under an open side panel
        const float r = 7f;
        var c = Color.Lime;
        Graphics.DrawLine(screen + new Vector2(-r, 0), screen + new Vector2(r, 0), 2f, c);
        Graphics.DrawLine(screen + new Vector2(0, -r), screen + new Vector2(0, r), 2f, c);
        Graphics.DrawBox(screen - new Vector2(r, r), screen + new Vector2(r, r), new Color(0, 255, 0, 50));

        var label = $"step target ({(int)target.X},{(int)target.Y})";
        var sz = Graphics.MeasureText(label);
        Graphics.DrawBox(
            screen + new Vector2(-sz.X / 2f - 2, r + 1),
            screen + new Vector2(sz.X / 2f + 2, r + sz.Y + 1),
            new Color(0, 0, 0, 160));
        Graphics.DrawText(label, screen + new Vector2(-sz.X / 2f, r + 2f), c);
    }

    // project a grid pos to minimap screen coords, same iso math as DrawPathMinimap
    private Vector2 DevMapPoint(Vector2 gridPos, Vector2 playerGrid, float playerHeight, Vector2 mapCenter, float mapScale)
    {
        var hd = _heightData;
        float z = 0f;
        if (hd != null)
        {
            int gy = (int)gridPos.Y, gx = (int)gridPos.X;
            if (gy >= 0 && gy < hd.Length && gx >= 0 && gx < hd[gy].Length)
                z = hd[gy][gx];
        }
        return mapCenter + GridDeltaToMapDelta(gridPos - playerGrid, playerHeight + z, mapScale);
    }

    // last two path segments, enough to identify entity type without the full Metadata/... prefix
    private static string DevShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var parts = path.Split('/');
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : path;
    }
}
