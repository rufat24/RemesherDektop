// Whether the position field's integer lattice is integrable, measured per triangle.
//
// This is the instrument for the fault LatticeDirections localised. Its finding was that of 2,180
// vacant lattice directions in the output graph, 819 have a cluster at a plausible distance sitting
// more than 30 degrees off the lattice axis and 618 have nothing there at all — and that refitting
// each frame to its own ring moves the histogram by 13 vertices of 4,957, so the frames are sound
// and the shear is real. The lattice is locally converged and globally does not tile.
//
// "Does not tile" has an exact statement, and it is the one QuadriFlow (Huang et al. 2018) turns
// into a constraint. The position field gives each vertex a lattice point, and each EDGE an integer
// offset between its endpoints' cells — that is what CompatIndex returns and what LatticeCollapse
// classifies. Those offsets describe a genuine integer grid map only if they are curl-free: walk the
// three edges of a triangle and the offsets must sum to zero. When they do, the offsets integrate to
// a global integer coordinate per vertex and cells tile by construction. When they do not, the
// triangle is a point where the parameterisation contradicts itself, and no downstream pass can
// repair it, because there is no assignment of cells consistent with all three of its edges.
//
// Instant Meshes never checks this. Its extraction is local and greedy, which is exactly the
// difference the plan records between it and QuadriFlow: converged is not consistent, and a single
// local energy has no term that asks the cells to line up.
//
// Two things have to be got right before a residual per triangle means anything.
//
// FRAMES. The integer offset on an edge is stated in a basis, and each vertex has its own cross,
// so the offsets on the three edges of a triangle live in three different bases and cannot be added
// until they are transported into one. The transport is a multiple of 90 degrees, given by the same
// integer that SingularityAnalysis accumulates: Compat4Index's (k1 - k0).
//
// GAUGE. Those 90-degree transports are themselves a choice — a 4-RoSy cross has four equally valid
// representatives — so the raw per-edge rotations carry an arbitrary component that has nothing to
// do with the surface. Gauge fixing removes it: walk a spanning tree of the mesh and rotate each
// vertex's frame so every TREE edge transports trivially. What rotation survives afterwards is
// holonomy, which is real, and it concentrates on the non-tree edges — around singularities, where
// the cross field genuinely cannot be combed. A face whose three rotations are all zero after
// gauging has its residual as a plain sum in Z^2, which is what makes the repair two independent
// scalar flow problems rather than one problem over a rotating quantity.
//
// This file measures. What it measures decides whether the flow repair is worth building and how
// large it would be, and measuring first is the standing lesson of this project: two sessions were
// spent on causes a direct measurement settled in one run.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public sealed class IntegrationStats
    {
        public int faces;
        public int edges;

        /// <summary>Faces whose three transports do not compose to the identity. Real singularities.</summary>
        public int singularFaces;

        /// <summary>Faces where gauging left a non-trivial transport, so the residual is not a plain sum.</summary>
        /// <remarks>
        /// Gauge-dependent, and knowing that is the point of reporting it. A face's rotation SUM is
        /// the singularity index and cannot be gauged away; the individual rotations can, and a
        /// breadth-first tree from one root leaves them non-trivial on any non-tree edge whose loop
        /// back through the tree happens to enclose a singularity — which, far from the root, is
        /// most of them. So this count measures the tree, not the surface, and the residuals on
        /// these faces are reported alongside rather than folded in.
        /// </remarks>
        public int twistedFaces;

        /// <summary>Unsatisfied faces among the twisted ones, and their total flow.</summary>
        public int twistedUnsatisfied;
        public int twistedResidualTotal;

        /// <summary>Tree edges, and the non-tree edges left carrying holonomy. Both expected.</summary>
        public int treeEdges;
        public int holonomyEdges;

        /// <summary>Faces with a boundary edge, which have no closed loop to sum around.</summary>
        public int boundaryFaces;

        /// <summary>Faces the constraint applies to: not singular, not twisted, not on a boundary.</summary>
        public int constrainedFaces;

        /// <summary>Of those, the ones already summing to zero. Nothing to do here.</summary>
        public int satisfiedFaces;

        /// <summary>Residual magnitudes over the constrained faces, |x| + |y|, clamped into 0..4.</summary>
        public int[] residualHistogram = new int[5];

        /// <summary>Total |x| + |y| over the constrained faces — the flow the repair would have to route.</summary>
        public int residualTotal;

        /// <summary>Sum of signed residuals, x and y. Must be zero on a closed surface.</summary>
        public Vector2Int residualSum;

        /// <summary>Tree edges whose transport is not trivial after gauging. A bug if non-zero.</summary>
        public int gaugeFailures;

        public float satisfiedFraction =>
            constrainedFaces > 0 ? satisfiedFaces / (float)constrainedFaces : 0f;

        public override string ToString()
        {
            return $"faces={faces} singular={singularFaces} boundary={boundaryFaces} " +
                   $"flat={constrainedFaces} satisfied={satisfiedFaces} ({satisfiedFraction:P1}) " +
                   $"|res|=[{residualHistogram[0]} {residualHistogram[1]} {residualHistogram[2]} " +
                   $"{residualHistogram[3]} {residualHistogram[4]}] total={residualTotal} " +
                   $"sum=({residualSum.x},{residualSum.y}) | twisted={twistedFaces} " +
                   $"unsat={twistedUnsatisfied} total={twistedResidualTotal} " +
                   $"| tree={treeEdges} holonomy={holonomyEdges} gaugeFail={gaugeFailures}";
        }
    }

    /// <summary>
    /// Per-edge integer data for the lattice, in gauge-fixed frames. Indexed by half-edge, with the
    /// twin's entry derived from its partner's so antisymmetry holds by construction rather than by
    /// two independent calls to CompatIndex agreeing.
    /// </summary>
    public sealed class LatticeEdges
    {
        /// <summary>Offset from this half-edge's origin cell to its target cell, in the origin's frame.</summary>
        public Vector2Int[] jump;

        /// <summary>Transport from the origin's gauged frame to the target's, in quarter turns.</summary>
        public int[] rotation;

        /// <summary>Residual distance between the two lattice points, from CompatIndex. A confidence.</summary>
        public float[] error;

        /// <summary>Quarter turns applied to each vertex's cross to fix the gauge.</summary>
        public int[] gauge;

        /// <summary>The vertex each one was reached from in the gauge walk, or -1 at a root.</summary>
        public int[] treeParent;
    }

    public static class LatticeIntegration
    {
        /// <summary>Rotates an integer lattice coordinate by <paramref name="turns"/> quarter turns.</summary>
        /// <remarks>
        /// A quarter turn of the frame acts on coordinates as (x, y) -> (-y, x). Negative and
        /// out-of-range turn counts are normalised, which matters because the transports are
        /// composed and inverted freely below.
        /// </remarks>
        public static Vector2Int Rotate(Vector2Int v, int turns)
        {
            turns = ((turns % 4) + 4) % 4;
            for (var i = 0; i < turns; i++) v = new Vector2Int(-v.y, v.x);
            return v;
        }

        /// <summary>
        /// Builds the gauge-fixed per-edge integer data.
        /// </summary>
        public static LatticeEdges BuildEdges(RemeshTopology topology, OrientationField orientation,
            PositionField position, IntegrationStats stats = null)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (orientation == null) throw new ArgumentNullException(nameof(orientation));
            if (position == null) throw new ArgumentNullException(nameof(position));

            var count = topology.vertexCount;
            var halfEdges = topology.halfEdgeVertex.Length;
            var scale = position.scale;
            var invScale = 1f / scale;

            var p = topology.positions;
            var n = topology.normals;
            var q = orientation.directions;
            var o = position.positions;

            var result = new LatticeEdges
            {
                jump = new Vector2Int[halfEdges],
                rotation = new int[halfEdges],
                error = new float[halfEdges],
                gauge = new int[count],
                treeParent = new int[count],
            };

            // --- gauge fixing, by breadth-first spanning tree over the vertices ---
            //
            // The tree is walked with the raw rotations, and each vertex reached is rotated so the
            // edge it was reached by transports trivially. Every vertex therefore gets a frame
            // stated relative to its component's root, and the arbitrary per-vertex choice of
            // 4-RoSy representative is gone.
            var gauge = result.gauge;
            var visited = new bool[count];
            var queue = new Queue<int>();
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;

            for (var root = 0; root < count; root++)
            {
                if (visited[root]) continue;

                visited[root] = true;
                gauge[root] = 0;
                result.treeParent[root] = -1;
                queue.Enqueue(root);

                while (queue.Count > 0)
                {
                    var i = queue.Dequeue();

                    for (var k = start[i]; k < start[i + 1]; k++)
                    {
                        var j = adjacency[k];
                        if (visited[j]) continue;

                        SingularityAnalysis.Compat4Index(q[i], n[i], q[j], n[j], out var k0, out var k1);

                        // c_j = R^(k1 - k0) c_i on the raw frames; gauging by g adds (g_i - g_j),
                        // so g_j = r + g_i makes the tree edge transport trivially.
                        gauge[j] = (((k1 - k0 + gauge[i]) % 4) + 4) % 4;

                        result.treeParent[j] = i;
                        visited[j] = true;
                        queue.Enqueue(j);
                    }
                }
            }

            // --- per-edge jumps and rotations ---
            //
            // Computed once per undirected edge, at whichever half-edge has the lower index, and
            // the twin derived from it. Calling CompatIndex twice with the arguments swapped would
            // not give an antisymmetric pair: the sign fold in Compat4Index adds its two quarter
            // turns to whichever side is passed second, so the two calls disagree about the frame.

            for (var h = 0; h < halfEdges; h++)
            {
                var twin = topology.halfEdgeTwin[h];
                if (twin >= 0 && twin < h) continue;   // done from the other side

                var i = topology.halfEdgeVertex[h];
                var j = topology.HalfEdgeTarget(h);

                SingularityAnalysis.Compat4Index(q[i], n[i], q[j], n[j], out var k0, out var k1);

                // The aligned crosses, which is the basis CompatIndex's integers are stated in.
                var qi = q[i];
                for (var t = 0; t < (k0 & 3); t++) qi = OrientationField.Rotate90(qi, n[i]);

                var qj = q[j];
                for (var t = 0; t < (k1 & 3); t++) qj = OrientationField.Rotate90(qj, n[j]);

                PositionField.CompatIndex(
                    p[i], n[i], qi, o[i],
                    p[j], n[j], qj, o[j],
                    scale, invScale,
                    out var shift0, out var shift1, out var error);

                // shift brings each lattice point to the shared meeting point, so vertex i's cell
                // sits at -shift0 and j's at -shift1 relative to it; the offset from i to j is
                // shift0 - shift1. Stated in the aligned basis, which is i's raw frame turned k0
                // times, and i's gauged frame is its raw frame turned gauge[i] times — so the
                // coordinates move by (k0 - gauge[i]) quarter turns.
                var aligned = shift0 - shift1;
                var jump = Rotate(aligned, k0 - gauge[i]);

                // Transport on the gauged frames.
                var rotation = ((((k1 - k0 + gauge[i] - gauge[j]) % 4) + 4) % 4);

                result.jump[h] = jump;
                result.rotation[h] = rotation;
                result.error[h] = error;

                if (twin >= 0)
                {
                    // Antisymmetry, by construction: the reverse offset is the forward one
                    // transported into the far frame and negated.
                    result.jump[twin] = Rotate(-jump, rotation);
                    result.rotation[twin] = (4 - rotation) % 4;
                    result.error[twin] = error;
                }
            }

            // Every TREE edge must transport trivially now, and gaugeFailures counting anything
            // but zero means the gauge walk is wrong and every residual below is meaningless.
            // Non-tree edges are separate and are EXPECTED to carry holonomy — the loop each one
            // closes through the tree can enclose singularities — so they are counted apart.
            if (stats != null)
            {
                for (var i = 0; i < count; i++)
                {
                    for (var k = start[i]; k < start[i + 1]; k++)
                    {
                        var j = adjacency[k];
                        if (j <= i) continue;

                        SingularityAnalysis.Compat4Index(q[i], n[i], q[j], n[j], out var k0, out var k1);
                        var gauged = ((((k1 - k0 + gauge[i] - gauge[j]) % 4) + 4) % 4);

                        var onTree = result.treeParent[j] == i || result.treeParent[i] == j;
                        if (onTree)
                        {
                            stats.treeEdges++;
                            if (gauged != 0) stats.gaugeFailures++;
                        }
                        else if (gauged != 0)
                        {
                            stats.holonomyEdges++;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// The residual of one triangle: the sum of its three edge offsets, all transported into the
        /// frame of its first vertex. Zero exactly when the three edges agree about where the cells
        /// go.
        /// </summary>
        public static Vector2Int FaceResidual(RemeshTopology topology, LatticeEdges edges, int face,
            out int rotationSum, out bool hasBoundary)
        {
            var h0 = face * 3;
            var h1 = h0 + 1;
            var h2 = h0 + 2;

            hasBoundary = topology.halfEdgeTwin[h0] < 0
                       || topology.halfEdgeTwin[h1] < 0
                       || topology.halfEdgeTwin[h2] < 0;

            var r0 = edges.rotation[h0];
            var r1 = edges.rotation[h1];
            var r2 = edges.rotation[h2];
            rotationSum = ((r0 + r1 + r2) % 4 + 4) % 4;

            // h0 is already in v0's frame. h1 is in v1's, and v1 -> v0 is the inverse of r0. h2 is
            // in v2's, and v2 -> v0 is the inverse of r0 then r1 composed.
            return edges.jump[h0]
                 + Rotate(edges.jump[h1], -r0)
                 + Rotate(edges.jump[h2], -(r0 + r1));
        }

        public static IntegrationStats Measure(RemeshTopology topology, OrientationField orientation,
            PositionField position, out LatticeEdges edges)
        {
            var stats = new IntegrationStats
            {
                faces = topology.triangleCount,
                edges = topology.halfEdgeVertex.Length,
            };

            edges = BuildEdges(topology, orientation, position, stats);

            for (var f = 0; f < topology.triangleCount; f++)
            {
                var residual = FaceResidual(topology, edges, f, out var rotationSum, out var hasBoundary);

                if (rotationSum != 0) { stats.singularFaces++; continue; }
                if (hasBoundary) { stats.boundaryFaces++; continue; }

                var magnitudeHere = Mathf.Abs(residual.x) + Mathf.Abs(residual.y);

                // Non-zero rotations that nevertheless compose to the identity. The residual is
                // still well defined — reported here — but it is not a plain sum of the three
                // offsets, so changing one of them moves the two incident faces by amounts related
                // through a rotation. That is a flow over a quantity that turns as it travels
                // rather than two scalar flows, so these are held out of the repair rather than
                // fudged, and their share of the total is exactly what that costs.
                if (edges.rotation[f * 3] != 0 || edges.rotation[f * 3 + 1] != 0 ||
                    edges.rotation[f * 3 + 2] != 0)
                {
                    stats.twistedFaces++;
                    stats.twistedResidualTotal += magnitudeHere;
                    if (magnitudeHere != 0) stats.twistedUnsatisfied++;
                    continue;
                }

                stats.constrainedFaces++;
                stats.residualSum += residual;

                stats.residualTotal += magnitudeHere;
                stats.residualHistogram[Mathf.Min(magnitudeHere, 4)]++;

                if (magnitudeHere == 0) stats.satisfiedFaces++;
            }

            return stats;
        }
    }
}
