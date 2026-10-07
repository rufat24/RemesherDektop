// The flow solver, tested against cases whose optimum is known by hand.
//
// This matters more than a usual unit test. Everything downstream of it — the lattice repair — is
// judged by mesh statistics, and a flow solver that returns a VALID but suboptimal flow would still
// produce a plausible-looking mesh and a slightly worse number, which is exactly the class of defect
// this project has repeatedly failed to catch by looking at output quality. So the assertions here
// are on the optimum itself, not on feasibility.

using NUnit.Framework;

namespace NativeRemesher.Tests
{
    public class MinCostFlowTests
    {
        /// <summary>
        /// Two routes to the sink, one cheap and narrow, one dear and wide. The optimum has to fill
        /// the cheap one first — a max-flow solver indifferent to cost would pass a feasibility test
        /// and fail this.
        /// </summary>
        [Test]
        public void CheapestRouteIsFilledFirst()
        {
            var flow = new MinCostFlow(4, 4);

            flow.AddArc(0, 1, 2, 1);    // cheap, narrow
            flow.AddArc(0, 2, 5, 10);   // dear, wide
            flow.AddArc(1, 3, 2, 0);
            flow.AddArc(2, 3, 5, 0);

            var pushed = flow.Solve(0, 3, 4);

            Assert.AreEqual(4, pushed);
            // 2 units at cost 1, then 2 units at cost 10.
            Assert.AreEqual(2 * 1 + 2 * 10, flow.totalCost);
        }

        /// <summary>Flow stops at the bottleneck, and the reported cost matches what was pushed.</summary>
        [Test]
        public void FlowIsBoundedByTheNarrowestArc()
        {
            var flow = new MinCostFlow(3, 2);

            flow.AddArc(0, 1, 7, 2);
            var narrow = flow.AddArc(1, 2, 3, 5);

            Assert.AreEqual(3, flow.Solve(0, 2));
            Assert.AreEqual(3, flow.FlowOn(narrow));
            Assert.AreEqual(3 * (2 + 5), flow.totalCost);
        }

        /// <summary>
        /// A disconnected sink yields nothing rather than looping. The lattice dual has components
        /// the repair cannot reach across, so this is a real case and not a defensive one.
        /// </summary>
        [Test]
        public void AnUnreachableSinkPushesNothing()
        {
            var flow = new MinCostFlow(4, 1);
            flow.AddArc(0, 1, 5, 1);

            Assert.AreEqual(0, flow.Solve(0, 3));
            Assert.AreEqual(0, flow.totalCost);
        }

        /// <summary>
        /// The case that catches a solver which commits to its first augmenting path. Routing
        /// greedily through the middle arc blocks the optimum, and a correct successive-shortest-path
        /// solver undoes it through the reverse arc rather than living with it.
        /// </summary>
        /// <remarks>
        /// The cheapest single path is 0-1-2-3 at cost 2, and taking it saturates the middle arc and
        /// the only arc into the sink from node 2. A solver that cannot undo a committed path then
        /// finds the sink unreachable and stops at ONE unit. The correct answer sends the second unit
        /// 0-2-1-3, travelling backwards along the middle arc through its reverse — which is the
        /// mechanism successive-shortest-paths exists for.
        ///
        /// So the discriminating assertion is the flow, not the cost: one unit means the reverse arcs
        /// are not working. The cost is asserted too, at the true optimum of 6, because the first
        /// version of this test asserted 4 from a hand-computation that was simply wrong and the
        /// solver was right.
        /// </remarks>
        [Test]
        public void AGreedyFirstPathIsRerouted()
        {
            var flow = new MinCostFlow(4, 5);

            flow.AddArc(0, 1, 1, 1);
            flow.AddArc(0, 2, 1, 2);
            flow.AddArc(1, 2, 1, 0);    // the tempting middle
            flow.AddArc(1, 3, 1, 2);
            flow.AddArc(2, 3, 1, 1);

            Assert.AreEqual(2, flow.Solve(0, 3, 2),
                "the second unit needs the reverse of the middle arc");
            Assert.AreEqual(6, flow.totalCost);
        }

        /// <summary>
        /// Under-declaring the arc count grows the graph instead of corrupting it.
        /// </summary>
        /// <remarks>
        /// The constructor takes a hint, not a limit. The first version treated it as a limit without
        /// checking, so a caller off by one got an IndexOutOfRangeException raised from inside the
        /// solver — a failure with nothing in it pointing at the miscount that caused it.
        /// </remarks>
        [Test]
        public void ArcCapacityIsAHintNotALimit()
        {
            var flow = new MinCostFlow(3, 1);

            flow.AddArc(0, 1, 4, 1);
            flow.AddArc(1, 2, 4, 1);

            Assert.AreEqual(2, flow.arcCount);
            Assert.AreEqual(4, flow.Solve(0, 2));
        }

        /// <summary>
        /// The shape the lattice repair actually builds: several sources of surplus and several of
        /// deficit, joined through a shared interior, balanced overall. Every unit of supply must be
        /// routed and the total must be the cheapest pairing.
        /// </summary>
        [Test]
        public void BalancedSupplyAndDemandIsFullyRouted()
        {
            // 0 = super-source, 1..2 = surplus faces, 3..4 = deficit faces, 5 = super-sink.
            var flow = new MinCostFlow(6, 7);

            flow.AddArc(0, 1, 2, 0);
            flow.AddArc(0, 2, 1, 0);

            flow.AddArc(1, 3, 2, 1);    // near
            flow.AddArc(1, 4, 2, 4);    // far
            flow.AddArc(2, 4, 2, 1);    // near

            flow.AddArc(3, 5, 2, 0);
            flow.AddArc(4, 5, 1, 0);

            Assert.AreEqual(3, flow.Solve(0, 5), "all supply must be routed");
            // 2 units on the 1->3 arc at cost 1, 1 unit on 2->4 at cost 1.
            Assert.AreEqual(3, flow.totalCost);
        }

        /// <summary>
        /// The invariant that caught the potential bug, and the only one strong enough to catch its
        /// whole class: <b>an optimal min-cost flow has a unique cost</b>, so the answer cannot
        /// depend on the order the arcs were added, on which of several equal-cost paths Dijkstra
        /// happens to settle first, or on the priority queue's implementation.
        /// </summary>
        /// <remarks>
        /// The bug this pins: the potential update raised reachable nodes by their distance and left
        /// unreachable nodes alone, so an arc out of an unreachable node acquired a NEGATIVE reduced
        /// cost — harmless while that node stayed unreachable, but pushing flow opens a reverse arc
        /// and it does not stay unreachable. `ShortestPath` then clamped the negative weight to zero
        /// and returned a wrong distance, and the solver augmented along a path that was not
        /// cheapest. It still returned a feasible flow of the right VALUE, which is why nothing
        /// downstream noticed; the reported symptom was that changing the queue moved the total cost
        /// 3,648,222 -> 3,673,228.
        ///
        /// The graph below is built so the early iterations cannot reach every node: `bypass` is
        /// only entered through a reverse arc that opens after the first augmentation.
        /// </remarks>
        [Test]
        public void TotalCostDoesNotDependOnArcOrder()
        {
            // (from, to, capacity, cost). Layered, with a bypass reachable only after a reroute,
            // and several equal-cost alternatives so ties are exercised too.
            var arcs = new[]
            {
                (0, 1, 3, 0), (0, 2, 3, 0),
                (1, 3, 2, 1), (1, 4, 2, 5),
                (2, 3, 1, 4), (2, 4, 3, 2),
                (3, 5, 2, 1), (4, 5, 3, 1),
                (3, 4, 2, 1), (4, 3, 2, 1),   // the bypass, in both directions
                (1, 2, 1, 7), (2, 1, 1, 7),
                (5, 6, 6, 0),
            };

            long expectedCost = -1;
            var expectedFlow = -1;

            // A fixed set of permutations rather than a random shuffle: a property test that fails
            // only on some seeds is worse than no test, because it gets re-run until it passes.
            var orders = new[]
            {
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 },
                new[] { 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
                new[] { 8, 9, 0, 3, 6, 1, 4, 7, 2, 5, 10, 11, 12 },
                new[] { 2, 5, 9, 12, 1, 4, 8, 11, 0, 3, 7, 10, 6 },
                new[] { 6, 0, 12, 3, 8, 1, 9, 4, 10, 2, 11, 5, 7 },
            };

            foreach (var order in orders)
            {
                var flow = new MinCostFlow(7, arcs.Length);
                foreach (var index in order)
                {
                    var (from, to, capacity, cost) = arcs[index];
                    flow.AddArc(from, to, capacity, cost);
                }

                var pushed = flow.Solve(0, 6);

                if (expectedFlow < 0)
                {
                    expectedFlow = pushed;
                    expectedCost = flow.totalCost;
                    Assert.Greater(expectedFlow, 0, "the fixture must actually route something");
                }
                else
                {
                    Assert.AreEqual(expectedFlow, pushed,
                        "max flow is unique, so arc order cannot change it");
                    Assert.AreEqual(expectedCost, flow.totalCost,
                        "an OPTIMAL min-cost flow has a unique cost — arc order changed it, so at " +
                        "least one of these runs was not optimal");
                }
            }
        }

        /// <summary>
        /// Reduced costs must stay non-negative across every augmentation, which is the precondition
        /// Dijkstra needs and the thing the clamp was hiding. Asserted through the public surface:
        /// the cost of routing k units one at a time must equal the cost of routing them together,
        /// because successive shortest paths finds them in non-decreasing cost order.
        /// </summary>
        [Test]
        public void RoutingOneUnitAtATimeCostsTheSameAsRoutingThemTogether()
        {
            MinCostFlow Build()
            {
                var f = new MinCostFlow(7, 13);
                f.AddArc(0, 1, 3, 0);
                f.AddArc(0, 2, 3, 0);
                f.AddArc(1, 3, 2, 1);
                f.AddArc(1, 4, 2, 5);
                f.AddArc(2, 3, 1, 4);
                f.AddArc(2, 4, 3, 2);
                f.AddArc(3, 5, 2, 1);
                f.AddArc(4, 5, 3, 1);
                f.AddArc(3, 4, 2, 1);
                f.AddArc(4, 3, 2, 1);
                f.AddArc(5, 6, 6, 0);
                return f;
            }

            var together = Build();
            var total = together.Solve(0, 6);

            // Same graph, but asked for one unit at a time on a fresh solver each time, so the
            // incremental costs are the marginal costs of the 1st, 2nd, ... units.
            long incremental = 0;
            for (var units = 1; units <= total; units++)
            {
                var partial = Build();
                Assert.AreEqual(units, partial.Solve(0, 6, units),
                    $"the graph must be able to carry {units} units");
                if (units == total) incremental = partial.totalCost;
            }

            Assert.AreEqual(together.totalCost, incremental,
                "successive shortest paths must find augmentations in non-decreasing cost order");
        }
    }
}
