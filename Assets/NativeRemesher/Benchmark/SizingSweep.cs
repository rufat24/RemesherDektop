// Sweeps curvature-adaptive sizing on the character, against BOTH things it trades between.
//
// WHY BOTH COLUMNS. Grading buys coverage — the thumb tip is resolved because the quads there are
// smaller — and it costs lattice quality, because two neighbouring cells of different sizes can
// never coincide exactly the way two equal ones can, so Compat leaves a residual and the edge
// classification downstream gets noisier. Measured at adaptivity 0.5 on the character, against
// uniform: deepest uncovered hole 1.03 % to 0.44 %, and edges folded past 45 degrees 2.94 % to
// 5.16 %. Each of those on its own would justify a decision, and they point opposite ways.
//
// So `uncovered%` and `>45%` sit next to each other and the value is chosen from the pair. A sweep
// of either alone picks a setting that is clearly wrong on the other, which is exactly how this
// project shipped a pass that amputated a thumb while every number it was tuned against improved.
//
// The reference: uncovered 0.146 %, deepest hole 0.755 %, >45 2.73 %, >90 0.17 %,
// minAngle p01 47.1, curled 0.02 %, 99.99 % quads, spacing spread 4.0x.

using System;
using System.Collections.Generic;
using System.Text;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class SizingSweep
    {
        const string k_Original = "Benchmarks/reference/ref_high.obj";
        const int k_Target = 21_780;
        const int k_DecimateTo = 217_800;
        const int k_CoverageSamples = 200_000;

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== SizingSweep (character @ the reference's face count) ===");
            report.AppendLine("  Reference: uncovered 0.146 %, deepest 0.755 %, >45 2.73 %, " +
                              ">90 0.17 %, minAngP01 47.1, curled 0.02 %, quads 99.99 %, spread 4.0x");
            report.AppendLine();
            report.AppendLine("  adapt,maxRatio,grade,spread,faces,quad%,UNCOVERED%,deepest%," +
                              ">45%,>90%,minAngP01,curled%,flipped%,ms");

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
                var samples = SpatialGrid.SampleSurface(originalPositions, originalTriangles,
                    k_CoverageSamples, 20260515);

                PreDecimate.Decimate(originalPositions, originalTriangles,
                    DecimateOptions.ForTriangleCount(k_DecimateTo),
                    out _, out var positions, out var triangles);

                var configs = new (float adapt, float maxRatio, float grade)[]
                {
                    (0f, 0f, 0f),           // uniform: the control
                    (0.25f, 3f, 1.3f),
                    (0.5f, 3f, 1.3f),       // the first default, and the reference's equivalent
                    (0.75f, 3f, 1.3f),
                    (0.5f, 2f, 1.3f),       // gentler clamp
                    (0.5f, 3f, 1.15f),      // gentler gradation
                    (0.25f, 2f, 1.15f),     // gentle on both
                    (1f, 3f, 1.3f),
                };

                foreach (var config in configs)
                    Measure(report, positions, triangles, samples, diagonal,
                        config.adapt, config.maxRatio, config.grade);
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
            }

            report.AppendLine("=== end SizingSweep ===");
            Emit(report);
        }

        static void Measure(StringBuilder report, Vector3[] positions, int[] triangles,
            List<Vector3> samples, float diagonal, float adaptivity, float maxRatio, float gradation)
        {
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = k_Target;

                var profile = RemeshProfile.For(RemeshProfileKind.Character);
                profile.preDecimate = false;
                profile.curvatureAdaptivity = adaptivity;
                if (maxRatio > 1f) profile.curvatureMaxRatio = maxRatio;
                if (gradation > 1f) profile.curvatureGradation = gradation;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
                var ms = watch.ElapsedMilliseconds;

                Coverage(result.extraction.faces, result.extraction.positions, samples, diagonal,
                    out var uncovered, out var deepest);

                Score(result.extraction.faces, result.extraction.positions,
                    out var over45, out var over90, out var angleP01, out var curled, out var flipped);

                var spread = result.scaleField?.spread ?? 1f;

                report.AppendLine(
                    $"  {adaptivity:F2},{maxRatio:F1},{gradation:F2},{spread:F2}," +
                    $"{result.extraction.faceCount},{result.extraction.quadFraction * 100f:F2}," +
                    $"{uncovered:F3},{deepest:F3},{over45:F2},{over90:F2}," +
                    $"{angleP01:F1},{curled:F2},{flipped:F2},{ms}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {adaptivity:F2},{maxRatio:F1},{gradation:F2},threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Fraction of the ORIGINAL surface with no output within half a quad, and how far the
        /// worst-covered sample is. The second is the one that says "a thumb tip is missing" rather
        /// than "the surface is slightly loose all over".
        /// </summary>
        static void Coverage(List<int[]> faces, Vector3[] positions, List<Vector3> samples,
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

        static void Score(List<int[]> faces, Vector3[] positions,
            out float over45, out float over90, out float angleP01, out float curledPercent,
            out float flippedPercent)
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

            var over45Count = 0;
            var over90Count = 0;
            var pairs = 0;
            foreach (var pair in edgeToFaces.Values)
            {
                if (pair.Count != 2) continue;
                var a = normals[pair[0]];
                var b = normals[pair[1]];
                if (a == Vector3.zero || b == Vector3.zero) continue;

                var angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f)) * Mathf.Rad2Deg;
                pairs++;
                if (angle > 45f) over45Count++;
                if (angle > 90f) over90Count++;
            }

            over45 = 100f * over45Count / Mathf.Max(1, pairs);
            over90 = 100f * over90Count / Mathf.Max(1, pairs);

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
            angleP01 = angles.Length > 0
                ? angles[Mathf.Clamp(Mathf.RoundToInt(0.01f * (angles.Length - 1)), 0, angles.Length - 1)]
                : 0f;
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
