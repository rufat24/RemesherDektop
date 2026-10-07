// Passes E and F: walk the embedded graph and harvest faces.
//
// Two of the extractor's three verified defects live here.
//
// DEFECT 1 — one pass, first-close-wins. The old tracer walked until the loop closed and accepted
// whatever size it got. On a quad lattice a quad's two diagonals are also short walks, so a
// three-step loop is almost always available and gets found first. The reference instead runs
// DEGREE-ORDERED passes and accepts a walk only if it closes at exactly the size that pass is
// looking for, with the order deliberately putting 4 before 3:
//
//     for (deg = 3..8):  actual = (deg == 3 || deg == 4) ? 7 - deg : deg     ->  4, 3, 5, 6, 7, 8
//
// Quads therefore claim their edges before triangles get a chance to.
//
// DEFECT 2 — destructive failure. The old tracer marked each directed edge consumed as it stepped
// onto it, before knowing whether the walk would close. A failed walk permanently poisoned its
// edges, so those edges could never form a face afterwards — and with no cleanup pass, failed walks
// were the common case. Here a walk collects its slots in a scratch list and commits only on
// success.

using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class TraceStats
    {
        public int[] facesBySize = new int[9];   // index == corner count, 0..8
        public int walksAttempted;
        public int walksFailed;
        public int slotsUnused;
        /// <summary>Retired: the catch-all pass it counted was removed. See the note in Trace.</summary>
        public int holesClosed;

        public int totalFaces
        {
            get
            {
                var total = 0;
                for (var i = 0; i < facesBySize.Length; i++) total += facesBySize[i];
                return total;
            }
        }

        public override string ToString()
        {
            return $"faces={totalFaces} (quads={facesBySize[4]} tris={facesBySize[3]} " +
                   $"5={facesBySize[5]} 6={facesBySize[6]} 7={facesBySize[7]} 8={facesBySize[8]}) " +
                   $"walks={walksAttempted} failed={walksFailed} holesClosed={holesClosed} " +
                   $"unusedSlots={slotsUnused}";
        }
    }

    public static class FaceTracer
    {
        public const int maxFaceSize = 8;

        /// <summary>
        /// Upper bound for the catch-all pass. Generous on purpose: past this a loop is not a face
        /// worth keeping, but stopping too early leaves the hole the pass exists to close.
        /// </summary>
        public const int catchAllMaxFaceSize = 32;

        public static List<int[]> Trace(FrozenQuadGraph graph, out TraceStats stats)
        {
            stats = new TraceStats();
            var faces = new List<int[]>(graph.vertexCount);

            graph.ResetUsed();

            var walkVertices = new List<int>(catchAllMaxFaceSize + 1);
            var walkSlots = new List<int>(catchAllMaxFaceSize + 1);

            for (var pass = 3; pass <= maxFaceSize; pass++)
            {
                // 4 before 3, so quads claim their edges first.
                var targetSize = (pass == 3 || pass == 4) ? 7 - pass : pass;

                for (var a = 0; a < graph.vertexCount; a++)
                {
                    for (var slot = graph.adjacencyStart[a]; slot < graph.adjacencyStart[a + 1]; slot++)
                    {
                        if (graph.used[slot]) continue;

                        stats.walksAttempted++;

                        if (TryWalk(graph, slot, targetSize, walkVertices, walkSlots))
                        {
                            foreach (var s in walkSlots) graph.used[s] = true;
                            faces.Add(walkVertices.ToArray());
                            stats.facesBySize[walkVertices.Count]++;
                        }
                        else
                        {
                            stats.walksFailed++;
                        }
                    }
                }
            }

            // No catch-all pass here, deliberately.
            //
            // Everything the sized passes could not close is a hole, and a hole is the most visible
            // defect this pipeline can produce — so the first fix was one more pass with no target
            // size and a generous cap, accepting any loop that closed. Measured, that removed the
            // holes (boundary edges went to zero on a closed sphere) and made the topology WORSE:
            // Euler went from -7 to -14 instead of to 2. A walk over the graph is free to leave the
            // neighbourhood of the hole, and a loop that wanders far enough and returns has closed
            // a tunnel, not a cap — every such face adds a handle.
            //
            // Closing holes is now <see cref="HoleFiller"/>'s job, working on the emitted faces'
            // own boundary loops, where capping a simple cycle cannot add genus. Do not reinstate a
            // size-free walk here.
            for (var s = 0; s < graph.slotCount; s++)
                if (!graph.used[s]) stats.slotsUnused++;

            return faces;
        }

        /// <summary>
        /// Walks the face to the left of directed edge <paramref name="startSlot"/>, succeeding only
        /// if it closes after exactly <paramref name="targetSize"/> corners. Never mutates
        /// <c>graph.used</c> — the caller commits.
        /// </summary>
        /// <param name="targetSize">
        /// Exact corner count the walk must close at, or 0 to accept any loop up to
        /// <see cref="catchAllMaxFaceSize"/>. The exact form is what lets quads claim their edges
        /// before triangles; the any-size form exists only to close what would otherwise be a hole.
        /// </param>
        static bool TryWalk(FrozenQuadGraph graph, int startSlot, int targetSize,
            List<int> verticesOut, List<int> slotsOut)
        {
            verticesOut.Clear();
            slotsOut.Clear();

            var anySize = targetSize <= 0;   // retained for the size-free walk in tests only
            var cap = anySize ? catchAllMaxFaceSize : targetSize;

            var slot = startSlot;

            for (var step = 0; step <= cap; step++)
            {
                if (graph.used[slot]) return false;

                // A slot appearing twice in one walk means the loop is pinched and is not a face.
                for (var i = 0; i < slotsOut.Count; i++)
                    if (slotsOut[i] == slot) return false;

                var from = VertexOfSlot(graph, slot);
                var to = graph.adjacency[slot];

                verticesOut.Add(from);
                slotsOut.Add(slot);

                if (verticesOut.Count > cap) return false;

                // Step forward from the reverse edge: arriving at `to` along this edge, the next
                // edge of the same face is the one immediately counter-clockwise from the way we
                // came. The reference advances (reverse + 1); the old code went (index - 1), which
                // turns the other way and traces the face on the opposite side. Either is
                // self-consistent, but only one matches the winding everything downstream assumes.
                var reverse = graph.reverseSlot[slot];
                if (reverse < 0) return false;              // dangling: no face on this side

                var ringStart = graph.adjacencyStart[to];
                var ringEnd = graph.adjacencyStart[to + 1];
                var degree = ringEnd - ringStart;
                if (degree == 0) return false;

                var nextSlot = ringStart + ((reverse - ringStart) + 1) % degree;

                if (nextSlot == startSlot)
                    return anySize ? verticesOut.Count >= 3 : verticesOut.Count == targetSize;

                slot = nextSlot;
            }

            return false;
        }

        /// <summary>
        /// The origin vertex of a directed-edge slot. Found by binary search on adjacencyStart,
        /// which is sorted by construction — cheaper than carrying a parallel owner array, and this
        /// is not in the innermost loop.
        /// </summary>
        static int VertexOfSlot(FrozenQuadGraph graph, int slot)
        {
            int lo = 0, hi = graph.vertexCount;
            while (lo + 1 < hi)
            {
                var mid = (lo + hi) / 2;
                if (graph.adjacencyStart[mid] <= slot) lo = mid; else hi = mid;
            }
            return lo;
        }
    }
}
