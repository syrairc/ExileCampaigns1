using System;
using System.Collections.Generic;
using System.Linq;

namespace ExileCampaigns.Guide;

// mirrors HeartofPhos/exile-leveling fragment/types.d.ts. step = sequence of fragments (literal text +
// typed tokens). pure C#, no ExileCore dep, unit-testable. PlainText() is plain-text-first; icon/colour
// can layer on later by switching on Kind.
public enum FragmentKind
{
    Text, Kill, Arena, Area, Enter, Logout, Waypoint, WaypointUse, WaypointGet,
    Portal, Quest, QuestText, Generic, RewardQuest, RewardVendor, Trial, Ascend,
    Crafting, Direction, Copy
}

public abstract record Fragment(FragmentKind Kind)
{
    // plain-text for the overlay. area-id args render as the raw id; a higher layer resolves ids to names.
    public abstract string PlainText();

    // area this fragment moves the player into, if any (memory-driven auto-advance).
    public virtual string? AreaId => null;
}

public sealed record TextFragment(string Text) : Fragment(FragmentKind.Text)
{
    public override string PlainText() => Text;
}

public sealed record KillFragment(string Target) : Fragment(FragmentKind.Kill)
{
    public override string PlainText() => $"kill {Target}";
}

public sealed record ArenaFragment(string Target) : Fragment(FragmentKind.Arena)
{
    public override string PlainText() => Target;
}

public sealed record AreaFragment(string Id) : Fragment(FragmentKind.Area)
{
    // pure auto-advance marker, renders nothing (ParsedStep falls back to the id if the step is empty).
    public override string PlainText() => "";
    public override string? AreaId => Id;
}

public sealed record EnterFragment(string Id) : Fragment(FragmentKind.Enter)
{
    // pure auto-advance marker, renders nothing. PoE2 zone headers carry a readable label after it;
    // PoE1 routes ({enter|G1_2} alone) fall back to "-> id" via ParsedStep.PlainText.
    public override string PlainText() => "";
    public override string? AreaId => Id;
}

public sealed record LogoutFragment(string Id) : Fragment(FragmentKind.Logout)
{
    public override string PlainText() => $"logout to {Id}";
    public override string? AreaId => Id;
}

public sealed record WaypointFragment() : Fragment(FragmentKind.Waypoint)
{
    public override string PlainText() => "waypoint";
}

public sealed record WaypointUseFragment(string Source, string Destination) : Fragment(FragmentKind.WaypointUse)
{
    public override string PlainText() => $"waypoint -> {Destination}";
    public override string? AreaId => Destination;
}

public sealed record WaypointGetFragment() : Fragment(FragmentKind.WaypointGet)
{
    public override string PlainText() => "take waypoint";
}

public sealed record PortalFragment(string Operation) : Fragment(FragmentKind.Portal)
{
    public override string PlainText() =>
        Operation.Equals("set", StringComparison.OrdinalIgnoreCase) ? "set portal" : "take portal";
}

public sealed record QuestFragment(string QuestId, IReadOnlyList<string> RewardOffers) : Fragment(FragmentKind.Quest)
{
    public override string PlainText() => $"quest {QuestId}";
}

public sealed record QuestTextFragment(string Text) : Fragment(FragmentKind.QuestText)
{
    public override string PlainText() => Text;
}

public sealed record GenericFragment(string Text) : Fragment(FragmentKind.Generic)
{
    public override string PlainText() => Text;
}

public sealed record RewardQuestFragment(string Item) : Fragment(FragmentKind.RewardQuest)
{
    public override string PlainText() => $"take {Item}";
}

public sealed record RewardVendorFragment(string Item, string? Cost) : Fragment(FragmentKind.RewardVendor)
{
    public override string PlainText() => Cost is null ? $"buy {Item}" : $"buy {Item} ({Cost})";
}

public sealed record TrialFragment() : Fragment(FragmentKind.Trial)
{
    public override string PlainText() => "trial of ascendancy";
}

public sealed record AscendFragment(string Version) : Fragment(FragmentKind.Ascend)
{
    public override string PlainText() => "ascend";
}

public sealed record CraftingFragment(string? Id) : Fragment(FragmentKind.Crafting)
{
    public override string PlainText() => "crafting recipe";
    public override string? AreaId => Id;
}

public sealed record DirectionFragment(int Degrees) : Fragment(FragmentKind.Direction)
{
    // 8-point compass from degrees (0 = up/N, clockwise), exile-leveling's 45° steps.
    // ASCII labels (ImGui default font is ASCII-only).
    private static readonly string[] Compass = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    public override string PlainText()
    {
        var idx = ((Degrees % 360 + 360) % 360 + 22) / 45 % 8;
        return Compass[idx];
    }
}

public sealed record CopyFragment(string Text) : Fragment(FragmentKind.Copy)
{
    public override string PlainText() => Text;
}

// one route step: ordered run of fragments (literal text interleaved with typed tokens).
public sealed class ParsedStep
{
    public List<Fragment> Fragments { get; } = new();

    // flagged optional (poe2-leveling "(Opt)" or a leading "optional:" marker).
    public bool IsOptional { get; set; }

    // consolidated override metadata attached at load by OverrideApplier; null when no override targets this step.
    public StepMeta? Meta { get; set; }

    // a zone-label line ({enter|id} / {area|id}). narrower than "has an AreaId": waypoint-use/logout carry an
    // AreaId yet are real tasks.
    public bool IsHeader => Fragments.Any(f => f.Kind is FragmentKind.Enter or FragmentKind.Area);

    // area the player should be in / heading to, if any fragment names one. matched (lowercased)
    // against live WorldArea.Id for auto-advance.
    public string? AreaId =>
        Fragments.Select(f => f.AreaId).FirstOrDefault(id => !string.IsNullOrEmpty(id));

    public string PlainText()
    {
        var text = string.Concat(Fragments.Select(f => f.PlainText())).Trim();
        // marker-only step (e.g. PoE1 "{enter|G1_2}") renders to nothing; show where it leads.
        if (text.Length == 0 && AreaId is { Length: > 0 } id)
            return $"-> {id}";
        return text;
    }
}

// parsed route for one act.
public sealed class RouteSection
{
    public int Act { get; init; }
    public List<ParsedStep> Steps { get; } = new();
}
