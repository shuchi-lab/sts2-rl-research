namespace Sts2Emulator.Core;

public sealed class GameState
{
    public int PlayerHp;
    public int PlayerMaxHp;
    public int Gold;
    public int Floor;
    public List<CardInstance> Deck = [];
    public List<RelicInstance> Relics = [];
    public int[] PotionSlots = new int[Run.RunConstants.PotionSlotCapacity];
    public CombatState? ActiveCombat;
}
