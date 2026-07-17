using System;
using System.Linq;
using System.Numerics;
using ExileCampaigns.Build;
using ExileCore;
using ExileCore.Shared.Enums;
using ImGuiNET;

namespace ExileCampaigns;

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
    // files are not ready at plugin init anyway.
    private void DrawPicker(BuildSet set)
    {
        _catalog ??= BuildCatalog.Load(GameController, DirectoryFullName, m => LogError($"ExileCampaigns -> {m}"));

        ImGui.SetNextItemWidth(320);
        var sel = _pickerSelection;
        if (ImGuiHelpers.SearchCombobox("##ec_picker", ref _pickerInput, ref sel, _catalog.Items,
                (item, filter) => ImGuiHelpers.WhitespaceSeparatedContains(item.Label, filter),
                item => item.Label))
            _pickerSelection = sel;

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
                Used = false,                 // a fresh bracket is not equipped yet
                CapturedAt = e.CapturedAt,
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
            CapturedAt = DateTime.Now,
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

        if (!ImGui.BeginTable("##ec_entries", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
                new Vector2(0, 220)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableHeadersRow();

        BuildEntry? remove = null;
        foreach (var e in set.Entries.OrderBy(x => x.TargetLevel).ToList())
        {
            ImGui.PushID(e.Id);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var linkedTo = _build.FindEntry(e.LinkedToId);
            var label = linkedTo != null ? $"{e.Name} (+ {linkedTo.Name})" : e.Name;
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
