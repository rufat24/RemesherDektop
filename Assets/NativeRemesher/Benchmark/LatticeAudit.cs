// Where the singularities come from, on inputs whose optimal answer is known in advance.
//
// Every quality measurement in this project so far has been taken on meshes where nobody knows what
// the right answer is. "44% irregular against a reference's 11.3%" says we are worse; it cannot say
// how much better is even possible, so it cannot say which stage lost it.
//
// These four inputs can. Each is a closed analytic surface whose minimum possible irregular-vertex
// count follows from Euler's formula alone:
//
//   torus            chi = 0  ->  index sum 0  ->  ZERO irregular vertices are needed. A perfect
//                                 m x n quad grid exists and is reachable.
//   icosphere        chi = 2  ->  index sum 8  ->  at least 8 valence-3 vertices.
//   capped cylinder  chi = 2  ->  index sum 8  ->  same, plus two circular creases.
//   subdivided cube  chi = 2  ->  index sum 8  ->  same, and the optimum is literally a cube grid.
//
// The torus row is the one that matters. It has no features, no noise, no boundary, no curvature
// variation worth adapting to, and a provably attainable defect-free answer. Anything irregular in
// our torus output is a defect we manufactured, and no amount of feature detection or adaptive
// sizing can be the cause, because there is nothing there to detect or adapt to.
//
// The report puts the field's own singularity count next to the output's irregular-vertex count on
// the same row, because that pair localises the loss to one side of extraction and nothing else in
// the harness does.

using System;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class LatticeAudit
    {
        struct Case
        {
            public string name;
            public Func<Mesh> build;
            /// <summary>Minimum irregular vertices Euler's formula permits. 4 * chi = index sum.</summary>
            public int floor;
            public int target;
        }

        static readonly Case[] k_Cases =
        {
            new Case
            {
                name = "torus", floor = 0, target = 2000,
                build = () => MeshCorpus.Torus(1f, 0.35f, 128, 64),
            },
            new Case
            {
                name = "icosphere_80k", floor = 8, target = 2000,
                build = () => MeshCorpus.Icosphere(6, 1f),
            },
            new Case
            {
                name = "cylinder_capped", floor = 8, target = 2000,
                build = () => MeshCorpus.CappedCylinder(0.5f, 2f, 96, 24),
            },
            new Case
            {
                name = "cube_subdiv", floor = 8, target = 2000,
                build = () => MeshCorpus.SubdividedCube(1f, 48),
            },
        };

        /// <summary>
        /// Signed volume of every closed generator in the corpus. A closed surface wound outward
        /// encloses positive volume; negative means the generator is inside-out, and because
        /// <c>Build</c> calls <c>RecalculateNormals</c>, an inverted winding silently becomes
        /// inverted vertex normals for the whole mesh.
        /// </summary>
        /// <remarks>
        /// This exists because <c>disagreeAfter</c> read 1,908 of 1,910 faces on the torus while
        /// <c>inconsistentEdges</c> stayed 0 and the signed-volume test reported no flip needed.
        /// Those three cannot all be true of a correct output and a correct input, and the counter
        /// compares the output's face normals against the INPUT's vertex normals — so the suspect is
        /// the input.
        /// </remarks>
        static void AuditCorpusWinding(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("--- corpus winding (signed volume; negative = generator is inside-out)");

            var closed = new (string name, Func<Mesh> build)[]
            {
                ("torus", () => MeshCorpus.Torus(1f, 0.35f, 64, 32)),
                ("icosphere", () => MeshCorpus.Icosphere(3, 1f)),
                ("bumpy_sphere", () => MeshCorpus.BumpySphere(3, 1f, 0.1f, 4f)),
                ("cylinder_capped", () => MeshCorpus.CappedCylinder(0.5f, 2f, 32, 8)),
                ("cube_subdiv", () => MeshCorpus.SubdividedCube(1f, 8)),
            };

            foreach (var (name, build) in closed)
            {
                Mesh mesh = null;
                try
                {
                    mesh = build();
                    var p = mesh.vertices;
                    var t = mesh.triangles;
                    var total = 0.0;
                    for (var i = 0; i + 2 < t.Length; i += 3)
                    {
                        Vector3 a = p[t[i]], b = p[t[i + 1]], c = p[t[i + 2]];
                        total += Vector3.Dot(a, Vector3.Cross(b, c));
                    }
                    total /= 6.0;

                    report.AppendLine($"  {name}: signedVolume={total:F6} " +
                                      $"{(total < 0 ? "<-- INSIDE OUT" : "ok")}");
                }
                catch (Exception e)
                {
                    report.AppendLine($"  {name}: threw {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
        }

        /// <summary>
        /// Irregularity against the collapse cluster spread, at equal face count.
        /// </summary>
        /// <remarks>
        /// The spread bound says how far one output vertex's cluster may reach, in lattice cells.
        /// The default is 2.25, so a cluster is allowed to span more than two cells and become a
        /// single vertex with neighbours in more than four lattice directions — which is precisely
        /// the contested/vacant pair that shows up as valence 5 and 3.
        ///
        /// The bound was set generously because tightening it overshoots the requested face count.
        /// That objection is not valid on its own: the pipeline's spacing probe re-tunes the lattice
        /// until the count is met, so a tighter bound simply asks for a coarser lattice. This sweep
        /// exists to find out what the bound actually costs once the count is held fixed, which is
        /// the only comparison that means anything.
        /// </remarks>
        static void SweepClusterSpread(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("--- collapse cluster spread sweep (face count held by the spacing probe)");
            report.AppendLine("  case,spread,faces,quadFrac,tris,outIrreg,outIrregPct,hausdorff," +
                              "chamfer,graphVerts,graphMeanVal,rejLarge,largestCluster,clusters,ms");

            var spreads = new[] { 2.25f, 1.75f, 1.5f, 1.25f, 1.0f, 0.75f };

            var cases = new (string name, Func<Mesh> build, int target)[]
            {
                ("torus", () => MeshCorpus.Torus(1f, 0.35f, 128, 64), 2000),
                ("icosphere_80k", () => MeshCorpus.Icosphere(6, 1f), 2000),
                ("cylinder_capped", () => MeshCorpus.CappedCylinder(0.5f, 2f, 96, 24), 2000),
            };

            foreach (var (name, build, target) in cases)
            {
                foreach (var spread in spreads)
                {
                    Mesh mesh = null;
                    try
                    {
                        mesh = build();
                        var reference = PolyMesh.FromMesh(mesh);
                        var referenceCopy = new PolyMesh
                        {
                            positions = (Vector3[])reference.positions.Clone(),
                            faces = reference.faces,
                        };

                        var settings = RemeshSettings.Default;
                        settings.targetQuadCount = target;

                        var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
                        profile.clusterSpread = spread;

                        var result = RemeshPipeline.Run(mesh, settings, profile);
                        var metrics = MeshMetrics.Measure(
                            PolyMesh.FromExtraction(result.extraction), referenceCopy, 20000);

                        var e = result.extraction;
                        var irregular = Mathf.RoundToInt(
                            metrics.irregularFraction * e.positions.Length);

                        report.AppendLine(
                            $"  {name},{spread},{e.faceCount},{e.quadFraction:F4}," +
                            $"{e.triangleCount},{irregular},{metrics.irregularFraction:F4}," +
                            $"{metrics.hausdorffMax:F5},{metrics.chamferMean:F5}," +
                            $"{e.cleanupStats?.graphVertices},{e.cleanupStats?.meanValence:F3}," +
                            $"{e.collapseStats?.collapsesRejectedTooLarge}," +
                            $"{e.collapseStats?.largestCluster},{e.collapseStats?.clusters}," +
                            $"{result.totalMs}");
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"  {name},{spread},threw: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }
                report.AppendLine();
            }
        }

        /// <summary>
        /// `subdivideLongEdges` off against on, on the cases whose floor is known.
        /// </summary>
        /// <remarks>
        /// The claim under test is that discarding an edge the lattice cannot represent is the root
        /// of the defect chain — holes, hole-filler hubs, n-gon splits, triangles — and that
        /// splitting the edge instead removes it. So the row reports the causes as well as the
        /// effect: `discard` is what pass A threw away, `bnd` is what survived tracing, and `tris`
        /// is what the n-gon splitter had to manufacture.
        /// </remarks>
        static void SweepSubdivide(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("--- subdivideLongEdges off vs on (face count held by the spacing probe)");
            report.AppendLine("  case,subdiv,faces,quadFrac,tris,ngons,outIrreg,outIrregPct," +
                              "hausdorff,chamfer,discard,discardPct,bnd,fillAdded,fillVerts," +
                              "ngonSplit,split,rounds,unresolved,solvedTris,ms");

            foreach (var testCase in k_Cases)
            {
                foreach (var on in new[] { false, true })
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
                        profile.subdivideLongEdges = on;

                        var result = RemeshPipeline.Run(mesh, settings, profile);
                        var metrics = MeshMetrics.Measure(
                            PolyMesh.FromExtraction(result.extraction), referenceCopy, 20000);

                        var e = result.extraction;
                        var irregular = Mathf.RoundToInt(
                            metrics.irregularFraction * e.positions.Length);

                        report.AppendLine(
                            $"  {testCase.name},{(on ? "ON" : "off")}," +
                            $"{e.faceCount},{e.quadFraction:F4},{e.triangleCount},{e.ngonCount}," +
                            $"{irregular},{metrics.irregularFraction:F4}," +
                            $"{metrics.hausdorffMax:F5},{metrics.chamferMean:F5}," +
                            $"{e.collapseStats?.discardedEdges}," +
                            $"{e.collapseStats?.DiscardedFraction:F4}," +
                            $"{e.holeFillStats?.boundaryEdgesFound}," +
                            $"{e.holeFillStats?.quadsFromCaps + e.holeFillStats?.quadsFromGrids},{e.holeFillStats?.verticesAdded}," +
                            $"{e.ngonSplitStats?.ngonsSplit}," +
                            $"{e.subdivideStats?.edgesSplit ?? 0}," +
                            $"{e.subdivideStats?.rounds ?? 0}," +
                            $"{e.subdivideStats?.unresolvedEdges ?? 0}," +
                            $"{result.topology.triangleCount},{result.totalMs}");

                        if (e.subdivideStats != null && e.subdivideStats.edgesSplit > 0)
                            report.AppendLine($"      {e.subdivideStats}");
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"  {testCase.name},{(on ? "ON" : "off")},threw: {ex}");
                    }
                    finally
                    {
                        if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }
            }
        }

        /// <summary>
        /// The whole audit, plus the head-to-head on the real assets. Kept as one entry point so a
        /// single run produces everything a session needs to argue about extraction.
        /// </summary>
        public static void RunAll()
        {
            Run();
            RealAssetComparison.Run();
        }

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== LatticeAudit ===");
            report.AppendLine("Inputs whose minimum irregular-vertex count is known from Euler's formula.");
            report.AppendLine("'floor' is that minimum. 'fieldSing' is the cross field's own singular");
            report.AppendLine("triangles; 'outIrreg' is irregular vertices in the extracted mesh. The two");
            report.AppendLine("together say which side of extraction lost the structure.");
            report.AppendLine();

            foreach (var profileKind in new[]
                     {
                         RemeshProfileKind.GameAsset,
                         RemeshProfileKind.Character,
                     })
            {
                report.AppendLine($"--- profile {profileKind}");
                report.AppendLine(
                    "  case,floor,faces,quadFrac,tris,outIrreg,outDefect,fieldSing,indexSum," +
                    "v3,v5,vHigher,orientEnergy,mismatch,same,step,discardDiag,discardJump," +
                    "intgSatisfied,intgResidual,intgSupplyX,intgSupplyY,intgStrandedX,intgStrandedY");

                foreach (var testCase in k_Cases)
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

                        var profile = RemeshProfile.For(profileKind);
                        var result = RemeshPipeline.Run(mesh, settings, profile);

                        var metrics = MeshMetrics.Measure(
                            PolyMesh.FromExtraction(result.extraction), referenceCopy, 20000);
                        var field = FieldDiagnostics.Analyse(
                            result.topology, result.orientation, result.position);

                        // The integrability defect, on the same row. Without it the table cannot
                        // distinguish "the lattice does not tile" from "the lattice tiles and
                        // extraction threw the tiling away", and those imply opposite next steps.
                        var integration = LatticeIntegration.Measure(
                            result.topology, result.orientation, result.position, out var edges);
                        var repair = LatticeRepair.Plan(result.topology, edges);

                        // Irregular vertices as a count, not a fraction: the floor is a count, and a
                        // fraction cannot be compared against it without knowing the resolution.
                        var interior = Mathf.RoundToInt(
                            metrics.irregularFraction * result.extraction.positions.Length);

                        report.AppendLine(
                            $"  {testCase.name},{testCase.floor}," +
                            $"{result.extraction.faceCount}," +
                            $"{result.extraction.quadFraction:F4}," +
                            $"{result.extraction.triangleCount}," +
                            $"{interior}," +
                            $"{metrics.valenceDefect}," +
                            $"{field.singularityCount}," +
                            $"{field.indexSum}," +
                            $"{field.valence3Count},{field.valence5Count},{field.higherOrderCount}," +
                            $"{field.orientationEnergy:F6}," +
                            $"{field.latticeMismatch:F4}," +
                            $"{field.sameCellFraction:F4},{field.oneStepFraction:F4}," +
                            $"{field.discardDiagonalFraction:F4},{field.discardJumpFraction:F4}," +
                            $"{integration.satisfiedFraction:F4},{integration.residualTotal}," +
                            $"{repair.supplyX},{repair.supplyY}," +
                            $"{repair.strandedX},{repair.strandedY}");
                    }
                    catch (Exception e)
                    {
                        report.AppendLine($"  {testCase.name},threw: {e.GetType().Name}: {e.Message}");
                    }
                    finally
                    {
                        if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }

                report.AppendLine();
            }

            // The torus, in full, because it is the row with no alibi: nothing to detect, nothing to
            // adapt to, a defect-free answer available, a perfect field, and an untorn lattice. Every
            // irregular vertex below is manufactured by one of these passes, so this is the only
            // place the responsible pass can be identified.
            report.AppendLine("--- torus, every extraction pass (GameAsset)");
            Mesh torus = null;
            try
            {
                torus = MeshCorpus.Torus(1f, 0.35f, 128, 64);
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = 2000;
                var result = RemeshPipeline.Run(torus, settings,
                    RemeshProfile.For(RemeshProfileKind.GameAsset));

                report.AppendLine($"  {result.extraction}");
                report.AppendLine(result.extraction.Diagnostics());
            }
            catch (Exception e)
            {
                report.AppendLine($"  threw: {e}");
            }
            finally
            {
                if (torus != null) UnityEngine.Object.DestroyImmediate(torus);
            }

            AuditCorpusWinding(report);
            SweepSubdivide(report);
            SweepClusterSpread(report);

            report.AppendLine("=== end LatticeAudit ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
    }
}
