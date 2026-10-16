using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    /// <summary>
    /// Thin free-standing bands of the input -- straps, belts, flat cables -- recognised on the
    /// input and meshed on their own, as a swept rectangular tube a few quads round and about a
    /// cell per quad along; the rest of the input is solved without them.
    /// </summary>
    /// <remarks>
    /// The helmet head's chin straps are bands about two cells wide and a quarter of a cell thick,
    /// standing off the cheek. The decimated mesh keeps them as tubes, but the quad layout cuts
    /// across them -- an isotropic lattice has no row a quarter of a cell wide for the rims -- and
    /// the strap comes out a sliver melted into the face (2026-10-17). QuadRemesher's output of the
    /// same head meshes each one as a rectangle swept along it: four corners round, long quads.
    ///
    /// Recognised from the shape itself, as a thickness map would show it: every input vertex casts
    /// inward along its normal; where the far side of the part is within maxThicknessCells, the
    /// vertex is thin. Connected thin faces are one side of a plate, and the side its rays land on
    /// is the other. The midpoints between the two sides make the plate's middle sheet; its centre
    /// line runs through them, ordered by distance along the sheet, with the plate's normal (or, for
    /// a round cable, a frame carried along the line) for the frame. A plate is cut into runs where
    /// its width or direction changes -- two straps through a buckle make one L-shaped plate -- and
    /// a run is a ribbon when it is long, narrow, and free along both long edges (its own rim faces
    /// out beside it): a helmet brim, thin but attached along its length, is not. Each ring of the
    /// tube is cut from the part itself, its corners the section's extreme points.
    ///
    /// Its faces (both sides, and the rims: within three quarters of the thickness of the middle
    /// sheet) are taken out of the input, and the holes left where it met the rest -- its cut
    /// section, long and thin -- zipped shut; an end that runs into another part is pushed a little
    /// into it, a free end is capped. The tubes are made at the edge length the rest came out at.
    ///
    /// Measured on the helmet head at 3,000 (2026-10-17): nine runs found -- the chin straps both
    /// sides, upright and along the jaw, the two straps on the back of the helmet, the two at the
    /// nape -- 448 quads of tube; irregular 3.40 -> 3.56 %, chamfer 0.00104 -> 0.00101. The body and
    /// the ccnb scan have no free band: nothing found, output unchanged.
    /// </remarks>
    public static class RibbonParts
    {
        /// <summary>A plate is thin below this many cells through.</summary>
        public static float maxThicknessCells = 0.6f;

        /// <summary>Shortest ribbon, along its centre line, in cells.</summary>
        public static float minLengthCells = 3f;

        /// <summary>Widest ribbon, median over its length, in cells.</summary>
        public static float maxWidthCells = 4f;

        /// <summary>Least length over median width.</summary>
        public static float minAspect = 2.5f;

        /// <summary>Least fraction of the rim samples with air beside them.</summary>
        public static float minFreeRimFraction = 0.75f;

        /// <summary>A run of a plate ends where its width leaves the plate's median by more than this fraction.</summary>
        public static float runWidthTolerance = 0.25f;

        /// <summary>Spacing of the cross-sections along a ribbon, in cells.</summary>
        public static float stationSpacingCells = 1f;

        /// <summary>Quads across a broad side: the width over this many cells, rounded, at least one.</summary>
        public static float quadWidthCells = 1.75f;

        /// <summary>How far an end that meets another part is pushed into it, in cells.</summary>
        public static float embedCells = 0.5f;

        /// <summary>Fewest thin faces a side needs to be considered at all.</summary>
        public static int minSideFaces = 24;

        /// <summary>Diagnostics: when set, one line per plate measured -- where, how long, how wide, and the verdict.</summary>
        public static List<string> debugLog;

        /// <summary>Diagnostics: when set, the corners of every thin face, three per face.</summary>
        public static List<Vector3> debugThinFaces;

        /// <summary>Diagnostics: when set, the input as the rest is solved on -- ribbons out, holes closed.</summary>
        public static Action<Vector3[], int[]> debugKept;

        static void Log(string verdict, Vector3 at, float length, float width, float free, float cell)
        {
            if (debugLog == null) return;
            lock (debugLog)
                debugLog.Add($"{verdict} at ({at.x:F3},{at.y:F3},{at.z:F3}) length {length / cell:F1} width {width / cell:F2} free {free:F2} cells");
        }

        public sealed class Stats
        {
            public int thinVertices, sides, pairs, ribbons;
            public int rejectedShort, rejectedWide, rejectedStubby, rejectedAttached;
            public int removedTriangles, holes, fillTriangles, quads, shredTriangles;
            public long ms;

            public override string ToString() =>
                $"ribbons={ribbons} of {pairs} plates ({sides} sides, {thinVertices} thin vertices) " +
                $"rejected short {rejectedShort} wide {rejectedWide} stubby {rejectedStubby} attached {rejectedAttached} | " +
                $"removed {removedTriangles} tris ({shredTriangles} in shreds), {holes} holes closed with {fillTriangles} | quads {quads} | {ms} ms";
        }

        /// <summary>One recognised band: its centre line and the cross-section along it.</summary>
        public sealed class Ribbon
        {
            public Vector3[] centre;
            public Vector3[] up;
            public float[] width;
            public float[] thickness;
            public bool freeStart, freeEnd;
            public float length;
            public float medianWidth;
            /// <summary>How far past the first and last centre point the plate reached.</summary>
            public float startTrim, endTrim;
            /// <summary>As thick as it is wide: a cable, framed by transport along it rather than by its normal.</summary>
            public bool round;
            /// <summary>The corners of its faces in the input, three per face, which its rings are cut from.</summary>
            public List<Vector3> faceCorners;
            /// <summary>Midpoints between its two sides: its middle sheet. Used while finding its faces.</summary>
            public Vector3[] medial;
            public float medianThickness;
            /// <summary>Its thin faces in the input topology. Used while finding its faces.</summary>
            public List<int> seedFaces;
        }

        /// <summary>
        /// Finds the ribbons of a mesh and returns the mesh without them: the triangles left
        /// (source indices) and the triangles closing the holes, over the same positions.
        /// <paramref name="sourceOfTriangle"/> gives, per output triangle, the source
        /// triangle it is or borders; <paramref name="outwardSign"/> is -1 when the input is wound
        /// inside out, and <paramref name="surface"/> the input's search grid.
        /// </summary>
        public static List<Ribbon> Find(Vector3[] positions, int[] triangles, float cell,
            out Vector3[] keptPositions, out int[] keptTriangles, out int[] sourceOfTriangle,
            out float outwardSign, out SpatialGrid surface, Stats stats)
        {
            outwardSign = 1f;
            surface = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats ??= new Stats();
            var ribbons = new List<Ribbon>();
            keptPositions = positions;
            keptTriangles = triangles;
            sourceOfTriangle = null;
            if (positions == null || triangles == null || triangles.Length < 3 || !(cell > 0f)) return ribbons;

            var topology = RemeshTopology.Build(positions, triangles);
            var P = topology.positions;
            var T = topology.triangles;
            var nv = topology.vertexCount;
            var nt = topology.triangleCount;
            var twin = topology.halfEdgeTwin;

            // Which way is out: the sign of the enclosed volume. Thickness is measured inward.
            double volume = 0;
            for (var t = 0; t < nt; t++)
                volume += Vector3.Dot(P[T[3 * t]], Vector3.Cross(P[T[3 * t + 1]], P[T[3 * t + 2]]));
            var outward = volume >= 0 ? 1f : -1f;
            outwardSign = outward;
            var N = new Vector3[nv];
            for (var v = 0; v < nv; v++) N[v] = topology.normals[v] * outward;

            var grid = new SpatialGrid(P, T);
            surface = grid;

            // --- thickness: inward ray to the far side of the part ---
            var maxThickness = maxThicknessCells * cell;
            var half = 0.5f * maxThickness;
            var thickness = new float[nv];
            var opposite = new int[nv];
            Parallel.For(0, nv, v =>
            {
                thickness[v] = float.PositiveInfinity;
                opposite[v] = -1;
                var d = -N[v];
                // The segment [p, p + d * maxThickness], as a search both ways round its midpoint;
                // facing: only triangles whose winding normal (times outward) points along d -- the
                // far wall, not this one.
                if (!grid.Raycast(P[v] + d * half, d, half, outward, out var t, out var tri, out _, out _)) return;
                var distance = half + t;
                if (!(distance > 1e-4f * cell)) return;
                thickness[v] = distance;
                opposite[v] = tri;
            });

            var thinFace = new bool[nt];
            for (var t = 0; t < nt; t++)
                thinFace[t] = thickness[T[3 * t]] < maxThickness && thickness[T[3 * t + 1]] < maxThickness
                              && thickness[T[3 * t + 2]] < maxThickness;
            for (var v = 0; v < nv; v++) if (thickness[v] < maxThickness) stats.thinVertices++;
            if (debugThinFaces != null)
                for (var t = 0; t < nt; t++)
                    if (thinFace[t])
                        for (var i = 0; i < 3; i++) debugThinFaces.Add(P[T[3 * t + i]]);

            // --- sides: connected thin faces ---
            var side = new int[nt];
            for (var t = 0; t < nt; t++) side[t] = -1;
            var sideFaces = new List<List<int>>();
            var queue = new Queue<int>();
            for (var seed = 0; seed < nt; seed++)
            {
                if (!thinFace[seed] || side[seed] >= 0) continue;
                var id = sideFaces.Count;
                var list = new List<int>();
                side[seed] = id;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    list.Add(t);
                    for (var i = 0; i < 3; i++)
                    {
                        var o = twin[3 * t + i];
                        if (o < 0) continue;
                        var u = o / 3;
                        if (thinFace[u] && side[u] < 0) { side[u] = id; queue.Enqueue(u); }
                    }
                }
                sideFaces.Add(list);
            }
            stats.sides = sideFaces.Count;

            // --- plates: each side with the side its rays land on ---
            var group = new int[sideFaces.Count];
            for (var i = 0; i < group.Length; i++) group[i] = i;
            int Root(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }
            for (var s = 0; s < sideFaces.Count; s++)
            {
                if (sideFaces[s].Count < minSideFaces) continue;
                var votes = new Dictionary<int, int>();
                var total = 0;
                foreach (var t in sideFaces[s])
                    for (var i = 0; i < 3; i++)
                    {
                        var o = opposite[T[3 * t + i]];
                        if (o < 0) continue;
                        total++;
                        var os = side[o];
                        if (os < 0) continue;
                        votes[os] = votes.TryGetValue(os, out var c) ? c + 1 : 1;
                    }
                var best = -1; var bestVotes = 0;
                foreach (var kv in votes) if (kv.Value > bestVotes) { best = kv.Key; bestVotes = kv.Value; }
                if (best < 0 || bestVotes < 0.3f * total || sideFaces[best].Count < minSideFaces) continue;
                group[Root(s)] = Root(best);
            }
            var plates = new Dictionary<int, List<int>>();
            for (var s = 0; s < sideFaces.Count; s++)
            {
                if (sideFaces[s].Count < minSideFaces) continue;
                var r = Root(s);
                if (!plates.TryGetValue(r, out var l)) plates[r] = l = new List<int>();
                l.Add(s);
            }
            stats.pairs = plates.Count;

            var removed = new bool[nt];
            foreach (var plate in plates.Values)
            {
                var seeds = new List<int>();
                foreach (var s in plate) seeds.AddRange(sideFaces[s]);
                var found = Measure(topology, N, thickness, opposite, grid, seeds, cell, outward, stats);
                if (found == null) continue;
                foreach (var ribbon in found)
                {
                    ribbons.Add(ribbon);
                    // Its faces: both sides, and the rims between them.
                    Collect(topology, ribbon, ribbon.seedFaces, removed, cell);
                    ribbon.seedFaces = null;
                }
            }
            stats.ribbons = ribbons.Count;
            if (ribbons.Count == 0) { stats.ms = watch.ElapsedMilliseconds; return ribbons; }
            DropShreds(topology, removed, cell, stats);

            Rebuild(positions, triangles, topology, removed, out keptPositions, out keptTriangles, out sourceOfTriangle, stats);
            debugKept?.Invoke(keptPositions, keptTriangles);
            stats.ms = watch.ElapsedMilliseconds;
            return ribbons;
        }

        // ------------------------------------------------------------------
        // The ribbons of one plate -- its centre line and cross-section, in runs -- or null.
        static List<Ribbon> Measure(RemeshTopology topology, Vector3[] N, float[] thickness, int[] opposite,
            SpatialGrid grid, List<int> faces, float cell, float outward, Stats stats)
        {
            var P = topology.positions;
            var T = topology.triangles;
            // The plate's vertices (both sides) and their midpoints, which lie on its middle sheet.
            var seenVertex = new HashSet<int>();
            var verts = new List<int>();
            foreach (var t in faces)
                for (var i = 0; i < 3; i++)
                {
                    var v = T[3 * t + i];
                    if (seenVertex.Add(v)) verts.Add(v);
                }
            var n = verts.Count;
            var mid = new Vector3[n];
            for (var i = 0; i < n; i++) mid[i] = P[verts[i]] - N[verts[i]] * (0.5f * thickness[verts[i]]);

            // The middle sheet as a graph: midpoints within a radius of each other. One sheet for
            // both sides, so a side broken into pieces still gives one line.
            var radius = Mathf.Max(2.5f * topology.averageEdgeLength, 0.12f * cell);
            var buckets = new Dictionary<Vector3Int, List<int>>();
            Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / radius), Mathf.FloorToInt(p.y / radius), Mathf.FloorToInt(p.z / radius));
            for (var i = 0; i < n; i++)
            {
                var key = Key(mid[i]);
                if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<int>();
                l.Add(i);
            }
            var adjacency = new List<int>[n];
            var r2 = radius * radius;
            for (var i = 0; i < n; i++)
            {
                adjacency[i] = new List<int>();
                var key = Key(mid[i]);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dz = -1; dz <= 1; dz++)
                            if (buckets.TryGetValue(new Vector3Int(key.x + dx, key.y + dy, key.z + dz), out var l))
                                foreach (var j in l)
                                    if (j != i && (mid[j] - mid[i]).sqrMagnitude <= r2) adjacency[i].Add(j);
            }
            float[] Distances(int from)
            {
                var d = new float[n];
                for (var i = 0; i < n; i++) d[i] = float.PositiveInfinity;
                var heap = new MinHeap(n);
                d[from] = 0f;
                heap.Push(from, 0f);
                while (heap.Count > 0)
                {
                    heap.Pop(out var i, out var di);
                    if (di > d[i]) continue;
                    foreach (var j in adjacency[i])
                    {
                        var dj = di + Vector3.Distance(mid[i], mid[j]);
                        if (dj < d[j]) { d[j] = dj; heap.Push(j, dj); }
                    }
                }
                return d;
            }
            int Farthest(float[] d)
            {
                var best = 0;
                for (var i = 1; i < n; i++) if (!float.IsInfinity(d[i]) && d[i] > d[best]) best = i;
                return best;
            }
            var a0 = Farthest(Distances(0));
            var along = Distances(a0);
            var length = along[Farthest(along)];
            if (length < minLengthCells * cell) { stats.rejectedShort++; Log("short", mid[a0], length, 0f, 0f, cell); return null; }

            // Binned by distance along: centres, and the plate's normal with one sign throughout.
            var bins = Mathf.Clamp(Mathf.CeilToInt(length / (0.5f * cell)), 2, 4096);
            var members = new List<int>[bins];
            for (var k = 0; k < bins; k++) members[k] = new List<int>();
            for (var i = 0; i < n; i++)
                if (!float.IsInfinity(along[i]))
                    members[Mathf.Min(bins - 1, (int)(along[i] / length * bins))].Add(i);
            var centre = new List<Vector3>();
            var up = new List<Vector3>();
            var tk = new List<float>();
            var binOfCentre = new List<int>();
            var reference = Vector3.zero;
            for (var k = 0; k < bins; k++)
            {
                if (members[k].Count < 3) continue;
                var sum = Vector3.zero;
                foreach (var i in members[k]) sum += mid[i];
                centre.Add(sum / members[k].Count);
                if (reference == Vector3.zero) reference = N[verts[members[k][0]]];
                var plateNormal = reference;
                for (var pass = 0; pass < 2; pass++)
                {
                    var acc = Vector3.zero;
                    foreach (var i in members[k])
                    {
                        var nv = N[verts[i]];
                        acc += Vector3.Dot(nv, plateNormal) >= 0f ? nv : -nv;
                    }
                    plateNormal = MeshMath.SafeNormalize(acc, plateNormal);
                }
                reference = plateNormal;
                up.Add(plateNormal);
                var th = new List<float>(members[k].Count);
                foreach (var i in members[k]) th.Add(thickness[verts[i]]);
                th.Sort();
                tk.Add(th[th.Count / 2]);
                binOfCentre.Add(k);
            }
            var m = centre.Count;
            if (m < 3) { stats.rejectedShort++; return null; }
            var c = centre.ToArray();
            for (var pass = 0; pass < 3; pass++)
            {
                var s = (Vector3[])c.Clone();
                for (var k = 1; k + 1 < m; k++) s[k] = 0.25f * c[k - 1] + 0.5f * c[k] + 0.25f * c[k + 1];
                c = s;
            }
            var tangent = new Vector3[m];
            for (var k = 0; k < m; k++)
                tangent[k] = MeshMath.SafeNormalize(c[Mathf.Min(m - 1, k + 1)] - c[Mathf.Max(0, k - 1)], Vector3.forward);
            var u = up.ToArray();
            for (var pass = 0; pass < 2; pass++)
            {
                var s = (Vector3[])u.Clone();
                for (var k = 1; k + 1 < m; k++) s[k] = MeshMath.SafeNormalize(u[k - 1] + 2f * u[k] + u[k + 1], u[k]);
                u = s;
            }
            for (var k = 0; k < m; k++)
                u[k] = MeshMath.SafeNormalize(u[k] - tangent[k] * Vector3.Dot(u[k], tangent[k]), OrientationField.ArbitraryTangent(tangent[k]));
            var across = new Vector3[m];
            for (var k = 0; k < m; k++) across[k] = Vector3.Cross(u[k], tangent[k]);

            // Width: the midpoints' spread across, plus the rims round them.
            var width = new float[m];
            var thicknessAt = tk.ToArray();
            for (var k = 0; k < m; k++)
            {
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                foreach (var i in members[binOfCentre[k]])
                {
                    var x = Vector3.Dot(mid[i] - c[k], across[k]);
                    lo = Mathf.Min(lo, x);
                    hi = Mathf.Max(hi, x);
                }
                width[k] = hi - lo + thicknessAt[k];
                c[k] += across[k] * (0.5f * (lo + hi));
            }
            var sorted = (float[])width.Clone();
            Array.Sort(sorted);
            var medianWidth = sorted[m / 2];
            var sortedThickness = (float[])thicknessAt.Clone();
            Array.Sort(sortedThickness);
            var medianThickness = sortedThickness[m / 2];

            // A round part (a cable): its normal goes all the way round and says nothing about a
            // frame. Carried along the line instead, turning as little as it can (double reflection).
            var round = medianThickness > 0.6f * medianWidth;
            if (round)
            {
                for (var k = 1; k < m; k++)
                {
                    var v1 = c[k] - c[k - 1];
                    var c1 = Vector3.Dot(v1, v1);
                    if (!(c1 > 0f)) { u[k] = u[k - 1]; continue; }
                    var rL = u[k - 1] - (2f / c1) * Vector3.Dot(v1, u[k - 1]) * v1;
                    var tL = tangent[k - 1] - (2f / c1) * Vector3.Dot(v1, tangent[k - 1]) * v1;
                    var v2 = tangent[k] - tL;
                    var c2 = Vector3.Dot(v2, v2);
                    u[k] = c2 > 0f ? rL - (2f / c2) * Vector3.Dot(v2, rL) * v2 : rL;
                    u[k] = MeshMath.SafeNormalize(u[k] - tangent[k] * Vector3.Dot(u[k], tangent[k]), u[k - 1]);
                }
                for (var k = 0; k < m; k++) across[k] = Vector3.Cross(u[k], tangent[k]);
            }
            if (medianWidth > maxWidthCells * cell) { stats.rejectedWide++; Log("wide", c[m / 2], length, medianWidth, 0f, cell); return null; }

            // --- runs: stretches of steady width and direction ---
            //
            // A plate is often more than one strap: two straps through a buckle make an L, a strap
            // and a buckle's plates one region. Where the width leaves the plate's median by a
            // quarter, or the line turns sharply, the run ends; each run long enough is a ribbon on
            // its own, and the rest (the corner, the buckle) stays with the lattice. The end bins,
            // cut by round distance fronts and holding only part of the width, fall out the same way.
            var good = new bool[m];
            for (var k = 0; k < m; k++)
            {
                var steady = Mathf.Abs(width[k] - medianWidth) <= runWidthTolerance * medianWidth;
                var straight = Vector3.Dot(tangent[Mathf.Max(0, k - 1)], tangent[Mathf.Min(m - 1, k + 1)]) > 0.6f;
                good[k] = steady && straight;
            }
            var vertexBin = new Dictionary<int, int>();
            for (var k = 0; k < m; k++)
                foreach (var i in members[binOfCentre[k]]) vertexBin[verts[i]] = k;

            bool Rim(Vector3 from, Vector3 dir, float reach)
            {
                var h = 0.5f * reach;
                return grid.Raycast(from + dir * h, dir, h, outward, out _, out _, out _, out _);
            }
            bool Air(Vector3 p)
            {
                var q = grid.NearestPoint(p, out var dist, out var nrm);
                return dist > 0.05f * cell && Vector3.Dot(p - q, nrm * outward) > 0f;
            }

            var found = new List<Ribbon>();
            for (var k0 = 0; k0 < m; k0++)
            {
                if (!good[k0]) continue;
                var k1 = k0;
                while (k1 + 1 < m && good[k1 + 1]) k1++;
                var start = k0;
                k0 = k1;
                var count = k1 - start + 1;
                if (count < 2) continue;

                var rc = Slice(c, start, count);
                var lineLength = 0f;
                for (var k = 1; k < count; k++) lineLength += Vector3.Distance(rc[k - 1], rc[k]);
                // Past each end, as far as the plate went before the next run or its own end (the
                // bins dropped there), at most a cell and a half.
                var before = start;
                while (before > 0 && !good[before - 1] && start - before < 3) before--;
                var after = k1;
                while (after < m - 1 && !good[after + 1] && after - k1 < 3) after++;
                var startTrim = Mathf.Min(1.5f * cell, Vector3.Distance(c[before], c[start]));
                var endTrim = Mathf.Min(1.5f * cell, Vector3.Distance(c[k1], c[after]));
                var at = rc[count / 2];
                if (lineLength + startTrim + endTrim < minAspect * medianWidth || lineLength < minLengthCells * cell)
                { stats.rejectedStubby++; Log("stubby", at, lineLength, medianWidth, 0f, cell); continue; }

                // A free rim: from inside the plate, sideways, its own narrow side faces out within
                // half the width. Along an edge the plate is attached by, the material goes on into
                // the part it is attached to, and the first wall facing that way is far off (a strap
                // lying against the face is no attachment: its rim is still there).
                var free = 0;
                for (var k = start; k <= k1; k++)
                {
                    var reach = 0.5f * width[k] + 0.25f * thicknessAt[k] + 0.1f * cell;
                    if (Rim(c[k], across[k], reach)) free++;
                    if (Rim(c[k], -across[k], reach)) free++;
                }
                if (free < minFreeRimFraction * 2 * count)
                { stats.rejectedAttached++; Log("attached", at, lineLength, medianWidth, free / (2f * count), cell); continue; }
                Log(round ? "cable" : "ribbon", at, lineLength, medianWidth, free / (2f * count), cell);

                var rw = Slice(width, start, count);
                var rt = Slice(thicknessAt, start, count);
                for (var k = 0; k < count; k++)
                {
                    rw[k] = Mathf.Clamp(rw[k], 0.85f * medianWidth, 1.15f * medianWidth);
                    rt[k] = Mathf.Clamp(rt[k], 0.7f * medianThickness, 1.3f * medianThickness);
                }
                // Its own midpoints and faces: the run's bins and one each side.
                var medial = new List<Vector3>();
                for (var k = Mathf.Max(0, start - 1); k <= Mathf.Min(m - 1, k1 + 1); k++)
                    foreach (var i in members[binOfCentre[k]]) medial.Add(mid[i]);
                var seedFaces = new List<int>();
                foreach (var t in faces)
                {
                    var inside = true;
                    for (var j = 0; j < 3 && inside; j++)
                        inside = vertexBin.TryGetValue(T[3 * t + j], out var kb) && kb >= start - 1 && kb <= k1 + 1;
                    if (inside) seedFaces.Add(t);
                }
                var rtan = Slice(tangent, start, count);
                found.Add(new Ribbon
                {
                    centre = rc,
                    up = Slice(u, start, count),
                    width = rw,
                    thickness = rt,
                    length = lineLength,
                    freeStart = Air(rc[0] - rtan[0] * (startTrim + 0.4f * cell)),
                    freeEnd = Air(rc[count - 1] + rtan[count - 1] * (endTrim + 0.4f * cell)),
                    medianWidth = medianWidth,
                    startTrim = startTrim,
                    endTrim = endTrim,
                    round = round,
                    medial = medial.ToArray(),
                    medianThickness = medianThickness,
                    seedFaces = seedFaces
                });
            }
            return found;
        }

        static T[] Slice<T>(T[] a, int start, int count)
        {
            var r = new T[count];
            Array.Copy(a, start, r, 0, count);
            return r;
        }

        // ------------------------------------------------------------------
        // Marks the ribbon's faces: the seeds, and every face reached from them whose centre is
        // within three quarters of the thickness of the middle sheet -- the rims round its long
        // edges, the ends; not a surface it lies over (that is half the thickness off, plus the gap).
        static void Collect(RemeshTopology topology, Ribbon ribbon, List<int> seeds, bool[] removed, float cell)
        {
            var P = topology.positions;
            var T = topology.triangles;
            var twin = topology.halfEdgeTwin;
            var reach = 0.75f * ribbon.medianThickness + 0.02f * cell;
            var r2 = reach * reach;
            var buckets = new Dictionary<Vector3Int, List<Vector3>>();
            Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / reach), Mathf.FloorToInt(p.y / reach), Mathf.FloorToInt(p.z / reach));
            foreach (var q in ribbon.medial)
            {
                var key = Key(q);
                if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<Vector3>();
                l.Add(q);
            }
            bool Inside(int t)
            {
                var x = (P[T[3 * t]] + P[T[3 * t + 1]] + P[T[3 * t + 2]]) / 3f;
                var key = Key(x);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dz = -1; dz <= 1; dz++)
                            if (buckets.TryGetValue(new Vector3Int(key.x + dx, key.y + dy, key.z + dz), out var l))
                                foreach (var q in l)
                                    if ((q - x).sqrMagnitude <= r2) return true;
                return false;
            }
            var queue = new Queue<int>();
            var seen = new HashSet<int>();
            var taken = new List<int>();
            foreach (var t in seeds)
            {
                removed[t] = true;
                seen.Add(t);
                queue.Enqueue(t);
                taken.Add(t);
            }
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                for (var i = 0; i < 3; i++)
                {
                    var o = twin[3 * t + i];
                    if (o < 0) continue;
                    var g = o / 3;
                    if (!seen.Add(g)) continue;
                    if (removed[g] || !Inside(g)) continue;
                    removed[g] = true;
                    queue.Enqueue(g);
                    taken.Add(g);
                }
            }
            ribbon.faceCorners = new List<Vector3>(3 * taken.Count);
            foreach (var t in taken)
                for (var i = 0; i < 3; i++) ribbon.faceCorners.Add(P[T[3 * t + i]]);
            ribbon.medial = null;
        }

        // Faces left in pieces cut off from the rest and small -- shreds of a ribbon's rim the
        // flood did not reach -- go with it.
        static void DropShreds(RemeshTopology topology, bool[] removed, float cell, Stats stats)
        {
            var nt = topology.triangleCount;
            var twin = topology.halfEdgeTwin;
            var P = topology.positions;
            var T = topology.triangles;
            var component = new int[nt];
            for (var t = 0; t < nt; t++) component[t] = -1;
            var queue = new Queue<int>();
            var members = new List<int>();
            var limit = 4f * cell * cell;
            var id = 0;
            for (var seed = 0; seed < nt; seed++)
            {
                if (removed[seed] || component[seed] >= 0) continue;
                members.Clear();
                component[seed] = id;
                queue.Enqueue(seed);
                var area = 0f;
                var touchesRemoved = false;
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    members.Add(t);
                    area += 0.5f * Vector3.Cross(P[T[3 * t + 1]] - P[T[3 * t]], P[T[3 * t + 2]] - P[T[3 * t]]).magnitude;
                    for (var i = 0; i < 3; i++)
                    {
                        var o = twin[3 * t + i];
                        if (o < 0) continue;
                        var g = o / 3;
                        if (removed[g]) { touchesRemoved = true; continue; }
                        if (component[g] < 0) { component[g] = id; queue.Enqueue(g); }
                    }
                }
                id++;
                if (!touchesRemoved || area > limit) continue;
                foreach (var t in members) { removed[t] = true; stats.shredTriangles++; }
            }
        }

        // Nearest point of the centre line: segment k, fraction f along it, and s, the fraction of
        // the whole line (below 0 or above 1 off the ends).
        static void Locate(Ribbon ribbon, Vector3 x, out int segment, out float fraction, out float s)
        {
            var c = ribbon.centre;
            var best = float.PositiveInfinity;
            segment = 0; fraction = 0f;
            var bestAlong = 0f;
            var along = 0f;
            for (var k = 0; k + 1 < c.Length; k++)
            {
                var d = c[k + 1] - c[k];
                var l2 = d.sqrMagnitude;
                var raw = l2 > 0f ? Vector3.Dot(x - c[k], d) / l2 : 0f;
                var f = Mathf.Clamp01(raw);
                if (k == 0 && raw < 0f) f = raw;
                if (k + 2 == c.Length && raw > 1f) f = raw;
                var dist = (c[k] + d * f - x).sqrMagnitude;
                if (dist < best) { best = dist; segment = k; fraction = f; bestAlong = along + f * Mathf.Sqrt(l2); }
                along += Mathf.Sqrt(l2);
            }
            s = ribbon.length > 0f ? bestAlong / ribbon.length : 0f;
        }

        // The cross-section frame at fraction f of segment k.
        static void Frame(Ribbon ribbon, int k, float f, out Vector3 c, out Vector3 tangent, out Vector3 up,
            out Vector3 across, out float width, out float thickness)
        {
            var m = ribbon.centre.Length;
            var k1 = Mathf.Min(m - 1, k + 1);
            var g = Mathf.Clamp01(f);
            c = ribbon.centre[k] + (ribbon.centre[k1] - ribbon.centre[k]) * f;
            tangent = MeshMath.SafeNormalize(ribbon.centre[k1] - ribbon.centre[k], Vector3.forward);
            up = MeshMath.SafeNormalize(Vector3.Lerp(ribbon.up[k], ribbon.up[k1], g), ribbon.up[k]);
            up = MeshMath.SafeNormalize(up - tangent * Vector3.Dot(up, tangent), OrientationField.ArbitraryTangent(tangent));
            across = Vector3.Cross(up, tangent);
            width = Mathf.Lerp(ribbon.width[k], ribbon.width[k1], g);
            thickness = Mathf.Lerp(ribbon.thickness[k], ribbon.thickness[k1], g);
        }

        // ------------------------------------------------------------------
        // The input without the removed triangles, its holes zipped shut.
        static void Rebuild(Vector3[] positions, int[] triangles, RemeshTopology topology, bool[] removed,
            out Vector3[] keptPositions, out int[] keptTriangles, out int[] sourceOfTriangle, Stats stats)
        {
            var T = topology.triangles;
            var twin = topology.halfEdgeTwin;
            var nt = topology.triangleCount;
            var removedSource = new bool[triangles.Length / 3];
            for (var t = 0; t < nt; t++)
                if (removed[t]) { removedSource[topology.sourceTriangle[t]] = true; stats.removedTriangles++; }

            // A source vertex for each welded one.
            var sourceVertex = new int[topology.vertexCount];
            for (var i = 0; i < sourceVertex.Length; i++) sourceVertex[i] = -1;
            for (var i = topology.sourceToWelded.Length - 1; i >= 0; i--)
            {
                var w = topology.sourceToWelded[i];
                if (w >= 0) sourceVertex[w] = i;
            }

            var outTriangles = new List<int>(triangles.Length);
            var outSource = new List<int>(triangles.Length / 3);
            for (var t = 0; t * 3 + 2 < triangles.Length; t++)
            {
                if (removedSource[t]) continue;
                outTriangles.Add(triangles[3 * t]);
                outTriangles.Add(triangles[3 * t + 1]);
                outTriangles.Add(triangles[3 * t + 2]);
                outSource.Add(t);
            }

            // The new border: kept half-edges whose twin was removed, by start vertex.
            var next = new Dictionary<int, List<int>>();
            for (var t = 0; t < nt; t++)
            {
                if (removed[t]) continue;
                for (var i = 0; i < 3; i++)
                {
                    var h = 3 * t + i;
                    var o = twin[h];
                    if (o < 0 || !removed[o / 3]) continue;
                    var a = topology.halfEdgeVertex[h];
                    if (!next.TryGetValue(a, out var l)) next[a] = l = new List<int>(1);
                    l.Add(h);
                }
            }
            var used = new HashSet<int>();
            foreach (var start in next)
                foreach (var h0 in start.Value)
                {
                    if (used.Contains(h0)) continue;
                    var loop = new List<int>();
                    var h = h0;
                    for (var guard = 0; guard < 1 << 20; guard++)
                    {
                        if (!used.Add(h)) break;
                        loop.Add(h);
                        var b = topology.HalfEdgeTarget(h);
                        if (!next.TryGetValue(b, out var l)) break;
                        var found = -1;
                        foreach (var x in l) if (!used.Contains(x)) { found = x; break; }
                        if (found < 0) break;
                        h = found;
                    }
                    if (loop.Count < 3) continue;
                    stats.holes++;
                    // The hole a band leaves where it ran into another part is its cut section:
                    // long and thin. Zipped shut from one end of it to the other -- a fan round its
                    // middle would stand a star of slivers up off the surface.
                    var ring = new List<int>(loop.Count);
                    foreach (var x in loop) ring.Add(topology.halfEdgeVertex[x]);
                    var n = ring.Count;
                    int e0 = 0, e1 = 0;
                    var far = -1f;
                    for (var i = 0; i < n; i++)
                        for (var j = i + 1; j < n; j++)
                        {
                            var d = (topology.positions[ring[i]] - topology.positions[ring[j]]).sqrMagnitude;
                            if (d > far) { far = d; e0 = i; e1 = j; }
                        }
                    // Two chains from e0 to e1: forward (along the loop) and backward.
                    var source = topology.sourceTriangle[loop[0] / 3];
                    void Emit(int a, int b, int c)
                    {
                        int sa = sourceVertex[a], sb = sourceVertex[b], sc = sourceVertex[c];
                        if (sa < 0 || sb < 0 || sc < 0 || sa == sb || sb == sc || sa == sc) return;
                        outTriangles.Add(sa); outTriangles.Add(sb); outTriangles.Add(sc);
                        outSource.Add(source);
                        stats.fillTriangles++;
                    }
                    // The loop runs the kept faces' way round; the fill goes the other way.
                    var fwd = e0; var bwd = e0;
                    var fwdEnd = e1; var bwdEnd = e1;
                    var stepsF = (e1 - e0 + n) % n;
                    var stepsB = (e0 - e1 + n) % n;
                    int doneF = 0, doneB = 0;
                    while (doneF < stepsF || doneB < stepsB)
                    {
                        var nf = (fwd + 1) % n;
                        var nb = (bwd - 1 + n) % n;
                        bool advanceF;
                        if (doneF >= stepsF) advanceF = false;
                        else if (doneB >= stepsB) advanceF = true;
                        else
                            advanceF = (topology.positions[ring[nf]] - topology.positions[ring[bwd]]).sqrMagnitude
                                       <= (topology.positions[ring[nb]] - topology.positions[ring[fwd]]).sqrMagnitude;
                        if (advanceF)
                        {
                            Emit(ring[nf], ring[fwd], ring[bwd]);
                            fwd = nf; doneF++;
                        }
                        else
                        {
                            Emit(ring[fwd], ring[bwd], ring[nb]);
                            bwd = nb; doneB++;
                        }
                    }
                }

            keptPositions = positions;
            keptTriangles = outTriangles.ToArray();
            sourceOfTriangle = outSource.ToArray();
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// The ribbons as quad tubes: a cross-section ring every stationSpacingCells along, two
        /// broad sides of width / quadWidthCells quads (at least one) and a narrow quad over each
        /// rim. Each ring is read off the part itself: its faces cut by the plane across the line
        /// there, the ring's corners the section's extreme points in the frame's four diagonal
        /// directions. Free ends are capped. <paramref name="cell"/> is the edge length the tubes
        /// are made at -- the rest of the output's, so the ribbons match it.
        /// </summary>
        public static void Mesh(List<Ribbon> ribbons, SpatialGrid surface, float cell, float outward,
            out Vector3[] positions, out List<int[]> faces)
        {
            var outPositions = new List<Vector3>();
            var outFaces = new List<int[]>();
            foreach (var ribbon in ribbons)
            {
                var k = Mathf.Max(1, Mathf.RoundToInt(ribbon.medianWidth / (quadWidthCells * cell)));
                var ringSize = 2 * k + 2;
                var startExtra = ribbon.startTrim + (ribbon.freeStart ? 0f : embedCells * cell);
                var endExtra = ribbon.endTrim + (ribbon.freeEnd ? 0f : embedCells * cell);
                var total = ribbon.length + startExtra + endExtra;
                var stations = Mathf.Max(1, Mathf.RoundToInt(total / (stationSpacingCells * cell)));
                var step = total / stations;

                // The part's faces, by where along the line they lie.
                var corners = ribbon.faceCorners;
                var faceCount = corners == null ? 0 : corners.Count / 3;
                var faceAlong = new float[faceCount];
                for (var f = 0; f < faceCount; f++)
                {
                    Locate(ribbon, (corners[3 * f] + corners[3 * f + 1] + corners[3 * f + 2]) / 3f, out _, out _, out var s);
                    faceAlong[f] = s * ribbon.length;
                }

                // Per station, the ring in the frame's coordinates (across, up) about the centre.
                var ringXY = new List<Vector2[]>();
                var frames = new List<(Vector3 c, Vector3 across, Vector3 up)>();
                var measured = new List<bool>();
                for (var i = 0; i <= stations; i++)
                {
                    var along = -startExtra + step * i;
                    Station(ribbon, along, out var c, out var tangent, out var up, out var across, out var w, out var th);
                    frames.Add((c, across, up));
                    var xy = Section(corners, faceAlong, along, 0.75f * step + 0.25f * w, c, tangent, across, up, k, w);
                    measured.Add(xy != null);
                    ringXY.Add(xy ?? Box(k, w, th));
                }
                // Stations with no section (past the part's ends, or a gap) take the nearest measured one's shape.
                for (var i = 0; i <= stations; i++)
                {
                    if (measured[i]) continue;
                    for (var d = 1; d <= stations; d++)
                    {
                        if (i - d >= 0 && measured[i - d]) { ringXY[i] = ringXY[i - d]; break; }
                        if (i + d <= stations && measured[i + d]) { ringXY[i] = ringXY[i + d]; break; }
                    }
                }

                var rings = new List<int>();
                for (var i = 0; i <= stations; i++)
                {
                    var (c, across, up) = frames[i];
                    rings.Add(outPositions.Count);
                    foreach (var q in ringXY[i]) outPositions.Add(c + across * q.x + up * q.y);
                }
                var firstFace = outFaces.Count;
                for (var i = 0; i < stations; i++)
                    for (var j = 0; j < ringSize; j++)
                    {
                        var j1 = (j + 1) % ringSize;
                        outFaces.Add(new[] { rings[i] + j, rings[i] + j1, rings[i + 1] + j1, rings[i + 1] + j });
                    }
                // Outward, with the source's sense of out: the first broad face against its up.
                if (outFaces.Count > firstFace)
                {
                    var f = outFaces[firstFace];
                    var n = Vector3.Cross(outPositions[f[1]] - outPositions[f[0]], outPositions[f[3]] - outPositions[f[0]]);
                    if (Vector3.Dot(n, frames[0].up) * outward < 0f)
                        for (var x = firstFace; x < outFaces.Count; x++) Array.Reverse(outFaces[x]);
                }
                void Cap(int ring, int other)
                {
                    var capFirst = outFaces.Count;
                    for (var j = 0; j < k; j++)
                        outFaces.Add(new[] { ring + j, ring + j + 1, ring + k + 1 + (k - j - 1), ring + k + 1 + (k - j) });
                    // Facing away from the tube.
                    var c = Vector3.zero;
                    var c2 = Vector3.zero;
                    for (var j = 0; j < ringSize; j++) { c += outPositions[ring + j]; c2 += outPositions[other + j]; }
                    var f = outFaces[capFirst];
                    var n = Vector3.Cross(outPositions[f[1]] - outPositions[f[0]], outPositions[f[3]] - outPositions[f[0]]);
                    if (Vector3.Dot(n, c - c2) * outward < 0f)
                        for (var x = capFirst; x < outFaces.Count; x++) Array.Reverse(outFaces[x]);
                }
                if (ribbon.freeStart && rings.Count > 1) Cap(rings[0], rings[1]);
                if (ribbon.freeEnd && rings.Count > 1) Cap(rings[rings.Count - 1], rings[rings.Count - 2]);
            }
            positions = outPositions.ToArray();
            faces = outFaces;
        }

        // A plain rectangle ring, w wide and th through: top left to right, then bottom right to left.
        static Vector2[] Box(int k, float w, float th)
        {
            var r = new Vector2[2 * k + 2];
            for (var j = 0; j <= k; j++) r[j] = new Vector2(-0.5f * w + w * j / k, 0.5f * th);
            for (var j = 0; j <= k; j++) r[k + 1 + j] = new Vector2(0.5f * w - w * j / k, -0.5f * th);
            return r;
        }

        // The ring at one station from the part's own section, or null when the plane meets too
        // little of it. Points of the section within reach along the line of the station only.
        static Vector2[] Section(List<Vector3> corners, float[] faceAlong, float along, float reach,
            Vector3 c, Vector3 tangent, Vector3 across, Vector3 up, int k, float w)
        {
            if (corners == null) return null;
            var pts = new List<Vector2>();
            void Cut(Vector3 a, Vector3 b, float da, float db)
            {
                var p = a + (b - a) * (da / (da - db));
                pts.Add(new Vector2(Vector3.Dot(p - c, across), Vector3.Dot(p - c, up)));
            }
            for (var f = 0; f < faceAlong.Length; f++)
            {
                if (Mathf.Abs(faceAlong[f] - along) > reach) continue;
                Vector3 a = corners[3 * f], b = corners[3 * f + 1], d = corners[3 * f + 2];
                float sa = Vector3.Dot(a - c, tangent), sb = Vector3.Dot(b - c, tangent), sd = Vector3.Dot(d - c, tangent);
                if ((sa > 0) != (sb > 0)) Cut(a, b, sa, sb);
                if ((sb > 0) != (sd > 0)) Cut(b, d, sb, sd);
                if ((sd > 0) != (sa > 0)) Cut(d, a, sd, sa);
            }
            if (pts.Count < 8) return null;
            // Points far off the line belong to another stretch of the part the plane also cuts.
            var limit = 1.25f * w;
            pts.RemoveAll(p => p.magnitude > limit);
            if (pts.Count < 8) return null;
            Vector2 Extreme(Vector2 dir)
            {
                var best = pts[0];
                var bestDot = float.NegativeInfinity;
                foreach (var p in pts)
                {
                    var d = Vector2.Dot(p, dir);
                    if (d > bestDot) { bestDot = d; best = p; }
                }
                return best;
            }
            // Diagonals weighted towards across, so a flat band's corners land at its rims.
            var topLeft = Extreme(new Vector2(-1f, 0.5f));
            var topRight = Extreme(new Vector2(1f, 0.5f));
            var bottomRight = Extreme(new Vector2(1f, -0.5f));
            var bottomLeft = Extreme(new Vector2(-1f, -0.5f));
            var r = new Vector2[2 * k + 2];
            for (var j = 0; j <= k; j++)
            {
                var t = j / (float)k;
                var x = Mathf.Lerp(topLeft.x, topRight.x, t);
                // In between, the top of the section at that x.
                r[j] = j == 0 ? topLeft : j == k ? topRight : new Vector2(x, Top(pts, x, 1f, Mathf.Lerp(topLeft.y, topRight.y, t)));
                var xb = Mathf.Lerp(bottomRight.x, bottomLeft.x, t);
                r[k + 1 + j] = j == 0 ? bottomRight : j == k ? bottomLeft : new Vector2(xb, Top(pts, xb, -1f, Mathf.Lerp(bottomRight.y, bottomLeft.y, t)));
            }
            return r;
        }

        // The section's highest (side +1) or lowest (side -1) point near across-coordinate x.
        static float Top(List<Vector2> pts, float x, float side, float fallback)
        {
            var best = float.NegativeInfinity;
            var bestY = fallback;
            var window = float.PositiveInfinity;
            foreach (var p in pts) window = Mathf.Min(window, Mathf.Abs(p.x - x));
            window = Mathf.Max(window * 2f, 1e-6f);
            foreach (var p in pts)
            {
                if (Mathf.Abs(p.x - x) > window) continue;
                if (p.y * side > best) { best = p.y * side; bestY = p.y; }
            }
            return bestY;
        }

        // The frame at a distance along the centre line (extrapolated off its ends).
        static void Station(Ribbon ribbon, float along, out Vector3 c, out Vector3 tangent, out Vector3 up,
            out Vector3 across, out float width, out float thickness)
        {
            var line = ribbon.centre;
            var acc = 0f;
            var k = 0;
            var f = 0f;
            if (along <= 0f)
            {
                var l0 = Vector3.Distance(line[0], line[1]);
                f = l0 > 0f ? along / l0 : 0f;
            }
            else
            {
                for (k = 0; k + 1 < line.Length; k++)
                {
                    var l = Vector3.Distance(line[k], line[k + 1]);
                    if (acc + l >= along || k + 2 == line.Length)
                    {
                        f = l > 0f ? (along - acc) / l : 0f;
                        break;
                    }
                    acc += l;
                }
            }
            Frame(ribbon, k, f, out c, out tangent, out up, out across, out width, out thickness);
        }

        // ------------------------------------------------------------------
        sealed class MinHeap
        {
            int[] m_Items;
            float[] m_Keys;
            public int Count { get; private set; }

            public MinHeap(int capacity)
            {
                m_Items = new int[Math.Max(4, capacity)];
                m_Keys = new float[m_Items.Length];
            }

            public void Push(int item, float key)
            {
                if (Count == m_Items.Length)
                {
                    Array.Resize(ref m_Items, Count * 2);
                    Array.Resize(ref m_Keys, Count * 2);
                }
                var i = Count++;
                while (i > 0)
                {
                    var p = (i - 1) / 2;
                    if (m_Keys[p] <= key) break;
                    m_Items[i] = m_Items[p]; m_Keys[i] = m_Keys[p];
                    i = p;
                }
                m_Items[i] = item; m_Keys[i] = key;
            }

            public void Pop(out int item, out float key)
            {
                item = m_Items[0]; key = m_Keys[0];
                var lastItem = m_Items[--Count];
                var lastKey = m_Keys[Count];
                var i = 0;
                while (true)
                {
                    var l = 2 * i + 1;
                    if (l >= Count) break;
                    var r = l + 1;
                    var c = r < Count && m_Keys[r] < m_Keys[l] ? r : l;
                    if (m_Keys[c] >= lastKey) break;
                    m_Items[i] = m_Items[c]; m_Keys[i] = m_Keys[c];
                    i = c;
                }
                m_Items[i] = lastItem; m_Keys[i] = lastKey;
            }
        }
    }
}
