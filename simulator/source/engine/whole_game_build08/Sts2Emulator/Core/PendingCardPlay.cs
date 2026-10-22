namespace Sts2Emulator.Core;

/// <summary>
/// A chosen card play parked inside `CardModel.OnPlayWrapper`, waiting for the selection
/// its own effect opened.
/// </summary>
/// <remarks>
/// <para>
/// The game awaits `OnPlay` -- and so the `CardSelectCmd` inside it -- before
/// `Hook.AfterCardPlayed`, before the next repeat and before the card leaves the Play
/// pile. Burning Pact is the shape: its `OnPlay` awaits the hand selection, exhausts, and
/// only then draws, and the Burning Pact itself is still in Play while all of that
/// happens. The emulator used to finish the play the moment the effect RAISED the screen,
/// so the card was already in the discard when the draw ran and a short draw pile
/// reshuffled it back in.
/// </para>
/// <para>
/// Everything here was settled when the play was set up and must not be settled again on
/// resume: the costs are already paid, the repeat count already spent its modifiers, and
/// the before-play amounts are snapshots the after-play hooks pay out. The card itself is
/// in no pile while this stands, which is where the game's Play pile is from the
/// observation's point of view.
/// </para>
/// </remarks>
public sealed class PendingCardPlay
{
    /// <summary>The physical card being played, as it left the hand.</summary>
    public CardInstance Card;

    /// <summary>The resolved energy cost that was paid, for the per-play relic hooks.</summary>
    public int EnergyToSpend;

    /// <summary>`CardPlay.Resources.EnergyValue`: what the play actually cost.</summary>
    public int EnergySpent;

    /// <summary>
    /// An admitted energy-X card's settled resources. Always default today: no admitted
    /// card pauses, and the play loop refuses one that does.
    /// </summary>
    internal CardResourceContext Resources;

    public bool FeralReturn;
    public int MonologueBefore;
    public int ReboundBefore;
    public bool NostalgiaReturnsToDraw;

    /// <summary>Repeats beyond the first play, settled by `GeneratePlayCount`.</summary>
    public int ExtraPlays;

    /// <summary>The play whose effect raised the selection; its after-play is still owed.</summary>
    public int PlayIndex;

    /// <summary>Physical-card series identity, shared by all repeats.</summary>
    public int BlockSourceId;

    /// <summary>The paused CardPlay's identity and its own block history rows.</summary>
    public BlockPlayContext BlockPlay;

    /// <summary>The parked effect's remainder, run once its selection closes.</summary>
    public EffectTail EffectTail;

    /// <summary>Rupture Strength the parked CardPlay accrued, paid at its completion.</summary>
    public int RuptureStrength;

    /// <summary>The target the player aimed the card at.</summary>
    public int TargetEnemyIndex = -1;

    /// <summary>
    /// <see cref="CombatState.PlayTarget" /> as the play was parked, by reference. The
    /// resumed CardPlay and every repeat after it keep this creature even when it is
    /// dead or the roster has shifted -- re-resolving the index would pick another one.
    /// </summary>
    internal EnemyState? PlayTarget;

    /// <summary>Reward baseline: HP at the point the play was parked.</summary>
    public int PlayerHpBefore;

    /// <inheritdoc cref="PlayerHpBefore" />
    public int[] EnemyHpsBefore = [];

    /// <summary>`OblivionPower.BeforeCardPlayed`'s per-enemy amounts.</summary>
    public int[] OblivionBefore = [];

    /// <summary>
    /// Each enemy's HP before the parked play's effect, for the death check that follows
    /// it -- a kill during the selection's own resolution is a kill by this play.
    /// </summary>
    public int[] PlayEnemyHps = [];
}
