using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace STS2_MCP;

public sealed class AgentIdRegistry
{
    private readonly Dictionary<string, Dictionary<string, int>> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _supported = new(StringComparer.Ordinal);
    public JsonElement Acquisition { get; private set; }

    public static string Canonical(string entry) => string.Concat(entry.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    public static AgentIdRegistry FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("schema").GetString() != "sts2.live_contract/v1")
            throw new ArgumentException("Unexpected live contract schema.");
        var registry = new AgentIdRegistry();
        if (root.TryGetProperty("acquisition", out var acquisition)) registry.Acquisition = acquisition.Clone();
        foreach (var group in root.GetProperty("ids").EnumerateObject())
        {
            var entries = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in group.Value.EnumerateObject())
            {
                string key = Canonical(pair.Name);
                int value = pair.Value.GetInt32();
                if (entries.TryGetValue(key, out int existing) && existing != value)
                    throw new ArgumentException($"Ambiguous identity {group.Name}:{pair.Name}.");
                entries[key] = value;
            }
            registry._groups[group.Name] = entries;
        }
        foreach (string key in new[] { "battle_card_ids", "battle_potion_ids" })
            if (root.TryGetProperty(key, out var ids))
                registry._supported[key] = new HashSet<int>(ids.EnumerateArray().Select(x => x.GetInt32()));
        return registry;
    }

    public int Resolve(string group, string entry)
    {
        if (group == "buffs") group = "powers";
        if (!_groups.TryGetValue(group, out var entries) || !entries.TryGetValue(Canonical(entry), out int id))
            throw new KeyNotFoundException($"Unsupported frozen identity {group}:{entry}.");
        return id;
    }

    public bool Supports(string group, int id) => _supported.TryGetValue(group, out var ids) && ids.Contains(Math.Abs(id));
}
