using Sts2Emulator.Core.Rng;

namespace Sts2Emulator.Core.Run;

public static class RunRewardGenerator
{
    private const int RarityCommon = 1;
    private const int RarityUncommon = 2;
    private const int RarityRare = 3;
    public const float CardRarityBaseOffset = -0.05f;
    private const float CardRarityMaxOffset = 0.4f;

    private static float CardRarityGrowth(RunState state) =>
        Ascension.Has(state.AscensionLevel, Ascension.Scarcity) ? 0.005f : 0.01f;

    private const float PotionRewardStep = 0.1f;

    /// <summary>
    /// The Ironclad card pool, straight from the game's own IroncladCardPool
    /// declaration. A hand-written copy of it used to live here, and it was wrong in
    /// both directions: it carried three Colorless cards (Restlessness, Splash,
    /// Ultimate Defend) and was missing six Ironclad ones. Every extra entry shifts
    /// the index NextItem lands on, so a reward roll that agreed with the game on
    /// rarity still handed back the neighbouring card.
    /// </summary>
    public static ReadOnlySpan<int> IroncladRewardPool => GeneratedData.CardPools.Ironclad;

    public static ReadOnlySpan<int> PlayerCardPool(RunState state) =>
        CharacterCatalog.CardPool(state.Character);

    /// <summary>
    /// What a card may be transformed into, given what it is now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CardFactory.GetDefaultTransformationOptions draws from the ORIGINAL card's own
    /// pool -- an Ironclad card becomes an Ironclad card, a curse becomes a curse --
    /// falling back to Colorless only for the rarities that have no pool of their own
    /// (Ancient, Event, Token) and for Quest cards. It then keeps Common, Uncommon and
    /// Rare, drops the original itself, and drops multiplayer-only cards.
    /// </para>
    /// <para>
    /// The rarity filter is skipped when the original is a Status or a Curse, which is
    /// how a curse transforms into another curse rather than into nothing at all.
    /// </para>
    /// <para>
    /// One hand-written list used to stand for all of this. It held 83 cards where the
    /// Ironclad case has 80: it carried Break and Corruption, which are Ancient, and
    /// Demonic Shield and Tank, which are multiplayer-only, and it was missing Feed.
    /// </para>
    /// </remarks>
    public static int[] TransformOptionsFor(
        int originalCardId,
        ProgressionContext? progression = null
    )
    {
        progression ??= ProgressionContext.AllUnlocked;
        var original = GeneratedData.Cards.Get(originalCardId);
        bool ownPoolHasNoHome =
            original.Rarity is CardRarity.Ancient or CardRarity.Event or CardRarity.Token;
        int[] pool = ownPoolHasNoHome
            ? GeneratedData.CardPools.Colorless.ToArray()
            : PoolContaining(originalCardId);

        // Status and Curse originals keep the whole pool; everything else is narrowed to
        // the three rarities a run can actually be handed.
        bool keepEveryRarity = original.Rarity is CardRarity.Status or CardRarity.Curse;

        return
        [
            .. pool.Where(cardId =>
                cardId != originalCardId
                && progression.IsCardAvailableInSinglePlayer(GeneratedData.Cards.Get(cardId).Entry)
                && (
                    keepEveryRarity
                    || GeneratedData.Cards.Get(cardId).Rarity
                        is CardRarity.Common
                            or CardRarity.Uncommon
                            or CardRarity.Rare
                )
            ),
        ];
    }

    /// <summary>The pool a card belongs to, which is the pool it transforms within.</summary>
    private static int[] PoolContaining(int cardId)
    {
        foreach (
            var pool in (int[][])
                [
                    GeneratedData.CardPools.Ironclad.ToArray(),
                    GeneratedData.CardPools.Silent.ToArray(),
                    GeneratedData.CardPools.Regent.ToArray(),
                    GeneratedData.CardPools.Necrobinder.ToArray(),
                    GeneratedData.CardPools.Defect.ToArray(),
                    GeneratedData.CardPools.Curse.ToArray(),
                    GeneratedData.CardPools.Status.ToArray(),
                    GeneratedData.CardPools.Colorless.ToArray(),
                ]
        )
        {
            if (pool.Contains(cardId))
            {
                return pool;
            }
        }

        // A card in no pool the emulator models: the Ironclad pool is the only sensible
        // home, and it is what the old single list assumed for everything.
        return GeneratedData.CardPools.Ironclad.ToArray();
    }

    /// <summary>
    /// The Colorless pool, as the game declares it. Callers filter it: the solo filter
    /// runs at pick time, the way CardFactory.FilterForPlayerCount runs on every pool.
    ///
    /// A hand-written copy stood here with 63 entries against the pool's 64 -- it kept
    /// ten multiplayer-only cards the pick would drop anyway and left out Coordinate for
    /// no reason anyone recorded.
    /// </summary>
    public static ReadOnlySpan<int> ColorlessRewardPool => GeneratedData.CardPools.Colorless;

    /// <summary>
    /// What the merchant can stock, by card type. The game does not keep per-type shop
    /// lists at all: MerchantInventory hands CardFactory.CreateForMerchant the player's
    /// whole character pool and it filters by type at pick time, dropping Basic cards
    /// and anything multiplayer-only.
    ///
    /// Hand-written copies of these lists used to live here and they were far short of
    /// the real thing -- 20 attacks, 18 skills and 5 powers against the pool's 35, 28
    /// and 19 -- with three Colorless cards mixed into the skills for good measure. A
    /// short pool does not just narrow the choice: NextItem indexes into it, so every
    /// slot after the first came back with a different card than the game stocked.
    /// </summary>
    private static readonly int[] _shopAttackCards = ShopPoolOfType(CardType.Attack);
    private static readonly int[] _shopSkillCards = ShopPoolOfType(CardType.Skill);
    private static readonly int[] _shopPowerCards = ShopPoolOfType(CardType.Power);

    public static ReadOnlySpan<int> ShopAttackCards => _shopAttackCards;

    public static ReadOnlySpan<int> ShopSkillCards => _shopSkillCards;

    public static ReadOnlySpan<int> ShopPowerCards => _shopPowerCards;

    private static int[] ShopPoolOfType(
        CardType type,
        CharacterId character = CharacterId.Ironclad
    ) =>
        [
            .. CharacterCatalog
                .CardPool(character)
                .ToArray()
                .Where(cardId =>
                {
                    var def = GeneratedData.Cards.Get(cardId);
                    return def.Type == type
                        && def.Rarity != CardRarity.Basic
                        && IsAllowedSolo(cardId);
                }),
        ];

    /// <summary>
    /// What a potion roll may offer: the character's own pool followed by the shared
    /// one, which is how PotionFactory.GetPotionOptions builds it. Order is load-bearing
    /// because NextItem indexes into the concatenation.
    ///
    /// A hand-written list stood here and held 43 of the 48, so a roll that agreed with
    /// the game on rarity still landed on a different potion.
    /// </summary>
    private static readonly int[] _potionRewardPool =
    [
        .. GeneratedData.PotionPools.Ironclad.ToArray(),
        .. GeneratedData.PotionPools.Shared.ToArray(),
    ];

    public static ReadOnlySpan<int> PotionRewardPool => _potionRewardPool;

    /// <summary>
    /// The HP a won or survived combat pays before anything else looks at the room.
    /// </summary>
    /// <remarks>
    /// `CombatManager.EndCombatInternal` runs `Hook.AfterCombatEnd` and then
    /// `Hook.AfterCombatVictory` for every combat the player survives -- an escaped enemy
    /// included -- before rewards and before an event's `Resume`. So a fight an event
    /// resumes after pays these too; it only skips the reward screen. Consumes no RNG.
    /// The order is the one this code has always used (Meat on the Bone is
    /// `AfterCombatVictoryEarly` in the game and has not been reordered here).
    /// </remarks>
    public static void ApplyPostCombatHpEffects(RunState state)
    {
        ApplyChosenCheeseAndBurningBlood(state);
        ApplyBlackBloodAndMeatOnTheBone(state);
    }

    private static void ApplyChosenCheeseAndBurningBlood(RunState state)
    {
        // ChosenCheese.AfterCombatEnd: GainMaxHp(1). Gaining a maximum heals by the same
        // amount, so a fight won at 2 HP ends at 3 before Burning Blood's six -- which is
        // how this surfaced, as a single point of player HP after 126 clean steps of a
        // capture (`NXV45HW43K`). The emulator could already be GIVEN the relic by Room
        // Full of Cheese and then did nothing with it.
        if (HasRelic(state, RunConstants.RelicChosenCheese))
        {
            RunNonCombatEffects.GainMaxHp(state, 1);
        }

        if (HasRelic(state, RunConstants.RelicBurningBlood))
        {
            state.PlayerHp = Math.Min(state.PlayerMaxHp, state.PlayerHp + 6);
        }
    }

    private static void ApplyBlackBloodAndMeatOnTheBone(RunState state)
    {
        if (HasRelic(state, RunConstants.RelicBlackBlood))
        {
            state.PlayerHp = Math.Min(state.PlayerMaxHp, state.PlayerHp + 12);
        }

        if (
            HasRelic(state, RunConstants.RelicMeatOnTheBone)
            && state.PlayerHp <= state.PlayerMaxHp / 2
        )
        {
            state.PlayerHp = Math.Min(state.PlayerMaxHp, state.PlayerHp + 12);
        }
    }

    public static void GenerateCombatRewards(RunState state)
    {
        ApplyChosenCheeseAndBurningBlood(state);

        bool finalActBoss =
            state.LastResolvedRoomType == RunConstants.NodeBoss
            && state.CurrentActIndex >= state.Acts.Count - 1;

        // `RoyaltiesPower.AfterCombatEnd` hangs a `GoldReward(Amount)` on the room. Through
        // `GainGold` like the fight's own gold, so Ectoplasm still zeroes it and Bowler Hat
        // still raises it -- the power pays out through the run's chokepoint, not past it.
        // Preserve its existing position between the HP passes: gold can trigger Dragon
        // Fruit's max-HP gain and affect Meat on the Bone's half-health condition.
        if (state.RoyaltiesGoldOwed > 0)
        {
            RunNonCombatEffects.GainGold(state, state.RoyaltiesGoldOwed);
            state.RoyaltiesGoldOwed = 0;
        }

        ApplyBlackBloodAndMeatOnTheBone(state);

        ClearRewardScreen(state);
        // RewardsSet.WithRewardsForRoom returns before constructing default and
        // room-extra rewards for the final boss. ModifyRewards still runs below.
        if (!finalActBoss)
        {
            bool hasPotionReward = CheckPotionRoll(state);
            state.RewardGold = GoldRewardForCurrentNode(state);
            // A Heist's gold is its own row, claimed separately and shown after the fight's
            // ordinary gold.
            if (state.ActiveCombat?.StolenBackGold > 0)
            {
                state.PendingGoldRewards.Add(state.ActiveCombat.StolenBackGold);
                state.ActiveCombat.StolenBackGold = 0;
            }

            // RunState.AddCard registers the model; it does not add to PileType.Deck.
            // SwipePower creates a SpecialCardReward, claimed separately from the
            // normal three-card offer. An escaped Hopper never produces this reward.
            if (state.ActiveCombat is { StolenBackCards.Count: > 0 } combat)
            {
                foreach (var card in combat.StolenBackCards)
                {
                    state.PendingSpecialCardRewards.Add(card.DeckOrigin?.Card ?? card);
                }

                combat.StolenBackCards.Clear();
            }

            // `RoyaltiesPower.AfterCombatEnd` adds a GoldReward the same way, so it is another
            // row rather than more gold on the fight's own.
            if (state.ActiveCombat?.RoyaltiesGold > 0)
            {
                state.PendingGoldRewards.Add(state.ActiveCombat.RoyaltiesGold);
                state.ActiveCombat.RoyaltiesGold = 0;
            }

            // A `SpecialCardReward` the room owes outright -- the Lantern Key, off its knight.
            // TheLanternKey.Fight hands it to EnterCombatWithoutExitingEvent as an extra
            // reward, so it is its own row (RewardsSetIndex 4, between relic and card) and
            // the room's ordinary CardReward is still rolled. Writing it into the first
            // offer slot lost it when PopulateCardReward rebuilt the offer (ABB75XX
            // step0314). It joined CombatRoom.ExtraRewards at combat entry, ahead of any
            // card a Thieving Hopper's death adds later.
            if (state.PendingSpecialCardReward != 0)
            {
                state.PendingSpecialCardRewards.Insert(
                    0,
                    new CardInstance(state.PendingSpecialCardReward, Upgraded: false)
                );
                state.PendingSpecialCardReward = 0;
            }

            if (hasPotionReward)
            {
                state.RewardPotion = NextPotion(state, state.PlayerRng.Rewards);
            }

            // Only an ELITE hands over a relic. RewardsSet's Boss case is gold, potion, card
            // and nothing else -- the relic in the Elite case has no counterpart there. The
            // emulator gave one for both, so beating the act 1 boss handed over a Whetstone
            // the run never earned, and its pickup upgraded two attacks in the deck.
            state.PendingRelicReward = state.CurrentNodeType is RunConstants.NodeElite;

            // The CARDS are rolled before the relic. RewardsSet builds gold, potion, card,
            // relic and then populates them in that order, and only sorts by RewardsSetIndex
            // afterwards -- so the order the screen SHOWS them in (relic above the card) is
            // not the order they were rolled in. The relic's rarity roll comes off
            // PlayerRng.Rewards like everything else, so taking it early handed the card
            // offer the relic's value and shifted all three cards by a draw.
            // Extra CardRewards subscribe when combat creates them, before the base reward
            // is constructed. Population order is still base first, extras afterward.
            int extraCardRewards = state.ActiveCombat?.ExtraCardRewards ?? 0;
            int firstExtraSubscription = state.CardRewardSubscriptionSequence + 1;
            state.CardRewardSubscriptionSequence += extraCardRewards;
            PopulateCardReward(state);
            if (state.PendingRelicReward)
            {
                state.RelicReward = NextRelic(state);
            }

            // RewardsSet populates base rewards first (including the elite relic), then
            // CombatRoom.ExtraRewards. Display sorting does not change RNG consumption.
            // An event's extras were added when `EventCombatSynchronizer` built the room,
            // so they come before any extra the fight itself added (Heist, Royalties,
            // extra card rewards), in the order the event listed them. Sorting by
            // RewardsSetIndex then shows an extra potion after the rolled one and an
            // extra relic after an elite's.
            foreach (var extra in state.PendingEventCombatRewards)
            {
                if (extra == EventCombatExtraReward.Relic)
                {
                    AddRelicReward(state);
                }
                else
                {
                    state.PendingPotionRewards.Add(NextPotion(state, state.PlayerRng.Rewards));
                }
            }
            state.PendingEventCombatRewards.Clear();

            // GoldReward.Populate rolls even a fixed amount: Heist/Royalties room extras
            // advance Rewards once per represented row before ModifyRewards runs.
            for (int i = 0; i < state.PendingGoldRewards.Count; i++)
            {
                int amount = state.PendingGoldRewards[i];
                state.PendingGoldRewards[i] = state.PlayerRng.Rewards.NextInt(amount, amount + 1);
            }
            for (int i = 0; i < extraCardRewards; i++)
            {
                state.PendingCardOffers.Add(
                    CreateCardRewardOffer(
                        state,
                        isFromCombat: false,
                        subscriptionOrder: firstExtraSubscription + i
                    )
                );
            }
            if (state.ActiveCombat is not null)
            {
                state.ActiveCombat.ExtraCardRewards = 0;
            }
        }

        // RewardsSet first populates the ordinary list and room extras, then appends
        // rewards from Hook.ModifyRewards in player relic order. Every added reward is
        // populated before any choice. Display sorting never changes this RNG order.
        for (int i = 0; i < state.Relics.Count; i++)
        {
            switch (state.Relics[i].DefId)
            {
                case Effects.RelicEffects.AmethystAubergine when !finalActBoss:
                    // TryModifyRewards appends GoldReward(15), already populated by
                    // its fixed-amount constructor. No extra RNG draw is made, and
                    // GainGold (including gold modifiers) runs only when claimed.
                    state.PendingGoldRewards.Add(
                        Effects.RelicEffects.ExtraCombatRewardGold(state, finalActBoss)
                    );
                    break;
                case Effects.RelicEffects.PrayerWheel
                    when state.CurrentNodeType == RunConstants.NodeNormal:
                    state.PendingCardOffers.Add(CreateCardRewardOffer(state, isFromCombat: false));
                    break;
                case Effects.RelicEffects.WhiteStar
                    when state.CurrentNodeType == RunConstants.NodeElite:
                    state.PendingCardOffers.Add(
                        CreateCardRewardOffer(
                            state,
                            options: new CardRewardOptions(
                                RunConstants.NodeBoss,
                                IsFromCombat: false
                            )
                        )
                    );
                    break;
                case Effects.RelicEffects.BlackStar
                    when state.CurrentNodeType == RunConstants.NodeElite:
                    AddRelicReward(state);
                    break;
                case RunConstants.RelicLavaRock
                    when state.CurrentNodeType == RunConstants.NodeBoss
                        && state.CurrentActIndex == 0
                        && !state.LavaRockTriggered:
                    AddRelicReward(state);
                    AddRelicReward(state);
                    state.LavaRockTriggered = true;
                    break;
                case Effects.RelicEffects.WongosMysteryTicket
                    when Effects.RelicEffects.WongosTicketPaysOut(state):
                    for (int j = 0; j < Effects.RelicEffects.WongosTicketRelics; j++)
                        AddRelicReward(state);
                    Effects.RelicEffects.RetireWongosTicket(state);
                    break;
            }
        }

        state.RewardCardPending = !finalActBoss;
        // `LastingCandy.BeforeCombatRewardOffered` sees the finished set and returns when it
        // holds no `CardReward`: the room's own card row (absent for the final act's boss),
        // plus any card reward an extra or `ModifyRewards` appended (Prayer Wheel, White
        // Star). `SpecialCardReward` is not a `CardReward`. Counting consumes no RNG.
        if (state.RewardCardPending || state.PendingCardOffers.Count > 0)
        {
            Effects.RelicEffects.CountCombatForLastingCandy(state);
        }
        state.Phase = RunPhase.RelicReward;
    }

    private static void AddRelicReward(RunState state)
    {
        int relic = NextRelic(state);
        if (state.RelicReward == 0)
            state.RelicReward = relic;
        else
            state.PendingBonusRelicRewards.Add(relic);
        state.PendingRelicReward = true;
    }

    public static void EnterCardReward(RunState state)
    {
        state.Phase = RunPhase.CardReward;
        PopulateCardReward(state);
    }

    /// <summary>
    /// Kaleidoscope constructs both manual rewards before RewardsSet populates either.
    /// Each card has its own factory hooks; each assembled row then has reward hooks.
    /// </summary>
    internal static void BeginKaleidoscopeRewards(RunState state, SelectionReturn returnTo)
    {
        var phase = returnTo == SelectionReturn.Shop ? RunPhase.Shop : state.Phase;
        BeginCustomRewardScreen(state, phase);
        var offers = new List<CardRewardOffer>();
        for (int i = 0; i < 2; i++)
        {
            var pools = OtherCharacterPools(state);
            state.Rng.Niche.Shuffle(pools);
            var cards = new List<CardInstance>();
            foreach (var pool in pools.Take(3))
                cards.Add(
                    CreateCardRewardOffer(
                        state,
                        options: new CardRewardOptions(
                            RunConstants.NodeNormal,
                            EncounterSource: false,
                            IsFromCombat: false
                        )
                        {
                            CardCount = 1,
                            CardPool = pool,
                            IsCardReward = false,
                            AllowPoolModifications = false,
                        }
                    ).Cards[0]
                );
            offers.Add(
                new CardRewardOffer([.. cards])
                {
                    Options = new CardRewardOptions(
                        RunConstants.NodeNormal,
                        EncounterSource: false,
                        IsFromCombat: false
                    )
                    {
                        CardPool = [],
                        AllowPoolModifications = false,
                    },
                    // The beta supplies an EMPTY pool and omits IsCardReward here.
                    // Preserve it: Driftwood actually fails after one rarity draw.
                    RerollOptions = new CardRewardOptions(
                        RunConstants.NodeNormal,
                        EncounterSource: false,
                        IsFromCombat: false
                    )
                    {
                        CardCount = cards.Count,
                        CardPool = [],
                        NoUpgradeRoll = true,
                        IsCardReward = false,
                    },
                    // The manual CardReward constructor does not subscribe to pickups.
                    ReceivesRelicUpdates = false,
                    CanReroll = HasRelic(state, Effects.RelicEffects.Driftwood),
                }
            );
        }
        foreach (var offer in offers)
            state.PendingCardOffers.Add(ApplyCardRewardHooks(state, offer));
        OfferFirstPendingCardOffer(state);
        state.RewardCardPending = true;
        state.Phase = RunPhase.RelicReward;
    }

    /// <summary>
    /// The character pools the player is not, sorted the way StableShuffle sorts them —
    /// by ModelId, which for these is the slugified class name, so alphabetical by
    /// character. The sort is what makes the shuffle reproducible.
    /// </summary>
    private static List<int[]> OtherCharacterPools(RunState state)
    {
        // ModelId order before StableShuffle, excluding the player's own character.
        var pools = new (string Entry, int[] Cards)[]
        {
            ("DEFECT", GeneratedData.CardPools.Defect.ToArray()),
            ("IRONCLAD", GeneratedData.CardPools.Ironclad.ToArray()),
            ("NECROBINDER", GeneratedData.CardPools.Necrobinder.ToArray()),
            ("REGENT", GeneratedData.CardPools.Regent.ToArray()),
            ("SILENT", GeneratedData.CardPools.Silent.ToArray()),
        };
        return
        [
            .. pools
                .Where(p =>
                    p.Entry != CharacterCatalog.Entry(state.Character)
                    && state.Progression.IsCharacterUnlocked(p.Entry)
                )
                .Select(p => p.Cards),
        ];
    }

    /// <summary>
    /// Moves the next owed relic into the primary slot; all queued relics remain visible
    /// and independently claimable on the same screen. When the
    /// queue runs dry, anything waiting on the whole screen being answered happens here --
    /// which for Neow's Bones is its curse.
    /// </summary>
    public static void OfferNextBonusRelic(RunState state)
    {
        if (state.RelicReward != 0)
        {
            return;
        }

        if (state.PendingBonusRelicRewards.Count > 0)
        {
            state.RelicReward = state.PendingBonusRelicRewards[0];
            state.PendingBonusRelicRewards.RemoveAt(0);
            return;
        }

        RunNonCombatEffects.AddNeowsBonesCurse(state);
    }

    public static bool HasPendingRewards(RunState state)
    {
        return state.RewardGold != 0
            || state.PendingGoldRewards.Count > 0
            || state.RewardPotion != 0
            || state.RelicReward != 0
            || state.RewardCardPending
            || state.PendingCardOffers.Count > 0
            || state.PendingSpecialCardRewards.Count > 0
            || state.PendingPotionRewards.Count > 0
            || state.PendingBonusRelicRewards.Count > 0;
    }

    public static bool ClaimNextReward(RunState state)
    {
        return ClaimRewardAtIndex(state, 0);
    }

    public static bool ClaimRewardAtIndex(RunState state, int itemIndex)
    {
        // Every GoldReward is its own row (RewardsSetIndex 1, ahead of the potion): a
        // Heist's stolen-back gold sits beside the fight's own, and either can be taken
        // first. The primary slot plus the queue behind it, in that order.
        int firstGoldCount = state.RewardGold != 0 ? 1 : 0;
        int goldCount = firstGoldCount + state.PendingGoldRewards.Count;
        if (itemIndex >= 0 && itemIndex < goldCount)
        {
            int pendingIndex = itemIndex - firstGoldCount;
            if (pendingIndex < 0)
            {
                RunNonCombatEffects.GainGold(state, state.RewardGold);
                state.RewardGold = 0;
                OfferNextGold(state);
            }
            else
            {
                RunNonCombatEffects.GainGold(state, state.PendingGoldRewards[pendingIndex]);
                state.PendingGoldRewards.RemoveAt(pendingIndex);
            }
            return true;
        }
        itemIndex -= goldCount;

        int firstPotionCount = state.RewardPotion != 0 ? 1 : 0;
        int potionCount = firstPotionCount + state.PendingPotionRewards.Count;
        if (itemIndex >= 0 && itemIndex < potionCount)
        {
            int pendingIndex = itemIndex - firstPotionCount;
            int potion =
                pendingIndex < 0 ? state.RewardPotion : state.PendingPotionRewards[pendingIndex];
            // PotionReward.OnSelect keeps the reward on TooFull. Sozu's NotAllowed
            // occurs before capacity is checked and consumes the row even on a full belt.
            // A refused full-belt click is handled, but makes no state change.
            if (!HasRelic(state, Effects.RelicEffects.Sozu) && !AddPotion(state, potion))
                return true;
            if (pendingIndex < 0)
            {
                state.RewardPotion = 0;
                OfferNextPotion(state);
            }
            else
                state.PendingPotionRewards.RemoveAt(pendingIndex);
            return true;
        }
        itemIndex -= potionCount;

        int firstRelicCount = state.RelicReward != 0 ? 1 : 0;
        int relicCount = firstRelicCount + state.PendingBonusRelicRewards.Count;
        if (itemIndex >= 0 && itemIndex < relicCount)
        {
            int pendingIndex = itemIndex - firstRelicCount;
            int claimed =
                pendingIndex < 0 ? state.RelicReward : state.PendingBonusRelicRewards[pendingIndex];
            if (pendingIndex < 0)
                state.RelicReward = 0;
            else
                state.PendingBonusRelicRewards.RemoveAt(pendingIndex);
            // Remove only the chosen row before its awaited pickup. A nested reward set
            // captures the remaining rows; ordinary pickups then promote the primary.
            var followUp = RunNonCombatEffects.ApplyRelicPickup(state, claimed);
            if (followUp == RunFollowUp.BundleSelect)
            {
                // RelicReward.OnSelect awaits RelicCmd.Obtain, and ScrollBoxes.AfterObtained
                // awaits FromChooseABundleScreen inside it: the bundle screen opens over the
                // rewards screen, which resumes -- and Neow's Bones' curse waits -- until
                // the bundle is confirmed.
                state.BundleReturnsToRewards = true;
                state.Phase = RunPhase.BundleSelect;
                return true;
            }
            if (followUp != RunFollowUp.CustomRewards)
                OfferNextBonusRelic(state);
            return true;
        }
        itemIndex -= relicCount;

        // SpecialCardReward.RewardsSetIndex = 4, before ordinary card offers.
        if (itemIndex >= 0 && itemIndex < state.PendingSpecialCardRewards.Count)
        {
            var card = state.PendingSpecialCardRewards[itemIndex];
            state.PendingSpecialCardRewards.RemoveAt(itemIndex);
            RunNonCombatEffects.AddCardToDeck(state, card);
            return true;
        }
        itemIndex -= state.PendingSpecialCardRewards.Count;

        if (state.RewardCardPending && itemIndex == 0)
        {
            state.OpenedCardRewardIndex = 0;
            state.RewardCardPending = false;
            state.ReturnToRewardScreenAfterCardReward = true;
            state.Phase = RunPhase.CardReward;
            return true;
        }

        int cardIndex = itemIndex;
        if (state.RewardCardPending)
            itemIndex--;

        if (itemIndex >= 0 && itemIndex < state.PendingCardOffers.Count)
        {
            var chosen = state.PendingCardOffers[itemIndex];
            state.PendingCardOffers.RemoveAt(itemIndex);
            if (state.RewardCardPending)
                state.PendingCardOffers.Insert(0, CaptureCardOffer(state));
            ShowCardOffer(state, chosen);
            state.OpenedCardRewardIndex = cardIndex;
            state.RewardCardPending = false;
            state.ReturnToRewardScreenAfterCardReward = true;
            state.Phase = RunPhase.CardReward;
            return true;
        }

        return false;
    }

    public static void ClearRewardScreen(RunState state)
    {
        ClearCardOffer(state);
        state.RewardGold = 0;
        state.RewardPotion = 0;
        state.RewardCardPending = false;
        state.ReturnToRewardScreenAfterCardReward = false;
        state.RelicReward = 0;
        state.PendingGoldRewards.Clear();
        state.PendingCardOffers.Clear();
        state.PendingSpecialCardRewards.Clear();
    }

    internal static void BeginOrreryRewards(RunState state, SelectionReturn returnTo)
    {
        var phase = returnTo == SelectionReturn.Shop ? RunPhase.Shop : state.Phase;
        BeginCustomRewardScreen(state, phase);
        var options = new CardRewardOptions(
            RunConstants.NodeNormal,
            EncounterSource: false,
            IsFromCombat: false
        );
        for (int i = 0; i < 5; i++)
            state.PendingCardOffers.Add(CreateCardRewardOffer(state, options: options));
        OfferFirstPendingCardOffer(state);
        state.RewardCardPending = true;
        state.Phase = RunPhase.RelicReward;
    }

    internal static void BeginCustomRewardScreen(RunState state, RunPhase returnPhase)
    {
        state.SuspendedRewardScreens.Add(RewardScreenSnapshot.Capture(state, returnPhase));
        ClearRewardScreen(state);
        state.PendingBonusRelicRewards.Clear();
        state.PendingPotionRewards.Clear();
        state.CardRemovalsOwed = 0;
        state.PendingRelicReward = false;
        state.PendingNeowsBonesCurse = false;
        state.NeowAwaitingProceed = false;
        state.EventAwaitingProceed = false;
        state.Phase = RunPhase.RelicReward;
    }

    internal static void BeginCauldronRewards(RunState state, SelectionReturn returnTo)
    {
        BeginCustomRewardScreen(
            state,
            returnTo == SelectionReturn.Shop ? RunPhase.Shop : state.Phase
        );
        for (int i = 0; i < 5; i++)
            state.PendingPotionRewards.Add(NextPotion(state, state.PlayerRng.Rewards));
        OfferNextPotion(state);
    }

    internal static bool TryFinishCustomRewards(RunState state, bool skip = false)
    {
        if (state.SuspendedRewardScreens.Count == 0 || (!skip && HasPendingRewards(state)))
            return false;
        var parent = state.SuspendedRewardScreens[^1];
        state.SuspendedRewardScreens.RemoveAt(state.SuspendedRewardScreens.Count - 1);
        parent.Restore(state);
        if (state.Phase == RunPhase.RelicReward)
            OfferNextBonusRelic(state);
        else if (state.Phase == RunPhase.Shop && state.ParasolSweepPending)
            ContinueLordsParasolSweep(state);
        return true;
    }

    /// <summary>Moves the next queued gold pile onto the screen, if the screen is free.</summary>
    public static void OfferNextGold(RunState state)
    {
        // `GoldReward.IsPopulated => Amount >= 0`: a zero pile is still a row. `RewardGold`
        // 0 means "no primary", so a zero stays queued, where it is still exported and
        // claimable, rather than vanishing into that sentinel.
        if (
            state.PendingGoldRewards.Count == 0
            || state.RewardGold != 0
            || state.PendingGoldRewards[0] == 0
        )
        {
            return;
        }

        state.RewardGold = state.PendingGoldRewards[0];
        state.PendingGoldRewards.RemoveAt(0);
    }

    /// <summary>Advance the first visible potion; all rows have already been populated.</summary>
    public static void OfferNextPotion(RunState state)
    {
        if (state.RewardPotion != 0 || state.PendingPotionRewards.Count == 0)
            return;
        state.RewardPotion = state.PendingPotionRewards[0];
        state.PendingPotionRewards.RemoveAt(0);
    }

    /// <summary>
    /// Put the head of the queue on the card screen, leaving the rest queued.
    /// </summary>
    public static void OfferFirstPendingCardOffer(RunState state)
    {
        if (state.PendingCardOffers.Count > 0)
        {
            OfferNextCardOffer(state);
        }
    }

    /// <summary>Moves the next queued card offer onto the card screen.</summary>
    private static void OfferNextCardOffer(RunState state)
    {
        var offer = state.PendingCardOffers[0];
        state.PendingCardOffers.RemoveAt(0);
        ShowCardOffer(state, offer);
    }

    internal static void ShowCardOffer(RunState state, CardRewardOffer offer)
    {
        state.DisplayedCardReward = offer;
        Array.Clear(state.RewardCards);
        Array.Clear(state.RewardUpgraded);
        state.RewardEnchantIndex = -1;
        state.RewardEnchantment = Enchantment.None;
        state.LastingCandyRewardCard = offer.BonusPowerCard.DefId;
        state.LastingCandyRewardUpgraded = offer.BonusPowerCard.Upgraded;
        state.CardRewardRerollAvailable = offer.CanReroll;
        state.CardRewardOptions = offer.Options;
        for (int i = 0; i < state.RewardCards.Length && i < offer.Cards.Length; i++)
        {
            var card = offer.Cards[i];
            state.RewardCards[i] = card.DefId;
            state.RewardUpgraded[i] = card.Upgraded;
            if (card.Enchantment != Enchantment.None)
            {
                state.RewardEnchantIndex = i;
                state.RewardEnchantment = card.Enchantment;
            }
        }
    }

    internal static CardRewardOffer CaptureCardOffer(RunState state) =>
        state.DisplayedCardReward
        ?? new([
            .. state.RewardCards.Select(
                (id, i) =>
                    new CardInstance(
                        id,
                        state.RewardUpgraded[i],
                        Enchantment: i == state.RewardEnchantIndex
                            ? state.RewardEnchantment
                            : Enchantment.None,
                        EnchantAmount: i == state.RewardEnchantIndex ? 1 : 0
                    )
            ),
        ])
        {
            BonusPowerCard = new CardInstance(
                state.LastingCandyRewardCard,
                state.LastingCandyRewardUpgraded
            ),
            CanReroll = state.CardRewardRerollAvailable,
            Options = state.CardRewardOptions,
        };

    internal static void ClearCardOffer(RunState state)
    {
        ShowCardOffer(state, new CardRewardOffer([]));
        state.DisplayedCardReward = null;
    }

    // Original CardReward.OnRelicObtained runs only the newly acquired relic's hooks,
    // in subscription order. It calls AfterModifyingRewards, not the per-card hook,
    // so this notification must not spend Silver Crucible or Silken Tress.
    internal static void OnRelicObtained(RunState state, int relicId)
    {
        var offers = new List<CardRewardOffer>(state.PendingCardOffers);
        if (
            state.DisplayedCardReward is { } displayed
            && (state.RewardCardPending || state.Phase == RunPhase.CardReward)
        )
            offers.Add(displayed);
        foreach (var screen in state.SuspendedRewardScreens)
        {
            if (screen.CardPending || screen.Phase == RunPhase.CardReward)
                offers.Add(screen.Card);
            offers.AddRange(screen.RemainingCards);
        }
        var changed = offers
            .Where(o => o.ReceivesRelicUpdates)
            .OrderBy(o => o.SubscriptionOrder)
            .ToDictionary(
                o => o.SubscriptionOrder,
                o => ApplyCardRewardRelic(state, o, relicId, consume: false)
            );
        CardRewardOffer Update(CardRewardOffer offer) =>
            changed.GetValueOrDefault(offer.SubscriptionOrder, offer);
        if (state.DisplayedCardReward is { } current)
            ShowCardOffer(state, Update(current));
        for (int i = 0; i < state.PendingCardOffers.Count; i++)
            state.PendingCardOffers[i] = Update(state.PendingCardOffers[i]);
        for (int i = 0; i < state.SuspendedRewardScreens.Count; i++)
        {
            var screen = state.SuspendedRewardScreens[i];
            state.SuspendedRewardScreens[i] = screen with
            {
                Card = Update(screen.Card),
                RemainingCards = [.. screen.RemainingCards.Select(Update)],
            };
        }
    }

    /// <summary>
    /// A card offer of a pinned rarity, rolled off a given stream: the Crystal Sphere's
    /// buried card rewards are <c>CardRarityOddsType.Uniform</c> over the character's pool
    /// filtered to one rarity, with the event's own Rng overriding the player's.
    /// CreateForReward then rolls an upgrade per card whatever the odds, so each card is
    /// two draws.
    /// </summary>
    /// <summary>
    /// A relic's own "choose a card" screen: <c>CardFactory.CreateForReward</c> over one
    /// pool, with <c>CardCreationSource.Other</c>.
    /// </summary>
    /// <remarks>
    /// Source Other means <c>RollForRarity</c> goes down <c>RollWithBaseOdds</c> -- the
    /// flat odds, with the running rare-chance offset neither read nor grown -- so a card
    /// costs three draws: its rarity, itself, and an upgrade roll. Two cards is six draws,
    /// which is exactly what Lead Paperweight's stand-in count in
    /// <c>AdvanceRewardRngForNeowRelic</c> used to burn. That is the tell that the count
    /// was right and only the CARDS were missing.
    /// </remarks>
    public static int[] GenerateOtherSourceCardOffer(
        RunState state,
        ReadOnlySpan<int> pool,
        int count
    )
    {
        var poolCards = pool.ToArray();
        var blacklist = new List<int>();
        var offer = new int[count];
        for (int i = 0; i < count; i++)
        {
            int rarity = RollCardRarity(
                state,
                RegularEncounterCardOdds(state),
                mutateOffset: false,
                state.PlayerRng.Rewards,
                useOffset: false
            );
            offer[i] = ChooseCardWithRarity(
                state.Progression,
                poolCards,
                rarity,
                blacklist,
                state.PlayerRng.Rewards
            );
            blacklist.Add(offer[i]);
            RollCardUpgrade(state, offer[i], state.PlayerRng.Rewards);
        }

        return offer;
    }

    /// <summary>
    /// Hefty Tablet's three: the owner's pool filtered to Rare, <c>Uniform</c> odds and
    /// the <c>NoUpgradeRoll</c> flag.
    /// </summary>
    /// <remarks>
    /// Uniform skips <c>RollForRarity</c> outright and NoUpgradeRoll skips the upgrade, so
    /// each card is a SINGLE draw -- three for the screen, which is what its stand-in
    /// count burned.
    /// </remarks>
    public static int[] GenerateRareOnlyCardOffer(RunState state, int count)
    {
        var blacklist = new List<int>();
        var offer = new int[count];
        for (int i = 0; i < count; i++)
        {
            offer[i] = ChooseCardWithRarity(
                state.Progression,
                PlayerCardPool(state),
                RarityRare,
                blacklist,
                state.PlayerRng.Rewards
            );
            blacklist.Add(offer[i]);
        }

        return offer;
    }

    /// <summary>
    /// Puts a pre-rolled offer on a CHOOSE-A-CARD grid, which is what
    /// <c>CardSelectCmd.FromChooseACardScreen</c> raises -- the live game calls this
    /// screen <c>card_select</c>, not <c>card_reward</c>. The two are different screens
    /// and the difference is visible in every capture.
    /// </summary>
    public static void OfferPreRolledCards(RunState state, int[] offer)
    {
        SetOfferGrid(state, CardRewardOffer.Unupgraded(offer).Cards, picks: 1, optional: false);
    }

    public static int[] GenerateFixedRarityCardOffer(
        RunState state,
        int count,
        CardRarity rarity,
        GameRng rng
    ) => GenerateFixedRarityCardOffer(state, count, rarity, rng, PlayerCardPool(state));

    /// <summary>
    /// As above, over a NAMED pool — Sea Glass draws from another character's.
    /// </summary>
    public static int[] GenerateFixedRarityCardOffer(
        RunState state,
        int count,
        CardRarity rarity,
        GameRng rng,
        ReadOnlySpan<int> pool
    ) =>
        [
            .. GenerateFixedRarityCardReward(state, count, rarity, rng, pool)
                .Cards.Select(card => card.DefId),
        ];

    public static CardRewardOffer GenerateFixedRarityCardReward(
        RunState state,
        int count,
        CardRarity rarity,
        GameRng rng
    ) => GenerateFixedRarityCardReward(state, count, rarity, rng, PlayerCardPool(state));

    public static CardRewardOffer GenerateFixedRarityCardReward(
        RunState state,
        int count,
        CardRarity rarity,
        GameRng rng,
        ReadOnlySpan<int> pool
    )
    {
        int rarityValue = rarity switch
        {
            CardRarity.Rare => RarityRare,
            CardRarity.Uncommon => RarityUncommon,
            _ => RarityCommon,
        };
        var blacklist = new List<int>();
        var offer = new CardInstance[count];
        for (int i = 0; i < count; i++)
        {
            int id = ChooseCardWithRarity(state.Progression, pool, rarityValue, blacklist, rng);
            blacklist.Add(id);
            offer[i] = new CardInstance(id, RollCardUpgrade(state, id, rng));
        }

        return new CardRewardOffer(offer);
    }

    /// <summary>
    /// One potion of a named rarity, uniformly. No rarity roll first: the Crystal Sphere's
    /// potions pin their own rarity and take <c>rng.NextItem</c> over what is left, the way
    /// the Potion Courier's Ransack does.
    /// </summary>
    public static int NextPotionOfRarity(
        GameRng rng,
        Core.PotionRarity rarity,
        CharacterId character = CharacterId.Ironclad
    )
    {
        var available = CharacterCatalog
            .PotionPool(character)
            .ToArray()
            .Where(potionId => GeneratedData.Potions.Get(potionId).Rarity == rarity)
            .ToArray();
        return available.Length > 0
            ? rng.NextItem(available)
            : rng.NextItem(CharacterCatalog.PotionPool(character).ToArray());
    }

    /// <summary>
    /// Roll the cards an event offers on a grid -- Brain Leech's five, Room Full of
    /// Cheese's eight.
    /// </summary>
    /// <remarks>
    /// CardCreationOptions.ForNonCombatWithDefaultOdds is CardCreationSource.Other with
    /// RegularEncounter odds and the NoUpgradeRoll flag, so each card is two draws on the
    /// Rewards stream rather than three: the rarity, off the flat odds with no running
    /// offset, and the card itself. A combat reward's third draw is the upgrade roll, and
    /// this path does not make it.
    /// </remarks>
    /// <param name="fixedRarity">
    /// Set when the event pins the rarity itself, as Room Full of Cheese does by filtering
    /// its pool to Commons and asking for Uniform odds. CreateForReward skips RollForRarity
    /// entirely on the Uniform branch, so a pinned rarity is one draw per card rather than
    /// two -- getting that wrong shifts the whole grid.
    /// </param>
    public static int[] GenerateEventOfferCards(
        RunState state,
        int count,
        ReadOnlySpan<int> pool,
        CardRarity? fixedRarity = null
    )
    {
        var blacklist = new List<int>();
        var offers = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int rarity = fixedRarity switch
            {
                CardRarity.Common => RarityCommon,
                CardRarity.Uncommon => RarityUncommon,
                CardRarity.Rare => RarityRare,
                _ => RollCardRarity(
                    state,
                    RegularEncounterCardOdds(state),
                    mutateOffset: false,
                    state.PlayerRng.Rewards,
                    useOffset: false
                ),
            };
            int cardId = ChooseCardWithRarity(
                state.Progression,
                pool,
                rarity,
                blacklist,
                state.PlayerRng.Rewards
            );
            offers.Add(cardId);
            blacklist.Add(cardId);
        }

        return [.. offers];
    }

    internal static bool PopulateCardReward(RunState state, bool isReroll = false)
    {
        var previous = isReroll ? CaptureCardOffer(state) : null;
        // Original Reroll clears cards and disables reroll before Populate, even
        // when the factory subsequently throws. Preserve that state on failure.
        if (previous != null)
            ShowCardOffer(
                state,
                previous with
                {
                    Cards = [],
                    BonusPowerCard = default,
                    CanReroll = false,
                }
            );
        CardRewardOffer offer;
        try
        {
            offer = CreateCardRewardOffer(
                state,
                isReroll,
                options: previous?.RerollOptions ?? previous?.Options,
                subscriptionOrder: previous?.SubscriptionOrder
            );
        }
        catch (CardRewardGenerationException) when (isReroll)
        {
            return false;
        }
        if (previous != null)
            offer = offer with
            {
                ReceivesRelicUpdates = previous.ReceivesRelicUpdates,
                RerollOptions = previous.RerollOptions,
            };
        ShowCardOffer(state, offer);
        return true;
    }

    private static int[] ResolveRewardPool(RunState state, CardRewardOptions options)
    {
        int[] pool = options.CardPool ?? PlayerCardPool(state).ToArray();
        if (!options.IsCardReward || !options.AllowPoolModifications)
            return pool;
        foreach (var relic in state.Relics)
        {
            if (relic.DefId == Effects.RelicEffects.DingyRug)
                pool = pool.Union(ColorlessRewardPool.ToArray()).ToArray();
            if (
                relic.DefId == Effects.RelicEffects.PrismaticGem
                && pool.Any(id => !ColorlessRewardPool.Contains(id))
            )
            {
                // ModelDb.AllCharacters / UnlockState.Characters order is significant.
                var characters = new (string Entry, int[] Pool)[]
                {
                    ("IRONCLAD", IroncladRewardPool.ToArray()),
                    ("SILENT", GeneratedData.CardPools.Silent.ToArray()),
                    ("REGENT", GeneratedData.CardPools.Regent.ToArray()),
                    ("NECROBINDER", GeneratedData.CardPools.Necrobinder.ToArray()),
                    ("DEFECT", GeneratedData.CardPools.Defect.ToArray()),
                };
                pool = characters
                    .Where(c => state.Progression.IsCharacterUnlocked(c.Entry))
                    .SelectMany(c => c.Pool)
                    .Union(pool)
                    .ToArray();
            }
        }
        return pool;
    }

    // CardFactory can also return cards directly to a selection grid. Such calls do
    // run the general option hooks, but are not IsCardReward and never subscribe.
    internal static CardRewardOffer CreateGridOffer(
        RunState state,
        int count,
        ReadOnlySpan<int> pool,
        CardRarity? fixedRarity = null,
        bool noUpgradeRoll = true
    ) =>
        CreateCardRewardOffer(
            state,
            options: new CardRewardOptions(
                RunConstants.NodeNormal,
                EncounterSource: false,
                IsFromCombat: false
            )
            {
                CardCount = count,
                CardPool = pool.ToArray(),
                FixedRarity = fixedRarity,
                NoUpgradeRoll = noUpgradeRoll,
                IsCardReward = false,
            }
        );

    internal static void SetOfferGrid(
        RunState state,
        CardInstance[] cards,
        int picks,
        bool optional
    )
    {
        state.PendingOfferInstances = cards;
        state.PendingOfferCards = [.. cards.Select(c => c.DefId)];
        state.PendingOfferPicks = picks;
        state.PendingOfferOptional = optional;
        state.Phase = RunPhase.TransformSelect;
    }

    internal static CardRewardOffer CreateCardRewardOffer(
        RunState state,
        bool isReroll = false,
        bool isFromCombat = true,
        CardRewardOptions? options = null,
        int? subscriptionOrder = null
    )
    {
        options ??= new CardRewardOptions(state.CurrentNodeType, IsFromCombat: isFromCombat);
        var cards = new CardInstance[options.CardCount];
        int[] pool = ResolveRewardPool(state, options);
        // Original WithCardPools mutates the options retained by CardReward. Keep the
        // expanded pool for later rerolls even if a pool-modifying relic is removed.
        options = options with
        {
            CardPool = pool,
        };
        if (options.FixedRarity is { } fixedRarity)
            pool = pool.Where(id => GeneratedData.Cards.Get(id).Rarity == fixedRarity).ToArray();
        if (options.FixedType is { } fixedType)
            pool = pool.Where(id => GeneratedData.Cards.Get(id).Type == fixedType).ToArray();
        // Retain the stream's identity in the options, not a mutable RNG reference:
        // a cloned search state must use its own stream on a later reroll.
        var rng =
            options.RngSource == CardRewardRngSource.CrystalSphere
                ? state.CrystalSphereRng
                    ?? throw new InvalidOperationException("Missing Crystal Sphere reward RNG")
                : state.PlayerRng.Rewards;

        var blacklist = new List<int>();
        for (int i = 0; i < cards.Length; i++)
        {
            int rarity = options.FixedRarity switch
            {
                CardRarity.Common => RarityCommon,
                CardRarity.Uncommon => RarityUncommon,
                CardRarity.Rare => RarityRare,
                _ => RollRewardCardRarity(state, options),
            };
            if (pool.Length == 0)
                throw new CardRewardGenerationException();
            int cardId = ChooseCardWithRarity(state.Progression, pool, rarity, blacklist, rng);
            blacklist.Add(cardId);
            // The roll comes FIRST and unconditionally: CardFactory.CreateForReward calls
            // RollForUpgrade for every card unless NoUpgradeRoll is set, and RollForUpgrade
            // draws its float before it even asks whether the card is upgradable. Written
            // as one `||` chain with silverCrucibleUpgrade in front, C# short-circuits and
            // the draw never happens -- so a run holding Silver Crucible spent two values
            // per reward card where the game spends three, and every card the rewards
            // stream produced from the second one on was a different card.
            bool rolledUpgrade = !options.NoUpgradeRoll && RollCardUpgrade(state, cardId, rng);
            cards[i] = new CardInstance(cardId, rolledUpgrade);
        }

        var offer = new CardRewardOffer(cards)
        {
            CanReroll =
                options.IsCardReward
                && !isReroll
                && HasRelic(state, Effects.RelicEffects.Driftwood),
            Options = options,
            SubscriptionOrder = options.IsCardReward
                ? subscriptionOrder ?? ++state.CardRewardSubscriptionSequence
                : 0,
            ReceivesRelicUpdates = options.IsCardReward,
        };
        return ApplyCardRewardHooks(state, offer);
    }

    private static CardRewardOffer ApplyCardRewardHooks(RunState state, CardRewardOffer offer)
    {
        var options = offer.Options!;
        // Hook runs every ordinary hook first, then every Late hook in relic order.
        if (HasRelic(state, Effects.RelicEffects.LastingCandy))
            offer = ApplyCardRewardRelic(
                state,
                offer,
                Effects.RelicEffects.LastingCandy,
                consume: true
            );
        foreach (var relic in state.Relics.ToArray())
            if (relic.DefId != Effects.RelicEffects.LastingCandy)
                offer = ApplyCardRewardRelic(state, offer, relic.DefId, consume: true);
        // CardReward.AfterGenerated runs after factory hooks, on the initial offer
        // and every reroll (TheFutureOfPotions.UpgradeCardsInReward).
        if (options.UpgradeAfterGenerated)
            offer = offer with
            {
                Cards =
                [
                    .. offer.Cards.Select(c =>
                        GeneratedData.Cards.Get(c.DefId).Upgradable ? c with { Upgraded = true } : c
                    ),
                ],
            };
        return offer;
    }

    /// <summary>
    /// `LastingCandy.TryModifyCardRewardOptions`: on every SECOND combat, a fourth option
    /// is ADDED to the card reward, and it is always a POWER.
    /// </summary>
    /// <remarks>
    /// The pool is the screen's own, filtered to Powers the screen is not already offering
    /// -- `options.TrueForAll(o => o.originalCard.Id != c.Id)` -- and falls back to every
    /// Power when that leaves nothing. Then ONE card through `CreateForReward` with the
    /// same `RarityOdds`, which is a rarity roll, a pick and an upgrade roll: three draws
    /// on the Rewards stream, exactly as each of the other three cost. Spending a different
    /// number here would move every reward in the rest of the run -- E451's lesson.
    ///
    /// `CardCreationSource.Other` with `NoModifyHooks | NoCardPoolModifications`, so the
    /// relics that widen or enchant a reward do not get a second bite at this one.
    /// </remarks>
    private static CardInstance CreateLastingCandyPowerOption(
        RunState state,
        int[] pool,
        List<int> blacklist,
        CardRewardOptions options
    )
    {
        if (!Effects.RelicEffects.LastingCandyOffersAPower(state))
        {
            return default;
        }

        var powers = pool.Where(cardId =>
                GeneratedData.Cards.Get(cardId).Type == CardType.Power
                && !blacklist.Contains(cardId)
            )
            .ToArray();
        if (powers.Length == 0)
        {
            // "If none is left, take any Power at all" -- the game's second `from` clause,
            // which drops the not-already-offered filter rather than giving up.
            powers = pool.Where(cardId => GeneratedData.Cards.Get(cardId).Type == CardType.Power)
                .ToArray();
        }

        if (powers.Length == 0)
        {
            return default;
        }

        int rarity = RollRewardCardRarity(state, options with { EncounterSource = false });
        int cardId = ChooseCardWithRarity(
            state.Progression,
            powers,
            rarity,
            [],
            state.PlayerRng.Rewards
        );
        return new CardInstance(cardId, RollCardUpgrade(state, cardId, state.PlayerRng.Rewards));
    }

    /// <summary>
    /// The beta's card-option hooks, applied to populated instances. Generation calls
    /// the per-card completion hooks; a RelicObtained notification does not.
    /// </summary>
    private static CardRewardOffer ApplyCardRewardRelic(
        RunState state,
        CardRewardOffer offer,
        int relicId,
        bool consume
    )
    {
        if (offer.Options is not { } options)
            return offer;
        if (relicId == Effects.RelicEffects.LastingCandy)
        {
            if (!options.EncounterSource || !options.IsFromCombat)
                return offer;
            // The appended card is created without modify hooks; the outer Late hooks
            // still visit it, together with the three original options.
            int[] pool = options.CardPool ?? ResolveRewardPool(state, options);
            return offer with
            {
                BonusPowerCard = CreateLastingCandyPowerOption(
                    state,
                    pool,
                    [.. offer.Cards.Select(c => c.DefId)],
                    options
                ),
            };
        }
        bool upgradeAll = false;
        CardType? upgradeType = null;
        Enchantment enchantment = Enchantment.None;
        int amount = 1;
        int relicIndex = state.Relics.FindIndex(r => r.DefId == relicId);
        switch (relicId)
        {
            case RunConstants.RelicMoltenEgg:
                upgradeType = CardType.Attack;
                break;
            case RunConstants.RelicToxicEgg:
                upgradeType = CardType.Skill;
                break;
            case RunConstants.RelicFrozenEgg:
                upgradeType = CardType.Power;
                break;
            case RunConstants.RelicSilverCrucible:
                if (!options.IsCardReward || state.Relics[relicIndex].Counter <= 0)
                    return offer;
                upgradeAll = true;
                break;
            case Effects.RelicEffects.LavaLamp:
                if (!Effects.RelicEffects.UpgradesCardRewards(state))
                    return offer;
                upgradeAll = true;
                break;
            case Effects.RelicEffects.WingCharm:
                enchantment = Enchantment.Swift;
                break;
            case Effects.RelicEffects.Glitter:
                enchantment = Enchantment.Glam;
                break;
            case Effects.RelicEffects.FresnelLens:
                enchantment = Enchantment.Nimble;
                amount = 2;
                break;
            case RunConstants.RelicSilkenTress:
                if (!options.IsCardReward || state.Relics[relicIndex].Counter > 0)
                    return offer;
                enchantment = Enchantment.Glam;
                break;
            default:
                return offer;
        }
        var cards = offer.Cards.ToList();
        if (offer.BonusPowerCard.DefId != 0)
            cards.Add(offer.BonusPowerCard);
        if (relicId == Effects.RelicEffects.WingCharm)
        {
            var eligible = Enumerable
                .Range(0, cards.Count)
                .Where(i => Enchantments.CanEnchant(cards[i], enchantment, inDeck: false))
                .ToArray();
            if (eligible.Length == 0)
                return offer;
            int index = eligible[state.Rng.Niche.NextInt(eligible.Length)];
            cards[index] = cards[index] with { Enchantment = enchantment, EnchantAmount = amount };
        }
        else
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                var def = GeneratedData.Cards.Get(card.DefId);
                if ((upgradeAll || upgradeType == def.Type) && def.Upgradable)
                    card = card with { Upgraded = true };
                if (Enchantments.CanEnchant(card, enchantment, inDeck: false))
                    card = card with { Enchantment = enchantment, EnchantAmount = amount };
                cards[i] = card;
            }
        }
        if (consume && relicId == RunConstants.RelicSilverCrucible)
            state.Relics[relicIndex] = state.Relics[relicIndex] with
            {
                Counter = state.Relics[relicIndex].Counter - 1,
            };
        if (consume && relicId == RunConstants.RelicSilkenTress)
            state.Relics[relicIndex] = state.Relics[relicIndex] with { Counter = 1 };
        return offer with
        {
            Cards = [.. cards.Take(offer.Cards.Length)],
            BonusPowerCard = cards.Count > offer.Cards.Length ? cards[^1] : default,
        };
    }

    public static void EnterRelicReward(RunState state)
    {
        state.Phase = RunPhase.RelicReward;
        state.RelicReward = NextRelic(state);
    }

    public static void EnterTreasureRoom(RunState state)
    {
        ClearRewardScreen(state);
        state.Phase = RunPhase.Treasure;
        state.TreasureChestOpened = false;
        state.PendingTreasureRelic = 0;
        if (state.Relics.Any(relic => relic.DefId == RunConstants.RelicSilverCrucible))
        {
            // AfterRoomEntered increments before ShouldGenerateTreasure runs.
            // Empty chests still open, but neither reward stream is consumed.
            state.SilverCrucibleTreasureRoomsEntered++;
            if (state.SilverCrucibleTreasureRoomsEntered == 1)
                return;
        }

        // TreasureRoomRelicSynchronizer.BeginRelicPicking rolls its own stream
        // and pulls the shared bag. Ownership changes only when the player claims it;
        // skipping still consumes this shared offer, but not the player's relic bag.
        var rarity = RelicGrabBag.RollRarity(state.Rng.TreasureRoomRelics);
        state.PendingTreasureRelic =
            state.SharedRelicBag.Pull(
                rarity,
                fromFront: true,
                RelicGrabBag.AllowedInSoloRun(state.Floor)
            ) ?? FallbackRelic;
    }

    public static bool OpenTreasureChest(RunState state)
    {
        if (state.Phase != RunPhase.Treasure || state.TreasureChestOpened)
            return false;

        // NTreasureRoom.OpenChest -> DoNormalRewards -> OneOffSynchronizer:
        // the gold roll and award happen on the click, not on room entry.
        state.TreasureChestOpened = true;
        if (state.PendingTreasureRelic == 0)
            return true;

        int gold = state.PlayerRng.Rewards.NextInt(42, 53);
        RunNonCombatEffects.GainGold(
            state,
            Ascension.Has(state.AscensionLevel, Ascension.Poverty) ? (int)(gold * 0.75) : gold
        );
        CompleteSpoilsMapQuest(state);
        state.RelicReward = state.PendingTreasureRelic;
        state.PendingTreasureRelic = 0;
        return true;
    }

    /// <summary>
    /// <c>OneOffSynchronizer.TryHandleSpoilsMap</c>, right after the chest's own gold: when
    /// the point the run stands on carries the quest marker, EVERY Spoils Map in the deck
    /// completes -- <c>OnQuestComplete</c> gains its 600 gold and removes the card.
    /// </summary>
    /// <remarks>
    /// The marker is dropped once no copy is left, which is what the last removal's
    /// <c>BeforeCardRemoved</c> -> <c>RemoveQuest</c> leaves behind.
    /// </remarks>
    private static void CompleteSpoilsMapQuest(RunState state)
    {
        if (state.SpoilsMapQuestCoord != state.CurrentMapCoord)
            return;

        int index;
        while (
            (index = state.Deck.FindIndex(card => card.DefId == RunConstants.SpoilsMapCard)) >= 0
        )
        {
            RunNonCombatEffects.GainGold(state, SpoilsActMapGenerator.SpoilsGold);
            state.Deck.RemoveAt(index);
        }

        state.SpoilsMapQuestCoord = null;
    }

    public static void EnterShop(RunState state)
    {
        // Meal Ticket's AfterRoomEntered(MerchantRoom): HealVar(15m), skipped when dead.
        if (state.PlayerHp > 0 && HasRelic(state, Effects.RelicEffects.MealTicket))
        {
            state.PlayerHp = Math.Min(
                state.PlayerMaxHp,
                state.PlayerHp + Effects.RelicEffects.MealTicketHeal
            );
        }

        state.Phase = RunPhase.Shop;
        Array.Clear(state.ShopCards);
        Array.Clear(state.ShopRelics);
        Array.Clear(state.ShopPotions);
        Array.Clear(state.ShopCosts);
        Array.Clear(state.ShopBaseCosts);

        int saleIndex = state.PlayerRng.Shops.NextInt(5);
        var blacklist = new List<int>();
        int[][] typedPools =
        [
            ShopPoolOfType(CardType.Attack, state.Character),
            ShopPoolOfType(CardType.Attack, state.Character),
            ShopPoolOfType(CardType.Skill, state.Character),
            ShopPoolOfType(CardType.Skill, state.Character),
            ShopPoolOfType(CardType.Power, state.Character),
        ];
        for (int i = 0; i < typedPools.Length; i++)
        {
            int rarity = RollCardRarity(
                state,
                (Ascension.Has(state.AscensionLevel, Ascension.Scarcity) ? 0.045f : 0.09f, 0.37f),
                mutateOffset: false,
                state.PlayerRng.Rewards
            );
            int cardId = ChooseCardWithRarity(
                state.Progression,
                typedPools[i],
                rarity,
                blacklist,
                state.PlayerRng.Shops
            );
            state.ShopCards[i] = cardId;
            blacklist.Add(cardId);
            state.PlayerRng.Rewards.NextDouble();
            int cost = ShopCardCost(cardId, colorless: false, state.PlayerRng.Shops);
            if (i == saleIndex)
            {
                // MerchantEntry.Populate calls CalcCost, and SetOnSale calls it again --
                // so the discounted slot prices itself twice, and the second roll is the
                // one that stands. Halving a single roll instead left the Shops stream a
                // draw short from here on, which is why every slot after the sale came
                // back with a different card.
                cost = ShopCardCost(cardId, colorless: false, state.PlayerRng.Shops) / 2;
            }

            state.ShopBaseCosts[i] = cost;
        }

        for (int i = 0; i < 2; i++)
        {
            int action = 5 + i;
            int rarity = i == 0 ? RarityUncommon : RarityRare;
            int cardId = ChooseCardWithRarity(
                state.Progression,
                ColorlessRewardPool,
                rarity,
                blacklist,
                state.PlayerRng.Shops
            );
            state.ShopCards[action] = cardId;
            blacklist.Add(cardId);
            state.PlayerRng.Rewards.NextDouble();
            state.ShopBaseCosts[action] = ShopCardCost(
                cardId,
                colorless: true,
                state.PlayerRng.Shops
            );
        }

        // MerchantInventory.PopulateRelicEntries builds its three slots as
        // [RollRarity, RollRarity, RelicRarity.Shop] and fills each at that rarity. The
        // two rolls were being made and then thrown away, with all three slots pulled at
        // Shop rarity -- so the rolls lined the stream up correctly and then the relics
        // came from the wrong queues.
        RelicRarity[] slotRarities =
        [
            RelicGrabBag.RollRarity(state.PlayerRng.Rewards),
            RelicGrabBag.RollRarity(state.PlayerRng.Rewards),
            RelicRarity.Shop,
        ];
        for (int i = 0; i < state.ShopRelics.Length; i++)
        {
            state.ShopRelics[i] = NextShopRelic(state, slotRarities[i]);
            state.ShopBaseCosts[7 + i] = ShopRelicCost(state.ShopRelics[i], state.PlayerRng.Shops);
        }

        // MerchantInventory.PopulatePotionEntries rolls all three potions in one
        // CreateRandomPotionsOutOfCombat call and only then builds the entries that price
        // them, so the draws are three picks followed by three costs -- not a pick and a
        // cost three times over. Interleaving them read the same number of values off the
        // Shops stream in the wrong order, which left every potion after the first
        // rolling its rarity against a price.
        var potionBlacklist = new List<int>();
        for (int i = 0; i < state.ShopPotions.Length; i++)
        {
            int potion = NextPotion(state, state.PlayerRng.Shops, potionBlacklist);
            state.ShopPotions[i] = potion;
            potionBlacklist.Add(potion);
        }

        for (int i = 0; i < state.ShopPotions.Length; i++)
        {
            state.ShopBaseCosts[10 + i] = ShopPotionCost(
                state.ShopPotions[i],
                state.PlayerRng.Shops
            );
        }
        bool inflation = Ascension.Has(state.AscensionLevel, Ascension.Inflation);
        state.ShopBaseCosts[RunConstants.ShopRemoveAction] =
            (inflation ? 100 : 75) + (inflation ? 50 : 25) * state.ShopRemovalsUsed;
        // A fresh merchant stocks the service again -- the price carries across shops,
        // the once-per-visit limit does not.
        state.ShopRemovalUsedThisVisit = false;

        RefreshShopQuotes(state);

        if (HasRelic(state, RunConstants.RelicLordsParasol))
        {
            state.ParasolSweepPending = true;
            ContinueLordsParasolSweep(state);
        }
    }

    /// <summary>
    /// `MerchantEntry.Cost`, for every slot: the entry's stored `_cost` (its base roll,
    /// a card's on-sale halving included) put through `Hook.ModifyMerchantPrice` as a
    /// decimal and truncated once. The game computes it afresh on every read, so a price
    /// relic obtained while the shop is open re-prices everything at once -- Membership
    /// Card bought mid-shop halves the rest of the stock and the removal service.
    /// `ShopCosts` is that quote; `ShopBaseCosts` is `_cost`. No RNG is read.
    /// </summary>
    internal static void RefreshShopQuotes(RunState state)
    {
        for (int i = 0; i < state.ShopCosts.Length; i++)
        {
            state.ShopCosts[i] = Effects.RelicEffects.ModifyMerchantPrice(
                state,
                state.ShopBaseCosts[i]
            );
        }
    }

    /// <summary>
    /// `LordsParasol.AfterRoomEntered(MerchantRoom)`: every entry bought with
    /// <c>ignoreCost: true</c>, in the game's order -- character cards, colourless cards,
    /// relics, potions -- and then the removal service, <c>cancelable: false</c>.
    /// </summary>
    /// <remarks>
    /// FREE, not merely afforded: `ignoreCost` skips the gold entirely, so the parasol
    /// never touches the run's gold and never closes Maw Bank. Both are `AfterItemPurchased`
    /// consequences of gold SPENT, and nothing is spent.
    ///
    /// RESUMABLE, because the game awaits every purchase: a relic whose own
    /// <c>AfterObtained</c> opens a card-select screen stops the sweep where it stands, and
    /// answering that screen calls back in here to finish. The shelves are the cursor --
    /// each entry is zeroed as it is taken -- so nothing else has to be remembered.
    ///
    /// A potion the belt has no room for is left on the shelf, the way `AddPotion` refuses
    /// one; the game logs the same as an out-of-stock purchase and moves on.
    /// </remarks>
    public static void ContinueLordsParasolSweep(RunState state)
    {
        if (!state.ParasolSweepPending)
        {
            return;
        }

        for (int i = 0; i < state.ShopCards.Length; i++)
        {
            if (state.ShopCards[i] != 0)
            {
                RunNonCombatEffects.AddCardToDeck(
                    state,
                    new CardInstance(state.ShopCards[i], Upgraded: false)
                );
                state.ShopCards[i] = 0;
            }
        }

        for (int i = 0; i < state.ShopRelics.Length; i++)
        {
            if (state.ShopRelics[i] == 0)
            {
                continue;
            }

            int relicId = state.ShopRelics[i];
            state.ShopRelics[i] = 0;
            var followUp = RunNonCombatEffects.ApplyRelicPickup(
                state,
                relicId,
                SelectionReturn.Shop
            );

            // The relic asked the player something. Everything after it -- the rest of the
            // shelf, the potions, the removal -- waits for the answer, which is what the
            // game's await does.
            if (followUp == RunFollowUp.CustomRewards)
                return;
            if (state.PendingSelectionKind != DeckSelection.None)
            {
                state.Phase = RunPhase.TransformSelect;
                return;
            }
        }

        for (int i = 0; i < state.ShopPotions.Length; i++)
        {
            if (state.ShopPotions[i] != 0 && AddPotion(state, state.ShopPotions[i]))
            {
                state.ShopPotions[i] = 0;
            }
        }

        state.ParasolSweepPending = false;

        if (
            !state.ShopRemovalUsedThisVisit
            && state.Deck.Count > 1
            && RunNonCombatEffects.BeginDeckSelection(
                state,
                DeckSelection.Remove,
                0,
                count: 1,
                returnTo: SelectionReturn.Shop
            )
        )
        {
            state.ShopRemovalUsedThisVisit = true;
            state.ShopRemovalsUsed++;
            state.Phase = RunPhase.TransformSelect;
        }
    }

    /// <summary>
    /// `DelicateFrond.BeforeCombatStart`: fill every OPEN potion slot, one
    /// `CreateRandomPotionOutOfCombat` off `Rng.CombatPotionGeneration` per slot.
    /// </summary>
    /// <remarks>
    /// The DRAW COUNT is the whole risk, which is why this was declared rather than guessed
    /// for so long -- a count that is wrong by one moves every later draw on that stream for
    /// the rest of the run, which is what E451 cost.
    ///
    /// The game's loop is `while (HasOpenPotionSlots)`: create, then `TryToProcure`, and
    /// BREAK if the procure fails. So it is one draw per open slot -- and exactly ONE draw
    /// when the procure is refused outright, which is Sozu. `AddPotion` already answers
    /// `TryToProcure`'s question, Sozu's gate included, so the loop reads the same either way.
    ///
    /// The blocker recorded against this relic was "the run has no before-combat hook that
    /// both entry paths share". It has one now: every combat in the run goes through
    /// `RunEngine.StartCombatWithDeck`, which is where this is called.
    /// </remarks>
    public static void FillPotionSlotsForDelicateFrond(RunState state)
    {
        if (!HasRelic(state, Effects.RelicEffects.DelicateFrond))
        {
            return;
        }

        int guard = state.PotionSlots.Length + 1;
        while (guard-- > 0 && HasOpenPotionSlot(state))
        {
            if (!AddPotion(state, NextPotion(state, state.Rng.CombatPotionGeneration)))
            {
                break;
            }
        }
    }

    /// <summary>`Player.HasOpenPotionSlots`, over the slots that actually exist yet.</summary>
    private static bool HasOpenPotionSlot(RunState state)
    {
        int usable = Math.Min(state.MaxPotionSlots, state.PotionSlots.Length);
        for (int i = 0; i < usable; i++)
        {
            if (state.PotionSlots[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    public static bool AddPotion(RunState state, int potionId)
    {
        // Sozu's ShouldProcurePotion is false for its owner, and PotionCmd is the game's
        // single gate for gaining one — so this covers rewards, events and shop buys alike.
        if (HasRelic(state, Effects.RelicEffects.Sozu))
        {
            return false;
        }

        int maxPotionSlots = Math.Min(state.MaxPotionSlots, state.PotionSlots.Length);
        for (int i = 0; i < maxPotionSlots; i++)
        {
            if (state.PotionSlots[i] != 0)
            {
                continue;
            }

            state.PotionSlots[i] = potionId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The game's <c>RelicFactory.PullNextRelicFromFront</c>: roll a rarity off the
    /// player's rewards stream, take the front of that rarity's queue, and strike it from
    /// the shared bag too. Shops call <see cref="NextShopRelic"/>, which reads the same
    /// queues from the back.
    ///
    /// This used to re-roll uniformly from a flat pool on the UpFront stream, filtered to
    /// relics the player did not already own. Wrong mechanism, wrong stream, and a rarity
    /// distribution that did not exist: the queue is the reason a run does not see the
    /// same relic twice, and the 50/33/17 rarity split is the reason it sees Commons most.
    /// </summary>
    public static int NextRelic(RunState state, GameRng? rngOverride = null) =>
        PullRelic(state, fromFront: true, rngOverride: rngOverride);

    /// <summary>
    /// A shop pull of a NAMED rarity, filtered to relics that may be sold. Welcome to
    /// Wongos asks for a Common for its bargain bin and a Rare for its featured item, so
    /// neither is the usual rolled rarity -- and both go through the shop filter, which
    /// five relics fail.
    /// </summary>
    public static int NextShopRelicOfRarity(RunState state, RelicRarity rarity)
    {
        var allowed = RelicGrabBag.AllowedInSoloRun(state.Floor);
        int? relicId = state.RelicBag.Pull(
            rarity,
            fromFront: true,
            allowed,
            relicId => GeneratedData.Relics.Get(relicId).IsAllowedInShops
        );
        if (relicId is null)
        {
            return FallbackRelic;
        }

        state.SharedRelicBag.Remove(relicId.Value);
        return relicId.Value;
    }

    /// <summary>
    /// MerchantRelicEntry.FillSlot pulls from the BACK with IsAllowedInShops as a
    /// per-pull filter. Skipped relics remain available to ordinary reward pulls.
    /// </summary>
    public static int NextShopRelic(RunState state, RelicRarity rarity = RelicRarity.Shop) =>
        PullRelic(
            state,
            fromFront: false,
            rarity,
            filter: relicId => GeneratedData.Relics.Get(relicId).IsAllowedInShops
        );

    private static int PullRelic(
        RunState state,
        bool fromFront,
        RelicRarity? rarity = null,
        GameRng? rngOverride = null,
        Func<int, bool>? filter = null
    )
    {
        var rolled = rarity ?? RelicGrabBag.RollRarity(rngOverride ?? state.PlayerRng.Rewards);
        var allowed = RelicGrabBag.AllowedInSoloRun(state.Floor);
        int? relicId = state.RelicBag.Pull(rolled, fromFront, allowed, filter);
        if (relicId is null)
        {
            // RelicFactory falls back to a fixed relic when the bag has nothing left.
            return FallbackRelic;
        }

        state.SharedRelicBag.Remove(relicId.Value);
        return relicId.Value;
    }

    /// <summary>
    /// <c>RelicFactory.FallbackRelic</c>, handed over when every queue is exhausted.
    /// </summary>
    private static int FallbackRelic =>
        GeneratedData.Relics.FindId("Circlet")
        ?? throw new InvalidOperationException("No relic named Circlet");

    /// <summary>
    /// <c>ScrollBoxes.GenerateRandomBundles</c>: two bundles of two Commons and one
    /// Uncommon, all six distinct, drawn from the character's pool on
    /// <c>PlayerRng.Rewards</c>.
    /// </summary>
    /// <remarks>
    /// Uniform odds with <c>NoRarityModification</c>, so there is no rarity roll and no
    /// upgrade roll: the only draws are the six <c>NextItem</c> picks, in bundle order,
    /// two Commons then the Uncommon. The <c>usedCardIds</c> set spans BOTH bundles, so
    /// the second bundle draws from a pool three cards smaller. The 1% all-Claw bundle is
    /// Defect's alone and its <c>NextInt(100)</c> sits behind a <c>flag &amp;&amp;</c> that
    /// never runs for anyone else, so it costs no draw here.
    /// </remarks>
    public static int[][] GenerateScrollBoxBundles(RunState state)
    {
        var rng = state.PlayerRng.Rewards;
        var used = new List<int>();
        var bundles = new int[2][];
        for (int bundle = 0; bundle < 2; bundle++)
        {
            var cards = new int[3];
            for (int card = 0; card < 2; card++)
            {
                cards[card] = rng.NextItem(BundleCandidates(state, used, RarityCommon));
                used.Add(cards[card]);
            }

            cards[2] = rng.NextItem(BundleCandidates(state, used, RarityUncommon));
            used.Add(cards[2]);
            bundles[bundle] = cards;
        }

        return bundles;
    }

    private static int[] BundleCandidates(RunState state, List<int> used, int rarity) =>
        PlayerCardPool(state)
            .ToArray()
            .Where(cardId =>
                state.Progression.IsCardAvailableInSinglePlayer(
                    GeneratedData.Cards.Get(cardId).Entry
                )
                && RarityOf(cardId) == rarity
                && !used.Contains(cardId)
            )
            .ToArray();

    /// <summary>
    /// <c>RewardsSet</c>: <c>GoldReward(Encounter.MinGoldReward * GoldProportion,
    /// Encounter.MaxGoldReward * GoldProportion)</c>. A monster room's 10-20 becomes 7-15
    /// once A8's Poverty multiplier is applied, which is the range below.
    /// </summary>
    /// <remarks>
    /// The Gremlin Merc encounter used to return a flat 0 here, which was right for the
    /// runs it was fitted to and wrong about why. Its <c>CalculateGoldProportion</c> pays
    /// in FULL when nothing escaped — `9V9WN98106` kills the fat gremlin and is paid 9 —
    /// half when a gremlin escaped having stolen nothing, and nothing at all when one
    /// escaped with the loot, which is what `WK1DEGZD8P`, `J09SPL8Y3V` and `NXV45HW43K`
    /// each captured. A zero proportion means no reward and therefore **no draw**:
    /// <c>RewardsSet</c> guards the whole row behind <c>if (GoldProportion > 0f)</c>, so a
    /// run that rolls one anyway is off by a value for the rest of the act.
    /// </remarks>
    private static int GoldRewardForCurrentNode(RunState state)
    {
        bool poverty = Ascension.Has(state.AscensionLevel, Ascension.Poverty);
        if (state.CurrentNodeType == RunConstants.NodeElite)
        {
            return state.PlayerRng.Rewards.NextInt(poverty ? 26 : 35, poverty ? 34 : 46);
        }

        if (state.CurrentNodeType == RunConstants.NodeBoss)
        {
            // A boss's Min and Max are both 100, and A8's Poverty multiplier takes a
            // quarter off BEFORE the roll -- the same 0.75 already baked into the monster
            // range (10-20 -> 7-15) and the elite one (35-45 -> 26-33). Only this branch
            // was written as a flat 100, and no capture could contradict it until one
            // finally won an act.
            return state.PlayerRng.Rewards.NextInt(poverty ? 75 : 100, poverty ? 76 : 101);
        }

        float proportion = CombatGoldProportion(state);
        if (proportion <= 0)
        {
            return 0;
        }

        // Math.Round on the SCALED bounds, the way RewardsSet does it -- not a scaled roll.
        int min = CombatGoldBound(poverty ? 7 : 10, proportion);
        int max = CombatGoldBound(poverty ? 15 : 20, proportion);
        return state.PlayerRng.Rewards.NextInt(min, max + 1);
    }

    /// <summary>
    /// <c>RewardsSet</c>: <c>(int)Math.Round((float)MinGoldReward * GoldProportion)</c>. The
    /// product is float, and <c>Math.Round</c> binds to its double overload, which rounds a
    /// midpoint to even.
    /// </summary>
    internal static int CombatGoldBound(int bound, float proportion) =>
        (int)Math.Round((float)bound * proportion);

    /// <summary>
    /// <c>CombatRoom.GoldProportion</c>. Every encounter is 1 except the Gremlin Merc's,
    /// the only one that overrides <c>CalculateGoldProportion</c>.
    /// </summary>
    private static float CombatGoldProportion(RunState state)
    {
        var combat = state.ActiveCombat;
        if (
            combat is null
            || combat.EncounterId != RunConstants.GremlinMercEncounterId
            || !combat.FatGremlinEscaped
        )
        {
            return 1;
        }

        return combat.MercGoldWasStolen ? 0f : 0.5f;
    }

    /// <summary>The RegularEncounter rarity odds: rare, then uncommon.</summary>
    private static (float Rare, float Uncommon) RegularEncounterCardOdds(RunState state) =>
        (Ascension.Has(state.AscensionLevel, Ascension.Scarcity) ? 0.0149f : 0.03f, 0.37f);

    private static int RollRewardCardRarity(RunState state, CardRewardOptions? options = null)
    {
        options ??= new CardRewardOptions(state.CurrentNodeType);
        var odds = options.RoomType switch
        {
            RunConstants.NodeElite => (
                Ascension.Has(state.AscensionLevel, Ascension.Scarcity) ? 0.05f : 0.1f,
                0.4f
            ),
            RunConstants.NodeBoss => (1f, 0f),
            _ => RegularEncounterCardOdds(state),
        };
        return RollCardRarity(
            state,
            odds,
            options.EncounterSource,
            state.PlayerRng.Rewards,
            useOffset: options.EncounterSource
        );
    }

    /// <param name="useOffset">
    /// Whether the running rare-chance offset applies. CardFactory.RollForRarity picks
    /// <c>Roll</c> — which reads and grows the offset — only when the card is made for an
    /// ENCOUNTER; everything else takes <c>RollWithBaseOdds</c>, which uses the flat odds
    /// and leaves the offset alone. Kaleidoscope creates with CardCreationSource.Other,
    /// so applying the offset there skewed its rarities rare AND spent the pity timer the
    /// next real combat reward was owed.
    /// </param>
    private static int RollCardRarity(
        RunState state,
        (float Rare, float Uncommon) odds,
        bool mutateOffset,
        GameRng rng,
        bool useOffset = true
    )
    {
        // CardRarityOdds stores and adds floats, including rounding after each growth.
        float offset = !useOffset || odds.Rare >= 1f ? 0f : (float)state.CardRarityOffset;
        float roll = rng.NextFloat();
        float rareThreshold = odds.Rare + offset;
        // v0.111.0 CardRarityOdds adds the uncommon band above the rare threshold
        // in both RollWithoutChangingFutureOdds and RollWithBaseOdds.
        float uncommonThreshold = rareThreshold + odds.Uncommon;
        int rarity =
            roll < rareThreshold ? RarityRare
            : roll < uncommonThreshold ? RarityUncommon
            : RarityCommon;

        if (mutateOffset)
        {
            state.CardRarityOffset =
                rarity == RarityRare
                    ? CardRarityBaseOffset
                    : Math.Min(
                        (float)state.CardRarityOffset + CardRarityGrowth(state),
                        CardRarityMaxOffset
                    );
        }
        return rarity;
    }

    private static int ChooseCardWithRarity(
        ProgressionContext progression,
        ReadOnlySpan<int> pool,
        int rarity,
        List<int> blacklist,
        GameRng rng
    )
    {
        // CardFactory.FilterForPlayerCount runs on the pool before anything is rolled from
        // it, so every branch below chooses from the same solo-legal set — including the
        // last-resort one, which used to reach past the blacklist into the raw pool.
        var allowed = pool.ToArray()
            .Where(id =>
                progression.IsCardAvailableInSinglePlayer(GeneratedData.Cards.Get(id).Entry)
            )
            .ToArray();

        foreach (int allowedRarity in RarityFallbacks(rarity))
        {
            var available = allowed
                .Where(cardId => !blacklist.Contains(cardId) && RarityOf(cardId) == allowedRarity)
                .ToArray();
            if (available.Length > 0)
            {
                return rng.NextItem(available);
            }
        }

        var fallback = allowed.Where(cardId => !blacklist.Contains(cardId)).ToArray();
        return rng.NextItem(fallback.Length > 0 ? fallback : allowed);
    }

    /// <summary>
    /// CardFactory.FilterForPlayerCount: a solo run drops every MultiplayerOnly card from
    /// the pool before the rarity roll, so the choice is made over a smaller set — not just
    /// re-rolled when one comes up.
    /// </summary>
    public static bool IsAllowedSolo(int cardId) =>
        !GeneratedData.Cards.Get(cardId).MultiplayerOnly;

    private static bool RollCardUpgrade(RunState state, int cardId, GameRng rng)
    {
        // CardFactory.RollForUpgrade draws even for non-upgradable or rare cards.
        // The comparison converts the game's float to decimal before comparing.
        decimal roll = (decimal)rng.NextFloat();
        var card = GeneratedData.Cards.Get(cardId);
        decimal chance =
            card.Rarity == CardRarity.Rare
                ? 0m
                : state.CurrentActIndex
                    * (Ascension.Has(state.AscensionLevel, Ascension.Scarcity) ? 0.125m : 0.25m);
        return card.Upgradable && roll <= chance;
    }

    private static bool CheckPotionRoll(RunState state)
    {
        // Original PotionRewardOdds.Roll returns immediately when the hook forces a
        // potion. It consumes no Rewards draw and leaves the pity value unchanged.
        float currentValue = state.PotionRewardOdds;
        bool forced =
            HasRelic(state, RunConstants.RelicWhiteBeastStatue)
            && state.CurrentNodeType
                is RunConstants.NodeNormal
                    or RunConstants.NodeElite
                    or RunConstants.NodeBoss;
        if (forced)
            return true;

        // The original odds, bonus, roll and updates are all single precision.
        // Keeping a double pity value changes later thresholds after repeated sets.
        float eliteBonus = state.CurrentNodeType == RunConstants.NodeElite ? 0.25f : 0f;
        float threshold = currentValue + eliteBonus * 0.5f;
        float roll = state.PlayerRng.Rewards.NextFloat();
        if (roll < threshold)
        {
            state.PotionRewardOdds -= PotionRewardStep;
            return true;
        }
        state.PotionRewardOdds += PotionRewardStep;
        return false;
    }

    public static int NextPotion(RunState state, GameRng rng, List<int>? blacklist = null)
    {
        var available = AvailablePotions(
            state.Progression,
            RollPotionRarity(rng.NextFloat()),
            blacklist,
            state.Character
        );
        return rng.NextItem(available);
    }

    /// <summary>
    /// `PotionFactory.CreateRandomPotionsOutOfCombat(count)`: CreateRandomPotions removes
    /// each pick from its option list before the next roll, so one batch never repeats a
    /// potion and every later NextItem indexes the shorter list.
    /// </summary>
    public static List<int> NextPotionsOutOfCombat(RunState state, int count, GameRng rng)
    {
        var picked = new List<int>();
        for (int i = 0; i < count; i++)
            picked.Add(NextPotion(state, rng, picked));
        return picked;
    }

    /// <summary>Combat generation uses the same ordered, unlocked potion pool.</summary>
    public static int NextPotion(
        ProgressionContext progression,
        Random rng,
        CharacterId character = CharacterId.Ironclad
    )
    {
        var available = AvailablePotions(
            progression,
            RollPotionRarity((float)rng.NextDouble()),
            character: character
        );
        return available[rng.Next(available.Length)];
    }

    /// <summary>
    /// `PotionFactory.CreateRandomPotionInCombat` (PotionFactory.cs:64-74, 76-90): the
    /// ordered character-then-Shared unlocked pool, filtered to
    /// <see cref="PotionDef.CanBeGeneratedInCombat" />, then `NextFloat` for the rarity
    /// (&lt;= 0.1f Rare, &lt;= 0.35f Uncommon) and `NextItem` over that rarity in pool
    /// order -- two draws. Unlike the reward roll there is no fallback: an empty rarity
    /// makes `NextItem` return null without drawing, reported here as 0.
    /// </summary>
    public static int NextCombatPotion(
        ProgressionContext progression,
        Random rng,
        CharacterId character = CharacterId.Ironclad
    )
    {
        int rarity = RollPotionRarity((float)rng.NextDouble());
        var available = CharacterCatalog
            .PotionPool(character)
            .ToArray()
            .Where(progression.IsPotionIdUnlocked)
            .Where(potionId => GeneratedData.Potions.Get(potionId).CanBeGeneratedInCombat)
            .Where(potionId => PotionRarity(potionId) == rarity)
            .ToArray();
        return available.Length > 0 ? available[rng.Next(available.Length)] : 0;
    }

    private static int[] AvailablePotions(
        ProgressionContext progression,
        int rarity,
        List<int>? blacklist = null,
        CharacterId character = CharacterId.Ironclad
    )
    {
        var blocked = blacklist ?? [];
        var unlocked = CharacterCatalog
            .PotionPool(character)
            .ToArray()
            .Where(progression.IsPotionIdUnlocked)
            .ToArray();
        var available = unlocked
            .Where(potionId => !blocked.Contains(potionId) && PotionRarity(potionId) == rarity)
            .ToArray();
        return available.Length > 0 ? available : unlocked;
    }

    /// <summary>
    /// `Rewards.NextItem(Character.PotionPool.GetUnlockedPotions(...).Concat(
    /// SharedPotionPool.GetUnlockedPotions(...)))`: ONE uniform draw over every unlocked
    /// potion, character pool first, with no rarity roll. Wellspring's Bottle, The Legends
    /// Were True, Battleworn Dummy's potion setting and Endless Conveyor's Suspicious
    /// Condiment all pick this way and hand the result to `new PotionReward(potion, ...)`,
    /// which is already populated -- a different draw, over a different set, from
    /// <see cref="NextPotion(RunState, GameRng, List{int}?)" />'s rarity-first
    /// `PotionReward.Populate`.
    /// </summary>
    public static int NextUnlockedPotion(RunState state, GameRng rng) =>
        rng.NextItem(
            CharacterCatalog
                .PotionPool(state.Character)
                .ToArray()
                .Where(state.Progression.IsPotionIdUnlocked)
                .ToArray()
        );

    /// <summary>
    /// The Potion Courier's Ransack rolls only among Uncommon potions, so it does NOT
    /// roll a rarity first -- it filters the pool and takes one.
    /// </summary>
    public static int NextUncommonPotion(RunState state, GameRng rng)
    {
        var available = CharacterCatalog
            .PotionPool(state.Character)
            .ToArray()
            .Where(state.Progression.IsPotionIdUnlocked)
            .Where(potionId => PotionRarity(potionId) == RarityUncommon)
            .ToArray();
        return available.Length > 0 ? rng.NextItem(available) : NextPotion(state, rng);
    }

    private static int RollPotionRarity(float roll)
    {
        if (roll <= 0.1f)
        {
            return RarityRare;
        }

        if (roll <= 0.35f)
        {
            return RarityUncommon;
        }

        return RarityCommon;
    }

    private static int RollRelicRarity(GameRng rng)
    {
        double roll = rng.NextDouble();
        return roll < 0.5 ? RarityCommon
            : roll < 0.83 ? RarityUncommon
            : RarityRare;
    }

    /// <summary>
    /// What the merchant asks for a card before any discount. Internal so a test can
    /// tell a sale slot from a cheap card.
    /// </summary>
    internal static int ShopCardCost(int cardId, bool colorless, GameRng rng)
    {
        int baseCost = RarityOf(cardId) switch
        {
            RarityRare => 150,
            RarityUncommon => 75,
            _ => 50,
        };
        if (colorless)
        {
            baseCost = RoundToEven(baseCost * 1.15f);
        }

        // MerchantCardEntry.CalcCost is float arithmetic end to end, rounded the way
        // Mathf.RoundToInt rounds.
        return RoundToEven(baseCost * NextFloat(rng, 0.95f, 1.05f));
    }

    /// <summary>
    /// The game's <c>MerchantRelicEntry.CalcCost</c>: the relic's MerchantCost jittered
    /// and rounded. MerchantCost is not per-relic data at all -- RelicModel derives it
    /// from the rarity, and the only relics that override it are the Fake Merchant's,
    /// whose base price is 50 before this jitter. A hand-written table of base costs used to stand in for
    /// it, defaulting anything it did not know to 200.
    /// </summary>
    internal static int ShopRelicCost(int relicId, GameRng rng)
    {
        var def = GeneratedData.Relics.Get(relicId);
        int baseCost = def.Name.StartsWith("Fake", StringComparison.Ordinal)
            ? 50
            : def.Rarity switch
            {
                RelicRarity.Common => 175,
                RelicRarity.Uncommon => 225,
                RelicRarity.Rare => 275,
                RelicRarity.Shop => 200,
                RelicRarity.None => 1,
                // Ancient, Starter and Event relics are priced out of reach rather than
                // excluded, which is the game's way of saying they are never for sale.
                _ => 999999999,
            };

        return RoundToEven(baseCost * NextFloat(rng, 0.85f, 1.15f));
    }

    internal static int ShopPotionCost(int potionId, GameRng rng)
    {
        int baseCost = PotionRarity(potionId) switch
        {
            RarityRare => 100,
            RarityUncommon => 75,
            _ => 50,
        };
        // MerchantPotionEntry.CalcCost: (int)Mathf.Round((float)_cost * NextFloat(0.95f,
        // 1.05f)) -- float end to end and rounded to even, like the cards and relics.
        return RoundToEven(baseCost * NextFloat(rng, 0.95f, 1.05f));
    }

    /// <summary>
    /// The game's <c>Rng.NextFloat(min, max)</c>: computed in double, then cast to
    /// float. The cast is not cosmetic -- the merchant multiplies a price by this and
    /// rounds, so carrying the extra double precision moves the odd price by one gold.
    /// </summary>
    private static float NextFloat(GameRng rng, float min, float max) =>
        (float)(rng.NextDouble() * (double)(max - min) + min);

    /// <summary>
    /// Godot's <c>Mathf.RoundToInt</c>, which is <c>Math.Round</c> -- and .NET rounds a
    /// midpoint to even, not away from zero. It matters here because merchant prices land
    /// on a midpoint exactly: a Rare colourless card is 150 * 1.15f, which is 172.5f to
    /// the bit, and the two rules disagree by a gold on every one of them.
    /// </summary>
    private static int RoundToEven(double value) => (int)Math.Round(value);

    private static bool HasRelic(RunState state, int relicId) =>
        state.Relics.Any(relic => relic.DefId == relicId);

    /// <summary>
    /// A card's rarity, read from the extracted card data.
    ///
    /// This used to be a hand-written table of 144 ids that defaulted to Common — fine
    /// for the Ironclad pool it was built from, wrong for everything else: 249 Uncommon
    /// and Rare cards were absent from it and so read as Common, which let a Common roll
    /// hand back a Rare. Kaleidoscope draws from the other characters' pools and hit it
    /// on nearly every card. The table agreed with the extracted data on every id it did
    /// carry, so nothing is lost by reading the data instead.
    /// </summary>
    private static int RarityOf(int cardId) => (int)GeneratedData.Cards.Get(cardId).Rarity;

    /// <summary>
    /// The card rarity a potion trades for at The Future of Potions, from the event's own
    /// GetCardRarity: Rare and Event potions buy Rare cards, Uncommon buys Uncommon, and
    /// Common and Token buy Common.
    /// </summary>
    public static CardRarity CardRarityForPotion(int potionId) =>
        GeneratedData.Potions.Get(potionId).Rarity switch
        {
            Core.PotionRarity.Rare or Core.PotionRarity.Event => CardRarity.Rare,
            Core.PotionRarity.Uncommon => CardRarity.Uncommon,
            _ => CardRarity.Common,
        };

    /// <summary>
    /// The card types a potion can buy at The Future of Potions. Power is off the table
    /// for a Common or Token potion -- and the reason is in the pool: the Ironclad has no
    /// Common Power at all, so rolling one would filter the reward down to nothing and
    /// offer the player an empty screen.
    /// </summary>
    public static CardType[] FutureOfPotionsCardTypes(int potionId) =>
        GeneratedData.Potions.Get(potionId).Rarity
            is Core.PotionRarity.Common
                or Core.PotionRarity.Token
            ? [CardType.Attack, CardType.Skill]
            : [CardType.Attack, CardType.Skill, CardType.Power];

    /// <summary>
    /// The Future of Potions offers THREE upgraded cards, all of one rarity and one type:
    /// the rarity comes from the potion handed over, and the type is rolled per potion
    /// from Attack/Skill/Power -- with Power off the table for a Common or Token potion,
    /// which cannot buy one.
    ///
    /// The odds are uniform over the filtered pool, not the usual rarity roll, so this
    /// picks cards rather than rolling a rarity first.
    /// </summary>
    public static CardRewardOptions FutureOfPotionsRewardOptions(int potionId, CardType type) =>
        new(RunConstants.NodeNormal, EncounterSource: false, IsFromCombat: false)
        {
            FixedRarity = CardRarityForPotion(potionId),
            FixedType = type,
            AllowPoolModifications = false,
            NoUpgradeRoll = true,
            UpgradeAfterGenerated = true,
        };

    /// <summary>
    /// A potion's rarity, from the extracted potion data. A hand-written table stood
    /// here and defaulted anything it did not list to Common, which put every potion it
    /// had never heard of into the bucket a Common roll draws from.
    /// </summary>
    private static int PotionRarity(int potionId) =>
        GeneratedData.Potions.Get(potionId).Rarity switch
        {
            Core.PotionRarity.Rare => RarityRare,
            Core.PotionRarity.Uncommon => RarityUncommon,
            _ => RarityCommon,
        };

    private static int[] RarityFallbacks(int rarity) =>
        rarity switch
        {
            RarityCommon => [RarityCommon, RarityUncommon, RarityRare],
            RarityUncommon => [RarityUncommon, RarityRare, RarityCommon],
            _ => [RarityRare, RarityCommon, RarityUncommon],
        };
}
