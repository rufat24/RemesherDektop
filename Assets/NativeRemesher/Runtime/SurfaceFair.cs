// A hard surface's output made as flat as its panels are.
//
// Every Hard Surface output -- lattice, error-driven, building -- puts its points on the source (or
// on the 50,000-triangle mesh solved on). On the four high-poly cars that surface is lumpy below a
// quad's size: the source's panels carry millimetre ripples, and the solve mesh's long slivers
// add streaks of their own. A quad mesh with one point a quad across samples those ripples, and
// flat-shaded the doors come out crumpled -- on car2 at 5,000 quads its faces turned 5.8 degrees
// from the source on average where a plain decimation at half the faces turned 5.4, and every
// way of putting the points back on the source (nearest point, robust fits, the high-poly itself)
// crumpled them more, since the ripples are in the source.
//
// So the output's own faces are faired: each face's normal is averaged with its neighbours'
// (by distance, and only with those turned less than about sigmaDegrees from it, so a door stays
// apart from the window frame beside it), and the points are then moved to fit the averaged
// normals (each a little way along its faces' normals toward their planes). No point moves more
// than maxMoveEdges of the median edge. The ripples go to the normal map, which is baked from the
// source afterwards.
//
// Measured at 5,000 (thousandths of the diagonal, mean both ways; p99 in brackets): car2 0.40 /
// 0.33 (3.06) off the source before, 0.66 / 0.56 (3.25) after; car3 0.34 / 0.28 -> 0.71 / 0.64;
// car4 0.45 / 0.40 -> 0.83 / 0.77 -- about a millimetre on a car, the worst places unchanged --
// and the doors flat, the window frames, sills and arches kept. At 15 degrees car3's and car4's
// windows melted into the doors; at 10 they stay. 6 passes and a cap of 0.1 left car2's doors
// dented; 20 passes and 0.3 rounded its windows off.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    public static class SurfaceFair
    {
        public sealed class Stats
        {
            public int faces, points, capped;
            public float meanMove, maxMove;
            public long ms;
            public override string ToString() =>
                $"fair: {faces} faces, {points} points, mean move {meanMove:G3}, max {maxMove:G3}, {capped} capped, {ms} ms";
        }

        /// <summary>Passes of the faces' normals averaged with their neighbours'.</summary>
        public static int normalIterations = 12;

        /// <summary>Normals further apart than about this are not averaged together, degrees.</summary>
        public static float sigmaDegrees = 10f;

        /// <summary>Passes moving the points onto the averaged normals.</summary>
        public static int vertexIterations = 15;

        /// <summary>Farthest a point moves, in median edges.</summary>
        public static float maxMoveEdges = 0.2f;

        public static Stats Apply(Vector3[] positions, List<int[]> faces)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stats = new Stats { faces = faces.Count, points = positions.Length };
            var n = positions.Length;
            var m = faces.Count;
            if (n == 0 || m == 0) return stats;

            // Each point's faces, and each face's neighbours (faces sharing a point with it).
            var pointFaces = new List<int>[n];
            for (var v = 0; v < n; v++) pointFaces[v] = new List<int>(6);
            for (var f = 0; f < m; f++)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;
                foreach (var v in face) pointFaces[v].Add(f);
            }
            var around = new int[m][];
            Parallel.For(0, m, BackgroundScheduler.Options, f =>
            {
                var face = faces[f];
                if (face == null || face.Length < 3) { around[f] = Array.Empty<int>(); return; }
                var set = new HashSet<int>();
                foreach (var v in face) foreach (var g in pointFaces[v]) set.Add(g);
                var list = new int[set.Count];
                set.CopyTo(list);
                around[f] = list;
            });

            var p = positions;
            var start = (Vector3[])p.Clone();
            var normal = new Vector3[m];
            var centre = new Vector3[m];
            var area = new float[m];
            void Faces()
            {
                Parallel.For(0, m, BackgroundScheduler.Options, f =>
                {
                    var face = faces[f];
                    if (face == null || face.Length < 3) { normal[f] = Vector3.zero; area[f] = 0f; return; }
                    var s = Vector3.zero;
                    var c = Vector3.zero;
                    for (var k = 1; k + 1 < face.Length; k++)
                        s += Vector3.Cross(p[face[k]] - p[face[0]], p[face[k + 1]] - p[face[0]]);
                    foreach (var v in face) c += p[v];
                    var l = s.magnitude;
                    area[f] = 0.5f * l;
                    normal[f] = l > 0f ? s / l : Vector3.zero;
                    centre[f] = c / face.Length;
                });
            }
            Faces();

            var edges = new List<float>(m * 4);
            foreach (var face in faces)
                if (face != null)
                    for (var k = 0; k < face.Length; k++)
                        edges.Add((p[face[k]] - p[face[(k + 1) % face.Length]]).magnitude);
            edges.Sort();
            var edge = edges.Count > 0 ? edges[edges.Count / 2] : 0f;
            if (!(edge > 0f)) return stats;
            var spatial = 1f / (2f * edge * edge);
            var range = 2f * Mathf.Sin(0.5f * sigmaDegrees * Mathf.Deg2Rad);
            var rangeWeight = 1f / (2f * range * range);

            var filtered = new Vector3[m];
            for (var it = 0; it < normalIterations; it++)
            {
                Parallel.For(0, m, BackgroundScheduler.Options, f =>
                {
                    if (normal[f] == Vector3.zero) { filtered[f] = normal[f]; return; }
                    var s = Vector3.zero;
                    foreach (var g in around[f])
                    {
                        var d = (centre[g] - centre[f]).sqrMagnitude;
                        var dn = (normal[g] - normal[f]).sqrMagnitude;
                        s += area[g] * Mathf.Exp(-d * spatial - dn * rangeWeight) * normal[g];
                    }
                    filtered[f] = s.sqrMagnitude > 0f ? s.normalized : normal[f];
                });
                Array.Copy(filtered, normal, m);
            }

            var cap = maxMoveEdges * edge;
            var next = new Vector3[n];
            var centres = new Vector3[m];
            for (var it = 0; it < vertexIterations; it++)
            {
                // Centres of the faces as they stand; the averaged normals held.
                Parallel.For(0, m, BackgroundScheduler.Options, f =>
                {
                    var face = faces[f];
                    if (face == null || face.Length < 3) return;
                    var c = Vector3.zero;
                    foreach (var v in face) c += p[v];
                    centres[f] = c / face.Length;
                });
                Parallel.For(0, n, BackgroundScheduler.Options, v =>
                {
                    var fs = pointFaces[v];
                    if (fs.Count == 0) { next[v] = p[v]; return; }
                    var move = Vector3.zero;
                    foreach (var f in fs)
                    {
                        var nf = normal[f];
                        move += nf * Vector3.Dot(nf, centres[f] - p[v]);
                    }
                    var x = p[v] + move / fs.Count;
                    var d = x - start[v];
                    if (cap > 0f && d.magnitude > cap) x = start[v] + d * (cap / d.magnitude);
                    next[v] = x;
                });
                Array.Copy(next, p, n);
            }

            double sum = 0;
            for (var v = 0; v < n; v++)
            {
                var d = (p[v] - start[v]).magnitude;
                sum += d;
                stats.maxMove = Mathf.Max(stats.maxMove, d);
                if (cap > 0f && d >= 0.999f * cap) stats.capped++;
            }
            stats.meanMove = (float)(sum / n);
            stats.ms = watch.ElapsedMilliseconds;
            return stats;
        }
    }
}
