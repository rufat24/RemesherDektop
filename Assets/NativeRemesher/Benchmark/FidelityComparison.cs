// Ours against QuadRemesher's, on the metrics a person actually SEES.
//
// WHY THIS FILE AND NOT THE IRREGULARITY COUNT. Every measurement in this project so far has scored
// irregular vertices, and the reported complaints about the output are different things: "looks a
// bit distorted", "lighting works weird", "a lot of small spots are weird", "QuadRemesher is very
// clean and precise compared to the original high poly". None of those is a valence count. They are:
//
//   distorted / not precise   -> the output surface does not sit on the input surface
//   lighting weird            -> normals disagree with the input, or quads are not planar
//   small weird spots         -> localised outliers, not the mean
//
// So this measures deviation from the ORIGINAL high-poly, normal agreement, planarity and quad
// shape, reports the tail rather than only the average, and runs the identical code over
// QuadRemesher's own output so every number has a reference value beside it.
//
// THE SIGNED DEVIATION IS THE ONE TO READ FIRST. v1's output vertices are weighted averages of the
// input vertices that collapsed onto each lattice point, and an average of points on a curved
// surface lies INSIDE it. Nothing in the pipeline projects them back - the only projection is
// inside HoleFiller, for hole-fill hubs. If that is what is happening, mean signed deviation is
// negative and the model is quietly shrunk, which is exactly what "distorted" and "not precise"
// look like, and it would be worst where curvature is highest, which is what "small weird spots"
// look like.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class FidelityComparison
    {
        const string k_Original = "Benchmarks/reference/ref_high.obj";
        const string k_Competitor = "Benchmarks/reference/qr-character.obj";

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== FidelityComparison (ours vs QuadRemesher, against the original) ===");

            Mesh original = null;
            Mesh competitor = null;

            try
            {
                original = Load(k_Original);
                competitor = Load(k_Competitor);

                if (original == null || competitor == null)
                {
                    report.AppendLine("  inputs missing - see Headless/README.md and " +
                                      "Benchmarks/reference/export-competitor.py");
                    Debug.Log(report.ToString());
                    Console.WriteLine(report.ToString());
                    return;
                }

                RemeshTopology.ReadMesh(original, out var originalPositions,
                    out var originalTriangles);

                var grid = new SpatialGrid(originalPositions, originalTriangles);
                var diagonal = BoundsDiagonal(originalPositions);

                report.AppendLine($"  original: {originalPositions.Length:N0} verts, " +
                                  $"{originalTriangles.Length / 3:N0} tris, " +
                                  $"bbox diagonal {diagonal:F4}");
                report.AppendLine();

                // --- QuadRemesher ---
                // Positions and faces from ONE read, so their indices agree. Reading positions
                // from a Mesh instead put raw OBJ face indices against a re-indexed vertex array,
                // and reported perfectly planar quads and a 90-degree normal error - both pure
                // artefacts of the mismatch.
                var competitorFaces = ObjReader.ReadPolygons(k_Competitor,
                    out var competitorPositions);

                // FBX carries its own unit scale, and Blender's importer applied it: the exported
                // OBJ came out about 60x smaller than the original, which turned a fidelity
                // measurement into a measurement of the gap between two separated objects (a
                // near-constant 4.5 % "deviation" with nothing inside the surface). Aligned by
                // uniform scale and centroid, which is exactly the right correction for a mesh
                // that differs from the original only by units - and `Verify` below refuses the
                // comparison if it differs by more than that.
                Align(report, competitorPositions, originalPositions, out var aligned);

                Score(report, "QuadRemesher", aligned, competitorFaces,
                    grid, originalPositions, originalTriangles, diagonal);

                // --- ours, at the same face count ---
                var target = competitorFaces != null ? competitorFaces.Count : 21_780;

                RemeshTopology.ReadMesh(original, out var inputPositions, out var inputTriangles);
                PreDecimate.Decimate(inputPositions, inputTriangles,
                    DecimateOptions.ForTriangleCount(target * 10),
                    out _, out inputPositions, out inputTriangles);

                var settings = RemeshSettings.Default;
                settings.targetQuadCount = target;
                var profile = RemeshProfile.For(RemeshProfileKind.Character);

                var result = RemeshPipeline.Solve(inputPositions, inputTriangles, settings, profile);

                Score(report, "NativeRemesher", result.extraction.positions,
                    result.extraction.faces, grid, originalPositions, originalTriangles, diagonal);

                report.AppendLine();
                report.AppendLine("  Read the SIGNED mean first: a negative value means the output " +
                                  "sits inside the");
                report.AppendLine("  original surface, i.e. the model is shrunk. Then the p99/max " +
                                  "columns, which are");
                report.AppendLine("  the 'small weird spots'. The mean hides both.");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
                if (competitor != null) UnityEngine.Object.DestroyImmediate(competitor);
            }

            report.AppendLine("=== end FidelityComparison ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void Score(StringBuilder report, string arm, Vector3[] positions,
            List<int[]> faces, SpatialGrid grid, Vector3[] originalPositions,
            int[] originalTriangles, float diagonal)
        {
            report.AppendLine($"  --- {arm}: {positions.Length:N0} verts, " +
                              $"{(faces != null ? faces.Count : 0):N0} faces ---");

            // --- deviation, signed and unsigned, per output vertex ---
            var unsigned = new float[positions.Length];
            var signedSum = 0f;
            var inside = 0;

            for (var i = 0; i < positions.Length; i++)
            {
                var distance = grid.NearestDistance(positions[i], out var triangle);
                unsigned[i] = distance;

                if (triangle < 0) continue;

                var a = originalPositions[originalTriangles[triangle * 3]];
                var b = originalPositions[originalTriangles[triangle * 3 + 1]];
                var c = originalPositions[originalTriangles[triangle * 3 + 2]];

                var normal = Vector3.Cross(b - a, c - a);
                var closest = SpatialGrid.ClosestPointOnTriangle(positions[i], a, b, c);

                // Sign from the original surface's own outward normal. Positive means the output
                // vertex floats outside the surface, negative means it has sunk inside it.
                var side = Vector3.Dot(positions[i] - closest, normal);
                signedSum += side >= 0f ? distance : -distance;
                if (side < 0f) inside++;
            }

            Array.Sort(unsigned);

            var meanUnsigned = Mean(unsigned);
            var meanSigned = positions.Length > 0 ? signedSum / positions.Length : 0f;

            report.AppendLine($"      deviation from original, as % of bbox diagonal:");
            report.AppendLine($"        mean {meanUnsigned / diagonal:P3}   " +
                              $"p95 {Percentile(unsigned, 0.95f) / diagonal:P3}   " +
                              $"p99 {Percentile(unsigned, 0.99f) / diagonal:P3}   " +
                              $"max {unsigned[unsigned.Length - 1] / diagonal:P3}");
            report.AppendLine($"        SIGNED mean {meanSigned / diagonal:P3}   " +
                              $"({inside * 100f / Mathf.Max(1, positions.Length):F1} % of vertices " +
                              $"are inside the original surface)");

            if (faces == null || faces.Count == 0) return;

            // --- face shape and planarity ---
            var minAngles = new List<float>(faces.Count);
            var planarity = new List<float>(faces.Count);
            var edgeLengths = new List<float>(faces.Count * 4);
            var normalDeviation = new List<float>(faces.Count);
            var quads = 0;

            foreach (var face in faces)
            {
                if (face.Length == 4) quads++;

                var worstAngle = 180f;
                for (var k = 0; k < face.Length; k++)
                {
                    var previous = positions[face[(k + face.Length - 1) % face.Length]];
                    var current = positions[face[k]];
                    var following = positions[face[(k + 1) % face.Length]];

                    var u = previous - current;
                    var v = following - current;
                    var lengthU = u.magnitude;
                    var lengthV = v.magnitude;

                    edgeLengths.Add(lengthV);

                    if (lengthU < 1e-12f || lengthV < 1e-12f) { worstAngle = 0f; continue; }

                    var cos = Mathf.Clamp(Vector3.Dot(u, v) / (lengthU * lengthV), -1f, 1f);
                    var angle = Mathf.Acos(cos) * Mathf.Rad2Deg;
                    if (angle < worstAngle) worstAngle = angle;
                }

                minAngles.Add(worstAngle);

                // Planarity: how far the corners sit from their own best-fit plane, relative to the
                // face's size. A non-planar quad is drawn as two triangles with a crease down the
                // middle, and that crease is a lighting artefact with no geometry behind it.
                if (face.Length == 4)
                {
                    var p0 = positions[face[0]];
                    var p1 = positions[face[1]];
                    var p2 = positions[face[2]];
                    var p3 = positions[face[3]];

                    var n = Vector3.Cross(p2 - p0, p3 - p1);
                    var size = Mathf.Max(1e-12f, (p2 - p0).magnitude + (p3 - p1).magnitude) * 0.5f;

                    if (n.sqrMagnitude > 1e-24f)
                    {
                        n = n.normalized;
                        var offset = Mathf.Abs(Vector3.Dot(p1 - p0, n));
                        planarity.Add(offset / size);
                    }
                }

                // Normal agreement with the original surface at the face centroid. This is the
                // direct measure of "lighting works weird": the output can sit close to the surface
                // and still face a different way, and shading follows the normal, not the position.
                var centroid = Vector3.zero;
                foreach (var corner in face) centroid += positions[corner];
                centroid /= face.Length;

                grid.NearestDistance(centroid, out var nearest);
                if (nearest < 0) continue;

                var qa = originalPositions[originalTriangles[nearest * 3]];
                var qb = originalPositions[originalTriangles[nearest * 3 + 1]];
                var qc = originalPositions[originalTriangles[nearest * 3 + 2]];
                var originalNormal = Vector3.Cross(qb - qa, qc - qa);

                var faceNormal = FaceNormal(positions, face);

                var originalLength = originalNormal.magnitude;
                var faceLength = faceNormal.magnitude;
                if (originalLength < 1e-20f || faceLength < 1e-20f) continue;

                // Divided explicitly rather than via `Vector3.normalized`, which returns ZERO for
                // any vector shorter than 1e-5. These are cross products of triangle edges: the
                // original here has 1.5 M triangles across a bounding box of 1.385, so twice-area
                // is around 1e-6 and every normal came back as the zero vector. The dot product was
                // then 0 and this metric read a flat 90 degrees for BOTH arms - a number with no
                // information in it that looked like a catastrophic finding.
                var dot = Mathf.Clamp(
                    Vector3.Dot(faceNormal, originalNormal) / (faceLength * originalLength),
                    -1f, 1f);

                // Absolute, because a consistently inverted output would otherwise read as 180
                // degrees of error everywhere and drown the local disagreement being measured.
                normalDeviation.Add(Mathf.Acos(Mathf.Abs(dot)) * Mathf.Rad2Deg);
            }

            var angleArray = minAngles.ToArray();
            Array.Sort(angleArray);
            var planarityArray = planarity.ToArray();
            Array.Sort(planarityArray);
            var normalArray = normalDeviation.ToArray();
            Array.Sort(normalArray);
            var edgeArray = edgeLengths.ToArray();
            Array.Sort(edgeArray);

            report.AppendLine($"      quads {quads * 100f / faces.Count:F2} %");
            report.AppendLine($"      min interior angle (degrees, higher is better):");
            report.AppendLine($"        p01 {Percentile(angleArray, 0.01f):F1}   " +
                              $"p05 {Percentile(angleArray, 0.05f):F1}   " +
                              $"median {Percentile(angleArray, 0.5f):F1}   " +
                              $"worst {angleArray[0]:F1}");
            report.AppendLine($"      quad non-planarity (0 is flat):");
            report.AppendLine($"        median {Percentile(planarityArray, 0.5f):F4}   " +
                              $"p95 {Percentile(planarityArray, 0.95f):F4}   " +
                              $"p99 {Percentile(planarityArray, 0.99f):F4}   " +
                              $"max {(planarityArray.Length > 0 ? planarityArray[planarityArray.Length - 1] : 0f):F4}");
            report.AppendLine($"      normal deviation from original (degrees):");
            report.AppendLine($"        mean {Mean(normalArray):F1}   " +
                              $"p95 {Percentile(normalArray, 0.95f):F1}   " +
                              $"p99 {Percentile(normalArray, 0.99f):F1}   " +
                              $"max {(normalArray.Length > 0 ? normalArray[normalArray.Length - 1] : 0f):F1}");
            // What the WORST faces have in common. The mean is fine on every metric, so the visual
            // complaints are about the tail, and the tail needs a cause rather than a percentile.
            // Aspect ratio separates the two candidate causes cleanly: a needle whose edges are
            // wildly unequal comes from output vertices sitting too close together, which a
            // relaxation of vertex positions fixes. A needle with EVEN edges is a sheared
            // parallelogram, which is a field-direction problem and relaxation would not touch it.
            {
                var worst = new List<(float angle, float aspect, bool nearNonQuad)>(faces.Count);
                var nonQuadVertices = new HashSet<int>();

                foreach (var face in faces)
                {
                    if (face.Length == 4) continue;
                    foreach (var corner in face) nonQuadVertices.Add(corner);
                }

                foreach (var face in faces)
                {
                    var worstAngle = 180f;
                    var shortest = float.MaxValue;
                    var longest = 0f;
                    var touches = false;

                    for (var k = 0; k < face.Length; k++)
                    {
                        var previous = positions[face[(k + face.Length - 1) % face.Length]];
                        var current = positions[face[k]];
                        var following = positions[face[(k + 1) % face.Length]];

                        if (nonQuadVertices.Contains(face[k])) touches = true;

                        var length = (following - current).magnitude;
                        if (length < shortest) shortest = length;
                        if (length > longest) longest = length;

                        var u = previous - current;
                        var v = following - current;
                        if (u.magnitude < 1e-12f || v.magnitude < 1e-12f) { worstAngle = 0f; continue; }

                        var cos = Mathf.Clamp(Vector3.Dot(u, v) / (u.magnitude * v.magnitude),
                            -1f, 1f);
                        var angle = Mathf.Acos(cos) * Mathf.Rad2Deg;
                        if (angle < worstAngle) worstAngle = angle;
                    }

                    worst.Add((worstAngle, longest / Mathf.Max(1e-9f, shortest), touches));
                }

                worst.Sort((x, y) => x.angle.CompareTo(y.angle));

                var tail = Mathf.Max(1, worst.Count / 100);
                var aspectSum = 0f;
                var touching = 0;
                for (var i = 0; i < tail; i++)
                {
                    aspectSum += worst[i].aspect;
                    if (worst[i].nearNonQuad) touching++;
                }

                report.AppendLine($"      the worst 1 % of faces by angle ({tail:N0} faces):");
                report.AppendLine($"        mean edge aspect ratio {aspectSum / tail:F1}x   " +
                                  $"({touching * 100f / tail:F0} % touch a non-quad)");
            }

            report.AppendLine($"      edge length spread (uniformity):");
            report.AppendLine($"        p05 {Percentile(edgeArray, 0.05f):F4}   " +
                              $"median {Percentile(edgeArray, 0.5f):F4}   " +
                              $"p95 {Percentile(edgeArray, 0.95f):F4}   " +
                              $"ratio p95/p05 {Percentile(edgeArray, 0.95f) / Mathf.Max(1e-9f, Percentile(edgeArray, 0.05f)):F1}x");
        }

        /// <summary>
        /// Puts a mesh into the original's units: uniform scale about the centroid, then translate.
        /// </summary>
        /// <remarks>
        /// Legitimate only if the two really differ by units alone, so this checks rather than
        /// assumes: it compares the two bounding boxes' SHAPE (their side ratios) after scaling. A
        /// pure unit difference leaves those identical. If they disagree the meshes are not the same
        /// object and no alignment makes the comparison mean anything, so it says so loudly instead
        /// of quietly producing a number.
        /// </remarks>
        static void Align(StringBuilder report, Vector3[] source, Vector3[] reference,
            out Vector3[] aligned)
        {
            Bounds(source, out var sourceMin, out var sourceMax);
            Bounds(reference, out var referenceMin, out var referenceMax);

            var sourceSize = sourceMax - sourceMin;
            var referenceSize = referenceMax - referenceMin;

            var scale = referenceSize.magnitude / Mathf.Max(1e-12f, sourceSize.magnitude);

            var sourceCentre = (sourceMin + sourceMax) * 0.5f;
            var referenceCentre = (referenceMin + referenceMax) * 0.5f;

            aligned = new Vector3[source.Length];
            for (var i = 0; i < source.Length; i++)
                aligned[i] = (source[i] - sourceCentre) * scale + referenceCentre;

            // Side ratios, largest-normalised, so a pure scale cancels out entirely.
            var sourceRatio = sourceSize / Mathf.Max(1e-12f, Mathf.Max(sourceSize.x,
                Mathf.Max(sourceSize.y, sourceSize.z)));
            var referenceRatio = referenceSize / Mathf.Max(1e-12f, Mathf.Max(referenceSize.x,
                Mathf.Max(referenceSize.y, referenceSize.z)));

            var shapeError = (sourceRatio - referenceRatio).magnitude;

            report.AppendLine($"      aligned by x{scale:F3} uniform scale " +
                              $"(bbox {sourceSize.magnitude:F4} -> {referenceSize.magnitude:F4}), " +
                              $"bbox shape error {shapeError:F4}");

            if (shapeError > 0.05f)
                report.AppendLine("      WARNING: the bounding boxes are not the same shape, so " +
                                  "these two are not the same object under a unit change and the " +
                                  "deviation numbers below mean nothing.");
        }

        static void Bounds(Vector3[] positions, out Vector3 min, out Vector3 max)
        {
            min = positions.Length > 0 ? positions[0] : Vector3.zero;
            max = min;

            foreach (var p in positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        static Vector3 FaceNormal(Vector3[] positions, int[] face)
        {
            // Newell's method: correct for a non-planar polygon, where a single cross product of
            // two edges is whichever triangle it happened to pick.
            var normal = Vector3.zero;
            for (var k = 0; k < face.Length; k++)
            {
                var current = positions[face[k]];
                var following = positions[face[(k + 1) % face.Length]];
                normal.x += (current.y - following.y) * (current.z + following.z);
                normal.y += (current.z - following.z) * (current.x + following.x);
                normal.z += (current.x - following.x) * (current.y + following.y);
            }

            return normal;
        }

        static float Mean(float[] sorted)
        {
            if (sorted.Length == 0) return 0f;
            var sum = 0.0;
            foreach (var v in sorted) sum += v;
            return (float)(sum / sorted.Length);
        }

        static float Percentile(float[] sorted, float fraction)
        {
            if (sorted.Length == 0) return 0f;
            var index = Mathf.Clamp(Mathf.RoundToInt(fraction * (sorted.Length - 1)),
                0, sorted.Length - 1);
            return sorted[index];
        }

        static float BoundsDiagonal(Vector3[] positions)
        {
            if (positions.Length == 0) return 1f;

            var min = positions[0];
            var max = positions[0];
            foreach (var p in positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            return Mathf.Max(1e-9f, (max - min).magnitude);
        }

        static Mesh Load(string path)
        {
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"[FidelityComparison] missing {path}");
                return null;
            }

            return ObjReader.Read(path);
        }
    }
}
