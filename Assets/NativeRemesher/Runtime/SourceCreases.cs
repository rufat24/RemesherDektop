// Which of a hard surface's creases the asked count can carry, judged on the source.
//
// The crease tests run on the 50,000-triangle mesh solved on, edge by edge, and keep whatever
// bends there -- a door handle's rim, a door's gap, the streaks of the solve mesh's slivers -- at
// any count. At 5,000 quads on a car a quad is several centimetres across: a handle is a bump
// inside one or two quads, a door gap a groove a fraction of one, and pinned to them the grid
// bends round them and the door crumples. They belong in the normal map.
//
// So each crease is measured again on the source, at the size of a quad: the source's faces
// within radiusCells of the edge's middle, split by which side of the edge they lie on (by
// position, not by normal, so a groove's two walls fall on their own sides), and the two sides'
// area-weighted normals compared. A crease is kept where they turn at least angleDegrees apart
// and the line they meet along runs with the edge (minAlign) -- a window frame, a sill, a wheel
// arch. A handle averages into the door round it, a gap into the panels either side, a sliver's
// streak into the flat panel it crosses. Kept pieces shorter than minChainCells are dropped as well.
// At a higher count the radius shrinks with the quads, and the handle's rim comes back.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher
{
    public static class SourceCreases
    {
        /// <summary>How far either side of a crease the source is read, in cells.</summary>
        public static float radiusCells = 0.7f;

        /// <summary>The turn between the two sides' normals a kept crease needs, degrees. At 25 the armoured car's shallow plate seams went soft.</summary>
        public static float angleDegrees = 20f;

        /// <summary>How closely the line the two sides meet along must run with the edge (|cosine|).</summary>
        public static float minAlign = 0.85f;

        /// <summary>Kept pieces shorter than this, in cells, are dropped.</summary>
        public static float minChainCells = 1.5f;

        /// <summary>Most source faces read either side of an edge; past it they are sampled evenly.</summary>
        public static int maxSamples = 600;

        /// <summary>Of two parallel kept creases closer than this, in cells, only the longer stays (a rounding read twice).</summary>
        public static float separationCells = 0.7f;

        /// <summary>Diagnostics: where each search writes its mesh and kept creases (OBJ, lines), numbered; null: nowhere.</summary>
        public static string debugPath;
        static int s_DebugDumps;

        public sealed class Stats
        {
            public int creases, kept, short_, unread;
            public long ms;
            public override string ToString() =>
                $"source creases: {kept} of {creases} kept ({short_} in short pieces, {unread} not read), {ms} ms";
        }

        /// <summary>The source's faces, binned for reading at one radius.</summary>
        public sealed class Index
        {
            internal float cellSize;
            internal Vector3[] centre;
            internal Vector3[] areaNormal; // area times unit normal
            internal int[] order;
            internal Dictionary<long, (int start, int count)> bins;
            internal int[] sourceTriangles;

            public static Index Build(Vector3[] positions, int[] triangles, float cellSize)
            {
                var m = triangles.Length / 3;
                var index = new Index
                {
                    cellSize = cellSize, centre = new Vector3[m], areaNormal = new Vector3[m],
                    order = new int[m], sourceTriangles = triangles
                };
                var keys = new long[m];
                Parallel.For(0, m, BackgroundScheduler.Options, f =>
                {
                    Vector3 a = positions[triangles[3 * f]], b = positions[triangles[3 * f + 1]], c = positions[triangles[3 * f + 2]];
                    index.centre[f] = (a + b + c) / 3f;
                    index.areaNormal[f] = 0.5f * Vector3.Cross(b - a, c - a);
                    keys[f] = Key(Vector3Int.FloorToInt(index.centre[f] / cellSize));
                });
                for (var f = 0; f < m; f++) index.order[f] = f;
                Array.Sort(keys, index.order);
                index.bins = new Dictionary<long, (int, int)>();
                for (var i = 0; i < m;)
                {
                    var j = i;
                    while (j < m && keys[j] == keys[i]) j++;
                    index.bins[keys[i]] = (i, j - i);
                    i = j;
                }
                return index;
            }

            internal static long Key(Vector3Int c) =>
                ((long)(c.x & 0x1FFFFF) << 42) | ((long)(c.y & 0x1FFFFF) << 21) | (long)(c.z & 0x1FFFFF);
        }

        /// <summary>
        /// <paramref name="graph"/>'s boundaries, and those of its creases the source bends at across
        /// <paramref name="cell"/>-sized quads. <paramref name="index"/> is reused when built at this radius.
        /// </summary>
        public static FeatureGraph Keep(FeatureGraph graph, RemeshTopology topology, Vector3[] sourcePositions,
            int[] sourceTriangles, float cell, ref Index index, Stats stats = null, FeatureGraph candidates = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats ??= new Stats();
            if (graph == null || topology == null || sourcePositions == null || sourceTriangles == null
                || !(cell > 0f) || graph.featureEdgeCount == 0) return graph;
            var radius = radiusCells * cell;
            if (index == null || index.sourceTriangles != sourceTriangles || !Mathf.Approximately(index.cellSize, radius))
                index = Index.Build(sourcePositions, sourceTriangles, radius);
            var src = index;

            // Each crease edge's half-edge (with a twin) or boundary.
            var P = topology.positions;
            var T = topology.triangles;
            var he = topology.halfEdgeVertex;
            var twin = topology.halfEdgeTwin;
            var creases = new List<int>();
            var boundary = new List<(int a, int b)>();
            for (var h = 0; h < he.Length; h++)
            {
                var t = twin[h];
                if (t >= 0 && t < h) continue;
                int a = he[h], b = topology.HalfEdgeTarget(h);
                if (!graph.IsFeatureEdge(a, b) && (candidates == null || !candidates.IsFeatureEdge(a, b))) continue;
                if (t < 0) boundary.Add((a, b));
                else creases.Add(h);
            }
            stats.creases = creases.Count;

            var keep = new bool[creases.Count];
            var unread = 0;
            var cosAngle = Mathf.Cos(angleDegrees * Mathf.Deg2Rad);
            var radiusSqr = radius * radius;
            Parallel.For(0, creases.Count, BackgroundScheduler.Options, i =>
            {
                var h = creases[i];
                int a = he[h], b = topology.HalfEdgeTarget(h);
                var m = 0.5f * (P[a] + P[b]);
                var e = P[b] - P[a];
                var len = e.magnitude;
                if (!(len > 0f)) return;
                var t = e / len;
                Vector3 Across(int face)
                {
                    var c = (P[T[3 * face]] + P[T[3 * face + 1]] + P[T[3 * face + 2]]) / 3f - m;
                    return Unit(c - t * Vector3.Dot(t, c));
                }
                var split = Across(h / 3) - Across(twin[h] / 3);
                if (split.sqrMagnitude < 1e-12f) return;
                Vector3 Normal(int face) => Vector3.Cross(P[T[3 * face + 1]] - P[T[3 * face]], P[T[3 * face + 2]] - P[T[3 * face]]);
                var f1 = Normal(h / 3);
                var f2 = Normal(twin[h] / 3);

                var cm = Vector3Int.FloorToInt(m / radius);
                var found = 0;
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                    if (src.bins.TryGetValue(Index.Key(new Vector3Int(cm.x + dx, cm.y + dy, cm.z + dz)), out var bin))
                        found += bin.count;
                var stride = Mathf.Max(1, found / Mathf.Max(1, 6 * maxSamples));
                Vector3 n1 = Vector3.zero, n2 = Vector3.zero;
                int c1 = 0, c2 = 0;
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!src.bins.TryGetValue(Index.Key(new Vector3Int(cm.x + dx, cm.y + dy, cm.z + dz)), out var bin)) continue;
                    for (var k = bin.start; k < bin.start + bin.count; k += stride)
                    {
                        var f = src.order[k];
                        var d = src.centre[f] - m;
                        if (d.sqrMagnitude > radiusSqr) continue;
                        // Each turned to face the way the mesh's own face on its side does: a wall
                        // thinner than the radius would otherwise cancel its front with its back, and
                        // a source wound both ways cancel itself.
                        var an = src.areaNormal[f];
                        if (Vector3.Dot(d, split) > 0f) { n1 += Vector3.Dot(an, f1) >= 0f ? an : -an; c1++; }
                        else { n2 += Vector3.Dot(an, f2) >= 0f ? an : -an; c2++; }
                    }
                }
                if (c1 < 2 || c2 < 2 || n1.sqrMagnitude <= 0f || n2.sqrMagnitude <= 0f)
                {
                    System.Threading.Interlocked.Increment(ref unread);
                    return;
                }
                n1 = Unit(n1);
                n2 = Unit(n2);
                if (Vector3.Dot(n1, n2) >= cosAngle) return;
                var line = Unit(Vector3.Cross(n1, n2));
                keep[i] = Mathf.Abs(Vector3.Dot(line, t)) >= minAlign;
            });
            stats.unread = unread;

            var kept = new List<(int a, int b)>();
            for (var i = 0; i < creases.Count; i++)
                if (keep[i]) kept.Add((he[creases[i]], topology.HalfEdgeTarget(creases[i])));
            var before = kept.Count;
            if (minChainCells > 0f) kept = LongPieces(P, kept, minChainCells * cell);
            stats.short_ = before - kept.Count;
            kept.AddRange(boundary);
            var result = FeatureGraph.Empty(topology.vertexCount).WithEdges(topology, kept);
            if (separationCells > 0f) result = result.KeepSeparatedCurves(topology, separationCells * cell, 0.5f, out _);
            stats.kept = result.featureEdgeCount - boundary.Count;
            stats.ms = watch.ElapsedMilliseconds;
            if (debugPath != null) Dump(topology, result);
            return result;
        }

        // Unity's normalize gives zero under 1e-5, and a model imported at a hundredth of a metre has
        // face areas far below that.
        static Vector3 Unit(Vector3 v)
        {
            var l = v.magnitude;
            return l > 0f ? v / l : Vector3.zero;
        }

        static void Dump(RemeshTopology topology, FeatureGraph graph)
        {
            var path = debugPath.Replace(".obj", $"_{s_DebugDumps++}.obj");
            using var w = new System.IO.StreamWriter(path);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var p in topology.positions) w.WriteLine(string.Format(inv, "v {0} {1} {2}", p.x, p.y, p.z));
            var t = topology.triangles;
            for (var i = 0; i < t.Length; i += 3) w.WriteLine($"f {t[i] + 1} {t[i + 1] + 1} {t[i + 2] + 1}");
            var he = topology.halfEdgeVertex;
            for (var h = 0; h < he.Length; h++)
            {
                if (topology.halfEdgeTwin[h] >= 0 && topology.halfEdgeTwin[h] < h) continue;
                int a = he[h], b = topology.HalfEdgeTarget(h);
                if (graph.IsFeatureEdge(a, b)) w.WriteLine($"l {a + 1} {b + 1}");
            }
        }

        // The edges in pieces (joined through shared vertices) at least `minLength` long in all.
        static List<(int a, int b)> LongPieces(Vector3[] positions, List<(int a, int b)> edges, float minLength)
        {
            var parent = new Dictionary<int, int>();
            int Find(int v)
            {
                if (!parent.ContainsKey(v)) { parent[v] = v; return v; }
                var root = v;
                while (parent[root] != root) root = parent[root];
                while (parent[v] != root) { var next = parent[v]; parent[v] = root; v = next; }
                return root;
            }
            foreach (var (a, b) in edges)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }
            var length = new Dictionary<int, float>();
            foreach (var (a, b) in edges)
            {
                var r = Find(a);
                length[r] = (length.TryGetValue(r, out var l) ? l : 0f) + (positions[a] - positions[b]).magnitude;
            }
            var kept = new List<(int a, int b)>(edges.Count);
            foreach (var e in edges)
                if (length[Find(e.a)] >= minLength) kept.Add(e);
            return kept;
        }
    }
}
