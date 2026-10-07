// Pass G: merge adjacent triangle pairs back into quads.
//
// Even with the tracer preferring quads, some triangles survive — genuinely, around singularities,
// where a quad-only tiling is impossible. But many others are pairs that happen to sit either side of
// an edge the lattice was ambivalent about, and those two triangles are one perfectly good quad.
//
// This is the cheapest quad-fraction win available, and unlike the passes before it, it cannot make
// the mesh invalid: replacing two triangles that share an edge with the quad spanning them removes
// exactly one interior edge and changes nothing else about the topology.
//
// It is greedy by quality rather than exhaustive. Maximum quad recovery is a matching problem, and
// the optimal matching is not worth solving here: the marginal gain over greedy is small, and a bad
// merge (a sliver, or a non-convex quad) costs more in output quality than a missed merge does.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class RegularizeStats
    {
        public int candidatePairs;
        public int merged;
        public int rejectedNonConvex;
        public int rejectedQuality;
        public int trianglesRemaining;

        public override string ToString()
        {
            return $"candidates={candidatePairs} merged={merged} " +
                   $"rejNonConvex={rejectedNonConvex} rejQuality={rejectedQuality} " +
                   $"trisLeft={trianglesRemaining}";
        }
    }

    public static class QuadRegularizer
    {
        /// <summary>
        /// Minimum score for a merge to be accepted. A perfect square scores 1. The default admits
        /// noticeably skewed quads, because a skewed quad still reads as quad topology and can be
        /// relaxed later, whereas a leftover triangle pair cannot.
        /// </summary>
        /// <remarks>
        /// LOWERED from 0.15 to 0.03 on measurement. At 0.15 this pass refused 256 of 595 candidate
        /// merges on quality and took only 199, which looked like the right caution: a merge that
        /// produces a badly shaped quad is a real cost.
        ///
        /// The competitor comparison showed the caution was backwards. The visible defect in our
        /// output is needle faces, and 88 % of ours touch a leftover non-quad against QuadRemesher's
        /// 2 % - so a surviving triangle costs far more than a mediocre quad does. Relaxing to 0.03
        /// takes 347 merges instead of 199 and, decisively, the minimum-angle percentiles went UP
        /// rather than down (p01 3.6 -> 4.2, p05 20.4 -> 21.7) with non-manifold edges still zero.
        /// The relaxation removes bad triangles; it does not manufacture bad quads.
        /// </remarks>
        public const float defaultQualityThreshold = 0.03f;

        struct Candidate
        {
            public int faceA;
            public int faceB;
            public int[] quad;
            public float score;
        }

        public static RegularizeStats MergeTrianglePairs(FrozenQuadGraph graph, List<int[]> faces,
            float qualityThreshold = defaultQualityThreshold)
        {
            if (faces == null) throw new ArgumentNullException(nameof(faces));

            var stats = new RegularizeStats();

            // Map every triangle edge to the triangles carrying it. Only triangles participate:
            // merging a triangle into a quad would make a pentagon, which is not the goal.
            var edgeToFaces = new Dictionary<long, List<int>>(faces.Count * 2, EdgeKey.Comparer);

            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face.Length != 3) continue;

                for (var i = 0; i < 3; i++)
                {
                    var key = EdgeKey.PackUndirected(face[i], face[(i + 1) % 3]);
                    if (!edgeToFaces.TryGetValue(key, out var list))
                        edgeToFaces[key] = list = new List<int>(2);
                    list.Add(f);
                }
            }

            var candidates = new List<Candidate>();

            foreach (var kv in edgeToFaces)
            {
                // Exactly two triangles: one is a boundary, three or more is non-manifold and this
                // pass is not the place to resolve it.
                if (kv.Value.Count != 2) continue;

                var fa = kv.Value[0];
                var fb = kv.Value[1];

                var u = (int)(kv.Key >> 32);
                var v = (int)(kv.Key & 0xFFFFFFFF);

                var quad = BuildQuad(faces[fa], faces[fb], u, v);
                if (quad == null) continue;

                stats.candidatePairs++;

                var score = Score(graph.positions, quad, out var convex);
                if (!convex)
                {
                    stats.rejectedNonConvex++;
                    continue;
                }
                if (score < qualityThreshold)
                {
                    stats.rejectedQuality++;
                    continue;
                }

                candidates.Add(new Candidate { faceA = fa, faceB = fb, quad = quad, score = score });
            }

            // Best first, so a triangle with two possible partners pairs with the better one.
            candidates.Sort((x, y) => y.score.CompareTo(x.score));

            var consumed = new bool[faces.Count];
            var replacement = new int[faces.Count];
            for (var i = 0; i < replacement.Length; i++) replacement[i] = -1;

            var quads = new List<int[]>(candidates.Count);

            foreach (var candidate in candidates)
            {
                if (consumed[candidate.faceA] || consumed[candidate.faceB]) continue;

                consumed[candidate.faceA] = true;
                consumed[candidate.faceB] = true;
                replacement[candidate.faceA] = quads.Count;
                quads.Add(candidate.quad);

                stats.merged++;
            }

            // Rebuild in place, preserving the original face order where possible so that output is
            // stable run to run.
            var rebuilt = new List<int[]>(faces.Count);
            for (var f = 0; f < faces.Count; f++)
            {
                if (replacement[f] >= 0) rebuilt.Add(quads[replacement[f]]);
                else if (!consumed[f]) rebuilt.Add(faces[f]);
            }

            faces.Clear();
            faces.AddRange(rebuilt);

            foreach (var face in faces)
                if (face.Length == 3) stats.trianglesRemaining++;

            return stats;
        }

        /// <summary>
        /// The quad spanning two triangles that share edge (u, v), or null if they do not form one.
        /// </summary>
        /// <remarks>
        /// Winding is preserved by walking the union's boundary rather than by concatenating corners.
        /// With consistent manifold winding the shared edge is traversed u-&gt;v in one triangle and
        /// v-&gt;u in the other, so the boundary is p-&gt;u-&gt;r-&gt;v where p and r are the two
        /// opposite corners. Getting this wrong produces a bowtie that still passes a corner-count
        /// check.
        /// </remarks>
        static int[] BuildQuad(int[] triA, int[] triB, int u, int v)
        {
            if (triA.Length != 3 || triB.Length != 3) return null;

            var p = Opposite(triA, u, v);
            var r = Opposite(triB, u, v);
            if (p < 0 || r < 0 || p == r) return null;

            // Orient so that triA traverses u -> v; if it traverses v -> u instead, swap the roles
            // of u and v so the boundary walk below comes out consistent with triA's winding.
            if (!HasDirectedEdge(triA, u, v))
            {
                if (!HasDirectedEdge(triA, v, u)) return null;
                var t = u; u = v; v = t;
            }

            return new[] { p, u, r, v };
        }

        static int Opposite(int[] tri, int u, int v)
        {
            for (var i = 0; i < 3; i++)
                if (tri[i] != u && tri[i] != v) return tri[i];
            return -1;
        }

        static bool HasDirectedEdge(int[] face, int from, int to)
        {
            for (var i = 0; i < face.Length; i++)
                if (face[i] == from && face[(i + 1) % face.Length] == to) return true;
            return false;
        }

        /// <summary>
        /// Quality of a candidate quad in [0, 1], where 1 is a planar square. Also reports whether
        /// the quad is convex — a non-convex result is always rejected, because the two triangles it
        /// came from tile the region correctly and the quad does not.
        /// </summary>
        static float Score(Vector3[] positions, int[] quad, out bool convex)
        {
            convex = true;

            var p = new Vector3[4];
            for (var i = 0; i < 4; i++) p[i] = positions[quad[i]];

            var normal = Vector3.zero;
            for (var i = 0; i < 4; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % 4];
                normal += new Vector3(
                    (a.y - b.y) * (a.z + b.z),
                    (a.z - b.z) * (a.x + b.x),
                    (a.x - b.x) * (a.y + b.y));
            }

            if (normal.sqrMagnitude <= 1e-20f) { convex = false; return 0f; }
            // Newell sum again: see MeshMath. A zero normal here makes every candidate read as
            // degenerate and silently disables triangle-pair merging.
            normal = MeshMath.SafeNormalize(normal, Vector3.up);

            var minAngle = 180f;
            var maxAngle = 0f;
            var perimeter = 0f;
            var shortest = float.MaxValue;
            var longest = 0f;

            for (var i = 0; i < 4; i++)
            {
                var prev = p[(i + 3) % 4];
                var cur = p[i];
                var next = p[(i + 1) % 4];

                var e0 = prev - cur;
                var e1 = next - cur;
                if (e0.sqrMagnitude <= 1e-20f || e1.sqrMagnitude <= 1e-20f) { convex = false; return 0f; }

                var angle = Vector3.Angle(e0, e1);

                // Convexity: every corner must turn the same way about the face normal.
                if (Vector3.Dot(Vector3.Cross(e1, e0), normal) < 0f)
                    convex = false;

                minAngle = Mathf.Min(minAngle, angle);
                maxAngle = Mathf.Max(maxAngle, angle);

                var edge = (next - cur).magnitude;
                perimeter += edge;
                shortest = Mathf.Min(shortest, edge);
                longest = Mathf.Max(longest, edge);
            }

            if (!convex) return 0f;

            // Three independent ways a quad can be bad, multiplied so any one of them can veto.
            var angleQuality = Mathf.Clamp01(minAngle / 90f) * Mathf.Clamp01((180f - maxAngle) / 90f);
            var aspectQuality = longest > 1e-20f ? Mathf.Clamp01(shortest / longest) : 0f;

            var meanEdge = perimeter * 0.25f;
            var centroid = (p[0] + p[1] + p[2] + p[3]) * 0.25f;
            var deviation = 0f;
            for (var i = 0; i < 4; i++)
                deviation = Mathf.Max(deviation, Mathf.Abs(Vector3.Dot(normal, p[i] - centroid)));

            var planarQuality = meanEdge > 1e-20f ? 1f / (1f + 4f * deviation / meanEdge) : 0f;

            return angleQuality * aspectQuality * planarQuality;
        }
    }
}
