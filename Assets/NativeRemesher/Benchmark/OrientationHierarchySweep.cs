// Does combing the cross field coarse-to-fine reduce singularities, and does that reach the output?
//
// WHY THIS IS BEING MEASURED AGAIN. The hierarchical orientation solve existed once. It was swept,
// scored, judged worthless and deleted, and HierarchicalSolve's own header still carries the table
// that killed it:
//
//     no hierarchy        0.8422          <- quad fraction
//     orientations only   0.8389
//     positions only      0.8718
//     both                0.8770
//
// Every column there is quad fraction. Quad fraction counts how many faces have four corners; a
// singularity is a VERTEX whose valence is not four, and a mesh can be 100 % quads with every
// vertex irregular. The metric was structurally incapable of seeing the only thing this solve does.
//
// This is the second time that exact mistake has been made in this project. The triangle-pairing
// tolerance was set to 40 degrees on four columns that all agreed and reversed to 10 the moment
// irregular-vertex fraction was added to the table. The rule was written down in Benchmarks/README
// after the first time, and then the same trap caught the same person again, which is worth more
// than the rule was.
//
// WHAT THE THEORY PREDICTS, so it can be wrong. A Gauss-Seidel sweep carries information one edge.
// A flat solve on 50,000 vertices therefore produces a field that is locally smooth and globally
// arbitrary: lots of cancelling +/- singularity pairs, each one a place the field turned and turned
// back. The character's field carries 427 of them (206 positive, 204 negative - very nearly all
// cancelling) where the competitor's ENTIRE OUTPUT has 204 irregular vertices. If the reach
// argument is right, coarse-to-fine should cut indexPlus and indexMinus together while indexSum
// stays fixed, because the sum is Euler's and no solver may change it.
//
// THE THREE WAYS THIS CAN FAIL, all of which have a column:
//
//   1. It reduces field singularities and extraction manufactures them back anyway. The census
//      column answers this directly: watch whether the drop at `field` survives to `final`.
//   2. It costs geometry. A smoother field means straighter edge flow, and straighter flow over a
//      finger or a nose means the lattice stops following the shape. UNCOVERED% is why coverage is
//      in every table in this project.
//   3. It breaks features. The original deletion noted the cube's feature-fidelity test failing,
//      because restricting a crease direction onto a coarse normal averaged across the crease
//      points it nowhere. RestrictConstraints now DROPS such a constraint rather than pinning the
//      coarse solve to a degenerate direction - so HardSurface is in this sweep, and if it still
//      regresses the flag gets turned off for that profile rather than the solve being deleted
//      twice.

using System;
using System.Collections.Generic;
using System.Text;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class OrientationHierarchySweep
    {
        const string k_Character = "Benchmarks/reference/ref_high.obj";

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== OrientationHierarchySweep ===");
            report.AppendLine("  Reference on the character at this density: 4,954 faces, " +
                              "4.12 % irregular, 0 triangles.");
            report.AppendLine();

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

                    var samples = SpatialGrid.SampleSurface(positions, triangles, 200_000, 20260515);
                    var diagonal = Diagonal(positions);

                    report.AppendLine("  sweeps,faces,quad%,irregular%,val3,val5,val6+,tris," +
                                      "sing+,sing-,singSum,UNCOVERED%,deepest%,ms");

                    report.AppendLine("  -- character @ 5,215, Character profile --");
                    foreach (var sweeps in new[] { -1, 3, 6, 12 })
                        Measure(report, positions, triangles, 5215, RemeshProfileKind.Character,
                            sweeps, samples, diagonal);

                    // The profile the first version regressed. If the drop-degenerate-constraints
                    // fix is not enough, it shows here and nowhere else.
                    report.AppendLine("  -- character @ 5,215, HardSurface profile --");
                    foreach (var sweeps in new[] { -1, 6 })
                        Measure(report, positions, triangles, 5215, RemeshProfileKind.HardSurface,
                            sweeps, samples, diagonal);

                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }

            report.AppendLine("=== end OrientationHierarchySweep ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void Measure(StringBuilder report, Vector3[] positions, int[] triangles, int target,
            RemeshProfileKind kind, int sweeps, List<Vector3> samples, float diagonal)
        {
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = target;

                var profile = RemeshProfile.For(kind);
                profile.orientationHierarchyLevelIterations = sweeps;
                profile.recordCensus = true;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
                var ms = watch.ElapsedMilliseconds;

                var faces = result.extraction.faces;
                Valence(faces, out var irregular, out var interior,
                    out var v3, out var v5, out var v6plus);

                var tris = 0;
                foreach (var face in faces)
                    if (face != null && face.Length == 3) tris++;

                // The point of the whole exercise: what the FIELD carries, before extraction has
                // had any say. indexSum is Euler's and must not move; the two counts either side
                // of it are what a solve with more reach is supposed to cancel against each other.
                var singular = SingularityAnalysis.Analyse(result.topology, result.orientation);

                Coverage(faces, result.extraction.positions, samples, diagonal,
                    out var uncovered, out var deepest);

                report.AppendLine(
                    $"  {(sweeps < 0 ? "off" : sweeps.ToString())}," +
                    $"{result.extraction.faceCount}," +
                    $"{result.extraction.quadFraction * 100f:F2}," +
                    $"{100f * irregular / Mathf.Max(1, interior):F2},{v3},{v5},{v6plus},{tris}," +
                    $"{singular.indexPlus},{singular.indexMinus},{singular.indexSum}," +
                    $"{uncovered:F3},{deepest:F3},{ms}");

                // The census is what separates "the field improved" from "the output improved".
                // Without it a drop at the field and a rise in extraction cancel into a null
                // result, which is exactly how the first version of this solve was misread.
                if (result.extraction.census != null)
                {
                    var line = new StringBuilder("      census:");
                    foreach (var stage in result.extraction.census)
                        line.Append($" {stage.stage}={stage.irregular}");
                    report.AppendLine(line.ToString());
                }

                if (result.orientationLevels != null)
                    report.AppendLine($"      {result.orientationLevels}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {sweeps},threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Interior vertices whose valence is not four. Boundary vertices are excluded: an open rim
        /// has a valence deficit that is not a singularity and counting it hides the real number.
        /// </summary>
        static void Valence(List<int[]> faces, out int irregular, out int interior,
            out int v3, out int v5, out int v6plus)
        {
            var valence = new Dictionary<int, int>();
            var uses = new Dictionary<long, int>(EdgeKey.Comparer);

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    valence.TryGetValue(a, out var used);
                    valence[a] = used + 1;

                    var key = EdgeKey.PackUndirected(a, b);
                    uses.TryGetValue(key, out var count);
                    uses[key] = count + 1;
                }
            }

            var onBoundary = new HashSet<int>();
            foreach (var entry in uses)
            {
                if (entry.Value == 2) continue;
                onBoundary.Add(EdgeKey.First(entry.Key));
                onBoundary.Add(EdgeKey.Second(entry.Key));
            }

            irregular = 0;
            interior = 0;
            v3 = 0;
            v5 = 0;
            v6plus = 0;

            foreach (var entry in valence)
            {
                if (onBoundary.Contains(entry.Key)) continue;
                interior++;
                if (entry.Value == 4) continue;

                irregular++;
                if (entry.Value == 3) v3++;
                else if (entry.Value == 5) v5++;
                else if (entry.Value >= 6) v6plus++;
            }
        }

        /// <summary>
        /// Fraction of the ORIGINAL surface with no output within half a quad, and the worst gap.
        /// </summary>
        static void Coverage(List<int[]> faces, Vector3[] positions, List<Vector3> samples,
            float diagonal, out float uncoveredPercent, out float deepestPercent)
        {
            var indices = new List<int>(faces.Count * 6);
            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;
                for (var k = 1; k + 1 < face.Length; k++)
                {
                    indices.Add(face[0]);
                    indices.Add(face[k]);
                    indices.Add(face[k + 1]);
                }
            }

            var grid = new SpatialGrid(positions, indices.ToArray());
            var threshold = 0.0025f * diagonal;

            var uncovered = 0;
            var deepest = 0f;
            foreach (var sample in samples)
            {
                var d = grid.NearestDistance(sample, out _);
                if (d > threshold) uncovered++;
                deepest = Mathf.Max(deepest, d);
            }

            uncoveredPercent = 100f * uncovered / Mathf.Max(1, samples.Count);
            deepestPercent = 100f * deepest / diagonal;
        }

        static float Diagonal(Vector3[] positions)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            return (max - min).magnitude;
        }
    }
}
