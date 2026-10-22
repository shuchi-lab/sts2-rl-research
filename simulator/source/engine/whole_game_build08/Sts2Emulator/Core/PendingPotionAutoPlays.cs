namespace Sts2Emulator.Core;

/// <summary>
/// A `CardPileCmd.AutoPlayFromDrawPile` batch (Distilled Chaos, Mayhem), suspended on a
/// choice one of its cards asked the player to make.
/// </summary>
/// <remarks>
/// The game stages every card into Play first (CardPileCmd.cs:1151-1169) and then awaits
/// `CardCmd.AutoPlay` for each. A child's `CardSelectCmd.FromHand` (Burning Pact) has no
/// auto-play exception, so the solo player is asked, and the rest of the batch and the
/// potion's `AfterPotionUsed` wait for the answer. Mayhem's turn-start batch makes the same
/// `AutoPlayFromDrawPile` call and uses this class too (<see cref="ResumesTurnStart" />);
/// it waits the same way, and the rest of the pre-play phase waits with it. History
/// Course's dupe is a one-card batch of the same kind (<see cref="HistoryCourseEncore" />).
/// Only these
/// top-level batches park; a play nested inside one of their children still resolves
/// automatically.
/// </remarks>
internal sealed class PendingPotionAutoPlays
{
    /// <summary>Staged cards not yet played, in Play-pile order.</summary>
    public List<CardInstance> Staged = [];

    /// <summary>The child whose effect is waiting on the open selection, if any.</summary>
    public ParkedAutoPlay? Parked;

    /// <summary>
    /// Mayhem's batch rather than a potion's: `MayhemPower.AfterAutoPrePlayPhaseEntered`
    /// awaits the same `AutoPlayFromDrawPile`, so its children ask the player too, and the
    /// rest of the pre-play phase -- not a potion's `AfterPotionUsed` -- waits for it.
    /// </summary>
    public bool ResumesTurnStart;

    /// <summary>
    /// History Course's one-card batch, which also resumes the turn start: its
    /// `CardCmd.AutoPlay` of the dupe uses the hook's choice context. Mayhem's resume plays
    /// it next, and its own resume goes straight on to the rest of the pre-play phase.
    /// </summary>
    public bool HistoryCourseEncore;

    /// <summary>
    /// A Stampede swing rather than a draw-pile batch: `StampedePower.AfterAutoPostPlayPhaseEntered`
    /// awaits `CardCmd.AutoPlay` on an Attack from HAND, and the rest of Stampede and of
    /// the end of the turn wait for its answer.
    /// </summary>
    public bool ResumesEndTurn;

    /// <summary>Stampede swings still owed after the parked one.</summary>
    public int StampedeRemaining;

    /// <summary>
    /// The enemy HP the hand auto-play settles its deaths against once the play is done,
    /// as `AutoPlayCardFromHand` did; null for a draw-pile batch.
    /// </summary>
    public int[]? HandEnemyHpsBefore;

    public PendingPotionAutoPlays Clone() =>
        new()
        {
            Staged = [.. Staged],
            Parked = Parked?.Clone(),
            ResumesTurnStart = ResumesTurnStart,
            HistoryCourseEncore = HistoryCourseEncore,
            ResumesEndTurn = ResumesEndTurn,
            StampedeRemaining = StampedeRemaining,
            HandEnemyHpsBefore = HandEnemyHpsBefore is null ? null : [.. HandEnemyHpsBefore],
        };
}

/// <summary>
/// Havoc's `AutoPlayFromDrawPile(..., forceExhaust: true)` child, parked inside the
/// enclosing effect on a choice it asked the player to make.
/// </summary>
/// <remarks>
/// `Havoc.OnPlay` awaits `CardCmd.AutoPlay` with its own choiceContext, so a child's
/// `CardSelectCmd.FromHand` (True Grit+) asks the player while Havoc is still in Play.
/// The child runs through the same parkable loop as a Distilled Chaos child. The
/// enclosing effect owes <see cref="EffectTailKind.ResumeNestedAutoPlay" />, which
/// finishes the child and exhausts it before that effect goes on. The list is a stack:
/// a child that is itself a Havoc parks its own child first.
/// </remarks>
internal sealed class PendingNestedAutoPlay
{
    public ParkedAutoPlay Child = new();

    /// <summary>What the child's own parked OnPlay still owes after the answer.</summary>
    public EffectTail ChildTail;

    public PendingNestedAutoPlay Clone() => new() { Child = Child.Clone(), ChildTail = ChildTail };
}

/// <summary>One automatic play parked inside its `OnPlay`.</summary>
internal sealed class ParkedAutoPlay
{
    public CardInstance Card;
    public int Play;
    public int ExtraPlays;
    public int SourceId;
    internal CardResourceContext Resources;
    public int TargetIndex = -1;
    public int CallerTargetIndex = -1;
    public int CallerAutoPlayTargetIndex = -1;
    internal EnemyState? PlayTarget;
    public int ReboundBefore;
    public bool NostalgiaReturnsToDraw;
    public bool CorruptedSkill;
    public BlockPlayContext BlockPlay;
    internal CombatEngine.PlayedCardChannels Channels;
    internal CombatEngine.PlayedCardChannels CallerChannels;
    internal EnemyState[] DeathBaseline = [];

    public ParkedAutoPlay Clone()
    {
        var copy = (ParkedAutoPlay)MemberwiseClone();
        copy.DeathBaseline = [.. DeathBaseline];
        return copy;
    }
}
