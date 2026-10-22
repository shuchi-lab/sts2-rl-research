using Sts2Emulator.Core.Rng;

namespace Sts2Emulator.Core.Run;

public enum RunFollowUp
{
    None,
    CardReward,
    TransformSelect,

    /// <summary>A rewards screen holding relics the player must claim one at a time.</summary>
    BonusRelicRewards,

    /// <summary>
    /// A choose-a-card screen whose cards are ALREADY rolled, so the caller must not roll
    /// another offer over the top of them.
    /// </summary>
    PreRolledCardReward,

    /// <summary>Scroll Boxes' choose-a-bundle screen.</summary>
    BundleSelect,

    /// <summary>
    /// A queue of card-reward screens claimed one after another, as Glass Eye's five are.
    /// </summary>
    BonusCardOffers,

    /// <summary>A nested reward set already opened by the pickup effect.</summary>
    CustomRewards,
}

public static class RunNonCombatEffects
{
    private static readonly int[] RareIroncladSingleplayerPool =
    [
        9,
        29,
        58,
        546,
        99,
        113,
        114,
        119,
        141,
        183,
        188,
        246,
        261,
        272,
        295,
        328,
        332,
        334,
        339,
        364,
        374,
        464,
        494,
        505,
        525,
    ];

    /// <summary>
    /// The game's Hook.TryModifyCardBeingAddedToDeck, which the egg relics answer: a card
    /// of their type joins the deck already upgraded. Every deck addition goes through
    /// here so an event, a shop and a card reward all agree.
    /// </summary>
    /// <summary>
    /// `MawBank.AfterRoomEntered`: GoldVar(12) on entering a room, every room, until the
    /// player buys ANYTHING from a merchant -- `AfterItemPurchased` with `goldSpent > 0`
    /// sets `HasItemBeenBought` and the bank is used up for good.
    /// </summary>
    /// <remarks>
    /// The counter is the spent flag, so it survives combats the way the teas' does. The
    /// gold goes through `GainGold`, which is the run's chokepoint -- Bloody Idol and the
    /// rest read a gain however it arrives.
    /// </remarks>
    public static void PayMawBank(RunState state)
    {
        int index = state.Relics.FindIndex(relic => relic.DefId == Effects.RelicEffects.MawBank);
        if (index >= 0 && state.Relics[index].Counter == 0)
        {
            GainGold(state, 12);
        }
    }

    /// <summary>Any purchase closes the bank, whatever it cost.</summary>
    public static void CloseMawBank(RunState state, int goldSpent)
    {
        if (goldSpent <= 0)
        {
            return;
        }

        int index = state.Relics.FindIndex(relic => relic.DefId == Effects.RelicEffects.MawBank);
        if (index >= 0)
        {
            state.Relics[index] = state.Relics[index] with { Counter = 1 };
        }
    }

    /// <summary>
    /// `SwordOfStone.AfterCombatVictory`: five ELITE wins and `RelicCmd.Replace` swaps it
    /// for Sword of Jade, which is Strength 3 at the top of every fight thereafter.
    /// </summary>
    /// <remarks>
    /// Five, not three -- `DynamicVar("Elites", 5m)`. A REPLACEMENT rather than a second
    /// relic: the stone is gone, and the relic list keeps its length.
    /// </remarks>
    /// <summary>
    /// `ImprovementPower.AfterCombatEnd`: upgrade `Amount` upgradable cards in the DECK,
    /// each drawn off `Rng.CombatCardSelection` and removed from the pool so one card is
    /// never upgraded twice by the same power.
    /// </summary>
    /// <remarks>
    /// Mad Science's Improvement rider. The power lives on the player IN COMBAT and pays
    /// out against the RUN's deck once the fight is over, which is why it sat unmodelled
    /// while the other eight riders were done -- there was nowhere in combat to put it.
    /// It fires whether the fight was won or lost; `AfterCombatEnd` is not
    /// `AfterCombatVictory`.
    /// </remarks>
    public static void ApplyImprovementUpgrades(RunState state)
    {
        int amount = state.ActiveCombat is null
            ? 0
            : BuffSystem.Get(state.ActiveCombat.PlayerBuffs, BuffId.Improvement);
        if (amount <= 0)
        {
            return;
        }

        var upgradable = Enumerable
            .Range(0, state.Deck.Count)
            .Where(index => RunConstants.IsRunCardUpgradable(state.Deck[index]))
            .ToList();
        // The game has ONE `CombatCardSelection` stream, and the fight has been drawing on
        // it. The combat's copy is where the fight left it -- the run's copy is still at
        // its pre-combat position until `SyncAfterCombat`, which then overwrites it from
        // the combat's. So the pick comes off the combat's copy, and the sync carries it.
        Random? combatStream = state.ActiveCombat!.CardSelectionRng;
        for (int i = 0; i < amount && upgradable.Count > 0; i++)
        {
            int pick =
                combatStream?.Next(upgradable.Count)
                ?? state.Rng.CombatCardSelection.NextInt(upgradable.Count);
            int deckIndex = upgradable[pick];
            upgradable.RemoveAt(pick);
            state.Deck[deckIndex] = state.Deck[deckIndex] with { Upgraded = true };
        }
    }

    public static void CountEliteVictoryForSwordOfStone(RunState state, bool wasElite)
    {
        if (!wasElite)
        {
            return;
        }

        int index = state.Relics.FindIndex(relic =>
            relic.DefId == Effects.RelicEffects.SwordOfStone
        );
        if (index < 0)
        {
            return;
        }

        int elites = state.Relics[index].Counter + 1;
        state.Relics[index] = state.Relics[index] with { Counter = elites };
        if (elites >= Effects.RelicEffects.SwordOfStoneElites)
        {
            state.Relics[index] = new RelicInstance(Effects.RelicEffects.SwordOfJade);
        }
    }

    public static void AddCardToDeck(RunState state, CardInstance card)
    {
        // `DarkstonePeriapt.AfterCardChangedPiles` fires on a CURSE entering the DECK, so
        // it pays for every curse however it arrives -- an event's Decay, a Neow drawback,
        // a Normality. Read before the add so the max HP lands with the card.
        if (
            GeneratedData.Cards.Get(card.DefId).Type == CardType.Curse
            && Effects.RelicEffects.Has(state.Relics, Effects.RelicEffects.DarkstonePeriapt)
        )
        {
            GainMaxHp(state, 6);
        }

        var landed = EnchantedByFresnelLens(state, UpgradedByEggs(state, card));
        state.Deck.Add(landed);

        // `BingBong.AfterCardChangedPiles`: every card entering the DECK is CLONED to the
        // bottom of it -- `clonedBy == null` and a `CardsToSkip` set, which together mean
        // the clone does not clone itself. The doll's gift doubles a deck it likes and
        // doubles a curse just as happily.
        if (
            Effects.RelicEffects.Has(state.Relics, Effects.RelicEffects.BingBong)
            && !state.AddingBingBongClone
        )
        {
            state.AddingBingBongClone = true;
            AddCardToDeck(state, landed);
            state.AddingBingBongClone = false;
        }

        // `AfterCardChangedPiles` with the destination pile being the DECK. Two relics
        // read it, and both are per-card rather than per-reward: taking three cards in one
        // Neow blessing pays three times.
        if (Effects.RelicEffects.Has(state.Relics, Effects.RelicEffects.LuckyFysh))
        {
            GainGold(state, 15);
        }

        int book = state.Relics.FindIndex(relic =>
            relic.DefId == Effects.RelicEffects.BookOfFiveRings
        );
        if (book >= 0)
        {
            // `CardsAddedSinceLastTrigger` is `CardsAdded % 5`, so the heal lands on every
            // fifth card rather than once at five.
            int added = state.Relics[book].Counter + 1;
            state.Relics[book] = state.Relics[book] with { Counter = added % 5 };
            if (added % 5 == 0)
            {
                state.PlayerHp = Math.Min(state.PlayerMaxHp, state.PlayerHp + 20);
            }
        }
    }

    /// <summary>
    /// `FresnelLens.TryModifyCardBeingAddedToDeck`: every card entering the deck that
    /// Nimble can enchant arrives already carrying Nimble at
    /// `DynamicVar("NimbleAmount", 2m)`.
    /// </summary>
    /// <remarks>
    /// The lens has three hooks and they are the same rule at three doors --
    /// `TryModifyCardRewardOptionsLate`, `ModifyMerchantCardCreationResults` and this one.
    /// The first two only change what the SCREEN shows; this one is what actually lands,
    /// and it catches every route into the deck including the ones the other two miss.
    /// Modelling only the reward screen would have enchanted a bought card and not an
    /// event's gift.
    ///
    /// `CanEnchant` is the whole rule, so a card that already carries an enchantment keeps
    /// it -- the lens does not overwrite.
    /// </remarks>
    public static CardInstance EnchantedByFresnelLens(RunState state, CardInstance card)
    {
        if (
            !Effects.RelicEffects.Has(state.Relics, Effects.RelicEffects.FresnelLens)
            || !Enchantments.CanEnchant(card, Enchantment.Nimble)
        )
        {
            return card;
        }

        return card with
        {
            Enchantment = Enchantment.Nimble,
            EnchantAmount = 2,
        };
    }

    public static CardInstance UpgradedByEggs(RunState state, CardInstance card)
    {
        if (!RunConstants.IsRunCardUpgradable(card))
        {
            return card;
        }

        int eggForType = card.ModelType() switch
        {
            CardType.Power => RunConstants.RelicFrozenEgg,
            CardType.Attack => RunConstants.RelicMoltenEgg,
            CardType.Skill => RunConstants.RelicToxicEgg,
            _ => 0,
        };

        return eggForType != 0 && HasRelic(state, eggForType)
            ? card with
            {
                Upgraded = true,
            }
            : card;
    }

    /// <summary>
    /// War Paint and Whetstone: CardsVar(2) upgraded off the deck on pickup, taken from
    /// Deck.Where(type and IsUpgradable).StableShuffle(Rng.Niche).Take(2).
    /// </summary>
    /// <param name="type">
    /// The card type to filter to, or null for ANY upgradable card -- Fragrant Mushroom's
    /// `Deck.Where(c => c.IsUpgradable)` has no type filter where War Paint's does.
    /// </param>
    /// <summary>
    /// The same StableShuffle-and-take as the pickup relics', off a stream the CALLER
    /// names -- Battleworn Dummy upgrades off the event's own Rng, not `Rng.Niche`.
    /// </summary>
    public static void UpgradeRandomDeckCardsForEvent(RunState state, int count, GameRng rng)
    {
        var candidates = Enumerable
            .Range(0, state.Deck.Count)
            .Where(index => RunConstants.IsRunCardUpgradable(state.Deck[index]))
            .ToList();

        SortDeckIndexesForStableShuffle(state, candidates);
        rng.Shuffle(candidates);

        foreach (int index in candidates.Take(count))
        {
            state.Deck[index] = state.Deck[index] with { Upgraded = true };
        }
    }

    /// <summary>
    /// The sort half of `ListExtensions.StableShuffle` over deck cards: `List.Sort()` on
    /// `CardModel.CompareTo`, which is the ModelId entry (ordinal) and then
    /// `CurrentUpgradeLevel`.
    /// </summary>
    /// <remarks>
    /// `List.Sort` is the BCL introsort, whose equal-key ordering is not stable above 16
    /// candidates, so the order of tied copies depends on every comparison the sort makes.
    /// A LINQ `OrderBy` or a sort on the numeric `DefId` therefore hands the same shuffle a
    /// different list and upgrades a different physical copy (ABB75XX step0171, Doors of
    /// Light and Dark). Sorting the indexes with the same comparison makes the same moves.
    /// </remarks>
    internal static void SortDeckIndexesForStableShuffle(RunState state, List<int> indexes) =>
        indexes.Sort(
            (left, right) =>
            {
                var a = state.Deck[left];
                var b = state.Deck[right];
                int entry = string.CompareOrdinal(
                    GeneratedData.Cards.Get(a.DefId).Entry,
                    GeneratedData.Cards.Get(b.DefId).Entry
                );
                return entry != 0 ? entry : a.Upgraded.CompareTo(b.Upgraded);
            }
        );

    private static void UpgradeRandomDeckCards(RunState state, CardType? type, int count)
    {
        var candidates = Enumerable
            .Range(0, state.Deck.Count)
            .Where(index =>
                (type is null || state.Deck[index].ModelType() == type)
                && RunConstants.IsRunCardUpgradable(state.Deck[index])
            )
            .ToList();

        SortDeckIndexesForStableShuffle(state, candidates);
        state.Rng.Niche.Shuffle(candidates);

        foreach (int index in candidates.Take(count))
        {
            state.Deck[index] = state.Deck[index] with { Upgraded = true };
        }
    }

    private static bool HasRelic(RunState state, int relicId) =>
        state.Relics.Any(relic => relic.DefId == relicId);

    public static RunFollowUp ApplyRelicPickup(
        RunState state,
        int relicId,
        SelectionReturn returnTo = SelectionReturn.Map
    )
    {
        if (state.Relics.All(relic => relic.DefId != relicId))
        {
            state.Relics.Add(new RelicInstance(relicId, StartingRelicCounter(relicId)));
            // Player.AddRelicInternal notifies existing CardRewards before AfterObtained.
            RunRewardGenerator.OnRelicObtained(state, relicId);

            // `PotionBelt.AfterObtained` -- GainMaxPotionCount(2). Inside the not-already-
            // held branch so a duplicate pickup cannot pay twice, which is the same reason
            // the strike-from-the-grab-bag below sits here.
            if (relicId == Effects.RelicEffects.PotionBelt)
            {
                state.MaxPotionSlots += 2;
            }

            // `SignetRing.AfterObtained`: GoldVar(999). Through `GainGold` rather than a
            // bare add, because that is the run's chokepoint and Bloody Idol is listening.
            if (relicId == Effects.RelicEffects.SignetRing)
            {
                GainGold(state, 999);
            }

            // `BloodSoakedRose.AfterObtained`: an Enthralled into the deck. That curse is
            // the whole price of the relic's +1 max energy -- the energy half lives in
            // `RelicEffects.MaxEnergyRelics`, and modelling only that made it free.
            //
            // Through `AddCardToDeck` so Darkstone Periapt and Bing Bong see the curse,
            // which a direct `state.Deck.Add` would have hidden from both.
            if (relicId == Effects.RelicEffects.BloodSoakedRose)
            {
                AddCardToDeck(state, new CardInstance(166, Upgraded: false));
            }

            // `TanxsWhistle.AfterObtained` and `JewelryBox.AfterObtained` are the same
            // one line with a different card: `CreateCard<T>` then `CardPileCmd.Add` to
            // the DECK. Through `AddCardToDeck` for the reason Blood Soaked Rose's curse
            // is -- Bing Bong clones whatever enters the deck, and Fresnel Lens enchants
            // it, and a direct `Deck.Add` is invisible to both.
            if (relicId == Effects.RelicEffects.TanxsWhistle)
            {
                AddCardToDeck(state, new CardInstance(539, Upgraded: false));
            }

            if (relicId == Effects.RelicEffects.JewelryBox)
            {
                AddCardToDeck(state, new CardInstance(16, Upgraded: false));
            }
        }

        // RelicCmd.Obtain strikes the relic from both grab bags unless it is stackable,
        // and Circlet -- the fallback -- is the only stackable relic there is. Without
        // this a relic handed over by name (the Sword of Stone, a Neow pick) stays in the
        // queue and can be offered a second time.
        //
        // Guarded rather than a plain Get: RunConstants carries five Neow-only relic ids
        // in the 1300-1500 range (Astrolabe = 1332, where Relics.g.cs says Astrolabe is
        // id 8) that resolve to nothing. Those are ancient relics and are not in any grab
        // bag anyway, so skipping them is correct here -- but the mismatch is real and is
        // its own bug.
        if (relicId != CircletRelic && GeneratedData.Relics.TryGet(relicId, out _))
        {
            state.RelicBag.Remove(relicId);
            state.SharedRelicBag.Remove(relicId);
        }

        switch (relicId)
        {
            case RunConstants.RelicWarPaint:
                UpgradeRandomDeckCards(state, CardType.Skill, 2);
                break;
            case RunConstants.RelicWhetstone:
                UpgradeRandomDeckCards(state, CardType.Attack, 2);
                break;
            case RunConstants.RelicGoldenPearl:
                GainGold(state, 150);
                break;
            case RunConstants.RelicNeowsTorment:
                AddCardToDeck(state, new CardInstance(RunConstants.NeowsFuryCard, Upgraded: false));
                break;
            case RunConstants.RelicNeowsBones:
                // NeowsBones.AfterObtained: shuffle every relic Neow could offer except
                // itself on PlayerRng.Rewards, take two, and OFFER them -- a RewardsSet
                // with WithSkippingDisallowed, so the player claims both -- and only then
                // add the curse.
                //
                // This used to be two Rng.UpFront.NextItem draws applied on the spot: the
                // wrong stream, a draw that can hand out the SAME relic twice where a
                // shuffle-and-take cannot, no screen at all, and a candidate list of only
                // the positives. A live capture answers a rewards screen twice here and
                // comes away with Winged Boots and Silken Tress.
                state.PendingBonusRelicRewards.Clear();
                state.PendingBonusRelicRewards.AddRange(NeowsBonesRelicOffer(state));
                state.PendingNeowsBonesCurse = true;
                // Put the first one ON the screen here rather than leaving that to the
                // caller: the pickup should leave a state that makes sense however it was
                // reached, and a queue with nothing offered from it is a screen that
                // reports rewards it will not hand over.
                RunRewardGenerator.OfferNextBonusRelic(state);
                return RunFollowUp.BonusRelicRewards;
            case RunConstants.RelicNutritiousOyster:
                GainMaxHp(state, 11);
                break;
            case RunConstants.RelicStrawberry:
                GainMaxHp(state, 7);
                break;
            case RunConstants.RelicPear:
                GainMaxHp(state, 10);
                break;
            case RunConstants.RelicMango:
                GainMaxHp(state, 14);
                break;
            // Both mushrooms are `HasUponPickupEffect`, and the Hungry For Mushrooms event
            // is only the door they come through: the HP swing is the RELIC's, so it lands
            // however the relic is obtained. The emulator had the numbers on the event
            // instead, and had them wrong -- +7 max HP and -9.
            // `LoomingFruit.AfterObtained`: MaxHpVar(31m), an Ancient relic. The
            // cornucopia its icon may or may not show is decided by the last byte of the
            // PROFILE's unique id and is purely cosmetic -- it changes `IconBaseName` and
            // nothing else, which is a joke about multiplayer rather than a mechanic.
            // `Byrdpip.AfterObtained`: every BYRDONIS EGG in the deck becomes a Byrd
            // Swoop. That is the relic's whole mechanic -- the pet it also summons is
            // 9999 HP with an invisible health bar and a `NOTHING_MOVE` state machine
            // that returns a completed task, an animation anchor for the card and nothing
            // the rules can see. Its `Skin` roll is off `new Rng(Owner, Id)`, a stream of
            // its own, so it spends none of the run's. The eggs are snapshotted, then
            // `CardCmd.TransformTo` runs once PER egg: it removes that egg and re-adds its
            // Swoop to the DECK with no index, through the deck-addition hooks, before the
            // next egg is touched. So each Swoop lands at the end, and each hook sees the
            // deck as it is between two transforms.
            case Effects.RelicEffects.Byrdpip:
                int eggs = state.Deck.Count(card => card.DefId == ByrdonisEggCard);
                for (int i = 0; i < eggs; i++)
                {
                    state.Deck.RemoveAt(
                        state.Deck.FindIndex(card => card.DefId == ByrdonisEggCard)
                    );
                    AddCardToDeck(state, new CardInstance(ByrdSwoopCard, Upgraded: false));
                }

                break;
            // `FakeMango.AfterObtained`: MaxHpVar(3m) against the real Mango's 14.
            case Effects.RelicEffects.FakeMango:
                GainMaxHp(state, 3);
                break;
            // `FakeLeesWaffle.AfterObtained`: heal `MaxHp * (HealVar(10) / 100)` -- a
            // PERCENTAGE, where the real waffle grants 7 flat max HP. Integer-truncated,
            // the way every decimal-to-int in the damage path is.
            case Effects.RelicEffects.FakeLeesWaffle:
                state.PlayerHp = Math.Min(
                    state.PlayerMaxHp,
                    state.PlayerHp + state.PlayerMaxHp * 10 / 100
                );
                break;
            case RunConstants.RelicLoomingFruit:
                GainMaxHp(state, 31);
                break;
            case RunConstants.RelicBigMushroom:
                // `MaxHpVar(20)`. GainMaxHp heals by the same amount, which the live
                // capture shows: 64/80 became 84/100.
                GainMaxHp(state, 20);
                break;
            case RunConstants.RelicFragrantMushroom:
                // `HpLossVar(15)` Unblockable | Unpowered, then `CardsVar(2)` upgradable
                // deck cards upgraded off `Rng.Niche` -- no type filter, unlike War Paint.
                DamagePlayer(state, 15);
                UpgradeRandomDeckCards(state, null, 2);
                break;
            case RunConstants.RelicLeesWaffle:
                GainMaxHp(state, 7);
                state.PlayerHp = state.PlayerMaxHp;
                break;
            case RunConstants.RelicOldCoin:
                GainGold(state, 300);
                break;
            case RunConstants.RelicGoldenCompass:
                // `AfterObtained` latches the act and then calls `GenerateMap()` outright:
                // the map the run is standing on is REPLACED, not the next one.
                state.GoldenPathAct = state.CurrentActIndex;
                RunMapGenerator.GenerateActMap(state);
                break;

            case RunConstants.RelicFurCoat:
                // `AfterObtained` latches the CURRENT act and marks its map at once. Seven
                // fights in this act only -- the coat does not follow the run upstairs.
                RunMapGenerator.MarkRoomsWhenFurCoatArrives(state);
                break;

            case RunConstants.RelicToyBox:
                // FOUR relics off the FRONT of the grab bag, offered on a screen the way
                // Small Capsule's one is. Each is marked WAX, and every third combat melts
                // one -- so this is four relics on loan, not four relics.
                state.PendingBonusRelicRewards.Clear();
                for (int i = 0; i < RunConstants.ToyBoxRelics; i++)
                {
                    int wax = RunRewardGenerator.NextRelic(state);
                    state.PendingBonusRelicRewards.Add(wax);
                    state.WaxRelics.Add(wax);
                }

                RunRewardGenerator.OfferNextBonusRelic(state);
                return RunFollowUp.BonusRelicRewards;
            case RunConstants.RelicSmallCapsule:
                // RewardsCmd.OfferCustom with a single RelicReward: the relic goes on a
                // SCREEN the player claims from, not straight into the run. Granting it
                // outright also ran its pickup effect a step early, so a capture
                // (`P14DQ9GNPW`) is on `rewards` holding two relics where the emulator
                // was back on the ancient holding three.
                state.PendingBonusRelicRewards.Clear();
                state.PendingBonusRelicRewards.Add(RunRewardGenerator.NextRelic(state));
                RunRewardGenerator.OfferNextBonusRelic(state);
                return RunFollowUp.BonusRelicRewards;
            case RunConstants.RelicLargeCapsule:
                ApplyRelicPickup(state, RunRewardGenerator.NextRelic(state));
                ApplyRelicPickup(state, RunRewardGenerator.NextRelic(state));
                // LargeCapsule selects Basic cards with Strike/Defend tags from the owner pool.
                foreach (bool strike in new[] { true, false })
                {
                    int basic = CharacterCatalog
                        .CardPool(state.Character)
                        .ToArray()
                        .First(id =>
                        {
                            var definition = GeneratedData.Cards.Get(id);
                            return definition.Rarity == CardRarity.Basic
                                && (strike ? definition.StrikeTag : definition.DefendTag);
                        });
                    AddCardToDeck(state, new CardInstance(basic, Upgraded: false));
                }
                break;
            case RunConstants.RelicPomander:
                // CardsVar(1) through CardSelectCmd.FromDeckForUpgrade: the PLAYER picks
                // which card is upgraded. UpgradeFirstCard upgraded the deck's first
                // upgradable card, which in a starting deck is a Strike -- and a live
                // capture (`RRRR6WR3C4`) shows the game opening a card_select screen at
                // step 1 where the emulator had already silently upgraded one.
                return BeginDeckSelection(state, DeckSelection.Upgrade, 0, count: 1)
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
            case RunConstants.RelicNeowsTalisman:
                // The LAST BASIC card tagged Strike and the last tagged Defend -- the real
                // tags, as Leafy Poultice's are. Ironclad's two ids found nothing for any
                // other character.
                UpgradeLastTaggedBasic(state, strike: true);
                UpgradeLastTaggedBasic(state, strike: false);
                break;
            case RunConstants.RelicCursedPearl:
                AddCardToDeck(state, new CardInstance(NamedCard("Greed"), Upgraded: false));
                GainGold(state, 333);
                break;
            case RunConstants.RelicDowsingRod:
                // Pinned v0.111.0 DowsingRod.AfterObtained adds the quest directly.
                AddCardToDeck(state, new CardInstance(NamedCard("Dowsing"), Upgraded: false));
                break;
            case RunConstants.RelicNeowsSacrifice:
                // NeowsSacrifice.AfterObtained: procure the fixed Ambergris first,
                // then add Guilty. Neither item is a random reward roll.
                RunRewardGenerator.AddPotion(state, NamedPotion("Ambergris"));
                AddCardToDeck(state, new CardInstance(NamedCard("Guilty"), Upgraded: false));
                break;
            case RunConstants.RelicHeftyTablet:
                // AfterObtained offers CardsVar(3) RARE cards from the owner's own pool --
                // Uniform odds, NoUpgradeRoll -- on a choose-a-card screen, and adds its
                // Injury together with whichever the player takes. It used to hand over
                // one card rolled off Rng.UpFront and drop the Injury immediately: no
                // screen, no choice, the wrong stream and the curse a decision early.
                RunRewardGenerator.SetOfferGrid(
                    state,
                    RunRewardGenerator
                        .CreateGridOffer(
                            state,
                            3,
                            RunRewardGenerator.PlayerCardPool(state),
                            CardRarity.Rare
                        )
                        .Cards,
                    picks: 1,
                    optional: true
                );
                state.PendingHeftyTabletCurse = true;
                return RunFollowUp.PreRolledCardReward;
            case RunConstants.RelicKaleidoscope:
                RunRewardGenerator.BeginKaleidoscopeRewards(state, returnTo);
                return RunFollowUp.CustomRewards;
            case RunConstants.RelicArcaneScroll:
                AddCardToDeck(
                    state,
                    new CardInstance(
                        state.PlayerRng.Rewards.NextItem(RareIroncladSingleplayerPool),
                        Upgraded: false
                    )
                );
                break;
            case RunConstants.RelicLeadPaperweight:
                // Two COLOURLESS cards on a choose-a-card screen, RegularEncounter odds
                // through CardCreationSource.Other. Same story as Hefty Tablet: one card
                // off the wrong stream, granted rather than offered.
                RunRewardGenerator.SetOfferGrid(
                    state,
                    RunRewardGenerator
                        .CreateGridOffer(
                            state,
                            2,
                            RunRewardGenerator.ColorlessRewardPool,
                            noUpgradeRoll: false
                        )
                        .Cards,
                    picks: 1,
                    optional: true
                );
                return RunFollowUp.PreRolledCardReward;
            case RunConstants.RelicPhialHolster:
                // PhialHolster.AfterObtained: GainMaxPotionCount(PotionSlots=1) FIRST --
                // so both of the potions it then rolls have somewhere to go -- and
                // CreateRandomPotionsOutOfCombat(Potions=2, Rng.CombatPotionGeneration).
                //
                // The stream is the whole point. These rolled off PlayerRng.Rewards, which
                // is the stream every card reward, shop and transformation in the run also
                // draws from: two draws the game never makes there put the very next
                // combat's gold reward and card offer at the wrong position, and every one
                // after it. A live capture of a run that took this from Neow paid 15 gold
                // where the emulator paid 9.
                // Both potions come from ONE batch, so the second never repeats the first.
                state.MaxPotionSlots++;
                foreach (
                    int potion in RunRewardGenerator.NextPotionsOutOfCombat(
                        state,
                        2,
                        state.Rng.CombatPotionGeneration
                    )
                )
                {
                    RunRewardGenerator.AddPotion(state, potion);
                }
                break;
            // ---- the act ancients' blessings -------------------------------------
            case RunConstants.RelicPaelsHorn:
                // Two Relax into the deck. The one ancient blessing a live capture pins:
                // `ACT2TEST01` takes it and comes away with two.
                AddCardToDeck(state, new CardInstance(NamedCard("Relax"), Upgraded: false));
                AddCardToDeck(state, new CardInstance(NamedCard("Relax"), Upgraded: false));
                break;
            case RunConstants.RelicStorybook:
                AddCardToDeck(
                    state,
                    new CardInstance(NamedCard("BrightestFlame"), Upgraded: false)
                );
                break;
            case RunConstants.RelicSandCastle:
                // CardsVar(6), StableShuffled on Rng.NICHE -- not the event's own stream
                // and not UpFront. Same shape as Doors of Light and Dark's light door,
                // which is worth knowing because that one taught the lesson twice: sort
                // by ModelId before shuffling, and only upgradable cards are candidates.
                UpgradeRandomCards(state, 6, state.Rng.Niche);
                break;
            case RunConstants.RelicAlchemicalCoffer:
                // PotionSlots(4): GainMaxPotionCount FIRST so all four potions have
                // somewhere to go, then four rolled off CombatPotionGeneration. Phial
                // Holster is the same shape, and the stream is the point -- rolling these
                // off PlayerRng.Rewards would move every card reward after it.
                state.MaxPotionSlots += 4;
                foreach (
                    int potion in RunRewardGenerator.NextPotionsOutOfCombat(
                        state,
                        4,
                        state.Rng.CombatPotionGeneration
                    )
                )
                {
                    RunRewardGenerator.AddPotion(state, potion);
                }

                break;
            case RunConstants.RelicPaelsClaw:
                // Goopy onto EVERY card in the deck that can take it — no screen, no
                // choice. `CanEnchant` is the filter, which for Goopy is the Defend tag.
                EnchantEveryCard(state, Enchantment.Goopy, 1);
                break;
            case RunConstants.RelicNutritiousSoup:
                // Tezcatara's Ember onto every BASIC Strike. The rarity and tag checks
                // are the relic's own, on top of the enchantment's CanEnchant.
                EnchantEveryCard(
                    state,
                    Enchantment.TezcatarasEmber,
                    1,
                    card => IsBasicStrike(card)
                );
                break;
            case RunConstants.RelicArchaicTooth:
            {
                // `GetTranscendenceStarterCard` is `Deck.Cards.FirstOrDefault(...)` -- the
                // FIRST match in deck order, not a choice and not a roll. The relic's
                // `GetTranscendenceTransformedCard` carries the starter's upgrade and
                // enchantment onto the replacement. `CardCmd.Transform` removes it and calls
                // `AddInternal(replacement)` with no index, so Break is APPENDED rather
                // than taking Bash's slot -- and it goes through the same
                // `ModifyCardBeingAddedToDeck`/`AfterCardChangedPiles` hooks as any other
                // card entering the deck, which is what `AddCardToDeck` models.
                int index = state.Deck.FindIndex(card =>
                    RunConstants.TranscendenceUpgrades.Any(pair => pair.Starter == card.DefId)
                );
                if (index >= 0)
                {
                    var starter = state.Deck[index];
                    int ancient = RunConstants
                        .TranscendenceUpgrades.First(pair => pair.Starter == starter.DefId)
                        .Ancient;
                    var transformed = new CardInstance(ancient, Upgraded: starter.Upgraded)
                    {
                        Enchantment = starter.Enchantment,
                        EnchantAmount = starter.EnchantAmount,
                    };
                    state.Deck.RemoveAt(index);
                    AddCardToDeck(state, transformed);
                }

                break;
            }

            case RunConstants.RelicTouchOfOrobas:
            {
                // `RelicCmd.Replace(starter, refined)` -- the starter relic is GONE, not
                // kept alongside. A starter with no entry in the table becomes a Circlet,
                // which is the game's fallback rather than a no-op.
                // `GetStarterRelic` is `Relics.FirstOrDefault(r => r.Rarity == Starter)`,
                // so it finds the relic by RARITY rather than by table membership -- which
                // is what makes the Circlet fallback reachable at all.
                int index = state.Relics.FindIndex(relic =>
                    GeneratedData.Relics.Get(relic.DefId).Rarity == RelicRarity.Starter
                );
                if (index >= 0)
                {
                    int starter = state.Relics[index].DefId;
                    var match = RunConstants.RefinementUpgrades.FirstOrDefault(pair =>
                        pair.Starter == starter
                    );
                    state.Relics[index] = new RelicInstance(
                        match.Starter == starter ? match.Upgraded : RunConstants.RelicCirclet
                    );
                }

                break;
            }

            case RunConstants.RelicTriBoomerang:
                // `FromDeckForEnchantment` with Instinct at 1, on three cards. Instinct
                // takes Attacks only, so a deck of nothing but Skills is offered nothing.
                return BeginDeckSelection(
                    state,
                    DeckSelection.Enchant,
                    (int)Enchantment.Instinct,
                    count: 3,
                    enchantAmount: 1
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;

            case RunConstants.RelicSereTalon:
            {
                // v0.111.0 SereTalon.AfterObtained: `CreatureCmd.LoseMaxHp(HpLossVar(9))`,
                // then Wishes(3) into the deck. No curses and no Niche draw.
                LoseMaxHp(state, 9);
                for (int i = 0; i < 3; i++)
                {
                    AddCardToDeck(state, new CardInstance(541, Upgraded: false));
                }

                break;
            }

            case RunConstants.RelicPumpkinCandle:
                // `AfterObtained` is `Rekindle()`, which is `KindleCount += 5`. The count
                // rides the relic's Counter because it is RUN state -- it survives the
                // fight that spends it.
                {
                    int index = state.Relics.FindIndex(relic =>
                        relic.DefId == RunConstants.RelicPumpkinCandle
                    );
                    if (index >= 0)
                    {
                        state.Relics[index] = state.Relics[index] with
                        {
                            Counter = state.Relics[index].Counter + 5,
                        };
                    }
                }

                break;

            case RunConstants.RelicDistinguishedCape:
                // `LoseMaxHp(9)` and then THREE Apparitions into the deck. No selection --
                // it takes the HP and hands over the cards, which is why it is the only one
                // of this batch that resolves in place.
                state.PlayerMaxHp = Math.Max(1, state.PlayerMaxHp - 9);
                state.PlayerHp = Math.Min(state.PlayerHp, state.PlayerMaxHp);
                for (int i = 0; i < 3; i++)
                {
                    AddCardToDeck(state, new CardInstance(17, Upgraded: false));
                }

                break;

            case RunConstants.RelicPreservedFog:
                // `FromDeckForRemoval` for THREE, and then a Folly whatever was removed.
                // The curse rides the follow-up rather than being added here, so it arrives
                // after the removals the way the game orders it.
                return BeginDeckSelection(
                    state,
                    DeckSelection.Remove,
                    0,
                    count: 3,
                    followUpCard: 10028,
                    followUpCount: 1
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;

            case RunConstants.RelicBeautifulBracelet:
                // `FromDeckForEnchantment` with Swift at THREE, on three eligible cards.
                // Beta Swift has no extra card-type restriction.
                return BeginDeckSelection(
                    state,
                    DeckSelection.Enchant,
                    (int)Enchantment.Swift,
                    count: 3,
                    enchantAmount: 3
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;

            case RunConstants.RelicClaws:
                // `FromDeckForTransformation` into Maul, SIX cards. The game preserves the
                // original's upgrade on the Maul when the Maul is upgradable; the emulator's
                // TransformTo does not carry that across yet, which is the one thing this
                // arm approximates.
                return BeginDeckSelection(state, DeckSelection.TransformToCarrying, 299, count: 6)
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;

            case RunConstants.RelicElectricShrymp:
                // FromDeckForEnchantment with Imbued: ONE card the player picks, and
                // Imbued takes skills only, so a deck with no skill left is offered
                // nothing.
                return BeginDeckSelection(
                    state,
                    DeckSelection.Enchant,
                    (int)Enchantment.Imbued,
                    count: 1
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
            case RunConstants.RelicPaelsGrowth:
                // Likewise, with Clone at amount FOUR rather than one.
                return BeginDeckSelection(
                    state,
                    DeckSelection.Enchant,
                    (int)Enchantment.Clone,
                    count: 1,
                    enchantAmount: 4
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
            case RunConstants.RelicSeaGlass:
                // Three direct CardFactory batches, Uniform + NoUpgradeRoll, followed
                // by one optional grid. Hooks run per batch, without IsCardReward.
                RunRewardGenerator.SetOfferGrid(
                    state,
                    new[] { CardRarity.Common, CardRarity.Uncommon, CardRarity.Rare }
                        .SelectMany(rarity =>
                            RunRewardGenerator
                                .CreateGridOffer(
                                    state,
                                    5,
                                    state.SeaGlassCharacter < 0
                                        ? RunRewardGenerator.PlayerCardPool(state)
                                        : RunConstants.OtherCharacterPoolFor(
                                            state.SeaGlassCharacter,
                                            state.Character
                                        ),
                                    rarity
                                )
                                .Cards
                        )
                        .ToArray(),
                    picks: 15,
                    optional: true
                );
                return RunFollowUp.PreRolledCardReward;
            case RunConstants.RelicGlassEye:
                // Original ForNonCombatWithUniformOdds includes NoUpgradeRoll.
                // Preserve the fixed-rarity filter and flags for Driftwood rerolls.
                state.PendingCardOffers.Clear();
                foreach (
                    CardRarity rarity in new[]
                    {
                        CardRarity.Common,
                        CardRarity.Common,
                        CardRarity.Uncommon,
                        CardRarity.Uncommon,
                        CardRarity.Rare,
                    }
                )
                {
                    state.PendingCardOffers.Add(
                        RunRewardGenerator.CreateCardRewardOffer(
                            state,
                            options: new CardRewardOptions(
                                RunConstants.NodeNormal,
                                EncounterSource: false,
                                IsFromCombat: false
                            )
                            {
                                FixedRarity = rarity,
                                NoUpgradeRoll = true,
                            }
                        )
                    );
                }

                return RunFollowUp.BonusCardOffers;
            case RunConstants.RelicPaelsTooth:
                // CardsVar(5) through FromDeckForRemoval with a filter of `IsUpgradable`
                // -- five cards the player picks, and only upgradable ones are offered.
                // The relic keeps copies of what it took for its own combat effect, which
                // the run layer does not need.
                return BeginDeckSelection(state, DeckSelection.RemoveUpgradable, 0, count: 5)
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
            case RunConstants.RelicYummyCookie:
                // CardsVar(4) through FromDeckForUpgrade: four cards the PLAYER picks.
                return BeginDeckSelection(state, DeckSelection.Upgrade, 0, count: 4)
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
            case RunConstants.RelicBiiigHug:
                // CardsVar(4) through FromDeckForRemoval.
                return BeginDeckSelection(state, DeckSelection.Remove, 0, count: 4)
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
            case RunConstants.RelicPreciseScissors:
                // CardsVar(1) through CardSelectCmd.FromDeckForRemoval -- the PLAYER picks
                // which card goes, and picking is the whole of the blessing.
                // RemoveLowestPriorityCard was the emulator choosing instead, and choosing
                // badly: its priority list starts with the curse placeholder, which is
                // Ascender's Bane, a card the game will not even offer for removal.
                return BeginDeckSelection(state, DeckSelection.Remove, 0, count: 1)
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
            case RunConstants.RelicScrollBoxes:
                // GenerateRandomBundles on PlayerRng.Rewards, then
                // CardSelectCmd.FromChooseABundleScreen: the player takes ONE bundle of
                // three whole, and never sees the other three cards again. This handed
                // over three cards rolled off Rng.UpFront -- the wrong stream, the wrong
                // count of draws, six cards' worth of choice collapsed into none, and no
                // screen at all.
                state.BundleOffer =
                [
                    .. RunRewardGenerator.GenerateScrollBoxBundles(state).SelectMany(b => b),
                ];
                state.SelectedBundle = -1;
                return RunFollowUp.BundleSelect;
            case RunConstants.RelicLeafyPoultice:
                state.PlayerMaxHp = Math.Max(1, state.PlayerMaxHp - 12);
                state.PlayerHp = Math.Min(state.PlayerHp, state.PlayerMaxHp);
                // The first BASIC card tagged Strike and the first tagged Defend -- the
                // real tags, not Ironclad's two ids, which found nothing for any other
                // character. Both transformed together off `PlayerRng.Transformations`.
                TransformFirstTaggedBasic(state, strike: true);
                TransformFirstTaggedBasic(state, strike: false);
                break;
            case RunConstants.RelicPrecariousShears:
                // CardsVar(2) through the same removal screen, then DamageVar(16). The
                // damage is owed only once the cards are gone, which BeginDeckSelection's
                // follow-up already models -- the same shape Luminous Choir uses.
                return BeginDeckSelection(
                    state,
                    DeckSelection.Remove,
                    0,
                    count: 2,
                    followUpHpLoss: 16
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
            case RunConstants.RelicSilkenTress:
                state.Gold = 0;
                break;
            case RunConstants.RelicPandorasBox:
                // `Cards.Where(c.IsBasicStrikeOrDefend && c.IsRemovable)`, transformed off
                // `Rng.Niche`. This matched Ironclad's two card IDS on the Transformations
                // stream -- wrong filter, wrong stream, and for any other character it
                // found nothing at all. The TAG is what the game reads, and Ascender's
                // Bane fails `IsRemovable` rather than the tag.
                PandorasBoxTransform(state);
                break;
            case RunConstants.RelicCallingBell:
                // A Curse of the Bell and THREE relics on a rewards SCREEN, one of each
                // rarity: `RelicReward(Common)`, `RelicReward(Uncommon)`, `RelicReward(Rare)`.
                // The emulator handed over three ROLLED pool relics directly -- no screen,
                // no rarities, and three draws off the wrong end of the queue.
                AddCardToDeck(
                    state,
                    new CardInstance(NamedCard("CurseOfTheBell"), Upgraded: false)
                );
                state.PendingBonusRelicRewards.Add(
                    RunRewardGenerator.NextShopRelicOfRarity(state, RelicRarity.Common)
                );
                state.PendingBonusRelicRewards.Add(
                    RunRewardGenerator.NextShopRelicOfRarity(state, RelicRarity.Uncommon)
                );
                state.PendingBonusRelicRewards.Add(
                    RunRewardGenerator.NextShopRelicOfRarity(state, RelicRarity.Rare)
                );
                return RunFollowUp.BonusRelicRewards;
            case RunConstants.RelicDustyTome:
                // An UPGRADED copy of the ANCIENT-rarity card the relic was set up with --
                // `SetupForPlayer` picks it off `PlayerRng.Rewards` from the character's
                // own pool, filtered to Ancient rarity minus Archaic Tooth's transcendence
                // cards. For the Ironclad that pool is Break and Corruption, and Break is
                // a transcendence card -- so it is always CORRUPTION, off one draw.
                //
                // The emulator added a random card off the whole reward pool on the UpFront
                // stream: wrong card, wrong pool, wrong stream.
                //
                // Darv sets the Tome up when it OFFERS it; taking it reuses that card
                // rather than drawing a second time.
                int tomeCard =
                    state.DustyTomeAncientCard >= 0
                        ? state.DustyTomeAncientCard
                        : DustyTomeCard(state);
                state.DustyTomeAncientCard = -1;
                AddCardToDeck(state, new CardInstance(tomeCard, Upgraded: true));
                break;
            case RunConstants.RelicNewLeaf:
                // CardsVar(1) through CardSelectCmd.FromDeckForTransformation, then
                // CardCmd.TransformToRandom on Rng.Niche. This used the older
                // TransformSelectedDeckIndex path, which answers the screen a step later
                // than the game does -- the `N11HWGCNUN` capture is back at the event on
                // step 3 with the card already transformed while the emulator is still
                // holding the selection open.
                return BeginDeckSelection(state, DeckSelection.TransformToRandom, 0, count: 1)
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
            case RunConstants.RelicAstrolabe:
                // THREE cards the player picks, each transformed off `Rng.Niche` and then
                // UPGRADED. The legacy path transformed the first three cards in the deck
                // and upgraded whatever landed last, which is neither a choice nor the
                // right cards.
                return BeginDeckSelection(
                    state,
                    DeckSelection.TransformToRandomUpgraded,
                    0,
                    count: 3,
                    returnTo: SelectionReturn.Map
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
            case RunConstants.RelicEmptyCage:
                // TWO cards the player picks, removed.
                return BeginDeckSelection(
                    state,
                    DeckSelection.Remove,
                    0,
                    count: 2,
                    returnTo: SelectionReturn.Map
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;

            // Orrery.AfterObtained awaits a separate RewardsSet of five populated offers.
            case Effects.RelicEffects.Orrery:
                RunRewardGenerator.BeginOrreryRewards(state, returnTo);
                return RunFollowUp.CustomRewards;

            // Cauldron.AfterObtained awaits a separate set, just like Orrery. Its
            // potion rows must not merge with the parent or lose a shop return.
            case Effects.RelicEffects.Cauldron:
                RunRewardGenerator.BeginCauldronRewards(state, returnTo);
                return RunFollowUp.CustomRewards;

            // The five shop relics whose AfterObtained raises a DECK-SELECTION screen.
            // All five go through the same machinery Empty Cage and the events use; what
            // differs is the kind, the count and the enchantment they apply.
            case Effects.RelicEffects.DollysMirror:
                return BeginDeckSelection(state, DeckSelection.Duplicate, 0, returnTo: returnTo)
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;

            case Effects.RelicEffects.GnarledHammer:
                // `CardSelectorPrefs(prompt, 0, 3)` -- up to THREE cards, and Sharp at 3
                // rather than the 2 Self-Help Book applies.
                return BeginDeckSelection(
                    state,
                    DeckSelection.Enchant,
                    (int)Enchantment.Sharp,
                    count: 3,
                    enchantAmount: 3,
                    returnTo: returnTo
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;

            case Effects.RelicEffects.Kifuda:
                return BeginDeckSelection(
                    state,
                    DeckSelection.Enchant,
                    (int)Enchantment.Adroit,
                    count: 3,
                    enchantAmount: 3,
                    returnTo: returnTo
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;

            case Effects.RelicEffects.PunchDagger:
                return BeginDeckSelection(
                    state,
                    DeckSelection.Enchant,
                    (int)Enchantment.Momentum,
                    enchantAmount: 5,
                    returnTo: returnTo
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;

            case Effects.RelicEffects.RoyalStamp:
                // The game shuffles the eligible cards on Rng.Niche before offering them,
                // which changes the ORDER of the screen and not what is on it. The
                // emulator offers the deck in deck order, so the shuffle is not modelled
                // and no stream draw is spent -- recorded rather than faked, because a
                // wrong draw on Niche desynchronises far more than a screen's ordering.
                return BeginDeckSelection(
                    state,
                    DeckSelection.Enchant,
                    (int)Enchantment.RoyallyApproved,
                    enchantAmount: 1,
                    returnTo: returnTo
                )
                    ? RunFollowUp.TransformSelect
                    : RunFollowUp.None;
        }

        return RunFollowUp.None;
    }

    /// <summary>
    /// <c>NeowsBones.GetValidRelics</c>, shuffled and cut to two.
    /// </summary>
    /// <remarks>
    /// The list is <c>Neow.AllPossibleOptions</c> in DECLARATION order -- curse options,
    /// then positive ones, then the six that are offered as one of a pair -- minus Neow's
    /// Bones itself and anything <c>IsAllowedAtNeow</c> refuses. Order is load-bearing:
    /// the shuffle that follows is over this exact sequence, so a list assembled any other
    /// way draws different relics from the same stream position. MassiveScroll is absent
    /// for the same reason it is absent from Neow's own offer -- its IsAllowed is
    /// <c>Players.Count > 1</c>.
    /// </remarks>
    private static List<int> NeowsBonesRelicOffer(RunState state)
    {
        var candidates = new List<int>();
        foreach (int relicId in RunConstants.NeowCurseOptions)
        {
            if (relicId != RunConstants.RelicNeowsBones)
            {
                candidates.Add(relicId);
            }
        }

        candidates.AddRange(RunConstants.NeowPositiveOptions);
        candidates.AddRange(RunConstants.NeowPairedOptions);
        state.PlayerRng.Rewards.Shuffle(candidates);
        return candidates.Take(2).ToList();
    }

    /// <summary>
    /// The curse Neow's Bones adds once its relics are claimed:
    /// <c>Rng.Niche.NextItem</c> over the generatable curses, ordered by id.
    /// </summary>
    public static void AddNeowsBonesCurse(RunState state)
    {
        if (!state.PendingNeowsBonesCurse)
        {
            return;
        }

        state.PendingNeowsBonesCurse = false;
        AddCardToDeck(state, new CardInstance(RollGeneratableCurse(state), Upgraded: false));
    }

    /// <summary>
    /// `ToyBox.AfterCombatEnd`: count the fight, and on every third one melt the first wax
    /// relic still held. `RelicCmd.Melt` takes it away -- the box lends four relics for
    /// twelve fights, it does not give four.
    /// </summary>
    public static void MeltAToyBoxRelic(RunState state)
    {
        int box = state.Relics.FindIndex(relic => relic.DefId == RunConstants.RelicToyBox);
        if (box < 0)
        {
            return;
        }

        int seen = state.Relics[box].Counter + 1;
        state.Relics[box] = state.Relics[box] with { Counter = seen };
        if (seen % RunConstants.ToyBoxCombats != 0)
        {
            return;
        }

        // `FirstOrDefault(r => r.IsWax && !r.IsMelted)` -- the first still HELD, which is
        // why a wax relic the player never claimed is skipped rather than counted.
        int melting = state.WaxRelics.FindIndex(id => state.Relics.Any(relic => relic.DefId == id));
        if (melting >= 0)
        {
            int relicId = state.WaxRelics[melting];
            state.WaxRelics.RemoveAt(melting);
            state.Relics.RemoveAll(relic => relic.DefId == relicId);
        }
    }

    /// <summary>
    /// `PaelsWing.OnSacrifice`: the card reward is given up instead of claimed, and every
    /// SECOND sacrifice pulls a relic from the FRONT of the grab bag -- the same end an
    /// elite reward and Shovel's dig pull from.
    /// </summary>
    public static void SacrificeACardReward(RunState state)
    {
        int index = state.Relics.FindIndex(relic => relic.DefId == RunConstants.RelicPaelsWing);
        if (index < 0)
        {
            return;
        }

        int sacrificed = state.Relics[index].Counter + 1;
        state.Relics[index] = state.Relics[index] with { Counter = sacrificed };
        if (sacrificed % RunConstants.PaelsWingSacrifices == 0)
        {
            ApplyRelicPickup(state, RunRewardGenerator.NextRelic(state));
        }
    }

    /// <summary>
    /// `PumpkinCandle.AfterCombatEnd`: <c>KindleCount = Math.Max(KindleCount - 1, 0)</c>.
    /// Fires whether or not the fight was won, so it sits with the other unconditional
    /// end-of-combat work rather than behind the victory check.
    /// </summary>
    public static void BurnPumpkinCandle(RunState state)
    {
        int index = state.Relics.FindIndex(relic => relic.DefId == RunConstants.RelicPumpkinCandle);
        if (index >= 0)
        {
            state.Relics[index] = state.Relics[index] with
            {
                Counter = Math.Max(state.Relics[index].Counter - 1, 0),
            };
        }
    }

    /// <summary>
    /// `CookRestSiteOption.IsEnabled`: two REMOVABLE cards in the deck. Removability is
    /// `!Keywords.Contains(Eternal)`, the same rule a plain removal screen uses.
    /// </summary>
    public static bool CanCook(RunState state) => state.Deck.Count(IsRemovable) >= 2;

    private static int StartingRelicCounter(int relicId)
    {
        // The three teas count DOWN the combats they have left, and that count is run
        // state: the relic is one object for the whole run, so it has to arrive charged
        // rather than be charged by the first fight it sees.
        return relicId switch
        {
            RunConstants.RelicSilverCrucible => 3,
            Effects.RelicEffects.BoneTea => Effects.RelicEffects.BoneTeaCombats,
            Effects.RelicEffects.EmberTea => Effects.RelicEffects.EmberTeaCombats,
            Effects.RelicEffects.TeaOfDiscourtesy => Effects.RelicEffects.TeaOfDiscourtesyCombats,
            _ => 0,
        };
    }

    /// <summary>
    /// Put the run into <paramref name="eventId"/> the way the game does.
    /// </summary>
    /// <remarks>
    /// Entering an event is not just setting its id: the game calls the event's
    /// <c>CalculateVars</c> as it generates the options, and several events DRAW from
    /// their own Rng stream there. Skip it and every later draw in that event lands one
    /// position early -- which is not a visible failure, it is a different outcome. The
    /// Endless Conveyor rolls its dish in CalculateVars and nothing else touches it, so
    /// Observe the Chef upgraded the wrong card against a live capture.
    /// </remarks>
    public static void BeginEvent(RunState state, int eventId)
    {
        state.EventValue0 = null;
        state.EventValue1 = null;
        state.EventAmount0 = null;
        state.EventAmount1 = null;
        state.SlipperyBridgeSkippedRemovals.Clear();
        state.EventPage = 0;
        state.EventRandomOffer = [];
        state.CrystalSphere = null;
        state.CrystalSphereRng = null;
        state.EventRngStream = null;
        state.EventRngName = null;
        state.EventRelicStock = null;
        state.FakeMerchantPrices = null;
        state.EventRelicOffers = null;
        state.EventId = eventId;
        BuildEventCombatLayout(state, eventId);
        CalculateEventVars(state);
        // The custom merchant has no initial EventOption; its inventory is the room UI.
        state.Phase =
            eventId == RunConstants.EventFakeMerchant ? RunPhase.FakeMerchant : RunPhase.Event;
    }

    /// <summary>
    /// `EventSynchronizer.GenerateInternalCombatStateIfNecessary` on room entry: an event
    /// whose `LayoutType` is `Combat` builds its `CanonicalEncounter`'s creatures at once,
    /// and each `CombatState.CreateCreature` draws its HP from `Rng.Niche` -- even when the
    /// player never takes the fight (Nab, Return The Key). The HPs are kept so the event's
    /// own fight, which reuses `CombatStateForLayout`, does not draw them again.
    /// </summary>
    private static void BuildEventCombatLayout(RunState state, int eventId)
    {
        state.EventLayoutEncounterId = 0;
        state.EventLayoutEnemyHps.Clear();
        int encounterId = eventId switch
        {
            RunConstants.EventPunchOff => RunConstants.PunchOffEncounterId,
            RunConstants.EventTheLanternKey => RunConstants.MysteriousKnightEncounterId,
            _ => 0,
        };
        if (encounterId == 0)
        {
            return;
        }

        var niche = new CountingRandom(state.Rng.Niche);
        state.EventLayoutEnemyHps.AddRange(
            CombatFactory.RollLayoutEnemyHps(encounterId, state.AscensionLevel, niche, null)
        );
        state.Rng.Niche.Restore(niche.Snapshot());
        state.EventLayoutEncounterId = encounterId;
    }

    public static void EnterEvent(RunState state)
    {
        state.EventValue0 = null;
        state.EventValue1 = null;
        state.EventAmount0 = null;
        state.EventAmount1 = null;
        state.SlipperyBridgeSkippedRemovals.Clear();
        state.EventPage = 0;
        state.EventRandomOffer = [];
        state.CrystalSphere = null;
        state.CrystalSphereRng = null;
        state.EventRngStream = null;
        state.EventRngName = null;
        state.EventRelicStock = null;
        state.FakeMerchantPrices = null;
        state.EventRelicOffers = null;
        if (
            state.EventSequenceIndex == 0
            && state.Relics.Any(relic => relic.DefId == RunConstants.RelicNewLeaf)
            && IsEventAllowed(state, RunConstants.EventTheLegendsWereTrue)
        )
        {
            BeginEvent(state, RunConstants.EventTheLegendsWereTrue);
            return;
        }

        // RoomSet.EnsureNextEventIsValid: NextEvent is events[visited % count], and the
        // cursor is walked forward while the candidate is disallowed OR already seen.
        // The sequence WRAPS -- it is a ring, not a list that runs out -- and the game
        // only gives up after a full lap, logging "all unique events exhausted".
        List<int> eventPool = [];
        if (state.EventSequence.Length > 0)
        {
            for (int i = 0; i < state.EventSequence.Length; i++)
            {
                int eventId = state.EventSequence[
                    state.EventSequenceIndex % state.EventSequence.Length
                ];
                if (IsEventAllowed(state, eventId) && !state.VisitedEventIds.Contains(eventId))
                {
                    // PullNextEvent records the MODIFIED event (Lantern Key's Repy, not
                    // the slot it replaced); MarkRoomVisited moves the cursor past it.
                    int pulled = ModifyNextEvent(state, eventId);
                    state.VisitedEventIds.Add(pulled);
                    state.EventSequenceIndex++;
                    BeginEvent(state, pulled);
                    return;
                }

                state.EventSequenceIndex++;
            }
        }

        eventPool.Add(RunConstants.EventJungleMazeAdventure);
        eventPool.Add(RunConstants.EventBrainLeech);
        eventPool.Add(RunConstants.EventCrystalSphere);
        eventPool.Add(RunConstants.EventDollRoom);
        eventPool.Add(RunConstants.EventFakeMerchant);
        eventPool.Add(RunConstants.EventPotionCourier);
        eventPool.Add(RunConstants.EventRanwidTheElder);
        eventPool.Add(RunConstants.EventRelicTrader);
        eventPool.Add(RunConstants.EventRoomFullOfCheese);
        eventPool.Add(RunConstants.EventSlipperyBridge);
        eventPool.Add(RunConstants.EventStoneOfAllTime);
        eventPool.Add(RunConstants.EventSymbiote);
        eventPool.Add(RunConstants.EventTeaMaster);
        eventPool.Add(RunConstants.EventTheFutureOfPotions);
        eventPool.Add(RunConstants.EventThisOrThat);
        eventPool.Add(RunConstants.EventWarHistorianRepy);
        eventPool.Add(RunConstants.EventWelcomeToWongos);
        eventPool.Add(RunConstants.EventDoorsOfLightAndDark);
        eventPool.Add(RunConstants.EventSunkenTreasury);
        eventPool.Add(RunConstants.EventSelfHelpBook);
        if (state.Act == RunConstants.ActOvergrowth)
        {
            eventPool.Add(RunConstants.EventByrdonisNest);
            eventPool.Add(RunConstants.EventDenseVegetation);
            eventPool.Add(RunConstants.EventLuminousChoir);
            eventPool.Add(RunConstants.EventSapphireSeed);
            eventPool.Add(RunConstants.EventSunkenStatue);
            eventPool.Add(RunConstants.EventTabletOfTruth);
            eventPool.Add(RunConstants.EventWellspring);
            eventPool.Add(RunConstants.EventWhisperingHollow);
            eventPool.Add(RunConstants.EventWoodCarvings);
        }
        else
        {
            eventPool.Add(RunConstants.EventAbyssalBaths);
            eventPool.Add(RunConstants.EventDrowningBeacon);
            eventPool.Add(RunConstants.EventEndlessConveyor);
            eventPool.Add(RunConstants.EventPunchOff);
            eventPool.Add(RunConstants.EventSpiralingWhirlpool);
            eventPool.Add(RunConstants.EventSunkenStatue);
            eventPool.Add(RunConstants.EventTrashHeap);
            eventPool.Add(RunConstants.EventWaterloggedScriptorium);
        }
        if (
            state.PlayerHp >= 10
            && state.Deck.Any(card => card.DefId != RunConstants.SpoilsMapCard)
        )
        {
            eventPool.Add(RunConstants.EventTheLegendsWereTrue);
        }

        if (state.Gold >= 100 && state.Deck.Count >= 2)
        {
            eventPool.Add(RunConstants.EventMorphicGrove);
        }

        // UnrestSite.IsAllowed compares in decimal: in double, 90 * 0.7 is 62.999...
        if (state.PlayerHp <= state.PlayerMaxHp * 0.70m)
        {
            eventPool.Add(RunConstants.EventUnrestSite);
            eventPool.Add(RunConstants.EventAromaOfChaos);
            eventPool.Add(RunConstants.EventSimpleReward);
        }
        else
        {
            eventPool.Add(RunConstants.EventAromaOfChaos);
            eventPool.Add(RunConstants.EventSimpleReward);
        }

        // The fallback pool is hand-written and had no eligibility check of any kind, so
        // it could hand out an event the run is not entitled to -- including the act-2
        // ones it lists by name. Everything the sequence is filtered by applies here too.
        var allowed = eventPool.Where(id => IsEventAllowed(state, id)).ToList();
        BeginEvent(
            state,
            ModifyNextEvent(
                state,
                state.Rng.UpFront.NextItem(
                    allowed.Count > 0 ? allowed : [RunConstants.EventSimpleReward]
                )
            )
        );
    }

    /// <summary>
    /// Events whose <c>IsAllowed</c> reads <c>CurrentActIndex</c> and refuses index 0.
    /// The emulator models Act 1 and only Act 1 -- a run ends at its boss -- so every one
    /// of these is unreachable, and rolling one puts a room in front of an agent that the
    /// game would never have shown it.
    ///
    /// Eight of the nine were not gated at all. The ninth, the Crystal Sphere, was gated
    /// on <c>state.Act > ActOvergrowth</c> -- but Act is which of the two Act-1 acts the
    /// run drew (Overgrowth 1, Underdocks 2), not an act INDEX, so the test was true for
    /// every Underdocks run and let the sphere into half of Act 1.
    /// </summary>
    private static readonly int[] ActTwoAndLaterEvents =
    [
        RunConstants.EventCrystalSphere,
        RunConstants.EventDollRoom,
        RunConstants.EventFakeMerchant,
        RunConstants.EventPotionCourier,
        RunConstants.EventRanwidTheElder,
        RunConstants.EventRelicTrader,
        RunConstants.EventStoneOfAllTime,
        RunConstants.EventSymbiote,
        RunConstants.EventWelcomeToWongos,
    ];

    /// <summary>
    /// The four events whose pool is a LATER ACT's -- Hive for the first two, Glory for
    /// the other two -- rather than one of Act 1's. The emulator models a run's first
    /// act, so no pool it draws from contains them.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="ActTwoAndLaterEvents"/>, which is a list of
    /// <c>IsAllowed</c> transcriptions, because this is a different rule: these four
    /// declare no act gate of their own at all. Conflating the two put "not in this act's
    /// pool" inside the run predicate, where it read as though the game refuses these
    /// events to a healthy Act 1 player -- it does not; it simply never offers them.
    /// Their real gates are transcribed in <see cref="IsEventAllowedForRun"/> below, so
    /// adding a later act's pool gets a working event rather than a refused one.
    /// </remarks>
    private static readonly int[] LaterActPoolEvents =
    [
        RunConstants.EventColorfulPhilosophers,
        RunConstants.EventColossalFlower,
        RunConstants.EventGraveOfTheForgotten,
        RunConstants.EventRoundTeaParty,
    ];

    /// <summary>Test seam for <see cref="IsEventAllowed"/>.</summary>
    public static bool IsEventAllowedForTests(RunState state, int eventId) =>
        IsEventAllowed(state, eventId);

    internal static bool IsEventAllowed(RunState state, int eventId)
    {
        if (
            state.CurrentActIndex == 0
            && (ActTwoAndLaterEvents.Contains(eventId) || LaterActPoolEvents.Contains(eventId))
        )
        {
            return false;
        }

        // These shared events have an upper act limit too. Source IsAllowed
        // uses CurrentActIndex, not the region identifier (Overgrowth/Hive/etc.).
        if (
            state.CurrentActIndex != 1
            && eventId
                is RunConstants.EventDollRoom
                    or RunConstants.EventWelcomeToWongos
                    or RunConstants.EventStoneOfAllTime
        )
            return false;
        if (
            state.CurrentActIndex >= 2
            && eventId
                is RunConstants.EventTeaMaster
                    or RunConstants.EventBrainLeech
                    or RunConstants.EventRoomFullOfCheese
        )
            return false;
        if (state.CurrentActIndex != 0 && eventId == RunConstants.EventTheLegendsWereTrue)
            return false;

        return IsEventAllowedForRun(state, eventId);
    }

    /// <summary>
    /// <c>RunState.TotalFloor</c> as an event's <c>IsAllowed</c> sees it. It is the length
    /// of <c>MapPointHistory</c>, and <c>RunManager.EnterMapPointInternal</c> pulls the event
    /// in <c>CreateRoom</c> BEFORE <c>AppendToMapPointHistory</c> records the room being
    /// entered. <see cref="RunState.Floor"/> already counts that room, so the gate reads one
    /// less -- the seventh room of a run is TotalFloor 6 to Punch Off and Slippery Bridge.
    /// </summary>
    private static int EventPullTotalFloor(RunState state) => state.Floor - 1;

    /// <summary>
    /// Each event's own <c>IsAllowed(IRunState)</c>, transcribed -- whether THIS RUN may
    /// be shown the event, which is a separate question from whether the act's pool
    /// contains it. Public because the events an Act 1 pool never offers still have gates
    /// worth pinning.
    /// </summary>
    public static bool IsEventAllowedForRun(RunState state, int eventId)
    {
        return eventId switch
        {
            RunConstants.EventMorphicGrove => state.Gold >= 100 && state.Deck.Count >= 2,
            // IsAllowed runs before BeginEvent.CalculateVars discounts the price.
            RunConstants.EventLuminousChoir => state.Gold >= 149
                && state.RelicBag.HasAvailableRelics(RelicGrabBag.AllowedInSoloRun(state.Floor)),
            RunConstants.EventWoodCarvings => state.Deck.Any(card =>
                GeneratedData.Cards.Get(card.DefId).Rarity == CardRarity.Basic
            ),
            RunConstants.EventDrowningBeacon => state.PlayerHp > 13,
            // Gold >= 120, not 40 -- the belt is a rich man's event.
            RunConstants.EventEndlessConveyor => state.Gold >= 120,
            // Gold >= 55 and nothing else. The `|| Deck.Count > 0` made this always true,
            // because a run always has a deck.
            RunConstants.EventWaterloggedScriptorium => state.Gold >= 55,
            RunConstants.EventWhisperingHollow => state.Gold >= 44,
            RunConstants.EventTrashHeap => state.PlayerHp > 5,
            RunConstants.EventSpiralingWhirlpool => state.Deck.Any(card =>
                Enchantments.CanEnchant(card, Enchantment.Spiral)
            ),
            RunConstants.EventPunchOff => EventPullTotalFloor(state) >= 6,
            RunConstants.EventSlipperyBridge => EventPullTotalFloor(state) > 6
                && state.Deck.Any(IsRemovable),
            // WarHistorianRepy.IsAllowed is `return false` outright: it is never drawn
            // from the sequence, only reached by its own means. Falling through to the
            // default handed it out as an ordinary event.
            RunConstants.EventWarHistorianRepy => false,
            // SelfHelpBook declares no IsAllowed; a deck with nothing to enchant gets its
            // NO_OPTIONS page (SkipBook), not a skipped event.
            RunConstants.EventSelfHelpBook => true,
            RunConstants.EventTheLegendsWereTrue => state.PlayerHp >= 10
                && state.Deck.Any(card => card.DefId != RunConstants.SpoilsMapCard),
            RunConstants.EventCrystalSphere => state.Gold >= 100,
            RunConstants.EventRanwidTheElder => state.Gold >= 100
                && state.PotionSlots.Any(potion => potion != 0)
                && state.Relics.Any(relic => IsTradableRelic(state, relic)),
            RunConstants.EventRelicTrader => state.Relics.Count(relic =>
                IsTradableRelic(state, relic)
            ) >= 5,
            // StoneOfAllTime.IsAllowed requires a potion. Enchantable cards
            // only enable Push once the event has already been admitted.
            RunConstants.EventStoneOfAllTime => state.PotionSlots.Any(potion => potion != 0),
            RunConstants.EventTeaMaster => state.Gold >= 150,
            RunConstants.EventTheFutureOfPotions => state.PotionSlots.Count(potion => potion != 0)
                >= 2,
            RunConstants.EventWelcomeToWongos => state.Gold >= 100,
            RunConstants.EventAmalgamator => state.Deck.Count >= 2,
            RunConstants.EventFieldOfManSizedHoles => state.Deck.Count > 0,
            RunConstants.EventInfestedAutomaton => state.Deck.Count > 0,
            RunConstants.EventSpiritGrafter => state.Deck.Count > 0,
            // Reflections and TinkerTime declare no IsAllowed at all: both were gated on
            // a non-empty deck, which is a rule neither model has. The deck-size gates
            // came in with placeholder options that no longer stand.
            RunConstants.EventReflections => true,
            RunConstants.EventTinkerTime => true,
            // The weaver only turns up for a run that can afford EmotionalAwarenessCost:
            // `Players.All(p => p.Gold >= 125)`. The upgradable-or-hurt test belonged to
            // the placeholder options.
            RunConstants.EventZenWeaver => state.Gold >= RunConstants.ZenWeaverEmotionalCost,
            // The site only turns up on a run that is actually hurt: CurrentHp <= 70% of
            // max. It was grouped with the unconditional events.
            RunConstants.EventUnrestSite => state.PlayerHp <= state.PlayerMaxHp * 0.70m,
            // ByrdonisNest is `!HasEventPet()`. Pets are not modelled, and the sequence
            // offers each event at most once per act, so within an act it cannot matter.
            // BrainLeech and RoomFullOfCheese are `CurrentActIndex < 2`, and Dense
            // Vegetation only refuses in multiplayer. The emulator models a run's first
            // act, single player, so all three are open -- transcribed rather than left
            // to the default so the gate says so.
            RunConstants.EventBrainLeech
            or RunConstants.EventRoomFullOfCheese
            or RunConstants.EventDenseVegetation
            or RunConstants.EventByrdonisNest
            or RunConstants.EventAromaOfChaos
            or RunConstants.EventSimpleReward => true,
            // Colossal Flower's ladder costs 5 + 6 + 7 = 18 unblockable to walk to the
            // bottom of, and its gate is `CurrentHp >= 19` -- one more than the whole
            // climb, so the event can never be the thing that kills you.
            RunConstants.EventColossalFlower => state.PlayerHp >= 19,
            // The tea party's fight is 11 unblockable and its gate is `CurrentHp >= 12`,
            // the same one-more-than-the-damage rule.
            RunConstants.EventRoundTeaParty => state.PlayerHp >= 12,
            // `All(HasEnchantableCards)`, where enchantable means Souls Power will take
            // it -- so a deck with nothing that exhausts never sees the grave at all.
            // Note this is the WHOLE event's gate, not just Confront's lock: the game
            // refuses to show it rather than showing it with one option greyed out.
            RunConstants.EventGraveOfTheForgotten => state.Deck.Any(card =>
                Enchantments.CanEnchant(card, Enchantment.SoulsPower)
            ),
            // `All(p => p.UnlockState.CharacterCardPools.Count() > 1)` -- a profile that
            // has unlocked a second character. The emulator runs a fixed profile with all
            // five pools, so this is true. See the fixed-profile note in HANDOFF.md.
            RunConstants.EventColorfulPhilosophers => true,
            // Act index aside, the stall wants a customer: `Gold >= 100 || holds a Foul
            // Potion`. The potion half is the interesting one -- it is the price of the
            // ROBBERY, not of a purchase, and a broke player carrying one still qualifies.
            RunConstants.EventFakeMerchant => state.Gold >= 100
                || state.PotionSlots.Contains(NamedPotion("FoulPotion")),
            _ => true,
        };
    }

    /// <summary>
    /// The book offers Sharp to Attacks, Nimble to Skills and Swift to Powers, so a card
    /// is a candidate for SOME page of it when its own type's enchantment would take.
    /// </summary>
    public static bool CardCanReceiveSelfHelpBookEnchantment(CardInstance card) =>
        Enchantments.CanEnchant(card, Enchantment.Sharp)
        || Enchantments.CanEnchant(card, Enchantment.Nimble)
        || Enchantments.CanEnchant(card, Enchantment.Swift);

    public static Enchantment SelfHelpBookEnchantment(int enchantType) =>
        enchantType switch
        {
            0 => Enchantment.Sharp,
            1 => Enchantment.Nimble,
            2 => Enchantment.Swift,
            _ => Enchantment.None,
        };

    internal const string SelfHelpBookSelectionOrigin = "SELF_HELP_BOOK";

    /// <summary>The book adds a page-specific type restriction to CanEnchant.</summary>
    private static bool CanEnchantForSelfHelpBook(CardInstance card, Enchantment enchantment)
    {
        CardType? requiredType = enchantment switch
        {
            Enchantment.Sharp => CardType.Attack,
            Enchantment.Nimble => CardType.Skill,
            Enchantment.Swift => CardType.Power,
            _ => null,
        };
        // MadScience.Type is its TinkerTimeType, not the generated Attack default.
        CardType actualType = card.ModelType();
        return requiredType.HasValue
            && actualType == requiredType.Value
            && Enchantments.CanEnchant(card, enchantment);
    }

    /// <summary>
    /// Open a deck selection: the run moves to the card-select screen and stays there
    /// until <paramref name="count"/> cards have been chosen. Refuses -- and leaves the
    /// run where it was -- when no card in the deck is eligible, which is what an event
    /// with a locked option is telling the player.
    /// </summary>
    /// <summary>
    /// Whether <see cref="BeginDeckSelection"/> would open rather than refuse. The action
    /// mask has to ask the same question the step will, or the two disagree about an
    /// option and the agent is offered a move it cannot make -- which is the single most
    /// common defect this layer has had.
    /// </summary>
    public static bool CanBeginDeckSelection(
        RunState state,
        DeckSelection kind,
        int arg,
        string? eventEntry = null
    )
    {
        var savedKind = state.PendingSelectionKind;
        int savedArg = state.PendingSelectionArg;
        string? savedEventEntry = state.PendingSelectionEventEntry;
        state.PendingSelectionKind = kind;
        state.PendingSelectionArg = arg;
        state.PendingSelectionEventEntry = eventEntry;
        bool any = Enumerable.Range(0, state.Deck.Count).Any(i => CanSelectCard(state, i));
        state.PendingSelectionKind = savedKind;
        state.PendingSelectionArg = savedArg;
        state.PendingSelectionEventEntry = savedEventEntry;
        return any;
    }

    public static bool BeginDeckSelection(
        RunState state,
        DeckSelection kind,
        int arg,
        int count = 1,
        int followUpCard = 0,
        int followUpCount = 0,
        int followUpHpLoss = 0,
        int followUpMaxHpGain = 0,
        string? eventEntry = null,
        int enchantAmount = 0,
        SelectionReturn returnTo = SelectionReturn.EventResult,
        int followUpGoldLoss = 0
    )
    {
        state.PendingSelectionEventEntry = eventEntry;
        state.PendingSelectionKind = kind;
        state.PendingSelectionArg = arg;
        state.PendingSelectionCount = count;
        state.PendingSelectionFollowUpCard = followUpCard;
        state.PendingSelectionFollowUpCount = followUpCount;
        state.PendingSelectionFollowUpHpLoss = followUpHpLoss;
        state.PendingSelectionFollowUpMaxHpGain = followUpMaxHpGain;
        state.PendingSelectionFollowUpGoldLoss = followUpGoldLoss;
        state.PendingSelectionEnchantAmount = enchantAmount;
        state.PendingSelectionReturn = returnTo;
        if (!Enumerable.Range(0, state.Deck.Count).Any(i => CanSelectCard(state, i)))
        {
            ClearDeckSelection(state);
            return false;
        }

        state.Phase = RunPhase.TransformSelect;
        return true;
    }

    public static void ClearDeckSelection(RunState state)
    {
        state.PendingShopRemovalCost = null;
        state.PendingSelectionEventEntry = null;
        state.PendingSelectionKind = DeckSelection.None;
        state.PendingSelectionArg = 0;
        state.PendingSelectionCount = 0;
        state.PendingSelectionFollowUpCard = 0;
        state.PendingSelectionFollowUpCount = 0;
        state.PendingSelectionFollowUpHpLoss = 0;
        state.PendingSelectionFollowUpGoldLoss = 0;
        state.PendingSelectionFollowUpMaxHpGain = 0;
        state.PendingSelectionEnchantAmount = 0;
        state.PendingSelectionReturn = SelectionReturn.EventResult;
        state.PendingSelectionReturnsToEvent = false;
    }

    /// <summary>
    /// Pay out whatever the event owes once the selection is done -- the curse Luminous
    /// Choir and the Wellspring hand over in exchange for the removal.
    /// </summary>
    public static void ResolveDeckSelectionFollowUp(RunState state)
    {
        for (int i = 0; i < state.PendingSelectionFollowUpCount; i++)
        {
            AddCardToDeck(
                state,
                new CardInstance(state.PendingSelectionFollowUpCard, Upgraded: false)
            );
        }

        DamagePlayer(state, state.PendingSelectionFollowUpHpLoss);
        // `PlayerCmd.LoseGold(cost, GoldLossType.Spent)` notifies nothing; it only spends.
        state.Gold = Math.Max(0, state.Gold - state.PendingSelectionFollowUpGoldLoss);

        // `CreatureCmd.GainMaxHp` HEALS by the same amount it raises the maximum, which is
        // why the cook is worth taking at full HP as well as hurt.
        if (state.PendingSelectionFollowUpMaxHpGain > 0)
        {
            state.PlayerMaxHp += state.PendingSelectionFollowUpMaxHpGain;
            state.PlayerHp = Math.Min(
                state.PlayerMaxHp,
                state.PlayerHp + state.PendingSelectionFollowUpMaxHpGain
            );
        }
    }

    /// <summary>Whether the pending selection would take the card at this deck index.</summary>
    public static bool CanSelectCard(RunState state, int deckIndex)
    {
        if ((uint)deckIndex >= (uint)state.Deck.Count)
        {
            return false;
        }

        var card = state.Deck[deckIndex];
        return state.PendingSelectionKind switch
        {
            DeckSelection.Enchant => state.PendingSelectionEventEntry == SelfHelpBookSelectionOrigin
                ? CanEnchantForSelfHelpBook(card, (Enchantment)state.PendingSelectionArg)
                : Enchantments.CanEnchant(card, (Enchantment)state.PendingSelectionArg),
            // CardSelectCmd.FromDeckGeneric(c => c.IsTransformable && c.Rarity == Basic)
            // -- Wood Carvings carves a Basic card into a Peck or a Toric Toughness.
            DeckSelection.TransformTo => GeneratedData.Cards.Get(card.DefId).Rarity
                == CardRarity.Basic,
            // Claws passes no predicate, so every transformable card is on the table.
            DeckSelection.TransformToCarrying => true,
            DeckSelection.Upgrade => RunConstants.IsRunCardUpgradable(card),
            // Dolly's Mirror excludes only Quest cards; a curse is a legal copy.
            DeckSelection.Duplicate => GeneratedData.Cards.Get(card.DefId).Type != CardType.Quest,
            // `FromDeckForRemoval` filters on `c.IsRemovable && filter(c)`, so the Eternal
            // check applies to this one too.
            DeckSelection.RemoveUpgradable => RunConstants.IsRunCardUpgradable(card)
                && IsRemovable(card),
            // `CardSelectCmd.FromDeckForRemoval` filters on `c.IsRemovable`, which is
            // `!Keywords.Contains(CardKeyword.Eternal)` -- so the game will not so much as
            // OFFER an Eternal card for removal. Seven curses carry it, Ascender's Bane
            // among them, and the emulator let a run delete every one.
            DeckSelection.Remove => IsRemovable(card),
            DeckSelection.RemoveTaggedBasic => IsRemovableTaggedBasic(
                card,
                state.PendingSelectionArg
            ),
            DeckSelection.TransformToRandom => true,
            DeckSelection.TransformToRandomUpgraded => true,
            _ => false,
        };
    }

    /// <summary>
    /// Amalgamator's `IsValid(tag, card)`: the tag, BASIC rarity, and removable.
    /// </summary>
    /// <param name="tag">0 for `CardTag.Strike`, 1 for `CardTag.Defend`.</param>
    private static bool IsRemovableTaggedBasic(CardInstance card, int tag)
    {
        var def = GeneratedData.Cards.Get(card.DefId);
        return (tag == 0 ? def.StrikeTag : def.DefendTag)
            && def.Rarity == CardRarity.Basic
            && IsRemovable(card);
    }

    /// <summary>
    /// Take one card for the pending selection. Returns false -- changing nothing -- when
    /// the index is out of range or the card is not one the selection would take.
    /// </summary>
    public static bool ApplyDeckSelection(RunState state, int deckIndex)
    {
        if (!CanSelectCard(state, deckIndex))
        {
            return false;
        }

        var card = state.Deck[deckIndex];
        switch (state.PendingSelectionKind)
        {
            case DeckSelection.Enchant:
                state.Deck[deckIndex] = card with
                {
                    Enchantment = (Enchantment)state.PendingSelectionArg,
                    // Self-Help Book applies at 2 (its Enchantment*Amount vars); every
                    // event enchantment is CardCmd.Enchant<T>(card, 1m).
                    EnchantAmount =
                        state.PendingSelectionEnchantAmount > 0
                            ? state.PendingSelectionEnchantAmount
                            : SelfHelpBookAmount((Enchantment)state.PendingSelectionArg),
                };
                break;
            case DeckSelection.Duplicate:
                AddCardToDeck(state, card);
                break;
            case DeckSelection.TransformTo:
                // CardCmd.Transform appends replacements in Deck; only combat piles
                // insert at the old index. This is also the ordering used by random transforms.
                state.Deck.RemoveAt(deckIndex);
                AddCardToDeck(state, new CardInstance(state.PendingSelectionArg, Upgraded: false));
                break;
            case DeckSelection.TransformToCarrying:
            {
                // `CreateMaulFromOriginal`: the upgrade rides across when the NEW card is
                // upgradable, and the enchantment when it can take on the new card. Both
                // are the new card's own rules, not the old one's -- transforming an
                // upgraded Strike into something unupgradable loses the upgrade.
                var made = new CardInstance(state.PendingSelectionArg, Upgraded: false);
                if (card.Upgraded && RunConstants.IsRunCardUpgradable(made))
                {
                    made = made with { Upgraded = true };
                }

                if (
                    card.Enchantment != Enchantment.None
                    && Enchantments.CanEnchant(made, card.Enchantment)
                )
                {
                    made = made with
                    {
                        Enchantment = card.Enchantment,
                        EnchantAmount = card.EnchantAmount,
                    };
                }

                state.Deck.RemoveAt(deckIndex);
                AddCardToDeck(state, made);
                break;
            }
            case DeckSelection.Upgrade:
                state.Deck[deckIndex] = card with { Upgraded = true };
                break;
            case DeckSelection.TransformToRandom:
                // Every event hands CardCmd.TransformToRandom its OWN base.Rng; only
                // NewLeaf, which does not come through here, uses Rng.Niche. Rolling the
                // new card off Niche gave the event a card from a stream the game never
                // touched for it.
                TransformCardAt(
                    state,
                    deckIndex,
                    state.PendingSelectionEventEntry is null
                        ? state.Rng.Niche
                        : EventRng(state, state.PendingSelectionEventEntry)
                );
                break;
            case DeckSelection.TransformToRandomUpgraded:
                // Astrolabe: the roll is off `Rng.Niche`, and the UPGRADE lands on the new
                // card -- `CardCmd.Upgrade(cardModel)` happens between the create and the
                // transform, so an unupgradable roll simply arrives unupgraded.
                TransformCardAt(state, deckIndex, state.Rng.Niche);
                if (RunConstants.IsRunCardUpgradable(state.Deck[^1]))
                {
                    state.Deck[^1] = state.Deck[^1] with { Upgraded = true };
                }

                break;
            case DeckSelection.Remove:
            case DeckSelection.RemoveUpgradable:
            case DeckSelection.RemoveTaggedBasic:
                state.Deck.RemoveAt(deckIndex);
                break;
            default:
                return false;
        }

        state.PendingSelectionCount--;
        return true;
    }

    /// <summary>
    /// What amount an enchantment lands at when nothing overrides it.
    /// </summary>
    /// <remarks>
    /// Self-Help Book applies Sharp, Nimble and Swift at 2 (its Enchantment*Amount vars);
    /// every event enchantment is <c>CardCmd.Enchant&lt;T&gt;(card, 1m)</c>. Pael's Growth
    /// is the exception that made this an override rather than a rule — it applies Clone
    /// at FOUR — so a caller that knows its own amount passes it.
    /// </remarks>
    private static int SelfHelpBookAmount(Enchantment enchantment) =>
        enchantment is Enchantment.Sharp or Enchantment.Nimble or Enchantment.Swift ? 2 : 1;

    /// <summary>
    /// What Luminous Choir asks for its tribute. The event starts from a GoldVar of 149
    /// and, on generate, takes off <c>Rng.NextInt(0, 50)</c> from its own stream -- so
    /// the price is somewhere in 100..149 and the option is locked below it.
    /// </summary>
    public static int LuminousChoirTributeCost(RunState state)
    {
        if (state.EventValue0 is null)
        {
            CalculateLuminousChoirVars(state);
        }

        return state.EventValue0!.Value;
    }

    /// <summary>
    /// <c>LuminousChoir.CalculateVars</c> takes GoldVar(149) and subtracts
    /// <c>Rng.NextInt(0, 50)</c> -- once, as the event is generated. Reading it re-rolled
    /// it, which was invisible only because every draw came off a fresh stream.
    /// </summary>
    private static void CalculateLuminousChoirVars(RunState state) =>
        state.EventValue0 = 149 - EventRng(state, "LUMINOUS_CHOIR").NextInt(0, 50);

    /// <summary>
    /// What the sphere charges to Uncover Future. <c>CrystalSphere.CalculateVars</c> adds
    /// <c>Rng.NextInt(1, 50)</c> to a base of 50, so the price is 51..99 -- and since the
    /// event only turns up on a run holding at least 100 gold, it is always affordable and
    /// neither option is ever locked.
    ///
    /// The draw matters beyond the price: it is the first thing taken from the event's
    /// stream, and the board the minigame lays out is drawn from the same stream straight
    /// after. A cost read off a fresh Rng would leave the board one draw early.
    /// </summary>
    public static int CrystalSphereCost(RunState state)
    {
        EnsureCrystalSphereVars(state);
        return state.EventValue0!.Value;
    }

    private static void EnsureCrystalSphereVars(RunState state)
    {
        if (state.CrystalSphereRng is not null)
        {
            return;
        }

        state.CrystalSphereRng = EventRng(state, "CRYSTAL_SPHERE");
        state.EventValue0 = 50 + state.CrystalSphereRng.NextInt(1, 50);
    }

    /// <summary>
    /// Opens the sphere with the given number of divinations: three for Uncover Future,
    /// six for the Payment Plan. Both options roll nothing of their own -- one spends gold,
    /// the other adds a Debt -- so the board is the same either way for a given seed.
    /// </summary>
    public static void OpenCrystalSphere(RunState state, int divinations)
    {
        EnsureCrystalSphereVars(state);
        state.CrystalSphere = CrystalSphereGame.Create(state.CrystalSphereRng!, divinations);
        state.Phase = RunPhase.CrystalSphere;
    }

    /// <summary>
    /// What uncovering a thing does on the spot. Only the curse acts immediately -- its
    /// <c>RevealItem</c> puts a Doubt in the deck there and then; everything else waits for
    /// the last divination and arrives as a reward.
    /// </summary>
    public static void RevealCrystalSphereItem(RunState state, CrystalSphereItem item)
    {
        if (item.Kind == CrystalSphereItemKind.Curse)
        {
            AddCardToDeck(state, new CardInstance(NamedCard("Doubt"), Upgraded: false));
        }
    }

    public static int SunkenTreasurySmallChestGold(RunState state)
    {
        EnsureSunkenTreasuryVars(state);
        return state.EventValue0!.Value;
    }

    public static int SunkenTreasuryLargeChestGold(RunState state)
    {
        EnsureSunkenTreasuryVars(state);
        return state.EventValue1!.Value;
    }

    private static void EnsureSunkenTreasuryVars(RunState state)
    {
        if (state.EventValue0 is null || state.EventValue1 is null)
        {
            CalculateSunkenTreasuryVars(state);
        }
    }

    private static void CalculateEventVars(RunState state)
    {
        switch (state.EventId)
        {
            case RunConstants.EventTheArchitect:
            {
                // TheArchitect.LoadDialogue: `Rng.NextItem(GetValidDialogues(character,
                // TotalWins, ...))`. GetValidDialogues returns the dialogue whose VisitIndex
                // equals the win count if one exists, else every IsRepeating dialogue with
                // VisitIndex <= it. Silent's authored dialogues are all one line. Ironclad's
                // (DefineDialogues) are VisitIndex 0/1/2 with 2/3/3 lines, and all three are
                // repeating (loc keys THE_ARCHITECT.talk.IRONCLAD.{0,1,2}-0r), so from three
                // wins on the pick is a real roll over all three.
                int wins = state.Progression.WinsForCharacter(
                    CharacterCatalog.Entry(state.Character)
                );
                int[] lines = state.Character == CharacterId.Silent ? [1, 1, 1, 1] : [2, 3, 3];
                var rng = EventRng(state, "THE_ARCHITECT");
                // NextItem consumes a draw even when one dialogue is eligible.
                int selected = rng.NextInt(wins < lines.Length ? 1 : lines.Length);
                state.EventValue0 = lines[wins < lines.Length ? wins : selected];
                break;
            }
            case RunConstants.EventSunkenTreasury:
                CalculateSunkenTreasuryVars(state);
                break;
            case RunConstants.EventEndlessConveyor:
                CalculateEndlessConveyorVars(state);
                break;
            case RunConstants.EventSunkenStatue:
                CalculateSunkenStatueVars(state);
                break;
            case RunConstants.EventDenseVegetation:
                CalculateDenseVegetationVars(state);
                break;
            case RunConstants.EventWhisperingHollow:
                CalculateWhisperingHollowVars(state);
                break;
            case RunConstants.EventJungleMazeAdventure:
                CalculateJungleMazeVars(state);
                break;
            case RunConstants.EventThisOrThat:
                CalculateThisOrThatVars(state);
                break;
            case RunConstants.EventLuminousChoir:
                CalculateLuminousChoirVars(state);
                break;
            case RunConstants.EventCrystalSphere:
                // The cost is CalculateVars' only draw, and the board comes off the same
                // stream straight after -- so it has to be spent on entry, not on the
                // first read. Reading the mask would otherwise roll it.
                EnsureCrystalSphereVars(state);
                break;
            case RunConstants.EventSlipperyBridge:
                RollSlipperyBridgeCard(state);
                break;
            case RunConstants.EventRanwidTheElder:
                CalculateRanwidVars(state);
                break;
            case RunConstants.EventRelicTrader:
                RelicTraderOffers(state);
                break;
            case RunConstants.EventWelcomeToWongos:
                // The featured item is pulled when the options are generated, so it comes
                // off the bag on entry whether or not the player buys it.
                WongosFeaturedItem(state);
                break;
            case RunConstants.EventColorfulPhilosophers:
                // Which three of the four pools survive the cut is decided in
                // `GenerateInitialOptions`, so it is spent on entry. Deriving it lazily
                // rolled the event's stream on a mask READ and could show a different
                // three than the ones the player is looking at -- Tinker Time's bug.
                ColorfulPhilosopherPools(state);
                break;
            case RunConstants.EventTheFutureOfPotions:
                PrepareFutureOfPotions(state);
                break;
            case RunConstants.EventFakeMerchant:
                // `BeforeEventStarted` shuffles the nine fakes and takes six, so the
                // shelf exists before the player has done anything.
                FakeMerchantStock(state);
                break;
        }
    }

    /// <summary>
    /// The gold the statue's pool pays. <c>SunkenStatue.CalculateVars</c> takes a
    /// GoldVar of 111 and adds <c>Rng.NextInt(-10, 11)</c> from the event's own stream,
    /// so the amount is 101..121 and is rolled when the event is generated -- before
    /// either option is taken, which is why it cannot be rolled inside the option.
    /// </summary>
    public static int SunkenStatueGold(RunState state)
    {
        if (state.EventValue0 is null)
        {
            CalculateSunkenStatueVars(state);
        }

        return state.EventValue0!.Value;
    }

    private static void CalculateSunkenStatueVars(RunState state)
    {
        state.EventValue0 = 111 + EventRng(state, "SUNKEN_STATUE").NextInt(-10, 11);
    }

    /// <summary>
    /// What trudging through the vegetation pays: <c>Rng.NextInt(61, 100)</c>, rolled in
    /// CalculateVars rather than in the option.
    /// </summary>
    public static int DenseVegetationGold(RunState state)
    {
        if (state.EventValue0 is null)
        {
            CalculateDenseVegetationVars(state);
        }

        return state.EventValue0!.Value;
    }

    private static void CalculateDenseVegetationVars(RunState state)
    {
        state.EventValue0 = EventRng(state, "DENSE_VEGETATION").NextInt(61, 100);
    }

    /// <summary>
    /// Trash Heap's two prize tables, transcribed from the event's own literal arrays.
    /// The game lists them by class, so they are resolved by name here rather than by
    /// number: an id that stops resolving throws instead of silently picking whatever
    /// now sits at that index.
    /// </summary>
    private static readonly string[] TrashHeapRelicNames =
    [
        "DarkstonePeriapt",
        "DreamCatcher",
        "HandDrill",
        "MawBank",
        "TheBoot",
    ];

    private static readonly string[] TrashHeapCardNames =
    [
        "Caltrops",
        "Clash",
        "Distraction",
        "DualWield",
        "Entrench",
        "HelloWorld",
        "Outmaneuver",
        "Rebound",
        "RipAndTear",
        "Stack",
    ];

    public static int TrashHeapRelic(RunState state) =>
        ResolveRelic(EventRng(state, "TRASH_HEAP").NextItem(TrashHeapRelicNames));

    public static int TrashHeapCard(RunState state) =>
        ResolveCard(EventRng(state, "TRASH_HEAP").NextItem(TrashHeapCardNames));

    /// <summary>
    /// A relic an event names outright. Most events that hand over a relic hand over a
    /// SPECIFIC one -- <c>RelicCmd.Obtain&lt;ChosenCheese&gt;</c> -- and the emulator was
    /// rolling one from the reward pool at every such site, which is a different relic
    /// and also burns a draw the game never makes.
    /// </summary>
    public static int NamedRelic(string name) => ResolveRelic(name);

    /// <summary>A card an event names outright -- Wood Carvings' Peck and Toric Toughness.</summary>
    public static int NamedCard(string name) => ResolveCard(name);

    /// <summary>Byrdonis Nest's quest card, which is what puts HATCH on a rest site.</summary>
    public static int ByrdonisEggCard => ResolveCard("ByrdonisEgg");

    /// <summary>What the egg becomes: a 0-cost, 14-damage Attack.</summary>
    public static int ByrdSwoopCard => ResolveCard("ByrdSwoop");

    /// <summary>Circlet, the fallback relic -- the only stackable one in the game.</summary>
    public static int CircletRelic => ResolveRelic("Circlet");

    /// <summary>
    /// The three relics the trader is willing to take, in the order it offers them:
    /// <c>player.Relics.Where(IsTradable).StableShuffle(Rng).Take(3)</c>. StableShuffle
    /// sorts by ModelId -- Category then Entry, as ordinal strings -- before Fisher-Yates,
    /// which is why RelicDef carries an Entry now: our own numeric ids sort differently,
    /// and a different pre-shuffle order is a different shuffle from the same stream.
    ///
    /// Returns indices into <c>state.Relics</c>, because the trade removes the relic the
    /// player owns rather than a copy.
    /// </summary>
    public static List<int> RelicTraderStock(RunState state)
    {
        state.EventRelicStock ??= RollRelicTraderStock(state);
        return state.EventRelicStock;
    }

    public static List<int> RelicTraderOffers(RunState state)
    {
        if (state.EventRelicOffers is not null)
            return state.EventRelicOffers;
        // CalculateVars first reads OwnedRelics, then NewRelics. NewRelics pulls
        // three entries from the grab bag, each rolling rarity on PlayerRng.Rewards.
        // Unchosen entries stay removed from the bag; choosing consumes no further roll.
        state.EventRelicOffers = [];
        if (RelicTraderStock(state).Count > 0)
            for (int i = 0; i < 3; i++)
                state.EventRelicOffers.Add(RunRewardGenerator.NextRelic(state));
        return state.EventRelicOffers;
    }

    /// <summary>
    /// Colorful Philosophers' option list: which of the four candidate pools survived the
    /// cut, as indices into <see cref="ColorfulPhilosopherPoolOrder"/>.
    /// </summary>
    /// <remarks>
    /// `CardPoolColorOrder` is Necrobinder, Ironclad, Regent, Silent, Defect, filtered by
    /// `character.CardPool != cardPool` -- which for the emulator's Ironclad leaves four
    /// -- and then `while (list.Count > 3) list.RemoveAt(Rng.NextInt(list.Count))` drops
    /// one. Removing is not taking three at random: it is ONE draw rather than three, and
    /// the survivors keep the colour order rather than a shuffled one, so the same seed
    /// gives a different set under either reading.
    /// </remarks>
    public static int[] ColorfulPhilosopherPools(RunState state)
    {
        if (state.EventRandomOffer.Length > 0)
        {
            return state.EventRandomOffer;
        }

        var candidates = Enumerable
            .Range(0, ColorfulPhilosopherPoolOrderFor(state.Character).Length)
            .ToList();
        var rng = EventRng(state, "COLORFUL_PHILOSOPHERS");
        while (candidates.Count > 3)
        {
            candidates.RemoveAt(rng.NextInt(candidates.Count));
        }

        state.EventRandomOffer = candidates.ToArray();
        return state.EventRandomOffer;
    }

    // GenerateInitialOptions reads PotionToCardType, which assigns a type to EVERY
    // held potion once, in slot order. Empty slots consume nothing. The event does
    // not draw another type when the player chooses a potion.
    public static void PrepareFutureOfPotions(RunState state)
    {
        if (state.EventRandomOffer.Length == state.PotionSlots.Length)
            return;
        state.EventRandomOffer = Enumerable
            .Repeat((int)CardType.None, state.PotionSlots.Length)
            .ToArray();
        var rng = EventRng(state, "THE_FUTURE_OF_POTIONS");
        for (int i = 0; i < state.PotionSlots.Length; i++)
            if (state.PotionSlots[i] != 0)
                state.EventRandomOffer[i] = (int)
                    rng.NextItem(RunRewardGenerator.FutureOfPotionsCardTypes(state.PotionSlots[i]));
    }

    /// <summary>
    /// The four pools the event can offer an Ironclad, in `CardPoolColorOrder` minus
    /// Ironclad's own.
    /// </summary>
    public static int[][] ColorfulPhilosopherPoolOrder =>
        ColorfulPhilosopherPoolOrderFor(CharacterId.Ironclad);

    public static int[][] ColorfulPhilosopherPoolOrderFor(CharacterId character) =>
        new[] { "NECROBINDER", "IRONCLAD", "REGENT", "SILENT", "DEFECT" }
            .Where(entry => entry != CharacterCatalog.Entry(character))
            .Select(entry => CharacterCatalog.CardPoolForEntry(entry).ToArray())
            .ToArray();

    /// <summary>
    /// The Fake Merchant's stall: six of the nine fake relics, in the order it shuffled
    /// them. A slot the player has bought is set to 0 rather than removed, so the indices
    /// stay put for the whole visit -- and the still-stocked ones are what the fight pays
    /// out if the player throws a Foul Potion instead.
    /// </summary>
    /// <remarks>
    /// <c>BeforeEventStarted</c> builds this ONCE, so it does not reroll when the player
    /// looks again -- the Relic Trader's shelf rule, and the reason both live on
    /// <c>EventRelicStock</c>. Fake Merchant's Rug is deliberately NOT in the list: the
    /// game's own comment says it is a combat reward, not something on sale.
    /// </remarks>
    public static List<int> FakeMerchantStock(RunState state)
    {
        if (state.EventRelicStock is null)
        {
            state.EventRelicStock = RollFakeMerchantStock(state);
            // Each MerchantRelicEntry constructor rolls CalcCost on PlayerRng.Shops,
            // after the event stream has shuffled the shelf. EventRoom is not a
            // MerchantRoom, so MerchantEntry.Cost applies no shop discount hooks.
            state.FakeMerchantPrices = state
                .EventRelicStock.Select(relic =>
                    RunRewardGenerator.ShopRelicCost(relic, state.PlayerRng.Shops)
                )
                .ToList();
        }
        return state.EventRelicStock;
    }

    private static List<int> RollFakeMerchantStock(RunState state)
    {
        // `_inventoryRelics` is a fixed array in the game's own order, which is
        // alphabetical by class name; UnstableShuffle then permutes it in place and the
        // first six are the stall.
        var fakes = new List<int>
        {
            ResolveRelic("FakeAnchor"),
            ResolveRelic("FakeBloodVial"),
            ResolveRelic("FakeHappyFlower"),
            ResolveRelic("FakeLeesWaffle"),
            ResolveRelic("FakeMango"),
            ResolveRelic("FakeOrichalcum"),
            ResolveRelic("FakeSneckoEye"),
            ResolveRelic("FakeStrikeDummy"),
            ResolveRelic("FakeVenerableTeaSet"),
        };
        EventRng(state, "FAKE_MERCHANT").Shuffle(fakes);
        return fakes.Take(RunConstants.FakeMerchantInventorySize).ToList();
    }

    /// <summary>
    /// <c>RelicTrader._ownedRelics</c> is built once and kept: the shelf does not reshuffle
    /// itself every time the player looks at it.
    /// </summary>
    private static List<int> RollRelicTraderStock(RunState state)
    {
        var tradable = Enumerable
            .Range(0, state.Relics.Count)
            .Where(i => IsTradableRelic(state, state.Relics[i]))
            .OrderBy(
                i => GeneratedData.Relics.Get(state.Relics[i].DefId).Entry,
                StringComparer.Ordinal
            )
            .ToList();
        EventRng(state, "RELIC_TRADER").Shuffle(tradable);
        return tradable.Take(3).ToList();
    }

    /// <summary>
    /// The relic Ranwid asks for: <c>Rng.NextItem(Relics.Where(IsTradable))</c> off the
    /// event's own stream. Returns an index into <c>state.Relics</c>, or -1 when the run
    /// holds nothing the elder will take.
    /// </summary>
    public static int RanwidTradeIndex(RunState state)
    {
        if (state.EventValue0 is null)
        {
            CalculateRanwidVars(state);
        }

        return state.EventValue0!.Value;
    }

    /// <summary>
    /// The potion Ranwid asks for: <c>Rng.NextItem(Owner.Potions)</c>, drawn before the
    /// relic. Returns an index into <c>state.PotionSlots</c>, or -1 on an empty belt.
    /// </summary>
    public static int RanwidPotionSlot(RunState state)
    {
        if (state.EventValue1 is null)
        {
            CalculateRanwidVars(state);
        }

        return state.EventValue1!.Value;
    }

    /// <summary>
    /// <c>RanwidTheElder.GenerateInitialOptions</c> draws twice: a potion, then a relic.
    /// </summary>
    /// <remarks>
    /// The potion draw was never spent here, so the relic was read one position early
    /// whenever the run held a potion. <c>Rng.NextItem</c> returns default and consumes
    /// NOTHING on an empty sequence, and <c>Player.Potions</c> filters empty slots out --
    /// so a run carrying no potions really does draw the relic first.
    /// </remarks>
    private static void CalculateRanwidVars(RunState state)
    {
        var rng = EventRng(state, "RANWID_THE_ELDER");
        // The potion draw binds WHICH potion GivePotion discards, so its slot is kept in
        // EventValue1. It indexes `Player.Potions`, the belt with gaps filtered out, and
        // is mapped back to the real slot here.
        var occupied = Enumerable
            .Range(0, state.PotionSlots.Length)
            .Where(i => state.PotionSlots[i] != 0)
            .ToList();
        state.EventValue1 = occupied.Count == 0 ? -1 : occupied[rng.NextInt(0, occupied.Count)];

        var tradable = Enumerable
            .Range(0, state.Relics.Count)
            .Where(i => IsTradableRelic(state, state.Relics[i]))
            .ToList();
        state.EventValue0 = tradable.Count == 0 ? -1 : rng.NextItem(tradable);
    }

    /// <summary>
    /// <c>RelicModel.IsTradable</c>: Starter, Event and Ancient relics are never traded,
    /// nor is one whose pickup already paid out, nor one with a pet attached.
    /// </summary>
    public static bool IsTradableRelic(RunState state, RelicInstance relic) =>
        GeneratedData.Relics.Get(relic.DefId).IsTradable
        && !state.UsedUpRelics.Contains(relic.DefId);

    /// <summary>
    /// A curse rolled the way anything that hands one out rolls it: the curse pool,
    /// filtered to cards that may be generated -- eight of the eighteen refuse -- ordered
    /// by ModelId, and picked off the Niche stream.
    ///
    /// Ascender's Bane stood here for every such roll, which is not merely the wrong curse:
    /// it is one of the eight the game will never generate.
    /// </summary>
    public static int RollGeneratableCurse(RunState state)
    {
        var curses = GeneratedData
            .CardPools.Curse.ToArray()
            .Where(id => GeneratedData.Cards.Get(id).CanBeGeneratedByModifiers)
            .OrderBy(id => GeneratedData.Cards.Get(id).Entry, StringComparer.Ordinal)
            .ToArray();
        return curses.Length == 0
            ? RunConstants.CursePlaceholderCard
            : state.Rng.Niche.NextItem(curses);
    }

    /// <summary>A potion an event names outright -- the Potion Courier's Foul Potions.</summary>
    public static int NamedPotion(string name) =>
        GeneratedData.Potions.FindId(name)
        ?? throw new InvalidOperationException($"No potion named {name}");

    /// <summary>
    /// What Whispering Hollow charges for its two potions: a GoldVar of 35 shifted by
    /// <c>Rng.NextInt(-9, 10)</c> in CalculateVars, so 26..44 -- and it is SPENT, not paid
    /// out. The event only appears at all when the run holds 44 gold.
    /// </summary>
    public static int WhisperingHollowGold(RunState state)
    {
        if (state.EventValue0 is null)
        {
            CalculateWhisperingHollowVars(state);
        }

        return state.EventValue0!.Value;
    }

    private static void CalculateWhisperingHollowVars(RunState state)
    {
        state.EventValue0 = 35 + EventRng(state, "WHISPERING_HOLLOW").NextInt(-9, 10);
    }

    /// <summary>
    /// Jungle Maze's two purses as the options display them. CalculateVars shifts each by
    /// <c>Rng.NextFloat(-15f, 15f)</c> off the same stream, solo first, and
    /// DynamicVar.IntValue truncates the decimal.
    /// </summary>
    public static int JungleMazeSoloGold(RunState state)
    {
        EnsureJungleMazeVars(state);
        return state.EventValue0!.Value;
    }

    public static int JungleMazeJoinForcesGold(RunState state)
    {
        EnsureJungleMazeVars(state);
        return state.EventValue1!.Value;
    }

    /// <summary>
    /// The purses as the options PAY them: `DontNeedHelp` and `SafetyInNumbers` pass the
    /// decimal BaseValue to `PlayerCmd.GainGold`, so Bowler Hat multiplies the fraction and
    /// the truncation comes after -- 38.76077 pays 48 with the hat, not (int)(38 * 1.25).
    /// </summary>
    public static decimal JungleMazeSoloGoldAmount(RunState state)
    {
        EnsureJungleMazeVars(state);
        return state.EventAmount0!.Value;
    }

    /// <inheritdoc cref="JungleMazeSoloGoldAmount(RunState)" />
    public static decimal JungleMazeJoinForcesGoldAmount(RunState state)
    {
        EnsureJungleMazeVars(state);
        return state.EventAmount1!.Value;
    }

    private static void EnsureJungleMazeVars(RunState state)
    {
        if (state.EventAmount0 is null || state.EventAmount1 is null)
        {
            CalculateJungleMazeVars(state);
        }
    }

    /// <summary>
    /// This or That's purse: <c>Rng.NextInt(41, 69)</c>, rolled in CalculateVars.
    /// </summary>
    public static int ThisOrThatGold(RunState state)
    {
        if (state.EventValue0 is null)
        {
            CalculateThisOrThatVars(state);
        }

        return state.EventValue0!.Value;
    }

    private static void CalculateThisOrThatVars(RunState state)
    {
        state.EventValue0 = EventRng(state, "THIS_OR_THAT").NextInt(41, 69);
    }

    /// <summary>
    /// What one immersion in the Abyssal Baths costs. <c>OnImmerse</c> gains MaxHpVar(2),
    /// takes DamageVar and then raises that damage by one, so the sequence is 3, 4, 5 and
    /// on -- and the Max HP arrives first, carrying current HP up with it, which is why
    /// the first dip is a net loss of only one.
    /// </summary>
    public static void Immerse(RunState state)
    {
        GainMaxHp(state, 2);
        DamagePlayer(state, AbyssalBathsDamage(state));
        state.EventPage++;
    }

    /// <summary>
    /// What the next Decipher costs at the Tablet of Truth. The tablet is not one choice
    /// but five, and the price doubles each time before the last one asks for everything:
    /// 3, 6, 12, 24, then MaxHp - 1. <c>GetDecipherCost</c> reads the count AFTER it moves,
    /// which is why the first is the DynamicVar's own 3 rather than anything the switch
    /// returns.
    /// </summary>
    public static int TabletOfTruthCost(RunState state) =>
        state.EventPage switch
        {
            0 => 3,
            1 => 6,
            2 => 12,
            3 => 24,
            _ => state.PlayerMaxHp - 1,
        };

    /// <summary>
    /// One Decipher: pay the Max HP and upgrade. Every stage but the last upgrades ONE
    /// card rolled off the event's stream; the fifth upgrades every upgradable card in the
    /// deck, which is the whole reason to have kept paying.
    ///
    /// <c>LoseMaxHpAndUpgrade</c> also kills outright when the price is not less than Max
    /// HP -- it takes MaxHp - 1 and then calls Kill -- so a tablet the run cannot afford
    /// ends it.
    /// </summary>
    public static void Decipher(RunState state)
    {
        int cost = TabletOfTruthCost(state);
        bool lethal = cost >= state.PlayerMaxHp;
        LoseMaxHp(state, lethal ? state.PlayerMaxHp - 1 : cost);
        if (lethal)
        {
            state.PlayerHp = 0;
            return;
        }

        if (state.EventPage == 4)
        {
            for (int i = 0; i < state.Deck.Count; i++)
            {
                if (RunConstants.IsRunCardUpgradable(state.Deck[i]))
                {
                    state.Deck[i] = state.Deck[i] with { Upgraded = true };
                }
            }
        }
        else
        {
            UpgradeRandomCard(state, "TABLET_OF_TRUTH");
        }

        state.EventPage++;
    }

    /// <summary>The tablet has five secrets and no more.</summary>
    public static bool TabletHasMoreToSay(RunState state) => state.EventPage < 5;

    /// <summary>The damage the NEXT immersion will do: DamageVar(3), plus one per dip.</summary>
    public static int AbyssalBathsDamage(RunState state) => 3 + state.EventPage;

    /// <summary>
    /// What the next Hold On costs: <c>CurrentHpLoss => 3 + NumberOfHoldOns</c>, read
    /// before the counter moves.
    /// </summary>
    public static int SlipperyBridgeHpLoss(RunState state) => 3 + state.EventPage;

    /// <summary>
    /// The card the Slippery Bridge is holding over the player. It prefers a non-Basic
    /// card and falls back to any removable one, which on a starter deck is the whole
    /// deck bar Ascender's Bane -- so it is a roll, not a fixed pick.
    /// </summary>
    public static int SlipperyBridgeCardIndex(RunState state)
    {
        if (state.EventValue0 is null)
        {
            RollSlipperyBridgeCard(state);
        }

        return state.EventValue0!.Value;
    }

    /// <summary>
    /// <c>SlipperyBridge.GetNewRandomCard</c>: called once from GenerateInitialOptions and
    /// again from every Hold On, storing the result in <c>RandomCardToLose</c>. Recomputing
    /// it per read drew a new card each time the mask or a test looked at it.
    /// </summary>
    public static void RollSlipperyBridgeCard(RunState state) =>
        state.EventValue0 = PickSlipperyBridgeCard(state);

    private static int PickSlipperyBridgeCard(RunState state)
    {
        int previous = state.EventValue0.GetValueOrDefault(-1);
        if (previous >= 0)
            state.SlipperyBridgeSkippedRemovals.Add(previous);
        var candidates = Enumerable
            .Range(0, state.Deck.Count)
            .Where(i =>
                previous < 0
                    ? GeneratedData.Cards.Get(state.Deck[i].DefId).Rarity != CardRarity.Basic
                    : state.Deck[i].DefId != state.Deck[previous].DefId
            )
            .Where(i =>
                IsRemovable(state.Deck[i]) && !state.SlipperyBridgeSkippedRemovals.Contains(i)
            )
            .ToList();
        if (candidates.Count == 0)
        {
            candidates = Enumerable
                .Range(0, state.Deck.Count)
                .Where(i => IsRemovable(state.Deck[i]))
                .ToList();
        }

        if (candidates.Count == 0)
        {
            return -1;
        }

        // Each GetNewRandomCard consumes ONE draw from the persistent event stream.
        // Replaying EventPage extra draws skips values now that this stream is retained.
        return EventRng(state, "SLIPPERY_BRIDGE").NextItem(candidates);
    }

    /// <summary>
    /// <c>CardModel.IsRemovable</c> excludes the Eternal keyword, including the keyword
    /// added by TezcatarasEmber.OnEnchant. Rarity and card type are not this rule.
    /// </summary>
    private static bool IsRemovable(CardInstance card)
    {
        var def = GeneratedData.Cards.Get(card.DefId);
        return !def.Eternal && card.Enchantment != Enchantment.TezcatarasEmber;
    }

    /// <summary>
    /// <c>FishingRod.AfterCombatEnd</c>: every third MONSTER room upgrades a card.
    /// </summary>
    /// <remarks>
    /// Elites, bosses and the fights an event starts are all skipped -- the relic returns
    /// early on anything that is not <c>RoomType.Monster</c>, so they do not even advance
    /// the counter. The card is a plain <c>Rng.Niche.NextItem</c> over the upgradable
    /// ones, and the counter lives on the relic so it survives a save.
    /// </remarks>
    /// <summary>
    /// `Guilty.AfterCombatEnd`: a Guilty sitting in the DECK counts the combat, and at
    /// FIVE it removes itself. The only card in the game that leaves on its own.
    /// </summary>
    /// <remarks>
    /// Counted per COPY -- the count is `[SavedProperty]` on the card model, so two
    /// Guilties taken at different times serve their sentences separately. `Pile.Type ==
    /// Deck` is checked twice in the source, before and after the increment, because the
    /// removal is awaited; here the list is rebuilt against the deck each time, which
    /// says the same thing.
    /// </remarks>
    public static void ServeGuiltySentences(RunState state)
    {
        int guilty = ResolveCard("Guilty");
        int copies = state.Deck.Count(card => card.DefId == guilty);
        if (copies == 0)
        {
            state.GuiltyCombatsServed.Clear();
            return;
        }

        while (state.GuiltyCombatsServed.Count < copies)
        {
            state.GuiltyCombatsServed.Add(0);
        }

        while (state.GuiltyCombatsServed.Count > copies)
        {
            state.GuiltyCombatsServed.RemoveAt(state.GuiltyCombatsServed.Count - 1);
        }

        for (int i = 0; i < state.GuiltyCombatsServed.Count; i++)
        {
            state.GuiltyCombatsServed[i]++;
        }

        int served = state.GuiltyCombatsServed.Count(count => count >= GuiltyCombats);
        for (int i = 0; i < served; i++)
        {
            int index = state.Deck.FindIndex(card => card.DefId == guilty);
            if (index >= 0)
            {
                state.Deck.RemoveAt(index);
            }
        }

        state.GuiltyCombatsServed.RemoveAll(count => count >= GuiltyCombats);
    }

    /// <summary>`Guilty.maxCombats`.</summary>
    private const int GuiltyCombats = 5;

    public static void TriggerFishingRod(RunState state)
    {
        if (state.LastResolvedRoomType != RunConstants.NodeNormal)
        {
            return;
        }

        int index = state.Relics.FindIndex(relic => relic.DefId == RunConstants.RelicFishingRod);
        if (index < 0)
        {
            return;
        }

        int seen = state.Relics[index].Counter + 1;
        state.Relics[index] = state.Relics[index] with { Counter = seen };
        if (seen % RunConstants.FishingRodCombats != 0)
        {
            return;
        }

        var upgradable = Enumerable
            .Range(0, state.Deck.Count)
            .Where(i => RunConstants.IsRunCardUpgradable(state.Deck[i]))
            .ToList();
        if (upgradable.Count == 0)
        {
            return;
        }

        int pick = state.Rng.Niche.NextItem(upgradable);
        state.Deck[pick] = state.Deck[pick] with { Upgraded = true };
    }

    /// <summary>Upgrade one upgradable card, chosen off the event's own stream.</summary>
    public static bool UpgradeRandomCard(RunState state, string eventEntry)
    {
        var candidates = Enumerable
            .Range(0, state.Deck.Count)
            .Where(i => RunConstants.IsRunCardUpgradable(state.Deck[i]))
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        int index = EventRng(state, eventEntry).NextItem(candidates);
        state.Deck[index] = state.Deck[index] with { Upgraded = true };
        return true;
    }

    private static void CalculateJungleMazeVars(RunState state)
    {
        var rng = EventRng(state, "JUNGLE_MAZE_ADVENTURE");
        decimal solo = 150m + (decimal)rng.NextFloat(-15f, 15f);
        decimal joinForces = 50m + (decimal)rng.NextFloat(-15f, 15f);
        state.EventAmount0 = solo;
        state.EventAmount1 = joinForces;
        state.EventValue0 = (int)solo;
        state.EventValue1 = (int)joinForces;
    }

    /// <summary>Doll Room's three dolls, in the event's own declaration order.</summary>
    private static readonly string[] DollRoomDolls =
    [
        "DaughterOfTheWind",
        "MrStruggles",
        "BingBong",
    ];

    public static int DollRoomRandomDoll(RunState state) =>
        ResolveRelic(EventRng(state, "DOLL_ROOM").NextItem(DollRoomDolls));

    /// <summary>
    /// The dolls the second page puts in front of the player: the three shuffled, then
    /// two of them for Take Some Time and all three for Examine. StableShuffle sorts by
    /// ModelId -- Category then Entry as ordinal strings -- before Fisher-Yates, which is
    /// what RelicDef.Entry is for.
    /// </summary>
    public static List<int> DollRoomOffer(RunState state, int count)
    {
        var dolls = DollRoomDolls
            .Select(ResolveRelic)
            .OrderBy(id => GeneratedData.Relics.Get(id).Entry, StringComparer.Ordinal)
            .ToList();
        EventRng(state, "DOLL_ROOM").Shuffle(dolls);
        return dolls.Take(count).ToList();
    }

    /// <summary>
    /// The Rare relic Wongo has on display. It is pulled in GenerateInitialOptions -- when
    /// the event OPENS, not when it is bought -- so the option can name it, and it leaves
    /// the bag either way. Cached in EventValue1 so reading it twice does not pull twice.
    /// </summary>
    public static int WongosFeaturedItem(RunState state)
    {
        state.EventValue1 ??= RunRewardGenerator.NextShopRelicOfRarity(state, RelicRarity.Rare);
        return state.EventValue1.Value;
    }

    /// <summary>The Lantern Key card, which War Historian Repy spends to open anything.</summary>
    public static int LanternKeyCard => ResolveCard("LanternKey");

    /// <summary>
    /// Repy gives a SECOND reward when the run still holds a Lantern Key after the first
    /// is spent -- <c>ShouldGetSecondReward</c> -- which for a solo run means it arrived
    /// holding two. The first choice spends one key; the page that follows offers only
    /// the door the player did not take.
    /// </summary>
    public static bool RepyOwesASecondReward(RunState state) =>
        state.Deck.Any(card => card.DefId == LanternKeyCard);

    /// <summary>
    /// Whether a Lantern Key in the deck rules this act: both of the card's hooks
    /// (<c>ModifyUnknownMapPointRoomTypes</c> to Event alone, <c>ModifyNextEvent</c> to
    /// War Historian Repy) answer only while <c>CurrentActIndex == 2</c>.
    /// </summary>
    public static bool LanternKeyGovernsAct(RunState state) =>
        state.CurrentActIndex == 2 && state.Deck.Any(card => card.DefId == LanternKeyCard);

    /// <summary>
    /// <c>Hook.ModifyNextEvent</c> over the event <c>EnsureNextEventIsValid</c> settled on.
    /// </summary>
    private static int ModifyNextEvent(RunState state, int eventId) =>
        LanternKeyGovernsAct(state) ? RunConstants.EventWarHistorianRepy : eventId;

    /// <summary>Spends one Lantern Key, if the run has one to spend.</summary>
    public static void SpendLanternKey(RunState state)
    {
        int index = state.Deck.FindIndex(card => card.DefId == LanternKeyCard);
        if (index >= 0)
        {
            state.Deck.RemoveAt(index);
        }
    }

    private static int ResolveRelic(string name) =>
        GeneratedData.Relics.FindId(name)
        ?? throw new InvalidOperationException($"No relic named {name}");

    private static int ResolveCard(string name) =>
        GeneratedData.Cards.FindId(name)
        ?? throw new InvalidOperationException($"No card named {name}");

    /// <summary>
    /// What Spiraling Whirlpool's Drink heals: a HealVar whose BaseValue is
    /// <c>MaxHp * 0.33m</c>, read through <c>DynamicVar.IntValue</c>, which is a plain
    /// <c>(int)</c> cast and therefore truncates.
    /// </summary>
    public static int SpiralingWhirlpoolHeal(RunState state) => (int)(state.PlayerMaxHp * 0.33m);

    public static int FresnelLensRelic => ResolveRelic("FresnelLens");

    public static int SwordOfStoneRelic => ResolveRelic("SwordOfStone");

    public static int GlowwaterPotion =>
        GeneratedData.Potions.FindId("GlowwaterPotion")
        ?? throw new InvalidOperationException("No potion named GlowwaterPotion");

    // ── Endless Conveyor's belt ───────────────────────────────────────────────
    // The dish is rolled before the option is even shown -- the option reads "Grab
    // Fried Eel off the Belt" -- and rolled again after every grab, so the belt is a
    // small state machine rather than a one-off. EventValue0 carries the dish now on
    // the belt and EventValue1 how many grabs have happened.

    public const int DishCaviar = 1;
    public const int DishSpicySnappy = 2;
    public const int DishJellyLiver = 3;
    public const int DishFriedEel = 4;
    public const int DishSuspiciousCondiment = 5;
    public const int DishClamRoll = 6;
    public const int DishGoldenFysh = 7;
    public const int DishSeapunkSalad = 8;

    /// <summary>The dish currently on the belt.</summary>
    public static int EndlessConveyorDish(RunState state)
    {
        if (state.EventValue0 is null)
        {
            CalculateEndlessConveyorVars(state);
        }

        return state.EventValue0!.Value;
    }

    private static void CalculateEndlessConveyorVars(RunState state)
    {
        state.EventValue1 = 0;
        RollDish(state);
    }

    /// <summary>
    /// EndlessConveyor.RollDish: every fifth grab is a Seapunk Salad outright, and
    /// otherwise a weighted pick over the dishes the run currently qualifies for, minus
    /// whatever was on the belt a moment ago.
    /// </summary>
    /// <remarks>
    /// The forced fifth returns before the roll, so it consumes nothing -- which is why
    /// the stream position is the grab count less the fifths, not the grab count.
    /// </remarks>
    /// <summary>Turn the belt after a grab.</summary>
    public static void RollNextConveyorDish(RunState state) => RollDish(state);

    private static void RollDish(RunState state)
    {
        int grabs = (state.EventValue1 ?? 0) + 1;
        state.EventValue1 = grabs;
        if (grabs % 5 == 0)
        {
            state.EventValue0 = DishSeapunkSalad;
            return;
        }

        int lastDish = state.EventValue0 ?? 0;
        var dishes = new List<(int Dish, float Weight)>
        {
            (DishCaviar, 6f),
            (DishSpicySnappy, 3f),
            (DishJellyLiver, 3f),
            (DishFriedEel, 3f),
        };
        if (state.UsablePotionSlots().Any(potion => potion == 0))
        {
            dishes.Add((DishSuspiciousCondiment, 3f));
        }

        if (state.PlayerHp != state.PlayerMaxHp)
        {
            dishes.Add((DishClamRoll, 6f));
        }

        if (grabs > 1)
        {
            dishes.Add((DishGoldenFysh, 1f));
        }

        dishes.RemoveAll(dish => dish.Dish == lastDish);

        // The event's stream is persistent now, so it is already where this roll belongs.
        // This used to fast-forward a freshly built stream to grabs-minus-the-fifths --
        // the fifth grab returns above without drawing -- which became a REWIND as soon
        // as anything else drew from the event, and threw.
        var rng = EventRng(state, "ENDLESS_CONVEYOR");

        float total = dishes.Sum(dish => dish.Weight);
        float roll = (float)rng.NextDouble() * total;
        float running = 0f;
        foreach (var (dish, weight) in dishes)
        {
            running += weight;
            if (roll < running)
            {
                state.EventValue0 = dish;
                return;
            }
        }

        state.EventValue0 = dishes[^1].Dish;
    }

    /// <summary>
    /// Eat what was grabbed, then let the belt turn. Returns false when the dish needs a
    /// screen the caller has to open.
    /// </summary>
    public static void ApplyEndlessConveyorDish(RunState state, int dish)
    {
        switch (dish)
        {
            case DishClamRoll:
                state.PlayerHp = Math.Min(state.PlayerMaxHp, state.PlayerHp + 10);
                break;
            case DishCaviar:
                GainMaxHp(state, 4);
                break;
            case DishGoldenFysh:
                GainGold(state, 75);
                break;
            case DishSeapunkSalad:
                AddCardToDeck(state, new CardInstance(NamedCard("FeedingFrenzy"), Upgraded: false));
                break;
            case DishFriedEel:
                AddCardToDeck(
                    state,
                    new CardInstance(
                        RunRewardGenerator.GenerateEventOfferCards(
                            state,
                            1,
                            RunRewardGenerator.ColorlessRewardPool
                        )[0],
                        Upgraded: false
                    )
                );
                break;
            case DishSpicySnappy:
                UpgradeRandomCard(state, "ENDLESS_CONVEYOR");
                break;
            case DishSuspiciousCondiment:
                state.PendingPotionRewards.Add(
                    RunRewardGenerator.NextUnlockedPotion(state, state.PlayerRng.Rewards)
                );
                break;
            default:
                break;
        }
    }

    private static void CalculateSunkenTreasuryVars(RunState state)
    {
        GameRng rng = EventRng(state, "SUNKEN_TREASURY");
        state.EventValue0 = 60 + rng.NextInt(16) - 8;
        state.EventValue1 = 333 + rng.NextInt(61) - 30;
    }

    /// <summary>
    /// An event's own stream. EventModel seeds it with
    /// <c>Rng.Seed + (IsShared ? 0 : GetPlayerSlotIndex(Owner)) + hash(Id.Entry)</c>,
    /// and a solo run's only player is slot 0 either way -- so the term is zero, not
    /// one. It was one here, which is the same off-by-one that had Neow offering the
    /// wrong relics and the player rng set reading the wrong stream. Sunken Treasury's
    /// chests paid 67 and 343 where the game pays 63 and 340.
    /// </summary>
    /// <summary>An event's own stream, for callers outside this class.</summary>
    public static GameRng EventStream(RunState state, string eventEntry) =>
        EventRng(state, eventEntry);

    /// <summary>
    /// The event's own Rng stream -- one per event, not one per draw.
    /// </summary>
    /// <remarks>
    /// An event holds a single <c>base.Rng</c>, so its draws are a sequence: the Endless
    /// Conveyor rolls its dish in CalculateVars and then picks the card Observe the Chef
    /// upgrades, and that pick reads the SECOND value. Building a fresh stream per call
    /// handed it the first, which upgraded the wrong card against a live capture. Keyed
    /// by entry so changing events starts a new stream rather than inheriting one.
    /// </remarks>
    private static GameRng EventRng(RunState state, string eventEntry)
    {
        if (state.EventRngStream is not null && state.EventRngName == eventEntry)
        {
            return state.EventRngStream;
        }

        ulong eventSeed = unchecked(
            state.Rng.Seed + DeterministicHash.GetDeterministicHashCode(eventEntry)
        );
        state.EventRngName = eventEntry;
        state.EventRngStream = new GameRng(eventSeed);
        return state.EventRngStream;
    }

    /// <summary>
    /// An ancient's three blessings: one drawn from each of its pools, off the ancient's
    /// own Rng, in pool order.
    /// </summary>
    /// <remarks>
    /// All three of Hive's ancients share this shape and differ only in their pools and
    /// in which entries are CONDITIONAL on the run. Neow does not come through here — it
    /// has its own generator, and a different shape (a curse plus a shuffled positive
    /// list) rather than three pools.
    /// </remarks>
    public static int[] GenerateAncientOptions(RunState state, string ancient)
    {
        var rng = EventStream(state, ancient);
        return ancient switch
        {
            RunConstants.AncientDarv => DarvOptions(state, rng),
            RunConstants.AncientOrobas => OrobasOptions(state, rng),
            RunConstants.AncientPael => PaelOptions(state, rng),
            RunConstants.AncientTezcatara => TezcataraOptions(state, rng),
            RunConstants.AncientTanx => TanxOptions(state, rng),
            RunConstants.AncientNonupeipe => NonupeipeOptions(state, rng),
            RunConstants.AncientVakuu => VakuuOptions(rng),
            _ => [],
        };
    }

    /// <summary>
    /// v0.111.0 Vakuu.GenerateInitialOptions: each of the three pools is copied in its
    /// declared order and UnstableShuffle'd WHOLE -- Pool1, then Pool2, then Pool3 -- and
    /// the first of each is offered. That is Count - 1 draws per pool, 2 + 2 + 3 = 7,
    /// not one pick per pool. Sere Talon's `ThatDecreasesMaxHp(9m)` only marks the option
    /// as potentially lethal; its loss is the relic's own AfterObtained.
    /// </summary>
    private static int[] VakuuOptions(GameRng rng)
    {
        string[][] pools =
        [
            ["BloodSoakedRose", "WhisperingEarring", "Fiddle"],
            ["PreservedFog", "SereTalon", "DistinguishedCape"],
            ["ChoicesParadox", "MusicBox", "LordsParasol", "JeweledMask"],
        ];
        var offered = new int[pools.Length];
        for (int i = 0; i < pools.Length; i++)
        {
            var pool = pools[i]
                .Select(name =>
                    GeneratedData.Relics.FindId(name)
                    ?? throw new InvalidOperationException($"Missing Vakuu relic {name}")
                )
                .ToList();
            rng.Shuffle(pool);
            offered[i] = pool[0];
        }

        return offered;
    }

    private static int[] NonupeipeOptions(RunState state, GameRng rng)
    {
        // v0.111.0 Nonupeipe.GenerateInitialOptions: preserve pool order before
        // UnstableShuffle, including the conditional Beautiful Bracelet at the end.
        string[] names =
        [
            "BlessedAntler",
            "BrilliantScarf",
            "DelicateFrond",
            "DiamondDiadem",
            "FurCoat",
            "Glitter",
            "JewelryBox",
            "LoomingFruit",
            "SignetRing",
        ];
        var options = names
            .Select(name =>
                GeneratedData.Relics.FindId(name)
                ?? throw new InvalidOperationException($"Missing Nonupeipe relic {name}")
            )
            .ToList();
        if (state.Deck.Count(card => Enchantments.CanEnchant(card, Enchantment.Swift)) >= 4)
            options.Add(GeneratedData.Relics.FindId("BeautifulBracelet")!.Value);
        rng.Shuffle(options);
        return options.Take(3).ToArray();
    }

    private static int[] TanxOptions(RunState state, GameRng rng)
    {
        // v0.111.0 Tanx.BaseOptionPool order is part of UnstableShuffle's input.
        string[] names =
        [
            "Claws",
            "Crossbow",
            "IronClub",
            "MeatCleaver",
            "Sai",
            "SpikedGauntlets",
            "TanxsWhistle",
            "ThrowingAxe",
            "WarHammer",
        ];
        var options = names
            .Select(name =>
                GeneratedData.Relics.FindId(name)
                ?? throw new InvalidOperationException($"Missing Tanx relic {name}")
            )
            .ToList();
        if (state.Deck.Count(card => Enchantments.CanEnchant(card, Enchantment.Instinct)) >= 3)
            options.Add(RunConstants.RelicTriBoomerang);
        rng.Shuffle(options);
        return options.Take(3).ToArray();
    }

    /// <summary>
    /// Orobas spends TWO draws before its pools: one picking a character other than the
    /// player's, to brand a Sea Glass with, and one deciding whether pool 1 gets the Sea
    /// Glass or a Prismatic Gem instead — <c>NextFloat() &lt; 1/3</c> for the gem.
    /// </summary>
    /// <remarks>
    /// The list the character is drawn from is `UnlockState.Characters` minus the
    /// player's own, which is four on a mature profile. `NextItem` over an empty list
    /// returns null WITHOUT a draw and Orobas falls back to the player's own character,
    /// which `SeaGlassCharacter = -1` already means.
    /// </remarks>
    private static int[] OrobasOptions(RunState state, GameRng rng)
    {
        // The character is kept, not just spent: if the Sea Glass is offered AND taken,
        // its cards come from that character's pool. It is stored as an index into the
        // full other-character list, so a partial unlock keeps the same meaning.
        string own = CharacterCatalog.Entry(state.Character);
        string[] others = [.. RunConstants.AllCharacterEntries.Where(entry => entry != own)];
        int[] unlockedOthers =
        [
            .. Enumerable
                .Range(0, others.Length)
                .Where(i => state.Progression.IsCharacterUnlocked(others[i])),
        ];
        state.SeaGlassCharacter = unlockedOthers.Length == 0 ? -1 : rng.NextItem(unlockedOthers);
        bool prismaticGem = rng.NextFloat() < 0.3333333f;

        var pool1 = RunConstants.OrobasPool1.ToArray().ToList();
        pool1.Add(prismaticGem ? RunConstants.RelicPrismaticGemOption : RunConstants.RelicSeaGlass);

        // `OptionPool3`: Touch of Orobas only if `SetupForPlayer` finds a Starter-rarity
        // relic, Archaic Tooth only if it finds a Transcendence starter card in the deck
        // -- a removed Bash takes the Tooth off the table. With neither, the pool is the
        // `OPTION_POOL_3_LOCKED` option, which has no callback: an empty slot (0) here,
        // still after its one draw.
        bool hasStarterRelic = state.Relics.Any(relic =>
            GeneratedData.Relics.Get(relic.DefId).Rarity == RelicRarity.Starter
        );
        bool hasTranscendenceCard = state.Deck.Any(card =>
            RunConstants.TranscendenceUpgrades.Any(pair => pair.Starter == card.DefId)
        );
        // Filter the audited ordered pool instead of rebuilding its membership here.
        var declaredPool3 = RunConstants.OrobasPool3.ToArray();
        var pool3 = declaredPool3
            .Where(relic =>
                relic switch
                {
                    RunConstants.RelicTouchOfOrobas => hasStarterRelic,
                    RunConstants.RelicArchaicTooth => hasTranscendenceCard,
                    _ => false,
                }
            )
            .ToList();
        if (pool3.Count == 0)
        {
            pool3.Add(0);
        }

        return
        [
            rng.NextItem(pool1),
            rng.NextItem(RunConstants.OrobasPool2.ToArray()),
            rng.NextItem(pool3),
        ];
    }

    /// <summary>
    /// Pael's second pool is a weighting trick: the conditional entries are added, then
    /// <c>list.AddRange(list)</c> DOUBLES everything so far, and only then is Growth
    /// appended — so Growth is half as likely as anything else in the pool.
    /// </summary>
    private static int[] PaelOptions(RunState state, GameRng rng)
    {
        var pool2 = RunConstants.PaelPool2.ToArray().ToList();
        if (state.Deck.Count(CanTakeAnEnchantment) >= 3)
        {
            pool2.Add(RunConstants.RelicPaelsClaw);
        }

        if (state.Deck.Count(IsRemovableCard) >= 5)
        {
            pool2.Add(RunConstants.RelicPaelsTooth);
        }

        pool2.AddRange(pool2);
        pool2.Add(RunConstants.RelicPaelsGrowth);

        var pool3 = RunConstants.PaelPool3.ToArray().ToList();
        if (!HasEventPet(state))
        {
            pool3.Add(RunConstants.RelicPaelsLegion);
        }

        return
        [
            rng.NextItem(RunConstants.PaelPool1.ToArray()),
            rng.NextItem(pool2),
            rng.NextItem(pool3),
        ];
    }

    /// <summary>
    /// `Player.HasEventPet`: a relic with `AddsPet` (Byrdpip and Pael's Legion are the
    /// only two in v0.111.0) or a Byrdonis Egg still in the deck. Byrdonis Nest is an
    /// act 1 event, so a pet can reach Pael.
    /// </summary>
    private static bool HasEventPet(RunState state) =>
        state.Relics.Any(relic =>
            relic.DefId is Effects.RelicEffects.Byrdpip or RunConstants.RelicPaelsLegion
        ) || state.Deck.Any(card => card.DefId == ByrdonisEggCard);

    /// <summary>
    /// Tezcatara adds Nutritious Soup to its first pool when the deck holds a BASIC
    /// Strike — which every starting deck does, and keeps doing unless every Strike is
    /// removed or transformed.
    /// </summary>
    /// <summary>
    /// Darv: one relic from each set whose filter passes, shuffled, and the first three --
    /// or the first two plus Dusty Tome, on a coin flip.
    /// </summary>
    /// <remarks>
    /// The draw order is what makes this reproducible, and it is not obvious: ONE
    /// `NextItem` per surviving set FIRST (even for the seven sets that hold a single
    /// relic, which still spend a draw), then the shuffle, then `NextBool` for the tome.
    /// Reordering any of those gives a different three from the same seed.
    ///
    /// `UnstableShuffle` is Fisher-Yates over the list as built, so the set order in
    /// `DarvSingleRelicSets` is part of the answer.
    ///
    /// Pandora's Box drops out for a run whose modifiers clear the deck; the emulator
    /// models no such modifier, so its filter is always true here and is written as a
    /// comment rather than a condition that can never fire.
    /// </remarks>
    private static int[] DarvOptions(RunState state, GameRng rng)
    {
        var offered = new List<int>();
        foreach (int relicId in RunConstants.DarvSingleRelicSets)
        {
            // `Rng.NextItem` over a one-element array is still a draw.
            offered.Add(rng.NextItem(new[] { relicId }));
        }

        // v0.111.0 Darv declares these as FOUR separate singleton sets, not two
        // two-item alternatives. Each eligible relic consumes its own NextItem draw.
        if (state.CurrentActIndex == 1)
        {
            foreach (int relicId in RunConstants.DarvActOneSet)
            {
                offered.Add(rng.NextItem(new[] { relicId }));
            }
        }

        if (state.CurrentActIndex >= 1)
        {
            foreach (int relicId in RunConstants.DarvActTwoSet)
            {
                offered.Add(rng.NextItem(new[] { relicId }));
            }
        }

        rng.Shuffle(offered);

        state.DustyTomeAncientCard = -1;
        if (rng.NextBool())
        {
            // `dustyTome.SetupForPlayer(Owner)` runs while the offer is built, so its
            // `PlayerRng.Rewards` draw is spent whether or not the Tome is taken.
            state.DustyTomeAncientCard = DustyTomeCard(state);
            return [.. offered.Take(2), RunConstants.DarvDustyTome];
        }

        return [.. offered.Take(3)];
    }

    private static int[] TezcataraOptions(RunState state, GameRng rng)
    {
        var pool1 = RunConstants.TezcataraPool1.ToArray().ToList();
        if (state.Deck.Any(IsBasicStrike))
        {
            pool1.Add(RunConstants.RelicNutritiousSoup);
        }

        return
        [
            rng.NextItem(pool1),
            rng.NextItem(RunConstants.TezcataraPool2.ToArray()),
            rng.NextItem(RunConstants.TezcataraPool3.ToArray()),
        ];
    }

    /// <summary>
    /// <c>c.Tags.Contains(CardTag.Strike) &amp;&amp; c.Rarity == Basic</c>. Card tags are
    /// not extracted, so this reads the entry name instead — every Strike-tagged card the
    /// game has is named for it, and at Basic rarity there is only the starter Strike.
    /// </summary>
    /// <summary>
    /// Nutritious Soup: `c.Rarity == Basic && c.Tags.Contains(CardTag.Strike)`. The tag,
    /// not a "STRIKE" substring in the entry -- the two agree among Basic cards, which is
    /// why the stand-in survived, and the tags are extracted now (E358).
    /// </summary>
    private static bool IsBasicStrike(CardInstance card)
    {
        var def = GeneratedData.Cards.Get(Math.Abs(card.DefId));
        return def.Rarity == CardRarity.Basic && def.StrikeTag;
    }

    /// <summary>
    /// `Goopy.CanEnchant`, which Pael's Claw counts against a threshold of three.
    /// </summary>
    /// <remarks>
    /// This used to be `not (Curse or Status)` behind a comment saying the emulator had no
    /// Goopy enchantment at all. It has one, and the comment outlived that: Goopy takes
    /// Defend-TAGGED cards, at any rarity, which is a far narrower set than "not a curse".
    /// The threshold made the difference unreachable -- every deck a run can hold clears
    /// three on its starting cards -- but an approximation nobody can trip is still an
    /// approximation, and it disagreed with the enchantment it was standing in for.
    /// </remarks>
    private static bool CanTakeAnEnchantment(CardInstance card) =>
        Enchantments.CanEnchant(card, Enchantment.Goopy);

    /// <summary>
    /// The same Eternal-aware rule used by removal screens and Slippery Bridge.
    /// </summary>
    private static bool IsRemovableCard(CardInstance card) => IsRemovable(card);

    /// <summary>
    /// Enchant every card in the deck that will take it — the shape Pael's Claw and
    /// Nutritious Soup share, where the relic offers no choice at all.
    /// </summary>
    private static void EnchantEveryCard(
        RunState state,
        Enchantment enchantment,
        int amount,
        Func<CardInstance, bool>? extraFilter = null
    )
    {
        for (int i = 0; i < state.Deck.Count; i++)
        {
            var card = state.Deck[i];
            if (!Enchantments.CanEnchant(card, enchantment))
            {
                continue;
            }

            if (extraFilter is not null && !extraFilter(card))
            {
                continue;
            }

            state.Deck[i] = card with { Enchantment = enchantment, EnchantAmount = amount };
        }
    }

    /// <summary>
    /// `PlayerCmd.GainGold`. The single place run-side gold is gained, because
    /// `AfterGoldGained` is a hook and a `Gold +=` cannot dispatch one.
    ///
    /// The early return is the game's, and it is load-bearing: the amount is modified
    /// FIRST and a non-positive result returns before `AfterGoldGained`. Ectoplasm zeroes
    /// the amount, so an Ectoplasm owner never fires the hook at all -- Dragon Fruit next
    /// to Ectoplasm gains no max HP, rather than gaining it off gold it did not receive.
    /// `LoseGold` is a different command and fires nothing.
    /// </summary>
    public static void GainGold(RunState state, int amount) => GainGold(state, (decimal)amount);

    /// <summary>
    /// The command's own signature: `GainGold(decimal amount, ...)` modifies the decimal
    /// and only then does `player.Gold += (int)amount`, so a fractional source (Jungle
    /// Maze's purses) keeps its fraction through Bowler Hat's multiply.
    /// </summary>
    public static void GainGold(RunState state, decimal amount)
    {
        amount = Effects.RelicEffects.ModifyGoldGained(state.Relics, amount);
        if (!(amount > 0m))
        {
            return;
        }

        state.Gold += (int)amount;
        Effects.RelicEffects.ApplyAfterGoldGained(state);
    }

    /// <summary>
    /// Whether the emulator models this potion's `OnUse` out of combat. Only
    /// `PotionUsage.AnyTime` potions can be used there (`NPotionPopup.RefreshButtons`);
    /// Foul Potion's shop branch lives in the shop itself.
    /// </summary>
    public static bool HasOutOfCombatUse(int potionId) =>
        potionId != 0
        && GeneratedData.Potions.Get(potionId).Usage == PotionUsage.AnyTime
        && (
            potionId == Effects.PotionEffects.FruitJuiceId
            || potionId == Effects.PotionEffects.AmbergrisId
            || potionId == Effects.PotionEffects.BloodPotionId
        );

    /// <summary>
    /// An AnyTime potion's `OnUse` with no combat in progress. The slot is already empty
    /// (`RemoveBeforeUse`). Blood Potion heals `MaxHp * 20 / 100`. Fruit Juice is `CreatureCmd.GainMaxHp(5)`, which heals the
    /// gain; Ambergris heals `MaxHp * 50 / 100` and applies no power because
    /// `CombatManager.IsInProgress` is false. `Heal` dispatches `AfterCurrentHpChanged`
    /// only with a combat state, and the potion-use relics act only in combat.
    /// </summary>
    public static void UsePotionOutOfCombat(RunState state, int potionId)
    {
        if (potionId == Effects.PotionEffects.FruitJuiceId)
        {
            GainMaxHp(state, Effects.PotionEffects.FruitJuiceMaxHp);
        }
        else if (potionId == Effects.PotionEffects.AmbergrisId)
        {
            state.PlayerHp = Math.Min(
                state.PlayerMaxHp,
                state.PlayerHp + Effects.PotionEffects.AmbergrisHealAmount(state.PlayerMaxHp)
            );
        }
        else if (potionId == Effects.PotionEffects.BloodPotionId)
        {
            // BloodPotion.OnUse has no combat branch: the same Heal as in combat, with
            // no AfterCurrentHpChanged because there is no combat state.
            state.PlayerHp = Math.Min(
                state.PlayerMaxHp,
                state.PlayerHp + Effects.PotionEffects.BloodPotionHealAmount(state.PlayerMaxHp)
            );
        }
    }

    /// <summary>
    /// The out-of-combat <c>CreatureCmd.Damage</c> on the player: every event and relic
    /// pickup that costs HP (This or That, Precarious Shears, the follow-up of a Hug...).
    /// The command runs `Hook.ModifyHpLost` out of combat too, so a held Tungsten Rod
    /// trims every one of them: live, This or That's 6 took 76 to 71.
    /// </summary>
    public static void DamagePlayer(RunState state, int amount)
    {
        amount = Effects.RelicEffects.ModifyHpLostOutOfCombat(state.Relics, amount);
        state.PlayerHp = Math.Max(0, state.PlayerHp - amount);
    }

    public static void GainMaxHp(RunState state, int amount)
    {
        state.PlayerMaxHp += amount;
        state.PlayerHp = Math.Min(state.PlayerMaxHp, state.PlayerHp + amount);
    }

    /// <summary>
    /// The game's <c>CreatureCmd.LoseMaxHp</c>: current HP is only damaged by the amount
    /// the new maximum falls BELOW it, and the maximum itself never goes under 1. A
    /// player already below the new cap keeps the HP they had.
    /// </summary>
    public static void LoseMaxHp(RunState state, int amount)
    {
        int newMaxHp = Math.Max(1, state.PlayerMaxHp - amount);
        state.PlayerHp = Math.Min(state.PlayerHp, newMaxHp);
        state.PlayerMaxHp = newMaxHp;
    }

    /// <summary>
    /// Doors of Light and Dark's Light door:
    /// <c>Deck.Cards.Where(IsUpgradable).StableShuffle(base.Rng).Take(Cards)</c>.
    /// </summary>
    /// <remarks>
    /// Two things here are the whole of it, and the emulator had both wrong. The stream is
    /// the EVENT's own <c>base.Rng</c>, not <c>Rng.Niche</c> -- the same mistake as E14,
    /// where every event's transform was rolled off Niche. And <c>StableShuffle</c> sorts
    /// by ModelId before it shuffles, which compares the slugified class name as an
    /// ordinal string; sorting by the emulator's own numeric ids puts a different card
    /// under the same draw. A live capture upgraded Whirlwind where this took Shrug It Off.
    /// </remarks>
    /// <summary>
    /// <c>Deck.Where(IsUpgradable).StableShuffle(rng).Take(count)</c>, upgraded.
    /// </summary>
    /// <remarks>
    /// The two things that matter are the STREAM, which differs per caller, and the sort:
    /// <c>StableShuffle</c> orders by ModelId — the slugified class name, compared
    /// ordinally — before it shuffles, so sorting by the emulator's own numeric ids puts a
    /// different card under the same draw. Both were wrong in the Light Door once (E40).
    /// </remarks>
    /// <summary>
    /// `WarHammer.AfterCombatVictory(room.RoomType == Elite)`: `CardsVar(4)` upgradable
    /// deck cards, `StableShuffle(Rng.Niche).Take(4)`.
    /// </summary>
    /// <remarks>
    /// Takes `wasElite` rather than reading the room, for the same reason
    /// `CountEliteVictoryForSwordOfStone` does: the gate is the interesting half and a
    /// parameter is the only way to test both sides of it. On the VICTORY, so fleeing or
    /// dying in an elite pays nothing.
    /// </remarks>
    public static void ApplyWarHammerOnVictory(RunState state, bool wasElite)
    {
        if (wasElite && Effects.RelicEffects.Has(state.Relics, Effects.RelicEffects.WarHammer))
        {
            UpgradeRandomCards(state, 4, state.Rng.Niche);
        }
    }

    public static void UpgradeRandomCards(RunState state, int count, GameRng rng)
    {
        var indexes = state
            .Deck.Select((card, index) => (card, index))
            .Where(item => RunConstants.IsRunCardUpgradable(item.card))
            .Select(item => item.index)
            .ToList();
        SortDeckIndexesForStableShuffle(state, indexes);
        rng.Shuffle(indexes);
        foreach (int index in indexes.Take(count))
        {
            state.Deck[index] = state.Deck[index] with { Upgraded = true };
        }
    }

    /// <summary>
    /// Reflections' Touch a Mirror: downgrade up to two upgraded cards, then upgrade up
    /// to four upgradable ones.
    /// </summary>
    /// <remarks>
    /// The upgradable list is derived AFTER the downgrades, not alongside them, so the
    /// two cards the mirror just knocked down are candidates to come straight back up.
    /// Each pick is <c>Rng.NextItem</c> over the remaining list with the pick removed --
    /// a draw per card, and only while the list has something in it, which is why both
    /// loops break rather than clamp.
    /// </remarks>
    public static void ReflectionsTouchAMirror(RunState state)
    {
        var rng = EventRng(state, "REFLECTIONS");

        var upgraded = Enumerable
            .Range(0, state.Deck.Count)
            .Where(index => state.Deck[index].Upgraded)
            .ToList();
        for (int i = 0; i < 2 && upgraded.Count > 0; i++)
        {
            int index = rng.NextItem(upgraded);
            upgraded.Remove(index);
            state.Deck[index] = state.Deck[index] with { Upgraded = false };
        }

        var upgradable = Enumerable
            .Range(0, state.Deck.Count)
            .Where(index => RunConstants.IsRunCardUpgradable(state.Deck[index]))
            .ToList();
        for (int i = 0; i < 4 && upgradable.Count > 0; i++)
        {
            int index = rng.NextItem(upgradable);
            upgradable.Remove(index);
            state.Deck[index] = state.Deck[index] with { Upgraded = true };
        }
    }

    /// <summary>
    /// Reflections' Shatter: a copy of every card in the deck, then a Bad Luck.
    /// </summary>
    /// <remarks>
    /// The loop runs to the deck's size as it was BEFORE any copy was added, which is
    /// what stops it copying its own copies forever. CloneCard preserves the card as it
    /// stands -- upgrades and enchantment included -- and costs no draws.
    /// </remarks>
    public static void ReflectionsShatter(RunState state)
    {
        int originalDeckSize = state.Deck.Count;
        for (int i = 0; i < originalDeckSize; i++)
        {
            // Not AddCardToDeck: this is CloneCard, so the egg relics do not get a second
            // look at a card that is already in the deck.
            state.Deck.Add(state.Deck[i]);
        }

        AddCardToDeck(state, new CardInstance(RunConstants.CardBadLuck, Upgraded: false));
    }

    /// <summary>
    /// Tinker Time's second page: two of Attack, Skill and Power.
    /// </summary>
    /// <remarks>
    /// <c>TakeRandom(2, Rng)</c> is <c>UnstableShuffle(rng).Take(2)</c>, so all three are
    /// permuted -- two draws for a list of three -- and the first two are shown. Taking
    /// only the draws the two shown cards need would leave the rider page reading the
    /// stream one value early.
    /// </remarks>
    public static void BeginTinkerCardTypePage(RunState state)
    {
        var types = new List<int>
        {
            (int)CardType.Attack,
            (int)CardType.Skill,
            (int)CardType.Power,
        };
        EventRng(state, "TINKER_TIME").Shuffle(types);
        state.EventRandomOffer = [.. types.Take(2)];
        state.EventPage = 1;
    }

    /// <summary>
    /// Tinker Time's third page: two of the three riders that belong to the chosen type.
    /// </summary>
    public static void BeginTinkerRiderPage(RunState state)
    {
        var riders = state.TinkerCardType switch
        {
            CardType.Attack => new List<int>
            {
                (int)TinkerRider.Sapping,
                (int)TinkerRider.Violence,
                (int)TinkerRider.Choking,
            },
            CardType.Skill => new List<int>
            {
                (int)TinkerRider.Energized,
                (int)TinkerRider.Wisdom,
                (int)TinkerRider.Chaos,
            },
            _ => new List<int>
            {
                (int)TinkerRider.Expertise,
                (int)TinkerRider.Curious,
                (int)TinkerRider.Improvement,
            },
        };
        // The same stream the type page used: one event, one Rng.
        EventRng(state, "TINKER_TIME").Shuffle(riders);
        state.EventRandomOffer = [.. riders.Take(2)];
        state.EventPage = 2;
    }

    public static void UpgradeTwoRandomCardsForLightDoor(RunState state)
    {
        var indexes = state
            .Deck.Select((card, index) => (card, index))
            .Where(item => RunConstants.IsRunCardUpgradable(item.card))
            .Select(item => item.index)
            .ToList();
        SortDeckIndexesForStableShuffle(state, indexes);
        EventRng(state, "DOORS_OF_LIGHT_AND_DARK").Shuffle(indexes);
        foreach (int index in indexes.Take(2))
        {
            state.Deck[index] = state.Deck[index] with { Upgraded = true };
        }
    }

    public static void TransformCardAt(RunState state, int deckIndex, GameRng rng)
    {
        if ((uint)deckIndex >= (uint)state.Deck.Count)
        {
            return;
        }

        int originalId = Math.Abs(state.Deck[deckIndex].DefId);
        // What a card can become depends on what it is: the options come from the
        // original's own pool, narrowed to the rarities a run is handed, with the
        // original and the multiplayer-only cards dropped.
        var pool = RunRewardGenerator.TransformOptionsFor(originalId, state.Progression);
        if (pool.Length == 0)
        {
            return;
        }

        state.Deck.RemoveAt(deckIndex);
        AddCardToDeck(state, new CardInstance(rng.NextItem(pool), Upgraded: false));
    }

    /// <summary>
    /// Leafy Poultice: the first BASIC card carrying `CardTag.Strike`, or `CardTag.Defend`.
    /// </summary>
    private static void TransformFirstTaggedBasic(RunState state, bool strike)
    {
        int index = state.Deck.FindIndex(card =>
        {
            var def = GeneratedData.Cards.Get(card.DefId);
            return def.Rarity == CardRarity.Basic && (strike ? def.StrikeTag : def.DefendTag);
        });
        if (index >= 0)
        {
            TransformCardAt(state, index, state.PlayerRng.Transformations);
        }
    }

    public static void TransformFirstCardMatching(RunState state, int cardId)
    {
        int index = state.Deck.FindIndex(card => Math.Abs(card.DefId) == cardId);
        if (index >= 0)
        {
            TransformCardAt(state, index, state.PlayerRng.Transformations);
        }
    }

    /// <summary>
    /// `PandorasBox.AfterObtained`: every BASIC Strike or Defend the deck holds that is
    /// removable, transformed off `Rng.Niche`.
    /// </summary>
    private static void PandorasBoxTransform(RunState state)
    {
        // The set is taken FIRST, then transformed. `TransformCardAt` removes the card and
        // appends the replacement, so a forward walk over a shifting list skips half of
        // them -- and the appended replacements are themselves candidates for the walk to
        // find, which a `where` over the live deck would keep transforming.
        var targets = state
            .Deck.Where(card =>
            {
                var def = GeneratedData.Cards.Get(card.DefId);
                return def.Rarity == CardRarity.Basic
                    && (def.StrikeTag || def.DefendTag)
                    && IsRemovableCard(card);
            })
            .ToList();

        foreach (var target in targets)
        {
            int index = state.Deck.IndexOf(target);
            if (index >= 0)
            {
                TransformCardAt(state, index, state.Rng.Niche);
            }
        }
    }

    /// <summary>
    /// `DustyTome.SetupForPlayer`: an ANCIENT-rarity card from the player's own pool,
    /// minus Archaic Tooth's transcendence cards, off `PlayerRng.Rewards`.
    /// </summary>
    /// <remarks>
    /// The Ironclad's Ancient cards are Break and Corruption, and Break is one of the
    /// eleven transcendence cards -- so the filtered list is a single card and the draw
    /// always lands on Corruption. The draw is still made: skipping it would shift every
    /// later value on the Rewards stream.
    /// </remarks>
    private static int DustyTomeCard(RunState state)
    {
        var candidates = CharacterCatalog
            .CardPool(state.Character)
            .ToArray()
            .Where(id =>
                GeneratedData.Cards.Get(id).Rarity == CardRarity.Ancient
                && !RunConstants.TranscendenceCardNames.Contains(GeneratedData.Cards.Get(id).Name)
            )
            .ToArray();
        return candidates.Length == 0
            ? RandomRewardCard(state, state.Rng.UpFront)
            : state.PlayerRng.Rewards.NextItem(candidates);
    }

    private static void TransformAllMatching(RunState state, int cardId)
    {
        for (int i = 0; i < state.Deck.Count; i++)
        {
            if (Math.Abs(state.Deck[i].DefId) == cardId)
            {
                TransformCardAt(state, i, state.PlayerRng.Transformations);
            }
        }
    }

    /// <summary>
    /// Neow's Talisman: the LAST BASIC card carrying `CardTag.Strike`, or `CardTag.Defend`.
    /// </summary>
    private static void UpgradeLastTaggedBasic(RunState state, bool strike)
    {
        for (int i = state.Deck.Count - 1; i >= 0; i--)
        {
            var def = GeneratedData.Cards.Get(state.Deck[i].DefId);
            if (def.Rarity != CardRarity.Basic || (strike ? !def.StrikeTag : !def.DefendTag))
            {
                continue;
            }

            state.Deck[i] = state.Deck[i] with { Upgraded = true };
            return;
        }
    }

    private static void UpgradeLastCardMatching(RunState state, int cardId)
    {
        for (int i = state.Deck.Count - 1; i >= 0; i--)
        {
            if (state.Deck[i].DefId != cardId || !RunConstants.IsRunCardUpgradable(state.Deck[i]))
            {
                continue;
            }

            state.Deck[i] = state.Deck[i] with { Upgraded = true };
            return;
        }
    }

    private static int RandomRewardCard(RunState state, GameRng rng) =>
        rng.NextItem(RunRewardGenerator.PlayerCardPool(state).ToArray());

    private static void AddRandomRewardCard(RunState state, GameRng rng)
    {
        AddCardToDeck(state, new CardInstance(RandomRewardCard(state, rng), Upgraded: false));
    }
}
