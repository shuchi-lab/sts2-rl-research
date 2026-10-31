using System;
using System.Collections.Generic;

namespace STS2_MCP;

internal enum BattleGenerationKind { Started, Finished, Generated }

// One past native history entry: a card play opening/closing, or a generated card.
internal readonly record struct BattleGenerationEvent(BattleGenerationKind Kind,object? Play,string CardEntry,bool ByPlayer);

internal static class BattleGeneratedCounter
{
    // Selected frozen card generators that add a card without NoteGeneratedCard, so its
    // CardsGeneratedThisCombat stays put. Native still writes a CardGeneratedEntry inside
    // the play. Frozen worker_build08 AddRandomInfernalBladeAttack: Hand.Add only.
    private static readonly HashSet<string> FrozenUncounted=new(StringComparer.Ordinal) { "INFERNAL_BLADE" };

    // Native CardModel writes CardPlayStarted before OnPlay and CardPlayFinished after it,
    // so a generated entry belongs to the innermost play still open at that point.
    internal static int CardsGeneratedThisCombat(IEnumerable<BattleGenerationEvent> history)
    {
        var open=new List<(object Play,string Entry)>();
        int count=0;
        foreach(var e in history)
        {
            switch(e.Kind)
            {
                case BattleGenerationKind.Started: open.Add((e.Play!,e.CardEntry)); break;
                case BattleGenerationKind.Finished:
                    int i=open.FindLastIndex(p => ReferenceEquals(p.Play,e.Play));
                    if(i>=0) open.RemoveAt(i);
                    break;
                case BattleGenerationKind.Generated:
                    if(e.ByPlayer && !(open.Count>0 && FrozenUncounted.Contains(open[^1].Entry))) count++;
                    break;
            }
        }
        return count;
    }
}
