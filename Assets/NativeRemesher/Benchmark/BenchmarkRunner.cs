// The benchmark entry points: a menu item for interactive use, and a headless command line that
// exits non-zero on regression so it can gate a commit.
//
// Two behaviours here are deliberate and worth not "tidying away":
//
//  * Every mesh runs inside its own try/catch. The corpus intentionally contains non-manifold,
//    self-intersecting and degenerate meshes whose whole purpose is to try to crash the solver. One
//    of them throwing must produce a row that says so, not abort the other forty rows.
//
//  * The target quad count is clamped against RemeshTopology.MaxQuadCountForInput and the clamp is
//    recorded on the row. Field-aligned remeshing samples the lattice at input vertices, so it can
//    only coarsen; an unreachable target produces output that looks exactly like a solver bug.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NativeRemesher.Benchmark
{
    public sealed class BenchmarkOptions
    {
        public string corpus = "procedural";     // procedural | external | all
        public int[] targets = { 500, 2000, 8000 };
        public string outPath = "Benchmarks/run.csv";
        public string baselinePath = "Benchmarks/baseline.csv";
        public float tolerance = 0.02f;
        public int geometrySamples = 60000;
        public bool includeOracle = true;
        /// <summary>Skip meshes above this triangle count. 0 = no limit.</summary>
        public int maxTriangles;

        /// <summary>Profiles to measure. Each adds a row per (mesh, target).</summary>
        public RemeshProfileKind[] profiles = { RemeshProfileKind.GameAsset };

        /// <summary>Also measure with pre-decimation off, to show what the stage buys.</summary>
        public bool includeNoDecimateBaseline;

        /// <summary>
        /// Force surface relaxation off on every profile, for an A/B against a run with it on.
        /// </summary>
        /// <remarks>
        /// Needed because the baseline CSV is older than several changes at once, so a straight
        /// comparison against it attributes every regression to whatever landed last. Relaxation
        /// moves vertices and cannot touch topology, so an A/B is also the only way to show that:
        /// if the topology columns are bit-identical between the two runs, they are not its doing.
        /// </remarks>
        public bool disableRelaxation;

        /// <summary>
        /// Overrides every profile's curvature adaptivity when non-negative; -1 leaves each
        /// profile's own value alone.
        /// </summary>
        /// <remarks>
        /// Here so that a regression against a baseline can be ATTRIBUTED. A baseline recorded
        /// before both curvature-graded sizing and the relaxation pass cannot say which of them
        /// moved a number, and guessing at that is how this project previously convinced itself a
        /// working stage was broken. `-relax off -adaptivity 0` reproduces the old configuration
        /// exactly, so anything still regressing is neither of them.
        /// </remarks>
        public float adaptivityOverride = -1f;

        public override string ToString()
        {
            return $"corpus={corpus} targets=[{string.Join(",", targets)}] out={outPath} " +
                   $"baseline={baselinePath} tolerance={tolerance} samples={geometrySamples} " +
                   $"oracle={includeOracle} maxTris={maxTriangles}";
        }
    }

    public static class BenchmarkRunner
    {
        [MenuItem("Tools/Native Remesher/Run Benchmark", false, 101)]
        public static void RunFromMenu()
        {
            var options = new BenchmarkOptions
            {
                // Interactive runs skip the biggest mesh and use fewer samples: the point is a fast
                // answer while iterating. The gate uses the full set.
                maxTriangles = 120000,
                geometrySamples = 20000,
                outPath = "Benchmarks/run-interactive.csv"
            };

            var rows = Execute(options, out var regressions, out var notes);

            Debug.Log("[Benchmark] Results\n" + BenchmarkReport.ToMarkdown(rows));
            foreach (var note in notes) Debug.Log("[Benchmark] " + note);

            if (regressions.Count == 0)
                Debug.Log($"[Benchmark] {rows.Count} rows, no regressions past {options.tolerance:P0}.");
            else
                foreach (var r in regressions) Debug.LogWarning("[Benchmark] REGRESSION " + r);
        }

        /// <summary>
        /// Headless entry point.
        /// <code>
        /// Unity.exe -batchmode -nographics -projectPath . \
        ///   -executeMethod NativeRemesher.Benchmark.BenchmarkRunner.RunFromCommandLine \
        ///   -corpus procedural -targets 500,2000,8000 \
        ///   -out Benchmarks/run.csv -baseline Benchmarks/baseline.csv -tolerance 0.02
        /// </code>
        /// </summary>
        public static void RunFromCommandLine()
        {
            var exitCode = 0;

            try
            {
                var options = ParseArgs(Environment.GetCommandLineArgs());
                Debug.Log($"[Benchmark] {options}");

                var rows = Execute(options, out var regressions, out var notes);

                var markdownPath = Path.ChangeExtension(options.outPath, ".md");
                File.WriteAllText(markdownPath, BenchmarkReport.ToMarkdown(rows));

                Console.WriteLine(BenchmarkReport.ToMarkdown(rows));
                foreach (var note in notes) Console.WriteLine("NOTE: " + note);

                if (regressions.Count > 0)
                {
                    foreach (var r in regressions) Console.WriteLine("REGRESSION: " + r);
                    Console.WriteLine($"FAILED: {regressions.Count} regressions past {options.tolerance:P0}.");
                    exitCode = 1;
                }
                else
                {
                    Console.WriteLine($"OK: {rows.Count} rows, no regressions.");
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Console.WriteLine("FAILED: " + e);
                exitCode = 2;
            }

            if (Application.isBatchMode)
                EditorApplication.Exit(exitCode);
        }

        // ------------------------------------------------------------------
        public static List<MeshMetrics> Execute(BenchmarkOptions options,
            out List<BenchmarkReport.Regression> regressions, out List<string> notes)
        {
            var entries = new List<CorpusEntry>();
            if (options.corpus == "procedural" || options.corpus == "all")
                entries.AddRange(MeshCorpus.Procedural());
            if (options.corpus == "external" || options.corpus == "all")
                entries.AddRange(MeshCorpus.External());

            var rows = new List<MeshMetrics>();
            var context = RunContext.Current();

            var totalWork = Mathf.Max(1, entries.Count * options.targets.Length);
            var done = 0;

            foreach (var entry in entries)
            {
                Mesh mesh = null;
                try
                {
                    mesh = entry.create();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Benchmark] Could not build corpus mesh '{entry.name}': {e.Message}");
                    done += options.targets.Length;
                    continue;
                }

                var triangleCount = mesh.triangles.Length / 3;
                if (options.maxTriangles > 0 && triangleCount > options.maxTriangles)
                {
                    Debug.Log($"[Benchmark] Skipping '{entry.name}' ({triangleCount:N0} tris > " +
                              $"{options.maxTriangles:N0} limit).");
                    done += options.targets.Length;
                    UnityEngine.Object.DestroyImmediate(mesh);
                    continue;
                }

                foreach (var target in options.targets)
                {
                    if (!Application.isBatchMode)
                    {
                        EditorUtility.DisplayProgressBar("Benchmark",
                            $"{entry.name} @ {target} quads", done / (float)totalWork);
                    }
                    done++;

                    foreach (var kind in options.profiles)
                    {
                        var profile = RemeshProfile.For(kind);
                        if (options.disableRelaxation) profile.relaxSurface = false;
                        if (options.adaptivityOverride >= 0f)
                            profile.curvatureAdaptivity = options.adaptivityOverride;
                        rows.Add(RunOne(entry, mesh, target, options, profile, kind.ToString()));
                    }

                    if (options.includeNoDecimateBaseline)
                    {
                        var raw = RemeshProfile.Default;
                        raw.preDecimate = false;
                        rows.Add(RunOne(entry, mesh, target, options, raw, "noDecimate"));
                    }
                }

                if (options.includeOracle)
                {
                    var oracle = TryMeasureOracle(entry, mesh, options);
                    if (oracle != null) rows.Add(oracle);
                }

                UnityEngine.Object.DestroyImmediate(mesh);
            }

            if (!Application.isBatchMode)
                EditorUtility.ClearProgressBar();

            BenchmarkReport.WriteCsv(options.outPath, rows, context);
            regressions = BenchmarkReport.CompareToBaseline(options.baselinePath, rows,
                options.tolerance, out notes);

            return rows;
        }

        static MeshMetrics RunOne(CorpusEntry entry, Mesh mesh, int target, BenchmarkOptions options,
            RemeshProfile profile, string profileName)
        {
            var row = new MeshMetrics
            {
                meshName = entry.name,
                targetQuadCount = target,
                profile = profileName,
                variant = "native"
            };

            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = target;

                var before = GC.GetTotalMemory(false);
                var result = RemeshPipeline.Run(mesh, settings, profile);
                row.managedBytesDelta = GC.GetTotalMemory(false) - before;

                row.topologyMs = result.topologyMs;
                row.orientationMs = result.orientationMs;
                row.positionMs = result.positionMs;
                row.extractionMs = result.extractionMs;
                row.totalMs = result.totalMs;

                var output = PolyMesh.FromExtraction(result.extraction);
                var input = PolyMesh.FromTopology(result.topology);

                var measured = MeshMetrics.Measure(output, input, options.geometrySamples);
                CopyQuality(measured, row);

                row.spacingSpread = result.scaleField?.spread ?? 1f;

                var field = FieldDiagnostics.Analyse(result.topology, result.orientation, result.position);
                FieldDiagnostics.CopyInto(field, row);

                // Ratio is against the EFFECTIVE target — after the pipeline clamped it to the
                // coarsen-only ceiling. Scoring against an unreachable request would report a
                // failure the remesher had no way to avoid.
                row.maxQuadCountForInput = result.topology.MaxQuadCountForInput();
                row.targetWasClamped = result.targetWasClamped;
                if (result.targetWasClamped)
                    row.notes = MeshMetrics.AppendNote(row.notes,
                        $"target {target} clamped to {result.effectiveTargetQuadCount} " +
                        "(coarsen-only ceiling)");

                row.faceCountRatio = result.effectiveTargetQuadCount > 0
                    ? row.outputFaces / (float)result.effectiveTargetQuadCount
                    : 0f;

                if (result.faceCountAttempts > 1)
                    row.notes = MeshMetrics.AppendNote(row.notes,
                        $"{result.faceCountAttempts} face-count passes");

                if (result.decimateStats != null)
                    row.notes = MeshMetrics.AppendNote(row.notes,
                        $"pre-decimated {result.decimateStats.inputTriangles}->" +
                        $"{result.decimateStats.outputTriangles} tris");

                if (result.mesh != null)
                    UnityEngine.Object.DestroyImmediate(result.mesh);

                Debug.Log($"[Benchmark] {row}\n           {field.Summary()}");
            }
            catch (Exception e)
            {
                // A corpus mesh that breaks the solver must still produce a row. Losing the whole
                // run to one bad input is how a robustness corpus stops being run at all.
                row.notes = MeshMetrics.AppendNote(row.notes, "FAILED: " + e.GetType().Name + ": " + e.Message);
                Debug.LogWarning($"[Benchmark] {entry.name} @ {target} failed: {e.Message}");
            }

            return row;
        }

        /// <summary>
        /// Scores a hand-baked Exoside QuadRemesher result for the same mesh, if one is present at
        /// <c>Assets/QuadRemesher/Output/&lt;name&gt;*.fbx</c>. Read-only: the bridge is never driven
        /// programmatically. Every type in QuadRemesher.Editor is internal with an empty asmdef
        /// reference list, so calling it would need reflection plus its EditorApplication.update
        /// polling plus Selection — fragile headless, and not worth it for a reference row that
        /// changes only when the corpus does.
        /// </summary>
        static MeshMetrics TryMeasureOracle(CorpusEntry entry, Mesh input, BenchmarkOptions options)
        {
            const string k_OutputFolder = "Assets/QuadRemesher/Output";
            if (!Directory.Exists(k_OutputFolder)) return null;

            string found = null;
            foreach (var path in Directory.GetFiles(k_OutputFolder, entry.name + "*.fbx",
                         SearchOption.TopDirectoryOnly))
            {
                found = path.Replace('\\', '/');
                break;
            }
            if (found == null) return null;

            var row = new MeshMetrics
            {
                meshName = entry.name,
                targetQuadCount = 0,
                profile = "exoside",
                variant = "oracle"
            };

            try
            {
                var asset = AssetDatabase.LoadAssetAtPath<Mesh>(found);
                if (asset == null)
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(found);
                    var filter = go != null ? go.GetComponentInChildren<MeshFilter>() : null;
                    asset = filter != null ? filter.sharedMesh : null;
                }

                if (asset == null)
                {
                    row.notes = "oracle FBX present but no mesh could be loaded";
                    return row;
                }

                var output = PolyMesh.FromMesh(asset);
                var inputPoly = PolyMesh.FromTopology(RemeshTopology.Build(input));

                var measured = MeshMetrics.Measure(output, inputPoly, options.geometrySamples);
                CopyQuality(measured, row);

                if (row.quadCount == 0 && row.triangleCount > 0)
                    row.notes = MeshMetrics.AppendNote(row.notes,
                        "FBX import triangulated the quads; quadFraction here is a property of the " +
                        "file, not of the engine that produced it");

                Debug.Log($"[Benchmark] {row}");
            }
            catch (Exception e)
            {
                row.notes = MeshMetrics.AppendNote(row.notes, "FAILED: " + e.Message);
            }

            return row;
        }

        static void CopyQuality(MeshMetrics from, MeshMetrics to)
        {
            to.inputVertices = from.inputVertices;
            to.inputTriangles = from.inputTriangles;
            to.outputVertices = from.outputVertices;
            to.outputFaces = from.outputFaces;

            to.quadCount = from.quadCount;
            to.triangleCount = from.triangleCount;
            to.ngonCount = from.ngonCount;
            to.quadFraction = from.quadFraction;
            to.faceSizeHistogram = from.faceSizeHistogram;
            to.valenceHistogram = from.valenceHistogram;
            to.boundaryVertices = from.boundaryVertices;
            to.valenceDefect = from.valenceDefect;
            to.irregularFraction = from.irregularFraction;

            to.boundaryEdges = from.boundaryEdges;
            to.nonManifoldEdges = from.nonManifoldEdges;
            to.isolatedVertices = from.isolatedVertices;
            to.duplicateFaces = from.duplicateFaces;
            to.degenerateFaces = from.degenerateFaces;
            to.eulerCharacteristic = from.eulerCharacteristic;
            to.windingConsistent = from.windingConsistent;
            to.isWatertight = from.isWatertight;
            to.planarityMean = from.planarityMean;
            to.planarityP95 = from.planarityP95;

            to.chamferMean = from.chamferMean;
            to.chamferP95 = from.chamferP95;
            to.hausdorffMax = from.hausdorffMax;
            to.normalConsistency = from.normalConsistency;
            to.normalFlippedFraction = from.normalFlippedFraction;

            to.notes = MeshMetrics.AppendNote(to.notes, from.notes);
        }

        // ------------------------------------------------------------------
        static BenchmarkOptions ParseArgs(string[] args)
        {
            var options = new BenchmarkOptions();
            var culture = CultureInfo.InvariantCulture;

            // Valueless flags first, so one appearing as the final argument is not skipped by the
            // pair loop below.
            foreach (var arg in args)
                if (arg == "-noOracle")
                    options.includeOracle = false;

            foreach (var arg in args)
                if (arg == "-compareDecimate")
                    options.includeNoDecimateBaseline = true;

            for (var i = 0; i < args.Length - 1; i++)
            {
                var value = args[i + 1];
                switch (args[i])
                {
                    case "-corpus":
                        options.corpus = value;
                        break;
                    case "-targets":
                        var parts = value.Split(',');
                        var targets = new List<int>();
                        foreach (var part in parts)
                            if (int.TryParse(part.Trim(), NumberStyles.Integer, culture, out var t))
                                targets.Add(t);
                        if (targets.Count > 0) options.targets = targets.ToArray();
                        break;
                    case "-out":
                        options.outPath = value;
                        break;
                    case "-baseline":
                        options.baselinePath = value;
                        break;
                    case "-tolerance":
                        if (float.TryParse(value, NumberStyles.Float, culture, out var tol))
                            options.tolerance = tol;
                        break;
                    case "-samples":
                        if (int.TryParse(value, NumberStyles.Integer, culture, out var samples))
                            options.geometrySamples = samples;
                        break;
                    case "-profiles":
                        {
                            var kinds = new List<RemeshProfileKind>();
                            foreach (var part in value.Split(','))
                                if (Enum.TryParse<RemeshProfileKind>(part.Trim(), true, out var kind))
                                    kinds.Add(kind);
                            if (kinds.Count > 0) options.profiles = kinds.ToArray();
                        }
                        break;
                    case "-relax":
                        options.disableRelaxation = value == "off" || value == "false" || value == "0";
                        break;
                    case "-adaptivity":
                        if (float.TryParse(value, NumberStyles.Float, culture, out var adaptivity))
                            options.adaptivityOverride = adaptivity;
                        break;
                    case "-maxTriangles":
                        if (int.TryParse(value, NumberStyles.Integer, culture, out var maxTris))
                            options.maxTriangles = maxTris;
                        break;
                }
            }

            return options;
        }
    }
}
