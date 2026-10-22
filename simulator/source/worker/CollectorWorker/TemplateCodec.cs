using System.Reflection;
using System.Text.Json;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Run;
using Sts2Emulator.Training;

namespace CombatWorker;

/// <summary>
/// Lossless JSON form of a <see cref="BattleInput" />: schema "cds.engine_input/v1".
/// Record structs (CardInstance, RelicInstance) are written by primary-constructor
/// parameter, omitting a parameter only when it equals its declared default; reading
/// calls the same constructor, and an unknown property is an error rather than dropped.
/// </summary>
public static class TemplateCodec
{
    public const string EngineInputSchema = "cds.engine_input/v1";

    private static ConstructorInfo PrimaryCtor(Type t) =>
        t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

    public static void WriteRecord(Utf8JsonWriter w, object value)
    {
        var type = value.GetType();
        var ctor = PrimaryCtor(type);
        w.WriteStartObject();
        foreach (var p in ctor.GetParameters())
        {
            var prop = type.GetProperty(p.Name!)!;
            var v = prop.GetValue(value);
            if (p.HasDefaultValue && Equals(v, p.DefaultValue))
                continue;
            if (p.HasDefaultValue && v is null && p.DefaultValue is null)
                continue;
            w.WritePropertyName(p.Name!);
            WriteScalar(w, v);
        }
        w.WriteEndObject();
    }

    private static void WriteScalar(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null:
                w.WriteNullValue();
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case decimal d:
                w.WriteNumberValue(d);
                break;
            case Enum e:
                w.WriteStringValue(e.ToString());
                break;
            case DeckCardOrigin o:
                w.WriteStartObject();
                w.WriteNumber("Index", o.Index);
                w.WritePropertyName("Card");
                WriteRecord(w, o.Card);
                w.WriteEndObject();
                break;
            default:
                throw new NotSupportedException($"record field type {v.GetType()}");
        }
    }

    public static T ReadRecord<T>(JsonElement e)
    {
        var type = typeof(T);
        return (T)ReadRecord(type, e);
    }

    private static object ReadRecord(Type type, JsonElement e)
    {
        var ctor = PrimaryCtor(type);
        var ps = ctor.GetParameters();
        var names = ps.Select(p => p.Name!).ToHashSet();
        foreach (var prop in e.EnumerateObject())
            if (!names.Contains(prop.Name))
                throw new FormatException($"{type.Name}: unknown field '{prop.Name}' (schema mismatch)");
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (e.TryGetProperty(p.Name!, out var v))
                args[i] = ReadScalar(p.ParameterType, v);
            else if (p.HasDefaultValue)
                args[i] = p.DefaultValue;
            else
                throw new FormatException($"{type.Name}: missing required field '{p.Name}'");
        }
        return ctor.Invoke(args);
    }

    private static object? ReadScalar(Type t, JsonElement v)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (v.ValueKind == JsonValueKind.Null)
            return null;
        if (u == typeof(bool))
            return v.GetBoolean();
        if (u == typeof(int))
            return v.GetInt32();
        if (u == typeof(decimal))
            return v.GetDecimal();
        if (u.IsEnum)
        {
            // Names only: Enum.Parse would also accept numeric text and undefined values.
            string name = v.GetString()!;
            if (!Enum.GetNames(u).Contains(name, StringComparer.Ordinal))
                throw new FormatException($"{u.Name}: undefined value '{name}'");
            return Enum.Parse(u, name, ignoreCase: false);
        }
        if (u == typeof(DeckCardOrigin))
            return new DeckCardOrigin(v.GetProperty("Index").GetInt32(), ReadRecord<CardInstance>(v.GetProperty("Card")));
        throw new NotSupportedException($"record field type {t}");
    }

    public static void WriteBattleInput(Utf8JsonWriter w, BattleInput b, List<string> flags)
    {
        w.WriteStartObject();
        w.WriteString("schema", EngineInputSchema);
        w.WriteString("Character", b.Character.ToString());
        w.WriteNumber("AscensionLevel", b.AscensionLevel);
        w.WritePropertyName("Progression");
        EngineDumper.WriteProgression(w, b.Progression);
        w.WritePropertyName("Deck");
        w.WriteStartArray();
        for (int i = 0; i < b.Deck.Count; i++)
        {
            var card = b.Deck[i];
            // The origin stamp is derived (index + the card itself) and re-stamped at build.
            if (card.DeckOrigin is { } origin && (origin.Index != i || origin.Card != (card with { DeckOrigin = null })))
                flags.Add($"deck[{i}]: DeckOrigin is not the standard self stamp; kept verbatim");
            var stripped = card.DeckOrigin is { } o2 && o2.Index == i && o2.Card == (card with { DeckOrigin = null })
                ? card with { DeckOrigin = null }
                : card;
            WriteRecord(w, stripped);
        }
        w.WriteEndArray();
        w.WritePropertyName("Relics");
        w.WriteStartArray();
        foreach (var r in b.Relics)
            WriteRecord(w, r);
        w.WriteEndArray();
        w.WritePropertyName("UsedUpRelics");
        JsonSerializer.Serialize(w, b.UsedUpRelics);
        w.WriteNumber("PlayerHp", b.PlayerHp);
        w.WriteNumber("PlayerMaxHp", b.PlayerMaxHp);
        w.WriteNumber("Gold", b.Gold);
        w.WritePropertyName("StartingPotions");
        JsonSerializer.Serialize(w, b.StartingPotions);
        w.WriteNumber("MaxPotionSlots", b.MaxPotionSlots);
        w.WriteNumber("CompletedCombatRoomsBeforeCurrent", b.CompletedCombatRoomsBeforeCurrent);
        w.WriteNumber("EncounterId", b.EncounterId);
        w.WriteBoolean("EncounterWeakVariant", b.EncounterWeakVariant);
        w.WriteNumber("TotalFloor", b.TotalFloor);
        w.WriteBoolean("IsEliteRoom", b.IsEliteRoom);
        w.WriteBoolean("FurCoatBites", b.FurCoatBites);
        w.WritePropertyName("PresetEnemyHps");
        JsonSerializer.Serialize(w, b.PresetEnemyHps);
        w.WritePropertyName("context");
        w.WriteStartObject();
        w.WriteNumber("ActIndex", b.ActIndex);
        w.WriteNumber("CurrentNodeType", b.CurrentNodeType);
        w.WriteNumber("LastResolvedRoomType", b.LastResolvedRoomType);
        w.WriteNumber("NormalEncountersVisited", b.NormalEncountersVisited);
        w.WriteNumber("EliteEncountersVisited", b.EliteEncountersVisited);
        w.WriteNumber("MapCol", b.MapCol);
        w.WriteNumber("MapRow", b.MapRow);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static readonly HashSet<string> BattleInputFields =
    [
        "schema", "Character", "AscensionLevel", "Progression", "Deck", "Relics", "UsedUpRelics", "PlayerHp",
        "PlayerMaxHp", "Gold", "StartingPotions", "MaxPotionSlots", "CompletedCombatRoomsBeforeCurrent",
        "EncounterId", "EncounterWeakVariant", "TotalFloor", "IsEliteRoom", "FurCoatBites", "PresetEnemyHps", "context",
    ];

    /// <summary>
    /// Values the engine would silently clamp or fail on are rejected here instead, so a
    /// template never runs as something other than what it says. HP 0 stays legal (a fight
    /// that is over on entry).
    /// </summary>
    public static void Validate(BattleInput b)
    {
        var p = new List<string>();
        if (!Enum.IsDefined(b.Character))
            p.Add($"Character {b.Character} undefined");
        if (b.AscensionLevel is < 0 or > Ascension.DoubleBoss)
            p.Add($"AscensionLevel {b.AscensionLevel} outside 0..{Ascension.DoubleBoss}");
        if (b.PlayerMaxHp < 1)
            p.Add($"PlayerMaxHp {b.PlayerMaxHp} < 1");
        if (b.PlayerHp < 0 || b.PlayerHp > b.PlayerMaxHp)
            p.Add($"PlayerHp {b.PlayerHp} outside 0..PlayerMaxHp ({b.PlayerMaxHp})");
        if (b.Gold < 0)
            p.Add($"Gold {b.Gold} < 0");
        if (b.MaxPotionSlots is < 0 or > RunConstants.PotionSlotCapacity)
            p.Add($"MaxPotionSlots {b.MaxPotionSlots} outside 0..{RunConstants.PotionSlotCapacity}");
        if (b.StartingPotions.Length > RunConstants.PotionSlotCapacity)
            p.Add($"StartingPotions has {b.StartingPotions.Length} slots > capacity {RunConstants.PotionSlotCapacity}");
        for (int i = 0; i < b.StartingPotions.Length; i++)
        {
            int id = b.StartingPotions[i];
            if (id == 0)
                continue;
            if (i >= b.MaxPotionSlots)
                p.Add($"StartingPotions[{i}] = {id} lies beyond MaxPotionSlots {b.MaxPotionSlots}");
            try
            {
                Sts2Emulator.GeneratedData.Potions.Get(id);
            }
            catch (ArgumentException)
            {
                p.Add($"StartingPotions[{i}] = {id} is not a potion id");
            }
        }
        for (int i = 0; i < b.Deck.Count; i++)
        {
            try
            {
                Sts2Emulator.GeneratedData.Cards.Get(b.Deck[i].DefId);
            }
            catch (ArgumentException)
            {
                p.Add($"Deck[{i}].DefId {b.Deck[i].DefId} is not a card id");
            }
        }
        for (int i = 0; i < b.Relics.Count; i++)
            if (!Sts2Emulator.GeneratedData.Relics.TryGet(b.Relics[i].DefId, out _))
                p.Add($"Relics[{i}].DefId {b.Relics[i].DefId} is not a relic id");
        foreach (int id in b.UsedUpRelics)
            if (!Sts2Emulator.GeneratedData.Relics.TryGet(id, out _))
                p.Add($"UsedUpRelics contains non-relic id {id}");
        if (!Enum.IsDefined(typeof(CombatFactory.ActOneEncounter), b.EncounterId))
            p.Add($"EncounterId {b.EncounterId} is not an implemented encounter (0 is Cultists)");
        if (b.TotalFloor < 0)
            p.Add($"TotalFloor {b.TotalFloor} < 0");
        if (b.CompletedCombatRoomsBeforeCurrent < 0)
            p.Add($"CompletedCombatRoomsBeforeCurrent {b.CompletedCombatRoomsBeforeCurrent} < 0");
        if (b.PresetEnemyHps is { } hps && hps.Any(h => h <= 0))
            p.Add("PresetEnemyHps contains a value <= 0");
        if (p.Count > 0)
            throw new FormatException("invalid engine_input: " + string.Join("; ", p));
    }

    public static BattleInput ReadBattleInput(JsonElement e)
    {
        var b = ReadBattleInputUnchecked(e);
        Validate(b);
        return b;
    }

    private static BattleInput ReadBattleInputUnchecked(JsonElement e)
    {
        if (e.GetProperty("schema").GetString() != EngineInputSchema)
            throw new FormatException($"engine_input schema {e.GetProperty("schema")} != {EngineInputSchema}");
        foreach (var p in e.EnumerateObject())
            if (!BattleInputFields.Contains(p.Name))
                throw new FormatException($"engine_input: unknown field '{p.Name}'");
        var prog = e.GetProperty("Progression").GetProperty("$progression");
        ProgressionContext progression = prog.GetProperty("is_all_unlocked_singleton").GetBoolean()
            ? ProgressionContext.AllUnlocked
            : new ProgressionContext(
                prog.GetProperty("revealed_epochs").EnumerateArray().Select(x => x.GetString()!),
                prog.GetProperty("encounters_seen").EnumerateArray().Select(x => x.GetString()!),
                prog.GetProperty("number_of_runs").GetInt32(),
                prog.GetProperty("discovered_acts").ValueKind == JsonValueKind.Null
                    ? null
                    : prog.GetProperty("discovered_acts").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                prog.GetProperty("character_wins").EnumerateObject().Select(x => new KeyValuePair<string, int>(x.Name, x.Value.GetInt32())).ToArray()
            );
        var ctx = e.GetProperty("context");
        return new BattleInput
        {
            Character = Enum.Parse<CharacterId>(e.GetProperty("Character").GetString()!),
            AscensionLevel = e.GetProperty("AscensionLevel").GetInt32(),
            Progression = progression,
            Deck = e.GetProperty("Deck").EnumerateArray().Select(ReadRecord<CardInstance>).ToList(),
            Relics = e.GetProperty("Relics").EnumerateArray().Select(ReadRecord<RelicInstance>).ToList(),
            UsedUpRelics = e.GetProperty("UsedUpRelics").EnumerateArray().Select(x => x.GetInt32()).ToList(),
            PlayerHp = e.GetProperty("PlayerHp").GetInt32(),
            PlayerMaxHp = e.GetProperty("PlayerMaxHp").GetInt32(),
            Gold = e.GetProperty("Gold").GetInt32(),
            StartingPotions = e.GetProperty("StartingPotions").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            MaxPotionSlots = e.GetProperty("MaxPotionSlots").GetInt32(),
            CompletedCombatRoomsBeforeCurrent = e.GetProperty("CompletedCombatRoomsBeforeCurrent").GetInt32(),
            EncounterId = e.GetProperty("EncounterId").GetInt32(),
            EncounterWeakVariant = e.GetProperty("EncounterWeakVariant").GetBoolean(),
            TotalFloor = e.GetProperty("TotalFloor").GetInt32(),
            IsEliteRoom = e.GetProperty("IsEliteRoom").GetBoolean(),
            FurCoatBites = e.GetProperty("FurCoatBites").GetBoolean(),
            PresetEnemyHps = e.GetProperty("PresetEnemyHps").ValueKind == JsonValueKind.Null
                ? null
                : e.GetProperty("PresetEnemyHps").EnumerateArray().Select(x => x.GetInt32()).ToList(),
            ActIndex = ctx.GetProperty("ActIndex").GetInt32(),
            CurrentNodeType = ctx.GetProperty("CurrentNodeType").GetInt32(),
            LastResolvedRoomType = ctx.GetProperty("LastResolvedRoomType").GetInt32(),
            NormalEncountersVisited = ctx.GetProperty("NormalEncountersVisited").GetInt32(),
            EliteEncountersVisited = ctx.GetProperty("EliteEncountersVisited").GetInt32(),
            MapCol = ctx.GetProperty("MapCol").GetInt32(),
            MapRow = ctx.GetProperty("MapRow").GetInt32(),
        };
    }
}
