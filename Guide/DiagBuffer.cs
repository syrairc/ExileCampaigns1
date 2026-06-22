using System;
using System.Collections.Generic;

namespace ExileCampaigns.Guide;

// one recorded diagnostic event. Json is the inner field fragment (no braces), e.g. "area":"g1_2","act":1
public readonly record struct DiagEvent(DateTime Time, string Kind, string Json);

// fixed-cap rolling buffer of recent events. when full, oldest drops off. no ExileCore dependency so it
// unit-tests offline and stays cheap on the game loop (plain list append).
public sealed class DiagBuffer
{
    private readonly List<DiagEvent> _items;
    private readonly int _cap;

    public DiagBuffer(int cap = 300)
    {
        _cap = cap < 1 ? 1 : cap;
        _items = new List<DiagEvent>(_cap);
    }

    public int Count => _items.Count;

    public void Add(DateTime time, string kind, string json)
    {
        _items.Add(new DiagEvent(time, kind, json ?? ""));
        if (_items.Count > _cap)
            _items.RemoveRange(0, _items.Count - _cap);
    }

    public IReadOnlyList<DiagEvent> Snapshot() => _items.ToArray();

    public void Clear() => _items.Clear();
}
