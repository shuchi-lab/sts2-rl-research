using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace STS2_MCP;

public static partial class McpMod
{
    // "applied", or the failure that left native trails unguarded in turbo.
    private static string _agentCardTrailPatch = "not applied";

    // Patched explicitly (no [HarmonyPatch]) so it neither depends on nor can
    // abort the optional settings-UI PatchAll.
    private static void ApplyAgentCardTrailPatch()
    {
        try
        {
            var target = AccessTools.Method(typeof(NCardTrail), "CreatePoint", new[] { typeof(Vector2), typeof(double) })
                ?? throw new MissingMethodException("NCardTrail.CreatePoint(Vector2, double)");
            AgentCardTrailPatch.Bind();
            new Harmony("com.sts2mcp.cardtrail").Patch(target,
                prefix: new HarmonyMethod(typeof(AgentCardTrailPatch), nameof(AgentCardTrailPatch.Prefix)));
            _agentCardTrailPatch = "applied";
        }
        catch (Exception ex)
        {
            _agentCardTrailPatch = ex.GetType().Name + ": " + ex.Message;
            GD.PrintErr("[STS2 Agent] Turbo card-trail guard not applied: " + _agentCardTrailPatch);
        }
    }

    internal static class AgentCardTrailPatch
    {
        private static AccessTools.FieldRef<NCardTrail, List<float>>? _ages;
        private static AccessTools.FieldRef<NCardTrail, Vector2?>? _last;

        internal static void Bind()
        {
            _ages = AccessTools.FieldRefAccess<NCardTrail, List<float>>("_pointAge");
            _last = AccessTools.FieldRefAccess<NCardTrail, Vector2?>("_lastPointPosition");
        }

        internal static bool Prefix(NCardTrail __instance) =>
            AgentCardTrailGuard.Allow(_agentTurbo.Enabled, _ages!(__instance), __instance.ClearPoints,
                () => _last!(__instance) = null);
    }
}
