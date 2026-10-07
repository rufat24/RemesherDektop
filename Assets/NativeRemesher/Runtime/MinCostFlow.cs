// Minimum-cost flow, self-contained.
//
// This is the machinery QuadriFlow (Huang et al. 2018, BSD-3) uses to turn a locally-converged
// lattice into a globally consistent one, and it is the piece this project has been missing. The
// measurement that says so is in Benchmarks/README.md and is worth restating, because it is what
// justifies writing a flow solver inside a remesher at all:
//
//   * the cross field is fine        — 429 singular faces of 54,740, 0.78%
//   * the tracer is fine             — 43.4% irregular in, 42.6% out
//   * more sweeping does nothing     — 60/50 and 300/300 sweeps land 1.3 points apart
//   * the lattice does not TILE      — 2,128 vacant lattice directions against 1,374 contested
//
// "Does not tile" has an exact statement, and LatticeIntegration already measures it: walk the three
// edges of a triangle and the per-edge integer offsets must sum to zero, or no assignment of cells
// satisfies all three. On the reference asset 99.0% of triangles already satisfy it and the total
// residual is 503 units over ~500 faces. That is a SMALL flow problem, which is the reason this is
// worth doing rather than daunting: the defect is sparse and localised, and routing it away is a
// min-cost flow whose supply is measured in hundreds.
//
// WHY A FLOW PROBLEM AT ALL. Each triangle carries a residual — how far its three offsets are from
// summing to zero. Fixing one triangle means changing an offset on one of its edges, which changes
// the neighbouring triangle across that edge by the same amount with the opposite sign. So a repair
// is a conserved quantity moving between adjacent faces: surplus flowing to deficit across the dual
// graph. That is exactly a transhipment problem, the residuals are the supplies, and the cost of
// each unit is how far the offset had to be pushed from what the position field asked for. Solving
// it optimally is what makes the result the SMALLEST change that makes the lattice integrable,
// rather than one of many arbitrary repairs.
//
// Gauge fixing is what makes the residual a plain sum in Z^2 instead of a quantity that rotates as
// you carry it around the mesh, and LatticeIntegration.BuildEdges already does that and verifies it
// (gaugeFail=0). Because the two components no longer mix, the repair is TWO INDEPENDENT SCALAR
// problems rather than one vector problem — which is why this solver is scalar and gets run twice.
//
// ALGORITHM. Successive shortest paths with Johnson potentials: repeatedly push flow along the
// cheapest residual path from a super-source to a super-sink, reduced costs keeping every arc
// non-negative so Dijkstra is valid after the first pass. Costs here are small non-negative
// integers and the graph is sparse and planar-ish (a triangle dual), so this is comfortably fast at
// the sizes involved; QuadriFlow reaches for Boykov-Kolmogorov only above a supply of 20 and uses a
// plain augmenting-path solver below that.
//
// Bellman-Ford seeds the potentials rather than assuming zero, so the solver stays correct if a
// caller ever supplies a negative arc cost. Nothing here allocates per augmentation.

using System;
using System.Collections.Generic;

namespace NativeRemesher
{
    /// <summary>
    /// Integer minimum-cost maximum-flow on a directed graph. Nodes are indices; arcs are added with
    /// a capacity and a per-unit cost, and each gets a reverse arc so the residual graph is implicit.
    /// </summary>
    public sealed class MinCostFlow
    {
        // Not readonly: the arc arrays GROW. The constructor's arcCapacity is a hint for how much to
        // reserve, not a limit, because a caller that under-counts by one should get a slightly
        // slower solve rather than silently running off the end of the arrays — which is exactly
        // what the first version did, and it surfaced as an IndexOutOfRangeException from inside the
        // solver rather than as anything the caller could act on.
        int[] m_Head;
        int[] m_Next;
        int[] m_Capacity;
        int[] m_Cost;

        readonly int[] m_First;

        int m_ArcCount;

        readonly int m_NodeCount;

        // Scratch, allocated once. A solve on a 55k-face dual runs thousands of augmentations and
        // must not allocate in the loop.
        readonly long[] m_Distance;
        readonly long[] m_Potential;
        readonly int[] m_ArcInto;
        readonly bool[] m_Settled;

        // Lazy binary heap: entries are never decreased in place, a node already settled is skipped
        // when popped, so a node can appear more than once and no decrease-key is needed. Grows if a
        // pathological graph pushes more entries than nodes.
        long[] m_HeapDistance;
        int[] m_HeapNode;
        int m_HeapCount;

        // Lowest arc cost added so far. Bellman-Ford seeding is only needed below zero; above it a
        // zero potential is already valid, and on the integer grid map's 626k-node dual the seeding
        // pass alone was a few seconds of every solve.
        int m_MinCost;

        // Phased augmentation scratch: per-node "next arc to try" and a dead mark for the current
        // phase, plus the DFS stack.
        int[] m_CurrentArc;
        int[] m_Dead;
        int[] m_StackNode;
        int[] m_ArcPhase;
        int[] m_Touched;
        int m_TouchedCount;
        int m_Phase;

        /// <summary>
        /// Push flow along EVERY shortest path one Dijkstra finds before running the next one
        /// (primal-dual, a blocking flow on the zero-reduced-cost arcs), instead of one path per
        /// Dijkstra. The cost is identical -- both are optimal -- but the integer grid map routes
        /// about a thousand units on a dual of more than half a million nodes, one Dijkstra each:
        /// measured 51.7 s of a 158 s body run went into the flow, twice per run (probe and kept
        /// solve). Off is the original one-path-per-Dijkstra solver, kept for comparison.
        /// </summary>
        public static bool phasedAugmentation = true;

        /// <summary>Dijkstra runs the last <see cref="Solve"/> made.</summary>
        public int phases { get; private set; }

        /// <summary>Nodes Dijkstra settled over the last <see cref="Solve"/>, summed over phases.</summary>
        public long settled { get; private set; }

        public int nodeCount => m_NodeCount;
        public int arcCount => m_ArcCount / 2;

        /// <summary>Total cost of the flow the last <see cref="Solve"/> pushed.</summary>
        public long totalCost { get; private set; }

        /// <summary>Units of flow the last <see cref="Solve"/> pushed.</summary>
        public int totalFlow { get; private set; }

        public MinCostFlow(int nodes, int arcCapacity)
        {
            if (nodes < 0) throw new ArgumentOutOfRangeException(nameof(nodes));
            if (arcCapacity < 0) throw new ArgumentOutOfRangeException(nameof(arcCapacity));

            m_NodeCount = nodes;

            // Two entries per arc: the arc and its reverse. Pairing them at 2k and 2k+1 makes the
            // reverse of arc a simply (a ^ 1), which is what keeps the residual graph implicit.
            var slots = Math.Max(2, arcCapacity * 2);
            m_Head = new int[slots];
            m_Next = new int[slots];
            m_Capacity = new int[slots];
            m_Cost = new int[slots];

            m_First = new int[nodes];
            for (var i = 0; i < nodes; i++) m_First[i] = -1;

            m_Distance = new long[nodes];
            m_Potential = new long[nodes];
            m_ArcInto = new int[nodes];
            m_Settled = new bool[nodes];

            // A binary heap in two parallel arrays, sized for the worst case and REUSED across
            // augmentations. The first version used a SortedSet<(long, int)> allocated per call: a
            // red-black tree with a boxed comparison per node, rebuilt a few hundred times per solve.
            // On the reference asset's 49,894-node dual that was most of the six seconds the plan
            // took, and none of it was the algorithm.
            m_HeapDistance = new long[nodes + 1];
            m_HeapNode = new int[nodes + 1];
        }

        /// <summary>
        /// Adds a directed arc and its zero-capacity reverse. Returns the arc's index, which
        /// <see cref="FlowOn"/> reads back.
        /// </summary>
        public int AddArc(int from, int to, int capacity, int cost)
        {
            if (from < 0 || from >= m_NodeCount) throw new ArgumentOutOfRangeException(nameof(from));
            if (to < 0 || to >= m_NodeCount) throw new ArgumentOutOfRangeException(nameof(to));
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));

            var arc = m_ArcCount;

            if (arc + 2 > m_Head.Length)
            {
                var grown = Math.Max(4, m_Head.Length * 2);
                Array.Resize(ref m_Head, grown);
                Array.Resize(ref m_Next, grown);
                Array.Resize(ref m_Capacity, grown);
                Array.Resize(ref m_Cost, grown);
            }

            if (cost < m_MinCost) m_MinCost = cost;
            m_Head[arc] = to;
            m_Capacity[arc] = capacity;
            m_Cost[arc] = cost;
            m_Next[arc] = m_First[from];
            m_First[from] = arc;

            m_Head[arc + 1] = from;
            m_Capacity[arc + 1] = 0;
            m_Cost[arc + 1] = -cost;
            m_Next[arc + 1] = m_First[to];
            m_First[to] = arc + 1;

            m_ArcCount += 2;
            return arc;
        }

        /// <summary>
        /// Flow currently carried by the arc <see cref="AddArc"/> returned — the capacity that has
        /// been consumed, which is what the reverse arc accumulated.
        /// </summary>
        public int FlowOn(int arc) => m_Capacity[arc ^ 1];

        /// <summary>
        /// Pushes at most <paramref name="limit"/> units from source to sink as cheaply as possible.
        /// Returns the units pushed; <see cref="totalCost"/> carries the price.
        /// </summary>
        public int Solve(int source, int sink, int limit = int.MaxValue)
        {
            totalCost = 0;
            totalFlow = 0;

            phases = 0;
            settled = 0;
            if (source == sink || m_NodeCount == 0) return 0;

            if (m_MinCost < 0) SeedPotentials(source);
            else Array.Clear(m_Potential, 0, m_NodeCount);

            if (phasedAugmentation) return SolvePhased(source, sink, limit);

            while (totalFlow < limit)
            {
                if (!ShortestPath(source, sink)) break;
                phases++;

                // Potentials shift by the distances just found, which is what keeps every reduced
                // cost non-negative for the next Dijkstra even though the residual graph now has
                // backward arcs with negative real cost.
                //
                // The clamp at d[sink] is not a safety net, it is the correct update, and leaving it
                // out is a real bug that this solver had. A node unreachable in THIS iteration keeps
                // its old potential; a node reachable in this iteration grows by its distance. So
                // for an arc (u, v) with u unreachable and v reachable, the reduced cost
                // cost + pot[u] - pot[v] falls by d[v] and can go negative. Such an arc has no
                // residual capacity right now — which is why u is unreachable — but pushing flow
                // opens its reverse arc, u becomes reachable in a later iteration, and Dijkstra is
                // then run on a graph with a negative reduced cost. Raising every unreachable node
                // by d[sink] instead keeps the arc at
                // old_reduced + (d[sink] - d[v]) >= old_reduced >= 0, because Dijkstra settled the
                // sink last among the nodes that matter.
                //
                // The previous version instead clamped the reduced cost itself to zero inside
                // ShortestPath, which silently accepted the wrong distance rather than preventing
                // it. The evidence it mattered: swapping the priority queue for a heap moved the
                // total cost 3,648,222 -> 3,673,228, and an optimal min-cost flow has a UNIQUE
                // cost, so at least one of those runs was not optimal.
                var sinkDistance = m_Distance[sink];
                for (var v = 0; v < m_NodeCount; v++)
                {
                    m_Potential[v] += m_Distance[v] < sinkDistance ? m_Distance[v] : sinkDistance;
                }

                // Bottleneck along the path found, walking it backwards through m_ArcInto.
                var push = limit - totalFlow;
                for (var v = sink; v != source; )
                {
                    var arc = m_ArcInto[v];
                    push = Math.Min(push, m_Capacity[arc]);
                    v = m_Head[arc ^ 1];
                }

                if (push <= 0) break;

                for (var v = sink; v != source; )
                {
                    var arc = m_ArcInto[v];
                    m_Capacity[arc] -= push;
                    m_Capacity[arc ^ 1] += push;
                    totalCost += (long)push * m_Cost[arc];
                    v = m_Head[arc ^ 1];
                }

                totalFlow += push;
            }

            return totalFlow;
        }

        int SolvePhased(int source, int sink, int limit)
        {
            if (m_CurrentArc == null)
            {
                m_CurrentArc = new int[m_NodeCount];
                m_Dead = new int[m_NodeCount];
                m_StackNode = new int[m_NodeCount + 1];
                m_ArcPhase = new int[m_NodeCount];
                m_Touched = new int[m_NodeCount];
                for (var i = 0; i < m_NodeCount; i++) m_Distance[i] = long.MaxValue;
            }

            while (totalFlow < limit)
            {
                if (!ShortestPathLocal(source, sink)) break;
                phases++;

                // The same potential update as the one-path solver (see Solve) less a constant:
                // there every node not settled rises by d[sink]; here every node falls by d[sink]
                // relative to that, so only the settled ones move and a phase costs what it
                // explored rather than the whole graph. Reduced costs only see differences.
                var sinkDistance = m_Distance[sink];
                for (var i = 0; i < m_TouchedCount; i++)
                {
                    var v = m_Touched[i];
                    if (m_Settled[v] && m_Distance[v] < sinkDistance)
                        m_Potential[v] -= sinkDistance - m_Distance[v];
                }

                // Every arc on a shortest path now has reduced cost zero, so any source-sink path
                // through zero-reduced-cost residual arcs is a shortest path, and pushing along it
                // keeps every reduced cost non-negative (its reverse arc is zero too). Push along
                // them until none is left, then let Dijkstra raise the potentials again.
                m_Phase++;

                var pushedThisPhase = 0;
                while (totalFlow < limit)
                {
                    var pushed = AugmentAdmissible(source, sink, limit - totalFlow);
                    if (pushed <= 0) break;
                    totalFlow += pushed;
                    pushedThisPhase += pushed;
                }

                // Cannot happen with valid potentials -- Dijkstra's own path is admissible -- but
                // a phase that moves nothing would otherwise loop forever.
                if (pushedThisPhase == 0) break;
            }

            return totalFlow;
        }

        /// <summary>
        /// Min-cost transshipment: routes every node's excess (positive) to nodes with a deficit
        /// (negative) over the arcs added so far, one excess node at a time, along the shortest
        /// path to the NEAREST deficit. Returns the units routed; <paramref name="excess"/> is
        /// left holding what could not be.
        /// </summary>
        /// <remarks>
        /// The successive-shortest-path algorithm in its transshipment form (Ahuja, Magnanti and
        /// Orlin, ch. 9): optimal like <see cref="Solve"/>, but without a super-source each Dijkstra
        /// grows from ONE node and stops at the first deficit it settles, so it explores a
        /// neighbourhood rather than the surface. Through a super-source every search grows from
        /// all remaining sources at once out to the global shortest distance -- on the head's
        /// 950k-node dual that was 52 s for 1,789 units. Potentials are updated on settled nodes
        /// only, less a constant (see <see cref="SolvePhased"/>), so a search costs what it
        /// explored. Arc costs must be non-negative.
        /// </remarks>
        public int SolveExcess(int[] excess)
        {
            if (excess == null || excess.Length != m_NodeCount)
                throw new ArgumentException("One excess per node", nameof(excess));
            totalCost = 0;
            totalFlow = 0;
            phases = 0;
            settled = 0;
            if (m_NodeCount == 0) return 0;
            if (m_MinCost < 0) throw new InvalidOperationException("SolveExcess needs non-negative costs.");

            Array.Clear(m_Potential, 0, m_NodeCount);
            if (m_Touched == null)
            {
                m_Touched = new int[m_NodeCount];
                for (var i = 0; i < m_NodeCount; i++) m_Distance[i] = long.MaxValue;
            }
            m_TouchedCount = 0;

            for (var k = 0; k < m_NodeCount; k++)
            {
                while (excess[k] > 0)
                {
                    var deficit = NearestDeficit(k, excess);
                    if (deficit < 0) break;
                    phases++;

                    var reach = m_Distance[deficit];
                    for (var i = 0; i < m_TouchedCount; i++)
                    {
                        var v = m_Touched[i];
                        if (m_Settled[v] && m_Distance[v] < reach)
                            m_Potential[v] -= reach - m_Distance[v];
                    }

                    var push = Math.Min(excess[k], -excess[deficit]);
                    for (var v = deficit; v != k; )
                    {
                        var arc = m_ArcInto[v];
                        push = Math.Min(push, m_Capacity[arc]);
                        v = m_Head[arc ^ 1];
                    }
                    if (push <= 0) break;
                    for (var v = deficit; v != k; )
                    {
                        var arc = m_ArcInto[v];
                        m_Capacity[arc] -= push;
                        m_Capacity[arc ^ 1] += push;
                        totalCost += (long)push * m_Cost[arc];
                        v = m_Head[arc ^ 1];
                    }
                    excess[k] -= push;
                    excess[deficit] += push;
                    totalFlow += push;
                }
            }

            return totalFlow;
        }

        /// <summary>
        /// Dijkstra from <paramref name="source"/> over reduced costs until the first node with a
        /// deficit is settled; returns it, or -1. Resets only what the last search touched.
        /// </summary>
        int NearestDeficit(int source, int[] excess)
        {
            for (var i = 0; i < m_TouchedCount; i++)
            {
                var v = m_Touched[i];
                m_Distance[v] = long.MaxValue;
                m_Settled[v] = false;
                m_ArcInto[v] = -1;
            }
            m_TouchedCount = 0;

            m_Distance[source] = 0;
            m_Touched[m_TouchedCount++] = source;
            m_HeapCount = 0;
            HeapPush(0, source);

            while (m_HeapCount > 0)
            {
                var distance = m_HeapDistance[0];
                var v = m_HeapNode[0];
                HeapPop();
                if (m_Settled[v]) continue;
                m_Settled[v] = true;
                settled++;
                if (excess[v] < 0) return v;

                for (var arc = m_First[v]; arc >= 0; arc = m_Next[arc])
                {
                    if (m_Capacity[arc] <= 0) continue;
                    var to = m_Head[arc];
                    if (m_Settled[to]) continue;
                    var candidate = distance + m_Cost[arc] + m_Potential[v] - m_Potential[to];
                    if (candidate >= m_Distance[to]) continue;
                    if (m_Distance[to] == long.MaxValue) m_Touched[m_TouchedCount++] = to;
                    m_Distance[to] = candidate;
                    m_ArcInto[to] = arc;
                    HeapPush(candidate, to);
                }
            }

            return -1;
        }

        /// <summary>
        /// Dijkstra for the phased solver: resets only what the previous phase touched, and never
        /// queues a node at or beyond the best distance to the sink found so far -- such a node
        /// cannot be on a shortest path, and the potential update treats it as unreached anyway.
        /// </summary>
        bool ShortestPathLocal(int source, int sink)
        {
            for (var i = 0; i < m_TouchedCount; i++)
            {
                var v = m_Touched[i];
                m_Distance[v] = long.MaxValue;
                m_Settled[v] = false;
                m_ArcInto[v] = -1;
            }
            m_TouchedCount = 0;

            m_Distance[source] = 0;
            m_Touched[m_TouchedCount++] = source;
            m_HeapCount = 0;
            HeapPush(0, source);

            while (m_HeapCount > 0)
            {
                var distance = m_HeapDistance[0];
                var v = m_HeapNode[0];
                HeapPop();

                if (m_Settled[v]) continue;
                if (distance >= m_Distance[sink]) break;
                m_Settled[v] = true;
                settled++;

                if (v == sink) break;

                var bound = m_Distance[sink];
                for (var arc = m_First[v]; arc >= 0; arc = m_Next[arc])
                {
                    if (m_Capacity[arc] <= 0) continue;

                    var to = m_Head[arc];
                    if (m_Settled[to]) continue;

                    var candidate = distance + m_Cost[arc] + m_Potential[v] - m_Potential[to];
                    if (candidate >= m_Distance[to] || candidate >= bound) continue;

                    if (m_Distance[to] == long.MaxValue) m_Touched[m_TouchedCount++] = to;
                    m_Distance[to] = candidate;
                    m_ArcInto[to] = arc;
                    if (to == sink) { bound = candidate; continue; }
                    HeapPush(candidate, to);
                }
            }

            if (m_Distance[sink] != long.MaxValue) m_Settled[sink] = true;
            return m_Distance[sink] != long.MaxValue;
        }

        /// <summary>
        /// One DFS from source to sink over admissible arcs (residual capacity, zero reduced cost),
        /// with Dinic-style current-arc pointers and dead marks so a phase scans each arc a bounded
        /// number of times. Returns the units pushed, zero when no admissible path remains.
        /// </summary>
        int AugmentAdmissible(int source, int sink, int limit)
        {
            if (m_ArcPhase[source] != m_Phase) { m_ArcPhase[source] = m_Phase; m_CurrentArc[source] = m_First[source]; }
            // m_Dead doubles as "on the current stack" (-phase) and "dead this phase" (+phase), so
            // a zero-cost cycle in the admissible graph cannot trap the search.
            var depth = 0;
            m_StackNode[0] = source;
            m_Dead[source] = -m_Phase;

            while (depth >= 0)
            {
                var v = m_StackNode[depth];
                if (v == sink)
                {
                    var push = limit;
                    for (var i = 0; i < depth; i++)
                        push = Math.Min(push, m_Capacity[m_CurrentArc[m_StackNode[i]]]);
                    for (var i = 0; i < depth; i++)
                    {
                        var arc = m_CurrentArc[m_StackNode[i]];
                        m_Capacity[arc] -= push;
                        m_Capacity[arc ^ 1] += push;
                        totalCost += (long)push * m_Cost[arc];
                    }
                    for (var i = 0; i <= depth; i++)
                        if (m_Dead[m_StackNode[i]] == -m_Phase) m_Dead[m_StackNode[i]] = 0;
                    return push;
                }

                var advanced = false;
                for (var arc = m_CurrentArc[v]; arc >= 0; arc = m_Next[arc])
                {
                    m_CurrentArc[v] = arc;
                    if (m_Capacity[arc] <= 0) continue;
                    var to = m_Head[arc];
                    var mark = m_Dead[to];
                    if (mark == m_Phase || mark == -m_Phase) continue;
                    if (m_Cost[arc] + m_Potential[v] - m_Potential[to] != 0) continue;
                    if (m_ArcPhase[to] != m_Phase) { m_ArcPhase[to] = m_Phase; m_CurrentArc[to] = m_First[to]; }
                    m_Dead[to] = -m_Phase;
                    m_StackNode[++depth] = to;
                    advanced = true;
                    break;
                }

                if (advanced) continue;

                // Nothing admissible leaves v this phase.
                m_CurrentArc[v] = -1;
                m_Dead[v] = m_Phase;
                depth--;
                if (depth >= 0)
                {
                    // Retreat: the parent's current arc led here and is spent for this phase.
                    var parent = m_StackNode[depth];
                    var spent = m_CurrentArc[parent];
                    if (spent >= 0) m_CurrentArc[parent] = m_Next[spent];
                }
            }

            return 0;
        }

        /// <summary>
        /// Bellman-Ford once, so the first Dijkstra has valid potentials even if a caller supplied a
        /// negative cost. With all-non-negative costs this settles immediately at the true
        /// distances, so it is not wasted work.
        /// </summary>
        void SeedPotentials(int source)
        {
            for (var i = 0; i < m_NodeCount; i++) m_Potential[i] = long.MaxValue;
            m_Potential[source] = 0;

            for (var round = 0; round < m_NodeCount; round++)
            {
                var changed = false;

                for (var v = 0; v < m_NodeCount; v++)
                {
                    if (m_Potential[v] == long.MaxValue) continue;

                    for (var arc = m_First[v]; arc >= 0; arc = m_Next[arc])
                    {
                        if (m_Capacity[arc] <= 0) continue;

                        var to = m_Head[arc];
                        var candidate = m_Potential[v] + m_Cost[arc];
                        if (candidate >= m_Potential[to]) continue;

                        m_Potential[to] = candidate;
                        changed = true;
                    }
                }

                if (!changed) break;
            }

            // A node the source cannot reach has no meaningful potential; zero keeps the reduced
            // costs finite and such nodes never enter a path anyway.
            for (var i = 0; i < m_NodeCount; i++)
                if (m_Potential[i] == long.MaxValue) m_Potential[i] = 0;
        }

        /// <summary>
        /// Dijkstra over reduced costs. Returns false when the sink is unreachable, which is how
        /// <see cref="Solve"/> learns the flow is maximal.
        /// </summary>
        bool ShortestPath(int source, int sink)
        {
            for (var i = 0; i < m_NodeCount; i++)
            {
                m_Distance[i] = long.MaxValue;
                m_Settled[i] = false;
                m_ArcInto[i] = -1;
            }

            m_Distance[source] = 0;

            m_HeapCount = 0;
            HeapPush(0, source);

            while (m_HeapCount > 0)
            {
                var distance = m_HeapDistance[0];
                var v = m_HeapNode[0];
                HeapPop();

                if (m_Settled[v]) continue;
                m_Settled[v] = true;
                settled++;

                if (v == sink) break;

                for (var arc = m_First[v]; arc >= 0; arc = m_Next[arc])
                {
                    if (m_Capacity[arc] <= 0) continue;

                    var to = m_Head[arc];
                    if (m_Settled[to]) continue;

                    // Reduced cost. Non-negative by construction once the potentials are valid,
                    // which is the whole reason Dijkstra may be used on a graph whose residual arcs
                    // carry negative real costs.
                    //
                    // Deliberately NOT clamped. A negative value here means the potential update is
                    // wrong, and clamping it turns a detectable invariant violation into a
                    // silently non-optimal answer — which is what it did. See the d[sink] clamp in
                    // Solve, which is where the invariant is actually maintained.
                    var weight = m_Cost[arc] + m_Potential[v] - m_Potential[to];

                    var candidate = distance + weight;
                    if (candidate >= m_Distance[to]) continue;

                    m_Distance[to] = candidate;
                    m_ArcInto[to] = arc;
                    HeapPush(candidate, to);
                }
            }

            return m_Distance[sink] != long.MaxValue;
        }

        void HeapPush(long distance, int node)
        {
            if (m_HeapCount == m_HeapDistance.Length)
            {
                var grown = m_HeapDistance.Length * 2;
                Array.Resize(ref m_HeapDistance, grown);
                Array.Resize(ref m_HeapNode, grown);
            }

            var i = m_HeapCount++;
            m_HeapDistance[i] = distance;
            m_HeapNode[i] = node;

            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (m_HeapDistance[parent] <= m_HeapDistance[i]) break;
                Swap(parent, i);
                i = parent;
            }
        }

        void HeapPop()
        {
            m_HeapCount--;
            if (m_HeapCount <= 0) return;

            m_HeapDistance[0] = m_HeapDistance[m_HeapCount];
            m_HeapNode[0] = m_HeapNode[m_HeapCount];

            var i = 0;
            while (true)
            {
                var left = i * 2 + 1;
                if (left >= m_HeapCount) break;

                var smallest = left;
                var right = left + 1;
                if (right < m_HeapCount && m_HeapDistance[right] < m_HeapDistance[left])
                    smallest = right;

                if (m_HeapDistance[i] <= m_HeapDistance[smallest]) break;

                Swap(i, smallest);
                i = smallest;
            }
        }

        void Swap(int a, int b)
        {
            (m_HeapDistance[a], m_HeapDistance[b]) = (m_HeapDistance[b], m_HeapDistance[a]);
            (m_HeapNode[a], m_HeapNode[b]) = (m_HeapNode[b], m_HeapNode[a]);
        }
    }
}
