// Makes the output's face winding agree with the input surface, per connected component.
//
// This is the fix for what was reported as holes in the output, and the reason it was reported as
// holes rather than as inverted faces is that an inverted face is backface-culled: from outside you
// see straight through it to the inside of the mesh, while the boundary-edge count stays at zero and
// insists the surface is closed. Both observations were true.
//
// What the measurement showed, on the reference asset at 5,474 faces:
//
//     winding:  paired=11,481  sameDirection=0  |  faceNormals agree=34  disagree=5,939
//
// sameDirection=0 says every shared edge is traversed in opposite directions by its two faces, so
// the winding was already globally CONSISTENT. It was consistently inside-out: 5,939 of 5,973 faces
// pointed away from the surface they were extracted from. The old code decided this per face, in
// QuadExtractor.ShouldFlip, by comparing that one face's geometric normal against its vertices'
// averaged input normals — so it flipped the 5,939 and left the other 34 alone, and those 34 became
// the only faces disagreeing with their neighbours. Thirty-four holes in an otherwise sound mesh.
//
// Why the per-face test cannot work. Newell's normal of a face has the magnitude of twice its area,
// so on a thin sliver — and a centroid-hub pie slice is exactly that — it is both tiny and dominated
// by rounding, and the sign of its dot product with anything is close to a coin flip. There is no
// threshold that fixes this, because the quantity being thresholded carries no information at that
// size.
//
// So the decision belongs to a whole connected component, and this is the same split Blender's
// Shift+N makes:
//
//   1. Consistency, which is combinatorial. Union-find over faces sharing an undirected edge, then
//      a breadth-first walk of each component flipping any face that traverses a shared edge the
//      same direction as the neighbour it was reached from. This is the half of the job the previous
//      version did not do at all — it assumed consistency and only chose a global sign — which left
//      the 34 folds it reported as unfixable.
//
//   2. Outwardness, which is geometric. For a CLOSED component the signed volume settles it with no
//      reference to anything outside the mesh: the divergence theorem gives a positive volume
//      exactly when the winding is outward, and it is dominated by the large faces automatically
//      because a sliver encloses no volume. Blender ray-casts instead, which answers the same
//      question and is more robust on self-intersecting input, but needs an acceleration structure
//      this stage does not have. For an OPEN component volume means nothing, so that case falls
//      back to an area-weighted vote against the input surface's normals.
//
// Per component rather than globally because the extractor can fragment a surface — see the Euler
// characteristic note in MeshMetrics, where closing the holes on a sphere revealed four separate
// shells — and two shells can legitimately need opposite flips.
//
// Orientation is fixed in the FACE LIST, not at mesh-build time, so the OBJ the benchmark scores,
// any FBX exported later, and the Unity mesh all agree. QuadExtractor.ToMesh no longer decides
// winding at all.

using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class OrientStats
    {
        public int components;
        public int componentsFlipped;
        public int facesFlipped;
        public int faceCount;

        /// <summary>Components whose sign came from signed volume rather than a normal vote.</summary>
        public int componentsClosed;

        /// <summary>
        /// Open components whose own vote was too weak to read, so they took the consensus instead.
        /// </summary>
        /// <remarks>
        /// Measured on the reference asset before this existed: GameAsset produced 13 components,
        /// flipped 12, and left a 3-face shell pointing the other way. A shell that small has an
        /// area-weighted mean cosine near zero, so its sign was decided by rounding.
        /// </remarks>
        public int componentsWeakVote;

        /// <summary>Weakest area-weighted mean cosine over the open components, for diagnosis.</summary>
        public float weakestConfidence = 1f;

        /// <summary>Faces reversed by the consistency walk, before any component-wide flip.</summary>
        public int facesMadeConsistent;

        /// <summary>Shared edges still traversed the same way by both faces. Should be zero.</summary>
        public int inconsistentEdges;

        /// <summary>Faces whose own normal still disagrees with the surface after the pass.</summary>
        public int disagreeingAfter;

        /// <summary>Faces too small for their normal to carry a usable sign. Excluded from the vote.</summary>
        public int negligibleArea;

        public override string ToString()
        {
            return $"components={components} (closed={componentsClosed} weak={componentsWeakVote} " +
                   $"minConf={weakestConfidence:F3}) flipped={componentsFlipped} " +
                   $"consistency={facesMadeConsistent} faces={facesFlipped}/{faceCount} " +
                   $"inconsistentEdges={inconsistentEdges} disagreeAfter={disagreeingAfter} " +
                   $"negligible={negligibleArea}";
        }
    }

    public static class FaceOrienter
    {
        /// <summary>
        /// Area-weighted mean cosine below which an open component's own vote is not worth reading.
        /// </summary>
        /// <remarks>
        /// PROVISIONAL, and the measurement says so. The intent is to separate "the normals
        /// disagree with this winding" from "there are not enough normals here to say", and the
        /// first draft of this comment asserted the two populations were orders of magnitude apart.
        /// They are not, on the only asset where it has been checked:
        ///
        ///     Photogrammetry  minConf=0.152      Character  0.627      GameAsset  0.669
        ///
        /// So the lowest confidence on the reference asset sits two thousandths above this value,
        /// and the test consequently fires on nothing (weak=0 on all three profiles). The mechanism
        /// below is sound and the threshold is a guess that has not been earned — what it needs is
        /// the per-component distribution, and specifically the confidence of the components that
        /// end up UNFLIPPED, which is the population it exists to catch. Until that is measured this
        /// constant should not be trusted and must not be tuned by moving it until a corpus row
        /// improves.
        /// </remarks>
        const float k_MinVoteConfidence = 0.15f;

        public static OrientStats Run(List<int[]> faces, Vector3[] positions, Vector3[] normals)
        {
            var stats = new OrientStats();
            if (faces == null || positions == null || normals == null) return stats;

            stats.faceCount = faces.Count;
            if (faces.Count == 0) return stats;

            // --- adjacency: which faces meet along each undirected edge ---
            //
            // Both halves of the pass need this. The first pair per edge is enough for the walk,
            // and the count is what decides whether a component is closed.
            var firstFace = new Dictionary<long, int>(faces.Count * 2, EdgeKey.Comparer);
            var secondFace = new Dictionary<long, int>(faces.Count / 2 + 1, EdgeKey.Comparer);
            var incidence = new Dictionary<long, int>(faces.Count * 2, EdgeKey.Comparer);

            var parent = new int[faces.Count];
            for (var i = 0; i < parent.Length; i++) parent[i] = i;

            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;

                for (var i = 0; i < face.Length; i++)
                {
                    var key = EdgeKey.PackUndirected(face[i], face[(i + 1) % face.Length]);

                    incidence.TryGetValue(key, out var count);
                    incidence[key] = count + 1;

                    if (!firstFace.ContainsKey(key)) firstFace[key] = f;
                    else if (!secondFace.ContainsKey(key)) { secondFace[key] = f; Union(parent, f, firstFace[key]); }
                    else Union(parent, f, firstFace[key]);
                }
            }

            // --- 1. consistency, by breadth-first flood over each component ---
            //
            // Two faces sharing an edge are consistently wound exactly when they traverse it in
            // OPPOSITE directions, the way two adjacent triangles of any sane mesh do. So the walk
            // needs no geometry at all: reach a neighbour, look at how it traverses the shared edge,
            // and reverse it if it matches instead of opposing.
            var visited = new bool[faces.Count];
            var queue = new Queue<int>();

            for (var seed = 0; seed < faces.Count; seed++)
            {
                if (visited[seed]) continue;

                var face = faces[seed];
                if (face == null || face.Length < 3) { visited[seed] = true; continue; }

                visited[seed] = true;
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var currentFace = faces[current];

                    for (var i = 0; i < currentFace.Length; i++)
                    {
                        var a = currentFace[i];
                        var b = currentFace[(i + 1) % currentFace.Length];
                        var key = EdgeKey.PackUndirected(a, b);

                        var neighbour = -1;
                        if (firstFace.TryGetValue(key, out var one) && one != current) neighbour = one;
                        else if (secondFace.TryGetValue(key, out var two) && two != current) neighbour = two;

                        if (neighbour < 0 || visited[neighbour]) continue;

                        var neighbourFace = faces[neighbour];
                        if (neighbourFace == null || neighbourFace.Length < 3) { visited[neighbour] = true; continue; }

                        // Does the neighbour walk a->b as well? Then it agrees with us where it
                        // should oppose, and it is the neighbour that is wrong.
                        if (TraversesForward(neighbourFace, a, b))
                        {
                            System.Array.Reverse(neighbourFace);
                            stats.facesMadeConsistent++;
                        }

                        visited[neighbour] = true;
                        queue.Enqueue(neighbour);
                    }
                }
            }

            // --- 2. outwardness, per component ---
            //
            // Two accumulators, because which one is meaningful depends on the component. Signed
            // volume is exact on a closed shell and meaningless on an open one; the normal vote is
            // available always but only as good as the sampled input normals.
            var volume = new Dictionary<int, double>();
            var votes = new Dictionary<int, double>();
            var areas = new Dictionary<int, double>();
            var open = new HashSet<int>();

            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;

                var root = Find(parent, f);

                if (!volume.ContainsKey(root)) { volume[root] = 0.0; votes[root] = 0.0; areas[root] = 0.0; }

                // Any edge of this face not shared by exactly two faces makes the component
                // something the divergence theorem does not apply to.
                for (var i = 0; i < face.Length; i++)
                {
                    var key = EdgeKey.PackUndirected(face[i], face[(i + 1) % face.Length]);
                    if (incidence[key] != 2) { open.Add(root); break; }
                }

                volume[root] += SignedVolume(positions, face);

                var geometric = QuadExtractor.FaceNormal(positions, face);
                var area = geometric.magnitude;   // Newell's normal is twice the area

                // A face whose normal has no usable direction contributes nothing rather than a
                // random sign. This is the whole reason the per-face version failed.
                if (!(area > 0f)) { stats.negligibleArea++; continue; }

                var surface = QuadExtractor.AverageNormal(normals, face);
                votes[root] += MeshMath.CosAngleBetween(geometric, surface) * area;

                // The same weight the vote used, kept separately so the vote's SIZE can be divided
                // out of it. votes/areas is an area-weighted mean cosine in [-1, 1]: its sign is the
                // answer and its magnitude is how much that answer is worth.
                areas[root] += area;
            }

            stats.components = votes.Count;

            // --- consensus, for the components that cannot read their own sign ---
            //
            // A vote's SIGN is the answer and its MAGNITUDE relative to the component's area is how
            // much that answer is worth: votes/areas is an area-weighted mean cosine, which is near
            // +/-1 where the surface normals agree with the winding and near 0 where they are noise.
            //
            // Measured, GameAsset on the reference asset: 13 components, 12 flipped, and the
            // thirteenth held THREE faces and went the other way. A 3-face shell has no usable mean
            // cosine, so its orientation was decided by rounding, and an inverted 3-face patch in
            // the middle of a surface is exactly as visible as a hole.
            //
            // The consensus is legitimate because extraction FRAGMENTS one input surface — the six
            // components on a closed manifold input are pieces of a single shell, not six shells —
            // and every piece inherits the same outward direction from the same input normals. The
            // assumption fails only for genuinely nested shells needing opposite flips, and a shell
            // large enough for that to matter has a confident vote of its own and never reaches here.
            var consensus = 0.0;
            foreach (var kv in votes)
            {
                var root = kv.Key;
                var area = areas[root];
                if (!(area > 0.0)) continue;

                // Confident components only, so the noise this exists to overrule cannot vote on
                // what overrules it.
                if (System.Math.Abs(kv.Value / area) >= k_MinVoteConfidence)
                    consensus += kv.Value;
            }

            var flip = new HashSet<int>();
            foreach (var kv in votes)
            {
                var root = kv.Key;
                var isClosed = !open.Contains(root);
                if (isClosed) stats.componentsClosed++;

                // A closed shell knows its own inside. Falling back to the vote when the volume is
                // exactly zero rather than trusting a sign that does not exist.
                if (isClosed && volume[root] != 0.0)
                {
                    if (volume[root] < 0.0) flip.Add(root);
                    continue;
                }

                var area = areas[root];
                var confidence = area > 0.0 ? (float)System.Math.Abs(kv.Value / area) : 0f;
                if (confidence < stats.weakestConfidence) stats.weakestConfidence = confidence;

                // Its own vote, when there is enough of it to read.
                if (confidence >= k_MinVoteConfidence)
                {
                    if (kv.Value < 0.0) flip.Add(root);
                    continue;
                }

                stats.componentsWeakVote++;

                // Otherwise the consensus, and its own vote only if there is no consensus either —
                // which happens when every component is weak, and then nothing better exists.
                var decision = consensus != 0.0 ? consensus : kv.Value;
                if (decision < 0.0) flip.Add(root);
            }

            stats.componentsFlipped = flip.Count;

            if (flip.Count > 0)
            {
                for (var f = 0; f < faces.Count; f++)
                {
                    var face = faces[f];
                    if (face == null || face.Length < 3) continue;
                    if (!flip.Contains(Find(parent, f))) continue;

                    System.Array.Reverse(face);
                    stats.facesFlipped++;
                }
            }

            Measure(faces, positions, normals, stats);

            return stats;
        }

        /// <summary>
        /// What is still wrong, measured rather than assumed. inconsistentEdges is the number the
        /// consistency walk is responsible for and should be zero; disagreeingAfter counts faces
        /// whose own normal opposes their component, which after a successful walk means a genuine
        /// geometric fold and not something winding can express.
        /// </summary>
        static void Measure(List<int[]> faces, Vector3[] positions, Vector3[] normals, OrientStats stats)
        {
            var directed = new HashSet<long>(faces.Count * 2, EdgeKey.Comparer);

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;

                for (var i = 0; i < face.Length; i++)
                {
                    var a = face[i];
                    var b = face[(i + 1) % face.Length];
                    if (!directed.Add(EdgeKey.Pack(a, b))) stats.inconsistentEdges++;
                }

                var geometric = QuadExtractor.FaceNormal(positions, face);
                var surface = QuadExtractor.AverageNormal(normals, face);
                if (MeshMath.CosAngleBetween(geometric, surface) < 0f) stats.disagreeingAfter++;
            }
        }

        /// <summary>True when the face walks from <paramref name="a"/> straight to <paramref name="b"/>.</summary>
        static bool TraversesForward(int[] face, int a, int b)
        {
            for (var i = 0; i < face.Length; i++)
                if (face[i] == a && face[(i + 1) % face.Length] == b) return true;

            return false;
        }

        /// <summary>
        /// Six times the signed volume this face contributes to its shell, by the divergence
        /// theorem, summed over a fan triangulation from the first corner. Doubles because the
        /// terms are products of three coordinates and a 5,000-face shell cancels most of them.
        /// </summary>
        static double SignedVolume(Vector3[] positions, int[] face)
        {
            var origin = positions[face[0]];
            var total = 0.0;

            for (var i = 1; i + 1 < face.Length; i++)
            {
                var u = positions[face[i]];
                var v = positions[face[i + 1]];

                double ax = origin.x, ay = origin.y, az = origin.z;
                double bx = u.x, by = u.y, bz = u.z;
                double cx = v.x, cy = v.y, cz = v.z;

                total += ax * (by * cz - bz * cy)
                       - ay * (bx * cz - bz * cx)
                       + az * (bx * cy - by * cx);
            }

            return total;
        }

        static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];   // path halving
                x = parent[x];
            }
            return x;
        }

        static void Union(int[] parent, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a != b) parent[b] = a;
        }
    }
}
