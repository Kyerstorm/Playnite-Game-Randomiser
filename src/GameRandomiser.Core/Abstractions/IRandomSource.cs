using System;
using System.Security.Cryptography;

namespace GameRandomiser.Core.Abstractions
{
    /// <summary>Source of uniform randomness. Injected so tests can be deterministic.</summary>
    public interface IRandomSource
    {
        /// <summary>Uniform integer in [0, maxExclusive).</summary>
        int NextInt(int maxExclusive);

        /// <summary>Uniform double in [0, 1).</summary>
        double NextDouble();
    }

    /// <summary>
    /// Cryptographically strong, unbiased random source used in production.
    /// Uses rejection sampling so every index is exactly equally likely (no modulo bias).
    /// </summary>
    public sealed class CryptoRandomSource : IRandomSource, IDisposable
    {
        private readonly RandomNumberGenerator rng = RandomNumberGenerator.Create();
        private readonly byte[] buffer = new byte[8];
        private readonly object sync = new object();

        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            }

            if (maxExclusive == 1)
            {
                return 0;
            }

            // Largest multiple of maxExclusive that fits in uint range; values above it are rejected.
            var range = (ulong)uint.MaxValue + 1;
            var limit = range - (range % (ulong)maxExclusive);
            while (true)
            {
                var value = NextUInt32();
                if (value < limit)
                {
                    return (int)(value % (ulong)maxExclusive);
                }
            }
        }

        public double NextDouble()
        {
            ulong value;
            lock (sync)
            {
                rng.GetBytes(buffer);
                value = BitConverter.ToUInt64(buffer, 0);
            }

            // 53 random bits -> [0, 1) with full double precision.
            return (value >> 11) * (1.0 / (1UL << 53));
        }

        private ulong NextUInt32()
        {
            lock (sync)
            {
                rng.GetBytes(buffer);
                return BitConverter.ToUInt32(buffer, 0);
            }
        }

        public void Dispose() => rng.Dispose();
    }

    /// <summary>Seeded pseudo-random source, for tests and reproducible simulations.</summary>
    public sealed class SeededRandomSource : IRandomSource
    {
        private readonly Random random;

        public SeededRandomSource(int seed)
        {
            random = new Random(seed);
        }

        public int NextInt(int maxExclusive) => random.Next(maxExclusive);

        public double NextDouble() => random.NextDouble();
    }
}
