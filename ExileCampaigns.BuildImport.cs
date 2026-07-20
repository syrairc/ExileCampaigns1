using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCampaigns.Build;
using ExileCore.Shared.Enums;
using ImGuiNET;

namespace ExileCampaigns;

public partial class ExileCampaigns
{
    private enum PairingMode { MergeByMarker, OnePerItemSet, SingleSet }

    // a socket group whose label names none of its own gems (a shopping list or stray note, not a real link).
    // built but held out of the set so the user can review it (see the gems + label) and keep or skip per group.
    private sealed class ImportCluster
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string SetId = "";
        public string SetName = "";
        public string Label = "";
        public List<BuildEntry> Entries = new();
        public bool Keep;
    }

    private static readonly System.Net.Http.HttpClient _http = MakeHttp();

    private static System.Net.Http.HttpClient MakeHttp()
    {
        var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // pobb.in's cloudflare worker rejects an empty/bot user-agent; identify as a browser-ish client
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ExileCampaigns/1.0");
        return h;
    }

    private string _importInput = "";
    private string _importError = "";
    private volatile bool _importFetching;
    private readonly object _importLock = new();
    private string? _importPendingCode;   // handed from the fetch task to the render thread
    private string _importMetaTitle = "";

    private PobBuild? _importBuild;
    private List<BuildSet>? _importPreview;
    private List<string> _importDropped = new();          // gem names not found in the catalog
    private readonly List<string> _importDroppedItems = new();   // gear whose base could not be resolved
    private readonly HashSet<string> _importUnparsed = new();    // set ids whose level range was guessed
    private readonly List<ImportCluster> _importClusters = new();   // reference groups awaiting keep/skip review
    private PairingMode _importMode = PairingMode.MergeByMarker;

    private void BeginImport() => BeginImportFrom(_importInput);

    // a pobb.in link is fetched async; anything else is treated as a raw PoB export code
    private void BeginImportFrom(string raw)
    {
        _importError = "";
        var input = raw.Trim();
        if (input.Length == 0) { _importError = "paste a pobb.in link, or copy a PoB code and use From clipboard"; return; }

        if (input.Contains("pobb.in", StringComparison.OrdinalIgnoreCase))
        {
            var code = ExtractPobbCode(input);
            if (code == null) { _importError = "could not read the pobb.in code from that link"; return; }
            _importFetching = true;
            _ = System.Threading.Tasks.Task.Run(() => FetchPobbin(code));
        }
        else
        {
            _importMetaTitle = "";
            lock (_importLock) _importPendingCode = input;
        }
    }

    // the overlay's ImGui has no clipboard-paste, so read the OS clipboard directly (STA, like DevTree)
    private static string ReadClipboard()
    {
        var text = "";
        var t = new System.Threading.Thread(() =>
        {
            try { text = System.Windows.Forms.Clipboard.GetText(); } catch { }
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();
        return text ?? "";
    }

    private static string? ExtractPobbCode(string input)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            input, @"pobb\.in/([A-Za-z0-9_-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private async System.Threading.Tasks.Task FetchPobbin(string code)
    {
        try
        {
            // /json carries the code + metadata; /raw is dead. the worker 500s intermittently, so retry.
            var url = $"https://pobb.in/{code}/json";
            string? json = null;
            for (int attempt = 0; attempt < 5 && json == null; attempt++)
            {
                if (attempt > 0) await System.Threading.Tasks.Task.Delay(500);
                using var resp = await _http.GetAsync(url);
                if (resp.IsSuccessStatusCode) json = await resp.Content.ReadAsStringAsync();
            }
            if (json == null) throw new Exception("pobb.in kept returning errors");
            var o = Newtonsoft.Json.Linq.JObject.Parse(json);
            var content = (string?)o["content"];
            if (string.IsNullOrEmpty(content)) throw new Exception("pobb.in response had no 'content'");
            var title = (string?)o["metadata"]?["title"] ?? "";
            lock (_importLock) { _importPendingCode = content; _importMetaTitle = title; }
        }
        catch (Exception ex)
        {
            lock (_importLock)
            {
                _importPendingCode = null;
                _importError = $"fetch failed: {ex.Message}. Paste the raw PoB code instead.";
            }
        }
        finally { _importFetching = false; }
    }

    // called each frame from the import UI: pick up a code the fetch task produced, decode + preview it
    private void ConsumeFetchedCode()
    {
        string? code;
        lock (_importLock) { code = _importPendingCode; _importPendingCode = null; }
        if (code == null) return;

        try
        {
            var xml = PobImport.Decode(code);
            _importBuild = PobImport.Parse(xml);
            _importPreview = ToProposal(_importBuild, _importMode, out _importDropped);
            _importError = _importPreview.Count == 0 ? "nothing importable found in that build" : "";
        }
        catch (Exception ex)   // never let a parse/map throw imbalance the settings ImGui stack
        {
            _importBuild = null;
            _importPreview = null;
            _importError = ex is PobImportException ? ex.Message : $"import failed: {ex.Message}";
        }
    }

    // main gear slots only. flasks, jewels and weapon-swap are intentionally excluded (detection ignores them)
    private static readonly string[] GearSlotNames =
    {
        "Weapon 1", "Weapon 2", "Helmet", "Body Armour", "Gloves", "Boots",
        "Belt", "Amulet", "Ring 1", "Ring 2",
    };

    private Dictionary<string, string>? _pobBaseClass;   // normalized base name -> ItemClass
    private Dictionary<string, string>? _pobGemCanon;     // normalized gem name -> catalog display name
    private Dictionary<string, int>? _pobGemReq;          // catalog display name -> required level
    private List<(string Base, string Class)>? _pobBaseList;   // real base names, longest first, for magic-name scan

    private void EnsureCatalogLookups()
    {
        _catalog ??= BuildCatalog.Load(GameController, DirectoryFullName, m => LogError($"ExileCampaigns -> {m}"));
        if (_pobBaseClass != null) return;

        _pobBaseClass = new Dictionary<string, string>();
        _pobGemCanon = new Dictionary<string, string>();
        _pobGemReq = new Dictionary<string, int>();
        foreach (var it in _catalog.Items)
        {
            if (it.IsGem)
            {
                _pobGemCanon[BuildIndex.Normalize(it.Name)] = it.Name;
                _pobGemReq[it.Name] = it.RequiredLevel;
            }
            else
            {
                var key = BuildIndex.Normalize(it.BaseType);
                if (!_pobBaseClass.ContainsKey(key)) _pobBaseClass[key] = it.ItemClass;
            }
        }

        _pobBaseList = _catalog.Items
            .Where(i => !i.IsGem && i.BaseType.Length > 0)
            .Select(i => (i.BaseType, i.ItemClass))
            .Distinct()
            .OrderByDescending(t => t.BaseType.Length)   // longest first so "Chain Gloves" beats "Gloves"
            .ToList();
    }

    private string ResolveClass(string baseName) =>
        _pobBaseClass != null && _pobBaseClass.TryGetValue(BuildIndex.Normalize(baseName), out var c) ? c : "";

    // a magic item's PoB "base" is its whole rolled name ("Healthy Chain Gloves of the Student"). rares/whites
    // give a clean base. resolve the real base by finding the longest catalog base embedded as whole words.
    private (string Base, string Class)? ResolveGearBase(string baseOrDisplay)
    {
        var cls = ResolveClass(baseOrDisplay);
        if (cls.Length > 0) return (baseOrDisplay, cls);       // already a clean base
        if (_pobBaseList == null) return null;
        foreach (var (b, c) in _pobBaseList)
            if (ContainsWholeWord(baseOrDisplay, b)) return (b, c);
        return null;
    }

    private static bool ContainsWholeWord(string haystack, string needle)
    {
        int i = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            bool leftOk = i == 0 || !char.IsLetter(haystack[i - 1]);
            int end = i + needle.Length;
            bool rightOk = end >= haystack.Length || !char.IsLetter(haystack[end]);
            if (leftOk && rightOk) return true;
            i = haystack.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // supports: nameSpec drops the "Support" suffix the in-game base name carries, and a support name often
    // collides with an active gem (Barrage, Blasphemy). try the suffixed form first so a support never binds
    // to the active gem of the same name.
    private string? CanonGem(string name, bool isSupport)
    {
        if (_pobGemCanon == null) return null;
        if (isSupport && _pobGemCanon.TryGetValue(BuildIndex.Normalize(name + " Support"), out var cs)) return cs;
        if (_pobGemCanon.TryGetValue(BuildIndex.Normalize(name), out var c)) return c;
        return null;
    }

    private static ItemRarity MapRarity(string pob) => pob switch
    {
        "UNIQUE" => ItemRarity.Unique,
        "RARE" => ItemRarity.Rare,
        "MAGIC" => ItemRarity.Magic,
        _ => ItemRarity.Normal,
    };

    private static int ClampInto(int req, int min, int max) =>
        req <= 0 ? min : req < min ? min : req > max ? max : req;

    private List<BuildSet> ToProposal(PobBuild b, PairingMode mode, out List<string> droppedGems)
    {
        EnsureCatalogLookups();
        droppedGems = new List<string>();
        _importDroppedItems.Clear();
        _importUnparsed.Clear();
        _importClusters.Clear();
        var sets = new List<BuildSet>();

        if (mode == PairingMode.SingleSet)
        {
            var iset = b.ItemSets.FirstOrDefault(x => x.Id == b.ActiveItemSetId) ?? b.ItemSets.FirstOrDefault();
            var sset = b.SkillSets.FirstOrDefault(x => x.Id == b.ActiveSkillSetId) ?? b.SkillSets.FirstOrDefault();
            var name = string.IsNullOrWhiteSpace(iset?.Title) ? "Imported build" : iset!.Title;
            sets.Add(BuildOneSet(b, name, 1, 100, iset, sset, droppedGems));
            return sets;
        }

        // MergeByMarker and OnePerItemSet both spine off item sets
        foreach (var iset in b.ItemSets)
        {
            var (min, max, parsed) = PobImport.ParseLevelRange(iset.Title);
            var sset = PickSkillSet(b, iset, mode);
            var name = string.IsNullOrWhiteSpace(iset.Title) ? $"Set {sets.Count + 1}" : iset.Title;
            var set = BuildOneSet(b, name, min, max, iset, sset, droppedGems);
            if (!parsed) _importUnparsed.Add(set.Id);
            sets.Add(set);
        }
        return sets;
    }

    private PobSkillSet? PickSkillSet(PobBuild b, PobItemSet iset, PairingMode mode)
    {
        if (mode == PairingMode.MergeByMarker && iset.Markers.Length > 0)
        {
            var best = b.SkillSets
                .Select(s => (set: s, overlap: s.Markers.Count(iset.Markers.Contains)))
                .Where(x => x.overlap > 0)
                .OrderByDescending(x => x.overlap)
                .Select(x => x.set)
                .FirstOrDefault();
            if (best != null) return best;
        }
        return b.SkillSets.FirstOrDefault(x => x.Id == b.ActiveSkillSetId) ?? b.SkillSets.FirstOrDefault();
    }

    private BuildSet BuildOneSet(PobBuild b, string name, int min, int max,
        PobItemSet? iset, PobSkillSet? sset, List<string> droppedGems)
    {
        var set = new BuildSet { Name = name, MinLevel = min, MaxLevel = max };

        if (iset != null)
        {
            var seen = new HashSet<int>();
            foreach (var slot in iset.Slots)
            {
                if (slot.ItemId == 0 || !GearSlotNames.Contains(slot.Slot)) continue;
                if (!seen.Add(slot.ItemId)) continue;   // a 2H fills Weapon 1 and Weapon 2 with one id
                var item = b.Items.FirstOrDefault(x => x.Id == slot.ItemId);
                if (item != null) AddGearEntry(set, item);
            }
        }

        if (sset != null)
            foreach (var grp in sset.Groups)
                AddGroupGems(set, grp, droppedGems);

        return set;
    }

    private void AddGearEntry(BuildSet set, PobItem item)
    {
        var rarity = MapRarity(item.Rarity);
        if (_importUniquesOnly && rarity != ItemRarity.Unique) return;   // magic/rare/white gear skipped by request
        string name, baseType, itemClass;
        if (rarity == ItemRarity.Unique)
        {
            name = item.Name;                          // unique title
            baseType = item.BaseType;
            itemClass = ResolveClass(item.BaseType);
        }
        else
        {
            var resolved = ResolveGearBase(item.BaseType);   // magic names carry no clean base line
            if (resolved == null)
            {
                _importDroppedItems.Add(item.BaseType.Length > 0 ? item.BaseType : item.Name);
                return;                                 // an unresolvable base can never match detection
            }
            baseType = resolved.Value.Base;
            name = baseType;                            // non-unique matches by base, so Name == BaseType
            itemClass = resolved.Value.Class;
        }

        if (string.IsNullOrWhiteSpace(baseType)) return;   // nothing to match on
        set.Entries.Add(new BuildEntry
        {
            Name = name,
            BaseType = baseType,
            ItemClass = itemClass,
            Rarity = rarity,
            Kind = BuildItemKind.Equipment,
            RequiredLevel = item.LevelReq,
            TargetLevel = ClampInto(item.LevelReq, set.MinLevel, set.MaxLevel),
        });
    }

    private void AddGroupGems(BuildSet set, PobLinkGroup grp, List<string> droppedGems)
    {
        var actives = grp.Gems.Where(g => !g.IsSupport).ToList();
        var primaryGem = actives.ElementAtOrDefault(Math.Max(0, grp.MainActive - 1)) ?? actives.FirstOrDefault();

        BuildEntry? primaryEntry = null;
        BuildEntry? firstActiveEntry = null;
        var made = new List<BuildEntry>();

        // actives first so supports can link to a real entry id
        foreach (var g in grp.Gems.Where(g => !g.IsSupport))
        {
            var entry = MakeGemEntry(set, g, droppedGems);
            if (entry == null) continue;
            made.Add(entry);
            firstActiveEntry ??= entry;
            if (ReferenceEquals(g, primaryGem)) primaryEntry = entry;
        }
        primaryEntry ??= firstActiveEntry;

        foreach (var g in grp.Gems.Where(g => g.IsSupport))
        {
            var entry = MakeGemEntry(set, g, droppedGems);
            if (entry == null) continue;
            made.Add(entry);
            entry.LinkedToId = primaryEntry?.Id;   // null tolerated by detection's fallback
        }
        if (made.Count == 0) return;

        // a real group's label (if any) names one of its own gems; a shopping-list or stray-note label names
        // none of them. hold those aside for keep/skip review instead of dropping their gems into the set.
        if (LabelNamesNoGem(grp))
        {
            _importClusters.Add(new ImportCluster
            {
                SetId = set.Id, SetName = set.Name, Label = grp.Label, Entries = made,
            });
            return;
        }

        set.Entries.AddRange(made);

        // the label describes the whole group: note on the primary skill (skip dividers and labels that just
        // repeat the name), optional flags every gem in the group
        if (primaryEntry != null && grp.Label.Length > 0 &&
            !PobImport.IsDivider(grp.Label) && !SameWords(grp.Label, primaryEntry.Name))
            primaryEntry.Note = grp.Label;
        if (grp.Optional)
            foreach (var e in made) e.Optional = true;
    }

    // a label that is just the gem's name in different spacing/case ("Bloodrage" == "Blood Rage") adds nothing
    private static bool SameWords(string a, string b) => Squash(a) == Squash(b);
    private static string Squash(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // a real socket group's label names one of its own gems ("Flame Wall (shoot into wall)"); a shopping-list
    // or stray-note label references none of them ("Click here for Gems to Purchase"). empty label = real.
    private static bool LabelNamesNoGem(PobLinkGroup grp)
    {
        if (grp.Label.Length == 0) return false;
        var label = Squash(grp.Label);
        foreach (var g in grp.Gems)
        {
            var name = Squash(g.Name);
            if (name.Length >= 4 && label.Contains(name)) return false;   // label mentions a gem it holds -> real
        }
        return true;
    }

    private BuildEntry? MakeGemEntry(BuildSet set, PobGem g, List<string> droppedGems)
    {
        var canon = CanonGem(g.Name, g.IsSupport);
        if (canon == null) { droppedGems.Add(g.Name); return null; }
        int req = _pobGemReq != null && _pobGemReq.TryGetValue(canon, out var r) ? r : 0;
        var entry = new BuildEntry
        {
            Name = canon,
            BaseType = canon,
            ItemClass = ResolveClass(canon),
            Rarity = ItemRarity.Gem,
            Kind = BuildItemKind.Gem,
            IsSupport = g.IsSupport,
            RequiredLevel = req,
            TargetLevel = req > 0 ? Clamp(req) : set.MinLevel,   // gem's own required level, not the set floor
        };
        return entry;   // caller decides whether it lands in the set or a held-aside reference cluster
    }

    private bool _importReplace;
    private bool _importUniquesOnly;
    private readonly HashSet<string> _importExclude = new();

    private void DrawImportSection()
    {
        ConsumeFetchedCode();   // must run every frame so a finished fetch is picked up

        if (!ImGui.CollapsingHeader("Import from PoB")) return;

        ImGui.SetNextItemWidth(360);
        // a raw PoB export code is tens of KB, so the buffer must be large or a pasted code gets truncated
        ImGui.InputText("##ec_importinput", ref _importInput, 262144);
        ImGui.SameLine();
        ImGui.BeginDisabled(_importFetching);
        if (ImGui.Button(_importFetching ? "Fetching..." : "Load")) BeginImport();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("From clipboard")) BeginImportFrom(ReadClipboard());
        ImGui.TextDisabled("type a pobb.in link + Load, or copy a PoB code and press From clipboard");

        if (_importError.Length > 0)
            ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.3f, 1f), _importError);

        if (_importPreview == null) { ImGui.Separator(); return; }

        if (_importMetaTitle.Length > 0)
            ImGui.TextDisabled($"Build: {_importMetaTitle}");

        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("Pairing", _importMode.ToString()))
        {
            foreach (PairingMode m in Enum.GetValues<PairingMode>())
                if (ImGui.Selectable(m.ToString(), m == _importMode) && m != _importMode)
                {
                    _importMode = m;
                    _importExclude.Clear();
                    if (_importBuild != null)
                        _importPreview = ToProposal(_importBuild, _importMode, out _importDropped);
                }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.Checkbox("Replace existing plan", ref _importReplace);
        ImGui.SameLine();
        if (ImGui.Checkbox("Unique items only", ref _importUniquesOnly) && _importBuild != null)
            _importPreview = ToProposal(_importBuild, _importMode, out _importDropped);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("skip magic/rare/white gear; gems still import");

        if (ImGui.BeginTable("##ec_importpreview", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
                new Vector2(0, 160)))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 26);
            ImGui.TableSetupColumn("Set", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Min", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Max", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Entries", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            foreach (var set in _importPreview)
            {
                ImGui.PushID(set.Id);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                bool inc = !_importExclude.Contains(set.Id);
                if (ImGui.Checkbox("##inc", ref inc))
                {
                    if (inc) _importExclude.Remove(set.Id); else _importExclude.Add(set.Id);
                }

                ImGui.TableNextColumn();
                var nm = set.Name; ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##nm", ref nm, 64)) set.Name = nm;

                ImGui.TableNextColumn();
                int mn = set.MinLevel; ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##mn", ref mn, 0)) set.MinLevel = Clamp(mn);

                ImGui.TableNextColumn();
                int mx = set.MaxLevel; ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##mx", ref mx, 0)) set.MaxLevel = Clamp(mx);

                ImGui.TableNextColumn();
                ImGui.Text(set.Entries.Count.ToString());

                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        if (_importUnparsed.Count > 0)
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.3f, 1f),
                $"{_importUnparsed.Count} set(s) had no clear level range - check Min/Max before importing");

        if (_importDropped.Count > 0)
            ImGui.TextDisabled($"{_importDropped.Distinct().Count()} gem(s) not recognized, skipped: " +
                               string.Join(", ", _importDropped.Distinct().Take(12)));

        if (_importDroppedItems.Count > 0)
            ImGui.TextDisabled($"{_importDroppedItems.Distinct().Count()} item(s) base not recognized, skipped: " +
                               string.Join(", ", _importDroppedItems.Distinct().Take(8)));

        if (_importClusters.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.3f, 1f),
                $"{_importClusters.Count} group(s) whose label names no gem (shopping lists / notes - usually not real). Tick to import:");
            foreach (var c in _importClusters)
            {
                ImGui.PushID(c.Id);
                bool keep = c.Keep;
                if (ImGui.Checkbox("##keep", ref keep)) c.Keep = keep;
                ImGui.SameLine();
                var gems = string.Join(", ", c.Entries.Select(e => e.Name));
                var trimmed = c.Label.Trim('<', '>', ' ');   // "<<Level 1-20 >>" -> "Level 1-20"
                var lbl = trimmed.Length > 0 ? trimmed : "(no label)";
                ImGui.TextWrapped($"{c.SetName} / {lbl}: {gems}");
                ImGui.PopID();
            }
        }

        if (ImGui.Button("Import", new Vector2(120, 0))) CommitImport();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0))) ResetImport();
        ImGui.Separator();
    }

    private void CommitImport()
    {
        if (_importPreview == null) return;
        var chosen = _importPreview.Where(s => !_importExclude.Contains(s.Id)).ToList();
        if (chosen.Count == 0) { _importError = "no sets selected"; return; }

        // fold kept reference clusters back into their set (only if that set is being imported)
        foreach (var c in _importClusters.Where(c => c.Keep))
            chosen.FirstOrDefault(s => s.Id == c.SetId)?.Entries.AddRange(c.Entries);

        if (_importReplace) _build.Sets.Clear();
        int entries = chosen.Sum(s => s.Entries.Count);
        _build.Sets.AddRange(chosen);
        _selectedSetId = chosen[0].Id;
        SaveBuild();
        ShowToast($"Imported {chosen.Count} set(s), {entries} entrie(s)", ToastLevel.Success);
        ResetImport();
    }

    private void ResetImport()
    {
        _importPreview = null;
        _importBuild = null;
        _importDropped = new List<string>();
        _importDroppedItems.Clear();
        _importUnparsed.Clear();
        _importClusters.Clear();
        _importExclude.Clear();
        _importInput = "";
        _importError = "";
        _importReplace = false;
    }
}
