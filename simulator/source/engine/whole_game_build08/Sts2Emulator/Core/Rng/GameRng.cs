namespace Sts2Emulator.Core.Rng;

/// <summary>
/// Port of the game's <c>MegaCrit.Sts2.Core.Random.Rng</c>: a named, counted
/// stream over <see cref="MegaRandom" /> (Xoshiro256**).
///
/// Method-for-method faithful to the game, because every call both produces a
/// value and advances shared stream state — a wrapper that consumes a different
/// number of draws desynchronises everything downstream even when each
/// individual value looks reasonable.
/// </summary>
public sealed class GameRng
{
    private readonly MegaRandom _rng;

    public ulong RawSeed { get; }

    /// <summary>combat_data_simulator fork: the core generator this stream runs on.</summary>
    public RngAlgorithm Algorithm => _rng.Algorithm;

    /// <summary>The game's <c>Rng.Counter</c>: how many values this stream has produced.</summary>
    public int CallCount { get; private set; }

    public GameRng(ulong seed = 0)
        : this(seed, RngAlgorithmContext.Current) { }

    public GameRng(ulong seed, RngAlgorithm algorithm)
    {
        RawSeed = seed;
        _rng = new MegaRandom(seed, algorithm);
    }

    // An empty name is still hashed. It is different from the unnamed constructor.
    public GameRng(ulong seed, string name)
        : this(unchecked(seed + DeterministicHash.GetDeterministicHashCode(name))) { }

    /// <summary>Compatibility for the existing signed 32-bit combat API.</summary>
    public GameRng(int seed)
        : this(unchecked((uint)seed)) { }

    public RngState Snapshot() => _rng.Snapshot(CallCount);

    public void Restore(RngState state)
    {
        CallCount = state.Counter;
        _rng.Restore(state);
    }

    public int NextInt(int maxExclusive = int.MaxValue)
    {
        CallCount++;
        return _rng.Next(maxExclusive);
    }

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minInclusive),
                "Minimum must be lower than maximum."
            );
        }

        CallCount++;
        return _rng.Next(minInclusive, maxExclusive);
    }

    /// <summary>
    /// Matches the game's <c>Rng.NextBool</c>, which is <c>Next(2) == 0</c> — NOT
    /// MegaRandom's own sign-bit NextBool, which would consume the same draw but
    /// return the opposite answer roughly half the time.
    /// </summary>
    public bool NextBool()
    {
        CallCount++;
        return _rng.Next(2) == 0;
    }

    public double NextDouble()
    {
        CallCount++;
        return _rng.NextDouble();
    }

    public double NextDouble(double min, double max)
    {
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(min));
        CallCount++;
        return _rng.NextDouble() * (max - min) + min;
    }

    public uint NextUnsignedInt(uint maxExclusive = uint.MaxValue) =>
        NextUnsignedInt(0, maxExclusive);

    public uint NextUnsignedInt(uint minInclusive, uint maxExclusive)
    {
        if (minInclusive >= maxExclusive)
            throw new ArgumentOutOfRangeException(nameof(minInclusive));
        CallCount++;
        return minInclusive + (uint)(_rng.NextDouble() * (maxExclusive - minInclusive));
    }

    public ulong NextUnsignedLong()
    {
        CallCount++;
        return _rng.NextULong();
    }

    public ulong NextUnsignedLong(ulong maxExclusive) =>
        maxExclusive == ulong.MaxValue ? NextUnsignedLong() : NextUnsignedLong(0, maxExclusive);

    public ulong NextUnsignedLong(ulong minInclusive, ulong maxExclusive)
    {
        if (minInclusive >= maxExclusive)
            throw new ArgumentOutOfRangeException(nameof(minInclusive));
        CallCount++;
        return unchecked(minInclusive + (ulong)(_rng.NextDouble() * (maxExclusive - minInclusive)));
    }

    /// <summary>
    /// The game's <c>Rng.NextFloat(min, max)</c>, which is
    /// <c>(float)(NextDouble() * (max - min) + min)</c> -- the cast to float happens
    /// after the arithmetic, and the comparison thresholds it feeds are floats too.
    /// </summary>
    public float NextFloat(float max = 1f) => NextFloat(0, max);

    public float NextFloat(float min, float max)
    {
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(min));
        CallCount++;
        return (float)(_rng.NextDouble() * (max - min) + min);
    }

    public T NextItem<T>(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return default!;
        }

        return items[NextInt(items.Count)];
    }

    /// <summary>
    /// Box–Muller, matching the game. Note the game uses plain <c>Math.Round</c>
    /// (banker's rounding, to-even) — not away-from-zero.
    /// </summary>
    public int NextGaussianInt(int mean, int stdDev, int min, int max)
    {
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(min));
        if (mean < min || mean > max)
            throw new ArgumentOutOfRangeException(nameof(mean));
        while (true)
        {
            double d = 1.0 - NextDouble();
            double num = 1.0 - NextDouble();
            double sample = Math.Sqrt(-2.0 * Math.Log(d)) * Math.Sin(Math.PI * 2.0 * num);
            int result = (int)Math.Round(mean + stdDev * sample);
            if (min <= result && result <= max)
            {
                return result;
            }
        }
    }

    public float NextGaussianFloat(
        float mean = 0,
        float stdDev = 1,
        float min = 0,
        float max = 1
    ) => (float)NextGaussianDouble(mean, stdDev, min, max);

    public double NextGaussianDouble(
        double mean = 0,
        double stdDev = 1,
        double min = 0,
        double max = 1
    )
    {
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(min));
        if (mean < 0 || mean > 1)
            throw new ArgumentOutOfRangeException(nameof(mean));
        while (true)
        {
            double d = 1.0 - NextDouble();
            double phase = 1.0 - NextDouble();
            double sample =
                mean + Math.Sqrt(-2.0 * Math.Log(d)) * Math.Cos(Math.PI * 2.0 * phase) * stdDev;
            if (!(sample < 0 || sample > 1))
                return sample * (max - min) + min;
        }
    }

    /// <summary>The game's <c>ListExtensions.UnstableShuffle</c>.</summary>
    public void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    public void StableShuffle<T>(List<T> items, IComparer<T> comparer)
    {
        items.Sort(comparer);
        Shuffle(items);
    }

    /// <summary>Legacy helper; beta save/clone paths must use complete snapshots.</summary>
    public void AdvanceToCallCount(int callCount)
    {
        if (callCount < CallCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(callCount),
                "Cannot rewind an RNG stream."
            );
        }

        while (CallCount < callCount)
        {
            CallCount++;
            _rng.NextInt();
        }
    }
}
