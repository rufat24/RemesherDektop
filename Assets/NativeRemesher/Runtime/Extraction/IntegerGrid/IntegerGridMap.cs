// The integer grid map: the stage between the two fields and the quads.
//
// This is QuadriFlow's ComputeIndexMap (Huang et al. 2018, BSD-3), ported rather than re-derived.
// It is also, at the level of the mathematics, what Exoside's engine does — its own strings name
// integer u/v variables, seam-variable substitution and pinned "GridPole" singularities solved by a
// direct Cholesky factorisation — and it is exactly the stage this project's v3 attempt lacked when
// it tried to read quads off a least-squares (u,v) map and got folds everywhere.
//
// WHAT IT HOLDS. One integer offset per undirected input edge: how many lattice cells apart its two
// endpoints' lattice points are, stated in the frame of the lower-indexed endpoint. Each face carries,
// per edge, the quarter-turn rotation that brings that offset into the face's own frame, so that the
// three rotated offsets of a face must sum to zero for the face to be a genuine triangle of an
// integer grid. Where the position field left them not summing to zero, the face is a defect; the
// defect is conserved across edges, and routing it away is a flow problem.
//
// WHAT IT DOES, in order (see IntegerGridExtract for the last step):
//   Build                edge offsets from the fields, singular faces, sharp-edge locks
//   BuildIntegerConstraints  per-face rotations, one global frame per component via an orient
//                        tree, then per-component balancing of supply against demand
//   SolveFlow            minimum-cost flow of the residual over the triangle dual, with widening
//                        capacity until every unit routes
//   Subdivide            split every edge whose offset still spans more than one cell, keeping
//                        the per-face closure exact by construction (IntegerGridSubdivide)
//   FixFlips             greedy removal of parametrically inverted faces (EdgeHierarchy)
//
// Every quantity here is an integer or a quarter turn. There is no tolerance anywhere, which is the
// property the greedy per-edge collapse never had.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class IntegerGridStats
    {
        public int inputVertices, inputFaces, inputEdges;
        public int singularFaces;
        public int sharpEdges;

        /// <summary>Faces whose three offsets did not sum to zero before the flow.</summary>
        public int unsatisfiedBefore;

        /// <summary>Offsets changed to balance each component's supply against its demand.</summary>
        public int valveChangesPass1, valveChangesPass2;

        /// <summary>Detached pieces dropped as debris after extraction, and their faces.</summary>
        public int debrisPieces, debrisFaces;

        /// <summary>Hole caps removed because they made an edge non-manifold.</summary>
        public int capsDroppedNonManifold;

        /// <summary>Open and non-manifold edges right after hole closing, and at the end of extraction.</summary>
        public int openAfterClose, nonManifoldAfterClose, openAtEnd, nonManifoldAtEnd;

        /// <summary>Short rims capped by the undirected fallback.</summary>
        public int smallLoopsCapped;

        public int flowNodes, flowArcs;
        public int flowSupply, flowRouted, flowIterations, flowCapacity;
        public long flowCost;

        /// <summary>Faces still not summing to zero after the flow. Each one is a hole or a fold.</summary>
        public int unsatisfiedAfter;

        /// <summary>Edge offsets that exceeded one lattice cell and were clamped back to unit.</summary>
        public int edgeDiffClamped;

        public int subdivideSplitsFirst, subdivideSplitsSecond;
        public int subdivideOutOfRange;
        /// <summary>Long edges split while a face on one side was folded. Diagnostic only.</summary>
        public int subdivideSplitOnFlipped;
        /// <summary>Splits refused because one edge had already been split four times.</summary>
        public int subdivideEdgeCapped;
        /// <summary>Over-length edges refused because they are far shorter than a cell: folds.</summary>
        public int subdivideFoldRefused;
        /// <summary>Splits refused because a face had already taken part in too many: a cycle.</summary>
        public int subdivideFaceCycleRefused;
        /// <summary>Edges still over length when the budget ran out. Falling = it was converging.</summary>
        public int subdivideStillLong;
        public bool subdivideAbandoned;
        public string subdivideTrace;

        /// <summary>The global position least squares: size, cost and how far it moved things.</summary>
        /// <summary>The crease stage: vertices on creases, chains traced, cells and vertices pinned.</summary>
        public int sharpVertices, sharpChains, sharpConstrainedCells, sharpConstrainedVertices;
        public long sharpMs;

        /// <summary>Output vertices moved onto their feature vertices, refused as too far, and the largest move seen.</summary>
        public int featurePlacements, featurePlacementsRefused;
        public float featurePlacementMaxMove;

        public int positionUnknowns, positionNonZeros, positionIterations;
        public float positionMeanMove, positionMeanProject;

        public int flippedBefore, flippedAfter;
        public int flipPasses;

        public int coarseLevels;
        public int compactVertices, outputVertices;
        public int flippedTriangles, degenerateQuadsRemoved;
        public int unpairedDiagonals;
        public int holeQuads;
        public int valence2Removed, nonManifoldSplits, highValenceSplits;

        public int flowPhases;
        public int needlesRemoved;
        public int flowCoarseLevel;
        public int flowCoarseLevels, flowCoarseFaces, flowCoarseSupply, flowCoarseRouted;
        public long flowCoarseMs;
        public long flowSettled;
        public long buildMs, constraintsMs, flowMs, subdivideMs, flipMs, positionMs, extractMs, totalMs;

        public override string ToString()
        {
            return $"igm:      in v={inputVertices} f={inputFaces} e={inputEdges} sing={singularFaces} " +
                   $"sharp={sharpEdges} unsat={unsatisfiedBefore}->{unsatisfiedAfter} " +
                   $"valve={valveChangesPass1}+{valveChangesPass2} " +
                   $"flow: supply={flowSupply} routed={flowRouted} iters={flowIterations} " +
                   $"cap={flowCapacity} cost={flowCost} arcs={flowArcs} phases={flowPhases} settled={flowSettled} " +
                   $"coarse: levels={flowCoarseLevels} at={flowCoarseLevel} f={flowCoarseFaces} routed={flowCoarseRouted}/{flowCoarseSupply} ms={flowCoarseMs} | " +
                   $"crease: v={sharpVertices} chains={sharpChains} cells={sharpConstrainedCells} pinned={sharpConstrainedVertices} place={featurePlacements} refused={featurePlacementsRefused} maxMove={featurePlacementMaxMove:F4} | " +
                   $"pos: n={positionUnknowns} nnz={positionNonZeros} cg={positionIterations} move={positionMeanMove:F5} proj={positionMeanProject:F5} | " +
                   $"clamped={edgeDiffClamped} subdiv={subdivideSplitsFirst}+{subdivideSplitsSecond} oor={subdivideOutOfRange} onFlip={subdivideSplitOnFlipped} capped={subdivideEdgeCapped} foldRefused={subdivideFoldRefused} cycleRefused={subdivideFaceCycleRefused} stillLong={subdivideStillLong}" +
                   (subdivideAbandoned ? " ABANDONED " : " ") +
                   $"flips={flippedBefore}->{flippedAfter} ({flipPasses} passes) | " +
                   $"levels={coarseLevels} compact={compactVertices} out={outputVertices} " +
                   $"flippedTris={flippedTriangles} degen={degenerateQuadsRemoved} " +
                   $"unpaired={unpairedDiagonals} holeQuads={holeQuads} " +
                   $"v2={valence2Removed} nmSplit={nonManifoldSplits} hiVal={highValenceSplits} " +
                   $"debris={debrisPieces}/{debrisFaces}f needles={needlesRemoved} capsNM={capsDroppedNonManifold} small={smallLoopsCapped} " +
                   $"edges(open/nm) close={openAfterClose}/{nonManifoldAfterClose} end={openAtEnd}/{nonManifoldAtEnd} | " +
                   $"ms build={buildMs} cons={constraintsMs} flow={flowMs} subdiv={subdivideMs} " +
                   $"flip={flipMs} pos={positionMs} extract={extractMs} total={totalMs}";
        }
    }

    public sealed partial class IntegerGridMap
    {
        // The mesh, as growable lists because subdivision adds vertices and faces.
        public readonly List<Vector3> V = new List<Vector3>();
        public readonly List<Vector3> N = new List<Vector3>();
        public readonly List<Vector3> Q = new List<Vector3>();
        public readonly List<Vector3> O = new List<Vector3>();
        /// <summary>Per-vertex lattice spacing, or null when uniform at <see cref="scale"/>.</summary>
        public List<float> S;

        /// <summary>Triangle corners, three per face.</summary>
        public readonly List<int> F = new List<int>();
        /// <summary>Twin of each half-edge, or -1. Half-edge 3f+j runs from F[3f+j] to F[3f+(j+1)%3].</summary>
        public readonly List<int> E2E = new List<int>();
        public readonly List<bool> boundaryVertex = new List<bool>();
        public readonly List<bool> nonManifoldVertex = new List<bool>();

        /// <summary>Per undirected edge: offset from edgeX's cell to edgeY's cell, in edgeX's frame.</summary>
        public readonly List<Vector2Int> edgeDiff = new List<Vector2Int>();
        public readonly List<int> edgeX = new List<int>();

        /// <summary>
        /// Per undirected edge, in the same frame as <see cref="edgeDiff"/>: the position field's own
        /// real-valued offset, before rounding. Filled by BuildEdgeInfo and read only by the
        /// weighted flow; subdivision does not extend it.
        /// </summary>
        public readonly List<Vector2> edgeContinuous = new List<Vector2>();
        public readonly List<int> edgeY = new List<int>();

        /// <summary>Undirected edge of each half-edge.</summary>
        public readonly List<int> feId = new List<int>();
        /// <summary>Quarter turns taking that edge's offset into the face's frame, traversal direction included.</summary>
        public readonly List<int> feOrient = new List<int>();

        /// <summary>Per edge and component (2e, 2e+1): 0 locks the value, 1 lets the flow change it.</summary>
        public readonly List<byte> allow = new List<byte>();
        /// <summary>Per half-edge: lies on a crease the lattice must keep as an iso-line.</summary>
        public readonly List<bool> sharp = new List<bool>();

        public readonly Dictionary<int, int> singularities = new Dictionary<int, int>();

        public float scale;
        public readonly IntegerGridStats stats = new IntegerGridStats();

        int m_Seed;

        public int faceCount => F.Count / 3;
        public int edgeCount => edgeDiff.Count;

        /// <summary>
        /// Diagnostic: when set, the input edges whose integer offset the flow and the flip repair
        /// changed from the position field's own rounding are written here as OBJ line sets, one
        /// file per stage. Null in normal use.
        /// </summary>
        public static string debugChangedEdgesPath;

        /// <summary>Diagnostic: counts written by <see cref="DumpChangedEdges"/>.</summary>
        public static string debugChangedEdgesReport = "";

        /// <summary>Writes <see cref="debugChangedEdgesPath"/> for one stage.</summary>
        public void DumpChangedEdges(Vector2Int[] initialDiff, int[] initialX, int[] initialY, string stage)
        {
            var sb = new System.Text.StringBuilder();
            var count = 0;
            var lines = new System.Text.StringBuilder();
            for (var e = 0; e < Mathf.Min(initialDiff.Length, edgeCount); e++)
            {
                if (edgeX[e] != initialX[e] || edgeY[e] != initialY[e]) continue;
                if (edgeDiff[e] == initialDiff[e]) continue;
                var a = V[edgeX[e]];
                var b = V[edgeY[e]];
                sb.Append("v ").Append((-a.x).ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                    .Append(a.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                    .Append(a.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append((char)10);
                sb.Append("v ").Append((-b.x).ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                    .Append(b.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                    .Append(b.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append((char)10);
                count++;
                lines.Append("l ").Append(2 * count - 1).Append(' ').Append(2 * count).Append((char)10);
            }
            System.IO.File.WriteAllText(debugChangedEdgesPath + "." + stage + ".obj", sb.ToString() + lines);
            debugChangedEdgesReport += $"{stage}={count}/{initialDiff.Length} ";
        }
        public int vertexCount => V.Count;

        /// <summary>Rotates an integer offset by quarter turns: (x, y) -> (-y, x) per turn.</summary>
        public static Vector2Int Rshift90(Vector2Int s, int amount)
        {
            amount = Mod4(amount);
            if ((amount & 1) != 0) s = new Vector2Int(-s.y, s.x);
            if (amount >= 2) s = -s;
            return s;
        }

        /// <summary><see cref="Rshift90(Vector2Int, int)"/> for a real-valued offset.</summary>
        public static Vector2 Rshift90(Vector2 s, int amount)
        {
            amount = Mod4(amount);
            if ((amount & 1) != 0) s = new Vector2(-s.y, s.x);
            if (amount >= 2) s = -s;
            return s;
        }

        public static Vector3 Rotate90By(Vector3 q, Vector3 n, int amount)
        {
            amount = Mod4(amount);
            var r = (amount & 1) != 0 ? Vector3.Cross(n, q) : q;
            return amount < 2 ? r : -r;
        }

        public static int Mod4(int a) => ((a % 4) + 4) % 4;
        public static int Next3(int h) => h % 3 == 2 ? h - 2 : h + 1;
        public static int Prev3(int h) => h % 3 == 0 ? h + 2 : h - 1;

        float ScaleAt(int v) => S != null ? S[v] : scale;

        /// <summary>
        /// Every list that describes the map, for the snapshot below. Kept as one place so a field
        /// added later cannot be silently left out of a restore.
        /// </summary>
        sealed class Snapshot
        {
            public Vector3[] v, n, q, o;
            public float[] s;
            public int[] f, e2e, ex, ey, feid, feorient;
            public bool[] bnd, nonMan, sharp;
            public Vector2Int[] diff;
            public byte[] allow;
        }

        Snapshot Capture() => new Snapshot
        {
            v = V.ToArray(), n = N.ToArray(), q = Q.ToArray(), o = O.ToArray(),
            s = S?.ToArray(),
            f = F.ToArray(), e2e = E2E.ToArray(),
            ex = edgeX.ToArray(), ey = edgeY.ToArray(),
            feid = feId.ToArray(), feorient = feOrient.ToArray(),
            bnd = boundaryVertex.ToArray(), nonMan = nonManifoldVertex.ToArray(),
            sharp = sharp.ToArray(), diff = edgeDiff.ToArray(), allow = allow.ToArray(),
        };

        void Restore(Snapshot snap)
        {
            static void Put<T>(List<T> list, T[] from)
            {
                list.Clear();
                list.AddRange(from);
            }

            Put(V, snap.v); Put(N, snap.n); Put(Q, snap.q); Put(O, snap.o);
            if (snap.s != null) Put(S, snap.s);
            Put(F, snap.f); Put(E2E, snap.e2e);
            Put(edgeX, snap.ex); Put(edgeY, snap.ey);
            Put(feId, snap.feid); Put(feOrient, snap.feorient);
            Put(boundaryVertex, snap.bnd); Put(nonManifoldVertex, snap.nonMan);
            Put(sharp, snap.sharp); Put(edgeDiff, snap.diff); Put(allow, snap.allow);
        }

        static Vector2Int WithComponent(Vector2Int v, int k, int value)
        {
            if (k == 0) v.x = value; else v.y = value;
            return v;
        }

        // ------------------------------------------------------------------
        // Build
        // ------------------------------------------------------------------

        public static IntegerGridMap Build(RemeshTopology topology, OrientationField orientation,
            PositionField position, FeatureGraph features, int seed = 0)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (orientation == null) throw new ArgumentNullException(nameof(orientation));
            if (position == null) throw new ArgumentNullException(nameof(position));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var g = new IntegerGridMap { scale = position.scale, m_Seed = seed };

            g.V.AddRange(topology.positions);
            g.N.AddRange(topology.normals);
            g.Q.AddRange(orientation.directions);
            g.O.AddRange(position.positions);
            if (position.spacing != null) g.S = new List<float>(position.spacing);
            if (position.cornerNodes != null && position.cornerNodes.Length == topology.triangles.Length
                && position.faceUAxis != null && position.faceUAxis.Length * 3 == topology.triangles.Length)
            {
                g.m_CornerNodes = position.cornerNodes;
                g.m_CornerUv = position.cornerUv;
                g.m_FaceUAxis = position.faceUAxis;
                g.m_CornerRank = position.cornerRank;
                g.m_SingularFaces = position.singularFaces;
            }

            g.F.AddRange(topology.triangles);
            g.E2E.AddRange(topology.halfEdgeTwin);
            g.boundaryVertex.AddRange(topology.isBoundaryVertex);
            g.nonManifoldVertex.AddRange(topology.isNonManifoldVertex);

            // Twins must pair up. The topology links a third half-edge on a non-manifold edge to the
            // first opposite it finds without that one linking back, and an asymmetric twin here
            // would let BuildEdgeInfo hand one undirected edge two ids.
            for (var h = 0; h < g.E2E.Count; h++)
            {
                var t = g.E2E[h];
                if (t >= 0 && g.E2E[t] != h) g.E2E[h] = -1;
            }

            for (var h = 0; h < g.F.Count; h++)
            {
                var isSharp = features != null && features.IsFeatureEdge(g.F[h], g.F[Next3(h)]);
                g.sharp.Add(isSharp);
                if (isSharp) g.stats.sharpEdges++;
            }

            g.stats.inputVertices = g.V.Count;
            g.stats.inputFaces = g.faceCount;

            g.ComputeOrientationSingularities();
            g.BuildEdgeInfo();
            if (clampEdgeDiffToUnit) g.ClampEdgeDiffToUnit();
            // Diagnostic switch: the reference pairs this with optimize_positions_sharp/_fixed,
            // which we do not have yet, so it can be turned off to isolate that gap.
            if (applySharpConstraints) g.ApplySharpEdgeConstraints();
            else g.RebuildAllowChanges();

            g.stats.inputEdges = g.edgeCount;
            g.stats.buildMs = watch.ElapsedMilliseconds;
            return g;
        }

        // A global parametrisation's lattice, when Build was handed one: see BuildEdgeInfo.
        Vector2Int[] m_CornerNodes;
        Vector2[] m_CornerUv;
        Vector3[] m_FaceUAxis;
        // Per corner, the quarter-turns that carry the vertex's cross onto its face chart's u
        // axis; the flow's per-face rotations are read from these when present, so they agree
        // with the offsets exactly.
        int[] m_FaceRank;
        int[] m_CornerRank;
        int[] m_SingularFaces;

        /// <summary>
        /// Faces whose three crosses cannot be combed continuously. Also normalises the sign of one
        /// cross per singular face so the accumulated index sits in [0, 4), as the reference does.
        /// </summary>
        void ComputeOrientationSingularities()
        {
            singularities.Clear();
            var faces = faceCount;

            // From a parametrisation: its singular faces, each with the index its neighbours'
            // ranks give it. No cross is re-signed -- the ranks are relative to the crosses as given.
            if (m_CornerRank != null && m_SingularFaces != null)
            {
                foreach (var f in m_SingularFaces)
                {
                    var index = 0;
                    for (var k = 0; k < 3; k++)
                    {
                        var h = 3 * f + k;
                        var twin = E2E[h];
                        // The edge i -> j as its neighbour states it: there it runs j -> i.
                        int ri, rj;
                        if (twin >= 0)
                        {
                            var g = twin / 3;
                            rj = m_CornerRank[twin];
                            ri = m_CornerRank[3 * g + (twin % 3 == 2 ? 0 : twin % 3 + 1)];
                        }
                        else
                        {
                            ri = m_CornerRank[h];
                            rj = m_CornerRank[3 * f + (k == 2 ? 0 : k + 1)];
                        }
                        if (ri < 0 || rj < 0) continue;
                        index += rj - ri;
                    }
                    var mod = Mod4(index);
                    if (mod == 1 || mod == 3) singularities[f] = mod;
                }
                stats.singularFaces = singularities.Count;
                return;
            }

            for (var f = 0; f < faces; f++)
            {
                var index = 0;
                for (var k = 0; k < 3; k++)
                {
                    var i = F[3 * f + k];
                    var j = F[3 * f + (k == 2 ? 0 : k + 1)];
                    SingularityAnalysis.Compat4Index(Q[i], N[i], Q[j], N[j], out var k0, out var k1);
                    index += k1 - k0;
                }

                var mod = Mod4(index);
                if (mod != 1 && mod != 3) continue;

                if (index >= 4 || index < 0) Q[F[3 * f]] = -Q[F[3 * f]];
                singularities[f] = mod;
            }

            stats.singularFaces = singularities.Count;
        }

        /// <summary>
        /// One integer offset per undirected edge. Computed per face with all three crosses aligned
        /// jointly, then stated in the lower endpoint's raw frame; a singular face never overwrites
        /// what its non-singular neighbour said about a shared edge.
        /// </summary>
        void BuildEdgeInfo()
        {
            var faces = faceCount;
            var posRank = new int[faces * 3];
            var posIndex = new Vector2Int[faces * 3];
            var posResidual = new Vector2[faces * 3];

            var q = new Vector3[3];
            var n = new Vector3[3];
            var o = new Vector3[3];
            var v = new Vector3[3];
            var best = new int[3];

            for (var f = 0; f < faces; f++)
            {
                for (var k = 0; k < 3; k++)
                {
                    var i = F[3 * f + k];
                    q[k] = MeshMath.SafeNormalize(Q[i], OrientationField.ArbitraryTangent(N[i]));
                    n[k] = N[i];
                    o[k] = O[i];
                    v[k] = V[i];
                }

                // From a global parametrisation: each corner's cross turned onto the face chart's u
                // axis, and the offsets read straight off the chart's rounded nodes. They close round
                // the face exactly, and agree across every edge -- across a cut the charts differ by
                // a quarter-turn and an integer shift, which the ranks and the lattice absorb.
                if (m_CornerNodes != null)
                {
                    var axis = m_FaceUAxis[f];
                    for (var k = 0; k < 3; k++)
                    {
                        var target = axis - n[k] * Vector3.Dot(n[k], axis);
                        var rank = 0;
                        var bestAlign = float.NegativeInfinity;
                        for (var r = 0; r < 4; r++)
                        {
                            var d = Vector3.Dot(Rotate90By(q[k], n[k], r), target);
                            if (d > bestAlign) { bestAlign = d; rank = r; }
                        }
                        posRank[3 * f + k] = m_CornerRank != null && m_CornerRank[3 * f + k] >= 0
                            ? m_CornerRank[3 * f + k]
                            : rank;
                    }
                    m_FaceRank ??= new int[faces * 3];
                    for (var k = 0; k < 3; k++) m_FaceRank[3 * f + k] = posRank[3 * f + k];
                    for (var k = 0; k < 3; k++)
                    {
                        var kn = k == 2 ? 0 : k + 1;
                        posIndex[3 * f + k] = m_CornerNodes[3 * f + kn] - m_CornerNodes[3 * f + k];
                        posResidual[3 * f + k] = m_CornerUv != null
                            ? (m_CornerUv[3 * f + kn] - m_CornerUv[3 * f + k]) - (Vector2)posIndex[3 * f + k]
                            : Vector2.zero;
                    }
                    continue;
                }

                // The joint alignment of three crosses: the triple of representatives whose worst
                // pairwise agreement is best. Pairwise alignment would let the three answers
                // disagree around the face.
                var bestDot = float.NegativeInfinity;
                for (var i = 0; i < 4; i++)
                {
                    var v0 = Rotate90By(q[0], n[0], i);
                    for (var j = 0; j < 4; j++)
                    {
                        var v1 = Rotate90By(q[1], n[1], j);
                        for (var k = 0; k < 4; k++)
                        {
                            var v2 = Rotate90By(q[2], n[2], k);
                            var dp = Mathf.Min(Mathf.Min(Vector3.Dot(v0, v1), Vector3.Dot(v1, v2)),
                                Vector3.Dot(v2, v0));
                            if (dp <= bestDot) continue;
                            bestDot = dp;
                            best[0] = i;
                            best[1] = j;
                            best[2] = k;
                        }
                    }
                }

                for (var k = 0; k < 3; k++)
                {
                    posRank[3 * f + k] = best[k];
                    q[k] = Rotate90By(q[k], n[k], best[k]);
                }

                for (var k = 0; k < 3; k++)
                {
                    var kn = k == 2 ? 0 : k + 1;
                    var sk = ScaleAt(F[3 * f + k]);
                    var skn = ScaleAt(F[3 * f + kn]);

                    PositionField.CompatIndex(
                        v[k], n[k], q[k], o[k],
                        v[kn], n[kn], q[kn], o[kn],
                        sk, 1f / sk, skn, 1f / skn,
                        out var a, out var b, out _);

                    posIndex[3 * f + k] = a - b;

                    // How far the two lattice points the rounding paired actually are apart, in
                    // cells of the first vertex's frame: a - b plus this is the field's real offset.
                    var t0 = OrientationField.Rotate90(q[k], n[k]);
                    var t1 = OrientationField.Rotate90(q[kn], n[kn]);
                    var l0 = o[k] + (q[k] * a.x + t0 * a.y) * sk;
                    var l1 = o[kn] + (q[kn] * b.x + t1 * b.y) * skn;
                    var gap = l1 - l0;
                    var cellSize = 0.5f * (sk + skn);
                    posResidual[3 * f + k] = cellSize > 0f
                        ? new Vector2(Vector3.Dot(gap, q[k]), Vector3.Dot(gap, t0)) / cellSize
                        : Vector2.zero;
                }
            }

            feId.Clear();
            feOrient.Clear();
            for (var h = 0; h < faces * 3; h++)
            {
                feId.Add(-1);
                feOrient.Add(0);
            }

            edgeDiff.Clear();
            edgeX.Clear();
            edgeY.Clear();
            edgeContinuous.Clear();

            for (var i = 0; i < faces; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var k1 = j;
                    var k2 = (j + 1) % 3;
                    var v1 = F[3 * i + k1];
                    var v2 = F[3 * i + k2];

                    Vector2Int diff2;
                    Vector2 continuous2;
                    var real = posIndex[3 * i + k1] + posResidual[3 * i + k1];
                    if (v1 > v2)
                    {
                        var rank2 = posRank[3 * i + k2];
                        diff2 = Rshift90(-posIndex[3 * i + k1], rank2);
                        continuous2 = Rshift90(-real, rank2);
                    }
                    else
                    {
                        var rank2 = posRank[3 * i + k1];
                        diff2 = Rshift90(posIndex[3 * i + k1], rank2);
                        continuous2 = Rshift90(real, rank2);
                    }

                    var current = 3 * i + k1;
                    var twin = E2E[current];
                    var existing = feId[current];

                    if (existing == -1)
                    {
                        var id = edgeDiff.Count;
                        edgeX.Add(Mathf.Min(v1, v2));
                        edgeY.Add(Mathf.Max(v1, v2));
                        edgeDiff.Add(diff2);
                        edgeContinuous.Add(continuous2);
                        feId[current] = id;
                        if (twin != -1) feId[twin] = id;
                    }
                    else if (!singularities.ContainsKey(i))
                    {
                        edgeDiff[existing] = diff2;
                        edgeContinuous[existing] = continuous2;
                    }
                }
            }
        }

        /// <summary>
        /// Clamp every edge offset to a single lattice cell, keeping its sign.
        /// <para>
        /// The position field hands back however many cells an input edge happens to span, which on
        /// uniform input is already zero or one but on a coarse triangle fan -- a cylinder cap, a
        /// decimated scan -- can be many. Those wide offsets are what the flow then has to balance and
        /// what subdivision then has to split away, and splitting a wide edge re-derives wide offsets
        /// on the halves, so the pass never converges. The reference clamps first for exactly this
        /// reason: the map only ever needs to know which way an edge steps, not how far, because the
        /// distance is recovered by subdividing.
        /// </para>
        /// </summary>
        /// <summary>Off: measured 2026-09-18, it costs icosphere 8 -> 118 and cube 11 -> 244 irregular.</summary>
        public static bool clampEdgeDiffToUnit;

        /// <summary>Forces creases to be iso-lines of the map. See the remark in Build.</summary>
        public static bool applySharpConstraints = true;

        /// <summary>
        /// Lets curvature grading run on the integer path, with the input refined against the
        /// LOCAL spacing first (<see cref="InputRefine.RefineLocal"/>). Off, the map runs a uniform
        /// lattice as the reference does. See RemeshPipeline's grading section.
        /// </summary>
        /// <remarks>
        /// On since 2026-09-23. Measured through the editor's own path on the user's helmet head at
        /// 5000 quads (irregular / open edges / pieces / Hausdorff): uniform 485 / 84 / 4 / 0.0216,
        /// graded at <see cref="gradedAdaptivity"/> 626 / 72 / 1 / 0.0164 -- a quarter less surface
        /// error, one piece instead of four, and in the renders the chin strap appears and the brim
        /// stops crumpling. It costs irregular vertices, because the grading spends singularities
        /// on the size changes; for a detailed asset at a low count that is the right trade.
        /// </remarks>
        public static bool gradeSpacing = true;

        /// <summary>
        /// Curvature adaptivity the integer path uses wherever the chosen profile grades at all.
        /// </summary>
        /// <remarks>
        /// Half, which is also what QuadRemesher's own Blender bridge sends by default. The profiles'
        /// quarter was swept on the COLLAPSE extractor, where past a quarter neighbouring cells stop
        /// agreeing and the extraction tears; the integer map pairs cells through the flow instead
        /// and holds together further. Swept on the head: 0.25 gives Hausdorff 0.0217 in 5 pieces,
        /// 0.5 gives 0.0164 in 1, 0.75 gives 0.0212 in 2; widening the ratio clamp to 3 at 0.5 gives
        /// 0.0206. Creases at 60 degrees on top cost fidelity (0.0216) on this asset.
        /// </remarks>
        public static float gradedAdaptivity = 0.5f;

        /// <summary>
        /// Sweeps per level of the coarse-to-fine orientation solve the integer path turns on when
        /// the chosen profile has it off. Zero leaves the profile alone.
        /// </summary>
        /// <remarks>
        /// Every shipped profile turns the orientation hierarchy OFF, on a measurement taken with the
        /// collapse extractor: there a 12 % better field moved the output by nothing, because
        /// collapse amplifies each field singularity into about 2.5 irregular vertices whatever it
        /// is handed (see RemeshProfile.orientationHierarchyLevelIterations). The integer map does
        /// not amplify -- it turns a singularity into about 0.6 irregular vertices -- so here the
        /// field is what bounds regularity and the hierarchy pays. Measured on the helmet head,
        /// mean over 4600/5000/5400 quads: irregular 13.45 % -> 11.18 %, open edges 54.7 -> 31.3.
        /// </remarks>
        public static int orientationHierarchyLevelIterations = 6;

        /// <summary>
        /// One-ring normal-smoothing sweeps the orientation field is solved against on the integer
        /// path. Zero solves against the raw normals. See RemeshPipeline's call site.
        /// </summary>
        /// <remarks>
        /// ONE, and the dose matters more than the direction. Measured on the helmet head with the
        /// hierarchy on, mean over 4600/5000/5400 quads (irregular / open / pieces / Hausdorff):
        /// 0 sweeps 11.18 % / 31.3 / 2.0 / 0.0202; 1 sweep 8.78 % / 53.3 / 2.0 / 0.0181;
        /// 2 sweeps 9.00 % / 43.3 / 1.0 / 0.0244; 3 sweeps 9.00 % / 56.0 / 4.0 / 0.0282, and a
        /// cell's radius (12 sweeps) or two (49) makes the singularity count go UP, 1644 and 2848,
        /// because a field smooth against averaged normals is knotted once projected back onto
        /// the true ones wherever the surface turns sharply. What one sweep removes is noise at
        /// the scale of single input triangles: the decimated, midpoint-refined input's vertex
        /// normals disagree with their neighbours for reasons that are not shape, and the field
        /// was winding singularity pairs round those disagreements -- 1064 of them, against 614
        /// after one sweep.
        /// </remarks>
        public static int orientationNormalSweeps = 1;

        /// <summary>
        /// Detached pieces with fewer faces than this are dropped after extraction, provided the
        /// mesh has a larger piece. Zero keeps everything.
        /// </summary>
        /// <remarks>
        /// What the extraction leaves behind where the lattice folded is not a part of the model:
        /// measured on the helmet head, the "second piece" in the component count was a single quad
        /// at 5000 and a 3-face and a 2-face scrap at 5400, floating off the helmet shell. A real
        /// separate part of an asset -- an eye, a button -- is tens of quads at any usable count.
        /// </remarks>
        public static int debrisBelowFaces = 8;

        /// <summary>
        /// Faces whose longest edge exceeds this many median edges are dropped before hole closing
        /// and the gap re-capped; zero keeps them. See QuadExtractor.RemoveNeedles.
        /// </summary>
        public static float needleEdgeOverMedian = 5f;

        /// <summary>
        /// <see cref="needleEdgeOverMedian"/> for an input with an open boundary (a scan). Zero uses
        /// the closed-mesh value.
        /// </summary>
        /// <remarks>
        /// On ccnb the greedy flip repair leaves ~450 folded triangles, and where they cluster the
        /// extraction glues one cell across many: torn strips of quads 3-5 median edges long and a
        /// hole, on the embankment beside the parking lot, in about one run in three. At 3 those
        /// strips go and the gap is re-capped. Means over 4600/5000/5400 with crease-loop snap:
        /// long faces (edge over 3x median) 26/217/19 -> 10/17/7, p05 40.8 -> 45.2, Hausdorff
        /// 0.0156 -> 0.0174. On the closed head the same threshold left 34 open edges and on the body
        /// raised irregular 2.12 -> 2.35 %, so closed meshes keep 5.
        /// </remarks>
        public static float needleEdgeOverMedianOpen = 3f;

        /// <summary>
        /// Longest simple rim, in edges, the undirected fallback closes (with a ladder of quads past
        /// 8 edges). 8 or less keeps only the small-loop cap.
        /// </summary>
        public static int ladderCapMaxEdges = 64;

        /// <summary>
        /// Cap the holes extraction leaves (HoleFiller) and pair the triangles that makes back into
        /// quads (TrianglePairing), as the collapse path does. See QuadExtractor.ExtractIntegerGrid.
        /// </summary>
        public static bool closeHoles = true;

        /// <summary>
        /// QuadRemesher's "Adapt Quad Count": the target sizes the quads on flat areas, and curved
        /// areas get smaller quads ON TOP, so the result may carry more than the target. Off holds
        /// the count exactly, taking curved areas' extra quads from flat ones. See RemeshPipeline.
        /// </summary>
        public static bool adaptiveQuadCount = true;

        /// <summary>
        /// QuadRemesher's "Adaptive Size", 0..1: how strongly quad size follows curvature on the
        /// integer path. Negative leaves the profile's own value (raised to
        /// <see cref="gradedAdaptivity"/>). 0.5 is the same as that default; 0 is a uniform grid.
        /// </summary>
        public static float adaptiveSize = -1f;

        /// <summary>
        /// What <see cref="adaptiveSize"/> is multiplied by before it becomes the curvature
        /// adaptivity: the window's slider reads like QuadRemesher's (50 % by default) and lands at
        /// half that here.
        /// </summary>
        /// <remarks>
        /// The two scales are not the same quantity. At 0.5 our spacing spread on the body is 4.0,
        /// about QuadRemesher's own, but our lattice does not grade that steeply without shearing:
        /// measured through the window's path (target 5,000, Adapt Quad Count on, mean of
        /// 4,600/5,000/5,400), 0.25 against 0.5 takes the 5th-percentile quad corner from 58.5 to
        /// 60.4 degrees on the body, 45.9 to 49.3 on the head and 31.9 to 37.5 on ccnb, with
        /// irregular vertices 7.09 -> 6.47 % (head) and 9.49 -> 8.63 % (ccnb) and Hausdorff
        /// 0.0189 -> 0.0162 on ccnb. 1 restores the literal mapping.
        /// </remarks>
        public static float adaptiveSizeScale = 0.5f;

        /// <summary>
        /// Adapt Quad Count sizes its extra quads from the slider value itself rather than the
        /// softened adaptivity, so softening the grading does not also cut the face count.
        /// </summary>
        public static bool adaptiveQuadCountFromSlider = true;

        /// <summary>
        /// Solve the position field and extract in full on every face-count attempt and keep the
        /// first whose count is in tolerance, instead of probing cheaply and then solving again.
        /// See RemeshPipeline. Off is the probe-then-solve loop the collapse extractor uses.
        /// </summary>
        public static bool keepInToleranceSolve = true;

        /// <summary>
        /// For profiles with dihedral crease detection off (the scan profiles, which the auto
        /// profile picks for sculpts too): detect creases anyway, and keep only the chains that run
        /// straight for <see cref="coherentCreaseMinCells"/> cells. See RemeshPipeline.
        /// </summary>
        public static bool coherentCreases = true;

        /// <summary>
        /// Also cut the lattice along the coherent creases (sharp offsets, crease resampling,
        /// vertex placement), not only steer the cross field by them. See RemeshPipeline.
        /// </summary>
        public static bool coherentCreasesCut = false;

        /// <summary>
        /// With <see cref="coherentCreasesCut"/>, cut only creases at least this many cells end to
        /// end; shorter coherent creases still steer the field. At or below
        /// <see cref="coherentCreaseMinCells"/> every kept crease is cut.
        /// </summary>
        public static float coherentCreaseCutMinCells = 0f;

        /// <summary>Straightness a crease needs to be cut under <see cref="coherentCreaseCutMinCells"/>.</summary>
        public static float coherentCreaseCutStraightness = 0f;

        /// <summary>Dihedral angle, degrees, a coherent crease must reach.</summary>
        public static float coherentCreaseAngle = 35f;

        /// <summary>End-to-end length, in cells, a crease chain needs to be kept.</summary>
        /// <remarks>
        /// Three, up from 1.5. Every kept chain is an orientation constraint, and on a scan or a
        /// detailed sculpt the chains between 1.5 and 3 cells are mostly noise and sub-quad detail
        /// the field then twists round. Measured through the window's path (target 5,000, Adapt
        /// Quad Count on, mean of 4,600/5,000/5,400), 1.5 -> 3: irregular 2.90 -> 2.27 % (body),
        /// 5.07 -> 4.55 % (head), 7.08 -> 6.82 % (ccnb); Hausdorff 0.0094 -> 0.0091, 0.0144 ->
        /// 0.0126, 0.0157 -> 0.0157; 5th-percentile quad corner 59.3 -> 56.2, 51.3 -> 49.7,
        /// 36.8 -> 42.9 degrees; worst ccnb quad 5.5x the median edge -> 3.9x.
        /// </remarks>
        public static float coherentCreaseMinCells = 3f;

        /// <summary>End-to-end over walked length a crease chain needs to be kept.</summary>
        public static float coherentCreaseStraightness = 0.6f;

        /// <summary>
        /// When cutting along coherent creases, a crease running alongside a longer one closer than
        /// this many cells is not cut (it still steers the field). Zero cuts every one. See
        /// FeatureGraph.KeepSeparatedCurves.
        /// </summary>
        public static float coherentCreaseMinSeparationCells = 1f;

        /// <summary>Share of a crease's vertices that may lie near a longer parallel one before it is thinned.</summary>
        public static float coherentCreaseMaxShared = 0.3f;

        /// <summary>
        /// Half-length, in cells, of the polyline chord each feature vertex's orientation lock is
        /// taken from. Zero keeps the one-ring chord. See FeatureGraph.SmoothedTangents.
        /// </summary>
        /// <remarks>
        /// Half a cell. Measured through the window's path (target 5,000, Adapt Quad Count on, mean
        /// of 4,600/5,000/5,400), one-ring -> 0.5: ccnb's open edges 674 -> 510, its cut border
        /// now one clean edge loop where it was a staircase of slivers; 5th-percentile quad corner
        /// 56.2 -> 61.0 degrees on the body and 49.7 -> 51.9 on the head, irregular 2.27 -> 2.12 %
        /// and 4.55 -> 4.38 %. The head's Hausdorff moved 0.0126 -> 0.0153 the other way. A whole
        /// cell measured no better than half on ccnb.
        /// </remarks>
        public static float featureTangentRadiusCells = 0.5f;

        /// <summary>Most the target may be multiplied by under <see cref="adaptiveQuadCount"/>.</summary>
        public static float adaptiveQuadCountMaxGrowth = 3f;

        /// <summary>Area percentile of the graded spacing taken as "flat" for <see cref="adaptiveQuadCount"/>.</summary>
        public static float adaptiveFlatPercentile = 0.9f;

        /// <summary>
        /// Fold-back tolerance, in degrees, for a second triangle-pairing pass over what the first
        /// left. Zero skips it.
        /// </summary>
        public static float lenientPairingReflexDegrees = 45f;

        /// <summary>
        /// Strength of the principal-curvature guides on the integer path's cross field, in the
        /// solver's units (a multiple of vertex degree at full anisotropy). Zero is off. See
        /// <see cref="CurvatureDirections"/>.
        /// </summary>
        /// <remarks>
        /// OFF, on measurement. Means over 4600/5000/5400 quads through the editor's path
        /// (irregular / Hausdorff), against 8.78 % / 0.0185 on the helmet head and 2.85 % / 0.0101
        /// on the body with no guides, using the per-triangle (Rusinkiewicz) estimator:
        /// strength 0.1 over 1 cell: head 9.26 % / 0.0219, body 3.42 % / 0.0171;
        /// 0.3 over 1 cell: head 10.43 % / 0.0235, body 4.19 % / 0.0119;
        /// 0.3 over 2 cells: head 9.29 % / 0.0242, body 3.42 % / 0.0104.
        /// The renders agree: a few more ring-like loops on the legs, no clear visual win.
        /// (The first estimator, Taubin's, was biased 17.5 degrees on a regular triangulation and
        /// measured worse still -- see CurvatureDirections -- so these numbers are the fair ones.)
        ///
        /// Why it does not pay here: at the scale a quad can express, these sculpts bend nearly
        /// equally both ways or in directions that change within a cell, so the tensor either says
        /// nothing or says it noisily, and the smoothing field -- which is extrinsic, and so already
        /// follows the surface's big turns -- is the better guide. Kept as a switch for inputs with
        /// long clean anisotropic forms (pipes, limbs on a smooth base mesh), where it has not yet
        /// been measured.
        ///
        /// NOISE FLOOR: a run with the guides ON but zero of them active came out 3.05 % against
        /// the baseline's 2.85 % on the body. Nothing is nondeterministic -- identical settings
        /// reproduce bit for bit -- but a perturbation in the last float of the field moves the
        /// face-count probe to a different spacing (4930 against 4780 faces at 5000) and everything
        /// downstream with it. Differences under ~0.3 points between means are not a result.
        /// </remarks>
        public static float curvatureAlignment;

        /// <summary>Radius, in output cells, the curvature tensor is averaged over.</summary>
        public static float curvatureRadiusCells = 1f;

        /// <summary>
        /// Solve the cross field as one global linear problem (the smoothest field under the
        /// feature locks) instead of by local Gauss-Seidel descent. See GlobalCrossField.
        /// </summary>
        /// <remarks>
        /// OFF, on measurement (helmet head, 5000, window path). Hard locks: 716 -> 906 singular
        /// faces, irregular 4.29 -> 5.19 %. No creases, eigenvector field: loop turn 6.43 -> 5.10
        /// degrees but 1020 singular faces and the same irregular count; locks softened to 0.02-0.1
        /// or normals smoothed over 0.5-1 cell land within the run-to-run noise.
        /// </remarks>
        public static bool globalField = false;

        /// <summary>
        /// Place the lattice by a global seamless parametrisation (mixed-integer, MIQ) instead of
        /// the Instant Meshes position field. See SeamlessParametrization.
        /// </summary>
        /// <remarks>
        /// Off in code (tests and scripted runs keep the old lattice); the editor window turns it on
        /// with its Global Parametrization toggle. Means over 4600/5000/5400, window path:
        ///   body    p05 58.1 -> 62.5, irregular 2.12 -> 1.96 %, Hausdorff 0.0090 -> 0.0094,
        ///           orth 9.1 -> 7.9, loop turn 4.92 -> 4.48, 32.6 -> 28.8 s.
        ///   head    p05 49.3 -> 52.5, irregular 4.38 -> 4.05 %, Hausdorff 0.0156 -> 0.0110,
        ///           orth 11.8 -> 10.4, loop turn 7.19 -> 6.91, 50.8 -> 64.8 s.
        ///   ccnb    p05 45.2 -> 43.7, irregular 6.94 -> 6.65 %, Hausdorff 0.0174 -> 0.0167,
        ///           long faces 11.3 -> 14.0 per run, needle fans at the scan's jagged border --
        ///           hence <see cref="parametrizationOnOpenMeshes"/>.
        /// </remarks>
        public static bool parametrization = false;

        /// <summary>
        /// With <see cref="parametrization"/>, use it on meshes with an open border too. Off: a mesh
        /// with boundary edges keeps the position-field lattice, which handles a ragged scan border
        /// better (see the ccnb row above).
        /// </summary>
        public static bool parametrizationOnOpenMeshes = false;

        /// <summary>
        /// With <see cref="parametrization"/>, trace the quads straight off the parametrisation's
        /// integer grid on the decimated mesh (see ParametrizationTrace) instead of handing its
        /// lattice to this map's collapse.
        /// </summary>
        public static bool parametrizationTrace = false;

        /// <summary>
        /// With <see cref="parametrization"/>, mesh the quantised quad layout of the free, untangled
        /// map (see QuantizedLayout) instead of rounding the map's integers one by one.
        /// </summary>
        public static bool parametrizationLayout = false;

        /// <summary>
        /// With <see cref="parametrizationLayout"/>, creases closer than this many cells to a longer
        /// parallel one are not laid as patch borders. Head at 3.5k: 0 -> 1 cell took Hausdorff
        /// 0.0145 -> 0.0111, p05 50.5 -> 52.4, and the helmet rim's spikes went.
        /// </summary>
        public static float layoutCreaseSeparationCells = 1f;

        /// <summary>
        /// With <see cref="parametrization"/>, solve and extract on the decimated mesh rather than
        /// the refined one. See RemeshPipeline.ParametrizeDecimated.
        /// </summary>
        public static bool parametrizationOnDecimated = true;

        /// <summary>
        /// With <see cref="parametrizationOnDecimated"/>, carry the solved map back onto the refined
        /// mesh and extract there. See ParametrizationTransfer.
        /// </summary>
        public static bool parametrizationExtractRefined = true;

        /// <summary>
        /// With <see cref="parametrization"/>, solve the cross field again on the decimated mesh, as
        /// the smoothest field under the crease locks (see GlobalCrossField), instead of sampling the
        /// refined one.
        /// </summary>
        public static bool parametrizationGlobalField = false;

        /// <summary>Weight of the sampled refined field as a guide to that solve (0 = locks only).</summary>
        public static float parametrizationGlobalFieldGuide = 0f;

        /// <summary>Smoothing sweeps of the cross field after it is sampled onto the decimated mesh.</summary>
        public static int parametrizationFieldSweeps = 0;

        /// <summary>Make the border and the coherent creases grid lines of the parametrisation.</summary>
        public static bool parametrizationFeatures = true;

        /// <summary>Shortest coherent crease, in cells, made a grid line of the parametrisation.</summary>
        public static float parametrizationCreaseMinCells = 3f;

        /// <summary>Creases closer than this many cells to a longer parallel one are not made grid lines.</summary>
        public static float parametrizationCreaseSeparationCells = 0f;

        /// <summary>
        /// With <see cref="parametrization"/>, read the edge offsets straight off the
        /// parametrisation's rounded nodes rather than re-rounding each edge from the lattice
        /// points it produced.
        /// </summary>
        public static bool parametrizationOffsets = true;

        /// <summary>
        /// Cancel pairs of opposite cross-field singularities closer than
        /// <see cref="cancelPairCells"/> by re-solving the field in a disk round each. See
        /// GlobalCrossField.CancelPairs.
        /// </summary>
        /// <remarks>
        /// OFF: of 170 pairs re-solved on the head only ~34 singular faces went (580 -> 546) -- the
        /// pairs sit where the surface's own bumps and strap crossings make the smoothest field want
        /// them. Irregular 4.29 -> 4.06-4.19 % on single runs, not yet shown beyond noise.
        /// </remarks>
        public static bool cancelSingularPairs = false;

        /// <summary>Largest distance, in cells, between the two singularities of a cancelled pair.</summary>
        public static float cancelPairCells = 2f;

        /// <summary>Margin, in cells, of the re-solved disk beyond the pair.</summary>
        public static float cancelPairRimCells = 1f;

        /// <summary>Cancellation rounds; each pairs what the last one left.</summary>
        public static int cancelPairRounds = 3;

        /// <summary>
        /// Local sweeps run on the field after it is projected from the smoothed normals back onto
        /// the true ones. Zero skips them.
        /// </summary>
        /// <remarks>
        /// OFF: the projection adds ~140 singular faces on the head (580 -> 716), but sweeping on the
        /// true normals makes it worse -- 5 sweeps: 834 singular, irregular 5.42 %, Hausdorff 0.0198.
        /// </remarks>
        public static int trueNormalPolishSweeps = 0;

        /// <summary>
        /// After relaxation, slide the edge loop running alongside each coherent crease onto it, so
        /// hard lines shade as lines instead of a staircase across a quad row. See CreaseLoopSnap.
        /// </summary>
        /// <remarks>
        /// ON, measured through the window's path (target 4600/5000/5400, Adapt Quad Count, Adaptive
        /// Size 50, Detect Hard Edges), means. The topology is untouched -- irregular count and face
        /// count identical -- so the whole effect is where the vertices sit. Mean distance from the
        /// input surface to the output, in cells (lower keeps more detail): body 0.098 -> 0.095,
        /// helmet head 0.152 -> 0.136, ccnb 0.137 -> 0.119. Hausdorff unchanged (0.0089 -> 0.0090,
        /// 0.0153 -> 0.0156, 0.0156 -> 0.0156). The cost is the quads beside a crease, which take the
        /// squeeze: 5th-percentile corner angle 61.0 -> 58.1, 51.9 -> 49.3, 41.5 -> 40.8. In the
        /// renders the helmet's plate edges, the balaclava folds and the cheek straps' borders read
        /// as lines where they were a zig-zag of light and dark quads.
        /// Cutting the lattice along the long creases instead (coherentCreaseCutMinCells 6,
        /// coherentCreaseCutStraightness 0.9) is crisper still on the helmet but damages the body's
        /// cloth folds: p05 61.0 -> 52.2, Hausdorff 0.0089 -> 0.0127.
        /// </remarks>
        public static bool creaseLoopSnap = true;

        /// <summary>Furthest, in cells, a loop vertex is moved onto a crease.</summary>
        public static float creaseLoopSnapCells = 0.5f;

        /// <summary>Snap rounds, each followed by a relaxation with the snapped vertices held.</summary>
        public static int creaseLoopSnapRounds = 3;

        /// <summary>Relaxation sweeps after each snap round.</summary>
        public static int creaseLoopRelaxIterations = 8;

        /// <summary>
        /// Drift cap, in cells, of each relaxation between snap rounds. 0.15 measured Hausdorff
        /// 0.0094 -> 0.0169 on the head (each round's cap stacks on the main pass's); 0.05 keeps it.
        /// </summary>
        public static float creaseLoopRelaxMaxDrift = 0.05f;

        /// <summary>
        /// Largest offset along the vertex normal, in cells, a crease snap may make. 0.3 refused the
        /// snaps onto the top of a raised step -- the useful ones -- and lost most of the gain.
        /// </summary>
        public static float creaseLoopMaxNormalCells = 0.6f;

        /// <summary>Local sweeps run on top of the global field; zero uses it as solved.</summary>
        public static int globalFieldPolishSweeps = 0;

        /// <summary>Conjugate-gradient iteration cap of the global field solve.</summary>
        public static int globalFieldMaxIterations = 2000;

        /// <summary>
        /// When positive, the global field holds every hard-locked feature vertex only with this
        /// data weight (a multiple of its degree) instead of exactly. Zero keeps hard locks.
        /// </summary>
        public static float globalFieldSoftenLocks = 0f;

        /// <summary>Relative residual the global field solve stops at.</summary>
        public static float globalFieldTolerance = 1e-5f;

        /// <summary>
        /// Trace a ridge/valley curve network and cut the lattice along it: every curve becomes a
        /// crease -- a hard direction for the cross field, an iso-line of the map, a line the output
        /// vertices are pinned to. See <see cref="CurveNetwork"/>.
        /// </summary>
        /// <remarks>
        /// OFF by default -- a per-model choice, decided by the renders rather than the numbers.
        /// With the thin-feature filter (CurveNetwork.thinCells = 0.75), means over
        /// 4600/5000/5400 through the editor's path, holes closed (closeHoles), irregular / open /
        /// Hausdorff / chamfer / RIDGE -- the mean distance from the input's ridge lines to the
        /// output, EditorParity's "ridge", always scored against the same default-traced set:
        ///
        ///   body  off  2.85 % / 0.0 / 0.01013 / 0.00067 / 0.00265
        ///         on   3.20 % / 0.0 / 0.01006 / 0.00063 / 0.00167
        ///   head  off  9.35 % / 0.0 / 0.02037 / 0.00133 / 0.00716
        ///         on   9.16 % / 1.3 / 0.01950 / 0.00127 / 0.00465
        ///
        /// Edges a third closer on both, surface error level or better, regularity within about
        /// the noise floor. The 1.3 open edges are one three-edge loop through a non-manifold edge
        /// at 5400, which HoleFiller rightly refuses to cap.
        ///
        /// The numbers favour it on both and the body's render agrees -- the vest plate comes out a
        /// crisp box with a loop along each edge. The head's render does not: the helmet brim is a
        /// lip thinner than a cell, and pinning cells to its crest zig-zags them across it, so the
        /// brim reads jagged where the uncut field lays loops smoothly along it. The ridge metric
        /// rewards vertices ON the crest and cannot see how they are connected. So it stays a
        /// switch: on for gear with plates and panels, off for thin-shelled hardware.
        ///
        /// Without the thin filter it was a per-model choice: body 3.42 % / 2.0 / 0.0110, head
        /// 9.20 % / 88.7 / 0.0219 -- helmet hardware put ridges and valleys within a cell of each
        /// other, the lattice had room for one iso-line where it was asked for two, and the brim
        /// crumpled and a 59-edge hole opened on the side rail. Also tried on the way (same means,
        /// before the filter): hard direction constraints instead of guides (head 10.05-10.48 %,
        /// body 3.64-4.80 %); lower start thresholds (150+ curves, body Hausdorff 0.0182); aligning
        /// the field only, without the cut (ridges no closer at all: body 0.00280, head 0.00852 --
        /// the cut is what holds an edge); snapping to the nearest curve in a cell with a one-cell
        /// band (body Hausdorff 0.0144, head 0.0250); thin filter at 0.5 cells (head Hausdorff
        /// 0.0234) and 1.0 (removes nearly every curve, back to baseline).
        /// </remarks>
        public static bool curveNetwork;

        /// <summary>
        /// True cuts along the curves (creases: direction, iso-line and pinned positions). False
        /// only aligns the cross field to them and leaves the map and the positions free.
        /// </summary>
        public static bool curveNetworkCut = true;

        public void ClampEdgeDiffToUnit()
        {
            for (var e = 0; e < edgeCount; e++)
            {
                var d = edgeDiff[e];
                if (Mathf.Abs(d.x) <= 1 && Mathf.Abs(d.y) <= 1) continue;
                if (Mathf.Abs(d.x) > 1) d.x = d.x > 0 ? 1 : -1;
                if (Mathf.Abs(d.y) > 1) d.y = d.y > 0 ? 1 : -1;
                edgeDiff[e] = d;
                stats.edgeDiffClamped++;
            }
        }

        /// <summary>
        /// A crease must be an iso-line of the map: an edge along it may step in one lattice
        /// direction only, and that zero component is then locked against the flow.
        /// </summary>
        void ApplySharpEdgeConstraints()
        {
            for (var h = 0; h < sharp.Count; h++)
            {
                if (!sharp[h]) continue;
                var e = feId[h];
                var d = edgeDiff[e];
                if (d.x * d.y == 0) continue;

                var dv = O[edgeY[e]] - O[edgeX[e]];
                var q = Q[edgeX[e]];
                var n = N[edgeX[e]];
                var qy = Vector3.Cross(n, q);
                if (Mathf.Abs(Vector3.Dot(q, dv)) > Mathf.Abs(Vector3.Dot(qy, dv))) d.y = 0;
                else d.x = 0;
                edgeDiff[e] = d;
            }

            RebuildAllowChanges();
        }

        public void RebuildAllowChanges()
        {
            allow.Clear();
            for (var e = 0; e < edgeCount; e++)
            {
                allow.Add(1);
                allow.Add(1);
            }

            for (var h = 0; h < sharp.Count; h++)
            {
                if (!sharp[h]) continue;
                var e = feId[h];
                for (var k = 0; k < 2; k++)
                    if (edgeDiff[e][k] == 0) allow[2 * e + k] = 0;
            }
        }

        /// <summary>The three offsets of a face, rotated into its frame and summed. Zero is closed.</summary>
        public Vector2Int FaceResidual(int f)
        {
            var d = Vector2Int.zero;
            for (var j = 0; j < 3; j++)
                d += Rshift90(edgeDiff[feId[3 * f + j]], feOrient[3 * f + j]);
            return d;
        }

        public int CountUnsatisfied()
        {
            var count = 0;
            for (var f = 0; f < faceCount; f++)
                if (FaceResidual(f) != Vector2Int.zero) count++;
            return count;
        }

        // ------------------------------------------------------------------
        // Integer constraints
        // ------------------------------------------------------------------

        /// <summary>
        /// Assigns every face its edge rotations, gauges all faces of a component into one frame,
        /// and balances each component so the flow can route everything.
        /// </summary>
        public void BuildIntegerConstraints()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var faces = faceCount;
            var edges = edgeCount;
            var rng = new System.Random(m_Seed);

            // Undirected edge -> its one or two half-edges.
            var e2dFirst = new int[edges];
            var e2dSecond = new int[edges];
            for (var e = 0; e < edges; e++)
            {
                e2dFirst[e] = -1;
                e2dSecond[e] = -1;
            }

            for (var i = 0; i < faces; i++)
            {
                var v0 = F[3 * i];
                var v1 = F[3 * i + 1];
                var v2 = F[3 * i + 2];

                SingularityAnalysis.Compat4Index(Q[v0], N[v0], Q[v1], N[v1], out var a1, out var b1);
                SingularityAnalysis.Compat4Index(Q[v0], N[v0], Q[v2], N[v2], out var a2, out var b2);
                var rank1 = Mod4(a1 - b1);   // v1 -> v0
                var rank2 = Mod4(a2 - b2);   // v2 -> v0
                if (m_FaceRank != null && 3 * i + 2 < m_FaceRank.Length && !singularities.ContainsKey(i))
                {
                    rank1 = Mod4(m_FaceRank[3 * i] - m_FaceRank[3 * i + 1]);
                    rank2 = Mod4(m_FaceRank[3 * i] - m_FaceRank[3 * i + 2]);
                }

                // The face frame is v0's raw frame. Each slot's rotation carries the edge's stored
                // offset (from its lower endpoint, in that endpoint's frame) into v0's frame, in the
                // direction the face walks it — the +2 is the reversal.
                feOrient[3 * i] = v1 < v0 ? Mod4(rank1 + 2) : 0;
                feOrient[3 * i + 1] = v2 < v1 ? Mod4(rank2 + 2) : rank1;
                feOrient[3 * i + 2] = v2 < v0 ? rank2 : 2;

                for (var j = 0; j < 3; j++)
                {
                    var e = feId[3 * i + j];
                    if (e2dFirst[e] == -1) e2dFirst[e] = 3 * i + j;
                    else e2dSecond[e] = 3 * i + j;
                }
            }

            var sharpUE = new bool[edges];
            for (var h = 0; h < sharp.Count; h++)
                if (sharp[h]) sharpUE[feId[h]] = true;

            // One frame per component. Non-singular, non-sharp edges first so the spanning tree
            // runs through the regular part of the surface; singular faces and creases join last
            // and take whatever frame the tree already gave their neighbours.
            var tree = new DisjointOrientTree(faces);

            void MergeAcross(int e)
            {
                var a = e2dFirst[e];
                var b = e2dSecond[e];
                if (a == -1 || b == -1) return;
                var orient1 = feOrient[a];
                var orient0 = Mod4(feOrient[b] + 2);
                tree.Merge(a / 3, b / 3, orient0, orient1);
            }

            for (var e = 0; e < edges; e++)
            {
                if (e2dFirst[e] == -1 || e2dSecond[e] == -1) continue;
                var f0 = e2dFirst[e] / 3;
                var f1 = e2dSecond[e] / 3;
                if (singularities.ContainsKey(f0) || singularities.ContainsKey(f1) || sharpUE[e]) continue;
                MergeAcross(e);
            }

            foreach (var f in singularities.Keys)
            {
                for (var j = 0; j < 3; j++)
                {
                    var e = feId[3 * f + j];
                    if (sharpUE[e]) continue;
                    MergeAcross(e);
                }
            }

            for (var e = 0; e < edges; e++)
                if (sharpUE[e]) MergeAcross(e);

            for (var i = 0; i < faces; i++)
            {
                var orient = tree.Orient(i);
                for (var j = 0; j < 3; j++)
                    feOrient[3 * i + j] = Mod4(feOrient[3 * i + j] + orient);
            }

            stats.unsatisfiedBefore = CountUnsatisfied();

            // --- pass 1: components joined by consistently oriented, non-sharp edges ---
            var colour = new int[faces];
            for (var i = 0; i < faces; i++) colour[i] = -1;
            var components = 0;
            var queue = new Queue<int>();

            for (var i = 0; i < faces; i++)
            {
                if (colour[i] != -1) continue;
                colour[i] = components;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    var v = queue.Dequeue();
                    for (var j = 0; j < 3; j++)
                    {
                        var e = feId[3 * v + j];
                        var d1 = e2dFirst[e];
                        var d2 = e2dSecond[e];
                        if (d1 == -1 || d2 == -1) continue;
                        if (Mod4(feOrient[d1] - feOrient[d2]) != 2 || sharpUE[e]) continue;

                        var fa = d1 / 3;
                        var fb = d2 / 3;
                        if (colour[fa] == -1) { colour[fa] = components; queue.Enqueue(fa); }
                        if (colour[fb] == -1) { colour[fb] = components; queue.Enqueue(fb); }
                    }
                }

                components++;
            }

            var totalFlows = new int[components];
            for (var i = 0; i < faces; i++)
            {
                var diff = FaceResidual(i);
                totalFlows[colour[i]] += diff.x + diff.y;
            }

            BuildVariables(out var varEq0, out var varEq1, out var varSign);
            stats.valveChangesPass1 = ApplyValveModifications(totalFlows,
                v => colour[varEq0[v] / 2], varEq0, varEq1, varSign, rng);

            // --- pass 2: components of the flow network itself ---
            BuildEdgeToConstraints(out var c0, out var s0, out var c1, out var s1);
            var nodeTree = new DisjointTree(faces * 2);
            for (var v = 0; v < edges * 2; v++)
            {
                if (allow[v] == 0) continue;
                if (c0[v] == -1 || c1[v] == -1) continue;
                if (s0[v] == -s1[v]) nodeTree.Merge(c0[v], c1[v]);
            }
            nodeTree.BuildCompactParent();

            var nodeFlows = new int[nodeTree.compactCount];
            for (var i = 0; i < faces; i++)
            {
                var diff = FaceResidual(i);
                nodeFlows[nodeTree.Index(2 * i)] += diff.x;
                nodeFlows[nodeTree.Index(2 * i + 1)] += diff.y;
            }

            stats.valveChangesPass2 = ApplyValveModifications(nodeFlows,
                v => nodeTree.Index(varEq0[v]), varEq0, varEq1, varSign, rng);

            stats.constraintsMs = watch.ElapsedMilliseconds;
        }

        /// <summary>
        /// For every variable (edge, component): the one or two equations it appears in and the sum
        /// of its signs there. Zero means it is conserved between two faces; anything else makes it
        /// a valve that can move a component's total.
        /// </summary>
        void BuildVariables(out int[] eq0, out int[] eq1, out int[] signSum)
        {
            var count = edgeCount * 2;
            eq0 = new int[count];
            eq1 = new int[count];
            signSum = new int[count];
            for (var v = 0; v < count; v++)
            {
                eq0[v] = -1;
                eq1[v] = -1;
            }

            var faces = faceCount;
            for (var i = 0; i < faces; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var orient = feOrient[3 * i + j];
                    var sign = Rshift90(new Vector2Int(1, 1), orient);
                    var e = feId[3 * i + j];
                    var index = Rshift90(new Vector2Int(2 * e, 2 * e + 1), orient);
                    for (var k = 0; k < 2; k++)
                    {
                        var v = Mathf.Abs(index[k]);
                        if (eq0[v] == -1) eq0[v] = 2 * i + k;
                        else eq1[v] = 2 * i + k;
                        signSum[v] += sign[k];
                    }
                }
            }
        }

        /// <summary>
        /// Like <see cref="BuildVariables"/> but with the sign kept per appearance, using the
        /// reference's 1-based trick so a variable with index zero keeps its sign.
        /// </summary>
        void BuildEdgeToConstraints(out int[] c0, out int[] s0, out int[] c1, out int[] s1)
        {
            var count = edgeCount * 2;
            c0 = new int[count];
            c1 = new int[count];
            s0 = new int[count];
            s1 = new int[count];
            for (var v = 0; v < count; v++)
            {
                c0[v] = -1;
                c1[v] = -1;
            }

            var faces = faceCount;
            for (var i = 0; i < faces; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var e = feId[3 * i + j];
                    var index = Rshift90(new Vector2Int(2 * e + 1, 2 * e + 2), feOrient[3 * i + j]);
                    for (var k = 0; k < 2; k++)
                    {
                        var l = Mathf.Abs(index[k]);
                        var s = index[k] / l;
                        var ind = l - 1;
                        var eq = 2 * i + k;
                        if (c0[ind] == -1)
                        {
                            c0[ind] = eq;
                            s0[ind] = s;
                        }
                        else
                        {
                            c1[ind] = eq;
                            s1[ind] = s;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Moves each component's total to zero by nudging valve variables — boundary offsets and
        /// same-sign (holonomy) offsets — chosen uniformly at random among the eligible ones.
        /// Supply must equal demand within a component or the flow cannot route it all.
        /// </summary>
        int ApplyValveModifications(int[] totalFlows, Func<int, int> componentOf,
            int[] eq0, int[] eq1, int[] signSum, System.Random rng)
        {
            var componentCount = totalFlows.Length;
            var lists = new List<(int variable, int delta)>[2][];
            lists[0] = new List<(int, int)>[componentCount];
            lists[1] = new List<(int, int)>[componentCount];

            var variables = edgeCount * 2;
            for (var v = 0; v < variables; v++)
            {
                if (eq0[v] == -1) continue;
                if (!((eq1[v] == -1 || signSum[v] != 0) && allow[v] == 1)) continue;

                var component = componentOf(v);
                var step = Mathf.Abs(signSum[v]) % 2;
                var current = edgeDiff[v / 2][v % 2];
                var total = totalFlows[component];

                var list = lists[step][component] ??= new List<(int, int)>();
                if (total > 0)
                {
                    if (signSum[v] > 0 && current > -1) list.Add((v, -1));
                    if (signSum[v] < 0 && current < 1) list.Add((v, 1));
                }
                else if (total < 0)
                {
                    if (signSum[v] < 0 && current > -1) list.Add((v, -1));
                    if (signSum[v] > 0 && current < 1) list.Add((v, 1));
                }
            }

            for (var step = 0; step < 2; step++)
            {
                foreach (var list in lists[step])
                {
                    if (list == null) continue;
                    for (var i = list.Count - 1; i > 0; i--)
                    {
                        var j = rng.Next(i + 1);
                        (list[i], list[j]) = (list[j], list[i]);
                    }
                }
            }

            var changes = 0;
            for (var component = 0; component < componentCount; component++)
            {
                for (var step = 0; step < 2; step++)
                {
                    if (totalFlows[component] == 0) continue;
                    var list = lists[step][component];
                    if (list == null) continue;

                    var total = totalFlows[component];
                    var maxNum = step == 0
                        ? Mathf.Min(Mathf.Abs(total) / 2, list.Count)
                        : Mathf.Min(Mathf.Abs(total), list.Count);
                    var dir = total > 0 ? -1 : 1;

                    for (var i = 0; i < maxNum; i++)
                    {
                        var (variable, delta) = list[i];
                        var d = edgeDiff[variable / 2];
                        edgeDiff[variable / 2] = WithComponent(d, variable % 2, d[variable % 2] + delta);
                        totalFlows[component] += step == 0 ? 2 * dir : dir;
                        changes++;
                    }
                }
            }

            return changes;
        }

        // ------------------------------------------------------------------
        // The flow
        // ------------------------------------------------------------------

        /// <summary>
        /// OFF: measured on the head at 4,600 with Adapt Quad Count, routing on ANY contracted
        /// level -- the top, or level 1 at 298k of 475k faces -- left 15-36 needle quads (>4x the
        /// median edge, Hausdorff 0.04-0.06) against 2 (0.019) for the fine solve; RouteOnce on
        /// level 0 reproduces the fine solve exactly, so the coarse SOLUTION is the problem, not the
        /// code. The body showed none, which is why it looked free at first. Kept for comparison.
        /// Solve the flow on the coarsest level of the edge hierarchy first -- the triangle graph
        /// with every zero-offset edge contracted, which never contracts a face whose offsets do
        /// not close, so it carries exactly the same supplies -- then push the offsets down and let
        /// the fine solve route only what is left. The fine graph of a refined input is about
        /// 31 faces per output quad; on the body at 5,000 (626k flow nodes) the fine solve alone
        /// was 51.7 s, of which Bellman-Ford seeding was 28 s and one Dijkstra per routed unit the
        /// rest. Off solves on the fine graph only, as QuadriFlow does (its DownsampleEdgeGraph is
        /// called with one level), with LEMON's network simplex where this has a Dijkstra loop.
        /// </summary>
        public static bool coarseFlow = false;

        /// <summary>
        /// Route the flow as a transshipment, one excess face at a time to its nearest deficit
        /// (MinCostFlow.SolveExcess), instead of through a super-source. Same optimal cost, and each
        /// search stays local: on the head the fine solve went from 52 s. Off is the super-source
        /// solve.
        /// </summary>
        public static bool excessFlow = true;

        /// <summary>
        /// Price each unit the flow moves by how far it takes the offset from the position field's
        /// real value, instead of 1 everywhere. Off is the reference's unit cost.
        /// </summary>
        /// <remarks>
        /// With unit cost every route of the same length is equally good, so the flow is free to
        /// carry its correction through edges the field rounded with complete confidence, and a long
        /// run of those changed by one cell is a band of sheared quads between two irregular
        /// vertices -- the defect on the body's crotch fold and the helmet's ear cup. The cost here
        /// is the quadratic rounding energy, (c +- 1 - x)^2 - (c - x)^2 = 1 -+ 2(x - c), shifted to
        /// stay positive: an offset the field left near half a cell flips for almost nothing, one it
        /// left on an integer costs the most, so corrections settle where the rounding was a coin
        /// toss anyway.
        /// </remarks>
        public static bool weightedFlow = false;

        /// <summary>Integer resolution of the weighted flow's cost; a unit change costs 1 to 2x this + 1.</summary>
        public static int weightedFlowScale = 8;

        /// <summary>Cost of moving offset variable <paramref name="v"/> by <paramref name="step"/> (+1 or -1).</summary>
        int FlowCost(int v, int step)
        {
            if (!weightedFlow || v / 2 >= edgeContinuous.Count) return 1;
            var x = edgeContinuous[v / 2][v % 2];
            var c = edgeDiff[v / 2][v % 2];
            var change = (c + step - x) * (c + step - x) - (c - x) * (c - x);
            return 1 + Mathf.Max(0, Mathf.RoundToInt(weightedFlowScale * change));
        }

        /// <summary>
        /// Route the top level's leftovers on every intermediate level too, as QuadriFlow does.
        /// Measured slower and no better: the levels just below the top are nearly as large as the
        /// fine graph, and the last few units still reach it -- body flow 1.4 -> 3.0 s, irregular
        /// 1.89 -> 1.97 %; ccnb 11.8 -> 9.9 s. Off routes on the top level only.
        /// </summary>
        public static bool coarseFlowAllLevels = false;

        /// <summary>
        /// Hierarchy level the coarse flow routes on; negative is the top. Levels above it are not
        /// used. See <see cref="coarseFlow"/>.
        /// </summary>
        public static int coarseFlowLevel = -1;

        /// <summary>Capacity widenings the coarse flow may take before leaving the rest to the fine one.</summary>
        public static int coarseFlowWidenings = 3;

        /// <summary>
        /// One min-cost flow over an arbitrary level's arrays, written back into
        /// <paramref name="diff"/>. The same network SolveFlow builds, on lists instead of fields.
        /// </summary>
        static void RouteOnce(List<int> fq, List<int> f2e, List<Vector2Int> diff, List<byte> allowList,
            int capacity, out int supply, out int routed, out long cost, out int phases, out long settled)
        {
            var faces = f2e.Count / 3;
            var edges = diff.Count;
            var nodes = faces * 2;
            var count = edges * 2;
            var c0 = new int[count];
            var c1 = new int[count];
            var s0 = new int[count];
            var s1 = new int[count];
            for (var v = 0; v < count; v++) { c0[v] = -1; c1[v] = -1; }
            for (var i = 0; i < faces; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var e = f2e[3 * i + j];
                    var index = Rshift90(new Vector2Int(2 * e + 1, 2 * e + 2), fq[3 * i + j]);
                    for (var k = 0; k < 2; k++)
                    {
                        var l = Mathf.Abs(index[k]);
                        var sign = index[k] / l;
                        var ind = l - 1;
                        if (c0[ind] == -1) { c0[ind] = 2 * i + k; s0[ind] = sign; }
                        else { c1[ind] = 2 * i + k; s1[ind] = sign; }
                    }
                }
            }

            var initial = new int[nodes];
            for (var v = 0; v < count; v++)
            {
                var value = diff[v / 2][v % 2];
                if (c0[v] != -1) initial[c0[v]] += s0[v] * value;
                if (c1[v] != -1) initial[c1[v]] += s1[v] * value;
            }

            supply = 0;
            for (var eq = 0; eq < nodes; eq++)
                if (initial[eq] > 0) supply += initial[eq];
            routed = 0; cost = 0; phases = 0; settled = 0;
            // Nothing to route: a level below one that routed everything costs only this scan.
            if (supply == 0) return;

            var source = nodes;
            var sink = nodes + 1;
            var flow = new MinCostFlow(nodes + 2, edges * 4 + nodes);
            var arcVariable = new List<int>(count);
            var arcForward = new List<int>(count);
            var arcBackward = new List<int>(count);
            for (var v = 0; v < count; v++)
            {
                if (allowList[v] == 0) continue;
                if (c0[v] == -1 || c1[v] == -1) continue;
                if (s0[v] != -s1[v]) continue;
                var v1 = c0[v];
                var v2 = c1[v];
                if (s0[v] < 0) (v1, v2) = (v2, v1);
                var c = diff[v / 2][v % 2];
                arcVariable.Add(v);
                arcForward.Add(flow.AddArc(v1, v2, Mathf.Max(0, c + capacity), 1));
                arcBackward.Add(flow.AddArc(v2, v1, Mathf.Max(0, -c + capacity), 1));
            }

            if (excessFlow)
            {
                var excess = new int[nodes + 2];
                Array.Copy(initial, excess, nodes);
                routed = flow.SolveExcess(excess);
            }
            else
            {
                for (var eq = 0; eq < nodes; eq++)
                {
                    if (initial[eq] > 0) flow.AddArc(source, eq, initial[eq], 0);
                    else if (initial[eq] < 0) flow.AddArc(eq, sink, -initial[eq], 0);
                }

                routed = flow.Solve(source, sink);
            }
            for (var i = 0; i < arcVariable.Count; i++)
            {
                var v = arcVariable[i];
                var d = diff[v / 2];
                var delta = -flow.FlowOn(arcForward[i]) + flow.FlowOn(arcBackward[i]);
                if (delta != 0) diff[v / 2] = WithComponent(d, v % 2, d[v % 2] + delta);
            }
            cost = flow.totalCost;
            phases = flow.phases;
            settled = flow.settled;
        }

        /// <summary>
        /// Routes every face's residual to a face of opposite sign across the triangle dual, as
        /// cheaply as possible, and writes the route back into the offsets. Widens the per-edge
        /// capacity and retries while any unit fails to route.
        /// </summary>
        public void SolveFlow(int maxIterations = 10)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // Coarse first: route on the contracted graph, push the result down, and leave the
            // fine solve below only what the coarse one could not express.
            if (coarseFlow)
            {
                var h = EdgeHierarchy.Build(feOrient, feId, edgeDiff, allow, -1);
                stats.flowCoarseLevels = h.levels;
                if (h.levels > 1 || coarseFlowLevel == 0)
                {
                    // Top level first, then (coarseFlowAllLevels) down through every level to 1,
                    // each routing only what the one above could not express -- QuadriFlow's own
                    // optimizer loops over its levels that way. Each level is widened like the fine solve
                    // below while anything fails to route: one coarse edge stands for a run of
                    // fine ones, and on ccnb the top level alone left 150 units that cost the fine
                    // graph 12 s. Each round reads the offsets back, so it routes only the rest.
                    var first = coarseFlowLevel >= 0 ? Math.Min(coarseFlowLevel, h.top) : h.top;
                    var lowest = coarseFlowAllLevels ? 1 : first;
                    stats.flowCoarseLevel = first;
                    for (var level = first; level >= lowest; level--)
                    {
                        for (var widened = 2; widened <= 2 + coarseFlowWidenings; widened++)
                        {
                            RouteOnce(h.FQ[level], h.F2E[level], h.EdgeDiff[level], h.Allow[level],
                                widened, out var supplyLeft, out var routedNow, out _,
                                out var coarsePhases, out var coarseSettled);
                            if (level == first && widened == 2) stats.flowCoarseSupply = supplyLeft;
                            stats.flowCoarseRouted += routedNow;
                            stats.flowPhases += coarsePhases;
                            stats.flowSettled += coarseSettled;
                            if (routedNow == supplyLeft) break;
                        }
                        if (level == first) stats.flowCoarseFaces = h.F2E[level].Count / 3;
                        if (level > lowest) h.PropagateLevel(level);
                    }
                    for (var level = lowest; level >= 1; level--) h.PropagateLevel(level);
                    h.UpdateGraphValue(feOrient, feId, edgeDiff);
                }
                stats.flowCoarseMs = watch.ElapsedMilliseconds;
            }

            var faces = faceCount;
            var edges = edgeCount;
            var nodes = faces * 2;
            var source = nodes;
            var sink = nodes + 1;

            var capacity = 2;
            var iterations = 0;
            var arcVariable = new List<int>(edges * 2);
            var arcForward = new List<int>(edges * 2);
            var arcBackward = new List<int>(edges * 2);

            while (true)
            {
                BuildEdgeToConstraints(out var c0, out var s0, out var c1, out var s1);

                var initial = new int[nodes];
                for (var v = 0; v < edges * 2; v++)
                {
                    var value = edgeDiff[v / 2][v % 2];
                    if (c0[v] != -1) initial[c0[v]] += s0[v] * value;
                    if (c1[v] != -1) initial[c1[v]] += s1[v] * value;
                }

                var flow = new MinCostFlow(nodes + 2, edges * 4 + nodes);
                arcVariable.Clear();
                arcForward.Clear();
                arcBackward.Clear();

                for (var v = 0; v < edges * 2; v++)
                {
                    if (allow[v] == 0) continue;
                    if (c0[v] == -1 || c1[v] == -1) continue;
                    if (s0[v] != -s1[v]) continue;

                    var v1 = c0[v];
                    var v2 = c1[v];
                    if (s0[v] < 0) (v1, v2) = (v2, v1);

                    var c = edgeDiff[v / 2][v % 2];
                    arcVariable.Add(v);
                    // Flow on the forward arc lowers the offset by one, on the backward arc raises it.
                    arcForward.Add(flow.AddArc(v1, v2, Mathf.Max(0, c + capacity), FlowCost(v, -1)));
                    arcBackward.Add(flow.AddArc(v2, v1, Mathf.Max(0, -c + capacity), FlowCost(v, +1)));
                }

                var supply = 0;
                int routed;
                if (excessFlow)
                {
                    var excess = new int[nodes + 2];
                    for (var eq = 0; eq < nodes; eq++)
                    {
                        excess[eq] = initial[eq];
                        if (initial[eq] > 0) supply += initial[eq];
                    }
                    routed = flow.SolveExcess(excess);
                }
                else
                {
                    for (var eq = 0; eq < nodes; eq++)
                    {
                        if (initial[eq] > 0)
                        {
                            flow.AddArc(source, eq, initial[eq], 0);
                            supply += initial[eq];
                        }
                        else if (initial[eq] < 0)
                        {
                            flow.AddArc(eq, sink, -initial[eq], 0);
                        }
                    }

                    routed = flow.Solve(source, sink);
                }

                for (var i = 0; i < arcVariable.Count; i++)
                {
                    var v = arcVariable[i];
                    var d = edgeDiff[v / 2];
                    var delta = -flow.FlowOn(arcForward[i]) + flow.FlowOn(arcBackward[i]);
                    if (delta != 0)
                        edgeDiff[v / 2] = WithComponent(d, v % 2, d[v % 2] + delta);
                }

                iterations++;
                stats.flowNodes = nodes;
                stats.flowArcs = arcVariable.Count;
                stats.flowSupply = supply;
                stats.flowRouted = routed;
                stats.flowIterations = iterations;
                stats.flowCapacity = capacity;
                stats.flowCost = flow.totalCost;
                stats.flowPhases += flow.phases;
                stats.flowSettled += flow.settled;

                if (routed == supply || iterations >= maxIterations) break;
                capacity++;
            }

            stats.unsatisfiedAfter = CountUnsatisfied();
            stats.flowMs = watch.ElapsedMilliseconds;
        }
    }
}
