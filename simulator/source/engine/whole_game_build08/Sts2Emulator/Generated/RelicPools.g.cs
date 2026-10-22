// AUTO-GENERATED — do not edit. Re-run scripts/extract_data.py to update.
using Sts2Emulator.Core;
namespace Sts2Emulator.GeneratedData;

/// <summary>
/// The relic pools, extracted from the game's RelicPoolModel declarations.
///
/// RelicGrabBag.Populate builds a run's relic queue from the shared pool plus the
/// character's, so this is where the queue's contents AND their pre-shuffle order come
/// from. A RelicDef carries a rarity but not a pool, and the grab bag needs both.
/// </summary>
internal static class RelicPools
{
    /// <summary>Shared: 118 relics, in pool order.</summary>
    public static ReadOnlySpan<int> Shared =>
        [1, 3, 4, 7, 9, 10, 11, 13, 14, 23, 27, 31, 32, 35, 37, 40, 41, 42, 43, 44, 46, 51, 61, 65, 66, 75, 87, 92, 93, 97, 98, 99, 100, 103, 107, 108, 110, 114, 115, 117, 122, 123, 125, 126, 127, 128, 130, 131, 135, 136, 137, 138, 142, 144, 147, 149, 150, 151, 153, 154, 156, 158, 160, 166, 169, 170, 172, 173, 174, 186, 189, 190, 192, 191, 193, 194, 198, 199, 202, 204, 210, 212, 213, 214, 217, 218, 219, 222, 224, 230, 236, 237, 241, 246, 248, 249, 252, 253, 254, 260, 262, 265, 267, 270, 273, 274, 276, 278, 279, 280, 282, 283, 284, 287, 288, 290, 291, 292];

    /// <summary>Ironclad: 8 relics, in pool order.</summary>
    public static ReadOnlySpan<int> Ironclad =>
        [34, 36, 45, 59, 188, 215, 225, 234];

    /// <summary>Silent: 8 relics, in pool order.</summary>
    public static ReadOnlySpan<int> Silent =>
        [112, 165, 187, 221, 244, 264, 269, 275];

    /// <summary>Defect: 8 relics, in pool order.</summary>
    public static ReadOnlySpan<int> Defect =>
        [52, 56, 73, 106, 203, 152, 226, 257];

    /// <summary>Necrobinder: 8 relics, in pool order.</summary>
    public static ReadOnlySpan<int> Necrobinder =>
        [15, 24, 28, 26, 30, 94, 119, 277];

    /// <summary>Regent: 8 relics, in pool order.</summary>
    public static ReadOnlySpan<int> Regent =>
        [64, 86, 96, 143, 155, 171, 216, 285];
}
