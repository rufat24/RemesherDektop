// Why the output graph has the right average valence and the wrong valences.
//
// Measured on the user's reference asset at 5,474 faces, straight out of GraphCleanup:
//
//     graph 5094 verts  mean=4.005  [3=1251  4=2836  5=792  6=174  7+=41]
//
// A mean of 4.005 on a closed surface is not a sign of health, it is arithmetic: Euler's formula
// forces it. What quality lives in is the SPREAD, and 44% of these vertices are irregular against
// 11.3% in the reference retopology of the same asset. The excess cancels almost perfectly, 1251
// threes against 792 fives and 215 sixes-and-up, which is the signature of a lattice that is locally
// plausible everywhere and globally does not tile.
//
// This pass reads each cluster's neighbour ring in the cluster's OWN lattice frame and reports how
// many of the four directions are actually occupied. On the reference asset, before any change:
//
//     occupied[0..4] = [1  36  431  1515  3489]   contested=1730  surplus=1879
//
// So 3,489 clusters of 5,472 have all four. 1,515 are missing one outright, 431 are missing two,
// and 1,730 have some direction claimed by more than one neighbour. Those two counts being nearly
// equal is the cancelling pair, seen directly: a valence-5 and a valence-3 manufactured together
// wherever two lattice cells failed to line up.
//
// The mechanism follows from how the graph is built. LatticeCollapse classifies each INPUT edge as
// one lattice step using the two endpoint vertices' own crosses, then maps the survivors onto
// clusters. A cluster has many members, each with its own frame, and nothing requires the one-step
// edges leaving a cluster to leave in four distinct directions. Two members on opposite sides of a
// cluster can each contribute a one-step edge that, read in the cluster's frame, points the same
// way — while the direction between them gets no edge at all, because every input edge across that
// gap classified as two cells apart and was discarded.
//
// That was the hypothesis: a bookkeeping artefact of classifying input edges before clustering
// them, and therefore repairable in the graph. Two repairs follow from it, and both are implemented
// here behind ExtractionOptions.repairLatticeDirections —
//
//   Surplus removal. Drop an edge that is the weaker claimant on its direction at BOTH ends.
//   Mutual, because one-sided removal is wrong: a valence-3 vertex needs every edge it has, and
//   deleting one because its far end had a better candidate takes a vertex that was merely
//   irregular and makes it broken.
//
//   Direction completion. Add the edge a vacant direction is asking for, when the cluster across
//   the gap is asking for the matching one. This is the half that can turn a valence-3 into a
//   valence-4, so it is where the value would be.
//
// THE HYPOTHESIS IS WRONG, and the repair is off by default because of it. Breaking down 2,180
// vacant directions by why completion could not fill them:
//
//     added=93   notMutual=456   offAxis=819   badSpan=112   none=618
//
// Only 93 are fillable. 618 have no cluster in that direction at all. 819 — the largest group —
// have a cluster at a plausible distance which is more than 30 degrees off the lattice axis.
//
// The control for the obvious objection, that the frame is wrong rather than the lattice: refit
// each cluster's frame to its own ring by the 4-RoSy circular mean (FitFrame) and read the
// histogram again. It moves by 13 vertices out of 4,957 —
//
//     field frame  [2  24  393  1330  3208]
//     fitted frame [2  25  388  1321  3221]
//
// so the frame is not the problem and the shear is real. The lattice is locally converged and
// globally sheared, and no amount of edge editing in the graph can square it. Across the
// procedural corpus the repair moves quad fraction up on 14 rows of 22 and irregular-vertex
// fraction the WRONG way on 14 of 22 — a wash, negative on the metric it exists for.
//
// What this file is for, then, is the measurement, and the measurement is what localises the fault.
// The remaining fix is QuadriFlow's integer constraints (Huang et al. 2018): solve for the integer
// offsets between neighbouring cells with min-cost flow so the lattice tiles by construction,
// instead of solving a local energy and hoping the cells agree afterwards. That is in PositionField,
// not here.

using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class DirectionStats
    {
        public int vertices;

        /// <summary>How many of the four lattice directions each vertex has a neighbour in, before.</summary>
        public int[] occupied = new int[5];

        /// <summary>The same histogram, read in the frame fitted to each ring. See FitFrame.</summary>
        public int[] occupiedFitted = new int[5];

        /// <summary>The same histogram after removal and completion.</summary>
        public int[] occupiedAfter = new int[5];

        /// <summary>(vertex, direction) pairs claimed by more than one neighbour.</summary>
        public int contestedDirections;

        /// <summary>Neighbours beyond the first in a contested direction, summed over all vertices.</summary>
        public int surplusNeighbours;

        public int edgesRemoved;

        /// <summary>Surplus at one end but not the other, so deliberately left alone.</summary>
        public int keptOneSided;

        /// <summary>Left alone because removal would drop an endpoint below valence 2.</summary>
        public int keptWouldStrand;

        /// <summary>Left alone because the edge carries a feature.</summary>
        public int keptProtected;

        /// <summary>Directions with no neighbour at all, summed over vertices, before completion.</summary>
        public int vacantDirections;

        public int edgesAdded;

        /// <summary>Vacancies where the best candidate was not asking for the matching direction.</summary>
        public int refusedNotMutual;

        /// <summary>Vacancies with no cluster at all in that direction anywhere in the two-ring.</summary>
        public int refusedNoCandidate;

        /// <summary>Vacancies whose only candidates in that direction were too near or too far.</summary>
        public int refusedSpan;

        /// <summary>Vacancies whose only candidates at a plausible distance were too far off axis.</summary>
        public int refusedAngle;

        public override string ToString()
        {
            return $"occupied=[{occupied[0]} {occupied[1]} {occupied[2]} {occupied[3]} {occupied[4]}]" +
                   $" fitted=[{occupiedFitted[0]} {occupiedFitted[1]} {occupiedFitted[2]} {occupiedFitted[3]} {occupiedFitted[4]}]" +
                   $"->[{occupiedAfter[0]} {occupiedAfter[1]} {occupiedAfter[2]} {occupiedAfter[3]} {occupiedAfter[4]}] " +
                   $"contested={contestedDirections} surplus={surplusNeighbours} vacant={vacantDirections} " +
                   $"removed={edgesRemoved} (oneSided={keptOneSided} strand={keptWouldStrand} prot={keptProtected}) " +
                   $"added={edgesAdded} (notMutual={refusedNotMutual} offAxis={refusedAngle} " +
                   $"badSpan={refusedSpan} none={refusedNoCandidate})";
        }
    }

    public static class LatticeDirections
    {
        /// <summary>Squareness a candidate edge must have to the axis it would fill. cos(30 degrees).</summary>
        /// <remarks>
        /// A vacancy is filled from the two-ring, and the two-ring is mostly diagonals — the very
        /// links LatticeCollapse discarded for being on the (1,1) step. Admitting a diagonal as a
        /// lattice edge is the bug that gave the original extractor 0.3% quads, so the gate here is
        /// tight: 0.866 excludes anything past 30 degrees off axis, where a true diagonal sits at
        /// 45 and scores 0.707.
        /// </remarks>
        public const float minCompletionAlignment = 0.866f;

        /// <summary>Distance band a completion candidate must fall in, in lattice cells.</summary>
        /// <remarks>
        /// One cell is what an output edge is supposed to span. The band is generous downward
        /// because a cluster's position is a weighted mean of lattice points that each sit within
        /// half a cell of their vertex, and generous upward to two cells because a vacancy usually
        /// exists precisely where the lattice stretched.
        /// </remarks>
        public const float minCompletionSpan = 0.35f;
        public const float maxCompletionSpan = 2.0f;

        /// <summary>
        /// Which of the four lattice directions, in <paramref name="vertex"/>'s own frame, the
        /// direction to <paramref name="other"/> points along, and how squarely it does so.
        /// </summary>
        /// <remarks>
        /// The frame is the cluster's cross direction and its 90-degree rotation about the cluster
        /// normal — the same pair QuadGraph.directions was populated for. Sectors are quadrants
        /// bisected by the axes, so the nearest axis wins and a perfectly diagonal edge falls to the
        /// u side by the tie-break, which is arbitrary and harmless: a diagonal is a bad claimant on
        /// either axis and loses every comparison below to anything squarer.
        ///
        /// alignment is the cosine of the angle to that axis, so 1 is dead on and 0.707 is a
        /// diagonal. It is deliberately NOT distance: a neighbour at the wrong spacing is still the
        /// right neighbour if it is the only one in its direction, and spacing is what the position
        /// field is for. Completion needs distance as well and measures it separately.
        /// </remarks>
        /// <summary>
        /// The lattice frame to read <paramref name="vertex"/>'s ring in: the 4-fold direction that
        /// best fits the ring itself, rather than the cross of whichever member happened to donate
        /// <c>QuadGraph.directions</c>.
        /// </summary>
        /// <remarks>
        /// This matters more than it looks, and it was measured. Sectors are 90 degrees wide and
        /// the squareness gate admits the middle 60 of each, so a frame off by 30 degrees turns a
        /// perfectly lattice-aligned neighbour into an off-axis reject. LatticeCollapse sets a
        /// cluster's direction from its single highest-weight member, which is one sample of a field
        /// that varies across the cluster — so the frame carries that member's error, and the ring
        /// gets judged against it.
        ///
        /// The fit is the standard one for a 4-RoSy quantity: take each neighbour's angle in the
        /// tangent plane, quadruple it, average on the circle, and divide by four. Quadrupling is
        /// what makes the four lattice directions land on the same point, so a square ring sums
        /// coherently while a ring pointing four different ways cancels. The magnitude of that sum
        /// is therefore a confidence, and a ring with no coherent square in it keeps the field's
        /// frame rather than a fitted angle read out of noise.
        ///
        /// Note what this changes about the QUESTION being asked. Against the field's frame, a
        /// contested direction means the ring disagrees with the cross field. Against the fitted
        /// frame it means the ring is not square in its own terms — which is the thing that decides
        /// valence, and so the thing worth counting.
        /// </remarks>
        public static Vector3 FitFrame(QuadGraph graph, int vertex)
        {
            var n = graph.normals[vertex];
            var u = graph.directions[vertex];
            var v = Vector3.Cross(n, u);

            var sumCos = 0f;
            var sumSin = 0f;
            var counted = 0;

            foreach (var m in graph.Neighbours(vertex))
            {
                var e = graph.positions[m] - graph.positions[vertex];
                e -= n * Vector3.Dot(n, e);

                var x = Vector3.Dot(e, u);
                var y = Vector3.Dot(e, v);
                if (!(x * x + y * y > 0f)) continue;

                var angle = Mathf.Atan2(y, x) * 4f;
                sumCos += Mathf.Cos(angle);
                sumSin += Mathf.Sin(angle);
                counted++;
            }

            if (counted < 2) return u;

            // Coherence: 1 when every neighbour agrees on the same square, 0 when they cancel.
            // Below a quarter there is no square to find and the fitted angle would be noise.
            var coherence = Mathf.Sqrt(sumCos * sumCos + sumSin * sumSin) / counted;
            if (coherence < 0.25f) return u;

            var phi = Mathf.Atan2(sumSin, sumCos) * 0.25f;
            return u * Mathf.Cos(phi) + v * Mathf.Sin(phi);
        }

        public static int Sector(QuadGraph graph, int vertex, int other,
            out float alignment, out float span)
        {
            return Sector(graph, vertex, other, graph.directions[vertex], out alignment, out span);
        }

        public static int Sector(QuadGraph graph, int vertex, int other, Vector3 frame,
            out float alignment, out float span)
        {
            var n = graph.normals[vertex];
            var u = frame;
            var v = Vector3.Cross(n, u);

            var e = graph.positions[other] - graph.positions[vertex];
            span = e.magnitude;
            e -= n * Vector3.Dot(n, e);

            var x = Vector3.Dot(e, u);
            var y = Vector3.Dot(e, v);

            var length = Mathf.Sqrt(x * x + y * y);
            if (!(length > 0f))
            {
                // Two clusters at the same place in the tangent plane. No direction to read, and
                // treating it as squarely aligned would let it win a contest it has no claim on.
                alignment = 0f;
                return 0;
            }

            if (Mathf.Abs(x) >= Mathf.Abs(y))
            {
                alignment = Mathf.Abs(x) / length;
                return x > 0f ? 0 : 2;
            }

            alignment = Mathf.Abs(y) / length;
            return y > 0f ? 1 : 3;
        }

        public static DirectionStats Run(QuadGraph graph, bool apply, float scale)
        {
            var stats = new DirectionStats();
            if (graph == null) return stats;

            var capacity = graph.capacity;
            var best = new int[capacity * 4];
            var bestAlignment = new float[capacity * 4];
            var claimants = new int[capacity * 4];
            var frames = new Vector3[capacity];

            // Both readings, because they answer different questions and the difference between
            // them is itself the measurement. occupied is the ring judged against the cross field;
            // occupiedFitted is the ring judged against its own best square.
            for (var c = 0; c < capacity; c++)
                frames[c] = graph.alive[c] ? graph.directions[c] : Vector3.right;

            Survey(graph, frames, best, bestAlignment, claimants, stats.occupied, null);

            for (var c = 0; c < capacity; c++)
                if (graph.alive[c]) frames[c] = FitFrame(graph, c);

            Survey(graph, frames, best, bestAlignment, claimants, stats.occupiedFitted, stats);

            if (!apply)
            {
                System.Array.Copy(stats.occupiedFitted, stats.occupiedAfter, 5);
                return stats;
            }

            RemoveSurplus(graph, frames, best, stats);

            // Re-survey: a removal frees the direction it was contesting, and completion must not
            // fill a vacancy that no longer exists or re-add what was just taken out. The frames
            // are refitted too — a ring that lost its worst neighbour has a better square in it.
            for (var c = 0; c < capacity; c++)
                if (graph.alive[c]) frames[c] = FitFrame(graph, c);

            Survey(graph, frames, best, bestAlignment, claimants, null, null);
            Complete(graph, frames, best, claimants, scale, stats);

            for (var c = 0; c < capacity; c++)
                if (graph.alive[c]) frames[c] = FitFrame(graph, c);

            Survey(graph, frames, best, bestAlignment, claimants, stats.occupiedAfter, null);

            return stats;
        }

        /// <summary>
        /// Fills the best-claimant and claimant-count tables. <paramref name="histogram"/> and
        /// <paramref name="stats"/> are optional so the same walk serves the measurement and the
        /// two re-surveys.
        /// </summary>
        static void Survey(QuadGraph graph, Vector3[] frames, int[] best, float[] bestAlignment, int[] claimants,
            int[] histogram, DirectionStats stats)
        {
            for (var i = 0; i < best.Length; i++) { best[i] = -1; bestAlignment[i] = 0f; claimants[i] = 0; }

            for (var c = 0; c < graph.capacity; c++)
            {
                if (!graph.alive[c]) continue;

                if (stats != null) stats.vertices++;

                foreach (var m in graph.Neighbours(c))
                {
                    var sector = Sector(graph, c, m, frames[c], out var alignment, out _);
                    var slot = c * 4 + sector;

                    claimants[slot]++;
                    if (best[slot] >= 0 && alignment <= bestAlignment[slot]) continue;

                    best[slot] = m;
                    bestAlignment[slot] = alignment;
                }

                var occupiedHere = 0;
                for (var s = 0; s < 4; s++)
                {
                    var slot = c * 4 + s;
                    if (claimants[slot] == 0) continue;

                    occupiedHere++;
                    if (stats == null || claimants[slot] <= 1) continue;

                    stats.contestedDirections++;
                    stats.surplusNeighbours += claimants[slot] - 1;
                }

                if (stats != null) stats.vacantDirections += 4 - occupiedHere;
                if (histogram != null) histogram[occupiedHere]++;
            }
        }

        static void RemoveSurplus(QuadGraph graph, Vector3[] frames, int[] best, DirectionStats stats)
        {
            // Collect first, remove second. Removing inside the walk would change the valences the
            // remaining decisions are read against, making the result depend on vertex order.
            var doomed = new List<long>();

            for (var c = 0; c < graph.capacity; c++)
            {
                if (!graph.alive[c]) continue;

                foreach (var m in graph.Neighbours(c))
                {
                    if (m <= c) continue;   // each undirected edge considered once

                    var here = Sector(graph, c, m, frames[c], out _, out _);
                    var there = Sector(graph, m, c, frames[m], out _, out _);

                    var surplusHere = best[c * 4 + here] != m;
                    var surplusThere = best[m * 4 + there] != c;

                    if (!surplusHere && !surplusThere) continue;
                    if (!surplusHere || !surplusThere) { stats.keptOneSided++; continue; }
                    if (graph.IsProtected(c, m)) { stats.keptProtected++; continue; }

                    // Valence 2 survives — GraphCleanup splices it — but valence 1 is a spur that
                    // gets pruned along with whatever it was holding on to.
                    if (graph.Valence(c) <= 2 || graph.Valence(m) <= 2)
                    {
                        stats.keptWouldStrand++;
                        continue;
                    }

                    doomed.Add(EdgeKey.Pack(c, m));
                }
            }

            foreach (var key in doomed)
            {
                var a = EdgeKey.First(key);
                var b = EdgeKey.Second(key);

                // An earlier removal in this batch may have taken one of these down to where the
                // next one would strand it.
                if (graph.Valence(a) <= 2 || graph.Valence(b) <= 2) { stats.keptWouldStrand++; continue; }

                graph.RemoveEdge(a, b);
                stats.edgesRemoved++;
            }
        }

        /// <summary>
        /// Adds the edge a vacant direction is asking for, when the cluster across the gap is asking
        /// for the matching one.
        /// </summary>
        /// <remarks>
        /// Candidates come from the two-ring, which is where a missing lattice neighbour is: the
        /// edge that should reach it is absent, so the only route is around one of the diagonals
        /// that is present.
        ///
        /// Mutual need is the load-bearing condition. It is not enough that c wants a neighbour in
        /// +u and m happens to sit there — m must have nothing in the direction facing back at c,
        /// which for a real lattice gap is exactly what the far side looks like. Without that
        /// condition this pass would hang extra edges off whichever clusters happened to be short,
        /// raising valences past four and turning the tracer's tightest-turn walk back into a
        /// triangle finder.
        /// </remarks>
        static void Complete(QuadGraph graph, Vector3[] frames, int[] best, int[] claimants, float scale,
            DirectionStats stats)
        {
            var minSpan = minCompletionSpan * scale;
            var maxSpan = maxCompletionSpan * scale;

            var proposals = new List<long>();
            var twoRing = new List<int>(32);

            for (var c = 0; c < graph.capacity; c++)
            {
                if (!graph.alive[c]) continue;

                var vacant = 0;
                for (var s = 0; s < 4; s++)
                    if (claimants[c * 4 + s] == 0) vacant++;

                if (vacant == 0) continue;

                twoRing.Clear();
                foreach (var m in graph.Neighbours(c))
                    foreach (var k in graph.Neighbours(m))
                        if (k != c && !graph.AreAdjacent(c, k)) twoRing.Add(k);

                for (var s = 0; s < 4; s++)
                {
                    if (claimants[c * 4 + s] != 0) continue;

                    var pick = -1;
                    var pickAlignment = 0f;
                    var sawPlausible = false;
                    var sawAny = false;
                    var sawInBand = false;

                    foreach (var k in twoRing)
                    {
                        var sector = Sector(graph, c, k, frames[c], out var alignment, out var span);
                        if (sector != s) continue;

                        // Recorded before the gates, so a vacancy refused for want of a candidate
                        // can be told apart from one refused for want of a candidate THIS SQUARE.
                        // The distinction decides whether the lattice is missing a cell or the
                        // gates are too tight, and those have opposite fixes.
                        sawAny = true;
                        if (span >= minSpan && span <= maxSpan) sawInBand = true;

                        if (span < minSpan || span > maxSpan) continue;
                        if (alignment < minCompletionAlignment) continue;

                        sawPlausible = true;

                        // Mutual need: the direction on k that faces back at c must be vacant too.
                        var backSector = Sector(graph, k, c, frames[k], out _, out _);
                        if (claimants[k * 4 + backSector] != 0) continue;

                        if (alignment <= pickAlignment) continue;

                        pick = k;
                        pickAlignment = alignment;
                    }

                    if (pick < 0)
                    {
                        // Separated so the two are not confused: no candidate at all means the
                        // lattice really has no cluster there, while a plausible candidate that
                        // was not asking for the matching direction means the gap is one-sided
                        // and closing it would push the far side past valence four.
                        if (sawPlausible) stats.refusedNotMutual++;
                        else if (sawInBand) stats.refusedAngle++;
                        else if (sawAny) stats.refusedSpan++;
                        else stats.refusedNoCandidate++;
                        continue;
                    }

                    // Reserve both directions immediately, so two vacancies on the same cluster
                    // cannot both be filled by the same neighbour and a later cluster cannot claim
                    // a direction this proposal has already taken.
                    claimants[c * 4 + s] = 1;
                    best[c * 4 + s] = pick;

                    var back = Sector(graph, pick, c, frames[pick], out _, out _);
                    claimants[pick * 4 + back] = 1;
                    best[pick * 4 + back] = c;

                    proposals.Add(EdgeKey.PackUndirected(c, pick));
                }
            }

            foreach (var key in proposals)
            {
                var a = EdgeKey.First(key);
                var b = EdgeKey.Second(key);
                if (graph.AreAdjacent(a, b)) continue;

                graph.AddEdge(a, b);
                stats.edgesAdded++;
            }
        }
    }
}
