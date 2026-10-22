using System.Numerics;

namespace Sts2Emulator.Core.Rng;

/// <summary>
/// Faithful port of the game's <c>MegaCrit.Sts2.Core.Random.MegaRandom</c>: a
/// Xoshiro256** generator seeded through Splitmix64.
///
/// This is the generator the game actually uses for every seeded decision. The
/// emulator previously used a port of .NET's legacy subtractive Random, which
/// produces a completely different stream and is why shuffles diverged from the
/// live game — see MegaRandomHypothesisTests. That class has been deleted so it
/// cannot be wired back in.
///
/// Keep this bit-faithful: the range mapping goes through NextDouble rather than
/// a modulo or rejection scheme, and reproducing the game means reproducing that
/// exactly, including its slight bias.
/// </summary>
public sealed class MegaRandom
{
    private const double IncrDouble = 1.1102230246251565E-16;

    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    // combat_data_simulator fork: the core generator. For the training algorithm the four
    // lanes hold PCG64's 128-bit state (s0 = high, s1 = low) and odd increment (s2, s3).
    private RngAlgorithm _algorithm;

    public RngAlgorithm Algorithm => _algorithm;

    public MegaRandom(ulong seed)
        : this(seed, RngAlgorithmContext.Current) { }

    public MegaRandom(ulong seed, RngAlgorithm algorithm)
    {
        _algorithm = algorithm;
        RngAlgorithmContext.NoteStream(algorithm);
        Reinitialise(seed);
    }

    internal RngState Snapshot(int counter) => new(counter, _s0, _s1, _s2, _s3, _algorithm);

    internal void Restore(RngState state)
    {
        _s0 = state.S0;
        _s1 = state.S1;
        _s2 = state.S2;
        _s3 = state.S3;
        if (state.Algorithm != _algorithm)
            RngAlgorithmContext.NoteStream(state.Algorithm);
        _algorithm = state.Algorithm;
    }

    /// <summary>Splitmix64 PRNG, used to expand the seed into the four lanes.</summary>
    public static ulong Splitmix64(ref ulong x)
    {
        ulong num = (x += 11400714819323198485uL);
        num = (num ^ (num >> 30)) * 13787848793156543929uL;
        num = (num ^ (num >> 27)) * 10723151780598845931uL;
        return num ^ (num >> 31);
    }

    public void Reinitialise(ulong seed)
    {
        if (_algorithm == RngAlgorithm.TrainingPcg64XslRr)
        {
            ReinitialiseTraining(seed);
            return;
        }

        _s0 = Splitmix64(ref seed);
        _s1 = Splitmix64(ref seed);
        _s2 = Splitmix64(ref seed);
        _s3 = Splitmix64(ref seed);
    }

    // Domain tag so a training stream never expands a seed the way the game does.
    private const ulong TrainingSeedTag = 0x7472_6169_6E5F_7063UL; // "train_pc"

    // PCG's default 128-bit LCG multiplier, 0x2360ED051FC65DA44385DF649FCCF645.
    private static readonly UInt128 PcgMultiplier = new(0x2360ED051FC65DA4UL, 0x4385DF649FCCF645UL);

    private void ReinitialiseTraining(ulong seed)
    {
        ulong x = seed ^ TrainingSeedTag;
        var initState = new UInt128(Splitmix64(ref x), Splitmix64(ref x));
        var increment = (new UInt128(Splitmix64(ref x), Splitmix64(ref x)) << 1) | UInt128.One;
        // pcg_setseq_128_srandom_r: state = 0; step; state += initstate; step.
        UInt128 state = UInt128.Zero;
        state = unchecked(state * PcgMultiplier + increment);
        state = unchecked(state + initState);
        state = unchecked(state * PcgMultiplier + increment);
        StorePcg(state, increment);
    }

    private void StorePcg(UInt128 state, UInt128 increment)
    {
        _s0 = (ulong)(state >> 64);
        _s1 = (ulong)state;
        _s2 = (ulong)(increment >> 64);
        _s3 = (ulong)increment;
    }

    private ulong NextULongTraining()
    {
        var state = new UInt128(_s0, _s1);
        var increment = new UInt128(_s2, _s3);
        // Advance first, then output from the new state (pcg64 "XSL RR" as in NumPy's PCG64).
        state = unchecked(state * PcgMultiplier + increment);
        _s0 = (ulong)(state >> 64);
        _s1 = (ulong)state;
        ulong xored = _s0 ^ _s1;
        int rotation = (int)(_s0 >> 58);
        return BitOperations.RotateRight(xored, rotation);
    }

    private ulong NextULongInner()
    {
        if (_algorithm == RngAlgorithm.TrainingPcg64XslRr)
            return NextULongTraining();

        ulong s = _s0;
        ulong s2 = _s1;
        ulong s3 = _s2;
        ulong s4 = _s3;
        ulong result = BitOperations.RotateLeft(s2 * 5, 7) * 9;
        ulong num = s2 << 17;
        s3 ^= s;
        s4 ^= s2;
        s2 ^= s3;
        s ^= s4;
        s3 ^= num;
        s4 = BitOperations.RotateLeft(s4, 45);
        _s0 = s;
        _s1 = s2;
        _s2 = s3;
        _s3 = s4;
        return result;
    }

    public ulong NextULong() => NextULongInner();

    public double NextDouble() => (NextULongInner() >> 11) * IncrDouble;

    /// <summary>Inclusive of int.MaxValue, matching the game.</summary>
    public int NextInt() => (int)(NextULongInner() >> 33);

    public uint NextUInt() => (uint)NextULongInner();

    public bool NextBool() => (NextULongInner() & 0x8000000000000000uL) != 0;

    private int NextInner(int maxValue) => (int)(NextDouble() * maxValue);

    private long NextInner(long maxValue) => (long)(NextDouble() * maxValue);

    public int Next(int maxValue)
    {
        if (maxValue < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxValue),
                maxValue,
                "maxValue must be > 0"
            );
        }

        return NextInner(maxValue);
    }

    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxValue),
                maxValue,
                "maxValue must be > minValue"
            );
        }

        long range = (long)maxValue - minValue;
        return range <= int.MaxValue
            ? NextInner((int)range) + minValue
            : (int)(NextInner(range) + minValue);
    }
}
