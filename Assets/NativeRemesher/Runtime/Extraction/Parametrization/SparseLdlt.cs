// Sparse LDL^T for the parametrisation's normal equations.
//
// Jacobi-preconditioned CG needed ~17,000 iterations (60 s) on the helmet head's 290k-triangle
// solve: the system is a vector Laplacian over a surface hundreds of edges across, so its condition
// number grows with the square of that and CG with it. A direct sparse Cholesky is the standard
// answer (CHOLMOD); this is the textbook route at a fraction of its size:
//
//   ordering   geometric nested dissection -- split the vertices at the median of the longest
//              axis, number the separator last, recurse. On a surface mesh that keeps fill near
//              n log n, where bandwidth orderings (RCM) fill like n^1.5.
//   symbolic   elimination tree and column counts, Davis's LDL (ACM TOMS 2005), up-looking.
//   numeric    the same, row by row: the subtrees under the top separators in parallel, then
//              the separators above them in order (or, with parallelSeparators, on every core --
//              see Separators).
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
        int[] perm;      // new -> old
        int[] inverse;   // old -> new
        // The permuted matrix's upper triangle by column (CSC): rows < col, plus the diagonal.
        int[] ap;
        int[] ai;
        double[] ax;
        int[] sourceSlot;   // ax[k] comes from the original CSR value at this slot
        int[] parent;
        int[] lnz;
        int[] lp;
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
            if (debugDumpPath != null) m_Dump = (start, col, points, last);
            perm = NestedDissection(n, start, col, points, last);
            Symbolic(start, col);
            if (parallelSeparators)
            {
                // The separators above the subtrees moved after every subtree, in their order: a
                // child still comes before its parent, so this is the same elimination tree and the
                // same factor, and the separators are one run at the end (see Separators).
                var task = Tasks();
                var moved = false;
                for (var k = 1; k < n && !moved; k++) moved = task[k - 1] < 0 && task[k] >= 0;
                if (moved)
                {
                    var order = new int[n];
                    var q = 0;
                    for (var k = 0; k < n; k++) if (task[k] >= 0) order[q++] = perm[k];
                    for (var k = 0; k < n; k++) if (task[k] < 0) order[q++] = perm[k];
                    perm = order;
                    Symbolic(start, col);
                }
            }
            d = new double[n];
        }

        /// <summary>
        /// The separators above the subtrees factored by all cores rather than one after another:
        /// each separator row's part left of the separators solved against the subtrees in parallel
        /// (no separator row reads another's), then their Schur complement summed in parallel, then
        /// factored on its own -- a matrix a fraction of the size. The same factor to rounding:
        /// armoured car at 5,000, 9.6 s -> 1.4 s a factorisation in the editor, the whole remesh
        /// 315 -> 216 s. False: the separators one after another, as before.
        /// </summary>
        public static bool parallelSeparators = true;

        // The permuted matrix and its symbolic factor, for the current perm.
        void Symbolic(int[] start, int[] col)
        {
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
        }

        /// <summary>Diagnostics: when set, the first factorisation's matrix is written here (and this cleared).</summary>
        public static string debugDumpPath;
        (int[] start, int[] col, Vector3[] points, bool[] last)? m_Dump;

        void Dump(double[] values, bool[] fixedVar)
        {
            var path = debugDumpPath;
            debugDumpPath = null;
            var (start, col, points, last) = m_Dump.Value;
            using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
            w.Write(n);
            w.Write(col.Length);
            foreach (var v in start) w.Write(v);
            foreach (var v in col) w.Write(v);
            for (var i = 0; i < n; i++) { w.Write(points[i].x); w.Write(points[i].y); w.Write(points[i].z); }
            for (var i = 0; i < n; i++) w.Write(last != null && last[i]);
            for (var i = 0; i < col.Length; i++) w.Write(values[i]);
            for (var i = 0; i < n; i++) w.Write(fixedVar[i]);
        }

        /// <summary>
        /// Factors the matrix with the given values (same pattern as at construction), with every
        /// fixed variable's row and column replaced by the identity. False if a pivot vanished.
        /// </summary>
        public bool Factor(double[] values, bool[] fixedVar)
        {
            if (m_Dump != null && debugDumpPath != null) Dump(values, fixedVar);
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
            System.Threading.Tasks.Parallel.ForEach(m_TaskRows, BackgroundScheduler.Options, rows =>
            {
                var scratch = new Scratch(n);
                foreach (var k in rows)
                    if (!Row(k, values, fixedVar, count, scratch)) System.Threading.Interlocked.Exchange(ref failed, 1);
            });
            if (failed != 0) return false;
            lastParallelMs = watch.ElapsedMilliseconds;
            // The separators all after the subtrees (as parallelSeparators orders them): on every core.
            if (parallelSeparators && m_TailRun == m_TopRows.Length && m_TailRun > 0)
            {
                var separatorsOk = Separators(n - m_TailRun, values, fixedVar, count);
                lastSeparatorMs = watch.ElapsedMilliseconds - lastParallelMs;
                return separatorsOk;
            }
            var top = new Scratch(n);
            foreach (var k in m_TopRows)
                if (!Row(k, values, fixedVar, count, top)) return false;
            lastSeparatorMs = watch.ElapsedMilliseconds - lastParallelMs;
            return true;
        }

        /// <summary>Diagnostics: the last factorisation's separators, and their three stages, ms.</summary>
        public long lastSeparatorMs, lastSolveMs, lastSumMs, lastFactorMs;
        public string lastSplit = "";

        /// <summary>A separator column gathering at least this many updates has them split across the cores.</summary>
        public static int splitWork = 20000;

        // The separator rows head..n-1 (every row a subtree's is before head): see parallelSeparators.
        bool Separators(int head, double[] values, bool[] fixedVar, int[] count)
        {
            var t = n - head;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var rowIndex = new int[t][];
            var rowValue = new double[t][];
            var ownIndex = new int[t][];
            var ownValue = new double[t][];
            // 1. Each row's entries left of the separators, solved against the subtrees' rows.
            System.Threading.Tasks.Parallel.For(0, t, BackgroundScheduler.Options, () => new Scratch(n), (r, _, s) =>
            {
                var k = head + r;
                var y = s.y;
                var pattern = s.pattern;
                var flag = s.flag;
                var top = n;
                flag[k] = k;
                var kFixed = fixedVar[perm[k]];
                var own = 0;
                for (var p = ap[k]; p < ap[k + 1]; p++) if (ai[p] >= head) own++;
                var oi = new int[own];
                var ov = new double[own];
                own = 0;
                for (var p = ap[k]; p < ap[k + 1]; p++)
                {
                    var i = ai[p];
                    var v = values[sourceSlot[p]];
                    if (kFixed || fixedVar[perm[i]]) v = i == k ? 1.0 : 0.0;
                    if (i >= head) { oi[own] = i; ov[own++] = v; continue; }
                    y[i] += v;
                    var len = 0;
                    for (; i < head && flag[i] != k; i = parent[i])
                    {
                        pattern[len++] = i;
                        flag[i] = k;
                    }
                    while (len > 0) pattern[--top] = pattern[--len];
                }
                var idx = new int[n - top];
                var val = new double[n - top];
                for (var q = 0; top < n; top++, q++)
                {
                    var i = pattern[top];
                    var yi = y[i];
                    y[i] = 0;
                    var p2 = lp[i] + count[i];
                    for (var p = lp[i]; p < p2; p++) y[li[p]] -= lx[p] * yi;
                    idx[q] = i;
                    val[q] = yi / d[i];
                }
                rowIndex[r] = idx;
                rowValue[r] = val;
                ownIndex[r] = oi;
                ownValue[r] = ov;
                return s;
            }, _ => { });

            // Into their columns, in row order, after the subtrees' rows.
            var headCount = (int[])count.Clone();
            for (var r = 0; r < t; r++)
            {
                var idx = rowIndex[r];
                var val = rowValue[r];
                for (var q = 0; q < idx.Length; q++)
                {
                    var p = lp[idx[q]] + count[idx[q]]++;
                    li[p] = head + r;
                    lx[p] = val[q];
                }
            }
            lastSolveMs = watch.ElapsedMilliseconds;

            // 2. The Schur complement's rows, lower triangle: S(k, j) = A(k, j) - sum over h of L(k, h) d(h) L(j, h).
            var schurIndex = new int[t][];
            var schurValue = new double[t][];
            System.Threading.Tasks.Parallel.For(0, t, BackgroundScheduler.Options, () => (new double[t], new int[t], new bool[t]), (r, _, s) =>
            {
                var (acc, touched, mark) = s;
                var k = head + r;
                var used = 0;
                void Add(int j, double v)
                {
                    var c = j - head;
                    if (!mark[c]) { mark[c] = true; touched[used++] = c; }
                    acc[c] += v;
                }
                var oi = ownIndex[r];
                var ov = ownValue[r];
                for (var q = 0; q < oi.Length; q++) Add(oi[q], ov[q]);
                var idx = rowIndex[r];
                var val = rowValue[r];
                for (var q = 0; q < idx.Length; q++)
                {
                    var h = idx[q];
                    var w = val[q] * d[h];
                    var end = lp[h] + count[h];
                    for (var p = lp[h] + headCount[h]; p < end; p++)
                    {
                        var j = li[p];
                        if (j > k) break;
                        Add(j, -w * lx[p]);
                    }
                }
                Array.Sort(touched, 0, used);
                var si = new int[used];
                var sv = new double[used];
                for (var q = 0; q < used; q++)
                {
                    var c = touched[q];
                    si[q] = head + c;
                    sv[q] = acc[c];
                    acc[c] = 0;
                    mark[c] = false;
                }
                schurIndex[r] = si;
                schurValue[r] = sv;
                return s;
            }, _ => { });
            lastSumMs = watch.ElapsedMilliseconds - lastSolveMs;

            // 3. The Schur complement factored by supernodes, left-looking: a run of columns whose
            //    rows below are the same (a separator's, mostly) gathered as one dense block, rows
            //    by its columns -- S's entries, less L(:, k) d(k) L(J, k) for every earlier column k
            //    with entries in its rows, those split across the cores, each into its own copy --
            //    then factored dense, its rows below the diagonal block in parallel too. Its pattern
            //    is the same every time, so it is laid once.
            if (!m_SeparatorPattern) LaySeparatorPattern(head, schurIndex);
            var threads = Math.Max(1, Math.Min(16, Environment.ProcessorCount - 1));
            var position = new int[t];      // column c's next entry at or below the current block, as a slot of lx
            var firstAt = new int[t];       // the first column whose next entry is in row r, or -1
            var nextIn = new int[t];
            for (var r = 0; r < t; r++) firstAt[r] = -1;
            var rowPos = new int[t];
            var gather = new List<int>();
            m_Blocks ??= new double[threads + 1][];
            if (m_At == null)
            {
                m_At = new int[threads + 1][];
                for (var q = 0; q <= threads; q++) m_At[q] = new int[t];
            }
            long split = 0, total = 0;
            foreach (var (f, c) in m_Supernodes)
            {
                var fc = f - head;
                var lastCol = f + c - 1;
                var below = lnz[lastCol];
                var m = c + below;
                var rows = new int[m];
                for (var q = 0; q < c; q++) rows[q] = f + q;
                Array.Copy(li, lp[lastCol], rows, c, below);
                for (var q = 0; q < m; q++) rowPos[rows[q] - head] = q;
                double[] Block(int slot, bool clear = true)
                {
                    var b = m_Blocks[slot];
                    if (b == null || b.Length < m * c) m_Blocks[slot] = b = new double[Math.Max(m * c, 1024)];
                    else if (clear) Array.Clear(b, 0, m * c);
                    return b;
                }
                var block = Block(threads);
                for (var jc = 0; jc < c; jc++)
                {
                    var r = fc + jc;
                    var si = schurIndex[r];
                    var sv = schurValue[r];
                    // Row r's entries left of and at the diagonal are S(r, j <= r): only those in this block's columns land.
                    for (var q = 0; q < si.Length; q++)
                    {
                        var j = si[q];
                        if (j >= f) block[(j - f) * m + jc] += sv[q];
                    }
                }
                // S below the block's own rows: entries S(i, j) with i a row below and j a block column.
                for (var q = c; q < m; q++)
                {
                    var r = rows[q] - head;
                    var si = schurIndex[r];
                    var sv = schurValue[r];
                    var lo = Array.BinarySearch(si, f);
                    if (lo < 0) lo = ~lo;
                    for (var e = lo; e < si.Length && si[e] <= lastCol; e++) block[(si[e] - f) * m + q] += sv[e];
                }

                gather.Clear();
                long work = 0;
                for (var r = fc; r < fc + c; r++)
                {
                    for (var k = firstAt[r]; k >= 0; k = nextIn[k])
                    {
                        gather.Add(k);
                        work += (long)(lp[head + k] + lnz[head + k] - position[k]) * Math.Min(c, 8);
                    }
                    firstAt[r] = -1;
                }
                total += work;

                // L(i, k) d(k) L(j, k) off the block for each gathered column k, i its rows from j down.
                void Gather(int k, double[] into, int[] at)
                {
                    var p0 = position[k];
                    var endK = lp[head + k] + lnz[head + k];
                    var dk = d[head + k];
                    for (var p = p0; p < endK; p++) at[p - p0] = rowPos[li[p] - head];
                    for (var jj = p0; jj < endK && li[jj] <= lastCol; jj++)
                    {
                        var column = (li[jj] - f) * m;
                        var w = lx[jj] * dk;
                        for (var p = jj; p < endK; p++) into[column + at[p - p0]] -= lx[p] * w;
                    }
                }
                if (work < splitWork || threads == 1)
                    foreach (var k in gather) Gather(k, block, m_At[threads]);
                else
                {
                    split += work;
                    var parts = (int)Math.Max(2, Math.Min(threads, work / splitWork));
                    var copies = new double[parts][];
                    for (var part = 0; part < parts; part++) copies[part] = Block(part, false);
                    System.Threading.Tasks.Parallel.For(0, parts, BackgroundScheduler.Options, part =>
                    {
                        Array.Clear(copies[part], 0, m * c);
                        for (var g = part; g < gather.Count; g += parts) Gather(gather[g], copies[part], m_At[part]);
                    });
                    var size = m * c;
                    System.Threading.Tasks.Parallel.For(0, (size + 4095) / 4096, BackgroundScheduler.Options, chunk =>
                    {
                        var e1 = Math.Min(size, (chunk + 1) * 4096);
                        for (var e = chunk * 4096; e < e1; e++)
                        {
                            var sum = 0.0;
                            for (var part = 0; part < parts; part++) sum += copies[part][e];
                            block[e] += sum;
                        }
                    });
                }
                // The gathered columns past this block, onto their next row's list.
                foreach (var k in gather)
                {
                    var endK = lp[head + k] + lnz[head + k];
                    var pk = position[k];
                    while (pk < endK && li[pk] <= lastCol) pk++;
                    position[k] = pk;
                    if (pk < endK)
                    {
                        var r = li[pk] - head;
                        nextIn[k] = firstAt[r];
                        firstAt[r] = k;
                    }
                }

                // The block factored column by column: column jc less column e times L(jc, e) d(e)
                // for every e before it, then over its pivot -- the diagonal rows first, as the
                // multipliers come from them, then the rows below in chunks.
                var dd = new double[c];
                var wTop = new double[c * c];   // wTop[jc * c + e] = L(jc, e) d(e)
                for (var jc = 0; jc < c; jc++)
                {
                    var cj = jc * m;
                    var rw = jc * c;
                    for (var e = 0; e < jc; e++) wTop[rw + e] = block[e * m + jc] * dd[e];
                    void Rows(int a0, int a1)
                    {
                        for (var e = 0; e < jc; e++)
                        {
                            var we = wTop[rw + e];
                            var ce = e * m;
                            for (var a = a0; a < a1; a++) block[cj + a] -= block[ce + a] * we;
                        }
                    }
                    var span = c - jc;
                    if ((long)span * jc < 2L * splitWork || threads == 1 || span < 128) Rows(jc, c);
                    else
                        System.Threading.Tasks.Parallel.For(0, (span + 63) / 64, BackgroundScheduler.Options, chunk =>
                            Rows(jc + chunk * 64, Math.Min(c, jc + (chunk + 1) * 64)));
                    var diag = block[cj + jc];
                    dd[jc] = diag;
                    if (!(Math.Abs(diag) >= 1e-300))
                    {
                        lastFailedRow = perm[f + jc];
                        lastFailedPivot = diag;
                        return false;
                    }
                    var inv = 1.0 / diag;
                    for (var a = jc + 1; a < c; a++) block[cj + a] *= inv;
                }
                void SolveBelow(int q0, int q1)
                {
                    for (var jc = 0; jc < c; jc++)
                    {
                        var cj = jc * m;
                        var rw = jc * c;
                        for (var e = 0; e < jc; e++)
                        {
                            var we = wTop[rw + e];
                            var ce = e * m;
                            for (var q = q0; q < q1; q++) block[cj + q] -= block[ce + q] * we;
                        }
                        var inv = 1.0 / dd[jc];
                        for (var q = q0; q < q1; q++) block[cj + q] *= inv;
                    }
                }
                if ((long)below * c * c < 4L * splitWork || threads == 1) SolveBelow(c, m);
                else
                    System.Threading.Tasks.Parallel.For(0, (below + 63) / 64, BackgroundScheduler.Options, chunk =>
                        SolveBelow(c + chunk * 64, Math.Min(m, c + (chunk + 1) * 64)));
                // Into L's columns (the pattern laid already) and d.
                for (var jc = 0; jc < c; jc++)
                {
                    var j = f + jc;
                    d[j] = dd[jc];
                    var slot = lp[j];
                    var cj = jc * m;
                    for (var q = jc + 1; q < m; q++) lx[slot++] = block[cj + q];
                    count[j] = lnz[j];
                    // This column's next entry below the block, for the blocks above.
                    var pj = lp[j] + (c - 1 - jc);
                    position[j - head] = pj;
                    if (pj < lp[j] + lnz[j])
                    {
                        var r = li[pj] - head;
                        nextIn[j - head] = firstAt[r];
                        firstAt[r] = j - head;
                    }
                }
            }
            lastFactorMs = watch.ElapsedMilliseconds - lastSolveMs - lastSumMs;
            lastSplit = $"{m_Supernodes.Count} supernodes, {split / (double)Math.Max(1, total):P0} of the work split";
            return true;
        }

        bool m_SeparatorPattern;
        List<(int first, int columns)> m_Supernodes;
        double[][] m_Blocks;
        int[][] m_At;

        // L's pattern in the separators' columns, from S's (the same every factorisation): row k
        // has an entry in every column its entries' etree paths pass, in row order. Then the
        // supernodes: runs of columns, each its predecessor's only child with one row fewer.
        void LaySeparatorPattern(int head, int[][] schurIndex)
        {
            var t = n - head;
            var fill = new int[n];
            var flag = new int[n];
            for (var j = 0; j < n; j++) flag[j] = -1;
            for (var j = head; j < n; j++) fill[j] = lp[j];
            for (var r = 0; r < t; r++)
            {
                var k = head + r;
                flag[k] = k;
                foreach (var j0 in schurIndex[r])
                    for (var i = j0; flag[i] != k; i = parent[i])
                    {
                        li[fill[i]++] = k;
                        flag[i] = k;
                    }
            }
            for (var j = head; j < n; j++)
                if (fill[j] != lp[j] + lnz[j]) throw new InvalidOperationException($"separator column {j}: {fill[j] - lp[j]} rows, symbolic {lnz[j]}");
            var children = new int[n];
            for (var j = head; j < n; j++) if (parent[j] >= 0) children[parent[j]]++;
            m_Supernodes = new List<(int, int)>();
            for (var j = head; j < n;)
            {
                var first = j++;
                while (j < n && parent[j - 1] == j && lnz[j - 1] == lnz[j] + 1 && children[j] == 1) j++;
                m_Supernodes.Add((first, j - first));
            }
            m_SeparatorPattern = true;
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

        // Each row's task: the root of the largest subtree under the limit on its path, or -1 for a
        // separator above them.
        int[] Tasks()
        {
            var size = new int[n];
            for (var k = 0; k < n; k++)
            {
                size[k]++;
                if (parent[k] >= 0) size[parent[k]] += size[k];
            }
            var limit = Math.Max(256, n / (8 * Math.Max(1, Environment.ProcessorCount)));
            var task = new int[n];
            for (var k = n - 1; k >= 0; k--)
            {
                // Parents are visited first (higher index), so a node's task is its parent's, or
                // itself when it is the largest subtree under the limit on its path.
                var p = parent[k];
                if (p >= 0 && task[p] >= 0 && size[p] <= limit) task[k] = task[p];
                else task[k] = size[k] <= limit ? k : -1;
            }
            return task;
        }

        void BuildTasks()
        {
            if (m_TaskRows != null) return;
            var task = Tasks();
            var lists = new Dictionary<int, List<int>>();
            var topRows = new List<int>();
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
            m_TailRun = 0;
            while (m_TailRun < n && task[n - 1 - m_TailRun] < 0) m_TailRun++;
        }

        int m_TailRun;
        public int tailRunRows => m_TailRun;

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
