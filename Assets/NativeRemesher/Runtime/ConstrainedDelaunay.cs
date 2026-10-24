// A constrained Delaunay triangulation of points in the plane, kept to the inside of the given
// segments (crossing a segment from outside goes in, crossing another goes out again: holes).
//
// Bowyer-Watson insertion with a walking search, segments recovered by flipping the edges that
// cross them (Sloan), inside told from outside by the number of segments crossed from the
// enclosing triangle. Grid points are cocircular four at a time; the in-circle test is strict,
// which keeps every insertion's cavity star-shaped and any of the equal choices valid.

using System;
using System.Collections.Generic;

namespace NativeRemesher
{
    internal static class ConstrainedDelaunay
    {
        /// <summary>Diagnostics: why the last triangulation on this thread gave up.</summary>
        [ThreadStatic] public static string lastFailure;

        /// <summary>
        /// Triangles (indices into the points, counter-clockwise) of the area inside the segments;
        /// false when the points or segments defeat it (a segment through a point, segments that
        /// cross, a segment not recovered). Points closer than a millionth of the extent are one;
        /// a triangle uses the first of them.
        /// </summary>
        public static bool Triangulate(IList<double> xs, IList<double> ys, IList<(int a, int b)> segments, out List<int> triangles)
        {
            triangles = new List<int>();
            lastFailure = null;
            var n = xs.Count;
            if (n < 3) return false;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (var i = 0; i < n; i++)
            {
                minX = Math.Min(minX, xs[i]); maxX = Math.Max(maxX, xs[i]);
                minY = Math.Min(minY, ys[i]); maxY = Math.Max(maxY, ys[i]);
            }
            var extent = Math.Max(maxX - minX, maxY - minY);
            if (!(extent > 0)) return false;
            // Normalised to the unit square, three far points round it.
            var x = new double[n + 3];
            var y = new double[n + 3];
            for (var i = 0; i < n; i++) { x[i] = (xs[i] - minX) / extent; y[i] = (ys[i] - minY) / extent; }
            x[n] = -100; y[n] = -100;
            x[n + 1] = 100; y[n + 1] = -100;
            x[n + 2] = 0.5; y[n + 2] = 100;
            var mesh = new Mesh(x, y, 2 * n + 8);
            mesh.Add(n, n + 1, n + 2);

            // Inserted in a spatially coherent order (rows of a coarse grid, snaking) so each walk is short.
            var order = new int[n];
            for (var i = 0; i < n; i++) order[i] = i;
            var cells = Math.Max(1, (int)Math.Sqrt(n / 4.0));
            var key = new long[n];
            for (var i = 0; i < n; i++)
            {
                var cy = Math.Min(cells - 1, (int)(y[i] * cells));
                var cx = Math.Min(cells - 1, (int)(x[i] * cells));
                if ((cy & 1) == 1) cx = cells - 1 - cx;
                key[i] = (long)cy * cells + cx;
            }
            Array.Sort(key, order);
            var same = new int[n];
            for (var i = 0; i < n; i++) same[i] = i;
            const double merge = 1e-6;
            var last = 0;
            foreach (var p in order)
            {
                var t = mesh.Locate(x[p], y[p], last, out var onVertex);
                if (t < 0) { lastFailure = $"locate {p}"; return false; }
                if (onVertex >= 0 && (Math.Abs(x[onVertex] - x[p]) < merge && Math.Abs(y[onVertex] - y[p]) < merge))
                {
                    same[p] = onVertex;
                    continue;
                }
                // A point within the merge distance of one inserted but not at a corner of t.
                var near = mesh.NearCorner(t, x[p], y[p], merge);
                if (near >= 0) { same[p] = near; continue; }
                last = mesh.Insert(p, t);
                if (last < 0) { lastFailure = $"insert {p}"; return false; }
            }

            // Segments, as edges.
            var constrained = new HashSet<long>();
            foreach (var (a0, b0) in segments)
            {
                int a = same[a0], b = same[b0];
                if (a == b) continue;
                if (!mesh.Recover(a, b, constrained)) { lastFailure = $"recover {a0}-{b0} ({a}-{b})"; return false; }
                constrained.Add(Key(a, b));
            }

            // Inside: crossings of segments counted from the outer triangles.
            var depth = new int[mesh.count];
            for (var t = 0; t < mesh.count; t++) depth[t] = -1;
            var queue = new Queue<int>();
            for (var t = 0; t < mesh.count; t++)
            {
                if (!mesh.alive[t]) continue;
                for (var k = 0; k < 3; k++)
                    if (mesh.v[3 * t + k] >= n) { depth[t] = 0; queue.Enqueue(t); break; }
            }
            var next = new Queue<int>();
            var level = 0;
            while (queue.Count > 0)
            {
                // Breadth-first within a level, segments crossed into the next.
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    for (var k = 0; k < 3; k++)
                    {
                        var u = mesh.nb[3 * t + k];
                        if (u < 0 || depth[u] >= 0) continue;
                        var crossing = constrained.Contains(Key(mesh.v[3 * t + k], mesh.v[3 * t + (k + 1) % 3]));
                        if (crossing) { if (depth[u] < 0) next.Enqueue(u); continue; }
                        depth[u] = level;
                        queue.Enqueue(u);
                    }
                }
                level++;
                while (next.Count > 0)
                {
                    var u = next.Dequeue();
                    if (depth[u] >= 0) continue;
                    depth[u] = level;
                    queue.Enqueue(u);
                }
            }
            for (var t = 0; t < mesh.count; t++)
            {
                if (!mesh.alive[t] || (depth[t] & 1) == 0) continue;
                int a = mesh.v[3 * t], b = mesh.v[3 * t + 1], c = mesh.v[3 * t + 2];
                if (a >= n || b >= n || c >= n) { lastFailure = "inside reaches the far points"; return false; }
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
            }
            return triangles.Count > 0;
        }

        /// <summary>
        /// Points inserted into a triangulation that is already there (counter-clockwise triangles
        /// of points 0..n-1, holes and slits and all), then made Delaunay by flipping every edge
        /// but the fixed ones and the open ones. Points [first, n) are inserted where they fall in
        /// a triangle; one falling on no triangle, or on a fixed or open edge, is left out
        /// (inserted[i - first] false). The triangles are replaced.
        /// </summary>
        public static bool Refine(IList<double> xs, IList<double> ys, List<int> triangles, HashSet<long> fixedEdges, int first,
            out bool[] inserted)
        {
            lastFailure = null;
            var n = xs.Count;
            inserted = new bool[Math.Max(0, n - first)];
            var x = new double[n];
            var y = new double[n];
            for (var i = 0; i < n; i++) { x[i] = xs[i]; y[i] = ys[i]; }
            var mesh = new Mesh(x, y, triangles.Count / 3 + 2 * (n - first) + 8);
            var directed = new Dictionary<long, (int t, int k)>(triangles.Count);
            for (var i = 0; i + 2 < triangles.Count; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                if (!(Orient(x[a], y[a], x[b], y[b], x[c], y[c]) > 0)) { lastFailure = "a triangle not counter-clockwise"; return false; }
                var t = mesh.Add(a, b, c);
                for (var k = 0; k < 3; k++)
                {
                    int p = mesh.v[3 * t + k], q = mesh.v[3 * t + (k + 1) % 3];
                    var key = ((long)p << 32) | (uint)q;
                    if (directed.ContainsKey(key)) { lastFailure = "an edge twice the same way"; return false; }
                    directed[key] = (t, k);
                    if (directed.TryGetValue(((long)q << 32) | (uint)p, out var twin))
                    {
                        mesh.nb[3 * t + k] = twin.t;
                        mesh.nb[3 * twin.t + twin.k] = t;
                    }
                }
            }
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (var i = 0; i < n; i++)
            {
                minX = Math.Min(minX, x[i]); maxX = Math.Max(maxX, x[i]);
                minY = Math.Min(minY, y[i]); maxY = Math.Max(maxY, y[i]);
            }
            var extent = Math.Max(maxX - minX, maxY - minY);
            if (!(extent > 0)) return false;
            var last = 0;
            for (var p = first; p < n; p++)
            {
                var t = mesh.Locate(x[p], y[p], last, out _);
                if (t < 0 || !mesh.Holds(t, x[p], y[p])) t = mesh.Search(x[p], y[p]);
                if (t < 0) continue;
                if (!mesh.SplitAt(t, p, fixedEdges, 1e-9 * extent)) continue;
                inserted[p - first] = true;
                last = t;
            }
            mesh.Legalise(fixedEdges);
            triangles.Clear();
            for (var t = 0; t < mesh.count; t++)
            {
                if (!mesh.alive[t]) continue;
                triangles.Add(mesh.v[3 * t]); triangles.Add(mesh.v[3 * t + 1]); triangles.Add(mesh.v[3 * t + 2]);
            }
            return true;
        }

        static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        static double Orient(double ax, double ay, double bx, double by, double cx, double cy) =>
            (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

        sealed class Mesh
        {
            readonly double[] x, y;
            // Triangle t: corners v[3t..3t+2] counter-clockwise; nb[3t+k] across the edge v[3t+k] -> v[3t+k+1].
            public int[] v, nb;
            public bool[] alive;
            public int count;
            readonly List<int> m_Cavity = new List<int>();
            readonly List<(int a, int b, int outside)> m_Rim = new List<(int, int, int)>();
            readonly Stack<int> m_Free = new Stack<int>();
            int[] m_Mark;
            int m_Stamp;
            readonly int[] m_Corner;

            public Mesh(double[] x, double[] y, int capacity)
            {
                this.x = x; this.y = y;
                v = new int[3 * capacity];
                nb = new int[3 * capacity];
                alive = new bool[capacity];
                m_Mark = new int[capacity];
                m_Corner = new int[x.Length];
                for (var i = 0; i < m_Corner.Length; i++) m_Corner[i] = -1;
            }

            int New()
            {
                if (m_Free.Count > 0) { var f = m_Free.Pop(); alive[f] = true; return f; }
                if (count == alive.Length)
                {
                    var size = 2 * alive.Length;
                    Array.Resize(ref v, 3 * size);
                    Array.Resize(ref nb, 3 * size);
                    Array.Resize(ref alive, size);
                    Array.Resize(ref m_Mark, size);
                }
                alive[count] = true;
                return count++;
            }

            public int Add(int a, int b, int c)
            {
                var t = New();
                v[3 * t] = a; v[3 * t + 1] = b; v[3 * t + 2] = c;
                nb[3 * t] = nb[3 * t + 1] = nb[3 * t + 2] = -1;
                m_Corner[a] = m_Corner[b] = m_Corner[c] = t;
                return t;
            }

            // The triangle holding the point, walked to from `start`; onVertex is a corner it lies on.
            public int Locate(double px, double py, int start, out int onVertex)
            {
                onVertex = -1;
                var t = start;
                if (t < 0 || t >= count || !alive[t]) t = FirstAlive();
                for (var steps = 0; steps < 4 * count + 16; steps++)
                {
                    var moved = false;
                    for (var k = 0; k < 3; k++)
                    {
                        int a = v[3 * t + k], b = v[3 * t + (k + 1) % 3];
                        if (Orient(x[a], y[a], x[b], y[b], px, py) < 0)
                        {
                            var u = nb[3 * t + k];
                            if (u < 0) return -1;
                            t = u;
                            moved = true;
                            break;
                        }
                    }
                    if (moved) continue;
                    for (var k = 0; k < 3; k++)
                    {
                        var c = v[3 * t + k];
                        if (x[c] == px && y[c] == py) onVertex = c;
                    }
                    return t;
                }
                return -1;
            }

            int FirstAlive()
            {
                for (var t = 0; t < count; t++) if (alive[t]) return t;
                return -1;
            }

            public int NearCorner(int t, double px, double py, double merge)
            {
                for (var k = 0; k < 3; k++)
                {
                    var c = v[3 * t + k];
                    if (Math.Abs(x[c] - px) < merge && Math.Abs(y[c] - py) < merge) return c;
                }
                return -1;
            }

            bool InCircle(int t, double px, double py)
            {
                int a = v[3 * t], b = v[3 * t + 1], c = v[3 * t + 2];
                double adx = x[a] - px, ady = y[a] - py, bdx = x[b] - px, bdy = y[b] - py, cdx = x[c] - px, cdy = y[c] - py;
                var det = (adx * adx + ady * ady) * (bdx * cdy - cdx * bdy)
                          - (bdx * bdx + bdy * bdy) * (adx * cdy - cdx * ady)
                          + (cdx * cdx + cdy * cdy) * (adx * bdy - bdx * ady);
                return det > 1e-18;
            }

            // Bowyer-Watson: the triangles whose circles hold p, from t outward, replaced by a fan.
            public int Insert(int p, int t)
            {
                double px = x[p], py = y[p];
                m_Stamp++;
                m_Cavity.Clear();
                m_Rim.Clear();
                var stack = new Stack<int>();
                stack.Push(t);
                m_Mark[t] = m_Stamp;
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    m_Cavity.Add(c);
                    for (var k = 0; k < 3; k++)
                    {
                        var u = nb[3 * c + k];
                        if (u >= 0 && m_Mark[u] != m_Stamp && InCircle(u, px, py)) { m_Mark[u] = m_Stamp; stack.Push(u); }
                    }
                }
                foreach (var c in m_Cavity)
                    for (var k = 0; k < 3; k++)
                    {
                        var u = nb[3 * c + k];
                        if (u >= 0 && m_Mark[u] == m_Stamp) continue;
                        m_Rim.Add((v[3 * c + k], v[3 * c + (k + 1) % 3], u));
                    }
                // Every rim edge must see p on its left (a star-shaped cavity).
                foreach (var (a, b, _) in m_Rim)
                    if (!(Orient(x[a], y[a], x[b], y[b], px, py) > 0)) return -1;
                foreach (var c in m_Cavity) { alive[c] = false; m_Free.Push(c); }
                var made = new int[m_Rim.Count];
                var from = new Dictionary<int, int>(m_Rim.Count);
                for (var i = 0; i < m_Rim.Count; i++)
                {
                    var (a, b, outside) = m_Rim[i];
                    var f = Add(a, b, p);
                    made[i] = f;
                    nb[3 * f] = outside;
                    if (outside >= 0)
                        for (var k = 0; k < 3; k++)
                            if (v[3 * outside + k] == b && v[3 * outside + (k + 1) % 3] == a) nb[3 * outside + k] = f;
                    from[a] = f;
                }
                // Fan neighbours: the triangle on a -> b -> p meets the one starting at b.
                for (var i = 0; i < m_Rim.Count; i++)
                {
                    var f = made[i];
                    var b = v[3 * f + 1];
                    var a = v[3 * f];
                    if (!from.TryGetValue(b, out var after)) return -1;
                    nb[3 * f + 1] = after;
                    nb[3 * after + 2] = f;
                }
                return made[0];
            }

            public bool Holds(int t, double px, double py)
            {
                for (var k = 0; k < 3; k++)
                {
                    int a = v[3 * t + k], b = v[3 * t + (k + 1) % 3];
                    if (Orient(x[a], y[a], x[b], y[b], px, py) < 0) return false;
                }
                return true;
            }

            public int Search(double px, double py)
            {
                for (var t = 0; t < count; t++) if (alive[t] && Holds(t, px, py)) return t;
                return -1;
            }

            // p into t: three triangles, or four round an edge it lies on (not a fixed or open one).
            public bool SplitAt(int t, int p, HashSet<long> fixedEdges, double eps)
            {
                double px = x[p], py = y[p];
                var on = -1;
                for (var k = 0; k < 3; k++)
                {
                    int a = v[3 * t + k], b = v[3 * t + (k + 1) % 3];
                    double dx = x[b] - x[a], dy = y[b] - y[a];
                    var len = Math.Sqrt(dx * dx + dy * dy);
                    if (Math.Abs(Orient(x[a], y[a], x[b], y[b], px, py)) <= eps * len) on = k;
                }
                if (on < 0)
                {
                    int a = v[3 * t], b = v[3 * t + 1], c = v[3 * t + 2];
                    int nab = nb[3 * t], nbc = nb[3 * t + 1], nca = nb[3 * t + 2];
                    // t becomes (a, b, p); two more, (b, c, p) and (c, a, p).
                    v[3 * t + 2] = p;
                    var t1 = Add(b, c, p);
                    var t2 = Add(c, a, p);
                    m_Corner[a] = t; m_Corner[b] = t;
                    nb[3 * t] = nab; nb[3 * t + 1] = t1; nb[3 * t + 2] = t2;
                    nb[3 * t1] = nbc; nb[3 * t1 + 1] = t2; nb[3 * t1 + 2] = t;
                    nb[3 * t2] = nca; nb[3 * t2 + 1] = t; nb[3 * t2 + 2] = t1;
                    Relink(nbc, b, c, t1);
                    Relink(nca, c, a, t2);
                    return true;
                }
                {
                    int a = v[3 * t + on], b = v[3 * t + (on + 1) % 3], c = v[3 * t + (on + 2) % 3];
                    var u = nb[3 * t + on];
                    if (u < 0 || fixedEdges.Contains(Key(a, b))) return false;
                    var d = Opposite(u, a, b);
                    if (d < 0) return false;
                    // Outer neighbours: of t across b -> c and c -> a; of u across a -> d and d -> b.
                    var tbc = nb[3 * t + (on + 1) % 3];
                    var tca = nb[3 * t + (on + 2) % 3];
                    var j = Corner(u, b);
                    var uad = nb[3 * u + (j + 1) % 3];
                    var udb = nb[3 * u + (j + 2) % 3];
                    // t = (c, a, p), t1 = (b, c, p); u = (a, d, p), u1 = (d, b, p).
                    v[3 * t] = c; v[3 * t + 1] = a; v[3 * t + 2] = p;
                    v[3 * u] = a; v[3 * u + 1] = d; v[3 * u + 2] = p;
                    var t1 = Add(b, c, p);
                    var u1 = Add(d, b, p);
                    m_Corner[a] = t; m_Corner[c] = t; m_Corner[d] = u;
                    nb[3 * t] = tca; nb[3 * t + 1] = u; nb[3 * t + 2] = t1;
                    nb[3 * u] = uad; nb[3 * u + 1] = u1; nb[3 * u + 2] = t;
                    nb[3 * t1] = tbc; nb[3 * t1 + 1] = t; nb[3 * t1 + 2] = u1;
                    nb[3 * u1] = udb; nb[3 * u1 + 1] = t1; nb[3 * u1 + 2] = u;
                    Relink(tbc, b, c, t1);
                    Relink(udb, d, b, u1);
                    return true;
                }
            }

            // Lawson's flips to the constrained Delaunay triangulation.
            public void Legalise(HashSet<long> fixedEdges)
            {
                var queue = new Queue<(int p, int q)>();
                for (var t = 0; t < count; t++)
                {
                    if (!alive[t]) continue;
                    for (var k = 0; k < 3; k++)
                    {
                        int p = v[3 * t + k], q = v[3 * t + (k + 1) % 3];
                        if (p < q && nb[3 * t + k] >= 0) queue.Enqueue((p, q));
                    }
                }
                var guard = 0;
                var limit = 64 * count + 1000;
                while (queue.Count > 0 && ++guard < limit)
                {
                    var (p0, q0) = queue.Dequeue();
                    if (fixedEdges.Contains(Key(p0, q0))) continue;
                    if (!FindEdge(p0, q0, out var t, out var k)) continue;
                    var u = nb[3 * t + k];
                    if (u < 0) continue;
                    int p = v[3 * t + k], q = v[3 * t + (k + 1) % 3], r = v[3 * t + (k + 2) % 3];
                    var s = Opposite(u, p, q);
                    if (s < 0 || !InCircle(t, x[s], y[s])) continue;
                    var convex = Orient(x[r], y[r], x[p], y[p], x[s], y[s]) > 0 && Orient(x[s], y[s], x[q], y[q], x[r], y[r]) > 0;
                    if (!convex) continue;
                    Flip(t, k, u, r, s, p, q);
                    queue.Enqueue((Math.Min(r, p), Math.Max(r, p)));
                    queue.Enqueue((Math.Min(p, s), Math.Max(p, s)));
                    queue.Enqueue((Math.Min(s, q), Math.Max(s, q)));
                    queue.Enqueue((Math.Min(q, r), Math.Max(q, r)));
                }
            }

            // Make a -> b an edge, flipping the edges that cross it. Edges are held by their
            // points, not their triangles: a flip rewrites two triangles in place.
            public bool Recover(int a, int b, HashSet<long> fixedEdges)
            {
                var queue = new Queue<(int p, int q)>();
                for (var round = 0; round < 8; round++)
                {
                    if (HasEdge(a, b)) return true;
                    queue.Clear();
                    if (!Crossings(a, b, queue)) return false;
                    var guard = 0;
                    var limit = 50 * queue.Count + 1000;
                    while (queue.Count > 0)
                    {
                        if (++guard > limit) break;
                        var (p, q) = queue.Dequeue();
                        if (!FindEdge(p, q, out var t, out var k)) continue;
                        // Held as p -> q in t.
                        p = v[3 * t + k];
                        q = v[3 * t + (k + 1) % 3];
                        if (!Crosses(a, b, p, q)) continue;
                        // Two segments crossing: no triangulation has both.
                        if (fixedEdges.Contains(Key(p, q))) return false;
                        var u = nb[3 * t + k];
                        if (u < 0) return false;
                        var r = v[3 * t + (k + 2) % 3];
                        var s = Opposite(u, p, q);
                        if (s < 0) return false;
                        // Convex quad p s q r (strictly), else wait for others to flip first.
                        var convex = Orient(x[r], y[r], x[p], y[p], x[s], y[s]) > 0 && Orient(x[s], y[s], x[q], y[q], x[r], y[r]) > 0;
                        if (!convex) { queue.Enqueue((p, q)); continue; }
                        Flip(t, k, u, r, s, p, q);
                        if (r != a && r != b && s != a && s != b && Crosses(a, b, r, s)) queue.Enqueue((r, s));
                    }
                }
                return HasEdge(a, b);
            }

            bool EdgeIndex(int t, int p, int q, out int k)
            {
                for (k = 0; k < 3; k++)
                    if (v[3 * t + k] == p && v[3 * t + (k + 1) % 3] == q) return true;
                k = -1;
                return false;
            }

            int Opposite(int u, int p, int q)
            {
                for (var k = 0; k < 3; k++)
                    if (v[3 * u + k] == q && v[3 * u + (k + 1) % 3] == p) return v[3 * u + (k + 2) % 3];
                return -1;
            }

            bool FindEdge(int p, int q, out int t, out int k)
            {
                // Round p from the triangle last made with it, both ways.
                var start = p < m_Corner.Length ? m_Corner[p] : -1;
                if (start >= 0 && start < count && alive[start] && Corner(start, p) >= 0)
                {
                    for (var side = 0; side < 2; side++)
                    {
                        var cur = start;
                        for (var steps = 0; steps < 1024 && cur >= 0; steps++)
                        {
                            var i = Corner(cur, p);
                            if (i < 0) break;
                            if (v[3 * cur + (i + 1) % 3] == q) { t = cur; k = i; return true; }
                            if (v[3 * cur + (i + 2) % 3] == q) { t = cur; k = (i + 2) % 3; return true; }
                            // Across the edge into p (clockwise... one way), or out of p (the other).
                            cur = side == 0 ? nb[3 * cur + (i + 2) % 3] : nb[3 * cur + i];
                            if (cur == start) break;
                        }
                    }
                }
                for (t = 0; t < count; t++)
                {
                    if (!alive[t]) continue;
                    if (EdgeIndex(t, p, q, out k)) return true;
                    if (EdgeIndex(t, q, p, out k)) return true;
                }
                t = -1; k = -1;
                return false;
            }

            int Corner(int t, int p)
            {
                for (var i = 0; i < 3; i++) if (v[3 * t + i] == p) return i;
                return -1;
            }

            bool HasEdge(int a, int b) => FindEdge(a, b, out _, out _);

            bool Crosses(int a, int b, int p, int q)
            {
                var d1 = Orient(x[a], y[a], x[b], y[b], x[p], y[p]);
                var d2 = Orient(x[a], y[a], x[b], y[b], x[q], y[q]);
                var d3 = Orient(x[p], y[p], x[q], y[q], x[a], y[a]);
                var d4 = Orient(x[p], y[p], x[q], y[q], x[b], y[b]);
                return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
            }

            // Every edge crossing a -> b, once each; false when a point lies on the segment.
            bool Crossings(int a, int b, Queue<(int p, int q)> into)
            {
                var seen = new HashSet<long>();
                for (var t = 0; t < count; t++)
                {
                    if (!alive[t]) continue;
                    for (var k = 0; k < 3; k++)
                    {
                        int p = v[3 * t + k], q = v[3 * t + (k + 1) % 3];
                        if (p == a || p == b || q == a || q == b) continue;
                        if (!Crosses(a, b, p, q))
                        {
                            // A point on the open segment blocks it.
                            if (OnSegment(a, b, p)) return false;
                            continue;
                        }
                        if (seen.Add(Key(p, q))) into.Enqueue((p, q));
                    }
                }
                return true;
            }

            bool OnSegment(int a, int b, int p)
            {
                var o = Orient(x[a], y[a], x[b], y[b], x[p], y[p]);
                double dx = x[b] - x[a], dy = y[b] - y[a];
                var len2 = dx * dx + dy * dy;
                if (Math.Abs(o) > 1e-12 * Math.Sqrt(len2)) return false;
                var s = ((x[p] - x[a]) * dx + (y[p] - y[a]) * dy) / len2;
                return s > 1e-9 && s < 1 - 1e-9;
            }

            // The edge p -> q of t (k) and q -> p of u become r -> s: t = (r, p, s), u = (s, q, r).
            void Flip(int t, int k, int u, int r, int s, int p, int q)
            {
                // Outer neighbours before the flip.
                var tqr = nb[3 * t + (k + 1) % 3]; // q -> r
                var trp = nb[3 * t + (k + 2) % 3]; // r -> p
                EdgeIndex(u, q, p, out var j);
                var ups = nb[3 * u + (j + 1) % 3]; // p -> s
                var usq = nb[3 * u + (j + 2) % 3]; // s -> q
                v[3 * t] = r; v[3 * t + 1] = p; v[3 * t + 2] = s;
                nb[3 * t] = trp; nb[3 * t + 1] = ups; nb[3 * t + 2] = u;
                v[3 * u] = s; v[3 * u + 1] = q; v[3 * u + 2] = r;
                nb[3 * u] = usq; nb[3 * u + 1] = tqr; nb[3 * u + 2] = t;
                Relink(ups, p, s, t);
                Relink(tqr, q, r, u);
                m_Corner[r] = m_Corner[p] = m_Corner[s] = t;
                m_Corner[q] = u;
            }

            // The neighbour across a's edge b -> a (seen from it) now points to `to`.
            void Relink(int w, int a, int b, int to)
            {
                if (w < 0) return;
                for (var k = 0; k < 3; k++)
                    if (v[3 * w + k] == b && v[3 * w + (k + 1) % 3] == a) { nb[3 * w + k] = to; return; }
            }
        }
    }
}
