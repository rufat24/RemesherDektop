// Puts something in the scene to remesh.
//
// The benchmark corpus is generated in code, and the project ships no model assets, so a fresh
// checkout has literally nothing for the remesher window to act on — it just reports "Select a
// GameObject with a mesh". These menu items close that gap so the tool can be tried in one click.
//
// The spawned mesh is saved as an asset rather than left in memory, for the same reason the remesher
// saves its output: a procedurally built Mesh assigned only to a MeshFilter does not survive a domain
// reload, and a GameObject whose mesh has silently become null is a confusing thing to hand someone.

using System;
using System.IO;
using NativeRemesher.Benchmark;
using UnityEditor;
using UnityEngine;

namespace NativeRemesher
{
    internal static class TestMeshSpawner
    {
        const string k_Folder = "Assets/NativeRemesher/TestMeshes";
        const string k_Menu = "Tools/Native Remesher/Spawn Test Mesh/";

        [MenuItem(k_Menu + "Subdivided Cube (hard surface)", false, 200)]
        static void SpawnCube() => Spawn("cube_subdivided", () => MeshCorpus.SubdividedCube(1f, 24));

        [MenuItem(k_Menu + "Icosphere (smooth)", false, 201)]
        static void SpawnSphere() => Spawn("icosphere", () => MeshCorpus.Icosphere(5, 1f));

        [MenuItem(k_Menu + "Torus (genus 1)", false, 202)]
        static void SpawnTorus() => Spawn("torus", () => MeshCorpus.Torus(1f, 0.35f, 128, 48));

        [MenuItem(k_Menu + "Capped Cylinder (creases)", false, 203)]
        static void SpawnCylinder() => Spawn("cylinder_capped", () => MeshCorpus.CappedCylinder(0.5f, 2f, 96, 24));

        [MenuItem(k_Menu + "Bumpy Sphere (varying curvature)", false, 204)]
        static void SpawnBumpy() => Spawn("sphere_bumpy", () => MeshCorpus.BumpySphere(6, 1f, 0.12f, 6f));

        [MenuItem(k_Menu + "Open Disc (boundary)", false, 205)]
        static void SpawnDisc() => Spawn("disc_open", () => MeshCorpus.OpenDisc(1f, 96, 24));

        [MenuItem(k_Menu + "Spawn All In A Row", false, 220)]
        static void SpawnAll()
        {
            var factories = new (string name, Func<Mesh> create)[]
            {
                ("cube_subdivided", () => MeshCorpus.SubdividedCube(1f, 24)),
                ("icosphere", () => MeshCorpus.Icosphere(5, 1f)),
                ("torus", () => MeshCorpus.Torus(1f, 0.35f, 128, 48)),
                ("cylinder_capped", () => MeshCorpus.CappedCylinder(0.5f, 2f, 96, 24)),
                ("sphere_bumpy", () => MeshCorpus.BumpySphere(6, 1f, 0.12f, 6f)),
                ("disc_open", () => MeshCorpus.OpenDisc(1f, 96, 24))
            };

            GameObject last = null;
            for (var i = 0; i < factories.Length; i++)
                last = Spawn(factories[i].name, factories[i].create, new Vector3(i * 3f, 0f, 0f), false);

            if (last != null) Selection.activeGameObject = last;
        }

        static GameObject Spawn(string name, Func<Mesh> create) =>
            Spawn(name, create, Vector3.zero, true);

        static GameObject Spawn(string name, Func<Mesh> create, Vector3 position, bool select)
        {
            var mesh = create();

            var path = SaveAsset(mesh, name);
            if (!string.IsNullOrEmpty(path))
            {
                var loaded = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (loaded != null) mesh = loaded;
            }

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Spawn Test Mesh");
            go.transform.position = position;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = DefaultMaterial();

            if (select) Selection.activeGameObject = go;

            var triangles = mesh.triangles.Length / 3;
            Debug.Log($"[Native Remesher] Spawned '{name}': {mesh.vertexCount:N0} verts, " +
                      $"{triangles:N0} tris. Select it and use Tools > Native Remesher.", go);

            return go;
        }

        static string SaveAsset(Mesh mesh, string name)
        {
            try
            {
                if (!AssetDatabase.IsValidFolder(k_Folder))
                    AssetDatabase.CreateFolder("Assets/NativeRemesher", "TestMeshes");

                var path = $"{k_Folder}/{name}.asset";
                if (File.Exists(path))
                {
                    // Reuse rather than piling up duplicates: these are fixed reference shapes, so
                    // the existing one is by definition identical.
                    UnityEngine.Object.DestroyImmediate(mesh);
                    return path;
                }

                AssetDatabase.CreateAsset(mesh, path);
                AssetDatabase.SaveAssets();
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Native Remesher] Could not save test mesh: {e.Message}. " +
                                 "It will work now but not survive a domain reload.");
                return "";
            }
        }

        /// <summary>
        /// A material that renders under whichever pipeline this project uses. Falls back to none
        /// rather than throwing — an unlit magenta object is still perfectly remeshable.
        /// </summary>
        static Material DefaultMaterial()
        {
            foreach (var shaderName in new[]
                     {
                         "Universal Render Pipeline/Lit",
                         "Standard",
                         "Sprites/Default"
                     })
            {
                var shader = Shader.Find(shaderName);
                if (shader != null) return new Material(shader) { name = "TestMesh" };
            }
            return null;
        }
    }
}
