// Turns every remaining n-gon into quads.
//
// The last stage before output, and the fix for the most visible defect in the result. Faces with
// more than four corners survive extraction from two sources — the tracer's degree-5 to degree-8
// passes, and the hole filler's caps — and QuadExtractor.ToMesh fan-triangulates all of them for
// display, because a Unity mesh has no n-gon topology to emit them into.
//
// A fan is what the wireframe shows: long thin triangles radiating from one corner of the polygon.
// On a 26-corner cap that is 24 slivers meeting at a point. The user's reference retopology of the
// same asset has no n-gon at all — its face histogram is {3: 389, 4: 5085} — and this is what
// closes that difference.
//
// The construction is the same one HoleFiller uses for a cap: one interior vertex at the centroid,
// then adjacent rim edges paired into quads, hub-v[i]-v[i+1]-v[i+2]. It needs no new rim vertices,
// which matters because a rim edge is shared with a neighbouring face that would otherwise have to
// be edited too. An even rim yields exactly length/2 quads; an odd rim leaves one triangle.
//
// Why this is a good trade at the sizes that actually occur. The hub takes valence length/2, which
// for the 5-to-8 corner faces the tracer produces is valence 3 or 4 — ordinary quad-mesh
// singularities, not defects. It is only a large n-gon that would leave a bad hub, and by the time
// this runs the hole filler has already quadrangulated the large caps it could.
//
// The cost is face count: an n-gon becomes about n/2 faces, so the total grows. That is accounted
// for by the spacing search in RemeshPipeline, which measures the count this stage produces rather
// than the count before it.

using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class NgonSplitStats
    {
        public int ngonsSplit;
        public int quadsProduced;
        public int trianglesProduced;
        public int verticesAdded;
        public int largestSplit;

        /// <summary>N-gons left alone because they were degenerate. Should be zero.</summary>
        public int refused;

        /// <summary>N-gons left alone because their rim visits a vertex twice — splitting one of
        /// those makes the mesh non-manifold. See the guard in Run.</summary>
        public int refusedNonSimple;

        public override string ToString()
        {
            return $"split={ngonsSplit} (+{quadsProduced}quads +{trianglesProduced}tris) " +
                   $"added={verticesAdded} largest={largestSplit} " +
                   $"refused={refused} nonSimple={refusedNonSimple}";
        }
    }

    public static class NgonQuadrangulator
    {
        /// <summary>
        /// Replaces every face with more than four corners by quads around a new centroid vertex.
        /// Appends the new vertices to <paramref name="graph"/>'s position and normal arrays.
        /// </summary>
        public static NgonSplitStats Run(FrozenQuadGraph graph, List<int[]> faces)
        {
            var stats = new NgonSplitStats();
            if (graph == null || faces == null) return stats;

            // Count first, so the arrays grow once rather than per face.
            var ngons = 0;
            foreach (var face in faces)
                if (face != null && face.Length > 4) ngons++;

            if (ngons == 0) return stats;

            var oldCount = graph.positions.Length;
            var positions = graph.positions;
            var normals = graph.normals;
            System.Array.Resize(ref positions, oldCount + ngons);
            System.Array.Resize(ref normals, oldCount + ngons);

            var next = oldCount;
            var replacements = new List<int[]>(ngons * 4);

            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null || face.Length <= 4) continue;

                var length = face.Length;

                // A rim that visits the same vertex twice must be left alone. As one n-gon face
                // every edge still appears once and the mesh stays manifold; split around a hub,
                // the edge from the hub to the repeated vertex lands in four faces instead of two.
                // Measured: without this check the output gained 2 non-manifold edges where the
                // reference has none, which is a correctness regression and not a trade.
                var repeated = false;
                for (var i = 0; i < length && !repeated; i++)
                    for (var j = i + 1; j < length; j++)
                        if (face[i] == face[j]) { repeated = true; break; }

                if (repeated)
                {
                    stats.refusedNonSimple++;
                    continue;
                }

                var centroid = Vector3.zero;
                var normal = Vector3.zero;
                var counted = 0;
                for (var i = 0; i < length; i++)
                {
                    var v = face[i];
                    if (v < 0 || v >= oldCount) continue;
                    centroid += positions[v];
                    normal += normals[v];
                    counted++;
                }

                if (counted == 0)
                {
                    stats.refused++;
                    continue;
                }

                var hub = next++;
                positions[hub] = centroid / counted;
                // The hub's normal follows the rim, not the polygon's own plane: the rim describes
                // the surface this face stands in for, which is what shading should follow.
                normals[hub] = MeshMath.SafeNormalize(normal, Vector3.up);

                // The first quad replaces the original face in place, so no index shifting is
                // needed and face order stays stable for anything that reads it afterwards.
                var wroteFirst = false;
                var i0 = 0;

                while (i0 + 2 <= length)
                {
                    var a = face[i0];
                    var b = face[(i0 + 1) % length];
                    var c = face[(i0 + 2) % length];
                    i0 += 2;

                    if (a == b || b == c || a == c) continue;

                    var quad = new[] { hub, a, b, c };
                    if (!wroteFirst)
                    {
                        faces[f] = quad;
                        wroteFirst = true;
                    }
                    else
                    {
                        replacements.Add(quad);
                    }

                    stats.quadsProduced++;
                }

                // An odd rim leaves one edge, which can only be a triangle without splitting a rim
                // edge shared with a neighbouring face.
                if (i0 < length)
                {
                    var a = face[i0];
                    var b = face[(i0 + 1) % length];
                    if (a != b)
                    {
                        var tri = new[] { hub, a, b };
                        if (!wroteFirst)
                        {
                            faces[f] = tri;
                            wroteFirst = true;
                        }
                        else
                        {
                            replacements.Add(tri);
                        }

                        stats.trianglesProduced++;
                    }
                }

                if (!wroteFirst)
                {
                    // Every corner pair was degenerate. Leave the original face; dropping it would
                    // open a hole, which is the worse defect.
                    stats.refused++;
                    next--;
                    continue;
                }

                stats.ngonsSplit++;
                stats.largestSplit = Mathf.Max(stats.largestSplit, length);
            }

            stats.verticesAdded = next - oldCount;

            // Trim any hub slots the refusals did not use, so vertexCount stays truthful.
            if (next < positions.Length)
            {
                System.Array.Resize(ref positions, next);
                System.Array.Resize(ref normals, next);
            }

            graph.positions = positions;
            graph.normals = normals;
            faces.AddRange(replacements);

            return stats;
        }
    }
}
