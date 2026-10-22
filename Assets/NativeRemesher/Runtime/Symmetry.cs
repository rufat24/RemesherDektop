// Mirror symmetry in X, Y or Z: the input is cut at the plane, one half
// is remeshed with the cut as an open border, and the result is mirrored and welded along it.
//
// The quad layout lays an open border as a patch side (QuantizedLayout.borderLayout), so the cut
// comes out as one straight grid line; its vertices are put exactly on the plane and shared by the
// two halves. The other extractors also run on the half, but leave their border ragged; the weld
// then joins what lies on the plane and leaves the rest open.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public enum SymmetryAxis { None = 0, X = 1, Y = 2, Z = 3 }

    public static class Symmetry
    {
        /// <summary>Statistics of the last <see cref="Mirror"/>.</summary>
        public sealed class Stats
        {
            public int halfVertices, seamVertices, snapped, halfFaces;
            public float plane;
            public override string ToString() =>
                $"plane={plane:G4} halfVerts={halfVertices} seam={seamVertices} snapped={snapped} halfFaces={halfFaces}";
        }

        /// <summary>
        /// The plane: the local origin when the mesh straddles it about evenly (a character modelled
        /// round its centre line), else the middle of its bounds along the axis.
        /// </summary>
        public static float PlaneOf(Vector3[] positions, SymmetryAxis axis)
        {
            var k = (int)axis - 1;
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (var p in positions) { lo = Mathf.Min(lo, p[k]); hi = Mathf.Max(hi, p[k]); }
            if (!(hi > lo)) return 0f;
            var mid = 0.5f * (lo + hi);
            return Mathf.Abs(mid) <= 0.05f * (hi - lo) ? 0f : mid;
        }

        /// <summary>
        /// The part of the mesh on the positive side of the plane, triangles crossing it split there.
        /// Vertices within a hair of the plane are put on it.
        /// </summary>
        public static void Clip(Vector3[] positions, int[] triangles, SymmetryAxis axis, float plane,
            out Vector3[] outPositions, out int[] outTriangles)
            => Clip(positions, triangles, axis, plane, out outPositions, out outTriangles, out _);

        /// <summary>As above, and per kept triangle the input triangle it is part of.</summary>
        public static void Clip(Vector3[] positions, int[] triangles, SymmetryAxis axis, float plane,
            out Vector3[] outPositions, out int[] outTriangles, out int[] parentTriangle)
        {
            var k = (int)axis - 1;
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (var p in positions) { lo = Mathf.Min(lo, p[k]); hi = Mathf.Max(hi, p[k]); }
            var eps = 1e-6f * Mathf.Max(1e-12f, hi - lo);
            var n = positions.Length;
            var d = new float[n];
            for (var i = 0; i < n; i++)
            {
                d[i] = positions[i][k] - plane;
                if (Mathf.Abs(d[i]) <= eps) d[i] = 0f;
            }
            var pos = new List<Vector3>(n / 2 + 16);
            var map = new int[n];
            for (var i = 0; i < n; i++) map[i] = -1;
            int Keep(int i)
            {
                if (map[i] >= 0) return map[i];
                var p = positions[i];
                if (d[i] == 0f) p[k] = plane;
                map[i] = pos.Count;
                pos.Add(p);
                return map[i];
            }
            var cut = new Dictionary<long, int>(Extraction.EdgeKey.Comparer);
            int Cut(int a, int b)
            {
                var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                if (cut.TryGetValue(key, out var id)) return id;
                var t = d[a] / (d[a] - d[b]);
                var p = Vector3.LerpUnclamped(positions[a], positions[b], t);
                p[k] = plane;
                cut[key] = pos.Count;
                pos.Add(p);
                return pos.Count - 1;
            }
            var tris = new List<int>(triangles.Length / 2 + 16);
            var parents = new List<int>(triangles.Length / 6 + 16);
            var current = 0;
            void Add(int a, int b, int c)
            {
                if (a == b || b == c || c == a) return;
                tris.Add(a); tris.Add(b); tris.Add(c);
                parents.Add(current);
            }
            for (var t = 0; t < triangles.Length; t += 3)
            {
                current = t / 3;
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                var ia = d[a] >= 0f; var ib = d[b] >= 0f; var ic = d[c] >= 0f;
                if (ia && ib && ic)
                {
                    // On the plane entirely: a sliver of the centre line, dropped.
                    if (d[a] == 0f && d[b] == 0f && d[c] == 0f) continue;
                    Add(Keep(a), Keep(b), Keep(c));
                    continue;
                }
                if (!ia && !ib && !ic) continue;
                // Rotated so the lone vertex (the one inside, or the one outside) is first.
                var inside = (ia ? 1 : 0) + (ib ? 1 : 0) + (ic ? 1 : 0);
                int p0 = a, p1 = b, p2 = c;
                bool Lone(int v) => inside == 1 ? d[v] >= 0f : d[v] < 0f;
                if (Lone(b)) { p0 = b; p1 = c; p2 = a; }
                else if (Lone(c)) { p0 = c; p1 = a; p2 = b; }
                if (inside == 1)
                {
                    // p0 inside: one triangle up to the plane. A vertex on the plane counts as inside,
                    // so the cut points here are strictly between.
                    Add(Keep(p0), Cut(p0, p1), Cut(p0, p2));
                }
                else
                {
                    // p0 outside: the quad p1 p2 and the two cut points.
                    var q1 = Cut(p0, p1);
                    var q2 = Cut(p0, p2);
                    Add(q1, Keep(p1), Keep(p2));
                    Add(q1, Keep(p2), q2);
                }
            }
            outPositions = pos.ToArray();
            outTriangles = tris.ToArray();
            parentTriangle = parents.ToArray();
        }

        /// <summary>
        /// Border vertices within half the mean edge length of the plane, put on it: decimation
        /// keeps a border only to within its error, and the cut must stay in the plane.
        /// </summary>
        public static Vector3[] LockBorderToPlane(Vector3[] positions, int[] triangles, SymmetryAxis axis, float plane)
        {
            var k = (int)axis - 1;
            var count = new Dictionary<long, int>(triangles.Length, Extraction.EdgeKey.Comparer);
            double total = 0;
            for (var t = 0; t < triangles.Length; t += 3)
                for (var i = 0; i < 3; i++)
                {
                    int a = triangles[t + i], b = triangles[t + (i + 1) % 3];
                    var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    count[key] = count.TryGetValue(key, out var c) ? c + 1 : 1;
                    total += (positions[b] - positions[a]).magnitude;
                }
            var tolerance = 0.5f * (float)(total / Math.Max(1, triangles.Length));
            var result = (Vector3[])positions.Clone();
            foreach (var kv in count)
            {
                if (kv.Value != 1) continue;
                foreach (var v in new[] { (int)(kv.Key >> 32), (int)(kv.Key & 0xffffffff) })
                {
                    if (Mathf.Abs(result[v][k] - plane) > tolerance) continue;
                    var p = result[v];
                    p[k] = plane;
                    result[v] = p;
                }
            }
            return result;
        }

        /// <summary>
        /// The half's result mirrored across the plane and welded on it: open-border vertices within
        /// <paramref name="snapDistance"/> of the plane are put on it and shared.
        /// </summary>
        public static QuadExtractionResult Mirror(QuadExtractionResult half, SymmetryAxis axis, float plane,
            float snapDistance, Stats stats)
        {
            var k = (int)axis - 1;
            var n = half.positions.Length;
            var positions = (Vector3[])half.positions.Clone();
            stats.plane = plane;
            stats.halfVertices = n;
            stats.halfFaces = half.faces.Count;

            // Open edges: the cut, and any border the input had.
            var edgeUse = new Dictionary<long, int>(Extraction.EdgeKey.Comparer);
            foreach (var f in half.faces)
                for (var i = 0; i < f.Length; i++)
                {
                    int a = f[i], b = f[(i + 1) % f.Length];
                    var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    edgeUse[key] = edgeUse.TryGetValue(key, out var u) ? u + 1 : 1;
                }
            var onSeam = new bool[n];
            foreach (var kv in edgeUse)
            {
                if (kv.Value != 1) continue;
                var a = (int)(kv.Key >> 32);
                var b = (int)(kv.Key & 0xffffffff);
                if (Mathf.Abs(positions[a][k] - plane) > snapDistance || Mathf.Abs(positions[b][k] - plane) > snapDistance) continue;
                onSeam[a] = onSeam[b] = true;
            }
            for (var i = 0; i < n; i++)
            {
                if (!onSeam[i]) continue;
                stats.seamVertices++;
                if (positions[i][k] != plane) stats.snapped++;
                var p = positions[i];
                p[k] = plane;
                positions[i] = p;
            }

            // The mirror image: every vertex not on the seam doubled, every face doubled and turned.
            var mirrorOf = new int[n];
            var outPos = new List<Vector3>(2 * n);
            outPos.AddRange(positions);
            for (var i = 0; i < n; i++)
            {
                if (onSeam[i]) { mirrorOf[i] = i; continue; }
                var p = positions[i];
                p[k] = 2f * plane - p[k];
                mirrorOf[i] = outPos.Count;
                outPos.Add(p);
            }
            Vector3[] MirrorNormals(Vector3[] src)
            {
                if (src == null || src.Length != n) return null;
                var res = new Vector3[outPos.Count];
                for (var i = 0; i < n; i++)
                {
                    var v = src[i];
                    if (onSeam[i])
                    {
                        // On the plane a symmetric surface's normal lies in it.
                        v[k] = 0f;
                        v = MeshMath.SafeNormalize(v, src[i]);
                        res[i] = v;
                        continue;
                    }
                    res[i] = v;
                    var w = v;
                    w[k] = -w[k];
                    res[mirrorOf[i]] = w;
                }
                return res;
            }

            var result = new QuadExtractionResult
            {
                positions = outPos.ToArray(),
                normals = MirrorNormals(half.normals),
                shadingNormals = MirrorNormals(half.shadingNormals),
                census = half.census,
                stageTimes = half.stageTimes,
                subdivideStats = half.subdivideStats,
                collapseStats = half.collapseStats,
                directionStats = half.directionStats,
                cleanupStats = half.cleanupStats,
                traceStats = half.traceStats,
                holeFillStats = half.holeFillStats,
                regularizeStats = half.regularizeStats,
                trianglePairingStats = half.trianglePairingStats,
                trianglePairingAfterNgonStats = half.trianglePairingAfterNgonStats,
                ngonSplitStats = half.ngonSplitStats,
                repairStats = half.repairStats,
                orientStats = half.orientStats,
                relaxStats = half.relaxStats,
                snapStats = half.snapStats,
                creaseLoopStats = half.creaseLoopStats,
                integerGridStats = half.integerGridStats,
                parametrizationTraceStats = half.parametrizationTraceStats,
            };
            foreach (var f in half.faces) result.faces.Add((int[])f.Clone());
            foreach (var f in half.faces)
            {
                // Reflected, so wound the other way to face out.
                var g = new int[f.Length];
                for (var i = 0; i < f.Length; i++) g[i] = mirrorOf[f[f.Length - 1 - i]];
                result.faces.Add(g);
            }
            foreach (var f in result.faces)
            {
                if (f.Length == 4) result.quadCount++;
                else if (f.Length == 3) result.triangleCount++;
                else result.ngonCount++;
            }
            return result;
        }
    }
}
