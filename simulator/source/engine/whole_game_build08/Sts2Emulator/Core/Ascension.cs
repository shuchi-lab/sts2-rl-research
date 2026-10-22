namespace Sts2Emulator.Core;

/// <summary>
/// The ascension level the emulator models, and the game's value-picking rule.
/// </summary>
/// <remarks>
/// <para>
/// Ascension modifiers are not a difficulty slider bolted on top — the game bakes them
/// into monster data through <c>AscensionHelper.GetValueIfAscension(level, high, low)</c>,
/// which returns <c>high</c> only once the run is at or above <c>level</c>. The enum's
/// ordinal IS the level (<c>ToughEnemies = 8</c>, <c>DeadlyEnemies = 9</c>), so at A8
/// the Tough branch is live and the Deadly branch is not.
/// </para>
/// <para>
/// Every intent in <c>CombatFactory</c> was originally transcribed as a bare literal,
/// and the Deadly (A9) branch was taken for all of them — so enemy HP matched the live
/// game at A8 while nearly every attack landed one or two points high. A combat sweep
/// caught it on 13 of 16 captures. Mirror the game's expression instead of a number:
/// <c>Ascension.Value(Ascension.DeadlyEnemies, 9, 8)</c> reads the same as the property
/// it came from, so it can be diffed against the decompiled source by eye.
/// </para>
/// </remarks>
public static class Ascension
{
    /// <summary>Ascension levels, by the ordinal the game's enum gives them.</summary>
    public const int SwarmingElites = 1;
    public const int WearyTraveler = 2;
    public const int Poverty = 3;
    public const int TightBelt = 4;
    public const int AscendersBane = 5;
    public const int Inflation = 6;
    public const int Scarcity = 7;
    public const int ToughEnemies = 8;
    public const int DeadlyEnemies = 9;
    public const int DoubleBoss = 10;

    /// <summary>
    /// The level assumed when a caller does not say. A8 is what most differential
    /// captures are taken at; A10 is captured too, and the two disagree on nearly every
    /// enemy's damage, which is why this is a default rather than the answer.
    /// </summary>
    public const int DefaultLevel = 8;

    /// <summary>Port of <c>AscensionManager.HasLevel</c>: levels are cumulative.</summary>
    public static bool Has(int ascensionLevel, int level) => ascensionLevel >= level;

    /// <summary>Port of <c>AscensionHelper.GetValueIfAscension</c>.</summary>
    public static int Value(int ascensionLevel, int level, int ascensionValue, int fallbackValue) =>
        Has(ascensionLevel, level) ? ascensionValue : fallbackValue;
}
