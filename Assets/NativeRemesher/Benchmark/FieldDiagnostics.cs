// Scores the orientation and position fields *independently of extraction*.
//
// This file exists because of a specific failure in this project's history: stages 4 and 5 were
// judged together, the combined output was bad, and the blame landed on stage 4 — which then drove
// a plan to spend weeks building a multiresolution hierarchy. Every statistic used to reach that
// conclusion turned out to be measuring the wrong thing. So the rule here is that the field must be
// scorable on its own, with quantities whose meaning is unambiguous, before anyone reasons about
// what the extractor does with it.
//
// The single most informative number below is the edge-class distribution. If a healthy fraction of
// edges land in Collapse and Edge with few Discards, the position field is doing its job, whatever
// the extractor then makes of it.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public sealed class FieldReport
    {
        // --- orientation ---------------------------------------------------
        /// <summary>Mean cross-field agreement over edges, 1.0 = perfectly combed.</summary>
        public float orientationEnergy;
        public int orientationIterations;
        public float orientationResidual;

        // --- position ------------------------------------------------------
        /// <summary>Mean per-edge lattice fit residual, in lattice units. Converged tends to 0.</summary>
        public float latticeMismatch;
        public float latticeMismatchMax;
        public float representativeDrift;
        public float positionResidualRaw;   // the misleading legacy number, for continuity
        public int positionIterations;

        /// <summary>Fraction of edges whose endpoints share one lattice point.</summary>
        public float sameCellFraction;
        /// <summary>Fraction of edges exactly one lattice step apart — the real output edges.</summary>
        public float oneStepFraction;
        /// <summary>Fraction of edges too far apart or diagonal — carry no meaning.</summary>
        public float discardedFraction;

        /// <summary>
        /// The discarded fraction split by cause, because the two causes mean opposite things and
        /// the total conflates them.
        /// </summary>
        /// <remarks>
        /// A <b>diagonal</b> discard — the two lattice indices differ by exactly (1,1) — is
        /// expected and benign. The input is a triangle mesh; when a lattice cell is covered by two
        /// triangles, the shared edge of that pair *is* the cell's diagonal. Discarding it is
        /// correct, and a healthy field on a triangulated input has a substantial diagonal rate for
        /// purely combinatorial reasons.
        ///
        /// A <b>jump</b> discard — the indices differ by two or more cells along an axis — is a
        /// genuine defect. Two vertices one input edge apart landed two lattice cells apart, so the
        /// lattice is not merely rotated relative to the surface, it is torn: there is a cell
        /// between them that no input vertex claims, and the extractor has no way to build the
        /// missing edge.
        ///
        /// This split exists because the total was being read as evidence about the field. It is
        /// not; only the jump share is.
        /// </remarks>
        public float discardDiagonalFraction;
        public float discardJumpFraction;

        /// <summary>
        /// Histogram of |o_i - o_j| in lattice units, 10 buckets over [0, 1.5].
        /// A working field is BIMODAL: one peak near (inputEdge / scale) from same-cell pairs, a
        /// second near 1.0 from one-step pairs. A featureless unimodal spread means the lattice
        /// never organised.
        /// </summary>
        public int[] edgeGapHistogram = new int[10];

        // --- structure -----------------------------------------------------
        /// <summary>Triangles carrying a non-zero cross-field rotation index.</summary>
        public int singularityCount;
        public int valence3Count;   // index +1
        public int valence5Count;   // index -1
        public int higherOrderCount;

        /// <summary>
        /// Signed sum of all rotation indices. This is a topological invariant, not a quality
        /// measure: it must equal 4 * Euler characteristic (+8 for a closed genus-0 surface, 0 for a
        /// torus) no matter how good or bad the field is. If it does not, the singularity detector
        /// is wrong — which makes it the one number here that can validate the detector itself.
        /// </summary>
        public int indexSum;

        public int edgeCount;

        public string Summary()
        {
            return $"lattice: same={sameCellFraction:P1} step={oneStepFraction:P1} " +
                   $"discard={discardedFraction:P1} (diag={discardDiagonalFraction:P1} " +
                   $"jump={discardJumpFraction:P1}) mismatch={latticeMismatch:F4} " +
                   $"drift={representativeDrift:E2} | orientation energy={orientationEnergy:F6} " +
                   $"| singularities={singularityCount} (v3={valence3Count} v5={valence5Count})";
        }

        /// <summary>
        /// Whether the position field looks healthy enough that extraction defects are extraction's
        /// fault. Thresholds are deliberately loose — this is a triage signal, not a quality bar.
        /// </summary>
        public bool PositionFieldLooksHealthy()
        {
            // The primary signals are the discarded fraction and the mismatch: those say whether
            // neighbouring lattices agree, which is the solver's actual job.
            //
            // The same-cell / one-step split is NOT a quality signal, it is a function of how coarse
            // the requested lattice is. At a coarse target most input edges fall inside one cell, so
            // same-cell legitimately dominates and one-step drops — an 80/18 split with 0.8%
            // discarded is an excellent field, not a failing one. The one-step floor is therefore
            // set only high enough to confirm there are *some* edges to build faces out of.
            return discardedFraction < 0.25f
                   && sameCellFraction > 0.05f
                   && oneStepFraction > 0.05f
                   && latticeMismatch < 0.35f;
        }
    }

    public static class FieldDiagnostics
    {
        public static FieldReport Analyse(RemeshTopology topology, OrientationField orientation,
            PositionField position)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (orientation == null) throw new ArgumentNullException(nameof(orientation));

            var report = new FieldReport
            {
                orientationEnergy = SmoothnessEnergy(topology, orientation),
                orientationIterations = orientation.iterationsRun,
                orientationResidual = orientation.lastResidual
            };

            AnalyseSingularities(topology, orientation, report);

            if (position != null)
                AnalyseLattice(topology, orientation, position, report);

            return report;
        }

        /// <summary>
        /// Mean agreement of the cross field across edges, under the 4-fold symmetry. 1.0 means
        /// globally parallel.
        /// </summary>
        /// <remarks>
        /// Read this with care: a *random* 4-RoSy field already scores about 0.90, because the
        /// symmetry means the best of four rotations is never more than 45 degrees off. Judge by
        /// the fraction of achievable error removed (see <see cref="ErrorRemoved"/>), not by the
        /// raw value.
        /// </remarks>
        public static float SmoothnessEnergy(RemeshTopology topology, OrientationField field)
        {
            double sum = 0;
            var count = 0;

            for (var i = 0; i < topology.vertexCount; i++)
            {
                for (var k = topology.adjacencyStart[i]; k < topology.adjacencyStart[i + 1]; k++)
                {
                    var j = topology.adjacency[k];
                    if (j <= i) continue; // each undirected edge once

                    OrientationField.Compat4(field.directions[i], topology.normals[i],
                        field.directions[j], topology.normals[j], out var a, out var b);

                    sum += Vector3.Dot(a, b);
                    count++;
                }
            }

            return count > 0 ? (float)(sum / count) : 1f;
        }

        /// <summary>
        /// Fraction of the achievable smoothness error that was actually removed, given where the
        /// field started. The only fair way to score a 4-RoSy solve.
        /// </summary>
        public static float ErrorRemoved(float initialEnergy, float finalEnergy)
        {
            var achievable = 1f - initialEnergy;
            if (achievable <= 1e-6f) return 1f;
            return (finalEnergy - initialEnergy) / achievable;
        }

        static void AnalyseLattice(RemeshTopology topology, OrientationField orientation,
            PositionField position, FieldReport report)
        {
            var scale = position.scale;
            var invScale = 1f / scale;

            var p = topology.positions;
            var normals = topology.normals;
            var q = orientation.directions;
            var o = position.positions;

            double mismatchSum = 0;
            var mismatchMax = 0f;
            int same = 0, step = 0, discard = 0, total = 0;
            int discardDiagonal = 0, discardJump = 0;

            for (var i = 0; i < topology.vertexCount; i++)
            {
                for (var k = topology.adjacencyStart[i]; k < topology.adjacencyStart[i + 1]; k++)
                {
                    var j = topology.adjacency[k];
                    if (j <= i) continue;

                    // Frames must be aligned before the integer coordinates mean anything.
                    OrientationField.Compat4(q[i], normals[i], q[j], normals[j],
                        out var qi, out var qj);

                    PositionField.CompatIndex(
                        p[i], normals[i], qi, o[i],
                        p[j], normals[j], qj, o[j],
                        scale, invScale,
                        out var shift0, out var shift1, out var error);

                    switch (PositionField.Classify(shift0, shift1))
                    {
                        case PositionField.EdgeClass.Collapse: same++; break;
                        case PositionField.EdgeClass.Edge: step++; break;
                        default:
                            discard++;
                            // Re-derive the cause rather than asking Classify for it, so this
                            // measurement cannot be changed by editing Classify's return values.
                            if (Mathf.Abs(shift0.x - shift1.x) == 1 &&
                                Mathf.Abs(shift0.y - shift1.y) == 1) discardDiagonal++;
                            else discardJump++;
                            break;
                    }

                    var mismatch = Mathf.Sqrt(Mathf.Max(0f, error)) * invScale;
                    mismatchSum += mismatch;
                    mismatchMax = Mathf.Max(mismatchMax, mismatch);

                    var gap = Vector3.Distance(o[i], o[j]) * invScale;
                    var bucket = Mathf.Clamp(
                        Mathf.FloorToInt(gap / 1.5f * report.edgeGapHistogram.Length),
                        0, report.edgeGapHistogram.Length - 1);
                    report.edgeGapHistogram[bucket]++;

                    total++;
                }
            }

            report.edgeCount = total;
            if (total > 0)
            {
                report.latticeMismatch = (float)(mismatchSum / total);
                report.sameCellFraction = same / (float)total;
                report.oneStepFraction = step / (float)total;
                report.discardedFraction = discard / (float)total;
                report.discardDiagonalFraction = discardDiagonal / (float)total;
                report.discardJumpFraction = discardJump / (float)total;
            }

            report.latticeMismatchMax = mismatchMax;
            report.representativeDrift = position.representativeDrift;
            report.positionResidualRaw = position.lastResidual;
            report.positionIterations = position.iterationsRun;
        }

        /// <summary>
        /// Locates cross-field singularities by accumulating the rotation index around each
        /// triangle. A cross field on a closed surface must carry singularities — the hairy ball
        /// theorem forbids a smooth global combing — so the question is never whether they exist but
        /// whether there are few of them in sensible places. Index +1 quarter-turn corresponds to a
        /// valence-3 vertex in the eventual quad mesh, -1 to a valence-5.
        /// </summary>
        static void AnalyseSingularities(RemeshTopology topology, OrientationField orientation,
            FieldReport report)
        {
            var triangles = topology.triangles;
            var normals = topology.normals;
            var q = orientation.directions;
            var positions = topology.positions;

            const float k_Quarter = Mathf.PI * 0.5f;

            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                int ia = triangles[t], ib = triangles[t + 1], ic = triangles[t + 2];

                var a = positions[ia];
                var b = positions[ib];
                var c = positions[ic];

                var faceNormal = Vector3.Cross(b - a, c - a);
                if (faceNormal.sqrMagnitude <= 1e-20f) continue;
                faceNormal = MeshMath.SafeNormalize(faceNormal, Vector3.up);

                // A single frame on the triangle's plane, so all three crosses are compared in one
                // coordinate system rather than each in its own tangent plane.
                var u = OrientationField.ArbitraryTangent(faceNormal);
                var v = OrientationField.Rotate90(u, faceNormal);

                if (!TryProjectedAngle(q[ia], normals[ia], faceNormal, u, v, out var t0) ||
                    !TryProjectedAngle(q[ib], normals[ib], faceNormal, u, v, out var t1) ||
                    !TryProjectedAngle(q[ic], normals[ic], faceNormal, u, v, out var t2))
                    continue;

                // Each angle is only defined modulo a quarter turn. Wrap each edge's difference
                // into (-45, +45] degrees; the integer quarter-turns needed to close the loop is
                // the rotation index.
                var d0 = WrapQuarter(t1 - t0);
                var d1 = WrapQuarter(t2 - t1);
                var d2 = WrapQuarter(t0 - t2);

                // Sign convention pinned by the sphere: for a 4-RoSy field the indices must sum to
                // 4 * Euler characteristic, so a closed genus-0 surface totals +8, carried by eight
                // index +1 singularities — the valence-3 corners you would draw on a cube. The
                // torus cannot settle this, because genus 1 totals zero either way round.
                var index = Mathf.RoundToInt((d0 + d1 + d2) / k_Quarter);
                if (index == 0) continue;

                report.singularityCount++;
                report.indexSum += index;
                if (index == 1) report.valence3Count++;
                else if (index == -1) report.valence5Count++;
                else report.higherOrderCount++;
            }
        }

        static bool TryProjectedAngle(Vector3 direction, Vector3 vertexNormal, Vector3 faceNormal,
            Vector3 u, Vector3 v, out float angle)
        {
            angle = 0f;

            // Project the tangent direction into the face's plane. When the vertex normal is nearly
            // perpendicular to the face normal the projection is degenerate and the angle carries
            // no information, so skip rather than invent one.
            if (Mathf.Abs(Vector3.Dot(vertexNormal, faceNormal)) < 0.2f)
                return false;

            var projected = direction - faceNormal * Vector3.Dot(faceNormal, direction);
            if (projected.sqrMagnitude <= 1e-16f)
                return false;

            angle = Mathf.Atan2(Vector3.Dot(projected, v), Vector3.Dot(projected, u));
            return true;
        }

        /// <summary>Wraps an angle into (-pi/4, pi/4], the fundamental domain of a 4-RoSy field.</summary>
        static float WrapQuarter(float angle)
        {
            const float k_Quarter = Mathf.PI * 0.5f;
            angle -= Mathf.Floor(angle / k_Quarter + 0.5f) * k_Quarter;
            return angle;
        }

        /// <summary>
        /// Copies the field numbers onto a metrics row, so a CSV carries both output quality and
        /// the field state that produced it. Diagnosing a bad row without the field state next to it
        /// is guesswork.
        /// </summary>
        public static void CopyInto(FieldReport report, MeshMetrics metrics)
        {
            metrics.latticeMismatch = report.latticeMismatch;
            metrics.representativeDrift = report.representativeDrift;
            metrics.sameCellFraction = report.sameCellFraction;
            metrics.oneStepFraction = report.oneStepFraction;
            metrics.discardedFraction = report.discardedFraction;
            metrics.orientationEnergy = report.orientationEnergy;
            metrics.orientationIterations = report.orientationIterations;
            metrics.positionIterations = report.positionIterations;
            metrics.singularityCount = report.singularityCount;
            metrics.indexSum = report.indexSum;
        }
    }
}
