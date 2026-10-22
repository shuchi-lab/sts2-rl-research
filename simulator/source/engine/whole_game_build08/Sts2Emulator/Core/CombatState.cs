namespace Sts2Emulator.Core;

public sealed class CombatState
{
    public CharacterId Character = CharacterId.Ironclad;
    public Run.ProgressionContext Progression = Run.ProgressionContext.AllUnlocked;

    /// <summary>
    /// The run's ascension level, which is an INPUT to enemy data rather than a
    /// difficulty label: the game picks monster damage with
    /// <c>GetValueIfAscension(level, high, low)</c>, so A8 and A10 are different
    /// numbers for the same enemy. Kept on the state so captures at different levels
    /// can be compared in one process — see Core/Ascension.cs.
    /// </summary>
    public int AscensionLevel = Ascension.DefaultLevel;

    // Player
    public int PlayerHp;
    public int PlayerMaxHp;
    public int PlayerBlock;
    public int Energy;
    public int MaxEnergy;
    public int PlayerGold;

    /// <summary>
    /// Gold a Heist gave back on its owner's death, waiting for the reward screen.
    /// </summary>
    /// <remarks>
    /// <c>HeistPower.BeforeDeath</c> calls <c>combatRoom.AddExtraReward(new GoldReward(
    /// Amount, wasGoldStolenBack: true))</c> — the player has to CLAIM it, and a capture
    /// shows it as its own row reading "80 Gold (stolen back)" beside the fight's ordinary
    /// gold. Handing it straight back mid-combat skipped that row.
    /// </remarks>
    public int StolenBackGold;

    /// <summary>
    /// `RoyaltiesPower.AfterCombatEnd` calls `AddExtraReward(new GoldReward(Amount))`. Its
    /// own row like the Heist's, and carried here for the same reason: a combat cannot
    /// reach the reward generator.
    /// </summary>
    public int RoyaltiesGold;

    /// <summary>A Fat Gremlin left the fight under its own steam rather than dying.</summary>
    /// <remarks>
    /// The emulator ends its escape by setting <c>Hp = 0</c>, which makes it
    /// indistinguishable from a kill — and the two owe the player opposite things.
    /// <c>GremlinMercNormal.CalculateGoldProportion</c> pays the fight in full when
    /// nothing escaped and nothing at all when a gremlin escaped carrying stolen gold.
    /// </remarks>
    public bool FatGremlinEscaped;

    /// <summary>
    /// The Battleworn Dummy's Battle Friend outlasted its `BattlewornDummyTimeLimitPower`
    /// and escaped, so the event pays nothing. The encounter's own `RanOutOfTime`.
    /// </summary>
    public bool BattlewornDummyRanOutOfTime;

    /// <summary>
    /// `PaelsLegion`'s `Cooldown`: turns left before the pet will double a card's block
    /// again. Set to `Turns` (2) when a doubled gain lands, decremented at the owner's
    /// side turn start.
    /// </summary>
    public int PaelsLegionCooldown;

    /// <summary>
    /// <c>GremlinMercNormal.GoldWasStolen</c>: the merc died having taken something.
    /// <c>SurprisePower.AfterDeath</c> only marks it when the total is above zero.
    /// </summary>
    public bool MercGoldWasStolen;

    // Cards
    public List<CardInstance> Hand = [];
    public List<CardInstance> DrawPile = [];
    public List<CardInstance> DiscardPile = [];
    public List<CardInstance> ExhaustPile = [];
    public List<CardInstance> ReturnToHandBeforeDraw = [];

    /// <summary>
    /// Clones queued by <c>NightmarePower</c>, delivered to hand at the start of the next
    /// turn BEFORE the draw and then dropped — the power removes itself once it fires, so
    /// this is a one-shot queue and not a standing effect.
    /// </summary>
    public List<CardInstance> CopiesToHandBeforeDraw = [];

    /// <summary>
    /// Set while a `RetainForNextTurn` screen stands between the player ending their turn
    /// and the turn actually ending — the end turn is owed and runs as soon as the screen
    /// is answered.
    /// </summary>
    public bool EndTurnAwaitingSelection;

    public PendingEnemyTurn? PendingEnemyTurn;

    internal PendingTurnSetup? PendingTurnSetup;

    // Rupture tracks self-damage by the resolving card and pays after its effect.
    public bool DeferCardRupture;
    public int CardRuptureStrength;

    // RupturePower pays a card's accrued Strength at that CardPlay's AfterCardPlayed, not
    // when its effect returns. An engine-owned play hands its total here for the play
    // loop to pay at completion -- or to carry across a paused selection.
    public int CompletedCardRuptureStrength;

    /// <summary>
    /// What the effect that just opened a selection still owes once it is answered
    /// (Brand's Strength). Moved onto <see cref="PendingCardPlay.EffectTail" /> when the
    /// play parks.
    /// </summary>
    public EffectTail PendingEffectTail;

    /// <summary>
    /// `AfterimagePower.BeforeCardPlayed` records the power's amount for the card about to
    /// be played, and `AfterCardPlayed` spends THAT rather than whatever the amount has
    /// become. Read before the card resolves for the reason its own Data comment gives:
    /// so an Afterimage does not pay out on its own play.
    /// </summary>
    public int AfterimageBeforePlay;

    /// <summary>
    /// The same reading, for the two powers that pay out AFTER a Power card resolves.
    /// `StormPower` and `SubroutinePower` both keep a
    /// `Dictionary&lt;CardModel, int&gt; amountsForPlayedCards` filled in
    /// `BeforeCardPlayed`, with the same comment on it that `AfterimagePower` has: it
    /// stops the power triggering on its own play, and stops a second copy paying out
    /// twice on the turn it arrives.
    /// </summary>
    public int StormBeforePlay;

    /// <inheritdoc cref="StormBeforePlay" />
    public int SubroutineBeforePlay;

    /// <summary>
    /// Status cards drawn this turn, for `IterationPower` — which fires only on the FIRST
    /// one, and so needs the count rather than a flag per draw.
    /// </summary>
    public int StatusCardsDrawnThisTurn;

    /// <summary>
    /// `Rng.CombatOrbGeneration`: the stream Chaos rolls its orb type on. Kept apart from
    /// the card-generation and target streams for the same reason those are kept apart
    /// from each other — sharing one desynchronises everything downstream.
    /// </summary>
    public CountingRandom? OrbGenerationRng;

    /// <summary>
    /// Whether the card currently resolving is tagged <c>CardTag.Defend</c>, which is what
    /// `FastenPower.ModifyBlockAdditive` asks about the block's `cardSource`. Set for the
    /// duration of the card so any block it gains carries the tag, rather than every arm
    /// that blocks having to remember to say so.
    /// </summary>
    public bool ResolvingDefendCard;

    /// <summary>
    /// `EnergyCost.SetThisCombat(n)` on the card being played — Momentum Strike zeroes its
    /// own cost for the rest of the combat. `int.MinValue` means the card said nothing.
    /// </summary>
    /// <remarks>
    /// Handed back through the state for the same reason `PlayedCardCostBump` is:
    /// `CardEffects.Apply` takes the card BY VALUE, so an arm that assigns to its own
    /// parameter changes nothing at all. Momentum Strike did exactly that and had been
    /// free-after-one-play only in the local variable.
    /// </remarks>
    public int PlayedCardCostForCombat = int.MinValue;
    public List<CardInstance> AutoPlayQueue = [];

    // Defect-style orb queue.
    public List<OrbState> Orbs = [];

    /// <summary>
    /// `OrbQueue.Capacity`. Zero for everyone but the Defect: `BaseOrbSlotCount` is a
    /// CharacterModel property and only <c>Defect.cs</c> overrides it, to 3. This used to
    /// default to 3 for every combat, which handed the Ironclad the Defect's whole queue.
    /// </summary>
    public int OrbCapacity;

    /// <summary>
    /// `Character.BaseOrbSlotCount`, which `OrbCmd.Channel` reads to decide whether a
    /// slotless character should be GIVEN a slot: a character with none of their own gets
    /// one the first time they channel, and a Defect whose slots have been taken to zero
    /// does not.
    /// </summary>
    public int BaseOrbSlots;

    // Necrobinder pet state.
    public int OstyHp;
    public int OstyMaxHp;

    // Regent star resource.
    public int Stars;

    /// <summary>
    /// Energy spent since Orbit was played, for `OrbitPower`'s every-fourth payout. Its
    /// own counter rather than `EnergySpentThisTurn` because the game's is per POWER
    /// INSTANCE and cumulative over the combat, not per turn.
    /// </summary>
    public int OrbitEnergySpent;

    /// <summary>
    /// Each enemy's Strangle amount as it stood BEFORE the current card resolved, so a
    /// card that applies or stacks Strangle does not make the enemy pay for itself.
    /// </summary>
    public List<int> StrangleBeforePlay = [];

    /// <summary>
    /// `SerpentFormPower`'s per-CardPlay record: the amount at BeforeCardPlayed, one entry
    /// per play in flight, innermost last. A play nested inside another's effect (a Sly
    /// discard, an auto-play) pushes and pops above its parent, and a play parked on a
    /// selection keeps its entry until it resumes and completes.
    /// </summary>
    public List<int> SerpentFormSnapshots = [];

    // Potions: slot index → potion def ID, 0 = empty
    public int[] PotionSlots = new int[Run.RunConstants.PotionSlotCapacity];

    /// <summary>Every card the fight is holding, wherever it sits.</summary>
    public IEnumerable<CardInstance> AllCards() =>
        Hand.Concat(DrawPile).Concat(DiscardPile).Concat(ExhaustPile);

    public int MaxPotionSlots = 3;

    // Relics
    public List<RelicInstance> Relics = [];

    // Buffs/debuffs on the player
    public List<BuffState> PlayerBuffs = [];

    /// <summary>The original HexPower applier; its unprevented death removes Hex.</summary>
    public EnemyState? HexApplier;

    /// <summary>The original ShrinkPower applier; its death releases the player.</summary>
    public EnemyState? ShrinkApplier;

    /// <summary>
    /// Nonzero while one all-target damage call is still hitting its targets. Only
    /// transient inside that call, so it is never cloned: `CreatureCmd.Damage` modifies
    /// and applies every target's damage before any kill runs `ShrinkPower.AfterDeath`.
    /// </summary>
    public int AllTargetDamageDepth;

    /// <summary>
    /// The player's duration debuffs as the round began, for the one-tick grace the game
    /// gives anything applied to a player-side creature (PowerCmd sets
    /// SkipNextDurationTick). See CombatEngine.TickDurationDebuffs.
    /// </summary>
    public List<BuffState> PlayerDebuffsAtRoundStart = [];

    // Enemies
    public List<EnemyState> Enemies = [];
    public int EncounterId;
    public bool IsEliteCombat;

    // Shuffle RNG (RunRngSet.shuffle subsystem) — used for mid-combat discard reshuffles.
    // Null falls back to the combat RNG (only valid when no pre-shuffle was done).
    // CountingRandom tracks total Next() calls so RunEngine can sync its shuffle RNG.
    public CountingRandom? ShuffleRng;

    // Target RNG (RunRngSet.combat_targets subsystem) — used whenever an effect picks
    // which enemy to hit (Juggernaut, Volley, Sword Boomerang). Null falls back to the
    // combat RNG (only valid in single-combat tests).
    public CountingRandom? TargetRng;

    // Card-selection RNG (RunRngSet.combat_card_selection subsystem) — used whenever an
    // effect picks WHICH existing card to exhaust, transform or otherwise act on (Cinder,
    // Thrash, unupgraded True Grit, Entropy). Distinct from combat_card_generation, which
    // rolls up a NEW card (Infernal Blade, Splash, Stoke). Null falls back to the combat
    // RNG (only valid in single-combat tests).
    public CountingRandom? CardSelectionRng;

    // Card-generation RNG (RunRngSet.combat_card_generation subsystem) — used when an
    // effect rolls up a NEW card (Stoke, Splash, Infernal Blade, Discovery). Distinct
    // from combat_card_selection, which picks among cards that already exist. Null falls
    // back to the combat RNG (only valid in single-combat tests).
    public CountingRandom? CardGenerationRng;

    // Potion-generation RNG (RunRngSet.combat_potion_generation subsystem) — used when a
    // card rolls up a potion in combat (Alchemize). Null falls back to the combat RNG
    // (only valid in single-combat tests).
    public CountingRandom? PotionGenerationRng;

    // AI RNG (RunRngSet.monster_ai subsystem) — used for enemy intent selection.
    // Null falls back to the combat RNG (used in single-combat tests).
    public Random? AiRng;

    // Niche HP RNG — used ONLY for SetUniqueMonsterHpValue (CreateEnemy HP calls).
    // When non-null, CreateEnemy uses this instead of the main combat RNG for HP.
    // CountingRandom.CallCount tracks how many HP values were drawn (= enemy count).
    public CountingRandom? NicheHpRng;

    // Enemy max HPs already drawn when the event that starts this fight built its combat
    // layout (`EventCombatSynchronizer.InitializeForEvent`). The fight reuses those
    // creatures, so CreateEnemy takes these in roster order and draws no Niche for them.
    // Read once while the roster is built, then cleared.
    public List<int>? PresetEnemyHps;

    // A card-selection screen the play is waiting on. Non-null blocks every other
    // action until it is answered — see PendingCardSelection.
    public PendingCardSelection? PendingSelection;

    // The chosen card play that raised PendingSelection, parked until the selection
    // closes -- its after-play hooks, repeats and placement are still owed. See
    // PendingCardPlay.
    public PendingCardPlay? PendingCardPlay;

    // True while the engine plays a card automatically. A resumable caller also sets
    // AutoPlayMayPause and retains the parent continuation until a choice is answered.
    // Older callers without that continuation still resolve selections automatically.
    public bool AutoPlaying;

    /// <summary>
    /// Allows an automatic play to open a player selection. Its matching pending
    /// continuation must finish the play and resume the parent phase after the answer.
    /// </summary>
    public bool AutoPlayMayPause;

    /// <summary>
    /// A Distilled Chaos, Mayhem, History Course or Stampede play waiting on a child's selection.
    /// </summary>
    internal PendingPotionAutoPlays? PendingPotionAutoPlays;

    /// <summary>Havoc children parked on a selection, innermost first.</summary>
    internal List<PendingNestedAutoPlay> PendingNestedAutoPlays = [];

    // Damage to add to the copy currently being played, before it lands in a pile. Set by
    // CardEffects during Apply and consumed by CombatEngine.PlayCard, because Apply takes
    // the CardInstance by value and cannot hand a mutation back any other way.
    public decimal PlayedCardBonusDamage;
    public int PlayedCardMomentumDamage;

    /// <summary>The same channel for block. Genetic Algorithm is the only card using it.</summary>
    public int PlayedCardBonusBlock;

    /// <summary>
    /// Energy SPENT this turn, which Helix Drill counts its hits from — the sum of the
    /// turn's `EnergySpentEntry` amounts. Not the energy REMAINING, which is what the
    /// emulator was reading and is very nearly its opposite.
    /// </summary>
    public int EnergySpentThisTurn;

    /// <summary>`PenNib`: whether the card now resolving is the tenth Attack, and doubled.</summary>
    public bool PenNibArmed;

    /// <summary>
    /// `JossPaper` banks Ethereal exhausts and folds them in at `AfterSideTurnEnd` rather
    /// than counting them as they happen — the relic says so explicitly.
    /// </summary>
    public int EtherealExhaustsThisTurn;

    /// <summary>`Vambrace` doubles the FIRST card block of a combat; this latches it.</summary>
    public bool VambraceSpent;
    public int VambraceTriggerSourceId;
    public int PaelsLegionAffectedPlayId;

    /// <summary>
    /// `BeatingRemnant.DamageReceivedThisTurn` — the running total its 20-per-turn cap is
    /// measured against, reset at the owner's side-turn start.
    /// </summary>
    public int UnblockedDamageThisTurn;

    /// <summary>`RainbowRing`'s three counters and its once-a-turn latch.</summary>
    public int RainbowRingAttacks;

    /// <inheritdoc cref="RainbowRingAttacks" />
    public int RainbowRingSkills;

    /// <inheritdoc cref="RainbowRingAttacks" />
    public int RainbowRingPowers;

    /// <inheritdoc cref="RainbowRingAttacks" />
    public bool RainbowRingPaidThisTurn;

    /// <summary>
    /// `UnsettlingLamp`: whether the doubling has been used up for this combat, and
    /// whether the card CURRENTLY resolving is the one that claimed it. Two flags because
    /// the card that claims it keeps the doubling for the rest of its own resolution.
    /// </summary>
    public bool UnsettlingLampSpent;

    /// <inheritdoc cref="UnsettlingLampSpent" />
    public bool UnsettlingLampCard;

    /// <summary>
    /// `GamblingChip`: how many cards its turn-one screen has discarded so far, which is
    /// how many are drawn when the screen closes.
    /// </summary>
    public int GamblingChipDiscarded;

    /// <summary>
    /// Whether Gambling Chip's screen is owed but could not be raised because another one
    /// was already up. Only Toolbox does that, and only on turn one.
    /// </summary>
    public bool GamblingChipOwed;

    /// <summary>`BurningSticks`: whether its once-per-combat copy has been spent.</summary>
    public bool BurningSticksUsed;

    /// <summary>
    /// `BeltBuckle`: whether its Dexterity is currently applied. Tracked rather than
    /// recomputed because the grant has to be REMOVED when the belt fills again.
    /// </summary>
    public bool BeltBuckleApplied;

    /// <summary>Whether this combat is an ELITE room's, which Sling of Courage reads.</summary>
    public bool IsEliteRoom;

    /// <summary>
    /// Whether the player has taken UNBLOCKED, blockable damage this combat — Lava Lamp's
    /// condition.
    /// </summary>
    /// <remarks>
    /// Set from `DealDamageToPlayer`, which is the blockable path: block is subtracted
    /// there, so anything left is damage the player could have blocked and did not.
    /// Unblockable HP loss goes through `LoseHp` instead and correctly does not count,
    /// which is what the relic's `props.HasFlag(ValueProp.Unblockable)` check is for.
    /// </remarks>
    public bool TookUnblockedDamage;

    /// <summary>
    /// A once-per-combat enchantment on the card being played has just fired, so the copy
    /// that lands in its result pile must carry the spent flag. CardEffects takes the card
    /// by value and cannot hand a mutation back, which is the same reason
    /// PlayedCardBonusDamage exists.
    /// </summary>
    public bool PlayedCardEnchantSpent;

    /// <summary>
    /// The card just played carries an enchantment that GROWS on play — Goopy's.
    /// </summary>
    /// <remarks>
    /// Handed back through the state for the same reason <see cref="PlayedCardEnchantSpent"/>
    /// is: CardEffects takes the card by value, so the played copy has to be rebuilt by
    /// the caller that files it away.
    /// </remarks>
    public bool PlayedCardEnchantGrew;

    /// <summary>
    /// How much the card being resolved raised its OWN cost for the rest of the combat.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="PlayedCardEnchantGrew"/> and for the same reason: the
    /// effect runs while the card is out of every pile, so what it did to itself has to
    /// be carried until the card is written back. Frantic Escape's
    /// `EnergyCost.AddThisCombat(1)` is the only user so far.
    /// </remarks>
    public int PlayedCardCostBump;

    /// <summary>
    /// The block a Toric Toughness will hand back when block clears. The power's own
    /// `Amount` is the TURN COUNT -- the game keeps the block in a `BlockVar` set by
    /// `SetBlock`, because the counter slot is taken.
    /// </summary>
    public int ToricToughnessBlock;

    /// <summary>
    /// The run's <c>combat_energy_costs</c> stream, which Slither re-rolls its cost from
    /// every time it is drawn. Null falls back to the combat rng, as the other streams do.
    /// </summary>
    public CountingRandom? EnergyCostRng;

    /// <summary>
    /// Whispering Earring's turn-one auto-play has run. The game enters the pre-play phase
    /// ONCE per turn; <c>CombatEngine.Step</c> is called many times inside that turn, so
    /// without this the loop would re-enter on every action the player took on turn one.
    /// </summary>
    public bool WhisperingEarringSpent;

    // Turn tracking
    /// <summary>
    /// `PlayerCombatState.TurnNumber - 1`: every player turn, extra ones included. The
    /// turn-number gates on relics and cards read this.
    /// </summary>
    public int Turn;

    /// <summary>
    /// `CombatState.RoundNumber`, from 1. `CombatManager.SwitchSides` raises it only on the
    /// enemy-to-player switch, never for an extra player turn (Ambergris, Pael's Eye), so
    /// it falls behind <see cref="Turn" /> + 1 once one is taken. PlatingPower reads it.
    /// </summary>
    public int RoundNumber = 1;
    public bool PlayerTurn = true;
    public bool SkillPlayedWhileSmoggy;

    /// <summary>
    /// CHOSEN card plays this turn (Brilliant Scarf skips `IsAutoPlay`). SlothPower keeps
    /// its own count, <see cref="SlothCardsPlayedThisTurn" />.
    /// </summary>
    public int CardsPlayedThisTurn;

    public int AttackCardsPlayedThisTurn;

    /// <summary>
    /// Own Attack CardPlays STARTED this turn -- `History.CardPlaysStarted`, once per play
    /// of a repeated card and counting auto-played and nested plays. JugglingPower reads
    /// it at BeforeCardPlayed; <see cref="AttackCardsPlayedThisTurn" /> stays the
    /// after-play count its own consumers expect.
    /// </summary>
    public int AttackPlaysStartedThisTurn;

    /// <summary>
    /// Own CardPlays of every type STARTED this turn -- `History.CardPlaysStarted`, once
    /// per play of a repeated card, auto-played and nested plays included, and recorded
    /// after BeforeCardPlayed listeners and before OnPlay. `RingingPower.ShouldPlay`
    /// refuses once this is non-zero,
    /// so a card drawn by a play still in progress is refused too.
    /// </summary>
    public int CardPlaysStartedThisTurn;

    /// <summary>Nostalgia reads prior started Attack/Skill plays when fixing a result pile.</summary>
    public int AttackOrSkillPlaysStartedThisTurn;
    public int AttackOrSkillCardsPlayedThisTurn;
    public int CardPlaysThisTurn;

    /// <summary>
    /// The last Attack the player finished playing THIS turn, and the one they finished
    /// last turn. Automatic plays of real cards (Havoc's) count; only dupes do not.
    /// History Course reads the second at the start of every turn from two onwards and
    /// auto-plays a DUPE of it.
    /// </summary>
    /// <remarks>
    /// Two fields rather than a history list because that is all anything asks for. The
    /// game queries `CardPlaysFinished.LastOrDefault(HappenedLastPlayerTurn &amp;&amp;
    /// Type == Attack &amp;&amp; !IsDupe)` -- the `!IsDupe` matters: the dupe History Course
    /// itself plays must not become next turn's answer, or the relic would latch onto one
    /// card forever.
    /// </remarks>
    public CardInstance? LastAttackThisTurn;

    public CardInstance? LastAttackLastTurn;
    public int CardsPlayedThisCombat;
    public int DrawnCardsSinceAutomationProc;

    /// <summary>
    /// Every card drawn this combat. Murder's damage is one per entry —
    /// `CalculatedDamageVar.WithMultiplier(count of CardDrawnEntry for this player)`,
    /// read off the combat HISTORY, so it counts the whole fight and not the turn.
    /// </summary>
    public int CardsDrawnThisCombat;

    /// <summary>
    /// Shivs whose play has FINISHED this turn. Phantom Blades' bonus lands on the
    /// first Shiv of the turn only — the power counts `CardPlayFinishedEntry` rows
    /// tagged Shiv and pays nothing once any exist.
    /// </summary>
    public int ShivsPlayedThisTurn;

    /// <summary>
    /// Extra card-reward rows this combat has earned. The Hunt adds one for every
    /// enemy its attack KILLS — `combatRoom.AddExtraReward(new CardReward(..., 3, ...))`.
    /// Carried here rather than pushed straight at the run, exactly as a Heist's
    /// `StolenBackGold` is, because a combat cannot reach the reward generator.
    /// </summary>
    public int ExtraCardRewards;

    /// <summary>
    /// Cards a Thieving Hopper died holding. SwipePower creates SpecialCardRewards;
    /// only claiming those rewards returns the cards to the permanent deck.
    /// </summary>
    public List<CardInstance> StolenBackCards = [];

    public bool TracksRunDeck;
    public List<DeckCardOrigin> StolenFromDeck = [];

    /// <summary>
    /// What the enemy loop ACTUALLY called during the turn just past, five ints per
    /// creature: def id, intent type, intent magnitude, and the player's deck size before
    /// and after. Cleared as the enemy phase begins.
    /// </summary>
    /// <remarks>
    /// Introspection for differential testing, not gameplay. The sweep can already see
    /// which creatures the emulator HOLDS and what they announce; it could not see which
    /// ones the loop actually called, and that gap left a Fabricator's Noisebot -- present,
    /// correctly intended, and provably able to add its cards -- silently adding nothing.
    /// </remarks>
    public List<int> ExecutedThisEnemyTurn = [];

    /// <summary>
    /// The enemy an auto-play was given EXPLICITLY, or -1 when it has to pick its own.
    /// </summary>
    /// <remarks>
    /// `CardCmd.AutoPlay` takes a target parameter. Almost everything that auto-plays
    /// passes null and the card rolls `Rng.CombatTargets` for itself — but Knife Trap
    /// hands each Shiv it replays the target the TRAP was aimed at, so those plays must
    /// not roll at all. Rolling for them would hit the wrong creature and move the
    /// stream for everything after it.
    /// </remarks>
    public int AutoPlayTargetIndex = -1;

    // Transient synchronous dispatch nesting; a clone starts outside the active call.
    internal int EnemyDeathDispatchDepth;

    // The rng of the card effect now resolving, so an attack inside it can pay its kills'
    // death hooks on the same stream the play's own dispatch would. Transient, as above.
    internal Random? CardEffectRng;

    // Where a chosen play records the roster just before an attack's mid-effect death
    // pass, for the paused-play reward. Transient, as above.
    internal AwaitedDeathPrePass? AwaitedDeathPrePass;

    // The powered attack command whose hits are resolving, and the last one issued, so a
    // hit knows which command's AfterAttack it reports to. The active one is transient;
    // the last one is cloned because enemies hold ids issued from it.
    internal int ActiveAttackCommand;
    internal int LastAttackCommand;
    public int CardsPlayedSincePanacheProc;
    public int BlockGainsThisTurn;
    public int NextBlockPlayIdentity;
    public BlockPlayContext ActiveBlockPlay;

    /// <summary>
    /// The settled resources of the admitted energy-X activation whose effect is resolving:
    /// `EnergyCost.CapturedXValue` and the play's resource value. Default (not captured)
    /// outside such an effect. Set per iteration by the play loops and restored in their
    /// `finally`, so a nested play cannot leak its capture into its caller.
    /// </summary>
    internal CardResourceContext ActiveCardResources;
    public int BlockHistoryEpoch;
    public int BlockHistoryTurn = -1;
    public bool BlockHistoryPlayerTurn;
    public int PlayerHpLostThisTurn;
    public int CardsExhaustedThisTurn;

    /// <summary>
    /// `CardDiscardedEntry` rows this turn -- one per card `CardCmd.DiscardAndDraw` puts in
    /// the discard pile, which in the emulator is `CardEffects.DiscardMovedCards`. The
    /// end-of-turn flush writes none. Memento Mori reads it.
    /// </summary>
    public int CardsDiscardedThisTurn;
    public int LightningOrbsChanneledThisCombat;
    public int EtherealExhaustCount; // number of cards exhausted by Ethereal this turn (Dark Embrace)

    /// <summary>
    /// `CardPlayFinishedEntry.WasEthereal` counted over the whole combat history: how many
    /// Ethereal cards the player has FINISHED playing. Pull From Below's hit count.
    /// Distinct from <see cref="EtherealExhaustCount" />, which counts cards Ethereal
    /// EXHAUSTED at end of turn and resets every turn.
    /// </summary>
    public int EtherealCardPlaysThisCombat;

    /// <summary>
    /// Set while the card being resolved is the FIRST Attack of the turn, which is the
    /// condition `LethalityPower` pays out on. A flag rather than a re-derivation inside
    /// the damage maths, because the attack counter has not been bumped yet while the card
    /// is resolving and a repeat play must not qualify twice.
    /// </summary>
    public bool LethalAttackPlay;

    /// <summary>
    /// `CardPlaysFinished` filtered to SKILLS the player finished this turn, which Lunar
    /// Blast spends as its hit count. Its own counter rather than a filter over history,
    /// for the same reason every other per-turn tally here is.
    /// </summary>
    public int SkillCardsPlayedThisTurn;

    /// <summary>
    /// `MusicBox.WasUsedThisTurn`: the box copies the FIRST Attack of each turn and then
    /// sits out the rest of it. Cleared at the player turn boundary and at combat end,
    /// which is where the game clears it too.
    /// </summary>
    /// <summary>
    /// `ChainsOfBindingPower`: how many cards it has BOUND this turn, and whether a bound
    /// card has already been played. The first caps the affliction at the power's amount;
    /// the second is the restriction -- at most ONE bound card may be played per turn.
    /// </summary>
    public int BoundCardsThisTurn;

    public bool BoundCardPlayedThisTurn;

    public bool MusicBoxUsedThisTurn;

    /// <summary>
    /// `FurCoat`: this fight is in one of the coat's marked rooms, so every enemy in it
    /// arrives on ONE hp -- the opening roster and anything that joins later alike.
    /// </summary>
    /// <remarks>
    /// A COMBAT flag rather than a run lookup, because the thing that has to answer it is
    /// `AfterCreatureAddedToCombat` -- which fires mid-fight, where the map coordinate is
    /// not in reach.
    /// </remarks>
    public bool FurCoatBites;

    /// <summary>
    /// `CardPlaysFinished.Count(HappenedLastPlayerTurn)`. Pale Blue Dot draws more only when
    /// the player finished at least five plays LAST turn, so the count has to survive the
    /// reset that clears the current one.
    /// </summary>
    public int CardPlaysLastTurn;

    /// <summary>
    /// `CardDrawnEntry` rows this turn with `!FromHandDraw` -- every draw that was not the
    /// turn's opening hand. Death March's damage scales with it.
    /// </summary>
    public int ExtraCardsDrawnThisTurn;

    /// <summary>
    /// A `PowerReceivedEntry` for DoomPower with the player as Applier, this turn. Death's
    /// Door reads it, and nothing else does -- so it is a flag rather than a count.
    /// </summary>
    public bool DoomAppliedThisTurn;

    /// <summary>
    /// `CreatureAttackedEntry` rows this turn whose Actor is OSTY. Flatten costs nothing
    /// once the pet has swung at all, and Rattle hits `1 +` this many times.
    /// </summary>
    /// <remarks>
    /// Counted once per Osty ATTACK, which is once per card that swings -- including a
    /// swing that lands on several enemies at once, like High Five's. Whether the game logs
    /// one entry per attack or one per creature attacked is not settled by the source, and
    /// only a Rattle played after a High Five tells them apart.
    /// </remarks>
    public int OstyAttacksThisTurn;

    /// <summary>
    /// The sum of `StarsModifiedEntry.Amount` over the POSITIVE entries this turn -- stars
    /// GAINED, not stars held. Radiate hits that many times, so spending them does not
    /// reduce it and starting the turn with a pile does not raise it.
    /// </summary>
    public int StarsGainedThisTurn;

    /// <summary>
    /// `CardModel.LastStarsSpent` for the play in flight. Stardust reads the stars it SPENT
    /// as its hit count, and by the time its effect runs the counter is already zero --
    /// `SpendResources` takes them before `OnPlay`.
    /// </summary>
    public int PlayedCardStarsSpent;

    /// <summary>
    /// `VoidFormPower`'s own `cardsPlayedThisTurn`, which is not the engine's: it counts
    /// only plays that are neither auto-plays nor repeats (`!IsAutoPlay &&
    /// IsLastInSeries`), and it resets at the side turn's START rather than with the rest.
    /// </summary>
    public int VoidFormCardsPlayedThisTurn;

    /// <summary>
    /// `PlayerCmd.EndTurn(canBackOut: false)` called from inside a card's OnPlay — Void Form
    /// ends the turn as part of playing it. Set during the play and honoured once it has
    /// finished resolving.
    /// </summary>
    public bool EndTurnAfterPlay;

    /// <summary>
    /// `CardGeneratedEntry` count for this player over the whole combat, which Supermassive
    /// spends as its damage scale. Counted at `NoteGeneratedCard`, the same chokepoint
    /// Arsenal and Pillar of Creation pay from.
    /// </summary>
    public int CardsGeneratedThisCombat;

    /// <summary>
    /// Cards played this turn while <see cref="BuffId.Tender" /> was up, which is what
    /// `TenderPower` gives back at the end of the player's turn. The power takes 1
    /// Strength and 1 Dexterity per card and restores exactly what it took, so the count
    /// has to be carried rather than recomputed.
    /// </summary>
    public int TenderCardsPlayedThisTurn;

    /// <summary>
    /// `SlothPower._cardsPlayedThisTurn`: the power's own count, which is also its
    /// DisplayAmount. `BeforeCardPlayed` bumps it once per CardPlay while the power
    /// stands -- repeats and automatic plays included -- and `BeforeSideTurnStart` clears
    /// it. A newly created power starts from zero, so plays made before the power existed
    /// do not count. Its Amount (the cap) stays on the buff.
    /// </summary>
    public int SlothCardsPlayedThisTurn;
    public int UnblockedDamageHitCount; // times player took unblocked damage this combat (TearAsunder)
    public int TargetEnemyIndex = -1; // -1 = auto (first living enemy), >=0 = specific index

    /// <summary>
    /// The creature an AnyEnemy card's activation is aimed at, held by reference for
    /// every repeat. `CardModel.OnPlayWrapper` hands each CardPlay the same `Target`, and
    /// `AttackCommand.Execute` hits only living targets, so a repeat whose target died
    /// hits nobody. <see cref="TargetEnemyIndex" /> cannot carry this on its own: a dead
    /// enemy leaves `Enemies`, and the index then names another creature or nothing.
    /// Set and restored around each activation, so it is null between steps and is not
    /// cloned; a parked play carries the reference in its PendingCardPlay, which is.
    /// </summary>
    internal EnemyState? PlayTarget;

    // ── What the player knows about draw-pile order ──────────────────────────
    // The pile is one ordered list, but the player is not entitled to all of it.
    // They know its composition, and they know where a card they deliberately
    // placed went -- and nothing else, until the next shuffle takes even that away.
    // These two counters carry that distinction so an observation can expose the
    // known part without leaking the rest, and so a determinization knows which
    // region it is allowed to resample. See docs/agent-interface.md.

    /// <summary>The first N cards of <see cref="DrawPile" /> are known, in order.</summary>
    public int KnownTopCount;

    /// <summary>The last M cards of <see cref="DrawPile" /> are known, in order.</summary>
    public int KnownBottomCount;

    /// <summary>Every shuffle takes the whole order away.</summary>
    public void ForgetDrawOrder()
    {
        KnownTopCount = 0;
        KnownBottomCount = 0;
    }

    /// <summary>Place a card on top of the draw pile, where the player can see it.</summary>
    public void TopDeck(CardInstance card)
    {
        DrawPile.Insert(0, card);
        KnownTopCount++;
        ClampKnownOrder();
    }

    /// <summary>Place a card on the bottom of the draw pile.</summary>
    public void BottomDeck(CardInstance card)
    {
        DrawPile.Add(card);
        KnownBottomCount++;
        ClampKnownOrder();
    }

    /// <summary>
    /// Put a card somewhere the player does not get to see -- a shuffle-in. Anything
    /// it lands inside stops being known, because an unknown card now sits in it.
    /// </summary>
    public void InsertIntoDrawPile(int index, CardInstance card)
    {
        DrawPile.Insert(index, card);
        if (index < KnownTopCount)
        {
            KnownTopCount = index;
        }

        int indexFromBottom = DrawPile.Count - 1 - index;
        if (indexFromBottom < KnownBottomCount)
        {
            KnownBottomCount = indexFromBottom;
        }

        ClampKnownOrder();
    }

    /// <summary>
    /// Take a card out of the draw pile, shrinking whichever known region held it.
    /// Drawing is this with index 0.
    /// </summary>
    public CardInstance RemoveFromDrawPileAt(int index)
    {
        int before = DrawPile.Count;
        var card = DrawPile[index];
        DrawPile.RemoveAt(index);
        if (index < KnownTopCount)
        {
            KnownTopCount--;
        }
        else if (index >= before - KnownBottomCount)
        {
            KnownBottomCount--;
        }

        ClampKnownOrder();
        return card;
    }

    /// <summary>Neither region may run past the pile, and they may not overlap.</summary>
    private void ClampKnownOrder()
    {
        KnownTopCount = Math.Clamp(KnownTopCount, 0, DrawPile.Count);
        KnownBottomCount = Math.Clamp(KnownBottomCount, 0, DrawPile.Count - KnownTopCount);
    }
}
