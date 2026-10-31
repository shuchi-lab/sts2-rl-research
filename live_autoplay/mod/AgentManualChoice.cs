using System.Text.Json;

namespace STS2_MCP;

// Pure manual-input bookkeeping shared by the native bridge and transition fixtures.
internal static class AgentManualChoice
{
    internal const string IncompleteReason = "unsupported:manual_strategy_change_requires_fresh_observed_run";
    private const string ChoiceSchema = "rs1/public_run_observation/v2";

    internal static string? Capture(string? marker, bool gameplay, bool overlay, string owner, string? schema,
        bool pendingChoice, string currentSignature, string? pendingNativeSignature)
    {
        if (marker != null || !gameplay || overlay) return marker;
        bool currentChoice = owner == "strategy" || owner == "automatic" && schema == ChoiceSchema;
        if (!currentChoice && !pendingChoice) return null;
        if (!pendingChoice) return currentSignature;
        // A pending Choice can outlive its current UI surface. Preserve its bound policy snapshot,
        // not the transient Waiting signature, before gameplay input cancels Stage0.
        using var snapshot = JsonDocument.Parse(pendingNativeSignature!);
        return snapshot.RootElement.GetProperty("policy").GetString()
            ?? throw new System.InvalidOperationException("Pending Choice lacks its bound public signature.");
    }

    internal static bool Changed(string? marker, string currentSignature, bool pending, bool ready, string owner, string? schema)
        => marker != null && marker != currentSignature && !pending && ready
            && (owner is "strategy" or "battle" || owner == "automatic" && schema == ChoiceSchema);
}
