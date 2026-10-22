using Sts2Emulator.Core;
using Sts2Emulator.Core.Run;

namespace Sts2Emulator.Training;

/// <summary>
/// combat_data_simulator fork: every value <c>RunEngine.StartCombatWithDeck</c> reads to
/// build a fight, captured at the moment it reads them (after Delicate Frond's fill and
/// the deck-origin stamp, before any combat RNG is touched).
/// </summary>
/// <remarks>
/// This is the complete battle input of the vendored engine: StartCombatWithDeck and the
/// factory it calls read nothing else from the run. The game's RNG stream positions are
/// deliberately NOT part of it -- a training battle reseeds every stream.
/// </remarks>
public sealed class BattleInput
{
    public CharacterId Character;
    public int AscensionLevel;
    public ProgressionContext Progression = ProgressionContext.AllUnlocked;
    public List<CardInstance> Deck = [];
    public List<RelicInstance> Relics = [];
    public List<int> UsedUpRelics = [];
    public int PlayerHp;
    public int PlayerMaxHp;
    public int Gold;
    public int[] StartingPotions = [];
    public int MaxPotionSlots;
    public int CompletedCombatRoomsBeforeCurrent;
    public int EncounterId;
    public bool EncounterWeakVariant;
    public int TotalFloor;
    public bool IsEliteRoom;
    public bool FurCoatBites;
    public List<int>? PresetEnemyHps;

    // Context only (not read by the combat build): where in the run this fight sat.
    public int ActIndex;
    public int CurrentNodeType;
    public int LastResolvedRoomType;
    public int NormalEncountersVisited;
    public int EliteEncountersVisited;
    public int MapCol;
    public int MapRow;
}

public static class CombatEntryCapture
{
    [ThreadStatic]
    public static Action<BattleInput>? Observer;

    internal static BattleInput FromRun(
        RunState state,
        int encounterId,
        int[] startingPotions,
        bool weakVariant
    ) =>
        new()
        {
            Character = state.Character,
            AscensionLevel = state.AscensionLevel,
            Progression = state.Progression,
            Deck = [.. state.Deck],
            Relics = [.. state.Relics],
            UsedUpRelics = [.. state.UsedUpRelics],
            PlayerHp = state.PlayerHp,
            PlayerMaxHp = state.PlayerMaxHp,
            Gold = state.Gold,
            StartingPotions = [.. startingPotions],
            MaxPotionSlots = state.MaxPotionSlots,
            CompletedCombatRoomsBeforeCurrent = state.CompletedCombatRoomsBeforeCurrent,
            EncounterId = encounterId,
            EncounterWeakVariant = weakVariant,
            TotalFloor = state.Floor,
            IsEliteRoom = state.LastResolvedRoomType == RunConstants.NodeElite,
            FurCoatBites = state.FurCoatRooms.Contains(state.CurrentMapCoord),
            PresetEnemyHps =
                encounterId == state.EventLayoutEncounterId && state.EventLayoutEnemyHps.Count > 0
                    ? [.. state.EventLayoutEnemyHps]
                    : null,
            ActIndex = state.CurrentActIndex,
            CurrentNodeType = state.CurrentNodeType,
            LastResolvedRoomType = state.LastResolvedRoomType,
            NormalEncountersVisited = state.NormalEncountersVisited,
            EliteEncountersVisited = state.EliteEncountersVisited,
            MapCol = state.CurrentMapCoord.Col,
            MapRow = state.CurrentMapCoord.Row,
        };
}
