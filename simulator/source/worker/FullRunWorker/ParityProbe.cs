using System.Reflection;
using System.Text.Json;
using CombatWorker;
using Sts2Emulator.Training;

namespace FullRunWorker;

/// <summary>
/// DEBUG ONLY (bridge-equivalence evidence, never used by a run): drives an original
/// <c>RlCombatWorker.Session</c> through its own Reset/Step on a template fight and, after every
/// step, views the SAME <c>CombatState</c> object through <see cref="CombatFacade.FightView" />.
/// The two Session-shaped replies must be byte-identical, and the original's reply can be compared
/// with the separate original worker process for the same template/seed/options.
/// </summary>
public static class ParityProbe
{
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static readonly string[] ReflectedMembers =
    [
        "RlCombatWorker.Session.Reset() [debug parity only]",
        "RlCombatWorker.Session.Step(int,int) [debug parity only]",
        "RlCombatWorker.Session.Fight { get; }",
    ];

    private static readonly MethodInfo ResetMethod = CombatFacade.SessionType.GetMethod("Reset", Inst, null, Type.EmptyTypes, null)
        ?? throw new MissingMethodException("Session.Reset");
    private static readonly MethodInfo StepMethod = CombatFacade.SessionType.GetMethod("Step", Inst, null, [typeof(int), typeof(int)], null)
        ?? throw new MissingMethodException("Session.Step");
    private static readonly MethodInfo WriteMethod = CombatFacade.SessionType.GetMethods(Inst).Single(m => m.Name == "WriteResponse");
    private static readonly PropertyInfo FightProperty = CombatFacade.SessionType.GetProperty("Fight", Inst)!;
    private static readonly FieldInfo DecisionField = CombatFacade.SessionType.GetField("_decision", Inst)!;
    private static readonly FieldInfo StatusField = CombatFacade.SessionType.GetField("_status", Inst)!;
    private static readonly FieldInfo StepsField = CombatFacade.SessionType.GetField("_steps", Inst)!;
    private static readonly FieldInfo LegalField = CombatFacade.SessionType.GetField("_legal", Inst)!;

    private static object? Call(MethodInfo m, object target, object?[] args)
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

    public static void Run(JsonElement cmd, Utf8JsonWriter w)
    {
        var input = TemplateCodec.ReadBattleInput(cmd.GetProperty("engine_input"));
        ulong seed = cmd.GetProperty("env_seed").GetUInt64();
        int maxSteps = cmd.GetProperty("max_steps").GetInt32();
        int maxTurns = cmd.GetProperty("max_turns").GetInt32();
        var options = cmd.GetProperty("options").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        string templateId = cmd.GetProperty("template_id").GetString()!;
        int env = cmd.TryGetProperty("env", out var e) ? e.GetInt32() : 0;

        var ctor = CombatFacade.SessionType.GetConstructors(Inst).Single(c => c.GetParameters().Length == 9);
        object original = ctor.Invoke([env, templateId, input, seed, EnvironmentRngMode.Training, maxSteps, maxTurns, false, true]);
        Call(ResetMethod, original, []);
        var fight = (TrainingCombat)FightProperty.GetValue(original)!;
        var view = new CombatFacade.FightView(env, templateId, input, fight.State, fight.CombatRng, maxSteps, maxTurns, false, seed);

        w.WriteStartObject();
        w.WriteBoolean("ok", true);
        w.WriteString("type", "parity_probe");
        w.WritePropertyName("decisions");
        w.WriteStartArray();
        int k = 0;
        object? info = null;
        while ((string)StatusField.GetValue(original)! == "running")
        {
            view.Refresh((int)StepsField.GetValue(original)!);
            byte[] originalNoInfo = Import.Serialize(x => Call(WriteMethod, original, [x, k == 0 ? "reset" : "step", null]));
            byte[] originalReply = Import.Serialize(x => Call(WriteMethod, original, [x, k == 0 ? "reset" : "step", info]));
            byte[] facade = view.WriteReply(k == 0 ? "reset" : "step");
            w.WriteStartObject();
            w.WriteNumber("k", k);
            w.WriteBoolean("same_combat_object", ReferenceEquals(view.Combat, fight.State));
            w.WriteBoolean("identical", originalNoInfo.AsSpan().SequenceEqual(facade));
            w.WriteBoolean("same_decision", view.Decision == (int)DecisionField.GetValue(original)!);
            w.WritePropertyName("original");
            w.WriteRawValue(originalReply, skipInputValidation: true);
            w.WritePropertyName("facade");
            w.WriteRawValue(facade, skipInputValidation: true);
            w.WriteEndObject();
            var legal = (List<Option>)LegalField.GetValue(original)!;
            if (k >= options.Length || legal.Count == 0)
                break;
            int option = Math.Min(options[k], legal.Count - 1);
            info = Call(StepMethod, original, [(int)DecisionField.GetValue(original)!, option]);
            k++;
        }
        w.WriteEndArray();
        string final = (string)StatusField.GetValue(original)!;
        w.WriteString("final_status", final);
        if (final is "win" or "loss" or "terminal_other")
        {
            // The original Session's own terminal reply vs the facade's terminal writer on the SAME object.
            byte[] originalNoInfo = Import.Serialize(x => Call(WriteMethod, original, [x, "step", null]));
            byte[] originalReply = Import.Serialize(x => Call(WriteMethod, original, [x, "step", info]));
            byte[] facade = view.WriteTerminal(final == "win", (int)StepsField.GetValue(original)!);
            w.WritePropertyName("terminal");
            w.WriteStartObject();
            w.WriteBoolean("identical", originalNoInfo.AsSpan().SequenceEqual(facade));
            w.WritePropertyName("original");
            w.WriteRawValue(originalReply, skipInputValidation: true);
            w.WritePropertyName("facade");
            w.WriteRawValue(facade, skipInputValidation: true);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }
}
