// Pass K: put an edge loop ON each crease the cross field was steered along.
//
// THE DEFECT. With Loops Along Creases the coherent creases steer the cross field, so the quads run
// parallel to a panel edge, a fold, a strap -- but nothing decides where across the crease the loops
// fall. The crease lands anywhere inside a quad row, and the row straddling it is bent through the
// middle: on the helmet's plate edges and the balaclava's folds that reads as a staircase of dark
// and light quads along every hard line, the "wobble". A strap narrower
// than a quad whose two edges both fall mid-row is not represented at all.
//
// Cutting the lattice along the creases (IntegerGridMap.longCreaseLoopsCut) fixes the hard lines and
// wrecks everything else on a scan: needles and torn strips wherever creases are doubled or noisy.
// This takes the finished, stable steer-only mesh instead and slides the loop already running
// alongside each crease onto it. Because the field was aligned to the crease, that loop is never
// more than half a cell away.
//
// THE GUARDS.
//   1. A vertex moves only if one of its own edges runs along the crease (within ~30 degrees): it is
//      on a loop parallel to the crease, not on a rung crossing it.
//   2. Nearest first, and a snapped vertex blocks its neighbours across the crease from snapping to
//      the same crease, so a crease half-way between two loops claims one of them, not alternate
//      vertices of both -- which would be a zig-zag worse than the staircase.
//   3. No face may flip or be left with an edge under minEdgeCells of a cell.
//   4. Boundary vertices stay put.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class CreaseLoopSnapStats
    {
        public int segments;
        public int candidates;
        public int snapped;
        public int rejectedBlocked;
        public int rejectedFlip;
        public int rejectedShort;
        public float meanMoveCells;

        public override string ToString() =>
            $"segments={segments} candidates={candidates} snapped={snapped} blocked={rejectedBlocked} " +
            $"flip={rejectedFlip} short={rejectedShort} meanMove={meanMoveCells:F3} cells";
    }

    public static class CreaseLoopSnap
    {
        public static CreaseLoopSnapStats Run(List<int[]> faces, Vector3[] positions,
            Vector3[] creasePositions, FeatureGraph creases, float scale,
            float maxCells = 0.5f, float minEdgeCells = 0.2f, float alongCos = 0.85f,
            int[] snappedCurve = null, Vector3[] snappedTangent = null)
        {
            var stats = new CreaseLoopSnapStats();
            if (faces == null || positions == null || creases == null || creasePositions == null
                || !(scale > 0f) || creases.polylineCount == 0)
                return stats;

            // Crease segments, hashed by the cells they pass through.
            var segA = new List<Vector3>();
            var segB = new List<Vector3>();
            var segCurve = new List<int>();
            for (var c = 0; c < creases.polylineCount; c++)
                for (var i = creases.polylineStart[c] + 1; i < creases.polylineStart[c + 1]; i++)
                {
                    segA.Add(creasePositions[creases.polylineVertices[i - 1]]);
                    segB.Add(creasePositions[creases.polylineVertices[i]]);
                    segCurve.Add(c);
                }
            stats.segments = segA.Count;
            if (segA.Count == 0) return stats;

            var radius = maxCells * scale;
            var cell = Mathf.Max(radius, 1e-9f);
            var hash = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Key(Vector3 p) =>
                (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
            for (var s = 0; s < segA.Count; s++)
            {
                var length = (segB[s] - segA[s]).magnitude;
                var steps = Mathf.Max(1, Mathf.CeilToInt(length / cell));
                var added = new HashSet<(int, int, int)>();
                for (var k = 0; k <= steps; k++)
                {
                    var key = Key(Vector3.Lerp(segA[s], segB[s], k / (float)steps));
                    if (!added.Add(key)) continue;
                    if (!hash.TryGetValue(key, out var list)) hash[key] = list = new List<int>(4);
                    list.Add(s);
                }
            }

            // Output adjacency, boundary and incident faces.
            var n = positions.Length;
            var neighbours = new List<int>[n];
            var facesAt = new List<int>[n];
            var edgeUse = new Dictionary<long, int>(EdgeKey.Comparer);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    (facesAt[a] ??= new List<int>(4)).Add(f);
                    var key = EdgeKey.PackUndirected(a, b);
                    edgeUse.TryGetValue(key, out var used);
                    edgeUse[key] = used + 1;
                    if (used == 0)
                    {
                        (neighbours[a] ??= new List<int>(4)).Add(b);
                        (neighbours[b] ??= new List<int>(4)).Add(a);
                    }
                }
            }
            var boundary = new bool[n];
            foreach (var entry in edgeUse)
                if (entry.Value != 2)
                {
                    boundary[EdgeKey.First(entry.Key)] = true;
                    boundary[EdgeKey.Second(entry.Key)] = true;
                }

            // Nearest crease point per vertex.
            var target = new Vector3[n];
            var tangent = new Vector3[n];
            var curve = new int[n];
            var distance = new float[n];
            var order = new List<int>();
            var carried = snappedCurve != null && snappedCurve.Length == n
                                               && snappedTangent != null && snappedTangent.Length == n;
            for (var v = 0; v < n; v++)
            {
                curve[v] = -1;
                if (boundary[v] || neighbours[v] == null || facesAt[v] == null) continue;
                if (carried && snappedCurve[v] >= 0) continue;   // placed by an earlier round
                var p = positions[v];
                var (x, y, z) = Key(p);
                var best = radius * radius;
                var bestSeg = -1;
                var bestPoint = Vector3.zero;
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!hash.TryGetValue((x + dx, y + dy, z + dz), out var list)) continue;
                    foreach (var s in list)
                    {
                        var q = ClosestOnSegment(p, segA[s], segB[s]);
                        var d = (q - p).sqrMagnitude;
                        if (d >= best) continue;
                        best = d;
                        bestSeg = s;
                        bestPoint = q;
                    }
                }
                if (bestSeg < 0) continue;

                // Guard 5: the crease must lie along the surface from v, not across a gap above
                // or below it -- the far side of a strap, the other lip of a fold.
                var vn = Vector3.zero;
                foreach (var f in facesAt[v]) vn += QuadExtractor.FaceNormal(positions, faces[f]);
                if (vn.sqrMagnitude > 1e-30f
                    && Mathf.Abs(Vector3.Dot(bestPoint - p, vn.normalized)) > Extraction.IntegerGridMap.creaseLoopMaxNormalCells * scale)
                    continue;

                var t = (segB[bestSeg] - segA[bestSeg]).normalized;
                // Guard 1: v must sit on a loop running along the crease.
                var along = false;
                foreach (var w in neighbours[v])
                {
                    var e = positions[w] - p;
                    var len = e.magnitude;
                    if (len > 1e-12f && Mathf.Abs(Vector3.Dot(e / len, t)) >= alongCos) { along = true; break; }
                }
                if (!along) continue;

                target[v] = bestPoint;
                tangent[v] = t;
                curve[v] = segCurve[bestSeg];
                distance[v] = Mathf.Sqrt(best);
                order.Add(v);
            }
            stats.candidates = order.Count;
            order.Sort((a, b) => distance[a].CompareTo(distance[b]));

            var blocked = new HashSet<long>();
            void Block(int v, int c, Vector3 t, Vector3 at)
            {
                foreach (var w in neighbours[v])
                {
                    var e = positions[w] - at;
                    var len = e.magnitude;
                    if (len <= 1e-12f) continue;
                    if (Mathf.Abs(Vector3.Dot(e / len, t)) < alongCos)
                        blocked.Add(((long)w << 32) | (uint)c);
                }
            }
            if (carried)
                for (var v = 0; v < n; v++)
                    if (snappedCurve[v] >= 0 && neighbours[v] != null)
                        Block(v, snappedCurve[v], snappedTangent[v], positions[v]);

            var minEdge2 = minEdgeCells * scale * minEdgeCells * scale;
            double moved = 0;
            foreach (var v in order)
            {
                if (blocked.Contains(((long)v << 32) | (uint)curve[v])) { stats.rejectedBlocked++; continue; }

                var original = positions[v];
                positions[v] = target[v];
                if (Flips(faces, facesAt[v], positions, v, original))
                {
                    positions[v] = original;
                    stats.rejectedFlip++;
                    continue;
                }
                if (TooShort(faces, facesAt[v], positions, minEdge2))
                {
                    positions[v] = original;
                    stats.rejectedShort++;
                    continue;
                }

                stats.snapped++;
                moved += (target[v] - original).magnitude;

                // Guard 2: the rungs crossing the crease at v may not bring a second loop onto it.
                Block(v, curve[v], tangent[v], original);
                if (carried)
                {
                    snappedCurve[v] = curve[v];
                    snappedTangent[v] = tangent[v];
                }
            }

            stats.meanMoveCells = stats.snapped > 0 ? (float)(moved / stats.snapped / scale) : 0f;
            return stats;
        }

        static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            var l2 = ab.sqrMagnitude;
            if (l2 <= 1e-24f) return a;
            var t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / l2);
            return a + ab * t;
        }

        static bool Flips(List<int[]> faces, List<int> at, Vector3[] positions, int vertex, Vector3 original)
        {
            foreach (var f in at)
            {
                var face = faces[f];
                var after = QuadExtractor.FaceNormal(positions, face);
                var saved = positions[vertex];
                positions[vertex] = original;
                var before = QuadExtractor.FaceNormal(positions, face);
                positions[vertex] = saved;
                if (Vector3.Dot(before, after) <= 0f) return true;

                // Each corner's own triangle as well: a quad can fold across a diagonal while its
                // summed normal still points the same way.
                if (face.Length == 4)
                    for (var k = 0; k < 4; k++)
                    {
                        var a = positions[face[(k + 3) % 4]];
                        var b = positions[face[k]];
                        var c = positions[face[(k + 1) % 4]];
                        if (Vector3.Dot(Vector3.Cross(b - a, c - b), before) <= 0f) return true;
                    }
            }
            return false;
        }

        static bool TooShort(List<int[]> faces, List<int> at, Vector3[] positions, float min2)
        {
            foreach (var f in at)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                    if ((positions[face[k]] - positions[face[(k + 1) % face.Length]]).sqrMagnitude < min2)
                        return true;
            }
            return false;
        }
    }
}
