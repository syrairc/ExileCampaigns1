// SpriteAtlas.cs - UV mapping for textures/Icons_Desaturated.png.
// atlas 1024 x 768 px, 32-bit RGBA, transparent bg. grid 8 cols x 6 rows = 48 cells, each 128 x 128 px.
// icons are desaturated (grayscale) with baked 3D shading: under a multiply blend the white highlights
// take the RGBA tint while the dark shading survives.

using System.Numerics;
using RectangleF = SharpDX.RectangleF; // ExileCore DrawImage overload uses SharpDX.RectangleF

namespace ExileCampaigns.Rendering;

/// <summary>Index of each icon in Icons_Desaturated.png (row-major, 0-based).</summary>
public enum SpriteIcon
{
    // Row 0 : filled solid shapes
    Circle = 0, Square = 1, TriangleUp = 2, TriangleDown = 3,
    Diamond = 4, Hexagon = 5, Pentagon = 6, Octagon = 7,
    // Row 1 : filled stars / specials
    Star5 = 8, Star6 = 9, Star4 = 10, Star8 = 11,
    Crescent = 12, Teardrop = 13, Kite = 14, Shield = 15,
    // Row 2 : marks / abstract
    Heart = 16, Plus = 17, Cross = 18, Chevron = 19,
    Arrow = 20, Dot = 21, Flower4 = 22, Flower6 = 23,
    // Row 3 : abstract / geometric
    Pinwheel = 24, Trapezoid = 25, Dome = 26, Pill = 27,
    DiamondCluster = 28, Target = 29, Burst12 = 30, Exclamation = 31,
    // Row 4 : outline shapes
    CircleOutline = 32, SquareOutline = 33, TriangleOutline = 34, DiamondOutline = 35,
    HexagonOutline = 36, PentagonOutline = 37, OctagonOutline = 38, Star5Outline = 39,
    // Row 5 : outline specials
    Star6Outline = 40, Star4Outline = 41, CrescentOutline = 42, TeardropOutline = 43,
    KiteOutline = 44, ShieldOutline = 45, HeartOutline = 46, RingThin = 47,
}

/// <summary>UV / source-rect helpers for the desaturated icon atlas.</summary>
public static class SpriteAtlas
{
    public const int AtlasWidth = 1024;
    public const int AtlasHeight = 768;
    public const int CellSize = 128;
    public const int Columns = 8;

    public const string FileName = "Icons_Desaturated.png";

    /// <summary>SpriteIcon for a stored name (case-insensitive). Unknown / empty -> Exclamation.</summary>
    public static SpriteIcon Parse(string? key) =>
        System.Enum.TryParse<SpriteIcon>(key, true, out var v) ? v : SpriteIcon.Exclamation;

    /// <summary>Top-left pixel of the icon's cell.</summary>
    private static (int X, int Y) GetCell(SpriteIcon icon)
    {
        int i = (int)icon;
        return ((i % Columns) * CellSize, (i / Columns) * CellSize);
    }

    /// <summary>Normalised UVs as (Uv0 top-left, Uv1 bottom-right) corner pair.</summary>
    public static (Vector2 Uv0, Vector2 Uv1) GetUVPair(SpriteIcon icon)
    {
        var (x, y) = GetCell(icon);
        var uv0 = new Vector2((float)x / AtlasWidth, (float)y / AtlasHeight);
        var uv1 = new Vector2((float)(x + CellSize) / AtlasWidth, (float)(y + CellSize) / AtlasHeight);
        return (uv0, uv1);
    }

    /// <summary>V-flipped corner pair (swaps V) so an upward sprite renders pointing down.</summary>
    public static (Vector2 TopLeftUv, Vector2 BottomRightUv) GetUVPairFlippedV(SpriteIcon icon)
    {
        var (uv0, uv1) = GetUVPair(icon);
        return (new Vector2(uv0.X, uv1.Y), new Vector2(uv1.X, uv0.Y));
    }

    /// <summary>Normalised UV rectangle (x, y, w, h) for the Graphics.DrawImage(..., RectangleF uv, ...) overload.</summary>
    public static RectangleF GetUVRect(SpriteIcon icon)
    {
        var (uv0, uv1) = GetUVPair(icon);
        return new RectangleF(uv0.X, uv0.Y, uv1.X - uv0.X, uv1.Y - uv0.Y);
    }

    /// <summary>V-flipped UV rect (negative height) so an upward sprite renders pointing down.
    /// DrawImage forwards uv to ImGui's AddImage, which flips when uv1.Y &lt; uv0.Y.</summary>
    public static RectangleF GetUVRectFlippedV(SpriteIcon icon)
    {
        var (uv0, uv1) = GetUVPair(icon);
        return new RectangleF(uv0.X, uv1.Y, uv1.X - uv0.X, -(uv1.Y - uv0.Y));
    }
}
