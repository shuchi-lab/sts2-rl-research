using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.ValueProps;

namespace STS2_MCP;

// Frozen CombatState.TookUnblockedDamage, the combat-wide Lava Lamp latch. Frozen
// RelicEffects.ApplyAfterPlayerDamageReceived sets it for a player hit that lost HP, left the
// player alive, and did not carry ValueProp.Unblockable. Native LavaLamp.AfterDamageReceived
// refuses the same props, and CreatureCmd.Damage skips that dispatch for a killing hit even
// when Fairy in a Bottle or Lizard Tail revives afterwards. The HP-lost tallies keep every
// prop; only this latch filters.
internal static class BattleDamageHistory
{
    internal static bool LatchesTookUnblocked(DamageResult result) =>
        result.UnblockedDamage > 0 && !result.WasTargetKilled && !result.Props.HasFlag(ValueProp.Unblockable);

    // `results` are the local player's own DamageReceivedEntry results for this combat.
    internal static bool TookUnblockedDamage(IEnumerable<DamageResult> results) => results.Any(LatchesTookUnblocked);

    // Beating Remnant's tally is written by that same surviving-player damage hook.
    // It includes Unblockable HP costs, and excludes the killing hit whose hook was skipped.
    internal static int UnblockedDamageThisTurn(IEnumerable<DamageResult> results) =>
        results.Where(r => !r.WasTargetKilled).Sum(r => r.UnblockedDamage);
}
