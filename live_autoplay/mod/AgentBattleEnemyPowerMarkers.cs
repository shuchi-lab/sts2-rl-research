using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

namespace STS2_MCP;

// Current public facts of one native enemy. Null means the fact does not apply.
internal sealed record BattleEnemyMarkerFacts(string MonsterType,string? NextMoveId,
    IReadOnlyList<int> SlowAmounts,int? TurnsUntilSummonable,int? CallForBackupCount);

internal sealed record BattleEnemyMarkerIds(int Stunned,int SummonCooldown,int BackupCount,int SlowCount);

internal static class BattleEnemyPowerMarkers
{
    // Native spawns whose own SPAWNED_MOVE (StunIntent) the selected worker models as
    // CreateEnemy(stunned:true): Phrog Wrigglers and the Gremlin Merc reinforcements.
    private static readonly HashSet<string> FrozenStunnedSpawns = new() {"Wriggler","FatGremlin","SneakyGremlin"};

    // Selected worker synthetic BuffIds that native models hold outside PowerModel rows.
    // BuffSystem.Apply drops a row whose magnitude reaches 0, so 0 emits nothing.
    //  SlowCount52: +1 per card play on a living Slow owner, removed at player turn start.
    //    Native SlowPower.DynamicVars[SlowAmount] (DisplayAmount/10) is the same public counter
    //    during the player turn: +1 AfterCardPlayed, reset 0 at the owner's side turn start.
    //  SummonCooldown94/BackupCount97: TwoTailedRat. Native TurnsUntilSummonable starts 2 and is
    //    decremented only by executed SCRATCH/DISEASE_BITE/SCREECH moves (past public moves);
    //    frozen ticks the same moves from 2 but stops at 0. CallForBackupCount is public.
    //  Stunned89: frozen skip-next-intent flag. Native CreatureCmd.Stun announces NextMove
    //    STUNNED (Ravenous, Shriek, Plow, Asleep, Burrowed, Slumber, Flutter, Whistle, ...).
    //    BowlbugRock's Imbalanced stun is the frozen Unknown intent without Stunned.
    internal static List<int[]> Rows(BattleEnemyMarkerFacts facts,BattleEnemyMarkerIds ids)
    {
        if(facts==null || ids==null || facts.SlowAmounts==null)
            throw new BattleProjectionUnsupported("Missing public enemy marker facts");
        var rows=new List<int[]>();
        if(facts.SlowAmounts.Count>0)
        {
            // SlowPower is a Counter; legal re-application stacks one instance. Any copies
            // count the same card plays, so only a disagreeing counter is unrepresentable.
            if(facts.SlowAmounts.Any(a=>a<0) || facts.SlowAmounts.Distinct().Count()!=1)
                throw new BattleProjectionUnsupported("Slow instances disagree on the public SlowAmount counter");
            if(facts.SlowAmounts[0]>0) rows.Add(new[] {ids.SlowCount,facts.SlowAmounts[0]});
        }
        if(facts.MonsterType=="TwoTailedRat")
        {
            int cooldown=facts.TurnsUntilSummonable ??
                throw new BattleProjectionUnsupported("Two-Tailed Rat has no summon cooldown");
            int backups=facts.CallForBackupCount ??
                throw new BattleProjectionUnsupported("Two-Tailed Rat has no backup count");
            if(cooldown>2 || backups<0) throw new BattleProjectionUnsupported("Two-Tailed Rat counters are outside native bounds");
            if(cooldown>0) rows.Add(new[] {ids.SummonCooldown,cooldown});
            if(backups>0) rows.Add(new[] {ids.BackupCount,backups});
        }
        else if(facts.TurnsUntilSummonable!=null || facts.CallForBackupCount!=null)
            throw new BattleProjectionUnsupported("Summon counters on a non-rat enemy: "+facts.MonsterType);
        bool stunned=(facts.NextMoveId=="STUNNED" && facts.MonsterType!="BowlbugRock") ||
            (facts.NextMoveId=="SPAWNED_MOVE" && FrozenStunnedSpawns.Contains(facts.MonsterType));
        if(stunned) rows.Add(new[] {ids.Stunned,1});
        return rows;
    }
}

public static partial class McpMod
{
    // Root wires: rows.AddRange(BattleEnemyPowerMarkerRows(creature)) in BattlePowerRows.
    private static List<int[]> BattleEnemyPowerMarkerRows(Creature creature)
    {
        var monster=creature.Monster;
        if(monster==null) return new();
        var rat=monster as TwoTailedRat;
        var facts=new BattleEnemyMarkerFacts(monster.GetType().Name,monster.NextMove?.Id,
            creature.Powers.OfType<SlowPower>().Select(p=>p.DynamicVars["SlowAmount"].IntValue).ToArray(),
            rat==null ? null : Convert.ToInt32(BattleRead(rat,"TurnsUntilSummonable")),
            rat?.CallForBackupCount);
        return BattleEnemyPowerMarkers.Rows(facts,new BattleEnemyMarkerIds(AgentIds.Id("buffs","Stunned"),
            AgentIds.Id("buffs","SummonCooldown"),AgentIds.Id("buffs","BackupCount"),AgentIds.Id("buffs","SlowCount")));
    }
}
