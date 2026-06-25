using System;
using System.Collections.Generic;
using System.Linq;
using ExileCampaigns.Guide;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;

namespace ExileCampaigns;

public partial class ExileCampaigns
{
    // per-step progress: counts events since the current step started; reset when step changes.
    private sealed class ProgressTracker
    {
        public RouteStep? Step;
        public readonly HashSet<uint> KilledIds = new();         // matched hostiles seen dead
        public readonly HashSet<uint> InteractedIds = new();     // matched entities seen non-interactable
        public readonly HashSet<string> DialogNames = new();     // npc names whose dialog opened
        public bool WaypointPulsed;
        public bool LoggedInPulse;                               // set on a fresh login, cleared on step change
        public readonly HashSet<uint> SeenLiveInteract = new();  // ids we saw interactable at least once
    }

    private ProgressTracker _progress = new();

    // call once per tick before advance eval. detects newly-dead matched hostiles, newly-non-interactable
    // matched entities, and open dialogs for the current step's matchers; resets on step change.
    private void UpdateProgressTracker(RouteStep? current)
    {
        if (!ReferenceEquals(_progress.Step, current)) _progress = new ProgressTracker { Step = current };
        if (current == null) return;

        var entMatchers = current.Objectives
            .SelectMany(o => o.Entities ?? Enumerable.Empty<EntityMatcher>())
            .ToList();

        if (entMatchers.Count > 0)
        {
            var ents = GameController?.EntityListWrapper?.Entities;
            if (ents != null)
            {
                foreach (var e in ents)
                {
                    if (e == null || !e.IsValid) continue;
                    if (!MatchesAnyEntity(e, entMatchers)) continue;
                    uint id = e.Id;
                    if (!e.IsAlive)
                    {
                        _progress.KilledIds.Add(id);
                    }
                    var targ = e.GetComponent<Targetable>();
                    if (targ != null)
                    {
                        if (targ.isTargetable)
                            _progress.SeenLiveInteract.Add(id);
                        else if (_progress.SeenLiveInteract.Contains(id))
                            _progress.InteractedIds.Add(id);
                    }
                    // opened chests also count as interacted
                    var chest = e.GetComponent<Chest>();
                    if (chest != null && chest.IsOpened) _progress.InteractedIds.Add(id);
                }
            }
        }

        try
        {
            var dialog = GameController?.IngameState?.IngameUi?.NpcDialog;
            if (dialog != null && dialog.IsVisible)
            {
                var name = dialog.NpcName ?? "";
                if (!string.IsNullOrEmpty(name)) _progress.DialogNames.Add(name);
            }
        }
        catch { /* dialog panel not ready */ }
    }

    private static bool MatchesAnyEntity(ExileCore.PoEMemory.MemoryObjects.Entity e, IReadOnlyList<EntityMatcher>? ms)
    {
        if (ms == null) return false;
        foreach (var m in ms)
        {
            var pm = new PatternMatcher(m.Match);
            var candidate = m.MatchBy == MatchKind.Path ? e.Path : e.RenderName;
            if (pm.IsMatch(candidate)) return true;
        }
        return false;
    }

    // display names of every item currently in the player's inventories (backpack + equipped).
    private List<string> InventoryItemNames()
    {
        var result = new List<string>();
        try
        {
            var invs = GameController?.IngameState?.ServerData?.PlayerInventories;
            if (invs == null) return result;
            foreach (var holder in invs)
            {
                var items = holder?.Inventory?.Items;
                if (items == null) continue;
                foreach (var e in items)
                {
                    if (e == null) continue;
                    // loot name read, ported off the excised ReadItemSnapshot; verify vs live poe1 items in phase 3
                    var name = e.GetComponent<Base>()?.Name ?? e.RenderName;
                    if (!string.IsNullOrEmpty(name)) result.Add(name);
                }
            }
        }
        catch { /* server data not ready */ }
        return result;
    }

    // live IWorldState bound to the current frame + the progress tracker.
    private sealed class WorldState : IWorldState
    {
        private readonly ExileCampaigns _p;
        public WorldState(ExileCampaigns p) { _p = p; }

        private IReadOnlyDictionary<QuestFlag, bool>? Flags
        {
            get
            {
                try { return _p.GameController?.IngameState?.Data?.ServerData?.QuestFlags; }
                catch { return null; }
            }
        }

        public bool QuestFlagSatisfied(Pattern flag)
        {
            var f = Flags;
            if (f == null) return false;
            var pm = new PatternMatcher(flag);
            foreach (var kv in f)
                if (kv.Value && pm.IsMatch(kv.Key.ToString())) return true;
            return false;
        }

        public int SatisfiedFlagCount(IReadOnlyList<Pattern> flags)
        {
            var f = Flags;
            if (f == null || flags == null) return 0;
            int n = 0;
            foreach (var p in flags)
            {
                var pm = new PatternMatcher(p);
                if (f.Any(kv => kv.Value && pm.IsMatch(kv.Key.ToString()))) n++;
            }
            return n;
        }

        // area IDs are exact identifiers, so literal patterns use equality not substring.
        // regex mode still works for wildcards if needed.
        public bool InAreaSatisfied(Pattern area) =>
            area.Regex
                ? new PatternMatcher(area).IsMatch(_p._areaId)
                : string.Equals(area.Value, _p._areaId, StringComparison.OrdinalIgnoreCase);

        public bool WaypointPulsed() => _p._progress.WaypointPulsed;

        public bool JustLoggedIn() => _p._progress.LoggedInPulse;

        // near a live TownPortal entity (a placed/town portal). lets "take portal" steps auto-advance as
        // you walk up to one. instantaneous truth, no per-step state.
        public bool NearTownPortal(float distance)
        {
            try
            {
                var ents = _p.GameController?.EntityListWrapper?.Entities;
                if (ents == null) return false;
                foreach (var e in ents)
                    if (e != null && e.IsValid && e.Type == EntityType.TownPortal && e.DistancePlayer <= distance)
                        return true;
            }
            catch { /* entities not ready */ }
            return false;
        }

        public bool ProximitySatisfied(IReadOnlyList<EntityMatcher>? entities, IReadOnlyList<Pattern>? tiles, float distance)
        {
            if (entities == null || entities.Count == 0) return false;
            try
            {
                var ents = _p.GameController?.EntityListWrapper?.Entities;
                if (ents == null) return false;
                foreach (var e in ents)
                {
                    if (e == null || !e.IsValid) continue;
                    if (MatchesAnyEntity(e, entities) && e.DistancePlayer <= distance) return true;
                }
            }
            catch { /* entities not ready */ }
            return false;
        }

        public bool LootSatisfied(IReadOnlyList<ItemMatcher>? items)
        {
            if (items == null || items.Count == 0) return false;
            var names = _p.InventoryItemNames();
            foreach (var it in items)
            {
                var pm = new PatternMatcher(it.Match);
                int held = names.Count(n => pm.IsMatch(n));
                if (held < (it.Count > 0 ? it.Count : 1)) return false;
            }
            return true;
        }

        public int KillProgress(IReadOnlyList<EntityMatcher>? entities) =>
            _p._progress.KilledIds.Count;

        public int InteractProgress(IReadOnlyList<EntityMatcher>? entities) =>
            _p._progress.InteractedIds.Count;

        public int TalkProgress(IReadOnlyList<EntityMatcher>? entities)
        {
            var names = _p._progress.DialogNames;
            if (entities == null || entities.Count == 0) return names.Count;
            int n = 0;
            foreach (var name in names)
                foreach (var m in entities)
                    if (new PatternMatcher(m.Match).IsMatch(name)) { n++; break; }
            return n;
        }
    }
}
