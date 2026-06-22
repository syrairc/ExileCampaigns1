// ExileCampaigns/Guide/StepMeta.cs
using System.Collections.Generic;

namespace ExileCampaigns.Guide;

// multi-sub-objective info for a step (was area-objectives.json). EntityPath set => entity-based objective
// (match live entities whose metadata Path contains this fragment), else room-based.
public sealed record ObjectiveMeta(
    string? Label = null,
    IReadOnlyList<string>? Rooms = null,
    int Count = 0,
    IReadOnlyList<string>? ProgressFlags = null,
    string? EntityPath = null);

// consolidated per-step metadata: the four side-JSONs collapsed onto the step. all fields optional so a
// partial override (e.g. user tweaks one flag) merges cleanly over the bundled record.
public sealed record StepMeta(
    string? CompletionFlag = null,
    string? CompletionKind = null,     // boss|reward|quest|waypoint|event
    ObjectiveMeta? Objective = null,
    string? PathTarget = null,
    string? TransitionTile = null,
    string? InteractKind = null,       // dialog|chest|proximity|kill
    bool? Optional = null,
    string? Note = null,
    // advance past this step when the player (re)enters this area id. for an optional sub-zone step (e.g. the
    // Mausoleum's Forgotten Riches cache) that should clear when you return to the parent zone, where no
    // quest flag re-fires (VisitedG1_7 is permanent) and the header-based OnAreaChanged is forward-only.
    string? CompleteOnEnterArea = null)
{
    // field-wise merge; `higher` wins per non-null field, falls back to `lower`.
    public static StepMeta Merge(StepMeta? lower, StepMeta? higher)
    {
        if (lower == null) return higher ?? new StepMeta();
        if (higher == null) return lower;
        return new StepMeta(
            higher.CompletionFlag ?? lower.CompletionFlag,
            higher.CompletionKind ?? lower.CompletionKind,
            higher.Objective ?? lower.Objective,
            higher.PathTarget ?? lower.PathTarget,
            higher.TransitionTile ?? lower.TransitionTile,
            higher.InteractKind ?? lower.InteractKind,
            higher.Optional ?? lower.Optional,
            higher.Note ?? lower.Note,
            higher.CompleteOnEnterArea ?? lower.CompleteOnEnterArea);
    }
}
