using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ExileCampaigns.Build;

public sealed class PobImportException : Exception
{
    public PobImportException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record PobMeta(string ClassName, string Ascendancy, int Level);
public sealed record PobItem(int Id, string Rarity, string Name, string BaseType, int LevelReq);
public sealed record PobSlot(string Slot, int ItemId);
public sealed record PobGem(string Name, bool IsSupport, int Level);

public sealed class PobItemSet
{
    public string Title { get; init; } = "";
    public int Id { get; init; }
    public int[] Markers { get; init; } = Array.Empty<int>();
    public List<PobSlot> Slots { get; init; } = new();
}

public sealed class PobLinkGroup
{
    public int MainActive { get; init; } = 1;   // 1-based index into this group's active gems
    public string Label { get; init; } = "";    // author's socket-group note, colour codes stripped
    public bool Optional { get; init; }          // label led with "Optional"
    public List<PobGem> Gems { get; init; } = new();
}

public sealed class PobSkillSet
{
    public string Title { get; init; } = "";
    public int Id { get; init; }
    public int[] Markers { get; init; } = Array.Empty<int>();
    public List<PobLinkGroup> Groups { get; init; } = new();
}

public sealed class PobBuild
{
    public PobMeta Meta { get; init; } = new("", "", 1);
    public List<PobItem> Items { get; init; } = new();
    public List<PobItemSet> ItemSets { get; init; } = new();
    public List<PobSkillSet> SkillSets { get; init; } = new();
    public int ActiveItemSetId { get; init; }
    public int ActiveSkillSetId { get; init; }
}

// Turns a Path of Building export (base64 url-safe + zlib XML) into a flat model. BCL only.
public static class PobImport
{
    public static string Decode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new PobImportException("empty PoB code");
        var s = code.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }

        byte[] raw;
        try { raw = Convert.FromBase64String(s); }
        catch (FormatException ex) { throw new PobImportException("not valid base64 - is this a PoB code?", ex); }

        try { return Encoding.UTF8.GetString(Inflate(raw)); }
        catch (Exception ex) { throw new PobImportException("could not decompress the PoB code", ex); }
    }

    // PoB uses zlib (0x78 header); fall back to raw deflate for the odd export
    private static byte[] Inflate(byte[] raw)
    {
        try { return InflateStream(new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress)); }
        catch
        {
            var body = raw.Length > 2 ? raw[2..] : raw;   // skip the 2-byte zlib header for a raw deflate
            return InflateStream(new DeflateStream(new MemoryStream(body), CompressionMode.Decompress));
        }
    }

    private static byte[] InflateStream(Stream s)
    {
        using (s)
        using (var outMs = new MemoryStream())
        {
            s.CopyTo(outMs);
            return outMs.ToArray();
        }
    }

    public static PobBuild Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { throw new PobImportException("PoB payload is not valid XML", ex); }
        var root = doc.Root ?? throw new PobImportException("PoB XML has no root");

        var buildEl = root.Element("Build");
        var meta = new PobMeta(
            (string?)buildEl?.Attribute("className") ?? "",
            (string?)buildEl?.Attribute("ascendClassName") ?? "",
            ParseInt((string?)buildEl?.Attribute("level"), 1));

        var itemsEl = root.Element("Items");

        var items = new List<PobItem>();
        foreach (var it in itemsEl?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            int id = ParseInt((string?)it.Attribute("id"), 0);
            if (id != 0) items.Add(ParseItemText(id, it.Value));
        }

        var itemSets = new List<PobItemSet>();
        foreach (var isEl in itemsEl?.Elements("ItemSet") ?? Enumerable.Empty<XElement>())
        {
            var title = CleanTitle((string?)isEl.Attribute("title") ?? "");
            var slots = isEl.Elements("Slot")
                .Select(sl => new PobSlot((string?)sl.Attribute("name") ?? "",
                                          ParseInt((string?)sl.Attribute("itemId"), 0)))
                .ToList();
            itemSets.Add(new PobItemSet
            {
                Title = title,
                Id = ParseInt((string?)isEl.Attribute("id"), 0),
                Markers = StageMarkers(title),
                Slots = slots,
            });
        }

        var skillsEl = root.Element("Skills");
        var setEls = skillsEl?.Elements("SkillSet").ToList() ?? new List<XElement>();
        // older exports have <Skill> groups directly under <Skills> with no <SkillSet>
        if (setEls.Count == 0 && skillsEl != null) setEls.Add(skillsEl);

        var skillSets = new List<PobSkillSet>();
        foreach (var setEl in setEls)
        {
            var title = CleanTitle((string?)setEl.Attribute("title") ?? "");
            var groups = new List<PobLinkGroup>();
            foreach (var grpEl in setEl.Elements("Skill"))
            {
                var gems = new List<PobGem>();
                foreach (var gEl in grpEl.Elements("Gem"))
                {
                    if (((string?)gEl.Attribute("enabled") ?? "true") == "false") continue;
                    var name = (string?)gEl.Attribute("nameSpec") ?? "";
                    if (name.Length == 0) continue;
                    var gemId = (string?)gEl.Attribute("gemId") ?? "";
                    gems.Add(new PobGem(name,
                        gemId.Contains("SupportGem", StringComparison.OrdinalIgnoreCase),
                        ParseInt((string?)gEl.Attribute("level"), 1)));
                }
                var (label, optional) = CleanLabel((string?)grpEl.Attribute("label") ?? "");
                if (gems.Count > 0)
                    groups.Add(new PobLinkGroup
                    {
                        MainActive = ParseInt((string?)grpEl.Attribute("mainActiveSkill"), 1),
                        Label = label,
                        Optional = optional,
                        Gems = gems,
                    });
            }
            skillSets.Add(new PobSkillSet
            {
                Title = title,
                Id = ParseInt((string?)setEl.Attribute("id"), 0),
                Markers = StageMarkers(title),
                Groups = groups,
            });
        }

        return new PobBuild
        {
            Meta = meta,
            Items = items,
            ItemSets = itemSets,
            SkillSets = skillSets,
            ActiveItemSetId = ParseInt((string?)itemsEl?.Attribute("activeItemSet"), 0),
            ActiveSkillSetId = ParseInt((string?)skillsEl?.Attribute("activeSkillSet"), 0),
        };
    }

    // the item body is the in-game clipboard text: "Rarity: X", then 1-2 header lines (title/base), then Key: value lines
    private static PobItem ParseItemText(int id, string text)
    {
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var rarity = "NORMAL";
        int levelReq = 0;
        var header = new List<string>();
        bool afterRarity = false, headerDone = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase))
            {
                rarity = line[(line.IndexOf(':') + 1)..].Trim().ToUpperInvariant();
                afterRarity = true;
                continue;
            }
            if (line.StartsWith("LevelReq:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), out levelReq);

            if (afterRarity && !headerDone)
            {
                if (IsKeyValue(line)) headerDone = true;      // reached the attribute block
                else header.Add(line);
            }
        }

        var name = header.Count > 0 ? header[0] : "";
        bool twoLine = rarity is "UNIQUE" or "RARE";
        var baseType = twoLine && header.Count >= 2 ? header[1] : name;
        return new PobItem(id, rarity, name, baseType, levelReq);
    }

    private static bool IsKeyValue(string line) => Regex.IsMatch(line, @"^[A-Za-z][A-Za-z0-9 ]*:");

    // "^4Level 1-20 {1}" -> "Level 1-20 {1}"  (strip PoB colour codes)
    private static string CleanTitle(string title) =>
        Regex.Replace(title, @"\^(x[0-9a-fA-F]{6}|\d)", "").Trim();

    // socket-group label: strip colour codes and pull an "Optional" marker out into a flag. dividers are kept
    // (the cluster review shows them to identify a group); IsDivider gates whether one becomes a skill note.
    public static (string Label, bool Optional) CleanLabel(string raw)
    {
        var s = CleanTitle(raw);
        bool optional = Regex.IsMatch(s, @"\boptional\b", RegexOptions.IgnoreCase);
        if (optional)
        {
            s = Regex.Replace(s, @"^optional\b[\s:_\-]*", "", RegexOptions.IgnoreCase);   // leading marker
            s = Regex.Replace(s, @"\s*\(\s*optional\s*\)", "", RegexOptions.IgnoreCase);  // trailing (Optional)
            s = s.Trim();
        }
        return (s, optional);
    }

    // "<< Damage Skills >>" style dividers identify a group but must not be attached as a skill's note
    public static bool IsDivider(string label) => Regex.IsMatch(label, @"^<{1,}.*>{1,}$");

    // the {n} numbers in a title, e.g. "{4,5}" -> [4,5]
    public static int[] StageMarkers(string title)
    {
        var m = Regex.Match(title, @"\{([\d,\s]+)\}");
        if (!m.Success) return Array.Empty<int>();
        return m.Groups[1].Value.Split(',')
            .Select(p => int.TryParse(p.Trim(), out var n) ? n : -1)
            .Where(n => n >= 0).ToArray();
    }

    // title -> level bracket. Parsed=false means "no range found, caller should default to 1-100 and flag it".
    public static (int Min, int Max, bool Parsed) ParseLevelRange(string title)
    {
        var t = Regex.Replace(title, @"\{[^}]*\}", " ");    // drop stage markers so they aren't read as levels
        var r = Regex.Match(t, @"(\d+)\s*(?:-|–|—|to)\s*(\d+)", RegexOptions.IgnoreCase);
        if (r.Success && int.TryParse(r.Groups[1].Value, out var a) && int.TryParse(r.Groups[2].Value, out var b))
            return (Clamp(Math.Min(a, b)), Clamp(Math.Max(a, b)), true);
        var plus = Regex.Match(t, @"(\d+)\s*\+");
        if (plus.Success && int.TryParse(plus.Groups[1].Value, out var n))
            return (Clamp(n), 100, true);
        // a lone "Level 90" gives a floor but no ceiling. use it as Min (Max stays 100) so the set does not
        // start at 1 and shadow every later bracket; Parsed=false flags it for the user to set the ceiling.
        var lone = Regex.Match(t, @"(?:level|lvl)\s*(\d+)", RegexOptions.IgnoreCase);
        if (lone.Success && int.TryParse(lone.Groups[1].Value, out var ln))
            return (Clamp(ln), 100, false);
        return (1, 100, false);
    }

    private static int Clamp(int l) => l < 1 ? 1 : l > 100 ? 100 : l;

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out var n) ? n : fallback;
}
