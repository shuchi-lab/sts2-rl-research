namespace Sts2Emulator.Core;

/// <summary>
/// What a card's `OnPlay` still owes after an awaited player choice. In the game this is
/// just the rest of the async method; the emulator returns from the effect when the
/// selection opens, so the remainder has to be carried as data until the answer arrives.
/// </summary>
/// <remarks>
/// Brand is the first shape (`Brand.cs:42-49`): choose, `CardCmd.Exhaust` with all of its
/// hooks, and only then `PowerCmd.Apply&lt;StrengthPower&gt;`. A selection resolved inline
/// (an auto-played card, a lone candidate, an empty hand) runs the tail immediately.
/// </remarks>
public enum EffectTailKind
{
    None = 0,
    GainPlayerStrength,

    /// <summary>
    /// The rest of an automatic play parked inside this effect -- Havoc's
    /// `AutoPlayFromDrawPile` child waiting on its own selection. See
    /// <see cref="PendingNestedAutoPlay" />. Run by the engine, which owns the child's
    /// play loop.
    /// </summary>
    ResumeNestedAutoPlay,

    /// <summary>
    /// Havoc's `AutoPlayFromDrawPile` after its `ShuffleIfNecessary` paused on
    /// Stratagem's screen: the top card is read and played only after the pick. Run by
    /// the engine, which needs the RNG and may park the child in turn.
    /// </summary>
    HavocPlayTop,
}

/// <inheritdoc cref="EffectTailKind" />
public readonly record struct EffectTail(EffectTailKind Kind, int Amount)
{
    public bool IsNone => Kind == EffectTailKind.None;
}
