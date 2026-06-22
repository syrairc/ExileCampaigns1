using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace ExileCampaigns;

// reusable toast notifications: short transient messages stacked at a fixed anchor, each fading out near
// the end of its life. call ShowToast(...) from anywhere; DrawToasts() runs every frame in Render.
public partial class ExileCampaigns
{
    internal enum ToastLevel { Info, Success, Warning, Error }

    private sealed class Toast
    {
        public string Text = "";
        public ToastLevel Level;
        public DateTime ShownAt;
        public double Duration;
    }

    private readonly List<Toast> _toasts = new();
    private const int MaxToasts = 5;
    private const float ToastAccentW = 4f;   // coloured bar down the left edge

    private void ShowToast(string text, ToastLevel level = ToastLevel.Info, double? seconds = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var dur = seconds ?? Settings.Toasts.DurationSeconds.Value;
        _toasts.Add(new Toast { Text = text, Level = level, ShownAt = DateTime.Now, Duration = dur });
        if (_toasts.Count > MaxToasts) _toasts.RemoveRange(0, _toasts.Count - MaxToasts);
    }

    private static Color ToastAccent(ToastLevel l) => l switch
    {
        ToastLevel.Success => new Color(120, 210, 120, 255),
        ToastLevel.Warning => new Color(230, 180, 90, 255),
        ToastLevel.Error => new Color(230, 110, 100, 255),
        _ => new Color(150, 190, 240, 255),
    };

    private void DrawToasts()
    {
        var t = Settings.Toasts;
        if (!t.Enable) return;

        var now = DateTime.Now;
        _toasts.RemoveAll(x => (now - x.ShownAt).TotalSeconds >= x.Duration);
        bool preview = t.Preview.Value;
        if (_toasts.Count == 0 && !preview) return;

        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        if (baseSize <= 0) baseSize = 16f;
        float size = t.TextSize.Value;
        float scale = size / baseSize;
        float pad = t.Padding.Value;
        float lineH = (float)Math.Ceiling(size) + 4f;
        const float gap = 6f;

        // measure a box (wrapping to MaxWidth) -> (size, rows).
        (Vector2 size, List<string> rows) MeasureBox(string text)
        {
            var ascii = Ascii(text);
            float maxTextW = t.MaxWidth.Value > 0 ? Math.Max(10f, t.MaxWidth.Value - pad * 2 - ToastAccentW) : float.MaxValue;
            var rows = t.MaxWidth.Value > 0 && ImGui.CalcTextSize(ascii).X * scale > maxTextW
                ? WrapText(ascii, maxTextW, scale)
                : new List<string> { ascii };
            float contentW = 0f;
            foreach (var r in rows) contentW = Math.Max(contentW, ImGui.CalcTextSize(r).X * scale);
            return (new Vector2(contentW + pad * 2 + ToastAccentW, rows.Count * lineH + pad * 2), rows);
        }

        float anchorX = t.PosX.Value;   // horizontal centre of the stack
        float y = t.PosY.Value;          // top of the stack, grows downward

        // center-anchored move/resize during preview (only when unlocked), via the shared helper. the sample
        // box doubles as the drag handle; moving it updates PosX/PosY/MaxWidth for the real toasts too.
        if (preview && !Settings.LockOverlays.Value)
        {
            var (ssize, _) = MeasureBox("Sample toast (preview)");
            var min = new Vector2(t.PosX.Value - ssize.X / 2f, t.PosY.Value);
            var (hovered, onEdge, active) = HandleCenterInteract("toasts", ref min, ssize, t.PosX, t.PosY, t.MaxWidth);
            if (hovered || active) DrawClickBlocker("toasts", min, ssize);
            DrawDragHint(min, min + ssize, active, hovered, onEdge, _resizeId == "toasts");
            anchorX = t.PosX.Value;   // follow a move this frame
            y = t.PosY.Value;
        }

        // one box per toast; returns the y for the next one below it.
        float DrawBox(string text, ToastLevel level, float alpha, float top)
        {
            var (boxSize, rows) = MeasureBox(text);
            var min = new Vector2(anchorX - boxSize.X / 2f, top);
            var max = min + boxSize;

            var bg = Fade(t.BackgroundColor.Value, alpha);
            if (bg.A > 0) dl.AddRectFilled(min, max, U32(bg));
            dl.AddRectFilled(min, new Vector2(min.X + ToastAccentW, max.Y), U32(Fade(ToastAccent(level), alpha)));
            if (t.BorderThickness.Value > 0)
                dl.AddRect(min, max, U32(Fade(t.BorderColor.Value, alpha)), 0f, ImDrawFlags.None, t.BorderThickness.Value);

            uint textCol = U32(Fade(t.TextColor.Value, alpha));
            var p = new Vector2(min.X + ToastAccentW + pad, min.Y + pad);
            foreach (var r in rows) { dl.AddText(font, size, p, textCol, r); p.Y += lineH; }

            return max.Y + gap;
        }

        foreach (var toast in _toasts)
        {
            const double fade = 0.5;
            var remaining = toast.Duration - (now - toast.ShownAt).TotalSeconds;
            float alpha = remaining < fade ? (float)Math.Clamp(remaining / fade, 0, 1) : 1f;
            y = DrawBox(toast.Text, toast.Level, alpha, y);
        }

        if (preview && _toasts.Count == 0)
            DrawBox("Sample toast (preview)", ToastLevel.Info, 1f, y);
    }
}
