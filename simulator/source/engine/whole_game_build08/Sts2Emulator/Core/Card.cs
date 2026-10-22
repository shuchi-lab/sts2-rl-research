namespace Sts2Emulator.Core;

public enum CardType
{
    Attack,
    Skill,
    Power,
    Status,
    Curse,

    /// <summary>
    /// Quest cards (e.g. SpoilsMap). Appended rather than slotted into the game's
    /// own ordering: these ordinals are referenced across the engine and the
    /// observation encoding, so inserting would silently reinterpret existing data.
    /// </summary>
    Quest,

    /// <summary>
    /// The game's <c>CardType.None</c>, appended for the same reason Quest was: these
    /// ordinals are referenced across the engine and the observation encoding, so
    /// inserting would silently reinterpret existing data. Only a card that has not been
    /// through Tinker Time uses it, so nothing reads it as a real type.
    /// </summary>
    None,
}

public enum CardRarity
{
    Basic,
    Common,
    Uncommon,
    Rare,
    Status,
    Curse,
    Special,
    Ancient,
    Event,
    Token,

    /// <summary>Quest rarity — appended; see CardType.Quest.</summary>
    Quest,
}

/// <summary>
/// The game's <c>TargetType</c>: whether playing this card picks a creature.
/// </summary>
/// <remarks>
/// Only <see cref="AnyEnemy"/> performs target selection, which is what decides whether
/// a play carries a creature <c>Target</c> — and so whether the Kaiser Crab's
/// <c>SurroundedPower</c> turns the player toward it. An AllEnemies attack does not.
/// </remarks>
public enum CardTarget
{
    None,
    Self,
    AnyEnemy,
    AllEnemies,
    RandomEnemy,
    AnyPlayer,
    AnyAlly,
    AllAllies,
    TargetedNoCreature,
    Osty,
}

public readonly record struct CardDef(
    int Id,
    string Name,
    int Cost,
    int BaseDamage,
    int BaseBlock,
    int UpgradeDamage,
    int UpgradeBlock,
    int UpgradeCost,
    CardType Type,
    CardRarity Rarity,
    CardTarget Target = CardTarget.Self,
    bool Ethereal = false,
    bool Exhaust = false,
    bool Unplayable = false,
    bool Retain = false,
    // Innate is declared two different ways in the game: in the card's
    // CanonicalKeywords (always innate), or added by OnUpgrade (innate only once
    // upgraded).  Keep them separate — see CardInstanceExtensions.IsInnate.
    bool Innate = false,
    bool InnateWhenUpgraded = false,
    // The game's CardModel.HasEnergyCostX. An X card is printed at cost 0 and spends
    // whatever is left on the bar, so the printed cost says nothing about what a play
    // actually cost — which is what CardPlay.Resources.EnergyValue reports to relics.
    bool HasEnergyCostX = false,
    // The game's CardModel.CanonicalStarCost, which defaults to -1 and means "no star
    // cost". Stars are the Regent's SECOND resource: `PlayerCombatState.
    // HasEnoughResourcesFor` refuses a play whose star cost exceeds them, and
    // `CardModel.SpendResources` takes them after the energy. Nothing upgrades a star cost
    // -- `UpgradeStarCostBy` has no callers -- so the printed number is the whole story.
    int StarCost = -1,
    // The game's CardModel.HasStarCostX, which Stardust alone declares: it spends EVERY
    // star and reads the count back as its hit count, the way an X energy card does.
    bool HasStarCostX = false,
    // The game's CardModel.MultiplayerConstraint. CardFactory.FilterForPlayerCount drops
    // these from every pool in a solo run, so a single-player agent must never be offered
    // one — 21 cards, and the emulator's pools were built without the filter.
    bool MultiplayerOnly = false,
    // The game's ModelId.Entry — the slugified class name. The mid-combat reshuffle
    // sorts the pile by ModelId before Fisher-Yates (ListExtensions.StableShuffle), and
    // ModelId orders by Category then Entry as ordinal strings. Our own numeric ids sort
    // differently, and a different pre-shuffle order is a different shuffle from the
    // same stream — so the pile order has to come from this, not from Id.
    string Entry = "",
    // CardModel.CanBeGeneratedByModifiers: eight curses refuse to be handed out by
    // anything that rolls one, so a curse roll has to filter on it.
    bool CanBeGeneratedByModifiers = true,
    // `CardModel.CanBeGeneratedInCombat`. Fourteen cards refuse to be rolled by an
    // in-combat generator -- Feed and The Hunt among them -- and `FilterForCombat` drops
    // them alongside the Basic, Ancient and Event rarities.
    bool CanBeGeneratedInCombat = true,
    // `CardTag.OstyAttack`. Thirteen Necrobinder cards carry it, and Squeeze's damage
    // COUNTS them -- so it is a number the emulator has to be able to derive rather than
    // a label. Generated for the reason every other hand-kept list in here was deleted.
    bool OstyAttack = false,
    // CardTag.Strike / CardTag.Defend, off each card's CanonicalTags. Amalgamator's deck
    // filter is the tag, not the name -- see IsStrikeOrDefend for what the name got wrong.
    bool StrikeTag = false,
    bool DefendTag = false,
    // CardTag.Shiv: Helical Dart reads it off the card just played. Blade of Ink's Shivs
    // and Knife Trap's replays are all tagged, so this cannot be "id == 430".
    // CardModel.HasTurnEndInHandEffect: the card does something to its holder while it
    // sits in hand at the end of the turn. Eleven cards, and only five were modelled.
    bool TurnEndInHand = false,
    bool ShivTag = false,
    // CardTag.Minion: Vitruvian Minion doubles both the damage and the block of a
    // Minion-tagged card.
    bool MinionTag = false,
    // CardModel.IsUpgradable is CurrentUpgradeLevel < MaxUpgradeLevel, and 38 cards
    // override MaxUpgradeLevel to zero -- every curse and status. This is that override,
    // read from the source rather than restated: the hand-kept list of ids it replaces
    // had 14 of the 38, and the dozen curses missing from it were silently eligible for
    // every upgrade in the game.
    bool Upgradable = true,
    // CardKeyword.Sly. A Sly card DISCARDED by an effect is auto-played --
    // `CardCmd.DiscardAndDraw` gathers the sly ones as it moves them and plays each
    // afterwards. The end-of-turn hand discard does not route through that command and so
    // does not trigger it.
    bool Sly = false,
    // Keywords an UPGRADE changes, which the flags above cannot express on their own. The
    // direction differs by keyword and is not interchangeable: twelve cards GAIN Retain
    // when upgraded, the way fifteen gain Innate, while nineteen LOSE Exhaust and three
    // lose Ethereal -- and for most of those losing it is the whole benefit of the
    // upgrade. See CardInstanceExtensions.IsRetained / IsExhaust / IsEthereal.
    bool RetainWhenUpgraded = false,
    bool ExhaustRemovedWhenUpgraded = false,
    bool EtherealRemovedWhenUpgraded = false,
    // CardKeyword.Eternal, which is `CardModel.IsRemovable => !Keywords.Contains(Eternal)`.
    // Seven curses carry it and `CardSelectCmd.FromDeckForRemoval` filters on it, so the
    // game will not so much as OFFER one for removal.
    bool Eternal = false,
    // Canonical CardModel.GainsBlock, extracted from the pinned beta sources.
    bool GainsBlock = false
);

/// <summary>
/// The game's enchantments, as <c>EnchantmentModel</c> subclasses. A card carries at most
/// ONE -- <c>CardModel.Enchantment</c> is a single reference, and the base
/// <c>CanEnchant</c> refuses any card that already has one unless the enchantment is
/// stackable with itself -- so this is a slot, not a set of flags.
/// </summary>
public enum Enchantment
{
    None = 0,

    /// <summary>Attacks only; adds its amount to the card's damage.</summary>
    Sharp,

    /// <summary>Cards that gain block; adds its amount to the card's block.</summary>
    Nimble,

    /// <summary>Draws its amount the first time the card is played each combat.</summary>
    Swift,

    /// <summary>Grants the Retain keyword.</summary>
    Steady,

    /// <summary>Basic Strikes and Defends only; the card plays one extra time.</summary>
    Spiral,

    /// <summary>Once per combat, playing the card also gains its amount of energy.</summary>
    Sown,

    /// <summary>Attacks only; 1.5x powered damage, and playing it costs 2 HP.</summary>
    Corrupted,

    /// <summary>Costs a fresh random amount (0..3) every time it is drawn to hand.</summary>
    Slither,

    /// <summary>Attacks only; adds its amount to the FIRST powered attack, then stops.</summary>
    Vigorous,

    // The four the act-2 ancients hand out. Appended, never inserted: the value is what
    // the observation carries.

    /// <summary>Skills only; starts at the bottom of the draw pile and auto-plays on turn 1.</summary>
    Imbued,

    /// <summary>Declares nothing of its own — an empty EnchantmentModel carrying an amount.</summary>
    Clone,

    /// <summary>Defend-tagged cards only; gains Exhaust.</summary>
    Goopy,

    /// <summary>Costs nothing, gains Eternal, and adds its amount to a powered attack.</summary>
    TezcatarasEmber,

    /// <summary>
    /// Blade of Ink's rider on the Shivs it makes: +1 damage on a powered attack, and Weak
    /// 1 on what the card hit. Both numbers are the enchantment's OWN vars — a
    /// <c>DamageVar(1)</c> and a <c>PowerVar&lt;WeakPower&gt;(1)</c> — rather than the
    /// amount it was applied at, which is why it declares <c>ShowAmount =&gt; false</c>.
    /// </summary>
    Inky,

    /// <summary>
    /// Kifuda's: block equal to its amount whenever the card is played, any card type.
    /// `RecalculateValues` sets a `BlockVar` from the amount, so the block IS the amount.
    /// </summary>
    Adroit,

    /// <summary>
    /// Punch Dagger's, Attacks only: every play adds its amount to a running bonus that
    /// the card then carries for the rest of the run.
    /// </summary>
    /// <remarks>
    /// The accumulation happens in `OnPlay` and the damage is read in
    /// `EnchantDamageAdditive`, which runs FIRST — so the play that adds the amount does
    /// not benefit from it. A freshly enchanted card hits for its printed damage the first
    /// time and grows from the second.
    /// </remarks>
    Momentum,

    /// <summary>
    /// Royal Stamp's, Attacks and Skills only: `OnEnchant` adds the Innate AND Retain
    /// keywords to the card, permanently. It has no play-time behaviour of its own.
    /// </summary>
    RoyallyApproved,

    /// <summary>
    /// `PerfectFit.ModifyShuffleOrder`: the card goes to the FRONT of the draw pile on
    /// every reshuffle except the initial one, so it is the next card drawn after any
    /// shuffle mid-combat. Field Of Man-Sized Holes is the only source.
    /// </summary>
    PerfectFit,

    /// <summary>
    /// `SoulsPower.OnEnchant` is `RemoveKeyword(Exhaust)` and nothing else, and its
    /// `CanEnchant` narrows the base rule to cards that HAVE Exhaust locally -- so it is
    /// the only enchantment whose eligibility is a keyword rather than a card type, and
    /// the only one that takes something away. Grave of the Forgotten is the only source.
    /// </summary>
    SoulsPower,

    /// <summary>
    /// Attacks only; DOUBLES powered damage. Tri-Boomerang is the only source.
    /// </summary>
    /// <remarks>
    /// APPENDED, not inserted. The numeric value is what the observation carries, so
    /// inserting renumbers every enchantment above it and silently invalidates every
    /// committed fixture and any trained policy. This pair was originally added after
    /// `Steady`, which moved `Spiral` from 5 to 7 and everything above it with it.
    /// </remarks>
    Instinct,

    /// <summary>
    /// The card plays one extra time, ONCE per combat. Spiral's effect with a fuse, and
    /// Glitter -- which stamps it on every eligible card reward -- is the only source.
    /// </summary>
    Glam,
}

/// <summary>
/// Mad Science's rider, chosen on Tinker Time's third page.
/// </summary>
/// <remarks>
/// Declared in the game on <c>TinkerTime</c> itself. Three riders per card type, and the
/// event offers two of the three: Attack gets Sapping/Violence/Choking, Skill gets
/// Energized/Wisdom/Chaos, Power gets Expertise/Curious/Improvement. The ordering is the
/// enum's own, which is what the event's shuffle permutes.
/// </remarks>
public enum TinkerRider
{
    None = 0,
    Sapping,
    Violence,
    Choking,
    Energized,
    Wisdom,
    Chaos,
    Expertise,
    Curious,
    Improvement,
}

/// <summary>Immutable link to the permanent card copied into a run combat.</summary>
public sealed record DeckCardOrigin(int Index, CardInstance Card);

public readonly record struct CardInstance(
    int DefId,
    bool Upgraded,
    bool FreeThisTurn = false,
    bool Retain = false,
    // At most one enchantment, with the amount it was applied at. Self-Help Book grants
    // Sharp/Nimble/Swift at 2; the event enchantments are all applied at 1.
    Enchantment Enchantment = Enchantment.None,
    int EnchantAmount = 0,
    // `EnergyCost.AddThisCombat(n)` on THIS card's model, which is per-card and not
    // player-wide: a Frantic Escape that has been played costs more, and its siblings do
    // not. Tracked here so the bump travels with the copy through the piles.
    int CostBump = 0,
    // Whether a once-per-combat enchantment on this copy has already fired. Sown, Swift
    // and Vigorous each set EnchantmentStatus.Disabled after they go off.
    //
    // Modelled as once per COMBAT because combat builds its own CardInstance list from the
    // run deck, so the flag never travels home. Nothing in the decompiled source resets
    // EnchantmentStatus, which would make it once per RUN instead -- but nothing was found
    // that copies the deck into the draw pile either, and the two readings only differ from
    // the second combat onwards. A capture of a Sown card played in two fights settles it.
    // Extra times this copy plays, from Hidden Gem's Replay. Per-copy, like BonusDamage:
    // the gem picks one card out of the draw pile and only that copy replays.
    int ReplayCount = 0,
    bool EnchantSpent = false,
    int CostForCombat = int.MinValue,
    // Damage this copy has permanently gained during the combat. Rampage raises its own
    // damage every time it is played, and the growth rides on the card rather than on the
    // player, so two Rampages in a deck grow independently.
    decimal BonusDamage = 0m,
    // Momentum owns a separate enchantment counter, not the card's DamageVar growth.
    int MomentumDamage = 0,
    // Mad Science is built at Tinker Time and nowhere else: its type and its rider are
    // chosen by the player and then SAVED ON THE CARD ([SavedProperty] on both), which
    // makes them part of the instance rather than of the definition. Default to None for
    // every other card.
    CardType TinkerType = CardType.None,
    TinkerRider TinkerRider = TinkerRider.None,
    // `CardModel.HasSingleTurnSly`, which Hand Trick sets on a chosen Skill. Sly is
    // otherwise a keyword on the DEFINITION, so this is the per-copy half of the same
    // question -- see CardInstanceExtensions.IsSlyThisTurn.
    // `CardPlayFinishedEntry.CardPlay.Card == this && HappenedThisTurn` -- whether THIS
    // copy has already been played this turn. Fetch draws only on its first play of a turn,
    // so a Fetch pulled back out of the discard and played again draws nothing, while a
    // SECOND Fetch played the same turn still does. Set as the card leaves play and cleared
    // across every pile at the turn boundary.
    bool PlayedThisTurn = false,
    bool SlyThisTurn = false,
    // `CardCmd.ApplyKeyword(card, CardKeyword.Sly)`, which Master Planner does to every
    // Skill its owner plays. Unlike Hand Trick's grant this one is permanent for the
    // combat, so it rides on the copy through the piles rather than expiring with the turn.
    bool SlyForCombat = false,
    // `CardCmd.ApplyKeyword(card, CardKeyword.Ethereal)`, which Call of the Void's power
    // does to each card it generates. Ethereal is otherwise a keyword on the DEFINITION,
    // so this is the per-copy half of the same question, and it rides on the copy through
    // the piles the way SlyForCombat does.
    bool EtherealForCombat = false,
    // `CardCmd.ApplyKeyword(card, CardKeyword.Retain)`, which Choices Paradox does to each
    // of the five cards it generates before offering them. Permanent for the combat like
    // SlyForCombat, not the single-turn grant `RetainThisTurn` models.
    bool RetainForCombat = false,
    // `DampenPower` remembers which cards it downgraded so `AfterRemoved` can put the
    // upgrade back. The game holds that in a dictionary keyed on the card; the emulator's
    // cards are values that move between piles, so the card carries the memory instead.
    bool DampenedUpgrade = false,
    // `CardCmd.AfflictAndPreview<Bound>`, which ChainsOfBindingPower puts on the cards its
    // owner draws. An AFFLICTION rather than a keyword in the game; the emulator has no
    // affliction system and Bound is the only one that does anything, so it rides the card.
    bool Bound = false,
    // `CardModel.GiveSingleTurnRetain()`, which Well-Laid Plans' power hands to the cards
    // its owner picks just before the hand is flushed. Single-turn like Hand Trick's Sly:
    // it survives one flush and is cleared as the card lands in the next hand.
    bool RetainThisTurn = false,
    // `EnergyCost.SetThisTurnOrUntilPlayed(n)`: a cost that lasts the turn rather than the
    // combat. Enlightenment's unupgraded half is the only source, and it sets 1 rather
    // than 0 -- which is why a `FreeThisTurn` flag cannot stand in for it.
    int CostThisTurn = int.MinValue,
    // `EnergyCost.SetUntilPlayed(0)`, which Rocket Punch does to ITSELF when its owner
    // generates a Status card. Unlike FreeThisTurn this survives the turn boundary and is
    // spent by the play rather than by the clock.
    bool FreeUntilPlayed = false,
    // Block this copy has permanently gained during the combat. Genetic Algorithm raises
    // its own block every time it is played, and the growth rides on the CARD -- the same
    // shape as Rampage's BonusDamage, and for the same reason: two copies grow apart.
    int BonusBlock = 0,
    DeckCardOrigin? DeckOrigin = null,
    // Snecko Oil's `EnergyCost.SetThisTurnOrUntilPlayed(roll)`: an ABSOLUTE, non-reduce-only
    // local modifier, so it may raise the cost, and it overrides every local modifier the
    // copy carried when it was set (combat cost, until-played free, relative bumps). It
    // expires at turn end or when the copy is played. `CostThisTurn` cannot stand in for
    // it: that one is Enlightenment's reduce-only form.
    int CostOverrideThisTurn = int.MinValue,
    // The per-turn relative discounts (Stomp, Pinpoint, Banshee's Cry) already on the card
    // when the override was set. Those modifiers came BEFORE it, so only the part added
    // since still applies on top.
    int CostOverrideBaseline = 0,
    // Whether the copy was already upgraded when the override was set. `UpgradeBy` lowers
    // an absolute modifier above the new base (CardEnergyCost.UpgradeBy), but only for an
    // upgrade that happens AFTER the modifier exists.
    bool CostOverrideSetUpgraded = false,
    // `CardModel.IsDupe`, set by `CreateDupe` (History Course is its only caller). A dupe's
    // result pile is `PileType.None`: once played it is removed from combat, not discarded
    // or exhausted. History Course also skips a dupe's play when it picks next turn's card.
    bool Dupe = false
);

public static class CardInstanceExtensions
{
    /// <summary>
    /// This copy's <c>CardModel.Type</c>. Mad Science overrides it with its saved
    /// <c>TinkerTimeType</c> (MadScience.cs, <c>Type => TinkerTimeType</c>), carried here
    /// as <see cref="CardInstance.TinkerType" />; its generated def holds only the class's
    /// Attack default. Every other card's type is its def's.
    /// </summary>
    public static CardType ModelType(this CardInstance card) =>
        card.TinkerType is CardType.Attack or CardType.Skill or CardType.Power
            ? card.TinkerType
            : GeneratedData.Cards.Get(Math.Abs(card.DefId)).Type;

    /// <summary>
    /// This copy's def as its <c>CardModel</c> presents it in combat. Mad Science derives
    /// <c>Type</c>, <c>TargetType</c> and <c>GainsBlock</c> from its saved
    /// <c>TinkerTimeType</c> (MadScience.cs), so a tinkered copy gets a per-copy def with
    /// those three replaced; every other card gets the shared generated def unchanged.
    /// </summary>
    /// <remarks>
    /// A combat path that reads a played card's type -- the Power result pile, Corruption,
    /// Curious, Smoggy, the play-count and free-play powers -- takes its def from here, so
    /// a Power Mad Science leaves combat like any other Power.
    /// </remarks>
    public static CardDef ModelDef(this CardInstance card)
    {
        var def = GeneratedData.Cards.Get(Math.Abs(card.DefId));
        return card.TinkerType is CardType.Attack or CardType.Skill or CardType.Power
            ? def with
            {
                Type = card.TinkerType,
                Target = card.TinkerType == CardType.Attack ? CardTarget.AnyEnemy : CardTarget.Self,
                GainsBlock = card.TinkerType == CardType.Skill,
            }
            : def;
    }

    /// <summary>
    /// Whether this card counts as Innate right now, mirroring the game's
    /// <c>CardModel.Keywords.Contains(CardKeyword.Innate)</c> for the sources the
    /// emulator models (canonical keywords + the upgrade-granted keyword).
    /// Enchantment- and power-granted keywords are not modelled yet.
    /// </summary>
    public static bool IsInnate(this CardInstance card)
    {
        var def = GeneratedData.Cards.Get(card.DefId);
        return def.Innate
            || (card.Upgraded && def.InnateWhenUpgraded)
            || card.Enchantment == Enchantment.RoyallyApproved;
    }

    /// <summary>
    /// Whether this card should be sorted to the bottom of the draw pile at the start of
    /// combat — the game's <c>EnchantmentModel.ShouldStartAtBottomOfDrawPile</c>, true
    /// only for <c>Imbued</c>.
    /// </summary>
    /// <remarks>
    /// This was a stub returning false, written against the real rule so the turn-1
    /// reorder would not silently omit half of it, and waiting for an enchantment that
    /// did not exist. Electric Shrymp brought Imbued with it.
    /// </remarks>
    public static bool StartsAtBottomOfDrawPile(this CardInstance card) =>
        card.Enchantment == Enchantment.Imbued;

    /// <summary>The amount this card's enchantment was applied at, if it is that one.</summary>
    public static int EnchantedWith(this CardInstance card, Enchantment enchantment) =>
        card.Enchantment == enchantment ? card.EnchantAmount : 0;

    /// <summary>
    /// Whether this card is Retained, from any source the emulator models: the printed
    /// keyword, or the Steady enchantment, which adds it (<c>Steady.OnEnchant</c>).
    /// </summary>
    public static bool IsRetained(this CardInstance card)
    {
        var def = GeneratedData.Cards.Get(card.DefId);
        return def.Retain
            || card.RetainForCombat
            || (card.Upgraded && def.RetainWhenUpgraded)
            || card.Enchantment == Enchantment.Steady
            // `RoyallyApproved.OnEnchant` adds Innate AND Retain -- both keywords, one
            // enchantment, which is why it reads in two places rather than one.
            || card.Enchantment == Enchantment.RoyallyApproved
            || card.RetainThisTurn;
    }

    /// <summary>
    /// Whether this copy exhausts when played. Nineteen cards DROP Exhaust when upgraded,
    /// and for most of them that is the whole benefit of the upgrade — an upgraded
    /// Discovery, Hologram or Secret Technique comes back.
    /// </summary>
    public static bool IsExhaust(this CardInstance card)
    {
        // SoulsPower.OnEnchant removes the keyword outright, so the card stops exhausting
        // for good -- the one enchantment that subtracts.
        if (card.Enchantment == Enchantment.SoulsPower)
        {
            return false;
        }

        var def = GeneratedData.Cards.Get(card.DefId);
        return def.Exhaust && !(card.Upgraded && def.ExhaustRemovedWhenUpgraded);
    }

    /// <summary>
    /// Whether this copy is Ethereal. Apparition, Echo Form and Void Form all drop it when
    /// upgraded; the emulator knew about Echo Form only, by its raw id.
    /// </summary>
    public static bool IsEthereal(this CardInstance card)
    {
        if (card.EtherealForCombat)
        {
            return true;
        }

        var def = GeneratedData.Cards.Get(card.DefId);
        return def.Ethereal && !(card.Upgraded && def.EtherealRemovedWhenUpgraded);
    }

    /// <summary>
    /// `CardModel.IsSlyThisTurn`: the Sly keyword on the definition, or a single-turn
    /// grant from Hand Trick. A Sly card DISCARDED by an effect is played instead of
    /// joining the pile.
    /// </summary>
    public static bool IsSlyThisTurn(this CardInstance card) =>
        GeneratedData.Cards.Get(card.DefId).Sly || card.SlyForCombat || card.SlyThisTurn;
}

public static class Enchantments
{
    /// <summary>
    /// Enchantments whose combat behaviour is NOT modelled. Empty now: applying one used
    /// to be recorded on the card and change nothing when it was played, so a run that
    /// took Sapphire Seed, Wood Carvings or Self-Help Book got the wrong combat.
    ///
    /// Kept as a list rather than deleted so the next unmodelled one has an obvious place
    /// to be declared, and so the test that pins the gap keeps working.
    /// </summary>
    public static readonly Enchantment[] InertInCombat = [];

    /// <summary>
    /// The game's <c>EnchantmentModel.CanEnchant</c>: Status, Curse and Quest cards are
    /// never enchantable, an Unplayable card in the deck is not either, a card that
    /// already carries an enchantment is refused (none of the modelled ones stack with
    /// themselves), and each enchantment may narrow it further.
    /// </summary>
    public static bool CanEnchant(CardInstance card, Enchantment enchantment, bool inDeck = true)
    {
        if (enchantment == Enchantment.None || card.Enchantment != Enchantment.None)
        {
            return false;
        }

        var def = GeneratedData.Cards.Get(card.DefId);
        if (def.Type is CardType.Status or CardType.Curse or CardType.Quest)
        {
            return false;
        }

        if (inDeck && def.Unplayable)
        {
            return false;
        }

        return enchantment switch
        {
            // CanEnchantCardType overrides.
            // `CanEnchantCardType(card.Type)` reads the copy's type (EnchantmentModel.cs:280).
            Enchantment.Sharp or Enchantment.Corrupted or Enchantment.Vigorous => card.ModelType()
                == CardType.Attack,
            // v0.111.0 Nimble filters GainsBlock, not the card's type. Mad Science's
            // block trait is stored on its instance; Sovereign Blade needs combat Parry
            // and does not gain block at these out-of-combat selection boundaries.
            Enchantment.Nimble => def.GainsBlock || card.TinkerType == CardType.Skill,
            Enchantment.Swift => true,

            // Instinct.CanEnchantCardType: attacks.
            Enchantment.Instinct => card.ModelType() == CardType.Attack,

            // Glam has no CanEnchant override, so the base rule is the whole rule -- which
            // is what lets Glitter reach almost every card a reward can offer.
            Enchantment.Glam => true,

            // Spiral.CanEnchant: Basic rarity, and tagged Strike or Defend.
            Enchantment.Spiral => def.Rarity == CardRarity.Basic && IsStrikeOrDefend(def),

            // Slither.CanEnchant also refuses an X-cost card.
            Enchantment.Slither => !def.HasEnergyCostX,

            // `PerfectFit` has no `CanEnchant` override of its own, so the base rule is
            // the only one: any card the enchantment system will take.
            Enchantment.PerfectFit => true,

            // Imbued.CanEnchantCardType: skills.
            Enchantment.Imbued => def.Type == CardType.Skill,

            // Momentum.CanEnchantCardType: attacks.
            Enchantment.Momentum => def.Type == CardType.Attack,

            // RoyallyApproved.CanEnchantCardType is `(uint)(cardType - 1) <= 1u`, which is
            // the GAME's Attack and Skill -- its CardType starts at 1 where this one starts
            // at 0, so the ordinals do not transfer and the names have to.
            Enchantment.RoyallyApproved => def.Type is CardType.Attack or CardType.Skill,

            // Goopy.CanEnchant: tagged Defend, at any rarity -- it has no Basic clause of
            // its own, unlike Spiral. The name stand-in this used to carry was blind to
            // Fasten and Ultimate Defend, which are Defend-tagged and are not Basic.
            Enchantment.Goopy => def.DefendTag,

            // SoulsPower.CanEnchant: the card must already have the Exhaust keyword,
            // because removing it is the entire enchantment. `KeywordSources.Local` is
            // the card's own keywords, so a card that only exhausts because something
            // else said so does not qualify -- which for a DECK card means the printed
            // keyword, minus the nineteen that shed it on upgrade.
            Enchantment.SoulsPower => card.IsExhaust(),

            _ => true,
        };
    }

    /// <summary>
    /// The game's <c>CardTag.Strike</c> / <c>CardTag.Defend</c>, extracted from each card's
    /// <c>CanonicalTags</c>.
    /// </summary>
    /// <remarks>
    /// This used to test the entry slug for a `STRIKE_` / `DEFEND_` prefix, which is true
    /// for Basic cards -- where the tag and the name agree -- and wrong past them:
    /// Perfected Strike is tagged Strike and is not Basic, and Shining Strike and Solar
    /// Strike are tagged and named alike but neither is Basic either. Amalgamator filters
    /// the deck on the real tag, so the real tag is extracted now.
    /// </remarks>
    private static bool IsStrikeOrDefend(CardDef def) => def.StrikeTag || def.DefendTag;
}
