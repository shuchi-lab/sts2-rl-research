using System.Numerics;
using System.Text.Json;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Rng;

namespace CombatWorker;

/// <summary>RNG-level checks, reported to the Python test-suite as JSON.</summary>
public static class SelfTest
{
    private sealed class RefXoshiro
    {
        private ulong _s0, _s1, _s2, _s3;

        public RefXoshiro(ulong seed)
        {
            _s0 = Sm(ref seed);
            _s1 = Sm(ref seed);
            _s2 = Sm(ref seed);
            _s3 = Sm(ref seed);
        }

        private static ulong Sm(ref ulong x)
        {
            ulong z = x += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public ulong Next()
        {
            ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = BitOperations.RotateLeft(_s3, 45);
            return result;
        }
    }

    public static void Run(Action<Action<Utf8JsonWriter>> emit)
    {
        var checks = new List<(string Name, bool Ok, string Detail)>();
        void Check(string name, Func<(bool, string)> f)
        {
            try
            {
                var (ok, detail) = f();
                checks.Add((name, ok, detail));
            }
            catch (Exception ex)
            {
                checks.Add((name, false, ex.ToString()));
            }
        }

        ulong[] seeds = [0, 1, 2, 42, 0xDEADBEEF, ulong.MaxValue, 123456789012345UL];

        Check("game_mode_matches_reference_xoshiro256starstar", () =>
        {
            foreach (var seed in seeds)
            {
                var a = new MegaRandom(seed, RngAlgorithm.GameXoshiro256StarStar);
                var b = new RefXoshiro(seed);
                for (int i = 0; i < 1000; i++)
                    if (a.NextULong() != b.Next())
                        return (false, $"seed {seed} draw {i}");
            }
            return (true, "7 seeds x 1000 draws");
        });

        Check("default_context_is_game_algorithm", () =>
        {
            var r = new CountingRandom(5UL);
            return (r.Algorithm == RngAlgorithm.GameXoshiro256StarStar, r.Algorithm.ToString());
        });

        Check("training_differs_from_game_for_same_seed", () =>
        {
            int same = 0;
            foreach (var seed in seeds)
            {
                var a = new MegaRandom(seed, RngAlgorithm.GameXoshiro256StarStar);
                var b = new MegaRandom(seed, RngAlgorithm.TrainingPcg64XslRr);
                for (int i = 0; i < 1000; i++)
                    if (a.NextULong() == b.NextULong())
                        same++;
            }
            return (same == 0, $"equal 64-bit outputs: {same} / 7000");
        });

        Check("training_same_seed_repeatable", () =>
        {
            var a = new MegaRandom(99, RngAlgorithm.TrainingPcg64XslRr);
            var b = new MegaRandom(99, RngAlgorithm.TrainingPcg64XslRr);
            for (int i = 0; i < 10000; i++)
                if (a.NextULong() != b.NextULong())
                    return (false, $"draw {i}");
            return (true, "10000 draws");
        });

        Check("training_different_seeds_vary", () =>
        {
            var firsts = new HashSet<ulong>();
            for (ulong s = 0; s < 1000; s++)
                firsts.Add(new MegaRandom(s, RngAlgorithm.TrainingPcg64XslRr).NextULong());
            return (firsts.Count == 1000, $"distinct first outputs {firsts.Count}/1000");
        });

        Check("training_bounds_and_uniformity", () =>
        {
            using var _ = RngAlgorithmContext.Use(RngAlgorithm.TrainingPcg64XslRr);
            var g = new GameRng(7);
            int[] counts = new int[10];
            const int n = 200000;
            for (int i = 0; i < n; i++)
            {
                int v = g.NextInt(10);
                if (v < 0 || v >= 10)
                    return (false, $"NextInt(10) -> {v}");
                counts[v]++;
                int m = g.NextInt(-3, 4);
                if (m < -3 || m >= 4)
                    return (false, $"NextInt(-3,4) -> {m}");
                double d = g.NextDouble();
                if (d < 0 || d >= 1)
                    return (false, $"NextDouble -> {d}");
                float f = g.NextFloat(2f, 5f);
                if (f < 2f || f > 5f)
                    return (false, $"NextFloat -> {f}");
            }
            double expected = n / 10.0;
            double chi = counts.Sum(c => (c - expected) * (c - expected) / expected);
            // 9 d.o.f.: p = 0.001 at 27.88.
            return (chi < 27.88 && g.CallCount == 4 * n, $"chi2={chi:F2}, counter={g.CallCount}");
        });

        Check("training_draw_count_per_call_matches_game", () =>
        {
            // Every call family consumes exactly one 64-bit output in both modes.
            int CountDraws(RngAlgorithm alg)
            {
                using var _ = RngAlgorithmContext.Use(alg);
                var g = new GameRng(11);
                g.NextInt(5); g.NextInt(1, 9); g.NextBool(); g.NextDouble(); g.NextFloat(1f);
                g.NextUnsignedInt(7); g.NextUnsignedLong(); g.NextDouble(0.5, 2.0);
                var list = Enumerable.Range(0, 20).ToList();
                g.Shuffle(list);
                return g.CallCount;
            }
            int a = CountDraws(RngAlgorithm.GameXoshiro256StarStar);
            int b = CountDraws(RngAlgorithm.TrainingPcg64XslRr);
            return (a == b && a == 8 + 19, $"game={a} training={b}");
        });

        Check("training_shuffle_is_permutation", () =>
        {
            using var _ = RngAlgorithmContext.Use(RngAlgorithm.TrainingPcg64XslRr);
            var g = new GameRng(3);
            for (int k = 0; k < 200; k++)
            {
                var list = Enumerable.Range(0, 30).ToList();
                g.Shuffle(list);
                if (!list.OrderBy(x => x).SequenceEqual(Enumerable.Range(0, 30)))
                    return (false, "not a permutation");
            }
            return (true, "200 shuffles of 30");
        });

        Check("snapshot_restore_and_clone_preserve_algorithm", () =>
        {
            CountingRandom src;
            using (RngAlgorithmContext.Use(RngAlgorithm.TrainingPcg64XslRr))
                src = new CountingRandom(1234UL);
            for (int i = 0; i < 37; i++)
                src.Next(100);
            // Clone OUTSIDE the training scope: the snapshot, not the context, decides.
            var clone = src.Clone();
            var copyViaGame = new CountingRandom(new GameRng(0));
            copyViaGame.Restore(src.Snapshot());
            bool ok = clone.Algorithm == RngAlgorithm.TrainingPcg64XslRr
                && copyViaGame.Algorithm == RngAlgorithm.TrainingPcg64XslRr
                && clone.CallCount == 37;
            for (int i = 0; i < 1000 && ok; i++)
            {
                int x = src.Next(1000), y = clone.Next(1000), z = copyViaGame.Next(1000);
                ok = x == y && y == z;
            }
            return (ok, $"clone={clone.Algorithm} counter={clone.CallCount}");
        });

        Check("reseeded_clone_in_training_scope_is_training", () =>
        {
            CountingRandom src;
            using (RngAlgorithmContext.Use(RngAlgorithm.TrainingPcg64XslRr))
            {
                src = new CountingRandom(5UL);
                src.Next(10);
                var reseeded = src.Reseeded(77)!;
                return (reseeded.Algorithm == RngAlgorithm.TrainingPcg64XslRr && reseeded.CallCount == 1, reseeded.Algorithm.ToString());
            }
        });

        Check("reseed_preserves_algorithm_outside_scope", () =>
        {
            foreach (var alg in new[] { RngAlgorithm.GameXoshiro256StarStar, RngAlgorithm.TrainingPcg64XslRr })
            {
                var source = new CountingRandom(42UL, alg);
                for (int i = 0; i < 7; i++) source.Next();
                foreach (var newSeed in new[] { 0, 99, -3 })
                {
                    var actual = source.Reseeded(newSeed)!;
                    var expected = new CountingRandom(unchecked((uint)newSeed), alg);
                    for (int i = 0; i < 7; i++) expected.Next();
                    if (actual.Algorithm != alg || actual.CallCount != 7)
                        return (false, $"{alg}: reseed changed algorithm or counter");
                    for (int i = 0; i < 100; i++)
                        if (actual.Next() != expected.Next())
                            return (false, $"{alg}: seed {newSeed}, draw {i}");
                }
            }
            return (true, "both algorithms; three signed seeds; 100 draws each; no ambient scope");
        });

        Check("game_stream_in_training_scope_is_counted", () =>
        {
            RngAlgorithmContext.ResetGameStreamCounter();
            using (RngAlgorithmContext.Use(RngAlgorithm.TrainingPcg64XslRr))
            {
                _ = new MegaRandom(1, RngAlgorithm.GameXoshiro256StarStar);
                _ = new MegaRandom(1);
            }
            long n = RngAlgorithmContext.GameStreamsCreatedInTrainingScope;
            RngAlgorithmContext.ResetGameStreamCounter();
            return (n == 1, $"counted {n}");
        });

        Check("training_stream_in_game_scope_is_counted", () =>
        {
            RngAlgorithmContext.ResetGameStreamCounter();
            using (RngAlgorithmContext.Use(RngAlgorithm.GameXoshiro256StarStar))
            {
                _ = new MegaRandom(1, RngAlgorithm.TrainingPcg64XslRr);
                _ = new MegaRandom(1);
            }
            long n = RngAlgorithmContext.ForeignStreamsCreatedInScope;
            RngAlgorithmContext.ResetGameStreamCounter();
            return (n == 1, $"counted {n}");
        });

        Check("game_mode_seeding_equals_run_rng_set", () =>
        {
            // Game mode seeds each stream as GameRng(runSeed, name). For a real run seed this
            // must be exactly the stream RunRngSet builds for the game.
            using var _ = RngAlgorithmContext.Use(RngAlgorithm.GameXoshiro256StarStar);
            var set = new RunRngSet("ABB30XX");
            var pairs = new (GameRng Run, string Name)[]
            {
                (set.Shuffle, "shuffle"), (set.Niche, "niche"), (set.MonsterAi, "monster_ai"),
                (set.CombatTargets, "combat_targets"), (set.CombatCardSelection, "combat_card_selection"),
                (set.CombatEnergyCosts, "combat_energy_costs"), (set.CombatCardGeneration, "combat_card_generation"),
                (set.CombatPotionGeneration, "combat_potion_generation"), (set.CombatOrbs, "combat_orbs"),
            };
            foreach (var (run, name) in pairs)
            {
                var mine = new GameRng(new GameRng(set.Seed, name).RawSeed);
                if (mine.RawSeed != run.RawSeed)
                    return (false, $"{name}: seed differs");
                for (int i = 0; i < 200; i++)
                    if (mine.NextInt(1 << 20) != run.NextInt(1 << 20))
                        return (false, $"{name}: draw {i} differs");
            }
            return (true, $"{pairs.Length} named streams x 200 draws");
        });

        Check("training_seed_derivation_distinct_per_stream", () =>
        {
            var set = new HashSet<ulong>();
            foreach (var name in Sts2Emulator.Training.TrainingCombatFactory.StreamNames)
                set.Add(TrainingSeeds.For(1, name));
            set.Add(TrainingSeeds.For(1, "policy"));
            set.Add(TrainingSeeds.For(2, "shuffle"));
            return (set.Count == Sts2Emulator.Training.TrainingCombatFactory.StreamNames.Length + 2, $"{set.Count} distinct");
        });

        emit(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "selftest");
            w.WritePropertyName("checks");
            w.WriteStartArray();
            foreach (var (name, ok, detail) in checks)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteBoolean("ok", ok);
                w.WriteString("detail", detail);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>First outputs and lanes of a training stream, for an independent Python check.</summary>
    public static void Vector(ulong seed, int count, Action<Action<Utf8JsonWriter>> emit)
    {
        var m = new MegaRandom(seed, RngAlgorithm.TrainingPcg64XslRr);
        var lanes = m.Algorithm;
        var g = new CountingRandom(0UL);
        using (RngAlgorithmContext.Use(RngAlgorithm.TrainingPcg64XslRr))
            g = new CountingRandom(seed);
        var snap = g.Snapshot();
        var outs = new List<ulong>();
        var raw = new MegaRandom(seed, RngAlgorithm.TrainingPcg64XslRr);
        for (int i = 0; i < count; i++)
            outs.Add(raw.NextULong());
        emit(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "rng_vector");
            w.WriteNumber("seed", seed);
            w.WritePropertyName("initial_lanes");
            w.WriteStartArray();
            w.WriteNumberValue(snap.S0);
            w.WriteNumberValue(snap.S1);
            w.WriteNumberValue(snap.S2);
            w.WriteNumberValue(snap.S3);
            w.WriteEndArray();
            w.WritePropertyName("outputs");
            w.WriteStartArray();
            foreach (var o in outs)
                w.WriteNumberValue(o);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }
}
