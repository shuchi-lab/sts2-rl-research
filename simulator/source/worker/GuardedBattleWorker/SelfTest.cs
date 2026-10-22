using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CombatWorker;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Effects;
using Sts2Emulator.Training;
using Guard = FullRunWorker.AutoplayLoopGuard;

namespace GuardedBattleWorker;

/// <summary>
/// <c>--selftest</c>: structural checks with NO gameplay. No fight is built (no
/// <c>TrainingCombatFactory</c>), no engine step runs and no RNG stream is created. It covers
/// (1) the compiled reviewed guard on hand-built <see cref="CombatState" />s, (2) facade routing
/// against fake sessions, and (3) the pinned reflection path on a real original
/// <c>Session</c> that was constructed but never reset.
/// </summary>
internal static class SelfTest
{
    private sealed record Case(string Name, bool Pass, string Detail);

    private static readonly List<Case> Cases = [];

    private static void Check(string name, bool pass, string detail = "") => Cases.Add(new Case(name, pass, detail));

    private static void Throws<T>(string name, Action a, string? messagePart = null) where T : Exception
    {
        try
        {
            a();
            Check(name, false, "no exception");
        }
        catch (T ex)
        {
            Check(name, messagePart is null || ex.Message.Contains(messagePart), $"{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Check(name, false, $"wrong exception {ex.GetType().FullName}: {ex.Message}");
        }
    }

    public static int Run()
    {
        Run("guard", GuardCases);
        Run("routing", RoutingCases);
        Run("original", OriginalCases);
        int failed = Cases.Count(c => !c.Pass);
        var json = Import.Serialize(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", "act1ft/guarded_battle_worker_selftest/v1");
            w.WriteBoolean("gameplay", false);
            w.WriteNumber("passed", Cases.Count - failed);
            w.WriteNumber("failed", failed);
            w.WritePropertyName("cases");
            w.WriteStartArray();
            foreach (var c in Cases)
            {
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                w.WriteBoolean("pass", c.Pass);
                w.WriteString("detail", c.Detail.Length > 600 ? c.Detail[..600] : c.Detail);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
        Console.Out.WriteLine(Encoding.UTF8.GetString(json));
        return failed == 0 && Cases.Count > 0 ? 0 : 1;
    }

    private static void Run(string group, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Check($"{group}:unexpected_exception", false, $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ------------------------------------------------------------------ (1) the compiled guard

    private static CardInstance C(int id) => new(id, false);

    private static Option Play(int handIndex, int defId) => new("play_card", 1000 + handIndex, -1, HandIndex: handIndex, CardDefId: defId);

    /// <summary>Class A: the t18058 sentinel-HP shape (giant at 999,999,999 in eruption phase).</summary>
    internal static CombatState ClassA() => new()
    {
        PlayerHp = 60, PlayerMaxHp = 80,
        Hand = [C(IC.Pillage), C(IC.StrikeIronclad)],
        DrawPile = [C(IC.StrikeIronclad), C(IC.StrikeIronclad), C(IC.TwinStrike)],
        DiscardPile = [C(IC.PerfectedStrike)],
        PlayerBuffs = [new BuffState(BuffId.Hellraiser, 1)],
        Relics = [new RelicInstance(Sts2Emulator.Core.Run.RunConstants.RelicBurningBlood)],
        Enemies = [new EnemyState { DefId = KE.WaterfallGiant, Hp = 999_999_999, MaxHp = 999_999_999, SteamEruptionPhase = 1, Buffs = [new BuffState(BuffId.SteamEruption, 1)] }],
    };

    /// <summary>Class B: the t47872 shape (giant at normal HP still holding SteamEruption).</summary>
    internal static CombatState ClassB() => new()
    {
        PlayerHp = 40, PlayerMaxHp = 80,
        Hand = [C(IC.Pillage), C(IC.Bash)],
        DrawPile = [C(IC.StrikeIronclad), C(IC.PommelStrike), C(IC.DefendIronclad)],
        DiscardPile = [C(IC.StrikeIronclad)],
        ExhaustPile = [C(IC.ForgottenRitual)],
        PlayerBuffs = [new BuffState(BuffId.Hellraiser, 1), new BuffState(BuffId.Juggling, 1)],
        Relics = [new RelicInstance(Sts2Emulator.Core.Run.RunConstants.RelicBurningBlood), new RelicInstance(RelicEffects.PenNib)],
        Enemies = [new EnemyState { DefId = KE.WaterfallGiant, Hp = 300, MaxHp = 300, SteamEruptionPhase = 0, Buffs = [new BuffState(BuffId.SteamEruption, 1)] }],
    };

    private static void GuardCases()
    {
        var a = Guard.Check(ClassA(), Play(0, IC.Pillage));
        Check("guard:class_a_refused", a is not null && a.StartsWith("Pillage (hand 0)"), a ?? "null");
        var b = Guard.Check(ClassB(), Play(0, IC.Pillage));
        Check("guard:class_b_refused", b is not null && b.StartsWith("potential runaway (class B)"), b ?? "null");

        Check("guard:class_a_other_card_allowed", Guard.Check(ClassA(), Play(1, IC.StrikeIronclad)) is null);
        Check("guard:class_a_end_turn_allowed", Guard.Check(ClassA(), new Option("end_turn", 0, -1)) is null);
        var noHell = ClassB();
        noHell.PlayerBuffs.RemoveAll(x => x.Id == BuffId.Hellraiser);
        Check("guard:class_b_without_hellraiser_allowed", Guard.Check(noHell, Play(0, IC.Pillage)) is null);
        var relic = ClassB();
        relic.Relics.Add(new RelicInstance(987_654));
        Check("guard:class_b_unreviewed_relic_allowed", Guard.Check(relic, Play(0, IC.Pillage)) is null);
        var noSteam = ClassB();
        noSteam.Enemies[0].Buffs.Clear();
        Check("guard:class_b_without_steam_eruption_allowed", Guard.Check(noSteam, Play(0, IC.Pillage)) is null);
        var second = ClassB();
        second.Hand.Add(C(IC.Pillage));
        Check("guard:class_b_second_pillage_allowed", Guard.Check(second, Play(0, IC.Pillage)) is null);
        var upgraded = ClassA();
        upgraded.Hand[0] = new CardInstance(IC.Pillage, true);
        Check("guard:class_a_upgraded_pillage_still_plain_refused", Guard.Check(upgraded, Play(0, IC.Pillage)) is not null);

        // Pure read: the guard leaves the state exactly as it found it.
        var s = ClassB();
        string before = Snapshot(s);
        Guard.Check(s, Play(0, IC.Pillage));
        Check("guard:pure_read", Snapshot(s) == before);
    }

    private static string Snapshot(CombatState s) =>
        string.Join("|", s.PlayerHp, string.Join(",", s.Hand), string.Join(",", s.DrawPile), string.Join(",", s.DiscardPile),
            string.Join(",", s.ExhaustPile), string.Join(",", s.PlayerBuffs), string.Join(",", s.Relics),
            string.Join(",", s.Enemies.Select(e => $"{e.DefId}:{e.Hp}:{e.SteamEruptionPhase}:{string.Join("/", e.Buffs)}")));

    // ------------------------------------------------------------------ (2) routing against fakes

    private sealed class FakeSession(int env) : IGuardSession
    {
        public string Status { get; set; } = "running";
        public int Decision { get; set; } = 1;
        public List<Option> Options = [new("end_turn", 0, -1), Play(1, IC.StrikeIronclad), Play(2, IC.Pillage)];
        public CombatState? State { get; set; } = new();
        public int NativeSteps;
        public int Refusals;
        public int LegalCount => Options.Count;
        public Option Legal(int ordinal) => Options[ordinal];

        public void RefuseAndWrite(Utf8JsonWriter w, int decision, int option, Option o, string evidence)
        {
            Refusals++;
            Status = "unsupported";
            Options = [];
            w.WriteStartObject();
            w.WriteNumber("env", env);
            w.WriteString("status", Status);
            w.WriteBoolean("refused", true);
            w.WriteString("evidence", evidence);
            w.WriteEndObject();
        }
    }

    /// <summary>Mimics the original Dispatch/Session.Step checks and counts its native steps.</summary>
    private sealed class FakeOriginal : IOriginalWorker
    {
        public readonly Dictionary<int, FakeSession> Sessions = [];
        public readonly List<string> Calls = [];

        public IGuardSession? Find(int env) => Sessions.GetValueOrDefault(env);

        public void Dispatch(string name, JsonElement cmd, Utf8JsonWriter w)
        {
            Calls.Add($"{name} {cmd.GetRawText()}");
            if (name != "step")
            {
                w.WriteStartObject();
                w.WriteString("type", name);
                w.WriteEndObject();
                return;
            }
            int env = cmd.GetProperty("env").GetInt32();
            var s = Sessions.TryGetValue(env, out var x) ? x : throw new KeyNotFoundException($"env {env} not reset");
            int decision = cmd.GetProperty("decision").GetInt32(), option = cmd.GetProperty("option").GetInt32();
            if (s.Status != "running")
                throw new InvalidOperationException($"env {env} is {s.Status}; reset first");
            if (decision != s.Decision)
                throw new InvalidOperationException($"env {env}: stale decision {decision}, current {s.Decision}");
            if (option < 0 || option >= s.Options.Count)
                throw new ArgumentOutOfRangeException(nameof(option), $"option {option} outside 0..{s.Options.Count - 1}");
            s.NativeSteps++;
            s.Decision++;
            w.WriteStartObject();
            w.WriteString("type", "step");
            w.WriteNumber("env", env);
            w.WriteNumber("option", option);
            w.WriteNumber("native_steps", s.NativeSteps);
            w.WriteEndObject();
        }
    }

    private static (FakeOriginal, GuardedDispatcher, Func<int>) Rig(params int[] envs)
    {
        var orig = new FakeOriginal();
        foreach (var e in envs)
            orig.Sessions[e] = new FakeSession(e);
        int inspections = 0;
        // Fake predicate: refuses exactly the Pillage play (marker), counts every inspection.
        var d = new GuardedDispatcher(orig, (_, o) => { inspections++; return o.CardDefId == IC.Pillage ? "fake refusal" : null; }, null);
        return (orig, d, () => inspections);
    }

    private static JsonElement Cmd(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Run(GuardedDispatcher d, string json)
    {
        var cmd = Cmd(json);
        return Encoding.UTF8.GetString(Import.Serialize(w => d.Dispatch(cmd.GetProperty("cmd").GetString()!, cmd, w)));
    }

    private static void RoutingCases()
    {
        {
            // Allowed: exactly one original call with the identical request; reply bytes are the original's.
            var (orig, d, insp) = Rig(0);
            string json = """{"cmd":"step","env":0,"decision":1,"option":1}""";
            string got = Run(d, json);
            var (orig2, _, _) = Rig(0);
            var cmd = Cmd(json);
            string direct = Encoding.UTF8.GetString(Import.Serialize(w => orig2.Dispatch("step", cmd, w)));
            Check("routing:allowed_delegated_once_identical_request", orig.Calls.Count == 1 && orig.Calls[0] == $"step {json}", string.Join(";", orig.Calls));
            Check("routing:allowed_reply_identical_to_original", got == direct, $"{got} vs {direct}");
            Check("routing:allowed_one_native_step_one_inspection", orig.Sessions[0].NativeSteps == 1 && insp() == 1);
        }
        {
            // Refused: no original call, zero native steps, session closed, later steps refused by the original's own check.
            var (orig, d, insp) = Rig(0);
            string got = Run(d, """{"cmd":"step","env":0,"decision":1,"option":2}""");
            Check("routing:refused_zero_original_calls", orig.Calls.Count == 0, string.Join(";", orig.Calls));
            Check("routing:refused_zero_native_steps", orig.Sessions[0].NativeSteps == 0 && orig.Sessions[0].Refusals == 1);
            Check("routing:refused_reply_written", got.Contains("\"refused\":true") && got.Contains("\"status\":\"unsupported\""), got);
            Throws<InvalidOperationException>("routing:step_after_refusal_rejected_by_original",
                () => Run(d, """{"cmd":"step","env":0,"decision":1,"option":0}"""), "is unsupported; reset first");
            Check("routing:step_after_refusal_not_inspected", insp() == 1 && orig.Sessions[0].NativeSteps == 0);
        }
        {
            // Stale / invalid requests are never inspected: the original throws its own exception.
            var (orig, d, insp) = Rig(0);
            Throws<InvalidOperationException>("routing:stale_decision_original_error", () => Run(d, """{"cmd":"step","env":0,"decision":0,"option":2}"""), "stale decision");
            Throws<ArgumentOutOfRangeException>("routing:option_out_of_range_original_error", () => Run(d, """{"cmd":"step","env":0,"decision":1,"option":3}"""));
            Throws<ArgumentOutOfRangeException>("routing:negative_option_original_error", () => Run(d, """{"cmd":"step","env":0,"decision":1,"option":-1}"""));
            Throws<KeyNotFoundException>("routing:unknown_env_original_error", () => Run(d, """{"cmd":"step","env":9,"decision":1,"option":2}"""), "not reset");
            Check("routing:invalid_requests_not_inspected_no_steps", insp() == 0 && orig.Sessions[0].NativeSteps == 0 && orig.Sessions[0].Refusals == 0);
            orig.Sessions[0].State = null;
            Run(d, """{"cmd":"step","env":0,"decision":1,"option":1}""");
            Check("routing:no_fight_state_delegated_uninspected", insp() == 0 && orig.Sessions[0].NativeSteps == 1);
        }
        {
            // step_many: every member guarded in order; a refusal skips nothing after it.
            var (orig, d, insp) = Rig(0, 1, 2);
            string got = Run(d, """{"cmd":"step_many","steps":[{"env":0,"decision":1,"option":1},{"env":1,"decision":1,"option":2},{"env":2,"decision":1,"option":0}]}""");
            using var doc = JsonDocument.Parse(got);
            var results = doc.RootElement.GetProperty("results");
            Check("routing:batch_envelope", doc.RootElement.GetProperty("type").GetString() == "step_many" && results.GetArrayLength() == 3, got);
            Check("routing:batch_order_preserved", results.GetArrayLength() == 3 && results[0].GetProperty("env").GetInt32() == 0
                && results[1].GetProperty("env").GetInt32() == 1 && results[2].GetProperty("env").GetInt32() == 2, got);
            Check("routing:batch_members_after_refusal_stepped", orig.Sessions[0].NativeSteps == 1 && orig.Sessions[1].NativeSteps == 0
                && orig.Sessions[2].NativeSteps == 1 && orig.Sessions[1].Refusals == 1, got);
            Check("routing:batch_every_member_inspected_once", insp() == 3 && orig.Calls.Count == 2);
            // A refused member then a valid one for the same env in one batch: the second hits the original's own check.
            var (o2, d2, i2) = Rig(0);
            Throws<InvalidOperationException>("routing:batch_duplicate_env_after_refusal_original_error",
                () => Run(d2, """{"cmd":"step_many","steps":[{"env":0,"decision":1,"option":2},{"env":0,"decision":1,"option":0}]}"""), "reset first");
            Check("routing:batch_duplicate_env_zero_native_steps", o2.Sessions[0].NativeSteps == 0 && i2() == 1);
            // A stale member fails the command as in the original (earlier members already ran there too).
            var (o3, d3, _) = Rig(0, 1, 2);
            Throws<InvalidOperationException>("routing:batch_stale_member_original_error",
                () => Run(d3, """{"cmd":"step_many","steps":[{"env":0,"decision":1,"option":1},{"env":1,"decision":7,"option":1},{"env":2,"decision":1,"option":1}]}"""), "stale decision");
            Check("routing:batch_stale_member_original_semantics", o3.Sessions[0].NativeSteps == 1 && o3.Sessions[1].NativeSteps == 0 && o3.Sessions[2].NativeSteps == 0);
        }
        {
            // Every other command passes through untouched.
            var (orig, d, insp) = Rig(0);
            foreach (var name in new[] { "hello", "add_template", "reset", "close", "engine_dump", "nonsense" })
                Run(d, $$"""{"cmd":"{{name}}","env":0}""");
            Check("routing:other_commands_pass_through", orig.Calls.Count == 6 && insp() == 0 && orig.Sessions[0].NativeSteps == 0, string.Join(";", orig.Calls));
        }
        {
            // The real compiled guard through the routing: class A refused, near miss delegated.
            var orig = new FakeOriginal();
            var s = new FakeSession(0) { State = ClassA() };
            s.Options = [Play(0, IC.Pillage), Play(1, IC.StrikeIronclad)];
            orig.Sessions[0] = s;
            var d = new GuardedDispatcher(orig, Guard.Check, null);
            Run(d, """{"cmd":"step","env":0,"decision":1,"option":1}""");
            Check("routing:real_guard_near_miss_delegated", s.NativeSteps == 1 && s.Refusals == 0);
            s.Decision = 2;
            string got = Run(d, """{"cmd":"step","env":0,"decision":2,"option":0}""");
            Check("routing:real_guard_class_a_refused_zero_steps", s.NativeSteps == 1 && s.Refusals == 1 && got.Contains("Waterfall Giant"), got);
        }
        {
            var obj = Encoding.UTF8.GetBytes("""{"ok":true,"x":1}""");
            var spliced = Encoding.UTF8.GetString(GuardedProgram.AppendProperty(obj, "battle_guard", Encoding.UTF8.GetBytes("""{"a":"\"q\""}""")));
            Check("routing:append_property", spliced == """{"ok":true,"x":1,"battle_guard":{"a":"\"q\""}}""", spliced);
            Throws<InvalidOperationException>("routing:append_property_rejects_non_object", () => GuardedProgram.AppendProperty(Encoding.UTF8.GetBytes("[1]"), "k", Encoding.UTF8.GetBytes("1")));
        }
    }

    // ------------------------------------------------------------------ (3) the real original, no fight built

    private static void OriginalCases()
    {
        var c = OriginalContract.Bind();
        Check("original:contract_bound", c.Lines.Count == 17, string.Join("\n", c.Lines));
        var identity = GuardedProgram.Identity(c);
        using (var id = JsonDocument.Parse(identity))
            Check("original:identity_guard_sha", id.RootElement.GetProperty("guard_source_sha256").GetString() == GuardedProgram.ReviewedGuardSha256);

        // A real Session, constructed but never reset: Fight stays null, so nothing can reach the engine.
        var ctor = c.SessionType.GetConstructor([typeof(int), typeof(string), typeof(BattleInput), typeof(ulong), typeof(EnvironmentRngMode),
            typeof(int), typeof(int), typeof(bool), typeof(bool)]) ?? throw new MissingMethodException("Session .ctor");
        var input = new BattleInput { PlayerHp = 50, PlayerMaxHp = 80 };
        var session = ctor.Invoke([41, "selftest", input, 7UL, EnvironmentRngMode.Training, 100, 100, false, true]);
        var table = (IDictionary)c.Sessions.GetValue(null)!;
        table[41] = session;
        var worker = new OriginalWorker(c);
        var d = new GuardedDispatcher(worker, (_, _) => throw new InvalidOperationException("guard must not be inspected"), identity);

        // Valid env, empty legal list: the request is invalid, delegated, and the ORIGINAL Session.Step throws.
        Throws<ArgumentOutOfRangeException>("original:invalid_option_rejected_by_original_step", () => Run(d, """{"cmd":"step","env":41,"decision":0,"option":0}"""), "outside 0..-1");
        Throws<InvalidOperationException>("original:stale_decision_rejected_by_original_step", () => Run(d, """{"cmd":"step","env":41,"decision":5,"option":0}"""), "stale decision 5");
        Throws<KeyNotFoundException>("original:unknown_env_rejected_by_original", () => Run(d, """{"cmd":"step_many","steps":[{"env":42,"decision":0,"option":0}]}"""), "env 42 not reset");

        // The refusal path on the real Session: pinned fields set, original WriteResponse writes the reply.
        var real = (OriginalSession)worker.Find(41)!;
        var reply = Import.Serialize(w => real.RefuseAndWrite(w, 0, 0, Play(0, IC.Pillage), "selftest evidence"));
        using (var doc = JsonDocument.Parse(reply))
        {
            var r = doc.RootElement;
            Check("original:refusal_status", r.GetProperty("status").GetString() == "unsupported" && r.GetProperty("reason").GetString() == "limit:battle_unbounded_autoplay_loop",
                Encoding.UTF8.GetString(reply));
            Check("original:refusal_zero_steps_decision_unchanged", r.GetProperty("steps").GetInt32() == 0 && r.GetProperty("decision").GetInt32() == 0
                && r.GetProperty("step").GetProperty("terminal").GetBoolean() == false);
            Check("original:refusal_error_and_block", r.GetProperty("error").GetString()!.StartsWith("battle_unbounded_autoplay_loop: selftest evidence")
                && r.GetProperty("battle_guard").GetProperty("refused").GetBoolean() && !r.GetProperty("battle_guard").GetProperty("engine_call").GetBoolean());
            Check("original:refusal_reply_shape", r.GetProperty("type").GetString() == "step" && r.GetProperty("env").GetInt32() == 41 && r.GetProperty("allow_potions").GetBoolean());
        }
        Throws<InvalidOperationException>("original:closed_session_rejected_by_original_step",
            () => Run(d, """{"cmd":"step","env":41,"decision":0,"option":0}"""), "env 41 is unsupported; reset first");

        // hello: the original Hello unchanged, plus the separate battle_guard identity.
        var hello = Run(d, """{"cmd":"hello"}""");
        using (var h = JsonDocument.Parse(hello))
        {
            var r = h.RootElement;
            Check("original:hello_original_fields", r.GetProperty("protocol").GetString() == RlCombatWorker.Program.ProtocolVersion
                && r.GetProperty("rl_worker_assembly_sha256").GetString() == GuardedProgram.ExpectedDependencySha256["RlCombatWorker.dll"]
                && r.GetProperty("engine_assembly_sha256").GetString() == GuardedProgram.ExpectedDependencySha256["Sts2Emulator.dll"]
                && r.GetProperty("cds_worker_assembly_sha256").GetString() == GuardedProgram.ExpectedDependencySha256["CombatWorker.dll"]);
            Check("original:hello_battle_guard_last", r.EnumerateObject().Last().Name == "battle_guard"
                && r.GetProperty("battle_guard").GetProperty("schema").GetString() == GuardedProgram.FacadeSchema);
        }
        Run(d, """{"cmd":"close","env":41}""");
        Check("original:close_through_original", !((IDictionary)c.Sessions.GetValue(null)!).Contains(41) && worker.Find(41) is null);
    }
}
