using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

internal static class BattleEnemySlot
{
    // Frozen EnemyState.Slot defaults to -1. The selected worker_build08 DLL
    // assigns it in exactly three encounter families, each mirroring that
    // native encounter's declared Slots: TwoTailedRats (starters 2/3/4, backup
    // into the last free slot), Fabricator (own slot 2, bots into the first
    // free slot; a respawned Axebot keeps its slot) and Ovicopter (own slot 5,
    // eggs downward). Every other native encounter's slots, e.g. the
    // PhantasmalGardeners first..fourth or Axebots front, are scene anchors the
    // frozen engine never models, so they stay -1.
    private static readonly Dictionary<string,string[]> FrozenSlotted = new()
    {
        ["TwoTailedRatsNormal"] = new[] { "first","second","third","fourth","fifth" },
        ["FabricatorNormal"] = new[] { "bot1","bot2","fabricator","bot3","bot4" },
        ["OvicopterNormal"] = new[] { "egg1","egg2","egg3","egg4","egg5","ovicopter" },
    };

    internal static int Project(string? encounterType,IReadOnlyList<string>? declaredSlots,string? slotName)
    {
        if (encounterType == null || !FrozenSlotted.TryGetValue(encounterType,out var frozen)) return -1;
        if (declaredSlots == null || !declaredSlots.SequenceEqual(frozen))
            throw new BattleProjectionUnsupported("Native slots differ from the frozen " + encounterType + " slot table");
        if (slotName == null) return -1;
        int slot = Array.IndexOf(frozen,slotName);
        if (slot < 0) throw new BattleProjectionUnsupported("Undeclared " + encounterType + " slot " + slotName);
        return slot;
    }
}
