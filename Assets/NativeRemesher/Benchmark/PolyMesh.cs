// A polygon mesh with faces of arbitrary size, plus the adapters that get one out of the things
// the benchmark needs to score.
//
// Three sources have to be measured with the *identical* metric code or the numbers are not
// comparable: our own QuadExtractionResult (quads and n-gons), a Unity Mesh imported from the
// reference oracle (quads, or triangles once the FBX importer has had its way), and the raw input
// triangle mesh. This type is the common denominator.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public sealed class PolyMesh
    {
        public Vector3[] positions;
        public List<int[]> faces = new List<int[]>();

        public int faceCount => faces.Count;
        public int vertexCount => positions?.Length ?? 0;

        public static PolyMesh FromExtraction(QuadExtractionResult extraction)
        {
            if (extraction == null)
                throw new ArgumentNullException(nameof(extraction));

            return new PolyMesh
            {
                positions = extraction.positions,
                // Share the list: the caller owns the extraction for the duration of scoring, and
                // copying a few thousand int[] per benchmark row is pure waste.
                faces = extraction.faces
            };
        }

        /// <summary>
        /// Reads a Unity mesh, preserving quad topology when the submesh actually carries it.
        /// FBX round-tripping usually triangulates, so a quad-count of zero here is a property of
        /// the file, not of the remesher that produced it — the oracle rows are scored on what
        /// Unity can see, and that caveat belongs in the report rather than in a silent fudge.
        /// </summary>
        public static PolyMesh FromMesh(Mesh mesh)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            var result = new PolyMesh { positions = mesh.vertices };

            for (var sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var topology = mesh.GetTopology(sub);
                var indices = mesh.GetIndices(sub);

                switch (topology)
                {
                    case MeshTopology.Quads:
                        for (var i = 0; i + 3 < indices.Length; i += 4)
                            result.faces.Add(new[] { indices[i], indices[i + 1], indices[i + 2], indices[i + 3] });
                        break;

                    case MeshTopology.Triangles:
                        for (var i = 0; i + 2 < indices.Length; i += 3)
                            result.faces.Add(new[] { indices[i], indices[i + 1], indices[i + 2] });
                        break;

                    default:
                        // Lines, points, strips: not a surface, nothing to score.
                        break;
                }
            }

            return result;
        }

        public static PolyMesh FromTopology(RemeshTopology topology)
        {
            if (topology == null)
                throw new ArgumentNullException(nameof(topology));

            var result = new PolyMesh { positions = topology.positions };
            for (var i = 0; i + 2 < topology.triangles.Length; i += 3)
                result.faces.Add(new[] { topology.triangles[i], topology.triangles[i + 1], topology.triangles[i + 2] });

            return result;
        }

        /// <summary>
        /// Fan-triangulates every face, for the geometric queries that need a triangle soup.
        /// Fanning is adequate here because the faces being measured are small and near-convex;
        /// a badly non-convex n-gon would distort its own surface-distance contribution slightly,
        /// and the planarity metric is what catches that case.
        /// </summary>
        public int[] Triangulate()
        {
            var triangles = new List<int>(faces.Count * 6);
            foreach (var face in faces)
            {
                for (var k = 1; k + 1 < face.Length; k++)
                {
                    triangles.Add(face[0]);
                    triangles.Add(face[k]);
                    triangles.Add(face[k + 1]);
                }
            }
            return triangles.ToArray();
        }
    }
}
