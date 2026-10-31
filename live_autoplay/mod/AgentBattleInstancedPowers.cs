using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

// Native IsInstanced powers (Panache, Automation, Orbit, ToricToughness, Thievery, ...)
// keep one PowerModel per application, appended to Creature.Powers in application order.
// The frozen build08 engine stacks each into one BuffId amount and keeps ONE scalar
// progress counter (CardsPlayedSincePanacheProc, DrawnCardsSinceAutomationProc,
// OrbitEnergySpent, ...) that starts at the first application; later applications only
// add amount. That counter is the oldest native instance's progress. Every instance
// still emits its own public power rows; only the frozen scalar reads the oldest.
// A duplicate non-instanced power remains impossible native state and is refused.
static class BattleInstancedPowers
{
    public static T? Select<T>(IEnumerable<T> matches, Func<T,bool> isInstanced) where T : class
    {
        var list = matches.ToList();
        if (list.Count <= 1) return list.SingleOrDefault();
        if (!list.All(isInstanced))
            throw new InvalidOperationException("Sequence contains more than one matching element");
        return list[0];
    }
}
