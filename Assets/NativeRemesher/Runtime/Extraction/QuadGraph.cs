// The output graph, during extraction.
//
// This is deliberately a separate structure from RemeshTopology. The input graph is immutable and
// read by two solvers, so CSR is exactly right for it. The output graph is the opposite: the cleanup
// passes delete vertices, splice their neighbours together, merge pairs and split non-manifold
// vertices in place, and doing that to a CSR array means rebuilding it after every operation.
//
// So: mutable adjacency sets while cleanup runs, then Freeze() once to produce the flat,
// cyclically-ordered, reverse-slot-indexed form that face tracing wants. Tracing is the hot loop and
// wants no hashing in it; cleanup is the fiddly part and wants no index bookkeeping in it. Neither
// compromise is necessary if the two phases use different representations.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class QuadGraph
    {
        public Vector3[] positions;
        public Vector3[] normals;
        /// <summary>Representative cross direction per vertex, carried over from the input field.</summary>
        public Vector3[] directions;

        /// <summary>False once a cleanup pass has deleted the vertex.</summary>
        public bool[] alive;

        readonly List<HashSet<int>> m_Adjacency = new List<HashSet<int>>();

        /// <summary>
        /// Undirected edges that must survive cleanup — feature/crease edges. Collapsing one of
        /// these is what rounds a hard edge off, so they are protected explicitly rather than by
        /// hoping the geometry happens to prevent it.
        /// </summary>
        public readonly HashSet<long> protectedEdges = new HashSet<long>(EdgeKey.Comparer);

        public int capacity => m_Adjacency.Count;

        public int aliveCount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < alive.Length; i++) if (alive[i]) count++;
                return count;
            }
        }

        public QuadGraph(int vertexCount)
        {
            positions = new Vector3[vertexCount];
            normals = new Vector3[vertexCount];
            directions = new Vector3[vertexCount];
            alive = new bool[vertexCount];

            for (var i = 0; i < vertexCount; i++)
            {
                m_Adjacency.Add(new HashSet<int>());
                alive[i] = true;
            }
        }

        public IReadOnlyCollection<int> Neighbours(int vertex) => m_Adjacency[vertex];

        public int Valence(int vertex) => m_Adjacency[vertex].Count;

        public bool AreAdjacent(int a, int b) => m_Adjacency[a].Contains(b);

        public void AddEdge(int a, int b)
        {
            if (a == b) return;                       // self-loops are never meaningful here
            if (!alive[a] || !alive[b]) return;

            m_Adjacency[a].Add(b);
            m_Adjacency[b].Add(a);
        }

        public void RemoveEdge(int a, int b)
        {
            m_Adjacency[a].Remove(b);
            m_Adjacency[b].Remove(a);
        }

        public bool IsProtected(int a, int b) => protectedEdges.Contains(EdgeKey.PackUndirected(a, b));

        public void Protect(int a, int b) => protectedEdges.Add(EdgeKey.PackUndirected(a, b));

        /// <summary>Deletes a vertex and every edge touching it.</summary>
        public void RemoveVertex(int vertex)
        {
            if (!alive[vertex]) return;

            foreach (var neighbour in m_Adjacency[vertex])
                m_Adjacency[neighbour].Remove(vertex);

            m_Adjacency[vertex].Clear();
            alive[vertex] = false;
        }

        /// <summary>
        /// Removes a valence-2 vertex by joining its two neighbours directly. When those neighbours
        /// were already connected the splice would create a duplicate edge, so the vertex and both
        /// its edges are simply dropped instead — that configuration is a 2-gon, and keeping it
        /// would leave a degenerate face for the tracer to find.
        /// </summary>
        public bool SpliceValence2(int vertex)
        {
            if (!alive[vertex] || m_Adjacency[vertex].Count != 2) return false;

            var pair = new int[2];
            m_Adjacency[vertex].CopyTo(pair);
            int a = pair[0], b = pair[1];

            // Never splice away a vertex holding a protected edge: that is a crease corner, and its
            // position is the feature.
            if (IsProtected(vertex, a) || IsProtected(vertex, b)) return false;

            RemoveVertex(vertex);
            if (!AreAdjacent(a, b)) AddEdge(a, b);
            return true;
        }

        // MergeVertex and DuplicateVertex lived here and are deliberately gone rather than left
        // unused. Neither was ever called: clusters are merged by union-find inside LatticeCollapse
        // before this graph is built, so nothing downstream merges two graph vertices, and the
        // non-manifold resolution DuplicateVertex named was never written. Both were also shaped
        // wrong for the call they were waiting for - MergeVertex scanned the whole protectedEdges
        // set per merge, and DuplicateVertex grew four arrays one element at a time - so a caller
        // arriving later would have inherited a quadratic loop along with the method. Restore from
        // history if the split-vertex pass is ever built, but size them for the caller first.

        public int RemoveIsolatedVertices()
        {
            var removed = 0;
            for (var i = 0; i < alive.Length; i++)
            {
                if (!alive[i] || m_Adjacency[i].Count > 0) continue;
                alive[i] = false;
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// Compacts to the flat form face tracing needs: dead vertices dropped, neighbour rings in
        /// cyclic order, and every directed edge given the slot index of its reverse. That last
        /// part removes an O(valence) search from the innermost loop of the tracer.
        /// </summary>
        public FrozenQuadGraph Freeze()
        {
            var remap = new int[capacity];
            var count = 0;
            for (var i = 0; i < capacity; i++)
                remap[i] = alive[i] ? count++ : -1;

            var frozen = new FrozenQuadGraph
            {
                positions = new Vector3[count],
                normals = new Vector3[count],
                directions = new Vector3[count],
                adjacencyStart = new int[count + 1]
            };

            for (var i = 0; i < capacity; i++)
            {
                if (remap[i] < 0) continue;
                frozen.positions[remap[i]] = positions[i];
                frozen.normals[remap[i]] = normals[i];
                frozen.directions[remap[i]] = directions[i];
            }

            // Degrees, then prefix sum.
            var total = 0;
            for (var i = 0; i < capacity; i++)
            {
                if (remap[i] < 0) continue;

                var degree = 0;
                foreach (var neighbour in m_Adjacency[i])
                    if (remap[neighbour] >= 0) degree++;

                frozen.adjacencyStart[remap[i]] = total;
                total += degree;
            }
            frozen.adjacencyStart[count] = total;

            frozen.adjacency = new int[total];
            frozen.reverseSlot = new int[total];
            frozen.used = new bool[total];

            // Fill each ring in cyclic order about the vertex normal.
            var scratch = new List<int>(16);
            var angles = new List<float>(16);

            for (var i = 0; i < capacity; i++)
            {
                if (remap[i] < 0) continue;

                scratch.Clear();
                foreach (var neighbour in m_Adjacency[i])
                    if (remap[neighbour] >= 0) scratch.Add(remap[neighbour]);

                var origin = positions[i];
                var n = normals[i];
                var u = OrientationField.ArbitraryTangent(n);
                var v = OrientationField.Rotate90(u, n);

                // An arbitrary tangent frame is fine and is what the reference does: only the
                // CYCLIC order matters, and that is fixed by the handedness of (u, v, n), not by
                // where the frame's zero angle happens to point.
                angles.Clear();
                for (var k = 0; k < scratch.Count; k++)
                {
                    var d = frozen.positions[scratch[k]] - origin;
                    d -= n * Vector3.Dot(n, d);
                    angles.Add(Mathf.Atan2(Vector3.Dot(d, v), Vector3.Dot(d, u)));
                }

                var order = scratch.ToArray();
                var keys = angles.ToArray();
                Array.Sort(keys, order);

                var from = frozen.adjacencyStart[remap[i]];
                for (var k = 0; k < order.Length; k++)
                    frozen.adjacency[from + k] = order[k];
            }

            // Reverse slots, once the whole adjacency array exists.
            for (var a = 0; a < count; a++)
            {
                for (var slot = frozen.adjacencyStart[a]; slot < frozen.adjacencyStart[a + 1]; slot++)
                {
                    var b = frozen.adjacency[slot];
                    var reverse = -1;
                    for (var s = frozen.adjacencyStart[b]; s < frozen.adjacencyStart[b + 1]; s++)
                    {
                        if (frozen.adjacency[s] != a) continue;
                        reverse = s;
                        break;
                    }
                    frozen.reverseSlot[slot] = reverse;
                }
            }

            frozen.protectedEdges = new HashSet<long>(EdgeKey.Comparer);
            foreach (var key in protectedEdges)
            {
                var lo = (int)(key >> 32);
                var hi = (int)(key & 0xFFFFFFFF);
                if (lo < 0 || hi < 0 || lo >= remap.Length || hi >= remap.Length) continue;
                if (remap[lo] < 0 || remap[hi] < 0) continue;
                frozen.protectedEdges.Add(EdgeKey.PackUndirected(remap[lo], remap[hi]));
            }

            return frozen;
        }
    }

    /// <summary>
    /// The compacted output graph, ready for face tracing. Flat arrays, cyclically ordered rings,
    /// and a precomputed reverse slot for every directed edge.
    /// </summary>
    public sealed class FrozenQuadGraph
    {
        public Vector3[] positions;
        public Vector3[] normals;
        public Vector3[] directions;

        public int[] adjacencyStart;
        public int[] adjacency;

        /// <summary>For directed edge slot s, the slot holding the same edge in the other direction.</summary>
        public int[] reverseSlot;

        /// <summary>Consumed-by-a-face flag per directed edge slot. A bool[] keyed by slot rather
        /// than a hash set keyed by vertex pair: the slot index is already the natural key, and this
        /// takes hashing out of the tracer's inner loop entirely.</summary>
        public bool[] used;

        public HashSet<long> protectedEdges;

        public int vertexCount => positions.Length;
        public int slotCount => adjacency.Length;

        public int Valence(int vertex) => adjacencyStart[vertex + 1] - adjacencyStart[vertex];

        /// <summary>The slot index of the edge from <paramref name="a"/> to <paramref name="b"/>, or -1.</summary>
        public int FindSlot(int a, int b)
        {
            for (var s = adjacencyStart[a]; s < adjacencyStart[a + 1]; s++)
                if (adjacency[s] == b) return s;
            return -1;
        }

        public void ResetUsed()
        {
            Array.Clear(used, 0, used.Length);
        }
    }
}
