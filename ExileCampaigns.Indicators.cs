using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ExileCampaigns.Guide;
using ExileCampaigns.Rendering;
using ExileCampaigns.Tracking;
using ExileCore.PoEMemory.Components;
using ImGuiNET;
using SharpDX;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileCampaigns;

// in-world interaction indicator: golden pulsing down-arrow over the step's resolved target.
// target = ONLY the objective's explicit Indicators[] entity children, decoupled from the ground path
// (Paths[]): an arrow needs no path, a path needs no arrow, and no Indicator means no arrow (no text-pass
// inference). advance is handled by EvaluateAdvance (AdvanceEngine); this file only drives the arrow.
public partial class ExileCampaigns
{
    private const string IndicatorTexture = "ExileCampaigns_Icons";
    private bool _indicatorTexLoaded;

    // current step's resolved interaction targets, refreshed (throttled) in Tick, reused by Render. a list so
    // multiple authored Indicators[] (or one pattern matching several live entities) each get an arrow.
    private IReadOnlyList<InteractTarget> _interactTargets = System.Array.Empty<InteractTarget>();
    private DateTime _lastInteractResolve;


    // load the icon atlas once. DirectoryFullName = Plugins\Temp output dir where csproj Content lands
    private void InitIndicatorTexture()
    {
        if (_indicatorTexLoaded) return;
        try
        {
            var path = Path.Combine(DirectoryFullName, "textures", SpriteAtlas.FileName);
            if (File.Exists(path))
            {
                Graphics.InitImage(IndicatorTexture, path);
                _indicatorTexLoaded = true;
            }
            else LogError($"ExileCampaigns -> indicator texture not found: {path}");
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> indicator texture init failed: {ex.Message}"); }
    }

    private bool InteractFeatureActive => Settings.InteractIndicator.Enable.Value;

    // throttled resolve of the current step's interaction target. called from Tick
    private void UpdateInteractTarget()
    {
        if (!InteractFeatureActive) { _interactTargets = System.Array.Empty<InteractTarget>(); return; }
        if ((DateTime.Now - _lastInteractResolve).TotalSeconds < 0.15) return;
        _lastInteractResolve = DateTime.Now;

        var flat = _route.CurrentStep;
        var step = flat?.Step;
        if (step == null) { _interactTargets = System.Array.Empty<InteractTarget>(); return; }

        try
        {
            float maxDist = Settings.InteractIndicator.MaxDistance.Value;
            // arrow source = ONLY the objective's explicit Indicators[] entity children (ALL matching
            // entities, one arrow each). no text-pass fallback: a step with no authored Indicator shows no
            // arrow. advance is unaffected (it runs through AdvanceEngine), so this is purely cosmetic.
            var indTargets = flat?.Model != null
                ? GuidanceView.IndicatorEntityTargets(flat.Model)
                : (IReadOnlyList<Target>)System.Array.Empty<Target>();
            _interactTargets = indTargets.Count > 0
                ? _targetResolver.ResolveIndicatorEntities(GameController, indTargets, maxDist)
                : System.Array.Empty<InteractTarget>();
        }
        catch { _interactTargets = System.Array.Empty<InteractTarget>(); }
    }

    // draw the pulsing golden down-arrow above the resolved target. called from Render after the
    // fullscreen-panel hide guard, so it inherits that suppression
    private void DrawInteractIndicators()
    {
        if (!Settings.InteractIndicator.Enable || !_indicatorTexLoaded) return;
        foreach (var target in _interactTargets)
            DrawOneIndicator(target);
    }

    private void DrawOneIndicator(InteractTarget target)
    {
        var e = target?.Entity;
        if (e == null || !e.IsValid) return;

        // don't keep pointing at an already-opened chest
        if (target!.Kind == InteractKind.ChestOpen && (e.GetComponent<Chest>()?.IsOpened ?? false)) return;

        Vector2 screen;
        try
        {
            var pos = e.Pos;                                   // world-space, at the feet
            var boundsZ = e.GetComponent<Render>()?.Bounds.Z ?? 0f;
            pos.Z -= boundsZ * 2f + Settings.InteractIndicator.HeightOffset.Value;  // lift above the head
            var raw = GameController.IngameState.Camera.WorldToScreen(pos);
            screen = new Vector2(raw.X, raw.Y);
        }
        catch { return; }
        if (screen == Vector2.Zero) return;                   // off-screen / behind camera

        // bob up/down so it reads as nudging at the object below it
        float t = (float)ImGui.GetTime();
        float bob = MathF.Sin(t * Settings.InteractIndicator.BobSpeed.Value) * Settings.InteractIndicator.BobDistance.Value;

        float size = Settings.InteractIndicator.IconSize.Value;
        float half = size / 2f;
        var col = Settings.InteractIndicator.IconColor.Value;

        var dest = new RectangleF(screen.X - half, screen.Y - half + bob, size, size);
        var uv = SpriteAtlas.GetUVRectFlippedV(SpriteIcon.Arrow);   // up-arrow flipped to point down
        Graphics.DrawImage(IndicatorTexture, dest, uv, col);
    }

}
