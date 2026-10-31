using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace STS2_MCP;

public static partial class McpMod
{
    // "applied", or the failure that left the PunchOff Instant loop unguarded.
    private static string _agentPunchOffPatch = "not applied";

    // Patched explicitly (no [HarmonyPatch]); see AgentPunchOff.cs.
    private static void ApplyAgentPunchOffPatch()
    {
        try
        {
            var target = AccessTools.Method(typeof(PunchOff), "PunchEachOther", Type.EmptyTypes)
                ?? throw new MissingMethodException("PunchOff.PunchEachOther()");
            new Harmony("com.sts2mcp.punchoff").Patch(target,
                prefix: new HarmonyMethod(typeof(AgentPunchOffPatch), nameof(AgentPunchOffPatch.Prefix)));
            _agentPunchOffPatch = "applied";
        }
        catch (Exception ex)
        {
            _agentPunchOffPatch = ex.GetType().Name + ": " + ex.Message;
            GD.PrintErr("[STS2 Agent] PunchOff Instant loop guard not applied: " + _agentPunchOffPatch);
        }
    }

    internal static class AgentPunchOffPatch
    {
        internal static bool Prefix(ref Task __result)
        {
            bool yields;
            try
            {
                // Same order and short-circuits as native Cmd.Wait.
                yields = !NonInteractiveMode.IsActive && (NGame.Instance == null
                    || AgentPunchOffGuard.WaitYields(false, true,
                        SaveManager.Instance.PrefsSave.FastMode == FastModeType.Instant, CombatManager.Instance.IsEnding));
            }
            catch (Exception)
            {
                return true;
            }
            if (AgentPunchOffGuard.Allow(yields)) return true;
            __result = Task.CompletedTask;
            return false;
        }
    }
}
