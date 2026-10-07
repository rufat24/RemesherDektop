// Pass D: make the graph traceable.
//
// Face tracing assumes it is walking a clean planar embedding — every directed edge belongs to
// exactly one face, and turning consistently right eventually comes back to where it started. A
// graph straight out of the lattice collapse does not satisfy that: dangling vertices lead the walk
// into dead ends, and valence-2 vertices insert corners into faces that should not have them.
//
// The old extractor had no cleanup at all and relied on the tracer noticing and bailing out. That is
// why a failed walk was so destructive there: bailing out was the normal case.
//
// Each pass runs to a fixpoint, because they feed each other — removing a valence-1 vertex can leave
// its neighbour at valence 1, and splicing a valence-2 vertex can drop another to valence 2.

using UnityEngine;
namespace NativeRemesher.Extraction
{
    public sealed class CleanupStats
    {
        public int valence1Removed;
        public int valence2Spliced;
        public int valence2Kept;
        public int isolatedRemoved;
        public int rounds;

        /// <summary>
        /// Valence histogram of the graph handed to the tracer, index 0..7 with 7 meaning "7 or
        /// more", plus its mean.
        /// </summary>
        /// <remarks>
        /// The single most useful thing to know before blaming the tracer, and it was missing.
        /// The tracer's own statistics say what it produced; they cannot say whether it was given a
        /// quad graph to work with. A quad graph has mean valence 4 and its irregular vertices are
        /// the field's singularities and nothing else. If the graph arrives under-connected — mean
        /// valence below 4, a large valence-3 population — then no tracer can close quads on it and
        /// the defect is upstream in the lattice classification, not in the walk.
        ///
        /// Measured on the reference asset, the field wants 429 singularities and the output has
        /// 2,423 irregular vertices, so roughly 2,000 are made somewhere in extraction. This is the
        /// line that says whether they are made before this point or after it.
        /// </remarks>
        public int[] valenceHistogram = new int[8];
        public float meanValence;
        public int graphVertices;

        public override string ToString()
        {
            var histogram = new System.Text.StringBuilder();
            for (var v = 1; v < valenceHistogram.Length; v++)
            {
                if (valenceHistogram[v] == 0) continue;
                if (histogram.Length > 0) histogram.Append(' ');
                histogram.Append(v == 7 ? "7+" : v.ToString()).Append('=').Append(valenceHistogram[v]);
            }

            return $"v1removed={valence1Removed} v2spliced={valence2Spliced} " +
                   $"v2kept={valence2Kept} isolated={isolatedRemoved} rounds={rounds} " +
                   $"| graph {graphVertices} verts mean={meanValence:F3} [{histogram}]";
        }
    }

    public static class GraphCleanup
    {
        /// <summary>
        /// How far off the line through its two neighbours a valence-2 vertex may sit, as a fraction
        /// of the lattice spacing, before it is treated as a genuine corner and kept.
        /// </summary>
        public const float defaultCornerHeight = 0.25f;

        /// <summary>
        /// Corner height meaning "splice every valence-2 vertex, however far off the line it sits".
        /// </summary>
        /// <remarks>
        /// For profiles that have declared their input creaseless. The corner test protects a genuine
        /// corner from being flattened, and on an input with no corners it only protects noise, at
        /// the cost of leaving a defect in the output: a valence-2 vertex makes both faces through it
        /// one corner larger, which is where the pentagons and hexagons come from.
        /// </remarks>
        public const float spliceAlways = float.PositiveInfinity;

        /// <summary>Safety bound. Each round strictly removes vertices, so this can never bind in
        /// practice; it exists so a logic error becomes a wrong answer rather than a hung editor.</summary>
        const int k_MaxRounds = 64;

        public static CleanupStats Run(QuadGraph graph, float scale,
            float cornerHeight = defaultCornerHeight)
        {
            var stats = new CleanupStats();
            // Infinity times a zero scale is NaN, and every comparison against NaN is false, which
            // would silently invert spliceAlways into keep-everything. Handle it as the sentinel it is.
            var heightLimit = float.IsPositiveInfinity(cornerHeight)
                ? float.PositiveInfinity
                : cornerHeight * scale;

            for (var round = 0; round < k_MaxRounds; round++)
            {
                stats.rounds = round + 1;
                var changed = false;

                // --- valence 1: a dangling spur belongs to no face ---
                for (var v = 0; v < graph.capacity; v++)
                {
                    if (!graph.alive[v] || graph.Valence(v) != 1) continue;

                    graph.RemoveVertex(v);
                    stats.valence1Removed++;
                    changed = true;
                }

                // --- valence 2: splice out, unless it is a real corner ---
                for (var v = 0; v < graph.capacity; v++)
                {
                    if (!graph.alive[v] || graph.Valence(v) != 2) continue;

                    if (!IsFlatEnoughToSplice(graph, v, heightLimit))
                    {
                        stats.valence2Kept++;
                        continue;
                    }

                    if (graph.SpliceValence2(v))
                    {
                        stats.valence2Spliced++;
                        changed = true;
                    }
                    else
                    {
                        // Refused because it holds a protected feature edge.
                        stats.valence2Kept++;
                    }
                }

                // --- isolated ---
                var isolated = graph.RemoveIsolatedVertices();
                if (isolated > 0)
                {
                    stats.isolatedRemoved += isolated;
                    changed = true;
                }

                if (!changed) break;
            }

            MeasureGraph(graph, stats);
            return stats;
        }


        /// <summary>Records the valence structure of the graph the tracer is about to receive.</summary>
        static void MeasureGraph(QuadGraph graph, CleanupStats stats)
        {
            var corners = 0;

            for (var v = 0; v < graph.capacity; v++)
            {
                if (!graph.alive[v]) continue;

                var valence = graph.Valence(v);
                if (valence == 0) continue;   // isolated; contributes nothing to any face

                stats.graphVertices++;
                corners += valence;
                stats.valenceHistogram[valence < 7 ? valence : 7]++;
            }

            stats.meanValence = stats.graphVertices > 0 ? corners / (float)stats.graphVertices : 0f;
        }

        /// <summary>
        /// True when the vertex sits close enough to the straight line through its two neighbours
        /// that removing it changes nothing geometrically.
        /// </summary>
        /// <remarks>
        /// The gate matters. Splicing every valence-2 vertex unconditionally is what rounds the
        /// corners off a cube: the vertex sitting on a 90-degree crease is exactly the one whose
        /// position carries the shape, and it is also the one most likely to end up at valence 2.
        /// </remarks>
        static bool IsFlatEnoughToSplice(QuadGraph graph, int vertex, float heightLimit)
        {
            var pair = new int[2];
            var written = 0;
            foreach (var neighbour in graph.Neighbours(vertex))
            {
                if (written == 2) break;
                pair[written++] = neighbour;
            }
            if (written != 2) return false;

            var p = graph.positions[vertex];
            var a = graph.positions[pair[0]];
            var b = graph.positions[pair[1]];

            var ab = b - a;
            var lengthSqr = ab.sqrMagnitude;
            if (lengthSqr <= 1e-20f)
            {
                // The two neighbours are coincident, so there is no line to be off. Splicing would
                // join a vertex to itself; keep it.
                return false;
            }

            var t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lengthSqr);
            var closest = a + ab * t;

            return Vector3.Distance(p, closest) <= heightLimit;
        }
    }
}
