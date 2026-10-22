// The remesher exactly as the window runs it, headlessly.
//
// Every other real-asset probe here feeds the pipeline a pre-decimated OBJ under a fixed profile.
// The window does neither: it hands over the full-resolution imported mesh, seeds GameAsset, and
// lets Auto Profile choose. Those differences are not cosmetic. An Auto Profile run re-folds the
// chosen profile's settings after the seed's, and a fix made on the fixed-profile path was silently
// undone there -- the audit measured one thing and the user ran another. This harness exists so that
// cannot happen again: it reads the same FBX with the same import settings and calls the same entry
// point with the same arguments as NativeRemesherWindow.
//
// Expects the assets at Headless/Assets/Parity/{head,body}.fbx, copied with their .meta files so the
// importer settings match the user's project.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class EditorParity
    {
        /// <summary>
        /// Mean distance from the input's ridge and valley lines to the output surface: how well the
        /// creases a viewer reads survive. The lines are traced with CurveNetwork's DEFAULT settings
        /// whatever this run used, so every variant is scored against the same set.
        /// </summary>
        static float RidgeError(RemeshResult result)
        {
            var e = result.extraction;
            if (result.topology == null || e?.positions == null) return 0f;

            var savedThin = CurveNetwork.thinCells;
            CurveNetwork.thinCells = 0f;
            var saved = (CurveNetwork.seedTurn, CurveNetwork.continueTurn, CurveNetwork.minCells,
                CurveNetwork.radiusCells, CurveNetwork.bandCells, CurveNetwork.seedAnisotropy);
            (CurveNetwork.seedTurn, CurveNetwork.continueTurn, CurveNetwork.minCells,
                CurveNetwork.radiusCells, CurveNetwork.bandCells, CurveNetwork.seedAnisotropy) =
                (1.0f, 0.5f, 4f, 0.5f, 0.5f, 0.5f);
            var cell = result.topology.EdgeLengthForQuadCount(result.effectiveTargetQuadCount);
            var edges = CurveNetwork.Trace(result.topology, cell, out _, out _);
            (CurveNetwork.seedTurn, CurveNetwork.continueTurn, CurveNetwork.minCells,
                CurveNetwork.radiusCells, CurveNetwork.bandCells, CurveNetwork.seedAnisotropy) = saved;
            CurveNetwork.thinCells = savedThin;
            if (edges.Count == 0) return 0f;

            var triangles = new List<int>();
            foreach (var face in e.faces)
            {
                if (face == null) continue;
                for (var k = 1; k + 1 < face.Length; k++)
                {
                    triangles.Add(face[0]);
                    triangles.Add(face[k]);
                    triangles.Add(face[k + 1]);
                }
            }
            var grid = new SpatialGrid(e.positions, triangles.ToArray());
            var seen = new HashSet<int>();
            double sum = 0;
            foreach (var (a, b) in edges)
                foreach (var v in new[] { a, b })
                    if (seen.Add(v)) sum += grid.NearestDistance(result.topology.positions[v], out _);
            return (float)(sum / seen.Count);
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        static Mesh LargestMesh(string path)
        {
            Mesh best = null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is Mesh mesh && (best == null || mesh.vertexCount > best.vertexCount))
                    best = mesh;
            return best;
        }

        static int Components(List<int[]> faces)
        {
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            foreach (var face in faces)
                foreach (var v in face)
                    if (!parent.ContainsKey(v)) parent[v] = v;
            foreach (var face in faces)
                for (var k = 1; k < face.Length; k++)
                {
                    var a = Find(face[0]);
                    var b = Find(face[k]);
                    if (a != b) parent[a] = b;
                }
            // Find compresses paths, which writes to the dictionary, so walk a copy of the keys.
            var roots = new HashSet<int>();
            foreach (var v in new List<int>(parent.Keys)) roots.Add(Find(v));
            return roots.Count;
        }

        public static void Run()
        {
            var singleTarget = int.TryParse(Arg("-target"), out var t) ? t : 5000;
            // Several targets in one process: a single run is chaotic in the spacing the face-count
            // probe lands on, so a setting is judged on the mean over these rather than on one draw.
            var targets = new List<int>();
            foreach (var part in (Arg("-targets") ?? singleTarget.ToString()).Split(','))
                if (int.TryParse(part, out var value)) targets.Add(value);
            var only = Arg("-asset");
            var which = Arg("-extractor");
            Extraction.IntegerGridMap.optimizePositions = Array.IndexOf(Environment.GetCommandLineArgs(), "-igmNoPos") < 0;
            RemeshPipeline.debugOrientationIterations = int.TryParse(Arg("-orientIters"), out var oi) ? oi : 0;
            var tag = Arg("-tag") ?? "";
            RemeshPipeline.debugCurvatureAdaptivity = float.TryParse(Arg("-adapt"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ad) ? ad : -1f;
            RemeshPipeline.debugCurvatureMaxRatio = float.TryParse(Arg("-maxRatio"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mr) ? mr : -1f;
            RemeshPipeline.debugDecimateBudgetPerQuad = float.TryParse(Arg("-perQuad"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pq) ? pq : -1f;
            RemeshPipeline.debugOrientationHierarchyLevelIterations = int.TryParse(Arg("-orientHier"), out var oh) ? oh : 0;
            if (float.TryParse(Arg("-normSmooth"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ns))
                RemeshPipeline.orientationNormalSmoothingCells = ns;
            RemeshPipeline.debugOrientationNormalSweeps = int.TryParse(Arg("-normSweeps"), out var nsw) ? nsw : 0;
            if (int.TryParse(Arg("-igmNormSweeps"), out var ins)) Extraction.IntegerGridMap.orientationNormalSweeps = ins;
            if (int.TryParse(Arg("-igmOrientHier"), out var ioh)) Extraction.IntegerGridMap.orientationHierarchyLevelIterations = ioh;
            if (float.TryParse(Arg("-curvAlign"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ca))
                Extraction.IntegerGridMap.curvatureAlignment = ca;
            if (float.TryParse(Arg("-curvRadius"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cr))
                Extraction.IntegerGridMap.curvatureRadiusCells = cr;
            if (float.TryParse(Arg("-curvStart"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cst))
                CurvatureDirections.anisotropyStart = cst;
            if (float.TryParse(Arg("-curvFull"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cfu))
                CurvatureDirections.anisotropyFull = cfu;
            RemeshPipeline.debugFeatureAngle = float.TryParse(Arg("-featureAngle"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fa) ? fa : -1f;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-curves") >= 0)
                Extraction.IntegerGridMap.curveNetwork = true;
            if (float.TryParse(Arg("-lenientPair"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lp)) Extraction.IntegerGridMap.lenientPairingReflexDegrees = lp;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-exactCount") >= 0)
                Extraction.IntegerGridMap.curvesAddQuads = false;
            if (float.TryParse(Arg("-aqcGrowth"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var aqg)) Extraction.IntegerGridMap.countMaxGrowth = aqg;
            if (float.TryParse(Arg("-aqcFlat"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var aqf)) Extraction.IntegerGridMap.flatPercentile = aqf;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-noCloseHoles") >= 0)
                Extraction.IntegerGridMap.closeHoles = false;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-placeCluster") >= 0)
                Extraction.IntegerGridMap.placeNearestCluster = true;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-curveAlignOnly") >= 0)
                Extraction.IntegerGridMap.curveNetworkCut = false;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-noCurves") >= 0)
                Extraction.IntegerGridMap.curveNetwork = false;
            if (float.TryParse(Arg("-curveSeed"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cvs)) CurveNetwork.seedTurn = cvs;
            if (float.TryParse(Arg("-curveCont"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cvc)) CurveNetwork.continueTurn = cvc;
            if (float.TryParse(Arg("-curveMin"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cvm)) CurveNetwork.minCells = cvm;
            if (float.TryParse(Arg("-curveRadius"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cvr)) CurveNetwork.radiusCells = cvr;
            if (float.TryParse(Arg("-curveBand"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cvb)) CurveNetwork.bandCells = cvb;
            if (float.TryParse(Arg("-curveCorner"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cvk)) CurveNetwork.cornerDegrees = cvk;
            if (float.TryParse(Arg("-curveGuide"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cvg)) CurveNetwork.guideWeight = cvg;
            if (float.TryParse(Arg("-curveThinTurn"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ctt)) CurveNetwork.thinTurn = ctt;
            if (float.TryParse(Arg("-curveThin"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cvt)) CurveNetwork.thinCells = cvt;
            if (float.TryParse(Arg("-curveAniso"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cva)) CurveNetwork.seedAnisotropy = cva;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-igmGrade") >= 0)
                Extraction.IntegerGridMap.gradeSpacing = true;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-igmNoGrade") >= 0)
                Extraction.IntegerGridMap.gradeSpacing = false;

            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== EditorParity (window path: full-res FBX, GameAsset seed, Auto Profile) ===");
            report.AppendLine("  asset,extractor,chosenProfile,inputTris,faces,quadFrac,interior,irregular,v3,v5,vHigher," +
                              "bndEdges,components,hausdorff,chamfer,ms");

            foreach (var asset in new[] { "head", "body", "ccnb" })
            {
                if (only != null && asset != only) continue;
                // ccnb is the user's textured photogrammetry scan, an OBJ with UV seams -- the input
                // whose raw and welded vertex counts differ.
                var path = System.IO.File.Exists($"Assets/Parity/{asset}.fbx")
                    ? $"Assets/Parity/{asset}.fbx"
                    : $"Assets/Parity/{asset}.obj";
                if (only == null && asset == "ccnb") continue;
                var mesh = LargestMesh(path);
                if (mesh == null)
                {
                    report.AppendLine($"  {asset}: not found under Assets/Parity");
                    continue;
                }

                RemeshTopology.ReadMesh(mesh, out var positions, out var triangles, out var uvs);
                var reference = PolyMesh.FromMesh(mesh);

                foreach (var extractor in new[] { RemeshExtractor.Collapse, RemeshExtractor.IntegerGrid })
                {
                    if (which != null && !string.Equals(which, extractor.ToString(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    double sumIrregularFraction = 0, sumHausdorff = 0, sumChamfer = 0, sumRidge = 0;
                    int sumBoundary = 0, sumComponents = 0, runs = 0;
                    foreach (var target in targets)
                    {
                    try
                    {
                        // Byte for byte what NativeRemesherWindow does at its run site.
                        var settings = RemeshSettings.Default;
                        settings.targetQuadCount = target;
                        var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
                        profile.extractor = extractor;

                        var auto = Array.IndexOf(Environment.GetCommandLineArgs(), "-noAuto") < 0;
                        var result = RemeshPipeline.Solve(positions, triangles, settings, profile, null, uvs, auto);
                        var e = result.extraction;

                        IntegerGridAudit.Valences(e.faces, out var interior, out var irregular, out var v3,
                            out var v5, out var higher, out var boundaryEdges);
                        var metrics = MeshMetrics.Measure(PolyMesh.FromExtraction(e), reference, 20000);

                        var components = Components(e.faces);
                        runs++;
                        sumIrregularFraction += interior > 0 ? irregular / (double)interior : 0;
                        sumBoundary += boundaryEdges;
                        sumComponents += components;
                        sumHausdorff += metrics.hausdorffMax;
                        sumChamfer += metrics.chamferMean;
                        var ridge = RidgeError(result);
                        sumRidge += ridge;
                        report.AppendLine(
                            $"  {asset},{extractor},{result.profile.kind},{triangles.Length / 3}," +
                            $"{e.faceCount},{e.quadFraction:F4},{interior},{irregular},{v3},{v5},{higher}," +
                            $"{boundaryEdges},{Components(e.faces)},{metrics.hausdorffMax:F5}," +
                            $"{metrics.chamferMean:F5},{result.totalMs} ridge={ridge:F5}");
                        var refinedProbe = FeatureGraph.Detect(result.topology,
                            RemeshPipeline.autoProfileProbeAngleDegrees).isFeatureVertex;
                        var marked = 0;
                        foreach (var flag in refinedProbe) if (flag) marked++;
                        report.AppendLine($"      probe: preRefine={result.preRefineProbeFeatureFraction:P1} " +
                            $"onRefined={(refinedProbe.Length > 0 ? marked / (float)refinedProbe.Length : 0f):P1} " +
                            $"used={result.profileProbeFeatureFraction:P1} threshold={RemeshPipeline.suspectFeatureFraction:P0}");
                        report.AppendLine($"      {result.refineStats} ({result.refineMs} ms)");
                        if (result.requestedQuadCount > 0)
                            report.AppendLine($"      adaptive quad count: {result.requestedQuadCount} -> " +
                                              $"{result.effectiveTargetQuadCount} (x{result.curveCountFactor:F2})");
                        if (result.curvatureGuideStats != null)
                            report.AppendLine($"      {result.curvatureGuideStats}");
                        if (result.curveNetworkStats != null)
                        {
                            report.AppendLine($"      {result.curveNetworkStats}");
                            // The curves as OBJ polylines, for rendering over the input.
                            var lines = new StringBuilder();
                            var p = result.topology.positions;
                            foreach (var v in p) lines.AppendLine(FormattableString.Invariant($"v {v.x} {v.y} {v.z}"));
                            var tri = result.topology.triangles;
                            for (var k = 0; k < tri.Length; k += 3)
                                lines.AppendLine($"f {tri[k] + 1} {tri[k + 1] + 1} {tri[k + 2] + 1}");
                            foreach (var (a, b) in result.curveNetworkEdges) lines.AppendLine($"l {a + 1} {b + 1}");
                            System.IO.File.WriteAllText($"Benchmarks/reference/curves_{asset}_{target}{tag}.obj", lines.ToString());
                        }
                        if (result.orientationNormalSweeps > 0)
                            report.AppendLine($"      field normals smoothed over {result.orientationNormalSweeps} sweeps");
                        if (result.localRefineStats.facesBefore > 0)
                            report.AppendLine($"      local {result.localRefineStats} " +
                                              $"worstCells={result.localRefineStats.worstCells:F2}" +
                                              (result.scaleField != null ? $" field={result.scaleField}" : ""));
                        if (e.integerGridStats != null) report.AppendLine($"      {e.integerGridStats}");
                        if (e.relaxStats != null) report.AppendLine($"      relax: {e.relaxStats}");
                        if (e.holeFillStats != null) report.AppendLine($"      holes: {e.holeFillStats}");
                        if (e.trianglePairingStats != null) report.AppendLine($"      pairing: {e.trianglePairingStats}");
                        if (e.trianglePairingAfterNgonStats != null) report.AppendLine($"      pairing2: {e.trianglePairingAfterNgonStats}");
                        if (e.integerGridStats?.subdivideTrace != null && Array.IndexOf(Environment.GetCommandLineArgs(), "-trace") >= 0)
                            report.AppendLine(e.integerGridStats.subdivideTrace);

                        ObjWriter.Write($"Benchmarks/reference/parity_{asset}_{extractor}_{target}{tag}.obj",
                            e.positions, e.faces, asset);
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"  {asset},{extractor},threw: {ex}");
                    }
                    }

                    if (runs > 1)
                        report.AppendLine($"  MEAN {asset},{extractor} over {runs}: irregular " +
                                          $"{sumIrregularFraction / runs:P2} open {sumBoundary / (double)runs:F1} " +
                                          $"pieces {sumComponents / (double)runs:F2} hausdorff " +
                                          $"{sumHausdorff / runs:F5} chamfer {sumChamfer / runs:F5} " +
                                          $"ridge {sumRidge / runs:F5}");
                }
            }

            report.AppendLine("=== end EditorParity ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
    }
}
