using System;
using System.Collections.Generic;
using System.Text;

namespace ExileCampaigns.Build;

public sealed record BuildMatch(BuildEntry Entry, BuildSet Set);

// the one answer to "is this in my build?". indexes every set, not just the active one: at level 10 a
// Goldrim for your 13-30 set must still light up in your stash.
public sealed class BuildIndex
{
    private readonly Dictionary<string, List<BuildMatch>> _byName = new();
    private readonly Dictionary<string, List<BuildMatch>> _byBase = new();
    private static readonly IReadOnlyList<BuildMatch> Empty = Array.Empty<BuildMatch>();

    // lowercase, alphanumerics only, so "Added Lightning Damage" == "addedlightningdamage"
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static void Add(Dictionary<string, List<BuildMatch>> map, string key, BuildMatch m)
    {
        if (key.Length == 0) return;
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<BuildMatch>();
        list.Add(m);
    }

    public void Rebuild(BuildPlan plan)
    {
        _byName.Clear();
        _byBase.Clear();
        foreach (var set in plan.Sets)
            foreach (var e in set.Entries)
            {
                var m = new BuildMatch(e, set);
                Add(_byName, Normalize(e.Name), m);

                // only plain bases get a base+class key. a Goldrim indexed under "leathercap|helmet" would
                // claim every white Leather Cap you pick up, since those miss on name and fall through here.
                if (Normalize(e.Name) == Normalize(e.BaseType))
                    Add(_byBase, Normalize(e.BaseType) + "|" + Normalize(e.ItemClass), m);
            }
    }

    // exact name first, then base+class for plain bases. values are lists: the same support name appears
    // once per skill it feeds, so name is not a unique key.
    public IReadOnlyList<BuildMatch> Match(string name, string baseType, string itemClass)
    {
        if (_byName.TryGetValue(Normalize(name), out var byName)) return byName;
        if (_byBase.TryGetValue(Normalize(baseType) + "|" + Normalize(itemClass), out var byBase)) return byBase;
        return Empty;
    }
}
