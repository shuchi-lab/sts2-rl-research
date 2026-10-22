namespace Sts2Emulator.Core;

public static class CombatObservation
{
    public const int MaxHand = 10;
    public const int MaxEnemies = 6;
    public const int MaxPlayerBuffs = 10;
    public const int MaxEnemyBuffs = 5;

    /// <summary>Compact candidate prefix; full pile choices are exposed by run state list34.</summary>
    public const int MaxSelectionCandidates = 10;

    /// <summary>Hp, max hp, block, energy, max energy, and the three pile sizes.</summary>
    public const int ScalarCount = 8;

    /// <summary>
    /// Per card: def id, upgraded, enchantment, and the amount it was applied at. The last
    /// two used to be missing, so a Sharp Strike and a plain one were the same two numbers
    /// -- and the enchantment is worth 2 damage on every attack, which is the difference
    /// between a card being worth playing and not.
    /// </summary>
    public const int CardSlotSize = 4;

    public const int HandOffset = ScalarCount;
    public const int PotionSlotSize = 2;

    /// <summary>
    /// Potion slots the observation carries. Fixed at the run's capacity rather than at
    /// the three a run starts combat with: Alchemical Coffer alone takes a run to six,
    /// and an observation three wide cannot show the potions it bought. Widening the
    /// layout is why this was recorded rather than done in passing -- see O15.
    /// </summary>
    public const int MaxPotions = Run.RunConstants.PotionSlotCapacity;
    public const int PotionOffset = HandOffset + MaxHand * CardSlotSize;
    public const int BuffSlotSize = 2;
    public const int PlayerBuffOffset = PotionOffset + MaxPotions * PotionSlotSize;

    /// <summary>Hp, max hp, block, intent type, announced magnitude, then the buffs.</summary>
    public const int EnemySlotSize = 5 + MaxEnemyBuffs * BuffSlotSize;

    /// <summary>Where an enemy slot carries what it means to do this turn.</summary>
    public const int EnemyIntentField = 3;

    public const int EnemyOffset = PlayerBuffOffset + MaxPlayerBuffs * BuffSlotSize;
    public const int SecondaryIntentSlotSize = 2;
    public const int SecondaryIntentOffset = EnemyOffset + MaxEnemies * EnemySlotSize;
    public const int GoldOffset = SecondaryIntentOffset + MaxEnemies * SecondaryIntentSlotSize;
    public const int SelectionKindOffset = GoldOffset + 1;
    public const int SelectionCountOffset = SelectionKindOffset + 1;
    public const int SelectionOffset = SelectionCountOffset + 1;

    /// <summary>
    /// The orb ring. <c>Recursion</c> and its kin cap capacity at 10, so ten slots hold
    /// any legal ring.
    /// </summary>
    public const int MaxOrbs = 10;

    /// <summary>
    /// Per orb: type + 1 (so an empty slot reads 0, since Lightning is 0), then what the
    /// orb's passive and evoke are WORTH right now.
    /// </summary>
    /// <remarks>
    /// The two values are carried rather than left to be derived because they are not
    /// derivable from the type alone: a Dark orb's evoke is whatever it has banked, and a
    /// Glass orb's is whatever it has left to spend. Neither appears anywhere else in the
    /// observation, and both are the whole reason to hold the orb rather than evoke it.
    /// </remarks>
    public const int OrbSlotSize = 3;

    /// <summary>
    /// How many orbs the player can hold. Channelling into a full ring evokes the oldest
    /// orb, so an agent that cannot see the capacity cannot tell channelling from
    /// channelling-and-spending.
    /// </summary>
    public const int OrbCapacityOffset = SelectionOffset + MaxSelectionCandidates * CardSlotSize;

    public const int OrbOffset = OrbCapacityOffset + 1;

    // Appended after the selection block, not filed with the player's own scalars: every
    // offset above is a committed layout that fixtures and any trained policy are written
    // against, and inserting a field renumbers all of them. Same discipline as BuffId.
    /// <summary>
    /// Cards already promised to the hand at the start of the next turn, in the order
    /// they will arrive: <c>ReturnToHandBeforeDraw</c> first, then
    /// <c>CopiesToHandBeforeDraw</c>.
    /// </summary>
    /// <remarks>
    /// `NightmarePower` is a visible power in the game and its `StringVar("Card")` NAMES
    /// what it is holding, so a player can see three Backstabs coming and plan the energy
    /// for them. E425 made the COUNT observable by giving the power a BuffId; this is the
    /// other half, and it closes O25. Five slots covers a Nightmare's three with room for
    /// the return-to-hand list beside it.
    /// </remarks>
    public const int MaxIncomingCards = 5;

    public const int IncomingOffset = OrbOffset + MaxOrbs * OrbSlotSize;

    // Full declared move: three ordered intents, each (type + 1, damage per hit,
    // hit count). Zero marks an absent intent; non-attacks have zero damage/hits.
    // Appended to preserve existing combat offsets. The run suffix follows ObsSize.
    public const int IntentOffset = IncomingOffset + MaxIncomingCards * CardSlotSize;
    public const int MaxIntentsPerEnemy = 3;
    public const int IntentSlotSize = 3;
    public const int ObsSize = IntentOffset + MaxEnemies * MaxIntentsPerEnemy * IntentSlotSize;

    /// <summary>Writes one card into a slot: what it is, and what has been done to it.</summary>
    private static void WriteCard(Span<int> obs, int at, CardInstance card)
    {
        obs[at] = card.DefId;
        obs[at + 1] = card.Upgraded ? 1 : 0;
        obs[at + 2] = (int)card.Enchantment;
        obs[at + 3] = card.EnchantAmount;
    }

    /// <summary>
    /// What the game shows on an attack intent. AttackIntent.GetSingleDamage runs the
    /// move's damage through Hook.ModifyDamage before displaying it, so the number the
    /// player reads already includes the attacker's Strength and its own Weak — reporting
    /// the raw move damage would tell a policy a Ritual-stacking cultist still hits for
    /// nine on the turn it hits for fifteen. Non-attack intents carry a count, not damage.
    /// </summary>
    private static int AnnouncedMagnitude(CombatState s, EnemyState enemy) =>
        enemy.CurrentIntent.AnnouncedDamage(
            enemy.Buffs,
            s.PlayerBuffs,
            // Paper Krane deepens the attacker's Weak, and the readout runs through the
            // same `Hook.ModifyDamage` chain -- so the announced number moves with it, the
            // way it already moves with Strength and the Crab's Surrounded.
            Effects.RelicEffects.WeakMultiplierDeltaAgainstPlayer(s)
        );

    /// <summary>
    /// The amount the game DISPLAYS for a player power, shared by the compact vector and
    /// the full run state list 37 so the two cannot disagree. Display counters can differ
    /// from the internal effect state: Tender shows the cards played this turn, and
    /// Surrounded has source Amount(1), with Facing kept in a separate source field.
    /// AutomationPower.DisplayAmount is its `cardsLeft` countdown from 10, while its Amount
    /// (the magnitude here) is the energy per payout. The source counter is per instance;
    /// native keeps one shared counter, which is exact for a single Automation.
    /// </summary>
    public static int PlayerBuffDisplayAmount(CombatState s, BuffState buff) =>
        buff.Id switch
        {
            BuffId.Tender => s.TenderCardsPlayedThisTurn,
            // SlothPower.DisplayAmount is `_cardsPlayedThisTurn`; Amount is the cap.
            BuffId.Sloth => s.SlothCardsPlayedThisTurn,
            BuffId.Surrounded => 1,
            BuffId.AutomationPower => 10 - s.DrawnCardsSinceAutomationProc,
            _ => buff.Magnitude,
        };

    public static void Write(CombatState s, Span<int> obs)
    {
        if (obs.Length < ObsSize)
        {
            throw new ArgumentException("Combat observation buffer is too small.", nameof(obs));
        }

        obs[..ObsSize].Clear();
        obs[0] = s.PlayerHp;
        obs[1] = s.PlayerMaxHp;
        obs[2] = s.PlayerBlock;
        obs[3] = s.Energy;
        obs[4] = CombatEngine.EffectiveMaxEnergyForTurn(s, s.Turn + 1);
        obs[5] = s.DrawPile.Count;
        obs[6] = s.DiscardPile.Count;
        obs[7] = s.ExhaustPile.Count;

        for (int i = 0; i < MaxHand && i < s.Hand.Count; i++)
        {
            WriteCard(obs, HandOffset + i * CardSlotSize, s.Hand[i]);
        }

        int offset = PotionOffset;
        for (int i = 0; i < MaxPotions; i++)
        {
            int potion = i < s.PotionSlots.Length ? s.PotionSlots[i] : 0;
            obs[offset + i * PotionSlotSize] = potion;
            obs[offset + i * PotionSlotSize + 1] = potion != 0 ? 1 : 0;
        }

        // Cards already promised to the next hand, in delivery order: EndTurn hands over
        // `ReturnToHandBeforeDraw` and then `CopiesToHandBeforeDraw`. Without this an
        // agent pays three energy for a Nightmare and cannot see what it bought until the
        // cards turn up -- the game shows the card's NAME on the power.
        offset = IncomingOffset;
        int incoming = 0;
        foreach (var card in s.ReturnToHandBeforeDraw.Concat(s.CopiesToHandBeforeDraw))
        {
            if (incoming >= MaxIncomingCards)
            {
                break;
            }

            WriteCard(obs, offset + incoming * CardSlotSize, card);
            incoming++;
        }

        offset = PlayerBuffOffset;
        for (int i = 0; i < MaxPlayerBuffs; i++)
        {
            if (i < s.PlayerBuffs.Count)
            {
                obs[offset + i * BuffSlotSize] = (int)s.PlayerBuffs[i].Id;
                obs[offset + i * BuffSlotSize + 1] = PlayerBuffDisplayAmount(s, s.PlayerBuffs[i]);
            }
        }

        offset = EnemyOffset;
        int enemySlotSize = EnemySlotSize;
        for (int enemyIndex = 0; enemyIndex < MaxEnemies; enemyIndex++)
        {
            int baseIndex = offset + enemyIndex * enemySlotSize;
            if (enemyIndex >= s.Enemies.Count)
            {
                continue;
            }

            var enemy = s.Enemies[enemyIndex];
            obs[baseIndex] = enemy.Hp;
            obs[baseIndex + 1] = enemy.MaxHp;
            obs[baseIndex + 2] = enemy.Block;
            // The legacy compact slot has no absent-intent code. Keep its no-op
            // placeholder while the structured intent list below accurately stays empty.
            obs[baseIndex + 3] = (int)(
                EnemyAI.DeclaredIntent(enemy, 0)?.Type ?? IntentType.Unknown
            );
            obs[baseIndex + 4] = AnnouncedMagnitude(s, enemy);
            for (int buffIndex = 0; buffIndex < MaxEnemyBuffs; buffIndex++)
            {
                if (buffIndex < enemy.Buffs.Count)
                {
                    obs[baseIndex + 5 + buffIndex * 2] = (int)enemy.Buffs[buffIndex].Id;
                    var buff = enemy.Buffs[buffIndex];
                    obs[baseIndex + 5 + buffIndex * 2 + 1] =
                        buff.Id == BuffId.HardenedShell
                            ? Math.Max(0, buff.Magnitude - enemy.HardenedShellHpLostThisSide)
                            : buff.Magnitude;
                }
            }
        }

        offset = SecondaryIntentOffset;
        for (int enemyIndex = 0; enemyIndex < MaxEnemies; enemyIndex++)
        {
            if (
                enemyIndex < s.Enemies.Count
                && EnemyAI.DeclaredIntent(s.Enemies[enemyIndex], 1) is { } secondary
            )
            {
                obs[offset + enemyIndex * SecondaryIntentSlotSize] = (int)secondary.Type + 1;
                obs[offset + enemyIndex * SecondaryIntentSlotSize + 1] = secondary.Magnitude;
            }
        }

        for (int enemyIndex = 0; enemyIndex < Math.Min(MaxEnemies, s.Enemies.Count); enemyIndex++)
        {
            var enemy = s.Enemies[enemyIndex];
            if (enemy.Hp <= 0)
                continue;
            for (int i = 0; i < MaxIntentsPerEnemy; i++)
            {
                if (EnemyAI.DeclaredIntent(enemy, i) is not { } intent)
                    continue;
                int at = IntentOffset + (enemyIndex * MaxIntentsPerEnemy + i) * IntentSlotSize;
                obs[at] = (int)intent.Type + 1;
                if (intent.Type is IntentType.Attack or IntentType.DeathBlow && intent.Hits > 0)
                {
                    obs[at + 1] =
                        intent.AnnouncedDamage(
                            enemy.Buffs,
                            s.PlayerBuffs,
                            Effects.RelicEffects.WeakMultiplierDeltaAgainstPlayer(s)
                        ) / intent.Hits;
                    obs[at + 2] = intent.Hits;
                }
            }
        }

        obs[GoldOffset] = s.PlayerGold;

        // An open card selection replaces the action space, so a policy is blind without
        // knowing both that one is open and what it is choosing between.
        if (s.PendingSelection is { } selection)
        {
            obs[SelectionKindOffset] = (int)selection.Kind;
            obs[SelectionCountOffset] = selection.Candidates.Count;

            for (int i = 0; i < Math.Min(MaxSelectionCandidates, selection.Candidates.Count); i++)
                WriteCard(obs, SelectionOffset + i * CardSlotSize, SelectionCard(s, selection, i));
        }

        // Defect's whole character is the ring, and none of it was here: not the orbs, not
        // their order, not Dark's banked total or Glass's remaining charge. An agent
        // cannot learn when to evoke something it cannot see (O26).
        obs[OrbCapacityOffset] = s.OrbCapacity;
        for (int i = 0; i < MaxOrbs && i < s.Orbs.Count; i++)
        {
            var orb = s.Orbs[i];
            var (passive, evoke) = Effects.CardEffects.OrbDisplayValues(s, orb);
            int at = OrbOffset + i * OrbSlotSize;
            // Type + 1: Lightning is 0, so a raw type cannot be told from an empty slot.
            // The secondary-intent block uses the same offset for the same reason.
            obs[at] = (int)orb.Type + 1;
            obs[at + 1] = passive;
            obs[at + 2] = evoke;
        }
    }

    internal static CardInstance SelectionCard(
        CombatState state,
        PendingCardSelection selection,
        int ordinal
    )
    {
        int index = selection.Candidates[ordinal];
        if (
            selection.Kind
            is CardSelectionKind.GeneratedCardToHand
                or CardSelectionKind.CurseOfKnowledge
        )
            return new CardInstance(selection.GeneratedCandidates[index], false);
        var pile = selection.Kind switch
        {
            CardSelectionKind.DiscardToDrawPileTop or CardSelectionKind.DiscardToHand =>
                state.DiscardPile,
            CardSelectionKind.DrawPileToHand
            or CardSelectionKind.ExhaustFromDrawPile
            or CardSelectionKind.TransformDrawPileToSoul
            or CardSelectionKind.TransformDrawToMinionDiveBomb => state.DrawPile,
            _ => state.Hand,
        };
        return pile[index];
    }
}
