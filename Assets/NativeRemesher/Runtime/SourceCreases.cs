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

        /// <summary>
        /// How near the edge, in cells, the line the two sides meet along must pass. Without it every
        /// edge within the radius of a bend read the bend: on car2 77 % of the candidates were kept,
        /// in bands either side of each frame, which the separation then thinned to dashes. 0: anywhere.
        /// </summary>
        /// <remarks>
        /// Off: with the gaps bridged after the thinning (bridgeCells) car2 measured the same without
        /// it (0.93 against 0.98 % of the area turned over), and at 0.2 the white house's window
        /// recesses -- two bends a third of a cell apart, whose planes meet nowhere near either -- lost
        /// edges enough that its torn lattice beat the error-driven quads.
        /// </remarks>
        public static float lineCells = 0f;

        /// <summary>Kept pieces shorter than this, in cells, are dropped.</summary>
        public static float minChainCells = 1.5f;

        /// <summary>
        /// Closed pieces spanning less than this (their box's diagonal), in cells, are dropped too: a
        /// door handle's rim is a loop round a recess a cell deep, which the source does bend at, and
        /// pinned to it the grid folds into the recess. A badge, a bolt, a hubcap's slots the same.
        /// Car2's handles span 1-2.5 cells at 5,000; the white house's window frames 2.5-3.5, and at
        /// 3.5 they went and its windows tore. Open pieces stay: dropping the house's window bars
        /// left its lattice 3 % fewer faces, the error-driven quads made at that count, and their
        /// windows crumpled.
        /// </summary>
        public static float minExtentCells = 2.5f;

        /// <summary>Most source faces read either side of an edge; past it they are sampled evenly.</summary>
        public static int maxSamples = 600;

        /// <summary>Of two parallel kept creases closer than this, in cells, only the longer stays (a rounding read twice).</summary>
        public static float separationCells = 0.7f;

        /// <summary>
        /// A kept piece's open end is joined to the next kept crease ahead of it, up to this many
        /// cells away, along mesh edges the source bends at too (at bridgeAngleShare of the angle).
        /// A window frame rounded over a few triangles is marked only where an edge happens to run
        /// along it: car2's came out as 888 pieces with gaps of about one solve-mesh edge, and the
        /// grid ran through them. 0: off.
        /// </summary>
        public static float bridgeCells = 2f;

        /// <summary>The share of angleDegrees a bridging edge must bend at.</summary>
        public static float bridgeAngleShare = 0.6f;

        /// <summary>How closely a bridging edge must run with the line the source bends along (|cosine|).</summary>
        public static float bridgeAlign = 0.5f;

        /// <summary>Diagnostics: where each search writes its mesh and kept creases (OBJ, lines), numbered; null: nowhere.</summary>
        public static string debugPath;
        static int s_DebugDumps;

        public sealed class Stats
        {
            public int creases, kept, short_, unread, bridged;
            /// <summary>Read off the solve mesh edge by edge (candidates given), so joinPieces would change them.</summary>
            public bool loose;
            public long ms;
            public override string ToString() =>
                $"source creases: {kept} of {creases} kept ({short_} in short pieces, {unread} not read, {bridged} gaps bridged), {ms} ms";
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
            int[] sourceTriangles, float cell, ref Index index, Stats stats = null, FeatureGraph candidates = null,
            bool joinPieces = true)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats ??= new Stats();
            if (graph == null || topology == null || sourcePositions == null || sourceTriangles == null
                || !(cell > 0f) || graph.featureEdgeCount == 0) return graph;
            var radius = radiusCells * cell;
            // The line, gap and size tests are for edges read one by one off the solve mesh
            // (candidates given): a panel's lines come whole, and on car3 and car4 the tests only
            // moved them (0.56 -> 0.74 and 0.46 -> 0.57 % of the area turned over). And for a grid
            // laid along them (joinPieces): the error-driven quads follow every crease they are
            // given, and without the white house's window bars (under the size) and with its frames
            // bridged its windows came out crumpled.
            stats.loose = candidates != null;
            var loose = candidates != null && joinPieces;
            var lineDistance = loose ? lineCells * cell : 0f;
            var bridge = loose && bridgeCells > 0f;
            var minExtent = loose ? minExtentCells * cell : 0f;
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
                var read = Bends(creases[i], cosAngle, minAlign, lineDistance);
                if (read < 0) System.Threading.Interlocked.Increment(ref unread);
                keep[i] = read > 0;
            });
            stats.unread = unread;

            // 1: the source bends across half-edge h by more than the angle cosAngle gives, along it;
            // 0: not; -1: too little of the source either side to read.
            int Bends(int h, float cosA, float align, float lineDistance)
            {
                int a = he[h], b = topology.HalfEdgeTarget(h);
                var m = 0.5f * (P[a] + P[b]);
                var e = P[b] - P[a];
                var len = e.magnitude;
                if (!(len > 0f) || twin[h] < 0) return 0;
                var t = e / len;
                Vector3 Across(int face)
                {
                    var c = (P[T[3 * face]] + P[T[3 * face + 1]] + P[T[3 * face + 2]]) / 3f - m;
                    return Unit(c - t * Vector3.Dot(t, c));
                }
                var split = Across(h / 3) - Across(twin[h] / 3);
                if (split.sqrMagnitude < 1e-12f) return 0;
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
                Vector3 n1 = Vector3.zero, n2 = Vector3.zero, s1 = Vector3.zero, s2 = Vector3.zero;
                float w1 = 0f, w2 = 0f;
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
                        var w = an.magnitude;
                        if (Vector3.Dot(d, split) > 0f) { n1 += Vector3.Dot(an, f1) >= 0f ? an : -an; s1 += w * d; w1 += w; c1++; }
                        else { n2 += Vector3.Dot(an, f2) >= 0f ? an : -an; s2 += w * d; w2 += w; c2++; }
                    }
                }
                if (c1 < 2 || c2 < 2 || n1.sqrMagnitude <= 0f || n2.sqrMagnitude <= 0f) return -1;
                n1 = Unit(n1);
                n2 = Unit(n2);
                if (Vector3.Dot(n1, n2) >= cosA) return 0;
                var line = Unit(Vector3.Cross(n1, n2));
                if (Mathf.Abs(Vector3.Dot(line, t)) < align) return 0;
                if (!(lineDistance > 0f) || !(w1 > 0f) || !(w2 > 0f)) return 1;
                // The two sides as planes through their middles; the line they meet along, nearest
                // the edge's middle (the origin here): m + a n1 + b n2 on both.
                var c = Vector3.Dot(n1, n2);
                float o1 = Vector3.Dot(n1, s1 / w1), o2 = Vector3.Dot(n2, s2 / w2), det = 1f - c * c;
                if (!(det > 1e-6f)) return 0;
                float pa = (o1 - c * o2) / det, pb = (o2 - c * o1) / det;
                return (pa * n1 + pb * n2).sqrMagnitude <= lineDistance * lineDistance ? 1 : 0;
            }

            var kept = new List<(int a, int b)>();
            for (var i = 0; i < creases.Count; i++)
                if (keep[i]) kept.Add((he[creases[i]], topology.HalfEdgeTarget(creases[i])));
            if (bridge)
            {
                // Thinned first: bridged, the pieces would be thinned back to dashes at the junctions
                // the bridges make.
                if (separationCells > 0f)
                {
                    var thinned = FeatureGraph.Empty(topology.vertexCount).WithEdges(topology, kept)
                        .KeepSeparatedCurves(topology, separationCells * cell, 0.5f, out _);
                    kept.RemoveAll(e => !thinned.IsFeatureEdge(e.a, e.b));
                }
                stats.bridged = Bridge(topology, kept, bridgeCells * cell,
                    h => Bends(h, Mathf.Cos(bridgeAngleShare * angleDegrees * Mathf.Deg2Rad), bridgeAlign,
                        2f * lineDistance) > 0);
            }
            var before = kept.Count;
            if (minChainCells > 0f || minExtent > 0f) kept = LongPieces(P, kept, minChainCells * cell, minExtent);
            stats.short_ = before - kept.Count;
            kept.AddRange(boundary);
            var result = FeatureGraph.Empty(topology.vertexCount).WithEdges(topology, kept);
            if (separationCells > 0f && !bridge)
                result = result.KeepSeparatedCurves(topology, separationCells * cell, 0.5f, out _);
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

        // Joins each open end of `edges` to the first kept crease vertex ahead of it (within 60 degrees
        // of the way the piece runs out, no further than maxGap), along the shortest mesh path,
        // when that path runs on fairly straight (no more than half again the gap) and every edge
        // of it `bends`. The bridges are added to `edges`; returns how many.
        static int Bridge(RemeshTopology topology, List<(int a, int b)> edges, float maxGap, Func<int, bool> bends)
        {
            var P = topology.positions;
            var linked = new Dictionary<int, List<int>>();
            void Link(int a, int b)
            {
                if (!linked.TryGetValue(a, out var la)) linked[a] = la = new List<int>(2);
                if (!linked.TryGetValue(b, out var lb)) linked[b] = lb = new List<int>(2);
                la.Add(b);
                lb.Add(a);
            }
            foreach (var (a, b) in edges) Link(a, b);
            var ends = new List<int>();
            foreach (var kv in linked)
                if (kv.Value.Count == 1) ends.Add(kv.Key);
            ends.Sort();

            var bridged = 0;
            var cost = new Dictionary<int, float>();
            var from = new Dictionary<int, int>();
            var queue = new SortedSet<(float cost, int v)>();
            var path = new List<int>();
            foreach (var v in ends)
            {
                if (linked[v].Count != 1) continue; // joined from the other side already
                // The way the piece runs out, read over half a gap back along it.
                int previous = -1, current = v;
                var walked = 0f;
                while (walked < 0.5f * maxGap)
                {
                    var next = -1;
                    foreach (var w in linked[current])
                        if (w != previous) { if (next >= 0) { next = -2; break; } next = w; }
                    if (next < 0) break;
                    walked += (P[next] - P[current]).magnitude;
                    previous = current;
                    current = next;
                }
                if (current == v) continue;
                var ahead = Unit(P[v] - P[current]);

                cost.Clear();
                from.Clear();
                queue.Clear();
                cost[v] = 0f;
                queue.Add((0f, v));
                var target = -1;
                while (queue.Count > 0)
                {
                    var (c, u) = queue.Min;
                    queue.Remove(queue.Min);
                    if (c > cost[u]) continue;
                    if (u != v && linked.ContainsKey(u))
                    {
                        // A crease beside this one (a frame's other edge) is passed by, not joined.
                        var to = P[u] - P[v];
                        if (Vector3.Dot(Unit(to), ahead) >= 0.8f && c <= 1.5f * to.magnitude) { target = u; break; }
                        continue;
                    }
                    for (var k = topology.adjacencyStart[u]; k < topology.adjacencyStart[u + 1]; k++)
                    {
                        var w = topology.adjacency[k];
                        var d = P[w] - P[v];
                        var dl = d.magnitude;
                        if (dl > maxGap || Vector3.Dot(d, ahead) < 0.5f * dl) continue;
                        var cw = c + (P[w] - P[u]).magnitude;
                        if (cost.TryGetValue(w, out var old) && old <= cw) continue;
                        if (cost.ContainsKey(w)) queue.Remove((old, w));
                        cost[w] = cw;
                        from[w] = u;
                        queue.Add((cw, w));
                    }
                }
                if (target < 0) continue;
                path.Clear();
                for (var u = target; u != v; u = from[u]) path.Add(u);
                path.Add(v);
                var ok = true;
                for (var i = 0; i + 1 < path.Count && ok; i++)
                {
                    var h = HalfEdge(topology, path[i], path[i + 1]);
                    ok = h >= 0 && bends(h);
                }
                if (!ok) continue;
                for (var i = 0; i + 1 < path.Count; i++)
                {
                    edges.Add((path[i], path[i + 1]));
                    Link(path[i], path[i + 1]);
                }
                bridged++;
            }
            return bridged;
        }

        // A half-edge between a and b (either way round), or -1.
        static int HalfEdge(RemeshTopology topology, int a, int b)
        {
            foreach (var (x, y) in new[] { (a, b), (b, a) })
            {
                var start = topology.vertexHalfEdge[x];
                if (start < 0) continue;
                var h = start;
                for (var guard = 0; guard < 64; guard++)
                {
                    if (topology.HalfEdgeTarget(h) == y) return h;
                    var next = topology.halfEdgeTwin[RemeshTopology.Prev(h)];
                    if (next < 0 || next == start) break;
                    h = next;
                }
            }
            return -1;
        }

        // The edges in pieces (joined through shared vertices) at least `minLength` long in all and,
        // closed ones, spanning at least `minExtent` (their box's diagonal).
        static List<(int a, int b)> LongPieces(Vector3[] positions, List<(int a, int b)> edges, float minLength,
            float minExtent)
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
            var box = new Dictionary<int, Bounds>();
            foreach (var (a, b) in edges)
            {
                var r = Find(a);
                if (!box.TryGetValue(r, out var bb)) bb = new Bounds(positions[a], Vector3.zero);
                bb.Encapsulate(positions[a]);
                bb.Encapsulate(positions[b]);
                box[r] = bb;
            }
            // Open: a piece with an end (a point on one of its edges only).
            var degree = new Dictionary<int, int>();
            foreach (var (a, b) in edges)
            {
                degree[a] = degree.TryGetValue(a, out var da) ? da + 1 : 1;
                degree[b] = degree.TryGetValue(b, out var db) ? db + 1 : 1;
            }
            var open = new HashSet<int>();
            foreach (var kv in degree)
                if (kv.Value == 1) open.Add(Find(kv.Key));
            var minExtentSqr = minExtent * minExtent;
            var kept = new List<(int a, int b)>(edges.Count);
            foreach (var e in edges)
            {
                var r = Find(e.a);
                if (length[r] >= minLength && (open.Contains(r) || box[r].size.sqrMagnitude >= minExtentSqr)) kept.Add(e);
            }
            return kept;
        }
    }
}
