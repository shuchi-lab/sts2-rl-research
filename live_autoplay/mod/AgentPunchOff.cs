namespace STS2_MCP;

// Guard for PunchOff's decorative fire-and-forget loop. Native PunchEachOther
// repeats `while (!_punchCts.IsCancellationRequested)` with only
// CreatureCmd.TriggerAnim(.., 0f) and Cmd.Wait awaits. When Cmd.Wait skips its
// SceneTree timer (Instant, non-interactive, combat ending) every await completes
// synchronously, so AfterEventStarted never returns to EventRoom.EnterInternal,
// the main frame never ends and TakeThem/room exit can never cancel it (native256
// game95 froze twice). The loop only triggers enemy animations, spawns hit VFX
// and flips the left enemy's scale; _punchCts, choices, RNG and HP are untouched.
internal static class AgentPunchOffGuard
{
    public static long Skipped { get; private set; }

    // Native Cmd.Wait(seconds > 0, ignoreCombatEnd: false) awaits a timer exactly when this holds.
    public static bool WaitYields(bool nonInteractive, bool hasGame, bool instant, bool combatEnding) =>
        !nonInteractive && (!hasGame || (!instant && !combatEnding));

    // Returns whether native PunchEachOther should run.
    public static bool Allow(bool waitYields)
    {
        if (waitYields) return true;
        Skipped++;
        return false;
    }
}
