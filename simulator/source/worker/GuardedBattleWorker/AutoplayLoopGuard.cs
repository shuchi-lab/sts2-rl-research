using CombatWorker;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Effects;

namespace FullRunWorker;

/// <summary>
/// Experiment-lifecycle guard for ONE confirmed native combo whose resolution inside a single <c>RunEngine.Step</c>
/// exceeds any per-step budget: Pillage played under Hellraiser against a Waterfall Giant in its eruption phase
/// (HP reset to 999,999,999). The quality-20k n5 worker 0 stalled on it at t18058 d155. One Mac core needed ~370 s
/// (~6.5e7 card draws) before the giant died; on the loaded Windows host the step outlived the 900 s guard.
/// </summary>
/// <remarks>
/// <para>This is NOT a game-rule repair: the engine is read-only and its result (millions of reshuffles of the run's
/// own streams) cannot be reproduced without running that loop. The guard only refuses to start the step. It ends the run
/// as a visible <c>limit</c> (<c>battle_unbounded_autoplay_loop</c>) with evidence, never as a death or win, and
/// never removes an option from E or picks another action.</para>
/// <para>It is a pure read of the live combat state: no clone, no engine call, no RNG use. It fires only when every
/// condition below holds. Each condition is read off the engine source (Pillage → <c>CardEffects.DrawUntilNonAttack</c>,
/// <c>DrawCard</c> / <c>DrawOneCard</c>, the Hellraiser <c>AutoPlayNested</c>, <c>NoPrimaryEnemyLeft</c>). Together they leave
/// the loop no exit except the giant's death, and the giant's HP bounds the number of draws from below:</para>
/// <list type="number">
/// <item>The chosen option plays a plain (<see cref="Plain" />) Pillage from hand, the player holds Hellraiser, and
/// the player's HP is positive.</item>
/// <item>No draw veto: no NoDraw, and every relic is in <see cref="InertRelics" /> (Fiddle is not).</item>
/// <item>Every player power is in <see cref="InertPlayerBuffs" />, so no draw/play/damage hook can end the loop
/// and the player cannot be harmed during it.</item>
/// <item>Every enemy is a primary Waterfall Giant at eruption phase ≥ 1 with HP ≥ <see cref="SentinelHp" />, whose
/// powers are all in <see cref="InertEnemyBuffs" /> (no thorns-like reaction).</item>
/// <item>Draw + discard hold only <see cref="Plain" /> Attacks. Every Strike-named one is a
/// <see cref="RecurrentStrikes" /> card, which Hellraiser auto-plays back to discard (no Dupe, which leaves combat
/// instead); at least one is present.</item>
/// <item>Hand cap: (hand count − the played Pillage, which <c>PlayCard</c> removes before its effect) + every other
/// (non-recurrent) attack in draw + discard ≤ 9. Even if every one of them is drawn into hand, the hand never
/// reaches the 10-card cap. No Status/Curse is in hand.</item>
/// <item>The lower bound (giant HP+block − upper bound on Pillage's own hit) / (upper bound on one auto-play's
/// damage) exceeds <see cref="MaxLoopDraws" />.</item>
/// </list>
/// <para>Anything outside this class runs exactly as before. That includes a normal enemy, a possible non-attack
/// draw, attacks that would fill the hand, a draw veto, an unverified relic or power, and an unlisted Strike. If such
/// a state also runs away, the unchanged 900 s worker watchdog remains the backstop.</para>
/// </remarks>
public static class AutoplayLoopGuard
{
    /// <summary>The eruption phase's reset HP is 999,999,999; normal enemies are below 10^4.</summary>
    public const int SentinelHp = 100_000_000;

    /// <summary>Card draws one engine step may need before it counts as runaway (normal plays draw &lt;100).</summary>
    public const long MaxLoopDraws = 100_000;

    /// <summary>Hand size at which <c>DrawOneCard</c> stops (<c>CardEffects.MaxCardsInHand</c>).</summary>
    public const int MaxCardsInHand = 10;

    /// <summary>Every Strike-named card (the engine's <c>IsStrikeCard</c>: Name contains "Strike"), frozen build08.</summary>
    public static readonly HashSet<int> StrikeNamed =
        [5, 20, 44, 201, 281, 305, 310, 314, 349, 358, 412, 417, 421, 429, 445, 471, 472, 473, 474, 475, 519, 522];

    /// <summary>Strikes whose auto-play only deals damage (Pommel: + draws from the same pool) and lands in discard.</summary>
    public static readonly HashSet<int> RecurrentStrikes = [IC.StrikeIronclad, IC.PerfectedStrike, IC.PommelStrike, IC.TwinStrike];

    /// <summary>Relics verified to have no draw/play/damage/shuffle hook in this loop (t18058's set).</summary>
    public static readonly HashSet<int> InertRelics =
        [RunConstantsRelic.BurningBlood, RunConstantsRelic.FishingRod, RelicEffects.OddlySmoothStone, RelicEffects.UnsettlingLamp];

    public static readonly HashSet<BuffId> InertPlayerBuffs =
        [BuffId.Hellraiser, BuffId.Strength, BuffId.Dexterity, BuffId.Weak, BuffId.Frail, BuffId.Vulnerable];

    public static readonly HashSet<BuffId> InertEnemyBuffs =
        [BuffId.Strength, BuffId.Vulnerable, BuffId.Weak, BuffId.SteamEruption];

    /// <summary>
    /// Evidence when the chosen play is in a refused class; null otherwise (the step runs). Class A (the confirmed
    /// t18058 sentinel-HP class, unchanged) is checked first; class B (<see cref="CheckSteamEruptionRevive" />) only
    /// when class A does not fire.
    /// </summary>
    public static string? Check(CombatState s, Option o) => CheckSentinelHp(s, o) ?? CheckSteamEruptionRevive(s, o);

    /// <summary>Class A: the confirmed t18058 runaway class (unchanged logic, allowlists and evidence).</summary>
    private static string? CheckSentinelHp(CombatState s, Option o)
    {
        if (o.Kind != "play_card" || o.HandIndex < 0 || o.HandIndex >= s.Hand.Count)
            return null;
        var pillage = s.Hand[o.HandIndex];
        if (pillage.DefId != IC.Pillage || !Plain(pillage) || !BuffSystem.Has(s.PlayerBuffs, BuffId.Hellraiser) || s.PlayerHp <= 0)
            return null;
        if (BuffSystem.Has(s.PlayerBuffs, BuffId.NoDraw) || s.Relics.Any(r => !InertRelics.Contains(r.DefId)))
            return null;
        if (s.PlayerBuffs.Any(b => !InertPlayerBuffs.Contains(b.Id)))
            return null;
        if (s.Enemies.Count == 0 || s.Enemies.Any(e => e.DefId != KE.WaterfallGiant || e.SteamEruptionPhase < 1
                || e.Hp < SentinelHp || e.Buffs.Any(b => !InertEnemyBuffs.Contains(b.Id))))
            return null;
        var pool = s.DrawPile.Concat(s.DiscardPile).ToList();
        if (pool.Any(c => !Plain(c) || c.ModelType() != CardType.Attack))
            return null;
        if (pool.Any(c => StrikeNamed.Contains(c.DefId) && !RecurrentStrikes.Contains(c.DefId)))
            return null;
        int strikes = pool.Count(c => RecurrentStrikes.Contains(c.DefId));
        int others = pool.Count - strikes;
        // PlayCard takes Pillage out of hand before its effect (CombatEngine.PlayCard), so the loop starts with
        // Hand.Count - 1 cards; even if every other attack is drawn, the hand must stay below the 10-card cap.
        if (strikes == 0 || s.Hand.Count - 1 + others > MaxCardsInHand - 1)
            return null;
        if (s.Hand.Any(c => c.ModelType() is CardType.Status or CardType.Curse))
            return null;

        // Upper bound on one auto-play's damage (plain copies: no per-copy bonus): Perfected Strike's 6 + 3 per
        // Strike (every card counted) plus the largest other base (Pommel 10, Twin 7 x2), positive Strength,
        // x2 hits, x2 for Vulnerable (1.5). Pillage's own first hit (9 upgraded) is bounded the same way and
        // taken off the giant's HP before dividing.
        int cards = s.Hand.Count + pool.Count + s.ExhaustPile.Count + 1;
        int strength = Math.Max(0, BuffSystem.Get(s.PlayerBuffs, BuffId.Strength));
        decimal perPlay = 4m * (10 + 6 + 3m * cards + strength);
        decimal pillageHit = 2m * (9 + strength);
        long hp = s.Enemies.Sum(e => (long)e.Hp + e.Block);
        long lowerBound = (long)((hp - pillageHit) / perPlay);
        if (lowerBound <= MaxLoopDraws)
            return null;

        return $"Pillage (hand {o.HandIndex}) under Hellraiser against a Waterfall Giant eruption phase: the draw loop's only exit is the giant's death, "
            + $"at least {lowerBound} Strike auto-plays (HP+block {hp} / at most {perPlay} damage each; budget {MaxLoopDraws}) inside one engine step; not executed. "
            + $"enemies [{string.Join("; ", s.Enemies.Select(e => $"enemy {e.DefId} hp {e.Hp}/{e.MaxHp} block {e.Block} eruption_phase {e.SteamEruptionPhase} buffs [{string.Join(",", e.Buffs.Select(b => $"{b.Id}:{b.Magnitude}"))}]"))}]; "
            + $"player hp {s.PlayerHp} buffs [{string.Join(",", s.PlayerBuffs.Select(b => $"{b.Id}:{b.Magnitude}"))}]; "
            + $"hand [{string.Join(",", s.Hand.Select(c => c.DefId))}] draw [{string.Join(",", s.DrawPile.Select(c => c.DefId))}] "
            + $"discard [{string.Join(",", s.DiscardPile.Select(c => c.DefId))}] exhaust [{string.Join(",", s.ExhaustPile.Select(c => c.DefId))}] "
            + $"relics [{string.Join(",", s.Relics.Select(r => r.DefId))}]; recurrent strikes {strikes}, other attacks {others}";
    }

    // ------------------------------------------------------------------ class B (potential runaway)

    /// <summary>
    /// Class B relics: <see cref="InertRelics" /> plus only the relics of the t47872 reproduction, each read off the
    /// build08 engine source as having no draw, draw-veto, play, shuffle, death or combat-end hook in this loop:
    /// Neow's Talisman (pickup upgrade only, RunNonCombatEffects.cs:604-609), The Boot (HP-lost floor, RelicEffects.cs:
    /// 2288-2301), Strike Dummy (+3 Strike damage, RelicEffects.cs:2092-2101), Petrified Toad (combat-start potion,
    /// RelicEffects.cs:792-800), Pen Nib (doubles every tenth attack, RelicEffects.cs:2113-2192).
    /// </summary>
    public static readonly HashSet<int> SteamReviveInertRelics =
    [
        .. InertRelics, Sts2Emulator.Core.Run.RunConstants.RelicNeowsTalisman, RelicEffects.TheBoot, RelicEffects.StrikeDummy,
        RelicEffects.PetrifiedToad, RelicEffects.PenNib,
    ];

    /// <summary>
    /// Class B player powers: <see cref="InertPlayerBuffs" /> plus Inferno / InfernoSelfDamage (fire only at the player's
    /// turn start and on player HP loss: CombatEngine.cs:3134-3160, CardEffects.cs:6041, 10503-10515; nothing in this
    /// loop damages the player) and Juggling (CombatEngine.cs:390-407: at most its stack count of clones, once per
    /// turn, on the THIRD attack started this turn; counted in the hand bound below).
    /// </summary>
    public static readonly HashSet<BuffId> SteamReviveInertPlayerBuffs =
        [.. InertPlayerBuffs, BuffId.Inferno, BuffId.InfernoSelfDamage, BuffId.Juggling];

    /// <summary>
    /// The only non-Strike cards class B accepts in hand, draw and discard: plain Ironclad Defend (a Skill: block
    /// when played; no draw hook) and plain Bash (an Attack: damage + Vulnerable when played; no draw hook). Drawn,
    /// either only lands in hand (DrawOneCard, CardEffects.cs:2826-2915). Every other non-Strike card is outside class B.
    /// </summary>
    public static readonly HashSet<int> SteamRevivePlainNonStrikes = [IC.DefendIronclad, IC.Bash];

    /// <summary>Exhausted cards class B accepts besides plain pool cards: the reproduction's Forgotten Ritual and Not Yet
    /// (play effects only, CardEffects.cs:959, 996; no exhaust-pile reader names them).</summary>
    public static readonly HashSet<int> SteamReviveExhaustCards = [IC.ForgottenRitual, IC.NotYet];

    /// <summary>
    /// Class B: a POTENTIAL runaway, refused before execution (t47872 d157, n5-tiered 2026-10-07). A plain Pillage
    /// played under Hellraiser while every enemy is a Waterfall Giant that STILL holds SteamEruption.
    /// </summary>
    /// <remarks>
    /// <para>Why the loop can have no exit (engine source, build08): Pillage draws until the card DrawCard RETURNED is a
    /// non-Attack or the hand holds 10 (CardEffects.cs:610-612, 9737-9750). Hellraiser auto-plays every drawn
    /// Strike-named card inside that draw (2862-2863, 2903-2904), and Pommel Strike's own nested draw can take a
    /// non-Attack into hand without stopping Pillage. DrawCard returns null only once the player is dead or no primary
    /// enemy is left (2787-2797), and a giant holding SteamEruption keeps the fight open (CombatEngine.cs:5287-5302).
    /// Its in-step death restores 999,999,999 HP, clears DeathHooksHandled and keeps SteamEruption (5112-5126), so every
    /// later in-step death revives it again. SteamEruption leaves only on the giant's own turn (EnemyAI.cs:316-319).</para>
    /// <para>This is NOT a proof that every refused state is infinite. Whether this step runs away depends on the
    /// hidden draw order: if Pillage's own draw returns a Defend before a nested Pommel draw takes it, the step ends
    /// normally. A pure read cannot see that order, so this class also refuses such terminating orders. It is a
    /// visible, documented limit (the whole run is excluded, never a death or win). No option is removed from E, and
    /// no alternative action is chosen. A state outside this class runs exactly as before; the 900 s worker watchdog
    /// stays the backstop.</para>
    /// <para>Pure read: no clone, no engine call, no RNG use, no mutation.</para>
    /// </remarks>
    private static string? CheckSteamEruptionRevive(CombatState s, Option o)
    {
        if (o.Kind != "play_card" || o.HandIndex < 0 || o.HandIndex >= s.Hand.Count)
            return null;
        var pillage = s.Hand[o.HandIndex];
        if (pillage.DefId != IC.Pillage || !Plain(pillage) || !BuffSystem.Has(s.PlayerBuffs, BuffId.Hellraiser) || s.PlayerHp <= 0)
            return null;
        // No draw veto: NoDraw and Fiddle are outside both allowlists; every relic and power is source-verified.
        if (BuffSystem.Has(s.PlayerBuffs, BuffId.NoDraw) || s.Relics.Any(r => !SteamReviveInertRelics.Contains(r.DefId)))
            return null;
        if (s.PlayerBuffs.Any(b => !SteamReviveInertPlayerBuffs.Contains(b.Id)))
            return null;
        // Every enemy a living Waterfall Giant still holding SteamEruption, with only inert powers (so it is primary).
        if (s.Enemies.Count == 0 || s.Enemies.Any(e => e.DefId != KE.WaterfallGiant || e.Hp <= 0
                || BuffSystem.Get(e.Buffs, BuffId.SteamEruption) <= 0 || e.Buffs.Any(b => !InertEnemyBuffs.Contains(b.Id))))
            return null;
        bool Allowed(CardInstance c) => Plain(c) && (RecurrentStrikes.Contains(c.DefId) || SteamRevivePlainNonStrikes.Contains(c.DefId));
        var pool = s.DrawPile.Concat(s.DiscardPile).ToList();
        if (pool.Any(c => !Allowed(c)))
            return null;
        // The rest of the hand: plain pool cards only (no Status, Curse, second Pillage or unreviewed card).
        var rest = s.Hand.Where((_, i) => i != o.HandIndex).ToList();
        if (rest.Any(c => !Allowed(c)))
            return null;
        if (s.ExhaustPile.Any(c => !Plain(c) || !(Allowed(c) || SteamReviveExhaustCards.Contains(c.DefId))))
            return null;
        int recurrent = pool.Count(c => RecurrentStrikes.Contains(c.DefId));
        int defends = pool.Count(c => c.DefId == IC.DefendIronclad);
        int pommels = pool.Count(c => c.DefId == IC.PommelStrike);
        // A cycle needs a recurrent Strike; a Defend in the pool can only be passed by a nested Pommel draw.
        if (recurrent == 0 || (defends > 0 && pommels == 0))
            return null;
        // Hand bound: every non-Strike pool card landing in hand plus Juggling's clones never reaches the 10-card cap,
        // so the cap never ends the loop and no Juggling clone is diverted to the discard pile.
        int juggling = Math.Max(0, BuffSystem.Get(s.PlayerBuffs, BuffId.Juggling));
        int nonStrikes = pool.Count - recurrent;
        if (rest.Count + nonStrikes + juggling > MaxCardsInHand - 1)
            return null;

        return $"potential runaway (class B): Pillage (hand {o.HandIndex}) under Hellraiser against a Waterfall Giant still holding SteamEruption: "
            + "every in-step giant death restores 999,999,999 HP and re-arms the revive, so HP gives the draw loop no exit; the loop ends only if "
            + "Pillage's own draw returns a non-Attack, which depends on the hidden draw order (a terminating order is refused too: conservative, "
            + "not a proof of infinity); not executed. "
            + $"enemies [{string.Join("; ", s.Enemies.Select(e => $"enemy {e.DefId} hp {e.Hp}/{e.MaxHp} block {e.Block} eruption_phase {e.SteamEruptionPhase} buffs [{string.Join(",", e.Buffs.Select(b => $"{b.Id}:{b.Magnitude}"))}]"))}]; "
            + $"player hp {s.PlayerHp} buffs [{string.Join(",", s.PlayerBuffs.Select(b => $"{b.Id}:{b.Magnitude}"))}]; "
            + $"hand [{string.Join(",", s.Hand.Select(c => c.DefId))}] draw [{string.Join(",", s.DrawPile.Select(c => c.DefId))}] "
            + $"discard [{string.Join(",", s.DiscardPile.Select(c => c.DefId))}] exhaust [{string.Join(",", s.ExhaustPile.Select(c => c.DefId))}] "
            + $"relics [{string.Join(",", s.Relics.Select(r => r.DefId))}]; recurrent strikes {recurrent}, pommel {pommels}, defends {defends}, "
            + $"other non-strikes {nonStrikes - defends}, juggling {juggling}, hand bound {rest.Count + nonStrikes + juggling}/{MaxCardsInHand - 1}";
    }

    /// <summary>
    /// A card copy with no per-instance behaviour: equal (as a record, so any field added later also counts) to the
    /// default copy of its def and upgrade, ignoring only cost/turn bookkeeping that cannot change what the loop
    /// does. Dupe, TinkerType, enchantments, replays, bonus/momentum damage, Bound, Sly, Ethereal and Dampen all fail.
    /// </summary>
    public static bool Plain(CardInstance c) =>
        c with
        {
            FreeThisTurn = false, Retain = false, CostBump = 0, CostForCombat = int.MinValue, PlayedThisTurn = false,
            RetainThisTurn = false, RetainForCombat = false, CostThisTurn = int.MinValue, FreeUntilPlayed = false,
            CostOverrideThisTurn = int.MinValue, CostOverrideBaseline = 0, CostOverrideSetUpgraded = false, DeckOrigin = null,
        } == new CardInstance(c.DefId, c.Upgraded);

    /// <summary>Starter/ancient relic ids that live in <c>RunConstants</c> rather than <c>RelicEffects</c>.</summary>
    private static class RunConstantsRelic
    {
        public const int BurningBlood = Sts2Emulator.Core.Run.RunConstants.RelicBurningBlood;
        public const int FishingRod = Sts2Emulator.Core.Run.RunConstants.RelicFishingRod;
    }
}
