// Split the input edges the lattice cannot represent, instead of throwing them away.
//
// THE DEFECT THIS FIXES. LatticeCollapse classifies every input edge on the integer lattice and
// returns one of three answers: same cell (collapse), one step apart (an output edge), or
// two-or-more cells apart / diagonal (discard). The discard is the problem. An edge whose endpoints
// land two cells apart means there is a lattice cell BETWEEN them that no input vertex claims — the
// requested lattice is finer than the input mesh in that neighbourhood — and dropping the edge
// destroys the only record that those two vertices are adjacent on the surface.
//
// Measured on the reference scan: 8,054 of 82,110 edges discarded. Nothing downstream can rebuild
// an edge that was never emitted, so the loss propagates the whole way out:
//
//   discarded edges  ->  holes in the output graph
//                    ->  951 boundary edges survive tracing
//                    ->  the hole filler adds 226 faces, valence-7 hubs and 195 invented vertices
//                    ->  the n-gon splitter turns 344 n-gons into 183 triangles
//                    ->  671 triangles, valence-1 vertices, 17-degree sliver quads
//
// That chain is why several sessions of graph repair moved nothing: every one of them was editing
// the symptoms of data already thrown out.
//
// WHAT THE REFERENCE DOES. QuadriFlow's ComputeIndexMap (Huang et al. 2018, MIT) does not discard.
// Step 5, `subdivide_edgeDiff`, SUBDIVIDES the offending edge until its integer difference is
// representable. This is that step.
//
// WHY MIDPOINTS, AND WHY IN ROUNDS. Splitting an edge at its midpoint at most halves its lattice
// difference, so one round is not always enough; the loop re-measures and repeats. Rounds terminate
// because the difference is bounded below by 1 and halves each time — in practice three rounds
// clear everything, and the cap exists only so a pathological field cannot spin.
//
// WHAT IS DELIBERATELY *NOT* SPLIT. The (1,1) diagonal. Two vertices in diagonally adjacent cells
// is a correct, expected state on a triangulated input — when one cell is covered by two triangles,
// their shared edge IS that cell's diagonal — and a quad lattice has no edge along a diagonal.
// Diagonals are 8.2% of scan edges against 1.6% for genuine jumps, so conflating the two would
// subdivide five times more than necessary for no benefit. This is the distinction
// FieldDiagnostics.discardDiagonalFraction / discardJumpFraction was added to measure.
//
// THE FIELD ON NEW VERTICES IS INTERPOLATED, NOT SOLVED, and the interpolation is not naive:
// two crosses 90 degrees apart average to zero, and two lattice points in different frames cannot
// be averaged at all. Both are handled by aligning representatives first — Compat4 for the cross,
// RoundToLattice for the lattice point — which is the same discipline the solvers use internally.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class SubdivideStats
    {
        public int inputVertices;
        public int inputTriangles;
        public int outputVertices;
        public int outputTriangles;

        /// <summary>Rounds actually run. Three is typical; the cap is <see cref="EdgeSubdivide.maxRounds"/>.</summary>
        public int rounds;

        /// <summary>Edges split, total across rounds.</summary>
        public int edgesSplit;

        /// <summary>Vertices inserted. Equals <see cref="edgesSplit"/> — one per split edge.</summary>
        public int verticesInserted;

        /// <summary>
        /// Edges still spanning two or more cells when the round cap was hit. Non-zero means the
        /// cap bound the result rather than convergence, and the discard is back for those edges.
        /// This is the counter that says whether the pass finished its job.
        /// </summary>
        public int unresolvedEdges;

        /// <summary>Split edges per round. A rising sequence means the refinement is diverging.</summary>
        public readonly List<int> perRound = new List<int>();

        /// <summary>
        /// Set when the pass gave up and returned the input unchanged. Non-null means the refinement
        /// was diverging or too expensive, and shipping half of it would have been worse than
        /// shipping none — see <see cref="EdgeSubdivide"/>'s note on cascading.
        /// </summary>
        public string abandoned;

        public long totalMs;

        public override string ToString()
        {
            return $"subdiv:   verts={inputVertices}->{outputVertices} " +
                   $"tris={inputTriangles}->{outputTriangles} rounds={rounds} " +
                   $"split={edgesSplit} perRound=[{string.Join(",", perRound)}] " +
                   $"unresolved={unresolvedEdges}" +
                   (abandoned != null ? $" ABANDONED({abandoned})" : "") +
                   $" {totalMs}ms";
        }
    }

    public static class EdgeSubdivide
    {
        /// <summary>
        /// Round cap. A midpoint split at most halves an edge's lattice difference, so clearing a
        /// difference of k needs ceil(log2 k) rounds; the largest difference ever measured on this
        /// corpus is 3, so this is slack rather than a tuning knob.
        /// </summary>
        public const int maxRounds = 6;

        /// <summary>
        /// Vertex-count growth beyond which the refinement is abandoned.
        /// </summary>
        /// <remarks>
        /// Measured: `cylinder_capped` converges in ONE round for 4.3% more vertices and drops
        /// worst-case deviation 34-fold; `cube_subdiv` splits 17,823 edges over six rounds for 178%
        /// more vertices, ends with 741 edges still unrepresentable, and makes the output worse
        /// (148 irregular vertices to 488, quad fraction 0.99 to 0.80). A cap this far above the
        /// converging case cannot penalise it, and the diverging case trips it long before it can
        /// do damage.
        /// </remarks>
        public const float maxVertexGrowth = 1.5f;

        /// <summary>
        /// Splits every input edge whose endpoints land two or more lattice cells apart, until none
        /// remain or <see cref="maxRounds"/> is reached, and returns the refined topology and the
        /// fields interpolated onto it. Does not modify its arguments.
        /// </summary>
        /// <remarks>
        /// Returns the ORIGINAL objects untouched when nothing needs splitting, so the common case
        /// costs one measurement pass and no allocation.
        /// </remarks>
        public static void Run(RemeshTopology topology, OrientationField orientation,
            PositionField position, out RemeshTopology refinedTopology,
            out OrientationField refinedOrientation, out PositionField refinedPosition,
            out SubdivideStats stats)
        {
            Run(topology, orientation, position, null, out refinedTopology, out refinedOrientation,
                out refinedPosition, out _, out stats);
        }

        /// <summary>
        /// As above, also carrying <paramref name="features"/> onto the refined vertices.
        /// </summary>
        /// <remarks>
        /// The feature graph MUST travel with the mesh. Every array in it is indexed by vertex, and
        /// extraction reads <c>isFeatureVertex[i]</c> for refined indices — so handing over the
        /// original graph throws an IndexOutOfRangeException on any mesh with a crease. That is how
        /// this overload came to exist: three of the four audit cases died on it.
        /// </remarks>
        public static void Run(RemeshTopology topology, OrientationField orientation,
            PositionField position, FeatureGraph features, out RemeshTopology refinedTopology,
            out OrientationField refinedOrientation, out PositionField refinedPosition,
            out FeatureGraph refinedFeatures, out SubdivideStats stats)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (orientation == null) throw new ArgumentNullException(nameof(orientation));
            if (position == null) throw new ArgumentNullException(nameof(position));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats = new SubdivideStats
            {
                inputVertices = topology.vertexCount,
                inputTriangles = topology.triangleCount,
            };

            var scale = position.scale;
            var invScale = 1f / scale;

            // The rebuild is INSIDE the loop, deliberately, and this was found by a test rather
            // than by reasoning. RemeshTopology.Build recomputes area-weighted normals, so the
            // frames after a rebuild are not the frames the splits were measured in: a cross has to
            // be re-projected onto its vertex's new normal, which rotates it slightly, which moves
            // the integer indices. Measuring on the pre-rebuild arrays and rebuilding once at the
            // end left six edges still spanning two cells while the pass reported success. Every
            // round now measures the topology it will actually hand on.
            refinedTopology = topology;
            refinedOrientation = orientation;
            refinedPosition = position;
            refinedFeatures = features;

            for (var round = 0; round < maxRounds; round++)
            {
                var p = new List<Vector3>(refinedTopology.positions);
                var n = new List<Vector3>(refinedTopology.normals);
                var q = new List<Vector3>(refinedOrientation.directions);
                var o = new List<Vector3>(refinedPosition.positions);
                var s = refinedPosition.spacing != null
                    ? new List<float>(refinedPosition.spacing)
                    : null;
                var tris = refinedTopology.triangles;

                var midpoint = new Dictionary<long, int>(EdgeKey.Comparer);
                var splits = new List<(int a, int b, int mid)>();

                // --- measure: which edges cannot be represented ---
                for (var t = 0; t + 2 < tris.Length; t += 3)
                {
                    for (var e = 0; e < 3; e++)
                    {
                        var i = tris[t + e];
                        var j = tris[t + (e + 1) % 3];
                        var key = EdgeKey.PackUndirected(i, j);
                        if (midpoint.ContainsKey(key)) continue;

                        if (Steps(p, n, q, o, s, i, j, scale, invScale) < 2) continue;

                        var mid = Insert(p, n, q, o, s, i, j, scale, invScale);
                        midpoint[key] = mid;
                        splits.Add((i, j, mid));
                    }
                }

                if (midpoint.Count == 0) break;

                // --- divergence guard ---
                //
                // A midpoint split does NOT simply halve the problem, and this is the thing that
                // had to be measured rather than assumed. Re-triangulating a split triangle creates
                // a new edge from the midpoint to the opposite vertex, and that edge's lattice
                // difference is unconstrained — it can itself be unrepresentable. Where the field is
                // locally inconsistent the effect compounds: `cube_subdiv` splits
                // [1513, 3625, 6731, ...], rising for three rounds before decaying, and finishes
                // with 741 edges still unrepresentable and a materially worse mesh.
                //
                // So the pass refuses to ship a partial refinement. Half a subdivision is worse
                // than none: it pays the vertex cost, adds the new unconstrained edges, and does
                // not remove the discards it was meant to remove.
                var diverging = stats.perRound.Count > 0 &&
                                midpoint.Count > stats.perRound[stats.perRound.Count - 1];
                var tooLarge = p.Count > stats.inputVertices * maxVertexGrowth;

                if (diverging || tooLarge)
                {
                    stats.abandoned = diverging
                        ? $"diverging at round {round + 1}: {midpoint.Count} splits after " +
                          $"{stats.perRound[stats.perRound.Count - 1]}"
                        : $"growth {p.Count / (float)stats.inputVertices:F2}x exceeds {maxVertexGrowth}x";

                    refinedTopology = topology;
                    refinedOrientation = orientation;
                    refinedPosition = position;
                    refinedFeatures = features;
                    break;
                }

                stats.rounds = round + 1;
                stats.edgesSplit += midpoint.Count;
                stats.verticesInserted += midpoint.Count;
                stats.perRound.Add(midpoint.Count);

                // --- re-triangulate every triangle touching a split edge ---
                var next = new List<int>(tris.Length + midpoint.Count * 6);
                for (var t = 0; t + 2 < tris.Length; t += 3)
                    Retriangulate(tris[t], tris[t + 1], tris[t + 2], midpoint, next);

                Rebuild(p, q, o, s, next, position, orientation,
                    out refinedTopology, out refinedOrientation, out refinedPosition,
                    out var remap);

                if (refinedFeatures != null)
                    refinedFeatures = refinedFeatures.WithSplitEdges(
                        refinedTopology.vertexCount, remap, splits);
            }

            // Always measured, against what is being returned — not only when the cap was hit. The
            // earlier version reported zero whenever the loop exited early, which is exactly the
            // case where a stale-frame residue hides.
            stats.unresolvedEdges = CountUnrepresentable(
                refinedTopology, refinedOrientation, refinedPosition);

            stats.outputVertices = refinedTopology.vertexCount;
            stats.outputTriangles = refinedTopology.triangleCount;
            stats.totalMs = watch.ElapsedMilliseconds;
        }

        /// <summary>
        /// Rebuilds the topology from the refined soup and carries the fields onto it.
        /// </summary>
        static void Rebuild(List<Vector3> p, List<Vector3> q, List<Vector3> o, List<float> s,
            List<int> tris,
            PositionField position, OrientationField orientation,
            out RemeshTopology topology, out OrientationField refinedOrientation,
            out PositionField refinedPosition, out int[] soupToWelded)
        {
            // Build welds, so the field arrays travel through its remap rather than being handed
            // over positionally. Every vertex here is already distinct, so in practice the remap is
            // the identity; relying on that instead of applying it would be a silent bug the day an
            // input arrives with two coincident vertices.
            var positions = p.ToArray();
            topology = RemeshTopology.Build(positions, tris.ToArray());

            var welded = topology.vertexCount;
            var remap = topology.sourceToWelded;

            // Materialise the remap even when Build did not weld, so callers never have to branch
            // on null — a null-means-identity contract is how an index bug like the feature-graph
            // one gets written in the first place.
            if (remap == null)
            {
                remap = new int[positions.Length];
                for (var i = 0; i < remap.Length; i++) remap[i] = i;
            }
            soupToWelded = remap;

            var directions = new Vector3[welded];
            var lattice = new Vector3[welded];
            var spacing = s != null ? new float[welded] : null;

            for (var source = 0; source < positions.Length; source++)
            {
                var target = remap[source];
                if (target < 0 || target >= welded) continue;
                directions[target] = q[source];
                lattice[target] = o[source];
                if (spacing != null) spacing[target] = s[source];
            }

            // Re-project onto the rebuilt normals: a cross that is not in its vertex's tangent
            // plane makes Rotate90 return something not perpendicular to it, which quietly
            // corrupts every lattice index computed downstream.
            var normals = topology.normals;
            for (var v = 0; v < welded; v++)
            {
                var tangent = directions[v] - normals[v] * Vector3.Dot(normals[v], directions[v]);
                directions[v] = MeshMath.SafeNormalize(tangent, AnyTangent(normals[v]));
            }

            refinedOrientation = orientation.OnRefinedVertices(directions);
            refinedPosition = position.OnRefinedVertices(lattice, spacing);
        }

        /// <summary>Edges still spanning two or more lattice cells. Zero is the postcondition.</summary>
        static int CountUnrepresentable(RemeshTopology topology, OrientationField orientation,
            PositionField position)
        {
            var scale = position.scale;
            var invScale = 1f / scale;
            var p = new List<Vector3>(topology.positions);
            var n = new List<Vector3>(topology.normals);
            var q = new List<Vector3>(orientation.directions);
            var o = new List<Vector3>(position.positions);
            var s = position.spacing != null ? new List<float>(position.spacing) : null;

            var count = 0;
            for (var i = 0; i < topology.vertexCount; i++)
            for (var k = topology.adjacencyStart[i]; k < topology.adjacencyStart[i + 1]; k++)
            {
                var j = topology.adjacency[k];
                if (j <= i) continue;
                if (Steps(p, n, q, o, s, i, j, scale, invScale) >= 2) count++;
            }
            return count;
        }

        /// <summary>
        /// How many lattice cells apart the two endpoints land, as
        /// max(|dx|, |dy|) — the same quantity <see cref="PositionField.Classify"/> thresholds.
        /// Returns 1 for the (1,1) diagonal, which must not be split. See the header.
        /// </summary>
        static int Steps(List<Vector3> p, List<Vector3> n, List<Vector3> q, List<Vector3> o,
            List<float> s, int i, int j, float scale, float invScale)
        {
            // Each endpoint in its own spacing. Under grading, measuring both against one mean
            // would report a fine-region edge as spanning several cells and split it repeatedly
            // against a lattice that never asked for it.
            var si = s != null ? s[i] : scale;
            var sj = s != null ? s[j] : scale;

            OrientationField.Compat4(q[i], n[i], q[j], n[j], out var qi, out var qj);
            PositionField.CompatIndex(
                p[i], n[i], qi, o[i],
                p[j], n[j], qj, o[j],
                si, 1f / si, sj, 1f / sj,
                out var shift0, out var shift1, out _);

            var dx = Mathf.Abs(shift0.x - shift1.x);
            var dy = Mathf.Abs(shift0.y - shift1.y);
            return Mathf.Max(dx, dy);
        }

        /// <summary>Adds the midpoint vertex for edge (i, j) and returns its index.</summary>
        static int Insert(List<Vector3> p, List<Vector3> n, List<Vector3> q, List<Vector3> o,
            List<float> s, int i, int j, float scale, float invScale)
        {
            var pm = 0.5f * (p[i] + p[j]);

            // Two nearly opposite normals sum to nothing; fall back to one side rather than to a
            // zero vector, which Vector3.normalized would hand back silently.
            var nm = MeshMath.SafeNormalize(n[i] + n[j], n[i]);

            // Two crosses 90 degrees apart average to zero, so align representatives first. Then
            // re-project onto the interpolated normal, because the average of two tangent vectors
            // from different tangent planes is in neither.
            OrientationField.Compat4(q[i], n[i], q[j], n[j], out var qi, out var qj);
            var qm = qi + qj;
            qm -= nm * Vector3.Dot(nm, qm);
            qm = MeshMath.SafeNormalize(qm, AnyTangent(nm));

            // The lattice point cannot be averaged across frames either. Take one endpoint's
            // lattice and translate it by whole steps of the NEW frame into the half-cell box
            // around the new vertex — exactly what a solver sweep would have anchored it to.
            // The midpoint's own spacing, so the anchoring snap below lands it in the cell it
            // will actually be measured against. The mean rather than either endpoint's: it sits
            // between them and gradation guarantees the two are within 1.3x, so the mean is inside
            // the bound whichever neighbour it is later compared to.
            var sm = s != null ? 0.5f * (s[i] + s[j]) : scale;
            var om = PositionField.RoundToLattice(o[i], qm, nm, pm, sm, 1f / sm);

            p.Add(pm);
            n.Add(nm);
            q.Add(qm);
            o.Add(om);
            s?.Add(sm);
            return p.Count - 1;
        }

        /// <summary>Any unit vector perpendicular to <paramref name="normal"/>.</summary>
        static Vector3 AnyTangent(Vector3 normal)
        {
            var axis = Mathf.Abs(normal.x) < 0.9f ? Vector3.right : Vector3.up;
            return MeshMath.SafeNormalize(Vector3.Cross(normal, axis), Vector3.right);
        }

        /// <summary>
        /// Emits the triangle (a, b, c) split according to which of its edges have midpoints,
        /// preserving winding. One, two or three split edges give two, three or four triangles.
        /// </summary>
        static void Retriangulate(int a, int b, int c, Dictionary<long, int> midpoint, List<int> outTris)
        {
            var m0 = Lookup(midpoint, a, b);
            var m1 = Lookup(midpoint, b, c);
            var m2 = Lookup(midpoint, c, a);

            var count = (m0 >= 0 ? 1 : 0) + (m1 >= 0 ? 1 : 0) + (m2 >= 0 ? 1 : 0);

            // Rotating (a,b,c) -> (b,c,a) maps (m0,m1,m2) -> (m1,m2,m0) and preserves winding, so
            // the one- and two-split cases only need to be written once, in a canonical position.
            if (count == 1)
            {
                while (m0 < 0)
                {
                    Rotate(ref a, ref b, ref c);
                    Rotate(ref m0, ref m1, ref m2);
                }
                Emit(outTris, a, m0, c);
                Emit(outTris, m0, b, c);
                return;
            }

            if (count == 2)
            {
                while (!(m0 >= 0 && m1 >= 0))
                {
                    Rotate(ref a, ref b, ref c);
                    Rotate(ref m0, ref m1, ref m2);
                }
                Emit(outTris, m0, b, m1);
                Emit(outTris, a, m0, m1);
                Emit(outTris, a, m1, c);
                return;
            }

            if (count == 3)
            {
                Emit(outTris, a, m0, m2);
                Emit(outTris, m0, b, m1);
                Emit(outTris, m2, m1, c);
                Emit(outTris, m0, m1, m2);
                return;
            }

            Emit(outTris, a, b, c);
        }

        static void Rotate(ref int x, ref int y, ref int z)
        {
            var t = x;
            x = y;
            y = z;
            z = t;
        }

        static int Lookup(Dictionary<long, int> midpoint, int a, int b)
            => midpoint.TryGetValue(EdgeKey.PackUndirected(a, b), out var m) ? m : -1;

        static void Emit(List<int> tris, int a, int b, int c)
        {
            if (a == b || b == c || c == a) return;   // a split can never create one; cheap guard
            tris.Add(a);
            tris.Add(b);
            tris.Add(c);
        }
    }
}
