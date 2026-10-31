using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    // Includes the bundle screen that relic AfterObtained (Scroll Boxes) awaits via
    // CardSelectCmd.FromChooseABundleScreen; only its captured selector certifies it.
    private static bool AgentSupportedSelectionSurface() =>
        NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen or NChooseACardSelectionScreen or NChooseABundleSelectionScreen
        || NCombatRoom.Instance?.Ui?.Hand?.IsInCardSelection == true;

    private static GameAction? AgentNativeChoiceAction(PlayerChoiceContext? context)
    {
        var visited = new HashSet<PlayerChoiceContext>(ReferenceEqualityComparer.Instance);
        while (context is BranchingPlayerChoiceContext branch)
        {
            if (!visited.Add(context)) return null;
            // PotionModel.OnUseWrapper passes a Branching context to OnUse.
            // Its native SignalPlayerChoiceEnded delegates to created ?? original;
            // use that exact same owner, without borrowing an unrelated action.
            var type = typeof(BranchingPlayerChoiceContext);
            context = (type.GetField("_createdContext", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(branch) ?? type.GetField("_originalContext", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(branch)) as PlayerChoiceContext;
        }
        return context switch
        {
            GameActionPlayerChoiceContext direct => direct.Action,
            HookPlayerChoiceContext hook => hook.GameAction,
            _ => null
        };
    }

    private static AgentQueueState AgentCurrentQueueState(bool selectionSurface)
    {
        Task? selector = null;
        PlayerChoiceContext? context = null;
        if (_battleSelection is { Task.IsCompleted: false } battle)
        { selector = battle.Task; context = battle.ChoiceContext; }
        else if (AgentSelectionContext is { Finished: false } choice)
        { selector = choice.SelectorTask; context = choice.ChoiceContext; }
        // Both getters refer to the action paused by this captured native context.
        // A visible selection screen by itself cannot bypass an executing queue.
        var action = AgentNativeChoiceAction(context);
        var executor = RunManager.Instance.ActionExecutor;
        var execution = executor?.FinishedExecutingActions() ?? Task.CompletedTask;
        if (execution.IsFaulted) return AgentQueueState.Faulted;
        if (execution.IsCanceled) return AgentQueueState.Cancelled;
        var queue = AgentQueueReadiness.Evaluate(
            RunManager.Instance.ActionQueueSet?.BecameEmpty() ?? Task.CompletedTask,
            executor?.IsRunning ?? false,
            selectionSurface && action != null && ReferenceEquals(action, executor?.CurrentlyRunningAction),
            selector, action?.State.ToString());
        if (queue != AgentQueueState.Ready) return queue;
        return AgentChoiceEventTransitionState(RunManager.Instance.DebugOnlyGetState(),
            selectionSurface, combatSurface: true, rewardSurface: true);
    }
}
