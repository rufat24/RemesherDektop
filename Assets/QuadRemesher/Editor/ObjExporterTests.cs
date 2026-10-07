// Quad Remesher Bridge for Unity
//
// Verification of ObjExporter -- the one piece of this bridge that is not a transcription of the
// Blender addon, and so the one that can actually be wrong.
//
// The exporter's real contract is with the engine, which is picky about two things:
//   . positions must be welded (Unity splits vertices at every UV/normal seam, and a mesh handed
//     over unwelded reads to the engine as disconnected polygon soup)
//   . winding must survive the left-handed -> right-handed flip
//
// Run from the menu (Tools > Quad Remesher > Run Obj Exporter Tests), or headless with:
//   Unity.exe -batchmode -nographics -projectPath . -executeMethod \
//       Exoside.QuadRemesher.ObjExporterTests.Run

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Exoside.QuadRemesher
{
    internal static class ObjExporterTests
    {
        static int s_Failures;

        [MenuItem("Tools/Quad Remesher/Run Obj Exporter Tests", false, 100)]
        public static void Run()
        {
            s_Failures = 0;

            var dir = Path.Combine(Path.GetTempPath(), "QuadRemesherObjTests");
            Directory.CreateDirectory(dir);

            try
            {
                TestCubeExport(dir);
            }
            catch (Exception e)
            {
                Fail("unexpected exception: " + e);
            }

            if (s_Failures == 0)
                Debug.Log("[Quad Remesher] ALL TESTS PASSED");
            else
                Debug.LogError($"[Quad Remesher] {s_Failures} TEST(S) FAILED");

            // Only ever tear the editor down when we were started for exactly that.
            if (Application.isBatchMode)
                EditorApplication.Exit(s_Failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// A Unity cube is 24 vertices (split per face for normals) but only 8 distinct positions.
        /// The export must weld those to 8 and keep every face pointing the same way.
        /// </summary>
        static void TestCubeExport(string dir)
        {
            var mesh = BuildCube();
            var materials = new[] { NewMaterial("Red"), NewMaterial("Blue") };

            var path = Path.Combine(dir, "cube.obj");
            var export = ObjExporter.Write(path, mesh, materials, "TestCube");

            AssertEqual(8, export.positionCount, "positions welded (24 Unity verts -> 8)");
            AssertEqual(12, export.faceCount, "exported triangles");
            AssertEqual(2, export.materialNames.Count, "material groups");
            AssertEqual("Red", export.materialNames[0], "first material name");

            var obj = ParseObj(path);

            AssertEqual(8, obj.positions.Count, "'v' lines written");
            AssertEqual(12, obj.faces.Count, "'f' lines written");
            AssertEqual(2, obj.materials.Count, "'usemtl' lines written");
            AssertEqual(true, File.Exists(Path.Combine(dir, "cube.mtl")), "companion .mtl written");

            // Every index must be in range, or the engine reads garbage.
            foreach (var face in obj.faces)
            {
                foreach (var i in face)
                {
                    if (i < 0 || i >= obj.positions.Count)
                    {
                        Fail($"face index {i} out of range (0..{obj.positions.Count - 1})");
                        return;
                    }
                }
            }

            // The cube is 2x2x2 about the origin, so the welded corners must be exactly that.
            var min = Vector3.one * float.MaxValue;
            var max = Vector3.one * float.MinValue;
            foreach (var p in obj.positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            AssertApprox(new Vector3(-1, -1, -1), min, "welded bounds min");
            AssertApprox(new Vector3(1, 1, 1), max, "welded bounds max");

            // Signed volume computed from the .obj itself. Negating X flips handedness, so the
            // exporter reverses winding to compensate; if it did not, this comes out negative.
            var volume = SignedVolume(obj);
            AssertApprox(8f, volume, "signed volume in obj space (winding compensated)");

            // Each material group must keep its own half of the cube.
            AssertEqual(6, CountFacesFor(obj, 0), "faces under first usemtl");
            AssertEqual(6, CountFacesFor(obj, 1), "faces under second usemtl");
        }

        // ---------------------------------------------------------------- minimal obj reader

        sealed class ParsedObj
        {
            public readonly List<Vector3> positions = new List<Vector3>();
            public readonly List<int[]> faces = new List<int[]>();
            public readonly List<string> materials = new List<string>();
            /// <summary>Index into <see cref="materials"/> for each face.</summary>
            public readonly List<int> faceMaterial = new List<int>();
        }

        /// <summary>Deliberately dumb and test-local, so it cannot share a bug with the exporter.</summary>
        static ParsedObj ParseObj(string path)
        {
            var obj = new ParsedObj();
            var current = -1;

            foreach (var line in File.ReadAllLines(path))
            {
                var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0 || tokens[0].StartsWith("#"))
                    continue;

                switch (tokens[0])
                {
                    case "v":
                        obj.positions.Add(new Vector3(
                            float.Parse(tokens[1], CultureInfo.InvariantCulture),
                            float.Parse(tokens[2], CultureInfo.InvariantCulture),
                            float.Parse(tokens[3], CultureInfo.InvariantCulture)));
                        break;

                    case "usemtl":
                        obj.materials.Add(tokens[1]);
                        current = obj.materials.Count - 1;
                        break;

                    case "f":
                        var face = new int[tokens.Length - 1];
                        for (var i = 1; i < tokens.Length; i++)
                            face[i - 1] = int.Parse(tokens[i].Split('/')[0], CultureInfo.InvariantCulture) - 1;
                        obj.faces.Add(face);
                        obj.faceMaterial.Add(current);
                        break;
                }
            }

            return obj;
        }

        static int CountFacesFor(ParsedObj obj, int material)
        {
            var count = 0;
            foreach (var m in obj.faceMaterial)
            {
                if (m == material)
                    count++;
            }
            return count;
        }

        static float SignedVolume(ParsedObj obj)
        {
            var volume = 0f;
            foreach (var face in obj.faces)
            {
                for (var i = 1; i + 1 < face.Length; i++)
                {
                    var a = obj.positions[face[0]];
                    var b = obj.positions[face[i]];
                    var c = obj.positions[face[i + 1]];
                    volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;
                }
            }
            return volume;
        }

        // ---------------------------------------------------------------- fixtures

        /// <summary>A cube built the way Unity builds one: 24 vertices, split per face.</summary>
        static Mesh BuildCube()
        {
            var corners = new[]
            {
                new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
                new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1)
            };

            // Each face wound clockwise seen from outside (Unity's front-face convention).
            var faces = new[]
            {
                new[] { 0, 3, 2, 1 }, // back  (-z)
                new[] { 4, 5, 6, 7 }, // front (+z)
                new[] { 0, 1, 5, 4 }, // bottom
                new[] { 2, 3, 7, 6 }, // top
                new[] { 1, 2, 6, 5 }, // right
                new[] { 0, 4, 7, 3 }  // left
            };

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var submeshTris = new[] { new List<int>(), new List<int>() };

            for (var f = 0; f < faces.Length; f++)
            {
                var baseIndex = vertices.Count;
                foreach (var c in faces[f])
                {
                    vertices.Add(corners[c]);
                    uvs.Add(new Vector2(c & 1, (c >> 1) & 1));
                }

                var tris = submeshTris[f < 3 ? 0 : 1];
                tris.Add(baseIndex + 0);
                tris.Add(baseIndex + 1);
                tris.Add(baseIndex + 2);
                tris.Add(baseIndex + 0);
                tris.Add(baseIndex + 2);
                tris.Add(baseIndex + 3);
            }

            var mesh = new Mesh { name = "TestCube" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(submeshTris[0], 0);
            mesh.SetTriangles(submeshTris[1], 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Material NewMaterial(string name)
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            return new Material(shader) { name = name };
        }

        // ---------------------------------------------------------------- assertions

        static void AssertEqual<T>(T expected, T actual, string what)
        {
            if (Equals(expected, actual))
                Debug.Log($"  PASS  {what}: {actual}");
            else
                Fail($"{what}: expected {expected}, got {actual}");
        }

        static void AssertApprox(float expected, float actual, string what)
        {
            if (Mathf.Abs(expected - actual) < 0.001f)
                Debug.Log($"  PASS  {what}: {actual}");
            else
                Fail($"{what}: expected {expected}, got {actual}");
        }

        static void AssertApprox(Vector3 expected, Vector3 actual, string what)
        {
            if (Vector3.Distance(expected, actual) < 0.001f)
                Debug.Log($"  PASS  {what}: {actual}");
            else
                Fail($"{what}: expected {expected}, got {actual}");
        }

        static void Fail(string message)
        {
            s_Failures++;
            Debug.LogError("  FAIL  " + message);
        }
    }
}
