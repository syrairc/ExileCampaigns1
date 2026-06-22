using System;
using System.Collections.Generic;

namespace ExileCampaigns.Guide;

// mutable in-memory route: CRUD over steps preserving list order. area grouping for display
// is a rendering concern handled by RouteRepository, not here. pure C#.
public sealed class RouteStore
{
    private readonly List<RouteStep> _steps;

    public RouteStore(RouteDocument doc)
    {
        _steps = new List<RouteStep>(doc?.Steps ?? Array.Empty<RouteStep>());
    }

    public IReadOnlyList<RouteStep> Steps => _steps;

    public void Add(RouteStep step) { _steps.Add(step); }

    public bool Update(RouteStep step)
    {
        for (int i = 0; i < _steps.Count; i++)
            if (_steps[i].Id == step.Id) { _steps[i] = step; return true; }
        return false;
    }

    public bool Delete(string id)
    {
        int removed = _steps.RemoveAll(s => s.Id == id);
        return removed > 0;
    }

    public bool Move(string id, int targetIndex)
    {
        int from = _steps.FindIndex(s => s.Id == id);
        if (from < 0) return false;
        var step = _steps[from];
        _steps.RemoveAt(from);
        targetIndex = Math.Clamp(targetIndex, 0, _steps.Count);
        _steps.Insert(targetIndex, step);
        return true;
    }

    // insert immediately before/after the anchor step. false if anchor absent (no insert).
    public bool InsertRelative(string anchorId, RouteStep step, bool before)
    {
        int at = _steps.FindIndex(s => s.Id == anchorId);
        if (at < 0) return false;
        _steps.Insert(before ? at : at + 1, step);
        return true;
    }

    // clone the step at id with a fresh GUID, insert right after it. returns the new id, or null if absent.
    public string? Duplicate(string id)
    {
        int at = _steps.FindIndex(s => s.Id == id);
        if (at < 0) return null;
        var newId = Guid.NewGuid().ToString("N");
        _steps.Insert(at + 1, _steps[at] with { Id = newId });
        return newId;
    }

    // reassigns area metadata in place; does not reorder
    public bool MoveToArea(string id, int act, string areaId, string areaName)
    {
        for (int i = 0; i < _steps.Count; i++)
            if (_steps[i].Id == id)
            {
                _steps[i] = _steps[i] with { Act = act, AreaId = areaId, AreaName = areaName };
                return true;
            }
        return false;
    }

    public RouteDocument ToDocument() => new(RouteDocument.CurrentVersion, new List<RouteStep>(_steps));
}
