using System;
using System.Collections.Generic;
using ExileCore.Shared.Enums;
using System.Text;
using System.IO;
using ExileCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ExileCampaigns.Build;

public enum BuildItemKind { Equipment, Gem }

// one planned gem or item. name is not a unique key: the same support appears once per skill it feeds.
public sealed class BuildEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseType { get; set; } = "";
    public string ItemClass { get; set; } = "";
    public ItemRarity Rarity { get; set; } = ItemRarity.Normal;
    public BuildItemKind Kind { get; set; } = BuildItemKind.Equipment;
    public bool IsSupport { get; set; }
    public string? LinkedToId { get; set; }   // -> a gem entry in the same set. required for supports.
    public int TargetLevel { get; set; } = 1;
    public int RequiredLevel { get; set; }
    public string Note { get; set; } = "";
    public bool Optional { get; set; }        // pob socket-group flagged optional
    public bool Used { get; set; }            // "had": set by auto-detect OR a manual mark. sticky once set
    public bool Equipped { get; set; }        // set ONLY by auto-detect (actually worn/socketed), not manual mark
}

// one level bracket's whole loadout. gear carried across brackets is duplicated per set on purpose.
public sealed class BuildSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New set";
    public int MinLevel { get; set; } = 1;
    public int MaxLevel { get; set; } = 100;
    public List<BuildEntry> Entries { get; set; } = new();
}

// persisted inside the character profile json under the "build" key. gear and skills are independent axes:
// one gear set and one skill set are active at any level, because gear cadence and gem cadence rarely line up.
public sealed class BuildPlan
{
    public int Version { get; set; } = 2;
    public List<BuildSet> GearSets { get; set; } = new();
    public List<BuildSet> SkillSets { get; set; } = new();
    public string? PinnedGearSetId { get; set; }    // null = follow character level
    public string? PinnedSkillSetId { get; set; }
    public string Notes { get; set; } = "";         // raw pob notes, colour codes kept as-is

    // computed, not a real list - keep it out of the saved json or it just doubles the file
    [JsonIgnore]
    public IEnumerable<BuildSet> AllSets => GearSets.Concat(SkillSets);

    public BuildSet? FindSet(string? id) =>
        string.IsNullOrEmpty(id) ? null : AllSets.FirstOrDefault(s => s.Id == id);

    // entry lookup across every set. used to resolve a support's LinkedToId to its skill.
    public BuildEntry? FindEntry(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var s in AllSets)
        {
            var e = s.Entries.Find(x => x.Id == id);
            if (e != null) return e;
        }
        return null;
    }

    // v1 profiles carry one "Sets" list of mixed gear+gem entries. split each by entry kind into a gear set
    // and a skill set on the same bracket, so an old plan keeps resolving exactly as it did. entry ids are
    // preserved so a support's LinkedToId still finds its skill (both are gems, so they land in the same half).
    public static BuildPlan Migrate(JObject? raw)
    {
        // must never throw - the caller can't tell "bad build data" from "bad everything" and reacts by
        // tossing the whole profile load, so a throw here costs the route position too, not just the build.
        try
        {
            return MigrateCore(raw);
        }
        catch
        {
            return new BuildPlan();
        }
    }

    private static BuildPlan MigrateCore(JObject? raw)
    {
        if (raw == null) return new BuildPlan();

        // case-insensitive because the on-disk shape is PascalCase but hand-edited files turn up either way
        var gearTok = raw.GetValue("GearSets", StringComparison.OrdinalIgnoreCase);
        var skillTok = raw.GetValue("SkillSets", StringComparison.OrdinalIgnoreCase);
        if (gearTok != null || skillTok != null)
            return raw.ToObject<BuildPlan>() ?? new BuildPlan();

        var plan = new BuildPlan
        {
            Notes = (string?)raw.GetValue("Notes", StringComparison.OrdinalIgnoreCase) ?? "",
        };
        // check the shape instead of deserializing the whole array in one call - a non-array Sets ({} or a
        // bare string) just isn't a JArray, no throw needed. deserialize per element too, so one hand-typo'd
        // set (a string where a number belongs) can't take every other set in the profile down with it.
        var setsTok = raw.GetValue("Sets", StringComparison.OrdinalIgnoreCase);
        var old = new List<BuildSet>();
        if (setsTok is JArray arr)
            foreach (var t in arr)
            {
                try { if (t.ToObject<BuildSet>() is { } s) old.Add(s); }
                catch (JsonException) { /* one bad set, skip it, keep the rest of the profile */ }
            }

        var pin = (string?)raw.GetValue("ActiveSetOverrideId", StringComparison.OrdinalIgnoreCase);
        foreach (var s in old)
        {
            // complement, not two positive filters - json.net doesn't validate enum range, so a corrupt or
            // future third Kind value must still land somewhere instead of matching neither and vanishing.
            // gear is the safe default side since Equipment is the zero value anyway.
            var gems = s.Entries.Where(e => e.Kind == BuildItemKind.Gem).ToList();
            var gear = s.Entries.Where(e => e.Kind != BuildItemKind.Gem).ToList();

            if (gear.Count > 0)
            {
                var g = new BuildSet { Name = s.Name, MinLevel = s.MinLevel, MaxLevel = s.MaxLevel, Entries = gear };
                plan.GearSets.Add(g);
                if (s.Id == pin) plan.PinnedGearSetId = g.Id;
            }
            if (gems.Count > 0)
            {
                var k = new BuildSet { Name = s.Name, MinLevel = s.MinLevel, MaxLevel = s.MaxLevel, Entries = gems };
                plan.SkillSets.Add(k);
                if (s.Id == pin) plan.PinnedSkillSetId = k.Id;
            }
        }
        return plan;
    }
}

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
        foreach (var set in plan.AllSets)
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

// one pickable thing. RequiredLevel is a prefill hint only, so 0 is fine.
public sealed class CatalogItem
{
    public string Name { get; init; } = "";
    public string BaseType { get; init; } = "";
    public string ItemClass { get; init; } = "";
    public int RequiredLevel { get; init; }
    public bool IsGem { get; init; }
    public bool IsSupport { get; init; }

    // what the search box matches and renders
    public string Label => IsSupport ? $"{Name}  (support)"
        : IsGem ? $"{Name}  (gem)"
        : $"{Name}  ({BaseType})";
}

// pickable gems, uniques and plain bases. built once, lazily: BaseItemTypes is a few thousand entries.
public sealed class BuildCatalog
{
    public IReadOnlyList<CatalogItem> Items { get; }

    private BuildCatalog(List<CatalogItem> items) => Items = items;

    // equippable classes only. unfiltered, BaseItemTypes is mostly currency, maps and quest items.
    private static readonly HashSet<string> EquipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Helmet", "Body Armour", "Gloves", "Boots", "Shield", "Quiver",
        "Amulet", "Ring", "Belt",
        "Claw", "Dagger", "Rune Dagger", "Wand", "One Hand Sword", "Thrusting One Hand Sword",
        "One Hand Axe", "One Hand Mace", "Sceptre", "Bow", "Staff", "Warstaff",
        "Two Hand Sword", "Two Hand Axe", "Two Hand Mace", "Fishing Rod",
    };

    public static BuildCatalog Load(GameController gc, string dataDir, Action<string> logError)
    {
        var items = new List<CatalogItem>();
        var byBaseName = new Dictionary<string, BaseItemTypeInfo>(StringComparer.OrdinalIgnoreCase);

        // plain bases, and a reverse map so uniques can resolve their class from their base name
        try
        {
            foreach (var kv in gc.Files.BaseItemTypes.Contents)
            {
                var bit = kv.Value;
                if (bit == null || string.IsNullOrEmpty(bit.BaseName)) continue;
                if (!EquipClasses.Contains(bit.ClassName ?? "")) continue;

                byBaseName[bit.BaseName] = new BaseItemTypeInfo(bit.ClassName ?? "", bit.DropLevel);
                items.Add(new CatalogItem
                {
                    Name = bit.BaseName,
                    BaseType = bit.BaseName,
                    ItemClass = bit.ClassName ?? "",
                    RequiredLevel = bit.DropLevel,
                });
            }
        }
        catch (Exception ex) { logError($"build catalog: base items failed: {ex.Message}"); }

        // gems. SkillGems has several rows per gem base (alt granted-effects), so dedupe by base name
        try
        {
            var seenGems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dat in gc.Files.SkillGems.EntriesList)
            {
                var bit = dat?.ItemType;
                if (bit == null || string.IsNullOrEmpty(bit.BaseName)) continue;
                if (!seenGems.Add(bit.BaseName)) continue;
                items.Add(new CatalogItem
                {
                    Name = bit.BaseName,
                    BaseType = bit.BaseName,
                    ItemClass = bit.ClassName ?? "",
                    RequiredLevel = bit.DropLevel,
                    IsGem = true,
                    IsSupport = dat!.IsSupportGem,
                });
            }
        }
        catch (Exception ex) { logError($"build catalog: gems failed: {ex.Message}"); }

        // uniques: names cannot be enumerated from memory, so they ship as data
        try
        {
            var path = Path.Combine(dataDir, "Data", "poe1", "uniques.json");
            if (File.Exists(path))
            {
                foreach (var tok in JArray.Parse(File.ReadAllText(path)))
                {
                    var name = (string?)tok["name"] ?? "";
                    var baseName = (string?)tok["base"] ?? "";
                    if (name.Length == 0 || baseName.Length == 0) continue;

                    byBaseName.TryGetValue(baseName, out var info);
                    int req = (int?)tok["req"] ?? 0;
                    if (req == 0) req = info?.DropLevel ?? 0;   // no explicit floor -> fall back to the base

                    items.Add(new CatalogItem
                    {
                        Name = name,
                        BaseType = baseName,
                        ItemClass = info?.ClassName ?? "",
                        RequiredLevel = req,
                    });
                }
            }
            else logError($"build catalog: {path} missing, uniques unavailable in the picker");
        }
        catch (Exception ex) { logError($"build catalog: uniques failed: {ex.Message}"); }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new BuildCatalog(items);
    }

    private sealed record BaseItemTypeInfo(string ClassName, int DropLevel);
}

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
    public string Notes { get; init; } = "";   // raw, colour codes kept. NotesHTML is ignored on purpose
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
            Notes = (root.Element("Notes")?.Value ?? "").Trim(),
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
    // as labels; IsDivider gates whether one becomes a skill note.
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

    // title -> level bracket. Parsed=false means "no range found, caller should default to 1-100 and flag it".
    // strictRange is for skill-set titles: those are usually act names ("Act 4-10") and a bare number range
    // that is act numbers, not levels. gear titles are authored as brackets so they keep the loose rule.
    public static (int Min, int Max, bool Parsed) ParseLevelRange(string title, bool strictRange = false)
    {
        var t = Regex.Replace(title, @"\{[^}]*\}", " ");    // drop stage markers so they aren't read as levels
        var r = Regex.Match(t, strictRange
            ? @"(?:level|lvl|lv)\s*(\d+)\s*(?:-|–|—|to)\s*(\d+)"
            : @"(\d+)\s*(?:-|–|—|to)\s*(\d+)", RegexOptions.IgnoreCase);
        if (r.Success && int.TryParse(r.Groups[1].Value, out var a) && int.TryParse(r.Groups[2].Value, out var b))
            return (Clamp(Math.Min(a, b)), Clamp(Math.Max(a, b)), true);
        var plus = Regex.Match(t, strictRange ? @"(?:level|lvl|lv)\s*(\d+)\s*\+" : @"(\d+)\s*\+",
            RegexOptions.IgnoreCase);
        if (plus.Success && int.TryParse(plus.Groups[1].Value, out var n))
            return (Clamp(n), 100, true);
        // a lone "Level 90" gives a floor but no ceiling. use it as Min (Max stays 100) so the set does not
        // start at 1 and shadow every later bracket; Parsed=false flags it for the user to set the ceiling.
        // "lv" is in the alternation because pob authors write "lv73" far more often than "lvl 73".
        var lone = Regex.Match(t, @"(?:level|lvl|lv)\s*(\d+)", RegexOptions.IgnoreCase);
        if (lone.Success && int.TryParse(lone.Groups[1].Value, out var ln))
            return (Clamp(ln), 100, false);
        return (1, 100, false);
    }

    private static int Clamp(int l) => l < 1 ? 1 : l > 100 ? 100 : l;

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out var n) ? n : fallback;
}
