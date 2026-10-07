// Native quad remesher for Unity — Stage 2: the orientation field.
//
// From Jakob et al., "Instant Field-Aligned Meshes" (SIGGRAPH Asia 2015), §3.
//
// Every vertex carries a unit tangent direction. What makes it a *4-RoSy* field is that the
// direction is only meaningful up to 90-degree rotation about the vertex normal: q, q rotated 90,
// 180 and 270 all describe the same cross. That is exactly the symmetry of a quad, which is why
// this representation is what quad remeshing is built on.
//
// Smoothing the field means repeatedly nudging each vertex's cross toward its neighbours'. The
// only subtlety is that "toward" has to be measured under that 4-fold symmetry — comparing raw
// vectors would fight the symmetry instead of using it. That comparison is Compat4 below, and
// it is the heart of the whole stage.

using System;
using UnityEngine;

namespace NativeRemesher
{
    public sealed class OrientationField
    {
        /// <summary>Unit tangent direction per vertex, lying in that vertex's tangent plane.</summary>
        public Vector3[] directions;

        /// <summary>
        /// The same field carried onto a refined vertex set. See
        /// <see cref="PositionField.OnRefinedVertices"/> — the convergence counters are omitted for
        /// the same reason.
        /// </summary>
        public OrientationField OnRefinedVertices(Vector3[] refinedDirections)
        {
            if (refinedDirections == null) throw new ArgumentNullException(nameof(refinedDirections));
            return new OrientationField { directions = refinedDirections };
        }

        /// <summary>Mean angular change over the last smoothing sweep, in radians.</summary>
        public float lastResidual { get; private set; }

        public int iterationsRun { get; private set; }

        /// <summary>Rotate a tangent vector by 90 degrees about the surface normal.</summary>
        public static Vector3 Rotate90(Vector3 v, Vector3 n)
        {
            return Vector3.Cross(n, v);
        }

        /// <summary>
        /// Picks the pair of representatives — one from each vertex's set of four equivalent
        /// directions — that agree most closely. This is what lets neighbouring crosses be
        /// compared and averaged without the 4-fold ambiguity tearing the field apart.
        /// </summary>
        public static void Compat4(Vector3 q0, Vector3 n0, Vector3 q1, Vector3 n1,
            out Vector3 a, out Vector3 b)
        {
            // Only two candidates per side are needed: q and q rotated 90. The 180 and 270 cases
            // are their negations, and sign is handled at the end.
            var a0 = q0;
            var a1 = Rotate90(q0, n0);
            var b0 = q1;
            var b1 = Rotate90(q1, n1);

            var score00 = Mathf.Abs(Vector3.Dot(a0, b0));
            var score01 = Mathf.Abs(Vector3.Dot(a0, b1));
            var score10 = Mathf.Abs(Vector3.Dot(a1, b0));
            var score11 = Mathf.Abs(Vector3.Dot(a1, b1));

            Vector3 bestA, bestB;
            var best = score00;
            bestA = a0;
            bestB = b0;

            if (score01 > best)
            {
                best = score01;
                bestA = a0;
                bestB = b1;
            }
            if (score10 > best)
            {
                best = score10;
                bestA = a1;
                bestB = b0;
            }
            if (score11 > best)
            {
                bestA = a1;
                bestB = b1;
            }

            a = bestA;
            b = Vector3.Dot(bestA, bestB) < 0f ? -bestB : bestB;
        }

        /// <summary>Any unit vector orthogonal to <paramref name="n"/>, chosen deterministically.</summary>
        public static Vector3 ArbitraryTangent(Vector3 n)
        {
            // Cross with whichever axis is least aligned with n, so the result never degenerates.
            var axis = Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up;
            var t = Vector3.Cross(n, axis);
            var length = t.magnitude;
            return length > 1e-20f ? t / length : Vector3.right;
        }

        /// <summary>
        /// Solves the orientation field by Gauss-Seidel sweeps over the vertex graph.
        /// </summary>
        /// <remarks>
        /// Sweeping the finest level directly converges slowly on dense meshes, because
        /// information crosses only one edge per sweep. Stage 3 (the multiresolution hierarchy)
        /// is what fixes that; this is correct but not yet fast.
        /// </remarks>
        /// <param name="randomSeed">
        /// When non-negative, start from a randomised field instead of the deterministic tangent
        /// construction. A correct solver reaches the same quality from noise, so this is how the
        /// tests check that convergence is real rather than an artefact of a lucky initialisation.
        /// </param>
        /// <param name="constraintDirections">
        /// Per-vertex direction to align to, or <see cref="Vector3.zero"/> where the vertex is free.
        /// Null for an unconstrained solve.
        /// </param>
        /// <param name="constraintWeights">
        /// Per-vertex constraint strength. 0 is free, 1 or more is a hard lock.
        /// </param>
        /// <remarks>
        /// The constraint needs no new mathematics, which is the pleasant part. The accumulator
        /// already blends a starting value against the neighbours by weight, and Compat4 with the
        /// same normal on both sides is exactly the right 4-RoSy reduction for mixing a constraint
        /// with a smoothed direction. So a constraint is simply "seed the accumulator with the
        /// constrained direction and a non-zero self-weight" — the slot that otherwise carries the
        /// vertex's previous value at weight zero.
        /// </remarks>
        /// <param name="guideDirections">
        /// Per-vertex direction the field is PULLED toward on every sweep, or null for none.
        /// </param>
        /// <param name="guideWeights">
        /// Per-vertex pull, as a multiple of the vertex's degree: 0 is free, 1 weighs the guide as
        /// heavily as all the neighbours together. Ignored where a constraint applies.
        /// </param>
        /// <remarks>
        /// Guides are not soft constraints, and the difference is the point. A soft constraint
        /// seeds the vertex with its direction once and then carries the vertex's OWN current
        /// direction as the self-weighted slot, so it is inertia from a starting value rather than
        /// a pull: the field is free to drift from it and it never pulls back. A guide puts the
        /// guide direction itself in that slot on every sweep, so the solve is a genuine trade
        /// between smoothness and agreement with the guide -- which is what aligning to curvature
        /// needs. The soft-constraint behaviour is left exactly as it was.
        /// </remarks>
        /// <param name="initialDirections">
        /// A field to start from instead of an arbitrary or random one. Each entry is projected
        /// onto its vertex's tangent plane and renormalised, so a caller may pass directions read
        /// off a coarser level, a previous solve, or anything else that does not already satisfy
        /// those invariants — nothing downstream validates them, and PositionField feeds q[i]
        /// straight into Rotate90.
        /// </param>
        public static OrientationField Solve(RemeshTopology topology, int iterations = 50,
            Action<float> onProgress = null, int randomSeed = -1,
            Vector3[] constraintDirections = null, float[] constraintWeights = null,
            Vector3[] initialDirections = null,
            Vector3[] guideDirections = null, float[] guideWeights = null)
        {
            if (topology == null)
                throw new ArgumentNullException(nameof(topology));

            var count = topology.vertexCount;
            var field = new OrientationField { directions = new Vector3[count] };

            var hasConstraints = constraintDirections != null && constraintWeights != null
                                 && constraintDirections.Length == count
                                 && constraintWeights.Length == count;
            var hasGuides = guideDirections != null && guideWeights != null
                            && guideDirections.Length == count && guideWeights.Length == count;

            if (initialDirections != null && initialDirections.Length == count)
            {
                // Sanitised, not trusted. A prolongated direction is tangent to the COARSE
                // vertex's normal, not this one's, so it has to be re-projected; and a caller
                // outside the hierarchy may hand over something not even unit length.
                for (var i = 0; i < count; i++)
                {
                    var n = topology.normals[i];
                    var d = initialDirections[i];
                    d -= n * Vector3.Dot(n, d);
                    field.directions[i] = MeshMath.SafeNormalize(d, ArbitraryTangent(n));
                }
            }
            else if (randomSeed >= 0)
            {
                var rng = new System.Random(randomSeed);
                for (var i = 0; i < count; i++)
                {
                    var n = topology.normals[i];
                    var u = ArbitraryTangent(n);
                    var v = Rotate90(u, n);
                    var angle = (float)(rng.NextDouble() * 2.0 * Math.PI);
                    field.directions[i] = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                    field.directions[i] = ArbitraryTangent(topology.normals[i]);
            }

            // Seed constrained vertices from their constraint regardless of how the rest was
            // initialised. This is what lets the field propagate outward FROM the features instead
            // of spending its whole budget fighting them from a random start.
            if (hasConstraints)
            {
                for (var i = 0; i < count; i++)
                {
                    if (constraintWeights[i] <= 0f) continue;
                    var d = constraintDirections[i];
                    if (d.sqrMagnitude <= 1e-12f) continue;

                    var n = topology.normals[i];
                    d -= n * Vector3.Dot(n, d);
                    if (d.sqrMagnitude > 1e-12f)
                        field.directions[i] = MeshMath.SafeNormalize(d, field.directions[i]);
                }
            }

            var directions = field.directions;
            var normals = topology.normals;
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;

            // Coloured parallel sweeps — see GraphColouring for why this preserves Gauss-Seidel
            // exactly rather than degrading to Jacobi.
            var colouring = topology.colouring;
            var residualPerVertex = new float[count];
            var activePerVertex = new bool[count];

            const int k_ParallelThreshold = 2048;
            var parallel = count >= k_ParallelThreshold;

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                for (var phase = 0; phase < colouring.classCount; phase++)
                {
                    var phaseFrom = colouring.start[phase];
                    var phaseTo = colouring.start[phase + 1];

                    void Sweep(int slot)
                    {
                        var i = colouring.vertices[slot];

                        var from = start[i];
                        var to = start[i + 1];
                        if (from == to)
                        {
                            activePerVertex[i] = false;
                            return; // isolated vertex, nothing to align to
                        }

                        var n = normals[i];
                        var sum = directions[i];
                        var weight = 0f;

                        if (hasConstraints && constraintWeights[i] > 0f)
                        {
                            // A hard lock skips the sweep entirely: cheaper than blending, and
                            // exactly equivalent to an infinite weight.
                            if (constraintWeights[i] >= 1f)
                            {
                                // Excluded from the residual as well. Counting locked vertices as
                                // having "not moved" deflates the mean by the locked fraction and
                                // fires the early-out before the free part of the field has
                                // settled.
                                activePerVertex[i] = false;
                                return;
                            }

                            // Soft: enter the accumulator as a virtual neighbour whose weight is
                            // scaled by the vertex's degree, so the constraint's influence is
                            // independent of how many real neighbours happen to be pulling
                            // against it.
                            weight = constraintWeights[i] * (to - from);
                        }
                        else if (hasGuides && guideWeights[i] > 0f)
                        {
                            // The guide itself in the self slot, every sweep: see the remarks.
                            var g = guideDirections[i];
                            g -= n * Vector3.Dot(n, g);
                            var gl = g.magnitude;
                            if (gl > 1e-12f)
                            {
                                sum = g / gl;
                                weight = guideWeights[i] * (to - from);
                            }
                        }

                        for (var k = from; k < to; k++)
                        {
                            var j = adjacency[k];

                            Compat4(sum, n, directions[j], normals[j], out var a, out var b);

                            sum = a * weight + b;
                            weight += 1f;

                            // Keep the running average in the tangent plane, then renormalise.
                            // Doing this inside the loop rather than at the end keeps the
                            // accumulated vector from drifting off the surface.
                            sum -= n * Vector3.Dot(n, sum);

                            var length = sum.magnitude;
                            if (length > 1e-20f)
                                sum /= length;
                            else
                                sum = ArbitraryTangent(n);
                        }

                        // Movement has to be measured under the 4-fold symmetry too. A cross that
                        // merely swapped to an equivalent representative of itself has not moved,
                        // but a raw dot product would score that as a 90-degree jump and the
                        // residual would never settle even on a fully converged field.
                        Compat4(directions[i], n, sum, n, out var before, out var after);
                        residualPerVertex[i] = Mathf.Acos(Mathf.Clamp(Vector3.Dot(before, after), -1f, 1f));
                        activePerVertex[i] = true;
                        directions[i] = sum;
                    }

                    if (parallel && phaseTo - phaseFrom >= 256)
                        System.Threading.Tasks.Parallel.For(phaseFrom, phaseTo, Sweep);
                    else
                        for (var slot = phaseFrom; slot < phaseTo; slot++) Sweep(slot);
                }

                double residual = 0;
                var moved = 0;

                for (var i = 0; i < count; i++)
                {
                    if (!activePerVertex[i]) continue;
                    residual += residualPerVertex[i];
                    moved++;
                }

                field.lastResidual = moved > 0 ? (float)(residual / moved) : 0f;
                field.iterationsRun = iteration + 1;

                onProgress?.Invoke((iteration + 1) / (float)iterations);

                // The field has settled; more sweeps would only burn time.
                if (field.lastResidual < 1e-5f)
                    break;
            }

            return field;
        }

        /// <summary>
        /// The four equivalent directions of the cross at a vertex, for drawing or extraction.
        /// </summary>
        public void GetCross(int vertex, Vector3 normal, out Vector3 u, out Vector3 v)
        {
            u = directions[vertex];
            v = Rotate90(u, normal);
        }
    }
}
