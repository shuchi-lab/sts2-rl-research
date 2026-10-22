namespace Sts2Emulator.Core;

/// <summary>What the player is being asked to choose a card for.</summary>
public enum CardSelectionKind
{
    None = 0,

    /// <summary>Headbutt: a card from the discard pile goes on top of the draw pile.</summary>
    DiscardToDrawPileTop = 1,

    /// <summary>True Grit (upgraded): a card in hand is exhausted.</summary>
    ExhaustFromHand = 2,

    /// <summary>Burning Pact: a card in hand is exhausted, and then cards are drawn.</summary>
    ExhaustFromHandThenDraw = 3,

    /// <summary>
    /// Secret Technique, Secret Weapon, Seeker Strike: a card from the draw pile goes to
    /// hand. The candidates are the draw-pile indices the card is allowed to offer, so a
    /// type filter lives in the candidate list rather than in the resolution.
    /// </summary>
    DrawPileToHand = 4,

    /// <summary>Thinking Ahead: a card in hand goes back on top of the draw pile.</summary>
    HandToDrawPileTop = 5,

    /// <summary>
    /// Purity: a card in hand is exhausted, and the screen reopens until
    /// <see cref="PendingCardSelection.Amount" /> picks are spent or the hand runs out.
    /// </summary>
    ExhaustFromHandRepeated = 6,

    /// <summary>
    /// Discovery: one of several freshly generated cards joins the hand, free for the
    /// turn. The candidates are not in any pile, so they are carried on the selection
    /// itself — see <see cref="PendingCardSelection.GeneratedCandidates" />.
    /// </summary>
    GeneratedCardToHand = 7,

    /// <summary>
    /// The Knowledge Demon's CURSE_OF_KNOWLEDGE: two curses offered, and the one chosen
    /// applies its power. The pair changes on each of its three casts, and Disintegration
    /// — always the first option — escalates 6, 7, 8.
    /// </summary>
    /// <remarks>
    /// The candidates are card models the game generates for the screen; nothing is added
    /// to a pile, so <see cref="PendingCardSelection.GeneratedCandidates" /> carries them
    /// exactly as Discovery's do. The emulator used to apply a flat Disintegration 6 with
    /// no choice at all — the same picking-for-the-player this type exists to stop.
    /// </remarks>
    CurseOfKnowledge = 8,

    /// <summary>
    /// Survivor, Acrobatics, Dagger Throw, Prepared and Hidden Daggers: a card in hand is
    /// DISCARDED, and the screen reopens until <see cref="PendingCardSelection.Amount" />
    /// picks are spent or the hand runs out — the same shape as Purity's exhaust.
    /// </summary>
    /// <remarks>
    /// All five used to discard the FIRST card in hand. That is not a small
    /// simplification: choosing what to throw away is the whole point of Survivor, and an
    /// agent told it discards the leftmost card learns a rule the game does not have.
    /// </remarks>
    DiscardFromHandRepeated = 9,

    /// <summary>
    /// Hand Trick: a Skill in hand that is not already Sly is made Sly for this turn, so
    /// discarding it later plays it. The filter lives in the candidate list, as Secret
    /// Weapon's does.
    /// </summary>
    MarkHandCardSly = 10,

    /// <summary>
    /// Nightmare: a card in hand is chosen, LEFT WHERE IT IS, and three copies of it join
    /// the hand at the start of the next turn.
    /// </summary>
    /// <remarks>
    /// The only selection so far that does not move the card it picks. Nightmare reads the
    /// choice into <c>NightmarePower</c> and the power does the work a turn later, so the
    /// pick is a question about the future rather than an edit to a pile.
    /// </remarks>
    QueueHandCardCopies = 11,

    /// <summary>
    /// Well-Laid Plans: cards in hand are chosen to survive the end-of-turn flush, up to
    /// <see cref="PendingCardSelection.Amount" /> of them, and the screen reopens until
    /// the picks are spent or nothing is left to offer.
    /// </summary>
    /// <remarks>
    /// The first selection raised OUTSIDE a card play — `WellLaidPlansPower` asks in
    /// `BeforeFlushLate`, every turn, for as long as the power stands. It is also the
    /// first that may be DECLINED: `CardSelectorPrefs(prompt, 0, Amount)` has a minimum of
    /// zero, so keeping nothing is a legal answer and the action space has to offer it.
    /// </remarks>
    RetainForNextTurn = 12,

    /// <summary>
    /// Hologram: a card from the DISCARD pile goes to hand. Headbutt's screen with a
    /// different destination — <see cref="DiscardToDrawPileTop" /> is the same pile and
    /// the same question, asked about the top of the draw pile instead.
    /// </summary>
    DiscardToHand = 13,

    /// <summary>
    /// Gambling Chip: discard ANY number of cards from hand on turn one, then draw that
    /// many. The screen reopens after every pick and can be declined at any point.
    /// </summary>
    /// <remarks>
    /// The first selection with no upper bound. `CardSelectorPrefs(prompt, 0, 999999999)`
    /// is min zero and max effectively-unlimited, where every other repeated screen so far
    /// has spent a fixed <see cref="PendingCardSelection.Amount" />. The draw is deferred
    /// to whichever answer CLOSES the screen, because the count is not known until then —
    /// `CardCmd.DiscardAndDraw(list, list.Count)` takes the whole list at once.
    /// </remarks>
    DiscardAnyThenDraw = 14,

    /// <summary>
    /// Armaments (unupgraded): one UPGRADABLE card in hand is upgraded.
    /// <c>CardSelectCmd.FromHandForUpgrade</c> filters the hand to upgradable cards and
    /// asks, so the filter lives in the candidate list the way Hand Trick's does.
    /// </summary>
    /// <remarks>
    /// The emulator upgraded the FIRST upgradable card. Armaments is a card whose entire
    /// decision is which card to improve, and an agent told it always improves the
    /// leftmost one learns a rule the game does not have. The game does auto-pick when
    /// <c>list.Count &lt;= 1</c> — with nothing to decide there is no screen — which the
    /// candidate list reproduces for free.
    /// </remarks>
    UpgradeInHand = 15,

    /// <summary>
    /// Seance: a card CHOSEN from the draw pile becomes a Soul, in place.
    /// <c>CardSelectCmd.FromCombatPile(PileType.Draw, ...)</c> then
    /// <c>CardCmd.TransformTo&lt;Soul&gt;</c> on each pick.
    /// </summary>
    /// <remarks>
    /// The emulator transformed <c>DrawPile[0]</c>. Which card you spend is the whole
    /// decision the card offers, and taking the top one is a rule the game does not have.
    /// </remarks>
    TransformDrawPileToSoul = 16,

    /// <summary>
    /// Cleanse: a card CHOSEN from the draw pile is exhausted.
    /// <c>CardSelectCmd.FromCombatPile(PileType.Draw, ..., ExhaustSelectionPrompt)</c> then
    /// <c>CardCmd.Exhaust</c>. The emulator exhausted the top card.
    /// </summary>
    ExhaustFromDrawPile = 17,

    /// <summary>
    /// Sculpting Strike: a card CHOSEN from hand, filtered to those not already Ethereal,
    /// gains the ETHEREAL keyword. The emulator gave the leftmost card RETAIN — a
    /// different keyword on a card nobody picked.
    /// </summary>
    GrantEtherealInHand = 18,

    /// <summary>
    /// Transfigure: a card CHOSEN from hand gains a REPLAY and, unless it costs X or less
    /// than nothing, costs one more for the combat. The emulator transformed a card at
    /// random into a different card, which is a different effect entirely.
    /// </summary>
    TransfigureInHand = 19,

    /// <summary>
    /// Begone: a card CHOSEN from hand becomes a MINION STRIKE, upgraded if Begone was.
    /// </summary>
    TransformHandToMinionStrike = 20,

    /// <summary>
    /// Charge: cards CHOSEN from the draw pile become MINION DIVE BOMBS, in place, upgraded
    /// if Charge was. Two of them, so the screen reopens.
    /// </summary>
    TransformDrawToMinionDiveBomb = 21,

    /// <summary>
    /// Guards: ANY NUMBER of hand cards become MINION SACRIFICES —
    /// <c>CardSelectorPrefs(prompt, 0, 999999999)</c>, so keeping none is legal and the
    /// screen reopens until the player stops or the hand runs out.
    /// </summary>
    TransformHandToMinionSacrifice = 22,

    /// <summary>
    /// Decisions, Decisions: a playable SKILL chosen from hand is AUTO-PLAYED three times.
    /// </summary>
    AutoPlaySkillThrice = 23,

    /// <summary>
    /// Heirloom Hammer: a COLOURLESS card chosen from hand is CLONED into hand, and the
    /// original stays where it is. The filter lives in the candidate list, as Hand Trick's
    /// does.
    /// </summary>
    /// <remarks>
    /// The second selection that does not move the card it picks, after Nightmare's — the
    /// answer is "which card do I want another of", not "which card do I give up".
    /// </remarks>
    CloneColorlessInHand = 24,

    /// <summary>
    /// Dual Wield: the player picks an ATTACK OR POWER in hand, and `Amount` clones of it
    /// are added to hand. The filter and the choice are the card -- it used to duplicate
    /// whatever happened to be first.
    /// </summary>
    DualWield = 25,

    /// <summary>
    /// Touch of Insanity: a card CHOSEN from hand, filtered to the ones that cost energy or
    /// stars, is made free for the rest of the combat -- `SetToFreeThisCombat`. The card
    /// stays where it is.
    /// </summary>
    FreeThisCombatInHand = 26,

    /// <summary>
    /// Ashwater: ANY NUMBER of hand cards are chosen, and only once the screen is CONFIRMED
    /// are they exhausted, one at a time in the order picked, each through
    /// <c>CardCmd.Exhaust</c> and its <c>AfterCardExhausted</c> hooks.
    /// </summary>
    /// <remarks>
    /// Gambler's Brew's screen shape -- <c>FromHand(prefs(0, 999999999))</c>, picks carried on
    /// <see cref="PendingCardSelection.Selected" />, candidates the unpicked hand indices in
    /// hand order, the skip action is the confirm -- with a different resolution. It is its
    /// own kind so an agent can tell "these go to the exhaust pile" from "these are
    /// discarded and redrawn".
    /// </remarks>
    ExhaustAnyFromHand = 27,
}

/// <summary>
/// A choice the game would raise a card-selection screen for, paused mid-play until the
/// caller answers it.
///
/// The alternative — picking on the player's behalf — is what the emulator used to do,
/// and it silently made two cards weaker than they are: Headbutt always took the most
/// recently discarded card rather than the best one, and upgraded True Grit exhausted at
/// random rather than the card you would actually pick.
///
/// While this is open, <see cref="CombatEngine.ValidActions" /> offers the candidate
/// indices and nothing else, so an agent answers it as an ordinary step.
/// </summary>
public sealed class PendingCardSelection
{
    public required CardSelectionKind Kind { get; init; }

    /// <summary>Indices into the pile the choice is made from, in pile order.</summary>
    public required List<int> Candidates { get; init; }

    /// <summary>The card that asked, so an observation can say what the choice is for.</summary>
    public required int SourceCardDefId { get; init; }

    /// <summary>The potion whose OnUse is awaiting this choice, or zero.</summary>
    public int SourcePotionId { get; init; }

    /// <summary>
    /// What the resolution does afterwards, for a choice that is not the last thing the
    /// card does — Burning Pact draws this many cards once the exhaust is chosen, and the
    /// draw has to happen after it so the newly drawn cards are not candidates.
    /// </summary>
    public int Amount { get; init; }

    /// <summary>
    /// Card def ids the choice is over, for a selection whose options do not exist in a
    /// pile yet. <see cref="Candidates" /> indexes into this rather than into the hand or
    /// the draw pile.
    /// </summary>
    public List<int> GeneratedCandidates { get; init; } = [];

    /// <summary>
    /// Cards that join the hand once the LAST pick is made — Hidden Daggers' Shivs, which
    /// the game creates after the discard and so must not be discard candidates
    /// themselves.
    /// </summary>
    public List<CardInstance> AfterSelectionToHand { get; init; } = [];

    /// <summary>
    /// Hand indices already picked on a multi-select screen that acts only once it is
    /// CONFIRMED, in the order they were picked. Gambler's Brew is the one user: its
    /// `FromHandForDiscard` returns the whole list and `DiscardAndDraw` then discards it in
    /// that order, so nothing leaves the hand until the confirm.
    /// </summary>
    public List<int> Selected { get; init; } = [];

    /// <summary>
    /// Whether declining is a legal answer, for a screen whose `CardSelectorPrefs` has a
    /// minimum of zero. The skip is offered as the action one past the last candidate,
    /// which is free: while a selection is open only candidate indices are valid, so
    /// nothing else claims that index.
    /// </summary>
    public bool Skippable { get; init; }

    /// <summary>
    /// Whether a card taken from <see cref="GeneratedCandidates" /> arrives upgraded.
    /// Splash upgrades what it offers; Discovery's upgrade removes its Exhaust instead and
    /// leaves the card alone, which is why this cannot be read off the source card.
    /// </summary>
    public bool GeneratedUpgraded { get; init; }

    /// <summary>
    /// Whether a card taken from <see cref="GeneratedCandidates" /> arrives Retained
    /// instead of free for the turn. Choices Paradox is the only source: it applies Retain
    /// to its five options and does NOT make the kept one free, where Discovery does the
    /// opposite -- so this picks between the two rather than adding to one.
    /// </summary>
    public bool GeneratedRetained { get; init; }
}
