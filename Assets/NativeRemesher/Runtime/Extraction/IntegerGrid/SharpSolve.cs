// Port of QuadriFlow's Optimizer::optimize_positions_sharp (src/optimizer.cpp), the stage that makes
// the integer grid map actually hold a crease.
//
// ApplySharpEdgeConstraints already tells the MAP that a crease is an iso-line: an edge along it may
// only step in one lattice direction. That is half of what is needed and, on its own, the costly
// half. It spends degrees of freedom -- one offset component locked per crease edge -- and buys
// nothing geometric, because nothing then moves the output vertices onto the crease. Measured on a
// helmeted head, marking creases without this stage tore the helmet open (184 boundary edges against
// 54) while the brim it was meant to protect still came out rounded.
//
// This is the other half. Crease edges are gathered per lattice cell and traced into polylines --
// open chains and closed loops. Each chain is resampled at even lattice spacing along its own arc
// length, and every cell on it gets a constraint: a point on the crease and the crease's direction
// there. The fixed-position solve then keeps that cell on the line and lets it slide only along it;
// a cell where chains meet or end is a corner and does not move at all.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    /// <summary>
    /// A point a crease passes through and the crease's direction there. A zero direction marks a
    /// corner, which is held in place outright.
    /// </summary>
    public struct SharpConstraint
    {
        public Vector3 position;
        public Vector3 direction;

        /// <summary>
        /// The crease points either side of <see cref="position"/>. The solve slides a cell along
        /// <see cref="direction"/>, which is only the crease's tangent; these are what the result is
        /// projected back onto, so a curved crease stays curved.
        /// </summary>
        public Vector3 previous, next;

        /// <summary>The point on the polyline previous - position - next nearest <paramref name="p"/>.</summary>
        public Vector3 Project(Vector3 p)
        {
            var a = ClosestOnSegment(previous, position, p);
            var b = ClosestOnSegment(position, next, p);
            return (a - p).sqrMagnitude <= (b - p).sqrMagnitude ? a : b;
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            var ab = b - a;
            var length = ab.sqrMagnitude;
            if (length <= 0f) return a;
            return a + ab * Mathf.Clamp01(Vector3.Dot(p - a, ab) / length);
        }

        public bool isCorner => direction == Vector3.zero;
    }

    public partial class IntegerGridMap
    {
        /// <summary>
        /// Chains shorter than this many cells carry no constraint. A crease two cells long cannot
        /// be resampled and on a scan is almost always a fragment of noise; the reference drops it.
        /// </summary>
        const int k_MinChainCells = 3;

        /// <summary>Whether the crease stage runs. Diagnostic switch.</summary>
        public static bool creaseConstraints = true;

        /// <summary>Whether output vertices are moved onto their cell's feature vertices. Diagnostic switch.</summary>
        public static bool placeFeatureVertices = true;

        /// <summary>
        /// Treat open-boundary edges as creases, as the reference does under <c>-boundary</c>. A
        /// closed surface holds its own shape through the lattice all the way round; an open one
        /// has nothing past its rim, so the position solve is free to pull the rim inward. Pinning
        /// rim cells so they may only slide along the rim is what stops that.
        /// </summary>
        public static bool creaseBoundaries = true;

        bool IsCreaseHalfEdge(int h) =>
            (h < sharp.Count && sharp[h]) || (creaseBoundaries && h < E2E.Count && E2E[h] == -1);

        static long CellPairKey(int a, int b)
        {
            var lo = Math.Min(a, b);
            var hi = Math.Max(a, b);
            return ((long)lo << 32) | (uint)hi;
        }

        /// <summary>
        /// Traces the creases through the lattice cells, moves the position field onto evenly
        /// resampled crease polylines, and returns a constraint for every input vertex in a crease
        /// cell. Hand the result to <see cref="OptimizePositionsFixed"/>.
        /// </summary>
        public Dictionary<int, SharpConstraint> OptimizePositionsSharp()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var constraints = new Dictionary<int, SharpConstraint>();

            // Input vertex -> the vertices a crease half-edge leaves it towards. Kept per vertex in
            // index order so every walk below is deterministic.
            var vertexLinks = new SortedDictionary<int, SortedSet<int>>();
            var sharpVertices = new SortedSet<int>();
            for (var h = 0; h < F.Count; h++)
            {
                if (!IsCreaseHalfEdge(h)) continue;
                var v1 = F[h];
                var v2 = F[Next3(h)];
                sharpVertices.Add(v1);
                sharpVertices.Add(v2);
                if (!vertexLinks.TryGetValue(v1, out var set))
                    vertexLinks[v1] = set = new SortedSet<int>();
                set.Add(v2);
            }

            stats.sharpVertices = sharpVertices.Count;
            if (sharpVertices.Count == 0)
            {
                stats.sharpMs = watch.ElapsedMilliseconds;
                return constraints;
            }

            var tree = new DisjointTree(V.Count);
            for (var e = 0; e < edgeCount; e++)
                if (edgeDiff[e] == Vector2Int.zero)
                    tree.Merge(edgeX[e], edgeY[e]);
            tree.BuildCompactParent();

            var crossesCells = new HashSet<long>(EdgeKey.Comparer);
            for (var h = 0; h < F.Count; h++)
            {
                if (!IsCreaseHalfEdge(h)) continue;
                crossesCells.Add(CellPairKey(tree.Index(F[h]), tree.Index(F[Next3(h)])));
            }

            // Every cell holding a crease vertex gets a compact index.
            var compactOf = new Dictionary<int, int>();
            foreach (var v in sharpVertices)
            {
                var p = tree.Index(v);
                if (!compactOf.ContainsKey(p)) compactOf[p] = compactOf.Count;
            }

            var num = compactOf.Count;
            var members = new List<int>[num];
            for (var s = 0; s < num; s++) members[s] = new List<int>();

            // Member order matters: the first entry is the cell's representative, and the reference
            // puts crease corners and ends ahead of mid-crease vertices so a cell that holds a corner
            // is represented by it.
            foreach (var entry in vertexLinks)
                if (entry.Value.Count != 2)
                    members[compactOf[tree.Index(entry.Key)]].Add(entry.Key);
            foreach (var entry in vertexLinks)
                if (entry.Value.Count == 2)
                    members[compactOf[tree.Index(entry.Key)]].Add(entry.Key);
            for (var i = 0; i < V.Count; i++)
            {
                if (sharpVertices.Contains(i)) continue;
                if (compactOf.TryGetValue(tree.Index(i), out var s)) members[s].Add(i);
            }

            // The crease graph over cells.
            var cellLinks = new SortedSet<int>[num];
            for (var s = 0; s < num; s++) cellLinks[s] = new SortedSet<int>();
            for (var e = 0; e < edgeCount; e++)
            {
                var p1 = tree.Index(edgeX[e]);
                var p2 = tree.Index(edgeY[e]);
                if (p1 == p2 || !crossesCells.Contains(CellPairKey(p1, p2))) continue;
                if (!compactOf.TryGetValue(p1, out var a) || !compactOf.TryGetValue(p2, out var b)) continue;
                cellLinks[a].Add(b);
                cellLinks[b].Add(a);
            }

            int NextAlong(int v, int previous)
            {
                var next = -1;
                foreach (var candidate in cellLinks[v])
                    if (candidate != previous) next = candidate;
                return next;
            }

            var visited = new bool[num];
            var chains = 0;
            var constrainedCells = 0;

            for (var start = 0; start < num; start++)
            {
                if (visited[start] || cellLinks[start].Count != 2) continue;

                // Walk forward from a mid-crease cell until the chain ends, branches, or closes.
                var chain = new List<int> { start };
                visited[start] = true;
                var v = start;
                var previous = -1;
                var isLoop = false;
                while (cellLinks[v].Count == 2)
                {
                    var next = NextAlong(v, previous);
                    if (next < 0) break;
                    if (visited[next])
                    {
                        isLoop = true;
                        break;
                    }
                    if (cellLinks[next].Count == 2) visited[next] = true;
                    chain.Add(next);
                    previous = v;
                    v = next;
                }

                // An open chain was entered somewhere in its middle: walk back the other way too.
                if (!isLoop && chain.Count >= 2)
                {
                    var back = new List<int>();
                    v = start;
                    previous = chain[1];
                    while (cellLinks[v].Count == 2)
                    {
                        var next = NextAlong(v, previous);
                        if (next < 0) break;
                        if (visited[next])
                        {
                            isLoop = true;
                            break;
                        }
                        if (cellLinks[next].Count == 2) visited[next] = true;
                        back.Add(next);
                        previous = v;
                        v = next;
                    }
                    back.Reverse();
                    back.AddRange(chain);
                    chain = back;
                }

                if (chain.Count < k_MinChainCells) continue;

                var skip = false;
                foreach (var cell in chain)
                    if (members[cell].Count == 0) skip = true;
                if (skip) continue;

                if (isLoop) chain.Add(chain[0]);
                ResampleCrease(chain, isLoop, members, cellLinks, constraints);
                chains++;
                constrainedCells += isLoop ? chain.Count - 1 : chain.Count;
            }

            stats.sharpChains = chains;
            stats.sharpConstrainedCells = constrainedCells;
            stats.sharpConstrainedVertices = constraints.Count;
            stats.sharpMs = watch.ElapsedMilliseconds;
            return constraints;
        }

        /// <summary>
        /// Records a constraint for every vertex of every cell on the chain, then moves the position
        /// field of those cells to points spaced evenly along the chain's own arc length.
        /// </summary>
        void ResampleCrease(List<int> chain, bool isLoop, List<int>[] members, SortedSet<int>[] cellLinks,
            Dictionary<int, SharpConstraint> constraints)
        {
            var n = chain.Count;
            var o = new Vector3[n];
            var spacing = new float[n];
            // The crease polyline runs through the INPUT positions of its crease vertices, not through
            // the position field there. The reference reads O, but its position solve has already
            // pulled O onto creases and boundaries by then; ours has not, so a lattice point can sit
            // up to half a cell off the crease it belongs to, and pinning the cell there drags the
            // crease with it. Measured on the open disc, whose only "crease" is its rim: reading O
            // took Hausdorff error from 0.0009 to 0.0184. The representative is a crease vertex --
            // members are ordered with those first -- so its input position is on the crease.
            for (var k = 0; k < n; k++)
            {
                var representative = members[chain[k]][0];
                o[k] = V[representative];
                spacing[k] = ScaleAt(representative);
            }

            // Directions from the ORIGINAL polyline, as the reference records them, before the
            // resampling below moves anything.
            var distinct = isLoop ? n - 1 : n;
            for (var k = 0; k < n; k++)
            {
                var direction = Vector3.zero;
                if (isLoop)
                {
                    var i = k % distinct;
                    direction = (o[(i + 1) % distinct] - o[(i + distinct - 1) % distinct]).normalized;
                }
                else if (k != 0 && k != n - 1)
                {
                    direction = (o[k + 1] - o[k - 1]).normalized;
                }
                else if (cellLinks[chain[k]].Count == 1)
                {
                    // A crease that simply stops may still slide along itself; one that ends at a
                    // junction is a corner and stays where it is.
                    direction = k == 0 ? (o[1] - o[0]).normalized : (o[k] - o[k - 1]).normalized;
                }

                // Neighbouring crease points, for projecting the solved position back onto the
                // crease itself. A loop wraps; an open chain's ends use themselves.
                Vector3 before, after;
                if (isLoop)
                {
                    var i = k % distinct;
                    before = o[(i + distinct - 1) % distinct];
                    after = o[(i + 1) % distinct];
                }
                else
                {
                    before = k > 0 ? o[k - 1] : o[k];
                    after = k < n - 1 ? o[k + 1] : o[k];
                }

                foreach (var vertex in members[chain[k]])
                    constraints[vertex] = new SharpConstraint
                    {
                        position = o[k], direction = direction, previous = before, next = after,
                    };
            }

            // Even resampling by arc length, weighted by the local lattice spacing. The endpoints
            // stay put; every interior cell moves to its share of the length.
            var length = 0.0;
            var weight = 0.0;
            for (var k = 0; k < n - 1; k++)
            {
                length += (o[k + 1] - o[k]).magnitude;
                weight += spacing[k];
            }

            var resampled = (Vector3[])o.Clone();
            if (length > 0.0 && weight > 0.0)
            {
                var walk = (Vector3[])o.Clone();
                var remaining = length * spacing[0] / weight;
                var segment = 0;
                var segmentLength = (double)(walk[1] - walk[0]).magnitude;

                for (var k = 1; k < n - 1; k++)
                {
                    while (remaining >= segmentLength && segment + 2 < n)
                    {
                        remaining -= segmentLength;
                        segment++;
                        segmentLength = (walk[segment + 1] - walk[segment]).magnitude;
                    }

                    if (segmentLength <= 1e-12) break;

                    var t = (float)(remaining / segmentLength);
                    resampled[k] = Vector3.Lerp(walk[segment], walk[segment + 1], Mathf.Clamp01(t));
                    walk[segment] = resampled[k];
                    segmentLength -= remaining;
                    remaining = length * spacing[segment] / weight;
                }
            }

            for (var k = 0; k < n; k++)
            {
                // Resampling only evens out the spacing along the crease, which never needs more
                // than a fraction of a cell. A larger move means the walk above stepped between two
                // creases sharing cells -- both lips of a seam, a step's top and bottom -- and the
                // "arc length" ran along a zig-zag between them; following it dragged a cell's
                // field several cells along the wrong line and extraction made a needle face of it,
                // measured on the body with creases on: 18 faces more than 4x the median edge.
                var target = resampled[k];
                if (maxResampleMoveCells > 0f &&
                    (target - o[k]).sqrMagnitude > Sq(maxResampleMoveCells * spacing[k]))
                    target = o[k];
                foreach (var vertex in members[chain[k]])
                    O[vertex] = target;
            }
        }

        static float Sq(float x) => x * x;

        /// <summary>
        /// Furthest, in cells, crease resampling may move a cell's field; past it the cell stays
        /// on its own crease point. Zero lets every move through, as the reference does.
        /// </summary>
        public static float maxResampleMoveCells = 0.75f;
    }
}
