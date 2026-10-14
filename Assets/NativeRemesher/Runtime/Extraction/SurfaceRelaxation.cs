// Pass I: smooth the output vertices across the surface, then put them back on it.
//
// WHAT THIS FIXES, measured rather than assumed. At QuadRemesher's own face count on the character,
// our output and theirs have the same mean edge length (0.452 % of the bbox diagonal against
// 0.449 %) and our MEDIAN dihedral angle between neighbouring faces is the better of the two —
// 7.7 degrees against 8.6. The bulk of the surface is not the problem, and that is why the shape
// reads correctly.
//
// The tail is the problem:
//
//     angle between neighbours   ours     QuadRemesher
//     > 45 deg                   8.37 %   2.73 %
//     > 60 deg                   6.00 %   1.12 %
//     > 90 deg                   2.05 %   0.17 %       <- 925 edges folded past perpendicular
//     worst                    179.8 deg  154.7 deg
//
// 925 creases scattered over an otherwise smooth surface is exactly what the user described: a
// shape that is basically right with "a lot of small spots" that are wrong, curled, and lit as
// though something is there that is not. Mean deviation from the original cannot see this at all —
// ours is 0.0053 % against their 0.0202 %, so by that measure we are four times the better mesh.
// Deviation measures where the vertices are. This measures how they are arranged, and an
// arrangement that zigzags is invisible to the first and decisive for the second.
//
// WHY SMOOTHING, AND WHY REPROJECTION. Instant Meshes — which the rest of this pipeline is a port
// of — places each output vertex at a lattice point and stops. Nothing downstream of that is
// responsible for how one vertex sits relative to its neighbours, and nothing has to be for the
// algorithm to be correct: a lattice point half a cell off is still a valid lattice point. It just
// shades badly. Every production retopologiser closes that gap the same way, with a
// smooth-and-reproject relaxation, and it is the pass we do not have.
//
// Reprojection is what makes it safe. Plain Laplacian smoothing shrinks a closed surface and
// rounds off everything it touches; snapping each smoothed vertex back to the nearest point of the
// input surface removes that entirely, because the tangential rearrangement is kept and the normal
// component — the shrinkage — is undone. The result moves vertices ALONG the surface into a better
// arrangement without moving them OFF it.
//
// FIVE GUARDS, each against a way this pass could manufacture a defect of its own. The last two
// were added after the pass had already shipped and damaged something: guard 4 after the corpus
// caught it rounding the creases off a cube, guard 5 after the user noticed a thumb was no longer
// complete. Both were invisible to every metric this file was tuned against.
//
//   1. A move is rejected if any face at that vertex would end up facing the other way. Adding a
//      flipped face while removing creases would trade one visible defect for a worse one.
//   2. A move is rejected if the reprojection had to travel further than `maxProjection` of a cell.
//      Where two sheets of the surface come close — under an arm, inside a fold — the nearest
//      point can be on the wrong sheet, and a vertex that jumps sheets tears the mesh.
//   3. Boundary vertices never move. There is nothing on the far side to average against, so the
//      umbrella would pull the rim inwards and every open edge would creep.
//   4. Vertices in a crease of the INPUT never move either, and vertices whose move would cross
//      one are refused. Smoothing has no way to tell a crease worth keeping from a fold worth
//      removing; the input does, so the question is asked there.
//   5. No vertex may end up more than `maxDrift` of a cell from where extraction put it. Smoothing
//      migrates vertices away from extremities - at a fingertip every neighbour is further down the
//      finger, so the umbrella points down the finger and reprojection lands the vertex on the
//      finger's side rather than back at the tip - and twenty-four sweeps of that walk a fingertip
//      off the end of a finger. See defaultMaxDrift.
//
// Jacobi, not Gauss-Seidel: every vertex reads the previous iteration's positions, so the result
// does not depend on vertex order. The solvers upstream are Gauss-Seidel and had to be given a
// deterministic sweep order to be reproducible at all (see RemeshTopology.BuildAdjacency); this
// pass has no such requirement to inherit because it never reads a value it wrote.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class RelaxStats
    {
        public int iterations;
        public int movable;
        public int moves;
        public int rejectedFlip;
        public int rejectedProjection;
        public int rejectedFeature;
        public int rejectedDrift;

        /// <summary>Vertices found to sit in a crease of the input.</summary>
        public int pinnedByFeature;

        /// <summary>Crease vertices free to slide along the crease but not off it.</summary>
        public int slidingOnCrease;

        /// <summary>Crease vertices held still because the crease has no single direction there.</summary>
        public int pinnedAsJunction;

        /// <summary>Distance the average moved vertex travelled, in units of the lattice cell.</summary>
        public float meanDisplacement;

        /// <summary>The furthest any vertex travelled, in units of the lattice cell.</summary>
        public float maxDisplacement;

        public override string ToString()
        {
            return $"iters={iterations} movable={movable} moves={moves} " +
                   $"rejFlip={rejectedFlip} rejProj={rejectedProjection} rejFeature={rejectedFeature} rejDrift={rejectedDrift} onCrease={pinnedByFeature} sliding={slidingOnCrease} junction={pinnedAsJunction} " +
                   $"meanMove={meanDisplacement:F3} maxMove={maxDisplacement:F3} cells";
        }
    }

    public static class SurfaceRelaxation
    {
        /// <summary>
        /// How far each vertex moves toward its neighbours' centroid per sweep.
        /// </summary>
        /// <remarks>
        /// Half, and 1 is the stability limit of the operator rather than a value to use. Swept on
        /// the character at 24 sweeps: 0.8 is the better row on the headline number (2.42 % of edges
        /// past 45 degrees against 0.5's 2.94 %, which beats QuadRemesher's 2.73 %) but it starts
        /// bouncing vertices off the surface — `rejProj` goes from 0 to 74 — and 1.0 is worse on
        /// every column at once (curled 0.59 % against 0.39 %, min angle p01 26.9 against 28.5,
        /// `rejProj` 459). Overshoot shows up as rejections before it shows up as damage, so 0.5 is
        /// chosen for the margin: it is the only setting measured that refuses nothing.
        /// </remarks>
        public const float defaultStrength = 0.5f;

        /// <summary>
        /// Sweeps. A ceiling rather than a cost — <see cref="defaultConvergence"/> stops the pass
        /// as soon as the mesh settles, and on the character 96 stops itself at 42.
        /// </summary>
        /// <remarks>
        /// Measured on the character (% of edges past 45 degrees, then past 90, then relaxation
        /// seconds): off 8.37 / 2.05 / 0; 4 sweeps 4.64 / 0.40 / 4.5; 12 sweeps 3.29 / 0.29 / 13.6;
        /// 24 sweeps 2.94 / 0.26 / 27; 48 sweeps 2.68 / 0.23 / 48. Returns are steeply diminishing
        /// and the cost is linear, so this sits at the knee rather than at the best row. Going to 48
        /// buys another 9 % for another 21 seconds and is a reasonable thing for a caller who cares
        /// more about the output than the wait to ask for.
        /// </remarks>
        public const int defaultIterations = 24;

        /// <summary>
        /// Furthest a smoothed vertex may be from the input surface, in lattice cells, before the
        /// move is refused. Half a cell is generous for the intended correction and far short of
        /// the distance to another sheet of the surface at any sane target count.
        /// </summary>
        /// <remarks>
        /// Measured on the character it never binds — `rejProj` is 0 across the whole sweep, at 4
        /// iterations and at 24. That is the result to want from a guard: it is a floor under the
        /// failure case, not a parameter doing work, and it is recorded here so a future sweep that
        /// DOES show rejections is read as "something upstream changed" rather than "tune it up".
        /// </remarks>
        public const float defaultMaxProjection = 0.5f;

        /// <summary>
        /// How far the input surface may turn between where a vertex is and where a move would put
        /// it, in degrees, before the move is refused as crossing a crease.
        /// </summary>
        /// <remarks>
        /// THIS IS THE GUARD THAT MAKES THE PASS SAFE ON ANYTHING WITH HARD EDGES, and it was added
        /// because the pass shipped without it and the corpus caught the damage. Measured A/B on
        /// `cube_subdivided` at 2,000 quads: a perfect result — Chamfer 0, Hausdorff 0, zero flipped
        /// normals — became Chamfer 0.0012, Hausdorff 0.034, and 17 % of normals flipped.
        /// `cylinder_capped` lost a factor of 24 on Hausdorff at the rim.
        ///
        /// The reasoning that let it through was that reprojection cannot round a crease off,
        /// because a crease is part of the surface and a vertex smoothed off it is projected back
        /// onto it. That is wrong, and the direction matters: smoothing moves a crease vertex ALONG
        /// the surface, perpendicular to the crease line and down onto one of the two faces. The
        /// nearest point of the input to where it lands is on that face. Nothing pulls it back.
        ///
        /// The test is the angle between the INPUT surface normal where the vertex is now and where
        /// the move would put it. Both are ground truth, so this asks only "does this move cross a
        /// sharp edge of the thing being remeshed" — not "is the output rough here", which is the
        /// question that cannot distinguish a crease worth keeping from a defect worth removing.
        /// The character's own defects sit on a smooth surface and are not refused by it.
        ///
        /// ZERO OR LESS DISABLES IT, and that is a setting with a real use rather than an escape
        /// hatch. The pipeline passes the profile's own <c>featureAngleDegrees</c> here, so crease
        /// protection is on exactly for the profiles that assert their input has creases, and off
        /// for the two that assert it does not — the scan profiles, which already switch feature
        /// DETECTION off for the same reason: on a scan the things that look like creases at this
        /// scale are folds and noise, not edges anyone wants kept.
        ///
        /// That is not a preference, it is the measurement. Run with protection on at 40 degrees,
        /// the character pins 4,203 of its 22,246 vertices — 19 % — and the pass's own result goes
        /// from 2.94 % of edges past 45 degrees back to 7.03 %, against 8.37 % for not running it at
        /// all. Nearly the whole win, spent protecting features that a scan does not have. The same
        /// protection on the cube is the difference between a perfect result and a ruined one.
        /// Neither setting is right for both inputs, which is what a profile is for.
        /// </remarks>
        public const float defaultFeatureAngleDegrees = 40f;

        /// <summary>
        /// How far a vertex may end up from where extraction put it, in lattice cells, over the
        /// whole pass. Zero or less removes the cap.
        /// </summary>
        /// <remarks>
        /// THIS IS THE GUARD AGAINST LOSING GEOMETRY, and it exists because the pass shipped without
        /// it and quietly amputated a thumb.
        ///
        /// Smoothing migrates vertices away from extremities and there is nothing in
        /// smooth-and-reproject to stop it. At a fingertip every ring neighbour is further down the
        /// finger, so the umbrella target is down the finger; reprojection puts the vertex back on
        /// the surface, but on the SIDE of the finger, not back at the tip. Each sweep walks the tip
        /// ring a little further down, and after twenty-four of them the tip is not represented by
        /// any vertex at all. The same thing happens to any thin protrusion: fingers, a nose, hair.
        ///
        /// None of the smoothness metrics can see this. They are computed over the faces that exist,
        /// and a mesh that dropped a fingertip has beautifully behaved faces everywhere it still is.
        /// Nor can deviation, which measures our vertices to the input surface and is near zero by
        /// construction. Only coverage — the input surface to OUR mesh — sees it, and measured that
        /// way relaxation took the character from 0.049 % of the surface uncovered to 0.898 %, an
        /// eighteen-fold regression against QuadRemesher's 0.146 %, concentrated in a symmetric pair
        /// of blobs at the hands. See Benchmark/CoverageAudit.cs.
        ///
        /// A cap is the right shape of fix because the defect this pass removes is LOCAL: a folded
        /// quad is fixed by its vertex moving a fraction of a cell. Long-range migration buys
        /// nothing and costs the shape, so bounding total travel keeps all of the first and none of
        /// the second.
        ///
        /// Half a cell, swept on the character against coverage AND smoothness together, because
        /// the two trade directly and a sweep of either alone picks the wrong value. Re-swept after
        /// curvature-graded sizing landed, since grading changes what the cap has to protect: it
        /// resolves the fingertips on its own, so the only coverage left to lose is what this pass
        /// itself takes away. Uncovered surface / deepest unrepresented hole / edges folded past 45
        /// degrees, all as a percentage:
        ///
        ///   uncapped 0.505 / -     / 4.18      0.40 cell 0.015 / 0.410 / 5.19
        ///   1.00 cell 0.455 / -     / 4.15     0.35 cell 0.012 / 0.410 / 5.68
        ///   0.50 cell 0.036 / 0.448 / 4.55     0.25 cell 0.009 / 0.343 / 7.20
        ///                                      off       0.007 / 0.326 / 11.09
        ///
        /// The knee is still sharp and still at 0.5: uncovered surface falls thirteen-fold from
        /// 1.0 to 0.5 for 0.4 points of smoothness, and tightening past 0.5 costs smoothness fast
        /// for hundredths of a point of coverage. At 0.5 we lose a quarter as much geometry as
        /// QuadRemesher (0.036 %% against 0.146 %%) and the deepest hole is 0.448 %% against 0.755 %%.
        ///
        /// REFUTED, and worth recording because the argument for it is good: measuring the cap in
        /// each vertex's OWN cell, taken from the output edges meeting it, rather than in the mean
        /// cell. Under a graded lattice the mean is plainly the wrong unit and wrong in the worst
        /// direction — loosest where the quads are smallest, which is where the detail is. Swept
        /// the same way, it is not an improvement: matched for smoothness it covers LESS
        /// (0.069 %% uncovered at 4.57 against 0.036 %% at 4.55), and at equal drift it is worse on
        /// every quality measure. The reason appears to be that grading has already made the cells
        /// small at the extremities, so a local cap tightens where protection is no longer needed
        /// while also tightening wherever extraction happened to leave short edges — near
        /// singularities, at collapsed clusters — where drifting was harmless and smoothing was
        /// worth having.
        /// </remarks>
        public const float defaultMaxDrift = 0.5f;

        /// <summary>
        /// Mean per-sweep displacement, in lattice cells, below which the pass stops early.
        /// </summary>
        /// <remarks>
        /// Laplacian smoothing converges geometrically and slowly, so the iteration count that is
        /// right for a rough mesh is mostly wasted on a smooth one. With this, the count is a
        /// ceiling rather than a cost, which is what lets the default be generous.
        /// </remarks>
        public const float defaultConvergence = 0.002f;

        public static RelaxStats Run(List<int[]> faces, Vector3[] positions, SpatialGrid surface,
            float scale, int iterations = defaultIterations, float strength = defaultStrength,
            float maxProjection = defaultMaxProjection, float convergence = defaultConvergence,
            float featureAngleDegrees = defaultFeatureAngleDegrees, float maxDrift = defaultMaxDrift,
            bool[] pinned = null)
        {
            if (faces == null) throw new ArgumentNullException(nameof(faces));
            if (positions == null) throw new ArgumentNullException(nameof(positions));

            var stats = new RelaxStats { iterations = 0 };
            if (surface == null || surface.triangleCount == 0 || positions.Length == 0) return stats;

            strength = Mathf.Clamp(strength, 0f, 1f);
            if (iterations <= 0 || strength <= 0f) return stats;

            BuildRings(faces, positions.Length, out var ringStart, out var ring, out var onBoundary);
            // Caller-pinned vertices (vertices already placed on a crease by CreaseLoopSnap) are
            // held exactly as a boundary is.
            if (pinned != null)
                for (var v = 0; v < positions.Length && v < pinned.Length; v++)
                    if (pinned[v]) onBoundary[v] = true;

            // Parallel to onBoundary, and deliberately NOT the same array: a mesh boundary has
            // nothing on the far side to average against and must stay pinned, while a crease has a
            // direction and only needs one degree of freedom removed.
            var onCrease = new bool[positions.Length];
            var creaseTangent = new Vector3[positions.Length];
            BuildIncidence(faces, positions.Length, out var faceStart, out var faceOf);

            // Above 1 no dot can reach it, so every comparison against it passes and both crease
            // tests below become no-ops without a branch of their own.
            var protectCreases = featureAngleDegrees > 0f;
            var minNormalDot = protectCreases
                ? Mathf.Cos(Mathf.Min(featureAngleDegrees, 180f) * Mathf.Deg2Rad)
                : -2f;
            var maxDistance = maxProjection * scale;

            // Where extraction left each vertex, kept for the whole pass so the cap bounds TOTAL
            // travel rather than per-sweep travel. Per-sweep is already small — a hundredth of a
            // cell on average — and capping that would have caught nothing: the damage is
            // twenty-four small steps all pointing the same way.
            var anchor = (Vector3[])positions.Clone();
            var maxDriftDistance = maxDrift > 0f ? maxDrift * scale : float.PositiveInfinity;

            // The input surface's normal under each vertex, seeded once and then carried forward on
            // every accepted move. Sampling it per sweep instead would double the nearest-point
            // queries, and those are the entire cost of this pass.
            var surfaceNormal = new Vector3[positions.Length];
            for (var v = 0; v < positions.Length; v++)
            {
                if (onBoundary[v]) continue;
                surface.NearestPoint(positions[v], out _, out surfaceNormal[v]);
            }

            // Vertices sitting in a crease of the input are pinned outright, before any sweep.
            //
            // Refusing individual moves is not enough, and the reason is worth keeping: a vertex ON
            // a convex crease has its neighbours' centroid INSIDE the solid, and the nearest surface
            // point to an interior point near a 90-degree edge is on one of the two faces, a step
            // away from the edge. Both the vertex and where it lands are on the same face, so a test
            // that compares the two normals sees no change and allows the move. The crease is shaved
            // one step at a time by moves that each look locally innocent.
            for (var v = 0; protectCreases && v < positions.Length; v++)
            {
                if (onBoundary[v] || surfaceNormal[v] == Vector3.zero) continue;

                // Sampled at the midpoints to the ring neighbours: near enough to v that this is a
                // statement about v's own neighbourhood, far enough to straddle a crease running
                // through it.
                for (var k = ringStart[v]; k < ringStart[v + 1]; k++)
                {
                    var midpoint = (positions[v] + positions[ring[k]]) * 0.5f;
                    surface.NearestPoint(midpoint, out _, out var nearby);
                    if (nearby == Vector3.zero) continue;
                    if (Vector3.Dot(surfaceNormal[v], nearby) >= minNormalDot) continue;

                    onCrease[v] = true;
                    stats.pinnedByFeature++;
                    break;
                }
            }

            // THE CREASE TANGENT, and why a crease vertex is no longer frozen.
            //
            // Marking a crease vertex as boundary pinned it forever, and on the armour asset that
            // froze 2,715 output vertices of about 4,900 - more than half the mesh, including the
            // whole collar. The user's description was "there is 0 smoothing", which is exactly what
            // it was: the choice on offer was a smooth surface with no creases (features off) or
            // crisp creases on a jagged surface (features on), because the one pass that smooths was
            // forbidden from touching the places that most needed it.
            //
            // A crease vertex has one degree of freedom, not zero. It may slide ALONG the crease -
            // which is what evens out spacing and takes the crumple out of a collar - and must not
            // step off it. The direction comes from the vertex's own ring: the neighbours that are
            // themselves on the crease are the crease, so the line through them is its tangent. Two
            // such neighbours give a chord; one gives the direction to it; three or more is a
            // junction, where there is no single line to slide along and pinning is right.
            for (var v = 0; v < positions.Length; v++)
            {
                if (!onCrease[v] || onBoundary[v]) continue;

                // THE THROUGH-LINE, not "exactly two neighbours". A first version took the chord
                // between the two crease neighbours and pinned anything with three or more as a
                // junction. On a clean mechanical part that is right and on this asset it was
                // useless: 11,574 detected creases make a dense web rather than tidy polylines, so
                // nearly every crease vertex has three or four crease neighbours and nearly every
                // one got pinned. Smoothing moves did not move at all - 30,959 to 30,108.
                //
                // So instead: among the crease neighbours, take the PAIR POINTING MOST NEARLY
                // OPPOSITE. A crease running through a vertex has a neighbour on each side, and that
                // pair is the crease whatever else is also flagged nearby. A genuine junction - a
                // corner where two creases cross - has no opposed pair and still pins, which is what
                // should happen there.
                var found = 0;
                var bestDot = -0.5f;   // at least 120 degrees apart to count as a line through v
                var a = -1;
                var b = -1;

                for (var k = ringStart[v]; k < ringStart[v + 1]; k++)
                {
                    if (!onCrease[ring[k]]) continue;
                    found++;

                    var dk = positions[ring[k]] - positions[v];
                    if (dk.magnitude < 1e-5f) continue;
                    dk = dk.normalized;

                    for (var m = k + 1; m < ringStart[v + 1]; m++)
                    {
                        if (!onCrease[ring[m]]) continue;

                        var dm = positions[ring[m]] - positions[v];
                        if (dm.magnitude < 1e-5f) continue;

                        var dot = Vector3.Dot(dk, dm.normalized);
                        if (dot >= bestDot) continue;

                        bestDot = dot;
                        a = ring[k];
                        b = ring[m];
                    }
                }

                if (a >= 0 && b >= 0)
                    creaseTangent[v] = positions[b] - positions[a];
                else if (found >= 1)
                {
                    // One arm only - the end of a crease. It may still slide along that arm, which
                    // is what lets the last vertex of a panel edge even out with the rest of it.
                    for (var k = ringStart[v]; k < ringStart[v + 1]; k++)
                        if (onCrease[ring[k]]) { creaseTangent[v] = positions[ring[k]] - positions[v]; break; }
                }

                // SafeNormalize's floor is why the test is against 1e-5 rather than zero.
                if (creaseTangent[v].magnitude < 1e-5f)
                {
                    creaseTangent[v] = Vector3.zero;
                    onBoundary[v] = true;
                    stats.pinnedAsJunction++;
                }
                else
                {
                    creaseTangent[v] = creaseTangent[v].normalized;
                    stats.slidingOnCrease++;
                }
            }

            // Counted after pinning, so `movable` means what the sweeps will actually visit.
            var movable = 0;
            for (var v = 0; v < positions.Length; v++)
                if (!onBoundary[v] && ringStart[v + 1] > ringStart[v]) movable++;
            stats.movable = movable;

            var current = positions;
            var next = new Vector3[positions.Length];

            // Face normals before the sweep, so the flip test compares against the arrangement the
            // whole iteration started from rather than a half-updated one.
            var before = new Vector3[faces.Count];

            var totalDisplacement = 0f;
            var displacementCount = 0;
            var outcome = new byte[positions.Length];
            var displaced = new float[positions.Length];

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                for (var f = 0; f < faces.Count; f++)
                    before[f] = Newell(current, faces[f]);

                Array.Copy(current, next, current.Length);

                var moved = 0;
                var sweepDisplacement = 0f;

                // Every vertex reads `current` and writes only its own slot of `next`, so the sweep
                // runs in parallel; the outcome is tallied afterwards in vertex order, which keeps
                // the floating-point sums -- and the convergence test they feed -- identical to a
                // sequential sweep. It was 5.5 s of the body scan's 37 s run on one core.
                var sweepCurrent = current;
                var sweepNext = next;
                System.Threading.Tasks.Parallel.For(0, current.Length, BackgroundScheduler.Options, v =>
                {
                    outcome[v] = 0;
                    if (onBoundary[v]) return;

                    var from = ringStart[v];
                    var to = ringStart[v + 1];
                    if (to <= from) return;

                    var centroid = Vector3.zero;
                    for (var k = from; k < to; k++) centroid += sweepCurrent[ring[k]];
                    centroid /= to - from;

                    var step = (centroid - sweepCurrent[v]) * strength;

                    // A crease vertex keeps only the component of the umbrella that runs along the
                    // crease. The perpendicular component is precisely the one that would round the
                    // crease off, and dropping it is what lets this pass smooth a collar without
                    // flattening its rim.
                    if (creaseTangent[v] != Vector3.zero)
                        step = creaseTangent[v] * Vector3.Dot(step, creaseTangent[v]);

                    var candidate = sweepCurrent[v] + step;
                    candidate = surface.NearestPoint(candidate, out var distance, out var landed);

                    // Guard 2. `distance` is measured from the SMOOTHED point, so it answers "how
                    // far off the surface did smoothing take this vertex", which is the quantity
                    // that says whether the projection is a correction or a jump.
                    if (!(distance <= maxDistance))
                    {
                        outcome[v] = 2;
                        return;
                    }

                    // Guard 5. Measured from the anchor, so twenty-four steps that all point the
                    // same way are caught even though each one on its own is a hundredth of a cell.
                    if ((candidate - anchor[v]).sqrMagnitude > maxDriftDistance * maxDriftDistance)
                    {
                        outcome[v] = 3;
                        return;
                    }

                    // Guard 3. Both normals are read off the INPUT, so this asks only whether the
                    // move crosses a sharp edge of the surface being remeshed. The same question
                    // asked of the OUTPUT's own dihedral angles cannot be answered: a crease worth
                    // keeping and a fold worth removing look identical there, and this pass exists
                    // to remove the second kind.
                    // Not asked of a crease vertex. That test rejects a move whose landing normal
                    // disagrees with the vertex's own, which is the definition of sitting ON a
                    // crease - so asking it here would refuse every constrained slide and put the
                    // freeze back by another route.
                    if (creaseTangent[v] == Vector3.zero &&
                        surfaceNormal[v] != Vector3.zero && landed != Vector3.zero &&
                        Vector3.Dot(surfaceNormal[v], landed) < minNormalDot)
                    {
                        outcome[v] = 4;
                        return;
                    }

                    // Guard 1.
                    if (WouldFlip(faces, sweepCurrent, before, faceOf, faceStart[v], faceStart[v + 1],
                            v, candidate))
                    {
                        outcome[v] = 5;
                        return;
                    }

                    displaced[v] = (candidate - sweepCurrent[v]).magnitude;
                    sweepNext[v] = candidate;
                    if (landed != Vector3.zero) surfaceNormal[v] = landed;
                    outcome[v] = 1;
                });

                for (var v = 0; v < current.Length; v++)
                {
                    switch (outcome[v])
                    {
                        case 2: stats.rejectedProjection++; continue;
                        case 3: stats.rejectedDrift++; continue;
                        case 4: stats.rejectedFeature++; continue;
                        case 5: stats.rejectedFlip++; continue;
                        case 1: break;
                        default: continue;
                    }

                    var displacement = displaced[v];
                    moved++;

                    sweepDisplacement += displacement;
                    totalDisplacement += displacement;
                    displacementCount++;
                    if (displacement > stats.maxDisplacement * scale)
                        stats.maxDisplacement = displacement / Mathf.Max(1e-20f, scale);
                }

                stats.moves += moved;
                stats.iterations++;

                var swap = current;
                current = next;
                next = swap;

                if (moved == 0) break;

                // Averaged over the MOVABLE vertices, not the ones that happened to move, so a
                // sweep in which almost everything has settled reads as converged instead of being
                // held open by the handful still travelling.
                if (movable > 0 && sweepDisplacement / movable / Mathf.Max(1e-20f, scale) < convergence)
                    break;
            }

            // `current` alternates between the caller's array and the scratch one, so on an odd
            // number of completed sweeps the answer is in the scratch buffer and has to be copied
            // back. Getting this wrong silently returns the input, which looks exactly like a pass
            // that did nothing.
            if (!ReferenceEquals(current, positions))
                Array.Copy(current, positions, positions.Length);

            stats.meanDisplacement = displacementCount > 0
                ? totalDisplacement / displacementCount / Mathf.Max(1e-20f, scale)
                : 0f;

            return stats;
        }

        /// <summary>
        /// Would moving <paramref name="vertex"/> to <paramref name="candidate"/> reverse any of the
        /// faces around it, relative to <paramref name="before"/>?
        /// </summary>
        /// <remarks>
        /// Compared against the face's own previous normal rather than against a surface normal,
        /// because the question is whether this move reverses the face, not whether the face was
        /// pointing the right way to begin with. A face that was already wrong is the orienter's
        /// problem and must not be a veto here.
        /// </remarks>
        static bool WouldFlip(List<int[]> faces, Vector3[] positions, Vector3[] before,
            int[] faceOf, int from, int to, int vertex, Vector3 candidate)
        {
            // The candidate is substituted while the normal is summed, never written: the sweep
            // runs in parallel, and a write here would be read by a neighbour's test.
            for (var k = from; k < to; k++)
            {
                var f = faceOf[k];
                var was = before[f];
                if (was.sqrMagnitude <= 1e-24f) continue;

                var now = Newell(positions, faces[f], vertex, candidate);
                if (now.sqrMagnitude <= 1e-24f) return true;   // collapsed to nothing
                if (Vector3.Dot(was, now) <= 0f) return true;
            }

            return false;
        }

        /// <summary>
        /// Unnormalised Newell normal. Left unnormalised on purpose: at this mesh's scale a face
        /// normal's magnitude is around 1e-6, and <c>Vector3.normalized</c> returns zero below 1e-5,
        /// which would make every comparison above read as degenerate.
        /// </summary>
        static Vector3 Newell(Vector3[] positions, int[] face, int vertex, Vector3 at)
        {
            var normal = Vector3.zero;
            for (var k = 0; k < face.Length; k++)
            {
                var ia = face[k];
                var ib = face[(k + 1) % face.Length];
                var a = ia == vertex ? at : positions[ia];
                var b = ib == vertex ? at : positions[ib];
                normal += new Vector3(
                    (a.y - b.y) * (a.z + b.z),
                    (a.z - b.z) * (a.x + b.x),
                    (a.x - b.x) * (a.y + b.y));
            }
            return normal;
        }

        static Vector3 Newell(Vector3[] positions, int[] face)
        {
            var normal = Vector3.zero;
            for (var k = 0; k < face.Length; k++)
            {
                var a = positions[face[k]];
                var b = positions[face[(k + 1) % face.Length]];
                normal += new Vector3(
                    (a.y - b.y) * (a.z + b.z),
                    (a.z - b.z) * (a.x + b.x),
                    (a.x - b.x) * (a.y + b.y));
            }
            return normal;
        }

        /// <summary>
        /// One-ring neighbours of every vertex as CSR, plus which vertices sit on a boundary. A
        /// vertex is on the boundary when any edge at it is carried by fewer than two faces.
        /// </summary>
        static void BuildRings(List<int[]> faces, int vertexCount, out int[] ringStart,
            out int[] ring, out bool[] onBoundary)
        {
            var edgeUse = new Dictionary<long, int>(faces.Count * 4, EdgeKey.Comparer);
            var neighbours = new HashSet<long>(EdgeKey.Comparer);

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    if (a == b) continue;

                    var key = EdgeKey.PackUndirected(a, b);
                    edgeUse.TryGetValue(key, out var used);
                    edgeUse[key] = used + 1;
                    neighbours.Add(key);
                }
            }

            onBoundary = new bool[vertexCount];
            var degree = new int[vertexCount];

            foreach (var key in neighbours)
            {
                var a = EdgeKey.First(key);
                var b = EdgeKey.Second(key);
                if (a < 0 || a >= vertexCount || b < 0 || b >= vertexCount) continue;

                degree[a]++;
                degree[b]++;

                // Fewer than two faces is an open rim; more than two is non-manifold, where an
                // averaged position is not meaningful either. Both are pinned.
                if (edgeUse[key] != 2)
                {
                    onBoundary[a] = true;
                    onBoundary[b] = true;
                }
            }

            ringStart = new int[vertexCount + 1];
            for (var v = 0; v < vertexCount; v++) ringStart[v + 1] = ringStart[v] + degree[v];

            ring = new int[ringStart[vertexCount]];
            var cursor = new int[vertexCount];

            foreach (var key in neighbours)
            {
                var a = EdgeKey.First(key);
                var b = EdgeKey.Second(key);
                if (a < 0 || a >= vertexCount || b < 0 || b >= vertexCount) continue;

                ring[ringStart[a] + cursor[a]++] = b;
                ring[ringStart[b] + cursor[b]++] = a;
            }
        }

        /// <summary>Faces incident to every vertex, as CSR.</summary>
        static void BuildIncidence(List<int[]> faces, int vertexCount, out int[] faceStart,
            out int[] faceOf)
        {
            var degree = new int[vertexCount];
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null) continue;
                foreach (var v in face)
                    if (v >= 0 && v < vertexCount) degree[v]++;
            }

            faceStart = new int[vertexCount + 1];
            for (var v = 0; v < vertexCount; v++) faceStart[v + 1] = faceStart[v] + degree[v];

            faceOf = new int[faceStart[vertexCount]];
            var cursor = new int[vertexCount];

            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null) continue;
                foreach (var v in face)
                    if (v >= 0 && v < vertexCount) faceOf[faceStart[v] + cursor[v]++] = f;
            }
        }
    }
}
