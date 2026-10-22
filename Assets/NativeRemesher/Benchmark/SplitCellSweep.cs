// Does repairing split cells reduce the irregular-vertex count, and what does it cost?
//
// THE CHAIN OF MEASUREMENT THAT LEADS HERE, because this is the fourth thing tried on this defect
// and the first three were refuted:
//
//   the output is 31.4 % irregular against the competitor's 4.1 %       <- the visible defect
//   the FIELD carries 427 singularities, not 1,625                      <- so the field is innocent
//   the tracer is innocent too (43.4 % irregular in, 42.6 % out)
//   98.3 % of triangles are already integrable                          <- so is integer repair
//   more curvature adaptivity does not help, nor does skipping decimation
//
//   18 % of output edges are shorter than 0.75 cells, 2.5 % shorter than half a cell,
//   where a lattice that tiles would put nearly all of them at 1.0      <- THIS
//
// An edge whose endpoints sit half a cell apart is a coin toss between "same cell" and "one step",
// so one cell comes out as two clusters half a cell apart, dividing the four directions between
// them. That is a valence-3 beside a valence-5, which is exactly what the output is full of, and
// `LatticeCollapse.MergeSplitCells` repairs it by merging the halves back.
//
// WHAT TO WATCH BESIDES THE HEADLINE. Merging clusters removes vertices, so the face count falls
// and the pipeline's target compensation has to absorb it; a threshold that merges too much will
// show up as a face count well under the request, and then as deviation.

using System;
using System.Collections.Generic;
using System.Text;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class SplitCellSweep
    {
        const string k_Character = "Benchmarks/reference/ref_high.obj";

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== SplitCellSweep ===");
            report.AppendLine("  Reference on the character at this density: 4,954 faces, " +
                              "4.12 % irregular, 0 triangles.");
            report.AppendLine();
            report.AppendLine("  reflex,merge,faces,quad%,irregular%,val3,val5,val6+,tris," +
                              "short<0.75,UNCOVERED%,deepest%,ms");

            try
            {
                // TWO opposite defects, which is why they are swept together. A cluster spread
                // bound of 2.25 cells lets one cluster own two neighbours in the SAME lattice
                // direction - over-merging - while the half-cell ambiguity splits other cells in
                // two - under-merging. Tightening the bound was not safe before, because a refused
                // collapse falls back to an output edge and the mesh gained faces instead; with the
                // split-cell repair in place the refusals it causes are picked back up.
                if (System.IO.File.Exists(k_Character))
                {
                    var mesh = ObjReader.Read(k_Character);
                    RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);
                    report.AppendLine("  -- character @ 5,215 (field has 427 singularities) --");

                    // COVERAGE IS IN THE TABLE because merging clusters removes vertices, and a
                    // vertex removed from a fingertip is geometry gone. Regularity bought that way
                    // is the same trade that once amputated a thumb while every number the pass was
                    // tuned against improved, and the rule this project took from it is that
                    // anything touching extraction reports coverage.
                    var samples = SpatialGrid.SampleSurface(positions, triangles, 200_000, 20260515);
                    var diagonal = Diagonal(positions);

                    // REFLEX, not merge, is the axis now. The per-stage census showed triangle
                    // pairing is the single largest source of irregular vertices in the whole
                    // pipeline - it added 386 on this model and doubled the icosphere's count,
                    // nearly all of them valence-3 - because re-tiling n+2 faces into n+1 quads
                    // removes a face, and every vertex on the seam loses a neighbour. That pass was
                    // tuned on triangles and quad fraction with no valence column in the table,
                    // which is the mistake these rows exist to correct.
                    foreach (var reflex in new[] { -1f, 0f, 10f, 20f, 40f })
                        Measure(report, positions, triangles, 5215,
                            RemeshProfileKind.Character, LatticeCollapse.defaultMergeBelowCells,
                            -1f, samples, diagonal, reflex);
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }

            report.AppendLine("=== end SplitCellSweep ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void Measure(StringBuilder report, Vector3[] positions, int[] triangles, int target,
            RemeshProfileKind kind, float merge, float spread = -1f,
            List<Vector3> samples = null, float diagonal = 0f, float reflex = -2f)
        {
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = target;

                var profile = RemeshProfile.For(kind);
                profile.mergeSplitCellsBelowCells = merge;
                if (spread > 0f) profile.clusterSpread = spread;

                // Below -1 leaves the pass at its default; -1 turns it off outright, which is the
                // control the others are read against; 0 and above set how far a re-tiled quad may
                // fold before the tiling is refused.
                if (reflex >= 0f) profile.trianglePairingMaxReflexDegrees = reflex;
                else if (reflex >= -1.5f) profile.pairTrianglesAcrossQuads = false;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
                var ms = watch.ElapsedMilliseconds;

                var faces = result.extraction.faces;
                var outPositions = result.extraction.positions;

                Valence(faces, out var irregular, out var interior,
                    out var v3, out var v5, out var v6plus);

                var tris = 0;
                foreach (var face in faces)
                    if (face != null && face.Length == 3) tris++;

                var short75 = ShortEdgeFraction(faces, outPositions, result.scale);

                var uncovered = 0f;
                var deepest = 0f;
                if (samples != null && diagonal > 0f)
                    Coverage(faces, outPositions, samples, diagonal, out uncovered, out deepest);

                report.AppendLine(
                    $"  {(reflex < -1.5f ? "dflt" : reflex < 0f ? "off" : reflex.ToString("F0"))}," +
                    $"{merge:F2},{result.extraction.faceCount}," +
                    $"{result.extraction.quadFraction * 100f:F2}," +
                    $"{100f * irregular / Mathf.Max(1, interior):F2},{v3},{v5},{v6plus},{tris}," +
                    $"{100f * short75:F2},{uncovered:F3},{deepest:F3},{ms}");

                if (result.extraction.collapseStats != null)
                    report.AppendLine($"      {result.extraction.collapseStats}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {spread:F2},{merge:F2},threw: {ex.Message}");
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

        /// <summary>Fraction of output edges shorter than three quarters of a cell.</summary>
        static float ShortEdgeFraction(List<int[]> faces, Vector3[] positions, float scale)
        {
            var seen = new HashSet<long>(EdgeKey.Comparer);
            var shortEdges = 0;
            var total = 0;

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    if (!seen.Add(EdgeKey.PackUndirected(a, b))) continue;

                    total++;
                    if ((positions[a] - positions[b]).magnitude < 0.75f * scale) shortEdges++;
                }
            }

            return total > 0 ? shortEdges / (float)total : 0f;
        }
    }
}
