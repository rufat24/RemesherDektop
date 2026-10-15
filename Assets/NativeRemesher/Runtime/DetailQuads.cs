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
            public string featureSize;
            public int rejectedSize, creaseEdges;
            public override string ToString() =>
                $"detail base={baseTriangles} pairs={pairs} lone={loneTriangles} quads={quads} decimate={decimateMs}ms build={buildMs}ms fit={fitMs}ms {fit} size[{featureSize} rejected={rejectedSize}] creases={creaseEdges}";
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

        /// <summary>Smooth the simplification's own points too, not only the split's.</summary>
        public static bool relaxAll = false;

        /// <summary>
        /// Exponent on (median spacing / curvature spacing) weighting the simplification's planes:
        /// curved ground (a screw, a slot's rim) costs more to flatten than its size says, so it
        /// keeps triangles the way a curvature-sized remesh would. 0: plain quadric error.
        /// </summary>
        public static float curvatureWeightPower = 0f;

        /// <summary>Largest ratio of that weight's spacing over and under its median.</summary>
        public static float curvatureMaxRatio = 4f;

        /// <summary>
        /// Longest edge allowed where the surface turns: the normals within this many cells of a
        /// point are compared, and where they turn by an angle a the simplification may not leave
        /// edges longer than featureSizeAngle / a of that radius (a circle of radius r comes out in
        /// segments turning by about featureSizeAngle). 0: off -- the quadric budget alone, which
        /// spends almost nothing on a screw: at the reference's 12 densest spots it has 15-36 faces
        /// within 1.5 cm, the simplification 0-4.
        /// </summary>
        public static float featureSizeRadiusCells = 0f;

        /// <summary>Turn per segment, radians, for that limit.</summary>
        public static float featureSizeAngle = 0.5f;

        /// <summary>Smallest edge the limit asks for, in cells.</summary>
        public static float featureSizeMinCells = 0.1f;

        /// <summary>
        /// The limit is scaled as a whole until the triangles it asks for, capped at this many
        /// cells on flat ground, add up to this fraction of the budget: only the sharpest places
        /// are held, the rest is left to the quadric error.
        /// </summary>
        public static float featureSizeBudgetShare = 1f;

        /// <summary>Edge length taken for unlimited ground when counting, in cells.</summary>
        public static float featureSizeFlatCells = 1.5f;

        /// <summary>
        /// Weight of the simplification's planes within edgeRadiusCells of a sharp edge (dihedral
        /// over edgeAngle), against 1 elsewhere: hard-surface rims keep their triangles and organic
        /// folds -- the fabric of the head's neck and face -- give theirs up, which is how the
        /// reference spends its faces. 1: off.
        /// </summary>
        public static float edgeWeight = 30f;

        /// <summary>Dihedral, degrees, of the edges edgeWeight looks for.</summary>
        public static float edgeAngle = 30f;

        /// <summary>Reach of edgeWeight from such an edge, in cells.</summary>
        public static float edgeRadiusCells = 0.3f;

        /// <summary>Simplification keeps creases sharper than this as edge chains (0: off).</summary>
        public static float creaseAngle = 0f;

        /// <summary>Shortest crease kept, in cells (the simplification's own filter needs it above 0).</summary>
        public static float creaseMinCells = 0.15f;

        /// <summary>Straightness (end to end over length) a kept crease needs.</summary>
        public static float creaseStraightness = 0f;

        /// <summary>Weight of a crease's planes against a face's.</summary>
        public static float creaseWeight = 1000f;

        public static void Build(Vector3[] sourcePositions, int[] sourceTriangles, int targetQuads,
            out Vector3[] positions, out List<int[]> faces, out Stats stats)
        {
            stats = new Stats();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var budget = Mathf.Max(64, Mathf.RoundToInt(targetQuads * (split ? trianglesPerQuad : trianglesPerFaceUnsplit)));
            var options = DecimateOptions.ForTriangleCount(budget);
            double totalArea = 0;
            for (var t = 0; t + 2 < sourceTriangles.Length; t += 3)
                totalArea += 0.5 * Vector3.Cross(sourcePositions[sourceTriangles[t + 1]] - sourcePositions[sourceTriangles[t]],
                    sourcePositions[sourceTriangles[t + 2]] - sourcePositions[sourceTriangles[t]]).magnitude;
            var unit = (float)Math.Sqrt(totalArea / Math.Max(1, targetQuads));
            if (creaseAngle > 0f)
            {
                options.creaseAngle = creaseAngle;
                options.creaseMinLength = Mathf.Max(1e-6f, creaseMinCells * unit);
                options.creaseStraightness = creaseStraightness;
                options.creaseWeight = creaseWeight;
            }
            if (featureSizeRadiusCells > 0f)
            {
                double area = 0;
                for (var t = 0; t + 2 < sourceTriangles.Length; t += 3)
                    area += 0.5 * Vector3.Cross(sourcePositions[sourceTriangles[t + 1]] - sourcePositions[sourceTriangles[t]],
                        sourcePositions[sourceTriangles[t + 2]] - sourcePositions[sourceTriangles[t]]).magnitude;
                var cell = (float)Math.Sqrt(area / Math.Max(1, targetQuads));
                var sizes = FeatureSize(sourcePositions, sourceTriangles, featureSizeRadiusCells * cell,
                    featureSizeAngle, featureSizeMinCells * cell, out var sizeStats);
                var scale = ScaleToBudget(sourcePositions, sourceTriangles, sizes, featureSizeFlatCells * cell,
                    featureSizeBudgetShare * budget);
                var floor = featureSizeMinCells * cell;
                for (var i = 0; i < sizes.Length; i++)
                    if (sizes[i] < float.MaxValue) sizes[i] = Mathf.Max(floor, sizes[i] * scale);
                stats.featureSize = sizeStats + $" scale={scale:G3}";
                var lookup = new SpatialGrid(sourcePositions, sourceTriangles);
                options.vertexSizes = points =>
                {
                    var result = new float[points.Length];
                    Parallel.For(0, points.Length, BackgroundScheduler.Options, i =>
                    {
                        lookup.NearestDistance(points[i], out var t);
                        result[i] = t < 0 ? float.MaxValue : Mathf.Min(sizes[sourceTriangles[3 * t]],
                            Mathf.Min(sizes[sourceTriangles[3 * t + 1]], sizes[sourceTriangles[3 * t + 2]]));
                    });
                    return result;
                };
            }
            if (edgeWeight > 1f)
            {
                var reach = edgeRadiusCells * unit;
                options.vertexWeights = topology =>
                {
                    var sharp = FeatureGraph.Detect(topology, edgeAngle).isFeatureVertex;
                    var n = topology.vertexCount;
                    var w = new float[n];
                    var distance = new float[n];
                    var queue = new Queue<int>();
                    for (var v = 0; v < n; v++)
                    {
                        distance[v] = sharp[v] ? 0f : float.MaxValue;
                        if (sharp[v]) queue.Enqueue(v);
                    }
                    // Breadth-first, by summed edge length: near enough for a reach of a third of a cell.
                    while (queue.Count > 0)
                    {
                        var v = queue.Dequeue();
                        for (var k = topology.adjacencyStart[v]; k < topology.adjacencyStart[v + 1]; k++)
                        {
                            var u = topology.adjacency[k];
                            var d = distance[v] + (topology.positions[u] - topology.positions[v]).magnitude;
                            if (d >= distance[u] || d > reach) continue;
                            distance[u] = d;
                            queue.Enqueue(u);
                        }
                    }
                    for (var v = 0; v < n; v++) w[v] = distance[v] <= reach ? edgeWeight : 1f;
                    return w;
                };
            }
            else if (curvatureWeightPower > 0f)
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
            PreDecimate.Decimate(sourcePositions, sourceTriangles, options, out var decimateStats, out var basePositions,
                out var baseTriangles);
            stats.decimateMs = watch.ElapsedMilliseconds;
            stats.rejectedSize = decimateStats.rejectedSize;
            stats.creaseEdges = decimateStats.creaseEdges;
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
            if (relaxAll) for (var v = 0; v < isNew.Length; v++) isNew[v] = true;
            Relax(positions, faces, isNew, surface, relaxIterations);
            stats.buildMs = watch.ElapsedMilliseconds;

            watch.Restart();
            if (fitIterations > 0)
                stats.fit = FeatureFit.Fit(positions, faces, sourcePositions, sourceTriangles, fitIterations);
            stats.fitMs = watch.ElapsedMilliseconds;
        }

        // The factor on the sizes at which the triangles they ask for (equilateral, h capped at
        // `flat`) number `budget`, by bisection on its logarithm.
        static float ScaleToBudget(Vector3[] p, int[] t, float[] sizes, float flat, float budget)
        {
            var count = t.Length / 3;
            var area = new float[count];
            var h = new float[count];
            for (var k = 0; k < count; k++)
            {
                area[k] = 0.5f * Vector3.Cross(p[t[3 * k + 1]] - p[t[3 * k]], p[t[3 * k + 2]] - p[t[3 * k]]).magnitude;
                h[k] = Mathf.Min(sizes[t[3 * k]], Mathf.Min(sizes[t[3 * k + 1]], sizes[t[3 * k + 2]]));
            }
            double Count(double s)
            {
                double n = 0;
                for (var k = 0; k < count; k++)
                {
                    var e = Math.Min(s * h[k], flat);
                    n += area[k] / (0.433 * e * e);
                }
                return n;
            }
            double lo = -12, hi = 12;
            for (var it = 0; it < 50; it++)
            {
                var mid = 0.5 * (lo + hi);
                if (Count(Math.Exp(mid)) > budget) lo = mid; else hi = mid;
            }
            return (float)Math.Exp(0.5 * (lo + hi));
        }

        // Per input vertex, the longest edge the shape allows there: radius / (turn of the normals
        // within radius) x angle, from the welded mesh. Unlimited where the surface is flat.
        static float[] FeatureSize(Vector3[] p, int[] t, float radius, float angle, float minimum, out string info)
        {
            var weld = new Dictionary<Vector3, int>(p.Length);
            var of = new int[p.Length];
            var wp = new List<Vector3>(p.Length);
            for (var i = 0; i < p.Length; i++)
            {
                if (!weld.TryGetValue(p[i], out var w)) { w = wp.Count; weld[p[i]] = w; wp.Add(p[i]); }
                of[i] = w;
            }
            var n = wp.Count;
            var normals = new Vector3[n];
            var degree = new int[n + 1];
            for (var k = 0; k + 2 < t.Length; k += 3)
            {
                int a = of[t[k]], b = of[t[k + 1]], c = of[t[k + 2]];
                var fn = Vector3.Cross(wp[b] - wp[a], wp[c] - wp[a]);
                normals[a] += fn; normals[b] += fn; normals[c] += fn;
                degree[a] += 2; degree[b] += 2; degree[c] += 2;
            }
            for (var v = 0; v < n; v++) normals[v] = MeshMath.SafeNormalize(normals[v], Vector3.up);
            var start = new int[n + 1];
            for (var v = 0; v < n; v++) start[v + 1] = start[v] + degree[v];
            var fill = (int[])start.Clone();
            var adjacency = new int[start[n]];
            for (var k = 0; k + 2 < t.Length; k += 3)
            {
                int a = of[t[k]], b = of[t[k + 1]], c = of[t[k + 2]];
                adjacency[fill[a]++] = b; adjacency[fill[a]++] = c;
                adjacency[fill[b]++] = c; adjacency[fill[b]++] = a;
                adjacency[fill[c]++] = a; adjacency[fill[c]++] = b;
            }
            var welded = new float[n];
            var r2 = radius * radius;
            Parallel.For(0, n, BackgroundScheduler.Options,
                () => (seen: new HashSet<int>(), queue: new List<int>(64)),
                (v, _, local) =>
                {
                    local.seen.Clear();
                    local.queue.Clear();
                    local.queue.Add(v);
                    local.seen.Add(v);
                    var minDot = 1f;
                    for (var q = 0; q < local.queue.Count && q < 4096; q++)
                    {
                        var x = local.queue[q];
                        minDot = Mathf.Min(minDot, Vector3.Dot(normals[v], normals[x]));
                        for (var s = start[x]; s < start[x + 1]; s++)
                        {
                            var y = adjacency[s];
                            if ((wp[y] - wp[v]).sqrMagnitude > r2 || !local.seen.Add(y)) continue;
                            local.queue.Add(y);
                        }
                    }
                    var turn = Mathf.Acos(Mathf.Clamp(minDot, -1f, 1f));
                    welded[v] = turn > 1e-4f ? Mathf.Max(minimum, angle * radius / turn) : float.MaxValue;
                    return local;
                }, _ => { });
            var sizes = new float[p.Length];
            var limited = 0;
            for (var i = 0; i < p.Length; i++)
            {
                sizes[i] = welded[of[i]];
                if (sizes[i] < 4f * radius) limited++;
            }
            info = $"radius={radius:G3} limited(<4r)={limited}/{p.Length}";
            return sizes;
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
