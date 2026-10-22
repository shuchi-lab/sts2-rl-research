using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CombatWorker;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Rng;
using Sts2Emulator.Training;

namespace RlCombatWorker;

/// <summary>
/// Interactive reset/step worker for combat_rl, speaking JSON lines: one command per stdin
/// line, exactly one JSON response per stdout line. Several fights ("envs") can be open at
/// once in one process; each keeps its own state, streams and legal-option list.
/// </summary>
/// <remarks>
/// Fights are built and stepped through <see cref="TrainingCombatFactory" /> (which opens the
/// fight's RNG algorithm scope per call), legal options come from the collector's own
/// <see cref="Episode.Enumerate" />, and engine-derived visible values from
/// <see cref="Episode.WriteDerivedView" />. Nothing here chooses for the agent: every legal
/// (action, target) pair and every selection-prompt option is returned, and the step command
/// executes exactly the option ordinal it is given. Potion use is withheld unless a reset
/// opts in with <c>allow_potions: true</c> (potion_rl_v1, see <see cref="PotionMode" />);
/// potion inventory is observed either way. <c>CombatEngine.PlayerIsDead</c> is never
/// called (it spends resurrection relics); terminal status is the engine's StepResult.
/// <para>
/// This copy runs on combat_rl's owned build08 engine (engine/whole_game_build08). The
/// protocol and every observation key are those of worker/RlWorker; the build08 state the
/// v3 features cannot encode is refused through <c>boundary_violations</c> (see
/// <see cref="PublicState.BoundaryViolations" />).
/// </para>
/// </remarks>
public static class Program
{
    public const string ProtocolVersion = "crl.worker_protocol/v1";
    // v3 adds public resources, counters, enemy relations, pending card queues and boundary
    // invariants (see PublicState). Every v2 key is unchanged.
    public const string ObservationSchema = "crl.raw_observation/v3";
    // The engine source line this worker was written for (engine/whole_game_build08/SOURCE.json).
    public const string EngineLine = "sts2emulator-whole-game-build08-v0.111.0+crl-training-overlay/v1+p2-juggernaut-await-kill";

    // Bind the engine's read-only implementation instead of duplicating energy rules.
    internal static readonly Func<CombatState, int, int> EffectiveEnergyCapacity =
        typeof(CombatEngine).GetMethod("EffectiveMaxEnergyForTurn", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<CombatState, int, int>>();
    internal static readonly string[] PlayerHistoryFields =
    [
        "card_starts_turn", "attack_starts_turn", "attack_skill_starts_turn", "attack_finishes_turn",
        "exhausted_any_turn", "damaged_any_turn", "damage_events_combat",
        "eligible_block_gains_turn", "card_finishes_combat",
    ];

    private static readonly Dictionary<string, BattleInput> Templates = [];
    private static readonly Dictionary<string, string> TemplateHashes = [];
    private static readonly Dictionary<int, Session> Sessions = [];
    private static Stream _out = null!;

    // CardInstance primary-constructor fields, in declaration order, without DeckOrigin (a
    // deck bookkeeping stamp, not a visible card property) and Dupe (History Course's
    // marker; a visible dupe is refused as a boundary violation instead of being written).
    // Reported in hello so the Python adapter can refuse a build whose card record changed shape.
    private static readonly PropertyInfo[] CardFields = typeof(CardInstance)
        .GetConstructors()
        .OrderByDescending(c => c.GetParameters().Length)
        .First()
        .GetParameters()
        .Where(p => p.Name != nameof(CardInstance.DeckOrigin) && p.Name != nameof(CardInstance.Dupe))
        .Select(p => typeof(CardInstance).GetProperty(p.Name!)!)
        .ToArray();

    public static int Main(string[] args)
    {
        _out = new BufferedStream(Console.OpenStandardOutput(), 1 << 20);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 1 << 20);
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;
            string cmdName = "?";
            byte[] response;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var cmd = doc.RootElement;
                cmdName = cmd.GetProperty("cmd").GetString()!;
                if (cmdName == "quit")
                {
                    Write(Serialize(w => { w.WriteStartObject(); w.WriteBoolean("ok", true); w.WriteString("type", "quit"); w.WriteEndObject(); }));
                    _out.Flush();
                    return 0;
                }
                response = Serialize(w => Dispatch(cmdName, cmd, w));
            }
            catch (Exception ex)
            {
                response = Serialize(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("ok", false);
                    w.WriteString("type", "command_error");
                    w.WriteString("cmd", cmdName);
                    w.WriteString("error_type", ex.GetType().FullName);
                    w.WriteString("message", ex.Message);
                    w.WriteString("stack", ex.StackTrace);
                    w.WriteEndObject();
                });
            }
            Write(response);
        }
        _out.Flush();
        return 0;
    }

    private static void Write(byte[] bytes)
    {
        _out.Write(bytes);
        _out.WriteByte((byte)'\n');
        _out.Flush();
    }

    private static byte[] Serialize(Action<Utf8JsonWriter> write) => Import.Serialize(write);

    private static void Dispatch(string name, JsonElement cmd, Utf8JsonWriter w)
    {
        switch (name)
        {
            case "hello":
                Hello(w);
                break;
            case "add_template":
            {
                string tid = cmd.GetProperty("template_id").GetString()!;
                var raw = cmd.GetProperty("engine_input");
                Templates[tid] = TemplateCodec.ReadBattleInput(raw);
                TemplateHashes[tid] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw.GetRawText()))).ToLowerInvariant();
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteString("type", "template_added");
                w.WriteString("template_id", tid);
                w.WriteNumber("templates_loaded", Templates.Count);
                w.WriteEndObject();
                break;
            }
            case "reset":
            {
                int env = cmd.GetProperty("env").GetInt32();
                string tid = cmd.GetProperty("template_id").GetString()!;
                if (!Templates.TryGetValue(tid, out var battle))
                    throw new KeyNotFoundException($"template {tid} not loaded");
                var mode = (cmd.TryGetProperty("rng_mode", out var m) ? m.GetString() : "training") switch
                {
                    "training" => EnvironmentRngMode.Training,
                    "game" => EnvironmentRngMode.Game,
                    var other => throw new ArgumentException($"rng_mode must be 'game' or 'training', not '{other}'"),
                };
                var session = new Session(
                    env,
                    tid,
                    battle,
                    cmd.GetProperty("env_seed").GetUInt64(),
                    mode,
                    cmd.GetProperty("max_steps").GetInt32(),
                    cmd.GetProperty("max_turns").GetInt32(),
                    cmd.TryGetProperty("purity_check", out var pc) && pc.GetBoolean(),
                    // potion_rl_v1 opt-in; absent or false keeps the original contract byte for byte.
                    cmd.TryGetProperty("allow_potions", out var ap) && ap.GetBoolean()
                );
                Sessions[env] = session;
                session.Reset();
                session.WriteResponse(w, "reset", null);
                break;
            }
            case "step":
            {
                var session = GetSession(cmd.GetProperty("env").GetInt32());
                var stepInfo = session.Step(cmd.GetProperty("decision").GetInt32(), cmd.GetProperty("option").GetInt32());
                session.WriteResponse(w, "step", stepInfo);
                break;
            }
            case "step_many":
            {
                // Several envs stepped in one round trip; each entry is an independent step.
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteString("type", "step_many");
                w.WritePropertyName("results");
                w.WriteStartArray();
                foreach (var e in cmd.GetProperty("steps").EnumerateArray())
                {
                    var session = GetSession(e.GetProperty("env").GetInt32());
                    var stepInfo = session.Step(e.GetProperty("decision").GetInt32(), e.GetProperty("option").GetInt32());
                    session.WriteResponse(w, "step", stepInfo);
                }
                w.WriteEndArray();
                w.WriteEndObject();
                break;
            }
            case "engine_dump":
            {
                // Debug/test only: the complete raw engine state INCLUDING hidden draw order,
                // RNG and enemy AI internals. Never part of an observation.
                var session = GetSession(cmd.GetProperty("env").GetInt32());
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteString("type", "engine_dump");
                w.WritePropertyName("engine");
                new EngineDumper { ElideReference = session.Input.Progression, ElideNote = "progression" }.Write(w, session.Fight!.State, "$.engine");
                w.WriteEndObject();
                break;
            }
            case "close":
            {
                Sessions.Remove(cmd.GetProperty("env").GetInt32());
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteString("type", "closed");
                w.WriteEndObject();
                break;
            }
            default:
                throw new ArgumentException($"unknown command {name}");
        }
    }

    private static Session GetSession(int env) =>
        Sessions.TryGetValue(env, out var s) ? s : throw new KeyNotFoundException($"env {env} not reset");

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Hello(Utf8JsonWriter w)
    {
        var engineAsm = typeof(CombatEngine).Assembly;
        var cdsAsm = typeof(Episode).Assembly;
        var rlAsm = typeof(Program).Assembly;
        w.WriteStartObject();
        w.WriteBoolean("ok", true);
        w.WriteString("type", "hello");
        w.WriteString("protocol", ProtocolVersion);
        w.WriteString("observation_schema", ObservationSchema);
        w.WriteString("engine_input_schema", TemplateCodec.EngineInputSchema);
        w.WriteString("engine_assembly_sha256", Sha256File(engineAsm.Location));
        w.WriteString("cds_worker_assembly_sha256", Sha256File(cdsAsm.Location));
        w.WriteString("rl_worker_assembly_sha256", Sha256File(rlAsm.Location));
        w.WriteString("engine_assembly_mvid", engineAsm.ManifestModule.ModuleVersionId.ToString());
        w.WriteString("engine_line", EngineLine);
        w.WriteString("rng_training_version", RngAlgorithmContext.TrainingAlgorithmVersion);
        w.WriteString("seed_derivation", TrainingSeeds.Describe());
        w.WriteString("terminal_boundary", Episode.TerminalBoundary);
        w.WritePropertyName("card_fields");
        w.WriteStartArray();
        foreach (var f in CardFields)
            w.WriteStringValue(f.Name);
        w.WriteEndArray();
        w.WritePropertyName("buff_ids");
        w.WriteStartArray();
        foreach (var n in Enum.GetNames<BuffId>())
            w.WriteStringValue(n);
        w.WriteEndArray();
        w.WritePropertyName("intent_types");
        w.WriteStartArray();
        foreach (var n in Enum.GetNames<IntentType>())
            w.WriteStringValue(n);
        w.WriteEndArray();
        w.WritePropertyName("selection_kinds");
        w.WriteStartArray();
        foreach (var n in Enum.GetNames<CardSelectionKind>())
            w.WriteStringValue(n);
        w.WriteEndArray();
        // Enum fields inside the card row, so the adapter can validate their value ranges.
        void EnumMap<T>(string name) where T : struct, Enum
        {
            w.WritePropertyName(name);
            w.WriteStartObject();
            foreach (var v in Enum.GetValues<T>())
                w.WriteNumber(v.ToString(), Convert.ToInt32(v));
            w.WriteEndObject();
        }
        EnumMap<Enchantment>("enchantments");
        EnumMap<CardType>("card_types");
        EnumMap<TinkerRider>("tinker_riders");
        w.WritePropertyName("player_history_fields");
        w.WriteStartArray();
        foreach (var field in PlayerHistoryFields)
            w.WriteStringValue(field);
        w.WriteEndArray();
        PublicState.WriteHello(w);
        PotionMode.WriteHello(w);
        w.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        w.WriteNumber("pid", Environment.ProcessId);
        w.WriteEndObject();
    }

    /// <summary>One card as a flat numeric array in <see cref="CardFields" /> order.</summary>
    internal static double[] CardRow(CardInstance c)
    {
        var row = new double[CardFields.Length];
        for (int i = 0; i < CardFields.Length; i++)
        {
            row[i] = CardFields[i].GetValue(c) switch
            {
                bool b => b ? 1 : 0,
                int v => v,
                decimal d => (double)d,
                Enum e => Convert.ToInt32(e),
                var v => throw new NotSupportedException($"CardInstance.{CardFields[i].Name}: {v?.GetType()}"),
            };
        }
        return row;
    }

    internal static int CompareRows(double[] a, double[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0)
                return c;
        }
        return 0;
    }

    internal static void WriteCardRows(Utf8JsonWriter w, IEnumerable<double[]> rows)
    {
        w.WriteStartArray();
        foreach (var r in rows)
        {
            w.WriteStartArray();
            foreach (var x in r)
                w.WriteNumberValue(x);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    internal static void WriteBuffs(Utf8JsonWriter w, List<BuffState> buffs)
    {
        w.WriteStartArray();
        foreach (var b in buffs)
        {
            w.WriteStartArray();
            w.WriteNumberValue((int)b.Id);
            w.WriteNumberValue(b.Magnitude);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }
}

internal sealed record StepInfo(
    int Decision,
    int Option,
    string Kind,
    int Action,
    int Target,
    int TurnBefore,
    int TurnAfter,
    bool Terminal,
    bool PlayerWon,
    float EngineReward
);

internal sealed class Session(
    int env,
    string templateId,
    BattleInput input,
    ulong envSeed,
    EnvironmentRngMode mode,
    int maxSteps,
    int maxTurns,
    bool purityCheck,
    bool allowPotions = false
)
{
    public int Env { get; } = env;
    public string TemplateId { get; } = templateId;
    public BattleInput Input { get; } = input;
    public TrainingCombat? Fight { get; private set; }

    private int _steps;
    private int _decision;
    private List<Option> _legal = [];
    private int _excludedPotionOptions;
    private string _phase = "combat";
    private string _status = "running";
    private string? _reason;
    private string? _error;
    private long _foreignStreams;
    private int _purityViolations;

    public void Reset()
    {
        long f0 = RngAlgorithmContext.ForeignStreamsCreatedInScope;
        try
        {
            Fight = TrainingCombatFactory.Build(Input, envSeed, mode);
        }
        catch (Exception ex)
        {
            _status = "error";
            _reason = "engine_exception_on_build";
            _error = $"{ex.GetType().FullName}: {ex.Message}";
            return;
        }
        _foreignStreams += RngAlgorithmContext.ForeignStreamsCreatedInScope - f0;
        if (Fight.DeadOnEntry || Fight.WonOnEntry)
        {
            _status = Fight.WonOnEntry ? "win" : "loss";
            _reason = "terminal_on_entry";
            _legal = [];
            return;
        }
        Refresh();
    }

    /// <summary>Enumerate the decision now owed, or close the episode by truncation.</summary>
    private void Refresh()
    {
        var s = Fight!.State;
        byte[]? before = purityCheck ? Dump(s) : null;
        List<Option> legal, excluded;
        string phase;
        try
        {
            (legal, excluded, phase) = Episode.Enumerate(s, allowPotions);
            if (allowPotions)
                PotionMode.WithholdAutomatic(legal, excluded);
        }
        catch (Exception ex)
        {
            // The engine reached a state its own option enumeration cannot describe (an
            // engine limitation). Close only this env, visibly; a step_many batch goes on.
            _status = "error";
            _reason = "enumerate_exception";
            _error = $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}";
            _legal = [];
            _decision++;
            return;
        }
        if (purityCheck)
        {
            Import.Serialize(w => Episode.WriteDerivedView(w, s));
            if (!before!.AsSpan().SequenceEqual(Dump(s)))
                _purityViolations++;
        }
        _legal = legal;
        _excludedPotionOptions = excluded.Count;
        _phase = phase;
        _decision++;
        if (purityCheck)
        {
            // v3: the whole observation writer (counters, relations, reflection reads) is
            // checked too, not only the derived view.
            byte[] beforeObs = Dump(s);
            Import.Serialize(WriteObservation);
            if (!beforeObs.AsSpan().SequenceEqual(Dump(s)))
                _purityViolations++;
        }
        // Truncation keeps the legal list: a TD target may bootstrap from this state.
        if (_steps >= maxSteps)
        {
            _status = "truncated";
            _reason = "max_steps";
        }
        else if (s.Turn > maxTurns)
        {
            _status = "truncated";
            _reason = "max_turns";
        }
        else if (legal.Count == 0)
        {
            _status = "error";
            _reason = "no_legal_action";
        }
    }

    private byte[] Dump(CombatState s) =>
        Import.Serialize(w => new EngineDumper { ElideReference = Input.Progression }.Write(w, s));

    public StepInfo Step(int decision, int option)
    {
        if (_status != "running")
            throw new InvalidOperationException($"env {Env} is {_status}; reset first");
        if (decision != _decision)
            throw new InvalidOperationException($"env {Env}: stale decision {decision}, current {_decision}");
        if (option < 0 || option >= _legal.Count)
            throw new ArgumentOutOfRangeException(nameof(option), $"option {option} outside 0..{_legal.Count - 1}");
        var o = _legal[option];
        var s = Fight!.State;
        int turnBefore = s.Turn;
        StepResult result;
        long f0 = RngAlgorithmContext.ForeignStreamsCreatedInScope;
        try
        {
            result = TrainingCombatFactory.Step(Fight, o.Action, o.Target);
        }
        catch (Exception ex)
        {
            _steps++;
            _status = "error";
            _reason = "engine_exception";
            _error = $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}";
            _legal = [];
            return new StepInfo(decision, option, o.Kind, o.Action, o.Target, turnBefore, s.Turn, false, false, 0f);
        }
        _foreignStreams += RngAlgorithmContext.ForeignStreamsCreatedInScope - f0;
        _steps++;
        if (result.Terminal)
        {
            // Pure reads only (no PlayerIsDead): a win is the engine's verdict; otherwise a
            // non-positive HP is a loss and anything else is reported as terminal_other.
            _status = result.PlayerWon ? "win" : s.PlayerHp <= 0 ? "loss" : "terminal_other";
            _reason = "engine_terminal";
            _legal = [];
            _decision++;
        }
        else
        {
            Refresh();
        }
        return new StepInfo(decision, option, o.Kind, o.Action, o.Target, turnBefore, s.Turn, result.Terminal, result.PlayerWon, result.Reward);
    }

    public void WriteResponse(Utf8JsonWriter w, string type, StepInfo? info)
    {
        // The observation is serialised first, so a failure to describe this env's state
        // closes only this env (status error) and never aborts a step_many batch midway.
        byte[]? obs = null;
        if (Fight is not null)
        {
            try
            {
                obs = Import.Serialize(WriteObservation);
            }
            catch (Exception ex)
            {
                _status = "error";
                _reason = "observation_exception";
                _error = $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}";
                _legal = [];
            }
        }
        w.WriteStartObject();
        w.WriteBoolean("ok", true);
        w.WriteString("type", type);
        w.WriteNumber("env", Env);
        w.WriteString("template_id", TemplateId);
        w.WriteNumber("env_seed", envSeed);
        w.WriteString("status", _status);
        w.WriteString("reason", _reason);
        if (_error is not null)
            w.WriteString("error", _error);
        w.WriteNumber("steps", _steps);
        w.WriteNumber("decision", _decision);
        w.WriteNumber("entry_hp", Input.PlayerHp);
        w.WriteNumber("entry_max_hp", Input.PlayerMaxHp);
        w.WriteNumber("foreign_rng_streams", _foreignStreams);
        w.WriteNumber("purity_violations", _purityViolations);
        if (allowPotions)
            w.WriteBoolean("allow_potions", true);
        if (info is not null)
        {
            w.WritePropertyName("step");
            w.WriteStartObject();
            w.WriteNumber("decision", info.Decision);
            w.WriteNumber("option", info.Option);
            w.WriteString("kind", info.Kind);
            w.WriteNumber("action", info.Action);
            w.WriteNumber("target", info.Target);
            w.WriteNumber("turn_before", info.TurnBefore);
            w.WriteNumber("turn_after", info.TurnAfter);
            w.WriteBoolean("terminal", info.Terminal);
            w.WriteBoolean("player_won", info.PlayerWon);
            w.WriteNumber("engine_reward", info.EngineReward);
            w.WriteEndObject();
        }
        if (obs is not null)
        {
            w.WritePropertyName("obs");
            w.WriteRawValue(obs, skipInputValidation: true);
        }
        w.WriteEndObject();
    }

    /// <summary>
    /// The visible state. Draw-pile order is never written: the part of the pile the player
    /// knows in order (cards they deliberately placed) is written in that order, and the
    /// unknown rest as a canonically sorted multiset, so even raw trajectories cannot leak it.
    /// Draw-pile selection candidates are written as cards with their pile index withheld.
    /// </summary>
    private void WriteObservation(Utf8JsonWriter w)
    {
        var s = Fight!.State;
        w.WriteStartObject();
        w.WriteNumber("turn", s.Turn);
        w.WriteBoolean("player_turn", s.PlayerTurn);
        w.WriteString("phase", _phase);
        w.WritePropertyName("player");
        w.WriteStartObject();
        w.WriteNumber("hp", s.PlayerHp);
        w.WriteNumber("max_hp", s.PlayerMaxHp);
        w.WriteNumber("block", s.PlayerBlock);
        w.WriteNumber("energy", s.Energy);
        w.WriteNumber("max_energy", s.MaxEnergy);
        w.WriteNumber("energy_capacity", Program.EffectiveEnergyCapacity(s, s.Turn + 1));
        w.WriteNumber("gold", s.PlayerGold);
        w.WritePropertyName("buffs");
        Program.WriteBuffs(w, s.PlayerBuffs);
        w.WritePropertyName("debuffs_at_round_start");
        Program.WriteBuffs(w, s.PlayerDebuffsAtRoundStart);
        // Turn/combat history counters the player can count for themselves.
        w.WritePropertyName("history");
        w.WriteStartArray();
        foreach (int v in new[]
        {
            s.CardPlaysStartedThisTurn, s.AttackPlaysStartedThisTurn, s.AttackOrSkillPlaysStartedThisTurn,
            s.AttackCardsPlayedThisTurn, s.CardsExhaustedThisTurn > 0 ? 1 : 0,
            s.PlayerHpLostThisTurn > 0 ? 1 : 0, s.UnblockedDamageHitCount,
            s.BlockHistoryTurn == s.Turn && s.BlockHistoryPlayerTurn == s.PlayerTurn
                ? s.BlockGainsThisTurn - (s.ActiveBlockPlay.Epoch == s.BlockHistoryEpoch ? s.ActiveBlockPlay.BlockGains : 0)
                : 0,
            s.CardsPlayedThisCombat,
        })
            w.WriteNumberValue(v);
        w.WriteEndArray();
        // Side state that belongs to specific effect rows (folded in by the Python adapter).
        w.WritePropertyName("effect_aux");
        w.WriteStartObject();
        w.WriteNumber("toric_toughness_block", s.ToricToughnessBlock);
        w.WriteNumber("sloth_cards_played_this_turn", s.SlothCardsPlayedThisTurn);
        w.WriteNumber("tender_cards_played_this_turn", s.TenderCardsPlayedThisTurn);
        w.WriteNumber("bound_cards_this_turn", s.BoundCardsThisTurn);
        w.WriteBoolean("bound_card_played_this_turn", s.BoundCardPlayedThisTurn);
        w.WriteNumber("cards_played_since_panache_proc", s.CardsPlayedSincePanacheProc);
        w.WriteNumber("drawn_cards_since_automation_proc", s.DrawnCardsSinceAutomationProc);
        w.WriteNumber("hex_applier", s.HexApplier is null ? -1 : s.Enemies.IndexOf(s.HexApplier));
        w.WriteBoolean("hex_applier_alive", s.HexApplier is { Hp: > 0 });
        w.WriteNumber("shrink_applier", s.ShrinkApplier is null ? -1 : s.Enemies.IndexOf(s.ShrinkApplier));
        w.WriteBoolean("shrink_applier_alive", s.ShrinkApplier is { Hp: > 0 });
        PublicState.WriteEffectAux(w, s);
        w.WriteEndObject();
        PublicState.WritePlayerExtras(w, s);
        w.WriteEndObject();

        w.WritePropertyName("hand");
        Program.WriteCardRows(w, s.Hand.Select(Program.CardRow));
        int top = Math.Min(s.KnownTopCount, s.DrawPile.Count);
        int bottom = Math.Min(s.KnownBottomCount, s.DrawPile.Count - top);
        w.WritePropertyName("draw_known_top");
        Program.WriteCardRows(w, s.DrawPile.Take(top).Select(Program.CardRow));
        w.WritePropertyName("draw_known_bottom");
        Program.WriteCardRows(w, s.DrawPile.Skip(s.DrawPile.Count - bottom).Select(Program.CardRow));
        var unknown = s.DrawPile.Skip(top).Take(s.DrawPile.Count - top - bottom).Select(Program.CardRow).ToList();
        unknown.Sort(Program.CompareRows);
        w.WritePropertyName("draw_unknown_sorted");
        Program.WriteCardRows(w, unknown);
        w.WritePropertyName("discard");
        Program.WriteCardRows(w, s.DiscardPile.Select(Program.CardRow));
        w.WritePropertyName("exhaust");
        Program.WriteCardRows(w, s.ExhaustPile.Select(Program.CardRow));
        w.WritePropertyName("incoming");
        Program.WriteCardRows(w, s.ReturnToHandBeforeDraw.Concat(s.CopiesToHandBeforeDraw).Select(Program.CardRow));

        w.WritePropertyName("relics");
        w.WriteStartArray();
        foreach (var r in s.Relics)
        {
            w.WriteStartArray();
            w.WriteNumberValue(r.DefId);
            w.WriteNumberValue(r.Counter);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WritePropertyName("potions");
        w.WriteStartArray();
        for (int i = 0; i < s.MaxPotionSlots && i < s.PotionSlots.Length; i++)
            w.WriteNumberValue(s.PotionSlots[i]);
        w.WriteEndArray();

        w.WritePropertyName("enemies");
        w.WriteStartArray();
        foreach (var e in s.Enemies)
        {
            w.WriteStartObject();
            w.WriteNumber("def_id", e.DefId);
            w.WriteNumber("hp", e.Hp);
            w.WriteNumber("max_hp", e.MaxHp);
            w.WriteNumber("block", e.Block);
            w.WritePropertyName("buffs");
            Program.WriteBuffs(w, e.Buffs);
            w.WriteNumber("hardened_shell_hp_lost_this_side", e.HardenedShellHpLostThisSide);
            PublicState.WriteEnemyExtras(w, s, e);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        PublicState.WriteStateExtras(w, s);

        w.WritePropertyName("view");
        Episode.WriteDerivedView(w, s);

        w.WritePropertyName("selection");
        if (s.PendingSelection is { } sel)
        {
            w.WriteStartObject();
            w.WriteNumber("kind", (int)sel.Kind);
            w.WriteString("kind_name", sel.Kind.ToString());
            w.WriteNumber("amount", sel.Amount);
            w.WriteNumber("selected_count", sel.Selected.Count);
            w.WriteBoolean("skippable", sel.Skippable);
            w.WriteNumber("source_card_def_id", sel.SourceCardDefId);
            w.WriteNumber("candidate_count", sel.Candidates.Count);
            // v3: what the screen's answer does, all shown on the screen itself.
            w.WriteNumber("source_potion_id", sel.SourcePotionId);
            w.WriteBoolean("generated_upgraded", sel.GeneratedUpgraded);
            w.WriteBoolean("generated_retained", sel.GeneratedRetained);
            w.WritePropertyName("after_selection_to_hand");
            Program.WriteCardRows(w, sel.AfterSelectionToHand.Select(Program.CardRow));
            w.WriteEndObject();
        }
        else
        {
            w.WriteNullValue();
        }

        w.WriteNumber("excluded_potion_options", _excludedPotionOptions);
        if (allowPotions)
            PotionMode.WriteObservationExtras(w, s);
        w.WritePropertyName("legal");
        w.WriteStartArray();
        for (int i = 0; i < _legal.Count; i++)
            WriteLegal(w, s, _legal[i], i);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteLegal(Utf8JsonWriter w, CombatState s, Option o, int ordinal)
    {
        w.WriteStartObject();
        w.WriteNumber("o", ordinal);
        w.WriteString("kind", o.Kind);
        w.WriteNumber("action", o.Action);
        w.WriteNumber("target", o.Target);
        if (o.HandIndex >= 0)
            w.WriteNumber("hand_index", o.HandIndex);
        if (o.TargetType is not null)
            w.WriteString("target_type", o.TargetType);
        if (o.Note is not null)
            w.WriteString("note", o.Note);
        if (o.Kind == "use_potion")
        {
            // Only reachable with allow_potions. The PHYSICAL slot (engine action =
            // hand count + 1 + slot) and the identity the slot holds right now.
            if (o.PotionSlot < 0 || o.PotionSlot >= s.PotionSlots.Length || s.PotionSlots[o.PotionSlot] != o.PotionId)
                throw new InvalidOperationException($"potion option slot {o.PotionSlot} / id {o.PotionId} disagrees with the inventory");
            w.WriteNumber("potion_slot", o.PotionSlot);
            w.WriteNumber("potion_id", o.PotionId);
        }
        if (o.Kind == "select")
        {
            w.WriteString("pile", o.Pile);
            // Draw-pile positions would reveal hidden order; only hand/discard indices are public.
            if (o.Pile is "hand" or "discard")
                w.WriteNumber("pile_index", o.PileIndex);
            CardInstance card = o.Pile switch
            {
                "generated" => new CardInstance(o.CardDefId, o.CardUpgraded),
                "hand" => s.Hand[o.PileIndex],
                "discard" => s.DiscardPile[o.PileIndex],
                "draw" => s.DrawPile[o.PileIndex],
                var p => throw new NotSupportedException($"selection pile {p}"),
            };
            if (card.DefId != o.CardDefId || card.Upgraded != o.CardUpgraded)
                throw new InvalidOperationException(
                    $"selection candidate mismatch: pile {o.Pile}[{o.PileIndex}] is {card.DefId}/{card.Upgraded}, option says {o.CardDefId}/{o.CardUpgraded} ({o.SelectionKind})");
            w.WritePropertyName("card");
            w.WriteStartArray();
            foreach (var x in Program.CardRow(card))
                w.WriteNumberValue(x);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }
}

/// <summary>
/// potion_rl_v1's opt-in potion contract (<c>reset.allow_potions</c>). Off by default: a reset
/// without the flag writes exactly the original observation. With it, every manual potion use
/// the engine allows is a legal option, except Automatic-usage potions (Fairy in a Bottle):
/// the game never offers them as a manual use, and the engine spends them itself in
/// <c>CombatEngine.PlayerIsDead</c>. They are kept in the <c>excluded</c> list with a note,
/// never hidden from the inventory.
/// </summary>
internal static class PotionMode
{
    public const string Schema = "crl.potion_actions/v2";
    public const string AutomaticNote = "automatic_potion_not_manually_usable";

    private static PotionDef[]? _defs;

    private static PotionDef Def(int id)
    {
        _defs ??= (PotionDef[])(typeof(CombatEngine).Assembly.GetType("Sts2Emulator.GeneratedData.Potions")
                      ?.GetField("_all", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                  ?? throw new MissingFieldException("Potions", "_all"));
        foreach (var d in _defs)
            if (d.Id == id)
                return d;
        throw new ArgumentException($"Unknown potion id {id}");
    }

    internal static void WithholdAutomatic(List<Option> legal, List<Option> excluded)
    {
        for (int i = legal.Count - 1; i >= 0; i--)
        {
            var o = legal[i];
            if (o.Kind == "use_potion" && Def(o.PotionId).Usage == PotionUsage.Automatic)
            {
                legal.RemoveAt(i);
                excluded.Add(o with { Note = AutomaticNote });
            }
        }
    }

    /// <summary>Public round context: an extra turn (Ambergris, Pael's Eye) does not raise it.</summary>
    internal static void WriteObservationExtras(Utf8JsonWriter w, CombatState s)
    {
        w.WriteBoolean("allow_potions", true);
        w.WriteNumber("round_number", s.RoundNumber);
        // v2: the ACTUAL capacity and the represented slot array. `potions` lists
        // min(MaxPotionSlots, array) slots, so a capacity above the array (Alchemical Coffer on a
        // big belt) is not representable; the adapter refuses it instead of calling it full.
        w.WriteNumber("potion_capacity", s.MaxPotionSlots);
        w.WriteNumber("potion_slot_array", s.PotionSlots.Length);
    }

    internal static void WriteHello(Utf8JsonWriter w)
    {
        w.WritePropertyName("potion_actions");
        w.WriteStartObject();
        w.WriteString("schema", Schema);
        w.WriteString("reset_flag", "allow_potions");
        w.WriteBoolean("default", false);
        w.WriteString("automatic_usage", "excluded: " + AutomaticNote);
        w.WriteString("legal_fields", "use_potion: potion_slot (physical), potion_id, target");
        w.WriteString("observation_fields", "round_number, allow_potions, potion_capacity, potion_slot_array (potion mode only)");
        w.WritePropertyName("potions");
        w.WriteStartArray();
        Def(1);
        foreach (var d in _defs!.OrderBy(d => d.Id))
        {
            w.WriteStartArray();
            w.WriteNumberValue(d.Id);
            w.WriteStringValue(d.Name);
            w.WriteStringValue(d.Usage.ToString());
            w.WriteBooleanValue(d.TargetsEnemy);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}

/// <summary>
/// The v3 public-state extension: every <see cref="CombatState" /> and <see cref="EnemyState" />
/// field is classified here, and <see cref="WriteHello" /> refuses to start if the engine has a
/// field this table does not name, so a new engine field can never be silently ignored.
/// </summary>
/// <remarks>
/// Classes: <c>exposed</c> (written somewhere in the observation), <c>counter</c> (written in
/// <c>player.counters</c>), <c>hidden_rng</c>, <c>hidden_ai</c> (enemy move machine internals,
/// i.e. future-move information), <c>hidden_order</c> (draw order; only the known part and a
/// sorted multiset are written), <c>reward_bookkeeping</c> (post-combat reward state with no
/// in-combat effect), <c>engine_bookkeeping</c> (identities, continuations and caches whose
/// public consequences are exposed elsewhere), <c>transient</c> (only non-default inside one
/// engine call; checked at every decision by <see cref="BoundaryViolations" />),
/// <c>inert_at_decision</c> (cannot affect anything after the current engine call; checked),
/// <c>unsupported_if_used</c> (relic-specific, or build08's <c>RoundNumber</c> once an extra
/// turn separates it from <c>Turn</c>; the adapter refuses states where it is live).
/// All reads are pure: no engine method with side effects is called.
/// </remarks>
/// <summary>Read-only reflection over the engine's internal auto-play continuations.</summary>
internal static class AutoPlays
{
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo NestedField = typeof(CombatState).GetField("PendingNestedAutoPlays", Inst)!;
    private static readonly FieldInfo BatchField = typeof(CombatState).GetField("PendingPotionAutoPlays", Inst)!;

    internal static object? Get(object o, string field) =>
        (o.GetType().GetField(field, Inst) ?? throw new MissingFieldException(o.GetType().Name, field)).GetValue(o);

    internal static bool Flag(object o, string field) => (bool)Get(o, field)!;
    internal static int Int(object o, string field) => (int)Get(o, field)!;
    internal static List<object> Nested(CombatState s) => ((System.Collections.IList)NestedField.GetValue(s)!).Cast<object>().ToList();
    internal static object? Batch(CombatState s) => BatchField.GetValue(s);
    internal static object Child(object nested) => Get(nested, "Child")!;
    internal static CardInstance Card(object parked) => (CardInstance)Get(parked, "Card")!;

    /// <summary>Every parked automatic child (ParkedAutoPlay): Havoc stack, then the batch's.</summary>
    internal static IEnumerable<object> ParkedChildren(CombatState s)
    {
        foreach (var n in Nested(s))
            yield return Child(n);
        if (Batch(s) is { } b && Get(b, "Parked") is { } p)
            yield return p;
    }

    /// <summary>A parked child's creature: its held reference, else its target index.</summary>
    internal static EnemyState? Target(CombatState s, object parked)
    {
        if (Get(parked, "PlayTarget") is EnemyState held)
            return held;
        int i = Int(parked, "TargetIndex");
        return i >= 0 && i < s.Enemies.Count ? s.Enemies[i] : null;
    }
}

internal static class PublicState
{
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static readonly Dictionary<string, string> CombatFields = new()
    {
        ["Character"] = "exposed", ["Progression"] = "engine_bookkeeping", ["AscensionLevel"] = "counter",
        ["PlayerHp"] = "exposed", ["PlayerMaxHp"] = "exposed", ["PlayerBlock"] = "exposed", ["Energy"] = "exposed",
        ["MaxEnergy"] = "counter", ["PlayerGold"] = "exposed",
        ["StolenBackGold"] = "reward_bookkeeping", ["RoyaltiesGold"] = "reward_bookkeeping",
        ["FatGremlinEscaped"] = "reward_bookkeeping", ["BattlewornDummyRanOutOfTime"] = "reward_bookkeeping",
        ["PaelsLegionCooldown"] = "counter", ["MercGoldWasStolen"] = "reward_bookkeeping",
        ["Hand"] = "exposed", ["DrawPile"] = "hidden_order", ["DiscardPile"] = "exposed", ["ExhaustPile"] = "exposed",
        ["ReturnToHandBeforeDraw"] = "exposed", ["CopiesToHandBeforeDraw"] = "exposed",
        ["EndTurnAwaitingSelection"] = "counter", ["PendingEnemyTurn"] = "engine_bookkeeping",
        ["PendingTurnSetup"] = "engine_bookkeeping", ["DeferCardRupture"] = "counter",
        ["CardRuptureStrength"] = "counter", ["CompletedCardRuptureStrength"] = "counter",
        ["PendingEffectTail"] = "engine_bookkeeping", ["AfterimageBeforePlay"] = "counter",
        ["StormBeforePlay"] = "counter", ["SubroutineBeforePlay"] = "counter",
        ["StatusCardsDrawnThisTurn"] = "counter", ["OrbGenerationRng"] = "hidden_rng",
        ["ResolvingDefendCard"] = "counter", ["PlayedCardCostForCombat"] = "counter",
        ["AutoPlayQueue"] = "exposed", ["Orbs"] = "exposed", ["OrbCapacity"] = "exposed", ["BaseOrbSlots"] = "exposed",
        ["OstyHp"] = "exposed", ["OstyMaxHp"] = "exposed", ["Stars"] = "exposed", ["OrbitEnergySpent"] = "counter",
        ["StrangleBeforePlay"] = "exposed", ["SerpentFormSnapshots"] = "counter",
        ["PotionSlots"] = "exposed", ["MaxPotionSlots"] = "counter", ["Relics"] = "exposed",
        ["PlayerBuffs"] = "exposed", ["HexApplier"] = "exposed", ["ShrinkApplier"] = "exposed",
        ["AllTargetDamageDepth"] = "transient", ["PlayerDebuffsAtRoundStart"] = "exposed",
        ["Enemies"] = "exposed", ["EncounterId"] = "engine_bookkeeping", ["IsEliteCombat"] = "counter",
        ["ShuffleRng"] = "hidden_rng", ["TargetRng"] = "hidden_rng", ["CardSelectionRng"] = "hidden_rng",
        ["CardGenerationRng"] = "hidden_rng", ["PotionGenerationRng"] = "hidden_rng", ["AiRng"] = "hidden_rng",
        ["NicheHpRng"] = "hidden_rng", ["PresetEnemyHps"] = "engine_bookkeeping",
        ["PendingSelection"] = "exposed", ["PendingCardPlay"] = "counter",
        ["AutoPlaying"] = "transient", ["AutoPlayMayPause"] = "transient",
        ["PendingPotionAutoPlays"] = "exposed", ["PendingNestedAutoPlays"] = "exposed",
        ["PlayedCardBonusDamage"] = "counter", ["PlayedCardMomentumDamage"] = "counter",
        ["PlayedCardBonusBlock"] = "counter", ["EnergySpentThisTurn"] = "counter", ["PenNibArmed"] = "counter",
        ["EtherealExhaustsThisTurn"] = "counter", ["VambraceSpent"] = "counter",
        ["VambraceTriggerSourceId"] = "counter", ["PaelsLegionAffectedPlayId"] = "counter",
        ["UnblockedDamageThisTurn"] = "counter", ["RainbowRingAttacks"] = "counter",
        ["RainbowRingSkills"] = "counter", ["RainbowRingPowers"] = "counter", ["RainbowRingPaidThisTurn"] = "counter",
        ["UnsettlingLampSpent"] = "counter", ["UnsettlingLampCard"] = "counter",
        ["GamblingChipDiscarded"] = "counter", ["GamblingChipOwed"] = "counter", ["BurningSticksUsed"] = "counter",
        ["BeltBuckleApplied"] = "counter", ["IsEliteRoom"] = "counter", ["TookUnblockedDamage"] = "counter",
        ["PlayedCardEnchantSpent"] = "counter", ["PlayedCardEnchantGrew"] = "counter",
        ["PlayedCardCostBump"] = "counter", ["ToricToughnessBlock"] = "exposed", ["EnergyCostRng"] = "hidden_rng",
        ["WhisperingEarringSpent"] = "counter", ["Turn"] = "exposed", ["RoundNumber"] = "unsupported_if_used",
        ["PlayerTurn"] = "exposed",
        ["SkillPlayedWhileSmoggy"] = "counter", ["CardsPlayedThisTurn"] = "counter",
        ["AttackCardsPlayedThisTurn"] = "exposed", ["AttackPlaysStartedThisTurn"] = "exposed",
        ["CardPlaysStartedThisTurn"] = "exposed", ["AttackOrSkillPlaysStartedThisTurn"] = "exposed",
        ["AttackOrSkillCardsPlayedThisTurn"] = "counter", ["CardPlaysThisTurn"] = "counter",
        ["LastAttackThisTurn"] = "unsupported_if_used", ["LastAttackLastTurn"] = "unsupported_if_used",
        ["CardsPlayedThisCombat"] = "exposed", ["DrawnCardsSinceAutomationProc"] = "exposed",
        ["CardsDrawnThisCombat"] = "counter", ["ShivsPlayedThisTurn"] = "counter",
        ["ExtraCardRewards"] = "reward_bookkeeping", ["StolenBackCards"] = "reward_bookkeeping",
        ["TracksRunDeck"] = "reward_bookkeeping", ["StolenFromDeck"] = "reward_bookkeeping",
        ["ExecutedThisEnemyTurn"] = "engine_bookkeeping", ["AutoPlayTargetIndex"] = "transient",
        ["EnemyDeathDispatchDepth"] = "transient", ["CardEffectRng"] = "transient",
        ["AwaitedDeathPrePass"] = "transient", ["ActiveAttackCommand"] = "transient",
        ["LastAttackCommand"] = "engine_bookkeeping", ["CardsPlayedSincePanacheProc"] = "exposed",
        ["BlockGainsThisTurn"] = "exposed", ["NextBlockPlayIdentity"] = "engine_bookkeeping",
        ["ActiveBlockPlay"] = "engine_bookkeeping", ["ActiveCardResources"] = "engine_bookkeeping",
        ["BlockHistoryEpoch"] = "engine_bookkeeping", ["BlockHistoryTurn"] = "engine_bookkeeping",
        ["BlockHistoryPlayerTurn"] = "engine_bookkeeping", ["PlayerHpLostThisTurn"] = "counter",
        ["CardsExhaustedThisTurn"] = "counter", ["CardsDiscardedThisTurn"] = "counter",
        ["LightningOrbsChanneledThisCombat"] = "counter", ["EtherealExhaustCount"] = "counter",
        ["EtherealCardPlaysThisCombat"] = "counter", ["LethalAttackPlay"] = "counter",
        ["SkillCardsPlayedThisTurn"] = "counter", ["BoundCardsThisTurn"] = "exposed",
        ["BoundCardPlayedThisTurn"] = "exposed", ["MusicBoxUsedThisTurn"] = "counter", ["FurCoatBites"] = "counter",
        ["CardPlaysLastTurn"] = "counter", ["ExtraCardsDrawnThisTurn"] = "counter",
        ["DoomAppliedThisTurn"] = "counter", ["OstyAttacksThisTurn"] = "counter",
        ["StarsGainedThisTurn"] = "counter", ["PlayedCardStarsSpent"] = "counter",
        ["VoidFormCardsPlayedThisTurn"] = "counter", ["EndTurnAfterPlay"] = "counter",
        ["CardsGeneratedThisCombat"] = "counter", ["TenderCardsPlayedThisTurn"] = "exposed",
        ["SlothCardsPlayedThisTurn"] = "exposed", ["UnblockedDamageHitCount"] = "exposed",
        ["TargetEnemyIndex"] = "inert_at_decision", ["PlayTarget"] = "transient",
        ["KnownTopCount"] = "exposed", ["KnownBottomCount"] = "exposed",
    };

    internal static readonly Dictionary<string, string> EnemyFields = new()
    {
        ["DefId"] = "exposed", ["Hp"] = "exposed", ["DeathHooksHandled"] = "engine_bookkeeping", ["MaxHp"] = "exposed",
        ["Block"] = "exposed", ["CurrentIntent"] = "exposed", ["SecondaryIntent"] = "exposed",
        ["StunReturnsToPreparedMove"] = "hidden_ai", ["ResumePreparedMoveAfterStun"] = "hidden_ai",
        ["Buffs"] = "exposed", ["LastSpawnedDefId"] = "hidden_ai", ["PoweredHitsThisTurn"] = "exposed",
        ["CurlUpCardSourceId"] = "exposed", ["SkittishFirstHitCommand"] = "inert_at_decision",
        ["SkittishFirstHitUnblocked"] = "inert_at_decision", ["HardenedShellHpLostThisSide"] = "exposed",
        ["PossessedPlayerStrength"] = "exposed", ["PossessedPlayerDexterity"] = "exposed",
        ["MoveIndex"] = "hidden_ai", ["SteamEruptionPhase"] = "exposed", ["SteamEruptionDamage"] = "exposed",
        ["LastMove"] = "hidden_ai", ["LastMoveRepeats"] = "hidden_ai", ["OnceOnlyMoveUsed"] = "hidden_ai",
        ["MoveHistory"] = "hidden_ai", ["StartsOnBranch"] = "hidden_ai", ["RollsNextMove"] = "hidden_ai",
        ["NemesisIntangibleOn"] = "exposed", ["StarterMove"] = "hidden_ai", ["StolenGold"] = "exposed",
        ["StolenCard"] = "exposed", ["Escaped"] = "exposed", ["OffBalance"] = "exposed",
        ["LastBranch"] = "hidden_ai", ["RepeatStreak"] = "hidden_ai", ["BranchCooldown"] = "hidden_ai",
        ["Slot"] = "exposed",
    };

    private static double B(bool b) => b ? 1 : 0;

    /// <summary>Public scalar counters, in the order written to <c>player.counters</c>.</summary>
    internal static readonly (string Name, Func<CombatState, double> Read)[] Counters =
    [
        ("ascension_level", s => s.AscensionLevel),
        ("max_energy", s => s.MaxEnergy),
        ("max_potion_slots", s => s.MaxPotionSlots),
        ("is_elite_combat", s => B(s.IsEliteCombat)),
        ("is_elite_room", s => B(s.IsEliteRoom)),
        ("fur_coat_bites", s => B(s.FurCoatBites)),
        ("end_turn_awaiting_selection", s => B(s.EndTurnAwaitingSelection)),
        ("pending_card_play", s => B(s.PendingCardPlay is not null)),
        ("parked_play_extra_plays", s => s.PendingCardPlay?.ExtraPlays ?? 0),
        ("parked_play_index", s => s.PendingCardPlay?.PlayIndex ?? 0),
        ("parked_play_energy_spent", s => s.PendingCardPlay?.EnergySpent ?? 0),
        ("nested_autoplay_depth", s => AutoPlays.Nested(s).Count),
        ("autoplay_batch_pending", s => B(AutoPlays.Batch(s) is not null)),
        ("autoplay_batch_resumes_turn_start", s => B(AutoPlays.Batch(s) is { } b && AutoPlays.Flag(b, "ResumesTurnStart"))),
        ("autoplay_batch_resumes_end_turn", s => B(AutoPlays.Batch(s) is { } b && AutoPlays.Flag(b, "ResumesEndTurn"))),
        ("stampede_remaining", s => AutoPlays.Batch(s) is { } b ? AutoPlays.Int(b, "StampedeRemaining") : 0),
        ("defer_card_rupture", s => B(s.DeferCardRupture)),
        ("card_rupture_strength", s => s.CardRuptureStrength),
        ("completed_card_rupture_strength", s => s.CompletedCardRuptureStrength),
        ("afterimage_before_play", s => s.AfterimageBeforePlay),
        ("storm_before_play", s => s.StormBeforePlay),
        ("subroutine_before_play", s => s.SubroutineBeforePlay),
        ("serpent_form_snapshot_count", s => s.SerpentFormSnapshots.Count),
        ("serpent_form_snapshot_innermost", s => s.SerpentFormSnapshots.Count > 0 ? s.SerpentFormSnapshots[^1] : 0),
        ("status_cards_drawn_this_turn", s => s.StatusCardsDrawnThisTurn),
        ("resolving_defend_card", s => B(s.ResolvingDefendCard)),
        ("played_card_cost_for_combat_set", s => B(s.PlayedCardCostForCombat != int.MinValue)),
        ("played_card_cost_for_combat", s => s.PlayedCardCostForCombat != int.MinValue ? s.PlayedCardCostForCombat : 0),
        ("played_card_bonus_damage", s => (double)s.PlayedCardBonusDamage),
        ("played_card_momentum_damage", s => s.PlayedCardMomentumDamage),
        ("played_card_bonus_block", s => s.PlayedCardBonusBlock),
        ("played_card_enchant_spent", s => B(s.PlayedCardEnchantSpent)),
        ("played_card_enchant_grew", s => B(s.PlayedCardEnchantGrew)),
        ("played_card_cost_bump", s => s.PlayedCardCostBump),
        ("played_card_stars_spent", s => s.PlayedCardStarsSpent),
        ("lethal_attack_play", s => B(s.LethalAttackPlay)),
        ("end_turn_after_play", s => B(s.EndTurnAfterPlay)),
        ("energy_spent_this_turn", s => s.EnergySpentThisTurn),
        ("orbit_energy_spent", s => s.OrbitEnergySpent),
        ("void_form_cards_played_this_turn", s => s.VoidFormCardsPlayedThisTurn),
        ("cards_played_this_turn", s => s.CardsPlayedThisTurn),
        ("attack_or_skill_cards_played_this_turn", s => s.AttackOrSkillCardsPlayedThisTurn),
        ("card_plays_this_turn", s => s.CardPlaysThisTurn),
        ("skill_cards_played_this_turn", s => s.SkillCardsPlayedThisTurn),
        ("shivs_played_this_turn", s => s.ShivsPlayedThisTurn),
        ("card_plays_last_turn", s => s.CardPlaysLastTurn),
        ("cards_drawn_this_combat", s => s.CardsDrawnThisCombat),
        ("extra_cards_drawn_this_turn", s => s.ExtraCardsDrawnThisTurn),
        ("cards_generated_this_combat", s => s.CardsGeneratedThisCombat),
        ("cards_exhausted_this_turn", s => s.CardsExhaustedThisTurn),
        ("cards_discarded_this_turn", s => s.CardsDiscardedThisTurn),
        ("ethereal_exhausts_this_turn", s => s.EtherealExhaustsThisTurn),
        ("ethereal_exhaust_count", s => s.EtherealExhaustCount),
        ("ethereal_card_plays_this_combat", s => s.EtherealCardPlaysThisCombat),
        ("player_hp_lost_this_turn", s => s.PlayerHpLostThisTurn),
        ("unblocked_damage_this_turn", s => s.UnblockedDamageThisTurn),
        ("took_unblocked_damage", s => B(s.TookUnblockedDamage)),
        ("lightning_orbs_channeled_this_combat", s => s.LightningOrbsChanneledThisCombat),
        ("doom_applied_this_turn", s => B(s.DoomAppliedThisTurn)),
        ("osty_attacks_this_turn", s => s.OstyAttacksThisTurn),
        ("stars_gained_this_turn", s => s.StarsGainedThisTurn),
        ("skill_played_while_smoggy", s => B(s.SkillPlayedWhileSmoggy)),
        ("pen_nib_armed", s => B(s.PenNibArmed)),
        ("vambrace_spent", s => B(s.VambraceSpent)),
        ("vambrace_claimed_by_play", s => B(s.VambraceTriggerSourceId != 0)),
        ("paels_legion_cooldown", s => s.PaelsLegionCooldown),
        ("paels_legion_claimed_by_play", s => B(s.PaelsLegionAffectedPlayId != 0)),
        ("rainbow_ring_attacks", s => s.RainbowRingAttacks),
        ("rainbow_ring_skills", s => s.RainbowRingSkills),
        ("rainbow_ring_powers", s => s.RainbowRingPowers),
        ("rainbow_ring_paid_this_turn", s => B(s.RainbowRingPaidThisTurn)),
        ("unsettling_lamp_spent", s => B(s.UnsettlingLampSpent)),
        ("unsettling_lamp_card", s => B(s.UnsettlingLampCard)),
        ("gambling_chip_discarded", s => s.GamblingChipDiscarded),
        ("gambling_chip_owed", s => B(s.GamblingChipOwed)),
        ("burning_sticks_used", s => B(s.BurningSticksUsed)),
        ("belt_buckle_applied", s => B(s.BeltBuckleApplied)),
        ("whispering_earring_spent", s => B(s.WhisperingEarringSpent)),
        ("music_box_used_this_turn", s => B(s.MusicBoxUsedThisTurn)),
    ];

    /// <summary>Per-enemy public scalars, in the order written to <c>enemies[j].extra</c>.</summary>
    internal static readonly (string Name, Func<CombatState, EnemyState, int, double> Read)[] EnemyExtras =
    [
        ("curl_up_pending", (s, e, j) => B(e.CurlUpCardSourceId != 0)),
        ("possessed_player_strength", (s, e, j) => e.PossessedPlayerStrength),
        ("possessed_player_dexterity", (s, e, j) => e.PossessedPlayerDexterity),
        ("steam_eruption_phase", (s, e, j) => e.SteamEruptionPhase),
        ("steam_eruption_damage", (s, e, j) => e.SteamEruptionDamage),
        ("nemesis_intangible_on", (s, e, j) => B(e.NemesisIntangibleOn)),
        ("at_player_back", (s, e, j) => B(AtPlayerBack(s, e))),
        ("is_dampen_caster", (s, e, j) => B(IsDampenCaster(s, e))),
        ("stolen_gold", (s, e, j) => e.StolenGold),
        ("has_stolen_card", (s, e, j) => B(e.StolenCard is not null)),
        ("escaped", (s, e, j) => B(e.Escaped)),
        ("off_balance", (s, e, j) => B(e.OffBalance)),
        ("powered_hits_this_turn", (s, e, j) => e.PoweredHitsThisTurn),
        ("slot", (s, e, j) => e.Slot),
        ("strangle_before_play", (s, e, j) => j < s.StrangleBeforePlay.Count ? s.StrangleBeforePlay[j] : 0),
        ("has_secondary_intent", (s, e, j) => B(e.SecondaryIntent is not null)),
        ("is_parked_play_target", (s, e, j) => B(ParkedPlayTarget(s) is { } t && ReferenceEquals(t, e))),
        ("is_parked_autoplay_target", (s, e, j) => B(AutoPlays.ParkedChildren(s).Any(c => ReferenceEquals(AutoPlays.Target(s, c), e)))),
    ];

    private static readonly FieldInfo ParkedPlayTargetField =
        typeof(PendingCardPlay).GetField("PlayTarget", Inst) ?? throw new MissingFieldException(nameof(PendingCardPlay), "PlayTarget");

    /// <summary>
    /// The creature a parked chosen play is aimed at: its held reference when set, otherwise
    /// its target index (the engine restores both on resume). Public: the player aimed it.
    /// </summary>
    internal static EnemyState? ParkedPlayTarget(CombatState s)
    {
        if (s.PendingCardPlay is not { } play)
            return null;
        if (ParkedPlayTargetField.GetValue(play) is EnemyState held)
            return held;
        return play.TargetEnemyIndex >= 0 && play.TargetEnemyIndex < s.Enemies.Count ? s.Enemies[play.TargetEnemyIndex] : null;
    }

    /// <summary>
    /// <c>SurroundedPower</c>: the player's facing is the BuffState magnitude (1 Right, 2 Left);
    /// an enemy is at the player's back when it attacks from the side the player faces away from.
    /// </summary>
    internal static bool AtPlayerBack(CombatState s, EnemyState e)
    {
        int facing = BuffSystem.Get(s.PlayerBuffs, BuffId.Surrounded);
        return facing == Sts2Emulator.Core.Run.RunConstants.FacingRight
            ? BuffSystem.Get(e.Buffs, BuffId.BackAttackLeft) > 0
            : facing == Sts2Emulator.Core.Run.RunConstants.FacingLeft && BuffSystem.Get(e.Buffs, BuffId.BackAttackRight) > 0;
    }

    /// <summary>
    /// <c>DampenPower</c>'s caster set as the engine models it (CombatEngine death pass): every
    /// living Magi Knight while Dampen is up; the last one's death restores the upgrades.
    /// </summary>
    internal static bool IsDampenCaster(CombatState s, EnemyState e) =>
        BuffSystem.Get(s.PlayerBuffs, BuffId.Dampen) > 0 && e.DefId == KE.MagiKnight && e.Hp > 0;

    private static FieldInfo F(string name) =>
        typeof(CombatState).GetField(name, Inst) ?? throw new MissingFieldException(nameof(CombatState), name);

    private static readonly FieldInfo ActiveAttackCommand = F("ActiveAttackCommand");
    private static readonly FieldInfo EnemyDeathDispatchDepth = F("EnemyDeathDispatchDepth");
    private static readonly FieldInfo CardEffectRng = F("CardEffectRng");
    private static readonly FieldInfo AwaitedDeathPrePass = F("AwaitedDeathPrePass");
    private static readonly FieldInfo PlayTarget = F("PlayTarget");
    private static readonly FieldInfo PendingTurnSetup = F("PendingTurnSetup");

    /// <summary>
    /// Fields that must hold their between-calls value at a decision. A violation means the
    /// state carries in-flight engine context the observation does not describe; Python turns
    /// it into an explicit unsupported state. <c>ActiveAttackCommand == 0</c> is also what makes
    /// the Skittish first-hit record inert: every later attack command takes a fresh id
    /// (<c>++LastAttackCommand</c>), so a stored id can never match again.
    /// </summary>
    /// <remarks>
    /// build08 adds four refusals for state the v3 features cannot encode. They are explicit
    /// v3 encoder limitations, not acquisition restrictions: the engine plays each rule, and
    /// the state is reachable in training (Stratagem, engine card 470, is a retained card that
    /// combat can generate). Python labels them <c>EncoderLimitation</c> and ends the episode as
    /// unsupported; nothing is auto-answered or removed. <c>RoundNumber</c> (an extra turn left the round behind
    /// <c>Turn + 1</c>, and Plating's decay reads the round), <c>DupeCard</c> (a History Course
    /// dupe leaves combat after its play, which its card row cannot say), <c>HandDrawOwed</c>
    /// (Stratagem paused the turn-start hand draw with draws still owed) and
    /// <c>HavocPlayTop</c> (Stratagem paused Havoc, whose top-card play waits on the pick).
    /// </remarks>
    internal static List<string> BoundaryViolations(CombatState s)
    {
        var v = new List<string>();
        if ((int)ActiveAttackCommand.GetValue(s)! != 0) v.Add("ActiveAttackCommand");
        if ((int)EnemyDeathDispatchDepth.GetValue(s)! != 0) v.Add("EnemyDeathDispatchDepth");
        if (s.AllTargetDamageDepth != 0) v.Add("AllTargetDamageDepth");
        if (CardEffectRng.GetValue(s) is not null) v.Add("CardEffectRng");
        if (AwaitedDeathPrePass.GetValue(s) is not null) v.Add("AwaitedDeathPrePass");
        if (PlayTarget.GetValue(s) is not null) v.Add("PlayTarget");
        if (s.AutoPlaying) v.Add("AutoPlaying");
        if (s.AutoPlayTargetIndex != -1) v.Add("AutoPlayTargetIndex");
        if (s.Character != CharacterId.Ironclad) v.Add("Character");
        if (s.RoundNumber != s.Turn + 1) v.Add("RoundNumber");
        if (VisibleCards(s).Any(c => c.Dupe)) v.Add("DupeCard");
        if (PendingTurnSetup.GetValue(s) is { } setup && AutoPlays.Int(setup, "HandDrawOwed") > 0) v.Add("HandDrawOwed");
        if (PendingTails(s).Any(t => t.Kind == EffectTailKind.HavocPlayTop)) v.Add("HavocPlayTop");
        return v;
    }

    /// <summary>Every card the observation writes, wherever it sits.</summary>
    internal static IEnumerable<CardInstance> VisibleCards(CombatState s)
    {
        var cards = s.AllCards().Concat(s.ReturnToHandBeforeDraw).Concat(s.CopiesToHandBeforeDraw)
            .Concat(s.AutoPlayQueue)
            .Concat(AutoPlays.Nested(s).Select(n => AutoPlays.Card(AutoPlays.Child(n))))
            .Concat(s.Enemies.Select(e => e.StolenCard).OfType<CardInstance>());
        if (s.PendingCardPlay is { } parked)
            cards = cards.Append(parked.Card);
        if (AutoPlays.Batch(s) is { } batch)
        {
            if (AutoPlays.Get(batch, "Parked") is { } bp)
                cards = cards.Append(AutoPlays.Card(bp));
            cards = cards.Concat((List<CardInstance>)AutoPlays.Get(batch, "Staged")!);
        }
        if (s.PendingSelection is { } sel)
            cards = cards.Concat(sel.AfterSelectionToHand);
        return cards;
    }

    /// <summary>Every owed effect tail: the state's, the parked chosen play's, each Havoc child's.</summary>
    private static IEnumerable<EffectTail> PendingTails(CombatState s)
    {
        yield return s.PendingEffectTail;
        if (s.PendingCardPlay is { } parked)
            yield return parked.EffectTail;
        foreach (var n in AutoPlays.Nested(s))
            yield return (EffectTail)AutoPlays.Get(n, "ChildTail")!;
    }

    private static IEnumerable<string> FieldNames(Type t) =>
        t.GetFields(Inst).Where(f => !f.Name.Contains('<')).Select(f => f.Name)
            .Concat(t.GetProperties(Inst).Where(p => p.GetIndexParameters().Length == 0 && p.DeclaringType == t
                && p.GetMethod is { } g && !g.IsStatic && p.SetMethod is not null).Select(p => p.Name));

    private static void CheckClassified(Type t, Dictionary<string, string> table)
    {
        var actual = FieldNames(t).ToHashSet();
        var missing = actual.Where(n => !table.ContainsKey(n)).OrderBy(n => n).ToList();
        var stale = table.Keys.Where(n => !actual.Contains(n)).OrderBy(n => n).ToList();
        if (missing.Count > 0 || stale.Count > 0)
            throw new InvalidOperationException(
                $"{t.Name} field classification out of date: unclassified [{string.Join(", ", missing)}], stale [{string.Join(", ", stale)}]");
    }

    private static T[] GeneratedTable<T>(string type) =>
        (T[])(typeof(CombatEngine).Assembly.GetType($"Sts2Emulator.GeneratedData.{type}")
                  ?.GetField("_all", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
              ?? throw new MissingFieldException(type, "_all"));

    internal static void WriteHello(Utf8JsonWriter w)
    {
        CheckClassified(typeof(CombatState), CombatFields);
        CheckClassified(typeof(EnemyState), EnemyFields);
        w.WritePropertyName("state_field_classes");
        w.WriteStartObject();
        foreach (var (owner, table) in new[] { ("CombatState", CombatFields), ("EnemyState", EnemyFields) })
        {
            w.WritePropertyName(owner);
            w.WriteStartObject();
            foreach (var kv in table.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WritePropertyName("player_counter_fields");
        w.WriteStartArray();
        foreach (var c in Counters)
            w.WriteStringValue(c.Name);
        w.WriteEndArray();
        w.WritePropertyName("enemy_extra_fields");
        w.WriteStartArray();
        foreach (var c in EnemyExtras)
            w.WriteStringValue(c.Name);
        w.WriteEndArray();
        w.WritePropertyName("orb_types");
        w.WriteStartArray();
        foreach (var n in Enum.GetNames<OrbType>())
            w.WriteStringValue(n);
        w.WriteEndArray();
        // The engine's own definition tables, so vocabularies are checked against what the
        // engine can actually create (spawned enemies are absent from catalog.json).
        w.WritePropertyName("engine_enemies");
        w.WriteStartArray();
        foreach (var e in GeneratedTable<EnemyDef>("Enemies").OrderBy(e => e.Id))
        {
            w.WriteStartArray();
            w.WriteNumberValue(e.Id);
            w.WriteStringValue(e.Name);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WritePropertyName("engine_cards");
        w.WriteStartArray();
        foreach (var c in GeneratedTable<CardDef>("Cards").OrderBy(c => c.Id))
        {
            w.WriteStartArray();
            w.WriteNumberValue(c.Id);
            w.WriteStringValue(c.Entry);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteNumber("magi_knight_def_id", KE.MagiKnight);
        w.WriteNumber("history_course_relic_id", Sts2Emulator.Core.Effects.RelicEffects.HistoryCourse);
    }

    internal static void WriteEffectAux(Utf8JsonWriter w, CombatState s)
    {
        w.WriteNumber("orbit_energy_spent", s.OrbitEnergySpent);
        w.WriteNumber("void_form_cards_played_this_turn", s.VoidFormCardsPlayedThisTurn);
        w.WriteBoolean("dampen_caster_alive", s.Enemies.Any(e => IsDampenCaster(s, e)));
        w.WriteNumber("dampened_cards", s.AllCards().Concat(s.ReturnToHandBeforeDraw).Concat(s.CopiesToHandBeforeDraw)
            .Count(c => c.DampenedUpgrade));
    }

    internal static void WritePlayerExtras(Utf8JsonWriter w, CombatState s)
    {
        w.WritePropertyName("resources");
        w.WriteStartObject();
        w.WriteNumber("stars", s.Stars);
        w.WriteNumber("orb_capacity", s.OrbCapacity);
        w.WriteNumber("base_orb_slots", s.BaseOrbSlots);
        w.WriteNumber("osty_hp", s.OstyHp);
        w.WriteNumber("osty_max_hp", s.OstyMaxHp);
        w.WritePropertyName("orbs");
        w.WriteStartArray();
        foreach (var o in s.Orbs)
        {
            w.WriteStartArray();
            w.WriteNumberValue((int)o.Type);
            w.WriteNumberValue(o.EvokeValue);
            w.WriteNumberValue(o.PassiveValue);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WritePropertyName("counters");
        w.WriteStartArray();
        foreach (var c in Counters)
            w.WriteNumberValue(c.Read(s));
        w.WriteEndArray();
    }

    internal static void WriteEnemyExtras(Utf8JsonWriter w, CombatState s, EnemyState e)
    {
        int j = s.Enemies.IndexOf(e);
        w.WritePropertyName("extra");
        w.WriteStartArray();
        foreach (var c in EnemyExtras)
            w.WriteNumberValue(c.Read(s, e, j));
        w.WriteEndArray();
        w.WritePropertyName("stolen_card");
        if (e.StolenCard is { } card)
        {
            w.WriteStartArray();
            foreach (var x in Program.CardRow(card))
                w.WriteNumberValue(x);
            w.WriteEndArray();
        }
        else
        {
            w.WriteNullValue();
        }
    }

    internal static void WriteStateExtras(Utf8JsonWriter w, CombatState s)
    {
        w.WriteString("character", s.Character.ToString());
        w.WritePropertyName("autoplay_queue");
        Program.WriteCardRows(w, s.AutoPlayQueue.Select(Program.CardRow));
        // The chosen card a selection interrupted: in no pile (the game's Play pile) until resumed.
        w.WritePropertyName("parked_play_card");
        Program.WriteCardRows(w, s.PendingCardPlay is { } parked ? [Program.CardRow(parked.Card)] : []);
        // Automatic plays parked on a choice, also in the game's Play pile: Havoc children
        // (innermost first; each is exhausted when it finishes) and a Stampede / Mayhem /
        // Distilled Chaos batch's waiting child and its staged, not yet played cards.
        w.WritePropertyName("parked_nested_autoplays");
        Program.WriteCardRows(w, AutoPlays.Nested(s).Select(n => Program.CardRow(AutoPlays.Card(AutoPlays.Child(n)))));
        var batch = AutoPlays.Batch(s);
        w.WritePropertyName("parked_batch_autoplay");
        Program.WriteCardRows(w, batch is not null && AutoPlays.Get(batch, "Parked") is { } bp
            ? [Program.CardRow(AutoPlays.Card(bp))] : []);
        w.WritePropertyName("batch_staged");
        Program.WriteCardRows(w, batch is not null
            ? ((List<CardInstance>)AutoPlays.Get(batch, "Staged")!).Select(Program.CardRow) : []);
        w.WriteBoolean("history_course_encore_pending", s.LastAttackLastTurn is not null
            && Sts2Emulator.Core.Effects.RelicEffects.Has(s.Relics, Sts2Emulator.Core.Effects.RelicEffects.HistoryCourse));
        w.WritePropertyName("boundary_violations");
        w.WriteStartArray();
        foreach (var v in BoundaryViolations(s))
            w.WriteStringValue(v);
        w.WriteEndArray();
    }
}
