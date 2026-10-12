// Quads read straight off the integer grid of a seamless parametrisation.
//
// The integer grid map hands the parametrisation's lattice to QuadriFlow's extraction, which
// collapses the mesh onto it edge by edge: it rounds what the parametrisation already made exact,
// repairs what that rounding breaks, and places every output vertex by averaging. Along a crease
// that is where the notches come from -- the crease is an exact grid line in (u, v) and the output
// still lands beside it. This is the extraction the parametrisation methods use instead (Ebke,
// Bommes, Chen and Kobbelt, "QEx: Robust Quad Mesh Extraction", SIGGRAPH Asia 2013), in its plain
// form:
//
//   vertices   every integer point (i, j) of every triangle's chart, found once: inside a face, on
//              an edge (owned by one side) or at a mesh vertex. Its position is the barycentric
//              point on the input surface, so a vertex on a pinned crease lies ON the crease.
//   edges      from each vertex, one ray per lattice direction that leaves it into some triangle,
//              walked across the triangles -- through the seams' transitions -- until it reaches
//              the next integer point. A ray along a mesh edge is taken to run infinitesimally to
//              its left, which decides every tie the same way on both sides.
//   faces      walking the edges, turning right at every vertex.
//
// Folded triangles are skipped: no vertex is made in one and a ray that meets one stops. What they
// leave is a hole, which the caller's capping closes as it does every other.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class ParametrizationTraceStats
    {
        public int faces, foldedFaces;
        public int vertices, insideVertices, edgeVertices, cornerVertices;
        public int nonIntegerTransitions;
        public int rays, traced, hitBoundary, hitFold, missedTarget, tooLong, noReverse, unpaired;
        public int polygons, quads, triangles, larger, brokenWalks;
        public long ms;
        public string debug = "";

        public override string ToString() => debug +
            $"tris={faces} folded={foldedFaces} verts={vertices} (in {insideVertices} edge {edgeVertices} corner {cornerVertices}) " +
            $"nonIntT={nonIntegerTransitions} rays={rays} traced={traced} boundary={hitBoundary} fold={hitFold} " +
            $"missed={missedTarget} long={tooLong} noReverse={noReverse} unpaired={unpaired} " +
            $"polys={polygons} quads={quads} tris={triangles} larger={larger} broken={brokenWalks} ms={ms}";
    }

    public static class ParametrizationTrace
    {
        /// <summary>Triangles a ray may cross before it is given up.</summary>
        public static int maxSteps = 4000;

        /// <summary>Distance, in cells, under which a point counts as on a line.</summary>
        public static double onLine = 1e-7;

        // Quarter-turn k applied to (x, y).
        static void Rot(int k, double x, double y, out double rx, out double ry)
        {
            switch (k & 3)
            {
                case 0: rx = x; ry = y; break;
                case 1: rx = -y; ry = x; break;
                case 2: rx = -x; ry = -y; break;
                default: rx = y; ry = -x; break;
            }
        }

        static readonly int[] DirX = { 1, 0, -1, 0 };
        static readonly int[] DirY = { 0, 1, 0, -1 };

        static int DirIndex(double dx, double dy)
        {
            if (Math.Abs(dx) > Math.Abs(dy)) return dx > 0 ? 0 : 2;
            return dy > 0 ? 1 : 3;
        }

        static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

        struct Registration
        {
            public int face;
            public int i, j;
        }

        sealed class QuadVertex
        {
            public Vector3 position;
            public Vector3 normal;
            public int kind;                  // 0 inside, 1 edge, 2 mesh vertex
            public int meshVertex = -1;
            public int halfEdge = -1;         // for an edge vertex: the owner's half-edge
            public readonly List<Registration> at = new List<Registration>(2);
            // Ray slots in counter-clockwise order: (face, direction in that face's chart).
            public readonly List<(int face, int dir)> slots = new List<(int, int)>(4);
        }

        /// <param name="uv">Per triangle corner (3f + k) its chart coordinates, (u, v) interleaved.</param>
        public static void Extract(RemeshTopology topology, double[] uv,
            out Vector3[] positions, out Vector3[] normals, out List<int[]> faces,
            out ParametrizationTraceStats stats)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats = new ParametrizationTraceStats();
            var P = topology.positions;
            var T = topology.triangles;
            var twin = topology.halfEdgeTwin;
            var m = T.Length / 3;
            stats.faces = m;

            double U(int corner) => uv[2 * corner];
            double V(int corner) => uv[2 * corner + 1];

            // --- faces: orientation in the chart ---------------------------------------------
            var valid = new bool[m];
            var faceNormal = new Vector3[m];
            for (var f = 0; f < m; f++)
            {
                int c0 = 3 * f, c1 = c0 + 1, c2 = c0 + 2;
                var det = Cross(U(c1) - U(c0), V(c1) - V(c0), U(c2) - U(c0), V(c2) - V(c0));
                valid[f] = det > 1e-12;
                if (!valid[f]) stats.foldedFaces++;
                faceNormal[f] = MeshMath.SafeNormalize(
                    Vector3.Cross(P[T[c1]] - P[T[c0]], P[T[c2]] - P[T[c0]]), Vector3.up);
            }

            // --- transitions: uv in the face across = R^rot uv + t ----------------------------
            var rot = new int[3 * m];
            var tu = new double[3 * m];
            var tv = new double[3 * m];
            for (var h = 0; h < 3 * m; h++)
            {
                var o = twin[h];
                if (o < 0) continue;
                var f = h / 3;
                var g = o / 3;
                int a = h, b = 3 * f + (h % 3 + 1) % 3;          // a -> b in f
                int gb = o, ga = 3 * g + (o % 3 + 1) % 3;        // b -> a in g
                double ex = U(b) - U(a), ey = V(b) - V(a);
                double gx = U(gb) - U(ga), gy = V(gb) - V(ga);   // a -> b in g
                var best = 0;
                var bestErr = double.PositiveInfinity;
                for (var k = 0; k < 4; k++)
                {
                    Rot(k, ex, ey, out var rx, out var ry);
                    Rot(k, U(a), V(a), out var ax, out var ay);
                    double ttu = U(ga) - ax, ttv = V(ga) - ay;
                    // Edge direction first; a vanishing edge falls back to the integral shift.
                    var err = Math.Abs(rx - gx) + Math.Abs(ry - gy)
                              + 1e-3 * (Math.Abs(ttu - Math.Round(ttu)) + Math.Abs(ttv - Math.Round(ttv)));
                    if (err < bestErr) { bestErr = err; best = k; }
                }
                rot[h] = best;
                Rot(best, U(a), V(a), out var pax, out var pay);
                double su = U(ga) - pax, sv = V(ga) - pay;
                var ru = Math.Round(su);
                var rv = Math.Round(sv);
                if (Math.Abs(ru - su) > 1e-4 || Math.Abs(rv - sv) > 1e-4)
                {
                    stats.nonIntegerTransitions++;
                    if (stats.nonIntegerTransitions <= 3)
                        stats.debug += $"[h{h} o{o} T {T[a]},{T[b]} / {T[gb]},{T[ga]} f({U(a):F4},{V(a):F4})({U(b):F4},{V(b):F4}) g({U(ga):F4},{V(ga):F4})({U(gb):F4},{V(gb):F4}) rot{best}] ";
                }
                tu[h] = ru;
                tv[h] = rv;
            }

            void Across(int h, ref double x, ref double y)
            {
                Rot(rot[h], x, y, out var rx, out var ry);
                x = rx + tu[h];
                y = ry + tv[h];
            }
            void AcrossDir(int h, ref double x, ref double y)
            {
                Rot(rot[h], x, y, out var rx, out var ry);
                x = rx;
                y = ry;
            }

            // --- vertices --------------------------------------------------------------------
            var verts = new List<QuadVertex>();
            var lookup = new Dictionary<(int f, int i, int j), int>();
            var cornerVertex = new Dictionary<int, int>();
            var edgeVertex = new Dictionary<(int h, int i, int j), int>();

            void Register(int qv, int f, int i, int j)
            {
                if (lookup.ContainsKey((f, i, j))) return;
                lookup[(f, i, j)] = qv;
                verts[qv].at.Add(new Registration { face = f, i = i, j = j });
            }
            int NewVertex(int kind, Vector3 p, Vector3 n)
            {
                verts.Add(new QuadVertex { kind = kind, position = p, normal = n });
                return verts.Count - 1;
            }

            for (var f = 0; f < m; f++)
            {
                if (!valid[f]) continue;
                int c0 = 3 * f;
                double u0 = U(c0), v0 = V(c0), u1 = U(c0 + 1), v1 = V(c0 + 1), u2 = U(c0 + 2), v2 = V(c0 + 2);
                var det = Cross(u1 - u0, v1 - v0, u2 - u0, v2 - v0);
                var len0 = Math.Sqrt((u2 - u1) * (u2 - u1) + (v2 - v1) * (v2 - v1));   // opposite corner 0
                var len1 = Math.Sqrt((u0 - u2) * (u0 - u2) + (v0 - v2) * (v0 - v2));
                var len2 = Math.Sqrt((u1 - u0) * (u1 - u0) + (v1 - v0) * (v1 - v0));
                var iMin = (int)Math.Ceiling(Math.Min(u0, Math.Min(u1, u2)) - onLine);
                var iMax = (int)Math.Floor(Math.Max(u0, Math.Max(u1, u2)) + onLine);
                var jMin = (int)Math.Ceiling(Math.Min(v0, Math.Min(v1, v2)) - onLine);
                var jMax = (int)Math.Floor(Math.Max(v0, Math.Max(v1, v2)) + onLine);
                for (var i = iMin; i <= iMax; i++)
                for (var j = jMin; j <= jMax; j++)
                {
                    // Twice the signed areas opposite each corner, and as distances to that edge.
                    var s0 = Cross(u1 - i, v1 - j, u2 - i, v2 - j);
                    var s1 = Cross(u2 - i, v2 - j, u0 - i, v0 - j);
                    var s2 = Cross(u0 - i, v0 - j, u1 - i, v1 - j);
                    var d0 = s0 / Math.Max(len0, 1e-30);
                    var d1 = s1 / Math.Max(len1, 1e-30);
                    var d2 = s2 / Math.Max(len2, 1e-30);
                    if (d0 < -onLine || d1 < -onLine || d2 < -onLine) continue;
                    bool z0 = Math.Abs(d0) <= onLine, z1 = Math.Abs(d1) <= onLine, z2 = Math.Abs(d2) <= onLine;
                    var zeros = (z0 ? 1 : 0) + (z1 ? 1 : 0) + (z2 ? 1 : 0);
                    if (zeros >= 2)
                    {
                        // At a corner: the corner whose own area is not zero.
                        var k = !z0 ? 0 : !z1 ? 1 : 2;
                        var mv = T[c0 + k];
                        if (!cornerVertex.TryGetValue(mv, out var qv))
                        {
                            qv = NewVertex(2, P[mv], topology.normals[mv]);
                            verts[qv].meshVertex = mv;
                            cornerVertex[mv] = qv;
                            stats.cornerVertices++;
                        }
                        Register(qv, f, i, j);
                        continue;
                    }
                    var l0 = Math.Max(0, s0 / det);
                    var l1 = Math.Max(0, s1 / det);
                    var l2 = Math.Max(0, s2 / det);
                    var sum = l0 + l1 + l2;
                    var point = (P[T[c0]] * (float)l0 + P[T[c0 + 1]] * (float)l1 + P[T[c0 + 2]] * (float)l2) / (float)sum;
                    if (zeros == 0)
                    {
                        var qv = NewVertex(0, point, faceNormal[f]);
                        stats.insideVertices++;
                        Register(qv, f, i, j);
                        continue;
                    }
                    // On the edge opposite the zero corner: corner k+1 -> k+2 is half-edge 3f + k + 1.
                    var zk = z0 ? 0 : z1 ? 1 : 2;
                    var h = 3 * f + (zk + 1) % 3;
                    var o = twin[h];
                    var g = o >= 0 ? o / 3 : -1;
                    // Owned by the lower valid face; its coordinates are the key.
                    var ownHere = g < 0 || !valid[g] || f < g;
                    int key, ki, kj;
                    if (ownHere) { key = h; ki = i; kj = j; }
                    else
                    {
                        double x = i, y = j;
                        Across(h, ref x, ref y);
                        key = o;
                        ki = (int)Math.Round(x);
                        kj = (int)Math.Round(y);
                    }
                    if (!edgeVertex.TryGetValue((key, ki, kj), out var ev))
                    {
                        ev = NewVertex(1, point, faceNormal[f]);
                        verts[ev].halfEdge = key;
                        edgeVertex[(key, ki, kj)] = ev;
                        stats.edgeVertices++;
                    }
                    Register(ev, f, i, j);
                }
            }
            stats.vertices = verts.Count;

            // --- ray slots, counter-clockwise --------------------------------------------------
            // Whether direction d leaves the point (i, j) of face f into f, with a ray along an edge
            // taken to run just left of it.
            bool Leaves(int f, int i, int j, int d)
            {
                double dx = DirX[d], dy = DirY[d];
                // The point's position relative to each edge (k -> k+1); interior is to the left.
                var inside = true;
                for (var k = 0; k < 3; k++)
                {
                    int ca = 3 * f + k, cb = 3 * f + (k + 1) % 3;
                    double ex = U(cb) - U(ca), ey = V(cb) - V(ca);
                    var len = Math.Sqrt(ex * ex + ey * ey);
                    if (len < 1e-30) return false;
                    var dist = Cross(ex, ey, i - U(ca), j - V(ca)) / len;
                    if (dist > onLine) continue;          // well inside this edge's half-plane
                    // On this edge's line: the ray must go into the half-plane, or along the edge
                    // forwards (the interior is then on its left).
                    var c = Cross(ex, ey, dx, dy) / len;
                    if (c > 1e-9) continue;
                    if (Math.Abs(c) <= 1e-9 && ex * dx + ey * dy > 0) continue;
                    inside = false;
                    break;
                }
                return inside;
            }

            // Faces round a mesh vertex in counter-clockwise order, each with the corner there.
            List<int> FanCorners(int mv, int anyCorner)
            {
                var list = new List<int>(8);
                // Clockwise to the start (a boundary) or once round.
                var start = anyCorner;
                var c = anyCorner;
                for (var guard = 0; guard < 64; guard++)
                {
                    // Clockwise neighbour: across the edge (v -> next), i.e. half-edge c.
                    var o = twin[c];
                    if (o < 0) { start = c; break; }
                    var g = o / 3;
                    var cg = 3 * g + (o % 3 + 1) % 3;     // in g the edge runs next -> v: v is o's head
                    if (cg == anyCorner) { start = anyCorner; break; }
                    c = cg;
                    start = c;
                }
                c = start;
                for (var guard = 0; guard < 64; guard++)
                {
                    list.Add(c);
                    var f = c / 3;
                    var prev = 3 * f + (c % 3 + 2) % 3;   // edge (prev -> v): counter-clockwise neighbour
                    var o = twin[prev];
                    if (o < 0) break;
                    var cg = o;                            // in g that edge runs v -> prev: v is its tail
                    if (cg == start) break;
                    c = cg;
                }
                return list;
            }

            for (var q = 0; q < verts.Count; q++)
            {
                var qv = verts[q];
                if (qv.kind == 0)
                {
                    var r = qv.at[0];
                    for (var d = 0; d < 4; d++) qv.slots.Add((r.face, d));
                    continue;
                }
                // The registrations in counter-clockwise order, each face's directions sorted by
                // angle from where its sector starts.
                var ordered = new List<(int face, double sx, double sy, int i, int j)>(8);
                if (qv.kind == 1)
                {
                    var h = qv.halfEdge;
                    var o = twin[h];
                    foreach (var hh in o >= 0 ? new[] { h, o } : new[] { h })
                    {
                        var f = hh / 3;
                        var reg = qv.at.FindIndex(x => x.face == f);
                        if (reg < 0) continue;
                        var b = 3 * f + (hh % 3 + 1) % 3;
                        // Sector starts along the edge towards its head.
                        ordered.Add((f, U(b) - U(hh), V(b) - V(hh), qv.at[reg].i, qv.at[reg].j));
                    }
                }
                else
                {
                    var anyCorner = -1;
                    var f0 = qv.at[0].face;
                    for (var k = 0; k < 3; k++) if (T[3 * f0 + k] == qv.meshVertex) anyCorner = 3 * f0 + k;
                    foreach (var c in FanCorners(qv.meshVertex, anyCorner))
                    {
                        var f = c / 3;
                        var reg = qv.at.FindIndex(x => x.face == f);
                        if (reg < 0) continue;
                        var nx = 3 * f + (c % 3 + 1) % 3;
                        ordered.Add((f, U(nx) - U(c), V(nx) - V(c), qv.at[reg].i, qv.at[reg].j));
                    }
                }
                foreach (var (f, sx, sy, i, j) in ordered)
                {
                    var dirs = new List<(double angle, int d)>(4);
                    for (var d = 0; d < 4; d++)
                    {
                        if (!Leaves(f, i, j, d)) continue;
                        var angle = Math.Atan2(Cross(sx, sy, DirX[d], DirY[d]), sx * DirX[d] + sy * DirY[d]);
                        if (angle < -1e-9) angle += 2 * Math.PI;
                        dirs.Add((angle, d));
                    }
                    dirs.Sort((a, b) => a.angle.CompareTo(b.angle));
                    foreach (var (_, d) in dirs) qv.slots.Add((f, d));
                }
            }

            // --- rays -----------------------------------------------------------------------
            var slotBase = new int[verts.Count + 1];
            for (var q = 0; q < verts.Count; q++) slotBase[q + 1] = slotBase[q] + verts[q].slots.Count;
            var slotCount = slotBase[verts.Count];
            var target = new int[slotCount];       // the slot the ray arrives back through, or -1
            var targetVertex = new int[slotCount];
            for (var s = 0; s < slotCount; s++) { target[s] = -1; targetVertex[s] = -1; }
            stats.rays = slotCount;

            int SlotOf(int q, int face, int dir)
            {
                var slots = verts[q].slots;
                for (var k = 0; k < slots.Count; k++)
                    if (slots[k].face == face && slots[k].dir == dir) return slotBase[q] + k;
                return -1;
            }

            // The slot at vertex w whose ray leaves along direction (dx, dy) of face f's chart: in f
            // itself, or in a face round w reached through the transitions.
            int ReverseSlot(int w, int f, double dx, double dy)
            {
                var d = DirIndex(dx, dy);
                var s = SlotOf(w, f, d);
                if (s >= 0) return s;
                var qv = verts[w];
                if (qv.kind == 0) return -1;
                // Breadth-first over the faces round w, carrying the direction.
                var seen = new HashSet<int> { f };
                var queue = new Queue<(int f, double x, double y)>();
                queue.Enqueue((f, dx, dy));
                while (queue.Count > 0)
                {
                    var (cf, cx, cy) = queue.Dequeue();
                    for (var k = 0; k < 3; k++)
                    {
                        var h = 3 * cf + k;
                        var o = twin[h];
                        if (o < 0) continue;
                        var g = o / 3;
                        if (seen.Contains(g)) continue;
                        if (!qv.at.Exists(x => x.face == g)) continue;
                        // The edge must touch w: both faces hold it.
                        seen.Add(g);
                        double nx = cx, ny = cy;
                        AcrossDir(h, ref nx, ref ny);
                        var gs = SlotOf(w, g, DirIndex(nx, ny));
                        if (gs >= 0) return gs;
                        queue.Enqueue((g, nx, ny));
                    }
                }
                return -1;
            }

            for (var q = 0; q < verts.Count; q++)
            {
                var qv = verts[q];
                for (var k = 0; k < qv.slots.Count; k++)
                {
                    var (f0, d0) = qv.slots[k];
                    var reg = qv.at.Find(x => x.face == f0);
                    double ox = reg.i, oy = reg.j;
                    double dx = DirX[d0], dy = DirY[d0];
                    double tx = ox + dx, ty = oy + dy;
                    var f = f0;
                    var entry = -1;
                    var done = false;
                    for (var step = 0; step < maxSteps && !done; step++)
                    {
                        // Arrived: the target is a vertex of this face.
                        var ti = (int)Math.Round(tx);
                        var tj = (int)Math.Round(ty);
                        if (lookup.TryGetValue((f, ti, tj), out var w) && w != q)
                        {
                            targetVertex[slotBase[q] + k] = w;
                            target[slotBase[q] + k] = ReverseSlot(w, f, -dx, -dy);
                            if (target[slotBase[q] + k] < 0) stats.noReverse++;
                            else stats.traced++;
                            done = true;
                            break;
                        }
                        // Leave through the edge whose tail is right of the ray and head left of it.
                        var exit = -1;
                        for (var e = 0; e < 3; e++)
                        {
                            var h = 3 * f + e;
                            if (h == entry) continue;
                            int ca = h, cb = 3 * f + (e + 1) % 3;
                            var sa = Cross(dx, dy, U(ca) - ox, V(ca) - oy);
                            var sb = Cross(dx, dy, U(cb) - ox, V(cb) - oy);
                            var ra = sa <= onLine;     // on the line counts as right: the ray runs left
                            var lb = sb > onLine;
                            if (!ra || !lb) continue;
                            exit = h;
                            break;
                        }
                        if (exit < 0) { stats.missedTarget++; done = true; break; }
                        {
                            // Past the target already without meeting it: nothing there.
                            int ca = exit, cb = 3 * f + (exit % 3 + 1) % 3;
                            double ex = U(cb) - U(ca), ey = V(cb) - V(ca);
                            var den = Cross(dx, dy, ex, ey);
                            if (Math.Abs(den) > 1e-30)
                            {
                                var t = Cross(U(ca) - ox, V(ca) - oy, ex, ey) / den;
                                var along = (tx - ox) * dx + (ty - oy) * dy;
                                if (t > along + 1e-6) { stats.missedTarget++; done = true; break; }
                            }
                        }
                        var o = twin[exit];
                        if (o < 0) { stats.hitBoundary++; done = true; break; }
                        var g = o / 3;
                        if (!valid[g]) { stats.hitFold++; done = true; break; }
                        Across(exit, ref ox, ref oy);
                        Across(exit, ref tx, ref ty);
                        AcrossDir(exit, ref dx, ref dy);
                        entry = o;
                        f = g;
                        if (step == maxSteps - 1) stats.tooLong++;
                    }
                }
            }

            // --- faces -----------------------------------------------------------------------
            // Each ray is a half-edge; it stands when the ray back from its target returns.
            var standing = new bool[slotCount];
            for (var q = 0; q < verts.Count; q++)
                for (var k = 0; k < verts[q].slots.Count; k++)
                {
                    var s = slotBase[q] + k;
                    var r = target[s];
                    if (r < 0) continue;
                    if (target[r] == s) standing[s] = true;
                    else stats.unpaired++;
                }
            var slotVertex = new int[slotCount];
            for (var q = 0; q < verts.Count; q++)
                for (var k = 0; k < verts[q].slots.Count; k++) slotVertex[slotBase[q] + k] = q;

            faces = new List<int[]>(verts.Count);
            var used = new bool[slotCount];
            var loop = new List<int>(8);
            for (var s0 = 0; s0 < slotCount; s0++)
            {
                if (!standing[s0] || used[s0]) continue;
                loop.Clear();
                var s = s0;
                var closed = false;
                for (var guard = 0; guard < 16; guard++)
                {
                    if (!standing[s] || used[s]) break;
                    used[s] = true;
                    loop.Add(slotVertex[s]);
                    // Arrive at w through slot r; the next edge is the one clockwise of r.
                    var r = target[s];
                    var w = slotVertex[r];
                    var count = verts[w].slots.Count;
                    var next = slotBase[w] + ((r - slotBase[w]) + count - 1) % count;
                    if (next == s0) { closed = true; break; }
                    s = next;
                }
                if (!closed || loop.Count < 3) { stats.brokenWalks++; continue; }
                var distinct = new HashSet<int>(loop);
                if (distinct.Count != loop.Count) { stats.brokenWalks++; continue; }
                faces.Add(loop.ToArray());
                stats.polygons++;
                if (loop.Count == 4) stats.quads++;
                else if (loop.Count == 3) stats.triangles++;
                else stats.larger++;
            }

            positions = new Vector3[verts.Count];
            normals = new Vector3[verts.Count];
            for (var q = 0; q < verts.Count; q++)
            {
                positions[q] = verts[q].position;
                normals[q] = verts[q].normal;
            }
            stats.ms = watch.ElapsedMilliseconds;
        }
    }
}
