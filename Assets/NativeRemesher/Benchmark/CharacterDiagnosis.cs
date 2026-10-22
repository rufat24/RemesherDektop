// The character asset, and nothing else. Every symptom reported by eye, given a number.
//
// SCOPE IS DELIBERATE. Earlier work in this project tuned against a torus, an icosphere and a bumpy
// sphere and shipped two architectures whose failure mode could not appear on any of them. The only
// input here is `20260515221044_54b8f8f4` (as `Benchmarks/reference/ref_high.obj`), scored against
// the reference output the user actually looked at.
//
// THE SYMPTOMS THIS MEASURES, in the words they were reported in:
//
//   "some useless faces wrongly directed"  -> FLIPPED faces. Note the earlier normal metric took
//                                             the ABSOLUTE dot product, which masks a flip by
//                                             construction. That is measured separately here.
//   "some parts are curled"                -> non-convex / folded quads, which have a reflex
//                                             corner and shade as a crease
//   "some parts are popping out"            -> vertices well off the input surface, counted rather
//                                             than percentiled, because a handful is what is seen
//   "mesh looks like slimed"                -> loss of surface detail, i.e. the output is smoother
//                                             than the original
//   "maybe decimation destroys that"        -> tested directly: the decimated input is scored
//                                             against the original before any remeshing happens
//
// WHAT THE COMPETITOR DOES, read from its own bridge in this project rather than guessed:
//   - `QuadRemesherBridge.WriteSettingsFile` sends `CurvatureAdaptivness=50` by default, so half
//     the available curvature adaptivity is on. Ours is uniform.
//   - The bridge hands the engine `s_InputMesh` unchanged. It does NOT decimate. We decimate to
//     10 triangles per target quad first, discarding about 85 % of the input.

using System;
using System.Collections.Generic;
using System.Text;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class CharacterDiagnosis
    {
        const string k_Original = "Benchmarks/reference/ref_high.obj";
        const string k_Competitor = "Benchmarks/reference/qr-character.obj";

        /// <summary>The reference's own face count on this asset, so the arms are comparable.</summary>
        const int k_Target = 21_780;

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== CharacterDiagnosis (20260515221044_54b8f8f4 only) ===");

            Mesh original = null;
            try
            {
                original = ObjReader.Read(k_Original);
                RemeshTopology.ReadMesh(original, out var originalPositions,
                    out var originalTriangles);

                var grid = new SpatialGrid(originalPositions, originalTriangles);
                var diagonal = Diagonal(originalPositions);
                var spacing = RemeshTopology.Build(originalPositions, originalTriangles)
                    .EdgeLengthForQuadCount(k_Target);

                report.AppendLine($"  original: {originalPositions.Length:N0} verts, " +
                                  $"{originalTriangles.Length / 3:N0} tris, diagonal {diagonal:F4}, " +
                                  $"target spacing {spacing:F5}");
                report.AppendLine();

                // --- 1. is decimation the problem? ---
                // Scored BEFORE any remeshing: if the decimated input already deviates as much as
                // the finished output does, then nothing downstream can recover it and every other
                // measurement here is bounded by this one.
                report.AppendLine("  --- decimation, measured before remeshing ---");
                foreach (var budget in new[] { 217_800, 435_600, 0 })
                {
                    var positions = originalPositions;
                    var triangles = originalTriangles;

                    if (budget > 0 && triangles.Length / 3 > budget)
                        PreDecimate.Decimate(originalPositions, originalTriangles,
                            DecimateOptions.ForTriangleCount(budget),
                            out _, out positions, out triangles);

                    var deviation = SampleDeviation(grid, positions);
                    report.AppendLine(
                        $"      {(budget == 0 ? "no decimation" : budget.ToString("N0") + " tris"),18}: " +
                        $"{triangles.Length / 3,9:N0} tris, deviation from original " +
                        $"mean {deviation.mean / diagonal:P4} p99 {deviation.p99 / diagonal:P4} " +
                        $"max {deviation.max / diagonal:P4}");
                }

                report.AppendLine();

                // --- 2. the reference ---
                var competitorFaces = ObjReader.ReadPolygons(k_Competitor,
                    out var competitorRaw);
                Align(competitorRaw, originalPositions, out var competitorPositions);

                report.AppendLine("  --- reference ---");
                Score(report, competitorPositions, competitorFaces, grid, originalPositions,
                    originalTriangles, diagonal, spacing);

                // --- 3. ours, decimated as the profile does today ---
                report.AppendLine();
                report.AppendLine("  --- NativeRemesher, decimated to 217,800 tris (today) ---");
                Ours(report, originalPositions, originalTriangles, 217_800, grid,
                    diagonal, spacing);

                // --- 3b. ours with the triangle pairing OFF ---
                // `QuadRegularizer` refuses a non-convex merge; `TrianglePairing` scores its
                // tilings on minimum angle only and never checks convexity. So it may be the
                // source of the curled quads rather than a fix for anything, and that has to be
                // measured before its threshold is tuned any further.
                report.AppendLine();
                report.AppendLine("  --- NativeRemesher, triangle pairing OFF ---");
                Ours(report, originalPositions, originalTriangles, 217_800, grid,
                    diagonal, spacing, pairTriangles: false);

                // --- 4. full mesh (skipped here: 203 s, and measured to change little) ---
                report.AppendLine();
                report.AppendLine("  (full-mesh arm skipped: 203 s, moved curled 822 -> 723)");
                // Left out of the default run: it took 203 s against 37, and moved curled quads
                // only 822 -> 723. Re-enable when the question is specifically about decimation.

            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
            }

            report.AppendLine("=== end CharacterDiagnosis ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void Ours(StringBuilder report, Vector3[] originalPositions, int[] originalTriangles,
            int decimateTo, SpatialGrid grid, float diagonal, float spacing,
            bool pairTriangles = true)
        {
            var positions = originalPositions;
            var triangles = originalTriangles;

            if (decimateTo > 0 && triangles.Length / 3 > decimateTo)
                PreDecimate.Decimate(originalPositions, originalTriangles,
                    DecimateOptions.ForTriangleCount(decimateTo),
                    out _, out positions, out triangles);

            var settings = RemeshSettings.Default;
            settings.targetQuadCount = k_Target;

            var profile = RemeshProfile.For(RemeshProfileKind.Character);

            // Decimation is done above so both arms are driven the same way, and the profile's own
            // pre-decimation would otherwise apply on top of it.
            profile.preDecimate = false;
            profile.pairTrianglesAcrossQuads = pairTriangles;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
            var ms = watch.ElapsedMilliseconds;

            report.AppendLine($"      input {triangles.Length / 3:N0} tris, {ms:N0} ms");
            Score(report, result.extraction.positions, result.extraction.faces, grid,
                originalPositions, originalTriangles, diagonal, spacing);
        }

        static void Score(StringBuilder report, Vector3[] positions, List<int[]> faces,
            SpatialGrid grid, Vector3[] originalPositions, int[] originalTriangles,
            float diagonal, float spacing)
        {
            var quads = 0;
            var triangleCount = 0;
            var ngons = 0;

            // "Wrongly directed": the face's own normal opposes the input surface's normal there.
            // Measured SIGNED, unlike the earlier metric which took the absolute dot product and so
            // reported a flipped face as a perfect match.
            var flipped = 0;

            // "Curled": a quad with a reflex corner. It cannot be drawn flat, so it renders as two
            // triangles meeting at a fold and shades as a crease with no geometry behind it.
            var nonConvex = 0;

            var minAngles = new List<float>(faces.Count);
            var normalError = new List<float>(faces.Count);

            foreach (var face in faces)
            {
                if (face.Length == 4) quads++;
                else if (face.Length == 3) triangleCount++;
                else ngons++;

                var worst = 180f;
                var reflex = false;
                var faceNormal = Newell(positions, face);

                for (var k = 0; k < face.Length; k++)
                {
                    var previous = positions[face[(k + face.Length - 1) % face.Length]];
                    var current = positions[face[k]];
                    var following = positions[face[(k + 1) % face.Length]];

                    var u = previous - current;
                    var v = following - current;
                    var lengthU = u.magnitude;
                    var lengthV = v.magnitude;
                    if (lengthU < 1e-12f || lengthV < 1e-12f) { worst = 0f; continue; }

                    var cos = Mathf.Clamp(Vector3.Dot(u, v) / (lengthU * lengthV), -1f, 1f);
                    var angle = Mathf.Acos(cos) * Mathf.Rad2Deg;
                    if (angle < worst) worst = angle;

                    // Reflex test against the face's own plane: the corner turns the opposite way
                    // to the face as a whole.
                    if (faceNormal.sqrMagnitude > 1e-24f
                        && Vector3.Dot(Vector3.Cross(v, u), faceNormal) < 0f)
                        reflex = true;
                }

                minAngles.Add(worst);
                if (reflex) nonConvex++;

                var centroid = Vector3.zero;
                foreach (var corner in face) centroid += positions[corner];
                centroid /= face.Length;

                grid.NearestDistance(centroid, out var nearest);
                if (nearest < 0) continue;

                var a = originalPositions[originalTriangles[nearest * 3]];
                var b = originalPositions[originalTriangles[nearest * 3 + 1]];
                var c = originalPositions[originalTriangles[nearest * 3 + 2]];
                var reference = Vector3.Cross(b - a, c - a);

                var referenceLength = reference.magnitude;
                var faceLength = faceNormal.magnitude;
                if (referenceLength < 1e-20f || faceLength < 1e-20f) continue;

                // Divided explicitly: `Vector3.normalized` returns ZERO below 1e-5, and this
                // original's triangles have twice-area around 1e-6, which silently made every
                // normal comparison read 90 degrees.
                var dot = Mathf.Clamp(
                    Vector3.Dot(faceNormal, reference) / (faceLength * referenceLength), -1f, 1f);

                if (dot < 0f) flipped++;
                normalError.Add(Mathf.Acos(Mathf.Abs(dot)) * Mathf.Rad2Deg);
            }

            // "Popping out": counted, not percentiled. A few hundred vertices standing off the
            // surface is invisible in a p99 and is exactly what the eye catches.
            var off = 0;
            var wayOff = 0;
            var deviation = SampleDeviation(grid, positions);
            foreach (var p in positions)
            {
                var d = grid.NearestDistance(p, out _);
                if (d > spacing * 0.5f) off++;
                if (d > spacing) wayOff++;
            }

            var angles = minAngles.ToArray();
            Array.Sort(angles);
            var normals = normalError.ToArray();
            Array.Sort(normals);

            report.AppendLine($"      {faces.Count:N0} faces: {quads * 100f / faces.Count:F2} % quads, " +
                              $"{triangleCount:N0} tris, {ngons:N0} n-gons");
            report.AppendLine($"      WRONGLY DIRECTED faces: {flipped:N0} " +
                              $"({flipped * 100f / faces.Count:F2} %)");
            report.AppendLine($"      CURLED (non-convex) quads: {nonConvex:N0} " +
                              $"({nonConvex * 100f / faces.Count:F2} %)");
            report.AppendLine($"      POPPING OUT vertices: {off:N0} over half a quad off surface, " +
                              $"{wayOff:N0} over a full quad ({positions.Length:N0} total)");
            report.AppendLine($"      deviation: mean {deviation.mean / diagonal:P4} " +
                              $"p99 {deviation.p99 / diagonal:P4} max {deviation.max / diagonal:P4}");
            report.AppendLine($"      min angle: p01 {Percentile(angles, 0.01f):F1} " +
                              $"p05 {Percentile(angles, 0.05f):F1} " +
                              $"median {Percentile(angles, 0.5f):F1} worst {angles[0]:F1}");
            report.AppendLine($"      normal error: mean {Mean(normals):F1} " +
                              $"p95 {Percentile(normals, 0.95f):F1} p99 {Percentile(normals, 0.99f):F1}");
        }

        struct Deviation
        {
            public float mean;
            public float p99;
            public float max;
        }

        static Deviation SampleDeviation(SpatialGrid grid, Vector3[] positions)
        {
            // Every vertex on an output mesh; a stride on the dense input, where a million distance
            // queries would dominate the run and the distribution is what is wanted, not each value.
            var stride = Mathf.Max(1, positions.Length / 60_000);
            var samples = new List<float>(positions.Length / stride + 1);

            for (var i = 0; i < positions.Length; i += stride)
                samples.Add(grid.NearestDistance(positions[i], out _));

            var array = samples.ToArray();
            Array.Sort(array);

            return new Deviation
            {
                mean = Mean(array),
                p99 = Percentile(array, 0.99f),
                max = array.Length > 0 ? array[array.Length - 1] : 0f,
            };
        }

        static Vector3 Newell(Vector3[] positions, int[] face)
        {
            var normal = Vector3.zero;
            for (var k = 0; k < face.Length; k++)
            {
                var current = positions[face[k]];
                var following = positions[face[(k + 1) % face.Length]];
                normal.x += (current.y - following.y) * (current.z + following.z);
                normal.y += (current.z - following.z) * (current.x + following.x);
                normal.z += (current.x - following.x) * (current.y + following.y);
            }

            return normal;
        }

        static void Align(Vector3[] source, Vector3[] reference, out Vector3[] aligned)
        {
            // The reference's FBX is in centimetres, so its exported OBJ is 100x smaller. Left
            // uncorrected this reads as a constant 4.5 % deviation, which looks like a catastrophic
            // fidelity failure and is only the gap between two separated objects.
            Bounds(source, out var sourceMin, out var sourceMax);
            Bounds(reference, out var referenceMin, out var referenceMax);

            var scale = (referenceMax - referenceMin).magnitude
                        / Mathf.Max(1e-12f, (sourceMax - sourceMin).magnitude);
            var sourceCentre = (sourceMin + sourceMax) * 0.5f;
            var referenceCentre = (referenceMin + referenceMax) * 0.5f;

            aligned = new Vector3[source.Length];
            for (var i = 0; i < source.Length; i++)
                aligned[i] = (source[i] - sourceCentre) * scale + referenceCentre;
        }

        static void Bounds(Vector3[] positions, out Vector3 min, out Vector3 max)
        {
            min = positions.Length > 0 ? positions[0] : Vector3.zero;
            max = min;
            foreach (var p in positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        static float Diagonal(Vector3[] positions)
        {
            Bounds(positions, out var min, out var max);
            return Mathf.Max(1e-9f, (max - min).magnitude);
        }

        static float Mean(float[] values)
        {
            if (values.Length == 0) return 0f;
            var sum = 0.0;
            foreach (var v in values) sum += v;
            return (float)(sum / values.Length);
        }

        static float Percentile(float[] sorted, float fraction)
        {
            if (sorted.Length == 0) return 0f;
            return sorted[Mathf.Clamp(Mathf.RoundToInt(fraction * (sorted.Length - 1)),
                0, sorted.Length - 1)];
        }
    }
}
