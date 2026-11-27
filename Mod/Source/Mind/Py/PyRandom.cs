// Exact CPython random.Random (Mersenne Twister MT19937) so a seeded run draws the same numbers as the Python.
// Verified against Python 3.14 recorded sequences (Mod.Tests/Mind/PyRandomTests.cs).
// API: new PyRandom(long seed); Random(); GetRandBits(k); RandBelow(n); RandRange(stop) / RandRange(start, stop);
// RandInt(a, b) (inclusive); Choice<T>(IList<T>); Shuffle<T>(IList<T>); Sample<T>(IList<T>, k);
// Uniform(a, b); Choices<T>(IList<T>, k) (uniform, no weights).
// Only integer seeds are supported (the Python package seeds only with ints).
using System;
using System.Collections.Generic;
using System.Numerics;

namespace RimWorldLLM.Mind.Py
{
    public sealed class PyRandom
    {
        private const int N = 624, M = 397;
        private readonly uint[] mt = new uint[N];
        private int index = N + 1;

        public PyRandom(long seed) { Seed(seed); }

        /// <summary>random.seed(int): key = |seed| as little-endian 32-bit words, init_by_array.</summary>
        public void Seed(long seed)
        {
            BigInteger a = BigInteger.Abs(new BigInteger(seed));
            var key = new List<uint>();
            if (a.IsZero) key.Add(0);
            while (!a.IsZero) { key.Add((uint)(a & 0xFFFFFFFF)); a >>= 32; }
            InitByArray(key.ToArray());
        }

        private void InitGenrand(uint s)
        {
            mt[0] = s;
            for (int i = 1; i < N; i++)
                mt[i] = unchecked(1812433253u * (mt[i - 1] ^ (mt[i - 1] >> 30)) + (uint)i);
            index = N;
        }

        private void InitByArray(uint[] key)
        {
            InitGenrand(19650218u);
            int i = 1, j = 0, k = Math.Max(N, key.Length);
            for (; k > 0; k--)
            {
                mt[i] = unchecked((mt[i] ^ ((mt[i - 1] ^ (mt[i - 1] >> 30)) * 1664525u)) + key[j] + (uint)j);
                i++; j++;
                if (i >= N) { mt[0] = mt[N - 1]; i = 1; }
                if (j >= key.Length) j = 0;
            }
            for (k = N - 1; k > 0; k--)
            {
                mt[i] = unchecked((mt[i] ^ ((mt[i - 1] ^ (mt[i - 1] >> 30)) * 1566083941u)) - (uint)i);
                i++;
                if (i >= N) { mt[0] = mt[N - 1]; i = 1; }
            }
            mt[0] = 0x80000000u;
            index = N;
        }

        private uint GenRand32()
        {
            if (index >= N)
            {
                int kk;
                uint y;
                for (kk = 0; kk < N - M; kk++)
                {
                    y = (mt[kk] & 0x80000000u) | (mt[kk + 1] & 0x7fffffffu);
                    mt[kk] = mt[kk + M] ^ (y >> 1) ^ ((y & 1u) != 0 ? 0x9908b0dfu : 0u);
                }
                for (; kk < N - 1; kk++)
                {
                    y = (mt[kk] & 0x80000000u) | (mt[kk + 1] & 0x7fffffffu);
                    mt[kk] = mt[kk + (M - N)] ^ (y >> 1) ^ ((y & 1u) != 0 ? 0x9908b0dfu : 0u);
                }
                y = (mt[N - 1] & 0x80000000u) | (mt[0] & 0x7fffffffu);
                mt[N - 1] = mt[M - 1] ^ (y >> 1) ^ ((y & 1u) != 0 ? 0x9908b0dfu : 0u);
                index = 0;
            }
            uint r = mt[index++];
            r ^= r >> 11;
            r ^= (r << 7) & 0x9d2c5680u;
            r ^= (r << 15) & 0xefc60000u;
            r ^= r >> 18;
            return r;
        }

        /// <summary>random.random(): float in [0, 1) with 53 bits.</summary>
        public double Random()
        {
            uint a = GenRand32() >> 5, b = GenRand32() >> 6;
            return (a * 67108864.0 + b) * (1.0 / 9007199254740992.0);
        }

        /// <summary>random.getrandbits(k) for k >= 0 (result as BigInteger for k > 63).</summary>
        public BigInteger GetRandBitsBig(int k)
        {
            if (k < 0) throw new ArgumentOutOfRangeException(nameof(k));
            if (k == 0) return BigInteger.Zero;
            if (k <= 32) return GenRand32() >> (32 - k);
            BigInteger result = BigInteger.Zero;
            int words = (k - 1) / 32 + 1, shift = 0;
            for (int i = 0; i < words; i++, k -= 32, shift += 32)
            {
                uint r = GenRand32();
                if (k < 32) r >>= (32 - k);
                result |= new BigInteger(r) << shift;
            }
            return result;
        }

        /// <summary>random.getrandbits(k) for 0 &lt;= k &lt;= 63.</summary>
        public long GetRandBits(int k)
        {
            if (k > 63) throw new ArgumentOutOfRangeException(nameof(k));
            return (long)GetRandBitsBig(k);
        }

        /// <summary>Random._randbelow(n): uniform int in [0, n); 0 when n is 0.</summary>
        public long RandBelow(long n)
        {
            if (n <= 0) return 0;
            int k = 64 - BitOperations.LeadingZeroCount((ulong)n);
            long r = GetRandBits(k);
            while (r >= n) r = GetRandBits(k);
            return r;
        }

        public long RandRange(long stop)
        {
            if (stop <= 0) throw new ArgumentException("empty range for RandRange()");
            return RandBelow(stop);
        }

        public long RandRange(long start, long stop)
        {
            long width = stop - start;
            if (width <= 0) throw new ArgumentException("empty range in RandRange(" + start + ", " + stop + ")");
            return start + RandBelow(width);
        }

        /// <summary>randint(a, b): inclusive.</summary>
        public long RandInt(long a, long b) { return RandRange(a, b + 1); }

        public T Choice<T>(IList<T> seq)
        {
            if (seq.Count == 0) throw new ArgumentException("Cannot choose from an empty sequence");
            return seq[(int)RandBelow(seq.Count)];
        }

        public void Shuffle<T>(IList<T> x)
        {
            for (int i = x.Count - 1; i >= 1; i--)
            {
                int j = (int)RandBelow(i + 1);
                T t = x[i]; x[i] = x[j]; x[j] = t;
            }
        }

        public List<T> Sample<T>(IList<T> population, int k)
        {
            int n = population.Count;
            if (k < 0 || k > n) throw new ArgumentException("Sample larger than population or is negative");
            var result = new List<T>(new T[k]);
            long setsize = 21;
            if (k > 5) setsize += (long)Math.Pow(4, Math.Ceiling(Math.Log(k * 3.0, 4)));
            if (n <= setsize)
            {
                var pool = new List<T>(population);
                for (int i = 0; i < k; i++)
                {
                    int j = (int)RandBelow(n - i);
                    result[i] = pool[j];
                    pool[j] = pool[n - i - 1];
                }
            }
            else
            {
                var selected = new HashSet<int>();
                for (int i = 0; i < k; i++)
                {
                    int j = (int)RandBelow(n);
                    while (selected.Contains(j)) j = (int)RandBelow(n);
                    selected.Add(j);
                    result[i] = population[j];
                }
            }
            return result;
        }

        public double Uniform(double a, double b) { return a + (b - a) * Random(); }

        /// <summary>random.choices(population, k=k) without weights.</summary>
        public List<T> Choices<T>(IList<T> population, int k)
        {
            var r = new List<T>(k);
            int n = population.Count;
            for (int i = 0; i < k; i++) r.Add(population[(int)Math.Floor(Random() * n)]);
            return r;
        }
    }
}
