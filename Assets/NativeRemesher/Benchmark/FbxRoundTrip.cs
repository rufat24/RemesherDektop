// Does the .fbx we write come back into Unity as the mesh we wrote?
//
// Two conventions decide that and both are easy to get wrong in a way that looks fine in the file:
// handedness (FBX is right-handed, Unity is not, and getting it wrong gives a MIRRORED mesh that
// passes every symmetry and quality check) and units (the header declares a scale the importer
// divides by, and getting it wrong gives a mesh a hundred times too small - which is what the
// competitor's own output does on the way back into this project, so it is not a hypothetical).
//
// Neither can be settled by reading documentation, so this writes a mesh whose every symmetry is
// deliberately broken, imports it through Unity's own importer, and compares.
//
// THE FIXTURE. A unit cube would be useless: it is symmetric under exactly the transforms being
// tested, so a mirror is invisible. This is a box of different extents in all three axes with one
// corner pulled out, so that position, scale, handedness and winding each have a witness.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class FbxRoundTrip
    {
        /// <summary>Inside Assets/, because AssetDatabase cannot import from anywhere else.</summary>
        const string k_Folder = "Assets/NativeRemesherRoundTrip";

        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine();
            report.AppendLine("=== FbxRoundTrip ===");

            try
            {
                Fixture(out var positions, out var faces, out var normals);
                report.AppendLine($"  wrote {positions.Length} verts, {faces.Count} faces " +
                                  $"({Quads(faces)} of them quads)");

                Directory.CreateDirectory(k_Folder);
                var path = k_Folder + "/roundtrip.fbx";
                // Per-corner UVs, different on every face, so a corner written to the wrong place
                // or a layer the importer ignored both show up as a (position, UV) pair never written.
                var faceUvs = new List<Vector2[]>();
                for (var f = 0; f < faces.Count; f++)
                {
                    var uv = new Vector2[faces[f].Length];
                    for (var k = 0; k < uv.Length; k++)
                        uv[k] = new Vector2(f / 6f + (k == 1 || k == 2 ? 0.15f : 0f), k >= 2 ? 0.9f : 0.1f);
                    faceUvs.Add(uv);
                }
                FbxWriter.Write(path, positions, faces, normals, "roundtrip",
                    FbxWriter.defaultUnitScale, faceUvs);
                report.AppendLine($"  {new FileInfo(path).Length:N0} bytes");

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var imported = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (imported == null)
                {
                    // A GameObject import puts the mesh underneath rather than at the root.
                    foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path))
                        if (sub is Mesh mesh) { imported = mesh; break; }
                }

                if (imported == null)
                {
                    report.AppendLine("  FAILED: Unity imported no mesh from the file at all");
                }
                else
                {
                    Compare(report, positions, faces, imported);
                    CompareUvs(report, positions, faces, faceUvs, imported);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  threw: {ex}");
            }
            finally
            {
                AssetDatabase.DeleteAsset(k_Folder);
            }

            report.AppendLine("=== end FbxRoundTrip ===");
            Debug.Log(report.ToString());
            Console.WriteLine(report.ToString());
        }

        static void Compare(StringBuilder report, Vector3[] positions, List<int[]> faces, Mesh imported)
        {
            var wroteMin = positions[0];
            var wroteMax = positions[0];
            foreach (var p in positions)
            {
                wroteMin = Vector3.Min(wroteMin, p);
                wroteMax = Vector3.Max(wroteMax, p);
            }

            var got = imported.bounds;
            var gotMin = got.min;
            var gotMax = got.max;

            report.AppendLine($"  wrote bounds  min ({wroteMin.x:F3},{wroteMin.y:F3},{wroteMin.z:F3})" +
                              $"  max ({wroteMax.x:F3},{wroteMax.y:F3},{wroteMax.z:F3})");
            report.AppendLine($"  imported      min ({gotMin.x:F3},{gotMin.y:F3},{gotMin.z:F3})" +
                              $"  max ({gotMax.x:F3},{gotMax.y:F3},{gotMax.z:F3})");

            var wroteSize = wroteMax - wroteMin;
            var gotSize = gotMax - gotMin;
            var scale = wroteSize.x > 0f ? gotSize.x / wroteSize.x : 0f;
            report.AppendLine($"  SCALE      x{scale:F4}" +
                              (Mathf.Abs(scale - 1f) < 0.01f ? "  (1:1, correct)"
                                  : "  WRONG - the unit scale in the header does not match"));

            // A mirror shows up as the pulled corner landing on the other side, which is what the
            // asymmetric fixture is for: compare where the extreme vertex sits along each axis.
            var wroteFar = Farthest(positions);
            var gotFar = Farthest(imported.vertices);
            var mirrored = Mathf.Sign(wroteFar.x) != Mathf.Sign(gotFar.x)
                           || Mathf.Sign(wroteFar.z) != Mathf.Sign(gotFar.z);
            report.AppendLine($"  wrote far corner ({wroteFar.x:F3},{wroteFar.y:F3},{wroteFar.z:F3})" +
                              $"  imported ({gotFar.x / Mathf.Max(1e-6f, scale):F3}," +
                              $"{gotFar.y / Mathf.Max(1e-6f, scale):F3}," +
                              $"{gotFar.z / Mathf.Max(1e-6f, scale):F3})");
            report.AppendLine(mirrored ? "  HANDEDNESS WRONG - the mesh came back mirrored"
                : "  HANDEDNESS correct");

            report.AppendLine($"  vertices {imported.vertexCount} (wrote {positions.Length})");
            for (var submesh = 0; submesh < imported.subMeshCount; submesh++)
                report.AppendLine($"  submesh {submesh}: topology {imported.GetTopology(submesh)}, " +
                                  $"{imported.GetIndices(submesh).Length} indices");

            // Unity's importer triangulates on import whatever the file holds, so the quads are not
            // expected to survive INTO Unity - they survive in the FILE, which is what any other
            // application opening it will read. Counting the triangles confirms the polygons were
            // read as quads rather than as something the importer had to guess at.
            var triangles = imported.triangles.Length / 3;
            var expected = 0;
            foreach (var face in faces) expected += face.Length - 2;
            report.AppendLine($"  triangles after import {triangles}, expected {expected} " +
                              $"from fan-triangulating what was written" +
                              (triangles == expected ? "  (polygons read correctly)" : "  MISMATCH"));

            // Signed volume: negative means inside-out, which is the winding half of handedness.
            report.AppendLine($"  signed volume wrote {SignedVolume(positions, faces):F4}, " +
                              $"imported {SignedVolumeTriangles(imported):F4} " +
                              "(same sign = winding survived)");
        }

        static void CompareUvs(StringBuilder report, Vector3[] positions, List<int[]> faces,
            List<Vector2[]> faceUvs, Mesh imported)
        {
            var uvs = imported.uv;
            if (uvs == null || uvs.Length != imported.vertexCount)
            {
                report.AppendLine("  UVS MISSING - the importer read no UV channel");
                return;
            }

            // Unity negates X on import; positions come back 1:1 otherwise (checked above).
            var written = new List<(Vector3, Vector2)>();
            for (var f = 0; f < faces.Count; f++)
                for (var k = 0; k < faces[f].Length; k++)
                    written.Add((positions[faces[f][k]], faceUvs[f][k]));

            var vertices = imported.vertices;
            var bad = 0;
            for (var v = 0; v < vertices.Length; v++)
            {
                var found = false;
                foreach (var (p, uv) in written)
                    if ((p - vertices[v]).sqrMagnitude < 1e-8f && (uv - uvs[v]).sqrMagnitude < 1e-8f)
                    {
                        found = true;
                        break;
                    }
                if (!found) bad++;
            }
            report.AppendLine($"  UVS {uvs.Length} imported, {bad} with a (position, UV) pair never written" +
                              (bad == 0 ? "  (corners carried correctly)" : "  WRONG"));
        }

        static Vector3 Farthest(Vector3[] positions)
        {
            var best = positions[0];
            var bestD = best.sqrMagnitude;
            foreach (var p in positions)
                if (p.sqrMagnitude > bestD) { bestD = p.sqrMagnitude; best = p; }
            return best;
        }

        static int Quads(List<int[]> faces)
        {
            var n = 0;
            foreach (var f in faces) if (f.Length == 4) n++;
            return n;
        }

        static float SignedVolume(Vector3[] p, List<int[]> faces)
        {
            var total = 0f;
            foreach (var f in faces)
                for (var k = 1; k + 1 < f.Length; k++)
                    total += Vector3.Dot(Vector3.Cross(p[f[0]], p[f[k]]), p[f[k + 1]]) / 6f;
            return total;
        }

        static float SignedVolumeTriangles(Mesh mesh)
        {
            var p = mesh.vertices;
            var t = mesh.triangles;
            var total = 0f;
            for (var i = 0; i + 2 < t.Length; i += 3)
                total += Vector3.Dot(Vector3.Cross(p[t[i]], p[t[i + 1]]), p[t[i + 2]]) / 6f;
            return total;
        }

        /// <summary>A box of three different extents with one corner pulled out, all quads.</summary>
        static void Fixture(out Vector3[] positions, out List<int[]> faces, out Vector3[] normals)
        {
            positions = new[]
            {
                new Vector3(-1f, -2f, -3f), new Vector3(1f, -2f, -3f),
                new Vector3(1f, 2f, -3f),   new Vector3(-1f, 2f, -3f),
                new Vector3(-1f, -2f, 3f),  new Vector3(1f, -2f, 3f),
                new Vector3(2.5f, 2f, 4.5f),                            // the pulled corner
                new Vector3(-1f, 2f, 3f)
            };

            faces = new List<int[]>
            {
                new[] { 0, 3, 2, 1 },   // -Z
                new[] { 4, 5, 6, 7 },   // +Z
                new[] { 0, 1, 5, 4 },   // -Y
                new[] { 2, 3, 7, 6 },   // +Y
                new[] { 0, 4, 7, 3 },   // -X
                new[] { 1, 2, 6, 5 }    // +X
            };

            normals = new Vector3[positions.Length];
            for (var i = 0; i < positions.Length; i++) normals[i] = positions[i].normalized;
        }
    }
}
