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
}
