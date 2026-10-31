using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Rooms;

namespace STS2_MCP;

public static partial class McpMod
{
    private static readonly ConditionalWeakTable<RunState, AgentChoiceSavedHistory> AgentLoadedHistories = new();
    private static readonly AgentChoiceHistoryStore AgentHistoryStore = new(Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "agent_history"));
    private static bool _agentRestoredRestPending;

    private static void InstallAgentChoiceResumeHooks(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(RunSaveManager), "SaveRun", new[] { typeof(SerializableRun), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceSaving)));
        harmony.Patch(AccessTools.Method(typeof(RunState), "FromSerializable"),
            postfix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceLoading)));
    }

    private static string AgentChoiceSaveKey(SerializableRun save)
    {
        var info = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        return AgentChoiceHistoryStore.Key(JsonSerializer.Serialize(save, info));
    }

    private static void AgentChoiceSaving(SerializableRun save, bool isMultiplayer)
    {
        try
        {
            if (isMultiplayer || _agentChoiceRun == null
                || !ReferenceEquals(_agentChoiceRun, RunManager.Instance.DebugOnlyGetState())) return;
            if (!_agentChoiceHistoryComplete || _agentChoiceHookError != null)
            {
                AgentHistoryStore.Forget(AgentChoiceSaveKey(save));
                return;
            }
            int decisions = AgentStrategicDecisions, restMask = AgentRestTakenMask;
            // The game saves a completed native change before the bridge's final
            // receipt settles. Include that choice once in this saved snapshot.
            if (_agentExecution is { ChoiceDecision: true } pending && AgentNativeSignature() != pending.BeforeSignature)
                (decisions, restMask) = AgentChoiceSemantics.RecordChoiceCommit(decisions, restMask,
                    pending.Candidate.Kind, pending.Owner == "strategy" && !AgentChoiceAutomaticKinds.Contains(pending.Candidate.Kind));
            var fights = AgentChoiceFights.ToList();
            // CombatManager saves a pre-finished room before firing CombatWon.
            // Include its already captured pre-heal terminal in the saved copy;
            // the normal callback will add it to live history exactly once.
            if (save.PreFinishedRoom != null && _agentChoiceFight is CombatRoom room && !AgentChoiceClosed.Contains(room))
                fights.Add(AgentChoiceFightRecord(room, "victory"));
            AgentHistoryStore.Save(AgentChoiceSaveKey(save),
                AgentChoiceHistoryStore.Capture(decisions, restMask, fights));
        }
        catch (Exception e) { GD.PrintErr("[STS2 Agent] Choice history save failed: " + e.Message); }
    }

    private static void AgentChoiceLoading(SerializableRun save, RunState __result)
    {
        try
        {
            var history = AgentHistoryStore.Load(AgentChoiceSaveKey(save));
            if (history != null) AgentLoadedHistories.Add(__result, history);
        }
        catch (Exception e) { GD.PrintErr("[STS2 Agent] Choice history load failed: " + e.Message); }
    }

    private static void AgentChoiceRestoreHistory(RunState run)
    {
        if (!AgentLoadedHistories.TryGetValue(run, out var memory)) return;
        AgentChoiceFights.AddRange(memory.RestoreFights());
        AgentStrategicDecisions = memory.StrategicDecisions;
        AgentRestTakenMask = memory.RestTakenMask;
        _agentRestoredRestPending = true;
        _agentChoiceHistoryComplete = true;
        GD.Print($"[STS2 Agent] Restored Choice history: {AgentChoiceFights.Count} fights, {AgentStrategicDecisions} decisions.");
    }
}
