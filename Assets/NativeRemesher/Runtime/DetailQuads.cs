// A detail-first quad mesh: the quads are built from an error-driven simplification of the input
// instead of from a lattice.
//
// The user's reference for the helmet head (heradremesh.fbx, 6,628 faces) keeps the rail, its
// slots and the screws at a face count where our lattice output melts them: a lattice cell a
// centimetre across cannot show a centimetre-wide slot unless its edges happen to run along the
// slot's rims, and a lattice has no reason to. A quadric-error simplification of the same input
// keeps exactly those rims as edges (92,000 triangles of it render crisper than the reference) and
// spends its triangles where the shape needs them. So:
//
//   1. simplify the input to about half as many triangles as quads are wanted;
//   2. pair neighbouring triangles into quads, best-shaped and flattest first, never across a crease;
//   3. split every face once, Catmull-Clark's topology (a quad into four, a triangle into three):
//      all quads, and every rim the simplification kept is still an edge chain;
//   4. put the new points on the input, then FeatureFit everything onto the rims and corners.
//
// The result is irregular -- the simplification's vertices keep their valence -- which is what the
// reference is too: it trades a clean grid for the shape.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    public static class DetailQuads
    {
        public sealed class Stats
        {
            public int baseTriangles, pairs, loneTriangles, quads;
            public long decimateMs, buildMs, fitMs;
            public FeatureFit.Stats fit;
            public override string ToString() =>
                $"detail base={baseTriangles} pairs={pairs} lone={loneTriangles} quads={quads} decimate={decimateMs}ms build={buildMs}ms fit={fitMs}ms {fit}";
        }

        /// <summary>Simplified triangles per wanted quad, with the split.</summary>
        public static float trianglesPerQuad = 0.42f;

        /// <summary>
        /// Split every face into quads (all quads). Off: the paired simplification as it is, quads
        /// and the triangles left unpaired -- quad-dominant, as the reference is (627 of its 6,628
        /// faces are triangles) -- on a base four times as dense for the same count.
        /// </summary>
        public static bool split = true;

        /// <summary>Simplified triangles per wanted face, without the split.</summary>
        public static float trianglesPerFaceUnsplit = 1.75f;

        /// <summary>Two triangles are paired only when their normals are within this angle.</summary>
        public static float pairMaxDihedralDegrees = 50f;

        /// <summary>Smallest pair score (worst corner's sine, times flatness) accepted.</summary>
        public static float pairMinScore = 0.15f;

        /// <summary>FeatureFit passes after the split.</summary>
        public static int fitIterations = 3;

        /// <summary>Tangential smoothing passes over the split's new points before the fit.</summary>
        public static int relaxIterations = 2;

        /// <summary>
        /// Exponent on (median spacing / curvature spacing) weighting the simplification's planes:
        /// curved ground (a screw, a slot's rim) costs more to flatten than its size says, so it
        /// keeps triangles the way a curvature-sized remesh would. 0: plain quadric error.
        /// </summary>
        public static float curvatureWeightPower = 2f;

        /// <summary>Largest ratio of that weight's spacing over and under its median.</summary>
        public static float curvatureMaxRatio = 4f;

        /// <summary>Simplification keeps creases sharper than this as edge chains (0: off).</summary>
        public static float creaseAngle = 0f;

        public static void Build(Vector3[] sourcePositions, int[] sourceTriangles, int targetQuads,
            out Vector3[] positions, out List<int[]> faces, out Stats stats)
        {
            stats = new Stats();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var budget = Mathf.Max(64, Mathf.RoundToInt(targetQuads * (split ? trianglesPerQuad : trianglesPerFaceUnsplit)));
            var options = DecimateOptions.ForTriangleCount(budget);
            if (creaseAngle > 0f) { options.creaseAngle = creaseAngle; options.creaseMinLength = 0f; }
            if (curvatureWeightPower > 0f)
                options.vertexWeights = topology =>
                {
                    var field = ScaleField.FromCurvature(topology, targetQuads, 1f, curvatureMaxRatio);
                    var sorted = (float[])field.spacing.Clone();
                    Array.Sort(sorted);
                    var median = sorted[sorted.Length / 2];
                    var w = new float[field.spacing.Length];
                    for (var v = 0; v < w.Length; v++)
                        w[v] = field.spacing[v] > 0f ? Mathf.Pow(median / field.spacing[v], curvatureWeightPower) : 1f;
                    return w;
                };
            PreDecimate.Decimate(sourcePositions, sourceTriangles, options, out _, out var basePositions,
                out var baseTriangles);
            stats.decimateMs = watch.ElapsedMilliseconds;
            stats.baseTriangles = baseTriangles.Length / 3;

            watch.Restart();
            var baseFaces = Pair(basePositions, baseTriangles, stats);
            bool[] isNew;
            if (split) Split(basePositions, baseFaces, out positions, out faces, out isNew);
            else
            {
                positions = basePositions;
                faces = baseFaces;
                isNew = new bool[positions.Length];
            }
            stats.quads = faces.Count;

            var surface = new SpatialGrid(sourcePositions, sourceTriangles);
            var placed = positions;
            Parallel.For(0, placed.Length, BackgroundScheduler.Options, v =>
                placed[v] = surface.NearestPoint(placed[v], out _));
            Relax(positions, faces, isNew, surface, relaxIterations);
            stats.buildMs = watch.ElapsedMilliseconds;

            watch.Restart();
            if (fitIterations > 0)
                stats.fit = FeatureFit.Fit(positions, faces, sourcePositions, sourceTriangles, fitIterations);
            stats.fitMs = watch.ElapsedMilliseconds;
        }

        // Greedy matching on the shared edges, best quad first.
        static List<int[]> Pair(Vector3[] p, int[] t, Stats stats)
        {
            var count = t.Length / 3;
            var normals = new Vector3[count];
            for (var f = 0; f < count; f++)
                normals[f] = Vector3.Cross(p[t[3 * f + 1]] - p[t[3 * f]], p[t[3 * f + 2]] - p[t[3 * f]]).normalized;
            var halfEdge = new Dictionary<(int, int), int>(3 * count);
            for (var f = 0; f < count; f++)
                for (var k = 0; k < 3; k++)
                    halfEdge[(t[3 * f + k], t[3 * f + (k + 1) % 3])] = f;
            var minDot = Mathf.Cos(pairMaxDihedralDegrees * Mathf.Deg2Rad);
            var candidates = new List<(float score, int f, int g, int[] quad)>();
            for (var f = 0; f < count; f++)
                for (var k = 0; k < 3; k++)
                {
                    int a = t[3 * f + k], b = t[3 * f + (k + 1) % 3], x = t[3 * f + (k + 2) % 3];
                    if (!halfEdge.TryGetValue((b, a), out var g) || g <= f) continue;
                    var dot = Vector3.Dot(normals[f], normals[g]);
                    if (dot < minDot) continue;
                    var y = -1;
                    for (var j = 0; j < 3; j++)
                        if (t[3 * g + j] != a && t[3 * g + j] != b) y = t[3 * g + j];
                    if (y < 0 || y == x) continue;
                    var quad = new[] { a, y, b, x };
                    var worst = 1f;
                    for (var i = 0; i < 4; i++)
                    {
                        Vector3 prev = p[quad[(i + 3) % 4]], cur = p[quad[i]], next = p[quad[(i + 1) % 4]];
                        var e0 = (prev - cur).normalized;
                        var e1 = (next - cur).normalized;
                        var sin = Vector3.Cross(e0, e1).magnitude;
                        // A reflex corner (the quad folds over its diagonal) scores nothing.
                        if (Vector3.Dot(Vector3.Cross(cur - prev, next - cur), normals[f] + normals[g]) <= 0f) sin = 0f;
                        worst = Mathf.Min(worst, sin);
                    }
                    var score = worst * dot;
                    if (score >= pairMinScore) candidates.Add((score, f, g, quad));
                }
            candidates.Sort((u, v) => v.score.CompareTo(u.score));
            var used = new bool[count];
            var faces = new List<int[]>(count);
            foreach (var c in candidates)
            {
                if (used[c.f] || used[c.g]) continue;
                used[c.f] = used[c.g] = true;
                faces.Add(c.quad);
                stats.pairs++;
            }
            for (var f = 0; f < count; f++)
                if (!used[f])
                {
                    faces.Add(new[] { t[3 * f], t[3 * f + 1], t[3 * f + 2] });
                    stats.loneTriangles++;
                }
            return faces;
        }

        // One Catmull-Clark topology step with the old points left where they are.
        static void Split(Vector3[] basePositions, List<int[]> baseFaces, out Vector3[] positions,
            out List<int[]> faces, out bool[] isNew)
        {
            var list = new List<Vector3>(basePositions);
            var mid = new Dictionary<(int, int), int>();
            faces = new List<int[]>(4 * baseFaces.Count);
            int Mid(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                if (mid.TryGetValue(key, out var m)) return m;
                m = list.Count;
                list.Add(0.5f * (basePositions[a] + basePositions[b]));
                mid[key] = m;
                return m;
            }
            foreach (var face in baseFaces)
            {
                var n = face.Length;
                var centre = Vector3.zero;
                foreach (var v in face) centre += basePositions[v];
                var c = list.Count;
                list.Add(centre / n);
                var mids = new int[n];
                for (var i = 0; i < n; i++) mids[i] = Mid(face[i], face[(i + 1) % n]);
                for (var i = 0; i < n; i++)
                    faces.Add(new[] { face[i], mids[i], c, mids[(i + n - 1) % n] });
            }
            positions = list.ToArray();
            isNew = new bool[positions.Length];
            for (var v = basePositions.Length; v < positions.Length; v++) isNew[v] = true;
        }

        // The split's new points evened out along the surface; the simplification's points stay.
        static void Relax(Vector3[] positions, List<int[]> faces, bool[] isNew, SpatialGrid surface, int iterations)
        {
            if (iterations <= 0) return;
            var n = positions.Length;
            var ring = new List<int>[n];
            for (var v = 0; v < n; v++) ring[v] = new List<int>(4);
            foreach (var face in faces)
                for (var k = 0; k < face.Length; k++)
                {
                    int a = face[k], b = face[(k + 1) % face.Length];
                    if (!ring[a].Contains(b)) ring[a].Add(b);
                    if (!ring[b].Contains(a)) ring[b].Add(a);
                }
            var next = new Vector3[n];
            for (var it = 0; it < iterations; it++)
            {
                Parallel.For(0, n, BackgroundScheduler.Options, v =>
                {
                    if (!isNew[v] || ring[v].Count == 0) { next[v] = positions[v]; return; }
                    var c = Vector3.zero;
                    foreach (var w in ring[v]) c += positions[w];
                    c /= ring[v].Count;
                    var p = surface.NearestPoint(0.5f * (positions[v] + c), out _, out var normal);
                    next[v] = p;
                });
                Array.Copy(next, positions, n);
            }
        }
    }
}
