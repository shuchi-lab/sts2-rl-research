using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

internal sealed record BattleDecimillipedePower(string Type,double Amount);

internal sealed record BattleDecimillipedeView(int RevivingAmount)
{
    // The selected worker's Episode.WriteDerivedView exposes NO declared intent
    // for any HP0 row, including either certified revival phase.
    internal bool SuppressDeclaredIntents => RevivingAmount>0;
    internal List<int[]> AppendReviving(IReadOnlyList<int[]> rows,int frozenReattachId,int frozenRevivingId)
    {
        if(rows==null || frozenReattachId<=0 || frozenRevivingId<=0 || frozenReattachId==frozenRevivingId ||
            RevivingAmount is <0 or >2 || rows.Any(row=>row==null || row.Length!=2 || row[0]==frozenRevivingId))
            throw new BattleProjectionUnsupported("Invalid or already folded Decimillipede revival rows");
        if(RevivingAmount>0 && (rows.Count!=1 || rows[0][0]!=frozenReattachId || rows[0][1]!=25))
            throw new BattleProjectionUnsupported("Dead Decimillipede must retain exactly the frozen Reattach25 row");
        var copy=rows.Select(row=>(int[])row.Clone()).ToList();
        if(RevivingAmount>0) copy.Add(new[]{frozenRevivingId,RevivingAmount});
        return copy;
    }
}

internal static class BattleDecimillipedePublic
{
    // Native v0.111.0 (sts2.dll SHA256 9cb4f1ad...) uses three visual subclasses
    // of the same segment model. Its announced DEAD_MOVE has no intent and its
    // REATTACH_MOVE has Heal. Frozen CombatEngine.TryReattachSegment stores Buff0
    // throughout both phases, seeding Reviving2 and decrementing it to1 before
    // healing25. This fold uses only present public state, never private Data,
    // move logs, future random branches, or a remembered death counter.
    internal static BattleDecimillipedeView Project(string nativeType,double hp,bool targetable,double block,
        IReadOnlyList<BattleDecimillipedePower> powers,string? announcedMove,IReadOnlyList<string> nativeIntentTypes)
    {
        if(powers==null || nativeIntentTypes==null)
            throw new BattleProjectionUnsupported("Missing public Decimillipede power or intent data");
        bool segment=nativeType is "DecimillipedeSegmentFront" or "DecimillipedeSegmentMiddle" or "DecimillipedeSegmentBack";
        if(!segment)
        {
            if(powers.Any(power=>power?.Type=="ReattachPower"))
                throw new BattleProjectionUnsupported("Reattach belongs to an uncertified native segment: "+nativeType);
            return new(0);
        }
        if(!double.IsFinite(hp) || hp<0 || hp!=Math.Truncate(hp) ||
            !double.IsFinite(block) || block<0 || block!=Math.Truncate(block) ||
            powers.Any(power=>power==null || string.IsNullOrEmpty(power.Type) ||
                !double.IsFinite(power.Amount) || power.Amount!=Math.Truncate(power.Amount)))
            throw new BattleProjectionUnsupported("Invalid public Decimillipede HP, block or power amount");
        var reattach=powers.Where(power=>power.Type=="ReattachPower").ToArray();
        if(reattach.Length!=1 || reattach[0].Amount!=25)
            throw new BattleProjectionUnsupported("Decimillipede requires exactly one public Reattach25 power");
        if(announcedMove is "DEAD_MOVE" or "REATTACH_MOVE")
        {
            if(hp!=0 || targetable || block!=0 || powers.Count!=1)
                throw new BattleProjectionUnsupported("Dead Decimillipede public state is inconsistent with frozen revival");
            bool dead=announcedMove=="DEAD_MOVE";
            if(dead ? nativeIntentTypes.Count!=0 : !nativeIntentTypes.SequenceEqual(new[]{"Heal"}))
                throw new BattleProjectionUnsupported("Decimillipede revival move has an uncertified native declaration");
            return new(dead?2:1);
        }
        string[]? expected=announcedMove switch {
            "WRITHE_MOVE" => new[]{"Attack"},
            "BULK_MOVE" => new[]{"Attack","Buff"},
            "CONSTRICT_MOVE" => new[]{"Attack","Debuff"},
            _ => null
        };
        if(hp<=0 || !targetable || expected==null || !nativeIntentTypes.SequenceEqual(expected))
            throw new BattleProjectionUnsupported("Uncertified living Decimillipede move, targetability or declaration");
        return new(0);
    }
}
