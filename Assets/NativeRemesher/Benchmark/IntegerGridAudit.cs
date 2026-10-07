// The collapse extractor against the integer grid map, on the inputs whose floor is known.
//
// Same four cases as LatticeAudit, same metrics, one extra column per row: the extractor. This
// is the gate PLAN.md W1.5 set — torus at most 30 irregular against a floor of 0, icosphere at
// most 15 against a floor of 8, and 100 % quads on both — and the only comparison that can say
// whether the new path is better or merely different.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class IntegerGridAudit
    {
        struct Case
        {
            public string name;
            public Func<Mesh> build;
            public int floor;
            public int target;
        }

        static readonly Case[] k_Cases =
        {
            new Case { name = "torus", floor = 0, target = 2000, build = () => MeshCorpus.Torus(1f, 0.35f, 128, 64) },
            new Case { name = "icosphere_80k", floor = 8, target = 2000, build = () => MeshCorpus.Icosphere(6, 1f) },
            new Case { name = "cylinder_capped", floor = 8, target = 2000, build = () => MeshCorpus.CappedCylinder(0.5f, 2f, 96, 24) },
            new Case { name = "cube_subdiv", floor = 8, target = 2000, build = () => MeshCorpus.SubdividedCube(1f, 48) },
            new Case { name = "sphere_bumpy", floor = 8, target = 2000, build = () => MeshCorpus.BumpySphere(5, 1f, 0.1f, 4f) },
            new Case { name = "disc_open", floor = 0, target = 500, build = () => MeshCorpus.OpenDisc(1f, 96, 24) },
        };

        public static void Valences(List<int[]> faces, out int interior, out int irregular,
            out int v3, out int v5, out int higher, out int boundaryEdges)
        {
            var valence = new Dictionary<int, int>();
            var uses = new Dictionary<long, int>(Extraction.EdgeKey.Comparer);
            foreach (var face in faces)
            {
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    valence.TryGetValue(a, out var used);
                    valence[a] = used + 1;
                    var key = Extraction.EdgeKey.PackUndirected(a, b);
                    uses.TryGetValue(key, out var count);
                    uses[key] = count + 1;
                }
            }

            var onBoundary = new HashSet<int>();
            boundaryEdges = 0;
            foreach (var entry in uses)
            {
                if (entry.Value == 2) continue;
                boundaryEdges++;
                onBoundary.Add(Extraction.EdgeKey.First(entry.Key));
                onBoundary.Add(Extraction.EdgeKey.Second(entry.Key));
            }

            interior = irregular = v3 = v5 = higher = 0;
            foreach (var entry in valence)
            {
                if (onBoundary.Contains(entry.Key)) continue;
                interior++;
                if (entry.Value == 4) continue;
                irregular++;
                if (entry.Value == 3) v3++;
                else if (entry.Value == 5) v5++;
                else higher++;
            }
        }

        /// <summary>The one case named by <c>-case name</c> on the command line, or null for all.</summary>
        static string CaseFilter()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-case") return args[i + 1];
            return null;
        }

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== IntegerGridAudit ===");
            report.AppendLine("collapse (ships) against integerGridMap, GameAsset profile, face count held by the spacing probe.");
            report.AppendLine("  case,extractor,floor,faces,quadFrac,tris,ngons,interior,irregular,v3,v5,vHigher," +
                              "bndEdges,hausdorff,chamfer,fieldSing,ms");

            var noSharp = Array.IndexOf(Environment.GetCommandLineArgs(), "-igmNoSharp") >= 0;
            Extraction.IntegerGridMap.applySharpConstraints = !noSharp;
            report.AppendLine($"applySharpConstraints={!noSharp}");
            var noRollback = Array.IndexOf(Environment.GetCommandLineArgs(), "-igmNoRollback") >= 0;
            Extraction.IntegerGridMap.rollbackOnBudget = !noRollback;
            report.AppendLine($"rollbackOnBudget={!noRollback}");
            Extraction.IntegerGridMap.creaseConstraints = Array.IndexOf(Environment.GetCommandLineArgs(), "-igmNoCrease") < 0;
            {
                var ti = Array.IndexOf(Environment.GetCommandLineArgs(), "-tether");
                if (ti >= 0 && double.TryParse(Environment.GetCommandLineArgs()[ti + 1], out var tw))
                    Extraction.IntegerGridMap.tetherWeight = tw;
                Extraction.IntegerGridMap.optimizePositions = Array.IndexOf(Environment.GetCommandLineArgs(), "-igmNoPos") < 0;
            }
            Extraction.IntegerGridMap.placeFeatureVertices = Array.IndexOf(Environment.GetCommandLineArgs(), "-igmNoPlace") < 0;
            var only = CaseFilter();
            foreach (var testCase in k_Cases)
            {
                if (only != null && testCase.name != only) continue;
                foreach (var integer in new[] { false, true })
                {
                    Mesh mesh = null;
                    try
                    {
                        mesh = testCase.build();
                        var reference = PolyMesh.FromMesh(mesh);
                        var referenceCopy = new PolyMesh
                        {
                            positions = (Vector3[])reference.positions.Clone(),
                            faces = reference.faces,
                        };

                        var settings = RemeshSettings.Default;
                        settings.targetQuadCount = testCase.target;
                        var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
                        profile.integerGridMap = integer;

                        var result = RemeshPipeline.Run(mesh, settings, profile);
                        var e = result.extraction;
                        var metrics = MeshMetrics.Measure(PolyMesh.FromExtraction(e), referenceCopy, 20000);
                        var field = FieldDiagnostics.Analyse(result.topology, result.orientation, result.position);

                        Valences(e.faces, out var interior, out var irregular, out var v3, out var v5,
                            out var higher, out var boundaryEdges);

                        report.AppendLine(
                            $"  {testCase.name},{(integer ? "integer" : "collapse")},{testCase.floor}," +
                            $"{e.faceCount},{e.quadFraction:F4},{e.triangleCount},{e.ngonCount}," +
                            $"{interior},{irregular},{v3},{v5},{higher},{boundaryEdges}," +
                            $"{metrics.hausdorffMax:F5},{metrics.chamferMean:F5}," +
                            $"{field.singularityCount},{result.totalMs}");

                        if (integer) report.AppendLine($"      {result.refineStats} ({result.refineMs} ms)");
                        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-dump") >= 0)
                            ObjWriter.Write($"Benchmarks/reference/corpus_{testCase.name}_{(integer ? "integer" : "collapse")}.obj",
                                e.positions, e.faces, testCase.name);
                        if (e.integerGridStats != null) report.AppendLine($"      {e.integerGridStats}");
                        if (e.integerGridStats?.subdivideTrace != null)
                            report.AppendLine(e.integerGridStats.subdivideTrace);
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"  {testCase.name},{(integer ? "integer" : "collapse")},threw: {ex}");
                    }
                    finally
                    {
                        if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }
            }

            report.AppendLine("=== end IntegerGridAudit ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        /// <summary>
        /// The same A/B on whatever real scans sit in Benchmarks/reference as OBJ, under the scan
        /// profiles. Targets come from the command line as <c>-targets 5000,12000</c>, or default
        /// to one count per asset.
        /// </summary>
        public static void RunReal()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== IntegerGridAudit (real assets) ===");
            report.AppendLine("  asset,profile,target,extractor,faces,quadFrac,tris,ngons,interior,irregular," +
                              "v3,v5,vHigher,bndEdges,nonManifold,components,hausdorff,chamfer,inputTris,fieldSing,ms");

            var noPos = Array.IndexOf(Environment.GetCommandLineArgs(), "-igmNoPos") >= 0;
            Extraction.IntegerGridMap.optimizePositions = !noPos;
            report.AppendLine($"optimizePositions={!noPos}");
            var targets = new List<int>();
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] != "-targets") continue;
                foreach (var s in args[i + 1].Split(','))
                    if (int.TryParse(s, out var t)) targets.Add(t);
            }
            if (targets.Count == 0) targets.Add(5000);

            var assets = new[] { "head", "body", "ref_high", "scan_open" };
            foreach (var asset in assets)
            {
                var path = $"Benchmarks/reference/{asset}.obj";
                if (!System.IO.File.Exists(path)) continue;

                Extraction.IntegerGridMap.applySharpConstraints =
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-igmNoSharp") < 0;
                var pk = RemeshProfileKind.Photogrammetry;
                var pi = Array.IndexOf(Environment.GetCommandLineArgs(), "-profile");
                if (pi >= 0 && pi + 1 < Environment.GetCommandLineArgs().Length)
                    pk = (RemeshProfileKind)Enum.Parse(typeof(RemeshProfileKind),
                        Environment.GetCommandLineArgs()[pi + 1], true);

                foreach (var target in targets)
                foreach (var kind in new[] { pk })
                foreach (var integer in new[] { false, true })
                {
                    Mesh mesh = null;
                    try
                    {
                        mesh = ObjReader.Read(path);
                        var reference = PolyMesh.FromMesh(mesh);
                        var referenceCopy = new PolyMesh
                        {
                            positions = (Vector3[])reference.positions.Clone(),
                            faces = reference.faces,
                        };

                        var settings = RemeshSettings.Default;
                        settings.targetQuadCount = target;
                        var profile = RemeshProfile.For(kind);
                        var fa = Array.IndexOf(Environment.GetCommandLineArgs(), "-featureAngle");
                        if (fa >= 0 && fa + 1 < Environment.GetCommandLineArgs().Length
                            && float.TryParse(Environment.GetCommandLineArgs()[fa + 1], out var fav))
                            profile.featureAngleDegrees = fav;
                        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-igmNoAdapt") >= 0)
                            profile.curvatureAdaptivity = 0f;
                        var oi = Array.IndexOf(Environment.GetCommandLineArgs(), "-orientIters");
                        if (oi >= 0 && oi + 1 < Environment.GetCommandLineArgs().Length
                            && int.TryParse(Environment.GetCommandLineArgs()[oi + 1], out var oit))
                            profile.orientationIterations = oit;
                        profile.integerGridMap = integer;

                        var result = RemeshPipeline.Run(mesh, settings, profile);
                        var e = result.extraction;
                        var metrics = MeshMetrics.Measure(PolyMesh.FromExtraction(e), referenceCopy, 20000);
                        var field = FieldDiagnostics.Analyse(result.topology, result.orientation, result.position);

                        Valences(e.faces, out var interior, out var irregular, out var v3, out var v5,
                            out var higher, out var boundaryEdges);

                        report.AppendLine(
                            $"  {asset},{kind},{target},{(integer ? "integer" : "collapse")}," +
                            $"{e.faceCount},{e.quadFraction:F4},{e.triangleCount},{e.ngonCount}," +
                            $"{interior},{irregular},{v3},{v5},{higher},{boundaryEdges}," +
                            $"{metrics.nonManifoldEdges},{metrics.componentCount}," +
                            $"{metrics.hausdorffMax:F5},{metrics.chamferMean:F5}," +
                            $"{result.topology.triangleCount},{field.singularityCount},{result.totalMs}");
                        if (e.integerGridStats != null) report.AppendLine($"      {e.integerGridStats}");
                        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-dump") >= 0)
                            ObjWriter.Write($"Benchmarks/reference/out_{asset}_{kind}_{target}_" +
                                $"{(integer ? "integer" : "collapse")}.obj", e.positions, e.faces, asset);
                        report.AppendLine($"      {e.Diagnostics().Replace("\n", "\n      ")}");
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"  {asset},{kind},{target},{(integer ? "integer" : "collapse")},threw: {ex}");
                    }
                    finally
                    {
                        if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }
            }

            report.AppendLine("=== end IntegerGridAudit (real assets) ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
    }
}
