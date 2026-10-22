using Sts2Emulator.Core.Rng;

namespace Sts2Emulator.Core.Run;

/// <summary>
/// The per-encounter RNG stream: <c>EncounterModel.Rng</c>.
/// </summary>
/// <remarks>
/// <para>
/// Encounter models roll their own composition from a stream that is neither the combat
/// RNG nor any named run stream. <c>EncounterModel</c> builds it as
/// <c>new Rng(runState.Rng.Seed + (ulong)runState.TotalFloor +
/// StringHelper.GetDeterministicHashCode(Id.Entry))</c> — the run seed, the number of
/// map points visited so far, and a hash of the encounter's own id.
/// </para>
/// <para>
/// Two things ride on it, and both were wrong before this existed: which slimes spawn in
/// a Slimes encounter (<c>SlimesWeak.GenerateMonsters</c>), and which move each Corpse
/// Slug opens on (<c>CorpseSlug.EnsureCorpseSlugsStartWithDifferentMoves</c>). Neither is
/// derivable from the combat RNG, so a combat env that does not know the floor cannot
/// reproduce them.
/// </para>
/// </remarks>
public static class EncounterRng
{
    /// <summary>
    /// The game's <c>Id.Entry</c> for encounters whose generation consumes their own Rng.
    /// Anything absent here does not roll its composition and needs no seed.
    /// </summary>
    /// <remarks>
    /// Read from the generated table rather than transcribed, because this half of the
    /// plumbing fails SILENTLY: a builder can be handed its seed and still quietly fall
    /// back to the combat rng, since the seed only exists if the encounter is listed.
    /// That is exactly what happened to the Bowlbugs — plumbed and inert (E90) — and it
    /// is why four more that never had an entry could sit here unnoticed.
    ///
    /// The model names, not the emulator's, because two models can share one emulator id
    /// and still have different entries: Corpse Slugs weak and normal are one
    /// <c>ActOneEncounter</c> value and two <c>Id.Entry</c> strings.
    /// </remarks>
    public static string? EntryId(int encounterId, bool weakVariant) =>
        GeneratedData.EncounterTags.EntryForModel(ModelName(encounterId, weakVariant) ?? "");

    /// <summary>
    /// The encounter model whose <c>GenerateMonsters</c> an emulator encounter id runs.
    /// </summary>
    private static string? ModelName(int encounterId, bool weakVariant) =>
        encounterId switch
        {
            RunConstants.SlimesWeakEncounterId => "SlimesWeak",
            RunConstants.SlimesNormalEncounterId => "SlimesNormal",
            // Both of these roll their composition too: Flyconid picks a medium slime to
            // stand with, and Slithering Strangler picks a whole secondary enemy type.
            // Rolling either on the combat rng gets the roster right by luck only.
            RunConstants.FlyconidNormalEncounterId => "FlyconidNormal",
            // The rats roll which move the FIRST of them opens on; the other two take the
            // next two in order, so one draw decides all three openings.
            RunConstants.TwoTailedRatsEncounterId => "TwoTailedRatsNormal",
            // Three raiders drawn from five, each capped at one, so the roster is three
            // draws on the encounter's stream over a shrinking list.
            RunConstants.RubyRaidersEncounterId => "RubyRaidersNormal",
            RunConstants.SlitheringStranglerEncounterId => "SlitheringStranglerNormal",
            // Both slug variants share one emulator enum id but not one entry id.
            RunConstants.CorpseSlugsEncounterId => weakVariant
                ? "CorpseSlugsWeak"
                : "CorpseSlugsNormal",
            // A Rock and one worker; a Rock and two, drawn without replacement.
            RunConstants.BowlbugsWeakEncounterId => "BowlbugsWeak",
            RunConstants.BowlbugsNormalEncounterId => "BowlbugsNormal",
            // These three roll ONE value that offsets the whole roster's opening moves,
            // so a single draw off the wrong stream moves every creature in the fight.
            RunConstants.DecimillipedeEncounterId => "DecimillipedeElite",
            RunConstants.ScrollsWeakEncounterId => "ScrollsOfBitingWeak",
            RunConstants.ScrollsNormalEncounterId => "ScrollsOfBitingNormal",
            // Two draws, one per construct: how much starting HP each has lost.
            RunConstants.PunchOffEncounterId => "PunchOffEventEncounter",
            _ => null,
        };

    /// <summary>Seed for an encounter's own stream, or null when it does not use one.</summary>
    public static ulong? SeedFor(int runSeed, int totalFloor, int encounterId, bool weakVariant) =>
        SeedFor(unchecked((uint)runSeed), totalFloor, encounterId, weakVariant);

    public static ulong? SeedFor(ulong runSeed, int totalFloor, int encounterId, bool weakVariant)
    {
        string? entry = EntryId(encounterId, weakVariant);
        if (entry is null)
        {
            return null;
        }

        return unchecked(
            runSeed + (ulong)totalFloor + DeterministicHash.GetDeterministicHashCode(entry)
        );
    }

    /// <summary>The stream itself: the game's unnamed <c>new Rng(seed)</c>.</summary>
    public static GameRng Stream(ulong seed) => new GameRng(seed);
}
