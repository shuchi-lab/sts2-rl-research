// Reviewed beta Neow content: scripts/extract_beta_neow_content.py
// AUTO-GENERATED — do not edit. Re-run scripts/extract_data.py to update.
using Sts2Emulator.Core;
namespace Sts2Emulator.GeneratedData;

internal static class Potions
{
    private static readonly PotionDef[] _all =
    [
        new PotionDef(Id: 64, Name: "Ambergris", Rarity: PotionRarity.Event, Usage: PotionUsage.AnyTime),
        new PotionDef(Id: 1, Name: "Ashwater", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 2, Name: "AttackPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 3, Name: "BeetleJuice", Rarity: PotionRarity.Rare, TargetsEnemy: true, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 4, Name: "BlessingOfTheForge", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 5, Name: "BlockPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 6, Name: "BloodPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.AnyTime),
        new PotionDef(Id: 7, Name: "BoneBrew", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 8, Name: "BottledPotential", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 9, Name: "Clarity", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 10, Name: "ColorlessPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 11, Name: "CosmicConcoction", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 12, Name: "CunningPotion", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 13, Name: "CureAll", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 14, Name: "DexterityPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 15, Name: "DistilledChaos", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 16, Name: "DropletOfPrecognition", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 17, Name: "Duplicator", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 18, Name: "EnergyPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 19, Name: "EntropicBrew", Rarity: PotionRarity.Rare, Usage: PotionUsage.AnyTime),
        new PotionDef(Id: 20, Name: "EssenceOfDarkness", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 21, Name: "ExplosiveAmpoule", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 22, Name: "FairyInABottle", Rarity: PotionRarity.Rare, CanBeGeneratedInCombat: false, Usage: PotionUsage.Automatic),
        new PotionDef(Id: 23, Name: "FirePotion", Rarity: PotionRarity.Common, TargetsEnemy: true, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 24, Name: "FlexPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 25, Name: "FocusPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 26, Name: "Fortifier", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 27, Name: "FoulPotion", Rarity: PotionRarity.Event, Usage: PotionUsage.AnyTime),
        new PotionDef(Id: 28, Name: "FruitJuice", Rarity: PotionRarity.Rare, CanBeGeneratedInCombat: false, Usage: PotionUsage.AnyTime),
        new PotionDef(Id: 29, Name: "FyshOil", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 30, Name: "GamblersBrew", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 31, Name: "GhostInAJar", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 32, Name: "GigantificationPotion", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 33, Name: "GlowwaterPotion", Rarity: PotionRarity.Event, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 34, Name: "HeartOfIron", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 35, Name: "KingsCourage", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 36, Name: "LiquidBronze", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 37, Name: "LiquidMemories", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 38, Name: "LuckyTonic", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 39, Name: "MazalethsGift", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 40, Name: "OrobicAcid", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 41, Name: "PoisonPotion", Rarity: PotionRarity.Common, TargetsEnemy: true, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 42, Name: "PotionOfBinding", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 43, Name: "PotionOfCapacity", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 44, Name: "PotionOfDoom", Rarity: PotionRarity.Common, TargetsEnemy: true, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 45, Name: "PotionShapedRock", Rarity: PotionRarity.Token, TargetsEnemy: true, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 46, Name: "PotOfGhouls", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 47, Name: "PowderedDemise", Rarity: PotionRarity.Uncommon, TargetsEnemy: true, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 48, Name: "PowerPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 49, Name: "RadiantTincture", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 50, Name: "RegenPotion", Rarity: PotionRarity.Uncommon, CanBeGeneratedInCombat: false, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 51, Name: "ShacklingPotion", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 52, Name: "ShipInABottle", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 53, Name: "SkillPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 54, Name: "SneckoOil", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 55, Name: "SoldiersStew", Rarity: PotionRarity.Rare, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 56, Name: "SpeedPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 57, Name: "StableSerum", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 58, Name: "StarPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 59, Name: "StrengthPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 60, Name: "SwiftPotion", Rarity: PotionRarity.Common, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 61, Name: "TouchOfInsanity", Rarity: PotionRarity.Uncommon, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 62, Name: "VulnerablePotion", Rarity: PotionRarity.Common, TargetsEnemy: true, Usage: PotionUsage.CombatOnly),
        new PotionDef(Id: 63, Name: "WeakPotion", Rarity: PotionRarity.Common, TargetsEnemy: true, Usage: PotionUsage.CombatOnly),
    ];

    public static PotionDef Get(int id) =>
        Array.Find(_all, p => p.Id == id) is { Id: > 0 } def
            ? def
            : throw new ArgumentException($"Unknown potion id {id}");

    public static int? FindId(string name) =>
        Array.Find(_all, p => p.Name == name) is { Id: > 0 } def
            ? def.Id
            : null;
}
