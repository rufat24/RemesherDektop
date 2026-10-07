// Per-stage timing at increasing mesh sizes, so a super-linear stage is visible as a scaling
// exponent instead of a guess.
//
// This exists because a 1M-triangle asset took 29 minutes and no measurement in the project could
// say which stage was responsible. Every benchmark row was capped at 25k triangles, which is
// exactly the range where an O(n log n) and an O(n^2) stage look identical.
//
// Run:
//   Unity.exe -batchmode -nographics -projectPath . \
//     -executeMethod NativeRemesher.Benchmark.PerfProbe.RunFromCommandLine -logFile probe.log
//
// Reads the exponent from consecutive rows: each subdivision level is 4x the triangles, so a
// linear stage costs 4x more and a quadratic one 16x.

using System;
using System.Diagnostics;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using NativeRemesher.Extraction;

namespace NativeRemesher.Benchmark
{
    public static class PerfProbe
    {
        /// <summary>Stops a stage that has clearly run away rather than waiting it out.</summary>
        const int k_StageBudgetMs = 240_000;

        public static void RunFromCommandLine()
        {
            var levels = new[] { 4, 5, 6, 7, 8 };
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== PerfProbe ===");
            report.AppendLine("tris,verts,buildTotalMs,orientMs,gridBuildMs,stages");

            foreach (var level in levels)
            {
                Mesh mesh = null;
                try
                {
                    var genWatch = Stopwatch.StartNew();
                    mesh = MeshCorpus.Icosphere(level, 1f);
                    genWatch.Stop();

                    var tris = mesh.triangles.Length / 3;
                    UnityEngine.Debug.Log($"[Probe] level {level}: generated {tris:N0} tris in {genWatch.ElapsedMilliseconds:N0} ms");

                    var row = Probe(mesh, tris, report);
                    UnityEngine.Debug.Log($"[Probe] {row}");

                    if (row.EndsWith("ABORTED", StringComparison.Ordinal))
                    {
                        report.AppendLine("# aborted: a stage exceeded its budget, larger levels skipped");
                        break;
                    }
                }
                catch (Exception e)
                {
                    report.AppendLine($"# level {level} threw: {e.GetType().Name}: {e.Message}");
                    UnityEngine.Debug.LogError($"[Probe] level {level} threw: {e}");
                    break;
                }
                finally
                {
                    if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            report.AppendLine("=== end PerfProbe ===");
            UnityEngine.Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }


        /// <summary>
        /// Times pre-decimation alone, at increasing input size and a fixed output budget.
        ///
        /// Separated from <see cref="RunFromCommandLine"/> because this is the stage the user sees
        /// as "Decimating input", and the stage a 1M-triangle scan sat in for minutes. The output
        /// budget is held constant on purpose: the work is then a function of the INPUT size only,
        /// so the ratio between consecutive rows is the scaling exponent with nothing else moving.
        /// </summary>
        public static void RunDecimateProbe()
        {
            const int k_Budget = 20_000;

            var levels = new[] { 5, 6, 7, 8 };
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== DecimateProbe ===");
            report.AppendLine($"budget={k_Budget:N0} tris");
            report.AppendLine("inTris,outTris,totalMs,stages");

            foreach (var level in levels)
            {
                Mesh mesh = null, decimated = null;
                try
                {
                    mesh = MeshCorpus.Icosphere(level, 1f);
                    var tris = mesh.triangles.Length / 3;

                    var watch = Stopwatch.StartNew();
                    decimated = PreDecimate.Decimate(mesh,
                        DecimateOptions.ForTriangleCount(k_Budget), out var stats);
                    watch.Stop();

                    var line = $"{tris},{stats.outputTriangles},{watch.ElapsedMilliseconds}," +
                               $"{stats.timings}";
                    report.AppendLine(line);
                    UnityEngine.Debug.Log($"[DecimateProbe] {line}");

                    if (watch.ElapsedMilliseconds > k_StageBudgetMs)
                    {
                        report.AppendLine("# aborted: over budget, larger levels skipped");
                        break;
                    }
                }
                catch (Exception e)
                {
                    report.AppendLine($"# level {level} threw: {e.GetType().Name}: {e.Message}");
                    UnityEngine.Debug.LogError($"[DecimateProbe] level {level} threw: {e}");
                    break;
                }
                finally
                {
                    if (decimated != null) UnityEngine.Object.DestroyImmediate(decimated);
                    if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            report.AppendLine("=== end DecimateProbe ===");
            UnityEngine.Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        /// <summary>
        /// Times the whole pipeline on the largest real input available, and prints the per-stage
        /// split.
        /// </summary>
        /// <remarks>
        /// Prefers the user's own scan over a procedural stand-in. A synthetic icosphere has
        /// uniform edge lengths, no boundary and no noise, so it flatters every stage that depends
        /// on the input being well behaved — which is most of them.
        /// </remarks>
        public static void RunPipelineProbe()
        {
            var targets = new[] { 2_000, 8_000 };

            var mesh = LoadScan(out var sourceLabel);
            if (mesh == null)
            {
                mesh = MeshCorpus.Icosphere(8, 1f);
                sourceLabel = "icosphere-8 (no scan found)";
            }

            RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);

            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== PipelineProbe ===");
            report.AppendLine($"input: {sourceLabel}, {triangles.Length / 3:N0} tris, " +
                              $"{positions.Length:N0} verts");
            report.AppendLine("profile,target,totalMs,decimateMs,topoMs,orientMs,posMs,extractMs,faces,quadFrac");

            foreach (var kind in new[] { RemeshProfileKind.Photogrammetry, RemeshProfileKind.GameAsset })
            {
                foreach (var target in targets)
                {
                    try
                    {
                        var settings = RemeshSettings.Default;
                        settings.targetQuadCount = target;

                        var watch = Stopwatch.StartNew();
                        var result = RemeshPipeline.Solve(positions, triangles, settings,
                            RemeshProfile.For(kind));
                        watch.Stop();

                        var faces = result.extraction != null ? result.extraction.faceCount : 0;
                        var quadFrac = result.extraction != null
                            ? result.extraction.quadFraction.ToString("F3") : "-";

                        var line = $"{kind},{target},{watch.ElapsedMilliseconds}," +
                                   $"{result.decimateMs},{result.topologyMs},{result.orientationMs}," +
                                   $"{result.positionMs},{result.extractionMs},{faces},{quadFrac}";
                        report.AppendLine(line);
                        UnityEngine.Debug.Log($"[PipelineProbe] {line}");

                        if (result.decimateStats != null)
                            UnityEngine.Debug.Log($"[PipelineProbe]   decimate: {result.decimateStats}");
                    }
                    catch (Exception e)
                    {
                        report.AppendLine($"# {kind}/{target} threw: {e.GetType().Name}: {e.Message}");
                        UnityEngine.Debug.LogError($"[PipelineProbe] {kind}/{target} threw: {e}");
                    }
                }
            }

            report.AppendLine("=== end PipelineProbe ===");
            UnityEngine.Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        /// <summary>Finds the densest readable mesh among the project's scan assets.</summary>
        /// <summary>The user's reference high-poly asset, or null when it is not on disk.</summary>
        public static Mesh LoadReferenceHigh(out string label)
        {
            const string path = "Benchmarks/reference/ref_high.obj";
            label = path;

            try
            {
                return ObjReader.Read(path);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"cannot read {path}: {ex.Message}");
                return null;
            }
        }

        static Mesh LoadScan(out string label)
        {
            label = null;

            // Read from disk first, so this probe runs in the lean headless mirror as well as in
            // the real project. The distinction this asset carries is worth keeping reachable: the
            // reference pair in Assets/ccnb is a CLOSED manifold, while ccnb.obj has 4,419 edges of
            // genuinely open outline. Only the second can answer whether a reported hole is ours or
            // inherited, and every measurement of holes so far has been taken on the first.
            const string k_ScanPath = "Benchmarks/reference/scan_open.obj";
            if (System.IO.File.Exists(k_ScanPath))
            {
                label = k_ScanPath;
                return ObjReader.Read(k_ScanPath);
            }

#if UNITY_EDITOR
            var candidates = new[] { "Assets/ccnb/ccnb.obj" };

            Mesh best = null;
            foreach (var path in candidates)
            {
                var assets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path);
                if (assets == null) continue;

                foreach (var asset in assets)
                {
                    if (asset is Mesh candidate &&
                        (best == null || candidate.triangles.Length > best.triangles.Length))
                    {
                        best = candidate;
                        label = path;
                    }
                }
            }

            return best;
#else
            return null;
#endif
        }

        /// <summary>
        /// Quad fraction and field residual as a function of sweep count, so the budget is chosen
        /// from a curve instead of a habit.
        /// </summary>
        /// <remarks>
        /// Every benchmark row reports 50/50 sweeps used out of 50/50 allowed, which means the
        /// early-out never fires and the budget — not convergence — is what stops both solvers.
        /// Every quality number the project has ever recorded therefore describes a partially
        /// propagated field, and comparisons between sweep orders at a fixed 50 are comparisons of
        /// how far each got rather than of where each lands.
        ///
        /// This is the missing measurement: what does more actually buy, and does the residual ever
        /// reach the threshold the early-out tests against.
        /// </remarks>
        public static void RunConvergenceProbe()
        {
            var budgets = new[] { 12, 25, 50, 100, 200, 400 };

            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== ConvergenceProbe ===");
            report.AppendLine("mesh,target,sweeps,orientIters,orientResid,posIters,posDrift," +
                              "faces,quadFrac,bnd,orientMs,posMs");

            var cases = new (string name, Func<Mesh> make, int target)[]
            {
                ("icosphere_80k", () => MeshCorpus.Icosphere(6, 1f), 2000),
                ("torus", () => MeshCorpus.Torus(1f, 0.35f, 128, 48), 500),
                ("cube_subdivided", () => MeshCorpus.SubdividedCube(1f, 24), 500),
            };

            foreach (var (name, make, target) in cases)
            {
                Mesh mesh = null;
                try
                {
                    mesh = make();
                    var topology = RemeshTopology.Build(mesh);
                    var scale = topology.EdgeLengthForQuadCount(target);

                    foreach (var sweeps in budgets)
                    {
                        var orientWatch = Stopwatch.StartNew();
                        var orientation = OrientationField.Solve(topology, sweeps, null);
                        orientWatch.Stop();

                        var posWatch = Stopwatch.StartNew();
                        var position = PositionField.Solve(topology, orientation, scale, sweeps);
                        posWatch.Stop();

                        var options = ExtractionOptions.Default;
                        var extraction = QuadExtractor.Extract(topology, orientation, position, options);

                        var line = $"{name},{target},{sweeps}," +
                                   $"{orientation.iterationsRun},{orientation.lastResidual:E3}," +
                                   $"{position.iterationsRun},{position.representativeDrift:E3}," +
                                   $"{extraction.faceCount},{extraction.quadFraction:F4}," +
                                   $"{extraction.holeFillStats?.boundaryEdgesFound ?? -1}," +
                                   $"{orientWatch.ElapsedMilliseconds},{posWatch.ElapsedMilliseconds}";
                        report.AppendLine(line);
                        UnityEngine.Debug.Log($"[ConvergenceProbe] {line}");
                    }
                }
                catch (Exception e)
                {
                    report.AppendLine($"# {name} threw: {e.GetType().Name}: {e.Message}");
                    UnityEngine.Debug.LogError($"[ConvergenceProbe] {name} threw: {e}");
                }
                finally
                {
                    if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            report.AppendLine("=== end ConvergenceProbe ===");
            UnityEngine.Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        /// <summary>
        /// Diagnoses the three failures reported on a real scan: no texture, holes, and output that
        /// does not read as a quad mesh. One row per target, with the numbers that decide each.
        /// </summary>
        public static void RunScanDiagnostic()
        {
            var targets = new[] { 8_000, 50_000 };

            var mesh = LoadScan(out var sourceLabel);
            if (mesh == null)
            {
                UnityEngine.Debug.LogError("[ScanDiag] no scan asset found");
                return;
            }

            RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);
            var sourceUvs = mesh.uv;

            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== ScanDiagnostic ===");
            report.AppendLine($"input: {sourceLabel}, {triangles.Length / 3:N0} tris, " +
                              $"{positions.Length:N0} verts, sourceUVs={sourceUvs.Length:N0}");

            // Does the INPUT have a boundary? This is what gates hole filling, and a scan is an
            // open surface, so the gate refuses to run at all.
            var inputTopology = RemeshTopology.Build(positions, triangles);
            report.AppendLine($"input boundaryEdgeCount={inputTopology.boundaryEdgeCount:N0} " +
                              $"nonManifoldEdgeCount={inputTopology.nonManifoldEdgeCount:N0}");
            report.AppendLine("target,faces,quads,quadFrac,ngons,tris,holeFill,outBnd,meshTopo,meshUVs,ms");

            foreach (var target in targets)
            {
                try
                {
                    var settings = RemeshSettings.Default;
                    settings.targetQuadCount = target;

                    var watch = Stopwatch.StartNew();
                    var result = RemeshPipeline.Solve(positions, triangles, settings,
                        RemeshProfile.For(RemeshProfileKind.Photogrammetry), null, sourceUvs);
                    watch.Stop();

                    RemeshPipeline.BuildOutputMesh(result, "diag");

                    var e = result.extraction;

                    // Boundary edges in the emitted face set: the holes the user can see.
                    var incidence = new Dictionary<long, int>(EdgeKey.Comparer);
                    foreach (var face in e.faces)
                        for (var i = 0; i < face.Length; i++)
                        {
                            var key = EdgeKey.PackUndirected(face[i], face[(i + 1) % face.Length]);
                            incidence.TryGetValue(key, out var c);
                            incidence[key] = c + 1;
                        }
                    var outBnd = 0;
                    foreach (var c in incidence.Values) if (c == 1) outBnd++;

                    var ngons = 0;
                    var tris = 0;
                    foreach (var face in e.faces)
                    {
                        if (face.Length == 3) tris++;
                        else if (face.Length > 4) ngons++;
                    }

                    var topo = new StringBuilder();
                    for (var sub = 0; sub < result.mesh.subMeshCount; sub++)
                    {
                        if (sub > 0) topo.Append('+');
                        topo.Append(result.mesh.GetTopology(sub));
                        topo.Append('x');
                        topo.Append(result.mesh.GetIndexCount(sub));
                    }

                    // Deviation of the INPUT surface from the OUTPUT. This is the direction that
                    // detects both failure modes a hole fix can produce: a hole leaves input points
                    // with nothing near them, and a fan spike over a large rim leaves them far from
                    // the flat lid that replaced their relief. Normalised by the bounding-box
                    // diagonal so the numbers are comparable across meshes.
                    var outTris = new List<int>(e.faceCount * 6);
                    foreach (var face in e.faces)
                    {
                        if (face == null || face.Length < 3) continue;
                        for (var i = 1; i + 1 < face.Length; i++)
                        {
                            outTris.Add(face[0]);
                            outTris.Add(face[i]);
                            outTris.Add(face[i + 1]);
                        }
                    }

                    var outGrid = new SpatialGrid(e.positions, outTris.ToArray());
                    var samplePoints = SpatialGrid.SampleSurface(positions, triangles, 40000, 12345, null);

                    var bbMin = positions[0];
                    var bbMax = positions[0];
                    for (var i = 1; i < positions.Length; i++)
                    {
                        bbMin = Vector3.Min(bbMin, positions[i]);
                        bbMax = Vector3.Max(bbMax, positions[i]);
                    }
                    var invDiag = 1f / Mathf.Max((bbMax - bbMin).magnitude, 1e-20f);

                    double devSum = 0;
                    var devMax = 0f;
                    var devCount = 0;
                    foreach (var s in samplePoints)
                    {
                        var d = outGrid.NearestDistance(s, out _) * invDiag;
                        if (float.IsInfinity(d)) continue;
                        devSum += d;
                        if (d > devMax) devMax = d;
                        devCount++;
                    }
                    var devMean = devCount > 0 ? devSum / devCount : 0.0;

                    var line = $"{target},{e.faceCount},{e.quadCount},{e.quadFraction:F4}," +
                               $"{ngons},{tris},[{e.holeFillStats}],{outBnd}," +
                               $"{topo},{result.mesh.uv.Length},{watch.ElapsedMilliseconds}," +
                               $"dev={devMean:F5}/{devMax:F5}";
                    report.AppendLine(line);
                    UnityEngine.Debug.Log($"[ScanDiag] {line}");

                    UnityEngine.Object.DestroyImmediate(result.mesh);
                }
                catch (Exception ex)
                {
                    report.AppendLine($"# {target} threw: {ex.GetType().Name}: {ex.Message}");
                    UnityEngine.Debug.LogError($"[ScanDiag] {target} threw: {ex}");
                }
            }

            report.AppendLine("=== end ScanDiagnostic ===");
            UnityEngine.Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        /// <summary>
        /// Decimation alone, on the reference input, with the guards' rejections split by cause.
        /// </summary>
        /// <remarks>
        /// Isolated from the pipeline because the full solve is 40-170 s per profile and the
        /// question here is answered in the first stage. Pre-decimation applied zero collapses to
        /// the 1.5M-triangle reference input and rejected all 2,247,091 candidates on the
        /// normal-flip guard, which left the field solving at 10.9x the lattice spacing it is
        /// documented to work at. Quality on photogrammetry input has never been limited by the
        /// solver; it has been limited by this stage doing nothing at all.
        /// </remarks>
        public static void RunDecimateDiagnostic()
        {
            const string k_InputPath = "Benchmarks/reference/ref_high.obj";

            Mesh mesh;
            try
            {
                mesh = ObjReader.Read(k_InputPath);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[DecDiag] cannot read {k_InputPath}: {ex.Message}");
                return;
            }

            RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);

            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== DecimateDiagnostic ===");
            report.AppendLine($"input: {triangles.Length / 3:N0} tris, {positions.Length:N0} verts");

            // 54,740 is what the Photogrammetry profile asks for at the reference's 5,474 faces.
            // The others bracket it so a failure that depends on how far the decimation has to go
            // is distinguishable from one that does not.
            var budgets = new[] { 750_000, 200_000, 54_740 };

            foreach (var budget in budgets)
            {
                var watch = Stopwatch.StartNew();
                PreDecimate.Decimate(positions, triangles,
                    DecimateOptions.ForTriangleCount(budget), out var stats,
                    out _, out _);
                watch.Stop();

                var line = $"budget={budget:N0} -> {stats.outputTriangles:N0} tris in " +
                           $"{watch.ElapsedMilliseconds} ms";
                report.AppendLine(line);
                report.AppendLine($"  {stats}");
                UnityEngine.Debug.Log($"[DecDiag] {line} | {stats}");
            }

            report.AppendLine("=== end DecimateDiagnostic ===");
            UnityEngine.Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());

        }

        /// <summary>
        /// Scores our output against a third-party retopology of the same asset, at its face count.
        /// </summary>
        /// <remarks>
        /// Assets/ccnb holds a matched pair: a 1.5M-triangle input and a 5,474-face quad
        /// retopology of it, produced by a tool the user considers correct. That pair is worth more
        /// than any synthetic corpus row, because it fixes the target instead of leaving it to
        /// taste, and because the input differs from ccnb.obj in a way that changes the diagnosis:
        /// it is a CLOSED manifold, zero boundary edges. Every hole in our output on this input is
        /// therefore ours, with nothing inherited from an open scan to hide behind.
        ///
        /// The output is written as OBJ rather than scored in-process on purpose. Unity's importer
        /// runs with keepQuads off and ToMesh splits n-gons into a second submesh, so a quad
        /// fraction read back through either is a statement about Unity's triangulator. Writing
        /// faces exactly as extracted lets the same external script score both meshes, which is the
        /// only way the comparison means anything.
        /// </remarks>

        /// <summary>
        /// One corpus case, both ways, with the full extraction diagnostics and an OBJ each.
        ///
        /// Written for cylinder_capped, which is the only corpus row the hierarchy made clearly
        /// worse: worst-case deviation 0.0008 -> 0.2008 at target 2000 while every other case
        /// improved. A CSV row cannot say whether that is the field, the tracer or the hole filler,
        /// and guessing between those has cost this project two sessions before.
        /// </summary>
        public static void RunCaseDiagnostic()
        {
            const int k_Target = 2000;
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== CaseDiagnostic ===");

            foreach (var levels in new[] { 6, -1 })
            {
                Mesh mesh = null;
                try
                {
                    mesh = MeshCorpus.CappedCylinder(0.5f, 2f, 96, 24);

                    var settings = RemeshSettings.Default;
                    settings.targetQuadCount = k_Target;

                    var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
                    profile.hierarchyLevelIterations = levels;

                    var result = RemeshPipeline.Run(mesh, settings, profile);

                    report.AppendLine($"--- hierarchyLevelIterations={levels} " +
                                      $"({(levels > 0 ? "hierarchical" : "finest level only")})");
                    report.AppendLine($"  {result.extraction}");
                    report.AppendLine(result.extraction.Diagnostics());

                    var path = $"Benchmarks/reference/cyl-{(levels > 0 ? "hier" : "flat")}.obj";
                    ObjWriter.Write(path, result.extraction.positions, result.extraction.faces,
                        "cylinder_capped");
                    report.AppendLine($"  wrote {path}");
                }
                catch (Exception e)
                {
                    report.AppendLine($"# levels={levels} threw: {e}");
                }
                finally
                {
                    if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            report.AppendLine("=== end CaseDiagnostic ===");
            UnityEngine.Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
        public static void RunReferenceComparison()
        {
            const string k_InputPath = "Benchmarks/reference/ref_high.obj";
            const int k_ReferenceFaces = 5_474;

            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== ReferenceComparison ===");

            Mesh mesh;
            try
            {
                mesh = ObjReader.Read(k_InputPath);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[RefCmp] cannot read {k_InputPath}: {ex.Message}");
                return;
            }

            RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);
            var inputTopology = RemeshTopology.Build(positions, triangles);

            report.AppendLine($"input: {k_InputPath}, {triangles.Length / 3:N0} tris, " +
                              $"{positions.Length:N0} verts, " +
                              $"boundary={inputTopology.boundaryEdgeCount:N0}, " +
                              $"nonManifold={inputTopology.nonManifoldEdgeCount:N0}");
            report.AppendLine($"reference target: {k_ReferenceFaces:N0} faces, quadFrac 0.9289");
            report.AppendLine("profile,target,faces,quads,quadFrac,tris,ngons,outBnd,dev,ms,obj");

            var bbMin = positions[0];
            var bbMax = positions[0];
            for (var i = 1; i < positions.Length; i++)
            {
                bbMin = Vector3.Min(bbMin, positions[i]);
                bbMax = Vector3.Max(bbMax, positions[i]);
            }
            var invDiag = 1f / Mathf.Max((bbMax - bbMin).magnitude, 1e-20f);
            var samplePoints = SpatialGrid.SampleSurface(positions, triangles, 40000, 12345, null);

            var kinds = new[]
            {
                RemeshProfileKind.Photogrammetry,
                RemeshProfileKind.Character,
                RemeshProfileKind.GameAsset,
            };

            foreach (var kind in kinds)
            {
                try
                {
                    var settings = RemeshSettings.Default;
                    settings.targetQuadCount = k_ReferenceFaces;

                    var watch = Stopwatch.StartNew();
                    var result = RemeshPipeline.Solve(positions, triangles, settings,
                        RemeshProfile.For(kind));
                    watch.Stop();

                    var e = result.extraction;

                    var incidence = new Dictionary<long, int>(EdgeKey.Comparer);
                    foreach (var face in e.faces)
                        for (var i = 0; i < face.Length; i++)
                        {
                            var key = EdgeKey.PackUndirected(face[i], face[(i + 1) % face.Length]);
                            incidence.TryGetValue(key, out var c);
                            incidence[key] = c + 1;
                        }
                    var outBnd = 0;
                    foreach (var c in incidence.Values) if (c == 1) outBnd++;

                    var tris = 0;
                    var ngons = 0;
                    foreach (var face in e.faces)
                    {
                        if (face.Length == 3) tris++;
                        else if (face.Length > 4) ngons++;
                    }

                    var outTris = new List<int>(e.faceCount * 6);
                    foreach (var face in e.faces)
                    {
                        if (face == null || face.Length < 3) continue;
                        for (var i = 1; i + 1 < face.Length; i++)
                        {
                            outTris.Add(face[0]);
                            outTris.Add(face[i]);
                            outTris.Add(face[i + 1]);
                        }
                    }

                    var outGrid = new SpatialGrid(e.positions, outTris.ToArray());
                    double devSum = 0;
                    var devMax = 0f;
                    var devCount = 0;
                    foreach (var s in samplePoints)
                    {
                        var d = outGrid.NearestDistance(s, out _) * invDiag;
                        if (float.IsInfinity(d)) continue;
                        devSum += d;
                        if (d > devMax) devMax = d;
                        devCount++;
                    }
                    var devMean = devCount > 0 ? devSum / devCount : 0.0;

                    var objPath = $"Benchmarks/reference/ours-{kind}.obj";
                    ObjWriter.Write(objPath, e.positions, e.faces, $"ours_{kind}");

                    var line = $"{kind},{k_ReferenceFaces},{e.faceCount},{e.quadCount}," +
                               $"{e.quadFraction:F4},{tris},{ngons},{outBnd}," +
                               $"{devMean:F5}/{devMax:F5},{watch.ElapsedMilliseconds},{objPath}";
                    report.AppendLine(line);
                    report.AppendLine($"  profile:  {result.profile.Describe()}");
                    report.AppendLine($"  decimate: {(result.decimateStats == null ? "DID NOT RUN" : result.decimateStats.ToString())}");
                    report.AppendLine($"  solved:   {result.topology.triangleCount:N0} tris, " +
                                      $"{result.topology.vertexCount:N0} verts, " +
                                      $"ceiling={result.topology.MaxQuadCountForInput():N0}, " +
                                      $"effectiveTarget={result.effectiveTargetQuadCount:N0}, " +
                                      $"clamped={result.targetWasClamped}, scale={result.scale:F6}");

                    // Average valence over the emitted face set. This is the number that separates
                    // a retopology from a decimation, and neither quad fraction nor deviation shows
                    // it: a pure quad grid sits at 4, an unchanged triangulation at 6, and a
                    // triangle graph with pairs glued into quads lands in between while its quad
                    // fraction still reads respectably. The reference retopology is 4.07.
                    var corners = 0;
                    var valence = new Dictionary<int, int>(e.faceCount * 2);
                    foreach (var face in e.faces)
                    {
                        corners += face.Length;
                        foreach (var c in face)
                        {
                            valence.TryGetValue(c, out var d);
                            valence[c] = d + 1;
                        }
                    }
                    var used = valence.Count;
                    var meanValence = used > 0 ? corners / (float)used : 0f;
                    report.AppendLine($"  valence:  mean={meanValence:F3} over {used:N0} " +
                                      "used verts (reference 4.07 over 5,282)");

                    // Both sides of extraction, in one place. The output's irregular-vertex count
                    // alone cannot say whether a noisy result came from a noisy field or from an
                    // extraction manufacturing irregularity out of a clean one, because extraction
                    // sits between the measurement and the cause.
                    var singular = SingularityAnalysis.Analyse(result.topology, result.orientation);
                    var irregular = 0;
                    foreach (var d in valence.Values)
                        if (d != 4) irregular++;
                    if (result.hierarchy != null)
                    {
                        report.AppendLine($"  hier:     {result.hierarchy.buildTimings}");
                        report.AppendLine($"  posLvl:   {result.positionLevels}");
                    }

                    report.AppendLine($"  field:    {singular}");

                    // The per-edge lattice classification, with the discard split by cause. The
                    // total discard rate was being read as a verdict on the field; it is not, and
                    // only the jump share ever was — a diagonal discard is what a triangulated
                    // input does to a correct lattice.
                    report.AppendLine($"  lattice:  " +
                                      FieldDiagnostics.Analyse(result.topology, result.orientation,
                                          result.position).Summary());

                    // Whether the integer lattice is integrable at all — the constraint Instant
                    // Meshes never states and QuadriFlow solves. A triangle whose three edge
                    // offsets do not sum to zero is a place where no assignment of cells satisfies
                    // all three of its edges, so nothing downstream can repair it.
                    var integration = LatticeIntegration.Measure(
                        result.topology, result.orientation, result.position, out var latticeEdges);
                    report.AppendLine($"  integr:   {integration}");

                    // And whether that defect can be ROUTED away, which is the question the integer
                    // solve exists to answer. Reported before anything acts on it, so the plan and
                    // its price are on the record independently of the repair.
                    var repair = LatticeRepair.Plan(result.topology, latticeEdges);
                    report.AppendLine($"  repair:   {repair}");
                    report.AppendLine($"  output:   irregular={irregular:N0}/{used:N0} " +
                                      $"({(used > 0 ? irregular / (float)used : 0f):P1})" +
                                      " (reference 596/5,282 = 11.3%)");

                    // Winding consistency, and normal agreement with the input surface.
                    //
                    // Two faces sharing an edge are consistently wound if and only if they traverse
                    // that edge in opposite directions. A single inconsistent face is invisible from
                    // outside once backface culling runs, which is why this is measured here rather
                    // than trusted: it presents as a HOLE, and the boundary-edge count says zero at
                    // the same time. The corpus reports windingConsistent on every row, but every
                    // corpus mesh is a clean unit-scale primitive, which is the same blind spot that
                    // hid the normalisation bug.
                    var directed = new Dictionary<long, int>(EdgeKey.Comparer);
                    var reversed = 0;
                    var sameDirection = 0;
                    foreach (var face in e.faces)
                        for (var i = 0; i < face.Length; i++)
                        {
                            var from = face[i];
                            var to = face[(i + 1) % face.Length];
                            var forward = EdgeKey.Pack(from, to);
                            var backward = EdgeKey.Pack(to, from);

                            if (directed.TryGetValue(backward, out var b) && b > 0)
                            {
                                reversed++;
                                directed[backward] = b - 1;
                                continue;
                            }

                            directed.TryGetValue(forward, out var f2);
                            directed[forward] = f2 + 1;
                        }

                    // Anything left pointing the same way as another face's traversal of the same
                    // edge is an inconsistency; a leftover with no partner at all is a boundary.
                    foreach (var kv in directed)
                        if (kv.Value > 1) sameDirection += kv.Value - 1;

                    // Do the emitted face normals agree with the input surface they came from?
                    var agreeing = 0;
                    var disagreeing = 0;
                    foreach (var face in e.faces)
                    {
                        var geometric = QuadExtractor.FaceNormal(e.positions, face);
                        var surface = QuadExtractor.AverageNormal(e.normals, face);
                        if (MeshMath.CosAngleBetween(geometric, surface) < 0f) disagreeing++;
                        else agreeing++;
                    }

                    report.AppendLine($"  winding:  paired={reversed:N0} sameDirection={sameDirection:N0} " +
                                      $"| faceNormals agree={agreeing:N0} disagree={disagreeing:N0}");
                    report.AppendLine($"  {e.Diagnostics()}");
                    UnityEngine.Debug.Log($"[RefCmp] {line}");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"# {kind} threw: {ex.GetType().Name}: {ex.Message}");
                    UnityEngine.Debug.LogError($"[RefCmp] {kind} threw: {ex}");
                }
            }

            report.AppendLine("=== end ReferenceComparison ===");
            UnityEngine.Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static string Probe(Mesh mesh, int tris, StringBuilder report)
        {
            // RemeshTopology.Build is one call, so the sub-stages are timed by rebuilding the parts
            // that are separable and subtracting. Total is what matters; the split says where.
            var total = Stopwatch.StartNew();
            var topology = RemeshTopology.Build(mesh);
            total.Stop();

            var verts = topology.vertexCount;
            var buildMs = total.ElapsedMilliseconds;

            if (buildMs > k_StageBudgetMs)
            {
                var aborted = $"{tris},{verts},{buildMs},-,-,{topology.buildTimings} ABORTED";
                report.AppendLine(aborted);
                return aborted;
            }

            var orientWatch = Stopwatch.StartNew();
            var orientation = OrientationField.Solve(topology, 1, null);
            orientWatch.Stop();

            var gridWatch = Stopwatch.StartNew();
            var grid = new SpatialGrid(topology.positions, topology.triangles);
            gridWatch.Stop();
            GC.KeepAlive(grid);
            GC.KeepAlive(orientation);

            var line = $"{tris},{verts},{buildMs},{orientWatch.ElapsedMilliseconds}," +
                       $"{gridWatch.ElapsedMilliseconds},{topology.buildTimings}";
            report.AppendLine(line);
            return line;
        }
    }
}
