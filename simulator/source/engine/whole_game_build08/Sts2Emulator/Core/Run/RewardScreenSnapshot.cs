namespace Sts2Emulator.Core.Run;

/// <summary>
/// A suspended parent RewardsSet. Pickup effects can await a separate set of rewards;
/// returning restores only that screen, never the deck, relics, or random streams.
/// </summary>
public sealed record RewardScreenSnapshot
{
    public RunPhase Phase { get; init; }
    public int Gold { get; init; }
    public int Potion { get; init; }
    public int Relic { get; init; }
    public required CardRewardOffer Card { get; init; }
    public bool CardPending { get; init; }
    public bool RelicPending { get; init; }
    public bool ReturnAfterCard { get; init; }
    public int OpenedCardIndex { get; init; }
    public bool NeowAwaitingProceed { get; init; }
    public bool EventAwaitingProceed { get; init; }
    public bool NeowsBonesCurse { get; init; }
    public int CardRemovals { get; init; }
    public int[] RemainingGold { get; init; } = [];
    public int[] RemainingPotions { get; init; } = [];
    public int[] RemainingRelics { get; init; } = [];
    public CardRewardOffer[] RemainingCards { get; init; } = [];
    public CardInstance[] SpecialCards { get; init; } = [];

    public static RewardScreenSnapshot Capture(RunState state, RunPhase returnPhase) =>
        new()
        {
            Phase = returnPhase,
            Gold = state.RewardGold,
            Potion = state.RewardPotion,
            Relic = state.RelicReward,
            Card = RunRewardGenerator.CaptureCardOffer(state),
            CardPending = state.RewardCardPending,
            RelicPending = state.PendingRelicReward,
            ReturnAfterCard = state.ReturnToRewardScreenAfterCardReward,
            OpenedCardIndex = state.OpenedCardRewardIndex,
            NeowAwaitingProceed = state.NeowAwaitingProceed || returnPhase == RunPhase.Ancient,
            EventAwaitingProceed = state.EventAwaitingProceed,
            NeowsBonesCurse = state.PendingNeowsBonesCurse,
            CardRemovals = state.CardRemovalsOwed,
            RemainingGold = [.. state.PendingGoldRewards],
            RemainingPotions = [.. state.PendingPotionRewards],
            RemainingRelics = [.. state.PendingBonusRelicRewards],
            RemainingCards = [.. state.PendingCardOffers.Select(CopyOffer)],
            SpecialCards = [.. state.PendingSpecialCardRewards],
        };

    private static CardRewardOffer CopyOffer(CardRewardOffer offer) =>
        offer with
        {
            Cards = [.. offer.Cards],
        };

    public RewardScreenSnapshot DeepCopy() =>
        this with
        {
            Card = CopyOffer(Card),
            RemainingGold = [.. RemainingGold],
            RemainingPotions = [.. RemainingPotions],
            RemainingRelics = [.. RemainingRelics],
            RemainingCards = [.. RemainingCards.Select(CopyOffer)],
            SpecialCards = [.. SpecialCards],
        };

    public void Restore(RunState state)
    {
        state.Phase = Phase;
        state.RewardGold = Gold;
        state.RewardPotion = Potion;
        state.RelicReward = Relic;
        RunRewardGenerator.ShowCardOffer(state, CopyOffer(Card));
        state.RewardCardPending = CardPending;
        state.PendingRelicReward = RelicPending;
        state.ReturnToRewardScreenAfterCardReward = ReturnAfterCard;
        state.OpenedCardRewardIndex = OpenedCardIndex;
        state.NeowAwaitingProceed = NeowAwaitingProceed;
        state.EventAwaitingProceed = EventAwaitingProceed;
        state.PendingNeowsBonesCurse = NeowsBonesCurse;
        state.CardRemovalsOwed = CardRemovals;
        state.PendingGoldRewards = [.. RemainingGold];
        state.PendingPotionRewards = [.. RemainingPotions];
        state.PendingBonusRelicRewards = [.. RemainingRelics];
        state.PendingCardOffers = [.. RemainingCards.Select(CopyOffer)];
        state.PendingSpecialCardRewards = [.. SpecialCards];
    }
}
