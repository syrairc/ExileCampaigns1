using System;
using System.Collections.Generic;
using System.Linq;

namespace ExileCampaigns.Guide;

// flattened step + its act and 1-based position within that act, for sequential navigation across the
// campaign (StepInAct drives the steps-tracker number column + act headers). Model is null for area
// header rows; HeaderAreaId/HeaderAreaName carry the zone identity for those rows.
public sealed record FlatStep(int Act, int StepInAct, RouteStep? Model, string? HeaderAreaId = null, string? HeaderAreaName = null)
{
    public bool IsHeader => Model == null;
    public string DisplayText =>
        Model != null ? Model.Text
        : !string.IsNullOrEmpty(HeaderAreaName) ? HeaderAreaName!
        : !string.IsNullOrEmpty(HeaderAreaId) ? $"-> {HeaderAreaId}" : "";
}

// loads per-act route files, flattens to one ordered sequence, tracks the current step. drives memory
// auto-advance: each zone-header step carries an area id mapped to its position, so an AreaChange jumps
// straight to it. pure C# (no ExileCore dep), unit-testable.
public sealed class RouteRepository
{
    private readonly List<FlatStep> _steps = new();
    private readonly Dictionary<string, List<int>> _areaToIndices = new(StringComparer.OrdinalIgnoreCase);
    // effective area id per step: most recent zone-header AreaId at or before that step, so sub-steps in a
    // zone (kill/reward, no AreaId of their own) still know their zone. quest-flag auto-advance confines
    // its search to the flag's area block.
    private readonly List<string> _stepArea = new();
    // cleaned zone label parallel to _stepArea -- only set when the step is a zone header, carried forward
    // so every sub-step in a zone knows the readable name without re-scanning headers.
    private readonly List<string> _stepAreaName = new();

    public IReadOnlyList<FlatStep> Steps => _steps;
    public int Current { get; private set; }
    public string? Status { get; private set; }

    // mirrors Settings.ShowOptional. when false, optional steps are hidden from the overlay AND navigation
    // skips them -- auto/manual advance and back land only on visible steps, never a hidden optional one.
    // default true so position restore / sync / tests behave exactly as before unless the host sets it.
    public bool IncludeOptional { get; set; } = true;

    // mirrors Settings.ShowLeagueStart. when false, league-start steps (crafting recipes, trials) are hidden
    // from the overlay AND navigation skips them, same as a hidden optional. default true.
    public bool IncludeLeagueStart { get; set; } = true;
    public FlatStep? CurrentStep => _steps.Count > 0 && Current < _steps.Count ? _steps[Current] : null;

    // effective area id of the current step: its own AreaId if it has one (zone header), else the most
    // recent header above it. sub-steps (kill/loot/enter-text) carry no AreaId of their own, so the path
    // resolver needs this to key into the authored area->tile map.
    public string CurrentAreaId => Current >= 0 && Current < _stepArea.Count ? _stepArea[Current] : "";

    // readable zone label of the current step's area (cleaned of [tags] and (level) decorations),
    // for the route header's top-right. "" when there is no current step.
    public string CurrentAreaName => Current >= 0 && Current < _stepAreaName.Count ? _stepAreaName[Current] : "";

    // count of real (numbered) steps in an act, for the route-progress stat. headers carry StepInAct 0.
    public int StepsInAct(int act) => _steps.Count(s => s.Act == act && s.StepInAct > 0);

    // a zone-label line: kept in the model for the area->index map, but never an objective. navigation
    // never rests on one and they're excluded from Previous/Upcoming.
    private static bool IsHeaderStep(FlatStep s) => s.IsHeader;

    // a step navigation must never rest on: a zone-label header always, plus -- when optional is hidden --
    // an optional step. headers and (hidden) optionals are stepped over together by advance/back.
    private bool IsSkippable(int i) =>
        IsHeaderStep(_steps[i])
        || (!IncludeOptional && (_steps[i].Model?.Optional ?? false))
        || (!IncludeLeagueStart && (_steps[i].Model?.LeagueStart ?? false));

    // after an advance landed Current on a skippable row (header, or hidden optional), step forward to the
    // next visible objective. falls back to the nearest visible step behind so we never rest on a hidden
    // row when nothing visible lies ahead (e.g. last step is a hidden optional).
    private void SnapForwardVisible()
    {
        if (_steps.Count == 0) { Current = 0; return; }
        if (!IsSkippable(Current)) return;
        for (int i = Current; i < _steps.Count; i++)
            if (!IsSkippable(i)) { Current = i; return; }
        for (int i = Current - 1; i >= 0; i--)
            if (!IsSkippable(i)) { Current = i; return; }
    }

    // if Current landed on a zone-label header, move to the first real step in that zone (forward
    // preferred so entering a zone lands on its first task; fall back to the last real step behind).
    private void SnapOffHeader()
    {
        if (_steps.Count == 0) { Current = 0; return; }
        if (!IsHeaderStep(_steps[Current])) return;
        for (int i = Current; i < _steps.Count; i++)
            if (!IsHeaderStep(_steps[i])) { Current = i; return; }
        for (int i = Current - 1; i >= 0; i--)
            if (!IsHeaderStep(_steps[i])) { Current = i; return; }
    }

    // load the unified RouteDocument: one header row per area boundary, then each model step.
    public bool LoadFromDocument(RouteDocument doc)
    {
        _steps.Clear(); _areaToIndices.Clear(); _stepArea.Clear(); _stepAreaName.Clear();
        Current = 0;
        if (doc == null) { Status = "no route document"; return false; }

        int act = -1, stepInAct = 0;
        string curArea = "";
        foreach (var model in doc.Steps)
        {
            // act split resets the per-act objective number, matching the act-file flatten.
            if (model.Act != act) { act = model.Act; stepInAct = 0; }

            // area boundary: emit a header row (StepInAct 0, Model null).
            if (!string.Equals(model.AreaId, curArea, System.StringComparison.OrdinalIgnoreCase))
            {
                curArea = model.AreaId ?? "";
                int hidx = _steps.Count;
                _steps.Add(new FlatStep(model.Act, 0, null, model.AreaId ?? "", model.AreaName ?? ""));
                if (!string.IsNullOrEmpty(curArea))
                {
                    if (!_areaToIndices.TryGetValue(curArea, out var hlist))
                        _areaToIndices[curArea] = hlist = new List<int>();
                    hlist.Add(hidx);
                }
                _stepArea.Add(curArea);
                _stepAreaName.Add(model.AreaName ?? "");
            }

            int idx = _steps.Count;
            _steps.Add(new FlatStep(model.Act, ++stepInAct, model));
            _stepArea.Add(curArea);
            _stepAreaName.Add(model.AreaName ?? "");
        }

        SnapOffHeader();
        Status = $"{doc.Steps.Count} steps ({_steps.Count} rows)";
        return _steps.Count > 0;
    }

    // how far ahead auto-advance may jump. early arrival at a zone that next appears far ahead
    // shouldn't skip a chunk of the campaign.
    private const int AdvanceWindow = 5;

    // entered a new area: jump to the route step for that area id. forward-only: nearest occurrence at or
    // ahead of current, within AdvanceWindow. a revisit whose steps are all behind us (e.g. TP to town),
    // or whose next step is beyond the window, leaves current unchanged.
    public void OnAreaChanged(string? areaIdLower)
    {
        if (string.IsNullOrEmpty(areaIdLower)) return;
        if (!_areaToIndices.TryGetValue(areaIdLower, out var idxs) || idxs.Count == 0) return;
        int next = int.MaxValue;
        foreach (var i in idxs)
            if (i >= Current && i - Current <= AdvanceWindow && i < next) next = i;
        if (next == int.MaxValue) return;

        // the matched step is the zone-label header. rest the objective on the zone's first VISIBLE task
        // (skips the header and, when optional is hidden, any leading optional steps).
        Current = next;
        SnapForwardVisible();
    }

    // advance past the current step iff it itself matches, area-agnostic. for a global flag that should only
    // complete the step you're standing on -- taking any waypoint flips AchievementCheckWaypoints, but it
    // should only clear the "Take waypoint" step you're on, never scan ahead and skip content to a later one.
    public bool AdvanceCurrentStepIfMatch(Func<string, bool> matches)
    {
        if (_steps.Count == 0 || Current < 0 || Current >= _steps.Count) return false;
        if (IsHeaderStep(_steps[Current])) return false;
        if (!matches(_steps[Current].Model?.Text ?? "")) return false;
        int target = Math.Min(Current + 1, _steps.Count - 1);
        if (target <= Current) return false;
        Current = target;
        SnapForwardVisible();
        return true;
    }

    // the cursor is parked on a "dead" reminder step (no possible auto-advance, e.g. "TP back to start of
    // zone") -- find the first LIVE step ahead so completing IT can pull the cursor past the reminder.
    // forward-only, within AdvanceWindow, scanning over consecutive dead reminders. returns -1 when the
    // current step is itself live (normal current-step advance applies) or no live step is in reach.
    // STOPS at a zone header: crossing a zone boundary is the area-change advance's job, never interaction's.
    public int FirstLiveStepAheadIndex(Func<RouteStep, bool> isDead)
    {
        if (isDead == null || _steps.Count == 0 || Current < 0 || Current >= _steps.Count) return -1;
        if (IsHeaderStep(_steps[Current]) || !isDead(_steps[Current].Model!)) return -1;
        for (int i = Current + 1; i < _steps.Count && i - Current <= AdvanceWindow; i++)
        {
            if (IsHeaderStep(_steps[i])) return -1;        // don't skip across a zone label
            if (isDead(_steps[i].Model!)) continue;        // skip a further dead reminder
            return i;                                       // first live step in reach
        }
        return -1;
    }

    // read-only: does any step in `area`'s block satisfy the predicate? lets tests check every curated
    // quest-flag entry joins to a step, without mutating position.
    public bool AnyStepInArea(string area, Func<string, bool> matches)
    {
        if (string.IsNullOrEmpty(area)) return false;
        for (int i = 0; i < _steps.Count; i++)
        {
            if (IsHeaderStep(_steps[i])) continue;
            if (string.Equals(_stepArea[i], area, StringComparison.OrdinalIgnoreCase)
                && matches(_steps[i].Model?.Text ?? ""))
                return true;
        }
        return false;
    }

    // step over zone-label headers (and hidden optionals) so manual next/prev lands only on visible objectives.
    public void Next()
    {
        if (_steps.Count == 0) return;
        int i = Current + 1;
        while (i < _steps.Count && IsSkippable(i)) i++;
        if (i < _steps.Count) Current = i;
    }

    public void Prev()
    {
        if (_steps.Count == 0) return;
        int i = Current - 1;
        while (i >= 0 && IsSkippable(i)) i--;
        if (i >= 0) Current = i;
    }

    // restore a saved position (clamped to the loaded route, snapped off any zone label).
    public void SetCurrent(int index)
    {
        if (_steps.Count == 0) { Current = 0; return; }
        Current = Math.Clamp(index, 0, _steps.Count - 1);
        SnapOffHeader();
    }

    // upcoming steps after current (optionally hiding optional), up to `count`.
    public IEnumerable<FlatStep> Upcoming(int count, bool includeOptional, bool includeLeagueStart = true)
    {
        int taken = 0;
        for (int i = Current + 1; i < _steps.Count && taken < count; i++)
        {
            if (IsHeaderStep(_steps[i])) continue;              // zone labels aren't objectives
            if (!includeOptional && (_steps[i].Model?.Optional ?? false)) continue;
            if (!includeLeagueStart && (_steps[i].Model?.LeagueStart ?? false)) continue;
            taken++;
            yield return _steps[i];
        }
    }

    // `count` steps before current (optionally hiding optional), oldest first so they read
    // top-to-bottom above the current step.
    public IEnumerable<FlatStep> Previous(int count, bool includeOptional, bool includeLeagueStart = true)
    {
        var result = new List<FlatStep>();
        for (int i = Current - 1; i >= 0 && result.Count < count; i--)
        {
            if (IsHeaderStep(_steps[i])) continue;              // zone labels aren't objectives
            if (!includeOptional && (_steps[i].Model?.Optional ?? false)) continue;
            if (!includeLeagueStart && (_steps[i].Model?.LeagueStart ?? false)) continue;
            result.Add(_steps[i]);
        }
        result.Reverse();
        return result;
    }
}
