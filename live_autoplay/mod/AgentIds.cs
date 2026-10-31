using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;

namespace STS2_MCP;

public sealed class AgentUnsupportedException : Exception
{
    public AgentUnsupportedException(string message) : base(message) { }
}

public static class AgentIds
{
    private static AgentIdRegistry? _registry;
    public static string? Error { get; private set; }
    public static bool Ready => _registry != null;
    public static string ContractPath => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "agent_contract.json");

    public static void Load()
    {
        try { _registry = AgentIdRegistry.FromJson(File.ReadAllText(ContractPath)); Error = null; }
        catch (Exception ex) { _registry = null; Error = ex.Message; }
    }

    public static int Id(string group, string nativeEntry)
    {
        if (_registry == null) throw new AgentUnsupportedException("Agent contract is unavailable: " + Error);
        try { return _registry.Resolve(group, nativeEntry); }
        catch (KeyNotFoundException ex) { throw new AgentUnsupportedException(ex.Message); }
    }

    public static bool CardSupported(int id) => _registry?.Supports("battle_card_ids", id) ?? false;
    public static bool PotionSupported(int id) => id == 0 || (_registry?.Supports("battle_potion_ids", id) ?? false);
    private static JsonElement Acquisition(string key)
    {
        if (_registry == null || _registry.Acquisition.ValueKind != JsonValueKind.Object || !_registry.Acquisition.TryGetProperty(key, out var value))
            throw new AgentUnsupportedException("Missing frozen acquisition rule: " + key);
        return value;
    }
    public static bool AncientRelicDeclined(int id) => Acquisition("declined_ancient_relic_ids").EnumerateArray().Any(x => x.GetInt32() == id);
    public static int CapacityIncrease(int id) => Acquisition("capacity_relics").EnumerateArray()
        .Where(x => x.GetProperty("relic_id").GetInt32() == id).Select(x => x.GetProperty("slots").GetInt32()).FirstOrDefault();
    public static bool EventDeclined(int eventId, int page, int option) => Acquisition("declined_event_options").EnumerateArray()
        .Any(x => x.GetProperty("event_id").GetInt32() == eventId && x.GetProperty("page").GetInt32() == page && x.GetProperty("option").GetInt32() == option);
}

public static class AgentObjectIds
{
    private sealed class Identity { public required int Id; }
    private static ConditionalWeakTable<object, Identity> _objects = new();
    private static int _next;
    public static int Get(object instance) => _objects.GetValue(instance, _ => new Identity { Id = ++_next }).Id;
    public static int Card(CardModel card) => Get(card);
    public static void Reset() { _objects = new(); _next = 0; }
}
