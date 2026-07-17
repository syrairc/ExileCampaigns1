using System;
using System.Linq;
using System.Numerics;
using ExileCampaigns.Build;
using ImGuiNET;

namespace ExileCampaigns;

// Build tab: level-bracketed sets on the left, that set's entries on the right.
public partial class ExileCampaigns
{
    private string? _selectedSetId;

    private BuildSet? SelectedSet =>
        _build.FindSet(_selectedSetId) ?? _build.Sets.FirstOrDefault();

    private void DrawBuildTab()
    {
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
            SaveProgress();
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
        if (ImGui.InputText("Name", ref name, 64)) { set.Name = name; SaveProgress(); }

        int min = set.MinLevel, max = set.MaxLevel;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Min level", ref min)) { set.MinLevel = Clamp(min); SaveProgress(); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Max level", ref max)) { set.MaxLevel = Clamp(max); SaveProgress(); }

        ImGui.Separator();

        if (ImGui.Button("Duplicate set"))
        {
            var copy = CloneSet(set);
            _build.Sets.Add(copy);
            _selectedSetId = copy.Id;
            SaveProgress();
            ShowToast($"Duplicated {set.Name}", ToastLevel.Success);
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete set"))
        {
            _build.Sets.Remove(set);
            _selectedSetId = null;
            SaveProgress();
        }

        ImGui.Separator();
        DrawEntryTable(set);

        ImGui.PopID();
        ImGui.EndChild();
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
        _openBuildPopup = true;
    }

    // the set says which loadout, the entry says when within it
    private int PrefillLevel(BuildSet set, int requiredLevel) =>
        Clamp(requiredLevel > set.MinLevel ? requiredLevel : set.MinLevel);

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
        if (set == null) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }

        ImGui.Text(_pendingItem.Name);
        var kind = _pendingItem.IsSupport ? "support gem" : _pendingItem.IsGem ? "skill gem" : _pendingItem.ItemClass;
        ImGui.TextDisabled($"{_pendingItem.BaseType}  |  {kind}");
        ImGui.TextDisabled($"into set: {set.Name} ({set.MinLevel}-{set.MaxLevel})");
        ImGui.Separator();

        if (_focusDialogLevel) { ImGui.SetKeyboardFocusHere(); _focusDialogLevel = false; }
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Target level", ref _dialogLevel);
        _dialogLevel = Clamp(_dialogLevel);

        ImGui.SetNextItemWidth(320);
        ImGui.InputText("Note", ref _dialogNote, 128);

        ImGui.Separator();
        if (ImGui.Button("Add", new Vector2(90, 0)))
        {
            AddPendingItem(set);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(90, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void AddPendingItem(BuildSet set)
    {
        if (!_pendingItem.Valid) return;
        set.Entries.Add(new BuildEntry
        {
            Name = _pendingItem.Name,
            BaseType = _pendingItem.BaseType,
            ItemClass = _pendingItem.ItemClass,
            Rarity = _pendingItem.Rarity,
            Kind = _pendingItem.IsGem ? BuildItemKind.Gem : BuildItemKind.Equipment,
            IsSupport = _pendingItem.IsSupport,
            TargetLevel = _dialogLevel,
            RequiredLevel = _pendingItem.RequiredLevel,
            Note = _dialogNote.Trim(),
            CapturedAt = System.DateTime.Now,
        });
        SaveProgress();
        ShowToast($"Added {_pendingItem.Name} @ Lvl {_dialogLevel}", ToastLevel.Success);
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
            if (e.Used) ImGui.TextDisabled(e.Name); else ImGui.Text(e.Name);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(e.IsSupport ? "support" : e.Kind == BuildItemKind.Gem ? "gem" : "item");

            ImGui.TableNextColumn();
            int lvl = e.TargetLevel;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##lvl", ref lvl, 0)) { e.TargetLevel = Clamp(lvl); SaveProgress(); }

            ImGui.TableNextColumn();
            var note = e.Note;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##note", ref note, 128)) { e.Note = note; SaveProgress(); }

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("X")) remove = e;

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (remove != null) { set.Entries.Remove(remove); SaveProgress(); }
    }
}
