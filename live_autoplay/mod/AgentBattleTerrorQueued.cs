using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

// The frozen worker appends each synthetic buff to the enemy's list when it is born
// (BuffSystem.Apply), so it sits after every power held then and before later ones.
// A birth stores those native power INSTANCES and a sequence; nothing is read from the future.
internal sealed record BattleSyntheticBirth(long Sequence,object[] Powers,object? Move);

internal sealed class BattleRavenousSpan(object owner,long epoch)
{
    internal object Owner { get; } = owner;
    internal long Epoch { get; } = epoch;
    // The STUNNED move this AfterDeath's stun installed; null if native refused it.
    internal object? AcceptedMove { get; set; }
}

internal sealed class BattleSyntheticPowerLedger
{
    // One combat's ledger; BattleCombatSetUp clears it.
    internal static readonly BattleSyntheticPowerLedger Current=new();
    private readonly Dictionary<object,Dictionary<string,BattleSyntheticBirth>> _births=new(ReferenceEqualityComparer.Instance);
    private long _sequence;
    // Ravenous AfterDeath calls in progress, per owner; Clear starts a new epoch so a late
    // completion from an earlier combat records nothing.
    private readonly Dictionary<object,List<BattleRavenousSpan>> _ravenous=new(ReferenceEqualityComparer.Instance);
    private long _epoch;
    internal void Clear()
    {
        _births.Clear();
        _ravenous.Clear();
        _epoch++;
    }
    internal BattleRavenousSpan RavenousBegan(object owner)
    {
        var span=new BattleRavenousSpan(owner,_epoch);
        if(!_ravenous.TryGetValue(owner,out var spans)) _ravenous[owner]=spans=new();
        spans.Add(span);
        return span;
    }
    internal BattleRavenousSpan? RavenousPending(object owner) =>
        _ravenous.TryGetValue(owner,out var spans) && spans.Count>0 ? spans[^1] : null;
    internal bool RavenousEnded(BattleRavenousSpan span)
    {
        if(span.Epoch!=_epoch || !_ravenous.TryGetValue(span.Owner,out var spans) || !spans.Remove(span)) return false;
        if(spans.Count==0) _ravenous.Remove(span.Owner);
        return true;
    }
    internal void Born(object owner,string marker,object[] powers,object? move,bool rebirth)
    {
        if(!_births.TryGetValue(owner,out var markers)) _births[owner]=markers=new(StringComparer.Ordinal);
        if(rebirth || !markers.ContainsKey(marker)) markers[marker]=new(++_sequence,powers,move);
    }
    internal void Died(object owner,string marker)
    {
        if(_births.TryGetValue(owner,out var markers)) markers.Remove(marker);
    }
    internal BattleSyntheticBirth? Birth(object owner,string marker) =>
        _births.TryGetValue(owner,out var markers) && markers.TryGetValue(marker,out var birth) ? birth : null;

    // Native powers keep insertion order, so a born marker goes after the last power it saw
    // (a removed one is gone, a re-applied one is a new instance); markers sharing a slot keep birth order.
    internal static List<int[]> Place(IReadOnlyList<(object Power,List<int[]> Rows)> powers,IEnumerable<(int[] Row,BattleSyntheticBirth Birth)> markers)
    {
        int Slot(BattleSyntheticBirth birth)
        {
            int slot=0;
            for(int i=0;i<powers.Count;i++)
                if(birth.Powers.Any(p => ReferenceEquals(p,powers[i].Power))) slot=i+1;
            return slot;
        }
        var ordered=markers.Select(m => (m.Row,m.Birth.Sequence,Slot:Slot(m.Birth))).OrderBy(m => m.Slot).ThenBy(m => m.Sequence).ToList();
        var rows=new List<int[]>();
        int next=0;
        for(int i=0;i<=powers.Count;i++)
        {
            while(next<ordered.Count && ordered[next].Slot==i) rows.Add(ordered[next++].Row);
            if(i<powers.Count) rows.AddRange(powers[i].Rows);
        }
        return rows;
    }
}

// Synchronous native events, reported by the Battle hooks.
internal static class BattleSyntheticPowers
{
    internal static void Clear() => BattleSyntheticPowerLedger.Current.Clear();
    // SkittishPower.HasGainedBlockThisTurn: true in AfterAttack (frozen appends SkittishSpent once,
    // Get==0), false in AfterSideTurnEnd (frozen removes it at the end of the player side).
    internal static void SkittishBlockChanged(object creature,object[] powers,bool value)
    {
        if(value) BattleSyntheticPowerLedger.Current.Born(creature,"SkittishSpent",powers,null,false);
        else BattleSyntheticPowerLedger.Current.Died(creature,"SkittishSpent");
    }
    // Creature.StunInternal, after SetMoveImmediate made `move` (a new STUNNED MoveState) current.
    // Frozen TriggerShriekIfWounded appends TerrorQueued then Stunned; StunAndRepeatPreparedMove
    // removes and re-appends Stunned, so every observed stun is a re-birth. The birth lives only
    // while that exact move is current, so a consumed stun never comes back.
    internal static void Stunned(object creature,object[] powers,object? move,bool shriek)
    {
        if(shriek) BattleTerrorLedger.Current.Shrieked(creature,powers.Where(p => p.GetType().Name!="ShriekPower"));
        BattleSyntheticPowerLedger.Current.Born(creature,"Stunned",powers,move,true);
    }
    // StunInternal with the move before and after it: SetMoveImmediate replaces NextMove only
    // if the current move CanTransitionAway, so an unchanged move is a refused stun.
    // Frozen Ravenous applies Strength first and Stunned only if absent, so during a Ravenous
    // AfterDeath the birth waits for its completion and a refused stun keeps the existing slot.
    // Any other stun is StunAndRepeatPreparedMove's remove-and-re-append, refused or not.
    internal static void NativeStunned(object creature,object[] powers,object? before,object? after,bool shriek)
    {
        if(!shriek && BattleSyntheticPowerLedger.Current.RavenousPending(creature) is { } span)
        {
            if(!ReferenceEquals(before,after)) span.AcceptedMove=after;
            return;
        }
        Stunned(creature,powers,after,shriek);
    }
    // RavenousPower.AfterDeath began for this owner (Harmony prefix).
    internal static object RavenousBegan(object owner) => BattleSyntheticPowerLedger.Current.RavenousBegan(owner);
    internal static object? RavenousOwner(object span) => (span as BattleRavenousSpan)?.Owner;
    // Its Task finished; `powers` are held now, after the Strength it applied.
    internal static void RavenousCompleted(object span,bool succeeded,object[] powers)
    {
        if(span is not BattleRavenousSpan s || !BattleSyntheticPowerLedger.Current.RavenousEnded(s)) return;
        if(succeeded && s.AcceptedMove!=null)
            BattleSyntheticPowerLedger.Current.Born(s.Owner,"Stunned",powers,s.AcceptedMove,true);
    }
}

// Frozen EnemyAI.TriggerShriekIfWounded removes Shriek and appends TerrorQueued1 (then
// Stunned1); TERROR's Execute removes it. So the row lives through the Shriek stun and the
// pending TERROR_MOVE. Native ShriekPower calls CreatureCmd.Stun(owner, TerrorState.StateId).
internal sealed class BattleTerrorLedger
{
    internal static readonly BattleTerrorLedger Current=new();
    internal void Clear() => BattleSyntheticPowerLedger.Current.Clear();
    internal void Shrieked(object creature,IEnumerable<object> powersAtStun) =>
        BattleSyntheticPowerLedger.Current.Born(creature,"TerrorQueued",powersAtStun.ToArray(),null,false);
    internal void TerrorExecuted(object creature) => BattleSyntheticPowerLedger.Current.Died(creature,"TerrorQueued");
    // A queued Terror is pending only while the public move is the stun or TERROR itself.
    internal BattleSyntheticBirth? Birth(object creature,string? nextMoveId) =>
        nextMoveId is "STUNNED" or "TERROR_MOVE" ? BattleSyntheticPowerLedger.Current.Birth(creature,"TerrorQueued") : null;
}
