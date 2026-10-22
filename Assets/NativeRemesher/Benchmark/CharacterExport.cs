// Writes our character output to OBJ so the SAME external script scores ours and the reference's.
//
// WHY THIS EXISTS. The asset the user compared against the reference has 5,215 faces; the reference's
// has 21,780. That is a 4.2x density difference, and every shading and smoothness judgement is
// sensitive to it - a coarser mesh has larger dihedral angles between neighbours for no reason
// other than being coarser. So a side-by-side of those two files cannot separate "our algorithm is
// worse" from "our mesh is coarser", and until they are at the same count no conclusion drawn from
// looking at them is safe.
//
// This emits ours at the reference's own face count, and at the delivered count as a control, in the
// one format that survives the trip (see ObjWriter's header for why every other route destroys the
// quads being measured).

using System;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class CharacterExport
    {
        const string k_Original = "Benchmarks/reference/ref_high.obj";

        /// <summary>The reference's own face count on this asset.</summary>
        const int k_Matched = 21_780;

        /// <summary>The count the delivered asset actually has, kept as the control arm.</summary>
        const int k_Delivered = 5_215;

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== CharacterExport ===");

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
                report.AppendLine($"  input {triangles.Length / 3:N0} triangles");

                Export(report, positions, triangles, k_Matched,
                    "Benchmarks/reference/v1-character-21780.obj");
                Export(report, positions, triangles, k_Delivered,
                    "Benchmarks/reference/v1-character-5215.obj");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
            }

            report.AppendLine("=== end CharacterExport ===");
            Emit(report);
        }

        static void Export(StringBuilder report, Vector3[] originalPositions, int[] originalTriangles,
            int target, string path)
        {
            var positions = originalPositions;
            var triangles = originalTriangles;

            // The profile's own pre-decimation is bypassed and done here instead, so this arm is
            // driven exactly the way CharacterDiagnosis drives its arms and the two are comparable.
            PreDecimate.Decimate(originalPositions, originalTriangles,
                DecimateOptions.ForTriangleCount(217_800),
                out _, out positions, out triangles);

            var settings = RemeshSettings.Default;
            settings.targetQuadCount = target;

            var profile = RemeshProfile.For(RemeshProfileKind.Character);
            profile.preDecimate = false;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
            var ms = watch.ElapsedMilliseconds;

            ObjWriter.Write(path, result.extraction.positions, result.extraction.faces,
                $"character-{target}");

            // And as .fbx, which is the form the result is actually usable in outside this project.
            var fbx = System.IO.Path.ChangeExtension(path, ".fbx");
            FbxWriter.Write(fbx, result.extraction.positions, result.extraction.faces,
                result.extraction.shadingNormals, $"character-{target}");

            report.AppendLine($"  target {target:N0} -> {result.extraction.faces.Count:N0} faces, " +
                              $"{result.extraction.quadFraction * 100f:F2} % quads, {ms:N0} ms");
            report.AppendLine($"    wrote {path}");
            report.AppendLine($"    wrote {fbx}");
            if (result.extraction.relaxStats != null)
                report.AppendLine($"    {result.extraction.relaxStats}");
        }

        static void Emit(StringBuilder report)
        {
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
    }
}
