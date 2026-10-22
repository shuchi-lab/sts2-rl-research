namespace Sts2Emulator.Core;

using Sts2Emulator.Core.Rng;
using Sts2Emulator.Core.Run;

/// <summary>
/// Deep copies of the mutable simulation state, so a search can fork a position
/// instead of replaying the run to reach it.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is hand-written rather than reflected, because the core is
/// NativeAOT. That makes it drift-prone -- a field added to a state class and not
/// added here is a clone that silently shares it -- so <c>StateCloningTests</c>
/// walks every field by reflection and fails when one is missed.
/// </para>
/// <para>
/// A plain clone is a faithful copy, which for a tree search is an oracle: every
/// run-level stream derives from the run seed, so rolling forward reads the exact
/// rewards, shop stock and encounter compositions still to come. Resampling is what
/// makes a clone safe to search with -- see <c>RunEngine.ResampleHiddenState</c> and
/// docs/agent-interface.md.
/// </para>
/// </remarks>
public static class StateCloning
{
    public static GameRng Clone(this GameRng rng)
    {
        var copy = new GameRng(rng.RawSeed, rng.Algorithm);
        copy.Restore(rng.Snapshot());
        return copy;
    }

    /// <summary>
    /// A copy of this stream, from a DIFFERENT seed but at the same call count.
    /// </summary>
    /// <remarks>
    /// The position is kept deliberately. A stream's call count is not hidden information --
    /// it is a fact about how much has happened -- so a determinization should change what
    /// the next value IS, not how far along the sequence stands. Keeping it also means a
    /// reseeded copy stays comparable to the original in the stream-position diff.
    /// </remarks>
    public static CountingRandom? Reseeded(this CountingRandom? rng, int? seed)
    {
        if (rng is null)
        {
            return null;
        }

        if (seed is null)
        {
            return rng.Clone();
        }

        var copy = new CountingRandom(unchecked((uint)seed.Value), rng.Algorithm);
        for (int i = 0; i < rng.CallCount; i++)
        {
            copy.Next();
        }

        return copy;
    }

    public static CountingRandom Clone(this CountingRandom rng)
    {
        var copy = new CountingRandom(rng.Seed, rng.Algorithm);
        copy.Restore(rng.Snapshot());
        return copy;
    }

    public static EnemyState Clone(this EnemyState enemy) =>
        new()
        {
            DefId = enemy.DefId,
            Hp = enemy.Hp,
            DeathHooksHandled = enemy.DeathHooksHandled,
            MaxHp = enemy.MaxHp,
            Block = enemy.Block,
            CurrentIntent = enemy.CurrentIntent,
            SecondaryIntent = enemy.SecondaryIntent,
            StunReturnsToPreparedMove = enemy.StunReturnsToPreparedMove,
            ResumePreparedMoveAfterStun = enemy.ResumePreparedMoveAfterStun,
            Buffs = [.. enemy.Buffs],
            LastSpawnedDefId = enemy.LastSpawnedDefId,
            PoweredHitsThisTurn = enemy.PoweredHitsThisTurn,
            CurlUpCardSourceId = enemy.CurlUpCardSourceId,
            SkittishFirstHitCommand = enemy.SkittishFirstHitCommand,
            SkittishFirstHitUnblocked = enemy.SkittishFirstHitUnblocked,
            HardenedShellHpLostThisSide = enemy.HardenedShellHpLostThisSide,
            PossessedPlayerStrength = enemy.PossessedPlayerStrength,
            PossessedPlayerDexterity = enemy.PossessedPlayerDexterity,
            MoveIndex = enemy.MoveIndex,
            SteamEruptionPhase = enemy.SteamEruptionPhase,
            SteamEruptionDamage = enemy.SteamEruptionDamage,
            LastMove = enemy.LastMove,
            LastMoveRepeats = enemy.LastMoveRepeats,
            OnceOnlyMoveUsed = enemy.OnceOnlyMoveUsed,
            MoveHistory = [.. enemy.MoveHistory],
            StartsOnBranch = enemy.StartsOnBranch,
            RollsNextMove = enemy.RollsNextMove,
            NemesisIntangibleOn = enemy.NemesisIntangibleOn,
            StarterMove = enemy.StarterMove,
            Slot = enemy.Slot,
            StolenGold = enemy.StolenGold,
            StolenCard = enemy.StolenCard,
            Escaped = enemy.Escaped,
            OffBalance = enemy.OffBalance,
            LastBranch = enemy.LastBranch,
            RepeatStreak = enemy.RepeatStreak,
            BranchCooldown = enemy.BranchCooldown,
        };

    public static PendingCardSelection Clone(this PendingCardSelection selection) =>
        new()
        {
            Kind = selection.Kind,
            Candidates = [.. selection.Candidates],
            SourceCardDefId = selection.SourceCardDefId,
            SourcePotionId = selection.SourcePotionId,
            Amount = selection.Amount,
            GeneratedCandidates = [.. selection.GeneratedCandidates],
            AfterSelectionToHand = [.. selection.AfterSelectionToHand],
            Selected = [.. selection.Selected],
            Skippable = selection.Skippable,
            GeneratedUpgraded = selection.GeneratedUpgraded,
            GeneratedRetained = selection.GeneratedRetained,
        };

    public static PendingCardPlay Clone(this PendingCardPlay play) =>
        new()
        {
            Card = play.Card,
            EnergyToSpend = play.EnergyToSpend,
            EnergySpent = play.EnergySpent,
            Resources = play.Resources,
            FeralReturn = play.FeralReturn,
            MonologueBefore = play.MonologueBefore,
            ReboundBefore = play.ReboundBefore,
            NostalgiaReturnsToDraw = play.NostalgiaReturnsToDraw,
            ExtraPlays = play.ExtraPlays,
            PlayIndex = play.PlayIndex,
            BlockSourceId = play.BlockSourceId,
            BlockPlay = play.BlockPlay,
            EffectTail = play.EffectTail,
            RuptureStrength = play.RuptureStrength,
            TargetEnemyIndex = play.TargetEnemyIndex,
            PlayTarget = play.PlayTarget,
            PlayerHpBefore = play.PlayerHpBefore,
            EnemyHpsBefore = [.. play.EnemyHpsBefore],
            OblivionBefore = [.. play.OblivionBefore],
            PlayEnemyHps = [.. play.PlayEnemyHps],
        };

    /// <summary>
    /// Copy a combat, optionally RESEEDING every random stream it owns.
    /// </summary>
    /// <remarks>
    /// Without a seed this is a faithful copy: same streams, same seeds, same call counts,
    /// so the copy plays out exactly as the original would. That is what the differential
    /// tests want and must not change.
    ///
    /// With one, each stream is rebuilt from the new seed and fast-forwarded to the SAME
    /// call count -- different values, identical positions. That is the latent half of a
    /// determinization: every roll still to come (enemy moves, card generation, targeting,
    /// orb and HP rolls) resolves differently in each copy, which is what a search over an
    /// imperfect-information game needs and a plain clone cannot give it. `RunState.Clone`
    /// has taken a `stringSeed` for this reason; combat had no equivalent.
    ///
    /// It does NOT touch the draw pile. That order was materialised by a shuffle that has
    /// already happened, so reseeding cannot undo it -- see `ReshuffleUnknownDrawOrder`.
    /// </remarks>
    public static CombatState Clone(this CombatState combat, int? rngSeed = null)
    {
        var copy = new CombatState
        {
            AscensionLevel = combat.AscensionLevel,
            Character = combat.Character,
            Progression = combat.Progression,
            PlayerHp = combat.PlayerHp,
            PlayerMaxHp = combat.PlayerMaxHp,
            PlayerBlock = combat.PlayerBlock,
            Energy = combat.Energy,
            MaxEnergy = combat.MaxEnergy,
            PlayerGold = combat.PlayerGold,
            StolenBackGold = combat.StolenBackGold,
            RoyaltiesGold = combat.RoyaltiesGold,
            FatGremlinEscaped = combat.FatGremlinEscaped,
            BattlewornDummyRanOutOfTime = combat.BattlewornDummyRanOutOfTime,
            PaelsLegionCooldown = combat.PaelsLegionCooldown,
            MercGoldWasStolen = combat.MercGoldWasStolen,
            Hand = [.. combat.Hand],
            DrawPile = [.. combat.DrawPile],
            DiscardPile = [.. combat.DiscardPile],
            ExhaustPile = [.. combat.ExhaustPile],
            ReturnToHandBeforeDraw = [.. combat.ReturnToHandBeforeDraw],
            CopiesToHandBeforeDraw = [.. combat.CopiesToHandBeforeDraw],
            EndTurnAwaitingSelection = combat.EndTurnAwaitingSelection,
            PendingTurnSetup = combat.PendingTurnSetup?.Clone(),
            AllTargetDamageDepth = combat.AllTargetDamageDepth,
            DeferCardRupture = combat.DeferCardRupture,
            CardRuptureStrength = combat.CardRuptureStrength,
            CompletedCardRuptureStrength = combat.CompletedCardRuptureStrength,
            PendingEffectTail = combat.PendingEffectTail,
            AfterimageBeforePlay = combat.AfterimageBeforePlay,
            StormBeforePlay = combat.StormBeforePlay,
            SubroutineBeforePlay = combat.SubroutineBeforePlay,
            StatusCardsDrawnThisTurn = combat.StatusCardsDrawnThisTurn,
            ResolvingDefendCard = combat.ResolvingDefendCard,
            PlayedCardCostForCombat = combat.PlayedCardCostForCombat,
            AutoPlayQueue = [.. combat.AutoPlayQueue],
            Orbs = [.. combat.Orbs],
            OrbCapacity = combat.OrbCapacity,
            BaseOrbSlots = combat.BaseOrbSlots,
            OstyHp = combat.OstyHp,
            OstyMaxHp = combat.OstyMaxHp,
            Stars = combat.Stars,
            OrbitEnergySpent = combat.OrbitEnergySpent,
            StrangleBeforePlay = [.. combat.StrangleBeforePlay],
            SerpentFormSnapshots = [.. combat.SerpentFormSnapshots],
            PotionSlots = (int[])combat.PotionSlots.Clone(),
            MaxPotionSlots = combat.MaxPotionSlots,
            Relics = [.. combat.Relics],
            PlayerBuffs = [.. combat.PlayerBuffs],
            PlayerDebuffsAtRoundStart = [.. combat.PlayerDebuffsAtRoundStart],
            Enemies = [.. combat.Enemies.Select(enemy => enemy.Clone())],
            EncounterId = combat.EncounterId,
            IsEliteCombat = combat.IsEliteCombat,
            ShuffleRng = combat.ShuffleRng.Reseeded(rngSeed),
            TargetRng = combat.TargetRng.Reseeded(rngSeed),
            CardSelectionRng = combat.CardSelectionRng.Reseeded(rngSeed),
            EnergyCostRng = combat.EnergyCostRng.Reseeded(rngSeed),
            CardGenerationRng = combat.CardGenerationRng.Reseeded(rngSeed),
            PotionGenerationRng = combat.PotionGenerationRng.Reseeded(rngSeed),
            OrbGenerationRng = combat.OrbGenerationRng.Reseeded(rngSeed),
            // A CountingRandom carries its seed and position, so it clones exactly. A
            // plain Random does not, and only the combat-only environment supplies one
            // -- that path has no clone export, so the shared reference is unreachable.
            AiRng = combat.AiRng is CountingRandom counting
                ? counting.Reseeded(rngSeed)
                : combat.AiRng,
            NicheHpRng = combat.NicheHpRng.Reseeded(rngSeed),
            PendingSelection = combat.PendingSelection?.Clone(),
            PendingCardPlay = combat.PendingCardPlay?.Clone(),
            PendingPotionAutoPlays = combat.PendingPotionAutoPlays?.Clone(),
            PendingNestedAutoPlays = [.. combat.PendingNestedAutoPlays.Select(n => n.Clone())],
            AutoPlaying = combat.AutoPlaying,
            AutoPlayMayPause = combat.AutoPlayMayPause,
            PlayedCardBonusDamage = combat.PlayedCardBonusDamage,
            PlayedCardMomentumDamage = combat.PlayedCardMomentumDamage,
            PlayedCardBonusBlock = combat.PlayedCardBonusBlock,
            EnergySpentThisTurn = combat.EnergySpentThisTurn,
            PenNibArmed = combat.PenNibArmed,
            EtherealExhaustsThisTurn = combat.EtherealExhaustsThisTurn,
            VambraceSpent = combat.VambraceSpent,
            VambraceTriggerSourceId = combat.VambraceTriggerSourceId,
            PaelsLegionAffectedPlayId = combat.PaelsLegionAffectedPlayId,
            UnblockedDamageThisTurn = combat.UnblockedDamageThisTurn,
            RainbowRingAttacks = combat.RainbowRingAttacks,
            RainbowRingSkills = combat.RainbowRingSkills,
            RainbowRingPowers = combat.RainbowRingPowers,
            RainbowRingPaidThisTurn = combat.RainbowRingPaidThisTurn,
            UnsettlingLampSpent = combat.UnsettlingLampSpent,
            UnsettlingLampCard = combat.UnsettlingLampCard,
            GamblingChipDiscarded = combat.GamblingChipDiscarded,
            GamblingChipOwed = combat.GamblingChipOwed,
            BurningSticksUsed = combat.BurningSticksUsed,
            BeltBuckleApplied = combat.BeltBuckleApplied,
            IsEliteRoom = combat.IsEliteRoom,
            TookUnblockedDamage = combat.TookUnblockedDamage,
            PlayedCardEnchantSpent = combat.PlayedCardEnchantSpent,
            PlayedCardEnchantGrew = combat.PlayedCardEnchantGrew,
            PlayedCardCostBump = combat.PlayedCardCostBump,
            ToricToughnessBlock = combat.ToricToughnessBlock,
            Turn = combat.Turn,
            RoundNumber = combat.RoundNumber,
            PlayerTurn = combat.PlayerTurn,
            LastAttackThisTurn = combat.LastAttackThisTurn,
            LastAttackLastTurn = combat.LastAttackLastTurn,
            SkillPlayedWhileSmoggy = combat.SkillPlayedWhileSmoggy,
            CardsPlayedThisTurn = combat.CardsPlayedThisTurn,
            AttackCardsPlayedThisTurn = combat.AttackCardsPlayedThisTurn,
            AttackPlaysStartedThisTurn = combat.AttackPlaysStartedThisTurn,
            AttackOrSkillPlaysStartedThisTurn = combat.AttackOrSkillPlaysStartedThisTurn,
            CardPlaysStartedThisTurn = combat.CardPlaysStartedThisTurn,
            AttackOrSkillCardsPlayedThisTurn = combat.AttackOrSkillCardsPlayedThisTurn,
            CardPlaysThisTurn = combat.CardPlaysThisTurn,
            CardsPlayedThisCombat = combat.CardsPlayedThisCombat,
            DrawnCardsSinceAutomationProc = combat.DrawnCardsSinceAutomationProc,
            CardsDrawnThisCombat = combat.CardsDrawnThisCombat,
            ShivsPlayedThisTurn = combat.ShivsPlayedThisTurn,
            ExtraCardRewards = combat.ExtraCardRewards,
            StolenBackCards = [.. combat.StolenBackCards],
            TracksRunDeck = combat.TracksRunDeck,
            StolenFromDeck = [.. combat.StolenFromDeck],
            ExecutedThisEnemyTurn = [.. combat.ExecutedThisEnemyTurn],
            AutoPlayTargetIndex = combat.AutoPlayTargetIndex,
            CardsPlayedSincePanacheProc = combat.CardsPlayedSincePanacheProc,
            BlockGainsThisTurn = combat.BlockGainsThisTurn,
            NextBlockPlayIdentity = combat.NextBlockPlayIdentity,
            ActiveBlockPlay = combat.ActiveBlockPlay,
            // Enemies keep the id of the attack command that last hit them (Skittish),
            // so a clone must continue the sequence rather than restart it.
            LastAttackCommand = combat.LastAttackCommand,
            ActiveCardResources = combat.ActiveCardResources,
            BlockHistoryEpoch = combat.BlockHistoryEpoch,
            BlockHistoryTurn = combat.BlockHistoryTurn,
            BlockHistoryPlayerTurn = combat.BlockHistoryPlayerTurn,
            PlayerHpLostThisTurn = combat.PlayerHpLostThisTurn,
            CardsExhaustedThisTurn = combat.CardsExhaustedThisTurn,
            CardsDiscardedThisTurn = combat.CardsDiscardedThisTurn,
            LightningOrbsChanneledThisCombat = combat.LightningOrbsChanneledThisCombat,
            EtherealExhaustCount = combat.EtherealExhaustCount,
            EtherealCardPlaysThisCombat = combat.EtherealCardPlaysThisCombat,
            LethalAttackPlay = combat.LethalAttackPlay,
            SkillCardsPlayedThisTurn = combat.SkillCardsPlayedThisTurn,
            BoundCardsThisTurn = combat.BoundCardsThisTurn,
            BoundCardPlayedThisTurn = combat.BoundCardPlayedThisTurn,
            MusicBoxUsedThisTurn = combat.MusicBoxUsedThisTurn,
            FurCoatBites = combat.FurCoatBites,
            WhisperingEarringSpent = combat.WhisperingEarringSpent,
            CardPlaysLastTurn = combat.CardPlaysLastTurn,
            ExtraCardsDrawnThisTurn = combat.ExtraCardsDrawnThisTurn,
            DoomAppliedThisTurn = combat.DoomAppliedThisTurn,
            OstyAttacksThisTurn = combat.OstyAttacksThisTurn,
            StarsGainedThisTurn = combat.StarsGainedThisTurn,
            PlayedCardStarsSpent = combat.PlayedCardStarsSpent,
            VoidFormCardsPlayedThisTurn = combat.VoidFormCardsPlayedThisTurn,
            EndTurnAfterPlay = combat.EndTurnAfterPlay,
            CardsGeneratedThisCombat = combat.CardsGeneratedThisCombat,
            TenderCardsPlayedThisTurn = combat.TenderCardsPlayedThisTurn,
            SlothCardsPlayedThisTurn = combat.SlothCardsPlayedThisTurn,
            UnblockedDamageHitCount = combat.UnblockedDamageHitCount,
            TargetEnemyIndex = combat.TargetEnemyIndex,
            KnownTopCount = combat.KnownTopCount,
            KnownBottomCount = combat.KnownBottomCount,
        };

        if (combat.HexApplier is { } hexApplier)
        {
            int index = combat.Enemies.IndexOf(hexApplier);
            copy.HexApplier = index >= 0 ? copy.Enemies[index] : hexApplier.Clone();
        }
        if (combat.ShrinkApplier is { } shrinkApplier)
        {
            int index = combat.Enemies.IndexOf(shrinkApplier);
            copy.ShrinkApplier = index >= 0 ? copy.Enemies[index] : shrinkApplier.Clone();
        }
        // A parked play's target by identity: the copy's own creature, or a detached copy
        // of one that has already left the roster, never whoever now holds its index.
        EnemyState Map(EnemyState enemy)
        {
            int index = combat.Enemies.IndexOf(enemy);
            return index >= 0 ? copy.Enemies[index] : enemy.Clone();
        }

        void MapParked(ParkedAutoPlay parkedChild)
        {
            if (parkedChild.PlayTarget is { } childTarget)
                parkedChild.PlayTarget = Map(childTarget);
            parkedChild.DeathBaseline = [.. parkedChild.DeathBaseline.Select(Map)];
        }

        if (copy.PendingPotionAutoPlays?.Parked is { } parkedChild)
            MapParked(parkedChild);
        foreach (var nested in copy.PendingNestedAutoPlays)
            MapParked(nested.Child);

        if (copy.PendingCardPlay is { PlayTarget: { } playTarget } copiedPlay)
        {
            int index = combat.Enemies.IndexOf(playTarget);
            copiedPlay.PlayTarget = index >= 0 ? copy.Enemies[index] : playTarget.Clone();
        }
        if (combat.PendingEnemyTurn is { } pending)
        {
            copy.PendingEnemyTurn = new PendingEnemyTurn
            {
                PlayerHpBefore = pending.PlayerHpBefore,
                EnemyHpsBefore = [.. pending.EnemyHpsBefore],
                EnemyDamageBanked = pending.EnemyDamageBanked,
                RemainingEnemies = pending
                    .RemainingEnemies.Select(enemy =>
                    {
                        int index = combat.Enemies.IndexOf(enemy);
                        return index >= 0 ? copy.Enemies[index] : enemy.Clone();
                    })
                    .ToArray(),
            };
        }
        return copy;
    }

    public static RunMapNode Clone(this RunMapNode node)
    {
        var copy = new RunMapNode(node.Col, node.Row)
        {
            NodeType = node.NodeType,
            EncounterId = node.EncounterId,
            CanBeModified = node.CanBeModified,
        };
        copy.Children.AddRange(node.Children);
        copy.Parents.AddRange(node.Parents);
        return copy;
    }

    /// <summary>
    /// A RunRngSet is twelve streams off one seed, so it rebuilds from the seed and
    /// then fast-forwards each stream to where the original had got to.
    /// </summary>
    public static RunRngSet CloneAt(string stringSeed, RunRngSet source)
    {
        var copy = new RunRngSet(stringSeed);
        if (stringSeed == source.StringSeed)
        {
            copy.UpFront.Restore(source.UpFront.Snapshot());
            copy.Shuffle.Restore(source.Shuffle.Snapshot());
            copy.UnknownMapPoint.Restore(source.UnknownMapPoint.Snapshot());
            copy.CombatCardGeneration.Restore(source.CombatCardGeneration.Snapshot());
            copy.CombatPotionGeneration.Restore(source.CombatPotionGeneration.Snapshot());
            copy.CombatCardSelection.Restore(source.CombatCardSelection.Snapshot());
            copy.CombatEnergyCosts.Restore(source.CombatEnergyCosts.Snapshot());
            copy.CombatTargets.Restore(source.CombatTargets.Snapshot());
            copy.MonsterAi.Restore(source.MonsterAi.Snapshot());
            copy.Niche.Restore(source.Niche.Snapshot());
            copy.CombatOrbs.Restore(source.CombatOrbs.Snapshot());
            copy.TreasureRoomRelics.Restore(source.TreasureRoomRelics.Snapshot());
            return copy;
        }
        // A different seed explicitly requests a search determinization, not a clone.
        copy.UpFront.AdvanceToCallCount(source.UpFront.CallCount);
        copy.Shuffle.AdvanceToCallCount(source.Shuffle.CallCount);
        copy.UnknownMapPoint.AdvanceToCallCount(source.UnknownMapPoint.CallCount);
        copy.CombatCardGeneration.AdvanceToCallCount(source.CombatCardGeneration.CallCount);
        copy.CombatPotionGeneration.AdvanceToCallCount(source.CombatPotionGeneration.CallCount);
        copy.CombatCardSelection.AdvanceToCallCount(source.CombatCardSelection.CallCount);
        copy.CombatEnergyCosts.AdvanceToCallCount(source.CombatEnergyCosts.CallCount);
        copy.CombatTargets.AdvanceToCallCount(source.CombatTargets.CallCount);
        copy.MonsterAi.AdvanceToCallCount(source.MonsterAi.CallCount);
        copy.Niche.AdvanceToCallCount(source.Niche.CallCount);
        copy.CombatOrbs.AdvanceToCallCount(source.CombatOrbs.CallCount);
        copy.TreasureRoomRelics.AdvanceToCallCount(source.TreasureRoomRelics.CallCount);
        return copy;
    }

    public static PlayerRngSet CloneAt(RunRngSet runRng, PlayerRngSet source)
    {
        var copy = new PlayerRngSet(runRng);
        if (copy.Seed == source.Seed)
        {
            copy.Rewards.Restore(source.Rewards.Snapshot());
            copy.Shops.Restore(source.Shops.Snapshot());
            copy.Transformations.Restore(source.Transformations.Snapshot());
            return copy;
        }
        copy.Rewards.AdvanceToCallCount(source.Rewards.CallCount);
        copy.Shops.AdvanceToCallCount(source.Shops.CallCount);
        copy.Transformations.AdvanceToCallCount(source.Transformations.CallCount);
        return copy;
    }

    /// <summary>
    /// A deep copy of the run. Pass a different <paramref name="stringSeed" /> to
    /// rebuild every stream off a new seed, which resamples everything the run has
    /// not yet paid out -- rewards, shop stock, encounter composition, shuffles --
    /// while leaving what has already happened exactly as it was.
    /// </summary>
    public static RunState Clone(this RunState state, string? stringSeed = null)
    {
        var rng = CloneAt(stringSeed ?? state.StringSeed, state.Rng);
        var eventRng = state.EventRngStream?.Clone();
        var mapNodes = new Dictionary<(int Col, int Row), RunMapNode>(state.MapNodes.Count);
        foreach (var (coord, node) in state.MapNodes)
        {
            mapNodes[coord] = node.Clone();
        }

        return new RunState
        {
            StringSeed = state.StringSeed,
            AscensionLevel = state.AscensionLevel,
            Character = state.Character,
            Progression = state.Progression,
            Rng = rng,
            PlayerRng = CloneAt(rng, state.PlayerRng),
            PlayerHp = state.PlayerHp,
            PlayerMaxHp = state.PlayerMaxHp,
            Gold = state.Gold,
            Floor = state.Floor,
            Phase = state.Phase,
            Deck = [.. state.Deck],
            Relics = [.. state.Relics],
            UsedUpRelics = [.. state.UsedUpRelics],
            MaxPotionSlots = state.MaxPotionSlots,
            CardRewardRerollAvailable = state.CardRewardRerollAvailable,
            CardRewardOptions = state.CardRewardOptions,
            DisplayedCardReward = state.DisplayedCardReward is { } displayed
                ? displayed with
                {
                    Cards = [.. displayed.Cards],
                }
                : null,
            CardRewardSubscriptionSequence = state.CardRewardSubscriptionSequence,
            OpenedCardRewardIndex = state.OpenedCardRewardIndex,
            LastingCandyRewardCard = state.LastingCandyRewardCard,
            LastingCandyRewardUpgraded = state.LastingCandyRewardUpgraded,
            SuspendedRewardScreens =
            [
                .. state.SuspendedRewardScreens.Select(screen => screen.DeepCopy()),
            ],
            CardRemovalsOwed = state.CardRemovalsOwed,
            RoyaltiesGoldOwed = state.RoyaltiesGoldOwed,
            RestOptionsTaken = state.RestOptionsTaken,
            TookUnblockedDamageThisCombat = state.TookUnblockedDamageThisCombat,
            RewardEnchantIndex = state.RewardEnchantIndex,
            RewardEnchantment = state.RewardEnchantment,
            PendingBonusRelicRewards = [.. state.PendingBonusRelicRewards],
            WaxRelics = [.. state.WaxRelics],
            FurCoatRooms = [.. state.FurCoatRooms],
            FurCoatAct = state.FurCoatAct,
            GoldenPathAct = state.GoldenPathAct,
            SpoilsMapQuestCoord = state.SpoilsMapQuestCoord,
            ParasolSweepPending = state.ParasolSweepPending,
            LavaRockTriggered = state.LavaRockTriggered,
            PendingSpecialCardReward = state.PendingSpecialCardReward,
            PendingSpecialCardRewards = [.. state.PendingSpecialCardRewards],
            PendingEventCombatRewards = [.. state.PendingEventCombatRewards],
            EventLayoutEncounterId = state.EventLayoutEncounterId,
            EventLayoutEnemyHps = [.. state.EventLayoutEnemyHps],
            ResumeEventId = state.ResumeEventId,
            ResumeEventPage = state.ResumeEventPage,
            ResumeEventDummyEscaped = state.ResumeEventDummyEscaped,
            PendingNeowsBonesCurse = state.PendingNeowsBonesCurse,
            PendingHeftyTabletCurse = state.PendingHeftyTabletCurse,
            PendingPotionRewards = [.. state.PendingPotionRewards],
            PotionSlots = (int[])state.PotionSlots.Clone(),
            CurrentNodeType = state.CurrentNodeType,
            NeowOptions = (int[])state.NeowOptions.Clone(),
            RewardCards = (int[])state.RewardCards.Clone(),
            RewardGold = state.RewardGold,
            RewardPotion = state.RewardPotion,
            RewardCardPending = state.RewardCardPending,
            NeowAwaitingProceed = state.NeowAwaitingProceed,
            ReturnToRewardScreenAfterCardReward = state.ReturnToRewardScreenAfterCardReward,
            MapNodeTypes = (int[])state.MapNodeTypes.Clone(),
            MapChoices = (int[])state.MapChoices.Clone(),
            ShopCards = (int[])state.ShopCards.Clone(),
            ShopRelics = (int[])state.ShopRelics.Clone(),
            ShopPotions = (int[])state.ShopPotions.Clone(),
            ShopCosts = (int[])state.ShopCosts.Clone(),
            ShopBaseCosts = (int[])state.ShopBaseCosts.Clone(),
            RewardUpgraded = (bool[])state.RewardUpgraded.Clone(),
            RelicReward = state.RelicReward,
            PendingTreasureRelic = state.PendingTreasureRelic,
            TreasureChestOpened = state.TreasureChestOpened,
            SilverCrucibleTreasureRoomsEntered = state.SilverCrucibleTreasureRoomsEntered,
            EventId = state.EventId,
            EventValue0 = state.EventValue0,
            EventValue1 = state.EventValue1,
            EventAmount0 = state.EventAmount0,
            EventAmount1 = state.EventAmount1,
            SlipperyBridgeSkippedRemovals = [.. state.SlipperyBridgeSkippedRemovals],
            ActiveCombat = state.ActiveCombat?.Clone(),
            ActiveCombatRng = state.ActiveCombatRng?.Clone(),
            LastPlayerWon = state.LastPlayerWon,
            CompletedCombatRoomsBeforeCurrent = state.CompletedCombatRoomsBeforeCurrent,
            MapNodes = mapNodes,
            CurrentMapCoord = state.CurrentMapCoord,
            MapOptionCoords = ((int Col, int Row)?[])state.MapOptionCoords.Clone(),
            // The act list carries the sequences and the boss for EVERY act, and the
            // per-act properties are views on it — so cloning the list is what clones
            // them. Copying the views instead would silently drop acts 2 and 3.
            Acts =
            [
                .. state.Acts.Select(act =>
                    act with
                    {
                        Events = (int[])act.Events.Clone(),
                        NormalEncounters = (int[])act.NormalEncounters.Clone(),
                        EliteEncounters = (int[])act.EliteEncounters.Clone(),
                    }
                ),
            ],
            CurrentActIndex = state.CurrentActIndex,
            SeaGlassCharacter = state.SeaGlassCharacter,
            DustyTomeAncientCard = state.DustyTomeAncientCard,
            NormalEncountersVisited = state.NormalEncountersVisited,
            EliteEncountersVisited = state.EliteEncountersVisited,
            EventSequenceIndex = state.EventSequenceIndex,
            CardRarityOffset = state.CardRarityOffset,
            PotionRewardOdds = state.PotionRewardOdds,
            PendingRelicReward = state.PendingRelicReward,
            ShopRemovalsUsed = state.ShopRemovalsUsed,
            ShopRemovalUsedThisVisit = state.ShopRemovalUsedThisVisit,
            PendingShopRemovalCost = state.PendingShopRemovalCost,
            TransformSelectedDeckIndex = state.TransformSelectedDeckIndex,
            PendingSelectionKind = state.PendingSelectionKind,
            PendingSelectionArg = state.PendingSelectionArg,
            PendingSelectionCount = state.PendingSelectionCount,
            PendingOfferCards = (int[])state.PendingOfferCards.Clone(),
            PendingOfferInstances = (CardInstance[])state.PendingOfferInstances.Clone(),
            BundleOffer = (int[])state.BundleOffer.Clone(),
            SelectedBundle = state.SelectedBundle,
            BundleReturnsToRewards = state.BundleReturnsToRewards,
            PendingOfferPicks = state.PendingOfferPicks,
            PendingOfferOptional = state.PendingOfferOptional,
            EventPage = state.EventPage,
            CrystalSphere = state.CrystalSphere?.Clone(),
            CrystalSphereRng = ReferenceEquals(state.CrystalSphereRng, state.EventRngStream)
                ? eventRng
                : state.CrystalSphereRng?.Clone(),
            EventRngStream = eventRng,
            EventRngName = state.EventRngName,
            EventRelicStock = state.EventRelicStock is null ? null : [.. state.EventRelicStock],
            FakeMerchantPrices = state.FakeMerchantPrices is null
                ? null
                : [.. state.FakeMerchantPrices],
            EventRelicOffers = state.EventRelicOffers is null ? null : [.. state.EventRelicOffers],
            AddingBingBongClone = state.AddingBingBongClone,
            GuiltyCombatsServed = [.. state.GuiltyCombatsServed],
            VisitedEventIds = [.. state.VisitedEventIds],
            PendingSelectionEventEntry = state.PendingSelectionEventEntry,
            PendingGoldRewards = [.. state.PendingGoldRewards],
            PendingCardOffers =
            [
                .. state.PendingCardOffers.Select(offer => offer with { Cards = [.. offer.Cards] }),
            ],
            PendingSelectionFollowUpCard = state.PendingSelectionFollowUpCard,
            PendingSelectionFollowUpCount = state.PendingSelectionFollowUpCount,
            PendingSelectionFollowUpHpLoss = state.PendingSelectionFollowUpHpLoss,
            PendingSelectionFollowUpGoldLoss = state.PendingSelectionFollowUpGoldLoss,
            PendingSelectionFollowUpMaxHpGain = state.PendingSelectionFollowUpMaxHpGain,
            PendingSelectionReturnsToEvent = state.PendingSelectionReturnsToEvent,
            PendingSelectionReturn = state.PendingSelectionReturn,
            EventRandomOffer = (int[])state.EventRandomOffer.Clone(),
            TinkerCardType = state.TinkerCardType,
            RelicBag = state.RelicBag.Clone(),
            SharedRelicBag = state.SharedRelicBag.Clone(),
            PendingRestUpgrade = state.PendingRestUpgrade,
            RestResultPending = state.RestResultPending,
            UnknownMapPointsVisited = state.UnknownMapPointsVisited,
            UnknownMapPointMonsterOdds = state.UnknownMapPointMonsterOdds,
            UnknownMapPointEliteOdds = state.UnknownMapPointEliteOdds,
            UnknownMapPointTreasureOdds = state.UnknownMapPointTreasureOdds,
            UnknownMapPointShopOdds = state.UnknownMapPointShopOdds,
            LastResolvedRoomType = state.LastResolvedRoomType,
        };
    }

    /// <summary>
    /// Shuffle the draw pile into an order the agent has not been shown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of a combat determinization. <see cref="Clone(CombatState, int?)" />
    /// reseeds every stream, which changes every roll STILL TO COME, but the draw order is
    /// not a future roll -- it was materialised by a shuffle that already happened, and a
    /// reseed cannot undo it. Without this, a search that forks the position reads the true
    /// order off its own copy and plays each fight with foresight.
    /// </para>
    /// <para>
    /// Only the UNKNOWN region moves. <see cref="CombatState.KnownTopCount" /> and
    /// <see cref="CombatState.KnownBottomCount" /> exist for exactly this -- cards the
    /// player has been shown, in order, at each end of the pile -- so the shuffle runs
    /// over the middle and leaves both ends where they are. Shuffling through them
    /// resamples information the agent legitimately holds, which is not a determinization
    /// but a corruption: the engine goes on believing the top N are the cards it put
    /// there, and acts on cards that are no longer where it left them.
    /// </para>
    /// <para>
    /// Deliberately not folded into <see cref="Clone(CombatState, int?)" />: a plain clone
    /// is a faithful copy and the differential tests depend on that, seed or no seed.
    /// </para>
    /// </remarks>
    public static void ReshuffleUnknownDrawOrder(this CombatState combat, Random rng)
    {
        int start = combat.KnownTopCount;
        int end = combat.DrawPile.Count - combat.KnownBottomCount;
        // Fisher-Yates over [start, end), the region nothing has been shown.
        for (int i = end - 1; i > start; i--)
        {
            int j = start + rng.Next(i - start + 1);
            (combat.DrawPile[i], combat.DrawPile[j]) = (combat.DrawPile[j], combat.DrawPile[i]);
        }
    }
}
