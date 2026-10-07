// Which profile is the user actually getting, and what does the wrong one cost on a scan?
//
// THE OBSERVATION THAT STARTED THIS. Every benchmark in this repo runs the Character profile on the
// reference scan. The editor window defaults to GameAsset. So the artefact the user looks at and
// the artefact this project measures have never been the same thing, and a screenshot of visible
// dents sat next to a measurement saying 2 vertices of 5,020 are off the surface.
//
// Measured on the delivered files rather than on benchmark exports (Benchmarks/reference/dents.py):
//
//     their Retopo 9     10 vertices off the surface, 8 clusters
//     our bench export    2 vertices, 2 clusters          <- Character profile
//     our Quads 38      215 vertices, 75 clusters         <- what the user has
//
// THE MECHANISM, which is a profile mismatch and not a solver defect. GameAsset declares
// featureAngleDegrees = 30, because a game asset has real creases worth holding. A photogrammetry
// scan has none - it has NOISE, and a 30-degree dihedral test on a 1.5 M triangle scan fires on
// noise in every soft fold of the body. Each false crease then reaches SurfaceRelaxation, whose
// fourth guard pins any vertex sitting in an input crease and refuses any move that would cross
// one. So precisely the vertices that most need relaxing back onto the surface are the ones held
// still, and they stay where extraction left them.
//
// That predicts the defect is CLUSTERED at soft folds - shoulders, chest, armpits - rather than
// scattered, which is what the cluster centroids say and what the user circled on a screenshot.
//
// Both profiles are written out as OBJ so dents.py can score them against the original scan.

using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class ProfileProbe
    {
        const string k_Character = "Benchmarks/reference/ref_high.obj";

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== ProfileProbe ===");

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

                    report.AppendLine("  profile,faces,quad%,tris,features,pinned,rejFeature," +
                                      "rejDrift,rejProj,moves,ms");

                    // Matched to the face count of the file the user is comparing against, so the
                    // two arms differ by profile and nothing else.
                    foreach (var kind in new[]
                             {
                                 RemeshProfileKind.GameAsset,
                                 RemeshProfileKind.Character,
                                 RemeshProfileKind.Photogrammetry,
                                 RemeshProfileKind.HardSurface
                             })
                        Measure(report, positions, triangles, 4955, kind);

                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }

            report.AppendLine("=== end ProfileProbe ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void Measure(StringBuilder report, Vector3[] positions, int[] triangles, int target,
            RemeshProfileKind kind)
        {
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = target;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = RemeshPipeline.Solve(positions, triangles, settings,
                    RemeshProfile.For(kind));
                var ms = watch.ElapsedMilliseconds;

                var tris = 0;
                foreach (var face in result.extraction.faces)
                    if (face != null && face.Length == 3) tris++;

                var featureVertices = 0;
                if (result.features?.isFeatureVertex != null)
                    foreach (var f in result.features.isFeatureVertex)
                        if (f) featureVertices++;
                var relax = result.extraction.relaxStats;

                report.AppendLine(
                    $"  {kind},{result.extraction.faceCount}," +
                    $"{result.extraction.quadFraction * 100f:F2},{tris},{featureVertices}," +
                    $"{(relax != null ? relax.pinnedByFeature : -1)}," +
                    $"{(relax != null ? relax.rejectedFeature : -1)}," +
                    $"{(relax != null ? relax.rejectedDrift : -1)}," +
                    $"{(relax != null ? relax.rejectedProjection : -1)}," +
                    $"{(relax != null ? relax.moves : -1)},{ms}");

                WriteObj($"Benchmarks/reference/profile-{kind}.obj", result);
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {kind},threw: {ex.Message}");
            }
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
