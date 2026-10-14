// Carry the source mesh's UVs (and any other per-vertex attribute) onto the remeshed output.
//
// WHY THIS IS NOT OPTIONAL POLISH.
//
// Before this existed, ToMesh wrote positions and indices and nothing else. The result had no UV
// channel at all, so no material could texture it. On a photogrammetry scan that throws away the
// entire point of the scan: the quad mesh is a rough approximation of the shape, and every detail a
// viewer actually recognises — brickwork, road markings, the cars in the lot — lives in the texture.
// The reported symptom was "a lot of holes, and no texture at all", and the texture half was this.
//
// The source's UVs were right there the whole time. Measured on the reported input, ccnb.obj:
// 561,376 source vertices, 561,376 source UVs, 0 UVs on the output.
//
// HOW.
//
// Each output vertex is a lattice point that sits near, but not on, the input surface. So for each
// one: find the closest point on the input triangle soup, take that triangle's three corner
// attributes, and blend them by the barycentric coordinates of the closest point. That is an exact
// reconstruction wherever the attribute is linear across the triangle, which is what a UV map is.
//
// Sampled from the ORIGINAL mesh, not the welded topology and not the decimated intermediate.
// Welding merges vertices that share a position, and at a UV seam the duplicates carry *different*
// UVs — that is what makes them a seam. Sampling post-weld would blend across every seam in the
// mesh and smear the texture. Sampling the decimated intermediate would compound its own error.

using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public static class AttributeTransfer
    {
        /// <summary>
        /// Samples per-vertex UVs from a source mesh onto arbitrary target positions.
        /// </summary>
        /// <param name="sourcePositions">Source vertex positions, unwelded.</param>
        /// <param name="sourceTriangles">Source triangle indices, unwelded.</param>
        /// <param name="sourceUvs">One UV per source vertex. Null or mismatched returns null.</param>
        /// <param name="targetPositions">Points to sample at — the output vertices.</param>
        /// <returns>One UV per target position, or null when the source carried none.</returns>
        public static Vector2[] SampleUvs(Vector3[] sourcePositions, int[] sourceTriangles,
            Vector2[] sourceUvs, Vector3[] targetPositions)
        {
            if (sourcePositions == null || sourceTriangles == null || targetPositions == null)
                return null;
            if (sourceUvs == null || sourceUvs.Length != sourcePositions.Length)
                return null;
            if (sourceTriangles.Length < 3 || targetPositions.Length == 0)
                return null;

            var grid = new SpatialGrid(sourcePositions, sourceTriangles);
            var uvs = new Vector2[targetPositions.Length];

            // One nearest-surface query per output vertex, each independent — so this parallelises
            // exactly, and on a scan there are tens of thousands of them against a million
            // triangles.
            System.Threading.Tasks.Parallel.For(0, targetPositions.Length, BackgroundScheduler.Options, i =>
            {
                var p = targetPositions[i];
                var distance = grid.NearestDistance(p, out var triangle);

                if (triangle < 0 || float.IsInfinity(distance))
                {
                    uvs[i] = Vector2.zero;
                    return;
                }

                var ia = sourceTriangles[triangle * 3];
                var ib = sourceTriangles[triangle * 3 + 1];
                var ic = sourceTriangles[triangle * 3 + 2];

                var a = sourcePositions[ia];
                var b = sourcePositions[ib];
                var c = sourcePositions[ic];

                var closest = SpatialGrid.ClosestPointOnTriangle(p, a, b, c);
                Barycentric(closest, a, b, c, out var wa, out var wb, out var wc);

                uvs[i] = sourceUvs[ia] * wa + sourceUvs[ib] * wb + sourceUvs[ic] * wc;
            });

            return uvs;
        }

        /// <summary>Counters from <see cref="SampleFaceUvs"/>.</summary>
        public sealed class FaceUvStats
        {
            /// <summary>UV charts on the source: islands joined across edges whose UVs agree.</summary>
            public int charts;
            /// <summary>Output faces with at least one corner re-projected to the near side of a seam.</summary>
            public int seamFaces;
            /// <summary>Corners whose own nearest source triangle lay across a seam from their face.</summary>
            public int reprojectedCorners;
            public long ms;

            public override string ToString() =>
                $"uv: charts={charts} seamFaces={seamFaces} reprojected={reprojectedCorners} ({ms} ms)";
        }

        /// <summary>
        /// One UV per face CORNER, every corner of a face taken from one UV-continuous patch of the
        /// source.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WHY PER CORNER. <see cref="SampleUvs"/> gives each output vertex one UV. That is right
        /// everywhere except at a UV seam, and there it is badly wrong: an output quad straddling a
        /// seam gets two corners from one side and two from the other, which may sit on opposite
        /// sides of the atlas, and the quad then samples the whole texture in between -- a smeared
        /// stripe of unrelated texels running across the model. One UV per vertex cannot fix that,
        /// because the vertex on the seam genuinely has two UVs; the source mesh itself can only
        /// represent it by duplicating the vertex.
        /// </para>
        /// <para>
        /// WHY NOT BY CHART. The first version of this picked one chart per face and projected every
        /// corner onto it. That misses the commonest seam there is: a chart that wraps round and
        /// meets ITSELF -- a cylinder, a sleeve, a lat/long sphere -- is one chart on both sides of
        /// its seam, so chart identity cannot see the seam at all. Measured on the test sphere:
        /// 0 seam faces found, and faces spanning 0.99 of the atlas.
        /// </para>
        /// <para>
        /// HOW. Each face grows a patch from the source triangle under its centroid, crossing only
        /// edges whose endpoints carry the same UVs on both sides and stopping at a radius just past
        /// its farthest corner. A patch grown that way cannot step over a seam, whichever charts lie
        /// either side of it. A corner whose own nearest triangle is in the patch keeps it; one past
        /// the seam is projected onto the patch instead, i.e. clamped to the near side of the seam,
        /// so the face is slightly squeezed in texture space rather than stretched across the atlas.
        /// </para>
        /// </remarks>
        /// <returns>One array per face, parallel to <paramref name="faces"/>; null when no UVs.</returns>
        public static Vector2[][] SampleFaceUvs(Vector3[] sourcePositions, int[] sourceTriangles,
            Vector2[] sourceUvs, Vector3[] targetPositions, IList<int[]> faces, out FaceUvStats stats)
        {
            stats = new FaceUvStats();
            if (sourcePositions == null || sourceTriangles == null || targetPositions == null ||
                faces == null)
                return null;
            if (sourceUvs == null || sourceUvs.Length != sourcePositions.Length)
                return null;
            if (sourceTriangles.Length < 3 || targetPositions.Length == 0)
                return null;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            UvAdjacency(sourcePositions, sourceTriangles, sourceUvs, out var adjacencyStart,
                out var adjacency);
            Charts(adjacencyStart, adjacency, sourceTriangles.Length / 3, out stats.charts);
            var grid = new SpatialGrid(sourcePositions, sourceTriangles);

            Vector3 Closest(Vector3 p, int triangle) => SpatialGrid.ClosestPointOnTriangle(p,
                sourcePositions[sourceTriangles[triangle * 3]],
                sourcePositions[sourceTriangles[triangle * 3 + 1]],
                sourcePositions[sourceTriangles[triangle * 3 + 2]]);

            Vector2 Sample(Vector3 p, int triangle)
            {
                var ia = sourceTriangles[triangle * 3];
                var ib = sourceTriangles[triangle * 3 + 1];
                var ic = sourceTriangles[triangle * 3 + 2];
                var a = sourcePositions[ia];
                var b = sourcePositions[ib];
                var c = sourcePositions[ic];
                var closest = SpatialGrid.ClosestPointOnTriangle(p, a, b, c);
                Barycentric(closest, a, b, c, out var wa, out var wb, out var wc);
                return sourceUvs[ia] * wa + sourceUvs[ib] * wb + sourceUvs[ic] * wc;
            }

            // Nearest triangle per output vertex, unrestricted -- shared by every face using it.
            var nearest = new int[targetPositions.Length];
            System.Threading.Tasks.Parallel.For(0, targetPositions.Length, BackgroundScheduler.Options, i =>
            {
                grid.NearestDistance(targetPositions[i], out nearest[i]);
            });

            var result = new Vector2[faces.Count][];
            var seamFaces = 0;
            var reprojected = 0;

            System.Threading.Tasks.Parallel.For(0, faces.Count, BackgroundScheduler.Options,
                () => (patch: new List<int>(), seen: new HashSet<int>()),
                (f, _, local) =>
                {
                    var face = faces[f];
                    if (face == null || face.Length < 3) return local;

                    var centroid = Vector3.zero;
                    for (var k = 0; k < face.Length; k++) centroid += targetPositions[face[k]];
                    centroid /= face.Length;
                    var radius = 0f;
                    for (var k = 0; k < face.Length; k++)
                        radius = Mathf.Max(radius, (targetPositions[face[k]] - centroid).magnitude);
                    // Past the farthest corner, so a corner that sits just off the surface still has
                    // the triangle under it inside the patch.
                    radius = radius * 1.5f + 1e-6f;

                    var uvs = new Vector2[face.Length];
                    grid.NearestDistance(centroid, out var anchor);
                    if (anchor < 0)
                    {
                        for (var k = 0; k < face.Length; k++)
                            uvs[k] = nearest[face[k]] >= 0
                                ? Sample(targetPositions[face[k]], nearest[face[k]])
                                : Vector2.zero;
                        result[f] = uvs;
                        return local;
                    }

                    // Grow the UV-continuous patch.
                    var patch = local.patch;
                    var seen = local.seen;
                    patch.Clear();
                    seen.Clear();
                    patch.Add(anchor);
                    seen.Add(anchor);
                    for (var i = 0; i < patch.Count; i++)
                    {
                        var t = patch[i];
                        for (var a = adjacencyStart[t]; a < adjacencyStart[t + 1]; a++)
                        {
                            var n = adjacency[a];
                            if (!seen.Add(n)) continue;
                            if ((Closest(centroid, n) - centroid).sqrMagnitude > radius * radius)
                                continue;
                            patch.Add(n);
                        }
                    }

                    var moved = false;
                    for (var k = 0; k < face.Length; k++)
                    {
                        var p = targetPositions[face[k]];
                        var t = nearest[face[k]];
                        if (t < 0 || !seen.Contains(t) || !InPatch(patch, t))
                        {
                            var best = anchor;
                            var bestSqr = float.PositiveInfinity;
                            foreach (var candidate in patch)
                            {
                                var sqr = (Closest(p, candidate) - p).sqrMagnitude;
                                if (sqr < bestSqr) { bestSqr = sqr; best = candidate; }
                            }
                            if (best != t)
                            {
                                t = best;
                                moved = true;
                                System.Threading.Interlocked.Increment(ref reprojected);
                            }
                        }
                        uvs[k] = Sample(p, t);
                    }

                    if (moved) System.Threading.Interlocked.Increment(ref seamFaces);
                    result[f] = uvs;
                    return local;
                },
                _ => { });

            stats.seamFaces = seamFaces;
            stats.reprojectedCorners = reprojected;
            stats.ms = watch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// `seen` also holds triangles the growth looked at and rejected for distance, so membership
        /// of the patch proper is checked against the list. Patches are small; a scan is fine.
        /// </summary>
        static bool InPatch(List<int> patch, int triangle)
        {
            for (var i = 0; i < patch.Count; i++)
                if (patch[i] == triangle) return true;
            return false;
        }

        /// <summary>
        /// Chart id per source triangle. Two triangles share a chart when they share an edge (by
        /// position) whose endpoints carry the same UVs on both sides.
        /// </summary>
        public static int[] Charts(Vector3[] positions, int[] triangles, Vector2[] uvs, out int count)
        {
            UvAdjacency(positions, triangles, uvs, out var start, out var adjacency);
            return Charts(start, adjacency, triangles.Length / 3, out count);
        }

        static int[] Charts(int[] start, int[] adjacency, int triangleCount, out int count)
        {
            var labels = new int[triangleCount];
            for (var t = 0; t < triangleCount; t++) labels[t] = -1;
            count = 0;
            var stack = new Stack<int>();
            for (var seed = 0; seed < triangleCount; seed++)
            {
                if (labels[seed] >= 0) continue;
                labels[seed] = count;
                stack.Push(seed);
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    for (var a = start[t]; a < start[t + 1]; a++)
                    {
                        var n = adjacency[a];
                        if (labels[n] >= 0) continue;
                        labels[n] = count;
                        stack.Push(n);
                    }
                }
                count++;
            }
            return labels;
        }

        /// <summary>
        /// Triangle adjacency across UV-CONTINUOUS edges only, as CSR: the neighbours of t are
        /// adjacency[start[t] .. start[t + 1] - 1].
        /// </summary>
        static void UvAdjacency(Vector3[] positions, int[] triangles, Vector2[] uvs,
            out int[] start, out int[] adjacency)
        {
            var triangleCount = triangles.Length / 3;

            // Exact position match. A seam vertex in a Unity mesh is a COPY of its twin, so the two
            // positions are bit-identical; a tolerance is not needed, and on a fine mesh it could
            // weld two genuinely separate points. Adding zero folds -0 into +0, which compare equal
            // but hash differently -- measured, that alone split a sphere's chart at its poles.
            var weld = new Dictionary<Vector3, int>(positions.Length);
            var welded = new int[positions.Length];
            for (var v = 0; v < positions.Length; v++)
            {
                var key = new Vector3(positions[v].x + 0f, positions[v].y + 0f, positions[v].z + 0f);
                if (!weld.TryGetValue(key, out var id))
                {
                    id = weld.Count;
                    weld.Add(key, id);
                }
                welded[v] = id;
            }

            const float k_Same = 1e-6f;
            bool SameUv(Vector2 a, Vector2 b) =>
                Mathf.Abs(a.x - b.x) <= k_Same && Mathf.Abs(a.y - b.y) <= k_Same;

            var pairs = new List<long>();
            // Last half-edge seen per undirected welded edge, chained back through `previous` to
            // every earlier one. Usually two, but where welding makes an edge non-manifold -- a
            // pole's fan of degenerate triangles -- more, and comparing only against the first one
            // misses the neighbour that actually matches: measured, it isolated a pole triangle.
            // EdgeKey.Comparer, not the default: long's own hash of this packing is wa ^ wb, and a
            // photogrammetry export numbers neighbouring vertices consecutively, so most edges of
            // ccnb.obj hashed to a handful of values and this loop never finished -- the window's
            // "Transferring UVs" stage sat there for over twenty minutes. See EdgeKey's header.
            var lastSeen = new Dictionary<long, int>(triangles.Length, Extraction.EdgeKey.Comparer);
            var previous = new int[triangleCount * 3];
            for (var h = 0; h < triangleCount * 3; h++)
            {
                var t = h / 3;
                var a = triangles[h];
                var b = triangles[t * 3 + (h % 3 + 1) % 3];
                var wa = welded[a];
                var wb = welded[b];
                if (wa == wb) continue;
                var key = wa < wb ? ((long)wa << 32) | (uint)wb : ((long)wb << 32) | (uint)wa;
                previous[h] = lastSeen.TryGetValue(key, out var last) ? last : -1;
                lastSeen[key] = h;

                for (var other = previous[h]; other >= 0; other = previous[other])
                {
                    var ot = other / 3;
                    if (ot == t) continue;
                    var oa = triangles[other];
                    var ob = triangles[ot * 3 + (other % 3 + 1) % 3];
                    // Endpoints matched by welded id: the two sides usually run opposite ways.
                    var joined = welded[oa] == wa
                        ? SameUv(uvs[oa], uvs[a]) && SameUv(uvs[ob], uvs[b])
                        : SameUv(uvs[oa], uvs[b]) && SameUv(uvs[ob], uvs[a]);
                    if (joined) pairs.Add(((long)t << 32) | (uint)ot);
                }
            }

            start = new int[triangleCount + 1];
            foreach (var pair in pairs)
            {
                start[(int)(pair >> 32) + 1]++;
                start[(int)(pair & 0xFFFFFFFF) + 1]++;
            }
            for (var t = 0; t < triangleCount; t++) start[t + 1] += start[t];
            adjacency = new int[start[triangleCount]];
            var fill = new int[triangleCount];
            foreach (var pair in pairs)
            {
                var a = (int)(pair >> 32);
                var b = (int)(pair & 0xFFFFFFFF);
                adjacency[start[a] + fill[a]++] = b;
                adjacency[start[b] + fill[b]++] = a;
            }
        }

        /// <summary>
        /// Barycentric coordinates of <paramref name="p"/> with respect to triangle abc.
        /// </summary>
        /// <remarks>
        /// Solved from the two-dimensional system in the triangle's own plane rather than by the
        /// ratio-of-areas formula, which loses all precision on a sliver — and a photogrammetry
        /// mesh is full of slivers. Degenerate triangles fall back to corner a, which is wrong but
        /// bounded: a degenerate triangle has no interior for the point to be inside of.
        /// </remarks>
        static void Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c,
            out float wa, out float wb, out float wc)
        {
            var v0 = b - a;
            var v1 = c - a;
            var v2 = p - a;

            var d00 = Vector3.Dot(v0, v0);
            var d01 = Vector3.Dot(v0, v1);
            var d11 = Vector3.Dot(v1, v1);
            var d20 = Vector3.Dot(v2, v0);
            var d21 = Vector3.Dot(v2, v1);

            var denom = d00 * d11 - d01 * d01;

            if (Mathf.Abs(denom) <= 1e-20f)
            {
                wa = 1f; wb = 0f; wc = 0f;
                return;
            }

            var inv = 1f / denom;
            wb = (d11 * d20 - d01 * d21) * inv;
            wc = (d00 * d21 - d01 * d20) * inv;

            // Clamp back into the triangle. The closest point is on the triangle by construction,
            // so any excursion here is rounding, but an unclamped negative weight would extrapolate
            // the UV outside the chart and show up as a visible tear.
            wb = Mathf.Clamp01(wb);
            wc = Mathf.Clamp01(wc);
            if (wb + wc > 1f)
            {
                var scale = 1f / (wb + wc);
                wb *= scale;
                wc *= scale;
            }

            wa = 1f - wb - wc;
        }
    }
}
