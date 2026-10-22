namespace Sts2Emulator.Core;

/// <summary>
/// Which native energy-X effect bodies have MIGRATED to the captured-resource contract.
/// </summary>
/// <remarks>
/// <para>
/// This is implementation status, not a game property. In the game every
/// `HasEnergyCostX` card captures X once when its resources are settled and reads
/// `ResolveEnergyXValue()` from that capture (CardModel.cs:1117-1124, 1807-1834;
/// CardCmd.cs:99-130). Native bodies used to read and zero live Energy instead. Admitting a
/// card here switches its payment to the source order (pay X before any hook) and makes
/// its body read <see cref="Effects.CardEffects.ResolveCapturedEnergyX" />.
/// </para>
/// <para>
/// An unadmitted X card keeps its old payment and body exactly: paying early while its
/// body still reads live Energy would hand it zero. The remaining eight generated
/// energy-X cards (Dirge, Eradicate, Heavenly Drill, Malaise, Multi-Cast, Skewer, Tempest,
/// Volley) are unadmitted. `scripts/check_energy_x_migration.py` derives that inventory
/// from the generated `HasEnergyCostX` flags and guards both halves.
/// </para>
/// </remarks>
internal static class EnergyXMigration
{
    /// <summary>`Whirlwind` (Cards.g.cs Id 538).</summary>
    internal const int Whirlwind = 538;

    /// <summary>`Cascade` (Cards.g.cs Id 546).</summary>
    internal const int Cascade = 546;

    internal static bool UsesCapturedEnergy(int defId) => defId is Whirlwind or Cascade;
}

/// <summary>How a play's resources were settled.</summary>
internal enum CardResourceOrigin : byte
{
    /// <summary>`PlayCardAction`: `SpendResources`, then `OnPlayWrapper(isAutoPlay: false)`.</summary>
    Manual,

    /// <summary>`CardCmd.AutoPlay`: X captured from current Energy, nothing spent.</summary>
    FreeAuto,

    /// <summary>
    /// Whispering Earring: `SpendResources`, then `CardCmd.AutoPlay(skipXCapture: true)`,
    /// so X is the paid amount but the play's resource value is what the bar holds after.
    /// </summary>
    PaidAuto,
}

/// <summary>
/// The resources of one physical activation, shared by every repeat. Admitted X cards
/// capture their effect value; normal automatic plays retain their cost for relic hooks.
/// </summary>
/// <param name="Captured">False for every card that is not admitted.</param>
/// <param name="CapturedEnergyX">`EnergyCost.CapturedXValue`: what the effect resolves.</param>
/// <param name="EnergyPaid">Energy actually taken from the bar for this activation.</param>
/// <param name="ResourceEnergyValue">`CardPlay.Resources.EnergyValue`, which Intimidating
/// Helmet and Ivory Tile read.</param>
/// <param name="Origin">Which of the three settlement shapes produced it.</param>
internal readonly record struct CardResourceContext(
    bool Captured,
    int CapturedEnergyX,
    int EnergyPaid,
    int ResourceEnergyValue,
    CardResourceOrigin Origin
)
{
    /// <summary>`CardPlay.Resources.EnergySpent`: only a manual play reports its payment.</summary>
    public int ResourceEnergySpent => Origin == CardResourceOrigin.Manual ? EnergyPaid : 0;

    public static CardResourceContext ManualPayment(int paid) =>
        new(true, paid, paid, paid, CardResourceOrigin.Manual);

    public static CardResourceContext PaidAutoPayment(int paid, int energyAfterPayment) =>
        new(true, paid, paid, energyAfterPayment, CardResourceOrigin.PaidAuto);

    public static CardResourceContext FreeAutoCapture(int energy) =>
        new(true, energy, 0, energy, CardResourceOrigin.FreeAuto);

    /// <summary>A normal auto-played card has a cost value, but neither payment nor X capture.</summary>
    public static CardResourceContext FreeAutoCost(int energyValue) =>
        new(false, 0, 0, Math.Max(0, energyValue), CardResourceOrigin.FreeAuto);
}
