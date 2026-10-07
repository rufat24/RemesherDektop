// Stage 3: the multiresolution hierarchy. Ported from instant-meshes/src/hierarchy.cpp (BSD-3),
// MultiResolutionHierarchy::build and downsample_graph.
//
// This was the piece HANDOVER.md called the prerequisite for everything, then the plan demoted to
// a deferred "global quality upgrade" once it was established that quads could be extracted
// without it. Both readings were wrong, and the measurements say why.
//
// A single-level Gauss-Seidel solve propagates information exactly one edge per sweep. Running
// 50-120 sweeps on a 655k-vertex mesh therefore reaches a neighbourhood, never the surface, no
// matter how many iterations are spent — which is why raising the iteration count never improved
// anything. The reference runs just six sweeps per level (`levelIterations = 6` in field.cpp)
// because the hierarchy is what carries agreement globally.
//
// The cost of not having it, measured on this corpus: 8-11% of lattice edges classified as
// multi-cell and discarded, which fragmented a sphere into four disconnected shells, left 83-268
// boundary edges as visible holes, and scattered singularities. One missing component, the whole
// symptom list. See Benchmarks/README.md.
//
// Each level carries its own GraphColouring: vertices of one colour are mutually non-adjacent, so
// they can be updated in parallel while each still reads only neighbours from other colours —
// Gauss-Seidel semantics exactly, not Jacobi. The plan asserted the opposite and deferred all
// parallelism on that basis.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    /// <summary>One level of the hierarchy: a coarsened vertex graph plus its parallel colouring.</summary>
    public sealed class HierarchyLevel
    {
        public Vector3[] positions;
        public Vector3[] normals;
        public float[] areas;

        /// <summary>Mean length of this level's edges. Decides how deep the position field can go.</summary>
        public float averageEdgeLength;

        /// <summary>CSR adjacency, same convention as <see cref="RemeshTopology"/>.</summary>
        public int[] adjacencyStart;
        public int[] adjacency;

        /// <summary>
        /// The two finer-level vertices merged into each vertex of this level. Second entry is
        /// <see cref="invalid"/> for a vertex that was carried over unmerged.
        /// </summary>
        public int[] toUpperA;
        public int[] toUpperB;

        /// <summary>Finer-level vertex to its vertex on this level.</summary>
        public int[] toLower;

        /// <summary>
        /// Vertex indices grouped by colour. No two vertices in one group are adjacent, so a group
        /// can be updated in parallel without any of its members reading a value being written.
        /// </summary>
        /// <remarks>
        /// The same GraphColouring the finest level already carries, rather than a second
        /// implementation of the same thing. That matters for one reason beyond duplication: the
        /// colouring IS part of a Gauss-Seidel sweep order, so two colourings of the same graph
        /// give two different sequences of intermediate values, and a solver that ran on one at the
        /// finest level and the other at coarser levels would not be the same solver at each level.
        /// </remarks>
        public GraphColouring colouring;

        public int vertexCount => positions.Length;

        public const int invalid = -1;
    }

    public sealed class MeshHierarchy
    {
        /// <summary>
        /// Levels, finest first. Level 0 aliases the topology's own arrays and must not be mutated
        /// through this object.
        /// </summary>
        public readonly List<HierarchyLevel> levels = new List<HierarchyLevel>();

        public int levelCount => levels.Count;

        /// <summary>
        /// Cap on depth. The reference uses MAX_DEPTH for the same purpose; coarsening halves the
        /// vertex count per level at best, so 25 is far more than any real mesh needs and exists
        /// only to bound a pathological input that refuses to shrink.
        /// </summary>
        public const int maxDepth = 25;

        /// <summary>
        /// A level with fewer vertices than this is not worth coarsening further — the solve there
        /// costs nothing and the graph is too small for the matching to make progress.
        /// </summary>
        public const int minLevelVertices = 8;

        public string buildTimings { get; private set; }

        public static MeshHierarchy Build(RemeshTopology topology)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var hierarchy = new MeshHierarchy();

            var finest = new HierarchyLevel
            {
                positions = topology.positions,
                normals = topology.normals,
                areas = topology.areas,
                adjacencyStart = topology.adjacencyStart,
                adjacency = topology.adjacency,
                toUpperA = null,          // nothing above the finest level
                toUpperB = null,
                toLower = null
            };
            finest.averageEdgeLength = MeanEdgeLength(finest);
            finest.colouring = topology.colouring
                               ?? GraphColouring.Build(finest.vertexCount,
                                      finest.adjacencyStart, finest.adjacency);
            hierarchy.levels.Add(finest);

            var sizes = new List<int> { finest.vertexCount };
            var edges = new List<float> { finest.averageEdgeLength };

            while (hierarchy.levels.Count < maxDepth)
            {
                var fine = hierarchy.levels[hierarchy.levels.Count - 1];
                if (fine.vertexCount <= minLevelVertices) break;

                var coarse = Downsample(fine);

                // No progress means the matching found nothing to merge — a graph with no edges,
                // or one where every candidate was already claimed. Another level would be
                // identical, so stop rather than loop to maxDepth.
                if (coarse == null || coarse.vertexCount >= fine.vertexCount) break;

                coarse.averageEdgeLength = MeanEdgeLength(coarse);
                coarse.colouring = GraphColouring.Build(coarse.vertexCount,
                    coarse.adjacencyStart, coarse.adjacency);
                hierarchy.levels.Add(coarse);
                sizes.Add(coarse.vertexCount);
                edges.Add(coarse.averageEdgeLength);
            }

            // Edge lengths, not just sizes. They are what decides how deep the POSITION field can
            // be solved — a level whose edges are longer than one lattice cell cannot say where
            // the cell boundaries fall — so the number that sets that cut has to be visible.
            var edgeText = new string[edges.Count];
            for (var i = 0; i < edges.Count; i++) edgeText[i] = edges[i].ToString("G3");

            hierarchy.buildTimings =
                $"levels={hierarchy.levelCount} sizes=[{string.Join(",", sizes)}] " +
                $"edges=[{string.Join(",", edgeText)}] in {watch.ElapsedMilliseconds}ms";

            return hierarchy;
        }

        /// <summary>
        /// One coarsening step: a greedy maximal matching over edges scored by normal similarity
        /// times area ratio, merging each matched pair area-weighted.
        /// </summary>
        /// <remarks>
        /// Faithful to downsample_graph. Two details are easy to get backwards and both are
        /// deliberate:
        ///
        /// The score is <c>dp * ratio</c> with <c>ratio = max(A_i, A_j) / min(A_i, A_j)</c>, so it
        /// is at least 1 and *rewards* merging vertices of dissimilar area. That is not a typo in
        /// the reference: absorbing a small vertex into a large one equalises area across the
        /// coarse level, which keeps it well graded. Preferring similar areas instead makes the
        /// coarse levels progressively lumpier.
        ///
        /// And the sort is descending, so the most coplanar pairs merge first and creases survive
        /// to the coarser levels where the field most needs to see them.
        /// </remarks>
        static HierarchyLevel Downsample(HierarchyLevel fine)
        {
            var count = fine.vertexCount;
            var edgeCount = fine.adjacency.Length;      // each undirected edge appears twice
            if (edgeCount == 0) return null;

            // --- score every directed edge once, keeping only i < j so each undirected edge scores once ---
            // Sized to the full directed-edge count, not half of it. The CSR is symmetric on a
            // manifold mesh, but the non-manifold fallback in RemeshTopology.BuildRing sorts its
            // ring instead of walking a fan, so a row can be asymmetric — and silently truncating
            // the candidate list there would drop edges from the matching without saying so.
            var scores = new float[edgeCount + 1];
            var edgeA = new int[scores.Length];
            var edgeB = new int[scores.Length];
            var candidates = 0;

            for (var i = 0; i < count; i++)
            {
                for (var s = fine.adjacencyStart[i]; s < fine.adjacencyStart[i + 1]; s++)
                {
                    var j = fine.adjacency[s];
                    if (j <= i) continue;
                    if (candidates >= scores.Length) break;   // unreachable: bound is the directed-edge count

                    var dp = Vector3.Dot(fine.normals[i], fine.normals[j]);

                    var ai = Mathf.Max(fine.areas[i], 1e-20f);
                    var aj = Mathf.Max(fine.areas[j], 1e-20f);
                    var ratio = ai > aj ? ai / aj : aj / ai;

                    scores[candidates] = dp * ratio;
                    edgeA[candidates] = i;
                    edgeB[candidates] = j;
                    candidates++;
                }
            }

            if (candidates == 0) return null;

            // Descending by score. Sorting the negated key keeps this to one Array.Sort over an
            // index permutation, which is the cheapest ordering available without a custom sort.
            var order = new int[candidates];
            var keys = new float[candidates];
            for (var k = 0; k < candidates; k++) { order[k] = k; keys[k] = -scores[k]; }
            Array.Sort(keys, order);

            // --- greedy maximal matching ---
            var merged = new bool[count];
            var toLower = new int[count];
            for (var v = 0; v < count; v++) toLower[v] = HierarchyLevel.invalid;

            var upperA = new List<int>(count / 2 + 1);
            var upperB = new List<int>(count / 2 + 1);

            for (var k = 0; k < candidates; k++)
            {
                var e = order[k];
                int i = edgeA[e], j = edgeB[e];
                if (merged[i] || merged[j]) continue;

                merged[i] = merged[j] = true;

                var coarseIndex = upperA.Count;
                upperA.Add(i);
                upperB.Add(j);
                toLower[i] = coarseIndex;
                toLower[j] = coarseIndex;
            }

            // Everything the matching did not claim carries over as a singleton, so no vertex is
            // ever dropped between levels — losing one would tear the coarse graph exactly the way
            // the discarded lattice edges tear the extraction graph.
            for (var v = 0; v < count; v++)
            {
                if (toLower[v] != HierarchyLevel.invalid) continue;
                toLower[v] = upperA.Count;
                upperA.Add(v);
                upperB.Add(HierarchyLevel.invalid);
            }

            var coarseCount = upperA.Count;
            var coarse = new HierarchyLevel
            {
                positions = new Vector3[coarseCount],
                normals = new Vector3[coarseCount],
                areas = new float[coarseCount],
                toUpperA = upperA.ToArray(),
                toUpperB = upperB.ToArray(),
                toLower = toLower
            };

            // --- area-weighted merge ---
            for (var c = 0; c < coarseCount; c++)
            {
                var a = coarse.toUpperA[c];
                var b = coarse.toUpperB[c];

                if (b == HierarchyLevel.invalid)
                {
                    coarse.positions[c] = fine.positions[a];
                    coarse.normals[c] = fine.normals[a];
                    coarse.areas[c] = fine.areas[a];
                    continue;
                }

                var wa = fine.areas[a];
                var wb = fine.areas[b];
                var sum = wa + wb;

                if (sum > 1e-20f)
                {
                    coarse.positions[c] = (fine.positions[a] * wa + fine.positions[b] * wb) / sum;
                    var n = fine.normals[a] * wa + fine.normals[b] * wb;
                    coarse.normals[c] = MeshMath.SafeNormalize(n, fine.normals[a]);
                }
                else
                {
                    coarse.positions[c] = (fine.positions[a] + fine.positions[b]) * 0.5f;
                    var n = fine.normals[a] + fine.normals[b];
                    coarse.normals[c] = MeshMath.SafeNormalize(n, fine.normals[a]);
                }

                coarse.areas[c] = sum;
            }

            BuildCoarseAdjacency(fine, coarse);
            return coarse;
        }

        static float MeanEdgeLength(HierarchyLevel level)
        {
            var total = 0.0;
            var counted = 0;

            for (var v = 0; v < level.vertexCount; v++)
            {
                for (var s = level.adjacencyStart[v]; s < level.adjacencyStart[v + 1]; s++)
                {
                    var w = level.adjacency[s];
                    if (w <= v) continue;

                    total += Vector3.Distance(level.positions[v], level.positions[w]);
                    counted++;
                }
            }

            return counted > 0 ? (float)(total / counted) : 0f;
        }

        /// <summary>
        /// Projects the fine adjacency through <c>toLower</c>, dropping self-links and duplicates.
        /// </summary>
        static void BuildCoarseAdjacency(HierarchyLevel fine, HierarchyLevel coarse)
        {
            var coarseCount = coarse.vertexCount;
            var rows = new List<int>[coarseCount];
            var seen = new HashSet<int>();

            for (var c = 0; c < coarseCount; c++)
            {
                seen.Clear();
                var row = new List<int>(8);

                for (var side = 0; side < 2; side++)
                {
                    var f = side == 0 ? coarse.toUpperA[c] : coarse.toUpperB[c];
                    if (f == HierarchyLevel.invalid) continue;

                    for (var s = fine.adjacencyStart[f]; s < fine.adjacencyStart[f + 1]; s++)
                    {
                        var neighbour = coarse.toLower[fine.adjacency[s]];
                        if (neighbour == c) continue;              // both endpoints merged into c
                        if (seen.Add(neighbour)) row.Add(neighbour);
                    }
                }

                rows[c] = row;
            }

            coarse.adjacencyStart = new int[coarseCount + 1];
            var total = 0;
            for (var c = 0; c < coarseCount; c++)
            {
                coarse.adjacencyStart[c] = total;
                total += rows[c].Count;
            }
            coarse.adjacencyStart[coarseCount] = total;

            coarse.adjacency = new int[total];
            for (var c = 0; c < coarseCount; c++)
                rows[c].CopyTo(coarse.adjacency, coarse.adjacencyStart[c]);
        }

    }
}
