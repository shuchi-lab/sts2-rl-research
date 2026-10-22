using Sts2Emulator.Core;
using Sts2Emulator.Core.Rng;
using Sts2Emulator.Core.Run;

namespace Sts2Emulator.Training;

/// <summary>A standalone training fight and the combat RNG its steps take.</summary>
/// <remarks>
/// <see cref="DeadOnEntry" /> and <see cref="WonOnEntry" /> are StartCombatWithDeck's own
/// entry checks, taken once in the same order. <c>CombatEngine.PlayerIsDead</c> is not a
/// pure query (it spends Fairy in a Bottle and Lizard Tail), so observers must never call
/// it again; read <c>PlayerHp</c> instead.
/// </remarks>
public sealed record TrainingCombat(
    CombatState State,
    CountingRandom CombatRng,
    ulong EnvironmentSeed,
    bool DeadOnEntry,
    bool WonOnEntry,
    EnvironmentRngMode Mode = EnvironmentRngMode.Training
);

/// <summary>
/// combat_data_simulator fork: how a fresh fight's streams are generated and seeded.
/// Both modes run the same rules; neither restores a recorded run's stream positions, so
/// neither is a replay of the historical fight.
/// </summary>
public enum EnvironmentRngMode
{
    /// <summary>PCG-XSL-RR streams seeded by <see cref="TrainingSeeds" /> from the env seed.</summary>
    Training,

    /// <summary>
    /// The game's Xoshiro256** streams seeded the game's way, with the environment seed as
    /// the run seed: <c>new GameRng(seed, name)</c> per stream as in <c>RunRngSet</c>, the
    /// combat rng on the niche stream's raw seed and the encounter stream from
    /// <c>EncounterRng.SeedFor(seed, floor, ...)</c>, as <c>StartCombatWithDeck</c> does at
    /// the start of a run.
    /// </summary>
    Game,
}

/// <summary>
/// combat_data_simulator fork: builds a fight from a <see cref="BattleInput" /> the way
/// <c>RunEngine.StartCombatWithDeck</c> does, with every stream on the training generator
/// and seeded from one environment seed.
/// </summary>
/// <remarks>
/// Mirrors the setup statements of StartCombatWithDeck in the same order (deck origin
/// stamp, shuffle of the deck on the shuffle stream, combat-state fields, the factory
/// reset with the same arguments, elite flag, used-up relics, pre-play phase). It adds no
/// rule. Differences, all deliberate: (1) every stream is a fresh training stream instead
/// of the run's stream at its position; (2) the combat rng and the niche-HP stream get
/// distinct training seeds (the run seeds the combat rng from the niche stream's raw seed);
/// (3) the encounter stream seed comes from the environment seed with the recorded floor
/// and weak-variant flag, so a composition-rolling encounter rolls afresh.
/// </remarks>
public static class TrainingCombatFactory
{
    public static readonly string[] StreamNames =
    [
        "shuffle",
        "combat",
        "niche",
        "monster_ai",
        "combat_targets",
        "combat_card_selection",
        "combat_energy_costs",
        "combat_card_generation",
        "combat_potion_generation",
        "combat_orbs",
        "encounter",
    ];

    public static RngAlgorithm AlgorithmFor(EnvironmentRngMode mode) => mode switch
    {
        EnvironmentRngMode.Game => RngAlgorithm.GameXoshiro256StarStar,
        EnvironmentRngMode.Training => RngAlgorithm.TrainingPcg64XslRr,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static TrainingCombat Build(BattleInput input, ulong environmentSeed) =>
        Build(input, environmentSeed, EnvironmentRngMode.Training);

    public static TrainingCombat Build(BattleInput input, ulong environmentSeed, EnvironmentRngMode mode)
    {
        using var scope = RngAlgorithmContext.Use(AlgorithmFor(mode));
        bool game = mode == EnvironmentRngMode.Game;
        // Game mode: RunRngSet's `new GameRng(Seed, name)`; the combat rng and the niche-HP
        // stream both start from the niche stream (RunEngine.StartCombatWithDeck), and the
        // encounter stream takes the run seed itself (EncounterRng.SeedFor).
        ulong Seed(string name) =>
            !game ? TrainingSeeds.For(environmentSeed, name)
            : name == "combat" ? new GameRng(environmentSeed, "niche").RawSeed
            : name == "encounter" ? environmentSeed
            : new GameRng(environmentSeed, name).RawSeed;

        var deck = input
            .Deck.Select(
                (card, index) =>
                    card with
                    {
                        DeckOrigin = new DeckCardOrigin(index, card with { DeckOrigin = null }),
                    }
            )
            .ToArray();

        var shuffleStream = new GameRng(Seed("shuffle"));
        var combatDeck = deck.ToArray();
        shuffleStream.Shuffle(combatDeck);
        var shuffleRng = new CountingRandom(shuffleStream);

        var combat = new CombatState
        {
            Progression = input.Progression,
            AscensionLevel = input.AscensionLevel,
            TracksRunDeck = true,
        };
        var combatRng = new CountingRandom(Seed("combat"));
        combat.NicheHpRng = new CountingRandom(Seed("niche"));
        if (input.PresetEnemyHps is { Count: > 0 } preset)
            combat.PresetEnemyHps = [.. preset];
        var aiRng = new CountingRandom(Seed("monster_ai"));
        combat.TargetRng = new CountingRandom(Seed("combat_targets"));
        combat.CardSelectionRng = new CountingRandom(Seed("combat_card_selection"));
        combat.EnergyCostRng = new CountingRandom(Seed("combat_energy_costs"));
        combat.CardGenerationRng = new CountingRandom(Seed("combat_card_generation"));
        combat.PotionGenerationRng = new CountingRandom(Seed("combat_potion_generation"));
        combat.OrbGenerationRng = new CountingRandom(Seed("combat_orbs"));
        combat.MaxPotionSlots = input.MaxPotionSlots;
        combat.Character = input.Character;

        int hp = Math.Clamp(input.PlayerHp, 0, Math.Max(1, input.PlayerMaxHp));
        int maxHp = Math.Max(1, input.PlayerMaxHp);
        CombatFactory.Reset(
            combat,
            combatRng,
            combatDeck,
            input.EncounterId,
            input.Relics.Select(relic => relic.DefId).ToArray(),
            hp,
            maxHp,
            input.StartingPotions,
            Math.Max(0, input.Gold),
            deckPreShuffled: true,
            shuffleRng,
            EncounterRng.SeedFor(Seed("encounter"), input.TotalFloor, input.EncounterId, input.EncounterWeakVariant),
            nicheSkipCount: 0,
            aiRng,
            Math.Max(0, input.CompletedCombatRoomsBeforeCurrent),
            input.Relics,
            furCoatBites: input.FurCoatBites
        );
        combat.IsEliteRoom = input.IsEliteRoom;
        Core.Effects.RelicEffects.RestoreUsedUpRelics(combat, input.UsedUpRelics);
        CombatEngine.ResolvePrePlayPhase(combat, combatRng);
        // StartCombatWithDeck: a turn one that already ended the fight has nothing to act on.
        bool deadOnEntry = CombatEngine.PlayerIsDead(combat);
        bool wonOnEntry = !deadOnEntry && CombatEngine.NoPrimaryEnemyLeft(combat);
        return new TrainingCombat(combat, combatRng, environmentSeed, deadOnEntry, wonOnEntry, mode);
    }

    /// <summary>Step a training fight; any stream the step constructs is a training one.</summary>
    public static StepResult Step(TrainingCombat fight, int action, int targetEnemyIndex)
    {
        using var scope = RngAlgorithmContext.Use(AlgorithmFor(fight.Mode));
        return targetEnemyIndex >= 0
            ? CombatEngine.Step(fight.State, action, fight.CombatRng, targetEnemyIndex)
            : CombatEngine.Step(fight.State, action, fight.CombatRng);
    }
}
