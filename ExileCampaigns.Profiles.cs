using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ExileCampaigns.Build;
using ImGuiNET;

namespace ExileCampaigns;

// manual profile UI at top of settings: auto-switch toggle + list/load/create/delete per-character profiles under ConfigDirectory\profiles
public partial class ExileCampaigns
{
    private string _newProfileName = "";
    private int _selectedProfileIdx = -1;
    private string? _confirmResetProfile;   // name pending reset confirmation (two-step button)

    // display names = filenames (sans .json). PoE ids have no illegal chars so they survive SanitizeProfile, display == switch key
    private string[] ListProfiles()
    {
        try
        {
            if (!Directory.Exists(ProfilesDir)) return Array.Empty<string>();
            return Directory.GetFiles(ProfilesDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
        }
        catch { return Array.Empty<string>(); }
    }

    // create + activate, writing immediately so it shows in the list at step 0. filesafe up front so display id matches the file
    private void CreateProfileManually(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        name = SanitizeProfile(name);
        SwitchProfile(name);   // banks old profile, loads/zeroes this one
        SaveProgress();        // force-write so the file exists before the first step change
    }

    private void DeleteProfile(string name)
    {
        try
        {
            var path = Path.Combine(ProfilesDir, SanitizeProfile(name) + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> delete profile failed: {ex.Message}"); }

        // deleted the active profile: drop to no-profile state. auto-switch re-creates it next tick if enabled + char loaded
        if (name == _charName)
        {
            _charName = "";
            _route.SetCurrent(0);
            _lastSavedStep = _route.Current;
            _build = new BuildPlan();
            _buildIndex.Rebuild(_build);
        }
    }

    // drawn at top of DrawSettings, above the reflected node list
    private void DrawProfileSettings()
    {
        ImGui.TextColored(new Vector4(0.92f, 0.80f, 0.43f, 1f), "Profiles");
        ImGui.TextDisabled($"Active: {(string.IsNullOrEmpty(_charName) ? "(none)" : _charName)}");

        bool auto = Settings.AutoSwitchProfile.Value;
        if (ImGui.Checkbox("Auto-switch profile by character", ref auto))
            Settings.AutoSwitchProfile.Value = auto;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On: the profile follows the logged-in character (<Name> - <Class> - <League>).\n" +
                             "Off: pin a profile manually below; the game character is ignored.");

        var profiles = ListProfiles();
        if (profiles.Length > 0)
        {
            // default selection to the active profile when nothing's picked yet
            if (_selectedProfileIdx < 0 || _selectedProfileIdx >= profiles.Length)
                _selectedProfileIdx = Math.Max(0, Array.IndexOf(profiles, _charName));

            ImGui.SetNextItemWidth(340);
            int sel = _selectedProfileIdx;
            if (ImGui.Combo("##ec_profiles", ref sel, profiles, profiles.Length))
                _selectedProfileIdx = sel;

            // loading while auto-switch is on would revert next tick, so disable it then
            ImGui.SameLine();
            ImGui.BeginDisabled(auto);
            if (ImGui.Button("Load") && sel >= 0 && sel < profiles.Length)
                SwitchProfile(profiles[sel]);
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Reset progress") && sel >= 0 && sel < profiles.Length)
                _confirmResetProfile = profiles[sel];

            ImGui.SameLine();
            if (ImGui.Button("Delete") && sel >= 0 && sel < profiles.Length)
            {
                DeleteProfile(profiles[sel]);
                if (_confirmResetProfile == profiles[sel]) _confirmResetProfile = null;
                _selectedProfileIdx = -1;
            }

            // reset step 2: explicit confirm before wiping progress
            if (_confirmResetProfile != null)
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f),
                    $"Reset '{_confirmResetProfile}' back to step 1? This can't be undone.");
                if (ImGui.Button("Yes, reset progress"))
                {
                    ResetProfileProgress(_confirmResetProfile);
                    _confirmResetProfile = null;
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) _confirmResetProfile = null;
            }
        }
        else
        {
            ImGui.TextDisabled("(no saved profiles yet)");
            _confirmResetProfile = null;
        }

        ImGui.SetNextItemWidth(340);
        ImGui.InputTextWithHint("##ec_newprofile", "New profile name", ref _newProfileName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Create") && !string.IsNullOrWhiteSpace(_newProfileName))
        {
            CreateProfileManually(_newProfileName);
            _newProfileName = "";
            _selectedProfileIdx = -1;
        }

        // show the filesafe name we'd actually save when it differs from what was typed
        if (!string.IsNullOrWhiteSpace(_newProfileName))
        {
            var safe = SanitizeProfile(_newProfileName);
            if (safe != _newProfileName.Trim())
                ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f), $"Will be saved as: {safe}");
        }

        ImGui.Separator();
    }
}
