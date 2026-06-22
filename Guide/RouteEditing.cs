using System;
using System.Collections.Generic;
using System.Linq;

namespace ExileCampaigns.Guide;

public enum ChildKind { Path, Indicator, MinimapIcon }

// pure edit helpers over the immutable route records. the ImGui editor is a thin caller; all list math and
// record-rebuilding lives here so it's unit-tested without any game state.
public static class RouteEditing
{
    public static RouteStep SkeletonStep(int act, string areaId, string areaName, string? note) =>
        new(Guid.NewGuid().ToString("N"), act, areaId, areaName, "(new step)", note ?? "", false,
            CompleteWhen.All, new List<Objective> { new(ObjectiveType.Manual) }, null);

    public static Objective BlankObjective() => new(ObjectiveType.Manual);

    // triage quick-bind: make the step advance on `flag` via a QuestFlag objective, CompleteWhen.Any.
    // if the step's sole objective is a bare Manual/QuestFlag, retype it in place (guidance children stay,
    // since guidance is decoupled from Type) instead of stacking a second objective; otherwise append one so
    // existing kill/talk/path objectives keep providing guidance.
    public static RouteStep AddQuestFlagObjective(RouteStep step, string flag)
    {
        var pat = new Pattern(flag);
        if (step.Objectives.Count == 1 &&
            step.Objectives[0].Type is ObjectiveType.Manual or ObjectiveType.QuestFlag)
        {
            var only = step.Objectives[0] with { Type = ObjectiveType.QuestFlag, Flag = pat };
            return step with { Objectives = new List<Objective> { only }, CompleteWhen = CompleteWhen.Any };
        }
        var objs = step.Objectives.ToList();
        objs.Add(new Objective(ObjectiveType.QuestFlag, Flag: pat));
        return step with { Objectives = objs, CompleteWhen = CompleteWhen.Any };
    }

    // triage quick-bind: make the step advance on entering `areaId` via an EnterArea objective, CompleteWhen.Any.
    // same in-place-vs-append rule as AddQuestFlagObjective: a sole bare advance objective is retyped (guidance
    // children stay, since guidance is decoupled from Type), otherwise we append so existing objectives keep theirs.
    public static RouteStep AddEnterAreaObjective(RouteStep step, string areaId)
    {
        var pat = new Pattern(areaId);
        if (step.Objectives.Count == 1 &&
            step.Objectives[0].Type is ObjectiveType.Manual or ObjectiveType.QuestFlag or ObjectiveType.EnterArea)
        {
            var only = step.Objectives[0] with { Type = ObjectiveType.EnterArea, AreaTarget = pat, Flag = null };
            return step with { Objectives = new List<Objective> { only }, CompleteWhen = CompleteWhen.Any };
        }
        var objs = step.Objectives.ToList();
        objs.Add(new Objective(ObjectiveType.EnterArea, AreaTarget: pat));
        return step with { Objectives = objs, CompleteWhen = CompleteWhen.Any };
    }

    public static Objective AddPath(Objective o, GuidePath p) => o with { Paths = Append(o.Paths, p) };

    public static Objective AddIndicator(Objective o, Indicator i) => o with { Indicators = Append(o.Indicators, i) };

    public static Objective AddMinimapIcon(Objective o, MinimapIcon icon) => o with { MinimapIcons = Append(o.MinimapIcons, icon) };

    // replace a Path/Indicator's Target in place (e.g. toggle LivingOnly). no-op if out of range.
    public static Objective ReplaceTarget(Objective o, ChildKind kind, int index, Target target)
    {
        if (kind == ChildKind.Path)
        {
            var list = (o.Paths ?? new List<GuidePath>()).ToList();
            if (index < 0 || index >= list.Count) return o;
            list[index] = new GuidePath(target);
            return o with { Paths = list };
        }
        else
        {
            var list = (o.Indicators ?? new List<Indicator>()).ToList();
            if (index < 0 || index >= list.Count) return o;
            list[index] = new Indicator(target);
            return o with { Indicators = list };
        }
    }

    // replace the icon at index in place (sprite/tint/size/target edits). no-op if out of range.
    public static Objective UpdateMinimapIcon(Objective o, int index, MinimapIcon icon)
    {
        var list = (o.MinimapIcons ?? new List<MinimapIcon>()).ToList();
        if (index < 0 || index >= list.Count) return o;
        list[index] = icon;
        return o with { MinimapIcons = list };
    }

    public static Objective RemoveAt(Objective o, ChildKind kind, int index)
    {
        switch (kind)
        {
            case ChildKind.Path:
            {
                var list = (o.Paths ?? new List<GuidePath>()).ToList();
                if (index < 0 || index >= list.Count) return o;
                list.RemoveAt(index);
                return o with { Paths = list };
            }
            case ChildKind.MinimapIcon:
            {
                var list = (o.MinimapIcons ?? new List<MinimapIcon>()).ToList();
                if (index < 0 || index >= list.Count) return o;
                list.RemoveAt(index);
                return o with { MinimapIcons = list };
            }
            default:
            {
                var inds = (o.Indicators ?? new List<Indicator>()).ToList();
                if (index < 0 || index >= inds.Count) return o;
                inds.RemoveAt(index);
                return o with { Indicators = inds };
            }
        }
    }

    public static Objective MoveChild(Objective o, ChildKind kind, int index, int delta)
    {
        switch (kind)
        {
            case ChildKind.Path:
            {
                var list = (o.Paths ?? new List<GuidePath>()).ToList();
                return Move(list, index, delta) ? o with { Paths = list } : o;
            }
            case ChildKind.MinimapIcon:
            {
                var list = (o.MinimapIcons ?? new List<MinimapIcon>()).ToList();
                return Move(list, index, delta) ? o with { MinimapIcons = list } : o;
            }
            default:
            {
                var inds = (o.Indicators ?? new List<Indicator>()).ToList();
                return Move(inds, index, delta) ? o with { Indicators = inds } : o;
            }
        }
    }

    private static List<T> Append<T>(IReadOnlyList<T>? src, T item)
    {
        var list = src != null ? new List<T>(src) : new List<T>();
        list.Add(item);
        return list;
    }

    private static bool Move<T>(List<T> list, int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= list.Count || to < 0 || to >= list.Count) return false;
        (list[index], list[to]) = (list[to], list[index]);
        return true;
    }
}
