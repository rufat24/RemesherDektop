// Packing a vertex pair into a long is the obvious way to key an edge. Handing that long to a
// .NET hash set is a trap, and it cost this project a 29-minute topology build once already.
//
// long.GetHashCode() is (int)v ^ (int)(v >> 32). For the packing ((long)a << 32) | (uint)b that
// is literally `a ^ b`. Vertex indices on a subdivided surface are small and strongly correlated,
// so vast numbers of distinct edges hash to the same value, buckets degenerate into chains whose
// length grows with the mesh, and every lookup becomes a linear scan. The container stays
// correct and quietly goes quadratic.
//
// So: pack with Pack, and always pass Comparer to the container.

using System.Collections.Generic;

namespace NativeRemesher.Extraction
{
    public static class EdgeKey
    {
        public static long Pack(int a, int b) => ((long)a << 32) | (uint)b;

        public static int First(long key) => (int)(key >> 32);

        public static int Second(long key) => unchecked((int)(key & 0xFFFFFFFFL));

        /// <summary>Ordered so an undirected edge has one key regardless of which way it is given.</summary>
        public static long PackUndirected(int a, int b) => a < b ? Pack(a, b) : Pack(b, a);

        public static readonly IEqualityComparer<long> Comparer = new MixComparer();

        sealed class MixComparer : IEqualityComparer<long>
        {
            public bool Equals(long x, long y) => x == y;

            public int GetHashCode(long key)
            {
                // splitmix64's finalising avalanche, folded to 32 bits. Every input bit affects
                // every output bit, which is the property the default XOR fold lacks.
                unchecked
                {
                    var z = (ulong)key;
                    z ^= z >> 30; z *= 0xBF58476D1CE4E5B9UL;
                    z ^= z >> 27; z *= 0x94D049BB133111EBUL;
                    z ^= z >> 31;
                    return (int)z ^ (int)(z >> 32);
                }
            }
        }
    }
}
