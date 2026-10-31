using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

internal sealed record BattleTestSubjectPower(string Type,string Entry,int FrozenId,double Amount);

internal sealed record BattleTestSubjectView(int RevivingAmount)
{
    // Actual selected Episode.WriteDerivedView gates every declaration on HP>0.
    internal bool SuppressDeclaredIntents => RevivingAmount>0;
    internal List<int[]> AppendReviving(IReadOnlyList<int[]> rows)
    {
        if(rows==null || rows.Any(row=>row==null || row.Length!=2 || row[0]==154) || RevivingAmount is <0 or >1)
            throw new BattleProjectionUnsupported("Invalid or already folded Test Subject revival rows");
        if(RevivingAmount==1 && (rows.Count is <1 or >2 || !rows[0].SequenceEqual(new[]{141,1}) ||
            (rows.Count==2 && !rows[1].SequenceEqual(new[]{111,1}))))
            throw new BattleProjectionUnsupported("Test Subject revival rows differ from its certified surviving powers");
        var copy=rows.Select(row=>(int[])row.Clone()).ToList();
        if(RevivingAmount==1) copy.Add(new[]{154,1});
        return copy;
    }
}

internal static class BattleTestSubjectPublic
{
    // Native RESPAWN_MOVE has numberless Heal/Buff icons. Frozen death retains
    // Adaptable/PainfulStabs, appends Reviving1, and the actual worker emits no
    // intents at HP0. Only this current public phase is folded; private Respawns,
    // Adaptable.Data.isReviving, move logs and future branches are never read.
    internal static BattleTestSubjectView Project(string nativeType,string nativeEntry,int frozenEnemyId,
        double hp,double maxHp,bool targetable,double block,IReadOnlyList<BattleTestSubjectPower> powers,
        string? announcedMove,IReadOnlyList<string> nativeIntentTypes,int ascensionLevel)
    {
        if(powers==null || nativeIntentTypes==null)
            throw new BattleProjectionUnsupported("Missing public Test Subject powers or intent data");
        bool subject=nativeType=="TestSubject" && nativeEntry=="TEST_SUBJECT" && frozenEnemyId==10007;
        if(!subject)
        {
            if(nativeType=="TestSubject" || nativeEntry=="TEST_SUBJECT" || frozenEnemyId==10007)
                throw new BattleProjectionUnsupported("Unverified native Test Subject identity");
            return new(0);
        }
        if(!double.IsFinite(hp) || hp<0 || hp!=Math.Truncate(hp) ||
            !double.IsFinite(maxHp) || maxHp<=0 || maxHp!=Math.Truncate(maxHp) || hp>maxHp ||
            !double.IsFinite(block) || block<0 || block!=Math.Truncate(block) ||
            powers.Any(power=>power==null || string.IsNullOrEmpty(power.Type) || string.IsNullOrEmpty(power.Entry) ||
                !double.IsFinite(power.Amount) || power.Amount!=Math.Truncate(power.Amount)))
            throw new BattleProjectionUnsupported("Invalid public Test Subject HP, block or power shape");
        if(announcedMove=="RESPAWN_MOVE")
        {
            if(hp!=0 || targetable || block!=0 || !nativeIntentTypes.SequenceEqual(new[]{"Heal","Buff"}))
                throw new BattleProjectionUnsupported("Unsettled or inconsistent public Test Subject respawn phase");
            if(powers.Count is <1 or >2 || powers[0]!=new BattleTestSubjectPower("AdaptablePower","ADAPTABLE_POWER",141,1) ||
                (powers.Count==2 && powers[1]!=new BattleTestSubjectPower("PainfulStabsPower","PAINFUL_STABS_POWER",111,1)))
                throw new BattleProjectionUnsupported("Test Subject respawn must retain only its exact Adaptable/PainfulStabs powers");
            int expectedMax=powers.Count==2 ? (ascensionLevel>=8?212:200) : (ascensionLevel>=8?111:100);
            if(maxHp!=expectedMax)
                throw new BattleProjectionUnsupported("Test Subject downed form HP differs from its public powers/ascension");
            return new(1);
        }
        if(hp<=0 || !targetable)
            throw new BattleProjectionUnsupported("Test Subject downed state lacks its certified announced respawn move");
        // Living declarations retain the ordinary projector, including supported
        // common Stun; only HP0 RESPAWN_MOVE receives synthetic revival semantics.
        return new(0);
    }
}
