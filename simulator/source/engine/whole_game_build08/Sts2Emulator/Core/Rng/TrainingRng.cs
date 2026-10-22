using System.Text;

namespace Sts2Emulator.Core.Rng;

/// <summary>
/// combat_data_simulator fork: which 64-bit core generator a <see cref="MegaRandom" />
/// stream runs on.
/// </summary>
/// <remarks>
/// Every range mapping (<c>NextDouble</c>, <c>Next(max)</c>, <c>NextInt</c>, shuffles,
/// Gaussian helpers in <see cref="GameRng" />) is shared by both algorithms, and every
/// call consumes exactly one 64-bit output in both, so the chance rules and bounds of the
/// engine are unchanged. Only the raw 64-bit sequence and the seed expansion differ.
/// </remarks>
public enum RngAlgorithm : byte
{
    /// <summary>The game's Xoshiro256** with Splitmix64 seed expansion (replay parity).</summary>
    GameXoshiro256StarStar = 0,

    /// <summary>
    /// PCG-XSL-RR 128/64 (O'Neill 2014): 128-bit LCG state and increment, output
    /// <c>rotr64(hi ^ lo, state &gt;&gt; 122)</c>. Seeded through a domain-separated
    /// Splitmix64 expansion. Not the game's generator; never game-seed compatible.
    /// </summary>
    TrainingPcg64XslRr = 1,
}

/// <summary>
/// The algorithm newly constructed streams use on this thread. The default is the game's
/// generator, so the vendored engine behaves exactly as accepted unless a caller opts into
/// training mode with <see cref="Use" />. Restoring a snapshot always takes the snapshot's
/// own algorithm.
/// </summary>
public static class RngAlgorithmContext
{
    public const string TrainingAlgorithmVersion = "sts2-training-pcg64-xslrr-128-64/v1";
    public const string TrainingSeedDerivationVersion = "sts2-training-seed-splitmix-xxh64/v1";

    [ThreadStatic]
    private static RngAlgorithm? _current;

    [ThreadStatic]
    private static long _foreignStreams;

    [ThreadStatic]
    private static int _scopeDepth;

    public static RngAlgorithm Current => _current ?? RngAlgorithm.GameXoshiro256StarStar;

    /// <summary>
    /// Streams constructed or restored on this thread, while a scope was open, whose
    /// algorithm differs from the innermost scope's. An episode must leave this at zero.
    /// </summary>
    public static long ForeignStreamsCreatedInScope => _foreignStreams;

    /// <summary>Kept for the self-tests: the same counter, read in a training scope.</summary>
    public static long GameStreamsCreatedInTrainingScope => _foreignStreams;

    internal static void NoteStream(RngAlgorithm algorithm)
    {
        if (_scopeDepth > 0 && algorithm != Current)
            _foreignStreams++;
    }

    public static void ResetGameStreamCounter() => _foreignStreams = 0;

    public static IDisposable Use(RngAlgorithm algorithm) => new Scope(algorithm);

    private sealed class Scope : IDisposable
    {
        private readonly RngAlgorithm? _previous;
        private bool _disposed;

        public Scope(RngAlgorithm algorithm)
        {
            _previous = _current;
            _current = algorithm;
            _scopeDepth++;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _current = _previous;
            _scopeDepth--;
        }
    }
}

/// <summary>
/// Stream seeds for one training episode, derived from a single environment seed and a
/// stream name. Independent of the game's run-seed hashing: the game adds
/// <c>xxHash64(name)</c> to the run seed, this mixes a domain tag, the name and the
/// environment seed through Splitmix64.
/// </summary>
public static class TrainingSeeds
{
    private static readonly ulong Domain = DeterministicHash.GetDeterministicHashCode(
        RngAlgorithmContext.TrainingSeedDerivationVersion
    );

    public static ulong For(ulong environmentSeed, string streamName)
    {
        ulong x = unchecked(
            environmentSeed ^ Domain ^ (DeterministicHash.GetDeterministicHashCode(streamName) * 0x9E3779B97F4A7C15UL)
        );
        ulong a = MegaRandom.Splitmix64(ref x);
        ulong b = MegaRandom.Splitmix64(ref x);
        return a ^ BitOperationsRotate(b, 29);
    }

    private static ulong BitOperationsRotate(ulong value, int by) =>
        System.Numerics.BitOperations.RotateLeft(value, by);

    /// <summary>A stable text form for manifests.</summary>
    public static string Describe() =>
        new StringBuilder()
            .Append(RngAlgorithmContext.TrainingSeedDerivationVersion)
            .Append(": seed(name) = splitmix64 pair of (env_seed ^ xxh64(version) ^ xxh64(name)*golden)")
            .ToString();
}
