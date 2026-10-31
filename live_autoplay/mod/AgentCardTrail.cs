using System;
using System.Collections.Generic;

namespace STS2_MCP;

// Turbo-only guard for the decorative NCardTrail (Line2D). Native CreatePoint
// subdivides the jump since its last point in 48px steps with no bound; at
// accelerated clocks NHandCardHolder's unclamped delta*7/8/10 lerps diverge,
// a flown card then starts at a huge or non-finite position, and the loop
// allocates until OutOfMemory. In turbo the line is cleared and never
// extended. The owning NCardTrailVfx, its sprites, FadeOut tween and
// QueueFree are untouched; normal mode runs the native method unchanged.
internal static class AgentCardTrailGuard
{
    public static long Suppressed { get; private set; }

    // Returns whether native CreatePoint should run. Points and ages are
    // cleared together so native _Process keeps indexing them in lockstep.
    public static bool Allow(bool turbo, List<float> ages, Action clearPoints, Action forgetLast)
    {
        if (!turbo) return true;
        clearPoints();
        ages.Clear();
        forgetLast();
        Suppressed++;
        return false;
    }
}
