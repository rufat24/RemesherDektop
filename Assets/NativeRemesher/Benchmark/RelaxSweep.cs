// Sweeps the relaxation pass on the character, at the reference's face count.
//
// WHAT TO READ, AND WHY THE COLUMN ORDER CHANGED. This sweep originally reported smoothness and
// deviation, both improved with every extra sweep, and on that evidence the pass shipped at 24
// iterations with no drift cap. It had amputated a thumb.
//
// Neither column could see that. Smoothness is computed over the faces that exist, and a mesh that
// walked its fingertip away has well-behaved faces everywhere it still is. Deviation measures OUR
// VERTICES to the INPUT SURFACE, which is near zero by construction — every vertex is placed on the
// surface and projected back onto it. Geometry can go missing without either number moving.
//
// So `uncovered%` is now the first column after the settings, and it is the one that decides. It is
// the fraction of the ORIGINAL surface with no output within half a quad: the other direction, and
// the only one that sees absence. Anything that improves smoothness while raising it is not an
// improvement, it is a mesh losing its extremities.
//
// `deepest%` sits beside it for the same reason one column short of the other: uncovered is an AREA
// fraction, so a hole the size of a fingertip moves it by thousandths and reads as noise, while
// deepest is a single worst case and says a thumb is gone rather than that the surface is loose.
//
// The reference: uncovered 0.146 %, deepest 0.755 %, >45 2.73 %, >90 0.17 %,
// minAngP01 47.1, curled 0.02 %, flipped 0.12 %. Relaxation off, under graded sizing:
// uncovered 0.007 %, deepest 0.326 %, >45 11.09 % — which is this pass's whole reason to exist and
// its whole risk in two numbers.

using System;
using System.Collections.Generic;
using System.Text;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class RelaxSweep
    {
        const string k_Original = "Benchmarks/reference/ref_high.obj";
        const int k_Target = 21_780;
        const int k_DecimateTo = 217_800;
        const int k_CoverageSamples = 200_000;

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== RelaxSweep (character @ the reference's face count) ===");
            report.AppendLine("  Reference: uncovered 0.146 %, >45 2.73 %, >90 0.17 %, " +
                              "minAngP01 47.1, curled 0.02 %, flipped 0.12 %");
            report.AppendLine();
            report.AppendLine("  iters,strength,drift,UNCOVERED%,deepest%,>45%,>90%,maxDihedral," +
                              "minAngP01,minAngP05,curled%,flipped%,deviation%,relaxMs");

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

                var grid = new SpatialGrid(originalPositions, originalTriangles);
                var diagonal = Diagonal(originalPositions);
                var samples = SpatialGrid.SampleSurface(originalPositions, originalTriangles,
                    k_CoverageSamples, 20260515);

                PreDecimate.Decimate(originalPositions, originalTriangles,
                    DecimateOptions.ForTriangleCount(k_DecimateTo),
                    out _, out var positions, out var triangles);

                // The drift cap is the axis under test. Iterations and strength are held at the
                // values the earlier sweep chose, plus one row raising iterations to show that a cap
                // makes a generous iteration count safe rather than merely slower.
                var configs = new (int iters, float strength, float drift)[]
                {
                    (0, 0f, 0f),          // off: the control that keeps the most geometry
                    (24, 0.5f, 0f),       // uncapped: what shipped, and what lost the thumb
                    (24, 0.5f, 1.0f),
                    (24, 0.5f, 0.5f),     // the default
                    (24, 0.5f, 0.4f),
                    (24, 0.5f, 0.35f),
                    (24, 0.5f, 0.25f),
                };

                var settings = RemeshSettings.Default;
                settings.targetQuadCount = k_Target;

                var baseProfile = RemeshProfile.For(RemeshProfileKind.Character);
                baseProfile.preDecimate = false;
                baseProfile.relaxSurface = false;

                // Solved ONCE; every row relaxes a fresh copy of that one result, so the rows are
                // independent and each can be quoted on its own.
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var solved = RemeshPipeline.Solve(positions, triangles, settings, baseProfile);
                report.AppendLine($"  solved once: {solved.extraction.faceCount:N0} faces, " +
                                  $"{watch.ElapsedMilliseconds:N0} ms");

                // Relaxation projects onto the surface the lattice was solved against - the
                // decimated one - exactly as the pipeline does, so a row here predicts a run.
                // `grid` is the ORIGINAL, and scores deviation and coverage, which must both be
                // measured against what the user supplied rather than our own intermediate.
                var surface = new SpatialGrid(positions, triangles);
                var pristine = (Vector3[])solved.extraction.positions.Clone();

                foreach (var config in configs)
                    Measure(report, solved, pristine, surface, grid, samples, diagonal,
                        config.iters, config.strength, config.drift);
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
            }

            report.AppendLine("=== end RelaxSweep ===");
            Emit(report);
        }

        static void Measure(StringBuilder report, RemeshResult solved, Vector3[] pristine,
            SpatialGrid surface, SpatialGrid grid, List<Vector3> samples, float diagonal,
            int iterations, float strength, float drift)
        {
            try
            {
                var positions = solved.extraction.positions;
                Array.Copy(pristine, positions, pristine.Length);

                RelaxStats stats = null;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                if (iterations > 0)
                    stats = SurfaceRelaxation.Run(solved.extraction.faces, positions, surface,
                        solved.scale, iterations, strength,
                        SurfaceRelaxation.defaultMaxProjection, SurfaceRelaxation.defaultConvergence,
                        0f, drift);
                var ms = watch.ElapsedMilliseconds;

                Uncovered(solved.extraction.faces, positions, samples, diagonal,
                    out var uncovered, out var deepest);

                Score(solved.extraction.faces, positions, grid, diagonal,
                    out var over45, out var over90, out var maxDihedral, out var angleP01,
                    out var angleP05, out var curled, out var flipped, out var deviation);

                report.AppendLine(
                    $"  {iterations},{strength:F2},{drift:F2}," +
                    $"{uncovered:F3},{deepest:F3},{over45:F2},{over90:F2},{maxDihedral:F1}," +
                    $"{angleP01:F1},{angleP05:F1},{curled:F2},{flipped:F2}," +
                    $"{deviation:F4},{ms}");

                if (stats != null) report.AppendLine($"      {stats}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {iterations},{strength:F2},{drift:F2},threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Percentage of samples on the ORIGINAL surface with no point of the output within half a
        /// quad edge, and how far away the worst-covered sample is. This is the measurement the
        /// pass was missing.
        /// </summary>
        /// <remarks>
        /// Both, because they fail differently. `uncovered` is an AREA fraction, so a hole the size
        /// of a fingertip moves it by thousandths and can be read as noise; `deepest` is a single
        /// worst case, so it is the one that says a thumb is gone rather than that the surface is
        /// slightly loose all over. The drift cap is chosen against the pair.
        /// </remarks>
        static void Uncovered(List<int[]> faces, Vector3[] positions, List<Vector3> samples,
            float diagonal, out float uncoveredPercent, out float deepestPercent)
        {
            var indices = new List<int>(faces.Count * 6);
            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;
                for (var k = 1; k + 1 < face.Length; k++)
                {
                    indices.Add(face[0]);
                    indices.Add(face[k]);
                    indices.Add(face[k + 1]);
                }
            }

            var grid = new SpatialGrid(positions, indices.ToArray());
            var threshold = 0.0025f * diagonal;

            var uncovered = 0;
            var deepest = 0f;
            foreach (var sample in samples)
            {
                var d = grid.NearestDistance(sample, out _);
                if (d > threshold) uncovered++;
                deepest = Mathf.Max(deepest, d);
            }

            uncoveredPercent = 100f * uncovered / Mathf.Max(1, samples.Count);
            deepestPercent = 100f * deepest / diagonal;
        }

        static void Score(List<int[]> faces, Vector3[] positions, SpatialGrid grid, float diagonal,
            out float over45, out float over90, out float maxDihedral, out float angleP01,
            out float angleP05, out float curledPercent, out float flippedPercent,
            out float deviationPercent)
        {
            var normals = new Vector3[faces.Count];
            for (var f = 0; f < faces.Count; f++)
                normals[f] = Normalize(Newell(positions, faces[f]));

            var edgeToFaces = new Dictionary<long, List<int>>(faces.Count * 4, EdgeKey.Comparer);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    if (!edgeToFaces.TryGetValue(key, out var list))
                        edgeToFaces[key] = list = new List<int>(2);
                    list.Add(f);
                }
            }

            var dihedral = new List<float>(edgeToFaces.Count);
            foreach (var pair in edgeToFaces.Values)
            {
                if (pair.Count != 2) continue;
                var a = normals[pair[0]];
                var b = normals[pair[1]];
                if (a == Vector3.zero || b == Vector3.zero) continue;
                dihedral.Add(Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f)) * Mathf.Rad2Deg);
            }

            over45 = 100f * Count(dihedral, 45f) / Mathf.Max(1, dihedral.Count);
            over90 = 100f * Count(dihedral, 90f) / Mathf.Max(1, dihedral.Count);
            maxDihedral = 0f;
            foreach (var d in dihedral) maxDihedral = Mathf.Max(maxDihedral, d);

            var angles = new float[faces.Count];
            var curled = 0;
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                var worst = 180f;
                var convex = true;
                for (var k = 0; k < face.Length; k++)
                {
                    var previous = positions[face[(k + face.Length - 1) % face.Length]];
                    var current = positions[face[k]];
                    var following = positions[face[(k + 1) % face.Length]];

                    var u = previous - current;
                    var v = following - current;
                    var lu = u.magnitude;
                    var lv = v.magnitude;
                    if (lu < 1e-12f || lv < 1e-12f) { worst = 0f; continue; }

                    worst = Mathf.Min(worst,
                        Mathf.Acos(Mathf.Clamp(Vector3.Dot(u, v) / (lu * lv), -1f, 1f)) * Mathf.Rad2Deg);

                    if (Vector3.Dot(Vector3.Cross(v, u), normals[f]) < 0f) convex = false;
                }
                if (!convex) curled++;
                angles[f] = worst;
            }
            Array.Sort(angles);
            angleP01 = Percentile(angles, 0.01f);
            angleP05 = Percentile(angles, 0.05f);
            curledPercent = 100f * curled / Mathf.Max(1, faces.Count);

            var flipped = 0;
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                var around = Vector3.zero;
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    foreach (var g in edgeToFaces[key])
                        if (g != f) around += normals[g];
                }
                if (around != Vector3.zero && Vector3.Dot(normals[f], around.normalized) < 0f)
                    flipped++;
            }
            flippedPercent = 100f * flipped / Mathf.Max(1, faces.Count);

            var total = 0f;
            foreach (var p in positions) total += grid.NearestDistance(p, out _);
            deviationPercent = 100f * total / Mathf.Max(1, positions.Length) / diagonal;
        }

        static int Count(List<float> values, float threshold)
        {
            var n = 0;
            foreach (var v in values) if (v > threshold) n++;
            return n;
        }

        static Vector3 Newell(Vector3[] positions, int[] face)
        {
            var normal = Vector3.zero;
            for (var k = 0; k < face.Length; k++)
            {
                var a = positions[face[k]];
                var b = positions[face[(k + 1) % face.Length]];
                normal += new Vector3(
                    (a.y - b.y) * (a.z + b.z),
                    (a.z - b.z) * (a.x + b.x),
                    (a.x - b.x) * (a.y + b.y));
            }
            return normal;
        }

        /// <summary>
        /// Explicit magnitude rather than <c>Vector3.normalized</c>, which returns zero below 1e-5
        /// and would zero every normal on this asset.
        /// </summary>
        static Vector3 Normalize(Vector3 v)
        {
            var m = v.magnitude;
            return m > 0f ? v / m : Vector3.zero;
        }

        static float Percentile(float[] sorted, float fraction)
        {
            if (sorted.Length == 0) return 0f;
            return sorted[Mathf.Clamp(Mathf.RoundToInt(fraction * (sorted.Length - 1)),
                0, sorted.Length - 1)];
        }

        static float Diagonal(Vector3[] positions)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            return (max - min).magnitude;
        }

        static void Emit(StringBuilder report)
        {
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
    }
}
