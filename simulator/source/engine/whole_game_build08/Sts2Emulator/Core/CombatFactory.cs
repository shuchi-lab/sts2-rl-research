using System.Collections.Generic;
using System.Linq;
using Sts2Emulator.Core.Effects;
using Sts2Emulator.Core.Run;

namespace Sts2Emulator.Core;

public static class CombatFactory
{
    // Set before CreateEncounter; CreateEnemy uses this for HP instead of the main rng.
    // Matches RunState.Rng.Niche which is used exclusively by SetUniqueMonsterHpValue.
    private static CountingRandom? _currentNicheHpRng;

    // Tracks HP values already assigned in the current encounter to prevent duplicates,
    // matching Creature.SetUniqueMonsterHpValue which excludes already-used MaxHp values.
    private static HashSet<int>? _usedNicheHps;

    // CombatState.PresetEnemyHps while the roster is built: HPs an event layout rolled.
    private static Queue<int>? _presetHps;

    private const int StartingPlayerHp = 64;
    private const int StartingPlayerMaxHp = 80;

    /// <summary>The ascension of the combat currently being built; see CreateEnemy.</summary>
    private static int _currentAscension = Ascension.DefaultLevel;
    private const int StartingEnergy = 3;

    // Ironclad starting deck card IDs (from Generated/Cards.g.cs).
    private const int StrikeId = IC.StrikeIronclad; // 472
    private const int DefendId = IC.DefendIronclad; // 131
    private const int BashId = IC.Bash; // 30
    private const int AscendersBaneId = IC.AscendersBane; // 10001

    /// <summary>
    /// The encounters the emulator can build. Internal rather than private so a combat
    /// test can name the encounter it is about instead of passing a bare index.
    /// </summary>
    internal enum ActOneEncounter
    {
        Cultists,
        Chompers,
        NibbitsWeak,
        SlimesWeak,
        Exoskeletons,
        Inklets,
        TwoTailedRats,
        GremlinMerc,
        FuzzyWurmCrawler,
        CorpseSlugs,
        SludgeSpinner,
        ShrinkerBeetle,
        Seapunk,
        Toadpoles,
        Mawler,
        NibbitsNormal,
        SlimesNormal,
        FlyconidNormal,
        SnappingJaxfruitNormal,
        CubexConstruct,
        VineShambler,
        OvergrowthCrawlers,
        CultistAndSeapunk,
        FossilStalker,
        PunchConstruct,
        SewerClam,
        HauntedShip,
        SlitheringStrangler,
        RubyRaiders,
        Fogmog,
        LivingFog,
        BowlbugsWeak,
        Bowlbugs,
        Tunneler,
        TunnelerAndChomper,
        ThievingHopper,
        Mytes,
        SlumberingBeetle,
        SpinyToad,
        Ovicopter,
        LouseProgenitor,
        HunterKiller,
        Axebot,
        DevotedSculptor,
        Fabricator,
        FrogKnight,
        GlobeHead,
        TurretOperator,
        OwlMagistrate,
        ScrollsWeak,
        Scrolls,
        SlimedBerserker,
        LostAndForgotten,
        Obscura,
        ConstructMenagerie,
        DenseVegetation,
        PunchOff,
        FakeMerchant,
        MysteriousKnight,
        BattlewornDummy1,
        BattlewornDummy2,
        BattlewornDummy3,
        BygoneEffigy,
        Entomancer,
        InfestedPrisms,
        PhrogParasite,
        SoulNexus,
        TerrorEel,
        Byrdonis,
        Decimillipede,
        Knights,
        MechaKnight,
        PhantasmalGardeners,
        Aeonglass,
        CeremonialBeast,
        KaiserCrab,
        KnowledgeDemon,
        LagavulinMatriarch,
        Queen,
        SoulFysh,
        TestSubject,
        TheInsatiable,
        TheKin,
        Vantom,
        WaterfallGiant,
        Architect,
        SkulkingColony,

        // Hive's five-Exoskeleton encounter. The emulator's `Exoskeletons` above is the
        // FOUR-monster roster, which is the game's ExoskeletonsWeak -- so the Normal
        // variant had no id at all. Appended rather than inserted beside its sibling:
        // these ordinals are the encounter ids, and the act pools name them as literals.
        ExoskeletonsNormal,
    }

    private static readonly ActOneEncounter[] OvergrowthWeakEncounters =
    [
        ActOneEncounter.NibbitsWeak,
        ActOneEncounter.SlimesWeak,
        ActOneEncounter.ShrinkerBeetle,
        ActOneEncounter.FuzzyWurmCrawler,
    ];

    private static readonly ActOneEncounter[] UnderdocksWeakEncounters =
    [
        ActOneEncounter.CorpseSlugs,
        ActOneEncounter.Seapunk,
        ActOneEncounter.SludgeSpinner,
        ActOneEncounter.Toadpoles,
    ];

    public static CombatState NewCombat(int seed)
    {
        return NewCombat(new Random(seed));
    }

    public static CombatState NewCombat(Random rng)
    {
        var state = new CombatState
        {
            PlayerHp = StartingPlayerHp,
            PlayerMaxHp = StartingPlayerMaxHp,
            Energy = StartingEnergy,
            MaxEnergy = StartingEnergy,
        };
        Reset(state, rng);
        return state;
    }

    public static void Reset(CombatState state, int? seed = null)
    {
        Reset(state, seed.HasValue ? new Random(seed.Value) : new Random());
    }

    public static void Reset(CombatState state, Random rng)
    {
        Reset(state, rng, StarterDeck());
    }

    public static void Reset(CombatState state, Random rng, ReadOnlySpan<int> deckIds)
    {
        Reset(state, rng, deckIds, null);
    }

    public static void Reset(
        CombatState state,
        Random rng,
        ReadOnlySpan<int> deckIds,
        int? encounterId
    )
    {
        Reset(state, rng, deckIds, encounterId, []);
    }

    // Same as the deck+encounter reset but lets callers set the "weak" combat
    // context. completedCombatRoomsBeforeCurrent in [0,3) selects the weak
    // encounter variant (e.g. CorpseSlugsWeak = 2 slugs vs Normal = 3); -1 (the
    // default elsewhere) yields the normal variant.
    public static void Reset(
        CombatState state,
        Random rng,
        ReadOnlySpan<int> deckIds,
        int? encounterId,
        int completedCombatRoomsBeforeCurrent,
        ulong? encounterRngSeed = null
    )
    {
        Reset(
            state,
            rng,
            deckIds,
            encounterId,
            [],
            StartingPlayerHp,
            StartingPlayerMaxHp,
            [],
            playerGold: 0,
            encounterRngSeed: encounterRngSeed,
            completedCombatRoomsBeforeCurrent: completedCombatRoomsBeforeCurrent
        );
    }

    public static void Reset(
        CombatState state,
        Random rng,
        ReadOnlySpan<int> deckIds,
        int? encounterId,
        ReadOnlySpan<int> relicIds
    )
    {
        Reset(state, rng, deckIds, encounterId, relicIds, StartingPlayerHp, StartingPlayerMaxHp);
    }

    public static void Reset(
        CombatState state,
        Random rng,
        ReadOnlySpan<int> deckIds,
        int? encounterId,
        ReadOnlySpan<int> relicIds,
        int playerHp,
        int playerMaxHp
    )
    {
        Reset(state, rng, deckIds, encounterId, relicIds, playerHp, playerMaxHp, []);
    }

    public static void Reset(
        CombatState state,
        Random rng,
        ReadOnlySpan<int> deckIds,
        int? encounterId,
        ReadOnlySpan<int> relicIds,
        int playerHp,
        int playerMaxHp,
        ReadOnlySpan<int> potionIds
    )
    {
        Reset(
            state,
            rng,
            deckIds,
            encounterId,
            relicIds,
            playerHp,
            playerMaxHp,
            potionIds,
            playerGold: 0
        );
    }

    public static void Reset(
        CombatState state,
        Random rng,
        ReadOnlySpan<int> deckIds,
        int? encounterId,
        ReadOnlySpan<int> relicIds,
        int playerHp,
        int playerMaxHp,
        ReadOnlySpan<int> potionIds,
        int playerGold,
        bool deckPreShuffled = false,
        CountingRandom? shuffleRng = null,
        ulong? encounterRngSeed = null,
        int nicheSkipCount = 0,
        Random? aiRng = null,
        int completedCombatRoomsBeforeCurrent = -1
    )
    {
        var deck = deckIds.ToArray().Select(id => new CardInstance(Math.Abs(id), id < 0)).ToArray();
        Reset(
            state,
            rng,
            deck,
            encounterId,
            relicIds,
            playerHp,
            playerMaxHp,
            potionIds,
            playerGold,
            deckPreShuffled,
            shuffleRng,
            encounterRngSeed,
            nicheSkipCount,
            aiRng,
            completedCombatRoomsBeforeCurrent
        );
    }

    public static void Reset(
        CombatState state,
        Random rng,
        ReadOnlySpan<CardInstance> deck,
        int? encounterId,
        ReadOnlySpan<int> relicIds,
        int playerHp,
        int playerMaxHp,
        ReadOnlySpan<int> potionIds,
        int playerGold,
        bool deckPreShuffled = false,
        CountingRandom? shuffleRng = null,
        ulong? encounterRngSeed = null,
        int nicheSkipCount = 0,
        Random? aiRng = null,
        int completedCombatRoomsBeforeCurrent = -1,
        IReadOnlyList<RelicInstance>? runRelics = null,
        bool furCoatBites = false
    )
    {
        // Assigned on every reset so a reused state cannot carry a marked room forward.
        state.FurCoatBites = furCoatBites;
        state.PlayerMaxHp = Math.Max(1, playerMaxHp);
        state.PlayerHp = Math.Clamp(playerHp, 0, state.PlayerMaxHp);
        state.PlayerGold = Math.Max(0, playerGold);
        state.Energy = StartingEnergy;
        state.MaxEnergy = StartingEnergy;
        state.PlayerBlock = 0;
        state.PendingSelection = null;
        state.PendingTurnSetup = null;
        state.PendingCardPlay = null;
        state.PendingPotionAutoPlays = null;
        state.PendingNestedAutoPlays.Clear();
        state.SerpentFormSnapshots.Clear();
        state.AutoPlayMayPause = false;
        CardBlockHistory.ResetCombat(state);
        state.ActiveCardResources = default;
        state.HexApplier = null;
        state.ShrinkApplier = null;
        state.AutoPlaying = false;
        state.PlayerBuffs = [];
        state.TenderCardsPlayedThisTurn = 0;
        state.SlothCardsPlayedThisTurn = 0;
        state.ForgetDrawOrder();
        state.Hand = [];
        state.DiscardPile = [];
        state.ExhaustPile = [];
        state.StolenFromDeck.Clear();
        state.StolenBackCards.Clear();
        state.ReturnToHandBeforeDraw = [];
        state.CopiesToHandBeforeDraw = [];
        state.EndTurnAwaitingSelection = false;
        state.PendingEnemyTurn = null;
        state.DeferCardRupture = false;
        state.CardRuptureStrength = 0;
        state.AfterimageBeforePlay = 0;
        state.StormBeforePlay = 0;
        state.SubroutineBeforePlay = 0;
        state.StatusCardsDrawnThisTurn = 0;
        state.ResolvingDefendCard = false;
        state.PlayedCardCostForCombat = int.MinValue;
        state.PotionSlots = new int[RunConstants.PotionSlotCapacity];
        for (int i = 0; i < Math.Min(state.PotionSlots.Length, potionIds.Length); i++)
        {
            state.PotionSlots[i] = potionIds[i];
        }

        // A relic's counter is RUN state in the game, where the relic is one object for
        // the whole run -- Girya's lifts, a tea's remaining combats. The run hands them
        // over HERE, before `ApplyCombatStart` runs, because that is where they are read.
        // The run engine used to copy them in after this call returned, which is after
        // every combat-start hook had already asked and been told zero: three lifts on a
        // Girya applied no Strength at all.
        state.Relics = relicIds
            .ToArray()
            .Select(id => new RelicInstance(
                id,
                runRelics?.FirstOrDefault(relic => relic.DefId == id).Counter ?? 0
            ))
            .ToList();
        state.Turn = 0;
        state.RoundNumber = 1;
        state.PlayerTurn = true;
        state.SkillPlayedWhileSmoggy = false;
        state.AttackCardsPlayedThisTurn = 0;
        state.AttackOrSkillPlaysStartedThisTurn = 0;
        state.CardPlaysStartedThisTurn = 0;
        state.PlayerHpLostThisTurn = 0;
        state.CardsExhaustedThisTurn = 0;
        state.CardsDiscardedThisTurn = 0;
        // Preserve a ShuffleRng the caller set on the state (the direct combat env
        // wires GameRng(seed,"shuffle") there); only override when a shuffleRng arg
        // is explicitly passed (the run engine).
        state.ShuffleRng = shuffleRng ?? state.ShuffleRng;
        // Preserve an AiRng the caller set (the direct combat env wires
        // GameRng(seed,"monster_ai") there); only override when one is passed in.
        state.AiRng = aiRng ?? state.AiRng;

        state.DrawPile = deck.ToArray().ToList();

        var encounter = encounterId.HasValue
            ? (ActOneEncounter)encounterId.Value
            : SelectFirstCombatEncounter(rng);
        state.EncounterId = (int)encounter;
        state.IsEliteCombat = IsEliteEncounter(encounter);
        // Use state.NicheHpRng (set by caller) as the dedicated HP RNG matching
        // RunState.Rng.Niche.  Null falls back to the main combat rng.
        _currentAscension = state.AscensionLevel;
        _currentNicheHpRng = state.NicheHpRng;
        _usedNicheHps = _currentNicheHpRng != null ? new HashSet<int>() : null;
        _presetHps = state.PresetEnemyHps is { Count: > 0 } preset ? new Queue<int>(preset) : null;
        state.PresetEnemyHps = null;
        try
        {
            state.Enemies = CreateEncounter(
                encounter,
                rng,
                encounterRngSeed,
                completedCombatRoomsBeforeCurrent,
                state.AscensionLevel
            );
        }
        finally
        {
            _currentNicheHpRng = null;
            _usedNicheHps = null;
            _presetHps = null;
        }

        // SurroundedPower goes on the PLAYER, so it cannot be applied while the roster is
        // being built. Direction.Right is the enum's zero, which is where the player
        // starts -- with their back to the Crusher.
        if (state.Enemies.Any(e => BuffSystem.Get(e.Buffs, BuffId.BackAttackLeft) > 0))
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Surrounded, Run.RunConstants.FacingRight);
        }

        // `FurCoat.BeforeCombatStart`: StartCombatInternal runs it after every creature's
        // AfterCreatureAdded and before the first StartTurn, which rolls the opening
        // intents and ends SetupPlayerTurn with AfterPlayerTurnStart (Festive Popper) ahead
        // of AfterSideTurnStart (Rampart). Biting after this method returned let Popper
        // hit full-HP enemies and Rampart block a Turret Operator the game had killed.
        // `FurCoatBites` stays set so `RelicEffects.Spawned` bites reinforcements too.
        foreach (var enemy in state.Enemies)
        {
            RelicEffects.BiteForFurCoat(state, enemy);
        }

        EnemyAI.ChooseIntents(state.Enemies, state.Turn, rng, state.AiRng, state.AscensionLevel);
        EnemyAI.UpdateSecondaryIntents(state.Enemies);

        // Shuffle draw pile (skip if caller pre-shuffled it) and deal opening hand of 5.
        // Use the dedicated Shuffle stream when available (matches the game's
        // State.Rng.Shuffle); ShufflePile is the same Fisher-Yates as GameRng.Shuffle.
        if (!deckPreShuffled)
        {
            CardEffects.ShufflePile(state.DrawPile, state.ShuffleRng ?? rng);
        }

        RelicEffects.ApplyAfterCombatRoomEntered(state, rng);
        RelicEffects.ApplyBeforeOpeningHand(state, rng);

        if (PlayerTurnSetup.Applies(state))
        {
            // Anchor and the other combat-start effects precede any setup pause.
            // Ordinary/late AfterPlayerTurnStart callbacks use the continuation.
            RelicEffects.ApplyCombatStart(state, rng);
            RelicEffects.ApplyStartOfPlayerTurn(state, rng);
            PlayerTurnSetup.BeginOpening(state, rng);
            if (CombatEngine.PlayerIsDead(state) || CombatEngine.NoPrimaryEnemyLeft(state))
                return;
            RelicEffects.ApplySideStartRelicsForTurnSetup(state, rng);
            CombatEngine.ApplyRampartAtPlayerTurnStart(state, extraTurn: false);
            PlayerTurnSetup.AfterSideStart(state, rng);
            RelicEffects.ApplyAfterPlayerHpChanged(state);
            return;
        }

        RelicEffects.AdvancePendulumBeforeHandDraw(state);
        RelicEffects.TickPollinousCore(state);
        // ModifyHandDraw precedes the Innate maximum and opening hand cap.
        int handDraw = ApplyTurnOneDrawPileReorder(
            state.DrawPile,
            5 + RelicEffects.ExtraOpeningHandDraw(state)
        );
        RelicEffects.ResetPollinousCoreAfterModifyingHandDraw(state);
        DrawOpeningHand(
            state,
            Math.Min(handDraw, CardEffects.MaxCardsInHand - state.Hand.Count),
            rng
        );

        // Bellows and Vexing Puzzlebox are `AfterPlayerTurnStart` on turn ONE, which is
        // after the opening hand has been dealt -- so they cannot ride the ordinary
        // turn-start seam, which only runs from turn two.
        RelicEffects.ApplyStartOfPlayerTurnRares(state, 1, rng);

        if (RelicEffects.OpensToolboxScreen(state, 1))
        {
            CardEffects.OpenToolboxSelection(state, rng);
        }

        if (RelicEffects.OpensGamblingChipScreen(state, 1))
        {
            if (state.PendingSelection is null)
                CombatEngine.OpenGamblingChipScreenForCombatStart(state);
            else
                state.GamblingChipOwed = true;
        }

        RelicEffects.ApplyCombatStart(state, rng);
        RelicEffects.ApplyStartOfPlayerTurn(state, rng);
        RelicEffects.ApplyCrossbowAfterSideTurnStart(state, rng);
        RelicEffects.ApplyFlowersAfterSideTurnStart(state);
        CombatEngine.ApplyRampartAtPlayerTurnStart(state, extraTurn: false);
        QueueCombatStartAutoPlays(state);
        RelicEffects.ApplyAfterPlayerHpChanged(state);
    }

    internal static void DrawOpeningHand(CombatState state, int handDraw, Random rng)
    {
        for (int i = 0; i < handDraw && state.DrawPile.Count > 0; i++)
        {
            // Through the same rolls the ordinary draw path uses: the opening hand IS a
            // draw, so a Slither-enchanted card in it costs what the stream says rather
            // than what it is printed at -- and a Confused player has this hand's costs
            // re-rolled too, because `BeforeCombatStart` puts the power up before the
            // first card is dealt.
            state.Hand.Add(
                CardEffects.ConfuseCostOnDraw(
                    state,
                    CardEffects.RollSlitherCost(state, state.DrawPile[0], rng)
                )
            );
            state.RemoveFromDrawPileAt(0);
        }
    }

    /// <summary>The game's <c>CardPile.MaxCardsInHand</c>.</summary>
    private const int MaxCardsInHand = 10;

    /// <summary>
    /// Port of the game's turn-1 draw-pile reorder (decompiled
    /// <c>MegaCrit.Sts2.Core.Combat/CombatManager.cs</c> ~line 658), applied after the
    /// shuffle and before the opening draw: cards flagged
    /// <c>ShouldStartAtBottomOfDrawPile</c> go to the bottom, then Innate cards (minus
    /// those just sent to the bottom) go to the top, and the draw count is raised to
    /// cover every Innate card, capped at the max hand size.
    /// </summary>
    /// <param name="drawPile">Draw pile, index 0 = top. Reordered in place.</param>
    /// <param name="handDraw">Base number of cards to draw.</param>
    /// <returns>The possibly-increased number of cards to draw.</returns>
    /// <summary>
    /// Queue the cards that play themselves before the player moves.
    /// </summary>
    /// <remarks>
    /// <c>Imbued.AfterAutoPrePlayPhaseEntered</c> auto-plays its card on turn 1, and the
    /// turn-1 reorder has just put it at the BOTTOM of the draw pile — so it plays from
    /// there rather than from hand, which the game's AutoPlay is happy to do. Queued
    /// rather than played, because this method builds a state and does not resolve card
    /// effects; the first <c>CombatEngine.Step</c> drains it before the player's action.
    /// </remarks>
    public static void QueueCombatStartAutoPlays(CombatState state)
    {
        // The HAND as well as the draw pile. Imbued normally sits at the bottom of the
        // pile and plays from there, but the reorder only moves it — it does not stop the
        // opening draw reaching it. A deck of five or fewer draws its own bottom card, so
        // the Imbued one lands in hand and the game plays it from there, leaving the turn
        // to start on four. Scanning only the pile lost it entirely in that case: it never
        // played at all and the player kept a card the game had spent.
        QueueImbuedFrom(state, state.DrawPile, index => state.RemoveFromDrawPileAt(index));
        QueueImbuedFrom(state, state.Hand, index => state.Hand.RemoveAt(index));
    }

    private static void QueueImbuedFrom(
        CombatState state,
        List<CardInstance> pile,
        Action<int> removeAt
    )
    {
        for (int i = pile.Count - 1; i >= 0; i--)
        {
            if (pile[i].Enchantment != Enchantment.Imbued)
            {
                continue;
            }

            // Taken OUT as it is queued: AutoPlayCore files the card into the discard or
            // exhaust pile when it is done, so leaving it where it was would duplicate it.
            state.AutoPlayQueue.Insert(0, pile[i]);
            removeAt(i);
        }
    }

    public static int ApplyTurnOneDrawPileReorder(List<CardInstance> drawPile, int handDraw)
    {
        // The game moves each bottom-sorted card with Remove+Add, so afterwards they
        // sit in the last slots in their original relative order — a stable partition.
        var bottom = new List<CardInstance>();
        var kept = new List<CardInstance>();
        foreach (var card in drawPile)
        {
            (card.StartsAtBottomOfDrawPile() ? bottom : kept).Add(card);
        }

        // Innate cards are then selected from the post-move pile and Except'd against
        // the bottom group, so a card that is both stays at the bottom.
        var innate = new List<CardInstance>();
        var rest = new List<CardInstance>();
        foreach (var card in kept)
        {
            (card.IsInnate() ? innate : rest).Add(card);
        }

        // MoveToTopInternal inserts at index 0, so walking the innate cards in pile
        // order leaves that block reversed relative to where it started.
        innate.Reverse();

        drawPile.Clear();
        drawPile.AddRange(innate);
        drawPile.AddRange(rest);
        drawPile.AddRange(bottom);

        return Math.Min(Math.Max(handDraw, innate.Count), MaxCardsInHand);
    }

    private static int[] StarterDeck() =>
        [
            .. Enumerable.Repeat(StrikeId, 5),
            .. Enumerable.Repeat(DefendId, 4),
            BashId,
            AscendersBaneId,
        ];

    /// <summary>
    /// `EventCombatSynchronizer.InitializeForEvent`: an event with
    /// `EventLayoutType.Combat` builds its encounter's creatures on room entry, and
    /// `CombatState.CreateCreature` rolls each one's HP there with
    /// `SetUniqueMonsterHpValue(..., RunState.Rng.Niche)` -- whether or not the player
    /// ever fights. Returns those max HPs in roster order, drawn on
    /// <paramref name="nicheHpRng" />; nothing else is kept from the throwaway roster.
    /// </summary>
    internal static int[] RollLayoutEnemyHps(
        int encounterId,
        int ascension,
        CountingRandom nicheHpRng,
        ulong? encounterRngSeed
    )
    {
        _currentAscension = ascension;
        _currentNicheHpRng = nicheHpRng;
        _usedNicheHps = [];
        try
        {
            return
            [
                .. CreateEncounter(
                        (ActOneEncounter)encounterId,
                        new Random(0),
                        encounterRngSeed,
                        -1,
                        ascension
                    )
                    .Select(enemy => enemy.MaxHp),
            ];
        }
        finally
        {
            _currentNicheHpRng = null;
            _usedNicheHps = null;
        }
    }

    private static List<EnemyState> CreateEncounter(
        ActOneEncounter encounter,
        Random rng,
        ulong? encounterRngSeed = null,
        int completedCombatRoomsBeforeCurrent = -1,
        int ascension = Ascension.DefaultLevel
    ) =>
        encounter switch
        {
            ActOneEncounter.Cultists =>
            [
                CreateEnemy(KE.CalcifiedCultist, rng, new Intent(IntentType.Buff, 0)),
                CreateEnemy(KE.DampCultist, rng, new Intent(IntentType.Buff, 0)),
            ],

            ActOneEncounter.Chompers =>
            [
                CreateChomper(rng, new Intent(IntentType.Attack, 18)),
                CreateChomper(rng, new Intent(IntentType.Debuff, 3), moveIndex: 1),
            ],

            ActOneEncounter.NibbitsWeak =>
            [
                CreateEnemy(
                    KE.Nibbit,
                    rng,
                    // Nibbit.ButtDamage
                    new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 12)
                    )
                ),
            ],

            ActOneEncounter.SlimesWeak => CreateSlimeEncounter(rng, encounterRngSeed, ascension),

            // ExoskeletonsWeak declares THREE; the normal version is the one with four.
            // The weak roster had been written from the normal model, so act 2's opening
            // fight arrived with an extra creature.
            ActOneEncounter.Exoskeletons =>
            [
                CreateExoskeleton(rng, new Intent(IntentType.Attack, 4)),
                CreateExoskeleton(rng, new Intent(IntentType.Attack, 9)),
                CreateExoskeleton(rng, new Intent(IntentType.Buff, 0)),
            ],

            // ExoskeletonsNormal had no case at all, and it IS in Hive's normal pool --
            // so an act-2 run that drew it did not fight it wrongly, it threw
            // ArgumentOutOfRangeException out of the roster switch.
            ActOneEncounter.ExoskeletonsNormal =>
            [
                CreateExoskeleton(rng, new Intent(IntentType.Attack, 4)),
                CreateExoskeleton(rng, new Intent(IntentType.Attack, 9)),
                CreateExoskeleton(rng, new Intent(IntentType.Buff, 0)),
                CreateExoskeleton(rng, new Intent(IntentType.Attack, 9)),
            ],

            ActOneEncounter.Inklets =>
            [
                CreateInklet(rng, new Intent(IntentType.Attack, 4)),
                CreateInklet(rng, new Intent(IntentType.Attack, 9), moveIndex: 1),
                CreateInklet(rng, new Intent(IntentType.Attack, 4)),
            ],

            ActOneEncounter.TwoTailedRats => CreateTwoTailedRatsEncounter(rng, encounterRngSeed),

            ActOneEncounter.GremlinMerc => [CreateGremlinMerc(rng)],

            ActOneEncounter.FuzzyWurmCrawler =>
            [
                CreateEnemy(
                    KE.FuzzyWurmCrawler,
                    rng,
                    // FuzzyWurmCrawler.AcidGoopDamage
                    new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 4)
                    )
                ),
            ],

            ActOneEncounter.CorpseSlugs => CreateCorpseSlugsEncounter(
                rng,
                completedCombatRoomsBeforeCurrent is >= 0 and < 3,
                encounterRngSeed,
                ascension
            ),

            ActOneEncounter.SludgeSpinner =>
            [
                CreateEnemy(
                    KE.SludgeSpinner,
                    rng,
                    // SludgeSpinner.OilSprayDamage (OIL_SPRAY is attack + debuff)
                    new Intent(
                        IntentType.Debuff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    )
                ),
            ],

            ActOneEncounter.ShrinkerBeetle =>
            [
                CreateEnemy(KE.ShrinkerBeetle, rng, new Intent(IntentType.Debuff, 1)),
            ],

            // SeapunkWeak is one Seapunk; SeapunkNormal is a Calcified Cultist AND a
            // Seapunk. They are two encounters in the game and one entry here, the same
            // way CorpseSlugs is -- so the variant has to come off the weak flag. Without
            // it the fourth normal fight of an Underdocks run was a lone Seapunk where
            // the live run had a cultist beside it, and the sequence check could not see
            // it: verify_run_generation normalises the WEAK/NORMAL suffix away.
            ActOneEncounter.Seapunk => completedCombatRoomsBeforeCurrent is >= 0 and < 3
                ?
                [
                    CreateEnemy(
                        KE.Seapunk,
                        rng,
                        // Seapunk.SeaKickDamage
                        new Intent(
                            IntentType.Attack,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 11)
                        )
                    ),
                ]
                :
                [
                    CreateEnemy(KE.CalcifiedCultist, rng, new Intent(IntentType.Buff, 0)),
                    CreateEnemy(
                        KE.Seapunk,
                        rng,
                        new Intent(
                            IntentType.Attack,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 11)
                        )
                    ),
                ],

            ActOneEncounter.Toadpoles =>
            [
                CreateEnemy(KE.Toadpole, rng, new Intent(IntentType.Buff, 0)),
                CreateEnemy(
                    KE.Toadpole,
                    rng,
                    // Toadpole.WhirlDamage
                    new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                    ),
                    moveIndex: 2
                ),
            ],

            ActOneEncounter.Mawler =>
            [
                CreateEnemy(KE.Mawler, rng, new Intent(IntentType.Attack, 10)),
            ],

            ActOneEncounter.NibbitsNormal =>
            [
                CreateEnemy(
                    KE.Nibbit,
                    rng,
                    // Nibbit.SliceDamage
                    new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6)
                    ),
                    moveIndex: 1
                ),
                CreateEnemy(KE.Nibbit, rng, new Intent(IntentType.Buff, 0), moveIndex: 2),
            ],

            ActOneEncounter.SlimesNormal => CreateSlimesNormalEncounter(rng, encounterRngSeed),

            ActOneEncounter.FlyconidNormal => CreateFlyconidNormalEncounter(rng, encounterRngSeed),

            ActOneEncounter.SnappingJaxfruitNormal =>
            [
                CreateEnemy(KE.SnappingJaxfruit, rng, new Intent(IntentType.Attack, 4)),
                CreateEnemy(KE.Flyconid, rng, new Intent(IntentType.Attack, 8)),
            ],

            ActOneEncounter.CubexConstruct => [CreateCubexConstruct(rng)],

            ActOneEncounter.VineShambler =>
            [
                CreateEnemy(KE.VineShambler, rng, new Intent(IntentType.Attack, 14)),
            ],

            ActOneEncounter.OvergrowthCrawlers =>
            [
                CreateEnemy(KE.ShrinkerBeetle, rng, new Intent(IntentType.Debuff, 1)),
                CreateEnemy(
                    KE.FuzzyWurmCrawler,
                    rng,
                    // FuzzyWurmCrawler.AcidGoopDamage
                    new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 4)
                    )
                ),
            ],

            ActOneEncounter.CultistAndSeapunk =>
            [
                CreateEnemy(KE.CalcifiedCultist, rng, new Intent(IntentType.Buff, 0)),
                CreateEnemy(
                    KE.Seapunk,
                    rng,
                    // Seapunk.SeaKickDamage
                    new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 11)
                    )
                ),
            ],

            ActOneEncounter.FossilStalker => [CreateFossilStalker(rng)],

            // `StartsWithFastPunch` is set in exactly ONE place in the game --
            // `PunchOffEventEncounter` -- and it defaults false everywhere else. So an
            // ordinary Punch Construct opens on READY_MOVE, a DefendIntent for 10 block,
            // and the loop is READY -> FAST_PUNCH -> STRONG_PUNCH.
            //
            // The emulator COIN-FLIPPED it, which is why this only failed on one seed in
            // three: half the time the flip happened to agree. The live capture opened on
            // Defend where the emulator opened on a 10-damage Fast Punch and landed a Frail
            // the game never applied. It also spent a draw the game does not.
            ActOneEncounter.PunchConstruct =>
            [
                CreatePunchConstruct(rng, startsWithFastPunch: false),
            ],

            ActOneEncounter.SewerClam => [CreateSewerClam(rng)],

            ActOneEncounter.HauntedShip =>
            [
                CreateEnemy(KE.HauntedShip, rng, new Intent(IntentType.Debuff, 5)),
            ],

            ActOneEncounter.SlitheringStrangler => CreateSlitheringStranglerEncounter(
                rng,
                encounterRngSeed
            ),

            ActOneEncounter.RubyRaiders => CreateRubyRaiders(rng, encounterRngSeed),

            ActOneEncounter.SkulkingColony => [CreateSkulkingColony(rng)],

            ActOneEncounter.Fogmog => [CreateEnemy(KE.Fogmog, rng, new Intent(IntentType.Buff, 0))],

            ActOneEncounter.LivingFog =>
            [
                CreateEnemy(KE.LivingFog, rng, new Intent(IntentType.Debuff, 9)),
            ],

            ActOneEncounter.BowlbugsWeak => CreateBowlbugsWeakEncounter(rng, encounterRngSeed),

            ActOneEncounter.Bowlbugs => CreateBowlbugsEncounter(rng, encounterRngSeed),

            ActOneEncounter.Tunneler =>
            [
                CreateEnemy(KE.Tunneler, rng, new Intent(IntentType.Attack, 15)),
            ],

            // TunnelerNormal sets `chomper.ScreamFirst = true`, which makes SCREECH the
            // machine's INITIAL state rather than CLAMP -- so this chomper opens on the
            // screech and its whole alternation runs opposite to a plain one. Without the
            // offset it clamped first and stayed inverted for the rest of the fight.
            ActOneEncounter.TunnelerAndChomper =>
            [
                CreateChomper(rng, new Intent(IntentType.Debuff, 3), moveIndex: 1),
                CreateEnemy(KE.Tunneler, rng, new Intent(IntentType.Attack, 13)),
            ],

            ActOneEncounter.ThievingHopper =>
            [
                CreateEnemy(KE.ThievingHopper, rng, new Intent(IntentType.Attack, 19)),
            ],

            // The second Myte's INIT_MOVE branch is SUCK, which is phase 2 of the
            // TOXIC -> BITE -> SUCK cycle -- so it starts two moves ahead, not on the
            // same beat as the first.
            ActOneEncounter.Mytes =>
            [
                CreateEnemy(KE.Myte, rng, new Intent(IntentType.Debuff, 2)),
                CreateEnemy(KE.Myte, rng, new Intent(IntentType.Attack, 6), moveIndex: 2),
            ],

            ActOneEncounter.SlumberingBeetle =>
            [
                CreateEnemy(KE.BowlbugRock, rng, new Intent(IntentType.Attack, 16)),
                CreateBowlbugWorker(KE.BowlbugSilk, rng),
                CreateSlumberingBeetle(rng, ascension),
            ],

            ActOneEncounter.SpinyToad =>
            [
                CreateEnemy(KE.SpinyToad, rng, new Intent(IntentType.Buff, 5)),
            ],

            ActOneEncounter.Ovicopter => [OvicopterInItsSlot(rng)],

            ActOneEncounter.LouseProgenitor =>
            [
                CreateEnemy(KE.LouseProgenitor, rng, new Intent(IntentType.Attack, 10)),
            ],

            ActOneEncounter.HunterKiller =>
            [
                CreateEnemy(KE.HunterKiller, rng, new Intent(IntentType.Debuff, 1)),
            ],

            ActOneEncounter.Axebot =>
            [
                // The machine opens on HAMMER_UPPERCUT: BOOT_UP is index 0 and only a
                // respawn, which builds the bot with a stock override, starts there.
                CreateEnemy(KE.Axebot, rng, new Intent(IntentType.Attack, 14), moveIndex: 1),
            ],

            ActOneEncounter.DevotedSculptor =>
            [
                CreateEnemy(KE.DevotedSculptor, rng, new Intent(IntentType.Buff, 9)),
            ],

            ActOneEncounter.Fabricator =>
            [
                // A placeholder, like every opening intent here: ChooseIntents overwrites
                // it as soon as the roster is built, and it is the branch in SelectIntent
                // that actually rolls.
                FabricatorInItsSlot(rng),
            ],

            ActOneEncounter.FrogKnight => [CreateFrogKnight(rng)],

            ActOneEncounter.GlobeHead =>
            [
                CreateEnemy(KE.GlobeHead, rng, new Intent(IntentType.Attack, 14)),
            ],

            ActOneEncounter.TurretOperator => [CreateLivingShield(rng), CreateTurretOperator(rng)],

            ActOneEncounter.OwlMagistrate =>
            [
                CreateEnemy(KE.OwlMagistrate, rng, new Intent(IntentType.Attack, 17)),
            ],

            ActOneEncounter.ScrollsWeak => CreateScrollsEncounter(rng, 3, encounterRngSeed),

            ActOneEncounter.Scrolls => CreateScrollsEncounter(rng, 4, encounterRngSeed),

            ActOneEncounter.SlimedBerserker =>
            [
                CreateEnemy(KE.SlimedBerserker, rng, new Intent(IntentType.Debuff, 10)),
            ],

            ActOneEncounter.LostAndForgotten =>
            [
                CreateEnemy(KE.TheLost, rng, new Intent(IntentType.Debuff, 2)),
                CreateEnemy(KE.TheForgotten, rng, new Intent(IntentType.Debuff, 2)),
            ],

            ActOneEncounter.Obscura =>
            [
                CreateEnemy(KE.TheObscura, rng, new Intent(IntentType.Buff, 0)),
            ],

            ActOneEncounter.ConstructMenagerie =>
            [
                CreatePunchConstruct(rng, startsWithFastPunch: false),
                CreateCubexConstruct(rng),
                CreateCubexConstruct(rng),
            ],

            ActOneEncounter.DenseVegetation =>
            [
                CreateEnemy(KE.Wriggler, rng, new Intent(IntentType.Attack, 7)),
                CreateEnemy(KE.Wriggler, rng, new Intent(IntentType.Buff, 1), moveIndex: 1),
                CreateEnemy(KE.Wriggler, rng, new Intent(IntentType.Attack, 7)),
                CreateEnemy(KE.Wriggler, rng, new Intent(IntentType.Buff, 1), moveIndex: 1),
            ],

            // Two draws, in roster order, off the encounter's own stream: how much
            // starting HP each construct has lost.
            ActOneEncounter.PunchOff => CreatePunchOffEncounter(rng, encounterRngSeed),

            ActOneEncounter.FakeMerchant =>
            [
                CreateEnemy(KE.FakeMerchant, rng, new Intent(IntentType.Attack, 15)),
            ],

            ActOneEncounter.MysteriousKnight => [CreateMysteriousKnight(rng)],

            ActOneEncounter.BattlewornDummy1 => [CreateBattleFriend(KE.BattleFriendV1, rng)],

            ActOneEncounter.BattlewornDummy2 => [CreateBattleFriend(KE.BattleFriendV2, rng)],

            ActOneEncounter.BattlewornDummy3 => [CreateBattleFriend(KE.BattleFriendV3, rng)],

            ActOneEncounter.BygoneEffigy =>
            [
                CreateEnemy(KE.BygoneEffigy, rng, new Intent(IntentType.Unknown, 0)),
            ],

            // Opens on BEES. AfterAddedToRoom's PersonalHivePower at 1 -- which its
            // PHEROMONE_SPIT reads to decide between growing the hive and taking
            // Strength 2 -- is applied by CreateEnemy's rider block, not here.
            ActOneEncounter.Entomancer =>
            [
                CreateEnemy(KE.Entomancer, rng, new Intent(IntentType.Attack, 3), moveIndex: 1),
            ],

            ActOneEncounter.InfestedPrisms =>
            [
                CreateEnemy(KE.InfestedPrism, rng, new Intent(IntentType.Attack, 17)),
            ],

            ActOneEncounter.PhrogParasite =>
            [
                CreateEnemy(KE.PhrogParasite, rng, new Intent(IntentType.Debuff, 3)),
            ],

            ActOneEncounter.SoulNexus =>
            [
                CreateEnemy(KE.SoulNexus, rng, new Intent(IntentType.Attack, 31)),
            ],

            ActOneEncounter.TerrorEel =>
            [
                CreateEnemy(KE.TerrorEel, rng, new Intent(IntentType.Attack, 18)),
            ],

            ActOneEncounter.Byrdonis =>
            [
                CreateEnemy(KE.Byrdonis, rng, new Intent(IntentType.Attack, 19)),
            ],

            ActOneEncounter.Decimillipede => CreateDecimillipede(rng, encounterRngSeed),

            ActOneEncounter.Knights =>
            [
                // MoveIndex 0: the Flail Knight's machine STARTS on RAM_MOVE, which its
                // own case now expresses. The 2 was an index into the fixed cycle the
                // emulator used to run instead of the branch state.
                CreateEnemy(KE.FlailKnight, rng, new Intent(IntentType.Attack, 15)),
                CreateEnemy(KE.SpectralKnight, rng, new Intent(IntentType.Debuff, 2)),
                CreateEnemy(KE.MagiKnight, rng, new Intent(IntentType.Attack, 7)),
            ],

            ActOneEncounter.MechaKnight => [CreateMechaKnight(rng)],

            ActOneEncounter.PhantasmalGardeners => CreatePhantasmalGardeners(rng),

            ActOneEncounter.Aeonglass => [CreateAeonglass(rng)],

            ActOneEncounter.CeremonialBeast =>
            [
                CreateEnemy(KE.CeremonialBeast, rng, new Intent(IntentType.Buff, 160)),
            ],

            ActOneEncounter.KaiserCrab => CreateKaiserCrab(rng),

            ActOneEncounter.KnowledgeDemon =>
            [
                CreateEnemy(KE.KnowledgeDemon, rng, new Intent(IntentType.Debuff, 0)),
            ],

            ActOneEncounter.LagavulinMatriarch => [CreateLagavulinMatriarch(rng)],

            ActOneEncounter.Queen =>
            [
                CreateEnemy(KE.TorchHeadAmalgam, rng, new Intent(IntentType.Attack, 19)),
                CreateEnemy(KE.Queen, rng, new Intent(IntentType.Debuff, 3)),
            ],

            ActOneEncounter.SoulFysh =>
            [
                CreateEnemy(KE.SoulFysh, rng, new Intent(IntentType.Debuff, 2)),
            ],

            ActOneEncounter.TestSubject => [CreateTestSubject(rng)],

            ActOneEncounter.TheInsatiable =>
            [
                CreateEnemy(KE.TheInsatiable, rng, new Intent(IntentType.Buff, 0)),
            ],

            ActOneEncounter.TheKin =>
            [
                CreateKinFollower(rng, startsWithDance: true),
                CreateKinFollower(rng, startsWithDance: false),
                CreateEnemy(KE.KinPriest, rng, new Intent(IntentType.Attack, 9)),
            ],

            ActOneEncounter.Vantom => [CreateVantom(rng, ascension)],

            ActOneEncounter.WaterfallGiant =>
            [
                CreateEnemy(KE.WaterfallGiant, rng, new Intent(IntentType.Buff, 20)),
            ],

            ActOneEncounter.Architect =>
            [
                CreateEnemy(KE.Architect, rng, new Intent(IntentType.Unknown, 0)),
            ],

            _ => throw new ArgumentOutOfRangeException(nameof(encounter), encounter, null),
        };

    internal static ActOneEncounter SelectFirstCombatEncounter(Random rng)
    {
        var pool = rng.Next(2) == 0 ? OvergrowthWeakEncounters : UnderdocksWeakEncounters;
        return pool[rng.Next(pool.Length)];
    }

    /// <summary>
    /// Every elite encounter in every act, read from the generated per-act pools rather
    /// than named here.
    /// </summary>
    /// <remarks>
    /// This was <c>>= BygoneEffigy and &lt;= WaterfallGiant</c>, which was true when
    /// WaterfallGiant was the last name in the enum and quietly stopped being true when
    /// Architect and SkulkingColony were appended after it: a Skulking Colony elite did
    /// not read as one, so Booming Conch never fired and a live capture opened that fight
    /// with seven cards and four energy where the emulator had five and three. A range
    /// over an enum is a promise about declaration ORDER that nothing enforces.
    ///
    /// <para>
    /// It also swept up every boss, and the game does not: BoomingConch asks for
    /// <c>CurrentRoom.RoomType == RoomType.Elite</c>, and a boss room is RoomType.Boss.
    /// </para>
    ///
    /// <para>
    /// The named list that replaced the range then stopped at act 1 while the run went on
    /// to the Hive and Glory, so their six elites read as normal fights and a live capture
    /// opened Infested Prisms with five cards and three energy instead of seven and four.
    /// A combat room's type IS its encounter's (<c>CombatRoom.RoomType =&gt;
    /// Encounter.RoomType</c>), and <c>EncounterTags.Pool(act, "Elite")</c> is extracted
    /// from each encounter's own <c>RoomType</c> override, so it cannot go stale the way a
    /// hand-kept list did twice.
    /// </para>
    /// </remarks>
    private static readonly HashSet<int> EliteEncounters =
    [
        .. GeneratedData.EncounterTags.Pool(RunConstants.ActOvergrowth, "Elite"),
        .. GeneratedData.EncounterTags.Pool(RunConstants.ActUnderdocks, "Elite"),
        .. GeneratedData.EncounterTags.Pool(RunConstants.ActHive, "Elite"),
        .. GeneratedData.EncounterTags.Pool(RunConstants.ActGlory, "Elite"),
    ];

    private static bool IsEliteEncounter(ActOneEncounter encounter) =>
        EliteEncounters.Contains((int)encounter);

    private static List<EnemyState> CreateSlimeEncounter(
        Random rng,
        ulong? encounterRngSeed = null,
        int ascension = Ascension.DefaultLevel
    )
    {
        // SlimesWeak.GenerateMonsters, call for call. It draws THREE times from the
        // encounter's own Rng, not two:
        //
        //     m1 = Rng.NextItem(smalls);  smalls.Remove(m1);
        //     m2 = Rng.NextItem(smalls);            // one item left — still a draw
        //     add m1; add Rng.NextItem(mediums); add m2;
        //
        // The second draw is forced (one candidate) but it advances the stream, so
        // inferring the second small "for free" — what this used to do — read the
        // MEDIUM slime off the wrong draw and produced the wrong roster.
        //
        // Without a seed there is no encounter stream to speak of; fall back to the
        // combat rng so the direct env still produces something playable, and note that
        // such a roster is NOT comparable to the live game.
        int[] smalls = [KE.LeafSlimeS, KE.TwigSlimeS];
        int[] mediums = [KE.LeafSlimeM, KE.TwigSlimeM];
        int firstSmall;
        int secondSmall;
        int middle;
        if (encounterRngSeed.HasValue)
        {
            var typeRng = EncounterRng.Stream(encounterRngSeed.Value);
            var remaining = smalls.ToList();
            firstSmall = remaining[typeRng.NextInt(0, remaining.Count)];
            remaining.Remove(firstSmall);
            secondSmall = remaining[typeRng.NextInt(0, remaining.Count)];
            middle = mediums[typeRng.NextInt(0, mediums.Length)];
        }
        else
        {
            firstSmall = rng.Next(2) == 0 ? KE.LeafSlimeS : KE.TwigSlimeS;
            middle = rng.Next(2) == 0 ? KE.LeafSlimeM : KE.TwigSlimeM;
            secondSmall = firstSmall == KE.LeafSlimeS ? KE.TwigSlimeS : KE.LeafSlimeS;
        }

        // LeafSlimeS starting intent depends on slot: firstSmall=Attack(0), secondSmall=Debuff(1).
        // TwigSlimeS always starts with Attack(5). These are slot-deterministic, not niche-RNG-based.
        var firstIntent =
            firstSmall == KE.LeafSlimeS
                // LeafSlimeS.TackleDamage
                ? new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3)
                )
                // TwigSlimeS.TackleDamage
                : new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4)
                );
        var secondIntent =
            secondSmall == KE.LeafSlimeS
                ? new Intent(IntentType.Debuff, 1)
                : new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4)
                );
        int secondMoveIndex = secondSmall == KE.LeafSlimeS ? 1 : 0;

        return
        [
            CreateEnemy(firstSmall, rng, firstIntent),
            CreateSlime(middle, rng),
            CreateEnemy(secondSmall, rng, secondIntent, secondMoveIndex),
        ];
    }

    /// <summary>
    /// BowlbugsWeak: a Rock, then ONE worker out of Egg and Nectar.
    /// </summary>
    /// <remarks>
    /// The pick is <c>base.Rng.NextItem(Bugs)</c> — the ENCOUNTER's stream, not the
    /// combat rng, which is the same class of defect <c>EncounterRng</c> was built for
    /// and which act 2 has no capture to catch.
    /// </remarks>
    private static List<EnemyState> CreateBowlbugsWeakEncounter(Random rng, ulong? encounterRngSeed)
    {
        int[] bugs = [KE.BowlbugEgg, KE.BowlbugNectar];
        int worker = encounterRngSeed.HasValue
            ? bugs[EncounterRng.Stream(encounterRngSeed.Value).NextInt(0, bugs.Length)]
            : bugs[rng.Next(bugs.Length)];
        return
        [
            CreateEnemy(KE.BowlbugRock, rng, new Intent(IntentType.Attack, 16)),
            CreateBowlbugWorker(worker, rng),
        ];
    }

    /// <summary>
    /// BowlbugsNormal: a Rock, then TWO workers drawn without replacement.
    /// </summary>
    /// <remarks>
    /// <c>GenerateMonsters</c> loops twice, and each pass re-derives the candidates as
    /// "the workers not already taken" (`_workerValidCounts` caps each at one) and calls
    /// <c>base.Rng.NextItem</c> on THAT list — so it is two draws over three then two,
    /// not a shuffle of three. `OrderBy(_ => rng.Next())` spent a draw per worker off the
    /// wrong stream and distributed them differently besides.
    /// </remarks>
    private static List<EnemyState> CreateBowlbugsEncounter(Random rng, ulong? encounterRngSeed)
    {
        var remaining = new List<int> { KE.BowlbugEgg, KE.BowlbugSilk, KE.BowlbugNectar };
        var typeRng = encounterRngSeed.HasValue
            ? EncounterRng.Stream(encounterRngSeed.Value)
            : null;
        var enemies = new List<EnemyState>
        {
            CreateEnemy(KE.BowlbugRock, rng, new Intent(IntentType.Attack, 16)),
        };
        for (int i = 0; i < 2; i++)
        {
            int index = typeRng is null
                ? rng.Next(remaining.Count)
                : typeRng.NextInt(0, remaining.Count);
            int worker = remaining[index];
            remaining.RemoveAt(index);
            enemies.Add(CreateBowlbugWorker(worker, rng));
        }

        return enemies;
    }

    private static EnemyState CreateBowlbugWorker(int defId, Random rng) =>
        defId switch
        {
            KE.BowlbugEgg => CreateEnemy(defId, rng, new Intent(IntentType.Attack, 8)),
            KE.BowlbugNectar => CreateEnemy(defId, rng, new Intent(IntentType.Attack, 3)),
            KE.BowlbugSilk => CreateEnemy(defId, rng, new Intent(IntentType.Debuff, 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(defId), defId, null),
        };

    /// <summary>
    /// Asleep behind Plating, and it wakes on a COUNTER rather than a turn count.
    /// </summary>
    /// <remarks>
    /// <c>AfterAddedToRoom</c> applies PlatingPower at <c>PlatingAmount</c> — the TOUGH
    /// pair (18, 15), so 18 at A8 — and SlumberPower at a flat 3.
    /// </remarks>
    /// <summary>
    /// A Battleworn Dummy's target: 75, 150 or 300 HP, a move state machine that does
    /// NOTHING, and `AfterAddedToRoom` applying `BattlewornDummyTimeLimitPower` at 3.
    /// </summary>
    /// <remarks>
    /// The time limit is the whole encounter. The dummy never attacks, so the fight is a
    /// damage check against a clock: three of its side turns and it escapes, and the event
    /// pays nothing. Without the power the emulator's dummy would stand there for ever.
    /// </remarks>
    private static EnemyState CreateBattleFriend(int defId, Random rng)
    {
        var enemy = CreateEnemy(defId, rng, new Intent(IntentType.Unknown, 0));
        BuffSystem.Apply(enemy.Buffs, BuffId.BattlewornDummyTimeLimit, 3);
        return enemy;
    }

    private static EnemyState CreateSlumberingBeetle(Random rng, int ascension)
    {
        var enemy = CreateEnemy(KE.SlumberingBeetle, rng, new Intent(IntentType.Unknown, 0));
        int plating = Ascension.Value(ascension, Ascension.ToughEnemies, 18, 15);
        enemy.Block = plating;
        BuffSystem.Apply(enemy.Buffs, BuffId.Plating, plating);
        BuffSystem.Apply(enemy.Buffs, BuffId.Slumber, 3);
        return enemy;
    }

    private static EnemyState CreateFrogKnight(Random rng)
    {
        // The machine opens on TONGUE_LASH and is walked by LastMove, not by MoveIndex,
        // so the old `moveIndex: 2` no longer selects anything.
        var enemy = CreateEnemy(KE.FrogKnight, rng, new Intent(IntentType.Attack, 14));
        // PlatingAmount, which was on the ToughEnemies branch.
        int plating = Ascension.Value(_currentAscension, Ascension.ToughEnemies, 19, 15);
        enemy.Block = plating;
        BuffSystem.Apply(enemy.Buffs, BuffId.Plating, plating);
        return enemy;
    }

    /// <summary>
    /// The Living Shield, which is what keeps the Turret Operator alive.
    /// </summary>
    /// <remarks>
    /// <c>AfterAddedToRoom</c> applies RampartPower at 25 to the SHIELD, and Rampart's
    /// <c>AfterSideTurnStart</c> grants that block to every TurretOperator at the start
    /// of each PLAYER turn — both of which CreateEnemy's rider block and CombatEngine
    /// already do. What was wrong was the turret ALSO handing itself 25 at creation and
    /// another 25 on every reload, so killing the shield cost the player nothing.
    /// </remarks>
    private static EnemyState CreateLivingShield(Random rng) =>
        CreateEnemy(KE.LivingShield, rng, new Intent(IntentType.Attack, 6));

    private static EnemyState CreateTurretOperator(Random rng) =>
        CreateEnemy(KE.TurretOperator, rng, new Intent(IntentType.Attack, 3));

    /// <summary>
    /// Three or four Scrolls of Biting, whose opening moves are one roll and two offsets.
    /// </summary>
    /// <remarks>
    /// <c>StarterMoveIdx = base.Rng.NextInt(3)</c> for the first, then <c>+1</c> and
    /// <c>+2</c> mod 3 — so ONE draw decides every scroll's opening, and taking it off
    /// the combat rng moves the whole fight. The fourth, which only the normal encounter
    /// has, is pinned at 2 and takes no draw.
    /// </remarks>
    private static List<EnemyState> CreateScrollsEncounter(
        Random rng,
        int count,
        ulong? encounterRngSeed
    )
    {
        int firstMove = encounterRngSeed.HasValue
            ? EncounterRng.Stream(encounterRngSeed.Value).NextInt(3)
            : rng.Next(3);
        var enemies = new List<EnemyState>();
        for (int i = 0; i < count; i++)
        {
            // StarterMoveIdx, not a turn count -- the scroll's own case walks the chain
            // from it. The fourth scroll, which only the normal encounter has, is pinned
            // at 2 and takes no draw.
            int starter = count == 4 && i == 3 ? 2 : (firstMove + i) % 3;
            var scroll = CreateEnemy(KE.ScrollOfBiting, rng, ScrollIntent(starter));
            scroll.StarterMove = starter;
            // AfterAddedToRoom gives PaperCutsPower 2 to EVERY scroll. The `i < 3` here
            // left the normal encounter's fourth one without it.
            BuffSystem.Apply(scroll.Buffs, BuffId.PaperCuts, 2);
            enemies.Add(scroll);
        }
        return enemies;
    }

    private static Intent ScrollIntent(int moveIndex) =>
        (moveIndex % 3) switch
        {
            0 => new Intent(IntentType.Attack, 16),
            1 => new Intent(IntentType.Attack, 12),
            _ => new Intent(IntentType.Buff, 2),
        };

    /// <summary>
    /// The Punch Off event's two constructs, each already hurt by a rolled amount.
    /// </summary>
    /// <remarks>
    /// <c>StartingHpReduction = base.Rng.NextInt(2, 10)</c> for each, in roster order —
    /// two draws off the encounter's own stream, which is why they are taken here rather
    /// than inside the per-construct builder.
    /// </remarks>
    private static List<EnemyState> CreatePunchOffEncounter(Random rng, ulong? encounterRngSeed)
    {
        var stream = encounterRngSeed.HasValue ? EncounterRng.Stream(encounterRngSeed.Value) : null;
        return
        [
            CreatePunchOffConstruct(rng, stream, startsWithFastPunch: true),
            CreatePunchOffConstruct(rng, stream, startsWithFastPunch: false),
        ];
    }

    private static EnemyState CreatePunchOffConstruct(
        Random rng,
        Rng.GameRng? stream,
        bool startsWithFastPunch
    )
    {
        var enemy = CreatePunchConstruct(rng, startsWithFastPunch);
        int hpReduction = stream?.NextInt(2, 10) ?? rng.Next(2, 10);
        enemy.Hp = Math.Max(1, enemy.Hp - hpReduction);
        return enemy;
    }

    /// <summary>
    /// `MysteriousKnightEventEncounter.GenerateMonsters` spawns `MysteriousKnight`, its own
    /// ModelId: a `FlailKnight` subclass whose `AfterAddedToRoom` adds Strength 6 and
    /// Plating 6. Building a Flail Knight reported the wrong monster (ABB75XX step0295).
    /// </summary>
    private static EnemyState CreateMysteriousKnight(Random rng)
    {
        // RAM_MOVE is where the machine starts; see the Knights roster above.
        var enemy = CreateEnemy(KE.MysteriousKnight, rng, new Intent(IntentType.Attack, 15));
        enemy.Block = 6;
        BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 6);
        BuffSystem.Apply(enemy.Buffs, BuffId.Plating, 6);
        return enemy;
    }

    /// <summary>
    /// Three segments, whose opening moves are one roll and two offsets.
    /// </summary>
    /// <remarks>
    /// Same shape as the Scrolls: <c>base.Rng.NextInt(3)</c> for the front segment, then
    /// <c>+1</c> and <c>+2</c> mod 3 for the middle and back.
    /// </remarks>
    private static List<EnemyState> CreateDecimillipede(Random rng, ulong? encounterRngSeed)
    {
        int starter = encounterRngSeed.HasValue
            ? EncounterRng.Stream(encounterRngSeed.Value).NextInt(3)
            : rng.Next(3);
        var enemies = new List<EnemyState>(3);
        for (int i = 0; i < 3; i++)
        {
            // StarterMoveIdx numbers the moves 0/1/2 = WRITHE/BULK/CONSTRICT, but the
            // machine walks WRITHE -> CONSTRICT -> BULK. MoveIndex carries the CYCLE
            // POSITION so advancing it is advancing the cycle; this is the one place the
            // two numberings have to be reconciled.
            int starterMove = (starter + i) % 3;
            int moveIndex = starterMove switch
            {
                0 => 0, // WRITHE, cycle position 0
                1 => 2, // BULK, cycle position 2
                _ => 1, // CONSTRICT, cycle position 1
            };
            var enemy = CreateEnemy(
                KE.DecimillipedeSegment,
                rng,
                DecimillipedeIntent(moveIndex),
                moveIndex
            );
            // AfterAddedToRoom applies ReattachPower at 25 to every segment: a dead
            // segment spends a turn as DEAD_MOVE and then heals back, and the fight is
            // only won by emptying all three inside one window.
            BuffSystem.Apply(enemy.Buffs, BuffId.Reattach, RunConstants.DecimillipedeReattachHeal);
            enemies.Add(enemy);
        }
        // CombatRoom creates the full roster before AfterAddedToRoom runs. Each
        // segment checks every teammate, including later segments' unrounded HP.
        foreach (var enemy in enemies)
            MakeDecimillipedeHpEvenAndUnique(enemy, enemies);
        return enemies;
    }

    /// <summary>
    /// A placeholder: ChooseIntents overwrites every opening intent from
    /// <c>EnemyAI.SelectIntent</c> the moment the roster is built. Kept only so a segment
    /// is never constructed with an empty intent.
    /// </summary>
    private static Intent DecimillipedeIntent(int moveIndex) => new(IntentType.Attack, moveIndex);

    private static void MakeDecimillipedeHpEvenAndUnique(
        EnemyState enemy,
        List<EnemyState> existing
    )
    {
        int hp = enemy.MaxHp;
        if (hp % 2 == 1)
        {
            hp++;
        }

        var band = GeneratedData.Enemies.Get(KE.DecimillipedeSegment).HpBand(_currentAscension);
        while (existing.Any(e => !ReferenceEquals(e, enemy) && e.MaxHp == hp))
        {
            hp += 2;
            if (hp > band.Max)
            {
                hp = band.Min;
            }
        }
        enemy.MaxHp = hp;
        enemy.Hp = hp;
    }

    /// <summary>
    /// The two halves of the Kaiser Crab, and the pair of powers that tie them together.
    /// </summary>
    /// <remarks>
    /// Neither was modelled, and between them they are most of the fight.
    /// <c>SurroundedPower</c> goes on the PLAYER and makes an attack from whichever half
    /// is at their back land at 1.5x; the player starts facing Right, so it is the
    /// Crusher's, and it stops when the Rocket dies and the player turns. The emulator
    /// had the 1.5x multiplied into the Crusher's announced damage, which is a number
    /// that can never stop being wrong. <c>CrabRagePower</c> is the other half of it: the
    /// survivor of the pair takes Strength 6 and 99 block, so halving the boss is not
    /// free.
    /// </remarks>
    private static List<EnemyState> CreateKaiserCrab(Random rng)
    {
        var crusher = CreateEnemy(KE.Crusher, rng, new Intent(IntentType.Attack, 12));
        BuffSystem.Apply(crusher.Buffs, BuffId.BackAttackLeft, 1);
        BuffSystem.Apply(crusher.Buffs, BuffId.CrabRage, 1);

        var rocket = CreateEnemy(KE.Rocket, rng, new Intent(IntentType.Attack, 3));
        BuffSystem.Apply(rocket.Buffs, BuffId.BackAttackRight, 1);
        BuffSystem.Apply(rocket.Buffs, BuffId.CrabRage, 1);

        return [crusher, rocket];
    }

    private static EnemyState CreateMechaKnight(Random rng)
    {
        var enemy = CreateEnemy(KE.MechaKnight, rng, new Intent(IntentType.Attack, 30));
        BuffSystem.Apply(enemy.Buffs, BuffId.Artifact, 3);
        return enemy;
    }

    private static List<EnemyState> CreatePhantasmalGardeners(Random rng) =>
        [
            CreateEnemy(KE.PhantasmalGardener, rng, new Intent(IntentType.Attack, 3), moveIndex: 2),
            CreateEnemy(KE.PhantasmalGardener, rng, new Intent(IntentType.Attack, 5)),
            CreateEnemy(KE.PhantasmalGardener, rng, new Intent(IntentType.Attack, 7), moveIndex: 1),
            CreateEnemy(KE.PhantasmalGardener, rng, new Intent(IntentType.Buff, 3), moveIndex: 3),
        ];

    /// <summary>`WitheringPresencePower._baseCardsLeft`, and what it resets to.</summary>
    internal const int WitheringPresenceCards = 6;

    private static EnemyState CreateAeonglass(Random rng)
    {
        var enemy = CreateEnemy(KE.Aeonglass, rng, new Intent(IntentType.Attack, 32));
        BuffSystem.Apply(enemy.Buffs, BuffId.Artifact, 3);
        // `_baseCardsLeft = 6`, and the live capture of the boss shows it at 6 on turn one.
        BuffSystem.Apply(enemy.Buffs, BuffId.WitheringPresence, WitheringPresenceCards);
        return enemy;
    }

    private static EnemyState CreateLagavulinMatriarch(Random rng)
    {
        var enemy = CreateEnemy(KE.LagavulinMatriarch, rng, new Intent(IntentType.Unknown, 0));
        enemy.Block = 12;
        BuffSystem.Apply(enemy.Buffs, BuffId.Plating, 12);
        BuffSystem.Apply(enemy.Buffs, BuffId.Asleep, 3);
        return enemy;
    }

    private static EnemyState CreateTestSubject(Random rng)
    {
        var enemy = CreateEnemy(KE.TestSubject, rng, new Intent(IntentType.Attack, 22));
        BuffSystem.Apply(enemy.Buffs, BuffId.Adaptable, 1);
        // EnrageAmount, which was on the A9 branch.
        BuffSystem.Apply(
            enemy.Buffs,
            BuffId.Enrage,
            Ascension.Value(_currentAscension, Ascension.DeadlyEnemies, 3, 2)
        );
        return enemy;
    }

    private static EnemyState CreateKinFollower(Random rng, bool startsWithDance) =>
        CreateEnemy(
            KE.KinFollower,
            rng,
            startsWithDance ? new Intent(IntentType.Buff, 3) : new Intent(IntentType.Attack, 5),
            startsWithDance ? 2 : 0
        );

    private static EnemyState CreateVantom(Random rng, int ascension)
    {
        var enemy = CreateEnemy(KE.Vantom, rng, new Intent(IntentType.Attack, 8));
        BuffSystem.Apply(
            enemy.Buffs,
            BuffId.Slippery,
            Ascension.Value(ascension, Ascension.ToughEnemies, 9, 8)
        );
        return enemy;
    }

    private static List<EnemyState> CreateSlimesNormalEncounter(
        Random rng,
        ulong? encounterRngSeed = null
    )
    {
        // SlimesNormal.GenerateMonsters: ONE NextBool on the encounter's own stream
        // decides which small slime leads; the two mediums are fixed. Rolling this on
        // the combat rng instead — what this used to do — gets it right half the time.
        bool leafSmallFirst = encounterRngSeed.HasValue
            ? EncounterRng.Stream(encounterRngSeed.Value).NextBool()
            : rng.Next(2) == 0;
        int firstSmall = leafSmallFirst ? KE.LeafSlimeS : KE.TwigSlimeS;
        int secondSmall = leafSmallFirst ? KE.TwigSlimeS : KE.LeafSlimeS;

        return
        [
            CreateSlime(KE.TwigSlimeM, rng),
            CreateSlime(KE.LeafSlimeM, rng),
            CreateSlime(firstSmall, rng),
            CreateSlime(secondSmall, rng),
        ];
    }

    private static EnemyState CreateSlime(int defId, Random rng)
    {
        return defId switch
        {
            KE.LeafSlimeS => CreateEnemy(
                defId,
                rng,
                new Intent(IntentType.Debuff, 1),
                moveIndex: 1
            ),

            KE.TwigSlimeS => CreateEnemy(defId, rng, new Intent(IntentType.Attack, 5)),

            KE.LeafSlimeM => CreateEnemy(
                defId,
                rng,
                new Intent(IntentType.Debuff, 2),
                moveIndex: 1
            ),

            KE.TwigSlimeM => CreateEnemy(
                defId,
                rng,
                new Intent(IntentType.Debuff, 1),
                moveIndex: 1
            ),

            _ => throw new ArgumentOutOfRangeException(nameof(defId), defId, null),
        };
    }

    private static List<EnemyState> CreateTwoTailedRatsEncounter(
        Random rng,
        ulong? encounterRngSeed
    )
    {
        // TwoTailedRatsNormal.GenerateMonsters: Rng.NextInt(3) on the encounter's own
        // stream picks the first rat's StarterMoveIndex, and the other two follow it. On
        // the combat rng instead, all three openings were wrong together.
        int firstMove = encounterRngSeed.HasValue
            ? EncounterRng.Stream(encounterRngSeed.Value).NextInt(0, 3)
            : rng.Next(3);
        // TwoTailedRatsNormal places its three in Slots[2..4] of five, which is what
        // decides where a summoned rat joins the roster.
        return
        [
            CreateTwoTailedRat(rng, firstMove, slot: 2),
            CreateTwoTailedRat(rng, (firstMove + 1) % 3, slot: 3),
            CreateTwoTailedRat(rng, (firstMove + 2) % 3, slot: 4),
        ];
    }

    /// <summary>
    /// FlyconidNormal.GenerateMonsters: one NextItem over the two medium slimes on the
    /// encounter's own stream, then the Flyconid.
    /// </summary>
    private static List<EnemyState> CreateFlyconidNormalEncounter(
        Random rng,
        ulong? encounterRngSeed
    )
    {
        var stream = encounterRngSeed.HasValue ? EncounterRng.Stream(encounterRngSeed.Value) : null;
        int medium = (stream?.NextInt(0, 2) ?? rng.Next(2)) == 0 ? KE.LeafSlimeM : KE.TwigSlimeM;
        return
        [
            CreateSlime(medium, rng),
            CreateEnemy(KE.Flyconid, rng, new Intent(IntentType.Attack, 8)),
        ];
    }

    /// <summary>
    /// SlitheringStranglerNormal.GenerateMonsters: a NextItem over the three
    /// SecondaryEnemyType values, then a slime NextItem for two of the three branches —
    /// all on the encounter's own stream, so the roster size itself depends on it.
    /// </summary>
    private static List<EnemyState> CreateSlitheringStranglerEncounter(
        Random rng,
        ulong? encounterRngSeed
    )
    {
        var stream = encounterRngSeed.HasValue ? EncounterRng.Stream(encounterRngSeed.Value) : null;
        var enemies = new List<EnemyState>();
        switch (stream?.NextInt(0, 3) ?? rng.Next(3))
        {
            case 0:
                enemies.Add(
                    CreateEnemy(KE.SnappingJaxfruit, rng, new Intent(IntentType.Attack, 4))
                );
                break;
            case 1:
                enemies.Add(
                    CreateSlime(
                        (stream?.NextInt(0, 2) ?? rng.Next(2)) == 0 ? KE.LeafSlimeM : KE.TwigSlimeM,
                        rng
                    )
                );
                break;
            default:
                for (int i = 0; i < 2; i++)
                {
                    enemies.Add(
                        CreateSlime(
                            (stream?.NextInt(0, 2) ?? rng.Next(2)) == 0
                                ? KE.LeafSlimeS
                                : KE.TwigSlimeS,
                            rng
                        )
                    );
                }

                break;
        }
        enemies.Add(CreateEnemy(KE.SlitheringStrangler, rng, new Intent(IntentType.Debuff, 3)));
        return enemies;
    }

    /// <summary>
    /// RubyRaidersNormal.GenerateMonsters: three NextItem draws on the encounter's own
    /// stream over the raiders not yet taken — every type is capped at one, so the list
    /// shrinks 5, 4, 3 — in the order the count dictionary declares them. The emulator
    /// built a fixed Tracker/Assassin/Brute trio, which is one of the sixty possibilities.
    /// </summary>
    private static List<EnemyState> CreateRubyRaiders(Random rng, ulong? encounterRngSeed)
    {
        int[] pool =
        [
            KE.AxeRubyRaider,
            KE.AssassinRubyRaider,
            KE.BruteRubyRaider,
            KE.CrossbowRubyRaider,
            KE.TrackerRubyRaider,
        ];

        var stream = encounterRngSeed.HasValue ? EncounterRng.Stream(encounterRngSeed.Value) : null;
        var remaining = pool.ToList();
        var raiders = new List<EnemyState>();
        for (int i = 0; i < 3; i++)
        {
            int index = stream?.NextInt(0, remaining.Count) ?? rng.Next(remaining.Count);
            int defId = remaining[index];
            remaining.RemoveAt(index);
            raiders.Add(CreateEnemy(defId, rng, new Intent(IntentType.Unknown, 0)));
        }

        return raiders;
    }

    private static EnemyState CreateTwoTailedRat(Random rng, int moveIndex, int slot = -1)
    {
        var enemy = CreateEnemy(KE.TwoTailedRat, rng, RatIntent(moveIndex), moveIndex);
        enemy.Slot = slot;
        BuffSystem.Apply(enemy.Buffs, BuffId.SummonCooldown, 2);
        return enemy;
    }

    private static Intent RatIntent(int moveIndex) =>
        (moveIndex % 3) switch
        {
            0 => new Intent(IntentType.Attack, 9),
            1 => new Intent(IntentType.Attack, 7),
            _ => new Intent(IntentType.Debuff, 1),
        };

    private static List<EnemyState> CreateCorpseSlugsEncounter(
        Random rng,
        bool weak = false,
        ulong? encounterRngSeed = null,
        int ascension = Ascension.DefaultLevel
    )
    {
        // CorpseSlug.EnsureCorpseSlugsStartWithDifferentMoves: the encounter rolls ONE
        // number and then deals consecutive starting moves to the slugs.
        //
        //     int num = rng.NextInt(3);
        //     foreach (slug) { slug.StarterMoveIdx = num % 3; num++; }
        //
        // The hardcoded (2, 0) this used to return is that sequence for a roll of 2 —
        // right one time in three, which is exactly how often the sweep saw it pass.
        int count = weak ? 2 : 3;
        int start = encounterRngSeed.HasValue
            ? EncounterRng.Stream(encounterRngSeed.Value).NextInt(3)
            : 2;

        var slugs = new List<EnemyState>(count);
        for (int i = 0; i < count; i++)
        {
            slugs.Add(CreateCorpseSlug(rng, (start + i) % 3, ascension: ascension));
        }

        return slugs;
    }

    private static EnemyState CreateCorpseSlug(
        Random rng,
        int moveIndex,
        int? fixedHp = null,
        // Zero means "whatever the combat being built runs at". CreateEncounter has a
        // hundred-odd CreateEnemy calls and threading the level through every one of them
        // would be a hundred chances to miss one, so it rides the same ambient field the
        // Niche HP stream already uses.
        int ascension = 0
    ) => CreateCorpseSlugEnemy(rng, moveIndex, fixedHp, ascension);

    private static EnemyState CreateCorpseSlugEnemy(
        Random rng,
        int moveIndex,
        int? fixedHp = null,
        // Zero means "whatever the combat being built runs at". CreateEncounter has a
        // hundred-odd CreateEnemy calls and threading the level through every one of them
        // would be a hundred chances to miss one, so it rides the same ambient field the
        // Niche HP stream already uses.
        int ascension = 0
    )
    {
        var enemy = CreateEnemy(
            KE.CorpseSlug,
            rng,
            CorpseSlugIntent(moveIndex, ascension),
            moveIndex,
            fixedHp
        );
        // RavenousStr is GetValueIfAscension(DeadlyEnemies, 5, 4): the 5 is the A9
        // branch, taken here as a bare literal. At A8 the slug gains 4 per dead ally,
        // and a live capture announces "7x2" -- (3 + 4) twice -- where a 5 gives 8x2.
        BuffSystem.Apply(
            enemy.Buffs,
            BuffId.Ravenous,
            Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4)
        );
        return enemy;
    }

    // Move order is the game's MoveState declaration order in CorpseSlug.cs:
    // 0 WHIP_SLAP (MultiAttackIntent 3 x 2), 1 GLOMP (SingleAttackIntent), 2 GOOP (debuff).
    private static Intent CorpseSlugIntent(int moveIndex, int ascension = Ascension.DefaultLevel) =>
        (moveIndex % 3) switch
        {
            // WhipSlapDamage * WhipSlapRepeat; the live readout shows "3x2" and the
            // comparison comes down on total damage.
            0 => new Intent(IntentType.Attack, 3 * 2),
            1 => new Intent(
                IntentType.Attack,
                Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
            ),
            _ => new Intent(IntentType.Debuff, 2),
        };

    public static EnemyState CreateEnemy(
        int defId,
        Random rng,
        Intent startingIntent,
        int moveIndex = 0,
        int? fixedHp = null,
        // Zero means "whatever the combat being built runs at". CreateEncounter has a
        // hundred-odd CreateEnemy calls and threading the level through every one of them
        // would be a hundred chances to miss one, so it rides the same ambient field the
        // Niche HP stream already uses.
        int ascension = 0
    )
    {
        var def = GeneratedData.Enemies.Get(defId);
        var band = def.HpBand(ascension > 0 ? ascension : _currentAscension);
        // Use the dedicated niche HP RNG when available, matching SetUniqueMonsterHpValue
        // which calls rng.NextItem(remaining_set) to avoid duplicate HP values across
        // creatures on the same side.
        int hp;
        if (_presetHps is { Count: > 0 })
        {
            // Rolled when the event built its layout; the fight reuses that creature.
            hp = _presetHps.Dequeue();
            _usedNicheHps?.Add(hp);
        }
        else if (fixedHp.HasValue)
        {
            hp = fixedHp.Value;
            if (_currentNicheHpRng != null)
            {
                _currentNicheHpRng.Next(0, band.Max - band.Min + 1);
            }
        }
        else if (_currentNicheHpRng != null)
        {
            // Build remaining set = [minHp..maxHp] minus already-used HP values.
            var range = Enumerable.Range(band.Min, band.Max - band.Min + 1).ToHashSet();
            if (_usedNicheHps != null)
            {
                range.ExceptWith(_usedNicheHps);
            }

            if (range.Count == 0)
            {
                // Fallback: full range (matches game behaviour when all values are taken).
                hp = _currentNicheHpRng.Next(band.Min, band.Max + 1);
            }
            else
            {
                // NextItem equivalent: NextInt(0, count) → ElementAt(index).
                int index = _currentNicheHpRng.Next(0, range.Count);
                hp = range.ElementAt(index);
            }
            _usedNicheHps?.Add(hp);
        }
        else
        {
            hp = rng.Next(band.Min, band.Max + 1);
        }
        var enemy = new EnemyState
        {
            DefId = defId,
            Hp = hp,
            MaxHp = hp,
            Block = 0,
            CurrentIntent = startingIntent,
            Buffs = [],
            MoveIndex = moveIndex,
        };
        ApplyIntrinsicEnemyPowers(enemy);
        return enemy;
    }

    /// <summary>
    /// `FabricatorNormal.Slots` is `[bot1, bot2, fabricator, bot3, bot4]` -- the Fabricator
    /// stands in the MIDDLE with two bots in front of it and two behind. It takes slot 2,
    /// and `SpawnBot` fills the rest through `GetNextSlot`, which is the first FREE slot
    /// and therefore refills one a dead bot vacated.
    /// </summary>
    private static EnemyState FabricatorInItsSlot(Random rng)
    {
        // A placeholder intent, like every opening intent here: ChooseIntents overwrites
        // it as soon as the roster is built, and it is the branch in SelectIntent that
        // actually rolls.
        var fabricator = CreateEnemy(KE.Fabricator, rng, new Intent(IntentType.Buff, 0));
        fabricator.Slot = FabricatorOwnSlot;
        return fabricator;
    }

    /// <summary>
    /// `OvicopterNormal.Slots` is `[egg1, egg2, egg3, egg4, egg5, ovicopter]`: the Ovicopter
    /// stands in the last slot and LAY_EGGS fills the five in front of it.
    /// </summary>
    private static EnemyState OvicopterInItsSlot(Random rng)
    {
        var ovicopter = CreateEnemy(KE.Ovicopter, rng, new Intent(IntentType.Buff, 0));
        ovicopter.Slot = OvicopterOwnSlot;
        return ovicopter;
    }

    /// <summary>The Ovicopter's own index in `OvicopterNormal.Slots`.</summary>
    public const int OvicopterOwnSlot = 5;

    /// <summary>The Fabricator's own index in `FabricatorNormal.Slots`.</summary>
    public const int FabricatorOwnSlot = 2;

    /// <summary>How many slots that encounter has in all.</summary>
    public const int FabricatorSlotCount = 5;

    private static void ApplyIntrinsicEnemyPowers(EnemyState enemy)
    {
        if (enemy.DefId == KE.TheLost)
            BuffSystem.Apply(enemy.Buffs, BuffId.PossessStrength, 1);
        if (enemy.DefId == KE.TheForgotten)
            BuffSystem.Apply(enemy.Buffs, BuffId.PossessSpeed, 1);

        if (enemy.DefId == KE.ToughEgg)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Hatch, 1);
        }

        if (enemy.DefId == KE.Axebot)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Stock, 2);
        }

        // `ThievingHopper.AfterAddedToRoom` applies EscapeArtistPower at 5 -- the timer
        // that counts down to its ESCAPE. Purely visual in the game; carried here so the
        // observation and the sweep see what the live creature has.
        if (enemy.DefId == KE.ThievingHopper)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.EscapeArtist, 5);
        }

        // `BowlbugRock.AfterAddedToRoom` applies ImbalancedPower at 1. The Rock's
        // off-balance TURN is `OffBalance`; this is the marker that says it has the rule.
        if (enemy.DefId == KE.BowlbugRock)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Imbalanced, 1);
        }

        // `TorchHeadAmalgam.AfterAddedToRoom` applies MinionPower to ITSELF -- it is a
        // minion from the moment the Queen's fight starts, not one she summons. So killing
        // it pays no fatal bonus: Feed, Hand of Greed and The Hunt all gate on
        // `ShouldOwnerDeathTriggerFatal`, which MinionPower answers false. E459 was this
        // same marker missing on an illusion.
        // `KinFollower.AfterAddedToRoom` applies the same marker. Both followers carry it
        // from the moment the fight is built, so killing one pays no fatal bonus -- the Kin
        // is a three-creature fight where only the PRIEST is worth a Feed.
        if (enemy.DefId is KE.TorchHeadAmalgam or KE.KinFollower)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Minion, 1);
        }

        if (enemy.DefId == KE.TerrorEel)
        {
            // TerrorEel.AfterAddedToRoom applies ShriekPower(ShriekAmount) — 75 at A8,
            // where ToughEnemies is live. It is the HP at or below which an unblocked hit
            // stuns it into TERROR.
            BuffSystem.Apply(
                enemy.Buffs,
                BuffId.Shriek,
                Ascension.Value(_currentAscension, Ascension.ToughEnemies, 75, 70)
            );
        }

        if (enemy.DefId == KE.BygoneEffigy)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Slow, 1);
        }

        if (enemy.DefId == KE.Byrdonis)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Territorial, 1);
        }

        if (enemy.DefId == KE.Entomancer)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.PersonalHive, 1);
        }

        if (enemy.DefId == KE.GlobeHead)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Galvanic, 6);
        }

        if (enemy.DefId == KE.InfestedPrism)
        {
            // InfestedPrism.AfterAddedToRoom applies VitalSparkPower(VitalSparkAmount).
            // It is the Skill-card twin of the Globe Head's Galvanic above: where Galvanic
            // damages the player for playing a Power, Vital Spark taints them for playing
            // a Skill, and a tainted player takes that much more from every powered attack
            // for the rest of the round.
            BuffSystem.Apply(
                enemy.Buffs,
                BuffId.VitalSpark,
                Ascension.Value(_currentAscension, Ascension.DeadlyEnemies, 3, 2)
            );
        }

        if (enemy.DefId == KE.LivingShield)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Rampart, 25);
        }

        if (enemy.DefId == KE.LouseProgenitor)
        {
            BuffSystem.Apply(
                enemy.Buffs,
                BuffId.CurlUp,
                Ascension.Value(_currentAscension, Ascension.ToughEnemies, 18, 14)
            );
        }

        if (enemy.DefId == KE.PhantasmalGardener)
        {
            // PhantasmalGardener.AfterAddedToRoom applies SkittishPower at SkittishAmount.
            BuffSystem.Apply(
                enemy.Buffs,
                BuffId.Skittish,
                Ascension.Value(_currentAscension, Ascension.ToughEnemies, 7, 6)
            );
        }

        if (enemy.DefId == KE.PhrogParasite)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Infested, 4);
        }

        if (enemy.DefId == KE.Zapbot)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.HighVoltage, 2);
        }
    }

    private static EnemyState CreateFossilStalker(Random rng)
    {
        var enemy = CreateEnemy(
            KE.FossilStalker,
            rng,
            new Intent(IntentType.Attack, 14),
            moveIndex: 1
        );
        BuffSystem.Apply(enemy.Buffs, BuffId.Suck, 3);
        return enemy;
    }

    private static EnemyState CreateChomper(Random rng, Intent startingIntent, int moveIndex = 0)
    {
        var enemy = CreateEnemy(KE.Chomper, rng, startingIntent, moveIndex);
        BuffSystem.Apply(enemy.Buffs, BuffId.Artifact, 2);
        return enemy;
    }

    private static EnemyState CreateInklet(Random rng, Intent startingIntent, int moveIndex = 0)
    {
        var enemy = CreateEnemy(KE.Inklet, rng, startingIntent, moveIndex);
        BuffSystem.Apply(enemy.Buffs, BuffId.Slippery, 1);
        return enemy;
    }

    private static EnemyState CreateGremlinMerc(Random rng)
    {
        var enemy = CreateEnemy(KE.GremlinMerc, rng, new Intent(IntentType.Attack, 16));
        BuffSystem.Apply(enemy.Buffs, BuffId.Surprise, 1);
        // GremlinMerc.AfterAddedToRoom applies this power before the first turn.
        BuffSystem.Apply(enemy.Buffs, BuffId.Thievery, 20);
        return enemy;
    }

    private static EnemyState CreateCubexConstruct(Random rng)
    {
        var enemy = CreateEnemy(KE.CubexConstruct, rng, new Intent(IntentType.Buff, 0));
        BuffSystem.Apply(enemy.Buffs, BuffId.Artifact, 1);
        return enemy;
    }

    private static EnemyState CreatePunchConstruct(Random rng, bool startsWithFastPunch)
    {
        var enemy = CreateEnemy(
            KE.PunchConstruct,
            rng,
            startsWithFastPunch
                ? new Intent(IntentType.Attack, 12)
                : new Intent(IntentType.Defend, 10),
            startsWithFastPunch ? 1 : 0
        );
        BuffSystem.Apply(enemy.Buffs, BuffId.Artifact, 1);
        return enemy;
    }

    private static EnemyState CreateSewerClam(Random rng)
    {
        var enemy = CreateEnemy(KE.SewerClam, rng, new Intent(IntentType.Attack, 11), moveIndex: 1);
        int plating = Ascension.Value(_currentAscension, Ascension.ToughEnemies, 9, 8);
        enemy.Block = plating;
        BuffSystem.Apply(enemy.Buffs, BuffId.Plating, plating);
        return enemy;
    }

    private static EnemyState CreateExoskeleton(
        Random rng,
        Intent startingIntent,
        int moveIndex = 0
    )
    {
        var enemy = CreateEnemy(KE.Exoskeleton, rng, startingIntent, moveIndex);
        BuffSystem.Apply(enemy.Buffs, BuffId.HardToKill, 9);
        return enemy;
    }

    private static EnemyState CreateSkulkingColony(Random rng)
    {
        var enemy = CreateEnemy(KE.SkulkingColony, rng, new Intent(IntentType.Attack, 16));
        BuffSystem.Apply(enemy.Buffs, BuffId.HardenedShell, 20);
        return enemy;
    }
}
