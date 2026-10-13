// A quad layout read off a seamless parametrisation, quantised, and meshed patch by patch.
//
// Rounding the integer grid map's unknowns one by one -- singularities, then cut translations,
// then creases -- decides each in isolation, and two singularities the field put a fraction of a
// cell apart are rounded onto one node or a whole cell apart: their fans fold (see the round 7
// census in Benchmarks/round5-parametrization.md), and no amount of untangling opens a map whose
// integers are inconsistent. This is the route of Campen, Bommes and Kobbelt, "Quantized Global
// Parametrization" (SIGGRAPH Asia 2015), and of QuadWild (Pietroni et al. 2021):
//
//   layout     the separatrices of every singularity, traced along iso-lines of the free -- not
//              yet integer -- and fold-free map, each stopping where it meets one already there
//              (the motorcycle graph). Every face of the resulting graph is a rectangle in (u, v).
//   quantise   an integer length for every arc of the graph, every rectangle's opposite sides
//              equal, no length negative. Two singularities a fraction of a cell apart get an arc
//              of 0 or 1 between them, consistently, instead of colliding.
//   mesh       each rectangle, now w x h cells, a regular grid mapped through the free map.
//
// The map comes in as per-corner (u, v) on the decimated mesh; transitions across every edge are
// read off it, as ParametrizationTrace does, but real-valued: the free map's translations are not
// integers.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class QuantizedLayoutStats
    {
        public int faces, foldedFaces, singularVertices, coneMismatch;
        public int traces, steps, hitTrace, hitCollinear, hitBoundary, hitFold, hitLimit, hitSelf;
        public int nodes, arcs, layoutFaces, rectangles, annuli, otherFaces, reflexCorners, openWalks;
        public string faceCorners = "";
        public int constraintRows, zeroArcs, violatedRows, clampedArcs, quantSolves, quantFactorFailures, propagated, propagationConflicts, repairs, repairFailed, repairExpanded;
        public double quantDeviation, freeResidual;
        public int quads, mergedPoints, flatFaces, unequalFaces, degenerateQuads, walkFailed, walkCrossings;
        public int creaseTraces, creaseLoops, creaseExtensions, creaseClashes, crossings, layoutAttempts, duplicateEnds, creaseDropped, spirals, ringsClosed, truncated, truncatedJunctions;
        public long ms;
        public string debug = "";

        public override string ToString() => debug +
            $"tris={faces} folded={foldedFaces} singular={singularVertices} coneMismatch={coneMismatch} " +
            $"creases={creaseTraces}(loops {creaseLoops} extensions {creaseExtensions} clashes {creaseClashes}) attempts={layoutAttempts} crossings={crossings} dupEnds={duplicateEnds} creaseDropped={creaseDropped} spirals={spirals} rings={ringsClosed} truncated={truncated}/{truncatedJunctions} traces={traces} steps={steps} hit[trace {hitTrace} collinear {hitCollinear} self {hitSelf} boundary {hitBoundary} fold {hitFold} limit {hitLimit}] " +
            $"nodes={nodes} arcs={arcs} faces={layoutFaces} (rect {rectangles} annuli {annuli} other {otherFaces} reflex {reflexCorners} open {openWalks}) corners[{faceCorners}] quant[rows {constraintRows} free residual {freeResidual:G3} deviation {quantDeviation:F1} zero {zeroArcs} clamped {clampedArcs} violated {violatedRows} propagated {propagated} conflicts {propagationConflicts} repairs {repairs} repairFailed {repairFailed} (expanded {repairExpanded}) solves {quantSolves} failed {quantFactorFailures}] mesh[quads {quads} merged {mergedPoints} flat {flatFaces} unequal {unequalFaces} degenerate {degenerateQuads} walkFailed {walkFailed} crossings {walkCrossings}] ms={ms}";
    }

    /// <summary>
    /// A separatrix that came back round to a triangle it crossed before, going the same way but
    /// shifted sideways: the map's twist round a cylinder, which makes its iso-lines spiral.
    /// </summary>
    public sealed class LayoutLoop
    {
        public int face;              // the triangle met twice, whose chart the shift is in
        public int dir;               // the separatrix's direction there
        public double shift;          // sideways offset, in cells
        public int[] crossed;         // half-edges crossed in between, in order
        public int[] turns;           // their transitions' quarter turns
    }

    public static class QuantizedLayout
    {
        /// <summary>Weight of a rectangle's side equation against one arc's deviation.</summary>
        public static double quantizeConstraintWeight = 1e6;

        /// <summary>Rows a repair path may visit.</summary>
        public static int repairSearchLimit = 20000;

        /// <summary>Fraction of the open lengths rounded per solve.</summary>
        public static double quantizeBatch = 0.1;

        static double[] Quantize(double[] target, List<List<(int arc, double coef)>> rows, Vector3[] points, QuantizedLayoutStats stats)
        {
            var n = target.Length;
            var x = (double[])target.Clone();
            if (n == 0) return x;
            // Normal equations: I + mu C^T C, b = target.
            var entries = new Dictionary<long, double>();
            void Add(int i, int j, double v)
            {
                var key = ((long)i << 32) | (uint)j;
                entries[key] = (entries.TryGetValue(key, out var o) ? o : 0) + v;
            }
            for (var i = 0; i < n; i++) Add(i, i, 1.0);
            foreach (var row in rows)
                foreach (var (a, ca) in row)
                    foreach (var (b, cb) in row)
                        Add(a, b, quantizeConstraintWeight * ca * cb);
            var keys = new List<long>(entries.Keys);
            keys.Sort();
            var start = new int[n + 1];
            var col = new int[keys.Count];
            var val = new double[keys.Count];
            for (var k = 0; k < keys.Count; k++)
            {
                var i = (int)(keys[k] >> 32);
                start[i + 1]++;
                col[k] = (int)(keys[k] & 0xffffffff);
                val[k] = entries[keys[k]];
            }
            for (var i = 0; i < n; i++) start[i + 1] += start[i];
            var ldlt = new SparseLdlt(n, start, col, points);
            var fixedVar = new bool[n];
            var rhs = new double[n];
            void Solve()
            {
                if (!ldlt.Factor(val, fixedVar)) { stats.quantFactorFailures++; return; }
                for (var r = 0; r < n; r++)
                {
                    if (fixedVar[r]) { rhs[r] = x[r]; continue; }
                    var sum = target[r];
                    for (var j = start[r]; j < start[r + 1]; j++)
                        if (fixedVar[col[j]]) sum -= val[j] * x[col[j]];
                    rhs[r] = sum;
                }
                ldlt.Solve(rhs, x);
                stats.quantSolves++;
            }
            // Rows of each arc, for propagation: a row with one open arc left decides it.
            var rowsOf = new List<int>[n];
            for (var r = 0; r < rows.Count; r++)
                foreach (var (a, _) in rows[r]) (rowsOf[a] ??= new List<int>(2)).Add(r);
            void Propagate(List<int> changed)
            {
                var stack = new Stack<int>(changed);
                while (stack.Count > 0)
                {
                    var a0 = stack.Pop();
                    if (rowsOf[a0] == null) continue;
                    foreach (var r in rowsOf[a0])
                    {
                        var openArc = -1;
                        var openCoef = 0.0;
                        var openCount = 0;
                        double sum = 0;
                        foreach (var (a, c) in rows[r])
                        {
                            if (fixedVar[a]) sum += c * x[a];
                            else { openCount++; openArc = a; openCoef = c; }
                        }
                        if (openCount != 1) continue;
                        var value = -sum / openCoef;
                        if (value < -0.5) { stats.propagationConflicts++; value = 0; }
                        x[openArc] = Math.Max(0, Math.Round(value));
                        fixedVar[openArc] = true;
                        stats.propagated++;
                        stack.Push(openArc);
                    }
                }
            }
            Solve();
            var open = new List<int>();
            for (var i = 0; i < n; i++) open.Add(i);
            while (open.Count > 0)
            {
                // Negative lengths first, held at zero; then the nearest to an integer.
                var any = false;
                var clamped = new List<int>();
                foreach (var i in open)
                    if (x[i] < 0) { x[i] = 0; fixedVar[i] = true; any = true; stats.clampedArcs++; clamped.Add(i); }
                if (any)
                {
                    Propagate(clamped);
                    open.RemoveAll(i => fixedVar[i]);
                    Solve();
                    continue;
                }
                open.Sort((p, q) => Math.Abs(x[p] - Math.Round(x[p])).CompareTo(Math.Abs(x[q] - Math.Round(x[q]))));
                var take = Math.Max(1, (int)Math.Ceiling(open.Count * quantizeBatch));
                // One at a time, each followed by what it decides: a row is never closed by two
                // roundings at once.
                for (var k = 0; k < take; k++)
                {
                    var i = open[k];
                    if (fixedVar[i]) continue;
                    x[i] = Math.Max(0, Math.Round(x[i]));
                    fixedVar[i] = true;
                    Propagate(new List<int> { i });
                }
                open.RemoveAll(i => fixedVar[i]);
                if (open.Count > 0) Solve();
            }

            // Repair: a row left unequal (an arc decided by one rectangle that also closes another)
            // passes its error along -- one arc +-1, which moves the error into that arc's other
            // row -- breadth first, until it meets a row unequal the other way, or an arc no other
            // row holds. Augmenting paths, as in a flow.
            double Residual(int r)
            {
                double sum = 0;
                foreach (var (a, c) in rows[r]) sum += c * x[a];
                return sum;
            }
            var residual = new double[rows.Count];
            for (var r = 0; r < rows.Count; r++) residual[r] = Math.Round(Residual(r));
            for (var pass = 0; pass < 4; pass++)
            {
                var progress = false;
                for (var r0 = 0; r0 < rows.Count; r0++)
                {
                    var attempts = 0;
                    while (residual[r0] != 0 && attempts++ < 8)
                    {
                        double before = 0;
                        foreach (var rr in residual) before += Math.Abs(rr);
                        var saved = (double[])x.Clone();
                        // BFS state: a row and the change its sum needs; parent link through an arc.
                        var need0 = -Math.Sign(residual[r0]);
                        var parent = new Dictionary<(int row, int need), (int prevRow, int prevNeed, int arc, int delta)>();
                        var q = new Queue<(int row, int need)>();
                        q.Enqueue((r0, need0));
                        parent[(r0, need0)] = (-1, 0, -1, 0);
                        (int row, int need, int arc, int delta, int endRow) goal = (-1, 0, -1, 0, -1);
                        var expanded = 0;
                        while (q.Count > 0 && goal.arc < 0 && expanded < repairSearchLimit)
                        {
                            var (r, need) = q.Dequeue();
                            expanded++;
                            foreach (var (a, c) in rows[r])
                            {
                                var delta = (int)(need / c);
                                if (delta * c != need || x[a] + delta < 0) continue;
                                // The arc's other row, if any.
                                var other = -1;
                                double otherCoef = 0;
                                foreach (var r2 in rowsOf[a])
                                    if (r2 != r) { other = r2; foreach (var (b, cb) in rows[r2]) if (b == a) otherCoef = cb; break; }
                                if (other < 0) { goal = (r, need, a, delta, -1); break; }
                                var change = otherCoef * delta;
                                if (other == r0) continue;   // back where it started: that undoes the first step
                                if (residual[other] != 0 && Math.Sign(residual[other]) == -Math.Sign(change) && Math.Abs(residual[other]) >= Math.Abs(change))
                                {
                                    goal = (r, need, a, delta, other);
                                    break;
                                }
                                var key = (other, (int)(-change));
                                if (parent.ContainsKey(key)) continue;
                                parent[key] = (r, need, a, delta);
                                q.Enqueue(key);
                            }
                        }
                        if (goal.arc < 0) { stats.repairFailed++; stats.repairExpanded = Math.Max(stats.repairExpanded, expanded); break; }
                        // Apply the path back to r0.
                        x[goal.arc] += goal.delta;
                        var log = $"[path r0={r0} res={residual[r0]} goal arc{goal.arc}{goal.delta:+0;-0} end row {goal.endRow} res {(goal.endRow >= 0 ? residual[goal.endRow] : 0)}";
                        var cur = (goal.row, goal.need);
                        while (true)
                        {
                            var pr = parent[cur];
                            if (pr.prevRow < 0) break;
                            x[pr.arc] += pr.delta;
                            log += $" <- r{cur.Item1} via arc{pr.arc}{pr.delta:+0;-0}";
                            cur = (pr.prevRow, pr.prevNeed);
                        }
                        for (var r = 0; r < rows.Count; r++) residual[r] = Math.Round(Residual(r));
                        double after = 0;
                        foreach (var rr in residual) after += Math.Abs(rr);
                        if (after >= before)
                        {
                            // A path that did not help: undone, and the row left.
                            Array.Copy(saved, x, n);
                            for (var r = 0; r < rows.Count; r++) residual[r] = Math.Round(Residual(r));
                            stats.repairFailed++;
                            if (stats.repairFailed <= 3) stats.debug += log + $" before {before} after {after}] ";
                            break;
                        }
                        stats.repairs++;
                        progress = true;
                    }
                }
                if (!progress) break;
            }
            return x;
        }

        /// <summary>
        /// Sideways distance, in cells, under which a separatrix running along another one meets
        /// it -- a loop round a cylinder whose twist was held to zero closes on itself.
        /// </summary>
        public static double collinearTolerance = 1e-3;

        /// <summary>Diagnostics: write the layout's arcs to this path.</summary>
        public static string debugArcPath = null;

        /// <summary>Extra layout passes that add lines into faces that are not rectangles.</summary>
        public static int layoutAttempts = 3;

        /// <summary>Triangles a separatrix may cross before it is given up.</summary>
        public static int maxSteps = 20000;

        static readonly int[] DirX = { 1, 0, -1, 0 };
        static readonly int[] DirY = { 0, 1, 0, -1 };

        static int Mod4(int k) => ((k % 4) + 4) % 4;

        static int Mod4Of(int k, int m) => m <= 0 ? 0 : ((k % m) + m) % m;

        static void Rot(int k, double x, double y, out double rx, out double ry)
        {
            switch (Mod4(k))
            {
                case 0: rx = x; ry = y; break;
                case 1: rx = -y; ry = x; break;
                case 2: rx = -x; ry = -y; break;
                default: rx = y; ry = -x; break;
            }
        }

        static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

        // One piece of a separatrix inside one triangle, in that triangle's chart.
        struct Segment
        {
            public int trace;
            public double ax, ay, bx, by;
            public int dir;
            public double s0;          // the trace's length at a
        }

        sealed class Trace
        {
            public int startNode;
            public int endNode = -1;
            public double length;
            public int face, dir, steps;
            public double px, py;
            public bool active = true;
            // Nodes along it: (length from the start, node).
            public readonly List<(double s, int node)> points = new List<(double, int)>();
            // Half-edges crossed so far, and the first visit of each triangle: (crossed count, side).
            public readonly List<int> crossed = new List<int>();
            public Dictionary<int, (int count, double side)> visited;
            public bool looped;
            // Its pieces, in order: (face, start, end, direction, length at the start).
            public readonly List<(int face, double ax, double ay, double bx, double by, int dir, double s0)> path
                = new List<(int, double, double, double, double, int, double)>();
        }

        // A node of the layout: a singularity, or where a separatrix ended on another.
        sealed class Node
        {
            public int vertex = -1;           // mesh vertex of a singularity
            public int face = -1;             // chart a junction's directions are in
            public double u, v;
            // Its arc ends, each (arc, end 0 = the arc leaves here / 1 = arrives here, position).
            // Position: quarter turns counter-clockwise, comparable within the node only.
            public readonly List<(int arc, int end, int pos)> ends = new List<(int, int, int)>();
            public int quarterTurns = 4;      // full turn round the node
        }

        sealed class Arc
        {
            public int trace;
            public double s0, s1;
            public int node0, node1;
        }

        /// <summary>
        /// Traces the layout of <paramref name="uv"/> (per corner, interleaved) on
        /// <paramref name="topology"/> and reports it. The quantisation and meshing are built on
        /// this; for now it is the layout and its statistics.
        /// </summary>
        public static QuantizedLayoutStats Build(RemeshTopology topology, double[] uv)
            => Build(topology, uv, null, out _, out _, out _);

        /// <summary>
        /// Traces, quantises and meshes the layout of <paramref name="uv"/> (per corner, interleaved)
        /// on <paramref name="topology"/>. The quads are wound like the input.
        /// </summary>
        public static QuantizedLayoutStats Build(RemeshTopology topology, double[] uv,
            List<(int a, int b)> featureEdges,
            out Vector3[] positions, out Vector3[] normals, out List<int[]> quads)
            => Build(topology, uv, featureEdges, out positions, out normals, out quads, out _);

        public static QuantizedLayoutStats Build(RemeshTopology topology, double[] uv,
            List<(int a, int b)> featureEdges,
            out Vector3[] positions, out Vector3[] normals, out List<int[]> quads, out List<LayoutLoop> loops)
        {
            loops = new List<LayoutLoop>();
            var loopsFound = loops;
            positions = null;
            normals = null;
            quads = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stats = new QuantizedLayoutStats();
            var T = topology.triangles;
            var twin = topology.halfEdgeTwin;
            var m = T.Length / 3;
            var n = topology.vertexCount;
            stats.faces = m;
            double U(int c) => uv[2 * c];
            double V(int c) => uv[2 * c + 1];

            var valid = new bool[m];
            for (var f = 0; f < m; f++)
            {
                int c0 = 3 * f, c1 = c0 + 1, c2 = c0 + 2;
                valid[f] = Cross(U(c1) - U(c0), V(c1) - V(c0), U(c2) - U(c0), V(c2) - V(c0)) > 0;
                if (!valid[f]) stats.foldedFaces++;
            }

            // --- transitions: uv in the face across = R^rot uv + t (t real) ---------------------
            var rot = new int[3 * m];
            var tu = new double[3 * m];
            var tv = new double[3 * m];
            for (var h = 0; h < 3 * m; h++)
            {
                var o = twin[h];
                if (o < 0) continue;
                var f = h / 3;
                var g = o / 3;
                int a = h, b = 3 * f + (h % 3 + 1) % 3;
                int gb = o, ga = 3 * g + (o % 3 + 1) % 3;
                double ex = U(b) - U(a), ey = V(b) - V(a);
                double gx = U(gb) - U(ga), gy = V(gb) - V(ga);
                var best = 0;
                var bestErr = double.PositiveInfinity;
                for (var k = 0; k < 4; k++)
                {
                    Rot(k, ex, ey, out var rx, out var ry);
                    var err = Math.Abs(rx - gx) + Math.Abs(ry - gy);
                    if (err < bestErr) { bestErr = err; best = k; }
                }
                rot[h] = best;
                Rot(best, U(a), V(a), out var pax, out var pay);
                tu[h] = U(ga) - pax;
                tv[h] = V(ga) - pay;
            }

            // --- vertex fans: counter-clockwise corner order and cone angle ----------------------
            var cornersOf = new List<int>[n];
            for (var c = 0; c < 3 * m; c++) (cornersOf[T[c]] ??= new List<int>(6)).Add(c);
            int CcwNext(int c)
            {
                var f = c / 3;
                var o = twin[3 * f + (c % 3 + 2) % 3];
                return o;                                  // the corner of the same vertex, or -1
            }
            double CornerAngle(int c)
            {
                var f = c / 3;
                int c1 = 3 * f + (c % 3 + 1) % 3, c2 = 3 * f + (c % 3 + 2) % 3;
                double ax = U(c1) - U(c), ay = V(c1) - V(c), bx = U(c2) - U(c), by = V(c2) - V(c);
                return Math.Atan2(Cross(ax, ay, bx, by), ax * bx + ay * by);
            }
            var fan = new List<int>[n];
            var valence = new int[n];
            for (var v = 0; v < n; v++)
            {
                var list = cornersOf[v];
                if (list == null) continue;
                var ordered = new List<int>(list.Count);
                var c = list[0];
                var closed = false;
                for (var guard = 0; guard <= list.Count; guard++)
                {
                    ordered.Add(c);
                    var nx = CcwNext(c);
                    if (nx < 0) break;
                    if (nx == list[0]) { closed = true; break; }
                    c = nx;
                }
                if (!closed || ordered.Count != list.Count) continue;   // border or non-manifold
                fan[v] = ordered;
                double angle = 0;
                foreach (var cc in ordered) angle += CornerAngle(cc);
                valence[v] = (int)Math.Round(angle / (0.5 * Math.PI));
                if (Math.Abs(angle / (0.5 * Math.PI) - valence[v]) > 0.25) stats.coneMismatch++;
                if (valence[v] != 4) stats.singularVertices++;
            }

            const double tiny = 1e-9;
            const double onLine = 1e-9;

            // --- the layout, again until every face is a rectangle --------------------------------
            List<Node> nodes = null;
            List<Trace> traces = null;
            List<Arc> arcs = null;
            List<(List<int> half, List<int> turns)> rectFaces = null;
            HashSet<int> badArcs = null;
            var extraEmits = new List<(int v, int pos)>();
            var continueThrough = new HashSet<int>();
            for (var attempt = 0; ; attempt++)
            {
                stats.traces = stats.steps = stats.hitTrace = stats.hitCollinear = stats.hitBoundary = stats.hitFold = stats.hitLimit = stats.hitSelf = 0;
                stats.nodes = stats.arcs = stats.layoutFaces = stats.rectangles = stats.annuli = stats.otherFaces = stats.reflexCorners = stats.openWalks = 0;
                stats.creaseTraces = stats.creaseLoops = stats.creaseExtensions = stats.creaseClashes = stats.crossings = stats.duplicateEnds = stats.creaseDropped = stats.ringsClosed = stats.truncated = stats.truncatedJunctions = 0;
                stats.faceCorners = "";
                var crossedOnce = new HashSet<int>();
                // --- separatrices -------------------------------------------------------------------
                nodes = new List<Node>();
                traces = new List<Trace>();
                var segs = new List<Segment>[m];
                var queue = new SortedSet<(double s, int trace)>();

                // Every lattice direction leaving a vertex, with its position round the fan in quarter
                // turns; and the position at which each corner's sector starts.
                var sectorStart = new double[3 * m];
                for (var v = 0; v < n; v++)
                {
                    if (fan[v] == null) continue;
                    // Measured from the first corner's +u axis, so that axis directions sit on integers.
                    var c0 = fan[v][0];
                    var n0 = 3 * (c0 / 3) + (c0 % 3 + 1) % 3;
                    double cum = Math.Atan2(V(n0) - V(c0), U(n0) - U(c0)) / (0.5 * Math.PI);
                    foreach (var c in fan[v]) { sectorStart[c] = cum; cum += CornerAngle(c) / (0.5 * Math.PI); }
                }
                List<(int c, int d, int pos)> Directions(int v)
                {
                    var list = new List<(int, int, int)>();
                    foreach (var c in fan[v])
                    {
                        var f = c / 3;
                        if (!valid[f]) continue;
                        int c1 = 3 * f + (c % 3 + 1) % 3, c2 = 3 * f + (c % 3 + 2) % 3;
                        double e1x = U(c1) - U(c), e1y = V(c1) - V(c), e2x = U(c2) - U(c), e2y = V(c2) - V(c);
                        for (var d = 0; d < 4; d++)
                        {
                            var cr = Cross(e1x, e1y, DirX[d], DirY[d]);
                            if (cr < 0) continue;
                            if (cr == 0 && e1x * DirX[d] + e1y * DirY[d] <= 0) continue;
                            if (Cross(DirX[d], DirY[d], e2x, e2y) <= 0) continue;
                            var ang = Math.Atan2(cr, e1x * DirX[d] + e1y * DirY[d]) / (0.5 * Math.PI);
                            list.Add((c, d, Mod4Of((int)Math.Round(sectorStart[c] + ang), valence[v])));
                        }
                    }
                    return list;
                }

                var vertexNode = new Dictionary<int, int>();
                int NodeOf(int v)
                {
                    if (vertexNode.TryGetValue(v, out var id)) return id;
                    nodes.Add(new Node { vertex = v, quarterTurns = valence[v] });
                    vertexNode[v] = nodes.Count - 1;
                    return nodes.Count - 1;
                }
                void Emit(int v, int c, int d, int pos)
                {
                    var nodeId = NodeOf(v);
                    var tr = new Trace { startNode = nodeId, face = c / 3, dir = d, px = U(c), py = V(c) };
                    tr.points.Add((0, nodeId));
                    traces.Add(tr);
                    nodes[nodeId].ends.Add((-1 - (traces.Count - 1), 0, pos));
                    queue.Add((0, traces.Count - 1));
                }

                // --- creases: traces laid in advance, along mesh edges ------------------------------
                if (featureEdges != null)
                {
                    var halfOf = new Dictionary<(int, int), int>();
                    for (var h = 0; h < 3 * m; h++) halfOf[(T[h], T[3 * (h / 3) + (h % 3 + 1) % 3])] = h;
                    var adjacent = new Dictionary<int, List<int>>();
                    var edgeSet = new HashSet<(int, int)>();
                    foreach (var (a, b) in featureEdges)
                    {
                        if (a == b || fan[a] == null || fan[b] == null) continue;
                        if (!halfOf.ContainsKey((a, b)) || !halfOf.ContainsKey((b, a))) continue;
                        if (!edgeSet.Add((Math.Min(a, b), Math.Max(a, b)))) continue;
                        (adjacent.TryGetValue(a, out var la) ? la : adjacent[a] = new List<int>()).Add(b);
                        (adjacent.TryGetValue(b, out var lb) ? lb : adjacent[b] = new List<int>()).Add(a);
                    }
                    int PosToward(int v, int w) => Mod4Of((int)Math.Round(sectorStart[halfOf[(v, w)]]), valence[v]);
                    // Two crease edges leaving a vertex the same way cannot both be arcs: the shorter
                    // in (u, v) is left out.
                    foreach (var kv in new List<KeyValuePair<int, List<int>>>(adjacent))
                    {
                        var l = kv.Value;
                        for (var i = 0; i < l.Count; i++)
                            for (var j = i + 1; j < l.Count; j++)
                            {
                                if (PosToward(kv.Key, l[i]) != PosToward(kv.Key, l[j])) continue;
                                double Len(int w)
                                {
                                    var hh = halfOf[(kv.Key, w)];
                                    var nx = 3 * (hh / 3) + (hh % 3 + 1) % 3;
                                    return Math.Abs(U(nx) - U(hh)) + Math.Abs(V(nx) - V(hh));
                                }
                                var drop = Len(l[i]) < Len(l[j]) ? l[i] : l[j];
                                l.Remove(drop);
                                adjacent[drop].Remove(kv.Key);
                                stats.creaseDropped++;
                                i = -1;
                                break;
                            }
                    }
                    bool Break(int v)
                    {
                        var l = adjacent[v];
                        if (l.Count != 2 || valence[v] != 4) return true;
                        return Mod4Of(PosToward(v, l[0]) - PosToward(v, l[1]), 4) != 2;
                    }
                    foreach (var kv in adjacent)
                    {
                        var seen = new Dictionary<int, int>();
                        foreach (var w in kv.Value)
                        {
                            var pw = PosToward(kv.Key, w);
                            if (seen.ContainsKey(pw) && stats.creaseClashes++ < 4)
                            {
                                stats.debug += $"[clash v{kv.Key} val{valence[kv.Key]} deg{kv.Value.Count}:";
                                foreach (var x in kv.Value)
                                {
                                    var hh = halfOf[(kv.Key, x)];
                                    var nx = 3 * (hh / 3) + (hh % 3 + 1) % 3;
                                    stats.debug += $" ->{x}@{PosToward(kv.Key, x)} d({U(nx) - U(hh):F3},{V(nx) - V(hh):F3}) start {sectorStart[hh]:F2}";
                                }
                                stats.debug += "] ";
                            }
                            seen[pw] = w;
                        }
                    }
                    var usedEdge = new HashSet<(int, int)>();
                    void Lay(int v0, int w0)
                    {
                        var id = traces.Count;
                        var tr = new Trace { startNode = NodeOf(v0), active = false };
                        traces.Add(tr);
                        tr.points.Add((0, tr.startNode));
                        nodes[tr.startNode].ends.Add((-1 - id, 0, PosToward(v0, w0)));
                        int v = v0, w = w0;
                        while (true)
                        {
                            usedEdge.Add((Math.Min(v, w), Math.Max(v, w)));
                            var h = halfOf[(v, w)];
                            var o = halfOf[(w, v)];
                            // The piece in both triangles at the edge, each in its own chart.
                            double length = 0;
                            foreach (var (hc, cv, cw) in new[] { (h, h, 3 * (h / 3) + (h % 3 + 1) % 3), (o, 3 * (o / 3) + (o % 3 + 1) % 3, o) })
                            {
                                var f = hc / 3;
                                double ax = U(cv), ay = V(cv), bx = U(cw), by = V(cw);
                                double trueAx = ax, trueAy = ay, trueBx = bx, trueBy = by;
                                int dir;
                                if (Math.Abs(bx - ax) >= Math.Abs(by - ay))
                                {
                                    var y = 0.5 * (ay + by);
                                    ay = by = y;
                                    dir = bx >= ax ? 0 : 2;
                                }
                                else
                                {
                                    var x = 0.5 * (ax + bx);
                                    ax = bx = x;
                                    dir = by >= ay ? 1 : 3;
                                }
                                (segs[f] ??= new List<Segment>()).Add(new Segment { trace = id, ax = ax, ay = ay, bx = bx, by = by, dir = dir, s0 = tr.length });
                                if (hc == h)
                                {
                                    // Located on the edge itself, not on the averaged line: points placed on
                                    // it then lie in the triangle.
                                    tr.path.Add((f, trueAx, trueAy, trueBx, trueBy, dir, tr.length));
                                    length = Math.Abs(bx - ax) + Math.Abs(by - ay);
                                }
                            }
                            tr.length += length;
                            if (Break(w) || w == v0)
                            {
                                tr.endNode = NodeOf(w);
                                tr.points.Add((tr.length, tr.endNode));
                                nodes[tr.endNode].ends.Add((-1 - id, 1, PosToward(w, v)));
                                stats.creaseTraces++;
                                return;
                            }
                            var next = -1;
                            foreach (var x in adjacent[w]) if (x != v) next = x;
                            if (next < 0 || usedEdge.Contains((Math.Min(w, next), Math.Max(w, next))))
                            {
                                tr.endNode = NodeOf(w);
                                tr.points.Add((tr.length, tr.endNode));
                                nodes[tr.endNode].ends.Add((-1 - id, 1, PosToward(w, v)));
                                stats.creaseTraces++;
                                return;
                            }
                            v = w;
                            w = next;
                        }
                    }
                    foreach (var kv in adjacent)
                    {
                        if (!Break(kv.Key)) continue;
                        foreach (var w in kv.Value)
                            if (!usedEdge.Contains((Math.Min(kv.Key, w), Math.Max(kv.Key, w)))) Lay(kv.Key, w);
                    }
                    // Closed loops with no break on them: cut at any vertex.
                    foreach (var kv in adjacent)
                        foreach (var w in kv.Value)
                            if (!usedEdge.Contains((Math.Min(kv.Key, w), Math.Max(kv.Key, w)))) { Lay(kv.Key, w); stats.creaseLoops++; }
                }

                // --- separatrices, and the continuations a crease node needs --------------------------
                // A singularity sends one along every direction a crease does not already take. A
                // crease end or corner sends the fewest that leave no angle at it wider than two
                // quarters, which a rectangle could not hold.
                for (var v = 0; v < n; v++)
                {
                    if (fan[v] == null || valence[v] <= 0) continue;
                    var singular = valence[v] != 4;
                    var isNode = vertexNode.ContainsKey(v);
                    if (!singular && !isNode) continue;
                    var occupied = new HashSet<int>();
                    if (isNode) foreach (var e in nodes[vertexNode[v]].ends) occupied.Add(e.pos);
                    var dirs = Directions(v);
                    if (singular)
                    {
                        foreach (var (c, d, pos) in dirs)
                            if (occupied.Add(pos)) Emit(v, c, d, pos);
                        var count = 0;
                        foreach (var _ in dirs) count++;
                        if (count != valence[v]) stats.debug += $"[v{v} valence {valence[v]} seps {count}] ";
                        continue;
                    }
                    for (var guard = 0; guard < 4; guard++)
                    {
                        var sorted = new List<int>(occupied);
                        sorted.Sort();
                        var want = -1;
                        for (var i = 0; i < sorted.Count; i++)
                        {
                            var a = sorted[i];
                            var b = i + 1 < sorted.Count ? sorted[i + 1] : sorted[0] + 4;
                            if (b - a > 2) { want = Mod4Of(a + 2, 4); break; }
                        }
                        if (want < 0) break;
                        var emitted = false;
                        foreach (var (c, d, pos) in dirs)
                            if (pos == want) { Emit(v, c, d, pos); occupied.Add(pos); emitted = true; stats.creaseExtensions++; break; }
                        if (!emitted) { stats.debug += $"[ext v{v} pos {want} missing] "; break; }
                    }
                }
                // Lines added by earlier attempts, into faces that came out other than rectangles.
                foreach (var (v, want) in extraEmits)
                    foreach (var (c, d, pos) in Directions(v))
                    {
                        if (pos != want) continue;
                        var taken = false;
                        foreach (var e in nodes[NodeOf(v)].ends) if (e.pos == want) taken = true;
                        if (!taken) Emit(v, c, d, pos);
                        break;
                    }
                stats.traces = traces.Count;


                // A new node where trace `id` ends on trace `hitTrace` at length sHit, in face f's chart.
                int Junction(int id, int hitTrace, double sHit, int f, double x, double y, int hitDir, int arriveDir)
                {
                    var node = new Node { face = f, u = x, v = y };
                    nodes.Add(node);
                    var nodeId = nodes.Count - 1;
                    traces[hitTrace].points.Add((sHit, nodeId));
                    // Positions are directions in f's chart: along the hit trace both ways, and back along
                    // the arriving one.
                    node.ends.Add((-1 - hitTrace, 2, Mod4(hitDir)));          // hit trace, forwards
                    node.ends.Add((-1 - hitTrace, 3, Mod4(hitDir + 2)));      // hit trace, backwards
                    node.ends.Add((-1 - id, 1, Mod4(arriveDir + 2)));         // the arriving trace's end
                    return nodeId;
                }

                // For a trace meeting segment sg of another (or itself) going the same way: the position
                // at the other's start node it would arrive at, if it may end there (within a cell of
                // that start, and the position free); else -1.
                // Cuts trace `id` back to length s: its pieces beyond, in its path and in the triangles.
                void Truncate(int id, double sCut)
                {
                    var t0 = traces[id];
                    for (var i = t0.path.Count - 1; i >= 0; i--)
                    {
                        var pc = t0.path[i];
                        var plen = Math.Abs(pc.bx - pc.ax) + Math.Abs(pc.by - pc.ay);
                        var list0 = segs[pc.face];
                        if (pc.s0 >= sCut - tiny)
                        {
                            t0.path.RemoveAt(i);
                            list0?.RemoveAll(q => q.trace == id && Math.Abs(q.s0 - pc.s0) < 1e-12);
                            continue;
                        }
                        if (pc.s0 + plen > sCut)
                        {
                            var w = (sCut - pc.s0) / Math.Max(plen, 1e-300);
                            var nbx = pc.ax + w * (pc.bx - pc.ax);
                            var nby = pc.ay + w * (pc.by - pc.ay);
                            t0.path[i] = (pc.face, pc.ax, pc.ay, nbx, nby, pc.dir, pc.s0);
                            if (list0 != null)
                                for (var q = 0; q < list0.Count; q++)
                                    if (list0[q].trace == id && Math.Abs(list0[q].s0 - pc.s0) < 1e-12)
                                    {
                                        var sq = list0[q];
                                        sq.bx = nbx;
                                        sq.by = nby;
                                        list0[q] = sq;
                                    }
                        }
                        break;
                    }
                    var dropped = t0.points.RemoveAll(q => q.s > sCut + tiny);
                    stats.truncated++;
                    stats.truncatedJunctions += dropped;
                    t0.length = sCut;
                }

                int MergeTarget(int id, Segment sg)
                {
                    if (sg.s0 > 1.0) return -1;
                    var h = traces[sg.trace];
                    var nd = nodes[h.startNode];
                    var leave = -1;
                    foreach (var e in nd.ends) if (e.arc == -1 - sg.trace && e.end == 0) leave = e.pos;
                    if (leave < 0) return -1;
                    var want = Mod4Of(leave + 2, nd.quarterTurns);
                    foreach (var e in nd.ends) if (e.pos == want) return -1;
                    return want;
                }

                while (queue.Count > 0)
                {
                    var (s, id) = queue.Min;
                    queue.Remove(queue.Min);
                    var tr = traces[id];
                    if (!tr.active) continue;
                    var f = tr.face;
                    if (!valid[f]) { stats.hitFold++; tr.active = false; continue; }
                    if (++tr.steps > maxSteps) { stats.hitLimit++; tr.active = false; continue; }
                    stats.steps++;
                    double dx = DirX[tr.dir], dy = DirY[tr.dir];
                    // Exit edge: tail right of the ray, head left of it (on the line counts as right: the
                    // ray runs just left of it).
                    var exit = -1;
                    double qx = 0, qy = 0;
                    for (var k = 0; k < 3; k++)
                    {
                        int a = 3 * f + k, b = 3 * f + (k + 1) % 3;
                        var sa = Cross(dx, dy, U(a) - tr.px, V(a) - tr.py);
                        var sb = Cross(dx, dy, U(b) - tr.px, V(b) - tr.py);
                        if (!(sa <= onLine) || sb <= onLine) continue;
                        var lambda = sa >= 0 ? 0 : -sa / (sb - sa);
                        var ix = U(a) + lambda * (U(b) - U(a));
                        var iy = V(a) + lambda * (V(b) - V(a));
                        // Ahead of the point (or at it, leaving a corner).
                        if ((ix - tr.px) * dx + (iy - tr.py) * dy < -1e-12) continue;
                        exit = a;
                        qx = ix;
                        qy = iy;
                        break;
                    }
                    if (exit < 0) { stats.hitFold++; tr.active = false; stats.debug += stats.hitFold <= 3 ? $"[noexit t{id} f{f}] " : ""; continue; }
                    var len = Math.Abs(qx - tr.px) + Math.Abs(qy - tr.py);

                    // The earliest separatrix already in this triangle that the step meets.
                    var hitT = double.PositiveInfinity;
                    var hitIndex = -1;
                    var hitCollinear = false;
                    var list = segs[f];
                    if (list != null)
                    {
                        for (var i = 0; i < list.Count; i++)
                        {
                            var sg = list[i];
                            // A line through this trace's own start node, met within a hundredth of a cell:
                            // the start itself, off by the crease's rounding.
                            var near = tr.length < 1e-2 && (traces[sg.trace].startNode == tr.startNode || traces[sg.trace].endNode == tr.startNode);
                            if (sg.trace == id && Math.Abs(sg.s0 + Math.Abs(sg.bx - sg.ax) + Math.Abs(sg.by - sg.ay) - tr.length) < 1e-12) continue;
                            // Distance along the step to the segment's line, and where on it.
                            if (Mod4(sg.dir - tr.dir) % 2 == 1)
                            {
                                // Perpendicular: the segment is a line across the step.
                                double t, along, lo, hi;
                                if (dx != 0) { t = (sg.ax - tr.px) * dx; along = tr.py; lo = Math.Min(sg.ay, sg.by); hi = Math.Max(sg.ay, sg.by); }
                                else { t = (sg.ay - tr.py) * dy; along = tr.px; lo = Math.Min(sg.ax, sg.bx); hi = Math.Max(sg.ax, sg.bx); }
                                if (t <= tiny || t > len + tiny) continue;
                                if (near && tr.length + t < 1e-2) continue;
                                if (along < lo - onLine || along > hi + onLine) continue;
                                if (t < hitT) { hitT = t; hitIndex = i; hitCollinear = false; }
                            }
                            else
                            {
                                // Parallel: only on the same line, met at its nearest end.
                                var off = dx != 0 ? sg.ay - tr.py : sg.ax - tr.px;
                                if (Math.Abs(off) > collinearTolerance) continue;
                                var ta = (sg.ax - tr.px) * dx + (sg.ay - tr.py) * dy;
                                var tb = (sg.bx - tr.px) * dx + (sg.by - tr.py) * dy;
                                var t = Math.Min(ta, tb);
                                if (t <= tiny || t > len + tiny) continue;
                                if (near && tr.length + t < 1e-2) continue;
                                // Going the same way along another line: only a ring closing on its own
                                // start is met (it ends there, see below); anything else runs on.
                                if (sg.dir == tr.dir && MergeTarget(id, sg) < 0) continue;
                                if (t < hitT) { hitT = t; hitIndex = i; hitCollinear = true; }
                            }
                        }
                    }

                    if (hitIndex >= 0)
                    {
                        var sg = list[hitIndex];
                        var hx = tr.px + hitT * dx;
                        var hy = tr.py + hitT * dy;
                        // Along the hit line only: a parallel line met is up to collinearTolerance off it.
                        var sHit = sg.s0 + (sg.dir % 2 == 0 ? Math.Abs(hx - sg.ax) : Math.Abs(hy - sg.ay));
                        // The last piece, up to the meeting point.
                        (segs[f] ??= new List<Segment>()).Add(new Segment { trace = id, ax = tr.px, ay = tr.py, bx = hx, by = hy, dir = tr.dir, s0 = tr.length });
                        tr.path.Add((f, tr.px, tr.py, hx, hy, tr.dir, tr.length));
                        tr.length += hitT;
                        tr.active = false;
                        if (sg.trace == id) stats.hitSelf++;
                        if (hitCollinear && sg.dir == tr.dir)
                        {
                            // A ring closing on the start of the line it runs along: it ends at that
                            // start, arriving from the side the line leaves by.
                            stats.ringsClosed++;
                            var target = MergeTarget(id, sg);
                            var start = traces[sg.trace].startNode;
                            tr.endNode = start;
                            nodes[start].ends.Add((-1 - id, 1, target));
                        }
                        else if (hitCollinear)
                        {
                            // Head on, along one line: the two are one arc; they meet at the other's tip,
                            // which stops there too.
                            stats.hitCollinear++;
                            var other = traces[sg.trace];
                            var nodeId = Junction(id, sg.trace, sHit, f, hx, hy, sg.dir, tr.dir);
                            // The other one stops here too -- cut back if it already ran on into the
                            // next triangle, where this one would meet its track a second time.
                            if (other.endNode < 0 || other.length > sHit + collinearTolerance)
                            {
                                if (other.length > sHit + collinearTolerance) Truncate(sg.trace, sHit);
                                other.active = false;
                                other.endNode = nodeId;
                            }
                            tr.endNode = nodeId;
                        }
                        else if (continueThrough.Contains(id) && !crossedOnce.Contains(id))
                        {
                            // Crossing: a node with the hit trace both ways and this one both ways, and on.
                            crossedOnce.Add(id);
                            stats.crossings++;
                            var node = new Node { face = f, u = hx, v = hy };
                            nodes.Add(node);
                            var nodeId = nodes.Count - 1;
                            traces[sg.trace].points.Add((sHit, nodeId));
                            tr.points.Add((tr.length, nodeId));
                            node.ends.Add((-1 - sg.trace, 2, Mod4(sg.dir)));
                            node.ends.Add((-1 - sg.trace, 3, Mod4(sg.dir + 2)));
                            node.ends.Add((-1 - id, 3, Mod4(tr.dir + 2)));
                            node.ends.Add((-1 - id, 2, Mod4(tr.dir)));
                            tr.active = true;
                            tr.px = hx;
                            tr.py = hy;
                            // Off the crossed line, so it is not met again at once.
                            tr.length += 0;
                            queue.Add((tr.length, id));
                            continue;
                        }
                        else
                        {
                            stats.hitTrace++;
                            tr.endNode = Junction(id, sg.trace, sHit, f, hx, hy, sg.dir, tr.dir);
                        }
                        tr.points.Add((tr.length, tr.endNode));
                        continue;
                    }

                    (segs[f] ??= new List<Segment>()).Add(new Segment { trace = id, ax = tr.px, ay = tr.py, bx = qx, by = qy, dir = tr.dir, s0 = tr.length });
                    tr.path.Add((f, tr.px, tr.py, qx, qy, tr.dir, tr.length));
                    tr.length += len;
                    var o = twin[exit];
                    if (o < 0) { stats.hitBoundary++; tr.active = false; continue; }
                    // Spirals: this triangle met before, the same way, shifted sideways.
                    if (attempt == 0 && !tr.looped)
                    {
                        tr.visited ??= new Dictionary<int, (int, double)>();
                        var side = tr.dir % 2 == 0 ? tr.py : tr.px;
                        var key = f * 4 + tr.dir;
                        if (tr.visited.TryGetValue(key, out var first))
                        {
                            var shift = side - first.side;
                            if (Math.Abs(shift) > collinearTolerance)
                            {
                                var cr = new int[tr.crossed.Count - first.count];
                                var tq = new int[cr.Length];
                                for (var i = 0; i < cr.Length; i++) { cr[i] = tr.crossed[first.count + i]; tq[i] = rot[cr[i]]; }
                                loopsFound.Add(new LayoutLoop { face = f, dir = tr.dir, shift = shift, crossed = cr, turns = tq });
                                stats.spirals++;
                                if (stats.spirals <= 6) stats.debug += $"[spiral t{id} f{f} d{tr.dir} shift {shift:F4} crossed {cr.Length}] ";
                            }
                            tr.looped = true;
                        }
                        else tr.visited[key] = (tr.crossed.Count, side);
                    }
                    tr.crossed.Add(exit);
                    var nxp = qx;
                    var nyp = qy;
                    Rot(rot[exit], nxp, nyp, out var rx, out var ry);
                    tr.px = rx + tu[exit];
                    tr.py = ry + tv[exit];
                    tr.dir = Mod4(tr.dir + rot[exit]);
                    tr.face = o / 3;
                    queue.Add((tr.length, id));
                }

                // --- arcs: each trace cut at its nodes ---------------------------------------------
                arcs = new List<Arc>();
                // (trace, which end-kind, node) -> arc, for resolving the node ends.
                var traceArcs = new List<Arc>[traces.Count];
                for (var id = 0; id < traces.Count; id++)
                {
                    var tr = traces[id];
                    tr.points.Sort((p, q) => p.s.CompareTo(q.s));
                    var list = traceArcs[id] = new List<Arc>();
                    for (var i = 0; i + 1 < tr.points.Count; i++)
                    {
                        var a = new Arc { trace = id, s0 = tr.points[i].s, s1 = tr.points[i + 1].s, node0 = tr.points[i].node, node1 = tr.points[i + 1].node };
                        arcs.Add(a);
                        list.Add(a);
                    }
                    if (tr.endNode < 0) stats.openWalks++;
                }
                var arcIndex = new Dictionary<Arc, int>();
                for (var i = 0; i < arcs.Count; i++) arcIndex[arcs[i]] = i;

                // Resolve every node end to a concrete arc and whether the arc leaves or arrives there.
                var resolved = new List<(int arc, bool leaves, int pos)>[nodes.Count];
                for (var nid = 0; nid < nodes.Count; nid++)
                {
                    var r = resolved[nid] = new List<(int, bool, int)>();
                    foreach (var (code, kind, pos) in nodes[nid].ends)
                    {
                        var id = -1 - code;
                        var list = traceArcs[id];
                        Arc found = null;
                        var leaves = true;
                        foreach (var a in list)
                        {
                            // 0: the trace's start; 1: its end; 2: the arc leaving a node on it; 3: arriving.
                            if ((kind == 0 || kind == 2) && a.node0 == nid) { found = a; leaves = true; break; }
                            if ((kind == 1 || kind == 3) && a.node1 == nid) { found = a; leaves = false; break; }
                        }
                        if (found == null) continue;
                        r.Add((arcIndex[found], leaves, pos));
                    }
                    r.Sort((p, q) => p.pos.CompareTo(q.pos));
                    for (var i = 0; i + 1 < r.Count; i++)
                        if (r[i].pos == r[i + 1].pos && stats.duplicateEnds++ < 4)
                        {
                            var nd = nodes[nid];
                            stats.debug += $"[dup n{nid} v{nd.vertex} val{(nd.vertex >= 0 ? valence[nd.vertex] : 4)}:";
                            foreach (var e in nd.ends)
                            {
                                var t0 = traces[-1 - e.arc];
                                stats.debug += $" tr{-1 - e.arc}({(t0.path.Count > 0 && t0.face == 0 && t0.dir == 0 && t0.steps == 0 ? "crease" : "trace")} kind{e.end} pos{e.pos} start n{t0.startNode})";
                            }
                            stats.debug += "] ";
                        }
                }
                stats.nodes = nodes.Count;
                stats.arcs = arcs.Count;

                // --- faces: walk with the face on the left, turning clockwise at every node -----------
                // Half-edge 2a + 0 runs node0 -> node1, 2a + 1 back.
                var used = new bool[2 * arcs.Count];
                var cornerHist = new SortedDictionary<string, int>();
                rectFaces = new List<(List<int> half, List<int> turns)>();
                var fixes = 0;
                badArcs = new HashSet<int>();
                for (var h0 = 0; h0 < 2 * arcs.Count; h0++)
                {
                    if (used[h0]) continue;
                    var h = h0;
                    var turns = new List<int>();
                    var halfs = new List<int>();
                    var ok = false;
                    for (var guard = 0; guard < 100000; guard++)
                    {
                        used[h] = true;
                        halfs.Add(h);
                        var a = arcs[h / 2];
                        var forward = h % 2 == 0;
                        var at = forward ? a.node1 : a.node0;
                        var r = resolved[at];
                        // The end we arrive through: the arc a, arriving if forward, leaving if not.
                        var k = -1;
                        for (var i = 0; i < r.Count; i++)
                            if (r[i].arc == h / 2 && r[i].leaves == !forward) { k = i; break; }
                        if (k < 0) break;
                        // Next: clockwise from it, i.e. the previous position.
                        var j = (k - 1 + r.Count) % r.Count;
                        var gap = r[k].pos - r[j].pos;
                        if (gap <= 0) gap += nodes[at].quarterTurns;
                        turns.Add(gap);
                        var nh = 2 * r[j].arc + (r[j].leaves ? 0 : 1);
                        h = nh;
                        if (h == h0) { ok = true; break; }
                        if (used[h]) break;
                    }
                    if (!ok) { stats.openWalks++; continue; }
                    stats.layoutFaces++;
                    var corners = 0;
                    var sum = 0;
                    var reflex = false;
                    foreach (var t in turns)
                    {
                        if (t == 1) corners++;
                        if (t >= 3) reflex = true;
                        sum += 2 - t;
                    }
                    if (reflex)
                    {
                        stats.reflexCorners++;
                        if (stats.reflexCorners <= 6)
                        {
                            // The node after each half-edge with a turn of 3 or more.
                            for (var i = 0; i < turns.Count; i++)
                            {
                                if (turns[i] < 3) continue;
                                var ah = arcs[halfs[i] / 2];
                                var at = halfs[i] % 2 == 0 ? ah.node1 : ah.node0;
                                var nd = nodes[at];
                                var tr0 = traces[ah.trace];
                                stats.debug += $"[reflex t{turns[i]} node{at} v{nd.vertex} f{nd.face} val{(nd.vertex >= 0 ? valence[nd.vertex] : 4)} ends:";
                                foreach (var e in resolved[at]) stats.debug += $" a{e.arc}{(e.leaves ? ">" : "<")}@{e.pos}{(traces[arcs[e.arc].trace].active || traces[arcs[e.arc].trace].path.Count == 0 ? "" : "")}";
                                stats.debug += $" raw {nd.ends.Count}] ";
                            }
                        }
                    }
                    if (!(corners == 4 && !reflex && sum == 4)) foreach (var hb in halfs) badArcs.Add(hb / 2);
                    if (corners == 4 && !reflex && sum == 4) { stats.rectangles++; rectFaces.Add((halfs, turns)); }
                    else if (attempt < layoutAttempts)
                    {
                        // One more line into this face, from the first node where its side runs
                        // straight or bends back: a junction's arriving trace carried on through, or a
                        // vertex sending one more along the middle of the angle.
                        for (var i = 0; i < turns.Count; i++)
                        {
                            if (turns[i] < 2) continue;
                            var ah = arcs[halfs[i] / 2];
                            var at = halfs[i] % 2 == 0 ? ah.node1 : ah.node0;
                            var nd = nodes[at];
                            var r = resolved[at];
                            var k = -1;
                            for (var q = 0; q < r.Count; q++)
                                if (r[q].arc == halfs[i] / 2 && r[q].leaves == (halfs[i] % 2 != 0)) { k = q; break; }
                            if (k < 0) continue;
                            var j = (k - 1 + r.Count) % r.Count;
                            if (nd.vertex >= 0)
                            {
                                var want = Mod4Of(r[j].pos + 1, nd.quarterTurns);
                                if (extraEmits.Contains((nd.vertex, want))) continue;
                                extraEmits.Add((nd.vertex, want));
                                fixes++;
                                break;
                            }
                            // A junction: the trace that arrived there (its end, kind 1).
                            var arrived = -1;
                            foreach (var e in nd.ends) if (e.end == 1) arrived = -1 - e.arc;
                            if (arrived < 0 || turns[i] != 2 || continueThrough.Contains(arrived)) continue;
                            continueThrough.Add(arrived);
                            fixes++;
                            break;
                        }
                    }
                    else if (sum == 0)
                    {
                        stats.annuli++;
                        if (attempt >= 1 && stats.annuli <= 1)
                        {
                            stats.debug += $"[annulus a{attempt} len {turns.Count}:";
                            for (var i = 0; i < turns.Count && i < 30; i++)
                            {
                                var ah = arcs[halfs[i] / 2];
                                var at = halfs[i] % 2 == 0 ? ah.node1 : ah.node0;
                                var nd = nodes[at];
                                stats.debug += $" n{at}(v{nd.vertex} t{turns[i]} tr{ah.trace}{(traces[ah.trace].path.Count > 0 && !traces[ah.trace].active ? "" : "")} ends{nd.ends.Count})";
                            }
                            stats.debug += "] ";
                        }
                    }
                    else
                    {
                        stats.otherFaces++;
                        if (stats.otherFaces <= 2)
                        {
                            stats.debug += $"[other c{corners} sum{sum}:";
                            for (var i = 0; i < turns.Count; i++)
                            {
                                var ah = arcs[halfs[i] / 2];
                                var at = halfs[i] % 2 == 0 ? ah.node1 : ah.node0;
                                stats.debug += $" a{halfs[i] / 2}{(halfs[i] % 2 == 0 ? ">" : "<")}n{at}(v{nodes[at].vertex},t{turns[i]},len {ah.s1 - ah.s0:F2},tr{ah.trace}{(traces[ah.trace].active ? "" : "")})";
                            }
                            stats.debug += "] ";
                        }
                    }
                    var key = $"{corners}c/{sum}";
                    cornerHist[key] = cornerHist.TryGetValue(key, out var cnt) ? cnt + 1 : 1;
                }
                foreach (var kv in cornerHist) stats.faceCorners += $"{kv.Key}:{kv.Value} ";
                stats.layoutAttempts = attempt + 1;
                if (fixes == 0) break;
            }

            // --- quantisation -----------------------------------------------------------------
            // An integer length per arc, each rectangle's opposite sides equal, none negative:
            //     min sum (l - L)^2 + mu |C l|^2,
            // C one row per rectangle and direction (bottom minus top, left minus right). Rounded in
            // batches, nearest-integer first, with a solve after each; a length the solve drives
            // below zero is fixed at zero.
            var arcLength = new double[arcs.Count];
            for (var i = 0; i < arcs.Count; i++) arcLength[i] = arcs[i].s1 - arcs[i].s0;
            var rows = new List<List<(int arc, double coef)>>();
            foreach (var (half, turns) in rectFaces)
            {
                // Sides: runs between the corners (turn 1 at the node after a half-edge).
                var count = half.Count;
                var first = 0;
                while (turns[(first + count - 1) % count] != 1) first++;
                var sides = new List<List<int>>();
                var side = new List<int>();
                for (var i = 0; i < count; i++)
                {
                    var k = (first + i) % count;
                    side.Add(half[k] / 2);
                    if (turns[k] == 1) { sides.Add(side); side = new List<int>(); }
                }
                if (sides.Count != 4) continue;
                for (var pair = 0; pair < 2; pair++)
                {
                    var row = new Dictionary<int, double>();
                    foreach (var a in sides[pair]) row[a] = (row.TryGetValue(a, out var c0) ? c0 : 0) + 1;
                    foreach (var a in sides[pair + 2]) row[a] = (row.TryGetValue(a, out var c1) ? c1 : 0) - 1;
                    var list = new List<(int, double)>();
                    foreach (var kv in row) if (kv.Value != 0) list.Add((kv.Key, kv.Value));
                    if (list.Count > 0) rows.Add(list);
                }
            }
            stats.constraintRows = rows.Count;
            // Arc positions for the solver's ordering: the midpoint of its nodes on the surface.
            var nodePos = new Vector3[nodes.Count];
            for (var i = 0; i < nodes.Count; i++)
            {
                var nd = nodes[i];
                if (nd.vertex >= 0) nodePos[i] = topology.positions[nd.vertex];
                else if (nd.face >= 0)
                    nodePos[i] = (topology.positions[T[3 * nd.face]] + topology.positions[T[3 * nd.face + 1]] + topology.positions[T[3 * nd.face + 2]]) / 3f;
            }
            var arcPos = new Vector3[arcs.Count];
            for (var i = 0; i < arcs.Count; i++) arcPos[i] = 0.5f * (nodePos[arcs[i].node0] + nodePos[arcs[i].node1]);
            var ell = Quantize(arcLength, rows, arcPos, stats);
            for (var i = 0; i < arcs.Count; i++)
            {
                stats.quantDeviation += Math.Abs(ell[i] - arcLength[i]);
                if (ell[i] == 0) stats.zeroArcs++;
            }
            var rowCount = new int[arcs.Count];
            foreach (var row in rows) foreach (var (a, _) in row) rowCount[a]++;
            foreach (var row in rows)
            {
                double r = 0;
                foreach (var (a, c) in row) r += c * ell[a];
                if (Math.Abs(r) > 0.5)
                {
                    stats.violatedRows++;
                    if (stats.violatedRows <= 4)
                    {
                        stats.debug += $"[viol r={r}:";
                        foreach (var (a, c) in row) stats.debug += $" {(c > 0 ? "+" : "-")}a{a}={ell[a]}({arcLength[a]:F2},rows {rowCount[a]})";
                        stats.debug += "] ";
                    }
                }
                double r0 = 0;
                foreach (var (a, c) in row) r0 += c * arcLength[a];
                stats.freeResidual = Math.Max(stats.freeResidual, Math.Abs(r0));
            }

            // --- meshing: every rectangle a grid -----------------------------------------------
            // Points: a node each, the inner points of every arc, the inner points of every face.
            // Zero-length arcs and flat faces glue points together (union-find).
            var P = topology.positions;
            var N = topology.normals;
            var pointPos = new List<Vector3>();
            var pointNrm = new List<Vector3>();
            var link = new List<int>();
            int NewPoint(Vector3 p, Vector3 nrm) { pointPos.Add(p); pointNrm.Add(nrm); link.Add(link.Count); return link.Count - 1; }
            int Find(int a) { while (link[a] != a) { link[a] = link[link[a]]; a = link[a]; } return a; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) { link[a] = b; stats.mergedPoints++; } }

            // A point of face f's chart on the surface, and its normal.
            void Surface(int f, double x, double y, out Vector3 p, out Vector3 nrm)
            {
                int c0 = 3 * f, c1 = c0 + 1, c2 = c0 + 2;
                var det = Cross(U(c1) - U(c0), V(c1) - V(c0), U(c2) - U(c0), V(c2) - V(c0));
                double l1 = 0, l2 = 0;
                if (Math.Abs(det) > 1e-300)
                {
                    l1 = Cross(x - U(c0), y - V(c0), U(c2) - U(c0), V(c2) - V(c0)) / det;
                    l2 = Cross(U(c1) - U(c0), V(c1) - V(c0), x - U(c0), y - V(c0)) / det;
                }
                var l0 = 1 - l1 - l2;
                l0 = Math.Max(0, l0); l1 = Math.Max(0, l1); l2 = Math.Max(0, l2);
                var sum = l0 + l1 + l2;
                if (sum <= 0) { l0 = 1; sum = 1; }
                l0 /= sum; l1 /= sum; l2 /= sum;
                p = (float)l0 * P[T[c0]] + (float)l1 * P[T[c1]] + (float)l2 * P[T[c2]];
                nrm = MeshMath.SafeNormalize((float)l0 * N[T[c0]] + (float)l1 * N[T[c1]] + (float)l2 * N[T[c2]], Vector3.up);
            }

            // Where trace `id` is at length s: face, chart point, direction.
            (int face, double x, double y, int dir) Locate(int id, double s)
            {
                var path = traces[id].path;
                if (path.Count == 0) return (-1, 0, 0, 0);
                int lo = 0, hi = path.Count - 1;
                while (lo < hi)
                {
                    var mid = (lo + hi + 1) / 2;
                    if (path[mid].s0 <= s) lo = mid; else hi = mid - 1;
                }
                var sg = path[lo];
                var len = Math.Abs(DirX[sg.dir]) * Math.Abs(sg.bx - sg.ax) + Math.Abs(DirY[sg.dir]) * Math.Abs(sg.by - sg.ay);
                var t = Math.Max(0, Math.Min(len, s - sg.s0));
                var w = len > 0 ? t / len : 0;
                return (sg.face, sg.ax + w * (sg.bx - sg.ax), sg.ay + w * (sg.by - sg.ay), sg.dir);
            }

            // From (f, x, y), straight along direction d for distance dist through the charts.
            // At a corner of f, the triangle of that vertex's fan whose angle holds direction d (the
            // point and direction carried into its chart); unchanged when the point is no corner.
            void IntoSector(ref int f, ref double x, ref double y, ref int d)
            {
                var c = -1;
                for (var k = 0; k < 3; k++)
                    if (Math.Abs(U(3 * f + k) - x) + Math.Abs(V(3 * f + k) - y) < 1e-3) c = 3 * f + k;
                if (c < 0) return;
                for (var step = 0; step < 64; step++)
                {
                    var ff = c / 3;
                    int c1 = 3 * ff + (c % 3 + 1) % 3, c2 = 3 * ff + (c % 3 + 2) % 3;
                    double e1x = U(c1) - U(c), e1y = V(c1) - V(c), e2x = U(c2) - U(c), e2y = V(c2) - V(c);
                    var cr = Cross(e1x, e1y, DirX[d], DirY[d]);
                    var inside = (cr > 0 || (cr == 0 && e1x * DirX[d] + e1y * DirY[d] > 0)) && Cross(DirX[d], DirY[d], e2x, e2y) > 0;
                    if (inside) { f = ff; x = U(c); y = V(c); return; }
                    // Counter-clockwise to the next triangle round the vertex, across edge (c2 -> c).
                    var h = c2;
                    var o = twin[h];
                    if (o < 0) return;
                    d = Mod4(d + rot[h]);
                    c = o;                                   // in the triangle across, the vertex is o's tail
                }
            }

            (int face, double x, double y, int dir) Walk(int f, double x, double y, int d, double dist)
            {
                var crossings = 0;
                IntoSector(ref f, ref x, ref y, ref d);
                for (var guard = 0; guard < 10000 && dist > 1e-12; guard++)
                {
                    if (guard > 0) IntoSector(ref f, ref x, ref y, ref d);
                    double dx = DirX[d], dy = DirY[d];
                    var exit = -1;
                    double qx = 0, qy = 0;
                    for (var k = 0; k < 3; k++)
                    {
                        int a = 3 * f + k, b = 3 * f + (k + 1) % 3;
                        var sa = Cross(dx, dy, U(a) - x, V(a) - y);
                        var sb = Cross(dx, dy, U(b) - x, V(b) - y);
                        if (!(sa <= onLine) || sb <= onLine) continue;
                        var lambda = sa >= 0 ? 0 : -sa / (sb - sa);
                        var ix = U(a) + lambda * (U(b) - U(a));
                        var iy = V(a) + lambda * (V(b) - V(a));
                        if ((ix - x) * dx + (iy - y) * dy < -1e-12) continue;
                        exit = a; qx = ix; qy = iy;
                        break;
                    }
                    if (exit < 0)
                    {
                        // Starting on an edge with the triangle on the wrong side of the ray (a crease
                        // arc walked backwards): across that edge, and again.
                        var crossed = false;
                        for (var k = 0; k < 3 && !crossed; k++)
                        {
                            int a = 3 * f + k, b = 3 * f + (k + 1) % 3;
                            double ex = U(b) - U(a), ey = V(b) - V(a);
                            var el = Math.Sqrt(ex * ex + ey * ey);
                            if (el <= 0) continue;
                            var dist0 = Math.Abs(Cross(ex, ey, x - U(a), y - V(a))) / el;
                            var along = ((x - U(a)) * ex + (y - V(a)) * ey) / (el * el);
                            if (dist0 > 1e-3 || along < -1e-3 || along > 1 + 1e-3) continue;
                            var o2 = twin[a];
                            if (o2 < 0) continue;
                            Rot(rot[a], x, y, out var cx, out var cy);
                            x = cx + tu[a];
                            y = cy + tv[a];
                            d = Mod4(d + rot[a]);
                            f = o2 / 3;
                            crossed = true;
                        }
                        if (crossed && ++crossings <= 4) { stats.walkCrossings++; continue; }
                        stats.walkFailed++;
                        if (stats.walkFailed <= 2)
                            stats.debug += $"[walk f{f} p({x:F4},{y:F4}) d{d} left {dist:F3} tri ({U(3 * f):F4},{V(3 * f):F4}) ({U(3 * f + 1):F4},{V(3 * f + 1):F4}) ({U(3 * f + 2):F4},{V(3 * f + 2):F4})] ";
                        break;
                    }
                    var step = Math.Abs(qx - x) + Math.Abs(qy - y);
                    if (step >= dist) { x += dist * dx; y += dist * dy; dist = 0; break; }
                    dist -= step;
                    var o = twin[exit];
                    if (o < 0) { x = qx; y = qy; stats.walkFailed++; break; }
                    Rot(rot[exit], qx, qy, out var rx, out var ry);
                    x = rx + tu[exit];
                    y = ry + tv[exit];
                    d = Mod4(d + rot[exit]);
                    f = o / 3;
                }
                return (f, x, y, d);
            }

            if (debugArcPath != null)
            {
                // Diagnostics: every arc as a polyline on the surface ("# bad" before the arcs of
                // faces that are not rectangles), every node as a point.
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < arcs.Count; i++)
                {
                    var a = arcs[i];
                    sb.Append(badArcs.Contains(i) ? "# bad " : "# arc ").Append(i).Append(' ').Append(a.trace).Append((char)10);
                    var steps = Math.Max(2, (int)Math.Ceiling((a.s1 - a.s0) * 4));
                    for (var k = 0; k <= steps; k++)
                    {
                        var (f, x, y, _) = Locate(a.trace, a.s0 + (a.s1 - a.s0) * k / steps);
                        if (f < 0) continue;
                        Surface(f, x, y, out var p, out _);
                        sb.Append("p ").Append(p.x.ToString("R", ic)).Append(' ').Append(p.y.ToString("R", ic)).Append(' ').Append(p.z.ToString("R", ic)).Append((char)10);
                    }
                }
                System.IO.File.WriteAllText(debugArcPath, sb.ToString());
            }
            var nodePoint = new int[nodes.Count];
            for (var i = 0; i < nodes.Count; i++)
            {
                var nd = nodes[i];
                Vector3 p, nrm;
                if (nd.vertex >= 0) { p = P[nd.vertex]; nrm = N[nd.vertex]; }
                else if (nd.face >= 0) Surface(nd.face, nd.u, nd.v, out p, out nrm);
                else { p = Vector3.zero; nrm = Vector3.up; }
                nodePoint[i] = NewPoint(p, nrm);
            }
            // Arc points 0..l: the ends are its nodes, the inner ones spread in proportion along it.
            var arcPoints = new int[arcs.Count][];
            for (var i = 0; i < arcs.Count; i++)
            {
                var a = arcs[i];
                var l = (int)Math.Round(ell[i]);
                var pts = arcPoints[i] = new int[l + 1];
                pts[0] = nodePoint[a.node0];
                pts[l] = nodePoint[a.node1];
                if (l == 0) { Union(nodePoint[a.node0], nodePoint[a.node1]); continue; }
                for (var k = 1; k < l; k++)
                {
                    var (f, x, y, _) = Locate(a.trace, a.s0 + (a.s1 - a.s0) * k / l);
                    Surface(f, x, y, out var p, out var nrm);
                    pts[k] = NewPoint(p, nrm);
                }
            }
            // The k-th point along half-edge h (as walked).
            int HalfPoint(int h, int k)
            {
                var pts = arcPoints[h / 2];
                return h % 2 == 0 ? pts[k] : pts[pts.Length - 1 - k];
            }

            var faceQuads = new List<int[]>();
            foreach (var (half, turns) in rectFaces)
            {
                var count = half.Count;
                var first = 0;
                while (turns[(first + count - 1) % count] != 1) first++;
                var sides = new List<List<int>>();
                var side = new List<int>();
                for (var i = 0; i < count; i++)
                {
                    var k = (first + i) % count;
                    side.Add(half[k]);
                    if (turns[k] == 1) { sides.Add(side); side = new List<int>(); }
                }
                if (sides.Count != 4) continue;
                // Each side's points in walking order, and their real positions along it.
                var sidePts = new List<int>[4];
                var sideReal = new List<double>[4];
                for (var sIdx = 0; sIdx < 4; sIdx++)
                {
                    var pts = sidePts[sIdx] = new List<int>();
                    var real = sideReal[sIdx] = new List<double>();
                    double run = 0;
                    foreach (var h in sides[sIdx])
                    {
                        var l = arcPoints[h / 2].Length - 1;
                        var L = arcLength[h / 2];
                        for (var k = pts.Count == 0 ? 0 : 1; k <= l; k++)
                        {
                            pts.Add(HalfPoint(h, k));
                            real.Add(run + (l > 0 ? L * k / l : 0));
                        }
                        run += L;
                    }
                }
                var W = sidePts[0].Count - 1;
                var H = sidePts[1].Count - 1;
                if (W != sidePts[2].Count - 1 || H != sidePts[3].Count - 1) { stats.unequalFaces++; continue; }
                // Grid (i, j): bottom side 0 runs i = 0..W at j = 0; side 1 runs j = 0..H at i = W;
                // side 2 runs back along j = H; side 3 back along i = 0.
                var grid = new int[W + 1, H + 1];
                for (var i = 0; i <= W; i++) { grid[i, 0] = sidePts[0][i]; grid[W - i, H] = sidePts[2][i]; }
                for (var j = 0; j <= H; j++) { grid[W, j] = sidePts[1][j]; grid[0, H - j] = sidePts[3][j]; }
                if (W == 0 || H == 0)
                {
                    // Flat: the two long sides are one line.
                    if (H == 0) for (var i = 0; i <= W; i++) Union(sidePts[0][i], sidePts[2][W - i]);
                    if (W == 0) for (var j = 0; j <= H; j++) Union(sidePts[1][j], sidePts[3][H - j]);
                    stats.flatFaces++;
                    continue;
                }
                // Inner points: Coons blend of the sides' real positions, walked from corner 0 along
                // side 0's direction, then turned a quarter left.
                if (W > 1 && H > 1)
                {
                    var h0 = sides[0][0];
                    var a0 = arcs[h0 / 2];
                    var start = h0 % 2 == 0 ? Locate(a0.trace, a0.s0) : Locate(a0.trace, a0.s1);
                    var d0 = h0 % 2 == 0 ? start.dir : Mod4(start.dir + 2);
                    var Lb = sideReal[0][W];
                    var Lr = sideReal[1][H];
                    for (var i = 1; i < W; i++)
                        for (var j = 1; j < H; j++)
                        {
                            var ub = sideReal[0][i];
                            var ut = Lb - sideReal[2][W - i];
                            var vr = sideReal[1][j];
                            var vl = Lr - sideReal[3][H - j];
                            var su = (double)j / H;
                            var sv = (double)i / W;
                            var u = (1 - su) * ub + su * ut;
                            var v = (1 - sv) * vl + sv * vr;
                            var w1 = Walk(start.face, start.x, start.y, d0, u);
                            var w2 = Walk(w1.face, w1.x, w1.y, Mod4(w1.dir + 1), v);
                            Surface(w2.face, w2.x, w2.y, out var p, out var nrm);
                            grid[i, j] = NewPoint(p, nrm);
                        }
                }
                for (var i = 0; i < W; i++)
                    for (var j = 0; j < H; j++)
                        faceQuads.Add(new[] { grid[i, j], grid[i + 1, j], grid[i + 1, j + 1], grid[i, j + 1] });
            }

            // Glue, compact, and drop quads that glued onto themselves.
            var remap = new int[link.Count];
            for (var i = 0; i < remap.Length; i++) remap[i] = -1;
            var outPos = new List<Vector3>();
            var outNrm = new List<Vector3>();
            quads = new List<int[]>(faceQuads.Count);
            foreach (var q in faceQuads)
            {
                var r = new int[4];
                for (var k = 0; k < 4; k++)
                {
                    var root = Find(q[k]);
                    if (remap[root] < 0) { remap[root] = outPos.Count; outPos.Add(pointPos[root]); outNrm.Add(pointNrm[root]); }
                    r[k] = remap[root];
                }
                if (r[0] == r[1] || r[1] == r[2] || r[2] == r[3] || r[3] == r[0] || r[0] == r[2] || r[1] == r[3]) { stats.degenerateQuads++; continue; }
                quads.Add(r);
            }
            positions = outPos.ToArray();
            normals = outNrm.ToArray();
            stats.quads = quads.Count;

            stats.ms = watch.ElapsedMilliseconds;
            return stats;
        }
    }
}
