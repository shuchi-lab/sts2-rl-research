using System;
using System.Collections.Generic;
using System.Reflection;

namespace STS2_MCP;

internal sealed record BattleRelicCounterRule(string NativeType,string NativeEntry,int FrozenId,
    string Member,int Period,string? PeriodVariable,bool Modulo,bool RunPersistent=true);

internal static class BattleRelicCounterRules
{
    private static readonly Dictionary<string,BattleRelicCounterRule> Rules=new(StringComparer.Ordinal) {
        ["OrnamentalFan"]=new("OrnamentalFan","ORNAMENTAL_FAN",173,"AttacksPlayedThisTurn",3,"Cards",true,false),
        ["Kunai"]=new("Kunai","KUNAI",126,"AttacksPlayedThisTurn",3,"Cards",true,false),
        ["Shuriken"]=new("Shuriken","SHURIKEN",237,"AttacksPlayedThisTurn",3,"Cards",true,false),
        ["Kusarigama"]=new("Kusarigama","KUSARIGAMA",127,"AttacksPlayedThisTurn",3,"Cards",true,false),
        ["LetterOpener"]=new("LetterOpener","LETTER_OPENER",136,"SkillsPlayedThisTurn",3,"Cards",true,false),
        ["Nunchaku"]=new("Nunchaku","NUNCHAKU",166,"AttacksPlayed",10,"Cards",true),
        ["PenNib"]=new("PenNib","PEN_NIB",192,"AttacksPlayed",10,null,true),
        ["IronClub"]=new("IronClub","IRON_CLUB",118,"CardsPlayed",4,"Cards",true),
        ["HappyFlower"]=new("HappyFlower","HAPPY_FLOWER",110,"TurnsSeen",3,"Turns",false),
        ["Pendulum"]=new("Pendulum","PENDULUM",191,"TurnsSeen",3,"Turns",false),
        ["LastingCandy"]=new("LastingCandy","LASTING_CANDY",130,"CombatRewardsSeen",2,null,false),
        ["FakeHappyFlower"]=new("FakeHappyFlower","FAKE_HAPPY_FLOWER",78,"TurnsSeen",5,"Turns",false),
        ["PollinousCore"]=new("PollinousCore","POLLINOUS_CORE",200,"TurnsSeen",4,"Turns",false),
        ["JossPaper"]=new("JossPaper","JOSS_PAPER",122,"CardsExhausted",5,"ExhaustAmount",true),
        ["BookOfFiveRings"]=new("BookOfFiveRings","BOOK_OF_FIVE_RINGS",27,"CardsAdded",5,"Cards",true),
        ["PaelsWing"]=new("PaelsWing","PAELS_WING",184,"RewardsSacrificed",2,"Sacrifices",false),
        ["ToyBox"]=new("ToyBox","TOY_BOX",271,"CombatsSeen",3,"Combats",false),
        // Frozen TriggerFishingRod stores raw normal-room wins; native DisplayAmount is %3.
        ["FishingRod"]=new("FishingRod","FISHING_ROD",89,"CombatsSeen",3,"Combats",false),
        ["GalacticDust"]=new("GalacticDust","GALACTIC_DUST",96,"StarsSpent",10,"Stars",true,false),
        ["Metronome"]=new("Metronome","METRONOME",152,"OrbsChanneled",7,"OrbCount",false,false),
        ["TuningFork"]=new("TuningFork","TUNING_FORK",274,"SkillsPlayed",10,"Cards",false)
    };
    internal static bool IsKnown(string nativeType) => Rules.ContainsKey(nativeType);
    internal static bool TryRule(string nativeType,string nativeEntry,int frozenId,out BattleRelicCounterRule? rule)
    {
        if(!Rules.TryGetValue(nativeType,out rule)) return false;
        if(nativeEntry!=rule.NativeEntry || frozenId!=rule.FrozenId)
            throw new BattleProjectionUnsupported("Certified relic counter has a different native/frozen identity: "+nativeType);
        return true;
    }
    internal static int Counter(BattleRelicCounterRule rule,int reportedPeriod,object? mechanical)
    {
        if(reportedPeriod!=rule.Period || mechanical is not int count || count<0)
            throw new BattleProjectionUnsupported("Certified relic counter has an unknown period, field type or past count: "+rule.NativeType);
        return rule.Modulo?count%rule.Period:count;
    }
    // Frozen RelicEffects.ApplyAfterPlayerHpChanged is Red Skull's only Counter writer:
    // 1 when it grants +3 Strength (HP <= MaxHp/2), 0 when it takes it back. It is not in
    // RunCounterRelics, so every combat starts from the run's 0 and nothing carries back.
    // Native private StrengthApplied is that executed latch; AfterCombatEnd clears it.
    internal static bool TryRedSkull(object relic,string nativeEntry,int frozenId,out int counter)
    {
        counter=0;
        if(relic.GetType().FullName!="MegaCrit.Sts2.Core.Models.Relics.RedSkull" || nativeEntry!="RED_SKULL" || frozenId!=215)
            throw new BattleProjectionUnsupported("Red Skull has a different native/frozen identity");
        var latch=relic.GetType().GetProperty("StrengthApplied",
            BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
        if(latch==null || latch.PropertyType!=typeof(bool) || latch.GetIndexParameters().Length!=0 || latch.GetMethod==null)
            throw new BattleProjectionUnsupported("Red Skull strength latch is unavailable");
        counter=(bool)latch.GetValue(relic)! ? 1 : 0;
        return true;
    }
    // Native boolean latches whose executed value is the selected frozen Counter (0/1).
    // FiresOncePerCombat: Permafrost (first Power), Centennial Puzzle (first unblocked damage),
    // Ruined Helmet (first positive Strength), Lizard Tail (revive). RunCounterRelics omits all
    // four, so the run Counter stays 0 (combat-local). Lizard Tail's spent state reaches later
    // combats through UsedUpRelics/RestoreUsedUpRelics instead; native WasUsed is never reset.
    // Silken Tress: RunRewardGenerator consumes it on the first card reward, setting the run
    // Counter 1, which each combat copies.
    private static readonly Dictionary<string,BattleRelicLatchRule> Latches=new(StringComparer.Ordinal) {
        ["Permafrost"]=new("PERMAFROST",193,"ActivatedThisCombat",true),
        ["CentennialPuzzle"]=new("CENTENNIAL_PUZZLE",43,"UsedThisCombat",true),
        ["RuinedHelmet"]=new("RUINED_HELMET",225,"UsedThisCombat",true),
        ["LizardTail"]=new("LIZARD_TAIL",137,"WasUsed",true),
        ["SilkenTress"]=new("SILKEN_TRESS",239,"IsUsed",false)
    };
    internal sealed record BattleRelicLatchRule(string NativeEntry,int FrozenId,string Member,bool CombatLocal);
    internal static bool IsLatch(string nativeType) => Latches.ContainsKey(nativeType);
    // The run-level (Choice) Counter of these relics is 0 whatever the native latch says.
    internal static bool IsCombatLocalLatch(string nativeType) => Latches.TryGetValue(nativeType,out var rule) && rule.CombatLocal;
    internal static bool TryLatch(object relic,string nativeEntry,int frozenId,out int counter)
    {
        counter=0;
        string nativeType=relic.GetType().Name;
        if(!Latches.TryGetValue(nativeType,out var rule)) return false;
        if(relic.GetType().FullName!="MegaCrit.Sts2.Core.Models.Relics."+nativeType || nativeEntry!=rule.NativeEntry || frozenId!=rule.FrozenId)
            throw new BattleProjectionUnsupported(nativeType+" has a different native/frozen identity");
        var latch=relic.GetType().GetProperty(rule.Member,
            BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
        if(latch==null || latch.PropertyType!=typeof(bool) || latch.GetIndexParameters().Length!=0 || latch.GetMethod==null)
            throw new BattleProjectionUnsupported(nativeType+" latch is unavailable");
        counter=(bool)latch.GetValue(relic)! ? 1 : 0;
        return true;
    }
    internal static bool TryPermafrost(object relic,string nativeEntry,int frozenId,out int counter)
    {
        if(relic.GetType().FullName!="MegaCrit.Sts2.Core.Models.Relics.Permafrost")
            throw new BattleProjectionUnsupported("Permafrost has a different native/frozen identity");
        return TryLatch(relic,nativeEntry,frozenId,out counter);
    }
    // Choice scope. Outside combat these native DisplayAmounts return -1, the hidden-counter UI
    // value (StoneCalendar/PaelsLegion: !CombatManager.IsInProgress => -1). The selected frozen
    // worker never writes their run Counter (acquisition 0, not carried back), and its Choice
    // observation reads that run Counter outside combat.
    private static readonly Dictionary<string,(string Entry,int FrozenId)> ChoiceRunZero=new(StringComparer.Ordinal) {
        ["StoneCalendar"]=("STONE_CALENDAR",248),
        ["PaelsLegion"]=("PAELS_LEGION",181)
    };
    internal static bool TryChoiceRunZero(string nativeType,string nativeEntry,int frozenId)
    {
        if(!ChoiceRunZero.TryGetValue(nativeType,out var rule)) return false;
        if(nativeEntry!=rule.Entry || frozenId!=rule.FrozenId)
            throw new BattleProjectionUnsupported("Choice run counter has a different native/frozen identity: "+nativeType);
        return true;
    }
    internal static bool TryStoneCalendar(string nativeEntry,int frozenId,bool inCombat,bool isCanonical,
        bool hasOwnerCombat,int damageTurn,int ownerTurn,out int counter)
    {
        counter=0;
        if(nativeEntry!="STONE_CALENDAR" || frozenId!=248)
            throw new BattleProjectionUnsupported("Stone Calendar has a different native/frozen identity");
        if(!inCombat) return false;
        if(isCanonical || !hasOwnerCombat || damageTurn!=7 || ownerTurn<1)
            throw new BattleProjectionUnsupported("Stone Calendar has an uncertified active-combat lifecycle or threshold");
        // Frozen acquisition starts Counter0 and its only effect reads Turn+1.
        // Native DisplayAmount reads the public turn and a cosmetic flash instead.
        return true;
    }
}
