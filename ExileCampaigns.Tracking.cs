using System;
using System.Collections.Concurrent;
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
            var pm = PatternMatcher.For(m.Match);
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
                    // loot name read: Base.Name, falling back to RenderName
                    var name = e.GetComponent<Base>()?.Name ?? e.RenderName;
                    if (!string.IsNullOrEmpty(name)) result.Add(name);
                }
            }
        }
        catch { /* server data not ready */ }
        return result;
    }

    // flag name -> enum value never changes, only the bools do. so resolve a pattern to its flags once and
    // then just look those up: checking a flag used to walk all ~2240 dict entries and ToString every key.
    private static readonly QuestFlag[] AllQuestFlagValues = (QuestFlag[])Enum.GetValues(typeof(QuestFlag));
    private static readonly string[] AllQuestFlagNames = Enum.GetNames(typeof(QuestFlag));

    private static readonly ConcurrentDictionary<Pattern, QuestFlag[]> FlagsByPattern = new();

    private static QuestFlag[] FlagsMatching(Pattern? pattern)
    {
        if (pattern == null) return Array.Empty<QuestFlag>();
        return FlagsByPattern.GetOrAdd(pattern, static p =>
        {
            var pm = PatternMatcher.For(p);
            var hits = new List<QuestFlag>();
            for (int i = 0; i < AllQuestFlagNames.Length && i < AllQuestFlagValues.Length; i++)
                if (pm.IsMatch(AllQuestFlagNames[i])) hits.Add(AllQuestFlagValues[i]);
            return hits.ToArray();
        });
    }

    // an authored progress-flag string -> its enum, parsed once. exact (case-insensitive) name match, same as
    // the old compare against the live flag names - deliberately NOT PatternMatcher, which does substring.
    private static readonly ConcurrentDictionary<string, QuestFlag?> ProgressFlagValues = new();

    private static QuestFlag? ProgressFlagValue(string name) =>
        ProgressFlagValues.GetOrAdd(name, static n => Enum.TryParse<QuestFlag>(n, true, out var v) ? v : (QuestFlag?)null);

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
            foreach (var q in FlagsMatching(flag))
                if (f.TryGetValue(q, out var set) && set) return true;
            return false;
        }

        public int SatisfiedFlagCount(IReadOnlyList<Pattern> flags)
        {
            var f = Flags;
            if (f == null || flags == null) return 0;
            int n = 0;
            foreach (var p in flags)
                if (QuestFlagSatisfied(p)) n++;
            return n;
        }

        // area IDs are exact identifiers, so literal patterns use equality not substring.
        // regex mode still works for wildcards if needed.
        public bool InAreaSatisfied(Pattern area) =>
            area.Regex
                ? PatternMatcher.For(area).IsMatch(_p._areaId)
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
                var pm = PatternMatcher.For(it.Match);
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
                    if (PatternMatcher.For(m.Match).IsMatch(name)) { n++; break; }
            return n;
        }
    }

    // pulse detection for the two flagless advance triggers: waypoint activation and fresh login.
    // "Get Waypoint" steps (ActivateWaypoint objective) advance on AchievementCheckWaypoints, which pulses
    // false->true each time you activate a waypoint. (WaypointUnlocked was wrong - it's a near one-time flag,
    // set on your first waypoint ever and then stuck true, so the edge only ever fired once per character.)
    // edge-triggered so an already-set flag doesn't auto-complete the step.
    // _progress.WaypointPulsed clears on step change (new ProgressTracker), same as the login pulse.
    private const string WaypointFlag = "AchievementCheckWaypoints";
    // resolved once: this runs every tick, and the old name sweep walked the whole ~2240-entry flag dict
    private static readonly QuestFlag? WaypointFlagValue =
        Enum.TryParse<QuestFlag>(WaypointFlag, out var wpv) ? wpv : null;
    private bool _wpFlagWasSet;
    private bool _wpFirstPoll = true;
    private void UpdateWaypointPulse()
    {
        if (WaypointFlagValue is not { } wpFlag) return;
        var flags = ReadQuestFlags();
        if (flags == null) return;
        bool nowSet = flags.TryGetValue(wpFlag, out var wpSet) && wpSet;
        if (_wpFirstPoll) { _wpFirstPoll = false; _wpFlagWasSet = nowSet; return; }
        if (nowSet && !_wpFlagWasSet) _progress.WaypointPulsed = true;
        _wpFlagWasSet = nowSet;
    }

    // fresh-login detection for "log out" steps (the relog/instance-reset trick). exileapi freezes plugin
    // ticks while you sit at char-select, so we can't catch the login-screen frame directly. instead watch
    // the in-game session clock: TimeInGame keeps climbing across normal zone transitions but resets to ~0
    // on a fresh login. so a backwards jump = a relog happened while we were frozen -> pulse it onto the
    // current step's progress (cleared on step change, like the waypoint pulse). no false trigger on zones.
    private float _lastTimeInGame = -1f;
    private void UpdateLoginPulse()
    {
        var game = GameController?.Game;
        if (game == null || !game.IsInGameState) return;
        var t = GameController?.IngameState?.TimeInGameF ?? 0f;
        if (t <= 0f) return;
        if (_lastTimeInGame <= 0f) { _lastTimeInGame = t; return; }  // first poll, just seed
        // 2s margin absorbs read jitter so only a real session reset trips it
        if (t + 2f < _lastTimeInGame) _progress.LoggedInPulse = true;
        _lastTimeInGame = t;
    }


    // manual sync: re-point the tracker at the character's real progress from live quest flags + area.
    private void SyncToCharacter()
    {
        if (_route.Steps.Count == 0) return;
        var world = new WorldState(this);
        int target = RouteSync.ResolveSyncTarget(_route.Steps, world.QuestFlagSatisfied, _areaId);
        if (target < 0) return;
        _route.SetCurrent(target);
        var txt = _route.CurrentStep?.DisplayText ?? "";
        ShowToast(string.IsNullOrEmpty(txt) ? "Synced to character" : $"Synced to: {txt}", ToastLevel.Success);
        MaybeSaveProgress();
    }
}
