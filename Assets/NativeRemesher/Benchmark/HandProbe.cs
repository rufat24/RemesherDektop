// The hands, which is where our output and QuadRemesher's now differ most.
//
// After triangle pairing was fixed, the defect audit (Benchmarks/reference/defects.py) on the
// character at the delivered density puts the remaining difference in one place:
//
//   ours    folds 284, in clusters at (3,84,50) %, (98,84,40) % and (94,84,59) % of the bounding
//           box - a symmetric set at the far left and right, at hand height. 93 faces.
//   theirs  folds 214, and NONE of them there. Every cluster they have follows a real crease of
//           the shape: the top of the head, and a symmetric pair at the ears.
//
// A finger is a tube a few quads around. Where the quads are too big to wrap it, the lattice has to
// fold to get round, and no amount of relaxation fixes that because the mesh has nowhere smaller to
// put a vertex. The fix, if there is one, is to spend more of the budget there - which is what
// curvature adaptivity does, and QuadRemesher ships at twice ours (CurvatureAdaptivness 50 against
// our 0.25) with a target it is allowed to exceed.
//
// So this writes the same model at the same face count with the sizing turned up, for the audit
// script to score. The question it answers is narrow and worth being clear about: does spending
// more of a FIXED budget on curvature resolve the fingers, or does it only move the shortage
// somewhere else?

using System;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class HandProbe
    {
        const string k_Original = "Benchmarks/reference/ref_high.obj";
        const int k_Target = 5_215;

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== HandProbe (character @ the delivered face count) ===");

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

                // SIZING was tested first and refuted: adaptivity 0.25 / 0.50 / 0.75 gave 59 / 35
                // / 76 faces in the hand clusters, so grading harder does not resolve a finger.
                //
                // This axis is decimation, and it is a different mechanism from the one already
                // ruled out. `DecimationProbe` shows the decimated SURFACE is intact - 0.000 %
                // uncovered at the delivered density, worst error nine times finer than half a quad
                // - but the lattice is solved on that mesh's VERTICES, and a quad corner can only
                // land on one of them. Ten triangles per quad leaves about five vertices per quad
                // edge across the model; around a finger, that may be the entire cross-section. The
                // geometry surviving decimation and the field having enough places to put a vertex
                // are two different questions, and only the first has been answered.
                foreach (var budget in new[] { 10, 40, 100, 0 })
                    Export(report, positions, triangles, budget,
                        $"Benchmarks/reference/v1-hand-d{budget}.obj");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
            }

            report.AppendLine("=== end HandProbe ===");
            Emit(report);
        }

        static void Export(StringBuilder report, Vector3[] positions, int[] triangles,
            int budgetPerQuad, string path)
        {
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = k_Target;

            var profile = RemeshProfile.For(RemeshProfileKind.Character);
            profile.preDecimate = budgetPerQuad > 0;
            if (budgetPerQuad > 0) profile.decimateBudgetPerQuad = budgetPerQuad;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
            var ms = watch.ElapsedMilliseconds;

            ObjWriter.Write(path, result.extraction.positions, result.extraction.faces, "hand");

            report.AppendLine($"  budget {(budgetPerQuad > 0 ? budgetPerQuad + "/quad" : "none")}: " +
                              $"{result.extraction.faceCount:N0} faces, " +
                              $"{result.extraction.quadFraction * 100f:F2} % quads, " +
                              $"{ms:N0} ms -> {path}");
            if (result.extraction.trianglePairingStats != null)
                report.AppendLine($"      {result.extraction.trianglePairingStats}");
        }

        static void Emit(StringBuilder report)
        {
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
    }
}
