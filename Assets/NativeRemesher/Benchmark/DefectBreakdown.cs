// WHERE v1's irregular vertices actually come from, on the real assets.
//
// WHY THIS EXISTS. v1 produces 2,149 irregular vertices on the character where QuadRemesher produces
// 341. That is the whole quality gap in one number, and "2,149" says nothing about what to fix. This
// attributes each one to a cause, so effort goes where the count is instead of where the last idea
// was.
//
// THE DISCIPLINE THIS FILE ENFORCES, learned expensively. Two parallel architectures were built and
// removed after being tuned against a torus, an icosphere and a bumpy sphere - inputs on which their
// failure mode could not appear. So this measures the character and ccnb and nothing else. A
// primitive may be added when there is a specific reason to want one, never as the default corpus.
//
// AND: a defect count means nothing on a mesh that is not connected. v3 scored perfectly on quad
// fraction and interior valence while arriving in 69 pieces. So the connectivity columns come first
// here too, even though v1 passes them comfortably - the point is that the report cannot be read
// without them.

using System;
using System.Collections.Generic;
using System.Text;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class DefectBreakdown
    {
        struct Job
        {
            public string asset;
            public int target;
            public Func<Mesh> load;
            public RemeshProfileKind profile;
            public int preDecimateTo;
        }

        // The competitors' own face counts on these assets, so the comparison is like-for-like.
        //   character  5,474   the retopology reference
        //   character 12,451   QuadRemesher's run
        //   ccnb       9,451   QuadRemesher's run
        static Job[] Jobs() => new[]
        {
            new Job
            {
                asset = "character", target = 5474, profile = RemeshProfileKind.Character,
                load = () => PerfProbe.LoadReferenceHigh(out _), preDecimateTo = 54_740,
            },
            new Job
            {
                asset = "character", target = 12451, profile = RemeshProfileKind.Character,
                load = () => PerfProbe.LoadReferenceHigh(out _), preDecimateTo = 124_510,
            },
            new Job
            {
                asset = "ccnb", target = 9451, profile = RemeshProfileKind.Photogrammetry,
                load = LoadScan, preDecimateTo = 94_510,
            },
        };

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== DefectBreakdown (where v1's irregular vertices come from) ===");
            report.AppendLine("QuadRemesher's own numbers, for the target:");
            report.AppendLine("  character @12,451: 341 irregular (2.7 %), 99.97 % quads, 4 tris");
            report.AppendLine("  ccnb      @ 9,451: 25.7 % irregular, 95.3 % quads");
            report.AppendLine();

            foreach (var job in Jobs())
                Measure(report, job);

            report.AppendLine("=== end DefectBreakdown ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void Measure(StringBuilder report, Job job)
        {
            Mesh mesh = null;
            try
            {
                mesh = job.load();
                if (mesh == null)
                {
                    report.AppendLine($"  {job.asset}@{job.target}: input missing - skipped");
                    return;
                }

                RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);

                if (job.preDecimateTo > 0 && triangles.Length / 3 > job.preDecimateTo)
                    PreDecimate.Decimate(positions, triangles,
                        DecimateOptions.ForTriangleCount(job.preDecimateTo),
                        out _, out positions, out triangles);

                var settings = RemeshSettings.Default;
                settings.targetQuadCount = job.target;
                var profile = RemeshProfile.For(job.profile);

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
                var ms = watch.ElapsedMilliseconds;

                var extraction = result.extraction;

                report.AppendLine($"  --- {job.asset} @ {job.target} " +
                                  $"({triangles.Length / 3:N0} tris in, {ms} ms) ---");
                report.AppendLine($"      {extraction.faceCount:N0} faces, " +
                                  $"{extraction.quadFraction:P1} quads, " +
                                  $"{extraction.triangleCount:N0} tris, " +
                                  $"{extraction.ngonCount:N0} n-gons");

                // The regularizer's own account of why triangles survived. 70 % of the irregular
                // vertices touch a triangle, so this is the load-bearing stage and "644 remaining"
                // has to be split into "had no partner" versus "was refused".
                report.AppendLine($"      regularizer: {extraction.regularizeStats}");

                Attribute(report, extraction.faces);
                TriangleNeighbourhoods(report, extraction.faces);
                TriangleSeparation(report, extraction.faces);
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {job.asset}@{job.target} threw: {ex}");
            }
            finally
            {
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// Splits the irregular vertices by cause, and reports what is reachable versus irreducible.
        /// </summary>
        /// <remarks>
        /// The key column is <c>touchingNonQuad</c>: an irregular vertex next to a triangle or an
        /// n-gon is irregular BECAUSE of that face, so merging the non-quads away removes it. One
        /// next to nothing but quads is a genuine singularity or a tracing artefact and needs a
        /// different fix. Reporting the total without this split is what makes "2,149" unactionable.
        /// </remarks>
        static void Attribute(StringBuilder report, List<int[]> faces)
        {
            var valence = new Dictionary<int, int>(faces.Count * 2);
            var edgeUse = new Dictionary<long, int>(faces.Count * 4);
            var touchesNonQuad = new HashSet<int>();
            var touchesTriangle = new HashSet<int>();

            foreach (var face in faces)
            {
                foreach (var c in face)
                {
                    valence.TryGetValue(c, out var d);
                    valence[c] = d + 1;

                    if (face.Length == 4) continue;
                    touchesNonQuad.Add(c);
                    if (face.Length == 3) touchesTriangle.Add(c);
                }

                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    edgeUse.TryGetValue(key, out var used);
                    edgeUse[key] = used + 1;
                }
            }

            var onBoundary = new HashSet<int>();
            var boundaryEdges = 0;
            var nonManifoldEdges = 0;
            foreach (var entry in edgeUse)
            {
                if (entry.Value == 1)
                {
                    boundaryEdges++;
                    onBoundary.Add((int)(entry.Key >> 32));
                    onBoundary.Add((int)(entry.Key & 0xFFFFFFFFL));
                }
                else if (entry.Value > 2)
                {
                    nonManifoldEdges++;
                }
            }

            ConnectedComponents(faces, out var components, out var largestFraction);

            // --- the split ---
            var histogram = new Dictionary<int, int>();
            int irregular = 0, irregularOnBoundary = 0, irregularTouchingNonQuad = 0,
                irregularTouchingTriangle = 0, irregularInteriorAllQuad = 0;

            foreach (var entry in valence)
            {
                var v = entry.Key;
                var d = entry.Value;

                histogram.TryGetValue(d, out var n);
                histogram[d] = n + 1;

                if (d == 4) continue;
                irregular++;

                if (onBoundary.Contains(v)) { irregularOnBoundary++; continue; }

                if (touchesNonQuad.Contains(v))
                {
                    irregularTouchingNonQuad++;
                    if (touchesTriangle.Contains(v)) irregularTouchingTriangle++;
                    continue;
                }

                irregularInteriorAllQuad++;
            }

            report.AppendLine($"      connectivity: {components:N0} components, " +
                              $"{largestFraction:P1} in the largest, " +
                              $"{boundaryEdges:N0} boundary edges, " +
                              $"{nonManifoldEdges:N0} non-manifold edges");

            report.AppendLine($"      irregular: {irregular:N0} of {valence.Count:N0} vertices " +
                              $"({irregular / (float)Mathf.Max(1, valence.Count):P1})");
            report.AppendLine($"        on a boundary        {irregularOnBoundary,6:N0}   " +
                              $"(expected: a rim is irregular)");
            report.AppendLine($"        touching a non-quad  {irregularTouchingNonQuad,6:N0}   " +
                              $"(of which {irregularTouchingTriangle:N0} touch a TRIANGLE) " +
                              $"<- removable by merging non-quads away");
            report.AppendLine($"        interior, all quads  {irregularInteriorAllQuad,6:N0}   " +
                              $"<- genuine layout defect or singularity");

            var line = new StringBuilder("      valence:");
            for (var d = 1; d <= 9; d++)
            {
                histogram.TryGetValue(d, out var n);
                if (n == 0) continue;
                line.Append($"  {d}:{n:N0}");
            }

            var beyond = 0;
            foreach (var entry in histogram)
                if (entry.Key > 9) beyond += entry.Value;
            if (beyond > 0) line.Append($"  10+:{beyond:N0}");

            report.AppendLine(line.ToString());
        }

        /// <summary>
        /// How the surviving triangles sit relative to each other.
        /// </summary>
        /// <remarks>
        /// Decides which fix is even applicable. A triangle with a triangle neighbour can still be
        /// merged into a quad, so if most survivors have one, the merger's threshold or its pairing
        /// is at fault and tuning it is the fix. A triangle surrounded entirely by quads cannot be
        /// paired at all, and no amount of threshold tuning reaches it - it needs a different
        /// operation, so knowing the split decides whether the next change is a parameter or a pass.
        /// </remarks>
        static void TriangleNeighbourhoods(StringBuilder report, List<int[]> faces)
        {
            var edgeToFaces = new Dictionary<long, List<int>>(faces.Count * 4);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

                    if (!edgeToFaces.TryGetValue(key, out var list))
                    {
                        list = new List<int>(2);
                        edgeToFaces[key] = list;
                    }

                    list.Add(f);
                }
            }

            int isolated = 0, withOnePartner = 0, withTwo = 0, withThree = 0, onBoundary = 0;
            var triangles = 0;

            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face.Length != 3) continue;
                triangles++;

                var partners = 0;
                var hasBoundaryEdge = false;

                for (var k = 0; k < 3; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % 3];
                    var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    var users = edgeToFaces[key];

                    if (users.Count == 1) { hasBoundaryEdge = true; continue; }

                    foreach (var g in users)
                    {
                        if (g == f) continue;
                        if (faces[g].Length == 3) partners++;
                    }
                }

                if (hasBoundaryEdge) onBoundary++;

                if (partners == 0) isolated++;
                else if (partners == 1) withOnePartner++;
                else if (partners == 2) withTwo++;
                else withThree++;
            }

            report.AppendLine($"      {triangles:N0} surviving triangles by neighbourhood:");
            report.AppendLine($"        isolated (all-quad neighbours) {isolated,6:N0}   " +
                              $"<- unreachable by pair merging, needs a different operation");
            report.AppendLine($"        1 triangle neighbour           {withOnePartner,6:N0}   " +
                              $"<- mergeable, so refused or missed");
            report.AppendLine($"        2 triangle neighbours          {withTwo,6:N0}");
            report.AppendLine($"        3 triangle neighbours          {withThree,6:N0}");
            report.AppendLine($"        touching a mesh boundary       {onBoundary,6:N0}");
        }

        /// <summary>
        /// How far each triangle is from the nearest other triangle, in faces crossed.
        /// </summary>
        /// <remarks>
        /// THE NUMBER THAT DECIDES THE NEXT PASS, because of a parity constraint that cannot be
        /// argued with. A triangle plus its three quad neighbours is a region with 3 + 3*2 = 9
        /// boundary edges, and a region with an ODD boundary edge count admits no all-quad
        /// tiling - so a lone triangle cannot be removed by any local remesh, however clever.
        /// Triangles have to be removed in PAIRS.
        ///
        /// Distance therefore sets the cost of each removal:
        ///   1  the two triangles are adjacent - the existing pair merger already handles it
        ///   2  T + one quad + T is a hexagon, 6 boundary edges, and a hexagon splits into 2 quads
        ///      with a single internal diagonal. A clean, purely local operation.
        ///   3+ needs the quad strip between them rotated first, which is a much bigger pass
        ///
        /// So if distance 2 dominates, one small local pass removes most of the triangles and with
        /// them roughly 70 % of v1's irregular vertices. If distance 3+ dominates, that pass is not
        /// worth writing and the effort belongs upstream.
        /// </remarks>
        static void TriangleSeparation(StringBuilder report, List<int[]> faces)
        {
            var adjacency = FaceAdjacency(faces);

            var triangleIds = new List<int>();
            for (var f = 0; f < faces.Count; f++)
                if (faces[f].Length == 3) triangleIds.Add(f);

            if (triangleIds.Count == 0) return;

            var isTriangle = new bool[faces.Count];
            foreach (var f in triangleIds) isTriangle[f] = true;

            // Multi-source BFS from every triangle at once gives each face its distance to the
            // NEAREST triangle in one pass. Running a separate BFS per triangle would be
            // quadratic, and on 1,256 triangles over 13,201 faces that is the difference between
            // a diagnostic and a wait.
            var distance = new int[faces.Count];
            for (var f = 0; f < faces.Count; f++) distance[f] = int.MaxValue;

            // Each triangle's own distance to ANOTHER triangle is what is wanted, so a triangle
            // does not seed itself with 0 - it seeds its neighbours with 1 and reads its own answer
            // from whichever wave reaches it.
            var histogram = new Dictionary<int, int>();

            foreach (var source in triangleIds)
            {
                // Bounded BFS outward from this triangle, stopping at the first other triangle.
                // Capped at 6 because anything beyond is in the "needs a big pass" bucket anyway
                // and the cap is what keeps this linear enough to run.
                const int cap = 6;
                var visited = new HashSet<int> { source };
                var frontier = new List<int> { source };
                var found = 0;

                for (var step = 1; step <= cap && found == 0; step++)
                {
                    var next = new List<int>();

                    foreach (var f in frontier)
                    {
                        var from = adjacency.start[f];
                        var to = adjacency.start[f + 1];
                        for (var k = from; k < to; k++)
                        {
                            var g = adjacency.neighbours[k];
                            if (!visited.Add(g)) continue;

                            if (isTriangle[g]) { found = step; break; }
                            next.Add(g);
                        }

                        if (found != 0) break;
                    }

                    frontier = next;
                    if (frontier.Count == 0) break;
                }

                var bucket = found == 0 ? cap + 1 : found;
                histogram.TryGetValue(bucket, out var n);
                histogram[bucket] = n + 1;
            }

            report.AppendLine($"      triangle-to-nearest-triangle distance (faces crossed):");
            for (var d = 1; d <= 7; d++)
            {
                histogram.TryGetValue(d, out var n);
                if (n == 0) continue;

                var note = d == 1 ? "  <- adjacent, existing merger's job"
                    : d == 2 ? "  <- HEXAGON PATCH, 2 quads, purely local"
                    : d == 7 ? "  <- beyond 6, needs a global pairing pass"
                    : "";
                report.AppendLine($"        {(d == 7 ? "7+" : d.ToString()),3}  {n,6:N0}{note}");
            }
        }

        struct Adjacency
        {
            public int[] start;
            public int[] neighbours;
        }

        /// <summary>Face-to-face adjacency through shared undirected edges, as CSR.</summary>
        static Adjacency FaceAdjacency(List<int[]> faces)
        {
            var edgeToFaces = new Dictionary<long, List<int>>(faces.Count * 4);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

                    if (!edgeToFaces.TryGetValue(key, out var list))
                    {
                        list = new List<int>(2);
                        edgeToFaces[key] = list;
                    }

                    list.Add(f);
                }
            }

            var counts = new int[faces.Count + 1];
            foreach (var users in edgeToFaces.Values)
            {
                if (users.Count < 2) continue;
                foreach (var f in users) counts[f + 1] += users.Count - 1;
            }

            for (var f = 0; f < faces.Count; f++) counts[f + 1] += counts[f];

            var cursor = new int[faces.Count];
            Array.Copy(counts, cursor, faces.Count);
            var neighbours = new int[counts[faces.Count]];

            foreach (var users in edgeToFaces.Values)
            {
                if (users.Count < 2) continue;
                foreach (var f in users)
                foreach (var g in users)
                {
                    if (f == g) continue;
                    neighbours[cursor[f]++] = g;
                }
            }

            return new Adjacency { start = counts, neighbours = neighbours };
        }

        static void ConnectedComponents(List<int[]> faces, out int components,
            out float largestFraction)
        {
            components = 0;
            largestFraction = 0f;
            if (faces.Count == 0) return;

            var edgeToFaces = new Dictionary<long, List<int>>(faces.Count * 4);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

                    if (!edgeToFaces.TryGetValue(key, out var list))
                    {
                        list = new List<int>(2);
                        edgeToFaces[key] = list;
                    }

                    list.Add(f);
                }
            }

            var component = new int[faces.Count];
            for (var f = 0; f < faces.Count; f++) component[f] = -1;

            var queue = new Queue<int>();
            var largest = 0;

            for (var seed = 0; seed < faces.Count; seed++)
            {
                if (component[seed] >= 0) continue;

                var id = components++;
                component[seed] = id;
                queue.Clear();
                queue.Enqueue(seed);
                var size = 0;

                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    size++;

                    var face = faces[f];
                    for (var k = 0; k < face.Length; k++)
                    {
                        var a = face[k];
                        var b = face[(k + 1) % face.Length];
                        var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

                        foreach (var neighbour in edgeToFaces[key])
                        {
                            if (component[neighbour] >= 0) continue;
                            component[neighbour] = id;
                            queue.Enqueue(neighbour);
                        }
                    }
                }

                if (size > largest) largest = size;
            }

            largestFraction = largest / (float)faces.Count;
        }

        static Mesh LoadScan()
        {
            const string path = "Benchmarks/reference/scan_open.obj";
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"[DefectBreakdown] missing {path}");
                return null;
            }

            return ObjReader.Read(path);
        }
    }
}
