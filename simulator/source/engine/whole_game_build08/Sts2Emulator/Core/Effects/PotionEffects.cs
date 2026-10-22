using Sts2Emulator.Core.Run;

namespace Sts2Emulator.Core.Effects;

public static class PotionEffects
{
    /// <summary>`Clarity`'s `PowerVar&lt;ClarityPower&gt;(3)` -- TURNS, one card each.</summary>
    private const int ClarityTurns = 3;

    /// <summary>`RadiantTincture`'s `PowerVar&lt;RadiancePower&gt;(3)` -- turns, one energy each.</summary>
    private const int RadianceTurns = 3;

    /// <summary>
    /// `RegenPotion`'s `PowerVar&lt;RegenPower&gt;(5)`. The amount HEALED on the first turn
    /// and the number of turns it runs for, because the power decrements as it pays.
    /// </summary>
    private const int RegenAmount = 5;

    /// <summary>`PowderedDemise`'s `DynamicVar("Demise", 9)`.</summary>
    private const int PowderedDemiseAmount = 9;

    /// <summary>`BottledPotential`'s `CardsVar(5)`.</summary>
    private const int BottledPotentialCards = 5;

    /// <summary>`DexterityPotion`'s `PowerVar&lt;DexterityPower&gt;(2)`.</summary>
    private const int DexterityPotionAmount = 2;

    /// <summary>`EnergyPotion`'s `EnergyVar(2)`.</summary>
    private const int EnergyPotionAmount = 2;

    /// <summary>`BeetleJuice`'s `RepeatVar(4)`, applied as ShrinkPower.</summary>
    private const int BeetleJuiceTurns = 4;

    /// <summary>`PotionOfBinding`'s WeakPower and VulnerablePower vars are both one.</summary>
    private const int BindingDebuffAmount = 1;

    /// <summary>`FirePotion`'s `DamageVar(20, ValueProp.Unpowered)`.</summary>
    private const int FirePotionDamage = 20;

    /// <summary>`CunningPotion`'s `CardsVar(3)`: the Shivs `Shiv.CreateInHand` makes.</summary>
    private const int CunningPotionCards = 3;

    /// <summary>`FoulPotion`'s `DamageVar(12m, ValueProp.Unpowered)`.</summary>
    private const int FoulPotionDamage = 12;

    /// <summary>`FlexPotion`'s `PowerVar&lt;StrengthPower&gt;(5)`, applied as FlexPotionPower.</summary>
    private const int FlexPotionStrength = 5;

    /// <summary>`SpeedPotion`'s `PowerVar&lt;DexterityPower&gt;(5)`, applied as SpeedPotionPower.</summary>
    private const int SpeedPotionDexterity = 5;

    /// <summary>`MazalethsGift`'s `PowerVar&lt;RitualPower&gt;(1)`.</summary>
    private const int MazalethsGiftRitual = 1;

    /// <summary>`Fortifier.OnUse`: `GainBlock(target, target.Block * 2, Unpowered)`.</summary>
    private const int FortifierBlockMultiplier = 2;

    /// <summary>`LiquidBronze`'s `PowerVar&lt;ThornsPower&gt;(3)`.</summary>
    private const int LiquidBronzeThorns = 3;

    /// <summary>`VulnerablePotion`'s `PowerVar&lt;VulnerablePower&gt;(3)`.</summary>
    private const int VulnerablePotionAmount = 3;

    /// <summary>`DistilledChaos`'s `RepeatVar(3)`.</summary>
    private const int DistilledChaosCards = 3;

    /// <summary>`FruitJuice`'s `MaxHpVar(5m)`.</summary>
    internal const int FruitJuiceMaxHp = 5;

    /// <summary>`GlowwaterPotion`'s `CardsVar(10)`.</summary>
    private const int GlowwaterCards = 10;

    /// <summary>`PotionShapedRock`'s `DamageVar(15m, ValueProp.Unpowered)`.</summary>
    private const int PotionShapedRockDamage = 15;

    /// <summary>`SneckoOil`'s `CardsVar(7)`.</summary>
    private const int SneckoOilCards = 7;

    /// <summary>`Ambergris`'s `DynamicVar("HealPercent", 50m)`.</summary>
    private const decimal AmbergrisHealPercent = 50m;

    internal static readonly int FruitJuiceId = NamedPotionId("FruitJuice");

    internal static readonly int AmbergrisId = NamedPotionId("Ambergris");

    internal static readonly int BloodPotionId = NamedPotionId("BloodPotion");

    /// <summary>`BloodPotion`'s `DynamicVar("HealPercent", 20m)`.</summary>
    private const decimal BloodPotionHealPercent = 20m;

    private static int NamedPotionId(string name) =>
        GeneratedData.Potions.FindId(name)
        ?? throw new InvalidOperationException($"No potion named {name}");

    public static void Apply(int potionId, CombatState state, Random? rng = null)
    {
        // Populated incrementally as potions are reverse-engineered from sts2.dll.
        switch (potionId)
        {
            case 1: // Ashwater: exhaust any number of chosen hand cards, on confirm.
                OpenAshwaterSelection(state, potionId);
                break;
            case 2: // AttackPotion: choose a generated Attack, free this turn.
                CardEffects.OpenPoolCardPotionSelection(state, potionId, CardType.Attack, rng);
                break;
            case 3: // BeetleJuice: four owner-side turns of powered attack reduction.
                if (CardEffects.TargetedEnemyFor(state) is { } shrunk)
                    CardEffects.ApplyPotionDebuffToTarget(
                        state,
                        shrunk,
                        BuffId.Shrink,
                        BeetleJuiceTurns,
                        rng
                    );
                break;
            case 4: // BlessingOfTheForge: upgrade eligible cards in the current hand.
                CardEffects.UpgradeAllCardsInHand(state);
                break;
            case 5: // Block Potion: gain 12 unpowered Block.
                CardEffects.GainUnpoweredBlock(state, 12, rng);
                break;
            case 6: // BloodPotion: heal 20% of maximum HP (Creature truncates).
                CardEffects.HealPlayer(state, BloodPotionHealAmount(state.PlayerMaxHp));
                break;
            case 8: // BottledPotential: hand to draw, the ordinary shuffle, then draw 5.
                ReshuffleHandAndDraw(state, rng ?? state.ShuffleRng ?? new Random(0));
                break;
            case 9: // Clarity: draw one now, then one extra on each of the next 3 turns.
                CardEffects.DrawCards(state, 1, rng ?? state.ShuffleRng ?? new Random(0));
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Clarity, ClarityTurns);
                break;
            case 10: // ColorlessPotion: choose a generated Colorless card, free this turn.
                CardEffects.OpenColorlessPotionSelection(state, potionId, rng);
                break;
            case 12: // CunningPotion: Shiv.CreateInHand(Cards), then upgrade every Shiv made.
                CreateShivsThenUpgradeThem(state, CunningPotionCards);
                break;
            case 15: // DistilledChaos: auto-play the top 3 of the draw pile, no energy.
                // `AutoPlayFromDrawPile(3, Top, forceExhaust: false)` -- Cascade's command:
                // all three staged (shuffling as needed) before the first plays.
                // A child that asks the player (Burning Pact) parks the batch, and the
                // potion's use completes only after the rest has played.
                CombatEngine.BeginPotionAutoPlayBatch(
                    state,
                    DistilledChaosCards,
                    rng ?? state.ShuffleRng ?? new Random(0)
                );
                break;
            case 16: // DropletOfPrecognition: a chosen draw-pile card into hand.
                OpenDropletSelection(state, potionId);
                break;
            case 13: // CureAll: EnergyVar(1), then CardsVar(2).
                CardEffects.GainEnergy(state, 1);
                CardEffects.DrawCards(state, 2, rng ?? state.ShuffleRng ?? new Random(0));
                break;
            case 14: // DexterityPotion: PowerVar<DexterityPower>(2), on the player.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Dexterity, DexterityPotionAmount);
                break;
            case 17: // Duplicator: play the next card an extra time.
                // `DuplicationPower`, NOT `OneTwoPunchPower`. Both add a play and both
                // decrement, and the game keeps them as separate classes because
                // OneTwoPunch refuses anything that is not an Attack. This case stacked
                // OneTwoPunch, so a Duplicator spent on a Skill or a Power did nothing at
                // all here and doubled it in the game -- see E490.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Duplication, 1);
                break;
            case 18: // EnergyPotion: gain two through the ordinary energy modifiers.
                CardEffects.GainEnergy(state, EnergyPotionAmount);
                break;
            case 19: // EntropicBrew: fill every usable empty slot, including its own.
                var potionRng = state.PotionGenerationRng ?? rng ?? new Random(0);
                for (
                    int slot = 0;
                    slot < Math.Min(state.MaxPotionSlots, state.PotionSlots.Length);
                    slot++
                )
                {
                    if (state.PotionSlots[slot] != 0)
                        continue;
                    int potion = RunRewardGenerator.NextPotion(
                        state.Progression,
                        potionRng,
                        state.Character
                    );
                    // TryToProcure checks Sozu after the rarity and item have rolled.
                    if (RelicEffects.Has(state.Relics, RelicEffects.Sozu))
                        break;
                    state.PotionSlots[slot] = potion;
                }
                break;
            case 21: // ExplosiveAmpoule: DamageVar(10, Unpowered), all enemies.
                CardEffects.DealUnpoweredDamageToAll(state, 10);
                break;
            case 27: // FoulPotion in combat: DamageVar(12, Unpowered) at every creature.
                // `CreatureCmd.Damage(CombatState.Creatures.Where(!IsPet), ...)` with the
                // player as dealer. `Creatures` is allies then enemies, so the player is
                // hit first -- through block, with its HP-loss hooks (Centennial Puzzle)
                // -- and then each enemy in roster order, through block. Unpowered: no
                // Strength, Weak or Vulnerable on either side.
                CardEffects.DealDamageToPlayer(state, FoulPotionDamage);
                CardEffects.DealUnpoweredDamageToAll(state, FoulPotionDamage);
                break;
            case 23: // FirePotion: DamageVar(20, Unpowered) at the AnyEnemy target.
                if (CardEffects.TargetedEnemyFor(state) is { } burned)
                    CardEffects.DealUnpoweredDamageToEnemy(state, burned, FirePotionDamage);
                break;
            case 24: // FlexPotion: FlexPotionPower(5), a TemporaryStrengthPower.
                // `BeforeApplied` grants the Strength through PowerCmd -- Ruined Helmet's
                // door -- and `AfterSideTurnEnd` hands the power's own amount back.
                RelicEffects.GainPlayerStrength(state, FlexPotionStrength);
                BuffSystem.Apply(state.PlayerBuffs, BuffId.TemporaryStrength, FlexPotionStrength);
                break;
            case 26: // Fortifier: GAIN twice the current Block, unpowered -- 5 becomes 15.
                // Read before the gain; Unpowered, so Dexterity and Frail do not touch it,
                // and it is one Block gain for Juggernaut and the other gain hooks.
                CardEffects.GainUnpoweredBlock(
                    state,
                    FortifierBlockMultiplier * state.PlayerBlock,
                    rng
                );
                break;
            case 28: // FruitJuice: GainMaxHp(5), which heals the gain.
                CardEffects.GainMaxHp(state, FruitJuiceMaxHp);
                break;
            case 29: // FyshOil: StrengthPower(1), then DexterityPower(1).
                RelicEffects.GainPlayerStrength(state, 1);
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Dexterity, 1);
                break;
            case 30: // GamblersBrew: discard any number from hand, then draw that many.
                OpenGamblersBrewSelection(state, potionId);
                break;
            case 32: // Gigantification: the next attack card deals TRIPLE damage.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Gigantification, 1);
                break;
            case 33: // GlowwaterPotion: exhaust the hand as it was, THEN draw 10.
                ExhaustHandThenDraw(state, rng ?? state.ShuffleRng ?? new Random(0));
                break;
            case 34: // HeartOfIron: v0.111.0 PowerVar<PlatingPower>(7).
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Plating, 7);
                break;
            case 36: // LiquidBronze: PowerVar<ThornsPower>(3), a Counter, so it stacks.
                // ThornsPower answers each powered hit before it lands (EnemyAI's Thorns).
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Thorns, LiquidBronzeThorns);
                break;
            case 37: // LiquidMemories: a chosen discard returns to hand, free this turn.
                OpenLiquidMemoriesSelection(state, potionId);
                break;
            case 38: // LuckyTonic: PowerVar<BufferPower>(1).
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Buffer, 1);
                break;
            case 39: // MazalethsGift: PowerVar<RitualPower>(1), on the player.
                // RitualPower only skips its first payout when an ENEMY owns it, so the
                // player's first Strength arrives at the end of this very turn.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Ritual, MazalethsGiftRitual);
                break;
            case 40: // OrobicAcid: one generated Attack, Skill and Power, free this turn.
                CardEffects.AddOrobicAcidCards(state, rng ?? state.CardGenerationRng ?? new Random(0));
                break;
            case 42: // PotionOfBinding: Weak on the whole snapshot, then Vulnerable.
                // Creature.IsHittable requires alive AND ShouldAllowHitting. Native
                // IsTargetable also allows zero-HP SteamEruption; that is not alive.
                var bindingTargets = state
                    .Enemies.Where(enemy => enemy.Hp > 0 && EnemyAI.IsTargetable(enemy))
                    .ToArray();
                foreach (var enemy in bindingTargets)
                {
                    if (state.PlayerHp <= 0 || CombatEngine.NoPrimaryEnemyLeft(state))
                        return;
                    CardEffects.ApplyPotionDebuffToTarget(
                        state,
                        enemy,
                        BuffId.Weak,
                        BindingDebuffAmount,
                        rng
                    );
                }
                foreach (var enemy in bindingTargets)
                {
                    // PowerCmd.Apply(targets) awaits and rechecks ending per target.
                    // Vicious may draw a Hellraiser Strike that ends combat here.
                    if (state.PlayerHp <= 0 || CombatEngine.NoPrimaryEnemyLeft(state))
                        return;
                    CardEffects.ApplyPotionDebuffToTarget(
                        state,
                        enemy,
                        BuffId.Vulnerable,
                        BindingDebuffAmount,
                        rng
                    );
                }
                break;
            case 45: // PotionShapedRock: DamageVar(15, Unpowered) at the AnyEnemy target.
                if (CardEffects.TargetedEnemyFor(state) is { } struck)
                    CardEffects.DealUnpoweredDamageToEnemy(state, struck, PotionShapedRockDamage);
                break;
            case 47: // Powdered Demise: 9 unblockable to one enemy at each of its turn ends.
                // TargetType.AnyEnemy, so it lands on the AIMED-AT creature rather than on
                // every one of them.
                if (CardEffects.TargetedEnemyFor(state) is { } cursed)
                {
                    BuffSystem.Apply(cursed.Buffs, BuffId.Demise, PowderedDemiseAmount);
                }

                break;
            case 48: // PowerPotion: choose a generated power, free this turn.
                CardEffects.OpenPoolCardPotionSelection(state, potionId, CardType.Power, rng);
                break;
            case 49: // Radiant Tincture: +1 energy now and for the next 3 turns.
                CardEffects.GainEnergy(state, 1);
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Radiance, RadianceTurns);
                break;
            case 50: // Regen Potion: heal 5 at turn end, winding down by one a turn.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Regen, RegenAmount);
                break;
            case 51: // Shackling Potion: all enemies lose 7 Strength this turn.
                foreach (var enemy in state.Enemies.Where(e => e.Hp > 0))
                {
                    if (BuffSystem.TryConsumeArtifact(enemy.Buffs))
                    {
                        continue;
                    }

                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, -7);
                    BuffSystem.Apply(enemy.Buffs, BuffId.TemporaryStrength, 7);
                }
                break;
            case 52: // ShipInABottle: ten unpowered Block now and next turn.
                CardEffects.GainUnpoweredBlock(state, 10, rng);
                BuffSystem.Apply(state.PlayerBuffs, BuffId.BlockNextTurn, 10);
                break;
            case 53: // SkillPotion: choose a generated Skill, free this turn.
                CardEffects.OpenPoolCardPotionSelection(state, potionId, CardType.Skill, rng);
                break;
            case 54: // SneckoOil: draw 7, then roll 0..3 for each costed card in hand.
                CardEffects.DrawCards(state, SneckoOilCards, rng ?? state.ShuffleRng ?? new Random(0));
                RandomizeHandCosts(state, rng ?? new Random(0));
                break;
            case 55: // SoldiersStew: every Strike-tagged card held gains one replay.
                GiveStrikeCardsOneReplay(state);
                break;
            case 56: // SpeedPotion: SpeedPotionPower(5), a TemporaryDexterityPower.
                // The same pair Anticipate applies: the Dexterity now, and the power that
                // hands it back.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Dexterity, SpeedPotionDexterity);
                BuffSystem.Apply(
                    state.PlayerBuffs,
                    BuffId.TemporaryDexterity,
                    SpeedPotionDexterity
                );
                break;
            case 57: // StableSerum: v0.111.0 RepeatVar(2), RetainHandPower.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.RetainHand, 2);
                break;
            case 59: // StrengthPotion: PowerVar<StrengthPower>(2).
                RelicEffects.GainPlayerStrength(state, 2);
                break;
            case 60: // SwiftPotion: CardsVar(3), through normal draw hooks.
                CardEffects.DrawCards(state, 3, rng ?? state.ShuffleRng ?? new Random(0));
                break;
            case 61: // TouchOfInsanity: a chosen card that costs something is free this combat.
                OpenTouchOfInsanitySelection(state, potionId);
                break;
            case 62: // VulnerablePotion: Vulnerable 3 at the AnyEnemy target, no card source.
                if (CardEffects.TargetedEnemyFor(state) is { } exposed)
                    CardEffects.ApplyPotionDebuffToTarget(
                        state,
                        exposed,
                        BuffId.Vulnerable,
                        VulnerablePotionAmount,
                        rng
                    );
                break;
            case 63: // WeakPotion has no card source: Artifact applies, Lamp does not.
                if (CardEffects.TargetedEnemyFor(state) is { } target)
                    CardEffects.ApplyPotionDebuffToTarget(state, target, BuffId.Weak, 3, rng);
                break;
            case 64: // Ambergris: heal half of max HP, then (in combat) an extra turn.
                CardEffects.HealPlayer(state, AmbergrisHealAmount(state.PlayerMaxHp));
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Ambergris, 1);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// `CreatureCmd.Heal(target, MaxHp * 50m / 100m)`. `Creature.SetCurrentHpInternal`
    /// truncates the decimal sum, and HP is whole, so the heal is the truncated amount.
    /// Shared by the combat case and the out-of-combat use.
    /// </summary>
    internal static int AmbergrisHealAmount(int maxHp) =>
        (int)(maxHp * AmbergrisHealPercent / 100m);

    /// <summary>
    /// `BloodPotion.OnUse`: `CreatureCmd.Heal(target, MaxHp * 20m / 100m)`, truncated by
    /// `Creature.SetCurrentHpInternal`. Shared by the combat case and the out-of-combat use.
    /// </summary>
    internal static int BloodPotionHealAmount(int maxHp) =>
        (int)(maxHp * BloodPotionHealPercent / 100m);

    /// <summary>
    /// `GlowwaterPotion.OnUse`: `Hand.Cards.ToList()`, `CardCmd.Exhaust` on each in hand
    /// order, then `CardPileCmd.Draw(10)`. Cards a hook draws during the exhaust (Dark
    /// Embrace) were not in the snapshot and stay. The draw checks the combat's end itself.
    /// </summary>
    private static void ExhaustHandThenDraw(CombatState state, Random rng)
    {
        ExhaustCardsInOrder(state, [.. Enumerable.Range(0, state.Hand.Count)], rng);
        CardEffects.DrawCards(state, GlowwaterCards, rng);
    }

    /// <summary>
    /// `SneckoOil.OnUse` after its draw: every hand card whose cost is not X and whose
    /// `GetWithModifiers(None)` (its base) is not negative gets
    /// `SetThisTurnOrUntilPlayed(Rng.CombatEnergyCosts.NextInt(4))`, in hand order.
    /// </summary>
    /// <remarks>
    /// The modifier is absolute and not reduce-only, so it replaces whatever local cost
    /// the copy had; `FreeThisTurn` and `CostThisTurn` share its expiry and are cleared.
    /// </remarks>
    private static void RandomizeHandCosts(CombatState state, Random fallback)
    {
        var energyCosts = state.EnergyCostRng as Random ?? fallback;
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var card = state.Hand[i];
            var def = GeneratedData.Cards.Get(card.DefId);
            if (def.HasEnergyCostX || def.Cost < 0)
                continue;

            state.Hand[i] = card with
            {
                FreeThisTurn = false,
                CostThisTurn = int.MinValue,
                CostOverrideThisTurn = energyCosts.Next(4),
                CostOverrideBaseline = CombatEngine.CounterCostDiscount(def, state),
                CostOverrideSetUpgraded = card.Upgraded,
            };
        }
    }

    /// <summary>
    /// `DropletOfPrecognition.OnUse`: `CardSelectCmd.FromCombatPile(Draw, prefs(1))`, then
    /// `CardPileCmd.Add(card, Hand)`. An empty pile returns nothing, and a pile of one is
    /// taken without a screen (`num &lt;= MinSelect`); otherwise the use waits on the pick.
    /// </summary>
    private static void OpenDropletSelection(CombatState state, int potionId)
    {
        switch (state.DrawPile.Count)
        {
            case 0:
                return;
            case 1:
                MoveDrawPileCardToHand(state, 0);
                return;
        }

        state.PendingSelection = new PendingCardSelection
        {
            Kind = CardSelectionKind.DrawPileToHand,
            Candidates = [.. Enumerable.Range(0, state.DrawPile.Count)],
            SourceCardDefId = 0,
            SourcePotionId = potionId,
            Amount = 1,
        };
    }

    /// <summary>
    /// Droplet's `CardPileCmd.Add(card, Hand)`: a full hand redirects the add to the
    /// DISCARD pile (CardPileCmd.cs:484-487), so the card still leaves the draw pile.
    /// </summary>
    internal static void MoveDrawPileCardToHand(CombatState state, int drawIndex)
    {
        if (drawIndex < 0 || drawIndex >= state.DrawPile.Count)
            return;

        var card = state.DrawPile[drawIndex];
        state.RemoveFromDrawPileAt(drawIndex);
        var destination =
            state.Hand.Count < CardEffects.MaxCardsInHand ? state.Hand : state.DiscardPile;
        destination.Add(card);
    }

    /// <summary>
    /// `BottledPotential.OnUse`: `CardPileCmd.Add(Hand.Cards, PileType.Draw)`, then
    /// `CardPileCmd.Shuffle`, then `CardPileCmd.Draw(CardsVar(5))` -- Reboot's body.
    /// </summary>
    /// <remarks>
    /// The hand goes to the BOTTOM of the draw pile in hand order (`Add`'s default
    /// position), and that order matters: `Shuffle` builds discard-then-draw and sorts it
    /// with the unstable introsort before Fisher-Yates on `Rng.Shuffle`, so tied copies
    /// land by input position. `Shuffle` always runs -- it has no empty-pile guard -- and
    /// fires AfterShuffle (Abacus, Stratagem). The exhaust pile is not touched. The moved
    /// cards keep any this-turn cost, because `EndOfTurnCleanup` visits every pile. The
    /// draw is an ordinary non-hand draw, so No Draw vetoes it and Speedster counts it.
    /// </remarks>
    private static void ReshuffleHandAndDraw(CombatState state, Random rng)
    {
        state.DrawPile.AddRange(state.Hand);
        state.Hand.Clear();
        CardEffects.ShuffleDiscardIntoDraw(state, rng);
        CardEffects.DrawCards(state, BottledPotentialCards, rng);
    }

    /// <summary>
    /// `SoldiersStew.OnUse`: `PlayerCombatState.AllCards` filtered to `CardTag.Strike`,
    /// then `BaseReplayCount++` on each -- the cards held NOW, for the rest of the combat.
    /// </summary>
    /// <remarks>
    /// `AllCards` is Hand, Draw, Discard, Exhaust and Play. A solo player cannot drink while
    /// one of their own cards is resolving or waiting on a screen (`UsePotionAction` is
    /// play-phase only and the action queue runs serially), and native `Step` answers an
    /// open selection before anything else, so Play is empty at every legal drink and the
    /// four piles are the whole set. Cards are values here, so each is written back in
    /// place with only its `ReplayCount` changed; the run-deck card it came from
    /// (`DeckOrigin`) is untouched. No energy, RNG or pile movement.
    /// </remarks>
    private static void GiveStrikeCardsOneReplay(CombatState state)
    {
        foreach (
            var pile in new[] { state.Hand, state.DrawPile, state.DiscardPile, state.ExhaustPile }
        )
        {
            for (int i = 0; i < pile.Count; i++)
            {
                if (GeneratedData.Cards.Get(pile[i].DefId).StrikeTag)
                {
                    pile[i] = pile[i] with { ReplayCount = pile[i].ReplayCount + 1 };
                }
            }
        }
    }

    /// <summary>
    /// `GamblersBrew.OnUse`: `CardSelectCmd.FromHandForDiscard(prefs(0, 999999999))`, then
    /// `CardCmd.DiscardAndDraw(list, list.Count)`.
    /// </summary>
    /// <remarks>
    /// Min zero and max unbounded, so `RequireManualConfirmation` is set and the screen
    /// stays up until it is confirmed -- the skip action is the confirm. Nothing is
    /// discarded while it is open: `FromHand` returns the whole selection and only then does
    /// `DiscardAndDraw` move it, which is why the picks are carried on
    /// <see cref="PendingCardSelection.Selected" /> rather than discarded one by one the way
    /// Gambling Chip's are. An empty hand returns an empty list and nothing is drawn.
    /// </remarks>
    private static void OpenGamblersBrewSelection(CombatState state, int potionId)
    {
        if (state.Hand.Count == 0)
            return;

        state.PendingSelection = new PendingCardSelection
        {
            Kind = CardSelectionKind.DiscardAnyThenDraw,
            Candidates = [.. Enumerable.Range(0, state.Hand.Count)],
            SourceCardDefId = 0,
            SourcePotionId = potionId,
            Skippable = true,
        };
    }

    /// <summary>
    /// One more pick on Gambler's Brew's screen. The card stays in hand; the screen reopens
    /// over the cards not yet picked, and with none left only the confirm remains.
    /// </summary>
    internal static void PickForGamblersBrew(
        CombatState state,
        PendingCardSelection selection,
        int handIndex
    )
    {
        List<int> selected = [.. selection.Selected, handIndex];
        state.PendingSelection = new PendingCardSelection
        {
            Kind = CardSelectionKind.DiscardAnyThenDraw,
            Candidates =
            [
                .. Enumerable.Range(0, state.Hand.Count).Where(i => !selected.Contains(i)),
            ],
            SourceCardDefId = 0,
            SourcePotionId = selection.SourcePotionId,
            Skippable = true,
            Selected = selected,
        };
    }

    /// <summary>
    /// Gambler's Brew confirmed: `DiscardAndDraw(list, list.Count)`. Every picked card is
    /// discarded, in the order picked and each through `AfterCardDiscarded`, and only then
    /// is anything drawn. Sly cards are queued by the discard and play after the draw.
    /// </summary>
    internal static void ConfirmGamblersBrew(
        CombatState state,
        PendingCardSelection selection,
        Random rng
    )
    {
        var picked = selection
            .Selected.Where(i => i >= 0 && i < state.Hand.Count)
            .Distinct()
            .ToList();
        if (picked.Count == 0)
            return;

        var cards = picked.Select(i => state.Hand[i]).ToList();
        foreach (int index in picked.OrderByDescending(i => i))
            state.Hand.RemoveAt(index);

        CardEffects.DiscardMovedCards(state, cards, rng);
        CardEffects.DrawCards(state, cards.Count, rng);
    }

    /// <summary>
    /// `Ashwater.OnUse`: `CardSelectCmd.FromHand(prefs(0, 999999999), filter: null)`, then
    /// `CardCmd.Exhaust` on each card of the returned list.
    /// </summary>
    /// <remarks>
    /// Gambler's Brew's screen: min zero and max unbounded set `RequireManualConfirmation`,
    /// so the screen stays up until it is confirmed and the skip action is the confirm.
    /// Nothing is exhausted while it is open -- `FromHand` returns the whole list first --
    /// so the picks ride on <see cref="PendingCardSelection.Selected" />. An empty hand
    /// returns an empty list with no screen.
    /// </remarks>
    private static void OpenAshwaterSelection(CombatState state, int potionId)
    {
        if (state.Hand.Count == 0)
            return;

        state.PendingSelection = new PendingCardSelection
        {
            Kind = CardSelectionKind.ExhaustAnyFromHand,
            Candidates = [.. Enumerable.Range(0, state.Hand.Count)],
            SourceCardDefId = 0,
            SourcePotionId = potionId,
            Skippable = true,
        };
    }

    /// <summary>
    /// One more pick on Ashwater's screen. `SelectCardInSimpleMode` takes the card out of the
    /// displayed hand, so the screen reopens over the hand indices not yet picked, in hand
    /// order; the card itself stays in <see cref="CombatState.Hand" />.
    /// </summary>
    internal static void PickForAshwater(
        CombatState state,
        PendingCardSelection selection,
        int handIndex
    )
    {
        List<int> selected = [.. selection.Selected, handIndex];
        state.PendingSelection = new PendingCardSelection
        {
            Kind = CardSelectionKind.ExhaustAnyFromHand,
            Candidates =
            [
                .. Enumerable.Range(0, state.Hand.Count).Where(i => !selected.Contains(i)),
            ],
            SourceCardDefId = 0,
            SourcePotionId = selection.SourcePotionId,
            Skippable = true,
            Selected = selected,
        };
    }

    /// <summary>
    /// Ashwater confirmed: `foreach (item in list) await CardCmd.Exhaust(item)`, in the order
    /// picked (`_selectedCards`). Each card leaves the hand and runs its `AfterCardExhausted`
    /// hooks before the next one moves, so a Dark Embrace draw lands between two exhausts and
    /// is never itself exhausted.
    /// </summary>
    /// <remarks>
    /// The picks name card COPIES, resolved here before anything moves; their positions are
    /// then kept in step with each removal. `CardCmd.Exhaust` returns without moving anything
    /// once the combat is over or ending, so a Charon's Ashes kill leaves the rest in hand.
    /// </remarks>
    internal static void ConfirmAshwater(
        CombatState state,
        PendingCardSelection selection,
        Random rng
    )
    {
        ExhaustCardsInOrder(
            state,
            [.. selection.Selected.Where(i => i >= 0 && i < state.Hand.Count).Distinct()],
            rng
        );
    }

    /// <summary>
    /// `foreach (item in list) await CardCmd.Exhaust(item)` over hand positions resolved
    /// to copies before anything moves -- Ashwater's confirm and Glowwater's snapshot.
    /// </summary>
    private static void ExhaustCardsInOrder(CombatState state, List<int> positions, Random rng)
    {
        var cards = positions.Select(i => state.Hand[i]).ToList();

        for (int n = 0; n < cards.Count; n++)
        {
            if (state.PlayerHp <= 0 || CombatEngine.NoPrimaryEnemyLeft(state))
                return;

            int at =
                positions[n] < state.Hand.Count && state.Hand[positions[n]] == cards[n]
                    ? positions[n]
                    : state.Hand.IndexOf(cards[n]);
            if (at < 0)
                continue;

            state.Hand.RemoveAt(at);
            for (int m = n + 1; m < positions.Count; m++)
            {
                if (positions[m] > at)
                    positions[m]--;
            }

            CardEffects.ExhaustCard(state, cards[n], rng: rng);
        }
    }

    /// <summary>
    /// `LiquidMemories.OnUse`: `CardSelectCmd.FromCombatPile(Discard, prefs(1))`, then
    /// `SetToFreeThisTurn` and `CardPileCmd.Add(card, Hand)`.
    /// </summary>
    /// <remarks>
    /// `prefs(1)` is min one and max one, so there is no skip and no confirm, and a pile of
    /// one is taken without a screen (`num &lt;= MinSelect`). An empty pile returns nothing.
    /// </remarks>
    private static void OpenLiquidMemoriesSelection(CombatState state, int potionId)
    {
        switch (state.DiscardPile.Count)
        {
            case 0:
                return;
            case 1:
                ReturnDiscardFreeThisTurn(state, 0);
                return;
        }

        state.PendingSelection = new PendingCardSelection
        {
            Kind = CardSelectionKind.DiscardToHand,
            Candidates = [.. Enumerable.Range(0, state.DiscardPile.Count)],
            SourceCardDefId = 0,
            SourcePotionId = potionId,
            Amount = 1,
        };
    }

    /// <summary>
    /// Liquid Memories' card: free this turn, then `CardPileCmd.Add(card, Hand)`. A full
    /// hand redirects the add to the DISCARD pile, so the card leaves its place and lands
    /// on the end of the same pile, still free for the turn.
    /// </summary>
    internal static void ReturnDiscardFreeThisTurn(CombatState state, int discardIndex)
    {
        if (discardIndex < 0 || discardIndex >= state.DiscardPile.Count)
            return;

        var card = state.DiscardPile[discardIndex] with { FreeThisTurn = true };
        state.DiscardPile.RemoveAt(discardIndex);
        var destination =
            state.Hand.Count < CardEffects.MaxCardsInHand ? state.Hand : state.DiscardPile;
        destination.Add(card);
    }

    /// <summary>
    /// `TouchOfInsanity.OnUse`: `CardSelectCmd.FromHand(prefs(1), filter)` and
    /// `SetToFreeThisCombat` on the pick. The filter is
    /// `c.CostsEnergyOrStars(false) || c.CostsEnergyOrStars(true)` -- the card's own cost,
    /// or its cost after every global hook, above zero.
    /// </summary>
    /// <remarks>
    /// Min one and max one: no skip, and a single eligible card is taken without a screen.
    /// </remarks>
    private static void OpenTouchOfInsanitySelection(CombatState state, int potionId)
    {
        var candidates = Enumerable
            .Range(0, state.Hand.Count)
            .Where(i => CostsEnergyOrStars(state, state.Hand[i]))
            .ToList();
        switch (candidates.Count)
        {
            case 0:
                return;
            case 1:
                MakeFreeThisCombat(state, candidates[0]);
                return;
        }

        state.PendingSelection = new PendingCardSelection
        {
            Kind = CardSelectionKind.FreeThisCombatInHand,
            Candidates = candidates,
            SourceCardDefId = 0,
            SourcePotionId = potionId,
            Amount = 1,
        };
    }

    /// <summary>
    /// `CardModel.CostsEnergyOrStars(false) || CostsEnergyOrStars(true)`. The local reading
    /// is `EnergyCost.GetWithModifiers(Local) &gt; 0 || CurrentStarCost &gt; 0`; the global
    /// one excludes X costs and adds the combat hooks. Both return the base untouched for
    /// an X card (zero) and for an unplayable one (negative), so neither is eligible.
    /// </summary>
    private static bool CostsEnergyOrStars(CombatState state, CardInstance card)
    {
        var def = GeneratedData.Cards.Get(card.DefId);
        if (!def.HasStarCostX && def.StarCost > 0)
            return true;
        if (def.HasEnergyCostX || def.Cost < 0)
            return false;
        return CombatEngine.LocalEnergyCost(card, state) > 0
            || CombatEngine.EffectiveCost(card, state) > 0;
    }

    /// <summary>
    /// `SetToFreeThisCombat`: `EnergyCost.SetThisCombat(0)`, an ABSOLUTE local modifier
    /// added after every one the card already carries -- so a combat-long bump it picked up
    /// earlier (Transfigure, a played Frantic Escape) is overridden rather than added on.
    /// </summary>
    internal static void MakeFreeThisCombat(CombatState state, int handIndex)
    {
        if (handIndex < 0 || handIndex >= state.Hand.Count)
            return;

        state.Hand[handIndex] = state.Hand[handIndex] with
        {
            CostForCombat = 0,
            CostBump = 0,
            // Added after any Snecko override, so it wins over that too.
            CostOverrideThisTurn = int.MinValue,
        };
    }

    /// <summary>
    /// `CunningPotion.OnUse`: `foreach (card in await Shiv.CreateInHand(player, count, ...))
    /// CardCmd.Upgrade(card)`. `CreateInHand` returns nothing once the combat is over or
    /// ending; otherwise it makes every Shiv and `AddGeneratedCardsToCombat` adds them one
    /// at a time -- each generation hook seeing an ordinary Shiv, and a full hand sending
    /// the rest to the discard pile. Only then is each RETURNED Shiv upgraded, so the
    /// overflow in the discard pile is upgraded too. `CardCmd.Upgrade` fires no hook.
    /// </summary>
    private static void CreateShivsThenUpgradeThem(CombatState state, int count)
    {
        if (count <= 0 || state.PlayerHp <= 0 || CombatEngine.NoPrimaryEnemyLeft(state))
            return;

        int inHand = state.Hand.Count;
        int inDiscard = state.DiscardPile.Count;
        CardEffects.AddGeneratedCardsToHand(state, SI.Shiv, count);
        for (int i = inHand; i < state.Hand.Count; i++)
            state.Hand[i] = state.Hand[i] with { Upgraded = true };
        for (int i = inDiscard; i < state.DiscardPile.Count; i++)
            state.DiscardPile[i] = state.DiscardPile[i] with { Upgraded = true };
    }
}
