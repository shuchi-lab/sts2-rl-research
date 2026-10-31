using System.Collections.Generic;

namespace STS2_MCP;

internal static class BattlePowerFold
{
    // Frozen BuffSystem.Apply stacks these shared counters. Native powers
    // retain their card/potion/relic origin, which can produce several rows
    // (BlockNextTurn81: BlockNextTurnPower and SelfFormingClayPower).
    // IsInstanced PanachePower38 gives one row plus PanacheArmed39 per application;
    // frozen build08 stacks one amount with one armed flag started by the first
    // application, so 38 is summed and only the oldest instance's 39 is kept.
    // Other repeated IDs (notably Bomb timers) represent separate instances.
    internal static List<int[]> TemporaryCounters(List<int[]> rows)
    {
        var result = new List<int[]>();
        var counters = new Dictionary<int,int[]>();
        foreach (var row in rows)
        {
            if (row[0] == 39)
            {
                if (!counters.ContainsKey(39))
                {
                    var armed = new[] {row[0],row[1]};
                    counters.Add(39,armed);
                    result.Add(armed);
                }
            }
            else if (row[0] is 81 or 83 or 130 or 38)
            {
                if (counters.TryGetValue(row[0],out var existing)) existing[1] += row[1];
                else
                {
                    var copy = new[] {row[0],row[1]};
                    counters.Add(row[0],copy);
                    result.Add(copy);
                }
            }
            else result.Add(row);
        }
        result.RemoveAll(row => row[0] is 83 or 130 && row[1] == 0);
        return result;
    }
}
