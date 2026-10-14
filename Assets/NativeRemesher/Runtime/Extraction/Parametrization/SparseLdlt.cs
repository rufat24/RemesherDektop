// Sparse LDL^T for the parametrisation's normal equations.
//
// Jacobi-preconditioned CG needed ~17,000 iterations (60 s) on the helmet head's 290k-triangle
// solve: the system is a vector Laplacian over a surface hundreds of edges across, so its condition
// number grows with the square of that and CG with it. FreeRemesher ships CHOLMOD for the same
// system. This is the textbook route at a fraction of CHOLMOD's size:
//
//   ordering   geometric nested dissection -- split the vertices at the median of the longest
//              axis, number the separator last, recurse. On a surface mesh that keeps fill near
//              n log n, where bandwidth orderings (RCM) fill like n^1.5.
//   symbolic   elimination tree and column counts, Davis's LDL (ACM TOMS 2005), up-looking.
//   numeric    the same, row by row.
//
// Fixed variables (the rounded integers) keep their rows and columns, replaced by the identity, so
// the pattern never changes and only the numeric pass is repeated as rounding proceeds.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class SparseLdlt
    {
        readonly int n;
        readonly int[] perm;      // new -> old
        readonly int[] inverse;   // old -> new
        // The permuted matrix's upper triangle by column (CSC): rows < col, plus the diagonal.
        readonly int[] ap;
        readonly int[] ai;
        readonly double[] ax;
        readonly int[] sourceSlot;   // ax[k] comes from the original CSR value at this slot
        readonly int[] parent;
        readonly int[] lnz;
        readonly int[] lp;
        int[] li;
        double[] lx;
        readonly double[] d;

        public long factorNonZeros => lp[n];

        /// <param name="start">CSR row starts of the full symmetric matrix.</param>
        /// <param name="col">CSR columns.</param>
        /// <param name="points">A position per variable, for the ordering.</param>
        public SparseLdlt(int n, int[] start, int[] col, Vector3[] points, bool[] last = null)
        {
            this.n = n;
            perm = NestedDissection(n, start, col, points, last);
            inverse = new int[n];
            for (var k = 0; k < n; k++) inverse[perm[k]] = k;

            // Upper triangle of P A P^T by column: entry (i, j) of A lands at (inv[i], inv[j]).
            var count = new int[n + 1];
            for (var i = 0; i < n; i++)
                for (var s = start[i]; s < start[i + 1]; s++)
                {
                    int r = inverse[i], c = inverse[col[s]];
                    if (r <= c) count[c + 1]++;
                }
            ap = new int[n + 1];
            for (var c = 0; c < n; c++) ap[c + 1] = ap[c] + count[c + 1];
            ai = new int[ap[n]];
            ax = new double[ap[n]];
            sourceSlot = new int[ap[n]];
            var fill = new int[n];
            Array.Copy(ap, fill, n);
            for (var i = 0; i < n; i++)
                for (var s = start[i]; s < start[i + 1]; s++)
                {
                    int r = inverse[i], c = inverse[col[s]];
                    if (r > c) continue;
                    ai[fill[c]] = r;
                    sourceSlot[fill[c]] = s;
                    fill[c]++;
                }

            // Symbolic: elimination tree and the nonzero count of each column of L.
            parent = new int[n];
            lnz = new int[n];
            var flag = new int[n];
            for (var k = 0; k < n; k++)
            {
                parent[k] = -1;
                flag[k] = k;
                lnz[k] = 0;
                for (var p = ap[k]; p < ap[k + 1]; p++)
                {
                    var i = ai[p];
                    if (i >= k) continue;
                    for (; flag[i] != k; i = parent[i])
                    {
                        if (parent[i] == -1) parent[i] = k;
                        lnz[i]++;
                        flag[i] = k;
                    }
                }
            }
            lp = new int[n + 1];
            for (var k = 0; k < n; k++) lp[k + 1] = lp[k] + lnz[k];
            d = new double[n];
        }

        /// <summary>
        /// Factors the matrix with the given values (same pattern as at construction), with every
        /// fixed variable's row and column replaced by the identity. False if a pivot vanished.
        /// </summary>
        public bool Factor(double[] values, bool[] fixedVar)
        {
            if (li == null)
            {
                li = new int[lp[n]];
                lx = new double[lp[n]];
            }
            var count = new int[n];
            BuildTasks();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // Rows of disjoint elimination subtrees write disjoint columns and read only their own
            // subtree's, so the subtrees factor in parallel; the separators above them, whose rows
            // read everything below, follow in order. A parent's index always exceeds its
            // children's, so every column still receives its entries in increasing row order.
            var failed = 0;
            System.Threading.Tasks.Parallel.ForEach(m_TaskRows, rows =>
            {
                var scratch = new Scratch(n);
                foreach (var k in rows)
                    if (!Row(k, values, fixedVar, count, scratch)) System.Threading.Interlocked.Exchange(ref failed, 1);
            });
            if (failed != 0) return false;
            lastParallelMs = watch.ElapsedMilliseconds;
            var top = new Scratch(n);
            foreach (var k in m_TopRows)
                if (!Row(k, values, fixedVar, count, top)) return false;
            return true;
        }

        sealed class Scratch
        {
            public readonly double[] y;
            public readonly int[] pattern;
            public readonly int[] flag;
            public Scratch(int n)
            {
                y = new double[n];
                pattern = new int[n];
                flag = new int[n];
                for (var i = 0; i < n; i++) flag[i] = -1;
            }
        }

        public long lastParallelMs;
        public int topRowCount => m_TopRows?.Length ?? 0;
        public int taskCount => m_TaskRows?.Count ?? 0;
        public long TopNonZeros()
        {
            long s = 0;
            if (m_TopRows != null) foreach (var k in m_TopRows) s += lnz[k];
            return s;
        }

        List<int[]> m_TaskRows;
        int[] m_TopRows;

        void BuildTasks()
        {
            if (m_TaskRows != null) return;
            var size = new int[n];
            for (var k = 0; k < n; k++)
            {
                size[k]++;
                if (parent[k] >= 0) size[parent[k]] += size[k];
            }
            var limit = Math.Max(256, n / (8 * Math.Max(1, Environment.ProcessorCount)));
            var task = new int[n];
            var lists = new Dictionary<int, List<int>>();
            var topRows = new List<int>();
            for (var k = n - 1; k >= 0; k--)
            {
                // Parents are visited first (higher index), so a node's task is its parent's, or
                // itself when it is the largest subtree under the limit on its path.
                var p = parent[k];
                if (p >= 0 && task[p] >= 0 && size[p] <= limit) task[k] = task[p];
                else task[k] = size[k] <= limit ? k : -1;
            }
            for (var k = 0; k < n; k++)
            {
                if (task[k] < 0) { topRows.Add(k); continue; }
                if (!lists.TryGetValue(task[k], out var list)) lists[task[k]] = list = new List<int>();
                list.Add(k);
            }
            m_TaskRows = new List<int[]>(lists.Count);
            foreach (var list in lists.Values) m_TaskRows.Add(list.ToArray());
            m_TaskRows.Sort((x, y) => y.Length.CompareTo(x.Length));
            m_TopRows = topRows.ToArray();
        }

        bool Row(int k, double[] values, bool[] fixedVar, int[] count, Scratch s)
        {
            var y = s.y;
            var pattern = s.pattern;
            var flag = s.flag;
            y[k] = 0;
            var top = n;
            flag[k] = k;
            count[k] = 0;
            var kFixed = fixedVar[perm[k]];
            for (var p = ap[k]; p < ap[k + 1]; p++)
            {
                var i = ai[p];
                var v = values[sourceSlot[p]];
                if (kFixed || fixedVar[perm[i]]) v = i == k ? 1.0 : 0.0;
                y[i] += v;
                var len = 0;
                for (; flag[i] != k; i = parent[i])
                {
                    pattern[len++] = i;
                    flag[i] = k;
                }
                while (len > 0) pattern[--top] = pattern[--len];
            }
            d[k] = y[k];
            var akk = y[k];
            y[k] = 0;
            for (; top < n; top++)
            {
                var i = pattern[top];
                var yi = y[i];
                y[i] = 0;
                var p2 = lp[i] + count[i];
                for (var p = lp[i]; p < p2; p++) y[li[p]] -= lx[p] * yi;
                var lki = yi / d[i];
                d[k] -= lki * yi;
                li[p2] = k;
                lx[p2] = lki;
                count[i]++;
            }
            if (Math.Abs(d[k]) < 1e-9 * Math.Abs(akk) && nearZeroRow < 0) { nearZeroRow = perm[k]; nearZeroPivot = d[k] / akk; }
            if (Math.Abs(d[k]) >= 1e-300 && !double.IsNaN(d[k])) return true;
            lastFailedRow = perm[k];
            lastFailedPivot = d[k];
            return false;
        }

        /// <summary>Diagnostics: the variable whose pivot vanished on the last failed factorisation.</summary>
        public int lastFailedRow = -1;

        /// <summary>Diagnostics: the first variable whose pivot fell below 1e-9 of its diagonal.</summary>
        public int nearZeroRow = -1;
        public double nearZeroPivot;
        public double lastFailedPivot;

        /// <summary>Solves A x = b in place (b in original variable order).</summary>
        public void Solve(double[] b, double[] x)
        {
            var z = new double[n];
            for (var k = 0; k < n; k++) z[k] = b[perm[k]];
            for (var j = 0; j < n; j++)
                for (var p = lp[j]; p < lp[j + 1]; p++) z[li[p]] -= lx[p] * z[j];
            for (var j = 0; j < n; j++) z[j] /= d[j];
            for (var j = n - 1; j >= 0; j--)
                for (var p = lp[j]; p < lp[j + 1]; p++) z[j] -= lx[p] * z[li[p]];
            for (var k = 0; k < n; k++) x[perm[k]] = z[k];
        }

        /// <summary>
        /// Geometric nested dissection on the variable graph: halve at the median of the longest
        /// extent, take as separator the vertices of the upper half with a neighbour in the lower
        /// half, order both halves first (recursively) and the separator last.
        /// </summary>
        static int[] NestedDissection(int n, int[] start, int[] col, Vector3[] points, bool[] last)
        {
            var order = new List<int>(n);
            var part = new int[n];
            var stamp = 0;
            var all = new List<int>(n);
            for (var i = 0; i < n; i++) if (last == null || !last[i]) all.Add(i);
            Recurse(all.ToArray(), 0);
            // Variables whose rows span the surface (a cut's translation touches every face along
            // the cut) are separators of everything; numbered last, they fill one small dense
            // corner instead of every column they cross.
            if (last != null) for (var i = 0; i < n; i++) if (last[i]) order.Add(i);

            void Recurse(int[] set, int depth)
            {
                if (set.Length <= 64 || depth > 40)
                {
                    order.AddRange(set);
                    return;
                }
                Vector3 lo = points[set[0]], hi = lo;
                foreach (var v in set)
                {
                    lo = Vector3.Min(lo, points[v]);
                    hi = Vector3.Max(hi, points[v]);
                }
                var ext = hi - lo;
                var axis = ext.x >= ext.y && ext.x >= ext.z ? 0 : ext.y >= ext.z ? 1 : 2;
                var keys = new float[set.Length];
                for (var i = 0; i < set.Length; i++) keys[i] = points[set[i]][axis];
                var sorted = (int[])set.Clone();
                Array.Sort(keys, sorted);
                var half = sorted.Length / 2;
                var mark = ++stamp;
                for (var i = 0; i < half; i++) part[sorted[i]] = mark;          // lower half
                for (var i = half; i < sorted.Length; i++) part[sorted[i]] = -mark;  // upper half
                var lower = new List<int>(half);
                var upper = new List<int>(sorted.Length - half);
                var separator = new List<int>();
                for (var i = 0; i < half; i++) lower.Add(sorted[i]);
                for (var i = half; i < sorted.Length; i++)
                {
                    var v = sorted[i];
                    var touches = false;
                    for (var s = start[v]; s < start[v + 1]; s++)
                        if (part[col[s]] == mark) { touches = true; break; }
                    if (touches) separator.Add(v); else upper.Add(v);
                }
                // A separator that swallowed the half (a degenerate split) is not a dissection.
                if (separator.Count > set.Length / 2)
                {
                    order.AddRange(set);
                    return;
                }
                foreach (var v in separator) part[v] = 0;
                Recurse(lower.ToArray(), depth + 1);
                Recurse(upper.ToArray(), depth + 1);
                order.AddRange(separator);
            }

            return order.ToArray();
        }
    }
}
