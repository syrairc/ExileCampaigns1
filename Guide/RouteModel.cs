using System.Collections.Generic;

namespace ExileCampaigns.Guide;

// what completes a step's objective. see AdvanceEngine for how each is evaluated.
public enum ObjectiveType { Kill, Interact, Talk, Loot, Proximity, QuestFlag, EnterArea, ActivateWaypoint, Login, TownPortal, Manual }

// when a step with several objectives advances.
public enum CompleteWhen { All, Any }

// how many path lines an objective's guidance draws: Nearest = one, to the closest target (default);
// All = a line to every resolved target (e.g. all 3 Ancient Seals).
public enum PathMode { Nearest, All }

// how an entity matcher matches a live entity: by render name or by metadata path.
public enum MatchKind { Name, Path }

// every value matched against game state. literal (default, case-insensitive contains) or a regex.
public sealed record Pattern(string Value, bool Regex = false);

// a world target (boss by Name, no-name object like NailStake by Path).
public sealed record EntityMatcher(Pattern Match, MatchKind MatchBy = MatchKind.Name);

// a Loot target: an inventory item plus how many must be held.
public sealed record ItemMatcher(Pattern Match, int Count = 1);

// what a guidance child points at. Tile = Radar ClusterTarget pattern / .tdt; Room = AreaGraph room-name
// filter; Entity = live entity by metadata Path or RenderName.
public enum TargetKind { Tile, Entity, Room }

// a guidance target shared by Path / Indicator / MinimapIcon. MatchBy + LivingOnly apply only to Entity.
// LivingOnly: only resolve a live entity that's actually alive (has Life, CurrentHP > 0), so an arrow/icon
// skips a corpse or a lifeless prop sharing the name. honored by Indicators + MinimapIcons (not the Paths channel).
public sealed record Target(TargetKind Kind, Pattern Match, MatchKind MatchBy = MatchKind.Name, bool LivingOnly = false);

// one ground/minimap route line. an Entity target that matches several live entities draws one line each.
public sealed record GuidePath(Target Target);

// one on-screen arrow/marker. Entity targets draw the arrow; Tile/Room are accepted but not drawn yet.
public sealed record Indicator(Target Target);

// single per-objective minimap icon. IconKey = SpriteIcon enum name; Tint = packed ARGB (default gold);
// Size = per-icon pixel size, null = use the global MinimapIcons.IconSize default.
public sealed record MinimapIcon(string IconKey, Target? Target = null, uint Tint = 0xFFFFC83Cu, float? Size = null)  // Tint default = gold
{
    public const uint GoldDefault = 0xFFFFC83Cu;   // gold, matches the interaction arrow
}

// one objective on a step. only the fields relevant to Type are used.
public sealed record Objective(
    ObjectiveType Type,
    IReadOnlyList<EntityMatcher>? Entities = null,   // Kill/Interact/Talk/Proximity (priority order)
    IReadOnlyList<ItemMatcher>? Items = null,        // Loot
    int Count = 1,                                   // Kill/Interact/Talk (Loot uses ItemMatcher.Count)
    float Distance = 0f,                             // Proximity (units; engine applies a default when 0)
    Pattern? Flag = null,                            // QuestFlag
    Pattern? AreaTarget = null,                      // EnterArea (area id)
    IReadOnlyList<Pattern>? ProgressFlags = null,    // optional per-target flags (multi-activate drop-each)
    string? Label = null,
    string? Note = null,
    IReadOnlyList<GuidePath>? Paths = null,          // guidance: ground/minimap route lines (independent of Type)
    IReadOnlyList<Indicator>? Indicators = null,     // guidance: on-screen arrows (independent of Type)
    IReadOnlyList<MinimapIcon>? MinimapIcons = null,// guidance: large-map icons (independent of Type)
    PathMode Mode = PathMode.Nearest);              // path line count: Nearest = one (default), All = per target

// one route step. self-describing: explicit act + area, plus its objectives. Id is the stable identity.
public sealed record RouteStep(
    string Id,
    int Act,
    string AreaId,
    string AreaName,
    string Text,
    bool Optional,
    CompleteWhen CompleteWhen,
    IReadOnlyList<Objective> Objectives,
    string? ImportFp,    // fnv1a of the upstream text this came from (import diff); null = user-created
    bool LeagueStart = false);   // league-start-only chore (crafting recipes, trials); hidden when "Show league-start steps" is off

// the whole route. Steps list order is the canonical sequence.
public sealed record RouteDocument(int Version, IReadOnlyList<RouteStep> Steps)
{
    public const int CurrentVersion = 2;   // v2: guidance decoupled into Paths/Indicators/MinimapIcon children
    public static readonly RouteDocument Empty = new(CurrentVersion, new List<RouteStep>());
}
