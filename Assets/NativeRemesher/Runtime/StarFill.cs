using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    /// <summary>
    /// A last pass over the output: a vertex the lattice gathered many faces round, folded over
    /// each other (a star), is taken out with its faces and the hole filled flat across its ring.
    /// </summary>
    /// <remarks>
    /// Detail a cell or two across that the lattice cannot follow -- a house's window recesses,
    /// three cells wide on the white house at 5,000 -- is collapsed into one vertex with 13 to 24
    /// faces round it, some facing the other way: the stars the windows came out as. Moving the
    /// vertex (SurfaceClean) cannot help, it already sits in its ring's plane; the faces are what is
    /// wrong. Filled across the ring, the window is flat, and the baked maps (the normal map, the
    /// cavities) carry its recess, as they carry everything smaller than the quads.
    /// </remarks>
    public static class StarFill
    {
        /// <summary>Fill stars (false, the default: left as the lattice made them). Measured on the white house at 5,000: 14 of 16 filled, but across a ring of frame and recess both, the fill came out as large shards, no better to look at than the stars.</summary>
        public static bool enabled = false;

        /// <summary>A star has at least this many neighbours and a face folded against the rest...</summary>
        public static int minValence = 8;

        /// <summary>...or this many, folded or not.</summary>
        public static int alwaysValence = 12;

        /// <summary>The ring may lie off its plane by at most this share of its mean radius to be filled flat.</summary>
        public static float maxRelief = 0.35f;

        public sealed class Stats
        {
            public int stars, filled, notLoops, notFlat, notSimple, quadsAfter;
            public long ms;
            public override string ToString() =>
                $"stars {stars}: {filled} filled, {notLoops} rings not one loop, {notFlat} not flat, {notSimple} not simple, {ms} ms";
        }

        public static Stats Apply(List<int[]> faces, Vector3[] positions)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stats = new Stats();
            if (!enabled || faces == null || positions == null || faces.Count == 0) return stats;
            var n = positions.Length;
            var around = new List<int>[n];
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null) continue;
                foreach (var v in face)
                    if (v >= 0 && v < n) (around[v] ??= new List<int>(6)).Add(f);
            }
            Vector3 Normal(int[] face)
            {
                var s = Vector3.zero;
                for (var k = 1; k + 1 < face.Length; k++)
                    s += Vector3.Cross(positions[face[k]] - positions[face[0]], positions[face[k + 1]] - positions[face[0]]);
                return s;
            }
            // Candidates, most faces first.
            var candidates = new List<(int v, int valence)>();
            for (var v = 0; v < n; v++)
            {
                if (around[v] == null || around[v].Count < minValence) continue;
                var neighbours = new HashSet<int>();
                foreach (var f in around[v])
                {
                    var face = faces[f];
                    var k = Array.IndexOf(face, v);
                    neighbours.Add(face[(k + 1) % face.Length]);
                    neighbours.Add(face[(k + face.Length - 1) % face.Length]);
                }
                if (neighbours.Count < minValence) continue;
                var sum = Vector3.zero;
                foreach (var f in around[v]) sum += Normal(faces[f]);
                var folded = false;
                foreach (var f in around[v]) if (Vector3.Dot(Normal(faces[f]), sum) < 0f) { folded = true; break; }
                if (folded || neighbours.Count >= alwaysValence) candidates.Add((v, neighbours.Count));
            }
            candidates.Sort((a, b) => b.valence.CompareTo(a.valence));
            stats.stars = candidates.Count;

            var removed = new bool[faces.Count];
            var added = new List<int[]>();
            foreach (var (v, _) in candidates)
            {
                // The region: v's faces, grown round any vertex where its outline touches itself
                // (folded faces pinch the fan), until its outline is one loop.
                var region = new HashSet<int>(around[v]);
                List<int> loop = null;
                var faceSum = Vector3.zero;
                for (var grow = 0; grow <= 3 && loop == null; grow++)
                {
                    var count = new Dictionary<long, int>(Extraction.EdgeKey.Comparer);
                    var directed = new Dictionary<long, (int a, int b)>(Extraction.EdgeKey.Comparer);
                    foreach (var f in region)
                    {
                        var face = faces[f];
                        for (var i = 0; i < face.Length; i++)
                        {
                            int a0 = face[i], b0 = face[(i + 1) % face.Length];
                            if (a0 == b0) continue;
                            var key = Extraction.EdgeKey.PackUndirected(a0, b0);
                            count[key] = count.TryGetValue(key, out var c) ? c + 1 : 1;
                            directed[key] = (a0, b0);
                        }
                    }
                    var link = new Dictionary<int, List<int>>();
                    foreach (var kv in count)
                    {
                        if (kv.Value != 1) continue;
                        var (ea, eb) = directed[kv.Key];
                        (link.TryGetValue(ea, out var la) ? la : link[ea] = new List<int>(2)).Add(eb);
                        (link.TryGetValue(eb, out var lb) ? lb : link[eb] = new List<int>(2)).Add(ea);
                    }
                    var odd = new List<int>();
                    foreach (var kv in link) if (kv.Value.Count != 2) odd.Add(kv.Key);
                    if (odd.Count == 0 && link.Count >= 3)
                    {
                        var walk = new List<int>();
                        var first = -1;
                        foreach (var key in link.Keys) { first = key; break; }
                        int prev = -1, at = first;
                        do
                        {
                            walk.Add(at);
                            var l = link[at];
                            var next = l[0] != prev ? l[0] : l[1];
                            prev = at;
                            at = next;
                        } while (at != first && walk.Count <= link.Count);
                        if (at == first && walk.Count == link.Count) { loop = walk; break; }
                        // Two loops or more: not a disk.
                        foreach (var key in link.Keys) odd.Add(key);
                    }
                    var grown = false;
                    foreach (var o in odd)
                        if (around[o] != null)
                            foreach (var f in around[o])
                                grown |= region.Add(f);
                    if (!grown) break;
                }
                var mine = new List<int>(region);
                var taken = false;
                foreach (var f in mine) if (removed[f]) { taken = true; break; }
                if (taken) continue;
                var broken = loop == null;
                if (!broken)
                {
                    foreach (var f in mine) faceSum += Normal(faces[f]);
                    var loopNormal = Vector3.zero;
                    for (var i = 0; i < loop.Count; i++)
                        loopNormal += Vector3.Cross(positions[loop[i]], positions[loop[(i + 1) % loop.Count]]);
                    if (Vector3.Dot(loopNormal, faceSum) < 0f) loop.Reverse();
                }
                if (broken) { stats.notLoops++; continue; }

                // Flat enough: the ring's plane (area-weighted normal through its centre).
                var centre = Vector3.zero;
                foreach (var p in loop) centre += positions[p];
                centre /= loop.Count;
                var normal = Vector3.zero;
                for (var i = 0; i < loop.Count; i++)
                    normal += Vector3.Cross(positions[loop[i]] - centre, positions[loop[(i + 1) % loop.Count]] - centre);
                if (!(normal.sqrMagnitude > 0f)) { stats.notFlat++; continue; }
                // By hand: Vector3.Normalize returns zero under 1e-5, and the white house is a
                // hundredth of a unit across.
                normal /= normal.magnitude;
                float radius = 0f, relief = 0f;
                foreach (var p in loop)
                {
                    var d = positions[p] - centre;
                    radius += d.magnitude;
                    relief = Mathf.Max(relief, Mathf.Abs(Vector3.Dot(d, normal)));
                }
                radius /= loop.Count;
                if (relief > maxRelief * radius) { stats.notFlat++; continue; }

                // Ear clipping in the plane, wound as the loop (which is wound as the faces were).
                var axisU = Vector3.Cross(normal, Mathf.Abs(normal.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
                var axisV = Vector3.Cross(normal, axisU);
                var flat = new List<Vector2>(loop.Count);
                foreach (var p in loop) { var d = positions[p] - centre; flat.Add(new Vector2(Vector3.Dot(d, axisU), Vector3.Dot(d, axisV))); }
                var triangles = EarClip(flat);
                if (triangles == null)
                {
                    if (stats.notSimple++ == 0 && QuadReduction.logStats)
                        Debug.Log("[Native Remesher] star not simple: " + string.Join(" ", flat.ConvertAll(q => $"{q.x:G6},{q.y:G6}")));
                    continue;
                }
                foreach (var f in mine) removed[f] = true;
                for (var t = 0; t + 2 < triangles.Count; t += 3)
                    added.Add(new[] { loop[triangles[t]], loop[triangles[t + 1]], loop[triangles[t + 2]] });
                stats.filled++;
            }
            if (stats.filled == 0) { stats.ms = watch.ElapsedMilliseconds; return stats; }
            var kept = new List<int[]>(faces.Count + added.Count);
            for (var f = 0; f < faces.Count; f++) if (!removed[f]) kept.Add(faces[f]);
            kept.AddRange(added);
            Extraction.TrianglePairing.Run(kept, positions);
            faces.Clear();
            faces.AddRange(kept);
            stats.ms = watch.ElapsedMilliseconds;
            return stats;
        }

        // Triangles (indices into the polygon) of a simple polygon wound counter-clockwise; null
        // when it is not simple (no ear left before it is done). Points on an ear's edges do not
        // block it (a straight run of the ring is many collinear points).
        static List<int> EarClip(List<Vector2> polygon)
        {
            var count = polygon.Count;
            double area = 0;
            var size = 0f;
            for (var i = 0; i < count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % count];
                area += (double)a.x * b.y - (double)b.x * a.y;
                size = Mathf.Max(size, (b - a).magnitude);
            }
            if (area <= 0) return null;
            var eps = 1e-6f * size * size;
            var index = new List<int>(count);
            for (var i = 0; i < count; i++) index.Add(i);
            var result = new List<int>(3 * (count - 2));
            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            while (index.Count > 3)
            {
                // The ear with the best smallest angle, not the first: fewer slivers to pair.
                var best = -1;
                var bestScore = float.NegativeInfinity;
                for (var i = 0; i < index.Count; i++)
                {
                    int ia = index[(i + index.Count - 1) % index.Count], ib = index[i], ic = index[(i + 1) % index.Count];
                    Vector2 a = polygon[ia], b = polygon[ib], c = polygon[ic];
                    var turn = Cross(a, b, c);
                    if (turn <= eps) continue;
                    var inside = false;
                    foreach (var j in index)
                    {
                        if (j == ia || j == ib || j == ic) continue;
                        var p = polygon[j];
                        if (Cross(a, b, p) > eps && Cross(b, c, p) > eps && Cross(c, a, p) > eps) { inside = true; break; }
                    }
                    if (inside) continue;
                    var score = Mathf.Min(Vector2.Angle(b - a, c - a), Mathf.Min(Vector2.Angle(a - b, c - b), Vector2.Angle(a - c, b - c)));
                    if (score > bestScore) { bestScore = score; best = i; }
                }
                if (best < 0) return null;
                {
                    int ia = index[(best + index.Count - 1) % index.Count], ib = index[best], ic = index[(best + 1) % index.Count];
                    result.Add(ia); result.Add(ib); result.Add(ic);
                    index.RemoveAt(best);
                }
            }
            if (index.Count == 3) { result.Add(index[0]); result.Add(index[1]); result.Add(index[2]); }
            return result;
        }
    }
}
