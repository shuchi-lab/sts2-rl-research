using System.Threading.Tasks;

namespace STS2_MCP;

internal enum AgentQueueState { Waiting, Ready, Faulted, Cancelled }

internal static class AgentQueueReadiness
{
    internal static AgentQueueState Evaluate(Task queueSettlement, bool executorBusy,
        bool supportedSelection, Task? selectorTask, string? owningActionState)
    {
        // IsCompleted alone also accepts faults. Native IsEmpty has that meaning,
        // so require successful completion before publishing a settled state.
        if (queueSettlement.IsFaulted) return AgentQueueState.Faulted;
        if (queueSettlement.IsCanceled) return AgentQueueState.Cancelled;
        // A captured selector intentionally suspends its owning queued action.
        if (!queueSettlement.IsCompleted && supportedSelection && selectorTask is { IsCompleted: false }
            && owningActionState == "GatheringPlayerChoice") return AgentQueueState.Ready;
        return queueSettlement.IsCompletedSuccessfully && !executorBusy
            ? AgentQueueState.Ready : AgentQueueState.Waiting;
    }
}
