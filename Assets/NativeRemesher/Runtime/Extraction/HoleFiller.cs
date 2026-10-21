// Pass G': close the holes the sized tracing passes left behind.
//
// Why this exists instead of a catch-all tracing pass.
//
// The first attempt at fixing holes was to run one more tracing pass with no target size and a
// generous cap, accepting any loop that closed. It did remove the holes — boundary edges went to
// zero on a closed sphere — but the Euler characteristic went from -7 to -14 instead of to 2.
// The reason is that a walk over the *graph* is free to leave the region around the hole: a loop
// that wanders far enough and comes back has closed a tunnel, not a cap. Every such face adds a
// handle, so the surface got topologically worse while looking better.
//
// This pass cannot do that, by construction. It works on the *faces already emitted*, finds the
// edges with exactly one incident face, chains those into loops, and emits each simple loop as
// one face. Capping a simple boundary cycle with a disc is precisely the operation that removes
// a hole without changing genus, so V - E + F moves by exactly +1 per cap.
//
// A non-simple loop — one that visits a vertex twice, which happens where two holes meet at a
// pinch — is split at the repeat into simple sub-loops. Anything still not simple is left open
// and counted, because a wrong cap is worse than a reported hole.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class HoleFillStats
    {
        /// <summary>Boundary edges present before this pass ran.</summary>
        public int boundaryEdgesFound;

        /// <summary>Simple loops capped. Each raises the Euler characteristic by one.</summary>
        public int loopsCapped;

        /// <summary>Loops split at a repeated vertex before capping.</summary>
        public int loopsSplit;

        /// <summary>Loops closed by a centroid fan because they were too large for one face.</summary>
        public int loopsFanned;

        /// <summary>Rims turned into quads around a centroid hub, and how many quads that produced.</summary>
        public int loopsQuadrangulated;
        public int quadsFromCaps;

        /// <summary>
        /// Rims closed by an interior GRID rather than a hub, the quads that produced, and the
        /// interior vertices it added.
        /// </summary>
        /// <remarks>
        /// The number to watch. A hub pie leaves one vertex of valence rim/2 — a 67-corner rim gives
        /// a valence-33 singularity — and that is what renders as a "sun" of edges radiating from a
        /// point. A grid leaves interior vertices of valence FOUR and irregularity only at the four
        /// corners, which is what the reference retopology does and why it has none of these.
        /// </remarks>
        public int loopsGridFilled;
        public int quadsFromGrids;
        public int gridVertices;

        /// <summary>Triangles clipped off odd rims so the remainder could be gridded. One per rim.</summary>
        public int trianglesFromGridEars;

        /// <summary>Rims a grid was attempted on and rejected, so they fell back to a hub.</summary>
        public int gridRejected;

        /// <summary>Largest rim a grid closed, and largest still left to a hub.</summary>
        public int largestGrid;
        public int largestHub;

        /// <summary>Rims still emitted as a single n-gon face, because a hub was not geometrically
        /// honest there. Every n-gon above four corners in the output comes from here.</summary>
        public int ngonCaps;

        /// <summary>Vertices this pass added — one hub per fan. Appended to the output positions.</summary>
        public int verticesAdded;

        /// <summary>
        /// Positions of the vertices this pass added, in the order their indices were handed out
        /// starting at the caller's vertex count. The caller must append these to its own position
        /// array, or the fan faces reference indices that do not exist.
        /// </summary>
        public readonly List<Vector3> addedPositions = new List<Vector3>();

        /// <summary>Rim size of every loop closed by a fan, for choosing where to stop fanning.</summary>
        public readonly List<int> fanSizes = new List<int>();

        /// <summary>Rim size of every loop refused for being too large even to fan.</summary>
        public readonly List<int> refusedSizes = new List<int>();

        /// <summary>Loops left open because they could not be reduced to a simple cycle.</summary>
        public int loopsAbandoned;

        /// <summary>
        /// Failed walks that ran out of boundary edges to follow.
        /// </summary>
        /// <remarks>
        /// Over-counts, and deliberately kept separate from the two below because of it: walks are
        /// attempted from every (vertex, successor) pair, so the same unreachable edges are retried
        /// many times and each retry scores here. Useful as a ratio against
        /// <see cref="abandonedTooLong"/>, not as a count of anything.
        /// </remarks>
        public int abandonedDeadEnd;

        /// <summary>
        /// Loops left open because the hub a cap needs would not have been on the surface. See
        /// HoleFiller.maxHubSurfaceDistance.
        /// </summary>
        public int loopsOffSurface;

        /// <summary>Failed walks that exceeded <see cref="HoleFiller.maxCapSize"/> before closing.</summary>
        public int abandonedTooLong;

        /// <summary>Longest partial loop seen in a failed walk. The size a cap would have needed.</summary>
        public int longestAbandoned;

        /// <summary>Boundary edges still uncovered when the pass finished. The holes that remain.</summary>
        public int edgesLeftUncovered;

        /// <summary>
        /// Loops recognised as the input's own outline and deliberately left open.
        /// </summary>
        /// <remarks>
        /// On an open input this should be small and roughly the number of distinct outline loops
        /// the mesh has — one for a terrain patch, more if the scan has islands. A large number
        /// means the outline test is matching holes as well, and the result will keep them.
        /// </remarks>
        public int loopsLeftOpen;

        /// <summary>Stretches of the outline loop running inside the input's outline, closed and capped.</summary>
        public int outlineNotchesFilled;

        /// <summary>Corner count of the largest cap emitted — a big one is a quality warning.</summary>
        public int largestCap;

        public override string ToString()
        {
            return $"boundaryEdges={boundaryEdgesFound} capped={loopsCapped} split={loopsSplit} " +
                   $"quadrangulated={loopsQuadrangulated}(+{quadsFromCaps}quads) " +
                   $"grid={loopsGridFilled}(+{quadsFromGrids}quads +{trianglesFromGridEars}ears " +
                   $"+{gridVertices}verts " +
                   $"rej={gridRejected} largest={largestGrid}) hubLargest={largestHub} " +
                   $"ngonCaps={ngonCaps} fanned={loopsFanned} added={verticesAdded} " +
                   $"abandoned={loopsAbandoned}(dead={abandonedDeadEnd} long={abandonedTooLong} " +
                   $"offSurface={loopsOffSurface} " +
                   $"longest={longestAbandoned}) leftOpen={loopsLeftOpen} notches={outlineNotchesFilled} uncovered={edgesLeftUncovered} " +
                   $"largestCap={largestCap} fans={Sizes(fanSizes)} refused={Sizes(refusedSizes)}";
        }

        /// <summary>Compact distribution summary: count, median and max.</summary>
        static string Sizes(List<int> sizes)
        {
            if (sizes.Count == 0) return "none";

            var sorted = new List<int>(sizes);
            sorted.Sort();

            var over128 = 0;
            var over512 = 0;
            foreach (var s in sorted)
            {
                if (s > 128) over128++;
                if (s > 512) over512++;
            }

            return $"n={sorted.Count}/med={sorted[sorted.Count / 2]}/max={sorted[sorted.Count - 1]}" +
                   $"/gt128={over128}/gt512={over512}";
        }
    }

    public static class HoleFiller
    {
        /// <summary>
        /// A loop longer than this is not capped. Such a loop is not a hole in a lattice — it is
        /// the outline of a region the extractor failed on wholesale, and covering it with one
        /// enormous face hides that rather than fixing it.
        /// </summary>
        /// <remarks>
        /// Generous on purpose, and that was tested rather than assumed.
        ///
        /// The paragraph above reads like an argument for a small limit, and it was briefly
        /// tightened to 2 * FaceTracer.maxFaceSize on the theory that a 62-corner cap emitted on a
        /// subdivided cube was what drove worst-case deviation from 0.297 to 0.354. **That was a
        /// misattribution.** Measured both ways on that cube:
        ///
        ///     limit 64:  features on -> 0 boundary edges, Hausdorff 0.354
        ///                features off -> 88 boundary edges, Hausdorff 0.297
        ///     limit 16:  features on -> 117 boundary edges, Hausdorff 0.361
        ///                features off -> 116 boundary edges, Hausdorff 0.357
        ///
        /// Shrinking the limit left holes open and made the geometry *worse* on both arms — the
        /// features-off arm degraded from 0.297 to 0.357. Closing a hole helps surface fidelity
        /// even when the cap is an ugly n-gon, because a missing face deviates more than a flat
        /// one. The 0.354-vs-0.297 gap is a genuine difference between the two fields, not a cap
        /// artefact.
        ///
        /// So the limit stays generous, and the defect a big cap represents is *reported* rather
        /// than converted into a hole: watch <see cref="HoleFillStats.largestCap"/> and
        /// <see cref="HoleFillStats.loopsCapped"/>. Both should fall towards zero as the hierarchy
        /// lands and the extractor stops leaving regions unresolved.
        /// </remarks>
        public const int maxCapSize = 64;

        /// <summary>
        /// How far a chaining walk may travel before giving up on the loop closing.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="maxCapSize"/>, and separating the two is what made the real
        /// cause visible. Both were the same constant, so any loop longer than a cap may be was
        /// abandoned by the WALK — the pass never learned such loops existed, let alone how long
        /// they were, and the failure counter reported "longest=65" for every one of them, which is
        /// just the limit plus one.
        ///
        /// A loop is now followed to its end whatever its length, and the size question is answered
        /// once, at emission, where a large loop gets a centroid fan instead of a refusal.
        /// </remarks>
        public const int maxWalkLength = 8192;

        /// <summary>
        /// How far a hole's rim may depart from its own best-fit plane, as a fraction of the rim's
        /// radius, for a flat lid to be an acceptable stand-in for it.
        /// </summary>
        /// <remarks>
        /// This replaced two attempts at a size limit, both of which failed for the same reason:
        /// size does not decide the question.
        ///
        /// Measured on the reported scan (ccnb.obj) and the procedural corpus, deviation is
        /// normalised by bounding-box diagonal throughout:
        ///
        ///     ccnb, 50,000 quads   fan all: 13 boundary edges, dev 0.00014 / 0.01630
        ///                          refuse:   1,792 edges,      dev 0.00019 / 0.01630
        ///     ccnb,  8,000 quads   fan all:  0 boundary edges, dev 0.00061 / 0.02150
        ///                          refuse:     752 edges,      dev 0.00087 / 0.02467
        ///     cube,    500 quads   fan all:  0 boundary edges, quad fraction 0.921 -> 0.641
        ///     cylinder, 500 quads  fan all:  0 boundary edges, quad fraction 0.879 -> 0.602,
        ///                          and hausdorff 0.154 -> 0.204
        ///
        /// So the scan wants every rim fanned at both targets, and the coarse corpus does not want
        /// it at all. An absolute limit cannot express that. Nor can a relative one: the rim the
        /// scan wants fanned at 8,000 quads is 12.8% of the faces and the one the cube does not want
        /// fanned is 14.4% — the same ratio, opposite answers.
        ///
        /// What actually differs is whether a flat lid is near the surface it replaces. The scan's
        /// large rims bound flat ground; the cylinder's bound a curved wall, which is why fanning it
        /// made hausdorff worse rather than better. Relief against radius measures exactly that, and
        /// needs nothing but the rim.
        /// </remarks>
        public const float maxRimRelief = 0.2f;

        /// <summary>Absolute ceiling on fan size, so a pathological rim cannot allocate unboundedly.</summary>
        public const int maxFanSize = 4096;

        /// <summary>
        /// How far a proposed hub may sit from the input surface, in lattice spacings, before the
        /// loop is left open instead of capped.
        /// </summary>
        /// <remarks>
        /// This is the test <see cref="maxRimRelief"/> was standing in for, and its own note says
        /// so: "what actually differs is whether a flat lid is near the surface it replaces".
        /// Relief against radius is a proxy for that and there is one shape where the proxy is
        /// exactly wrong — a loop that goes all the way around a tube. Such a rim is perfectly
        /// FLAT, so it passes the relief gate with relief zero, and its centroid is on the tube.s
        /// axis, half a diameter inside the solid.
        ///
        /// Measured on cylinder_capped at target 2000. Two output vertices at radius 0.01 on a
        /// cylinder of radius 0.5, one at each end, each 0.49 from any surface — and worst-case
        /// deviation 0.2020 against 0.0008 without them. Everything else about the case was fine:
        /// quad fraction 0.9625, no non-manifold edges, winding consistent. Two vertices.
        ///
        /// A loop encircling a tube cannot be capped correctly by anything, so failing this test
        /// abandons the loop rather than falling back to an n-gon. That is a reversal of the
        /// standing rule here that a hole is the worse defect, and it is deliberate: a hole is
        /// visible as a hole, while a lid through the middle of the model reads as the shape being
        /// wrong. The n-gon fallback would not have helped anyway, because NgonQuadrangulator puts
        /// a hub at the centroid of whatever it is given.
        ///
        /// One and a half spacings, because the probe measures distance to the input SURFACE and
        /// a legitimate hub sits on it by construction; the slack is for the output vertex having
        /// been snapped within half a cell of its own input vertex. The failing case is fifteen
        /// spacings out, so nothing here is finely tuned.
        /// </remarks>
        public const float maxHubSurfaceDistance = 1.5f;

        /// <summary>
        /// Adds a face for every simple boundary loop that is not part of the input's own outline.
        /// </summary>
        /// <remarks>
        /// The distinction is the whole difficulty, and getting it wrong in either direction is
        /// visible. Cap nothing and an open mesh keeps every hole the extractor left. Cap everything
        /// and a scan's outline gets welded shut into one enormous flat lid.
        ///
        /// This used to take a single bool, <c>expectClosed</c>, set from
        /// <c>topology.boundaryEdgeCount == 0</c> — so on any input with a boundary it returned
        /// immediately having done nothing. A photogrammetry scan is an open surface by definition:
        /// it is a patch of terrain with an outline. Measured on the reported input, ccnb.obj at a
        /// 50,000-quad target:
        ///
        ///     input boundary edges                     4,419
        ///     output boundary edges                   23,689
        ///     boundary edges this pass even counted         0
        ///
        /// So roughly nineteen thousand boundary edges were holes the extractor had opened, the
        /// pass was the thing that existed to close them, and it never ran. "A lot of holes" was an
        /// accurate description.
        ///
        /// The test now is geometric rather than global. A loop is left alone when most of its
        /// vertices lie within <paramref name="outlineTolerance"/> of an edge of the input's own
        /// boundary; otherwise it is a hole and gets capped. Majority rather than all, because a
        /// loop that runs along the outline and then cuts inland across a hole is both, and leaving
        /// it open is the safe half.
        /// </remarks>
        public static HoleFillStats Fill(List<int[]> faces, int vertexCount, bool expectClosed,
            Vector3[] positions = null, Vector3[] outlineVertices = null,
            int[] outlineEdges = null, float outlineTolerance = 0f,
            int[] surfaceTriangles = null)
        {
            // The probe is only possible with all three of the input surface, its triangles and a
            // lattice spacing to state the limit in. Without them capping falls back to the
            // relief-against-radius test alone, which is what it used to be.
            var probe = positions != null && outlineVertices != null && surfaceTriangles != null
                        && outlineTolerance > 0f
                ? new SurfaceProbe(outlineVertices, surfaceTriangles,
                    maxHubSurfaceDistance * outlineTolerance)
                : null;
            var stats = new HoleFillStats();
            if (faces == null || faces.Count == 0) return stats;

            // Closed input: every boundary loop is a hole, no geometry needed.
            // Open input: capping is still wanted, but only away from the real outline, and that
            // needs positions and the outline to compare against.
            var haveOutline = positions != null && outlineVertices != null && outlineEdges != null
                              && outlineEdges.Length >= 2 && outlineTolerance > 0f;

            if (!expectClosed && !haveOutline) return stats;

            var outlineGrid = haveOutline
                ? new OutlineGrid(outlineVertices, outlineEdges, outlineTolerance)
                : null;

            // --- undirected edge INCIDENCE, plus the direction the owning face used ---
            //
            // Counted, not set-membership. The first version of this used a HashSet of directed
            // edges and treated "reverse not present" as boundary. That is wrong wherever winding
            // is inconsistent: two faces traversing the same edge in the SAME direction collapse to
            // one set entry, the reverse is absent, and the edge is misread as a boundary. Capping
            // it then puts a third face on an edge that already had two — a non-manifold edge.
            //
            // Measured: that produced Euler 8 on a single connected closed sphere, which is
            // impossible for a manifold surface and is what gave the bug away.
            var incidence = new Dictionary<long, int>(EdgeKey.Comparer);
            var direction = new Dictionary<long, long>(EdgeKey.Comparer);

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;
                for (var i = 0; i < face.Length; i++)
                {
                    var a = face[i];
                    var b = face[(i + 1) % face.Length];
                    var undirected = EdgeKey.PackUndirected(a, b);

                    incidence.TryGetValue(undirected, out var count);
                    incidence[undirected] = count + 1;
                    direction[undirected] = Key(a, b);
                }
            }

            // A boundary edge is one exactly one face touches, whatever direction it went. The cap
            // must traverse it the opposite way to wind consistently with that face, so the
            // successor map is keyed target -> origin of the owning face's traversal.
            var successor = new Dictionary<int, List<int>>();


            foreach (var pair in incidence)
            {
                if (pair.Value != 1) continue;

                Unkey(direction[pair.Key], out var a, out var b);

                stats.boundaryEdgesFound++;

                if (!successor.TryGetValue(b, out var outgoing))
                {
                    outgoing = new List<int>(2);
                    successor.Add(b, outgoing);
                }
                outgoing.Add(a);

            }

            if (stats.boundaryEdgesFound == 0) return stats;

            // --- chain the boundary edges into loops ---
            //
            // NON-DESTRUCTIVE, and this is load-bearing. The first version marked each edge consumed
            // as it stepped onto it, before knowing whether the walk would close — so a failed walk
            // permanently poisoned its edges and no later walk could ever use them. That is
            // precisely the defect documented in FaceTracer's header as DEFECT 2, reproduced here
            // after having been written down. Measured on a subdivided cube: 5 loops capped and 82
            // boundary edges still left open.
            //
            // A walk now collects its edges in a scratch list and commits only on success.

            var addedVertices = stats.addedPositions;
            var consumed = new HashSet<long>(EdgeKey.Comparer);
            var loop = new List<int>(16);
            var walkEdges = new List<long>(16);

            foreach (var entry in successor)
            {
                foreach (var firstTarget in entry.Value)
                {
                    var start = entry.Key;
                    if (consumed.Contains(Key(start, firstTarget))) continue;

                    loop.Clear();
                    walkEdges.Clear();

                    var current = start;
                    var next = firstTarget;
                    var closed = false;

                    while (loop.Count <= maxWalkLength)
                    {
                        loop.Add(current);
                        walkEdges.Add(Key(current, next));

                        if (next == start) { closed = true; break; }
                        // First successor neither already committed nor used earlier in this same
                        // walk. At a pinch vertex there is more than one and either choice yields a
                        // valid sub-loop; the within-walk check is what stops it doubling back.
                        var advanced = false;

                        if (successor.TryGetValue(next, out var candidates))
                        {
                            foreach (var candidate in candidates)
                            {
                                var edge = Key(next, candidate);
                                if (consumed.Contains(edge)) continue;
                                if (walkEdges.Contains(edge)) continue;

                                current = next;
                                next = candidate;
                                advanced = true;
                                break;
                            }
                        }

                        // No undirected fallback here, and that is a measured decision rather than
                        // an omission.
                        //
                        // A dead-ended walk looked like the obvious cause of the holes that
                        // survived this pass, so a fallback onto undirected boundary adjacency was
                        // written to cross winding breaks — it was safe to do, because ToMesh
                        // decides every face's orientation for itself against the vertex normals.
                        // Measured on the reported scan it moved the abandoned count by 15 out of
                        // 2,455, and the counters added afterwards said why: dead ends are exactly
                        // 0 of 7,031 failures. Every one is a loop longer than a cap may be. The
                        // fallback was deleted; the size limit is the real cause and is handled at
                        // emission instead.
                        if (!advanced) break;

                    }
                    if (!closed || loop.Count < 3)
                    {
                        if (loop.Count > 0)
                        {
                            stats.loopsAbandoned++;

                            // Which of the two reasons: the walk ran out of edges, or the loop was
                            // simply bigger than a cap is allowed to be. These want different fixes
                            // and the single counter could not tell them apart, which is how an
                            // undirected-chaining fix for the first one got written and measured
                            // before anyone checked it was the problem. It was not: it moved the
                            // count by 15 out of 2,455.
                            if (loop.Count > maxWalkLength) stats.abandonedTooLong++;
                            else stats.abandonedDeadEnd++;

                            if (loop.Count > stats.longestAbandoned)
                                stats.longestAbandoned = loop.Count;
                        }
                        continue;                       // edges stay available for another walk
                    }

                    // The input's own outline is not a hole. Consume its edges so no later walk
                    // retries them, but emit nothing.
                    if (haveOutline && IsOnInputOutline(loop, positions, outlineGrid,
                            outlineTolerance))
                    {
                        foreach (var edge in walkEdges) consumed.Add(edge);
                        stats.loopsLeftOpen++;
                        if (fillOutlineNotches)
                            FillOutlineNotches(new List<int>(loop), faces, vertexCount, stats, addedVertices,
                                positions, outlineVertices, outlineEdges, outlineGrid, outlineTolerance,
                                probe, incidence);
                        continue;
                    }

                    foreach (var edge in walkEdges) consumed.Add(edge);
                    EmitSimpleLoops(loop, faces, vertexCount, stats, addedVertices, positions, probe, incidence);
                }
            }
            stats.verticesAdded = addedVertices.Count;

            // Recount from the final face set: this is the number the user sees as holes, and it
            // is not derivable from the counters above because a failed walk retries the same edges
            // many times and a cap changes the incidence of every edge it covers.
            {
                var after = new Dictionary<long, int>(EdgeKey.Comparer);
                foreach (var face in faces)
                {
                    if (face == null || face.Length < 3) continue;
                    for (var i = 0; i < face.Length; i++)
                    {
                        var key = EdgeKey.PackUndirected(face[i], face[(i + 1) % face.Length]);
                        after.TryGetValue(key, out var c);
                        after[key] = c + 1;
                    }
                }
                foreach (var c in after.Values) if (c == 1) stats.edgesLeftUncovered++;
            }


            return stats;
        }

        /// <summary>
        /// Emits <paramref name="loop"/> as one face, or splits it at repeated vertices first.
        /// </summary>
        static void EmitSimpleLoops(List<int> loop, List<int[]> faces, int vertexCount,
            HoleFillStats stats, List<Vector3> addedVertices, Vector3[] positions,
            SurfaceProbe probe, Dictionary<long, int> incidence)
        {
            var seenAt = new Dictionary<int, int>(loop.Count);
            var simple = true;

            for (var i = 0; i < loop.Count; i++)
            {
                if (seenAt.ContainsKey(loop[i])) { simple = false; break; }
                seenAt.Add(loop[i], i);
            }

            if (simple)
            {
                Emit(loop, 0, loop.Count, faces, vertexCount, stats, addedVertices, positions, probe, incidence);
                return;
            }

            // Split at the first repeat: the span between the two occurrences is itself a closed
            // simple cycle, so peel it off and reconsider the remainder.
            stats.loopsSplit++;

            var remaining = new List<int>(loop);
            var guard = 0;

            while (remaining.Count >= 3 && guard++ < maxWalkLength)
            {
                seenAt.Clear();
                var repeatAt = -1;
                var firstAt = -1;

                for (var i = 0; i < remaining.Count; i++)
                {
                    if (seenAt.TryGetValue(remaining[i], out var earlier))
                    {
                        firstAt = earlier;
                        repeatAt = i;
                        break;
                    }
                    seenAt.Add(remaining[i], i);
                }

                if (repeatAt < 0)
                {
                    Emit(remaining, 0, remaining.Count, faces, vertexCount, stats, addedVertices, positions, probe, incidence);
                    return;
                }

                Emit(remaining, firstAt, repeatAt - firstAt, faces, vertexCount, stats, addedVertices,
                    positions, probe, incidence);
                remaining.RemoveRange(firstAt, repeatAt - firstAt);
            }

            if (remaining.Count >= 3) stats.loopsAbandoned++;
        }

        /// <summary>
        /// Emits a span of <paramref name="loop"/> as one face, or as a centroid fan when it is too
        /// large for a single face to be reasonable geometry.
        /// </summary>
        /// <remarks>
        /// The size limit used to be a refusal, and the refusal was the whole remaining hole
        /// problem. Measured on the reported scan at a 50,000-quad target: every one of 7,031 failed
        /// caps failed for length, none for any other reason, leaving 6,771 boundary edges open.
        ///
        /// A refusal is defensible when the alternative is one enormous near-flat n-gon spanning a
        /// region with creases running through it — that is what the limit was written to prevent,
        /// and it is a real concern. A centroid fan is not that. It adds one vertex at the loop's
        /// average position and joins it to every rim edge, so the hole closes with triangles whose
        /// size matches the rim rather than with a single face that ignores it.
        ///
        /// The cost is honest and shows up in the metrics rather than hiding: a fan is triangles, so
        /// quad fraction falls by roughly the number of rim edges it consumed. Holes are worse than
        /// triangles — a missing face deviates from the source surface more than a flat one does,
        /// which was measured separately when the cap limit was last tuned.
        /// </remarks>
        static void Emit(List<int> loop, int offset, int length, List<int[]> faces,
            int vertexCount, HoleFillStats stats, List<Vector3> addedVertices,
            Vector3[] positions, SurfaceProbe probe, Dictionary<long, int> incidence)
        {
            if (length < 3)
            {
                stats.loopsAbandoned++;
                return;
            }

            for (var i = 0; i < length; i++)
            {
                var v = loop[offset + i];
                if (v < 0 || v >= vertexCount + addedVertices.Count)
                {
                    stats.loopsAbandoned++;
                    return;
                }
            }
            // A triangle and a quad are already what we want; emit directly.
            if (length == 3 || length == 4)
            {
                var small = new int[length];
                for (var i = 0; i < length; i++) small[i] = loop[offset + i];

                faces.Add(small);
                stats.loopsCapped++;
                stats.largestCap = Mathf.Max(stats.largestCap, length);
                return;
            }

            // Everything larger is quadrangulated rather than emitted as one n-gon.
            //
            // This is the fix for the most visible defect in the output. A cap used to be a single
            // face of up to 64 corners, and QuadExtractor.ToMesh fan-triangulates anything over four
            // for display — so a 44-corner cap rendered as 42 long thin triangles radiating from one
            // vertex. That starburst is what the wireframe screenshots show, and the reference
            // retopology of the same asset contains no n-gon at all: {3: 389, 4: 5085}.
            //
            // One interior vertex at the centroid, then adjacent rim edges paired into quads. An
            // even rim needs nothing else and yields length/2 quads with no triangles; an odd rim
            // leaves exactly one triangle, which is unavoidable without splitting a rim edge, and
            // splitting one would mean editing the neighbouring face that shares it.
            //
            // The hub takes valence length/2, so a 44-corner cap still leaves one valence-22 vertex.
            // That is a real cost and it is the reason this is a mitigation rather than the answer:
            // the answer is for the tracer not to leave 1,226 edges uncovered in the first place.
            // Halving the rim into a proper inner ring instead would bound the valence, but the
            // naive construction leaves a triangle between every pair of adjacent quads — length/2
            // quads AND length/2 triangles — which is strictly worse than this.
            if (positions == null)
            {
                stats.loopsAbandoned++;
                return;
            }

            var centroid = Vector3.zero;
            var counted = 0;
            for (var i = 0; i < length; i++)
            {
                var v = loop[offset + i];
                if (v >= positions.Length) continue;
                centroid += positions[v];
                counted++;
            }

            if (counted == 0)
            {
                stats.loopsAbandoned++;
                return;
            }

            centroid /= counted;

            // Rim relief: how far the rim departs from its own best-fit plane, against how wide it
            // is. The plane's normal comes from Newell's method over the rim polygon, which is
            // stable on a non-planar loop where a cross product of any three vertices is not.
            var planeNormal = Vector3.zero;
            for (var i = 0; i < length; i++)
            {
                var c = positions[loop[offset + i]];
                var n = positions[loop[offset + (i + 1) % length]];
                planeNormal.x += (c.y - n.y) * (c.z + n.z);
                planeNormal.y += (c.z - n.z) * (c.x + n.x);
                planeNormal.z += (c.x - n.x) * (c.y + n.y);
            }

            var relief = 0f;
            var radius = 0f;

            if (planeNormal.sqrMagnitude > 1e-20f)
            {
                // SafeNormalize: this is a Newell sum, so its magnitude is twice the rim polygon area.
                // Under .normalized a small rim would silently become a zero normal, every relief
                // measurement below would read exactly 0, and the flatness test that decides which
                // rims may be fanned would pass every one of them. See MeshMath.
                planeNormal = MeshMath.SafeNormalize(planeNormal, Vector3.up);

                for (var i = 0; i < length; i++)
                {
                    var d = positions[loop[offset + i]] - centroid;
                    relief = Mathf.Max(relief, Mathf.Abs(Vector3.Dot(planeNormal, d)));
                    radius = Mathf.Max(radius, d.magnitude);
                }
            }
            else
            {
                // Degenerate rim polygon: no plane to measure against, so fall back rather than
                // guess a hub position.
                EmitAsNgon(loop, offset, length, faces, stats);
                return;
            }

            // The geometry gate. An interior vertex at the centroid stands in for whatever the rim
            // actually covered, so it is only fair where the rim is flat relative to its own width —
            // see maxRimRelief, which was established by measurement and separates the scan's flat
            // ground from a cube's creased rims.
            //
            // Failing it is NOT a refusal any more, except above maxFanSize. A cap that used to be
            // emitted as one n-gon is still emitted as one n-gon, because refusing it would leave a
            // hole and holes are the worse defect — this change is only allowed to convert existing
            // faces into quads, never to remove one.
            // And the direct test: is the hub anywhere near the surface it is standing in for? A
            // loop that encircles a tube is perfectly flat, so it passes every gate above, and
            // its centroid is on the axis. See maxHubSurfaceDistance.
            //
            // Abandoned rather than fallen back to an n-gon, which is the one place this pass
            // prefers a hole: a lid through the middle of the model is not a worse-looking hole,
            // it is the shape being wrong, and the n-gon path would put a hub there anyway.
            //
            // And when it passes, the hub is MOVED to that nearest surface point rather than left
            // at the centroid. The centroid of a rim is on the rim's chord, so on any curved region
            // it sits inside the surface by the sagitta of the cap — the lid dishes inward, its
            // faces tilt away from the surface around them, and the whole patch shades as a flat
            // spot against a smooth neighbourhood. That is the "large uniform light regions" in the
            // screenshots, and it is a geometry defect being read as a shading one.
            //
            // Projection cannot make it worse anywhere the gate already accepts: a rim over flat
            // ground has its centroid on the surface already, so the move is a no-op, and the move
            // is bounded by maxHubSurfaceDistance in any case. A rim encircling a tube never gets
            // here, because the gate rejects it first.
            var hubPosition = centroid;
            if (probe != null && !probe.Project(centroid, out hubPosition))
            {
                stats.loopsOffSurface++;
                stats.loopsAbandoned++;
                return;
            }

            if (radius <= 1e-20f || relief > maxRimRelief * radius || length > maxFanSize)
            {
                stats.refusedSizes.Add(length);

                if (length <= maxCapSize)
                {
                    EmitAsNgon(loop, offset, length, faces, stats);
                    return;
                }

                stats.loopsAbandoned++;
                return;
            }

            // A GRID first, and a hub only when a grid will not fit.
            //
            // This is the fix for what the wireframe shows as suns: circular bursts of edges
            // radiating from a point, scattered across the surface. A hub pie leaves exactly one
            // vertex of valence length/2, so a 67-corner rim leaves a valence-33 singularity, and
            // that IS the sun — it is a topology defect that happens to be visible rather than a
            // shading one. The reference retopology of the same asset has none, because a hole
            // there is closed with a grid whose interior vertices are all valence 4 and whose only
            // irregularity is at four corners.
            //
            // Attempted before the hub and falling back to it, never replacing a hub with a hole:
            // this pass's standing rule is that a hole is the worse defect, and nothing here is
            // allowed to reintroduce one.
            if (TryGridFill(loop, offset, length, faces, vertexCount, stats, addedVertices,
                    positions, probe, centroid, incidence))
                return;

            var hub = vertexCount + addedVertices.Count;
            addedVertices.Add(hubPosition);
            stats.largestHub = Mathf.Max(stats.largestHub, length);

            // Pair adjacent rim edges into quads: hub, v[i], v[i+1], v[i+2]. Each quad consumes two
            // rim edges, so the whole rim is covered in length/2 steps, and the winding matches the
            // fan it replaces (hub first, then forward along the rim).
            var emittedQuads = 0;
            var i0 = 0;
            while (i0 + 2 <= length)
            {
                var a = loop[offset + i0];
                var b = loop[offset + (i0 + 1) % length];
                var c = loop[offset + (i0 + 2) % length];

                if (a != b && b != c && a != c)
                {
                    faces.Add(new[] { hub, a, b, c });
                    emittedQuads++;
                }

                i0 += 2;
            }

            // An odd rim has one edge left over, which can only be a triangle without splitting a
            // rim edge shared with a neighbouring face.
            if (i0 < length)
            {
                var a = loop[offset + i0];
                var b = loop[offset + (i0 + 1) % length];
                if (a != b) faces.Add(new[] { hub, a, b });
            }

            stats.fanSizes.Add(length);
            stats.loopsQuadrangulated++;
            stats.quadsFromCaps += emittedQuads;
            stats.largestCap = Mathf.Max(stats.largestCap, length);
        }

        /// <summary>
        /// Whether a proposed hub lies on the input surface.
        /// </summary>
        /// <remarks>
        /// Distance to the input SURFACE, not to its nearest vertex: a rim can sit in the middle of
        /// a large input triangle, and a vertex-only query would read that as far away and refuse a
        /// perfectly good cap. SpatialGrid.NearestDistance already does the triangle version.
        /// </remarks>
        sealed class SurfaceProbe
        {
            readonly SpatialGrid m_Grid;
            readonly Vector3[] m_Vertices;
            readonly int[] m_Triangles;
            readonly float m_Limit;

            public SurfaceProbe(Vector3[] vertices, int[] triangles, float limit)
            {
                m_Grid = new SpatialGrid(vertices, triangles);
                m_Vertices = vertices;
                m_Triangles = triangles;
                m_Limit = limit;
            }

            public bool OnSurface(Vector3 point) =>
                m_Grid.NearestDistance(point, out _) <= m_Limit;

            /// <summary>
            /// The gate and the closest surface point in one query, since the gate already found it.
            /// </summary>
            /// <remarks>
            /// The caller wants both and the sweep is the expensive part, so asking twice would
            /// double the cost of every cap for a value already computed.
            /// </remarks>
            public bool Project(Vector3 point, out Vector3 projected)
            {
                projected = point;

                var distance = m_Grid.NearestDistance(point, out var triangle);
                if (triangle < 0) return false;

                // The projection is computed WHETHER OR NOT the gate passes, and the return value
                // reports only the gate. Those are two separate questions: "where is the surface"
                // always has an answer, while "was this point close enough to trust" is a judgement
                // about the construction that produced it. Conflating them is what let TryGridFill
                // keep raw Coons points that had failed the gate, and those are the faces standing
                // off the model in the reported renders.
                projected = SpatialGrid.ClosestPointOnTriangle(point,
                    m_Vertices[m_Triangles[triangle * 3]],
                    m_Vertices[m_Triangles[triangle * 3 + 1]],
                    m_Vertices[m_Triangles[triangle * 3 + 2]]);

                return distance <= m_Limit;
            }
        }

        /// <summary>
        /// Smallest rim worth gridding. Below this a hub leaves valence 3 or 4, which is an ordinary
        /// quad-mesh singularity and not a defect, so a grid would add vertices for nothing.
        /// </summary>
        public const int minGridRim = 8;

        /// <summary>
        /// Closes a rim with an interior quad grid — the construction a retopology artist uses, and
        /// the one the reference mesh uses, instead of a fan around a single centre.
        /// </summary>
        /// <remarks>
        /// The rim is split into four sides of lengths a, b, a, b with a + b = length/2, giving an
        /// a x b grid of quads whose boundary IS the rim, so no rim edge is touched and the
        /// neighbouring faces that share those edges never have to be edited.
        ///
        /// Interior positions come from a Coons patch — the bilinearly blended sum of the four
        /// sides, minus the bilinear surface through the four corners — which is the standard way to
        /// fill a curved quadrilateral boundary and reproduces the rim's curvature rather than
        /// flattening to a chord. Each interior point is then projected onto the input surface, the
        /// same projection the hub now gets.
        ///
        /// Valence, which is the whole point. A hub pie on a rim of n leaves ONE vertex of valence
        /// n/2. A grid leaves (a-1)(b-1) interior vertices, every one of them valence 4, and the
        /// only irregular vertices are the four corners at valence 3. For n = 32 that is a
        /// valence-16 singularity against four valence-3 corners.
        ///
        /// An ODD rim cannot bound a grid — opposite sides would have to differ by one — so one
        /// corner of it is closed with a single triangle first and the remaining even rim is
        /// gridded. That triangle sits ON the rim rather than in the interior, which is where a
        /// stray triangle is least visible, and it costs one triangle against a star of n/2 quads.
        /// Measured before this: the reference asset still carried hubs of rim 25 to 33, every one
        /// of them an odd rim the even-only version had refused.
        /// </remarks>
        static bool TryGridFill(List<int> loop, int offset, int length, List<int[]> faces,
            int vertexCount, HoleFillStats stats, List<Vector3> addedVertices,
            Vector3[] positions, SurfaceProbe probe, Vector3 centroid,
            Dictionary<long, int> incidence)
        {
            if (length < minGridRim) return false;

            // A rim that visits the same vertex twice cannot bound a grid. As one n-gon, or as a
            // fan, every edge still appears once; laid out as a grid the repeated vertex ends up on
            // two different grid corners and the edges through it land in more than two faces.
            // Measured after the first version omitted this: inconsistentEdges went 0 -> 2 on the
            // reference asset, which is the same correctness regression NgonQuadrangulator's own
            // guard exists to prevent, and it is why that guard is there.
            for (var i = 0; i < length; i++)
            {
                for (var j = i + 1; j < length; j++)
                {
                    if (loop[offset + i] != loop[offset + j]) continue;

                    stats.gridRejected++;
                    return false;
                }
            }

            // An odd rim: clip one ear to make it even, then grid the rest. The ear is taken at the
            // corner where the rim turns most sharply, because that is the corner a grid would fit
            // worst anyway and the one whose triangle reads as part of the rim rather than as a
            // defect in the middle of a patch.
            var earAt = -1;
            if ((length & 1) != 0)
            {
                // Clipping an ear leaves a CHORD between the ear's two neighbours, and that chord is
                // the only edge this whole construction creates between two vertices that already
                // existed — every other grid edge is either a rim edge or touches a new interior
                // vertex. So if the chord is already an edge somewhere in the mesh, emitting it puts
                // that edge in four faces.
                //
                // Measured: the first version picked the sharpest corner unconditionally and
                // inconsistentEdges went 0 -> 2 on the reference asset. Two edges of eleven
                // thousand, and still a non-manifold result that nothing downstream can repair.
                //
                // So the corners are considered sharpest-first and the first one with a free chord
                // wins; if every chord is taken the rim goes to a hub instead.
                var order = new List<int>(length);
                for (var i = 0; i < length; i++) order.Add(i);

                order.Sort((x, y) =>
                {
                    float Turn(int i)
                    {
                        var previous = positions[loop[offset + (i + length - 1) % length]];
                        var here = positions[loop[offset + i]];
                        var next = positions[loop[offset + (i + 1) % length]];

                        // +1 is a straight run, -1 a spike; smaller is sharper.
                        return MeshMath.CosAngleBetween(previous - here, next - here);
                    }

                    return Turn(x).CompareTo(Turn(y));
                });

                foreach (var candidate in order)
                {
                    var before = loop[offset + (candidate + length - 1) % length];
                    var after = loop[offset + (candidate + 1) % length];
                    if (before == after) continue;

                    if (incidence != null &&
                        incidence.ContainsKey(EdgeKey.PackUndirected(before, after))) continue;

                    earAt = candidate;
                    break;
                }

                if (earAt < 0)
                {
                    stats.gridRejected++;
                    return false;
                }
            }

            // Rotate so the ear is at the end, then work on the even prefix.
            var start = earAt < 0 ? 0 : (earAt + 1) % length;
            var rimLength = earAt < 0 ? length : length - 1;

            var half = rimLength / 2;

            // Squarest split, because a long thin grid has the same aspect problem a fan has: pick
            // a as close to half/2 as possible and let b take the rest.
            var a = Mathf.Max(1, half / 2);
            var b = half - a;
            if (a < 1 || b < 1) return false;

            // Indexes the EVEN sub-rim, rotated so it starts just past the ear, and wrapping within
            // itself rather than within the original loop — so Rim(rimLength) is Rim(0) and never
            // the ear vertex.
            int RimIndex(int i)
            {
                var k = ((i % rimLength) + rimLength) % rimLength;
                return loop[offset + (start + k) % length];
            }

            Vector3 Rim(int i) => positions[RimIndex(i)];

            // Grid corners, in the rim's own winding order, so the emitted quads inherit it.
            var c00 = Rim(0);
            var c10 = Rim(a);
            var c11 = Rim(a + b);
            var c01 = Rim(2 * a + b);

            // index[i, j] over the (a+1) x (b+1) lattice. Boundary entries are rim vertices and are
            // reused; interior entries are new.
            var index = new int[(a + 1) * (b + 1)];
            int At(int i, int j) => i * (b + 1) + j;

            for (var i = 0; i <= a; i++) index[At(i, 0)] = RimIndex(i);
            for (var j = 0; j <= b; j++) index[At(a, j)] = RimIndex(a + j);
            for (var i = 0; i <= a; i++) index[At(i, b)] = RimIndex(2 * a + b - i);
            for (var j = 0; j <= b; j++) index[At(0, j)] = RimIndex(rimLength - j);

            var interior = new List<Vector3>((a - 1) * (b - 1));
            var interiorAt = new List<int>((a - 1) * (b - 1));
            var offSurface = 0;

            for (var i = 1; i < a; i++)
            {
                for (var j = 1; j < b; j++)
                {
                    var u = i / (float)a;
                    var v = j / (float)b;

                    // Coons: the two ruled surfaces through opposite side pairs, minus the bilinear
                    // patch through the corners that both of them already contain.
                    var edgeU = (1f - v) * Rim(i) + v * Rim(2 * a + b - i);
                    var edgeV = (1f - u) * Rim(rimLength - j) + u * Rim(a + j);
                    var corners = (1f - u) * (1f - v) * c00 + u * (1f - v) * c10
                                  + (1f - u) * v * c01 + u * v * c11;

                    var point = edgeU + edgeV - corners;

                    if (probe != null)
                    {
                        // Snap to the surface unconditionally. A Coons point that missed the gate is
                        // still a point the grid needs, and the nearest surface point is the best
                        // available answer for it; leaving it at its raw blended position instead put
                        // a vertex off the model, which reads as a spike or a plate through the
                        // shape. The gate result is kept only as the count that decides whether the
                        // whole grid is the wrong construction for this rim.
                        if (!probe.Project(point, out var projected)) offSurface++;
                        point = projected;
                    }

                    interior.Add(point);
                    interiorAt.Add(At(i, j));
                }
            }

            // A grid whose interior mostly misses the surface is describing a region this
            // construction does not fit — a saddle, or a rim that is not really a quadrilateral.
            // Hand those back to the hub rather than emit geometry through the model.
            if (interior.Count > 0 && offSurface * 2 > interior.Count)
            {
                stats.gridRejected++;
                return false;
            }

            var first = vertexCount + addedVertices.Count;
            for (var k = 0; k < interior.Count; k++)
            {
                index[interiorAt[k]] = first + k;
                addedVertices.Add(interior[k]);
            }

            var emitted = 0;
            for (var i = 0; i < a; i++)
            {
                for (var j = 0; j < b; j++)
                {
                    var v00 = index[At(i, j)];
                    var v10 = index[At(i + 1, j)];
                    var v11 = index[At(i + 1, j + 1)];
                    var v01 = index[At(i, j + 1)];

                    // A rim that pinches can put the same vertex on two grid corners; that quad
                    // would be degenerate, and skipping it is safer than emitting it.
                    if (v00 == v10 || v10 == v11 || v11 == v01 || v01 == v00) continue;

                    faces.Add(new[] { v00, v10, v11, v01 });
                    emitted++;
                }
            }

            if (emitted == 0)
            {
                // Nothing was emitted, so give the interior slots back rather than leave orphans.
                addedVertices.RemoveRange(first - vertexCount, interior.Count);
                stats.gridRejected++;
                return false;
            }

            // The ear, last, so it is only emitted once the grid it completes is certain. It shares
            // its closing edge with the grid's own closing edge, traversed the other way, so the
            // pair is manifold.
            if (earAt >= 0)
            {
                var ear = loop[offset + earAt];
                var previous = loop[offset + (earAt + length - 1) % length];
                var next = loop[offset + start];

                if (ear != previous && ear != next && previous != next)
                {
                    faces.Add(new[] { previous, ear, next });
                    stats.trianglesFromGridEars++;

                    // Record the chord, so a later rim cannot pick an ear that recreates it.
                    if (incidence != null)
                        incidence[EdgeKey.PackUndirected(previous, next)] = 2;
                }
            }

            stats.loopsGridFilled++;
            stats.quadsFromGrids += emitted;
            stats.gridVertices += interior.Count;
            stats.loopsCapped++;
            stats.largestGrid = Mathf.Max(stats.largestGrid, length);
            stats.largestCap = Mathf.Max(stats.largestCap, length);

            return true;
        }

        /// <summary>
        /// Last-resort cap: one face with as many corners as the rim, which is what this stage did
        /// for every rim before quadrangulation. Keeps the surface closed when a hub would be
        /// geometrically dishonest.
        /// </summary>
        static void EmitAsNgon(List<int> loop, int offset, int length, List<int[]> faces,
            HoleFillStats stats)
        {
            var cap = new int[length];
            for (var i = 0; i < length; i++) cap[i] = loop[offset + i];

            faces.Add(cap);
            stats.loopsCapped++;
            stats.ngonCaps++;
            stats.largestCap = Mathf.Max(stats.largestCap, length);
        }


        /// <summary>
        /// Is this loop part of the input's own boundary rather than a hole the extractor opened?
        /// </summary>
        /// <remarks>
        /// Majority vote over the loop's vertices, not unanimity. A loop that follows the outline
        /// for a stretch and then cuts inland around a hole is genuinely both, and the conservative
        /// half is to leave it open — a reported hole is a defect the user can see and measure,
        /// where an outline welded shut is a silent change to what the mesh *is*.
        /// </remarks>
        /// <summary>
        /// Close the notches in a loop that is the input's outline: stretches of it running well
        /// inside the outline, where the extractor lost a strip along the edge.
        /// </summary>
        /// <remarks>
        /// A hole touching the outline is not a loop of its own -- it opens into the outline loop,
        /// which is rightly left open, so the hole was never capped. Measured on ccnb, the quad
        /// layout gave no grid to one strip along the slanted east edge (a two-sided patch between
        /// two border corners) and the scan came out with an 8 m notch: 0.47 % of its area
        /// uncovered and Hausdorff 0.0134 -> 0.0207. Each such stretch is closed along the input's
        /// own outline, resampled at the stretch's edge length, and capped like any hole. Off: the
        /// notch stays open.
        /// </remarks>
        public static bool fillOutlineNotches = true;

        static void FillOutlineNotches(List<int> loop, List<int[]> faces, int vertexCount,
            HoleFillStats stats, List<Vector3> addedVertices, Vector3[] positions,
            Vector3[] outlineVertices, int[] outlineEdges, OutlineGrid outline, float tolerance,
            SurfaceProbe probe, Dictionary<long, int> incidence)
        {
            var n = loop.Count;
            if (n < 4) return;
            Vector3 Pos(int v) => v < vertexCount ? positions[v] : addedVertices[v - vertexCount];
            var near = new bool[n];
            var anyNear = false;
            for (var i = 0; i < n; i++)
            {
                near[i] = outline.WithinSqr(Pos(loop[i]), tolerance * tolerance);
                anyNear |= near[i];
            }
            if (!anyNear) return;

            // The outline as a graph, for paths along it.
            Dictionary<int, List<int>> graph = null;

            // Runs of far vertices, from a near vertex to the next.
            var start = 0;
            while (!near[start]) start++;
            for (var step = 0; step < n;)
            {
                var a = (start + step) % n;
                if (!near[a]) { step++; continue; }
                var run = 0;
                while (run + 1 < n && !near[(a + 1 + run) % n]) run++;
                step += run + 1;
                if (run < 2) continue;
                var b = (a + run + 1) % n;
                if (run > 4 * maxCapSize) continue;

                graph ??= OutlineGraph(outlineEdges);
                var anchorA = loop[a];
                var anchorB = loop[b];
                var path = OutlinePath(graph, outlineVertices, Pos(anchorB), Pos(anchorA),
                    3f * (Pos(anchorA) - Pos(anchorB)).magnitude + 4f * tolerance);
                if (path == null) continue;

                // The new outline points, at the stretch's own edge length.
                var edgeSum = 0f;
                for (var k = 0; k <= run; k++)
                    edgeSum += (Pos(loop[(a + k + 1) % n]) - Pos(loop[(a + k) % n])).magnitude;
                var spacing = Mathf.Max(1e-6f, edgeSum / (run + 1));
                var total = 0f;
                for (var k = 1; k < path.Count; k++) total += (path[k] - path[k - 1]).magnitude;
                var pieces = Mathf.Max(1, Mathf.RoundToInt(total / spacing));

                var hole = new List<int>(run + 2 + pieces);
                for (var k = 0; k <= run + 1; k++) hole.Add(loop[(a + k) % n]);
                var firstNew = addedVertices.Count;
                for (var p = 1; p < pieces; p++)
                {
                    addedVertices.Add(PointAlong(path, total * p / pieces));
                    hole.Add(vertexCount + addedVertices.Count - 1);
                }

                // Emit reads rim positions from one array, so the rim's new points go in with it.
                var extended = new Vector3[vertexCount + addedVertices.Count];
                Array.Copy(positions, extended, Math.Min(positions.Length, vertexCount));
                for (var k = 0; k < addedVertices.Count; k++) extended[vertexCount + k] = addedVertices[k];
                var facesBefore = faces.Count;
                EmitSimpleLoops(hole, faces, vertexCount, stats, addedVertices, extended, probe, incidence);
                if (faces.Count == facesBefore)
                {
                    // Nothing capped it: take the outline points back.
                    addedVertices.RemoveRange(firstNew, addedVertices.Count - firstNew);
                    continue;
                }
                stats.outlineNotchesFilled++;
            }
        }

        static Dictionary<int, List<int>> OutlineGraph(int[] edges)
        {
            var graph = new Dictionary<int, List<int>>();
            for (var e = 0; e + 1 < edges.Length; e += 2)
            {
                int a = edges[e], b = edges[e + 1];
                (graph.TryGetValue(a, out var la) ? la : graph[a] = new List<int>(2)).Add(b);
                (graph.TryGetValue(b, out var lb) ? lb : graph[b] = new List<int>(2)).Add(a);
            }
            return graph;
        }

        // The shortest way along the outline from the outline vertex nearest `from` to the one
        // nearest `to`, as points from `from` to `to`; null if longer than maxLength or none.
        static List<Vector3> OutlinePath(Dictionary<int, List<int>> graph, Vector3[] vertices,
            Vector3 from, Vector3 to, float maxLength)
        {
            int Nearest(Vector3 p)
            {
                var best = -1;
                var bestSqr = float.PositiveInfinity;
                foreach (var v in graph.Keys)
                {
                    var d = (vertices[v] - p).sqrMagnitude;
                    if (d < bestSqr) { bestSqr = d; best = v; }
                }
                return best;
            }
            var s = Nearest(from);
            var t = Nearest(to);
            if (s < 0 || t < 0) return null;
            var dist = new Dictionary<int, float> { [s] = 0f };
            var prev = new Dictionary<int, int>();
            var open = new SortedSet<(float d, int v)> { (0f, s) };
            while (open.Count > 0)
            {
                var (d, v) = open.Min;
                open.Remove(open.Min);
                if (v == t) break;
                if (d > maxLength) return null;
                foreach (var w in graph[v])
                {
                    var nd = d + (vertices[w] - vertices[v]).magnitude;
                    if (dist.TryGetValue(w, out var old) && old <= nd) continue;
                    if (dist.ContainsKey(w)) open.Remove((old, w));
                    dist[w] = nd;
                    prev[w] = v;
                    open.Add((nd, w));
                }
            }
            if (!dist.ContainsKey(t) || dist[t] > maxLength) return null;
            var path = new List<Vector3>();
            for (var v = t; ; v = prev[v])
            {
                path.Add(vertices[v]);
                if (v == s) break;
            }
            path.Reverse();
            path[0] = from;
            path[path.Count - 1] = to;
            if (path.Count == 1) path.Add(to);
            return path;
        }

        static Vector3 PointAlong(List<Vector3> path, float s)
        {
            for (var k = 1; k < path.Count; k++)
            {
                var len = (path[k] - path[k - 1]).magnitude;
                if (s <= len || k == path.Count - 1)
                    return Vector3.Lerp(path[k - 1], path[k], len > 0f ? Mathf.Clamp01(s / len) : 0f);
                s -= len;
            }
            return path[path.Count - 1];
        }

        /// <summary>
        /// The same "is this loop the input's own outline" test <see cref="Fill"/> leaves loops open
        /// by, for the passes that close rims after it; null when the input is closed.
        /// </summary>
        public static Func<List<int>, Vector3[], bool> OutlineTest(Vector3[] outlineVertices, int[] outlineEdges,
            float tolerance)
        {
            if (outlineVertices == null || outlineEdges == null || outlineEdges.Length < 2 || tolerance <= 0f)
                return null;
            var grid = new OutlineGrid(outlineVertices, outlineEdges, tolerance);
            return (loop, positions) => IsOnInputOutline(loop, positions, grid, tolerance);
        }

        static bool IsOnInputOutline(List<int> loop, Vector3[] positions, OutlineGrid outline,
            float tolerance)
        {
            var toleranceSqr = tolerance * tolerance;
            var near = 0;

            for (var i = 0; i < loop.Count; i++)
            {
                var v = loop[i];
                if (v < 0 || v >= positions.Length) continue;
                if (outline.WithinSqr(positions[v], toleranceSqr)) near++;
            }

            return near * 2 > loop.Count;
        }

        /// <summary>
        /// A uniform grid over the input's boundary edge midpoints, for "is this point on the
        /// outline" queries.
        /// </summary>
        /// <remarks>
        /// Midpoints rather than the segments themselves. The outline is sampled at the input's
        /// resolution and the query tolerance is a whole lattice spacing — far coarser than the
        /// gap between consecutive midpoints — so a point near the segment is near a midpoint, and
        /// point-to-point is a great deal simpler to get right than point-to-segment. A separate
        /// grid rather than <see cref="SpatialGrid"/> because that one indexes triangles, and the
        /// outline is a polyline.
        /// </remarks>
        sealed class OutlineGrid
        {
            readonly Vector3[] m_Points;
            readonly int[] m_CellStart;
            readonly int[] m_CellItems;
            readonly Vector3 m_Origin;
            readonly float m_CellSize;
            readonly int m_DimX, m_DimY, m_DimZ;

            public int pointCount => m_Points.Length;

            public OutlineGrid(Vector3[] positions, int[] edges, float cellSize)
            {
                var count = edges.Length / 2;
                m_Points = new Vector3[count];
                for (var e = 0; e < count; e++)
                {
                    var a = positions[edges[e * 2]];
                    var b = positions[edges[e * 2 + 1]];
                    m_Points[e] = (a + b) * 0.5f;
                }

                m_CellSize = Mathf.Max(cellSize, 1e-6f);

                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (var i = 0; i < m_Points.Length; i++)
                {
                    min = Vector3.Min(min, m_Points[i]);
                    max = Vector3.Max(max, m_Points[i]);
                }

                if (m_Points.Length == 0) { min = Vector3.zero; max = Vector3.zero; }

                m_Origin = min;
                var span = max - min;
                m_DimX = Mathf.Max(1, Mathf.CeilToInt(span.x / m_CellSize) + 1);
                m_DimY = Mathf.Max(1, Mathf.CeilToInt(span.y / m_CellSize) + 1);
                m_DimZ = Mathf.Max(1, Mathf.CeilToInt(span.z / m_CellSize) + 1);

                var cells = m_DimX * m_DimY * m_DimZ;
                m_CellStart = new int[cells + 1];

                for (var i = 0; i < m_Points.Length; i++) m_CellStart[CellIndex(m_Points[i]) + 1]++;
                for (var c = 0; c < cells; c++) m_CellStart[c + 1] += m_CellStart[c];

                m_CellItems = new int[m_Points.Length];
                var fill = new int[cells];
                for (var i = 0; i < m_Points.Length; i++)
                {
                    var c = CellIndex(m_Points[i]);
                    m_CellItems[m_CellStart[c] + fill[c]++] = i;
                }
            }

            /// <summary>Is any outline point within the given squared distance of this point?</summary>
            public bool WithinSqr(Vector3 point, float toleranceSqr)
            {
                if (m_Points.Length == 0) return false;

                Coord(point, out var cx, out var cy, out var cz);

                // One ring is enough: cellSize is the tolerance, so nothing further than one cell
                // away in each axis can be within it.
                for (var z = cz - 1; z <= cz + 1; z++)
                {
                    if (z < 0 || z >= m_DimZ) continue;
                    for (var y = cy - 1; y <= cy + 1; y++)
                    {
                        if (y < 0 || y >= m_DimY) continue;
                        for (var x = cx - 1; x <= cx + 1; x++)
                        {
                            if (x < 0 || x >= m_DimX) continue;

                            var c = (z * m_DimY + y) * m_DimX + x;
                            for (var s = m_CellStart[c]; s < m_CellStart[c + 1]; s++)
                            {
                                if ((m_Points[m_CellItems[s]] - point).sqrMagnitude <= toleranceSqr)
                                    return true;
                            }
                        }
                    }
                }

                return false;
            }

            void Coord(Vector3 p, out int x, out int y, out int z)
            {
                x = Mathf.Clamp(Mathf.FloorToInt((p.x - m_Origin.x) / m_CellSize), 0, m_DimX - 1);
                y = Mathf.Clamp(Mathf.FloorToInt((p.y - m_Origin.y) / m_CellSize), 0, m_DimY - 1);
                z = Mathf.Clamp(Mathf.FloorToInt((p.z - m_Origin.z) / m_CellSize), 0, m_DimZ - 1);
            }

            int CellIndex(Vector3 p)
            {
                Coord(p, out var x, out var y, out var z);
                return (z * m_DimY + y) * m_DimX + x;
            }
        }

        static long Key(int a, int b) => EdgeKey.Pack(a, b);

        static void Unkey(long key, out int a, out int b)
        {
            a = EdgeKey.First(key);
            b = EdgeKey.Second(key);
        }
    }
}
