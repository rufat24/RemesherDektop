// Sharp feature detection, and the polylines the rest of the pipeline aligns and snaps to.
//
// This is the classical half of what a learned feature classifier would eventually replace, and it
// is deliberately built first. A consistent-angle chamfer is the EASY case for a dihedral threshold,
// so most of the benefit is available without any model at all — and whatever a model produced would
// write into exactly these arrays, so this is the integration point as well as the baseline.
//
// Features are stored as vertex indices, not as geometry in R^3. Everything downstream is
// vertex-indexed, and a geometric curve would force a nearest-point query at every use and would not
// survive the welding remap.

using System;
using System.Collections.Generic;
using UnityEngine;
using NativeRemesher.Extraction;

namespace NativeRemesher
{
    public enum FeatureKind : byte
    {
        None = 0,
        /// <summary>Interior edge whose two faces meet above the dihedral threshold.</summary>
        Crease = 1,
        /// <summary>Mesh boundary. Always a feature — there is no surface on the other side.</summary>
        Boundary = 2
    }

    public sealed class FeatureGraph
    {
        public bool[] isFeatureVertex;

        /// <summary>
        /// Unit tangent along the feature at each vertex, or zero where there is no single
        /// direction to align to.
        /// </summary>
        /// <remarks>
        /// Zero at corners on purpose. Where three chamfers meet at 120 degrees no cross field can
        /// align to all of them, and the correct classical behaviour is a singularity there, not a
        /// constraint the solver will smear across the neighbourhood. Leaving the tangent zero lets
        /// such a vertex stay unconstrained while still being protected from collapse.
        /// </remarks>
        public Vector3[] vertexTangent;

        /// <summary>0 free, 1 polyline endpoint, 2 interior of a polyline, 3+ junction.</summary>
        public byte[] featureDegree;

        /// <summary>CSR over polylines: vertices of curve c are the slice [start[c], start[c+1]).</summary>
        public int[] polylineStart;
        public int[] polylineVertices;
        public bool[] polylineClosed;

        readonly HashSet<long> m_FeatureEdges = new HashSet<long>(EdgeKey.Comparer);

        public int featureEdgeCount => m_FeatureEdges.Count;
        public int polylineCount => polylineStart == null ? 0 : polylineStart.Length - 1;

        public int featureVertexCount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < isFeatureVertex.Length; i++) if (isFeatureVertex[i]) count++;
                return count;
            }
        }

        public int cornerCount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < featureDegree.Length; i++) if (featureDegree[i] >= 3) count++;
                return count;
            }
        }

        public bool IsFeatureEdge(int a, int b) => m_FeatureEdges.Contains(Key(a, b));

        static long Key(int a, int b)
        {
            var lo = Mathf.Min(a, b);
            var hi = Mathf.Max(a, b);
            return ((long)lo << 32) | (uint)hi;
        }

        public static FeatureGraph Empty(int vertexCount)
        {
            return new FeatureGraph
            {
                isFeatureVertex = new bool[vertexCount],
                vertexTangent = new Vector3[vertexCount],
                featureDegree = new byte[vertexCount],
                polylineStart = new[] { 0 },
                polylineVertices = Array.Empty<int>(),
                polylineClosed = Array.Empty<bool>()
            };
        }

        /// <summary>
        /// This graph carried onto a mesh whose edges have been split at their midpoints.
        /// </summary>
        /// <param name="vertexCount">Vertex count of the refined mesh.</param>
        /// <param name="remap">Pre-refinement index to refined index. The first
        /// <c>isFeatureVertex.Length</c> entries are this graph's own vertices, in order.</param>
        /// <param name="splits">Parent endpoints and child, all in pre-remap indices.</param>
        /// <remarks>
        /// Needed because <see cref="Extraction.EdgeSubdivide"/> adds vertices after this graph was
        /// detected, and every array here is indexed by vertex — so without carrying it forward,
        /// extraction indexes past the end of <see cref="isFeatureVertex"/>. That is not a
        /// hypothetical: it threw on three of four audit cases the first time subdivision was
        /// switched on.
        ///
        /// A midpoint of a feature edge is itself on the feature, interior to the polyline, with the
        /// edge's own direction as its tangent — splitting a crease does not move it.
        ///
        /// <b>The polylines are deliberately NOT carried forward, and are left empty.</b> Inserting
        /// a vertex into a CSR polyline is real surgery, and nothing that runs after subdivision
        /// reads them: extraction touches only <see cref="isFeatureVertex"/>,
        /// <see cref="featureDegree"/> and <see cref="IsFeatureEdge"/>. The consumers that do read
        /// polylines are the two field solvers, and they have already finished by this point.
        /// Anything added later that reads a polyline after subdivision must extend this method
        /// rather than assume it works — hence <see cref="polylinesCarried"/>, so the assumption
        /// fails loudly instead of reading an empty curve set as "no features".
        /// </remarks>
        public FeatureGraph WithSplitEdges(int vertexCount, int[] remap,
            IReadOnlyList<(int a, int b, int mid)> splits)
        {
            if (remap == null) throw new ArgumentNullException(nameof(remap));
            if (splits == null) throw new ArgumentNullException(nameof(splits));

            var refined = new FeatureGraph
            {
                isFeatureVertex = new bool[vertexCount],
                vertexTangent = new Vector3[vertexCount],
                featureDegree = new byte[vertexCount],
                polylineStart = new[] { 0 },
                polylineVertices = Array.Empty<int>(),
                polylineClosed = Array.Empty<bool>(),
                polylinesCarried = false,
            };

            int Target(int source) =>
                source >= 0 && source < remap.Length ? remap[source] : -1;

            for (var v = 0; v < isFeatureVertex.Length; v++)
            {
                var t = Target(v);
                if (t < 0 || t >= vertexCount) continue;
                refined.isFeatureVertex[t] = isFeatureVertex[v];
                refined.vertexTangent[t] = vertexTangent[v];
                refined.featureDegree[t] = featureDegree[v];
            }

            // Which parent edges were split, so an unsplit feature edge can be told from one that
            // has to be replaced by its two halves.
            var wasSplit = new HashSet<long>(EdgeKey.Comparer);
            foreach (var (a, b, _) in splits) wasSplit.Add(Key(a, b));

            foreach (var edge in m_FeatureEdges)
            {
                if (wasSplit.Contains(edge)) continue;
                var a = EdgeKey.First(edge);
                var b = EdgeKey.Second(edge);
                int ta = Target(a), tb = Target(b);
                if (ta < 0 || tb < 0) continue;
                refined.m_FeatureEdges.Add(Key(ta, tb));
            }

            foreach (var (a, b, mid) in splits)
            {
                if (!IsFeatureEdge(a, b)) continue;

                int ta = Target(a), tb = Target(b), tm = Target(mid);
                if (ta < 0 || tb < 0 || tm < 0 || tm >= vertexCount) continue;

                refined.m_FeatureEdges.Add(Key(ta, tm));
                refined.m_FeatureEdges.Add(Key(tm, tb));

                refined.isFeatureVertex[tm] = true;
                refined.featureDegree[tm] = 2;

                // The tangent of the parent edge. A split does not bend the crease, so both halves
                // carry the same direction, and the midpoint is interior to the polyline by
                // construction — a split can never create an endpoint or a junction.
                var tangent = vertexTangent[a];
                if (tangent.sqrMagnitude <= 0f) tangent = vertexTangent[b];
                refined.vertexTangent[tm] = tangent;
            }

            return refined;
        }

        /// <summary>
        /// This graph with <paramref name="edges"/> added as creases, on the same topology.
        /// </summary>
        /// <param name="tangents">
        /// Optional per-vertex directions for the added curves, used in place of the one-ring chord
        /// wherever a vertex ends up interior to a single curve. See <see cref="CurveNetwork.Trace"/>.
        /// </param>
        /// <param name="corners">Optional vertices that get no direction at all, whatever their degree.</param>
        public FeatureGraph WithEdges(RemeshTopology topology, IEnumerable<(int a, int b)> edges,
            Vector3[] tangents = null, bool[] corners = null)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (edges == null) throw new ArgumentNullException(nameof(edges));

            var graph = Empty(topology.vertexCount);
            var neighbours = new List<int>[topology.vertexCount];

            void Add(int a, int b)
            {
                if (a == b || !graph.m_FeatureEdges.Add(Key(a, b))) return;
                graph.isFeatureVertex[a] = true;
                graph.isFeatureVertex[b] = true;
                (neighbours[a] ??= new List<int>(4)).Add(b);
                (neighbours[b] ??= new List<int>(4)).Add(a);
            }

            foreach (var edge in m_FeatureEdges) Add(EdgeKey.First(edge), EdgeKey.Second(edge));
            foreach (var (a, b) in edges) Add(a, b);

            for (var v = 0; v < topology.vertexCount; v++)
                graph.featureDegree[v] = (byte)Mathf.Min(255, neighbours[v]?.Count ?? 0);

            graph.BuildTangents(topology, neighbours);
            if (tangents != null && tangents.Length == topology.vertexCount)
                for (var v = 0; v < topology.vertexCount; v++)
                    if (graph.featureDegree[v] == 2 && tangents[v].sqrMagnitude > 0f)
                        graph.vertexTangent[v] = tangents[v];
            if (corners != null && corners.Length == topology.vertexCount)
                for (var v = 0; v < topology.vertexCount; v++)
                    if (corners[v] && !isFeatureVertex[v]) graph.vertexTangent[v] = Vector3.zero;
            graph.BuildPolylines(neighbours);
            return graph;
        }

        /// <summary>
        /// The boundary edges plus only those crease polylines that run at least
        /// <paramref name="minExtent"/> end to end, and do so fairly straight
        /// (<paramref name="minStraightness"/> = end-to-end over walked length).
        /// </summary>
        /// <remarks>
        /// A dihedral test on a scan or a detailed sculpt fires on noise and fine sculpting as
        /// readily as on a real crease, which is why the scan profiles switch it off altogether --
        /// and with it every crease worth having, such as the grooves between pressed-together
        /// fingers, which no lattice density can follow unless something tells it they are there.
        /// Noise creases come out as short fragments between junctions; a groove, a seam or a
        /// panel line comes out as a long chain. Polylines break at junctions, so this measures
        /// each stretch on its own. Boundaries are kept regardless.
        /// </remarks>
        public FeatureGraph KeepCoherentCurves(RemeshTopology topology, float minExtent,
            float minStraightness, out int keptCurves, out int droppedCurves)
            => KeepCoherentCurves(topology, minExtent, minStraightness, 0f, out keptCurves, out droppedCurves);

        /// <summary>
        /// As above, and then also the strokes at least <paramref name="attachedMinExtent"/> long
        /// (and as straight) that touch a kept one, a few rounds, so a structure grows off it.
        /// </summary>
        /// <remarks>
        /// A long minimum is what keeps a scan's clutter -- cars, units, the junkyard -- from
        /// laying lines, and it also drops a box built against a wall: on ccnb a 3 x 5 m enclosure
        /// at a wall's end lost its edges, the corners its geometry needs were left to float, and
        /// 13 irregular vertices came out round it. Its edges touch the wall's foot, which is kept;
        /// a car's touch nothing. 0: off.
        /// </remarks>
        public FeatureGraph KeepCoherentCurves(RemeshTopology topology, float minExtent,
            float minStraightness, float attachedMinExtent, out int keptCurves, out int droppedCurves)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            keptCurves = droppedCurves = 0;

            var boundary = new HashSet<long>(EdgeKey.Comparer);
            var halfEdges = topology.halfEdgeVertex?.Length ?? 0;
            for (var h = 0; h < halfEdges; h++)
                if (topology.halfEdgeTwin[h] < 0)
                    boundary.Add(Key(topology.halfEdgeVertex[h], topology.HalfEdgeTarget(h)));

            var kept = new List<(int a, int b)>();
            var positions = topology.positions;
            float Walked(int c)
            {
                var walked = 0f;
                for (var i = polylineStart[c] + 1; i < polylineStart[c + 1]; i++)
                    walked += (positions[polylineVertices[i]] - positions[polylineVertices[i - 1]]).magnitude;
                return walked;
            }
            bool IsCrease(int c) => polylineStart[c + 1] - polylineStart[c] >= 2
                && !boundary.Contains(Key(polylineVertices[polylineStart[c]], polylineVertices[polylineStart[c] + 1]));

            // Strokes: polylines joined where one runs on straight through a junction into another.
            var stroke = new int[polylineCount];
            for (var c = 0; c < polylineCount; c++) stroke[c] = c;
            int Find(int c) { while (stroke[c] != c) c = stroke[c] = stroke[stroke[c]]; return c; }
            // Per polyline end (2c = start, 2c + 1 = end), the end it is joined to, or -1.
            var joined = new int[2 * polylineCount];
            for (var i = 0; i < joined.Length; i++) joined[i] = -1;
            if (joinThroughJunctions) JoinThroughJunctions(positions, minExtent / 3f, IsCrease, joined, Find, stroke);
            // Across short gaps too: where the dihedral dips under the threshold for an edge or two.
            var bridges = bridgeGapFraction > 0f
                ? BridgeGaps(topology, minExtent / 3f, bridgeGapFraction * minExtent, IsCrease, joined, Find, stroke)
                : null;

            // Each stroke measured as a whole: its length, and the distance between its two free ends.
            var strokeWalked = new Dictionary<int, float>();
            var strokeEnds = new Dictionary<int, List<int>>();
            for (var c = 0; c < polylineCount; c++)
            {
                if (!IsCrease(c)) continue;
                var s = Find(c);
                strokeWalked[s] = (strokeWalked.TryGetValue(s, out var w) ? w : 0f) + Walked(c);
                // A bridged gap counts once, from the end with the lower id.
                for (var side = 0; side < 2; side++)
                {
                    var e = 2 * c + side;
                    if (bridges != null && bridges.TryGetValue(e, out var path) && e < joined[e])
                        for (var i = 1; i < path.Count; i++)
                            strokeWalked[s] += (positions[path[i]] - positions[path[i - 1]]).magnitude;
                }
                if (!strokeEnds.TryGetValue(s, out var ends)) strokeEnds[s] = ends = new List<int>(2);
                if (!polylineClosed[c])
                {
                    if (joined[2 * c] < 0) ends.Add(polylineVertices[polylineStart[c]]);
                    if (joined[2 * c + 1] < 0) ends.Add(polylineVertices[polylineStart[c + 1] - 1]);
                }
            }
            // Per stroke: whether it is kept, and how long and straight it is.
            var strokeKept = new Dictionary<int, bool>();
            var strokeSpan = new Dictionary<int, float>();
            foreach (var kv in strokeWalked)
            {
                var ends = strokeEnds[kv.Key];
                // A closed loop has no span; its size is what it encloses, so take half its length.
                var span = ends.Count == 2
                    ? (positions[ends[1]] - positions[ends[0]]).magnitude
                    : 0.5f * kv.Value;
                strokeSpan[kv.Key] = span;
                strokeKept[kv.Key] = span >= minExtent && kv.Value > 0f && span / kv.Value >= minStraightness;
            }
            if (attachedMinExtent > 0f && attachedMinExtent < minExtent)
            {
                // Strokes touching a kept one, grown out a few rounds.
                var keptVertex = new HashSet<int>();
                var strokesOf = new Dictionary<int, List<int>>();
                for (var c = 0; c < polylineCount; c++)
                {
                    if (!IsCrease(c)) continue;
                    var st = Find(c);
                    if (!strokesOf.TryGetValue(st, out var list)) strokesOf[st] = list = new List<int>(2);
                    list.Add(c);
                    if (strokeKept[st])
                        for (var i = polylineStart[c]; i < polylineStart[c + 1]; i++) keptVertex.Add(polylineVertices[i]);
                }
                for (var round = 0; round < attachRounds; round++)
                {
                    var added = new List<int>();
                    foreach (var kv in strokesOf)
                    {
                        var st = kv.Key;
                        if (strokeKept[st]) continue;
                        var walked = strokeWalked[st];
                        var span = strokeSpan[st];
                        if (span < attachedMinExtent || walked <= 0f || span / walked < minStraightness) continue;
                        var touches = false;
                        foreach (var c in kv.Value)
                            for (var i = polylineStart[c]; i < polylineStart[c + 1] && !touches; i++)
                                touches = keptVertex.Contains(polylineVertices[i]);
                        if (touches) added.Add(st);
                    }
                    if (added.Count == 0) break;
                    foreach (var st in added)
                    {
                        strokeKept[st] = true;
                        foreach (var c in strokesOf[st])
                            for (var i = polylineStart[c]; i < polylineStart[c + 1]; i++) keptVertex.Add(polylineVertices[i]);
                    }
                }
            }
            for (var c = 0; c < polylineCount; c++)
            {
                if (!IsCrease(c)) continue;
                var from = polylineStart[c];
                var to = polylineStart[c + 1];
                if (!strokeKept[Find(c)])
                {
                    droppedCurves++;
                    continue;
                }

                keptCurves++;
                for (var i = from + 1; i < to; i++)
                    kept.Add((polylineVertices[i - 1], polylineVertices[i]));
                for (var side = 0; side < 2; side++)
                {
                    var e = 2 * c + side;
                    if (bridges != null && bridges.TryGetValue(e, out var path) && e < joined[e])
                        for (var i = 1; i < path.Count; i++) kept.Add((path[i - 1], path[i]));
                }
            }

            var result = Empty(topology.vertexCount);
            var boundaryOnly = new List<(int a, int b)>();
            foreach (var key in boundary) boundaryOnly.Add((EdgeKey.First(key), EdgeKey.Second(key)));
            boundaryOnly.AddRange(kept);
            return result.WithEdges(topology, boundaryOnly);
        }

        /// <summary>
        /// Measure a crease through the junctions it runs straight through, not just between them.
        /// </summary>
        /// <remarks>
        /// Polylines break at every junction, and on a scan a long clean line is crossed by noise
        /// every few metres: a downpipe, an aerial, a ragged patch of wall. Measured on ccnb, a
        /// 33 m roof edge came out as a dozen pieces of 1-10 m with short spurs hanging off each
        /// junction, and under a 3-cell minimum (6 m there) two thirds of it was dropped as noise,
        /// while the foot of the same wall, crossed less often, survived. At a junction the two
        /// pieces that continue each other most nearly straight are one stroke; the spurs stay
        /// strokes of their own and are judged, and dropped, on their own length. Off: each piece
        /// between junctions is judged alone, as before.
        /// </remarks>
        public static bool joinThroughJunctions = true;

        /// <summary>Rounds of growing kept creases into the strokes attached to them.</summary>
        public static int attachRounds = 3;

        /// <summary>The most two pieces may turn at a junction and still be one stroke, degrees.</summary>
        public static float joinMaxTurnDegrees = 30f;

        /// <summary>
        /// Bridge a gap in a crease up to this fraction of the minimum length, where two ends point
        /// at each other: the gap's mesh edges join the crease, and both pieces are measured as one.
        /// </summary>
        /// <remarks>
        /// A scan's crease drops under the dihedral threshold for an edge or two wherever something
        /// rounds it off -- a bin against the foot of a wall, a patch of noise. Measured on ccnb, a
        /// 15 m stretch of wall kept 19 m of its roof edge and 4 m of its foot, so the grid ran
        /// along the ground a cell out from the wall and cut its lower half off diagonally. Every
        /// gap left open is also a crease end, which the layout turns into a singularity. 0: off.
        /// </remarks>
        public static float bridgeGapFraction = 0.15f;

        // Pairs of free crease ends across a gap; per end, the mesh path to the other end.
        Dictionary<int, List<int>> BridgeGaps(RemeshTopology topology, float reach, float maxGap,
            Func<int, bool> isCrease, int[] joined, Func<int, int> find, int[] stroke)
        {
            var positions = topology.positions;
            var ends = new List<(int end, int v, Vector3 p, Vector3 dir)>();
            for (var c = 0; c < polylineCount; c++)
            {
                if (!isCrease(c) || polylineClosed[c]) continue;
                var from = polylineStart[c];
                var to = polylineStart[c + 1];
                for (var side = 0; side < 2; side++)
                {
                    if (joined[2 * c + side] >= 0) continue;
                    var tip = side == 0 ? from : to - 1;
                    var step = side == 0 ? 1 : -1;
                    var p = positions[polylineVertices[tip]];
                    var q = p;
                    for (var i = tip + step; i >= from && i < to; i += step)
                    {
                        q = positions[polylineVertices[i]];
                        if ((q - p).sqrMagnitude >= reach * reach) break;
                    }
                    var dir = p - q;
                    if (dir.sqrMagnitude <= 0f) continue;
                    ends.Add((2 * c + side, polylineVertices[tip], p, dir.normalized));
                }
            }
            if (ends.Count < 2 || maxGap <= 0f) return null;

            // Candidate pairs from a grid of the ends, cell = the largest gap.
            var cellOf = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Cell(Vector3 p) => (Mathf.FloorToInt(p.x / maxGap), Mathf.FloorToInt(p.y / maxGap), Mathf.FloorToInt(p.z / maxGap));
            for (var i = 0; i < ends.Count; i++)
            {
                var key = Cell(ends[i].p);
                if (!cellOf.TryGetValue(key, out var list)) cellOf[key] = list = new List<int>(2);
                list.Add(i);
            }
            var aim = Mathf.Cos(joinMaxTurnDegrees * Mathf.Deg2Rad);
            var pairs = new List<(float gap, int i, int j)>();
            for (var i = 0; i < ends.Count; i++)
            {
                var (cx, cy, cz) = Cell(ends[i].p);
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!cellOf.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                    foreach (var j in list)
                    {
                        if (j <= i || ends[i].end / 2 == ends[j].end / 2) continue;
                        var g = ends[j].p - ends[i].p;
                        var gap = g.magnitude;
                        if (gap > maxGap || gap <= 0f) continue;
                        var u = g / gap;
                        // Each end pointing at the other.
                        if (Vector3.Dot(ends[i].dir, u) < aim || Vector3.Dot(ends[j].dir, -u) < aim) continue;
                        pairs.Add((gap, i, j));
                    }
                }
            }
            pairs.Sort((x, y) => x.gap.CompareTo(y.gap));

            var bridges = new Dictionary<int, List<int>>();
            foreach (var (gap, i, j) in pairs)
            {
                int ei = ends[i].end, ej = ends[j].end;
                if (joined[ei] >= 0 || joined[ej] >= 0) continue;
                int ri = find(ei / 2), rj = find(ej / 2);
                if (ri == rj) continue;
                var path = MeshPath(topology, ends[i].v, ends[j].v, 2f * gap);
                if (path == null) continue;
                joined[ei] = ej;
                joined[ej] = ei;
                stroke[ri] = rj;
                bridges[ei] = path;
                var back = new List<int>(path);
                back.Reverse();
                bridges[ej] = back;
            }
            return bridges;
        }

        // The shortest path along mesh edges from a to b, no longer than maxLength; null if none.
        static List<int> MeshPath(RemeshTopology topology, int a, int b, float maxLength)
        {
            var positions = topology.positions;
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;
            if (start == null || adjacency == null) return null;
            var dist = new Dictionary<int, float> { [a] = 0f };
            var prev = new Dictionary<int, int>();
            var open = new SortedSet<(float d, int v)> { (0f, a) };
            while (open.Count > 0)
            {
                var (d, v) = open.Min;
                open.Remove(open.Min);
                if (v == b) break;
                for (var k = start[v]; k < start[v + 1]; k++)
                {
                    var w = adjacency[k];
                    if (w == v) continue;
                    var nd = d + (positions[w] - positions[v]).magnitude;
                    if (nd > maxLength) continue;
                    if (dist.TryGetValue(w, out var old))
                    {
                        if (old <= nd) continue;
                        open.Remove((old, w));
                    }
                    dist[w] = nd;
                    prev[w] = v;
                    open.Add((nd, w));
                }
            }
            if (!dist.ContainsKey(b)) return null;
            var path = new List<int>();
            for (var v = b; ; v = prev[v])
            {
                path.Add(v);
                if (v == a) break;
            }
            path.Reverse();
            return path;
        }

        void JoinThroughJunctions(Vector3[] positions, float reach, Func<int, bool> isCrease,
            int[] joined, Func<int, int> find, int[] stroke)
        {
            // Each open crease polyline's two ends, by the vertex they sit on, with the direction
            // pointing out of the polyline there, as a chord over about `reach` of it.
            var endsAt = new Dictionary<int, List<(int end, Vector3 dir)>>();
            for (var c = 0; c < polylineCount; c++)
            {
                if (!isCrease(c) || polylineClosed[c]) continue;
                var from = polylineStart[c];
                var to = polylineStart[c + 1];
                for (var side = 0; side < 2; side++)
                {
                    var tip = side == 0 ? from : to - 1;
                    var step = side == 0 ? 1 : -1;
                    var p = positions[polylineVertices[tip]];
                    var q = p;
                    for (var i = tip + step; i >= from && i < to; i += step)
                    {
                        q = positions[polylineVertices[i]];
                        if ((q - p).sqrMagnitude >= reach * reach) break;
                    }
                    var dir = p - q;
                    if (dir.sqrMagnitude <= 0f) continue;
                    var v = polylineVertices[tip];
                    if (!endsAt.TryGetValue(v, out var list)) endsAt[v] = list = new List<(int, Vector3)>(3);
                    list.Add((2 * c + side, dir.normalized));
                }
            }

            var straight = -Mathf.Cos(joinMaxTurnDegrees * Mathf.Deg2Rad);
            var pairs = new List<(float dot, int a, int b)>();
            foreach (var list in endsAt.Values)
            {
                if (list.Count < 2) continue;
                // The straightest pairs first, each end used once.
                pairs.Clear();
                for (var i = 0; i < list.Count; i++)
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        if (list[i].end / 2 == list[j].end / 2) continue;
                        var dot = Vector3.Dot(list[i].dir, list[j].dir);
                        if (dot <= straight) pairs.Add((dot, list[i].end, list[j].end));
                    }
                pairs.Sort((x, y) => x.dot.CompareTo(y.dot));
                foreach (var (_, a, b) in pairs)
                {
                    if (joined[a] >= 0 || joined[b] >= 0) continue;
                    // Not into a ring: a stroke closing on itself through a junction is a loop
                    // already measured by its own length.
                    int ra = find(a / 2), rb = find(b / 2);
                    if (ra == rb) continue;
                    joined[a] = b;
                    joined[b] = a;
                    stroke[ra] = rb;
                }
            }
        }

        /// <summary>
        /// Drops a crease that runs alongside a longer one closer than
        /// <paramref name="minSeparation"/>. Boundaries are kept and count as the longest.
        /// </summary>
        /// <remarks>
        /// Cutting the lattice along a crease makes it an iso-line, and two parallel iso-lines
        /// closer than a cell cannot both hold: the cells between them are squashed to nothing and
        /// extraction glues the strip into needles. Hard-surface models are full of such pairs --
        /// both edges of a rail, a strap, a cable, a bevel's two shoulders. Measured on the helmet
        /// head with every coherent crease cut, 70 % of the quads with a corner under 50 degrees
        /// had one edge more than twice another. The longer of a close pair is the one the
        /// silhouette depends on; the shorter still steers the cross field through the
        /// orientation constraints, which take the unthinned graph.
        /// A crease is dropped when more than <paramref name="maxSharedFraction"/> of its vertices
        /// sit within the separation of an already accepted crease running within 30 degrees of
        /// parallel; a crossing shares only the few vertices at the junction and stays.
        /// </remarks>
        public FeatureGraph KeepSeparatedCurves(RemeshTopology topology, float minSeparation,
            float maxSharedFraction, out int droppedCurves)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            droppedCurves = 0;
            var positions = topology.positions;

            var boundary = new HashSet<long>(EdgeKey.Comparer);
            var halfEdges = topology.halfEdgeVertex?.Length ?? 0;
            for (var h = 0; h < halfEdges; h++)
                if (topology.halfEdgeTwin[h] < 0)
                    boundary.Add(Key(topology.halfEdgeVertex[h], topology.HalfEdgeTarget(h)));

            var order = new List<(int curve, float length, bool isBoundary)>();
            for (var c = 0; c < polylineCount; c++)
            {
                var from = polylineStart[c];
                var to = polylineStart[c + 1];
                if (to - from < 2) continue;
                var walked = 0f;
                for (var i = from + 1; i < to; i++)
                    walked += (positions[polylineVertices[i]] - positions[polylineVertices[i - 1]]).magnitude;
                order.Add((c, walked,
                    boundary.Contains(Key(polylineVertices[from], polylineVertices[from + 1]))));
            }
            order.Sort((x, y) => x.isBoundary != y.isBoundary
                ? (x.isBoundary ? -1 : 1)
                : y.length.CompareTo(x.length));

            var cell = Mathf.Max(minSeparation, 1e-12f);
            var grid = new Dictionary<(int, int, int), List<(Vector3 p, Vector3 t)>>();
            (int, int, int) Cell(Vector3 p) =>
                (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
            Vector3 Tangent(int from, int to, int i)
            {
                var a = polylineVertices[Mathf.Max(from, i - 1)];
                var b = polylineVertices[Mathf.Min(to - 1, i + 1)];
                return (positions[b] - positions[a]).normalized;
            }

            var kept = new List<(int a, int b)>();
            var minParallel = Mathf.Cos(30f * Mathf.Deg2Rad);
            var separation2 = minSeparation * minSeparation;
            foreach (var (c, _, isBoundary) in order)
            {
                var from = polylineStart[c];
                var to = polylineStart[c + 1];

                if (!isBoundary)
                {
                    var shared = 0;
                    for (var i = from; i < to; i++)
                    {
                        var p = positions[polylineVertices[i]];
                        var t = Tangent(from, to, i);
                        var (x, y, z) = Cell(p);
                        var near = false;
                        for (var dx = -1; dx <= 1 && !near; dx++)
                        for (var dy = -1; dy <= 1 && !near; dy++)
                        for (var dz = -1; dz <= 1 && !near; dz++)
                        {
                            if (!grid.TryGetValue((x + dx, y + dy, z + dz), out var bucket)) continue;
                            foreach (var (q, u) in bucket)
                            {
                                if ((q - p).sqrMagnitude >= separation2) continue;
                                if (Mathf.Abs(Vector3.Dot(t, u)) < minParallel) continue;
                                near = true;
                                break;
                            }
                        }
                        if (near) shared++;
                    }
                    if (shared > maxSharedFraction * (to - from))
                    {
                        droppedCurves++;
                        continue;
                    }
                }

                for (var i = from; i < to; i++)
                {
                    var p = positions[polylineVertices[i]];
                    var key = Cell(p);
                    if (!grid.TryGetValue(key, out var bucket))
                        grid[key] = bucket = new List<(Vector3 p, Vector3 t)>(4);
                    bucket.Add((p, Tangent(from, to, i)));
                    if (i > from) kept.Add((polylineVertices[i - 1], polylineVertices[i]));
                }
            }

            return Empty(topology.vertexCount).WithEdges(topology, kept);
        }

        /// <summary>
        /// False once this graph has been through <see cref="WithSplitEdges"/>, which does not
        /// rebuild the polyline CSR. Read it before trusting <see cref="polylineCount"/>.
        /// </summary>
        public bool polylinesCarried { get; private set; } = true;

        /// <summary>
        /// The creases less those in a row of ribs: a stroke most of whose points have a stroke
        /// running within 30 degrees of parallel within <paramref name="radius"/> on both sides.
        /// </summary>
        /// <remarks>
        /// A ribbed horn, a hose or a tread measured at a quad's scale is a crease every half cell,
        /// each a few cells round: long enough to keep and too close to follow, and pinned to them
        /// the grid kinks at every rib. A window frame or a door gap has its partner on one side
        /// only, and stays. Boundary edges are kept regardless.
        /// </remarks>
        public FeatureGraph KeepUnribbed(RemeshTopology topology, float radius, out int droppedCurves)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            droppedCurves = 0;
            var curves = polylineCount;
            if (curves == 0 || !(radius > 0f)) return this;
            var P = topology.positions;
            var N = topology.normals;
            // Each polyline point's curve and tangent, in a hash grid of the radius.
            var pointCurve = new List<int>();
            var pointVertex = new List<int>();
            var pointTangent = new List<Vector3>();
            for (var c = 0; c < curves; c++)
            {
                int from = polylineStart[c], to = polylineStart[c + 1];
                for (var i = from; i < to; i++)
                {
                    var prev = polylineVertices[Math.Max(from, i - 1)];
                    var next = polylineVertices[Math.Min(to - 1, i + 1)];
                    pointCurve.Add(c);
                    pointVertex.Add(polylineVertices[i]);
                    pointTangent.Add((P[next] - P[prev]).normalized);
                }
            }
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Cell(Vector3 x) => (Mathf.FloorToInt(x.x / radius), Mathf.FloorToInt(x.y / radius), Mathf.FloorToInt(x.z / radius));
            for (var i = 0; i < pointVertex.Count; i++)
            {
                var k = Cell(P[pointVertex[i]]);
                if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>(4);
                list.Add(i);
            }
            var parallelCos = Mathf.Cos(30f * Mathf.Deg2Rad);
            var r2 = radius * radius;
            var ribbedPoints = new int[curves];
            var points = new int[curves];
            for (var i = 0; i < pointVertex.Count; i++)
            {
                var c = pointCurve[i];
                points[c]++;
                var v = pointVertex[i];
                var t = pointTangent[i];
                if (t.sqrMagnitude < 0.5f) continue;
                var n = N != null && v < N.Length ? N[v] : Vector3.zero;
                var side = Vector3.Cross(n, t);
                if (side.sqrMagnitude < 1e-12f) continue;
                bool left = false, right = false;
                var (x, y, z) = Cell(P[v]);
                for (var dx = -1; dx <= 1 && !(left && right); dx++)
                for (var dy = -1; dy <= 1 && !(left && right); dy++)
                for (var dz = -1; dz <= 1 && !(left && right); dz++)
                {
                    if (!grid.TryGetValue((x + dx, y + dy, z + dz), out var list)) continue;
                    foreach (var j in list)
                    {
                        if (pointCurve[j] == c) continue;
                        var w = P[pointVertex[j]] - P[v];
                        if (w.sqrMagnitude > r2) continue;
                        if (Mathf.Abs(Vector3.Dot(pointTangent[j], t)) < parallelCos) continue;
                        if (Vector3.Dot(w, side) > 0f) left = true; else right = true;
                    }
                }
                if (left && right) ribbedPoints[c]++;
            }
            var edges = new List<(int a, int b)>();
            for (var c = 0; c < curves; c++)
            {
                if (ribbedPoints[c] > 0.5f * points[c]) { droppedCurves++; continue; }
                // A closed polyline repeats its first point at the end, so its pairs close it.
                for (var i = polylineStart[c]; i + 1 < polylineStart[c + 1]; i++)
                    edges.Add((polylineVertices[i], polylineVertices[i + 1]));
            }
            if (droppedCurves == 0) return this;
            return Detect(topology, 0f).WithEdges(topology, edges);
        }

        /// <summary>
        /// The boundary edges plus the crease edges that stand alone: those with less than
        /// <paramref name="maxLengthOverRadius"/> x <paramref name="radius"/> of crease (themselves
        /// included) whose midpoints lie within <paramref name="radius"/> of their own.
        /// </summary>
        /// <remarks>
        /// One crease straight through the disc measures about 2, a corner where three meet about
        /// 3, two creases side by side about 4. A tire's tread, a grille or a knurled grip at the
        /// grid's scale is a dozen creases inside one cell: no grid can follow them, and pinned to
        /// all of them it crumples. They are surface detail, for the normal map.
        /// </remarks>
        public FeatureGraph KeepSparseCreases(RemeshTopology topology, float radius, float maxLengthOverRadius,
            out int dropped)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            dropped = 0;
            if (radius <= 0f || maxLengthOverRadius <= 0f) return this;

            var boundary = new HashSet<long>(EdgeKey.Comparer);
            var halfEdgeCount = topology.halfEdgeVertex?.Length ?? 0;
            for (var h = 0; h < halfEdgeCount; h++)
                if (topology.halfEdgeTwin[h] < 0)
                    boundary.Add(Key(topology.halfEdgeVertex[h], topology.HalfEdgeTarget(h)));

            var creases = new List<long>();
            foreach (var edge in m_FeatureEdges)
                if (!boundary.Contains(edge)) creases.Add(edge);
            if (creases.Count == 0) return this;

            var p = topology.positions;
            var mid = new Vector3[creases.Count];
            var length = new float[creases.Count];
            var cells = new Dictionary<Vector3Int, List<int>>();
            for (var i = 0; i < creases.Count; i++)
            {
                Vector3 a = p[EdgeKey.First(creases[i])], b = p[EdgeKey.Second(creases[i])];
                mid[i] = 0.5f * (a + b);
                length[i] = (b - a).magnitude;
                var c = Vector3Int.FloorToInt(mid[i] / radius);
                if (!cells.TryGetValue(c, out var list)) cells[c] = list = new List<int>();
                list.Add(i);
            }

            var keep = new bool[creases.Count];
            var radiusSqr = radius * radius;
            var limit = maxLengthOverRadius * radius;
            System.Threading.Tasks.Parallel.For(0, creases.Count, BackgroundScheduler.Options, i =>
            {
                var c = Vector3Int.FloorToInt(mid[i] / radius);
                var sum = 0f;
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!cells.TryGetValue(new Vector3Int(c.x + dx, c.y + dy, c.z + dz), out var list)) continue;
                    foreach (var j in list)
                        if ((mid[j] - mid[i]).sqrMagnitude <= radiusSqr) sum += length[j];
                }
                keep[i] = sum <= limit;
            });

            var edges = new List<(int a, int b)>();
            foreach (var edge in boundary) edges.Add((EdgeKey.First(edge), EdgeKey.Second(edge)));
            for (var i = 0; i < creases.Count; i++)
            {
                if (keep[i]) edges.Add((EdgeKey.First(creases[i]), EdgeKey.Second(creases[i])));
                else dropped++;
            }
            if (dropped == 0) return this;
            return Empty(topology.vertexCount).WithEdges(topology, edges);
        }

        /// <summary>
        /// Detects features by dihedral angle. <paramref name="angleDegrees"/> at or below zero
        /// disables crease detection; boundaries are still reported, because a boundary is a feature
        /// regardless of any angle threshold.
        /// </summary>
        public static FeatureGraph Detect(RemeshTopology topology, float angleDegrees,
            bool includeBoundary = true)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));

            var graph = Empty(topology.vertexCount);
            var neighbours = new List<int>[topology.vertexCount];

            var detectCreases = angleDegrees > 0f;
            var cosThreshold = Mathf.Cos(Mathf.Clamp(angleDegrees, 0.01f, 179.99f) * Mathf.Deg2Rad);

            var halfEdgeCount = topology.halfEdgeVertex?.Length ?? 0;

            for (var h = 0; h < halfEdgeCount; h++)
            {
                var twin = topology.halfEdgeTwin[h];

                // Visit each undirected edge once. Boundary edges have no twin, so they are keyed
                // off h alone; interior edges are taken from the lower of the pair.
                if (twin >= 0 && twin < h) continue;

                var a = topology.halfEdgeVertex[h];
                var b = topology.HalfEdgeTarget(h);

                var kind = FeatureKind.None;

                if (twin < 0)
                {
                    if (includeBoundary) kind = FeatureKind.Boundary;
                }
                else if (detectCreases)
                {
                    var n0 = TriangleNormal(topology, h / 3);
                    var n1 = TriangleNormal(topology, twin / 3);

                    if (n0.sqrMagnitude > 1e-20f && n1.sqrMagnitude > 1e-20f &&
                        Vector3.Dot(n0, n1) < cosThreshold)
                        kind = FeatureKind.Crease;
                }

                if (kind == FeatureKind.None) continue;

                graph.m_FeatureEdges.Add(Key(a, b));
                graph.isFeatureVertex[a] = true;
                graph.isFeatureVertex[b] = true;

                (neighbours[a] ??= new List<int>(4)).Add(b);
                (neighbours[b] ??= new List<int>(4)).Add(a);
            }

            for (var v = 0; v < topology.vertexCount; v++)
                graph.featureDegree[v] = (byte)Mathf.Min(255, neighbours[v]?.Count ?? 0);

            graph.BuildTangents(topology, neighbours);
            graph.BuildPolylines(neighbours);

            return graph;
        }

        /// <summary>
        /// As <see cref="Detect"/>, with each edge's dihedral measured between the area-weighted
        /// normals of the faces on either side of it within <paramref name="radius"/>, not between
        /// its two triangles alone. An edge must still bend by <paramref name="minLocalFraction"/> of
        /// the angle itself, so the flat faces beside a corner do not light up with it.
        /// </summary>
        /// <remarks>
        /// A decimated scan rounds a right-angled corner into a chamfer a few tenths of a metre
        /// wide: two bends of about 45 degrees, each under a 35-degree test's notice or not, edge by
        /// edge. Measured on ccnb at 5000, the foot of a 4 m wall bent 9-46 degrees along its edges
        /// and 34 m of 94 passed, so the grid ran a cell out along the ground and cut the wall's
        /// lower half off diagonally. Over half a metre either side the corner is the 90 degrees it is.
        /// </remarks>
        public static FeatureGraph DetectAtScale(RemeshTopology topology, float angleDegrees, float radius,
            float minLocalFraction = 1f / 3f)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (!(radius > 0f) || !(angleDegrees > 0f)) return Detect(topology, angleDegrees);
            var T = topology.triangles;
            var P = topology.positions;
            var faceCount = T.Length / 3;
            var normal = new Vector3[faceCount];
            var centroid = new Vector3[faceCount];
            var area = new float[faceCount];
            for (var f = 0; f < faceCount; f++)
            {
                Vector3 a = P[T[3 * f]], b = P[T[3 * f + 1]], c = P[T[3 * f + 2]];
                var cr = Vector3.Cross(b - a, c - a);
                area[f] = 0.5f * cr.magnitude;
                normal[f] = MeshMath.SafeFaceNormal(a, b, c, Vector3.up);
                centroid[f] = (a + b + c) / 3f;
            }
            var cosAngle = Mathf.Cos(Mathf.Clamp(angleDegrees, 0.01f, 179.99f) * Mathf.Deg2Rad);
            var cosLocal = Mathf.Cos(Mathf.Clamp(angleDegrees * minLocalFraction, 0.01f, 179.99f) * Mathf.Deg2Rad);
            var twin = topology.halfEdgeTwin;
            var halfEdgeCount = topology.halfEdgeVertex?.Length ?? 0;
            var crease = new bool[halfEdgeCount];
            var r2 = radius * radius;
            System.Threading.Tasks.Parallel.For(0, halfEdgeCount, BackgroundScheduler.Options,
                () => (queue: new List<int>(64), seen: new HashSet<int>()),
                (h, _, local) =>
                {
                    var tw = twin[h];
                    if (tw < 0 || tw < h) return local;
                    int f1 = h / 3, f2 = tw / 3;
                    if (Vector3.Dot(normal[f1], normal[f2]) >= cosLocal) return local;
                    var a = topology.halfEdgeVertex[h];
                    var b = topology.HalfEdgeTarget(h);
                    var m = 0.5f * (P[a] + P[b]);
                    var t = P[b] - P[a];
                    var tl = t.magnitude;
                    if (tl <= 0f) return local;
                    t /= tl;
                    Vector3 Across(Vector3 p) { var w = p - m; return w - t * Vector3.Dot(w, t); }
                    var d1 = Across(centroid[f1]).normalized;
                    var d2 = Across(centroid[f2]).normalized;
                    Vector3 n1 = Vector3.zero, n2 = Vector3.zero;
                    var queue = local.queue; var seen = local.seen;
                    queue.Clear(); seen.Clear();
                    queue.Add(f1); queue.Add(f2); seen.Add(f1); seen.Add(f2);
                    for (var i = 0; i < queue.Count; i++)
                    {
                        var g = queue[i];
                        var w = Across(centroid[g]);
                        if (Vector3.Dot(w, d1) >= Vector3.Dot(w, d2)) n1 += normal[g] * area[g];
                        else n2 += normal[g] * area[g];
                        for (var k = 0; k < 3; k++)
                        {
                            var o = twin[3 * g + k];
                            if (o < 0) continue;
                            var nb = o / 3;
                            if (seen.Contains(nb)) continue;
                            seen.Add(nb);
                            if ((centroid[nb] - m).sqrMagnitude > r2) continue;
                            queue.Add(nb);
                        }
                    }
                    if (n1.sqrMagnitude > 0f && n2.sqrMagnitude > 0f
                        && Vector3.Dot(MeshMath.SafeNormalize(n1, Vector3.zero), MeshMath.SafeNormalize(n2, Vector3.zero)) < cosAngle)
                        crease[h] = true;
                    return local;
                }, _ => { });

            var edges = new List<(int a, int b)>();
            for (var h = 0; h < halfEdgeCount; h++)
                if (crease[h]) edges.Add((topology.halfEdgeVertex[h], topology.HalfEdgeTarget(h)));
            // Boundaries, as Detect gives them.
            return Detect(topology, 0f).WithEdges(topology, edges);
        }

        static Vector3 TriangleNormal(RemeshTopology topology, int triangle)
        {
            var a = topology.positions[topology.triangles[triangle * 3]];
            var b = topology.positions[topology.triangles[triangle * 3 + 1]];
            var c = topology.positions[topology.triangles[triangle * 3 + 2]];
            // SafeFaceNormal, not .normalized: a cross product of mesh edges is twice a triangle
            // area, and Vector3.normalized zeroes anything under 1e-5 magnitude, which is every
            // face on a dense scan. A zero normal here reads as a 90-degree dihedral and invents
            // creases along flat ground. See MeshMath.
            return MeshMath.SafeFaceNormal(a, b, c, Vector3.up);
        }

        void BuildTangents(RemeshTopology topology, List<int>[] neighbours)
        {
            for (var v = 0; v < isFeatureVertex.Length; v++)
            {
                var ring = neighbours[v];
                if (ring == null) continue;

                // Only a vertex sitting in the middle of a single curve has one direction. An
                // endpoint has half a direction and a junction has several, and inventing one for
                // either would fight the solver rather than guide it.
                if (ring.Count != 2) continue;

                var tangent = topology.positions[ring[1]] - topology.positions[ring[0]];

                // Project into the tangent plane, so the constraint is expressible as a cross-field
                // direction at all. A feature tangent nearly parallel to the surface normal is
                // degenerate and gets no constraint.
                var n = topology.normals[v];
                tangent -= n * Vector3.Dot(n, tangent);

                var length = tangent.magnitude;
                if (length > 1e-6f) vertexTangent[v] = tangent / length;
            }
        }

        /// <summary>
        /// Chains feature edges into maximal polylines, breaking at endpoints and junctions.
        /// </summary>
        void BuildPolylines(List<int>[] neighbours)
        {
            var starts = new List<int> { 0 };
            var vertices = new List<int>();
            var closed = new List<bool>();
            var visitedEdges = new HashSet<long>(EdgeKey.Comparer);

            // Open curves first, from their endpoints, so a curve that has ends is walked from one
            // rather than being picked up mid-way and split into two.
            for (var pass = 0; pass < 2; pass++)
            {
                for (var v = 0; v < isFeatureVertex.Length; v++)
                {
                    var ring = neighbours[v];
                    if (ring == null) continue;

                    var isJunctionOrEnd = ring.Count != 2;
                    if (pass == 0 && !isJunctionOrEnd) continue;
                    if (pass == 1 && isJunctionOrEnd) continue;

                    foreach (var first in ring)
                    {
                        if (visitedEdges.Contains(Key(v, first))) continue;

                        var walkStart = vertices.Count;
                        vertices.Add(v);

                        var previous = v;
                        var current = first;

                        while (true)
                        {
                            if (!visitedEdges.Add(Key(previous, current))) break;
                            vertices.Add(current);

                            if (current == v) break;                  // closed the loop

                            var currentRing = neighbours[current];
                            if (currentRing == null || currentRing.Count != 2) break;

                            var next = currentRing[0] == previous ? currentRing[1] : currentRing[0];
                            previous = current;
                            current = next;
                        }

                        if (vertices.Count - walkStart < 2)
                        {
                            vertices.RemoveRange(walkStart, vertices.Count - walkStart);
                            continue;
                        }

                        closed.Add(vertices[walkStart] == vertices[vertices.Count - 1]);
                        starts.Add(vertices.Count);
                    }
                }
            }

            polylineStart = starts.ToArray();
            polylineVertices = vertices.ToArray();
            polylineClosed = closed.ToArray();
        }

        /// <summary>
        /// Turns the feature tangents into per-vertex orientation constraints.
        /// </summary>
        /// <remarks>
        /// The weight cap is not cosmetic. Above roughly this fraction of locked vertices there is
        /// almost no free field left to smooth, so the solve stops propagating information and
        /// extraction thrashes against a field that is pinned nearly everywhere. On noisy input a
        /// dihedral threshold can easily lock most of the mesh, so the cap is what stops a bad
        /// detection from being worse than no detection.
        /// </remarks>
        /// <summary>
        /// Each feature vertex's tangent as the chord across about <paramref name="radius"/> of its
        /// polyline either side, in place of the one-ring chord <see cref="vertexTangent"/> holds.
        /// Junctions and corners keep their own.
        /// </summary>
        /// <remarks>
        /// The orientation solve hard-locks every feature vertex to its tangent, so whatever noise
        /// the tangent carries is written straight into the field. A one-ring chord is exact on a
        /// modelled edge and noise on a scan's cut border, which zigzags at the scale of the input
        /// triangles: measured on ccnb, the lattice ignored the border and ran on at the buildings'
        /// angle, so the rim cut through it in a staircase of slivers. Over half a cell the zigzag
        /// averages out and the direction is the border's own.
        /// </remarks>
        public Vector3[] SmoothedTangents(RemeshTopology topology, float radius)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            var positions = topology.positions;
            var result = (Vector3[])vertexTangent.Clone();
            if (!(radius > 0f)) return result;

            for (var c = 0; c < polylineCount; c++)
            {
                var from = polylineStart[c];
                var to = polylineStart[c + 1];
                var n = to - from;
                if (n < 3) continue;
                var closed = polylineClosed[c];
                // A closed loop may list its first vertex again at the end.
                if (closed && polylineVertices[to - 1] == polylineVertices[from]) n--;

                int At(int i) => polylineVertices[from + (closed ? ((i % n) + n) % n : i)];

                for (var i = 0; i < n; i++)
                {
                    var v = At(i);
                    if (featureDegree[v] != 2) continue;

                    var back = i;
                    var walked = 0f;
                    while (walked < radius && (closed ? back > i - n / 2 : back > 0))
                    {
                        walked += (positions[At(back)] - positions[At(back - 1)]).magnitude;
                        back--;
                    }
                    var ahead = i;
                    walked = 0f;
                    while (walked < radius && (closed ? ahead < i + n / 2 : ahead < n - 1))
                    {
                        walked += (positions[At(ahead + 1)] - positions[At(ahead)]).magnitude;
                        ahead++;
                    }

                    var chord = positions[At(ahead)] - positions[At(back)];
                    if (chord.sqrMagnitude > 1e-20f) result[v] = chord.normalized;
                }
            }
            return result;
        }

        public void ToOrientationConstraints(out Vector3[] directions, out float[] weights,
            float weight = 1f, float maxLockedFraction = 0.35f, Vector3[] tangents = null)
        {
            var vertexTangent = tangents ?? this.vertexTangent;
            var count = isFeatureVertex.Length;
            directions = new Vector3[count];
            weights = new float[count];

            var constrained = 0;
            for (var v = 0; v < count; v++)
                if (vertexTangent[v].sqrMagnitude > 1e-12f) constrained++;

            if (count > 0 && constrained > count * maxLockedFraction)
            {
                Debug.LogWarning($"[NativeRemesher] Feature detection constrained {constrained} of " +
                                 $"{count} vertices ({constrained / (float)count:P0}), above the " +
                                 $"{maxLockedFraction:P0} cap. Ignoring the constraints: at this " +
                                 "density there is no free field left to smooth. Raise the feature " +
                                 "angle, or turn detection off for this input.");
                return;
            }

            for (var v = 0; v < count; v++)
            {
                if (vertexTangent[v].sqrMagnitude <= 1e-12f) continue;
                directions[v] = vertexTangent[v];
                weights[v] = weight;
            }
        }

        public override string ToString()
        {
            return $"{featureEdgeCount:N0} feature edges, {featureVertexCount:N0} vertices, " +
                   $"{polylineCount:N0} polylines, {cornerCount:N0} corners";
        }
    }
}
