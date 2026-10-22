// AUTO-GENERATED — do not edit. Re-run scripts/extract_data.py to update.
using Sts2Emulator.Core;
namespace Sts2Emulator.GeneratedData;

/// <summary>
/// The potion pools, extracted from the game's PotionPoolModel declarations. What a shop
/// or a reward may offer is the character's pool followed by the shared one — see
/// PotionFactory.GetPotionOptions — and the order matters because NextItem indexes into
/// the concatenation.
/// </summary>
internal static class PotionPools
{
    /// <summary>Shared: 45 potions, in pool order.</summary>
    public static ReadOnlySpan<int> Shared =>
        [2, 3, 4, 5, 8, 9, 10, 13, 14, 15, 16, 17, 18, 19, 21, 22, 23, 24, 26, 28, 29, 30, 32, 34, 36, 37, 38, 39, 40, 42, 47, 48, 49, 50, 51, 52, 53, 54, 56, 57, 59, 60, 61, 62, 63];

    /// <summary>Ironclad: 3 potions, in pool order.</summary>
    public static ReadOnlySpan<int> Ironclad =>
        [6, 55, 1];

    /// <summary>Silent: 3 potions, in pool order.</summary>
    public static ReadOnlySpan<int> Silent =>
        [41, 31, 12];

    /// <summary>Defect: 3 potions, in pool order.</summary>
    public static ReadOnlySpan<int> Defect =>
        [25, 20, 43];

    /// <summary>Necrobinder: 3 potions, in pool order.</summary>
    public static ReadOnlySpan<int> Necrobinder =>
        [44, 46, 7];

    /// <summary>Regent: 3 potions, in pool order.</summary>
    public static ReadOnlySpan<int> Regent =>
        [58, 11, 35];
}
