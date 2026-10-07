// Writes an .fbx, because that is the format the result has to leave Unity in.
//
// WHY, when the tool already saves a .asset. A .asset mesh is a Unity-internal serialisation: it
// cannot be opened in Blender, Maya, ZBrush or Substance, it cannot be handed to anyone who is not
// in this project, and it does not survive leaving it. The competitor writes .fbx and that is why
// its output is usable as an asset rather than as a preview. This closes that gap.
//
// WHY ASCII, and why hand-written. Unity ships no FBX exporter in the editor API; com.unity.formats
// .fbx is a separate package wrapping the FBX SDK, and taking that dependency to write one static
// mesh is a poor trade - it is a large native plugin, it pins its own package versions, and it is
// not present in this project. The ASCII form of FBX 7.4 is a plain text tree, the subset a static
// mesh needs is small, and every DCC reads it. The cost is this file; the benefit is no dependency.
//
// WHY THE QUADS SURVIVE, which is the entire point of exporting at all. FBX stores polygons as a
// flat index run where the LAST index of each polygon is bitwise-negated (i XOR -1, i.e. -i-1).
// A quad is four indices with the fourth negated, so a quad mesh round-trips as quads - unlike
// nearly every other route out of Unity, which triangulates on the way and destroys the thing that
// was built. N-gons survive by the same rule, at whatever size.
//
// KNOWN LIMIT: THIS WRITES ASCII, AND BLENDER CANNOT READ IT. Blender's importer refuses ASCII FBX
// outright - "ASCII FBX files are not supported" - so the one application most likely to be used to
// inspect a retopology cannot open ours, while the competitor's binary FBX opens everywhere. Round
// tripping through Unity works (FbxRoundTrip covers it) and that is what the tests check, so this
// was invisible until someone tried to open the file somewhere else.
//
// The fix is a binary writer, which is a real piece of work: binary FBX is a length-prefixed node
// tree with typed, optionally zlib-deflated arrays and a footer whose contents are undocumented.
// Until then Benchmarks/reference/fbx2obj.py reads what we emit, so at least the delivered artefact
// can still be measured rather than a regenerated stand-in.
//
// THE TWO CONVENTIONS THAT GO WRONG, both settled by measurement in `FbxRoundTrip` rather than by
// reading documentation:
//
//   HANDEDNESS. FBX is right-handed, Unity is left-handed, and the importer negates X to convert,
//   so the writer negates X on the way out and the positions round-trip exactly. Negating one axis
//   also reverses which way a face appears to wind, and the importer does NOT compensate: written
//   in source order, the mesh comes back with every normal inverted. Measured rather than assumed -
//   the round trip reported matching positions and a signed volume of the opposite sign, which is
//   what inside-out looks like in one number. So the corner order is reversed here, and the two
//   reversals cancel.
//
//   UNITS. `UnitScaleFactor` declares centimetres PER UNIT, so 100 says "these coordinates are in
//   metres" - which is what a Unity unit is - and the positions are then written unchanged. Scaling
//   them by 100 as well, the intuitive reading that the file ought to be in centimetres, imports at
//   a hundred times size; that is what the round trip reported before this was corrected. The
//   competitor's own output has the opposite error and arrives a hundred times too small.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace NativeRemesher
{
    public static class FbxWriter
    {
        /// <summary>
        /// Centimetres per unit, as FBX means it: 100 declares the coordinates to be in metres,
        /// which is what a Unity unit is. Positions are written unchanged; this only tells a
        /// reader how to interpret them.
        /// </summary>
        public const float defaultUnitScale = 100f;

        /// <summary>Round-trips a 32-bit float exactly.</summary>
        const string k_Float = "G9";

        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Right-handed FBX from left-handed Unity. One axis negated; the importer negates the same
        /// one coming back.
        /// </summary>
        public static Vector3 ToFbxSpace(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        /// <summary>
        /// Writes positions and polygons of any size as a single static mesh.
        /// </summary>
        /// <param name="faces">
        /// One array per polygon. Quads stay quads and n-gons stay n-gons; nothing is triangulated.
        /// </param>
        /// <param name="normals">Per-vertex normals, or null to let the importer compute them.</param>
        /// <param name="faceUvs">
        /// Per-face-corner UVs parallel to <paramref name="faces"/>, or null for none. Written
        /// ByPolygonVertex, so a UV seam needs no duplicated vertex and the positions stay welded.
        /// </param>
        public static void Write(string path, IList<Vector3> positions, IList<int[]> faces,
            IList<Vector3> normals = null, string objectName = "mesh",
            float unitScale = defaultUnitScale, IList<Vector2[]> faceUvs = null)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (faces == null) throw new ArgumentNullException(nameof(faces));
            if (normals != null && normals.Count != positions.Count)
                throw new ArgumentException("One normal per position, or none.", nameof(normals));
            if (!(unitScale > 0f))
                throw new ArgumentOutOfRangeException(nameof(unitScale), "Unit scale must be positive.");

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Ids are arbitrary but must be unique and stable within the file.
            const long geometryId = 1000000L;
            const long modelId = 2000000L;

            var text = new StringBuilder(positions.Count * 48 + faces.Count * 24 + 2048);

            Header(text);
            GlobalSettings(text, unitScale);
            Definitions(text);

            text.AppendLine("Objects:  {");
            if (faceUvs != null && faceUvs.Count != faces.Count)
                throw new ArgumentException("One UV array per face, or none.", nameof(faceUvs));

            Geometry(text, geometryId, objectName, positions, faces, normals, faceUvs);
            Model(text, modelId, objectName);
            text.AppendLine("}");

            // Geometry hangs off the model, the model off the scene root (0).
            text.AppendLine("Connections:  {");
            text.AppendLine(string.Format(k_Inv, "\tC: \"OO\",{0},{1}", geometryId, modelId));
            text.AppendLine(string.Format(k_Inv, "\tC: \"OO\",{0},0", modelId));
            text.AppendLine("}");

            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Convenience overload for a <see cref="Mesh"/> whose topology is already quads.</summary>
        public static void Write(string path, Mesh mesh, string objectName = null,
            float unitScale = defaultUnitScale)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            var positions = mesh.vertices;
            var normals = mesh.normals;
            var meshUvs = mesh.uv;
            var hasUvs = meshUvs != null && meshUvs.Length == positions.Length;
            var faces = new List<int[]>();
            var faceUvs = hasUvs ? new List<Vector2[]>() : null;

            for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                var indices = mesh.GetIndices(submesh);
                var size = mesh.GetTopology(submesh) == MeshTopology.Quads ? 4 : 3;
                for (var i = 0; i + size <= indices.Length; i += size)
                {
                    var face = new int[size];
                    Array.Copy(indices, i, face, 0, size);
                    faces.Add(face);
                    if (hasUvs)
                    {
                        var uv = new Vector2[size];
                        for (var k = 0; k < size; k++) uv[k] = meshUvs[face[k]];
                        faceUvs.Add(uv);
                    }
                }
            }

            Write(path, positions, faces, normals != null && normals.Length == positions.Length
                ? normals
                : null, objectName ?? mesh.name, unitScale, faceUvs);
        }

        static void Header(StringBuilder text)
        {
            text.AppendLine("; FBX 7.4.0 project file");
            text.AppendLine("; Written by NativeRemesher");
            text.AppendLine();
            text.AppendLine("FBXHeaderExtension:  {");
            text.AppendLine("\tFBXHeaderVersion: 1003");
            text.AppendLine("\tFBXVersion: 7400");
            text.AppendLine("\tCreator: \"NativeRemesher\"");
            text.AppendLine("}");
        }

        static void GlobalSettings(StringBuilder text, float unitScale)
        {
            text.AppendLine("GlobalSettings:  {");
            text.AppendLine("\tVersion: 1000");
            text.AppendLine("\tProperties70:  {");
            // Y up, Z front, X right: the axis system Unity's importer expects to convert from.
            text.AppendLine("\t\tP: \"UpAxis\", \"int\", \"Integer\", \"\",1");
            text.AppendLine("\t\tP: \"UpAxisSign\", \"int\", \"Integer\", \"\",1");
            text.AppendLine("\t\tP: \"FrontAxis\", \"int\", \"Integer\", \"\",2");
            text.AppendLine("\t\tP: \"FrontAxisSign\", \"int\", \"Integer\", \"\",1");
            text.AppendLine("\t\tP: \"CoordAxis\", \"int\", \"Integer\", \"\",0");
            text.AppendLine("\t\tP: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1");
            text.AppendLine(string.Format(k_Inv,
                "\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\",{0}", unitScale));
            text.AppendLine(string.Format(k_Inv,
                "\t\tP: \"OriginalUnitScaleFactor\", \"double\", \"Number\", \"\",{0}", unitScale));
            text.AppendLine("\t}");
            text.AppendLine("}");
        }

        static void Definitions(StringBuilder text)
        {
            text.AppendLine("Definitions:  {");
            text.AppendLine("\tVersion: 100");
            text.AppendLine("\tCount: 3");
            text.AppendLine("\tObjectType: \"GlobalSettings\" {");
            text.AppendLine("\t\tCount: 1");
            text.AppendLine("\t}");
            text.AppendLine("\tObjectType: \"Geometry\" {");
            text.AppendLine("\t\tCount: 1");
            text.AppendLine("\t}");
            text.AppendLine("\tObjectType: \"Model\" {");
            text.AppendLine("\t\tCount: 1");
            text.AppendLine("\t}");
            text.AppendLine("}");
        }

        static void Geometry(StringBuilder text, long id, string name, IList<Vector3> positions,
            IList<int[]> faces, IList<Vector3> normals, IList<Vector2[]> faceUvs = null)
        {
            text.AppendLine(string.Format(k_Inv,
                "\tGeometry: {0}, \"Geometry::{1}\", \"Mesh\" {{", id, name));

            text.AppendLine(string.Format(k_Inv, "\t\tVertices: *{0} {{", positions.Count * 3));
            text.Append("\t\t\ta: ");
            for (var i = 0; i < positions.Count; i++)
            {
                var p = ToFbxSpace(positions[i]);
                if (i > 0) text.Append(',');
                text.Append(p.x.ToString(k_Float, k_Inv)).Append(',')
                    .Append(p.y.ToString(k_Float, k_Inv)).Append(',')
                    .Append(p.z.ToString(k_Float, k_Inv));
                if ((i & 0xFF) == 0xFF) text.AppendLine().Append("\t\t\t");
            }
            text.AppendLine();
            text.AppendLine("\t\t}");

            var corners = 0;
            for (var f = 0; f < faces.Count; f++)
                if (faces[f] != null && faces[f].Length >= 3) corners += faces[f].Length;

            // The last index of every polygon is negated, which is how FBX marks the end of one
            // without storing a size anywhere. This is what keeps a quad a quad.
            text.AppendLine(string.Format(k_Inv, "\t\tPolygonVertexIndex: *{0} {{", corners));
            text.Append("\t\t\ta: ");
            var written = 0;
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;

                // Reversed, so that this cancels the winding flip negating X introduces.
                for (var k = 0; k < face.Length; k++)
                {
                    var corner = face[face.Length - 1 - k];
                    if (written > 0) text.Append(',');
                    text.Append(k == face.Length - 1 ? (-corner - 1) : corner);
                    written++;
                    if ((written & 0xFF) == 0) text.AppendLine().Append("\t\t\t");
                }
            }
            text.AppendLine();
            text.AppendLine("\t\t}");

            text.AppendLine("\t\tGeometryVersion: 124");

            if (normals != null)
            {
                text.AppendLine("\t\tLayerElementNormal: 0 {");
                text.AppendLine("\t\t\tVersion: 101");
                text.AppendLine("\t\t\tName: \"\"");
                text.AppendLine("\t\t\tMappingInformationType: \"ByVertice\"");
                text.AppendLine("\t\t\tReferenceInformationType: \"Direct\"");
                text.AppendLine(string.Format(k_Inv, "\t\t\tNormals: *{0} {{", normals.Count * 3));
                text.Append("\t\t\t\ta: ");
                for (var i = 0; i < normals.Count; i++)
                {
                    var n = ToFbxSpace(normals[i]);
                    if (i > 0) text.Append(',');
                    text.Append(n.x.ToString(k_Float, k_Inv)).Append(',')
                        .Append(n.y.ToString(k_Float, k_Inv)).Append(',')
                        .Append(n.z.ToString(k_Float, k_Inv));
                    if ((i & 0xFF) == 0xFF) text.AppendLine().Append("\t\t\t\t");
                }
                text.AppendLine();
                text.AppendLine("\t\t\t}");
                text.AppendLine("\t\t}");
            }

            var hasUvs = faceUvs != null && WriteUvs(text, faces, faceUvs, corners);

            text.AppendLine("\t\tLayer: 0 {");
            text.AppendLine("\t\t\tVersion: 100");
            if (hasUvs)
            {
                text.AppendLine("\t\t\tLayerElement:  {");
                text.AppendLine("\t\t\t\tType: \"LayerElementUV\"");
                text.AppendLine("\t\t\t\tTypedIndex: 0");
                text.AppendLine("\t\t\t}");
            }
            if (normals != null)
            {
                text.AppendLine("\t\t\tLayerElement:  {");
                text.AppendLine("\t\t\t\tType: \"LayerElementNormal\"");
                text.AppendLine("\t\t\t\tTypedIndex: 0");
                text.AppendLine("\t\t\t}");
            }
            text.AppendLine("\t\t}");
            text.AppendLine("\t}");
        }

        /// <summary>
        /// The UV layer, ByPolygonVertex with an index into a deduplicated UV table. Corners are
        /// written in the same REVERSED order as PolygonVertexIndex, or every UV would land on the
        /// wrong corner of its face. UVs are not mirrored: negating X flips positions, not texture
        /// space, and Unity's importer reads V the way Unity stores it.
        /// </summary>
        static bool WriteUvs(StringBuilder text, IList<int[]> faces, IList<Vector2[]> faceUvs,
            int corners)
        {
            var table = new List<Vector2>();
            var lookup = new Dictionary<Vector2, int>();
            var indices = new List<int>(corners);

            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;
                var uv = faceUvs[f];
                for (var k = 0; k < face.Length; k++)
                {
                    var corner = face.Length - 1 - k;
                    var value = uv != null && uv.Length == face.Length ? uv[corner] : Vector2.zero;
                    if (!lookup.TryGetValue(value, out var index))
                    {
                        index = table.Count;
                        lookup.Add(value, index);
                        table.Add(value);
                    }
                    indices.Add(index);
                }
            }

            if (indices.Count != corners) return false;

            text.AppendLine("\t\tLayerElementUV: 0 {");
            text.AppendLine("\t\t\tVersion: 101");
            text.AppendLine("\t\t\tName: \"UVMap\"");
            text.AppendLine("\t\t\tMappingInformationType: \"ByPolygonVertex\"");
            text.AppendLine("\t\t\tReferenceInformationType: \"IndexToDirect\"");
            text.AppendLine(string.Format(k_Inv, "\t\t\tUV: *{0} {{", table.Count * 2));
            text.Append("\t\t\t\ta: ");
            for (var i = 0; i < table.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(table[i].x.ToString(k_Float, k_Inv)).Append(',')
                    .Append(table[i].y.ToString(k_Float, k_Inv));
                if ((i & 0xFF) == 0xFF) text.AppendLine().Append("\t\t\t\t");
            }
            text.AppendLine();
            text.AppendLine("\t\t\t}");
            text.AppendLine(string.Format(k_Inv, "\t\t\tUVIndex: *{0} {{", indices.Count));
            text.Append("\t\t\t\ta: ");
            for (var i = 0; i < indices.Count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(indices[i]);
                if ((i & 0xFF) == 0xFF) text.AppendLine().Append("\t\t\t\t");
            }
            text.AppendLine();
            text.AppendLine("\t\t\t}");
            text.AppendLine("\t\t}");
            return true;
        }

        static void Model(StringBuilder text, long id, string name)
        {
            text.AppendLine(string.Format(k_Inv,
                "\tModel: {0}, \"Model::{1}\", \"Mesh\" {{", id, name));
            text.AppendLine("\t\tVersion: 232");
            text.AppendLine("\t\tProperties70:  {");
            text.AppendLine("\t\t\tP: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",1,1,1");
            text.AppendLine("\t\t}");
            text.AppendLine("\t\tShading: T");
            text.AppendLine("\t\tCulling: \"CullingOff\"");
            text.AppendLine("\t}");
        }
    }
}
