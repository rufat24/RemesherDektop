// Head to head on the user's two real assets, at the face counts the competitors were run at.
//
// Everything measured before this ran on ONE asset — the closed character scan — and on procedural
// primitives. That left the harder of the two real inputs unmeasured: `ccnb.obj` is 990,591
// triangles of Agisoft photogrammetry with a genuinely open outline, 5 non-manifold edges and 818
// vertices touched by a single face corner. It is the input this tool is aimed at, and the one
// nobody's method handles cleanly:
//
//   * Blender's QuadriFlow REFUSES it outright, silently, returning the input unchanged. It also
//     refuses the character scan until the winding is made consistent. So the method measured as
//     the best available in `how-to-match-quadremesher.md` cannot even be run on this asset.
//   * QuadRemesher does produce output, but its own result carries 1,513 boundary edges, 377
//     valence-1 vertices and 6 non-manifold edges — so it is not solving it either, just not
//     refusing.
//
// That robustness gap is worth as much as the quality gap and had never been written down. This
// probe measures both assets, both competitors' face counts, and the subdivision pass on and off,
// so the comparison is one table instead of four separate sessions' worth of notes.
//
// Face counts are chosen to match what exists to compare against, not round numbers:
//   character  5,474  the retopology reference (Assets/ccnb/20260515221800_2ef3dacc.fbx)
//   character 12,451  QuadRemesher's run
//   ccnb       9,451  QuadRemesher's run (Output/default_Retopo.fbx)

using System;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class RealAssetComparison
    {
        struct Job
        {
            public string asset;
            public Func<Mesh> load;
            public int target;
            /// <summary>What the competitors scored at this count, for the report's last column.</summary>
            public string reference;
        }

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== RealAssetComparison ===");
            report.AppendLine("Both real assets, at the face counts the competitors were run at.");
            report.AppendLine();

            var jobs = new[]
            {
                new Job
                {
                    asset = "character", target = 5474,
                    load = () => PerfProbe.LoadReferenceHigh(out _),
                    reference = "retopo 596 irregular (11.3%), QuadriFlow 162 (3.3%)",
                },
                new Job
                {
                    asset = "character", target = 12451,
                    load = () => PerfProbe.LoadReferenceHigh(out _),
                    reference = "QuadRemesher 341 (2.7%), QuadriFlow 222 (1.8%)",
                },
                new Job
                {
                    asset = "ccnb", target = 9451,
                    load = LoadScan,
                    reference = "QuadRemesher 1352 interior irregular (15.6%), QuadriFlow REFUSED",
                },
            };

            foreach (var job in jobs)
            {
                report.AppendLine($"--- {job.asset} @{job.target}   [{job.reference}]");
                report.AppendLine("  profile,subdiv,faces,quadFrac,tris,ngons,irregular,irregPct," +
                                  "maxValence,v1,v2,bndEdges,nonMan,dev,hausdorff,discard," +
                                  "discardPct,split,subdivRounds,abandoned,ms");

                foreach (var kind in new[]
                         {
                             RemeshProfileKind.Photogrammetry,
                             RemeshProfileKind.Character,
                         })
                foreach (var subdiv in new[] { false, true })
                    Measure(report, job, kind, subdiv);

                report.AppendLine();
            }

            report.AppendLine("=== end RealAssetComparison ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static Mesh LoadScan()
        {
            const string path = "Benchmarks/reference/scan_open.obj";
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"[RealAsset] missing {path}");
                return null;
            }
            return ObjReader.Read(path);
        }

        static void Measure(StringBuilder report, Job job, RemeshProfileKind kind, bool subdiv)
        {
            Mesh mesh = null;
            try
            {
                mesh = job.load();
                if (mesh == null)
                {
                    report.AppendLine($"  {kind},{(subdiv ? "ON" : "off")},input missing — skipped");
                    return;
                }

                var reference = PolyMesh.FromMesh(mesh);
                var referenceCopy = new PolyMesh
                {
                    positions = (Vector3[])reference.positions.Clone(),
                    faces = reference.faces,
                };

                var settings = RemeshSettings.Default;
                settings.targetQuadCount = job.target;

                var profile = RemeshProfile.For(kind);
                profile.subdivideLongEdges = subdiv;

                var result = RemeshPipeline.Run(mesh, settings, profile);
                var e = result.extraction;

                var metrics = MeshMetrics.Measure(
                    PolyMesh.FromExtraction(e), referenceCopy, 40000);

                // Valence over the emitted faces, so the report carries the two defects the
                // irregular fraction hides: the worst valence, and vertices touched once or twice.
                var valence = new System.Collections.Generic.Dictionary<int, int>(e.faceCount * 2);
                foreach (var face in e.faces)
                foreach (var c in face)
                {
                    valence.TryGetValue(c, out var d);
                    valence[c] = d + 1;
                }

                int maxValence = 0, v1 = 0, v2 = 0, irregular = 0;
                foreach (var d in valence.Values)
                {
                    if (d > maxValence) maxValence = d;
                    if (d == 1) v1++;
                    if (d == 2) v2++;
                    if (d != 4) irregular++;
                }

                report.AppendLine(
                    $"  {kind},{(subdiv ? "ON" : "off")}," +
                    $"{e.faceCount},{e.quadFraction:F4},{e.triangleCount},{e.ngonCount}," +
                    $"{irregular},{irregular / (float)Mathf.Max(1, valence.Count):F4}," +
                    $"{maxValence},{v1},{v2}," +
                    $"{metrics.boundaryEdges},{metrics.nonManifoldEdges}," +
                    $"{metrics.chamferMean:F5},{metrics.hausdorffMax:F5}," +
                    $"{e.collapseStats?.discardedEdges}," +
                    $"{e.collapseStats?.DiscardedFraction:F4}," +
                    $"{e.subdivideStats?.edgesSplit ?? 0}," +
                    $"{e.subdivideStats?.rounds ?? 0}," +
                    $"{(e.subdivideStats?.abandoned != null ? "YES" : "no")}," +
                    $"{result.totalMs}");

                if (e.subdivideStats != null && e.subdivideStats.edgesSplit > 0)
                    report.AppendLine($"      {e.subdivideStats}");

                var path = $"Benchmarks/reference/ours-{job.asset}-{job.target}-{kind}" +
                           $"{(subdiv ? "-subdiv" : "")}.obj";
                ObjWriter.Write(path, e.positions, e.faces, $"{job.asset}_{kind}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {kind},{(subdiv ? "ON" : "off")},threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
    }
}
