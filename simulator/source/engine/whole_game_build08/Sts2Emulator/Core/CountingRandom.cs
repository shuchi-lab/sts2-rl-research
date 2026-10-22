namespace Sts2Emulator.Core;

using Rng;

/// <summary>
/// Wraps the game's deterministic RNG and counts calls so the caller can sync
/// run-level streams after combat-local RNG consumption.
///
/// Backed by <see cref="MegaRandom" /> (Xoshiro256**), the generator the game
/// actually uses. It is a <see cref="Random" /> subclass purely so existing
/// helpers like CardEffects.ShufflePile can take it; the values come from
/// MegaRandom, never from the base class.
/// </summary>
public sealed class CountingRandom : Random
{
    private readonly GameRng _rng;

    /// <summary>The stream's raw seed, kept so the stream can be cloned.</summary>
    public ulong Seed { get; }

    public int CallCount => _rng.CallCount;

    /// <summary>combat_data_simulator fork: the core generator this stream runs on.</summary>
    public RngAlgorithm Algorithm => _rng.Algorithm;

    /// <summary>
    /// Compatibility for the native API's signed 32-bit combat seed.
    /// </summary>
    public CountingRandom(int seed)
        : this(unchecked((uint)seed)) { }

    public CountingRandom(ulong seed)
        : this(seed, RngAlgorithmContext.Current) { }

    public CountingRandom(ulong seed, RngAlgorithm algorithm)
    {
        Seed = seed;
        _rng = new GameRng(seed, algorithm);
    }

    /// <summary>
    /// A stream already <paramref name="counter" /> draws in, which is
    /// <c>Rng.FastForwardCounter</c>: it takes one <c>MegaRandom.NextInt()</c> per step,
    /// the same draw a `Next` costs, and the game's own constructor uses it to resume a
    /// saved stream.
    /// </summary>
    /// <remarks>
    /// A rebuilt combat starts every stream at zero while the live run's had advanced, so
    /// a card that ROLLS matched the game's logic and disagreed about the result. The mod
    /// reports seed and counter for each named stream; this is the other half.
    /// </remarks>
    public CountingRandom(int seed, int counter)
        : this(seed)
    {
        _rng.AdvanceToCallCount(counter);
    }

    /// <summary>Copy a run stream without replaying its counter.</summary>
    public CountingRandom(GameRng source)
        : this(source.RawSeed, source.Algorithm)
    {
        Restore(source.Snapshot());
    }

    public RngState Snapshot() => _rng.Snapshot();

    public void Restore(RngState state) => _rng.Restore(state);

    public override int Next(int maxValue) => _rng.NextInt(maxValue);

    public override int Next() => _rng.NextInt();

    public override int Next(int minValue, int maxValue) => _rng.NextInt(minValue, maxValue);

    public override double NextDouble() => _rng.NextDouble();
}
