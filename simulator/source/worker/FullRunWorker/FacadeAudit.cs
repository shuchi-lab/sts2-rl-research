using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;

namespace FullRunWorker;

/// <summary>
/// Scans this compiled assembly's own IL for direct references to members the production bridge
/// must never call (<see cref="CombatFacade.ForbiddenMembers" />) and reports where each appears.
/// Reflection-invoked members are the pinned table in <see cref="CombatFacade" />; the debug-only
/// <see cref="ParityProbe" /> is reported separately because it drives an original Session.
/// </summary>
public static class FacadeAudit
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>Allowed engine entry points, reported as the scanner's positive control.</summary>
    public static readonly string[] EntryPoints =
    [
        "Sts2Emulator.Core.Run.RunEngine.Step", "Sts2Emulator.Core.Run.RunEngine.UsePotionOutsideCombat",
        "Sts2Emulator.Core.Run.RunEngine.Reset",
    ];

    public static List<(string Caller, string Callee)> References()
    {
        var found = new List<(string, string)>();
        var asm = typeof(FacadeAudit).Assembly;
        foreach (var type in asm.GetTypes())
        {
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                         .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                             | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
            {
                byte[]? il;
                try
                {
                    il = m.GetMethodBody()?.GetILAsByteArray();
                }
                catch (Exception)
                {
                    continue;
                }
                if (il is null)
                    continue;
                foreach (var callee in Callees(m, il))
                    found.Add(($"{type.FullName}.{m.Name}", callee));
            }
        }
        return found;
    }

    private static IEnumerable<string> Callees(MethodBase m, byte[] il)
    {
        int i = 0;
        while (i < il.Length)
        {
            short value = il[i] == 0xFE && i + 1 < il.Length ? (short)(0xFE00 | il[i + 1]) : il[i];
            if (!OpCodesByValue.TryGetValue(value, out var op))
                yield break;
            i += op.Size;
            int operand = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
            if (op.OperandType is OperandType.InlineMethod or OperandType.InlineTok)
            {
                int token = BitConverter.ToInt32(il, i);
                MemberInfo? member = null;
                try
                {
                    member = m.Module.ResolveMember(token,
                        m.DeclaringType?.IsGenericType == true ? m.DeclaringType.GetGenericArguments() : null,
                        m.IsGenericMethod ? m.GetGenericArguments() : null);
                }
                catch (Exception)
                {
                }
                if (member?.DeclaringType is { } dt)
                {
                    string full = $"{dt.FullName}.{member.Name}";
                    if (CombatFacade.ForbiddenMembers.Contains(full) || EntryPoints.Contains(full))
                        yield return full;
                }
            }
            i += operand;
        }
    }

    public static void Write(Utf8JsonWriter w)
    {
        var refs = References();
        w.WriteStartObject();
        w.WriteBoolean("ok", true);
        w.WriteString("type", "facade_audit");
        w.WritePropertyName("forbidden_members");
        w.WriteStartArray();
        foreach (var f in CombatFacade.ForbiddenMembers)
            w.WriteStringValue(f);
        w.WriteEndArray();
        w.WritePropertyName("direct_references");
        w.WriteStartArray();
        foreach (var (caller, callee) in refs.Where(r => CombatFacade.ForbiddenMembers.Contains(r.Callee)))
        {
            w.WriteStartObject();
            w.WriteString("caller", caller);
            w.WriteString("callee", callee);
            w.WriteBoolean("debug_only", caller.StartsWith(typeof(ParityProbe).FullName!, StringComparison.Ordinal));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("entry_point_references");
        w.WriteStartArray();
        foreach (var (caller, callee) in refs.Where(r => EntryPoints.Contains(r.Callee)))
        {
            w.WriteStartObject();
            w.WriteString("caller", caller);
            w.WriteString("callee", callee);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("reflected_members");
        w.WriteStartArray();
        foreach (var r in CombatFacade.ReflectedMembers)
            w.WriteStringValue(r);
        w.WriteEndArray();
        w.WritePropertyName("parity_probe_reflected_members");
        w.WriteStartArray();
        foreach (var r in ParityProbe.ReflectedMembers)
            w.WriteStringValue(r);
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
