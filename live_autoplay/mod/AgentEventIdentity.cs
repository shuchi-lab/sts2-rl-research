using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace STS2_MCP;

// EventRoom.EnterInternal awaits the room's asset preload before EventSynchronizer.BeginEvent
// replaces the previous room's mutable events. In that window CurrentRoom already names the new
// canonical event while the synchronizer still holds an unrelated earlier event (or none), so
// EventRoom.LocalMutableEvent must not be cast or projected until it is a clone of this room's event.
internal static class AgentEventIdentity
{
    public static EventModel? Current(IReadOnlyList<EventModel>? events, int localSlot, EventModel canonical)
    {
        if (events == null || localSlot < 0 || localSlot >= events.Count) return null;
        var local = events[localSlot];
        return local != null && local.GetType() == canonical.GetType() && local.Id.Entry == canonical.Id.Entry
            ? local : null;
    }
}
