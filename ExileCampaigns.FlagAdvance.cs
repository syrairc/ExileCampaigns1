namespace ExileCampaigns;

// pulse detection for the two flagless advance triggers: waypoint activation and fresh login.
public partial class ExileCampaigns
{
    // "Get Waypoint" steps (ActivateWaypoint objective) advance when PoE1 sets the WaypointUnlocked quest flag.
    // edge-triggered: pulse only on a fresh false->true so a waypoint already unlocked doesn't complete the step.
    // _progress.WaypointPulsed clears on step change (new ProgressTracker), same as the login pulse.
    private const string WaypointUnlockedFlag = "WaypointUnlocked";
    private bool _wpFlagWasSet;
    private bool _wpFirstPoll = true;
    private void UpdateWaypointPulse()
    {
        var flags = ReadQuestFlags();
        if (flags == null) return;
        bool nowSet = false;
        foreach (var kv in flags)
            if (kv.Value && kv.Key.ToString() == WaypointUnlockedFlag) { nowSet = true; break; }
        if (_wpFirstPoll) { _wpFirstPoll = false; _wpFlagWasSet = nowSet; return; }
        if (nowSet && !_wpFlagWasSet) _progress.WaypointPulsed = true;
        _wpFlagWasSet = nowSet;
    }

    // fresh-login detection for "log out" steps (the relog/instance-reset trick). exileapi freezes plugin
    // ticks while you sit at char-select, so we can't catch the login-screen frame directly. instead watch
    // the in-game session clock: TimeInGame keeps climbing across normal zone transitions but resets to ~0
    // on a fresh login. so a backwards jump = a relog happened while we were frozen -> pulse it onto the
    // current step's progress (cleared on step change, like the waypoint pulse). no false trigger on zones.
    private float _lastTimeInGame = -1f;
    private void UpdateLoginPulse()
    {
        var game = GameController?.Game;
        if (game == null || !game.IsInGameState) return;
        var t = GameController?.IngameState?.TimeInGameF ?? 0f;
        if (t <= 0f) return;
        if (_lastTimeInGame <= 0f) { _lastTimeInGame = t; return; }  // first poll, just seed
        // 2s margin absorbs read jitter so only a real session reset trips it
        if (t + 2f < _lastTimeInGame) _progress.LoggedInPulse = true;
        _lastTimeInGame = t;
    }
}
