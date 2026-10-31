using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

internal static class AgentIntegrity
{
    internal static bool SelectionMatches(IEnumerable<int> expected, IEnumerable<int> selected, int maximum)
    {
        var wanted = expected.ToArray(); var actual = selected.ToArray();
        return maximum > 0 && actual.Length <= maximum && wanted.Length == actual.Length
            && wanted.Distinct().Count() == wanted.Length && actual.Distinct().Count() == actual.Length
            && wanted.ToHashSet().SetEquals(actual);
    }

    internal static bool OneUpgrade(IReadOnlyDictionary<int,bool> before, IReadOnlyDictionary<int,bool> after, int expected)
    {
        if (!before.Keys.ToHashSet().SetEquals(after.Keys)) return false;
        if (before.Any(x => x.Value && !after[x.Key])) return false;
        var upgraded = before.Where(x => !x.Value && after[x.Key]).Select(x => x.Key).ToArray();
        return upgraded.Length == 1 && upgraded[0] == expected;
    }
}
