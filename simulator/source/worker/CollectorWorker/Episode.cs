using System.Text.Json;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Effects;
using Sts2Emulator.Core.Rng;
using Sts2Emulator.Training;

namespace CombatWorker;

public sealed record EpisodeRequest(
    string EpisodeId,
    string TemplateId,
    BattleInput Input,
    ulong EnvSeed,
    ulong PolicySeed,
    int MaxSteps,
    int MaxTurns,
    bool AllowPotions,
    string Policy,
    bool IncludeEngineState,
    EnvironmentRngMode RngMode,
    // Test-only fault injection (never set by the collector unless a test asks): -1 = off.
    int DebugThrowAtStep = -1,
    int DebugExitAtStep = -1,
    int DebugSleepAtStep = -1
);

/// <summary>One legal option in a decision: an engine action index plus an explicit target.</summary>
public sealed record Option(
    string Kind,
    int Action,
    int Target,
    int HandIndex = -1,
    int CardDefId = 0,
    bool CardUpgraded = false,
    string? TargetType = null,
    string? SelectionKind = null,
    string? Pile = null,
    int PileIndex = -1,
    int PotionSlot = -1,
    int PotionId = 0,
    string? Note = null
);

public static class Episode
{
    public const string GameAlgorithmVersion = "game-xoshiro256starstar (vendored MegaRandom, v0.111.0)";
    public const string GameSeedDerivation =
        "game: stream seed = env_seed + xxh64(name) (RunRngSet); combat rng = niche raw seed; encounter = EncounterRng.SeedFor(env_seed, floor, ...)";
    public const string TerminalBoundary =
        "CombatEngine.Step terminal, before RunEngine.FinishCombatStep and reward-time hooks: Burning Blood / Chosen Cheese heals, Improvement upgrades, Pumpkin Candle, Toy Box, Royalties and similar run mutations are NOT applied";

    public const string PolicyHierarchical = "uniform_engine_action_then_uniform_target/v1";
    public const string PolicyFlat = "uniform_over_action_target_pairs/v1";

    public static string SelectionPile(CardSelectionKind kind) =>
        kind switch
        {
            CardSelectionKind.GeneratedCardToHand or CardSelectionKind.CurseOfKnowledge => "generated",
            CardSelectionKind.DiscardToDrawPileTop or CardSelectionKind.DiscardToHand => "discard",
            CardSelectionKind.DrawPileToHand
            or CardSelectionKind.ExhaustFromDrawPile
            or CardSelectionKind.TransformDrawPileToSoul
            or CardSelectionKind.TransformDrawToMinionDiveBomb => "draw",
            _ => "hand",
        };

    /// <summary>
    /// Every legal (action, target) pair, and the legal actions withheld by configuration.
    /// Uses the engine's own <c>ValidActions</c> without any width cap.
    /// </summary>
    public static (List<Option> Legal, List<Option> Excluded, string Phase) Enumerate(CombatState s, bool allowPotions)
    {
        var legal = new List<Option>();
        var excluded = new List<Option>();
        var valid = CombatEngine.ValidActions(s);
        if (s.PendingSelection is { } sel)
        {
            string pile = SelectionPile(sel.Kind);
            foreach (int a in valid)
            {
                if (a < sel.Candidates.Count)
                {
                    var card = Sts2Emulator.Core.CombatObservation.SelectionCard(s, sel, a);
                    legal.Add(new Option("select", a, -1, CardDefId: card.DefId, CardUpgraded: card.Upgraded,
                        SelectionKind: sel.Kind.ToString(), Pile: pile, PileIndex: sel.Candidates[a]));
                }
                else
                {
                    legal.Add(new Option("skip_selection", a, -1, SelectionKind: sel.Kind.ToString()));
                }
            }
            return (legal, excluded, "selection");
        }

        var targetable = new List<int>();
        for (int i = 0; i < s.Enemies.Count; i++)
            if (EnemyAI.IsTargetable(s.Enemies[i]))
                targetable.Add(i);

        foreach (int a in valid)
        {
            if (a < s.Hand.Count)
            {
                var card = s.Hand[a];
                var def = card.ModelDef();
                var target = CardEffects.EffectiveTarget(s, def);
                if (target == CardTarget.AnyEnemy)
                {
                    if (targetable.Count == 0)
                        legal.Add(new Option("play_card", a, -1, a, card.DefId, card.Upgraded, target.ToString(),
                            Note: "no_targetable_enemy_engine_auto_target"));
                    foreach (int t in targetable)
                        legal.Add(new Option("play_card", a, t, a, card.DefId, card.Upgraded, target.ToString()));
                }
                else
                {
                    legal.Add(new Option("play_card", a, -1, a, card.DefId, card.Upgraded, target.ToString()));
                }
            }
            else if (a == s.Hand.Count)
            {
                legal.Add(new Option("end_turn", a, -1));
            }
            else
            {
                int slot = a - s.Hand.Count - 1;
                int potion = s.PotionSlots[slot];
                bool targetsEnemy = Sts2Emulator.GeneratedData.Potions.Get(potion).TargetsEnemy;
                var into = allowPotions ? legal : excluded;
                string? note = allowPotions ? null : "potion_use_disabled_by_config";
                if (targetsEnemy && targetable.Count > 0)
                {
                    foreach (int t in targetable)
                        into.Add(new Option("use_potion", a, t, TargetType: "AnyEnemy", PotionSlot: slot, PotionId: potion, Note: note));
                }
                else
                {
                    into.Add(new Option("use_potion", a, -1, TargetType: targetsEnemy ? "AnyEnemy" : "None",
                        PotionSlot: slot, PotionId: potion, Note: note));
                }
            }
        }
        return (legal, excluded, "combat");
    }

    public static int Choose(List<Option> legal, GameRng policyRng, string policy)
    {
        if (policy == PolicyFlat)
            return policyRng.NextInt(legal.Count);
        // Uniform over distinct engine actions, then uniform over that action's targets.
        var actions = legal.Select(o => o.Action).Distinct().ToList();
        int action = actions[policyRng.NextInt(actions.Count)];
        var group = Enumerable.Range(0, legal.Count).Where(i => legal[i].Action == action).ToList();
        return group.Count == 1 ? group[0] : group[policyRng.NextInt(group.Count)];
    }

    public static void WriteOption(Utf8JsonWriter w, Option o, int ordinal)
    {
        w.WriteStartObject();
        w.WriteNumber("o", ordinal);
        w.WriteString("kind", o.Kind);
        w.WriteNumber("action", o.Action);
        w.WriteNumber("target", o.Target);
        if (o.HandIndex >= 0) w.WriteNumber("hand_index", o.HandIndex);
        if (o.CardDefId != 0) { w.WriteNumber("card_def_id", o.CardDefId); w.WriteBoolean("card_upgraded", o.CardUpgraded); }
        if (o.TargetType is not null) w.WriteString("target_type", o.TargetType);
        if (o.SelectionKind is not null) w.WriteString("selection_kind", o.SelectionKind);
        if (o.Pile is not null) w.WriteString("pile", o.Pile);
        if (o.PileIndex >= 0) w.WriteNumber("pile_index", o.PileIndex);
        if (o.PotionSlot >= 0) { w.WriteNumber("potion_slot", o.PotionSlot); w.WriteNumber("potion_id", o.PotionId); }
        if (o.Note is not null) w.WriteString("note", o.Note);
        w.WriteEndObject();
    }

    /// <summary>Engine-computed values a player sees that are not stored as raw fields.</summary>
    public static void WriteDerivedView(Utf8JsonWriter w, CombatState s)
    {
        w.WriteStartObject();
        w.WriteNumber("turn", s.Turn);
        w.WriteBoolean("player_turn", s.PlayerTurn);
        w.WritePropertyName("hand");
        w.WriteStartArray();
        for (int i = 0; i < s.Hand.Count; i++)
        {
            var c = s.Hand[i];
            var def = c.ModelDef();
            w.WriteStartObject();
            w.WriteNumber("def_id", c.DefId);
            w.WriteNumber("effective_cost", CombatEngine.EffectiveCost(c, s));
            w.WriteBoolean("playable", CombatEngine.CanPlayFromHand(s, c));
            w.WriteString("target_type", CardEffects.EffectiveTarget(s, def).ToString());
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("enemies");
        w.WriteStartArray();
        float weakDelta = RelicEffects.WeakMultiplierDeltaAgainstPlayer(s);
        foreach (var e in s.Enemies)
        {
            w.WriteStartObject();
            w.WriteNumber("def_id", e.DefId);
            w.WriteNumber("hp", e.Hp);
            w.WriteNumber("max_hp", e.MaxHp);
            w.WriteNumber("block", e.Block);
            w.WriteBoolean("targetable", EnemyAI.IsTargetable(e));
            w.WritePropertyName("declared_intents");
            w.WriteStartArray();
            if (e.Hp > 0)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (EnemyAI.DeclaredIntent(e, i) is not { } intent)
                        continue;
                    w.WriteStartObject();
                    w.WriteString("type", intent.Type.ToString());
                    w.WriteNumber("magnitude", intent.Magnitude);
                    w.WriteNumber("hits", intent.Hits);
                    if (intent.Type is IntentType.Attack or IntentType.DeathBlow && intent.Hits > 0)
                    {
                        int total = intent.AnnouncedDamage(e.Buffs, s.PlayerBuffs, weakDelta);
                        w.WriteNumber("announced_total_damage", total);
                        w.WriteNumber("announced_damage_per_hit", total / intent.Hits);
                    }
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("player_buff_display");
        w.WriteStartArray();
        foreach (var b in s.PlayerBuffs)
        {
            w.WriteStartObject();
            w.WriteString("id", b.Id.ToString());
            w.WriteNumber("display_amount", Sts2Emulator.Core.CombatObservation.PlayerBuffDisplayAmount(s, b));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("pile_counts");
        w.WriteStartObject();
        w.WriteNumber("hand", s.Hand.Count);
        w.WriteNumber("draw", s.DrawPile.Count);
        w.WriteNumber("discard", s.DiscardPile.Count);
        w.WriteNumber("exhaust", s.ExhaustPile.Count);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    public static void WriteState(Utf8JsonWriter w, CombatState s, EngineDumper dumper, bool includeEngine)
    {
        w.WriteStartObject();
        if (includeEngine)
        {
            w.WritePropertyName("engine");
            dumper.Reset();
            dumper.Write(w, s, "$.engine");
        }
        w.WritePropertyName("view");
        WriteDerivedView(w, s);
        w.WriteEndObject();
    }

    public static void WriteStreams(Utf8JsonWriter w, CombatState s, CountingRandom combatRng)
    {
        w.WriteStartObject();
        void One(string name, Random? r)
        {
            if (r is CountingRandom c)
            {
                w.WritePropertyName(name);
                EngineDumper.WriteRng(w, "CountingRandom", c.Seed, c.Snapshot());
            }
            else if (r is not null)
            {
                w.WriteString(name, r.GetType().FullName);
            }
        }
        One("combat", combatRng);
        One("ShuffleRng", s.ShuffleRng);
        One("NicheHpRng", s.NicheHpRng);
        One("AiRng", s.AiRng);
        One("TargetRng", s.TargetRng);
        One("CardSelectionRng", s.CardSelectionRng);
        One("EnergyCostRng", s.EnergyCostRng);
        One("CardGenerationRng", s.CardGenerationRng);
        One("PotionGenerationRng", s.PotionGenerationRng);
        One("OrbGenerationRng", s.OrbGenerationRng);
        One("CardEffectRng", s.CardEffectRng);
        w.WriteEndObject();
    }

    /// <summary>Which deck copies and relics arrived in the fight unchanged.</summary>
    public static void WritePreservation(Utf8JsonWriter w, BattleInput input, CombatState s)
    {
        var all = s.Hand.Concat(s.DrawPile).Concat(s.DiscardPile).Concat(s.ExhaustPile).ToList();
        var missing = new List<int>();
        var changed = new List<(int, string)>();
        for (int i = 0; i < input.Deck.Count; i++)
        {
            var want = input.Deck[i] with { DeckOrigin = null };
            var found = all.Where(c => c.DeckOrigin?.Index == i).ToList();
            if (found.Count == 0)
            {
                missing.Add(i);
                continue;
            }
            var got = found[0] with { DeckOrigin = null };
            if (got != want)
                changed.Add((i, $"{want} -> {got}"));
        }
        w.WriteStartObject();
        w.WriteNumber("deck_cards", input.Deck.Count);
        w.WriteNumber("combat_pile_cards", all.Count);
        w.WriteNumber("combat_cards_without_deck_origin", all.Count(c => c.DeckOrigin is null));
        w.WritePropertyName("deck_indices_missing_from_piles");
        JsonSerializer.Serialize(w, missing);
        w.WritePropertyName("deck_copies_changed_at_combat_start");
        w.WriteStartArray();
        foreach (var (i, d) in changed)
        {
            w.WriteStartObject();
            w.WriteNumber("deck_index", i);
            w.WriteString("change", d);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("relics_changed_at_combat_start");
        w.WriteStartArray();
        for (int i = 0; i < Math.Max(input.Relics.Count, s.Relics.Count); i++)
        {
            RelicInstance? a = i < input.Relics.Count ? input.Relics[i] : null;
            RelicInstance? b = i < s.Relics.Count ? s.Relics[i] : null;
            if (a != b)
            {
                w.WriteStartObject();
                w.WriteNumber("index", i);
                w.WriteString("input", a?.ToString());
                w.WriteString("combat", b?.ToString());
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static void Run(EpisodeRequest req, Action<Action<Utf8JsonWriter>> emit)
    {
        var dumper = new EngineDumper { ElideReference = req.Input.Progression, ElideNote = "progression: see episode_start.progression (immutable)" };
        RngAlgorithmContext.ResetGameStreamCounter();
        var policyRng = PolicyRng(req.PolicySeed);
        int steps = 0;
        int purityViolations = 0;
        string status = "error";
        string? reason = null;
        Exception? error = null;
        TrainingCombat? fight = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        emit(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "episode_start");
            w.WriteString("episode_id", req.EpisodeId);
            w.WriteString("template_id", req.TemplateId);
            w.WriteNumber("env_seed", req.EnvSeed);
            w.WriteNumber("policy_seed", req.PolicySeed);
            w.WriteString("policy", req.Policy);
            w.WriteBoolean("allow_potions", req.AllowPotions);
            w.WriteNumber("max_steps", req.MaxSteps);
            w.WriteNumber("max_turns", req.MaxTurns);
            bool gameMode = req.RngMode == EnvironmentRngMode.Game;
            w.WriteString("rng_mode", gameMode ? "game" : "training");
            w.WriteString("rng_algorithm", TrainingCombatFactory.AlgorithmFor(req.RngMode).ToString());
            w.WriteString("rng_algorithm_version", gameMode ? GameAlgorithmVersion : RngAlgorithmContext.TrainingAlgorithmVersion);
            w.WriteString("seed_derivation_version", gameMode ? GameSeedDerivation : RngAlgorithmContext.TrainingSeedDerivationVersion);
            w.WriteBoolean("historical_rng_state", false);
            w.WriteString("battle_initialization", gameMode
                ? "fresh battle on the game generator seeded the game's way from env_seed; NOT the recorded run's RNG streams/positions"
                : "fresh battle on the training generator seeded from env_seed");
            w.WriteString("terminal_boundary", TerminalBoundary);
            w.WriteString("policy_rng", "TrainingPcg64XslRr seeded with TrainingSeeds.For(policy_seed, \"policy\")");
            w.WritePropertyName("progression");
            EngineDumper.WriteProgression(w, req.Input.Progression);
            w.WriteEndObject();
        });
        try
        {
            fight = TrainingCombatFactory.Build(req.Input, req.EnvSeed, req.RngMode);
            var s = fight.State;
            emit(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "initial_state");
                w.WritePropertyName("streams");
                WriteStreams(w, s, fight.CombatRng);
                w.WritePropertyName("input_preservation");
                WritePreservation(w, req.Input, s);
                w.WritePropertyName("state");
                WriteState(w, s, dumper, true);
                w.WriteEndObject();
            });
            bool dead0 = fight.DeadOnEntry;
            bool won0 = fight.WonOnEntry;
            if (dead0 || won0)
            {
                status = won0 ? "win" : "loss";
                reason = "terminal_on_entry";
            }
            else
            {
                while (true)
                {
                    if (steps >= req.MaxSteps)
                    {
                        status = "truncated";
                        reason = "max_steps";
                        break;
                    }
                    if (s.Turn > req.MaxTurns)
                    {
                        status = "truncated";
                        reason = "max_turns";
                        break;
                    }
                    // Observer purity audit: everything this worker computes from the state
                    // (legal enumeration, derived view) must leave the engine state untouched.
                    byte[] before = DumpBytes(s, req.Input.Progression);
                    var (legal, excluded, phase) = Enumerate(s, req.AllowPotions);
                    Import.Serialize(w => WriteDerivedView(w, s));
                    if (!before.AsSpan().SequenceEqual(DumpBytes(s, req.Input.Progression)))
                        purityViolations++;
                    if (legal.Count == 0)
                    {
                        status = "error";
                        reason = "no_legal_action";
                        break;
                    }
                    if (steps == req.DebugThrowAtStep)
                        throw new InvalidOperationException("debug_throw_at_step requested by test");
                    if (steps == req.DebugExitAtStep)
                        Environment.Exit(86);
                    if (steps == req.DebugSleepAtStep)
                        Thread.Sleep(Timeout.Infinite);
                    int turnBefore = s.Turn;
                    int chosen = Choose(legal, policyRng, req.Policy);
                    var option = legal[chosen];
                    var result = TrainingCombatFactory.Step(fight, option.Action, option.Target);
                    steps++;
                    // Pure reads only: PlayerIsDead would spend Fairy in a Bottle / Lizard
                    // Tail and so change the fight it is observing (e.g. after a Sandpit kill).
                    bool dead = s.PlayerHp <= 0;
                    bool noEnemy = CombatEngine.NoPrimaryEnemyLeft(s);
                    int stepIndex = steps - 1;
                    emit(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "step");
                        w.WriteNumber("t", stepIndex);
                        w.WriteNumber("turn_before", turnBefore);
                        w.WriteString("phase", phase);
                        w.WritePropertyName("legal");
                        w.WriteStartArray();
                        for (int i = 0; i < legal.Count; i++)
                            WriteOption(w, legal[i], i);
                        w.WriteEndArray();
                        w.WritePropertyName("excluded");
                        w.WriteStartArray();
                        for (int i = 0; i < excluded.Count; i++)
                            WriteOption(w, excluded[i], i);
                        w.WriteEndArray();
                        w.WriteNumber("chosen", chosen);
                        w.WritePropertyName("result");
                        w.WriteStartObject();
                        w.WriteBoolean("terminal", result.Terminal);
                        w.WriteBoolean("player_won", result.PlayerWon);
                        w.WriteNumber("engine_reward", result.Reward);
                        w.WriteBoolean("player_dead", dead);
                        w.WriteBoolean("no_primary_enemy_left", noEnemy);
                        w.WriteEndObject();
                        w.WritePropertyName("state");
                        WriteState(w, s, dumper, req.IncludeEngineState);
                        w.WriteEndObject();
                    });
                    if (result.Terminal)
                    {
                        status = result.PlayerWon ? "win" : dead ? "loss" : "terminal_other";
                        reason = "engine_terminal";
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            status = "error";
            reason = "engine_exception";
            error = ex;
        }
        long foreignStreams = RngAlgorithmContext.ForeignStreamsCreatedInScope;
        emit(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "episode_end");
            w.WriteString("episode_id", req.EpisodeId);
            w.WriteString("status", status);
            w.WriteString("reason", reason);
            w.WriteNumber("steps", steps);
            if (fight is not null)
            {
                w.WriteNumber("turn", fight.State.Turn);
                w.WriteNumber("player_hp", fight.State.PlayerHp);
                w.WriteNumber("player_max_hp", fight.State.PlayerMaxHp);
                w.WritePropertyName("final_streams");
                WriteStreams(w, fight.State, fight.CombatRng);
            }
            w.WriteNumber("policy_rng_counter", policyRng.CallCount);
            w.WriteNumber("foreign_algorithm_streams_created", foreignStreams);
            w.WriteNumber("observer_purity_violations", purityViolations);
            w.WriteNumber("worker_seconds", sw.Elapsed.TotalSeconds);
            if (error is not null)
            {
                w.WritePropertyName("error");
                w.WriteStartObject();
                w.WriteString("type", error.GetType().FullName);
                w.WriteString("message", error.Message);
                w.WriteString("stack", error.StackTrace);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        });
    }

    private static byte[] DumpBytes(CombatState s, object progression) =>
        Import.Serialize(w => new EngineDumper { ElideReference = progression }.Write(w, s));

    public static GameRng PolicyRng(ulong policySeed)
    {
        using var scope = RngAlgorithmContext.Use(RngAlgorithm.TrainingPcg64XslRr);
        return new GameRng(TrainingSeeds.For(policySeed, "policy"));
    }
}
