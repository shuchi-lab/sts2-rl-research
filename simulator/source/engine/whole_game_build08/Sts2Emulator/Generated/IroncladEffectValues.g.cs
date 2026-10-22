// AUTO-GENERATED — do not edit. Re-run scripts/generate_ironclad_effect_values.py --write.
// Game v0.111.0, Steam build 24724944, DLL SHA-256 9cb4f1ad8c9f284aa8fec3122ffd6d780bbf543d875c817abdd12ff63fbf12b4.
// Library data/v0.111.0/ironclad-card-library.json SHA-256 1b0a82ea8ef66dce67078ddbfc34c2142aca456b1df54c0251eaa92f32c07928.
// Bindings data/v0.111.0/ironclad-effect-bindings.json SHA-256 0c93e14d0c92cd9e3a9e7a2942df1c31f83c62e821ffc8da30114b1d755af761.
namespace Sts2Emulator.Core.Effects;

/// <summary>
/// Reviewed runtime scalars consumed by nine dedicated Ironclad cases in
/// <see cref="CardEffects"/>. CardDef damage, Block and cost are not here.
/// </summary>
internal static class IroncladEffectValues
{
    /// <summary>CrimsonMantle: library CrimsonMantlePower (PowerVar).</summary>
    internal static class CrimsonMantle
    {
        internal const int BlockPerTurnBase = 7;
        internal const int BlockPerTurnUpgraded = 10;
    }

    /// <summary>DemonForm: library StrengthPower (PowerVar).</summary>
    internal static class DemonForm
    {
        internal const int StrengthPerTurnBase = 3;
        internal const int StrengthPerTurnUpgraded = 4;
    }

    /// <summary>SetupStrike: library StrengthPower (PowerVar).</summary>
    internal static class SetupStrike
    {
        internal const int TemporaryStrengthBase = 3;
        internal const int TemporaryStrengthUpgraded = 4;
    }

    /// <summary>ExpectAFight: library CalculationBase (CalculationBaseVar), CalculationExtra (CalculationExtraVar).</summary>
    internal static class ExpectAFight
    {
        internal const int BaseBlockBase = 15;
        internal const int BaseBlockUpgraded = 16;
        internal const int BlockPerStrengthBase = 5;
        internal const int BlockPerStrengthUpgraded = 8;
    }

    /// <summary>Rampage: library Increase (DynamicVar).</summary>
    internal static class Rampage
    {
        internal const int DamageGrowthBase = 5;
        internal const int DamageGrowthUpgraded = 10;
    }

    /// <summary>ForgottenRitual: library Energy (EnergyVar).</summary>
    internal static class ForgottenRitual
    {
        internal const int EnergyGainBase = 3;
        internal const int EnergyGainUpgraded = 4;
    }

    /// <summary>Mangle: library StrengthLoss (DynamicVar).</summary>
    internal static class Mangle
    {
        internal const int TemporaryStrengthLossBase = 10;
        internal const int TemporaryStrengthLossUpgraded = 15;
    }

    /// <summary>PactsEnd: library Cards (CardsVar).</summary>
    internal static class PactsEnd
    {
        internal const int RequiredExhaustedCardsBase = 3;
        internal const int RequiredExhaustedCardsUpgraded = 3;
    }

    /// <summary>Taunt: library VulnerablePower (PowerVar).</summary>
    internal static class Taunt
    {
        internal const int VulnerableTurnsBase = 1;
        internal const int VulnerableTurnsUpgraded = 2;
    }
}
