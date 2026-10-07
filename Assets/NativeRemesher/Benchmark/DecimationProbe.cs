// Does decimation destroy the fingers, at the density the user actually asked for?
//
// WHY THIS IS BEING ASKED AGAIN. It was tested once and cleared: the decimated input on its own left
// 0.000 % of the surface uncovered, so decimation was ruled out as the cause of the missing detail.
// That test was run at 21,780 quads, where the budget is 217,800 triangles out of 1,500,000 - a
// factor of seven. The asset the user is actually looking at is 5,215 quads, where the same rule
// gives 52,150 triangles: a factor of TWENTY-NINE. A conclusion measured at 7x was quietly carried
// over to 29x, and a finger is about the size of thing that stops surviving somewhere in between.
//
// The competitor does not decimate at all - its bridge hands the engine the mesh unchanged, all
// 1.5 M triangles of it (Benchmarks/quadremesher-approach.md). So this is also the one place where
// our architecture spends something theirs does not.
//
// WHAT IS MEASURED, and in which direction. Coverage of the ORIGINAL by the DECIMATED input, before
// any remeshing happens. Deviation the other way - decimated vertices to the original surface - is
// near zero by construction for a QEM decimator and cannot see a finger that is gone. Two numbers:
// the fraction of the original surface with nothing within half a target quad, and the single
// worst-covered point, which is the one that says "a thumb is missing" rather than "everything
// moved slightly".
//
// The worst-covered samples are then clustered and located, so the answer is not just how much was
// lost but WHERE - which is the whole reason this file exists rather than another average.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class DecimationProbe
    {
        const string k_Original = "Benchmarks/reference/ref_high.obj";

        /// <summary>The delivered asset's face count, which is where the complaint is.</summary>
        const int k_Target = 5_215;

        const int k_Samples = 400_000;

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== DecimationProbe (character @ the delivered face count) ===");
            report.AppendLine("  Coverage of the ORIGINAL surface by the DECIMATED INPUT, before any");
            report.AppendLine("  remeshing. The competitor decimates not at all.");
            report.AppendLine();

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
                RemeshTopology.ReadMesh(original, out var positions, out var triangles);

                var diagonal = Diagonal(positions, out var lo, out var hi);
                var samples = SpatialGrid.SampleSurface(positions, triangles, k_Samples, 20260515);

                // Half a target quad: the same threshold every coverage measurement in this repo
                // uses, so the numbers are comparable with the ones on the output.
                var spacing = Mathf.Sqrt(SurfaceArea(positions, triangles) / k_Target);
                var threshold = 0.5f * spacing;

                report.AppendLine($"  input {triangles.Length / 3:N0} triangles, diagonal {diagonal:F4}");
                report.AppendLine($"  target quad edge {spacing:F5} ({100f * spacing / diagonal:F2} %% " +
                                  $"of the diagonal), so a gap counts at {threshold:F5}");
                report.AppendLine();
                report.AppendLine("  budget/quad,triangles,UNCOVERED%,deepest%,decimateMs");

                foreach (var budget in new[] { 10, 20, 40, 100, 0 })
                    Measure(report, positions, triangles, samples, diagonal, threshold, lo, hi, budget);
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
            }

            report.AppendLine("=== end DecimationProbe ===");
            Emit(report);
        }

        static void Measure(StringBuilder report, Vector3[] positions, int[] triangles,
            List<Vector3> samples, float diagonal, float threshold, Vector3 lo, Vector3 hi, int budget)
        {
            try
            {
                Vector3[] decimated;
                int[] decimatedTriangles;
                long ms = 0;

                if (budget <= 0)
                {
                    decimated = positions;
                    decimatedTriangles = triangles;
                }
                else
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    PreDecimate.Decimate(positions, triangles,
                        DecimateOptions.ForTriangleCount(budget * k_Target),
                        out _, out decimated, out decimatedTriangles);
                    ms = watch.ElapsedMilliseconds;
                }

                var grid = new SpatialGrid(decimated, decimatedTriangles);

                var uncovered = 0;
                var deepest = 0f;
                var worst = new List<Vector3>();
                foreach (var sample in samples)
                {
                    var d = grid.NearestDistance(sample, out _);
                    if (d > threshold)
                    {
                        uncovered++;
                        worst.Add(sample);
                    }
                    deepest = Mathf.Max(deepest, d);
                }

                report.AppendLine(
                    $"  {(budget <= 0 ? "none" : budget.ToString())}," +
                    $"{decimatedTriangles.Length / 3},{100f * uncovered / samples.Count:F3}," +
                    $"{100f * deepest / diagonal:F3},{ms}");

                Locate(report, worst, lo, hi, diagonal);
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {budget},threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Groups the uncovered samples into blobs and says where each one is, as a percentage of
        /// the bounding box so it lines up with the output audit in Benchmarks/reference/defects.py.
        /// </summary>
        static void Locate(StringBuilder report, List<Vector3> points, Vector3 lo, Vector3 hi,
            float diagonal)
        {
            if (points.Count == 0) return;

            // Single-link clustering at a twentieth of the diagonal, through a grid so it does not
            // cost a comparison per pair - there can be thousands of points.
            var cell = 0.02f * diagonal;
            var buckets = new Dictionary<(int, int, int), List<int>>();
            for (var i = 0; i < points.Count; i++)
            {
                var key = Key(points[i], cell);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                list.Add(i);
            }

            var seen = new bool[points.Count];
            var clusters = new List<List<int>>();

            for (var i = 0; i < points.Count; i++)
            {
                if (seen[i]) continue;

                var group = new List<int>();
                var stack = new Stack<int>();
                stack.Push(i);
                seen[i] = true;

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    group.Add(current);

                    var key = Key(points[current], cell);
                    for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (!buckets.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz),
                                out var list)) continue;

                        foreach (var j in list)
                        {
                            if (seen[j]) continue;
                            if ((points[j] - points[current]).sqrMagnitude > cell * cell) continue;
                            seen[j] = true;
                            stack.Push(j);
                        }
                    }
                }

                clusters.Add(group);
            }

            clusters.Sort((a, b) => b.Count.CompareTo(a.Count));

            var size = hi - lo;
            var shown = 0;
            foreach (var cluster in clusters)
            {
                if (cluster.Count < 20 || shown >= 6) break;

                var mid = Vector3.zero;
                foreach (var i in cluster) mid += points[i];
                mid /= cluster.Count;

                report.AppendLine(string.Format(
                    "      {0,6} samples at ({1:F0},{2:F0},{3:F0})%% of bbox",
                    cluster.Count,
                    100f * (mid.x - lo.x) / Mathf.Max(1e-9f, size.x),
                    100f * (mid.y - lo.y) / Mathf.Max(1e-9f, size.y),
                    100f * (mid.z - lo.z) / Mathf.Max(1e-9f, size.z)));
                shown++;
            }
        }

        static (int, int, int) Key(Vector3 p, float cell) =>
            (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));

        static float SurfaceArea(Vector3[] positions, int[] triangles)
        {
            var total = 0f;
            for (var i = 0; i + 2 < triangles.Length; i += 3)
                total += Vector3.Cross(positions[triangles[i + 1]] - positions[triangles[i]],
                    positions[triangles[i + 2]] - positions[triangles[i]]).magnitude * 0.5f;
            return total;
        }

        static float Diagonal(Vector3[] positions, out Vector3 lo, out Vector3 hi)
        {
            lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in positions) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            return (hi - lo).magnitude;
        }

        static void Emit(StringBuilder report)
        {
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
    }
}
