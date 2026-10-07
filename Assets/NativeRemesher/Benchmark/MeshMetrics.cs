// Everything the benchmark scores, in one place.
//
// Design rule, learned the hard way on this project: a metric that cannot distinguish a correct
// result from a broken one is worse than no metric, because it gets quoted. Three of the defects
// in HANDOVER.md were misdiagnosed from statistics that were measuring gauge rather than signal.
// So every field here says what it means, and anything whose interpretation is subtle says why.
//
// Distances are normalised by the *input* bounding-box diagonal, so a number is comparable across
// meshes of different physical size. Unnormalised distances are useless in a table.

using System;
using System.Collections.Generic;
using UnityEngine;
using NativeRemesher.Extraction;

namespace NativeRemesher.Benchmark
{
    public sealed class MeshMetrics
    {
        // --- identity -------------------------------------------------------
        public string meshName = "";
        public int targetQuadCount;
        public string profile = "";
        public string variant = "";       // "native" | "oracle" | "input"

        // --- size -----------------------------------------------------------
        public int inputVertices;
        public int inputTriangles;
        public int outputVertices;
        public int outputFaces;

        // --- topology -------------------------------------------------------
        public int quadCount;
        public int triangleCount;
        public int ngonCount;
        public float quadFraction;
        /// <summary>Face-size histogram, index 0 = 3 corners .. index 5 = 8+ corners.</summary>
        public int[] faceSizeHistogram = new int[6];

        /// <summary>Interior-vertex valence histogram, index 0 = valence 1 .. index 6 = 7+.</summary>
        public int[] valenceHistogram = new int[7];
        public int boundaryVertices;
        /// <summary>Sum of |valence - 4| over interior vertices. Zero is a perfect quad grid.</summary>
        public int valenceDefect;
        /// <summary>Fraction of interior vertices whose valence is not 4.</summary>
        public float irregularFraction;

        // --- validity -------------------------------------------------------
        public int boundaryEdges;        // incidence 1
        public int nonManifoldEdges;     // incidence > 2
        public int isolatedVertices;
        public int duplicateFaces;
        public int degenerateFaces;      // repeated corner index, or zero area
        public int eulerCharacteristic;  // V - E + F
        /// <summary>
        /// Connected components over the output's edge graph, ignoring isolated vertices. Needed to
        /// read <see cref="eulerCharacteristic"/> at all: chi = 2C - 2g, so chi = 8 is four spheres,
        /// not one impossible surface.
        /// </summary>
        public int componentCount;
        public bool windingConsistent;
        public bool isWatertight;        // no boundary and no non-manifold edges

        /// <summary>Max corner deviation from the face's best-fit plane, over mean edge length.</summary>
        public float planarityMean;
        public float planarityP95;

        // --- geometry (normalised by input bbox diagonal) -------------------
        public float chamferMean;        // symmetric mean surface distance
        public float hausdorffMax;       // symmetric max surface distance
        public float chamferP95;
        public float normalConsistency;  // mean |dot| of input vs nearest output face normal
        public float normalFlippedFraction;

        // --- intent ---------------------------------------------------------
        public float faceCountRatio;     // outputFaces / targetQuadCount
        public int maxQuadCountForInput; // the coarsen-only ceiling
        public bool targetWasClamped;

        // --- field diagnostics (filled by FieldDiagnostics, zero otherwise) --
        /// <summary>
        /// Ratio of the largest quad spacing asked for to the smallest, 1 when sizing is uniform.
        /// </summary>
        /// <remarks>
        /// The one number that says whether curvature grading did anything at all on this mesh,
        /// which a row cannot otherwise be read for: a graded run and a uniform one differ in
        /// every quality column and in none that names the cause.
        /// </remarks>
        public float spacingSpread;

        public float latticeMismatch;
        public float representativeDrift;
        public float sameCellFraction;
        public float oneStepFraction;
        public float discardedFraction;
        public float orientationEnergy;
        public int orientationIterations;
        public int positionIterations;
        public int singularityCount;
        /// <summary>Signed singularity index sum. Must equal 4 * Euler characteristic.</summary>
        public int indexSum;

        // --- cost -----------------------------------------------------------
        public long topologyMs, orientationMs, positionMs, extractionMs, totalMs;
        public long managedBytesDelta;

        // --- notes ----------------------------------------------------------
        public string notes = "";

        /// <summary>
        /// Scores <paramref name="output"/> against <paramref name="input"/>. Geometric metrics are
        /// skipped (left at zero) when <paramref name="geometrySamples"/> is zero, which is how the
        /// fast unit tests reuse this without paying for surface sampling.
        /// </summary>
        public static MeshMetrics Measure(PolyMesh output, PolyMesh input, int geometrySamples = 60000,
            int seed = 12345)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));

            var m = new MeshMetrics
            {
                outputVertices = output.vertexCount,
                outputFaces = output.faceCount
            };

            MeasureTopology(m, output);
            MeasureValidity(m, output);
            MeasurePlanarity(m, output);

            if (input != null)
            {
                m.inputVertices = input.vertexCount;
                m.inputTriangles = input.Triangulate().Length / 3;

                if (geometrySamples > 0)
                    MeasureGeometry(m, output, input, geometrySamples, seed);
            }

            return m;
        }

        // ------------------------------------------------------------------
        static void MeasureTopology(MeshMetrics m, PolyMesh output)
        {
            foreach (var face in output.faces)
            {
                var n = face.Length;
                switch (n)
                {
                    case 3: m.triangleCount++; break;
                    case 4: m.quadCount++; break;
                    default: m.ngonCount++; break;
                }

                var bucket = Mathf.Clamp(n - 3, 0, m.faceSizeHistogram.Length - 1);
                m.faceSizeHistogram[bucket]++;
            }

            m.quadFraction = output.faceCount > 0 ? m.quadCount / (float)output.faceCount : 0f;
        }

        static void MeasureValidity(MeshMetrics m, PolyMesh output)
        {
            var vertexCount = output.vertexCount;

            // Directed-edge tally. An undirected edge's incidence is the sum of both directions;
            // consistent winding additionally requires each direction to appear exactly once.
            var directed = new Dictionary<long, int>(output.faceCount * 4, EdgeKey.Comparer);
            var valence = new int[vertexCount];
            var touched = new bool[vertexCount];

            var winding = true;

            foreach (var face in output.faces)
            {
                // Degenerate: a repeated corner index inside one face.
                var repeated = false;
                for (var i = 0; i < face.Length && !repeated; i++)
                for (var j = i + 1; j < face.Length; j++)
                    if (face[i] == face[j]) { repeated = true; break; }
                if (repeated) m.degenerateFaces++;

                for (var i = 0; i < face.Length; i++)
                {
                    var a = face[i];
                    var b = face[(i + 1) % face.Length];
                    if (a < 0 || b < 0 || a >= vertexCount || b >= vertexCount)
                        continue;

                    touched[a] = true;
                    var key = ((long)a << 32) | (uint)b;
                    directed.TryGetValue(key, out var seen);
                    if (seen > 0) winding = false;   // same direction twice
                    directed[key] = seen + 1;
                }
            }

            // Undirected incidence, and per-vertex valence from the undirected edge set.
            var undirected = new Dictionary<long, int>(directed.Count, EdgeKey.Comparer);
            foreach (var kv in directed)
            {
                var a = (int)(kv.Key >> 32);
                var b = (int)(kv.Key & 0xFFFFFFFF);
                var lo = Mathf.Min(a, b);
                var hi = Mathf.Max(a, b);
                var key = ((long)lo << 32) | (uint)hi;
                undirected.TryGetValue(key, out var seen);
                undirected[key] = seen + kv.Value;
            }

            var boundary = new bool[vertexCount];
            foreach (var kv in undirected)
            {
                var a = (int)(kv.Key >> 32);
                var b = (int)(kv.Key & 0xFFFFFFFF);
                valence[a]++;
                valence[b]++;

                if (kv.Value == 1) { m.boundaryEdges++; boundary[a] = true; boundary[b] = true; }
                else if (kv.Value > 2) { m.nonManifoldEdges++; }
            }

            // V counts only vertices some face actually references.
            //
            // This used `vertexCount`, which is positions.Length — every lattice point the
            // extractor produced, including ones no face ended up using. Each of those added one to
            // chi and nothing to E or F, so chi read high by exactly the isolated-vertex count and
            // the surface looked topologically impossible when it was fine. On the sphere test:
            // V=577, E=1145, F=576 gave chi=8, and 577 - 6 isolated = 571 gives chi=2.
            //
            // Isolated vertices are still a defect and still reported separately. They must not
            // corrupt the one number that is supposed to describe the surface's topology.
            var usedVertices = 0;
            for (var v = 0; v < vertexCount; v++)
                if (touched[v]) usedVertices++;

            m.eulerCharacteristic = usedVertices - undirected.Count + output.faceCount;
            m.windingConsistent = winding;
            m.isWatertight = m.boundaryEdges == 0 && m.nonManifoldEdges == 0;

            // Connected components, without which the Euler characteristic cannot be read.
            //
            // chi = 2C - 2g for a closed orientable surface, so a single number conflates "one
            // sphere" (chi 2), "four spheres" (chi 8) and "one genus-4 shell" (chi -6). Closing the
            // holes on a sphere here produced chi = 8, which looked like nonsense until it was read
            // as four separate closed shells — the output was FRAGMENTED, and the holes had been
            // hiding it. Without this column that diagnosis was not available.
            m.componentCount = CountComponents(undirected, touched, vertexCount);

            for (var v = 0; v < vertexCount; v++)
            {
                if (!touched[v]) { m.isolatedVertices++; continue; }
                if (boundary[v]) { m.boundaryVertices++; continue; }


                var bucket = Mathf.Clamp(valence[v] - 1, 0, m.valenceHistogram.Length - 1);
                m.valenceHistogram[bucket]++;
                m.valenceDefect += Mathf.Abs(valence[v] - 4);
            }

            var interior = 0;
            var irregular = 0;
            for (var i = 0; i < m.valenceHistogram.Length; i++)
            {
                interior += m.valenceHistogram[i];
                if (i != 3) irregular += m.valenceHistogram[i]; // index 3 == valence 4
            }
            m.irregularFraction = interior > 0 ? irregular / (float)interior : 0f;

            // Duplicate faces: same corner set, any rotation or reflection.
            var seenFaces = new HashSet<string>();
            foreach (var face in output.faces)
            {
                var sorted = (int[])face.Clone();
                Array.Sort(sorted);
                var key = string.Join(",", sorted);
                if (!seenFaces.Add(key)) m.duplicateFaces++;
            }
        }

        /// <summary>
        /// Connected components of the output's undirected edge graph, skipping vertices no face
        /// touches (those are counted as isolated, not as components of their own).
        /// </summary>
        static int CountComponents(Dictionary<long, int> undirected, bool[] touched, int vertexCount)
        {
            // Adjacency as CSR, built from the edge set that was already collected. Two passes to
            // size it, so nothing here allocates per vertex.
            var degree = new int[vertexCount + 1];
            foreach (var key in undirected.Keys)
            {
                var a = (int)(key >> 32);
                var b = unchecked((int)(key & 0xFFFFFFFFL));
                if (a < 0 || a >= vertexCount || b < 0 || b >= vertexCount) continue;
                degree[a]++;
                degree[b]++;
            }

            var start = new int[vertexCount + 1];
            var running = 0;
            for (var v = 0; v < vertexCount; v++) { start[v] = running; running += degree[v]; }
            start[vertexCount] = running;

            var neighbours = new int[running];
            var fill = new int[vertexCount];
            foreach (var key in undirected.Keys)
            {
                var a = (int)(key >> 32);
                var b = unchecked((int)(key & 0xFFFFFFFFL));
                if (a < 0 || a >= vertexCount || b < 0 || b >= vertexCount) continue;
                neighbours[start[a] + fill[a]++] = b;
                neighbours[start[b] + fill[b]++] = a;
            }

            var seen = new bool[vertexCount];
            var stack = new Stack<int>();
            var components = 0;

            for (var v = 0; v < vertexCount; v++)
            {
                if (seen[v] || !touched[v]) continue;

                components++;
                seen[v] = true;
                stack.Push(v);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    for (var k = start[current]; k < start[current + 1]; k++)
                    {
                        var next = neighbours[k];
                        if (seen[next]) continue;
                        seen[next] = true;
                        stack.Push(next);
                    }
                }
            }

            return components;
        }

        static void MeasurePlanarity(MeshMetrics m, PolyMesh output)
        {
            var values = new List<float>(output.faceCount);

            foreach (var face in output.faces)
            {
                if (face.Length < 4) continue; // triangles are planar by definition

                var centroid = Vector3.zero;
                for (var i = 0; i < face.Length; i++) centroid += output.positions[face[i]];
                centroid /= face.Length;

                // Newell's method: robust normal for a non-planar polygon.
                var normal = Vector3.zero;
                var perimeter = 0f;
                for (var i = 0; i < face.Length; i++)
                {
                    var p = output.positions[face[i]];
                    var q = output.positions[face[(i + 1) % face.Length]];
                    normal += new Vector3(
                        (p.y - q.y) * (p.z + q.z),
                        (p.z - q.z) * (p.x + q.x),
                        (p.x - q.x) * (p.y + q.y));
                    perimeter += Vector3.Distance(p, q);
                }

                var meanEdge = perimeter / face.Length;
                if (normal.sqrMagnitude <= 1e-20f || meanEdge <= 1e-20f) continue;
                normal = MeshMath.SafeNormalize(normal, Vector3.up);

                var maxDeviation = 0f;
                for (var i = 0; i < face.Length; i++)
                    maxDeviation = Mathf.Max(maxDeviation,
                        Mathf.Abs(Vector3.Dot(normal, output.positions[face[i]] - centroid)));

                values.Add(maxDeviation / meanEdge);
            }

            if (values.Count == 0) return;
            values.Sort();

            double sum = 0;
            foreach (var v in values) sum += v;
            m.planarityMean = (float)(sum / values.Count);
            m.planarityP95 = Percentile(values, 0.95f);
        }

        static void MeasureGeometry(MeshMetrics m, PolyMesh output, PolyMesh input,
            int samples, int seed)
        {
            var outputTris = output.Triangulate();
            var inputTris = input.Triangulate();

            if (outputTris.Length == 0 || inputTris.Length == 0)
            {
                m.notes = AppendNote(m.notes, "geometry skipped: empty triangulation");
                return;
            }

            // Normalisation scale: the input's bounding-box diagonal.
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var t in inputTris)
            {
                min = Vector3.Min(min, input.positions[t]);
                max = Vector3.Max(max, input.positions[t]);
            }
            var diagonal = (max - min).magnitude;
            if (diagonal <= 1e-20f) diagonal = 1f;
            var invDiagonal = 1f / diagonal;

            var outputGrid = new SpatialGrid(output.positions, outputTris);
            var inputGrid = new SpatialGrid(input.positions, inputTris);

            // --- input -> output, with normal agreement ---
            var inputNormals = new List<Vector3>(samples);
            var inputSamples = SpatialGrid.SampleSurface(input.positions, inputTris, samples, seed, inputNormals);

            var distances = new List<float>(samples * 2);
            double sumForward = 0;
            double normalSum = 0;
            var flipped = 0;
            var maxForward = 0f;

            for (var i = 0; i < inputSamples.Count; i++)
            {
                var d = outputGrid.NearestDistance(inputSamples[i], out var tri) * invDiagonal;
                if (float.IsInfinity(d)) continue;

                sumForward += d;
                maxForward = Mathf.Max(maxForward, d);
                distances.Add(d);

                if (tri >= 0)
                {
                    var a = output.positions[outputTris[tri * 3]];
                    var b = output.positions[outputTris[tri * 3 + 1]];
                    var c = output.positions[outputTris[tri * 3 + 2]];
                    var faceNormal = Vector3.Cross(b - a, c - a);
                    if (faceNormal.sqrMagnitude > 1e-20f)
                    {
                        // Both sides go through MeshMath: this cross product is twice a triangle area,
                        // and .normalized zeroes below 1e-5, which scored a correct dense mesh as
                        // 100% flipped. A metric that condemns a good mesh is worse than none.
                        var dot = Vector3.Dot(
                            MeshMath.SafeNormalize(faceNormal, inputNormals[i]), inputNormals[i]);
                        normalSum += Mathf.Abs(dot);
                        if (dot < 0.9f) flipped++;
                    }
                }
            }

            // --- output -> input, the other half of the symmetric distance ---
            // One-sided Chamfer is the classic way to score a remesher as better than it is: a
            // result that covers only part of the surface looks perfect measured output->input.
            var outputSamples = SpatialGrid.SampleSurface(output.positions, outputTris, samples, seed + 1);
            double sumBackward = 0;
            var maxBackward = 0f;

            foreach (var s in outputSamples)
            {
                var d = inputGrid.NearestDistance(s, out _) * invDiagonal;
                if (float.IsInfinity(d)) continue;
                sumBackward += d;
                maxBackward = Mathf.Max(maxBackward, d);
                distances.Add(d);
            }

            var forwardCount = Mathf.Max(1, inputSamples.Count);
            var backwardCount = Mathf.Max(1, outputSamples.Count);

            m.chamferMean = (float)(0.5 * (sumForward / forwardCount + sumBackward / backwardCount));
            m.hausdorffMax = Mathf.Max(maxForward, maxBackward);
            m.normalConsistency = (float)(normalSum / forwardCount);
            m.normalFlippedFraction = flipped / (float)forwardCount;

            distances.Sort();
            m.chamferP95 = Percentile(distances, 0.95f);
        }

        static float Percentile(List<float> sorted, float fraction)
        {
            if (sorted.Count == 0) return 0f;
            var index = Mathf.Clamp(Mathf.RoundToInt(fraction * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[index];
        }

        public static string AppendNote(string existing, string note)
        {
            if (string.IsNullOrEmpty(note)) return existing;
            return string.IsNullOrEmpty(existing) ? note : existing + "; " + note;
        }

        public override string ToString()
        {
            return $"{meshName} t={targetQuadCount} [{variant}] " +
                   $"faces={outputFaces} quads={quadFraction:P1} " +
                   $"chamfer={chamferMean:F5} hausdorff={hausdorffMax:F5} " +
                   $"nonManifold={nonManifoldEdges} boundary={boundaryEdges} " +
                   $"irregular={irregularFraction:P1} spread={spacingSpread:F2} {totalMs}ms";
        }
    }
}
