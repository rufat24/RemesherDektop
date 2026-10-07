// Where has geometry gone MISSING, and at which stage did it go.
//
// WHY THIS EXISTS, and why every number reported so far could not answer it. Every deviation figure
// in this project measures OUR VERTICES to the ORIGINAL SURFACE. That direction is near zero by
// construction: extraction places output vertices on lattice points sampled at input vertices, so
// they sit on the input almost by definition, and relaxation projects them back onto it. A mesh
// that dropped an entire thumb would still score a perfect deviation, because the vertices it does
// have are all exactly where they should be.
//
// The missing half is the OTHER direction: from the ORIGINAL SURFACE to OUR MESH. A point on the
// original thumb has no nearby point on an output that lost the thumb, so that distance is large
// exactly where geometry is absent — and nowhere else. It is the only measurement here that can see
// a hole, a lost protrusion, or a thin feature collapsed flat.
//
// The stages are measured separately because "the thumb is missing" has several possible authors
// and they need different fixes:
//
//   decimated input     - QEM collapsed the thumb before the solver ever saw it
//   our output          - the lattice did, or extraction did
//   QuadRemesher        - the control: if theirs keeps it from the same original, it is ours to fix
//
// Worst samples are clustered and printed with coordinates, because "max 4 %" is not actionable and
// "a 0.03-wide blob at (0.42, 0.71, -0.05) holding 900 samples" is.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class CoverageAudit
    {
        const string k_Original = "Benchmarks/reference/ref_high.obj";
        const string k_Competitor = "Benchmarks/reference/qr-character.obj";
        const int k_Target = 21_780;
        const int k_DecimateTo = 217_800;

        /// <summary>Surface samples taken on the ORIGINAL. Enough to land many on a thumb.</summary>
        const int k_Samples = 400_000;

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== CoverageAudit (original -> output; the direction that sees MISSING geometry) ===");

            Mesh original = null;
            try
            {
                if (!System.IO.File.Exists(k_Original))
                {
                    report.AppendLine($"  missing {k_Original} - skipped");
                    Emit(report);
                    return;
                }

                original = ObjReader.Read(k_Original);
                RemeshTopology.ReadMesh(original, out var originalPositions, out var originalTriangles);

                var diagonal = Diagonal(originalPositions);
                report.AppendLine($"  original {originalTriangles.Length / 3:N0} tris, diagonal {diagonal:F4}");

                var samples = SpatialGrid.SampleSurface(originalPositions, originalTriangles, k_Samples, 20260515);
                report.AppendLine($"  {samples.Count:N0} samples on the original surface");
                report.AppendLine();

                // --- stage 1: does pre-decimation survive the thumb? ---
                PreDecimate.Decimate(originalPositions, originalTriangles,
                    DecimateOptions.ForTriangleCount(k_DecimateTo),
                    out _, out var decimatedPositions, out var decimatedTriangles);

                Score(report, "decimated input (217,800 tris)", decimatedPositions, decimatedTriangles,
                    samples, diagonal);

                // --- stage 2: ours ---
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = k_Target;

                var profile = RemeshProfile.For(RemeshProfileKind.Character);
                profile.preDecimate = false;

                // UNIFORM, as the control. Curvature-adaptive sizing is the change under test and
                // the arm it has to beat is the one that was shipping, not a hypothetical.
                var uniformProfile = profile;
                uniformProfile.curvatureAdaptivity = 0f;
                var uniform = RemeshPipeline.Solve(decimatedPositions, decimatedTriangles, settings, uniformProfile);
                Triangulate(uniform.extraction.faces, out var uniformIndices);
                Score(report, $"NativeRemesher, UNIFORM sizing ({uniform.extraction.faceCount:N0} faces)",
                    uniform.extraction.positions, uniformIndices, samples, diagonal);

                var ours = RemeshPipeline.Solve(decimatedPositions, decimatedTriangles, settings, profile);
                Triangulate(ours.extraction.faces, out var ourIndices);
                report.AppendLine($"      sizing: {ours.scaleField}");
                Score(report, $"NativeRemesher, GRADED sizing ({ours.extraction.faceCount:N0} faces)",
                    ours.extraction.positions, ourIndices, samples, diagonal);

                // Relaxation off, so a coverage loss can be attributed to it or cleared of it.
                var noRelax = profile;

                noRelax.relaxSurface = false;
                var plain = RemeshPipeline.Solve(decimatedPositions, decimatedTriangles, settings, noRelax);
                Triangulate(plain.extraction.faces, out var plainIndices);
                Score(report, $"NativeRemesher, relaxation OFF ({plain.extraction.faceCount:N0} faces)",
                    plain.extraction.positions, plainIndices, samples, diagonal);

                // The question this arm answers: does pre-decimation destroy the detail? It is a
                // reasonable suspicion and the coverage metric is the first one here able to test
                // it, since detail loss is exactly "surface with nothing near it". Same target,
                // same everything, 1.5 M triangles straight into the solver.
                // --- stage 3: the control ---
                if (System.IO.File.Exists(k_Competitor))
                {
                    var competitorFaces = ObjReader.ReadPolygons(k_Competitor, out var competitorPositions);
                    Align(competitorPositions, originalPositions, out var aligned);
                    Triangulate(competitorFaces, out var competitorIndices);
                    Score(report, $"QuadRemesher ({competitorFaces.Count:N0} faces)",
                        aligned, competitorIndices, samples, diagonal);
                }
                else
                {
                    report.AppendLine($"  {k_Competitor} missing - no control arm");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
            }

            report.AppendLine("=== end CoverageAudit ===");
            Emit(report);
        }

        static void Score(StringBuilder report, string label, Vector3[] positions, int[] triangles,
            List<Vector3> samples, float diagonal)
        {
            var grid = new SpatialGrid(positions, triangles);

            var distances = new float[samples.Count];
            for (var i = 0; i < samples.Count; i++)
                distances[i] = grid.NearestDistance(samples[i], out _);

            var sorted = (float[])distances.Clone();
            Array.Sort(sorted);

            // A quarter of a percent of the diagonal is about half a quad edge at this face count.
            // Anything beyond that is not approximation error, it is absence.
            var uncoveredAt = 0.0025f * diagonal;
            var uncovered = 0;
            foreach (var d in distances) if (d > uncoveredAt) uncovered++;

            report.AppendLine($"  --- {label} ---");
            report.AppendLine(
                $"      original -> output: mean {100f * Mean(sorted) / diagonal:F4} % " +
                $"p99 {100f * Percentile(sorted, 0.99f) / diagonal:F4} % " +
                $"p999 {100f * Percentile(sorted, 0.999f) / diagonal:F4} % " +
                $"max {100f * sorted[sorted.Length - 1] / diagonal:F4} %");
            report.AppendLine(
                $"      UNCOVERED (further than 0.25 % of diagonal): {uncovered:N0} " +
                $"of {samples.Count:N0} ({100f * uncovered / samples.Count:F3} %)");

            Blobs(report, samples, distances, uncoveredAt, diagonal);
        }

        /// <summary>
        /// Groups the uncovered samples into spatial blobs and prints the largest few. A single max
        /// tells you a defect exists; this tells you where it is and how big, which is the
        /// difference between a number and something that can be looked at in the scene.
        /// </summary>
        static void Blobs(StringBuilder report, List<Vector3> samples, float[] distances,
            float threshold, float diagonal)
        {
            // Bucket to a coarse grid, then merge buckets that touch. Cheap, and blob-finding does
            // not need to be better than the thing being found.
            var cell = 0.01f * diagonal;
            var buckets = new Dictionary<long, List<int>>();

            for (var i = 0; i < distances.Length; i++)
            {
                if (distances[i] <= threshold) continue;
                var p = samples[i];
                var key = Key(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell),
                    Mathf.FloorToInt(p.z / cell));
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                list.Add(i);
            }

            if (buckets.Count == 0)
            {
                report.AppendLine("      no uncovered blobs");
                return;
            }

            var seen = new HashSet<long>();
            var blobs = new List<(int count, Vector3 centre, float worst, Vector3 min, Vector3 max)>();
            var queue = new Queue<long>();

            foreach (var seed in buckets.Keys)
            {
                if (!seen.Add(seed)) continue;

                var members = new List<int>();
                queue.Clear();
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    var key = queue.Dequeue();
                    members.AddRange(buckets[key]);

                    Unkey(key, out var x, out var y, out var z);
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dz = -1; dz <= 1; dz++)
                            {
                                var neighbour = Key(x + dx, y + dy, z + dz);
                                if (!buckets.ContainsKey(neighbour)) continue;
                                if (seen.Add(neighbour)) queue.Enqueue(neighbour);
                            }
                }

                var centre = Vector3.zero;
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                var worst = 0f;
                foreach (var i in members)
                {
                    centre += samples[i];
                    min = Vector3.Min(min, samples[i]);
                    max = Vector3.Max(max, samples[i]);
                    worst = Mathf.Max(worst, distances[i]);
                }
                blobs.Add((members.Count, centre / members.Count, worst, min, max));
            }

            blobs.Sort((a, b) => b.count.CompareTo(a.count));

            report.AppendLine($"      {blobs.Count} uncovered blob(s); largest:");
            for (var i = 0; i < Mathf.Min(8, blobs.Count); i++)
            {
                var b = blobs[i];
                var size = b.max - b.min;
                report.AppendLine(
                    $"        {b.count,6:N0} samples at ({b.centre.x:F3}, {b.centre.y:F3}, {b.centre.z:F3}) " +
                    $"extent ({size.x:F3}, {size.y:F3}, {size.z:F3}) " +
                    $"worst {100f * b.worst / diagonal:F3} %");
            }
        }

        static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (uint)(z & 0x1FFFFF);
        }

        static void Unkey(long key, out int x, out int y, out int z)
        {
            x = Sign((int)((key >> 42) & 0x1FFFFF));
            y = Sign((int)((key >> 21) & 0x1FFFFF));
            z = Sign((int)(key & 0x1FFFFF));
        }

        static int Sign(int v) => v >= 0x100000 ? v - 0x200000 : v;

        /// <summary>Fan-triangulates polygon faces so a SpatialGrid can be built over them.</summary>
        static void Triangulate(List<int[]> faces, out int[] triangles)
        {
            var list = new List<int>(faces.Count * 6);
            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;
                for (var k = 1; k + 1 < face.Length; k++)
                {
                    list.Add(face[0]);
                    list.Add(face[k]);
                    list.Add(face[k + 1]);
                }
            }
            triangles = list.ToArray();
        }

        /// <summary>
        /// QuadRemesher's FBX is in centimetres, so its exported OBJ is 100x smaller. Aligned by
        /// uniform scale about the centroid; the bbox side ratios are printed nowhere here because
        /// FidelityComparison already verified they match to 0.0007.
        /// </summary>
        static void Align(Vector3[] source, Vector3[] reference, out Vector3[] aligned)
        {
            Bounds(source, out var sourceMin, out var sourceMax);
            Bounds(reference, out var referenceMin, out var referenceMax);

            var sourceSize = sourceMax - sourceMin;
            var referenceSize = referenceMax - referenceMin;

            var scale = Mathf.Max(sourceSize.x, Mathf.Max(sourceSize.y, sourceSize.z)) > 1e-20f
                ? Mathf.Max(referenceSize.x, Mathf.Max(referenceSize.y, referenceSize.z)) /
                  Mathf.Max(sourceSize.x, Mathf.Max(sourceSize.y, sourceSize.z))
                : 1f;

            var sourceCentre = (sourceMin + sourceMax) * 0.5f;
            var referenceCentre = (referenceMin + referenceMax) * 0.5f;

            aligned = new Vector3[source.Length];
            for (var i = 0; i < source.Length; i++)
                aligned[i] = (source[i] - sourceCentre) * scale + referenceCentre;
        }

        static void Bounds(Vector3[] positions, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        }

        static float Diagonal(Vector3[] positions)
        {
            Bounds(positions, out var min, out var max);
            return (max - min).magnitude;
        }

        static float Mean(float[] values)
        {
            if (values.Length == 0) return 0f;
            var total = 0.0;
            foreach (var v in values) total += v;
            return (float)(total / values.Length);
        }

        static float Percentile(float[] sorted, float fraction)
        {
            if (sorted.Length == 0) return 0f;
            return sorted[Mathf.Clamp(Mathf.RoundToInt(fraction * (sorted.Length - 1)),
                0, sorted.Length - 1)];
        }

        static void Emit(StringBuilder report)
        {
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
    }
}
