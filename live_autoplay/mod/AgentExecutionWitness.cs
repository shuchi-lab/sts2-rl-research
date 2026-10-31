using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace STS2_MCP;

internal readonly record struct AgentRelicWitness(string NativeEntry, int? MechanicalCounter,
    bool IncludesHiddenCounter = false);

internal static class AgentExecutionWitness
{
    internal static JsonElement Normalize(JsonElement nativeState, IReadOnlyList<AgentRelicWitness> relics)
    {
        // The observer's UI snapshot remains untouched. Only an exact physical
        // relic row with a certified mechanical counter receives a substitution.
        var clone = JsonNode.Parse(nativeState.GetRawText());
        if (clone is JsonObject root && root["player"] is JsonObject player
            && player["relics"] is JsonArray rows && rows.Count == relics.Count)
        {
            for (int index = 0; index < rows.Count; index++)
            {
                var witness = relics[index];
                if (witness.MechanicalCounter is not int counter || rows[index] is not JsonObject row
                    || row["id"] is not JsonValue id || !id.TryGetValue<string>(out var entry)
                    || !string.Equals(entry, witness.NativeEntry, StringComparison.Ordinal)
                    || !row.TryGetPropertyValue("counter", out var displayed)) continue;
                bool integerDisplay = displayed is JsonValue value && value.TryGetValue<int>(out _);
                // Stone Calendar's exact active-combat certificate maps its
                // native turn/activation display to the frozen stored Counter0.
                // Its hidden UI counter is explicitly null, not a missing key.
                bool certifiedHiddenStone = displayed is null && witness.IncludesHiddenCounter
                    && witness.NativeEntry == "STONE_CALENDAR" && counter == 0;
                if (!integerDisplay && !certifiedHiddenStone) continue;
                row["counter"] = counter;
            }
        }
        return JsonSerializer.SerializeToElement(clone);
    }

    // Native potion object per belt slot (0 = empty), for execution observation only. Public rows
    // carry just the def id, and installed EntropicBrew.OnUse may roll another Entropic Brew into
    // the slot it vacated, so a same-type refill is otherwise byte-identical to a no-op.
    internal static int[] PotionSlotObjects(IEnumerable<object?> slots, Func<object, int> identity)
        => slots.Select(p => p == null ? 0 : identity(p)).ToArray();
}
