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
            public long ms;

            public override string ToString() =>
                $"clean x{iterations}: spikes {spikesBefore} -> {spikesAfter}, folded faces {foldedBefore} -> {foldedAfter}, " +
                $"{smoothed} vertex moves, {ms} ms";
        }

        /// <summary>Distance out of the ring's centre, in its mean edge, past which a vertex may be a spike.</summary>
        public static float spikeOffset = 0.3f;

        /// <summary>...and that many times its neighbours' own mean offset: alone in sticking out.</summary>
        public static float spikeIsolation = 2.5f;

        public static Stats Apply(Vector3[] positions, IList<int[]> faces, int iterations)
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
                    if (!boundary[v] && neighbours[v].Count >= 3 && Spike(v)) fix[v] = true;

                var count = 0;
                for (var v = 0; v < n; v++)
                {
                    moved[v] = positions[v];
                    if (!fix[v] || boundary[v]) continue;
                    var c = Vector3.zero;
                    foreach (var u in neighbours[v]) c += positions[u];
                    moved[v] = c / neighbours[v].Count;
                    count++;
                }
                if (count == 0) break;
                stats.smoothed += count;
                Array.Copy(moved, positions, n);
                Normals();
            }

            stats.spikesAfter = Spikes();
            stats.foldedAfter = Folds();
            stats.ms = watch.ElapsedMilliseconds;
            return stats;
        }
    }
}
