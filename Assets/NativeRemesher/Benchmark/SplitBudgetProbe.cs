// Is the subdivision doing real work, or cycling?
//
// The integer grid map's subdivision hits its split budget on some inputs and rolls back, which
// leaves the map unrefined and the extraction full of holes. Two very different things look
// identical from the outside: a pass that would converge given more budget, and a pass where each
// split creates as much length as it removes.
//
// This separates them. It runs the same case at a rising budget and prints how many edges are
// still over length each time. Falling means the budget was simply too small. Flat or rising means
// the split arithmetic is trading the defect back and forth and no budget will ever be enough.

using System;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class SplitBudgetProbe
    {
        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== SplitBudgetProbe ===");
            report.AppendLine("  case,budget,splits,abandoned,stillLong,flipsBefore,flipsAfter,faces,quadFrac,bndEdges,ms");

            var budgets = new[] { 0.5f, 1f, 2f, 4f, 8f };

            var cases = new (string name, Func<Mesh> build, int target)[]
            {
                ("cylinder_capped", () => MeshCorpus.CappedCylinder(0.5f, 2f, 96, 24), 2000),
                ("cube_subdiv", () => MeshCorpus.SubdividedCube(1f, 48), 2000),
            };

            var original = Extraction.IntegerGridMap.splitBudgetPerEdge;
            try
            {
                foreach (var (name, build, target) in cases)
                {
                    foreach (var budget in budgets)
                    {
                        Mesh mesh = null;
                        try
                        {
                            Extraction.IntegerGridMap.splitBudgetPerEdge = budget;
                            mesh = build();

                            var settings = RemeshSettings.Default;
                            settings.targetQuadCount = target;
                            var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
                            profile.extractor = RemeshExtractor.IntegerGrid;

                            var result = RemeshPipeline.Run(mesh, settings, profile);
                            var e = result.extraction;
                            var s = e.integerGridStats;

                            IntegerGridAudit.Valences(e.faces, out _, out _, out _, out _, out _,
                                out var boundaryEdges);

                            report.AppendLine(
                                $"  {name},{budget},{s.subdivideSplitsFirst}," +
                                $"{s.subdivideAbandoned},{s.subdivideStillLong}," +
                                $"{s.flippedBefore},{s.flippedAfter}," +
                                $"{e.faceCount},{e.quadFraction:F4},{boundaryEdges},{result.totalMs}");
                        }
                        catch (Exception ex)
                        {
                            report.AppendLine($"  {name},{budget},threw: {ex.GetType().Name}: {ex.Message}");
                        }
                        finally
                        {
                            if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                        }
                    }
                    report.AppendLine();
                }
            }
            finally
            {
                Extraction.IntegerGridMap.splitBudgetPerEdge = original;
            }

            report.AppendLine("=== end SplitBudgetProbe ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
    }
}
