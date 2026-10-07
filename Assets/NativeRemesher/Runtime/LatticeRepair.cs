// The lattice repair: turning the integrability defect into a min-cost flow and solving it.
//
// LatticeIntegration measures the defect; this plans the fix. The two are separate files because the
// measurement has to stay trustworthy independently of anything that acts on it — that is the
// standing lesson of this project, and the reason the measurement came first by two sessions.
//
// WHAT IS BEING REPAIRED. Each triangle's three edge offsets must sum to zero, or no assignment of
// lattice cells satisfies all three and the cells cannot tile. LatticeIntegration.FaceResidual is
// that sum. On the reference asset 99.0% of faces already have residual zero and the total is 503
// units — sparse and localised, which is what makes this tractable.
//
// WHY IT IS A FLOW. Changing one edge's offset by a unit changes the residual of the face on one
// side by +1 and the face on the other side by -1, because the two faces read the same edge with
// opposite orientation. Residual is therefore CONSERVED and can only be moved between faces that
// share an edge. Moving it is what a repair is: a face with surplus hands it across the dual graph
// to a face with deficit, and when every face reaches zero the lattice is integrable. That is a
// transhipment problem on the triangle dual, so the cheapest repair is a minimum-cost flow.
//
// The cost of a unit is LatticeEdges.error — the residual distance CompatIndex reported between the
// two lattice points. A large error means the position field was unsure where that edge's cells fell,
// so pushing it is cheap; a small error means the field was confident and pushing it is expensive.
// Minimising total cost therefore means "change the offsets the field was least sure about", which
// is exactly the right notion of a minimal repair and is QuadriFlow's own choice.
//
// SCALAR, TWICE. Gauge fixing removed the rotation from the transports, so the x and y components no
// longer mix and this is two independent scalar problems rather than one over a rotating quantity.
// LatticeIntegration's header states that; it is what makes the flow ordinary.
//
// WHICH FACES PARTICIPATE. Only the constrained ones — not singular, not twisted, not on a boundary
// — matching exactly the population LatticeIntegration reports as `flat`. A singular face's residual
// is not a defect but the field's genuine holonomy and must not be routed away; a twisted face's
// residual is not a plain sum so the arithmetic does not apply; a boundary face has no closed loop to
// sum around. The three are excluded here for the same reasons they are excluded there.
//
// SCOPE. This plans and prices the repair. It does not yet WRITE the result back into
// LatticeEdges.jump, because doing that correctly means transporting each unit of flow into the
// frame the offset is stated in, and getting a sign wrong there would produce a plausible mesh with
// a slightly worse number — the exact defect class this project has repeatedly failed to catch by
// eye. The application is the next piece, and it belongs behind its own measurement.

using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public sealed class RepairPlanStats
    {
        /// <summary>Faces the flow may move residual between.</summary>
        public int participatingFaces;

        /// <summary>Dual arcs built — interior edges with a participating face on both sides.</summary>
        public int arcs;

        /// <summary>Faces excluded, by reason. These sum with participating to the face count.</summary>
        public int excludedSingular;
        public int excludedTwisted;
        public int excludedBoundary;

        /// <summary>Total positive supply, per component. Equals total demand when the mesh balances.</summary>
        public int supplyX, supplyY;
        public int demandX, demandY;

        /// <summary>Units actually routable, per component.</summary>
        public int routedX, routedY;

        /// <summary>Cost of the routed flow, in units of the error weighting.</summary>
        public long costX, costY;

        /// <summary>
        /// Supply that could not reach any deficit. Non-zero means the defect is not repairable by
        /// moving residual alone — the imbalance has to go somewhere, and on a closed surface that
        /// somewhere is a singularity.
        /// </summary>
        public int strandedX => supplyX - routedX;
        public int strandedY => supplyY - routedY;

        public long totalMs;

        public override string ToString()
        {
            return $"faces={participatingFaces} arcs={arcs} " +
                   $"(excl sing={excludedSingular} twist={excludedTwisted} bnd={excludedBoundary}) " +
                   $"x: supply={supplyX} demand={demandX} routed={routedX} stranded={strandedX} " +
                   $"cost={costX} | " +
                   $"y: supply={supplyY} demand={demandY} routed={routedY} stranded={strandedY} " +
                   $"cost={costY} | {totalMs}ms";
        }
    }

    public static class LatticeRepair
    {
        /// <summary>
        /// How many units one edge's offset may be pushed. The residual histogram never exceeds 2 on
        /// any measured mesh, so this is slack rather than a tuning knob; it exists so a pathological
        /// input cannot ask for an unbounded change to a single edge.
        /// </summary>
        public const int maxOffsetChange = 2;

        /// <summary>
        /// Scales the per-edge error into an integer arc cost. The solver is integral, and the errors
        /// are small floats, so they need a common denominator; 1000 keeps three significant figures,
        /// which is far finer than the differences that decide a route.
        /// </summary>
        const int k_CostScale = 1000;

        /// <summary>
        /// Builds and solves the repair flow for both components. Does not modify
        /// <paramref name="edges"/>.
        /// </summary>
        public static RepairPlanStats Plan(RemeshTopology topology, LatticeEdges edges)
        {
            if (topology == null) throw new System.ArgumentNullException(nameof(topology));
            if (edges == null) throw new System.ArgumentNullException(nameof(edges));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stats = new RepairPlanStats();

            var faceCount = topology.triangleCount;

            // --- classify faces, exactly as LatticeIntegration does ---
            //
            // The residual per face is computed once and kept, because the flow needs it twice (once
            // per component) and FaceResidual is not free.
            var residual = new Vector2Int[faceCount];
            var participates = new bool[faceCount];
            var node = new int[faceCount];

            for (var f = 0; f < faceCount; f++)
            {
                node[f] = -1;

                var r = LatticeIntegration.FaceResidual(topology, edges, f,
                    out var rotationSum, out var hasBoundary);

                if (hasBoundary) { stats.excludedBoundary++; continue; }
                if (rotationSum != 0) { stats.excludedSingular++; continue; }

                // A face whose three rotations are individually non-zero but compose to the identity
                // is "twisted": the residual is not a plain sum in Z^2 there, so it cannot be routed.
                if (edges.rotation[f * 3] != 0 || edges.rotation[f * 3 + 1] != 0 ||
                    edges.rotation[f * 3 + 2] != 0)
                {
                    stats.excludedTwisted++;
                    continue;
                }

                residual[f] = r;
                participates[f] = true;
                node[f] = stats.participatingFaces++;
            }

            if (stats.participatingFaces == 0)
            {
                stats.totalMs = watch.ElapsedMilliseconds;
                return stats;
            }

            // --- the dual arcs ---
            //
            // One per interior half-edge whose twin's face also participates, counted once by taking
            // only the half-edge with the lower index — the same trick PreDecimate uses to enumerate
            // undirected edges without hashing.
            var arcFrom = new List<int>(stats.participatingFaces * 2);
            var arcTo = new List<int>(stats.participatingFaces * 2);
            var arcCost = new List<int>(stats.participatingFaces * 2);

            var halfEdges = topology.halfEdgeVertex.Length;
            for (var h = 0; h < halfEdges; h++)
            {
                var twin = topology.halfEdgeTwin[h];
                if (twin < 0 || twin < h) continue;

                var a = h / 3;
                var b = twin / 3;
                if (!participates[a] || !participates[b]) continue;

                // Cheap where the position field was unsure, dear where it was confident. Both
                // half-edges describe the same edge, so either error would do; the larger is taken so
                // the pair agrees regardless of which side reported more doubt.
                var error = Mathf.Max(edges.error[h], edges.error[twin]);
                var cost = Mathf.Max(1, Mathf.RoundToInt(k_CostScale / (1f + error * k_CostScale)));

                arcFrom.Add(node[a]);
                arcTo.Add(node[b]);
                arcCost.Add(cost);
                stats.arcs++;
            }

            SolveComponent(stats, residual, participates, node, arcFrom, arcTo, arcCost,
                component: 0);
            SolveComponent(stats, residual, participates, node, arcFrom, arcTo, arcCost,
                component: 1);

            stats.totalMs = watch.ElapsedMilliseconds;
            return stats;
        }

        /// <summary>
        /// One scalar transhipment: super-source to every surplus face, every deficit face to
        /// super-sink, and the dual arcs in both directions between them.
        /// </summary>
        static void SolveComponent(RepairPlanStats stats, Vector2Int[] residual, bool[] participates,
            int[] node, List<int> arcFrom, List<int> arcTo, List<int> arcCost, int component)
        {
            var count = stats.participatingFaces;
            var source = count;
            var sink = count + 1;

            // Two directed arcs per dual edge, plus one terminal arc per face at worst.
            var flow = new MinCostFlow(count + 2, arcFrom.Count * 2 + count);

            var supply = 0;
            var demand = 0;

            for (var f = 0; f < residual.Length; f++)
            {
                if (!participates[f]) continue;

                var value = component == 0 ? residual[f].x : residual[f].y;
                if (value > 0)
                {
                    flow.AddArc(source, node[f], value, 0);
                    supply += value;
                }
                else if (value < 0)
                {
                    flow.AddArc(node[f], sink, -value, 0);
                    demand += -value;
                }
            }

            // Residual can move either way across an edge, so each dual edge is two directed arcs.
            for (var i = 0; i < arcFrom.Count; i++)
            {
                flow.AddArc(arcFrom[i], arcTo[i], maxOffsetChange, arcCost[i]);
                flow.AddArc(arcTo[i], arcFrom[i], maxOffsetChange, arcCost[i]);
            }

            var routed = flow.Solve(source, sink);

            if (component == 0)
            {
                stats.supplyX = supply;
                stats.demandX = demand;
                stats.routedX = routed;
                stats.costX = flow.totalCost;
            }
            else
            {
                stats.supplyY = supply;
                stats.demandY = demand;
                stats.routedY = routed;
                stats.costY = flow.totalCost;
            }
        }
    }
}
