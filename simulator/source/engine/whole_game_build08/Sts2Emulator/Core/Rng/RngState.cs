namespace Sts2Emulator.Core.Rng;

/// <summary>A complete, immutable v0.111.0 stream snapshot.</summary>
/// <remarks>
/// combat_data_simulator fork: the snapshot also names the core generator, so a clone or
/// restore of a training stream stays a training stream. Existing positional callers get
/// the game's generator by default.
/// </remarks>
public readonly record struct RngState(
    int Counter,
    ulong S0,
    ulong S1,
    ulong S2,
    ulong S3,
    RngAlgorithm Algorithm = RngAlgorithm.GameXoshiro256StarStar
);
