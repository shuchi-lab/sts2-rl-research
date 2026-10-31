using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using STS2_MCP.Recording;

namespace STS2_MCP;

public static partial class McpMod
{
    private sealed class ActionTrace
    {
        public required RecordingJournal Session;
        public long Operation;
        public uint? PreviousId;
        public required string Origin;
    }
    private sealed record RewardTrace(int SetId, int Index);
    private sealed record OperationTrace(RecordingJournal Session, long Id);
    private static readonly ConditionalWeakTable<GameAction, ActionTrace> _actionTraces = new();
    private static readonly ConditionalWeakTable<Reward, RewardTrace> _rewardTraces = new();
    private static readonly HashSet<long> _pausedActions = new();
    private static readonly HashSet<string> _reservedChoices = new();
    private static bool _loadedSave;
    [ThreadStatic] private static bool _recorderApiAction;

    private static void InstallRecordingHooks()
    {
        var harmony = new Harmony("com.sts2mcp.manual-recorder");
        void Hook(Type type, string name, string? prefix = null, string? postfix = null)
        {
            try
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == name).ToArray();
                if (methods.Length != 1) throw new MissingMethodException($"Expected one {type.Name}.{name}, got {methods.Length}");
                harmony.Patch(methods[0],
                    prefix == null ? null : new HarmonyMethod(typeof(McpMod), prefix),
                    postfix == null ? null : new HarmonyMethod(typeof(McpMod), postfix),
                    finalizer: prefix == nameof(RecorderApiRequest) ? new HarmonyMethod(typeof(McpMod), nameof(RecorderApiFinished)) : null);
                _recordingHooks.Add(type.Name + "." + name);
            }
            catch (Exception ex) { _recordingHookErrors.Add(type.Name + "." + name + ": " + ex.Message); }
        }
        Hook(typeof(ActionQueueSynchronizer), "RequestEnqueue", nameof(RecorderActionRequested));
        Hook(typeof(GameAction), "OnEnqueued", postfix: nameof(RecorderActionEnqueued));
        Hook(typeof(PlayerChoiceSynchronizer), "SyncLocalChoice", nameof(RecorderChoice));
        Hook(typeof(PlayerChoiceSynchronizer), "ReserveChoiceId", postfix: nameof(RecorderChoiceReserved));
        Hook(typeof(CardSelectCmd), "UseSelector", nameof(RecorderSelector));
        Hook(typeof(CardSelectCmd), "PushSelector", nameof(RecorderSelector));
        Hook(typeof(RunManager), "SetUpNewSingleplayer", nameof(RecorderNewRun));
        Hook(typeof(RunManager), "SetUpSavedSingleplayer", nameof(RecorderLoadedRun));
        Hook(typeof(RunManager), "OnEnded", postfix: nameof(RecorderRunEnded));
        Hook(typeof(RunManager), "CleanUp", nameof(RecorderCleanup), nameof(RecorderCleanupFinished));
        Hook(typeof(RunManager), "Abandon", nameof(RecorderDecision));
        Hook(typeof(RunManager), "ProceedFromTerminalRewardsScreen", nameof(RecorderDecision), nameof(RecorderTask));
        Hook(typeof(EventSynchronizer), "ChooseLocalOption", nameof(RecorderDecision));
        Hook(typeof(RewardsSetSynchronizer), "SelectLocalReward", nameof(RecorderDecision), nameof(RecorderTask));
        Hook(typeof(RewardsSetSynchronizer), "BeginRewardsSet", postfix: nameof(RecorderRewardsSet));
        Hook(typeof(RewardsSetSynchronizer), "SkipLocalRewardsSet", nameof(RecorderDecision));
        Hook(typeof(RewardsSetSynchronizer), "BeforeLeavingRoom", nameof(RecorderDecision));
        Hook(typeof(RestSiteSynchronizer), "ChooseLocalOption", nameof(RecorderDecision), nameof(RecorderTask));
        Hook(typeof(RestSiteSynchronizer), "BeforeLocalRestSiteExited", nameof(RecorderDecision));
        Hook(typeof(OneOffSynchronizer), "DoLocalTreasureRoomRewards", nameof(RecorderDecision), nameof(RecorderTask));
        Hook(typeof(OneOffSynchronizer), "DoLocalCrystalSphereRewards", nameof(RecorderDecision), nameof(RecorderTask));
        Hook(typeof(TreasureRoomRelicSynchronizer), "PickRelicLocally", nameof(RecorderDecision));
        Hook(typeof(MerchantEntry), "OnTryPurchaseWrapper", nameof(RecorderDecision), nameof(RecorderTask));
        Hook(typeof(MerchantCardRemovalEntry), "OnTryPurchaseWrapper", nameof(RecorderDecision), nameof(RecorderTask));
        Hook(typeof(CardRewardAlternative), "Generate", postfix: nameof(RecorderRewardAlternatives));
        Hook(typeof(CrystalSphereMinigame), "SetTool", nameof(RecorderDecision));
        Hook(typeof(CrystalSphereMinigame), "CellClicked", nameof(RecorderDecision), nameof(RecorderTask));
        // Record every bridge request as well: hooks alone cannot distinguish mouse and automation.
        Hook(typeof(McpMod), "ExecuteAction", nameof(RecorderApiRequest), nameof(RecorderApiFinished));
        Hook(typeof(McpMod), "ExecuteMenuSelect", nameof(RecorderApiRequest), nameof(RecorderApiFinished));
    }

    private static void RecorderApiRequest(MethodBase __originalMethod, object[] __args)
    {
        _recorderApiAction = true;
        RecorderSafe(() => { if (IsRecording) { _bridgeUsed = true; _recording!.Append("bridge_request", new { method = __originalMethod.Name, arguments = __args.Select(RecordArgument).ToArray() }); } });
    }
    private static void RecorderApiFinished() => _recorderApiAction = false;
    private static void RecorderSelector(MethodBase __originalMethod) => RecorderSafe(() =>
    {
        if (!IsRecording) return;
        _selectorScopes++;
        _recording!.Append("selector_scope", new { method = __originalMethod.Name, note = "Automatic selector bypasses player-choice sync; coverage requires separate validation." });
    });

    private static void RecorderNewRun() => RecorderSafe(() => { _loadedSave = false; });
    private static void RecorderLoadedRun() => RecorderSafe(() =>
    {
        _loadedSave = true;
        if (IsRecording) RecordBoundary("loaded_existing_run", null);
    });
    private static void RecorderRunEnded(bool isVictory) => RecorderSafe(() =>
    {
        if (!IsRecording || _recordedRunEnded) return;
        _recordedRunEnded = true;
        _abandonedRun = RunManager.Instance.IsAbandoned;
        RecordBoundary("run_ended", new { victory = isVictory, abandoned = _abandonedRun });
        SnapshotGameFiles("run_ended");
    });
    private static void RecorderCleanup(bool graceful) => RecorderSafe(() =>
    {
        if (IsRecording && _recordedRunStarted)
            RecordBoundary("before_run_cleanup", new { graceful });
    });
    private static void RecorderCleanupFinished() => RecorderSafe(() =>
    {
        if (IsRecording && _recordedRunStarted)
            StopRecording(_recordedRunEnded ? "run_finished" : "run_session_closed");
    });

    private static ActionTrace? TraceAction(GameAction action)
    {
        if (!IsRecording) return null;
        if (_actionTraces.TryGetValue(action, out var existing)) return existing.Session == _recording ? existing : null;
        var trace = new ActionTrace { Session = _recording!, Operation = ++_recordingOperation, PreviousId = action.Id,
            Origin = _recorderApiAction ? "bridge" : "game_input_or_game_derived" };
        _actionTraces.Add(action, trace);
        void Observe(string phase, GameAction a) => RecorderSafe(() =>
        {
            if (_recording != trace.Session) return;
            if (phase == "paused") _pausedActions.Add(trace.Operation);
            if (phase is "resumed" or "canceled" or "finished") _pausedActions.Remove(trace.Operation);
            RecordAction(phase, a, trace);
        });
        action.BeforeExecuted += a => Observe("before_execute", a);
        action.BeforePausedForPlayerChoice += a => Observe("paused", a);
        action.BeforeReadyToResumeAfterPlayerChoice += a => Observe("ready_to_resume", a);
        action.BeforeResumedAfterPlayerChoice += a => Observe("resumed", a);
        action.BeforeCancelled += a => Observe("canceled", a);
        action.AfterFinished += a => Observe("finished", a);
        return trace;
    }
    private static void RecordAction(string phase, GameAction action, ActionTrace trace)
    {
        object? payload = action.RecordableToReplay ? ProjectNet(action.ToNetAction()) : null;
        bool derived = action.GetType().Name is "GenericHookGameAction" or "ReadyToBeginEnemyTurnAction" or "MoveToMapCoordAction";
        var detail = new
        {
            operation_id = trace.Operation, phase, action_type = action.GetType().Name,
            action_id = action.Id, previous_action_id = trace.PreviousId,
            owner_id = action.OwnerId, queue_type = action.ActionType.ToString(), state = action.State.ToString(),
            origin = trace.Origin,
            derived,
            payload, error = action.Exception?.GetBaseException().Message,
        };
        if (derived || phase is "enqueued" or "ready_to_resume" or "resumed")
            _recording!.Append("game_action", new { detail });
        else RecordBoundary("game_action", detail);
        trace.PreviousId = action.Id;
    }
    private static void RecorderActionRequested(GameAction action) => RecorderSafe(() =>
    {
        var trace = TraceAction(action);
        if (trace != null) RecordAction("requested", action, trace);
    });
    private static void RecorderActionEnqueued(GameAction __instance) => RecorderSafe(() =>
    {
        var trace = TraceAction(__instance);
        if (trace != null) RecordAction("enqueued", __instance, trace);
    });

    private static void RecorderChoice(Player player, uint choiceId, PlayerChoiceResult result) => RecorderSafe(() =>
    {
        if (!IsRecording) return;
        _recording!.Append("choice_submitted", new { player_id = player.NetId, choice_id = choiceId, type = result.ChoiceType.ToString() });
        _reservedChoices.Remove(player.NetId + ":" + choiceId);
        var net = result.ToNetData();
        BeginObservedOperation("player_choice", new
        {
            player_id = player.NetId, choice_id = choiceId, type = net.type.ToString(),
            paused_operation_ids = _pausedActions.ToArray(),
            canonical_cards = net.canonicalCards?.Select(RecordCard).ToArray(),
            combat_cards = net.combatCards?.Select(c => new { combat_card_id = c.CombatCardIndex, card = RecordCard(c.ToCardModelOrNull()) }).ToArray(),
            deck_cards = net.deckCards?.Select(c => new { deck_index = c.DeckIndex, card = RecordCard(player.Deck.Cards[(int)c.DeckIndex]) }).ToArray(),
            mutable_cards = net.mutableCards, mutable_card_owner = net.mutableCardOwner,
            indexes = net.indexes, selected_player_id = net.playerId,
        });
    });
    private static void RecorderChoiceReserved(Player player, uint __result) => RecorderSafe(() =>
    {
        if (IsRecording) { _reservedChoices.Add(player.NetId + ":" + __result); _recording!.Append("choice_reserved", new { player_id = player.NetId, choice_id = __result }); }
    });

    private static void RecorderRewardsSet(RewardsSet set) => RecorderSafe(() =>
    {
        if (!IsRecording) return;
        for (int i = 0; i < set.Rewards.Count; i++)
        {
            _rewardTraces.Remove(set.Rewards[i]);
            _rewardTraces.Add(set.Rewards[i], new RewardTrace(set.Id, i));
        }
        RecordBoundary("rewards_offered", new { set_id = set.Id, rewards = set.Rewards.Select(RecordArgument).ToArray() });
    });
    private static void RecorderRewardAlternatives(CardReward cardReward, IReadOnlyList<CardRewardAlternative> __result) => RecorderSafe(() =>
    {
        if (IsRecording) RecordBoundary("card_reward_offered", new
        {
            cards = cardReward.Cards.Select(RecordCard).ToArray(),
            alternatives = __result.Select(x => x.OptionId).ToArray(),
        });
    });

    private static void RecorderDecision(object __instance, MethodBase __originalMethod, object[] __args, out OperationTrace? __state)
    {
        OperationTrace? trace = null;
        RecorderSafe(() =>
        {
            if (!IsRecording) return;
            _recording!.Append("decision_invoked", new { method = __originalMethod.DeclaringType!.Name + "." + __originalMethod.Name,
                arguments = __args.Select(a => a is int or uint or bool or string ? a : null).ToArray() });
            object? context = null;
            if (__instance is EventSynchronizer ev)
            {
                var model = ev.GetLocalEvent();
                context = new { event_id = model.Id.ToString(), page_index = AccessTools.Field(typeof(EventSynchronizer), "_pageIndex").GetValue(ev), options = model.CurrentOptions.Select(o => o.TextKey).ToArray() };
            }
            else if (__instance is RestSiteSynchronizer rest)
                context = new { options = rest.GetLocalOptions().Select(o => o.OptionId).ToArray() };
            else if (__instance is CrystalSphereMinigame crystal)
                context = new { tool = crystal.CrystalSphereTool.ToString(), divination_count = crystal.DivinationCount, rng = crystal.Rng.ToSerializable(), cells = crystal.cells.Cast<CrystalSphereCell>().Select(c => new { x = c.X, y = c.Y, hidden = c.IsHidden }).ToArray() };
            else if (__instance is MerchantEntry entry)
            {
                var inventory = __args.FirstOrDefault() as MerchantInventory;
                context = new { entry_type = entry.GetType().Name, index = inventory?.AllEntries.ToList().IndexOf(entry), base_cost = AccessTools.Field(typeof(MerchantEntry), "_cost").GetValue(entry), stocked = entry.IsStocked };
            }
            else if (__instance is TreasureRoomRelicSynchronizer treasure)
                context = new { relics = treasure.CurrentRelics?.Select(r => r.Id.ToString()).ToArray() };
            long id = BeginObservedOperation(__originalMethod.DeclaringType!.Name + "." + __originalMethod.Name,
                new { arguments = __args.Select(RecordArgument).ToArray(), context, origin = _recorderApiAction ? "bridge" : "game_input_or_game_derived" });
            trace = new OperationTrace(_recording!, id);
        });
        __state = trace;
    }
    private static void RecorderTask(MethodBase __originalMethod, Task __result, OperationTrace? __state) => RecorderSafe(() =>
    {
        if (__state != null && _recording == __state.Session)
            WatchOperation(__state.Id, __originalMethod.DeclaringType!.Name + "." + __originalMethod.Name, __result);
    });

    // Project only known primitives/models. Never serialize arbitrary live game object graphs.
    private static object? RecordArgument(object? value) => value switch
    {
        null => null,
        CardModel card => RecordCard(card),
        CrystalSphereCell cell => new { x = cell.X, y = cell.Y },
        AbstractModel model => new { id = model.Id.ToString() },
        Player player => new { player_id = player.NetId },
        Reward reward => new { reward_type = reward.GetType().Name, location = _rewardTraces.TryGetValue(reward, out var r) ? r : null },
        string or bool or int or uint or long or ulong or float or double or decimal => value,
        Enum e => e.ToString(),
        _ => new { object_type = value.GetType().FullName },
    };
    private static object? ProjectNet(object? value)
    {
        if (value == null || value is string || value.GetType().IsPrimitive || value is decimal) return value;
        if (value is Enum) return value.ToString();
        if (value is ModelId id) return id.ToString();
        if (value is NetCombatCard card) return new { combat_card_id = card.CombatCardIndex, card = RecordCard(card.ToCardModelOrNull()) };
        if (value is IEnumerable sequence) return sequence.Cast<object?>().Select(ProjectNet).ToArray();
        var result = new Dictionary<string, object?>();
        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)) result[field.Name] = ProjectNet(field.GetValue(value));
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            if (property.GetIndexParameters().Length == 0 && property.CanRead) result[property.Name] = ProjectNet(property.GetValue(value));
        return result;
    }
}
