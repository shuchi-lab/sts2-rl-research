using Sts2Emulator.Core.Effects;

namespace Sts2Emulator.Core.Run;

public static class RunConstants
{
    /// <summary>A valid action hit an original-game reward generation failure.</summary>
    public const int RewardGenerationFailed = -2;

    // Mirrors the combat observation rather than restating its size: the two drifted
    // apart the moment the combat block grew, and the run observation silently
    // reserved the old width for it.
    public const int CombatObsSize = CombatObservation.ObsSize;

    /// <summary>Where the map's node types start in the scalar block; one slot per choice.</summary>
    public const int MapNodeTypeObsOffset = 12;

    // A block used to sit here carrying the ENCOUNTER behind each map choice, and the
    // game does not put that on its map: you learn which monsters are in a room by
    // walking into it. A policy reading the observation was told the next fight before it
    // picked a node, which is the whole of the decision a monster row asks. The node
    // TYPES above it stay -- those are on the game's map, drawn as icons.
    //
    // `State.MapChoices` is unchanged and still resolves the encounter when a node is
    // actually entered; it is the OBSERVATION that has no business carrying it.
    public const int RelicRewardObsOffset = MapNodeTypeObsOffset + MapChoices;
    public const int CurrentEventObsOffset = RelicRewardObsOffset + 1;
    public const int PotionObsOffset = CurrentEventObsOffset + 1;
    public const int PotionObsSlots = 3;

    /// <summary>
    /// The run's flat scalars: phase, floor, gold, the screen in front of the player.
    /// Derived from the blocks inside it rather than written down, because widening the
    /// map's choice arrays moves everything after them.
    /// </summary>
    public const int RunScalarObsSize = PotionObsOffset + PotionObsSlots;

    /// <summary>
    /// How many of the deck's cards the observation carries. A run that grew past this
    /// would have its later cards unseen -- which the count at offset 3 still reports, so
    /// the truncation is visible rather than silent -- but 64 is well past what a full
    /// four-act run reaches, let alone Act 1.
    /// </summary>
    public const int MaxObservedDeck = 64;

    /// <summary>Per card: def id, upgraded, enchantment, and the amount it was applied at.</summary>
    public const int DeckSlotSize = 4;

    public const int MaxObservedRelics = 32;

    /// <summary>Per relic: def id, its counter, and whether the run has spent it.</summary>
    public const int RelicSlotSize = 3;

    /// <summary>
    /// Where the deck begins. Slot <c>i</c> is <c>State.Deck[i]</c>, and deliberately so:
    /// a card-select screen's action <c>i</c> indexes the same list, so sorting the
    /// observation into a canonical multiset would break the agent's only way to say
    /// which card it means. The order carries no information the player does not have --
    /// a deck is inspectable in full -- so nothing leaks by keeping it.
    /// </summary>
    public const int DeckObsOffset = RunScalarObsSize;

    public const int RelicObsOffset = DeckObsOffset + MaxObservedDeck * DeckSlotSize;

    /// <summary>
    /// One slot per thing a merchant sells, indexed by the action that buys it: seven
    /// cards, three relics, three potions, and the card-removal service at 13. Slot
    /// <c>i</c> is shop action <c>i</c>, the way a deck slot is a card-select action.
    /// </summary>
    public const int ShopSlots = 14;

    /// <summary>
    /// Per slot: what is on it, and what it costs. The removal slot has no item, so its id
    /// is 0 and only its price means anything. A sale is already in the price -- the game
    /// halves the slot's cost rather than flagging it -- so there is nothing else to carry.
    /// </summary>
    public const int ShopSlotSize = 2;

    public const int ShopObsOffset = RelicObsOffset + MaxObservedRelics * RelicSlotSize;

    /// <summary>
    /// Lasting Candy's FOURTH card-reward option: its id, then whether it is upgraded.
    /// </summary>
    /// <remarks>
    /// APPENDED, at the very end of the run's observation, rather than sat beside the
    /// other three at `RunScalarObsSize + 9`. Putting it there would shift the deck, the
    /// relics and the shop along by one and renumber the whole observation for every
    /// trained policy -- the same rule the `Enchantment` enum learned the hard way. On the
    /// end it costs two slots and moves nothing.
    /// </remarks>
    public const int FourthRewardObsOffset = ShopObsOffset + ShopSlots * ShopSlotSize;

    public const int RunExtraObsSize = FourthRewardObsOffset + 2;
    public const int RunObsSize = CombatObsSize + RunExtraObsSize;
    public const int RunInfoSize = 11;

    /// <summary>
    /// How wide the run's action mask is. 32 was enough while the widest screen was a
    /// shop, and silently wrong past that: <c>SetMask</c> drops anything it cannot fit,
    /// so a deck grown past 32 cards had its later cards unselectable at a card-select
    /// screen, without a word. The Crystal Sphere is what forced the issue -- its board
    /// is 121 cells and each may be divined with either tool -- so the mask now covers
    /// 242 and rounds up.
    /// </summary>
    public const int MaxActions = 256;

    /// <summary>The Crystal Sphere's board is 11x11.</summary>
    public const int CrystalSphereSize = 11;
    public const int CrystalSphereCells = CrystalSphereSize * CrystalSphereSize;

    /// <summary>
    /// Actions 0..120 divine a cell with the big tool, 121..241 with the small one; a
    /// cell's index is <c>x * 11 + y</c>. The tool is folded into the action rather than
    /// set by one of its own, because setting a tool costs the game nothing -- and a free
    /// action is a cycle an agent can ride forever.
    /// </summary>
    public const int CrystalSphereSmallToolAction = CrystalSphereCells;
    public const int MapWidth = 7;

    /// <summary>
    /// How many map nodes the player may be offered at once. Ordinarily that is the
    /// current node's children -- never more than four -- but <c>MapTravel</c> offers the
    /// WHOLE of the next row while <c>Hook.ShouldAllowFreeTravel</c> holds, and a row is
    /// as wide as the map. It was 4, so a Winged Boots run could not even be handed its
    /// options, let alone choose among them.
    /// </summary>
    public const int MapChoices = MapWidth;
    public const int MapBossRow = 16;
    public const int MapPathIterations = 7;

    /// <summary>
    /// The game's <c>MapPointTypeCounts.NumOfElites</c>:
    /// <c>round(5 * (SwarmingElites ? 1.6 : 1))</c>. The emulator models high
    /// ascension, so SwarmingElites is on and this is 8.
    /// </summary>
    public const int MapEliteCount = 8;

    public static int MapEliteCountFor(int ascension) =>
        Ascension.Value(ascension, Ascension.SwarmingElites, MapEliteCount, 5);

    /// <summary>The game's <c>MapPointTypeCounts.NumOfShops</c>.</summary>
    public const int MapShopCount = 3;
    public const int MapTreasureRow = MapBossRow - 7;
    public const int MapFinalRestRow = MapBossRow - 1;
    public const int MapStartCol = MapWidth / 2;
    public const int RewardSkipAction = 3;
    public const int TreasureOpenAction = 4;

    // Preserve actions 4-6 for the first three potion slots. Extra slots occupy the
    // end of the action space, leaving reward rows 3+ at their existing actions 7+.
    public const int RewardExtraPotionDiscardStart = MaxActions - (PotionSlotCapacity - 3);

    public static int RewardPotionDiscardAction(int slot) =>
        (uint)slot >= PotionSlotCapacity ? -1
        : slot < 3 ? 4 + slot
        : RewardExtraPotionDiscardStart + slot - 3;

    public static int RewardPotionDiscardSlot(int action) =>
        action is >= 4 and <= 6 ? action - 4
        : action >= RewardExtraPotionDiscardStart && action < MaxActions
            ? action - RewardExtraPotionDiscardStart + 3
        : -1;

    /// <summary>Overview rows exclude skip and both potion-discard ranges.</summary>
    public static int RewardItemAction(int index) =>
        (uint)index >= RewardExtraPotionDiscardStart - 4 ? -1
        : index < 3 ? index
        : index + 4;

    public static int RewardItemIndex(int action) =>
        action < 3 ? action
        : action >= 7 && action < RewardExtraPotionDiscardStart ? action - 4
        : -1;

    /// <summary>
    /// Commits the highlighted bundle on the Scroll Boxes screen. The game answers that
    /// screen in two steps — `select_bundle` then `confirm_bundle_selection` — and a
    /// capture spends an action on each, so the emulator does too.
    /// </summary>
    public const int BundleConfirmAction = 2;
    public const int RestHealAction = 0;
    public const int RestUpgradeAction = 1;

    /// <summary>
    /// The rest option Pael's Growth adds: copy every Clone-enchanted card in the deck.
    /// </summary>
    /// <remarks>
    /// Offered whenever the relic is HELD — <c>TryModifyRestSiteOptions</c> adds it
    /// unconditionally and <c>IsEnabled</c> is the base's true — not when the deck happens
    /// to hold a Clone card. With none it simply copies nothing.
    /// </remarks>
    public const int RestCloneAction = 2;

    /// <summary>
    /// Girya's <c>LiftRestSiteOption</c>: one of three lifts, each worth a point of
    /// Strength at the start of every later combat. Offered only while lifts remain —
    /// <c>TryModifyRestSiteOptions</c> returns false at three.
    /// </summary>
    public const int RestLiftAction = 4;

    /// <summary>
    /// Shovel's <c>DigRestSiteOption</c>: pull the next relic from the FRONT of the
    /// player's grab bag, which is the same queue and the same end an elite reward uses.
    /// </summary>
    public const int RestDigAction = 5;

    /// <summary>
    /// `ByrdonisEgg.TryModifyRestSiteOptions` adds `HatchRestSiteOption`, which grants the
    /// Byrdpip relic. Offered whenever the DECK holds an egg -- the option comes from the
    /// CARD, not from a relic, which makes it the only rest option in the game a card can
    /// put there.
    /// </summary>
    /// <remarks>
    /// Six, not three: three is `RewardSkipAction`, which the rest site borrows for its
    /// own leave. The gap at three is why the rest options run 0, 1, 2, 4, 5.
    /// </remarks>
    public const int RestHatchAction = 6;

    /// <summary>
    /// `MeatCleaver.TryModifyRestSiteOptions` adds `CookRestSiteOption`: remove TWO cards
    /// and gain 9 max HP. Its `IsEnabled` wants two REMOVABLE cards in the deck, so a deck
    /// of Eternals leaves the option on the screen greyed out rather than absent.
    /// </summary>
    /// <summary>
    /// `PaelsWing.TryModifyCardRewardAlternatives` adds a "SACRIFICE" alternative to the
    /// card reward: take no card, and every SECOND sacrifice pulls a relic from the front
    /// of the grab bag. `PostAlternateCardRewardAction.EndSelectionAndCompleteReward`, so
    /// it finishes the screen exactly as claiming a card does.
    /// </summary>
    /// <remarks>
    /// Four, because the card reward's own actions are the three cards and the skip at
    /// three -- the first id the screen does not already use.
    /// </remarks>
    public const int RewardSacrificeAction = 4;

    /// <summary>The game's `CardReward.OptionCount`: three cards roll onto the screen.</summary>
    /// <remarks>
    /// The array behind them is FOUR wide, because Lasting Candy adds an option rather than
    /// replacing one. This constant is the roll count and the action count for the ordinary
    /// three; the fourth is claimed by `RewardFourthCardAction` and is not part of the loop
    /// that fills the screen.
    /// </remarks>
    public const int CardRewardOptionCount = 3;

    /// <summary>
    /// `LastingCandy.TryModifyCardRewardOptions` adds a fourth option -- a POWER -- to the
    /// card reward of every second combat.
    /// </summary>
    /// <remarks>
    /// FIVE, not three. The screen's own actions are the three cards, the skip at three and
    /// Pael's Wing's sacrifice at four, so three is taken and taking it would turn every
    /// existing skip into a claim. Appending keeps every action id the run already had.
    /// This is why the relic was declared unmodelled for so long: the assumption was that a
    /// fourth card meant renumbering the screen, and it does not. See E493.
    /// </remarks>
    public const int RewardFourthCardAction = 5;

    /// <summary>
    /// `Driftwood.TryModifyRewardsLate` sets `CanReroll` on every card reward: the three
    /// options are thrown away and three more rolled, ONCE per screen.
    /// </summary>
    public const int RewardRerollAction = 6;

    public const int RestCookAction = 7;

    /// <summary>
    /// `PumpkinCandle.TryModifyRestSiteOptions` adds `KindleRestSiteOption`, whose whole
    /// body is `Rekindle()` -- KindleCount += 5. Offered whenever the relic is held, with
    /// no cap, so a run can keep the candle burning indefinitely by never sleeping.
    /// </summary>
    public const int RestKindleAction = 8;
    public const int ShopRemoveAction = 13;
    public const int ShopSkipAction = 14;

    // `DiscardPotionGameAction` also runs outside combat. In a shop every belt slot
    // gets the final PotionSlotCapacity actions (250-255), far above the purchases at
    // 0-14; slots 3-5 land on the same actions as the reward screen's extra discards.
    public const int ShopPotionDiscardStart = MaxActions - PotionSlotCapacity;

    public static int ShopPotionDiscardAction(int slot) =>
        (uint)slot >= PotionSlotCapacity ? -1 : ShopPotionDiscardStart + slot;

    public static int ShopPotionDiscardSlot(int action) =>
        action >= ShopPotionDiscardStart && action < MaxActions
            ? action - ShopPotionDiscardStart
            : -1;

    // `UsePotionAction` out of combat in a merchant room: slot-addressed, 244-249, the
    // block just below the discards. Only Foul Potion passes the out-of-combat usability
    // check in a shop (`FoulPotion.PassesCustomUsabilityCheck`); the mask enforces it.
    public const int ShopPotionUseStart = ShopPotionDiscardStart - PotionSlotCapacity;

    public static int ShopPotionUseAction(int slot) =>
        (uint)slot >= PotionSlotCapacity ? -1 : ShopPotionUseStart + slot;

    public static int ShopPotionUseSlot(int action) =>
        action >= ShopPotionUseStart && action < ShopPotionDiscardStart
            ? action - ShopPotionUseStart
            : -1;

    public const int EventSkipAction = 3;

    public const int NodeNone = 0;
    public const int NodeNormal = 1;
    public const int NodeElite = 2;
    public const int NodeRest = 3;
    public const int NodeShop = 4;
    public const int NodeRelic = 5;
    public const int NodeBoss = 6;
    public const int NodeEvent = 7;

    /// <summary>
    /// <c>MapPointType.Ancient</c>. <c>StandardActMap</c> stamps it onto the STARTING map
    /// point of every act, after every other type has been assigned. Act 1's is Neow and
    /// the run begins standing on it, which is why the emulator got away without the type
    /// for so long; act 2 opens on the map with its ancient as the only thing to travel
    /// to, and the run has to walk there like any other room.
    /// </summary>
    public const int NodeAncient = 8;

    /// <summary>
    /// The emulator's own act ids. These are REGIONS, not ordinals: a run's first act is
    /// Overgrowth or Underdocks depending on the seed, and Hive and Glory always follow
    /// in that order.
    /// </summary>
    public const int ActOvergrowth = 1;
    public const int ActUnderdocks = 2;

    public const int EventResultPending = -1;
    public const int EventUnrestSite = 1;
    public const int EventAromaOfChaos = 2;
    public const int EventSimpleReward = 3;
    public const int EventJungleMazeAdventure = 4;
    public const int EventMorphicGrove = 5;
    public const int EventBrainLeech = 6;
    public const int EventTheLegendsWereTrue = 7;
    public const int EventDoorsOfLightAndDark = 8;
    public const int EventSunkenTreasury = 9;
    public const int EventByrdonisNest = 10;
    public const int EventSelfHelpBook = 11;
    public const int EventDenseVegetation = 12;
    public const int EventLuminousChoir = 13;
    public const int EventSapphireSeed = 14;
    public const int EventSunkenStatue = 15;
    public const int EventTabletOfTruth = 16;
    public const int EventWellspring = 17;
    public const int EventWhisperingHollow = 18;
    public const int EventWoodCarvings = 19;
    public const int EventAbyssalBaths = 20;
    public const int EventDrowningBeacon = 21;
    public const int EventEndlessConveyor = 22;
    public const int EventPunchOff = 23;
    public const int EventSpiralingWhirlpool = 24;
    public const int EventTrashHeap = 25;
    public const int EventWaterloggedScriptorium = 26;
    public const int EventCrystalSphere = 27;
    public const int EventDollRoom = 28;
    public const int EventFakeMerchant = 29;
    public const int EventPotionCourier = 30;
    public const int EventRanwidTheElder = 31;
    public const int EventRelicTrader = 32;
    public const int EventRoomFullOfCheese = 33;
    public const int EventSlipperyBridge = 34;
    public const int EventStoneOfAllTime = 35;
    public const int EventSymbiote = 36;
    public const int EventTeaMaster = 37;
    public const int EventTheFutureOfPotions = 38;
    public const int EventThisOrThat = 39;
    public const int EventWarHistorianRepy = 40;
    public const int EventWelcomeToWongos = 41;
    public const int EventAmalgamator = 42;
    public const int EventBugslayer = 43;
    public const int EventColorfulPhilosophers = 44;
    public const int EventColossalFlower = 45;

    /// <summary>
    /// Colossal Flower's `_prizeCosts` -- the gold on each of its three rungs -- and its
    /// `_prizeDamage`, the unblockable cost of leaving each one. Both are indexed by the
    /// event's `NumberOfDigs`, which the emulator carries in `EventPage`.
    /// </summary>
    public static readonly int[] ColossalFlowerPrizes = [35, 75, 135];

    public static readonly int[] ColossalFlowerDigDamage = [5, 6, 7];

    /// <summary>Colorful Philosophers' `CardsVar(3)`: cards per rarity screen.</summary>
    public const int ColorfulPhilosophersCards = 3;

    /// <summary>`FakeMerchant.relicCost`: base MerchantCost before the Shops RNG jitter.</summary>
    public const int FakeMerchantRelicCost = 50;

    /// <summary>
    /// `_inventoryRelics.UnstableShuffle(Rng).Take(6)`: six of the nine fakes, and which
    /// six is the event's only roll.
    /// </summary>
    public const int FakeMerchantInventorySize = 6;

    /// <summary>Throwing a Foul Potion at the merchant, which starts the fight.</summary>
    public const int FakeMerchantThrowAction = 6;

    /// <summary>Walking out.</summary>
    public const int FakeMerchantLeaveAction = 7;

    /// <summary>
    /// `FakeMerchantEventEncounter`, which `CombatFactory` has always been able to build
    /// -- the event that reaches it is what was missing.
    /// </summary>
    public const int FakeMerchantEncounterId = 57;
    public const int EventFieldOfManSizedHoles = 46;
    public const int EventInfestedAutomaton = 47;
    public const int EventLostWisp = 48;
    public const int EventSpiritGrafter = 49;
    public const int EventTheLanternKey = 50;
    public const int EventZenWeaver = 51;
    public const int EventBattlewornDummy = 52;
    public const int EventGraveOfTheForgotten = 53;
    public const int EventHungryForMushrooms = 54;
    public const int EventReflections = 55;
    public const int EventRoundTeaParty = 56;
    public const int EventTrial = 57;
    public const int EventTinkerTime = 58;
    public const int EventTheArchitect = 59;

    public const int SlimesWeakEncounterId = 3;
    public const int SlimesNormalEncounterId = 16;
    public const int FlyconidNormalEncounterId = 17;
    public const int TwoTailedRatsEncounterId = 6;
    public const int RubyRaidersEncounterId = 28;
    public const int SlitheringStranglerEncounterId = 27;
    public const int CorpseSlugsEncounterId = 9;
    public const int GremlinMercEncounterId = 7;
    public const int SeapunkEncounterId = 12;
    public const int PunchConstructEncounterId = 24;

    public static ReadOnlySpan<int> OvergrowthWeakEncounters => [8, 2, 11, 3];
    public static ReadOnlySpan<int> UnderdocksWeakEncounters => [9, 12, 10, 13];
    public static ReadOnlySpan<int> OvergrowthNormalEncounters =>
        [19, 17, 29, 5, 14, 15, 21, 28, 16, 27, 18, 20];
    public static ReadOnlySpan<int> UnderdocksNormalEncounters =>
        [9, 0, 23, 7, 26, 30, 24, 12, 25, 6];

    // Pool order must match the act's own encounter-declaration order (the game
    // builds AllEliteEncounters/AllBossEncounters by filtering AllEncounters, which
    // is declared alphabetically in e.g. Acts/Overgrowth.cs). It is NOT the act's
    // BossDiscoveryOrder — that list only drives the unlock-progression override in
    // ActModel.ApplyDiscoveryOrderModifications, which does nothing once a profile
    // has seen every boss.
    // Overgrowth elites: BygoneEffigy, Byrdonis, PhrogParasite.
    public static ReadOnlySpan<int> OvergrowthEliteEncounters => [62, 68, 65];

    // Underdocks elites: PhantasmalGardeners, SkulkingColony, TerrorEel. Same two
    // defects as Overgrowth had (a missing third elite, and bosses in discovery
    // order) — fixed by the same rule, but NOT yet verified against a live
    // Underdocks run, since the only capture so far is an Overgrowth one.
    public static ReadOnlySpan<int> UnderdocksEliteEncounters => [72, 86, 67];

    // Overgrowth bosses: CeremonialBeast, TheKin, Vantom.
    public static ReadOnlySpan<int> OvergrowthBossEncounters => [74, 82, 83];

    // Underdocks bosses: LagavulinMatriarch, SoulFysh, WaterfallGiant.
    public static ReadOnlySpan<int> UnderdocksBossEncounters => [77, 79, 84];

    // Hive is act 2 for every run: ActsByIndex has one candidate at index 1, and
    // ActModel.GetRandomList still spends a draw picking it. The pools below are
    // Hive.GenerateAllEncounters() filtered by kind, IN ITS DECLARATION ORDER, which is
    // the same rule the two act-1 regions follow and the thing a shuffled bag depends on.
    //
    // Every one of these already had an id except ExoskeletonsNormal: the emulator's
    // `Exoskeletons` is the four-monster roster, which is the game's WEAK variant.
    // Several of the others carry the emulator's older shorter names (`Chompers` is
    // ChompersNormal, `Obscura` is TheObscuraNormal, `Tunneler` is TunnelerWeak), and a
    // few of those ROSTERS do not match the game's yet -- the emulator's Tunneler holds
    // one where TunnelerWeak holds two. That is a fight-time problem, not a generation
    // one: what a pool needs is identity and order.
    // Hive weak: BowlbugsWeak, ExoskeletonsWeak, ThievingHopperWeak, TunnelerWeak.
    /// <summary>
    /// <c>CombatFactory.ActOneEncounter.ExoskeletonsNormal</c>, appended at the end of
    /// that enum — named here so the pool below does not carry a bare 87 that silently
    /// means something else if anything is ever inserted.
    /// </summary>
    public const int ExoskeletonsNormalEncounterId = 87;

    public static ReadOnlySpan<int> HiveWeakEncounters => [31, 4, 35, 33];

    // Hive normals: BowlbugsNormal, ChompersNormal, ExoskeletonsNormal,
    // HunterKillerNormal, LouseProgenitorNormal, MytesNormal, OvicopterNormal,
    // SlumberingBeetleNormal, SpinyToadNormal, TheObscuraNormal.
    public static ReadOnlySpan<int> HiveNormalEncounters =>
        [32, 1, ExoskeletonsNormalEncounterId, 41, 40, 36, 39, 37, 38, 53];

    // Hive elites: DecimillipedeElite, EntomancerElite, InfestedPrismsElite.
    public static ReadOnlySpan<int> HiveEliteEncounters => [69, 63, 64];

    // Hive bosses: KaiserCrabBoss, KnowledgeDemonBoss, TheInsatiableBoss -- declaration
    // order, which puts KaiserCrab before KnowledgeDemon and TheInsatiable last.
    public static ReadOnlySpan<int> HiveBossEncounters => [75, 76, 81];

    /// <summary>
    /// <c>ActModel.NumberOfWeakEncounters</c> and <c>BaseNumberOfRooms</c>. The base is
    /// 3 weak, and Overgrowth and Underdocks both take it; Hive declares 2 and 14 rooms,
    /// Glory 2 and 13. The emulator hardcoded act 1's numbers, which is correct for the
    /// only act it generated and wrong for every act after.
    /// </summary>
    public static (int Weak, int Rooms) ActRoomCounts(int act) =>
        act switch
        {
            ActHive => (2, 14),
            ActGlory => (2, 13),
            _ => (3, 15),
        };

    /// <summary>StandardActMap: GetNumberOfRooms(singleplayer) + 1.</summary>
    public static int MapBossRowFor(int act) => ActRoomCounts(act).Rooms + 1;

    public const int ActHive = 3;
    public const int ActGlory = 4;

    /// <summary>
    /// How many potion slots a run can ever hold, which is the width of the arrays and of
    /// the observation -- NOT how many are usable. `MaxPotionSlots` says that, and it
    /// starts at two and grows: Potion Belt +2, Phial Holster +1, Alchemical Coffer +4.
    /// The Coffer alone takes a starting run to six, which is why three was never enough.
    /// </summary>
    public const int PotionSlotCapacity = 6;

    /// <summary>
    /// <c>ModelDb.ActsByIndex</c>: the acts a run may play at each index, in the order the
    /// game declares them. <c>ActModel.GetRandomList</c> takes ONE per index off the
    /// act_selection stream — including where there is only one candidate, which still
    /// spends a draw.
    /// </summary>
    /// <remarks>
    /// This is the extension point, and it is deliberately data. The devs have said act 2
    /// and act 3 will get alternates the way act 1 has Overgrowth and Underdocks: that is
    /// a new entry in an existing row, and the selection already rolls over whatever the
    /// row holds. A fourth act is a new row. Neither needs the generator touched — though
    /// a new act does need its pools and its <see cref="ActRoomCounts"/> entry, and an
    /// alternate needs the same, because those are the act's own data and not something
    /// that can be inferred.
    /// </remarks>
    public static readonly int[][] ActCandidatesByIndex =
    [
        [ActOvergrowth, ActUnderdocks],
        [ActHive],
        [ActGlory],
    ];

    /// <summary>Every act's elite list is fifteen long, whatever the act.</summary>
    public const int EliteSequenceLength = 15;

    /// <summary>
    /// The ancients, by the entry name their own Rng stream is keyed on. Act 1's is Neow;
    /// Hive's three are below, one of which each act draws in the last line of
    /// <c>ActModel.GenerateRooms</c>.
    /// </summary>
    /// <summary>Ascender's Bane, which the game refuses to remove from a deck.</summary>
    public const int CardAscendersBane = 10001;

    /// <summary>Zen Weaver's Breathing Techniques hands out two of these.</summary>
    public const int CardEnlightenment = 165;

    /// <summary>The curse Reflections' Shatter adds after copying the deck.</summary>
    public const int CardBadLuck = 10021;

    /// <summary>Zen Weaver's three prices, its CanonicalVars.</summary>
    public const int ZenWeaverBreathingCost = 50;

    public const int ZenWeaverEmotionalCost = 125;

    public const int ZenWeaverAcupunctureCost = 250;

    /// <summary>The card Tinker Time builds, and only Tinker Time.</summary>
    public const int CardMadScience = 292;

    /// <summary>
    /// How many characters Orobas may brand a Sea Glass with: everyone unlocked except
    /// the player's own, which is four on the mature profile the captures are taken on.
    /// Only the DRAW matters — the chosen character brands a relic the emulator does not
    /// model — but skipping it would shift every pool pick after it.
    /// </summary>
    public const int OtherCharacterCount = 4;

    /// <summary><c>ModelDb.AllCharacters</c>, by entry, in its declaration order.</summary>
    public static readonly string[] AllCharacterEntries =
    [
        "IRONCLAD",
        "SILENT",
        "REGENT",
        "NECROBINDER",
        "DEFECT",
    ];

    /// <summary>
    /// The characters Orobas may brand a Sea Glass with, in the order it draws over them:
    /// <c>ModelDb.AllCharacters</c> is Ironclad, Silent, Regent, Necrobinder, Defect, and
    /// Orobas takes <c>Where(c => c.Id != Owner.Character.Id)</c> — so for an Ironclad run
    /// that is these four, in this order.
    /// </summary>
    public static ReadOnlySpan<int> OtherCharacterPoolFor(
        int index,
        CharacterId character = CharacterId.Ironclad
    ) =>
        index switch
        {
            0 => character == CharacterId.Ironclad
                ? GeneratedData.CardPools.Silent
                : GeneratedData.CardPools.Ironclad,
            1 => GeneratedData.CardPools.Regent,
            2 => GeneratedData.CardPools.Necrobinder,
            _ => GeneratedData.CardPools.Defect,
        };

    public const string AncientNeow = "NEOW";

    public const string AncientDarv = "DARV";

    /// <summary>
    /// Darv's `_validRelicSets`, one entry per set, in DECLARATION order. Seven of the
    /// nine sets hold a single relic; the last two hold two apiece and are gated on the
    /// act, so they live in their own constants below.
    /// </summary>
    /// <remarks>
    /// Order is load-bearing. The event picks one relic from each set whose filter passes,
    /// shuffles THAT list, and takes from the front -- so a list assembled in any other
    /// order draws a different three from the same stream position, the same way the Relic
    /// Trader's shelf does.
    /// </remarks>
    public static ReadOnlySpan<int> DarvSingleRelicSets =>
        [
            8, // Astrolabe
            20, // BlackStar
            39, // CallingBell
            74, // EmptyCage
            185, // PandorasBox
            227, // RunicPyramid
            243, // SneckoEye
        ];

    /// <summary>Pandora's Box is skipped by a run whose modifiers clear the deck.</summary>
    public static readonly int DarvPandorasBox = 185;

    /// <summary>Separate Ectoplasm and Sozu singleton sets, only at `CurrentActIndex == 1`.</summary>
    public static ReadOnlySpan<int> DarvActOneSet => [70, 245];

    /// <summary>Separate Philosopher's Stone and Velvet Choker sets at `CurrentActIndex >= 1`.</summary>
    public static ReadOnlySpan<int> DarvActTwoSet => [196, 281];

    /// <summary>Dusty Tome, which replaces the third option on a coin flip.</summary>
    public static readonly int DarvDustyTome = 69;
    public const string AncientNonupeipe = "NONUPEIPE";
    public const string AncientTanx = "TANX";
    public const string AncientVakuu = "VAKUU";

    /// <summary>
    /// <c>ActModel.GetUnlockedAncients</c>, act by act. Both act-1 regions declare exactly
    /// ONE ancient and it is Neow, so act 1's pick is a one-item <c>NextItem</c> that
    /// still spends its draw. Hive declares three and drops Orobas when its epoch is
    /// unrevealed; Glory declares three of its own. The mature profile the captures are
    /// taken on has everything revealed.
    /// </summary>
    public static string[] AncientsFor(int act) =>
        act switch
        {
            ActHive => [AncientOrobas, AncientPael, AncientTezcatara],
            ActGlory => [AncientNonupeipe, AncientTanx, AncientVakuu],
            _ => [AncientNeow],
        };

    public const string AncientOrobas = "OROBAS";
    public const string AncientPael = "PAEL";
    public const string AncientTezcatara = "TEZCATARA";

    // v0.111.0 Orobas: SandCastle belongs to OptionPool2.
    public static ReadOnlySpan<int> OrobasPool1 => [71, 101];
    public static ReadOnlySpan<int> OrobasPool2 => [2, 68, 211, 229];
    public static ReadOnlySpan<int> OrobasPool3 => [268, 6];

    // The ancients' blessings that DO something when taken.
    public const int RelicElectricShrymp = 71;
    public const int RelicBeautifulBracelet = 12;
    public const int RelicClaws = 50;
    public const int RelicDistinguishedCape = 62;
    public const int RelicPreservedFog = 207;
    public const int RelicMeatCleaver = 148;
    public const int RelicPumpkinCandle = 209;
    public const int RelicSereTalon = 235;

    /// <summary>Toy Box's `DynamicVar("Relics", 4m)`: four wax relics on the screen.</summary>
    public const int ToyBoxRelics = 4;

    /// <summary>Toy Box's `DynamicVar("Combats", 3m)`: one melts every third fight.</summary>
    public const int ToyBoxCombats = 3;

    /// <summary>Pael's Wing's `DynamicVar("Sacrifices", 2m)`: a relic every second one.</summary>
    public const int PaelsWingSacrifices = 2;

    public const int RelicFurCoat = 95;
    public const int RelicGoldenCompass = 104;

    /// <summary>Fur Coat's `DynamicVar("Combats", 8m)`: eight rooms marked.</summary>
    public const int FurCoatCombats = 8;

    public const int RelicPaelsWing = 184;
    public const int RelicToyBox = 271;
    public const int RelicLordsParasol = 139;
    public const int RelicMassiveScroll = 145;
    public const int RelicArchaicTooth = 6;
    public const int RelicTouchOfOrobas = 268;
    public const int RelicTriBoomerang = 272;

    /// <summary>
    /// `ArchaicTooth.TranscendenceUpgrades`: the five starter cards that have a
    /// transcendent form, and what each becomes. The relic transforms the FIRST card in
    /// the deck that is a key here -- Bash, for an Ironclad run.
    /// </summary>
    public static readonly (int Starter, int Ancient)[] TranscendenceUpgrades =
    [
        (30, 59), // Bash -> Break
        (323, 482), // Neutralize -> Suppress
        (524, 368), // Unleash -> Protector
        (179, 304), // FallingStar -> MeteorShower
        (156, 375), // Dualcast -> Quadcast
    ];

    /// <summary>
    /// `TouchOfOrobas.RefinementUpgrades`: each character's starter relic and its refined
    /// form. A starter not in this table becomes a Circlet, which is the game's fallback
    /// rather than a failure.
    /// </summary>
    public static readonly (int Starter, int Upgraded)[] RefinementUpgrades =
    [
        (36, 19), // BurningBlood -> BlackBlood
        (221, 220), // RingOfTheSnake -> RingOfTheDrake
        (64, 63), // DivineRight -> DivineDestiny
        (30, 197), // BoundPhylactery -> PhylacteryUnbound
        (52, 116), // CrackedCore -> InfusedCore
    ];

    /// <summary>`ModelDb.Relic&lt;Circlet&gt;()`, Touch of Orobas's fallback.</summary>
    public const int RelicCirclet = 49;
    public const int RelicGlassEye = 101;
    public const int RelicSandCastle = 229;
    public const int RelicAlchemicalCoffer = 2;
    public const int RelicPaelsHorn = 180;
    public const int RelicYummyCookie = 296;
    public const int RelicBiiigHug = 17;
    public const int RelicStorybook = 251;

    public const int RelicPrismaticGemOption = 208;
    public const int RelicSeaGlass = 232;

    // Pael: PaelsFlesh, PaelsHorn, PaelsTears; PaelsWing; PaelsEye, PaelsBlood.
    public static ReadOnlySpan<int> PaelPool1 => [178, 180, 182];
    public static ReadOnlySpan<int> PaelPool2 => [184];
    public static ReadOnlySpan<int> PaelPool3 => [177, 175];
    public const int RelicPaelsClaw = 176;
    public const int RelicPaelsTooth = 183;
    public const int RelicPaelsGrowth = 179;
    public const int RelicPaelsLegion = 181;

    // Tezcatara: VeryHotCocoa, YummyCookie; BiiigHug, Storybook, ToastyMittens;
    // GoldenCompass, PumpkinCandle, ToyBox, SealOfGold.
    public static ReadOnlySpan<int> TezcataraPool1 => [283, 296];
    public static ReadOnlySpan<int> TezcataraPool2 => [17, 251, 266];
    public static ReadOnlySpan<int> TezcataraPool3 => [104, 209, 271, 233];
    public const int RelicNutritiousSoup = 168;

    public const int RelicBurningBlood = 36;
    public const int RelicFrozenEgg = 93;
    public const int RelicLizardTail = 137;
    public const int RelicMembershipCard = 150;
    public const int RelicMoltenEgg = 156;
    public const int RelicTinyMailbox = 265;
    public const int RelicToxicEgg = 270;
    public const int RelicWarPaint = 287;
    public const int RelicWhetstone = 288;
    public const int RelicWhiteBeastStatue = 290;
    public const int RelicBlackBlood = 19;
    public const int RelicMeatOnTheBone = 149;
    public const int RelicArcaneScroll = 5;
    public const int RelicChosenCheese = 48;
    public const int RelicCursedPearl = 54;
    public const int RelicFishingRod = 89;

    /// <summary>FishingRod's DynamicVar("Combats", 3m): it fires every third monster room.</summary>
    public const int FishingRodCombats = 3;
    public const int RelicGoldenPearl = 105;
    public const int RelicHeftyTablet = 111;
    public const int RelicKaleidoscope = 124;
    public const int RelicLeadPaperweight = 133;
    public const int RelicLeafyPoultice = 134;
    public const int RelicLeesWaffle = 135;
    public const int RelicLostCoffer = 140;
    public const int RelicMango = 144;

    // The two Hungry For Mushrooms relics. Both are `HasUponPickupEffect`; neither has any
    // combat behaviour modelled yet (they are in the 125 the emulator does not model), but
    // the pickup half is the event's whole payload and belongs with the relic.
    public const int RelicBigMushroom = 16;
    public const int RelicFragrantMushroom = 91;
    public const int RelicLargeCapsule = 129;
    public const int RelicLavaRock = 132;
    public const int RelicNeowsBones = 161;
    public const int RelicNeowsTalisman = 162;
    public const int RelicNeowsTorment = 163;
    public const int RelicNewLeaf = 164;
    public const int RelicNutritiousOyster = 167;
    public const int RelicOldCoin = 170;
    public const int RelicPear = 190;
    public const int RelicPhialHolster = 195;
    public const int RelicPomander = 201;
    public const int RelicPrecariousShears = 205;
    public const int RelicPreciseScissors = 206;
    public const int RelicScrollBoxes = 231;
    public const int RelicSilkenTress = 239;
    public const int RelicSilverCrucible = 240;
    public const int RelicSmallCapsule = 242;
    public const int RelicStrawberry = 252;
    public const int RelicStoneHumidifier = 250;
    public const int RelicWingedBoots = 293;

    /// <summary>
    /// Winged Boots' <c>DynamicVar("Rooms", 3m)</c>: how many times it will carry the
    /// player to a node the map does not connect to before it is used up.
    /// </summary>
    public const int WingedBootsTravels = 3;
    public const int RelicPrismaticGem = 208;

    public const int RelicAstrolabe = 8;
    public const int RelicCallingBell = 39;
    public const int RelicDustyTome = 69;
    public const int RelicEmptyCage = 74;
    public const int RelicPandorasBox = 185;

    /// <summary>
    /// `ArchaicTooth.TranscendenceCards` -- the eleven a starter card can become. Dusty
    /// Tome excludes them when picking its Ancient card, so a run cannot be handed the
    /// same card by both relics.
    /// </summary>
    public static readonly string[] TranscendenceCardNames =
    [
        "Bash",
        "Break",
        "Doubt",
        "Dualcast",
        "FallingStar",
        "MeteorShower",
        "Neutralize",
        "Protector",
        "Quadcast",
        "Suppress",
        "Unleash",
    ];

    /// <summary>Looming Fruit's `MaxHpVar(31m)`, paid on pickup.</summary>
    public const int RelicLoomingFruit = 138;

    /// <summary>Normal rooms drawn from the act's weak pool before the normal one.</summary>
    /// <summary>Endless Conveyor's GoldVar(40): the price of one grab.</summary>
    public const int ConveyorGrabCost = 40;

    /// <summary>The construct fight Punch Off's second page starts.</summary>
    /// <summary>The fight Dense Vegetation's Rest wakes up, entered from the event.</summary>
    public const int DenseVegetationEncounterId = 55;

    /// <summary>
    /// The Lantern Key's `EnterCombatWithoutExitingEvent&lt;MysteriousKnightEventEncounter&gt;`,
    /// and the three Battleworn Dummy settings. `CombatFactory` has built all four since
    /// the event encounters were modelled; the events that reach them did not.
    /// </summary>
    public const int MysteriousKnightEncounterId = 58;
    public const int BattlewornDummyEncounterIds0 = 59;

    /// <summary>SurroundedPower.Direction.Right, the enum's zero and the player's start.</summary>
    public const int FacingRight = 1;

    /// <summary>SurroundedPower.Direction.Left.</summary>
    public const int FacingLeft = 2;

    /// <summary>CrabRagePower's PowerVar&lt;StrengthPower&gt;, paid when its partner dies.</summary>
    public const int CrabRageStrength = 6;

    /// <summary>CrabRagePower's BlockVar, unpowered.</summary>
    public const int CrabRageBlock = 99;

    /// <summary>
    /// What Disintegration is offered AGAINST on each of the demon's three casts.
    /// </summary>
    public static readonly int[] CurseOfKnowledgePairs = [ST.MindRot, ST.Sloth, ST.WasteAway];

    /// <summary>The Knowledge Demon's three Disintegration amounts, in cast order.</summary>
    public static readonly int[] DisintegrationDamageValues = [6, 7, 8];

    /// <summary>MindRot's PowerVar: draw this many fewer cards.</summary>
    public const int MindRotAmount = 1;

    /// <summary>Sloth's PowerVar: play at most this many cards a turn.</summary>
    public const int SlothAmount = 3;

    /// <summary>WasteAway's PowerVar: this much less energy each turn.</summary>
    public const int WasteAwayAmount = 1;

    /// <summary>ReattachPower's Amount on every Decimillipede segment.</summary>
    public const int DecimillipedeReattachHeal = 25;

    public const int PunchOffEncounterId = 56;

    // Encounters that roll something off their OWN stream, and so need an entry id.
    public const int BowlbugsWeakEncounterId = 31;

    public const int BowlbugsNormalEncounterId = 32;

    public const int DecimillipedeEncounterId = 69;

    public const int ScrollsWeakEncounterId = 49;

    public const int ScrollsNormalEncounterId = 50;

    /// <summary>Room Full of Cheese's Gorge: eight offered, two kept.</summary>
    public const int GorgeCardChoices = 8;
    public const int GorgeCardsKept = 2;

    /// <summary>Brain Leech's IntVar("FromCardChoiceCount").</summary>
    public const int BrainLeechCardChoices = 5;

    /// <summary>
    /// The Waterlogged Scriptorium's two paid options, from the event's own DynamicVars:
    /// Gold(55) for the Tentacle Quill and PricklySpongeGold(99) for the sponge.
    /// </summary>
    public const int ScriptoriumQuillCost = 55;
    public const int ScriptoriumSpongeCost = 99;

    /// <summary>Tea Master's prices, from the event's own DynamicVars.</summary>
    public const int BoneTeaCost = 50;
    public const int EmberTeaCost = 150;

    /// <summary>Welcome to Wongos' price tags, from the event's own DynamicVars.</summary>
    public const int WongosBargainBinCost = 100;
    public const int WongosFeaturedItemCost = 200;
    public const int WongosMysteryBoxCost = 300;

    public const int WeakEncountersPerAct = 3;

    /// <summary>
    /// Ascender's Bane. It used to stand in for EVERY curse any event or relic inflicted,
    /// which is not merely the wrong name -- Ascender's Bane is Ethereal and the curses it
    /// replaced are not, and it is one of the eight the game will never generate. Every
    /// site names or rolls its own curse now; this is the starting deck's copy and the
    /// last-resort fallback if the curse pool ever comes back empty.
    /// </summary>
    public const int CursePlaceholderCard = 10001;
    public const int SpoilsMapCard = 10020;
    public const int NeowsFuryCard = 321;

    public static ReadOnlySpan<int> StarterDeckIds =>
        [
            IC.StrikeIronclad,
            IC.StrikeIronclad,
            IC.StrikeIronclad,
            IC.StrikeIronclad,
            IC.StrikeIronclad,
            IC.DefendIronclad,
            IC.DefendIronclad,
            IC.DefendIronclad,
            IC.DefendIronclad,
            IC.Bash,
            IC.AscendersBane,
        ];

    /// <summary>
    /// Neow's two curse relics that arrived after the rest of the pool, appended to the
    /// relic ids rather than slotted in alphabetically so no existing id moves.
    /// </summary>
    public const int RelicDowsingRod = 297;
    public const int RelicNeowsSacrifice = 298;

    /// <summary>
    /// <c>Neow.CurseOptions</c>, in its DECLARATION order. The curse is a
    /// <c>NextItem</c> over this list, so its length and order decide which curse a roll
    /// lands on: a pool of eight read index 7 (Silver Crucible) where the game's ten read
    /// index 8 (Silken Tress) off the same draw.
    /// </summary>
    public static ReadOnlySpan<int> NeowCurseOptions =>
        [
            RelicCursedPearl,
            RelicDowsingRod,
            RelicHeftyTablet,
            RelicLargeCapsule,
            RelicLeafyPoultice,
            RelicNeowsBones,
            RelicNeowsSacrifice,
            RelicPrecariousShears,
            RelicSilkenTress,
            RelicSilverCrucible,
        ];

    /// <summary>
    /// The six blessings Neow offers as one of a PAIR rather than from a flat list --
    /// LavaRock/SmallCapsule, NeowsTalisman/Pomander, NutritiousOyster/StoneHumidifier.
    /// In <c>Neow.AllPossibleOptions</c> they are appended after the curse and positive
    /// groups, in this order, which is the order anything shuffling that list depends on.
    /// </summary>
    public static ReadOnlySpan<int> NeowPairedOptions => [132, 162, 167, 201, 242, 250];

    public static ReadOnlySpan<int> NeowPositiveOptions =>
        [5, 29, 89, 105, 124, 133, 140, 163, 164, 195, 206, 231, 293];

    /// <summary>
    /// The game's <c>CardModel.IsUpgradable</c>: <c>CurrentUpgradeLevel &lt; MaxUpgradeLevel</c>.
    /// </summary>
    /// <remarks>
    /// This used to hold fourteen card ids written out by hand, against the thirty-seven
    /// that actually override <c>MaxUpgradeLevel</c> to zero. The twenty-three it missed
    /// were curses and statuses, which is invisible until something upgrades AT RANDOM:
    /// Doors of Light and Dark shuffles the upgradable cards and takes two, so one extra
    /// name in the candidate list is a different shuffle and a different pick. A live
    /// capture (`NXV45HW43K`) upgraded two Strikes where the emulator, counting Greed
    /// among the candidates, upgraded a Strike and a Defend.
    /// </remarks>
    public static bool IsRunCardUpgradable(CardInstance card)
    {
        return !card.Upgraded && GeneratedData.Cards.Get(Math.Abs(card.DefId)).Upgradable;
    }
}
