// Put the output's vertices where the input's shape is, the way dual contouring places its vertices.
//
// Measured on the helmet head against the reference remesh the user supplied (heradremesh.fbx, 6,628
// faces): round the rail our cells are as dense as the reference's or denser, and the result still
// reads as melted -- the slots, the screw and the rail's rims are gone. The reference's vertices sit
// ON the rims and corners; ours sit wherever the lattice put them, and a sharp edge running between
// two vertices is averaged into a soft bump.
//
// So each output vertex is moved to the point that best fits the input faces nearest it: the input
// triangles are shared out to their nearest output vertex, each adds its plane, area-weighted, to
// that vertex's quadric, and the vertex goes to the quadric's minimum. The directions the quadric
// does not constrain are left alone (truncated pseudo-inverse, as dual contouring does): on flat
// ground that is a projection onto the plane, on a crease a move onto the crease line, at a corner a
// move onto the corner. A move is capped at a fraction of the local edge and refused if it would
// turn a face over.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    public static class FeatureFit
    {
        public sealed class Stats
        {
            public int vertices, moved, refused, unassigned;
            public float meanMoveCells;
            public override string ToString() =>
                $"fit v={vertices} moved={moved} refused={refused} unassigned={unassigned} meanMove={meanMoveCells:F3} cells";
        }

        /// <summary>Directions whose quadric weight is below this fraction of the strongest are left free.</summary>
        public static float truncation = 0.05f;

        /// <summary>Weight of the one-ring's quadrics added to a vertex's own.</summary>
        public static float neighbourWeight = 0.25f;

        /// <summary>Largest move, in local edge lengths.</summary>
        public static float maxMove = 0.5f;

        /// <summary>An input triangle is shared out only to a vertex whose normal is within this of its own (dot).</summary>
        public static float minNormalDot = -0.3f;

        /// <summary>Faces around a moved vertex must keep their normal within this (dot) of what it was.</summary>
        public static float minFaceTurnDot = 0.3f;

        public static Stats Fit(Vector3[] positions, List<int[]> faces, Vector3[] sourcePositions,
            int[] sourceTriangles, int iterations)
        {
            var stats = new Stats { vertices = positions.Length };
            var n = positions.Length;
            if (n == 0 || faces.Count == 0 || sourceTriangles == null || sourceTriangles.Length < 3) return stats;

            // Vertex -> faces and one-ring.
            var vertexFaces = new List<int>[n];
            var ring = new HashSet<int>[n];
            for (var v = 0; v < n; v++) { vertexFaces[v] = new List<int>(4); ring[v] = new HashSet<int>(); }
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    int a = face[k], b = face[(k + 1) % face.Length];
                    vertexFaces[a].Add(f);
                    ring[a].Add(b);
                    ring[b].Add(a);
                }
            }
            var edge = new float[n];
            double edgeSum = 0;
            var edgeCount = 0;
            for (var v = 0; v < n; v++)
            {
                var s = 0f;
                foreach (var w in ring[v]) s += (positions[w] - positions[v]).magnitude;
                edge[v] = ring[v].Count > 0 ? s / ring[v].Count : 0f;
                if (ring[v].Count > 0) { edgeSum += edge[v]; edgeCount++; }
            }
            var h = (float)(edgeSum / Math.Max(1, edgeCount));
            if (h <= 0f) return stats;

            var st = sourceTriangles.Length / 3;
            var owner = new int[st];
            var q = new double[n * 10];   // a00 a01 a02 a11 a12 a22, b0 b1 b2, area
            double moveSum = 0;
            var moveCount = 0;

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var normals = VertexNormals(positions, faces);
                var grid = new VertexHash(positions, h);
                Parallel.For(0, st, BackgroundScheduler.Options, t =>
                {
                    Vector3 a = sourcePositions[sourceTriangles[3 * t]],
                        b = sourcePositions[sourceTriangles[3 * t + 1]],
                        c = sourcePositions[sourceTriangles[3 * t + 2]];
                    var nrm = Vector3.Cross(b - a, c - a);
                    var len = nrm.magnitude;
                    if (len <= 0f) { owner[t] = -1; return; }
                    nrm /= len;
                    owner[t] = grid.Nearest((a + b + c) / 3f, 1.5f * h, normals, nrm, minNormalDot);
                });

                Array.Clear(q, 0, q.Length);
                for (var t = 0; t < st; t++)
                {
                    var v = owner[t];
                    if (v < 0) continue;
                    Vector3 a = sourcePositions[sourceTriangles[3 * t]],
                        b = sourcePositions[sourceTriangles[3 * t + 1]],
                        c = sourcePositions[sourceTriangles[3 * t + 2]];
                    var cr = Vector3.Cross(b - a, c - a);
                    double area = 0.5 * cr.magnitude;
                    var nx = cr.x / (2 * area); var ny = cr.y / (2 * area); var nz = cr.z / (2 * area);
                    var d = nx * a.x + ny * a.y + nz * a.z;
                    var o = 10 * v;
                    q[o] += area * nx * nx; q[o + 1] += area * nx * ny; q[o + 2] += area * nx * nz;
                    q[o + 3] += area * ny * ny; q[o + 4] += area * ny * nz; q[o + 5] += area * nz * nz;
                    q[o + 6] += area * nx * d; q[o + 7] += area * ny * d; q[o + 8] += area * nz * d;
                    q[o + 9] += area;
                }

                var target = new Vector3[n];
                var has = new bool[n];
                Parallel.For(0, n, BackgroundScheduler.Options, v =>
                {
                    var m = new double[10];
                    for (var k = 0; k < 10; k++) m[k] = q[10 * v + k];
                    if (neighbourWeight > 0f)
                        foreach (var w in ring[v])
                            for (var k = 0; k < 10; k++) m[k] += neighbourWeight * q[10 * w + k];
                    if (m[9] <= 0) return;
                    var x = positions[v];
                    // Gradient at x: b - A x.
                    var g0 = m[6] - (m[0] * x.x + m[1] * x.y + m[2] * x.z);
                    var g1 = m[7] - (m[1] * x.x + m[3] * x.y + m[4] * x.z);
                    var g2 = m[8] - (m[2] * x.x + m[4] * x.y + m[5] * x.z);
                    Eigen3(m[0], m[1], m[2], m[3], m[4], m[5], out var values, out var vectors);
                    var top = Math.Max(values[0], Math.Max(values[1], values[2]));
                    if (top <= 0) return;
                    double dx = 0, dy = 0, dz = 0;
                    for (var i = 0; i < 3; i++)
                    {
                        if (values[i] < truncation * top) continue;
                        double ux = vectors[3 * i], uy = vectors[3 * i + 1], uz = vectors[3 * i + 2];
                        var s = (ux * g0 + uy * g1 + uz * g2) / values[i];
                        dx += s * ux; dy += s * uy; dz += s * uz;
                    }
                    var delta = new Vector3((float)dx, (float)dy, (float)dz);
                    var cap = maxMove * (edge[v] > 0f ? edge[v] : h);
                    if (delta.magnitude > cap) delta *= cap / delta.magnitude;
                    target[v] = x + delta;
                    has[v] = true;
                });

                // Applied one vertex at a time, so each check sees its neighbours where they now are.
                for (var v = 0; v < n; v++)
                {
                    if (!has[v]) { if (iteration == 0) stats.unassigned++; continue; }
                    var from = positions[v];
                    var step = target[v] - from;
                    if (step.sqrMagnitude <= 0f) continue;
                    var accepted = false;
                    for (var tries = 0; tries < 3 && !accepted; tries++, step *= 0.5f)
                    {
                        if (KeepsFaces(positions, faces, vertexFaces[v], v, from + step))
                        {
                            positions[v] = from + step;
                            accepted = true;
                            if (iteration == iterations - 1) { stats.moved++; }
                            moveSum += step.magnitude / h;
                            moveCount++;
                        }
                    }
                    if (!accepted && iteration == iterations - 1) stats.refused++;
                }
            }
            stats.meanMoveCells = moveCount > 0 ? (float)(moveSum / moveCount) : 0f;
            return stats;
        }

        static bool KeepsFaces(Vector3[] positions, List<int[]> faces, List<int> around, int v, Vector3 to)
        {
            var from = positions[v];
            foreach (var f in around)
            {
                var before = FaceNormal(positions, faces[f], v, from);
                var after = FaceNormal(positions, faces[f], v, to);
                var lb = before.magnitude;
                var la = after.magnitude;
                if (lb <= 0f) continue;
                if (la < 0.05f * lb) return false;
                if (Vector3.Dot(before, after) < minFaceTurnDot * lb * la) return false;
                // Every corner of the face must keep turning the way the face does.
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    Vector3 p = Pos(positions, face[(k + face.Length - 1) % face.Length], v, to),
                        c = Pos(positions, face[k], v, to),
                        nx = Pos(positions, face[(k + 1) % face.Length], v, to);
                    if (Vector3.Dot(Vector3.Cross(c - p, nx - c), after) <= 0f) return false;
                }
            }
            return true;
        }

        static Vector3 Pos(Vector3[] positions, int i, int v, Vector3 at) => i == v ? at : positions[i];

        static Vector3 FaceNormal(Vector3[] positions, int[] face, int v, Vector3 at)
        {
            var s = Vector3.zero;
            var o = Pos(positions, face[0], v, at);
            for (var k = 1; k + 1 < face.Length; k++)
                s += Vector3.Cross(Pos(positions, face[k], v, at) - o, Pos(positions, face[k + 1], v, at) - o);
            return s;
        }

        static Vector3[] VertexNormals(Vector3[] positions, List<int[]> faces)
        {
            var normals = new Vector3[positions.Length];
            foreach (var face in faces)
            {
                var fn = FaceNormal(positions, face, -1, Vector3.zero);
                foreach (var v in face) normals[v] += fn;
            }
            for (var v = 0; v < normals.Length; v++) normals[v] = normals[v].normalized;
            return normals;
        }

        // Symmetric 3x3 eigen-decomposition by Jacobi rotations; vectors are rows of `vectors`.
        static void Eigen3(double a00, double a01, double a02, double a11, double a12, double a22,
            out double[] values, out double[] vectors)
        {
            var a = new[,] { { a00, a01, a02 }, { a01, a11, a12 }, { a02, a12, a22 } };
            var e = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (var sweep = 0; sweep < 12; sweep++)
            {
                var off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (off < 1e-30) break;
                for (var p = 0; p < 2; p++)
                for (var r = p + 1; r < 3; r++)
                {
                    if (Math.Abs(a[p, r]) < 1e-30) continue;
                    var theta = (a[r, r] - a[p, p]) / (2 * a[p, r]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    var c = 1 / Math.Sqrt(t * t + 1);
                    var s = t * c;
                    for (var k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akr = a[k, r];
                        a[k, p] = c * akp - s * akr;
                        a[k, r] = s * akp + c * akr;
                    }
                    for (var k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], ark = a[r, k];
                        a[p, k] = c * apk - s * ark;
                        a[r, k] = s * apk + c * ark;
                    }
                    for (var k = 0; k < 3; k++)
                    {
                        double ekp = e[k, p], ekr = e[k, r];
                        e[k, p] = c * ekp - s * ekr;
                        e[k, r] = s * ekp + c * ekr;
                    }
                }
            }
            values = new[] { a[0, 0], a[1, 1], a[2, 2] };
            vectors = new double[9];
            for (var i = 0; i < 3; i++)
                for (var k = 0; k < 3; k++)
                    vectors[3 * i + k] = e[k, i];
        }

        // Output vertices hashed on a grid of the mean edge length, for nearest-vertex queries.
        sealed class VertexHash
        {
            readonly Vector3[] m_Positions;
            readonly float m_Cell;
            readonly Dictionary<long, List<int>> m_Cells = new Dictionary<long, List<int>>();

            public VertexHash(Vector3[] positions, float cell)
            {
                m_Positions = positions;
                m_Cell = cell;
                for (var i = 0; i < positions.Length; i++)
                {
                    var k = Key(Cell(positions[i].x), Cell(positions[i].y), Cell(positions[i].z));
                    if (!m_Cells.TryGetValue(k, out var list)) m_Cells[k] = list = new List<int>(4);
                    list.Add(i);
                }
            }

            int Cell(float x) => Mathf.FloorToInt(x / m_Cell);
            static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (uint)(z & 0x1FFFFF);

            public int Nearest(Vector3 p, float radius, Vector3[] normals, Vector3 normal, float minDot)
            {
                var r = Mathf.CeilToInt(radius / m_Cell);
                int cx = Cell(p.x), cy = Cell(p.y), cz = Cell(p.z);
                var best = -1;
                var bestD = radius * radius;
                for (var dx = -r; dx <= r; dx++)
                for (var dy = -r; dy <= r; dy++)
                for (var dz = -r; dz <= r; dz++)
                {
                    if (!m_Cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var list)) continue;
                    foreach (var i in list)
                    {
                        var d = (m_Positions[i] - p).sqrMagnitude;
                        if (d >= bestD) continue;
                        if (Vector3.Dot(normals[i], normal) < minDot) continue;
                        bestD = d;
                        best = i;
                    }
                }
                return best;
            }
        }
    }
}
