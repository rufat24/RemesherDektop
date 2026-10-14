// Native quad remesher for Unity — Stage 4: the position field.
//
// From Jakob et al., "Instant Field-Aligned Meshes" (SIGGRAPH Asia 2015), §4.
//
// The orientation field decided which way the quads run. The position field decides where their
// corners land. Each vertex carries a point o_i that is snapped onto a local quad lattice —
// spacing `scale`, axes given by the cross field. Smoothing pulls neighbouring vertices onto the
// *same* lattice, so that after convergence many vertices share a lattice point. Those shared
// points are the corners of the output quads, which is what stage 5 then harvests.
//
// The symmetry here is translational rather than rotational: o and o + scale*(a*q + b*t) describe
// the same lattice for any integers a, b. As with the 4-RoSy field, every comparison between two
// vertices has to respect that or the field fights itself.

using System;
using UnityEngine;

namespace NativeRemesher
{
    public sealed class PositionField
    {
        /// <summary>Lattice position per vertex, lying in that vertex's tangent plane.</summary>
        public Vector3[] positions;

        /// <summary>
        /// Lattice spacing — the target quad edge length. Under a graded field this is the
        /// area-weighted mean, and every consumer that needs to be exact about one vertex must read
        /// <see cref="spacing"/> instead.
        /// </summary>
        public float scale { get; private set; }

        /// <summary>
        /// Per-vertex lattice spacing, or null when the field is uniform at <see cref="scale"/>.
        /// </summary>
        /// <remarks>
        /// Null rather than a filled array for the uniform case on purpose: a consumer that has not
        /// been taught about grading reads <see cref="scale"/> and is correct, and one that has
        /// reads <see cref="SpacingAt"/> and is correct either way. A uniform field represented as
        /// an array of equal values would let an untaught consumer silently keep using the mean on
        /// a graded field without anything distinguishing the two cases.
        /// </remarks>
        public float[] spacing { get; private set; }

        public bool isGraded => spacing != null;

        /// <summary>
        /// Per triangle corner (3f + k), the lattice node of that corner in its face's chart, when
        /// the lattice came from a global parametrisation; null for a position field. The integer
        /// grid map then reads its edge offsets from these instead of rounding each edge on its
        /// own. See Extraction.SeamlessParametrization.
        /// </summary>
        public Vector2Int[] cornerNodes;

        /// <summary>Per corner, the chart coordinates the nodes were rounded from.</summary>
        public Vector2[] cornerUv;

        /// <summary>
        /// Per corner, the same coordinates in double precision, (u, v) interleaved -- exact enough
        /// that a pinned crease's vertices sit on their grid line. What
        /// Extraction.ParametrizationTrace reads.
        /// </summary>
        public double[] cornerUvExact;

        /// <summary>
        /// With IntegerGridMap.parametrizationLayout, the quantised layout's quads (see
        /// Extraction.QuantizedLayout), extracted instead of the lattice.
        /// </summary>
        public Vector3[] layoutPositions, layoutNormals;
        public System.Collections.Generic.List<int[]> layoutFaces;

        /// <summary>Per face, the chart's u direction in space.</summary>
        public Vector3[] faceUAxis;

        /// <summary>
        /// Per corner, the quarter-turns from the vertex's cross to its face chart, or -1 where
        /// the parametrisation could not say (a second fan component).
        /// </summary>
        public int[] cornerRank;

        /// <summary>Faces the parametrisation's singularities sit in, for the grid map.</summary>
        public int[] singularFaces;

        /// <summary>Lattice spacing at one vertex, whether or not the field is graded.</summary>
        public float SpacingAt(int vertex)
        {
            return spacing != null && vertex >= 0 && vertex < spacing.Length
                ? spacing[vertex]
                : scale;
        }

        /// <summary>
        /// The same field carried onto a refined vertex set, keeping <see cref="scale"/>.
        /// </summary>
        /// <remarks>
        /// Exists for <see cref="Extraction.EdgeSubdivide"/>, which splits input edges the lattice
        /// cannot represent and so has to hand extraction a field over more vertices than were
        /// solved. The convergence counters are deliberately NOT copied: they describe a solve that
        /// did not happen over these vertices, and carrying them would let a diagnostic claim a
        /// residual for a field that was interpolated rather than solved.
        /// </remarks>
        public PositionField OnRefinedVertices(Vector3[] refinedPositions)
        {
            return OnRefinedVertices(refinedPositions, null);
        }

        /// <summary>
        /// The same field carried onto a refined vertex set, with the spacing for those vertices.
        /// </summary>
        /// <param name="refinedSpacing">
        /// Per-refined-vertex spacing, or null to leave the refined field uniform.
        /// </param>
        /// <remarks>
        /// The spacing has to be supplied rather than inherited, and this is not a convenience
        /// overload. `spacing` is indexed by solved vertex and the refined set has more of them, so
        /// carrying the old array forward would be silently wrong for every vertex the subdivider
        /// added — and dropping it is equally wrong in a different direction, because extraction
        /// then classifies every edge against the MEAN and the grading is lost after the one pass
        /// that is meant to preserve it. The subdivider knows what a midpoint's spacing should be;
        /// nothing here does.
        /// </remarks>
        public PositionField OnRefinedVertices(Vector3[] refinedPositions, float[] refinedSpacing)
        {
            if (refinedPositions == null) throw new ArgumentNullException(nameof(refinedPositions));
            if (refinedSpacing != null && refinedSpacing.Length != refinedPositions.Length)
                throw new ArgumentException("Spacing and positions must agree in length.",
                    nameof(refinedSpacing));

            return new PositionField
            {
                positions = refinedPositions,
                scale = scale,
                spacing = refinedSpacing
            };
        }

        public int iterationsRun { get; private set; }

        /// <summary>
        /// Mean per-sweep movement of the lattice representatives, in lattice units.
        /// </summary>
        /// <remarks>
        /// KEPT ONLY FOR CONTINUITY WITH THE ORIGINAL MEASUREMENTS, AND IT IS MISLEADING.
        /// The unknown this solver actually determines is not o_i but the *coset*
        /// o_i + scale*(a*q_i + b*t_i) for integer a, b. The anchoring snap at the end of each
        /// sweep re-picks whichever representative of that coset lies nearest the vertex, so this
        /// number is dominated by representative re-selection — a gauge choice — rather than by
        /// any change in the lattice itself. It is bounded below by nothing and plateaus at a
        /// value with no interpretation. A plateau here was previously read as proof that this
        /// stage oscillated; it is not. The reference implementation does not compute a position
        /// residual at all, and runs a fixed sweep count.
        /// Judge convergence by <see cref="representativeDrift"/> instead.
        /// </remarks>
        public float lastResidual { get; private set; }

        /// <summary>
        /// Mean per-sweep movement measured *modulo the lattice*, in lattice units — the honest
        /// convergence signal. Zero means every vertex's coset is unchanged even if its
        /// representative jumped a whole cell. This is the translational analogue of the trick
        /// <see cref="OrientationField"/> already uses to measure movement under 4-fold symmetry.
        /// </summary>
        public float representativeDrift { get; private set; }

        /// <summary>
        /// A point between two vertices that respects both their tangent planes, rather than the
        /// naive midpoint which would sink beneath a curved surface.
        /// </summary>
        public static Vector3 MiddlePoint(Vector3 p0, Vector3 n0, Vector3 p1, Vector3 n1)
        {
            float n0p0 = Vector3.Dot(n0, p0), n0p1 = Vector3.Dot(n0, p1);
            float n1p0 = Vector3.Dot(n1, p0), n1p1 = Vector3.Dot(n1, p1);
            float n0n1 = Vector3.Dot(n0, n1);

            var denom = 1f / (1f - n0n1 * n0n1 + 1e-4f);
            var lambda0 = 2f * (n0p1 - n0p0 - n0n1 * (n1p0 - n1p1)) * denom;
            var lambda1 = 2f * (n1p0 - n1p1 - n0n1 * (n0p1 - n0p0)) * denom;

            return 0.5f * (p0 + p1) - 0.25f * (n0 * lambda0 + n1 * lambda1);
        }

        /// <summary>
        /// Moves <paramref name="o"/> by whole lattice steps to the cell nearest
        /// <paramref name="target"/>. This is the translational equivalent of picking a
        /// representative in the 4-RoSy field.
        /// </summary>
        public static Vector3 RoundToLattice(Vector3 o, Vector3 q, Vector3 n, Vector3 target,
            float scale, float invScale)
        {
            var t = OrientationField.Rotate90(q, n);
            var d = target - o;

            return o
                   + q * (Mathf.Round(Vector3.Dot(q, d) * invScale) * scale)
                   + t * (Mathf.Round(Vector3.Dot(t, d) * invScale) * scale);
        }

        /// <summary>
        /// Moves <paramref name="o"/> by whole lattice steps to the cell whose corner is at or
        /// below <paramref name="target"/> on both axes — the floor, not the nearest cell.
        /// Counterpart of the reference's <c>position_floor_4</c>.
        /// </summary>
        public static Vector3 FloorToLattice(Vector3 o, Vector3 q, Vector3 n, Vector3 target,
            float scale, float invScale)
        {
            var t = OrientationField.Rotate90(q, n);
            var d = target - o;

            return o
                   + q * (Mathf.Floor(Vector3.Dot(q, d) * invScale) * scale)
                   + t * (Mathf.Floor(Vector3.Dot(t, d) * invScale) * scale);
        }

        /// <summary>
        /// The integer lattice coordinates of the floored cell, rather than its position.
        /// Counterpart of the reference's <c>position_floor_index_4</c>.
        /// </summary>
        public static Vector2Int FloorIndex(Vector3 o, Vector3 q, Vector3 n, Vector3 target,
            float invScale)
        {
            var t = OrientationField.Rotate90(q, n);
            var d = target - o;

            return new Vector2Int(
                Mathf.FloorToInt(Vector3.Dot(q, d) * invScale),
                Mathf.FloorToInt(Vector3.Dot(t, d) * invScale));
        }

        /// <summary>
        /// Brings two vertices' lattice positions into agreement, so they can be averaged
        /// meaningfully.
        /// </summary>
        /// <remarks>
        /// Not independent rounding. Each side is *floored* toward the midpoint, which leaves four
        /// candidate cells per side, and the 4x4 pairs are then searched for the one minimising the
        /// distance between the two. Rounding each side to its own nearest cell — which is what
        /// this function used to do — optimises the wrong thing: it minimises each point's distance
        /// to the midpoint separately, and can leave the pair up to a full cell apart when a
        /// different pair of cells would have put them on top of each other. The whole purpose of
        /// this function is to make neighbouring lattices agree, so the joint minimum is the only
        /// correct choice. Matches the reference's <c>compat_position_extrinsic_4</c>.
        /// </remarks>
        public static void Compat(
            Vector3 p0, Vector3 n0, Vector3 q0, Vector3 o0,
            Vector3 p1, Vector3 n1, Vector3 q1, Vector3 o1,
            float scale, float invScale,
            out Vector3 a, out Vector3 b)
        {
            Compat(p0, n0, q0, o0, p1, n1, q1, o1,
                scale, invScale, scale, invScale, out a, out b);
        }

        /// <summary>
        /// <see cref="Compat"/> with a different lattice spacing on each side, for a graded field.
        /// </summary>
        /// <remarks>
        /// Each side floors and steps in ITS OWN spacing. That is the whole of what grading means
        /// here: the two vertices no longer share a lattice, so the search is for the pair of cells
        /// — one from each of two differently-sized lattices — that land closest together. The
        /// reference carries the same per-side scale_x/scale_y through its own signature.
        ///
        /// Because the spacings differ, the two cells can never coincide exactly the way they can
        /// in the uniform case, and the residual left over is real rather than a convergence
        /// failure. It is bounded by the difference in spacing, which is why gradation clamping is
        /// a correctness requirement and not a quality setting: unbounded, this residual grows
        /// until neighbouring cells cannot agree at all.
        /// </remarks>
        public static void Compat(
            Vector3 p0, Vector3 n0, Vector3 q0, Vector3 o0,
            Vector3 p1, Vector3 n1, Vector3 q1, Vector3 o1,
            float scale0, float invScale0, float scale1, float invScale1,
            out Vector3 a, out Vector3 b)
        {
            var t0 = OrientationField.Rotate90(q0, n0);
            var t1 = OrientationField.Rotate90(q1, n1);
            var middle = MiddlePoint(p0, n0, p1, n1);

            var o0p = FloorToLattice(o0, q0, n0, middle, scale0, invScale0);
            var o1p = FloorToLattice(o1, q1, n1, middle, scale1, invScale1);

            var bestCost = float.PositiveInfinity;
            int bestI = 0, bestJ = 0;

            for (var i = 0; i < 4; i++)
            {
                var o0t = o0p + (q0 * (i & 1) + t0 * ((i & 2) >> 1)) * scale0;
                for (var j = 0; j < 4; j++)
                {
                    var o1t = o1p + (q1 * (j & 1) + t1 * ((j & 2) >> 1)) * scale1;
                    var cost = (o0t - o1t).sqrMagnitude;

                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestI = i;
                        bestJ = j;
                    }
                }
            }

            a = o0p + (q0 * (bestI & 1) + t0 * ((bestI & 2) >> 1)) * scale0;
            b = o1p + (q1 * (bestJ & 1) + t1 * ((bestJ & 2) >> 1)) * scale1;
        }

        /// <summary>
        /// The same joint minimisation as <see cref="Compat"/>, but returning the two lattice
        /// points as *integer coordinates* plus the squared residual between them.
        /// </summary>
        /// <remarks>
        /// This is the primitive the extraction stage was missing, and its absence is the main
        /// reason the extractor produced almost no quads. Given the two integer coordinates, the
        /// difference tells you what the edge between the vertices actually is:
        /// <list type="bullet">
        /// <item>both components zero — the two vertices sit on the *same* lattice point, so the
        /// edge should be collapsed;</item>
        /// <item>exactly one lattice step apart (max component 1, and not the (1,1) diagonal) —
        /// this is a genuine edge of the output quad mesh;</item>
        /// <item>anything else — two or more cells apart, or a diagonal — and the link carries no
        /// meaning and must be discarded.</item>
        /// </list>
        /// Deciding this by Euclidean distance instead cannot distinguish the third case from the
        /// second, which floods every vertex's neighbour ring with diagonals and makes face tracing
        /// close a triangle almost everywhere it should have closed a quad.
        ///
        /// Callers must align the two crosses with <see cref="OrientationField.Compat4"/> first.
        /// Unlike the continuous <see cref="Compat"/> — where the square lattice is invariant under
        /// the 4-RoSy group, so the choice of representative cannot change the answer — the integer
        /// coordinates here are stated relative to a particular (q, t) basis and are meaningless
        /// across mismatched frames.
        ///
        /// Matches the reference's <c>compat_position_extrinsic_index_4</c>.
        /// </remarks>
        public static void CompatIndex(
            Vector3 p0, Vector3 n0, Vector3 q0, Vector3 o0,
            Vector3 p1, Vector3 n1, Vector3 q1, Vector3 o1,
            float scale, float invScale,
            out Vector2Int shift0, out Vector2Int shift1, out float error)
        {
            CompatIndex(p0, n0, q0, o0, p1, n1, q1, o1,
                scale, invScale, scale, invScale, out shift0, out shift1, out error);
        }

        /// <summary>
        /// <see cref="CompatIndex"/> with a different lattice spacing on each side.
        /// </summary>
        /// <remarks>
        /// The integer coordinates each side returns are in ITS OWN lattice, which is what lets the
        /// edge classification below keep working unchanged under grading: "one step apart" is a
        /// statement about each vertex's own cell index, and stays meaningful when the two cells
        /// are different sizes. The `error` it reports does not — it is a world-space residual, and
        /// under grading it carries an irreducible floor set by the spacing difference. Callers
        /// ordering candidates by it are still ordering them correctly, but must not read a nonzero
        /// value as disagreement.
        /// </remarks>
        public static void CompatIndex(
            Vector3 p0, Vector3 n0, Vector3 q0, Vector3 o0,
            Vector3 p1, Vector3 n1, Vector3 q1, Vector3 o1,
            float scale0, float invScale0, float scale1, float invScale1,
            out Vector2Int shift0, out Vector2Int shift1, out float error)
        {
            var t0 = OrientationField.Rotate90(q0, n0);
            var t1 = OrientationField.Rotate90(q1, n1);
            var middle = MiddlePoint(p0, n0, p1, n1);

            var o0p = FloorIndex(o0, q0, n0, middle, invScale0);
            var o1p = FloorIndex(o1, q1, n1, middle, invScale1);

            var bestCost = float.PositiveInfinity;
            int bestI = 0, bestJ = 0;

            for (var i = 0; i < 4; i++)
            {
                // Note the offsets fold the floor index in and are applied to the *original* o,
                // not to a pre-floored point. Algebraically the same location as in Compat; kept
                // in this form to stay line-for-line with the reference.
                var o0t = o0 + (q0 * ((i & 1) + o0p.x) + t0 * (((i & 2) >> 1) + o0p.y)) * scale0;
                for (var j = 0; j < 4; j++)
                {
                    var o1t = o1 + (q1 * ((j & 1) + o1p.x) + t1 * (((j & 2) >> 1) + o1p.y)) * scale1;
                    var cost = (o0t - o1t).sqrMagnitude;

                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestI = i;
                        bestJ = j;
                    }
                }
            }

            shift0 = new Vector2Int((bestI & 1) + o0p.x, ((bestI & 2) >> 1) + o0p.y);
            shift1 = new Vector2Int((bestJ & 1) + o1p.x, ((bestJ & 2) >> 1) + o1p.y);
            error = bestCost;
        }

        /// <summary>
        /// How an edge between two vertices should be treated, from the integer lattice
        /// coordinates produced by <see cref="CompatIndex"/>.
        /// </summary>
        public enum EdgeClass
        {
            /// <summary>Same lattice point: collapse the two vertices together.</summary>
            Collapse,
            /// <summary>One lattice step apart: a genuine output quad edge.</summary>
            Edge,
            /// <summary>Two or more cells apart, or a diagonal: meaningless, discard.</summary>
            Discard
        }

        public static EdgeClass Classify(Vector2Int shift0, Vector2Int shift1)
        {
            var dx = Mathf.Abs(shift0.x - shift1.x);
            var dy = Mathf.Abs(shift0.y - shift1.y);

            if (dx == 0 && dy == 0)
                return EdgeClass.Collapse;

            // The (1,1) diagonal is excluded along with everything two or more cells out: on a
            // quad lattice a diagonal is not an edge, and admitting it is what turns quads into
            // pairs of triangles.
            if (Mathf.Max(dx, dy) > 1 || (dx == 1 && dy == 1))
                return EdgeClass.Discard;

            return EdgeClass.Edge;
        }

        /// <param name="initialPositions">
        /// Lattice points to start from instead of the vertices themselves. Used to prolongate a
        /// coarser level's solution: the first sweep re-anchors each one into the half-cell box
        /// around its own vertex, so a coarse position that is off by less than a cell arrives
        /// already in the right cell rather than having to be discovered again from scratch.
        /// </param>
        public static PositionField Solve(RemeshTopology topology, OrientationField orientation,
            float scale, int iterations = 50, Action<float> onProgress = null,
            Vector3[] initialPositions = null, FeatureGraph features = null)
        {
            return Solve(topology, orientation, scale, null, iterations, onProgress,
                initialPositions, features);
        }

        /// <summary>
        /// The position solve over a graded lattice: each vertex carries its own spacing.
        /// </summary>
        /// <param name="scaleField">
        /// Per-vertex spacing, or null to solve uniformly at <paramref name="scale"/>.
        /// </param>
        /// <param name="features">
        /// Creases the lattice must land ON rather than merely run parallel to. Null leaves the
        /// field unconstrained, which is correct for a surface with no creases and is what every
        /// caller did before this parameter existed. See the projection in the sweep.
        /// </param>
        public static PositionField Solve(RemeshTopology topology, OrientationField orientation,
            float scale, ScaleField scaleField, int iterations = 50,
            Action<float> onProgress = null, Vector3[] initialPositions = null,
            FeatureGraph features = null)
        {
            if (topology == null)
                throw new ArgumentNullException(nameof(topology));
            if (orientation == null)
                throw new ArgumentNullException(nameof(orientation));
            if (scale <= 0f)
                throw new ArgumentOutOfRangeException(nameof(scale), "Lattice spacing must be positive.");

            var count = topology.vertexCount;

            // A field solved over a different vertex count is not this mesh's field. Falling back to
            // the mean would run and produce plausible output, which is worse than stopping.
            if (scaleField != null && scaleField.vertexCount != count)
                throw new ArgumentException(
                    $"Scale field has {scaleField.vertexCount} vertices, topology has {count}.",
                    nameof(scaleField));

            var field = new PositionField
            {
                positions = new Vector3[count],
                scale = scale,
                spacing = scaleField?.spacing
            };

            var invScale = 1f / scale;

            // Reciprocals hoisted: the sweep below reads them once per adjacency entry, which on a
            // 200k-vertex mesh is several million divisions per iteration.
            var perVertexScale = scaleField?.spacing;
            float[] perVertexInvScale = null;
            if (perVertexScale != null)
            {
                perVertexInvScale = new float[count];
                for (var i = 0; i < count; i++)
                    perVertexInvScale[i] = perVertexScale[i] > 0f ? 1f / perVertexScale[i] : invScale;
            }

            // Start each lattice on its own vertex; smoothing is what merges them. Unless a
            // coarser level has already decided roughly where the cells go, in which case start
            // there — that is the entire mechanism by which a hierarchy carries agreement further
            // than a sweep can.
            if (initialPositions != null && initialPositions.Length == count)
                Array.Copy(initialPositions, field.positions, count);
            else
                Array.Copy(topology.positions, field.positions, count);

            var o = field.positions;
            var p = topology.positions;
            var normals = topology.normals;
            var q = orientation.directions;
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;

            // Coloured parallel sweeps.
            //
            // Within a colour class no two vertices are adjacent, so a vertex reads only
            // neighbours from other classes and updating a whole class at once is identical to
            // updating its members one at a time. See GraphColouring for why this is a change of
            // visit order and not a change of method.
            //
            // Residuals go into per-vertex arrays and are summed afterwards, in index order. A
            // shared accumulator would need a lock in the hot loop, and an interlocked add on
            // floats would make the total depend on scheduling — which the regression gate would
            // then read as a change in the solver.
            var colouring = topology.colouring;
            var residualPerVertex = new float[count];
            var driftPerVertex = new float[count];
            var activePerVertex = new bool[count];

            // Below this there is more overhead in handing work to the pool than there is work.
            const int k_ParallelThreshold = 2048;
            var parallel = count >= k_ParallelThreshold;

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                for (var phase = 0; phase < colouring.classCount; phase++)
                {
                    var phaseFrom = colouring.start[phase];
                    var phaseTo = colouring.start[phase + 1];

                    void Sweep(int slot)
                    {
                        var i = colouring.vertices[slot];

                        var from = start[i];
                        var to = start[i + 1];
                        if (from == to)
                        {
                            activePerVertex[i] = false;
                            return;
                        }

                        var ni = normals[i];
                        var pi = p[i];
                        var sum = o[i];
                        var weight = 0f;

                        var scaleI = perVertexScale != null ? perVertexScale[i] : scale;
                        var invScaleI = perVertexInvScale != null ? perVertexInvScale[i] : invScale;

                        for (var k = from; k < to; k++)
                        {
                            var j = adjacency[k];

                            // The two crosses have to be brought into a common frame first, or the
                            // lattice axes disagree and the rounding below is meaningless.
                            OrientationField.Compat4(q[i], ni, q[j], normals[j], out var qi, out var qj);

                            var scaleJ = perVertexScale != null ? perVertexScale[j] : scale;
                            var invScaleJ = perVertexInvScale != null ? perVertexInvScale[j] : invScale;

                            Compat(pi, ni, qi, sum, p[j], normals[j], qj, o[j],
                                scaleI, invScaleI, scaleJ, invScaleJ, out var a, out var b);

                            sum = a * weight + b;
                            weight += 1f;
                            sum /= weight;

                            // Keep the accumulated position on the vertex's tangent plane.
                            sum -= ni * Vector3.Dot(ni, sum - pi);
                        }

                        // Snap to the lattice cell nearest the vertex itself, so the field stays
                        // anchored to the surface instead of drifting along it. This vertex's own
                        // spacing, always — the anchor is a statement about where THIS lattice sits.
                        sum = RoundToLattice(sum, q[i], ni, pi, scaleI, invScaleI);

                        // THE LATTICE IS PINNED TO THE CREASE, not merely aligned with it.
                        //
                        // This is the step the pipeline was missing, and the defect it leaves is
                        // specific: feature directions already reach the ORIENTATION field, so the
                        // cross field combs neatly along a panel edge and the quads run parallel to
                        // it. Nothing told the POSITION field where the cell boundaries fall, so the
                        // edge loop runs parallel to the crease and OFFSET from it by whatever the
                        // lattice happened to pick. Measured on the armour asset: 11,574 creases
                        // detected, 25.8 % with an output vertex on them, mean miss a third of a
                        // quad. Detected, aligned, and missed.
                        //
                        // A crease vertex's lattice point is therefore projected onto the crease
                        // LINE - the line through the input vertex along the polyline tangent -
                        // rather than being left anywhere on the tangent PLANE. One degree of
                        // freedom instead of two: the lattice may still slide along the crease to
                        // agree with its neighbours, which is what keeps spacing even, but it can no
                        // longer step off it.
                        //
                        // A junction pins outright. Where two creases meet there is no line left to
                        // slide along, and the corner is the part of the shape a viewer checks
                        // first.
                        if (features != null && features.isFeatureVertex[i])
                        {
                            if (features.featureDegree[i] >= 3)
                            {
                                sum = pi;
                            }
                            else
                            {
                                var tangent = features.vertexTangent[i];
                                var length = tangent.magnitude;
                                // Below 1e-5 .normalized returns zero, and a zero tangent would
                                // collapse the projection onto the vertex and pin a plain crease
                                // vertex as though it were a corner.
                                if (length > 1e-5f)
                                {
                                    tangent /= length;
                                    sum = pi + tangent * Vector3.Dot(sum - pi, tangent);
                                }
                            }
                        }

                        residualPerVertex[i] = Vector3.Distance(o[i], sum);

                        // Movement modulo the lattice: bring the new value's coset to whichever
                        // representative sits nearest the old value, then measure. A vertex that
                        // only swapped to an equivalent representative of the same lattice point
                        // has not moved, and must not be counted as if it had.
                        var gaugeFixed = RoundToLattice(sum, q[i], ni, o[i], scaleI, invScaleI);
                        driftPerVertex[i] = Vector3.Distance(o[i], gaugeFixed);

                        activePerVertex[i] = true;
                        o[i] = sum;
                    }

                    if (parallel && phaseTo - phaseFrom >= 256)
                        System.Threading.Tasks.Parallel.For(phaseFrom, phaseTo, BackgroundScheduler.Options, Sweep);
                    else
                        for (var slot = phaseFrom; slot < phaseTo; slot++) Sweep(slot);
                }

                double residual = 0;
                double drift = 0;
                var moved = 0;

                for (var i = 0; i < count; i++)
                {
                    if (!activePerVertex[i]) continue;
                    residual += residualPerVertex[i];
                    drift += driftPerVertex[i];
                    moved++;
                }

                field.lastResidual = moved > 0 ? (float)(residual / moved) * invScale : 0f;
                field.representativeDrift = moved > 0 ? (float)(drift / moved) * invScale : 0f;
                field.iterationsRun = iteration + 1;

                onProgress?.Invoke((iteration + 1) / (float)iterations);

                // Early-out on the gauge-invariant quantity. The old test used lastResidual
                // against 1e-5, which the anchoring snap makes unreachable, so it never fired and
                // every solve burned its full iteration budget.
                if (field.representativeDrift < 1e-4f)
                    break;
            }

            return field;
        }
    }
}
