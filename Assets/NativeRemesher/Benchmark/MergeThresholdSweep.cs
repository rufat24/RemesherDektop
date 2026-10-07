// Sweeps the two merge thresholds together, on the character, at QuadRemesher's face count.
//
// WHY BOTH AT ONCE. `QuadRegularizer` refuses 60 merges on quality and 25 on convexity out of 156
// candidates, and `TrianglePairing` has its own threshold. Relaxing either trades one defect for
// another, and they trade against the SAME metric, so tuning one at a time would attribute the
// result to whichever happened to be swept last.
//
// WHAT TO READ. The competitor comparison established that the visible defect is needle faces, and
// that 88 % of ours touch a non-quad. So relaxing should raise quad fraction AND raise the min-angle
// percentiles - the leftover triangle was the thing dragging them down. If instead quad fraction
// rises while p01 falls, the relaxation is manufacturing bad quads rather than removing bad
// triangles, and it is the wrong trade. Both columns are here so that cannot be misread.
//
// QuadRemesher, for reference: 99.99 % quads, min angle p01 47.1, p05 63.4, worst 15.3.

using System;
using System.Collections.Generic;
using System.Text;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class MergeThresholdSweep
    {
        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== MergeThresholdSweep (character @ QuadRemesher's face count) ===");
            report.AppendLine("QuadRemesher: 99.99 % quads, minAngle p01 47.1, p05 63.4, worst 15.3");
            report.AppendLine();
            report.AppendLine("  regQual,pairQual,chain,faces,quadPct,tris,ngons,minAngP01," +
                              "minAngP05,minAngWorst,nonManifold,components,ms");

            Mesh original = null;
            try
            {
                original = LoadOriginal();
                if (original == null)
                {
                    report.AppendLine("  Benchmarks/reference/ref_high.obj missing - skipped");
                    Debug.Log(report.ToString());
                    Console.WriteLine(report.ToString());
                    return;
                }

                RemeshTopology.ReadMesh(original, out var positions, out var triangles);
                PreDecimate.Decimate(positions, triangles,
                    DecimateOptions.ForTriangleCount(217_800),
                    out _, out var decimatedPositions, out var decimatedTriangles);

                // (regularizer quality, pairing quality, max chain)
                var configs = new (float reg, float pair, int chain)[]
                {
                    (0.15f, 1f, 0),        // pairing off: today's behaviour before this work
                    (0.15f, 0.05f, 1),     // hexagon only, the first version
                    (0.15f, 0.05f, 4),     // chains up to 4 quads
                    (0.08f, 0.05f, 4),     // regularizer relaxed too
                    (0.03f, 0.02f, 4),     // both loose
                    (0.03f, 0.02f, 8),     // and longer chains
                };

                foreach (var config in configs)
                    Measure(report, decimatedPositions, decimatedTriangles, config.reg,
                        config.pair, config.chain);
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
            }

            report.AppendLine("=== end MergeThresholdSweep ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void Measure(StringBuilder report, Vector3[] positions, int[] triangles,
            float regularizerQuality, float pairingQuality, int maxChain)
        {
            try
            {
                var topology = RemeshTopology.Build(positions, triangles);
                var profile = RemeshProfile.For(RemeshProfileKind.Character).Validated();

                var scale = topology.EdgeLengthForQuadCount(21_780);
                var orientation = OrientationField.Solve(topology,
                    Mathf.Max(1, profile.orientationIterations), null, 12345);
                var hierarchy = MeshHierarchy.Build(topology);
                var position = HierarchicalSolve.Positions(hierarchy, orientation, scale,
                    Mathf.Max(1, profile.hierarchyLevelIterations),
                    Mathf.Max(1, profile.positionIterations));

                var options = ExtractionOptions.Default;
                options.mergeQualityThreshold = regularizerQuality;

                // A chain limit of 0 means the pass is off, which is the honest control: running it
                // with an unreachable threshold would still pay for the search.
                options.pairTrianglesAcrossQuads = maxChain > 0;
                options.trianglePairingQualityThreshold = pairingQuality;
                options.trianglePairingMaxChainQuads = Mathf.Max(1, maxChain);

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = QuadExtractor.Extract(topology, orientation, position, options);
                var ms = watch.ElapsedMilliseconds;

                Score(result.faces, result.positions, out var angles, out var nonManifold,
                    out var components);

                report.AppendLine(
                    $"  {regularizerQuality:F2},{pairingQuality:F2},{maxChain}," +
                    $"{result.faceCount},{result.quadFraction * 100f:F2}," +
                    $"{result.triangleCount},{result.ngonCount}," +
                    $"{Percentile(angles, 0.01f):F1},{Percentile(angles, 0.05f):F1}," +
                    $"{(angles.Length > 0 ? angles[0] : 0f):F1}," +
                    $"{nonManifold},{components},{ms}");

                // The pass's own account of what it refused. Three hypotheses about why
                // triangles survive have now been wrong in a row - chain length, threshold, and
                // patch overlap - each guessed instead of read off these counters.
                if (result.trianglePairingStats != null)
                    report.AppendLine($"      {result.trianglePairingStats}");
                report.AppendLine($"      {result.regularizeStats}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {regularizerQuality:F2},{pairingQuality:F2},{maxChain}," +
                                  $"threw: {ex.Message}");
            }
        }

        static void Score(List<int[]> faces, Vector3[] positions, out float[] sortedAngles,
            out int nonManifoldEdges, out int components)
        {
            var angles = new List<float>(faces.Count);
            var edgeUse = new Dictionary<long, int>(faces.Count * 4, EdgeKey.Comparer);

            foreach (var face in faces)
            {
                var worst = 180f;
                for (var k = 0; k < face.Length; k++)
                {
                    var previous = positions[face[(k + face.Length - 1) % face.Length]];
                    var current = positions[face[k]];
                    var following = positions[face[(k + 1) % face.Length]];

                    var a = previous - current;
                    var b = following - current;
                    var lengthA = a.magnitude;
                    var lengthB = b.magnitude;

                    if (lengthA < 1e-12f || lengthB < 1e-12f) { worst = 0f; continue; }

                    var cos = Mathf.Clamp(Vector3.Dot(a, b) / (lengthA * lengthB), -1f, 1f);
                    var angle = Mathf.Acos(cos) * Mathf.Rad2Deg;
                    if (angle < worst) worst = angle;

                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    edgeUse.TryGetValue(key, out var used);
                    edgeUse[key] = used + 1;
                }

                angles.Add(worst);
            }

            nonManifoldEdges = 0;
            foreach (var used in edgeUse.Values)
                if (used > 2) nonManifoldEdges++;

            sortedAngles = angles.ToArray();
            Array.Sort(sortedAngles);

            components = CountComponents(faces);
        }

        static int CountComponents(List<int[]> faces)
        {
            if (faces.Count == 0) return 0;

            var edgeToFaces = new Dictionary<long, List<int>>(faces.Count * 4, EdgeKey.Comparer);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    if (!edgeToFaces.TryGetValue(key, out var list))
                        edgeToFaces[key] = list = new List<int>(2);
                    list.Add(f);
                }
            }

            var seen = new bool[faces.Count];
            var components = 0;
            var queue = new Queue<int>();

            for (var seed = 0; seed < faces.Count; seed++)
            {
                if (seen[seed]) continue;

                components++;
                seen[seed] = true;
                queue.Clear();
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    var face = faces[f];
                    for (var k = 0; k < face.Length; k++)
                    {
                        var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                        foreach (var g in edgeToFaces[key])
                        {
                            if (seen[g]) continue;
                            seen[g] = true;
                            queue.Enqueue(g);
                        }
                    }
                }
            }

            return components;
        }

        static float Percentile(float[] sorted, float fraction)
        {
            if (sorted.Length == 0) return 0f;
            var index = Mathf.Clamp(Mathf.RoundToInt(fraction * (sorted.Length - 1)),
                0, sorted.Length - 1);
            return sorted[index];
        }

        static Mesh LoadOriginal()
        {
            const string path = "Benchmarks/reference/ref_high.obj";
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"[MergeThresholdSweep] missing {path}");
                return null;
            }

            return ObjReader.Read(path);
        }
    }
}
