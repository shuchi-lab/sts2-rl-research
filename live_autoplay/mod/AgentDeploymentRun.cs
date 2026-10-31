namespace STS2_MCP;

// Deployment eligibility only. Public ascension and both frozen policy encoders stay native/unchanged.
internal static class AgentDeploymentRun
{
    internal const string Unsupported = "unsupported:requires_singleplayer_ironclad_a1_or_a2_ordinary_modifiers";

    internal static bool Allowed(string character, int ascension, int playerCount, int modifierCount, bool multiplayer)
        => character == "IRONCLAD" && (ascension == 1 || ascension == 2)
            && playerCount == 1 && modifierCount == 0 && !multiplayer;
}
