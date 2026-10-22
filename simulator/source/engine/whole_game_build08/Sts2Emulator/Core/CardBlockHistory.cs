namespace Sts2Emulator.Core;

/// <summary>
/// The block history belonging to one CardPlay. A value so nested effects can save the
/// parent's count on the stack, and a paused selection can carry it without sharing
/// mutable state with a search clone. Replays have distinct Ids but the same SourceId.
/// </summary>
public struct BlockPlayContext
{
    public int Id;
    public int SourceId;
    public int Epoch;
    public int BlockGains;
}

/// <summary>
/// The slice of CombatHistory needed by Unmovable and the two card-block relics.
/// Counts every positive Move gain, including those before Unmovable was acquired.
/// No full history or per-play dictionary is needed: only the active play's own rows
/// are excluded, and a nested play saves/restores that count with its parent context.
/// </summary>
internal static class CardBlockHistory
{
    internal static int NewSource(CombatState state) => ++state.NextBlockPlayIdentity;

    internal static void BeginPlay(CombatState state, int sourceId)
    {
        EnsureCurrentTurn(state);
        state.ActiveBlockPlay = new BlockPlayContext
        {
            Id = ++state.NextBlockPlayIdentity,
            SourceId = sourceId,
            Epoch = state.BlockHistoryEpoch,
        };
    }

    internal static int PriorBlockGains(CombatState state)
    {
        EnsureCurrentTurn(state);
        int current =
            state.ActiveBlockPlay.Epoch == state.BlockHistoryEpoch
                ? state.ActiveBlockPlay.BlockGains
                : 0;
        return state.BlockGainsThisTurn - current;
    }

    // Called only for Move block, after all modifiers and before AfterBlockGained.
    // CreatureCmd creates a row when the DECIMAL amount is positive even if its int
    // Amount is zero. The caller deliberately checks positivity before truncation.
    internal static void RecordBlockGained(CombatState state)
    {
        EnsureCurrentTurn(state);
        if (state.ActiveBlockPlay.Id == 0)
            return; // Unmovable ignores history rows whose CardPlay is null.
        if (state.ActiveBlockPlay.Epoch != state.BlockHistoryEpoch)
        {
            state.ActiveBlockPlay.Epoch = state.BlockHistoryEpoch;
            state.ActiveBlockPlay.BlockGains = 0;
        }
        state.BlockGainsThisTurn++;
        state.ActiveBlockPlay.BlockGains++;
    }

    // This is an AfterCardPlayed hook, not the end of CardEffects.Apply: an effect
    // can return to the UI before its awaited hand selection has completed.
    internal static void AfterCardPlayed(CombatState state)
    {
        var play = state.ActiveBlockPlay;
        if (play.Id == 0)
            return;
        if (state.VambraceTriggerSourceId == play.SourceId)
            state.VambraceSpent = true;
        if (state.PaelsLegionAffectedPlayId == play.Id)
        {
            state.PaelsLegionAffectedPlayId = 0;
            state.PaelsLegionCooldown = Effects.RelicEffects.PaelsLegionCooldownTurns;
        }
    }

    internal static void EnsureCurrentTurn(CombatState state)
    {
        // HappenedThisTurn compares both the side and the turn number. Check lazily
        // rather than clearing late in turn-start setup, after an early auto-play may
        // already have gained block. Extra player turns also change Turn.
        if (
            state.BlockHistoryTurn == state.Turn
            && state.BlockHistoryPlayerTurn == state.PlayerTurn
        )
            return;
        state.BlockHistoryTurn = state.Turn;
        state.BlockHistoryPlayerTurn = state.PlayerTurn;
        state.BlockHistoryEpoch++;
        state.BlockGainsThisTurn = 0;
    }

    internal static void ResetCombat(CombatState state)
    {
        state.NextBlockPlayIdentity = 0;
        state.ActiveBlockPlay = default;
        state.BlockHistoryEpoch = 0;
        state.BlockHistoryTurn = -1;
        state.BlockHistoryPlayerTurn = false;
        state.BlockGainsThisTurn = 0;
        state.VambraceTriggerSourceId = 0;
        state.VambraceSpent = false;
        state.PaelsLegionAffectedPlayId = 0;
        state.PaelsLegionCooldown = 0;
    }
}
