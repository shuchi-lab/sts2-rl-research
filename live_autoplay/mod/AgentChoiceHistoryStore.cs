using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace STS2_MCP;

internal sealed record AgentChoiceSavedHistory(int StrategicDecisions, int RestTakenMask,
    Dictionary<string, JsonElement>[] Fights)
{
    internal Dictionary<string, object?>[] RestoreFights() => Fights.Select(row => row.ToDictionary(
        pair => pair.Key, pair => pair.Value.ValueKind == JsonValueKind.Number
            ? (object?)pair.Value.GetInt32() : pair.Value.GetString())).ToArray();
}

// This companion file contains only previously observed Choice history. The
// native save contributes only its ordinary run and stage identifiers. No
// checksum, RNG state, or other private save contents enter the policy.
internal sealed class AgentChoiceHistoryStore(string directory)
{
    internal static string Key(string nativeSave)
    {
        using var doc = JsonDocument.Parse(nativeSave);
        var root = doc.RootElement;
        long Number(JsonElement obj, string field, long missing = 0) => obj.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : missing;
        var visits = root.TryGetProperty("visited_map_coords", out var coords) && coords.ValueKind == JsonValueKind.Array
            ? coords.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
        var points = root.TryGetProperty("map_point_history", out var history) && history.ValueKind == JsonValueKind.Array
            ? history.EnumerateArray().SelectMany(a => a.EnumerateArray()).ToArray() : Array.Empty<JsonElement>();
        int rooms = points.Sum(p => p.TryGetProperty("rooms", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0);
        long col = visits.Length == 0 ? -1 : Number(visits[^1], "col", -1);
        long row = visits.Length == 0 ? -1 : Number(visits[^1], "row", -1);
        string stage = root.TryGetProperty("pre_finished_room", out var room) && room.ValueKind != JsonValueKind.Null ? "rewards" : "entry";
        return $"run-{Number(root, "start_time")}-a{Number(root, "ascension")}-act{Number(root, "current_act_index") + 1}"
            + $"-floor{points.Length}-map{col}_{row}-rooms{rooms}-{stage}";
    }

    internal static AgentChoiceSavedHistory Capture(int decisions, int restMask,
        IEnumerable<Dictionary<string, object?>> fights) => new(decisions, restMask,
            fights.Select(row => row.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value))).ToArray());

    internal void Save(string key, AgentChoiceSavedHistory memory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, key + ".json"), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(memory));
        File.Move(temp, path, true);
    }

    internal AgentChoiceSavedHistory? Load(string key)
    {
        string path = Path.Combine(directory, key + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<AgentChoiceSavedHistory>(File.ReadAllText(path)) : null;
    }

    internal void Forget(string key) => File.Delete(Path.Combine(directory, key + ".json"));
}
