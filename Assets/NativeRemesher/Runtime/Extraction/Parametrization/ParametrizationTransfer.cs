// Carries a parametrisation solved on the decimated mesh onto the refined mesh it was refined into.
//
// The solve wants few unknowns (one sparse factorisation of the decimated body is 0.5 s, of the
// refined body 6.5 s); the integer grid map's extraction wants many vertices, because it places each
// output vertex by averaging the samples that fall on it -- extracting off the decimated mesh cost
// the body's p05 corner angle 62 -> 56. Refinement only splits triangles, so every refined triangle
// lies inside one decimated triangle and its corners' (u, v) are that triangle's chart interpolated
// -- exact for a map that is linear per triangle.
//
// What must not be interpolated is which way each vertex's cross is turned against each face's
// chart. Those ranks are rebuilt here the way SeamlessParametrization builds them: round each
// refined vertex's fan, carrying the rank across every edge by the turn between the two decimated
// charts there (zero inside one decimated face, the cut's quarter-turns across a cut). A fan that
// comes back turned is a singularity, and the face it closes on is handed to the grid map as the
// singular face.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public static class ParametrizationTransfer
    {
        public static void ToRefined(RemeshTopology fine, RemeshTopology coarse,
            Vector2[] coarseCornerUv, Vector3[] coarseFaceUAxis, int[] coarseAcross, int[] coarseTurn,
            Func<int, float> spacingAt,
            out Vector3[] latticeNodes, out Vector3[] axes, out float[] spacing,
            out Vector2Int[] cornerNodes, out Vector2[] cornerUv, out Vector3[] faceUAxis,
            out int[] cornerRank, out int[] singularFaces, out int unmapped)
        {
            var P = fine.positions;
            var N = fine.normals;
            var T = fine.triangles;
            var n = P.Length;
            var m = T.Length / 3;
            var CP = coarse.positions;
            var CT = coarse.triangles;

            // Each refined face's decimated face, by its centroid.
            var grid = new SpatialGrid(CP, CT);
            var coarseOf = new int[m];
            cornerUv = new Vector2[3 * m];
            cornerNodes = new Vector2Int[3 * m];
            faceUAxis = new Vector3[m];
            var fineAxis = faceUAxis;
            unmapped = 0;
            var cornerUvOut = cornerUv;
            var cornerNodesOut = cornerNodes;
            var unmappedCount = 0;
            System.Threading.Tasks.Parallel.For(0, m, f =>
            {
                var centroid = (P[T[3 * f]] + P[T[3 * f + 1]] + P[T[3 * f + 2]]) / 3f;
                grid.NearestDistance(centroid, out var c);
                if (c < 0) { coarseOf[f] = -1; System.Threading.Interlocked.Increment(ref unmappedCount); return; }
                coarseOf[f] = c;
                fineAxis[f] = coarseFaceUAxis[c];
                Vector3 a = CP[CT[3 * c]], b = CP[CT[3 * c + 1]], cc = CP[CT[3 * c + 2]];
                for (var k = 0; k < 3; k++)
                {
                    var w = Barycentric(P[T[3 * f + k]], a, b, cc);
                    var uv = coarseCornerUv[3 * c] * w.x + coarseCornerUv[3 * c + 1] * w.y + coarseCornerUv[3 * c + 2] * w.z;
                    cornerUvOut[3 * f + k] = uv;
                    cornerNodesOut[3 * f + k] = new Vector2Int(Mathf.RoundToInt(uv.x), Mathf.RoundToInt(uv.y));
                }
            });
            unmapped = unmappedCount;

            // Quarter-turns from face A's decimated chart to face B's, for neighbouring refined faces.
            int Turn(int fa, int fb)
            {
                int ca = coarseOf[fa], cb = coarseOf[fb];
                if (ca == cb || ca < 0 || cb < 0) return 0;
                for (var k = 0; k < 3; k++)
                    if (coarseAcross[3 * ca + k] == cb) return coarseTurn[3 * ca + k];
                // Not neighbours in the decimated mesh -- a centroid that landed on the wrong side
                // of a decimated edge. The geometric turn between the two charts stands in.
                var na = Vector3.Cross(P[T[3 * fa + 1]] - P[T[3 * fa]], P[T[3 * fa + 2]] - P[T[3 * fa]]).normalized;
                var carried = GlobalCrossField.Transport(fineAxis[fa], na, na);
                var best = 0;
                var bestDot = float.NegativeInfinity;
                var r = carried;
                for (var t = 0; t < 4; t++)
                {
                    var d = Vector3.Dot(r, fineAxis[fb]);
                    if (d > bestDot) { bestDot = d; best = t; }
                    r = Vector3.Cross(na, r);
                }
                return best;
            }

            // Refined half-edge adjacency.
            var across = new int[3 * m];
            var acrossCorner = new int[3 * m];
            for (var i = 0; i < across.Length; i++) across[i] = -1;
            {
                var directed = new Dictionary<long, int>(3 * m, EdgeKey.Comparer);
                for (var f = 0; f < m; f++)
                    for (var k = 0; k < 3; k++)
                    {
                        var key = ((long)T[3 * f + k] << 32) | (uint)T[3 * f + (k + 1) % 3];
                        directed[key] = directed.ContainsKey(key) ? -2 : 3 * f + k;
                    }
                for (var f = 0; f < m; f++)
                    for (var k = 0; k < 3; k++)
                    {
                        int a = T[3 * f + k], b = T[3 * f + (k + 1) % 3];
                        if (directed[((long)a << 32) | (uint)b] < 0) continue;
                        if (!directed.TryGetValue(((long)b << 32) | (uint)a, out var other) || other < 0) continue;
                        across[3 * f + k] = other / 3;
                        acrossCorner[3 * f + k] = other % 3;
                    }
            }

            var cornersOf = new List<int>[n];
            for (var c = 0; c < 3 * m; c++) (cornersOf[T[c]] ??= new List<int>(6)).Add(c);

            // Ranks round each fan; the closing face of a turned fan is singular.
            cornerRank = new int[3 * m];
            for (var i = 0; i < cornerRank.Length; i++) cornerRank[i] = -1;
            axes = new Vector3[n];
            var singular = new HashSet<int>();
            var fanCorner = new Dictionary<int, int>();
            var queue = new Queue<int>();
            var startCorner = new int[n];
            for (var v = 0; v < n; v++)
            {
                startCorner[v] = -1;
                var list = cornersOf[v];
                if (list == null) continue;
                fanCorner.Clear();
                foreach (var c in list) fanCorner[c / 3] = c;
                var turned = false;
                foreach (var s in list)
                {
                    if (cornerRank[s] >= 0 || coarseOf[s / 3] < 0) continue;
                    if (startCorner[v] < 0)
                    {
                        startCorner[v] = s;
                        var axis = faceUAxis[s / 3] - N[v] * Vector3.Dot(N[v], faceUAxis[s / 3]);
                        axes[v] = MeshMath.SafeNormalize(axis, OrientationField.ArbitraryTangent(N[v]));
                        cornerRank[s] = 0;
                    }
                    else
                    {
                        // A second fan component: its own geometric rank.
                        cornerRank[s] = GeometricRank(axes[v], N[v], faceUAxis[s / 3]);
                    }
                    queue.Clear();
                    queue.Enqueue(s);
                    while (queue.Count > 0)
                    {
                        var c = queue.Dequeue();
                        var f = c / 3;
                        var k = c % 3;
                        for (var side = 0; side < 2; side++)
                        {
                            var half = side == 0 ? c : 3 * f + (k + 2) % 3;
                            var g = across[half];
                            if (g < 0 || coarseOf[g] < 0 || !fanCorner.TryGetValue(g, out var cg)) continue;
                            var rank = Mod4(cornerRank[c] + Turn(f, g));
                            if (cornerRank[cg] < 0)
                            {
                                cornerRank[cg] = rank;
                                queue.Enqueue(cg);
                            }
                            else if (cornerRank[cg] != rank && !turned)
                            {
                                singular.Add(g);
                                turned = true;
                            }
                        }
                    }
                }
            }
            singularFaces = new int[singular.Count];
            singular.CopyTo(singularFaces);
            Array.Sort(singularFaces);

            // Lattice node, axis length per vertex, from its start face's map.
            latticeNodes = (Vector3[])P.Clone();
            spacing = new float[n];
            for (var v = 0; v < n; v++)
            {
                spacing[v] = spacingAt(v);
                var s = startCorner[v];
                if (s < 0) { axes[v] = OrientationField.ArbitraryTangent(N[v]); continue; }
                var f = s / 3;
                var p0 = P[T[3 * f]];
                var e1 = MeshMath.SafeNormalize(faceUAxis[f], OrientationField.ArbitraryTangent(N[v]));
                var fn = Vector3.Cross(P[T[3 * f + 1]] - p0, P[T[3 * f + 2]] - p0);
                if (fn.sqrMagnitude <= 1e-30f) continue;
                fn.Normalize();
                e1 = MeshMath.SafeNormalize(e1 - fn * Vector3.Dot(fn, e1), e1);
                var e2 = Vector3.Cross(fn, e1);
                var d1 = P[T[3 * f + 1]] - p0;
                var d2 = P[T[3 * f + 2]] - p0;
                double x1 = Vector3.Dot(d1, e1), y1 = Vector3.Dot(d1, e2);
                double x2 = Vector3.Dot(d2, e1), y2 = Vector3.Dot(d2, e2);
                var det = x1 * y2 - y1 * x2;
                if (Math.Abs(det) < 1e-30) continue;
                var u0 = cornerUv[3 * f];
                var du1 = cornerUv[3 * f + 1] - u0;
                var du2 = cornerUv[3 * f + 2] - u0;
                // J = [grad u; grad v], grad = E^-1 (d1, d2).
                double ux = (y2 * du1.x - y1 * du2.x) / det, uy = (-x2 * du1.x + x1 * du2.x) / det;
                double vx = (y2 * du1.y - y1 * du2.y) / det, vy = (-x2 * du1.y + x1 * du2.y) / det;
                var dj = ux * vy - uy * vx;
                if (dj <= 1e-30) continue;
                double ia = vy / dj, ib = -uy / dj, ic = -vx / dj, id = ux / dj;
                var uv = cornerUv[s];
                double du = Math.Round(uv.x) - uv.x, dv = Math.Round(uv.y) - uv.y;
                var move = e1 * (float)(ia * du + ib * dv) + e2 * (float)(ic * du + id * dv);
                var h = spacing[v];
                if (move.magnitude > 1.5f * h) move = move.normalized * (1.5f * h);
                var node = P[v] + move;
                latticeNodes[v] = node - N[v] * Vector3.Dot(N[v], node - P[v]);
                var cell = 0.5f * (float)(Math.Sqrt(ia * ia + ic * ic) + Math.Sqrt(ib * ib + id * id));
                if (cell > 0.25f * h && cell < 4f * h) spacing[v] = cell;
            }
        }

        static int GeometricRank(Vector3 q, Vector3 n, Vector3 target)
        {
            var best = 0;
            var bestDot = float.NegativeInfinity;
            var r = q;
            for (var t = 0; t < 4; t++)
            {
                var d = Vector3.Dot(r, target);
                if (d > bestDot) { bestDot = d; best = t; }
                r = Vector3.Cross(n, r);
            }
            return best;
        }

        static int Mod4(int k) => ((k % 4) + 4) % 4;

        static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var v0 = b - a;
            var v1 = c - a;
            var v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            var denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) < 1e-30f) return new Vector3(1f, 0f, 0f);
            var y = (d11 * d20 - d01 * d21) / denom;
            var z = (d00 * d21 - d01 * d20) / denom;
            return new Vector3(1f - y - z, y, z);
        }
    }
}
