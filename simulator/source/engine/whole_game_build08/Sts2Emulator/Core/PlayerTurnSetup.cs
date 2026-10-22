using Sts2Emulator.Core.Effects;

namespace Sts2Emulator.Core;

/// <summary>The non-card SetupPlayerTurn task, suspended at an awaited choice.</summary>
internal sealed class PendingTurnSetup
{
    public bool Opening;
    public int Stage;
    public int Cursor;
    public bool StrengthOwed;
    public bool SideStartComplete;
    public int PreservedQueuePrefix;
    public int HandDrawOwed;

    // Hook takes a listener snapshot. These arrays are immutable after construction.
    public int[] RelicIds = [];
    public BuffId[] PowerIds = [];

    public PendingTurnSetup Clone() => (PendingTurnSetup)MemberwiseClone();
}

internal static class PlayerTurnSetup
{
    // After a Stratagem pause: the rest of the hand draw, then AfterPlayerTurnStart.
    private const int HandDrawStage = 6;
    private const int AfterHandDrawStage = 7;

    internal static bool Applies(CombatState state) =>
        state.Relics.Any(r => r.DefId == RelicEffects.ToastyMittens)
        || (
            state.Relics.Any(r => r.DefId is RelicEffects.Pendulum or RelicEffects.PollinousCore)
            && state.Relics.Any(r => r.DefId == RelicEffects.Toolbox)
        )
        // GamblingChip pauses AfterPlayerTurnStart on turn one only, and the relics
        // acquired after it await its screen.
        || (state.Turn == 0 && state.Relics.Any(r => r.DefId == RelicEffects.GamblingChip));

    internal static void BeginOpening(CombatState state, Random rng)
    {
        state.PendingTurnSetup = new PendingTurnSetup
        {
            Opening = true,
            PreservedQueuePrefix = state.AutoPlayQueue.Count,
            RelicIds = state.Relics.Select(r => r.DefId).ToArray(),
        };
        Resume(state, rng);
    }

    internal static void BeginAfterDraw(CombatState state, Random rng)
    {
        state.PendingTurnSetup = new PendingTurnSetup
        {
            Stage = 2,
            PreservedQueuePrefix = state.AutoPlayQueue.Count,
            RelicIds = state.Relics.Select(r => r.DefId).ToArray(),
            PowerIds = state.PlayerBuffs.Select(p => p.Id).ToArray(),
        };
        Resume(state, rng);
    }

    /// <summary>
    /// `StratagemPower.AfterShuffle`'s screen paused `SetupPlayerTurn` inside its hand
    /// draw. StartTurn's AfterSideTurnStart runs at that pause; the owed draws and
    /// AfterPlayerTurnStart wait for the pick.
    /// </summary>
    internal static void BeginAfterStratagemPause(CombatState state, int handDrawOwed)
    {
        state.PendingTurnSetup = new PendingTurnSetup
        {
            Stage = HandDrawStage,
            HandDrawOwed = handDrawOwed,
            PreservedQueuePrefix = state.AutoPlayQueue.Count,
            RelicIds = state.Relics.Select(r => r.DefId).ToArray(),
        };
    }

    internal static void AfterSideStart(CombatState state, Random rng)
    {
        if (state.PendingTurnSetup is not { } pending)
            return;
        pending.SideStartComplete = true;
        Resume(state, rng);
    }

    internal static void Resume(CombatState state, Random rng)
    {
        while (state.PendingTurnSetup is { } pending)
        {
            // Await a whole child operation, including any nested card play. This guard
            // does not disable synchronous ExhaustCard -> DrawCards -> AutoPlayNested.
            if (state.PendingSelection is not null || state.PendingCardPlay is not null)
                return;
            if (state.PlayerHp <= 0 || CombatEngine.NoPrimaryEnemyLeft(state))
            {
                state.PendingTurnSetup = null;
                return;
            }
            // DiscardAndDraw awaits its Sly plays after replacement draws. Those
            // children must settle before the next listener or the Strength tail,
            // without releasing pre-existing top-level pre-play work.
            CombatEngine.ResolveAwaitedSetupAutoPlays(state, pending.PreservedQueuePrefix, rng);
            if (state.PendingSelection is not null || state.PendingCardPlay is not null)
                return;
            if (state.PlayerHp <= 0 || CombatEngine.NoPrimaryEnemyLeft(state))
            {
                state.PendingTurnSetup = null;
                return;
            }
            if (pending.StrengthOwed)
            {
                pending.StrengthOwed = false;
                RelicEffects.GainPlayerStrength(state, 1);
            }
            switch (pending.Stage)
            {
                case 0:
                    // BeforeHandDraw awaits relics in acquisition order. A Toolbox
                    // pause must not tick a later counter or replay an earlier one.
                    if (pending.Cursor == pending.RelicIds.Length)
                    {
                        pending.Stage = 1;
                        pending.Cursor = 0;
                        break;
                    }
                    int beforeDrawRelic = pending.RelicIds[pending.Cursor++];
                    if (beforeDrawRelic == RelicEffects.Pendulum)
                        RelicEffects.AdvancePendulumBeforeHandDraw(state);
                    else if (beforeDrawRelic == RelicEffects.PollinousCore)
                        RelicEffects.TickPollinousCore(state);
                    else if (beforeDrawRelic == RelicEffects.Toolbox)
                        CardEffects.OpenToolboxSelection(state, rng);
                    break;
                case 1:
                    pending.Stage = 2;
                    // Only after every pre-draw listener finishes: bonus, then
                    // max(Innate), then cap10, then slots left by Toolbox's card.
                    int handDraw = CombatFactory.ApplyTurnOneDrawPileReorder(
                        state.DrawPile,
                        5 + RelicEffects.ExtraOpeningHandDraw(state)
                    );
                    RelicEffects.ResetPollinousCoreAfterModifyingHandDraw(state);
                    CombatFactory.DrawOpeningHand(
                        state,
                        Math.Min(handDraw, CardEffects.MaxCardsInHand - state.Hand.Count),
                        rng
                    );
                    pending.PowerIds = state.PlayerBuffs.Select(p => p.Id).ToArray();
                    break;
                case 2:
                    if (pending.Cursor == pending.PowerIds.Length)
                    {
                        pending.Stage = 3;
                        pending.Cursor = 0;
                        break;
                    }
                    var power = pending.PowerIds[pending.Cursor++];
                    int amount = BuffSystem.Get(state.PlayerBuffs, power);
                    if (power == BuffId.Tyranny && amount > 0 && state.Hand.Count > 0)
                        CardEffects.OpenExhaustFromHandSelection(state, amount);
                    if (power == BuffId.ToolsOfTheTrade && amount > 0)
                    {
                        CardEffects.DrawCards(state, amount, rng);
                        CardEffects.OpenDiscardSelection(state, SI.ToolsOfTheTrade, amount);
                    }
                    break;
                case 3:
                    if (pending.Cursor == pending.RelicIds.Length)
                    {
                        pending.Stage = 4;
                        pending.Cursor = 0;
                        break;
                    }
                    int relic = pending.RelicIds[pending.Cursor++];
                    var living = CombatEngine.CaptureLivingEnemiesForDeath(state);
                    if (relic == RelicEffects.ToastyMittens)
                    {
                        pending.StrengthOwed = true;
                        if (state.Hand.Count > 1)
                        {
                            state.PendingSelection = new PendingCardSelection
                            {
                                Kind = CardSelectionKind.ExhaustFromHand,
                                Candidates = [.. Enumerable.Range(0, state.Hand.Count)],
                                SourceCardDefId = 0,
                                Skippable = false,
                            };
                        }
                        else if (state.Hand.Count == 1)
                        {
                            var card = state.Hand[0];
                            state.Hand.RemoveAt(0);
                            CardEffects.ExhaustCard(state, card, rng: rng);
                        }
                    }
                    else if (relic == RelicEffects.GamblingChip && state.Turn == 0)
                        CombatEngine.OpenGamblingChipScreenForCombatStart(state);
                    else
                        RelicEffects.ApplyOrderedPlayerTurnRelic(state, relic, rng);
                    CombatEngine.ResolveAwaitedEnemyDeaths(state, living, rng);
                    break;
                case HandDrawStage:
                    pending.HandDrawOwed = CardEffects.DrawHandCards(
                        state,
                        pending.HandDrawOwed,
                        rng
                    );
                    // A further reshuffle reopens the screen and owes the rest again.
                    if (pending.HandDrawOwed == 0)
                        pending.Stage = AfterHandDrawStage;
                    break;
                case AfterHandDrawStage:
                    CombatEngine.ApplyAfterPlayerTurnStart(state, rng);
                    if (Applies(state))
                    {
                        // Tyranny and Tools of the Trade, then the ordered relics.
                        pending.Stage = 2;
                        pending.Cursor = 0;
                        pending.PowerIds = state.PlayerBuffs.Select(p => p.Id).ToArray();
                    }
                    else
                        pending.Stage = 5;
                    break;
                case 4:
                    if (pending.Cursor < pending.RelicIds.Length)
                    {
                        RelicEffects.ApplyOrderedPlayerTurnRelicLate(
                            state,
                            pending.RelicIds[pending.Cursor++]
                        );
                        break;
                    }
                    pending.Stage = 5;
                    break;
                default:
                    // StartTurn runs AfterSideTurnStart at the first pause OR setup
                    // completion, but AutoPrePlay awaits setup's full completion.
                    if (!pending.SideStartComplete)
                        return;
                    state.PendingTurnSetup = null;
                    if (pending.Opening)
                        CombatFactory.QueueCombatStartAutoPlays(state);
                    else
                        CombatEngine.ResolveTurnStartAutoPlays(state, rng);
                    CombatEngine.ResolvePrePlayPhase(state, rng);
                    return;
            }
        }
    }
}
