namespace Sts2Emulator.Core.Run;

public enum CardRewardRngSource
{
    PlayerRewards,
    CrystalSphere,
}

/// <summary>Original creation semantics retained for queued rewards and rerolls.</summary>
public sealed record CardRewardOptions(
    int RoomType,
    bool EncounterSource = true,
    bool IsFromCombat = true
)
{
    public int CardCount { get; init; } = 3;
    public int[]? CardPool { get; init; }
    public CardRarity? FixedRarity { get; init; }
    public CardType? FixedType { get; init; }
    public CardRewardRngSource RngSource { get; init; }
    public bool UpgradeAfterGenerated { get; init; }
    public bool NoUpgradeRoll { get; init; }
    public bool IsCardReward { get; init; } = true;
    public bool AllowPoolModifications { get; init; } = true;
}

/// <summary>
/// An already populated reward. Keep card instances while another offer is shown,
/// so claiming rewards cannot discard upgrades or consume fresh random draws.
/// </summary>
public sealed record CardRewardOffer(CardInstance[] Cards)
{
    public CardInstance BonusPowerCard { get; init; }
    public bool CanReroll { get; init; }
    public CardRewardOptions? Options { get; init; }
    public CardRewardOptions? RerollOptions { get; init; }

    // CardReward subscribes when constructed, which can precede population (room extras).
    public int SubscriptionOrder { get; init; }
    public bool ReceivesRelicUpdates { get; init; }

    /// <summary>For event options created with NoUpgradeRoll.</summary>
    public static CardRewardOffer Unupgraded(IEnumerable<int> ids) =>
        new([.. ids.Select(id => new CardInstance(id, false))]);
}

/// <summary>The original CardFactory cannot generate a reward from this pool.</summary>
internal sealed class CardRewardGenerationException : InvalidOperationException
{
    public CardRewardGenerationException()
        : base("Cannot generate a card reward from an empty pool.") { }
}
