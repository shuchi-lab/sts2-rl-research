using Sts2Emulator.Core.Effects;
using Sts2Emulator.Core.Run;

namespace Sts2Emulator.Core;

/// <summary>Playable characters supported by the run API; ordinals are ABI values.</summary>
public enum CharacterId
{
    Ironclad = 0,
    Silent = 1,
}

/// <summary>Character-owned definitions from the pinned v0.111.0 character models.</summary>
public static class CharacterCatalog
{
    public static string Entry(CharacterId character) =>
        character switch
        {
            CharacterId.Ironclad => "IRONCLAD",
            CharacterId.Silent => "SILENT",
            _ => throw new ArgumentOutOfRangeException(nameof(character)),
        };

    public static int StartingHp(CharacterId character) =>
        character switch
        {
            CharacterId.Ironclad => 80,
            CharacterId.Silent => 70,
            _ => throw new ArgumentOutOfRangeException(nameof(character)),
        };

    public static int StarterRelic(CharacterId character) =>
        character switch
        {
            CharacterId.Ironclad => RunConstants.RelicBurningBlood,
            CharacterId.Silent => RelicEffects.RingOfTheSnake,
            _ => throw new ArgumentOutOfRangeException(nameof(character)),
        };

    // Ascender's Bane is removed by Reset below A5, as for the existing Ironclad deck.
    public static ReadOnlySpan<int> StarterDeck(CharacterId character) =>
        character switch
        {
            CharacterId.Ironclad => RunConstants.StarterDeckIds,
            CharacterId.Silent =>
            [
                SI.StrikeSilent,
                SI.StrikeSilent,
                SI.StrikeSilent,
                SI.StrikeSilent,
                SI.StrikeSilent,
                SI.DefendSilent,
                SI.DefendSilent,
                SI.DefendSilent,
                SI.DefendSilent,
                SI.DefendSilent,
                SI.Neutralize,
                SI.Survivor,
                IC.AscendersBane,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(character)),
        };

    public static ReadOnlySpan<int> CardPool(CharacterId character) =>
        character switch
        {
            CharacterId.Ironclad => GeneratedData.CardPools.Ironclad,
            CharacterId.Silent => GeneratedData.CardPools.Silent,
            _ => throw new ArgumentOutOfRangeException(nameof(character)),
        };

    public static ReadOnlySpan<int> RelicPool(CharacterId character) =>
        character switch
        {
            CharacterId.Ironclad => GeneratedData.RelicPools.Ironclad,
            CharacterId.Silent => GeneratedData.RelicPools.Silent,
            _ => throw new ArgumentOutOfRangeException(nameof(character)),
        };

    private static readonly int[] IroncladPotions =
    [
        .. GeneratedData.PotionPools.Ironclad,
        .. GeneratedData.PotionPools.Shared,
    ];
    private static readonly int[] SilentPotions =
    [
        .. GeneratedData.PotionPools.Silent,
        .. GeneratedData.PotionPools.Shared,
    ];

    /// <summary>PotionFactory.GetPotionOptions: character first, then shared, in pool order.</summary>
    public static ReadOnlySpan<int> PotionPool(CharacterId character) =>
        character switch
        {
            CharacterId.Ironclad => IroncladPotions,
            CharacterId.Silent => SilentPotions,
            _ => throw new ArgumentOutOfRangeException(nameof(character)),
        };

    // Other-character relics/events can reference unplayable characters' existing content.
    internal static ReadOnlySpan<int> CardPoolForEntry(string entry) =>
        entry switch
        {
            "IRONCLAD" => GeneratedData.CardPools.Ironclad,
            "SILENT" => GeneratedData.CardPools.Silent,
            "REGENT" => GeneratedData.CardPools.Regent,
            "NECROBINDER" => GeneratedData.CardPools.Necrobinder,
            "DEFECT" => GeneratedData.CardPools.Defect,
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        };
}
