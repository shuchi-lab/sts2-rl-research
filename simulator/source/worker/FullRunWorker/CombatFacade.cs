using System.Reflection;
using System.Text.Json;
using CombatWorker;
using Sts2Emulator.Core;
using Sts2Emulator.Training;

namespace FullRunWorker;

/// <summary>
/// The pinned reflection facade over the prebuilt RL worker (RlCombatWorker.dll, build08). It
/// reuses the original worker's private observation code byte for byte: the internal
/// <c>Session</c> class, its private <c>Refresh</c> and its <c>WriteResponse</c>, and the private
/// <c>Program.Hello</c>. Nothing here executes a combat action: <see cref="RunSession" /> passes
/// every action to <c>RunEngine.Step</c>, which owns combat and its post-combat persistence.
/// </summary>
/// <remarks>
/// Every reflected member is resolved once, at startup, by exact name, binding flags, parameter
/// types and return type; any difference throws and the worker refuses to start. The facade
/// deliberately has no handle on <c>Session.Step</c>, <c>Session.Reset</c>,
/// <c>TrainingCombatFactory.Build</c> or <c>TrainingCombatFactory.Step</c>: the member table is
/// reported in hello so a client can verify that, and <see cref="FacadeAudit" /> scans this
/// assembly's own IL for direct calls to them.
/// </remarks>
public static class CombatFacade
{
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static readonly Assembly RlAssembly = typeof(RlCombatWorker.Program).Assembly;
    public static readonly Type SessionType = Pin(RlAssembly.GetType("RlCombatWorker.Session"), "RlCombatWorker.Session");
    private static readonly Type StepInfoType = Pin(RlAssembly.GetType("RlCombatWorker.StepInfo"), "RlCombatWorker.StepInfo");

    private static readonly ConstructorInfo SessionCtor = Pin(
        SessionType.GetConstructor(Inst, null,
            [typeof(int), typeof(string), typeof(BattleInput), typeof(ulong), typeof(EnvironmentRngMode),
             typeof(int), typeof(int), typeof(bool), typeof(bool)], null),
        "Session(int env, string templateId, BattleInput input, ulong envSeed, EnvironmentRngMode mode, int maxSteps, int maxTurns, bool purityCheck, bool allowPotions)");

    private static readonly MethodInfo FightSetter = PinMethod(
        SessionType.GetProperty("Fight", Inst)?.GetSetMethod(nonPublic: true), typeof(void), [typeof(TrainingCombat)],
        "Session.Fight { private set; }");

    private static readonly MethodInfo RefreshMethod = PinMethod(
        SessionType.GetMethod("Refresh", Inst, null, Type.EmptyTypes, null), typeof(void), [], "Session.Refresh()");

    private static readonly MethodInfo WriteResponseMethod = PinMethod(
        SessionType.GetMethod("WriteResponse", Inst, null, [typeof(Utf8JsonWriter), typeof(string), StepInfoType], null),
        typeof(void), [typeof(Utf8JsonWriter), typeof(string), StepInfoType], "Session.WriteResponse(Utf8JsonWriter, string, StepInfo?)");

    private static readonly FieldInfo StepsField = PinField(SessionType, "_steps", typeof(int));
    private static readonly FieldInfo DecisionField = PinField(SessionType, "_decision", typeof(int));
    private static readonly FieldInfo StatusField = PinField(SessionType, "_status", typeof(string));
    private static readonly FieldInfo ReasonField = PinField(SessionType, "_reason", typeof(string));
    private static readonly FieldInfo ErrorField = PinField(SessionType, "_error", typeof(string));
    private static readonly FieldInfo LegalField = PinField(SessionType, "_legal", typeof(List<Option>));

    private static readonly MethodInfo HelloMethod = PinMethod(
        typeof(RlCombatWorker.Program).GetMethod("Hello", Stat, null, [typeof(Utf8JsonWriter)], null),
        typeof(void), [typeof(Utf8JsonWriter)], "RlCombatWorker.Program.Hello(Utf8JsonWriter)");

    /// <summary>Every reflected member, for hello and the facade audit.</summary>
    public static readonly string[] ReflectedMembers =
    [
        "RlCombatWorker.Session..ctor(int,string,BattleInput,ulong,EnvironmentRngMode,int,int,bool,bool)",
        "RlCombatWorker.Session.set_Fight(TrainingCombat) [private setter]",
        "RlCombatWorker.Session.Refresh() [private, read-only enumeration + truncation status]",
        "RlCombatWorker.Session.WriteResponse(Utf8JsonWriter,string,StepInfo?) [writer]",
        "RlCombatWorker.Session._steps:int [bridge-owned battle step counter, written before Refresh]",
        "RlCombatWorker.Session._decision:int [read]",
        "RlCombatWorker.Session._status:string [read]",
        "RlCombatWorker.Session._reason:string [read]",
        "RlCombatWorker.Session._error:string [read]",
        "RlCombatWorker.Session._legal:List<Option> [read: ordinal -> engine action/target]",
        "RlCombatWorker.Session._status/_reason/_legal/_decision [written ONCE when the run engine closes the fight: the original terminal bookkeeping values, so the writer emits the original terminal reply]",
        "RlCombatWorker.Program.Hello(Utf8JsonWriter) [private, nested original identity]",
    ];

    /// <summary>Members the facade must never reach (checked by <see cref="FacadeAudit" />).</summary>
    public static readonly string[] ForbiddenMembers =
    [
        "RlCombatWorker.Session.Step", "RlCombatWorker.Session.Reset",
        "Sts2Emulator.Training.TrainingCombatFactory.Build", "Sts2Emulator.Training.TrainingCombatFactory.Step",
        "Sts2Emulator.Core.Run.RunEngine.ConfirmRestUpgradeSelection",
        "Sts2Emulator.Core.CombatEngine.PlayerIsDead",
    ];

    private static T Pin<T>(T? value, string what) where T : class =>
        value ?? throw new MissingMemberException($"rs1 facade: pinned member {what} not found in {RlAssembly.GetName()}");

    private static MethodInfo PinMethod(MethodInfo? m, Type returns, Type[] parameters, string what)
    {
        if (m is null)
            throw new MissingMethodException($"rs1 facade: pinned member {what} not found");
        var ps = m.GetParameters().Select(p => p.ParameterType).ToArray();
        if (m.ReturnType != returns || !ps.SequenceEqual(parameters))
            throw new MissingMethodException($"rs1 facade: {what} signature changed: {m}");
        return m;
    }

    private static FieldInfo PinField(Type t, string name, Type type)
    {
        var f = t.GetField(name, Inst) ?? throw new MissingFieldException(t.FullName, name);
        if (f.FieldType != type)
            throw new MissingFieldException($"rs1 facade: {t.FullName}.{name} is {f.FieldType}, expected {type}");
        return f;
    }

    /// <summary>Force every static pin (startup check).</summary>
    public static void Verify()
    {
        _ = SessionCtor;
        _ = FightSetter;
        _ = RefreshMethod;
        _ = WriteResponseMethod;
        _ = StepsField;
        _ = LegalField;
        _ = HelloMethod;
    }

    /// <summary>The original worker's hello, written by its own private method.</summary>
    public static void WriteOriginalHello(Utf8JsonWriter w) => Invoke(HelloMethod, null, [w]);

    private static object? Invoke(MethodInfo m, object? target, object?[] args)
    {
        try
        {
            return m.Invoke(target, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// One live run fight, viewed through an original <c>Session</c>. The session never builds,
    /// resets or steps: it wraps the run's own combat state and RNG in a <see cref="TrainingCombat" />
    /// container (no factory call), and its private <c>Refresh</c> re-enumerates and applies the
    /// original truncation rules after each <c>RunEngine.Step</c>.
    /// </summary>
    public sealed class FightView
    {
        private readonly object _session;
        public CombatState Combat { get; }

        /// <param name="envSeed">Only echoed in the reply's <c>env_seed</c> (a run fight has none: 0).</param>
        public FightView(int env, string fightId, BattleInput entry, CombatState combat, CountingRandom combatRng,
            int maxSteps, int maxTurns, bool purityCheck, ulong envSeed = 0UL)
        {
            Combat = combat;
            _session = SessionCtor.Invoke([env, fightId, entry, envSeed, EnvironmentRngMode.Game, maxSteps, maxTurns,
                purityCheck, true]);
            Invoke(FightSetter, _session, [new TrainingCombat(combat, combatRng, 0UL, false, false, EnvironmentRngMode.Game)]);
        }

        /// <summary>Set the bridge-owned battle step count, then re-enumerate (original Refresh).</summary>
        public void Refresh(int bridgeSteps)
        {
            StepsField.SetValue(_session, bridgeSteps);
            Invoke(RefreshMethod, _session, []);
        }

        public int Decision => (int)DecisionField.GetValue(_session)!;
        public string Status => (string)StatusField.GetValue(_session)!;
        public string? Reason => (string?)ReasonField.GetValue(_session);
        public string? Error => (string?)ErrorField.GetValue(_session);
        public IReadOnlyList<Option> Legal => (List<Option>)LegalField.GetValue(_session)!;

        /// <summary>
        /// The original terminal reply for a fight the run's <c>RunEngine.Step</c> just closed: the same
        /// bookkeeping <c>Session.Step</c> applies on a terminal result (status from the engine verdict --
        /// a win first, else non-positive HP is a loss, else terminal_other -- reason engine_terminal, the
        /// bridge-owned step count including the closing step,
        /// no legal options, decision advanced), then the unchanged writer on the retained combat state
        /// (pre post-combat heal, exactly the original terminal boundary).
        /// </summary>
        public byte[] WriteTerminal(bool playerWon, int bridgeSteps)
        {
            string status = playerWon ? "win" : Combat.PlayerHp <= 0 ? "loss" : "terminal_other";
            StepsField.SetValue(_session, bridgeSteps);
            StatusField.SetValue(_session, status);
            ReasonField.SetValue(_session, "engine_terminal");
            LegalField.SetValue(_session, new List<Option>());
            DecisionField.SetValue(_session, Decision + 1);
            return WriteReply("step");
        }

        /// <summary>The complete original Session-shaped reply, as bytes.</summary>
        public byte[] WriteReply(string type) =>
            Import.Serialize(w => Invoke(WriteResponseMethod, _session, [w, type, null]));
    }
}
