namespace Sts2Emulator.Core;

public enum PotionRarity
{
    None,
    Common,
    Uncommon,
    Rare,
    Event,
    Token,
}

/// <summary>`PotionModel.Usage`, extracted per potion.</summary>
public enum PotionUsage
{
    None,
    CombatOnly,
    AnyTime,
    Automatic,
}

public readonly record struct PotionDef(
    int Id,
    string Name,
    PotionRarity Rarity = PotionRarity.None,
    bool TargetsEnemy = false,
    // `PotionModel.CanBeGeneratedInCombat`, which `PotionFactory.CreateRandomPotionInCombat`
    // filters on. False only where the potion overrides it.
    bool CanBeGeneratedInCombat = true,
    PotionUsage Usage = PotionUsage.None
);
