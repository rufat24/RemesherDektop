// Where a correct field turns into a disordered mesh.
//
// THE MEASUREMENT THAT FORCED THIS. On an icosphere the orientation field carries exactly EIGHT
// singularities with index sum 8 - the theoretical minimum for a sphere, exactly what Euler
// requires, which is as correct as a cross field on a sphere can be. The mesh extracted from that
// same run is 12.6 % irregular at 8,000 quads and 24.6 % at 500: on the order of a thousand
// irregular vertices out of a field that has eight. The tracer is not doing it either (43.4 %
// irregular in, 42.6 % out on the character). So it happens between the field and the graph, which
// is this file's subject.
//
// WHAT IS ASKED. Every input edge is classified on the integer lattice: same cell (collapse), one
// step (an output edge), or a diagonal / two-plus cells (discarded). Three questions follow, and
// none of them has been asked before:
//
//   1. HOW ARE THE CLASSES DISTRIBUTED? A healthy extraction discards diagonals - roughly a third
//      of the edges of a triangulated surface - and almost nothing else. Discarding TWO-STEP links
//      instead means the lattice is finer than the mesh can see, and every one of them is a missing
//      neighbour, which is a valence-3 vertex.
//
//   2. HOW BIG ARE THE CELLS, in input vertices? A cell holding one or two vertices cannot be
//      distinguished from its neighbours reliably; the edges that should say "same cell" and "one
//      step" are the same length.
//
//   3. HOW FAR APART ARE ADJACENT CLUSTERS, in cells? This is the one that separates the two
//      theories. If they sit about one cell apart, the lattice tiles and the irregularity is
//      bookkeeping. If a large share sit at HALF a cell, then single cells are being split into two
//      clusters - and a split cell shows up as exactly the cancelling valence-5/valence-3 pair the
//      output is full of.
//
// The icosphere is the control and the character is the subject, because a defect that appears on
// both cannot be blamed on scan noise.

using System;
using System.Collections.Generic;
using System.Text;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class CollapseProbe
    {
        const string k_Character = "Benchmarks/reference/ref_high.obj";

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== CollapseProbe ===");

            try
            {
                var sphere = MeshCorpus.Icosphere(4, 1f);
                RemeshTopology.ReadMesh(sphere, out var spherePositions, out var sphereTriangles);
                Probe(report, "icosphere (field has 8 singularities)", spherePositions,
                    sphereTriangles, 2000, RemeshProfileKind.GameAsset);
                UnityEngine.Object.DestroyImmediate(sphere);

                if (System.IO.File.Exists(k_Character))
                {
                    var mesh = ObjReader.Read(k_Character);
                    RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);
                    Probe(report, "character", positions, triangles, 5215,
                        RemeshProfileKind.Character);
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
                else
                {
                    report.AppendLine($"  {k_Character} missing - character arm skipped");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }

            report.AppendLine("=== end CollapseProbe ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void Probe(StringBuilder report, string name, Vector3[] positions, int[] triangles,
            int target, RemeshProfileKind kind)
        {
            report.AppendLine();
            report.AppendLine($"  --- {name}, {target:N0} quads ---");

            var settings = RemeshSettings.Default;
            settings.targetQuadCount = target;

            var profile = RemeshProfile.For(kind);
            profile.recordCensus = true;
            var result = RemeshPipeline.Solve(positions, triangles, settings, profile);

            var topology = result.topology;
            var position = result.position;
            var orientation = result.orientation;
            var scale = result.scale;

            report.AppendLine($"  {topology.vertexCount:N0} input verts -> " +
                              $"{result.extraction.faceCount:N0} faces");

            // THE FIELD ITSELF. Inferring this from the icosphere was not good enough: a scan's
            // normals are noisy, and a noisy cross field has genuine holonomy that extraction is
            // obliged to reproduce. If the field already carries hundreds of singularities then the
            // collapse is innocent and the fault is upstream, which is the opposite fix.
            var singular = SingularityAnalysis.Analyse(topology, orientation);
            report.AppendLine($"  FIELD: {singular}");

            // The stage-by-stage census. The field's singularity count above is the floor a perfect
            // extraction would reach, so every line below that rises above it names a stage that is
            // manufacturing irregularity rather than carrying it.
            if (result.extraction.census != null)
            {
                report.AppendLine("  CENSUS (the field's count is the floor):");
                foreach (var row in result.extraction.census)
                    report.AppendLine($"      {row}");
            }
            if (result.extraction.collapseStats != null)
                report.AppendLine($"  collapse: {result.extraction.collapseStats}");

            // 1. The class histogram, recomputed here from the public classifier so it is this
            //    file's own measurement and not a number handed over by the thing under test.
            var classes = new int[4];              // collapse, edge, diagonal, two-plus
            var stepHistogram = new int[8];        // max(|dx|,|dy|), clamped
            var pairs = 0;

            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;

            for (var i = 0; i < topology.vertexCount; i++)
            {
                for (var k = start[i]; k < start[i + 1]; k++)
                {
                    var j = adjacency[k];
                    if (j <= i) continue;   // each undirected edge once
                    pairs++;

                    OrientationField.Compat4(
                        orientation.directions[i], topology.normals[i],
                        orientation.directions[j], topology.normals[j],
                        out var q0, out var q1);

                    PositionField.CompatIndex(
                        topology.positions[i], topology.normals[i], q0, position.positions[i],
                        topology.positions[j], topology.normals[j], q1, position.positions[j],
                        position.SpacingAt(i), 1f / position.SpacingAt(i),
                        position.SpacingAt(j), 1f / position.SpacingAt(j),
                        out var shift0, out var shift1, out _);

                    var dx = Mathf.Abs(shift0.x - shift1.x);
                    var dy = Mathf.Abs(shift0.y - shift1.y);
                    var step = Mathf.Max(dx, dy);
                    stepHistogram[Mathf.Min(step, stepHistogram.Length - 1)]++;

                    if (dx == 0 && dy == 0) classes[0]++;
                    else if (dx + dy == 1) classes[1]++;
                    else if (dx == 1 && dy == 1) classes[2]++;
                    else classes[3]++;
                }
            }

            report.AppendLine($"  {pairs:N0} input edges classified:");
            report.AppendLine($"      same cell (collapse)  {classes[0],8:N0}  {Pct(classes[0], pairs)}");
            report.AppendLine($"      one step (edge)       {classes[1],8:N0}  {Pct(classes[1], pairs)}");
            report.AppendLine($"      diagonal (discarded)  {classes[2],8:N0}  {Pct(classes[2], pairs)}");
            report.AppendLine($"      TWO OR MORE (lost)    {classes[3],8:N0}  {Pct(classes[3], pairs)}" +
                              "   <- every one of these is a neighbour the graph never gets");

            var steps = new StringBuilder();
            for (var s = 0; s < stepHistogram.Length; s++)
                if (stepHistogram[s] > 0) steps.Append($" {s}:{stepHistogram[s]:N0}");
            report.AppendLine($"  max(|dx|,|dy|) histogram:{steps}");

            // 2. Cell occupancy: input vertices per output cluster. Below about four, a cell is not
            //    distinguishable from its neighbours by edge classification alone.
            var perCluster = result.extraction.collapseStats != null
                             && result.extraction.collapseStats.clusters > 0
                ? topology.vertexCount / (float)result.extraction.collapseStats.clusters
                : 0f;
            report.AppendLine($"  input vertices per cluster: {perCluster:F2} " +
                              $"(cell edge {scale:F5})");

            // 3. The distance between adjacent output vertices, in cells. The histogram that
            //    separates "the lattice tiles" from "cells are being split in two".
            var faces = result.extraction.faces;
            var outPositions = result.extraction.positions;
            var seen = new HashSet<long>(EdgeKey.Comparer);
            var bins = new int[12];
            var measured = 0;

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    if (!seen.Add(EdgeKey.PackUndirected(a, b))) continue;

                    var d = (outPositions[a] - outPositions[b]).magnitude / Mathf.Max(1e-12f, scale);
                    bins[Mathf.Clamp(Mathf.FloorToInt(d * 4f), 0, bins.Length - 1)]++;
                    measured++;
                }
            }

            report.AppendLine($"  output edge length in cells, {measured:N0} edges " +
                              "(a tiling lattice puts nearly all of them in 0.75-1.25):");
            for (var b = 0; b < bins.Length; b++)
            {
                if (bins[b] == 0) continue;
                var lo = b * 0.25f;
                report.AppendLine($"      {lo:F2}-{lo + 0.25f:F2}  {bins[b],8:N0}  {Pct(bins[b], measured)}");
            }

            if (result.extraction.directionStats != null)
                report.AppendLine($"  directions: {result.extraction.directionStats}");
        }

        static string Pct(int part, int total) =>
            total > 0 ? $"{100f * part / total,6:F2} %" : "     -";
    }
}
