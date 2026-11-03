// Flat walls found and made flat before the quads are built.
//
// A building is planes: walls, roof slopes, window glass, the sides of the window recesses. Its
// source carries relief on them a remesh has no use for -- the white house's siding is boards a
// twentieth of a cell proud, each tilted six degrees, with a step under every one -- and that
// relief is what the quads were spent on: the error-driven simplification held every board's step
// as a sharp edge, and the lattice followed the boards' tilt into walls that wave. The relief
// belongs to the normal map, which is baked from the source as it was.
//
// So the source's planar regions are found (grown over normals smoothed across a board or two,
// held to a plane within a fraction of a cell), and every vertex in one is put on its plane: on
// two, on the line where they meet; on three or more, at their corner. Detail standing off a
// plane by more than that fraction -- a window frame, a recess, a sill -- is a region of its own
// or none, and is left where it is.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    public static class PlanarRegions
    {
        /// <summary>Flatten planar regions before the error-driven quads of a dense mesh (false: built from the source as it is).</summary>
        public static bool enabled = true;

        /// <summary>Normals are smoothed over about this many cells before regions are grown (a board's tilt averages out).</summary>
        public static float smoothCells = 0.5f;

        /// <summary>A triangle joins a region when its smoothed normal is within this angle of the region's, degrees...</summary>
        public static float growAngle = 10f;

        /// <summary>...and its centre within this many cells of the region's plane (the most relief flattened away).</summary>
        public static float distanceCells = 0.08f;

        /// <summary>A growing region's plane is fitted to its points once it covers this many square cells (before: its mean normal).</summary>
        public static float fitAreaCells = 1f;

        /// <summary>Regions smaller than this many square cells are left as they are.</summary>
        public static float minAreaCells = 2f;

        /// <summary>
        /// Flat faces the first pass left (too small for its smoothing), grown on their own normals,
        /// are kept from this many square cells (0: none).
        /// </summary>
        public static float detailMinAreaCells = 0f;

        /// <summary>No vertex is moved further than this many cells (where its planes' corner lies further off, it is left).</summary>
        public static float maxMoveCells = 0.25f;

        /// <summary>Planes meeting at under this angle, degrees, are one plane for a vertex on both (no line where they meet).</summary>
        public static float lineMinAngle = 15f;

        /// <summary>Rounds of taking in unclaimed neighbours that lie on a region's plane (a board's step).</summary>
        public static int absorbRounds = 40;

        /// <summary>How far, degrees, a fillet point's normal may stray from the turn between its two planes (Flatten's fillet).</summary>
        public static float filletNormalDegrees = 20f;

        /// <summary>Last run's fillet points moved onto their panels' meeting line (diagnostic).</summary>
        public static int lastFilletPoints;

        /// <summary>The flattening is used when its regions cover at least this share of the area.</summary>
        public static float minShare = 0.5f;

        public sealed class Result
        {
            /// <summary>The source's positions, flattened (same indexing as the input).</summary>
            public Vector3[] positions;
            /// <summary>Each input triangle's region, or -1.</summary>
            public int[] triangleRegion;
            /// <summary>Each region's plane: unit normal and offset (normal · point = offset).</summary>
            public Vector3[] normals;
            public float[] offsets;
            public double[] areas;
            public int regions, moved, lines, corners, refused, smoothSweeps;
            public float share;
            public long ms;

            public override string ToString() =>
                $"planar regions {regions} over {share:P0} of the area, {moved} vertices moved ({lines} on lines, {corners} at corners, {refused} refused), {smoothSweeps} sweeps, {ms} ms";
        }

        public static Result Flatten(Vector3[] positions, int[] triangles, float cell) =>
            Flatten(positions, triangles, cell, minAreaCells, 0f);

        /// <summary>
        /// As above, keeping regions from <paramref name="minArea"/> square cells, and with
        /// <paramref name="filletCells"/> above 0 two regions meeting through a narrow fillet made to
        /// meet at the line where their planes cross: each point of the fillet within that many
        /// cells of both planes, whose normal turns between theirs, is moved onto the nearer plane
        /// and on toward the line (all the way at the fillet's middle), and the fillet's triangles
        /// then lying on a plane join its region. A plate's edge rounded over a fraction of a cell
        /// is then a line between two regions instead of a strip between them that is neither.
        /// </summary>
        public static Result Flatten(Vector3[] positions, int[] triangles, float cell, float minArea, float filletCells)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = new Result { positions = positions };
            var m = triangles.Length / 3;
            if (m == 0 || !(cell > 0f)) return result;

            // Welded by position: seams split vertices the regions must not stop at.
            var weldMap = new Dictionary<Vector3, int>(positions.Length);
            var weld = new int[positions.Length];
            var welded = new List<Vector3>(positions.Length);
            for (var i = 0; i < positions.Length; i++)
            {
                if (!weldMap.TryGetValue(positions[i], out var w)) { w = welded.Count; weldMap[positions[i]] = w; welded.Add(positions[i]); }
                weld[i] = w;
            }
            var p = welded.ToArray();
            var n = p.Length;
            var t = new int[3 * m];
            for (var i = 0; i < t.Length; i++) t[i] = weld[triangles[i]];

            var faceNormal = new Vector3[m];
            var centre = new Vector3[m];
            var area = new double[m];
            double totalArea = 0, edgeSum = 0;
            for (var f = 0; f < m; f++)
            {
                Vector3 a = p[t[3 * f]], b = p[t[3 * f + 1]], c = p[t[3 * f + 2]];
                var cross = Vector3.Cross(b - a, c - a);
                var len = cross.magnitude;
                area[f] = 0.5 * len;
                totalArea += area[f];
                faceNormal[f] = len > 0f ? cross / len : Vector3.zero;
                centre[f] = (a + b + c) / 3f;
                edgeSum += (b - a).magnitude;
            }
            var meanEdge = (float)(edgeSum / m);

            // Vertex to triangles, and vertex neighbours, as compressed rows.
            var vtStart = new int[n + 1];
            for (var i = 0; i < t.Length; i++) vtStart[t[i] + 1]++;
            for (var v = 0; v < n; v++) vtStart[v + 1] += vtStart[v];
            var vt = new int[t.Length];
            var fill = (int[])vtStart.Clone();
            for (var i = 0; i < t.Length; i++) vt[fill[t[i]]++] = i / 3;

            // Smoothed normals: the area-weighted vertex normal, averaged with its neighbours'
            // over as many sweeps as reach the radius (a walk's reach grows as the root of its steps).
            var normal = new Vector3[n];
            var next = new Vector3[n];
            for (var v = 0; v < n; v++)
            {
                var s = Vector3.zero;
                for (var k = vtStart[v]; k < vtStart[v + 1]; k++) s += faceNormal[vt[k]] * (float)area[vt[k]];
                normal[v] = s;
            }
            var reach = smoothCells * cell / Mathf.Max(1e-12f, meanEdge);
            var sweeps = Mathf.Clamp(Mathf.RoundToInt(reach * reach), 0, 64);
            result.smoothSweeps = sweeps;
            for (var sweep = 0; sweep < sweeps; sweep++)
            {
                var from = normal;
                var to = next;
                Parallel.For(0, n, BackgroundScheduler.Options, v =>
                {
                    var s = from[v];
                    for (var k = vtStart[v]; k < vtStart[v + 1]; k++)
                    {
                        var f = vt[k];
                        // Each triangle's other corners (a neighbour seen twice weighs twice: near enough).
                        for (var j = 0; j < 3; j++)
                        {
                            var u = t[3 * f + j];
                            if (u != v) s += 0.5f * from[u];
                        }
                    }
                    var len = s.magnitude;
                    to[v] = len > 0f ? s / len : Vector3.zero;
                });
                normal = to;
                next = from;
            }
            for (var v = 0; v < n; v++) { var len = normal[v].magnitude; if (len > 0f) normal[v] /= len; }
            var smooth = new Vector3[m];
            var coherence = new float[m];
            for (var f = 0; f < m; f++)
            {
                var s = normal[t[3 * f]] + normal[t[3 * f + 1]] + normal[t[3 * f + 2]];
                var len = s.magnitude;
                smooth[f] = len > 0f ? s / len : faceNormal[f];
                coherence[f] = len;
            }

            // Triangle neighbours across edges with exactly two triangles.
            var keys = new long[3 * m];
            var owner = new int[3 * m];
            for (var f = 0; f < m; f++)
                for (var j = 0; j < 3; j++)
                {
                    keys[3 * f + j] = Extraction.EdgeKey.PackUndirected(t[3 * f + j], t[3 * f + (j + 1) % 3]);
                    owner[3 * f + j] = f;
                }
            Array.Sort(keys, owner);
            var neighbour = new int[3 * m];
            var count = new byte[m];
            for (var i = 0; i < neighbour.Length; i++) neighbour[i] = -1;
            for (var i = 0; i + 1 < keys.Length;)
            {
                var j = i + 1;
                while (j < keys.Length && keys[j] == keys[i]) j++;
                if (j - i == 2)
                {
                    int f0 = owner[i], f1 = owner[i + 1];
                    if (count[f0] < 3 && count[f1] < 3)
                    {
                        neighbour[3 * f0 + count[f0]++] = f1;
                        neighbour[3 * f1 + count[f1]++] = f0;
                    }
                }
                i = j;
            }

            // Grown from the most coherent triangles (flat all round) first.
            var order = new int[m];
            for (var f = 0; f < m; f++) order[f] = f;
            var negative = new float[m];
            for (var f = 0; f < m; f++) negative[f] = -coherence[f];
            Array.Sort(negative, order);
            var region = new int[m];
            for (var f = 0; f < m; f++) region[f] = -1;
            var cosGrow = Mathf.Cos(growAngle * Mathf.Deg2Rad);
            var tolerance = distanceCells * cell;
            var sums = new List<(double nx, double ny, double nz, double cx, double cy, double cz, double a)>();
            var queue = new Queue<int>();
            var minRegionArea = minArea * cell * cell;
            var fitArea = fitAreaCells * cell * cell;
            void Grow(Vector3[] normals)
            {
            foreach (var seed in order)
            {
                if (region[seed] >= 0 || area[seed] <= 0) continue;
                var r = sums.Count;
                // Sums about the seed's centre (precision): normals, centres and their products.
                var origin = centre[seed];
                double nx = 0, ny = 0, nz = 0, cx = 0, cy = 0, cz = 0, a = 0;
                double sxx = 0, sxy = 0, sxz = 0, syy = 0, syz = 0, szz = 0;
                var plane = normals[seed];
                var point = origin;
                var fittedAt = 0.0;
                void Add(int f)
                {
                    region[f] = r;
                    var w = area[f];
                    var d = centre[f] - origin;
                    nx += w * normals[f].x; ny += w * normals[f].y; nz += w * normals[f].z;
                    cx += w * d.x; cy += w * d.y; cz += w * d.z;
                    sxx += w * d.x * d.x; sxy += w * d.x * d.y; sxz += w * d.x * d.z;
                    syy += w * d.y * d.y; syz += w * d.y * d.z; szz += w * d.z * d.z;
                    a += w;
                    queue.Enqueue(f);
                }
                Add(seed);
                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    // The plane: the mean normal at first, then -- once the region spans a cell or so,
                    // past a board's tilt, which all lean one way and bias the normals -- the plane
                    // fitted to its centres, refitted as it grows by a quarter.
                    if (a >= fitArea && a >= 1.25 * fittedAt)
                    {
                        var mx = cx / a; var my = cy / a; var mz = cz / a;
                        var cov = new[] { sxx / a - mx * mx, sxy / a - mx * my, sxz / a - mx * mz,
                            syy / a - my * my, syz / a - my * mz, szz / a - mz * mz };
                        plane = SmallestAxis(cov, 0, new Vector3((float)nx, (float)ny, (float)nz));
                        point = origin + new Vector3((float)mx, (float)my, (float)mz);
                        fittedAt = a;
                    }
                    else if (fittedAt == 0.0)
                    {
                        var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                        plane = new Vector3((float)(nx / len), (float)(ny / len), (float)(nz / len));
                        point = origin + new Vector3((float)(cx / a), (float)(cy / a), (float)(cz / a));
                    }
                    for (var j = 0; j < 3; j++)
                    {
                        var g = neighbour[3 * f + j];
                        if (g < 0 || region[g] >= 0) continue;
                        if (Vector3.Dot(normals[g], plane) < cosGrow) continue;
                        if (Mathf.Abs(Vector3.Dot(centre[g] - point, plane)) > tolerance) continue;
                        Add(g);
                    }
                }
                sums.Add((nx, ny, nz, cx, cy, cz, a));
            }
            }
            Grow(smooth);

            // Each region's plane, fitted to its triangles (area-weighted centres and covariance).
            var regions = sums.Count;
            var planeNormal = new Vector3[regions];
            var planeOffset = new float[regions];
            var regionArea = new double[regions];
            void Fit()
            {
                regions = sums.Count;
                if (planeNormal.Length < regions)
                {
                    Array.Resize(ref planeNormal, regions);
                    Array.Resize(ref planeOffset, regions);
                }
                regionArea = new double[regions];
                var mean = new double[regions * 3];
                for (var f = 0; f < m; f++)
                {
                    var r = region[f];
                    if (r < 0) continue;
                    var w = area[f];
                    regionArea[r] += w;
                    mean[3 * r] += w * centre[f].x; mean[3 * r + 1] += w * centre[f].y; mean[3 * r + 2] += w * centre[f].z;
                }
                var cov = new double[regions * 6];
                for (var f = 0; f < m; f++)
                {
                    var r = region[f];
                    if (r < 0 || !(regionArea[r] > 0)) continue;
                    var w = area[f];
                    double dx = centre[f].x - mean[3 * r] / regionArea[r], dy = centre[f].y - mean[3 * r + 1] / regionArea[r],
                        dz = centre[f].z - mean[3 * r + 2] / regionArea[r];
                    cov[6 * r] += w * dx * dx; cov[6 * r + 1] += w * dx * dy; cov[6 * r + 2] += w * dx * dz;
                    cov[6 * r + 3] += w * dy * dy; cov[6 * r + 4] += w * dy * dz; cov[6 * r + 5] += w * dz * dz;
                }
                for (var r = 0; r < regions; r++)
                {
                    if (!(regionArea[r] > 0)) continue;
                    var s = sums[r];
                    var guess = new Vector3((float)s.nx, (float)s.ny, (float)s.nz);
                    var nr = SmallestAxis(cov, 6 * r, guess);
                    var centroid = new Vector3((float)(mean[3 * r] / regionArea[r]), (float)(mean[3 * r + 1] / regionArea[r]),
                        (float)(mean[3 * r + 2] / regionArea[r]));
                    planeNormal[r] = nr;
                    planeOffset[r] = Vector3.Dot(nr, centroid);
                }
            }
            Fit();
            // Small regions are not walls.
            for (var f = 0; f < m; f++)
                if (region[f] >= 0 && regionArea[region[f]] < minRegionArea) region[f] = -1;

            // Unclaimed triangles lying on a neighbouring region's plane (corners within the
            // tolerance: a board's step, the band the smoothing bent at a wall's corner) join it.
            var frontier = new List<int>();
            for (var f = 0; f < m; f++)
                if (region[f] < 0)
                    for (var j = 0; j < 3; j++)
                    {
                        var g = neighbour[3 * f + j];
                        if (g >= 0 && region[g] >= 0) { frontier.Add(f); break; }
                    }
            for (var round = 0; round < absorbRounds && frontier.Count > 0; round++)
            {
                var joined = new List<(int f, int r)>();
                foreach (var f in frontier)
                {
                    if (region[f] >= 0) continue;
                    var best = -1;
                    var bestOff = float.MaxValue;
                    for (var j = 0; j < 3; j++)
                    {
                        var g = neighbour[3 * f + j];
                        if (g < 0 || region[g] < 0) continue;
                        var r = region[g];
                        var off = 0f;
                        for (var k = 0; k < 3; k++)
                            off = Mathf.Max(off, Mathf.Abs(Vector3.Dot(planeNormal[r], p[t[3 * f + k]]) - planeOffset[r]));
                        if (off < bestOff) { bestOff = off; best = r; }
                    }
                    if (best >= 0 && bestOff <= tolerance) joined.Add((f, best));
                }
                var nextFrontier = new List<int>();
                foreach (var (f, r) in joined)
                {
                    if (region[f] >= 0) continue;
                    region[f] = r;
                    for (var j = 0; j < 3; j++)
                    {
                        var g = neighbour[3 * f + j];
                        if (g >= 0 && region[g] < 0) nextFrontier.Add(g);
                    }
                }
                frontier = nextFrontier;
            }

            lastFilletPoints = 0;
            if (filletCells > 0f)
                lastFilletPoints = SharpenFillets(p, t, region, planeNormal, planeOffset, vtStart, vt, neighbour,
                    faceNormal, area, filletCells * cell, Mathf.Cos(lineMinAngle * Mathf.Deg2Rad), tolerance);

            // Small flat faces the smoothing blurred -- a window's casing, sash and panes, the sides
            // of its recess, a sill -- grown again on the triangles' own normals, and kept down to
            // a far smaller size: built flat, a window is boxes.
            if (detailMinAreaCells > 0f)
            {
                var firstDetail = sums.Count;
                Grow(faceNormal);
                Fit();
                var detailMinArea = detailMinAreaCells * cell * cell;
                for (var f = 0; f < m; f++)
                    if (region[f] >= firstDetail && regionArea[region[f]] < detailMinArea) region[f] = -1;
            }

            // Renumbered, the kept regions only.
            var renumber = new int[regions];
            for (var r = 0; r < regions; r++) renumber[r] = -1;
            var kept = 0;
            for (var f = 0; f < m; f++)
            {
                var r = region[f];
                if (r < 0) continue;
                if (renumber[r] < 0) renumber[r] = kept++;
                region[f] = renumber[r];
            }
            var keptNormal = new Vector3[kept];
            var keptOffset = new float[kept];
            for (var r = 0; r < regions; r++)
                if (renumber[r] >= 0) { keptNormal[renumber[r]] = planeNormal[r]; keptOffset[renumber[r]] = planeOffset[r]; }
            var keptArea = new double[kept];
            double covered = 0;
            for (var f = 0; f < m; f++)
                if (region[f] >= 0) { keptArea[region[f]] += area[f]; covered += area[f]; }
            result.regions = kept;
            result.normals = keptNormal;
            result.offsets = keptOffset;
            result.areas = keptArea;
            result.share = totalArea > 0 ? (float)(covered / totalArea) : 0f;

            // Every vertex onto its planes: one plane, a line, or a corner.
            var flat = (Vector3[])p.Clone();
            var maxMove = maxMoveCells * cell;
            var cosLine = Mathf.Cos(lineMinAngle * Mathf.Deg2Rad);
            int moved = 0, lines = 0, corners = 0, refused = 0;
            var mine = new List<int>(8);
            for (var v = 0; v < n; v++)
            {
                mine.Clear();
                for (var k = vtStart[v]; k < vtStart[v + 1]; k++)
                {
                    var r = region[vt[k]];
                    if (r >= 0 && !mine.Contains(r)) mine.Add(r);
                }
                if (mine.Count == 0) continue;
                // Planes alike in direction are one: the largest of them stands for them.
                mine.Sort((x, y) => keptArea[y].CompareTo(keptArea[x]));
                for (var i = 1; i < mine.Count; i++)
                    for (var j = 0; j < i; j++)
                        if (Mathf.Abs(Vector3.Dot(keptNormal[mine[i]], keptNormal[mine[j]])) > cosLine)
                        {
                            mine.RemoveAt(i--);
                            break;
                        }
                var q = p[v];
                Vector3 target;
                if (mine.Count == 1)
                {
                    var r = mine[0];
                    target = q - keptNormal[r] * (Vector3.Dot(keptNormal[r], q) - keptOffset[r]);
                }
                else
                {
                    // Least squares over the planes, held lightly to where the vertex is (a line
                    // of two planes leaves one direction free).
                    double a00 = 1e-6, a01 = 0, a02 = 0, a11 = 1e-6, a12 = 0, a22 = 1e-6;
                    double b0 = 1e-6 * q.x, b1 = 1e-6 * q.y, b2 = 1e-6 * q.z;
                    foreach (var r in mine)
                    {
                        var nr = keptNormal[r];
                        double d = keptOffset[r];
                        a00 += nr.x * nr.x; a01 += nr.x * nr.y; a02 += nr.x * nr.z;
                        a11 += nr.y * nr.y; a12 += nr.y * nr.z; a22 += nr.z * nr.z;
                        b0 += nr.x * d; b1 += nr.y * d; b2 += nr.z * d;
                    }
                    if (mine.Count == 2)
                    {
                        // The point of the line nearest q: q moved within the span of the two normals.
                        Vector3 n0 = keptNormal[mine[0]], n1 = keptNormal[mine[1]];
                        double r0 = keptOffset[mine[0]] - Vector3.Dot(n0, q), r1 = keptOffset[mine[1]] - Vector3.Dot(n1, q);
                        double g = Vector3.Dot(n0, n1), det = 1 - g * g;
                        var s0 = (r0 - g * r1) / det;
                        var s1 = (r1 - g * r0) / det;
                        target = q + (float)s0 * n0 + (float)s1 * n1;
                        lines++;
                    }
                    else
                    {
                        if (!Solve3(a00, a01, a02, a11, a12, a22, b0, b1, b2, out target)) target = q;
                        corners++;
                    }
                }
                if ((target - q).magnitude > maxMove)
                {
                    // Too far for its corner: onto its largest plane alone, if that is near.
                    var r = mine[0];
                    var alone = q - keptNormal[r] * (Vector3.Dot(keptNormal[r], q) - keptOffset[r]);
                    if ((alone - q).magnitude > maxMove) { refused++; continue; }
                    target = alone;
                }
                flat[v] = target;
                moved++;
            }
            result.moved = moved;
            result.lines = lines;
            result.corners = corners;
            result.refused = refused;

            var output = new Vector3[positions.Length];
            for (var i = 0; i < positions.Length; i++) output[i] = flat[weld[i]];
            result.positions = output;
            result.triangleRegion = region;
            result.ms = watch.ElapsedMilliseconds;
            return result;
        }

        // The point of the line where two planes meet nearest q.
        static Vector3 LinePoint(Vector3 q, Vector3 n0, float d0, Vector3 n1, float d1)
        {
            double r0 = d0 - Vector3.Dot(n0, q), r1 = d1 - Vector3.Dot(n1, q);
            double g = Vector3.Dot(n0, n1), det = 1 - g * g;
            if (!(det > 1e-9)) return q;
            var s0 = (r0 - g * r1) / det;
            var s1 = (r1 - g * r0) / det;
            return q + (float)s0 * n0 + (float)s1 * n1;
        }

        // Fillets between two regions closed onto the line their planes meet at (see Flatten):
        // `p` moved, and the fillet's triangles lying on a plane after given to its region.
        // Returns the points moved.
        static int SharpenFillets(Vector3[] p, int[] t, int[] region, Vector3[] planeNormal, float[] planeOffset,
            int[] vtStart, int[] vt, int[] neighbour, Vector3[] faceNormal, double[] area, float reach, float cosLine,
            float tolerance)
        {
            var n = p.Length;
            var target = new Vector3[n];
            var has = new bool[n];
            var reach2 = reach * reach;
            var straySin = Mathf.Sin(filletNormalDegrees * Mathf.Deg2Rad);
            Parallel.For(0, n, BackgroundScheduler.Options, () => (new HashSet<int>(), new Stack<int>(), new List<int>()),
                (v, state, local) =>
                {
                    var (seen, stack, found) = local;
                    seen.Clear(); stack.Clear(); found.Clear();
                    var q = p[v];
                    var normal = Vector3.zero;
                    for (var k = vtStart[v]; k < vtStart[v + 1]; k++)
                    {
                        var f = vt[k];
                        if (region[f] >= 0) return local;
                        normal += faceNormal[f] * (float)area[f];
                        if (seen.Add(f)) stack.Push(f);
                    }
                    if (stack.Count == 0) return local;
                    // The regions met within reach, across fillet triangles.
                    while (stack.Count > 0)
                    {
                        var f = stack.Pop();
                        for (var j = 0; j < 3; j++)
                        {
                            var g = neighbour[3 * f + j];
                            if (g < 0 || !seen.Add(g)) continue;
                            if (region[g] >= 0) { if (!found.Contains(region[g])) found.Add(region[g]); continue; }
                            var near = false;
                            for (var k = 0; k < 3 && !near; k++) near = (p[t[3 * g + k]] - q).sqrMagnitude <= reach2;
                            if (near) stack.Push(g);
                        }
                    }
                    if (found.Count < 2) return local;
                    // The two planes it lies nearest, not alike in direction.
                    int bestA = -1, bestB = -1;
                    var bestOff = float.MaxValue;
                    for (var i = 0; i < found.Count; i++)
                        for (var j = i + 1; j < found.Count; j++)
                        {
                            int ra = found[i], rb = found[j];
                            if (Mathf.Abs(Vector3.Dot(planeNormal[ra], planeNormal[rb])) > cosLine) continue;
                            var off = Mathf.Max(Mathf.Abs(Vector3.Dot(planeNormal[ra], q) - planeOffset[ra]),
                                Mathf.Abs(Vector3.Dot(planeNormal[rb], q) - planeOffset[rb]));
                            if (off < bestOff) { bestOff = off; bestA = ra; bestB = rb; }
                        }
                    if (bestA < 0 || bestOff > reach) return local;
                    Vector3 nA = planeNormal[bestA], nB = planeNormal[bestB];
                    // A fillet's normal turns from one plane's to the other's: it lies near their span.
                    var len = normal.magnitude;
                    if (!(len > 0f)) return local;
                    normal /= len;
                    var axis = Vector3.Cross(nA, nB).normalized;
                    if (Mathf.Abs(Vector3.Dot(normal, axis)) > straySin) return local;
                    var sA = Vector3.Dot(nA, q) - planeOffset[bestA];
                    var sB = Vector3.Dot(nB, q) - planeOffset[bestB];
                    float a = Mathf.Abs(sA), b = Mathf.Abs(sB);
                    Vector3 onPlane;
                    float toLine;
                    if (a <= b) { onPlane = q - sA * nA; toLine = b > 0f ? a / b : 1f; }
                    else { onPlane = q - sB * nB; toLine = a > 0f ? b / a : 1f; }
                    var line = LinePoint(onPlane, nA, planeOffset[bestA], nB, planeOffset[bestB]);
                    var moved = onPlane + (line - onPlane) * toLine;
                    if ((moved - q).sqrMagnitude > reach2) return local;
                    target[v] = moved;
                    has[v] = true;
                    return local;
                }, _ => { });
            var count = 0;
            for (var v = 0; v < n; v++) if (has[v]) { p[v] = target[v]; count++; }
            if (count == 0) return 0;

            // Fillet triangles now on a neighbouring region's plane join it, outward from the regions.
            var m = region.Length;
            var on = 0.25f * tolerance;
            for (var round = 0; round < 64; round++)
            {
                var joined = new List<(int f, int r)>();
                for (var f = 0; f < m; f++)
                {
                    if (region[f] >= 0) continue;
                    for (var j = 0; j < 3; j++)
                    {
                        var g = neighbour[3 * f + j];
                        if (g < 0 || region[g] < 0) continue;
                        var r = region[g];
                        var flat = true;
                        for (var k = 0; k < 3 && flat; k++)
                            flat = Mathf.Abs(Vector3.Dot(planeNormal[r], p[t[3 * f + k]]) - planeOffset[r]) <= on;
                        if (flat) { joined.Add((f, r)); break; }
                    }
                }
                if (joined.Count == 0) break;
                foreach (var (f, r) in joined) region[f] = r;
            }
            return count;
        }

        // The eigenvector of the smallest eigenvalue of a symmetric 3x3 (packed xx xy xz yy yz zz),
        // by inverse iteration from the guess; the guess's direction when the matrix is degenerate.
        static Vector3 SmallestAxis(double[] c, int o, Vector3 guess)
        {
            double xx = c[o], xy = c[o + 1], xz = c[o + 2], yy = c[o + 3], yz = c[o + 4], zz = c[o + 5];
            var trace = xx + yy + zz;
            // Not Vector3.normalized: it is zero under a length of 1e-5, and the area-weighted normals of
            // a mesh stored small (the white house, its triangles 3e-5 across) sum to about 1e-8: every
            // region stopped growing at one square cell, where this plane takes over.
            var guessLength = guess.magnitude;
            var g = guessLength > 0f ? guess / guessLength : Vector3.up;
            if (!(trace > 0)) return g;
            // Shifted slightly so the system stays solvable when the region is exactly flat.
            var shift = 1e-9 * trace;
            double vx = g.x, vy = g.y, vz = g.z;
            for (var it = 0; it < 8; it++)
            {
                if (!Solve3(xx + shift, xy, xz, yy + shift, yz, zz + shift, vx, vy, vz, out var w)) break;
                var len = w.magnitude;
                if (!(len > 0f) || float.IsNaN(len)) break;
                vx = w.x / len; vy = w.y / len; vz = w.z / len;
            }
            var result = new Vector3((float)vx, (float)vy, (float)vz);
            if (Vector3.Dot(result, g) < 0f) result = -result;
            return result.sqrMagnitude > 0.5f ? result : g;
        }

        static bool Solve3(double a00, double a01, double a02, double a11, double a12, double a22,
            double b0, double b1, double b2, out Vector3 x)
        {
            var c00 = a11 * a22 - a12 * a12;
            var c01 = a02 * a12 - a01 * a22;
            var c02 = a01 * a12 - a02 * a11;
            var det = a00 * c00 + a01 * c01 + a02 * c02;
            x = Vector3.zero;
            if (!(Math.Abs(det) > 1e-300)) return false;
            var c11 = a00 * a22 - a02 * a02;
            var c12 = a01 * a02 - a00 * a12;
            var c22 = a00 * a11 - a01 * a01;
            x = new Vector3((float)((c00 * b0 + c01 * b1 + c02 * b2) / det),
                (float)((c01 * b0 + c11 * b1 + c12 * b2) / det),
                (float)((c02 * b0 + c12 * b1 + c22 * b2) / det));
            return !(float.IsNaN(x.x) || float.IsNaN(x.y) || float.IsNaN(x.z));
        }
    }
}
