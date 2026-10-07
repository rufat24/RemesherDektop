// Vertex normals computed from the output geometry, which is what shading has to follow.
//
// This replaces carrying the input surface's normals through to the mesh, and the reasoning behind
// that carrying was wrong. The old comment on QuadExtractionResult.normals said they were "strictly
// better for shading than Mesh.RecalculateNormals, which can only see the coarse output geometry".
// The second half of that sentence is the requirement, not the objection: a shading normal that
// disagrees with the surface it is attached to is not a more detailed surface, it is a lit surface
// whose lighting does not match its own silhouette.
//
// Concretely, at 1.5M triangles down to 5.5k faces the input normal sampled at a cluster
// representative describes a bump that no longer exists — the decimator and the lattice both removed
// it. Attaching it to the coarse facet tilts that facet's lighting by whatever the bump's slope was,
// which is arbitrary with respect to its neighbours. Adjacent facets get independently arbitrary
// tilts, and the result is the blotching in the report: a surface that reads as dented everywhere,
// with the dents not corresponding to anything in the shape.
//
// Every DCC tool does it this way round. Blender has no mechanism for a vertex normal that came from
// somewhere other than the faces around it, short of an explicit custom-normal layer, and the reason
// Shift+N produces good shading is that it fixes the FACES and lets the normals follow.
//
// Weighting. Faces contribute in proportion to the interior angle they occupy at the vertex, not
// their area (Thurmer and Wuthrich 1998). Angle weighting is invariant to how a neighbourhood is
// tessellated, so a vertex sitting where one large quad meets three small ones gets the normal the
// surface implies rather than the normal of the large quad. Area weighting would also be defensible
// but it is exactly the choice that makes a hub surrounded by pie slices take the lid's normal.
//
// Unity's own Mesh.RecalculateNormals is not an option here regardless of the argument above: it
// reads the index buffer as triangles, and submesh 0 of this output is MeshTopology.Quads.

using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public static class NormalRecalculator
    {
        /// <summary>
        /// Angle-weighted vertex normals over <paramref name="faces"/>. Vertices no face reaches
        /// fall back to <paramref name="fallback"/> when it is supplied, so an isolated vertex keeps
        /// a usable normal instead of a zero that shades black.
        /// </summary>
        public static Vector3[] Compute(List<int[]> faces, Vector3[] positions, Vector3[] fallback)
        {
            if (positions == null) return null;

            var normals = new Vector3[positions.Length];
            if (faces == null) return normals;

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;

                // One normal for the whole face, by Newell, so a non-planar quad contributes its
                // best-fit plane rather than one of the two triangulations of it.
                var faceNormal = MeshMath.SafeNormalize(
                    QuadExtractor.FaceNormal(positions, face), Vector3.zero);

                if (faceNormal == Vector3.zero) continue;

                for (var i = 0; i < face.Length; i++)
                {
                    var v = face[i];
                    if (v < 0 || v >= positions.Length) continue;

                    var previous = positions[face[(i + face.Length - 1) % face.Length]];
                    var next = positions[face[(i + 1) % face.Length]];
                    var here = positions[v];

                    var a = MeshMath.SafeNormalize(previous - here, Vector3.zero);
                    var b = MeshMath.SafeNormalize(next - here, Vector3.zero);
                    if (a == Vector3.zero || b == Vector3.zero) continue;

                    // Acos of the clamped dot: the interior angle at this corner. Clamping matters
                    // because a dot of 1+1e-7 from rounding makes Acos return NaN, and one NaN
                    // poisons the vertex normal and everything that interpolates from it.
                    var cos = Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f);
                    normals[v] += faceNormal * Mathf.Acos(cos);
                }
            }

            var haveFallback = fallback != null && fallback.Length == positions.Length;

            for (var i = 0; i < normals.Length; i++)
            {
                var candidate = haveFallback ? fallback[i] : Vector3.up;
                normals[i] = MeshMath.SafeNormalize(normals[i], candidate);
            }

            return normals;
        }
    }
}
