using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CombatWorker;
using Sts2Emulator.Core;
using Sts2Emulator.Training;

namespace GuardedBattleWorker;

/// <summary>
/// Isolated-battle guard facade over the ORIGINAL prebuilt build08 <c>RlCombatWorker.dll</c>
/// (act1ft). It speaks the original JSON-lines protocol unchanged. Every command except
/// <c>step</c> / <c>step_many</c> goes to the original private <c>Program.Dispatch</c>. Each
/// step entry is checked against the reviewed <see cref="FullRunWorker.AutoplayLoopGuard" />
/// (class A + class B, byte-identical source) on the exact live <see cref="CombatState" />
/// before the original <c>Session.Step</c> runs.
/// </summary>
/// <remarks>
/// <para>An allowed entry is delegated to the original <c>Dispatch("step", entry)</c>. The
/// original then re-checks the request and executes exactly the option it was given. Masks,
/// options, RNG, observations and rules are the original's.</para>
/// <para>A refused entry gets no engine call and no RNG use. The original session is closed
/// as <c>unsupported</c> / <c>limit:battle_unbounded_autoplay_loop</c> through its own pinned
/// fields, and its own <c>WriteResponse</c> writes the reply plus a <c>battle_guard</c>
/// evidence block. The client excludes the whole episode. The facade never removes an option
/// and never picks a different one.</para>
/// <para>Stale or invalid requests (env not reset, closed env, stale decision, option out of
/// range) are never inspected. They are delegated, so the original throws its own exception.</para>
/// </remarks>
public static class GuardedProgram
{
    public const string FacadeSchema = "act1ft/guarded_battle_worker/v1";
    public const string LimitReason = "battle_unbounded_autoplay_loop";
    public const string RefusalStatus = "unsupported";
    public const string RefusalReason = "limit:" + LimitReason;
    public const string ReviewedGuardSha256 = "8a43e794432ebf2d0e8aad88d3150596be15bb523f9bfd1f5b9e3c4c49c64546";
    public const string GuardResource = "act1ft.AutoplayLoopGuard.cs";
    public const int StartupRefusedExitCode = 3;

    /// <summary>The frozen build08 assemblies frozen E's provenance names (run_strategy_v1.build pins).</summary>
    public static readonly IReadOnlyDictionary<string, string> ExpectedDependencySha256 = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["CombatWorker.dll"] = "ab408a807b1837c5da2e17b6785ca7ce18e49a78452271538bf33a1cbbe7cfb9",
        ["RlCombatWorker.dll"] = "b49a635d51cfc68c5e0997342e96ee2a21fb2076e5f406c150194198ff018369",
        ["Sts2Emulator.dll"] = "11d614a31ea796f5631cf163d876db56b5129c38b9a5e98e7ee7f1632f6b2b9b",
        ["System.IO.Hashing.dll"] = "fbe8c2fcb4adc01da5c3c8ed0d727903b8fda1e6b84da3c447c9794615f30664",
    };

    public static int Main(string[] args)
    {
        if (args is ["--selftest"])
            return SelfTest.Run();
        OriginalContract contract;
        byte[] identity;
        try
        {
            contract = OriginalContract.Bind();
            identity = Identity(contract);
        }
        catch (Exception ex)
        {
            // Fail closed before reading any command: wrong bytes or a changed original shape.
            Console.Error.WriteLine($"act1ft guarded worker refused to start: {ex.GetType().FullName}: {ex.Message}");
            return StartupRefusedExitCode;
        }
        if (args is ["--describe"])
        {
            Console.Out.WriteLine(Encoding.UTF8.GetString(identity));
            return 0;
        }
        var dispatcher = new GuardedDispatcher(new OriginalWorker(contract), FullRunWorker.AutoplayLoopGuard.Check, identity);

        // The original Program.Main loop, unchanged except that Dispatch is the facade's.
        var output = new BufferedStream(Console.OpenStandardOutput(), 1 << 20);
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
                    Write(output, Import.Serialize(w => { w.WriteStartObject(); w.WriteBoolean("ok", true); w.WriteString("type", "quit"); w.WriteEndObject(); }));
                    output.Flush();
                    return 0;
                }
                response = Import.Serialize(w => dispatcher.Dispatch(cmdName, cmd, w));
            }
            catch (Exception ex)
            {
                response = Import.Serialize(w =>
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
            Write(output, response);
        }
        output.Flush();
        return 0;
    }

    private static void Write(Stream output, byte[] bytes)
    {
        output.Write(bytes);
        output.WriteByte((byte)'\n');
        output.Flush();
    }

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string Sha256File(string path) => Sha256Hex(File.ReadAllBytes(path));

    /// <summary>The embedded copy of the compiled guard source (same file as the Compile item).</summary>
    internal static byte[] GuardSource()
    {
        using var s = typeof(GuardedProgram).Assembly.GetManifestResourceStream(GuardResource)
            ?? throw new InvalidOperationException($"embedded {GuardResource} missing");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>
    /// The facade's own identity, reported in hello under <c>battle_guard</c>. The original hello
    /// fields stay exactly what the original <c>Program.Hello</c> writes (they name the original
    /// assemblies). Refuses wrong dependency bytes, a different loaded copy, or a guard source that
    /// is not the reviewed one.
    /// </summary>
    internal static byte[] Identity(OriginalContract c)
    {
        string baseDir = AppContext.BaseDirectory;
        var deps = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, want) in ExpectedDependencySha256)
        {
            string got = Sha256File(Path.Combine(baseDir, name));
            if (got != want)
                throw new InvalidOperationException($"{name} sha256 {got} != pinned {want}");
            deps[name] = got;
        }
        foreach (var asm in new[] { typeof(RlCombatWorker.Program).Assembly, typeof(Episode).Assembly, typeof(CombatEngine).Assembly })
        {
            string expected = Path.GetFullPath(Path.Combine(baseDir, Path.GetFileName(asm.Location)));
            if (Path.GetFullPath(asm.Location) != expected)
                throw new InvalidOperationException($"{asm.GetName().Name} loaded from {asm.Location}, not {expected}");
        }
        string guardSha = Sha256Hex(GuardSource());
        if (guardSha != ReviewedGuardSha256)
            throw new InvalidOperationException($"compiled guard source sha256 {guardSha} != reviewed {ReviewedGuardSha256}");
        var facade = typeof(GuardedProgram).Assembly;
        return Import.Serialize(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", FacadeSchema);
            w.WriteString("role", "isolated-battle guard facade; every other hello field is written unchanged by the original "
                + "RlCombatWorker Program.Hello and names the original assemblies");
            w.WriteString("facade_assembly_sha256", Sha256File(facade.Location));
            w.WriteString("facade_assembly_mvid", facade.ManifestModule.ModuleVersionId.ToString());
            w.WriteString("guard_source_sha256", guardSha);
            w.WriteString("guard_type", typeof(FullRunWorker.AutoplayLoopGuard).FullName);
            w.WritePropertyName("guard_classes");
            w.WriteStartArray();
            w.WriteStringValue("A:sentinel_hp");
            w.WriteStringValue("B:steam_eruption_revive");
            w.WriteEndArray();
            w.WritePropertyName("guarded_commands");
            w.WriteStartArray();
            w.WriteStringValue("step");
            w.WriteStringValue("step_many");
            w.WriteEndArray();
            w.WritePropertyName("refusal");
            w.WriteStartObject();
            w.WriteString("status", RefusalStatus);
            w.WriteString("reason", RefusalReason);
            w.WriteString("limit_reason", LimitReason);
            w.WriteBoolean("engine_call", false);
            w.WriteBoolean("rng_use", false);
            w.WriteBoolean("mask_or_option_changed", false);
            w.WriteBoolean("substitute_action", false);
            w.WriteBoolean("session_steps_incremented", false);
            w.WriteEndObject();
            w.WritePropertyName("dependencies_sha256");
            w.WriteStartObject();
            foreach (var (k, v) in deps)
                w.WriteString(k, v);
            w.WriteEndObject();
            w.WriteString("rl_worker_assembly_mvid", typeof(RlCombatWorker.Program).Assembly.ManifestModule.ModuleVersionId.ToString());
            w.WritePropertyName("reflection_contract");
            w.WriteStartArray();
            foreach (var l in c.Lines)
                w.WriteStringValue(l);
            w.WriteEndArray();
            w.WriteString("reflection_contract_sha256", Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", c.Lines))));
            w.WritePropertyName("original_il_sha256");
            w.WriteStartObject();
            foreach (var (k, v) in c.IlSha256)
                w.WriteString(k, v);
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    /// <summary>Insert <c>"name": value</c> as the last property of a serialized JSON object.</summary>
    internal static byte[] AppendProperty(byte[] obj, string name, byte[] value)
    {
        if (obj.Length < 2 || obj[0] != (byte)'{' || obj[^1] != (byte)'}')
            throw new InvalidOperationException("not a serialized JSON object");
        var head = Import.Serialize(w => w.WriteStringValue(name)); // the escaped, quoted name
        using var m = new MemoryStream(obj.Length + head.Length + value.Length + 3);
        m.Write(obj, 0, obj.Length - 1);
        if (obj.Length > 2)
            m.WriteByte((byte)',');
        m.Write(head);
        m.WriteByte((byte)':');
        m.Write(value);
        m.WriteByte((byte)'}');
        var bytes = m.ToArray();
        using (JsonDocument.Parse(bytes)) { } // must still be one valid object
        return bytes;
    }
}

/// <summary>What the facade needs from one original session (a seam so routing is testable with fakes).</summary>
public interface IGuardSession
{
    string Status { get; }
    int Decision { get; }
    int LegalCount { get; }
    Option Legal(int ordinal);
    CombatState? State { get; }

    /// <summary>Close the episode as refused, without an engine call, and write its step reply.</summary>
    void RefuseAndWrite(Utf8JsonWriter w, int decision, int option, Option o, string evidence);
}

public interface IOriginalWorker
{
    /// <summary>The original session for <paramref name="env" />; null when the original has none.</summary>
    IGuardSession? Find(int env);

    /// <summary>The original, unmodified <c>Program.Dispatch</c>.</summary>
    void Dispatch(string name, JsonElement cmd, Utf8JsonWriter w);
}

/// <summary>
/// Facade routing: the guard sits only between a valid step request and the original
/// <c>Session.Step</c>. Pure read; the facade keeps no episode counters of its own.
/// </summary>
public sealed class GuardedDispatcher(IOriginalWorker original, Func<CombatState, Option, string?> guard, byte[]? identity)
{
    public void Dispatch(string name, JsonElement cmd, Utf8JsonWriter w)
    {
        switch (name)
        {
            case "hello" when identity is not null:
            {
                var hello = Import.Serialize(h => original.Dispatch(name, cmd, h));
                w.WriteRawValue(GuardedProgram.AppendProperty(hello, "battle_guard", identity), skipInputValidation: false);
                break;
            }
            case "step":
                StepOne(cmd, w);
                break;
            case "step_many":
            {
                // The original's step_many envelope; every entry is guarded, in order, none skipped.
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteString("type", "step_many");
                w.WritePropertyName("results");
                w.WriteStartArray();
                foreach (var e in cmd.GetProperty("steps").EnumerateArray())
                    StepOne(e, w);
                w.WriteEndArray();
                w.WriteEndObject();
                break;
            }
            default:
                original.Dispatch(name, cmd, w);
                break;
        }
    }

    /// <summary>
    /// One step request (a <c>step</c> command or a <c>step_many</c> entry: both carry env /
    /// decision / option, and the original writes the same per-env reply for both).
    /// </summary>
    private void StepOne(JsonElement e, Utf8JsonWriter w)
    {
        // Same reads in the same order as the original: env first, then decision and option.
        var session = original.Find(e.GetProperty("env").GetInt32());
        if (session is null)
        {
            original.Dispatch("step", e, w); // the original throws its own "env N not reset"
            return;
        }
        int decision = e.GetProperty("decision").GetInt32();
        int option = e.GetProperty("option").GetInt32();
        // The original Session.Step's own validity checks. Any failure is delegated before any
        // inspection, so the original throws its own exception and nothing is refused.
        if (session.Status != "running" || decision != session.Decision || option < 0 || option >= session.LegalCount
            || session.State is not { } state)
        {
            original.Dispatch("step", e, w);
            return;
        }
        var o = session.Legal(option);
        if (guard(state, o) is { } evidence)
        {
            session.RefuseAndWrite(w, decision, option, o, evidence);
            return;
        }
        original.Dispatch("step", e, w);
    }
}

/// <summary>The original worker's static session table and Dispatch, through the pinned contract.</summary>
internal sealed class OriginalWorker(OriginalContract c) : IOriginalWorker
{
    public IGuardSession? Find(int env)
    {
        var table = (IDictionary)c.Sessions.GetValue(null)!;
        return table.Contains(env) ? new OriginalSession(c, table[env]!) : null;
    }

    public void Dispatch(string name, JsonElement cmd, Utf8JsonWriter w) => c.Dispatch(name, cmd, w);
}

/// <summary>One original <c>RlCombatWorker.Session</c>, read and closed through its pinned members.</summary>
internal sealed class OriginalSession(OriginalContract c, object target) : IGuardSession
{
    public string Status => (string)c.Status.GetValue(target)!;
    public int Decision => (int)c.DecisionField.GetValue(target)!;
    private IList LegalList => (IList)c.Legal.GetValue(target)!;
    public int LegalCount => LegalList.Count;
    public Option Legal(int ordinal) => (Option)LegalList[ordinal]!;
    public CombatState? State => ((TrainingCombat?)c.Fight.GetValue(target))?.State;
    public int Steps => (int)c.Steps.GetValue(target)!;

    public void RefuseAndWrite(Utf8JsonWriter w, int decision, int option, Option o, string evidence)
    {
        int turn = State?.Turn ?? 0;
        int steps = Steps;
        // Mirrors the original's engine_exception close minus the engine call: no _steps++, no
        // _decision++; the legal list is emptied as for every closed status.
        c.Status.SetValue(target, GuardedProgram.RefusalStatus);
        c.Reason.SetValue(target, GuardedProgram.RefusalReason);
        c.Error.SetValue(target, $"{GuardedProgram.LimitReason}: {evidence}");
        c.Legal.SetValue(target, new List<Option>());
        var info = c.NewStepInfo(decision, option, o.Kind, o.Action, o.Target, turn, turn, false, false, 0f);
        var reply = Import.Serialize(r => c.WriteResponse(target, r, "step", info));
        var block = Import.Serialize(g =>
        {
            g.WriteStartObject();
            g.WriteBoolean("refused", true);
            g.WriteString("limit_reason", GuardedProgram.LimitReason);
            g.WriteString("guard_source_sha256", GuardedProgram.ReviewedGuardSha256);
            g.WriteNumber("decision", decision);
            g.WriteNumber("option", option);
            g.WriteString("kind", o.Kind);
            g.WriteNumber("action", o.Action);
            g.WriteNumber("target", o.Target);
            g.WriteNumber("hand_index", o.HandIndex);
            g.WriteNumber("card_def_id", o.CardDefId);
            g.WriteBoolean("engine_call", false);
            g.WriteNumber("session_steps", steps);
            g.WriteString("evidence", evidence);
            g.WriteEndObject();
        });
        w.WriteRawValue(GuardedProgram.AppendProperty(reply, "battle_guard", block), skipInputValidation: false);
    }
}

/// <summary>
/// The exact members of the original worker the facade uses, bound by exact signature. Any
/// difference (name, visibility, static-ness, parameter or field types) refuses startup.
/// </summary>
internal sealed class OriginalContract
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    public required Action<string, JsonElement, Utf8JsonWriter> Dispatch { get; init; }
    public required FieldInfo Sessions { get; init; }
    public required FieldInfo Status { get; init; }
    public required FieldInfo Reason { get; init; }
    public required FieldInfo Error { get; init; }
    public required FieldInfo Legal { get; init; }
    public required FieldInfo DecisionField { get; init; }
    public required FieldInfo Steps { get; init; }
    public required PropertyInfo Fight { get; init; }
    public required MethodInfo Step { get; init; }
    public required MethodInfo WriteResponseMethod { get; init; }
    public required ConstructorInfo StepInfoCtor { get; init; }
    public required Type SessionType { get; init; }
    public required List<string> Lines { get; init; }
    public required SortedDictionary<string, string> IlSha256 { get; init; }

    public object NewStepInfo(int decision, int option, string kind, int action, int target, int turnBefore, int turnAfter,
        bool terminal, bool playerWon, float engineReward) =>
        StepInfoCtor.Invoke([decision, option, kind, action, target, turnBefore, turnAfter, terminal, playerWon, engineReward]);

    public void WriteResponse(object session, Utf8JsonWriter w, string type, object? info)
    {
        try
        {
            WriteResponseMethod.Invoke(session, [w, type, info]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    public static OriginalContract Bind()
    {
        var lines = new List<string>();
        var program = typeof(RlCombatWorker.Program);
        var asm = program.Assembly;
        Require(program.IsAbstract && program.IsSealed && program.IsPublic, "RlCombatWorker.Program is not a public static class");
        var session = asm.GetType("RlCombatWorker.Session", throwOnError: true)!;
        Require(session.IsClass && session.IsSealed && session.IsNotPublic, "RlCombatWorker.Session is not an internal sealed class");
        var stepInfo = asm.GetType("RlCombatWorker.StepInfo", throwOnError: true)!;
        Require(stepInfo.IsClass && stepInfo.IsSealed && stepInfo.IsNotPublic, "RlCombatWorker.StepInfo is not an internal sealed record");
        lines.Add("type RlCombatWorker.Program public static");
        lines.Add("type RlCombatWorker.Session internal sealed");
        lines.Add("type RlCombatWorker.StepInfo internal sealed");

        var dispatch = program.GetMethod("Dispatch", PrivateStatic, [typeof(string), typeof(JsonElement), typeof(Utf8JsonWriter)])
            ?? throw new MissingMethodException("RlCombatWorker.Program", "Dispatch(string, JsonElement, Utf8JsonWriter)");
        Require(dispatch.IsPrivate && dispatch.ReturnType == typeof(void), "Program.Dispatch is not private static void");
        lines.Add(Describe(dispatch, "private static"));

        var sessions = program.GetField("Sessions", PrivateStatic) ?? throw new MissingFieldException("RlCombatWorker.Program", "Sessions");
        Require(sessions.IsPrivate && sessions.IsInitOnly && sessions.FieldType == typeof(Dictionary<,>).MakeGenericType(typeof(int), session),
            $"Program.Sessions is {sessions.FieldType}, not private static readonly Dictionary<int, Session>");
        lines.Add(Describe(sessions, "private static readonly"));

        FieldInfo Field(string name, Type type)
        {
            var f = session.GetField(name, PrivateInstance) ?? throw new MissingFieldException("RlCombatWorker.Session", name);
            Require(f.IsPrivate && !f.IsInitOnly && f.FieldType == type, $"Session.{name} is {f.FieldType}, not private mutable {type}");
            lines.Add(Describe(f, "private"));
            return f;
        }
        var status = Field("_status", typeof(string));
        var reason = Field("_reason", typeof(string));
        var error = Field("_error", typeof(string));
        var legal = Field("_legal", typeof(List<Option>));
        var decision = Field("_decision", typeof(int));
        var steps = Field("_steps", typeof(int));

        var fight = session.GetProperty("Fight", PublicInstance) ?? throw new MissingMemberException("RlCombatWorker.Session", "Fight");
        Require(fight.PropertyType == typeof(TrainingCombat) && fight.GetMethod is { IsPublic: true }, "Session.Fight is not a public TrainingCombat getter");
        lines.Add($"property RlCombatWorker.Session::Fight public {fight.PropertyType.FullName}");
        var state = typeof(TrainingCombat).GetProperty("State", PublicInstance);
        Require(state is not null && state.PropertyType == typeof(CombatState), "TrainingCombat.State is not a public CombatState");
        lines.Add($"property {typeof(TrainingCombat).FullName}::State public {typeof(CombatState).FullName}");

        var step = session.GetMethod("Step", PublicInstance, [typeof(int), typeof(int)]) ?? throw new MissingMethodException("RlCombatWorker.Session", "Step(int, int)");
        Require(step.ReturnType == stepInfo, "Session.Step does not return StepInfo");
        lines.Add(Describe(step, "public"));
        var write = session.GetMethod("WriteResponse", PublicInstance, [typeof(Utf8JsonWriter), typeof(string), stepInfo])
            ?? throw new MissingMethodException("RlCombatWorker.Session", "WriteResponse(Utf8JsonWriter, string, StepInfo)");
        Require(write.ReturnType == typeof(void), "Session.WriteResponse is not void");
        lines.Add(Describe(write, "public"));

        Type[] infoParams = [typeof(int), typeof(int), typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(float)];
        string[] infoNames = ["Decision", "Option", "Kind", "Action", "Target", "TurnBefore", "TurnAfter", "Terminal", "PlayerWon", "EngineReward"];
        var ctor = stepInfo.GetConstructor(PublicInstance, infoParams) ?? throw new MissingMethodException("RlCombatWorker.StepInfo", ".ctor");
        Require(ctor.GetParameters().Select(p => p.Name).SequenceEqual(infoNames), "StepInfo constructor parameter names changed");
        lines.Add($"ctor RlCombatWorker.StepInfo({string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"))})");

        var guard = typeof(FullRunWorker.AutoplayLoopGuard).GetMethod("Check", BindingFlags.Public | BindingFlags.Static, [typeof(CombatState), typeof(Option)]);
        Require(guard is not null && guard.ReturnType == typeof(string), "AutoplayLoopGuard.Check(CombatState, Option) -> string? missing");
        lines.Add(Describe(guard!, "public static"));

        var il = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["RlCombatWorker.Program::Dispatch"] = IlHash(dispatch),
            ["RlCombatWorker.Session::Step"] = IlHash(step),
            ["RlCombatWorker.Session::WriteResponse"] = IlHash(write),
            ["FullRunWorker.AutoplayLoopGuard::Check"] = IlHash(guard!),
        };
        return new OriginalContract
        {
            Dispatch = dispatch.CreateDelegate<Action<string, JsonElement, Utf8JsonWriter>>(),
            Sessions = sessions, Status = status, Reason = reason, Error = error, Legal = legal, DecisionField = decision,
            Steps = steps, Fight = fight, Step = step, WriteResponseMethod = write, StepInfoCtor = ctor, SessionType = session,
            Lines = lines, IlSha256 = il,
        };
    }

    private static string Describe(MethodInfo m, string modifiers) =>
        $"method {m.DeclaringType!.FullName}::{m.Name} {modifiers} {Name(m.ReturnType)} ({string.Join(", ", m.GetParameters().Select(p => Name(p.ParameterType)))})";

    private static string Describe(FieldInfo f, string modifiers) =>
        $"field {f.DeclaringType!.FullName}::{f.Name} {modifiers} {Name(f.FieldType)}";

    /// <summary>A type name without runtime assembly versions (generic arguments spelled out).</summary>
    private static string Name(Type t) =>
        t.IsGenericType ? $"{t.GetGenericTypeDefinition().FullName}<{string.Join(", ", t.GetGenericArguments().Select(Name))}>" : t.FullName!;

    private static string IlHash(MethodInfo m) =>
        GuardedProgram.Sha256Hex(m.GetMethodBody()?.GetILAsByteArray() ?? throw new InvalidOperationException($"{m.Name}: no IL body"));

    private static void Require(bool ok, string message)
    {
        if (!ok)
            throw new InvalidOperationException($"original worker contract: {message}");
    }
}
