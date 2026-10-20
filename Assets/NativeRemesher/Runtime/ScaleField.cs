// Per-vertex lattice spacing: small quads where the surface curves, large ones where it does not.
//
// WHY. Everything upstream of this assumes one spacing for the whole mesh, and three independent
// measurements on the character point at that assumption:
//
//   * the thumb tip is not represented at 21,780 faces, because a uniform quad edge of 0.006 cannot
//     resolve a tip of that radius (Benchmark/CoverageAudit.cs);
//   * our worst interior angles are 28 degrees at p01 against QuadRemesher's 47, which is what a
//     quad forced to follow curvature it cannot resolve has to do;
//   * their edge lengths vary 4.0x across the model and ours vary 1.6x.
//
// And QuadRemesher's own bridge sends `CurvatureAdaptivness=50` by default, with `ExactQuadCount=0`
// so it is allowed to exceed the requested count to fit curvature. Read it in
// QuadRemesher/Editor/QuadRemesherBridge.cs. Uniform sizing is the assumption we hold that they do
// not, and it is the last structural difference between the two pipelines.
//
// THE SIZING LAW is chord error, not curvature directly. A chord of length h across a circle of
// radius R sits h^2/(8R) below the arc, so holding that sagitta constant gives h proportional to
// sqrt(R) — gentler than the h proportional to R that "smaller where it curves" first suggests, and
// it is the right gentleness: sizing by 1/curvature spends quads on a fingertip so aggressively
// that the rest of the model starves.
//
// GRADATION CLAMPING IS NOT OPTIONAL. The lattice is a discrete structure and neighbouring cells
// have to agree; a spacing that doubles across one edge asks two adjacent cells to be different
// sizes and the field tears rather than grading. The ratio form is used because it is
// scale-invariant, so the clamp survives the normalisation that follows it.
//
// NORMALISATION. Quad count is approximately the sum over vertices of area_i / h_i^2, so writing
// h_i = c * s_i and solving for the requested count gives c = sqrt(sum(area_i / s_i^2) / N). With
// s_i identically 1 that is sqrt(totalArea / N), which is exactly
// RemeshTopology.EdgeLengthForQuadCount. Uniform is this formula with a constant input, not a
// special case — which is what makes `adaptivity = 0` provably a no-op rather than merely intended
// to be one.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public sealed class ScaleField
    {
        /// <summary>Target lattice spacing at each welded vertex, in world units.</summary>
        public float[] spacing;

        /// <summary>Area-weighted mean spacing — what a caller wanting one number should use.</summary>
        public float meanSpacing;

        public float minSpacing;
        public float maxSpacing;

        /// <summary>Coarsest spacing over finest. QuadRemesher's is about 4 on the character.</summary>
        public float spread => minSpacing > 0f ? maxSpacing / minSpacing : 1f;

        public int vertexCount => spacing?.Length ?? 0;

        /// <summary>
        /// How strongly spacing follows curvature. 0 is uniform and reproduces
        /// <see cref="RemeshTopology.EdgeLengthForQuadCount"/> exactly; 1 is the full chord-error
        /// law.
        /// </summary>
        /// <remarks>
        /// A QUARTER, which is well below the 0.5 QuadRemesher's bridge sends, and the reason is
        /// that the two numbers are not measuring the same solver. Swept on the character against
        /// coverage and smoothness together (uncovered surface / deepest hole / edges past 45
        /// degrees): uniform 0.113 / 1.000 / 3.65; 0.25 gives 0.036 / 0.448 / 4.55; 0.5 gives
        /// 0.116 / 0.436 / 5.17; 0.75 gives 0.142 / 0.457 / 7.00; 1.0 gives 0.210 / 0.548 / 8.08.
        ///
        /// Coverage is NOT monotone in adaptivity, which is the surprise and the reason to sweep
        /// rather than reason. Past about a quarter the lattice is graded faster than it can stay
        /// consistent — two neighbouring cells of very different sizes cannot agree, so edges
        /// misclassify — and the extra resolution spent on the detail is lost again to a noisier
        /// extraction. At 0.25 the deepest hole is already less than half the uniform one and less
        /// than QuadRemesher's, and uncovered surface is a third of uniform and a quarter of theirs.
        /// </remarks>
        public const float defaultAdaptivity = 0.25f;

        /// <summary>
        /// Largest ratio between any two spacings, before gradation. Bounds how much of the budget
        /// a single sharp detail can take: unbounded, one scanner spike sized the whole model.
        /// </summary>
        /// <remarks>
        /// Two, measured against three at the same adaptivity: uncovered surface 0.102 %% against
        /// 0.116 %%, deepest hole unchanged at 0.435 %%. Tightening it costs nothing here because
        /// the adaptivity exponent has already compressed the range, and it leaves less room for a
        /// single noisy vertex to claim.
        /// </remarks>
        public const float defaultMaxRatio = 2f;

        /// <summary>
        /// Largest ratio permitted between spacings at the two ends of one edge.
        /// </summary>
        /// <remarks>
        /// The bound is structural rather than aesthetic: a lattice cell has to line up with its
        /// neighbour, and a spacing that jumps asks two adjacent cells to disagree about their own
        /// size. The field tears there instead of grading, and a tear in the position field is a
        /// hole in the output.
        ///
        /// 1.15, not the 1.3 that is standard for graded TRIANGLE meshing, and the difference is
        /// the lattice. A triangle mesher only has to interpolate a size; this one has to make two
        /// neighbouring integer lattices agree, and the residual Compat cannot remove is set by
        /// exactly this ratio. Measured on the character at adaptivity 0.25 with the clamp at 2,
        /// 1.15 against 1.3 (uncovered surface / deepest hole / edges past 45 degrees):
        /// 0.036 / 0.448 / 4.55 against 0.058 / 0.758 / 4.38. Tightening it takes the deepest hole
        /// down by a THIRD and uncovered surface by nearly half, for 0.17 points of smoothness.
        /// </remarks>
        public const float defaultGradation = 1.15f;

        /// <summary>
        /// Smoothing sweeps over the raw curvature estimate. Negative means derive it from the
        /// ratio between the quad being asked for and the triangle being measured -- see
        /// <see cref="SweepsFor"/>, which is what the default should be in almost every case.
        /// </summary>
        /// <remarks>
        /// A scan's per-vertex normals disagree by a few degrees from noise alone, and the estimate
        /// below takes a MAXIMUM over the one-ring, which is exactly the statistic noise inflates.
        /// Smoothing first means the field follows the shape rather than the scanner.
        /// </remarks>
        public const int defaultSmoothingSweeps = -1;

        /// <summary>Sweeps needed to average the curvature estimate over one output quad.</summary>
        /// <remarks>
        /// The one-ring estimate answers "how fast does the normal turn between adjacent INPUT
        /// vertices", and that is the wrong question whenever the input is much finer than the
        /// output. At 320k triangles down to 2,000 quads each quad spans about 160 triangles, so
        /// the estimate is reporting the shape at a twelfth of the scale anything will be built at
        /// -- which on a smooth sphere is not shape at all, it is triangulation noise, and the
        /// field then grades a constant-curvature surface for no reason.
        ///
        /// A Laplacian sweep diffuses about one edge, and diffusion covers distance as the square
        /// root of the count, so reaching across a quad takes (spacing / edge)^2 of them.
        ///
        /// HONEST STATUS: this is a units argument, not a measured win. It was written to explain a
        /// corpus regression it turned out not to cause — the icospheres come out at a spacing
        /// spread of 1.01, so grading was already doing nothing there and no amount of smoothing
        /// could change it (that is what the spacingSpread column was added to show). Every
        /// configuration measured since — the whole procedural corpus, and the character at 21,780
        /// — lands on the floor of 8 and is bit-identical with and without it. It is kept because
        /// it can only ever smooth MORE than the old fixed count, and smoothing only removes noise
        /// from an estimate; it is not kept on evidence. The case it exists for is a detailed scan
        /// asked for a very coarse result, where a quad spans forty input triangles or more, and
        /// that case has not been measured.
        ///
        /// Capped because the count grows quadratically and a sweep is a pass over every vertex,
        /// and floored so a coarse input still gets the noise taken off.
        /// </remarks>
        public static int SweepsFor(RemeshTopology topology, int targetQuadCount)
        {
            var edge = topology.averageEdgeLength;
            if (!(edge > 0f) || targetQuadCount <= 0) return 8;

            var ratio = topology.EdgeLengthForQuadCount(targetQuadCount) / edge;
            return Mathf.Clamp(Mathf.RoundToInt(ratio * ratio), 8, 400);
        }

        public static ScaleField Uniform(RemeshTopology topology, float spacing)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (!(spacing > 0f))
                throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing must be positive.");

            var values = new float[topology.vertexCount];
            for (var i = 0; i < values.Length; i++) values[i] = spacing;

            return new ScaleField
            {
                spacing = values,
                meanSpacing = spacing,
                minSpacing = spacing,
                maxSpacing = spacing
            };
        }

        /// <summary>
        /// Spacing graded by surface curvature, normalised so the mesh comes out at about
        /// <paramref name="targetQuadCount"/> faces.
        /// </summary>
        public static ScaleField FromCurvature(RemeshTopology topology, int targetQuadCount,
            float adaptivity = defaultAdaptivity, float maxRatio = defaultMaxRatio,
            float gradation = defaultGradation, int smoothingSweeps = defaultSmoothingSweeps)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));

            var count = topology.vertexCount;
            if (count == 0 || targetQuadCount <= 0)
                return Uniform(topology, topology.averageEdgeLength);

            adaptivity = Mathf.Clamp01(adaptivity);
            if (adaptivity <= 0f)
                return Uniform(topology, topology.EdgeLengthForQuadCount(targetQuadCount));

            maxRatio = Mathf.Max(1f, maxRatio);
            gradation = Mathf.Max(1.01f, gradation);

            float[] curvature;
            if (tensorCurvature)
            {
                curvature = TensorCurvature(topology,
                    tensorRadiusCells * topology.EdgeLengthForQuadCount(targetQuadCount));
            }
            else
            {
                curvature = Curvature(topology);
                Smooth(topology, curvature,
                    smoothingSweeps < 0 ? SweepsFor(topology, targetQuadCount) : smoothingSweeps);
            }

            // Chord error: h proportional to sqrt(radius of curvature) = 1/sqrt(curvature).
            var relative = new float[count];
            for (var i = 0; i < count; i++)
                relative[i] = 1f / Mathf.Sqrt(Mathf.Max(curvature[i], 1e-12f));

            // Made relative to the MEDIAN rather than the mean, because the distribution has a long
            // tail — a handful of near-flat vertices have enormous radii — and a mean would let
            // those set the reference for everything else.
            var reference = Median(relative);
            if (!(reference > 0f)) return Uniform(topology, topology.EdgeLengthForQuadCount(targetQuadCount));

            var lower = 1f / maxRatio;
            for (var i = 0; i < count; i++)
            {
                // Powering the RATIO is what makes adaptivity a continuous dial between uniform and
                // the full law, and what makes 0 exactly uniform rather than nearly uniform.
                var r = Mathf.Pow(relative[i] / reference, adaptivity);
                relative[i] = Mathf.Clamp(r, lower, maxRatio);
            }

            if (gradationPerCell > 1f)
                GradePerCell(topology, relative, gradationPerCell,
                    topology.EdgeLengthForQuadCount(targetQuadCount));
            else
                Grade(topology, relative, gradation);

            // Normalisation, and the only place the target count enters. See the file header for
            // why this is the same formula uniform sizing already uses.
            var areas = topology.areas;
            var weighted = 0.0;
            for (var i = 0; i < count; i++)
                weighted += areas[i] / (double)(relative[i] * relative[i]);

            var c = (float)Math.Sqrt(weighted / targetQuadCount);
            if (!(c > 0f)) return Uniform(topology, topology.EdgeLengthForQuadCount(targetQuadCount));

            var values = new float[count];
            var min = float.MaxValue;
            var max = 0f;
            var areaWeighted = 0.0;
            var totalArea = 0.0;

            for (var i = 0; i < count; i++)
            {
                var h = c * relative[i];
                values[i] = h;
                min = Mathf.Min(min, h);
                max = Mathf.Max(max, h);
                areaWeighted += h * areas[i];
                totalArea += areas[i];
            }

            return new ScaleField
            {
                spacing = values,
                meanSpacing = totalArea > 0 ? (float)(areaWeighted / totalArea) : c,
                minSpacing = min,
                maxSpacing = max
            };
        }

        /// <summary>
        /// Spacing from an error-driven simplification of the surface to the output's own budget:
        /// wherever quadric decimation to <paramref name="coarseTriangles"/> left small triangles,
        /// the shape needed them, and the quads are made as small there.
        /// </summary>
        /// <remarks>
        /// A curvature law sizes by how much the surface bends, smoothed over a neighbourhood, so a
        /// slot a few millimetres wide on a flat plate -- most of what makes a hard-surface model
        /// read -- barely registers, and every cell is within a few times of every other. Measured
        /// on the helmet head against a reference retopology of the same asset at 6,628 faces:
        /// edge-length spread 0.23 against its 0.75, and at that face count 0.87-1.03 % of the
        /// diagonal at the 99th percentile of input-to-output distance against its 0.42 %. Quadric
        /// decimation is the error-optimal allocation of a polygon budget; its local edge length is
        /// a sizing field that already knows where the detail is. <paramref name="useMinimum"/>:
        /// each coarse vertex's shortest edge rather than its mean, so a thin cylinder decimated
        /// into long slivers along its axis is sized by its girth.
        /// </remarks>
        public static ScaleField FromDecimation(RemeshTopology topology, int targetQuadCount,
            Vector3[] coarsePositions, int[] coarseTriangles, float maxRatio, float gradation,
            bool useMinimum)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            var count = topology.vertexCount;
            if (count == 0 || targetQuadCount <= 0 || coarseTriangles == null || coarseTriangles.Length < 3)
                return Uniform(topology, topology.EdgeLengthForQuadCount(Mathf.Max(1, targetQuadCount)));

            // Per coarse vertex: its incident edges' shortest or mean length.
            var size = new float[coarsePositions.Length];
            var sum = new float[coarsePositions.Length];
            var n = new int[coarsePositions.Length];
            for (var i = 0; i < size.Length; i++) size[i] = float.MaxValue;
            for (var t = 0; t + 2 < coarseTriangles.Length; t += 3)
                for (var k = 0; k < 3; k++)
                {
                    int a = coarseTriangles[t + k], b = coarseTriangles[t + (k + 1) % 3];
                    var l = (coarsePositions[b] - coarsePositions[a]).magnitude;
                    if (!(l > 0f)) continue;
                    size[a] = Mathf.Min(size[a], l); size[b] = Mathf.Min(size[b], l);
                    sum[a] += l; sum[b] += l; n[a]++; n[b]++;
                }
            for (var i = 0; i < size.Length; i++)
                size[i] = n[i] == 0 ? 0f : useMinimum ? size[i] : sum[i] / n[i];

            // Onto the solver's vertices, through the nearest point of the coarse surface.
            var grid = new SpatialGrid(coarsePositions, coarseTriangles);
            var relative = new float[count];
            var positions = topology.positions;
            System.Threading.Tasks.Parallel.For(0, count, BackgroundScheduler.Options, i =>
            {
                grid.NearestDistance(positions[i], out var t);
                if (t < 0) { relative[i] = 0f; return; }
                int ia = coarseTriangles[3 * t], ib = coarseTriangles[3 * t + 1], ic = coarseTriangles[3 * t + 2];
                Vector3 a = coarsePositions[ia], b = coarsePositions[ib], c = coarsePositions[ic];
                var q = SpatialGrid.ClosestPointOnTriangle(positions[i], a, b, c);
                // Barycentric weights, clamped; the smaller corner wins at a tie-free edge anyway.
                Vector3 v0 = b - a, v1 = c - a, v2 = q - a;
                float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
                float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
                var den = d00 * d11 - d01 * d01;
                float wb = 0f, wc = 0f;
                if (Mathf.Abs(den) > 1e-30f)
                {
                    wb = Mathf.Clamp01((d11 * d20 - d01 * d21) / den);
                    wc = Mathf.Clamp01((d00 * d21 - d01 * d20) / den);
                    if (wb + wc > 1f) { var s = 1f / (wb + wc); wb *= s; wc *= s; }
                }
                relative[i] = (1f - wb - wc) * size[ia] + wb * size[ib] + wc * size[ic];
            });

            var reference = Median(relative);
            if (!(reference > 0f)) return Uniform(topology, topology.EdgeLengthForQuadCount(targetQuadCount));
            maxRatio = Mathf.Max(1f, maxRatio);
            for (var i = 0; i < count; i++)
                relative[i] = Mathf.Clamp(relative[i] > 0f ? relative[i] / reference : 1f, 1f / maxRatio, maxRatio);
            Grade(topology, relative, Mathf.Max(1.01f, gradation));
            return Normalised(topology, relative, targetQuadCount);
        }

        // Absolute spacings from relative ones: the quads they make on the topology's area add up
        // to the target count.
        /// <summary>
        /// The field shrunk round small sharp parts: every connected group of edges sharper than
        /// <paramref name="angle"/> whose extent is under <paramref name="maxExtentCells"/> cells (a
        /// slot's rim, a screw's, a buckle's) asks for spacing <paramref name="fraction"/> of that
        /// extent at its vertices, spread outward growing by <paramref name="slope"/> per unit
        /// distance, then all of it renormalised to the count.
        /// </summary>
        /// <remarks>
        /// The reference remesh of the helmet head draws its slots and screws with several grid
        /// lines across a part a centimetre wide; a cell of 1.9 cm cannot hold even two. Curvature
        /// does not find them -- on that sculpt the normals turn 37 degrees within 4 mm at the
        /// median point, fabric and all -- but their rims are sharp edges.
        /// </remarks>
        public static ScaleField ShrunkToSmallParts(RemeshTopology topology, ScaleField field, int targetQuadCount,
            float angle, float fraction, float maxExtentCells, float minLengthCells, float slope, float floorCells)
        {
            var n = topology.vertexCount;
            var cell = topology.EdgeLengthForQuadCount(Mathf.Max(1, targetQuadCount));
            var graph = FeatureGraph.Detect(topology, angle);
            var spacing = (float[])field.spacing.Clone();
            // Groups: polylines sharing a vertex.
            var parent = new int[graph.polylineCount];
            for (var c = 0; c < parent.Length; c++) parent[c] = c;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            var owner = new Dictionary<int, int>();
            for (var c = 0; c < graph.polylineCount; c++)
                for (var i = graph.polylineStart[c]; i < graph.polylineStart[c + 1]; i++)
                {
                    var v = graph.polylineVertices[i];
                    if (owner.TryGetValue(v, out var o)) parent[Find(c)] = Find(o);
                    else owner[v] = c;
                }
            var lo = new Dictionary<int, Vector3>();
            var hi = new Dictionary<int, Vector3>();
            var length = new Dictionary<int, float>();
            for (var c = 0; c < graph.polylineCount; c++)
            {
                var g = Find(c);
                for (var i = graph.polylineStart[c]; i < graph.polylineStart[c + 1]; i++)
                {
                    var p = topology.positions[graph.polylineVertices[i]];
                    lo[g] = lo.TryGetValue(g, out var l) ? Vector3.Min(l, p) : p;
                    hi[g] = hi.TryGetValue(g, out var h) ? Vector3.Max(h, p) : p;
                    if (i > graph.polylineStart[c])
                        length[g] = (length.TryGetValue(g, out var len) ? len : 0f)
                                    + (p - topology.positions[graph.polylineVertices[i - 1]]).magnitude;
                }
            }
            var seeds = new List<int>();
            var partSet = new HashSet<int>();
            for (var c = 0; c < graph.polylineCount; c++)
            {
                var g = Find(c);
                var extent = (hi[g] - lo[g]).magnitude;
                if (extent > maxExtentCells * cell || !length.TryGetValue(g, out var len) || len < minLengthCells * cell) continue;
                partSet.Add(g);
                var want = Mathf.Max(floorCells * cell, fraction * extent);
                for (var i = graph.polylineStart[c]; i < graph.polylineStart[c + 1]; i++)
                {
                    var v = graph.polylineVertices[i];
                    if (want < spacing[v]) { spacing[v] = want; seeds.Add(v); }
                }
            }
            lastSmallParts = partSet.Count;
            if (seeds.Count == 0) return field;
            // Spread: spacing(u) <= spacing(v) + slope * |uv|, Dijkstra from the seeds.
            var heap = new SortedSet<(float s, int v)>();
            foreach (var v in seeds) heap.Add((spacing[v], v));
            while (heap.Count > 0)
            {
                var top = heap.Min;
                heap.Remove(top);
                if (top.s > spacing[top.v]) continue;
                for (var k = topology.adjacencyStart[top.v]; k < topology.adjacencyStart[top.v + 1]; k++)
                {
                    var u = topology.adjacency[k];
                    if (u == top.v) continue;
                    var s = spacing[top.v] + slope * (topology.positions[u] - topology.positions[top.v]).magnitude;
                    if (s >= spacing[u]) continue;
                    heap.Remove((spacing[u], u));
                    spacing[u] = s;
                    heap.Add((s, u));
                }
            }
            return Normalised(topology, spacing, targetQuadCount);
        }

        /// <summary>
        /// As <see cref="ShrunkToSmallParts"/>, measured by the GAP between sharp edges rather than
        /// the size of a group of them: at each vertex of a sharp edge, the distance to the nearest
        /// other sharp edge it cannot reach along the edges within <paramref name="detour"/> times
        /// that distance -- the far rim of a slot, the far side of a screw's circle. The rail's
        /// rims all join into one group, so a group's size says nothing about its slots.
        /// </summary>
        public static ScaleField ShrunkToGaps(RemeshTopology topology, ScaleField field, int targetQuadCount,
            float angle, float fraction, float maxGapCells, float minLengthCells, float slope, float floorCells,
            float detour)
        {
            var cell = topology.EdgeLengthForQuadCount(Mathf.Max(1, targetQuadCount));
            var graph = FeatureGraph.Detect(topology, angle);
            if (minLengthCells > 0f)
                graph = graph.KeepCoherentCurves(topology, minLengthCells * cell, 0f, out _, out _);
            var p = topology.positions;
            var creaseNeighbours = new Dictionary<int, List<int>>();
            for (var c = 0; c < graph.polylineCount; c++)
                for (var i = graph.polylineStart[c] + 1; i < graph.polylineStart[c + 1]; i++)
                {
                    int a = graph.polylineVertices[i - 1], b = graph.polylineVertices[i];
                    if (!creaseNeighbours.TryGetValue(a, out var la)) creaseNeighbours[a] = la = new List<int>(2);
                    if (!creaseNeighbours.TryGetValue(b, out var lb)) creaseNeighbours[b] = lb = new List<int>(2);
                    if (!la.Contains(b)) la.Add(b);
                    if (!lb.Contains(a)) lb.Add(a);
                }
            var verts = new List<int>(creaseNeighbours.Keys);
            var maxGap = maxGapCells * cell;
            var hash = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Key(Vector3 x) => (Mathf.FloorToInt(x.x / maxGap), Mathf.FloorToInt(x.y / maxGap), Mathf.FloorToInt(x.z / maxGap));
            foreach (var v in verts)
            {
                var k = Key(p[v]);
                if (!hash.TryGetValue(k, out var list)) hash[k] = list = new List<int>();
                list.Add(v);
            }
            var gap = new float[verts.Count];
            System.Threading.Tasks.Parallel.For(0, verts.Count, BackgroundScheduler.Options, i =>
            {
                var v = verts[i];
                // Distances along the sharp edges from v, out to detour x maxGap.
                var along = new Dictionary<int, float> { [v] = 0f };
                var frontier = new List<int> { v };
                var reach = detour * maxGap;
                while (frontier.Count > 0)
                {
                    var next = new List<int>();
                    foreach (var x in frontier)
                        foreach (var y in creaseNeighbours[x])
                        {
                            var d = along[x] + (p[y] - p[x]).magnitude;
                            if (d > reach || along.TryGetValue(y, out var old) && old <= d) continue;
                            along[y] = d;
                            next.Add(y);
                        }
                    frontier = next;
                }
                var best = float.MaxValue;
                var (kx, ky, kz) = Key(p[v]);
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!hash.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                    foreach (var w in list)
                    {
                        if (w == v) continue;
                        var straight = (p[w] - p[v]).magnitude;
                        if (straight >= best || straight > maxGap) continue;
                        if (along.TryGetValue(w, out var path) && path <= detour * straight) continue;
                        best = straight;
                    }
                }
                gap[i] = best;
            });
            var spacing = (float[])field.spacing.Clone();
            var seeds = new List<int>();
            for (var i = 0; i < verts.Count; i++)
            {
                if (gap[i] > maxGap) continue;
                var v = verts[i];
                var want = Mathf.Max(floorCells * cell, fraction * gap[i]);
                if (want >= spacing[v]) continue;
                spacing[v] = want;
                seeds.Add(v);
            }
            lastSmallParts = seeds.Count;
            if (seeds.Count == 0) return field;
            var heap = new SortedSet<(float s, int v)>();
            foreach (var v in seeds) heap.Add((spacing[v], v));
            while (heap.Count > 0)
            {
                var top = heap.Min;
                heap.Remove(top);
                if (top.s > spacing[top.v]) continue;
                for (var k = topology.adjacencyStart[top.v]; k < topology.adjacencyStart[top.v + 1]; k++)
                {
                    var u = topology.adjacency[k];
                    if (u == top.v) continue;
                    var s = spacing[top.v] + slope * (p[u] - p[top.v]).magnitude;
                    if (s >= spacing[u]) continue;
                    heap.Remove((spacing[u], u));
                    spacing[u] = s;
                    heap.Add((s, u));
                }
            }
            return Normalised(topology, spacing, targetQuadCount);
        }

        /// <summary>Diagnostics: small parts the last ShrunkToSmallParts found.</summary>
        public static int lastSmallParts;

        static ScaleField Normalised(RemeshTopology topology, float[] relative, int targetQuadCount)
        {
            var count = relative.Length;
            var areas = topology.areas;
            var weighted = 0.0;
            for (var i = 0; i < count; i++)
                weighted += areas[i] / (double)(relative[i] * relative[i]);
            var c = (float)Math.Sqrt(weighted / targetQuadCount);
            if (!(c > 0f)) return Uniform(topology, topology.EdgeLengthForQuadCount(targetQuadCount));
            var values = new float[count];
            var min = float.MaxValue;
            var max = 0f;
            double areaWeighted = 0, totalArea = 0;
            for (var i = 0; i < count; i++)
            {
                var h = c * relative[i];
                values[i] = h;
                min = Mathf.Min(min, h);
                max = Mathf.Max(max, h);
                areaWeighted += h * areas[i];
                totalArea += areas[i];
            }
            return new ScaleField
            {
                spacing = values,
                meanSpacing = totalArea > 0 ? (float)(areaWeighted / totalArea) : c,
                minSpacing = min,
                maxSpacing = max
            };
        }

        /// <summary>Every spacing multiplied by <paramref name="factor"/>.</summary>
        /// <remarks>
        /// The pipeline's face-count probe corrects the lattice until the output lands on the
        /// requested count. With one scalar spacing that was a multiply; with a field it has to
        /// scale the whole field at once, or the probe would flatten the grading it is meant to
        /// leave alone.
        /// </remarks>
        public ScaleField Scaled(float factor)
        {
            if (!(factor > 0f)) throw new ArgumentOutOfRangeException(nameof(factor));

            var values = new float[spacing.Length];
            for (var i = 0; i < values.Length; i++) values[i] = spacing[i] * factor;

            return new ScaleField
            {
                spacing = values,
                meanSpacing = meanSpacing * factor,
                minSpacing = minSpacing * factor,
                maxSpacing = maxSpacing * factor
            };
        }

        /// <summary>
        /// A field from spacings already computed elsewhere - the hierarchy restricting this field
        /// onto a coarser vertex set, principally. Recomputes the summary statistics rather than
        /// trusting the caller for them.
        /// </summary>
        /// <summary>
        /// The field multiplied by a factor per vertex (painted areas, DensityPaint), graded again
        /// so a hard brush rim does not step the lattice faster than it can follow. Not normalised:
        /// the caller raises the target by what the paint adds.
        /// </summary>
        public static ScaleField Painted(RemeshTopology topology, ScaleField field, float[] factors, float gradation)
        {
            if (field?.spacing == null || factors == null || factors.Length != field.spacing.Length) return field;
            var values = new float[factors.Length];
            for (var i = 0; i < values.Length; i++) values[i] = field.spacing[i] * factors[i];
            Grade(topology, values, Mathf.Max(1.01f, gradation));
            var areas = topology.areas;
            double weighted = 0, total = 0;
            for (var i = 0; i < values.Length; i++) { weighted += values[i] * areas[i]; total += areas[i]; }
            return FromSpacings(values, total > 0 ? (float)(weighted / total) : field.meanSpacing);
        }

        /// <summary>
        /// How thick the part is at each vertex: the distance from it, straight in (against its
        /// normal), to the inside of the far wall, looked for out to <paramref name="reach"/>[i].
        /// Infinity where nothing is found that close -- a wide part, or an open sheet.
        /// </summary>
        public static float[] Thickness(RemeshTopology topology, float[] reach)
        {
            var count = topology.vertexCount;
            var thickness = new float[count];
            var normals = topology.normals;
            if (normals == null || normals.Length != count || topology.triangles == null)
            {
                for (var i = 0; i < count; i++) thickness[i] = float.PositiveInfinity;
                return thickness;
            }
            var grid = new SpatialGrid(topology.positions, topology.triangles);
            var positions = topology.positions;
            System.Threading.Tasks.Parallel.For(0, count, BackgroundScheduler.Options, i =>
            {
                thickness[i] = float.PositiveInfinity;
                var n = normals[i];
                var r = reach[i];
                if (!(r > 0f) || n.sqrMagnitude < 0.25f) return;
                // Started a hair inside, so the vertex's own triangles are not the hit; only walls
                // facing the same way as the ray count, which is the inside of the far side.
                var inward = -n.normalized;
                if (!grid.FirstHit(positions[i] + inward * (1e-3f * r), inward, r, 1f, out var t, out _)) return;
                t += 1e-3f * r;
                if (rodsOnly && !IsRod(grid, positions[i] + inward * (0.5f * t), inward, t)) return;
                thickness[i] = t;
            });
            return thickness;
        }

        /// <summary>
        /// <see cref="Thickness"/> counts only parts thin in two directions -- a finger, a toe, a
        /// rod, the rim of a strap -- and not a plate's face, which is thin one way and wide the
        /// other: sized to its thickness, a panel or a coat modelled with two sides would take a
        /// grid several times finer over its whole area.
        /// </summary>
        public static bool rodsOnly = true;

        /// <summary>
        /// How fast <see cref="CappedByThickness"/>'s small cells grow back out: the spacing may
        /// multiply by this much over one of its own cells. 1 or less: per edge, by the gradation.
        /// </summary>
        public static float thicknessGradationPerCell = 1.5f;

        /// <summary>
        /// Whether the solid round <paramref name="middle"/> (half way through it along
        /// <paramref name="through"/>, <paramref name="thickness"/> across) is closed in on both
        /// sides along some direction square to <paramref name="through"/>, within 0.75 of the
        /// thickness: across a rod it is, at 0.5; across a plate's face it runs on.
        /// </summary>
        static bool IsRod(SpatialGrid grid, Vector3 middle, Vector3 through, float thickness)
        {
            var u = OrientationField.ArbitraryTangent(through);
            var v = Vector3.Cross(through, u);
            var reach = 0.75f * thickness;
            for (var k = 0; k < 4; k++)
            {
                var angle = k * Mathf.PI / 4f;
                var d = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
                if (grid.FirstHit(middle, d, reach, 0f, out _, out _) && grid.FirstHit(middle, -d, reach, 0f, out _, out _))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The field with no cell wider than a part's thickness over <paramref name="cellsAcross"/>,
        /// graded back out to its surroundings: a finger, a toe, a rod gets that many cells across
        /// whatever the rest of the model is sized at. Parts thinner than
        /// <paramref name="thinnestCells"/> of the local cell are left alone -- a panel modelled
        /// with two sides a few millimetres apart is a sheet, not a finger, and sizing to its
        /// thickness would multiply the whole panel's quads.
        /// </summary>
        /// <remarks>
        /// Why not more curvature grading: the chord-error law's ratio is powered by the adaptivity
        /// (0.25 at Adaptive Size 50), so a finger whose radius is under one cell is given about 0.8
        /// of the median spacing, where eight segments round it need about 0.45. Raising the power
        /// regrades the whole model; this touches only what is thin. Creases are not thin, so a
        /// building's edge or a panel's fold is not refined by it.
        /// </remarks>
        public static ScaleField CappedByThickness(RemeshTopology topology, ScaleField field, float[] thickness,
            float cellsAcross, float thinnestCells, float gradation, float floor = 0f)
        {
            if (field?.spacing == null || thickness == null || thickness.Length != field.spacing.Length
                || !(cellsAcross > 0f)) return field;
            var values = (float[])field.spacing.Clone();
            var capped = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var t = thickness[i];
                var h = values[i];
                if (float.IsInfinity(t) || !(h > 0f) || t < thinnestCells * h) continue;
                var cap = Mathf.Max(t / cellsAcross, floor * h);
                if (cap < h) { values[i] = cap; capped++; }
            }
            if (capped == 0) return field;
            // Graded by distance in cells rather than per edge: per edge, how far a finger's small
            // cells spread depended on how finely the solve mesh happened to be cut, and the count
            // taken before local refinement disagreed with the one after it (body: 258 quads more
            // ungraded, 6,500 more graded per edge).
            if (thicknessGradationPerCell > 1f) GradePerCell(topology, values, thicknessGradationPerCell, 1f);
            else Grade(topology, values, Mathf.Max(1.01f, gradation));
            var areas = topology.areas;
            double weighted = 0, total = 0;
            for (var i = 0; i < values.Length; i++) { weighted += values[i] * areas[i]; total += areas[i]; }
            return FromSpacings(values, total > 0 ? (float)(weighted / total) : field.meanSpacing);
        }

        /// <summary>
        /// The field with cells no wider than the gap between a crest and the groove beside it over
        /// <paramref name="cellsPerGap"/>, graded back out: fingers pressed together, toes, a boot's
        /// welt, the ribs of a panel get a row on each crest and in each groove instead of being
        /// averaged into one smooth paddle.
        /// </summary>
        /// <remarks>
        /// A crest and a groove are vertices whose stronger principal curvature, averaged over half
        /// a cell (CurvatureDirections.ShapeTensors), turns the normal at least
        /// <paramref name="strength"/> radians across a cell, convex and concave. A pair counts when
        /// they are within <paramref name="reachCells"/> of each other and the surface between them
        /// slopes at least <paramref name="minSlopeDegrees"/> away from the groove's normal: a
        /// shallow wrinkle bends as sharply as a finger's groove but is not deep enough to be worth
        /// quads. The cell is not taken under <paramref name="floor"/> of the field's own.
        /// </remarks>
        /// <summary>Radius, in cells, the curvature is averaged over for <see cref="CappedByRidgeGaps"/>.</summary>
        public static float ridgeGapRadiusCells = 0.5f;

        public static ScaleField CappedByRidgeGaps(RemeshTopology topology, ScaleField field, float strength,
            float reachCells, float minSlopeDegrees, float cellsPerGap, float floor, float gradation,
            out int capped)
        {
            capped = 0;
            if (field?.spacing == null || topology?.normals == null || !(cellsPerGap > 0f)) return field;
            var count = topology.vertexCount;
            var h = field.spacing;
            var positions = topology.positions;
            var normals = topology.normals;
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;
            var median = Median(h);
            if (!(median > 0f)) return field;
            var tensor = CurvatureDirections.ShapeTensors(topology, ridgeGapRadiusCells * median, out _);

            // Signed stronger principal curvature times the local cell: + convex, - concave.
            var turn = new float[count];
            for (var i = 0; i < count; i++)
            {
                var n = normals[i];
                var u = OrientationField.ArbitraryTangent(n);
                var v = Vector3.Cross(n, u);
                var o = i * 6;
                Vector3 Apply(Vector3 x) => new Vector3(
                    tensor[o] * x.x + tensor[o + 1] * x.y + tensor[o + 2] * x.z,
                    tensor[o + 1] * x.x + tensor[o + 3] * x.y + tensor[o + 4] * x.z,
                    tensor[o + 2] * x.x + tensor[o + 4] * x.y + tensor[o + 5] * x.z);
                var m11 = Vector3.Dot(u, Apply(u));
                var m12 = Vector3.Dot(u, Apply(v));
                var m22 = Vector3.Dot(v, Apply(v));
                var mean = 0.5f * (m11 + m22);
                var radius = Mathf.Sqrt(0.25f * (m11 - m22) * (m11 - m22) + m12 * m12);
                var k1 = Mathf.Abs(mean + radius) >= Mathf.Abs(mean - radius) ? mean + radius : mean - radius;
                turn[i] = k1 * h[i];
            }

            // From every groove vertex at once, over the edges: the nearest groove, how far, and the
            // steepest the surface has turned from that groove's normal on the way.
            var source = new int[count];
            var slope = new float[count];
            for (var i = 0; i < count; i++) source[i] = -1;
            var queue = new Queue<int>();
            for (var i = 0; i < count; i++)
            {
                if (!(turn[i] <= -strength)) continue;
                source[i] = i;
                queue.Enqueue(i);
            }
            var gap = new float[count];
            for (var i = 0; i < count; i++) gap[i] = float.PositiveInfinity;
            var cosMin = Mathf.Cos(minSlopeDegrees * Mathf.Deg2Rad);
            while (queue.Count > 0)
            {
                var vtx = queue.Dequeue();
                var s = source[vtx];
                var reach = reachCells * h[s];
                for (var a = start[vtx]; a < start[vtx + 1]; a++)
                {
                    var j = adjacency[a];
                    if (source[j] >= 0) continue;
                    var d = (positions[j] - positions[s]).magnitude;
                    if (d > reach) continue;
                    source[j] = s;
                    slope[j] = Mathf.Max(slope[vtx], Vector3.Dot(normals[j], normals[s]) < cosMin ? 1f : 0f);
                    queue.Enqueue(j);
                    if (turn[j] >= strength && slope[j] > 0f)
                    {
                        gap[j] = Mathf.Min(gap[j], d);
                        gap[s] = Mathf.Min(gap[s], d);
                    }
                }
            }

            var values = (float[])h.Clone();
            for (var i = 0; i < count; i++)
            {
                if (float.IsInfinity(gap[i])) continue;
                var cap = Mathf.Max(gap[i] / cellsPerGap, floor * h[i]);
                if (cap < values[i]) { values[i] = cap; capped++; }
            }
            if (capped == 0) return field;
            Grade(topology, values, Mathf.Max(1.01f, gradation));
            var areas = topology.areas;
            double weighted = 0, total = 0;
            for (var i = 0; i < values.Length; i++) { weighted += values[i] * areas[i]; total += areas[i]; }
            return FromSpacings(values, total > 0 ? (float)(weighted / total) : field.meanSpacing);
        }

        /// <summary>Quads <paramref name="field"/> asks for over <paramref name="topology"/>: the sum of area over spacing squared.</summary>
        public static double QuadCount(RemeshTopology topology, ScaleField field)
        {
            var areas = topology.areas;
            double sum = 0;
            for (var i = 0; i < field.spacing.Length && i < areas.Length; i++)
                if (field.spacing[i] > 0f) sum += areas[i] / ((double)field.spacing[i] * field.spacing[i]);
            return sum;
        }

        public static ScaleField FromSpacings(float[] values, float meanSpacing)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));

            var min = float.MaxValue;
            var max = 0f;
            foreach (var v in values) { min = Mathf.Min(min, v); max = Mathf.Max(max, v); }

            return new ScaleField
            {
                spacing = values,
                meanSpacing = meanSpacing,
                minSpacing = values.Length > 0 ? min : meanSpacing,
                maxSpacing = values.Length > 0 ? max : meanSpacing
            };
        }

        public override string ToString()
        {
            return $"spacing mean {meanSpacing:F5} min {minSpacing:F5} max {maxSpacing:F5} " +
                   $"spread {spread:F2}x over {vertexCount:N0} vertices";
        }

        /// <summary>
        /// Maximum normal curvature at each vertex, estimated as the largest rate of change of the
        /// surface normal across the one-ring.
        /// </summary>
        /// <remarks>
        /// MAXIMUM over the ring, not mean, and that is the whole point. On a cylinder the normal
        /// does not change at all along the axis and changes at 1/radius around it; a mean would
        /// report half the curvature and size the quads as though the cylinder were twice as fat.
        /// Sizing has to follow the direction that needs the smallest quad.
        ///
        /// The angle is taken from the dot product rather than Vector3.Angle only to avoid the
        /// degree/radian round trip in a loop over every edge of a 200k-vertex mesh.
        /// </remarks>
        /// <summary>
        /// Measure curvature at the scale of an output cell -- the signed shape operator averaged
        /// over <see cref="tensorRadiusCells"/> of surface, then its largest principal curvature
        /// -- instead of the largest normal turn across any one input edge.
        /// </summary>
        /// <remarks>
        /// The per-edge maximum reads the finest detail in the input, and on a sculpt that is the
        /// sculpting: stitching, creases in cloth, pores. Smoothing that scalar afterwards averages
        /// the magnitudes, so a wrinkled sleeve still reads as more curved than a smooth gloved
        /// finger. Measured on the body at 5,000 with adaptivity 1.0: the hands came out at a mean
        /// spacing of 0.0089 against 0.0074 over the whole body -- COARSER than average, which is
        /// why no ratio or adaptivity setting could put a loop between the fingers. A signed tensor
        /// cancels a wrinkle's alternating bends and keeps a finger's consistent one.
        ///
        /// OFF by default all the same. Relative to the body the hands do come out finer (0.0102
        /// against a 0.0111 mean), but the same estimate flattens the Adapt Quad Count factor
        /// (body 1.96 -> 1.54), so in absolute terms the hands get FEWER quads than under the
        /// per-edge estimate (0.0102 against 0.0089), and over 4600/5000/5400 the metrics are a
        /// wash (body 1.86 / 1.97 % irregular, head 5.06 / 4.80 %). What separates the fingers is
        /// a loop in each groove -- IntegerGridMap.coherentCreases -- not the sizing.
        /// </remarks>
        public static bool tensorCurvature = false;

        /// <summary>Averaging radius of <see cref="tensorCurvature"/>, in output cells.</summary>
        public static float tensorRadiusCells = 0.5f;

        static float[] TensorCurvature(RemeshTopology topology, float radius)
        {
            var count = topology.vertexCount;
            var normals = topology.normals;
            var tensor = CurvatureDirections.ShapeTensors(topology, radius, out _);
            var curvature = new float[count];
            System.Threading.Tasks.Parallel.For(0, count, BackgroundScheduler.Options, i =>
            {
                var n = normals[i];
                var u = OrientationField.ArbitraryTangent(n);
                var v = Vector3.Cross(n, u);
                var o = i * 6;
                Vector3 Apply(Vector3 x) => new Vector3(
                    tensor[o] * x.x + tensor[o + 1] * x.y + tensor[o + 2] * x.z,
                    tensor[o + 1] * x.x + tensor[o + 3] * x.y + tensor[o + 4] * x.z,
                    tensor[o + 2] * x.x + tensor[o + 4] * x.y + tensor[o + 5] * x.z);
                var mu = Apply(u);
                var mv = Apply(v);
                var m11 = Vector3.Dot(u, mu);
                var m12 = Vector3.Dot(u, mv);
                var m22 = Vector3.Dot(v, mv);
                var mean = 0.5f * (m11 + m22);
                var half = 0.5f * (m11 - m22);
                var radial = Mathf.Sqrt(half * half + m12 * m12);
                curvature[i] = Mathf.Abs(mean) + radial;
            });
            return curvature;
        }

        static float[] Curvature(RemeshTopology topology)
        {
            var count = topology.vertexCount;
            var positions = topology.positions;
            var normals = topology.normals;
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;

            var curvature = new float[count];

            for (var i = 0; i < count; i++)
            {
                var worst = 0f;
                var ni = normals[i];
                var pi = positions[i];

                for (var k = start[i]; k < start[i + 1]; k++)
                {
                    var j = adjacency[k];
                    var d = (positions[j] - pi).magnitude;
                    if (d <= 1e-12f) continue;

                    var cos = Mathf.Clamp(Vector3.Dot(ni, normals[j]), -1f, 1f);
                    var angle = Mathf.Acos(cos);

                    worst = Mathf.Max(worst, angle / d);
                }

                curvature[i] = worst;
            }

            return curvature;
        }

        /// <summary>Laplacian smoothing of a per-vertex scalar over the adjacency graph.</summary>
        static void Smooth(RemeshTopology topology, float[] values, int sweeps)
        {
            if (sweeps <= 0) return;

            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;
            var next = new float[values.Length];

            for (var sweep = 0; sweep < sweeps; sweep++)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var from = start[i];
                    var to = start[i + 1];
                    if (to <= from) { next[i] = values[i]; continue; }

                    var sum = values[i];
                    for (var k = from; k < to; k++) sum += values[adjacency[k]];
                    next[i] = sum / (to - from + 1);
                }

                Array.Copy(next, values, values.Length);
            }
        }

        /// <summary>
        /// Largest spacing ratio between two points one output cell apart. Zero or less uses
        /// <see cref="defaultGradation"/> per INPUT edge instead, the original clamp.
        /// </summary>
        /// <remarks>
        /// The per-edge clamp bounds the wrong quantity for the integer path. What has to agree is
        /// two neighbouring lattice CELLS, and a cell spans several input edges, so 1.15 per edge
        /// let the spacing change by 1.15^(cell/edge) across one quad -- 30 to 50 % on the body
        /// and the head, where the refined input has two to three edges per cell. Two cells that
        /// different cannot share an integer lattice: the rounding of the position field stops
        /// closing round the faces between them (unsatisfied constraints 1,900-2,350 on the body
        /// graded against 1,100-1,600 uniform), the flow routes the residue through long chains,
        /// the parametrisation folds (2,400-5,750 flipped faces before repair against 1,700-2,100
        /// uniform) and the greedy flip repair leaves bands of sheared quads behind it. Measured
        /// as the 5th percentile of each quad's smallest corner angle, the body went from 63.5
        /// degrees uniform -- QuadRemesher's own output on it is 62.9 -- to 54.1 graded.
        /// Measured as a default and REFUTED at fixed density (Adapt Quad Count off, mean of three
        /// counts per model, 5th-percentile corner angle): body 60.6 -> 57.1, head 35.6 -> 41.0,
        /// ccnb 32.5 -> 34.7. The body's apparent gain came from Adapt Quad Count reading the
        /// stricter field as needing fewer quads. Off by default; lower adaptivity
        /// (IntegerGridMap.adaptiveSizeScale) is what measured better on all three.
        /// </remarks>
        public static float gradationPerCell = 0f;

        /// <summary>
        /// <see cref="Grade"/> with the per-edge ratio derived from a per-cell one: an edge of
        /// length l at local spacing h may change the spacing by gradationPerCell^(l/h).
        /// </summary>
        static void GradePerCell(RemeshTopology topology, float[] values, float perCell, float cell)
        {
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;
            var positions = topology.positions;
            var logRatio = Mathf.Log(perCell);
            const int k_MaxSweeps = 400;

            for (var sweep = 0; sweep < k_MaxSweeps; sweep++)
            {
                var changed = false;
                for (var i = 0; i < values.Length; i++)
                {
                    var local = Mathf.Max(values[i] * cell, 1e-12f);
                    for (var k = start[i]; k < start[i + 1]; k++)
                    {
                        var j = adjacency[k];
                        var length = Vector3.Distance(positions[i], positions[j]);
                        var ceiling = values[i] * Mathf.Exp(logRatio * length / local);
                        if (values[j] <= ceiling) continue;
                        values[j] = ceiling;
                        changed = true;
                    }
                }
                if (!changed) return;
            }
        }

        /// <summary>
        /// Enforces <paramref name="gradation"/> as the largest ratio between the values at the two
        /// ends of any edge, by repeatedly LOWERING the coarser one.
        /// </summary>
        /// <remarks>
        /// Lowering the coarse side rather than raising the fine one, so the finest spacing in a
        /// neighbourhood wins and a detail is never coarsened away by its flat surroundings. That
        /// does propagate fineness outwards, which sounds like it would drag the whole mesh down to
        /// its sharpest point — but the propagation is geometric, growing by the gradation ratio
        /// per edge, so it reaches its surroundings within a few edges and stops. At 1.15 a spacing
        /// has doubled five edges out.
        ///
        /// Face count is unaffected either way: the normalisation runs AFTER this and solves for
        /// the multiplier that puts the graded field on the requested count.
        ///
        /// Iterated to a fixpoint with a cap. One sweep is not enough — lowering a value can
        /// violate the constraint against ITS other neighbour — and the number of sweeps needed is
        /// the graph distance the constraint has to travel, which no bound in the input predicts.
        /// </remarks>
        static void Grade(RemeshTopology topology, float[] values, float gradation)
        {
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;

            // Generous, and reached only on a pathological input. Measured on the character it
            // settles in under twenty.
            const int k_MaxSweeps = 200;

            for (var sweep = 0; sweep < k_MaxSweeps; sweep++)
            {
                var changed = false;

                for (var i = 0; i < values.Length; i++)
                {
                    for (var k = start[i]; k < start[i + 1]; k++)
                    {
                        var j = adjacency[k];
                        var ceiling = values[i] * gradation;
                        if (values[j] <= ceiling) continue;

                        // j is too coarse for i. Pull j down rather than push i up, so the finest
                        // spacing in a neighbourhood is the one that wins and detail is never
                        // coarsened away by its flat surroundings.
                        values[j] = ceiling;
                        changed = true;
                    }
                }

                if (!changed) return;
            }
        }

        static float Median(float[] values)
        {
            if (values.Length == 0) return 0f;
            var copy = (float[])values.Clone();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }
    }
}
