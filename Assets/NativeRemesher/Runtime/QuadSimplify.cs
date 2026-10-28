// A quad mesh made coarser where the surface lets it, and left fine where it does not.
//
// The lattice spaces its quads evenly: on the armoured car at 5,000 the flat plates (more than half
// its area) hold as many quads a square metre as the wheels, the gun and the hinges. Asking the
// lattice itself for big quads on the plates does not work -- its least-squares parametrisation
// cannot follow a size field that steep (asked 3x the spacing on the plates, it gave 1.1x) -- and
// taking out whole quad strips does not either: the lattice's strips spiral round the model and
// cross themselves (on the car at 57,179 faces the strips ran out at 46,905).
//
// So the lattice's quads are simplified one by one afterwards, as far as a limit off the source
// allows, or down to a count:
//
//   collapse   a quad is taken out by merging two opposite corners into one point, placed where
//              the merged corners' quadrics (the source planes nearest them) are least. Cheapest
//              first; the cost is (diagonal / cell)^2, so flat ground coarsens evenly, plus the
//              farthest the new faces lie off the source in (errorCells * cell)^2, so a merge that
//              cuts across an edge or off a part is expensive, plus how far it moves the valences
//              from 4. Refused when a face would turn over or get a corner under minCornerDegrees,
//              when a point would be in more than maxValence faces, or when the new faces would lie
//              further off the source than the caller's limit.
//   doublets   a corner left with two faces (both sharing two of its edges) is merged away.
//   stages     every stage, the edges between two quads are turned where that brings the valences
//              nearer 4, the points off creases are moved toward the middle of their faces and put
//              back on the source, and the queue is costed again.
//   repair     at the end, a quad folded over both diagonals is collapsed whatever it costs.
//
// Measured on the car (sampled both ways against the source, thousandths of its diagonal, mean):
// held to 0.03 cells, 19,741 -> 15,063 faces at 0.66 -> 0.75 / 0.42 -> 0.46, renders alike; down
// to the count of 5,000, 4,788 faces at 1.37 / 0.99, where the lattice itself at 6,749 faces is at
// 1.62 / 0.81 (p99 12.9 against 8.1 from the source) -- the gun's barrel, the mirrors' arms and the
// plate edges kept, the wheels faceted. The quads on a plate are not in rows (each collapse leaves
// a 3-5 valence pair) but lie flat, and the plate's edges stay straight.
//
// Tried and dropped: limits on how bent (60, 80 degrees) or reflex a quad may turn out -- the
// collapses stalled at 9,600 and 7,700 faces and the shape came out worse; a term for the faces'
// normals against the source's -- its detail made the normals noisy and the shape twice as far off.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    public static class QuadSimplify
    {
        public sealed class Stats
        {
            public int facesBefore, facesAfter, collapses, refused, doublets, rotations, smoothed, repaired;
            public float regularShare;
            public long ms;
            public override string ToString() =>
                $"simplify: {facesBefore} -> {facesAfter} faces, {collapses} collapses ({refused} refused), {doublets} doublets, " +
                $"{rotations} rotations, {smoothed} moves, {repaired} folded quads taken out, {regularShare:P1} valence 4, {ms} ms";
        }

        /// <summary>Distance off the source, in cells, that costs as much as a collapse a cell across.</summary>
        public static float errorCells = 0.05f;

        /// <summary>Weight of a collapse's change in the squared valence errors.</summary>
        public static float valenceWeight = 0.5f;

        /// <summary>No collapse leaves a point in more faces than this.</summary>
        public static int maxValence = 7;

        /// <summary>No collapse or turn leaves a face with a corner under this, degrees.</summary>
        public static float minCornerDegrees = 12f;

        /// <summary>At the end, quads whose halves turn further apart than this however cut, degrees, are collapsed...</summary>
        public static float repairBentDegrees = 100f;

        /// <summary>...in at most this many passes.</summary>
        public static int repairPasses = 3;

        /// <summary>Passes of collapses, each followed by turns and smoothing.</summary>
        public static int stages = 12;

        /// <summary>Smoothing sweeps after each stage, and at the end.</summary>
        public static int stageSmoothSweeps = 2, finalSmoothSweeps = 4;

        /// <summary>A point whose faces turn more than this apart, degrees, is on a crease and is not smoothed.</summary>
        public static float creaseDegrees = 25f;

        struct Entry
        {
            public float cost;
            public int face, diagonal, stamp;
        }

        sealed class Heap
        {
            readonly List<Entry> m_Items = new List<Entry>();
            public int Count => m_Items.Count;
            public void Clear() => m_Items.Clear();

            public void Push(Entry e)
            {
                m_Items.Add(e);
                var i = m_Items.Count - 1;
                while (i > 0)
                {
                    var p = (i - 1) / 2;
                    if (m_Items[p].cost <= e.cost) break;
                    m_Items[i] = m_Items[p];
                    i = p;
                }
                m_Items[i] = e;
            }

            public Entry Pop()
            {
                var top = m_Items[0];
                var last = m_Items[m_Items.Count - 1];
                m_Items.RemoveAt(m_Items.Count - 1);
                if (m_Items.Count == 0) return top;
                var i = 0;
                var n = m_Items.Count;
                while (true)
                {
                    var l = 2 * i + 1;
                    if (l >= n) break;
                    var r = l + 1;
                    var c = r < n && m_Items[r].cost < m_Items[l].cost ? r : l;
                    if (m_Items[c].cost >= last.cost) break;
                    m_Items[i] = m_Items[c];
                    i = c;
                }
                m_Items[i] = last;
                return top;
            }
        }

        /// <summary>
        /// Simplifies <paramref name="faces"/> (quads, with any triangles left as they are) over
        /// <paramref name="positions"/> to about <paramref name="target"/> faces, against the source
        /// mesh. Returns the new positions; <paramref name="remap"/> gives each old vertex's new
        /// index, or -1 where it was merged away. <paramref name="hold"/>: vertices never moved.
        /// <paramref name="maxOffCells"/> above 0: no collapse whose new faces lie further than that
        /// many cells (at the target's spacing) off the source, so only what costs no shape is taken
        /// and the result can stay above the target; 0, down to the target.
        /// </summary>
        public static Vector3[] Simplify(Vector3[] positions, List<int[]> faces, Vector3[] sourcePositions,
            int[] sourceTriangles, SpatialGrid surface, int target, float maxOffCells, bool[] hold, out int[] remap,
            out Stats stats)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var st = new Stats { facesBefore = faces.Count };
            stats = st;
            var n = positions.Length;
            remap = new int[n];
            for (var i = 0; i < n; i++) remap[i] = i;
            if (n == 0 || faces.Count <= target || sourceTriangles == null || sourceTriangles.Length < 3)
            {
                st.facesAfter = faces.Count;
                return positions;
            }

            var p = (Vector3[])positions.Clone();
            var vf = new List<int>[n];
            for (var v = 0; v < n; v++) vf[v] = new List<int>(5);
            var alive = new bool[faces.Count];
            for (var f = 0; f < faces.Count; f++)
            {
                alive[f] = faces[f] != null && faces[f].Length >= 3;
                if (!alive[f]) continue;
                foreach (var v in faces[f]) vf[v].Add(f);
            }

            // Points on an open or non-manifold edge, on a triangle, or held, never move.
            var locked = new bool[n];
            var edgeUse = new Dictionary<long, int>();
            for (var f = 0; f < faces.Count; f++)
            {
                if (!alive[f]) continue;
                var face = faces[f];
                if (face.Length != 4) foreach (var v in face) locked[v] = true;
                for (var k = 0; k < face.Length; k++)
                {
                    var key = Extraction.EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    edgeUse[key] = edgeUse.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            }
            foreach (var kv in edgeUse)
                if (kv.Value != 2)
                {
                    locked[Extraction.EdgeKey.First(kv.Key)] = true;
                    locked[Extraction.EdgeKey.Second(kv.Key)] = true;
                }
            if (hold != null)
                for (var v = 0; v < n && v < hold.Length; v++)
                    if (hold[v]) locked[v] = true;

            // The source's area and each output point's quadric: the planes of the source triangles
            // nearest it, area-weighted.
            double sourceArea = 0;
            var m = sourceTriangles.Length / 3;
            for (var t = 0; t < m; t++)
                sourceArea += Vector3.Cross(sourcePositions[sourceTriangles[3 * t + 1]] - sourcePositions[sourceTriangles[3 * t]],
                    sourcePositions[sourceTriangles[3 * t + 2]] - sourcePositions[sourceTriangles[3 * t]]).magnitude * 0.5;
            var cell = (float)Math.Sqrt(sourceArea / Math.Max(1, target));
            var quadric = new double[n * 10];
            var nearest = NearestPoints(p, sourcePositions, sourceTriangles, cell);
            for (var t = 0; t < m; t++)
            {
                var v = nearest[t];
                if (v < 0) continue;
                Vector3 a = sourcePositions[sourceTriangles[3 * t]], b = sourcePositions[sourceTriangles[3 * t + 1]],
                    c = sourcePositions[sourceTriangles[3 * t + 2]];
                var cr = Vector3.Cross(b - a, c - a);
                var area = cr.magnitude * 0.5;
                if (!(area > 0)) continue;
                var nn = cr / (float)(2 * area);
                double nx = nn.x, ny = nn.y, nz = nn.z, d = -(nx * a.x + ny * a.y + nz * a.z);
                var o = 10 * v;
                quadric[o] += area * nx * nx; quadric[o + 1] += area * nx * ny; quadric[o + 2] += area * nx * nz;
                quadric[o + 3] += area * nx * d; quadric[o + 4] += area * ny * ny; quadric[o + 5] += area * ny * nz;
                quadric[o + 6] += area * ny * d; quadric[o + 7] += area * nz * nz; quadric[o + 8] += area * nz * d;
                quadric[o + 9] += area * d * d;
            }

            double Error(double[] q, int o, Vector3 x) =>
                q[o] * x.x * x.x + 2 * q[o + 1] * x.x * x.y + 2 * q[o + 2] * x.x * x.z + 2 * q[o + 3] * x.x
                + q[o + 4] * x.y * x.y + 2 * q[o + 5] * x.y * x.z + 2 * q[o + 6] * x.y
                + q[o + 7] * x.z * x.z + 2 * q[o + 8] * x.z + q[o + 9];

            // Where u and w merge: the merged quadric's least of the middle, either end and its minimum.
            Vector3 Place(int u, int w, double[] q)
            {
                for (var i = 0; i < 10; i++) q[i] = quadric[10 * u + i] + quadric[10 * w + i];
                if (locked[u]) return p[u];
                if (locked[w]) return p[w];
                var mid = (p[u] + p[w]) * 0.5f;
                var best = mid;
                var bestError = Error(q, 0, mid);
                void Try(Vector3 x)
                {
                    var e = Error(q, 0, x);
                    if (e < bestError) { bestError = e; best = x; }
                }
                Try(p[u]);
                Try(p[w]);
                double a00 = q[0], a01 = q[1], a02 = q[2], a11 = q[4], a12 = q[5], a22 = q[7];
                var reg = 1e-9 * Math.Max(1e-12, a00 + a11 + a22);
                a00 += reg; a11 += reg; a22 += reg;
                var det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
                if (Math.Abs(det) > 1e-30)
                {
                    double b0 = -q[3], b1 = -q[6], b2 = -q[8];
                    var x0 = (b0 * (a11 * a22 - a12 * a12) - a01 * (b1 * a22 - a12 * b2) + a02 * (b1 * a12 - a11 * b2)) / det;
                    var x1 = (a00 * (b1 * a22 - a12 * b2) - b0 * (a01 * a22 - a12 * a02) + a02 * (a01 * b2 - b1 * a02)) / det;
                    var x2 = (a00 * (a11 * b2 - b1 * a12) - a01 * (a01 * b2 - b1 * a02) + b0 * (a01 * a12 - a11 * a02)) / det;
                    var x = new Vector3((float)x0, (float)x1, (float)x2);
                    if ((x - mid).magnitude < 0.75f * (p[u] - p[w]).magnitude) Try(x);
                }
                return best;
            }

            Vector3 Normal(int[] face, int from = -1, int to = -1, Vector3 at = default)
            {
                var s = Vector3.zero;
                for (var k = 0; k < face.Length; k++)
                {
                    var i0 = face[k] == from ? to : face[k];
                    var i1 = face[(k + 1) % face.Length] == from ? to : face[(k + 1) % face.Length];
                    var a = i0 == to && to >= 0 ? at : p[i0];
                    var b = i1 == to && to >= 0 ? at : p[i1];
                    s += Vector3.Cross(a, b);
                }
                return s;
            }

            var minCornerCos = Mathf.Cos(minCornerDegrees * Mathf.Deg2Rad);
            // Every corner at least minCornerDegrees.
            bool CornersOk(Vector3[] q, int count)
            {
                for (var k = 0; k < count; k++)
                {
                    var e1 = q[(k + 1) % count] - q[k];
                    var e2 = q[(k + count - 1) % count] - q[k];
                    var l = e1.magnitude * e2.magnitude;
                    if (!(l > 1e-20f) || Vector3.Dot(e1, e2) / l > minCornerCos) return false;
                }
                return true;
            }

            bool FaceOk(int[] face, Vector3[] scratch)
            {
                for (var k = 0; k < face.Length; k++) scratch[k] = p[face[k]];
                return CornersOk(scratch, face.Length);
            }

            // The faces round u and w once merged at x (all but `skip`).
            var around = new List<int>(16);
            void Around(int u, int w, int skip)
            {
                around.Clear();
                foreach (var g in vf[u]) if (g != skip) around.Add(g);
                foreach (var g in vf[w]) if (g != skip && !around.Contains(g)) around.Add(g);
            }

            int Valence(int v) => vf[v].Count;

            float Cost(int f, int k, Vector3[] corner, List<int> ring)
            {
                var face = faces[f];
                int u = face[k], w = face[k + 2], s1 = face[k + 1], s3 = face[(k + 3) % 4];
                if (locked[u] && locked[w]) return float.NaN;
                var q = new double[10];
                var x = Place(u, w, q);
                var diagonal = (p[u] - p[w]).magnitude;
                ring.Clear();
                foreach (var g in vf[u]) if (g != f) ring.Add(g);
                foreach (var g in vf[w]) if (g != f && !ring.Contains(g)) ring.Add(g);
                var far = 0f;
                // How far a half's sample lies off the source.
                void Sample(Vector3 at)
                {
                    surface.NearestPoint(at, out var d);
                    far = Mathf.Max(far, d);
                }
                foreach (var g in ring)
                {
                    var gf = faces[g];
                    // A face the lattice left bent or reflex already is no reason to refuse; one the
                    // merge would make so is.
                    for (var i = 0; i < gf.Length; i++) corner[i] = p[gf[i]];
                    var wasOk = CornersOk(corner, gf.Length);
                    for (var i = 0; i < gf.Length; i++) corner[i] = gf[i] == u || gf[i] == w ? x : p[gf[i]];
                    if (wasOk && !CornersOk(corner, gf.Length)) return float.NaN;
                    if (gf.Length == 4)
                    {
                        Sample((corner[0] + corner[1] + corner[2] + corner[3]) * 0.25f);
                        Sample((corner[0] + corner[1] + corner[2]) / 3f);
                        Sample((corner[0] + corner[2] + corner[3]) / 3f);
                    }
                    else Sample((corner[0] + corner[1] + corner[2]) / 3f);
                }
                if (maxOffCells > 0f && far > maxOffCells * cell) return float.NaN;
                var after = ring.Count;
                float dv = (after - 4) * (after - 4) - (Valence(u) - 4) * (Valence(u) - 4) - (Valence(w) - 4) * (Valence(w) - 4);
                if (!locked[s1]) dv += (Valence(s1) - 5) * (Valence(s1) - 5) - (Valence(s1) - 4) * (Valence(s1) - 4);
                if (!locked[s3]) dv += (Valence(s3) - 5) * (Valence(s3) - 5) - (Valence(s3) - 4) * (Valence(s3) - 4);
                var e = far / (errorCells * cell);
                return diagonal / cell * (diagonal / cell) + e * e + valenceWeight * dv;
            }

            var heap = new Heap();
            var stamp = new int[faces.Count];
            var cornerScratch = new Vector3[8];
            var ringScratch = new List<int>(16);
            void Push(int f)
            {
                if (!alive[f] || faces[f].Length != 4) return;
                stamp[f]++;
                for (var k = 0; k < 2; k++)
                {
                    var c = Cost(f, k, cornerScratch, ringScratch);
                    if (!float.IsNaN(c)) heap.Push(new Entry { cost = c, face = f, diagonal = k, stamp = stamp[f] });
                }
            }

            // Every live quad's costs, in parallel.
            void Rebuild()
            {
                heap.Clear();
                var costs = new float[faces.Count * 2];
                Parallel.For(0, faces.Count, BackgroundScheduler.Options, () => (new Vector3[8], new List<int>(16)),
                    (f, _, local) =>
                    {
                        costs[2 * f] = costs[2 * f + 1] = float.NaN;
                        if (!alive[f] || faces[f].Length != 4) return local;
                        costs[2 * f] = Cost(f, 0, local.Item1, local.Item2);
                        costs[2 * f + 1] = Cost(f, 1, local.Item1, local.Item2);
                        return local;
                    }, _ => { });
                for (var f = 0; f < faces.Count; f++)
                {
                    stamp[f]++;
                    for (var k = 0; k < 2; k++)
                        if (!float.IsNaN(costs[2 * f + k]))
                            heap.Push(new Entry { cost = costs[2 * f + k], face = f, diagonal = k, stamp = stamp[f] });
                }
            }

            var count = 0;
            for (var f = 0; f < faces.Count; f++) if (alive[f]) count++;

            bool RemoveDoublet(int v)
            {
                if (locked[v] || vf[v].Count != 2) return false;
                int fa = vf[v][0], fb = vf[v][1];
                var f0 = faces[fa];
                var f1 = faces[fb];
                if (f0.Length != 4 || f1.Length != 4) return false;
                var i0 = Array.IndexOf(f0, v);
                int x = f0[(i0 + 1) % 4], a = f0[(i0 + 2) % 4], y = f0[(i0 + 3) % 4];
                var i1 = Array.IndexOf(f1, v);
                if (f1[(i1 + 1) % 4] != y || f1[(i1 + 3) % 4] != x) return false;
                var b = f1[(i1 + 2) % 4];
                if (a == b) return false;
                alive[fb] = false;
                foreach (var u in f1) vf[u].Remove(fb);
                faces[fa] = new[] { x, a, y, b };
                vf[v].Clear();
                vf[b].Add(fa);
                count--;
                st.doublets++;
                return true;
            }

            var unitNormals = new Vector3[faces.Count];
            void UnitNormals()
            {
                Parallel.For(0, faces.Count, BackgroundScheduler.Options, f =>
                {
                    unitNormals[f] = alive[f] ? MeshMath.SafeNormalize(Normal(faces[f]), Vector3.zero) : Vector3.zero;
                });
            }

            var creaseCos = Mathf.Cos(creaseDegrees * Mathf.Deg2Rad);
            void Smooth(int sweeps)
            {
                var moved = new Vector3[n];
                var moves = new bool[n];
                for (var s = 0; s < sweeps; s++)
                {
                    UnitNormals();
                    Parallel.For(0, n, BackgroundScheduler.Options, v =>
                    {
                        moves[v] = false;
                        if (locked[v] || vf[v].Count == 0) return;
                        var fs = vf[v];
                        for (var i = 0; i < fs.Count; i++)
                            for (var j = i + 1; j < fs.Count; j++)
                                if (Vector3.Dot(unitNormals[fs[i]], unitNormals[fs[j]]) < creaseCos) return;
                        var c = Vector3.zero;
                        foreach (var g in fs)
                        {
                            var face = faces[g];
                            var fc = Vector3.zero;
                            foreach (var x in face) fc += p[x];
                            c += fc / face.Length;
                        }
                        c /= fs.Count;
                        var onSurface = surface.NearestPoint(c, out _);
                        if ((onSurface - p[v]).magnitude >= 0.5f * cell) return;
                        moved[v] = onSurface;
                        moves[v] = true;
                    });
                    var old = (Vector3[])p.Clone();
                    for (var v = 0; v < n; v++) if (moves[v]) { p[v] = moved[v]; st.smoothed++; }
                    var before = (Vector3[])unitNormals.Clone();
                    UnitNormals();
                    var scratch = new Vector3[8];
                    for (var f = 0; f < faces.Count; f++)
                    {
                        if (!alive[f]) continue;
                        var changed = false;
                        foreach (var x in faces[f]) if (moves[x]) { changed = true; break; }
                        if (!changed) continue;
                        if (Vector3.Dot(before[f], unitNormals[f]) < 0.5f || !FaceOk(faces[f], scratch))
                            foreach (var x in faces[f]) p[x] = old[x];
                    }
                }
            }

            int Rotate()
            {
                var done = 0;
                var edges = new Dictionary<long, (int a, int b)>();
                for (var f = 0; f < faces.Count; f++)
                {
                    if (!alive[f] || faces[f].Length != 4) continue;
                    var face = faces[f];
                    for (var k = 0; k < 4; k++)
                    {
                        var key = Extraction.EdgeKey.PackUndirected(face[k], face[(k + 1) % 4]);
                        edges[key] = edges.TryGetValue(key, out var e) ? (e.a, f) : (f, -1);
                    }
                }
                var corners = new Vector3[4];
                foreach (var kv in edges)
                {
                    int fa = kv.Value.a, fb = kv.Value.b;
                    if (fb < 0 || !alive[fa] || !alive[fb]) continue;
                    var F1 = faces[fa];
                    var F2 = faces[fb];
                    if (F1.Length != 4 || F2.Length != 4) continue;
                    int x = Extraction.EdgeKey.First(kv.Key), y = Extraction.EdgeKey.Second(kv.Key);
                    var i = Array.IndexOf(F1, x);
                    if (i < 0 || Array.IndexOf(F1, y) < 0 || Array.IndexOf(F2, x) < 0 || Array.IndexOf(F2, y) < 0) continue;
                    if (F1[(i + 1) % 4] != y) { (x, y) = (y, x); i = Array.IndexOf(F1, x); }
                    if (F1[(i + 1) % 4] != y) continue;
                    int pp = F1[(i + 2) % 4], qq = F1[(i + 3) % 4];
                    var j = Array.IndexOf(F2, y);
                    if (F2[(j + 1) % 4] != x) continue;
                    int rr = F2[(j + 2) % 4], ss = F2[(j + 3) % 4];
                    if (locked[x] || locked[y] || pp == rr || pp == ss || qq == rr || qq == ss || pp == qq || rr == ss) continue;
                    int Err(int dx, int dy, int dp, int dq, int dr, int ds)
                    {
                        int E(int v, int d) { var e = Valence(v) + d - 4; return e * e; }
                        return E(x, dx) + E(y, dy) + E(pp, dp) + E(qq, dq) + E(rr, dr) + E(ss, ds);
                    }
                    var baseError = Err(0, 0, 0, 0, 0, 0);
                    var e1 = Err(-1, -1, 1, 0, 1, 0);
                    var e2 = Err(-1, -1, 0, 1, 0, 1);
                    int[] n1, n2;
                    int na, nb;
                    if (e1 <= e2) { if (e1 >= baseError) continue; n1 = new[] { pp, qq, x, rr }; n2 = new[] { rr, ss, y, pp }; na = pp; nb = rr; }
                    else { if (e2 >= baseError) continue; n1 = new[] { qq, x, rr, ss }; n2 = new[] { ss, y, pp, qq }; na = qq; nb = ss; }
                    var reference = MeshMath.SafeNormalize(Normal(F1) + Normal(F2), Vector3.zero);
                    var ok = true;
                    foreach (var nf in new[] { n1, n2 })
                    {
                        if (Vector3.Dot(MeshMath.SafeNormalize(Normal(nf), Vector3.zero), reference) < 0.7f) { ok = false; break; }
                        for (var k = 0; k < 4; k++) corners[k] = p[nf[k]];
                        if (!CornersOk(corners, 4)) { ok = false; break; }
                    }
                    if (!ok) continue;
                    var taken = false;
                    foreach (var g in vf[na]) if (g != fa && g != fb && Array.IndexOf(faces[g], nb) >= 0) { taken = true; break; }
                    if (taken) continue;
                    foreach (var v in F1) vf[v].Remove(fa);
                    foreach (var v in F2) vf[v].Remove(fb);
                    faces[fa] = n1;
                    faces[fb] = n2;
                    foreach (var v in n1) vf[v].Add(fa);
                    foreach (var v in n2) vf[v].Add(fb);
                    done++;
                }
                st.rotations += done;
                return done;
            }

            var start = count;
            var q4 = new double[10];
            var touched = new List<int>(32);
            var ringSet = new HashSet<int>();
            // Face f taken out across its diagonal k, unless that is not a valid merge; the faces
            // round it costed again when `push`.
            bool TryCollapse(int f, int k, bool push)
            {
                var face = faces[f];
                int u = face[k], w = face[k + 2], s1 = face[k + 1], s3 = face[(k + 3) % 4];
                if (locked[u] && locked[w]) return false;
                if (locked[w]) (u, w) = (w, u);
                Around(u, w, f);
                foreach (var g in around)
                    if (Array.IndexOf(faces[g], u) >= 0 && Array.IndexOf(faces[g], w) >= 0) return false;
                if (around.Count > maxValence) return false;
                var x = Place(u, w, q4);
                foreach (var g in around)
                {
                    var n0 = Normal(faces[g]);
                    // w's faces with w as u, and u's own, all with u at x.
                    var n1 = Normal(faces[g], w, u, x);
                    float l0 = n0.magnitude, l1 = n1.magnitude;
                    if (!(l0 > 1e-14f) || !(l1 > 1e-14f) || Vector3.Dot(n0, n1) / (l0 * l1) < 0.5f) return false;
                }

                alive[f] = false;
                count--;
                foreach (var v in face) vf[v].Remove(f);
                foreach (var g in vf[w])
                {
                    var gf = faces[g];
                    for (var i = 0; i < gf.Length; i++) if (gf[i] == w) gf[i] = u;
                    if (!vf[u].Contains(g)) vf[u].Add(g);
                }
                vf[w].Clear();
                p[u] = x;
                for (var i = 0; i < 10; i++) quadric[10 * u + i] = q4[i];
                st.collapses++;

                touched.Clear();
                touched.Add(u); touched.Add(s1); touched.Add(s3);
                RemoveDoublet(s1);
                RemoveDoublet(s3);
                if (!push) return true;
                // The faces round the points moved, costed again; those further out (whose
                // cost reads these faces' shapes too) wait for the next stage's Rebuild.
                ringSet.Clear();
                foreach (var v in touched)
                    foreach (var g in vf[v])
                        ringSet.Add(g);
                foreach (var g in ringSet) Push(g);
                return true;
            }

            for (var stage = 0; stage < stages && count > target; stage++)
            {
                var goal = Math.Max(target, (int)Math.Round(start - (start - target) * (stage + 1) / (double)stages));
                Rebuild();
                while (count > goal && heap.Count > 0)
                {
                    var e = heap.Pop();
                    if (!alive[e.face] || e.stamp != stamp[e.face]) continue;
                    if (!TryCollapse(e.face, e.diagonal, true)) st.refused++;
                }
                Rotate();
                Smooth(stageSmoothSweeps);
            }
            Rotate();
            Rotate();
            Smooth(finalSmoothSweeps);

            // Quads still folded over both diagonals -- drawn either way, one half faces the
            // other -- taken out whatever their cost.
            var repairCos = Mathf.Cos(repairBentDegrees * Mathf.Deg2Rad);
            for (var pass = 0; pass < repairPasses; pass++)
            {
                var repaired = 0;
                for (var f = 0; f < faces.Count; f++)
                {
                    if (!alive[f] || faces[f].Length != 4) continue;
                    var face = faces[f];
                    Vector3 a = p[face[0]], b = p[face[1]], c = p[face[2]], d = p[face[3]];
                    if (MeshMath.CosAngleBetween(Vector3.Cross(b - a, c - a), Vector3.Cross(c - a, d - a)) >= repairCos
                        || MeshMath.CosAngleBetween(Vector3.Cross(c - b, d - b), Vector3.Cross(d - b, a - b)) >= repairCos)
                        continue;
                    if (TryCollapse(f, 0, false) || TryCollapse(f, 1, false)) repaired++;
                }
                st.repaired += repaired;
                if (repaired == 0) break;
            }

            // Compact: live faces, their points renumbered.
            var live = new List<int[]>(count);
            for (var f = 0; f < faces.Count; f++) if (alive[f]) live.Add(faces[f]);
            var newIndex = new int[n];
            for (var v = 0; v < n; v++) newIndex[v] = -1;
            var outPositions = new List<Vector3>(n);
            foreach (var face in live)
                for (var k = 0; k < face.Length; k++)
                {
                    var v = face[k];
                    if (newIndex[v] < 0) { newIndex[v] = outPositions.Count; outPositions.Add(p[v]); }
                    face[k] = newIndex[v];
                }
            for (var v = 0; v < n; v++) remap[v] = newIndex[v];
            faces.Clear();
            faces.AddRange(live);

            var regular = 0;
            var interior = 0;
            var valence = new int[outPositions.Count];
            foreach (var face in faces) foreach (var v in face) valence[v]++;
            for (var v = 0; v < n; v++)
            {
                if (newIndex[v] < 0 || locked[v]) continue;
                interior++;
                if (valence[newIndex[v]] == 4) regular++;
            }
            st.regularShare = interior > 0 ? regular / (float)interior : 0f;
            st.facesAfter = faces.Count;
            st.ms = watch.ElapsedMilliseconds;
            return outPositions.ToArray();
        }

        // Each source triangle's nearest output point (by centroid), through a hash grid of the points.
        static int[] NearestPoints(Vector3[] points, Vector3[] sourcePositions, int[] sourceTriangles, float cell)
        {
            var size = Mathf.Max(cell, 1e-6f);
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Key(Vector3 x) => (Mathf.FloorToInt(x.x / size), Mathf.FloorToInt(x.y / size), Mathf.FloorToInt(x.z / size));
            for (var i = 0; i < points.Length; i++)
            {
                var k = Key(points[i]);
                if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>(4);
                list.Add(i);
            }
            var m = sourceTriangles.Length / 3;
            var nearest = new int[m];
            Parallel.For(0, m, BackgroundScheduler.Options, t =>
            {
                var c = (sourcePositions[sourceTriangles[3 * t]] + sourcePositions[sourceTriangles[3 * t + 1]]
                         + sourcePositions[sourceTriangles[3 * t + 2]]) / 3f;
                var (x, y, z) = Key(c);
                var best = -1;
                var bestD = float.PositiveInfinity;
                for (var ring = 1; ring <= 4 && best < 0; ring *= 2)
                    for (var dx = -ring; dx <= ring; dx++)
                    for (var dy = -ring; dy <= ring; dy++)
                    for (var dz = -ring; dz <= ring; dz++)
                    {
                        if (!grid.TryGetValue((x + dx, y + dy, z + dz), out var list)) continue;
                        foreach (var i in list)
                        {
                            var d = (points[i] - c).sqrMagnitude;
                            if (d < bestD) { bestD = d; best = i; }
                        }
                    }
                nearest[t] = best;
            });
            return nearest;
        }
    }
}
