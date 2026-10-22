namespace Sts2Emulator.Core.Rng;

public sealed class RunRngSet
{
    public ulong Seed { get; }

    public GameRng UpFront { get; }
    public GameRng Shuffle { get; }
    public GameRng UnknownMapPoint { get; }
    public GameRng CombatCardGeneration { get; }
    public GameRng CombatPotionGeneration { get; }
    public GameRng CombatCardSelection { get; }
    public GameRng CombatEnergyCosts { get; }
    public GameRng CombatTargets { get; }
    public GameRng MonsterAi { get; }
    public GameRng Niche { get; }
    public GameRng CombatOrbs { get; }
    public GameRng TreasureRoomRelics { get; }

    /// <summary>The exact seed stored by the game; UI normalization is a separate step.</summary>
    public string StringSeed { get; }

    /// <summary>Derive a stored seed exactly; UI normalization is a separate operation.</summary>
    public static ulong DeriveSeed(string stringSeed) =>
        // Original RunRngSet calls StartsWith(string), and NGame._EnterTree sets
        // invariant culture. Keep its linguistic treatment of ignorable characters
        // without depending on the embedding application's current thread culture.
        stringSeed.StartsWith("old", StringComparison.InvariantCulture)
            ? unchecked((uint)DeterministicHash.GetDeterministicHashCodeOld(stringSeed[3..]))
            : DeterministicHash.GetDeterministicHashCode(stringSeed);

    public RunRngSet(string stringSeed)
    {
        StringSeed = stringSeed;
        Seed = DeriveSeed(stringSeed);
        UpFront = new GameRng(Seed, "up_front");
        Shuffle = new GameRng(Seed, "shuffle");
        UnknownMapPoint = new GameRng(Seed, "unknown_map_point");
        CombatCardGeneration = new GameRng(Seed, "combat_card_generation");
        CombatPotionGeneration = new GameRng(Seed, "combat_potion_generation");
        CombatCardSelection = new GameRng(Seed, "combat_card_selection");
        CombatEnergyCosts = new GameRng(Seed, "combat_energy_costs");
        CombatTargets = new GameRng(Seed, "combat_targets");
        MonsterAi = new GameRng(Seed, "monster_ai");
        Niche = new GameRng(Seed, "niche");
        CombatOrbs = new GameRng(Seed, "combat_orbs");
        TreasureRoomRelics = new GameRng(Seed, "treasure_room_relics");
    }

    public GameRng ActMapRng(int actIndex = 0) => new(Seed, $"act_{actIndex + 1}_map");

    /// <summary>
    /// Neow's own stream. EventModel seeds every event with
    /// <c>Seed + (IsShared ? 0 : GetPlayerSlotIndex(Owner)) + hash(Id.Entry)</c>, and a
    /// solo run's only player is slot 0 — the 1 this used to default to seeded a
    /// different stream, so every Neow offer in every run was the wrong three relics.
    /// </summary>
    /// <summary>
    /// A relic's OWN stream: <c>new Rng(owner, Id)</c>, which is
    /// <c>Seed + slotIndex + hash(Id.Entry)</c> -- the same shape as Neow's, with the
    /// relic's entry name in place of "NEOW". Fur Coat is the only user.
    /// </summary>
    public GameRng RelicRng(string entry, int netId = 0, ulong mixin = 0)
    {
        ulong hash = DeterministicHash.GetDeterministicHashCode(entry);
        return new GameRng(unchecked(Seed + (ulong)netId + hash + mixin));
    }

    public GameRng NeowRng(int netId = 0)
    {
        return RelicRng("NEOW", netId);
    }
}
