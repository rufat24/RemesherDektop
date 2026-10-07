// Port of QuadriFlow's Optimizer::optimize_positions_fixed (src/optimizer.cpp), the stage that runs
// between the integer solve and extraction and that we were missing entirely.
//
// By this point the integer map has decided, for every input edge, how many lattice cells it spans.
// That is a purely combinatorial statement: nothing has yet moved the position field to agree with
// it. Extraction then averages the raw position-field samples inside each cell, so the output vertex
// lands wherever the smoothing happened to leave them -- close to right on a uniform mesh, visibly
// off anywhere the input was uneven.
//
// This solve closes that gap. It asks for the one thing the map asserts and the field does not yet
// satisfy: that the displacement between two neighbouring cells equals their integer offset times
// the lattice spacing, measured in the local cross frame. Two unknowns per cell (an in-plane offset
// from a representative input vertex), one residual per adjacent cell pair, solved globally in least
// squares. The reference factorises it directly; we run conjugate gradient warm-started from the
// current positions, which is the variant left commented out in the reference beside the direct one.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public partial class IntegerGridMap
    {
        /// <summary>
        /// Strength of the pull toward the field's own position, as a fraction of each unknown's
        /// own diagonal. See the tether in <see cref="OptimizePositionsFixed"/>.
        /// </summary>
        public static double tetherWeight = 0.02;

        /// <summary>Conjugate gradient iterations of the fixed position solve; 40 is the reference's.</summary>
        public static int positionSolveIterations = 40;

        /// <summary>Whether the global position least squares runs at all.</summary>
        public static bool optimizePositions = true;

        /// <summary>
        /// Moves the position field onto the lattice the integer map describes. Runs after the flow,
        /// the flip repair and subdivision have fixed the offsets, and before extraction reads them.
        /// </summary>
        /// <param name="sharpConstraints">
        /// From <see cref="OptimizePositionsSharp"/>, or null. A constrained cell is anchored on its
        /// crease, loses the degree of freedom across the crease, and a corner loses both.
        /// </param>
        /// <param name="iterations">
        /// Conjugate gradient iterations. The reference's own iterative variant uses 40; the system
        /// is well conditioned because it is a Laplacian on the cell graph, so this converges fast.
        /// </param>
        public void OptimizePositionsFixed(Dictionary<int, SharpConstraint> sharpConstraints = null,
            int iterations = 40)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // Cells are what extraction will build vertices from, so the solve has to be posed on
            // exactly the same partition: every zero-offset edge glues its endpoints together.
            var tree = new DisjointTree(V.Count);
            for (var e = 0; e < edgeCount; e++)
                if (edgeDiff[e] == Vector2Int.zero)
                    tree.Merge(edgeX[e], edgeY[e]);
            tree.BuildCompactParent();

            var num = tree.compactCount;
            if (num == 0) return;

            // Each cell is anchored to the input vertex nearest its centre rather than to the centre
            // itself, so the offsets stay small and the frame (Q, N) used to express them is one the
            // surface actually has at that point.
            var cellCentre = new Vector3[num];
            var cellCount = new int[num];
            for (var i = 0; i < V.Count; i++)
            {
                var p = tree.Index(i);
                cellCentre[p] += O[i];
                cellCount[p]++;
            }
            for (var p = 0; p < num; p++)
                if (cellCount[p] > 0) cellCentre[p] /= cellCount[p];

            var anchor = new int[num];
            var anchorDistance = new float[num];
            for (var p = 0; p < num; p++)
            {
                anchor[p] = -1;
                anchorDistance[p] = float.MaxValue;
            }
            for (var i = 0; i < V.Count; i++)
            {
                var p = tree.Index(i);
                var d = (cellCentre[p] - V[i]).sqrMagnitude;
                if (d >= anchorDistance[p]) continue;
                anchorDistance[p] = d;
                anchor[p] = i;
            }

            // Where each cell's offsets are measured from. Normally the anchor vertex itself; a cell
            // on a crease is instead anchored on the crease point the sharp stage moved it to, so
            // that an offset of zero across the crease means exactly "on the line". The reference
            // gets the same effect by overwriting the input positions of crease vertices.
            var anchorPos = new Vector3[num];
            for (var p = 0; p < num; p++)
                if (anchor[p] >= 0) anchorPos[p] = V[anchor[p]];

            var constrained = new SharpConstraint?[num];
            if (sharpConstraints != null && sharpConstraints.Count > 0)
            {
                var keys = new List<int>(sharpConstraints.Keys);
                keys.Sort();
                foreach (var v in keys)
                {
                    var p = tree.Index(v);
                    anchor[p] = v;
                    anchorPos[p] = O[v];
                    cellCentre[p] = O[v];
                    constrained[p] = sharpConstraints[v];
                }
            }

            // The frame a cell's two unknowns are expressed in. On a crease the first axis IS the
            // crease, so fixing the second pins the cell to the line and leaves it free to slide.
            void Frame(int p, out Vector3 q, out Vector3 qy)
            {
                var a = anchor[p];
                q = Q[a];
                if (constrained[p].HasValue && !constrained[p].Value.isCorner)
                    q = constrained[p].Value.direction;
                qy = Vector3.Cross(N[a], q);
            }

            // The displacement each adjacent cell pair should have, averaged over every input edge
            // that joins them -- a coarse cell can be reached by several.
            var ideal = new Dictionary<int, (int count, Vector3 sum)>[num];

            for (var e = 0; e < edgeCount; e++)
            {
                var v1 = edgeX[e];
                var v2 = edgeY[e];
                var p1 = tree.Index(v1);
                var p2 = tree.Index(v2);
                if (p1 == p2) continue;

                var a1 = anchor[p1];
                var a2 = anchor[p2];
                if (a1 < 0 || a2 < 0) continue;

                var c = LatticeStep(v1, v2, edgeDiff[e]) + anchorPos[p1] - anchorPos[p2];

                var row = ideal[p1] ??= new Dictionary<int, (int, Vector3)>();
                if (row.TryGetValue(p2, out var existing))
                    row[p2] = (existing.count + 1, existing.sum + c);
                else
                    row[p2] = (1, c);
            }

            // --- normal equations ---
            //
            // Unknowns are (x, y) per cell: the offset from its anchor along that anchor's cross and
            // its in-plane perpendicular. One residual per cell pair,
            //     (x2 q2 + y2 q2y) - (x1 q1 + y1 q1y) = ideal,
            // accumulated straight into A = sum of w w^T and b = sum of w . ideal, as the reference
            // assembles it. Each 4x4 block is a Gram matrix, so A comes out symmetric positive
            // semi-definite and conjugate gradient applies.
            var rows = new Dictionary<int, double>[2 * num];
            var b = new double[2 * num];

            for (var m = 0; m < num; m++)
            {
                var row = ideal[m];
                if (row == null) continue;

                if (anchor[m] < 0) continue;
                Frame(m, out var q1, out var q1y);

                foreach (var pair in row)
                {
                    if (anchor[pair.Key] < 0) continue;
                    Frame(pair.Key, out var q2, out var q2y);

                    var target = pair.Value.sum / pair.Value.count;

                    var w0 = q2;
                    var w1 = q2y;
                    var w2 = -q1;
                    var w3 = -q1y;

                    var wi = new[] { w0, w1, w2, w3 };
                    var id = new[] { 2 * pair.Key, 2 * pair.Key + 1, 2 * m, 2 * m + 1 };

                    for (var i = 0; i < 4; i++)
                    {
                        var dst = rows[id[i]] ??= new Dictionary<int, double>();
                        for (var j = 0; j < 4; j++)
                        {
                            var w = (double)Vector3.Dot(wi[i], wi[j]);
                            dst[id[j]] = dst.TryGetValue(id[j], out var had) ? had + w : w;
                        }
                        b[id[i]] += Vector3.Dot(wi[i], target);
                    }
                }
            }

            // A cell no edge reached keeps whatever it has: an identity row leaves it at its warm
            // start rather than dragging it to the origin.
            for (var i = 0; i < rows.Length; i++)
            {
                if (rows[i] != null && rows[i].Count != 0) continue;
                rows[i] = new Dictionary<int, double> { { i, 1.0 } };
                b[i] = 0.0;
            }

            var x = new double[2 * num];
            for (var p = 0; p < num; p++)
            {
                if (anchor[p] < 0) continue;
                Frame(p, out var q, out var qy);
                var d = cellCentre[p] - anchorPos[p];
                x[2 * p] = Vector3.Dot(d, q);
                x[2 * p + 1] = Vector3.Dot(d, qy);
            }

            // --- tether ---
            //
            // The residuals only relate neighbouring cells, so anything that moves every cell the
            // same way costs nothing. On a curved surface nothing does -- the tangent frames turn,
            // and a shift that suits one cell does not suit its neighbour. On a flat patch they all
            // lie in one plane, a sideways shift of the whole grid satisfies every residual at once,
            // and the least squares has a free direction. Any inconsistency in the targets then
            // pushes conjugate gradient along it: measured on the open disc, the solve moved every
            // vertex two lattice cells on average, the rim slid off itself, and Hausdorff error went
            // from 0.0009 to 0.10. A weak pull of each cell toward where the field already put it
            // removes that freedom and is negligible wherever the system was determined anyway.
            for (var i = 0; i < rows.Length; i++)
            {
                // A lone identity row is a cell no edge reached; it is already held where it is.
                if (anchor[i / 2] < 0 || rows[i].Count <= 1) continue;
                var diagonal = 0.0;
                rows[i].TryGetValue(i, out diagonal);
                var weight = tetherWeight * Math.Max(diagonal, 1.0);
                rows[i][i] = diagonal + weight;
                b[i] += weight * x[i];
            }

            // --- crease constraints ---
            //
            // A crease cell keeps its offset across the crease at its warm start (zero: it is
            // anchored on the line), and a corner keeps both. Their rows become identities and their
            // columns move to the right-hand side of every other row, exactly as the reference
            // eliminates them, which keeps the system symmetric for conjugate gradient.
            var fixedDim = new bool[2 * num];
            for (var p = 0; p < num; p++)
            {
                if (!constrained[p].HasValue) continue;
                fixedDim[2 * p + 1] = true;
                if (constrained[p].Value.isCorner) fixedDim[2 * p] = true;
            }

            for (var i = 0; i < rows.Length; i++)
            {
                if (fixedDim[i])
                {
                    rows[i] = new Dictionary<int, double> { { i, 1.0 } };
                    b[i] = x[i];
                    continue;
                }

                List<int> drop = null;
                foreach (var entry in rows[i])
                {
                    if (!fixedDim[entry.Key]) continue;
                    b[i] -= entry.Value * x[entry.Key];
                    (drop ??= new List<int>()).Add(entry.Key);
                }
                if (drop != null)
                    foreach (var column in drop)
                        rows[i].Remove(column);

                if (rows[i].Count == 0) rows[i][i] = 1.0;
            }

            // The identity rows were given b = 0, which would pull those cells to their anchor.
            // Keep them where they started instead.
            for (var i = 0; i < rows.Length; i++)
                if (rows[i].Count == 1 && rows[i].ContainsKey(i) && rows[i][i] == 1.0)
                    b[i] = x[i];

            var (rowStart, columns, values) = Compress(rows);
            stats.positionUnknowns = 2 * num;
            stats.positionNonZeros = values.Length;
            stats.positionIterations = SolveConjugateGradient(rowStart, columns, values, b, x,
                iterations == 40 ? positionSolveIterations : iterations);

            // The solved offset lives in the anchor's tangent plane, which is only the surface to
            // first order. Where a cell covers many input vertices -- a coarse target on a dense
            // scan -- the offset is large enough for that approximation to leave the surface, and
            // lattice regularity gets bought with fidelity. The reference does not accept that
            // trade: its optimize_positions_dynamic walks from the anchor to whichever input vertex
            // is actually nearest the solved point and re-projects onto the tangent plane THERE.
            // Without this, measured on a head scan at 5000 quads, Hausdorff error goes the wrong
            // way (0.0265 -> 0.0360) even though the topology improves.
            var adjacency = BuildVertexAdjacency();
            var placed = new Vector3[num];
            var projected = 0f;

            for (var p = 0; p < num; p++)
            {
                var a = anchor[p];
                if (a < 0) continue;

                Frame(p, out var q, out var qy);
                placed[p] = anchorPos[p] + q * (float)x[2 * p] + qy * (float)x[2 * p + 1];

                // A crease cell was solved on the crease's TANGENT, which leaves a curved crease as
                // soon as the cell slides: the error grows with the square of the slide. Measured on
                // the open disc, whose rim is one crease loop, cells sliding about a cell along
                // their tangents came out 0.005-0.008 inside the rim. So finish on the crease
                // polyline itself -- and never lift along a normal, which would pull it off again.
                if (constrained[p].HasValue)
                {
                    if (!constrained[p].Value.isCorner)
                    {
                        var onCrease = constrained[p].Value.Project(placed[p]);
                        projected += (onCrease - placed[p]).magnitude;
                        placed[p] = onCrease;
                    }
                    continue;
                }

                // The solve has two unknowns per cell and both lie in the anchor's tangent plane, so
                // it has nothing to say about the normal direction -- yet writing its result out as
                // it stands silently drops the normal offset the smoothed field had found, flattening
                // every cell onto its anchor's tangent plane. On a dense scan a cell spans twenty-odd
                // input vertices and that flattening is a real geometric error: measured on a head at
                // 5000 quads it took Hausdorff from 0.0265 to 0.0316 while leaving the topology bit
                // for bit identical, which is the signature of a change that is pure loss.
                //
                // So take only what the solve is entitled to decide -- the in-plane position -- and
                // restore the offset along the normal, measured at whichever input vertex the solved
                // point actually landed nearest rather than at the anchor it started from.
                var host = NearestHost(adjacency, a, placed[p]);
                var before = placed[p];
                var n = N[host];
                var lift = Vector3.Dot(cellCentre[p] - V[host], n);
                placed[p] += (lift - Vector3.Dot(placed[p] - V[host], n)) * n;
                projected += (placed[p] - before).magnitude;
            }

            var moved = 0f;
            for (var i = 0; i < O.Count; i++)
            {
                var p = tree.Index(i);
                if (anchor[p] < 0) continue;
                moved += (placed[p] - O[i]).magnitude;
                O[i] = placed[p];
            }

            stats.positionMeanMove = O.Count > 0 ? moved / O.Count : 0f;
            stats.positionMeanProject = num > 0 ? projected / num : 0f;
            stats.positionMs = watch.ElapsedMilliseconds;
        }

        /// <summary>
        /// The 3D displacement an edge should have, given its integer offset: the offset's two
        /// components along the averaged cross frame of the edge's endpoints, each scaled by the
        /// lattice spacing there.
        /// </summary>
        Vector3 LatticeStep(int v1, int v2, Vector2Int diff)
        {
            var q1 = Q[v1];
            var n1 = N[v1];
            var q2 = Q[v2];
            var n2 = N[v2];
            var q1y = Vector3.Cross(n1, q1);
            var q2y = Vector3.Cross(n2, q2);

            SingularityAnalysis.Compat4Index(q1, n1, q2, n2, out var k1, out var k2);
            var rank = Mod4(k2 - k1);

            // The two frames are combed into one before averaging, or the mean of two crosses a
            // quarter turn apart would cancel.
            var qx = 0.5f * (Rotate90By(q2, n2, rank) + q1);
            var qy = 0.5f * (Rotate90By(q2y, n2, rank) + q1y);

            // ScaleAt already returns an absolute lattice spacing -- S is a per-vertex length here,
            // not the reference's dimensionless factor that gets multiplied by a global scale, so
            // multiplying by `scale` again would shrink every target by the spacing itself.
            // Spacing is also isotropic, so the reference's swap of the two components on an odd
            // quarter turn has nothing to swap.
            var step = 0.5f * (ScaleAt(v1) + ScaleAt(v2));

            return diff.x * step * qx + diff.y * step * qy;
        }

        /// <summary>One-ring neighbours of every input vertex, as a flat CSR pair.</summary>
        (int[] start, int[] neighbours) BuildVertexAdjacency()
        {
            var n = V.Count;
            var degree = new int[n + 1];
            for (var h = 0; h < F.Count; h++) degree[F[h] + 1]++;

            var start = new int[n + 1];
            for (var i = 0; i < n; i++) start[i + 1] = start[i] + degree[i + 1];

            var fill = new int[n];
            var neighbours = new int[F.Count];
            for (var h = 0; h < F.Count; h++)
            {
                var v = F[h];
                neighbours[start[v] + fill[v]++] = F[Next3(h)];
            }

            return (start, neighbours);
        }

        /// <summary>
        /// Greedy descent from <paramref name="from"/> to the input vertex nearest
        /// <paramref name="target"/>, refusing any step that turns more than ten degrees so the walk
        /// cannot cross a crease onto the far side of a thin feature. Distance strictly decreases,
        /// so it terminates.
        /// </summary>
        int NearestHost((int[] start, int[] neighbours) adjacency, int from, Vector3 target)
        {
            const float k_MinDot = 0.9848078f; // cos 10 degrees, as the reference uses.

            var current = from;
            var normal = N[from];
            var best = (target - V[current]).sqrMagnitude;

            while (true)
            {
                var next = -1;
                for (var k = adjacency.start[current]; k < adjacency.start[current + 1]; k++)
                {
                    var v = adjacency.neighbours[k];
                    if (Vector3.Dot(N[v], normal) < k_MinDot) continue;
                    var d = (target - V[v]).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    next = v;
                }

                if (next == -1) return current;
                current = next;
            }
        }

        static (int[] rowStart, int[] columns, double[] values) Compress(Dictionary<int, double>[] rows)
        {
            var n = rows.Length;
            var rowStart = new int[n + 1];
            for (var i = 0; i < n; i++) rowStart[i + 1] = rowStart[i] + rows[i].Count;

            var columns = new int[rowStart[n]];
            var values = new double[rowStart[n]];
            for (var i = 0; i < n; i++)
            {
                var k = rowStart[i];
                foreach (var entry in rows[i])
                {
                    columns[k] = entry.Key;
                    values[k] = entry.Value;
                    k++;
                }
            }

            return (rowStart, columns, values);
        }

        static void Multiply(int[] rowStart, int[] columns, double[] values, double[] v, double[] result)
        {
            for (var i = 0; i < result.Length; i++)
            {
                var sum = 0.0;
                var end = rowStart[i + 1];
                for (var k = rowStart[i]; k < end; k++) sum += values[k] * v[columns[k]];
                result[i] = sum;
            }
        }

        /// <summary>
        /// Conjugate gradient on a symmetric positive semi-definite system, warm-started from
        /// <paramref name="x"/>. Bails out rather than dividing by a non-positive curvature, which
        /// is what a rank-deficient block -- a cell whose frame is degenerate -- would produce.
        /// </summary>
        static int SolveConjugateGradient(int[] rowStart, int[] columns, double[] values,
            double[] b, double[] x, int iterations)
        {
            var n = x.Length;
            var r = new double[n];
            var p = new double[n];
            var ap = new double[n];

            Multiply(rowStart, columns, values, x, r);

            var rsOld = 0.0;
            for (var i = 0; i < n; i++)
            {
                r[i] = b[i] - r[i];
                p[i] = r[i];
                rsOld += r[i] * r[i];
            }

            var norm = 0.0;
            for (var i = 0; i < n; i++) norm += b[i] * b[i];
            var tolerance = Math.Max(1e-20, norm * 1e-12);

            var done = 0;
            for (var it = 0; it < iterations && rsOld > tolerance; it++)
            {
                Multiply(rowStart, columns, values, p, ap);

                var curvature = 0.0;
                for (var i = 0; i < n; i++) curvature += p[i] * ap[i];
                if (!(curvature > 0.0) || double.IsNaN(curvature)) break;

                var alpha = rsOld / curvature;
                var rsNew = 0.0;
                for (var i = 0; i < n; i++)
                {
                    x[i] += alpha * p[i];
                    r[i] -= alpha * ap[i];
                    rsNew += r[i] * r[i];
                }

                var beta = rsNew / rsOld;
                for (var i = 0; i < n; i++) p[i] = r[i] + beta * p[i];
                rsOld = rsNew;
                done = it + 1;
            }

            for (var i = 0; i < n; i++)
                if (double.IsNaN(x[i]) || double.IsInfinity(x[i]))
                    x[i] = 0.0;

            return done;
        }
    }
}
