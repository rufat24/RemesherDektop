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
            for (var c = 0; c < polylineCount; c++)
            {
                var from = polylineStart[c];
                var to = polylineStart[c + 1];
                if (to - from < 2) continue;
                if (boundary.Contains(Key(polylineVertices[from], polylineVertices[from + 1])))
                    continue;

                var walked = 0f;
                for (var i = from + 1; i < to; i++)
                    walked += (positions[polylineVertices[i]] - positions[polylineVertices[i - 1]]).magnitude;
                var span = (positions[polylineVertices[to - 1]] - positions[polylineVertices[from]]).magnitude;
                // A closed loop has no span; its size is what it encloses, so take half its length.
                if (polylineClosed[c]) span = 0.5f * walked;

                if (span < minExtent || walked <= 0f || span / walked < minStraightness)
                {
                    droppedCurves++;
                    continue;
                }

                keptCurves++;
                for (var i = from + 1; i < to; i++)
                    kept.Add((polylineVertices[i - 1], polylineVertices[i]));
            }

            var result = Empty(topology.vertexCount);
            var boundaryOnly = new List<(int a, int b)>();
            foreach (var key in boundary) boundaryOnly.Add((EdgeKey.First(key), EdgeKey.Second(key)));
            boundaryOnly.AddRange(kept);
            return result.WithEdges(topology, boundaryOnly);
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
