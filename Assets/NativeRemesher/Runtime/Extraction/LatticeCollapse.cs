// Passes A, B and C: turn the position field into an output graph.
//
// This is where the extractor's main defect was. The old code decided two questions with one
// Euclidean distance threshold:
//
//   * should these two vertices merge?      distance < 0.45 * scale
//   * is there an output edge between them?  any input edge crossing a cluster boundary
//
// Both answers were wrong, and the second one was the expensive mistake. The reference classifies
// every edge on the INTEGER lattice coordinates, three ways: same cell (collapse), exactly one step
// apart (a genuine output edge), or two-plus cells / on the (1,1) diagonal (throw the link away).
// Admitting diagonals as edges over-populates every neighbour ring, and the tightest-turn walk then
// closes a triangle almost everywhere it should have closed a quad — which is most of why the output
// was ~0.3% quads.
//
// The merge question was wrong in a subtler way. Under the anchoring snap, two vertices that share a
// lattice point sit roughly 0.55 cells apart in plain Euclidean terms, so a 0.45-cell threshold
// rejected nearly all of them; and raising the threshold instead let union-find chain transitively
// until thousands of vertices merged into one. Distance cannot express this. Lattice identity can.
//
// HOW FEATURES ARE HANDLED, and how they are deliberately NOT.
//
// The obvious approach is to forbid merging a crease vertex with an off-crease one, on the grounds
// that averaging the two drags the corner off the crease. That was tried and it is clearly wrong:
// refusing those merges walls each crease off into a dense line of output vertices that the
// surrounding lattice cannot resolve against. Measured on a subdivided cube at 1200 quads, quad
// fraction fell from 98.6% to 65.0% and irregular vertices rose from 1.9% to 29.3%, while surface
// deviation did not improve at all — so it cost a great deal and bought nothing.
//
// The separation of concerns that does work: let the lattice decide the TOPOLOGY without
// interference, and let features decide the POSITIONS. Any cluster containing feature vertices takes
// its position from those members alone (and a corner snaps outright to the corner vertex), so the
// crease stays sharp while the quad layout around it is left undisturbed.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class CollapseStats
    {
        public int inputEdges;
        public int collapseCandidates;
        public int outputEdges;
        public int discardedEdges;

        public int collapsesApplied;
        public int collapsesRejectedAlreadyLinked;
        public int collapsesRejectedTooLarge;
        public int collapsesRejectedOpposedNormals;
        public int collapsesRejectedFeature;
        /// <summary>Edges added because a collapse was refused. See the fallback in Pass B.</summary>
        public int fallbackEdgesAdded;

        public int clusters;
        public int largestCluster;

        /// <summary>Cluster pairs closer than the merge threshold: cells that were split in two.</summary>
        public int splitCellCandidates;

        public int splitCellsMerged;

        public int splitCellRejectedTooLarge;

        public float DiscardedFraction => inputEdges > 0 ? discardedEdges / (float)inputEdges : 0f;

        public override string ToString()
        {
            return $"edges={inputEdges} (collapse={collapseCandidates} out={outputEdges} " +
                   $"discard={discardedEdges} / {DiscardedFraction:P1}) " +
                   $"applied={collapsesApplied} rejLinked={collapsesRejectedAlreadyLinked} " +
                   $"rejFeature={collapsesRejectedFeature} " +
                   $"rejLarge={collapsesRejectedTooLarge} rejOpposed={collapsesRejectedOpposedNormals} fallback={fallbackEdgesAdded} " +
                   $"splitCells={splitCellsMerged}/{splitCellCandidates} " +
                   $"(rejLarge={splitCellRejectedTooLarge}) " +
                   $"clusters={clusters} largest={largestCluster}";
        }
    }

    public static class LatticeCollapse
    {
        struct Candidate
        {
            public int a;
            public int b;
            public float error;
        }

        /// <summary>
        /// How far a cluster's lattice points may spread, in lattice cells, before a collapse into
        /// it is refused.
        /// </summary>
        /// <remarks>
        /// This is load-bearing, not a safety net, and it is standing in for the multiresolution
        /// hierarchy we do not have.
        ///
        /// The reference bounds transitive chaining with the already-linked check below: never
        /// collapse two clusters that an output edge already joins. That works because its hierarchy
        /// establishes lattice agreement coarse-to-fine, so by the time the finest level runs there
        /// is a healthy population of one-step edges doing the blocking.
        ///
        /// Solving a single level has a failure mode the reference never sees. As the requested
        /// lattice gets coarse relative to the input edge length, almost every edge classifies as
        /// Collapse and almost none as one-step — so the already-linked check has nothing to block
        /// with, the candidate graph is dense, and one chain can swallow the mesh. Measured on a
        /// 5120-triangle icosphere, a limit of 2 cells collapsed the whole sphere to 3 vertices once
        /// the lattice passed about 5.5x the input edge.
        ///
        /// The value is set by what a legitimate cluster actually spans. A cluster covers one lattice
        /// cell, so its members' own positions spread about one cell; on top of that each member's
        /// lattice point is anchored within half a cell of its vertex in each tangent direction,
        /// contributing up to another ~0.7 cell. Two-ish cells is therefore the honest bound, and
        /// measurement agrees: face count scales as 1/spread^2, and a 1-cell bound overshot the
        /// requested count by 4.9x on a 20480-triangle icosphere, which is a factor of 2.2 in spread.
        ///
        /// Note this bound is only safe to set generously because a refused collapse falls back to an
        /// output edge (see Pass B). Without that fallback a refusal silently destroyed the
        /// connection, tightening the bound made the output worse rather than safer, and the guard
        /// could not be tuned on face count at all.
        /// </remarks>
        public const float defaultMaxClusterSpread = 2.25f;

        /// <summary>
        /// Two clusters closer together than this many cells are the same cell, and are merged
        /// after the main collapse. 0 disables the pass.
        /// </summary>
        /// <remarks>
        /// THE DEFECT THIS EXISTS FOR. Every edge is classified independently, by asking which
        /// integer offset best explains the gap between its two endpoints' lattice points. An edge
        /// whose endpoints sit about HALF a cell apart is a coin toss: offset 0 explains it with a
        /// residual of 0.5 and offset 1 with a residual of 0.5, and which one wins is decided by
        /// noise. Different edges between the same two groups of vertices therefore come to
        /// different answers, and a single cell ends up split into two clusters that sit half a cell
        /// apart - with an output edge between them, because some edge voted "one step".
        ///
        /// A split cell is exactly a cancelling valence pair. The two halves divide the four
        /// directions between them, so each is short of neighbours, and every neighbour of the
        /// original cell now has two claimants in one direction. That is the 3-next-to-5 signature
        /// the output is full of, and it is why the already-linked guard in Pass B makes it
        /// permanent: once one edge between the halves has been called an output edge, the guard
        /// refuses every later candidate to merge them.
        ///
        /// Measured on the character at the delivered density, before this pass: 18 % of output
        /// edges are shorter than 0.75 cells and 2.5 % shorter than half a cell, where a lattice
        /// that tiles would put nearly all of them at 1.0. The field is not the cause - it carries
        /// 427 singularities against the 1,625 irregular vertices in the output.
        ///
        /// Merging cannot run during Pass B because clusters do not exist yet; it needs their
        /// representative positions, so it is its own pass afterwards.
        ///
        /// 0.8 cells, swept on the character at the delivered density. Irregular vertices / quad
        /// fraction / triangles / edges under 0.75 cells / uncovered surface:
        ///
        ///   off   31.5 %% / 96.63 %% / 184 / 18.2 %% / 3.88 %%
        ///   0.6   29.3 %% / 97.51 %% / 130 / 15.1 %% / 3.98 %%
        ///   0.8   25.7 %% / 98.06 %% / 100 /  9.4 %% / 4.22 %%      <- here
        ///   0.9   29.9 %% / 97.92 %% / 105 /  5.8 %% / 4.23 %%
        ///   1.0   46.1 %% / 97.79 %% / 103 /  2.5 %% / 3.63 %%
        ///
        /// NOT MONOTONE, and the turn is the useful part. Up to 0.8 this removes splits; past it,
        /// it starts merging cells that are genuinely ADJACENT - neighbouring cells sit one cell
        /// apart by definition - and each of those merges takes a vertex out of the middle of a
        /// regular grid, which costs two irregular vertices rather than saving any. At 1.0 the short
        /// edges are nearly all gone and the mesh is half irregular, which is the clearest possible
        /// statement that short edges were a symptom and not the disease.
        ///
        /// The cost at 0.8 is 0.34 points of uncovered surface, measured on the same run: merging
        /// two clusters removes a vertex, and a vertex removed near a fingertip is geometry gone.
        /// That is the trade this project has to keep looking at, and at 0.8 it is worth it - 5.8
        /// points of irregularity and 1.4 of quad fraction for a third of a point of coverage.
        /// </remarks>
        public const float defaultMergeBelowCells = 0.8f;

        /// <summary>
        /// Cosine of the largest angle allowed between two vertices' normals for them to collapse
        /// into one output vertex. Zero is ninety degrees.
        /// </summary>
        /// <remarks>
        /// This is a THIN-SHEET test, not a curvature one. At ninety degrees it cannot fire on a
        /// surface being sampled anywhere near the lattice spacing; what it fires on is an edge that
        /// crosses from one face of a sheet to the other, where the normals reverse. The collar of
        /// the reference asset is exactly that: a tube with a rolled rim whose two walls are closer
        /// together than one quad, so the lattice is right that they are within a cell and wrong
        /// that they are the same place.
        /// </remarks>
        // OFF. Minus one can never reject, because a dot product is never below it.
        //
        // HONEST STATUS: this is a correct guard that does not fix what it was built for. The collar
        // is fused by the collapse - that much is now localised, the decimated input carries it
        // perfectly and turning relaxation off leaves it just as fused - and this test fires without
        // changing it. It also costs the Character profile about a point of quad fraction (96.22 to
        // 95.28), which is a real price for no benefit, so it ships off rather than on.
        //
        // Kept rather than deleted because the mechanism is sound and the next attempt on the collar
        // will want it: a cluster whose members face opposite ways IS a welded sheet, and this is
        // how you ask. What it does not yet capture is whichever path actually welds this one.
        public const float defaultOpposedNormalDot = -1f;

        public static QuadGraph Build(RemeshTopology topology, OrientationField orientation,
            PositionField position, out CollapseStats stats,
            float maxClusterSpread = defaultMaxClusterSpread, FeatureGraph features = null,
            float opposedNormalDot = defaultOpposedNormalDot,
            float mergeBelowCells = defaultMergeBelowCells)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (orientation == null) throw new ArgumentNullException(nameof(orientation));
            if (position == null) throw new ArgumentNullException(nameof(position));

            stats = new CollapseStats();

            var count = topology.vertexCount;
            var scale = position.scale;
            var invScale = 1f / scale;

            // Per-vertex spacing where the field is graded, and the mean everywhere else. The two
            // cases go through the same code below so a graded run cannot take a different path
            // through extraction than a uniform one.
            var perVertexScale = position.spacing;

            var p = topology.positions;
            var normals = topology.normals;
            var q = orientation.directions;
            var o = position.positions;
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;

            // ---------------------------------------------------------------
            // Pass A — classify every undirected input edge on the lattice
            // ---------------------------------------------------------------
            var candidates = new List<Candidate>(count * 2);
            var outputEdges = new List<int>(count * 4);   // flat pairs

            for (var i = 0; i < count; i++)
            {
                for (var k = start[i]; k < start[i + 1]; k++)
                {
                    var j = adjacency[k];
                    if (j <= i) continue;                 // each undirected edge once

                    stats.inputEdges++;

                    // The integer coordinates are stated relative to a particular (q, t) basis, so
                    // the two crosses must be brought into a common frame first. Unlike the
                    // continuous case — where the square lattice is invariant under the 4-RoSy
                    // group — skipping this here produces meaningless indices.
                    OrientationField.Compat4(q[i], normals[i], q[j], normals[j], out var qi, out var qj);

                    var scaleI = perVertexScale != null ? perVertexScale[i] : scale;
                    var scaleJ = perVertexScale != null ? perVertexScale[j] : scale;

                    PositionField.CompatIndex(
                        p[i], normals[i], qi, o[i],
                        p[j], normals[j], qj, o[j],
                        scaleI, 1f / scaleI, scaleJ, 1f / scaleJ,
                        out var shift0, out var shift1, out var error);

                    switch (PositionField.Classify(shift0, shift1))
                    {
                        case PositionField.EdgeClass.Collapse:
                            candidates.Add(new Candidate { a = i, b = j, error = error });
                            stats.collapseCandidates++;
                            break;

                        case PositionField.EdgeClass.Edge:
                            outputEdges.Add(i);
                            outputEdges.Add(j);
                            stats.outputEdges++;
                            break;

                        default:
                            stats.discardedEdges++;
                            break;
                    }
                }
            }

            // ---------------------------------------------------------------
            // Pass B — apply collapses, cheapest first, each validated
            // ---------------------------------------------------------------

            // Cheapest first matters: the error is the residual distance between the two lattice
            // points, so the most confident merges happen while there is still freedom to make
            // them, and the marginal ones get refused once the neighbourhood is decided.
            candidates.Sort((x, y) => x.error.CompareTo(y.error));

            var parent = new int[count];
            var size = new int[count];
            for (var i = 0; i < count; i++) { parent[i] = i; size[i] = 1; }

            // Per-root neighbour sets over the OUTPUT edges, maintained across unions. Collapsing
            // two clusters that are already joined by an output edge would turn that edge into a
            // self-loop, destroying a quad corner. This check — not a distance — is what bounds
            // transitive chaining in the reference.
            var rootAdjacency = new HashSet<int>[count];
            for (var e = 0; e < outputEdges.Count; e += 2)
            {
                var a = outputEdges[e];
                var b = outputEdges[e + 1];
                (rootAdjacency[a] ??= new HashSet<int>()).Add(b);
                (rootAdjacency[b] ??= new HashSet<int>()).Add(a);
            }

            // Per-root bounds of the member lattice points, for the spread backstop.
            var boundsMin = new Vector3[count];
            var boundsMax = new Vector3[count];
            for (var i = 0; i < count; i++) { boundsMin[i] = o[i]; boundsMax[i] = o[i]; }

            // Per-root SUM of the member normals, for the thin-sheet test below. A sum rather than a
            // mean because merging two roots is then one addition, and the direction is all the test
            // reads. It is deliberately not normalised on the way in: a cluster straddling a rim has
            // members cancelling each other, and a short sum is itself the signal that they do.
            var normalSum = new Vector3[count];
            for (var i = 0; i < count; i++) normalSum[i] = normals[i];

            // The spread backstop is in CELLS, so under grading it has to be evaluated against the
            // cell size where the cluster actually is. A single world-space bound taken from the
            // mean would be several times too tight on the coarse end of a graded field and too
            // loose on the fine end, which is exactly backwards: the fine end is the detail the
            // grading exists to protect.
            var uniformMaxSpread = maxClusterSpread * scale;

            // A collapse we refuse to apply must NOT simply disappear. The two vertices are
            // adjacent on the input surface, so if they do not become one output vertex they must
            // become two output vertices with an edge between them — otherwise the connection is
            // lost and the output graph falls apart.
            //
            // This bites hardest exactly where it is least obvious. As the requested lattice gets
            // coarse, nearly every input edge classifies as Collapse and almost none as one-step, so
            // dropping refused collapses strips the graph of most of its edges; GraphCleanup's
            // iterative valence-1 pruning then eats the remaining sparse tree down to nothing.
            // Measured before this fallback existed: a 5120-triangle icosphere reduced to 3
            // vertices and 0 faces, and TIGHTENING the spread guard made it worse rather than
            // better, which is what pointed here.
            //
            // The reference never needs this because its only rejection test is "already linked",
            // which by definition means the edge is already present.
            var fallbackEdges = new List<int>();

            foreach (var candidate in candidates)
            {
                var ra = Find(parent, candidate.a);
                var rb = Find(parent, candidate.b);
                if (ra == rb) continue;

                if (rootAdjacency[ra] != null && rootAdjacency[ra].Contains(rb))
                {
                    // Already joined by a one-step edge, so connectivity is intact.
                    stats.collapsesRejectedAlreadyLinked++;
                    continue;
                }

                var mergedMin = Vector3.Min(boundsMin[ra], boundsMin[rb]);
                var mergedMax = Vector3.Max(boundsMax[ra], boundsMax[rb]);

                // The finer of the two, so a cluster spanning a size transition is held to the
                // tighter bound and detail is never merged away by its coarser neighbour.
                var maxSpread = perVertexScale != null
                    ? maxClusterSpread * Mathf.Min(perVertexScale[candidate.a], perVertexScale[candidate.b])
                    : uniformMaxSpread;

                if ((mergedMax - mergedMin).magnitude > maxSpread)
                {
                    stats.collapsesRejectedTooLarge++;
                    fallbackEdges.Add(candidate.a);
                    fallbackEdges.Add(candidate.b);
                    continue;
                }

                // THIN SHEETS, tested on the CLUSTERS and not on the candidate's two vertices.
                //
                // The asset that made this visible is the collar: a tube with a rolled rim whose
                // inner and outer walls sit closer together than one quad at the delivered density.
                // The collapse fuses it into a lump, and a first version of this guard compared the
                // two CANDIDATE vertices' normals and never fired even once - 90 degrees and 60
                // degrees produced output identical to the digit.
                //
                // It could not fire, by construction. Candidates are input EDGES, and the two walls
                // of the collar are not edge-adjacent: they are several edges apart across the rim.
                // The fusion is TRANSITIVE - outer wall joins rim, rim joins inner wall - and every
                // individual step is a perfectly reasonable twenty or thirty degrees. Only the
                // finished cluster spans a hundred and eighty.
                //
                // So the question has to be asked of what the clusters have ACCUMULATED. Each root
                // carries the sum of its members' normals; two roots whose sums point away from each
                // other are opposite faces of a sheet however gently the path between them turned.
                // This is the same shape of argument as the existing spread backstop, which bounds
                // where a cluster reaches in space while this bounds where it faces.
                //
                // The refusal falls through to an output edge like every other refusal here, so the
                // two walls become two rows of vertices with edges between them rather than one
                // welded row.
                if (normals != null)
                {
                    var na = normalSum[ra];
                    var nb = normalSum[rb];
                    var la = na.magnitude;
                    var lb = nb.magnitude;

                    // A sum that has cancelled to nothing has no direction left to compare, and
                    // .normalized would hand back zero below 1e-5 and pass the test by accident.
                    // Such a cluster already straddles a sheet, so refuse rather than wave through.
                    if (la < 1e-5f || lb < 1e-5f
                        || Vector3.Dot(na / la, nb / lb) < opposedNormalDot)
                    {
                        stats.collapsesRejectedOpposedNormals++;
                        fallbackEdges.Add(candidate.a);
                        fallbackEdges.Add(candidate.b);
                        continue;
                    }
                }


                // Union by size, so the adjacency merge below is amortised.
                if (size[ra] < size[rb]) { var t = ra; ra = rb; rb = t; }

                parent[rb] = ra;
                size[ra] += size[rb];
                boundsMin[ra] = mergedMin;
                boundsMax[ra] = mergedMax;
                normalSum[ra] += normalSum[rb];

                MergeRootAdjacency(parent, rootAdjacency, ra, rb);

                stats.collapsesApplied++;
            }

            // ---------------------------------------------------------------
            // Pass B2 — merge the clusters that are the same cell split in two
            // ---------------------------------------------------------------
            if (mergeBelowCells > 0f)
                MergeSplitCells(parent, size, boundsMin, boundsMax, o, outputEdges, fallbackEdges,
                    perVertexScale, scale, mergeBelowCells, maxClusterSpread, stats);

            // ---------------------------------------------------------------
            // Pass C — clusters become output vertices
            // ---------------------------------------------------------------
            var clusterIndex = new int[count];
            for (var i = 0; i < count; i++) clusterIndex[i] = -1;

            var clusterCount = 0;
            for (var i = 0; i < count; i++)
            {
                var root = Find(parent, i);
                if (clusterIndex[root] < 0) clusterIndex[root] = clusterCount++;
                clusterIndex[i] = clusterIndex[root];
            }

            stats.clusters = clusterCount;

            var graph = new QuadGraph(clusterCount);

            var weightSum = new float[clusterCount];
            var accumulatedPosition = new Vector3[clusterCount];
            var accumulatedNormal = new Vector3[clusterCount];
            var bestWeight = new float[clusterCount];
            var members = new int[clusterCount];

            // Parallel accumulators over feature members only, so a cluster that contains any
            // feature vertices can be positioned from those rather than from the lattice.
            var accumulatedFeaturePosition = new Vector3[clusterCount];
            var featureWeightSum = new float[clusterCount];
            var featureCorner = new int[clusterCount];
            for (var c = 0; c < clusterCount; c++) featureCorner[c] = -1;

            for (var i = 0; i < count; i++)
            {
                var c = clusterIndex[i];

                // Gaussian weighting, as the reference does: a vertex whose lattice point has been
                // dragged far from the vertex itself is a less trustworthy witness to where the
                // output corner belongs, so it contributes less. A plain mean gives the strays equal
                // say and pulls corners off the surface.
                var offset = (o[i] - p[i]).sqrMagnitude * invScale * invScale;
                var weight = Mathf.Exp(-offset * 9f);

                weightSum[c] += weight;
                accumulatedPosition[c] += o[i] * weight;
                accumulatedNormal[c] += normals[i] * weight;
                members[c]++;

                // The most trustworthy member also donates the cluster's cross direction, which
                // later passes need for feature snapping and for classifying output edges as u or v.
                if (weight > bestWeight[c])
                {
                    bestWeight[c] = weight;
                    graph.directions[c] = q[i];
                }

                if (features != null && features.isFeatureVertex[i])
                {
                    accumulatedFeaturePosition[c] += p[i] * weight;
                    featureWeightSum[c] += weight;

                    // A junction outranks a plain crease vertex: if any member is a corner, the
                    // cluster belongs at that corner exactly.
                    if (features.featureDegree[i] >= 3) featureCorner[c] = i;
                }

                if (members[c] > stats.largestCluster) stats.largestCluster = members[c];
            }

            for (var c = 0; c < clusterCount; c++)
            {
                graph.positions[c] = weightSum[c] > 1e-20f
                    ? accumulatedPosition[c] / weightSum[c]
                    : accumulatedPosition[c];

                // Feature clusters take their position from the surface, not from the lattice.
                //
                // The lattice point o_i is free to drift within a cell of its vertex, so averaging
                // lattice points puts a crease corner NEAR the crease rather than on it — and a
                // crease that is nearly right reads as a bevel, which is exactly the artefact this
                // whole pass exists to prevent. The input positions of feature members all lie
                // exactly on the crease, so their average lies on it too (or, on a curved crease,
                // very slightly inside it, which is the chord error of the crease itself and not
                // something extraction can improve on).
                //
                // A corner is snapped outright to the single vertex rather than averaged: the corner
                // IS the shape, and moving it even slightly is visible where a smooth region moving
                // is not.
                if (features != null && featureWeightSum[c] > 1e-20f)
                {
                    graph.positions[c] = featureCorner[c] >= 0
                        ? p[featureCorner[c]]
                        : accumulatedFeaturePosition[c] / featureWeightSum[c];
                }

                var n = accumulatedNormal[c];
                // The 1e-20 test is not enough on its own: it admits magnitudes down to 1e-10, and
                // .normalized returns zero for anything under 1e-5. SafeNormalize covers the gap.
                graph.normals[c] = MeshMath.SafeNormalize(n, Vector3.up);

                // Keep the direction tangent to the cluster's averaged normal, and never leave a
                // degenerate direction behind for a later pass to divide by.
                var d = graph.directions[c];
                d -= graph.normals[c] * Vector3.Dot(graph.normals[c], d);
                graph.directions[c] = d.sqrMagnitude > 1e-20f
                    ? MeshMath.SafeNormalize(d, OrientationField.ArbitraryTangent(graph.normals[c]))
                    : OrientationField.ArbitraryTangent(graph.normals[c]);
            }

            // Output edges, mapped onto clusters. An edge whose endpoints ended up in the same
            // cluster is dropped: the already-linked check makes that rare, but a chain of accepted
            // collapses can still produce one.
            for (var e = 0; e < outputEdges.Count; e += 2)
            {
                var a = clusterIndex[outputEdges[e]];
                var b = clusterIndex[outputEdges[e + 1]];
                if (a != b) graph.AddEdge(a, b);
            }

            // Tell the graph which output edges are features, so the cleanup passes refuse to
            // splice them away. A crease vertex is very often valence 2 in the output, which is
            // exactly the case the valence-2 pass removes — and the vertex sitting on the crease is
            // the one whose position carries the shape.
            if (features != null)
            {
                for (var i = 0; i < count; i++)
                {
                    if (!features.isFeatureVertex[i]) continue;
                    for (var k = start[i]; k < start[i + 1]; k++)
                    {
                        var j = adjacency[k];
                        if (j <= i || !features.IsFeatureEdge(i, j)) continue;

                        var ca = clusterIndex[i];
                        var cb = clusterIndex[j];
                        if (ca == cb) continue;

                        graph.AddEdge(ca, cb);
                        graph.Protect(ca, cb);
                    }
                }
            }

            // Refused collapses, as edges. Endpoints that ended up in the same cluster anyway — via
            // some other chain of accepted collapses — are skipped, and AddEdge de-duplicates the
            // rest against the one-step edges above.
            for (var e = 0; e < fallbackEdges.Count; e += 2)
            {
                var a = clusterIndex[fallbackEdges[e]];
                var b = clusterIndex[fallbackEdges[e + 1]];
                if (a != b)
                {
                    graph.AddEdge(a, b);
                    stats.fallbackEdgesAdded++;
                }
            }

            return graph;
        }

        static void MergeRootAdjacency(int[] parent, HashSet<int>[] rootAdjacency, int into, int from)
        {
            var fromSet = rootAdjacency[from];
            if (fromSet == null) return;

            var intoSet = rootAdjacency[into] ??= new HashSet<int>();

            foreach (var x in fromSet)
            {
                var rx = Find(parent, x);
                if (rx == into) continue;

                intoSet.Add(rx);
                var theirs = rootAdjacency[rx];
                if (theirs == null) continue;
                theirs.Remove(from);
                theirs.Add(into);
            }

            intoSet.Remove(from);
            intoSet.Remove(into);
            fromSet.Clear();
        }

        /// <summary>Union-find lookup with path halving.</summary>
        /// <summary>
        /// Merges cluster pairs whose lattice points sit closer than <paramref name="mergeBelow"/>
        /// cells, cheapest first. See <see cref="defaultMergeBelowCells"/> for why they exist.
        /// </summary>
        /// <remarks>
        /// Measured on the LATTICE points rather than the vertex positions, because that is the
        /// quantity the classification was ambiguous about: two clusters whose lattice points
        /// coincide are one cell however far apart their members' surface positions happen to be.
        ///
        /// Cheapest first and re-read through Find each time, so that a chain of three half-cell
        /// clusters merges into one rather than into an arbitrary pair, and so that the spread bound
        /// still applies to the result of every merge.
        /// </remarks>
        static void MergeSplitCells(int[] parent, int[] size, Vector3[] boundsMin, Vector3[] boundsMax,
            Vector3[] o, List<int> outputEdges, List<int> fallbackEdges, float[] perVertexScale,
            float scale, float mergeBelow, float maxClusterSpread, CollapseStats stats)
        {
            var count = parent.Length;

            // Mean lattice point per root, which is where the cluster will end up.
            var sum = new Vector3[count];
            var members = new int[count];
            for (var i = 0; i < count; i++)
            {
                var root = Find(parent, i);
                sum[root] += o[i];
                members[root]++;
            }

            var centre = new Vector3[count];
            for (var i = 0; i < count; i++)
                if (members[i] > 0) centre[i] = sum[i] / members[i];

            // Candidate pairs come from the output edges: only clusters the graph already joins can
            // be the two halves of one cell, and using any other pairing would be a proximity merge
            // rather than a repair of the classification.
            var pairs = new List<(int a, int b, float distance)>();
            var seen = new HashSet<long>(EdgeKey.Comparer);

            void Consider(List<int> flatPairs)
            {
                if (flatPairs == null) return;
                for (var e = 0; e + 1 < flatPairs.Count; e += 2)
                {
                    var ra = Find(parent, flatPairs[e]);
                    var rb = Find(parent, flatPairs[e + 1]);
                    if (ra == rb) continue;
                    if (members[ra] == 0 || members[rb] == 0) continue;
                    if (!seen.Add(EdgeKey.PackUndirected(ra, rb))) continue;

                    var cell = perVertexScale != null
                        ? Mathf.Min(perVertexScale[flatPairs[e]], perVertexScale[flatPairs[e + 1]])
                        : scale;
                    if (!(cell > 0f)) continue;

                    var distance = (centre[ra] - centre[rb]).magnitude / cell;
                    if (distance < mergeBelow) pairs.Add((ra, rb, distance));
                }
            }

            Consider(outputEdges);
            Consider(fallbackEdges);

            stats.splitCellCandidates = pairs.Count;
            if (pairs.Count == 0) return;

            pairs.Sort((x, y) => x.distance.CompareTo(y.distance));

            foreach (var pair in pairs)
            {
                var ra = Find(parent, pair.a);
                var rb = Find(parent, pair.b);
                if (ra == rb) continue;

                var mergedMin = Vector3.Min(boundsMin[ra], boundsMin[rb]);
                var mergedMax = Vector3.Max(boundsMax[ra], boundsMax[rb]);
                if ((mergedMax - mergedMin).magnitude > maxClusterSpread * scale)
                {
                    stats.splitCellRejectedTooLarge++;
                    continue;
                }

                if (size[ra] < size[rb]) { var t = ra; ra = rb; rb = t; }

                parent[rb] = ra;
                size[ra] += size[rb];
                boundsMin[ra] = mergedMin;
                boundsMax[ra] = mergedMax;

                // The merged cluster's centre is the weighted mean of the two, so a later pair in
                // this same sweep is measured against where the cluster actually is now.
                var total = members[ra] + members[rb];
                if (total > 0)
                    centre[ra] = (centre[ra] * members[ra] + centre[rb] * members[rb]) / total;
                members[ra] = total;
                members[rb] = 0;

                stats.splitCellsMerged++;
            }
        }

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }
    }
}
