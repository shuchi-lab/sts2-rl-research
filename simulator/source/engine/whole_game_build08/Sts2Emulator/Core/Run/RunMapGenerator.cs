using Sts2Emulator.Core.Rng;

namespace Sts2Emulator.Core.Run;

public static class RunMapGenerator
{
    /// <summary>
    /// <c>UnlockState.SharedAncients</c> — ancients belonging to no act, dealt out to the
    /// acts after the first. The game's own comment: "we only have 1 right now. That's
    /// Darv."
    /// </summary>
    /// <remarks>
    /// A one-item list is why the prefix ahead of the acts measured as exactly two draws:
    /// <c>UnstableShuffle</c> over one element costs nothing, leaving the two subset-size
    /// draws. That made "the shared pool is empty" look right, and it is not — a live
    /// capture (`3PFLW9XC5D`) opens act 2 on DARV, which is in no act's own list.
    /// </remarks>
    private static readonly string[] SharedAncientPool = [RunConstants.AncientDarv];

    /// <summary>
    /// RunManager.InitializeNewRun: the shared bag from SharedRelicPool as-is, then the
    /// player's from the shared pool plus the character's, filtered to the four reward
    /// rarities. Both shuffle off UpFront, shared first -- the order and the split are
    /// what put the rest of the run's draws where they are.
    /// </summary>
    private static void PopulateRelicGrabBags(RunState state, GameRng upFront)
    {
        state.SharedRelicBag = new RelicGrabBag(refreshAllowed: true);
        state.SharedRelicBag.Populate(
            GeneratedData
                .RelicPools.Shared.ToArray()
                .Where(id => state.Progression.IsRelicUnlocked(GeneratedData.Relics.Get(id).Entry))
                .ToArray(),
            upFront,
            filterRarities: false
        );

        state.RelicBag = new RelicGrabBag();
        state.RelicBag.Populate(
            (
                (int[])
                    [
                        .. GeneratedData.RelicPools.Shared.ToArray(),
                        .. CharacterCatalog.RelicPool(state.Character).ToArray(),
                    ]
            )
                .Where(id => state.Progression.IsRelicUnlocked(GeneratedData.Relics.Get(id).Entry))
                .ToArray(),
            upFront,
            filterRarities: true
        );
    }

    public static void SelectActAndGenerateRooms(RunState state)
    {
        // Act 1 is picked by StartRunLobby.BeginRunLocally from the *unlocked* acts for
        // index 0, via rng.NextItem on a dedicated "act_selection" stream — not a coin
        // flip on the raw seed, which is what this used to do.
        //
        // ActModel.GetRandomList forces an unlocked, undiscovered alternate without
        // drawing. Underdocks is the only alternate in v0.111.0. A supplied progress
        // save carries discovery history; callers without it use mature-profile defaults.
        var actRng = new GameRng(state.Rng.Seed, "act_selection");
        int[] acts =
        [
            .. RunConstants.ActCandidatesByIndex.Select(candidates =>
            {
                int[] unlocked = candidates
                    .Where(act =>
                        act != RunConstants.ActUnderdocks
                        || state.Progression.IsEpochRevealed("UNDERDOCKS_EPOCH")
                    )
                    .ToArray();
                if (
                    unlocked.Contains(RunConstants.ActUnderdocks)
                    && !state.Progression.IsActDiscovered("ACT.UNDERDOCKS")
                )
                    return RunConstants.ActUnderdocks;
                return unlocked[actRng.NextInt(unlocked.Length)];
            }),
        ];
        // RunManager.GenerateRooms drives one UpFront stream in a fixed order:
        // it shuffles the shared ancients, draws one subset size per act after the
        // first, and only then calls ActModel.GenerateRooms for each act. That
        // prefix is SharedAncientPrefixDraws draws; it depends on how many shared
        // ancients the profile has unlocked, so it is pinned to the mature profile
        // the differential captures are taken on rather than derived.
        //
        // ActModel.GenerateRooms then shuffles the act's event pool -- one draw
        // short of the pool size -- before rolling that act's encounters. That is
        // the whole reason the prefix ahead of the encounters is act-dependent:
        // 232 + 30 = 262 for Overgrowth's 31 events, 232 + 27 = 259 for
        // Underdocks' 28.
        var upFront = state.Rng.UpFront;
        PopulateRelicGrabBags(state, upFront);

        // RunManager.GenerateRooms shuffles the shared ancients and then deals each act
        // AFTER the first a slice off the front: `count = NextInt(list.Count + 1)`, take
        // that many, and remove them from what is left for the next act. With one shared
        // ancient that is two draws, which is what this used to spend as a constant --
        // but the VALUES matter, because taking it or not decides whether an act can open
        // on Darv at all.
        var remainingShared = SharedAncientPool
            .Where(state.Progression.IsSharedAncientUnlocked)
            .ToList();
        var sharedSubsets = new List<string[]>();
        for (int i = 1; i < RunConstants.ActCandidatesByIndex.Length; i++)
        {
            int take = upFront.NextInt(remainingShared.Count + 1);
            sharedSubsets.Add([.. remainingShared.Take(take)]);
            remainingShared = [.. remainingShared.Skip(take)];
        }

        // RunManager.GenerateRooms walks EVERY act, in index order, off this one stream.
        // The emulator generated only the first and stopped, which left its UpFront two
        // acts' worth of draws behind the game's for the rest of the run -- invisible so
        // far because nothing a committed trace does reads UpFront after generation, and
        // wrong the moment anything did.
        state.Acts.Clear();
        state.CurrentActIndex = 0;
        for (int index = 0; index < acts.Length; index++)
        {
            // The game's loop is `Acts.Skip(1)`, so act 1 is dealt no shared ancients.
            string[] subset = index == 0 ? [] : sharedSubsets[index - 1];
            state.Acts.Add(GenerateRoomsForAct(acts[index], upFront, subset, state.Progression));
        }

        state.EventSequenceIndex = 0;
    }

    /// <summary>
    /// <c>ActModel.GenerateRooms</c>, which is the same six steps for every act: shuffle
    /// the event pool, fill the weak encounters, fill the regular ones up to the act's
    /// room count, fill fifteen elites, take a boss, take an ancient.
    /// </summary>
    /// <remarks>
    /// The ANCIENT draw at the end is new. The emulator stopped at the boss, so every act
    /// it generated left the stream one draw short of where the game leaves it. Which
    /// ancient it picks is not modelled yet -- act 1's is Neow and the later acts' are
    /// rolled from the act's own three plus whatever shared ones it was dealt -- but the
    /// DRAW has to happen either way, and that is what keeps everything after it aligned.
    /// </remarks>
    private static ActRooms GenerateRoomsForAct(
        int act,
        GameRng upFront,
        string[] sharedSubset,
        ProgressionContext progression
    )
    {
        var (weakCount, roomCount) = RunConstants.ActRoomCounts(act);
        int[] weakPool = WeakPoolFor(act);
        int[] normalPool = NormalPoolFor(act);
        int[] elitePool = ElitePoolFor(act);
        int[] bossPool = BossPoolFor(act);

        int[] events = GenerateEventSequence(act, upFront);

        var normalSequence = new List<int>();
        var weakBag = weakPool.ToList();
        int? last = null;
        for (int i = 0; i < weakCount; i++)
        {
            if (weakBag.Count == 0)
            {
                weakBag = weakPool.ToList();
            }

            int enc = GrabWithoutRepeatingTags(weakBag, last, upFront);
            normalSequence.Add(enc);
            last = enc;
        }

        var normalBag = new List<int>();
        for (int i = weakCount; i < roomCount; i++)
        {
            if (normalBag.Count == 0)
            {
                normalBag = normalPool.ToList();
            }

            int enc = GrabWithoutRepeatingTags(normalBag, last, upFront);
            normalSequence.Add(enc);
            last = enc;
        }

        var eliteSequence = new List<int>();
        var eliteBag = new List<int>();
        // Elites go through the same AddWithoutRepeatingTags path as normals, and
        // track their own "last" -- the game passes _rooms.eliteEncounters, so the
        // no-repeat rule looks at the previous *elite*, not the previous normal.
        int? lastElite = null;
        for (int k = 0; k < RunConstants.EliteSequenceLength; k++)
        {
            if (eliteBag.Count == 0)
            {
                eliteBag = elitePool.ToList();
            }

            int enc = GrabWithoutRepeatingTags(eliteBag, lastElite, upFront);
            eliteSequence.Add(enc);
            lastElite = enc;
        }

        int boss = bossPool[(int)(upFront.NextDouble() * bossPool.Length)];

        // `_rooms.Ancient = rng.NextItem(GetUnlockedAncients().Concat(sharedSubset))`.
        // The subset is empty on this profile -- SharedAncients is empty, so the two
        // subset-size draws are NextInt(1) and both come back zero -- which leaves the
        // act's own list. This draw used to be a bare NextDouble standing in for "spend a
        // value"; it is the real pick now, so the run knows WHICH ancient each act opens
        // on. Both act-1 regions declare Neow alone, so act 1 is a one-item pick that
        // still costs its draw.
        // `Hive.GetUnlockedAncients` drops Orobas unless OrobasEpoch is revealed.
        string[] ancients =
        [
            .. RunConstants
                .AncientsFor(act)
                .Where(ancient =>
                    ancient != RunConstants.AncientOrobas
                    || progression.IsEpochRevealed("OROBAS_EPOCH")
                ),
            .. sharedSubset,
        ];
        // NextItem spelled out: it is `items[(int)(NextDouble() * count)]`, and having the
        // raw roll is what lets a mismatch be solved rather than guessed at.
        double ancientRoll = upFront.NextDouble();
        string ancient = ancients[(int)(ancientRoll * ancients.Length)];

        return new ActRooms(
            act,
            events,
            normalSequence.ToArray(),
            eliteSequence.ToArray(),
            boss,
            ancient
        );
    }

    // Glory reads the GENERATED pool; act 1 and Hive still read the constants they were
    // verified with. The generator reproduces those sixteen pools exactly -- which is
    // the whole argument that Glory's four are right -- and a test says so, so the two
    // sources cannot drift apart in silence. Act 3 was reusing Hive's encounters
    // outright until now.
    private static int[] WeakPoolFor(int act) =>
        act switch
        {
            RunConstants.ActUnderdocks => RunConstants.UnderdocksWeakEncounters.ToArray(),
            RunConstants.ActHive => RunConstants.HiveWeakEncounters.ToArray(),
            RunConstants.ActGlory => GeneratedData.EncounterTags.Pool(
                RunConstants.ActGlory,
                "Weak"
            ),
            _ => RunConstants.OvergrowthWeakEncounters.ToArray(),
        };

    private static int[] NormalPoolFor(int act) =>
        act switch
        {
            RunConstants.ActUnderdocks => RunConstants.UnderdocksNormalEncounters.ToArray(),
            RunConstants.ActHive => RunConstants.HiveNormalEncounters.ToArray(),
            RunConstants.ActGlory => GeneratedData.EncounterTags.Pool(
                RunConstants.ActGlory,
                "Normal"
            ),
            _ => RunConstants.OvergrowthNormalEncounters.ToArray(),
        };

    private static int[] ElitePoolFor(int act) =>
        act switch
        {
            RunConstants.ActUnderdocks => RunConstants.UnderdocksEliteEncounters.ToArray(),
            RunConstants.ActHive => RunConstants.HiveEliteEncounters.ToArray(),
            RunConstants.ActGlory => GeneratedData.EncounterTags.Pool(
                RunConstants.ActGlory,
                "Elite"
            ),
            _ => RunConstants.OvergrowthEliteEncounters.ToArray(),
        };

    private static int[] BossPoolFor(int act) =>
        act switch
        {
            RunConstants.ActUnderdocks => RunConstants.UnderdocksBossEncounters.ToArray(),
            RunConstants.ActHive => RunConstants.HiveBossEncounters.ToArray(),
            RunConstants.ActGlory => GeneratedData.EncounterTags.Pool(
                RunConstants.ActGlory,
                "Boss"
            ),
            _ => RunConstants.OvergrowthBossEncounters.ToArray(),
        };

    /// <summary>
    /// The events a run can draw, in the order ActModel.GenerateRooms builds them:
    /// the act's own AllEvents, then ModelDb.AllSharedEvents, then one UnstableShuffle.
    ///
    /// The order before the shuffle is the whole ballgame. UnstableShuffle is documented
    /// as order-dependent — two lists holding the same events in different orders shuffle
    /// to different results from the same stream — and this list used to be one
    /// alphabetical run of act and shared events mixed together, three of them missing
    /// entirely. It consumed a plausible number of draws and produced the wrong events
    /// all run, which is what the deleted TryEnterRetainedInstant5Event hardcode was
    /// papering over.
    /// </summary>
    /// <summary>
    /// <c>ModelDb.AllSharedEvents</c>: the block every act appends to its own list before
    /// the one shuffle. Eighteen of them, and the ORDER matters as much as the membership
    /// — <c>UnstableShuffle</c> is order-dependent, so the same events in a different
    /// sequence shuffle to a different run.
    /// </summary>
    private static readonly int[] SharedEventPool =
    [
        RunConstants.EventBrainLeech,
        RunConstants.EventCrystalSphere,
        RunConstants.EventDollRoom,
        RunConstants.EventFakeMerchant,
        RunConstants.EventPotionCourier,
        RunConstants.EventRanwidTheElder,
        RunConstants.EventRelicTrader,
        RunConstants.EventRoomFullOfCheese,
        RunConstants.EventSelfHelpBook,
        RunConstants.EventSlipperyBridge,
        RunConstants.EventStoneOfAllTime,
        RunConstants.EventSymbiote,
        RunConstants.EventTeaMaster,
        RunConstants.EventTheFutureOfPotions,
        RunConstants.EventTheLegendsWereTrue,
        RunConstants.EventThisOrThat,
        RunConstants.EventWarHistorianRepy,
        RunConstants.EventWelcomeToWongos,
    ];

    private static int[] GenerateEventSequence(int act, GameRng rng)
    {
        if (act == RunConstants.ActHive)
        {
            // Hive.AllEvents in its own declaration order, then the shared block every
            // act gets: ActModel.GenerateRooms is AllEvents.Concat(AllSharedEvents).
            int[] hivePool =
            [
                RunConstants.EventAmalgamator,
                RunConstants.EventBugslayer,
                RunConstants.EventColorfulPhilosophers,
                RunConstants.EventColossalFlower,
                RunConstants.EventFieldOfManSizedHoles,
                RunConstants.EventInfestedAutomaton,
                RunConstants.EventLostWisp,
                RunConstants.EventSpiritGrafter,
                RunConstants.EventTheLanternKey,
                RunConstants.EventZenWeaver,
                .. SharedEventPool,
            ];
            rng.Shuffle(hivePool);
            return hivePool.Where(eventId => eventId != 0).ToArray();
        }

        if (act == RunConstants.ActGlory)
        {
            // Glory.AllEvents in its own declaration order. This list used to reuse
            // Hive's, which put ten events act 3 never offers into the pool and left
            // out all seven it does -- and, because the pool is SHUFFLED, drew a
            // different count and desynced every later Rng.Shuffle draw in the act.
            int[] gloryPool =
            [
                RunConstants.EventBattlewornDummy,
                RunConstants.EventGraveOfTheForgotten,
                RunConstants.EventHungryForMushrooms,
                RunConstants.EventReflections,
                RunConstants.EventRoundTeaParty,
                RunConstants.EventTrial,
                RunConstants.EventTinkerTime,
                .. SharedEventPool,
            ];
            rng.Shuffle(gloryPool);
            return gloryPool.Where(eventId => eventId != 0).ToArray();
        }

        bool underdocks = act == RunConstants.ActUnderdocks;
        int[] eventPool = underdocks
            ?
            [
                RunConstants.EventAbyssalBaths,
                RunConstants.EventDrowningBeacon,
                RunConstants.EventEndlessConveyor,
                RunConstants.EventPunchOff,
                RunConstants.EventSpiralingWhirlpool,
                RunConstants.EventSunkenStatue,
                RunConstants.EventSunkenTreasury,
                RunConstants.EventDoorsOfLightAndDark,
                RunConstants.EventTrashHeap,
                RunConstants.EventWaterloggedScriptorium,
                RunConstants.EventBrainLeech,
                RunConstants.EventCrystalSphere,
                RunConstants.EventDollRoom,
                RunConstants.EventFakeMerchant,
                RunConstants.EventPotionCourier,
                RunConstants.EventRanwidTheElder,
                RunConstants.EventRelicTrader,
                RunConstants.EventRoomFullOfCheese,
                RunConstants.EventSelfHelpBook,
                RunConstants.EventSlipperyBridge,
                RunConstants.EventStoneOfAllTime,
                RunConstants.EventSymbiote,
                RunConstants.EventTeaMaster,
                RunConstants.EventTheFutureOfPotions,
                RunConstants.EventTheLegendsWereTrue,
                RunConstants.EventThisOrThat,
                RunConstants.EventWarHistorianRepy,
                RunConstants.EventWelcomeToWongos,
            ]
            :
            [
                RunConstants.EventAromaOfChaos,
                RunConstants.EventByrdonisNest,
                RunConstants.EventDenseVegetation,
                RunConstants.EventJungleMazeAdventure,
                RunConstants.EventLuminousChoir,
                RunConstants.EventMorphicGrove,
                RunConstants.EventSapphireSeed,
                RunConstants.EventSunkenStatue,
                RunConstants.EventTabletOfTruth,
                RunConstants.EventUnrestSite,
                RunConstants.EventWellspring,
                RunConstants.EventWhisperingHollow,
                RunConstants.EventWoodCarvings,
                RunConstants.EventBrainLeech,
                RunConstants.EventCrystalSphere,
                RunConstants.EventDollRoom,
                RunConstants.EventFakeMerchant,
                RunConstants.EventPotionCourier,
                RunConstants.EventRanwidTheElder,
                RunConstants.EventRelicTrader,
                RunConstants.EventRoomFullOfCheese,
                RunConstants.EventSelfHelpBook,
                RunConstants.EventSlipperyBridge,
                RunConstants.EventStoneOfAllTime,
                RunConstants.EventSymbiote,
                RunConstants.EventTeaMaster,
                RunConstants.EventTheFutureOfPotions,
                RunConstants.EventTheLegendsWereTrue,
                RunConstants.EventThisOrThat,
                RunConstants.EventWarHistorianRepy,
                RunConstants.EventWelcomeToWongos,
            ];

        rng.Shuffle(eventPool);
        return eventPool.Where(eventId => eventId != 0).ToArray();
    }

    /// <summary>
    /// <c>RunManager.SetActInternal</c>: move to the next act and lay out its map.
    /// </summary>
    /// <remarks>
    /// Four things, and the two easy to miss are the ones that are not there. The FLOOR
    /// does not reset — a live capture crosses into act 2 still on floor 17 and counts on
    /// from there — and the deck, relics and gold carry over untouched. What does reset is
    /// the unknown-map-point odds (<c>Odds.UnknownMapPoint.ResetToBase()</c>), which climb
    /// as a run walks question marks and start each act fresh. The rooms themselves are
    /// not generated here: every act's were rolled at run start, so this only points
    /// <c>CurrentActIndex</c> at them.
    /// </remarks>
    public static bool AdvanceToNextAct(RunState state)
    {
        if (state.CurrentActIndex >= state.Acts.Count - 1)
        {
            return false;
        }

        state.CurrentActIndex++;
        // RoomSet owns these counters per act; a new act begins at queue index 0.
        state.NormalEncountersVisited = 0;
        state.EliteEncountersVisited = 0;
        state.EventSequenceIndex = 0;
        state.AwaitingActStartNode = true;
        state.UnknownMapPointsVisited = 0;
        state.UnknownMapPointMonsterOdds = 0.1;
        state.UnknownMapPointEliteOdds = -1.0;
        state.UnknownMapPointTreasureOdds = 0.02;
        state.UnknownMapPointShopOdds = 0.03;
        GenerateActMap(state);
        return true;
    }

    /// <summary>
    /// `GoldenPathActMap`'s fixed sequence, from row one up. The compass replaces the
    /// act's whole map with this single column.
    /// </summary>
    /// <remarks>
    /// SIXTEEN rooms, with the boss on row seventeen -- one row PAST the standard map's
    /// <see cref="RunConstants.MapBossRow"/>. The golden path is a taller map, not a
    /// re-typed one, which is why the boss row is read off the generated map rather than
    /// assumed: see <c>BossRow</c>.
    ///
    /// `_defaultPointTypes` in the game drops index 2 in multiplayer; solo keeps all
    /// sixteen.
    /// </remarks>
    private static readonly int[] GoldenPathRooms =
    [
        RunConstants.NodeNormal,
        // `MapPointType.Unknown`, which every map spells `NodeEvent` (`GameMapPointType`);
        // resolved on arrival, and always an Event here. `NodeNone` is the empty
        // map-option slot, so a "?" written that way could not be travelled to.
        RunConstants.NodeEvent,
        RunConstants.NodeNormal,
        RunConstants.NodeRest,
        RunConstants.NodeNormal,
        RunConstants.NodeRest,
        RunConstants.NodeEvent,
        RunConstants.NodeRelic,
        RunConstants.NodeEvent,
        RunConstants.NodeRelic,
        RunConstants.NodeEvent,
        RunConstants.NodeShop,
        RunConstants.NodeElite,
        RunConstants.NodeRest,
        RunConstants.NodeElite,
        RunConstants.NodeRest,
    ];

    /// <summary>
    /// The row this map puts its boss on. The standard map's is
    /// <see cref="RunConstants.MapBossRow"/>; the golden path is one taller.
    /// </summary>
    /// <remarks>
    /// Read off the generated nodes rather than computed, so it answers for whatever map
    /// the run is actually standing on. The fallback is the constant, for a state whose
    /// map has not been generated yet.
    /// </remarks>
    public static int BossRow(RunState state)
    {
        foreach (var node in state.MapNodes.Values)
        {
            if (node.NodeType == RunConstants.NodeBoss)
            {
                return node.Row;
            }
        }

        return RunConstants.MapBossRowFor(state.Act);
    }

    /// <summary>
    /// `GoldenCompass.ModifyGeneratedMap` returns a <c>GoldenPathActMap</c> for the act the
    /// compass was picked up in: one column, a fixed sequence, no choices at all.
    /// </summary>
    private static bool GenerateGoldenPath(RunState state)
    {
        if (
            !state.Relics.Any(relic => relic.DefId == RunConstants.RelicGoldenCompass)
            || state.GoldenPathAct != state.CurrentActIndex
        )
        {
            return false;
        }

        state.MapNodes = [];
        state.CurrentMapCoord = (RunConstants.MapStartCol, 0);
        Array.Clear(state.MapOptionCoords);

        int col = RunConstants.MapStartCol;
        for (int row = 1; row <= GoldenPathRooms.Length; row++)
        {
            GetOrCreate(state, col, row).NodeType = GoldenPathRooms[row - 1];
            AddEdge(state, (col, row - 1), (col, row));
        }

        int bossRow = GoldenPathRooms.Length + 1;
        GetOrCreate(state, col, bossRow).NodeType = RunConstants.NodeBoss;
        AddEdge(state, (col, GoldenPathRooms.Length), (col, bossRow));
        GetOrCreate(state, col, 0).NodeType = RunConstants.NodeAncient;
        RefreshMapOptions(state);
        return true;
    }

    /// <summary>
    /// <c>ActModel.GetMapPointTypes</c>: each act draws its rests first, then its unknown
    /// rooms. Hive changes the Gaussian mean; Glory uses a uniform integer draw.
    /// </summary>
    internal static (int Rests, int Unknowns) RollPointTypeCounts(int act, GameRng rng)
    {
        int restCount = act switch
        {
            RunConstants.ActHive => rng.NextGaussianInt(6, 1, 6, 7),
            RunConstants.ActGlory => rng.NextInt(5, 7),
            _ => rng.NextGaussianInt(7, 1, 6, 7),
        };
        int unknownCount =
            rng.NextGaussianInt(12, 1, 10, 14)
            - (act is RunConstants.ActHive or RunConstants.ActGlory ? 1 : 0);
        return (restCount, unknownCount);
    }

    /// <summary>
    /// <c>RunManager.GenerateMap</c>: the standard map, then <c>Hook.ModifyGeneratedMap</c>
    /// over the listeners in order -- deck cards before relics -- then
    /// <c>ModifyGeneratedMapLate</c> and <c>AfterMapGenerated</c>.
    /// </summary>
    /// <remarks>
    /// Two listeners replace the map: Spoils Map (a deck card) with its hourglass, and
    /// Golden Compass (a relic) with its golden path. The relic is asked second, so when
    /// both answer for the same act the golden path is what the run gets -- which is why
    /// it is tried first here. The standard map the game builds and discards reads only
    /// its own fresh <c>act_N_map</c> stream, so skipping it costs no draw anywhere else.
    /// </remarks>
    public static void GenerateActMap(RunState state)
    {
        state.SpoilsMapQuestCoord = null;
        if (GenerateGoldenPath(state))
        {
            MarkSpoilsMapQuest(state);
            return;
        }

        if (SpoilsActMapGenerator.Applies(state))
        {
            SpoilsActMapGenerator.Generate(state);
            AssignEncounterIds(state);
            MarkSpoilsMapQuest(state);
            MarkRoomsForFurCoat(state);
            RefreshMapOptions(state);
            return;
        }

        state.MapNodes = [];
        state.CurrentMapCoord = (RunConstants.MapStartCol, 0);
        Array.Clear(state.MapOptionCoords);
        GetOrCreate(state, RunConstants.MapStartCol, 0).NodeType = RunConstants.NodeNone;
        GetOrCreate(
            state,
            RunConstants.MapStartCol,
            RunConstants.MapBossRowFor(state.Act)
        ).NodeType = RunConstants.NodeBoss;

        // The game keys this stream on the act *index* — `act_{CurrentActIndex + 1}_map`
        // — not on which act was rolled. This used to pass `state.Act - 1`, conflating
        // the two: an Underdocks act 1 would have read "act_2_map" instead of
        // "act_1_map" and desynced the entire map.
        var mapRng = state.Rng.ActMapRng(state.CurrentActIndex);
        var (restCount, unknownCount) = RollPointTypeCounts(state.Act, mapRng);

        var starts = new List<(int Col, int Row)>();
        for (int path = 0; path < RunConstants.MapPathIterations; path++)
        {
            int startCol = mapRng.NextInt(RunConstants.MapWidth);
            if (path == 1)
            {
                while (starts.Contains((startCol, 1)))
                {
                    startCol = mapRng.NextInt(RunConstants.MapWidth);
                }
            }

            var current = GetOrCreate(state, startCol, 1);
            if (!starts.Contains((startCol, 1)))
            {
                starts.Add((startCol, 1));
            }

            GeneratePath(state, mapRng, current);
        }

        // Order matters. The game wires these with ForEachInRow, which walks the grid
        // columns 0..6 in order, and that insertion order becomes the child-enumeration
        // order used by FindAllPaths. Segments then land in their duplicate group in
        // that order, and PrunePaths shuffles the group before keeping one — so a
        // different initial order prunes a different node. Adding them in path-draw
        // order (the order the 7 starts were rolled) is not the same thing.
        foreach (var start in starts.OrderBy(s => s.Col))
        {
            AddEdge(state, state.CurrentMapCoord, start);
        }

        foreach (
            var node in state
                .MapNodes.Values.Where(n => n.Row == RunConstants.MapBossRowFor(state.Act) - 1)
                .OrderBy(n => n.Col)
                .ToArray()
        )
        {
            AddEdge(
                state,
                (node.Col, node.Row),
                (RunConstants.MapStartCol, RunConstants.MapBossRowFor(state.Act))
            );
        }

        AssignPointTypes(state, mapRng, restCount, unknownCount);
        PruneAndRepair(state, mapRng, restCount, unknownCount);
        CenterGrid(state);
        SpreadAdjacentMapPoints(state);
        StraightenPaths(state);
        AssignEncounterIds(state);

        // StandardActMap stamps the starting point LAST — `BossMapPoint.PointType =
        // Boss; StartingMapPoint.PointType = Ancient;` come after every other assignment,
        // so they win. Setting it at the top of this method instead let the type
        // assignment pass overwrite it, and the node came out Unassigned.
        //
        // Act 1's ancient is Neow, which the run begins standing on rather than travels
        // to, so the emulator got away without the type entirely. Every act after opens
        // on the map with this node as the only thing to walk to.
        GetOrCreate(state, RunConstants.MapStartCol, 0).NodeType = RunConstants.NodeAncient;
        MarkSpoilsMapQuest(state);
        MarkRoomsForFurCoat(state);
        RefreshMapOptions(state);
    }

    /// <summary>
    /// <c>SpoilsMap.ModifyGeneratedMapLate</c> then <c>AfterMapGenerated</c>: on the quest's
    /// act, with a copy in the deck, the FIRST Treasure in <c>GetAllMapPoints</c> order --
    /// column-major -- takes the quest marker, whichever map the run was handed.
    /// </summary>
    private static void MarkSpoilsMapQuest(RunState state)
    {
        if (!SpoilsActMapGenerator.Applies(state))
        {
            return;
        }

        var treasure = state
            .MapNodes.Values.Where(node =>
                node.NodeType == RunConstants.NodeRelic && IsGridRow(state, node.Row)
            )
            .OrderBy(node => node.Col)
            .ThenBy(node => node.Row)
            .FirstOrDefault();
        if (treasure != null)
        {
            state.SpoilsMapQuestCoord = (treasure.Col, treasure.Row);
        }
    }

    /// <summary>
    /// `FurCoat.AddMarkedRooms`, called from both `AfterObtained` and
    /// `ModifyGeneratedMapLate`: eight Monster-or-Elite points, shuffled off the relic's
    /// OWN stream, marked for the act the coat was picked up in.
    /// </summary>
    /// <remarks>
    /// The act check is the game's early return: a coat obtained in act one marks act one
    /// and leaves every later map alone, which is why this is not simply "eight rooms per
    /// act". Re-generating the SAME act re-marks it -- the game re-reads its saved
    /// coordinates and only re-rolls when they no longer land on fights, which is the same
    /// answer for a map generated from the same seed.
    /// </remarks>
    private static void MarkRoomsForFurCoat(RunState state)
    {
        state.FurCoatRooms.Clear();
        if (
            !state.Relics.Any(relic => relic.DefId == RunConstants.RelicFurCoat)
            || state.FurCoatAct != state.CurrentActIndex
        )
        {
            return;
        }

        var fights = state
            .MapNodes.Values.Where(node =>
                node.NodeType is RunConstants.NodeNormal or RunConstants.NodeElite
            )
            .OrderBy(node => node.Col)
            .ThenBy(node => node.Row)
            .ToList();

        state
            .Rng.RelicRng(GeneratedData.Relics.Get(RunConstants.RelicFurCoat).Entry)
            .Shuffle(fights);
        state.FurCoatRooms.AddRange(
            fights.Take(RunConstants.FurCoatCombats).Select(node => (node.Col, node.Row))
        );
    }

    /// <summary>
    /// `FurCoat.AfterObtained`: the coat latches the act it arrived in and marks that act's
    /// map straight away, rather than waiting for the next one to be generated.
    /// </summary>
    public static void MarkRoomsWhenFurCoatArrives(RunState state)
    {
        state.FurCoatAct = state.CurrentActIndex;
        MarkRoomsForFurCoat(state);
    }

    private static void PruneAndRepair(RunState state, GameRng rng, int restCount, int unknownCount)
    {
        for (int i = 0; i < 3; i++)
        {
            PruneDuplicateSegments(state, rng);
            if (!RepairPrunedPointTypes(state, rng, restCount, unknownCount))
            {
                break;
            }
        }
    }

    private static bool RepairPrunedPointTypes(
        RunState state,
        GameRng rng,
        int restCount,
        int unknownCount
    )
    {
        bool repaired = false;
        repaired |= RepairPointType(state, RunConstants.NodeShop, RunConstants.MapShopCount, rng);
        repaired |= RepairPointType(
            state,
            RunConstants.NodeElite,
            RunConstants.MapEliteCountFor(state.AscensionLevel),
            rng
        );
        repaired |= RepairPointType(state, RunConstants.NodeRest, restCount, rng);
        repaired |= RepairPointType(state, RunConstants.NodeEvent, unknownCount, rng);
        return repaired;
    }

    private static bool RepairPointType(RunState state, int nodeType, int targetCount, GameRng rng)
    {
        int missing = targetCount - state.MapNodes.Values.Count(node => node.NodeType == nodeType);
        if (missing <= 0)
        {
            return false;
        }

        var candidates = state
            .MapNodes.Values.Where(node =>
                node.NodeType == RunConstants.NodeNormal && node.CanBeModified
            )
            .ToList();
        rng.StableShuffle(candidates, CompareNodesByColThenRow);
        bool repaired = false;
        foreach (var node in candidates)
        {
            if (missing == 0)
            {
                break;
            }

            if (IsValidPointType(state, nodeType, node))
            {
                node.NodeType = nodeType;
                missing--;
                repaired = true;
            }
        }

        return repaired;
    }

    private static void PruneDuplicateSegments(RunState state, GameRng rng)
    {
        int count = 0;
        var matchingSegments = FindMatchingSegments(state);
        while (PrunePaths(state, matchingSegments, rng))
        {
            count++;
            if (count > 50)
            {
                throw new InvalidOperationException("Unable to prune matching map segments");
            }

            matchingSegments = FindMatchingSegments(state);
        }
    }

    private static List<List<RunMapNode[]>> FindMatchingSegments(RunState state)
    {
        var paths = FindAllPaths(state, MapNodeAt(state, state.CurrentMapCoord));
        var segments = new SortedDictionary<string, List<RunMapNode[]>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            AddSegmentsToDictionary(path, segments);
        }

        return segments.Values.Where(segmentList => segmentList.Count > 1).ToList();
    }

    private static List<List<RunMapNode>> FindAllPaths(RunState state, RunMapNode current)
    {
        if (current.NodeType == RunConstants.NodeBoss)
        {
            return
            [
                [current],
            ];
        }

        var paths = new List<List<RunMapNode>>();
        foreach (var childCoord in current.Children)
        {
            var child = MapNodeAt(state, childCoord);
            foreach (var path in FindAllPaths(state, child))
            {
                path.Insert(0, current);
                paths.Add(path);
            }
        }

        return paths;
    }

    private static void AddSegmentsToDictionary(
        IReadOnlyList<RunMapNode> path,
        IDictionary<string, List<RunMapNode[]>> segments
    )
    {
        for (int i = 0; i < path.Count - 1; i++)
        {
            if (!IsValidSegmentStartMapPoint(path[i]))
            {
                continue;
            }

            for (int j = 2; j < path.Count - i; j++)
            {
                var end = path[i + j];
                if (!IsValidSegmentEndMapPoint(end))
                {
                    continue;
                }

                var segment = path.Skip(i).Take(j + 1).ToArray();
                string key = GenerateSegmentKey(segment);
                if (!segments.TryGetValue(key, out var existing))
                {
                    segments[key] = [segment];
                }
                else if (
                    !existing.Any(existingSegment => OverlappingSegment(existingSegment, segment))
                )
                {
                    existing.Add(segment);
                }
            }
        }
    }

    private static bool IsValidSegmentStartMapPoint(RunMapNode node) =>
        node.Children.Count <= 1 ? node.Row == 0 : true;

    private static bool IsValidSegmentEndMapPoint(RunMapNode node) => node.Parents.Count >= 2;

    /// <summary>
    /// Our node-type constants to the game's <c>MapPointType</c> enum values.
    ///
    /// This matters because segment keys embed the point types as integers and the
    /// segments live in a SortedDictionary ordered by that key string. Grouping
    /// survives any relabelling, but *iteration order* does not — and PrunePaths
    /// walks the groups in order, shuffling and pruning as it goes. Emitting our own
    /// numbering here sorts the groups differently from the game, which changes both
    /// the pruning decisions and the RNG draws they consume.
    ///
    /// NodeNone maps to Ancient because by the time pruning runs every other node has
    /// been assigned a type, so the start point is the only one left holding it.
    /// </summary>
    private static int GameMapPointType(int nodeType) =>
        nodeType switch
        {
            RunConstants.NodeEvent => 1, // Unknown
            RunConstants.NodeShop => 2, // Shop
            RunConstants.NodeRelic => 3, // Treasure
            RunConstants.NodeRest => 4, // RestSite
            RunConstants.NodeNormal => 5, // Monster
            RunConstants.NodeElite => 6, // Elite
            RunConstants.NodeBoss => 7, // Boss
            RunConstants.NodeNone => 8, // Ancient
            _ => 0, // Unassigned
        };

    private static string GenerateSegmentKey(IReadOnlyList<RunMapNode> segment)
    {
        var start = segment[0];
        var end = segment[^1];
        string prefix =
            start.Row == 0
                ? $"{start.Row}-{end.Col},{end.Row}-"
                : $"{start.Col},{start.Row}-{end.Col},{end.Row}-";
        return prefix + string.Join(",", segment.Select(node => GameMapPointType(node.NodeType)));
    }

    private static bool OverlappingSegment(
        IReadOnlyList<RunMapNode> left,
        IReadOnlyList<RunMapNode> right
    )
    {
        if (left.Count < 3 || right.Count < 3)
        {
            return false;
        }

        for (int i = 1; i <= left.Count - 2; i++)
        {
            if (ReferenceEquals(left[i], right[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PrunePaths(
        RunState state,
        IEnumerable<List<RunMapNode[]>> matchingSegments,
        GameRng rng
    )
    {
        foreach (var matchingSegment in matchingSegments)
        {
            rng.Shuffle(matchingSegment);
            if (PruneAllButLast(state, matchingSegment) != 0)
            {
                return true;
            }

            if (BreakAParentChildRelationshipInAnySegment(state, matchingSegment))
            {
                return true;
            }
        }

        return false;
    }

    private static int PruneAllButLast(RunState state, IReadOnlyList<RunMapNode[]> matches)
    {
        int pruned = 0;
        foreach (var match in matches)
        {
            if (pruned == matches.Count - 1)
            {
                return pruned;
            }

            if (PruneSegment(state, match))
            {
                pruned++;
            }
        }

        return pruned;
    }

    private static bool PruneSegment(RunState state, RunMapNode[] segment)
    {
        bool pruned = false;
        for (int i = 0; i < segment.Length - 1; i++)
        {
            var node = segment[i];
            if (!IsInMap(state, node))
            {
                return true;
            }

            if (
                node.Children.Count > 1
                || node.Parents.Count > 1
                // `&& !IsRemoved(...)`: the game tests the parent's *grid* cell, and the
                // ancient is never in the grid, so a row-1 node is never skipped on
                // account of the start point — even when pruning has left it one child.
                || node.Parents.Any(parent =>
                    MapNodeAt(state, parent).Children.Count == 1
                    && !IsRemovedFromGrid(state, parent)
                )
            )
            {
                continue;
            }

            var tail = segment.Skip(i).ToArray();
            if (tail.Any(n => n.Children.Count > 1 && n.Parents.Count == 1))
            {
                continue;
            }

            if (segment[^1].Parents.Count == 1)
            {
                return false;
            }

            if (
                !node
                    .Children.Where(child =>
                        !segment.Any(n => n.Col == child.Col && n.Row == child.Row)
                    )
                    .Any(child => MapNodeAt(state, child).Parents.Count == 1)
            )
            {
                RemovePoint(state, node);
                pruned = true;
            }
        }

        return pruned;
    }

    private static bool BreakAParentChildRelationshipInAnySegment(
        RunState state,
        IEnumerable<RunMapNode[]> matches
    )
    {
        foreach (var match in matches)
        {
            if (BreakAParentChildRelationshipInSegment(state, match))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BreakAParentChildRelationshipInSegment(
        RunState state,
        IReadOnlyList<RunMapNode> segment
    )
    {
        // The game walks the WHOLE segment and breaks every qualifying link before
        // reporting back (MapPathPruning.BreakAParentChildRelationshipInSegment sets a
        // flag and keeps going). Returning on the first break, as this used to, leaves
        // later links intact — and the re-scan that follows sees a different graph, so
        // they may never be broken at all. Seed "L4CEF9U55L" kept one such edge, which
        // then pinned its row-11 node to the only column both its children allowed.
        bool broken = false;
        for (int i = 0; i < segment.Count - 1; i++)
        {
            var node = segment[i];
            if (node.Children.Count < 2)
            {
                continue;
            }

            var child = segment[i + 1];
            if (child.Parents.Count != 1)
            {
                RemoveEdge(state, (node.Col, node.Row), (child.Col, child.Row));
                broken = true;
            }
        }

        return broken;
    }

    // The game's grid holds only the path rows, so the ancient and boss points read as
    // "removed" to any grid lookup — see IsGridRow.
    private static bool IsRemovedFromGrid(RunState state, (int Col, int Row) coord) =>
        !IsGridRow(state, coord.Row);

    private static bool IsInMap(RunState state, RunMapNode node) =>
        node.NodeType is RunConstants.NodeNone or RunConstants.NodeBoss
        || state.MapNodes.TryGetValue((node.Col, node.Row), out var current)
            && ReferenceEquals(current, node);

    private static void RemovePoint(RunState state, RunMapNode node)
    {
        state.MapNodes.Remove((node.Col, node.Row));
        foreach (var child in node.Children.ToArray())
        {
            RemoveEdge(state, (node.Col, node.Row), child);
        }

        foreach (var parent in node.Parents.ToArray())
        {
            RemoveEdge(state, parent, (node.Col, node.Row));
        }
    }

    private static void RemoveEdge(
        RunState state,
        (int Col, int Row) parentCoord,
        (int Col, int Row) childCoord
    )
    {
        if (state.MapNodes.TryGetValue(parentCoord, out var parent))
        {
            parent.Children.Remove(childCoord);
        }

        if (state.MapNodes.TryGetValue(childCoord, out var child))
        {
            child.Parents.Remove(parentCoord);
        }
    }

    // The game's post-processing takes `Grid`, which is [7, rooms + 1] — rows 1..15 of
    // path nodes. The ancient and boss points are NOT in it: StandardActMap holds them
    // as standalone `StartingMapPoint` / `BossMapPoint` at `GetColumnCount() / 2`, and
    // the boss's row is `GetRowCount()`, one past the grid's last row. So centering,
    // spreading and straightening never move them — they stay in the middle column
    // whatever the body does, which is also why the save writes `start` and `boss`
    // outside `points`. The emulator carries them as ordinary nodes, so every pass has
    // to skip them explicitly or a centered map drags them off-centre with it.
    private static bool IsGridRow(RunState state, int row) =>
        row > 0 && row < RunConstants.MapBossRowFor(state.Act);

    private static void CenterGrid(RunState state)
    {
        bool leftEmpty = IsColumnEmpty(state, 0) && IsColumnEmpty(state, 1);
        bool rightEmpty =
            IsColumnEmpty(state, RunConstants.MapWidth - 1)
            && IsColumnEmpty(state, RunConstants.MapWidth - 2);
        int delta =
            leftEmpty && !rightEmpty ? -1
            : !leftEmpty && rightEmpty ? 1
            : 0;
        if (delta == 0)
        {
            return;
        }

        var gridNodes = state.MapNodes.Values.Where(n => IsGridRow(state, n.Row));
        var nodes =
            delta > 0
                ? gridNodes.OrderByDescending(n => n.Col).ThenBy(n => n.Row).ToList()
                : gridNodes.OrderBy(n => n.Col).ThenBy(n => n.Row).ToList();
        foreach (var node in nodes)
        {
            MoveNode(state, node, node.Col + delta);
        }
    }

    private static bool IsColumnEmpty(RunState state, int col) =>
        !state.MapNodes.Values.Any(node => IsGridRow(state, node.Row) && node.Col == col);

    private static void SpreadAdjacentMapPoints(RunState state)
    {
        for (int row = 1; row < RunConstants.MapBossRowFor(state.Act); row++)
        {
            // Collect the row ONCE, in column order, and keep that list for every pass
            // of the loop below — the game walks its grid columns to build the list
            // before entering its do/while and never rebuilds it. Re-sorting each pass
            // (what this used to do) visits the nodes in a different order once one has
            // moved, and the order decides which node claims a free column: seed
            // "L4CEF9U55L" ended up with row 11's last node at c5 where the game put it
            // at c6, with the rest of the map identical.
            var rowNodes = state
                .MapNodes.Values.Where(node => node.Row == row)
                .OrderBy(node => node.Col)
                .ToList();
            bool changed;
            do
            {
                changed = false;
                foreach (var node in rowNodes)
                {
                    int currentCol = node.Col;
                    var allowed = GetAllowedPositions(state, node);
                    int bestCol = currentCol;
                    int bestGap = ComputeGap(currentCol, rowNodes, node);
                    foreach (int candidateCol in allowed)
                    {
                        if (
                            candidateCol == currentCol
                            || state.MapNodes.ContainsKey((candidateCol, row))
                        )
                        {
                            continue;
                        }

                        int gap = ComputeGap(candidateCol, rowNodes, node);
                        if (gap > bestGap)
                        {
                            bestCol = candidateCol;
                            bestGap = gap;
                        }
                    }

                    if (bestCol != currentCol)
                    {
                        MoveNode(state, node, bestCol);
                        changed = true;
                    }
                }
            } while (changed);
        }
    }

    private static HashSet<int> GetAllowedPositions(RunState state, RunMapNode node)
    {
        var allowed = Enumerable.Range(0, RunConstants.MapWidth).ToHashSet();
        foreach (var parent in node.Parents)
        {
            allowed.IntersectWith(GetNeighborAllowedPositions(parent.Col));
        }

        foreach (var child in node.Children)
        {
            allowed.IntersectWith(GetNeighborAllowedPositions(child.Col));
        }

        return allowed;
    }

    private static HashSet<int> GetNeighborAllowedPositions(int col) =>
        Enumerable
            .Range(
                Math.Max(0, col - 1),
                Math.Min(RunConstants.MapWidth - 1, col + 1) - Math.Max(0, col - 1) + 1
            )
            .ToHashSet();

    private static int ComputeGap(int candidateCol, List<RunMapNode> rowNodes, RunMapNode current)
    {
        int gap = int.MaxValue;
        foreach (var node in rowNodes)
        {
            if (!ReferenceEquals(node, current))
            {
                gap = Math.Min(gap, Math.Abs(candidateCol - node.Col));
            }
        }

        return gap;
    }

    private static void StraightenPaths(RunState state)
    {
        for (int row = 1; row < RunConstants.MapBossRowFor(state.Act); row++)
        {
            for (int col = 0; col < RunConstants.MapWidth; col++)
            {
                if (!state.MapNodes.TryGetValue((col, row), out var node))
                {
                    continue;
                }

                if (node.Parents.Count != 1 || node.Children.Count != 1)
                {
                    continue;
                }

                var parent = node.Parents[0];
                var child = node.Children[0];
                bool leftKink = node.Col < child.Col && node.Col < parent.Col;
                bool rightKink = node.Col > child.Col && node.Col > parent.Col;
                if (
                    leftKink
                    && col < RunConstants.MapWidth - 1
                    && !state.MapNodes.ContainsKey((col + 1, row))
                )
                {
                    MoveNode(state, node, col + 1);
                }
                else if (rightKink && col > 0 && !state.MapNodes.ContainsKey((col - 1, row)))
                {
                    MoveNode(state, node, col - 1);
                }
            }
        }
    }

    private static void MoveNode(RunState state, RunMapNode node, int newCol)
    {
        if (newCol < 0 || newCol >= RunConstants.MapWidth || newCol == node.Col)
        {
            return;
        }

        var oldCoord = (node.Col, node.Row);
        var newCoord = (newCol, node.Row);
        if (state.MapNodes.ContainsKey(newCoord))
        {
            return;
        }

        state.MapNodes.Remove(oldCoord);
        node.Col = newCol;
        state.MapNodes[newCoord] = node;
        foreach (var parentCoord in node.Parents.ToArray())
        {
            if (state.MapNodes.TryGetValue(parentCoord, out var parent))
            {
                ReplaceCoord(parent.Children, oldCoord, newCoord);
            }
        }

        foreach (var childCoord in node.Children.ToArray())
        {
            if (state.MapNodes.TryGetValue(childCoord, out var child))
            {
                ReplaceCoord(child.Parents, oldCoord, newCoord);
            }
        }

        if (state.CurrentMapCoord == oldCoord)
        {
            state.CurrentMapCoord = newCoord;
        }
    }

    private static void ReplaceCoord(
        List<(int Col, int Row)> coords,
        (int Col, int Row) oldCoord,
        (int Col, int Row) newCoord
    )
    {
        int index = coords.IndexOf(oldCoord);
        if (index >= 0)
        {
            coords[index] = newCoord;
        }
    }

    private static void AddTraceNode(RunState state, int col, int row, int nodeType)
    {
        var node = GetOrCreate(state, col, row);
        node.NodeType = nodeType;
    }

    /// <summary>
    /// <c>Hook.ShouldAllowFreeTravel</c>: true while anything the run carries answers yes.
    /// Winged Boots is the only one a solo run can hold -- the other implementor is the
    /// Flight modifier -- and it answers yes until its third charge is spent.
    /// </summary>
    public static bool AllowsFreeTravel(RunState state) =>
        state.Relics.Any(relic =>
            relic.DefId == RunConstants.RelicWingedBoots
            && relic.Counter < RunConstants.WingedBootsTravels
        );

    /// <summary>
    /// <c>MapTravel.GetTravelablePointsFrom</c>: the current node's children, or the whole
    /// of the next row while free travel is allowed.
    /// </summary>
    /// <remarks>
    /// The boss is deliberately not covered by the free-travel branch. It is not in the
    /// game's <c>Grid</c>, so <c>GetPointsInRow</c> never returns it and free travel from
    /// the last grid row would offer nothing at all; <c>NMapScreen</c> makes the boss
    /// travelable outright from there instead, which is what the children are.
    /// </remarks>
    private static IEnumerable<(int Col, int Row)> TravelableCoords(
        RunState state,
        RunMapNode current
    )
    {
        if (current.Row >= BossRow(state) - 1 || !AllowsFreeTravel(state))
        {
            return current.Children;
        }

        int row = current.Row + 1;
        return state
            .MapNodes.Values.Where(node => node.Row == row)
            .Select(node => (node.Col, node.Row));
    }

    public static void RefreshMapOptions(RunState state)
    {
        Array.Clear(state.MapNodeTypes);
        Array.Clear(state.MapChoices);
        Array.Clear(state.MapOptionCoords);
        if (!state.MapNodes.TryGetValue(state.CurrentMapCoord, out var current))
        {
            return;
        }

        // A new act shows its map before the run has stepped onto it, so the only thing
        // to travel to is the starting point -- the act's ancient.
        var options = state.AwaitingActStartNode
            ? [(RunConstants.MapStartCol, 0)]
            : TravelableCoords(state, current)
                .OrderBy(coord => coord.Row)
                .ThenBy(coord => coord.Col)
                .Take(RunConstants.MapChoices)
                .ToArray();
        for (int i = 0; i < options.Length; i++)
        {
            var node = state.MapNodes[options[i]];
            state.MapNodeTypes[i] = node.NodeType;
            state.MapChoices[i] = node.NodeType switch
            {
                RunConstants.NodeNormal => state.NormalEncounterSequence[
                    state.NormalEncountersVisited % state.NormalEncounterSequence.Length
                ],
                RunConstants.NodeElite => state.EliteEncounterSequence[
                    state.EliteEncountersVisited % state.EliteEncounterSequence.Length
                ],
                RunConstants.NodeBoss => state.BossEncounterId,
                _ => node.EncounterId,
            };
            state.MapOptionCoords[i] = options[i];
        }
    }

    public static bool ChooseMapNode(
        RunState state,
        int action,
        out int nodeType,
        out int encounterId
    )
    {
        nodeType = RunConstants.NodeNone;
        encounterId = 0;

        if (
            (uint)action >= RunConstants.MapChoices
            || state.MapNodeTypes[action] == RunConstants.NodeNone
        )
        {
            return false;
        }

        var coord = state.MapOptionCoords[action];
        if (coord is null)
        {
            return false;
        }

        nodeType = state.MapNodeTypes[action];
        encounterId = state.MapChoices[action];
        if (state.AwaitingActStartNode)
        {
            // Stepping onto the act's first point, which is not connected to anything
            // behind it -- there is nothing behind it.
            state.AwaitingActStartNode = false;
        }
        else
        {
            SpendFreeTravelIfUnconnected(state, coord.Value);
        }

        state.CurrentMapCoord = coord.Value;
        state.CurrentNodeType = nodeType;
        state.Floor++;
        if (nodeType == RunConstants.NodeNormal)
        {
            state.NormalEncountersVisited++;
        }
        else if (nodeType == RunConstants.NodeElite)
        {
            state.EliteEncountersVisited++;
        }

        Array.Clear(state.MapNodeTypes);
        Array.Clear(state.MapChoices);
        Array.Clear(state.MapOptionCoords);
        return true;
    }

    /// <summary>
    /// <c>WingedBoots.AfterRoomEntered</c>: a charge is spent only when the node just
    /// entered was NOT a child of the one left behind. Walking an edge the map already
    /// draws is free however many charges are left, which is why the relic can be held
    /// for a whole act without moving its counter.
    /// </summary>
    private static void SpendFreeTravelIfUnconnected(RunState state, (int Col, int Row) coord)
    {
        if (
            !state.MapNodes.TryGetValue(state.CurrentMapCoord, out var from)
            || from.Children.Contains(coord)
        )
        {
            return;
        }

        int index = state.Relics.FindIndex(relic => relic.DefId == RunConstants.RelicWingedBoots);
        if (index < 0 || state.Relics[index].Counter >= RunConstants.WingedBootsTravels)
        {
            return;
        }

        state.Relics[index] = state.Relics[index] with
        {
            Counter = state.Relics[index].Counter + 1,
        };
    }

    private static void GeneratePath(RunState state, GameRng rng, RunMapNode start)
    {
        var current = start;
        while (current.Row < RunConstants.MapBossRowFor(state.Act) - 1)
        {
            var child = GenerateNextCoord(state, rng, current);
            AddEdge(state, (current.Col, current.Row), child);
            current = state.MapNodes[child];
        }
    }

    private static (int Col, int Row) GenerateNextCoord(
        RunState state,
        GameRng rng,
        RunMapNode current
    )
    {
        var deltas = new List<int> { -1, 0, 1 };
        rng.StableShuffle(deltas, Comparer<int>.Default);
        foreach (int delta in deltas)
        {
            int target = Math.Clamp(current.Col + delta, 0, RunConstants.MapWidth - 1);
            if (!HasInvalidCrossover(state, current, target))
            {
                return (target, current.Row + 1);
            }
        }
        return (current.Col, current.Row + 1);
    }

    private static bool HasInvalidCrossover(RunState state, RunMapNode current, int targetCol)
    {
        int delta = targetCol - current.Col;
        if (delta == 0 || !state.MapNodes.TryGetValue((targetCol, current.Row), out var sibling))
        {
            return false;
        }

        return sibling.Children.Any(child => child.Col - sibling.Col == -delta);
    }

    private static void AssignPointTypes(
        RunState state,
        GameRng rng,
        int restCount,
        int unknownCount
    )
    {
        int bossRow = RunConstants.MapBossRowFor(state.Act);
        int treasureRow = bossRow - 7;
        int finalRestRow = bossRow - 1;
        foreach (var node in state.MapNodes.Values)
        {
            node.NodeType = node.Row switch
            {
                0 => RunConstants.NodeNone,
                1 => RunConstants.NodeNormal,
                var row when row == treasureRow => RunConstants.NodeRelic,
                var row when row == finalRestRow => RunConstants.NodeRest,
                var row when row == bossRow => RunConstants.NodeBoss,
                _ => RunConstants.NodeNone,
            };
            // The game sets CanBeModified = false on exactly the forced rows, which
            // keeps their points out of the repair candidate pool.
            node.CanBeModified =
                node.Row != 1 && node.Row != treasureRow && node.Row != finalRestRow;
        }

        var pointTypes = new Queue<int>(
            Enumerable
                .Repeat(RunConstants.NodeRest, restCount)
                .Concat(Enumerable.Repeat(RunConstants.NodeShop, RunConstants.MapShopCount))
                .Concat(
                    Enumerable.Repeat(
                        RunConstants.NodeElite,
                        RunConstants.MapEliteCountFor(state.AscensionLevel)
                    )
                )
                .Concat(Enumerable.Repeat(RunConstants.NodeEvent, unknownCount))
        );
        for (int pass = 0; pass < 3 && pointTypes.Count > 0; pass++)
        {
            var candidates = state
                .MapNodes.Values.Where(n =>
                    n.NodeType == RunConstants.NodeNone
                    && n.Row > 1
                    && n.Row < finalRestRow
                    && n.Row != treasureRow
                )
                .ToList();
            rng.StableShuffle(candidates, CompareNodesByColThenRow);
            foreach (var node in candidates)
            {
                if (pointTypes.Count == 0)
                {
                    break;
                }

                node.NodeType = GetNextValidPointType(state, pointTypes, node);
            }
        }

        foreach (
            var node in state.MapNodes.Values.Where(n =>
                n.NodeType == RunConstants.NodeNone && n.Row > 0
            )
        )
        {
            node.NodeType = RunConstants.NodeNormal;
        }
    }

    private static int GetNextValidPointType(RunState state, Queue<int> pointTypes, RunMapNode node)
    {
        for (int i = 0; i < pointTypes.Count; i++)
        {
            int nodeType = pointTypes.Dequeue();
            if (IsValidPointType(state, nodeType, node))
            {
                return nodeType;
            }

            pointTypes.Enqueue(nodeType);
        }
        return RunConstants.NodeNone;
    }

    private static bool IsValidPointType(RunState state, int nodeType, RunMapNode node) =>
        IsValidForLower(nodeType, node)
        && IsValidForUpper(state, nodeType, node)
        && IsValidWithParentsAndChildren(state, nodeType, node)
        && IsValidWithChildren(state, nodeType, node)
        && IsValidWithSiblings(state, nodeType, node);

    private static bool IsValidForLower(int nodeType, RunMapNode node) =>
        node.Row >= 6 || nodeType is not (RunConstants.NodeRest or RunConstants.NodeElite);

    private static bool IsValidForUpper(RunState state, int nodeType, RunMapNode node) =>
        node.Row < RunConstants.MapBossRowFor(state.Act) - 3 || nodeType != RunConstants.NodeRest;

    private static bool IsValidWithParentsAndChildren(
        RunState state,
        int nodeType,
        RunMapNode node
    ) =>
        !HasParentChildRestriction(nodeType)
        || !node
            .Parents.Concat(node.Children)
            .Any(coord => MapNodeTypeAt(state, coord) == nodeType);

    private static bool IsValidWithChildren(RunState state, int nodeType, RunMapNode node) =>
        !HasParentChildRestriction(nodeType)
        || !node.Children.Any(coord => MapNodeTypeAt(state, coord) == nodeType);

    private static bool IsValidWithSiblings(RunState state, int nodeType, RunMapNode node) =>
        !HasSiblingRestriction(nodeType)
        || !node
            .Parents.SelectMany(parent => MapNodeAt(state, parent).Children)
            .Where(coord => coord != (node.Col, node.Row))
            .Any(coord => MapNodeTypeAt(state, coord) == nodeType);

    private static RunMapNode MapNodeAt(RunState state, (int Col, int Row) coord) =>
        state.MapNodes[coord];

    private static int MapNodeTypeAt(RunState state, (int Col, int Row) coord) =>
        MapNodeAt(state, coord).NodeType;

    private static bool HasParentChildRestriction(int nodeType) =>
        nodeType
            is RunConstants.NodeElite
                or RunConstants.NodeRest
                or RunConstants.NodeRelic
                or RunConstants.NodeShop;

    private static bool HasSiblingRestriction(int nodeType) =>
        nodeType
            is RunConstants.NodeRest
                or RunConstants.NodeNormal
                or RunConstants.NodeEvent
                or RunConstants.NodeElite
                or RunConstants.NodeShop;

    private static readonly Comparer<RunMapNode> CompareNodesByColThenRow =
        Comparer<RunMapNode>.Create(
            (a, b) => a.Col != b.Col ? a.Col.CompareTo(b.Col) : a.Row.CompareTo(b.Row)
        );

    private static void AssignEncounterIds(RunState state)
    {
        foreach (
            var group in state
                .MapNodes.Values.Where(n => n.NodeType == RunConstants.NodeNormal)
                .GroupBy(n => n.Row)
                .OrderBy(g => g.Key)
        )
        {
            int index = Math.Min(group.Key - 1, state.NormalEncounterSequence.Length - 1);
            foreach (var node in group)
            {
                node.EncounterId = state.NormalEncounterSequence[Math.Max(0, index)];
            }
        }
        foreach (
            var group in state
                .MapNodes.Values.Where(n => n.NodeType == RunConstants.NodeElite)
                .GroupBy(n => n.Row)
                .OrderBy(g => g.Key)
        )
        {
            int index = Math.Min(group.Key, state.EliteEncounterSequence.Length - 1);
            foreach (var node in group)
            {
                node.EncounterId = state.EliteEncounterSequence[Math.Max(0, index)];
            }
        }
        foreach (var node in state.MapNodes.Values.Where(n => n.NodeType == RunConstants.NodeBoss))
        {
            node.EncounterId = state.BossEncounterId;
        }
    }

    private static RunMapNode GetOrCreate(RunState state, int col, int row)
    {
        if (!state.MapNodes.TryGetValue((col, row), out var node))
        {
            node = new RunMapNode(col, row);
            state.MapNodes[(col, row)] = node;
        }
        return node;
    }

    private static void AddEdge(
        RunState state,
        (int Col, int Row) parentCoord,
        (int Col, int Row) childCoord
    )
    {
        var parent = GetOrCreate(state, parentCoord.Col, parentCoord.Row);
        var child = GetOrCreate(state, childCoord.Col, childCoord.Row);
        if (!parent.Children.Contains(childCoord))
        {
            parent.Children.Add(childCoord);
        }

        if (!child.Parents.Contains(parentCoord))
        {
            child.Parents.Add(parentCoord);
        }
    }

    private static int GrabWithoutRepeatingTags(List<int> bag, int? lastEncounter, GameRng rng)
    {
        var lastTags = lastEncounter.HasValue ? Tags(lastEncounter.Value) : [];
        bool anyValid = bag.Any(enc => enc != lastEncounter && !Tags(enc).Overlaps(lastTags));
        while (true)
        {
            int index = (int)(rng.NextDouble() * bag.Count);
            int enc = bag[index];
            if (!anyValid || (enc != lastEncounter && !Tags(enc).Overlaps(lastTags)))
            {
                bag.RemoveAt(index);
                return enc;
            }
        }
    }

    /// <summary>
    /// An encounter's tags, which <c>AddWithoutRepeatingTags</c> avoids repeating.
    /// </summary>
    /// <remarks>
    /// Read from the generated table rather than transcribed. The hand-written switch
    /// this replaces had lost four entries — KnightsElite, both halves of ScrollsOfBiting
    /// and TunnelerNormal, which also carries a second tag its weak version does not —
    /// and a missing tag is worse than a wrong one: <c>GrabIndex</c> rejection-samples,
    /// so it changes how many draws a grab COSTS. Three of the four are Glory's, which is
    /// act 3 waiting to be handed exactly what E66 did to act 2.
    /// </remarks>
    private static HashSet<string> Tags(int encounterId) =>
        [.. GeneratedData.EncounterTags.For(encounterId)];
}
