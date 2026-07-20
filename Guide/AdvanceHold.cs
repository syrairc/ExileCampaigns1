namespace ExileCampaigns.Guide;

// how a manual-backtrack hold reacts to the current step's completion each advance poll. pure so the runtime
// and tests share it. the hold exists so auto-advance doesn't snap you forward off a step you deliberately
// stepped back onto - but a step that goes incomplete->complete WHILE you sit on it (a quest flag flipping,
// an area entered) is real progress, so that fresh edge releases the hold. "seed" = was the step already
// complete the moment the hold began (or when the held step last changed); only a false->true edge breaks it.
public static class AdvanceHold
{
    public readonly record struct State(bool Held, string? SeedStepId, bool SeedComplete);
    public readonly record struct Decision(bool Advance, State Next);

    public static Decision Evaluate(State s, string currentStepId, bool complete)
    {
        // not held: normal behaviour, advance whenever the step is complete.
        if (!s.Held) return new Decision(complete, new State(false, null, false));

        // (re)seed on entry, or when the held step changes (double-backtrack, manual move): never advance the
        // poll we seed on, so landing on an already-complete step can't snap forward.
        if (currentStepId != s.SeedStepId)
            return new Decision(false, new State(true, currentStepId, complete));

        // already complete when we landed, or still not complete: keep holding.
        if (!complete || s.SeedComplete) return new Decision(false, s);

        // became complete while we sat here: real progress, release the hold and advance.
        return new Decision(true, new State(false, null, false));
    }
}
