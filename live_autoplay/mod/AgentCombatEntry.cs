using MegaCrit.Sts2.Core.Combat;

namespace STS2_MCP;

public static partial class McpMod
{
    // Frozen entry_hp/entry_max_hp is the run HP handed to the fight: after room-entry
    // effects (AfterRoomEntered) and BeforeCombatStart (Pantograph's boss heal), before the
    // first player turn (Blood Vial). Native StartCombatInternal raises CombatBegan exactly
    // there; SetUpCombat/CombatSetUp run before both heals. Battle and Choice share it.
    private static void AgentCombatEntryBegan(CombatState state)
    {
        if (!ReferenceEquals(state, _battleCombat) || !_battleFreshCombat || _battleEntryHp >= 0
            || _battlePlayer is not { } player) return;
        _battleEntryHp = player.Creature.CurrentHp; _battleEntryMaxHp = player.Creature.MaxHp;
        // A Choice fight bound at CombatSetUp (or late, before this stage) completes here.
        if (ReferenceEquals(_agentChoiceFight?.CombatState, state) && _agentChoiceEntryFightId == _battleFightSerial.ToString())
            _agentChoiceEntryHp = _battleEntryHp;
    }
}
