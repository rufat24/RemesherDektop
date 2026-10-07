// Minimal Wavefront OBJ reader, for the external benchmark corpus only.
//
// Deliberately narrow: positions and faces, nothing else. The benchmark scores geometry and
// topology, so UVs, normals, materials and groups are all noise here — and reading less means
// fewer ways to be subtly wrong about someone else's file. N-gon faces are fan-triangulated on
// load because RemeshTopology.Build only consumes triangles.
//
// Note this is NOT the inverse of QuadRemesher/Editor/ObjExporter.cs and must not be used as one:
// that exporter flips handedness on the way out (ToObjSpace), which is correct for feeding the
// Exoside engine and wrong for reading a third-party mesh back into Unity space.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public static class ObjReader
    {
        /// <summary>
        /// Faces as written in the file, WITHOUT fan-triangulating them.
        /// </summary>
        /// <remarks>
        /// <see cref="Read"/> triangulates because <c>RemeshTopology.Build</c> only consumes
        /// triangles, and that is right for it. But quad topology is exactly what a competitor
        /// comparison is about, and it is unreadable through Unity - FBX imports with keepQuads off,
        /// so every competitor mesh arrives triangulated with its face structure gone. This is the
        /// only path that recovers it, so quad shape, planarity and valence can be scored on the
        /// same terms as ours.
        /// </remarks>
        public static List<int[]> ReadFaces(string path) =>
            ReadPolygons(path, out _);

        /// <summary>
        /// Positions AND faces from one pass, so their indices are guaranteed to agree.
        /// </summary>
        /// <remarks>
        /// The two must come from the same read. Pairing <see cref="ReadFaces"/> with positions
        /// pulled out of a <c>Mesh</c> built by <see cref="Read"/> puts raw OBJ face indices
        /// against a re-indexed vertex array, and every per-face measurement taken from the pair is
        /// then meaningless - while still producing plausible-looking numbers. That happened: a
        /// competitor comparison reported perfectly planar quads and a 90-degree normal error, both
        /// artefacts of exactly this mismatch.
        /// </remarks>
        public static List<int[]> ReadPolygons(string path, out Vector3[] positions)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path is empty.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("OBJ not found.", path);

            var faces = new List<int[]>();
            var corners = new List<int>(8);
            var vertices = new List<Vector3>();
            var culture = CultureInfo.InvariantCulture;

            using (var reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 2) continue;

                    if (line[0] == 'v' && line[1] == ' ')
                    {
                        var parts = line.Split((char[])null,
                            StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4
                            && float.TryParse(parts[1], NumberStyles.Float, culture, out var x)
                            && float.TryParse(parts[2], NumberStyles.Float, culture, out var y)
                            && float.TryParse(parts[3], NumberStyles.Float, culture, out var z))
                            vertices.Add(new Vector3(x, y, z));

                        continue;
                    }

                    if (line[0] != 'f' || line[1] != ' ') continue;

                    var vertexCount = vertices.Count;

                    corners.Clear();
                    var tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

                    for (var t = 1; t < tokens.Length; t++)
                    {
                        // "f v", "f v/vt", "f v/vt/vn" and "f v//vn" all carry the position index
                        // first, which is the only field this needs.
                        var slash = tokens[t].IndexOf('/');
                        var field = slash < 0 ? tokens[t] : tokens[t].Substring(0, slash);

                        if (!int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                out var index))
                        {
                            corners.Clear();
                            break;
                        }

                        // OBJ indices are 1-based, and a NEGATIVE index counts back from the last
                        // vertex read so far. Treating a negative as an absolute index would read
                        // the wrong vertex rather than fail, so it is resolved explicitly.
                        corners.Add(index > 0 ? index - 1 : vertexCount + index);
                    }

                    if (corners.Count >= 3) faces.Add(corners.ToArray());
                }
            }

            positions = vertices.ToArray();

            // Any face index outside the vertex array means the file was misread rather than that
            // the file is odd, and every downstream measurement would be silently wrong. Failing
            // here is the whole point of reading both in one pass.
            foreach (var face in faces)
            foreach (var corner in face)
            {
                if (corner >= 0 && corner < positions.Length) continue;
                throw new InvalidDataException(
                    $"{path}: face index {corner} outside 0..{positions.Length - 1}");
            }

            return faces;
        }

        public static Mesh Read(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path is empty.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("OBJ not found.", path);

            var positions = new List<Vector3>();
            var triangles = new List<int>();
            var face = new List<int>(8);
            var culture = CultureInfo.InvariantCulture;
            var skippedFaces = 0;

            using (var reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 2) continue;

                    // Only 'v ' and 'f ' matter. Checking the first two characters avoids a split
                    // on every comment and every vt/vn line, which on a 2M-line file is the
                    // difference between a second and a minute.
                    var c0 = line[0];
                    if (c0 != 'v' && c0 != 'f') continue;
                    var c1 = line[1];
                    if (c1 != ' ' && c1 != '\t') continue;

                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    if (c0 == 'v')
                    {
                        if (tokens.Length < 4) continue;
                        if (float.TryParse(tokens[1], NumberStyles.Float, culture, out var x) &&
                            float.TryParse(tokens[2], NumberStyles.Float, culture, out var y) &&
                            float.TryParse(tokens[3], NumberStyles.Float, culture, out var z))
                            positions.Add(new Vector3(x, y, z));
                        continue;
                    }

                    face.Clear();
                    for (var i = 1; i < tokens.Length; i++)
                    {
                        // "v", "v/vt", "v//vn", "v/vt/vn" — only the first field is wanted.
                        var token = tokens[i];
                        var slash = token.IndexOf('/');
                        if (slash >= 0) token = token.Substring(0, slash);

                        if (!int.TryParse(token, NumberStyles.Integer, culture, out var index))
                        {
                            face.Clear();
                            break;
                        }

                        // OBJ is 1-based, and negative indices count back from the current end.
                        if (index < 0) index = positions.Count + index;
                        else index -= 1;

                        if (index < 0 || index >= positions.Count)
                        {
                            face.Clear();
                            break;
                        }

                        face.Add(index);
                    }

                    if (face.Count < 3)
                    {
                        skippedFaces++;
                        continue;
                    }

                    for (var k = 1; k + 1 < face.Count; k++)
                    {
                        triangles.Add(face[0]);
                        triangles.Add(face[k]);
                        triangles.Add(face[k + 1]);
                    }
                }
            }

            if (positions.Count == 0)
                throw new InvalidDataException($"OBJ '{path}' contains no vertices.");

            if (skippedFaces > 0)
                Debug.LogWarning($"[Benchmark] {Path.GetFileName(path)}: skipped {skippedFaces} " +
                                 $"unparseable or degenerate face lines.");

            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
            if (positions.Count > 65534)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(positions);
            mesh.SetTriangles(triangles, 0, false);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
