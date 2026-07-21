using System;
using System.Collections.Generic;
using System.Linq;
using ExileCampaigns.Build;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore;
using ImGuiNET;
using SharpDX;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.Elements;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;
using RectangleF = SharpDX.RectangleF;

namespace ExileCampaigns;

// polls worn gear and its sockets ~1 Hz, sticky-marks matching build entries Used, persists on change.
// PoE1 gems live inside worn items, so one pass covers gear and gems both.
public partial class ExileCampaigns
{
    private readonly BuildIndex _buildIndex = new();
    private DateTime _lastUsedScan;

    // worn slots including weapon swap. flasks excluded.
    private static readonly HashSet<InventoryNameE> EquipSlots = new()
    {
        InventoryNameE.BodyArmour1, InventoryNameE.Weapon1, InventoryNameE.Offhand1,
        InventoryNameE.Helm1, InventoryNameE.Amulet1, InventoryNameE.Ring1, InventoryNameE.Ring2,
        InventoryNameE.Gloves1, InventoryNameE.Boots1, InventoryNameE.Belt1,
        InventoryNameE.Weapon2, InventoryNameE.Offhand2,
    };

    private IReadOnlyList<BuildMatch> MatchBuild(in ItemSnapshot s) =>
        _buildIndex.Match(s.Name, s.BaseType, s.ItemClass);

    // claim the first unclaimed entry in the level set, so a spare copy of a gem still greys the right
    // row out and gear worn at level 5 can't satisfy a pinned 31-55 set (pin is authoring-only, ignored here).
    private static BuildEntry? ClaimUnused(IReadOnlyList<BuildMatch> matches, BuildSet? levelSet, Func<BuildEntry, bool> extra)
    {
        foreach (var m in matches)
            if (m.Set == levelSet && !m.Entry.Used && extra(m.Entry)) return m.Entry;
        return null;
    }

    private void DetectBuildUsed()
    {
        if (_build.Sets.Count == 0) return;
        if ((DateTime.UtcNow - _lastUsedScan).TotalSeconds < 1.0) return;
        _lastUsedScan = DateTime.UtcNow;

        var levelSet = LevelSet();

        try
        {
            var holders = GameController?.IngameState?.ServerData?.PlayerInventories;
            if (holders == null) return;

            bool changed = false;
            foreach (var holder in holders)
            {
                if (holder == null || !EquipSlots.Contains(holder.TypeId)) continue;
                var items = holder.Inventory?.Items;
                if (items == null) continue;

                foreach (var e in items)
                {
                    if (e == null || e.Address == 0 || !e.IsValid) continue;
                    changed |= MarkWornItem(e, levelSet);
                    changed |= MarkSocketedGems(e, levelSet);
                }
            }

            if (changed) SaveProgress();
        }
        catch { /* server data not ready */ }
    }

    private bool MarkWornItem(Entity item, BuildSet? levelSet)
    {
        var snap = ReadItemSnapshot(item);
        if (!snap.Valid || snap.IsGem) return false;

        var claim = ClaimUnused(MatchBuild(snap), levelSet, e => e.Kind == BuildItemKind.Equipment);
        if (claim == null) return false;
        claim.Used = true;
        return true;
    }

    // gems socketed in this item, grouped by link group. a support only counts when it shares a group with
    // the skill its entry names: socketed-but-unlinked stays in the panel, which is the warning.
    private bool MarkSocketedGems(Entity item, BuildSet? levelSet)
    {
        if (!item.TryGetComponent<Sockets>(out var sockets) || sockets?.SocketInfo == null) return false;

        var groups = new Dictionary<int, List<ItemSnapshot>>();
        foreach (var socket in sockets.SocketInfo)
        {
            var gem = socket?.SocketedGemEntity;
            if (gem == null || gem.Address == 0 || !gem.IsValid) continue;
            var snap = ReadItemSnapshot(gem);
            if (!snap.Valid || !snap.IsGem) continue;

            if (!groups.TryGetValue(socket!.LinkGroup, out var list))
                groups[socket.LinkGroup] = list = new List<ItemSnapshot>();
            list.Add(snap);
        }

        bool changed = false;
        foreach (var group in groups.Values)
        {
            var skills = group.Where(g => !g.IsSupport).ToList();

            foreach (var snap in group)
            {
                if (!snap.IsSupport)
                {
                    var claim = ClaimUnused(MatchBuild(snap), levelSet, e => e.Kind == BuildItemKind.Gem && !e.IsSupport);
                    if (claim != null) { claim.Used = true; changed = true; }
                    continue;
                }

                // support: needs an entry whose LinkedToId names a skill sitting in this same link group
                var matches = MatchBuild(snap);
                BuildEntry? support = null;
                foreach (var skill in skills)
                {
                    var skillKey = BuildIndex.Normalize(skill.Name);
                    if (skillKey.Length == 0) continue; // empty name would false-match any unlinked support
                    support = ClaimUnused(matches, levelSet, e =>
                        e.Kind == BuildItemKind.Gem && e.IsSupport &&
                        BuildIndex.Normalize(_build.FindEntry(e.LinkedToId)?.Name) == skillKey);
                    if (support != null) break;
                }

                // unreachable from the editor, but hand-edited json and a future PoB import can leave a
                // support unlinked or pointed at a deleted entry. an entry that can never grey out is worse
                // than this fallback.
                support ??= ClaimUnused(matches, levelSet, e => e.Kind == BuildItemKind.Gem && e.IsSupport && _build.FindEntry(e.LinkedToId) == null);

                if (support != null) { support.Used = true; changed = true; }
            }
        }

        return changed;
    }
}

// Build tab: level-bracketed sets on the left, that set's entries on the right.
public partial class ExileCampaigns
{
    private string? _selectedSetId;

    private BuildSet? SelectedSet =>
        _build.FindSet(_selectedSetId) ?? _build.Sets.FirstOrDefault();

    // every build mutation goes through here: persist and re-index in one place
    private void SaveBuild()
    {
        _buildIndex.Rebuild(_build);
        SaveProgress();
    }

    private void DrawBuildTab()
    {
        // pin a set so you can author the 31-55 loadout at level 4
        var active = ActiveSet();
        var pinned = _build.FindSet(_build.ActiveSetOverrideId);
        ImGui.SetNextItemWidth(240);
        if (ImGui.BeginCombo("Active set", pinned?.Name ?? $"auto ({active?.Name ?? "none"})"))
        {
            if (ImGui.Selectable("auto (follow level)", pinned == null))
            {
                _build.ActiveSetOverrideId = null;
                SaveBuild();
            }
            foreach (var s in _build.Sets)
            {
                ImGui.PushID(s.Id);
                if (ImGui.Selectable(s.Name, s.Id == _build.ActiveSetOverrideId))
                {
                    _build.ActiveSetOverrideId = s.Id;
                    SaveBuild();
                }
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        ImGui.Separator();

        DrawImportSection();

        DrawSetList();
        ImGui.SameLine();
        DrawSetDetail();
    }

    private void DrawSetList()
    {
        ImGui.BeginChild("##ec_setlist", new Vector2(220, 320), ImGuiChildFlags.Border);

        if (ImGui.Button("Add set", new Vector2(-1, 0)))
        {
            var set = new BuildSet { Name = "New set" };
            _build.Sets.Add(set);
            _selectedSetId = set.Id;
            SaveBuild();
        }
        ImGui.Separator();

        foreach (var set in _build.Sets.ToList())
        {
            ImGui.PushID(set.Id);
            bool selected = set.Id == SelectedSet?.Id;
            if (ImGui.Selectable($"{set.Name}  ({set.MinLevel}-{set.MaxLevel})", selected))
                _selectedSetId = set.Id;
            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    private void DrawSetDetail()
    {
        var set = SelectedSet;
        ImGui.BeginChild("##ec_setdetail", new Vector2(0, 320), ImGuiChildFlags.Border);

        if (set == null)
        {
            ImGui.TextDisabled("No sets yet. Add one on the left.");
            ImGui.EndChild();
            return;
        }

        ImGui.PushID(set.Id);

        var name = set.Name;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("Name", ref name, 64)) { set.Name = name; SaveBuild(); }

        int min = set.MinLevel, max = set.MaxLevel;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Min level", ref min)) { set.MinLevel = Clamp(min); SaveBuild(); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Max level", ref max)) { set.MaxLevel = Clamp(max); SaveBuild(); }

        ImGui.Separator();

        if (ImGui.Button("Duplicate set"))
        {
            var copy = CloneSet(set);
            _build.Sets.Add(copy);
            _selectedSetId = copy.Id;
            SaveBuild();
            ShowToast($"Duplicated {set.Name}", ToastLevel.Success);
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete set"))
        {
            _build.Sets.Remove(set);
            _selectedSetId = null;
            SaveBuild();
        }

        ImGui.Separator();
        DrawPicker(set);

        ImGui.Separator();
        DrawEntryTable(set);

        ImGui.PopID();
        ImGui.EndChild();
    }

    private BuildCatalog? _catalog;
    private string _pickerInput = "";
    private CatalogItem? _pickerSelection;

    // add-by-name. the catalog is built on first use: BaseItemTypes is thousands of rows and the game
    // files are not ready at plugin init anyway. self-contained filter + combo: ExileCore's SearchCombobox
    // rebuilds dictionaries over the whole ~2000-item catalog every frame and hangs, so we don't use it.
    private void DrawPicker(BuildSet set)
    {
        _catalog ??= BuildCatalog.Load(GameController, DirectoryFullName, m => LogError($"ExileCampaigns -> {m}"));
        if (_catalog.Items.Count == 0) { ImGui.TextDisabled("item catalog unavailable"); return; }

        ImGui.SetNextItemWidth(300);
        if (ImGui.BeginCombo("##ec_picker", _pickerSelection?.Label ?? "<pick an item>"))
        {
            // filter lives inside the popup: type to narrow, focus grabbed on open
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(290);
            ImGui.InputText("##ec_pickerfilter", ref _pickerInput, 64);

            var matches = _catalog.Items.Where(it => PickerMatch(it.Label, _pickerInput)).Take(50).ToList();
            for (int i = 0; i < matches.Count; i++)
            {
                ImGui.PushID(i);
                if (ImGui.Selectable(matches[i].Label, ReferenceEquals(matches[i], _pickerSelection)))
                    _pickerSelection = matches[i];
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Add by name") && _pickerSelection != null)
        {
            _pendingItem = default;
            _pendingPick = _pickerSelection;
            _dialogLevel = PrefillLevel(set, _pickerSelection.RequiredLevel);
            _dialogNote = "";
            _pendingLinkId = null;
            _openBuildPopup = true;
        }
    }

    // all whitespace-separated tokens must appear in the label (case-insensitive)
    private static bool PickerMatch(string label, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        foreach (var tok in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (label.IndexOf(tok, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    private static int Clamp(int level) => level < 1 ? 1 : level > 100 ? 100 : level;

    // deep copy with fresh ids, remapping LinkedToId so a duplicated set's supports point at its own skills
    private static BuildSet CloneSet(BuildSet src)
    {
        var copy = new BuildSet
        {
            Name = src.Name + " copy",
            MinLevel = src.MinLevel,
            MaxLevel = src.MaxLevel,
        };

        var idMap = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var e in src.Entries)
        {
            var clone = new BuildEntry
            {
                Name = e.Name,
                BaseType = e.BaseType,
                ItemClass = e.ItemClass,
                Rarity = e.Rarity,
                Kind = e.Kind,
                IsSupport = e.IsSupport,
                TargetLevel = e.TargetLevel,
                RequiredLevel = e.RequiredLevel,
                Note = e.Note,
                Optional = e.Optional,
                Used = false,                 // a fresh bracket is not equipped yet
            };
            idMap[e.Id] = clone.Id;
            copy.Entries.Add(clone);
        }

        // second pass: links can only be remapped once every clone has an id
        for (int i = 0; i < src.Entries.Count; i++)
        {
            var srcLink = src.Entries[i].LinkedToId;
            if (srcLink != null && idMap.TryGetValue(srcLink, out var newId))
                copy.Entries[i].LinkedToId = newId;
        }

        return copy;
    }

    private ItemSnapshot _pendingItem;
    private bool _openBuildPopup;
    private int _dialogLevel;
    private string _dialogNote = "";
    private bool _focusDialogLevel;
    private const string BuildPopupId = "Add to Build##ec_addbuild";

    // hotkey: snapshot what is under the cursor and request the popup
    private void OnAddBuildItemPressed()
    {
        var set = SelectedSet;
        if (set == null)
        {
            ShowToast("No build set selected - add one in the Build tab", ToastLevel.Warning);
            return;
        }

        var snap = TryCaptureHoveredItem();
        if (!snap.Valid)
        {
            ShowToast("No item hovered to add to build", ToastLevel.Warning);
            return;
        }

        _pendingItem = snap;
        _dialogLevel = PrefillLevel(set, snap.RequiredLevel);
        _dialogNote = "";
        _pendingPick = null;
        _pendingLinkId = null;
        _openBuildPopup = true;
    }

    // the set says which loadout, the entry says when within it
    private int PrefillLevel(BuildSet set, int requiredLevel) =>
        Clamp(requiredLevel > set.MinLevel ? requiredLevel : set.MinLevel);

    private CatalogItem? _pendingPick;    // set when the add came from the picker instead of a hover
    private string? _pendingLinkId;

    // the two add paths converge here so the dialog does not care where the item came from
    private (string Name, string BaseType, string ItemClass, ItemRarity Rarity, bool IsGem, bool IsSupport, int ReqLevel, bool Valid) PendingFields()
    {
        if (_pendingPick != null)
            return (_pendingPick.Name, _pendingPick.BaseType, _pendingPick.ItemClass,
                ItemRarity.Normal, _pendingPick.IsGem, _pendingPick.IsSupport, _pendingPick.RequiredLevel, true);
        if (_pendingItem.Valid)
            return (_pendingItem.Name, _pendingItem.BaseType, _pendingItem.ItemClass,
                _pendingItem.Rarity, _pendingItem.IsGem, _pendingItem.IsSupport, _pendingItem.RequiredLevel, true);
        return ("", "", "", ItemRarity.Normal, false, false, 0, false);
    }

    // called from Render, not from the settings tab: the popup must work while settings are closed
    private void DrawBuildDialog()
    {
        if (_openBuildPopup)
        {
            ImGui.OpenPopup(BuildPopupId);
            _openBuildPopup = false;
            _focusDialogLevel = true;
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        bool open = true;
        if (!ImGui.BeginPopupModal(BuildPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var set = SelectedSet;
        var f = PendingFields();
        if (set == null || !f.Valid) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }

        ImGui.Text(f.Name);
        var kind = f.IsSupport ? "support gem" : f.IsGem ? "skill gem" : f.ItemClass;
        ImGui.TextDisabled($"{f.BaseType}  |  {kind}");
        ImGui.TextDisabled($"into set: {set.Name} ({set.MinLevel}-{set.MaxLevel})");
        ImGui.Separator();

        if (_focusDialogLevel) { ImGui.SetKeyboardFocusHere(); _focusDialogLevel = false; }
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Target level", ref _dialogLevel);
        _dialogLevel = Clamp(_dialogLevel);

        // a support is only ever "used" once it shares a link group with its skill, so the link is required
        if (f.IsSupport)
        {
            var skills = set.Entries.Where(e => e.Kind == BuildItemKind.Gem && !e.IsSupport).ToList();
            if (skills.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.3f, 1f), "Add a skill gem to this set first.");
            }
            else
            {
                var linked = _build.FindEntry(_pendingLinkId);
                ImGui.SetNextItemWidth(240);
                if (ImGui.BeginCombo("Supports", linked?.Name ?? "<pick a skill>"))
                {
                    foreach (var s in skills)
                    {
                        ImGui.PushID(s.Id);
                        if (ImGui.Selectable(s.Name, s.Id == _pendingLinkId)) _pendingLinkId = s.Id;
                        ImGui.PopID();
                    }
                    ImGui.EndCombo();
                }
            }
        }

        ImGui.SetNextItemWidth(320);
        ImGui.InputText("Note", ref _dialogNote, 128);

        ImGui.Separator();

        bool blocked = f.IsSupport && _pendingLinkId == null;
        ImGui.BeginDisabled(blocked);
        if (ImGui.Button("Add", new Vector2(90, 0)))
        {
            AddPending(set, f);
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        if (blocked)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("pick the skill it supports");
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(90, 0)))
        {
            // clear pending state too, same as Add - don't rely on the next opener to reset it
            _pendingPick = null;
            _pendingItem = default;
            _pendingLinkId = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void AddPending(BuildSet set,
        (string Name, string BaseType, string ItemClass, ItemRarity Rarity, bool IsGem, bool IsSupport, int ReqLevel, bool Valid) f)
    {
        if (!f.Valid) return;
        set.Entries.Add(new BuildEntry
        {
            Name = f.Name,
            BaseType = f.BaseType,
            ItemClass = f.ItemClass,
            Rarity = f.Rarity,
            Kind = f.IsGem ? BuildItemKind.Gem : BuildItemKind.Equipment,
            IsSupport = f.IsSupport,
            LinkedToId = f.IsSupport ? _pendingLinkId : null,
            TargetLevel = _dialogLevel,
            RequiredLevel = f.ReqLevel,
            Note = _dialogNote.Trim(),
        });
        _pendingPick = null;
        _pendingItem = default;
        _pendingLinkId = null;
        SaveBuild();
        ShowToast($"Added {f.Name} @ Lvl {_dialogLevel}", ToastLevel.Success);
    }

    // entry table for the selected set. per-row PushID: without it every row's widgets share an id and
    // editing one row edits another.
    private void DrawEntryTable(BuildSet set)
    {
        if (set.Entries.Count == 0)
        {
            ImGui.TextDisabled("  (no entries - hover an item and press the add key)");
            return;
        }

        if (!ImGui.BeginTable("##ec_entries", 7,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
                new Vector2(0, 220)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Opt", ImGuiTableColumnFlags.WidthFixed, 34);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableHeadersRow();

        BuildEntry? remove = null;
        foreach (var e in set.Entries.OrderBy(x => x.TargetLevel).ToList())
        {
            ImGui.PushID(e.Id);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var linkedTo = _build.FindEntry(e.LinkedToId);
            var label = linkedTo != null ? $"{e.Name}  [{linkedTo.Name}]" : e.Name;
            if (e.Used) ImGui.TextDisabled(label); else ImGui.Text(label);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(e.IsSupport ? "support" : e.Kind == BuildItemKind.Gem ? "gem" : "item");

            ImGui.TableNextColumn();
            int lvl = e.TargetLevel;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##lvl", ref lvl, 0)) { e.TargetLevel = Clamp(lvl); SaveBuild(); }

            ImGui.TableNextColumn();
            var note = e.Note;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##note", ref note, 128)) { e.Note = note; SaveBuild(); }

            ImGui.TableNextColumn();
            bool have = e.Used;
            // manual "I've got this" - hides it from the overlay. detector still re-marks anything
            // actually worn, so unchecking a truly-equipped item won't stick.
            if (ImGui.Checkbox("##have", ref have)) { e.Used = have; SaveBuild(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("mark as equipped / owned");

            ImGui.TableNextColumn();
            bool opt = e.Optional;
            if (ImGui.Checkbox("##opt", ref opt)) { e.Optional = opt; SaveBuild(); }

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("X")) remove = e;

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (remove != null)
        {
            // clear any support's link back to it so a deleted skill doesn't leave a dangling LinkedToId
            foreach (var e in set.Entries) if (e.LinkedToId == remove.Id) e.LinkedToId = null;
            set.Entries.Remove(remove);
            SaveBuild();
        }
    }
}

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

// corner markers on inventory items in the build, and outlines on quest rewards in the build. both route
// through BuildIndex, so a reward and an inventory item can never disagree about what is planned.
public partial class ExileCampaigns
{
    private Color IndicatorColor(BuildEntry e)
    {
        var s = Settings.BuildIndicators;
        if (e.Used) return s.UsedColor.Value;
        int delta = e.TargetLevel - _playerLevel;
        if (delta <= 0) return s.EquippableColor.Value;
        if (delta <= s.SoonWindow.Value) return s.SoonColor.Value;
        return s.LaterColor.Value;
    }

    // one gem in the bag can serve several planned copies: the soonest need wins
    private static BuildEntry? Best(IReadOnlyList<BuildMatch> matches)
    {
        BuildEntry? best = null;
        foreach (var m in matches)
        {
            if (m.Entry.Used) continue;
            if (best == null || m.Entry.TargetLevel < best.TargetLevel) best = m.Entry;
        }
        return best ?? (matches.Count > 0 ? matches[0].Entry : null);
    }

    private void DrawBuildIndicators()
    {
        if (!Settings.BuildIndicators.Enable || _build.Sets.Count == 0) return;
        if (Settings.BuildIndicators.MarkInventory) DrawInventoryMarkers();

        if (Settings.BuildIndicators.HighlightQuestRewards)
        {
            var rewards = GameController?.IngameState?.IngameUi?.QuestRewardWindow;
            if (rewards is { IsVisible: true })
            {
                DrawWindowItemHighlights(rewards);
                DrawWindowItemTooltip(rewards);
            }
        }

        if (Settings.BuildIndicators.HighlightVendorItems)
        {
            var vendor = GameController?.IngameState?.IngameUi?.PurchaseWindow;
            if (vendor is { IsVisible: true })
            {
                DrawWindowItemHighlights(vendor);
                DrawWindowItemTooltip(vendor);
            }
        }

        // stash: one path for gems tab + normal tabs, both render items as InventoryItem leaves under StashElement
        if (Settings.BuildIndicators.HighlightStashItems)
        {
            var stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (stash is { IsVisible: true })
            {
                DrawWindowItemHighlights(stash);
                DrawWindowItemTooltip(stash);
            }
        }
    }

    private void DrawInventoryMarkers()
    {
        try
        {
            var panel = GameController?.IngameState?.IngameUi?.InventoryPanel;
            if (panel is not { IsVisible: true }) return;

            var items = panel[InventoryIndex.PlayerInventory]?.VisibleInventoryItems;
            if (items == null) return;

            var dl = ImGui.GetForegroundDrawList();
            float sz = Settings.BuildIndicators.Size.Value;
            uint outline = U32(new Color(10, 10, 12, 220));   // SharpDX.Color is RGBA

            foreach (var item in items)
            {
                var e = item?.Item;
                if (e == null || e.Address == 0 || !e.IsValid) continue;

                var snap = ReadItemSnapshot(e);
                if (!snap.Valid) continue;
                var entry = Best(MatchBuild(snap));
                if (entry == null) continue;

                var rect = item!.GetClientRectCache;
                float right = rect.X + rect.Width;
                float top = rect.Y;

                // top-right corner triangle
                var p1 = new Vector2(right - sz, top);
                var p2 = new Vector2(right, top);
                var p3 = new Vector2(right, top + sz);
                dl.AddTriangleFilled(p1, p2, p3, U32(IndicatorColor(entry)));
                dl.AddTriangle(p1, p2, p3, outline, 1.5f);
            }
        }
        catch { /* inventory mid-teardown */ }
    }

    // outline in-build InventoryItem leaves under a window (quest reward, vendor, later stash). walk the
    // subtree for InventoryItem leaves and read their .Entity: GetPossibleRewards() hands back junk entities
    // (address 7) in this ExileAPI build. items not in the build are left alone, not dimmed: still readable.
    private void DrawWindowItemHighlights(Element window)
    {
        try
        {
            var dl = ImGui.GetForegroundDrawList();
            foreach (var element in WindowItemElements(window))
            {
                var snap = ReadItemSnapshot(element.Entity);
                if (!snap.Valid) continue;
                var entry = Best(MatchBuild(snap));
                if (entry == null) continue;

                var r = element.GetClientRect();
                var min = new Vector2(r.X - 2, r.Y - 2);
                var max = new Vector2(r.X + r.Width + 2, r.Y + r.Height + 2);
                dl.AddRect(min, max, U32(IndicatorColor(entry)), 0f, ImDrawFlags.None, 3f);
            }
        }
        catch { /* window mid-teardown */ }
    }

    // reward/vendor/stash icons sit several levels deep as InventoryItem elements; Element.Entity only reads
    // a real item on those. iterative walk, no dat lookup needed.
    private static IEnumerable<Element> WindowItemElements(Element root)
    {
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var el = stack.Pop();
            if (el == null || el.Address == 0) continue;
            if (el.Type == ElementType.InventoryItem) { yield return el; continue; }
            var kids = el.Children;
            if (kids == null) continue;
            foreach (var k in kids) stack.Push(k);
        }
    }

    // hovering a highlighted item shows which set(s) plan it and at what level. UIHover gives the exact item
    // under the cursor, so no rect hit-testing. one line per set: "usable Lv5  -  Leveling (1-20)".
    private void DrawWindowItemTooltip(Element window)
    {
        try
        {
            if (window is not { IsVisible: true }) return;

            var uiHover = GameController?.Game?.IngameState?.UIHover;
            if (uiHover == null || uiHover.Address == 0) return;
            var hovered = uiHover.AsObject<NormalInventoryItem>();
            if (hovered == null || hovered.Address == 0) return;

            var snap = ReadItemSnapshot(hovered.Item);
            if (!snap.Valid) return;
            var matches = MatchBuild(snap);
            if (matches.Count == 0) return;   // not in the build, so not highlighted either

            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Ascii(snap.Name));
            var seen = new HashSet<string>();
            foreach (var m in matches)
            {
                if (!seen.Add(m.Set.Id)) continue;   // supports feed several skills; collapse to one line per set
                int req = m.Entry.RequiredLevel > 0 ? m.Entry.RequiredLevel : snap.RequiredLevel;
                string flag = m.Entry.Used ? "  [have]" : m.Entry.Optional ? "  [optional]" : "";
                ImGui.TextUnformatted(Ascii($"usable Lv{req}  -  {m.Set.Name} ({m.Set.MinLevel}-{m.Set.MaxLevel}){flag}"));
            }
            ImGui.EndTooltip();
        }
        catch { /* hover/window mid-teardown */ }
    }
}

// build overlay: from the active set, what you can equip now and what unlocks next. reads the build
// directly, ignoring route steps. used entries drop off.
public partial class ExileCampaigns
{
    // first set whose range covers the character, no pin. overlaps are first-wins by design: validating
    // ranges would buy a modal error in exchange for nothing.
    private BuildSet? LevelSet() =>
        _build.Sets.FirstOrDefault(s => _playerLevel >= s.MinLevel && _playerLevel <= s.MaxLevel);

    // pinned set wins (authoring aid, for editing a bracket you haven't reached yet), else the level set.
    private BuildSet? ActiveSet() => _build.FindSet(_build.ActiveSetOverrideId) ?? LevelSet();

    // ctrl-click an overlay row to mark it equipped/owned, same one-way flag the editor checkbox sets.
    private void MarkEntryHave(BuildEntry e)
    {
        e.Used = true;
        SaveBuild();
        ShowToast($"Marked {e.Name} as have", ToastLevel.Success);
    }

    // availability row colours (redesigned 4a, semantic - hardcoded like the other signal colours).
    private static readonly Color BuildNow    = new Color(120, 210, 120, 255);   // usable now
    private static readonly Color BuildFuture = new Color(135, 146, 168, 255);   // #8792A8 unlocks later
    private static readonly Color BuildHave   = new Color(107, 119, 147, 255);   // #6B7793 equipped/dim

    // redesigned 4a: supports grouped under their parent skill (regardless of level order), the parent skill
    // always shown even when equipped. Num carries availability (now / have / L{level}); ctrl-click marks have.
    private List<PanelLine> BuildPanelLines(OverlayStyle s)
    {
        var set = ActiveSet();
        var setIdx = set == null ? 0 : _build.Sets.IndexOf(set) + 1;
        var header = set == null ? "Build" : $"Build - {set.Name}";
        var lines = new List<PanelLine>
        {
            new PanelLine(header, s.HeaderColor.Value, isHeader: true, right: setIdx > 0 ? $"{{{setIdx}}}" : null),
        };

        if (_build.Sets.Count == 0)
        {
            lines.Add(new PanelLine("  (no build sets - add one in the Build tab)", s.OptionalColor.Value));
            return lines;
        }
        if (set == null)
        {
            lines.Add(new PanelLine($"  (no set for level {_playerLevel})", s.OptionalColor.Value));
            return lines;
        }

        // one row for a gem/item: availability in Num, colour by state, "+ " + indent for supports.
        PanelLine Row(BuildEntry e, bool support)
        {
            string num = e.Used ? "have" : e.TargetLevel <= _playerLevel ? "now" : $"L{e.TargetLevel}";
            string name = (support ? "+ " : "") + e.Name;
            if (e.Used) name += "  (equipped)";
            if (e.Optional) name += "  (optional)";
            var col = e.Optional ? s.OptionalColor.Value
                : e.Used ? BuildHave
                : e.TargetLevel <= _playerLevel ? BuildNow
                : BuildFuture;
            return new PanelLine(name, col, num: num, indent: support ? 1 : 0, onCtrlClick: () => MarkEntryHave(e));
        }

        var skills = set.Entries.Where(e => e.Kind == BuildItemKind.Gem && !e.IsSupport).ToList();
        var supports = set.Entries.Where(e => e.Kind == BuildItemKind.Gem && e.IsSupport).ToList();
        var items = set.Entries.Where(e => e.Kind == BuildItemKind.Equipment).ToList();

        // each skill in declared order, then the supports that link to it (a support feeding several skills
        // repeats under each). orphan supports (no resolvable parent) list once at the end so they aren't lost.
        foreach (var skill in skills)
        {
            lines.Add(Row(skill, support: false));
            foreach (var sup in supports.Where(su => su.LinkedToId == skill.Id))
                lines.Add(Row(sup, support: true));
        }
        foreach (var sup in supports.Where(su => _build.FindEntry(su.LinkedToId) == null))
            lines.Add(Row(sup, support: true));

        // gear (armour/weapons/uniques) after the gem groups, same availability rules.
        foreach (var it in items)
            lines.Add(Row(it, support: false));

        if (set.Entries.Count == 0)
            lines.Add(new PanelLine("  (set is empty)", s.OptionalColor.Value));

        return lines;
    }
}

// reads an item entity into a small comparable struct. every read is wrapped, so a stale or half-written
// item just yields Valid=false rather than throwing into the render loop.
public partial class ExileCampaigns
{
    internal readonly struct ItemSnapshot
    {
        public readonly string Name;
        public readonly string BaseType;
        public readonly string ItemClass;
        public readonly ItemRarity Rarity;
        public readonly int RequiredLevel;
        public readonly bool IsGem;
        public readonly bool IsSupport;
        public readonly bool Valid;

        public ItemSnapshot(string name, string baseType, string itemClass, ItemRarity rarity,
            int requiredLevel, bool isGem, bool isSupport)
        {
            Name = name;
            BaseType = baseType;
            ItemClass = itemClass;
            Rarity = rarity;
            RequiredLevel = requiredLevel;
            IsGem = isGem;
            IsSupport = isSupport;
            Valid = true;
        }
    }

    private ItemSnapshot ReadItemSnapshot(Entity? entity)
    {
        try
        {
            if (entity == null || entity.Address == 0 || !entity.IsValid) return default;

            var bit = GameController!.Files.BaseItemTypes.Translate(entity.Path);
            var baseType = bit?.BaseName ?? "";
            var itemClass = bit?.ClassName ?? "";

            // name preference: unique title -> Base.Name -> base type
            string name = baseType;
            int reqLvl = 0;
            var rarity = ItemRarity.Normal;
            if (entity.TryGetComponent<Mods>(out var mods) && mods != null)
            {
                reqLvl = mods.RequiredLevel;
                rarity = mods.ItemRarity;
                if (!string.IsNullOrEmpty(mods.UniqueName)) name = mods.UniqueName;
            }
            if (name == baseType && entity.TryGetComponent<Base>(out var b) && b != null
                && !string.IsNullOrEmpty(b.Name))
                name = b.Name;

            bool isGem = false, isSupport = false;
            if (entity.TryGetComponent<SkillGem>(out var gem) && gem != null)
            {
                isGem = true;
                if (gem.RequiredLevel > 0) reqLvl = gem.RequiredLevel;
                isSupport = gem.SkillGemDat?.IsSupportGem ?? false;
            }

            return new ItemSnapshot(name, baseType, itemClass, rarity, reqLvl, isGem, isSupport);
        }
        catch { return default; }
    }

    private ItemSnapshot TryCaptureHoveredItem()
    {
        try
        {
            var uiHover = GameController?.Game?.IngameState?.UIHover;
            if (uiHover == null || uiHover.Address == 0) return default;

            var icon = uiHover.AsObject<HoverItemIcon>();
            if (icon == null || icon.Address == 0) return default;
            if (icon.ToolTipType is ToolTipType.ItemInChat or ToolTipType.None) return default;

            var entity = uiHover.AsObject<NormalInventoryItem>()?.Item;
            return ReadItemSnapshot(entity);
        }
        catch { return default; }
    }
}
