using System;
using System.Collections.Generic;
namespace STS2_MCP;
// Derived from pinned EnemyAI literal declared-intent semantics; not future AI/RNG state.
// Source SHA256: 5dcc877c6d7bd827c3402ef7a75a0ea55d16229c308ceef03555aaaa8923e89a
internal static class BattleNativeIntentMap
{
    private static readonly Dictionary<string,int> Values = new()
    {
        ["Aeonglass|Buff|1"] = 0,
        ["Aeonglass|Defend|1"] = 0,
        ["AxeRubyRaider|Defend|1"] = 0,
        ["Axebot|Buff|1"] = 0,
        ["Axebot|Debuff|1"] = 0,
        ["BowlbugEgg|Defend|1"] = 0,
        ["BowlbugRock|Stun|0"] = 0,
        ["BowlbugRock|Unknown|0"] = 0,
        ["BowlbugSilk|Debuff|0"] = 1,
        ["BruteRubyRaider|Buff|0"] = 0,
        ["BygoneEffigy|Buff|0"] = 10,
        ["BygoneEffigy|Sleep|0"] = 0,
        ["BygoneEffigy|Unknown|0"] = 0,
        ["CalcifiedCultist|Buff|0"] = 0,
        ["CeremonialBeast|Buff|1"] = 0,
        ["CeremonialBeast|Debuff|0"] = 0,
        ["Chomper|Debuff|0"] = 3,
        ["Chomper|StatusCard|0"] = 3,
        ["CorpseSlug|Debuff|0"] = 2,
        ["CrossbowRubyRaider|Defend|0"] = 3,
        ["Crusher|Debuff|1"] = 0,
        ["Crusher|Defend|1"] = 0,
        ["CubexConstruct|Buff|0"] = 0,
        ["CubexConstruct|Buff|1"] = 0,
        ["DampCultist|Buff|0"] = 0,
        ["DecimillipedeSegment|Buff|1"] = 0,
        ["DecimillipedeSegment|Debuff|1"] = 0,
        ["DevotedSculptor|Buff|0"] = 9,
        ["Entomancer|Buff|0"] = 1,
        ["EyeWithTeeth|Debuff|0"] = 3,
        ["EyeWithTeeth|StatusCard|0"] = 3,
        ["Fabricator|Buff|0"] = 0,
        ["FatGremlin|Escape|0"] = 0,
        ["FatGremlin|Stun|0"] = 0,
        ["FatGremlin|Unknown|0"] = 0,
        ["FlailKnight|Buff|0"] = 3,
        ["Flyconid|Debuff|0"] = 2,
        ["Flyconid|Debuff|1"] = 0,
        ["Fogmog|Buff|0"] = 0,
        ["Fogmog|Buff|1"] = 0,
        ["FossilStalker|Debuff|1"] = 0,
        ["FrogKnight|Buff|0"] = 5,
        ["FrogKnight|Debuff|1"] = 0,
        ["FuzzyWurmCrawler|Buff|0"] = 0,
        ["GlobeHead|Buff|1"] = 0,
        ["GlobeHead|Debuff|1"] = 0,
        ["GremlinMerc|Buff|1"] = 0,
        ["GremlinMerc|Debuff|1"] = 0,
        ["Guardbot|Defend|0"] = 15,
        ["HauntedShip|Debuff|0"] = 5,
        ["HauntedShip|StatusCard|1"] = 0,
        ["HunterKiller|Debuff|0"] = 1,
        ["InfestedPrism|Buff|1"] = 0,
        ["InfestedPrism|Defend|1"] = 0,
        ["KinPriest|Debuff|1"] = 0,
        ["KnowledgeDemon|Buff|1"] = 0,
        ["KnowledgeDemon|Debuff|0"] = 0,
        ["KnowledgeDemon|Heal|1"] = 0,
        ["LagavulinMatriarch|Buff|1"] = 0,
        ["LagavulinMatriarch|Debuff|0"] = 2,
        ["LagavulinMatriarch|Sleep|0"] = 0,
        ["LagavulinMatriarch|Unknown|0"] = 0,
        ["LeafSlimeM|Debuff|0"] = 2,
        ["LeafSlimeM|StatusCard|0"] = 2,
        ["LeafSlimeS|Debuff|0"] = 1,
        ["LeafSlimeS|StatusCard|0"] = 1,
        ["LivingFog|CardDebuff|1"] = 0,
        ["LivingFog|Debuff|1"] = 1,
        ["LivingShield|Buff|1"] = 0,
        ["LouseProgenitor|Buff|1"] = 0,
        ["LouseProgenitor|Debuff|1"] = 0,
        ["MagiKnight|Debuff|0"] = 1,
        ["MagiKnight|Defend|1"] = 0,
        ["Mawler|Debuff|0"] = 3,
        ["MechaKnight|Buff|1"] = 0,
        ["MechaKnight|Defend|0"] = 15,
        ["MechaKnight|StatusCard|1"] = 0,
        ["Myte|Buff|1"] = 0,
        ["Myte|Debuff|0"] = 2,
        ["Myte|StatusCard|0"] = 2,
        ["Nibbit|Buff|0"] = 0,
        ["Nibbit|Defend|1"] = 0,
        ["Noisebot|Debuff|0"] = 2,
        ["Noisebot|StatusCard|0"] = 2,
        ["Ovicopter|Debuff|1"] = 0,
        ["OwlMagistrate|Buff|0"] = 1,
        ["OwlMagistrate|Debuff|1"] = 0,
        ["PaelsLegion|Unknown|0"] = 0,
        ["PhrogParasite|Debuff|0"] = 3,
        ["PunchConstruct|Debuff|1"] = 0,
        ["PunchConstruct|Defend|0"] = 10,
        ["Queen|Defend|1"] = 0,
        ["Rocket|Sleep|0"] = 0,
        ["Rocket|Unknown|0"] = 0,
        ["Seapunk|Buff|0"] = 0,
        ["Seapunk|Defend|1"] = 0,
        ["SewerClam|Buff|0"] = 0,
        ["ShrinkerBeetle|Debuff|0"] = 1,
        ["ShrinkerBeetle|DebuffStrong|0"] = 1,
        ["SkulkingColony|Buff|1"] = 0,
        ["SlimedBerserker|Buff|1"] = 0,
        ["SlitheringStrangler|Debuff|0"] = 3,
        ["SlitheringStrangler|Defend|1"] = 0,
        ["SludgeSpinner|Buff|1"] = 3,
        ["SludgeSpinner|Debuff|1"] = 1,
        ["SlumberingBeetle|Buff|1"] = 0,
        ["SlumberingBeetle|Sleep|0"] = 0,
        ["SlumberingBeetle|Unknown|0"] = 0,
        ["SnappingJaxfruit|Buff|1"] = 0,
        ["SneakyGremlin|Unknown|0"] = 0,
        ["SoulFysh|Buff|0"] = 2,
        ["SoulFysh|Debuff|0"] = 2,
        ["SoulFysh|Debuff|1"] = 0,
        ["SoulFysh|StatusCard|1"] = 0,
        ["SoulNexus|DebuffStrong|1"] = 0,
        ["SpectralKnight|Debuff|0"] = 2,
        ["SpinyToad|Buff|0"] = 5,
        ["Stabbot|Debuff|1"] = 1,
        ["TerrorEel|Buff|1"] = 0,
        ["TerrorEel|Debuff|0"] = 99,
        ["TerrorEel|Unknown|0"] = 0,
        ["TestSubject|Buff|1"] = 0,
        ["TestSubject|Debuff|1"] = 0,
        ["TheAdversaryMkOne|Buff|1"] = 2,
        ["TheAdversaryMkThree|Buff|1"] = 4,
        ["TheAdversaryMkTwo|Buff|1"] = 3,
        ["TheForgotten|Buff|1"] = 0,
        ["TheForgotten|Debuff|0"] = 2,
        ["TheForgotten|Defend|1"] = 0,
        ["TheInsatiable|StatusCard|1"] = 0,
        ["TheLost|Buff|1"] = 0,
        ["TheLost|Debuff|0"] = 2,
        ["TheObscura|Defend|1"] = 0,
        ["ThievingHopper|Buff|0"] = 0,
        ["ThievingHopper|CardDebuff|1"] = 0,
        ["ThievingHopper|Escape|0"] = 0,
        ["ThievingHopper|Unknown|0"] = 0,
        ["Toadpole|Buff|0"] = 0,
        ["ToughEgg|Buff|0"] = 0,
        ["TrackerRubyRaider|Debuff|0"] = 2,
        ["Tunneler|Buff|0"] = 0,
        ["Tunneler|Defend|1"] = 0,
        ["TurretOperator|Buff|0"] = 1,
        ["TwigSlimeM|Debuff|0"] = 1,
        ["TwigSlimeM|StatusCard|0"] = 1,
        ["TwoTailedRat|Buff|0"] = 0,
        ["TwoTailedRat|Debuff|0"] = 1,
        ["Vantom|Buff|0"] = 2,
        ["Vantom|StatusCard|1"] = 0,
        ["VineShambler|CardDebuff|1"] = 0,
        ["VineShambler|Debuff|1"] = 1,
        ["WaterfallGiant|Buff|1"] = 0,
        ["WaterfallGiant|Debuff|1"] = 0,
        ["WaterfallGiant|Stun|0"] = 0,
        ["Wriggler|Buff|0"] = 1,
        ["Wriggler|StatusCard|1"] = 0,
    };
    // Reviewed SelectIntent ascension expressions and delegated helper declarations.
    // Each key is the CURRENT native public move, never a future branch or RNG result.
    // High/low values preserve frozen feature semantics, not arbitrary icon defaults.
    private static readonly Dictionary<(string Enemy,string Intent,string Move),(int Threshold,int High,int Low)> ReviewedPrimaryValues = new()
    {
        [("Exoskeleton","Buff","ENRAGE_MOVE")] = (0,0,0),
        [("BowlbugNectar","Buff","BUFF_MOVE")] = (9,16,15),
        [("Ovicopter","Buff","NUTRITIONAL_PASTE_MOVE")] = (9,4,3),
        [("LouseProgenitor","Defend","CURL_AND_GROW_MOVE")] = (8,18,14),
        [("Axebot","Defend","BOOT_UP_MOVE")] = (9,15,10),
        [("MagiKnight","Defend","PREP_MOVE")] = (8,9,5),
        [("Crusher","Buff","ADAPT_MOVE")] = (9,3,2),
        [("Rocket","Buff","CHARGE_UP_MOVE")] = (9,3,2),
        [("TheInsatiable","Buff","SALIVATE_MOVE")] = (9,3,2),
        [("TheInsatiable","Buff","LIQUIFY_GROUND_MOVE")] = (0,0,0),
        [("KinFollower","Buff","POWER_DANCE_MOVE")] = (9,3,2),
        [("KinPriest","Buff","RITUAL_MOVE")] = (9,3,2),
        [("WaterfallGiant","Buff","PRESSURIZE_MOVE")] = (9,20,15),
        [("WaterfallGiant","Heal","SIPHON_MOVE")] = (8,15,10),
        [("ScrollOfBiting","Buff","MORE_TEETH")] = (0,2,2),
        [("FakeMerchantMonster","Buff","ENRAGE_MOVE")] = (0,2,2),
        [("MysteriousKnight","Buff","WAR_CHANT")] = (0,3,3),
    };
    // Installed v0.111.0 GenerateMoveStateMachine SummonIntent owners; frozen EnemyAI
    // DeclaredIntent retypes exactly these (lines109-121,182-194) with magnitude0.
    private static readonly HashSet<string> FrozenSummonDeclarers = new() {
        "Fabricator","Fogmog","LivingFog","Ovicopter","TheObscura","ToughEgg","TwoTailedRat"
    };
    internal static int Magnitude(string enemy,string intent,int ordinal,string? moveId = null,int ascensionLevel = 2)
    {
        enemy=enemy switch {
            "DecimillipedeSegmentFront" or "DecimillipedeSegmentMiddle" or "DecimillipedeSegmentBack" => "DecimillipedeSegment",
            _=>enemy
        };
        if (intent == "Stun") return 0;
        // Native SummonIntent is parameterless; every frozen DeclaredIntent Summon is a
        // Buff0 retype or a literal Summon0. Only the reviewed installed summoners qualify.
        if (intent == "Summon" && FrozenSummonDeclarers.Contains(enemy)) return 0;
        // Pinned native SLASH2_MOVE declares Attack then numberless Defend.
        // Selected EnemyAI.DeclaredIntent(46,1) exposes Defend0; its actual
        // Slash2Block12/14 is applied separately by ExecuteIntent, never here.
        if (enemy == "LagavulinMatriarch" && intent == "Defend" && ordinal == 1 && moveId == "SLASH2_MOVE") return 0;
        if (ordinal == 2 && (enemy,intent) is ("TheForgotten","Buff") or ("InfestedPrism","Defend") or
            ("WaterfallGiant","Buff") or ("KnowledgeDemon","Buff")) return 0;
        // The same announced icon can carry different pinned-emulator public surrogate
        // magnitudes. These bindings use the CURRENT declared move only; no next AI state.
        if (ordinal == 0)
        {
            if(moveId!=null && ReviewedPrimaryValues.TryGetValue((enemy,intent,moveId),out var rule))
                return ascensionLevel>=rule.Threshold ? rule.High : rule.Low;
            // Native StampMove applies PlowAmount; the pinned SelectIntent carries
            // that public effect threshold as its primary Buff magnitude. Display-only
            // Buffs beside Plow/Crush and Beast Cry's Debuff keep their frozen zeros.
            if(enemy=="CeremonialBeast" && intent=="Buff" && moveId=="STAMP_MOVE")
                return ascensionLevel>=9 ? 160 : 150;
            // PhantasmalGardener.EnlargeStr and frozen EnemyAI.SelectIntent both use
            // the public run ascension: DeadlyEnemies(A9) gives Strength3, else2.
            // Its native BuffIntent icon has no number; the frozen magnitude carries
            // this reviewed effect surrogate. The component's supported default is A2.
            if(enemy=="PhantasmalGardener" && intent=="Buff" && moveId=="ENLARGE_MOVE")
                return ascensionLevel>=9 ? 3 : 2;
            if (enemy == "Queen") return (intent,moveId) switch {
                ("CardDebuff","PUPPET_STRINGS_MOVE") => 3,
                ("Debuff","YOU_ARE_MINE_MOVE") => 99,
                ("Buff","BURN_BRIGHT_FOR_ME_MOVE") => 20,
                ("Buff","ENRAGE_MOVE") => 2,
                _ => throw new BattleProjectionUnsupported($"Unverified Queen declaration {intent}/{moveId}")
            };
            if (enemy == "SlimedBerserker" && intent == "Debuff" && moveId == "LEECHING_HUG_MOVE") return 3;
            if (enemy == "SlimedBerserker" && intent == "StatusCard" && moveId == "VOMIT_ICHOR_MOVE") return 10;
            if (enemy == "TheObscura" && intent == "Buff" && moveId == "SAIL_MOVE") return 3;
        }
        if (Values.TryGetValue($"{enemy}|{intent}|{ordinal}",out int amount)) return amount;
        throw new BattleProjectionUnsupported($"Unverified announced intent magnitude: {enemy}/{intent}/{ordinal}");
    }

    // Worker enemy.extra[15] is SecondaryIntent != null, not DeclaredIntent(1) != null.
    // Source: pinned EnemyAI.SecondaryIntentFor lines3907-3968. Display-only rows in
    // DeclaredIntent do not set it. Native move IDs are reviewed against the matching
    // Monster GenerateMoveStateMachine definitions; no private future state is read.
    internal static bool HasSecondary(string enemy,string moveId) => enemy switch {
        "LivingShield"=>moveId=="SMASH_MOVE",
        "GremlinMerc"=>moveId is "DOUBLE_SMASH_MOVE" or "HEHE_MOVE",
        "PunchConstruct"=>moveId=="FAST_PUNCH_MOVE",
        "Crusher"=>moveId is "BUG_STING_MOVE" or "GUARDED_STRIKE_MOVE",
        "OwlMagistrate"=>moveId=="VERDICT",
        "KinPriest"=>moveId is "ORB_OF_FRAILTY_MOVE" or "ORB_OF_WEAKNESS_MOVE",
        "MechaKnight"=>moveId is "FLAMETHROWER_MOVE" or "WINDUP_MOVE",
        "SludgeSpinner"=>moveId is "OIL_SPRAY_MOVE" or "RAGE_MOVE",
        "Seapunk"=>moveId=="BUBBLE_BURP_MOVE",
        "LivingFog"=>moveId=="ADVANCED_GAS_MOVE",
        "VineShambler"=>moveId=="GRASPING_VINES_MOVE",
        "Stabbot"=>true,
        "SkulkingColony"=>moveId=="INERTIA_MOVE",
        "TheAdversaryMkOne" or "TheAdversaryMkTwo" or "TheAdversaryMkThree"=>moveId=="BARRAGE_MOVE",
        // Flyconid's pinned SecondaryIntentFor tests Debuff magnitude>2. Its current
        // SelectIntent returns Debuff2 or Attack, so the legacy branch is unreachable.
        "Flyconid"=>false,
        // CombatEngine.TryRespawnAxebot constructs this exception with SecondaryIntent
        // Buff directly (lines5595-5612). Fresh Axebot starts on Hammer Uppercut instead.
        "Axebot"=>moveId=="BOOT_UP_MOVE",
        _=>false
    };
    internal static bool NeedsSecondaryHistory(string enemy) => enemy is
        "LivingShield" or "GremlinMerc" or "PunchConstruct" or "Crusher" or "OwlMagistrate" or
        "KinPriest" or "MechaKnight" or "SludgeSpinner" or "Seapunk" or "LivingFog" or
        "VineShambler" or "SkulkingColony" or "TheAdversaryMkOne" or "TheAdversaryMkTwo" or
        "TheAdversaryMkThree" or "Axebot";
}
