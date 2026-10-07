// Does anything we have preserve a hard edge, and is pre-decimation what destroys it?
//
// THE MISDIAGNOSIS THIS EXISTS TO CORRECT. The reference asset was treated as a photogrammetry scan
// of a human body for the whole of this project. It is not. It is a soldier in body armour: a plate
// carrier with MOLLE webbing, straps, buckles, a belt with pouches, a collar. The surface is covered
// in sharp panel edges, and the competitor's output keeps them while ours renders as a melted
// mannequin.
//
// Every profile benchmarked on it - Character and Photogrammetry - sets featureAngleDegrees = 0.
// Feature detection OFF, on a model that is almost entirely features. The written-up rule "grade
// only where feature detection is off, scan profiles declare no creases" was derived on this asset
// and is founded on the same mistake.
//
// WHY NO METRIC CAUGHT IT. Rounding a crease moves a handful of vertices by a fraction of a quad.
// Mean deviation barely registers it - ours measures CLOSER to the original than the competitor's -
// and coverage cannot see it at all, because a rounded crease leaves nothing uncovered. The defect
// is entirely in where the few available quads are PLACED, and the project had no measurement of
// that. Hence renders (Benchmarks/reference/render.py) alongside the numbers from here on.
//
// WHAT THIS ASKS. Pre-decimation was cleared once, on coverage and irregular vertices - both of
// which are blind to creases in exactly the way described above. At 10 triangles per quad this
// asset goes from 1,500,000 triangles to about 50,000 before the solver sees it, and a MOLLE strap
// is a centimetre-scale feature on a figure nearly two metres tall. So the clearance is re-run with
// a measurement that can see a crease: how much of the input's feature graph survives into the
// output, and an OBJ to look at.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class FeatureProbe
    {
        const string k_Character = "Benchmarks/reference/ref_high.obj";

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== FeatureProbe ===");

            try
            {
                if (!System.IO.File.Exists(k_Character))
                {
                    report.AppendLine($"  {k_Character} missing - see Headless/README.md.");
                }
                else
                {
                    var mesh = ObjReader.Read(k_Character);
                    RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);

                    report.AppendLine("  case,faces,quad%,tris,inputFeatureVerts," +
                                      "creaseKept%,meanCreaseGap%,ms");

                    // Decimation on and off, at the two profiles that declare creases at all. If
                    // the pre-decimator is what removes the panel edges, the off rows keep them and
                    // the on rows do not, and the renders say so at a glance.
                    Measure(report, positions, triangles, 4955, RemeshProfileKind.HardSurface, true);

                    // ADAPTIVITY, which is the thing the competitor does and we do not. Its default
                    // is CurvatureAdaptivness = 50 and it ships that way even on scans; ours is 0 on
                    // every profile, so our quads are the same size on a webbing strap as on a
                    // thigh. Measured on the delivered files, its edge lengths vary far more than
                    // ours - coefficient of variation 0.39 against 0.22, aspect 1.82 against 1.28 -
                    // which is what spending a fixed budget where the detail is looks like.
                    //
                    // This was swept once before and scored half a point, on flagged faces and hand
                    // clusters. Both are blind to a rounded crease in exactly the way mean deviation
                    // is, and the asset was believed to be an organic scan at the time. Re-run with
                    // a crease-aware column and a render.
                    foreach (var adaptivity in new[] { 0.25f, 0.5f, 0.75f })
                        Measure(report, positions, triangles, 4955, RemeshProfileKind.HardSurface,
                            true, adaptivity);
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }

            report.AppendLine("=== end FeatureProbe ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void Measure(StringBuilder report, Vector3[] positions, int[] triangles, int target,
            RemeshProfileKind kind, bool decimate, float adaptivity = -1f)
        {
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = target;

                var profile = RemeshProfile.For(kind);
                profile.preDecimate = decimate;
                if (adaptivity >= 0f) profile.curvatureAdaptivity = adaptivity;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
                var ms = watch.ElapsedMilliseconds;

                var tris = 0;
                foreach (var face in result.extraction.faces)
                    if (face != null && face.Length == 3) tris++;

                var inputFeatures = 0;
                var featurePoints = new List<Vector3>();
                if (result.features?.isFeatureVertex != null)
                {
                    var flags = result.features.isFeatureVertex;
                    for (var i = 0; i < flags.Length; i++)
                    {
                        if (!flags[i]) continue;
                        inputFeatures++;
                        if (i < result.topology.positions.Length)
                            featurePoints.Add(result.topology.positions[i]);
                    }
                }

                // THE MEASUREMENT THAT WAS MISSING. For every vertex the input called a crease, how
                // far is the nearest OUTPUT vertex? A crease that survived has an output vertex
                // sitting on it; a crease that was rounded off has the output passing smoothly by at
                // a distance. Distances in percent of the bounding box diagonal, and "kept" means
                // within a fifth of a quad - close enough that an edge loop is actually following it.
                var kept = 0f;
                var meanGap = 0f;
                if (featurePoints.Count > 0)
                {
                    var diagonal = Diagonal(positions);
                    var grid = new SpatialGrid(result.extraction.positions,
                        PointIndices(result.extraction.positions.Length));
                    var near = 0;
                    var total = 0.0;
                    var threshold = 0.2f * result.scale;

                    foreach (var p in featurePoints)
                    {
                        var d = grid.NearestDistance(p, out _);
                        total += d;
                        if (d <= threshold) near++;
                    }

                    kept = 100f * near / featurePoints.Count;
                    meanGap = (float)(100.0 * total / featurePoints.Count / diagonal);
                }

                report.AppendLine(
                    $"  {kind}{(decimate ? "" : "+noDecimate")}" +
                    $"{(adaptivity >= 0f ? $"+a{adaptivity:F2}" : "")},{result.extraction.faceCount}," +
                    $"{result.extraction.quadFraction * 100f:F2},{tris},{inputFeatures}," +
                    $"{kept:F1},{meanGap:F4},{ms}");

                WriteObj($"Benchmarks/reference/feat-{kind}{(decimate ? "" : "-nodec")}" +
                        $"{(adaptivity >= 0f ? $"-a{adaptivity:F2}" : "")}.obj", result);
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {kind},threw: {ex.Message}");
            }
        }

        /// <summary>Degenerate triangles over every vertex, so the grid can answer nearest-VERTEX.</summary>
        static int[] PointIndices(int count)
        {
            var indices = new int[count * 3];
            for (var i = 0; i < count; i++)
            {
                indices[i * 3] = i;
                indices[i * 3 + 1] = i;
                indices[i * 3 + 2] = i;
            }

            return indices;
        }

        static float Diagonal(Vector3[] positions)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            return (max - min).magnitude;
        }

        static void WriteObj(string path, RemeshResult result)
        {
            var sb = new StringBuilder();
            var c = CultureInfo.InvariantCulture;

            foreach (var p in result.extraction.positions)
                sb.Append("v ").Append(p.x.ToString("F6", c)).Append(' ')
                  .Append(p.y.ToString("F6", c)).Append(' ')
                  .Append(p.z.ToString("F6", c)).Append('\n');

            foreach (var face in result.extraction.faces)
            {
                if (face == null || face.Length < 3) continue;
                sb.Append('f');
                foreach (var i in face) sb.Append(' ').Append(i + 1);
                sb.Append('\n');
            }

            System.IO.File.WriteAllText(path, sb.ToString());
        }
    }
}
