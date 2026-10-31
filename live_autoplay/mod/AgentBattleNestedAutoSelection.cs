using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

// A selector raised by a card that another card's effect auto-played inline
// (native CardCmd.AutoPlay awaited inside the parent's OnPlay, sharing its
// PlayerChoiceContext). Native 0.111 opens the screen; the frozen worker never
// does: CombatEngine.AutoPlayNested -> AutoPlay clears AutoPlayMayPause, so
// OpenCardSelection calls ResolveSelectionImmediately(kind, autoPick) and the
// policy has no decision and no parked_nested_autoplays row for it. The live
// continuation therefore answers the native screen automatically with the
// frozen pick. Known loss: the frozen pick is one card even when native allows
// more (Neow's Fury "up to 2"), and the policy is not consulted.
//
// Havoc is the other frozen shape: a chosen Havoc's child runs through
// PlayAwaitedForceExhaustChild/RunPotionBatchChild (AutoPlayMayPause=true), so
// the child's selection is the player's. The chosen root is PendingCardPlay
// (parked_play_card) and each Havoc child sits on PendingNestedAutoPlays,
// innermost first (parked_nested_autoplays). Once any ancestor is a
// non-pausable parent, every selection below it resolves immediately.
internal static class BattleNestedAutoSelection
{
    // Frozen parents whose children run through CombatEngine.AutoPlayNested.
    internal static readonly HashSet<string> NonPausableParents = new(StringComparer.Ordinal) { "Catastrophe" };
    // Frozen parents whose child parks for the player when the parent itself may park.
    internal static readonly HashSet<string> PausableParents = new(StringComparer.Ordinal) { "Havoc" };

    internal enum Shape { ImmediateFrozenPick, ParkedForPlayer }

    // pending: this player's started-unfinished card plays in start order (outermost first).
    internal static Shape Classify(IReadOnlyList<(string Card,bool Auto)> pending)
    {
        if (pending.Count < 2 || pending[0].Auto)
            throw new BattleProjectionUnsupported("Selection interrupted an automatic play without an enclosing chosen card play; nested/batch continuation context is not certified");
        bool immediate = false;
        for (int i = 1; i < pending.Count; i++)
        {
            string parent = pending[i - 1].Card;
            if (!pending[i].Auto)
                throw new BattleProjectionUnsupported("A chosen card play started inside " + parent + "'s unfinished play");
            if (NonPausableParents.Contains(parent)) immediate = true;
            else if (!PausableParents.Contains(parent))
                throw new BattleProjectionUnsupported("Selection interrupted an automatic play under " + parent +
                    "; its auto-play mechanism has no mapped frozen engine path");
        }
        return immediate ? Shape.ImmediateFrozenPick : Shape.ParkedForPlayer;
    }

    // Frozen PendingNestedAutoPlays order: ParkNestedChild runs as each child unwinds,
    // so the innermost child is added first.
    internal static T[] ParkedChildrenInnermostFirst<T>(IReadOnlyList<T> pending) => pending.Skip(1).Reverse().ToArray();

    // Frozen counters of the parked stack: PendingCardPlay, PendingNestedAutoPlays.Count,
    // and one SerpentFormSnapshots entry per started, unfinished play (root + children).
    internal static (int PendingCardPlay,int NestedDepth,int SerpentSnapshots) StackCounters(bool root,int children) =>
        (root ? 1 : 0, children, root ? 1 + children : 0);

    // Frozen block history subtracts only ActiveBlockPlay, which is the chosen root's while
    // a child is parked; a parked child's own gains are saved on the child and still count.
    internal static T[] BlockExcludedPlays<T>(IReadOnlyList<T> pending) => pending.Take(1).ToArray();

    internal enum Pick { First, Last }

    // Frozen ResolveSelectionImmediately for these sources' autoPick: one pile card.
    internal static readonly Dictionary<string,(string Pile,Pick Pick)> Sources = new(StringComparer.Ordinal) {
        ["NeowsFury"] = ("Discard", Pick.First),   // DiscardToHand, autoPick 0
        ["Headbutt"] = ("Discard", Pick.Last),     // DiscardToDrawPileTop, autoPick Count-1
    };

    internal static int FrozenPick(IReadOnlyList<(string Card,bool Auto)> pending,string source,bool sourceIsInnermost,
        string method,string pile,int eligibleCount,int maxSelect)
    {
        if (Classify(pending) != Shape.ImmediateFrozenPick)
            throw new BattleProjectionUnsupported("Frozen parks this nested automatic selection for the player");
        if (!sourceIsInnermost)
            throw new BattleProjectionUnsupported("Nested automatic selection source is not the innermost automatic play");
        if (!Sources.TryGetValue(source, out var rule) || method != "FromCombatPile" || pile != rule.Pile)
            throw new BattleProjectionUnsupported("Nested automatic selection lacks a frozen immediate-resolution rule: " + source + "/" + method + "/" + pile);
        if (eligibleCount <= 0 || maxSelect < 1)
            throw new BattleProjectionUnsupported("Nested automatic selection has no card for the frozen pick");
        return rule.Pick == Pick.First ? 0 : eligibleCount - 1;
    }
}
