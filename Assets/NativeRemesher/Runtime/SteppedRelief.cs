// A window, a door, a panel: a hole in a flat wall whose contents, seen square on, are flat steps
// -- the casing standing out, the sash a little in, the panes further in, the bars between them.
// Modelled by hand it is a grid of lines through every step's edge, each cell lifted or sunk to its
// depth and the steps between cells walled: quads only, every edge where the shape has one.
//
// Built the same way here. The hole's rectangle (in the wall's plane) is sampled square on: the
// height above the wall of the outermost surface at each sample. Lines go where the height jumps
// along much of a row or column; each cell takes its samples' median, the depths cluster to a few
// levels, lines with the same level on both sides go, long gaps get more lines. Then quads: each
// cell's top at its level, and between cells of different levels walls, cut at every level either
// end of them has so their edges are shared.
//
// The hole need not be a rectangle (a casing narrower than its head and sill): the grid covers a
// rectangle round it, and only the cells where the surface seen is not the wall's are built -- one
// piece, square-cornered, its outline the grid's lines; the rest is the wall's.
//
// The source must agree: the samples near enough their cell's level, none missing. Otherwise the
// hole is left to the general fill.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    internal static class SteppedRelief
    {
        /// <summary>Samples per cell, along each side.</summary>
        public static int samplesPerCell = 16;

        /// <summary>A height jump of more than this many cells is a step.</summary>
        public static float stepCells = 0.08f;

        /// <summary>A step is a line when it runs at least this many cells.</summary>
        public static float minStepCells = 0.15f;

        /// <summary>Samples within this many cells of their cell's level fit.</summary>
        public static float fitCells = 0.12f;

        /// <summary>At least this share of the samples (not next to a line) must fit.</summary>
        public static float fitShare = 0.9f;

        /// <summary>A relief with more cells than this many per square cell of its area is too busy to build this way.</summary>
        public static float maxCellsPerArea = 12f;

        /// <summary>
        /// A step narrower than this many cells (a window's glazing bar, a casing's lip) is not built:
        /// it takes its wider neighbour's level and is left to the normal map, and its samples are not
        /// asked to fit. Off: the white house's bars took 3,278 of its 5,375 faces at 5,000, but built
        /// they measure nearer and look right, and windows whose steps fail are boxed instead
        /// (PlanarQuads.reliefBoxes); at 0.5 its building quads lost to its torn lattice at 3,500-8,000,
        /// at 0.25 they kept and looked as clean. 0: every step built, as before.
        /// </summary>
        public static float minWidthCells = 0f;

        public sealed class Relief
        {
            // Lines across the rectangle (both ends included) and each cell's height above the wall.
            public List<float> us, vs;
            public float[,] level;
            // Cells inside the hole (outside: the wall's, at 0), and their outline: grid points,
            // counter-clockwise.
            public bool[,] inside;
            public List<Vector2Int> outline;
            public int samples, missing;
            public float fit;
        }

        // A source triangle in the wall's frame.
        public struct Flat
        {
            public Vector2 a, b, c;
            public float ha, hb, hc;
            // The wall's own.
            public bool wall;
        }

        /// <summary>
        /// The relief over [u0, u1] x [v0, v1] from the triangles (in the wall's frame, heights
        /// above it), with lines at least at the given places. Null and why when it is not one.
        /// </summary>
        public static Relief Build(List<Flat> triangles, float u0, float u1, float v0, float v1,
            List<float> exactU, List<float> exactV, float cell, float spacing, out string why)
        {
            why = null;
            float w = u1 - u0, hgt = v1 - v0;
            if (!(w > 0f) || !(hgt > 0f)) { why = "empty"; return null; }
            var s = cell / samplesPerCell;
            var nu = Mathf.Clamp(Mathf.CeilToInt(w / s), 2, 1024);
            var nv = Mathf.Clamp(Mathf.CeilToInt(hgt / s), 2, 1024);
            float su = w / nu, sv = hgt / nv;
            Raster(triangles, u0, v0, su, sv, nu, nv, out var height, out var wall);
            return Build(height, wall, u0, v0, su, sv, exactU, exactV, cell, spacing, out why);
        }

        /// <summary>
        /// The outermost surface's height at each sample (centres u0 + (i + 1/2) su, ...), and
        /// whether it is the wall's; negative infinity where there is none.
        /// </summary>
        public static void Raster(List<Flat> triangles, float u0, float v0, float su, float sv, int nu, int nv,
            out float[,] height, out bool[,] wall)
        {
            height = new float[nu, nv];
            wall = new bool[nu, nv];
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++) height[i, j] = float.NegativeInfinity;
            foreach (var t in triangles)
            {
                var d = (t.b.x - t.a.x) * (t.c.y - t.a.y) - (t.c.x - t.a.x) * (t.b.y - t.a.y);
                if (!(d > 0f)) continue;
                var minX = Mathf.Min(t.a.x, Mathf.Min(t.b.x, t.c.x));
                var maxX = Mathf.Max(t.a.x, Mathf.Max(t.b.x, t.c.x));
                var minY = Mathf.Min(t.a.y, Mathf.Min(t.b.y, t.c.y));
                var maxY = Mathf.Max(t.a.y, Mathf.Max(t.b.y, t.c.y));
                var i0 = Mathf.Max(0, Mathf.CeilToInt((minX - u0) / su - 0.5f));
                var i1 = Mathf.Min(nu - 1, Mathf.FloorToInt((maxX - u0) / su - 0.5f));
                var j0 = Mathf.Max(0, Mathf.CeilToInt((minY - v0) / sv - 0.5f));
                var j1 = Mathf.Min(nv - 1, Mathf.FloorToInt((maxY - v0) / sv - 0.5f));
                for (var i = i0; i <= i1; i++)
                {
                    var x = u0 + (i + 0.5f) * su;
                    for (var j = j0; j <= j1; j++)
                    {
                        var y = v0 + (j + 0.5f) * sv;
                        var l1 = ((x - t.a.x) * (t.c.y - t.a.y) - (t.c.x - t.a.x) * (y - t.a.y)) / d;
                        var l2 = ((t.b.x - t.a.x) * (y - t.a.y) - (x - t.a.x) * (t.b.y - t.a.y)) / d;
                        var l0 = 1f - l1 - l2;
                        const float eps = -1e-5f;
                        if (l0 < eps || l1 < eps || l2 < eps) continue;
                        var z = l0 * t.ha + l1 * t.hb + l2 * t.hc;
                        if (z > height[i, j]) { height[i, j] = z; wall[i, j] = t.wall; }
                    }
                }
            }
        }

        /// <summary>The relief over samples (as Raster gives them; these are changed).</summary>
        public static Relief Build(float[,] height, bool[,] wall, float u0, float v0, float su, float sv,
            List<float> exactU, List<float> exactV, float cell, float spacing, out string why)
        {
            why = null;
            int nu = height.GetLength(0), nv = height.GetLength(1);
            float u1 = u0 + nu * su, v1 = v0 + nv * sv;
            float w = u1 - u0, hgt = v1 - v0;
            var relief = new Relief { samples = nu * nv };
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++)
                    if (float.IsNegativeInfinity(height[i, j])) relief.missing++;
            if (relief.missing > 0.01f * relief.samples) { why = "missing"; return null; }
            // Gaps (a sample between triangles) take a neighbour's height.
            for (var pass = 0; pass < 4 && relief.missing > 0; pass++)
                for (var i = 0; i < nu; i++)
                    for (var j = 0; j < nv; j++)
                    {
                        if (!float.IsNegativeInfinity(height[i, j])) continue;
                        int fi = i, fj = j;
                        if (i > 0 && !float.IsNegativeInfinity(height[i - 1, j])) fi = i - 1;
                        else if (j > 0 && !float.IsNegativeInfinity(height[i, j - 1])) fj = j - 1;
                        else if (i + 1 < nu && !float.IsNegativeInfinity(height[i + 1, j])) fi = i + 1;
                        else if (j + 1 < nv && !float.IsNegativeInfinity(height[i, j + 1])) fj = j + 1;
                        height[i, j] = height[fi, fj];
                        wall[i, j] = wall[fi, fj];
                    }
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++)
                    if (float.IsNegativeInfinity(height[i, j])) { why = "missing"; return null; }

            // Lines where the height jumps, or the wall starts, along enough of a column (u) or row (v).
            var step = stepCells * cell;
            var us = Lines(nu, nv, (i, j) => Mathf.Abs(height[i + 1, j] - height[i, j]) > step || wall[i + 1, j] != wall[i, j],
                u0, su, sv, cell, exactU, u1);
            var vs = Lines(nv, nu, (j, i) => Mathf.Abs(height[i, j + 1] - height[i, j]) > step || wall[i, j + 1] != wall[i, j],
                v0, sv, su, cell, exactV, v1);

            // Each cell's median, the medians clustered to levels (the wall's own at 0).
            int Sample(float at, float origin, float size, int n) => Mathf.Clamp(Mathf.FloorToInt((at - origin) / size), 0, n - 1);
            int cu = us.Count - 1, cv = vs.Count - 1;
            var level = new float[cu, cv];
            var inside = new bool[cu, cv];
            var values = new List<float>();
            for (var a = 0; a < cu; a++)
                for (var b = 0; b < cv; b++)
                {
                    values.Clear();
                    var walls = 0;
                    int ia = Sample(us[a], u0, su, nu), ib = Sample(us[a + 1], u0, su, nu);
                    int ja = Sample(vs[b], v0, sv, nv), jb = Sample(vs[b + 1], v0, sv, nv);
                    // Samples whose centres are inside the cell (at least the one nearest its middle).
                    for (var i = ia; i <= ib; i++)
                    {
                        var x = u0 + (i + 0.5f) * su;
                        if (x < us[a] || x > us[a + 1]) continue;
                        for (var j = ja; j <= jb; j++)
                        {
                            var y = v0 + (j + 0.5f) * sv;
                            if (y < vs[b] || y > vs[b + 1]) continue;
                            values.Add(height[i, j]);
                            if (wall[i, j]) walls++;
                        }
                    }
                    if (values.Count == 0)
                    {
                        int mi = Sample(0.5f * (us[a] + us[a + 1]), u0, su, nu), mj = Sample(0.5f * (vs[b] + vs[b + 1]), v0, sv, nv);
                        values.Add(height[mi, mj]);
                        if (wall[mi, mj]) walls++;
                    }
                    values.Sort();
                    level[a, b] = values[values.Count / 2];
                    inside[a, b] = 2 * walls < values.Count;
                }
            if (!OnePiece(inside, cu, cv)) { why = "pieces"; return null; }
            for (var a = 0; a < cu; a++)
                for (var b = 0; b < cv; b++)
                    if (!inside[a, b]) level[a, b] = 0f;
            Cluster(level, cu, cv, 0.5f * step);
            var thinU = Thin(us, cu, cv, level, inside, true, minWidthCells * cell);
            var thinV = Thin(vs, cv, cu, level, inside, false, minWidthCells * cell);

            // Lines with the same level either side go (not the given ones).
            var keepU = new bool[us.Count];
            var keepV = new bool[vs.Count];
            keepU[0] = keepU[us.Count - 1] = keepV[0] = keepV[vs.Count - 1] = true;
            for (var a = 1; a + 1 < us.Count; a++) keepU[a] = Near(exactU, us[a], 0.25f * su);
            for (var b = 1; b + 1 < vs.Count; b++) keepV[b] = Near(exactV, vs[b], 0.25f * sv);
            for (var a = 1; a < cu; a++)
                for (var b = 0; b < cv && !keepU[a]; b++)
                    if (level[a - 1, b] != level[a, b] || inside[a - 1, b] != inside[a, b]) keepU[a] = true;
            for (var b = 1; b < cv; b++)
                for (var a = 0; a < cu && !keepV[b]; a++)
                    if (level[a, b - 1] != level[a, b] || inside[a, b - 1] != inside[a, b]) keepV[b] = true;
            var fu = Keep(us, keepU, spacing, out var fromU);
            var fv = Keep(vs, keepV, spacing, out var fromV);
            var kept = new float[fu.Count - 1, fv.Count - 1];
            var keptInside = new bool[fu.Count - 1, fv.Count - 1];
            for (var a = 0; a + 1 < fu.Count; a++)
                for (var b = 0; b + 1 < fv.Count; b++)
                {
                    kept[a, b] = level[fromU[a], fromV[b]];
                    keptInside[a, b] = inside[fromU[a], fromV[b]];
                }
            if ((fu.Count - 1) * (fv.Count - 1) > maxCellsPerArea * w * hgt / (cell * cell) + 16) { why = "busy"; return null; }

            // The samples must agree with their cells (those touching a line aside).
            var fit = fitCells * cell;
            int counted = 0, good = 0;
            for (var i = 0; i < nu; i++)
            {
                var x = u0 + (i + 0.5f) * su;
                var a = Cell(fu, x);
                if (Mathf.Min(x - fu[a], fu[a + 1] - x) < su) continue;
                if (thinU != null && thinU[Cell(us, x)]) continue;
                for (var j = 0; j < nv; j++)
                {
                    var y = v0 + (j + 0.5f) * sv;
                    var b = Cell(fv, y);
                    if (!keptInside[a, b] || Mathf.Min(y - fv[b], fv[b + 1] - y) < sv) continue;
                    if (thinV != null && thinV[Cell(vs, y)]) continue;
                    counted++;
                    if (Mathf.Abs(height[i, j] - kept[a, b]) <= fit) good++;
                }
            }
            relief.fit = counted > 0 ? (float)good / counted : 0f;
            if (counted == 0 || relief.fit < fitShare) { why = "fit"; return null; }
            relief.us = fu;
            relief.vs = fv;
            relief.level = kept;
            relief.inside = keptInside;
            relief.outline = Outline(keptInside);
            if (relief.outline == null) { why = "pinched"; return null; }
            if (!Manifold(relief)) { why = "saddle"; relief = null; }
            return relief;
        }

        /// <summary>
        /// A hole whose steps could not be built (Build's null) as one box: its rectangle at the median
        /// depth of what is in it, every frame and bar left to the normal map. Null where the hole
        /// reaches the rectangle's edge.
        /// </summary>
        public static Relief Box(float[,] height, bool[,] wall, float u0, float v0, float su, float sv, float cell, float spacing)
        {
            int nu = height.GetLength(0), nv = height.GetLength(1);
            int ia = nu, ib = -1, ja = nv, jb = -1;
            var values = new List<float>();
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++)
                {
                    if (wall[i, j] || float.IsNegativeInfinity(height[i, j])) continue;
                    ia = Math.Min(ia, i); ib = Math.Max(ib, i); ja = Math.Min(ja, j); jb = Math.Max(jb, j);
                    values.Add(height[i, j]);
                }
            if (ib < 0 || ia == 0 || ja == 0 || ib == nu - 1 || jb == nv - 1) return null;
            values.Sort();
            var depth = values[values.Count / 2];
            if (Mathf.Abs(depth) < stepCells * cell) depth = 0f;
            var us = new List<float> { u0, u0 + ia * su, u0 + (ib + 1) * su, u0 + nu * su };
            var vs = new List<float> { v0, v0 + ja * sv, v0 + (jb + 1) * sv, v0 + nv * sv };
            var all = new[] { true, true, true, true };
            var fu = Keep(us, all, spacing, out var fromU);
            var fv = Keep(vs, all, spacing, out var fromV);
            var relief = new Relief { samples = nu * nv, fit = 1f, us = fu, vs = fv };
            relief.level = new float[fu.Count - 1, fv.Count - 1];
            relief.inside = new bool[fu.Count - 1, fv.Count - 1];
            for (var a = 0; a + 1 < fu.Count; a++)
                for (var b = 0; b + 1 < fv.Count; b++)
                {
                    relief.inside[a, b] = fromU[a] == 1 && fromV[b] == 1;
                    relief.level[a, b] = relief.inside[a, b] ? depth : 0f;
                }
            relief.outline = Outline(relief.inside);
            if (relief.outline == null || !Manifold(relief)) return null;
            return relief;
        }

        // The cells inside, holes in them filled (a patch of wall inside a window is the window's,
        // at the wall's level), are one piece clear of the rectangle's edge.
        static bool OnePiece(bool[,] inside, int cu, int cv)
        {
            // Outside: reached from the rectangle's edge.
            var outside = new bool[cu, cv];
            var stack = new Stack<(int, int)>();
            void Out(int a, int b)
            {
                if (a < 0 || b < 0 || a >= cu || b >= cv || inside[a, b] || outside[a, b]) return;
                outside[a, b] = true;
                stack.Push((a, b));
            }
            for (var a = 0; a < cu; a++) { Out(a, 0); Out(a, cv - 1); }
            for (var b = 0; b < cv; b++) { Out(0, b); Out(cu - 1, b); }
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                Out(a - 1, b); Out(a + 1, b); Out(a, b - 1); Out(a, b + 1);
            }
            var count = 0;
            (int, int) first = (-1, -1);
            for (var a = 0; a < cu; a++)
                for (var b = 0; b < cv; b++)
                {
                    inside[a, b] = !outside[a, b];
                    if (!inside[a, b]) continue;
                    count++;
                    if (first.Item1 < 0) first = (a, b);
                    if (a == 0 || b == 0 || a == cu - 1 || b == cv - 1) return false;
                }
            if (count == 0) return false;
            var seen = new bool[cu, cv];
            var reached = 0;
            stack.Push(first);
            seen[first.Item1, first.Item2] = true;
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                reached++;
                foreach (var (x, y) in new[] { (a - 1, b), (a + 1, b), (a, b - 1), (a, b + 1) })
                {
                    if (x < 0 || y < 0 || x >= cu || y >= cv || !inside[x, y] || seen[x, y]) continue;
                    seen[x, y] = true;
                    stack.Push((x, y));
                }
            }
            return reached == count;
        }

        // The inside cells' outline, counter-clockwise, every grid point on it; null where two
        // cells touch only at a corner.
        static List<Vector2Int> Outline(bool[,] inside)
        {
            int cu = inside.GetLength(0), cv = inside.GetLength(1);
            bool In(int a, int b) => a >= 0 && b >= 0 && a < cu && b < cv && inside[a, b];
            var next = new Dictionary<Vector2Int, Vector2Int>();
            var edges = 0;
            bool Add(Vector2Int p, Vector2Int q)
            {
                if (next.ContainsKey(p)) return false;
                next[p] = q;
                edges++;
                return true;
            }
            for (var a = 0; a < cu; a++)
                for (var b = 0; b < cv; b++)
                {
                    if (!inside[a, b]) continue;
                    if (!In(a, b - 1) && !Add(new Vector2Int(a, b), new Vector2Int(a + 1, b))) return null;
                    if (!In(a + 1, b) && !Add(new Vector2Int(a + 1, b), new Vector2Int(a + 1, b + 1))) return null;
                    if (!In(a, b + 1) && !Add(new Vector2Int(a + 1, b + 1), new Vector2Int(a, b + 1))) return null;
                    if (!In(a - 1, b) && !Add(new Vector2Int(a, b + 1), new Vector2Int(a, b))) return null;
                }
            if (edges == 0) return null;
            var loop = new List<Vector2Int>(edges);
            var start = new Vector2Int(-1, -1);
            foreach (var key in next.Keys) { start = key; break; }
            var at = start;
            do
            {
                loop.Add(at);
                if (!next.TryGetValue(at, out at) || loop.Count > edges) return null;
            } while (at != start);
            return loop.Count == edges ? loop : null;
        }

        // The strips between lines narrower than `width`, each given its wider neighbour's levels
        // (inside cells only; the thinnest first, so a run of thin steps goes to the wide one beside
        // it). Along u (strips are columns of level) or v (rows). Null: none.
        static bool[] Thin(List<float> lines, int count, int across, float[,] level, bool[,] inside, bool alongU, float width)
        {
            if (!(width > 0f) || count < 2) return null;
            float Get(int a, int b) => alongU ? level[a, b] : level[b, a];
            bool In(int a, int b) => alongU ? inside[a, b] : inside[b, a];
            void Set(int a, int b, float x) { if (alongU) level[a, b] = x; else level[b, a] = x; }
            var size = new float[count];
            for (var a = 0; a < count; a++) size[a] = lines[a + 1] - lines[a];
            var thin = new bool[count];
            var any = false;
            var order = new List<int>();
            for (var a = 0; a < count; a++) if (size[a] < width) order.Add(a);
            order.Sort((x, y) => size[x].CompareTo(size[y]));
            foreach (var a in order)
            {
                // The wider neighbour (counting a merged one as one strip with it).
                int left = a - 1, right = a + 1;
                float wl = left >= 0 ? size[left] : -1f, wr = right < count ? size[right] : -1f;
                var from = wl >= wr ? left : right;
                if (from < 0 || from >= count) continue;
                var changed = false;
                for (var b = 0; b < across; b++)
                {
                    if (!In(a, b)) continue;
                    var x = In(from, b) ? Get(from, b) : 0f;
                    if (x != Get(a, b)) { Set(a, b, x); changed = true; }
                }
                size[from] += size[a];
                thin[a] = changed;
                any |= changed;
            }
            return any ? thin : null;
        }

        static int Cell(List<float> lines, float x)
        {
            var lo = 0;
            var hi = lines.Count - 2;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (lines[mid] <= x) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        static bool Near(List<float> at, float x, float tolerance)
        {
            foreach (var a in at) if (Mathf.Abs(a - x) <= tolerance) return true;
            return false;
        }

        // Lines along one direction: the given ones, and the boundaries between samples i and i + 1
        // where jump(i, j) holds on enough of the crossing samples (the strongest of neighbouring ones).
        static List<float> Lines(int n, int across, Func<int, int, bool> jump, float origin, float size, float acrossSize,
            float cell, List<float> exact, float end)
        {
            var count = new int[Math.Max(0, n - 1)];
            for (var i = 0; i + 1 < n; i++)
                for (var j = 0; j < across; j++)
                    if (jump(i, j)) count[i]++;
            var minCount = Mathf.Max(2, Mathf.CeilToInt(minStepCells * cell / acrossSize));
            var at = new List<(float x, bool exact)> { (origin, true), (end, true) };
            foreach (var e in exact) if (e > origin && e < end) at.Add((e, true));
            for (var i = 0; i < count.Length; i++)
            {
                if (count[i] < minCount) continue;
                if (i > 0 && count[i - 1] > count[i]) continue;
                if (i + 1 < count.Length && count[i + 1] >= count[i]) continue;
                at.Add((origin + (i + 1) * size, false));
            }
            at.Sort((p, q) => p.x.CompareTo(q.x));
            // Within a sample of a given line: the given line.
            var lines = new List<float>();
            var lineExact = new List<bool>();
            foreach (var (x, isExact) in at)
            {
                var k = lines.Count - 1;
                if (k >= 0 && x - lines[k] < 0.75f * size)
                {
                    if (isExact && !lineExact[k]) { lines[k] = x; lineExact[k] = true; }
                    continue;
                }
                lines.Add(x);
                lineExact.Add(isExact);
            }
            if (lines[lines.Count - 1] < end) lines[lines.Count - 1] = end;
            return lines;
        }

        // The kept lines, and more where two are further apart than the spacing; from[k] is the
        // original cell each new cell lies in.
        static List<float> Keep(List<float> lines, bool[] keep, float spacing, out List<int> from)
        {
            var kept = new List<float>();
            var keptIndex = new List<int>();
            for (var i = 0; i < lines.Count; i++)
                if (keep[i]) { kept.Add(lines[i]); keptIndex.Add(i); }
            var result = new List<float>();
            from = new List<int>();
            for (var k = 0; k + 1 < kept.Count; k++)
            {
                var gap = kept[k + 1] - kept[k];
                var steps = Mathf.Max(1, Mathf.RoundToInt(gap / spacing));
                for (var s = 0; s < steps; s++)
                {
                    result.Add(kept[k] + gap * s / steps);
                    // Any original cell within: all have the one level (no line between was kept).
                    from.Add(keptIndex[k]);
                }
            }
            result.Add(kept[kept.Count - 1]);
            return result;
        }

        // Levels within `close` of each other become one (the one with 0 in it, 0: the wall).
        static void Cluster(float[,] level, int cu, int cv, float close)
        {
            var all = new List<float> { 0f };
            for (var a = 0; a < cu; a++)
                for (var b = 0; b < cv; b++) all.Add(level[a, b]);
            all.Sort();
            var centre = new List<float>();
            var bounds = new List<(float lo, float hi)>();
            var i = 0;
            while (i < all.Count)
            {
                var j = i;
                double sum = 0;
                var hasZero = false;
                while (j < all.Count && (j == i || all[j] - all[j - 1] <= close))
                {
                    sum += all[j];
                    if (all[j] == 0f) hasZero = true;
                    j++;
                }
                centre.Add(hasZero ? 0f : (float)(sum / (j - i)));
                bounds.Add((all[i], all[j - 1]));
                i = j;
            }
            for (var a = 0; a < cu; a++)
                for (var b = 0; b < cv; b++)
                {
                    var x = level[a, b];
                    for (var k = 0; k < bounds.Count; k++)
                        if (x >= bounds[k].lo && x <= bounds[k].hi) { level[a, b] = centre[k]; break; }
                }
        }

        // Cell (a, b)'s level; outside the rectangle, the wall's.
        static float At(Relief r, int a, int b) => In(r, a, b) ? r.level[a, b] : 0f;

        static bool In(Relief r, int a, int b) =>
            a >= 0 && b >= 0 && a < r.us.Count - 1 && b < r.vs.Count - 1 && r.inside[a, b];

        // The levels at each grid point: its cells', and every level of a wall's other end within
        // the wall's span, until none is added -- then each wall's edges are shared.
        static List<float>[,] Levels(Relief r)
        {
            int nu = r.us.Count, nv = r.vs.Count;
            var levels = new List<float>[nu, nv];
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++)
                {
                    var l = levels[i, j] = new List<float>(4);
                    void Add(float x) { if (!l.Contains(x)) l.Add(x); }
                    Add(At(r, i - 1, j - 1)); Add(At(r, i, j - 1)); Add(At(r, i - 1, j)); Add(At(r, i, j));
                }
            var changed = true;
            var guard = 0;
            while (changed && guard++ < 64)
            {
                changed = false;
                foreach (var (p, q, lo, hi) in Walls(r))
                {
                    changed |= Spread(levels[p.x, p.y], levels[q.x, q.y], lo, hi);
                    changed |= Spread(levels[q.x, q.y], levels[p.x, p.y], lo, hi);
                }
            }
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++) levels[i, j].Sort();
            return levels;
        }

        static bool Spread(List<float> from, List<float> to, float lo, float hi)
        {
            var changed = false;
            foreach (var x in from)
                if (x >= lo && x <= hi && !to.Contains(x)) { to.Add(x); changed = true; }
            return changed;
        }

        // The walls: grid segments p -> q between cells of different levels, with the side that
        // is higher (+1: the cell to the segment's left as p -> q runs, -1: its right).
        static IEnumerable<(Vector2Int p, Vector2Int q, float lo, float hi)> Walls(Relief r)
        {
            foreach (var w in WallsSided(r)) yield return (w.p, w.q, w.lo, w.hi);
        }

        static IEnumerable<(Vector2Int p, Vector2Int q, float lo, float hi, bool leftHigher)> WallsSided(Relief r)
        {
            int nu = r.us.Count, nv = r.vs.Count;
            // Along v, at u-line i, from row j to j + 1: left cell (i - 1, j), right cell (i, j).
            for (var i = 0; i < nu; i++)
                for (var j = 0; j + 1 < nv; j++)
                {
                    float left = At(r, i - 1, j), right = At(r, i, j);
                    if (left == right) continue;
                    yield return (new Vector2Int(i, j), new Vector2Int(i, j + 1), Mathf.Min(left, right), Mathf.Max(left, right), left > right);
                }
            // Along u, at v-line j, from column i to i + 1: below (i, j - 1) is to the right, above (i, j) to the left.
            for (var j = 0; j < nv; j++)
                for (var i = 0; i + 1 < nu; i++)
                {
                    float below = At(r, i, j - 1), above = At(r, i, j);
                    if (below == above) continue;
                    yield return (new Vector2Int(i, j), new Vector2Int(i + 1, j), Mathf.Min(below, above), Mathf.Max(below, above), above > below);
                }
        }

        // Every stretch of every grid point's column between two of its levels is in two walls or none.
        static bool Manifold(Relief r)
        {
            var levels = Levels(r);
            var uses = new Dictionary<(int, int, float), int>();
            foreach (var (p, q, lo, hi) in Walls(r))
                foreach (var g in new[] { p, q })
                {
                    var l = levels[g.x, g.y];
                    for (var k = 0; k + 1 < l.Count; k++)
                    {
                        if (l[k] < lo || l[k + 1] > hi) continue;
                        var key = (g.x, g.y, l[k]);
                        uses[key] = uses.TryGetValue(key, out var n) ? n + 1 : 1;
                    }
                }
            foreach (var n in uses.Values) if (n != 2) return false;
            return true;
        }

        /// <summary>
        /// The relief's quads. border(i, j) is the index of the grid point (i, j) on the hole's
        /// outline at the wall's level (it is the wall's too); point(q, h) adds a point at frame
        /// position q, height h, and returns its index.
        /// </summary>
        public static List<int[]> Quads(Relief r, Func<int, int, int> border, Func<Vector2, float, int> point)
        {
            int nu = r.us.Count, nv = r.vs.Count;
            var levels = Levels(r);
            var index = new Dictionary<(int, int, float), int>();
            int At3(int i, int j, float h)
            {
                if (index.TryGetValue((i, j, h), out var v)) return v;
                var onBorder = !In(r, i - 1, j - 1) || !In(r, i, j - 1) || !In(r, i - 1, j) || !In(r, i, j);
                v = onBorder && h == 0f ? border(i, j) : point(new Vector2(r.us[i], r.vs[j]), h);
                index[(i, j, h)] = v;
                return v;
            }
            var faces = new List<int[]>();
            // Tops, counter-clockwise seen from outside.
            for (var a = 0; a + 1 < nu; a++)
                for (var b = 0; b + 1 < nv; b++)
                {
                    if (!r.inside[a, b]) continue;
                    var h = r.level[a, b];
                    faces.Add(new[] { At3(a, b, h), At3(a + 1, b, h), At3(a + 1, b + 1, h), At3(a, b + 1, h) });
                }
            // Walls, one quad per pair of levels, facing the lower side.
            foreach (var (p, q, lo, hi, leftHigher) in WallsSided(r))
            {
                var l = levels[p.x, p.y];
                for (var k = 0; k + 1 < l.Count; k++)
                {
                    if (l[k] < lo || l[k + 1] > hi) continue;
                    float z0 = l[k], z1 = l[k + 1];
                    // The higher cell to the left of p -> q: the wall faces right, p@z0, q@z0, q@z1, p@z1.
                    if (leftHigher)
                        faces.Add(new[] { At3(p.x, p.y, z0), At3(q.x, q.y, z0), At3(q.x, q.y, z1), At3(p.x, p.y, z1) });
                    else
                        faces.Add(new[] { At3(p.x, p.y, z1), At3(q.x, q.y, z1), At3(q.x, q.y, z0), At3(p.x, p.y, z0) });
                }
            }
            return faces;
        }
    }
}
