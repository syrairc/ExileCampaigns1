// ExileCampaigns/ExileCampaigns.Sync.cs
using ExileCampaigns.Guide;

namespace ExileCampaigns;

public partial class ExileCampaigns
{
    // manual sync: re-point the tracker at the character's real progress from live quest flags + area.
    private void SyncToCharacter()
    {
        if (_route.Steps.Count == 0) return;
        var world = new WorldState(this);
        int target = RouteSync.ResolveSyncTarget(_route.Steps, world.QuestFlagSatisfied, _areaId);
        if (target < 0) return;
        _route.SetCurrent(target);
        var txt = _route.CurrentStep?.DisplayText ?? "";
        ShowToast(string.IsNullOrEmpty(txt) ? "Synced to character" : $"Synced to: {txt}", ToastLevel.Success);
        MaybeSaveProgress();
    }
}
