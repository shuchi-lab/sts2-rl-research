namespace Sts2Emulator.Core;

using Effects;

public static class EnemyAI
{
    public static void ChooseIntents(
        List<EnemyState> enemies,
        int turn,
        Random rng,
        Random? aiRng = null,
        int ascension = Ascension.DefaultLevel
    )
    {
        var effectiveAiRng = aiRng ?? rng;
        foreach (var enemy in enemies.Where(e => e.Hp > 0))
        {
            // A killed illusion announces the revive it is about to spend the turn on, and
            // does not roll a move for a turn it will not act in.
            if (BuffSystem.Get(enemy.Buffs, BuffId.Reviving) > 0)
            {
                continue;
            }

            if (enemy.ResumePreparedMoveAfterStun)
            {
                // STUNNED.FollowUpStateId is StateLog.Last().Id. This direct edge
                // does not roll a RandomBranchState, but RollMove logs the move again.
                enemy.ResumePreparedMoveAfterStun = false;
                if (enemy.RepeatStreak > 0)
                    enemy.RepeatStreak++;
                if (enemy.LastMoveRepeats > 0)
                    enemy.LastMoveRepeats++;
                if (enemy.MoveHistory.Count > 0)
                    enemy.MoveHistory.Add(enemy.MoveHistory[^1]);
                continue;
            }

            enemy.CurrentIntent = SelectIntent(enemy, effectiveAiRng, ascension, enemies);
            enemy.SecondaryIntent = SecondaryIntentFor(enemy);
        }
    }

    public static void UpdateSecondaryIntents(IEnumerable<EnemyState> enemies)
    {
        foreach (var enemy in enemies)
        {
            enemy.SecondaryIntent = SecondaryIntentFor(enemy);
        }
    }

    /// <summary>CreatureCmd.Stun with its default follow-up, as used by Whistle.</summary>
    public static void StunAndRepeatPreparedMove(EnemyState enemy)
    {
        if (enemy.Hp <= 0)
            return;
        // SetMoveImmediate replaces an existing STUNNED move; it does not stack turns.
        BuffSystem.Remove(enemy.Buffs, BuffId.Stunned);
        BuffSystem.Apply(enemy.Buffs, BuffId.Stunned, 1);
        enemy.StunReturnsToPreparedMove = true;
    }

    /// <summary>
    /// One of the intents declared by the game's move, in display order. The
    /// execution dispatcher still uses its existing attack/buff/debuff categories;
    /// a heal, summon or sleep must retain its distinct meaning in observations.
    /// </summary>
    public static Intent? DeclaredIntent(EnemyState enemy, int index)
    {
        if (enemy.DefId == KE.WaterfallGiant && enemy.SteamEruptionPhase > 0)
            return index != 0 ? null
                : enemy.SteamEruptionPhase == 1 ? new Intent(IntentType.Stun, 0)
                : new Intent(IntentType.DeathBlow, enemy.SteamEruptionDamage);
        if (BuffSystem.Get(enemy.Buffs, BuffId.Stunned) > 0)
            return index == 0 ? new Intent(IntentType.Stun, 0) : null;
        // BattleFriendV1/V2/V3's NOTHING_MOVE declares an empty intent array. The
        // execution dispatcher uses Unknown for that no-op, but it is not a displayed
        // Unknown intent. An explicit stun still overrides the empty move above.
        if (enemy.DefId is KE.BattleFriendV1 or KE.BattleFriendV2 or KE.BattleFriendV3)
            return null;
        if (index == 0)
        {
            var type = enemy.DefId switch
            {
                KE.Chomper
                or KE.LeafSlimeS
                or KE.LeafSlimeM
                or KE.EyeWithTeeth
                or KE.TwigSlimeM when enemy.CurrentIntent.Type == IntentType.Debuff =>
                    IntentType.StatusCard,
                KE.ShrinkerBeetle when enemy.CurrentIntent.Type == IntentType.Debuff =>
                    IntentType.DebuffStrong,
                // Puppet Strings declares a card debuff; retain Debuff3 for execution.
                KE.Queen when enemy.LastMove == 0 => IntentType.CardDebuff,
                KE.BygoneEffigy when enemy.MoveIndex == 0 => IntentType.Sleep,
                // RECHARGE_MOVE declares Sleep; its executor remains a no-action Unknown.
                KE.Rocket when enemy.MoveIndex % 5 == 4 => IntentType.Sleep,
                KE.BowlbugRock when enemy.CurrentIntent.Type == IntentType.Unknown =>
                    IntentType.Stun,
                KE.Myte or KE.Aeonglass when enemy.CurrentIntent.Type == IntentType.Debuff =>
                    IntentType.StatusCard,
                KE.ThievingHopper when enemy.MoveIndex >= 4 => IntentType.Escape,
                KE.FatGremlin => enemy.MoveIndex == 0 ? IntentType.Stun : IntentType.Escape,
                KE.LagavulinMatriarch when BuffSystem.Get(enemy.Buffs, BuffId.Asleep) > 0 =>
                    IntentType.Sleep,
                KE.SlumberingBeetle when BuffSystem.Get(enemy.Buffs, BuffId.Slumber) > 0 =>
                    IntentType.Sleep,
                KE.PhrogParasite when enemy.MoveIndex % 2 == 0 => IntentType.StatusCard,
                KE.TwoTailedRat when enemy.LastMove == 3 => IntentType.Summon,
                // LayEggs uses the Buff dispatcher internally; its source declaration
                // is Summon. NutritionalPaste (positive magnitude) remains Buff.
                KE.Ovicopter
                    when enemy.CurrentIntent.Type == IntentType.Buff
                        && enemy.CurrentIntent.Magnitude == 0 => IntentType.Summon,
                KE.ToughEgg when BuffSystem.Get(enemy.Buffs, BuffId.Hatch) > 0 => IntentType.Summon,
                KE.GasBomb => IntentType.DeathBlow,
                KE.SlimedBerserker when enemy.MoveIndex % 4 == 0 => IntentType.StatusCard,
                KE.SoulFysh when enemy.MoveIndex % 5 == 0 => IntentType.StatusCard,
                KE.TheObscura when enemy.MoveIndex == 0 => IntentType.Summon,
                KE.Fogmog when enemy.LastMove == 0 => IntentType.Summon,
                KE.Fabricator when enemy.CurrentIntent.Type == IntentType.Buff => IntentType.Summon,
                KE.TestSubject when enemy.CurrentIntent.Type == IntentType.Debuff =>
                    IntentType.StatusCard,
                // Noisebot.NOISE_MOVE declares StatusIntent(2); Debuff2 remains
                // the execution dispatcher that creates its two Dazed cards.
                KE.Noisebot => IntentType.StatusCard,
                KE.WaterfallGiant when enemy.MoveIndex > 0 && (enemy.MoveIndex - 1) % 5 == 2 =>
                    IntentType.Heal,
                _ => enemy.CurrentIntent.Type,
            };
            return enemy.CurrentIntent with { Type = type };
        }
        if (index == 1)
        {
            return enemy.DefId switch
            {
                // Burn Bright already grants Block20 in ApplyBuffIntent. This row is
                // display-only and disappears immediately when AfterDeath selects Enrage.
                KE.Queen when enemy.LastMove == 2 => new Intent(IntentType.Defend, 0),
                KE.TheLost when enemy.MoveIndex % 2 == 0 => new Intent(IntentType.Buff, 0),
                KE.TheForgotten when enemy.MoveIndex % 2 == 0 => new Intent(IntentType.Defend, 0),
                KE.BowlbugEgg => new Intent(IntentType.Defend, 0),
                KE.Myte when enemy.MoveIndex % 3 == 2 => new Intent(IntentType.Buff, 0),
                // Both RepeaterBlast moves declare Attack + Buff; Expel is attack only.
                KE.CubexConstruct when enemy.MoveIndex > 0 && (enemy.MoveIndex - 1) % 3 is 0 or 1 =>
                    new Intent(IntentType.Buff, 0),
                KE.Axebot when enemy.MoveIndex == 0 => new Intent(IntentType.Buff, 0),
                KE.Axebot when enemy.MoveIndex % 2 == 1 => new Intent(IntentType.Debuff, 0),
                KE.SoulNexus when enemy.LastMove == 2 => new Intent(IntentType.DebuffStrong, 0),
                KE.KnowledgeDemon when KnowledgeDemonPhase(enemy.MoveIndex) == 3 => new Intent(
                    IntentType.Heal,
                    0
                ),
                KE.Aeonglass when enemy.MoveIndex % 3 == 0 => new Intent(IntentType.Defend, 0),
                KE.Aeonglass when enemy.MoveIndex % 3 == 2 => new Intent(IntentType.Buff, 0),
                KE.Nibbit
                    when enemy.CurrentIntent.Type == IntentType.Attack
                        && enemy.MoveIndex % 3 == 1 => new Intent(IntentType.Defend, 0),
                // THWACK declares Attack + Defend (ThwackMove gains 5 block after its
                // hit); LASH is attack only. Told apart by damage as the executor does:
                // ThwackDamage 7/8 is below LashDamage 12/13 at every ascension.
                KE.SlitheringStrangler
                    when enemy.CurrentIntent.Type == IntentType.Attack
                        && enemy.CurrentIntent.Magnitude <= 8 => new Intent(IntentType.Defend, 0),
                // Both SWING states declare Attack + Defend (SwingBlock); BIG_SWING,
                // MoveIndex % 3 == 2 exactly as SelectIntent picks it, is attack only.
                KE.AxeRubyRaider when enemy.MoveIndex % 3 != 2 => new Intent(IntentType.Defend, 0),
                KE.SnappingJaxfruit => new Intent(IntentType.Buff, 0),
                KE.Flyconid when enemy.LastMove == 1 => new Intent(IntentType.Debuff, 0),
                KE.ThievingHopper when enemy.MoveIndex == 0 => new Intent(IntentType.CardDebuff, 0),
                // GRASPING_VINES declares CardDebuff for Tangled, not generic Debuff.
                KE.VineShambler when enemy.MoveIndex % 3 == 1 => new Intent(
                    IntentType.CardDebuff,
                    0
                ),
                KE.FrogKnight when enemy.LastMove == 0 => new Intent(IntentType.Debuff, 0),
                // TACKLE_MOVE declares SingleAttackIntent then DebuffIntent (its Frail).
                KE.FossilStalker
                    when enemy.CurrentIntent.Type == IntentType.Attack && enemy.LastMove == 1 =>
                    new Intent(IntentType.Debuff, 0),
                KE.Fogmog when enemy.LastMove is 1 or 2 => new Intent(IntentType.Buff, 0),
                // FABRICATING_STRIKE declares Attack + Summon. DISINTEGRATE's base
                // damage is 11/13, so it stays attack-only at every ascension.
                KE.Fabricator
                    when enemy.CurrentIntent.Type == IntentType.Attack
                        && enemy.CurrentIntent.Magnitude is 18 or 21 => new Intent(
                    IntentType.Summon,
                    0
                ),
                KE.Vantom when enemy.MoveIndex % 4 == 2 => new Intent(IntentType.StatusCard, 0),
                // v0.111.0 LivingFog: ADVANCED_GAS declares CardDebuff;
                // BLOAT declares Summon after its attack.
                KE.LivingFog when enemy.MoveIndex == 0 => new Intent(IntentType.CardDebuff, 0),
                KE.LivingFog when enemy.MoveIndex % 2 == 1 => new Intent(IntentType.Summon, 0),
                KE.GlobeHead when enemy.MoveIndex % 3 == 0 => new Intent(IntentType.Debuff, 0),
                KE.GlobeHead when enemy.MoveIndex % 3 == 2 => new Intent(IntentType.Buff, 0),
                KE.InfestedPrism when enemy.MoveIndex % 4 == 1 => new Intent(IntentType.Defend, 0),
                KE.InfestedPrism when enemy.MoveIndex % 4 == 3 => new Intent(IntentType.Buff, 0),
                KE.MagiKnight when enemy.MoveIndex == 0 => new Intent(IntentType.Defend, 0),
                KE.SlimedBerserker when enemy.MoveIndex % 4 == 2 => new Intent(IntentType.Buff, 0),
                KE.SoulFysh when enemy.MoveIndex % 5 == 2 => new Intent(IntentType.StatusCard, 0),
                KE.SoulFysh when enemy.MoveIndex % 5 == 4 => new Intent(IntentType.Debuff, 0),
                KE.TheInsatiable when enemy.MoveIndex == 0 => new Intent(IntentType.StatusCard, 0),
                KE.Ovicopter when enemy.MoveIndex % 3 == 2 => new Intent(IntentType.Debuff, 0),
                // HARDENING_STRIKE declares Attack + Defend. PickBranch records the
                // Obscura's selection in LastBranch; LastMove is never set for it.
                KE.TheObscura when enemy.MoveIndex > 0 && enemy.LastBranch == 2 => new Intent(
                    IntentType.Defend,
                    0
                ),
                KE.TestSubject when enemy.CurrentIntent.Type == IntentType.Debuff => new Intent(
                    IntentType.Buff,
                    0
                ),
                KE.TestSubject
                    when BuffSystem.Get(enemy.Buffs, BuffId.Adaptable) > 0
                        && BuffSystem.Get(enemy.Buffs, BuffId.PainfulStabs) == 0
                        && enemy.MoveIndex % 2 == 1 => new Intent(IntentType.Debuff, 0),
                KE.DecimillipedeSegment when enemy.MoveIndex % 3 == 1 => new Intent(
                    IntentType.Debuff,
                    0
                ),
                KE.DecimillipedeSegment when enemy.MoveIndex % 3 == 2 => new Intent(
                    IntentType.Buff,
                    0
                ),
                KE.LouseProgenitor when enemy.MoveIndex % 3 == 0 => new Intent(
                    IntentType.Debuff,
                    0
                ),
                KE.LouseProgenitor when enemy.MoveIndex % 3 == 1 => new Intent(IntentType.Buff, 0),
                KE.CeremonialBeast
                    when enemy.CurrentIntent.Type == IntentType.Attack
                        && (
                            enemy.MoveIndex < BeastCryLoopBase
                            || (enemy.MoveIndex - BeastCryLoopBase) % 3 == 2
                        ) => new Intent(IntentType.Buff, 0),
                KE.Wriggler when enemy.MoveIndex % 2 == 1 => new Intent(IntentType.StatusCard, 0),
                KE.TerrorEel
                    when enemy.CurrentIntent.Type == IntentType.Attack
                        && enemy.MoveIndex % 2 == 1 => new Intent(IntentType.Buff, 0),
                KE.SlumberingBeetle when enemy.CurrentIntent.Type == IntentType.Attack =>
                    new Intent(IntentType.Buff, 0),
                KE.Tunneler when enemy.CurrentIntent.Type == IntentType.Buff => new Intent(
                    IntentType.Defend,
                    0
                ),
                KE.HauntedShip when enemy.MoveIndex == 0 => new Intent(IntentType.StatusCard, 0),
                KE.WaterfallGiant when enemy.MoveIndex > 0 => new Intent(
                    (enemy.MoveIndex - 1) % 5 == 0 ? IntentType.Debuff : IntentType.Buff,
                    0
                ),
                KE.LagavulinMatriarch
                    when BuffSystem.Get(enemy.Buffs, BuffId.Asleep) == 0
                        && enemy.MoveIndex % 4 == 3 => new Intent(IntentType.Defend, 0),
                KE.LagavulinMatriarch
                    when BuffSystem.Get(enemy.Buffs, BuffId.Asleep) == 0
                        && enemy.MoveIndex % 4 == 0 => new Intent(IntentType.Buff, 0),
                _ => enemy.SecondaryIntent,
            };
        }
        if (index != 2)
            return null;
        return enemy.DefId switch
        {
            KE.TheForgotten when enemy.MoveIndex % 2 == 0 => new Intent(IntentType.Buff, 0),
            KE.InfestedPrism when enemy.MoveIndex % 4 == 3 => new Intent(IntentType.Defend, 0),
            KE.WaterfallGiant when enemy.MoveIndex > 0 && (enemy.MoveIndex - 1) % 5 == 0 =>
                new Intent(IntentType.Buff, 0),
            KE.KnowledgeDemon when KnowledgeDemonPhase(enemy.MoveIndex) == 3 => new Intent(
                IntentType.Buff,
                0
            ),
            _ => null,
        };
    }

    public static void ExecuteIntent(EnemyState enemy, CombatState state, Random rng)
    {
        // Enemy data is ascension-dependent; read the run's level once here so every
        // damage and buff below picks the same branch the live game would.
        int ascension = state.AscensionLevel;
        bool wasBuffMove = enemy.CurrentIntent.Type == IntentType.Buff;
        // VigorPower is spent by the attack that benefits from it. Captured here because a
        // move can GRANT Vigor as it swings — Terror Eel's THRASH does — and the grant must
        // survive to the next turn while the amount it swung with does not.
        int vigorSpentThisTurn =
            enemy.CurrentIntent.Type == IntentType.Attack
                ? BuffSystem.Get(enemy.Buffs, BuffId.Vigor)
                : 0;

        // Block is cleared for the whole side in `CombatEngine` before any creature acts,
        // not here: doing it per-creature let a later actor wipe block an earlier one had
        // GIVEN it -- the Guardbot's 15 on its Fabricator.
        if (BuffSystem.Get(enemy.Buffs, BuffId.Stunned) > 0)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Stunned, -1);
            WakeSlumberingBeetleIfDone(enemy);
            if (enemy.StunReturnsToPreparedMove)
            {
                enemy.StunReturnsToPreparedMove = false;
                enemy.ResumePreparedMoveAfterStun = true;
            }
            else if (enemy.DefId != KE.CorpseSlug)
            {
                enemy.MoveIndex++;
            }

            RestoreTemporaryEnemyStrength(enemy);
            return;
        }

        switch (enemy.CurrentIntent.Type)
        {
            case IntentType.Stun
                when enemy.DefId == KE.WaterfallGiant && enemy.SteamEruptionPhase == 1:
                enemy.SteamEruptionDamage = BuffSystem.Get(enemy.Buffs, BuffId.SteamEruption);
                BuffSystem.Remove(enemy.Buffs, BuffId.SteamEruption);
                enemy.SteamEruptionPhase = 2;
                break;
            case IntentType.DeathBlow when enemy.DefId == KE.WaterfallGiant:
                DealAttackDamage(enemy, state, enemy.SteamEruptionDamage, rng);
                enemy.Hp = 0;
                break;
            case IntentType.Attack:
            {
                if (enemy.DefId == KE.Toadpole && enemy.MoveIndex % 3 == 1)
                {
                    // SpikeSpitMove spends the Spiken thorns before it swings; the hits
                    // themselves come from the intent's declared Hits below.
                    BuffSystem.Apply(enemy.Buffs, BuffId.Thorns, -2);
                }

                // `CrushMove` attacks and THEN takes CrushStrength. STOMP is the other
                // attack in the same loop and takes nothing, so they are told apart by
                // where in the loop they sit rather than by damage.
                if (
                    enemy.DefId == KE.CeremonialBeast
                    && enemy.MoveIndex >= BeastCryLoopBase
                    && (enemy.MoveIndex - BeastCryLoopBase) % 3 == 2
                )
                {
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    BuffSystem.Apply(
                        enemy.Buffs,
                        BuffId.Strength,
                        Ascension.Value(state.AscensionLevel, Ascension.DeadlyEnemies, 4, 3)
                    );
                    break;
                }

                // `AxeRubyRaider.SwingMove` attacks and THEN gains SwingBlock -- both SWING
                // states carry a `DefendIntent` beside their attack. BIG_SWING carries none,
                // and the two are told apart by damage the way the Strangler's are: Swing is
                // the smaller number.
                if (
                    enemy.DefId == KE.AxeRubyRaider
                    && enemy.CurrentIntent.Magnitude
                        == Ascension.Value(state.AscensionLevel, Ascension.DeadlyEnemies, 6, 5)
                )
                {
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    enemy.Block += BuffSystem.IncomingBlock(
                        Ascension.Value(state.AscensionLevel, Ascension.DeadlyEnemies, 6, 5),
                        enemy.Buffs
                    );
                    break;
                }

                if (
                    enemy.DefId == KE.SlitheringStrangler
                    && enemy.CurrentIntent.Magnitude
                        == Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                )
                {
                    // ThwackMove attacks and THEN gains 5 block -- the move carries a
                    // DefendIntent beside its attack, which the intent table already said
                    // in a comment and nothing ever acted on. Lash is the same enemy's
                    // other attack and gains nothing, so the damage is what tells them
                    // apart at execution time.
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    enemy.Block += BuffSystem.IncomingBlock(5, enemy.Buffs);
                    break;
                }

                // `HardeningStrikeMove` attacks and THEN gains block -- the Obscura's
                // other attack, PIERCING_GAZE, gains nothing. Told apart by `LastBranch`
                // rather than by damage: the Obscura's own WAIL gives Strength 3 to its
                // teammates and `GetTeammatesOf` includes ITSELF, so its attack magnitude
                // does not stay put and the Bowlbug's damage comparison would drift off it.
                //
                // Safe to read here because execution runs before the next `ChooseIntents`
                // -- both live in `EndTurn`, execute first.
                if (enemy.DefId == KE.TheObscura && enemy.LastBranch == 2)
                {
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    enemy.Block += BuffSystem.IncomingBlock(
                        Ascension.Value(state.AscensionLevel, Ascension.DeadlyEnemies, 7, 6),
                        enemy.Buffs
                    );
                    break;
                }

                if (enemy.DefId == KE.SnappingJaxfruit)
                {
                    // ENERGY_ORB is an attack plus StrengthPower(2), and it loops on
                    // itself — so the jaxfruit's announcement climbs 3, 5, 7. The buff sat
                    // in the debuff branch, which its attack intent no longer reaches, so
                    // it never grew at all.
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                    break;
                }

                if (enemy.DefId == KE.LivingFog && enemy.MoveIndex % 2 == 1)
                {
                    // BLOAT: an attack plus a SummonIntent. GetNextSlot decides whether a
                    // bomb fits — LivingFogNormal declares five bomb slots.
                    if (state.Enemies.Count(e => e.Hp > 0 && e.DefId == KE.GasBomb) < 5)
                    {
                        // Not stunned: LivingFog.BloatMove adds the bomb with a plain
                        // CreatureCmd.Add, where a monster that means to sit out the
                        // turn it arrives sets StartStunned (Wriggler does). The bomb
                        // goes off in the enemy phase that summoned it.
                        var bloatBomb = CreateEnemy(
                            KE.GasBomb,
                            rng,
                            new Intent(
                                IntentType.Attack,
                                Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                            ),
                            state: state
                        );
                        BuffSystem.Apply(bloatBomb.Buffs, BuffId.Minion, 1);
                        // The bomb takes a slot BEFORE the fog, which is where a live
                        // capture shows it: [Gas Bomb, Living Fog]. Appending it put the
                        // fog first, so the same target index named a different creature
                        // on each side -- a replayed run aimed its strikes at the fog
                        // where the game killed the bomb, then ate the eight the bomb was
                        // never alive to deal. Fogmog's eye is inserted the same way.
                        state.Enemies.Insert(
                            state.Enemies.IndexOf(enemy),
                            Effects.RelicEffects.Spawned(state, bloatBomb)
                        );
                    }

                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    break;
                }

                if (enemy.DefId == KE.TerrorEel && enemy.MoveIndex % 2 == 1)
                {
                    // THRASH_MOVE: three hits, then VigorPower(6) — not Strength, which is
                    // what the emulator granted. Vigor is spent by its next attack, so the
                    // Crash after a Thrash announces six higher and the one after that
                    // does not.
                    DealAttack(
                        enemy,
                        state,
                        enemy.CurrentIntent.Magnitude,
                        enemy.CurrentIntent.Hits,
                        rng
                    );
                    BuffSystem.Apply(enemy.Buffs, BuffId.Vigor, 6);
                    break;
                }

                if (enemy.DefId == KE.SkulkingColony && enemy.MoveIndex % 4 == 2)
                {
                    // INERTIA_MOVE: the attack the intent announces, plus the Strength its
                    // BuffIntent stands for.
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    BuffSystem.Apply(
                        enemy.Buffs,
                        BuffId.Strength,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 2)
                    );
                    break;
                }

                if (enemy.DefId == KE.CubexConstruct && (enemy.MoveIndex - 1) % 3 is 0 or 1)
                {
                    // REPEATER_BLAST: the attack the intent announces, plus the
                    // StrengthPower(2) its BuffIntent stands for.
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                    break;
                }

                if (enemy.DefId == KE.Fogmog && enemy.LastMove is 1 or 2)
                {
                    // SWIPE: attack plus StrengthPower(1) to itself.
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 1);
                    break;
                }

                if (enemy.DefId == KE.GremlinMerc)
                {
                    // Every Merc move steals through ThieveryPower; DOUBLE_SMASH adds
                    // WeakPower(2) and HEHE adds StrengthPower(2) to itself. The hits
                    // themselves come from the intent's Hits.
                    DealAttack(
                        enemy,
                        state,
                        enemy.CurrentIntent.Magnitude,
                        enemy.CurrentIntent.Hits,
                        rng
                    );
                    StealGremlinMercGold(enemy, state);
                    if (enemy.MoveIndex % 3 == 1)
                    {
                        BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 2);
                    }
                    else if (enemy.MoveIndex % 3 == 2)
                    {
                        BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                    }

                    break;
                }

                if (enemy.DefId == KE.LivingFog && enemy.MoveIndex == 0)
                {
                    // ADVANCED_GAS: the attack, plus the SmoggyPower(1) its
                    // CardDebuffIntent stands for -- which the debuff branch never
                    // applied at all, it only dealt the damage.
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Smoggy, 1);
                    break;
                }

                if (enemy.DefId == KE.SludgeSpinner && enemy.LastMove == 0)
                {
                    // OIL_SPRAY: attack plus the WeakPower(1) its DebuffIntent stands
                    // for. It used to be announced as a debuff and resolve in the debuff
                    // branch, which its attack intent no longer reaches.
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 1);
                    break;
                }

                if (enemy.DefId == KE.SludgeSpinner && enemy.LastMove == 2)
                {
                    // RAGE: attack plus the BuffIntent beside it. The live readout calls
                    // this an Attack, so it executes here rather than in the buff branch —
                    // and the Strength has to come with it, or every later Slam and Oil
                    // Spray announces low.
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 3);
                    break;
                }

                if (enemy.DefId == KE.VineShambler && enemy.MoveIndex % 3 == 1)
                {
                    // GRASPING_VINES: the attack the intent announces, plus the card
                    // debuff its second intent stands for.
                    DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Tangled, 1);
                    break;
                }

                // Two hand-rolled Test Subject multi-hits used to sit here, each with its
                // damage and hit count written out beside a `break` that skipped every
                // rider below. Both are ordinary `Hits:` intents now, so the generic path
                // deals them and PainfulStabs counts what lands.

                // A Fossil Stalker special case used to sit here, firing on whichever
                // turn MoveIndex happened to be 2 and dealing a two-hit Lash at its A9
                // damage regardless of the move the machine had chosen — which also
                // doubled the Strength its SuckPower grants, since Suck triggers per hit.
                // The intent's own Hits carries this now.

                // The Flail Knight used to have its Strength SUBTRACTED here, because its
                // intent table carried damage with the Strength already in it -- a
                // compensation for one bug that became a bug of its own the moment the
                // table was corrected to real base values. It announced 21 and dealt 15.
                int baseDamage = enemy.CurrentIntent.Magnitude;

                // Hits, not one: the riders below belong to the attack as a whole, and a
                // multi-hit intent used to break out above them -- which is how Punch
                // Construct's FAST_PUNCH lost the Frail its DebuffIntent declares.
                // Thievery removes the card before dealing its attack damage.
                if (enemy.DefId == KE.ThievingHopper && enemy.MoveIndex == 0)
                    StealDrawOrDiscardCard(state, rng, enemy);
                DealAttack(enemy, state, baseDamage, Math.Max(1, enemy.CurrentIntent.Hits), rng);

                if (
                    enemy.DefId == KE.MechaKnight
                    && enemy.MoveIndex > 0
                    && (enemy.MoveIndex - 1) % 3 == 0
                )
                {
                    // FlamethrowerMove awaits its attack before adding four Burns.
                    AddMechaKnightBurns(state);
                }

                if (enemy.DefId == KE.GasBomb)
                {
                    enemy.Hp = 0;
                }

                // WEB_CANNON_MOVE's Frail, and only its: POUNCE_MOVE is
                // `DamageCmd.Attack` and nothing else. This is the LIVE site -- a second
                // `case KE.LouseProgenitor` sat in `ApplyDebuffIntent`, which the louse
                // never reaches because none of its three intents is a Debuff. It has
                // been removed rather than corrected.
                if (enemy.DefId == KE.LouseProgenitor && enemy.MoveIndex % 3 == 0)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, LouseWebFrail);
                }

                // TENDERIZER_MOVE's rider, which used to ride on the Debuff branch
                // because the intent was typed Debuff. Retyping it Attack -- which is
                // what the move declares FIRST -- moves the damage into DealAttack and
                // would have dropped the Vulnerable on the floor.
                // `% 3`, matching the intent table above it. The Ovicopter's machine is a
                // THREE-cycle -- LAY_EGGS-or-PASTE, SMASH, TENDERIZER -- and the table was
                // corrected from `% 4` to `% 3` when that was found. This rider kept the
                // old modulus, so the Vulnerable landed on turns TENDERIZER did not run
                // and skipped turns it did.
                //
                // Nothing local said the two were connected: the table and the rider are
                // three hundred lines apart and each reads correctly on its own. Two
                // places deriving a move from the same index have to be corrected together.
                if (enemy.DefId == KE.Ovicopter && enemy.MoveIndex % 3 == 2)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Vulnerable, 2);
                }

                // Only FABRICATING_STRIKE, which is `SingleAttackIntent` beside a
                // `SummonIntent`. The Fabricator's OTHER attack is DISINTEGRATE, which
                // carries nothing -- and this summoned on both, so every disintegrate put
                // a bot on the board the game does not. That also feeds itself: the extra
                // bot keeps `CanFabricate` false, so the emulator disintegrates again and
                // spawns again while the game fabricates once and stops.
                //
                // Told apart by damage, the same way the Obscura's HARDENING_STRIKE is told
                // from its PIERCING_GAZE: 18 against 11 at A8, 21 against 13 at A9.
                if (
                    enemy.DefId == KE.Fabricator
                    && enemy.CurrentIntent.Magnitude
                        == Ascension.Value(ascension, Ascension.DeadlyEnemies, 21, 18)
                )
                {
                    SummonFabricatorBots(enemy, state, rng, includeDefensive: false);
                }

                if (enemy.DefId == KE.PunchConstruct && enemy.MoveIndex % 3 == 1)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 1);
                }

                // TACKLE_MOVE is an attack plus a DebuffIntent -- FrailPower(1) on the
                // target -- and the readout follows the attack, so it resolves here. It
                // sat in the debuff branch, which the stalker's Attack intent never
                // reaches, so the Frail was never applied at all and every Defend after
                // it blocked five where the game blocked three. LastMove, not MoveIndex:
                // this monster rolls its move rather than cycling.
                if (enemy.DefId == KE.FossilStalker && enemy.LastMove == 1)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 1);
                }

                // FRAIL_SPORES_MOVE is SingleAttackIntent(SporeDamage) then DebuffIntent,
                // so the readout calls it an Attack and it resolves here. Its
                // PowerCmd.Apply<FrailPower>(2) sat in the debuff branch, which the move's
                // Attack intent never reaches -- the damage landed and the Frail did not,
                // so every Defend after it blocked five where the game blocked three.
                if (enemy.DefId == KE.Flyconid && enemy.LastMove == 1)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 2);
                }

                // Cycle positions, matching SelectIntent: 1 is CONSTRICT and 2 is BULK.
                // These read as swapped against the old code because the CYCLE was what
                // was wrong, not the riders.
                if (enemy.DefId == KE.DecimillipedeSegment && enemy.MoveIndex % 3 == 2)
                {
                    // BulkStrength, a flat 2 with no ascension term.
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                }

                if (enemy.DefId == KE.DecimillipedeSegment && enemy.MoveIndex % 3 == 1)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 1);
                }

                // PONDER's HealIntent and BuffIntent, which ride its attack. The heal
                // is `30 * Players.Count`, so 30 in singleplayer; PonderStrength is a
                // Deadly pair, and it COMPOUNDS -- every later slap and every hit of
                // KNOWLEDGE OVERWHELMING reads higher for each ponder it has done, which
                // is why a flat 3 showed up as a growing error rather than a fixed one.
                if (enemy.DefId == KE.KnowledgeDemon && KnowledgeDemonPhase(enemy.MoveIndex) == 3)
                {
                    enemy.Hp = Math.Min(enemy.MaxHp, enemy.Hp + 30);
                    BuffSystem.Apply(
                        enemy.Buffs,
                        BuffId.Strength,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2)
                    );
                }

                // THROW_RELIC_MOVE's DebuffIntent is Frail 1, which the merchant's
                // attack branch never applied -- the intent said Attack and the rider
                // for it lived only in the buff handler, so a throw was bare damage.
                if (enemy.DefId == KE.FakeMerchant && enemy.LastBranch == 2)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 1);
                }

                // POWER_SHIELD_MOVE's DefendIntent, worth PowerShieldBlock -- and the
                // shield is the knight's move ZERO and nothing else, since MAGIC_BOMB
                // returns to RAM rather than to the opening. The `% 5` handed it out every
                // fifth turn, at the ToughEnemies value used at every level.
                if (enemy.DefId == KE.MagiKnight && enemy.MoveIndex == 0)
                {
                    enemy.Block += BuffSystem.IncomingBlock(
                        Ascension.Value(ascension, Ascension.ToughEnemies, 9, 5),
                        enemy.Buffs
                    );
                }

                // Two of the prism's four moves carry a DefendIntent, for different
                // amounts: RADIATE gains RadiateBlock and PULSATE gains PulsateBlock,
                // which is the TOUGH pair and so 22 at A8.
                //
                // This lived in ApplyBuffIntent, as a flat 22 after "every attack" -- and
                // ALL FOUR of the prism's moves are attacks, so that case never ran and
                // the prism gained no block at all. A rider on an attacking creature
                // belongs here, in the attack branch.
                if (enemy.DefId == KE.InfestedPrism && enemy.MoveIndex % 4 == 1)
                {
                    int radiate = Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 11);
                    enemy.Block += BuffSystem.IncomingBlock(radiate, enemy.Buffs);
                }

                if (enemy.DefId == KE.InfestedPrism && enemy.MoveIndex % 4 == 3)
                {
                    int pulsate = Ascension.Value(ascension, Ascension.ToughEnemies, 22, 20);
                    enemy.Block += BuffSystem.IncomingBlock(pulsate, enemy.Buffs);
                    // PULSATE stacks another VitalSparkAmount on top of the one the prism
                    // arrived with, and VitalSparkPower is a Counter -- so the tax on
                    // playing a Skill climbs as the fight goes on.
                    BuffSystem.Apply(
                        enemy.Buffs,
                        BuffId.VitalSpark,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2)
                    );
                }

                // `PlowMove` takes `PlowStrength`, which is 2 -- the BuffIntent it carries
                // beside its attack. Scoped to the PLOW loop: the Beast's second loop has
                // its own attacks, and STOMP takes nothing while CRUSH takes CrushStrength,
                // so an unscoped `MoveIndex > 0` handed both of them a Plow's worth.
                if (
                    enemy.DefId == KE.CeremonialBeast
                    && enemy.MoveIndex > 0
                    && enemy.MoveIndex < BeastCryLoopBase
                )
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                }

                if (enemy.DefId == KE.Crusher && enemy.MoveIndex % 5 == 2)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 2);
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 2);
                }
                if (enemy.DefId == KE.Crusher && enemy.MoveIndex % 5 == 4)
                {
                    enemy.Block += BuffSystem.IncomingBlock(18, enemy.Buffs);
                }

                if (enemy.DefId == KE.KinPriest && enemy.MoveIndex % 4 == 0)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 1);
                }

                if (enemy.DefId == KE.KinPriest && enemy.MoveIndex % 4 == 1)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 1);
                }

                // SLASH2_MOVE's DefendIntent, worth Slash2Block. ToughEnemies is live at
                // A8, so the 14 was right there and wrong at every level below it.
                if (enemy.DefId == KE.LagavulinMatriarch && enemy.MoveIndex % 4 == 3)
                {
                    enemy.Block += BuffSystem.IncomingBlock(
                        Ascension.Value(ascension, Ascension.ToughEnemies, 14, 12),
                        enemy.Buffs
                    );
                }

                if (enemy.DefId == KE.SoulFysh && enemy.MoveIndex % 5 == 2)
                {
                    AddStatus(state, ST.Beckon, 1);
                }

                if (enemy.DefId == KE.SoulFysh && enemy.MoveIndex % 5 == 4)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Vulnerable, 3);
                }

                if (enemy.DefId == KE.Vantom && enemy.MoveIndex % 4 == 2)
                {
                    AddStatus(state, ST.Wound, 3);
                }

                if (enemy.DefId == KE.WaterfallGiant && enemy.MoveIndex > 0)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.SteamEruption, 3);
                }

                if (enemy.DefId == KE.WaterfallGiant && (enemy.MoveIndex - 1) % 5 == 0)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 1);
                }

                if (
                    enemy.DefId == KE.TestSubject
                    && BuffSystem.Get(enemy.Buffs, BuffId.Adaptable) > 0
                    && BuffSystem.Get(enemy.Buffs, BuffId.PainfulStabs) == 0
                    && enemy.MoveIndex % 2 == 1
                )
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Vulnerable, 1);
                }

                // HAMMER_UPPERCUT_MOVE swings and then applies Weak 2 and Frail 2. The
                // Axebot had no attack rider at all, so both were simply missing -- odd
                // move indices are the uppercut, even ones the ONE_TWO.
                if (enemy.DefId == KE.Axebot && enemy.MoveIndex % 2 == 1)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 2);
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 2);
                }

                // ShockingSlap applies FrailPower(2); GalvanicBurstMove takes
                // StrengthPower(2). The Strength used to sit in ApplyBuffIntent, and every
                // one of the Globe Head's three moves is an attack -- so it never ran, and
                // when it had run it would have fired on all three.
                if (enemy.DefId == KE.GlobeHead && enemy.MoveIndex % 3 == 0)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 2);
                }

                if (enemy.DefId == KE.GlobeHead && enemy.MoveIndex % 3 == 2)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                }

                // VerdictMove applies VulnerablePower(4) as it lands, and takes the owl
                // back out of the air: `PowerCmd.Remove<SoarPower>` is the last line of
                // the move.
                if (enemy.DefId == KE.OwlMagistrate && enemy.MoveIndex % 4 == 3)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Vulnerable, 4);
                    BuffSystem.Remove(enemy.Buffs, BuffId.Soar);
                }

                // StabMove applies FrailPower(1) after it swings. It lived in the debuff
                // branch, which the bot's Attack intent no longer reaches.
                if (enemy.DefId == KE.Stabbot)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 1);
                }

                // SUCK_MOVE declares SingleAttackIntent then BuffIntent: the attack, then
                // StrengthPower(SuckStrength) on itself. Nothing applied it -- the Myte's
                // "suck" is a plain per-move Strength, not the per-HIT SuckPower the Fossil
                // Stalker carries, and only the stalker was ever given `BuffId.Suck`. So
                // the Myte never grew at all, and its cycle announces the same three
                // numbers for the whole fight where the game's climbs every third turn.
                if (enemy.DefId == KE.Myte && enemy.MoveIndex % 3 == 2)
                {
                    BuffSystem.Apply(
                        enemy.Buffs,
                        BuffId.Strength,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2)
                    );
                }

                // LivingShield.SMASH_MOVE awaits its attack, then gains EnrageStr(3).
                // Key off the prepared move, so killing its ally does not change the
                // already prepared flat-6 Shield Slam into a Smash mid-turn.
                if (
                    enemy.DefId == KE.LivingShield
                    && enemy.CurrentIntent.Magnitude > 6
                    && enemy.Hp > 0
                )
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 3);
                }

                // TongueLashMove applies FrailPower(2) after it swings. This lived in
                // ApplyDebuffIntent, and the knight has no Debuff intent at all.
                if (enemy.DefId == KE.FrogKnight && enemy.LastMove == 0)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 2);
                }

                // EbbMove attacks and then gains EbbBlock, a flat 33.
                if (enemy.DefId == KE.Aeonglass && enemy.MoveIndex % 3 == 0)
                {
                    enemy.Block += BuffSystem.IncomingBlock(33, enemy.Buffs);
                }

                // DRAIN_LIFE applies VulnerablePower(2) and WeakPower(2) after it
                // lands. It followed its attack into this branch when the intent was
                // retyped to the Attack it declares first; move 2 is the drain.
                if (enemy.DefId == KE.SoulNexus && enemy.LastMove == 2)
                {
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Vulnerable, 2);
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 2);
                }

                // ROLL_OUT_MOVE is `DamageCmd.Attack(RolloutDamage)` and then
                // `StrengthPower(2)` on itself, and it follows up to ITSELF -- so the
                // beetle's damage climbs 16, 18, 20 once it wakes. Only the attack was
                // modelled, so it announced 16 every turn where the game said 18 then 20.
                // The `BuffIntent()` riding beside the attack in its MoveState is what
                // says there is a second half.
                if (enemy.DefId == KE.SlumberingBeetle)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, BeetleRolloutStrength);
                }

                // SPIKE_EXPLOSION_MOVE ends with `ThornsPower(-5)`: the explosion SPENDS
                // the Thorns that PROTRUDING_SPIKES put up. Never taking it away left the
                // toad stacking 5 more every cycle, so a player who kept attacking it took
                // escalating damage the game never dealt.
                if (enemy.DefId == KE.SpinyToad && enemy.MoveIndex % 3 == 1)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Thorns, -ToadSpikeThorns);
                }

                // BarrageMove's StrengthPower, which followed its attack into the attack
                // branch when the intent was retyped.
                if (enemy.MoveIndex % 3 == 2)
                {
                    int barrageStrength = enemy.DefId switch
                    {
                        KE.TheAdversaryMkOne => 2,
                        KE.TheAdversaryMkTwo => 3,
                        KE.TheAdversaryMkThree => 4,
                        _ => 0,
                    };
                    if (barrageStrength > 0)
                    {
                        BuffSystem.Apply(enemy.Buffs, BuffId.Strength, barrageStrength);
                    }
                }

                break;
            }

            case IntentType.Defend:
                if (enemy.DefId == KE.Guardbot)
                {
                    foreach (
                        var ally in state.Enemies.Where(e => e.Hp > 0 && e.DefId == KE.Fabricator)
                    )
                    {
                        ally.Block += BuffSystem.IncomingBlock(15, ally.Buffs);
                    }

                    break;
                }

                enemy.Block += BuffSystem.IncomingBlock(enemy.CurrentIntent.Magnitude, enemy.Buffs);
                // WindupMove gains 15 block and then StrengthPower(5). The block comes
                // from the intent above; the Strength is the BuffIntent riding with it.
                if (enemy.DefId == KE.MechaKnight)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 5);
                }

                // CURL_AND_GROW_MOVE is `GainBlock(CurlBlock)` and then
                // `StrengthPower(5)` -- the same shape as the Mecha Knight's windup, and
                // the half that was missing. Without it the louse never grew: the game
                // announces POUNCE at 19 then 24 on its second and third cycles where the
                // emulator kept saying 14, and every number after the first curl was the
                // base one.
                if (enemy.DefId == KE.LouseProgenitor)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, LouseCurlStrength);
                }

                if (enemy.DefId == KE.Axebot && enemy.MoveIndex == 0)
                {
                    // BootUpStrGain * (2 - StockAmount): nothing on the bot that opens the
                    // fight, one helping on the first respawn and two on the second. Stock
                    // has already been decremented by the respawn that got here.
                    int stock = BuffSystem.Get(enemy.Buffs, BuffId.Stock);
                    int gain = Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3);
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, Math.Max(0, 2 - stock) * gain);
                }

                break;

            case IntentType.Buff:
                ApplyBuffIntent(enemy, state, rng);
                break;

            case IntentType.Debuff:
                ApplyDebuffIntent(enemy, state, rng);
                break;
        }

        // `ESCAPE_MOVE` is `CreatureCmd.Escape` -- the Hopper LEAVES, and in
        // ThievingHopperWeak it is the only creature, so the fight ends there. The
        // emulator ran its four moves and then stood the Hopper in the room announcing
        // Unknown forever, which only ended when the player killed it.
        //
        // Zeroing the HP is how a creature leaves a fight here, and `Escaped` is what says
        // it was not a kill -- the same pair the Fat Gremlin and the Battleworn Dummy use.
        // It matters twice over for this creature: an escaped Hopper keeps the card its
        // `SwipePower` took, which is exactly what the escape is FOR.
        //
        // `EscapeArtistPower` counts down to this and is otherwise inert; the move machine
        // drives it, not the power. The counter reaching one and the Hopper leaving are the
        // same turn by construction -- five moves, a timer that starts at five.
        if (enemy.DefId == KE.ThievingHopper && enemy.MoveIndex >= 4 && !enemy.Escaped)
        {
            enemy.Hp = 0;
            enemy.Escaped = true;
        }

        // `FatGremlin.FLEE_MOVE`: it runs off with whatever it is holding. Zeroing the HP
        // is how the emulator takes a creature out of the fight, so the flag is what tells
        // the reward screen this was an ESCAPE -- the gold does not come back and the fight
        // pays nothing. Beside the Hopper's because they are the same thing, and out of
        // `ApplyBuffIntent` because the flee is not a buff and is no longer announced as one.
        if (enemy.DefId == KE.FatGremlin && enemy.MoveIndex > 0 && !enemy.Escaped)
        {
            enemy.Hp = 0;
            enemy.Escaped = true;
            state.FatGremlinEscaped = true;
        }

        if (
            enemy.DefId == KE.Nibbit
            && enemy.CurrentIntent.Type == IntentType.Attack
            && enemy.MoveIndex % 3 == 1
        )
        {
            // Nibbit.SliceBlock increases from 5 to 6 at ToughEnemies (A8).
            enemy.Block += BuffSystem.IncomingBlock(
                Ascension.Value(ascension, Ascension.ToughEnemies, 6, 5),
                enemy.Buffs
            );
        }

        if (enemy.DefId == KE.BowlbugEgg && enemy.CurrentIntent.Type == IntentType.Attack)
        {
            // `BITE_MOVE` is `SingleAttackIntent(BiteDamage)` beside a `DefendIntent`, and
            // `ProtectBlock` is ascension-gated exactly as the damage is:
            // `GetValueIfAscension(DeadlyEnemies, 8, 7)`. The 8 was hardcoded, so the egg
            // carried the DEADLY block at every ascension below it.
            enemy.Block += BuffSystem.IncomingBlock(
                Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7),
                enemy.Buffs
            );
        }

        if (enemy.DefId == KE.TwoTailedRat && enemy.CurrentIntent.Type != IntentType.Buff)
        {
            TickRatSummonCooldown(enemy);
        }

        // AsleepPower removes Plating in BeforeSideTurnEndVeryEarly, before
        // Plating's BeforeSideTurnEndEarly block grant. Removing it afterward
        // left the Matriarch with one final shield on the turn she wakes.
        if (enemy.DefId == KE.LagavulinMatriarch && BuffSystem.Get(enemy.Buffs, BuffId.Asleep) == 1)
        {
            BuffSystem.Remove(enemy.Buffs, BuffId.Plating);
        }

        // PlatingPower decrements on AfterSideTurnStart and grants its block on
        // BeforeSideTurnEndEarly -- in that order -- so the block a turn ends with is the
        // ALREADY decremented amount. Granting first and decrementing after left every
        // plated enemy a point of block ahead of the game for the whole fight.
        if (BuffSystem.Get(enemy.Buffs, BuffId.Plating) > 0)
        {
            // ...except on round one, which AfterSideTurnStart skips for enemies. So a
            // plated enemy ends its first turn on the full amount and only starts giving
            // ground on its second: a live Sewer Clam holds 9 block at Plating 9, then 8
            // at Plating 8. The guard is `RoundNumber != 1`, not the player's turn number:
            // an extra player turn inside round one leaves the enemy phase after it on
            // round one, so the decay still waits.
            //
            // The Lagavulin Matriarch used to be excluded from this, with no reason
            // recorded and none in the game: `PlatingPower.AfterSideTurnStart` decrements
            // for its owner whatever else is true of it, and `AsleepPower` says nothing
            // about the decay -- only that the Plating comes off on the last sleeping turn
            // and the moment she is struck. The underdocks sweep settled it: at the third
            // turn the live game holds PLATING 11 and block 11 where the emulator held 12
            // and 12, having never decayed at all.
            if (state.RoundNumber > 1)
            {
                BuffSystem.Apply(enemy.Buffs, BuffId.Plating, -1);
            }

            enemy.Block += BuffSystem.IncomingBlock(
                BuffSystem.Get(enemy.Buffs, BuffId.Plating),
                enemy.Buffs
            );
        }

        if (enemy.DefId == KE.LagavulinMatriarch && BuffSystem.Get(enemy.Buffs, BuffId.Asleep) > 0)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Asleep, -1);
            // SLEEP_MOVE loops on itself for as long as AsleepPower lasts, and only then
            // does SLEEP_BRANCH send her to SLASH. Sleeping turns are all one move, so
            // they must not walk the four-move ring — parking the index here leaves the
            // increment below to start that ring at SLASH on the turn she wakes.
            enemy.MoveIndex = 0;
        }

        enemy.MoveIndex++;

        // Ritual: gain Strength at end of each enemy turn except the turn it was applied.
        if (!wasBuffMove)
        {
            int ritual = BuffSystem.Get(enemy.Buffs, BuffId.Ritual);
            if (ritual > 0)
            {
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, ritual);
            }
        }

        if (vigorSpentThisTurn > 0)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Vigor, -vigorSpentThisTurn);
        }

        RestoreTemporaryEnemyStrength(enemy);
    }

    /// <summary>
    /// `HighVoltagePower.AfterSideTurnEnd` and `TerritorialPower`'s equivalent, for every
    /// PARTICIPANT of the enemy side turn.
    /// </summary>
    /// <remarks>
    /// Participants, not actors. This used to run inside `ExecuteIntent`, so only a
    /// creature that took a turn got its Strength -- and a bot the Fabricator spawned
    /// DURING the enemy turn had not acted, so it waited a turn the game does not make it
    /// wait. Glory's sweep showed the Zapbot's Strength one application behind on every
    /// turn after it arrived.
    /// </remarks>
    internal static void ApplyEndOfEnemySideTurn(CombatState state)
    {
        // `DiamondDiademPower.AfterSideTurnEnd` removes itself when the ENEMY side's turn
        // ends. It is granted before the player's turn ends, so it covers the one enemy
        // turn in between and nothing after it.
        BuffSystem.Remove(state.PlayerBuffs, BuffId.DiamondDiadem);

        foreach (var enemy in state.Enemies.Where(enemy => enemy.Hp > 0))
        {
            ApplyHighVoltageEndOfEnemyTurn(enemy);
            ApplyDemiseEndOfEnemyTurn(state, enemy);
        }
    }

    /// <summary>
    /// `DemisePower.AfterSideTurnEnd` (the Powdered Demise potion, 9 on one enemy): the
    /// creature takes its amount as `Unblockable | Unpowered` damage when its own side turn
    /// ends.
    /// </summary>
    /// <remarks>
    /// UNBLOCKABLE, so it goes past the enemy's block rather than through it -- and the
    /// power neither decrements nor removes itself, so it pays every enemy turn for the
    /// rest of the fight. Both are worth stating because the neighbouring damage-over-time
    /// powers do the opposite on each count.
    /// </remarks>
    private static void ApplyDemiseEndOfEnemyTurn(CombatState state, EnemyState enemy)
    {
        int demise = BuffSystem.Get(enemy.Buffs, BuffId.Demise);
        if (demise > 0)
        {
            Effects.CardEffects.DealUnblockableUnpoweredDamageToEnemy(state, enemy, demise);
        }
    }

    private static void ApplyHighVoltageEndOfEnemyTurn(EnemyState enemy)
    {
        int highVoltage = BuffSystem.Get(enemy.Buffs, BuffId.HighVoltage);
        if (highVoltage > 0)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Strength, highVoltage);
        }

        int territorial = BuffSystem.Get(enemy.Buffs, BuffId.Territorial);
        if (territorial > 0)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Strength, territorial);
        }
    }

    /// <summary>
    /// <c>NemesisPower.AfterSideTurnEnd</c> flips a private bool each time its owner takes
    /// part in a side turn, applying Intangible on the flip up and removing it on the flip
    /// down — so the Test Subject's third form spends every other player turn untouchable.
    /// </summary>
    /// <remarks>
    /// Runs AFTER the duration tick, not with the rest of the end-of-enemy-turn riders,
    /// and the ordering is the whole mechanic. Intangible decrements itself at this same
    /// moment; if Nemesis applied first, the tick would take the stack straight back off
    /// and the power would never be observable at all. The game gets the same ordering
    /// from snapshotting its hook listeners before the pass — a power applied DURING the
    /// pass does not also fire during it.
    /// </remarks>
    public static void ToggleNemesisIntangible(CombatState state)
    {
        foreach (var enemy in state.Enemies)
        {
            if (enemy.Hp <= 0 || BuffSystem.Get(enemy.Buffs, BuffId.Nemesis) <= 0)
            {
                continue;
            }

            enemy.NemesisIntangibleOn = !enemy.NemesisIntangibleOn;
            if (enemy.NemesisIntangibleOn)
            {
                BuffSystem.Apply(enemy.Buffs, BuffId.Intangible, 1);
            }
            else
            {
                // A no-op in practice: Intangible's own tick, which runs just before this,
                // has already taken the stack off. The game's flip-down does the same
                // nothing for the same reason, and it is written out here because the
                // alternation is a property of the bool and not of the stack.
                BuffSystem.Remove(enemy.Buffs, BuffId.Intangible);
            }
        }
    }

    private static void RestoreTemporaryEnemyStrength(EnemyState enemy)
    {
        int temporaryStrength = BuffSystem.Get(enemy.Buffs, BuffId.TemporaryStrength);
        if (temporaryStrength == 0)
        {
            return;
        }

        BuffSystem.Apply(enemy.Buffs, BuffId.Strength, temporaryStrength);
        BuffSystem.Remove(enemy.Buffs, BuffId.TemporaryStrength);
    }

    // ── Per-enemy intent selection ─────────────────────────────────────────────

    /// <summary>
    /// SKITTER, MANDIBLES or ENRAGE — a slot-picked opener and a two-way branch after it.
    /// </summary>
    /// <remarks>
    /// A flat <c>rng.Next(3)</c> was wrong in four ways at once. The OPENER is not random
    /// at all: <c>INIT_MOVE</c> is a ConditionalBranchState on SlotName — first skitters,
    /// second bites, third enrages, and only a fourth (which the normal encounter has and
    /// the weak one does not) rolls. MANDIBLES then goes STRAIGHT to ENRAGE, with no roll.
    /// The RAND branch it eventually reaches offers only SKITTER and MANDIBLES, never
    /// ENRAGE. And both of its branches are <c>CannotRepeat</c>, so the move just
    /// performed has weight zero — after a skitter the roll can only come out mandibles.
    ///
    /// The draw still happens. <c>RandomBranchState.GetNextState</c> calls
    /// <c>rng.NextFloat(max)</c> unconditionally and only THEN walks the weights, so a
    /// forced branch costs a value off the AI stream exactly like a free one — the same
    /// rule that made a one-item ancient pool cost its draw (E65).
    /// </remarks>
    private static Intent ExoskeletonIntent(
        EnemyState enemy,
        Random rng,
        int ascension,
        IReadOnlyList<EnemyState>? roster
    )
    {
        var skitter = new Intent(
            IntentType.Attack,
            1,
            Hits: Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3)
        );
        var mandibles = new Intent(
            IntentType.Attack,
            Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
        );
        var enrage = new Intent(IntentType.Buff, 0);

        // The RAND branch: one draw, and the move just performed cannot come up.
        Intent Branch(bool lastWasSkitter)
        {
            bool takeSkitter = rng.Next(2) == 0;
            return lastWasSkitter || !takeSkitter ? mandibles : skitter;
        }

        if (enemy.MoveIndex == 0)
        {
            // Slot order IS roster order, and the roster is the encounter's own list.
            int slot = roster is null ? 0 : SlotAmongKind(enemy, roster);
            return slot switch
            {
                0 => skitter,
                1 => mandibles,
                2 => enrage,
                _ => Branch(lastWasSkitter: false),
            };
        }

        // MANDIBLES_MOVE.FollowUpState is ENRAGE_MOVE outright -- no branch, no draw.
        if (enemy.CurrentIntent.Type == IntentType.Attack && enemy.CurrentIntent.Hits == 1)
        {
            return enrage;
        }

        bool cameFromSkitter =
            enemy.CurrentIntent.Type == IntentType.Attack && enemy.CurrentIntent.Hits > 1;
        return Branch(cameFromSkitter);
    }

    /// <summary>
    /// One draw over the branches the game would still consider, excluding whichever the
    /// creature just performed.
    /// </summary>
    /// <remarks>
    /// <c>RandomBranchState.GetNextState</c> sums the eligible weights, rolls
    /// <c>NextFloat(total)</c> and walks the list in DECLARATION order — so with equal
    /// weights it is a uniform pick over the survivors, in the order the branches were
    /// added. The roll happens BEFORE the weights are read, so a choice narrowed to one
    /// branch still costs a value off the AI stream (E65's rule).
    /// </remarks>
    private static Intent PickBranch(
        EnemyState enemy,
        Random rng,
        Intent[] branches,
        int[]? maxRepeats = null
    )
    {
        // Per BRANCH, because they differ: the Flail Knight's WAR_CHANT is CannotRepeat
        // (a cap of one) while its FLAIL and RAM may each run twice. One shared cap gets
        // whichever branch it was not written for wrong.
        var eligible = Enumerable
            .Range(0, branches.Length)
            .Where(index =>
                index != enemy.LastBranch
                || enemy.RepeatStreak < (maxRepeats is null ? 1 : maxRepeats[index])
            )
            .ToArray();
        // Everything blocked: the game spends a draw and takes the FIRST declared branch.
        // Falling back to "all of them" spent the same draw but answered uniformly, which
        // is a different move whenever more than one branch exists.
        if (eligible.Length == 0)
        {
            eligible = [0];
        }

        // One draw whatever the choice narrowed to: GetNextState rolls NextFloat(total)
        // before it walks the weights, so a forced branch costs a value all the same.
        int chosen = eligible[rng.Next(eligible.Length)];
        enemy.RepeatStreak = chosen == enemy.LastBranch ? enemy.RepeatStreak + 1 : 1;
        enemy.LastBranch = chosen;
        return branches[chosen];
    }

    /// <summary>This creature's position among the enemies sharing its def id.</summary>
    private static int SlotAmongKind(EnemyState enemy, IReadOnlyList<EnemyState> roster)
    {
        int slot = 0;
        foreach (var other in roster)
        {
            if (ReferenceEquals(other, enemy))
            {
                return slot;
            }

            if (other.DefId == enemy.DefId)
            {
                slot++;
            }
        }

        return slot;
    }

    private static Intent SelectIntent(
        EnemyState enemy,
        Random rng,
        int ascension = Ascension.DefaultLevel,
        // The roster, for the handful of monsters whose eligibility depends on what
        // their neighbours have already announced this pass. Rolls happen in roster
        // order, so a monster reads the moves picked before its own.
        List<EnemyState>? roster = null
    )
    {
        switch (enemy.DefId)
        {
            case KE.CalcifiedCultist:
                // Turn 0: Incantation (Buff). Turn 1+: DarkStrikeDamage, which loops.
                return enemy.MoveIndex == 0
                    ? new Intent(IntentType.Buff, 0)
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 9)
                    );

            case KE.DampCultist:
                // Turn 0: Incantation (Buff). Turn 1+: DarkStrikeDamage, which loops.
                // This was a flat 3 — the Deadly branch — so it hit triple at A8.
                return enemy.MoveIndex == 0
                    ? new Intent(IntentType.Buff, 0)
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 1)
                    );

            case KE.Chomper:
                // CLAMP and SCREECH alternate forever. CLAMP is
                // MultiAttackIntent(ClampDamage, 2) -- the 18 was 9x2 folded, at the A9
                // damage besides.
                return enemy.MoveIndex % 2 == 0
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8),
                        Hits: 2
                    )
                    : new Intent(IntentType.Debuff, 3);

            case KE.Exoskeleton:
                return ExoskeletonIntent(enemy, rng, ascension, roster);

            case KE.FuzzyWurmCrawler:
                return (enemy.MoveIndex % 3) == 1
                    ? new Intent(IntentType.Buff, 0)
                    // AcidGoopDamage
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 4)
                    );

            case KE.Mawler:
            {
                // A RandomBranchState, not a cycle: RIP_AND_TEAR and CLAW are CannotRepeat
                // and ROAR is UseOnlyOnce, all weight 1, entered at CLAW. Modelling it as
                // MoveIndex % 3 put the emulator a whole move out of phase from turn two.
                const int ripAndTear = 0;
                const int roar = 1;
                const int claw = 2;
                int move;
                if (enemy.MoveIndex == 0)
                {
                    move = claw;
                }
                else
                {
                    var eligible = new List<int>();
                    foreach (int candidate in (int[])[ripAndTear, roar, claw])
                    {
                        bool repeats = candidate == enemy.LastMove;
                        bool roarSpent = candidate == roar && enemy.OnceOnlyMoveUsed;
                        if (!repeats && !roarSpent)
                        {
                            eligible.Add(candidate);
                        }
                    }

                    move = eligible[rng.Next(eligible.Count)];
                }

                enemy.OnceOnlyMoveUsed |= move == roar;
                enemy.LastMove = move;
                return move switch
                {
                    // ClawDamage x 2
                    claw => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4),
                        Hits: 2
                    ),
                    // RoarMove applies VulnerablePower(3).
                    roar => new Intent(IntentType.Debuff, 3),
                    // RipAndTearDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 14)
                    ),
                };
            }

            case KE.GremlinMerc:
                // GIMME_MOVE, DOUBLE_SMASH_MOVE and HEHE_MOVE all lead with an attack
                // intent, and the live readout announces all three as Attacks. Every value
                // keys off ToughEnemies, which is LIVE at A8 — converting them to
                // DeadlyEnemies made this Merc hit for 14 where the game hits for 16.
                return (enemy.MoveIndex % 3) switch
                {
                    // GimmeDamage x GimmeRepeat
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.ToughEnemies, 8, 7),
                        Hits: 2
                    ),
                    // DoubleSmashDamage x DoubleSmashRepeat, plus WeakPower(2)
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.ToughEnemies, 7, 6),
                        Hits: 2
                    ),
                    // HeheDamage, plus StrengthPower(2) to itself
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.ToughEnemies, 9, 8)
                    ),
                };

            case KE.SneakyGremlin:
                // TackleDamage
                return enemy.MoveIndex == 0
                    ? new Intent(IntentType.Unknown, 0)
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 10, 9)
                    );

            case KE.FatGremlin:
                // Execution is handled separately by the escape lifecycle. DeclaredIntent
                // exposes the source's Stun/Escape states instead of this no-action category.
                return new Intent(IntentType.Unknown, 0);

            case KE.Inklet:
            {
                // JAB leads into a branch of {PIERCING_GAZE, WHIRLWIND}; both of those lead
                // back to JAB. So an Inklet alternates JAB with a rolled move — and the
                // MIDDLE one opens on WHIRLWIND rather than JAB, which is what
                // MonsterMoveStateMachine's initialState says and what put this roster's
                // second Inklet on the wrong move from turn one.
                const int jab = 0;
                const int whirlwind = 1;
                const int piercingGaze = 2;
                int move;
                if (enemy.LastMove < 0)
                {
                    move = enemy.MoveIndex == 1 ? whirlwind : jab;
                }
                else if (enemy.LastMove != jab)
                {
                    move = jab;
                }
                else
                {
                    // Both branches are CannotRepeat, which RandomBranchState scores
                    // against the LAST LOGGED MOVE — and this branch is only ever reached
                    // from JAB, so neither is ever excluded and the roll is always over
                    // two. Excluding the move before the jab made it a roll over one on
                    // half the turns, which is a different draw from the same stream.
                    move = PickBranch([piercingGaze, whirlwind], rng);
                }

                enemy.LastMove = move;
                enemy.MoveHistory.Add(move);
                return move switch
                {
                    // JabDamage
                    jab => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3)
                    ),
                    // WhirlwindDamage x 3
                    whirlwind => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2),
                        Hits: 3
                    ),
                    // PiercingGazeDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 10)
                    ),
                };
            }

            case KE.Flyconid:
            {
                // INITIAL rolls {FRAIL_SPORES, SMASH}; every move then rolls RAND, which is
                // {VULNERABLE_SPORES, FRAIL_SPORES, SMASH} with cooldowns of 3, 2 and none.
                // The emulator rolled a flat 6-way weighting that could open on
                // VULNERABLE_SPORES, which the opening branch does not offer at all.
                const int vulnerableSpores = 0;
                const int frailSpores = 1;
                const int smash = 2;
                var eligible = new List<int>();
                if (enemy.LastMove < 0)
                {
                    eligible.Add(frailSpores);
                    eligible.Add(smash);
                }
                else
                {
                    foreach (
                        var (candidate, cooldown) in ((int, int)[])
                            [(vulnerableSpores, 3), (frailSpores, 2), (smash, 0)]
                    )
                    {
                        bool onCooldown = enemy
                            .MoveHistory.AsEnumerable()
                            .Reverse()
                            .Take(cooldown)
                            .Contains(candidate);
                        if (candidate != enemy.LastMove && !onCooldown)
                        {
                            eligible.Add(candidate);
                        }
                    }
                }

                // Weighted over the FULL declared list -- VULNERABLE_SPORES, FRAIL_SPORES,
                // SMASH, which is RAND's AddBranch order -- with a blocked branch at weight
                // zero. That is exactly `GetNextState`, and it is what makes the
                // everything-blocked case come out as the game's: a draw spent and the
                // FIRST branch taken. Filtering to an eligible list and falling back to
                // SMASH got both halves wrong.
                int move = PickWeightedBranch(
                    [
                        (vulnerableSpores, eligible.Contains(vulnerableSpores) ? 1f : 0f),
                        (frailSpores, eligible.Contains(frailSpores) ? 1f : 0f),
                        (smash, eligible.Contains(smash) ? 1f : 0f),
                    ],
                    rng
                );
                enemy.LastMove = move;
                enemy.MoveHistory.Add(move);
                return move switch
                {
                    vulnerableSpores => new Intent(IntentType.Debuff, 2),
                    // SporeDamage; FRAIL_SPORES is attack + debuff, announced as an attack.
                    frailSpores => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    ),
                    // SmashDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 12, 11)
                    ),
                };
            }

            case KE.SnappingJaxfruit:
                // EnergyDamage; ENERGY_ORB loops on itself forever
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3)
                );

            case KE.BowlbugRock:
                // HEADBUTT_MOVE's follow-up is a ConditionalBranchState, not an
                // alternation: it headbutts EVERY turn, and only owes a dizzy turn when
                // its own attack was fully blocked. `% 2` gave a Rock that stunned itself
                // every other turn against a player who never blocked at all.
                if (enemy.OffBalance)
                {
                    enemy.OffBalance = false;
                    return new Intent(IntentType.Unknown, 0);
                }

                // HeadbuttDamage
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 15)
                );

            case KE.BowlbugEgg:
                // BiteDamage
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                );

            case KE.BowlbugNectar:
                // THRASH -> BUFF -> THRASH2, and THRASH2 follows up to ITSELF. Not a
                // cycle: `% 3` sent it back for a second Buff on turn four, which the
                // machine has no edge for.
                //
                // BuffStrengthGain on the one buff turn; ThrashDamage, which carries no
                // ascension term, on every other.
                return enemy.MoveIndex == 1
                    ? new Intent(
                        IntentType.Buff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 15)
                    )
                    : new Intent(IntentType.Attack, 3);

            case KE.BowlbugSilk:
                // The machine's INITIAL state is TOXIC_SPIT, not THRASH -- it is built as
                // `new MonsterMoveStateMachine(list, moveState2)`. THRASH is
                // MultiAttackIntent(ThrashDamage, 2), so the 10 was 5x2 folded.
                return enemy.MoveIndex % 2 == 0
                    ? new Intent(IntentType.Debuff, 1)
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4),
                        Hits: 2
                    );

            case KE.Tunneler:
                // BITE -> BURROW -> BELOW, and BELOW follows up to ITSELF -- so it
                // burrows once and then hits from below forever, unless the player breaks
                // the burrow, which stuns it back to BITE. `% 3` walked it back to the
                // bite every fourth turn, at a third of the damage.
                return enemy.MoveIndex switch
                {
                    // BiteDamage
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 15, 13)
                    ),
                    // BURROW_MOVE declares its BuffIntent FIRST and its DefendIntent
                    // second, so the readout is a bare BUFF with no number -- a live
                    // capture shows (Buff, none) where this announced Defend 37. E12's
                    // rule again. The block it gains is BlockGain, the TOUGH pair, so 37
                    // at A8; it is applied by the rider rather than read off the intent.
                    1 => new Intent(IntentType.Buff, 0),
                    // BelowDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 26, 23)
                    ),
                };

            case KE.ThievingHopper:
                // THIEVERY -> FLUTTER -> HAT_TRICK -> NAB -> ESCAPE, and ESCAPE follows
                // up to ITSELF. `% 5` restarted the whole routine on turn six, so a
                // Hopper that had already left came back to steal again.
                return enemy.MoveIndex switch
                {
                    // TheftDamage
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 19, 17)
                    ),
                    // FLUTTER_MOVE is a bare BuffIntent, so it announces no number --
                    // but it DOES carry a power: `PowerCmd.Apply<FlutterPower>(.., 5m)`.
                    // An earlier pass removed an invented "Slippery 5" from here and went
                    // one step too far, concluding the move had no power at all. It has
                    // one, and it is not the one that was invented; the live game shows
                    // FLUTTER_POWER on the hopper from this turn on.
                    1 => new Intent(IntentType.Buff, 0),
                    // HatTrickDamage
                    2 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 23, 21)
                    ),
                    // NabDamage
                    3 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 14)
                    ),
                    _ => new Intent(IntentType.Unknown, 0),
                };

            case KE.Myte:
                // TOXIC -> BITE -> SUCK, cycling -- but the machine OPENS on a
                // ConditionalBranchState keyed to SlotName, and the second Myte starts on
                // SUCK. Its whole cycle is therefore two ahead of the first's, which a
                // shared `MoveIndex % 3` cannot express; the enemy's MoveIndex is seeded
                // with the offset instead, so this is a plain cycle again.
                return (enemy.MoveIndex % 3) switch
                {
                    0 => new Intent(IntentType.Debuff, 2),
                    // BiteDamage
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 15, 13)
                    ),
                    // SuckDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 4)
                    ),
                };

            case KE.SlumberingBeetle:
                // SNORE -> a ConditionalBranchState on `HasPower<SlumberPower>()`, and
                // ROLL_OUT once it is gone follows up to ITSELF. Counting three turns was
                // right only for a beetle nobody hit: SlumberPower also decrements on
                // every instance of UNBLOCKED damage it takes, so attacking it -- the
                // obvious play against something asleep behind Plating -- wakes it early.
                //
                // RolloutDamage
                return BuffSystem.Get(enemy.Buffs, BuffId.Slumber) > 0
                    ? new Intent(IntentType.Unknown, 0)
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 18, 16)
                    );

            case KE.SpinyToad:
                // SPIKES -> EXPLOSION -> LASH, cycling.
                return (enemy.MoveIndex % 3) switch
                {
                    // PROTRUDING_SPIKES_MOVE applies ThornsPower at a flat 5, with no
                    // ascension term -- one of the few numbers beside a Deadly pair that
                    // really is a literal.
                    0 => new Intent(IntentType.Buff, 5),
                    // ExplosionDamage
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 25, 23)
                    ),
                    // LashDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 19, 17)
                    ),
                };

            case KE.Ovicopter:
                // LAY_EGGS -> SMASH -> TENDERIZER -> a ConditionalBranchState that goes
                // back to LAY_EGGS or to NUTRITIONAL_PASTE, and both lead to SMASH. So it
                // is a THREE-cycle whose first slot is one or the other, not a four-cycle
                // that always does both -- `% 4` gave the Ovicopter an extra turn.
                return (enemy.MoveIndex % 3) switch
                {
                    // CanLay is `GetTeammatesOf(this).Count(alive) <= 3`, and
                    // `GetTeammatesOf` is `GetCreaturesOnSide` -- everyone on the side,
                    // THE OVICOPTER INCLUDED. So it lays only while at most TWO eggs are
                    // alive. Excluding itself let it lay one egg-load too many, which is
                    // both the extra pair of eggs the game does not have and the Strength
                    // it never gained: the game takes NUTRITIONAL_PASTE at that branch.
                    0 => roster is not null && roster.Count(other => other.Hp > 0) > 3
                        // NutritionalPasteStrengthAmount
                        ? new Intent(
                            IntentType.Buff,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3)
                        )
                        : new Intent(IntentType.Buff, 0),
                    // SmashDamage
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 17, 16)
                    ),
                    // TENDERIZER_MOVE declares SingleAttackIntent(TenderizerDamage) and
                    // THEN a DebuffIntent, and the readout follows the declaration -- so
                    // it is an Attack whose number is damage, not a Debuff. E12 again.
                    2 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                    ),
                };

            case KE.LouseProgenitor:
                // WEB_CANNON -> CURL_AND_GROW -> POUNCE, a fixed ring with no branch.
                return (enemy.MoveIndex % 3) switch
                {
                    // WebDamage
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 10, 9)
                    ),
                    1 => new Intent(
                        IntentType.Defend,
                        Ascension.Value(ascension, Ascension.ToughEnemies, 18, 14)
                    ),
                    // PounceDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 14)
                    ),
                };

            case KE.HunterKiller:
            {
                // TENDERIZING_GOOP once, then a RandomBranchState over BITE and PUNCTURE
                // that both moves return to. Equal weights, so it is a coin flip between
                // the eligible ones -- `rng.Next(3) == 0` made the bite a one-in-three.
                if (enemy.MoveIndex == 0)
                {
                    return new Intent(IntentType.Debuff, 1);
                }

                // BiteDamage, and PUNCTURE_MOVE's MultiAttackIntent(PunctureDamage, 3).
                // The 24 this once announced was the three hits folded into one number,
                // which matches only while the creature has no Strength.
                var bite = new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 19, 17)
                );
                var puncture = new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7),
                    Hits: 3
                );

                // BITE is CannotRepeat and PUNCTURE is CanRepeatXTimes(2). The caps
                // differ per branch; two branches with the LOWER cap would be wrong for
                // the puncture, so the two are handled as a pair here rather than by one
                // shared maxRepeats.
                bool punctureIsSpent = enemy.LastBranch == 1 && enemy.RepeatStreak >= 2;
                Intent[] branches = [bite, puncture];
                int chosen;
                if (enemy.LastBranch == 0)
                {
                    rng.Next(1);
                    chosen = 1;
                }
                else if (punctureIsSpent)
                {
                    rng.Next(1);
                    chosen = 0;
                }
                else
                {
                    chosen = rng.Next(2);
                }

                enemy.RepeatStreak = chosen == enemy.LastBranch ? enemy.RepeatStreak + 1 : 1;
                enemy.LastBranch = chosen;
                return branches[chosen];
            }

            case KE.Axebot:
                // BOOT_UP -> HAMMER_UPPERCUT, and from there HAMMER_UPPERCUT <-> ONE_TWO
                // forever. The machine's INITIAL state is HAMMER_UPPERCUT unless the bot
                // was built with a stock override, which only a respawn does -- so BOOT_UP
                // is index 0 and an Axebot that opens the fight never sees it. The old
                // `% 3` walked back to BOOT_UP every third turn and put ONE_TWO before the
                // uppercut besides.
                return enemy.MoveIndex switch
                {
                    // BootUpBlock. The Strength it also grants rides in the Defend branch
                    // of ExecuteIntent, since BOOT_UP announces as a Defend.
                    0 => new Intent(
                        IntentType.Defend,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 15, 10)
                    ),
                    // ONE_TWO_MOVE: MultiAttackIntent(OneTwoDamage, 2). The 20 was the two
                    // hits folded, at the A9 damage besides.
                    _ when enemy.MoveIndex % 2 == 0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 10),
                        Hits: 2
                    ),
                    // HAMMER_UPPERCUT_MOVE: HammerUppercutDamage, plus Weak 2 and Frail 2,
                    // which are applied in the attack branch.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 18, 14)
                    ),
                };

            case KE.DevotedSculptor:
                // FORBIDDEN_INCANTATION once (RitualPower at a flat 9), then SAVAGE,
                // which follows up to itself.
                return enemy.MoveIndex == 0
                    ? new Intent(IntentType.Buff, 9)
                    // SavageDamage
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 15, 12)
                    );

            case KE.Fabricator:
            {
                // A ConditionalBranchState on CanFabricate -- fewer than four living
                // teammates -- and only then a roll between FABRICATE and
                // FABRICATING_STRIKE. With the bench full it DISINTEGRATES instead, a move
                // the emulator never reached: it rolled the pair unconditionally, so a
                // Fabricator with four bots up kept summoning into a full board.
                // `CanFabricate` is `GetTeammatesOf(this).Count(alive) < 4`, and
                // `GetTeammatesOf` is `GetCreaturesOnSide` -- everyone on the side, THE
                // FABRICATOR INCLUDED. Excluding itself let it fabricate with three bots
                // up, where the game disintegrates; and because the fabricate branch ROLLS
                // and disintegrate does not, that also spent an AI draw the game never
                // made, so every later branch in the fight read the wrong number. Exactly
                // E430's off-by-one on the Ovicopter, in the same shape, one act over.
                bool canFabricate = (roster?.Count(other => other.Hp > 0) ?? 0) < 4;
                if (!canFabricate)
                {
                    // DisintegrateDamage
                    return new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 11)
                    );
                }

                return PickBranch([0, 1], rng) == 0
                    ? new Intent(IntentType.Buff, 0)
                    // FabricatingStrikeDamage; the move summons as it swings, which is why
                    // it announces as an attack rather than as the summon.
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 21, 18)
                    );
            }

            case KE.FrogKnight:
            {
                // TONGUE_LASH -> STRIKE_DOWN_EVIL -> FOR_THE_QUEEN -> a
                // ConditionalBranchState that sends it back to the lash unless the knight
                // has dropped below half HP without having charged yet, in which case
                // BEETLE_CHARGE -> TONGUE_LASH.
                //
                // The emulator ran a flat `% 3` with STRIKE_DOWN_EVIL and FOR_THE_QUEEN
                // the wrong way round -- so every Strength the knight took landed a turn
                // early and its 23 came before its 14 rather than after. BEETLE_CHARGE,
                // its biggest move by a distance, was unreachable.
                const int tongueLash = 0;
                const int strikeDownEvil = 1;
                const int forTheQueen = 2;
                const int beetleCharge = 3;
                int knightMove = enemy.LastMove switch
                {
                    tongueLash => strikeDownEvil,
                    strikeDownEvil => forTheQueen,
                    // HasBeetleCharged is set by the charge and never cleared, so the
                    // branch is a one-off: past half HP it lashes from here forever.
                    forTheQueen => !enemy.OnceOnlyMoveUsed && enemy.Hp < enemy.MaxHp / 2
                        ? beetleCharge
                        : tongueLash,
                    _ => tongueLash,
                };
                enemy.OnceOnlyMoveUsed |= knightMove == beetleCharge;
                enemy.LastMove = knightMove;
                return knightMove switch
                {
                    // TongueLashDamage, plus FrailPower(2) -- which sat in the debuff
                    // branch, and every one of the knight's moves that deals damage
                    // announces as an Attack, so it never ran.
                    tongueLash => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 14, 13)
                    ),
                    // StrikeDownEvilDamage
                    strikeDownEvil => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 23, 21)
                    ),
                    // FOR_THE_QUEEN: StrengthPower(5) on itself.
                    forTheQueen => new Intent(IntentType.Buff, 5),
                    // BeetleChargeDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 40, 35)
                    ),
                };
            }

            case KE.GlobeHead:
                // SHOCKING_SLAP -> THUNDER_STRIKE -> GALVANIC_BURST, cycling. Index 0 had
                // been given THUNDER_STRIKE's folded total as well as index 1, so the slap
                // announced 21 for a move that hits for 14 and never applied its Frail.
                return (enemy.MoveIndex % 3) switch
                {
                    // ShockingSlapDamage, plus FrailPower(2) on the target.
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 14, 13)
                    ),
                    // THUNDER_STRIKE: MultiAttackIntent(ThunderStrikeDamage, 3). The 21 was
                    // the three hits folded, at the A9 damage besides.
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6),
                        Hits: 3
                    ),
                    // GalvanicBurstDamage, plus StrengthPower(2) on itself.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 17, 16)
                    ),
                };

            case KE.LivingShield:
            {
                // SHIELD_SLAM, then a ConditionalBranchState: slam again while it still
                // has an ally, and SMASH -- which follows up to itself -- once it is
                // alone. The emulator slammed once and smashed forever regardless, so a
                // shield guarding a live turret hit three times as hard as it should.
                bool alone =
                    roster is null
                    || !roster.Any(other => !ReferenceEquals(other, enemy) && other.Hp > 0);
                if (enemy.MoveIndex == 0 || !alone)
                {
                    // ShieldSlamDamage, a flat 6 with no ascension term.
                    return new Intent(IntentType.Attack, 6);
                }

                // SmashDamage, with EnrageStr riding it.
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 18, 16)
                );
            }

            case KE.TurretOperator:
                // UNLOAD -> UNLOAD_2 -> RELOAD, cycling. Both unloads are
                // MultiAttackIntent(FireDamage, 5) -- the 20 was 4x5 folded, at A9.
                return (enemy.MoveIndex % 3) == 2
                    // RELOAD_MOVE's Strength.
                    ? new Intent(IntentType.Buff, 1)
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3),
                        Hits: 5
                    );

            case KE.OwlMagistrate:
                // MAGISTRATE_SCRUTINY -> PECK_ASSAULT -> JUDICIAL_FLIGHT -> VERDICT.
                return (enemy.MoveIndex % 4) switch
                {
                    // ScrutinyDamage
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 17, 16)
                    ),
                    // PECK_ASSAULT: MultiAttackIntent(PeckAssaultDamage, 6). The 24 was the
                    // six hits folded -- and six is a lot of per-instance triggers to lose.
                    // PeckAssaultDamage is 4 at both ascension levels.
                    1 => new Intent(IntentType.Attack, 4, Hits: 6),
                    // JUDICIAL_FLIGHT: SoarPower(1) on itself, which halves powered attack
                    // damage against it until VERDICT removes it. Soar itself is not
                    // modelled -- see docs/divergence-catalog.md.
                    2 => new Intent(IntentType.Buff, 1),
                    // VerdictDamage, plus VulnerablePower(4) on the target.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 36, 33)
                    ),
                };

            case KE.ScrollOfBiting:
                return ScrollOfBitingIntent(enemy, rng, ascension);

            case KE.SlimedBerserker:
                // VOMIT_ICHOR -> FURIOUS_PUMMELING -> LEECHING_HUG -> SMOTHER, cycling.
                return (enemy.MoveIndex % 4) switch
                {
                    // VOMIT_ICHOR: StatusIntent(10), ten Slimed into the discard.
                    0 => new Intent(IntentType.Debuff, 10),
                    // FURIOUS_PUMMELING: MultiAttackIntent(PummelingDamage, 4). The 20 was
                    // the four hits folded, at the A9 damage besides.
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4),
                        Hits: 4
                    ),
                    // LEECHING_HUG declares DebuffIntent BEFORE BuffIntent, so the readout
                    // calls it a Debuff -- Weak 3 on the player, Strength 3 on itself. It
                    // was typed Buff, which is what a policy read, and the effect sat in
                    // the buff branch to match.
                    2 => new Intent(IntentType.Debuff, 3),
                    // SmotherDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 33, 30)
                    ),
                };

            case KE.TheLost:
                // DEBILITATING_SMOG <-> EYE_LASERS. The smog declares DebuffIntent before
                // BuffIntent, so Debuff is what it announces, and its number is the
                // Strength it takes from the player and keeps.
                return (enemy.MoveIndex % 2) == 0
                    ? new Intent(IntentType.Debuff, 2)
                    // EYE_LASERS: MultiAttackIntent(EyeLasersDamage, 2). The 10 was the two
                    // hits folded, at the A9 damage besides.
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4),
                        Hits: 2
                    );

            case KE.TheForgotten:
                // MIASMA <-> DREAD. MIASMA declares DebuffIntent first of three, so it
                // announces as the Dexterity it steals.
                //
                // DreadDamage is not a constant: it is
                // `GetValueIfAscension(Deadly, 15, 13) + its own DexterityPower`, and
                // MIASMA hands it two Dexterity every other turn -- so the dread CLIMBS,
                // 15, 17, 19. The flat 15 was right for exactly one turn at A8, by the
                // coincidence of 13 + 2, which is why it read as a plausible A9 literal
                // and why the ascension audit never flagged it: `DreadDamage` is a
                // property with a BODY, not the one-line `=> GetValueIfAscension(...)` the
                // audit's regex looks for.
                return (enemy.MoveIndex % 2) == 0
                    ? new Intent(IntentType.Debuff, 2)
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 15, 13)
                            + BuffSystem.Get(enemy.Buffs, BuffId.Dexterity)
                    );

            case KE.TheObscura:
            {
                // ILLUSION once, then a RandomBranchState over PIERCING_GAZE, WAIL and
                // HARDENING_STRIKE that every move returns to. All three are
                // CannotRepeat, so the move just performed has weight ZERO -- the choice
                // is between the OTHER TWO, and `rng.Next(3)` gave the Obscura a
                // one-in-three chance of a move the game cannot pick.
                if (enemy.MoveIndex == 0)
                {
                    return new Intent(IntentType.Buff, 0);
                }

                // PiercingGazeDamage; WAIL, a bare BuffIntent whose magnitude carries the
                // Strength it grants; HardeningStrikeDamage.
                Intent[] branches =
                [
                    new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 10)
                    ),
                    new Intent(IntentType.Buff, 3),
                    new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6)
                    ),
                ];
                return PickBranch(enemy, rng, branches);
            }

            case KE.Parafright:
                // SlamDamage; SLAM_MOVE follows up to itself and is the whole creature.
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 17, 16)
                );

            case KE.Wriggler:
                return (enemy.MoveIndex % 2) == 0
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6)
                    )
                    : new Intent(IntentType.Buff, 1);

            case KE.FakeMerchant:
                return FakeMerchantIntent(enemy, rng, ascension);

            // MysteriousKnight : FlailKnight overrides only AfterAddedToRoom, so it runs
            // the Flail Knight's move machine.
            case KE.MysteriousKnight:
            case KE.FlailKnight:
            {
                // RAM first, then a RandomBranchState all three moves return to -- not a
                // fixed cycle, which is what `% 3` made it. WAR_CHANT is CannotRepeat;
                // FLAIL and RAM may each run twice.
                Intent[] branches =
                [
                    // WAR_CHANT's Strength.
                    new Intent(IntentType.Buff, 3),
                    // FLAIL_MOVE: MultiAttackIntent(FlailDamage, 2), folded into 20.
                    new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 10, 9),
                        Hits: 2
                    ),
                    // RamDamage. The 23 was not any branch of anything -- a live capture
                    // opens at 21, which is this 15 plus the Mysterious Knight's own
                    // Strength 6.
                    new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 17, 15)
                    ),
                ];

                if (enemy.MoveIndex == 0)
                {
                    enemy.LastBranch = 2;
                    // MonsterMoveStateMachine logs the initial RAM before the first roll.
                    enemy.RepeatStreak = 1;
                    return branches[2];
                }

                return PickBranch(enemy, rng, branches, [1, 2, 2]);
            }

            case KE.BygoneEffigy:
                return enemy.MoveIndex switch
                {
                    0 => new Intent(IntentType.Unknown, 0),
                    1 => new Intent(IntentType.Buff, 10),
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 15, 13)
                    ),
                };

            case KE.Entomancer:
                // Opens on BEES, then SPEAR, then PHEROMONE_SPIT, cycling.
                return (enemy.MoveIndex % 3) switch
                {
                    0 => new Intent(IntentType.Buff, 1),
                    // BEES_MOVE: MultiAttackIntent(BeesDamage, BeesRepeat). BeesDamage is
                    // 3 at BOTH levels and the REPEAT is what ascension moves, 8 at A9
                    // and 7 at A8 -- so the 24 was the A9 hit count folded into the
                    // damage, wrong in the same two ways the Exoskeleton's skitter was.
                    1 => new Intent(
                        IntentType.Attack,
                        3,
                        Hits: Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                    ),
                    // SpearMoveDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 20, 18)
                    ),
                };

            case KE.InfestedPrism:
                // JAB -> RADIATE -> WHIRLWIND -> PULSATE, cycling.
                return (enemy.MoveIndex % 4) switch
                {
                    // JabDamage
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 17, 15)
                    ),
                    // RadiateDamage, and RadiateBlock for itself.
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 11)
                    ),
                    // WHIRLWIND_MOVE: MultiAttackIntent(WhirlwindDamage, 3). The 18 was
                    // 6x3 folded, at the A9 damage.
                    2 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 5),
                        Hits: 3
                    ),
                    // PulsateDamage, plus PulsateBlock and another VitalSpark.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 10, 8)
                    ),
                };

            case KE.PhrogParasite:
                // INFECT_MOVE's StatusIntent(3), then LashDamage x 4.
                return (enemy.MoveIndex % 2) == 0
                    ? new Intent(IntentType.Debuff, 3)
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4),
                        Hits: 4
                    );

            case KE.SoulNexus:
            {
                // SOUL_BURN opens, and all three moves return to one RandomBranchState
                // whose three branches are weight 1 and CannotRepeat — so every turn after
                // the first is a flat roll over the two moves it did not just make. The
                // emulator ran a fixed three-cycle and never touched the AI stream.
                const int soulBurn = 0;
                const int maelstrom = 1;
                const int drainLife = 2;
                int nexusMove;
                if (enemy.LastMove < 0)
                {
                    nexusMove = soulBurn;
                }
                else
                {
                    var eligible = new List<int>();
                    foreach (int candidate in (int[])[soulBurn, maelstrom, drainLife])
                    {
                        if (candidate != enemy.LastMove)
                        {
                            eligible.Add(candidate);
                        }
                    }

                    nexusMove = PickBranch(eligible, rng);
                }

                enemy.LastMove = nexusMove;
                return nexusMove switch
                {
                    // SoulBurnDamage
                    soulBurn => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 31, 29)
                    ),
                    // MAELSTROM: MultiAttackIntent(MaelstromDamage, MaelstromRepeat). The
                    // repeat is 4 at both levels; the 28 was the four hits folded, at the
                    // A9 damage besides.
                    maelstrom => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6),
                        Hits: 4
                    ),
                    // DRAIN_LIFE declares SingleAttackIntent BEFORE its DebuffIntent, so
                    // it announces as an Attack — it had been typed Debuff, which told a
                    // policy a 19-damage turn was a debuff turn. Vulnerable 2 and Weak 2
                    // ride with it in the attack branch.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 19, 18)
                    ),
                };
            }

            case KE.TerrorEel:
                if (BuffSystem.Get(enemy.Buffs, BuffId.Stunned) > 0)
                {
                    return new Intent(IntentType.Unknown, 0);
                }

                // TERROR_MOVE, the turn after the stun: VulnerablePower(99).
                if (BuffSystem.Get(enemy.Buffs, BuffId.TerrorQueued) > 0)
                {
                    return new Intent(IntentType.Debuff, 99);
                }

                // CrashDamage, then THRASH_MOVE: ThrashDamage x ThrashRepeat plus a
                // BuffIntent, which the live readout announces as the attack.
                return (enemy.MoveIndex % 2) == 0
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 18, 16)
                    )
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3),
                        Hits: 3
                    );

            case KE.Byrdonis:
                // SwoopDamage, alternating with PeckDamage x PeckRepeat.
                return (enemy.MoveIndex % 2) == 0
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 19, 17)
                    )
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3),
                        Hits: 3
                    );

            case KE.DecimillipedeSegment:
                // WRITHE -> CONSTRICT -> BULK, and back to WRITHE. Not 0 -> 1 -> 2: the
                // FollowUpStates are `writhe -> constrict -> bulk -> writhe`, where the
                // STARTER index maps 0/1/2 to writhe/bulk/constrict. Walking the starter
                // numbering as though it were the cycle put every segment's second and
                // third moves the wrong way round. MoveIndex is seeded with the CYCLE
                // POSITION now, so this is the cycle itself.
                //
                // Except once. DEAD_MOVE -> REATTACH_MOVE -> a RandomBranchState over all
                // three moves, each CannotRepeat -- so a segment that comes back ROLLS
                // rather than picking the cycle up where it fell, and that roll is a draw
                // on the AI stream the emulator was not making. The branch is reached from
                // nowhere else, which is why this is the only place it appears.
                if (enemy.RollsNextMove)
                {
                    enemy.RollsNextMove = false;
                    // All three branches are CannotRepeat, and that is scored against the
                    // last move LOGGED -- the last one the segment actually performed, not
                    // the one it was announcing when it fell, since that turn never
                    // happened. MoveIndex is the announcement; MoveIndex - 1 is the
                    // performance -- and after a reattach that is REATTACH_MOVE, which is
                    // none of the three. `RandomBranchState.GetStateWeight` zeroes a
                    // CannotRepeat branch only when it EQUALS `StateLog[^1]`, so a segment
                    // coming back rolls over ALL THREE moves, including the one it was
                    // performing when it died.
                    //
                    // This used to exclude the last cycle move, which is the rule for an
                    // ordinary turn and not for this one. The draw count is the same either
                    // way -- one `NextFloat(total)` -- so the streams stayed in step and
                    // only the outcome differed, which is why it survived the RNG-drift
                    // check and showed up as a segment announcing the wrong move.
                    //
                    // MoveIndex IS the cycle position here, so writing the rolled move
                    // into it both announces that move and leaves ExecuteIntent to walk
                    // the ordinary cycle on from it.
                    //
                    // In the branch's OWN order, which is not the cycle's. `AddBranch` is
                    // called WRITHE, BULK, CONSTRICT, and `GetNextState` walks `States` in
                    // that order subtracting weights -- while MoveIndex numbers the CYCLE,
                    // where 1 is CONSTRICT and 2 is BULK. Passing [0, 1, 2] therefore gave
                    // the game's second branch (BULK) the emulator's second move
                    // (CONSTRICT), so the same roll produced a different creature.
                    //
                    // Both sides draw exactly one value, so nothing here shows as stream
                    // drift; only the move does.
                    enemy.MoveIndex = PickBranch([0, 2, 1], rng);
                }

                return (enemy.MoveIndex % 3) switch
                {
                    // WRITHE_MOVE: MultiAttackIntent(WritheDamage, 2). The 12 was 6x2
                    // folded, at the A9 damage as well.
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 5),
                        Hits: 2
                    ),
                    // CONSTRICT_MOVE: ConstrictDamage, and Weak 1 on the player.
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    ),
                    // BULK_MOVE: BulkDamage, and Strength 2 on itself.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6)
                    ),
                };

            case KE.SpectralKnight:
            {
                // HEX -> SOUL_SLASH -> a RandomBranchState that SOUL_SLASH and SOUL_FLAME
                // both return to. AddBranch(soulSlash, 2) is the maxRepeats overload --
                // barred once it has come up twice running -- and AddBranch(soulFlame,
                // CannotRepeat) bars the flame whenever it was the last move. The emulator
                // walked HEX, SOUL_SLASH, then SOUL_FLAME forever: a fixed order where the
                // game rolls, and it never touched the AI stream.
                const int hex = 0;
                const int soulSlash = 1;
                const int soulFlame = 2;
                int knightMove;
                if (enemy.LastMove < 0)
                {
                    knightMove = hex;
                }
                else if (enemy.LastMove == hex)
                {
                    knightMove = soulSlash;
                }
                else
                {
                    var eligible = new List<int>();
                    if (enemy.LastMove != soulSlash || enemy.LastMoveRepeats < 2)
                    {
                        eligible.Add(soulSlash);
                    }

                    if (enemy.LastMove != soulFlame)
                    {
                        eligible.Add(soulFlame);
                    }

                    knightMove = PickBranch(eligible, rng);
                }

                enemy.LastMoveRepeats =
                    knightMove == enemy.LastMove ? enemy.LastMoveRepeats + 1 : 1;
                enemy.LastMove = knightMove;
                return knightMove switch
                {
                    // HexMove: HexPower(2) on the target.
                    hex => new Intent(IntentType.Debuff, 2),
                    // SoulSlashDamage
                    soulSlash => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 17, 15)
                    ),
                    // SOUL_FLAME: MultiAttackIntent(SoulFlameDamage, 3). The 12 was the
                    // three hits folded, at the A9 damage besides.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3),
                        Hits: 3
                    ),
                };
            }

            case KE.MagiKnight:
                // POWER_SHIELD -> DAMPEN -> RAM -> PREP -> MAGIC_BOMB -> RAM -> ...:
                // MAGIC_BOMB follows up to RAM, not to the opening, so the shield and the
                // dampen happen ONCE and the fight is a three-cycle after them. `% 5`
                // brought both back every fifth turn.
                if (enemy.MoveIndex == 0)
                {
                    // PowerShieldDamage, plus PowerShieldBlock for itself.
                    return new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6)
                    );
                }

                if (enemy.MoveIndex == 1)
                {
                    // DAMPEN_MOVE: DampenPower(1) on the player.
                    return new Intent(IntentType.Debuff, 1);
                }

                return ((enemy.MoveIndex - 2) % 3) switch
                {
                    // SpearDamage, which RAM_MOVE deals.
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 10)
                    ),
                    // PREP_MOVE gains PowerShieldBlock, the same amount the shield does.
                    1 => new Intent(
                        IntentType.Defend,
                        Ascension.Value(ascension, Ascension.ToughEnemies, 9, 5)
                    ),
                    // BombDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 40, 35)
                    ),
                };

            case KE.MechaKnight:
                // CHARGE, then FLAMETHROWER -> WINDUP -> HEAVY_CLEAVE forever:
                // HEAVY_CLEAVE follows up to the FLAMETHROWER, not to the opening, so the
                // charge happens ONCE. `% 4` brought it back every fourth turn -- 25
                // damage the fight does not have -- and put the whole cycle out of step.
                if (enemy.MoveIndex == 0)
                {
                    // ChargeDamage
                    return new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 30, 25)
                    );
                }

                return ((enemy.MoveIndex - 1) % 3) switch
                {
                    // FLAMETHROWER declares its single attack before StatusIntent(4).
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 12, 8)
                    ),
                    // WINDUP declares DefendIntent BEFORE BuffIntent, so it announces as a
                    // Defend of _windupBlock and not as the Buff of 15 the emulator said.
                    // Typing it Buff also meant it did NOTHING: there is no MechaKnight
                    // case in ApplyBuffIntent, so the knight gained neither the block nor
                    // the Strength that comes with it.
                    1 => new Intent(IntentType.Defend, 15),
                    // HeavyCleaveDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 40, 35)
                    ),
                };

            case KE.PhantasmalGardener:
                // BITE -> LASH -> FLAIL -> ENLARGE, a fixed ring. Bite, Lash and the flail
                // repeat do NOT scale with ascension; only EnlargeStr does. An automated
                // conversion pass matched Lash's 7 to SkittishAmount and Flail's 3 to
                // EnlargeStr, which made the flail announce 2 where the game announces 3.
                return (enemy.MoveIndex % 4) switch
                {
                    // BiteDamage
                    0 => new Intent(IntentType.Attack, 5),
                    // LashDamage
                    1 => new Intent(IntentType.Attack, 7),
                    // FlailDamage(1) x FlailRepeat
                    2 => new Intent(IntentType.Attack, 1, Hits: 3),
                    // ENLARGE_MOVE's BuffIntent, worth EnlargeStr
                    _ => new Intent(
                        IntentType.Buff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2)
                    ),
                };

            case KE.Aeonglass:
                // EBB -> EYE_LASERS -> INCREASING_INTENSITY, cycling.
                return (enemy.MoveIndex % 3) switch
                {
                    // EbbDamage, plus EbbBlock (a flat 33) -- the block used to sit in the
                    // buff branch, which meant INCREASING_INTENSITY gained it and EBB did
                    // not.
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 26, 22)
                    ),
                    // EYE_LASERS: MultiAttackIntent(EyeLasersDamage, EyeLasersRepeat=2).
                    // The 24 was the two hits folded, at the A9 damage besides.
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 12, 11),
                        Hits: 2
                    ),
                    // INCREASING_INTENSITY declares StatusIntent BEFORE BuffIntent, so the
                    // readout calls it a Debuff and its number is WitherAmount -- the
                    // Withers it puts in the discard, not the Strength it takes.
                    _ => new Intent(
                        IntentType.Debuff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 2, 1)
                    ),
                };

            case KE.CeremonialBeast:
                // Two loops, and the STUN is the door between them. STAMP applies the Plow
                // threshold and then PLOW_MOVE follows ITSELF forever -- nothing in the
                // declared FollowUps ever reaches the second loop. The only way in is
                // `PlowPower` firing, which stuns the Beast onto STUN_MOVE and points it at
                // BEAST_CRY: from there it is BEAST_CRY -> STOMP -> CRUSH -> BEAST_CRY.
                //
                // The second loop was not modelled at all, so a stunned Beast went on
                // plowing. It only shows past turn ten, which is why six-turn sweeps missed
                // it and a twelve-turn one caught it on every seed at once.
                if (enemy.MoveIndex >= BeastCryLoopBase)
                {
                    return ((enemy.MoveIndex - BeastCryLoopBase) % 3) switch
                    {
                        // BEAST_CRY_MOVE: a DebuffIntent, which announces no number.
                        0 => new Intent(IntentType.Debuff, 0),
                        // StompDamage
                        1 => new Intent(
                            IntentType.Attack,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 17, 15)
                        ),
                        // CrushDamage, and CRUSH carries a BuffIntent beside it.
                        _ => new Intent(
                            IntentType.Attack,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 19, 17)
                        ),
                    };
                }

                return enemy.MoveIndex == 0
                    ? new Intent(
                        IntentType.Buff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 160, 150)
                    )
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 20, 18)
                    );

            case KE.Crusher:
                // THRASH -> ENLARGING_STRIKE -> BUG_STING -> ADAPT -> GUARDED_STRIKE.
                //
                // The old numbers had SurroundedPower's 1.5x multiplied INTO them --
                // 14 x 1.5 = 21 -- which is wrong twice: it is the A9 damage, and the
                // multiplier stops the moment the Rocket dies and the player turns to
                // face the survivor. The intent announces the base now and the 1.5x is
                // applied where the damage lands.
                return (enemy.MoveIndex % 5) switch
                {
                    // ThrashDamage
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 14, 12)
                    ),
                    // EnlargingStrikeDamage, which is 4 at BOTH levels.
                    1 => new Intent(IntentType.Attack, 4),
                    // BUG_STING_MOVE: MultiAttackIntent(BugStingDamage, 2), plus Weak and
                    // Frail. The 20 was the two hits folded and multiplied.
                    2 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6),
                        Hits: 2
                    ),
                    // AdaptStrengthGain
                    3 => new Intent(
                        IntentType.Buff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2)
                    ),
                    // GuardedStrikeDamage, and a flat 18 block.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 14, 12)
                    ),
                };

            case KE.Rocket:
                // TARGETING_RETICLE -> PRECISION_BEAM -> CHARGE_UP -> LASER -> RECHARGE.
                // The structure was right and every number was the A9 branch.
                return (enemy.MoveIndex % 5) switch
                {
                    // TargetingReticleDamage
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3)
                    ),
                    // PrecisionBeamDamage
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 20, 18)
                    ),
                    // ChargeUpStrengthGain
                    2 => new Intent(
                        IntentType.Buff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2)
                    ),
                    // LaserDamage
                    3 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 35, 31)
                    ),
                    // RECHARGE_MOVE is a SleepIntent: it spends the turn doing nothing.
                    _ => new Intent(IntentType.Unknown, 0),
                };

            case KE.KnowledgeDemon:
            {
                // CURSE -> SLAP -> KNOWLEDGE_OVERWHELMING -> PONDER, and PONDER's
                // follow-up is a ConditionalBranchState: back to CURSE while it has cast
                // fewer than three, and to SLAP forever after. A bare `MoveIndex switch`
                // with no wrap left it PONDERING every turn from the fourth on.
                //
                // Three curses land on moves 0, 4 and 8, so moves 0-11 are the four-cycle
                // and everything past 11 is the three-cycle SLAP -> OVERWHELMING ->
                // PONDER.
                int phase = KnowledgeDemonPhase(enemy.MoveIndex);
                return phase switch
                {
                    0 => new Intent(IntentType.Debuff, 0),
                    // SlapDamage
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 18, 17)
                    ),
                    // KNOWLEDGE_OVERWHELMING_MOVE:
                    // MultiAttackIntent(KnowledgeOverwhelmingDamage, 3). The 27 was 9x3
                    // folded, at the A9 damage.
                    2 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8),
                        Hits: 3
                    ),
                    // PONDER declares SingleAttackIntent FIRST, then HealIntent and
                    // BuffIntent -- so the readout calls it an ATTACK, for PonderDamage.
                    // Announcing it as a Buff told a policy a turn of damage was a turn
                    // of nothing; a live capture reads (Attack, 11) where this said
                    // (Buff, 11). E12's rule, a third time.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 11)
                    ),
                };
            }

            case KE.LagavulinMatriarch:
                return BuffSystem.Get(enemy.Buffs, BuffId.Asleep) > 0
                    ? new Intent(IntentType.Unknown, 0)
                    : (enemy.MoveIndex % 4) switch
                    {
                        // SlashDamage
                        1 => new Intent(
                            IntentType.Attack,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 21, 19)
                        ),
                        // DISEMBOWEL_MOVE: DisembowelDamage twice, which has to stay two
                        // hits rather than one of 20 — the matriarch gains Strength off
                        // her own SOUL_SIPHON, and Strength lands on every hit.
                        2 => new Intent(
                            IntentType.Attack,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 10, 9),
                            Hits: 2
                        ),
                        // Slash2Damage. Its 14 is the DeadlyEnemies branch and A8 is
                        // below that; the 14 that IS live at A8 is Slash2Block, applied
                        // separately with the DefendIntent.
                        3 => new Intent(
                            IntentType.Attack,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 14, 12)
                        ),
                        // SOUL_SIPHON_MOVE: a DebuffIntent and a BuffIntent.
                        _ => new Intent(IntentType.Debuff, 2),
                    };

            case KE.Queen:
            {
                // PUPPET_STRINGS -> YOU_ARE_MINE -> a ConditionalBranchState on whether
                // the Torch Head Amalgam has died. While it lives: BURN_BRIGHT_FOR_ME,
                // which loops back through the same condition. Once it is dead:
                // OFF_WITH_YOUR_HEAD -> EXECUTION -> ENRAGE, cycling.
                //
                // The emulator ran the first two moves and then burned bright FOREVER, so
                // **the Queen never attacked at all** — three of her six moves were
                // unreachable and the fight had no damage in it after turn two.
                const int puppetStrings = 0;
                const int youAreMine = 1;
                const int burnBright = 2;
                const int offWithYourHead = 3;
                const int execution = 4;
                const int enrage = 5;
                bool amalgamAlive =
                    roster is not null
                    && roster.Any(other => other.DefId == KE.TorchHeadAmalgam && other.Hp > 0);
                int queenMove = enemy.LastMove switch
                {
                    -1 => puppetStrings,
                    puppetStrings => youAreMine,
                    youAreMine or burnBright => amalgamAlive ? burnBright : offWithYourHead,
                    offWithYourHead => execution,
                    execution => enrage,
                    _ => offWithYourHead,
                };
                enemy.LastMove = queenMove;
                return queenMove switch
                {
                    // PUPPET_STRINGS: a CardDebuffIntent, ChainsOfBindingPower(3).
                    puppetStrings => new Intent(IntentType.Debuff, 3),
                    // YOU_ARE_MINE: Frail, Weak and Vulnerable at 99 apiece.
                    youAreMine => new Intent(IntentType.Debuff, 99),
                    // BURN_BRIGHT_FOR_ME declares BuffIntent then DefendIntent: Strength 1
                    // to every teammate, then 20 block for herself.
                    burnBright => new Intent(IntentType.Buff, 20),
                    // OFF_WITH_YOUR_HEAD: MultiAttackIntent(OffWithYourHeadDamage, 5).
                    offWithYourHead => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3),
                        Hits: 5
                    ),
                    // ExecutionDamage
                    execution => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 18, 15)
                    ),
                    // ENRAGE: StrengthPower(2) on herself.
                    _ => new Intent(IntentType.Buff, 2),
                };
            }

            case KE.TorchHeadAmalgam:
                // v0.111.0: STRONG_TACKLE -> TACKLE_2 -> BEAM -> TACKLE_3 ->
                // TACKLE_4 -> BEAM. Only the first tackle uses StrongTackleDamage.
                if (enemy.MoveIndex == 0)
                    return new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 32, 26)
                    );
                if (enemy.MoveIndex == 1)
                    return new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 22, 18)
                    );
                return ((enemy.MoveIndex - 2) % 3) switch
                {
                    0 => new Intent(IntentType.Attack, 8, Hits: 3),
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 14)
                    ),
                };

            case KE.SoulFysh:
                return (enemy.MoveIndex % 5) switch
                {
                    0 => new Intent(IntentType.Debuff, 2),
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 17, 16)
                    ),
                    2 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                    ),
                    3 => new Intent(IntentType.Buff, 2),
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 15, 13)
                    ),
                };

            case KE.TestSubject:
                // Three machines in one, chosen by which powers the respawns have left it
                // holding. Every damage number here was the A9 branch.
                if (BuffSystem.Get(enemy.Buffs, BuffId.PainfulStabs) > 0)
                {
                    // Second form: MULTI_CLAW follows up to ITSELF and nothing else, and
                    // each performance increments ExtraMultiClawCount — so the hit count
                    // climbs 3, 4, 5. The respawn parks MoveIndex at 2, which makes
                    // MoveIndex - 2 that count. The old announcement was
                    // `11 * (3 + max(0, LastMove))`: folded, and off by one besides, since
                    // LastMove started at -1 and the first two claws both read 3.
                    return new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 10),
                        Hits: 3 + Math.Max(0, enemy.MoveIndex - 2)
                    );
                }

                if (BuffSystem.Get(enemy.Buffs, BuffId.Adaptable) == 0)
                {
                    // Third form: PHASE3_LACERATE -> BIG_POUNCE -> BURNING_GROWL, cycling.
                    // The respawn parks MoveIndex at 4.
                    return ((enemy.MoveIndex - 4) % 3) switch
                    {
                        // PHASE3_LACERATE: MultiAttackIntent(Phase3LacerateDamage, 3),
                        // folded into 33.
                        0 => new Intent(
                            IntentType.Attack,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 10),
                            Hits: 3
                        ),
                        // BigPounceDamage, a flat 45 at both levels.
                        1 => new Intent(IntentType.Attack, 45),
                        // BURNING_GROWL declares StatusIntent BEFORE BuffIntent, so it
                        // announces as a Debuff whose number is BurningGrowlBurnCount --
                        // the Burns it adds -- and not as the Buff of 5 the emulator said.
                        _ => new Intent(
                            IntentType.Debuff,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 3)
                        ),
                    };
                }

                // First form: BITE <-> SKULL_BASH, which carries Vulnerable 1.
                return (enemy.MoveIndex % 2) == 0
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 22, 20)
                    )
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 14)
                    );

            case KE.TheInsatiable:
                // LIQUIFY once, then THRASH -> BITE -> SALIVATE -> THRASH_2 -> THRASH,
                // cycling. THRASH_2 is the same move in a second state, which is what
                // gives the cycle its two thrashes. `_ => thrash` for everything past
                // move three left it thrashing forever from the fifth turn on.
                if (enemy.MoveIndex == 0)
                {
                    return new Intent(IntentType.Buff, 0);
                }

                return ((enemy.MoveIndex - 1) % 4) switch
                {
                    // THRASH_MOVE: MultiAttackIntent(ThrashDamage, 2), folded into 18.
                    0 or 3 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8),
                        Hits: 2
                    ),
                    // BiteDamage
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 31, 28)
                    ),
                    // SalivateStrength
                    _ => new Intent(
                        IntentType.Buff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2)
                    ),
                };

            case KE.KinFollower:
                return (enemy.MoveIndex % 3) switch
                {
                    // QuickSlashDamage, which does not scale with ascension
                    0 => new Intent(IntentType.Attack, 5),
                    // BoomerangDamage x 2, pre-multiplied here into a single swing
                    1 => new Intent(IntentType.Attack, 2, Hits: 2),
                    _ => new Intent(
                        IntentType.Buff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2)
                    ),
                };

            case KE.KinPriest:
                return (enemy.MoveIndex % 4) switch
                {
                    // OrbOfFrailtyDamage, then OrbOfWeaknessDamage — both attacks that
                    // carry a DebuffIntent, and both 8 at A8 rather than the 9 pinned here.
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    ),
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    ),
                    // BeamDamage x 3, which does not scale
                    2 => new Intent(IntentType.Attack, 3, Hits: 3),
                    _ => new Intent(
                        IntentType.Buff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2)
                    ),
                };

            case KE.Vantom:
                return (enemy.MoveIndex % 4) switch
                {
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                    ),
                    // InkyLanceDamage x 2, which was pre-multiplied to its A9 total
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6),
                        Hits: 2
                    ),
                    // DismemberDamage; DISMEMBER also carries a StatusIntent(3)
                    2 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 30, 26)
                    ),
                    _ => new Intent(IntentType.Buff, 2),
                };

            case KE.WaterfallGiant:
                if (enemy.SteamEruptionPhase > 0)
                    return enemy.SteamEruptionPhase == 1
                        ? new Intent(IntentType.Stun, 0)
                        : new Intent(IntentType.DeathBlow, enemy.SteamEruptionDamage);
                // PRESSURIZE once, then the five-move ring STOMP, RAM, SIPHON,
                // PRESSURE_GUN, PRESSURE_UP — PRESSURE_UP's FollowUpState is STOMP, so
                // the ring never returns to PRESSURIZE.
                if (enemy.MoveIndex == 0)
                {
                    return new Intent(
                        IntentType.Buff,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 20, 15)
                    );
                }

                return ((enemy.MoveIndex - 1) % 5) switch
                {
                    // StompDamage; STOMP_MOVE is an attack plus a DebuffIntent and a
                    // BuffIntent, and the live readout announces the attack — as it does
                    // for RAM, PRESSURE_GUN and PRESSURE_UP. Only PRESSURIZE and SIPHON
                    // are pure non-attacks.
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 15)
                    ),
                    // RamDamage
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 10)
                    ),
                    // SIPHON_MOVE: HealIntent plus BuffIntent, no attack.
                    2 => new Intent(
                        IntentType.Buff,
                        Ascension.Value(ascension, Ascension.ToughEnemies, 15, 10)
                    ),
                    // PRESSURE_GUN_MOVE announces a lambda, not a constant: every firing
                    // adds PressureGunIncrease to CurrentPressureGunDamage, so the ring
                    // is 5 damage worse each time round.
                    3 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 23, 20)
                            + (PressureGunIncrease * ((enemy.MoveIndex - 4) / 5))
                    ),
                    // PressureUpDamage; PRESSURE_UP_MOVE is an attack plus a BuffIntent.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 14, 13)
                    ),
                };

            case KE.CubexConstruct:
                // CHARGE_UP happens once: EXPEL's FollowUpState is the first
                // REPEATER_BLAST, not the charge. Cycling all four re-charged every
                // fourth turn, where the live game blasts.
                if (enemy.MoveIndex == 0)
                {
                    return new Intent(IntentType.Buff, 0);
                }

                return ((enemy.MoveIndex - 1) % 3) switch
                {
                    // BlastDamage; REPEATER_BLAST is attack + buff, twice over, and the
                    // live readout announces the attack — growing 9, 11, 13, 15 as the
                    // construct stacks the Strength each blast hands it.
                    0 or 1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                    ),
                    // ExpelDamage x 2
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 5),
                        Hits: 2
                    ),
                };

            case KE.VineShambler:
                return (enemy.MoveIndex % 3) switch
                {
                    // SwipeDamage x 2
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6),
                        Hits: 2
                    ),
                    // GraspingVinesDamage. The MoveState lists SingleAttackIntent first
                    // and CardDebuffIntent second, and the live game announces the attack
                    // — a sweep caught this reported as a Debuff.
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    ),
                    // ChompDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 18, 16)
                    ),
                };

            case KE.SlitheringStrangler:
                // CONSTRICT -> a branch of {THWACK, LASH}, and both of those lead straight
                // back to CONSTRICT. So it alternates the debuff with a ROLLED attack;
                // MoveIndex % 3 made it a fixed three-cycle instead. Both branches are
                // CanRepeatForever, so the roll is always over two.
                if (enemy.MoveIndex % 2 == 0)
                {
                    return new Intent(IntentType.Debuff, 3);
                }

                return PickBranch([0, 1], rng) == 0
                    // ThwackDamage; THWACK also carries a DefendIntent.
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                    )
                    // LashDamage
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 12)
                    );

            case KE.HauntedShip:
                // HAUNT.FollowUpState is SWIPE, and SWIPE and STOMP then point at each
                // other — so the opening debuff happens once and never comes round again.
                // This used to cycle all three on MoveIndex % 3, re-haunting every third turn.
                if (enemy.MoveIndex == 0)
                {
                    return new Intent(IntentType.Debuff, 5);
                }

                return enemy.MoveIndex % 2 == 1
                    // SwipeDamage
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 14, 13)
                    )
                    // StompDamage x StompRepeat
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4),
                        Hits: 3
                    );

            case KE.LivingFog:
                // ADVANCED_GAS happens ONCE: its FollowUpState is BLOAT, and BLOAT and
                // SUPER_GAS_BLAST then point at each other forever. Cycling all three on
                // MoveIndex % 3 re-gassed every third turn. Same shape as Haunted Ship.
                if (enemy.MoveIndex == 0)
                {
                    // AdvancedGasDamage. ADVANCED_GAS_MOVE declares SingleAttackIntent
                    // first and CardDebuffIntent second, so the readout announces an
                    // attack -- the same shape as the Sludge Spinner's oil spray.
                    return new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    );
                }

                return enemy.MoveIndex % 2 == 1
                    // BloatDamage; BLOAT is an attack plus a SummonIntent.
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 5)
                    )
                    // SuperGasBlastDamage
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    );

            case KE.Fogmog:
            {
                // ILLUSION -> SWIPE -> a RandomBranchState weighted 0.4 SWIPE_RANDOM /
                // 0.6 HEADBUTT; SWIPE_RANDOM -> HEADBUTT -> SWIPE -> branch again. The
                // emulator ran a flat three-cycle, which re-summoned every third turn.
                const int illusion = 0;
                const int swipe = 1;
                const int swipeRandom = 2;
                const int headbutt = 3;
                int move = enemy.LastMove switch
                {
                    -1 => illusion,
                    illusion => swipe,
                    swipeRandom => headbutt,
                    headbutt => swipe,
                    // After SWIPE the machine rolls: 0.4 for another swipe, 0.6 headbutt.
                    _ => rng.NextDouble() <= 0.4 ? swipeRandom : headbutt,
                };
                enemy.LastMove = move;
                return move switch
                {
                    illusion => new Intent(IntentType.Buff, 0),
                    // HeadbuttDamage
                    headbutt => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 14)
                    ),
                    // SwipeDamage. SWIPE attacks and hands itself StrengthPower(1), and
                    // the live readout announces the attack.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    ),
                };
            }

            case KE.EyeWithTeeth:
                return new Intent(IntentType.Debuff, 3);

            case KE.GasBomb:
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                );

            case KE.AxeRubyRaider:
                // BigSwingDamage / SwingDamage
                return (enemy.MoveIndex % 3) == 2
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 12)
                    )
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 5)
                    );

            case KE.AssassinRubyRaider:
                // KillshotDamage; KILLSHOT_MOVE loops on itself forever.
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 10)
                );

            case KE.BruteRubyRaider:
                // BeatDamage, alternating with ROAR_MOVE's buff.
                return enemy.MoveIndex % 2 == 0
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                    )
                    : new Intent(IntentType.Buff, 0);

            case KE.CrossbowRubyRaider:
                // FireDamage
                return enemy.MoveIndex % 2 == 0
                    ? new Intent(IntentType.Defend, 3)
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 14)
                    );

            case KE.TrackerRubyRaider:
                // HoundsDamage(1) x HoundsRepeat, which is the ascension-dependent part —
                // the emulator announced a flat 9, its A9 repeat count, as one hit.
                return enemy.MoveIndex == 0
                    ? new Intent(IntentType.Debuff, 2)
                    : new Intent(
                        IntentType.Attack,
                        1,
                        Hits: Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    );

            case KE.Seapunk:
                return (enemy.MoveIndex % 3) switch
                {
                    // SeaKickDamage
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 11)
                    ),
                    // SpinningKickDamage x SpinningKickRepeat (2 x 4, no ascension term).
                    // Pre-multiplied to 8, this announced 9 against the live game's 12: the
                    // Strength from Bubble Burp lands on each of the four hits.
                    1 => new Intent(IntentType.Attack, 2, Hits: 4),
                    _ => new Intent(IntentType.Buff, 0),
                };

            case KE.ShrinkerBeetle:
                return enemy.MoveIndex == 0
                    ? new Intent(IntentType.Debuff, 1)
                    : (
                        enemy.MoveIndex % 2 == 1
                            // ChompDamage
                            ? new Intent(
                                IntentType.Attack,
                                Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                            )
                            // StompDamage
                            : new Intent(
                                IntentType.Attack,
                                Ascension.Value(ascension, Ascension.DeadlyEnemies, 14, 13)
                            )
                    );

            case KE.Nibbit:
                // Alone Nibbit: Butt, Slice+block, Hiss loop.
                return (enemy.MoveIndex % 3) switch
                {
                    // ButtDamage
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 13, 12)
                    ),
                    // SliceDamage
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6)
                    ),
                    _ => new Intent(IntentType.Buff, 0),
                };

            case KE.LeafSlimeS:
            {
                // Attacks are LeafSlimeS.TackleDamage.
                // Both branches CannotRepeat → strictly alternating, but RandomBranchState
                // always consumes 1 RNG call (even on initialization and forced transitions).
                double pick = rng.NextDouble();
                if (enemy.LastMove == -1)
                {
                    // Initialization: 50/50 (both available in empty StateLog).
                    if (pick < 0.5)
                    {
                        enemy.LastMove = 0;
                        return new Intent(
                            IntentType.Attack,
                            Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3)
                        );
                    }
                    enemy.LastMove = 1;
                    return new Intent(IntentType.Debuff, 1);
                }
                if (enemy.LastMove == 0)
                {
                    // Last was Attack (CannotRepeat) → forced Debuff.
                    enemy.LastMove = 1;
                    return new Intent(IntentType.Debuff, 1);
                }
                // Last was Debuff (CannotRepeat) → forced Attack.
                enemy.LastMove = 0;
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3)
                );
            }

            case KE.TwigSlimeS:
                // TackleDamage
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4)
                );

            case KE.LeafSlimeM:
                return enemy.MoveIndex % 2 == 0
                    // ClumpDamage
                    ? new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    )
                    : new Intent(IntentType.Debuff, 2);

            case KE.TwigSlimeM:
            {
                // Initialization call (MoveIndex=1, LastMove=-1): initial state is STICKY_SHOT,
                // no RNG consumed (the state machine starts at the pre-set initial state).
                if (enemy.MoveIndex == 1 && enemy.LastMove == -1)
                {
                    return new Intent(IntentType.Debuff, 1);
                }

                // RandomBranchState always consumes 1 RNG call.
                double pick = rng.NextDouble();
                // LastMove -1: round-2 call after initial Sticky Shot (CannotRepeat) → force Attack.
                // LastMove  1: last chosen move was Sticky Shot (CannotRepeat) → force Attack.
                if (enemy.LastMove is -1 or 1)
                {
                    enemy.LastMove = 0;
                    return new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 12, 11)
                    );
                }
                // LastMove 2: two consecutive attacks (CanRepeatXTimes=2 exhausted) → force Sticky.
                if (enemy.LastMove == 2)
                {
                    enemy.LastMove = 1;
                    return new Intent(IntentType.Debuff, 1);
                }
                // LastMove 0: one consecutive attack → 50/50.
                if (pick < 0.5)
                {
                    enemy.LastMove = 2;
                    return new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 12, 11)
                    );
                }
                enemy.LastMove = 1;
                return new Intent(IntentType.Debuff, 1);
            }

            case KE.TwoTailedRat:
            {
                // Four branches, all reached from every move, with weights that MOVE: when
                // the rat can summon, CALL_FOR_BACKUP weighs 0.75 and the other three a
                // twelfth each; when it cannot, backup weighs nothing and the others weigh
                // one. SCREECH also carries a cooldown of 3 on top of CannotRepeat. The
                // emulator summoned whenever it was able and otherwise cycled on MoveIndex.
                const int scratch = 0;
                const int bite = 1;
                const int screech = 2;
                const int backup = 3;

                int move;
                if (enemy.LastMove < 0 && !enemy.StartsOnBranch)
                {
                    // A rat that started the fight opens on its StarterMoveIndex move.
                    move = enemy.MoveIndex % 3;
                }
                else
                {
                    // CanSummon() also refuses when any OTHER living rat has already
                    // queued CALL_FOR_BACKUP, which is what stops a pack from all
                    // summoning at once: the rats roll in roster order, and the first to
                    // pick backup takes the option away from the rest of the pass.
                    bool anotherRatIsCalling =
                        roster?.Any(other =>
                            other != enemy
                            && other.Hp > 0
                            && other.DefId == KE.TwoTailedRat
                            && other.LastMove == backup
                        ) ?? false;
                    // CanSummon() also needs a free slot, and TwoTailedRatsNormal only
                    // declares five: once the pack fills them GetNextSlot comes back
                    // empty and backup weighs nothing, so the last rat attacks instead.
                    bool slotIsFree = (roster?.Count(other => other.Hp > 0) ?? 0) < RatSlots;
                    bool canSummon =
                        BuffSystem.Get(enemy.Buffs, BuffId.SummonCooldown) <= 0
                        && BuffSystem.Get(enemy.Buffs, BuffId.BackupCount) < 3
                        && slotIsFree
                        && !anotherRatIsCalling;
                    float ordinary = canSummon ? 1f / 12f : 1f;
                    bool screechOnCooldown = enemy
                        .MoveHistory.AsEnumerable()
                        .Reverse()
                        .Take(3)
                        .Contains(screech);

                    move = PickWeightedBranch(
                        [
                            (scratch, enemy.LastMove == scratch ? 0f : ordinary),
                            (bite, enemy.LastMove == bite ? 0f : ordinary),
                            (
                                screech,
                                enemy.LastMove == screech || screechOnCooldown ? 0f : ordinary
                            ),
                            // UseOnlyOnce: a rat calls for backup once in a combat.
                            (backup, canSummon && !enemy.OnceOnlyMoveUsed ? 0.75f : 0f),
                        ],
                        rng
                    );
                }

                enemy.OnceOnlyMoveUsed |= move == backup;
                enemy.LastMove = move;
                enemy.MoveHistory.Add(move);
                return move switch
                {
                    // ScratchDamage
                    scratch => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    ),
                    // DiseaseBiteDamage
                    bite => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6)
                    ),
                    screech => new Intent(IntentType.Debuff, 1),
                    _ => new Intent(IntentType.Buff, 0),
                };
            }

            case KE.CorpseSlug:
                return (enemy.MoveIndex % 3) switch
                {
                    // MultiAttackIntent(WhipSlapDamage, WhipSlapRepeat): 3, twice over,
                    // no ascension term. Folding it into a single 6 is the same number
                    // only while the slug has no Strength -- and Ravenous hands it
                    // Strength every time an ally dies. The game adds Strength to EACH
                    // hit, so at Strength 4 the game announces 14 and a single 6 gives
                    // 10. A live Underdocks capture reads "7x2" on this intent.
                    0 => new Intent(IntentType.Attack, 3, Hits: 2),
                    // GlompDamage
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    ),
                    _ => new Intent(IntentType.Debuff, 2),
                };

            case KE.SludgeSpinner:
            {
                int move;
                if (enemy.MoveIndex == 0)
                {
                    move = 0;
                }
                else
                {
                    int[] pool = enemy.LastMove switch
                    {
                        0 => [1, 2],
                        1 => [0, 2],
                        _ => [0, 1],
                    };
                    move = pool[rng.Next(pool.Length)];
                }
                enemy.LastMove = move;
                return move switch
                {
                    // OilSprayDamage. OIL_SPRAY_MOVE declares SingleAttackIntent first
                    // and DebuffIntent second, and a live capture reads Attack '8' then
                    // Debuff -- an attack that also applies Weak, not a debuff carrying
                    // damage.
                    0 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 9, 8)
                    ),
                    // SlamDamage
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 12, 11)
                    ),
                    // RageDamage; RAGE is attack + buff, and the live readout calls it
                    // an Attack — unlike OIL_SPRAY two cases up, which it calls a Debuff.
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 7, 6)
                    ),
                };
            }

            case KE.Toadpole:
                return (enemy.MoveIndex % 3) switch
                {
                    0 => new Intent(IntentType.Buff, 0),
                    // SpikeSpitDamage x SpikeSpitRepeat (repeat is 3, no ascension term)
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3),
                        Hits: 3
                    ),
                    // WhirlDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7)
                    ),
                };

            case KE.FossilStalker:
            {
                // AddBranch(state, 2) is the maxRepeats overload: uniform over the three
                // moves, weight 1, each barred only once it has come up twice running.
                // Entered at LATCH. The emulator had a hand-written opening sequence and
                // then a free roll, which drifted from the live game by turn two.
                const int latch = 0;
                const int tackle = 1;
                const int lash = 2;
                int move;
                // LastMove, not MoveIndex: this encounter builds its stalker with
                // moveIndex 1, so an "is this the first move" test written against
                // MoveIndex never fires and the machine rolled a branch at combat setup —
                // one draw the game never makes, which desynchronises every roll after it.
                if (enemy.LastMove < 0)
                {
                    move = latch;
                }
                else
                {
                    var eligible = new List<int>();
                    foreach (int candidate in (int[])[latch, tackle, lash])
                    {
                        bool spent = candidate == enemy.LastMove && enemy.LastMoveRepeats >= 2;
                        if (!spent)
                        {
                            eligible.Add(candidate);
                        }
                    }

                    move = PickBranch(eligible, rng);
                }

                enemy.LastMoveRepeats = move == enemy.LastMove ? enemy.LastMoveRepeats + 1 : 1;
                enemy.LastMove = move;
                return move switch
                {
                    // LatchDamage
                    latch => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 14, 12)
                    ),
                    // TackleDamage; TACKLE is attack + debuff (Frail): its primary intent
                    // is the Attack (15 live, with six Strength from Suck) and
                    // DeclaredIntent adds the DebuffIntent after it.
                    tackle => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 9)
                    ),
                    // LashDamage x LashRepeat (repeat is 2, no ascension term)
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 4, 3),
                        Hits: 2
                    ),
                };
            }

            case KE.PunchConstruct:
                return (enemy.MoveIndex % 3) switch
                {
                    0 => new Intent(IntentType.Defend, 10),
                    // FastPunchDamage x 2
                    1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 5),
                        Hits: 2
                    ),
                    // StrongPunchDamage
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 14)
                    ),
                };

            case KE.SewerClam:
                return enemy.MoveIndex % 2 == 0
                    ? new Intent(IntentType.Buff, 0)
                    // JetDamage
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 10)
                    );

            case KE.Guardbot:
                return new Intent(IntentType.Defend, 15);

            case KE.Noisebot:
                return new Intent(IntentType.Debuff, 2);

            case KE.Stabbot:
                // STAB_MOVE declares SingleAttackIntent BEFORE its DebuffIntent, so it
                // announces as an Attack of StabDamage -- it was typed Debuff with the
                // attack as its SECONDARY, which is the announcement inverted. The
                // `[types]` check could not see it: SecondaryIntentFor does say Attack for
                // this monster, and that check asks per MONSTER, not per move.
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 12, 11)
                );

            case KE.Zapbot:
                // ZapDamage
                return new Intent(
                    IntentType.Attack,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 15, 14)
                );

            case KE.ToughEgg:
                // HATCH once, then NIBBLE, which follows up to itself.
                return BuffSystem.Get(enemy.Buffs, BuffId.Hatch) > 0
                    ? new Intent(IntentType.Buff, 0)
                    // NibbleDamage
                    : new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 5, 4)
                    );

            case KE.SkulkingColony:
                return (enemy.MoveIndex % 4) switch
                {
                    // ZoomDamage, twice over
                    0 or 1 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 14)
                    ),
                    // InertiaDamage; INERTIA_MOVE is an attack plus a BuffIntent
                    2 => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 11, 9)
                    ),
                    // PiercingStabsDamage x PiercingStabsRepeat
                    _ => new Intent(
                        IntentType.Attack,
                        Ascension.Value(ascension, Ascension.DeadlyEnemies, 8, 7),
                        Hits: 2
                    ),
                };

            case KE.TheAdversaryMkOne:
                return (enemy.MoveIndex % 3) switch
                {
                    // SmashDamage
                    0 => new Intent(IntentType.Attack, 12),
                    // BeamDamage
                    1 => new Intent(IntentType.Attack, 15),
                    // BARRAGE_MOVE declares MultiAttackIntent(BarrageDamage, BarrageRepeat)
                    // BEFORE its BuffIntent, so it announces as an Attack of 8 twice
                    // over -- not as a Buff of 16, which is what the readout said and
                    // what a policy read. The Strength it also takes is the secondary.
                    _ => new Intent(IntentType.Attack, 8, Hits: 2),
                };

            case KE.TheAdversaryMkTwo:
                return (enemy.MoveIndex % 3) switch
                {
                    // BashDamage
                    0 => new Intent(IntentType.Attack, 13),
                    // FlameBeamDamage
                    1 => new Intent(IntentType.Attack, 16),
                    // BARRAGE_MOVE declares MultiAttackIntent(BarrageDamage, BarrageRepeat)
                    // BEFORE its BuffIntent, so it announces as an Attack of 9 twice
                    // over -- not as a Buff of 18, which is what the readout said and
                    // what a policy read. The Strength it also takes is the secondary.
                    _ => new Intent(IntentType.Attack, 9, Hits: 2),
                };

            case KE.TheAdversaryMkThree:
                return (enemy.MoveIndex % 3) switch
                {
                    // CrashDamage
                    0 => new Intent(IntentType.Attack, 15),
                    // FlameBeamDamage
                    1 => new Intent(IntentType.Attack, 18),
                    // BARRAGE_MOVE declares MultiAttackIntent(BarrageDamage, BarrageRepeat)
                    // BEFORE its BuffIntent, so it announces as an Attack of 10 twice
                    // over -- not as a Buff of 20, which is what the readout said and
                    // what a policy read. The Strength it also takes is the secondary.
                    _ => new Intent(IntentType.Attack, 10, Hits: 2),
                };

            case KE.Architect:
            case KE.BattleFriendV1:
            case KE.BattleFriendV2:
            case KE.BattleFriendV3:
            case KE.Byrdpip:
            case KE.Osty:
            case KE.PaelsLegion:
                return new Intent(IntentType.Unknown, 0);

            default:
                return GeneratedData.Enemies.ChooseIntent(enemy.DefId, enemy.MoveIndex, 0, rng);
        }
    }

    /// <summary>
    /// RandomBranchState.GetNextState: roll NextFloat(total weight), then walk the branches
    /// in the order they were added, subtracting each weight until the roll runs out.
    /// </summary>
    /// <remarks>
    /// This used to claim that the draw differs from <c>rng.Next(n)</c>. It does not, and
    /// the claim sent a reader off to "fix" six correct call sites. Both take exactly one
    /// value: the game's <c>Rng.NextFloat(max)</c> is <c>(float)(NextDouble() * max)</c>
    /// and <c>MegaRandom.Next(max)</c> is <c>(int)(NextDouble() * max)</c>. With every
    /// eligible branch at weight 1 the walk returns <c>ceil(roll) - 1</c> and the cast
    /// returns <c>floor(roll)</c>, which agree for every roll that is not an exact integer
    /// — checked over 400,000 draws, no mismatch. So <c>eligible[rng.Next(eligible.Count)]</c>
    /// is a faithful uniform branch and needs no rewriting.
    ///
    /// What the weighted form below IS needed for is weights that are not 1 — a Two-Tailed
    /// Rat's summon at 0.75 against three twelfths — and for keeping the branch ORDER,
    /// which is AddBranch order and not the emulator's move numbering when the two differ.
    /// </remarks>
    private static int PickBranch(List<int> eligible, Random rng) =>
        PickWeightedBranch([.. eligible.Select(move => (move, 1f))], rng);

    /// <summary>
    /// The weighted form. Weights are not always 1: a Two-Tailed Rat that can summon
    /// weighs the summon at 0.75 and its three other moves at a twelfth each, so the roll
    /// is over a total that is not the branch count.
    /// </summary>
    private static int PickWeightedBranch(List<(int Move, float Weight)> branches, Random rng)
    {
        float total = branches.Sum(branch => branch.Weight);

        // `RandomBranchState.GetNextState` when EVERY branch is blocked: `NextFloat(0)`
        // still increments the counter -- it is `Counter++` before the draw, unconditional
        // -- and the walk then subtracts zero and returns on the FIRST entry. So the game
        // spends a draw and takes the first DECLARED branch.
        //
        // This returned the LAST branch and spent NOTHING, which is wrong twice: the wrong
        // move, and a MonsterAi stream one draw behind the game for the rest of the fight.
        // The Flyconid is where it shows -- all three of its moves can be blocked at once,
        // by CannotRepeat and two cooldowns -- and a turn where only one branch is eligible
        // hides it, which is why it took seven turns to surface.
        if (total <= 0f)
        {
            rng.NextDouble();
            return branches[0].Move;
        }

        float roll = (float)(rng.NextDouble() * total);
        foreach (var branch in branches)
        {
            roll -= branch.Weight;
            if (roll <= 0f)
            {
                return branch.Move;
            }
        }

        return branches[^1].Move;
    }

    private static Intent? SecondaryIntentFor(EnemyState enemy)
    {
        return enemy.DefId switch
        {
            // LivingShield.SMASH_MOVE declares Attack then Buff; Shield Slam is
            // the distinct flat-6 move and carries no buff, even after an ally dies.
            KE.LivingShield
                when enemy.CurrentIntent.Type == IntentType.Attack
                    && enemy.CurrentIntent.Magnitude > 6 => new Intent(IntentType.Buff, 0),
            // GremlinMerc.cs:61-63: DOUBLE_SMASH's second intent is a DebuffIntent (its
            // WeakPower(2)) and HEHE's a BuffIntent (its StrengthPower(2)); neither carries
            // a number. The attack is the primary alone -- this had declared it twice.
            KE.GremlinMerc when enemy.MoveIndex % 3 == 1 => new Intent(IntentType.Debuff, 0),
            KE.GremlinMerc when enemy.MoveIndex % 3 == 2 => new Intent(IntentType.Buff, 0),
            // FAST_PUNCH_MOVE declares a numberless DebuffIntent after its two hits.
            KE.PunchConstruct when enemy.MoveIndex % 3 == 1 => new Intent(IntentType.Debuff, 0),
            // Crusher.cs: BUG_STING declares Debuff after its two hits (Weak2/Frail2);
            // GUARDED_STRIKE declares Defend after its hit (Block18).
            KE.Crusher when enemy.MoveIndex % 5 == 2 => new Intent(IntentType.Debuff, 0),
            KE.Crusher when enemy.MoveIndex % 5 == 4 => new Intent(IntentType.Defend, 0),
            // OwlMagistrate.cs: VERDICT declares Debuff after its hit (Vulnerable4).
            KE.OwlMagistrate when enemy.MoveIndex % 4 == 3 => new Intent(IntentType.Debuff, 0),
            // KinPriest.cs: both orb moves declare Attack followed by Debuff;
            // Beam and Ritual have only their primary intent.
            KE.KinPriest when enemy.MoveIndex % 4 is 0 or 1 => new Intent(IntentType.Debuff, 0),
            // MechaKnight.cs: Flamethrower attacks then adds four Burns; Windup
            // declares Defend followed by Buff (Block15 then Strength5).
            KE.MechaKnight when enemy.MoveIndex > 0 && (enemy.MoveIndex - 1) % 3 == 0 => new Intent(
                IntentType.StatusCard,
                0
            ),
            KE.MechaKnight when enemy.MoveIndex > 0 && (enemy.MoveIndex - 1) % 3 == 1 => new Intent(
                IntentType.Buff,
                0
            ),
            // OIL_SPRAY's second intent is the Weak it applies, RAGE's the Strength it
            // takes. Keyed off the move actually chosen: the spinner picks at random, so
            // MoveIndex says nothing about which move this is.
            KE.SludgeSpinner when enemy.LastMove == 0 => new Intent(IntentType.Debuff, 1),
            KE.SludgeSpinner when enemy.LastMove == 2 => new Intent(IntentType.Buff, 3),
            // BUBBLE_BURP declares both BuffIntent and DefendIntent.
            KE.Seapunk when enemy.MoveIndex % 3 == 2 => new Intent(IntentType.Defend, 0),
            // ADVANCED_GAS's second intent is the card debuff it inflicts.
            KE.LivingFog when enemy.MoveIndex == 0 => new Intent(IntentType.Debuff, 1),
            KE.VineShambler when enemy.MoveIndex % 3 == 1 => new Intent(IntentType.Debuff, 1),
            // STAB_MOVE's second declared intent is the Frail it applies, now that the
            // attack it leads with is the primary one.
            KE.Stabbot => new Intent(IntentType.Debuff, 1),
            KE.SkulkingColony when enemy.MoveIndex % 4 == 2 => new Intent(IntentType.Buff, 0),
            // BARRAGE_MOVE's second declared intent is the Strength it takes, now that
            // the attack it leads with is the primary one.
            KE.TheAdversaryMkOne when enemy.MoveIndex % 3 == 2 => new Intent(IntentType.Buff, 2),
            KE.TheAdversaryMkTwo when enemy.MoveIndex % 3 == 2 => new Intent(IntentType.Buff, 3),
            KE.TheAdversaryMkThree when enemy.MoveIndex % 3 == 2 => new Intent(IntentType.Buff, 4),
            KE.Flyconid
                when enemy.CurrentIntent.Type == IntentType.Debuff
                    && enemy.CurrentIntent.Magnitude > 2 => new Intent(
                IntentType.Attack,
                enemy.CurrentIntent.Magnitude
            ),
            _ => null,
        };
    }

    // ── Per-enemy buff actions ─────────────────────────────────────────────────

    private static void ApplyBuffIntent(EnemyState enemy, CombatState state, Random rng)
    {
        // Buff amounts are ascension-dependent too (Nibbit's Hiss gives 2 at A8, 3 at A9).
        int ascension = state.AscensionLevel;
        switch (enemy.DefId)
        {
            case KE.Tunneler:
                // BURROW_MOVE: BurrowedPower, then GainBlock(BlockGain). BlockGain is the
                // TOUGH pair (37, 32) and Tough is live at A8, so 37 here.
                BuffSystem.Apply(enemy.Buffs, BuffId.Burrowed, 1);
                enemy.Block += BuffSystem.IncomingBlock(
                    Ascension.Value(state.AscensionLevel, Ascension.ToughEnemies, 37, 32),
                    enemy.Buffs
                );
                break;
            case KE.CalcifiedCultist:
                // Incantation: apply 2 Ritual to self (gains +2 Strength each subsequent turn).
                BuffSystem.Apply(enemy.Buffs, BuffId.Ritual, 2);
                break;

            case KE.DampCultist:
                // Incantation: IncantationAmount of Ritual to self. The comment used to
                // say "deadly ascension value" and mean it — 6 at every level.
                BuffSystem.Apply(
                    enemy.Buffs,
                    BuffId.Ritual,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 5)
                );
                break;

            case KE.Nibbit:
                // Hiss: HissStrengthGain.
                BuffSystem.Apply(
                    enemy.Buffs,
                    BuffId.Strength,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 3, 2)
                );
                break;

            case KE.Exoskeleton:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                break;

            case KE.FuzzyWurmCrawler:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 7);
                break;

            case KE.Seapunk:
                // BubbleBlock is a ToughEnemies value (live at A8); BubbleStr is a
                // DeadlyEnemies one (not live at A8).
                enemy.Block += BuffSystem.IncomingBlock(
                    Ascension.Value(ascension, Ascension.ToughEnemies, 8, 7),
                    enemy.Buffs
                );
                BuffSystem.Apply(
                    enemy.Buffs,
                    BuffId.Strength,
                    Ascension.Value(ascension, Ascension.DeadlyEnemies, 2, 1)
                );
                break;

            case KE.SnappingJaxfruit:
                DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                break;

            case KE.BowlbugNectar:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.CubexConstruct:
                // CHARGE_UP only buffs; the blasts execute in the attack branch.
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                break;

            case KE.SewerClam:
                // PRESSURIZE_MOVE is StrengthPower(4) and nothing else. The block it also
                // used to gain here was invented: the clam's block comes from PlatingPower
                // alone, which grants it to every owner at the end of its side's turn, so
                // adding it a second time on the clam's buff turn gave it twice what the
                // game shows.
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 4);
                break;

            case KE.Fogmog:
            {
                // SWIPE executes in the attack branch now; what is left here is ILLUSION,
                // and ILLUSION_MOVE is the machine's INITIAL state with nothing leading
                // back to it -- it happens once per combat and the eye persists from then
                // on by reviving. This used to be guarded on "no eye is alive" and to
                // sweep away any dead one first, which was standing in for the revive:
                // with IllusionPower modelled that guard deletes an eye in the middle of
                // coming back and hands the fight a fresh one in its place.
                {
                    // Fogmog's ILLUSION_MOVE adds the eye with a plain CreatureCmd.Add.
                    // In the whole monster set only Wriggler sets StartStunned, so nothing
                    // else arrives stunned — and the enemy phase iterates a snapshot of
                    // the roster anyway, so a newcomer already sits out the phase that
                    // made it. Stunning on top of that delayed the eye's three Dazed by a
                    // turn, which a live capture shows in the player's hand.
                    var eye = CreateEnemy(
                        KE.EyeWithTeeth,
                        rng,
                        new Intent(IntentType.Debuff, 3),
                        state: state
                    );
                    ApplyIllusion(eye);
                    state.Enemies.Insert(
                        state.Enemies.IndexOf(enemy),
                        Effects.RelicEffects.Spawned(state, eye)
                    );
                }

                break;
            }

            case KE.BruteRubyRaider:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 3);
                break;

            case KE.Toadpole:
                BuffSystem.Apply(enemy.Buffs, BuffId.Thorns, 2);
                break;

            case KE.SpinyToad:
                BuffSystem.Apply(enemy.Buffs, BuffId.Thorns, enemy.CurrentIntent.Magnitude);
                break;

            case KE.Ovicopter:
                if (enemy.CurrentIntent.Magnitude > 0)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                }
                else
                {
                    SummonToughEggs(enemy, state, rng);
                }

                break;

            case KE.Axebot:
                enemy.Block += BuffSystem.IncomingBlock(15, enemy.Buffs);
                break;

            case KE.DevotedSculptor:
                BuffSystem.Apply(enemy.Buffs, BuffId.Ritual, enemy.CurrentIntent.Magnitude);
                break;

            case KE.Fabricator:
                SummonFabricatorBots(enemy, state, rng, includeDefensive: true);
                break;

            case KE.FrogKnight:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.TurretOperator:
                // RELOAD_MOVE is Strength and nothing else. The 25 block was RampartPower
                // -- which lives on the LIVING SHIELD, grants at the start of the
                // PLAYER's turn, and stops the moment the shield dies. Handing it to the
                // turret on its own reload made the shield's death cost nothing.
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.ScrollOfBiting:
                // MORE_TEETH_MOVE is Strength and nothing else. PaperCuts used to be
                // applied here too, which is the wrong trigger AND the wrong branch:
                // `AfterDamageGiven` fires when the scroll lands UNBLOCKED damage, so it
                // belongs to the attack, not to the buff.
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.TheObscura:
                if (enemy.MoveIndex == 0)
                {
                    SummonParafright(enemy, state);
                }
                else
                {
                    foreach (var ally in state.Enemies.Where(e => e.Hp > 0))
                    {
                        BuffSystem.Apply(ally.Buffs, BuffId.Strength, 3);
                    }
                }

                break;

            case KE.Wriggler:
                // WRIGGLE_MOVE adds an Infection, not a Dazed — the same status its
                // parent deals three of, and one that burns for 3 in hand at end of turn.
                AddStatus(state, ST.Infection, enemy.CurrentIntent.Magnitude);
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                break;

            case KE.FakeMerchant:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.ThievingHopper:
                // `FlutterMove` applies FlutterPower at 5. The intent announces nothing,
                // which is what a bare BuffIntent does -- the power is real regardless.
                BuffSystem.Apply(enemy.Buffs, BuffId.Flutter, 5);
                break;

            case KE.MysteriousKnight: // inherits FlailKnight's WAR_CHANT
            case KE.FlailKnight:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 3);
                break;

            case KE.BygoneEffigy:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.Entomancer:
                int hive = BuffSystem.Get(enemy.Buffs, BuffId.PersonalHive);
                if (hive < 3)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.PersonalHive, 1);
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 1);
                }
                else
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 2);
                }
                break;

            case KE.OwlMagistrate:
                // JUDICIAL_FLIGHT takes off and applies SoarPower(1), which halves powered
                // attack damage against the owl until VERDICT brings it down. Recorded as
                // an unmodelled gap (O19) on the grounds that the powered-attack
                // distinction was one `IncomingDamage` could not make -- it turns out that
                // function IS the powered-attack path, so the gap was a misreading.
                BuffSystem.Apply(enemy.Buffs, BuffId.Soar, enemy.CurrentIntent.Magnitude);
                break;

            case KE.PhantasmalGardener:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.CeremonialBeast:
                // `StampMove` applies `PlowPower` at `PlowAmount` -- 150, or 160 against
                // DeadlyEnemies -- which is an HP THRESHOLD, not the move's damage. The
                // emulator passed the INTENT's magnitude, which for a BuffIntent is zero,
                // so the threshold was never above zero and the power could never fire.
                //
                // Nothing read `BuffId.Plow` at all until O-Plow, so a wrong amount cost
                // nothing and stayed invisible. With the trigger wired the Beast simply
                // never stunned: it plowed forever where the live game takes one wound and
                // switches to the BEAST_CRY loop.
                BuffSystem.Apply(
                    enemy.Buffs,
                    BuffId.Plow,
                    Ascension.Value(state.AscensionLevel, Ascension.DeadlyEnemies, 160, 150)
                );
                break;

            case KE.Crusher:
            case KE.Rocket:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.Queen:
                // Two of the Queen's moves announce as Buffs. ENRAGE is StrengthPower(2)
                // on herself and nothing else; BURN_BRIGHT_FOR_ME is Strength for the
                // teammates and block for her.
                if (enemy.LastMove == 5)
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                    break;
                }

                foreach (var ally in state.Enemies.Where(e => e.Hp > 0 && e.DefId != KE.Queen))
                {
                    BuffSystem.Apply(ally.Buffs, BuffId.Strength, 1);
                }

                enemy.Block += BuffSystem.IncomingBlock(enemy.CurrentIntent.Magnitude, enemy.Buffs);
                break;

            case KE.SoulFysh:
                BuffSystem.Apply(enemy.Buffs, BuffId.Intangible, enemy.CurrentIntent.Magnitude);
                break;

            case KE.TheInsatiable:
                if (enemy.MoveIndex == 0)
                {
                    // SandpitPower goes on the INSATIABLE, targeting the player -- it is
                    // `PowerCmd.Apply(sandpitPower, base.Creature, 4m, ...)` with
                    // `sandpitPower.Target = target`. Applying it to the player instead
                    // left the enemy's counter at ZERO, which inverts the whole mechanic:
                    // a Frantic Escape is meant to BUY a turn by pushing the count up,
                    // and instead the first one played took it to 1 and the next enemy
                    // turn ticked it to 0 and killed the player outright. A live capture
                    // has the player at 48 where the emulator had them dead.
                    BuffSystem.Apply(enemy.Buffs, BuffId.Sandpit, 4);
                    // Six cards in ONE loop: `pile = (i < 3) ? Draw : Discard`, every one
                    // of them at CardPilePosition.Random. The discard half is random too
                    // -- appending it skipped three draws off the shuffle stream and put
                    // the cards somewhere the game did not.
                    Effects.CardEffects.AddCardToDrawPileRandomly(state, ST.FranticEscape, 3, rng);
                    Effects.CardEffects.AddCardToDiscardPileRandomly(
                        state,
                        ST.FranticEscape,
                        3,
                        rng
                    );
                }
                else
                {
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                }
                break;

            case KE.KinFollower:
            case KE.KinPriest:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.Vantom:
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.WaterfallGiant:
                // Two different moves announce as a buff. PRESSURIZE converts the whole
                // announced amount into Steam Eruption; SIPHON heals for it and racks up
                // the same flat 3 the giant's other moves do.
                if (enemy.MoveIndex == 0)
                {
                    BuffSystem.Apply(
                        enemy.Buffs,
                        BuffId.SteamEruption,
                        enemy.CurrentIntent.Magnitude
                    );
                }
                else
                {
                    enemy.Hp = Math.Min(enemy.MaxHp, enemy.Hp + enemy.CurrentIntent.Magnitude);
                    BuffSystem.Apply(enemy.Buffs, BuffId.SteamEruption, 3);
                }

                break;

            case KE.TwoTailedRat:
                SummonRatBackup(enemy, state, rng);
                break;

            case KE.Guardbot:
                foreach (var ally in state.Enemies.Where(e => e.Hp > 0 && e.DefId == KE.Fabricator))
                {
                    ally.Block += BuffSystem.IncomingBlock(15, ally.Buffs);
                }
                break;

            case KE.ToughEgg:
            {
                BuffSystem.Remove(enemy.Buffs, BuffId.Hatch);
                enemy.Buffs.RemoveAll(buff => buff.Id != BuffId.Minion);
                // v0.111.0 Hatch uses RunRng.Niche.NextInt(min, max + 1): include
                // the declared maximum, 22 below A8 or 23 at A8+. It is one plain
                // roll, without the initial monster HP uniqueness rule.
                var niche = state.NicheHpRng ?? rng;
                int hatchlingHp = niche.Next(
                    Ascension.Value(state.AscensionLevel, Ascension.ToughEnemies, 20, 19),
                    Ascension.Value(state.AscensionLevel, Ascension.ToughEnemies, 23, 22) + 1
                );
                enemy.Hp = hatchlingHp;
                enemy.MaxHp = hatchlingHp;
                break;
            }
        }
    }

    private static void ApplyDebuffIntent(EnemyState enemy, CombatState state, Random rng)
    {
        // `BeastCryMove` applies `RingingPower(1)` to the player. BEAST_CRY announces a
        // DebuffIntent, so it resolves here -- the Beast's other power move, STAMP, is a
        // BuffIntent and resolves in `ApplyBuffIntent`.
        if (enemy.DefId == KE.CeremonialBeast && enemy.MoveIndex >= BeastCryLoopBase)
        {
            BuffSystem.Apply(state.PlayerBuffs, BuffId.Ringing, 1);
            return;
        }

        switch (enemy.DefId)
        {
            case KE.Chomper:
                AddStatus(state, ST.Dazed, 3);
                break;

            case KE.LeafSlimeS:
            case KE.TwigSlimeM:
                AddStatus(state, ST.Slimed, 1);
                break;

            case KE.LeafSlimeM:
                AddStatus(state, ST.Slimed, 2);
                break;

            case KE.TwoTailedRat:
            case KE.CorpseSlug:
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, enemy.CurrentIntent.Magnitude);
                break;

            case KE.TerrorEel:
                BuffSystem.Apply(
                    state.PlayerBuffs,
                    BuffId.Vulnerable,
                    enemy.CurrentIntent.Magnitude
                );
                BuffSystem.Remove(enemy.Buffs, BuffId.TerrorQueued);
                // TERROR_MOVE's FollowUpState is CRASH, and the increment at the end of
                // this turn is what carries the index there.
                enemy.MoveIndex = -1;
                break;

            case KE.LagavulinMatriarch:
                // SOUL_SIPHON takes the Strength and Dexterity it gives itself: the
                // matriarch's attacks climb by 2 every fourth turn, which is the whole
                // shape of the fight after the first cycle.
                Effects.RelicEffects.GainPlayerStrength(state, -enemy.CurrentIntent.Magnitude);
                BuffSystem.Apply(
                    state.PlayerBuffs,
                    BuffId.Dexterity,
                    -enemy.CurrentIntent.Magnitude
                );
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.ShrinkerBeetle:
                // Applies permanent Shrink (–1 = infinite; tied to ShrinkerBeetle's life).
                bool alreadyShrunk = BuffSystem.Get(state.PlayerBuffs, BuffId.Shrink) != 0;
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Shrink, -1);
                if (!alreadyShrunk && BuffSystem.Get(state.PlayerBuffs, BuffId.Shrink) != 0)
                    state.ShrinkApplier = enemy;
                break;

            case KE.Mawler:
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Vulnerable, 3);
                break;

            case KE.Flyconid:
                // VULNERABLE_SPORES_MOVE, the only one of the three that is a bare
                // DebuffIntent. FRAIL_SPORES used to be handled here too, keyed on the
                // intent's magnitude -- but it announces as an Attack and so never
                // arrived; its rider lives with the attack now.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Vulnerable, 2);
                break;

            case KE.LivingFog:
                DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Smoggy, 1);
                break;

            case KE.BowlbugSilk:
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 1);
                break;

            case KE.Myte:
                AddStatusToHand(state, ST.Toxic, enemy.CurrentIntent.Magnitude);
                break;

            case KE.HunterKiller:
                // TENDERIZING_GOOP_MOVE is `PowerCmd.Apply<TenderPower>(targets, 1)`, not
                // Vulnerable. Vulnerable made the bite announce 25 where the game says 17,
                // and made every later hit land half again as hard for the rest of the
                // fight; Tender costs the player Strength and Dexterity per card played
                // and gives it back at end of turn.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Tender, 1);
                break;

            case KE.FakeMerchant:
                DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 1);
                break;

            case KE.PhrogParasite:
                AddStatus(state, ST.Infection, enemy.CurrentIntent.Magnitude);
                break;

            case KE.SpectralKnight:
                bool alreadyHexed = BuffSystem.Get(state.PlayerBuffs, BuffId.Hex) > 0;
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Hex, enemy.CurrentIntent.Magnitude);
                if (!alreadyHexed && BuffSystem.Get(state.PlayerBuffs, BuffId.Hex) > 0)
                    state.HexApplier = enemy;
                break;

            case KE.MagiKnight:
                // `DampenPower.AfterApplied` is what does the work: every upgraded card the
                // player holds is downgraded until the power goes. Applying the buff and
                // stopping there made this move a debuff the player carried and never paid.
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Dampen, enemy.CurrentIntent.Magnitude);
                Effects.CardEffects.ApplyDampen(state);
                break;

            case KE.Aeonglass:
                // INCREASING_INTENSITY. The old code here dealt attack damage and applied
                // an EbbPower(3) that nothing in the current build ever applies -- and
                // BuffId.Ebb was read nowhere, so it was a debuff the player carried and
                // never paid. What the move actually does is put WitherAmount Withers in
                // the discard and take StrengthPower(IncreasingIntensityBaseStrength +
                // AdditionalStrength), where AdditionalStrength counts the times this move
                // has already run -- so the Strength climbs 4, 5, 6 rather than sitting at
                // a flat 4. Every third move index is this one, which is that count.
                // `MatchWitherToUpgradeCount` and the FakeUpgrade sweep over every
                // Wither already in play: each run of this move makes EVERY Wither hit 3
                // harder, and the ones it adds arrive at the new level. So the count is
                // raised BEFORE the Withers are handed over.
                BuffSystem.Apply(enemy.Buffs, BuffId.WitherIntensity, 1);
                AddStatus(state, ST.Wither, enemy.CurrentIntent.Magnitude);
                BuffSystem.Apply(
                    enemy.Buffs,
                    BuffId.Strength,
                    Ascension.Value(state.AscensionLevel, Ascension.DeadlyEnemies, 4, 3)
                        + enemy.MoveIndex / 3
                );
                break;

            case KE.KnowledgeDemon:
                OpenCurseOfKnowledge(enemy, state);
                break;

            case KE.Queen:
                // Two of the Queen's moves announce as Debuffs, and they are not the same
                // debuff: YOU_ARE_MINE is Frail, Weak and Vulnerable at 99 apiece, and it
                // used to reach this branch and hand out ChainsOfBinding 99 instead.
                if (enemy.LastMove == 1)
                {
                    BuffSystem.Apply(
                        state.PlayerBuffs,
                        BuffId.Frail,
                        enemy.CurrentIntent.Magnitude
                    );
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, enemy.CurrentIntent.Magnitude);
                    BuffSystem.Apply(
                        state.PlayerBuffs,
                        BuffId.Vulnerable,
                        enemy.CurrentIntent.Magnitude
                    );
                    break;
                }

                BuffSystem.Apply(
                    state.PlayerBuffs,
                    BuffId.ChainsOfBinding,
                    enemy.CurrentIntent.Magnitude
                );
                break;

            case KE.SoulFysh:
                Effects.CardEffects.AddCardToDrawPileRandomly(state, ST.Beckon, 1, rng);
                AddStatus(state, ST.Beckon, enemy.CurrentIntent.Magnitude - 1);
                break;

            case KE.TestSubject:
                // BURNING_GROWL: BurningGrowlBurnCount Burns into the DISCARD -- this
                // case put them in the player's hand, and nothing reached it anyway while
                // the move was typed Buff -- then BurningGrowlStrengthGain for itself.
                AddStatus(state, ST.Burn, enemy.CurrentIntent.Magnitude);
                BuffSystem.Apply(
                    enemy.Buffs,
                    BuffId.Strength,
                    Ascension.Value(state.AscensionLevel, Ascension.DeadlyEnemies, 3, 2)
                );
                break;

            case KE.SlimedBerserker:
                // Two of the berserker's moves are Debuffs now that LEECHING_HUG is typed
                // the way it announces, so the branch has to say which one it is.
                if (enemy.MoveIndex % 4 == 2)
                {
                    // LEECHING_HUG: WeakPower(3) on the player, StrengthPower(3) on itself.
                    BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 3);
                    BuffSystem.Apply(enemy.Buffs, BuffId.Strength, 3);
                    break;
                }

                AddStatus(state, ST.Slimed, enemy.CurrentIntent.Magnitude);
                break;

            case KE.TheLost:
                Effects.RelicEffects.GainPlayerStrength(state, -enemy.CurrentIntent.Magnitude);
                enemy.PossessedPlayerStrength += enemy.CurrentIntent.Magnitude;
                BuffSystem.Apply(enemy.Buffs, BuffId.Strength, enemy.CurrentIntent.Magnitude);
                break;

            case KE.TheForgotten:
                BuffSystem.Apply(
                    state.PlayerBuffs,
                    BuffId.Dexterity,
                    -enemy.CurrentIntent.Magnitude
                );
                enemy.PossessedPlayerDexterity += enemy.CurrentIntent.Magnitude;
                // Miasma steals, gains block using its old Dexterity, then gains Dexterity.
                enemy.Block += BuffSystem.IncomingBlock(8, enemy.Buffs);
                BuffSystem.Apply(enemy.Buffs, BuffId.Dexterity, enemy.CurrentIntent.Magnitude);
                break;

            case KE.EyeWithTeeth:
                AddStatus(state, ST.Dazed, 3);
                break;

            case KE.TrackerRubyRaider:
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 2);
                break;

            case KE.PunchConstruct:
                DealAttackDamage(enemy, state, enemy.CurrentIntent.Magnitude, rng);
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Frail, 1);
                break;

            case KE.SlitheringStrangler:
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Constrict, 3);
                break;

            case KE.HauntedShip:
                BuffSystem.Apply(state.PlayerBuffs, BuffId.Weak, 3);
                AddStatus(state, ST.Dazed, 5);
                break;

            case KE.Noisebot:
                AddStatus(state, ST.Dazed, 1);
                Effects.CardEffects.AddCardToDrawPileRandomly(state, ST.Dazed, 1, rng);
                break;
        }
    }

    /// <summary>
    /// One attack of <paramref name="hits"/> hits, as a single <c>AttackCommand</c>.
    /// </summary>
    /// <remarks>
    /// The hits land one at a time so block, Thorns and every per-instance effect see each
    /// of them -- but <c>SuckPower.AfterAttack</c> fires ONCE for the whole command, with
    /// <c>Amount x</c> the number of hits that dealt unblocked damage. Triggering it per
    /// hit instead fed the Strength it grants back into the SAME attack's later hits: a
    /// Fossil Stalker's two-hit Lash landed its second swing three higher than the game's,
    /// and every announcement after it followed.
    /// </remarks>
    private static void DealAttack(
        EnemyState enemy,
        CombatState state,
        int baseDamage,
        int hits,
        Random rng
    )
    {
        int landed = 0;
        for (int i = 0; i < hits; i++)
        {
            // Each hit's `Kill` has already run, so a player at zero here is really dead;
            // one Fairy or Lizard Tail revived takes the remaining hits.
            if (state.PlayerHp <= 0 || enemy.Hp <= 0)
            {
                break;
            }

            if (DealAttackDamage(enemy, state, baseDamage, rng, triggerSuck: false))
            {
                landed++;
            }
        }

        TriggerSuck(enemy, landed);

        // PainfulStabsPower: `Amount` Wounds into the player's discard for each hit that
        // landed UNBLOCKED damage. It is a per-instance hook, so it counts the Test
        // Subject's climbing Multi Claw hit by hit -- which is precisely what the folded
        // announcement could not have told anyone, and what the hand-rolled attack path
        // that used to sit above `DealAttack` never triggered at all.
        int painfulStabs = BuffSystem.Get(enemy.Buffs, BuffId.PainfulStabs);
        if (painfulStabs > 0 && landed > 0)
        {
            AddStatus(state, ST.Wound, painfulStabs * landed);
        }
    }

    /// <summary>The enemy attack path, for tests about what modifies it.</summary>
    internal static void DealAttackDamageForTests(
        EnemyState enemy,
        CombatState state,
        int baseDamage
    ) => DealAttackDamage(enemy, state, baseDamage, new Random(0));

    private static bool DealAttackDamage(
        EnemyState enemy,
        CombatState state,
        int baseDamage,
        Random rng,
        bool triggerSuck = true
    )
    {
        // CreatureCmd.Damage skips an already-dead dealer or target. A dealer killed
        // by BeforeDamageReceived still finishes THIS hit, but cannot start another.
        if (state.PlayerHp <= 0 || enemy.Hp <= 0)
        {
            return false;
        }

        int damage = BuffSystem.IncomingDamage(
            baseDamage,
            enemy.Buffs,
            state.PlayerBuffs,
            weakDelta: Effects.RelicEffects.WeakMultiplierDeltaAgainstPlayer(state)
        );

        // `UndyingSigil.ModifyDamageMultiplicative`: an attacker already at or below its
        // own Doom hits the relic's owner for half. Applied after the buff chain, as a
        // multiplicative hook is.
        damage = (int)(
            damage * Effects.RelicEffects.IncomingDamageMultiplierFromDoom(state, enemy)
        );
        // `DiamondDiademPower.ModifyDamageMultiplicative` returns 0.5m on a POWERED attack
        // aimed at its owner. This path IS the powered-attack path, so the guard is the
        // power alone.
        if (BuffSystem.Get(state.PlayerBuffs, BuffId.DiamondDiadem) > 0)
        {
            damage /= 2;
        }

        // ThornsPower.BeforeDamageReceived runs after damage modifiers, before block
        // and HP are spent. Do not recheck the dealer's HP after this hook: the current
        // damage command continues even when the retaliation kills its dealer.
        ApplyPlayerThorns(enemy, state, rng);
        int absorbed = Math.Min(state.PlayerBlock, damage);
        state.PlayerBlock -= absorbed;
        int unblocked = damage - absorbed;

        // `DieForYouPower.ModifyUnblockedDamageTarget`: a living Osty becomes the target of
        // the UNBLOCKED remainder of a powered attack aimed at its owner. The block is the
        // player's and is spent above -- only what got through is redirected.
        //
        // Osty is a sponge with a capacity, NOT a shield. `CreatureCmd` deals the redirected
        // damage to the pet and then, because the target changed, deals that result's
        // OverkillDamage -- the part beyond what Osty had left -- to the original target
        // after all. So a big enough hit still reaches the player for the excess, and every
        // player-side consequence below runs on that excess rather than on the whole blow.
        if (unblocked > 0 && state.OstyHp > 0)
        {
            int toOsty = Math.Min(unblocked, state.OstyHp);
            Effects.CardEffects.DamageOsty(state, toOsty);
            unblocked -= toOsty;
        }

        // What reaches the player goes through their AfterOsty HP-loss modifiers (Tungsten
        // Rod, Beating Remnant, then Buffer) and `LoseHpInternal`, which also dispatches
        // `AfterCurrentHpChanged`. `lost` is the result's UnblockedDamage from here on:
        // actual HP lost, clamped, no overkill.
        var hit = Effects.CardEffects.LosePlayerHpInternal(
            state,
            Effects.CardEffects.ModifyPlayerHpLoss(state, unblocked)
        );
        int lost = hit.HpLost;

        // `CreatureCmd` runs the attacker's `AfterDamageGiven` next, then the receiver's
        // `AfterDamageReceived`, then `Kill`.
        //
        // PaperCutsPower.AfterDamageGiven: the player loses max HP when this creature
        // lands UNBLOCKED damage on them. It used to fire on the scroll's BUFF instead,
        // which is both the wrong trigger and the wrong branch.
        //
        // This is the ONLY site. A second one fired a flat helping per ATTACK from the
        // Attack-intent block -- the old call, left behind when the trigger was moved here
        // -- so a scroll that chewed cost the player three helpings instead of two and one
        // that chomped two instead of one. Whole turns came out at 10 max HP where the
        // game took 6.
        // Thorns can kill the dealer in BeforeDamageReceived. The current hit still
        // lands, but its removed powers are no longer in IterateHookListeners when
        // AfterDamageGiven runs (CombatState.Contains requires an attached owner).
        if (lost > 0 && enemy.Hp > 0)
        {
            int paperCuts = BuffSystem.Get(enemy.Buffs, BuffId.PaperCuts);
            if (paperCuts > 0)
            {
                // No hook: `CreatureCmd.SetMaxHp` does not dispatch
                // `AfterCurrentHpChanged`, so a shrinking maximum does not re-ask Red
                // Skull's question even though it moves the threshold.
                state.PlayerMaxHp = Math.Max(1, state.PlayerMaxHp - paperCuts);
                state.PlayerHp = Math.Min(state.PlayerHp, state.PlayerMaxHp);
            }
        }

        // ImbalancedPower.AfterDamageGiven, which only the Bowlbug Rock carries: an
        // attack of its own that block swallowed WHOLE knocks it off balance, and it
        // spends the following turn dizzy. `damage > 0` matters -- a zeroed attack was
        // not blocked, it was nothing.
        if (enemy.DefId == KE.BowlbugRock && damage > 0 && unblocked == 0)
        {
            enemy.OffBalance = true;
        }

        // `AfterDamageReceived` only when the hit did not kill the player, fully blocked
        // hits included. The player's powers come before their relics.
        if (hit.ReceiverHooksEligible(state))
        {
            // TheGambitPower.AfterDamageReceived: any unblocked powered attack removes the
            // power and calls `CreatureCmd.Kill` on the owner -- a normal kill, which takes
            // the remaining HP through `AfterCurrentHpChanged` and which Fairy in a Bottle
            // and Lizard Tail may still refuse.
            if (lost > 0 && BuffSystem.Get(state.PlayerBuffs, BuffId.TheGambitPower) > 0)
            {
                BuffSystem.Remove(state.PlayerBuffs, BuffId.TheGambitPower);
                state.PlayerHp = 0;
                Effects.RelicEffects.ApplyAfterPlayerHpChanged(state);
                CombatEngine.PlayerIsDead(state);
            }

            // A nested Gambit kill deactivates the owner's later hook listeners unless
            // a preventer revived them. Original CombatState.Contains rechecks this
            // between listeners, including powers as well as relics.
            if (state.PlayerHp > 0)
            {
                ApplyPlayerReflect(enemy, state, damage - unblocked, rng);
                ApplyPlayerFlameBarrier(enemy, state, rng);
            }

            // An enemy attack lands on the ENEMY side turn, which `state.PlayerTurn`
            // already says -- so Demon Tongue reads it there rather than being told.
            Effects.RelicEffects.ApplyAfterPlayerDamageReceived(state, lost, unblockable: false);
        }

        // `Kill` for a fatal hit: Fairy, then Lizard Tail. A multi-hit intent lands its
        // later hits on whoever this leaves standing.
        CombatEngine.PlayerIsDead(state);

        if (lost > 0 && triggerSuck)
        {
            TriggerSuck(enemy);
        }

        return lost > 0;
    }

    /// <summary>
    /// <c>FlameBarrierPower.AfterDamageReceived</c>: the attacker takes <c>Amount</c>
    /// unpowered damage back for every instance of damage the player receives from a
    /// powered attack.
    /// </summary>
    /// <remarks>
    /// Three things about that hook, each of which this used to get wrong. It fires PER
    /// HIT, so a multi-hit attack pays for every swing. It fires whether or not block
    /// absorbed the hit -- <c>CreatureCmd</c> guards <c>AfterCurrentHpChanged</c> on
    /// <c>UnblockedDamage > 0</c> and pointedly does NOT guard this one. And it is skipped
    /// when the hit killed its target (<c>!WasTargetKilled || !IsDead</c>), so a player who
    /// dies to the blow does not retaliate -- not even one Fairy in a Bottle or Lizard Tail
    /// then revives, because <c>Kill</c> runs after the dispatch. The caller gates on that;
    /// the HP guard here covers a Gambit kill earlier in the same dispatch.
    ///
    /// <para>
    /// It lived in the attack branch's generic tail, past eighteen <c>break</c>s, so it
    /// answered only single-hit attacks by monsters with no special case: zero retaliation
    /// against a multi-hit intent, and none against a Snapping Jaxfruit or a Sludge Spinner
    /// either. Here it is per hit for every attack, the way Thorns already was.
    /// </para>
    /// </remarks>
    private static void ApplyPlayerFlameBarrier(EnemyState enemy, CombatState state, Random rng)
    {
        int flameBarrier = BuffSystem.Get(state.PlayerBuffs, BuffId.FlameBarrier);
        if (flameBarrier <= 0 || state.PlayerHp <= 0 || enemy.Hp <= 0)
        {
            return;
        }

        Effects.CardEffects.DealUnpoweredDamage(state, enemy, flameBarrier);
        ResolveRetaliationDeath(enemy, state, rng);
    }

    private static void TriggerSuck(EnemyState enemy, int hitCount = 1)
    {
        int suck = BuffSystem.Get(enemy.Buffs, BuffId.Suck);
        if (suck > 0 && hitCount > 0)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Strength, suck * hitCount);
        }
    }

    /// <summary>
    /// `ReflectPower.AfterDamageReceived`: a POWERED attack whose damage was BLOCKED sends
    /// `result.BlockedDamage` back at the dealer as Unpowered damage. Not Thorns -- Thorns
    /// is a flat point per hit whatever happened, and this pays exactly what the block
    /// stopped, so a fully-absorbed 20 comes back as 20 and an unblocked one as nothing.
    /// </summary>
    private static void ApplyPlayerReflect(
        EnemyState enemy,
        CombatState state,
        int blocked,
        Random rng
    )
    {
        if (blocked <= 0 || enemy.Hp <= 0 || BuffSystem.Get(state.PlayerBuffs, BuffId.Reflect) <= 0)
        {
            return;
        }

        int spikes = BuffSystem.CapIncomingDamage(blocked, enemy.Buffs);
        int absorbed = Math.Min(enemy.Block, spikes);
        enemy.Block -= absorbed;
        enemy.Hp = Math.Max(0, enemy.Hp - (spikes - absorbed));
        ResolveRetaliationDeath(enemy, state, rng);
    }

    private static void ApplyPlayerThorns(EnemyState enemy, CombatState state, Random rng)
    {
        int thorns = BuffSystem.Get(state.PlayerBuffs, BuffId.Thorns);
        if (thorns <= 0 || state.PlayerHp <= 0 || enemy.Hp <= 0)
        {
            return;
        }

        Effects.CardEffects.DealUnpoweredDamage(state, enemy, thorns);
        ResolveRetaliationDeath(enemy, state, rng);
    }

    private static void ResolveRetaliationDeath(EnemyState enemy, CombatState state, Random rng)
    {
        // CreatureCmd.Damage awaits Kill/AfterDeath inside each retaliation hook.
        // In particular, Thorns completes this before the current hit spends block;
        // Ravenous must stun later actors before they get their turn. Keep the
        // original reference: death hooks may remove or replace roster entries.
        if (enemy.Hp <= 0 && !enemy.DeathHooksHandled)
            CombatEngine.ResolveAwaitedEnemyDeaths(state, [enemy], rng);
    }

    /// <summary>
    /// <c>ThieveryPower.Steal</c>: <c>Min(Amount, Gold)</c> off a target that is
    /// <c>!IsDead</c> and holds any.
    /// </summary>
    /// <remarks>
    /// The dead check is the whole of what was missing. A Gremlin Merc's move attacks and
    /// THEN steals, so on the blow that kills the player the game takes nothing and the
    /// emulator robbed the corpse — twenty gold that only shows up in the run's final
    /// snapshot, where it is easy to read as a rounding difference rather than a rule.
    /// </remarks>
    private static void StealGremlinMercGold(EnemyState enemy, CombatState state)
    {
        // A retaliatory kill clears the original Merc's powers before its attack
        // returns. Native death dispatch is later, so its stale Thievery cannot act.
        if (state.PlayerHp <= 0 || enemy.Hp <= 0)
        {
            return;
        }

        int amount = Math.Min(BuffSystem.Get(enemy.Buffs, BuffId.Thievery), state.PlayerGold);
        if (amount <= 0)
        {
            return;
        }

        state.PlayerGold -= amount;
        enemy.StolenGold += amount;
    }

    /// <summary>
    /// `ThieveryMove`'s steal: the whole draw pile and discard pile together, narrowed to
    /// the FIRST of four rarity tiers that matches anything, then one taken at random.
    /// </summary>
    /// <remarks>
    /// Taking the top of the draw pile was wrong three ways at once. It ignored the
    /// tiers, so it would take a Basic over the Uncommon the Hopper is actually after; it
    /// never looked in the discard, which the game pools with the draw pile; and it spent
    /// no draw, where the game rolls `Rng.CombatCardGeneration.NextItem` -- so every later
    /// card generation in the fight was off by one. Against a starter deck all three
    /// cards are Basic, so only the third tier matches and the visible symptom is just a
    /// different card going missing: the emulator lost the Bash the game kept.
    ///
    /// The order is draw pile then discard pile, which is what
    /// `CardPile.GetCards(player, PileType.Draw, PileType.Discard)` yields, and the index
    /// the roll returns is an index into that.
    /// </remarks>
    private static void StealDrawOrDiscardCard(
        CombatState state,
        Random rng,
        EnemyState? thief = null
    )
    {
        var candidates = new List<(bool FromDraw, int Index)>();
        for (int i = 0; i < state.DrawPile.Count; i++)
        {
            if (!state.TracksRunDeck || state.DrawPile[i].DeckOrigin is not null)
                candidates.Add((true, i));
        }

        for (int i = 0; i < state.DiscardPile.Count; i++)
        {
            if (!state.TracksRunDeck || state.DiscardPile[i].DeckOrigin is not null)
                candidates.Add((false, i));
        }

        if (candidates.Count == 0)
        {
            return;
        }

        CardInstance Card((bool FromDraw, int Index) c) =>
            c.FromDraw ? state.DrawPile[c.Index] : state.DiscardPile[c.Index];
        bool NotImbued((bool FromDraw, int Index) c) => Card(c).Enchantment != Enchantment.Imbued;
        CardRarity Rarity((bool FromDraw, int Index) c) =>
            GeneratedData.Cards.Get(Card(c).DefId).Rarity;

        Func<(bool FromDraw, int Index), bool>[] tiers =
        [
            c => NotImbued(c) && Rarity(c) == CardRarity.Uncommon,
            c =>
                NotImbued(c)
                && Rarity(c) is CardRarity.Common or CardRarity.Rare or CardRarity.Event,
            c => NotImbued(c) && Rarity(c) is CardRarity.Basic or CardRarity.Quest,
            c => Rarity(c) == CardRarity.Ancient || !NotImbued(c),
        ];

        foreach (var tier in tiers)
        {
            var matched = candidates.Where(tier).ToList();
            if (matched.Count > 0)
            {
                candidates = matched;
                break;
            }
        }

        var selection = state.CardGenerationRng ?? rng;
        var picked = candidates[selection.Next(candidates.Count)];
        var stolen = Card(picked);
        if (picked.FromDraw)
        {
            state.RemoveFromDrawPileAt(picked.Index);
        }
        else
        {
            state.DiscardPile.RemoveAt(picked.Index);
        }

        // `ThieveryMove` builds a `SwipePower` around the card it took and applies it to
        // the thief. The card was simply dropped here, so killing the Hopper never gave it
        // back -- the whole reason the fight is worth winning rather than surviving.
        if (thief is not null)
        {
            thief.StolenCard = stolen;
            BuffSystem.Apply(thief.Buffs, BuffId.Swipe, 1);
            if (stolen.DeckOrigin is { } origin)
                state.StolenFromDeck.Add(origin);
        }
    }

    /// <summary>
    /// ShriekPower.AfterDamageReceived: an unblocked hit that leaves the Terror Eel at or
    /// below its threshold stuns it, and the turn after the stun it performs TERROR —
    /// which lands Vulnerable 99 on the player. The power is removed as it fires, so this
    /// happens once a combat. Nothing else in act 1 can be reached only by hurting a
    /// monster, which is why this went unmodelled: a capture that plays no cards never
    /// scratches a 150 HP elite.
    /// </summary>
    internal static void TriggerShriekIfWounded(EnemyState enemy)
    {
        int threshold = BuffSystem.Get(enemy.Buffs, BuffId.Shriek);
        if (threshold <= 0 || enemy.Hp > threshold || enemy.Hp <= 0)
        {
            return;
        }

        BuffSystem.Remove(enemy.Buffs, BuffId.Shriek);
        BuffSystem.Apply(enemy.Buffs, BuffId.TerrorQueued, 1);
        BuffSystem.Apply(enemy.Buffs, BuffId.Stunned, 1);
        // CreatureCmd.Stun changes the move immediately, so the intent the player is
        // looking at becomes the stun rather than whatever was announced.
        enemy.CurrentIntent = new Intent(IntentType.Unknown, 0);
    }

    /// <summary>
    /// `PlowPower.AfterDamageReceived`: an unblocked hit that leaves the Ceremonial Beast
    /// at or below its threshold strips ALL of its Strength -- the permanent power and
    /// every temporary one -- and stuns it. The power goes as it fires, so this is once a
    /// combat, and it is the answer to a Beast that has been building Strength all fight.
    /// </summary>
    /// <remarks>
    /// The same shape as Shriek's, and it was applied and never read: the Beast took the
    /// power on its Plow move and nothing anywhere asked about it again, so dropping it low
    /// did nothing and its Strength kept climbing.
    ///
    /// `PowerCmd.Remove&lt;StrengthPower&gt;` REMOVES the power rather than subtracting from
    /// it, so a Beast at negative Strength comes back to zero rather than further down.
    /// </remarks>
    internal static void TriggerPlowIfWounded(EnemyState enemy)
    {
        int threshold = BuffSystem.Get(enemy.Buffs, BuffId.Plow);
        if (threshold <= 0 || enemy.Hp > threshold || enemy.Hp <= 0)
        {
            return;
        }

        BuffSystem.Remove(enemy.Buffs, BuffId.Plow);
        BuffSystem.Remove(enemy.Buffs, BuffId.Strength);
        BuffSystem.Remove(enemy.Buffs, BuffId.TemporaryStrength);
        BuffSystem.Apply(enemy.Buffs, BuffId.Stunned, 1);

        // `CreatureCmd.Stun(owner, StunnedMove, BeastCryState)`: the stun is a TURN, and the
        // Beast comes out of it into the cry loop rather than back to plowing. Parking the
        // index one BELOW the base leaves the end-of-turn increment to land on BEAST_CRY.
        if (enemy.DefId == KE.CeremonialBeast)
        {
            enemy.MoveIndex = BeastCryLoopBase - 1;
        }
        // As with Shriek: the stun replaces the announced intent rather than following it.
        enemy.CurrentIntent = new Intent(IntentType.Unknown, 0);
    }

    /// <summary>
    /// `AsleepPower.AfterDamageReceived`: ANY unblocked hit wakes the Lagavulin Matriarch
    /// at once -- the Plating comes off, she is stunned onto her WakeUpMove, and the sleep
    /// is gone. Not a countdown like the Slumbering Beetle's: one point of damage ends it
    /// however many sleeping turns were left.
    /// </summary>
    /// <remarks>
    /// The emulator ticked `Asleep` down over turns and had no answer to being hit at all,
    /// so hitting her did not wake her and she kept her Plating for the fight. Slumber --
    /// the same idea on the Beetle -- was modelled; this twin was not, which is the same
    /// shape as PossessSpeed having a twin nobody had written.
    ///
    /// `CreatureCmd.Stun(owner, WakeUpMove, "SLASH_MOVE")`: the wake turn does nothing and
    /// SLASH follows it. MoveIndex parks at zero so the increment at the end of her turn
    /// starts the four-move ring on SLASH, which is where the sleeping branch leaves it too.
    /// </remarks>
    internal static void WakeMatriarchIfStruck(EnemyState enemy)
    {
        if (
            enemy.DefId != KE.LagavulinMatriarch
            || BuffSystem.Get(enemy.Buffs, BuffId.Asleep) <= 0
            || enemy.Hp <= 0
        )
        {
            return;
        }

        BuffSystem.Remove(enemy.Buffs, BuffId.Asleep);
        BuffSystem.Remove(enemy.Buffs, BuffId.Plating);
        BuffSystem.Apply(enemy.Buffs, BuffId.Stunned, 1);
        enemy.MoveIndex = 0;
        enemy.CurrentIntent = new Intent(IntentType.Unknown, 0);
    }

    /// <summary>
    /// Where the Ceremonial Beast's SECOND loop starts counting. A sentinel rather than a
    /// flag because the move index is what the intent table already reads, and the two loops
    /// never interleave -- once `PlowPower` has fired the Beast never plows again.
    /// </summary>
    private const int BeastCryLoopBase = 1000;

    /// <summary>TwoTailedRatsNormal.Slots: five, three of them taken at the start.</summary>
    private const int RatSlots = 5;

    /// <summary>WaterfallGiant.PressureGunIncrease: what each firing adds.</summary>
    private const int PressureGunIncrease = 5;

    private static void AddStatus(CombatState state, int cardId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            state.DiscardPile.Add(new CardInstance(cardId, false));
        }
    }

    private static void AddStatusToHand(CombatState state, int cardId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            state.Hand.Add(new CardInstance(cardId, false));
        }
    }

    private static void AddMechaKnightBurns(CombatState state)
    {
        // CardPileCmd rejects a dead recipient and combat already ending, including
        // a last enemy killed by retaliation. The attack's death/revival hooks have
        // already run. A dead attacker alone is not the combat-ending condition.
        if (state.PlayerHp <= 0 || CombatEngine.NoPrimaryEnemyLeft(state))
            return;

        for (int i = 0; i < 4; i++)
        {
            var burn = new CardInstance(ST.Burn, false);
            if (state.Hand.Count >= CardEffects.MaxCardsInHand)
                state.DiscardPile.Add(burn);
            else
                state.Hand.Add(burn);
        }
        // Source creator:null: do not invoke player-attributed generation rewards.
    }

    /// <summary>
    /// `Ovicopter.LayEggsMove`: three times, `Slots.LastOrDefault(s => Enemies.All(c =>
    /// c.SlotName != s))`, then `CreatureCmd.Add&lt;ToughEgg&gt;` into that slot and
    /// MinionPower 1. `OvicopterNormal.Slots` is `[egg1..egg5, ovicopter]`.
    /// </summary>
    /// <remarks>
    /// `CreatureCmd.Add` ends in `CombatManager.AddCreature`, which calls
    /// `SortEnemiesBySlotName` for a creature with a slot -- so the roster is SLOT order,
    /// and that order is turn order and the order every "each enemy" effect walks. The
    /// first clutch fills egg5, egg4, egg3 and reads newest-first. A later clutch refills
    /// whatever the dead left: with the egg3 egg alive it takes egg5, egg4 and egg2, and
    /// the survivor stands between them (ABB52XX step-0390). Placing by "in front of the
    /// first egg" was only right while every egg of the last clutch was dead.
    ///
    /// Slots are read off the LIVING, as for the Fabricator: the game has removed a dead
    /// egg, and a corpse the emulator still holds does not keep its place. The corpse is
    /// dropped when its slot is refilled, so the roster never outgrows the six slots the
    /// observation reports.
    /// </remarks>
    private static void SummonToughEggs(EnemyState enemy, CombatState state, Random rng)
    {
        // The layer IS the Ovicopter, which `GenerateMonsters` puts in the `ovicopter` slot.
        if (enemy.Slot < 0)
            enemy.Slot = CombatFactory.OvicopterOwnSlot;
        for (int i = 0; i < 3; i++)
        {
            int slot = -1;
            for (int candidate = CombatFactory.OvicopterOwnSlot; candidate >= 0; candidate--)
            {
                if (!state.Enemies.Any(other => other.Hp > 0 && other.Slot == candidate))
                {
                    slot = candidate;
                    break;
                }
            }

            if (slot < 0)
            {
                // `LastOrDefault` found nothing, and LAY_EGGS adds nothing for that egg.
                continue;
            }

            // Not stunned — Ovicopter adds eggs with a plain CreatureCmd.Add, and the
            // egg's own first move is its hatch, so the delay is already modelled once.
            var egg = CreateEnemy(
                KE.ToughEgg,
                rng,
                new Intent(IntentType.Unknown, 0),
                state: state
            );
            BuffSystem.Apply(egg.Buffs, BuffId.Minion, 1);
            var spawned = Effects.RelicEffects.Spawned(state, egg);
            spawned.Slot = slot;

            state.Enemies.RemoveAll(other =>
                other.Hp <= 0 && other.DefId == KE.ToughEgg && other.Slot == slot
            );
            // A creature with no slot (there should be none here) sorts last.
            int at = state.Enemies.FindIndex(other => other.Slot > slot || other.Slot < 0);
            if (at < 0)
                state.Enemies.Add(spawned);
            else
                state.Enemies.Insert(at, spawned);
        }
    }

    internal static void SummonFabricatorBots(
        EnemyState enemy,
        CombatState state,
        Random rng,
        bool includeDefensive
    )
    {
        // `SpawnBot` is `RunRng.MonsterAi.NextItem(options)` -- ONE draw per bot, off the
        // AI stream. Rolling the type off the combat rng instead left MonsterAi two draws
        // short after a single FABRICATE, and every branch the Fabricator rolled from then
        // on read the wrong number. Found by comparing stream POSITIONS rather than
        // outcomes: `MonsterAi 3/5` at turn 3 says which stream and which turn, where the
        // intents only said a later roll came out differently.
        var ai = state.AiRng ?? rng;
        int insertIndex = state.Enemies.IndexOf(enemy);

        // `SpawnBot` filters its pool with `m != _lastSpawned` and then takes
        // `NextItem` -- which draws even from a one-element list, so the draw count does
        // not change, only the choice. `_lastSpawned` is ONE field shared by both pools,
        // so a defensive spawn can shut out a defensive option on the next fabricate.
        int PickBot(int first, int second)
        {
            int[] pool =
                enemy.LastSpawnedDefId == first ? [second]
                : enemy.LastSpawnedDefId == second ? [first]
                : [first, second];
            int chosen = pool[ai.Next(pool.Length)];
            enemy.LastSpawnedDefId = chosen;
            return chosen;
        }

        // LIVING, not the raw roster. The emulator keeps a corpse in `Enemies` at 0 HP so
        // an agent's observation has stable indices, and counting those blocked the spawn
        // once enough bots had died -- the Fabricator announced FABRICATING_STRIKE and no
        // bot arrived. The game has no such count: `GetNextSlot` asks which SLOT no LIVING
        // creature stands in, and a corpse holds no slot.
        int living = state.Enemies.Count(other => other.Hp > 0);
        if (includeDefensive && living < 6)
        {
            int defensive = PickBot(KE.Guardbot, KE.Noisebot);
            // NOT stunned. `SpawnBot` is a plain `CreatureCmd.Add` and a `MinionPower`,
            // with no `CreatureCmd.Stun` anywhere -- a fabricated bot simply misses the
            // turn it arrives in, because the enemy loop iterates a snapshot taken before
            // the Fabricator acted. Stunning it as well made it miss the NEXT turn too, so
            // every bot sat out its first real turn: the Guardbot's block never reached its
            // Fabricator, the Stabbot's Frail never landed, and the Noisebot never made its
            // noise. The snapshot already models the rule; this was the same rule twice.
            var bot = CreateEnemy(
                defensive,
                rng,
                BotIntent(defensive, state.AscensionLevel),
                state: state
            );
            BuffSystem.Apply(bot.Buffs, BuffId.Minion, 1);
            InsertFabricatorBot(state, enemy, bot);
        }
        if (state.Enemies.Count(other => other.Hp > 0) < 6)
        {
            int aggro = PickBot(KE.Zapbot, KE.Stabbot);
            var bot = CreateEnemy(aggro, rng, BotIntent(aggro, state.AscensionLevel), state: state);
            BuffSystem.Apply(bot.Buffs, BuffId.Minion, 1);
            InsertFabricatorBot(state, enemy, bot);
        }
    }

    /// <summary>
    /// Put a fabricated bot in the slot the encounter would give it.
    /// </summary>
    /// <remarks>
    /// `FabricatorNormal.Slots` is `bot1, bot2, fabricator, bot3, bot4` and
    /// `GetNextSlot` takes the FIRST free one -- so the first two bots stand in front of
    /// the Fabricator and every one after that stands BEHIND it. The emulator inserted
    /// every bot ahead of the Fabricator, which puts the roster in an order the game
    /// never has: `[19, 21, 23, 149]` where the game reads `[21, 19, 149, 23]`. Intents
    /// are compared by position, and the player's "first enemy" is a position too.
    /// </remarks>
    /// <summary>
    /// Puts a spawned bot in the slot `Encounter.GetNextSlot` would give it: the FIRST
    /// slot of `[bot1, bot2, fabricator, bot3, bot4]` with no living creature standing in
    /// it, and then into the roster in slot order.
    /// </summary>
    /// <remarks>
    /// The old rule counted list POSITIONS -- insert ahead of the Fabricator while it sat
    /// below index 2, append after that. That is right for a fight where nothing dies, and
    /// wrong the moment one does: a dead bot frees its slot and the game refills it, in
    /// FRONT, while the emulator went on appending to the back. Glory's first sweep showed
    /// it as a roster in a different order with the Strength on the wrong creature.
    ///
    /// Slots are read off the LIVING, because that is what `GetNextSlot` asks -- a corpse
    /// the emulator keeps at 0 HP for its observation slot does not hold its place in the
    /// game.
    /// </remarks>
    private static void InsertFabricatorBot(
        CombatState state,
        EnemyState fabricator,
        EnemyState bot
    )
    {
        int slot = -1;
        for (int candidate = 0; candidate < CombatFactory.FabricatorSlotCount; candidate++)
        {
            if (!state.Enemies.Any(other => other.Hp > 0 && other.Slot == candidate))
            {
                slot = candidate;
                break;
            }
        }

        var spawned = Effects.RelicEffects.Spawned(state, bot);
        if (slot < 0)
        {
            // Every slot taken. The game's `GetNextSlot` returns null and `SpawnBot` adds
            // nothing, so neither does this.
            return;
        }

        spawned.Slot = slot;

        // Into the roster in slot order, so the list reads front-to-back the way the
        // game's does. A creature with no slot (there should be none here) sorts last.
        int at = state.Enemies.FindIndex(other => other.Slot > slot);
        if (at < 0)
        {
            state.Enemies.Add(spawned);
            return;
        }

        state.Enemies.Insert(at, spawned);
    }

    /// <summary>
    /// The opening intent a summoned bot arrives holding. Takes the ascension level
    /// because two of the four carry a DeadlyEnemies pair, and this is the second place
    /// their numbers are written down — the audit reads `case KE.X:` arms and switch
    /// expressions alike now, which is how these two were found.
    /// </summary>
    private static Intent BotIntent(int defId, int ascension) =>
        defId switch
        {
            // GuardMove gives every Fabricator 15 block, a flat amount.
            KE.Guardbot => new Intent(IntentType.Defend, 15),
            // NOISE_MOVE: StatusIntent(2).
            KE.Noisebot => new Intent(IntentType.Debuff, 2),
            // ZapDamage
            KE.Zapbot => new Intent(
                IntentType.Attack,
                Ascension.Value(ascension, Ascension.DeadlyEnemies, 15, 14)
            ),
            // StabDamage. STAB_MOVE announces as an Attack, not as the Frail it also
            // applies.
            KE.Stabbot => new Intent(
                IntentType.Attack,
                Ascension.Value(ascension, Ascension.DeadlyEnemies, 12, 11)
            ),
            _ => new Intent(IntentType.Unknown, 0),
        };

    /// <summary>
    /// CURSE_OF_KNOWLEDGE: two curses offered, and the player picks one.
    /// </summary>
    /// <remarks>
    /// <c>ChooseCurse</c> builds the pair from <c>_curseOfKnowledgeSets[counter]</c> —
    /// Disintegration against MindRot, then Sloth, then WasteAway — and overwrites
    /// Disintegration's amount from <c>_disintegrationDamageValues[counter]</c>, 6 then 7
    /// then 8. The chosen card's <c>OnChosen</c> applies its power; nothing joins a pile.
    ///
    /// The emulator used to apply a flat Disintegration 6 and offer nothing, which chose
    /// for the player AND took the wrong curse twice out of three — the same defect the
    /// seven run-level policy sites had, in combat.
    ///
    /// The screen opens during the ENEMY's turn and the player answers it on their next
    /// action, which is what <c>PendingSelection</c> already does for every other
    /// mid-play choice.
    /// </remarks>
    /// <summary>
    /// Which of the Knowledge Demon's four moves a move index lands on.
    /// </summary>
    /// <remarks>
    /// Shared by the intent and its riders ON PURPOSE. PONDER heals and takes Strength,
    /// and that rider used to live in the BUFF handler because the emulator announced
    /// PONDER as a buff; retyping it to the attack it really is stopped the rider firing
    /// at all, and the demon stopped growing. Two places deriving the same phase from
    /// the move index is how they drift.
    /// </remarks>
    /// <summary>
    /// The Fake Merchant: SWIPE first, then two RandomBranchStates it moves between.
    /// </summary>
    /// <remarks>
    /// The emulator had a bare `MoveIndex switch` with no wrap, so from its fourth move
    /// on it enraged every turn forever — E100's shape a third time, in an act-1 event
    /// fight nothing had ever walked.
    ///
    /// The real machine has two branch sets. Everything returns to RAND_MOVE
    /// (swipe / spew / throw / enrage), EXCEPT throw, which returns to RAND_ATTACK_MOVE
    /// (swipe / spew / throw) — so it cannot enrage straight after throwing a relic. All
    /// branches are CannotRepeat, and ENRAGE additionally carries a COOLDOWN of three
    /// moves, which is a different rule: weight zero while it appears in the last three
    /// logged moves, not merely the last one.
    /// </remarks>
    private static Intent FakeMerchantIntent(EnemyState enemy, Random rng, int ascension)
    {
        // SwipeDamage; SPEW_COINS is MultiAttackIntent(2, 8) at a flat 2; ThrowRelicDamage
        // with a Frail rider; ENRAGE_MOVE's Strength.
        Intent[] branches =
        [
            new Intent(
                IntentType.Attack,
                Ascension.Value(ascension, Ascension.DeadlyEnemies, 15, 13)
            ),
            new Intent(IntentType.Attack, 2, Hits: 8),
            new Intent(
                IntentType.Attack,
                Ascension.Value(ascension, Ascension.DeadlyEnemies, 10, 9)
            ),
            new Intent(IntentType.Buff, 2),
        ];

        if (enemy.MoveIndex == 0)
        {
            enemy.LastBranch = 0;
            return branches[0];
        }

        if (enemy.BranchCooldown > 0)
        {
            enemy.BranchCooldown--;
        }

        // RAND_ATTACK_MOVE after a throw, RAND_MOVE otherwise -- and enrage is out while
        // it is cooling.
        bool enrageOffered = enemy.LastBranch != 2 && enemy.BranchCooldown == 0;
        int[] offered = enrageOffered ? [0, 1, 2, 3] : [0, 1, 2];
        var eligible = offered.Where(index => index != enemy.LastBranch).ToArray();
        int chosen = eligible[rng.Next(eligible.Length)];

        enemy.LastBranch = chosen;
        if (chosen == 3)
        {
            // Weight zero until three MOVES have passed, which is this one plus two more.
            enemy.BranchCooldown = 3;
        }

        return branches[chosen];
    }

    private static int KnowledgeDemonPhase(int moveIndex) =>
        moveIndex < 12 ? moveIndex % 4 : 1 + ((moveIndex - 12) % 3);

    private static void OpenCurseOfKnowledge(EnemyState enemy, CombatState state)
    {
        // Curses land on the demon's first move and every fourth after it, so the cast
        // index is the move index over four -- clamped, because the branch stops sending
        // it back to CURSE once it has cast three.
        int cast = Math.Clamp(
            enemy.MoveIndex / 4,
            0,
            Run.RunConstants.CurseOfKnowledgePairs.Length - 1
        );
        state.PendingSelection = new PendingCardSelection
        {
            Kind = CardSelectionKind.CurseOfKnowledge,
            Candidates = [0, 1],
            GeneratedCandidates = [ST.Disintegration, Run.RunConstants.CurseOfKnowledgePairs[cast]],
            SourceCardDefId = ST.Disintegration,
            Amount = Run.RunConstants.DisintegrationDamageValues[cast],
        };
    }

    /// <summary>
    /// <c>BurrowedPower.AfterBlockBroken</c>: the Tunneler is stunned out of its burrow.
    /// </summary>
    /// <remarks>
    /// `CreatureCmd.Stun(owner, StillDizzyMove, "BITE_MOVE")` — a turn spent dizzy and
    /// then back to the start of its table — and `AfterRemoved` takes the rest of the
    /// block with it. Breaking the burrow is the ONLY exit from BELOW_MOVE, which follows
    /// up to itself, so without this the emulator's Tunneler could never be interrupted.
    /// </remarks>
    public static void BreakBurrowIfBlockGone(EnemyState enemy)
    {
        if (BuffSystem.Get(enemy.Buffs, BuffId.Burrowed) <= 0 || enemy.Block > 0)
        {
            return;
        }

        BuffSystem.Remove(enemy.Buffs, BuffId.Burrowed);
        enemy.Block = 0;
        BuffSystem.Apply(enemy.Buffs, BuffId.Stunned, 1);
        // The stunned turn increments MoveIndex on its way past, so -1 lands on BITE.
        enemy.MoveIndex = -1;
    }

    /// <summary>
    /// `Hook.ShouldCreatureBeRemovedFromCombatAfterDeath`: a creature that dies leaves the
    /// combat unless a power vetoes it. Unanimous consent -- the default is REMOVE, and any
    /// listener returning false keeps the creature in the roster.
    /// </summary>
    /// <remarks>
    /// Five powers veto for their owner, and each is a creature that is not finished:
    /// `ReattachPower` (a Decimillipede segment reattaches), `IllusionPower` (an illusion
    /// revives), `AdaptablePower` (the Test Subject comes back in a bigger form),
    /// `SteamEruptionPower` (the Waterfall Giant is still exploding) and `PainfulStabsPower`
    /// (the Test Subject's later forms).
    ///
    /// `DieForYouPower` vetoes too and is deliberately absent: it belongs to Osty, a
    /// player-side pet, and never sits in `state.Enemies`.
    /// </remarks>
    internal static bool ShouldRemoveOnDeath(EnemyState enemy) =>
        !BuffSystem.Has(enemy.Buffs, BuffId.Reattach)
        && !BuffSystem.Has(enemy.Buffs, BuffId.Illusion)
        && !BuffSystem.Has(enemy.Buffs, BuffId.Adaptable)
        && !BuffSystem.Has(enemy.Buffs, BuffId.SteamEruption)
        && !BuffSystem.Has(enemy.Buffs, BuffId.PainfulStabs);

    /// <summary>
    /// `Hook.ShouldAllowHitting`: whether a card or effect may target this creature.
    /// </summary>
    /// <remarks>
    /// Also unanimous consent, and a DIFFERENT question from whether the creature is still
    /// in the fight. The Waterfall Giant stays AND is targetable while it explodes --
    /// `SteamEruptionPower` overrides removal and says nothing about hitting. The Test
    /// Subject stays and is NOT targetable between forms, because `AdaptablePower` answers
    /// `!IsReviving`. `ReattachPower` and `IllusionPower` answer the same way.
    ///
    /// So the rule is the REVIVING state, not zero HP: `BuffId.Reviving` is the emulator's
    /// marker for exactly the window those three powers refuse hits in.
    /// </remarks>
    internal static bool IsTargetable(EnemyState enemy) =>
        BuffSystem.Get(enemy.Buffs, BuffId.Reviving) <= 0
        && (enemy.Hp > 0 || BuffSystem.Has(enemy.Buffs, BuffId.SteamEruption));

    /// <summary>
    /// `SlumberPower` reaching zero runs `WakeUpMove`, which REMOVES the beetle's
    /// `PlatingPower` outright. Called from both decrement paths -- turn end and unblocked
    /// damage-triggered stunned move. Damage itself only queues that move; the
    /// plating stays until it executes.
    /// </summary>
    internal static void WakeSlumberingBeetleIfDone(EnemyState enemy)
    {
        if (
            enemy.DefId == KE.SlumberingBeetle
            && BuffSystem.Get(enemy.Buffs, BuffId.Slumber) <= 0
            && BuffSystem.Get(enemy.Buffs, BuffId.Plating) > 0
        )
        {
            BuffSystem.Remove(enemy.Buffs, BuffId.Plating);
        }
    }

    /// <summary>`SlumberingBeetle`'s ROLL_OUT: the Strength it takes with every roll.</summary>
    private const int BeetleRolloutStrength = 2;

    /// <summary>
    /// `SpinyToad`'s PROTRUDING_SPIKES puts up this much Thorns and SPIKE_EXPLOSION takes
    /// exactly it back down again.
    /// </summary>
    private const int ToadSpikeThorns = 5;

    /// <summary>`LouseProgenitor._growStrength`: what CURL_AND_GROW adds.</summary>
    private const int LouseCurlStrength = 5;

    /// <summary>`LouseProgenitor._webFrail`: WEB_CANNON's Frail, and only its.</summary>
    private const int LouseWebFrail = 2;

    /// <summary>
    /// The Scroll of Biting: CHOMP -> MORE_TEETH -> CHEW, then a branch back into it.
    /// </summary>
    /// <remarks>
    /// Not a three-cycle, which is what `MoveIndex % 3` made it. CHEW's follow-up is a
    /// RandomBranchState over CHOMP (CannotRepeat) and CHEW (at most twice running) —
    /// so a scroll can chew twice in a row and then must chomp, and the chain restarts
    /// only through CHOMP. The opening is `StarterMoveIdx % 3`, which numbers the moves
    /// 0/1/2 = CHOMP/CHEW/MORE_TEETH — a different order again from the chain.
    ///
    /// The current move is carried in <c>LastBranch</c> rather than derived from
    /// MoveIndex, because after the branch there is no arithmetic that gives it.
    /// </remarks>
    private static Intent ScrollOfBitingIntent(EnemyState enemy, Random rng, int ascension)
    {
        const int chomp = 0;
        const int moreTeeth = 1;
        const int chew = 2;

        // ChompDamage; CHEW is MultiAttackIntent(ChewDamage, 2), folded into 12 before;
        // MORE_TEETH's Strength.
        Intent[] moves =
        [
            new Intent(
                IntentType.Attack,
                Ascension.Value(ascension, Ascension.DeadlyEnemies, 16, 14)
            ),
            new Intent(IntentType.Buff, 2),
            new Intent(
                IntentType.Attack,
                Ascension.Value(ascension, Ascension.DeadlyEnemies, 6, 5),
                Hits: 2
            ),
        ];

        if (enemy.MoveIndex == 0)
        {
            // StarterMoveIdx, seeded into MoveIndex by the encounter: 0/1/2 is
            // CHOMP/CHEW/MORE_TEETH.
            enemy.LastBranch = enemy.StarterMove switch
            {
                0 => chomp,
                1 => chew,
                _ => moreTeeth,
            };
            enemy.RepeatStreak = 1;
            return moves[enemy.LastBranch];
        }

        int next;
        if (enemy.LastBranch == chomp)
        {
            next = moreTeeth;
        }
        else if (enemy.LastBranch == moreTeeth)
        {
            next = chew;
        }
        else
        {
            // The RandomBranchState, reached only from CHEW. CHOMP is always eligible
            // here (the last move was a chew); CHEW is out once it has run twice.
            //
            // `GetNextState` DRAWS EVERY TIME IT IS REACHED, including when only one
            // branch is eligible: it sums the weights, takes `rng.NextFloat(max)` and
            // walks the branches subtracting. Returning CHOMP without drawing left the
            // MonsterAi stream one short from the first spent chew onwards, which desyncs
            // every later branch in the fight -- the other scroll's, not just this one's.
            // That is why one scroll of a pair would run the right chain and the other
            // would not, and why its CHOMP announced 14 where the game said 16: the
            // MORE_TEETH that grants the Strength had landed on a different turn.
            //
            // Both weights are 1, so with CHEW eligible the boundary is the same one
            // `rng.Next(2)` drew and only the spent case moves. The draw itself costs the
            // same single MegaRandom step either way -- `Next(maxValue)` is
            // `(long)(NextDouble() * maxValue)` -- so this is about WHETHER it happens.
            double chewWeight = enemy.RepeatStreak >= 2 ? 0d : 1d;
            double roll = rng.NextDouble() * (1d + chewWeight);
            next = roll - 1d <= 0d ? chomp : chew;
        }

        enemy.RepeatStreak = next == enemy.LastBranch ? enemy.RepeatStreak + 1 : 1;
        enemy.LastBranch = next;
        return moves[next];
    }

    /// <summary>
    /// `IllusionPower.AfterApplied` applies `MinionPower` to whatever just gained the
    /// Illusion, unless it already has one. So an illusion is ALWAYS a minion, and the two
    /// go on together wherever Illusion is granted.
    /// </summary>
    /// <remarks>
    /// The Minion half is what stops `Feed`, `HandOfGreed`, `TheHunt` and
    /// `BookRepairKnife` paying their fatal bonus for killing the thing -- `TriggersFatal`
    /// already reads `BuffId.Minion` and was reading it correctly; the buff was simply
    /// never put on. So the emulator paid max HP and gold for killing an illusion and the
    /// game does not.
    ///
    /// Found by the sweep's first buff-aware run, on a row where block, HP, intents and
    /// pile sizes all agreed and only the power list differed.
    /// </remarks>
    private static void ApplyIllusion(EnemyState creature)
    {
        BuffSystem.Apply(creature.Buffs, BuffId.Illusion, 1);
        if (!BuffSystem.Has(creature.Buffs, BuffId.Minion))
        {
            BuffSystem.Apply(creature.Buffs, BuffId.Minion, 1);
        }
    }

    private static void SummonParafright(EnemyState enemy, CombatState state)
    {
        // Not stunned: TheObscura adds it with a plain CreatureCmd.Add.
        var parafright = CreateEnemy(
            KE.Parafright,
            new Random(0),
            new Intent(IntentType.Attack, 17),
            state: state
        );
        ApplyIllusion(parafright);
        state.Enemies.Insert(
            state.Enemies.IndexOf(enemy),
            Effects.RelicEffects.Spawned(state, parafright)
        );
    }

    /// <summary>
    /// The highest of <c>TwoTailedRatsNormal.Slots</c> no living creature stands in, or
    /// -1 when the pack has filled them.
    /// </summary>
    private static int LastFreeRatSlot(CombatState state)
    {
        for (int slot = RatSlots - 1; slot >= 0; slot--)
        {
            if (!state.Enemies.Any(e => e.Hp > 0 && e.Slot == slot))
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Puts a slotted creature into the roster in slot order.</summary>
    private static void InsertBySlot(CombatState state, EnemyState enemy)
    {
        int at = state.Enemies.FindIndex(other => other.Slot > enemy.Slot);
        if (at < 0)
        {
            state.Enemies.Add(enemy);
            return;
        }

        state.Enemies.Insert(at, enemy);
    }

    private static bool CanRatSummon(EnemyState enemy, Random rng)
    {
        return BuffSystem.Get(enemy.Buffs, BuffId.SummonCooldown) <= 0
            && BuffSystem.Get(enemy.Buffs, BuffId.BackupCount) < 3
            && rng.NextDouble() < 0.75;
    }

    private static void TickRatSummonCooldown(EnemyState enemy)
    {
        if (enemy.DefId != KE.TwoTailedRat)
        {
            return;
        }

        int cooldown = BuffSystem.Get(enemy.Buffs, BuffId.SummonCooldown);
        if (cooldown > 0)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.SummonCooldown, -1);
        }
    }

    private static void SummonRatBackup(EnemyState enemy, CombatState state, Random rng)
    {
        // CallForBackup: Slots.LastOrDefault(s => no creature holds s). The slots a dead
        // rat leaves behind are free again, so the answer moves during the fight -- and
        // CanSummon() fails outright when there is none.
        int slot = LastFreeRatSlot(state);
        if (slot < 0)
        {
            return;
        }

        // A summoned rat has StarterMoveIndex == -1, so its machine starts ON the branch
        // rather than on a move — which means its very first selection rolls, where a rat
        // that began the fight does not.
        // Not stunned: CallForBackup adds the rat with a plain CreatureCmd.Add, and the
        // enemy phase already iterates a snapshot of the roster — so the newcomer sits out
        // the phase that summoned it and then fights normally. A stun on top of that costs
        // it a second turn, which is a summoned rat's whole first attack.
        var summoned = CreateEnemy(
            KE.TwoTailedRat,
            rng,
            new Intent(IntentType.Unknown, 0),
            state: state
        );
        summoned.StartsOnBranch = true;
        summoned.Slot = slot;
        // The roster reads in slot order. With the three starters alive the last free
        // slot is "second" and the newcomer leads the pack, which is what this used to
        // assume unconditionally; once the rat in "fifth" has died the last free slot is
        // "fifth" and the newcomer joins the BACK, so a hardcoded front insert named a
        // different creature than the game on every target index from then on.
        InsertBySlot(state, Effects.RelicEffects.Spawned(state, summoned));

        int nextBackupCount =
            state
                .Enemies.Where(e => e.DefId == KE.TwoTailedRat)
                .Select(e => BuffSystem.Get(e.Buffs, BuffId.BackupCount))
                .DefaultIfEmpty(0)
                .Max() + 1;
        foreach (var rat in state.Enemies.Where(e => e.DefId == KE.TwoTailedRat))
        {
            int current = BuffSystem.Get(rat.Buffs, BuffId.BackupCount);
            BuffSystem.Apply(rat.Buffs, BuffId.BackupCount, nextBackupCount - current);
        }
    }

    /// <summary>
    /// A summon's HP, rolled the way CombatState.CreateCreature rolls it:
    /// SetUniqueMonsterHpValue on the Niche stream, over the monster's band minus the
    /// MaxHp of the creatures ALREADY ON THAT SIDE. Note the exclusion is the roster as
    /// it stands and not every value the combat has ever used, so a dead enemy's HP is
    /// available again. When the band is exhausted the game rolls it flat instead.
    /// </summary>
    internal static int RollSummonedHp(int min, int max, CombatState? state, Random rng)
    {
        var niche = state?.NicheHpRng;
        if (niche == null)
        {
            return rng.Next(min, max + 1);
        }

        var available = Enumerable.Range(min, max - min + 1).ToHashSet();
        available.ExceptWith(
            state!.Enemies.Where(other => other.Hp > 0).Select(other => other.MaxHp)
        );
        return available.Count == 0
            ? niche.Next(min, max + 1)
            : available.ElementAt(niche.Next(0, available.Count));
    }

    private static EnemyState CreateEnemy(
        int defId,
        Random rng,
        Intent intent,
        bool stunned = false,
        int? ascension = null,
        // The combat, for the HP roll. Without it the roll falls back to the combat
        // stream, which is not where the game takes it from.
        CombatState? state = null
    )
    {
        var def = GeneratedData.Enemies.Get(defId);
        // Creature construction reads the active run's ascension. Summoners
        // supplying a combat must not silently use the standalone default level.
        var band = def.HpBand(ascension ?? state?.AscensionLevel ?? Ascension.DefaultLevel);
        int hp = RollSummonedHp(band.Min, band.Max, state, rng);
        var enemy = new EnemyState
        {
            DefId = defId,
            Hp = hp,
            MaxHp = hp,
            CurrentIntent = intent,
            Buffs = [],
        };
        if (stunned)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Stunned, 1);
        }

        if (defId == KE.ToughEgg)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.Hatch, 1);
        }

        if (defId == KE.Zapbot)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.HighVoltage, 2);
        }

        if (defId == KE.TwoTailedRat)
        {
            BuffSystem.Apply(enemy.Buffs, BuffId.SummonCooldown, 2);
        }

        return enemy;
    }
}

// Known enemy def IDs (from Generated/Enemies.g.cs).
public static class KE
{
    public const int CalcifiedCultist = 14;
    public const int Aeonglass = 1;
    public const int Architect = 2;
    public const int Axebot = 4;
    public const int BattleFriendV1 = 10004;
    public const int BattleFriendV2 = 10005;
    public const int BattleFriendV3 = 10006;
    public const int Chomper = 16;
    public const int CorpseSlug = 17;
    public const int CeremonialBeast = 15;
    public const int Crusher = 19;
    public const int AxeRubyRaider = 5;
    public const int AssassinRubyRaider = 3;
    public const int BowlbugEgg = 6;
    public const int BowlbugNectar = 7;
    public const int BowlbugRock = 8;
    public const int BowlbugSilk = 9;
    public const int BruteRubyRaider = 10;
    public const int BygoneEffigy = 11;
    public const int Byrdonis = 12;
    public const int Byrdpip = 13;
    public const int DecimillipedeSegment = 22;
    public const int Entomancer = 24;
    public const int CrossbowRubyRaider = 18;
    public const int DampCultist = 21;
    public const int DevotedSculptor = 23;
    public const int EyeWithTeeth = 26;
    public const int Exoskeleton = 25;
    public const int Fabricator = 27;
    public const int FakeMerchant = 10003;
    public const int Flyconid = 30;
    public const int FlailKnight = 29;
    public const int MysteriousKnight = 110;
    public const int Fogmog = 31;
    public const int FossilStalker = 32;
    public const int FrogKnight = 33;
    public const int GasBomb = 35;
    public const int GlobeHead = 36;
    public const int Guardbot = 38;
    public const int FuzzyWurmCrawler = 34;
    public const int FatGremlin = 28;
    public const int GremlinMerc = 37;
    public const int HauntedShip = 39;
    public const int HunterKiller = 40;
    public const int Inklet = 42;
    public const int InfestedPrism = 41;
    public const int KinFollower = 43;
    public const int KinPriest = 44;
    public const int KnowledgeDemon = 45;
    public const int LagavulinMatriarch = 46;
    public const int LivingFog = 49;
    public const int LivingShield = 50;
    public const int LouseProgenitor = 51;
    public const int LeafSlimeM = 47;
    public const int LeafSlimeS = 48;
    public const int Mawler = 53;
    public const int MagiKnight = 52;
    public const int MechaKnight = 54;
    public const int Myte = 55;
    public const int Nibbit = 56;
    public const int Noisebot = 57;
    public const int Osty = 58;
    public const int Ovicopter = 59;
    public const int OwlMagistrate = 60;
    public const int PaelsLegion = 61;
    public const int Parafright = 62;
    public const int PhantasmalGardener = 63;
    public const int PhrogParasite = 64;
    public const int Queen = 66;
    public const int Rocket = 67;
    public const int PunchConstruct = 65;
    public const int ScrollOfBiting = 68;
    public const int Seapunk = 69;
    public const int SewerClam = 70;
    public const int ShrinkerBeetle = 71;
    public const int SkulkingColony = 72;
    public const int SneakyGremlin = 78;
    public const int SnappingJaxfruit = 77;
    public const int SpinyToad = 82;
    public const int SlitheringStrangler = 74;
    public const int SludgeSpinner = 75;
    public const int SlumberingBeetle = 76;
    public const int SlimedBerserker = 73;
    public const int SoulNexus = 80;
    public const int SoulFysh = 79;
    public const int SpectralKnight = 81;
    public const int Stabbot = 83;
    public const int TerrorEel = 84;
    public const int Toadpole = 93;
    public const int TorchHeadAmalgam = 94;
    public const int ThievingHopper = 92;
    public const int TheAdversaryMkOne = 85;
    public const int TheAdversaryMkThree = 86;
    public const int TheAdversaryMkTwo = 87;
    public const int TheForgotten = 88;
    public const int TheInsatiable = 89;
    public const int TheLost = 90;
    public const int TheObscura = 91;
    public const int TrackerRubyRaider = 96;
    public const int ToughEgg = 95;
    public const int TwigSlimeM = 99;
    public const int TwigSlimeS = 100;
    public const int Tunneler = 97;
    public const int TwoTailedRat = 101;
    public const int TurretOperator = 98;
    public const int TestSubject = 10007;
    public const int CubexConstruct = 20;
    public const int VineShambler = 103;
    public const int Vantom = 102;
    public const int WaterfallGiant = 104;
    public const int Wriggler = 105;
    public const int Zapbot = 106;
}
