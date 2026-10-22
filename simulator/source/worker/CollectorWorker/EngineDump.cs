using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Rng;
using Sts2Emulator.Core.Run;

namespace CombatWorker;

/// <summary>
/// Writes engine objects as raw JSON: every instance field (public, internal and private,
/// base classes included), enum values as names, no encoding or feature selection.
/// </summary>
/// <remarks>
/// Lossless conventions (documented in docs/SCHEMA.md and published per type by the
/// "type_schema" command):
/// * A field whose value equals the same field of the type's prototype instance is omitted
///   (prototype = parameterless constructor, or the widest constructor called with its
///   declared defaults). Null fields and empty collections are omitted when the prototype's
///   value is also null/empty.
/// * A mutable class instance reachable twice is written once; later occurrences are
///   {"$ref": "json.path"} to the first. Records (value semantics) are always inlined.
/// * RNG streams are written as {"$rng": {...}} -- hidden engine state.
/// * An object whose runtime type differs from the declared field type carries "$t".
/// </remarks>
public sealed class EngineDumper
{
    private readonly Dictionary<object, string> _seen = new(ReferenceEqualityComparer.Instance);
    // Objects currently being written (records included), so a self-referencing value is
    // written as {"$cycle": path} instead of recursing without bound.
    private readonly Dictionary<object, string> _onPath = new(ReferenceEqualityComparer.Instance);
    public object? ElideReference { get; set; }
    public const int MaxDepth = 48;
    private int _depth;
    public string ElideNote { get; set; } = "";
    public bool OmitDefaults { get; set; } = true;

    public static readonly HashSet<Type> TypesSeen = [];

    public sealed record FieldSpec(FieldInfo Field, string Name, bool IsObjectRef);

    private static readonly Dictionary<Type, FieldSpec[]> FieldCache = [];
    private static readonly Dictionary<Type, object?> PrototypeCache = [];

    public void Reset()
    {
        _seen.Clear();
        _onPath.Clear();
    }

    public static FieldSpec[] Fields(Type type)
    {
        if (FieldCache.TryGetValue(type, out var cached))
            return cached;
        var list = new List<FieldInfo>();
        var chain = new List<Type>();
        for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
            chain.Add(t);
        chain.Reverse();
        foreach (var t in chain)
            list.AddRange(
                t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            );
        var specs = list.Where(f => !f.IsDefined(typeof(CompilerGeneratedAttribute)) || f.Name.EndsWith("k__BackingField"))
            .Where(f => f.Name != "EqualityContract")
            .Select(f => new FieldSpec(f, CleanName(f.Name), IsMutableClass(f.FieldType)))
            .ToList();
        // Owning collections before single references, so a shared enemy is written in
        // its list and referenced from HexApplier/PlayTarget, not the other way round.
        var ordered = specs.Where(s => !s.IsObjectRef).Concat(specs.Where(s => s.IsObjectRef)).ToArray();
        FieldCache[type] = ordered;
        return ordered;
    }

    private static string CleanName(string name)
    {
        if (name.StartsWith('<'))
        {
            int end = name.IndexOf('>');
            if (end > 1)
                return name[1..end];
        }
        return name;
    }

    public static bool IsRecord(Type t) =>
        t.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null
        || (t.IsValueType && t.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) is not null);

    private static bool IsMutableClass(Type t) =>
        t.IsClass
        && t != typeof(string)
        && !typeof(IEnumerable).IsAssignableFrom(t)
        && !typeof(Delegate).IsAssignableFrom(t)
        && !typeof(Random).IsAssignableFrom(t)
        && t != typeof(ProgressionContext)
        && !IsRecord(t);

    public static bool IsScalar(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);

    public static object? Prototype(Type type)
    {
        if (PrototypeCache.TryGetValue(type, out var cached))
            return cached;
        object? proto = null;
        try
        {
            var parameterless = type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                Type.EmptyTypes
            );
            var widest = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(c => c.GetParameters().Length > 0)
                .Where(c => !(c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == type))
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (type.IsValueType && widest is not null)
            {
                proto = widest.Invoke(
                    widest.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : DefaultOf(p.ParameterType)).ToArray()
                );
            }
            else if (parameterless is not null)
            {
                proto = parameterless.Invoke(null);
            }
            else if (type.IsValueType)
            {
                proto = Activator.CreateInstance(type);
            }
            else if (widest is not null)
            {
                proto = widest.Invoke(
                    widest.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : DefaultOf(p.ParameterType)).ToArray()
                );
            }
        }
        catch
        {
            proto = null;
        }
        PrototypeCache[type] = proto;
        return proto;
    }

    private static object? DefaultOf(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

    public void Write(Utf8JsonWriter w, object? value, string path = "$") =>
        WriteValue(w, value, value?.GetType() ?? typeof(object), path);

    private static bool IsEmptyCollection(object? v) =>
        v is ICollection c ? c.Count == 0 : v is Array a && a.Length == 0;

    private void WriteValue(Utf8JsonWriter w, object? value, Type declared, string path)
    {
        if (value is null)
        {
            w.WriteNullValue();
            return;
        }
        var type = value.GetType();
        switch (value)
        {
            case bool b:
                w.WriteBooleanValue(b);
                return;
            case string s:
                w.WriteStringValue(s);
                return;
            case char ch:
                w.WriteStringValue(ch.ToString());
                return;
            case int i:
                w.WriteNumberValue(i);
                return;
            case long l:
                w.WriteNumberValue(l);
                return;
            case uint ui:
                w.WriteNumberValue(ui);
                return;
            case ulong ul:
                w.WriteNumberValue(ul);
                return;
            case short sh:
                w.WriteNumberValue(sh);
                return;
            case ushort us:
                w.WriteNumberValue(us);
                return;
            case byte by:
                w.WriteNumberValue(by);
                return;
            case sbyte sb:
                w.WriteNumberValue(sb);
                return;
            case decimal dm:
                w.WriteNumberValue(dm);
                return;
            case float f:
                if (float.IsFinite(f))
                    w.WriteNumberValue(f);
                else
                    w.WriteStringValue(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            case double d:
                if (double.IsFinite(d))
                    w.WriteNumberValue(d);
                else
                    w.WriteStringValue(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
        }
        if (type.IsEnum)
        {
            w.WriteStringValue(value.ToString());
            return;
        }
        if (value is CountingRandom cr)
        {
            WriteRng(w, "CountingRandom", cr.Seed, cr.Snapshot());
            return;
        }
        if (value is GameRng gr)
        {
            WriteRng(w, "GameRng", gr.RawSeed, gr.Snapshot());
            return;
        }
        if (value is Random)
        {
            w.WriteStartObject();
            w.WritePropertyName("$rng");
            w.WriteStartObject();
            w.WriteString("type", type.FullName);
            w.WriteString("note", "opaque System.Random fallback; state not inspectable");
            w.WriteEndObject();
            w.WriteEndObject();
            return;
        }
        if (value is ProgressionContext pc)
        {
            if (ReferenceEquals(pc, ElideReference))
            {
                w.WriteStartObject();
                w.WriteString("$elided", ElideNote);
                w.WriteEndObject();
                return;
            }
            WriteProgression(w, pc);
            return;
        }
        if (value is Delegate del)
        {
            w.WriteStartObject();
            w.WriteString("$delegate", del.Method.DeclaringType?.Name + "." + del.Method.Name);
            w.WriteEndObject();
            return;
        }
        if (value is IDictionary dict)
        {
            w.WriteStartArray();
            int k = 0;
            foreach (DictionaryEntry e in dict)
            {
                w.WriteStartObject();
                w.WritePropertyName("k");
                WriteValue(w, e.Key, e.Key.GetType(), $"{path}[{k}].k");
                w.WritePropertyName("v");
                WriteValue(w, e.Value, e.Value?.GetType() ?? typeof(object), $"{path}[{k}].v");
                w.WriteEndObject();
                k++;
            }
            w.WriteEndArray();
            return;
        }
        if (value is IEnumerable seq)
        {
            w.WriteStartArray();
            int k = 0;
            var elementType = ElementType(type);
            foreach (var item in seq)
            {
                WriteValue(w, item, elementType, $"{path}[{k}]");
                k++;
            }
            w.WriteEndArray();
            return;
        }

        // An object: struct, record or mutable class.
        if (_depth >= MaxDepth)
        {
            w.WriteStartObject();
            w.WriteString("$depth_limit", type.Name);
            w.WriteEndObject();
            return;
        }
        if (!type.IsValueType && _onPath.TryGetValue(value, out var ancestor))
        {
            w.WriteStartObject();
            w.WriteString("$cycle", ancestor);
            w.WriteEndObject();
            return;
        }
        bool tracked = IsMutableClass(type);
        if (tracked)
        {
            if (_seen.TryGetValue(value, out var first))
            {
                w.WriteStartObject();
                w.WriteString("$ref", first);
                w.WriteEndObject();
                return;
            }
            _seen[value] = path;
        }
        lock (TypesSeen)
            TypesSeen.Add(type);
        var proto = OmitDefaults ? Prototype(type) : null;
        if (!type.IsValueType)
            _onPath[value] = path;
        _depth++;
        w.WriteStartObject();
        var nonNullableDeclared = Nullable.GetUnderlyingType(declared) ?? declared;
        if (nonNullableDeclared != type)
            w.WriteString("$t", type.Name);
        foreach (var spec in Fields(type))
        {
            object? fieldValue = spec.Field.GetValue(value);
            if (proto is not null)
            {
                object? protoValue = spec.Field.GetValue(proto);
                if (fieldValue is null && protoValue is null)
                    continue;
                if (fieldValue is not null && protoValue is not null)
                {
                    var ft = fieldValue.GetType();
                    if ((IsScalar(ft) || (ft.IsValueType && !IsCollectionLike(ft))) && fieldValue.Equals(protoValue))
                        continue;
                    if (IsEmptyCollection(fieldValue) && IsEmptyCollection(protoValue))
                        continue;
                }
            }
            w.WritePropertyName(spec.Name);
            WriteValue(w, fieldValue, spec.Field.FieldType, $"{path}.{spec.Name}");
        }
        w.WriteEndObject();
        _depth--;
        if (!type.IsValueType)
            _onPath.Remove(value);
    }

    private static bool IsCollectionLike(Type t) => typeof(IEnumerable).IsAssignableFrom(t);

    private static Type ElementType(Type t)
    {
        if (t.IsArray)
            return t.GetElementType()!;
        var ienum = t.GetInterfaces()
            .Concat([t])
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return ienum?.GetGenericArguments()[0] ?? typeof(object);
    }

    public static void WriteRng(Utf8JsonWriter w, string kind, ulong seed, RngState s)
    {
        w.WriteStartObject();
        w.WritePropertyName("$rng");
        w.WriteStartObject();
        w.WriteString("type", kind);
        w.WriteString("algorithm", s.Algorithm.ToString());
        w.WriteNumber("seed", seed);
        w.WriteNumber("counter", s.Counter);
        w.WritePropertyName("lanes");
        w.WriteStartArray();
        w.WriteNumberValue(s.S0);
        w.WriteNumberValue(s.S1);
        w.WriteNumberValue(s.S2);
        w.WriteNumberValue(s.S3);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    public static void WriteProgression(Utf8JsonWriter w, ProgressionContext pc)
    {
        var snap = pc.ToSnapshot();
        w.WriteStartObject();
        w.WritePropertyName("$progression");
        w.WriteStartObject();
        w.WriteBoolean("is_all_unlocked_singleton", ReferenceEquals(pc, ProgressionContext.AllUnlocked));
        w.WriteNumber("number_of_runs", snap.NumberOfRuns);
        w.WritePropertyName("revealed_epochs");
        JsonSerializer.Serialize(w, snap.RevealedEpochs);
        w.WritePropertyName("encounters_seen");
        JsonSerializer.Serialize(w, snap.EncountersSeen);
        w.WritePropertyName("discovered_acts");
        JsonSerializer.Serialize(w, snap.DiscoveredActs);
        w.WritePropertyName("character_wins");
        w.WriteStartObject();
        foreach (var kv in snap.CharacterWins)
            w.WriteNumber(kv.Key, kv.Value);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>Field names, declared types and prototype values of every type written so far.</summary>
    public static void WriteTypeSchema(Utf8JsonWriter w, IEnumerable<Type> types)
    {
        w.WriteStartObject();
        foreach (var type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            w.WritePropertyName(type.Name);
            w.WriteStartObject();
            w.WriteString("full_name", type.FullName);
            w.WriteBoolean("record", IsRecord(type));
            w.WriteBoolean("reference_tracked", IsMutableClass(type));
            w.WritePropertyName("fields");
            w.WriteStartArray();
            foreach (var spec in Fields(type))
            {
                w.WriteStartObject();
                w.WriteString("name", spec.Name);
                w.WriteString("declared_type", TypeName(spec.Field.FieldType));
                w.WriteString("visibility", spec.Field.IsPublic ? "public" : spec.Field.IsAssembly ? "internal" : "private");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            var proto = Prototype(type);
            w.WritePropertyName("prototype");
            if (proto is null)
            {
                w.WriteNullValue();
            }
            else
            {
                var d = new EngineDumper { OmitDefaults = false };
                byte[] bytes;
                try
                {
                    bytes = Import.Serialize(x => d.Write(x, proto));
                }
                catch (Exception ex)
                {
                    bytes = Import.Serialize(x =>
                    {
                        x.WriteStartObject();
                        x.WriteString("$prototype_error", ex.GetType().Name + ": " + ex.Message);
                        x.WriteEndObject();
                    });
                }
                w.WriteRawValue(bytes, skipInputValidation: true);
            }
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    public static string TypeName(Type t)
    {
        if (!t.IsGenericType)
            return t.Name;
        return t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
    }
}
