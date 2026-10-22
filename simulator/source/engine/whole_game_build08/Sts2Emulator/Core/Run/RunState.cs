using Sts2Emulator.Core.Rng;

namespace Sts2Emulator.Core.Run;

public enum DeckSelection
{
    None = 0,

    /// <summary>Apply <c>PendingSelectionArg</c> as an <see cref="Enchantment"/>.</summary>
    Enchant,

    /// <summary>Transform the chosen card into the card id in <c>PendingSelectionArg</c>.</summary>
    TransformTo,

    /// <summary>
    /// Transform the chosen card into the card id in <c>PendingSelectionArg</c>, with NO
    /// rarity filter, carrying the original's upgrade and enchantment across.
    /// </summary>
    /// <remarks>
    /// Claws is the one that needs this. <c>TransformTo</c>'s Basic-only filter is Wood
    /// Carvings' -- it passes a predicate to <c>FromDeckForTransformation</c> and Claws
    /// passes none, so Claws will eat any transformable card in the deck.
    /// <c>CreateMaulFromOriginal</c> then copies the original's upgrade onto the Maul when
    /// the Maul is upgradable, and its enchantment when that enchantment can take.
    /// </remarks>
    TransformToCarrying,

    /// <summary>Upgrade the chosen card.</summary>
    Upgrade,

    /// <summary>Transform the chosen card into a rolled one (CardCmd.TransformToRandom).</summary>
    TransformToRandom,

    /// <summary>
    /// Transform the chosen card into a rolled one and then UPGRADE it. Astrolabe's:
    /// `CreateRandomCardForTransform`, `CardCmd.Upgrade`, then `CardCmd.Transform` -- the
    /// upgrade lands on the NEW card before it goes in, not on the old one.
    /// </summary>
    TransformToRandomUpgraded,

    /// <summary>Remove the chosen card from the deck.</summary>
    Remove,

    /// <summary>
    /// Remove the chosen card, offering only UPGRADABLE ones.
    /// </summary>
    /// <remarks>
    /// <c>FromDeckForRemoval</c> takes an optional filter and Pael's Tooth passes
    /// <c>c.IsUpgradable</c>. It is a different screen from a plain removal, not a
    /// different answer to the same one: a deck of nothing but curses offers this
    /// nothing at all where a plain removal would offer everything.
    /// </remarks>
    RemoveUpgradable,

    /// <summary>
    /// Amalgamator: remove BASIC cards carrying one tag, and the tag is in
    /// <c>PendingSelectionArg</c> -- 0 for Strike, 1 for Defend.
    /// </summary>
    /// <remarks>
    /// `IsValid(tag, c)` is `c.Tags.Contains(tag) &amp;&amp; c.Rarity == Basic &amp;&amp; c.IsRemovable`,
    /// so it is narrower than a plain removal in two directions at once: the tag, and
    /// Basic rarity. Ultimate Strike is Strike-tagged and would be offered by the tag
    /// alone, which would let the event eat its own reward.
    /// </remarks>
    RemoveTaggedBasic,

    /// <summary>
    /// Dolly's Mirror: the chosen card is COPIED into the deck, not changed.
    /// </summary>
    /// <remarks>
    /// `CardSelectCmd.FromDeckGeneric` with `c.Type != CardType.Quest` — a wider filter
    /// than any other selection here, because duplicating a curse is a legal thing to want
    /// and only Quest cards are excluded.
    /// </remarks>
    Duplicate,
}

/// <summary>An unpopulated reward an event attaches to the combat it starts.</summary>
public enum EventCombatExtraReward
{
    /// <summary>`new RelicReward(player)`: no rarity, pulled from the front at populate.</summary>
    Relic,

    /// <summary>`new PotionReward(player)`: rolled on `PlayerRng.Rewards` at populate.</summary>
    Potion,
}

/// <summary>One act's generated rooms, as <c>ActModel._rooms</c> holds them.</summary>
/// <summary>
/// Where a deck selection puts the run once it has been answered.
/// </summary>
/// <remarks>
/// Every selection used to land on <c>RunPhase.Event</c>, which is right for the events
/// that open most of them and wrong for everything else — and it is what kept the shop's
/// removal service and Empty Cage stuck choosing a card for the player instead of asking.
/// Neow is not in here: it is decided by <c>NeowAwaitingProceed</c>, which the reward
/// screens read too.
/// </remarks>
public enum SelectionReturn
{
    /// <summary>Back to the event, showing its result page.</summary>
    EventResult = 0,

    /// <summary>Back to the event with its options still up — the belt turns again.</summary>
    EventOptions,

    /// <summary>Back to the shop the service was bought from.</summary>
    Shop,

    /// <summary>On to the map: the room that opened it is finished.</summary>
    Map,

    /// <summary>
    /// Back to the rest site, with the option that opened it spent. Meat Cleaver's cook is
    /// the only rest option that asks the player anything, so it is the only one that has
    /// to come BACK -- and it has to come back to the rest screen, because Miniature Tent
    /// may still leave another option on it.
    /// </summary>
    Rest,
}

public sealed record ActRooms(
    int Act,
    int[] Events,
    int[] NormalEncounters,
    int[] EliteEncounters,
    int BossEncounterId,
    string Ancient
)
{
    /// <summary>Stands in before a run has been generated.</summary>
    public static readonly ActRooms None = new(
        RunConstants.ActOvergrowth,
        [],
        [],
        [],
        0,
        RunConstants.AncientNeow
    );
}

public sealed class RunState
{
    public CharacterId Character = CharacterId.Ironclad;
    public string StringSeed = "";
    public int AscensionLevel = Ascension.DefaultLevel;
    public RunRngSet Rng = new("0");
    public ProgressionContext Progression = ProgressionContext.AllUnlocked;
    public PlayerRngSet PlayerRng = new(new RunRngSet("0"));
    public int PlayerHp;
    public int PlayerMaxHp;
    public int Gold;
    public int Floor;
    public RunPhase Phase;
    public List<CardInstance> Deck = [];
    public List<RelicInstance> Relics = [];

    /// <summary>
    /// Relics spent for the rest of the run (the game's RelicModel.IsUsedUp). A combat
    /// rebuilds its relic list from ids, so anything one-shot per run has to be carried
    /// across the boundary rather than living on the combat's RelicInstance.
    /// </summary>
    public List<int> UsedUpRelics = [];

    /// <summary>
    /// Populated potion rows after RewardPotion, in display order. RewardsSet generates
    /// every row before showing the screen, and the player can claim any row first.
    /// Entries are concrete potion IDs; this list never defers random generation.
    /// </summary>
    public List<int> PendingPotionRewards = [];
    public int[] PotionSlots = new int[Run.RunConstants.PotionSlotCapacity];

    /// <summary>
    /// How many of those slots the run may actually fill.
    /// </summary>
    /// <remarks>
    /// <c>Player.CreateForNewRun</c> passes a literal 3 and
    /// <c>Player.initialMaxPotionSlotCount</c> is 3, but every live capture at A8 reports
    /// <c>max_potion_slots: 2</c> -- the emulator models A8, so 2 is the base here and the
    /// decompiled constant is the un-ascended one. Phial Holster's
    /// <c>GainMaxPotionCount(1)</c> is what moves it, and a capture holding three potions
    /// is what proved it moves at all.
    /// </remarks>
    public int MaxPotionSlots = 2;

    /// <summary>
    /// The slots that actually exist right now. <see cref="PotionSlots" /> is a fixed
    /// <see cref="RunConstants.PotionSlotCapacity" /> wide so the observation keeps one
    /// shape; anything asking "is there room for a potion" has to stop at
    /// <see cref="MaxPotionSlots" /> or it will find room the run has not bought.
    /// </summary>
    public IEnumerable<int> UsablePotionSlots() =>
        PotionSlots.Take(Math.Min(MaxPotionSlots, PotionSlots.Length));

    /// <summary>
    /// Parent reward screens suspended by a relic's pickup effect.
    /// </summary>
    public List<RewardScreenSnapshot> SuspendedRewardScreens = [];

    /// <summary>
    /// `ForbiddenGrimoirePower.AfterCombatEnd`: card REMOVALS the finished fight owes the
    /// reward screen, one `CardRemovalReward` per point.
    /// </summary>
    /// <remarks>
    /// Carried on the RUN because the power lives on the COMBAT, and the combat is dropped
    /// before the rewards are built -- the same seam `ApplyImprovementUpgrades` sits on.
    /// </remarks>
    public int CardRemovalsOwed;

    /// <summary>`RoyaltiesPower.AfterCombatEnd`: gold the finished fight owes, as a GoldReward.</summary>
    public int RoyaltiesGoldOwed;

    /// <summary>
    /// Rest options already taken on THIS visit, as a bitmask of action ids. Only ever
    /// more than one bit with Miniature Tent, which keeps the screen open.
    /// </summary>
    public int RestOptionsTaken;

    /// <summary>
    /// Whether the last combat dealt the player any UNBLOCKED, blockable damage — Lava
    /// Lamp's condition. Unblockable damage is excluded, so a Burn does not spoil it.
    /// </summary>
    public bool TookUnblockedDamageThisCombat;

    /// <summary>
    /// Legacy single-enchantment summary. DisplayedCardReward retains every option's
    /// complete enchantment and amount; claims and queued rewards use that record.
    /// </summary>
    public int RewardEnchantIndex = -1;

    /// <inheritdoc cref="RewardEnchantIndex" />
    public Enchantment RewardEnchantment;
    public int CurrentNodeType;
    public int[] NeowOptions = new int[3];

    public int[] RewardCards = new int[3];

    /// <summary>
    /// `LastingCandy.TryModifyCardRewardOptions`: the extra POWER it adds to the screen,
    /// or 0 when there is none.
    /// </summary>
    /// <remarks>
    /// Its own field rather than a fourth slot on <see cref="RewardCards" />. That array is
    /// the ROLL -- a dozen paths fill it by length, from Kaleidoscope to a Trade event to
    /// Silver Crucible's upgrade sweep -- and widening it made every one of them offer a
    /// fourth card. The candy's option is a different thing: added by a relic after the
    /// roll, claimed by its own action, and absent from most screens. See E493.
    /// </remarks>
    public int LastingCandyRewardCard;

    /// <summary>Whether <see cref="LastingCandyRewardCard" /> is upgraded.</summary>
    public bool LastingCandyRewardUpgraded;

    /// <summary>
    /// `CardReward.CanReroll`, which Driftwood sets. One reroll per screen -- the game's
    /// `Reroll()` clears the flag before it repopulates.
    /// </summary>
    public bool CardRewardRerollAvailable;
    public CardRewardOptions? CardRewardOptions;
    public CardRewardOffer? DisplayedCardReward;
    public int CardRewardSubscriptionSequence;
    public int OpenedCardRewardIndex;

    public int RewardGold;
    public int RewardPotion;
    public bool RewardCardPending;

    /// <summary>
    /// Neow is still on screen with nothing left but "Proceed". The game returns to the
    /// ancient after its rewards are answered and waits for one more input before the
    /// map; going straight to the map skips a decision the player actually makes.
    /// </summary>
    public bool NeowAwaitingProceed;

    /// <summary>
    /// An EVENT opened the reward screen currently up, so answering it returns to the
    /// event's result page rather than to the map.
    /// </summary>
    /// <remarks>
    /// Every event that hands out rewards does it the same way: <c>await
    /// RewardsCmd.OfferCustom(...)</c> and then <c>SetEventFinished(...)</c> on the line
    /// below, so the result page is shown once the screen is answered. Neow already had
    /// this and events did not, which cost the run one Proceed every time.
    /// </remarks>
    public bool EventAwaitingProceed;
    public bool ReturnToRewardScreenAfterCardReward;
    public int[] MapNodeTypes = new int[RunConstants.MapChoices];
    public int[] MapChoices = new int[RunConstants.MapChoices];
    public int[] ShopCards = new int[7];
    public int[] ShopRelics = new int[3];
    public int[] ShopPotions = new int[3];

    /// <summary>What each shop slot costs now: <see cref="ShopBaseCosts" /> through the price hooks.</summary>
    public int[] ShopCosts = new int[14];

    /// <summary>
    /// Each shop slot's `MerchantEntry._cost` -- the rolled price before any
    /// `ModifyMerchantPrice` relic. <see cref="RunRewardGenerator.RefreshShopQuotes" />
    /// derives <see cref="ShopCosts" /> from it.
    /// </summary>
    public int[] ShopBaseCosts = new int[14];
    public bool[] RewardUpgraded = new bool[3];
    public int RelicReward;
    public int PendingTreasureRelic;
    public bool TreasureChestOpened;

    // Silver Crucible's reward-upgrade counter belongs to RelicInstance.Counter;
    // its independent TreasureRoomsEntered property only empties the first chest.
    public int SilverCrucibleTreasureRoomsEntered;
    public int EventId;

    // These events lock the belt in BeforeEventStarted and unlock it in
    // OnEventFinished. EventResultPending replaces their ID on completion; the
    // original ID remains active while a reward or selection screen is open.
    public bool CanUseOrRemovePotions =>
        EventId
            is not (
                RunConstants.EventTheFutureOfPotions
                or RunConstants.EventRanwidTheElder
                or RunConstants.EventStoneOfAllTime
            );
    public int? EventValue0;
    public int? EventValue1;

    // The decimal DynamicVar.BaseValue behind an event value, for events whose options
    // hand BaseValue itself to a command. Jungle Maze passes its fractional purses to
    // PlayerCmd.GainGold, which modifies them before truncating, so EventValue0/1 (the
    // IntValue the option shows) cannot stand in for them.
    public decimal? EventAmount0;
    public decimal? EventAmount1;

    // SlipperyBridge tracks individual rejected cards; model IDs alone would also
    // exclude other copies. The deck keeps its order until Overcome removes one.
    public HashSet<int> SlipperyBridgeSkippedRemovals = [];

    /// <summary>
    /// The Crystal Sphere's board, while one is open. Board placement, completion
    /// rewards and subsequent rerolls share the event stream. Retain its reference
    /// after the board closes; reseeding would repeat the placement draws.
    /// </summary>
    public CrystalSphereGame? CrystalSphere;
    public GameRng? CrystalSphereRng;

    /// <summary>
    /// The current event's own Rng stream, and the entry it was seeded from.
    ///
    /// <para>
    /// The game gives an event ONE <c>base.Rng</c> for its lifetime, so a second draw
    /// continues where the first left off. This was rebuilt per call instead, which put
    /// every draw at position 0 -- each value plausible on its own, and wrong the moment
    /// an event drew twice.
    /// </para>
    /// </summary>
    public GameRng? EventRngStream;

    public string? EventRngName;

    /// <summary>The Relic Trader's three-relic shelf, drawn once when the event opens.</summary>
    public List<int>? EventRelicStock;

    /// <summary>Fake Merchant's rolled Shops prices, retained even after a slot is sold.</summary>
    public List<int>? FakeMerchantPrices;

    /// <summary>Relic Trader replacements, all three pulled on entry and kept for selection.</summary>
    public List<int>? EventRelicOffers;

    /// <summary>
    /// Bing Bong is adding its own clone right now. The game's guard is a `CardsToSkip`
    /// set the relic checks and removes from; a re-entrancy flag says the same thing for a
    /// single-player deck and cannot leak an entry the way a set can.
    /// </summary>
    public bool AddingBingBongClone;

    /// <summary>
    /// Combats each Guilty in the deck has sat through. `Guilty.AfterCombatEnd` counts
    /// while the card is in the DECK and removes it at five -- a curse that serves a
    /// sentence and leaves, which is the only self-removing card in the game.
    /// </summary>
    /// <remarks>
    /// Per COPY, not per deck: the count is `[SavedProperty]` on the card. A list parallel
    /// to the copies in the deck says the same thing without putting a counter on every
    /// CardInstance for the one card that needs it.
    /// </remarks>
    public List<int> GuiltyCombatsServed = [];

    /// <summary>
    /// Gold rewards still owed. The reward screen carries one pile at a time, the way it
    /// carries one potion at a time, so anything that offers several -- the Crystal
    /// Sphere can uncover seven -- queues the rest here.
    ///
    /// The game shows them all at once and lets the player claim them in any order; the
    /// queue claims them one at a time instead. The gold that ends up in the purse is the
    /// same either way, which is why the difference is left to stand: only the action
    /// index moves.
    /// </summary>
    public List<int> PendingGoldRewards = [];

    /// <summary>
    /// Card offers still owed, each already rolled: card instances the player will choose
    /// one of. The Crystal Sphere can uncover a card reward of each rarity and the screen
    /// shows one offer at a time, so the rest wait here.
    ///
    /// Rolled on the way in rather than on the way out because that is when the game rolls
    /// them: RewardsSet populates every reward as the screen opens, in the order the
    /// rewards were listed, and those draws all come off the same stream.
    /// </summary>
    public List<CardRewardOffer> PendingCardOffers = [];
    public List<CardInstance> PendingSpecialCardRewards = [];

    /// <summary>
    /// Which character Orobas branded its Sea Glass with, as an index into
    /// <c>RunConstants.OtherCharacterPools</c>, or -1 before Orobas has been seen.
    /// </summary>
    /// <remarks>
    /// The draw happens whether or not the Sea Glass is ever offered or taken — it is the
    /// first thing Orobas spends — but the RESULT only matters if it is, which is why it
    /// is carried rather than recomputed.
    /// </remarks>
    public int SeaGlassCharacter = -1;

    /// <summary>
    /// The Ancient card an offered Dusty Tome was set up with, or -1. Darv calls
    /// `DustyTome.SetupForPlayer` while building its offer, so the `PlayerRng.Rewards`
    /// draw happens then, not when (or whether) the Tome is taken.
    /// </summary>
    public int DustyTomeAncientCard = -1;
    public CombatState? ActiveCombat;
    public CountingRandom? ActiveCombatRng;
    public bool LastPlayerWon;
    public int CompletedCombatRoomsBeforeCurrent;
    public Dictionary<(int Col, int Row), RunMapNode> MapNodes = [];
    public (int Col, int Row) CurrentMapCoord;
    public (int Col, int Row)?[] MapOptionCoords = new (int Col, int Row)?[RunConstants.MapChoices];

    /// <summary>
    /// Every act of the run, in order, as <c>RunState.Acts</c> holds them.
    /// </summary>
    /// <remarks>
    /// The game rolls every act's rooms at run start, off one UpFront stream, in index
    /// order — act 2's encounters were decided before the player left Neow. This is that
    /// list, and it is the ONLY copy: the four per-act fields below are views on
    /// <c>Acts[CurrentActIndex]</c> rather than a second copy that has to be kept in step.
    /// An earlier version split act 1 into loose fields and put "the acts after it" in a
    /// separate list, which quietly assumed there are exactly three acts and that the
    /// first is special. Neither is safe: a fourth act is a new row in
    /// <c>RunConstants.ActCandidatesByIndex</c>, and an alternate act 2 is a new entry in
    /// an existing row. Both should cost nothing here.
    /// </remarks>
    public List<ActRooms> Acts = [];

    /// <summary>Which of <see cref="Acts"/> the run is in — the game's own field name.</summary>
    public int CurrentActIndex;

    /// <summary>The run is looking at a new act's map without having stepped onto it yet.</summary>
    /// <remarks>
    /// <c>RunManager.EnterAct</c> forks here: act 1 with Neow calls
    /// <c>EnterMapCoord(StartingMapPoint.coord)</c> and the run BEGINS standing on its
    /// ancient, while every other act opens a <c>MapRoom</c> with nothing entered — so the
    /// starting point, which is an Ancient in every act, is the only thing to travel to.
    /// Without this the emulator arrived already standing on it and offered row one.
    /// </remarks>
    public bool AwaitingActStartNode;

    private ActRooms CurrentAct
    {
        get => (uint)CurrentActIndex < (uint)Acts.Count ? Acts[CurrentActIndex] : ActRooms.None;
        set
        {
            if ((uint)CurrentActIndex < (uint)Acts.Count)
            {
                Acts[CurrentActIndex] = value;
                return;
            }

            Acts.Clear();
            Acts.Add(value);
            CurrentActIndex = 0;
        }
    }

    /// <summary>The REGION the run is currently in — Overgrowth, Underdocks, Hive…</summary>
    /// <remarks>
    /// Settable so a test can put a run in an act without generating one, which several do
    /// to check act-gated events. It rewrites the CURRENT act's region rather than moving
    /// the run between acts — advancing is <c>CurrentActIndex</c>'s job.
    /// </remarks>
    public int Act
    {
        get => CurrentAct.Act;
        set => CurrentAct = CurrentAct with { Act = value };
    }

    public int[] NormalEncounterSequence
    {
        get => CurrentAct.NormalEncounters;
        set => CurrentAct = CurrentAct with { NormalEncounters = value };
    }

    public int[] EliteEncounterSequence
    {
        get => CurrentAct.EliteEncounters;
        set => CurrentAct = CurrentAct with { EliteEncounters = value };
    }

    public int BossEncounterId
    {
        get => CurrentAct.BossEncounterId;
        set => CurrentAct = CurrentAct with { BossEncounterId = value };
    }

    /// <summary>The ancient this act opens on — Neow in act 1, one of Hive's three after.</summary>
    public string Ancient
    {
        get => CurrentAct.Ancient;
        set => CurrentAct = CurrentAct with { Ancient = value };
    }
    public int NormalEncountersVisited;
    public int EliteEncountersVisited;
    public int[] EventSequence
    {
        get => CurrentAct.Events;
        set => CurrentAct = CurrentAct with { Events = value };
    }
    public int EventSequenceIndex;

    /// <summary>
    /// <c>RunState.VisitedEventIds</c>: an event the run has already seen is skipped
    /// rather than offered twice, however the sequence wraps.
    /// </summary>
    public List<int> VisitedEventIds = [];

    /// <summary>
    /// The event entry a pending deck selection belongs to, or null when it does not
    /// come from an event. Every event passes its OWN Rng to CardCmd.TransformToRandom;
    /// only NewLeaf uses Rng.Niche.
    /// </summary>
    public string? PendingSelectionEventEntry;

    /// <summary>
    /// The run's relic queues, shuffled once at run start. Every relic reward pulls from
    /// the player's; the shared one exists so that a relic pulled by one player is gone
    /// for the others, and is kept because populating it consumes 112 UpFront draws that
    /// sit between the seed and everything else in the run.
    /// </summary>
    public RelicGrabBag RelicBag = new();
    public RelicGrabBag SharedRelicBag = new(refreshAllowed: true);

    public double CardRarityOffset;
    public float PotionRewardOdds = 0.4f;
    public bool PendingRelicReward;

    /// <summary>
    /// Lava Rock's `HasTriggered`: its two boss-room relics are once per RUN, and it
    /// disables itself afterwards.
    /// </summary>
    public bool LavaRockTriggered;

    /// <summary>
    /// Relic rewards still owed, each already rolled. The screen carries one at a time,
    /// the way it carries one potion at a time, so anything past the first waits here.
    /// </summary>
    public List<int> PendingBonusRelicRewards = [];

    /// <summary>
    /// The relic ids Toy Box produced, in the order it offered them -- the game's
    /// <c>RelicModel.IsWax</c>. Every third combat melts the first of these the run is
    /// still holding, so the order is what decides which one goes.
    /// </summary>
    public List<int> WaxRelics = [];

    /// <summary>
    /// The map coordinates Fur Coat marked, and the act it marked them in. Entering one of
    /// these rooms puts every enemy in the fight on ONE hp.
    /// </summary>
    /// <remarks>
    /// The act is part of the state because `ModifyGeneratedMapLate` re-marks on every map
    /// it is handed and `AddMarkedRooms` returns early when the act has moved on -- so a
    /// coat picked up in act one marks act one and nothing after it.
    /// </remarks>
    public List<(int Col, int Row)> FurCoatRooms = [];
    public int FurCoatAct = -1;

    /// <summary>
    /// The act Golden Compass replaced the map for. `ModifyGeneratedMap` only answers for
    /// that one act, and `ModifyUnknownMapPointRoomTypes` narrows the "?" to Event for the
    /// same act and no other.
    /// </summary>
    public int GoldenPathAct = -1;

    /// <summary>
    /// The current map's point carrying Spoils Map's quest marker (<c>SpoilsMap.SpoilsCoord</c>
    /// plus <c>MapPoint.AddQuest</c>), or null. Set only when the map for the quest's act is
    /// generated with a copy in the deck, and cleared by every later map, since
    /// <c>TryHandleSpoilsMap</c> reads the marker off the map the run is standing on.
    /// </summary>
    public (int Col, int Row)? SpoilsMapQuestCoord;

    /// <summary>
    /// Lord's Parasol still owes the rest of its sweep. The game awaits each purchase in
    /// turn, and a bought relic whose own <c>AfterObtained</c> opens a card-select screen
    /// suspends the sweep until it is answered -- so the sweep is resumable rather than
    /// one pass.
    /// </summary>
    public bool ParasolSweepPending;

    /// <summary>
    /// A card the next combat's reward screen owes outright, rather than as one of three
    /// rolled offers -- the game's `SpecialCardReward`. The Lantern Key's knight fight is
    /// the only one so far: beat it and the key is yours, and it is not a choice. Reward
    /// generation turns it into a <see cref="PendingSpecialCardRewards" /> row.
    /// </summary>
    public int PendingSpecialCardReward;

    /// <summary>
    /// Unpopulated rewards an event hands `EnterCombatWithoutExitingEvent` -- Punch Off's
    /// `[RelicReward, PotionReward]` -- in list order. `EventCombatSynchronizer` adds
    /// them to the new `CombatRoom.ExtraRewards` before the fight starts, so they
    /// populate after the room's own rewards and ahead of anything combat adds later.
    /// </summary>
    public List<EventCombatExtraReward> PendingEventCombatRewards = [];

    /// <summary>
    /// The encounter a combat-layout event (Punch Off, The Lantern Key) built on entry,
    /// and the max HPs its creatures rolled on `Rng.Niche` then. Zero / empty otherwise.
    /// The event's own fight reuses those creatures rather than rolling again.
    /// </summary>
    public int EventLayoutEncounterId;

    public List<int> EventLayoutEnemyHps = [];

    /// <summary>
    /// The event to return to when the current combat ends, or 0. The game's
    /// `EnterCombatWithoutExitingEvent(..., shouldResumeAfterCombat: true)`: the room is
    /// still the event's, and the fight's outcome is an input to what the event pays.
    /// </summary>
    /// <remarks>
    /// Only the Battleworn Dummy uses it so far, and its `Resume` reads the ENCOUNTER --
    /// which setting was fought, and whether the dummy ran out of time. The setting is
    /// `ResumeEventPage`, and the escape is `CombatState.BattlewornDummyRanOutOfTime`,
    /// carried across by <see cref="ResumeEventDummyEscaped"/> because the combat state is
    /// gone by the time the event resumes.
    /// </remarks>
    public int ResumeEventId;

    public int ResumeEventPage;

    public bool ResumeEventDummyEscaped;

    /// <summary>
    /// Neow's Bones adds its curse only once its two relics have been claimed:
    /// <c>AfterObtained</c> awaits the RewardsSet's <c>Offer()</c> and adds the curse on
    /// the line after. Rolling it up front would be the same two streams in the same order
    /// -- the relics come off Rewards and the curse off Niche -- but only until a claimed
    /// relic's own pickup draws from Niche, which several of the candidates do.
    /// </summary>
    public bool PendingNeowsBonesCurse;

    /// <summary>
    /// Hefty Tablet's Injury, which arrives WITH the card its screen offers rather than
    /// before it: <c>AfterObtained</c> awaits the choice, then adds a list holding the
    /// Injury with the chosen card inserted at its front. A live capture shows both land
    /// in the same snapshot.
    /// </summary>
    public bool PendingHeftyTabletCurse;
    public int ShopRemovalsUsed;

    /// <summary>Whether this shop's removal service has already been bought.</summary>
    /// <remarks>
    /// Separate from <see cref="ShopRemovalsUsed"/>, which is the RUN's total and only
    /// sets the price (<c>BaseCost + PriceIncrease * CardShopRemovalsUsed</c>). The
    /// merchant stocks the service once per visit — <c>MerchantCardRemovalEntry</c> has
    /// <c>IsStocked => !Used</c> — so a second removal in the same shop is not for sale
    /// at any price.
    /// </remarks>
    public bool ShopRemovalUsedThisVisit;

    /// <summary>Merchant fee reserved while the player chooses; charged on confirmation.</summary>
    public int? PendingShopRemovalCost;
    public int? TransformSelectedDeckIndex;

    /// <summary>
    /// A deck selection an event has opened and is waiting on: what is being done to the
    /// chosen cards, the enchantment or target card it is being done with, and how many
    /// are still to pick. Half the Act 1 events end in one of these -- enchant this,
    /// transform that -- and each used to need its own flag; Self-Help Book's was the
    /// only one, so every other event silently resolved without ever asking.
    /// </summary>
    public DeckSelection PendingSelectionKind;
    public int PendingSelectionArg;
    public int PendingSelectionCount;

    /// <summary>
    /// Cards an event has ROLLED and is offering, and how many of them the player still
    /// gets to keep. Distinct from the selection above, which picks out of the deck the
    /// player already has: Brain Leech rolls five cards and hands over one, Room Full of
    /// Cheese rolls eight and hands over two, so the grid is the source rather than the
    /// deck. A picked card leaves the offer, the way it leaves the game's grid.
    /// </summary>
    public int[] PendingOfferCards = [];

    // Full generated cards; PendingOfferCards is the ID projection used by the native UI.
    public CardInstance[] PendingOfferInstances = [];

    /// <summary>
    /// The card a pick of grid slot <paramref name="index" /> adds: the generated
    /// instance, or the bare id as a base card when no instances ride with the ids. One
    /// resolver for the pick and the native upgraded-flag readout, so they cannot differ.
    /// </summary>
    internal CardInstance PendingOfferCardAt(int index) =>
        PendingOfferInstances.Length == PendingOfferCards.Length
            ? PendingOfferInstances[index]
            : new CardInstance(PendingOfferCards[index], Upgraded: false);

    /// <summary>
    /// Scroll Boxes' two bundles, three card ids each, flat: bundle 0 then bundle 1.
    /// Empty when no bundle screen is up.
    /// </summary>
    public int[] BundleOffer = [];

    /// <summary>The bundle the player has highlighted, or -1 for none yet.</summary>
    public int SelectedBundle = -1;

    /// <summary>
    /// The bundle screen was opened by claiming a relic row, so confirming it resumes that
    /// rewards screen rather than Neow or the map.
    /// </summary>
    public bool BundleReturnsToRewards;
    public int PendingOfferPicks;

    /// <summary>
    /// Whether the offer grid may be closed with fewer picks taken, which is
    /// `CardSelectorPrefs(prompt, MIN, max)` with a min of ZERO and a manual confirm.
    /// Sea Glass is the one that has it: fifteen cards, take any number including none.
    /// Every other grid names one number and closes itself when the picks run out.
    /// </summary>
    public bool PendingOfferOptional;

    /// <summary>
    /// Which page of a multi-page event is showing; 0 is the one it opens on. Most
    /// events answer a choice by finishing, but a few answer it with a fresh page of
    /// their own options -- Punch Off's "I Can Take Them" leads to a page whose only
    /// option is the fight itself.
    /// </summary>
    public int EventPage;

    /// <summary>
    /// Which two of an event's candidates the current page is showing, in the order it
    /// shows them -- so an action index means the same thing to the engine as it does on
    /// screen.
    /// </summary>
    /// <remarks>
    /// Tinker Time is the case that needs it: both of its later pages are
    /// <c>TakeRandom(2, Rng)</c> over three candidates, so which option index 0 IS
    /// depends on a shuffle. Storing it beats re-deriving, which would advance the
    /// event's stream a second time and offer a different pair than the one shown.
    /// </remarks>
    public int[] EventRandomOffer = [];

    /// <summary>The card type Tinker Time's second page settled on.</summary>
    public CardType TinkerCardType;

    /// <summary>
    /// A card the event adds once the selection above finishes, and how many copies.
    /// Three events pay for a removal with a curse -- <c>RemoveFromDeck(cards)</c> and
    /// then <c>AddCurseToDeck&lt;T&gt;</c> -- so the curse has to survive the screen.
    /// </summary>
    public int PendingSelectionFollowUpCard;
    public int PendingSelectionFollowUpCount;

    /// <summary>
    /// HP the event takes once the selection finishes. Whispering Hollow's Hug transforms
    /// the chosen card FIRST and only then charges for it, so a capture taken while the
    /// selector is open shows the player still at full health.
    /// </summary>
    public int PendingSelectionFollowUpHpLoss;

    /// <summary>
    /// Gold the event takes once the selection finishes. Zen Weaver's
    /// `RemoveCardsAndProceed` removes the chosen cards FIRST and only then calls
    /// `PlayerCmd.LoseGold`, so the removal screen still shows the gold it will cost.
    /// </summary>
    public int PendingSelectionFollowUpGoldLoss;

    /// <summary>
    /// Max HP the finished selection pays out. Meat Cleaver's cook is the only source:
    /// remove two, gain nine, and `GainMaxHp` heals by what it adds.
    /// </summary>
    public int PendingSelectionFollowUpMaxHpGain;

    /// <summary>
    /// The event that opened this selection is not finished by it. The Endless Conveyor's
    /// Jelly Liver transforms a card and then the belt turns and offers the next dish, so
    /// resolving the selection has to return to the event rather than end it.
    /// </summary>
    /// <summary>The amount an Enchant selection lands at, or 0 for the enchantment's default.</summary>
    public int PendingSelectionEnchantAmount;

    public bool PendingSelectionReturnsToEvent;

    /// <summary>Where this selection leaves the run when it is answered.</summary>
    public SelectionReturn PendingSelectionReturn;
    public bool PendingRestUpgrade;
    public bool RestResultPending;
    public int UnknownMapPointsVisited;
    public double UnknownMapPointMonsterOdds = 0.1;
    public double UnknownMapPointEliteOdds = -1.0;
    public double UnknownMapPointTreasureOdds = 0.02;
    public double UnknownMapPointShopOdds = 0.03;
    public int LastResolvedRoomType = RunConstants.NodeNone;
}

public sealed class RunMapNode
{
    public int Col { get; set; }
    public int Row { get; set; }
    public int NodeType { get; set; }
    public List<(int Col, int Row)> Children { get; } = [];
    public List<(int Col, int Row)> Parents { get; } = [];
    public int EncounterId { get; set; }

    /// <summary>
    /// The game's <c>MapPoint.CanBeModified</c>. False for the rows whose type is
    /// forced during map generation (row 1, the treasure row, the final rest row);
    /// those points are excluded from post-prune type repair.
    /// </summary>
    public bool CanBeModified { get; set; } = true;

    public RunMapNode(int col, int row)
    {
        Col = col;
        Row = row;
    }
}
