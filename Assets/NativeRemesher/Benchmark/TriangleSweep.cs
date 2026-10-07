// Why 437 triangles survive on the delivered asset, and what moves that number.
//
// THE MEASUREMENT THAT PROMPTED THIS. On the delivered 5,399-face output, against QuadRemesher's
// 4,954-face one at the same density (Benchmarks/reference/defects.py, triangles.py):
//
//   ours    13.47 % of faces flagged - 437 triangles, 283 in folds, 41 curled, 17 on open edges
//   theirs   4.36 % - 214 in folds and NOTHING else. No triangles, no open edges, no slivers.
//
// and of our 437 triangles, only 46 are adjacent to another triangle. 61 % are two or three faces
// away from their nearest partner and 99 % touch a vertex of valence != 4. So they are not merges
// anybody missed; they are the parity leftovers of the tracer, scattered one at a time around the
// singularities, and each one has to be walked to a partner through the quads between them. That is
// exactly what `TrianglePairing` does - so the question is why it stops with 437 still there.
//
// Each row re-runs the whole pipeline, because pairing changes the mesh the next round searches.

using System;
using System.Collections.Generic;
using System.Text;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class TriangleSweep
    {
        const string k_Original = "Benchmarks/reference/ref_high.obj";

        /// <summary>The delivered asset's own face count, so the rows describe what the user has.</summary>
        const int k_Target = 5_215;

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== TriangleSweep (character @ the delivered face count) ===");
            report.AppendLine("  QuadRemesher at this density: 4,954 faces, 0 triangles, 0 open edges,");
            report.AppendLine("  7 curled, 2 slivers, 214 faces in folds.");
            report.AppendLine();
            report.AppendLine("  reflex,faces,quad%,tris,ngons,minAngP01,>45%,curled%,ms");

            Mesh original = null;
            try
            {
                if (!System.IO.File.Exists(k_Original))
                {
                    report.AppendLine($"  missing {k_Original} - skipped");
                    Emit(report);
                    return;
                }

                original = ObjReader.Read(k_Original);
                RemeshTopology.ReadMesh(original, out var positions, out var triangles);

                // The chain, rounds and quality knobs were swept first and moved NOTHING: every
                // refusal in the pass, 501 of 501, was the convexity test. So the axis is how much
                // fold a re-tiling may have when no convex tiling exists at all.
                foreach (var reflex in new[] { 0f, 5f, 10f, 20f, 40f, 90f })
                    Measure(report, positions, triangles, reflex);
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
            }

            report.AppendLine("=== end TriangleSweep ===");
            Emit(report);
        }

        static void Measure(StringBuilder report, Vector3[] positions, int[] triangles, float reflex)
        {
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = k_Target;

                var profile = RemeshProfile.For(RemeshProfileKind.Character);
                profile.trianglePairingMaxReflexDegrees = reflex;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
                var ms = watch.ElapsedMilliseconds;

                var faces = result.extraction.faces;
                var tris = 0;
                var ngons = 0;
                foreach (var face in faces)
                {
                    if (face == null) continue;
                    if (face.Length == 3) tris++;
                    else if (face.Length > 4) ngons++;
                }

                Score(faces, result.extraction.positions,
                    out var over45, out var angleP01, out var curled);

                report.AppendLine(
                    $"  {reflex:F0},{result.extraction.faceCount}," +
                    $"{result.extraction.quadFraction * 100f:F2},{tris},{ngons}," +
                    $"{angleP01:F1},{over45:F2},{curled:F2},{ms}");

                if (result.extraction.trianglePairingStats != null)
                    report.AppendLine($"      1st {result.extraction.trianglePairingStats}");
                if (result.extraction.trianglePairingAfterNgonStats != null)
                    report.AppendLine($"      2nd {result.extraction.trianglePairingAfterNgonStats}");
                if (result.extraction.ngonSplitStats != null)
                    report.AppendLine($"      ngon {result.extraction.ngonSplitStats}");
                if (result.extraction.regularizeStats != null)
                    report.AppendLine($"      {result.extraction.regularizeStats}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  {reflex:F0},threw: {ex.Message}");
            }
        }

        static void Score(List<int[]> faces, Vector3[] positions,
            out float over45, out float angleP01, out float curledPercent)
        {
            var normals = new Vector3[faces.Count];
            for (var f = 0; f < faces.Count; f++)
                normals[f] = Normalize(Newell(positions, faces[f]));

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

            var over45Count = 0;
            var pairs = 0;
            foreach (var pair in edgeToFaces.Values)
            {
                if (pair.Count != 2) continue;
                var a = normals[pair[0]];
                var b = normals[pair[1]];
                if (a == Vector3.zero || b == Vector3.zero) continue;

                pairs++;
                if (Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f)) * Mathf.Rad2Deg > 45f)
                    over45Count++;
            }
            over45 = 100f * over45Count / Mathf.Max(1, pairs);

            var angles = new float[faces.Count];
            var curled = 0;
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                var worst = 180f;
                var convex = true;
                for (var k = 0; k < face.Length; k++)
                {
                    var previous = positions[face[(k + face.Length - 1) % face.Length]];
                    var current = positions[face[k]];
                    var following = positions[face[(k + 1) % face.Length]];

                    var u = previous - current;
                    var v = following - current;
                    var lu = u.magnitude;
                    var lv = v.magnitude;
                    if (lu < 1e-12f || lv < 1e-12f) { worst = 0f; continue; }

                    worst = Mathf.Min(worst,
                        Mathf.Acos(Mathf.Clamp(Vector3.Dot(u, v) / (lu * lv), -1f, 1f)) * Mathf.Rad2Deg);
                    if (Vector3.Dot(Vector3.Cross(v, u), normals[f]) < 0f) convex = false;
                }
                if (!convex) curled++;
                angles[f] = worst;
            }

            Array.Sort(angles);
            angleP01 = angles.Length > 0
                ? angles[Mathf.Clamp(Mathf.RoundToInt(0.01f * (angles.Length - 1)), 0, angles.Length - 1)]
                : 0f;
            curledPercent = 100f * curled / Mathf.Max(1, faces.Count);
        }

        static Vector3 Newell(Vector3[] positions, int[] face)
        {
            var normal = Vector3.zero;
            for (var k = 0; k < face.Length; k++)
            {
                var a = positions[face[k]];
                var b = positions[face[(k + 1) % face.Length]];
                normal += new Vector3(
                    (a.y - b.y) * (a.z + b.z),
                    (a.z - b.z) * (a.x + b.x),
                    (a.x - b.x) * (a.y + b.y));
            }
            return normal;
        }

        /// <summary>
        /// Explicit magnitude rather than <c>Vector3.normalized</c>, which returns zero below 1e-5.
        /// </summary>
        static Vector3 Normalize(Vector3 v)
        {
            var m = v.magnitude;
            return m > 0f ? v / m : Vector3.zero;
        }

        static void Emit(StringBuilder report)
        {
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }
    }
}
