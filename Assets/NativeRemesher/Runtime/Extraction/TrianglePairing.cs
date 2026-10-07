// Remove triangles in PAIRS by re-tiling the patch between them.
//
// WHAT IT BUYS, measured against QuadRemesher on the character at 21,780 faces. The visible defect
// in our output is needle faces - worst corner 0.2 degrees against QuadRemesher's 15.3, p01 6.3
// against 47.1 - and 88 % of ours sit against a leftover triangle or n-gon, where QuadRemesher's
// figure is 2 %. Geometric fidelity, normals and median planarity are all already better than
// theirs. So quad fraction is the one number that moves the visible quality: 92.8 % against 99.99 %,
// about 1,650 non-quads against 2.
//
// THE PARITY CONSTRAINT, which decides the whole design. A triangle plus its three quad neighbours
// bounds a region with 3 + 3*2 = 9 boundary edges, and a region with an ODD boundary edge count
// admits no all-quad tiling at all. A lone triangle therefore cannot be removed by any local remesh,
// and `QuadRegularizer` is right to only merge triangle WITH triangle. Triangles come out in pairs
// or not at all.
//
// THE GENERALISATION. A chain of n quads between two triangles bounds a patch of n + 2 faces whose
// boundary is 2(n + 2) edges - always even, whatever n is - and a 2m-gon tiles into m - 1 quads. So
// the whole family is one operation:
//
//   n = 0   T1|T2 share an edge      4 boundary edges   -> 1 quad     (QuadRegularizer's case)
//   n = 1   T1-Q-T2                  6 boundary edges   -> 2 quads
//   n = 2   T1-Q-Q-T2                8 boundary edges   -> 3 quads
//   n = 3   T1-Q-Q-Q-T2             10 boundary edges   -> 4 quads
//
//        p                       p
//       / \                     /|\
//      /T1 \                   / | \
//     a-----b                 a  |  b        the shared edges become interior, the boundary is
//     |  Q  |       ->           |           a-p-b-q-c-d, and the tiling that scores best on
//     d-----c                 d  |  c        minimum angle is chosen
//      \ T2 /                   \ | /
//       \ /                      \|/
//        q                        q
//
// Every face count works out to "one fewer face, two fewer triangles", so this never inflates the
// mesh - which matters because the alternative textbook fix for a stray triangle is global
// subdivision, and that doubles the face count.
//
// WHAT IT DOES NOT FIX, established by measurement so it is not re-attempted. Removing triangles
// does NOT reduce the irregular-vertex count: they move from the "touching a non-quad" bucket into
// the "interior, all quads" bucket - 385 to 615 on the character - and the total is unchanged. The
// irregular vertices are a property of the quad LAYOUT, decided upstream in the field and the
// collapse, and re-tiling faces around them redistributes valence without removing any. This pass
// is for the needle faces, which is a different defect and the one that is actually visible.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class TrianglePairingStats
    {
        public int trianglesBefore;
        public int trianglesAfter;

        /// <summary>Triangle pairs found within the chain limit.</summary>
        public int candidatePairs;

        public int patchesApplied;

        /// <summary>Rounds run before the pass stopped making progress.</summary>
        public int rounds;

        /// <summary>Patches applied per chain length, index 1 = one quad between.</summary>
        public int[] appliedByChainLength = new int[16];

        /// <summary>Refused because the patch boundary was not a simple even loop.</summary>
        public int rejectedNotSimpleLoop;

        /// <summary>
        /// Refused because the patch enclosed a vertex not on its boundary. Tiling from the
        /// boundary alone would orphan it.
        /// </summary>
        public int rejectedInteriorVertex;

        /// <summary>Refused because no tiling produced acceptable quads without a duplicate edge.</summary>
        public int rejectedQuality;

        /// <summary>
        /// Patches that no strictly convex tiling could take, applied by the lenient second attempt.
        /// </summary>
        public int appliedWithFold;

        /// <summary>
        /// Edges with other than two incident faces, before and after. A re-tiling replaces a patch
        /// with a cover of the SAME boundary, so this may not change - and when it did, the mesh had
        /// a hole in it that nothing else in the pipeline reported.
        /// </summary>
        public int openEdgesBefore;

        public int openEdgesAfter;

        /// <summary>
        /// Of the refusals above, the reason that blocked the most starting corners. They are
        /// separated because they call for completely different fixes: a tiling refused because
        /// every fan bends the wrong way needs a better TILER, one refused because the diagonal it
        /// wants already exists needs a different PATCH, and one refused on the quality threshold is
        /// the only kind a parameter can move.
        /// </summary>
        public int becauseNonConvex;

        public int becauseBackwards;

        public int becauseDuplicateEdge;

        public int becauseBelowThreshold;

        /// <summary>Skipped because a face was already consumed by an earlier patch.</summary>
        public int skippedOverlapping;

        public int facesBefore;
        public int facesAfter;

        public long totalMs;

        public int trianglesRemoved => trianglesBefore - trianglesAfter;

        public override string ToString()
        {
            var byLength = new System.Text.StringBuilder();
            for (var n = 1; n < appliedByChainLength.Length; n++)
            {
                if (appliedByChainLength[n] == 0) continue;
                byLength.Append($" n{n}:{appliedByChainLength[n]}");
            }

            return $"trianglePairing: {trianglesBefore:N0} -> {trianglesAfter:N0} triangles " +
                   $"({trianglesRemoved:N0} removed), {patchesApplied:N0} of {candidatePairs:N0} " +
                   $"pairs applied in {rounds} rounds [{byLength.ToString().Trim()}], refused " +
                   $"{rejectedNotSimpleLoop:N0} loop + {rejectedInteriorVertex:N0} interior + " +
                   $"{rejectedQuality:N0} quality (nonConvex {becauseNonConvex:N0}, " +
                   $"backwards {becauseBackwards:N0}, dupEdge {becauseDuplicateEdge:N0}, " +
                   $"threshold {becauseBelowThreshold:N0}), " +
                   $"{appliedWithFold:N0} needed a fold, {skippedOverlapping:N0} overlapping, " +
                   $"open edges {openEdgesBefore:N0} -> {openEdgesAfter:N0}" +
                   (openEdgesAfter > openEdgesBefore ? " HOLE OPENED, " : ", ") +
                   $"faces {facesBefore:N0} -> {facesAfter:N0}, {totalMs} ms";
        }
    }

    public static class TrianglePairing
    {
        /// <summary>
        /// Lowest quad quality a re-tiling may produce, on the same scale
        /// <see cref="QuadRegularizer"/> uses: the smallest interior angle over 90 degrees.
        /// </summary>
        /// <remarks>
        /// Deliberately LOW, and the measurement says how low is right. `QuadRegularizer` chooses
        /// between "merge these triangles" and "leave two triangles", where a bad quad is a real
        /// cost, so its 0.15 is defensible. Here the choice is between quads and "two triangles plus
        /// the quads between them", and the competitor comparison showed a leftover non-quad costs
        /// far more visually than a mediocre quad: 88 % of our worst-angle faces touch a non-quad
        /// against QuadRemesher's 2 %. Refusing a merge to protect quad quality protects the wrong
        /// thing.
        /// </remarks>
        public const float defaultQualityThreshold = 0.02f;

        /// <summary>
        /// Longest chain of quads allowed between the two triangles.
        /// </summary>
        /// <remarks>
        /// 4, so the largest patch is a 12-gon tiled by 5 quads. The limit is about the tiling, not
        /// the search: a long patch is re-tiled by a fan from one of its corners, and past about
        /// this length the fan's centre vertex accumulates enough valence that the result is worse
        /// than the triangles it replaced. Measured chain lengths on the real assets fall off
        /// steeply anyway - on the character, 233 pairs at n=1 against 87 at n>=3.
        /// </remarks>
        public const int defaultMaxChainQuads = 4;

        /// <summary>
        /// Rounds to repeat the pass for, while it is still making progress.
        /// </summary>
        /// <remarks>
        /// A patch consumes its faces, so overlapping candidates are skipped - and on the real
        /// assets nearly all of them overlap. With about 3,500 triangles among 25,400 faces the
        /// typical separation is under three faces, so most patches compete for the same quads and
        /// only one of each cluster is taken. One pass therefore stalls well short of what the
        /// operation can reach: quad fraction plateaued at 90 % however far the thresholds were
        /// relaxed and however long the chains were allowed to be.
        ///
        /// Re-running exposes the pairings the previous round's consumption hid. It terminates on
        /// its own because every applied patch strictly reduces the triangle count, so the cap is a
        /// backstop rather than the mechanism.
        /// </remarks>
        public const int defaultMaxRounds = 12;

        /// <summary>
        /// How far a corner of a re-tiled quad may fold back past straight, in degrees, when no
        /// strictly convex tiling of the patch exists.
        /// </summary>
        /// <remarks>
        /// A convex tiling is always preferred - the lenient attempt only runs when the strict one
        /// has already failed - so this trades nothing away in the cases that were working. It only
        /// decides what happens in the cases that were being abandoned, and the choice there is not
        /// between a folded quad and a good one, it is between a folded quad and TWO TRIANGLES plus
        /// the quads between them, which is the worse of the two both to look at and to work with.
        ///
        /// The same lesson as this pass's quality threshold, which was lowered twice for the same
        /// reason: refusing a mediocre quad to protect quad quality protects the wrong thing when
        /// the alternative is a leftover triangle.
        ///
        /// 10 degrees, and the column that decides it is IRREGULAR VERTICES rather than triangles.
        ///
        /// This pass was first set to 40 on a sweep of triangles, quad fraction, curled faces and
        /// minimum angle, all of which improve monotonically as the tolerance rises. The per-stage
        /// census then showed what that sweep could not see: this is the largest single source of
        /// irregular vertices in the whole pipeline. On the character it added 386 of them, and on
        /// an icosphere whose field carries eight singularities it DOUBLED the count, 79 to 159.
        ///
        /// The mechanism is not incidental, it is what the operation does. Re-tiling a patch of
        /// n + 2 faces into n + 1 quads removes a face, so the vertices along the seam each lose a
        /// neighbour: a valence-4 vertex becomes a valence-3. Two triangles are traded for about two
        /// singularities, every time.
        ///
        /// Swept again with that column present, on the character at the delivered density -
        /// quad fraction / irregular / triangles left:
        ///
        ///   off     87.01 %% / 21.95 %% / 622        20 deg  97.06 %% / 26.27 %% / 151
        ///    0 deg  94.71 %% / 23.54 %% / 274        40 deg  97.55 %% / 26.47 %% / 127
        ///   10 deg  96.08 %% / 23.55 %% / 203
        ///
        /// Ten degrees is free: it has the same irregularity as refusing every fold and a quarter
        /// fewer triangles. Past it the trade turns sharply - 20 and 40 buy 50 and 76 more triangles
        /// for nearly three points of irregularity, and irregular vertices are what the output
        /// actually looks disordered from.
        ///
        /// Leaving the pass off entirely is not the answer either: 622 triangles is 13 %% of the
        /// mesh, and it costs nine points of quad fraction to save 1.6 of irregularity.
        /// </remarks>
        public const float defaultMaxReflexDegrees = 10f;

        public static TrianglePairingStats Run(List<int[]> faces, Vector3[] positions,
            float qualityThreshold = defaultQualityThreshold,
            int maxChainQuads = defaultMaxChainQuads,
            int maxRounds = defaultMaxRounds,
            float maxReflexDegrees = defaultMaxReflexDegrees)
        {
            if (faces == null) throw new ArgumentNullException(nameof(faces));
            if (positions == null) throw new ArgumentNullException(nameof(positions));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stats = new TrianglePairingStats { facesBefore = faces.Count };

            for (var f = 0; f < faces.Count; f++)
                if (faces[f].Length == 3) stats.trianglesBefore++;

            stats.openEdgesBefore = CountOpenEdges(faces);

            // Edges added by any round, carried across all of them. `edgeToFaces` is rebuilt each
            // round and so knows about earlier rounds' diagonals, but a diagonal added EARLIER IN
            // THE SAME round is not in it - and that gap produced exactly one non-manifold edge on
            // ccnb before the set existed.
            var addedEdges = new HashSet<long>(EdgeKey.Comparer);

            for (var round = 0; round < maxRounds; round++)
            {
                var applied = RunOnce(faces, positions, qualityThreshold, maxChainQuads,
                    maxReflexDegrees, addedEdges, stats);

                stats.rounds = round + 1;
                if (applied == 0) break;
            }

            foreach (var face in faces)
                if (face.Length == 3) stats.trianglesAfter++;

            stats.openEdgesAfter = CountOpenEdges(faces);
            stats.facesAfter = faces.Count;
            stats.totalMs = watch.ElapsedMilliseconds;
            return stats;
        }

        /// <summary>Edges with other than two incident faces: an open rim, or a hole.</summary>
        static int CountOpenEdges(List<int[]> faces)
        {
            var uses = new Dictionary<long, int>(faces.Count * 4, EdgeKey.Comparer);

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    uses.TryGetValue(key, out var used);
                    uses[key] = used + 1;
                }
            }

            var open = 0;
            foreach (var used in uses.Values)
                if (used != 2) open++;
            return open;
        }

        /// <summary>One pass of non-overlapping patches. Returns how many it applied.</summary>
        static int RunOnce(List<int[]> faces, Vector3[] positions, float qualityThreshold,
            int maxChainQuads, float maxReflexDegrees, HashSet<long> addedEdges,
            TrianglePairingStats stats)
        {
            var triangleIds = new List<int>();
            for (var f = 0; f < faces.Count; f++)
                if (faces[f].Length == 3) triangleIds.Add(f);

            if (triangleIds.Count < 2) return 0;

            var edgeToFaces = BuildEdgeMap(faces);
            var adjacency = BuildFaceAdjacency(faces, edgeToFaces);

            // --- find each triangle's nearest partner, and the chain of quads between them ---
            var candidates = new List<(int length, int a, int b, List<int> chain)>();

            foreach (var source in triangleIds)
            {
                if (!FindPartner(faces, adjacency, source, maxChainQuads,
                        out var partner, out var chain))
                    continue;

                // Recorded once per unordered pair. Both ends usually find each other, and letting
                // both in would double the candidate count and make the sort order depend on which
                // copy came first.
                if (partner < source) continue;

                candidates.Add((chain.Count, source, partner, chain));
            }

            // Accumulated across rounds, like every other counter here. Assigning it per round
            // left the report saying "1,224 of 35 pairs applied", which is not only wrong but
            // reads as though the pass applied more patches than it found candidates for.
            stats.candidatePairs += candidates.Count;

            // Shortest chain first: a short patch is a smaller, better-shaped re-tiling, and taking
            // it first stops a long patch from consuming faces a short one needed. Ties broken by
            // face index so the result does not depend on enumeration order.
            candidates.Sort((x, y) =>
            {
                var byLength = x.length.CompareTo(y.length);
                if (byLength != 0) return byLength;
                var byA = x.a.CompareTo(y.a);
                return byA != 0 ? byA : x.b.CompareTo(y.b);
            });

            var consumed = new bool[faces.Count];
            var replacement = new List<int[]>();
            var appliedThisRound = 0;

            foreach (var candidate in candidates)
            {
                var patch = new List<int>(candidate.chain.Count + 2) { candidate.a };
                patch.AddRange(candidate.chain);
                patch.Add(candidate.b);

                var overlapping = false;
                foreach (var f in patch)
                {
                    if (!consumed[f]) continue;
                    overlapping = true;
                    break;
                }

                if (overlapping) { stats.skippedOverlapping++; continue; }

                if (!BoundaryLoop(faces, patch, out var loop)
                    || loop.Count < 4 || loop.Count % 2 != 0)
                {
                    stats.rejectedNotSimpleLoop++;
                    continue;
                }

                // A vertex used by the patch but absent from its boundary is enclosed by it. Tiling
                // from the boundary would leave it referenced by no face - an orphan vertex, which
                // Unity carries silently and every downstream tool reports as a corrupt mesh.
                if (EnclosesAVertex(faces, patch, loop))
                {
                    stats.rejectedInteriorVertex++;
                    continue;
                }

                // The patch's own aggregate normal, from the faces about to be replaced. It is the
                // only local record of which way this piece of surface faces, and without it a fan
                // tiling of a curved patch can come out facing backwards - measured at 199 wrongly
                // directed faces against 53 with this pass switched off.
                var reference = PatchNormal(faces, positions, patch);

                // Convex first, always. Only if the patch admits no convex tiling at all is a
                // bounded fold considered, so nothing that works today is traded away.
                var strict = BestTiling(positions, loop, qualityThreshold, edgeToFaces, addedEdges,
                    reference, 0f, out var tiles, out var diagonals, out var why);

                if (!strict && maxReflexDegrees > 0f
                    && BestTiling(positions, loop, qualityThreshold, edgeToFaces, addedEdges,
                        reference, maxReflexDegrees, out tiles, out diagonals, out why))
                {
                    stats.appliedWithFold++;
                    strict = true;
                }

                if (!strict)
                {
                    stats.rejectedQuality++;
                    switch (why)
                    {
                        case TilingFailure.NonConvex: stats.becauseNonConvex++; break;
                        case TilingFailure.Backwards: stats.becauseBackwards++; break;
                        case TilingFailure.DuplicateEdge: stats.becauseDuplicateEdge++; break;
                        default: stats.becauseBelowThreshold++; break;
                    }
                    continue;
                }

                foreach (var f in patch) consumed[f] = true;
                replacement.AddRange(tiles);
                foreach (var diagonal in diagonals) addedEdges.Add(diagonal);

                appliedThisRound++;
                stats.patchesApplied++;
                var chainLength = Mathf.Min(candidate.chain.Count,
                    stats.appliedByChainLength.Length - 1);
                stats.appliedByChainLength[chainLength]++;
            }

            if (appliedThisRound > 0)
            {
                var rebuilt = new List<int[]>(faces.Count);
                for (var f = 0; f < faces.Count; f++)
                {
                    if (consumed[f]) continue;
                    rebuilt.Add(faces[f]);
                }

                rebuilt.AddRange(replacement);
                faces.Clear();
                faces.AddRange(rebuilt);
            }

            return appliedThisRound;
        }

        /// <summary>
        /// Breadth-first search from a triangle, through QUADS only, to the nearest other triangle.
        /// </summary>
        /// <remarks>
        /// Quads only as intermediates, because a chain that passed through another triangle would
        /// be describing two shorter pairs and consuming the middle triangle in a patch that does
        /// not remove it. The returned chain excludes both endpoints.
        /// </remarks>
        static bool FindPartner(List<int[]> faces, Adjacency adjacency, int source,
            int maxChainQuads, out int partner, out List<int> chain)
        {
            partner = -1;
            chain = null;

            var cameFrom = new Dictionary<int, int> { [source] = -1 };
            var frontier = new List<int> { source };

            for (var depth = 0; depth <= maxChainQuads && frontier.Count > 0; depth++)
            {
                var next = new List<int>();

                foreach (var f in frontier)
                {
                    var from = adjacency.start[f];
                    var to = adjacency.start[f + 1];

                    for (var k = from; k < to; k++)
                    {
                        var g = adjacency.neighbours[k];
                        if (cameFrom.ContainsKey(g)) continue;

                        if (faces[g].Length == 3)
                        {
                            // Found the partner. Walk the parent chain back, dropping both ends.
                            partner = g;
                            chain = new List<int>();

                            var step = f;
                            while (step != source)
                            {
                                chain.Add(step);
                                step = cameFrom[step];
                            }

                            chain.Reverse();
                            return true;
                        }

                        if (faces[g].Length != 4) continue; // n-gons are not valid intermediates

                        cameFrom[g] = f;
                        next.Add(g);
                    }
                }

                frontier = next;
            }

            return false;
        }

        struct Adjacency
        {
            public int[] start;
            public int[] neighbours;
        }

        static Adjacency BuildFaceAdjacency(List<int[]> faces,
            Dictionary<long, List<int>> edgeToFaces)
        {
            var counts = new int[faces.Count + 1];
            foreach (var users in edgeToFaces.Values)
            {
                if (users.Count != 2) continue;
                counts[users[0] + 1]++;
                counts[users[1] + 1]++;
            }

            for (var f = 0; f < faces.Count; f++) counts[f + 1] += counts[f];

            var cursor = new int[faces.Count];
            Array.Copy(counts, cursor, faces.Count);
            var neighbours = new int[counts[faces.Count]];

            foreach (var users in edgeToFaces.Values)
            {
                if (users.Count != 2) continue;
                neighbours[cursor[users[0]]++] = users[1];
                neighbours[cursor[users[1]]++] = users[0];
            }

            return new Adjacency { start = counts, neighbours = neighbours };
        }

        static Dictionary<long, List<int>> BuildEdgeMap(List<int[]> faces)
        {
            var map = new Dictionary<long, List<int>>(faces.Count * 4, EdgeKey.Comparer);

            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    if (!map.TryGetValue(key, out var list))
                        map[key] = list = new List<int>(2);
                    list.Add(f);
                }
            }

            return map;
        }

        /// <summary>
        /// The oriented boundary loop of a patch of faces, or false if it is not one simple loop.
        /// </summary>
        /// <remarks>
        /// Built from DIRECTED edges. In a consistently wound patch an interior edge appears twice,
        /// once in each direction, so cancelling opposite pairs leaves exactly the boundary already
        /// oriented. Undirected edges would lose that orientation and the output quads would have to
        /// guess their winding - a mistake that surfaces as inside-out shading, not as an error.
        /// </remarks>
        static bool BoundaryLoop(List<int[]> faces, List<int> patch, out List<int> loop)
        {
            loop = null;

            // Cancellation FIRST, keyed by the directed edge itself, and only then chained into a
            // loop. Keying by origin vertex and cancelling as it went was wrong: while the patch is
            // half-processed a vertex legitimately has two outgoing edges - in T1(0,1,4) + Q(1,2,5,4)
            // vertex 1 emits both 1->4 and 1->2 until 4->1 arrives and cancels the first - and
            // treating that as inconsistent winding rejected every valid patch, including the plain
            // hexagon.
            var directed = new HashSet<long>(EdgeKey.Comparer);

            foreach (var f in patch)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];

                    var backward = EdgeKey.Pack(b, a);
                    if (directed.Remove(backward)) continue; // interior to the patch

                    // The same directed edge twice means the patch is not consistently wound, and
                    // its boundary is then not well defined.
                    if (!directed.Add(EdgeKey.Pack(a, b))) return false;
                }
            }

            if (directed.Count < 4) return false;

            var next = new Dictionary<int, int>(directed.Count);
            foreach (var edge in directed)
            {
                var from = EdgeKey.First(edge);

                // Now that cancellation is done, two exits from one vertex means the boundary
                // pinches there and is not a simple loop.
                if (next.ContainsKey(from)) return false;
                next[from] = EdgeKey.Second(edge);
            }

            var start = -1;
            foreach (var entry in next) { start = entry.Key; break; }

            loop = new List<int>(next.Count);
            var seen = new HashSet<int>();
            var current = start;

            for (var i = 0; i < next.Count; i++)
            {
                // A vertex reached twice means the walk has closed a loop shorter than the boundary
                // it was given, and the rest of that boundary belongs to a DIFFERENT loop.
                //
                // Returning to the start after the full count is not enough on its own, and this is
                // the hole the corpus caught: two disjoint loops of the SAME length pass that test
                // perfectly. The walk goes round the first one twice, arrives back at the start, and
                // hands back a vertex list with every vertex in it twice. Tiling that produces
                // degenerate faces over the first region and leaves the second covered by nothing at
                // all - three boundary edges appeared on a closed icosphere, which is one triangular
                // hole in a mesh that had none.
                if (!seen.Add(current)) return false;

                loop.Add(current);
                if (!next.TryGetValue(current, out var following)) return false;
                current = following;
            }

            if (current != start) return false; // several disjoint loops rather than one

            var distinct = new HashSet<int>(loop);
            return distinct.Count == loop.Count;
        }

        static bool EnclosesAVertex(List<int[]> faces, List<int> patch, List<int> loop)
        {
            var onBoundary = new HashSet<int>(loop);

            foreach (var f in patch)
            foreach (var corner in faces[f])
            {
                if (!onBoundary.Contains(corner)) return true;
            }

            return false;
        }

        /// <summary>
        /// Tiles a 2m-gon with m-1 quads, choosing the fan origin that scores best.
        /// </summary>
        /// <remarks>
        /// A fan from corner s takes quads (s, s+1, s+2, s+3), (s, s+3, s+4, s+5) and so on, which
        /// uses every boundary edge exactly once and closes at s. Every origin is tried because the
        /// fan's centre carries all the added diagonals, so which corner it is decides both the
        /// worst angle and where valence accumulates.
        ///
        /// Each candidate diagonal is checked against the existing mesh AND against diagonals added
        /// by earlier patches in this same run. Without the second check the pass introduced exactly
        /// one non-manifold edge on ccnb: `edgeToFaces` is built once and does not know about
        /// edges this pass itself has added.
        /// </remarks>
        /// <summary>Area-weighted normal of the faces a patch is about to replace.</summary>
        static Vector3 PatchNormal(List<int[]> faces, Vector3[] positions, List<int> patch)
        {
            var normal = Vector3.zero;
            foreach (var f in patch) normal += Newell(positions, faces[f]);
            return normal;
        }

        /// <summary>
        /// Twice the area-weighted normal of a polygon, by Newell's method.
        /// </summary>
        /// <remarks>
        /// Newell rather than one cross product of two edges: these polygons are not planar, and a
        /// single cross product returns whichever corner it happened to sample.
        /// </remarks>
        static Vector3 Newell(Vector3[] positions, int[] face)
        {
            var normal = Vector3.zero;
            for (var k = 0; k < face.Length; k++)
            {
                var current = positions[face[k]];
                var following = positions[face[(k + 1) % face.Length]];
                normal.x += (current.y - following.y) * (current.z + following.z);
                normal.y += (current.z - following.z) * (current.x + following.x);
                normal.z += (current.x - following.x) * (current.y + following.y);
            }

            return normal;
        }

        /// <summary>
        /// Whether a quad has a reflex corner, i.e. is folded rather than flat-ish.
        /// </summary>
        /// <remarks>
        /// `QuadRegularizer` already refuses a non-convex merge and this pass did not, which is
        /// most of why it tripled the curled-quad count: 822 against 276 with it off, where
        /// QuadRemesher produces 5. A folded quad cannot be drawn flat, so it is split into two
        /// triangles meeting at a crease, and that crease shades as a hard edge with no geometry
        /// behind it - which is exactly the "curled" and "slimed" appearance being reported.
        /// </remarks>
        static bool IsNonConvex(Vector3[] positions, int[] quad, Vector3 reference)
        {
            return ReflexExcess(positions, quad, reference) > 0f;
        }

        /// <summary>
        /// How far past straight the worst reflex corner of a quad turns, in degrees. Zero when the
        /// quad is convex.
        /// </summary>
        /// <remarks>
        /// The binary test above answers "is any corner reflex at all", and on a CURVED patch that
        /// is nearly always yes by a degree or two: the four corners of a quad laid over a shoulder
        /// do not lie in a plane, so projecting them onto the patch normal turns a slight bend into
        /// a reflex corner. Measured on the character, every single refusal in this pass - 501 of
        /// 501 in the first run and 451 of 454 in the second - was this test, with not one caused by
        /// the quality threshold or a duplicate edge. A binary test on a quantity that is almost
        /// always slightly positive is not a filter, it is an off switch.
        ///
        /// Grading it lets the caller say how much folding is worth accepting to be rid of two
        /// triangles, which is the trade this pass exists to make.
        /// </remarks>
        static float ReflexExcess(Vector3[] positions, int[] quad, Vector3 reference)
        {
            var worst = 0f;

            for (var k = 0; k < 4; k++)
            {
                var previous = positions[quad[(k + 3) % 4]];
                var current = positions[quad[k]];
                var following = positions[quad[(k + 1) % 4]];

                var u = previous - current;
                var v = following - current;

                // Turning the opposite way to the patch as a whole means this corner folds back.
                if (Vector3.Dot(Vector3.Cross(v, u), reference) >= 0f) continue;

                var lu = u.magnitude;
                var lv = v.magnitude;
                if (lu < 1e-12f || lv < 1e-12f) return 180f;

                // The corner measures `angle` between its edges, so a reflex one has an interior
                // angle of 360 - angle and folds back by 180 - angle.
                var angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(u, v) / (lu * lv), -1f, 1f))
                            * Mathf.Rad2Deg;
                worst = Mathf.Max(worst, 180f - angle);
            }

            return worst;
        }

        /// <summary>Why every candidate tiling of a patch was refused.</summary>
        public enum TilingFailure
        {
            BelowThreshold,
            NonConvex,
            Backwards,
            DuplicateEdge
        }

        static bool BestTiling(Vector3[] positions, List<int> loop, float threshold,
            Dictionary<long, List<int>> edgeToFaces, HashSet<long> addedEdges,
            Vector3 reference, float maxReflexDegrees, out List<int[]> tiles,
            out List<long> diagonals, out TilingFailure failure)
        {
            tiles = null;
            diagonals = null;
            failure = TilingFailure.BelowThreshold;

            var corners = loop.Count;
            var quadCount = corners / 2 - 1;
            var best = float.NegativeInfinity;
            int nonConvex = 0, backwards = 0, duplicate = 0;

            for (var s = 0; s < corners; s++)
            {
                var candidateTiles = new List<int[]>(quadCount);
                var candidateDiagonals = new List<long>(quadCount - 1);
                var worst = float.MaxValue;
                var legal = true;

                for (var q = 0; q < quadCount; q++)
                {
                    var i0 = loop[s];
                    var i1 = loop[(s + 2 * q + 1) % corners];
                    var i2 = loop[(s + 2 * q + 2) % corners];
                    var i3 = loop[(s + 2 * q + 3) % corners];

                    // Every quad but the last introduces the diagonal from the fan centre to i3.
                    if (q < quadCount - 1)
                    {
                        var diagonal = EdgeKey.PackUndirected(i0, i3);
                        if (edgeToFaces.ContainsKey(diagonal) || addedEdges.Contains(diagonal))
                        {
                            legal = false;
                            duplicate++;
                            break;
                        }

                        candidateDiagonals.Add(diagonal);
                    }

                    var quad = new[] { i0, i1, i2, i3 };

                    // Both checks the pass was missing, and both are the reason it made the visible
                    // defects worse while improving quad fraction.
                    if (ReflexExcess(positions, quad, reference) > maxReflexDegrees)
                    {
                        legal = false;
                        nonConvex++;
                        break;
                    }

                    // Facing the opposite way to the surface it replaces. Reversing the quad is not
                    // an option - its winding is shared with the neighbours that stay - so the
                    // tiling is refused and another origin is tried.
                    if (Vector3.Dot(Newell(positions, quad), reference) <= 0f)
                    {
                        legal = false;
                        backwards++;
                        break;
                    }

                    var quality = Quality(positions, quad);
                    if (quality < worst) worst = quality;

                    candidateTiles.Add(quad);
                }

                if (!legal) continue;

                // Scored on the WORST quad of the tiling: one excellent quad beside a degenerate
                // one is a worse outcome than a middling set, and an average would hide it.
                if (worst <= best) continue;

                best = worst;
                tiles = candidateTiles;
                diagonals = candidateDiagonals;
            }

            if (tiles == null || best < threshold)
            {
                if (tiles != null) failure = TilingFailure.BelowThreshold;
                else if (nonConvex >= backwards && nonConvex >= duplicate)
                    failure = TilingFailure.NonConvex;
                else if (backwards >= duplicate) failure = TilingFailure.Backwards;
                else failure = TilingFailure.DuplicateEdge;

                tiles = null;
                diagonals = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Quad quality in [0,1]: the smallest interior angle, normalised so a square scores 1.
        /// </summary>
        /// <remarks>
        /// The same measure <see cref="QuadRegularizer"/> scores merges by, so the two passes'
        /// thresholds are on one scale. Minimum angle rather than an average because the defect that
        /// matters is the needle - and the competitor comparison put a number on that: our worst
        /// corner is 0.2 degrees against QuadRemesher's 15.3.
        /// </remarks>
        static float Quality(Vector3[] positions, int[] quad)
        {
            var worst = 180f;

            for (var k = 0; k < 4; k++)
            {
                var previous = positions[quad[(k + 3) % 4]];
                var current = positions[quad[k]];
                var following = positions[quad[(k + 1) % 4]];

                var a = previous - current;
                var b = following - current;

                var lengthA = a.magnitude;
                var lengthB = b.magnitude;

                // A zero-length edge means two corners coincide: the quad is degenerate and must
                // score zero rather than produce a NaN from the normalisation below.
                if (lengthA < 1e-12f || lengthB < 1e-12f) return 0f;

                var cos = Mathf.Clamp(Vector3.Dot(a, b) / (lengthA * lengthB), -1f, 1f);
                var angle = Mathf.Acos(cos) * Mathf.Rad2Deg;
                if (angle < worst) worst = angle;
            }

            return worst / 90f;
        }
    }
}
