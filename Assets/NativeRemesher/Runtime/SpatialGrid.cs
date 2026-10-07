// Uniform grid over a triangle soup, for nearest-surface queries.
//
// Chamfer and Hausdorff distance both need "how far is this point from that surface", asked a few
// hundred thousand times per benchmark row. A brute-force scan is O(samples x triangles), which on
// a 320k-triangle corpus mesh is not a benchmark, it is a lunch break. A uniform grid is the right
// structure here rather than a BVH: remeshing inputs are surfaces of roughly uniform triangle
// density, which is exactly the case where a grid matches a tree and is far simpler to get right.
//
// Moved from the Benchmark assembly into Runtime: attribute transfer needs the same
// nearest-surface query the metrics do, and Benchmark is Editor-only. NativeRemesher.Benchmark is
// nested inside NativeRemesher, so every existing call site resolves it unqualified and unchanged.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public sealed class SpatialGrid
    {
        readonly Vector3[] m_Vertices;
        readonly int[] m_Triangles;

        readonly Vector3 m_Origin;
        readonly float m_CellSize;
        readonly float m_InvCellSize;
        readonly int m_DimX, m_DimY, m_DimZ;

        // CSR over cells: triangles in cell c are m_CellItems[m_CellStart[c] .. m_CellStart[c+1]-1].
        readonly int[] m_CellStart;
        readonly int[] m_CellItems;

        public int triangleCount => m_Triangles.Length / 3;
        public int cellCount => m_DimX * m_DimY * m_DimZ;
        public float cellSize => m_CellSize;

        /// <summary>
        /// Size cells from the mean triangle area as well as the box volume; see the constructor.
        /// Off is the original volume-only rule.
        /// </summary>
        public static bool areaSizedCells = true;

        public SpatialGrid(Vector3[] vertices, int[] triangles)
        {
            m_Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
            m_Triangles = triangles ?? throw new ArgumentNullException(nameof(triangles));

            var triCount = triangles.Length / 3;
            if (triCount == 0)
            {
                // Degenerate but legal: an empty grid answers every query with +infinity rather
                // than throwing, so a metric run over a broken mesh still produces a row.
                m_Origin = Vector3.zero;
                m_CellSize = 1f;
                m_InvCellSize = 1f;
                m_DimX = m_DimY = m_DimZ = 1;
                m_CellStart = new int[2];
                m_CellItems = Array.Empty<int>();
                return;
            }

            // --- bounds ---
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (var i = 0; i < triangles.Length; i++)
            {
                var p = vertices[triangles[i]];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            var size = max - min;

            // Target roughly one triangle per cell. Deriving the cell size from the volume of the
            // *bounding box* would make cells far too large for a thin surface, so use the cube
            // root of (box volume / triangle count) but floor it against the box's smallest
            // non-degenerate extent so a flat mesh does not collapse to a single cell layer.
            var volume = Mathf.Max(size.x, 1e-6f) * Mathf.Max(size.y, 1e-6f) * Mathf.Max(size.z, 1e-6f);
            var cell = Mathf.Pow(volume / triCount, 1f / 3f);

            // A SURFACE fills its box only when the box is a slab. For a body standing in a tall
            // box the volume rule makes cells several triangles wide in every direction, and each
            // query then tests thousands: measured on the body scan, surface relaxation spent
            // 15.5 s of a 21.5 s extraction projecting 10k vertices onto a 313k-triangle grid. So
            // the cell is also capped at about two triangle edges, from the mean triangle area --
            // the cap below on the total cell count still stops a sparse box from exhausting
            // memory, by doubling the cell back up.
            if (areaSizedCells)
            {
                double area = 0;
                for (var t = 0; t < triCount; t++)
                {
                    var a = vertices[triangles[3 * t]];
                    area += Vector3.Cross(vertices[triangles[3 * t + 1]] - a,
                        vertices[triangles[3 * t + 2]] - a).magnitude * 0.5;
                }
                var surfaceCell = 2f * Mathf.Sqrt((float)(area / triCount));
                if (surfaceCell > 0f && surfaceCell < cell) cell = surfaceCell;
            }

            var diagonal = size.magnitude;
            if (!(cell > 0f) || float.IsNaN(cell))
                cell = diagonal / 32f;
            cell = Mathf.Clamp(cell, Mathf.Max(diagonal * 1e-4f, 1e-7f), Mathf.Max(diagonal, 1e-6f));

            m_CellSize = cell;
            m_InvCellSize = 1f / cell;

            // Pad by one cell so a query point just outside the surface still has a home cell.
            m_Origin = min - Vector3.one * cell;
            var padded = size + Vector3.one * (2f * cell);

            m_DimX = Mathf.Max(1, Mathf.CeilToInt(padded.x * m_InvCellSize));
            m_DimY = Mathf.Max(1, Mathf.CeilToInt(padded.y * m_InvCellSize));
            m_DimZ = Mathf.Max(1, Mathf.CeilToInt(padded.z * m_InvCellSize));

            // Guard against a pathological aspect ratio blowing up memory.
            const long k_MaxCells = 16_000_000;
            while ((long)m_DimX * m_DimY * m_DimZ > k_MaxCells)
            {
                m_CellSize *= 2f;
                m_InvCellSize = 1f / m_CellSize;
                m_DimX = Mathf.Max(1, (m_DimX + 1) / 2);
                m_DimY = Mathf.Max(1, (m_DimY + 1) / 2);
                m_DimZ = Mathf.Max(1, (m_DimZ + 1) / 2);
            }

            // --- two-pass CSR fill: count, prefix sum, place ---
            var cells = m_DimX * m_DimY * m_DimZ;
            var counts = new int[cells];

            for (var t = 0; t < triCount; t++)
            {
                ForEachOverlappedCell(t, c => counts[c]++);
            }

            m_CellStart = new int[cells + 1];
            var total = 0;
            for (var c = 0; c < cells; c++)
            {
                m_CellStart[c] = total;
                total += counts[c];
            }
            m_CellStart[cells] = total;

            m_CellItems = new int[total];
            var cursor = new int[cells];
            for (var t = 0; t < triCount; t++)
            {
                var tri = t;
                ForEachOverlappedCell(tri, c => m_CellItems[m_CellStart[c] + cursor[c]++] = tri);
            }
        }

        void ForEachOverlappedCell(int triangle, Action<int> action)
        {
            var a = m_Vertices[m_Triangles[triangle * 3]];
            var b = m_Vertices[m_Triangles[triangle * 3 + 1]];
            var c = m_Vertices[m_Triangles[triangle * 3 + 2]];

            var lo = Vector3.Min(Vector3.Min(a, b), c);
            var hi = Vector3.Max(Vector3.Max(a, b), c);

            CellCoord(lo, out var x0, out var y0, out var z0);
            CellCoord(hi, out var x1, out var y1, out var z1);

            for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                action(CellIndex(x, y, z));
        }

        void CellCoord(Vector3 p, out int x, out int y, out int z)
        {
            x = Mathf.Clamp((int)((p.x - m_Origin.x) * m_InvCellSize), 0, m_DimX - 1);
            y = Mathf.Clamp((int)((p.y - m_Origin.y) * m_InvCellSize), 0, m_DimY - 1);
            z = Mathf.Clamp((int)((p.z - m_Origin.z) * m_InvCellSize), 0, m_DimZ - 1);
        }

        int CellIndex(int x, int y, int z) => (z * m_DimY + y) * m_DimX + x;

        /// <summary>
        /// Distance from <paramref name="point"/> to the nearest point on the triangle soup, and
        /// the index of the triangle carrying it. Returns <see cref="float.PositiveInfinity"/> and
        /// -1 for an empty grid.
        /// </summary>
        public float NearestDistance(Vector3 point, out int nearestTriangle)
        {
            return NearestDistance(point, out nearestTriangle, null, 0);
        }

        /// <summary>
        /// As <see cref="NearestDistance(Vector3, out int)"/>, restricted to the triangles whose
        /// entry in <paramref name="triangleLabels"/> equals <paramref name="label"/>. A null label
        /// array accepts every triangle.
        /// </summary>
        /// <remarks>
        /// Used by the UV transfer to project a face corner onto ONE chart: the nearest triangle
        /// overall can sit across a UV seam, in a chart on the other side of the atlas.
        /// </remarks>
        public float NearestDistance(Vector3 point, out int nearestTriangle, int[] triangleLabels,
            int label)
        {
            nearestTriangle = -1;
            if (m_CellItems.Length == 0)
                return float.PositiveInfinity;

            CellCoord(point, out var cx, out var cy, out var cz);

            var bestSqr = float.PositiveInfinity;
            var maxRing = Mathf.Max(m_DimX, Mathf.Max(m_DimY, m_DimZ));

            for (var ring = 0; ring <= maxRing; ring++)
            {
                // Once the best hit is closer than the nearest possible point in the next ring,
                // no further ring can improve on it. `ring - 1` because the current ring's cells
                // touch the query cell's boundary at (ring-1) cell widths.
                if (nearestTriangle >= 0)
                {
                    var guaranteed = (ring - 1) * m_CellSize;
                    if (guaranteed > 0f && bestSqr <= guaranteed * guaranteed)
                        break;
                }

                var any = false;
                var xLo = cx - ring; var xHi = cx + ring;
                var yLo = cy - ring; var yHi = cy + ring;
                var zLo = cz - ring; var zHi = cz + ring;

                for (var z = zLo; z <= zHi; z++)
                {
                    if (z < 0 || z >= m_DimZ) continue;
                    var zEdge = z == zLo || z == zHi;

                    for (var y = yLo; y <= yHi; y++)
                    {
                        if (y < 0 || y >= m_DimY) continue;
                        var yEdge = y == yLo || y == yHi;

                        // Only the shell of the ring is new; skip the solid interior.
                        var xStep = (zEdge || yEdge) ? 1 : (xHi - xLo == 0 ? 1 : xHi - xLo);
                        for (var x = xLo; x <= xHi; x += xStep)
                        {
                            if (x < 0 || x >= m_DimX) continue;
                            any = true;

                            var cellIndex = CellIndex(x, y, z);
                            var from = m_CellStart[cellIndex];
                            var to = m_CellStart[cellIndex + 1];
                            for (var k = from; k < to; k++)
                            {
                                var t = m_CellItems[k];
                                if (triangleLabels != null && triangleLabels[t] != label) continue;
                                var closest = ClosestPointOnTriangle(point,
                                    m_Vertices[m_Triangles[t * 3]],
                                    m_Vertices[m_Triangles[t * 3 + 1]],
                                    m_Vertices[m_Triangles[t * 3 + 2]]);

                                var sqr = (closest - point).sqrMagnitude;
                                if (sqr < bestSqr)
                                {
                                    bestSqr = sqr;
                                    nearestTriangle = t;
                                }
                            }
                        }
                    }
                }

                // Ring fully outside the grid in every direction and nothing found yet: stop.
                if (!any && ring > 0 && nearestTriangle < 0 &&
                    xHi >= m_DimX && xLo < 0 && yHi >= m_DimY && yLo < 0 && zHi >= m_DimZ && zLo < 0)
                    break;
            }

            return nearestTriangle >= 0 ? Mathf.Sqrt(bestSqr) : float.PositiveInfinity;
        }

        /// <summary>
        /// The nearest point on the triangle soup to <paramref name="point"/>, with the distance to
        /// it. Returns <paramref name="point"/> itself and an infinite distance for an empty grid,
        /// so a caller that forgets to check still gets a no-op rather than a jump to the origin.
        /// </summary>
        /// <remarks>
        /// The closest point on the winning triangle is recomputed rather than carried through the
        /// search. It is one extra evaluation against the hundreds the ring walk already does, and
        /// keeping the hot loop comparing squared distances only is worth more than saving it.
        /// </remarks>
        public Vector3 NearestPoint(Vector3 point, out float distance)
        {
            return NearestPoint(point, out distance, out _);
        }

        /// <summary>
        /// As <see cref="NearestPoint(Vector3, out float)"/>, and also the unit normal of the
        /// triangle the nearest point landed on — <see cref="Vector3.zero"/> for an empty grid or a
        /// degenerate triangle.
        /// </summary>
        /// <remarks>
        /// Normalised by explicit magnitude. <c>Vector3.normalized</c> returns zero below 1e-5 and
        /// a cross product of two edges of a scan triangle is around 1e-6, so the property would
        /// hand back a zero vector for every triangle on the asset this exists to serve.
        /// </remarks>
        public Vector3 NearestPoint(Vector3 point, out float distance, out Vector3 surfaceNormal)
        {
            distance = NearestDistance(point, out var triangle);
            surfaceNormal = Vector3.zero;
            if (triangle < 0) return point;

            var a = m_Vertices[m_Triangles[triangle * 3]];
            var b = m_Vertices[m_Triangles[triangle * 3 + 1]];
            var c = m_Vertices[m_Triangles[triangle * 3 + 2]];

            var cross = Vector3.Cross(b - a, c - a);
            var magnitude = cross.magnitude;
            if (magnitude > 0f) surfaceNormal = cross / magnitude;

            return ClosestPointOnTriangle(point, a, b, c);
        }

        /// <summary>
        /// Closest point on triangle abc to p. Voronoi-region method from Ericson,
        /// "Real-Time Collision Detection", section 5.1.5 — branches on the barycentric region so
        /// edge and vertex cases are exact rather than falling out of a clamp.
        /// </summary>
        public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;

            var d1 = Vector3.Dot(ab, ap);
            var d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            var bp = p - b;
            var d3 = Vector3.Dot(ab, bp);
            var d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                var denom = d1 - d3;
                return a + ab * (Mathf.Abs(denom) > 1e-20f ? d1 / denom : 0f);
            }

            var cp = p - c;
            var d5 = Vector3.Dot(ab, cp);
            var d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                var denom = d2 - d6;
                return a + ac * (Mathf.Abs(denom) > 1e-20f ? d2 / denom : 0f);
            }

            var va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                var denom = (d4 - d3) + (d5 - d6);
                return b + (c - b) * (Mathf.Abs(denom) > 1e-20f ? (d4 - d3) / denom : 0f);
            }

            var total = va + vb + vc;
            if (Mathf.Abs(total) < 1e-20f)
                return a; // fully degenerate triangle

            var inv = 1f / total;
            return a + ab * (vb * inv) + ac * (vc * inv);
        }

        /// <summary>
        /// Area-weighted surface samples of a triangle soup. Area weighting matters: sampling per
        /// triangle would over-weight dense regions and let a remesher hide error wherever the
        /// input happened to be finely tessellated.
        /// </summary>
        public static List<Vector3> SampleSurface(Vector3[] vertices, int[] triangles,
            int targetSamples, int seed, List<Vector3> normalsOut = null)
        {
            var samples = new List<Vector3>(Mathf.Max(1, targetSamples));
            var triCount = triangles.Length / 3;
            if (triCount == 0 || targetSamples <= 0)
                return samples;

            // Cumulative area table, then stratified inverse-CDF sampling: one sample per equal
            // slice of total area plus a jitter, which has far lower variance than independent
            // uniform draws at the same count.
            var cumulative = new double[triCount + 1];
            for (var t = 0; t < triCount; t++)
            {
                var a = vertices[triangles[t * 3]];
                var b = vertices[triangles[t * 3 + 1]];
                var c = vertices[triangles[t * 3 + 2]];
                cumulative[t + 1] = cumulative[t] + Vector3.Cross(b - a, c - a).magnitude * 0.5;
            }

            var totalArea = cumulative[triCount];
            if (totalArea <= 0.0)
                return samples;

            var rng = new System.Random(seed);
            var slice = totalArea / targetSamples;

            for (var s = 0; s < targetSamples; s++)
            {
                var target = (s + rng.NextDouble()) * slice;

                // Binary search the cumulative table.
                int lo = 0, hi = triCount;
                while (lo + 1 < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (cumulative[mid] <= target) lo = mid; else hi = mid;
                }

                var ia = triangles[lo * 3];
                var ib = triangles[lo * 3 + 1];
                var ic = triangles[lo * 3 + 2];
                var pa = vertices[ia];
                var pb = vertices[ib];
                var pc = vertices[ic];

                // Uniform barycentric sample via the sqrt trick.
                var u = (float)rng.NextDouble();
                var v = (float)rng.NextDouble();
                var su = Mathf.Sqrt(u);
                var w0 = 1f - su;
                var w1 = su * (1f - v);
                var w2 = su * v;

                samples.Add(pa * w0 + pb * w1 + pc * w2);
                // See MeshMath: .normalized returns zero below 1e-5, and this cross product is twice
                // a triangle area, so on a dense mesh every sampled normal came back as (0,0,0)
                // and the normal-consistency metric scored a correct mesh as fully inconsistent.
                normalsOut?.Add(MeshMath.SafeNormalize(Vector3.Cross(pb - pa, pc - pa), Vector3.up));
            }

            return samples;
        }
    }
}
