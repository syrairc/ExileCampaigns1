using System.Numerics;
using ImGuiNET;

namespace ExileCampaigns;

// one-shot dialog offered the first time a profile is created (manual Create or auto-switch on a new
// character). lets the user flip the global Show-league-start setting without digging through settings.
public partial class ExileCampaigns
{
    private bool _leagueStartPromptPending;   // set in SwitchProfile on a brand-new profile; cleared on answer

    private void DrawLeagueStartPrompt()
    {
        const string popupId = "League Start mode?##ec_ls";

        if (_leagueStartPromptPending && !ImGui.IsPopupOpen(popupId))
            ImGui.OpenPopup(popupId);

        // centre on the viewport the first time it appears
        var ds = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new Vector2(ds.X * 0.5f, ds.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        // fixed width, height auto-fits content (0 = auto axis) so the box isn't oversized
        ImGui.SetNextWindowSize(new Vector2(380f, 0f), ImGuiCond.Appearing);

        if (!ImGui.BeginPopupModal(popupId))
            return;

        ImGui.TextWrapped("Enable League Start mode for this run?");
        ImGui.Spacing();
        ImGui.PushTextWrapPos(360f);
        ImGui.TextWrapped("It adds the league-start chores to the route - Trials of Ascendancy and Crafting " +
                          "Recipe pickups. On a later re-run you can turn them off under Show league-start steps.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("Enable", new Vector2(120f, 0f)))
        {
            Settings.ShowLeagueStart.Value = true;
            _leagueStartPromptPending = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Skip", new Vector2(120f, 0f)))
        {
            Settings.ShowLeagueStart.Value = false;
            _leagueStartPromptPending = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
