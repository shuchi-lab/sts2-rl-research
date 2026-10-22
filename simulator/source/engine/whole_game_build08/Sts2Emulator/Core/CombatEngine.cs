namespace Sts2Emulator.Core;

// Action encoding:
//   0..hand.Count-1  → play card at that hand index (targeting first enemy, or TargetEnemyIndex)
//   hand.Count       → end turn
//   hand.Count+1..   → use potion at slot (index - hand.Count - 1)
//
// While state.PendingSelection is open the encoding is replaced entirely:
//   0..Candidates.Count-1 → answer the card-selection screen with that candidate

public static class CombatEngine
{
    public static StepResult Step(
        CombatState state,
        int action,
        Random rng,
        int targetEnemyIndex = -1
    )
    {
        state.TargetEnemyIndex = targetEnemyIndex;

        // Anything queued before the player has acted goes off FIRST. Imbued's turn-1
        // auto-play is the only thing that queues at combat start, and it fires in the
        // game's pre-play phase — before the player's first move, not after it. The loop
        // further down drains what an action ITSELF queues; this drains what was already
        // waiting.
        // Normally a no-op: the combat's creator resolves the pre-play phase so the FIRST
        // observation already shows it. Kept here for states built without going through an
        // entry point -- the suite queues auto-plays by hand -- and idempotent either way.
        ResolvePrePlayPhase(state, rng);

        int endTurnAction = state.Hand.Count;
        StepResult result;

        // A pending card selection owns the action space until it is answered: the game
        // will not let you play, end the turn or quaff while its selection screen is up.
        //
        // This used to RETURN rather than fall through, which put anything the answer
        // queued a whole action late. Answering a discard screen with a Sly card queues it
        // to play, and the game plays it as part of that discard -- not after whatever the
        // player does next.
        if (state.PendingSelection is not null)
        {
            result = ResolveCardSelection(state, action, rng);
        }
        else if (action == endTurnAction)
        {
            result = EndTurn(state, rng);
        }
        else if (action < endTurnAction)
        {
            result = PlayCard(state, action, rng);
        }
        else
        {
            int potionSlot = action - endTurnAction - 1;
            result = UsePotion(state, potionSlot, rng);
        }

        // Auto-plays use first-living enemy, not the explicit target.
        state.TargetEnemyIndex = -1;

        // Process auto-plays (e.g. from Hellraiser). Not while a chosen play is parked on
        // a selection: the card that queued them has not finished, and the answer that
        // resumes it drains them through this same loop.
        while (
            state.AutoPlayQueue.Count > 0
            && !result.Terminal
            && state.PendingCardPlay is null
            && state.PendingTurnSetup is null
            && state.PendingPotionAutoPlays is null
        )
        {
            var next = state.AutoPlayQueue[0];
            state.AutoPlayQueue.RemoveAt(0);
            AutoPlay(state, next, rng);

            // Re-check terminality after auto-play.
            bool playerDead = PlayerIsDead(state);
            bool allDead = NoPrimaryEnemyLeft(state);
            if (playerDead || allDead)
            {
                result = result with { Terminal = true, PlayerWon = allDead && !playerDead };
            }
        }

        // An explicit auto-play target belongs to the queue that was given it and nothing
        // after: Knife Trap aims its Shivs, and the next Hellraiser must roll its own.
        state.AutoPlayTargetIndex = -1;

        return result;
    }

    private static StepResult PlayCard(
        CombatState state,
        int handIndex,
        Random rng,
        CardResourceOrigin origin = CardResourceOrigin.Manual
    )
    {
        var card = state.Hand[handIndex];
        var def = card.ModelDef();

        ResolveCosts(state, card, def, out int energyToSpend, out int starsToSpend);

        // One predicate, shared with `ValidActions`. These two used to answer the question
        // separately and disagreed in BOTH directions -- see `CanPlayFromHand`.
        if (!CanPlayFromHand(state, card))
        {
            return StepResult.Invalid;
        }

        // An admitted energy-X card pays its X HERE, in `SpendResources`' order: capture,
        // history, deduction, then AfterEnergySpent -- all before any hook or effect. An
        // unadmitted one keeps paying zero now and spending inside its own body.
        bool capturesEnergyX = def.HasEnergyCostX && EnergyXMigration.UsesCapturedEnergy(def.Id);
        int energyPaid = capturesEnergyX ? state.Energy : energyToSpend;

        // `EnergySpentEntry` amounts, summed over the turn. Helix Drill reads it and
        // subtracts its own cost, which is zero -- so the count excludes the drill itself
        // either way, and this runs before the card resolves.
        state.EnergySpentThisTurn += energyPaid;
        bool feralReturn =
            def.Type == CardType.Attack
            && energyToSpend == 0
            && BuffSystem.Get(state.PlayerBuffs, BuffId.Feral)
                > BuffSystem.Get(state.PlayerBuffs, BuffId.FeralUsed);

        // Snapshot HP before effects.
        int playerHpBefore = state.PlayerHp;
        Span<int> enemyHpsBefore = stackalloc int[state.Enemies.Count];
        for (int i = 0; i < state.Enemies.Count; i++)
        {
            enemyHpsBefore[i] = state.Enemies[i].Hp;
        }

        state.Energy -= energyPaid;
        // `SpendResources` takes the stars AFTER the energy, and records what it took --
        // an X-star card reads that back, because by the time its effect runs the counter
        // is already zero.
        state.Stars = Math.Max(0, state.Stars - starsToSpend);
        state.PlayedCardStarsSpent = starsToSpend;

        // `Hook.AfterStarsSpent`, dispatched from `CardModel.SpendStars` and from nowhere
        // else -- paying a card's star COST is the only thing that counts as spending, so
        // a card that burns the counter inside its own effect does not pay this.
        int childOfTheStars = BuffSystem.Get(state.PlayerBuffs, BuffId.ChildOfTheStars);
        if (starsToSpend > 0 && childOfTheStars > 0)
        {
            Effects.CardEffects.GainBlock(state, childOfTheStars * starsToSpend, rng);
        }

        if (starsToSpend > 0)
        {
            Effects.RelicEffects.ApplyAfterStarsSpent(state, starsToSpend, rng);
        }
        // What this play actually cost, which is what CardPlay.Resources.EnergyValue
        // reports: an X card is printed at zero and takes the rest of the bar inside its
        // own effect, so the printed cost would tell a relic the play was free.
        // The admitted path has already emptied the bar, so it must not re-read it.
        int energySpent =
            capturesEnergyX ? energyPaid
            : def.HasEnergyCostX ? state.Energy
            : energyToSpend;
        // `SpendEnergy` dispatches AfterEnergySpent here, before the card resolves.
        Effects.CardEffects.ApplyAfterEnergySpent(state, energySpent);
        // Whispering Earring autoplays with skipXCapture after paying, so its X is the
        // payment while its play's EnergyValue is `GetAmountToSpend()` on what is left --
        // read after AfterEnergySpent, as the game builds its ResourceInfo then.
        var resources =
            !capturesEnergyX ? default
            : origin == CardResourceOrigin.PaidAuto
                ? CardResourceContext.PaidAutoPayment(energyPaid, state.Energy)
            : CardResourceContext.ManualPayment(energyPaid);
        state.Hand.RemoveAt(handIndex);
        // Result location is fixed after spending resources and entering Play, before
        // BeforeCardPlayed hooks or repeat settlement can start any nested plays.
        bool nostalgiaReturnsToDraw = CaptureNostalgiaDestination(state, def);

        // `DanseMacabrePower.BeforeCardPlayed`: a card whose RESOLVED cost is 2 or more
        // gains its amount in Unpowered block. Resolved, so a card the player got for free
        // pays nothing -- the resolved cost, not the printed one. Reads the CLAMPED cost,
        // which is the same test at this threshold: the clamp only moves values below zero.
        int danseMacabre = BuffSystem.Get(state.PlayerBuffs, BuffId.DanseMacabre);
        if (danseMacabre > 0 && energyToSpend >= 2)
        {
            Effects.CardEffects.GainBlock(state, danseMacabre, rng);
        }

        // `SpiritOfAshPower.BeforeCardPlayed`: an ETHEREAL card played gains its amount in
        // Unpowered block. Its var is called BlockOnExhaust and the hook has nothing to do
        // with exhausting.
        int spiritOfAsh = BuffSystem.Get(state.PlayerBuffs, BuffId.SpiritOfAsh);
        if (spiritOfAsh > 0 && IsEtherealForPowers(state, card))
        {
            Effects.CardEffects.GainBlock(state, spiritOfAsh, rng);
        }

        // `TheSealedThronePower.BeforeCardPlayed`: a star for every card its owner plays.
        int sealedThrone = BuffSystem.Get(state.PlayerBuffs, BuffId.TheSealedThrone);
        if (sealedThrone > 0)
        {
            Effects.CardEffects.GainStars(state, sealedThrone);
        }

        // `LethalityPower` counts the turn's Attack plays and pays out only while that
        // count is still one. The counter is bumped after the play, so during resolution
        // it still holds the turn's earlier attacks -- zero means this is the first.
        state.LethalAttackPlay =
            def.Type == CardType.Attack && state.AttackCardsPlayedThisTurn == 0;

        // Its own power, not part of the chain above: `VeilpiercerPower.BeforeCardPlayed`
        // fires on any Ethereal card, so an Ethereal Attack spends a stack of this AND a
        // stack of FreeAttackPower.
        if (
            BuffSystem.Get(state.PlayerBuffs, BuffId.Veilpiercer) > 0
            && IsEtherealForPowers(state, card)
        )
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Veilpiercer, -1);
        }

        // `MonologuePower.BeforeCardPlayed` records the power's amount for THIS card, and
        // AfterCardPlayed pays what was recorded. Snapshotted for the same reason Oblivion's
        // is: the power does not exist yet while the card that applies it is resolving, so
        // Monologue never pays for its own play.
        int monologueBefore = BuffSystem.Get(state.PlayerBuffs, BuffId.Monologue);

        // `OblivionPower.BeforeCardPlayed` writes the current amount into a per-card
        // dictionary, and AfterCardPlayed spends THAT. Snapshotted here for the same
        // reason Serpent Form is: the power records what it was worth when the play began.
        Span<int> oblivionBefore = stackalloc int[state.Enemies.Count];
        for (int i = 0; i < state.Enemies.Count; i++)
        {
            oblivionBefore[i] = BuffSystem.Get(state.Enemies[i].Buffs, BuffId.Oblivion);
        }

        // `ReboundPower` redirects a card bound for the discard to the top of the draw
        // pile and is spent doing it. Snapshotted for the same reason Oblivion and
        // Monologue are: the power does not exist yet while the card that APPLIES it is
        // resolving, so Rebound never redirects its own play. The live capture is
        // unambiguous -- the game leaves Rebound in the discard and the draw pile at 6.
        int reboundBefore = BuffSystem.Get(state.PlayerBuffs, BuffId.Rebound);

        // CardModel.GeneratePlayCount settles and spends every repeat modifier
        // before the first resolution. A card cannot double itself by applying one.
        int extraPlays = SettlePlayCount(state, card, def);

        // OnPlayWrapper closes each play before beginning its replay. Reuse a
        // stack buffer for ordinary rosters; a summon can grow it when necessary.
        Span<int> playEnemyHps =
            stackalloc int[Math.Max(state.Enemies.Count, CombatObservation.MaxEnemies)];
        var frame = new CardPlayFrame
        {
            Card = card,
            EnergyToSpend = energyToSpend,
            EnergySpent = energySpent,
            Resources = resources,
            FeralReturn = feralReturn,
            MonologueBefore = monologueBefore,
            ReboundBefore = reboundBefore,
            NostalgiaReturnsToDraw = nostalgiaReturnsToDraw,
            ExtraPlays = extraPlays,
            BlockSourceId = CardBlockHistory.NewSource(state),
            PlayerHpBefore = playerHpBefore,
            EnemyHpsBefore = enemyHpsBefore,
            OblivionBefore = oblivionBefore,
            PlayEnemyHps = playEnemyHps,
        };
        return RunCardPlays(state, ref frame, def, rng, resumingAfterEffect: false);
    }

    /// <summary>
    /// `CardModel.GeneratePlayCount` (CardModel.cs:2029): `GetEnchantedReplayCount() + 1`,
    /// then every `ModifyCardPlayCount` listener, then `AfterModifyingCardPlayCount` on the
    /// ones that changed it -- which is where Duplication, Burst, One-Two Punch, Signal
    /// Boost and Throwing Axe are SPENT. Returns the plays beyond the first.
    /// </summary>
    /// <remarks>
    /// Once per physical activation: a chosen play, an auto-play, a nested play (Havoc),
    /// and Drum of Battle's exhaust, which is the one caller outside `OnPlayWrapper`. It
    /// starts no CardPlay; the callers do that once per repeat.
    /// </remarks>
    internal static int SettlePlayCount(CombatState state, CardInstance card, CardDef def)
    {
        // `BurstPower.ModifyCardPlayCount` is OneTwoPunch's rule for SKILLS -- but the
        // play count is settled when the play is SET UP, before the card resolves, which
        // is why a Burst does not double itself. Read here for that reason; the emulator
        // used to stack OneTwoPunch instead, which doubled Attacks rather than Skills.
        int burstPlays =
            def.Type == CardType.Skill && BuffSystem.Get(state.PlayerBuffs, BuffId.Burst) > 0
                ? 1
                : 0;
        // `EchoFormPower.ModifyCardPlayCount` is settled at the same moment Burst's is --
        // when the play is SET UP, before the card resolves -- so Echo Form does not double
        // ITSELF. Read after the resolution it doubled the card that applied it, which is
        // exactly the mistake the comment above was written about.
        //
        // The game's count is `CardPlaysStarted ... HappenedThisTurn` NOT counting the play
        // being set up, which is what CardPlaysThisTurn holds here: it is bumped after the
        // card resolves.
        int echoFormPlays =
            state.CardPlaysThisTurn < BuffSystem.Get(state.PlayerBuffs, BuffId.EchoForm) ? 1 : 0;

        // `ThrowingAxe.ModifyCardPlayCount` doubles the first card played in the combat.
        // Read HERE, with Burst and Echo Form, because all three are settled when the play
        // is set up -- and consumed here too, since the game's
        // `AfterModifyingCardPlayCount` fires at that same moment.
        int throwingAxePlays = Effects.RelicEffects.ConsumeThrowingAxePlay(state) ? 1 : 0;

        if (burstPlays > 0)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Burst, -1);
        }

        int extraPlays = echoFormPlays;

        // Spiral.EnchantPlayCount(original) => original + Times, and Times is 1.
        extraPlays += card.EnchantedWith(Enchantment.Spiral);

        // Glam.EnchantPlayCount is the same +Times(1), but it burns out: `AfterCardPlayed`
        // sets UsedThisCombat and the enchantment goes Disabled. `EnchantSpent` is the
        // field Vigorous already uses for exactly that once-only shape.
        if (card.Enchantment == Enchantment.Glam && !card.EnchantSpent)
        {
            extraPlays += card.EnchantAmount;
        }

        // Hidden Gem's Replay rides on the copy it was granted to, the same way.
        extraPlays += card.ReplayCount;
        extraPlays += burstPlays;
        extraPlays += throwingAxePlays;
        int signalBoost = BuffSystem.Get(state.PlayerBuffs, BuffId.SignalBoost);
        if (def.Type == CardType.Power && signalBoost > 0)
        {
            extraPlays++;
            if (signalBoost == 1)
            {
                BuffSystem.Remove(state.PlayerBuffs, BuffId.SignalBoost);
            }
            else
            {
                BuffSystem.Apply(state.PlayerBuffs, BuffId.SignalBoost, -1);
            }
        }

        // `DuplicationPower.ModifyCardPlayCount` adds one play to ANY card and then
        // `AfterModifyingCardPlayCount` decrements -- so it is spent by the card that used
        // it, not by the turn. Outside the Attack branch below on purpose: that branch is
        // `OneTwoPunchPower`, which is a DIFFERENT power of the same shape that refuses
        // anything but an Attack. Stacking one for the other is E490.
        int duplication = BuffSystem.Get(state.PlayerBuffs, BuffId.Duplication);
        if (duplication > 0)
        {
            extraPlays++;
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Duplication, -1);
        }

        if (def.Type == CardType.Attack)
        {
            int oneTwoPunch = BuffSystem.Get(state.PlayerBuffs, BuffId.OneTwoPunch);
            if (oneTwoPunch > 0)
            {
                extraPlays++;
                if (oneTwoPunch == 1)
                {
                    BuffSystem.Remove(state.PlayerBuffs, BuffId.OneTwoPunch);
                }
                else
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.OneTwoPunch, -1);
                }
            }
        }

        return extraPlays;
    }

    /// <summary>
    /// `Hook.BeforeCardPlayed` for the listeners that care about a CardPlay STARTING, once
    /// per play of a repeated card, in every play path -- chosen, auto-played, nested.
    /// </summary>
    /// <remarks>
    /// `JugglingPower.BeforeCardPlayed` (JugglingPower.cs:36-52): the turn's third own
    /// Attack start adds `Amount` clones through `AddGeneratedCardToCombat(Hand)`. The
    /// played card is in the Play pile by then, so it is not in hand, and the clone is
    /// there before its OnPlay runs. The power's `AfterApplied` seeds its count from
    /// `CardPlaysStarted` this turn, which is this counter.
    /// </remarks>
    internal static void StartCardPlay(CombatState state, CardInstance played, CardDef def)
    {
        if (def.Type is CardType.Attack or CardType.Skill)
            state.AttackOrSkillPlaysStartedThisTurn++;
        if (def.Type != CardType.Attack)
            return;
        state.AttackPlaysStartedThisTurn++;
        if (state.AttackPlaysStartedThisTurn != 3)
            return;
        int juggling = BuffSystem.Get(state.PlayerBuffs, BuffId.Juggling);
        for (int i = 0; i < juggling; i++)
        {
            Effects.CardEffects.AddGeneratedCardToHandOrDiscard(
                state,
                Effects.CardEffects.CloneForCombat(played)
            );
        }
    }

    // ── One CardPlay: the shared per-iteration boundary ───────────────────────────
    //
    // `CardModel.OnPlayWrapper` (CardModel.cs:1896-1968) runs, for each of the playCount
    // iterations that `GeneratePlayCount` settled once for the physical card:
    //   BeforeCardPlayed (1926) -> CardPlayStarted (1929) -> OnPlay, awaited (1933-1934)
    //   -> Enchantment.OnPlay (1942) -> Affliction.OnPlay (1952) -> CardPlayFinished (1961)
    //   -> AfterCardPlayed (1965)
    // and only after the loop moves the card to its result pile. Every play path -- chosen,
    // resumed, hand auto-play, queued auto-play, nested -- goes through these three steps,
    // with the card's effect between the first two.

    /// <summary>
    /// The start of one CardPlay: a new block-history identity under the physical card's
    /// source, then BeforeCardPlayed -- powers (Juggling via <see cref="StartCardPlay" />)
    /// before relics, as `IterateHookListeners` orders them.
    /// </summary>
    /// <param name="played">The card as it stands now, for anything that copies it.</param>
    /// <param name="chosen">
    /// Whether this is a chosen play. `CardsPlayedThisTurn` is the chosen-play count its
    /// readers (Brilliant Scarf skips `IsAutoPlay`) expect.
    /// </param>
    private static void BeginCardPlayIteration(
        CombatState state,
        CardInstance played,
        CardDef def,
        int sourceId,
        int energyToSpend,
        bool chosen,
        Random rng
    )
    {
        CardBlockHistory.BeginPlay(state, sourceId);
        // Free-card powers consume once per CardPlay, including replays and autoplay.
        // Payment happened once before this loop; a replay can consume a stack
        // granted by the preceding iteration (duplicated Unrelenting).
        if (
            def.Type == CardType.Attack
            && BuffSystem.Get(state.PlayerBuffs, BuffId.FreeAttackPower) > 0
        )
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.FreeAttackPower, -1);
        }
        else if (
            def.Type == CardType.Skill
            && BuffSystem.Get(state.PlayerBuffs, BuffId.FreeSkillPower) > 0
        )
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.FreeSkillPower, -1);
        }
        else if (
            def.Type == CardType.Power
            && BuffSystem.Get(state.PlayerBuffs, BuffId.FreePowerPower) > 0
        )
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.FreePowerPower, -1);
        }

        // SurroundedPower.BeforeCardPlayed also observes automatic and repeated
        // plays. The actual target is already selected on every path here.
        TurnPlayerTowardTarget(state, played);
        StartCardPlay(state, played, def);
        if (chosen)
            state.CardsPlayedThisTurn++;
        // SlothPower.BeforeCardPlayed: every CardPlay of the owner's, chosen or not.
        if (BuffSystem.Get(state.PlayerBuffs, BuffId.Sloth) > 0)
            state.SlothCardsPlayedThisTurn++;
        // `ChainsOfBindingPower.BeforeCardPlayed` latches on every CardPlay of a BOUND
        // card, chosen or not, before its OnPlay -- so a Bound Burning Pact waiting on its
        // exhaust choice already refuses the other Bound cards. The power skips `IsDupe`;
        // History Course's is the only dupe and its encore is played unbound.
        if (played.Bound)
            state.BoundCardPlayedThisTurn = true;
        Effects.RelicEffects.ApplyBeforeCardPlayedRares(state, energyToSpend, rng);
        CaptureBeforePlayPowers(state);
        // `SerpentFormPower.BeforeCardPlayed` records its amount against this CardPlay.
        state.SerpentFormSnapshots.Add(BuffSystem.Get(state.PlayerBuffs, BuffId.SerpentForm));
        Effects.RelicEffects.BeforeCardPlayedRelics(state, def);
        // CardModel.cs:1926-1933 records CardPlayStarted AFTER BeforeCardPlayed
        // listeners, and immediately before OnPlay can draw/nest another card.
        state.CardPlaysStartedThisTurn++;
    }

    /// <summary>
    /// What follows the card's own OnPlay in the same CardPlay: `Enchantment.OnPlay` on
    /// every play, then attack-card after-effects (attack count, Rage and Calamity).
    /// Attack-command bonuses are consumed by the damage helpers before the card tail.
    /// </summary>
    private static void AfterCardPlayEffect(
        CombatState state,
        CardInstance card,
        CardDef def,
        int playIndex,
        Random rng
    )
    {
        ApplyEnchantmentOnPlay(state, card, rng);

        // CardModel.OnPlayWrapper records History.CardPlayFinished after the awaited
        // effect and enchantment, once per iteration, before AfterCardPlayed. Winning
        // the fight does not skip that history row; the owner's death does.
        if (!PlayerIsDead(state))
            IncrementPlayedCardTypeCounters(state, def, InFlightCopy(state, card));

        if (def.Type != CardType.Attack)
            return;
        // Bolas and Thrumming Hatchet move one physical card before the next draw if ANY
        // of its plays finished last turn. Repeats do not create copies.
        if (playIndex == 0)
            QueueAttackPlayLifecycleEffects(state, card);
        state.AttackCardsPlayedThisTurn++;
        int rage = BuffSystem.Get(state.PlayerBuffs, BuffId.Rage);
        if (rage > 0)
            // RagePower grants Unpowered block: Dexterity and Frail do not apply.
            Effects.CardEffects.GainUnpoweredBlock(state, rage, rng);
        int calamity = BuffSystem.Get(state.PlayerBuffs, BuffId.CalamityPower);
        if (calamity > 0)
            Effects.CardEffects.AddRandomAttackCardsToHand(state, calamity, rng);
    }

    /// <summary>
    /// The end of one CardPlay: AfterCardPlayed -- Rupture's accrued Strength, the powers'
    /// and relics' hooks -- and then the block-history completion. The general terminal
    /// dispatch is still omitted here; IronClub, Nunchaku and TuningFork count separately
    /// for a surviving owner's winning iteration. The source dispatches AfterCardPlayed
    /// for that iteration, so broader terminal-hook parity remains a separate follow-up.
    /// </summary>
    private static void CompleteCardPlayIteration(
        CombatState state,
        CardDef def,
        int energySpent,
        Random rng
    )
    {
        Effects.CardEffects.PayCompletedCardRupture(state);
        PaySerpentForm(state, rng);
        ApplyAfterCardPlayedPowers(state, def, rng, energySpent);
        Effects.RelicEffects.ApplyAfterCardPlayedRares(state, def, rng);
        ApplyAfterCardPlayedEnemyPowers(state, def);
        CardBlockHistory.AfterCardPlayed(state);
        // `UnsettlingLamp.AfterCardPlayed` closes the latch on the claiming card's FIRST
        // CardPlay: OnPlayWrapper runs AfterCardPlayed once per play, so a One-Two Punch
        // or Burst repeat applies its debuff undoubled (ABB71XX step0532: Bash+ twice
        // gives Vulnerable 6 + 3 = 9, not 12).
        Effects.RelicEffects.FinishUnsettlingLampCard(state);
    }

    private static int TakeSerpentFormSnapshot(CombatState state)
    {
        var records = state.SerpentFormSnapshots;
        if (records.Count == 0)
            return 0;
        int amount = records[^1];
        records.RemoveAt(records.Count - 1);
        return amount;
    }

    /// <summary>
    /// `SerpentFormPower.AfterCardPlayed` (SerpentFormPower.cs:81-93): the amount this
    /// CardPlay recorded at BeforeCardPlayed, as Unpowered damage to a random hittable
    /// enemy on the CombatTargets stream -- once per play, so an Echo Form repeat pays
    /// again. A play that began before the power existed recorded 0 and pays nothing,
    /// which is why the Serpent Form that applies it does not trigger it.
    /// </summary>
    private static void PaySerpentForm(CombatState state, Random rng)
    {
        int amount = TakeSerpentFormSnapshot(state);
        if (amount <= 0)
            return;
        var target = Effects.CardEffects.RandomLivingEnemyFor(state, rng);
        if (target != null)
            Effects.CardEffects.DealUnpoweredDamage(state, target, amount);
    }

    /// <summary>
    /// `CardCmd.AutoPlay` -> `OnPlayWrapper(isAutoPlay: true)` for every automatic path:
    /// Stampede's hand auto-play, the auto-play queue, Hellraiser's nested draw play and
    /// Havoc's nested play. Settles the play count once, runs each CardPlay through the
    /// shared steps, and returns the physical card with what its plays handed back, for
    /// the caller to place once.
    /// </summary>
    /// <remarks>
    /// The per-play hand-back channels (`PlayedCard*`) and Rupture's completed total belong
    /// to whichever play is resolving, and an automatic play can run inside another play's
    /// effect. They are saved, cleared for this card, folded into the returned copy, and
    /// the caller's restored. Deaths the effect caused are resolved right after it.
    /// </remarks>
    internal static CardInstance RunAutomaticCardPlays(
        CombatState state,
        CardInstance card,
        CardDef def,
        Random rng
    )
    {
        var caller = PlayedCardChannels.Take(state);
        var callerTarget = state.PlayTarget;
        try
        {
            PinPlayTarget(state, def);
            // `CardCmd.AutoPlay` captures an X card's value from the bar as it stands and
            // spends nothing -- once per physical activation, before the play count is
            // settled, and every repeat resolves that capture.
            var resources = AutoPlayResources(state, def, card);
            int extraPlays = SettlePlayCount(state, card, def);
            int sourceId = CardBlockHistory.NewSource(state);
            for (int play = 0; play <= extraPlays; play++)
            {
                if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
                    break;
                BeginCardPlayIteration(
                    state,
                    InFlightCopy(state, card),
                    def,
                    sourceId,
                    energyToSpend: resources.ResourceEnergyValue,
                    chosen: false,
                    rng
                );
                // `CreatureCmd.Damage` awaits Kill/AfterDeath inside the effect, so a
                // creature this play kills has had its death hooks (Surprise's gremlins,
                // Gremlin Horn) before the effect that nested this play goes on -- before
                // Pommel Strike's next draw. The baseline is object identity, taken just
                // before the effect; the dispatch is the phase owner's once-only one.
                var deathBaseline = CaptureLivingEnemiesForDeath(state);
                state.ActiveCardResources = resources;
                Effects.CardEffects.Apply(
                    def,
                    card.Upgraded,
                    state,
                    rng,
                    InFlightCopy(state, card)
                );
                ResolveAwaitedEnemyDeaths(state, deathBaseline, rng);
                AfterCardPlayEffect(state, card, def, play, rng);
                if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
                {
                    // A terminal play discards its unused per-play snapshot.
                    TakeSerpentFormSnapshot(state);
                    if (state.PlayerHp > 0)
                    {
                        Effects.RelicEffects.ApplyIronClubAfterCardPlayed(state, rng);
                        Effects.RelicEffects.ApplyNunchakuAfterCardPlayed(state, def);
                        Effects.RelicEffects.ApplyTuningForkAfterCardPlayed(state, def, rng);
                    }
                    break;
                }
                CompleteCardPlayIteration(
                    state,
                    def,
                    energySpent: resources.ResourceEnergyValue,
                    rng
                );
            }

            // SetUntilPlayed -- and this was the play, so it is spent. An automatic play
            // still records `CardPlayFinished`, so the copy is a played card too (Fetch,
            // Thrumming Hatchet and Bolas read that history about this copy).
            return InFlightCopy(state, card) with
            {
                FreeUntilPlayed = false,
                CostOverrideThisTurn = int.MinValue,
                PlayedThisTurn = true,
            };
        }
        finally
        {
            caller.Restore(state);
            state.PlayTarget = callerTarget;
        }
    }

    /// <summary>
    /// Fixes <see cref="CombatState.PlayTarget" /> for one activation, resolved the way a
    /// single-target effect has always resolved it -- the explicit target when it is
    /// living, else the first living enemy -- but once, not per repeat. Anything but an
    /// AnyEnemy card clears it and keeps the index behaviour.
    /// </summary>
    private static void PinPlayTarget(CombatState state, CardDef def)
    {
        state.PlayTarget = null;
        if (Effects.CardEffects.EffectiveTarget(state, def) == CardTarget.AnyEnemy)
            state.PlayTarget = Effects.CardEffects.TargetedEnemyFor(state);
    }

    /// <summary>
    /// A nested automatic play whose caller places the card itself. Finished-play history
    /// is recorded by the shared per-iteration lifecycle, before completion hooks.
    /// </summary>
    /// <remarks>
    /// `CardCmd.AutoPlay` asks `Hook.ShouldPlay` before anything else it does; a card
    /// Ringing refuses is handed straight back unplayed, and the caller's placement is
    /// `MoveToResultPileWithoutPlaying`'s -- Havoc's forced exhaust, discard otherwise.
    /// </remarks>
    internal static CardInstance PlayAutomaticCardInPlace(
        CombatState state,
        CardInstance card,
        CardDef def,
        Random rng
    )
    {
        if (IsBlockedByRinging(state) || IsBlockedBySloth(state))
            return card;
        // Every nested caller (Havoc via `AutoPlayFromDrawPile`, Uproar, the draw-pile
        // attack plays) passes `CardCmd.AutoPlay` a null target, so an AnyEnemy card rolls
        // `Rng.CombatTargets.NextItem(HittableEnemies)` after `ShouldPlay` (CardCmd.cs:73-78)
        // -- the same roll `AutoPlay` makes for the queue. Without it the card inherited the
        // enclosing play's target, the first enemy for a Self card like Havoc, and drew
        // nothing. `AutoPlayTargetIndex` is the queued caller's given target (Knife Trap)
        // and is not read here: no nested caller gives one.
        int targetIndex =
            Effects.CardEffects.EffectiveTarget(state, def) == CardTarget.AnyEnemy
                ? RandomAutoPlayTargetIndex(state, rng)
                : -1;
        int callerTarget = state.TargetEnemyIndex;
        state.TargetEnemyIndex = targetIndex;
        try
        {
            return RunAutomaticCardPlays(state, card, def, rng);
        }
        finally
        {
            state.TargetEnemyIndex = callerTarget;
        }
    }

    /// <summary>
    /// <see cref="PlayAutomaticCardInPlace" /> for Havoc's forced-exhaust child when the
    /// play enclosing Havoc can itself park. The child runs through the Distilled Chaos
    /// child's parkable loop (<see cref="RunPotionBatchChild" />), so a selection it raises
    /// is the player's -- `CardSelectCmd.FromHand` has no auto-play exception.
    /// </summary>
    /// <returns>
    /// True when the child parked: it is on <see cref="CombatState.PendingNestedAutoPlays" />
    /// and the enclosing effect owes <see cref="EffectTailKind.ResumeNestedAutoPlay" />,
    /// which also places it. Otherwise the finished copy is in <paramref name="played" />
    /// for the caller to exhaust, as the in-place path hands it back.
    /// </returns>
    internal static bool PlayAwaitedForceExhaustChild(
        CombatState state,
        CardInstance card,
        CardDef def,
        Random rng,
        out CardInstance played
    )
    {
        played = card;
        if (IsBlockedByRinging(state) || IsBlockedBySloth(state))
            return false;

        var callerPlayTarget = state.PlayTarget;
        var child = new ParkedAutoPlay
        {
            Card = card,
            CallerTargetIndex = state.TargetEnemyIndex,
            CallerAutoPlayTargetIndex = state.AutoPlayTargetIndex,
        };
        state.AutoPlayTargetIndex = -1;
        // The same null-target roll as PlayAutomaticCardInPlace (CardCmd.cs:73-78).
        child.TargetIndex =
            Effects.CardEffects.EffectiveTarget(state, def) == CardTarget.AnyEnemy
                ? RandomAutoPlayTargetIndex(state, rng)
                : -1;
        state.TargetEnemyIndex = child.TargetIndex;
        child.CallerChannels = PlayedCardChannels.Take(state);
        PinPlayTarget(state, def);
        child.PlayTarget = state.PlayTarget;
        child.Resources = AutoPlayResources(state, def, card);
        child.ExtraPlays = SettlePlayCount(state, card, def);
        child.SourceId = CardBlockHistory.NewSource(state);

        if (RunPotionBatchChild(state, child, def, resuming: false, rng))
        {
            var nested = new PendingNestedAutoPlay
            {
                Child = child,
                ChildTail = TakePendingEffectTail(state),
            };
            ParkNestedChild(state, nested, callerPlayTarget);
            return true;
        }

        played = FinishNestedChild(state, child, callerPlayTarget);
        return false;
    }

    /// <summary>
    /// The enclosing effect's owed <see cref="EffectTailKind.ResumeNestedAutoPlay" />: the
    /// outermost parked child picks up in its own context; its owed tail first resumes
    /// any inner child, then it finishes its plays and is force-exhausted. A child that asks again
    /// parks again and the tail stays owed.
    /// </summary>
    private static void ResumeNestedAutoPlay(CombatState state, Random rng)
    {
        var stack = state.PendingNestedAutoPlays;
        var nested = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        var child = nested.Child;
        var def = child.Card.ModelDef();

        var callerPlayTarget = state.PlayTarget;
        child.CallerTargetIndex = state.TargetEnemyIndex;
        child.CallerAutoPlayTargetIndex = state.AutoPlayTargetIndex;
        child.CallerChannels = PlayedCardChannels.Take(state);
        child.Channels.Restore(state);
        state.TargetEnemyIndex = child.TargetIndex;
        state.AutoPlayTargetIndex = -1;
        state.PlayTarget = child.PlayTarget;
        var callerBlockPlay = state.ActiveBlockPlay;
        state.ActiveBlockPlay = child.BlockPlay;
        state.PendingEffectTail = nested.ChildTail;
        bool parked = RunPotionBatchChild(state, child, def, resuming: true, rng);
        state.ActiveBlockPlay = callerBlockPlay;
        if (parked)
        {
            nested.ChildTail = TakePendingEffectTail(state);
            ParkNestedChild(state, nested, callerPlayTarget);
            return;
        }

        var played = FinishNestedChild(state, child, callerPlayTarget);
        // `forceExhaust: true` makes Exhaust the child's result pile.
        Effects.CardEffects.ExhaustCard(state, played, rng: rng);
    }

    private static void ParkNestedChild(
        CombatState state,
        PendingNestedAutoPlay nested,
        EnemyState? callerPlayTarget
    )
    {
        state.PendingNestedAutoPlays.Add(nested);
        state.PendingEffectTail = new EffectTail(EffectTailKind.ResumeNestedAutoPlay, 0);
        // RunPotionBatchChild's park clears the target for a top-level batch; the effect
        // that nested this child keeps its own.
        state.PlayTarget = callerPlayTarget;
    }

    /// <summary>RunAutomaticCardPlays' hand-back, once the child's plays are done.</summary>
    private static CardInstance FinishNestedChild(
        CombatState state,
        ParkedAutoPlay child,
        EnemyState? callerPlayTarget
    )
    {
        var played = InFlightCopy(state, child.Card) with
        {
            FreeUntilPlayed = false,
            CostOverrideThisTurn = int.MinValue,
            // A finished automatic play, as in RunAutomaticCardPlays.
            PlayedThisTurn = true,
        };
        child.CallerChannels.Restore(state);
        state.PlayTarget = callerPlayTarget;
        state.TargetEnemyIndex = child.CallerTargetIndex;
        state.AutoPlayTargetIndex = child.CallerAutoPlayTargetIndex;
        return played;
    }

    /// <summary>
    /// Runs what a parked effect still owes. A nested automatic play is the engine's to
    /// finish; every other shape is the effect's own remainder.
    /// </summary>
    private static void RunOwedEffectTail(CombatState state, EffectTail tail, Random rng)
    {
        if (tail.Kind == EffectTailKind.ResumeNestedAutoPlay)
            ResumeNestedAutoPlay(state, rng);
        else if (tail.Kind == EffectTailKind.HavocPlayTop)
            Effects.CardEffects.PlayHavocTop(state, rng, mayPause: true);
        else
            Effects.CardEffects.RunEffectTail(state, tail);
    }

    /// <summary>
    /// `CardCmd.AutoPlay` spends nothing but reports EnergyValue for relic hooks: the
    /// captured bar for admitted X cards, or the modified cost of a normal card.
    /// Capture once before BeforeCardPlayed consumes free-card powers, then preserve
    /// that value across repeats and paused plays. Unmigrated X bodies are unchanged.
    /// </summary>
    private static CardResourceContext AutoPlayResources(
        CombatState state,
        CardDef def,
        CardInstance card
    ) =>
        def.HasEnergyCostX
            ? EnergyXMigration.UsesCapturedEnergy(def.Id)
                ? CardResourceContext.FreeAutoCapture(state.Energy)
                : default
            : CardResourceContext.FreeAutoCost(EffectiveCost(card, state));

    /// <summary>
    /// The per-play hand-back channels an automatic play must not share with the play it
    /// runs inside. Types are the state's own; nothing is converted.
    /// </summary>
    internal readonly record struct PlayedCardChannels(
        decimal BonusDamage,
        int MomentumDamage,
        int BonusBlock,
        bool EnchantSpent,
        bool EnchantGrew,
        int CostBump,
        int CostForCombat,
        int CompletedRupture,
        CardResourceContext Resources
    )
    {
        public static PlayedCardChannels Take(CombatState state)
        {
            var saved = new PlayedCardChannels(
                state.PlayedCardBonusDamage,
                state.PlayedCardMomentumDamage,
                state.PlayedCardBonusBlock,
                state.PlayedCardEnchantSpent,
                state.PlayedCardEnchantGrew,
                state.PlayedCardCostBump,
                state.PlayedCardCostForCombat,
                state.CompletedCardRuptureStrength,
                state.ActiveCardResources
            );
            state.PlayedCardBonusDamage = 0;
            state.PlayedCardMomentumDamage = 0;
            state.PlayedCardBonusBlock = 0;
            state.PlayedCardEnchantSpent = false;
            state.PlayedCardEnchantGrew = false;
            state.PlayedCardCostBump = 0;
            state.PlayedCardCostForCombat = int.MinValue;
            state.CompletedCardRuptureStrength = 0;
            state.ActiveCardResources = default;
            return saved;
        }

        public void Restore(CombatState state)
        {
            state.PlayedCardBonusDamage = BonusDamage;
            state.PlayedCardMomentumDamage = MomentumDamage;
            state.PlayedCardBonusBlock = BonusBlock;
            state.PlayedCardEnchantSpent = EnchantSpent;
            state.PlayedCardEnchantGrew = EnchantGrew;
            state.PlayedCardCostBump = CostBump;
            state.PlayedCardCostForCombat = CostForCombat;
            state.CompletedCardRuptureStrength = CompletedRupture;
            state.ActiveCardResources = Resources;
        }
    }

    /// <summary>
    /// The chosen card as it stands mid-play: what earlier repeats handed back through the
    /// `PlayedCard*` channels is part of the model the game would clone.
    /// </summary>
    private static CardInstance InFlightCopy(CombatState state, CardInstance card) =>
        card with
        {
            BonusDamage = card.BonusDamage + state.PlayedCardBonusDamage,
            MomentumDamage = card.MomentumDamage + state.PlayedCardMomentumDamage,
            BonusBlock = card.BonusBlock + state.PlayedCardBonusBlock,
            EnchantSpent = card.EnchantSpent || state.PlayedCardEnchantSpent,
            EnchantAmount = card.EnchantAmount + (state.PlayedCardEnchantGrew ? 1 : 0),
            CostBump = card.CostBump + state.PlayedCardCostBump,
            CostForCombat =
                state.PlayedCardCostForCombat != int.MinValue
                    ? state.PlayedCardCostForCombat
                    : card.CostForCombat,
        };

    private static EffectTail TakePendingEffectTail(CombatState state)
    {
        var tail = state.PendingEffectTail;
        state.PendingEffectTail = default;
        return tail;
    }

    /// <summary>
    /// `CardCmd.AutoPlay` awaited from inside another effect (Hellraiser's draw). Returns
    /// false, leaving the card to its caller, when the combat is already over --
    /// `AutoPlay` returns early on `IsOverOrEnding`.
    /// </summary>
    /// <param name="targetIndex">
    /// An enemy the caller already rolled (Beat Down rolls `Rng.CombatTargets` in its own
    /// `OnPlay`); -1 lets AutoPlay roll for an AnyEnemy card as it does by default.
    /// </param>
    /// <param name="bindWhenPlaced">
    /// Hellraiser's draw: `ChainsOfBindingPower.AfterCardDrawn` binds the card once its
    /// play has put it somewhere, so the copy that lands carries Bound.
    /// </param>
    internal static bool AutoPlayNested(
        CombatState state,
        CardInstance card,
        Random rng,
        bool bindWhenPlaced = false,
        int targetIndex = -1
    )
    {
        if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
            return false;
        int callerTarget = state.TargetEnemyIndex;
        int callerAutoTarget = state.AutoPlayTargetIndex;
        state.AutoPlayTargetIndex = targetIndex;
        try
        {
            AutoPlay(state, card, rng, bindWhenPlaced);
        }
        finally
        {
            state.TargetEnemyIndex = callerTarget;
            state.AutoPlayTargetIndex = callerAutoTarget;
        }
        return true;
    }

    /// <summary>
    /// The locals of one chosen card play, from the moment its costs are paid and its play
    /// count is settled to the moment it lands in a pile. A ref struct so the ordinary
    /// play keeps its stack buffers; <see cref="SuspendCardPlay" /> copies it into a
    /// <see cref="PendingCardPlay" /> only when a selection actually parks the play.
    /// </summary>
    private ref struct CardPlayFrame
    {
        public CardInstance Card;
        public int EnergyToSpend;
        public int EnergySpent;
        public CardResourceContext Resources;
        public bool FeralReturn;
        public int MonologueBefore;
        public int ReboundBefore;
        public bool NostalgiaReturnsToDraw;
        public int ExtraPlays;
        public int Play;
        public int BlockSourceId;
        public BlockPlayContext BlockPlay;
        public int PlayerHpBefore;
        public Span<int> EnemyHpsBefore;
        public Span<int> OblivionBefore;
        public Span<int> PlayEnemyHps;
        public int PlayRosterCount;
    }

    /// <summary>
    /// `OnPlayWrapper`'s repeat loop: before-play hooks, the effect, and the after-play
    /// hooks, once per play -- and then the placement, once.
    /// </summary>
    /// <remarks>
    /// The effect's `OnPlay` is AWAITED before `Hook.AfterCardPlayed`, so an effect that
    /// raises a selection parks the play right there. <paramref name="resumingAfterEffect" />
    /// is the way back in: the parked play's effect has finished with the answer, so it
    /// picks up at that play's after-play hooks rather than playing the card again.
    /// </remarks>
    private static StepResult RunCardPlays(
        CombatState state,
        ref CardPlayFrame frame,
        CardDef def,
        Random rng,
        bool resumingAfterEffect
    )
    {
        var callerBlockPlay = state.ActiveBlockPlay;
        var callerResources = state.ActiveCardResources;
        var callerTarget = state.PlayTarget;
        try
        {
            // A resumed play keeps the target its PendingCardPlay carried, which the
            // resume has already put back; resolving again would move off a dead one.
            if (!resumingAfterEffect)
                PinPlayTarget(state, def);
            return RunCardPlaysCore(state, ref frame, def, rng, resumingAfterEffect);
        }
        finally
        {
            state.PlayTarget = callerTarget;
            state.ActiveBlockPlay = callerBlockPlay;
            state.ActiveCardResources = callerResources;
            // A play that ended the combat never reaches AfterCardPlayed; a parked one has
            // already moved its total onto the PendingCardPlay.
            state.CompletedCardRuptureStrength = 0;
        }
    }

    private static StepResult RunCardPlaysCore(
        CombatState state,
        ref CardPlayFrame frame,
        CardDef def,
        Random rng,
        bool resumingAfterEffect
    )
    {
        var card = frame.Card;
        for (; frame.Play <= frame.ExtraPlays; frame.Play++)
        {
            int rosterCount;
            if (resumingAfterEffect)
            {
                resumingAfterEffect = false;
                state.ActiveBlockPlay = frame.BlockPlay;
                rosterCount = frame.PlayRosterCount;
            }
            else
            {
                if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
                    break;
                rosterCount = state.Enemies.Count;
                if (rosterCount > frame.PlayEnemyHps.Length)
                    frame.PlayEnemyHps = new int[rosterCount];
                for (int i = 0; i < rosterCount; i++)
                    frame.PlayEnemyHps[i] = state.Enemies[i].Hp;
                frame.PlayRosterCount = rosterCount;

                BeginCardPlayIteration(
                    state,
                    InFlightCopy(state, card),
                    def,
                    frame.BlockSourceId,
                    frame.Resources.Captured
                        ? frame.Resources.ResourceEnergyValue
                        : frame.EnergyToSpend,
                    chosen: true,
                    rng
                );
                // Set before EVERY iteration: a nested automatic play inside the previous
                // one restores the caller in its own finally, and a repeat must resolve
                // this activation's capture, never a later one.
                state.ActiveCardResources = frame.Resources;
                var parentPrePass = state.AwaitedDeathPrePass;
                var prePass = new AwaitedDeathPrePass();
                state.AwaitedDeathPrePass = prePass;
                try
                {
                    Effects.CardEffects.Apply(
                        def,
                        card.Upgraded,
                        state,
                        rng,
                        InFlightCopy(state, card)
                    );
                }
                finally
                {
                    state.AwaitedDeathPrePass = parentPrePass;
                }
                state.LethalAttackPlay = false;

                // `branchingPlayerChoiceContext.AssignTaskAndWaitForPauseOrCompletion`: the
                // effect is waiting on the player. Nothing after it in the wrapper may run
                // until the answer is in -- a combat that is already over is the exception,
                // since `CardSelectCmd` returns nothing once `IsOverOrEnding`.
                if (
                    state.PendingSelection is not null
                    && !PlayerIsDead(state)
                    && !NoPrimaryEnemyLeft(state)
                )
                {
                    // No admitted energy-X card opens a selection (Whirlwind asks nothing;
                    // Cascade's children autoplay and resolve their own). A future pausing
                    // admission would need its capture restored before the pending effect
                    // tail runs, not only in ResumeCardPlay, so refuse it loudly.
                    if (frame.Resources.Captured)
                    {
                        throw new InvalidOperationException(
                            $"Admitted energy-X card {def.Name} paused; its resource context "
                                + "is not restored before a pending effect tail."
                        );
                    }

                    // `CreatureCmd.Damage` awaits a kill's AfterDeath inside OnPlay, so a
                    // creature the effect has already killed is dead -- and Stock's next
                    // Axebot standing -- before the selection is asked (DaggerThrow.OnPlay
                    // attacks, draws, then asks). Resolved here, against this play's
                    // pre-effect baseline; `DeathHooksHandled` keeps the resumed play's
                    // HandleEnemyDeaths from paying them twice. AfterCardPlayed and history
                    // still wait for the answer.
                    //
                    // The step's shaped reward is measured BEFORE this pass, while the
                    // roster still lines up with the pre-play baseline: the pass removes
                    // or replaces the dead, and comparing old indices to the new roster
                    // loses the kill. What the awaited hooks themselves do is added on top.
                    // An attack inside the effect may already have run that pass (its
                    // kill's hooks are awaited inside the hit); then the roster it saw
                    // just before is the one measured.
                    float pausedReward = prePass.Roster is { } prePassRoster
                        ? RewardAcrossAwaitedDeaths(
                            state,
                            prePassRoster,
                            prePass.Hps!,
                            frame.PlayerHpBefore,
                            frame.EnemyHpsBefore
                        )
                        : ComputeReward(
                            state,
                            playerDead: false,
                            allDead: false,
                            frame.PlayerHpBefore,
                            frame.EnemyHpsBefore
                        );
                    pausedReward += ResolveDeathsBeforePause(
                        state,
                        frame.PlayEnemyHps[..rosterCount],
                        rng
                    );
                    // The resumed pass then only sees deaths from the answered tail.
                    rosterCount = state.Enemies.Count;
                    if (rosterCount > frame.PlayEnemyHps.Length)
                        frame.PlayEnemyHps = new int[rosterCount];
                    for (int i = 0; i < rosterCount; i++)
                        frame.PlayEnemyHps[i] = state.Enemies[i].Hp;
                    frame.PlayRosterCount = rosterCount;
                    return SuspendCardPlay(state, ref frame, pausedReward);
                }
            }

            // After both branches, so it runs exactly once per CardPlay: at once for an
            // effect that finished, on resume for one that waited on a selection.
            AfterCardPlayEffect(state, card, def, frame.Play, rng);

            HandleEnemyDeaths(state, frame.PlayEnemyHps[..rosterCount], rng);
            if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
            {
                // A terminal play discards its unused per-play snapshot.
                TakeSerpentFormSnapshot(state);
                if (state.PlayerHp > 0)
                {
                    Effects.RelicEffects.ApplyIronClubAfterCardPlayed(state, rng);
                    Effects.RelicEffects.ApplyNunchakuAfterCardPlayed(state, def);
                    Effects.RelicEffects.ApplyTuningForkAfterCardPlayed(state, def, rng);
                }
                break;
            }
            // Death summons have their own HP, and after-play damage can kill
            // them. Do not dispatch the previous creature's death twice.
            rosterCount = state.Enemies.Count;
            if (rosterCount > frame.PlayEnemyHps.Length)
                frame.PlayEnemyHps = new int[rosterCount];
            for (int i = 0; i < rosterCount; i++)
                frame.PlayEnemyHps[i] = state.Enemies[i].Hp;
            CompleteCardPlayIteration(
                state,
                def,
                frame.Resources.Captured ? frame.Resources.ResourceEnergyValue : frame.EnergySpent,
                rng
            );
            HandleEnemyDeaths(state, frame.PlayEnemyHps[..rosterCount], rng);
        }

        return FinishCardPlay(state, ref frame, def, rng);
    }

    /// <summary>
    /// Runs the death pass a paused play owes before its screen, and returns the shaped
    /// reward for what that pass itself did: HP the awaited death hooks took from
    /// creatures that were already standing (matched by identity, not index) and from the
    /// player, over the roster as it stood before the pass. A creature the pass spawns --
    /// Stock's next Axebot -- has no "before" and is not damage. The paused play's
    /// resume re-baselines on the post-pass roster, so none of it is paid twice.
    /// </summary>
    private static float ResolveDeathsBeforePause(
        CombatState state,
        ReadOnlySpan<int> playEnemyHps,
        Random rng
    )
    {
        var standing = state.Enemies.ToArray();
        var hpBefore = standing.Select(enemy => enemy.Hp).ToArray();
        float totalMaxHp = standing.Sum(enemy => (float)enemy.MaxHp);
        int playerHpBefore = state.PlayerHp;

        HandleEnemyDeaths(state, playEnemyHps, rng);

        float dealt = 0f;
        for (int i = 0; i < standing.Length; i++)
            dealt += Math.Max(0, hpBefore[i] - standing[i].Hp);
        float taken = Math.Max(0, playerHpBefore - state.PlayerHp);
        return (totalMaxHp > 0f ? dealt / totalMaxHp : 0f) - taken / (float)state.PlayerMaxHp;
    }

    /// <summary>
    /// Parks the play whose effect just raised a selection. The step reports what the
    /// play has done so far, and the resume reports the rest from here.
    /// </summary>
    private static StepResult SuspendCardPlay(
        CombatState state,
        ref CardPlayFrame frame,
        float reward
    )
    {
        var enemyHpsNow = new int[state.Enemies.Count];
        for (int i = 0; i < enemyHpsNow.Length; i++)
            enemyHpsNow[i] = state.Enemies[i].Hp;

        state.PendingCardPlay = new PendingCardPlay
        {
            Card = frame.Card,
            EnergyToSpend = frame.EnergyToSpend,
            EnergySpent = frame.EnergySpent,
            Resources = frame.Resources,
            FeralReturn = frame.FeralReturn,
            MonologueBefore = frame.MonologueBefore,
            ReboundBefore = frame.ReboundBefore,
            NostalgiaReturnsToDraw = frame.NostalgiaReturnsToDraw,
            ExtraPlays = frame.ExtraPlays,
            PlayIndex = frame.Play,
            BlockSourceId = frame.BlockSourceId,
            BlockPlay = state.ActiveBlockPlay,
            EffectTail = TakePendingEffectTail(state),
            RuptureStrength = state.CompletedCardRuptureStrength,
            // Where the pinned target sits NOW: an earlier death may have shifted it.
            TargetEnemyIndex = state.PlayTarget is { } aimed
                ? state.Enemies.IndexOf(aimed)
                : state.TargetEnemyIndex,
            PlayTarget = state.PlayTarget,
            PlayerHpBefore = state.PlayerHp,
            EnemyHpsBefore = enemyHpsNow,
            OblivionBefore = frame.OblivionBefore.ToArray(),
            PlayEnemyHps = frame.PlayEnemyHps[..frame.PlayRosterCount].ToArray(),
        };
        return new StepResult(Terminal: false, PlayerWon: false, Reward: reward);
    }

    /// <summary>
    /// Picks a parked play back up once its selection has closed: the rest of the parked
    /// play's wrapper, any repeats still owed, and the placement.
    /// </summary>
    private static StepResult ResumeCardPlay(CombatState state, PendingCardPlay pending, Random rng)
    {
        var frame = new CardPlayFrame
        {
            Card = pending.Card,
            EnergyToSpend = pending.EnergyToSpend,
            EnergySpent = pending.EnergySpent,
            Resources = pending.Resources,
            FeralReturn = pending.FeralReturn,
            MonologueBefore = pending.MonologueBefore,
            ReboundBefore = pending.ReboundBefore,
            NostalgiaReturnsToDraw = pending.NostalgiaReturnsToDraw,
            ExtraPlays = pending.ExtraPlays,
            Play = pending.PlayIndex,
            BlockSourceId = pending.BlockSourceId,
            BlockPlay = pending.BlockPlay,
            PlayerHpBefore = pending.PlayerHpBefore,
            EnemyHpsBefore = pending.EnemyHpsBefore,
            OblivionBefore = pending.OblivionBefore,
            PlayEnemyHps = pending.PlayEnemyHps,
            PlayRosterCount = pending.PlayEnemyHps.Length,
        };
        var def = pending.Card.ModelDef();
        state.CompletedCardRuptureStrength = pending.RuptureStrength;
        return RunCardPlays(state, ref frame, def, rng, resumingAfterEffect: true);
    }

    /// <summary>
    /// The tail of `OnPlayWrapper` after the last play: the card leaves Play for its result
    /// pile, and the after-play bookkeeping that happens once per CARD rather than once per
    /// repeat.
    /// </summary>
    private static StepResult FinishCardPlay(
        CombatState state,
        ref CardPlayFrame frame,
        CardDef def,
        Random rng
    )
    {
        var card = frame.Card;
        bool feralReturn = frame.FeralReturn;
        int energyToSpend = frame.EnergyToSpend;
        int monologueBefore = frame.MonologueBefore;
        int reboundBefore = frame.ReboundBefore;
        ReadOnlySpan<int> oblivionBefore = frame.OblivionBefore;

        Span<int> enemyHpsBeforeAfterPlay = stackalloc int[state.Enemies.Count];
        for (int i = 0; i < state.Enemies.Count; i++)
            enemyHpsBeforeAfterPlay[i] = state.Enemies[i].Hp;

        // The play is finished, so the copy carries `PlayedThisTurn` into whichever pile it
        // lands in -- read by Fetch, which draws only the first time a given copy is played
        // in a turn. Stamped here rather than in each branch below so no destination can
        // forget it.
        card = card with
        {
            PlayedThisTurn = true,
        };

        // `RazorTooth.AfterCardPlayed` upgrades the Attack or Skill that was just played,
        // on the COPY -- so it lands in the discard pile upgraded and comes back that way.
        // Applied here, before the placement branches, because there are four of them and
        // each one builds its own `card with { ... }`.
        if (
            !card.Upgraded
            && Run.RunConstants.IsRunCardUpgradable(card)
            && Effects.RelicEffects.UpgradesPlayedCard(state, def)
        )
        {
            card = card with { Upgraded = true };
        }

        // Corruption: Skills exhaust instead of discard.
        bool corruptedSkill =
            def.Type == CardType.Skill && BuffSystem.Get(state.PlayerBuffs, BuffId.Corruption) > 0;
        if (def.Type == CardType.Power)
        {
            // Played powers leave hand without firing exhaust hooks.
        }
        // OnPlayWrapper fixes the result pile before playing. Razor Tooth may
        // upgrade this copy afterward, but cannot change this play's destination.
        else if (ShouldExhaustAfterPlay(frame.Card) || corruptedSkill)
        {
            Effects.CardEffects.ExhaustCard(
                state,
                card with
                {
                    EnchantSpent = card.EnchantSpent || state.PlayedCardEnchantSpent,
                    EnchantAmount = card.EnchantAmount + (state.PlayedCardEnchantGrew ? 1 : 0),
                    CostBump = card.CostBump + state.PlayedCardCostBump,
                    // Genetic Algorithm Exhausts, and its growth is on the CARD -- so the
                    // copy that lands in the exhaust pile carries it. This branch is the
                    // only one that had no bonus fields at all. BonusDamage belongs here
                    // for the same reason: The Scythe exhausts itself and grows by four
                    // every play, so a copy recovered from the exhaust pile has to
                    // remember.
                    BonusDamage = card.BonusDamage + state.PlayedCardBonusDamage,
                    MomentumDamage = card.MomentumDamage + state.PlayedCardMomentumDamage,
                    BonusBlock = card.BonusBlock + state.PlayedCardBonusBlock,
                    CostForCombat =
                        state.PlayedCardCostForCombat != int.MinValue
                            ? state.PlayedCardCostForCombat
                            : card.CostForCombat,
                    // SetUntilPlayed -- and this is the play, so it is spent.
                    FreeUntilPlayed = false,
                    CostOverrideThisTurn = int.MinValue,
                },
                rng: rng
            );
        }
        else if (feralReturn)
        {
            state.Hand.Add(
                card with
                {
                    FreeThisTurn = false,
                    BonusDamage = card.BonusDamage + state.PlayedCardBonusDamage,
                    MomentumDamage = card.MomentumDamage + state.PlayedCardMomentumDamage,
                    BonusBlock = card.BonusBlock + state.PlayedCardBonusBlock,
                    EnchantSpent = card.EnchantSpent || state.PlayedCardEnchantSpent,
                    EnchantAmount = card.EnchantAmount + (state.PlayedCardEnchantGrew ? 1 : 0),
                    CostBump = card.CostBump + state.PlayedCardCostBump,
                    CostForCombat =
                        state.PlayedCardCostForCombat != int.MinValue
                            ? state.PlayedCardCostForCombat
                            : card.CostForCombat,
                    // SetUntilPlayed -- and this is the play, so it is spent.
                    FreeUntilPlayed = false,
                    CostOverrideThisTurn = int.MinValue,
                }
            );
            BuffSystem.Apply(state.PlayerBuffs, BuffId.FeralUsed, 1);
        }
        else if (
            ShouldPlaceOnDrawPileAfterPlay(state, def, reboundBefore, frame.NostalgiaReturnsToDraw)
        )
        {
            state.TopDeck(
                card with
                {
                    FreeThisTurn = false,
                    BonusDamage = card.BonusDamage + state.PlayedCardBonusDamage,
                    MomentumDamage = card.MomentumDamage + state.PlayedCardMomentumDamage,
                    BonusBlock = card.BonusBlock + state.PlayedCardBonusBlock,
                    EnchantSpent = card.EnchantSpent || state.PlayedCardEnchantSpent,
                    EnchantAmount = card.EnchantAmount + (state.PlayedCardEnchantGrew ? 1 : 0),
                    CostBump = card.CostBump + state.PlayedCardCostBump,
                    CostForCombat =
                        state.PlayedCardCostForCombat != int.MinValue
                            ? state.PlayedCardCostForCombat
                            : card.CostForCombat,
                    // SetUntilPlayed -- and this is the play, so it is spent.
                    FreeUntilPlayed = false,
                    CostOverrideThisTurn = int.MinValue,
                }
            );
        }
        else if (
            ReturnsToHandAfterPlay(def)
            && state.Hand.Count < Effects.CardEffects.MaxCardsInHand
        )
        {
            // `GetResultPileTypeForCardPlay` returning Hand where it would otherwise be
            // Discard: Particle Wall comes back. The override only redirects the DISCARD
            // case, so a card that exhausts or goes to the draw pile is unaffected -- which
            // is why this sits after those branches rather than in front of them.
            state.Hand.Add(
                card with
                {
                    FreeThisTurn = false,
                    BonusDamage = card.BonusDamage + state.PlayedCardBonusDamage,
                    MomentumDamage = card.MomentumDamage + state.PlayedCardMomentumDamage,
                    BonusBlock = card.BonusBlock + state.PlayedCardBonusBlock,
                    EnchantSpent = card.EnchantSpent || state.PlayedCardEnchantSpent,
                    EnchantAmount = card.EnchantAmount + (state.PlayedCardEnchantGrew ? 1 : 0),
                    CostBump = card.CostBump + state.PlayedCardCostBump,
                    CostForCombat =
                        state.PlayedCardCostForCombat != int.MinValue
                            ? state.PlayedCardCostForCombat
                            : card.CostForCombat,
                    FreeUntilPlayed = false,
                    CostOverrideThisTurn = int.MinValue,
                }
            );
        }
        else
        {
            state.DiscardPile.Add(
                card with
                {
                    FreeThisTurn = false,
                    BonusDamage = card.BonusDamage + state.PlayedCardBonusDamage,
                    MomentumDamage = card.MomentumDamage + state.PlayedCardMomentumDamage,
                    BonusBlock = card.BonusBlock + state.PlayedCardBonusBlock,
                    EnchantSpent = card.EnchantSpent || state.PlayedCardEnchantSpent,
                    EnchantAmount = card.EnchantAmount + (state.PlayedCardEnchantGrew ? 1 : 0),
                    CostBump = card.CostBump + state.PlayedCardCostBump,
                    CostForCombat =
                        state.PlayedCardCostForCombat != int.MinValue
                            ? state.PlayedCardCostForCombat
                            : card.CostForCombat,
                    // SetUntilPlayed -- and this is the play, so it is spent.
                    FreeUntilPlayed = false,
                    CostOverrideThisTurn = int.MinValue,
                    SlyForCombat = card.SlyForCombat || MasterPlannerMarks(state, def),
                }
            );
        }

        state.PlayedCardBonusDamage = 0;
        state.PlayedCardMomentumDamage = 0;
        state.PlayedCardBonusBlock = 0;
        state.PlayedCardEnchantSpent = false;
        state.PlayedCardEnchantGrew = false;
        state.PlayedCardCostBump = 0;
        state.PlayedCardCostForCombat = int.MinValue;
        // `RightHandHand.AfterCardPlayedLate`: a play that SPENT two or more energy pulls
        // every copy sitting in the DISCARD pile back to hand. On the card, so it comes
        // back for the card that was expensive rather than for the one it belongs to --
        // and it costs nothing itself, so it can never bring itself back.
        if (energyToSpend >= 2)
        {
            Effects.CardEffects.ReturnRightHandHandsFromDiscard(state);
        }

        // `CardPlayFinishedEntry.WasEthereal` -- counted when the play FINISHES, which is
        // here and not up with the Veilpiercer decrement, so a card cannot count itself.
        if (IsEtherealForPowers(state, card))
        {
            state.EtherealCardPlaysThisCombat++;
        }

        // `OblivionPower.AfterCardPlayed` pays out the amount its BeforeCardPlayed
        // recorded, as Doom on the enemy carrying it. Paying the SNAPSHOT is what keeps
        // Oblivion off the card that applied it -- at BeforeCardPlayed the enemy does not
        // have the power yet, so there is nothing recorded for that play.
        for (int i = 0; i < state.Enemies.Count && i < oblivionBefore.Length; i++)
        {
            if (oblivionBefore[i] > 0 && state.Enemies[i].Hp > 0)
            {
                BuffSystem.Apply(state.Enemies[i].Buffs, BuffId.Doom, oblivionBefore[i]);
            }
        }

        // `MonologuePower.AfterCardPlayed`: pay the amount recorded before the play, and
        // remember the running total -- it is both what the game DISPLAYS for the power and
        // what it takes back at the end of the turn.
        if (monologueBefore > 0)
        {
            Effects.RelicEffects.GainPlayerStrength(state, monologueBefore);
            BuffSystem.Apply(state.PlayerBuffs, BuffId.MonologueApplied, monologueBefore);
        }

        // `DevourLifePower.AfterCardPlayed`: the card played being a SOUL summons Osty for
        // its amount.
        int devourLife = BuffSystem.Get(state.PlayerBuffs, BuffId.DevourLife);
        if (devourLife > 0 && card.DefId == 446) // Soul
        {
            Effects.CardEffects.SummonOsty(state, devourLife);
        }

        // `HauntPower.AfterCardPlayed`, the same Soul trigger: Unblockable and Unpowered
        // damage to one random enemy, on the CombatTargets stream.
        int haunt = BuffSystem.Get(state.PlayerBuffs, BuffId.Haunt);
        if (haunt > 0 && card.DefId == 446) // Soul
        {
            var hauntTarget = Effects.CardEffects.RandomLivingEnemy(state, rng);
            if (hauntTarget != null)
            {
                Effects.CardEffects.DealUnblockableUnpoweredDamageToEnemy(
                    state,
                    hauntTarget,
                    haunt
                );
            }
        }

        // `VoidFormPower.AfterCardPlayed` counts a play only when it is neither an auto-play
        // nor a repeat, and this is the chosen-play path, so every arrival here counts once.
        if (BuffSystem.Get(state.PlayerBuffs, BuffId.VoidForm) > 0)
        {
            state.VoidFormCardsPlayedThisTurn++;
        }

        // `MakeItSo.AfterCardPlayedLate`: playing a SKILL counts the Skills played this
        // turn, and on every third one every Make It So NOT already in hand comes back to
        // it. It reads `CardPlaysFinished`, so THIS play is in the count -- hence below
        // the increment rather than beside the Right Hand Hand hook further up, where the
        // counter is still one short and the card would come back on the second, fifth and
        // eighth Skill instead.
        if (def.Type == CardType.Skill && state.SkillCardsPlayedThisTurn % 3 == 0)
        {
            Effects.CardEffects.ReturnMakeItSosToHand(state);
        }

        // Music Box needs the INSTANCE, not the definition: it clones the card that was
        // played, so the copy inherits its upgrade and its enchantment.
        Effects.RelicEffects.ApplyMusicBox(state, card, def);

        Effects.RelicEffects.ApplyAfterHandEmptied(state, rng);
        Effects.RelicEffects.ApplyAfterPlayerHpChanged(state);

        HandleEnemyDeaths(state, enemyHpsBeforeAfterPlay, rng);

        // `PlayerCmd.EndTurn(canBackOut: false)` called from inside Void Form's OnPlay. Run
        // once the play has fully resolved rather than mid-effect, and cleared first so a
        // card played by the enemy turn's own machinery cannot re-enter it.
        if (state.EndTurnAfterPlay)
        {
            state.EndTurnAfterPlay = false;
            return EndTurn(state, rng);
        }

        bool playerDead = PlayerIsDead(state);
        bool allDead = NoPrimaryEnemyLeft(state);

        return new StepResult(
            Terminal: playerDead || allDead,
            PlayerWon: allDead && !playerDead,
            Reward: ComputeReward(
                state,
                playerDead,
                allDead,
                frame.PlayerHpBefore,
                frame.EnemyHpsBefore
            )
        );
    }

    private static StepResult EndTurn(CombatState state, Random rng)
    {
        // v0.111.0 raises no selection at the end of the turn: `WellLaidPlansPower` no
        // longer has the older `BeforeFlushLate` retain screen (its `ShouldFlush` is
        // handled at the flush below). The parked-end-turn flag is only cleared here.
        state.EndTurnAwaitingSelection = false;

        // Snapshot HP before enemies act. An array, not a stackalloc: the hand pass below
        // pays deaths as they happen and re-reads this after each, and a death can grow
        // the roster.
        int playerHpBefore = state.PlayerHp;
        int[] enemyHpsBefore = state.Enemies.Select(enemy => enemy.Hp).ToArray();
        // What the reward had counted against `enemyHpsBefore` before a re-read dropped it.
        int enemyDamageBanked = 0;

        // ── End of player turn ────────────────────────────────────────────────

        // ── AutoPostPlay ──────────────────────────────────────────────────────
        // `CombatManager.EndPlayerTurnPhaseOneInternal` runs the whole of
        // `Hook.AfterAutoPostPlayPhaseEntered` the moment the player ends the turn --
        // before `BeforeTurnEnd`, before the hand flush and before the enemies act. Both
        // of these are that hook, so they sit ahead of the `AfterSideTurnEnd` work below
        // (Shadowmeld, DoubleDamage, TemporaryStrength) that would otherwise be gone
        // before their damage was rolled.
        //
        // Stampede's swing can ask the player (Headbutt), and everything after it waits
        // for the answer; `ResumePotionAutoPlays` picks the turn up again below.
        if (AutoPlayStampedeAttacks(state, rng, BuffSystem.Get(state.PlayerBuffs, BuffId.Stampede)))
            return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
        return EndTurnAfterStampede(state, rng, playerHpBefore, enemyHpsBefore, enemyDamageBanked);
    }

    /// <summary>
    /// The end of the player turn from the second AutoPostPlay listener on, after every
    /// Stampede swing has finished -- immediately, or once a parked swing is answered.
    /// </summary>
    private static StepResult EndTurnAfterStampede(
        CombatState state,
        Random rng,
        int playerHpBefore,
        int[] enemyHpsBefore,
        int enemyDamageBanked
    )
    {
        AutoPlayHowlsFromExhaust(state, rng);

        // `IAmInvincible.AfterAutoPostPlayPhaseEntered`: if the card is sitting on TOP of
        // the draw pile as the play phase ends, it plays ITSELF --
        // `AutoPlayFromDrawPile(..., 1, Top, forceExhaust: false)`. Not once per copy and
        // not repeatedly: the copy that plays leaves the pile, so a second one under it
        // gets its turn the same way only if it too ends up on top.
        if (
            state.DrawPile.Count > 0
            && GeneratedData.Cards.Get(state.DrawPile[0].DefId).Name == "IAmInvincible"
        )
        {
            var invincible = state.DrawPile[0];
            state.RemoveFromDrawPileAt(0);
            AutoPlay(state, invincible, rng);
        }

        // Each new BeforeSideTurnEnd dispatch is suppressed once AutoPostPlay
        // ends combat (Hook.IterateCombatHookListeners). Resolve owed deaths
        // first so revival/reinforcement hooks can keep the combat alive.
        DispatchEndTurnDeaths(state, ref enemyHpsBefore, ref enemyDamageBanked, rng);
        if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
        {
            return new StepResult(
                Terminal: true,
                PlayerWon: !PlayerIsDead(state),
                Reward: ComputeReward(
                    state,
                    PlayerIsDead(state),
                    NoPrimaryEnemyLeft(state),
                    playerHpBefore,
                    enemyHpsBefore,
                    enemyDamageBanked
                )
            );
        }

        // Orichalcum and its fake snapshot zero Block in VeryEarly, after
        // AutoPostPlay but before Pael's Eye/Plating and other Early effects.
        // This local belongs to this one end-turn invocation; enemy-choice
        // continuation resumes below the payout and must not capture it again.
        bool hadNoBlockBeforeEndTurn = state.PlayerBlock == 0;

        // `PaelsEye.BeforeSideTurnEndEarly` exhausts the whole hand -- EARLY, so it lands
        // before the end-of-turn effects a card in hand would otherwise see. It is the
        // price of the extra turn granted at the enemy-side seam below, and its guard is
        // the same one, so the two always agree.
        if (Effects.RelicEffects.ShouldExhaustHandForPaelsEye(state))
        {
            foreach (var handCard in state.Hand.ToList())
            {
                Effects.CardEffects.ExhaustCard(state, handCard, rng: rng);
            }

            state.Hand.Clear();
        }

        // Metallicize: gain block at end of player turn.
        int metallicize = BuffSystem.Get(state.PlayerBuffs, BuffId.Metallicize);
        if (metallicize > 0)
        {
            Effects.CardEffects.GainBlock(state, metallicize, rng);
        }
        // PlatingPower.BeforeSideTurnEndEarly grants ValueProp.Unpowered block.
        int plating = BuffSystem.Get(state.PlayerBuffs, BuffId.Plating);
        if (plating > 0)
        {
            Effects.CardEffects.GainUnpoweredBlock(state, plating, rng);
        }

        // `TheBombPower.BeforeSideTurnEnd` (plain pass) runs after Plating.
        TickTheBomb(state, ref enemyHpsBefore, ref enemyDamageBanked, rng);

        int hailstorm = BuffSystem.Get(state.PlayerBuffs, BuffId.Hailstorm);
        if (hailstorm > 0 && state.Orbs.Any(o => o.Type == OrbType.Frost))
        {
            Effects.CardEffects.DealUnpoweredDamageToAll(state, hailstorm);
        }

        Effects.CardEffects.TriggerAllOrbBeforeTurnEndPassives(state, rng);

        Effects.RelicEffects.ApplyEndOfPlayerTurn(state, rng, hadNoBlockBeforeEndTurn);
        Effects.RelicEffects.ApplyBeforeEndOfPlayerTurnShared(state, rng);

        // `RegenPower.BeforeSideTurnEndEarly`: HEAL the amount, then DECREMENT -- so a 5 pays
        // 5+4+3+2+1 and stops, not 5 a turn forever. Guarded on `!base.Owner.IsDead`.
        int regen = BuffSystem.Get(state.PlayerBuffs, BuffId.Regen);
        if (regen > 0 && state.PlayerHp > 0)
        {
            state.PlayerHp = Math.Min(state.PlayerMaxHp, state.PlayerHp + regen);
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Regen, -1);
        }

        // Move hand to discard, exhausting ethereal cards unless a retain-hand effect is active.
        // `RingingTriangle.ShouldFlush` is false on turn one, so the whole opening hand
        // is kept rather than discarded -- a stronger thing than RetainHand, which the
        // loop below already honours, because it is not spent.
        int retainHand = BuffSystem.Get(state.PlayerBuffs, BuffId.RetainHand);
        // `WellLaidPlansPower.ShouldFlush` is false for its owner on every turn, the same
        // hook Runic Pyramid overrides, and nothing spends it. Ethereal cards were already
        // exhausted above, which `ShouldFlush` does not reach.
        if (
            Effects.RelicEffects.SkipsHandFlush(state, state.Turn + 1)
            || BuffSystem.Get(state.PlayerBuffs, BuffId.WellLaidPlans) > 0
        )
        {
            retainHand = Math.Max(retainHand, 1);
        }
        // `Regret.BeforeSideTurnEnd` snapshots `Pile.Cards.Count` and spends it in its own
        // turn-end hook, so its damage is the hand as this sequence BEGAN rather than
        // whatever is left by the time the loop reaches it.
        int handAtTurnEnd = state.Hand.Count;

        // `Hook.AfterDeath` runs inside the `CreatureCmd.Damage` that killed, so whatever
        // above hurt an enemy has paid its death hooks before `DoTurnEnd` looks at the
        // hand -- a Gremlin Horn draw off an orb or a Hailstorm kill is in the hand it reads.
        DispatchEndTurnDeaths(state, ref enemyHpsBefore, ref enemyDamageBanked, rng);
        if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
        {
            return new StepResult(
                Terminal: true,
                PlayerWon: !PlayerIsDead(state),
                Reward: ComputeReward(
                    state,
                    PlayerIsDead(state),
                    NoPrimaryEnemyLeft(state),
                    playerHpBefore,
                    enemyHpsBefore,
                    enemyDamageBanked
                )
            );
        }

        // Three cards drop Ethereal when upgraded, not one: this was `def.Id == 159`,
        // which is Echo Form, and said nothing about Apparition or Void Form.
        // `GhostSeed.AfterCardEnteredCombat` gives Ethereal to every BASIC Strike or
        // Defend. Read here rather than baked onto the instance because the relic
        // grants the KEYWORD, and a keyword the relic gave is one it takes back if the
        // relic ever leaves.
        // `HexPower.TryModifyKeywordsInCombat` grants Ethereal to every card afflicted
        // with `Hexed`, and `AfterApplied` afflicts EVERY card the player owns in
        // combat (`AfterCardEnteredCombat` catches the ones made later). So while the
        // Spectral Knight's Hex is up the whole hand is Ethereal and burns off at end
        // of turn. The emulator tracked `BuffId.Hex` as a debuff counter and did
        // nothing with it, so the Knights fight diverged on hands from the turn the
        // Hex landed and never came back.
        bool EtherealAtTurnEnd(CardInstance card) =>
            card.IsEthereal()
            || (Effects.RelicEffects.MakesBasicsEthereal(state) && IsBasicStrikeOrDefend(card))
            || BuffSystem.Get(state.PlayerBuffs, BuffId.Hex) > 0;

        // `CombatManager.DoTurnEnd` walks the hand ONCE, into the cards with a turn-end
        // effect and the other Ethereal cards, and both lists are fixed from then on. It
        // exhausts the Ethereal ones first, resolves the turn-end ones into discard or
        // exhaust next, and `FlushPlayerHand` then takes whatever the hand holds by then.
        // A card that ARRIVES while the first two run -- a Gremlin Horn draw off a
        // Charon's Ashes kill -- is in neither list, so it is not exhausted even if it is
        // Ethereal and its turn-end effect does not fire; it is simply flushed, last.
        var etherealCards = new List<CardInstance>();
        var turnEndCards = new List<CardInstance>();
        foreach (var card in state.Hand)
        {
            if (GeneratedData.Cards.Get(card.DefId).TurnEndInHand)
            {
                turnEndCards.Add(card);
            }
            else if (EtherealAtTurnEnd(card))
            {
                etherealCards.Add(card);
            }
        }

        foreach (var card in etherealCards)
        {
            // `CardCmd.Exhaust` moves the card BEFORE `AfterCardExhausted`, so a draw the
            // hooks cause lands in a hand that no longer holds it.
            if (!TakeFromHand(state, card))
            {
                continue;
            }

            Effects.CardEffects.ExhaustCard(state, card, causedByEthereal: true, rng: rng);
            DispatchEndTurnDeaths(state, ref enemyHpsBefore, ref enemyDamageBanked, rng);
            if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
            {
                return new StepResult(
                    Terminal: true,
                    PlayerWon: !PlayerIsDead(state),
                    Reward: ComputeReward(
                        state,
                        PlayerIsDead(state),
                        NoPrimaryEnemyLeft(state),
                        playerHpBefore,
                        enemyHpsBefore,
                        enemyDamageBanked
                    )
                );
            }
        }

        foreach (var card in turnEndCards)
        {
            // `AddTurnEndCardToPlayPileWithDelay` takes it out of the hand into Play first.
            if (!TakeFromHand(state, card))
            {
                continue;
            }

            var def = GeneratedData.Cards.Get(card.DefId);
            bool ethereal = EtherealAtTurnEnd(card);

            // `CardModel.OnTurnEndInHand`, for the eleven cards that have one. This was a
            // hand-kept list of four "burn the holder" statuses plus Beckon beside it; six
            // were missing, and three of those do something other than damage.
            Effects.CardEffects.ApplyTurnEndInHand(state, def, handAtTurnEnd, rng);
            if (ethereal)
            {
                Effects.CardEffects.ExhaustCard(state, card, causedByEthereal: true, rng: rng);
            }
            else
            {
                state.DiscardPile.Add(
                    card with
                    {
                        FreeThisTurn = false,
                        SlyThisTurn = false,
                        CostThisTurn = int.MinValue,
                        CostOverrideThisTurn = int.MinValue,
                    }
                );
            }

            DispatchEndTurnDeaths(state, ref enemyHpsBefore, ref enemyDamageBanked, rng);
            if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
            {
                return new StepResult(
                    Terminal: true,
                    PlayerWon: !PlayerIsDead(state),
                    Reward: ComputeReward(
                        state,
                        PlayerIsDead(state),
                        NoPrimaryEnemyLeft(state),
                        playerHpBefore,
                        enemyHpsBefore,
                        enemyDamageBanked
                    )
                );
            }
        }

        // `FlushPlayerHand` reads the hand as it NOW is, in hand order -- what the two
        // passes above left, followed by anything they drew.
        var nextHand = new List<CardInstance>();
        foreach (var card in state.Hand)
        {
            // Hand Trick's Sly lasts a single TURN, so a card that survives into the
            // next hand does not carry the grant with it. Note this cleanup does NOT go
            // through DiscardMovedCards: the end-of-turn discard is not a
            // `CardCmd.Discard` in the game and does not trigger Sly, so holding a
            // Tactician to the end of the turn buys nothing.
            // `PhantomBladesPower.AfterCardEnteredCombat` gives the Retain KEYWORD to
            // every Shiv the player owns, so a Shiv in hand survives the turn while the
            // power is up.
            bool phantomRetain =
                GeneratedData.Cards.Get(card.DefId).Name == "Shiv"
                && BuffSystem.Get(state.PlayerBuffs, BuffId.PhantomBlades) > 0;
            if (retainHand > 0 || card.IsRetained() || phantomRetain)
            {
                // Well-Laid Plans' grant is for ONE flush -- `GiveSingleTurnRetain` -- so
                // it comes off as the card lands in the next hand, the same way Hand
                // Trick's Sly does.
                nextHand.Add(
                    card with
                    {
                        FreeThisTurn = false,
                        SlyThisTurn = false,
                        RetainThisTurn = false,
                        // "This turn or until played" ends with the turn, for a card that
                        // was retained as much as for one that was discarded.
                        CostThisTurn = int.MinValue,
                        CostOverrideThisTurn = int.MinValue,
                    }
                );
            }
            else
            {
                state.DiscardPile.Add(
                    card with
                    {
                        FreeThisTurn = false,
                        SlyThisTurn = false,
                        CostThisTurn = int.MinValue,
                        CostOverrideThisTurn = int.MinValue,
                    }
                );
            }
        }
        state.Hand.Clear();
        state.Hand.AddRange(nextHand);
        // `Hook.AfterFlush(cardsToFlush, cardsToRetain)`, dispatched once after the whole
        // hand has moved -- `nextHand` IS the retained list.
        Effects.RelicEffects.ApplyAfterFlush(state, rng);
        CleanupTurnCardCosts(state);

        // The ordinary hook's snapshot also includes relics and the enemy listeners.
        // Its iteration continues after a win, but a dead player's listeners are inactive.
        if (!PlayerIsDead(state) && !NoPrimaryEnemyLeft(state))
        {
            ApplyPlayerAfterSideTurnEnd(state, rng, ref enemyHpsBefore, ref enemyDamageBanked);
            if (!PlayerIsDead(state))
                Effects.RelicEffects.ApplyAfterEndOfPlayerTurnShared(state, rng);
            foreach (var enemy in state.Enemies)
                BuffSystem.Remove(enemy.Buffs, BuffId.Oblivion);
        }
        DispatchEndTurnDeaths(state, ref enemyHpsBefore, ref enemyDamageBanked, rng);

        // Hook.AfterSideTurnEnd starts a NEW listener dispatch for its Late phase.
        // Unlike the ordinary snapshot still in flight, this one skips a finished combat.
        if (!PlayerIsDead(state) && !NoPrimaryEnemyLeft(state))
        {
            int disintegration = BuffSystem.Get(state.PlayerBuffs, BuffId.Disintegration);
            if (disintegration > 0)
            {
                Effects.CardEffects.DealDamageToPlayer(state, disintegration);
            }
        }

        bool playerDeadAfterTurnEnd = PlayerIsDead(state);
        bool allDeadAfterTurnEnd = NoPrimaryEnemyLeft(state);
        if (playerDeadAfterTurnEnd || allDeadAfterTurnEnd)
        {
            return new StepResult(
                Terminal: true,
                PlayerWon: allDeadAfterTurnEnd && !playerDeadAfterTurnEnd,
                Reward: ComputeReward(
                    state,
                    playerDeadAfterTurnEnd,
                    allDeadAfterTurnEnd,
                    playerHpBefore,
                    enemyHpsBefore,
                    enemyDamageBanked
                )
            );
        }

        Effects.CardEffects.KillDoomedEnemiesForTurnEnd(state);

        // `PaelsEye.ShouldTakeExtraTurn`: a turn in which the owner played NOTHING, once
        // per combat, skips the enemy side entirely and hands the player another turn.
        // The game decides this in `SwitchFromPlayerToEnemySide` BEFORE switching, which
        // is exactly this seam -- and `SwitchSides` then leaves `CurrentSide` on Player
        // and raises the TURN number without raising the round.
        bool extraTurn = TakeExtraTurn(state);
        return ContinueEndTurn(
            state,
            rng,
            playerHpBefore,
            enemyHpsBefore,
            enemyDamageBanked,
            extraTurn
        );
    }

    /// <summary>
    /// `SwitchFromPlayerToEnemySide` (CombatManager.cs:1819-1848): `Hook.ShouldTakeExtraTurn`
    /// asks every listener -- Pael's Eye, and `AmbergrisPower` while its amount is positive
    /// -- and if any says yes, `Hook.AfterTakingExtraTurn` then runs on EVERY listener. So
    /// an Ambergris turn also spends an unused Pael's Eye, and a Pael's Eye turn also
    /// decrements Ambergris.
    /// </summary>
    private static bool TakeExtraTurn(CombatState state)
    {
        bool paelsEye = Effects.RelicEffects.TakeExtraTurnForPaelsEye(state);
        bool ambergris = BuffSystem.Get(state.PlayerBuffs, BuffId.Ambergris) > 0;
        if (!paelsEye && !ambergris)
            return false;

        // `PaelsEye.AfterTakingExtraTurn` sets UsedThisCombat whoever granted the turn.
        Effects.RelicEffects.SpendPaelsEyeOnExtraTurn(state);
        // `AmbergrisPower.AfterTakingExtraTurn`: PowerCmd.Decrement, removed at zero.
        if (ambergris)
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Ambergris, -1);
        return true;
    }

    private static void ApplyPlayerAfterSideTurnEnd(
        CombatState state,
        Random rng,
        ref int[] enemyHpsBefore,
        ref int enemyDamageBanked
    )
    {
        // CombatState.IterateHookListeners snapshots powers in application order.
        // New powers wait until the next dispatch; existing amounts can change while
        // an earlier listener draws and plays a card. The snapshot keeps running after
        // a win, while owner death makes subsequent player listeners inactive.
        foreach (var power in state.PlayerBuffs.ToArray())
        {
            if (PlayerIsDead(state))
                break;

            int amount = BuffSystem.Get(state.PlayerBuffs, power.Id);
            if (amount == 0)
                amount = power.Magnitude;
            // PowerModel.RemoveInternal keeps its owner and amount: a power removed
            // by an earlier listener is still in this snapshot. PowerCmd.Apply/Decrement
            // separately refuse new mutations after combat starts ending; Remove does not.
            bool ending = NoPrimaryEnemyLeft(state);

            switch (power.Id)
            {
                case BuffId.DoubleDamage:
                case BuffId.RetainHand:
                    if (!ending)
                        BuffSystem.Apply(state.PlayerBuffs, power.Id, -1);
                    break;
                case BuffId.Shadowmeld:
                case BuffId.Tangled:
                case BuffId.Ringing:
                case BuffId.Rage:
                case BuffId.OneTwoPunch:
                case BuffId.Duplication:
                case BuffId.BorrowedTime:
                case BuffId.NoDraw:
                case BuffId.CorrosiveWave:
                case BuffId.NoEnergyGain:
                    BuffSystem.Remove(state.PlayerBuffs, power.Id);
                    break;
                case BuffId.Tender:
                    if (!ending)
                    {
                        Effects.RelicEffects.GainPlayerStrength(
                            state,
                            state.TenderCardsPlayedThisTurn
                        );
                        BuffSystem.Apply(
                            state.PlayerBuffs,
                            BuffId.Dexterity,
                            state.TenderCardsPlayedThisTurn
                        );
                    }
                    state.TenderCardsPlayedThisTurn = 0;
                    break;
                case BuffId.Ritual:
                    // WasJustAppliedByEnemy is never set for a player owner.
                    if (!ending)
                        Effects.RelicEffects.GainPlayerStrength(state, amount);
                    break;
                case BuffId.TemporaryStrength:
                    BuffSystem.Remove(state.PlayerBuffs, power.Id);
                    if (
                        !ending && (amount < 0 || !BuffSystem.TryConsumeArtifact(state.PlayerBuffs))
                    )
                        Effects.RelicEffects.GainPlayerStrength(state, -amount);
                    break;
                case BuffId.TemporaryDexterity:
                    BuffSystem.Remove(state.PlayerBuffs, power.Id);
                    if (
                        !ending && (amount < 0 || !BuffSystem.TryConsumeArtifact(state.PlayerBuffs))
                    )
                        BuffSystem.Apply(state.PlayerBuffs, BuffId.Dexterity, -amount);
                    break;
                case BuffId.TemporaryFocus:
                case BuffId.Hotfix:
                case BuffId.FocusedStrike:
                case BuffId.Synchronize:
                case BuffId.HyperbeamFocusDown:
                    BuffSystem.Remove(state.PlayerBuffs, power.Id);
                    int focusDelta = power.Id == BuffId.HyperbeamFocusDown ? amount : -amount;
                    if (
                        !ending
                        && (focusDelta > 0 || !BuffSystem.TryConsumeArtifact(state.PlayerBuffs))
                    )
                        BuffSystem.Apply(state.PlayerBuffs, BuffId.Focus, focusDelta);
                    break;
                case BuffId.DarkEmbrace:
                    if (state.EtherealExhaustCount > 0)
                    {
                        if (!NoPrimaryEnemyLeft(state))
                            Effects.CardEffects.DrawCards(
                                state,
                                amount * state.EtherealExhaustCount,
                                rng
                            );
                        state.EtherealExhaustCount = 0;
                    }
                    break;
                case BuffId.Constrict:
                    Effects.CardEffects.DealDamageToPlayer(state, amount);
                    break;
                case BuffId.ConsumingShadow:
                    for (int i = 0; i < amount && !NoPrimaryEnemyLeft(state); i++)
                        Effects.CardEffects.EvokeLastOrb(state, rng);
                    break;
                case BuffId.Monologue:
                    BuffSystem.Remove(state.PlayerBuffs, power.Id);
                    int monologueApplied = BuffSystem.Get(
                        state.PlayerBuffs,
                        BuffId.MonologueApplied
                    );
                    // silent: true suppresses visuals, not Artifact's amount modifier.
                    if (
                        !ending
                        && monologueApplied != 0
                        && (
                            monologueApplied < 0
                            || !BuffSystem.TryConsumeArtifact(state.PlayerBuffs)
                        )
                    )
                        Effects.RelicEffects.GainPlayerStrength(state, -monologueApplied);
                    BuffSystem.Remove(state.PlayerBuffs, BuffId.MonologueApplied);
                    break;
                case BuffId.Smoggy:
                    state.SkillPlayedWhileSmoggy = false;
                    break;
                case BuffId.Doom:
                    // Player Doom is AfterSideTurnEnd; only enemy Doom uses Before.
                    // Its listener explicitly checks IsOverOrEnding again.
                    if (!NoPrimaryEnemyLeft(state) && state.PlayerHp <= amount)
                    {
                        state.PlayerHp = 0;
                        PlayerIsDead(state);
                    }
                    break;
            }

            // Draw/evoke listeners await their nested plays and death hooks before the
            // next power receives the hook, including any cards drawn by Gremlin Horn.
            DrainAutoPlayQueue(state, rng);
            DispatchEndTurnDeaths(state, ref enemyHpsBefore, ref enemyDamageBanked, rng);
        }
    }

    private static StepResult ContinueEndTurn(
        CombatState state,
        Random rng,
        int playerHpBefore,
        ReadOnlySpan<int> enemyHpsBefore,
        int enemyDamageBanked,
        bool extraTurn,
        EnemyState[]? remainingEnemies = null
    )
    {
        if (!extraTurn)
        {
            if (remainingEnemies is null)
            {
                // ── Enemy turns ───────────────────────────────────────────────────────
                state.PlayerTurn = false;

                // SkittishPower.AfterSideTurnEnd fires for the side that is NOT its owner's, so
                // a gardener may flinch again once the player's turn is over.
                foreach (var enemy in state.Enemies)
                {
                    BuffSystem.Remove(enemy.Buffs, BuffId.SkittishSpent);
                    enemy.HardenedShellHpLostThisSide = 0;
                }
                // Block clears for the WHOLE side before anyone acts, not as each creature takes
                // its turn. The difference is a creature that GIVES block to another: a Guardbot
                // hands its Fabricator 15, and when the clear was per-creature the Fabricator --
                // later in the roster -- wiped it on its own turn. Live showed 15 and the emulator
                // showed nothing. `BurrowedPower.ShouldClearBlock` is false for its owner, which
                // is what lets a burrowed Tunneler keep the same 37 across turns.
                foreach (var enemy in state.Enemies)
                {
                    if (enemy.Hp > 0 && BuffSystem.Get(enemy.Buffs, BuffId.Burrowed) <= 0)
                    {
                        enemy.Block = 0;
                    }
                }

                state.ExecutedThisEnemyTurn.Clear();

                // `PoisonPower.AfterSideTurnStart` is a `Hook.AfterSideTurnStart` listener, and
                // `CombatManager.StartTurn` awaits that hook for the WHOLE enemy side and then
                // `CheckWinCondition` before `ExecuteEnemyTurn` walks the roster. So every
                // poisoned enemy ticks (and dies, with its death hooks) before any enemy moves,
                // and poison that finishes the last primary enemy ends the fight with no
                // minion getting its move. Ticking each enemy just before its own move let
                // the Fogmog's eye act in a fight the Fogmog's poison had already won.
                var hpsBefore = enemyHpsBefore.ToArray();
                foreach (var enemy in state.Enemies.Where(e => e.Hp > 0).ToArray())
                {
                    TriggerEnemyPoison(state, enemy);
                    DispatchEndTurnDeaths(state, ref hpsBefore, ref enemyDamageBanked, rng);
                }
                enemyHpsBefore = hpsBefore;

                bool playerDeadAfterPoison = PlayerIsDead(state);
                bool allDeadAfterPoison = NoPrimaryEnemyLeft(state);
                if (playerDeadAfterPoison || allDeadAfterPoison)
                {
                    return new StepResult(
                        Terminal: true,
                        PlayerWon: allDeadAfterPoison && !playerDeadAfterPoison,
                        Reward: ComputeReward(
                            state,
                            playerDeadAfterPoison,
                            allDeadAfterPoison,
                            playerHpBefore,
                            enemyHpsBefore,
                            enemyDamageBanked
                        )
                    );
                }
            }

            // A reviving illusion is at 0 HP and still takes its turn -- that turn IS the
            // revive. It has to stay in the roster's own order while it does, so it comes back
            // where it stood rather than at one end.
            var actors =
                remainingEnemies
                ?? state
                    .Enemies.Where(e => e.Hp > 0 || BuffSystem.Get(e.Buffs, BuffId.Reviving) > 0)
                    .ToArray();
            for (int actorIndex = 0; actorIndex < actors.Length; actorIndex++)
            {
                var enemy = actors[actorIndex];
                if (BuffSystem.Get(enemy.Buffs, BuffId.Reviving) > 0)
                {
                    // A segment that reattaches waits a whole enemy phase FIRST. `AfterDeath`
                    // puts it in DEAD_MOVE, which it performs on the following turn, and
                    // `DoReattach` runs at the END of that turn -- so the player sees it at 0
                    // for two of their own turns, not none. Reviving is seeded at 2 for a
                    // reattach and 1 for an Illusion, whose revive turn IS the heal; this
                    // spends the extra phase without curing anything.
                    if (BuffSystem.Get(enemy.Buffs, BuffId.Reviving) > 1)
                    {
                        BuffSystem.Apply(enemy.Buffs, BuffId.Reviving, -1);
                        continue;
                    }

                    // IllusionPower.ReviveMove: Heal(MaxHp - CurrentHp), then back to the move
                    // it was on. It cannot be hit while reviving, which the emulator gets for
                    // free -- everything targets by "alive", and it is not yet.
                    //
                    // A Decimillipede segment reattaches for a FIXED amount instead, so it
                    // comes back hurt: 25 of a 46-to-52 pool.
                    //
                    // The Test Subject comes back as a BIGGER creature, so its max HP moves
                    // too, and it swaps powers on the way.
                    BuffSystem.Remove(enemy.Buffs, BuffId.Reviving);
                    enemy.DeathHooksHandled = false;
                    if (enemy.DefId == KE.TestSubject)
                    {
                        RespawnTestSubject(enemy, state.AscensionLevel);
                        continue;
                    }

                    int reattach = BuffSystem.Get(enemy.Buffs, BuffId.Reattach);
                    enemy.Hp =
                        reattach > 0 ? Math.Min(enemy.MaxHp, enemy.Hp + reattach) : enemy.MaxHp;
                    // REATTACH_MOVE's FollowUpState is the machine's RandomBranchState, not
                    // the cycle -- so a segment that comes back ROLLS its next move rather
                    // than resuming where it fell. The Fogmog's eye returns to the move it was
                    // on, which is why this is keyed on the reattach rather than on reviving.
                    enemy.RollsNextMove = reattach > 0;
                    continue;
                }

                // CombatManager rechecks roster membership before each snapshotted
                // actor. An earlier retaliation/death hook can kill or remove a
                // later actor; it must not perform its old attack or debuff.
                if (enemy.Hp <= 0 || enemy.Escaped || !state.Enemies.Contains(enemy))
                    continue;

                int sandpit = BuffSystem.Get(enemy.Buffs, BuffId.Sandpit);
                if (sandpit > 0)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Sandpit, -1);
                    if (BuffSystem.Get(enemy.Buffs, BuffId.Sandpit) == 0)
                    {
                        // `CreatureCmd.Kill(creature, force: true)`, and `force` is
                        // documented as blocking death prevention -- so no hook, and Lizard
                        // Tail and Fairy in a Bottle do not save the player from a Sandpit.
                        state.PlayerHp = 0;
                        return new StepResult(Terminal: true, PlayerWon: false, Reward: -1f);
                    }
                }

                // Recorded AROUND the call, not inside it: `ExecuteIntent` has an early
                // return, and a before/after pair written inside would be split by it. The
                // caller is also the only place that knows the call happened at all, which is
                // the distinction this record exists to draw -- reached, versus did something.
                int deckBefore = PlayerCardCount(state);
                state.ExecutedThisEnemyTurn.Add(enemy.DefId);
                state.ExecutedThisEnemyTurn.Add((int)enemy.CurrentIntent.Type);
                state.ExecutedThisEnemyTurn.Add(enemy.CurrentIntent.Magnitude);
                state.ExecutedThisEnemyTurn.Add(deckBefore);

                EnemyAI.ExecuteIntent(enemy, state, rng);

                state.ExecutedThisEnemyTurn.Add(PlayerCardCount(state));
                if (state.PendingSelection is not null)
                {
                    // KnowledgeDemon.ChooseCurse awaits the choice inside its move.
                    // Preserve the remaining roster and turn boundary; neither another
                    // enemy nor next-turn damage/draw may run before the answer.
                    state.PendingEnemyTurn = new PendingEnemyTurn
                    {
                        RemainingEnemies = actors[(actorIndex + 1)..],
                        PlayerHpBefore = playerHpBefore,
                        EnemyHpsBefore = enemyHpsBefore.ToArray(),
                        EnemyDamageBanked = enemyDamageBanked,
                    };
                    return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
                }
            }

            // A Gas Bomb kills itself as it explodes and leaves the roster. Left in place at
            // zero HP it kept announcing an intent every turn, so a Living Fog fight grew an
            // extra attacker per Bloat where the live game shows one bomb appear and go.
            state.Enemies.RemoveAll(e => e.Hp <= 0 && e.DefId == KE.GasBomb);

            // Before the duration tick, and over every LIVING enemy rather than the ones that
            // acted: `HighVoltagePower.AfterSideTurnEnd` pays each PARTICIPANT, which includes
            // a bot spawned during this very turn.
            EnemyAI.ApplyEndOfEnemySideTurn(state);
            CleanupTurnCardCosts(state);
            TickDurationDebuffs(state);
            EnemyAI.ToggleNemesisIntangible(state);

            HandleEnemyDeaths(state, enemyHpsBefore, rng);

            // The combat is over the moment the enemy phase leaves nobody standing.
            // CombatManager.ExecuteEnemyTurn awaits CheckWinCondition after EVERY enemy and
            // returns as soon as IsInProgress goes false, so the game never begins another
            // player turn -- while this ran the whole of one, drew a hand and reshuffled to
            // find it. That reshuffle is not free: Rng.Shuffle is a RUN-level stream, so a
            // fight whose last enemy dies (or, as a Fat Gremlin does, escapes) on the enemy
            // turn left it ahead of the game's by the size of a pile, and every hand dealt
            // for the rest of the run came off the wrong position. The last thing that can
            // add or remove an enemy is HandleEnemyDeaths -- Gremlin Merc reinforcements and
            // Phrog Parasite wrigglers both arrive there -- so the check belongs after it.
            bool playerDeadAfterEnemyTurn = PlayerIsDead(state);
            bool allDeadAfterEnemyTurn = NoPrimaryEnemyLeft(state);
            if (playerDeadAfterEnemyTurn || allDeadAfterEnemyTurn)
            {
                return new StepResult(
                    Terminal: true,
                    PlayerWon: allDeadAfterEnemyTurn && !playerDeadAfterEnemyTurn,
                    Reward: ComputeReward(
                        state,
                        playerDeadAfterEnemyTurn,
                        allDeadAfterEnemyTurn,
                        playerHpBefore,
                        enemyHpsBefore,
                        enemyDamageBanked
                    )
                );
            }

            // Restore temporary Strength debuffs applied this turn (e.g. DarkShackles).
            foreach (var enemy in state.Enemies)
            {
                int tempStr = BuffSystem.Get(enemy.Buffs, BuffId.TemporaryStrength);
                if (tempStr != 0)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, tempStr);
                    BuffSystem.Remove(enemy.Buffs, BuffId.TemporaryStrength);
                }

                // `StranglePower.AfterSideTurnEnd` removes itself when its OWNER's side turn
                // ends -- the enemy's, here -- so a Strangle taxes the cards played on the
                // turn it lands and no longer.
                BuffSystem.Remove(enemy.Buffs, BuffId.Strangle);

                // `SicEmPower.AfterSideTurnEnd` removes itself the same way.
                BuffSystem.Remove(enemy.Buffs, BuffId.SicEm);

                // `DebilitatePower.AfterSideTurnEnd` DECREMENTS rather than removing, so its
                // amount really is a turn count.
                if (BuffSystem.Get(enemy.Buffs, BuffId.Debilitate) > 0)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Debilitate, -1);
                }

                // `ConquerorPower.AfterSideTurnEnd`, the same shape.
                if (BuffSystem.Get(enemy.Buffs, BuffId.Conqueror) > 0)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Conqueror, -1);
                }
            }

            int colossus = BuffSystem.Get(state.PlayerBuffs, BuffId.Colossus);
            if (colossus > 0)
            {
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Colossus, -1);
            }

            int noBlock = BuffSystem.Get(state.PlayerBuffs, BuffId.NoBlock);
            if (noBlock == 1)
            {
                BuffSystem.Remove(state.PlayerBuffs, BuffId.NoBlock);
            }
            else if (noBlock > 1)
            {
                BuffSystem.Apply(state.PlayerBuffs, BuffId.NoBlock, -1);
            }

            // FlameBarrier expires after enemies have acted.
            BuffSystem.Remove(state.PlayerBuffs, BuffId.FlameBarrier);

            // `BattlewornDummyTimeLimitPower.AfterSideTurnEnd`: the dummy's counter ticks at
            // the end of its OWN side turn, and at 1 it flags the encounter and escapes rather
            // than decrementing to nothing. Three turns to kill it.
            foreach (var enemy in state.Enemies)
            {
                int limit = BuffSystem.Get(enemy.Buffs, BuffId.BattlewornDummyTimeLimit);
                if (limit <= 0 || enemy.Hp <= 0)
                {
                    continue;
                }

                if (limit > 1)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.BattlewornDummyTimeLimit, -1);
                    continue;
                }

                // Zeroing the HP is how the emulator takes a creature out of a fight; the
                // flag is what tells the event this was an escape and not a kill.
                enemy.Hp = 0;
                enemy.Escaped = true;
                state.BattlewornDummyRanOutOfTime = true;
            }
        }

        // EndEnemyTurn checks victory after AfterSideTurnEnd and before switching sides.
        // In particular, the dummy's timeout escape must not start a fourth player turn
        // or let Crossbow spend another CombatCardGeneration shuffle after combat ends.
        if (NoPrimaryEnemyLeft(state))
        {
            bool deadAtSideEnd = PlayerIsDead(state);
            return new StepResult(
                Terminal: true,
                PlayerWon: !deadAtSideEnd,
                Reward: ComputeReward(
                    state,
                    deadAtSideEnd,
                    true,
                    playerHpBefore,
                    enemyHpsBefore,
                    enemyDamageBanked
                )
            );
        }

        // ── Start of next player turn ─────────────────────────────────────────
        // `SwitchSides`: every player starting this turn calls `IncrementTurnNumber`, but
        // `RoundNumber++` belongs to the enemy-to-player branch only.
        state.Turn++;
        if (!extraTurn)
        {
            state.RoundNumber++;
        }
        else if (state.RoundNumber == 1)
        {
            // `PlatingPower.BeforeSideTurnStart` grants an enemy its Plating on every
            // player side start while `RoundNumber <= 1`, without asking who takes part.
            // `CombatFactory` grants the first one with the enemy; a round-one extra turn
            // is a second player side start inside that round.
            foreach (var enemy in state.Enemies)
            {
                int plating = BuffSystem.Get(enemy.Buffs, BuffId.Plating);
                if (enemy.Hp > 0 && plating > 0)
                    enemy.Block += plating;
            }
        }
        Effects.RelicEffects.TickPaelsLegionCooldown(state);
        state.PlayerTurn = true;
        // BeatingRemnant.BeforeSideTurnStart resets for its owner's participating turn,
        // before startup damage such as Inferno. That damage belongs to the new budget.
        state.UnblockedDamageThisTurn = 0;
        // The turn rolls over: what was "this turn" becomes what History Course reads.
        state.LastAttackLastTurn = state.LastAttackThisTurn;
        state.LastAttackThisTurn = null;
        // `VoidFormPower.BeforeSideTurnStart` clears its own count.
        state.VoidFormCardsPlayedThisTurn = 0;
        // `CombatManager.SetupPlayerTurn`: `ResetEnergy()` (Energy = MaxEnergy) when every
        // `ShouldPlayerResetEnergy` agrees, otherwise `AddMaxEnergyToCurrent()` (Energy +=
        // MaxEnergy). `IceCream` says no from turn two on, so the leftover CARRIES and the
        // turn's energy is still added on top -- not a turn with no refill at all.
        // `AfterEnergyReset` (Radiance and the rest, below) runs after either branch.
        // Both are direct writes in the game, not `PlayerCmd.GainEnergy`, so this is the
        // reset path and no energy-gain hook (NoEnergyGain) applies to either.
        int turnEnergy = EffectiveMaxEnergyForTurn(state, state.Turn + 1);
        state.Energy = Effects.RelicEffects.ShouldResetEnergy(state, state.Turn + 1)
            ? turnEnergy
            : state.Energy + turnEnergy;
        state.PlayerHpLostThisTurn = 0;
        state.CardsPlayedThisTurn = 0;
        // `SlothPower.BeforeSideTurnStart` clears its own count.
        state.SlothCardsPlayedThisTurn = 0;
        // `HappenedThisTurn` on the damage history, which Beat Into Shape reads per target.
        foreach (var enemy in state.Enemies)
        {
            enemy.PoweredHitsThisTurn = 0;
            enemy.HardenedShellHpLostThisSide = 0;
        }

        state.ShivsPlayedThisTurn = 0;

        // Enemies choose their next intent. `CombatManager.StartTurn` calls
        // `PrepareForNextTurn` for every enemy right after `BeforeSideTurnStart` and before
        // `AfterTurnStart`, `SetupPlayerTurn` and `AfterSideTurnStart` -- so an enemy that
        // Inferno, an orb or an auto-played card kills this turn start has already spent
        // its MonsterAi draw. The game skips the roll on an extra player turn.
        if (!extraTurn)
        {
            EnemyAI.ChooseIntents(
                state.Enemies,
                state.Turn,
                rng,
                state.AiRng,
                state.AscensionLevel
            );
        }

        // After the energy reset, which is where the game puts it and says why.
        Effects.RelicEffects.ApplyBoundPhylacteryTurnStart(state, state.Turn + 1);

        // History Course's dupe is played in AutoPrePlay, after Mayhem; see
        // AutoPlayHistoryCourseEncore.

        Effects.CardEffects.TriggerAllOrbAfterTurnStartPassives(state, rng);

        int spinner = BuffSystem.Get(state.PlayerBuffs, BuffId.Spinner);
        for (int i = 0; i < spinner; i++)
        {
            Effects.CardEffects.ChannelOrb(state, OrbType.Glass);
        }

        // Poison damage at start of player turn.
        int playerPoison = BuffSystem.Get(state.PlayerBuffs, BuffId.Poison);
        if (playerPoison > 0)
        {
            state.PlayerHp -= BuffSystem.CapHpLoss(playerPoison, state.PlayerBuffs);
            // Poison is a route to under-half like any other, and it is the one that
            // ticks with no card played -- so without this Red Skull could stay unarmed
            // through a whole turn it should have been active for.
            Effects.RelicEffects.ApplyAfterPlayerHpChanged(state);
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Poison, -1);
            if (PlayerIsDead(state))
            {
                return new StepResult(
                    Terminal: true,
                    PlayerWon: false,
                    Reward: ComputeReward(
                        state,
                        true,
                        false,
                        playerHpBefore,
                        enemyHpsBefore,
                        enemyDamageBanked
                    )
                );
            }
        }

        int entropy = BuffSystem.Get(state.PlayerBuffs, BuffId.EntropyPower);
        for (int i = 0; i < entropy; i++)
        {
            Effects.CardEffects.TransformRandomCardInHand(state, rng);
        }

        // Barricade: block does not reset. Blur is the same rule with a counter --
        // `BlurPower.ShouldClearBlock` is false for its owner while any remains -- and it
        // decrements at every side turn start its owner takes part in, whether or not it
        // was the thing that saved the block. Barricade does NOT decrement, which is the
        // whole difference between the two and why they cannot share an id.
        int blur = BuffSystem.Get(state.PlayerBuffs, BuffId.Blur);
        // CombatState.IterateHookListeners visits powers before relics, and
        // ShouldClearBlock returns the first preventer. Clamp's trim callback only
        // runs when Clamp prevented the clear; Blur or Barricade wins before it.
        bool powerKeepsBlock = BuffSystem.Get(state.PlayerBuffs, BuffId.Barricade) > 0 || blur > 0;
        if (!powerKeepsBlock && Effects.RelicEffects.KeepsBlockCappedAtTen(state))
        {
            state.PlayerBlock = Math.Min(state.PlayerBlock, 10);
        }
        else if (!powerKeepsBlock)
        {
            state.PlayerBlock = 0;
            // `ToricToughnessPower.AfterBlockCleared`: the block comes straight back, and
            // the counter -- which is TURNS, not an amount -- ticks down.
            int toric = BuffSystem.Get(state.PlayerBuffs, BuffId.ToricToughness);
            if (toric > 0)
            {
                Effects.CardEffects.GainUnpoweredBlock(state, state.ToricToughnessBlock, rng);
                BuffSystem.Apply(state.PlayerBuffs, BuffId.ToricToughness, -1);
            }

            // `Hook.AfterBlockCleared`, which Sparkling Rouge answers on turn three only.
            // Barricade and Blur keep the block, and the hook does not fire when it is
            // not cleared -- so a Barricade run never gets the Rouge's Strength.
            Effects.RelicEffects.ApplyAfterBlockCleared(state, state.Turn + 1);
        }

        if (blur > 0)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Blur, -1);
        }

        ApplyBlockNextTurn(state, rng);

        // `BiasedCognitionPower.AfterSideTurnStart` hands a point of Focus BACK every
        // turn. Without it the card is 4 Focus for one energy and no downside at all.
        int biasedCognition = BuffSystem.Get(state.PlayerBuffs, BuffId.BiasedCognition);
        if (biasedCognition > 0)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Focus, -biasedCognition);
        }

        Effects.RelicEffects.ApplyStartOfPlayerTurnShared(state, state.Turn + 1, rng);
        Effects.RelicEffects.ApplyStartOfPlayerTurnRares(state, state.Turn + 1, rng);
        Effects.RelicEffects.RefreshBeltBuckle(state);

        int coolant = BuffSystem.Get(state.PlayerBuffs, BuffId.Coolant);
        if (coolant > 0)
        {
            Effects.CardEffects.GainUnpoweredBlock(
                state,
                coolant * state.Orbs.Select(o => o.Type).Distinct().Count()
            );
        }

        foreach (var enemy in state.Enemies)
        {
            BuffSystem.Remove(enemy.Buffs, BuffId.SlowCount);
        }

        Effects.RelicEffects.ApplyStartOfPlayerTurn(state, rng);

        int loop = BuffSystem.Get(state.PlayerBuffs, BuffId.Loop);
        for (int i = 0; i < loop; i++)
        {
            Effects.CardEffects.TriggerOrbPassive(state, 0, rng);
        }

        // `RadiancePower.AfterEnergyReset`: `PlayerCmd.GainEnergy(1)` and DECREMENT, so the
        // counter is TURNS and the energy is always one. Added to the bar the reset just
        // filled rather than to the maximum -- a relic that raises max energy and this are
        // not the same quantity, and only one of them survives a Void Form.
        int radiance = BuffSystem.Get(state.PlayerBuffs, BuffId.Radiance);
        if (radiance > 0)
        {
            // `PlayerCmd.GainEnergy`, which is what the power actually calls -- so a
            // NoEnergyGain standing on the player zeroes it. Incrementing the energy field
            // directly here built it where nothing could stop it, and the chokepoint guard
            // in `BlockChokepointTests` is what caught it; its own comment records Seal of
            // Gold making the identical mistake. (That guard counts TEXTUAL matches, so do
            // not spell the raw increment out even in a comment -- this one used to.)
            Effects.CardEffects.GainEnergy(state, 1);
            // The DECREMENT is unconditional: the game awaits the gain and then decrements
            // whatever the gain did, so a turn spent under NoEnergyGain still burns one.
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Radiance, -1);
        }

        // `LightningRodPower.AfterEnergyReset` channels a Lightning orb and DECREMENTS,
        // so an amount of 2 is two TURNS of orbs. Its own comment explains why it fires
        // here rather than at side-turn start: an orb evoked to make room would otherwise
        // have its Plasma energy wiped by the reset, or its Frost block cleared.
        int lightningRod = BuffSystem.Get(state.PlayerBuffs, BuffId.LightningRod);
        if (lightningRod > 0)
        {
            Effects.CardEffects.ChannelOrb(state, OrbType.Lightning, rng);
            BuffSystem.Apply(state.PlayerBuffs, BuffId.LightningRod, -1);
        }

        int nextTurnEnergy = BuffSystem.Get(state.PlayerBuffs, BuffId.NextTurnEnergy);
        if (nextTurnEnergy > 0)
        {
            Effects.CardEffects.GainEnergy(state, nextTurnEnergy);
            BuffSystem.Remove(state.PlayerBuffs, BuffId.NextTurnEnergy);
        }

        int rollingBoulder = BuffSystem.Get(state.PlayerBuffs, BuffId.RollingBoulderPower);
        if (rollingBoulder > 0)
        {
            Effects.CardEffects.DealUnpoweredDamageToAll(state, rollingBoulder);
            BuffSystem.Apply(state.PlayerBuffs, BuffId.RollingBoulderPower, 5);
        }

        // `AggressionPower.BeforeSideTurnStart`: ATTACKS out of the DISCARD PILE into hand,
        // upgraded. The player's own cards, moved -- not new ones rolled up.
        int aggression = BuffSystem.Get(state.PlayerBuffs, BuffId.Aggression);
        if (aggression > 0)
        {
            Effects.CardEffects.MoveDiscardedAttacksToHandForAggression(state, aggression, rng);
        }

        // Plating decays by 1 at start of player turn.
        int platingNow = BuffSystem.Get(state.PlayerBuffs, BuffId.Plating);
        if (platingNow > 0)
        {
            if (platingNow == 1)
            {
                BuffSystem.Remove(state.PlayerBuffs, BuffId.Plating);
            }
            else
            {
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Plating, -1);
            }
        }

        BuffSystem.Remove(state.PlayerBuffs, BuffId.FeralUsed);

        state.AttackCardsPlayedThisTurn = 0;
        state.AttackPlaysStartedThisTurn = 0;
        state.AttackOrSkillPlaysStartedThisTurn = 0;
        state.CardPlaysStartedThisTurn = 0;
        state.SkillCardsPlayedThisTurn = 0;
        state.ExtraCardsDrawnThisTurn = 0;
        state.DoomAppliedThisTurn = false;
        state.OstyAttacksThisTurn = 0;
        state.StarsGainedThisTurn = 0;
        state.AttackOrSkillCardsPlayedThisTurn = 0;
        // Carried over before the reset: Pale Blue Dot reads LAST turn's plays.
        state.CardPlaysLastTurn = state.CardPlaysThisTurn;
        state.CardPlaysThisTurn = 0;
        CardBlockHistory.EnsureCurrentTurn(state);
        state.CardsExhaustedThisTurn = 0;
        state.CardsDiscardedThisTurn = 0;
        state.StatusCardsDrawnThisTurn = 0;
        state.EnergySpentThisTurn = 0;
        state.RainbowRingAttacks = 0;
        state.RainbowRingSkills = 0;
        state.RainbowRingPowers = 0;
        state.RainbowRingPaidThisTurn = false;

        // The returns read last turn's `PlayedThisTurn` (a stand-in for `HappenedLastPlayerTurn`
        // history), so the flag expires only once they have been answered.
        ReturnQueuedCardsToHandBeforeDraw(state);
        Effects.CardEffects.ClearPlayedThisTurn(state);
        DeliverQueuedCardCopiesBeforeDraw(state);

        // `ShadowStepPower.AfterSideTurnStart` converts itself into DoubleDamage and then
        // removes itself, which is what makes Shadow Step's payload land a turn late.
        int shadowStep = BuffSystem.Get(state.PlayerBuffs, BuffId.ShadowStep);
        if (shadowStep > 0)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.DoubleDamage, shadowStep);
            BuffSystem.Remove(state.PlayerBuffs, BuffId.ShadowStep);
        }

        // `InfiniteBladesPower.BeforeHandDraw` -- so the Shivs are in hand BEFORE the five
        // are drawn, not after. It matters at the hand limit: the Shivs take their slots
        // first and the draw is what gets cut short, which is the opposite of what the
        // emulator did by adding them at the end.
        int infiniteBlades = BuffSystem.Get(state.PlayerBuffs, BuffId.InfiniteBlades);
        if (infiniteBlades > 0)
        {
            Effects.CardEffects.AddGeneratedCardsToHand(state, 430, infiniteBlades);
        }

        // `HelloWorldPower.BeforeHandDraw`, the same boundary as the Shivs above and for
        // the same reason: the cards take their slots before the five are drawn.
        int helloWorld = BuffSystem.Get(state.PlayerBuffs, BuffId.HelloWorld);
        if (helloWorld > 0)
        {
            Effects.CardEffects.AddDistinctCommonCardsToHand(state, helloWorld, rng);
        }

        int creativeAi = BuffSystem.Get(state.PlayerBuffs, BuffId.CreativeAi);
        if (creativeAi > 0)
        {
            Effects.CardEffects.AddRandomDefectPowerCardsToHand(state, creativeAi, rng);
        }

        // `NeurosurgePower.AfterSideTurnStart`: Dooms its owner -- the PLAYER -- for its
        // amount, every turn.
        int neurosurge = BuffSystem.Get(state.PlayerBuffs, BuffId.Neurosurge);
        if (neurosurge > 0)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Doom, neurosurge);
        }

        // `ReflectPower.AfterSideTurnStart` DECREMENTS at its owner's turn start, so a
        // Reflect covers the enemies' turn and is gone by the player's next one.
        if (BuffSystem.Get(state.PlayerBuffs, BuffId.Reflect) > 0)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Reflect, -1);
        }

        // `GenesisPower.AfterEnergyReset`: stars every turn, and this one does NOT remove
        // itself.
        int genesis = BuffSystem.Get(state.PlayerBuffs, BuffId.Genesis);
        if (genesis > 0)
        {
            Effects.CardEffects.GainStars(state, genesis);
        }

        // `StarNextTurnPower.AfterEnergyReset`: the stars arrive at the turn's reset and the
        // power removes itself, so Convergence pays out exactly once.
        int starNextTurn = BuffSystem.Get(state.PlayerBuffs, BuffId.StarNextTurn);
        if (starNextTurn > 0)
        {
            Effects.CardEffects.GainStars(state, starNextTurn);
            BuffSystem.Remove(state.PlayerBuffs, BuffId.StarNextTurn);
        }

        // `SummonNextTurnPower.AfterPlayerTurnStart`: summons for its amount and then
        // removes itself, so Invoke pays out exactly once.
        int summonNextTurn = BuffSystem.Get(state.PlayerBuffs, BuffId.SummonNextTurn);
        if (summonNextTurn > 0)
        {
            Effects.CardEffects.SummonOsty(state, summonNextTurn);
            BuffSystem.Remove(state.PlayerBuffs, BuffId.SummonNextTurn);
        }

        // `CountdownPower.AfterSideTurnStart`: one RANDOM hittable enemy is Doomed for its
        // amount, rolled on the CombatTargets stream like every other unaimed card.
        int countdown = BuffSystem.Get(state.PlayerBuffs, BuffId.Countdown);
        if (countdown > 0)
        {
            var doomed = Effects.CardEffects.RandomLivingEnemy(state, rng);
            if (doomed != null)
            {
                // `PowerCmd.Apply<DoomPower>(..., base.Owner, null)` -- no card source, so
                // it skips the card-debuff chokepoint the Unsettling Lamp doubles, exactly
                // as Reaper Form's does.
                BuffSystem.Apply(doomed.Buffs, BuffId.Doom, countdown);
                Effects.CardEffects.ApplyPowerDoomRiders(state, doomed, countdown);
            }
        }

        // `ForegoneConclusionPower.BeforeHandDraw`: that many cards CHOSEN from the draw
        // pile go to hand, then the power removes itself. Raised BEFORE the draw, so the
        // screen stands over the old hand -- and the end-turn work is already finished, so
        // ValidActions simply restricts to it until it is answered, as Tools of the Trade's
        // does.
        int foregone = BuffSystem.Get(state.PlayerBuffs, BuffId.ForegoneConclusion);
        if (foregone > 0 && state.DrawPile.Count > 0)
        {
            Effects.CardEffects.OpenDrawPileToHandSelection(state, foregone);
            BuffSystem.Remove(state.PlayerBuffs, BuffId.ForegoneConclusion);
        }

        // `FurnacePower.AfterSideTurnStart`: a Forge of its amount, every turn.
        int furnace = BuffSystem.Get(state.PlayerBuffs, BuffId.Furnace);
        if (furnace > 0)
        {
            Effects.CardEffects.Forge(state, furnace);
        }

        // `SpectrumShiftPower.BeforeHandDraw`: distinct COLOURLESS cards into hand.
        int spectrumShift = BuffSystem.Get(state.PlayerBuffs, BuffId.SpectrumShift);
        if (spectrumShift > 0)
        {
            Effects.CardEffects.AddColorlessCardsToHand(state, spectrumShift, rng);
        }

        // `CallOfTheVoidPower.BeforeHandDraw`: cards from the character's OWN pool, each
        // granted Ethereal, straight into hand. The pool filter is the ordinary combat one
        // -- `GetUnlockedCards` drops Basic and Ancient, and `GetDistinctForCombat` runs
        // FilterForCombat over what it is handed.
        int callOfTheVoid = BuffSystem.Get(state.PlayerBuffs, BuffId.CallOfTheVoid);
        for (int i = 0; i < callOfTheVoid; i++)
        {
            Effects.CardEffects.AddEtherealPoolCardToHand(state, rng);
        }

        // `SentryModePower.BeforeHandDraw` -- BEFORE the hand is drawn, so the Gazes are
        // at the bottom of the new hand rather than the top, and they count against the
        // hand cap the draw then runs into.
        int sentryMode = BuffSystem.Get(state.PlayerBuffs, BuffId.SentryMode);
        for (int i = 0; i < sentryMode; i++)
        {
            Effects.CardEffects.AddGeneratedCardToHand(state, 485); // SweepingGaze
        }

        Effects.RelicEffects.AdvancePendulumBeforeHandDraw(state);
        Effects.RelicEffects.TickPollinousCore(state);

        // Draw five cards -- less MindRot, which is `Math.Max(0, count - Amount)` on the
        // whole draw rather than a per-card effect.
        int handDraw = Math.Max(
            0,
            5
                + BuffSystem.Get(state.PlayerBuffs, BuffId.MachineLearning)
                // `PaleBlueDotPower.ModifyHandDraw`, and only past the threshold: five
                // card plays LAST turn, or the power adds nothing at all.
                + (
                    state.CardPlaysLastTurn >= 5
                        ? BuffSystem.Get(state.PlayerBuffs, BuffId.PaleBlueDot)
                        : 0
                )
                // `TyrannyPower.ModifyHandDraw`, the half of that card that pays.
                + BuffSystem.Get(state.PlayerBuffs, BuffId.Tyranny)
                // `DemesnePower.ModifyHandDraw`, the other half of a power that also
                // raises max energy -- both every turn, not once.
                + BuffSystem.Get(state.PlayerBuffs, BuffId.Demesne)
                + Effects.RelicEffects.ExtraHandDraw(state)
                // `DrawCardsNextTurnPower.ModifyHandDraw`: part of the HAND DRAW, not a
                // draw of its own. It was a separate `DrawCards` after this one, which
                // Fiddle's `ShouldDraw` then refused -- it vetoes every draw that is
                // not the hand draw, and the game's hand draw is exactly what this is.
                // A run holding Fiddle lost every next-turn draw it was owed.
                + BuffSystem.Get(state.PlayerBuffs, BuffId.NextTurnDraw)
                // `ClarityPower.ModifyHandDraw` adds ONE, however many turns are left
                // on it -- the counter is turns, not cards, which is the whole of what
                // the game's remark means by "distinct due to its stacking behavior".
                // Summing the amount here would pay three cards on the first turn and
                // none on the other two.
                + (BuffSystem.Get(state.PlayerBuffs, BuffId.Clarity) > 0 ? 1 : 0)
                - BuffSystem.Get(state.PlayerBuffs, BuffId.MindRot)
        );
        Effects.RelicEffects.ResetPollinousCoreAfterModifyingHandDraw(state);
        // `StratagemPower.AfterShuffle` can pause the draw on its screen; the draws it
        // still owes and `AfterPlayerTurnStart` resume from PendingTurnSetup.
        int handDrawOwed = Effects.CardEffects.DrawHandCards(state, handDraw, rng);
        // Hook.IterateCombatHookListeners yields no listeners once combat is
        // over or ending. Hellraiser can win during the awaited hand draw, so
        // no later AfterPlayerTurnStart hooks, including Inferno, run. Preserve
        // the draw/shuffle already consumed and stop the turn-start tail.
        bool deadAfterHandDraw = PlayerIsDead(state);
        bool wonAfterHandDraw = NoPrimaryEnemyLeft(state);
        if (deadAfterHandDraw || wonAfterHandDraw)
            return new StepResult(
                Terminal: true,
                PlayerWon: wonAfterHandDraw && !deadAfterHandDraw,
                Reward: ComputeReward(
                    state,
                    deadAfterHandDraw,
                    wonAfterHandDraw,
                    playerHpBefore,
                    enemyHpsBefore,
                    enemyDamageBanked
                )
            );
        // `AfterSideTurnStart` removes it once it has been spent on that draw.
        BuffSystem.Remove(state.PlayerBuffs, BuffId.NextTurnDraw);
        // `ClarityPower.AfterSideTurnStart` decrements once per turn of the owner's, AFTER
        // the draw it just paid for -- decrementing first would cost the last turn's card.
        if (BuffSystem.Get(state.PlayerBuffs, BuffId.Clarity) > 0)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Clarity, -1);
        }
        if (handDrawOwed > 0)
            PlayerTurnSetup.BeginAfterStratagemPause(state, handDrawOwed);
        else
            ApplyAfterPlayerTurnStart(state, rng);

        if (handDrawOwed == 0 && PlayerTurnSetup.Applies(state))
        {
            PlayerTurnSetup.BeginAfterDraw(state, rng);
            bool setupDead = PlayerIsDead(state);
            bool setupWon = NoPrimaryEnemyLeft(state);
            if (setupDead || setupWon)
                return new StepResult(
                    setupDead || setupWon,
                    setupWon && !setupDead,
                    ComputeReward(
                        state,
                        setupDead,
                        setupWon,
                        playerHpBefore,
                        enemyHpsBefore,
                        enemyDamageBanked
                    )
                );
        }

        // PrepTimePower.AfterSideTurnStart follows SetupPlayerTurn's hand draw
        // (or its first choice pause). Hellraiser's draw plays cannot spend this
        // turn's Vigor. Hook.IterateCombatHookListeners yields nothing after a win.
        if (!PlayerIsDead(state) && !NoPrimaryEnemyLeft(state))
        {
            // `DemonFormPower.AfterSideTurnStart`, likewise after the hand draw's
            // awaited Hellraiser Strikes (live ABB60XX step437).
            int demonForm = BuffSystem.Get(state.PlayerBuffs, BuffId.DemonForm);
            if (demonForm > 0)
                Effects.RelicEffects.GainPlayerStrength(state, demonForm);

            int prepTime = BuffSystem.Get(state.PlayerBuffs, BuffId.PrepTimePower);
            if (prepTime > 0)
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Vigor, prepTime);
        }

        // CombatState.IterateHookListeners visits powers before relics.
        if (PlayerTurnSetup.Applies(state))
            Effects.RelicEffects.ApplySideStartRelicsForTurnSetup(state, rng);

        // `Crossbow.AfterSideTurnStart`, after the hand draw (and any Tools of the Trade
        // screen left standing), as the ordered setup above does it.
        Effects.RelicEffects.ApplyCrossbowAfterSideTurnStart(state, rng);
        Effects.RelicEffects.ApplyFlowersAfterSideTurnStart(state);

        // `WraithFormPower.AfterSideTurnStart` takes its Amount in Dexterity every turn.
        int wraithForm = BuffSystem.Get(state.PlayerBuffs, BuffId.WraithForm);
        if (wraithForm > 0)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Dexterity, -wraithForm);
        }

        int noxiousFumes = BuffSystem.Get(state.PlayerBuffs, BuffId.NoxiousFumes);
        if (noxiousFumes > 0)
        {
            Effects.CardEffects.ApplyPoisonToAllEnemies(state, noxiousFumes, rng);
        }

        // AfterSideTurnStart follows SetupPlayerTurn's completion OR first choice
        // pause (WaitForPauseOrCompletionWithoutAssigningTask), before auto-preplay.
        // With no choice pause, this includes the draw and its awaited nested plays.
        ApplyRampartAtPlayerTurnStart(state, extraTurn);

        // `Bombardment.AfterAutoPrePlayPhaseEnteredEarly` -- EARLY, so ahead of Mayhem's
        // hook, which is the plain `AfterAutoPrePlayPhaseEntered`.
        if (PlayerTurnSetup.Applies(state) || handDrawOwed > 0)
            PlayerTurnSetup.AfterSideStart(state, rng);
        else
            ResolveTurnStartAutoPlays(state, rng);

        Effects.RelicEffects.ApplyAfterPlayerHpChanged(state);

        bool playerDead = PlayerIsDead(state);
        bool allDead = NoPrimaryEnemyLeft(state);

        return new StepResult(
            Terminal: playerDead || allDead,
            PlayerWon: allDead && !playerDead,
            Reward: ComputeReward(
                state,
                playerDead,
                allDead,
                playerHpBefore,
                enemyHpsBefore,
                enemyDamageBanked
            )
        );
    }

    /// <summary>
    /// `Hook.AfterPlayerTurnStart`, the end of `SetupPlayerTurn` after its hand draw.
    /// Resumed from PendingTurnSetup when Stratagem's screen paused that draw.
    /// </summary>
    internal static void ApplyAfterPlayerTurnStart(CombatState state, Random rng)
    {
        // Both self-damage powers are AfterPlayerTurnStart listeners, after hand draw.
        // Preserve their acquisition order. Crimson Mantle can trigger a lethal Inferno
        // even before Inferno's own listener; it must not prevent this turn's shuffle.
        int crimsonIndex = state.PlayerBuffs.FindIndex(p => p.Id == BuffId.CrimsonMantleBlock);
        int infernoIndex = state.PlayerBuffs.FindIndex(p => p.Id == BuffId.Inferno);
        bool crimsonFirst = crimsonIndex >= 0 && (infernoIndex < 0 || crimsonIndex < infernoIndex);
        if (crimsonFirst)
            ApplyCrimsonMantleAfterHandDraw(state, rng);

        // `InfernoPower.AfterPlayerTurnStart`, which `SetupPlayerTurn` awaits only AFTER the
        // hand draw. A burn that kills the last enemy still leaves that turn's draw (and
        // any reshuffle it forced) spent on the Shuffle stream: live ABB36XX and ABB39XX
        // both ended a fight this way, and applying it before the draw left the stream
        // 10-12 calls short for every later combat.
        int infernoSelfDamage = BuffSystem.Get(state.PlayerBuffs, BuffId.InfernoSelfDamage);
        if (infernoSelfDamage > 0)
        {
            Span<int> enemyHpsBeforeInferno = stackalloc int[state.Enemies.Count];
            for (int i = 0; i < state.Enemies.Count; i++)
            {
                enemyHpsBeforeInferno[i] = state.Enemies[i].Hp;
            }

            Effects.CardEffects.LoseHp(state, infernoSelfDamage);
            HandleEnemyDeaths(state, enemyHpsBeforeInferno, rng);
        }

        if (crimsonIndex >= 0 && !crimsonFirst)
            ApplyCrimsonMantleAfterHandDraw(state, rng);

        // `TyrannyPower.AfterPlayerTurnStart`: EXHAUST that many cards CHOSEN from hand --
        // `CardSelectorPrefs(prompt, Amount)`, whose single count sets min and max alike, so
        // the exhaust is compulsory and the CHOICE is the player's. The half of that card
        // that costs, and it lands after the draw the other half paid for.
        int tyranny = BuffSystem.Get(state.PlayerBuffs, BuffId.Tyranny);
        if (!PlayerTurnSetup.Applies(state) && tyranny > 0 && state.Hand.Count > 0)
        {
            Effects.CardEffects.OpenExhaustFromHandSelection(state, tyranny);
        }

        // `ToolsOfTheTradePower` is two hooks. `ModifyHandDraw` adds its amount to the
        // hand draw, and `AfterPlayerTurnStart` raises a DISCARD SELECTION for that many
        // cards -- `CardSelectCmd.FromHandForDiscard` with a `CardSelectorPrefs(prompt,
        // Amount)`, whose single-count constructor sets min and max alike, so the discard
        // is compulsory but the CHOICE is the player's.
        //
        // The emulator threw away the leftmost card. Tools of the Trade is a filtering
        // card -- drawing one more and pitching your worst is the whole engine -- and
        // pitching whatever happens to be first is closer to a downside than an upside.
        //
        // The screen is left standing while the side-start hooks below run. Source
        // StartTurn waits for SetupPlayerTurn's completion OR first choice pause;
        // `ValidActions` restricts to that choice until it is answered.
        int toolsOfTheTrade = BuffSystem.Get(state.PlayerBuffs, BuffId.ToolsOfTheTrade);
        if (!PlayerTurnSetup.Applies(state) && toolsOfTheTrade > 0)
        {
            Effects.CardEffects.DrawCards(state, toolsOfTheTrade, rng);
            Effects.CardEffects.OpenDiscardSelection(
                state,
                Effects.SI.ToolsOfTheTrade,
                toolsOfTheTrade
            );
        }
    }

    private static void ApplyCrimsonMantleAfterHandDraw(CombatState state, Random rng)
    {
        int damage = BuffSystem.Get(state.PlayerBuffs, BuffId.CrimsonMantleSelfDamage);
        if (damage > 0)
        {
            // The HP loss fires Inferno's burst, and `CreatureCmd.Damage` awaits the
            // AfterDeath hooks of anything that burst kills (Possess Strength/Speed
            // giving their stat back) before Crimson Mantle's GainBlock. Live ABB68XX
            // step-0736: The Lost died here and its Strength never came back.
            var living = CaptureLivingEnemiesForDeath(state);
            Effects.CardEffects.LoseHp(state, damage);
            ResolveAwaitedEnemyDeaths(state, living, rng);
        }
        int block = BuffSystem.Get(state.PlayerBuffs, BuffId.CrimsonMantleBlock);
        if (block > 0)
            Effects.CardEffects.GainUnpoweredBlock(state, block, rng);
    }

    internal static void ResolveTurnStartAutoPlays(CombatState state, Random rng)
    {
        AutoPlayBombardmentsFromExhaust(state, rng);
        AutoPlayMayhemCards(state, rng);
        // A parked Mayhem batch owes the encore after its answer (ResumePotionAutoPlays).
        if (state.PendingPotionAutoPlays is null)
            AutoPlayHistoryCourseEncore(state, rng);
    }

    /// <summary>
    /// `HistoryCourse.AfterAutoPrePlayPhaseEntered`: from turn TWO on, a DUPE of the last
    /// Attack played last turn is auto-played with the hook's choice context, so a choice
    /// the dupe raises (Headbutt's `FromCombatPile`) is the player's. A one-card batch with
    /// the same pause/resume as Mayhem's; `IterateHookListeners` puts powers before relics,
    /// so it runs after Mayhem's batch.
    /// </summary>
    /// <remarks>
    /// Unbound: `ChainsOfBindingPower.BeforeSideTurnEnd` cleared the original's affliction
    /// before the dupe is cloned from it. `CreateDupe` marks it IsDupe, so it leaves combat
    /// after its play (see PlacePotionBatchChild).
    /// </remarks>
    private static void AutoPlayHistoryCourseEncore(CombatState state, Random rng)
    {
        if (
            state.Turn + 1 <= 1
            || PlayerIsDead(state)
            || NoPrimaryEnemyLeft(state)
            || !Effects.RelicEffects.Has(state.Relics, Effects.RelicEffects.HistoryCourse)
            || state.LastAttackLastTurn is not { } encore
        )
        {
            return;
        }

        var dupe = encore with { Bound = false, Dupe = true };
        // A turn-start screen still open (no PlayerTurnSetup to wait for it) cannot share
        // the batch's pause. Keep the queued, automatic play this path used before.
        if (state.PendingSelection is not null || state.PendingCardPlay is not null)
        {
            state.AutoPlayQueue.Insert(0, dupe);
            return;
        }

        var batch = new PendingPotionAutoPlays
        {
            ResumesTurnStart = true,
            HistoryCourseEncore = true,
        };
        state.PendingPotionAutoPlays = batch;
        if (StartPotionBatchChild(state, batch, dupe, rng))
            return;
        state.PendingPotionAutoPlays = null;
    }

    /// <summary>
    /// RampartPower.AfterSideTurnStart (v0.111.0:21-30), shared by initial setup and
    /// subsequent player turns. Each living provider grants its amount only to living
    /// Turret Operators; extra turns skip the hook. The block is Unpowered.
    /// </summary>
    internal static void ApplyRampartAtPlayerTurnStart(CombatState state, bool extraTurn)
    {
        if (extraTurn || PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
            return;

        foreach (var owner in state.Enemies)
        {
            if (owner.Hp <= 0 || owner.Escaped)
                continue;
            int amount = BuffSystem.Get(owner.Buffs, BuffId.Rampart);
            if (amount <= 0)
                continue;
            foreach (var turret in state.Enemies)
            {
                if (turret.Hp > 0 && !turret.Escaped && turret.DefId == KE.TurretOperator)
                    turret.Block += amount;
            }
        }
    }

    private static StepResult UsePotion(CombatState state, int slot, Random rng)
    {
        if (slot < 0 || slot >= state.PotionSlots.Length || state.PotionSlots[slot] == 0)
        {
            return StepResult.Invalid;
        }

        int playerHpBefore = state.PlayerHp;
        Span<int> enemyHpsBefore = stackalloc int[state.Enemies.Count];
        for (int i = 0; i < state.Enemies.Count; i++)
            enemyHpsBefore[i] = state.Enemies[i].Hp;
        int potionId = state.PotionSlots[slot];
        // PotionModel.RemoveBeforeUse frees the slot before the effect runs.
        state.PotionSlots[slot] = 0;
        // BeforePotionUsed receives the potion's actual target. Self/player and AOE
        // potions cannot turn Surrounded toward the enemy resolver's fallback target.
        if (GeneratedData.Potions.Get(potionId).TargetsEnemy)
            TurnPlayerTowardTarget(state, card: null);
        Effects.PotionEffects.Apply(potionId, state, rng);
        // A solo player's choice keeps PotionModel.OnUseWrapper suspended.
        if (
            state.PendingSelection is { SourcePotionId: not 0 }
            || state.PendingPotionAutoPlays is not null
        )
            return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
        return FinishPotionUse(state, rng, playerHpBefore, enemyHpsBefore);
    }

    private static StepResult FinishPotionUse(
        CombatState state,
        Random rng,
        int playerHpBefore,
        ReadOnlySpan<int> enemyHpsBefore
    )
    {
        HandleEnemyDeaths(state, enemyHpsBefore, rng);
        // `ReptileTrinket.AfterPotionUsed`, and it is AFTER: a potion that grants Strength
        // of its own has already landed, so the two stack rather than one replacing it.
        bool playerDead = PlayerIsDead(state);
        if (!playerDead)
            Effects.RelicEffects.ApplyAfterPotionUsed(state);
        Effects.RelicEffects.RefreshBeltBuckle(state);
        Effects.RelicEffects.ApplyAfterPlayerHpChanged(state);
        // CombatManager.CheckForEmptyHand runs after the potion effect completes,
        // and only while this combat remains in progress.
        if (!playerDead && !NoPrimaryEnemyLeft(state))
            Effects.RelicEffects.ApplyAfterHandEmptied(state, rng);

        bool allDead = NoPrimaryEnemyLeft(state);
        return new StepResult(
            Terminal: playerDead || allDead,
            PlayerWon: allDead && !playerDead,
            Reward: ComputeReward(state, playerDead, allDead, playerHpBefore, enemyHpsBefore)
        );
    }

    private static void CleanupTurnCardCosts(CombatState state)
    {
        // PlayerCombatState.EndOfTurnCleanup visits ALL combat piles, including
        // a Power Potion card sent to discard because the hand was full.
        // CardModel.EndOfTurnCleanup also ends HasSingleTurnRetain there: an
        // Expertise card discarded mid-turn must not be retained after a reshuffle.
        Cleanup(state.Hand);
        Cleanup(state.DrawPile);
        Cleanup(state.DiscardPile);
        Cleanup(state.ExhaustPile);
        static void Cleanup(List<CardInstance> pile)
        {
            for (int i = 0; i < pile.Count; i++)
            {
                if (
                    pile[i].FreeThisTurn
                    || pile[i].RetainThisTurn
                    || pile[i].CostThisTurn != int.MinValue
                    || pile[i].CostOverrideThisTurn != int.MinValue
                )
                    pile[i] = pile[i] with
                    {
                        FreeThisTurn = false,
                        RetainThisTurn = false,
                        CostThisTurn = int.MinValue,
                        CostOverrideThisTurn = int.MinValue,
                    };
            }
        }
    }

    // Shaped reward: fraction of enemy HP dealt minus fraction of player HP lost,
    // plus ±1 terminal bonus for win/death.
    /// <summary>FairyInABottle, looked up by name rather than pinned to an id.</summary>
    private static readonly int FairyInABottlePotionId =
        GeneratedData.Potions.FindId("FairyInABottle")
        ?? throw new InvalidOperationException("No potion named FairyInABottle");

    /// <summary>
    /// Whether the player is really dead, after anything that refuses to let them be.
    /// </summary>
    /// <remarks>
    /// FairyInABottle is an Automatic potion whose <c>ShouldDie</c> returns false for its
    /// owner; <c>AfterPreventingDeath</c> then runs its <c>OnUse</c>, healing
    /// <c>Max(MaxHp * 0.3, 1)</c>. Nothing modelled it, so a run holding one died where
    /// the game had it stand back up -- a live capture shows the player go from 1 hp to
    /// 24 in the middle of a boss fight, 30% of an 80 maximum.
    ///
    /// <para>
    /// This is the preventable half of <c>CreatureCmd.Kill</c>. <c>Hook.ShouldDie</c> asks
    /// every <c>ShouldDie</c> listener before any <c>ShouldDieLate</c> one, so Fairy is
    /// spent before an unused Lizard Tail is. Fairy goes through
    /// <c>PotionModel.OnUseWrapper</c>: the potion leaves its slot before the heal, and
    /// <c>AfterPotionUsed</c> runs once the owner is alive again. Damage paths call this
    /// per hit, after their receiver hooks; the outer calls are a backstop for routes that
    /// still write HP directly. Force kills (Sandpit) must not come here.
    /// </para>
    /// </remarks>
    internal static bool PlayerIsDead(CombatState state)
    {
        if (state.PlayerHp > 0)
        {
            return false;
        }

        int slot = Array.IndexOf(state.PotionSlots, FairyInABottlePotionId);
        if (slot >= 0)
        {
            state.PotionSlots[slot] = 0;
            // Creature HP never goes below zero; poison still subtracts it raw.
            state.PlayerHp = 0;
            Effects.CardEffects.HealPlayer(state, Math.Max((int)(state.PlayerMaxHp * 0.3m), 1));
            Effects.RelicEffects.ApplyAfterPotionUsed(state);
            Effects.RelicEffects.RefreshBeltBuckle(state);
            return false;
        }

        return !Effects.RelicEffects.TryPreventPlayerDeathLate(state);
    }

    /// <summary>
    /// The paused reward when an attack inside the effect already ran a death pass:
    /// <see cref="ComputeReward" /> over the roster as it stood just before that pass
    /// (which still lines up with the pre-play baseline), plus what has happened since to
    /// those same creatures, matched by identity -- the <see cref="ResolveDeathsBeforePause" />
    /// rule. A creature the pass put in a dead one's slot has no "before" and is not
    /// damage; a dead one it revived in place does not un-pay its kill.
    /// </summary>
    private static float RewardAcrossAwaitedDeaths(
        CombatState state,
        EnemyState[] roster,
        int[] hpsAtPass,
        int playerHpBefore,
        ReadOnlySpan<int> enemyHpsBefore
    )
    {
        float totalMaxHp = 0f;
        float dealt = 0f;
        for (int i = 0; i < roster.Length; i++)
        {
            totalMaxHp += roster[i].MaxHp;
            if (i < enemyHpsBefore.Length)
                dealt += Math.Max(0, enemyHpsBefore[i] - hpsAtPass[i]);
            dealt += Math.Max(0, hpsAtPass[i] - roster[i].Hp);
        }

        float taken = Math.Max(0, playerHpBefore - state.PlayerHp);
        return (totalMaxHp > 0f ? dealt / totalMaxHp : 0f) - taken / (float)state.PlayerMaxHp;
    }

    /// <param name="enemyDamageBanked">
    /// Damage already dealt this step against a baseline that was since re-read -- see
    /// <see cref="DispatchEndTurnDeaths"/>. Zero for every step that re-reads nothing.
    /// </param>
    private static float ComputeReward(
        CombatState state,
        bool playerDead,
        bool allDead,
        int playerHpBefore,
        ReadOnlySpan<int> enemyHpsBefore,
        int enemyDamageBanked = 0
    )
    {
        float totalMaxHp = 0f;
        float dmgDealt = enemyDamageBanked;
        for (int i = 0; i < state.Enemies.Count; i++)
        {
            totalMaxHp += state.Enemies[i].MaxHp;
            if (i < enemyHpsBefore.Length)
            {
                dmgDealt += Math.Max(0, enemyHpsBefore[i] - state.Enemies[i].Hp);
            }
        }

        float dmgTaken = Math.Max(0, playerHpBefore - state.PlayerHp);

        float shaped =
            (totalMaxHp > 0f ? dmgDealt / totalMaxHp : 0f) - dmgTaken / (float)state.PlayerMaxHp;

        float terminal = (allDead && !playerDead) ? 1f : (playerDead ? -1f : 0f);

        return shaped + terminal;
    }

    /// <summary>
    /// <c>SlothPower.ShouldPlay</c>: <c>_cardsPlayedThisTurn &lt; Amount</c>.
    /// </summary>
    /// <remarks>
    /// A cap on the turn, not on the cards: nothing becomes Unplayable, the turn simply
    /// stops accepting plays once the count is reached. The count is the power's own
    /// (<see cref="CombatState.SlothCardsPlayedThisTurn" />): every CardPlay since the
    /// power existed, repeats and automatic plays included -- not the chosen-play total.
    /// `ShouldPlay` gates automatic plays too, so every auto-play entrance asks this.
    /// </remarks>
    /// <summary>
    /// `ChainsOfBindingPower.ShouldPlay`: a BOUND card is refused once a bound card has
    /// already been played this turn -- so the chains allow exactly one a turn, whatever
    /// else is in hand.
    /// </summary>
    /// <summary>
    /// `RingingPower.ShouldPlay`: a RINGING card is refused once ANY card play has STARTED
    /// this turn -- so the Beast's cry costs the player every card after their first.
    /// </summary>
    /// <remarks>
    /// `BeastCryMove` afflicts every card the player holds and `AfterCardEnteredCombat`
    /// catches the ones made later, so while the power stands the whole deck is Ringing and
    /// the gate is the power alone. The game skips a card that ALREADY carries an affliction
    /// -- one slot per card -- which in this fight has no other source to compete with it.
    ///
    /// The count is `History.CardPlaysStarted`, not the finished plays: `OnPlayWrapper`
    /// records the start before OnPlay, so a Strike that Hellraiser auto-plays out of a
    /// play's own draw (Pommel Strike's) is already refused. `ShouldPlay` gates automatic
    /// plays too (`CardCmd.AutoPlay`), so every auto-play entrance asks this as well.
    /// </remarks>
    internal static bool IsBlockedByRinging(CombatState state) =>
        BuffSystem.Get(state.PlayerBuffs, BuffId.Ringing) > 0 && state.CardPlaysStartedThisTurn > 0;

    private static bool IsBlockedByBound(CardInstance card, CombatState state) =>
        card.Bound && state.BoundCardPlayedThisTurn;

    /// <summary>
    /// `Hook.ShouldPlay` as `CardCmd.AutoPlay` asks it (CardCmd.cs:63). Every v0.111.0
    /// listener listed here ignores the `AutoPlayType` -- `ChainsOfBindingPower`,
    /// `SmoggyPower`, `SlothPower`, `RingingPower`, `Normality` and `VelvetChoker`. Their
    /// automatic play is refused exactly where a chosen one would be, and the card goes
    /// to its result pile unplayed (`MoveToResultPileWithoutPlaying`). The auto-play
    /// paths used to ask only Ringing, Sloth and the relic caps, so a Stampede swing at a
    /// Bound Stomp, after the one Bound card had been played, hit every enemy for 12.
    /// Enthralled is different: its ShouldPlay explicitly permits AutoPlayType != None.
    /// Keep that restriction in CanPlayFromHand, not these automatic-play entry points.
    /// </summary>
    private static bool IsBlockedByPlayHook(CombatState state, CardInstance card, CardDef def) =>
        IsBlockedBySmoggy(def, state)
        || IsBlockedBySloth(state)
        || IsBlockedByBound(card, state)
        || IsBlockedByRinging(state)
        || Effects.RelicEffects.BlocksFurtherCardPlays(state);

    internal static bool IsBlockedBySloth(CombatState state)
    {
        int sloth = BuffSystem.Get(state.PlayerBuffs, BuffId.Sloth);
        return sloth > 0 && state.SlothCardsPlayedThisTurn >= sloth;
    }

    /// <summary>
    /// `ModifyMaxEnergy` from the relics that change it per TURN rather than per combat.
    /// Bread is the only one; the flat +1 relics are folded into MaxEnergy at combat start.
    /// </summary>
    internal static int EffectiveMaxEnergyForTurn(CombatState state, int turnNumber) =>
        Effects.RelicEffects.ModifyMaxEnergy(state, EffectiveMaxEnergy(state), turnNumber);

    private static int EffectiveMaxEnergy(CombatState state)
    {
        // WasteAwayPower.ModifyMaxEnergy subtracts its amount, so every turn starts short.
        return Math.Max(
            0,
            state.MaxEnergy
                + BuffSystem.Get(state.PlayerBuffs, BuffId.PyrePower)
                // `DemesnePower.ModifyMaxEnergy`.
                + BuffSystem.Get(state.PlayerBuffs, BuffId.Demesne)
                // `FriendshipPower.ModifyMaxEnergy`, the same shape -- and also for the
                // rest of the combat, not one turn.
                + BuffSystem.Get(state.PlayerBuffs, BuffId.Friendship)
                - BuffSystem.Get(state.PlayerBuffs, BuffId.WasteAway)
        );
    }

    /// <summary>Cost of a card in hand, for callers that hold no CardDef (relics).</summary>
    internal static int EffectiveCost(CardInstance card, CombatState state) =>
        EffectiveCost(card, card.ModelDef(), state);

    /// <summary>
    /// `EnergyCost.GetWithModifiers(CostModifiers.Local)`: the card's base and its own
    /// per-card modifiers, WITHOUT the combat-wide hooks -- Corruption, Void Form, Brilliant
    /// Scarf, relic taxes, Borrowed Time, Curious, Veilpiercer, Tangled and the Free*Powers.
    /// The local terms of <see cref="EffectiveCost(CardInstance, CardDef, CombatState)" />,
    /// in its order. Touch of Insanity asks this and the full cost, and takes either.
    /// </summary>
    internal static int LocalEnergyCost(CardInstance card, CombatState state)
    {
        var def = card.ModelDef();
        // `_base < 0` and `CostsX` return the base untouched.
        if (def.HasEnergyCostX)
        {
            return 0;
        }

        if (def.Cost < 0)
        {
            return def.Cost;
        }

        bool sneckoed = card.CostOverrideThisTurn != int.MinValue;
        // Ember's zeroed base is still under a later absolute local modifier.
        if (card.FreeThisTurn || (card.Enchantment == Enchantment.TezcatarasEmber && !sneckoed))
        {
            return 0;
        }

        int cost = card.CostForCombat == int.MinValue ? def.Cost : card.CostForCombat;
        if (card.CostThisTurn != int.MinValue)
        {
            cost = Math.Min(cost, card.CostThisTurn);
        }

        if (card.Upgraded)
        {
            cost += def.UpgradeCost;
        }

        if (sneckoed)
        {
            cost = CostOverrideAmount(card, def);
        }

        // Stomp's `AddThisTurn`, Banshee's Cry's and Pinpoint's discounts and Flatten's
        // `SetThisTurn(0)` are all on the card's own EnergyCost, so they are local.
        if (def.Id == Effects.IC.Stomp)
        {
            cost -= state.AttackCardsPlayedThisTurn;
        }

        if (def.Name == "BansheesCry")
        {
            cost -= 2 * state.EtherealCardPlaysThisCombat;
        }

        if (def.Name == "Flatten" && state.OstyAttacksThisTurn > 0)
        {
            return 0;
        }

        if (def.Name == "Pinpoint")
        {
            cost -= state.SkillCardsPlayedThisTurn;
        }

        // Set before a Snecko override, so the override hides it until turn end.
        if (card.FreeUntilPlayed && !sneckoed)
        {
            return 0;
        }

        return Math.Max(0, cost + (sneckoed ? 0 : card.CostBump));
    }

    /// <summary>
    /// A Snecko Oil override as it stands now: the rolled amount, lowered to the new base
    /// by an upgrade that came after it (`CardEnergyCost.UpgradeBy`), then any reduce-only
    /// `CostThisTurn` set after it, plus the per-turn discounts that PREDATE it, which the
    /// callers subtract again in full.
    /// </summary>
    private static int CostOverrideAmount(CardInstance card, CardDef def)
    {
        int amount = card.CostOverrideThisTurn;
        if (card.Upgraded && !card.CostOverrideSetUpgraded && def.UpgradeCost < 0)
        {
            amount = Math.Min(amount, Math.Max(0, def.Cost + def.UpgradeCost));
        }

        // Snecko clears CostThisTurn when it lands, so one present now came later.
        if (card.CostThisTurn != int.MinValue)
        {
            amount = Math.Min(amount, card.CostThisTurn);
        }

        return amount + card.CostOverrideBaseline;
    }

    /// <summary>
    /// The per-turn relative discounts the cost functions derive from counters: Stomp's,
    /// Banshee's Cry's and Pinpoint's. What a Snecko override remembers as its baseline.
    /// </summary>
    internal static int CounterCostDiscount(CardDef def, CombatState state)
    {
        if (def.Id == Effects.IC.Stomp)
            return state.AttackCardsPlayedThisTurn;
        if (def.Name == "BansheesCry")
            return 2 * state.EtherealCardPlaysThisCombat;
        if (def.Name == "Pinpoint")
            return state.SkillCardsPlayedThisTurn;
        return 0;
    }

    // Returns the energy cost of a card after applying active powers (e.g. Corruption).
    /// <summary>
    /// The enchantment hooks that fire when a card is PLAYED, as EnchantmentModel.OnPlay.
    ///
    /// Sown and Swift each go off once and then set EnchantmentStatus.Disabled -- the
    /// spent flag here -- while Corrupted charges its 2 HP every single play. The flag is
    /// handed back through the state because CardEffects takes the card by value.
    /// </summary>
    /// <remarks>
    /// Called once per CardPlay, after the effect. The once-only enchantments disable
    /// themselves on the first play (Sown and Swift inside their own OnPlay, Glam and
    /// Vigorous at its AfterCardPlayed), which is why they also read the in-flight flag:
    /// the copy's own `EnchantSpent` is only written when the card lands.
    /// </remarks>
    private static void ApplyEnchantmentOnPlay(CombatState state, CardInstance card, Random rng)
    {
        switch (card.Enchantment)
        {
            case Enchantment.Sown when !card.EnchantSpent && !state.PlayedCardEnchantSpent:
                Effects.CardEffects.GainEnergy(state, card.EnchantAmount);
                state.PlayedCardEnchantSpent = true;
                break;
            case Enchantment.Swift when !card.EnchantSpent && !state.PlayedCardEnchantSpent:
                Effects.CardEffects.DrawCards(state, card.EnchantAmount, rng);
                state.PlayedCardEnchantSpent = true;
                break;
            case Enchantment.Glam when !card.EnchantSpent && !state.PlayedCardEnchantSpent:
                // `Glam.AfterCardPlayed` sets UsedThisCombat on the FIRST play. The extra
                // play the enchantment bought is a repeat of the same card, and a repeat
                // does not re-arm it -- so the fuse burns here, on the play that lit it.
                state.PlayedCardEnchantSpent = true;
                break;
            case Enchantment.Vigorous when !card.EnchantSpent && !state.PlayedCardEnchantSpent:
                // Vigorous pays out through the damage calculation, and AfterCardPlayed
                // disables it whether or not the card actually attacked.
                state.PlayedCardEnchantSpent = true;
                break;
            case Enchantment.Corrupted:
                // `ValueProp.Unblockable | Unpowered | Move`, and every play -- not once.
                // Unblockable means it does NOT touch block: this went through
                // DealDamageToPlayer, which spends block first, so a blocking player paid
                // the enchantment's price out of their block and a Body Slam played off it
                // then hit for less. LoseHp is the unblockable-unpowered path.
                Effects.CardEffects.LoseHp(state, 2);
                break;
            case Enchantment.Adroit:
                // `RecalculateValues` sets a BlockVar from the amount, so the block IS the
                // amount -- and unlike Nimble it is not restricted to Skills.
                Effects.CardEffects.GainBlock(state, card.EnchantAmount, rng);
                break;
            case Enchantment.Momentum:
                // `OnPlay` adds the amount to a running bonus the CARD carries. The damage
                // is read before this runs, which is why the first play of a freshly
                // enchanted card gets nothing.
                state.PlayedCardMomentumDamage += card.EnchantAmount;
                break;
            case Enchantment.Inky:
                // Inky.OnPlay applies Weak 1 to what the card hit. It is the only modelled
                // enchantment whose payload lands on an ENEMY rather than on its owner.
                Effects.CardEffects.ApplyInkyOnPlay(
                    state,
                    GeneratedData.Cards.Get(card.DefId),
                    rng
                );
                break;
            case Enchantment.Goopy:
                // AfterCardPlayed bumps the amount, and bumps the DECK version's too --
                // so a goopied Defend is worth one more block in every fight after this
                // one, for the rest of the run. The block itself is Amount - 1.
                state.PlayedCardEnchantGrew = true;
                break;
        }
    }

    private static int EffectiveCost(CardInstance card, CardDef def, CombatState state)
    {
        // `SetToFreeThisTurn` is `EnergyCost.SetThisTurnOrUntilPlayed(0)`: a LOCAL absolute
        // modifier, not a final price (LocalEnergyCost returns 0 for it). `CardEnergyCost.
        // GetWithModifiers` applies the local modifiers and THEN `Hook.ModifyEnergyCostInCombat`,
        // whose additive pass (Spiked Gauntlets, Borrowed Time, Curious, Tangled) still prices
        // a free card; only its Late pass zeroes. Returning 0 here let Bullet Time free a
        // Power under Spiked Gauntlets, which the game prices at 1.
        bool freeThisTurn = card.FreeThisTurn;
        bool sneckoed = card.CostOverrideThisTurn != int.MinValue;
        // TezcatarasEmber.OnEnchant does `EnergyCost.UpgradeBy(-cost)` -- it zeroes the
        // card's printed cost for good, so this is not a "free this turn" flag. A later
        // absolute local modifier (Snecko Oil) still sets it.
        if (card.Enchantment == Enchantment.TezcatarasEmber && !sneckoed)
        {
            return 0;
        }

        if (def.Type == CardType.Skill && BuffSystem.Get(state.PlayerBuffs, BuffId.Corruption) > 0)
        {
            return 0;
        }

        // `VoidFormPower.TryModifyEnergyCostInCombatLate` -- a Late hook, so it sits with
        // the other zero-returns rather than with the additive terms.
        if (IsFreeUnderVoidForm(state))
        {
            return 0;
        }

        // `BrilliantScarf.TryModifyEnergyCostInCombatLate` -- also Late, and also a flat
        // zero rather than a discount.
        if (Effects.RelicEffects.IsFreeUnderBrilliantScarf(state))
        {
            return 0;
        }

        int cost = card.CostForCombat == int.MinValue ? def.Cost : card.CostForCombat;

        // `SetThisTurnOrUntilPlayed` sits on top of the combat-long cost and under the
        // free-for-the-turn flags above. Enlightenment sets it to 1, not 0.
        if (card.CostThisTurn != int.MinValue)
        {
            cost = Math.Min(cost, card.CostThisTurn);
        }

        // The game says so on the card: base.EnergyCost.UpgradeBy(-1), extracted into
        // CardDef.UpgradeCost. This used to be three hand-written id lists covering
        // eighteen of the fifty-six cards that actually get cheaper, so Unmovable, Tank,
        // Corruption and the rest silently kept their unupgraded cost.
        if (card.Upgraded)
        {
            cost += def.UpgradeCost;
        }

        // Snecko Oil's absolute `SetThisTurnOrUntilPlayed`, the last local modifier.
        if (sneckoed)
        {
            cost = CostOverrideAmount(card, def);
        }

        // The free-this-turn zero wins over every other local term, as it always has here
        // (the local terms below are skipped for it); the global terms then apply to it.
        if (freeThisTurn)
        {
            cost = 0;
        }

        cost += Effects.RelicEffects.ExtraEnergyCost(state, def);

        if (def.Id == Effects.IC.Stomp && !freeThisTurn)
        {
            cost -= state.AttackCardsPlayedThisTurn;
        }

        // Banshee's Cry gets `EnergyVar(2)` cheaper per ETHEREAL card played this combat,
        // through two hooks that add up to the same number: `AfterCardEnteredCombat` pays
        // the whole backlog at once, and `AfterCardPlayed` pays each new one as it lands.
        // Derived here instead of stamped on the instance, so a copy generated mid-combat
        // is priced correctly without a creation hook. (A CLONE skips the backlog in the
        // game -- `IsClone` returns early -- and is priced with it here.)
        if (def.Name == "BansheesCry" && !freeThisTurn)
        {
            cost -= 2 * state.EtherealCardPlaysThisCombat;
        }

        // `Flatten.ReduceCost` is `EnergyCost.SetThisTurn(0)`, fired by `AfterAttack` when
        // the attacker was OSTY and again by `AfterCardEnteredCombat` for a copy that
        // arrives after the pet has already swung. A SET rather than a discount, so it
        // beats Borrowed Time's tax below -- and derived here for the same reason as
        // Banshee's Cry, so a late copy is priced right without a creation hook.
        if (def.Name == "Flatten" && state.OstyAttacksThisTurn > 0)
        {
            return 0;
        }

        // Pinpoint is one cheaper per SKILL played this turn -- `AddThisTurn(-1)` from
        // `AfterCardPlayed`, plus an `AfterCardEnteredCombat` that pays the backlog at
        // once. Derived rather than stamped, for the same reason as Banshee's Cry: the two
        // hooks add to the same number, and a copy that arrives late is priced right
        // without a creation hook.
        if (def.Name == "Pinpoint" && !freeThisTurn)
        {
            cost -= state.SkillCardsPlayedThisTurn;
        }

        // FranticEscape's OnPlay ends with `base.EnergyCost.AddThisCombat(1)` -- on the
        // CARD, so only the copy that was played gets dearer. A player-wide counter made
        // every escape in the deck cost more the moment one was used, and a live capture
        // caught it as an energy shortfall: the game could still afford a Strike on turn
        // three where the emulator could not.
        // `EnergyCost.SetUntilPlayed(0)` -- Rocket Punch primed by a generated Status.
        // Spent by the PLAY rather than by the turn, which is why it is not FreeThisTurn.
        if (card.FreeUntilPlayed && !sneckoed)
        {
            return 0;
        }

        if (!sneckoed && !freeThisTurn)
        {
            cost += card.CostBump;
        }

        // `BorrowedTimePower.TryModifyEnergyCostInCombat` adds its amount to every card its
        // owner plays. Additive and NOT a Late hook, so it sits with Tangled rather than
        // with the zero-returns below. It prices a free-this-turn card too (the local zero
        // above); only the Rocket Punch / Flatten early returns skip it.
        cost += BuffSystem.Get(state.PlayerBuffs, BuffId.BorrowedTime);
        // `CuriousPower.TryModifyEnergyCostInCombat`: POWER cards only, and it refuses a
        // card that already costs nothing -- `if (originalCost <= 0m) return false` -- so
        // it cannot drive a free Power below zero or fire on one at all.
        int curious = BuffSystem.Get(state.PlayerBuffs, BuffId.Curious);
        if (curious > 0 && def.Type == CardType.Power && cost > 0)
        {
            cost = Math.Max(0, cost - curious);
        }

        if (
            BuffSystem.Get(state.PlayerBuffs, BuffId.Veilpiercer) > 0
            && IsEtherealForPowers(state, card)
        )
        {
            return 0;
        }

        if (def.Type == CardType.Attack)
        {
            cost += BuffSystem.Get(state.PlayerBuffs, BuffId.Tangled);
            if (BuffSystem.Get(state.PlayerBuffs, BuffId.FreeAttackPower) > 0)
            {
                return 0;
            }
        }

        if (
            def.Type == CardType.Power
            && BuffSystem.Get(state.PlayerBuffs, BuffId.FreePowerPower) > 0
        )
        {
            return 0;
        }

        if (
            def.Type == CardType.Skill
            && BuffSystem.Get(state.PlayerBuffs, BuffId.FreeSkillPower) > 0
        )
        {
            return 0;
        }

        return cost;
    }

    /// <summary>
    /// Whether Void Form is still paying: the first `Amount` cards a turn cost nothing at
    /// all. Its own count, which auto-plays and repeats do not raise.
    /// </summary>
    private static bool IsFreeUnderVoidForm(CombatState state)
    {
        int voidForm = BuffSystem.Get(state.PlayerBuffs, BuffId.VoidForm);
        return voidForm > 0 && state.VoidFormCardsPlayedThisTurn < voidForm;
    }

    private static bool IsBlockedBySmoggy(CardDef def, CombatState state)
    {
        return def.Type == CardType.Skill
            && state.SkillPlayedWhileSmoggy
            && BuffSystem.Get(state.PlayerBuffs, BuffId.Smoggy) > 0;
    }

    private static bool IsBlockedByEnthralled(CardInstance card, CombatState state) =>
        card.DefId != Effects.ST.Enthralled
        && state.Hand.Any(handCard => handCard.DefId == Effects.ST.Enthralled);

    /// <summary>
    /// `CardModel.IsPlayable`, for the cards that override it: a rule about the STATE
    /// rather than about the cost or a debuff.
    /// </summary>
    /// <remarks>
    /// Grand Finale is the Silent's: playable only with an empty draw pile. The emulator
    /// used to check that inside the card's effect and deal nothing otherwise, which is a
    /// different game — the play was allowed, the energy and the card were spent, and an
    /// agent was offered an action the real game does not have.
    /// </remarks>
    private static bool IsPlayableNow(CombatState state, CardDef def) =>
        def.Name switch
        {
            // Grand Finale is the Silent's: playable only with an empty draw pile.
            "GrandFinale" => state.DrawPile.Count == 0,
            // `Clash.IsPlayable`: every card in HAND must be an Attack -- itself included,
            // and it is one. A single Skill in hand makes it unplayable, which is the whole
            // deckbuilding constraint the card exists for. It was stacked into a plain
            // damage body and so could be played from any hand at all.
            "Clash" => state.Hand.All(handCard =>
                GeneratedData.Cards.Get(handCard.DefId).Type == CardType.Attack
            ),
            _ => true,
        };

    /// <summary>
    /// What a play costs: the energy after every cost hook, and the stars after theirs.
    /// </summary>
    /// <remarks>
    /// `PlayerCombatState.HasEnoughResourcesFor` refuses a play whose STAR cost is more
    /// than the player holds -- a second resource, checked alongside the energy rather than
    /// folded into it. (The excess-energy-for-stars hook is on AbstractModel and nothing in
    /// the game overrides it, so there is no conversion to model.) `BrilliantScarf
    /// .TryModifyStarCost` and `VoidFormPower.TryModifyStarCost` each zero the stars
    /// alongside the energy -- both hooks, or the card is half free.
    ///
    /// Shared so that the question "can this be played" and the act of paying for it cannot
    /// price the card differently.
    /// </remarks>
    private static void ResolveCosts(
        CombatState state,
        CardInstance card,
        CardDef def,
        out int energyToSpend,
        out int starsToSpend
    )
    {
        energyToSpend = Math.Max(0, EffectiveCost(card, def, state));
        starsToSpend = def.HasStarCostX ? state.Stars : Math.Max(0, def.StarCost);
        if (Effects.RelicEffects.IsFreeUnderBrilliantScarf(state) || IsFreeUnderVoidForm(state))
        {
            starsToSpend = 0;
        }
    }

    /// <summary>
    /// `CardModel.CanPlay` for a card in hand -- every reason a play is refused, in ONE
    /// predicate, shared by <see cref="ValidActions" />, <see cref="PlayCard" /> and
    /// Whispering Earring.
    /// </summary>
    /// <remarks>
    /// It is one predicate because it used to be two, and they disagreed in BOTH
    /// directions. `ValidActions` omitted the star cost, Sloth, Bound and Ringing, so it
    /// offered actions `PlayCard` then refused -- the agent spent a step and the state did
    /// not move, which `StepResult.Invalid` cannot be told apart from a legal play that
    /// happened to earn nothing. `PlayCard` omitted `IsPlayableNow`, so the native
    /// `Sts2_Step` -- which takes any index, not just a listed one -- would play Grand
    /// Finale on a full draw pile or Clash out of a mixed hand, neither of which the game
    /// allows.
    ///
    /// Unifying them WIDENS what `ValidActions` hides and NARROWS what `PlayCard` accepts,
    /// so the legal-action set changes. That is the point: both edits move it toward
    /// `CanPlay`, which is the only definition the game has. See E487.
    ///
    /// ONE clause of `CanPlay` is deliberately not here: the NoLivingAllies refusal that
    /// stops an ally-targeted card in a solo run. Every such card is `MultiplayerOnly` and
    /// `CardFactory.FilterForPlayerCount` drops it from every pool, so no solo agent can
    /// hold one -- while the suite constructs them on purpose to pin the card BODIES, the
    /// convention Largesse's comment describes. Enforcing it here would delete that
    /// affordance to fix nothing reachable. It lives instead on the one caller that can
    /// actually pick a card the player did not: see <see cref="NeedsAnAllyToPlay" />.
    /// </remarks>
    internal static bool CanPlayFromHand(CombatState state, CardInstance card)
    {
        var def = card.ModelDef();
        ResolveCosts(state, card, def, out int energyToSpend, out int starsToSpend);

        return !def.Unplayable
            && IsPlayableNow(state, def)
            && energyToSpend <= state.Energy
            && starsToSpend <= state.Stars
            && !IsBlockedBySmoggy(def, state)
            && !IsBlockedByEnthralled(card, state)
            && !IsBlockedBySloth(state)
            && !IsBlockedByBound(card, state)
            && !IsBlockedByRinging(state)
            && !Effects.RelicEffects.BlocksFurtherCardPlays(state);
    }

    public static int[] ValidActions(CombatState state)
    {
        var actions = new List<int>();

        if (state.PendingSelection is { } selection)
        {
            for (int i = 0; i < selection.Candidates.Count; i++)
            {
                actions.Add(i);
            }

            // A screen with a minimum of zero can be declined, and the decline is one past
            // the last candidate. Well-Laid Plans is the only one so far.
            if (selection.Skippable)
            {
                actions.Add(selection.Candidates.Count);
            }

            return [.. actions];
        }

        for (int i = 0; i < state.Hand.Count; i++)
        {
            if (CanPlayFromHand(state, state.Hand[i]))
            {
                actions.Add(i);
            }
        }

        actions.Add(state.Hand.Count); // end turn always valid

        for (int s = 0; s < state.PotionSlots.Length; s++)
        {
            if (state.PotionSlots[s] != 0)
            {
                actions.Add(state.Hand.Count + 1 + s);
            }
        }

        return [.. actions];
    }

    /// <summary>
    /// Answers the open card-selection screen and closes it. Moving a card cannot end
    /// the combat, so this is never terminal.
    /// </summary>
    /// <summary>
    /// <c>IChoosable.OnChosen</c> for each of the Knowledge Demon's four curses.
    /// </summary>
    /// <remarks>
    /// Every one applies a POWER rather than adding a card to the deck, which is why the
    /// emulator's old shape — a buff on the player — was right in kind and wrong in
    /// everything else: it applied Disintegration always, at a flat 6, with no choice.
    /// </remarks>
    private static void ApplyChosenCurse(CombatState state, int cardId, int disintegration)
    {
        switch (cardId)
        {
            case Effects.ST.Disintegration:
                // DynamicVars["DisintegrationPower"], which the demon overwrites per cast
                // from _disintegrationDamageValues -- 6, then 7, then 8.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Disintegration, disintegration);
                break;
            case Effects.ST.MindRot:
                BuffSystem.Apply(state.PlayerBuffs, BuffId.MindRot, Run.RunConstants.MindRotAmount);
                break;
            case Effects.ST.Sloth:
                // A NEW SlothPower starts its `_cardsPlayedThisTurn` at zero; stacking onto
                // one the player already holds keeps that instance's count.
                if (BuffSystem.Get(state.PlayerBuffs, BuffId.Sloth) == 0)
                    state.SlothCardsPlayedThisTurn = 0;
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Sloth, Run.RunConstants.SlothAmount);
                break;
            case Effects.ST.WasteAway:
                BuffSystem.Apply(
                    state.PlayerBuffs,
                    BuffId.WasteAway,
                    Run.RunConstants.WasteAwayAmount
                );
                break;
        }
    }

    /// <summary>
    /// Answers the open selection, and -- once the screen has actually closed -- resumes
    /// the card play that raised it.
    /// </summary>
    /// <remarks>
    /// A screen that reopens for its next pick (Purity, Foregone Conclusion) is still the
    /// same `CardSelectCmd` call to the card waiting on it, so the play stays parked until
    /// nothing is left open. An invalid answer leaves the screen up and so resumes nothing.
    /// The parked play's target is restored for the answer as well as the resume: the
    /// rest of the effect runs inside the answer, and it is still aimed where the player
    /// aimed the card.
    /// </remarks>
    private static StepResult ResolveCardSelection(CombatState state, int action, Random rng)
    {
        if (state.PendingPotionAutoPlays is { Parked: not null })
        {
            int hpBeforeAnswer = state.PlayerHp;
            int[] enemyHpsBeforeAnswer = [.. state.Enemies.Select(e => e.Hp)];
            var childAnswer = AnswerCardSelection(state, action, rng);
            if (state.PendingSelection is not null)
                return childAnswer;
            return ResumePotionAutoPlays(state, rng, hpBeforeAnswer, enemyHpsBeforeAnswer);
        }

        if (state.PendingCardPlay is not { } pending)
        {
            var living = state.PendingTurnSetup is not null
                ? CaptureLivingEnemiesForDeath(state)
                : null;
            int setupHpBefore = state.PlayerHp;
            int[]? setupEnemyHps = living is not null
                ? state.Enemies.Select(e => e.Hp).ToArray()
                : null;
            var standalone = AnswerCardSelection(state, action, rng);
            if (living is not null)
                ResolveAwaitedEnemyDeaths(state, living, rng);
            if (state.PendingSelection is null)
                RunOwedEffectTail(state, TakePendingEffectTail(state), rng);
            if (state.PendingTurnSetup is not null)
            {
                PlayerTurnSetup.Resume(state, rng);
                bool dead = PlayerIsDead(state);
                bool won = NoPrimaryEnemyLeft(state);
                standalone = standalone with
                {
                    Terminal = dead || won,
                    PlayerWon = won && !dead,
                    Reward =
                        standalone.Reward
                        + ComputeReward(state, dead, won, setupHpBefore, setupEnemyHps),
                };
            }
            return standalone;
        }

        int callerTarget = state.TargetEnemyIndex;
        state.TargetEnemyIndex = pending.TargetEnemyIndex;
        // The answer and the effect tail are still inside the parked CardPlay, so its
        // fixed target applies to them as it does to the repeats after them.
        var callerPlayTarget = state.PlayTarget;
        state.PlayTarget = pending.PlayTarget;
        var callerBlockPlay = state.ActiveBlockPlay;
        state.ActiveBlockPlay = pending.BlockPlay;
        StepResult answered;
        int autoPlaysBeforeAnswer = state.AutoPlayQueue.Count;
        try
        {
            answered = AnswerCardSelection(state, action, rng);
            // `CardCmd.Discard` -> `DiscardAndDraw` awaits each Sly card's
            // `AutoPlay(SlyDiscard)` INSIDE the parked card's OnPlay, so the child resolves
            // (and lands in its pile) before the parent's remaining effect and completion.
            if (state.PendingSelection is null)
                PlaySlyDiscardsQueuedSince(state, autoPlaysBeforeAnswer, rng);
            // The rest of the parked OnPlay, once its screen has fully closed (a repeated
            // exhaust that reopens keeps it waiting): inside the same CardPlay, after the
            // exhaust and all of its hooks, before the enchantment and completion.
            if (state.PendingSelection is null && !pending.EffectTail.IsNone)
            {
                var tail = pending.EffectTail;
                pending.EffectTail = default;
                RunOwedEffectTail(state, tail, rng);
                // A resumed Havoc child that asks again leaves the tail owed.
                if (state.PendingSelection is not null)
                    pending.EffectTail = TakePendingEffectTail(state);
            }
        }
        finally
        {
            // A repeated or invalid answer still belongs to this same CardPlay.
            pending.BlockPlay = state.ActiveBlockPlay;
            state.ActiveBlockPlay = callerBlockPlay;
        }
        if (state.PendingSelection is not null)
        {
            state.TargetEnemyIndex = callerTarget;
            state.PlayTarget = callerPlayTarget;
            return answered;
        }

        state.PendingCardPlay = null;
        var resumed = ResumeCardPlay(state, pending, rng);
        state.TargetEnemyIndex = callerTarget;
        state.PlayTarget = callerPlayTarget;
        if (state.PendingTurnSetup is not null)
            PlayerTurnSetup.Resume(state, rng);
        return resumed with { Reward = resumed.Reward + answered.Reward };
    }

    private static StepResult AnswerCardSelection(CombatState state, int action, Random rng)
    {
        var selection = state.PendingSelection!;
        int skipAction = selection.Skippable ? selection.Candidates.Count : -1;
        if (action < 0 || (action >= selection.Candidates.Count && action != skipAction))
        {
            return StepResult.Invalid;
        }

        int playerHpBeforePotion = state.PlayerHp;
        int[]? enemyHpsBeforePotion =
            selection.SourcePotionId != 0
                ? state.Enemies.Select(enemy => enemy.Hp).ToArray()
                : null;

        if (action == skipAction)
        {
            // Declining answers the whole screen: the game's minimum is zero, not "at
            // least one per reopen", so keeping nothing ends it rather than asking again.
            var declined = selection.Kind;
            state.PendingSelection = null;
            // Gambler's Brew's skip is its CONFIRM: the picks it carried are discarded and
            // drawn for before the potion's use completes.
            if (declined == CardSelectionKind.DiscardAnyThenDraw && selection.SourcePotionId != 0)
                Effects.PotionEffects.ConfirmGamblersBrew(state, selection, rng);
            // Ashwater's likewise: its picks are exhausted in pick order, then the use ends.
            if (declined == CardSelectionKind.ExhaustAnyFromHand)
                Effects.PotionEffects.ConfirmAshwater(state, selection, rng);
            if (enemyHpsBeforePotion is not null)
                return FinishPotionUse(state, rng, playerHpBeforePotion, enemyHpsBeforePotion);
            if (declined == CardSelectionKind.DiscardAnyThenDraw)
            {
                CloseGamblingChipScreen(state, rng);
                return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
            }

            return ResumeOwedEndTurn(state, rng);
        }

        int index = selection.Candidates[action];
        state.PendingSelection = null;

        switch (selection.Kind)
        {
            case CardSelectionKind.DiscardToDrawPileTop:
                if (index < state.DiscardPile.Count)
                {
                    var card = state.DiscardPile[index];
                    state.DiscardPile.RemoveAt(index);
                    state.TopDeck(card);
                }

                break;

            case CardSelectionKind.ExhaustFromHand:
            case CardSelectionKind.ExhaustFromHandThenDraw:
            case CardSelectionKind.ExhaustFromHandRepeated:
                if (index < state.Hand.Count)
                {
                    var card = state.Hand[index];
                    state.Hand.RemoveAt(index);
                    Effects.CardEffects.ExhaustCard(state, card, rng: rng);
                }

                if (selection.Kind == CardSelectionKind.ExhaustFromHandThenDraw)
                {
                    Effects.CardEffects.DrawCards(state, selection.Amount, rng);
                }

                // Purity asks again until its picks are spent or the hand empties.
                if (
                    selection.Kind == CardSelectionKind.ExhaustFromHandRepeated
                    && selection.Amount > 1
                    && state.Hand.Count > 0
                )
                {
                    Effects.CardEffects.ReopenExhaustSelection(
                        state,
                        selection.SourceCardDefId,
                        selection.Amount - 1,
                        selection.Skippable
                    );
                }

                break;

            case CardSelectionKind.ExhaustFromDrawPile:
                if (index < state.DrawPile.Count)
                {
                    var doomedCard = state.DrawPile[index];
                    state.RemoveFromDrawPileAt(index);
                    Effects.CardEffects.ExhaustCard(state, doomedCard, rng: rng);
                }

                break;

            case CardSelectionKind.GrantEtherealInHand:
                if (index < state.Hand.Count)
                {
                    // `CardCmd.ApplyKeyword(card, Ethereal)` -- the per-copy grant, the same
                    // one Call of the Void's power hands out.
                    state.Hand[index] = state.Hand[index] with
                    {
                        EtherealForCombat = true,
                    };
                }

                break;

            case CardSelectionKind.TransfigureInHand:
                if (index < state.Hand.Count)
                {
                    var chosen = state.Hand[index];
                    var chosenDef = GeneratedData.Cards.Get(chosen.DefId);

                    // `EnergyCost.AddThisCombat(1)` unless the card costs X or its cost is
                    // negative -- an unplayable at -1 is not made to cost 0.
                    int bump =
                        !chosenDef.HasEnergyCostX && EffectiveCost(chosen, chosenDef, state) >= 0
                            ? 1
                            : 0;

                    // `BaseReplayCount++` -- the card plays one extra time, and the count
                    // rides on the copy exactly as Hidden Gem's Replay does.
                    state.Hand[index] = chosen with
                    {
                        CostBump = chosen.CostBump + bump,
                        ReplayCount = chosen.ReplayCount + 1,
                    };
                }

                break;

            case CardSelectionKind.TransformDrawPileToSoul:
                if (index < state.DrawPile.Count)
                {
                    // In place: `CardCmd.TransformTo` replaces the card where it sits, so
                    // the pile order the rest of the turn draws from is unchanged.
                    state.DrawPile[index] = new CardInstance(446, false); // Soul
                }

                break;

            case CardSelectionKind.DrawPileToHand when selection.SourcePotionId != 0:
                // Droplet of Precognition: `CardPileCmd.Add(card, Hand)`, which a full
                // hand redirects to the discard pile.
                Effects.PotionEffects.MoveDrawPileCardToHand(state, index);
                break;

            case CardSelectionKind.DrawPileToHand:
                if (
                    index < state.DrawPile.Count
                    && state.Hand.Count < Effects.CardEffects.MaxCardsInHand
                )
                {
                    var card = state.DrawPile[index];
                    state.RemoveFromDrawPileAt(index);
                    state.Hand.Add(card);
                }

                // Foregone Conclusion asks for two or three, and a `CardSelectorPrefs` with
                // a single count sets min and max alike -- so the screen reopens until the
                // picks are spent, the way Well Laid Plans' does. Every other caller asks
                // for one and never reaches this.
                if (selection.Amount > 1 && state.DrawPile.Count > 0)
                {
                    var more = new List<int>();
                    for (int i = 0; i < state.DrawPile.Count; i++)
                    {
                        more.Add(i);
                    }

                    state.PendingSelection = new PendingCardSelection
                    {
                        Kind = CardSelectionKind.DrawPileToHand,
                        Candidates = more,
                        SourceCardDefId = selection.SourceCardDefId,
                        Amount = selection.Amount - 1,
                        Skippable = selection.Skippable,
                    };
                    return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
                }

                break;

            case CardSelectionKind.DiscardAnyThenDraw when selection.SourcePotionId != 0:
                // Gambler's Brew: the pick is remembered, not discarded, and the screen
                // stays up until it is confirmed -- so the potion is not finished yet.
                Effects.PotionEffects.PickForGamblersBrew(state, selection, index);
                return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);

            case CardSelectionKind.ExhaustAnyFromHand:
                // Ashwater: remembered, not exhausted, until the confirm.
                Effects.PotionEffects.PickForAshwater(state, selection, index);
                return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);

            case CardSelectionKind.DiscardAnyThenDraw:
                if (index < state.Hand.Count)
                {
                    var pitched = state.Hand[index];
                    state.Hand.RemoveAt(index);
                    Effects.CardEffects.DiscardMovedCards(state, [pitched]);
                    state.GamblingChipDiscarded++;
                }

                if (!OpenGamblingChipScreen(state))
                {
                    CloseGamblingChipScreen(state, rng);
                }

                break;

            case CardSelectionKind.DiscardToHand when selection.SourcePotionId != 0:
                // Liquid Memories: free this turn, and a full hand sends it back to discard.
                Effects.PotionEffects.ReturnDiscardFreeThisTurn(state, index);
                break;

            case CardSelectionKind.FreeThisCombatInHand:
                Effects.PotionEffects.MakeFreeThisCombat(state, index);
                break;

            case CardSelectionKind.DiscardToHand:
                if (index < state.DiscardPile.Count)
                {
                    var recovered = state.DiscardPile[index];
                    state.DiscardPile.RemoveAt(index);
                    state.Hand.Add(recovered);
                }

                // Neow's Fury takes up to 2/3, so the screen reopens until they are spent
                // or the pile runs out -- and it stays declinable, since its minimum is
                // zero at every step and not only the first.
                if (
                    selection.Amount > 1
                    && state.DiscardPile.Count > 0
                    && state.Hand.Count < Effects.CardEffects.MaxCardsInHand
                )
                {
                    Effects.CardEffects.ReopenDiscardToHandSelection(
                        state,
                        selection.SourceCardDefId,
                        selection.Amount - 1,
                        selection.Skippable
                    );
                }

                break;

            case CardSelectionKind.MarkHandCardSly:
                if (index < state.Hand.Count)
                {
                    state.Hand[index] = state.Hand[index] with { SlyThisTurn = true };
                }

                break;

            case CardSelectionKind.CloneColorlessInHand:
                // `selection.CreateClone()` into hand -- the WHOLE card, upgrade and
                // enchantment included, and the original stays where it was. Combat-local
                // state does not travel: a copy made free for the turn is a copy of the
                // card, not of the discount.
                if (
                    index < state.Hand.Count
                    && state.Hand.Count < Effects.CardEffects.MaxCardsInHand
                )
                {
                    var original = state.Hand[index];
                    state.Hand.Add(
                        original with
                        {
                            DeckOrigin = null,
                            FreeThisTurn = false,
                            FreeUntilPlayed = false,
                            CostOverrideThisTurn = int.MinValue,
                            RetainThisTurn = false,
                            SlyThisTurn = false,
                            PlayedThisTurn = false,
                        }
                    );
                    Effects.CardEffects.NoteGeneratedCard(state);
                }

                break;

            case CardSelectionKind.DualWield:
                // `CardsVar(1)` CLONES of the chosen card into hand -- the whole card, as
                // Clone Colourless does above, and the original stays where it is.
                if (index < state.Hand.Count)
                {
                    var chosen = state.Hand[index];
                    for (int copy = 0; copy < selection.Amount; copy++)
                    {
                        if (state.Hand.Count >= Effects.CardEffects.MaxCardsInHand)
                        {
                            break;
                        }

                        // `CreateClone` copies the energy cost's local modifiers
                        // (`_energyCost.Clone`), so a Snecko Oil roll travels with it.
                        state.Hand.Add(
                            chosen with
                            {
                                DeckOrigin = null,
                                FreeThisTurn = false,
                                FreeUntilPlayed = false,
                                RetainThisTurn = false,
                                SlyThisTurn = false,
                                PlayedThisTurn = false,
                            }
                        );
                        Effects.CardEffects.NoteGeneratedCard(state);
                    }
                }

                break;

            case CardSelectionKind.UpgradeInHand:
                if (index < state.Hand.Count)
                {
                    state.Hand[index] = state.Hand[index] with { Upgraded = true };
                }

                break;

            case CardSelectionKind.QueueHandCardCopies:
                if (index < state.Hand.Count)
                {
                    // The chosen card is not moved -- Nightmare only reads it.
                    Effects.CardEffects.QueueHandCardCopies(
                        state,
                        state.Hand[index],
                        selection.Amount
                    );
                }

                break;

            case CardSelectionKind.DiscardFromHandRepeated:
                if (index < state.Hand.Count)
                {
                    var discarded = state.Hand[index];
                    state.Hand.RemoveAt(index);
                    Effects.CardEffects.DiscardMovedCards(state, [discarded]);
                }

                // Ask again until the picks are spent or the hand empties; the follow-up
                // rides along and is flushed by whichever call finds nothing left to ask.
                if (selection.Amount > 1 && state.Hand.Count > 0)
                {
                    Effects.CardEffects.ReopenDiscardSelection(
                        state,
                        selection.SourceCardDefId,
                        selection.Amount - 1,
                        selection.AfterSelectionToHand
                    );
                }
                else
                {
                    Effects.CardEffects.AddCardsToHand(state, selection.AfterSelectionToHand);
                }

                break;

            case CardSelectionKind.CurseOfKnowledge:
                if (index < selection.GeneratedCandidates.Count)
                {
                    ApplyChosenCurse(state, selection.GeneratedCandidates[index], selection.Amount);
                }

                break;

            case CardSelectionKind.GeneratedCardToHand:
                if (selection.SourcePotionId != 0 && index < selection.GeneratedCandidates.Count)
                {
                    var picked = new CardInstance(
                        selection.GeneratedCandidates[index],
                        false,
                        FreeThisTurn: true
                    );
                    var destination =
                        state.Hand.Count < Effects.CardEffects.MaxCardsInHand
                            ? state.Hand
                            : state.DiscardPile;
                    destination.Add(picked);
                    Effects.CardEffects.NoteGeneratedCard(state);
                    break;
                }
                if (
                    selection.SourceCardDefId == Effects.CL.Discovery
                    && index < selection.GeneratedCandidates.Count
                )
                {
                    // Discovery: SetToFreeThisTurn, then AddGeneratedCardToCombat(.., Hand).
                    Effects.CardEffects.AddGeneratedCardToHandOrDiscard(
                        state,
                        new CardInstance(
                            selection.GeneratedCandidates[index],
                            false,
                            FreeThisTurn: true
                        )
                    );
                    break;
                }
                if (
                    index < selection.GeneratedCandidates.Count
                    && state.Hand.Count < Effects.CardEffects.MaxCardsInHand
                )
                {
                    state.Hand.Add(
                        selection.GeneratedRetained
                            ? new CardInstance(
                                selection.GeneratedCandidates[index],
                                selection.GeneratedUpgraded
                            )
                            {
                                RetainForCombat = true,
                            }
                            : new CardInstance(
                                selection.GeneratedCandidates[index],
                                selection.GeneratedUpgraded,
                                FreeThisTurn: true
                            )
                    );
                }

                break;

            case CardSelectionKind.TransformHandToMinionStrike:
                if (index < state.Hand.Count)
                {
                    // `CardCmd.Transform(chosen, new MinionStrike)` -- in place, and the new
                    // card is upgraded when the transforming card was.
                    state.Hand[index] = new CardInstance(310, selection.GeneratedUpgraded);
                }

                break;

            case CardSelectionKind.TransformHandToMinionSacrifice:
                if (index < state.Hand.Count)
                {
                    state.Hand[index] = new CardInstance(309, selection.GeneratedUpgraded);
                }

                // `CardSelectorPrefs(prompt, 0, 999999999)`: any number, so the screen comes
                // back until the player declines or the hand is spent.
                if (state.Hand.Count > 0)
                {
                    var rest = new List<int>();
                    for (int i = 0; i < state.Hand.Count; i++)
                    {
                        if (state.Hand[i].DefId != 309)
                        {
                            rest.Add(i);
                        }
                    }

                    if (rest.Count > 0)
                    {
                        state.PendingSelection = new PendingCardSelection
                        {
                            Kind = CardSelectionKind.TransformHandToMinionSacrifice,
                            Candidates = rest,
                            SourceCardDefId = selection.SourceCardDefId,
                            Amount = selection.Amount,
                            Skippable = true,
                            GeneratedUpgraded = selection.GeneratedUpgraded,
                        };
                        return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
                    }
                }

                break;

            case CardSelectionKind.TransformDrawToMinionDiveBomb:
                if (index < state.DrawPile.Count)
                {
                    state.DrawPile[index] = new CardInstance(308, selection.GeneratedUpgraded);
                }

                if (selection.Amount > 1 && state.DrawPile.Count > 0)
                {
                    var more = new List<int>();
                    for (int i = 0; i < state.DrawPile.Count; i++)
                    {
                        more.Add(i);
                    }

                    state.PendingSelection = new PendingCardSelection
                    {
                        Kind = CardSelectionKind.TransformDrawToMinionDiveBomb,
                        Candidates = more,
                        SourceCardDefId = selection.SourceCardDefId,
                        Amount = selection.Amount - 1,
                        Skippable = selection.Skippable,
                        GeneratedUpgraded = selection.GeneratedUpgraded,
                    };
                    return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
                }

                break;

            case CardSelectionKind.AutoPlaySkillThrice:
                if (index < state.Hand.Count)
                {
                    // `CardCmd.AutoPlay(card)` three times over on the SAME CardModel.
                    // Queueing the instance three times made three cards: the live capture
                    // shows one Defend in the discard and 15 block, and the emulator left
                    // three Defends there -- two cards manufactured out of nothing, which
                    // stay in the deck for the rest of the combat.
                    var chosenSkill = state.Hand[index];
                    state.Hand.RemoveAt(index);
                    state.AutoPlayQueue.Add(
                        chosenSkill with
                        {
                            ReplayCount = chosenSkill.ReplayCount + 2,
                        }
                    );
                }

                break;

            case CardSelectionKind.HandToDrawPileTop:
                if (index < state.Hand.Count)
                {
                    var card = state.Hand[index];
                    state.Hand.RemoveAt(index);
                    state.TopDeck(card);
                }

                break;

            case CardSelectionKind.RetainForNextTurn:
                if (index < state.Hand.Count)
                {
                    state.Hand[index] = state.Hand[index] with { RetainThisTurn = true };
                }

                // Ask again until the picks are spent or nothing is left to offer.
                if (selection.Amount > 1)
                {
                    var again = new PendingCardSelection
                    {
                        Kind = CardSelectionKind.RetainForNextTurn,
                        Candidates = [],
                        SourceCardDefId = selection.SourceCardDefId,
                        Amount = selection.Amount - 1,
                        Skippable = true,
                    };
                    var left = new List<int>();
                    for (int i = 0; i < state.Hand.Count; i++)
                    {
                        if (!state.Hand[i].IsRetained())
                        {
                            left.Add(i);
                        }
                    }

                    if (left.Count > 0)
                    {
                        again.Candidates.AddRange(left);
                        state.PendingSelection = again;
                        return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
                    }
                }

                return ResumeOwedEndTurn(state, rng);
        }

        if (enemyHpsBeforePotion is not null)
            return FinishPotionUse(state, rng, playerHpBeforePotion, enemyHpsBeforePotion);

        if (state.PendingSelection is null && state.PendingEnemyTurn is { } pendingTurn)
        {
            state.PendingEnemyTurn = null;
            return ContinueEndTurn(
                state,
                rng,
                pendingTurn.PlayerHpBefore,
                pendingTurn.EnemyHpsBefore,
                pendingTurn.EnemyDamageBanked,
                extraTurn: false,
                remainingEnemies: pendingTurn.RemainingEnemies
            );
        }

        // A screen that was owed while another stood follows it. Toolbox's pick is the one
        // case: its screen is up at combat start where the Chip's would otherwise be.
        if (state.PendingSelection is null && state.GamblingChipOwed)
        {
            state.GamblingChipOwed = false;
            OpenGamblingChipScreen(state);
        }

        return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
    }

    /// <summary>
    /// Runs the end turn that was parked while a `RetainForNextTurn` screen stood, if one
    /// was. A selection raised any other way owes nothing and just returns.
    /// </summary>
    private static StepResult ResumeOwedEndTurn(CombatState state, Random rng) =>
        state.EndTurnAwaitingSelection
            ? EndTurn(state, rng)
            : new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);

    /// <summary>
    /// Weak, Frail and Vulnerable count down once a round, AFTER the enemy side's turn —
    /// every one of them ticks in `AfterSideTurnEnd(side == CombatSide.Enemy)`. That
    /// timing is the whole point: an enemy attacking into the player's last point of
    /// Vulnerable still hits for 1.5x, and an enemy the player made Weak still swings
    /// weakened before the stack runs out. Ticking before the enemies act instead loses
    /// the final turn of every one of these debuffs.
    ///
    /// A debuff applied to the PLAYER also skips one tick — PowerCmd.Apply sets
    /// SkipNextDurationTick for any debuff landing on a player-side creature, which is
    /// what lets a Vulnerable applied during the enemy's turn still be at full value on
    /// the player's next turn. Enemies get no such grace. The skip is tracked by
    /// remembering what the player held when the round began: a stack that grew during
    /// the round was applied during it, so this tick is the one it skips.
    /// </summary>
    /// <summary>Every card the player owns, wherever it is standing.</summary>
    private static int PlayerCardCount(CombatState state) =>
        state.DrawPile.Count + state.DiscardPile.Count + state.Hand.Count + state.ExhaustPile.Count;

    private static void TickDurationDebuffs(CombatState state)
    {
        foreach (var enemy in state.Enemies.ToArray())
        {
            BuffSystem.TickEndOfTurn(enemy.Buffs);
            // `SlumberPower.AfterSideTurnEnd` decrements and, at zero, runs `WakeUpMove` --
            // which sheds the beetle's Plating. The tick above is the decrement; this is
            // the wake that has to follow it.
            EnemyAI.WakeSlumberingBeetleIfDone(enemy);

            // `EscapeArtistPower.AfterSideTurnEnd` decrements only while `Amount > 1`, so
            // the timer FLOORS at one and never reaches zero -- at one it starts pulsing
            // instead. A plain duration tick would have removed it a turn early and left
            // the hopper's last turn showing nothing.
            if (BuffSystem.Get(enemy.Buffs, BuffId.EscapeArtist) > 1)
            {
                BuffSystem.Apply(enemy.Buffs, BuffId.EscapeArtist, -1);
            }
        }

        BuffSystem.TickEndOfTurn(state.PlayerBuffs, state.PlayerDebuffsAtRoundStart);
        state.PlayerDebuffsAtRoundStart = BuffSystem.DurationDebuffSnapshot(state.PlayerBuffs);
    }

    /// <summary>
    /// `Hook.AfterDeath` for whatever an end-of-turn effect has just killed, paid where the
    /// game awaits it -- inside the damage -- rather than after the enemy phase.
    /// </summary>
    /// <remarks>
    /// The baseline is re-read after a dispatch so each death is paid exactly once: a
    /// segment left at 0 while it waits to reattach reads 0 from then on, and a creature
    /// removed or summoned no longer shifts the indices the later passes compare against.
    /// Left alone when nobody died, so a nonlethal turn keeps the snapshot it always had.
    ///
    /// <para>
    /// The same snapshot is what the step's reward measures damage against, so whatever it
    /// had counted up to the dispatch is banked first -- read before the death hooks, which
    /// only ever restore HP (a revive, the Waterfall Giant) and would otherwise hide it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// `PoisonPower.Trigger` on an enemy: `min(Amount, 1 + Accelerant)` iterations, each
    /// dealing the CURRENT amount and decrementing only while the owner lives -- so with
    /// Accelerant 1 a poison of 5 deals 5 then 4 and the stack falls by two. Accelerant
    /// lives on the PLAYER and is read from the poisoned creature's living OPPONENTS.
    /// The iteration count is fixed before the first tick, as `TriggerCount` is read once.
    /// Outbreak calls the same `Trigger()` mid-play.
    /// </summary>
    internal static void TriggerEnemyPoison(CombatState state, EnemyState enemy)
    {
        int poison = BuffSystem.Get(enemy.Buffs, BuffId.Poison);
        if (poison <= 0)
        {
            return;
        }

        int accelerant =
            state.PlayerHp > 0 ? BuffSystem.Get(state.PlayerBuffs, BuffId.Accelerant) : 0;
        int triggers = Math.Min(poison, 1 + accelerant);
        for (int tick = 0; tick < triggers && enemy.Hp > 0; tick++)
        {
            int amount = BuffSystem.Get(enemy.Buffs, BuffId.Poison);
            Effects.CardEffects.DealPoisonDamageToEnemy(state, enemy, amount);
            if (enemy.Hp > 0)
            {
                BuffSystem.Apply(enemy.Buffs, BuffId.Poison, -1);
            }
        }
    }

    private static void DispatchEndTurnDeaths(
        CombatState state,
        ref int[] enemyHpsBefore,
        ref int enemyDamageBanked,
        Random rng
    )
    {
        bool anyDied = false;
        for (int i = 0; i < state.Enemies.Count && i < enemyHpsBefore.Length; i++)
        {
            if (enemyHpsBefore[i] > 0 && state.Enemies[i].Hp <= 0)
            {
                anyDied = true;
                break;
            }
        }

        if (!anyDied)
        {
            return;
        }

        // The same per-index sum `ComputeReward` takes, so banking and then measuring the
        // rest against the new baseline adds up to what one measurement would have read.
        for (int i = 0; i < state.Enemies.Count && i < enemyHpsBefore.Length; i++)
        {
            enemyDamageBanked += Math.Max(0, enemyHpsBefore[i] - state.Enemies[i].Hp);
        }

        HandleEnemyDeaths(state, enemyHpsBefore, rng);
        enemyHpsBefore = state.Enemies.Select(enemy => enemy.Hp).ToArray();
    }

    /// <summary>
    /// Takes a card the end-of-turn pass snapshotted out of the hand. The first equal copy
    /// is the snapshotted one: equal copies are always in the same snapshot list, and
    /// anything drawn meanwhile is appended behind them.
    /// </summary>
    private static bool TakeFromHand(CombatState state, CardInstance card)
    {
        int index = state.Hand.IndexOf(card);
        if (index < 0)
        {
            return false;
        }

        state.Hand.RemoveAt(index);
        return true;
    }

    // The nested play owns references to its original living creatures: reinforcements
    // cannot take over an old HP-array index while its awaited death hooks are running.
    internal static EnemyState[] CaptureLivingEnemiesForDeath(CombatState state) =>
        state.Enemies.Where(enemy => enemy.Hp > 0).ToArray();

    private static void HandleEnemyDeaths(
        CombatState state,
        ReadOnlySpan<int> enemyHpsBefore,
        Random rng
    )
    {
        List<EnemyState>? candidates = null;
        for (int i = 0; i < state.Enemies.Count && i < enemyHpsBefore.Length; i++)
        {
            var enemy = state.Enemies[i];
            if (enemyHpsBefore[i] > 0 && enemy.Hp <= 0 && !enemy.DeathHooksHandled)
                (candidates ??= []).Add(enemy);
        }
        if (candidates is not null)
            ResolveAwaitedEnemyDeaths(
                state,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidates),
                rng
            );
        else if (state.EnemyDeathDispatchDepth == 0)
            RemoveDeadEnemies(state);
    }

    internal static void ResolveAwaitedEnemyDeaths(
        CombatState state,
        ReadOnlySpan<EnemyState> livingBefore,
        Random rng
    )
    {
        state.EnemyDeathDispatchDepth++;
        try
        {
            foreach (var dying in livingBefore)
            {
                if (dying.Hp > 0 || dying.DeathHooksHandled)
                    continue;

                // Claim BEFORE Gremlin Horn can recursively draw/play/kill another
                // creature. A resumed parent or outer turn pass must not pay it again.
                dying.DeathHooksHandled = true;
                // GremlinHorn.AfterDeath: EnergyVar(1) and CardsVar(1) for any creature that
                // dies on the other side. It checks only the SIDE -- not the
                // wasRemovalPrevented flag it is handed -- so it pays out even for a death
                // something else undoes, which is why it sits above the revive branches
                // rather than after them.
                if (Effects.RelicEffects.Has(state.Relics, Effects.RelicEffects.GremlinHorn))
                {
                    Effects.CardEffects.GainEnergy(state, 1);
                    Effects.CardEffects.DrawCards(state, 1, rng);
                }

                // Side effects of a death that are nobody's revive, and so must not sit in
                // the chain below: an `else if` there means "this creature came back, stop
                // looking", and these two are neither exclusive with a revive nor with each
                // other. Threading one of them into the chain is how the Test Subject's
                // respawn briefly ended up guarded on the creature not being an amalgam.
                if (dying.DefId == KE.TorchHeadAmalgam)
                {
                    EnrageQueenIfWaitingToBurnBright(state);
                }

                if (BuffSystem.Get(dying.Buffs, BuffId.PossessSpeed) > 0)
                {
                    // PossessSpeedPower.AfterDeath returns every point of Dexterity its owner
                    // took off the player -- so killing The Forgotten hands the whole debuff
                    // back, which the emulator never did.
                    int possessed = dying.PossessedPlayerDexterity;
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Dexterity, possessed);
                    dying.PossessedPlayerDexterity = 0;
                    BuffSystem.Remove(dying.Buffs, BuffId.PossessSpeed);
                }

                if (BuffSystem.Get(dying.Buffs, BuffId.PossessStrength) > 0)
                {
                    // `PossessStrengthPower.AfterDeath`, the same for The Lost's Strength.
                    // Written beside its twin rather than anywhere else, because the two
                    // being apart is how one of them stayed unmodelled.
                    int possessed = dying.PossessedPlayerStrength;
                    Effects.RelicEffects.GainPlayerStrength(state, possessed);
                    dying.PossessedPlayerStrength = 0;
                    BuffSystem.Remove(dying.Buffs, BuffId.PossessStrength);
                }

                // `DampenPower.AfterDeath`: the power tracks the CASTERS that applied it and
                // lifts once the last of them is gone -- and `AfterRemoved` is what gives the
                // upgrades back. Read off the roster rather than a caster set, because the Magi
                // Knight is the only source: no living Knight means no caster left.
                if (
                    BuffSystem.Get(state.PlayerBuffs, BuffId.Dampen) > 0
                    && dying.DefId == KE.MagiKnight
                    && !state.Enemies.Any(other =>
                        other.DefId == KE.MagiKnight
                        && other.Hp > 0
                        && !ReferenceEquals(other, dying)
                    )
                )
                {
                    Effects.CardEffects.RemoveDampen(state);
                }

                // The revive chain proper: at most one of these applies, and the ones that
                // bring the creature back `continue` rather than falling through to the
                // it-really-died work below.
                if (BuffSystem.Get(dying.Buffs, BuffId.Surprise) > 0)
                {
                    SpawnGremlinMercReinforcements(state, rng, dying.StolenGold);
                }
                else if (
                    dying.DefId == KE.WaterfallGiant
                    && BuffSystem.Get(dying.Buffs, BuffId.SteamEruption) > 0
                )
                {
                    // SteamEruptionPower.AfterDeath -> TriggerAboutToBlowState. The
                    // original restores HP immediately and forces one warning turn;
                    // reaching zero here is not victory.
                    var giant = dying;
                    giant.Hp = giant.MaxHp = 999999999;
                    giant.DeathHooksHandled = false;
                    giant.SteamEruptionPhase = 1;
                    giant.CurrentIntent = new Intent(IntentType.Stun, 0);
                    giant.SecondaryIntent = null;
                    giant.Buffs.RemoveAll(p => p.Id != BuffId.SteamEruption);
                }
                else if (TryReviveIllusion(dying))
                {
                    continue;
                }
                else if (TryRespawnAxebot(state, dying, rng))
                {
                    continue;
                }
                else if (TryReattachSegment(state, dying))
                {
                    continue;
                }
                else if (TryRespawnTestSubject(dying))
                {
                    continue;
                }
                else if (TryEnrageCrabPartner(state, dying))
                {
                    // Not a revive: the dead half stays dead, so this does not `continue`.
                    TurnPlayerToFaceSurvivor(state);
                }

                if (BuffSystem.Get(dying.Buffs, BuffId.Infested) > 0)
                {
                    SpawnPhrogParasiteWrigglers(state, rng, dying);
                    BuffSystem.Remove(dying.Buffs, BuffId.Infested);
                }
                else if (!dying.Escaped && BuffSystem.Get(dying.Buffs, BuffId.Heist) > 0)
                {
                    // HeistPower.BeforeDeath adds an extra REWARD for its OWNER's death, and it
                    // does so in every encounter -- the merc's own fight was excluded here,
                    // which is the one fight the power exists for. ModifyGoldGained is applied
                    // when the row is claimed, the way it is for the fight's ordinary gold.
                    // Paid once: the power goes with the payout.
                    state.StolenBackGold += BuffSystem.Get(dying.Buffs, BuffId.Heist);
                    BuffSystem.Remove(dying.Buffs, BuffId.Heist);
                }

                // `SwipePower.BeforeDeath` gives the stolen card back -- but only on DEATH.
                // A Hopper that reaches ESCAPE leaves with it, which is why `Escaped` is
                // checked here exactly as it is for the Heist above.
                if (dying.StolenCard is { } swiped && !dying.Escaped)
                {
                    state.StolenBackCards.Add(swiped.DeckOrigin?.Card ?? swiped);
                    dying.StolenCard = null;
                    BuffSystem.Remove(dying.Buffs, BuffId.Swipe);
                }

                if (dying.DefId == KE.SlitheringStrangler)
                {
                    BuffSystem.Remove(state.PlayerBuffs, BuffId.Constrict);
                }

                // ShrinkPower.AfterDeath also runs for retaliation and other
                // unpowered deaths, not just the card-attack damage path.
                RemoveShrinkFromDeadApplier(state, dying);

                // HexPower.AfterDeath removes the power only for its original applier,
                // after the branches above have ruled out prevented removal.
                if (ReferenceEquals(state.HexApplier, dying))
                {
                    BuffSystem.Remove(state.PlayerBuffs, BuffId.Hex);
                    state.HexApplier = null;
                }

                Effects.CardEffects.CheapenMelancholyForDeath(state);

                foreach (var enemy in state.Enemies.Where(e => e.Hp > 0))
                {
                    int ravenous = BuffSystem.Get(enemy.Buffs, BuffId.Ravenous);
                    if (ravenous <= 0)
                    {
                        continue;
                    }

                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, ravenous);
                    // Creature.StunInternal REPLACES the next move with one STUNNED move that
                    // must be performed before it can transition, so SetMoveImmediate ignores a
                    // second stun while it is pending: two allies dying in one turn still cost
                    // the slug one turn, and it then resumes the move it had been announcing.
                    if (BuffSystem.Get(enemy.Buffs, BuffId.Stunned) <= 0)
                    {
                        BuffSystem.Apply(enemy.Buffs, BuffId.Stunned, 1);
                    }
                    // The readout changes the moment the ally dies: a live capture shows the
                    // surviving slug announcing a Stun, not the attack it was going to make.
                    // Leaving the old intent up told an agent to expect 12 damage from a
                    // creature that is about to sit the turn out. Unknown is what a stunned
                    // enemy already announces elsewhere (TerrorEel does it this way).
                    enemy.CurrentIntent = new Intent(IntentType.Unknown, 0);
                    enemy.SecondaryIntent = null;
                }
            }
        }
        finally
        {
            state.EnemyDeathDispatchDepth--;
            if (state.EnemyDeathDispatchDepth == 0)
                RemoveDeadEnemies(state);
        }
    }

    private static void RemoveDeadEnemies(CombatState state) =>
        state.Enemies.RemoveAll(enemy => enemy.Hp <= 0 && EnemyAI.ShouldRemoveOnDeath(enemy));

    internal static void RemoveShrinkFromDeadApplier(CombatState state, EnemyState enemy)
    {
        if (enemy.Hp > 0 || !ReferenceEquals(state.ShrinkApplier, enemy))
            return;
        BuffSystem.Remove(state.PlayerBuffs, BuffId.Shrink);
        state.ShrinkApplier = null;
    }

    internal static void ResolveSteamEruptionDeath(CombatState state, EnemyState enemy)
    {
        if (
            enemy.DefId != KE.WaterfallGiant
            || enemy.Hp > 0
            || BuffSystem.Get(enemy.Buffs, BuffId.SteamEruption) <= 0
        )
            return;
        // CreatureCmd.Damage awaits death before the card's following effects.
        // Bash therefore applies a fresh Vulnerable to the restored giant, and
        // subsequent hits can damage it. The outer death pass sees it alive and
        // does not dispatch the same death hooks twice.
        var before = state.Enemies.Select(e => ReferenceEquals(e, enemy) ? 1 : e.Hp).ToArray();
        HandleEnemyDeaths(state, before, state.NicheHpRng ?? new Random(0));
    }

    /// <summary>
    /// <c>Creature.IsPrimaryEnemy</c>: an enemy that can stay alive on its own. The game's
    /// own words for the other kind are "a secondary enemy will automatically die unless
    /// there's also a living primary enemy", and what makes one secondary is carrying
    /// <c>MinionPower</c> or <c>IllusionPower</c>.
    /// </summary>
    private static bool IsPrimaryEnemy(EnemyState enemy) =>
        BuffSystem.Get(enemy.Buffs, BuffId.Minion) <= 0
        && BuffSystem.Get(enemy.Buffs, BuffId.Illusion) <= 0;

    /// <summary>
    /// Whether the combat is won, which <c>CombatManager.IsEnding</c> decides by asking
    /// whether any PRIMARY enemy is still alive.
    /// </summary>
    /// <remarks>
    /// Counting every creature instead is wrong in both directions. A Fogmog's eye revives
    /// forever, so a fight it outlives could never be won; and a Gas Bomb left over after
    /// the Living Fog dies would keep a finished fight running.
    /// </remarks>
    /// <summary>
    /// Whether the fight is won. A creature at 0 HP normally is not in it any more — but
    /// <c>AdaptablePower.ShouldStopCombatFromEnding</c> returns true, so a Test Subject
    /// waiting out its respawn turn still is.
    /// </summary>
    /// <remarks>
    /// Adaptable holds the fight through a delayed revival. Surprise, Stock, Infested
    /// and SteamEruption also hold it during their awaited death continuation, before
    /// replacements or restored HP exist. An Eye With Teeth is not primary, and a
    /// reattaching Decimillipede segment does NOT hold the fight open.
    /// </remarks>
    internal static bool NoPrimaryEnemyLeft(CombatState state) =>
        !state.Enemies.Any(enemy =>
            IsPrimaryEnemy(enemy)
            && (
                enemy.Hp > 0
                || BuffSystem.Get(enemy.Buffs, BuffId.Adaptable) > 0
                || BuffSystem.Get(enemy.Buffs, BuffId.Surprise) > 0
                || BuffSystem.Get(enemy.Buffs, BuffId.Stock) > 0
                || BuffSystem.Get(enemy.Buffs, BuffId.Infested) > 0
                || BuffSystem.Get(enemy.Buffs, BuffId.SteamEruption) > 0
            )
        );

    /// <summary>
    /// <c>IllusionPower</c>: the owner is never removed from combat when it dies, keeps
    /// its buffs through the death, and spends its next turn on a forced REVIVE_MOVE that
    /// heals it back to full.
    /// </summary>
    /// <remarks>
    /// So a Fogmog's Eye With Teeth cannot be killed off -- swing at it and it is back at
    /// 6 HP a turn later, Distracting again. The emulator left it dead at 0, which is
    /// worse than a missing attacker: the live run's next swing at the eye resolved
    /// against the emulator's first LIVING enemy, so every blow the player spent on the
    /// illusion landed on the Fogmog instead and the fight ended floors early.
    /// </remarks>
    private static bool TryReviveIllusion(EnemyState enemy)
    {
        if (BuffSystem.Get(enemy.Buffs, BuffId.Illusion) <= 0)
        {
            return false;
        }

        if (BuffSystem.Get(enemy.Buffs, BuffId.Reviving) > 0)
        {
            return true;
        }

        // `CreatureCmd.Kill` runs `RemoveAllPowersAfterDeath` after `AfterDeath` has set
        // REVIVE_MOVE, and IllusionPower vetoes that for everything but a non-temporary
        // debuff. A Weak landed on the Parafright before the killing blow is therefore
        // gone when it comes back, and its first Slam is announced at full strength.
        enemy.Buffs.RemoveAll(power => BuffSystem.IsRemovedFromDyingIllusion(power.Id));
        BuffSystem.Apply(enemy.Buffs, BuffId.Reviving, 1);
        // A HealIntent, so the readout says what the turn will be spent on.
        enemy.CurrentIntent = new Intent(IntentType.Buff, 0);
        enemy.Block = 0;
        return true;
    }

    /// <summary>
    /// <c>ReattachPower</c>: a killed Decimillipede segment comes back one turn later.
    /// </summary>
    /// <remarks>
    /// <c>AfterDeath</c> puts it in DEAD_MOVE and <c>DoReattach</c> heals it by the
    /// power's Amount — but only <c>if (!AreAllOtherSegmentsDead())</c>. That guard IS
    /// the fight: the elite is won by emptying all three inside one window, and the
    /// emulator, which left a killed segment dead, let it be taken apart one at a time.
    ///
    /// It heals 25 rather than to full, which is what separates this from the Fogmog
    /// eye's revive — a segment that comes back is not a fresh one.
    /// </remarks>
    /// <summary>
    /// `ReattachPower.AreAllOtherSegmentsDead`, inverted. Read by the reattach itself and
    /// by `CardEffects.TriggersFatal`, which is the same question asked from the player's
    /// side: a Decimillipede is only really dead when its last segment is.
    /// </summary>
    internal static bool AnyOtherSegmentAlive(CombatState state, EnemyState enemy) =>
        state.Enemies.Any(other =>
            !ReferenceEquals(other, enemy)
            && BuffSystem.Get(other.Buffs, BuffId.Reattach) > 0
            && (other.Hp > 0 || BuffSystem.Get(other.Buffs, BuffId.Reviving) > 0)
        );

    private static bool TryReattachSegment(CombatState state, EnemyState enemy)
    {
        if (BuffSystem.Get(enemy.Buffs, BuffId.Reattach) <= 0)
        {
            return false;
        }

        if (BuffSystem.Get(enemy.Buffs, BuffId.Reviving) > 0)
        {
            return true;
        }

        // The last one standing stays down, and takes the rest of the creature with it.
        if (!AnyOtherSegmentAlive(state, enemy))
        {
            return false;
        }

        // Death strips the segment's powers. `PowerModel.ShouldPowerBeRemovedAfterOwnerDeath`
        // defaults to TRUE and `ReattachPower` is one of the few that overrides it to false,
        // so a segment comes back carrying its Reattach and nothing else -- the Strength its
        // BULK turns banked is gone with the rest.
        //
        // Kept: Reattach itself, and the Reviving bookkeeping applied just below. Without
        // this a segment that died mid-fight came back with every point of Strength it had
        // ever gained, and announced damage the game never deals.
        enemy.Buffs.RemoveAll(buff => buff.Id is not (BuffId.Reattach or BuffId.Reviving));

        // TWO, not one: the phase it dies in is not the phase it reattaches in. See the
        // revive loop in EndTurn.
        BuffSystem.Apply(enemy.Buffs, BuffId.Reviving, 2);
        // A HealIntent, so the readout says what the turn is spent on.
        enemy.CurrentIntent = new Intent(IntentType.Buff, 0);
        enemy.Block = 0;
        return true;
    }

    /// <summary>
    /// <c>CrabRagePower.AfterDeath</c>: the surviving half takes Strength 6 and 99 block.
    /// </summary>
    /// <remarks>
    /// It fires for a death on its OWN side that is not its own, so killing one half of
    /// the Kaiser Crab enrages the other. None of it was modelled, which made halving the
    /// boss free — and the half left standing is the one the player then has to survive.
    /// </remarks>
    /// <summary>
    /// <c>Queen.AfterDeath</c>: when the Torch Head Amalgam dies and the Queen is alive,
    /// she stops burning bright — and if the move she has ALREADY announced is the one she
    /// was going to spend on her partner, <c>SetMoveImmediate(EnragedState)</c> replaces it
    /// there and then. So a player who kills the amalgam on the turn she declared
    /// BURN_BRIGHT_FOR_ME does not get a wasted enemy turn out of it; they get an enrage.
    /// </summary>
    /// <remarks>
    /// Every other consequence of the amalgam's death is read off the roster by
    /// <c>SelectIntent</c>, which is why only this one needs a hook: it is a change to an
    /// intent that has already been chosen, the same shape as Ravenous's stun.
    /// </remarks>
    private static void EnrageQueenIfWaitingToBurnBright(CombatState state)
    {
        foreach (var queen in state.Enemies)
        {
            if (queen.DefId != KE.Queen || queen.Hp <= 0 || queen.LastMove != QueenBurnBrightMove)
            {
                continue;
            }

            queen.LastMove = QueenEnrageMove;
            queen.CurrentIntent = new Intent(IntentType.Buff, 2);
        }
    }

    /// <summary>The Queen's own move numbering, as <c>EnemyAI.SelectIntent</c> assigns it.</summary>
    private const int QueenBurnBrightMove = 2;

    private const int QueenEnrageMove = 5;

    private static bool TryEnrageCrabPartner(CombatState state, EnemyState dead)
    {
        if (BuffSystem.Get(dead.Buffs, BuffId.CrabRage) <= 0)
        {
            return false;
        }

        bool enraged = false;
        foreach (var other in state.Enemies)
        {
            if (
                ReferenceEquals(other, dead)
                || other.Hp <= 0
                || BuffSystem.Get(other.Buffs, BuffId.CrabRage) <= 0
            )
            {
                continue;
            }

            BuffSystem.Apply(other.Buffs, BuffId.Strength, Run.RunConstants.CrabRageStrength);
            // BlockVar(99, ValueProp.Unpowered): Dexterity does not touch it.
            other.Block += Run.RunConstants.CrabRageBlock;
            // `PowerCmd.Remove(this)` -- the rage is spent on the partner it mourned. With
            // exactly two crabs there is no third death to reach it, so this changes no
            // reachable outcome; it is here so the power cannot fire twice if one ever
            // joins them.
            BuffSystem.Remove(other.Buffs, BuffId.CrabRage);
            enraged = true;
        }

        return enraged;
    }

    /// <summary>
    /// <c>SurroundedPower.BeforeCardPlayed</c>: the player turns to face what they aim at.
    /// </summary>
    /// <remarks>
    /// **This is the Kaiser Crab.** Whichever half you target, you turn to face it — and
    /// the OTHER half is then at your back, hitting for 1.5x. A live capture settles it:
    /// the Crusher opens at 18 against a base of 12 while the Rocket opens at its bare 3,
    /// and the moment the player's first card lands on the Crusher the bonus moves to
    /// the Rocket for the rest of the fight (27 for an 18 beam, 49 for a 33 laser).
    ///
    /// Modelling the turn on DEATH alone — which is where reading the decompiled source
    /// stopped — gets turn one right and every turn after it wrong.
    /// </remarks>
    private static void TurnPlayerTowardTarget(CombatState state, CardInstance? card)
    {
        int facing = BuffSystem.Get(state.PlayerBuffs, BuffId.Surrounded);
        if (facing == 0)
        {
            return;
        }

        // Only a card that AIMS at something turns the player: `cardPlay.Target != null`,
        // which is TargetType.AnyEnemy and nothing else. An AllEnemies attack -- a
        // Whirlwind, a Cleave -- performs no target selection and does NOT turn you.
        //
        // This was approximated as "is an attack" until the card table carried the real
        // TargetType, which is now extracted: 183 AnyEnemy, 35 AllEnemies, 9 Random.
        // The target type is the card's dynamic one, so a Fan of Knives Shiv does not turn
        // you either. Knife Trap hands its Shivs `cardPlay.Target` even then, but the trap's
        // own AnyEnemy play has already turned the player toward that same enemy.
        if (card is not null)
        {
            var def = card.Value.ModelDef();
            if (Effects.CardEffects.EffectiveTarget(state, def) != CardTarget.AnyEnemy)
            {
                return;
            }
        }

        // The aimed-at enemy, resolved the way every single-target effect resolves it:
        // the explicit target when there is one, else the first living enemy.
        int index = state.TargetEnemyIndex;
        var target =
            index >= 0 && index < state.Enemies.Count && EnemyAI.IsTargetable(state.Enemies[index])
                ? state.Enemies[index]
                : state.Enemies.FirstOrDefault(EnemyAI.IsTargetable);
        if (target is null)
        {
            return;
        }

        FacePast(state, target, facing);
    }

    /// <summary>
    /// <c>SurroundedPower.UpdateDirection</c>: turn only if the target is the side the
    /// player's back is currently to.
    /// </summary>
    private static void FacePast(CombatState state, EnemyState target, int facing)
    {
        bool turn =
            facing == Run.RunConstants.FacingRight
                ? BuffSystem.Get(target.Buffs, BuffId.BackAttackLeft) > 0
                : BuffSystem.Get(target.Buffs, BuffId.BackAttackRight) > 0;
        if (!turn)
        {
            return;
        }

        BuffSystem.Remove(state.PlayerBuffs, BuffId.Surrounded);
        BuffSystem.Apply(
            state.PlayerBuffs,
            BuffId.Surrounded,
            facing == Run.RunConstants.FacingRight
                ? Run.RunConstants.FacingLeft
                : Run.RunConstants.FacingRight
        );
    }

    /// <summary>
    /// <c>SurroundedPower.AfterDeath</c>: the player turns to face whoever is left.
    /// </summary>
    /// <remarks>
    /// It only turns when every REMAINING hittable enemy is on one side — which for the
    /// Kaiser Crab means one half has died. Turning to face the survivor is what takes
    /// the 1.5x away from it, and it is why the multiplier cannot be a constant baked
    /// into the announced damage.
    /// </remarks>
    private static void TurnPlayerToFaceSurvivor(CombatState state)
    {
        int facing = BuffSystem.Get(state.PlayerBuffs, BuffId.Surrounded);
        if (facing == 0)
        {
            return;
        }

        var living = state.Enemies.Where(e => e.Hp > 0).ToArray();
        if (living.Length == 0)
        {
            return;
        }

        // Only when every remaining hittable enemy is on one side, which for this fight
        // means one half has died.
        bool allLeft = living.All(e => BuffSystem.Get(e.Buffs, BuffId.BackAttackLeft) > 0);
        bool allRight = living.All(e => BuffSystem.Get(e.Buffs, BuffId.BackAttackRight) > 0);
        if (allLeft || allRight)
        {
            FacePast(state, living[0], facing);
        }
    }

    private static bool TryRespawnAxebot(CombatState state, EnemyState enemy, Random rng)
    {
        int index = state.Enemies.IndexOf(enemy);
        if (enemy.DefId != KE.Axebot)
        {
            return false;
        }

        int stock = BuffSystem.Get(enemy.Buffs, BuffId.Stock);
        if (stock <= 0)
        {
            return false;
        }

        var def = GeneratedData.Enemies.Get(KE.Axebot);
        var band = def.HpBand(state.AscensionLevel);
        int bonus = (3 - stock) * 10;
        int hp = EnemyAI.RollSummonedHp(band.Min + bonus, band.Max + bonus, state, rng);
        // A respawned Axebot is built with a stock override, which starts its machine on
        // BOOT_UP -- index 0 -- rather than on the HAMMER_UPPERCUT a fresh one opens with.
        var replacement = new EnemyState
        {
            DefId = KE.Axebot,
            Hp = hp,
            MaxHp = hp,
            Slot = enemy.Slot,
            CurrentIntent = new Intent(
                IntentType.Defend,
                Ascension.Value(state.AscensionLevel, Ascension.DeadlyEnemies, 15, 10)
            ),
            SecondaryIntent = new Intent(IntentType.Buff, 0),
        };
        BuffSystem.Apply(replacement.Buffs, BuffId.Stock, stock - 1);
        state.Enemies[index] = Effects.RelicEffects.Spawned(state, replacement);
        return true;
    }

    /// <summary>
    /// <c>AdaptablePower.AfterDeath</c>: the Test Subject stops the combat ending, is not
    /// removed, and <c>TriggerDeadState</c> puts it in RESPAWN_MOVE — a state with
    /// <c>MustPerformOnceBeforeTransitioning</c>, so **the respawn costs it a turn**. The
    /// emulator used to heal it the instant it fell and announce an attack for the turn
    /// after, which handed the player neither the free turn nor the readout the game gives
    /// them. It is the Fogmog illusion's shape, and it uses the same machinery.
    /// </summary>
    private static bool TryRespawnTestSubject(EnemyState enemy)
    {
        if (enemy.DefId != KE.TestSubject || BuffSystem.Get(enemy.Buffs, BuffId.Adaptable) <= 0)
        {
            return false;
        }

        if (BuffSystem.Get(enemy.Buffs, BuffId.Reviving) > 0)
        {
            return true;
        }

        // CreatureCmd.Kill removes ordinary powers after Adaptable's death hook.
        // Adaptable and PainfulStabs explicitly survive death; Enrage, Strength,
        // and temporary debuffs do not carry into the next form.
        enemy.Buffs.RemoveAll(buff => buff.Id is not (BuffId.Adaptable or BuffId.PainfulStabs));
        BuffSystem.Apply(enemy.Buffs, BuffId.Reviving, 1);
        // RESPAWN_MOVE declares a HealIntent and then a BuffIntent, so the readout is a
        // Buff — which is what the turn is spent on.
        enemy.CurrentIntent = new Intent(IntentType.Buff, 0);
        enemy.Block = 0;
        return true;
    }

    /// <summary>
    /// <c>RespawnMove</c> itself, on the enemy turn the revive is spent. Respawns 1 heals
    /// to SecondFormHp and takes PainfulStabs; respawns 2 heals to ThirdFormHp, takes
    /// Nemesis, and drops both of the earlier powers — which is what ends the second
    /// form's climbing Multi Claw.
    /// </summary>
    private static void RespawnTestSubject(EnemyState enemy, int ascension)
    {
        if (BuffSystem.Get(enemy.Buffs, BuffId.PainfulStabs) <= 0)
        {
            int second = Ascension.Value(ascension, Ascension.ToughEnemies, 212, 200);
            enemy.Hp = second;
            enemy.MaxHp = second;
            enemy.Block = 0;
            // MULTI_CLAW, whose hit count is read off MoveIndex - 2.
            enemy.MoveIndex = 2;
            BuffSystem.Apply(enemy.Buffs, BuffId.PainfulStabs, 1);
            return;
        }

        int third = Ascension.Value(ascension, Ascension.ToughEnemies, 313, 300);
        enemy.Hp = third;
        enemy.MaxHp = third;
        enemy.Block = 0;
        // PHASE3_LACERATE, the head of the third form's three-cycle.
        enemy.MoveIndex = 4;
        BuffSystem.Remove(enemy.Buffs, BuffId.Adaptable);
        BuffSystem.Remove(enemy.Buffs, BuffId.PainfulStabs);
        BuffSystem.Apply(enemy.Buffs, BuffId.Nemesis, 1);
    }

    private static void SpawnPhrogParasiteWrigglers(CombatState state, Random rng, EnemyState phrog)
    {
        int insertIndex = state.Enemies.IndexOf(phrog) + 1;
        for (int i = 0; i < 4 && state.Enemies.Count < 6; i++)
        {
            // The four do NOT act in step. Wriggler's INIT_MOVE is a conditional branch
            // on the creature's slot — wriggler1 and wriggler3 start on NASTY_BITE,
            // wriggler2 and wriggler4 on WRIGGLE — and they alternate from there, so the
            // pack always has half biting while the other half buys Strength. Spawning
            // them all on the same move made four bites land at once and then nothing.
            var wriggler = CreateEnemy(
                KE.Wriggler,
                rng,
                new Intent(IntentType.Unknown, 0),
                stunned: true,
                // SPAWNED_MOVE burns an index before INIT_MOVE reads the slot, so the
                // parity is inverted here: a wriggler that must OPEN on the bite starts
                // odd and lands on the bite once its stunned turn has ticked it over.
                moveIndex: (i + 1) % 2,
                state: state
            );
            state.Enemies.Insert(insertIndex + i, Effects.RelicEffects.Spawned(state, wriggler));
        }
    }

    private static void SpawnGremlinMercReinforcements(
        CombatState state,
        Random rng,
        int stolenGold
    )
    {
        // SurprisePower.AfterDeath CREATES Fat first (rolling Niche HP), applies
        // Heist, then creates/adds Sneaky and finally adds Fat. Creation order differs
        // from roster order; neither new gremlin is in the roster during either roll.
        var fatGremlin = CreateEnemy(
            28,
            rng,
            new Intent(IntentType.Unknown, 0),
            stunned: true,
            state.AscensionLevel,
            state: state
        );
        // SurprisePower.AfterDeath applies HeistPower(Gold) to the Fat Gremlin, before either
        // gremlin is added. PowerCmd.Apply returns on a zero amount, so a Merc that stole
        // nothing hands over no power.
        if (stolenGold > 0)
            BuffSystem.Apply(fatGremlin.Buffs, BuffId.Heist, stolenGold);
        // SurprisePower.AfterDeath marks the encounter only when the total taken is above
        // zero, and the mark is what separates "escaped with nothing" (half gold) from
        // "escaped with the loot" (none).
        state.MercGoldWasStolen |= stolenGold > 0;
        state.Enemies.Add(
            Effects.RelicEffects.Spawned(
                state,
                CreateEnemy(
                    78,
                    rng,
                    new Intent(IntentType.Unknown, 0),
                    stunned: true,
                    state.AscensionLevel,
                    state: state
                )
            )
        );
        state.Enemies.Add(Effects.RelicEffects.Spawned(state, fatGremlin));
    }

    /// <summary>
    /// `StampedePower.AfterAutoPostPlayPhaseEntered`: `Amount` times, a random playable
    /// Attack from hand off `Rng.Shuffle`, then `await CardCmd.AutoPlay`. True when a
    /// swing parked on a selection (Headbutt's discard pick) -- the remaining swings and
    /// the rest of the turn are owed by <see cref="ResumePotionAutoPlays" />.
    /// </summary>
    /// <remarks>
    /// The loop runs all `Amount` times. Nothing in it checks whether the combat has
    /// ended. A swing that wins the fight therefore does not stop the later picks:
    /// `NextItem` still draws `Rng.Shuffle` while the hand holds a playable Attack. Only
    /// `CardCmd.AutoPlay` returns early on `IsOverOrEnding`, and it leaves the pick in
    /// hand.
    /// </remarks>
    private static bool AutoPlayStampedeAttacks(CombatState state, Random rng, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var attackIndexes = state
                .Hand.Select((card, index) => (card, index))
                .Where(item =>
                {
                    var def = GeneratedData.Cards.Get(item.card.DefId);
                    return def.Type == CardType.Attack && !def.Unplayable;
                })
                .Select(item => item.index)
                .ToList();
            if (attackIndexes.Count == 0)
            {
                return false;
            }

            // StampedePower picks with Rng.Shuffle.NextItem(items) — not the
            // card-selection stream, despite also choosing a card from hand.
            var stampedeRng = state.ShuffleRng ?? rng;
            int handIndex = attackIndexes[stampedeRng.Next(attackIndexes.Count)];
            // `CardCmd.AutoPlay`: `IsOverOrEnding || card.Owner.Creature.IsDead` returns.
            if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
                continue;
            if (StartStampedeSwing(state, handIndex, remaining: count - i - 1, rng))
                return true;
        }

        return false;
    }

    /// <summary>
    /// `CardCmd.AutoPlay` on a card in HAND, through the parkable batch child the draw-pile
    /// auto-plays use, so a selection its effect raises is asked rather than auto-picked.
    /// </summary>
    private static bool StartStampedeSwing(
        CombatState state,
        int handIndex,
        int remaining,
        Random rng
    )
    {
        var card = state.Hand[handIndex];
        var def = card.ModelDef();

        // CardCmd.AutoPlay still attempts a card while a boss is reviving. With no
        // hittable target, a single-target card goes to its result pile without play
        // hooks; Stampede has already consumed its card-selection roll.
        if (
            IsBlockedByPlayHook(state, card, def)
            || (
                Effects.CardEffects.EffectiveTarget(state, def) == CardTarget.AnyEnemy
                && !state.Enemies.Any(EnemyAI.IsTargetable)
            )
        )
        {
            state.Hand.RemoveAt(handIndex);
            if (ShouldExhaustAfterPlay(card))
            {
                Effects.CardEffects.ExhaustCard(state, card, rng: rng);
            }
            else
            {
                state.DiscardPile.Add(card with { FreeThisTurn = false });
            }

            return false;
        }

        var batch = new PendingPotionAutoPlays
        {
            ResumesEndTurn = true,
            StampedeRemaining = remaining,
            HandEnemyHpsBefore = [.. state.Enemies.Select(enemy => enemy.Hp)],
        };
        state.Hand.RemoveAt(handIndex);
        state.PendingPotionAutoPlays = batch;
        if (StartPotionBatchChild(state, batch, card, rng))
            return true;
        state.PendingPotionAutoPlays = null;
        return false;
    }

    /// <summary>
    /// `HowlFromBeyond.AfterAutoPostPlayPhaseEntered` auto-plays the card whenever it is
    /// sitting in the owner's EXHAUST pile as the play phase ENDS, so being exhausted is
    /// not the end of it: it swings once more, free, before the enemies act.
    /// </summary>
    /// <remarks>
    /// Bombardment's shape with two differences, and both of them matter.
    ///
    /// The phase is `AfterAutoPostPlayPhaseEntered`, the END of the play phase, where
    /// Bombardment's is `AfterAutoPrePlayPhaseEnteredEarly`, the start of the next one.
    /// Against an enemy about to die that is the difference between killing it before its
    /// attack lands and after.
    ///
    /// And Howl does NOT exhaust itself. It declares no `CanonicalKeywords` -- only an
    /// `ExtraHoverTips` naming the keyword, the same way Havoc does -- so
    /// `GetResultPileTypeForCardPlay` sends it to the DISCARD pile, both when the player
    /// plays it and when this replays it. `CardCmd.AutoPlay(choiceContext, this, null)`
    /// takes no `forceExhaust`, unlike the `AutoPlayFromDrawPile` that Havoc uses to
    /// exhaust what it plays. So the replay fires once per trip to the exhaust pile and
    /// spends the card, where Bombardment's Exhaust keyword returns it to the pile and
    /// fires every turn for the rest of the combat. Something ELSE has to exhaust Howl --
    /// Havoc, an exhaust-from-hand effect -- or the hook never sees it at all.
    /// </remarks>
    private static void AutoPlayHowlsFromExhaust(CombatState state, Random rng)
    {
        foreach (
            var card in state.ExhaustPile.Where(c => c.DefId == Effects.IC.HowlFromBeyond).ToList()
        )
        {
            if (NoPrimaryEnemyLeft(state))
            {
                return;
            }

            // `CardCmd.AutoPlay` MOVES the card: out of the exhaust pile, into Play, then
            // to its result pile -- the discard, for a card with no Exhaust keyword.
            // Leaving it in place while auto-playing it keeps the exhausted copy AND adds
            // a discarded one, so the card would swing every turn forever.
            RemoveFirstMatchingCard(state.ExhaustPile, card);
            AutoPlay(state, card, rng);
        }
    }

    /// <summary>
    /// `Bombardment.AfterAutoPrePlayPhaseEnteredEarly` auto-plays the card whenever it is
    /// sitting in the owner's EXHAUST pile as the turn's play phase opens. So the card is
    /// 18 damage once and then 18 damage every turn for the rest of the combat, for free --
    /// exhausting it is the point rather than the cost. The emulator had a plain attack.
    /// </summary>
    /// <remarks>
    /// Howl From Beyond's shape with a different phase AND a different result pile: Howl
    /// is `AfterAutoPostPlayPhaseEntered`, at the END of the play phase, and carries no
    /// Exhaust keyword, so its replay spends it into the discard. See
    /// <see cref="AutoPlayHowlsFromExhaust" />.
    /// </remarks>
    private static void AutoPlayBombardmentsFromExhaust(CombatState state, Random rng)
    {
        foreach (
            var card in state
                .ExhaustPile.Where(c => GeneratedData.Cards.Get(c.DefId).Name == "Bombardment")
                .ToList()
        )
        {
            if (NoPrimaryEnemyLeft(state))
            {
                return;
            }

            // `CardCmd.AutoPlay` MOVES the card: out of the exhaust pile, into Play, then
            // to its result pile -- which for an Exhaust card is the exhaust pile again.
            // Leaving it in place while auto-playing it adds a second copy every turn, so
            // the pile grows and next turn plays two.
            RemoveFirstMatchingCard(state.ExhaustPile, card);
            AutoPlay(state, card, rng);
        }
    }

    /// <summary>
    /// `MayhemPower.AfterAutoPrePlayPhaseEntered`:
    /// `CardPileCmd.AutoPlayFromDrawPile(Amount, Top, forceExhaust: false)` -- the call
    /// Distilled Chaos makes, so it is the same batch: shuffle-if-empty before each pick,
    /// every card staged into Play, then each auto-played in turn, and a child's
    /// `CardSelectCmd` (Armaments' `FromHandForUpgrade`) asks the player.
    /// </summary>
    private static void AutoPlayMayhemCards(CombatState state, Random rng)
    {
        int mayhem = BuffSystem.Get(state.PlayerBuffs, BuffId.MayhemPower);
        if (mayhem > 0)
            BeginPotionAutoPlayBatch(state, mayhem, rng, resumesTurnStart: true);
    }

    private static void ReturnQueuedCardsToHandBeforeDraw(CombatState state)
    {
        // `ThrummingHatchet.BeforeHandDraw` / Bolas: `CardPlaysFinished ... Card == this`,
        // then `CardPileCmd.Add(this, Hand)` unless `this` is already in hand. So only a
        // card that was PLAYED last turn can answer, and the answer is that card as it now
        // stands (Razor Tooth's upgrade included), moved rather than copied. The copy's own
        // `PlayedThisTurn` is that history; it is cleared after this runs. A played card
        // already in hand is not searched, so it moves nothing -- and no unplayed copy of
        // the same card can ever stand in for it. A known deck copy must match exactly;
        // generated cards without that origin use the same upgrade, then any played copy.
        // The moved card leaves the piles, so it cannot answer a second queued return.
        foreach (var queued in state.ReturnToHandBeforeDraw)
        {
            CardInstance card;
            bool found = queued.DeckOrigin is not null
                ? TryTakeReturningCard(state, queued, ReturnMatch.SameDeckCopy, out card)
                : TryTakeReturningCard(state, queued, ReturnMatch.SameUpgrade, out card)
                    || TryTakeReturningCard(state, queued, ReturnMatch.AnyPlayedCopy, out card);
            if (found)
            {
                state.Hand.Add(card with { FreeThisTurn = false });
            }
        }
        state.ReturnToHandBeforeDraw.Clear();
    }

    private enum ReturnMatch
    {
        SameDeckCopy,
        SameUpgrade,
        AnyPlayedCopy,
    }

    private static bool TryTakeReturningCard(
        CombatState state,
        CardInstance queued,
        ReturnMatch match,
        out CardInstance card
    )
    {
        bool Matches(CardInstance c) =>
            c.DefId == queued.DefId
            && c.PlayedThisTurn
            && match switch
            {
                ReturnMatch.SameDeckCopy => queued.DeckOrigin is not null
                    && c.DeckOrigin == queued.DeckOrigin,
                ReturnMatch.SameUpgrade => c.Upgraded == queued.Upgraded,
                _ => true,
            };

        int index = state.DiscardPile.FindIndex(Matches);
        if (index >= 0)
        {
            card = state.DiscardPile[index];
            state.DiscardPile.RemoveAt(index);
            return true;
        }

        index = state.DrawPile.FindIndex(Matches);
        if (index >= 0)
        {
            card = state.RemoveFromDrawPileAt(index);
            return true;
        }

        index = state.ExhaustPile.FindIndex(Matches);
        if (index >= 0)
        {
            card = state.ExhaustPile[index];
            state.ExhaustPile.RemoveAt(index);
            return true;
        }

        card = default;
        return false;
    }

    /// <summary>
    /// `NightmarePower.BeforeHandDraw` adds its clones and then removes itself, so they
    /// arrive ONCE, at the start of the next turn and before the draw. Delivering them
    /// when the card was played -- what the emulator used to do -- gives away the reason
    /// the card costs three energy.
    /// </summary>
    private static void DeliverQueuedCardCopiesBeforeDraw(CombatState state)
    {
        foreach (var copy in state.CopiesToHandBeforeDraw)
        {
            if (state.Hand.Count >= Effects.CardEffects.MaxCardsInHand)
            {
                break;
            }

            state.Hand.Add(copy);
        }

        state.CopiesToHandBeforeDraw.Clear();
        // `await PowerCmd.Remove(this)` -- the power goes when its copies are handed over,
        // whether or not the hand had room for them.
        BuffSystem.Remove(state.PlayerBuffs, BuffId.Nightmare);
    }

    /// <summary>Combat setup's entry to the same screen.</summary>
    internal static void OpenGamblingChipScreenForCombatStart(CombatState state) =>
        OpenGamblingChipScreen(state);

    /// <summary>
    /// Raises Gambling Chip's screen if there is anything left to offer, and reports
    /// whether it did. Reopened after every pick, because the count has no upper bound.
    /// </summary>
    private static bool OpenGamblingChipScreen(CombatState state)
    {
        if (state.Hand.Count == 0)
        {
            return false;
        }

        state.PendingSelection = new PendingCardSelection
        {
            Kind = CardSelectionKind.DiscardAnyThenDraw,
            Candidates = [.. Enumerable.Range(0, state.Hand.Count)],
            SourceCardDefId = 0,
            Skippable = true,
        };
        return true;
    }

    /// <summary>
    /// Gambling Chip's screen is the live hand: `FromHandForDiscard` passes no filter, and
    /// `NPlayerHand.Add` re-runs `UpdateSelectModeCardVisibility` for a card that arrives
    /// while the screen is up, so that card is offered too.
    /// </summary>
    internal static void RefreshGamblingChipOffer(CombatState state)
    {
        if (
            state.PendingSelection is
            { Kind: CardSelectionKind.DiscardAnyThenDraw, SourcePotionId: 0 } selection
        )
        {
            selection.Candidates.Clear();
            selection.Candidates.AddRange(Enumerable.Range(0, state.Hand.Count));
        }
    }

    /// <summary>
    /// `CardCmd.DiscardAndDraw(list, list.Count)` — the draw is for as many as were
    /// pitched, and it happens once, when the screen closes.
    /// </summary>
    private static void CloseGamblingChipScreen(CombatState state, Random rng)
    {
        int drawn = state.GamblingChipDiscarded;
        state.GamblingChipDiscarded = 0;
        if (drawn > 0)
        {
            Effects.CardEffects.DrawCards(state, drawn, rng);
        }
    }

    /// <summary>
    /// The three powers that record what they were worth in `BeforeCardPlayed` and spend
    /// THAT in `AfterCardPlayed`, rather than reading the amount afterwards.
    /// </summary>
    /// <remarks>
    /// All three keep a `Dictionary&lt;CardModel, int&gt; amountsForPlayedCards`, and
    /// `AfterimagePower`'s carries the comment that explains all of them: it avoids
    /// "triggering on cards that started play before it was applied" and "gaining extra
    /// block on multiple plays". Reading the amount after the card resolves makes each
    /// power pay out on its own play, which is a whole turn of value it does not have.
    /// </remarks>
    private static void CaptureBeforePlayPowers(CombatState state)
    {
        state.AfterimageBeforePlay = BuffSystem.Get(state.PlayerBuffs, BuffId.Afterimage);
        state.StormBeforePlay = BuffSystem.Get(state.PlayerBuffs, BuffId.Storm);
        state.SubroutineBeforePlay = BuffSystem.Get(state.PlayerBuffs, BuffId.Subroutine);

        // `StranglePower.BeforeCardPlayed` records its amount against the card about to be
        // played, and `AfterCardPlayed` pays out what was recorded. Snapshotting is the
        // whole mechanism: the card that APPLIES a Strangle must not make its target pay
        // for it, and a card that stacks one pays the old amount rather than the new.
        state.StrangleBeforePlay.Clear();
        foreach (var enemy in state.Enemies)
        {
            state.StrangleBeforePlay.Add(BuffSystem.Get(enemy.Buffs, BuffId.Strangle));
        }
    }

    /// <summary>
    /// `GhostSeed.CanAffect`: Basic rarity AND tagged Strike or Defend. The entry-slug
    /// stand-in for the tag is exact within Basic rarity, which is the only rarity this
    /// asks about — the caveat `Card.IsStrikeOrDefend` carries is about cards ABOVE Basic.
    /// </summary>
    /// <summary>
    /// The Ethereal keyword as a POWER sees it. `GhostSeed.AfterCardEnteredCombat` adds
    /// the keyword to basic Strikes and Defends, so those really are Ethereal to anything
    /// that reads `Card.Keywords` -- `CardInstance.IsEthereal()` alone only knows the
    /// printed keyword.
    /// </summary>
    internal static bool IsEtherealForPowers(CombatState state, CardInstance card) =>
        card.IsEthereal()
        || (Effects.RelicEffects.MakesBasicsEthereal(state) && IsBasicStrikeOrDefend(card))
        // Hex grants the KEYWORD globally while it is up, so anything that asks whether a
        // card is Ethereal -- Spirit of Ash, Veilpiercer -- has to see it too.
        || BuffSystem.Get(state.PlayerBuffs, BuffId.Hex) > 0;

    private static bool IsBasicStrikeOrDefend(CardInstance card)
    {
        var def = GeneratedData.Cards.Get(card.DefId);
        return def.Rarity == CardRarity.Basic
            && (
                def.Entry.StartsWith("STRIKE_", StringComparison.Ordinal)
                || def.Entry.StartsWith("DEFEND_", StringComparison.Ordinal)
            );
    }

    private static void RemoveFirstMatchingCard(List<CardInstance> pile, CardInstance card)
    {
        int index = pile.FindIndex(pileCard =>
            pileCard.DefId == card.DefId && pileCard.Upgraded == card.Upgraded
        );
        if (index >= 0)
        {
            pile.RemoveAt(index);
        }
    }

    private static void QueueAttackPlayLifecycleEffects(CombatState state, CardInstance card)
    {
        // Bolas and ThrummingHatchet.BeforeHandDraw: return THIS card if any of its plays
        // finished last turn -- one card, however many plays. A copy retrieved and
        // activated again already has its return queued, as its played flag records.
        if (card.DefId is Effects.CL.Bolas or Effects.CL.ThrummingHatchet && !card.PlayedThisTurn)
        {
            state.ReturnToHandBeforeDraw.Add(card with { FreeThisTurn = false });
        }
    }

    private static void ApplyAfterCardPlayedPowers(
        CombatState state,
        CardDef def,
        Random? rng,
        int energySpent = 0
    )
    {
        Effects.RelicEffects.ApplyAfterCardPlayed(state, def, rng, energySpent);

        // `SmoggyPower.AfterCardPlayed`: once an own Skill play FINISHES, every Skill the
        // player owns takes Smog. Not at play start -- a Skill parked on a selection has
        // not finished, so the hand's Skills (and a Sly Skill it discards) stay playable.
        if (def.Type == CardType.Skill && BuffSystem.Get(state.PlayerBuffs, BuffId.Smoggy) > 0)
        {
            state.SkillPlayedWhileSmoggy = true;
        }

        // `TenderPower.AfterCardPlayed`: every card the player plays costs 1 Strength and
        // 1 Dexterity, applied `silent: true` -- so it does not go through
        // `GainPlayerStrength`, whose Girya doubling is an on-GAIN hook this must not
        // trip. `AfterSideTurnEnd` gives back exactly what was taken.
        if (BuffSystem.Get(state.PlayerBuffs, BuffId.Tender) > 0)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Strength, -1);
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Dexterity, -1);
            state.TenderCardsPlayedThisTurn++;
        }

        if (def.Type == CardType.Power)
        {
            for (int i = 0; i < state.StormBeforePlay; i++)
            {
                Effects.CardEffects.ChannelOrb(state, OrbType.Lightning);
            }

            if (state.SubroutineBeforePlay > 0)
            {
                Effects.CardEffects.GainEnergy(state, state.SubroutineBeforePlay);
            }
        }

        int panache = BuffSystem.Get(state.PlayerBuffs, BuffId.PanachePower);
        if (panache > 0)
        {
            // `PanachePower`'s internal `alreadyApplied` flag, whose comment in the game
            // says why it exists: "We track this so we don't count the Panache card
            // towards itself." `AfterCardPlayed` fires for the play that APPLIED the
            // power, and the counter only starts on the play after it -- so the damage
            // lands on the fifth card played AFTER Panache, not on the fourth.
            if (BuffSystem.Get(state.PlayerBuffs, BuffId.PanacheArmed) <= 0)
            {
                // Arm it and count nothing: this IS the play that applied the power.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.PanacheArmed, 1);
            }
            else
            {
                state.CardsPlayedSincePanacheProc++;
                if (state.CardsPlayedSincePanacheProc >= 5)
                {
                    state.CardsPlayedSincePanacheProc = 0;
                    Effects.CardEffects.DealUnpoweredDamageToAll(state, panache);
                }
            }
        }

        // `AfterimagePower` records its amount in `BeforeCardPlayed` and spends THAT in
        // `AfterCardPlayed` -- the comment on its internal Data says so outright: "avoid
        // triggering on cards that started play before it was applied, and avoid gaining
        // extra block on multiple plays of After Image". So the first Afterimage pays out
        // nothing for its own play, and the second pays 1 rather than 2. Reading the
        // amount here, after the card resolved, gave both of them a turn's head start --
        // the same defect Burst had.
        //
        // The block is `ValueProp.Unpowered`, so Dexterity does not touch it.
        if (state.AfterimageBeforePlay > 0)
        {
            Effects.CardEffects.GainUnpoweredBlock(state, state.AfterimageBeforePlay, rng);
        }
    }

    /// <summary>
    /// `Hook.AfterCardPlayed` for the listeners that sit on ENEMY creatures.
    /// `CombatState.IterateHookListeners` (CombatState.cs:411-435) visits the allies'
    /// powers and relics before any enemy, so every one of these runs after the player's
    /// relics -- Game Piece's draw lands before Withering Presence's Wither (BBB22XX
    /// step-0982). Their order among themselves is unchanged.
    /// </summary>
    private static void ApplyAfterCardPlayedEnemyPowers(CombatState state, CardDef def)
    {
        // CurlUpPower.AfterCardPlayed grants its unpowered block after the whole
        // matching card has resolved. Repeated hits never receive block between them.
        foreach (var enemy in state.Enemies)
        {
            if (
                enemy.Hp > 0
                && enemy.CurlUpCardSourceId != 0
                && enemy.CurlUpCardSourceId == state.ActiveBlockPlay.SourceId
            )
            {
                enemy.CurlUpCardSourceId = 0;
                enemy.Block += BuffSystem.Get(enemy.Buffs, BuffId.CurlUp);
                BuffSystem.Remove(enemy.Buffs, BuffId.CurlUp);
            }
        }

        // `WitheringPresencePower.AfterCardPlayed`: every card the player plays takes one
        // off `CardsLeft`, and at zero the player is handed a Wither and the count resets
        // to 6. It counts EVERY play -- VoidForm's power is the one that filters auto-plays
        // out, and it has to say so explicitly, so the default is that they count.
        // `AddToCombatAndPreview<Wither>(Hand)` goes through CardPileCmd.Add, which sends
        // a full-hand add to the discard pile (CardPileCmd.cs:484-488) rather than
        // dropping it.
        foreach (var enemy in state.Enemies)
        {
            if (enemy.Hp <= 0 || BuffSystem.Get(enemy.Buffs, BuffId.WitheringPresence) <= 0)
            {
                continue;
            }

            BuffSystem.Apply(enemy.Buffs, BuffId.WitheringPresence, -1);
            if (BuffSystem.Get(enemy.Buffs, BuffId.WitheringPresence) <= 0)
            {
                Effects.CardEffects.AddGeneratedCardToHandOrDiscard(
                    state,
                    new CardInstance(Effects.ST.Wither, false)
                );
                BuffSystem.Apply(
                    enemy.Buffs,
                    BuffId.WitheringPresence,
                    CombatFactory.WitheringPresenceCards
                );
            }
        }

        // The Strangle payout, from the snapshot taken before this card resolved.
        // `CreatureCmd.Damage(..., Unblockable | Unpowered)` at the enemy carrying it.
        for (int i = 0; i < state.Enemies.Count && i < state.StrangleBeforePlay.Count; i++)
        {
            int owed = state.StrangleBeforePlay[i];
            if (owed > 0 && state.Enemies[i].Hp > 0)
            {
                Effects.CardEffects.DealUnblockableUnpoweredDamageToEnemy(
                    state,
                    state.Enemies[i],
                    owed
                );
            }
        }

        if (def.Type == CardType.Skill)
        {
            foreach (var enemy in state.Enemies.Where(e => e.Hp > 0))
            {
                int enrage = BuffSystem.Get(enemy.Buffs, BuffId.Enrage);
                if (enrage > 0)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enrage);
                }
            }
        }

        if (def.Type == CardType.Power)
        {
            int galvanic = state
                .Enemies.Where(e => e.Hp > 0)
                .Select(e => BuffSystem.Get(e.Buffs, BuffId.Galvanic))
                .DefaultIfEmpty(0)
                .Max();
            if (galvanic > 0)
            {
                Effects.CardEffects.DealDamageToPlayer(state, galvanic);
            }
        }

        if (def.Type == CardType.Skill)
        {
            // VitalSparkPower.AfterCardPlayed: a Skill carrying its Tainted affliction
            // stamps TaintedPower on the player. Read as the largest Vital Spark on the
            // board, the same way Galvanic is read -- the affliction lands on the
            // CARD in the game, and modelling the card stamp rather than the board would
            // mean tracking an affliction per instance for one monster.
            int vitalSpark = state
                .Enemies.Where(e => e.Hp > 0)
                .Select(e => BuffSystem.Get(e.Buffs, BuffId.VitalSpark))
                .DefaultIfEmpty(0)
                .Max();
            if (vitalSpark > 0)
            {
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Tainted, vitalSpark);
            }
        }

        foreach (var enemy in state.Enemies.Where(e => e.Hp > 0))
        {
            if (BuffSystem.Get(enemy.Buffs, BuffId.Slow) > 0)
            {
                BuffSystem.Apply(enemy.Buffs, BuffId.SlowCount, 1);
            }
        }
    }

    /// <summary>
    /// `CorruptionPower.ModifyCardPlayResultLocation` ignores `isAutoPlay`, and
    /// OnPlayWrapper asks it before the play runs, so an auto-played Skill exhausts too.
    /// MoveToResultPileWithoutPlaying never asks it, so an auto-play that is refused is
    /// not affected. ABB68XX step0465.
    /// </summary>
    private static bool IsCorruptedSkill(CombatState state, CardDef def) =>
        def.Type == CardType.Skill && BuffSystem.Get(state.PlayerBuffs, BuffId.Corruption) > 0;

    private static bool ShouldExhaustAfterPlay(CardInstance card)
    {
        // Use the extracted keyword and its upgrade delta. A second list of card
        // exceptions had overridden Purity's correct metadata and discarded Purity+.
        // Goopy adds Exhaust to its particular Defend-tagged card on enchantment.
        return card.Enchantment == Enchantment.Goopy || card.IsExhaust();
    }

    /// <summary>
    /// `MasterPlannerPower.AfterCardPlayed` applies the Sly KEYWORD to every Skill its
    /// owner plays — permanently for the combat, so that copy plays itself the next time
    /// anything discards it.
    /// </summary>
    /// <remarks>
    /// Asked at each of the three places a played card can land in the discard pile. The
    /// game hooks "a card was played" once; the emulator has three disposal paths, and a
    /// rule applied to one of them is the shape this codebase keeps turning up.
    /// </remarks>
    private static bool MasterPlannerMarks(CombatState state, CardDef def) =>
        def.Type == CardType.Skill && BuffSystem.Get(state.PlayerBuffs, BuffId.MasterPlanner) > 0;

    /// <summary>
    /// `CardModel.GetResultPileTypeForCardPlay` overridden to return Hand where the base
    /// would return Discard — the card comes back instead of being spent.
    /// </summary>
    private static bool ReturnsToHandAfterPlay(CardDef def) => def.Name == "ParticleWall";

    /// <summary>
    /// CardModel fixes its result location before GeneratePlayCount and BeforeCardPlayed.
    /// Nostalgia counts STARTED plays, including any parent still awaiting a nested play.
    /// Callers apply this snapshot only when the card otherwise goes to discard.
    /// </summary>
    internal static bool CaptureNostalgiaDestination(CombatState state, CardDef def) =>
        def.Type is CardType.Attack or CardType.Skill
        && BuffSystem.Get(state.PlayerBuffs, BuffId.Nostalgia)
            > state.AttackOrSkillPlaysStartedThisTurn;

    private static bool ShouldPlaceOnDrawPileAfterPlay(
        CombatState state,
        CardDef def,
        // No default: every play site has to say what Rebound was worth BEFORE the card
        // resolved, because a card that applies Rebound must not redirect itself.
        int reboundBefore,
        bool nostalgiaReturnsToDraw
    )
    {
        // 429 is ShiningStrike, which returns itself to the TOP of the draw pile. Its own
        // `!Keywords.Contains(Exhaust)` guard needs no code: this branch sits after the
        // exhaust one, so a Shining Strike that has been given Exhaust never reaches it.
        // `ReboundPower.ModifyCardPlayResultPileTypeAndPosition` sends a card bound for the
        // DISCARD to the top of the DRAW pile instead, and is spent doing it -- so it moves
        // the NEXT card played after Rebound itself, once.
        if (reboundBefore > 0 && BuffSystem.Get(state.PlayerBuffs, BuffId.Rebound) > 0)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Rebound, -1);
            return true;
        }

        return def.Id == 429 || nostalgiaReturnsToDraw;
    }

    /// <summary>
    /// Plays a queued card. The engine is mid-drain here and cannot hand a selection
    /// screen back to the caller, so anything that would raise one resolves itself —
    /// see CardEffects.OpenCardSelection.
    /// </summary>
    /// <summary>
    /// Play whatever is already waiting in the auto-play queue.
    /// </summary>
    /// <remarks>
    /// Separate from the loop that runs after an action because the two fire at different
    /// moments: that one handles what a card's own effect queued, this one handles what
    /// was queued before the player moved at all. Imbued is the only thing that does the
    /// latter, and it plays from the BOTTOM of the draw pile, where the turn-1 reorder
    /// put it.
    /// </remarks>
    internal static void ResolveAwaitedSetupAutoPlays(
        CombatState state,
        int preservedPrefix,
        Random rng
    )
    {
        while (
            state.AutoPlayQueue.Count > preservedPrefix
            && state.PendingSelection is null
            && state.PendingCardPlay is null
        )
        {
            if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
                return;
            var child = state.AutoPlayQueue[preservedPrefix];
            state.AutoPlayQueue.RemoveAt(preservedPrefix);
            AutoPlay(state, child, rng);
        }
    }

    /// <summary>
    /// Plays, in discard order, the Sly cards a selection answer queued at or after
    /// <paramref name="prefix" /> (`CardEffects.DiscardMovedCards` queues them), leaving
    /// every other queued auto-play where it was for the ordinary drain.
    /// </summary>
    /// <remarks>
    /// `DiscardAndDraw` awaits `AutoPlay(item, null, AutoPlayType.SlyDiscard)` before
    /// returning to the card whose effect discarded, so Survivor's Flick-Flack reaches the
    /// discard pile before Survivor does (BBB21XX step-0217). The children run as
    /// auto-plays (`AutoPlaying` set by <see cref="AutoPlay" />), so a selection they raise
    /// resolves itself rather than stranding the parked parent. Targets are the auto-play
    /// ones, not the parent's.
    /// </remarks>
    private static void PlaySlyDiscardsQueuedSince(CombatState state, int prefix, Random rng)
    {
        int callerTarget = state.TargetEnemyIndex;
        var callerPlayTarget = state.PlayTarget;
        int callerAutoTarget = state.AutoPlayTargetIndex;
        try
        {
            for (
                int i = Math.Min(prefix, state.AutoPlayQueue.Count);
                i < state.AutoPlayQueue.Count;

            )
            {
                var child = state.AutoPlayQueue[i];
                if (!child.IsSlyThisTurn())
                {
                    i++;
                    continue;
                }

                if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
                    return;
                state.AutoPlayQueue.RemoveAt(i);
                state.TargetEnemyIndex = -1;
                state.AutoPlayTargetIndex = -1;
                AutoPlay(state, child, rng);
            }
        }
        finally
        {
            state.TargetEnemyIndex = callerTarget;
            state.PlayTarget = callerPlayTarget;
            state.AutoPlayTargetIndex = callerAutoTarget;
        }
    }

    private static void DrainAutoPlayQueue(CombatState state, Random rng)
    {
        // A chosen play parked on a selection still owns the queue; see Step.
        if (
            state.PendingCardPlay is not null
            || state.PendingTurnSetup is not null
            || state.PendingPotionAutoPlays is not null
        )
        {
            return;
        }

        state.AutoPlayTargetIndex = -1;
        while (state.AutoPlayQueue.Count > 0)
        {
            var next = state.AutoPlayQueue[0];
            state.AutoPlayQueue.RemoveAt(0);
            AutoPlay(state, next, rng);
            if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
            {
                state.AutoPlayQueue.Clear();
                return;
            }
        }
    }

    /// <summary>
    /// `PlayerTurnPhase.AutoPrePlay`: everything that plays itself before the player is
    /// allowed to act. Must run before the combat is OBSERVED, not merely before the first
    /// action is applied.
    /// </summary>
    /// <remarks>
    /// It used to run at the top of <see cref="Step" /> and nowhere else, which put it on
    /// the wrong side of the observation. `ValidActions` is queried on the state as it
    /// stands BEFORE `Step`, so the action the caller chose was chosen against a hand these
    /// auto-plays were about to change -- and since the end-turn action is `Hand.Count`,
    /// every index could shift underneath it. With Imbued that was one card; Whispering
    /// Earring made it up to thirteen, which is what turned a latent wart into a real
    /// defect. See E487.
    ///
    /// Idempotent, and deliberately so: the drain is a no-op on an empty queue and the
    /// earring is latched, so an entry point that calls it and a `Step` that calls it again
    /// cost nothing. That is what lets it be added at the entry points WITHOUT having to
    /// prove every construction path was found.
    /// </remarks>
    internal static void ResolvePrePlayPhase(CombatState state, Random rng)
    {
        // A Mayhem batch parked on a choice is still inside AfterAutoPrePlayPhaseEntered;
        // what follows it waits for the answer (FinishTurnStartAutoPlays).
        if (state.PendingTurnSetup is not null || state.PendingPotionAutoPlays is not null)
            return;
        DrainAutoPlayQueue(state, rng);

        // `WhisperingEarring.AfterAutoPrePlayPhaseEnteredLate` -- LATE, and the game's own
        // remark says why: "we want this to trigger after all other triggers fire (i.e.
        // Imbued)". The drain above IS Imbued, so the earring goes after it and the hand it
        // walks is the one Imbued left behind.
        RunWhisperingEarringAutoPlay(state, rng);
    }

    /// <summary>The game's `WhisperingEarring.maxCardsToPlay`.</summary>
    private const int WhisperingEarringMaxCards = 13;

    /// <summary>
    /// `WhisperingEarring.AfterAutoPrePlayPhaseEnteredLate`: on turn ONE, the relic plays
    /// up to thirteen cards for the player before they act.
    /// </summary>
    /// <remarks>
    /// This was declared unmodelled for a reason that turned out to be wrong -- "an AI
    /// standing in for the player", so approximating it would be worse than admitting the
    /// gap. There is no AI. `pile.Cards.FirstOrDefault(c =&gt; c.CanPlay())` is the LEFTMOST
    /// playable card, `GetTarget` gives the LEFTMOST hittable enemy, and the
    /// `VakuuCardSelector` pushed for the duration answers every selection screen with
    /// `options.Take(maxSelect)` -- the first ones. Three first-in-order rules, all
    /// deterministic, all transcribable. See E486.
    ///
    /// The plays go through <see cref="PlayCard" /> because that is precisely what the
    /// game does here: `SpendResources()` and then `CardCmd.AutoPlay(skipXCapture: true)`.
    /// AutoPlay alone plays a card FOR FREE -- that is its documented purpose -- and the
    /// explicit spend in front of it is what makes this an ordinary paid play rather than
    /// an Imbued-style freebie. Routing it through the auto-play path instead would have
    /// handed the player thirteen free cards.
    /// </remarks>
    internal static void RunWhisperingEarringAutoPlay(CombatState state, Random rng)
    {
        // `TurnNumber > 1` returns, and the game's TurnNumber is this counter PLUS ONE --
        // so the only turn this fires on is the one where `Turn` is still zero.
        if (
            state.WhisperingEarringSpent
            || state.Turn != 0
            || !Effects.RelicEffects.Has(state.Relics, Effects.RelicEffects.WhisperingEarring)
        )
        {
            return;
        }

        state.WhisperingEarringSpent = true;
        int startTurn = state.Turn;

        for (int played = 0; played < WhisperingEarringMaxCards; played++)
        {
            // `CombatManager.Instance.IsOverOrEnding`.
            if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
            {
                return;
            }

            // `PlayerCombatState.TurnNumber != startTurn`: a card that ended the turn under
            // it -- Void Form's, which `PlayCard` resolves by calling EndTurn -- stops the
            // loop rather than spilling thirteen plays across two turns.
            if (state.Turn != startTurn)
            {
                return;
            }

            int index = state.Hand.FindIndex(card =>
                CanPlayFromHand(state, card) && !NeedsAnAllyToPlay(card)
            );
            if (index < 0)
            {
                return;
            }

            var def = state.Hand[index].ModelDef();
            // `GetTarget`: AnyEnemy takes `HittableEnemies.FirstOrDefault()`, the LEFTMOST,
            // and it is GIVEN to AutoPlay rather than left null -- so the CombatTargets
            // stream is NOT drawn from here. Rolling one would desync every later target.
            bool anyEnemy = Effects.CardEffects.EffectiveTarget(state, def) == CardTarget.AnyEnemy;
            int target = anyEnemy ? FirstHittableEnemy(state) : -1;
            if (anyEnemy && target < 0)
            {
                // AutoPlay files an untargetable AnyEnemy card away without playing it.
                // Nothing living is targetable, so no later card would fare better either.
                return;
            }

            state.TargetEnemyIndex = target;
            // `SpendResources` then `AutoPlay(skipXCapture: true)`: paid, and the capture
            // is the payment. Only the resource origin changes here; the route keeps its
            // chosen-play bookkeeping (a known, separate difference from IsAutoPlay).
            PlayCard(state, index, rng, CardResourceOrigin.PaidAuto);

            // What the play itself queued -- a Hellraiser, a Knife Trap's Shivs. `Step`
            // drains this after the player's action; the earring's plays are actions too.
            // A no-op while the play is parked on a selection.
            DrainAutoPlayQueue(state, rng);

            // `CardSelectCmd.PushSelector(new VakuuCardSelector())` is held for the whole
            // loop, and it answers with `options.Take(maxSelect)`. The emulator asks one
            // pick at a time and reopens, so taking candidate ZERO until the screen closes
            // is the same "first ones, in order" answer.
            AnswerSelectionsAsVakuu(state, rng);

            // The answer finished a parked play, so what it queued is owed now -- before
            // the earring picks its next card, as the drain above is for an unparked one.
            DrainAutoPlayQueue(state, rng);
        }
    }

    /// <summary>
    /// `CanPlay` refuses an ally-targeted card on NoLivingAllies, and a solo run has no
    /// living allies ever.
    /// </summary>
    /// <remarks>
    /// Only the earring asks. The player cannot hold one of these -- they are all
    /// `MultiplayerOnly` and filtered out of every pool -- but the earring picks FOR the
    /// player out of whatever is in hand, so a test hand or a future generator that put one
    /// there would otherwise have it auto-played, which the game would never do. See the
    /// remarks on <see cref="CanPlayFromHand" /> for why it is not in the shared predicate.
    /// </remarks>
    private static bool NeedsAnAllyToPlay(CardInstance card) =>
        GeneratedData.Cards.Get(card.DefId).Target == CardTarget.AnyAlly;

    /// <summary>`ICombatState.HittableEnemies.FirstOrDefault()` as an index, or -1.</summary>
    private static int FirstHittableEnemy(CombatState state) =>
        state.Enemies.FindIndex(EnemyAI.IsTargetable);

    /// <summary>
    /// Drains any card-selection screen the way the game's `VakuuCardSelector` does:
    /// always the first candidate, never the skip.
    /// </summary>
    private static void AnswerSelectionsAsVakuu(CombatState state, Random rng)
    {
        // A screen that reopens per pick (Purity, Survivor) needs one pass per pick, and
        // the hand it draws from shrinks each time, so this terminates on its own. The
        // bound is a backstop against a screen that refuses to close, not the mechanism.
        int guard = WhisperingEarringMaxCards * 16;
        while (guard-- > 0 && state.PendingSelection is { } selection)
        {
            if (selection.Candidates.Count == 0)
            {
                return;
            }

            ResolveCardSelection(state, 0, rng);
        }
    }

    private static void AutoPlay(
        CombatState state,
        CardInstance card,
        Random rng,
        bool bindWhenPlaced = false
    )
    {
        bool wasAutoPlaying = state.AutoPlaying;
        bool mayPause = state.AutoPlayMayPause;
        var callerBlockPlay = state.ActiveBlockPlay;
        state.AutoPlaying = true;
        state.AutoPlayMayPause = false;
        try
        {
            AutoPlayCore(state, card, rng, bindWhenPlaced);
        }
        finally
        {
            state.AutoPlaying = wasAutoPlaying;
            state.AutoPlayMayPause = mayPause;
            state.ActiveBlockPlay = callerBlockPlay;
        }
    }

    /// <summary>
    /// Distilled Chaos: `CardPileCmd.AutoPlayFromDrawPile(count, Top, forceExhaust: false)`
    /// with the children able to ask the player. Stages the whole batch (shuffling when the
    /// draw pile runs dry), then plays it; see <see cref="PendingPotionAutoPlays" />.
    /// </summary>
    internal static void BeginPotionAutoPlayBatch(
        CombatState state,
        int count,
        Random rng,
        bool resumesTurnStart = false
    )
    {
        if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
            return;

        var batch = new PendingPotionAutoPlays { ResumesTurnStart = resumesTurnStart };
        for (int i = 0; i < count; i++)
        {
            if (state.DrawPile.Count == 0 && state.DiscardPile.Count > 0)
                Effects.CardEffects.ShuffleDiscardIntoDraw(state, rng);
            if (state.DrawPile.Count == 0)
                break;
            batch.Staged.Add(state.DrawPile[0]);
            state.RemoveFromDrawPileAt(0);
        }

        state.PendingPotionAutoPlays = batch;
        ContinuePotionAutoPlays(state, rng);
    }

    /// <summary>Plays the staged cards in order; true when one parked on a selection.</summary>
    private static bool ContinuePotionAutoPlays(CombatState state, Random rng)
    {
        var batch = state.PendingPotionAutoPlays!;
        while (batch.Staged.Count > 0)
        {
            // `if (!item.Owner.Creature.IsDead) AutoPlay(...) else break;`, and AutoPlay
            // returns at once when the combat is over or ending -- the rest stay in Play,
            // which the auto-play queue of a finished combat stands in for.
            if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
            {
                state.AutoPlayQueue.AddRange(batch.Staged);
                batch.Staged.Clear();
                break;
            }

            var card = batch.Staged[0];
            batch.Staged.RemoveAt(0);
            if (StartPotionBatchChild(state, batch, card, rng))
                return true;
        }

        state.PendingPotionAutoPlays = null;
        return false;
    }

    /// <summary>
    /// `CardCmd.AutoPlay` for one staged card: AutoPlayCore's refusal, target roll and
    /// placement around RunAutomaticCardPlays' loop, which here may park.
    /// </summary>
    private static bool StartPotionBatchChild(
        CombatState state,
        PendingPotionAutoPlays batch,
        CardInstance card,
        Random rng
    )
    {
        var def = card.ModelDef();
        if (def.Unplayable || IsBlockedByPlayHook(state, card, def))
        {
            // `MoveToResultPileWithoutPlaying` removes a dupe from combat.
            if (card.Dupe) { }
            else if (ShouldExhaustAfterPlay(card))
                Effects.CardEffects.ExhaustCard(state, card, rng: rng);
            else
                state.DiscardPile.Add(card with { FreeThisTurn = false });
            return false;
        }

        var child = new ParkedAutoPlay
        {
            Card = card,
            CallerTargetIndex = state.TargetEnemyIndex,
            CallerAutoPlayTargetIndex = state.AutoPlayTargetIndex,
        };
        state.AutoPlayTargetIndex = -1;
        child.TargetIndex =
            Effects.CardEffects.EffectiveTarget(state, def) == CardTarget.AnyEnemy
                ? RandomAutoPlayTargetIndex(state, rng)
                : -1;
        state.TargetEnemyIndex = child.TargetIndex;
        child.ReboundBefore = BuffSystem.Get(state.PlayerBuffs, BuffId.Rebound);
        child.NostalgiaReturnsToDraw = CaptureNostalgiaDestination(state, def);
        child.CorruptedSkill = IsCorruptedSkill(state, def);
        child.CallerChannels = PlayedCardChannels.Take(state);
        PinPlayTarget(state, def);
        child.PlayTarget = state.PlayTarget;
        child.Resources = AutoPlayResources(state, def, card);
        child.ExtraPlays = SettlePlayCount(state, card, def);
        child.SourceId = CardBlockHistory.NewSource(state);

        batch.Parked = child;
        if (RunPotionBatchChild(state, child, def, resuming: false, rng))
            return true;
        batch.Parked = null;
        SettleHandAutoPlayDeaths(state, batch, rng);
        PlacePotionBatchChild(state, child, def, rng);
        CheckForEmptyHandAfterStampedeSwing(state, batch, rng);
        return false;
    }

    /// <summary>
    /// `CardModel.OnPlayWrapper` ends every play, automatic ones too, with
    /// `CombatManager.CheckForEmptyHand`, which is skipped only while a card or potion
    /// effect is still executing. A potion's batch is inside `OnUse` and checks once in
    /// <see cref="FinishPotionUse" />; a Stampede swing comes from a POWER hook in
    /// AutoPostPlay, a phase `UnceasingTop.IsValidPhase` admits, so it checks for itself
    /// after the swung card is placed and before the next swing picks.
    /// </summary>
    private static void CheckForEmptyHandAfterStampedeSwing(
        CombatState state,
        PendingPotionAutoPlays batch,
        Random rng
    )
    {
        if (!batch.ResumesEndTurn || PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
            return;
        Effects.RelicEffects.ApplyAfterHandEmptied(state, rng);
    }

    /// <summary>
    /// RunAutomaticCardPlays' repeat loop for a batch child. Parks -- saving the in-flight
    /// channels, target and block play -- when the effect leaves a selection open.
    /// </summary>
    private static bool RunPotionBatchChild(
        CombatState state,
        ParkedAutoPlay child,
        CardDef def,
        bool resuming,
        Random rng
    )
    {
        bool wasAutoPlaying = state.AutoPlaying;
        bool mayPause = state.AutoPlayMayPause;
        var callerBlockPlay = state.ActiveBlockPlay;
        state.AutoPlaying = true;
        state.AutoPlayMayPause = true;
        try
        {
            for (; child.Play <= child.ExtraPlays; child.Play++)
            {
                if (resuming)
                {
                    resuming = false;
                    // The rest of the parked OnPlay, inside the same CardPlay.
                    RunOwedEffectTail(state, TakePendingEffectTail(state), rng);
                }
                else
                {
                    if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
                        break;
                    BeginCardPlayIteration(
                        state,
                        InFlightCopy(state, child.Card),
                        def,
                        child.SourceId,
                        energyToSpend: child.Resources.ResourceEnergyValue,
                        chosen: false,
                        rng
                    );
                    child.DeathBaseline = CaptureLivingEnemiesForDeath(state);
                    state.ActiveCardResources = child.Resources;
                    Effects.CardEffects.Apply(
                        def,
                        child.Card.Upgraded,
                        state,
                        rng,
                        InFlightCopy(state, child.Card)
                    );
                }

                if (
                    state.PendingSelection is not null
                    && !PlayerIsDead(state)
                    && !NoPrimaryEnemyLeft(state)
                )
                {
                    // The awaited kill is resolved before the child's selection, as it is
                    // for a chosen play; the resumed child's pass finds it handled.
                    ResolveAwaitedEnemyDeaths(state, child.DeathBaseline, rng);
                    child.BlockPlay = state.ActiveBlockPlay;
                    child.Channels = PlayedCardChannels.Take(state);
                    child.CallerChannels.Restore(state);
                    state.PlayTarget = null;
                    state.TargetEnemyIndex = child.CallerTargetIndex;
                    state.AutoPlayTargetIndex = child.CallerAutoPlayTargetIndex;
                    return true;
                }

                ResolveAwaitedEnemyDeaths(state, child.DeathBaseline, rng);
                AfterCardPlayEffect(state, child.Card, def, child.Play, rng);
                if (PlayerIsDead(state) || NoPrimaryEnemyLeft(state))
                {
                    // A terminal play discards its unused per-play snapshot.
                    TakeSerpentFormSnapshot(state);
                    if (state.PlayerHp > 0)
                    {
                        Effects.RelicEffects.ApplyIronClubAfterCardPlayed(state, rng);
                        Effects.RelicEffects.ApplyNunchakuAfterCardPlayed(state, def);
                        Effects.RelicEffects.ApplyTuningForkAfterCardPlayed(state, def, rng);
                    }
                    break;
                }
                CompleteCardPlayIteration(
                    state,
                    def,
                    energySpent: child.Resources.ResourceEnergyValue,
                    rng
                );
            }

            return false;
        }
        finally
        {
            state.AutoPlaying = wasAutoPlaying;
            state.AutoPlayMayPause = mayPause;
            state.ActiveBlockPlay = callerBlockPlay;
        }
    }

    /// <summary>
    /// A hand auto-play pays the deaths its plays caused against the HP it started from,
    /// before the card is placed -- the order `AutoPlayCardFromHand` had.
    /// </summary>
    private static void SettleHandAutoPlayDeaths(
        CombatState state,
        PendingPotionAutoPlays batch,
        Random rng
    )
    {
        if (batch.HandEnemyHpsBefore is { } hpsBefore)
            HandleEnemyDeaths(state, hpsBefore, rng);
    }

    /// <summary>AutoPlayCore's placement, once the child's plays are done.</summary>
    private static void PlacePotionBatchChild(
        CombatState state,
        ParkedAutoPlay child,
        CardDef def,
        Random rng
    )
    {
        var played = InFlightCopy(state, child.Card) with
        {
            FreeUntilPlayed = false,
            CostOverrideThisTurn = int.MinValue,
            // A finished automatic play, as in RunAutomaticCardPlays.
            PlayedThisTurn = true,
        };
        child.CallerChannels.Restore(state);
        state.PlayTarget = null;
        state.TargetEnemyIndex = child.CallerTargetIndex;
        state.AutoPlayTargetIndex = child.CallerAutoPlayTargetIndex;

        if (def.Type == CardType.Power || child.Card.Dupe)
        {
            // Played powers leave hand without firing exhaust hooks. A dupe's result pile
            // is `PileType.None`, as in AutoPlayCore: it is removed from combat.
        }
        else if (ShouldExhaustAfterPlay(child.Card) || child.CorruptedSkill)
        {
            Effects.CardEffects.ExhaustCard(state, played, rng: rng);
        }
        else if (
            ShouldPlaceOnDrawPileAfterPlay(
                state,
                def,
                child.ReboundBefore,
                child.NostalgiaReturnsToDraw
            )
        )
        {
            state.TopDeck(played with { FreeThisTurn = false });
        }
        else
        {
            state.DiscardPile.Add(
                played with
                {
                    FreeThisTurn = false,
                    SlyForCombat = played.SlyForCombat || MasterPlannerMarks(state, def),
                }
            );
        }

        Effects.RelicEffects.ApplyAfterPlayerHpChanged(state);
    }

    /// <summary>
    /// The parked child's selection has closed: finish its OnPlay and plays, place it,
    /// play the rest of the batch, and only then complete the potion's use.
    /// </summary>
    private static StepResult ResumePotionAutoPlays(
        CombatState state,
        Random rng,
        int playerHpBefore,
        int[] enemyHpsBefore
    )
    {
        var batch = state.PendingPotionAutoPlays!;
        bool resumesTurnStart = batch.ResumesTurnStart;
        var child = batch.Parked!;
        var def = child.Card.ModelDef();
        child.CallerTargetIndex = state.TargetEnemyIndex;
        child.CallerAutoPlayTargetIndex = state.AutoPlayTargetIndex;
        child.CallerChannels = PlayedCardChannels.Take(state);
        child.Channels.Restore(state);
        state.TargetEnemyIndex = child.TargetIndex;
        state.AutoPlayTargetIndex = -1;
        state.PlayTarget = child.PlayTarget;
        var callerBlockPlay = state.ActiveBlockPlay;
        state.ActiveBlockPlay = child.BlockPlay;
        bool parked = RunPotionBatchChild(state, child, def, resuming: true, rng);
        state.ActiveBlockPlay = callerBlockPlay;
        if (parked)
            return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);

        batch.Parked = null;
        SettleHandAutoPlayDeaths(state, batch, rng);
        PlacePotionBatchChild(state, child, def, rng);
        CheckForEmptyHandAfterStampedeSwing(state, batch, rng);
        if (batch.ResumesEndTurn)
        {
            state.PendingPotionAutoPlays = null;
            if (AutoPlayStampedeAttacks(state, rng, batch.StampedeRemaining))
                return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
            return EndTurnAfterStampede(state, rng, playerHpBefore, enemyHpsBefore, 0);
        }
        if (ContinuePotionAutoPlays(state, rng))
            return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
        if (resumesTurnStart && !batch.HistoryCourseEncore)
        {
            // Mayhem's batch was the power listener; History Course's relic listener is next.
            AutoPlayHistoryCourseEncore(state, rng);
            if (state.PendingPotionAutoPlays is not null)
                return new StepResult(Terminal: false, PlayerWon: false, Reward: 0f);
        }
        if (resumesTurnStart)
            return FinishTurnStartAutoPlays(state, rng, playerHpBefore, enemyHpsBefore);
        return FinishPotionUse(state, rng, playerHpBefore, enemyHpsBefore);
    }

    /// <summary>
    /// Mayhem's batch has finished after a pause: the pre-play phase it held back
    /// (the auto-play queue, then Whispering Earring's LATE hook) runs now, then the tail
    /// `EndTurn` would have run after <see cref="ResolveTurnStartAutoPlays" />.
    /// </summary>
    private static StepResult FinishTurnStartAutoPlays(
        CombatState state,
        Random rng,
        int playerHpBefore,
        ReadOnlySpan<int> enemyHpsBefore
    )
    {
        ResolvePrePlayPhase(state, rng);
        Effects.RelicEffects.ApplyAfterPlayerHpChanged(state);
        bool playerDead = PlayerIsDead(state);
        bool allDead = NoPrimaryEnemyLeft(state);
        return new StepResult(
            Terminal: playerDead || allDead,
            PlayerWon: allDead && !playerDead,
            Reward: ComputeReward(state, playerDead, allDead, playerHpBefore, enemyHpsBefore)
        );
    }

    private static void AutoPlayCore(
        CombatState state,
        CardInstance card,
        Random rng,
        bool bindWhenPlaced = false
    )
    {
        var def = card.ModelDef();

        // `ChainsOfBindingPower.AfterCardDrawn` after Hellraiser's play, on the copy as it
        // is placed. `Afflict` refuses while the combat is ending. An exhausted copy is
        // bound by ExhaustCard instead, once its exhaust hooks (Dark Embrace's draw) are
        // done.
        CardInstance Placed(CardInstance placed) =>
            bindWhenPlaced && !PlayerIsDead(state) && !NoPrimaryEnemyLeft(state)
                ? Effects.CardEffects.BindOnDraw(state, placed)
                : placed;

        // `CardCmd.AutoPlay` refuses an Unplayable card FIRST (CardCmd.cs:58-62), before
        // ShouldPlay, targeting or any capture; then Hook.ShouldPlay gates auto-plays as
        // well as chosen ones. Both answer with MoveToResultPileWithoutPlaying — the card
        // is spent, its effect never happens, no CardPlay starts and nothing counts it.
        if (def.Unplayable || IsBlockedByPlayHook(state, card, def))
        {
            // `MoveToResultPileWithoutPlaying` removes a dupe from combat.
            if (card.Dupe) { }
            else if (ShouldExhaustAfterPlay(card))
            {
                Effects.CardEffects.ExhaustCard(
                    state,
                    card,
                    rng: rng,
                    bindAfterHooks: bindWhenPlaced
                );
            }
            else
            {
                state.DiscardPile.Add(Placed(card) with { FreeThisTurn = false });
            }

            return;
        }

        // Auto-play picks its target the way CardCmd does when a played card has no
        // explicit one: Rng.CombatTargets.NextItem(HittableEnemies) -- unless it was GIVEN
        // one, which is what Knife Trap does to each Shiv it replays. A given target must
        // not roll.
        //
        // The roll used to happen and its result was then thrown away: `targetIndex` was
        // assigned and never read, so every auto-played attack drew from the combat-targets
        // stream and hit the first living enemy regardless. Invisible against one creature
        // and wrong against several, in both the target and the stream position.
        int targetIndex = state.AutoPlayTargetIndex;
        if (
            targetIndex < 0
            && Effects.CardEffects.EffectiveTarget(state, def) == CardTarget.AnyEnemy
        )
            targetIndex = RandomAutoPlayTargetIndex(state, rng);

        int callerTarget = state.TargetEnemyIndex;
        state.TargetEnemyIndex = targetIndex;
        // Snapshotted before the effects for the same reason as the hand-played path:
        // a Rebound applied by THIS card must not redirect this card.
        int reboundBefore = BuffSystem.Get(state.PlayerBuffs, BuffId.Rebound);
        // `ReplayCount` is the card playing again, not another card, and it is part of the
        // play count RunAutomaticCardPlays settles. It is also how Decisions, Decisions gets
        // its three plays out of ONE card -- the game calls `CardCmd.AutoPlay` three times
        // on the same model, so the effects happen three times and the card comes to rest
        // in one pile, once.
        CardInstance played;
        bool nostalgiaReturnsToDraw = CaptureNostalgiaDestination(state, def);
        bool corruptedSkill = IsCorruptedSkill(state, def);
        try
        {
            played = RunAutomaticCardPlays(state, card, def, rng);
        }
        finally
        {
            state.TargetEnemyIndex = callerTarget;
        }

        if (def.Type == CardType.Power || card.Dupe)
        {
            // Played powers leave hand without firing exhaust hooks. A dupe's result pile
            // is `PileType.None` too (`GetResultLocationForCardPlay`), and `CreateDupe`
            // removed its Exhaust keyword: it is removed from combat, not exhausted.
        }
        else if (ShouldExhaustAfterPlay(card) || corruptedSkill)
        {
            Effects.CardEffects.ExhaustCard(
                state,
                played,
                rng: rng,
                bindAfterHooks: bindWhenPlaced
            );
        }
        else if (ShouldPlaceOnDrawPileAfterPlay(state, def, reboundBefore, nostalgiaReturnsToDraw))
        {
            state.TopDeck(Placed(played) with { FreeThisTurn = false });
        }
        else
        {
            var placed = Placed(played);
            state.DiscardPile.Add(
                placed with
                {
                    FreeThisTurn = false,
                    SlyForCombat = placed.SlyForCombat || MasterPlannerMarks(state, def),
                }
            );
        }

        Effects.RelicEffects.ApplyAfterPlayerHpChanged(state);
    }

    internal static int RandomAutoPlayTargetIndex(CombatState state, Random rng)
    {
        var targets = Enumerable
            .Range(0, state.Enemies.Count)
            .Where(i => EnemyAI.IsTargetable(state.Enemies[i]))
            .ToArray();
        return targets.Length == 0 ? -1 : targets[(state.TargetRng ?? rng).Next(targets.Length)];
    }

    private static void IncrementPlayedCardTypeCounters(
        CombatState state,
        CardDef def,
        CardInstance? played = null
    )
    {
        state.CardPlaysThisTurn++;
        state.CardsPlayedThisCombat++;
        // PhantomBlades reads completed Shiv plays, including automatic plays and
        // repeats. Count here, after each effect, before another Shiv can resolve.
        // Physical card placement happens only once and skips automatic play paths.
        if (def.Name == "Shiv")
        {
            state.ShivsPlayedThisTurn++;
        }

        // `CardPlaysFinished` counted here, not at the start of the play: Lunar Blast's own
        // play must not be in its count, and this runs after the card resolves.
        if (def.Type == CardType.Skill)
        {
            state.SkillCardsPlayedThisTurn++;
        }

        if (def.Type == CardType.Attack || def.Type == CardType.Skill)
        {
            state.AttackOrSkillCardsPlayedThisTurn++;
        }

        // The `CardPlaysFinished` row History Course reads next turn. Recorded here, where
        // the game records it -- after the card has resolved -- for Attacks only
        // (`e.CardPlay.Card.Type == CardType.Attack`), and NOT for a card the relic itself
        // duped, which its `!e.CardPlay.Card.IsDupe` filter excludes. Any other automatic
        // play (Havoc, Mayhem, Sly) is a real card's row and counts.
        var finished = played ?? new CardInstance(def.Id, false);
        if (def.Type == CardType.Attack && !finished.Dupe)
        {
            state.LastAttackThisTurn = finished;
        }
    }

    private static void ApplyBlockNextTurn(CombatState state, Random? rng)
    {
        int blockNextTurn = BuffSystem.Get(state.PlayerBuffs, BuffId.BlockNextTurn);
        if (blockNextTurn <= 0)
        {
            return;
        }

        Effects.CardEffects.GainUnpoweredBlock(state, blockNextTurn, rng);
        BuffSystem.Remove(state.PlayerBuffs, BuffId.BlockNextTurn);
    }

    /// <summary>
    /// `TheBombPower.BeforeSideTurnEnd`, once per INSTANCE, oldest first: a fuse above 1 is
    /// decremented; otherwise its own damage (`ValueProp.Unpowered`) goes to
    /// `CombatState.HittableEnemies` and that one instance is removed.
    /// </summary>
    /// <remarks>
    /// `IterateHookListeners` lists the player's powers before the pass starts, so the
    /// fuses alive at the start are the ones that tick. `CreatureCmd.Damage` hits every
    /// target in its snapshot and then awaits `Kill` for the ones it killed, all before the
    /// power removes itself -- so the deaths are paid here, between two explosions, and a
    /// Gremlin Merc's reinforcements are standing when the next fuse picks its targets.
    /// </remarks>
    private static void TickTheBomb(
        CombatState state,
        ref int[] enemyHpsBefore,
        ref int enemyDamageBanked,
        Random rng
    )
    {
        int fuses = state.PlayerBuffs.Count(buff => buff.Id == BuffId.TheBombPower);
        int ordinal = 0;
        for (int n = 0; n < fuses; n++)
        {
            int index = Effects.CardEffects.TheBombFuseIndex(state, ordinal);
            if (index < 0)
            {
                return;
            }

            int turns = state.PlayerBuffs[index].Magnitude;
            if (turns > 1)
            {
                // `PowerCmd.Decrement(this)`: this instance only.
                state.PlayerBuffs[index] = state.PlayerBuffs[index] with
                {
                    Magnitude = turns - 1,
                };
                ordinal++;
                continue;
            }

            int damage = Effects.CardEffects.TheBombDamageAt(state, index);
            // `Creature.IsHittable`: alive, and not held off by a reviving power.
            foreach (
                var enemy in state.Enemies.Where(e => e.Hp > 0 && EnemyAI.IsTargetable(e)).ToList()
            )
            {
                Effects.CardEffects.DealUnpoweredDamageToEnemy(state, enemy, damage);
            }

            DispatchEndTurnDeaths(state, ref enemyHpsBefore, ref enemyDamageBanked, rng);

            // `PowerCmd.Remove(this)`, found again by ordinal: the deaths above may have
            // moved it. The next fuse now has this ordinal.
            index = Effects.CardEffects.TheBombFuseIndex(state, ordinal);
            state.PlayerBuffs.RemoveRange(index, 2);
        }
    }

    private static EnemyState CreateEnemy(
        int defId,
        Random rng,
        Intent intent,
        bool stunned = false,
        int? ascension = null,
        int moveIndex = 0,
        // The combat, so the HP roll uses the stream the game uses. Without it the roll
        // falls back to the combat rng with no unique-HP rule.
        CombatState? state = null
    )
    {
        var def = GeneratedData.Enemies.Get(defId);
        var band = def.HpBand(ascension ?? state?.AscensionLevel ?? Ascension.DefaultLevel);
        int hp = EnemyAI.RollSummonedHp(band.Min, band.Max, state, rng);
        var enemy = new EnemyState
        {
            DefId = defId,
            Hp = hp,
            MaxHp = hp,
            CurrentIntent = intent,
            Buffs = [],
            MoveIndex = moveIndex,
        };
        if (stunned)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Stunned, 1);
        }

        return enemy;
    }
}

public readonly record struct StepResult(bool Terminal, bool PlayerWon, float Reward)
{
    public static readonly StepResult Invalid = new(false, false, 0f);
}

/// <summary>
/// The roster just before the first death pass an attack ran inside a chosen play's
/// effect. Recorded once; later passes in the same effect do not overwrite it.
/// </summary>
internal sealed class AwaitedDeathPrePass
{
    public EnemyState[]? Roster;
    public int[]? Hps;

    public void Record(CombatState state)
    {
        if (Roster is not null)
            return;
        Roster = [.. state.Enemies];
        Hps = [.. Roster.Select(enemy => enemy.Hp)];
    }
}
