// Where does the collar die?
//
// The collar of the armour asset is a thin-walled tube with a rolled rim: an outer wall, a lip, and
// an inner wall, with the two walls closer together than one quad at the delivered density. Our
// output fuses the whole thing into a lump. The competitor resolves it at the same face count, so it
// is representable and we are losing it somewhere rather than running out of quads.
//
// Three stages could be responsible and one measurement separates them - write out what each stage
// HANDS ON, and look at it:
//
//   the decimated input   - if QEM has already welded the two walls, nothing downstream can help
//   extraction, relax off - if the walls survive decimation and not this, the collapse welded them
//   extraction, relax on  - if they survive the collapse and not this, reprojection is landing
//                           vertices on the wrong sheet
//
// ANSWER TO THE FIRST, and it is clean: the decimated input keeps the collar perfectly. 49,550
// triangles, tube and rim and inner wall all intact - sharper than the competitor's finished output.
// Pre-decimation is exonerated for the third time and for a third distinct reason, and the defect is
// inside extraction.
//
// Two attempts at the collapse have already failed, which is why this goes back a stage rather than
// trying a third: a per-candidate normal test that could not fire by construction, and a per-cluster
// normal cone that fires and changes nothing about the collar.

using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class CollarProbe
    {
        const string k_Character = "Benchmarks/reference/ref_high.obj";

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== CollarProbe ===");

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

                    var profile = RemeshProfile.For(RemeshProfileKind.HardSurface).Validated();
                    var settings = RemeshSettings.Default;
                    settings.targetQuadCount = 4955;

                    // Exactly what the pipeline does before the solver sees anything: the same
                    // budget, the same call. If the collar is gone here, every theory about the
                    // lattice is answering the wrong question.
                    var budget = Mathf.Max(200, Mathf.RoundToInt(
                        profile.decimateBudgetPerQuad * settings.targetQuadCount));

                    PreDecimate.Decimate(positions, triangles,
                        DecimateOptions.ForTriangleCount(budget), out var stats,
                        out var outPositions, out var outTriangles);

                    report.AppendLine($"  input      {positions.Length} verts, "
                                      + $"{triangles.Length / 3} tris");
                    report.AppendLine($"  decimated  {outPositions.Length} verts, "
                                      + $"{outTriangles.Length / 3} tris   budget {budget}");

                    WriteTriangles("Benchmarks/reference/collar-decimated.obj",
                        outPositions, outTriangles);
                    report.AppendLine("  wrote collar-decimated.obj");

                    // Relaxation on and off splits extraction in half.
                    foreach (var relax in new[] { true, false })
                    {
                        var variant = RemeshProfile.For(RemeshProfileKind.HardSurface);
                        variant.relaxSurface = relax;

                        var result = RemeshPipeline.Solve(positions, triangles, settings, variant);
                        var name = relax ? "collar-relaxed" : "collar-noRelax";

                        WriteFaces($"Benchmarks/reference/{name}.obj", result);
                        report.AppendLine($"  relax={relax}: {result.extraction.faceCount} faces, "
                                          + $"{result.extraction.quadFraction * 100f:F2} % quads "
                                          + $"-> {name}.obj");
                    }

                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }

            report.AppendLine("=== end CollarProbe ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void WriteFaces(string path, RemeshResult result)
        {
            var sb = new StringBuilder();
            var c = CultureInfo.InvariantCulture;

            foreach (var p in result.extraction.positions)
            {
                sb.Append("v ").Append(p.x.ToString("F6", c));
                sb.Append(' ').Append(p.y.ToString("F6", c));
                sb.Append(' ').Append(p.z.ToString("F6", c));
                sb.AppendLine();
            }

            foreach (var face in result.extraction.faces)
            {
                if (face == null || face.Length < 3) continue;

                sb.Append('f');
                foreach (var i in face) sb.Append(' ').Append(i + 1);
                sb.AppendLine();
            }

            System.IO.File.WriteAllText(path, sb.ToString());
        }

        static void WriteTriangles(string path, Vector3[] positions, int[] triangles)
        {
            var sb = new StringBuilder();
            var c = CultureInfo.InvariantCulture;

            foreach (var p in positions)
            {
                sb.Append("v ").Append(p.x.ToString("F6", c));
                sb.Append(' ').Append(p.y.ToString("F6", c));
                sb.Append(' ').Append(p.z.ToString("F6", c));
                sb.AppendLine();
            }

            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                sb.Append("f ").Append(triangles[i] + 1);
                sb.Append(' ').Append(triangles[i + 1] + 1);
                sb.Append(' ').Append(triangles[i + 2] + 1);
                sb.AppendLine();
            }

            System.IO.File.WriteAllText(path, sb.ToString());
        }
    }
}
