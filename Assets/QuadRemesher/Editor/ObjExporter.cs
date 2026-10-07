// Quad Remesher Bridge for Unity
// Port of the Blender bridge by Maxime Rouca (Exoside).
//
// The Blender bridge hands the engine an .fbx because Blender ships an FBX exporter. Unity does
// not, but the engine reads .obj just as well (and .obj carries everything the remesher looks at:
// positions, split normals, UVs and per-face material ids), so that is what this bridge writes.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Exoside.QuadRemesher
{
    /// <summary>What was written, so the result can be mapped back onto the source object.</summary>
    internal sealed class ObjExportResult
    {
        /// <summary>Material name written per submesh, in submesh order.</summary>
        public readonly List<string> materialNames = new List<string>();
        public int positionCount;
        public int faceCount;
    }

    internal static class ObjExporter
    {
        /// <summary>Float format that round-trips through a 32-bit float exactly.</summary>
        const string k_F = "G9";

        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// .obj is right-handed, Unity is left-handed. Negating X converts between the two; the
        /// importer applies the very same negation, so a round-trip through the engine is exact.
        /// </summary>
        public static Vector3 ToObjSpace(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        /// <summary>
        /// Writes <paramref name="mesh"/> as a Wavefront .obj (plus a sibling .mtl when it has
        /// more than one material).
        /// </summary>
        /// <remarks>
        /// Unity render meshes are split at every UV and normal seam, so the same position often
        /// appears several times in <c>mesh.vertices</c>. Writing those as distinct .obj vertices
        /// would hand the engine a disconnected polygon soup and wreck the remesh, so positions,
        /// normals and UVs are each de-duplicated and referenced independently per face corner --
        /// which is exactly what the <c>v/vt/vn</c> triple in .obj is for.
        /// </remarks>
        public static ObjExportResult Write(string path, Mesh mesh, Material[] materials, string objectName)
        {
            var result = new ObjExportResult();

            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uvs = mesh.uv;

            var hasNormals = normals != null && normals.Length == vertices.Length;
            var hasUVs = uvs != null && uvs.Length == vertices.Length;

            // --- de-duplicate each attribute, and remember where every Unity vertex landed ---
            var posIndex = new int[vertices.Length];
            var uvIndex = hasUVs ? new int[vertices.Length] : null;
            var nrmIndex = hasNormals ? new int[vertices.Length] : null;

            var uniquePositions = BuildUnique(vertices, posIndex, ToObjSpace);
            var uniqueNormals = hasNormals ? BuildUnique(normals, nrmIndex, ToObjSpace) : null;
            var uniqueUVs = hasUVs ? BuildUnique(uvs, uvIndex, v => v) : null;

            result.positionCount = uniquePositions.Count;

            var mtlName = Path.GetFileNameWithoutExtension(path) + ".mtl";

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16))
            {
                writer.WriteLine("# Obj exported from Unity by Quad Remesher Bridge");
                writer.WriteLine("mtllib " + mtlName);
                writer.WriteLine("o " + SanitizeName(objectName));

                foreach (var v in uniquePositions)
                {
                    writer.WriteLine(string.Concat("v ",
                        v.x.ToString(k_F, k_Inv), " ", v.y.ToString(k_F, k_Inv), " ", v.z.ToString(k_F, k_Inv)));
                }

                if (uniqueUVs != null)
                {
                    foreach (var t in uniqueUVs)
                        writer.WriteLine(string.Concat("vt ", t.x.ToString(k_F, k_Inv), " ", t.y.ToString(k_F, k_Inv)));
                }

                if (uniqueNormals != null)
                {
                    foreach (var n in uniqueNormals)
                    {
                        writer.WriteLine(string.Concat("vn ",
                            n.x.ToString(k_F, k_Inv), " ", n.y.ToString(k_F, k_Inv), " ", n.z.ToString(k_F, k_Inv)));
                    }
                }

                var usedNames = new HashSet<string>();
                var corner = new StringBuilder(48);

                for (var sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    var materialName = UniqueMaterialName(materials, sub, usedNames);
                    result.materialNames.Add(materialName);
                    writer.WriteLine("usemtl " + materialName);

                    if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                    {
                        Debug.LogWarning($"[Quad Remesher] Submesh {sub} of '{mesh.name}' is not made of " +
                                         "triangles and was skipped.");
                        continue;
                    }

                    var tris = mesh.GetTriangles(sub);
                    for (var i = 0; i + 2 < tris.Length; i += 3)
                    {
                        // Negating X flipped the handedness, so the winding has to be reversed
                        // to keep the faces pointing the same way.
                        corner.Length = 0;
                        corner.Append('f');
                        AppendCorner(corner, tris[i], posIndex, uvIndex, nrmIndex);
                        AppendCorner(corner, tris[i + 2], posIndex, uvIndex, nrmIndex);
                        AppendCorner(corner, tris[i + 1], posIndex, uvIndex, nrmIndex);
                        writer.WriteLine(corner.ToString());
                        result.faceCount++;
                    }
                }
            }

            WriteMtl(Path.Combine(Path.GetDirectoryName(path) ?? ".", mtlName), materials, result.materialNames);

            return result;
        }

        static void AppendCorner(StringBuilder sb, int vertex, int[] posIndex, int[] uvIndex, int[] nrmIndex)
        {
            sb.Append(' ').Append(posIndex[vertex] + 1);

            if (uvIndex != null)
                sb.Append('/').Append(uvIndex[vertex] + 1);
            else if (nrmIndex != null)
                sb.Append('/');

            if (nrmIndex != null)
                sb.Append('/').Append(nrmIndex[vertex] + 1);
        }

        static List<T> BuildUnique<T>(T[] source, int[] indexOut, System.Func<T, T> transform)
        {
            var unique = new List<T>(source.Length);
            var seen = new Dictionary<T, int>(source.Length);

            for (var i = 0; i < source.Length; i++)
            {
                var value = transform(source[i]);
                if (!seen.TryGetValue(value, out var index))
                {
                    index = unique.Count;
                    unique.Add(value);
                    seen.Add(value, index);
                }
                indexOut[i] = index;
            }

            return unique;
        }

        static void WriteMtl(string path, Material[] materials, List<string> names)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("# Material library exported from Unity by Quad Remesher Bridge");
                for (var i = 0; i < names.Count; i++)
                {
                    var color = Color.gray;
                    if (materials != null && i < materials.Length && materials[i] != null &&
                        materials[i].HasProperty("_BaseColor"))
                        color = materials[i].GetColor("_BaseColor");
                    else if (materials != null && i < materials.Length && materials[i] != null &&
                             materials[i].HasProperty("_Color"))
                        color = materials[i].GetColor("_Color");

                    writer.WriteLine();
                    writer.WriteLine("newmtl " + names[i]);
                    writer.WriteLine(string.Format(k_Inv, "Kd {0:0.######} {1:0.######} {2:0.######}",
                        color.r, color.g, color.b));
                    writer.WriteLine("Ka 0 0 0");
                    writer.WriteLine("Ks 0.1 0.1 0.1");
                    writer.WriteLine("illum 2");
                }
            }
        }

        static string UniqueMaterialName(Material[] materials, int submesh, HashSet<string> used)
        {
            var baseName = materials != null && submesh < materials.Length && materials[submesh] != null
                ? SanitizeName(materials[submesh].name)
                : "Material" + submesh;

            if (string.IsNullOrEmpty(baseName))
                baseName = "Material" + submesh;

            var name = baseName;
            var suffix = 1;
            while (!used.Add(name))
                name = baseName + "_" + suffix++;

            return name;
        }

        /// <summary>.obj tokens are whitespace-separated, so names must not contain whitespace.</summary>
        static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "Object";

            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(char.IsWhiteSpace(c) ? '_' : c);
            return sb.ToString();
        }
    }
}
