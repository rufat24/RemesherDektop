// Port of QuadriFlow's geometric pre-subdivision (the `subdivide` in src/subdivide.cpp, called from
// Parametrizer::Initialize before any field is solved).
//
// The integer grid map reads one lattice offset per INPUT edge. That only works when an input edge
// spans at most one lattice cell: an edge crossing two cells cannot carry a unit offset, and the
// integer subdivision meant to repair it has to split the same edge over and over, re-deriving an
// out-of-range offset on each half. Measured on this repo's corpus that is exactly where the map
// fails -- the cases that break are the ones with a few very long input edges (a cylinder cap fan,
// a decimated scan), not the ones with many.
//
// The reference never lets that happen, because it bounds the input's LONGEST edge before solving
// anything. Pre-decimation in RemeshPipeline controls the average edge length; this controls the
// maximum, and they are not the same constraint: a fan can have a fine average and still carry
// edges ten cells long.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    /// <summary>
    /// Splits input triangles until no edge is longer than half a lattice cell, so that every edge
    /// the integer grid map sees can carry a unit offset.
    /// </summary>
    public static class InputRefine
    {
        public struct Stats
        {
            public int splits;
            public int facesBefore, facesAfter;
            public float scale, targetLength;
            public float maxEdgeBefore, maxEdgeAfter, averageEdgeBefore;
            public bool capped;
            public long ms;
            /// <summary>RefineLocal only: the longest edge measured in LOCAL cells, before.</summary>
            public float worstCells;

            public override string ToString() =>
                $"refine: f={facesBefore}->{facesAfter} splits={splits} scale={scale:F5} " +
                $"target={targetLength:F5} maxEdge={maxEdgeBefore:F5}->{maxEdgeAfter:F5}" +
                (capped ? " CAPPED" : "");
        }

        /// <summary>
        /// Length, in lattice cells, input edges are split down to once refinement triggers. The
        /// reference's half cell; anything under one keeps the invariant (one offset per edge),
        /// the rest is margin.
        /// </summary>
        /// <remarks>
        /// 0.7, not the reference's 0.5. Every stage after this scales with the refined face count,
        /// and at 0.7 there are about half as many. Means over 4600/5000/5400 with Curves Add Quads,
        /// irregular / Hausdorff / open edges, against 0.5: body 1.84 % / 0.0093 / 0 vs 1.96 % /
        /// 0.0093 / 13 at 4600; head 4.31 % / 0.0150 / 0 vs 4.59 % / 0.0196 / 13 -- and run time
        /// 55 vs 115 s (body), 165 vs 316 s (head), measured six runs at a time.
        /// </remarks>
        public static float cellFraction = 0.7f;

        static int Next3(int h) => h % 3 == 2 ? h - 2 : h + 1;
        static int Prev3(int h) => h % 3 == 0 ? h + 2 : h - 1;

        /// <summary>Max-heap of half-edges keyed on squared length, as the reference's queue is.</summary>
        sealed class Heap
        {
            readonly List<KeyValuePair<float, int>> m_Items = new List<KeyValuePair<float, int>>();

            public int count => m_Items.Count;

            public void Push(float key, int value)
            {
                m_Items.Add(new KeyValuePair<float, int>(key, value));
                var i = m_Items.Count - 1;
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (m_Items[parent].Key >= m_Items[i].Key) break;
                    (m_Items[parent], m_Items[i]) = (m_Items[i], m_Items[parent]);
                    i = parent;
                }
            }

            public KeyValuePair<float, int> Pop()
            {
                var top = m_Items[0];
                m_Items[0] = m_Items[m_Items.Count - 1];
                m_Items.RemoveAt(m_Items.Count - 1);
                var i = 0;
                while (true)
                {
                    var l = 2 * i + 1;
                    var r = l + 1;
                    var best = i;
                    if (l < m_Items.Count && m_Items[l].Key > m_Items[best].Key) best = l;
                    if (r < m_Items.Count && m_Items[r].Key > m_Items[best].Key) best = r;
                    if (best == i) break;
                    (m_Items[best], m_Items[i]) = (m_Items[i], m_Items[best]);
                    i = best;
                }
                return top;
            }
        }

        /// <summary>
        /// The reference's <c>target_len = min(scale / 2, average_edge_length * 2)</c>, with
        /// <c>scale = sqrt(surface_area / faces)</c>. The second term keeps an already fine mesh
        /// from being refined pointlessly when the requested count is very low.
        /// </summary>
        public static float TargetLength(Vector3[] positions, int[] triangles, int targetQuadCount,
            out float scale, out float averageEdge, out float maxEdge)
        {
            double area = 0, edgeSum = 0;
            maxEdge = 0f;
            var faces = triangles.Length / 3;
            for (var f = 0; f < faces; f++)
            {
                var a = positions[triangles[3 * f]];
                var b = positions[triangles[3 * f + 1]];
                var c = positions[triangles[3 * f + 2]];
                area += 0.5 * Vector3.Cross(b - a, c - a).magnitude;
                for (var i = 0; i < 3; i++)
                {
                    var len = (positions[triangles[3 * f + (i + 1) % 3]] -
                               positions[triangles[3 * f + i]]).magnitude;
                    edgeSum += len;
                    if (len > maxEdge) maxEdge = len;
                }
            }

            averageEdge = faces > 0 ? (float)(edgeSum / (faces * 3)) : 0f;
            scale = targetQuadCount > 0 ? Mathf.Sqrt((float)(area / targetQuadCount)) : 0f;
            return Mathf.Min(scale * cellFraction, averageEdge * 2f);
        }

        /// <summary>
        /// Returns false, leaving the outputs pointing at the input arrays, when the mesh already
        /// satisfies the bound and nothing needs to be done.
        /// </summary>
        public static bool Refine(Vector3[] positions, int[] triangles, int targetQuadCount,
            out Vector3[] outPositions, out int[] outTriangles, out Stats stats)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats = default;
            outPositions = positions;
            outTriangles = triangles;

            stats.facesBefore = stats.facesAfter = triangles.Length / 3;
            var target = TargetLength(positions, triangles, targetQuadCount,
                out stats.scale, out stats.averageEdgeBefore, out stats.maxEdgeBefore);
            stats.targetLength = target;
            stats.maxEdgeAfter = stats.maxEdgeBefore;

            // The reference refines whenever the longest edge exceeds the target, i.e. half a cell.
            // We trigger a cell later, because refining a mesh that did not need it costs us field
            // quality the reference does not pay: our orientation solve is hierarchical over the
            // input triangles, and splitting a regular tessellation moves it to a worse minimum --
            // measured on the torus, which goes from 0 field singularities to 16 and from 0 to 22
            // irregular vertices for no gain, its longest edge already fitting inside one cell.
            //
            // A full cell is the real invariant: the map stores one lattice offset per input edge,
            // so an edge is only unrepresentable once it spans more than a cell. Every case that was
            // broken sits well past that (cylinder 1.43 cells, cube 18.3); every case that was
            // already perfect sits under it (torus 0.90, icosphere 0.80, disc 0.96). Once we do
            // refine we still refine down to the reference's half-cell target, for the margin.
            if (target <= 0f || stats.maxEdgeBefore <= stats.scale) return false;

            var ok = Split(positions, triangles, null, target, ref stats, out outPositions,
                out outTriangles, out _);
            stats.ms = watch.ElapsedMilliseconds;
            return ok;
        }

        /// <summary>
        /// Refinement against a per-vertex spacing rather than one global one: an edge is split
        /// while it is longer than half the mean of its endpoints' lattice spacings, and a split
        /// vertex takes the mean of its edge's two. Returns false, leaving the outputs pointing at
        /// the inputs, when no edge spans more than one local cell.
        /// </summary>
        /// <remarks>
        /// This is what lets curvature grading back onto the integer grid map. <see cref="Refine"/>
        /// bounds every edge against the GLOBAL spacing; with grading on, the local spacing in a
        /// curved region is a fraction of that, so edges which pass the global bound still span
        /// several local cells and their offsets cannot be represented -- measured, that was the
        /// holes (724 open edges against 12 on the body). Bounding against the local spacing
        /// restores the invariant where it is actually needed and nowhere else, which is also why
        /// it is cheap: the finest local cell is half the mean, so at most one more level of splits
        /// in the curved regions only.
        ///
        /// Same trigger as <see cref="Refine"/>: nothing is touched unless some edge spans a full
        /// local cell, and once refining it goes down to the half-cell target for the margin.
        /// </remarks>
        public static bool RefineLocal(Vector3[] positions, int[] triangles, float[] spacing,
            out Vector3[] outPositions, out int[] outTriangles, out float[] outSpacing,
            out Stats stats)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (spacing == null || spacing.Length != positions.Length)
                throw new ArgumentException("One spacing per position.", nameof(spacing));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats = default;
            outPositions = positions;
            outTriangles = triangles;
            outSpacing = spacing;
            stats.facesBefore = stats.facesAfter = triangles.Length / 3;

            var worst = 0f;
            var minSpacing = float.MaxValue;
            double edgeSum = 0;
            for (var h = 0; h < triangles.Length; h++)
            {
                var a = triangles[h];
                var b = triangles[Next3(h)];
                var len = (positions[a] - positions[b]).magnitude;
                edgeSum += len;
                stats.maxEdgeBefore = Mathf.Max(stats.maxEdgeBefore, len);
                var local = 0.5f * (spacing[a] + spacing[b]);
                if (local > 0f) worst = Mathf.Max(worst, len / local);
                minSpacing = Mathf.Min(minSpacing, spacing[a]);
            }
            stats.averageEdgeBefore = triangles.Length > 0 ? (float)(edgeSum / triangles.Length) : 0f;
            stats.maxEdgeAfter = stats.maxEdgeBefore;
            stats.scale = minSpacing < float.MaxValue ? minSpacing : 0f;
            stats.targetLength = 0.5f * stats.scale;
            stats.worstCells = worst;

            if (worst <= 1f) return false;

            var ok = Split(positions, triangles, spacing, 0f, ref stats, out outPositions,
                out outTriangles, out outSpacing);
            stats.ms = watch.ElapsedMilliseconds;
            return ok;
        }

        /// <summary>
        /// The reference's split loop. With <paramref name="spacing"/> null every edge is bounded by
        /// <paramref name="target"/>; otherwise each by half its endpoints' mean spacing.
        /// </summary>
        static bool Split(Vector3[] positions, int[] triangles, float[] spacing, float target,
            ref Stats stats, out Vector3[] outPositions, out int[] outTriangles,
            out float[] outSpacing)
        {
            outPositions = positions;
            outTriangles = triangles;
            outSpacing = spacing;

            var topology = RemeshTopology.Build(positions, triangles);
            var nonManifold = topology.isNonManifoldVertex;

            // The twins below are the topology's, numbered over ITS triangles. Welding drops the
            // triangles it collapses (a tiny part's slivers, once imported at 0.01: the Sketchfab
            // helmet's studs), and the input's numbering then no longer lines up -- an index past the
            // end, 2026-10-19. Split the welded arrays instead, each vertex taking the spacing of its
            // first source vertex.
            if (topology.triangles.Length != triangles.Length)
            {
                if (spacing != null)
                {
                    var welded = new float[topology.vertexCount];
                    for (var i = 0; i < welded.Length; i++) welded[i] = -1f;
                    for (var i = 0; i < topology.sourceToWelded.Length; i++)
                    {
                        var w = topology.sourceToWelded[i];
                        if (w >= 0 && welded[w] < 0f) welded[w] = spacing[i];
                    }
                    spacing = welded;
                }
                positions = topology.positions;
                triangles = topology.triangles;
            }

            var V = new List<Vector3>(positions);
            var F = new List<int>(triangles);
            var E2E = new List<int>(topology.halfEdgeTwin);

            // Twins must pair up, exactly as IntegerGridMap.Build requires: a third half-edge on a
            // non-manifold edge is linked one way only, and following it would corrupt the split.
            for (var h = 0; h < E2E.Count; h++)
            {
                var t = E2E[h];
                if (t >= 0 && E2E[t] != h) E2E[h] = -1;
            }

            var nm = new List<bool>(V.Count);
            for (var v = 0; v < V.Count; v++) nm.Add(v < nonManifold.Length && nonManifold[v]);

            var S = spacing != null ? new List<float>(spacing) : null;
            var maxSq = target * target;
            float LimitSq(int a, int b)
            {
                if (S == null) return maxSq;
                var l = 0.5f * cellFraction * (S[a] + S[b]);
                return l * l;
            }
            var queue = new Heap();

            for (var h = 0; h < E2E.Count; h++)
            {
                var v0 = F[h];
                var v1 = F[Next3(h)];
                if (nm[v0] || nm[v1]) continue;
                var len = (V[v0] - V[v1]).sqrMagnitude;
                if (len <= LimitSq(v0, v1)) continue;
                var other = E2E[h];
                if (other == -1 || other > h) queue.Push(len, h);
            }

            // A split adds three faces, so an input triangle with an edge n cells long costs about
            // n^2 faces. The cap is a guard against a pathological input, not a budget.
            var maxFaces = Mathf.Max(1 << 16, 32 * (triangles.Length / 3));

            void Schedule(int f)
            {
                for (var i = 0; i < 3; i++)
                {
                    var h = 3 * f + i;
                    var len = (V[F[h]] - V[F[Next3(h)]]).sqrMagnitude;
                    if (len > LimitSq(F[h], F[Next3(h)])) queue.Push(len, h);
                }
            }

            void SetTwin(int a, int b)
            {
                E2E[a] = b;
                if (b != -1) E2E[b] = a;
            }

            int AddFace()
            {
                F.Add(-1); F.Add(-1); F.Add(-1);
                E2E.Add(-1); E2E.Add(-1); E2E.Add(-1);
                return F.Count / 3 - 1;
            }

            while (queue.count > 0)
            {
                if (F.Count / 3 >= maxFaces)
                {
                    stats.capped = true;
                    break;
                }

                var top = queue.Pop();
                var e0 = top.Value;
                var e1 = E2E[e0];
                var isBoundary = e1 == -1;
                var f0 = e0 / 3;
                var f1 = isBoundary ? -1 : e1 / 3;

                var v0 = F[e0];
                var v1 = F[Next3(e0)];
                var v0p = F[Prev3(e0)];

                // A stale entry: this half-edge was already split, so its endpoints have moved.
                if ((V[v0] - V[v1]).sqrMagnitude != top.Key) continue;

                var v1p = isBoundary ? -1 : F[Prev3(e1)];

                var vn = V.Count;
                V.Add((V[v0] + V[v1]) * 0.5f);
                S?.Add(0.5f * (S[v0] + S[v1]));
                nm.Add(false);
                stats.splits++;

                var e0p = E2E[Prev3(e0)];
                var e0n = E2E[Next3(e0)];
                var e1p = isBoundary ? -1 : E2E[Prev3(e1)];
                var e1n = isBoundary ? -1 : E2E[Next3(e1)];

                var f2 = isBoundary ? -1 : AddFace();
                var f3 = AddFace();

                F[3 * f0] = vn; F[3 * f0 + 1] = v0p; F[3 * f0 + 2] = v0;
                if (!isBoundary)
                {
                    F[3 * f1] = vn; F[3 * f1 + 1] = v0; F[3 * f1 + 2] = v1p;
                    F[3 * f2] = vn; F[3 * f2 + 1] = v1p; F[3 * f2 + 2] = v1;
                }
                F[3 * f3] = vn; F[3 * f3 + 1] = v1; F[3 * f3 + 2] = v0p;

                SetTwin(3 * f0 + 0, 3 * f3 + 2);
                SetTwin(3 * f0 + 1, e0p);
                SetTwin(3 * f3 + 1, e0n);
                if (isBoundary)
                {
                    SetTwin(3 * f0 + 2, -1);
                    SetTwin(3 * f3 + 0, -1);
                }
                else
                {
                    SetTwin(3 * f0 + 2, 3 * f1 + 0);
                    SetTwin(3 * f1 + 1, e1n);
                    SetTwin(3 * f1 + 2, 3 * f2 + 0);
                    SetTwin(3 * f2 + 1, e1p);
                    SetTwin(3 * f2 + 2, 3 * f3 + 0);
                }

                Schedule(f0);
                if (!isBoundary)
                {
                    Schedule(f2);
                    Schedule(f1);
                }
                Schedule(f3);
            }

            outPositions = V.ToArray();
            outTriangles = F.ToArray();
            stats.facesAfter = F.Count / 3;

            stats.maxEdgeAfter = 0f;
            for (var h = 0; h < F.Count; h++)
            {
                var len = (V[F[h]] - V[F[Next3(h)]]).magnitude;
                if (len > stats.maxEdgeAfter) stats.maxEdgeAfter = len;
            }

            outSpacing = S?.ToArray();
            return stats.splits > 0;
        }
    }
}
