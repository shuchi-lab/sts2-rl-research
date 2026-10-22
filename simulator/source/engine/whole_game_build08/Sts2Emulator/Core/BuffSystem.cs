namespace Sts2Emulator.Core;

public static class BuffSystem
{
    public static void Apply(List<BuffState> buffs, BuffId id, int magnitude)
    {
        if (magnitude == 0)
        {
            return;
        }

        if (magnitude > 0 && IsDebuff(id))
        {
            if (TryConsumeArtifact(buffs))
            {
                return;
            }
        }

        int idx = IndexOf(buffs, id);
        if (idx >= 0)
        {
            int newVal = buffs[idx].Magnitude + magnitude;
            if (newVal == 0)
            {
                buffs.RemoveAt(idx);
            }
            else
            {
                buffs[idx] = buffs[idx] with { Magnitude = newVal };
            }
        }
        else
        {
            buffs.Add(new BuffState(id, magnitude));
        }
    }

    /// <summary>
    /// Index of <paramref name="id" /> in <paramref name="buffs" />, or -1.
    /// </summary>
    /// <remarks>
    /// A hand-written loop rather than <c>FindIndex(b =&gt; b.Id == id)</c>, because that
    /// lambda CAPTURES `id` — so it allocates a closure and a delegate on every call, and
    /// this is the most-called function in the emulator (242 call sites, several of them
    /// per point of damage). It was ~3KB of garbage per attacking enemy, which is most of
    /// what made the enemy phase the hottest path in a step. Buff lists are a handful of
    /// entries; the scan is cheaper than the allocation was.
    /// </remarks>
    private static int IndexOf(List<BuffState> buffs, BuffId id)
    {
        for (int i = 0; i < buffs.Count; i++)
        {
            if (buffs[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    public static int Get(List<BuffState> buffs, BuffId id)
    {
        int idx = IndexOf(buffs, id);
        return idx >= 0 ? buffs[idx].Magnitude : 0;
    }

    public static bool Has(List<BuffState> buffs, BuffId id) => Get(buffs, id) > 0;

    public static void Remove(List<BuffState> buffs, BuffId id)
    {
        for (int i = buffs.Count - 1; i >= 0; i--)
        {
            if (buffs[i].Id == id)
            {
                buffs.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// The powers the game declares <c>AllowNegative</c> on a <c>Counter</c> stack. A
    /// NEGATIVE application of one of these is a DEBUFF -- `PowerModel.GetTypeForAmount`
    /// says so outright -- which is what puts it in Artifact's way.
    /// </summary>
    /// <remarks>
    /// Strength, Dexterity, Focus, Shrink and Shriek are the whole list in
    /// `Models.Powers`. Shrink and Shriek are printed Debuffs either way; the first three
    /// are Buffs whose sign decides.
    /// </remarks>
    public static bool AllowsNegative(BuffId id) =>
        id is BuffId.Strength or BuffId.Dexterity or BuffId.Focus or BuffId.Shrink;

    /// <summary>
    /// Whether applying <paramref name="magnitude"/> of <paramref name="id"/> is a DEBUFF
    /// landing -- the question Artifact answers, and not the same as "is this a debuff".
    /// </summary>
    /// <remarks>
    /// Nine points of Strength off an enemy is a debuff; nine points ON is not, and both
    /// are `BuffId.Strength`. `Apply` only asks the positive half of this because most of
    /// its negative callers are BOOKKEEPING -- a temporary power handing back what it
    /// took, which the game does by removing the power rather than by applying a debuff,
    /// and which Artifact must not eat.
    /// </remarks>
    public static bool IsDebuffApplication(BuffId id, int magnitude) =>
        (magnitude > 0 && IsDebuff(id)) || (magnitude < 0 && AllowsNegative(id));

    public static bool TryConsumeArtifact(List<BuffState> buffs)
    {
        int artifact = Get(buffs, BuffId.Artifact);
        if (artifact <= 0)
        {
            return false;
        }

        int artifactIdx = IndexOf(buffs, BuffId.Artifact);
        if (artifact == 1)
        {
            buffs.RemoveAt(artifactIdx);
        }
        else
        {
            buffs[artifactIdx] = buffs[artifactIdx] with { Magnitude = artifact - 1 };
        }

        return true;
    }

    /// <summary>
    /// The `TemporaryFocusPower` subclasses, each its own power instance.
    /// </summary>
    public static bool IsTemporaryFocus(BuffId id) =>
        id
            is BuffId.HyperbeamFocusDown
                or BuffId.Hotfix
                or BuffId.FocusedStrike
                or BuffId.Synchronize;

    /// <summary>
    /// `IllusionPower.ShouldPowerBeRemovedOnDeath`: which of an illusion's powers
    /// `CreatureCmd.Kill` strips through `Creature.RemoveAllPowersAfterDeath`. Only a
    /// DEBUFF goes, and not one that is an `ITemporaryPower`; every buff, the Illusion
    /// itself included, stays on through the revive.
    /// </summary>
    /// <remarks>
    /// The test is the power's declared `Type`, not `TypeForCurrentAmount`, so Strength
    /// taken below zero is still a Buff here and stays. That is the generated `IsBuff`
    /// flag, not <see cref="IsDebuff" />, which is Artifact's narrower hand list. Ids
    /// with no power behind them (Reviving, SlowCount) are the emulator's own
    /// bookkeeping and are not debuffs.
    /// </remarks>
    public static bool IsRemovedFromDyingIllusion(BuffId id)
    {
        if (
            id is BuffId.TemporaryStrength or BuffId.TemporaryDexterity or BuffId.TemporaryFocus
            || IsTemporaryFocus(id)
        )
        {
            return false;
        }

        return GeneratedData.Powers.FindId($"{id}Power") is int found
            && !GeneratedData.Powers.Get(found).IsBuff;
    }

    /// <summary>`TemporaryFocusPower.Sign`: -1 for the `IsPositive => false` subclass.</summary>
    private static int TemporaryFocusSign(BuffId id) => id == BuffId.HyperbeamFocusDown ? -1 : 1;

    /// <summary>
    /// `PowerCmd.Apply&lt;FocusPower&gt;` as the game runs it: Focus `AllowNegative` on a
    /// Counter, so a NEGATIVE amount is a Debuff by `GetTypeForAmount` and Artifact blocks
    /// it, spending one charge. A positive amount is a Buff and always lands.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Apply" />, whose negative callers are mostly bookkeeping
    /// that the game does not route through Artifact. Only the temporary-Focus paths below,
    /// which the game does route through `PowerCmd.Apply`, use this.
    /// </remarks>
    private static void ApplyFocusAsPower(List<BuffState> buffs, int amount)
    {
        if (amount == 0)
        {
            return;
        }

        if (amount < 0 && TryConsumeArtifact(buffs))
        {
            return;
        }

        Apply(buffs, BuffId.Focus, amount);
    }

    /// <summary>
    /// `PowerCmd.Apply&lt;T&gt;` for a `TemporaryFocusPower` subclass <paramref name="id" />.
    /// The caller is responsible for the `CombatManager.IsEnding` early return.
    /// </summary>
    /// <remarks>
    /// New instance (`PowerCmd.Apply(power, ...)`): an amount of 0 returns before any hook.
    /// Otherwise `ModifyPowerAmountReceived` runs first; for HyperbeamFocusDown, whose Type
    /// is Debuff, Artifact zeroes it. `BeforeApplied` then applies `Sign * modifiedAmount`
    /// Focus -- nothing, when blocked -- and `ApplyInternal` returns at once for 0, so a
    /// blocked Hyperbeam leaves no marker. Artifact decrements afterwards. The first
    /// `AfterPowerAmountChanged` sees `amount == Amount` and does not apply Focus again.
    ///
    /// Existing instance (`ModifyAmount`): the same Artifact check on the offset -- for the
    /// Debuff subclass it fires even at offset 0, because `GetTypeForAmount(0)` is the
    /// declared Type. When the offset survives, `AfterPowerAmountChanged` applies
    /// `Sign * offset` Focus, since the new Amount differs from the offset.
    /// </remarks>
    public static void ApplyTemporaryFocus(List<BuffState> buffs, BuffId id, int amount)
    {
        int sign = TemporaryFocusSign(id);
        int idx = IndexOf(buffs, id);
        if (idx < 0 && amount == 0)
        {
            return;
        }

        // `TemporaryFocusPower.Type` is Debuff exactly when !IsPositive, and these powers do
        // not AllowNegative, so every non-negative amount of the negative subclass is a
        // Debuff landing. The card paths never pass a negative amount.
        if (sign < 0 && amount >= 0 && TryConsumeArtifact(buffs))
        {
            return;
        }

        if (amount == 0)
        {
            return;
        }

        if (idx < 0)
        {
            ApplyFocusAsPower(buffs, sign * amount);
            buffs.Add(new BuffState(id, amount));
            return;
        }

        int newAmount = buffs[idx].Magnitude + amount;
        buffs[idx] = buffs[idx] with { Magnitude = newAmount };
        if (amount != newAmount)
        {
            ApplyFocusAsPower(buffs, sign * amount);
        }

        // `ShouldRemoveDueToAmount`: not AllowNegative, so removed at or below zero.
        if (newAmount <= 0)
        {
            Remove(buffs, id);
        }
    }

    /// <summary>
    /// `TemporaryFocusPower.AfterSideTurnEnd` for every subclass the owner holds, in the
    /// order they were applied -- the order the hook listeners iterate the powers.
    /// </summary>
    /// <remarks>
    /// Each one is removed and then applies `-Sign * Amount` Focus through the ordinary
    /// `PowerCmd.Apply`, so a POSITIVE source's `-Amount` restoration is a Focus debuff
    /// that Artifact blocks, and that source's Focus is then kept. Hyperbeam's `+Amount`
    /// is a Buff and always lands. Every instance is restored on its own, so which one an
    /// Artifact charge meets depends on this order and not on a net.
    /// </remarks>
    public static void ExpireTemporaryFocus(List<BuffState> buffs)
    {
        var expiring = buffs.Where(buff => IsTemporaryFocus(buff.Id)).ToArray();
        foreach (var buff in expiring)
        {
            Remove(buffs, buff.Id);
            ApplyFocusAsPower(buffs, -TemporaryFocusSign(buff.Id) * buff.Magnitude);
        }
    }

    // Called at end of turn for the owning side (tick debuffs down by 1).
    /// <summary>The duration debuffs, as a snapshot to compare a later stack against.</summary>
    public static List<BuffState> DurationDebuffSnapshot(List<BuffState> buffs) =>
        [.. buffs.Where(buff => IsDurationDebuff(buff.Id))];

    private static bool IsDurationDebuff(BuffId id) =>
        id is BuffId.Vulnerable or BuffId.Weak or BuffId.Frail or BuffId.Shrink;

    /// <param name="atRoundStart">
    /// What the owner held when the round began, for the player's one-tick grace: a stack
    /// bigger now than it was then was applied during this round, and the game's
    /// SkipNextDurationTick means this tick passes it over. Omit for enemies, which get
    /// no grace.
    /// </param>
    public static void TickEndOfTurn(List<BuffState> buffs, List<BuffState>? atRoundStart = null)
    {
        for (int i = buffs.Count - 1; i >= 0; i--)
        {
            var b = buffs[i];
            switch (b.Id)
            {
                // SlumberPower.AfterSideTurnEnd, which ticks for its OWNER's side only --
                // the beetle is an enemy, and this runs for the enemies. Its other
                // decrement, on unblocked damage, is in DealDamageToEnemy.
                // IntangiblePower.AfterSideTurnEnd decrements at the end of the ENEMY
                // side turn whoever owns it, which is this moment. Listed apart from the
                // duration DEBUFFS below because it is a Buff: PowerCmd.Apply's
                // skip-a-tick grace is only given to a debuff landing on a player-side
                // creature, so an Intangible the player gains this turn ticks tonight.
                // TaintedPower.AfterSideTurnEnd REMOVES itself outright rather than
                // decrementing, so a round's worth of Skills is paid for once.
                // `ReboundPower.AfterSideTurnEnd` removes itself outright too, so an
                // unspent Rebound does not carry into the next turn -- the card is "the
                // next card THIS turn", not the next card ever.
                case BuffId.Tainted:
                case BuffId.Rebound:
                    buffs.RemoveAt(i);
                    break;

                case BuffId.Intangible:
                    buffs[i] = b with { Magnitude = b.Magnitude - 1 };
                    if (buffs[i].Magnitude <= 0)
                    {
                        buffs.RemoveAt(i);
                    }

                    break;

                case BuffId.Slumber:
                case BuffId.Vulnerable:
                case BuffId.Weak:
                case BuffId.Frail:
                case BuffId.Shrink:
                    if (b.Magnitude < 0)
                    {
                        break; // negative = permanent (e.g. ShrinkerBeetle Shrink)
                    }

                    if (atRoundStart != null && WasAppliedThisRound(b, atRoundStart))
                    {
                        break;
                    }

                    buffs[i] = b with { Magnitude = b.Magnitude - 1 };
                    if (buffs[i].Magnitude <= 0)
                    {
                        buffs.RemoveAt(i);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Whether this debuff is new to the player this round, and so owed the one-tick
    /// grace. It is the POWER that carries `SkipNextDurationTick`, not the application:
    /// `PowerCmd.Apply` sets the flag on the model it creates, so a debuff that lands on
    /// a stack the player already had leaves the existing power's flag alone and that
    /// power ticks as usual.
    ///
    /// Live, at A8, the Two-Tailed Rats screech nearly every turn and the player's Frail
    /// reads 1, 1, 0, 1 — each new point cancelled by the same round's tick. Treating any
    /// increase as a skip made it climb instead, which is a point of block a turn.
    /// </summary>
    private static bool WasAppliedThisRound(BuffState buff, List<BuffState> atRoundStart)
    {
        return !atRoundStart.Any(other => other.Id == buff.Id);
    }

    /// <param name="weakDelta">
    /// `Hook.ModifyWeakMultiplier`, which Paper Krane answers with `-0.15` when its owner
    /// is the TARGET -- a Weak attacker hits them for 0.60 rather than 0.75.
    /// </param>
    /// <param name="vulnerableDelta">
    /// `Hook.ModifyVulnerableMultiplier`, which Paper Phrog answers with `+0.25` when the
    /// target is NOT its owner -- a Vulnerable enemy takes 1.75 rather than 1.5.
    /// </param>
    /// <remarks>
    /// The two deltas arrive as parameters rather than being read from relics here,
    /// because this function deliberately cannot see a `CombatState` -- it is called with
    /// two buff lists and nothing else, including from `Intent.AnnouncedDamage` where the
    /// caller may not have a combat at all. Each caller that CAN see the relics passes
    /// them, which is also what keeps the announced intent and the landed damage in step.
    /// </remarks>
    public static int IncomingDamage(
        int baseDamage,
        List<BuffState> attackerBuffs,
        List<BuffState> defenderBuffs,
        float cardMultiplier = 1f,
        float weakDelta = 0f,
        float vulnerableDelta = 0f
    ) =>
        (int)IncomingDamageUnrounded(
            baseDamage,
            attackerBuffs,
            defenderBuffs,
            (decimal)cardMultiplier,
            (decimal)weakDelta,
            (decimal)vulnerableDelta
        );

    public static decimal IncomingDamageUnrounded(
        decimal baseDamage,
        List<BuffState> attackerBuffs,
        List<BuffState> defenderBuffs,
        decimal cardMultiplier = 1m,
        decimal weakDelta = 0m,
        decimal vulnerableDelta = 0m
    )
    {
        // Everything in this function is the POWERED-ATTACK path, which is what lets the
        // two hooks below live here at all. `ValueProp.IsPoweredAttack` is `Move &&
        // !Unpowered` -- attack damage from Attack cards and from enemy creatures
        // attacking -- and the two callers are exactly those: EnemyAI.DealAttackDamage and
        // CardEffects.DealDamageToEnemy. Relic, potion, thorns and poison damage all go
        // through the `Unpowered` helpers instead and never arrive here.
        decimal dmg = baseDamage;
        dmg += Get(attackerBuffs, BuffId.Strength);
        dmg += Get(attackerBuffs, BuffId.Vigor);
        // TaintedPower.ModifyDamageAdditive, which the Infested Prism's Vital Spark stamps
        // on the player for every Skill they play.
        dmg += Get(defenderBuffs, BuffId.Tainted);
        if (Get(attackerBuffs, BuffId.Weak) > 0)
        {
            // `DebilitatePower.ModifyWeakMultiplier` is `amount - (1 - amount)` for its
            // OWNER's attacks, so a debilitated attacker's Weak lands at 0.5 instead of
            // 0.75. The amount is a duration and does not scale the doubling.
            dmg *= (Get(attackerBuffs, BuffId.Debilitate) > 0 ? 0.5m : 0.75m) + weakDelta;
        }

        if (Get(attackerBuffs, BuffId.Shrink) != 0)
        {
            dmg *= 0.70m; // negative = permanent
        }

        // `FlutterPower.ModifyDamageMultiplicative` returns DamageDecrease/100 = 0.5 for
        // its owner, and only for a POWERED attack -- which is the whole of this function,
        // so no extra guard is needed here. Thorns, poison and relic damage go through the
        // Unpowered helpers and never reach it, exactly as the game's prop check intends.
        if (Get(defenderBuffs, BuffId.Flutter) > 0)
        {
            dmg *= 0.5m;
        }

        // ColossusPower.ModifyDamageMultiplicative: a vulnerable dealer's powered
        // attack is halved. Share this with intent previews and apply it before caps.
        if (Get(defenderBuffs, BuffId.Colossus) > 0 && Get(attackerBuffs, BuffId.Vulnerable) > 0)
        {
            dmg *= 0.5m;
        }

        if (Get(defenderBuffs, BuffId.Vulnerable) > 0)
        {
            decimal mult = 1.5m + Get(attackerBuffs, BuffId.CrueltyPower) / 100m + vulnerableDelta;
            // `DebilitatePower.ModifyVulnerableMultiplier` is `amount + (amount - 1)` when
            // the target is its owner, which doubles the BONUS rather than the multiplier:
            // 1.5 becomes 2.0, and a Cruelty-raised 1.75 becomes 2.5.
            if (Get(defenderBuffs, BuffId.Debilitate) > 0)
            {
                mult += mult - 1m;
            }

            dmg *= mult;
        }

        // SurroundedPower.ModifyDamageMultiplicative, the Kaiser Crab's: an attack from
        // the half at the player's BACK lands at 1.5x. It belongs here rather than at the
        // point of damage because the game's readout shows the modified number --
        // AttackIntent.GetSingleDamage runs the move through Hook.ModifyDamage first --
        // and a live capture confirms it: the Crusher opens announcing 18 for a base 12.
        int facing = Get(defenderBuffs, BuffId.Surrounded);
        bool fromBehind =
            facing == Run.RunConstants.FacingRight
                ? Get(attackerBuffs, BuffId.BackAttackLeft) > 0
                : facing == Run.RunConstants.FacingLeft
                    && Get(attackerBuffs, BuffId.BackAttackRight) > 0;
        if (fromBehind)
        {
            dmg *= 1.5m;
        }

        // TrackingPower.ModifyDamageMultiplicative: a powered CARD attack against a target
        // that has Weak is multiplied by the power's own amount, so Tracking 2 is double
        // damage. It reads the DEFENDER's Weak and the ATTACKER's Tracking.
        int tracking = Get(attackerBuffs, BuffId.Tracking);
        if (tracking > 0 && Get(defenderBuffs, BuffId.Weak) > 0)
        {
            dmg *= tracking;
        }

        // DoubleDamagePower.ModifyDamageMultiplicative returns a flat `2m` for a powered
        // CARD attack by its owner. The amount is a STACK COUNT and not a multiplier --
        // two stacks are still double, and buy a second turn of it rather than quadruple
        // damage.
        //
        // The game also requires `cardSource != null`, which this function cannot see.
        // Player damage that is not from a card is Unpowered in the emulator and does not
        // reach here, so the two agree for every source that exists today -- the same
        // standing caveat Tracking above carries.
        if (Get(attackerBuffs, BuffId.DoubleDamage) > 0)
        {
            dmg *= 2;
        }

        // SoarPower.ModifyDamageMultiplicative: a flying Owl Magistrate takes half from a
        // POWERED attack, which is the only kind that reaches this function.
        if (Get(defenderBuffs, BuffId.Soar) > 0)
        {
            dmg *= 0.5m;
        }

        // Card-dependent listener multipliers (Lethality, Pen Nib, Gigantification,
        // Hang and Conqueror) follow all listener additives, including Strength.
        // Preserve the fraction for callers such as Thrash's absorbed-card preview.
        dmg *= cardMultiplier;

        // SlowPower is another multiplicative hook before the damage-cap pass.
        // Integer hit/readout callers round only after this decimal result returns.
        if (Get(defenderBuffs, BuffId.Slow) > 0)
        {
            dmg *= 1m + 0.1m * Get(defenderBuffs, BuffId.SlowCount);
        }

        dmg = Math.Max(0m, dmg);
        return Get(defenderBuffs, BuffId.Intangible) > 0 ? Math.Min(dmg, 1m) : dmg;
    }

    /// <summary>
    /// <c>IntangiblePower.ModifyDamageCap</c>: damage aimed at its owner is capped at 1.
    /// </summary>
    /// <remarks>
    /// The cap runs inside <c>Hook.ModifyDamage</c> under the <c>Cap</c> flag, and
    /// <c>ModifyDamageHookType.All</c> — which is what almost every back-end call passes —
    /// includes it. So this belongs with the additive and multiplicative modifiers rather
    /// than at the point of damage, and it reaches the READOUT as well as the blow:
    /// <c>AttackIntent.GetSingleDamage</c> runs the move through the same hook, so an
    /// intangible player is told the enemy will hit them for 1, not for thirty.
    ///
    /// Applied per HIT, which is where the game applies it: a two-hit attack against an
    /// intangible creature announces 2 and lands two ones.
    /// </remarks>
    public static int CapIncomingDamage(int damage, List<BuffState> defenderBuffs) =>
        Get(defenderBuffs, BuffId.Intangible) > 0 ? Math.Min(damage, 1) : damage;

    /// <summary>
    /// <c>IntangiblePower.ModifyHpLostAfterOsty</c>: any HP loss of 1 or more becomes 1.
    /// </summary>
    /// <remarks>
    /// The second half of Intangible, and the reason the power carries two hooks: the cap
    /// above governs the damage NUMBER — what block absorbs, what a preview shows — and
    /// this one is the backstop on HP itself. It is what covers HP lost by a route that is
    /// not an attack at all. Poison happens to be capped by the first hook rather than this
    /// one, since <c>PoisonPower</c> runs its own damage through
    /// <c>Hook.ModifyDamage(..., All, ...)</c>, but either way the answer is 1.
    /// </remarks>
    public static int CapHpLoss(int hpLoss, List<BuffState> defenderBuffs) =>
        Get(defenderBuffs, BuffId.Intangible) > 0 ? Math.Min(hpLoss, 1) : hpLoss;

    public static int IncomingBlock(int baseBlock, List<BuffState> buffs, bool isDefend = false) =>
        (int)IncomingBlockUnrounded(baseBlock, buffs, isDefend);

    // Card block may still have relic/power multipliers after Frail. Keep its
    // fractional part until the final gain, as Hook.ModifyBlock does.
    internal static decimal IncomingBlockUnrounded(
        int baseBlock,
        List<BuffState> buffs,
        bool isDefend = false
    )
    {
        decimal blk = baseBlock;
        if (Get(buffs, BuffId.NoBlock) > 0)
        {
            return 0;
        }

        // `Hook.ModifyBlock` runs every ADDITIVE contribution first and only then the
        // multiplicative ones. Fasten is `ModifyBlockAdditive` like Dexterity, so it has to
        // land before Frail rather than after it: added afterwards, a Frail Defend came out
        // as (base + Dex) * 0.75 + Fasten where the game pays (base + Dex + Fasten) * 0.75.
        blk += Get(buffs, BuffId.Dexterity);

        // `FastenPower.ModifyBlockAdditive` refuses a card that is not tagged Defend, which
        // is why the caller has to say which kind of block this is.
        if (isDefend)
        {
            blk += Get(buffs, BuffId.FastenPower);
        }

        // `FrailPower.ModifyBlockMultiplicative`, the only multiplicative one here.
        if (Get(buffs, BuffId.Frail) > 0)
        {
            blk *= 0.75m;
        }

        return Math.Max(0m, blk);
    }

    private static bool IsDebuff(BuffId id) =>
        id
            is BuffId.Vulnerable
                or BuffId.Weak
                or BuffId.Frail
                or BuffId.Poison
                // StranglePower.Type is Debuff; Artifact blocks the rider after its hit.
                or BuffId.Strangle
                or BuffId.Burn
                or BuffId.Shrink
                or BuffId.Tangled
                or BuffId.Constrict
                or BuffId.Smoggy
                or BuffId.NoBlock
                or BuffId.Doom;
}
