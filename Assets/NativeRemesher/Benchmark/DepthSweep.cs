// How deep the position field should be solved, swept rather than argued about.
//
// The hierarchy is worth a great deal on a scan and actively harmful on a capped cylinder, and the
// difference between those two cases is how coarse the requested lattice is relative to the input:
// the cylinder at target 2000 is clamped to the finest spacing its input supports, so its coarse
// levels have edges longer than a cell and nothing to say about where cell boundaries fall.
//
// positionSpacingMultiple is the cut. A level is solved only while
//
//     level average edge length * multiple <= lattice spacing
//
// so a LARGER multiple is a shallower hierarchy. This runs both cases at several values of it and
// prints the numbers, because the two pull in opposite directions and a single case cannot settle
// it. Sweeping is cheaper than one wrong guess: the first version of this cut was chosen by
// reasoning and cost 2 points of quad fraction on the asset it was meant to help.

using System;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class DepthSweep
    {
        static readonly float[] k_Multiples = { 4f, 2f, 1f, 0.5f, 0.25f, 0.125f };

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== DepthSweep ===");
            report.AppendLine("A larger multiple means a shallower position hierarchy.");
            report.AppendLine();

            SweepCylinder(report);
            SweepScan(report);

            report.AppendLine("=== end DepthSweep ===");
            UnityEngine.Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void SweepCylinder(StringBuilder report)
        {
            report.AppendLine("cylinder_capped @2000 (clamped to its input's finest spacing)");
            report.AppendLine("  multiple,posLevels,faces,quadFrac,irregular,hausdorff,chamfer,bndEdges,largestCap");

            foreach (var multiple in k_Multiples)
            {
                Mesh mesh = null;
                try
                {
                    mesh = MeshCorpus.CappedCylinder(0.5f, 2f, 96, 24);
                    var reference = PolyMesh.FromMesh(mesh);
                    var referenceCopy = new PolyMesh
                    {
                        positions = (Vector3[])reference.positions.Clone(),
                        faces = reference.faces,
                    };

                    var settings = RemeshSettings.Default;
                    settings.targetQuadCount = 2000;

                    var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
                    profile.positionSpacingMultiple = multiple;

                    var result = RemeshPipeline.Run(mesh, settings, profile);
                    var metrics = MeshMetrics.Measure(
                        PolyMesh.FromExtraction(result.extraction), referenceCopy, 20000);

                    report.AppendLine(
                        $"  {multiple},{result.positionLevels?.levels},{result.extraction.faceCount}," +
                        $"{result.extraction.quadFraction:F4},{metrics.irregularFraction:F4}," +
                        $"{metrics.hausdorffMax:F5},{metrics.chamferMean:F5}," +
                        $"{result.extraction.holeFillStats?.boundaryEdgesFound}," +
                        $"{result.extraction.holeFillStats?.largestCap}");
                }
                catch (Exception e)
                {
                    report.AppendLine($"  {multiple},threw: {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            report.AppendLine();
        }

        static void SweepScan(StringBuilder report)
        {
            var mesh = PerfProbe.LoadReferenceHigh(out var label);
            if (mesh == null)
            {
                report.AppendLine("reference asset not found, skipping");
                report.AppendLine();
                return;
            }

            RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);
            UnityEngine.Object.DestroyImmediate(mesh);

            report.AppendLine($"{label} @5474 (reference retopology: 5,474 faces, quadFrac 0.9289)");
            report.AppendLine("  multiple,posLevels,faces,quadFrac,irregularOfUsed,ngons,ms");

            foreach (var multiple in k_Multiples)
            {
                try
                {
                    var settings = RemeshSettings.Default;
                    settings.targetQuadCount = 5474;

                    var profile = RemeshProfile.For(RemeshProfileKind.Photogrammetry);
                    profile.positionSpacingMultiple = multiple;

                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
                    watch.Stop();

                    // Irregular over vertices a face actually uses. An unused vertex has valence
                    // zero and would be counted as irregular by anything reading the raw array.
                    var valence = new System.Collections.Generic.Dictionary<int, int>();
                    foreach (var face in result.extraction.faces)
                        foreach (var v in face)
                        {
                            valence.TryGetValue(v, out var d);
                            valence[v] = d + 1;
                        }

                    var irregular = 0;
                    foreach (var d in valence.Values)
                        if (d != 4) irregular++;

                    report.AppendLine(
                        $"  {multiple},{result.positionLevels?.levels},{result.extraction.faceCount}," +
                        $"{result.extraction.quadFraction:F4}," +
                        $"{(valence.Count > 0 ? irregular / (float)valence.Count : 0f):F4}," +
                        $"{result.extraction.ngonCount},{watch.ElapsedMilliseconds}");
                }
                catch (Exception e)
                {
                    report.AppendLine($"  {multiple},threw: {e.GetType().Name}: {e.Message}");
                }
            }

            report.AppendLine();
        }
    }
}
