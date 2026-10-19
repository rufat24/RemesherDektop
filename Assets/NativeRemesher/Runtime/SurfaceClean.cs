using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    /// <summary>
    /// A last pass over the output: vertices that stick out of (or into) their ring on their own
    /// -- a lone spike or dent -- and faces folded over their neighbours are smoothed into the
    /// ring. Creases, rims and real bumps, which their neighbours share, are left alone.
    /// </summary>
    /// <remarks>
    /// Detail smaller than a quad -- the notch at a collar's front, a fold of a sleeve, a buckle --
    /// cannot be represented by the lattice, and a vertex that happens to land in it is pulled
    /// half an edge out of the surface the rest of its ring describes: the "pop-out" at the body's
    /// collar (2026-10-15; umbrella offset -0.50 edges against 0.03 round it). The detail is not
    /// lost -- the normal map carries it. Tried first and dropped: moving every vertex to the source
    /// averaged over its ring, plus tangential relaxation; no visible change on the arms and more
    /// spikes than before (54 against 19). Smoothing a fold's whole ring with it: more folds after
    /// four passes on the head (35 against 29); some faces this counts as folded are a thin cable's
    /// real sides.
    ///
    /// Given the surface the output was solved on, a sharp point the surface itself rises to is
    /// kept: a cone's apex is a lone vertex out of its ring too, and was cut down by a whole edge
    /// (sword points, claws, horns). Sharp: at least tipMinOffset edges out of its ring (a cone
    /// opening under about 90 degrees). Real: halfway to each neighbour the surface is still at
    /// least tipKeepFraction of the vertex's height above the ring (a cone: 0.5). Sharpness is the
    /// test that matters on real meshes -- measured on the raw body at 5,000 and head at 3,000, most
    /// lone spikes ARE on ring-scale source shape (strap and rim edges; the collar's pop-out: 0.56),
    /// but at 0.3-0.57 edges; 1 of 58 and 2 of 136 are 0.7 or more.
    /// Putting moved vertices back onto that surface (projectReachEdges) is off: it puts most of
    /// them back where they were -- body at 5,000, spikes left 9 -> 38, folded faces 1 -> 7.
    /// </remarks>
    public static class SurfaceClean
    {
        public sealed class Stats
        {
            public int iterations;
            public int spikesBefore;
            public int spikesAfter;
            public int foldedBefore;
            public int foldedAfter;
            public int smoothed;
            public int keptTips;
            public int projected;
            public long ms;

            public override string ToString() =>
                $"clean x{iterations}: spikes {spikesBefore} -> {spikesAfter}, folded faces {foldedBefore} -> {foldedAfter}, " +
                $"{smoothed} vertex moves, {keptTips} real tips kept, {projected} put back on the surface, {ms} ms";
        }

        /// <summary>Distance out of the ring's centre, in its mean edge, past which a vertex may be a spike.</summary>
        public static float spikeOffset = 0.3f;

        /// <summary>...and that many times its neighbours' own mean offset: alone in sticking out.</summary>
        public static float spikeIsolation = 2.5f;

        /// <summary>
        /// A spike is kept when the surface, halfway to its neighbours, is still at least this
        /// fraction of the vertex's height above the ring (a cone: 0.5; a narrow notch: about 0).
        /// </summary>
        public static float tipKeepFraction = 0.25f;

        /// <summary>A kept tip is at least this many edges out of its ring's centre (a 90-degree cone: 0.71).</summary>
        public static float tipMinOffset = 0.7f;

        /// <summary>
        /// Moved vertices go back onto the surface (its nearest point) when within this many edges
        /// of it; 0, the default, leaves them at their ring's centre (see the remarks).
        /// </summary>
        public static float projectReachEdges = 0f;

        public static Stats Apply(Vector3[] positions, IList<int[]> faces, int iterations) =>
            Apply(positions, faces, iterations, null);

        /// <param name="surface">
        /// The surface the output lies on: real tips are told from spikes against it, and moved
        /// vertices are put back on it. Null: neither, as before.
        /// </param>
        /// <param name="hold">
        /// Vertices never moved, or null: the ones the quad layout laid on a building edge or a
        /// border line (QuadExtractionResult.onLayoutLine), which sit there on purpose and look
        /// like spikes to the test below.
        /// </param>
        public static Stats Apply(Vector3[] positions, IList<int[]> faces, int iterations, SpatialGrid surface,
            bool[] hold = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stats = new Stats { iterations = iterations };
            var n = positions.Length;
            if (n == 0 || faces == null || faces.Count == 0)
                return stats;

            // Rings: neighbours, faces, and the edges' face counts (boundary = one face).
            var neighbours = new List<int>[n];
            var vertexFaces = new List<int>[n];
            for (var i = 0; i < n; i++) { neighbours[i] = new List<int>(6); vertexFaces[i] = new List<int>(6); }
            var edgeFaces = new Dictionary<long, (int a, int b)>();
            long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    int a = face[k], b = face[(k + 1) % face.Length];
                    vertexFaces[a].Add(f);
                    if (!neighbours[a].Contains(b)) neighbours[a].Add(b);
                    if (!neighbours[b].Contains(a)) neighbours[b].Add(a);
                    var key = Key(a, b);
                    edgeFaces[key] = edgeFaces.TryGetValue(key, out var e) ? (e.a, f) : (f, -1);
                }
            }
            var boundary = new bool[n];
            var faceNeighbours = new List<int>[faces.Count];
            for (var f = 0; f < faces.Count; f++) faceNeighbours[f] = new List<int>(4);
            foreach (var kv in edgeFaces)
            {
                if (kv.Value.b < 0)
                {
                    boundary[(int)(kv.Key >> 32)] = true;
                    boundary[(int)(kv.Key & 0xffffffff)] = true;
                }
                else
                {
                    faceNeighbours[kv.Value.a].Add(kv.Value.b);
                    faceNeighbours[kv.Value.b].Add(kv.Value.a);
                }
            }

            var faceNormals = new Vector3[faces.Count];
            var vertexNormals = new Vector3[n];
            var offset = new float[n];
            var folded = new bool[faces.Count];
            var fix = new bool[n];
            var moved = new Vector3[n];

            void Normals()
            {
                Parallel.For(0, faces.Count, BackgroundScheduler.Options, f =>
                {
                    var face = faces[f];
                    var s = Vector3.zero;
                    for (var k = 0; k < face.Length; k++)
                        s += Vector3.Cross(positions[face[k]], positions[face[(k + 1) % face.Length]]);
                    faceNormals[f] = s;
                });
                Parallel.For(0, n, BackgroundScheduler.Options, v =>
                {
                    var s = Vector3.zero;
                    foreach (var f in vertexFaces[v]) s += faceNormals[f];
                    var nv = MeshMath.SafeNormalize(s, Vector3.up);
                    vertexNormals[v] = nv;
                    offset[v] = 0f;
                    if (boundary[v] || neighbours[v].Count < 3) return;
                    var c = Vector3.zero;
                    var e = 0f;
                    foreach (var u in neighbours[v]) { c += positions[u]; e += (positions[u] - positions[v]).magnitude; }
                    c /= neighbours[v].Count;
                    e /= neighbours[v].Count;
                    if (e > 0f) offset[v] = Vector3.Dot(positions[v] - c, nv) / e;
                });
            }

            // A face pointing against the faces round it: a fold, not a crease (a thin lip's top
            // face still points with its walls' mean).
            int Folds()
            {
                var count = 0;
                for (var f = 0; f < faces.Count; f++)
                {
                    var around = Vector3.zero;
                    foreach (var g in faceNeighbours[f]) around += MeshMath.SafeNormalize(faceNormals[g], Vector3.zero);
                    folded[f] = faceNeighbours[f].Count > 0
                                && Vector3.Dot(MeshMath.SafeNormalize(faceNormals[f], Vector3.zero), around) < 0f;
                    if (folded[f]) count++;
                }
                return count;
            }

            bool Spike(int v)
            {
                var o = Mathf.Abs(offset[v]);
                if (o < spikeOffset) return false;
                var around = 0f;
                foreach (var u in neighbours[v]) around += Mathf.Abs(offset[u]);
                around /= neighbours[v].Count;
                return o > spikeIsolation * around;
            }

            float MeanEdge(int v)
            {
                var e = 0f;
                foreach (var u in neighbours[v]) e += (positions[u] - positions[v]).magnitude;
                return e / Mathf.Max(1, neighbours[v].Count);
            }

            // Height of the surface above the ring's plane at a point, along the ring's normal.
            float SurfaceHeight(Vector3 at, Vector3 centre, Vector3 normal, float reach)
            {
                var hit = surface.Raycast(at, normal, reach, 0f, out var t, out _, out _, out _)
                    ? at + normal * t
                    : surface.NearestPoint(at, out _);
                return Vector3.Dot(hit - centre, normal);
            }

            // A spike the surface rises to as well, over the ring: a real tip.
            var kept = new bool[n];
            bool RealTip(int v)
            {
                if (surface == null) return false;
                var c = Vector3.zero;
                foreach (var u in neighbours[v]) c += positions[u];
                c /= neighbours[v].Count;
                var normal = vertexNormals[v];
                var h = Vector3.Dot(positions[v] - c, normal);
                var e = MeanEdge(v);
                if (!(Mathf.Abs(h) >= tipMinOffset * e)) return false;
                var sum = 0f;
                foreach (var u in neighbours[v])
                    sum += SurfaceHeight((positions[v] + positions[u]) * 0.5f, c, normal, e);
                return sum / neighbours[v].Count / h >= tipKeepFraction;
            }

            int Spikes()
            {
                var count = 0;
                for (var v = 0; v < n; v++)
                    if (!boundary[v] && neighbours[v].Count >= 3 && Spike(v)) count++;
                return count;
            }

            Normals();
            stats.foldedBefore = Folds();
            stats.spikesBefore = Spikes();

            for (var it = 0; it < iterations; it++)
            {
                if (it > 0) Folds();
                Array.Clear(fix, 0, n);
                for (var f = 0; f < faces.Count; f++)
                    if (folded[f])
                        foreach (var v in faces[f]) fix[v] = true;
                for (var v = 0; v < n; v++)
                    if (!boundary[v] && neighbours[v].Count >= 3 && Spike(v))
                    {
                        if (!fix[v] && RealTip(v))
                        {
                            if (!kept[v]) { kept[v] = true; stats.keptTips++; }
                            continue;
                        }
                        fix[v] = true;
                    }

                var count = 0;
                for (var v = 0; v < n; v++)
                {
                    moved[v] = positions[v];
                    if (!fix[v] || boundary[v] || hold != null && v < hold.Length && hold[v]) continue;
                    var c = Vector3.zero;
                    foreach (var u in neighbours[v]) c += positions[u];
                    moved[v] = c / neighbours[v].Count;
                    count++;
                }
                if (count == 0) break;
                stats.smoothed += count;
                Array.Copy(moved, positions, n);
                Normals();
                if (surface != null && projectReachEdges > 0f)
                {
                    var projected = 0;
                    for (var v = 0; v < n; v++)
                    {
                        if (!fix[v] || boundary[v] || hold != null && v < hold.Length && hold[v]) continue;
                        var reach = projectReachEdges * MeanEdge(v);
                        if (!(reach > 0f)) continue;
                        // The nearest point, not along the normal: over a notch narrower than the ring
                        // that is its rim, where the normal would drop the vertex back into it.
                        var onSurface = surface.NearestPoint(positions[v], out var distance);
                        if (!(distance <= reach)) continue;
                        positions[v] = onSurface;
                        projected++;
                    }
                    stats.projected += projected;
                    if (projected > 0) Normals();
                }
            }

            stats.spikesAfter = Spikes();
            stats.foldedAfter = Folds();
            stats.ms = watch.ElapsedMilliseconds;
            return stats;
        }
    }
}
