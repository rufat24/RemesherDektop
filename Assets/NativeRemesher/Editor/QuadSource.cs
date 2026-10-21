// The artist's quads for a mesh imported from a model file.
//
// Unity triangulates a model on import unless Keep Quads is on, and with Optimize Polygons on (the
// default) it also reorders the triangles for the GPU, so a quad's two halves no longer sit side by
// side. Pairing them back by shape gets 95-99.6 % on the Porsche, and the pairs it gets wrong are
// not rare accidents: on a sheared panel (rows offset like brickwork) halves of two neighbouring
// quads make a better rectangle than the quads themselves -- 14 % of the door. QuadReduction needs
// the real quads to follow the real strips.
//
// So the file is imported once more, as a temporary copy with Keep Quads on, and the original
// mesh's own triangles are put in quad order: each quad's two triangles together. Nothing else of
// the mesh changes (same vertices, same triangles, same submeshes), and the user's asset and its
// import settings are not touched. The copy is deleted at once; the order is kept for the session.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NativeRemesher
{
    public static class QuadSource
    {
        // Per model file (path and write time): each mesh's quads, as corner positions.
        static readonly Dictionary<string, Dictionary<string, List<Vector3[]>>> s_Quads =
            new Dictionary<string, Dictionary<string, List<Vector3[]>>>();

        /// <summary>
        /// The mesh's triangles (and their submeshes) reordered so each of the source file's quads
        /// is two consecutive triangles; unchanged when the mesh is not from a model file, already
        /// keeps its quads, or has none.
        /// </summary>
        public static void Reorder(Mesh mesh, ref int[] triangles, ref int[] triangleSubmesh)
        {
            if (mesh == null || triangles == null || triangles.Length == 0) return;
            List<Vector3[]> quads;
            try { quads = QuadsOf(mesh); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Native Remesher] Could not read the quads of {mesh.name}: {e.Message}");
                return;
            }
            if (quads == null || quads.Count == 0) return;

            var weld = new Dictionary<Vector3, int>();
            var vertices = mesh.vertices;
            var welded = new int[vertices.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                if (!weld.TryGetValue(vertices[i], out var w)) { w = weld.Count; weld[vertices[i]] = w; }
                welded[i] = w;
            }
            var count = triangles.Length / 3;
            var byCorners = new Dictionary<(int, int, int), List<int>>(count);
            for (var t = 0; t < count; t++)
            {
                var key = Sorted(welded[triangles[3 * t]], welded[triangles[3 * t + 1]], welded[triangles[3 * t + 2]]);
                if (!byCorners.TryGetValue(key, out var list)) byCorners[key] = list = new List<int>(1);
                list.Add(t);
            }
            var used = new bool[count];
            var order = new List<int>(count);
            var corner = new int[4];
            foreach (var quad in quads)
            {
                var known = true;
                for (var k = 0; k < 4 && known; k++) known = weld.TryGetValue(quad[k], out corner[k]);
                if (!known) continue;
                // Whichever diagonal the importer split it on.
                if (!TakePair(corner[0], corner[1], corner[2], corner[0], corner[2], corner[3], byCorners, used, order))
                    TakePair(corner[0], corner[1], corner[3], corner[1], corner[2], corner[3], byCorners, used, order);
            }
            if (order.Count == 0) return;
            for (var t = 0; t < count; t++) if (!used[t]) order.Add(t);

            var reordered = new int[triangles.Length];
            var submesh = triangleSubmesh != null && triangleSubmesh.Length == count ? new int[count] : null;
            for (var i = 0; i < count; i++)
            {
                var t = order[i];
                reordered[3 * i] = triangles[3 * t];
                reordered[3 * i + 1] = triangles[3 * t + 1];
                reordered[3 * i + 2] = triangles[3 * t + 2];
                if (submesh != null) submesh[i] = triangleSubmesh[t];
            }
            triangles = reordered;
            if (submesh != null) triangleSubmesh = submesh;
        }

        static (int, int, int) Sorted(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }

        static bool TakePair(int a0, int b0, int c0, int a1, int b1, int c1,
            Dictionary<(int, int, int), List<int>> byCorners, bool[] used, List<int> order)
        {
            var first = Free(Sorted(a0, b0, c0), byCorners, used, -1);
            if (first < 0) return false;
            var second = Free(Sorted(a1, b1, c1), byCorners, used, first);
            if (second < 0) return false;
            used[first] = used[second] = true;
            order.Add(first);
            order.Add(second);
            return true;
        }

        static int Free((int, int, int) key, Dictionary<(int, int, int), List<int>> byCorners, bool[] used, int not)
        {
            if (!byCorners.TryGetValue(key, out var list)) return -1;
            foreach (var t in list) if (!used[t] && t != not) return t;
            return -1;
        }

        // The quads of the mesh of this name in its model file, read from a Keep Quads copy.
        static List<Vector3[]> QuadsOf(Mesh mesh)
        {
            var path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is ModelImporter importer)) return null;
            if (importer.keepQuads) return null; // ReadMesh already splits its quads in order
            var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (!s_Quads.TryGetValue(key, out var meshes))
            {
                meshes = ReadQuads(path);
                s_Quads[key] = meshes;
            }
            return meshes != null && meshes.TryGetValue(mesh.name, out var quads) ? quads : null;
        }

        static Dictionary<string, List<Vector3[]>> ReadQuads(string path)
        {
            const string folder = "Assets/NativeRemesherQuadsTemp";
            var copy = $"{folder}/source{Path.GetExtension(path)}";
            var meshes = new Dictionary<string, List<Vector3[]>>();
            try
            {
                if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "NativeRemesherQuadsTemp");
                if (!AssetDatabase.CopyAsset(path, copy)) return null;
                var importer = (ModelImporter)AssetImporter.GetAtPath(copy);
                importer.keepQuads = true;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                importer.importAnimation = false;
                importer.SaveAndReimport();
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(copy))
                {
                    if (!(asset is Mesh quadMesh) || meshes.ContainsKey(quadMesh.name)) continue;
                    var vertices = quadMesh.vertices;
                    var quads = new List<Vector3[]>();
                    for (var sub = 0; sub < quadMesh.subMeshCount; sub++)
                    {
                        if (quadMesh.GetTopology(sub) != MeshTopology.Quads) continue;
                        var indices = quadMesh.GetIndices(sub);
                        for (var i = 0; i + 3 < indices.Length; i += 4)
                            quads.Add(new[] { vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]], vertices[indices[i + 3]] });
                    }
                    meshes[quadMesh.name] = quads;
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
            return meshes;
        }
    }
}
