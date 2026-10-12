// A global seamless parametrisation: the integer grid solved for the whole surface at once.
//
// From Bommes, Zimmer and Kobbelt, "Mixed-Integer Quadrangulation" (SIGGRAPH 2009), the method
// FreeRemesher's engine is built on (its strings name "U+V integer", "GridPole" and a CHOLMOD
// solver). Nothing of that binary is used; this is written from the paper.
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
        public float closureWorst;
        public long factorNonZeros;
        public long symbolicMs, factorMs;
        public int factorizations, factorFailures;
        public long transferMs;
        public long structureMs, expressionMs, assemblyMs;
        public string split = "";
        public int refinedSingularFaces, unmappedFaces;
        public long ms;

        public override string ToString() =>
            $"faces={faces} singular={singularVertices} cancelled={cancelledPairs}+{cancelledFarPairs}(fail {pairCancelFailed}) poles={roundedSingularities} stiff={stiffenIterationsRun}/{stiffened} reused={reusedFreeSolve} features={featureEdges}(skip {featureEdgesSkipped}, nearPoles {featureEdgesNearPoles}, pins {pinnedFeatureCoordinates}) cut={cutEdges}(of {cutEdgesBeforePrune}) chains={chains} " +
            $"blocks={blocks} closures={closureConstraints} cg={cgIterations} res={residual:G3} " +
            $"rounds={roundingRounds} rounded={roundedVariables} maxRound={maxRoundingMove:F3} " +
            $"flipped={flippedFaces} stages={flipsByStage}closuresOpen={closuresOpen} propagated={propagatedChains} nonInt={closureNonInteger} forced={forcedChains} free={freeChains} tree={treeChains} freedPoles={freedPoles} regRoots={regularRoots} unsolved={unsolvedTree} kinds={closureKinds}worst={closureWorst:F2} nnzL={factorNonZeros} symbolic={symbolicMs} factor={factorMs}/{factorizations} failed={factorFailures} ms={ms} (split {split}structure {structureMs} expr {expressionMs} asm {assemblyMs}) transfer={transferMs} fineSingular={refinedSingularFaces} unmapped={unmappedFaces} trace[{trace}]";
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

        /// <summary>Diagnostics: classify the closures left open after rounding.</summary>
        public static bool diagnoseClosures = false;

        /// <summary>Order the cut translations last in the factorisation (see SparseLdlt).</summary>
        public static bool chainsLast = false;

        /// <summary>
        /// Cancel opposite singularities up to this many cells apart along the shortest path
        /// between them (0 = adjacent pairs only). See step 2c.
        /// </summary>
        public static float cancelPairRadiusCells = 0f;

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

            for (var v = 0; v < n; v++) if (singular[v]) stats.singularVertices++;

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
            // Closures, in cells; weighted against a face's energy, which is about one per face.
            var closureW = Math.Sqrt(closureWeight);
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
                var featureW = Math.Sqrt(featureWeight);
                var faceOfEdge = new Dictionary<long, int>(3 * m, EdgeKey.Comparer);
                for (var f = 0; f < m; f++)
                    for (var k = 0; k < 3; k++)
                    {
                        int a = T[3 * f + k], b = T[3 * f + (k + 1) % 3];
                        var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                        if (!faceOfEdge.ContainsKey(key)) faceOfEdge[key] = 3 * f + k;
                    }
                foreach (var (a, b) in featureEdges)
                {
                    if (a == b) continue;
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
                    if (Mathf.Max(alongU, alongV) < featureMinAlignment) { if (firstAssembly) stA.featureEdgesSkipped++; continue; }
                    // Running along u, v is constant; along v, u is.
                    var comp = alongU >= alongV ? 1 : 0;
                    rowVar.Clear();
                    rowCoef.Clear();
                    for (var i = 0; i < cornerCount[ca]; i++) Emit(pool[cornerStart[ca] + i], comp, 1.0);
                    for (var i = 0; i < cornerCount[cb]; i++) Emit(pool[cornerStart[cb] + i], comp, -1.0);
                    AddRow(0.0, featureW);
                    if (!firstAssembly) continue;
                    stA.featureEdges++;
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
                    }
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
                stats.factorNonZeros = ldlt.factorNonZeros;
                stats.symbolicMs = symbolic.ElapsedMilliseconds;
            }

            var st = stats;
            void SolveSystem(int iterations)
            {
                if (ldlt == null)
                {
                    st.cgIterations += matrix.Solve(rhs, x, fixedVar, iterations, tolerance, out st.residual);
                    return;
                }
                var numeric = System.Diagnostics.Stopwatch.StartNew();
                if (!ldlt.Factor(matrix.val, fixedVar))
                {
                    st.factorFailures++;
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
                    stats.reusedFreeSolve = true;
                }
            }

            int CountFlips()
            {
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

            // --- 7a. singularities onto grid nodes ----------------------------------------------
            // A singularity whose (u, v) is not an integer sits inside a cell -- often its centre,
            // where the quarter-turn round it maps the lattice onto itself -- and the cells round it
            // fold to make room. Rounded first, the translations of the cuts that end there then
            // round to values consistent with it. (FreeRemesher's own flag for this is "GridPole".)
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
                System.Threading.Tasks.Parallel.For(0, n, r =>
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
                System.Threading.Tasks.Parallel.For(0, n, i =>
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
