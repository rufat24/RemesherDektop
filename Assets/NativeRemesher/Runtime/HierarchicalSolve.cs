// Coarse-to-fine solving of the POSITION field, which is the piece this project has been missing.
//
// MeshHierarchy.cs was written, complete, and called from nowhere. Its own header already stated the
// argument; three separate measurements confirmed it from the other end.
//
//   1. LatticeDirections: 40% of output clusters lack one of the four lattice directions, and the
//      missing neighbour is genuinely absent or genuinely off-axis — refitting each frame to its own
//      ring moves the histogram by 13 vertices of 4,957, so the frames are not the problem.
//
//   2. LatticeIntegration: 98.3% of triangles already satisfy the curl-free condition, so the
//      per-edge integer offsets are very nearly a valid grid map. Only 926 triangles of 54,740 are
//      non-integrable, which bounds what any repair of them could change at about 1% of edge
//      classifications. The defect is not local inconsistency.
//
//   3. Raising the sweep budget from 60/50 to 300/300 changes irregular vertices from 51.3% to
//      50.0%. Five times the work, nothing to show.
//
// Together those say the field is locally right and globally wrong, and (3) says more of the same
// sweeping cannot fix it. That is the behaviour of Gauss-Seidel on a non-convex energy: information
// moves one edge per sweep, so on a 27,000-vertex mesh a few dozen sweeps reach a neighbourhood and
// never the surface, and what they converge to is whichever local minimum the starting point fell
// into. The reference runs SIX sweeps per level and gets a globally consistent field, because
// agreement is established where the mesh is small and then carried down.
//
// So the hierarchy is not the performance upgrade the plan filed it as. It is the mechanism.
//
// POSITIONS ONLY, and that is measured rather than assumed. The obvious thing is to solve both
// fields this way, and the first version did. On the reference asset, quad fraction:
//
//     no hierarchy                       0.8422
//     orientations only                  0.8389
//     positions only                     0.8718  (and 0.8563 on the feature-carrying profile)
//     both                               0.8770  (but 0.8224 with features)
//
// The orientation hierarchy is worth nothing on its own and it costs 3.4 points on the profile that
// carries feature constraints — because restricting a constraint upward is not sound: a coarse
// vertex straddling a crease has a normal averaged across it, so the crease direction projected
// onto that normal points nowhere in particular, and the coarse solve is then pinned to that and
// hands it down. It also failed the cube's feature-fidelity test outright.
//
// THAT CONCLUSION WAS RIGHT AND ITS EVIDENCE WAS NOT, which is why this paragraph is longer than it
// wants to be. Every column in the table above is quad fraction. A singularity is a VERTEX whose
// valence is not four; quad fraction counts FACES with four corners, and a mesh can be 100 % quads
// with every vertex irregular. Reducing singularities is the only thing an orientation hierarchy
// does, and the metric it was judged on could not see that at all. The same mistake was made a
// second time in this project — the triangle-pairing tolerance, set on four agreeing columns and
// reversed the moment irregular-vertex fraction joined them — so the solve was rebuilt and
// re-measured against the column that had been missing (Benchmark/OrientationHierarchySweep.cs):
//
//     sweeps   field sing+/-   irregular %   quad %   triangles
//     off        206 / 204        23.55      96.08       203
//     6          184 / 178        24.74      96.38       184
//
// The reach argument is CORRECT about the field — 410 singularities down to 362, both signs falling
// together exactly as a cancelling-pair theory predicts — and still does not pay, because
// extraction's later passes add about 390 irregular vertices whatever field they are handed. That
// is a more useful finding than the feature itself: no amount of field work is the answer while
// collapse turns 362 singularities into 934 clusters.
//
// So the hierarchical orientation solve is KEPT and switched off by default, the opposite of what
// was done last time. The earlier deletion was not wrong about the answer, it was wrong about
// having established it, and deleting the code took the only thing that could be re-measured with
// it. What guards against calling it by accident is RemeshProfile.Validated, where zero means off
// rather than taking a default.
//
// Restriction and prolongation:
//
//   Positions restrict for free — MeshHierarchy.Downsample already area-averages them when it
//   builds each level, so a coarse vertex sits where its children's mass is.
//
//   The lattice prolongates by copying a coarse value to both children. The copy lands within a
//   cell of where the child belongs and the first sweep's anchoring snap moves it into the child's
//   own half-cell box, so the child starts in the cell the coarse level chose rather than
//   rediscovering one.
//
//   The orientation field, solved once at the finest level, restricts UPWARD for the coarse
//   position solves to read. That restriction takes one child's cross rather than averaging: two
//   crosses 90 degrees apart describe the same cross and average to zero.
//
// The lattice spacing does NOT change per level. It is the target quad size, a property of the
// output and not of the level being solved, and the reference treats it the same way. That is also
// what bounds the depth — see defaultPositionSpacingMultiple.

using System;
using UnityEngine;

namespace NativeRemesher
{
    public sealed class HierarchicalStats
    {
        /// <summary>Levels actually solved, counting from the finest. See UsefulPositionLevels.</summary>
        public int levels;

        public int[] levelSizes;
        public int[] levelIterations;
        public float[] levelResidual;
        public long totalMs;

        public override string ToString()
        {
            var sizes = levelSizes != null ? string.Join(",", levelSizes) : "";
            var iterations = levelIterations != null ? string.Join(",", levelIterations) : "";
            var residuals = "";
            if (levelResidual != null)
            {
                var parts = new string[levelResidual.Length];
                for (var i = 0; i < parts.Length; i++) parts[i] = levelResidual[i].ToString("G3");
                residuals = string.Join(",", parts);
            }

            return $"levels={levels} sizes=[{sizes}] iters=[{iterations}] " +
                   $"residual=[{residuals}] {totalMs}ms";
        }
    }

    public static class HierarchicalSolve
    {
        /// <summary>Sweeps per level. The reference's <c>levelIterations</c>, which is 6.</summary>
        /// <remarks>
        /// Small on purpose, and the reason is the whole point of the file: a level's job is not to
        /// converge, it is to hand the next finer level a starting point in the right basin. Sweeps
        /// spent past that are the ones measurement showed buy nothing. The finest level gets as
        /// many as the caller asked for, because it is the only level whose answer is used.
        /// </remarks>
        public const int defaultLevelIterations = 6;

        /// <summary>Fallback sweep count at level 0 when a caller does not state one.</summary>
        public const int defaultFinestIterations = 20;

        /// <summary>
        /// How much coarser than a level's own edges the lattice must be for that level to be worth
        /// solving the position field on. A LARGER value means a SHALLOWER hierarchy.
        /// </summary>
        /// <remarks>
        /// The position field's whole content is where the cell boundaries fall, and a level whose
        /// edges are longer than one cell cannot express that: every edge there classifies as many
        /// cells apart, Compat picks a representative with no information in it, and what
        /// prolongates down is noise sitting where the starting guess used to be.
        ///
        /// One cell per level edge is therefore the principled cut, and it is also the measured
        /// knee. Swept over both cases that pull in opposite directions — DepthSweep in the
        /// benchmark assembly runs it:
        ///
        ///   cylinder_capped @2000, clamped to its input's finest spacing, so its coarse levels have
        ///   nothing to say. Worst-case deviation by level count: 1 level 0.00081, 2 levels 0.00084,
        ///   3 levels 0.20231, 5 levels 0.20037, 8 levels 0.20167. A cliff between two and three,
        ///   with the tracer going from 60 uncovered boundary edges to 212 and the hole filler
        ///   lidding a 58-corner cap across the cylinder.
        ///
        ///   The reference photogrammetry asset @5474, where depth is what pays. Quad fraction by
        ///   level count: 1 level 0.8389, 3 levels 0.8770, 5 levels 0.8938, 7 levels 0.8989, 10
        ///   levels 0.8995. Monotone, saturating around seven.
        ///
        /// A multiple of 1 gives the cylinder 2 levels and the asset 3, which is the last value
        /// before the cylinder falls off. It costs the asset about 2 points of quad fraction against
        /// an unbounded depth, and that is the trade taken: a 0.2 worst-case deviation on a shape as
        /// simple as a capped cylinder is a defect a user would see immediately, and 2 points of
        /// quad fraction is not.
        /// </remarks>
        public const float defaultPositionSpacingMultiple = 1f;

        /// <summary>
        /// How many levels from the finest the position field can usefully be solved on, counting
        /// level 0. Always at least 1.
        /// </summary>
        public static int UsefulPositionLevels(MeshHierarchy hierarchy, float scale,
            float spacingMultiple)
        {
            if (hierarchy == null) throw new ArgumentNullException(nameof(hierarchy));

            var usable = 1;

            for (var level = 1; level < hierarchy.levelCount; level++)
            {
                var edge = hierarchy.levels[level].averageEdgeLength;
                if (!(edge > 0f) || edge * spacingMultiple > scale) break;

                usable = level + 1;
            }

            return usable;
        }

        /// <param name="finestIterations">
        /// Sweeps at level 0, whose solution is the one that leaves here. Separate from
        /// levelIterations, and taken from the caller rather than fixed, because the hierarchy
        /// replaces the solver's REACH and not its convergence: a coarse level only has to put the
        /// next one in the right basin, but level 0's answer is the answer, and a profile asking for
        /// 300 sweeps is asking about that one. Getting this wrong is not theoretical — a first
        /// version pinned it at 20 and torus@8000 went from 0.00087 worst-case deviation to 0.00427.
        /// </param>
        public static PositionField Positions(MeshHierarchy hierarchy, OrientationField finestOrientation,
            float scale, int levelIterations = defaultLevelIterations,
            int finestIterations = defaultFinestIterations,
            Action<float> onProgress = null, HierarchicalStats stats = null,
            float spacingMultiple = defaultPositionSpacingMultiple)
        {
            return Positions(hierarchy, finestOrientation, scale, null, levelIterations,
                finestIterations, onProgress, stats, spacingMultiple);
        }

        /// <summary>
        /// The hierarchical position solve over a graded lattice.
        /// </summary>
        /// <param name="scaleField">
        /// Per-vertex spacing on the FINEST level, or null to solve uniformly.
        /// </param>
        /// <remarks>
        /// The level count is still chosen from the MEAN spacing. It is a question about how far
        /// the lattice can usefully coarsen relative to the mesh's own edges, and answering it
        /// per-vertex would give a different depth in different places, which the hierarchy has no
        /// way to represent.
        /// </remarks>
        /// <param name="features">
        /// Creases the finest level's lattice must land on. Applied at LEVEL 0 ONLY, and that is the
        /// point rather than a simplification: a feature graph is indexed by finest-level vertices,
        /// and restricting one upward means asking which crease a coarse vertex standing for two
        /// fine ones belongs to. The orientation hierarchy tried exactly that and it is what broke
        /// the profile carrying constraints — a coarse vertex straddling a crease has a normal
        /// averaged across it, so the crease projected onto that normal points nowhere. Coarse
        /// levels exist to put level 0 in the right basin; level 0's answer is the answer, and it is
        /// the only level whose vertices a crease can be stated about.
        /// </param>
        public static PositionField Positions(MeshHierarchy hierarchy, OrientationField finestOrientation,
            float scale, ScaleField scaleField, int levelIterations = defaultLevelIterations,
            int finestIterations = defaultFinestIterations,
            Action<float> onProgress = null, HierarchicalStats stats = null,
            float spacingMultiple = defaultPositionSpacingMultiple,
            FeatureGraph features = null)
        {
            if (hierarchy == null) throw new ArgumentNullException(nameof(hierarchy));
            if (finestOrientation == null) throw new ArgumentNullException(nameof(finestOrientation));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var count = UsefulPositionLevels(hierarchy, scale, spacingMultiple);

            var orientations = RestrictOrientations(hierarchy, finestOrientation, count);
            var scales = RestrictScales(hierarchy, scaleField, count);

            PositionField field = null;
            Vector3[] prolongated = null;

            Prepare(stats, hierarchy);
            if (stats != null) stats.levels = count;   // solved, not built

            for (var level = count - 1; level >= 0; level--)
            {
                var view = View(hierarchy, level);
                var iterations = level == 0
                    ? Mathf.Max(levelIterations, finestIterations)
                    : levelIterations;

                field = PositionField.Solve(view, orientations[level], scale, scales?[level],
                    iterations, null, prolongated, level == 0 ? features : null);

                Record(stats, hierarchy, level, field.iterationsRun, field.representativeDrift);
                onProgress?.Invoke((count - level) / (float)count);

                if (level == 0) break;
                prolongated = ProlongateVectors(hierarchy, level, field.positions);
            }

            if (stats != null) stats.totalMs = watch.ElapsedMilliseconds;
            return field;
        }

        /// <summary>
        /// Carries the finest level's cross field up to the coarse levels the position solve needs.
        /// </summary>
        /// <remarks>
        /// Takes one child's cross, whichever carries more area, rather than averaging. Two crosses
        /// 90 degrees apart describe the same cross and their vector average is zero, so an average
        /// is at best meaningless and at worst degenerate exactly where the field turns — which is
        /// where it matters. A coarse level needs the right basin, not the right answer.
        /// </remarks>
        /// <summary>
        /// The finest level's spacing carried up to the coarse levels the position solve needs.
        /// </summary>
        /// <remarks>
        /// A coarse vertex stands for one or two fine ones, and takes the SMALLER of their
        /// spacings. Not the mean: a coarse level exists to put the next finer level in the right
        /// basin, and a coarse cell that is too large has already merged surface the fine level
        /// then cannot separate, while one that is too small merely converges a little slower.
        /// The error is asymmetric, so the restriction is too.
        /// </remarks>
        static ScaleField[] RestrictScales(MeshHierarchy hierarchy, ScaleField finest, int count)
        {
            if (finest == null) return null;

            var scales = new ScaleField[count];
            scales[0] = finest;

            for (var level = 1; level < count; level++)
            {
                var coarse = hierarchy.levels[level];
                var fine = scales[level - 1].spacing;
                var values = new float[coarse.vertexCount];

                for (var c = 0; c < coarse.vertexCount; c++)
                {
                    var a = coarse.toUpperA[c];
                    var b = coarse.toUpperB[c];

                    var h = a >= 0 && a < fine.Length ? fine[a] : finest.meanSpacing;
                    if (b != HierarchyLevel.invalid && b >= 0 && b < fine.Length)
                        h = Mathf.Min(h, fine[b]);

                    values[c] = h;
                }

                scales[level] = ScaleField.FromSpacings(values, finest.meanSpacing);
            }

            return scales;
        }

        static OrientationField[] RestrictOrientations(MeshHierarchy hierarchy,
            OrientationField finest, int count)
        {
            var orientations = new OrientationField[count];
            orientations[0] = finest;

            for (var level = 1; level < count; level++)
            {
                var fine = hierarchy.levels[level - 1];
                var coarse = hierarchy.levels[level];
                var directions = new Vector3[coarse.vertexCount];

                for (var c = 0; c < coarse.vertexCount; c++)
                {
                    var a = coarse.toUpperA[c];
                    var b = coarse.toUpperB[c];

                    var pick = a;
                    if (b != HierarchyLevel.invalid && fine.areas != null &&
                        fine.areas[b] > fine.areas[a]) pick = b;

                    var d = orientations[level - 1].directions[pick];
                    var n = coarse.normals[c];
                    d -= n * Vector3.Dot(n, d);
                    directions[c] = MeshMath.SafeNormalize(d, OrientationField.ArbitraryTangent(n));
                }

                orientations[level] = new OrientationField { directions = directions };
            }

            return orientations;
        }

        /// <summary>
        /// The hierarchical orientation solve: coarse to fine, each level seeded from the one above.
        /// </summary>
        /// <remarks>
        /// WHY THIS EXISTS, and why it is not the same argument as the position solve's.
        ///
        /// A Gauss-Seidel sweep moves information across exactly one edge. Smoothing a cross field
        /// flat on a 50,000-vertex mesh therefore lets two singularities half the model apart find
        /// out about each other only after tens of thousands of sweeps, and nobody runs tens of
        /// thousands of sweeps. What the solver converges to instead is a field that is smooth
        /// everywhere LOCALLY and wrong globally: full of singularity pairs of opposite index that
        /// sit near each other, each pair a place where the field turned one way and then turned
        /// back, which a field with more reach would simply not have turned at all.
        ///
        /// Those pairs cost nothing topologically - they cancel, so the index sum is whatever Euler
        /// requires either way - and they cost everything visually, because extraction turns each
        /// one into a valence-3 beside a valence-5 and the quad flow has to swirl around it.
        ///
        /// The depth rule is DIFFERENT from the position field's, and this is the part worth being
        /// careful about. `UsefulPositionLevels` stops where a level's edges grow longer than one
        /// lattice cell, because the position field's entire content is where cell boundaries fall
        /// and a level coarser than a cell cannot represent that. A cross field has no length scale
        /// at all - it is a direction per vertex - so no level is too coarse to carry one, and the
        /// coarsest levels are precisely the ones that give the reach this is being built for.
        /// Every level is used.
        /// </remarks>
        /// <param name="finestIterations">
        /// Sweeps at level 0, whose field is the one returned. As in the position solve, the
        /// hierarchy replaces the solver's REACH and not its convergence.
        /// </param>
        public static OrientationField Orientations(MeshHierarchy hierarchy,
            int levelIterations = defaultLevelIterations,
            int finestIterations = defaultFinestIterations,
            Action<float> onProgress = null,
            Vector3[] constraintDirections = null, float[] constraintWeights = null,
            HierarchicalStats stats = null,
            Vector3[] guideDirections = null, float[] guideWeights = null)
        {
            if (hierarchy == null) throw new ArgumentNullException(nameof(hierarchy));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var count = hierarchy.levelCount;
            var constraints = RestrictConstraints(hierarchy, constraintDirections,
                constraintWeights, count);
            // Guides ride up the levels by the same rule: a coarse vertex takes its stronger child
            // and drops a direction the coarse normal projects to nothing.
            var guides = RestrictConstraints(hierarchy, guideDirections, guideWeights, count);

            OrientationField field = null;
            Vector3[] prolongated = null;

            Prepare(stats, hierarchy);
            if (stats != null) stats.levels = count;

            for (var level = count - 1; level >= 0; level--)
            {
                var view = View(hierarchy, level);
                var iterations = level == 0
                    ? Mathf.Max(levelIterations, finestIterations)
                    : levelIterations;

                field = OrientationField.Solve(view, iterations, null, -1,
                    constraints?[level].directions, constraints?[level].weights, prolongated,
                    guides?[level].directions, guides?[level].weights);

                Record(stats, hierarchy, level, field.iterationsRun, field.lastResidual);
                onProgress?.Invoke((count - level) / (float)count);

                if (level == 0) break;
                prolongated = ProlongateVectors(hierarchy, level, field.directions);
            }

            if (stats != null) stats.totalMs = watch.ElapsedMilliseconds;
            return field;
        }

        struct LevelConstraints
        {
            public Vector3[] directions;
            public float[] weights;
        }

        /// <summary>Carries per-vertex orientation constraints up to the coarse levels.</summary>
        /// <remarks>
        /// Takes the child with the STRONGER constraint rather than averaging or taking the larger
        /// child, because a constraint is a statement about a feature and diluting it with a free
        /// neighbour's nothing is how a feature stops being one.
        ///
        /// A constraint is DROPPED, not weakened, where projecting it onto the coarse normal
        /// leaves nothing. A coarse vertex spans both sides of a crease and its normal is the
        /// average of the two, so a direction perpendicular to the crease projects to near zero
        /// there - and a constraint pointing nowhere is worse than no constraint, since the solver
        /// cannot tell the difference between a degenerate direction and a deliberate one.
        /// </remarks>
        static LevelConstraints[] RestrictConstraints(MeshHierarchy hierarchy,
            Vector3[] directions, float[] weights, int count)
        {
            if (directions == null || weights == null) return null;
            if (directions.Length != hierarchy.levels[0].vertexCount) return null;

            var levels = new LevelConstraints[count];
            levels[0] = new LevelConstraints { directions = directions, weights = weights };

            for (var level = 1; level < count; level++)
            {
                var coarse = hierarchy.levels[level];
                var fineD = levels[level - 1].directions;
                var fineW = levels[level - 1].weights;
                var d = new Vector3[coarse.vertexCount];
                var w = new float[coarse.vertexCount];

                for (var c = 0; c < coarse.vertexCount; c++)
                {
                    var a = coarse.toUpperA[c];
                    var b = coarse.toUpperB[c];

                    var pick = a;
                    if (b != HierarchyLevel.invalid && b >= 0 && b < fineW.Length &&
                        a >= 0 && a < fineW.Length && fineW[b] > fineW[a]) pick = b;
                    if (pick < 0 || pick >= fineW.Length || fineW[pick] <= 0f) continue;

                    var n = coarse.normals[c];
                    var dir = fineD[pick];
                    dir -= n * Vector3.Dot(n, dir);

                    // Below a tenth of unit length the projection has destroyed the direction.
                    if (dir.sqrMagnitude <= 0.01f) continue;

                    d[c] = dir.normalized;
                    w[c] = fineW[pick];
                }

                levels[level] = new LevelConstraints { directions = d, weights = w };
            }

            return levels;
        }

        static RemeshTopology View(MeshHierarchy hierarchy, int level)
        {
            var l = hierarchy.levels[level];
            return RemeshTopology.VertexGraphView(l.positions, l.normals, l.areas,
                l.adjacencyStart, l.adjacency, l.colouring);
        }

        /// <summary>Copies each coarse value to both of the finer vertices it was merged from.</summary>
        static Vector3[] ProlongateVectors(MeshHierarchy hierarchy, int coarseLevel, Vector3[] coarse)
        {
            var fine = hierarchy.levels[coarseLevel - 1];
            var level = hierarchy.levels[coarseLevel];
            var result = new Vector3[fine.vertexCount];

            for (var c = 0; c < level.vertexCount; c++)
            {
                var a = level.toUpperA[c];
                var b = level.toUpperB[c];

                if (a != HierarchyLevel.invalid) result[a] = coarse[c];
                if (b != HierarchyLevel.invalid) result[b] = coarse[c];
            }

            return result;
        }

        static void Prepare(HierarchicalStats stats, MeshHierarchy hierarchy)
        {
            if (stats == null) return;

            stats.levels = hierarchy.levelCount;
            stats.levelSizes = new int[hierarchy.levelCount];
            stats.levelIterations = new int[hierarchy.levelCount];
            stats.levelResidual = new float[hierarchy.levelCount];
        }

        static void Record(HierarchicalStats stats, MeshHierarchy hierarchy, int level,
            int iterationsRun, float residual)
        {
            if (stats == null) return;

            stats.levelSizes[level] = hierarchy.levels[level].vertexCount;
            stats.levelIterations[level] = iterationsRun;
            stats.levelResidual[level] = residual;
        }
    }
}
