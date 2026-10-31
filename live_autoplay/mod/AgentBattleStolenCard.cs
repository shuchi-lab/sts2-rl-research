using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

internal sealed record BattleSwipeView(object Power,object? Target,object? CardOwner,object? Card);

internal sealed class BattleStolenCardLedger
{
    private sealed record Snapshot(object Card,double[] Features);
    private readonly Dictionary<object,Snapshot> _snapshots=new(ReferenceEqualityComparer.Instance);
    internal void Capture(object power,object card,double[] features,bool uncertifiedSingleTurnSly=false)
    {
        if(uncertifiedSingleTurnSly)
            throw new BattleProjectionUnsupported("Stolen card prior single-turn Sly cleanup differs from frozen semantics");
        if(features.Length!=28 || features.Any(x=>!double.IsFinite(x)))
            throw new BattleProjectionUnsupported("Invalid public stolen-card snapshot");
        if(_snapshots.TryGetValue(power,out var prior))
        {
            if(!ReferenceEquals(prior.Card,card))
                throw new BattleProjectionUnsupported("Swipe card changed after witnessed theft");
            return;
        }
        _snapshots.Add(power,new(card,(double[])features.Clone()));
    }
    internal double[]? Project(object local,IReadOnlyList<BattleSwipeView> swipes)
    {
        if(swipes.Count==0) return null;
        if(swipes.Count!=1) throw new BattleProjectionUnsupported("Multiple Swipe cards exceed frozen stolen-card slot");
        var swipe=swipes[0];
        if(swipe.Card==null || !ReferenceEquals(swipe.Target,local) || !ReferenceEquals(swipe.CardOwner,local))
            throw new BattleProjectionUnsupported("Swipe target/card owner is not the local player");
        if(!_snapshots.TryGetValue(swipe.Power,out var snapshot) || !ReferenceEquals(snapshot.Card,swipe.Card))
            throw new BattleProjectionUnsupported("Stolen card has no matching witnessed theft snapshot");
        return (double[])snapshot.Features.Clone();
    }
    internal void Clear() => _snapshots.Clear();
}
