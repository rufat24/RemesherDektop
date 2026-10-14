// The smoothest cross field, solved globally rather than by local sweeps.
//
// From Knöppel, Crane, Pinkall and Schröder, "Globally Optimal Direction Fields" (SIGGRAPH 2013),
// and the vertex-based connection Laplacian of Ray, Vallet, Li and Lévy, "N-Symmetry Direction
// Field Design" (2008).
//
// WHY. OrientationField smooths by Gauss-Seidel sweeps, each vertex averaged with its neighbours
// under Compat4. That is a nonlinear local descent, and on a scan or a detailed sculpt it stops in
// local minima: every pair of a +1/4 and a -1/4 singularity a cell or two apart is one. Measured on
// the helmet head the field arrives at extraction with 550-980 singular triangles whatever the sweep
// budget or hierarchy setting, and every one of those pairs is a valence 3 vertex next to a valence 5
// with a band of sheared quads between them -- the "wobble" in the output's edge loops. More sweeps
// cannot remove them because they are minima of the energy being descended.
//
// Written as one complex number per vertex, z = exp(4i theta) in the vertex's own tangent frame, the
// 4-fold symmetry disappears and the same smoothness energy becomes quadratic:
//
//     E(z) = sum over edges |z_j - r_ij z_i|^2,   r_ij = exp(4i phi_ij)
//
// with phi_ij the angle a tangent vector turns by when carried from i's frame to j's. Its minimiser
// subject to the feature locks is one sparse Hermitian linear solve, and it has no local minima to
// stop in: singularities sit only where the surface's own turning or the features demand them.
// The direction is read back as arg(z) / 4.
//
// Hard locks are eliminated as known values. Soft constraints and guides become a data term
// lambda |z - c|^2 at the vertex, weighted the way OrientationField weighs them (a multiple of the
// vertex's degree). With nothing at all to align to, the minimiser is the zero field, so the
// smoothest unit field is taken instead as the lowest eigenvector, by inverse iteration.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public sealed class GlobalFieldStats
    {
        public int free;
        public int locked;
        public int guided;
        public int cgIterations;
        public float residual;
        public bool eigen;
        public long ms;

        public override string ToString() =>
            $"free={free} locked={locked} guided={guided} cg={cgIterations} res={residual:G3} eigen={eigen} ms={ms}";
    }

    public static class GlobalCrossField
    {
        /// <summary>
        /// Solves the smoothest 4-RoSy field on <paramref name="topology"/> against its current
        /// normals, honouring hard locks (weight 1 or more), soft constraints and guides. Returns
        /// one unit tangent direction per vertex.
        /// </summary>
        public static Vector3[] Solve(RemeshTopology topology,
            Vector3[] constraintDirections, float[] constraintWeights,
            Vector3[] guideDirections, float[] guideWeights,
            int maxIterations, float tolerance, out GlobalFieldStats stats,
            float softenLocks = 0f)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats = new GlobalFieldStats();

            var n = topology.vertexCount;
            var normals = topology.normals;
            var e1 = new Vector3[n];
            var e2 = new Vector3[n];
            for (var i = 0; i < n; i++)
            {
                e1[i] = OrientationField.ArbitraryTangent(normals[i]);
                e2[i] = Vector3.Cross(normals[i], e1[i]);
            }

            // Symmetric neighbour lists. The topology's own adjacency is not symmetric on every
            // input -- ccnb's open, non-manifold scan has thousands of one-way pairs -- and a
            // conjugate-gradient solve needs a Hermitian matrix.
            BuildSymmetric(topology, out var start, out var nbr);

            // r[k] carries a neighbour's value into this vertex's frame: exp(4i phi_ji).
            var rRe = new double[nbr.Length];
            var rIm = new double[nbr.Length];
            System.Threading.Tasks.Parallel.For(0, n, BackgroundScheduler.Options, i =>
            {
                for (var k = start[i]; k < start[i + 1]; k++)
                {
                    var j = nbr[k];
                    var carried = Transport(e1[j], normals[j], normals[i]);
                    var phi = Math.Atan2(Vector3.Dot(carried, e2[i]), Vector3.Dot(carried, e1[i]));
                    rRe[k] = Math.Cos(4.0 * phi);
                    rIm[k] = Math.Sin(4.0 * phi);
                }
            });

            // Per vertex: locked (known value), data weight and target.
            var locked = new bool[n];
            var lambda = new double[n];
            var cRe = new double[n];
            var cIm = new double[n];
            var hasConstraints = constraintDirections != null && constraintWeights != null
                                 && constraintDirections.Length == n && constraintWeights.Length == n;
            var hasGuides = guideDirections != null && guideWeights != null
                            && guideDirections.Length == n && guideWeights.Length == n;
            for (var i = 0; i < n; i++)
            {
                var degree = start[i + 1] - start[i];
                Vector3 d;
                float w;
                if (hasConstraints && constraintWeights[i] > 0f
                                   && constraintDirections[i].sqrMagnitude > 1e-12f)
                {
                    d = constraintDirections[i];
                    w = constraintWeights[i];
                }
                else if (hasGuides && guideWeights[i] > 0f && guideDirections[i].sqrMagnitude > 1e-12f)
                {
                    d = guideDirections[i];
                    w = guideWeights[i];
                    stats.guided++;
                }
                else continue;

                var theta = Math.Atan2(Vector3.Dot(d, e2[i]), Vector3.Dot(d, e1[i]));
                if (double.IsNaN(theta)) continue;
                cRe[i] = Math.Cos(4.0 * theta);
                cIm[i] = Math.Sin(4.0 * theta);
                // A positive softenLocks turns every hard lock into a data term of that weight:
                // a crease traced on a scan is noisy, and holding the field to it exactly is what
                // winds singularities round it.
                if (softenLocks > 0f && w >= 1f) w = softenLocks;
                if (w >= 1f && hasConstraints && constraintWeights[i] >= 1f)
                {
                    locked[i] = true;
                    stats.locked++;
                }
                else lambda[i] = w * Math.Max(1, degree);
            }

            var zRe = new double[n];
            var zIm = new double[n];
            for (var i = 0; i < n; i++)
                if (locked[i]) { zRe[i] = cRe[i]; zIm[i] = cIm[i]; }

            var anyData = stats.locked > 0;
            for (var i = 0; i < n && !anyData; i++) anyData = lambda[i] > 0;
            stats.free = n - stats.locked;

            if (anyData)
            {
                // b = lambda c + what the locked neighbours contribute.
                var bRe = new double[n];
                var bIm = new double[n];
                for (var i = 0; i < n; i++)
                {
                    if (locked[i]) continue;
                    double re = lambda[i] * cRe[i], im = lambda[i] * cIm[i];
                    for (var k = start[i]; k < start[i + 1]; k++)
                    {
                        var j = nbr[k];
                        if (!locked[j]) continue;
                        re += rRe[k] * zRe[j] - rIm[k] * zIm[j];
                        im += rRe[k] * zIm[j] + rIm[k] * zRe[j];
                    }
                    bRe[i] = re;
                    bIm[i] = im;
                }
                stats.cgIterations = ConjugateGradient(n, start, nbr, rRe, rIm, lambda, locked, 0.0,
                    bRe, bIm, zRe, zIm, maxIterations, tolerance, out stats.residual);
            }
            else
            {
                // Inverse iteration for the smallest eigenvector, shifted slightly so the system
                // stays definite on a closed surface whose connection happens to be flat.
                stats.eigen = true;
                for (var i = 0; i < n; i++) { zRe[i] = 1.0; zIm[i] = 0.0; }
                var shift = 1e-6;
                var bRe = new double[n];
                var bIm = new double[n];
                for (var round = 0; round < 6; round++)
                {
                    Normalise(zRe, zIm);
                    Array.Copy(zRe, bRe, n);
                    Array.Copy(zIm, bIm, n);
                    stats.cgIterations += ConjugateGradient(n, start, nbr, rRe, rIm, lambda, locked,
                        shift, bRe, bIm, zRe, zIm, maxIterations, tolerance, out stats.residual);
                }
            }

            var directions = new Vector3[n];
            for (var i = 0; i < n; i++)
            {
                var theta = 0.25 * Math.Atan2(zIm[i], zRe[i]);
                if (double.IsNaN(theta)) theta = 0.0;
                var d = e1[i] * (float)Math.Cos(theta) + e2[i] * (float)Math.Sin(theta);
                directions[i] = MeshMath.SafeNormalize(d, e1[i]);
            }

            stats.ms = watch.ElapsedMilliseconds;
            return directions;
        }

        /// <summary>
        /// Cancels pairs of opposite singularities closer than <paramref name="pairDistance"/>:
        /// the field is re-solved, smoothest, inside a disk round each pair with the disk's rim
        /// held. Returns how many singular faces there were before and after.
        /// </summary>
        /// <remarks>
        /// A +1/4 and a -1/4 singularity a cell apart are a valence 3 and a valence 5 vertex in the
        /// output with a row of sheared quads between them -- the shear bands. Neither is demanded
        /// by the surface: the winding round a disk containing both is zero, so a field with no
        /// singularity in it exists, and the smoothest field under that rim is it (or close to it).
        /// Local descent cannot get there because separating the pair costs energy first.
        /// All disks are solved together, as one system whose unknowns are their interiors; hard
        /// locks stay locked.
        /// </remarks>
        public static void CancelPairs(RemeshTopology topology, Vector3[] directions,
            float[] constraintWeights, float pairDistance, float rimMargin, int maxIterations,
            out int before, out int after, out int pairs)
        {
            var n = topology.vertexCount;
            var positions = topology.positions;
            var normals = topology.normals;
            var triangles = topology.triangles;
            pairs = 0;

            var sign = Singular(topology, directions, out before);
            after = before;
            if (before == 0 || !(pairDistance > 0f)) return;

            // Centroids of singular faces, split by sign.
            var plus = new List<int>();
            var minus = new List<int>();
            for (var f = 0; f < sign.Length; f++)
                if (sign[f] == 1) plus.Add(f);
                else if (sign[f] == 3) minus.Add(f);
            Vector3 Centre(int f) =>
                (positions[triangles[3 * f]] + positions[triangles[3 * f + 1]] + positions[triangles[3 * f + 2]]) / 3f;

            var cell = Mathf.Max(pairDistance, 1e-9f);
            var hash = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Key(Vector3 p) =>
                (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
            foreach (var f in minus)
            {
                var key = Key(Centre(f));
                if (!hash.TryGetValue(key, out var list)) hash[key] = list = new List<int>();
                list.Add(f);
            }
            var candidates = new List<(float d, int a, int b)>();
            foreach (var f in plus)
            {
                var c = Centre(f);
                var (x, y, z) = Key(c);
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!hash.TryGetValue((x + dx, y + dy, z + dz), out var list)) continue;
                    foreach (var g in list)
                    {
                        var d = (Centre(g) - c).magnitude;
                        if (d < pairDistance) candidates.Add((d, f, g));
                    }
                }
            }
            candidates.Sort((p, q) => p.d.CompareTo(q.d));

            // Disk per accepted pair: vertices reachable from the pair's corners without leaving
            // a ball round the midpoint, so a disk never jumps to another sheet.
            var used = new HashSet<int>();
            var free = new bool[n];
            var stamp = new int[n];
            var queue = new Queue<int>();
            foreach (var (d, a, b) in candidates)
            {
                if (used.Contains(a) || used.Contains(b)) continue;
                used.Add(a);
                used.Add(b);
                pairs++;
                var mid = (Centre(a) + Centre(b)) * 0.5f;
                var radius = d * 0.5f + rimMargin;
                var r2 = radius * radius;
                queue.Clear();
                for (var k = 0; k < 3; k++)
                {
                    var v = triangles[3 * a + k];
                    if (stamp[v] != pairs) { stamp[v] = pairs; queue.Enqueue(v); }
                }
                while (queue.Count > 0)
                {
                    var v = queue.Dequeue();
                    free[v] = true;
                    for (var k = topology.adjacencyStart[v]; k < topology.adjacencyStart[v + 1]; k++)
                    {
                        var w = topology.adjacency[k];
                        if (stamp[w] == pairs || (positions[w] - mid).sqrMagnitude > r2) continue;
                        stamp[w] = pairs;
                        queue.Enqueue(w);
                    }
                }
            }
            if (pairs == 0) return;

            BuildSymmetric(topology, out var start, out var nbr);
            // The rim of every disk is held: a free vertex with a neighbour outside stays free, the
            // outside neighbour is the boundary value. Hard locks are never freed.
            var locked = new bool[n];
            for (var i = 0; i < n; i++)
                locked[i] = !free[i] || (constraintWeights != null && i < constraintWeights.Length
                                                                   && constraintWeights[i] >= 1f);

            var e1 = new Vector3[n];
            var e2 = new Vector3[n];
            var zRe = new double[n];
            var zIm = new double[n];
            for (var i = 0; i < n; i++)
            {
                e1[i] = OrientationField.ArbitraryTangent(normals[i]);
                e2[i] = Vector3.Cross(normals[i], e1[i]);
                var theta = Math.Atan2(Vector3.Dot(directions[i], e2[i]), Vector3.Dot(directions[i], e1[i]));
                zRe[i] = Math.Cos(4.0 * theta);
                zIm[i] = Math.Sin(4.0 * theta);
            }
            var rRe = new double[nbr.Length];
            var rIm = new double[nbr.Length];
            var bRe = new double[n];
            var bIm = new double[n];
            for (var i = 0; i < n; i++)
            {
                if (locked[i]) continue;
                double re = 0, im = 0;
                for (var k = start[i]; k < start[i + 1]; k++)
                {
                    var j = nbr[k];
                    var carried = Transport(e1[j], normals[j], normals[i]);
                    var phi = Math.Atan2(Vector3.Dot(carried, e2[i]), Vector3.Dot(carried, e1[i]));
                    rRe[k] = Math.Cos(4.0 * phi);
                    rIm[k] = Math.Sin(4.0 * phi);
                    if (!locked[j]) continue;
                    re += rRe[k] * zRe[j] - rIm[k] * zIm[j];
                    im += rRe[k] * zIm[j] + rIm[k] * zRe[j];
                }
                bRe[i] = re;
                bIm[i] = im;
            }
            ConjugateGradient(n, start, nbr, rRe, rIm, new double[n], locked, 0.0, bRe, bIm, zRe, zIm,
                maxIterations, 1e-6f, out _);

            for (var i = 0; i < n; i++)
            {
                if (locked[i]) continue;
                var theta = 0.25 * Math.Atan2(zIm[i], zRe[i]);
                if (double.IsNaN(theta)) continue;
                var d = e1[i] * (float)Math.Cos(theta) + e2[i] * (float)Math.Sin(theta);
                directions[i] = MeshMath.SafeNormalize(d, directions[i]);
            }

            Singular(topology, directions, out after);
        }

        /// <summary>Per face, its index mod 4 (1 or 3 when singular, else 0), and the count.</summary>
        public static int[] Singular(RemeshTopology topology, Vector3[] q, out int count)
        {
            var triangles = topology.triangles;
            var normals = topology.normals;
            var faces = triangles.Length / 3;
            var result = new int[faces];
            var total = 0;
            for (var f = 0; f < faces; f++)
            {
                var index = 0;
                for (var k = 0; k < 3; k++)
                {
                    var i = triangles[3 * f + k];
                    var j = triangles[3 * f + (k == 2 ? 0 : k + 1)];
                    SingularityAnalysis.Compat4Index(q[i], normals[i], q[j], normals[j], out var k0, out var k1);
                    index += k1 - k0;
                }
                var mod = ((index % 4) + 4) % 4;
                if (mod != 1 && mod != 3) continue;
                result[f] = mod;
                total++;
            }
            count = total;
            return result;
        }

        /// <summary>
        /// <paramref name="v"/> rotated by the smallest rotation taking <paramref name="from"/> onto
        /// <paramref name="to"/> (Rodrigues). Written out rather than through Quaternion, whose
        /// FromToRotation is a native engine call and so unusable off the main thread.
        /// </summary>
        internal static Vector3 Transport(Vector3 v, Vector3 from, Vector3 to)
        {
            var c = Vector3.Dot(from, to);
            if (c <= -0.999999f) return -v;
            var axis = Vector3.Cross(from, to);
            return v * c + Vector3.Cross(axis, v) + axis * (Vector3.Dot(axis, v) / (1f + c));
        }

        static void Normalise(double[] re, double[] im)
        {
            double s = 0;
            for (var i = 0; i < re.Length; i++) s += re[i] * re[i] + im[i] * im[i];
            s = s > 0 ? 1.0 / Math.Sqrt(s / re.Length) : 1.0;
            for (var i = 0; i < re.Length; i++) { re[i] *= s; im[i] *= s; }
        }

        /// <summary>
        /// Jacobi-preconditioned CG on the free rows of (L + diag(lambda) + shift) z = b, where
        /// (L z)_i = deg_i z_i - sum_j r_ij z_j. Locked entries of z are left as given and their
        /// neighbours' share is expected in b already. Returns the iteration count.
        /// </summary>
        static int ConjugateGradient(int n, int[] start, int[] nbr, double[] rRe, double[] rIm,
            double[] lambda, bool[] locked, double shift, double[] bRe, double[] bIm,
            double[] zRe, double[] zIm, int maxIterations, float tolerance, out float residual)
        {
            var diag = new double[n];
            for (var i = 0; i < n; i++) diag[i] = start[i + 1] - start[i] + lambda[i] + shift;

            var resRe = new double[n];
            var resIm = new double[n];
            var pRe = new double[n];
            var pIm = new double[n];
            var qRe = new double[n];
            var qIm = new double[n];
            var sRe = new double[n];
            var sIm = new double[n];

            // Apply only to free entries; locked entries of the operand are treated as zero, since
            // their contribution has been moved to the right-hand side.
            void Apply(double[] xRe, double[] xIm, double[] yRe, double[] yIm, bool lockedAsZero)
            {
                System.Threading.Tasks.Parallel.For(0, n, BackgroundScheduler.Options, i =>
                {
                    if (locked[i]) { yRe[i] = 0; yIm[i] = 0; return; }
                    double re = diag[i] * xRe[i], im = diag[i] * xIm[i];
                    for (var k = start[i]; k < start[i + 1]; k++)
                    {
                        var j = nbr[k];
                        if (lockedAsZero && locked[j]) continue;
                        re -= rRe[k] * xRe[j] - rIm[k] * xIm[j];
                        im -= rRe[k] * xIm[j] + rIm[k] * xRe[j];
                    }
                    yRe[i] = re;
                    yIm[i] = im;
                });
            }

            Apply(zRe, zIm, qRe, qIm, true);
            double bNorm = 0, rz = 0;
            for (var i = 0; i < n; i++)
            {
                if (locked[i]) continue;
                resRe[i] = bRe[i] - qRe[i];
                resIm[i] = bIm[i] - qIm[i];
                sRe[i] = resRe[i] / diag[i];
                sIm[i] = resIm[i] / diag[i];
                pRe[i] = sRe[i];
                pIm[i] = sIm[i];
                rz += resRe[i] * sRe[i] + resIm[i] * sIm[i];
                bNorm += bRe[i] * bRe[i] + bIm[i] * bIm[i];
            }
            bNorm = Math.Sqrt(Math.Max(bNorm, 1e-300));

            var iteration = 0;
            residual = 0f;
            for (; iteration < maxIterations; iteration++)
            {
                Apply(pRe, pIm, qRe, qIm, true);
                double pq = 0;
                for (var i = 0; i < n; i++)
                    if (!locked[i]) pq += pRe[i] * qRe[i] + pIm[i] * qIm[i];
                if (!(pq > 0)) break;
                var alpha = rz / pq;

                double rr = 0, rzNext = 0;
                for (var i = 0; i < n; i++)
                {
                    if (locked[i]) continue;
                    zRe[i] += alpha * pRe[i];
                    zIm[i] += alpha * pIm[i];
                    resRe[i] -= alpha * qRe[i];
                    resIm[i] -= alpha * qIm[i];
                    sRe[i] = resRe[i] / diag[i];
                    sIm[i] = resIm[i] / diag[i];
                    rzNext += resRe[i] * sRe[i] + resIm[i] * sIm[i];
                    rr += resRe[i] * resRe[i] + resIm[i] * resIm[i];
                }

                residual = (float)(Math.Sqrt(rr) / bNorm);
                if (residual < tolerance) { iteration++; break; }

                var beta = rzNext / rz;
                rz = rzNext;
                for (var i = 0; i < n; i++)
                {
                    if (locked[i]) continue;
                    pRe[i] = sRe[i] + beta * pRe[i];
                    pIm[i] = sIm[i] + beta * pIm[i];
                }
            }
            return iteration;
        }

        static void BuildSymmetric(RemeshTopology topology, out int[] start, out int[] nbr)
        {
            var n = topology.vertexCount;
            var adjStart = topology.adjacencyStart;
            var adj = topology.adjacency;
            var lists = new List<int>[n];
            var seen = new HashSet<long>(Extraction.EdgeKey.Comparer);
            for (var i = 0; i < n; i++)
            {
                for (var k = adjStart[i]; k < adjStart[i + 1]; k++)
                {
                    var j = adj[k];
                    if (j == i || j < 0 || j >= n) continue;
                    var a = Math.Min(i, j);
                    var b = Math.Max(i, j);
                    if (!seen.Add(((long)a << 32) | (uint)b)) continue;
                    (lists[a] ??= new List<int>(8)).Add(b);
                    (lists[b] ??= new List<int>(8)).Add(a);
                }
            }

            start = new int[n + 1];
            for (var i = 0; i < n; i++) start[i + 1] = start[i] + (lists[i]?.Count ?? 0);
            nbr = new int[start[n]];
            for (var i = 0; i < n; i++)
            {
                if (lists[i] == null) continue;
                lists[i].Sort();
                lists[i].CopyTo(nbr, start[i]);
            }
        }
    }
}
