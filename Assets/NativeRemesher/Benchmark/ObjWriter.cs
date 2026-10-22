// Minimal Wavefront OBJ writer that preserves face arity.
//
// Exists for one reason: every other way of getting extraction output out of Unity destroys the
// thing being measured. Mesh import has keepQuads off, ToMesh splits n-gons into a triangle
// submesh, and FBX export round-trips through the importer. A quad-dominance number read back
// through any of those is a number about Unity's triangulator.
//
// So faces are written exactly as the extractor emitted them, and the same external script that
// scores a third-party reference mesh scores ours. Positions only — the benchmark's geometry
// metrics run inside Unity, and normals and UVs would just be more surface to be wrong about.
//
// Handedness is left alone (unlike QuadRemesher/Editor/ObjExporter.cs, which flips for the reference engine).
// A mirrored comparison would show up as a sign error in nothing we measure here, so the
// no-op is the honest choice rather than a convenient one.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class ObjWriter
    {
        public static void Write(string path, Vector3[] positions, List<int[]> faces, string name)
        {
            var culture = CultureInfo.InvariantCulture;
            var builder = new StringBuilder(positions.Length * 40 + faces.Count * 24);

            builder.Append("# NativeRemesher extraction output\n");
            builder.Append("o ").Append(string.IsNullOrEmpty(name) ? "mesh" : name).Append('\n');

            foreach (var p in positions)
            {
                builder.Append("v ")
                    .Append(p.x.ToString("R", culture)).Append(' ')
                    .Append(p.y.ToString("R", culture)).Append(' ')
                    .Append(p.z.ToString("R", culture)).Append('\n');
            }

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;

                builder.Append('f');
                foreach (var corner in face)
                    builder.Append(' ').Append((corner + 1).ToString(culture)); // OBJ is 1-based

                builder.Append('\n');
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, builder.ToString());
        }
    }
}
