using System.Text.Json;

namespace STS2_MCP;

internal static class AgentStageChecks
{
    internal static bool RunReady(int players, int acts, int actIndex, bool roomEntered, bool mapInitialized)
        => players > 0 && acts > 0 && actIndex >= 0 && actIndex < acts && roomEntered && mapInitialized;

    internal static bool? SelectionReady(string action, JsonElement state)
    {
        string type = state.TryGetProperty("state_type", out var kind) ? kind.GetString()! : "unknown";
        if (action == "select_card") return type is "card_select" or "choose_card";
        if (action is "confirm_selection" or "combat_confirm_selection")
        {
            string expected = action == "combat_confirm_selection" ? "hand_select" : "card_select";
            return type == expected && state.TryGetProperty(expected, out var selection)
                && selection.TryGetProperty("can_confirm", out var ready) && ready.GetBoolean();
        }
        return null;
    }

    // A full-belt reward compound's claim after a use_potion stage. Installed EntropicBrew.OnUse
    // procures while HasOpenPotionSlots, refilling the slot it vacated; PotionReward.OnSelect then
    // returns false on TooFull and the row stays, so clicking it would never change the state.
    internal static bool RewardClaimAfterBeltRefill(JsonElement previous, JsonElement stage, bool beltFull)
        => beltFull && previous.GetProperty("action").GetString() == "use_potion"
            && stage.GetProperty("action").GetString() == "claim_reward"
            && stage.TryGetProperty("reward_handle", out _) && stage.TryGetProperty("expected_reward_potion_id", out _);

    internal static bool Witnessed(JsonElement stage, string before, string after, JsonElement state)
    {
        if (stage.GetProperty("action").GetString() == "crystal_sphere_set_tool")
            return state.TryGetProperty("crystal_sphere", out var sphere)
                && sphere.TryGetProperty("tool", out var tool) && tool.GetString() == stage.GetProperty("tool").GetString();
        return before != after;
    }
}
