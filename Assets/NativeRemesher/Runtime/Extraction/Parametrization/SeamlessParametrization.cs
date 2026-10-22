// A global seamless parametrisation: the integer grid solved for the whole surface at once.
//
// From Bommes, Zimmer and Kobbelt, "Mixed-Integer Quadrangulation" (SIGGRAPH 2009), written from
// the paper.
//
// WHY. The QuadriFlow port places the lattice by local smoothing of per-vertex lattice origins
// (Instant Meshes' position field), then rounds every edge's offset to an integer on its own. On the
// helmet head that leaves ~2000 triangles whose three rounded offsets do not close; a min-cost flow
// then repairs them by pushing a unit of correction along some path, blind to distortion, and every
// such path is a row of sheared quads. Here the lattice coordinates (u, v) are ONE function over a
// cut surface, solved by least squares against the cross field and the size field, so the integer
// grid is consistent by construction: the only integers are the translations across the cuts, and
// those are rounded globally, re-solving after each batch.
//
// THE STEPS.
//   1. Comb the per-vertex cross field onto faces along a spanning tree of the dual graph. Every
//      dual edge then carries a period jump p (0..3): how many quarter turns the neighbour's cross
//      is from this one's, carried over the hinge.
//   2. A vertex is singular when the jumps round its fan do not sum to 0 mod 4.
//   3. Cut graph: the primal edges the dual tree did not cross, with dangling branches pruned at
//      regular interior vertices. What remains joins singularities to each other and to the
//      boundary (and carries the handles of a closed surface). Every pruned edge has jump 0.
//   4. The cut graph is split into chains at its nodes. Crossing chain c from its left face to its
//      right face maps chart coordinates by (u, v) -> R(-p) (u, v) + t_c: a quarter-turn rotation
//      and ONE translation per chain.
//   5. Unknowns: one (u, v) per vertex (per fan component) in the chart of its first face, plus
//      t_c per chain. Every corner's (u, v) is that vertex's unknown carried round its fan through
//      the transitions it crosses -- an exact linear expression, so the seams are seamless by
//      construction. Where the fan closes back on itself through a junction or round a singularity
//      the closure is a linear constraint, held by a penalty.
//   6. Energy: sum over faces of area * |grad(u, v) - frame / h|^2. One sparse SPD solve by
//      Jacobi-preconditioned conjugate gradients.
//   7. Integers: the t_c are rounded in batches, nearest-to-integer first, re-solving after each.
//
// OUTPUT. Not a mesh. The lattice node nearest each vertex, its axis and spacing -- exactly what
// the integer grid map otherwise reads off the position field -- so the extraction, subdivision,
// flip repair and every cleanup pass downstream run unchanged on a lattice that is now globally
// consistent.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class ParametrizationStats
    {
        public int faces;
        public int singularVertices;
        public int cancelledPairs, roundedSingularities, stiffened, stiffenIterationsRun;
        public bool reusedFreeSolve;
        public int featureEdges, featureEdgesSkipped, pinnedFeatureCoordinates;
        public int cutEdges;
        public int cutEdgesBeforePrune;
        public int chains;
        public int blocks;
        public int closureConstraints;
        public int cgIterations;
        public float residual;
        public int roundingRounds;
        public int roundedVariables;
        public float maxRoundingMove;
        public int flippedFaces;
        /// <summary>Folded faces after the free solve, the poles, the translations, the features.</summary>
        public string flipsByStage = "";
        /// <summary>Fan closures the final map leaves open by more than 0.05 cells, and the worst.</summary>
        public int closuresOpen;
        public int propagatedChains, closureNonInteger, forcedChains;
        public int freeChains, treeChains, freedPoles, regularRoots, unsolvedTree;
        public string closureKinds = "";
        public string trace = "";
        public int featureEdgesNearPoles;
        public int foldsBeforeUntangle, untangleRounds;
        public int cancelledFarPairs, pairCancelFailed;
        public int untangleBarrierRounds, untangleEvaluations;
        public string untangleLog = "";
        public string layout = "";
        [NonSerialized] public Vector3[] layoutPositions, layoutNormals;
        [NonSerialized] public List<int[]> layoutFaces;
        public int splitSingularities, splitFailed, splitMismatch, spreadMoves, spreadFailed, multipleSingularities;
        public int featureEdgesDroppedV, loopRows, loopPasses, featureEdgesSquashed;
        public int borderEdges, borderRuns, borderLoops;
        public float borderDeviationSum;
        public string borderRunLog = "";
        public int borderMergeStuck;
        public int borderMisaligned;
        public bool reusedLayout, reusedStructure;
        public int releasedFeatureGroups, releasedFeatureCoordinates;
        public float closureWorst;
        public long factorNonZeros;
        public long symbolicMs, factorMs;
        public int factorizations, factorFailures, factorRetries, symbolicRedone;
        public long transferMs;
        public long structureMs, expressionMs, assemblyMs;
        public string split = "";
        public int refinedSingularFaces, unmappedFaces;
        public long ms;

        public override string ToString() =>
            $"faces={faces} singular={singularVertices} cancelled={cancelledPairs}+{cancelledFarPairs}(fail {pairCancelFailed}) split={splitSingularities}(fail {splitFailed}, mismatch {splitMismatch}) spread={spreadMoves}(fail {spreadFailed}) multiple={multipleSingularities} poles={roundedSingularities} stiff={stiffenIterationsRun}/{stiffened} reused={reusedFreeSolve}/{reusedStructure}/{reusedLayout} features={featureEdges}(skip {featureEdgesSkipped}, nearPoles {featureEdgesNearPoles}, pins {pinnedFeatureCoordinates}, droppedV {featureEdgesDroppedV}, squashed {featureEdgesSquashed}, loops {loopRows}/{loopPasses}, border {borderEdges} in {borderRuns} runs on {borderLoops} loops dev {borderDeviationSum / Mathf.Max(1, borderEdges):F1} misaligned {borderMisaligned} stuck {borderMergeStuck} runs[{borderRunLog}], released {releasedFeatureGroups}/{releasedFeatureCoordinates}) cut={cutEdges}(of {cutEdgesBeforePrune}) chains={chains} " +
            $"blocks={blocks} closures={closureConstraints} cg={cgIterations} res={residual:G3} " +
            $"rounds={roundingRounds} rounded={roundedVariables} maxRound={maxRoundingMove:F3} " +
            $"flipped={flippedFaces} stages={flipsByStage}closuresOpen={closuresOpen} propagated={propagatedChains} nonInt={closureNonInteger} forced={forcedChains} free={freeChains} tree={treeChains} freedPoles={freedPoles} regRoots={regularRoots} unsolved={unsolvedTree} kinds={closureKinds}worst={closureWorst:F2} nnzL={factorNonZeros} symbolic={symbolicMs} factor={factorMs}/{factorizations} failed={factorFailures} retried={factorRetries} resymbolic={symbolicRedone} ms={ms} (split {split}structure {structureMs} expr {expressionMs} asm {assemblyMs}) transfer={transferMs} fineSingular={refinedSingularFaces} unmapped={unmappedFaces} trace[{trace}] ulog[{untangleLog}] layout[{layout}]";
    }

    /// <summary>
    /// What a second solve of the same mesh and cross field can reuse when only the cell size
    /// changed -- the face-count search's next pass. The system matrix does not depend on the size
    /// at all and the right-hand side scales with 1/h, so under a uniformly rescaled size the free
    /// solution scales exactly and the stiffening, which measures distortion against h, reaches the
    /// same weights: both are carried over and four of the six factorisations are skipped.
    /// </summary>
    public sealed class ParametrizationCache
    {
        internal RemeshTopology topology;
        internal Vector3[] crossField;
        internal float[] spacing;
        internal double[] stiff;
        internal double[] freeSolution;
        internal double[] layoutSolution;
        internal int[] jump;
        internal bool[] forced;
        internal HashSet<long> layoutDropped;
        internal object loopRows;
    }

    public static class SeamlessParametrization
    {
        /// <summary>Weight of a fan-closure constraint against a face's energy.</summary>
        public static float closureWeight = 30f;

        /// <summary>Share of the still-unrounded translations fixed per rounding round.</summary>
        public static float roundingBatch = 1f;

        /// <summary>CG iteration cap of the first solve and of each re-solve.</summary>
        public static int firstIterations = 4000, roundIterations = 600;

        /// <summary>Relative residual the solves stop at.</summary>
        public static float tolerance = 1e-6f;

        /// <summary>Local stiffening passes before rounding (0 = off).</summary>
        public static int stiffenIterations = 1;

        /// <summary>
        /// Gauss-Seidel sweeps of the local untangling per round (0 = off). See step 7d.
        /// </summary>
        public static int untangleSweeps = 0;

        /// <summary>Rounds of the untangling, each re-finding the folds.</summary>
        public static int untangleRounds = 4;

        /// <summary>Rings round each fold the untangling may move.</summary>
        public static int untangleRings = 2;

        /// <summary>Fraction of the way to the neighbours' mean a vertex moves per sweep.</summary>
        public static float untangleStep = 1f;

        /// <summary>Stiffening passes after every integer is fixed (0 = off).</summary>
        public static int postStiffenIterations = 0;

        /// <summary>Distortion below which the passes after rounding leave a face alone.</summary>
        public static float postStiffenThreshold = 0.5f;

        /// <summary>Distortion added for a folded face by the passes after rounding.</summary>
        public static float postStiffenFoldPenalty = 4f;

        /// <summary>Distortion (Frobenius, in cells) below which a face is left alone.</summary>
        public static float stiffenThreshold = 0.3f;

        /// <summary>Weight growth per unit of distortion, per pass.</summary>
        public static float stiffenRate = 1f;

        /// <summary>Largest weight a face may reach.</summary>
        public static float stiffenMax = 50f;

        /// <summary>
        /// Round the singularities together with the translations instead of solving between --
        /// one factorisation fewer.
        /// </summary>
        public static bool mergeRounding = false;

        /// <summary>Round each singular vertex onto a grid node before the cut translations.</summary>
        public static bool roundSingularities = true;

        /// <summary>Round the features' held coordinates to integers (grid lines), not only hold them equal.</summary>
        public static bool pinFeatures = true;

        /// <summary>
        /// Rounds of releasing creases whose integer rounding folded faces nearby (0 = keep every
        /// crease on its grid line). See the feature rounding step.
        /// </summary>
        public static int featureRollbackRounds = 1;

        /// <summary>Rings round a new fold within which a crease counts as its cause.</summary>
        public static int featureRollbackRings = 1;

        /// <summary>Weight of a feature's equal-coordinate row against a face's energy.</summary>
        public static float featureWeight = 100f;

        /// <summary>
        /// Leave a crease unconstrained within this many rings of a singularity (0 = constrain
        /// everywhere).
        /// </summary>
        public static int featureSkipNearSingularRings = 0;

        /// <summary>A feature edge closer than this (cosine) to neither chart axis is not constrained.</summary>
        public static float featureMinAlignment = 0.8f;

        /// <summary>One translation per cut edge rather than per chain (see the corner expressions).</summary>
        public static bool perEdgeTranslations = true;

        /// <summary>
        /// Round the chain translations so every fan closure stays closed: a chain that is the last
        /// free one at a junction or singularity is computed from the others instead of rounded.
        /// </summary>
        public static bool consistentRounding = false;

        /// <summary>Share of the free chains rounded per batch by the consistent rounding.</summary>
        public static float chainBatch = 0.5f;

        /// <summary>Diagnostics: every solve's statistics, appended.</summary>
        public static System.Text.StringBuilder debugPassLog = null;

        /// <summary>Diagnostics: write the coarse map at every fold census to {prefix}_{k}.obj.</summary>
        public static string debugDumpPrefix = null;

        /// <summary>Diagnostics: classify the closures left open after rounding.</summary>
        public static bool diagnoseClosures = false;

        /// <summary>Order the cut translations last in the factorisation (see SparseLdlt).</summary>
        public static bool chainsLast = false;

        /// <summary>
        /// Cancel opposite singularities up to this many cells apart along the shortest path
        /// between them (0 = adjacent pairs only). See step 2c.
        /// </summary>
        public static float cancelPairRadiusCells = 1f;

        /// <summary>
        /// Spread singularities closer than <see cref="spreadMinCells"/> of the same sign, and split
        /// vertices turning by two or more quarters, a cell apart along a grid axis (see step 2d).
        /// </summary>
        public static bool spreadSingularities = false;

        /// <summary>Distance, in cells, under which two singularities of one sign are spread.</summary>
        public static float spreadMinCells = 0.8f;

        /// <summary>Passes of the spreading.</summary>
        public static int spreadPasses = 3;

        /// <summary>
        /// Rounds of the barrier untangling (Garanzha et al. 2021) on the free map, before any
        /// rounding (0 = off). See BarrierUntangle.
        /// </summary>
        public static int untangleFreeRounds = 0;

        /// <summary>Rounds of the barrier untangling on the final, rounded map (0 = off).</summary>
        public static int untangleFinalRounds = 0;

        /// <summary>L-BFGS iterations per untangling round.</summary>
        public static int untangleInner = 25;

        /// <summary>
        /// Least relative shrink of the barrier's eps per round. The paper uses 0.1; 0.5 untangles
        /// these maps in fewer rounds (body at 2k: 8 rounds against 30 still folded).
        /// </summary>
        public static float untangleSigmaMin = 0.5f;

        /// <summary>Diagnostics: log every untangling round into the stats.</summary>
        public static bool untangleTrace = false;

        /// <summary>Weight of the barrier against the least squares energy.</summary>
        public static float untangleBarrierWeight = 1f;

        /// <summary>Weight of the area term inside the barrier.</summary>
        public static float untangleTheta = 0.5f;

        /// <summary>
        /// With the layout: the open border's edges held along one grid direction per run, that of
        /// the border's chord over this many cells round each edge (0: edge by edge, by alignment).
        /// </summary>
        public static float layoutBorderRunCells = 1f;

        /// <summary>
        /// With the layout, a run of the open border held along one grid direction is at least this
        /// many cells long: a shorter one, where the field wobbles against the border, is carried on
        /// along the run before it instead of making two patch corners.
        /// </summary>
        public static float layoutBorderMinRunCells = 2f;

        // Per border edge (key min/max), the coordinate held constant along it in its face's chart:
        // 1 where the run goes along the face's u axis, 0 along v.
        static Dictionary<long, int> BorderRuns(RemeshTopology topology, Vector3[] fr, Vector3[] fn,
            float[] spacing, float runCells, float minRunCells, ParametrizationStats st)
        {
            var T = topology.triangles;
            var P = topology.positions;
            var m = T.Length / 3;
            var count = new Dictionary<long, int>(3 * m, EdgeKey.Comparer);
            for (var h = 0; h < 3 * m; h++)
            {
                int a = T[h], b = T[3 * (h / 3) + (h % 3 + 1) % 3];
                var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                count[key] = count.TryGetValue(key, out var c) ? c + 1 : 1;
            }
            // Border half-edges in chains: each followed by the one leaving its head, where that head
            // has one; a vertex where two border loops touch (a bow-tie, common on scans) ends one.
            var outOf = new Dictionary<int, List<int>>();
            var borderHalf = new List<int>();
            for (var h = 0; h < 3 * m; h++)
            {
                int a = T[h], b = T[3 * (h / 3) + (h % 3 + 1) % 3];
                var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                if (count[key] != 1) continue;
                borderHalf.Add(h);
                (outOf.TryGetValue(a, out var l) ? l : outOf[a] = new List<int>(1)).Add(h);
            }
            var succ = new Dictionary<int, int>();
            var hasPred = new HashSet<int>();
            foreach (var h in borderHalf)
            {
                var head = T[3 * (h / 3) + (h % 3 + 1) % 3];
                var s = outOf.TryGetValue(head, out var l) && l.Count == 1 ? l[0] : -1;
                succ[h] = s;
                if (s >= 0) hasPred.Add(s);
            }
            var result = new Dictionary<long, int>(EdgeKey.Comparer);
            var visited = new HashSet<int>();
            var chainsFound = new List<(List<int> edges, bool closed)>();
            foreach (var h0 in borderHalf)
            {
                if (hasPred.Contains(h0) || visited.Contains(h0)) continue;
                var chain = new List<int>();
                for (var h = h0; h >= 0 && visited.Add(h); h = succ[h]) chain.Add(h);
                chainsFound.Add((chain, false));
            }
            foreach (var h0 in borderHalf)
            {
                if (visited.Contains(h0)) continue;
                var chain = new List<int>();
                for (var h = h0; h >= 0 && visited.Add(h); h = succ[h]) chain.Add(h);
                chainsFound.Add((chain, true));
            }
            foreach (var (loop, closed) in chainsFound)
            {
                if (loop.Count == 0) continue;
                st.borderLoops++;
                var k = loop.Count;
                // Arc length at each edge's start.
                var at = new double[k + 1];
                for (var i = 0; i < k; i++)
                {
                    var h = loop[i];
                    at[i + 1] = at[i] + (P[T[3 * (h / 3) + (h % 3 + 1) % 3]] - P[T[h]]).magnitude;
                }
                var total = at[k];
                Vector3 PointAt(double s)
                {
                    if (closed) { s %= total; if (s < 0) s += total; }
                    else s = Math.Max(0, Math.Min(total, s));
                    int lo = 0, hi = k - 1;
                    while (lo < hi) { var mid = (lo + hi + 1) / 2; if (at[mid] <= s) lo = mid; else hi = mid - 1; }
                    var h = loop[lo];
                    var len = at[lo + 1] - at[lo];
                    var w = len > 0 ? (float)((s - at[lo]) / len) : 0f;
                    return Vector3.Lerp(P[T[h]], P[T[3 * (h / 3) + (h % 3 + 1) % 3]], w);
                }
                // Per edge: its face, and the border's direction there smoothed over a cell (the chord
                // round it), which a zigzag of the scan's border does not turn.
                var face = new int[k];
                var along = new Vector3[k];
                var len = new double[k];
                var half = new double[k];
                for (var i = 0; i < k; i++)
                {
                    var h = loop[i];
                    var f = face[i] = h / 3;
                    int a = T[h], b = T[3 * f + (h % 3 + 1) % 3];
                    half[i] = 0.5 * runCells * 0.5 * (spacing[a] + spacing[b]);
                    len[i] = at[i + 1] - at[i];
                    var mid = 0.5 * (at[i] + at[i + 1]);
                    var chord = closed && half[i] * 2 >= total ? P[b] - P[a] : PointAt(mid + half[i]) - PointAt(mid - half[i]);
                    if (chord.sqrMagnitude <= 0) chord = P[b] - P[a];
                    along[i] = chord.normalized;
                    var dev = Mathf.Max(Mathf.Abs(Vector3.Dot(along[i], fr[f])), Mathf.Abs(Vector3.Dot(along[i], Vector3.Cross(fn[f], fr[f]))));
                    st.borderDeviationSum += Mathf.Acos(Mathf.Clamp01(dev)) * Mathf.Rad2Deg;
                    if (dev < 0.9f) st.borderMisaligned++;
                }
                // Each edge held along the field direction nearest the border's chord; a run is a
                // stretch held along one direction in space (charts may differ by a quarter turn
                // between faces, so the directions are compared, not the components). A run shorter
                // than layoutBorderMinRunCells is a wobble of the field against the border, not a
                // corner: it is carried on from the run before it, the shortest first.
                Vector3 HeldBy(int i, int comp) => comp == 1 ? fr[face[i]] : Vector3.Cross(fn[face[i]], fr[face[i]]);
                var comps = new int[k];
                for (var i = 0; i < k; i++)
                {
                    var f = face[i];
                    comps[i] = Mathf.Abs(Vector3.Dot(along[i], fr[f])) >= Mathf.Abs(Vector3.Dot(along[i], Vector3.Cross(fn[f], fr[f]))) ? 1 : 0;
                }
                // A direction of edge i's face carried into edge j's: turned with the normal, so a
                // border crossing a sharp crease compares its directions on the surface, not in space.
                Vector3 Carry(int i, int j, Vector3 v) => GlobalCrossField.Transport(v, fn[face[i]], fn[face[j]]);
                bool Breaks(int i) => (closed || i < k - 1)
                    && Mathf.Abs(Vector3.Dot(Carry(i, (i + 1) % k, HeldBy(i, comps[i])), HeldBy((i + 1) % k, comps[(i + 1) % k]))) < 0.7071f;
                List<List<int>> Runs()
                {
                    var list = new List<List<int>>();
                    var first = 0;
                    if (closed)
                    {
                        first = -1;
                        for (var i = 0; i < k; i++) if (Breaks(i)) { first = (i + 1) % k; break; }
                        if (first < 0) { var all = new List<int>(k); for (var i = 0; i < k; i++) all.Add(i); list.Add(all); return list; }
                    }
                    var cur = new List<int>();
                    for (var step = 0; step < k; step++)
                    {
                        var i = (first + step) % k;
                        cur.Add(i);
                        if (Breaks(i)) { list.Add(cur); cur = new List<int>(); }
                    }
                    if (cur.Count > 0) list.Add(cur);
                    return list;
                }
                var runList = Runs();
                for (var guard = 0; guard < k && runList.Count > 1; guard++)
                {
                    // The shortest run, against the cells it spans.
                    var shortest = -1;
                    var shortestCells = double.PositiveInfinity;
                    for (var r = 0; r < runList.Count; r++)
                    {
                        double length = 0, cells = 0;
                        foreach (var i in runList[r]) { length += len[i]; cells += len[i] / Math.Max(1e-12, 2 * half[i] / Math.Max(1e-6, runCells)); }
                        if (cells < minRunCells && cells < shortestCells) { shortestCells = cells; shortest = r; }
                    }
                    if (shortest < 0) break;
                    var run = runList[shortest];
                    // Carried on from the edge before it (for an open chain's first run, back from
                    // the edge after it).
                    var before = closed || shortest > 0 ? runList[(shortest - 1 + runList.Count) % runList.Count] : null;
                    if (before != null)
                    {
                        var from = before[before.Count - 1];
                        var prev = HeldBy(from, comps[from]);
                        foreach (var i in run)
                        {
                            prev = Carry(from, i, prev);
                            comps[i] = Mathf.Abs(Vector3.Dot(HeldBy(i, 1), prev)) >= Mathf.Abs(Vector3.Dot(HeldBy(i, 0), prev)) ? 1 : 0;
                            prev = HeldBy(i, comps[i]);
                            from = i;
                        }
                    }
                    else
                    {
                        var after = runList[shortest + 1];
                        var from = after[0];
                        var prev = HeldBy(from, comps[from]);
                        for (var j = run.Count - 1; j >= 0; j--)
                        {
                            var i = run[j];
                            prev = Carry(from, i, prev);
                            comps[i] = Mathf.Abs(Vector3.Dot(HeldBy(i, 1), prev)) >= Mathf.Abs(Vector3.Dot(HeldBy(i, 0), prev)) ? 1 : 0;
                            prev = HeldBy(i, comps[i]);
                            from = i;
                        }
                    }
                    var count0 = runList.Count;
                    runList = Runs();
                    if (runList.Count >= count0) { st.borderMergeStuck++; break; }
                }
                var anyCorner = runList.Count > 1;
                if (st.borderRunLog.Length < 600)
                    foreach (var r in runList)
                    {
                        double cells = 0;
                        foreach (var i in r) cells += len[i] / Math.Max(1e-12, 2 * half[i] / Math.Max(1e-6, runCells));
                        st.borderRunLog += cells.ToString("F1") + "/" + r.Count + " ";
                    }
                for (var i = 0; i < k; i++)
                {
                    var h = loop[i];
                    int a = T[h], b = T[3 * (h / 3) + (h % 3 + 1) % 3];
                    result[((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b)] = comps[i];
                    st.borderEdges++;
                }
                var runs = runList.Count;
                st.borderRuns += Math.Max(1, runs);
            }
            return result;
        }

        /// <summary>
        /// When the factorisation meets a vanishing pivot, factor again with every diagonal entry
        /// raised by this fraction of itself before falling back to conjugate gradients.
        /// </summary>
        public static double factorRetryShift = 1e-6;

        /// <summary>
        /// Analyse the factorisation again when the matrix was assembled with another pattern.
        /// Off: the first analysis is reused, as before (wrong values once the pattern changed).
        /// </summary>
        public static bool refactorOnNewPattern = true;

        /// <summary>Re-solves holding spiral loops' twist to whole cells (see the layout pass).</summary>
        public static int layoutLoopPasses = 2;

        /// <summary>
        /// As <see cref="layoutLoopPasses"/>, on a mesh with an open border (a scan, a symmetry
        /// half). Measured there they cost a solve, an untangle and a layout each for nothing: the
        /// ccnb scan 68 -> 28 s with irregular 5.43 -> 5.14 %, p05 47.8 -> 49.1; the body's half
        /// 1.64 -> 1.58 %.
        /// </summary>
        public static int layoutLoopPassesOpen = 0;

        /// <summary>Closure weight when the layout is meshed: its separatrices cross every seam.</summary>
        public static float layoutClosureWeight = 1e5f;

        /// <summary>Untangling rounds of the free map when the layout is meshed (it needs no folds).</summary>
        public static int layoutUntangleRounds = 30;

        static int FreeUntangleRounds => IntegerGridMap.parametrizationLayout
            ? Math.Max(untangleFreeRounds, layoutUntangleRounds) : untangleFreeRounds;

        /// <summary>Smallest width, in cells, of a crease triangle before its creases are let go.</summary>
        public static float layoutSquashLimit = 0.15f;

        /// <summary>Rounds of letting go of crushing creases (0 = off).</summary>
        public static int layoutSquashRounds = 2;

        /// <summary>With the layout meshed, skip rounding this map's own integers.</summary>
        public static bool layoutSkipRounding = true;

        /// <summary>Crease row weight when the layout is meshed (creases become layout arcs).</summary>
        public static float layoutFeatureWeight = 1e5f;

        /// <summary>Diagnostics: trace the quad layout of the free map (see QuantizedLayout).</summary>
        public static bool layoutDiagnostics = false;

        /// <summary>
        /// With the layout, later passes over the same mesh and field take the first pass's
        /// singularity cancellations (see step 2b), so that its free solve can be reused (head at
        /// 5k: the second pass 18 s -> 1.6 s). Not on the rounding path: there it moved the head's
        /// Hausdorff at 3.5k from 0.0122 to 0.0139.
        /// </summary>
        public static bool reuseStructure = true;

        /// <summary>Cancel adjacent opposite singularities (see step 2b).</summary>
        public static bool cancelDipoles = true;

        /// <summary>Compare faces through a shared vertex's cross (see the combing step).</summary>
        public static bool useVertexBridge = true;

        /// <summary>
        /// Solve by sparse LDL^T (nested dissection) instead of conjugate gradients. See SparseLdlt.
        /// </summary>
        public static bool direct = true;

        struct Term
        {
            public int block;
            public int rot;    // 0 or 1: R(rot), quarter turns CCW; R(2) = -R(0) is folded into sign
            public int sign;   // +1 or -1
        }

        /// <summary>
        /// Solves the parametrisation and returns the per-vertex lattice node, axis and spacing in
        /// the form the integer grid map reads from a position field.
        /// </summary>
        public static void Solve(RemeshTopology topology, Vector3[] crossField,
            Func<int, float> spacingAt,
            out Vector3[] latticeNodes, out Vector3[] axes, out float[] spacing,
            out ParametrizationStats stats)
        {
            Solve(topology, crossField, spacingAt, null, null, out latticeNodes, out axes, out spacing,
                out _, out _, out _, out _, out _, out _, out _, out _, out stats);
        }

        /// <summary>
        /// As above, plus per triangle corner its rounded lattice node and chart coordinates, and
        /// per face the chart's u direction -- what the integer grid map reads its offsets from.
        /// </summary>
        public static void Solve(RemeshTopology topology, Vector3[] crossField,
            Func<int, float> spacingAt, List<(int a, int b)> featureEdges, ParametrizationCache cache,
            out Vector3[] latticeNodes, out Vector3[] axes, out float[] spacing,
            out Vector2Int[] cornerNodes, out Vector2[] cornerUv, out double[] cornerUvExact, out Vector3[] faceUAxis,
            out int[] cornerRank, out int[] singularFaces,
            out int[] halfEdgeAcross, out int[] halfEdgeTurn, out ParametrizationStats stats)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats = new ParametrizationStats();
            var P = topology.positions;
            var N = topology.normals;
            var T = topology.triangles;
            var n = P.Length;
            var m = T.Length / 3;
            stats.faces = m;

            latticeNodes = (Vector3[])P.Clone();
            axes = (Vector3[])crossField.Clone();
            spacing = new float[n];
            for (var i = 0; i < n; i++) spacing[i] = spacingAt(i);
            var vertexSpacing = spacing;
            cornerNodes = new Vector2Int[3 * m];
            cornerUv = new Vector2[3 * m];
            cornerUvExact = new double[6 * m];
            faceUAxis = new Vector3[m];
            cornerRank = new int[3 * m];
            for (var i = 0; i < cornerRank.Length; i++) cornerRank[i] = -1;
            singularFaces = Array.Empty<int>();
            halfEdgeAcross = new int[3 * m];
            halfEdgeTurn = new int[3 * m];
            if (m == 0) return;

            // --- faces: normal, frame axis, size -------------------------------------------------
            var fn = new Vector3[m];
            var fr = new Vector3[m];
            var fh = new float[m];
            var fArea = new float[m];
            for (var f = 0; f < m; f++)
            {
                var a = P[T[3 * f]];
                var b = P[T[3 * f + 1]];
                var c = P[T[3 * f + 2]];
                var cr = Vector3.Cross(b - a, c - a);
                fArea[f] = 0.5f * cr.magnitude;
                fn[f] = MeshMath.SafeNormalize(cr, N[T[3 * f]]);
                // The face's cross: its corners' crosses combed onto the face plane.
                var r = Project(crossField[T[3 * f]], fn[f]);
                for (var k = 1; k < 3; k++)
                {
                    OrientationField.Compat4(r, fn[f], Project(crossField[T[3 * f + k]], fn[f]), fn[f],
                        out var ca, out var cb);
                    r = MeshMath.SafeNormalize(ca * k + cb, ca);
                }
                fr[f] = MeshMath.SafeNormalize(r, OrientationField.ArbitraryTangent(fn[f]));
                fh[f] = (spacingAt(T[3 * f]) + spacingAt(T[3 * f + 1]) + spacingAt(T[3 * f + 2])) / 3f;
            }

            stats.split += "0:" + watch.ElapsedMilliseconds + " ";
            // --- edges: manifold interior edges with their two faces -----------------------------
            // across[3f+k] = the face on the other side of edge (corner k -> corner k+1), or -1.
            var across = new int[3 * m];
            var acrossCorner = new int[3 * m];
            var edgeOf = new int[3 * m];
            for (var i = 0; i < across.Length; i++) { across[i] = -1; edgeOf[i] = -1; }
            {
                var directed = new Dictionary<long, int>(3 * m, EdgeKey.Comparer);
                var undirectedCount = new Dictionary<long, int>(3 * m, EdgeKey.Comparer);
                for (var f = 0; f < m; f++)
                    for (var k = 0; k < 3; k++)
                    {
                        int a = T[3 * f + k], b = T[3 * f + (k + 1) % 3];
                        if (a == b) continue;
                        var key = ((long)a << 32) | (uint)b;
                        directed[key] = directed.ContainsKey(key) ? -2 : 3 * f + k;
                        var u = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                        undirectedCount[u] = undirectedCount.TryGetValue(u, out var c) ? c + 1 : 1;
                    }
                for (var f = 0; f < m; f++)
                    for (var k = 0; k < 3; k++)
                    {
                        int a = T[3 * f + k], b = T[3 * f + (k + 1) % 3];
                        if (a == b) continue;
                        var u = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                        if (undirectedCount[u] != 2) continue;
                        if (directed[((long)a << 32) | (uint)b] < 0) continue;
                        if (!directed.TryGetValue(((long)b << 32) | (uint)a, out var other) || other < 0) continue;
                        across[3 * f + k] = other / 3;
                        acrossCorner[3 * f + k] = other % 3;
                    }
            }
            // Undirected edge ids for the interior manifold edges, with endpoints and faces.
            var edgeA = new List<int>();
            var edgeB = new List<int>();
            var edgeF = new List<int>();   // face holding a -> b
            var edgeG = new List<int>();   // face holding b -> a
            for (var f = 0; f < m; f++)
                for (var k = 0; k < 3; k++)
                {
                    var g = across[3 * f + k];
                    if (g < 0 || edgeOf[3 * f + k] >= 0) continue;
                    var id = edgeA.Count;
                    edgeA.Add(T[3 * f + k]);
                    edgeB.Add(T[3 * f + (k + 1) % 3]);
                    edgeF.Add(f);
                    edgeG.Add(g);
                    edgeOf[3 * f + k] = id;
                    edgeOf[3 * g + acrossCorner[3 * f + k]] = id;
                }
            var edges = edgeA.Count;

            var edgesAt0 = new List<int>[n];
            for (var e = 0; e < edges; e++)
            {
                (edgesAt0[edgeA[e]] ??= new List<int>(6)).Add(e);
                (edgesAt0[edgeB[e]] ??= new List<int>(6)).Add(e);
            }

            stats.split += "1:" + watch.ElapsedMilliseconds + " ";
            // --- 1. comb along a dual spanning tree ---------------------------------------------
            // Faces are compared THROUGH a vertex cross, not by carrying one face's cross over the
            // hinge onto the other. On a bumpy scan the hinge carry accumulates the surface's own
            // turning round every bump, and that came out as singularity pairs the field never had
            // (helmet head: 914 against the field's 716; ccnb 3762 against 2114). Through the
            // shared edge's lower vertex, the turn is the one the field was solved with.
            int CornerTurn(int f, int v) => BestTurn(Project(crossField[v], fn[f]), fr[f], fn[f]);
            int RawJump(int e, int from, int to)
            {
                var bridge = Math.Min(edgeA[e], edgeB[e]);
                return Mod4(CornerTurn(to, bridge) - CornerTurn(from, bridge));
            }
            var visited = new bool[m];
            var treeEdge = new bool[edges];
            var component = new int[m];
            var componentCount = 0;
            var queue = new Queue<int>();
            for (var s = 0; s < m; s++)
            {
                if (visited[s]) continue;
                visited[s] = true;
                component[s] = componentCount;
                queue.Enqueue(s);
                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    for (var k = 0; k < 3; k++)
                    {
                        var g = across[3 * f + k];
                        if (g < 0 || visited[g]) continue;
                        visited[g] = true;
                        component[g] = componentCount;
                        var e0 = edgeOf[3 * f + k];
                        treeEdge[e0] = true;
                        fr[g] = Rotate(fr[g], fn[g], useVertexBridge
                            ? -RawJump(e0, f, g)
                            : -BestTurn(GlobalCrossField.Transport(fr[f], fn[f], fn[g]), fr[g], fn[g]));
                        queue.Enqueue(g);
                    }
                }
                componentCount++;
            }

            // Jump from face F to face G of each edge: r_G ~ R^jump (carried r_F).
            var jump = new int[edges];
            for (var e = 0; e < edges; e++)
            {
                if (treeEdge[e]) continue;
                jump[e] = useVertexBridge
                    ? RawJump(e, edgeF[e], edgeG[e])
                    : BestTurn(GlobalCrossField.Transport(fr[edgeF[e]], fn[edgeF[e]], fn[edgeG[e]]),
                        fr[edgeG[e]], fn[edgeG[e]]);
            }
            int Jump(int e, int from) => from == edgeF[e] ? jump[e] : Mod4(-jump[e]);

            stats.split += "2:" + watch.ElapsedMilliseconds + " ";
            // --- 2. vertex fans, boundary and singularity ---------------------------------------
            var cornersOf = new List<int>[n];
            for (var c = 0; c < 3 * m; c++)
            {
                var v = T[c];
                (cornersOf[v] ??= new List<int>(6)).Add(c);
            }
            var boundary = new bool[n];
            var singular = new bool[n];
            var index = new int[n];
            // Sum of jumps round v's fan, or -1 for a boundary / non-manifold fan.
            int FanIndex(int v)
            {
                var list = cornersOf[v];
                var start = list[0];
                var c = start;
                var sum = 0;
                var steps = 0;
                while (steps++ <= list.Count)
                {
                    var f = c / 3;
                    var k = c % 3;
                    var prev = 3 * f + (k + 2) % 3;
                    var g = across[prev];
                    if (g < 0) return -1;
                    sum += Jump(edgeOf[prev], f);
                    c = 3 * g + acrossCorner[prev];
                    if (c == start) return steps == list.Count ? Mod4(sum) : -1;
                }
                return -1;
            }
            for (var v = 0; v < n; v++)
            {
                var list = cornersOf[v];
                if (list == null) continue;
                // Walk the fan through the edge to the previous corner; closed if it returns.
                var start = list[0];
                var c = start;
                var sum = 0;
                var steps = 0;
                var closed = false;
                while (steps++ <= list.Count)
                {
                    var f = c / 3;
                    var k = c % 3;
                    var prev = 3 * f + (k + 2) % 3;          // edge (prev corner -> v)
                    var g = across[prev];
                    if (g < 0) break;
                    sum += Jump(edgeOf[prev], f);
                    // In g that edge runs v -> prev; v's corner is its tail.
                    var cg = 3 * g + acrossCorner[prev];
                    c = cg;
                    if (c == start) { closed = true; break; }
                }
                if (!closed || steps != list.Count) { boundary[v] = true; continue; }
                index[v] = Mod4(sum);
                if (index[v] != 0) singular[v] = true;
            }

            stats.split += "3:" + watch.ElapsedMilliseconds + " ";
            // --- 2b. cancel adjacent singularity pairs -------------------------------------------
            // A +1/4 and a -1/4 singularity one edge apart are a valence 3 and a valence 5 vertex
            // side by side, with a row of sheared quads off them: the shear band. Their indices are
            // sums of the jumps round their fans, and the edge between them is in both fans,
            // walked in opposite directions -- so one quarter-turn more on that edge takes one off
            // each and both vanish, while every other vertex's sum is untouched. The edge is then
            // a cut with its own translation. The parametrisation carries the turn the field did
            // not quite make; over one edge that is a small distortion against a band of shear.
            var forced = new bool[edges];
            // A later pass over the same mesh and field (the face-count search at another size)
            // takes the first pass's cancellations: decided again at the new size they differ by a
            // pair or two, the cut graph and its unknowns with them, and the solve cannot be reused.
            var structureReused = reuseStructure && IntegerGridMap.parametrizationLayout && cache != null && ReferenceEquals(cache.topology, topology)
                && ReferenceEquals(cache.crossField, crossField) && cache.jump != null && cache.jump.Length == edges
                && cache.forced != null && cache.forced.Length == edges;
            if (structureReused)
            {
                Array.Copy(cache.jump, jump, edges);
                Array.Copy(cache.forced, forced, edges);
                for (var v = 0; v < n; v++)
                {
                    if (boundary[v] || cornersOf[v] == null) continue;
                    var k = FanIndex(v);
                    index[v] = k < 0 ? 0 : k;
                    singular[v] = index[v] != 0;
                }
                stats.reusedStructure = true;
                goto structureDone;
            }
            if (cancelDipoles)
            {
                for (var v = 0; v < n; v++)
                {
                    if (!singular[v] || edgesAt0[v] == null) continue;
                    foreach (var e in edgesAt0[v])
                    {
                        var w = edgeA[e] == v ? edgeB[e] : edgeA[e];
                        if (!singular[w] || Mod4(index[v] + index[w]) != 0) continue;
                        var saved = jump[e];
                        var cancelled = false;
                        for (var delta = -1; delta <= 1 && !cancelled; delta += 2)
                        {
                            jump[e] = Mod4(saved + delta);
                            if (FanIndex(v) == 0 && FanIndex(w) == 0) cancelled = true;
                        }
                        if (!cancelled) { jump[e] = saved; continue; }
                        forced[e] = true;
                        singular[v] = singular[w] = false;
                        index[v] = index[w] = 0;
                        stats.cancelledPairs++;
                        break;
                    }
                }
            }
            // --- 2c. cancel near singularity pairs ---------------------------------------------
            // The same along a path: opposite singularities closer than cancelPairRadiusCells are a
            // fold waiting for the rounding -- both go to grid nodes, and the cells between them
            // have no room. One quarter-turn more on every edge of the shortest path between them
            // leaves every vertex inside the path its sum (the path enters and leaves it) and takes
            // one off each end. The path's edges become cuts.
            if (cancelPairRadiusCells > 0f)
            {
                var dist = new float[n];
                var via = new int[n];
                var touched = new List<int>();
                for (var i = 0; i < n; i++) { dist[i] = float.PositiveInfinity; via[i] = -1; }
                var heap = new SortedSet<(float d, int v)>();
                for (var v = 0; v < n; v++)
                {
                    if (!singular[v] || edgesAt0[v] == null) continue;
                    var radius = cancelPairRadiusCells * spacing[v];
                    foreach (var t in touched) { dist[t] = float.PositiveInfinity; via[t] = -1; }
                    touched.Clear();
                    heap.Clear();
                    dist[v] = 0f;
                    touched.Add(v);
                    heap.Add((0f, v));
                    var found = -1;
                    while (heap.Count > 0)
                    {
                        var (d, x0) = heap.Min;
                        heap.Remove(heap.Min);
                        if (d > dist[x0]) continue;
                        if (x0 != v && singular[x0] && Mod4(index[v] + index[x0]) == 0) { found = x0; break; }
                        // Paths run through regular interior vertices only.
                        if (x0 != v && (singular[x0] || boundary[x0])) continue;
                        foreach (var e in edgesAt0[x0])
                        {
                            var y = edgeA[e] == x0 ? edgeB[e] : edgeA[e];
                            var nd = d + (P[y] - P[x0]).magnitude;
                            if (nd > radius || nd >= dist[y]) continue;
                            if (float.IsPositiveInfinity(dist[y])) touched.Add(y);
                            dist[y] = nd;
                            via[y] = e;
                            heap.Add((nd, y));
                        }
                    }
                    if (found < 0) continue;
                    // The path from v to found, as edges in order from v.
                    var path = new List<int>();
                    for (var y = found; y != v; )
                    {
                        var e = via[y];
                        path.Add(e);
                        y = edgeA[e] == y ? edgeB[e] : edgeA[e];
                    }
                    path.Reverse();
                    if (path.Count < 2) continue;   // adjacent: step 2b's business
                    // Edge by edge: the turn that clears v first, then each one that restores the
                    // vertex just passed.
                    var saved = new int[path.Count];
                    for (var i = 0; i < path.Count; i++) saved[i] = jump[path[i]];
                    var ok = true;
                    var at = v;
                    for (var i = 0; i < path.Count && ok; i++)
                    {
                        var e = path[i];
                        // v is cleared, and every inner vertex returns to 0 once its second path
                        // edge is set.
                        var done = false;
                        for (var delta = -1; delta <= 1 && !done; delta += 2)
                        {
                            jump[e] = Mod4(saved[i] + delta);
                            if (FanIndex(at) == 0) done = true;
                            if (!done) jump[e] = saved[i];
                        }
                        if (!done) ok = false;
                        at = edgeA[e] == at ? edgeB[e] : edgeA[e];
                    }
                    if (ok && FanIndex(found) != 0) ok = false;
                    if (!ok)
                    {
                        for (var i = 0; i < path.Count; i++) jump[path[i]] = saved[i];
                        stats.pairCancelFailed++;
                        continue;
                    }
                    foreach (var e in path) forced[e] = true;
                    singular[v] = singular[found] = false;
                    index[v] = index[found] = 0;
                    stats.cancelledFarPairs++;
                }
            }

            // --- 2d. spread singularities ----------------------------------------------------------
            // Two singularities on grid nodes are a whole number of cells apart -- the map is
            // seamless -- so two that the field put a fraction of a cell apart round onto one node,
            // or a cell apart with the cells between them stretched: either way their fans fold.
            // On the body at 2k the 35 singularities with another within half a cell carry half
            // the folds; those more than a cell and a half from any other carry almost none. The
            // opposite pairs are cancelled above; what is left is pairs of the same sign, and
            // fans that turn by two or more quarters at one vertex. A fan's index is only known
            // mod 4: the jumps say how the frame comes back, not how far it turned on the way, and
            // at +-2 that is the whole answer -- a 180-degree cone and a 540-degree one, equally
            // far from the surface's 360, and the least squares settles on neither: the fan
            // collapses before anything is rounded. The turn itself -- the frame's rotation
            // against parallel transport round the fan, plus the angle defect -- says which.
            // Both are spread the way a quad mesh has them: one quarter at a vertex, the next a
            // cell away along a grid axis, moved there along a path as the pair cancellation does.
            structureDone:
            var trueTurn = new double[n];
            var cellOf = spacing;
            {
                var feature = new bool[n];
                if (featureEdges != null)
                    foreach (var (a, b) in featureEdges)
                    {
                        if (a >= 0 && a < n) feature[a] = true;
                        if (b >= 0 && b < n) feature[b] = true;
                    }
                // Quarter turns of the field round v's fan, or NaN for an open fan.
                double TrueTurn(int v)
                {
                    var list = cornersOf[v];
                    if (list == null) return double.NaN;
                    var start = list[0];
                    var c = start;
                    double turn = 0, angles = 0;
                    var steps = 0;
                    while (steps++ <= list.Count)
                    {
                        var f = c / 3;
                        var k = c % 3;
                        var pa = P[T[3 * f + (k + 1) % 3]] - P[v];
                        var pb = P[T[3 * f + (k + 2) % 3]] - P[v];
                        angles += Vector3.Angle(pa, pb) * Mathf.Deg2Rad;
                        var prev = 3 * f + (k + 2) % 3;
                        var g = across[prev];
                        if (g < 0) return double.NaN;
                        var t = GlobalCrossField.Transport(fr[f], fn[f], fn[g]);
                        var d = Math.Atan2(Vector3.Dot(Vector3.Cross(t, fr[g]), fn[g]), Vector3.Dot(t, fr[g]));
                        // Measured against the jump the fan actually uses, whichever way it was matched.
                        var r = d - 0.5 * Math.PI * Jump(edgeOf[prev], f);
                        r -= 2 * Math.PI * Math.Round(r / (2 * Math.PI));
                        turn += r;
                        c = 3 * g + acrossCorner[prev];
                        if (c == start) return steps == list.Count ? (turn + 2 * Math.PI - angles) / (0.5 * Math.PI) : double.NaN;
                    }
                    return double.NaN;
                }
                int Quarters(int v) => double.IsNaN(trueTurn[v]) ? 0 : (int)Math.Round(trueTurn[v]);
                for (var v = 0; v < n; v++) trueTurn[v] = singular[v] ? TrueTurn(v) : 0;

                if (spreadSingularities)
                {
                    var dist = new float[n];
                    var via = new int[n];
                    var touched = new List<int>();
                    for (var i = 0; i < n; i++) { dist[i] = float.PositiveInfinity; via[i] = -1; }
                    var heap = new SortedSet<(float d, int v)>();
                    var singularList = new List<int>();
                    for (var v = 0; v < n; v++) if (singular[v] && !double.IsNaN(trueTurn[v])) singularList.Add(v);

                    // The vertex, reachable from `from` through regular vertices, nearest a cell from
                    // `anchor` along one of its grid axes and clear of every singularity but `from`
                    // and `anchor`; -1 if none is within half a cell of such a spot.
                    int Spot(int from, int anchor, bool fromStays, out List<int> path, out double cost)
                    {
                        path = null;
                        cost = double.PositiveInfinity;
                        var h = cellOf[anchor];
                        var fa = cornersOf[anchor][0] / 3;
                        var ax0 = Project(fr[fa], fn[fa]).normalized;
                        var ax1 = Vector3.Cross(fn[fa], ax0);
                        foreach (var t in touched) { dist[t] = float.PositiveInfinity; via[t] = -1; }
                        touched.Clear();
                        heap.Clear();
                        dist[from] = 0f;
                        touched.Add(from);
                        heap.Add((0f, from));
                        var best = -1;
                        while (heap.Count > 0)
                        {
                            var (d, x0) = heap.Min;
                            heap.Remove(heap.Min);
                            if (d > dist[x0]) continue;
                            if (x0 != from)
                            {
                                var off = P[x0] - P[anchor];
                                var miss = Math.Min(Math.Min((off - h * ax0).magnitude, (off + h * ax0).magnitude),
                                    Math.Min((off - h * ax1).magnitude, (off + h * ax1).magnitude)) / h;
                                if (miss < cost)
                                {
                                    var clear = true;
                                    foreach (var o in singularList)
                                    {
                                        if (o == anchor || (o == from && !fromStays) || !singular[o]) continue;
                                        if ((P[o] - P[x0]).magnitude < spreadMinCells * h) { clear = false; break; }
                                    }
                                    if (clear) { cost = miss; best = x0; }
                                }
                            }
                            foreach (var e in edgesAt0[x0])
                            {
                                var y = edgeA[e] == x0 ? edgeB[e] : edgeA[e];
                                if (singular[y] || boundary[y] || feature[y] || edgesAt0[y] == null) continue;
                                var nd = d + (P[y] - P[x0]).magnitude;
                                if (nd > 2.5f * h || nd >= dist[y]) continue;
                                if (float.IsPositiveInfinity(dist[y])) touched.Add(y);
                                dist[y] = nd;
                                via[y] = e;
                                heap.Add((nd, y));
                            }
                        }
                        if (best < 0 || cost > 0.5) return -1;
                        path = new List<int>();
                        for (var y = best; y != from; )
                        {
                            var e = via[y];
                            path.Add(e);
                            y = edgeA[e] == y ? edgeB[e] : edgeA[e];
                        }
                        path.Reverse();
                        return best;
                    }

                    // One quarter of `from`'s turn, of sign sgn, moved along the path to `to`.
                    bool Move(int from, int to, int sgn, List<int> path)
                    {
                        var saved = new int[path.Count];
                        for (var i = 0; i < path.Count; i++) saved[i] = jump[path[i]];
                        var keep = Mod4(-(Quarters(from) - sgn));
                        var ok = true;
                        var at = from;
                        for (var i = 0; i < path.Count && ok; i++)
                        {
                            var e = path[i];
                            var want = i == 0 ? keep : 0;
                            var done = false;
                            for (var delta = -1; delta <= 1 && !done; delta += 2)
                            {
                                jump[e] = Mod4(saved[i] + delta);
                                if (FanIndex(at) == want) done = true;
                                if (!done) jump[e] = saved[i];
                            }
                            if (!done) ok = false;
                            at = edgeA[e] == at ? edgeB[e] : edgeA[e];
                        }
                        if (ok && FanIndex(to) != Mod4(-sgn)) ok = false;
                        if (!ok)
                        {
                            for (var i = 0; i < path.Count; i++) jump[path[i]] = saved[i];
                            return false;
                        }
                        foreach (var e in path) forced[e] = true;
                        trueTurn[from] = Quarters(from) - sgn;
                        index[from] = keep;
                        singular[from] = keep != 0;
                        trueTurn[to] = sgn;
                        index[to] = Mod4(-sgn);
                        singular[to] = true;
                        singularList.Add(to);
                        return true;
                    }

                    for (var pass = 0; pass < spreadPasses; pass++)
                    {
                        var moved = 0;
                        // Multiple turns at one vertex: keep one quarter, pass the others on.
                        foreach (var v in singularList.ToArray())
                        {
                            while (singular[v] && Math.Abs(Quarters(v)) >= 2)
                            {
                                if (Mod4(-Quarters(v)) != index[v]) { stats.splitMismatch++; break; }
                                var sgn = Math.Sign(Quarters(v));
                                var to = Spot(v, v, true, out var path, out _);
                                if (to < 0 || !Move(v, to, sgn, path)) { stats.splitFailed++; break; }
                                stats.splitSingularities++;
                                moved++;
                            }
                        }
                        // Pairs of the same sign closer than spreadMinCells: the one with the better
                        // spot moves there, a cell from the other.
                        var pairs = new List<(float d, int a, int b)>();
                        for (var i = 0; i < singularList.Count; i++)
                        {
                            var a = singularList[i];
                            if (!singular[a]) continue;
                            for (var j = i + 1; j < singularList.Count; j++)
                            {
                                var b = singularList[j];
                                if (!singular[b] || a == b) continue;
                                var d = (P[a] - P[b]).magnitude;
                                if (d >= spreadMinCells * Math.Min(spacing[a], spacing[b])) continue;
                                if (Math.Sign(Quarters(a)) != Math.Sign(Quarters(b))) continue;
                                pairs.Add((d, a, b));
                            }
                        }
                        pairs.Sort((x, y) => x.d.CompareTo(y.d));
                        foreach (var (d0, a, b) in pairs)
                        {
                            if (!singular[a] || !singular[b] || (P[a] - P[b]).magnitude != d0) continue;
                            if (Math.Abs(Quarters(a)) != 1 && Math.Abs(Quarters(b)) != 1) continue;
                            // Move a whole unit singularity only.
                            int toA = -1, toB = -1;
                            List<int> pathA = null, pathB = null;
                            double costA = double.PositiveInfinity, costB = double.PositiveInfinity;
                            if (Math.Abs(Quarters(a)) == 1) toA = Spot(a, b, false, out pathA, out costA);
                            if (Math.Abs(Quarters(b)) == 1) toB = Spot(b, a, false, out pathB, out costB);
                            var ok = false;
                            if (toA >= 0 && costA <= costB) ok = Move(a, toA, Math.Sign(Quarters(a)), pathA);
                            else if (toB >= 0) ok = Move(b, toB, Math.Sign(Quarters(b)), pathB);
                            if (ok) { stats.spreadMoves++; moved++; }
                            else stats.spreadFailed++;
                        }
                        if (moved == 0) break;
                    }
                }
                for (var v = 0; v < n; v++)
                    if (singular[v] && Math.Abs(Quarters(v)) >= 2) stats.multipleSingularities++;
            }

            for (var v = 0; v < n; v++) if (singular[v]) stats.singularVertices++;
            if (cache != null && !structureReused)
            {
                cache.jump = (int[])jump.Clone();
                cache.forced = (bool[])forced.Clone();
            }

            stats.split += "4:" + watch.ElapsedMilliseconds + " ";
            // --- 3. cut graph -------------------------------------------------------------------
            var cut = new bool[edges];
            var cutDegree = new int[n];
            var edgesAt = new List<int>[n];
            for (var e = 0; e < edges; e++)
            {
                (edgesAt[edgeA[e]] ??= new List<int>(6)).Add(e);
                (edgesAt[edgeB[e]] ??= new List<int>(6)).Add(e);
                if (treeEdge[e] && !forced[e]) continue;
                cut[e] = true;
                cutDegree[edgeA[e]]++;
                cutDegree[edgeB[e]]++;
                stats.cutEdgesBeforePrune++;
            }
            bool Prunable(int v) => cutDegree[v] == 1 && !singular[v] && !boundary[v];
            var prune = new Queue<int>();
            for (var v = 0; v < n; v++) if (Prunable(v)) prune.Enqueue(v);
            while (prune.Count > 0)
            {
                var v = prune.Dequeue();
                if (!Prunable(v)) continue;
                foreach (var e in edgesAt[v])
                {
                    if (!cut[e]) continue;
                    if (forced[e]) break;
                    cut[e] = false;
                    var w = edgeA[e] == v ? edgeB[e] : edgeA[e];
                    cutDegree[v]--;
                    cutDegree[w]--;
                    if (Prunable(w)) prune.Enqueue(w);
                    break;
                }
            }
            for (var e = 0; e < edges; e++) if (cut[e]) stats.cutEdges++;

            stats.split += "5:" + watch.ElapsedMilliseconds + " ";
            // --- 4. chains: one translation each -------------------------------------------------
            var chainOf = new int[edges];
            var leftFace = new int[edges];
            for (var e = 0; e < edges; e++) chainOf[e] = -1;
            bool PassThrough(int v) => cutDegree[v] == 2 && !singular[v] && !boundary[v];
            var chains = 0;
            var chainEndA = new List<int>();
            var chainEndB = new List<int>();
            void Walk(int from, int e0)
            {
                var c = chains++;
                chainEndA.Add(from);
                chainEndB.Add(from);
                var x = from;
                var e = e0;
                while (e >= 0 && chainOf[e] < 0)
                {
                    chainOf[e] = c;
                    var y = edgeA[e] == x ? edgeB[e] : edgeA[e];
                    // Left of the walk x -> y is the face that holds the directed half-edge x -> y.
                    leftFace[e] = edgeA[e] == x ? edgeF[e] : edgeG[e];
                    chainEndB[c] = y;
                    if (!PassThrough(y)) break;
                    var next = -1;
                    foreach (var f in edgesAt[y])
                        if (cut[f] && chainOf[f] < 0) { next = f; break; }
                    x = y;
                    e = next;
                }
            }
            for (var v = 0; v < n; v++)
            {
                if (edgesAt[v] == null || cutDegree[v] == 0 || PassThrough(v)) continue;
                foreach (var e in edgesAt[v])
                    if (cut[e] && chainOf[e] < 0) Walk(v, e);
            }
            for (var e = 0; e < edges; e++)
                if (cut[e] && chainOf[e] < 0) Walk(edgeA[e], e);   // closed loops with no node
            stats.chains = chains;

            stats.structureMs = watch.ElapsedMilliseconds;
            // --- 5. corner expressions ----------------------------------------------------------
            // Blocks: [0, chains) are the chain translations; vertex fan components follow.
            // One translation per cut edge (the default) or per chain. Per edge, each translation
            // touches only the faces at its edge, and the chain's edges are held equal by the fan
            // closures at the vertices between them: every unknown is local and the factorisation's
            // nested dissection works. Per chain, one variable runs the length of a cut across the
            // surface, lands in every separator, and on the helmet head put 8.2 M of the factor's
            // 9.5 M non-zeros into the serial top of the elimination tree.
            var tBlock = new int[edges];
            var translationBlocks = 0;
            for (var e = 0; e < edges; e++)
                tBlock[e] = !cut[e] ? -1 : perEdgeTranslations ? translationBlocks++ : chainOf[e];
            if (!perEdgeTranslations) translationBlocks = chains;
            var blocks = translationBlocks;
            var cornerStart = new int[3 * m];
            var cornerCount = new int[3 * m];
            var pool = new List<Term>(3 * m + 1024);
            var closureRows = new List<Term[]>();
            var closureVertex = new List<int>();
            var cornerSet = new bool[3 * m];
            var masterOf = new int[n];
            var masterCorner = new int[n];
            for (var v = 0; v < n; v++) { masterOf[v] = -1; masterCorner[v] = -1; }
            var fanCorner = new Dictionary<int, int>();
            var closureFaceOf = new Dictionary<int, int>();
            var fanQueue = new Queue<int>();
            var scratch = new List<Term>(8);

            void Store(int corner, List<Term> terms)
            {
                cornerStart[corner] = pool.Count;
                cornerCount[corner] = terms.Count;
                pool.AddRange(terms);
                cornerSet[corner] = true;
            }
            List<Term> Load(int corner)
            {
                var list = new List<Term>(cornerCount[corner] + 1);
                for (var i = 0; i < cornerCount[corner]; i++) list.Add(pool[cornerStart[corner] + i]);
                return list;
            }
            // Crossing edge e from face `from` to the other: (u, v) -> R(-p)(u, v) + t from the left,
            // and the inverse from the right.
            List<Term> Cross(List<Term> terms, int e, int from)
            {
                if (!cut[e]) return terms;
                var p = Jump(e, leftFace[e]);
                var result = new List<Term>(terms.Count + 1);
                if (from == leftFace[e])
                {
                    foreach (var t in terms) result.Add(Turn(t, -p));
                    result.Add(new Term { block = tBlock[e], rot = 0, sign = 1 });
                }
                else
                {
                    foreach (var t in terms) result.Add(Turn(t, p));
                    result.Add(Turn(new Term { block = tBlock[e], rot = 0, sign = -1 }, p));
                }
                return Simplify(result);
            }

            for (var v = 0; v < n; v++)
            {
                var list = cornersOf[v];
                if (list == null) continue;
                fanCorner.Clear();
                foreach (var c in list) fanCorner[c / 3] = c;
                foreach (var startCorner in list)
                {
                    if (cornerSet[startCorner]) continue;
                    var block = blocks++;
                    if (masterOf[v] < 0) { masterOf[v] = block; masterCorner[v] = startCorner; }
                    scratch.Clear();
                    scratch.Add(new Term { block = block, rot = 0, sign = 1 });
                    Store(startCorner, scratch);
                    fanQueue.Clear();
                    fanQueue.Enqueue(startCorner);
                    while (fanQueue.Count > 0)
                    {
                        var c = fanQueue.Dequeue();
                        var f = c / 3;
                        var k = c % 3;
                        // The two edges of f at v: (v -> next) is half-edge c, (prev -> v) is c-1.
                        for (var side = 0; side < 2; side++)
                        {
                            var half = side == 0 ? c : 3 * f + (k + 2) % 3;
                            var g = across[half];
                            if (g < 0 || !fanCorner.TryGetValue(g, out var cg)) continue;
                            var e = edgeOf[half];
                            var carried = Cross(Load(c), e, f);
                            if (!cornerSet[cg])
                            {
                                Store(cg, carried);
                                fanQueue.Enqueue(cg);
                            }
                            else
                            {
                                // The fan closing on itself: what arrives must equal what is there.
                                var diff = Load(cg);
                                foreach (var t in carried) diff.Add(new Term { block = t.block, rot = t.rot, sign = -t.sign });
                                diff = Simplify(diff);
                                if (diff.Count > 0) { closureRows.Add(diff.ToArray()); closureVertex.Add(v); }
                                // Where the fan closes turned, the face it closes on is the one the
                                // grid map treats as singular: the singularity sits in it.
                                if (singular[v] && !closureFaceOf.ContainsKey(v)
                                    && MasterTurn(carried, block) != MasterTurn(Load(cg), block))
                                    closureFaceOf[v] = g;
                            }
                        }
                    }
                }
            }
            // Each closure was seen from both of its sides; one copy is enough but two only doubles
            // the weight, which the penalty does not care about.
            stats.blocks = blocks;
            stats.closureConstraints = closureRows.Count;
            {
                var faceSet = new HashSet<int>(closureFaceOf.Values);
                singularFaces = new int[faceSet.Count];
                faceSet.CopyTo(singularFaces);
                Array.Sort(singularFaces);
            }

            stats.expressionMs = watch.ElapsedMilliseconds;
            // --- 6. least squares: assemble the normal equations ---------------------------------
            var vars = 2 * blocks;
            var keys = new List<long>(40 * m);
            var vals = new List<double>(40 * m);
            var rhs = new double[vars];
            var rowVar = new List<int>(16);
            var rowCoef = new List<double>(16);

            void AddRow(double target, double weight)
            {
                // Merge duplicate variables, then accumulate J^T J and J^T b.
                for (var i = 0; i < rowVar.Count; i++)
                    for (var j = i + 1; j < rowVar.Count; j++)
                        if (rowVar[j] == rowVar[i]) { rowCoef[i] += rowCoef[j]; rowCoef[j] = 0; }
                var w2 = weight * weight;
                for (var i = 0; i < rowVar.Count; i++)
                {
                    if (rowCoef[i] == 0) continue;
                    rhs[rowVar[i]] += w2 * rowCoef[i] * target;
                    for (var j = 0; j < rowVar.Count; j++)
                    {
                        if (rowCoef[j] == 0) continue;
                        keys.Add(((long)rowVar[i] << 32) | (uint)rowVar[j]);
                        vals.Add(w2 * rowCoef[i] * rowCoef[j]);
                    }
                }
            }
            // Variable and coefficient of component `comp` (0 = u, 1 = v) of one term.
            void Emit(Term t, int comp, double scale)
            {
                if (t.rot == 0)
                {
                    rowVar.Add(2 * t.block + comp);
                    rowCoef.Add(scale * t.sign);
                }
                else
                {
                    // R(1)(x, y) = (-y, x)
                    rowVar.Add(2 * t.block + (comp == 0 ? 1 : 0));
                    rowCoef.Add(scale * t.sign * (comp == 0 ? -1 : 1));
                }
            }

            var faceE1 = new Vector3[m];
            var faceE2 = new Vector3[m];
            var gradX = new double[3 * m];
            var gradY = new double[3 * m];
            var stiff = new double[m];
            for (var f = 0; f < m; f++) stiff[f] = 1.0;
            for (var f = 0; f < m; f++)
            {
                var e1 = fr[f];
                var e2 = Vector3.Cross(fn[f], e1);
                faceE1[f] = e1;
                faceE2[f] = e2;
                var p0 = P[T[3 * f]];
                var d1 = P[T[3 * f + 1]] - p0;
                var d2 = P[T[3 * f + 2]] - p0;
                double e1x = Vector3.Dot(d1, e1), e1y = Vector3.Dot(d1, e2);
                double e2x = Vector3.Dot(d2, e1), e2y = Vector3.Dot(d2, e2);
                var det = e1x * e2y - e1y * e2x;
                if (Math.Abs(det) < 1e-30) continue;
                // grad = E^-1 (du1, du2): coefficient of corner 1 is (e2y, -e2x)/det, corner 2 (-e1y, e1x)/det.
                gradX[3 * f + 1] = e2y / det; gradY[3 * f + 1] = -e2x / det;
                gradX[3 * f + 2] = -e1y / det; gradY[3 * f + 2] = e1x / det;
                gradX[3 * f] = -(gradX[3 * f + 1] + gradX[3 * f + 2]);
                gradY[3 * f] = -(gradY[3 * f + 1] + gradY[3 * f + 2]);
            }

            // Vertices within featureSkipNearSingularRings rings of a singularity: a crease there is
            // left unconstrained (see the option).
            var nearPole = new bool[n];
            for (var v = 0; v < n; v++) nearPole[v] = singular[v];
            for (var ring = 0; ring < featureSkipNearSingularRings; ring++)
            {
                var next = (bool[])nearPole.Clone();
                for (var f = 0; f < m; f++)
                {
                    var any = nearPole[T[3 * f]] || nearPole[T[3 * f + 1]] || nearPole[T[3 * f + 2]];
                    if (!any) continue;
                    for (var k = 0; k < 3; k++) next[T[3 * f + k]] = true;
                }
                nearPole = next;
            }

            var firstAssembly = true;
            // Each constrained feature edge as the two pinned (vertex, master coordinate) slots it
            // ties, 2v + comp: a crease line in u and one in v meeting at a corner stay apart.
            var usedFeatureEdges = new List<(int a, int b)>();
            var constrainedFeatureEdges = new List<(int a, int b)>();
            Dictionary<long, int> borderRunComp = null;
            var loopRows = new List<(List<(Term t, double coef)> row, int comp, double target)>();
            HashSet<long> layoutDropped = null;
            var pinU = new bool[n];
            var pinV = new bool[n];
            var stA = stats;
            Csr Assemble()
            {
            keys = new List<long>(40 * m);
            vals = new List<double>(40 * m);
            Array.Clear(rhs, 0, rhs.Length);
            for (var f = 0; f < m; f++)
            {
                if (!(fArea[f] > 0f) || !(fh[f] > 0f)) continue;
                var w = Math.Sqrt(fArea[f] * stiff[f]);
                for (var comp = 0; comp < 2; comp++)
                    for (var d = 0; d < 2; d++)
                    {
                        rowVar.Clear();
                        rowCoef.Clear();
                        for (var k = 0; k < 3; k++)
                        {
                            var corner = 3 * f + k;
                            var g = d == 0 ? gradX[corner] : gradY[corner];
                            for (var i = 0; i < cornerCount[corner]; i++)
                                Emit(pool[cornerStart[corner] + i], comp, g);
                        }
                        AddRow(comp == d ? 1.0 / fh[f] : 0.0, w);
                    }
            }
            // Spiral loops (see the layout): held hard, like a closure.
            foreach (var (row, comp, target) in loopRows)
            {
                rowVar.Clear();
                rowCoef.Clear();
                foreach (var (t, coef) in row) Emit(t, comp, coef);
                AddRow(target, Math.Sqrt(1e5));
            }

            // Closures, in cells; weighted against a face's energy, which is about one per face.
            // The layout traces iso-lines across every seam: there the closures must hold exactly.
            var closureW = Math.Sqrt(IntegerGridMap.parametrizationLayout ? Math.Max(closureWeight, layoutClosureWeight) : closureWeight);
            foreach (var row in closureRows)
                for (var comp = 0; comp < 2; comp++)
                {
                    rowVar.Clear();
                    rowCoef.Clear();
                    foreach (var t in row) Emit(t, comp, 1.0);
                    AddRow(0.0, closureW);
                }

            // Features: a crease or border runs along one chart axis, so the other coordinate is
            // held equal at its two ends -- and later rounded, so the crease is a grid line. Each
            // feature vertex records which master-chart coordinate it pins.
            if (featureEdges != null)
            {
                // The layout lays its creases along iso-lines: there they must be exact.
                var featureW = Math.Sqrt(IntegerGridMap.parametrizationLayout ? Math.Max(featureWeight, layoutFeatureWeight) : featureWeight);
                var faceOfEdge = new Dictionary<long, int>(3 * m, EdgeKey.Comparer);
                for (var f = 0; f < m; f++)
                    for (var k = 0; k < 3; k++)
                    {
                        int a = T[3 * f + k], b = T[3 * f + (k + 1) % 3];
                        var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                        if (!faceOfEdge.ContainsKey(key)) faceOfEdge[key] = 3 * f + k;
                    }
                // For the layout, the open border is held along one grid direction per straight run,
                // not per edge: a scan's border zigzags edge to edge, and held edge by edge it is a
                // staircase in (u, v) whose every step sends a separatrix across the mesh. The
                // direction of each edge is that of the border's chord over a cell round it.
                if (IntegerGridMap.parametrizationLayout && layoutBorderRunCells > 0 && borderRunComp == null)
                    borderRunComp = BorderRuns(topology, fr, fn, vertexSpacing, layoutBorderRunCells, layoutBorderMinRunCells, stA);
                bool OnBorderRun(int a, int b) => borderRunComp != null && borderRunComp.ContainsKey(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b));

                // For the layout: two crease edges at a vertex less than 45 degrees apart -- a sharp
                // V in a zigzag crease -- are held along one grid direction, squash the triangle
                // between them flat and leave the vertex twice the same way. The less aligned of the
                // two is left free.
                HashSet<long> dropped = null;
                if (IntegerGridMap.parametrizationLayout && firstAssembly)
                {
                    dropped = new HashSet<long>();
                    var byVertex = new Dictionary<int, List<(int w, int comp, float align, long key)>>();
                    foreach (var (a, b) in featureEdges)
                    {
                        if (a == b || OnBorderRun(a, b)) continue;
                        var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                        if (!faceOfEdge.TryGetValue(key, out var half)) continue;
                        var f = half / 3;
                        var d = (P[b] - P[a]).normalized;
                        var alongU = Mathf.Abs(Vector3.Dot(d, fr[f]));
                        var alongV = Mathf.Abs(Vector3.Dot(d, Vector3.Cross(fn[f], fr[f])));
                        var align = Mathf.Max(alongU, alongV);
                        if (align < featureMinAlignment) continue;
                        var comp = alongU >= alongV ? 1 : 0;
                        (byVertex.TryGetValue(a, out var la) ? la : byVertex[a] = new List<(int, int, float, long)>()).Add((b, comp, align, key));
                        (byVertex.TryGetValue(b, out var lb) ? lb : byVertex[b] = new List<(int, int, float, long)>()).Add((a, comp, align, key));
                    }
                    foreach (var kv in byVertex)
                    {
                        var l = kv.Value;
                        for (var i = 0; i < l.Count; i++)
                            for (var j = i + 1; j < l.Count; j++)
                            {
                                if (dropped.Contains(l[i].key) || dropped.Contains(l[j].key)) continue;
                                // Less than 45 degrees apart: one grid direction for both, whichever
                                // coordinate each was given in its own triangle's frame.
                                var di = (P[l[i].w] - P[kv.Key]).normalized;
                                var dj = (P[l[j].w] - P[kv.Key]).normalized;
                                if (Vector3.Dot(di, dj) <= 0.7071f) continue;
                                dropped.Add(l[i].align < l[j].align ? l[i].key : l[j].key);
                                stA.featureEdgesDroppedV++;
                            }
                    }
                    layoutDropped = dropped;
                }
                // The border's edges, held by run, whether or not the caller listed them.
                var rowEdges = featureEdges;
                if (borderRunComp != null)
                {
                    rowEdges = new List<(int a, int b)>(featureEdges.Count + borderRunComp.Count);
                    foreach (var (a, b) in featureEdges)
                        if (!OnBorderRun(a, b)) rowEdges.Add((a, b));
                    foreach (var key in borderRunComp.Keys) rowEdges.Add(((int)(key >> 32), (int)(key & 0xffffffff)));
                }
                foreach (var (a, b) in rowEdges)
                {
                    if (a == b) continue;
                    var onRun = OnBorderRun(a, b);
                    if (!onRun && layoutDropped != null && layoutDropped.Contains(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b))) continue;
                    if (featureSkipNearSingularRings > 0 && (nearPole[a] || nearPole[b]))
                    {
                        if (firstAssembly) stA.featureEdgesNearPoles++;
                        continue;
                    }
                    var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    if (!faceOfEdge.TryGetValue(key, out var half)) continue;
                    var f = half / 3;
                    var ca = T[half] == a ? half : 3 * f + (half % 3 + 1) % 3;
                    var cb = T[half] == a ? 3 * f + (half % 3 + 1) % 3 : half;
                    var d = (P[b] - P[a]).normalized;
                    var alongU = Mathf.Abs(Vector3.Dot(d, fr[f]));
                    var alongV = Mathf.Abs(Vector3.Dot(d, Vector3.Cross(fn[f], fr[f])));
                    if (!onRun && Mathf.Max(alongU, alongV) < featureMinAlignment) { if (firstAssembly) stA.featureEdgesSkipped++; continue; }
                    // Running along u, v is constant; along v, u is.
                    var comp = onRun ? borderRunComp[key] : alongU >= alongV ? 1 : 0;
                    rowVar.Clear();
                    rowCoef.Clear();
                    for (var i = 0; i < cornerCount[ca]; i++) Emit(pool[cornerStart[ca] + i], comp, 1.0);
                    for (var i = 0; i < cornerCount[cb]; i++) Emit(pool[cornerStart[cb] + i], comp, -1.0);
                    AddRow(0.0, featureW);
                    if (!firstAssembly) continue;
                    stA.featureEdges++;
                    constrainedFeatureEdges.Add((a, b));
                    var slotA = -1;
                    var slotB = -1;
                    // Which master coordinate that is at each end: the corner's chart is the master's
                    // turned by k quarter-turns, and an odd k swaps u and v.
                    foreach (var c in new[] { ca, cb })
                    {
                        var v = T[c];
                        var turn = -1;
                        for (var i = 0; i < cornerCount[c]; i++)
                        {
                            var t = pool[cornerStart[c] + i];
                            if (t.block == masterOf[v]) { turn = t.rot; break; }
                        }
                        if (turn < 0) continue;
                        var masterComp = turn == 0 ? comp : 1 - comp;
                        if (masterComp == 0) pinU[v] = true; else pinV[v] = true;
                        if (c == ca) slotA = 2 * v + masterComp; else slotB = 2 * v + masterComp;
                    }
                    if (slotA >= 0 && slotB >= 0) usedFeatureEdges.Add((slotA, slotB));
                }
            }

            // Every variable gets a diagonal slot and a vanishing regulariser: a variable no row
            // touches (a translation whose chain no fan crossed) would otherwise leave the
            // factorisation a structurally zero pivot -- which it hit once pair cancellation began
            // making one-edge chains, and fell back to CG without a word.
            for (var i = 0; i < vars; i++)
            {
                keys.Add(((long)i << 32) | (uint)i);
                vals.Add(1e-9);
            }
            var built = Csr.FromTriplets(vars, keys, vals);
            keys = null;
            vals = null;
            firstAssembly = false;
            return built;
            }
            var matrix = Assemble();

            // Gauge: the first vertex block of every connected component at the origin.
            stats.assemblyMs = watch.ElapsedMilliseconds;
            var fixedVar = new bool[vars];
            var x = new double[vars];
            {
                var gauged = new bool[componentCount];
                for (var f = 0; f < m; f++)
                {
                    if (gauged[component[f]]) continue;
                    var corner = 3 * f;
                    if (cornerCount[corner] == 0) continue;
                    var b = pool[cornerStart[corner]].block;
                    fixedVar[2 * b] = fixedVar[2 * b + 1] = true;
                    gauged[component[f]] = true;
                }
            }

            // Direct factorisation, when asked for: one symbolic pass, a numeric pass per solve.
            SparseLdlt ldlt = null;
            Vector3[] ldltPoints = null;
            bool[] ldltLast = null;
            int[] ldltStart = null, ldltCol = null;
            if (direct)
            {
                var points = new Vector3[vars];
                var chainSum = new Vector3[chains];
                var chainCount = new int[chains];
                for (var e = 0; e < edges; e++)
                {
                    if (!cut[e]) continue;
                    chainSum[chainOf[e]] += 0.5f * (P[edgeA[e]] + P[edgeB[e]]);
                    chainCount[chainOf[e]]++;
                }
                for (var e = 0; e < edges; e++)
                {
                    if (!cut[e]) continue;
                    var b = tBlock[e];
                    points[2 * b] = points[2 * b + 1] = perEdgeTranslations
                        ? 0.5f * (P[edgeA[e]] + P[edgeB[e]])
                        : chainSum[chainOf[e]] / Mathf.Max(1, chainCount[chainOf[e]]);
                }
                for (var c = 0; c < 3 * m; c++)
                {
                    if (cornerCount[c] == 0) continue;
                    var b = pool[cornerStart[c]].block;
                    if (b < translationBlocks) continue;
                    points[2 * b] = points[2 * b + 1] = P[T[c]];
                }
                var symbolic = System.Diagnostics.Stopwatch.StartNew();
                bool[] lastVars = null;
                if (chainsLast)
                {
                    lastVars = new bool[vars];
                    for (var i = 0; i < 2 * translationBlocks; i++) lastVars[i] = true;
                }
                ldlt = new SparseLdlt(vars, matrix.start, matrix.col, points, lastVars);
                ldltPoints = points;
                ldltLast = lastVars;
                ldltStart = matrix.start;
                ldltCol = matrix.col;
                stats.factorNonZeros = ldlt.factorNonZeros;
                stats.symbolicMs = symbolic.ElapsedMilliseconds;
            }

            // The factorisation's pattern is the matrix's at construction. A matrix assembled again
            // with other rows -- crease rows released, spiral loop rows added -- has another pattern,
            // and its values read through the old one land in the wrong slots (a NaN pivot, or a
            // wrong solve that does not fail). Analysed again when the pattern changed.
            bool SamePattern(Csr a)
            {
                if (ReferenceEquals(a.start, ldltStart) && ReferenceEquals(a.col, ldltCol)) return true;
                if (a.start.Length != ldltStart.Length || a.col.Length != ldltCol.Length) return false;
                for (var i = 0; i < a.start.Length; i++) if (a.start[i] != ldltStart[i]) return false;
                for (var i = 0; i < a.col.Length; i++) if (a.col[i] != ldltCol[i]) return false;
                return true;
            }

            var st = stats;
            void SolveSystem(int iterations)
            {
                if (ldlt == null)
                {
                    st.cgIterations += matrix.Solve(rhs, x, fixedVar, iterations, tolerance, out st.residual);
                    return;
                }
                if (refactorOnNewPattern && !SamePattern(matrix))
                {
                    var symbolicAgain = System.Diagnostics.Stopwatch.StartNew();
                    ldlt = new SparseLdlt(vars, matrix.start, matrix.col, ldltPoints, ldltLast);
                    ldltStart = matrix.start;
                    ldltCol = matrix.col;
                    st.symbolicMs += symbolicAgain.ElapsedMilliseconds;
                    st.symbolicRedone++;
                }
                var numeric = System.Diagnostics.Stopwatch.StartNew();
                if (!ldlt.Factor(matrix.val, fixedVar))
                {
                    st.factorFailures++;
                    if (st.factorFailures <= 4)
                    {
                        var bad = 0;
                        foreach (var value in matrix.val) if (double.IsNaN(value) || double.IsInfinity(value)) bad++;
                        var badRhs = 0;
                        foreach (var value in rhs) if (double.IsNaN(value) || double.IsInfinity(value)) badRhs++;
                        var fr0 = ldlt.lastFailedRow;
                        var diag = 0.0;
                        var rowNnz = 0;
                        if (fr0 >= 0)
                            for (var j = matrix.start[fr0]; j < matrix.start[fr0 + 1]; j++) { rowNnz++; if (matrix.col[j] == fr0) diag = matrix.val[j]; }
                        st.split += $"[factorFail var {fr0} (block {fr0 / 2}, translation {fr0 / 2 < translationBlocks}) pivot {ldlt.lastFailedPivot:G3} diag {diag:G3} nnz {rowNnz} fixed {(fr0 >= 0 && fixedVar[fr0])} nonFinite {bad}/{badRhs}";
                        var nz = ldlt.nearZeroRow;
                        if (nz >= 0)
                        {
                            var nzBlock = nz / 2;
                            var nzVertex = -1;
                            for (var v = 0; v < n && nzVertex < 0; v++) if (masterOf[v] == nzBlock) nzVertex = v;
                            var nzFaces = nzVertex >= 0 && cornersOf[nzVertex] != null ? cornersOf[nzVertex].Count : -1;
                            st.split += $" nearZero var {nz} pivot/diag {ldlt.nearZeroPivot:G3} translation {nzBlock < translationBlocks} vertex {nzVertex} boundary {(nzVertex >= 0 && boundary[nzVertex])} faces {nzFaces}";
                        }
                        st.split += "] ";
                    }
                    // Retried with the diagonal raised by a millionth of itself: a mode that the rows
                    // leave free (cancellation noise on a scan's slivers) is then held at its start,
                    // where the conjugate gradients below took 8000 iterations to find the same.
                    if (factorRetryShift > 0)
                    {
                        var shifted = (double[])matrix.val.Clone();
                        for (var r = 0; r < vars; r++)
                            for (var j = matrix.start[r]; j < matrix.start[r + 1]; j++)
                                if (matrix.col[j] == r) shifted[j] += factorRetryShift * Math.Abs(shifted[j]) + 1e-9;
                        if (ldlt.Factor(shifted, fixedVar))
                        {
                            st.factorRetries++;
                            var b3 = new double[vars];
                            for (var r = 0; r < vars; r++)
                            {
                                if (fixedVar[r]) { b3[r] = x[r]; continue; }
                                var sum = rhs[r];
                                for (var j = matrix.start[r]; j < matrix.start[r + 1]; j++)
                                    if (fixedVar[matrix.col[j]]) sum -= matrix.val[j] * x[matrix.col[j]];
                                b3[r] = sum;
                            }
                            ldlt.Solve(b3, x);
                            st.factorMs += numeric.ElapsedMilliseconds;
                            st.factorizations++;
                            return;
                        }
                    }
                    st.cgIterations += matrix.Solve(rhs, x, fixedVar, iterations, tolerance, out st.residual);
                    return;
                }
                // Free rows: b minus what the fixed values contribute. Fixed rows: the value itself.
                var b2 = new double[vars];
                for (var r = 0; r < vars; r++)
                {
                    if (fixedVar[r]) { b2[r] = x[r]; continue; }
                    var sum = rhs[r];
                    for (var j = matrix.start[r]; j < matrix.start[r + 1]; j++)
                        if (fixedVar[matrix.col[j]]) sum -= matrix.val[j] * x[matrix.col[j]];
                    b2[r] = sum;
                }
                ldlt.Solve(b2, x);
                st.factorMs += numeric.ElapsedMilliseconds;
                st.factorizations++;
                st.split += $"[par {ldlt.lastParallelMs} tasks {ldlt.taskCount} top {ldlt.topRowCount} topNnz {ldlt.TopNonZeros()}] ";
            }

            // A previous pass over the same mesh and field at a uniformly different size: its free
            // solution, rescaled, and its stiffening weights stand in for solving them again.
            var reused = false;
            var reuseRatio = 0.0;
            if (cache != null && ReferenceEquals(cache.topology, topology) && ReferenceEquals(cache.crossField, crossField)
                && cache.spacing != null && cache.spacing.Length == n && cache.stiff != null && cache.stiff.Length == m
                && cache.freeSolution != null && cache.freeSolution.Length == vars)
            {
                var ratio = 0.0;
                var uniform = true;
                for (var v = 0; v < n && uniform; v++)
                {
                    if (!(spacing[v] > 0f) || !(cache.spacing[v] > 0f)) continue;
                    var r = (double)cache.spacing[v] / spacing[v];
                    if (ratio == 0.0) ratio = r;
                    else if (Math.Abs(r / ratio - 1.0) > 1e-4) uniform = false;
                }
                if (uniform && ratio > 0.0)
                {
                    Array.Copy(cache.stiff, stiff, m);
                    matrix = Assemble();
                    for (var i = 0; i < vars; i++) x[i] = fixedVar[i] ? x[i] : cache.freeSolution[i] * ratio;
                    reused = true;
                    reuseRatio = ratio;
                    stats.reusedFreeSolve = true;
                }
            }

            bool[] FoldFlags()
            {
                var folded = new bool[m];
                for (var f = 0; f < m; f++)
                {
                    if (!(fArea[f] > 0f)) continue;
                    double ux = 0, uy = 0, vx = 0, vy = 0;
                    for (var k = 0; k < 3; k++)
                    {
                        var corner = 3 * f + k;
                        double cu = 0, cv = 0;
                        for (var i = 0; i < cornerCount[corner]; i++)
                        {
                            var t = pool[cornerStart[corner] + i];
                            double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                            if (t.rot == 0) { cu += t.sign * xu; cv += t.sign * xv; }
                            else { cu -= t.sign * xv; cv += t.sign * xu; }
                        }
                        ux += gradX[corner] * cu; uy += gradY[corner] * cu;
                        vx += gradX[corner] * cv; vy += gradY[corner] * cv;
                    }
                    folded[f] = ux * vy - uy * vx <= 0;
                }
                return folded;
            }

            int CountFlipsQuiet()
            {
                var keep = debugDumpPrefix;
                debugDumpPrefix = null;
                var r = CountFlips();
                debugDumpPrefix = keep;
                return r;
            }
            var dumpIndex = 0;
            int CountFlips()
            {
                if (debugDumpPrefix != null)
                {
                    // Diagnostics: the coarse mesh with every corner's (u, v) and the singularities.
                    var sb = new System.Text.StringBuilder();
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    for (var v = 0; v < n; v++) sb.Append("v ").Append(P[v].x.ToString("R", ic)).Append(' ').Append(P[v].y.ToString("R", ic)).Append(' ').Append(P[v].z.ToString("R", ic)).Append((char)10);
                    for (var c = 0; c < 3 * m; c++)
                    {
                        double cu = 0, cv = 0;
                        for (var i = 0; i < cornerCount[c]; i++)
                        {
                            var t = pool[cornerStart[c] + i];
                            double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                            if (t.rot == 0) { cu += t.sign * xu; cv += t.sign * xv; }
                            else { cu -= t.sign * xv; cv += t.sign * xu; }
                        }
                        sb.Append("vt ").Append(cu.ToString("R", ic)).Append(' ').Append(cv.ToString("R", ic)).Append((char)10);
                    }
                    for (var f = 0; f < m; f++)
                        sb.Append($"f {T[3 * f] + 1}/{3 * f + 1} {T[3 * f + 1] + 1}/{3 * f + 2} {T[3 * f + 2] + 1}/{3 * f + 3}" + (char)10);
                    for (var v = 0; v < n; v++) if (singular[v]) sb.Append($"# sing {v} {index[v]} {trueTurn[v].ToString("F3", ic)}" + (char)10);
                    for (var v = 0; v < n; v++) if (pinU[v] || pinV[v]) sb.Append($"# pin {v} {(pinU[v] ? 1 : 0)} {(pinV[v] ? 1 : 0)}" + (char)10);
                    for (var f = 0; f < m; f++) sb.Append($"# frame {f} {gradX[3 * f].ToString("R", ic)} {gradY[3 * f].ToString("R", ic)} {gradX[3 * f + 1].ToString("R", ic)} {gradY[3 * f + 1].ToString("R", ic)} {gradX[3 * f + 2].ToString("R", ic)} {gradY[3 * f + 2].ToString("R", ic)} {fArea[f].ToString("R", ic)}" + (char)10);
                    System.IO.File.WriteAllText($"{debugDumpPrefix}_{dumpIndex++}.obj", sb.ToString());
                }
                var flips = 0;
                for (var f = 0; f < m; f++)
                {
                    if (!(fArea[f] > 0f)) continue;
                    double ux = 0, uy = 0, vx = 0, vy = 0;
                    for (var k = 0; k < 3; k++)
                    {
                        var corner = 3 * f + k;
                        double cu = 0, cv = 0;
                        for (var i = 0; i < cornerCount[corner]; i++)
                        {
                            var t = pool[cornerStart[corner] + i];
                            double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                            if (t.rot == 0) { cu += t.sign * xu; cv += t.sign * xv; }
                            else { cu -= t.sign * xv; cv += t.sign * xu; }
                        }
                        ux += gradX[corner] * cu; uy += gradY[corner] * cu;
                        vx += gradX[corner] * cv; vy += gradY[corner] * cv;
                    }
                    if (ux * vy - uy * vx <= 0) flips++;
                }
                return flips;
            }


            // --- barrier untangling ----------------------------------------------------------------
            // Garanzha, Kaporin, Kudryavtseva, Protais, Ray and Sokolov, "Foldover-free maps in 50
            // lines of code" (SIGGRAPH 2021). The least squares energy plus, per face, a barrier on
            // its Jacobian M in cells (M = I is a square cell along the field):
            //     f(M) = (|M|^2 + theta (det^2 + 1)) / (2 chi(det, eps)),
            //     chi(D, eps) = (D + sqrt(eps^2 + D^2)) / 2,
            // finite everywhere for eps > 0 -- so it starts from a folded map, which a plain barrier
            // cannot -- and infinite at det <= 0 as eps goes to 0. eps shrinks between rounds by the
            // paper's rule. The variables are the solver's own: corners stay seamless, held integers
            // stay held, and closures and crease rows keep their least squares weight.
            int BarrierUntangle(int rounds, int inner, string label)
            {
                var untangleWatch = System.Diagnostics.Stopwatch.StartNew();
                var free = new List<int>();
                for (var i = 0; i < vars; i++) if (!fixedVar[i]) free.Add(i);
                var faceW = new double[m];
                for (var f = 0; f < m; f++)
                {
                    var h = fh[f];
                    var ok = fArea[f] > 0f && h > 0f && (gradX[3 * f] != 0 || gradY[3 * f] != 0 || gradX[3 * f + 1] != 0);
                    faceW[f] = ok ? untangleBarrierWeight * fArea[f] / ((double)h * h) : 0;
                }
                var theta = untangleTheta;
                double lsConstant = 0;
                for (var f = 0; f < m; f++)
                    if (fArea[f] > 0f && fh[f] > 0f) lsConstant += 2.0 * fArea[f] * stiff[f] / ((double)fh[f] * fh[f]);
                var eps = 1.0;
                var cu = new double[3 * m];
                var cv = new double[3 * m];
                var ax = new double[vars];

                double MinDet(double[] xs)
                {
                    var dmin = double.PositiveInfinity;
                    for (var f = 0; f < m; f++)
                    {
                        if (faceW[f] == 0) continue;
                        double ux = 0, uy = 0, vx = 0, vy = 0;
                        for (var k = 0; k < 3; k++)
                        {
                            var corner = 3 * f + k;
                            double a = 0, b = 0;
                            for (var i = 0; i < cornerCount[corner]; i++)
                            {
                                var t = pool[cornerStart[corner] + i];
                                double xu = xs[2 * t.block], xv = xs[2 * t.block + 1];
                                if (t.rot == 0) { a += t.sign * xu; b += t.sign * xv; }
                                else { a -= t.sign * xv; b += t.sign * xu; }
                            }
                            ux += gradX[corner] * a; uy += gradY[corner] * a;
                            vx += gradX[corner] * b; vy += gradY[corner] * b;
                        }
                        double h = fh[f];
                        dmin = Math.Min(dmin, h * h * (ux * vy - uy * vx));
                    }
                    return dmin;
                }

                // Energy at xs; with g != null its gradient too (zero on held variables). The matrix
                // product, the corners and the faces run in parallel; the scatter into g does not.
                var faceE = new double[m];
                var dCu = new double[3 * m];
                var dCv = new double[3 * m];
                double Energy(double[] xs, double[] g)
                {
                    // Least squares: x^T A x - 2 b^T x + b0, b0 the targets' own square (the face rows'
                    // 1/h), so that the energy is the sum of squares itself and never negative.
                    System.Threading.Tasks.Parallel.For(0, vars, BackgroundScheduler.Options, r =>
                    {
                        double sum = 0;
                        for (var j = matrix.start[r]; j < matrix.start[r + 1]; j++) sum += matrix.val[j] * xs[matrix.col[j]];
                        ax[r] = sum;
                    });
                    double e = lsConstant;
                    for (var r = 0; r < vars; r++) e += xs[r] * ax[r] - 2 * rhs[r] * xs[r];
                    if (g != null) for (var r = 0; r < vars; r++) g[r] = 2 * (ax[r] - rhs[r]);
                    System.Threading.Tasks.Parallel.For(0, 3 * m, BackgroundScheduler.Options, c =>
                    {
                        double a = 0, b = 0;
                        for (var i = 0; i < cornerCount[c]; i++)
                        {
                            var t = pool[cornerStart[c] + i];
                            double xu = xs[2 * t.block], xv = xs[2 * t.block + 1];
                            if (t.rot == 0) { a += t.sign * xu; b += t.sign * xv; }
                            else { a -= t.sign * xv; b += t.sign * xu; }
                        }
                        cu[c] = a;
                        cv[c] = b;
                    });
                    var withGrad = g != null;
                    var epsNow = eps;
                    System.Threading.Tasks.Parallel.For(0, m, BackgroundScheduler.Options, f =>
                    {
                        faceE[f] = 0;
                        var w = faceW[f];
                        if (w == 0) { for (var k = 0; k < 3; k++) { dCu[3 * f + k] = 0; dCv[3 * f + k] = 0; } return; }
                        double h = fh[f];
                        double ma = 0, mb = 0, mc = 0, md = 0;
                        for (var k = 0; k < 3; k++)
                        {
                            var c = 3 * f + k;
                            ma += gradX[c] * cu[c]; mb += gradY[c] * cu[c];
                            mc += gradX[c] * cv[c]; md += gradY[c] * cv[c];
                        }
                        ma *= h; mb *= h; mc *= h; md *= h;
                        var D = ma * md - mb * mc;
                        var sq = Math.Sqrt(epsNow * epsNow + D * D);
                        var chi = 0.5 * (D + sq);
                        var num = ma * ma + mb * mb + mc * mc + md * md + theta * (D * D + 1);
                        var fval = num / (2 * chi);
                        faceE[f] = w * fval;
                        if (!withGrad) return;
                        var dchi = 0.5 * (1 + D / sq);
                        // df/dM = (2M + 2 theta D cof) / (2 chi) - f chi' cof / chi
                        var kD = theta * D / chi - fval * dchi / chi;
                        var ga = w * (ma / chi + kD * md);
                        var gb = w * (mb / chi - kD * mc);
                        var gc = w * (mc / chi - kD * mb);
                        var gd = w * (md / chi + kD * ma);
                        for (var k = 0; k < 3; k++)
                        {
                            var c = 3 * f + k;
                            dCu[c] = h * (ga * gradX[c] + gb * gradY[c]);
                            dCv[c] = h * (gc * gradX[c] + gd * gradY[c]);
                        }
                    });
                    for (var f = 0; f < m; f++) e += faceE[f];
                    if (g == null) return e;
                    for (var c = 0; c < 3 * m; c++)
                    {
                        var du = dCu[c];
                        var dv = dCv[c];
                        if (du == 0 && dv == 0) continue;
                        for (var i = 0; i < cornerCount[c]; i++)
                        {
                            var t = pool[cornerStart[c] + i];
                            if (t.rot == 0) { g[2 * t.block] += t.sign * du; g[2 * t.block + 1] += t.sign * dv; }
                            else { g[2 * t.block + 1] -= t.sign * du; g[2 * t.block] += t.sign * dv; }
                        }
                    }
                    for (var r = 0; r < vars; r++) if (fixedVar[r]) g[r] = 0;
                    return e;
                }

                // L-BFGS over the free variables.
                const int history = 8;
                var sHist = new double[history][];
                var yHist = new double[history][];
                var rho = new double[history];
                var alphaH = new double[history];
                var grad = new double[vars];
                var gradNew = new double[vars];
                var dir = new double[vars];
                var trial = new double[vars];
                var evaluations = 0;
                var dmin0 = MinDet(x);
                if (dmin0 > 0)
                {
                    st.flipsByStage += label + " 0 ";
                    return 0;
                }
                // The paper's start: eps just large enough that the worst face is not yet crushed.
                eps = dmin0 > 0 ? 1e-9 : Math.Max(1e-3, 2 * Math.Abs(dmin0));
                var prevE = double.NaN;
                for (var round = 0; round < rounds; round++)
                {
                    var count = 0;
                    var head = 0;
                    var energy = Energy(x, grad);
                    evaluations++;
                    var startE = energy;
                    for (var it = 0; it < inner; it++)
                    {
                        // Two-loop recursion.
                        for (var i = 0; i < vars; i++) dir[i] = -grad[i];
                        for (var j = 0; j < count; j++)
                        {
                            var idx = (head - 1 - j + history) % history;
                            double a = 0;
                            foreach (var i in free) a += sHist[idx][i] * dir[i];
                            a *= rho[idx];
                            alphaH[idx] = a;
                            foreach (var i in free) dir[i] -= a * yHist[idx][i];
                        }
                        if (count > 0)
                        {
                            var last = (head - 1 + history) % history;
                            double sy = 0, yy = 0;
                            foreach (var i in free) { sy += sHist[last][i] * yHist[last][i]; yy += yHist[last][i] * yHist[last][i]; }
                            var gamma = yy > 0 ? sy / yy : 1;
                            foreach (var i in free) dir[i] *= gamma;
                        }
                        else
                        {
                            double gn = 0;
                            foreach (var i in free) gn += grad[i] * grad[i];
                            var scale = gn > 0 ? 1e-2 / Math.Sqrt(gn) : 0;
                            foreach (var i in free) dir[i] *= scale;
                        }
                        for (var j = count - 1; j >= 0; j--)
                        {
                            var idx = (head - 1 - j + history) % history;
                            double b = 0;
                            foreach (var i in free) b += yHist[idx][i] * dir[i];
                            b *= rho[idx];
                            foreach (var i in free) dir[i] += sHist[idx][i] * (alphaH[idx] - b);
                        }
                        double slope = 0;
                        foreach (var i in free) slope += grad[i] * dir[i];
                        if (!(slope < 0))
                        {
                            // Not a descent direction: restart from the gradient.
                            count = 0;
                            double gn = 0;
                            foreach (var i in free) gn += grad[i] * grad[i];
                            var scale = gn > 0 ? 1e-2 / Math.Sqrt(gn) : 0;
                            foreach (var i in free) dir[i] = -grad[i] * scale;
                            slope = 0;
                            foreach (var i in free) slope += grad[i] * dir[i];
                            if (!(slope < 0)) break;
                        }
                        // Backtracking on the Armijo condition.
                        var step = 1.0;
                        var newE = double.PositiveInfinity;
                        for (var ls = 0; ls < 30; ls++)
                        {
                            Array.Copy(x, trial, vars);
                            foreach (var i in free) trial[i] += step * dir[i];
                            newE = Energy(trial, gradNew);
                            evaluations++;
                            if (newE <= energy + 1e-4 * step * slope) break;
                            step *= 0.5;
                        }
                        if (!(newE < energy)) break;
                        var sv = sHist[head] ??= new double[vars];
                        var yv = yHist[head] ??= new double[vars];
                        double syv = 0;
                        foreach (var i in free)
                        {
                            sv[i] = trial[i] - x[i];
                            yv[i] = gradNew[i] - grad[i];
                            syv += sv[i] * yv[i];
                        }
                        Array.Copy(trial, x, vars);
                        Array.Copy(gradNew, grad, vars);
                        var dropped = energy - newE;
                        energy = newE;
                        if (syv > 1e-20)
                        {
                            rho[head] = 1.0 / syv;
                            head = (head + 1) % history;
                            count = Math.Min(count + 1, history);
                        }
                        if (dropped < 1e-10 * Math.Abs(energy)) break;
                    }
                    var dmin = MinDet(x);
                    var sigma = Math.Max(1 - energy / startE, untangleSigmaMin);
                    if (double.IsNaN(prevE)) sigma = untangleSigmaMin;
                    prevE = energy;
                    var chiMin = 0.5 * (dmin + Math.Sqrt(eps * eps + dmin * dmin));
                    var mu = (1 - sigma) * chiMin;
                    eps = dmin < mu ? 2 * Math.Sqrt(mu * (mu - dmin)) : 1e-9;
                    st.untangleBarrierRounds++;
                    if (untangleTrace) st.untangleLog += $"[{label}{round} E {startE:G4}->{energy:G4} dmin {dmin:G3} eps {eps:G3} flips {CountFlipsQuiet()} ev {evaluations}] ";
                    if (dmin > 0 && eps <= 1e-9 && round >= 1) break;
                }
                st.untangleEvaluations += evaluations;
                var flips = CountFlips();
                st.flipsByStage += label + " " + flips + $"({untangleWatch.ElapsedMilliseconds}ms) ";
                return flips;
            }

            if (!reused) SolveSystem(firstIterations);

            // --- 6b. local stiffening ------------------------------------------------------------
            // Where the map is distorted -- worst where it folds -- that face's weight goes up and
            // the free map is solved again, so the distortion moves to where it costs least. Bommes
            // et al. 2009, section 5. The pattern is unchanged, so only the numeric factorisation
            // repeats.
            // One stiffening pass over the current map: true if any face's weight changed.
            bool Stiffen(double threshold, double rate, double foldPenalty)
            {
                var changed = 0;
                for (var f = 0; f < m; f++)
                {
                    if (!(fArea[f] > 0f) || !(fh[f] > 0f)) continue;
                    double ux = 0, uy = 0, vx = 0, vy = 0;
                    for (var k = 0; k < 3; k++)
                    {
                        var corner = 3 * f + k;
                        double cu = 0, cv = 0;
                        for (var i = 0; i < cornerCount[corner]; i++)
                        {
                            var t = pool[cornerStart[corner] + i];
                            double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                            if (t.rot == 0) { cu += t.sign * xu; cv += t.sign * xv; }
                            else { cu -= t.sign * xv; cv += t.sign * xu; }
                        }
                        ux += gradX[corner] * cu; uy += gradY[corner] * cu;
                        vx += gradX[corner] * cv; vy += gradY[corner] * cv;
                    }
                    var h = fh[f];
                    double a = ux * h - 1, b = uy * h, c = vx * h, d = vy * h - 1;
                    var distortion = Math.Sqrt(a * a + b * b + c * c + d * d);
                    if ((ux * vy - uy * vx) <= 0) distortion += foldPenalty;
                    if (distortion < threshold) continue;
                    stiff[f] = Math.Min(stiffenMax, stiff[f] * (1.0 + rate * distortion));
                    changed++;
                }
                stA.stiffened += changed;
                return changed > 0;
            }

            for (var iteration = 0; iteration < (reused ? 0 : stiffenIterations); iteration++)
            {
                if (!Stiffen(stiffenThreshold, stiffenRate, 2.0)) break;
                matrix = Assemble();
                SolveSystem(firstIterations);
                stats.stiffenIterationsRun++;
            }

            stats.flipsByStage += "free " + CountFlips() + " ";
            if (cache != null && !reused)
            {
                cache.topology = topology;
                cache.crossField = crossField;
                cache.spacing = (float[])spacing.Clone();
                cache.stiff = (double[])stiff.Clone();
                cache.freeSolution = (double[])x.Clone();
            }
            // A pass at a uniformly different size takes the last pass's untangled, untwisted map,
            // rescaled, and its loop rows: neither depends on the size.
            if (reused && IntegerGridMap.parametrizationLayout && cache.layoutSolution != null && cache.layoutSolution.Length == vars)
            {
                for (var i = 0; i < vars; i++) x[i] = fixedVar[i] ? x[i] : cache.layoutSolution[i] * reuseRatio;
                if (cache.loopRows is List<(List<(Term t, double coef)> row, int comp, double target)> cachedRows)
                    loopRows.AddRange(cachedRows);
                // And the creases it let go: laid as arcs they would not be iso-lines of this map.
                if (cache.layoutDropped != null && layoutDropped != null)
                {
                    foreach (var key in cache.layoutDropped) layoutDropped.Add(key);
                    constrainedFeatureEdges.RemoveAll(e => layoutDropped.Contains(((long)Math.Min(e.a, e.b) << 32) | (uint)Math.Max(e.a, e.b)));
                }
                stats.reusedLayout = true;
            }

            // Creases crushing the map: held as exact iso-lines, two that converge squash the
            // triangles between them flat across (body at 5k: 1 cell of the map over 12 of surface,
            // and a band of long quads). Their edges at a triangle squashed under
            // layoutSquashLimit of a cell across are let go, and the map solved again.
            for (var round = 0; IntegerGridMap.parametrizationLayout && !reused && round < layoutSquashRounds; round++)
            {
                var release = new HashSet<int>();
                for (var f = 0; f < m; f++)
                {
                    if (!(fArea[f] > 0f) || !(fh[f] > 0f)) continue;
                    var touches = false;
                    for (var k = 0; k < 3; k++) touches |= pinU[T[3 * f + k]] || pinV[T[3 * f + k]];
                    if (!touches) continue;
                    double ma = 0, mb = 0, mc = 0, md = 0;
                    for (var k = 0; k < 3; k++)
                    {
                        var corner = 3 * f + k;
                        double cu = 0, cv = 0;
                        for (var i = 0; i < cornerCount[corner]; i++)
                        {
                            var t = pool[cornerStart[corner] + i];
                            double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                            if (t.rot == 0) { cu += t.sign * xu; cv += t.sign * xv; }
                            else { cu -= t.sign * xv; cv += t.sign * xu; }
                        }
                        ma += gradX[corner] * cu; mb += gradY[corner] * cu;
                        mc += gradX[corner] * cv; md += gradY[corner] * cv;
                    }
                    double h = fh[f];
                    ma *= h; mb *= h; mc *= h; md *= h;
                    // Smaller singular value of M.
                    var tr = ma * ma + mb * mb + mc * mc + md * md;
                    var det = Math.Abs(ma * md - mb * mc);
                    var disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det * det));
                    var smin = Math.Sqrt(Math.Max(0, tr / 2 - disc));
                    if (smin >= layoutSquashLimit) continue;
                    for (var k = 0; k < 3; k++) release.Add(T[3 * f + k]);
                }
                var before = layoutDropped.Count;
                foreach (var (a, b) in featureEdges)
                    if (release.Contains(a) || release.Contains(b))
                        layoutDropped.Add(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b));
                var freed = layoutDropped.Count - before;
                if (freed == 0) break;
                stA.featureEdgesSquashed += freed;
                constrainedFeatureEdges.RemoveAll(e => layoutDropped.Contains(((long)Math.Min(e.a, e.b) << 32) | (uint)Math.Max(e.a, e.b)));
                matrix = Assemble();
                SolveSystem(firstIterations);
            }
            // Untangled once, after the creases are settled: the squash test reads the plain
            // solve, which folds or not makes no difference to (head at 5k: 3 untangles -> 1).
            if (!stats.reusedLayout && FreeUntangleRounds > 0) BarrierUntangle(FreeUntangleRounds, untangleInner, "barrierFree");
            for (var loopPass = 0; layoutDiagnostics || IntegerGridMap.parametrizationLayout; loopPass++)
            {
                var freeUv = new double[6 * m];
                for (var c = 0; c < 3 * m; c++)
                {
                    double a = 0, b = 0;
                    for (var i = 0; i < cornerCount[c]; i++)
                    {
                        var t = pool[cornerStart[c] + i];
                        double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                        if (t.rot == 0) { a += t.sign * xu; b += t.sign * xv; }
                        else { a -= t.sign * xv; b += t.sign * xu; }
                    }
                    freeUv[2 * c] = a;
                    freeUv[2 * c + 1] = b;
                }
                var layoutStats = QuantizedLayout.Build(topology, freeUv, constrainedFeatureEdges,
                    out stats.layoutPositions, out stats.layoutNormals, out stats.layoutFaces, out var loops);
                stats.layout += "<<" + layoutStats.debug.Substring(0, Math.Min(layoutStats.debug.Length, 600)) + ">> ";
                if (loopPass == 0) stats.layout = "";
                stats.layout += layoutStats.ToString();
                if (loops.Count == 0 || loopPass >= (Array.IndexOf(boundary, true) >= 0 ? layoutLoopPassesOpen : layoutLoopPasses)) break;

                // Spirals: a separatrix came back round a cylinder shifted sideways, because the
                // map is twisted there -- its translation round that loop is not a whole number of
                // cells across. That translation is linear in the unknowns: carried round the loop,
                // a point of the start chart comes back as itself plus the sum of each crossing's
                // translation turned by the crossings after it. Its sideways part is held to the
                // nearest integer, and the map solved and untangled again.
                foreach (var loop in loops)
                {
                    var turnSum = 0;
                    foreach (var k in loop.turns) turnSum += k;
                    if (Mod4(turnSum) != 0) continue;
                    var comp = loop.dir % 2 == 0 ? 1 : 0;      // sideways to the separatrix
                    var row = new List<(Term t, double coef)>();
                    var after = 0;                              // quarter turns of the later crossings
                    for (var j = loop.crossed.Length - 1; j >= 0; j--)
                    {
                        var h = loop.crossed[j];
                        var o = across[h] < 0 ? -1 : 3 * across[h] + acrossCorner[h];
                        if (o < 0) { row = null; break; }
                        // t_j = corner of the vertex at h's tail in the face across, minus that corner
                        // in h's own face turned by the crossing.
                        var cf = h;
                        var cg = 3 * (o / 3) + (o % 3 + 1) % 3;
                        for (var i = 0; i < cornerCount[cg]; i++) row.Add((Turn(pool[cornerStart[cg] + i], after), 1.0));
                        for (var i = 0; i < cornerCount[cf]; i++) row.Add((Turn(pool[cornerStart[cf] + i], after + loop.turns[j]), -1.0));
                        after += loop.turns[j];
                    }
                    if (row == null) continue;
                    // Closed rings where the twist is under a cell; a whole number of cells otherwise.
                    loopRows.Add((row, comp, Math.Abs(loop.shift) < 1 ? 0 : Math.Round(loop.shift)));
                    stA.loopRows++;
                }
                stA.loopPasses++;
                matrix = Assemble();
                SolveSystem(firstIterations);
                if (FreeUntangleRounds > 0) BarrierUntangle(FreeUntangleRounds, untangleInner, "barrierLoop");
            }

            if (cache != null && !reused && IntegerGridMap.parametrizationLayout)
            {
                cache.layoutSolution = (double[])x.Clone();
                cache.layoutDropped = layoutDropped != null ? new HashSet<long>(layoutDropped) : null;
                cache.loopRows = new List<(List<(Term t, double coef)> row, int comp, double target)>(loopRows);
            }

            // Rounds every chain's translation so that every fan closure still closes. The closure at
            // a junction or a singularity is a linear relation between the chains that meet there:
            // round them all on their own and it opens by a whole cell, and the solve folds faces to
            // bridge the gap (helmet head: 554 open closures, up to one cell). So the relations are
            // eliminated along a spanning forest of the cut graph -- nodes are the vertices with a
            // closure, edges the chains, the border (or any chain end without a closure) a common
            // ground. The chains off the forest are free integers, rounded nearest first; each
            // forest chain is then COMPUTED from the closure at its child end, leaves first, which
            // makes it an integer combination of integers. A component that never reaches the
            // ground has one relation too many: its root is a singularity whose position is left
            // free to absorb it, as Bommes et al. do with the last one.
            string DiagnoseClosures()
            {
                var blockChainD = new int[translationBlocks];
                for (var e = 0; e < edges; e++) if (cut[e]) blockChainD[tBlock[e]] = chainOf[e];
                var kinds = new Dictionary<string, int>();
                foreach (var row in closureRows)
                {
                    double ru = 0, rv = 0;
                    int tb = 0, vb = 0, vfree = 0;
                    var chainSet = new HashSet<int>();
                    foreach (var t in row)
                    {
                        double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                        if (t.rot == 0) { ru += t.sign * xu; rv += t.sign * xv; }
                        else { ru -= t.sign * xv; rv += t.sign * xu; }
                        if (t.block < translationBlocks) { tb++; chainSet.Add(blockChainD[t.block]); }
                        else { vb++; if (!fixedVar[2 * t.block]) vfree++; }
                    }
                    if (Math.Max(Math.Abs(ru), Math.Abs(rv)) <= 0.05) continue;
                    var key = $"t{tb}c{chainSet.Count}v{vb}f{vfree}";
                    kinds[key] = kinds.TryGetValue(key, out var k) ? k + 1 : 1;
                }
                var sb = new System.Text.StringBuilder("[");
                foreach (var kv in kinds) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(' ');
                return sb.Append("] ").ToString();
            }

            void RoundChainsConsistently()
            {
                var blockChain = new int[translationBlocks];
                var chainBlocks = new List<int>[chains];
                for (var e = 0; e < edges; e++)
                {
                    if (!cut[e]) continue;
                    blockChain[tBlock[e]] = chainOf[e];
                    (chainBlocks[chainOf[e]] ??= new List<int>(4)).Add(tBlock[e]);
                }
                // One closure per vertex, at chain level: a chain's edges carry equal translations,
                // so a pass-through vertex's closure cancels out and the vertex is no node.
                var nodeRow = new Dictionary<int, (List<Term> cs, List<Term> vs)>();
                for (var r = 0; r < closureRows.Count; r++)
                {
                    var v = closureVertex[r];
                    if (nodeRow.ContainsKey(v)) continue;
                    var cs = new List<Term>();
                    var vs = new List<Term>();
                    foreach (var t in closureRows[r])
                    {
                        if (t.block < translationBlocks) cs.Add(new Term { block = blockChain[t.block], rot = t.rot, sign = t.sign });
                        else vs.Add(t);
                    }
                    cs = Simplify(cs);
                    vs = Simplify(vs);
                    if (cs.Count == 0 && vs.Count == 0) continue;
                    nodeRow[v] = (cs, vs);
                }
                bool IsNode(int v) => nodeRow.ContainsKey(v);

                var chainFixed = new bool[chains];
                var chainValue = new double[2 * chains];
                void ChainMean(int c, out double mu, out double mv)
                {
                    mu = mv = 0;
                    foreach (var b in chainBlocks[c]) { mu += x[2 * b]; mv += x[2 * b + 1]; }
                    mu /= chainBlocks[c].Count;
                    mv /= chainBlocks[c].Count;
                }
                void SetChain(int c, double u, double v)
                {
                    chainFixed[c] = true;
                    chainValue[2 * c] = u;
                    chainValue[2 * c + 1] = v;
                    foreach (var b in chainBlocks[c])
                    {
                        x[2 * b] = u;
                        x[2 * b + 1] = v;
                        fixedVar[2 * b] = fixedVar[2 * b + 1] = true;
                        stA.roundedVariables += 2;
                    }
                }
                for (var c = 0; c < chains; c++)
                {
                    if (chainBlocks[c] != null) continue;
                    chainFixed[c] = true;
                }

                // The spanning forest, grounded first.
                var chainsAt = new Dictionary<int, List<int>>();
                for (var c = 0; c < chains; c++)
                {
                    if (chainFixed[c]) continue;
                    foreach (var end in new[] { chainEndA[c], chainEndB[c] })
                    {
                        if (!chainsAt.TryGetValue(end, out var list)) chainsAt[end] = list = new List<int>(4);
                        list.Add(c);
                    }
                }
                var parentChain = new Dictionary<int, int>();
                var bfs = new List<int>();
                var treeChain = new bool[chains];
                var head = 0;
                void Grow()
                {
                    for (; head < bfs.Count; head++)
                    {
                        var v = bfs[head];
                        foreach (var c in chainsAt[v])
                        {
                            var w = chainEndA[c] == v ? chainEndB[c] : chainEndA[c];
                            if (!IsNode(w) || parentChain.ContainsKey(w)) continue;
                            parentChain[w] = c;
                            treeChain[c] = true;
                            bfs.Add(w);
                        }
                    }
                }
                for (var c = 0; c < chains; c++)
                {
                    if (chainFixed[c]) continue;
                    int a = chainEndA[c], b = chainEndB[c];
                    for (var side = 0; side < 2; side++)
                    {
                        var g = side == 0 ? a : b;
                        var w = side == 0 ? b : a;
                        if (IsNode(g) || !IsNode(w) || parentChain.ContainsKey(w)) continue;
                        parentChain[w] = c;
                        treeChain[c] = true;
                        bfs.Add(w);
                    }
                }
                Grow();
                // Ungrounded components: rooted at a singularity whose position stays free.
                var roots = new List<int>();
                foreach (var pass in new[] { true, false })
                    foreach (var v in nodeRow.Keys)
                    {
                        if (parentChain.ContainsKey(v) || !chainsAt.ContainsKey(v)) continue;
                        if (pass && !singular[v]) continue;
                        parentChain[v] = -1;
                        bfs.Add(v);
                        roots.Add(v);
                        Grow();
                    }
                foreach (var r in roots)
                {
                    if (singular[r] && masterOf[r] >= 0)
                    {
                        var b = masterOf[r];
                        if (fixedVar[2 * b] || fixedVar[2 * b + 1]) stA.freedPoles++;
                        fixedVar[2 * b] = fixedVar[2 * b + 1] = false;
                    }
                    else stA.regularRoots++;
                }

                // Free chains: nearest-to-integer first, in batches, re-solving between.
                var free = new List<int>();
                for (var c = 0; c < chains; c++) if (!chainFixed[c] && !treeChain[c]) free.Add(c);
                stA.freeChains = free.Count;
                stA.treeChains = 0;
                for (var c = 0; c < chains; c++) if (treeChain[c]) stA.treeChains++;
                var frac = new double[chains];
                while (free.Count > 0)
                {
                    foreach (var c in free)
                    {
                        ChainMean(c, out var mu, out var mv);
                        frac[c] = Math.Max(Frac(mu), Frac(mv));
                    }
                    free.Sort((a, b) => frac[a].CompareTo(frac[b]));
                    var take = Math.Max(1, (int)Math.Ceiling(free.Count * chainBatch));
                    for (var i = 0; i < take; i++)
                    {
                        var c = free[i];
                        ChainMean(c, out var mu, out var mv);
                        var ru = Math.Round(mu);
                        var rv = Math.Round(mv);
                        stA.maxRoundingMove = Mathf.Max(stA.maxRoundingMove, (float)Math.Max(Math.Abs(ru - mu), Math.Abs(rv - mv)));
                        SetChain(c, ru, rv);
                    }
                    free.RemoveRange(0, take);
                    // Re-solved after every batch, the last included: with the closures held, the
                    // forest chains then sit next to the integers they are about to be given.
                    stA.roundingRounds++;
                    SolveSystem(roundIterations);
                }

                // Forest chains, leaves first, each from its child's closure.
                void Accumulate(Term t, double xu, double xv, ref double su, ref double sv)
                {
                    if (t.rot == 0) { su += t.sign * xu; sv += t.sign * xv; }
                    else { su -= t.sign * xv; sv += t.sign * xu; }
                }
                for (var i = bfs.Count - 1; i >= 0; i--)
                {
                    var v = bfs[i];
                    var c = parentChain[v];
                    if (c < 0) continue;
                    var (cs, vs) = nodeRow[v];
                    var ok = true;
                    var count = 0;
                    var term = default(Term);
                    foreach (var t in cs)
                    {
                        if (t.block == c) { count++; term = t; }
                        else if (!chainFixed[t.block]) ok = false;
                    }
                    foreach (var t in vs) if (!fixedVar[2 * t.block] || !fixedVar[2 * t.block + 1]) ok = false;
                    if (!ok || count != 1) { stA.unsolvedTree++; continue; }
                    double su = 0, sv = 0;
                    foreach (var t in cs)
                        if (t.block != c) Accumulate(t, chainValue[2 * t.block], chainValue[2 * t.block + 1], ref su, ref sv);
                    foreach (var t in vs) Accumulate(t, x[2 * t.block], x[2 * t.block + 1], ref su, ref sv);
                    // sign * R(rot) * c = -(su, sv)  ->  c = -sign * R(-rot) (su, sv)
                    double cu, cv;
                    if (term.rot == 0) { cu = -term.sign * su; cv = -term.sign * sv; }
                    else { cu = -term.sign * sv; cv = term.sign * su; }
                    var ru = Math.Round(cu);
                    var rv = Math.Round(cv);
                    if (Math.Abs(ru - cu) > 1e-3 || Math.Abs(rv - cv) > 1e-3) stA.closureNonInteger++;
                    ChainMean(c, out var mu, out var mv);
                    stA.maxRoundingMove = Mathf.Max(stA.maxRoundingMove, (float)Math.Max(Math.Abs(ru - mu), Math.Abs(rv - mv)));
                    SetChain(c, ru, rv);
                    stA.propagatedChains++;
                }
                // Anything the forest could not reach or solve: rounded as before.
                for (var c = 0; c < chains; c++)
                {
                    if (chainFixed[c]) continue;
                    ChainMean(c, out var mu, out var mv);
                    SetChain(c, Math.Round(mu), Math.Round(mv));
                    stA.forcedChains++;
                }
                stA.roundingRounds++;
                SolveSystem(roundIterations);
            }

            // The layout has its own integers; rounding this map's would only be thrown away.
            if (IntegerGridMap.parametrizationLayout && layoutSkipRounding && stats.layoutFaces != null && stats.layoutFaces.Count > 0)
                goto readLattice;

            // --- 7a. singularities onto grid nodes ----------------------------------------------
            // A singularity whose (u, v) is not an integer sits inside a cell -- often its centre,
            // where the quarter-turn round it maps the lattice onto itself -- and the cells round it
            // fold to make room. Rounded first, the translations of the cuts that end there then
            // round to values consistent with it.
            if (roundSingularities)
            {
                var any = false;
                for (var v = 0; v < n; v++)
                {
                    if (!singular[v] || masterOf[v] < 0) continue;
                    for (var comp = 0; comp < 2; comp++)
                    {
                        var vi = 2 * masterOf[v] + comp;
                        if (fixedVar[vi]) continue;
                        x[vi] = Math.Round(x[vi]);
                        fixedVar[vi] = true;
                        stats.roundedSingularities++;
                        any = true;
                    }
                }
                if (any && !mergeRounding) { stats.roundingRounds++; SolveSystem(roundIterations); }
                stats.flipsByStage += "poles " + CountFlips() + " ";
            }

            // --- 7. round the chain translations ------------------------------------------------
            if (perEdgeTranslations && consistentRounding)
            {
                RoundChainsConsistently();
                stats.flipsByStage += "chains " + CountFlips() + " ";
                if (diagnoseClosures) stats.closureKinds = DiagnoseClosures();
            }
            else if (perEdgeTranslations)
            {
                // A chain's edges carry one translation between them: round their mean once and
                // give every edge that value, so a value near a half cannot split the chain.
                var sum = new double[2 * chains];
                var count = new int[chains];
                for (var e = 0; e < edges; e++)
                {
                    if (!cut[e]) continue;
                    var c = chainOf[e];
                    sum[2 * c] += x[2 * tBlock[e]];
                    sum[2 * c + 1] += x[2 * tBlock[e] + 1];
                    count[c]++;
                }
                for (var e = 0; e < edges; e++)
                {
                    if (!cut[e]) continue;
                    var c = chainOf[e];
                    for (var comp = 0; comp < 2; comp++)
                    {
                        var vi = 2 * tBlock[e] + comp;
                        if (fixedVar[vi]) continue;
                        var rounded = Math.Round(sum[2 * c + comp] / Math.Max(1, count[c]));
                        stats.maxRoundingMove = Mathf.Max(stats.maxRoundingMove, (float)Math.Abs(rounded - x[vi]));
                        x[vi] = rounded;
                        fixedVar[vi] = true;
                        stats.roundedVariables++;
                    }
                }
                stats.roundingRounds++;
                SolveSystem(roundIterations);
            }
            var open = new List<int>();
            for (var i = 0; i < 2 * translationBlocks; i++) if (!fixedVar[i]) open.Add(i);
            while (open.Count > 0)
            {
                open.Sort((a, b) => Frac(x[a]).CompareTo(Frac(x[b])));
                var take = Math.Max(1, (int)Math.Ceiling(open.Count * roundingBatch));
                for (var i = 0; i < take; i++)
                {
                    var vi = open[i];
                    var rounded = Math.Round(x[vi]);
                    stats.maxRoundingMove = Mathf.Max(stats.maxRoundingMove, (float)Math.Abs(rounded - x[vi]));
                    x[vi] = rounded;
                    fixedVar[vi] = true;
                    stats.roundedVariables++;
                }
                open.RemoveRange(0, take);
                stats.roundingRounds++;
                SolveSystem(roundIterations);
            }

            stats.flipsByStage += "trans " + CountFlips() + " ";
            var foldedBeforeFeatures = FoldFlags();
            // Feature coordinates: once the translations are integers, round every pinned
            // coordinate at once and solve again, so each crease lands on a grid line.
            if (featureEdges != null && pinFeatures)
            {
                var any = false;
                for (var v = 0; v < n; v++)
                {
                    var b = masterOf[v];
                    if (b < 0) continue;
                    for (var comp = 0; comp < 2; comp++)
                    {
                        if (!(comp == 0 ? pinU[v] : pinV[v])) continue;
                        var vi = 2 * b + comp;
                        if (fixedVar[vi]) continue;
                        x[vi] = Math.Round(x[vi]);
                        fixedVar[vi] = true;
                        stats.pinnedFeatureCoordinates++;
                        any = true;
                    }
                }
                if (any) { stats.roundingRounds++; SolveSystem(roundIterations); }

                // A crease whose rounding folded the faces round it goes back to a straight line at
                // a free coordinate. Rounding moves each crease by up to half a cell, and two
                // creases closer than a cell -- both edges of a strap, a collar's lip -- can land
                // on one grid line or a whole cell apart; either way the cells between them fold.
                // Released, the crease is still a single iso-line (its rows hold it), just not an
                // integer one.
                if (any && featureRollbackRounds > 0)
                {
                    var group = new int[2 * n];
                    for (var v = 0; v < 2 * n; v++) group[v] = v;
                    int Find(int v) { while (group[v] != v) { group[v] = group[group[v]]; v = group[v]; } return v; }
                    foreach (var (a, b) in usedFeatureEdges) { var ra = Find(a); var rb = Find(b); if (ra != rb) group[ra] = rb; }
                    var released = new bool[2 * n];
                    for (var round = 0; round < featureRollbackRounds; round++)
                    {
                        var after = FoldFlags();
                        var bad = new bool[n];
                        for (var f = 0; f < m; f++)
                        {
                            if (!after[f] || foldedBeforeFeatures[f]) continue;
                            for (var k = 0; k < 3; k++) bad[T[3 * f + k]] = true;
                        }
                        for (var ring = 0; ring < featureRollbackRings; ring++)
                        {
                            var next = (bool[])bad.Clone();
                            for (var f = 0; f < m; f++)
                            {
                                if (!(bad[T[3 * f]] || bad[T[3 * f + 1]] || bad[T[3 * f + 2]])) continue;
                                for (var k = 0; k < 3; k++) next[T[3 * f + k]] = true;
                            }
                            bad = next;
                        }
                        var badGroup = new HashSet<int>();
                        for (var v = 0; v < n; v++)
                        {
                            if (!bad[v]) continue;
                            for (var comp = 0; comp < 2; comp++)
                                if ((comp == 0 ? pinU[v] : pinV[v]) && !released[2 * v + comp]) badGroup.Add(Find(2 * v + comp));
                        }
                        if (badGroup.Count == 0) break;
                        var freed = 0;
                        for (var v = 0; v < n; v++)
                        {
                            var b = masterOf[v];
                            if (b < 0 || singular[v]) continue;
                            for (var comp = 0; comp < 2; comp++)
                            {
                                if (!(comp == 0 ? pinU[v] : pinV[v])) continue;
                                if (!badGroup.Contains(Find(2 * v + comp))) continue;
                                released[2 * v + comp] = true;
                                if (!fixedVar[2 * b + comp]) continue;
                                fixedVar[2 * b + comp] = false;
                                freed++;
                            }
                        }
                        if (freed == 0) break;
                        stats.releasedFeatureGroups += badGroup.Count;
                        stats.releasedFeatureCoordinates += freed;
                        stats.roundingRounds++;
                        SolveSystem(roundIterations);
                        stats.flipsByStage += "release" + round + " " + CountFlips() + " ";
                    }
                }
            }

            stats.flipsByStage += "feat " + CountFlips() + " ";

            if (diagnoseClosures)
            {
                // Where the folds are: by the distance, in rings, to a pinned feature vertex and to
                // a singularity.
                var nearFeature = new bool[n];
                var nearSingular = new bool[n];
                for (var v = 0; v < n; v++) { nearFeature[v] = pinU[v] || pinV[v]; nearSingular[v] = singular[v]; }
                for (var ring = 0; ring < 2; ring++)
                {
                    var nf = (bool[])nearFeature.Clone();
                    var ns = (bool[])nearSingular.Clone();
                    for (var f = 0; f < m; f++)
                    {
                        bool anyF = false, anyS = false;
                        for (var k = 0; k < 3; k++) { anyF |= nearFeature[T[3 * f + k]]; anyS |= nearSingular[T[3 * f + k]]; }
                        for (var k = 0; k < 3; k++) { if (anyF) nf[T[3 * f + k]] = true; if (anyS) ns[T[3 * f + k]] = true; }
                    }
                    nearFeature = nf;
                    nearSingular = ns;
                }
                int both = 0, onlyF = 0, onlyS = 0, neither = 0;
                for (var f = 0; f < m; f++)
                {
                    if (!(fArea[f] > 0f)) continue;
                    double ux = 0, uy = 0, vx = 0, vy = 0;
                    for (var k = 0; k < 3; k++)
                    {
                        var corner = 3 * f + k;
                        double cu = 0, cv = 0;
                        for (var i = 0; i < cornerCount[corner]; i++)
                        {
                            var t = pool[cornerStart[corner] + i];
                            double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                            if (t.rot == 0) { cu += t.sign * xu; cv += t.sign * xv; }
                            else { cu -= t.sign * xv; cv += t.sign * xu; }
                        }
                        ux += gradX[corner] * cu; uy += gradY[corner] * cu;
                        vx += gradX[corner] * cv; vy += gradY[corner] * cv;
                    }
                    if (ux * vy - uy * vx > 0) continue;
                    bool nF = false, nS = false;
                    for (var k = 0; k < 3; k++) { nF |= nearFeature[T[3 * f + k]]; nS |= nearSingular[T[3 * f + k]]; }
                    if (nF && nS) both++; else if (nF) onlyF++; else if (nS) onlyS++; else neither++;
                }
                stats.closureKinds += $"folds[feat {onlyF} sing {onlyS} both {both} neither {neither}] ";
            }

            // --- 7d. untangling --------------------------------------------------------------------
            // What still folds after the solve is opened locally: round every folded face, each
            // free vertex moves to the mean of its neighbours as seen in its own chart (Tutte's
            // embedding, one Gauss-Seidel sweep at a time), the integers held where they are. A
            // vertex moves its master coordinates and every corner follows through the corner
            // expressions, so the map stays seamless. Folds are what the tracer cannot cross.
            if (untangleSweeps > 0)
            {
                var faceFolded = new bool[m];
                double CornerU(int corner, int comp)
                {
                    double sum = 0;
                    for (var i = 0; i < cornerCount[corner]; i++)
                    {
                        var t = pool[cornerStart[corner] + i];
                        double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                        double cu, cv;
                        if (t.rot == 0) { cu = xu; cv = xv; } else { cu = -xv; cv = xu; }
                        sum += t.sign * (comp == 0 ? cu : cv);
                    }
                    return sum;
                }
                bool Folded(int f)
                {
                    double u0 = CornerU(3 * f, 0), v0 = CornerU(3 * f, 1);
                    double u1 = CornerU(3 * f + 1, 0), v1 = CornerU(3 * f + 1, 1);
                    double u2 = CornerU(3 * f + 2, 0), v2 = CornerU(3 * f + 2, 1);
                    return (u1 - u0) * (v2 - v0) - (u2 - u0) * (v1 - v0) <= 1e-12;
                }
                // The master term of each corner of v: its quarter-turn and sign.
                int MasterRot(int corner, int block)
                {
                    for (var i = 0; i < cornerCount[corner]; i++)
                    {
                        var t = pool[cornerStart[corner] + i];
                        if (t.block == block) return t.rot + (t.sign < 0 ? 2 : 0);
                    }
                    return -1;
                }
                var region = new bool[n];
                for (var round = 0; round < untangleRounds; round++)
                {
                    var folds = 0;
                    Array.Clear(region, 0, n);
                    for (var f = 0; f < m; f++)
                    {
                        faceFolded[f] = fArea[f] > 0f && Folded(f);
                        if (!faceFolded[f]) continue;
                        folds++;
                        for (var k = 0; k < 3; k++) region[T[3 * f + k]] = true;
                    }
                    if (round == 0) stats.foldsBeforeUntangle = folds;
                    if (folds == 0) break;
                    for (var ring = 0; ring < untangleRings; ring++)
                    {
                        var next = (bool[])region.Clone();
                        for (var f = 0; f < m; f++)
                        {
                            if (!(region[T[3 * f]] || region[T[3 * f + 1]] || region[T[3 * f + 2]])) continue;
                            for (var k = 0; k < 3; k++) next[T[3 * f + k]] = true;
                        }
                        region = next;
                    }
                    for (var sweep = 0; sweep < untangleSweeps; sweep++)
                    {
                        for (var v = 0; v < n; v++)
                        {
                            if (!region[v] || boundary[v]) continue;
                            var b = masterOf[v];
                            if (b < 0) continue;
                            bool freeU = !fixedVar[2 * b], freeV = !fixedVar[2 * b + 1];
                            if (!freeU && !freeV) continue;
                            double su = 0, sv = 0;
                            var count = 0;
                            var ok = true;
                            foreach (var c in cornersOf[v])
                            {
                                var k = MasterRot(c, b);
                                if (k < 0) { ok = false; break; }
                                var f = c / 3;
                                var kc = c % 3;
                                double cu = CornerU(c, 0), cv = CornerU(c, 1);
                                for (var side = 1; side <= 2; side++)
                                {
                                    var w = 3 * f + (kc + side) % 3;
                                    double eu = CornerU(w, 0) - cu, ev = CornerU(w, 1) - cv;
                                    // Back to the master chart: R(-k).
                                    double mu, mv;
                                    switch (k & 3)
                                    {
                                        case 0: mu = eu; mv = ev; break;
                                        case 1: mu = ev; mv = -eu; break;
                                        case 2: mu = -eu; mv = -ev; break;
                                        default: mu = -ev; mv = eu; break;
                                    }
                                    su += mu;
                                    sv += mv;
                                    count++;
                                }
                            }
                            if (!ok || count == 0) continue;
                            if (freeU) x[2 * b] += untangleStep * su / count;
                            if (freeV) x[2 * b + 1] += untangleStep * sv / count;
                        }
                    }
                    stats.untangleRounds++;
                }
                var after = 0;
                for (var f = 0; f < m; f++) if (fArea[f] > 0f && Folded(f)) after++;
                stats.flipsByStage += "untangle " + after + " ";
            }

            // --- 7c. stiffening with the integers held ---------------------------------------
            // Rounding moves the map by up to half a cell at every integer, and the faces between
            // two of them absorb it -- where that is more than they can, they fold. The same
            // stiffening as before rounding, now with every integer fixed, pushes the distortion
            // away from the folds into faces that can take it.
            for (var iteration = 0; iteration < postStiffenIterations; iteration++)
            {
                if (!Stiffen(postStiffenThreshold, stiffenRate, postStiffenFoldPenalty)) break;
                matrix = Assemble();
                SolveSystem(roundIterations);
                stats.flipsByStage += "post" + iteration + " " + CountFlips() + " ";
            }
            if (untangleFinalRounds > 0) BarrierUntangle(untangleFinalRounds, untangleInner, "barrier");
            foreach (var row in closureRows)
            {
                double ru = 0, rv = 0;
                foreach (var t in row)
                {
                    double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                    if (t.rot == 0) { ru += t.sign * xu; rv += t.sign * xv; }
                    else { ru -= t.sign * xv; rv += t.sign * xu; }
                }
                var r = (float)Math.Max(Math.Abs(ru), Math.Abs(rv));
                if (r > 0.05f) stats.closuresOpen++;
                stats.closureWorst = Mathf.Max(stats.closureWorst, r);
            }

            readLattice:
            // --- 8. read the lattice off ---------------------------------------------------------
            double U(int corner, int comp)
            {
                double s = 0;
                for (var i = 0; i < cornerCount[corner]; i++)
                {
                    var t = pool[cornerStart[corner] + i];
                    double xu = x[2 * t.block], xv = x[2 * t.block + 1];
                    double cu, cv;
                    if (t.rot == 0) { cu = xu; cv = xv; } else { cu = -xv; cv = xu; }
                    s += t.sign * (comp == 0 ? cu : cv);
                }
                return s;
            }

            var faceJ = new double[4 * m];
            var cornerUvExactA = cornerUvExact;
            for (var f = 0; f < m; f++)
            {
                double ux = 0, uy = 0, vx = 0, vy = 0;
                for (var k = 0; k < 3; k++)
                {
                    var corner = 3 * f + k;
                    var u = U(corner, 0);
                    var v = U(corner, 1);
                    ux += gradX[corner] * u; uy += gradY[corner] * u;
                    vx += gradX[corner] * v; vy += gradY[corner] * v;
                }
                faceJ[4 * f] = ux; faceJ[4 * f + 1] = uy; faceJ[4 * f + 2] = vx; faceJ[4 * f + 3] = vy;
                var faceDet = ux * vy - uy * vx;
                if (faceDet <= 0) stats.flippedFaces++;
                for (var k = 0; k < 3; k++)
                {
                    var corner = 3 * f + k;
                    var cu = U(corner, 0);
                    var cv = U(corner, 1);
                    cornerUv[corner] = new Vector2((float)cu, (float)cv);
                    cornerUvExactA[2 * corner] = cu;
                    cornerUvExactA[2 * corner + 1] = cv;
                    cornerNodes[corner] = new Vector2Int((int)Math.Round(cu), (int)Math.Round(cv));
                }
                // d(position)/du in the face plane; the frame axis where the map is degenerate.
                faceUAxis[f] = Math.Abs(faceDet) > 1e-20
                    ? MeshMath.SafeNormalize(faceE1[f] * (float)(vy / faceDet) + faceE2[f] * (float)(-vx / faceDet), faceE1[f])
                    : faceE1[f];
            }

            for (var v = 0; v < n; v++)
            {
                var list = cornersOf[v];
                if (list == null) continue;
                // The incident face whose map is best conditioned.
                var best = -1;
                var bestDet = 0.0;
                foreach (var c in list)
                {
                    var f = c / 3;
                    var det = faceJ[4 * f] * faceJ[4 * f + 3] - faceJ[4 * f + 1] * faceJ[4 * f + 2];
                    if (det > bestDet) { bestDet = det; best = c; }
                }
                var hv = spacing[v];
                if (best < 0)
                {
                    continue;   // every face here folded: leave the position field's own value
                }
                var bf = best / 3;
                double a = faceJ[4 * bf], b = faceJ[4 * bf + 1], cc = faceJ[4 * bf + 2], d = faceJ[4 * bf + 3];
                var detJ = a * d - b * cc;
                // Inverse map: face-plane displacement per unit of (u, v).
                double ia = d / detJ, ib = -b / detJ, ic = -cc / detJ, id = a / detJ;
                var uu = U(best, 0);
                var vv = U(best, 1);
                var du = Math.Round(uu) - uu;
                var dv = Math.Round(vv) - vv;
                var px = ia * du + ib * dv;
                var py = ic * du + id * dv;
                var move = faceE1[bf] * (float)px + faceE2[bf] * (float)py;
                if (move.magnitude > 1.5f * hv) move = move.normalized * (1.5f * hv);
                var node = P[v] + move;
                node -= N[v] * Vector3.Dot(N[v], node - P[v]);
                latticeNodes[v] = node;

                var axis3 = faceE1[bf] * (float)ia + faceE2[bf] * (float)ic;   // d(position)/du
                var axis2 = faceE1[bf] * (float)ib + faceE2[bf] * (float)id;   // d(position)/dv
                // The vertex's cross: every incident chart's u axis, combed together. One face's
                // axis alone is noisy enough that the grid map's own singularity test disagrees
                // with the charts round the vertex.
                var axis = Vector3.zero;
                var counted = 0;
                foreach (var c in list)
                {
                    var f = c / 3;
                    var detF = faceJ[4 * f] * faceJ[4 * f + 3] - faceJ[4 * f + 1] * faceJ[4 * f + 2];
                    if (detF <= 0) continue;
                    var du3 = faceE1[f] * (float)(faceJ[4 * f + 3] / detF) + faceE2[f] * (float)(-faceJ[4 * f + 2] / detF);
                    du3 = Project(du3, N[v]);
                    if (du3.sqrMagnitude <= 1e-24f) continue;
                    du3 = du3.normalized;
                    if (counted == 0) axis = du3;
                    else
                    {
                        OrientationField.Compat4(axis, N[v], du3, N[v], out var ca, out var cb);
                        axis = MeshMath.SafeNormalize(ca * counted + cb, ca);
                    }
                    counted++;
                }
                if (counted == 0) axis = Project(axis3, N[v]);
                if (axis.sqrMagnitude > 1e-24f) axes[v] = axis.normalized;
                var cell = 0.5f * (axis3.magnitude + axis2.magnitude);
                if (cell > 0.25f * hv && cell < 4f * hv) spacing[v] = cell;
            }

            // Combinatorial ranks. Each vertex's cross is its master chart's u axis; a corner whose
            // chart is the master's turned by k quarter-turns (its master term carries R(k), and
            // coordinates turning by k means axes turning by -k) has rank -k. Every edge then agrees
            // exactly between its two faces -- the transition's turn is in both ranks and both
            // offsets -- and only a singular vertex's fan fails to close, which is the singularity.
            for (var v = 0; v < n; v++)
            {
                var mc = masterCorner[v];
                if (mc < 0) continue;
                var axis = Project(faceUAxis[mc / 3], N[v]);
                if (axis.sqrMagnitude <= 1e-24f) continue;
                axes[v] = axis.normalized;
                foreach (var c in cornersOf[v])
                {
                    for (var i = 0; i < cornerCount[c]; i++)
                    {
                        var t = pool[cornerStart[c] + i];
                        if (t.block != masterOf[v]) continue;
                        var k = t.rot + (t.sign < 0 ? 2 : 0);
                        cornerRank[c] = Mod4(-k);
                        break;
                    }
                }
            }

            // Per half-edge: the face across it and the quarter-turns from this face's chart axes
            // to that face's -- the cut's jump from its left, the inverse from its right, zero
            // everywhere else (same chart).
            for (var c = 0; c < 3 * m; c++)
            {
                halfEdgeAcross[c] = across[c];
                var e = edgeOf[c];
                if (across[c] < 0 || e < 0 || !cut[e]) continue;
                var p = Jump(e, leftFace[e]);
                halfEdgeTurn[c] = c / 3 == leftFace[e] ? p : Mod4(-p);
            }

            stats.ms = watch.ElapsedMilliseconds;
            if (debugPassLog != null) debugPassLog.AppendLine("PASS " + stats);
        }

        /// <summary>Quarter-turns (0..3) on the term of `block` in an expression, or -1.</summary>
        static int MasterTurn(List<Term> terms, int block)
        {
            foreach (var t in terms)
                if (t.block == block) return t.rot + (t.sign < 0 ? 2 : 0);
            return -1;
        }

        static double Frac(double value) => Math.Abs(value - Math.Round(value));

        static int Mod4(int k) => ((k % 4) + 4) % 4;

        static Vector3 Project(Vector3 v, Vector3 n) => v - n * Vector3.Dot(n, v);

        /// <summary>v turned k quarter turns counter-clockwise about n.</summary>
        static Vector3 Rotate(Vector3 v, Vector3 n, int k)
        {
            k = Mod4(k);
            for (var i = 0; i < k; i++) v = Vector3.Cross(n, v);
            return v;
        }

        /// <summary>The k for which `target` is closest to `reference` turned k quarter turns.</summary>
        static int BestTurn(Vector3 reference, Vector3 target, Vector3 n)
        {
            var best = 0;
            var bestDot = float.NegativeInfinity;
            var r = Project(reference, n);
            for (var k = 0; k < 4; k++)
            {
                var d = Vector3.Dot(Rotate(r, n, k), target);
                if (d > bestDot) { bestDot = d; best = k; }
            }
            return best;
        }

        static Term Turn(Term t, int quarterTurns)
        {
            var k = Mod4(t.rot + quarterTurns);
            var sign = t.sign;
            if (k >= 2) { k -= 2; sign = -sign; }
            return new Term { block = t.block, rot = k, sign = sign };
        }

        static List<Term> Simplify(List<Term> terms)
        {
            var result = new List<Term>(terms.Count);
            foreach (var t in terms)
            {
                var merged = false;
                for (var i = 0; i < result.Count; i++)
                {
                    if (result[i].block != t.block || result[i].rot != t.rot) continue;
                    var s = result[i].sign + t.sign;
                    if (s == 0) result.RemoveAt(i);
                    else result[i] = new Term { block = t.block, rot = t.rot, sign = s };
                    merged = true;
                    break;
                }
                if (!merged) result.Add(t);
            }
            return result;
        }

        /// <summary>Symmetric sparse matrix in CSR with a Jacobi-preconditioned CG.</summary>
        sealed class Csr
        {
            public int n;
            public int[] start;
            public int[] col;
            public double[] val;
            public double[] diag;

            public static Csr FromTriplets(int n, List<long> keys, List<double> vals)
            {
                var k = keys.ToArray();
                var v = vals.ToArray();
                Array.Sort(k, v);
                var start = new int[n + 1];
                var cols = new List<int>(k.Length / 3);
                var values = new List<double>(k.Length / 3);
                var i = 0;
                var row = 0;
                while (i < k.Length)
                {
                    var key = k[i];
                    var sum = 0.0;
                    while (i < k.Length && k[i] == key) sum += v[i++];
                    var r = (int)(key >> 32);
                    var c = (int)(key & 0xffffffff);
                    while (row < r) start[++row] = cols.Count;
                    cols.Add(c);
                    values.Add(sum);
                }
                while (row < n) start[++row] = cols.Count;
                var m = new Csr { n = n, start = start, col = cols.ToArray(), val = values.ToArray(), diag = new double[n] };
                for (var r = 0; r < n; r++)
                    for (var j = start[r]; j < start[r + 1]; j++)
                        if (m.col[j] == r) m.diag[r] = m.val[j];
                return m;
            }

            void Multiply(double[] x, double[] y, bool[] mask)
            {
                System.Threading.Tasks.Parallel.For(0, n, BackgroundScheduler.Options, r =>
                {
                    if (mask[r]) { y[r] = 0; return; }
                    double s = 0;
                    for (var j = start[r]; j < start[r + 1]; j++)
                    {
                        var c = col[j];
                        if (!mask[c]) s += val[j] * x[c];
                    }
                    y[r] = s;
                });
            }

            /// <summary>Solves the free rows with the fixed entries of x held; returns iterations.</summary>
            public int Solve(double[] b, double[] x, bool[] fixedVar, int maxIterations, float tolerance, out float residual)
            {
                // r = b - A x, with the fixed values' contribution moved to the right.
                var r = new double[n];
                var z = new double[n];
                var p = new double[n];
                var q = new double[n];
                System.Threading.Tasks.Parallel.For(0, n, BackgroundScheduler.Options, i =>
                {
                    if (fixedVar[i]) { r[i] = 0; return; }
                    double s = b[i];
                    for (var j = start[i]; j < start[i + 1]; j++) s -= val[j] * x[col[j]];
                    r[i] = s;
                });
                double rz = 0, bn = 0;
                for (var i = 0; i < n; i++)
                {
                    if (fixedVar[i]) continue;
                    z[i] = diag[i] > 0 ? r[i] / diag[i] : r[i];
                    p[i] = z[i];
                    rz += r[i] * z[i];
                    bn += b[i] * b[i];
                }
                bn = Math.Sqrt(Math.Max(bn, 1e-300));
                residual = 0f;
                var it = 0;
                for (; it < maxIterations; it++)
                {
                    Multiply(p, q, fixedVar);
                    double pq = 0;
                    for (var i = 0; i < n; i++) pq += p[i] * q[i];
                    if (!(pq > 0)) break;
                    var alpha = rz / pq;
                    double rr = 0, rzNext = 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (fixedVar[i]) continue;
                        x[i] += alpha * p[i];
                        r[i] -= alpha * q[i];
                        z[i] = diag[i] > 0 ? r[i] / diag[i] : r[i];
                        rzNext += r[i] * z[i];
                        rr += r[i] * r[i];
                    }
                    residual = (float)(Math.Sqrt(rr) / bn);
                    if (residual < tolerance) { it++; break; }
                    var beta = rzNext / rz;
                    rz = rzNext;
                    for (var i = 0; i < n; i++)
                        if (!fixedVar[i]) p[i] = z[i] + beta * p[i];
                }
                return it;
            }
        }
    }
}
