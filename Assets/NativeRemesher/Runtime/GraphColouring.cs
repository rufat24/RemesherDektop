// Greedy graph colouring, so the Gauss-Seidel field sweeps can run on more than one core.
//
// WHY THIS IS NOT A CHANGE OF ALGORITHM, because the plan for this project claimed it was.
//
// Section 7 of the plan says parallelising Gauss-Seidel means switching to coloured Jacobi, which
// "changes the numerics". That is wrong, and it mattered: it is the reason both solvers were left
// single-threaded while a scan took a minute and a half.
//
// Within one colour class no two vertices are adjacent. A vertex therefore reads only neighbours
// belonging to *other* classes, so no read can ever observe a value that is mid-update, and the
// result of updating a whole class in parallel is bit-identical to updating its members one at a
// time in any order. What the colouring changes is the ORDER the sweep visits vertices in — from
// index order to colour order — which is a different Gauss-Seidel sweep, not a different method.
// Jacobi would be reading every neighbour from the previous iteration, and that is not what happens
// here.
//
// Both reference implementations parallelise exactly this way and for exactly this reason:
// Instant Meshes runs tbb::parallel_for over mRes.phases(level), and QuadriFlow runs
// #pragma omp parallel for over its phase lists.
//
// Determinism survives too, which matters because the benchmark gate compares runs. The colouring
// is greedy in index order rather than randomised, and within a class each vertex reads only
// values that other classes have already finished with, so the outcome does not depend on how the
// work was scheduled.

using System.Collections.Generic;

namespace NativeRemesher
{
    /// <summary>
    /// A partition of a graph's vertices into independent sets, stored as a flat CSR so a sweep
    /// can iterate one class without chasing an array of arrays.
    /// </summary>
    public sealed class GraphColouring
    {
        /// <summary>Class c occupies vertices[start[c] .. start[c + 1]).</summary>
        public int[] start;

        public int[] vertices;

        public int classCount => start != null ? start.Length - 1 : 0;

        /// <summary>
        /// The largest class, which bounds how much parallelism is actually available. A colouring
        /// whose classes are all tiny costs more in scheduling than it saves.
        /// </summary>
        public int largestClass;

        public override string ToString() => $"{classCount} classes, largest {largestClass:N0}";

        /// <summary>
        /// Colours a CSR adjacency graph greedily: each vertex takes the lowest colour none of its
        /// neighbours holds.
        /// </summary>
        /// <remarks>
        /// Index order, not random. Greedy colouring is not optimal either way, and a fixed order
        /// is what keeps two runs of the same input producing the same numbers — which the
        /// regression gate depends on.
        /// </remarks>
        public static GraphColouring Build(int vertexCount, int[] adjacencyStart, int[] adjacency)
        {
            var colour = new int[vertexCount];
            for (var v = 0; v < vertexCount; v++) colour[v] = -1;

            // The colouring has to hold in BOTH directions, and the adjacency it is handed need
            // not: where the topology lists u as v's neighbour without listing v as u's -- at
            // non-manifold and open edges -- a colouring built from v's list alone let u and v
            // share a class. The sweep for u still reads v, so the two raced. Measured on ccnb's
            // refined topology: 5,249 one-way pairs, 625 neighbours sharing a class, and two runs
            // of the same input giving different meshes. So each vertex also avoids the colours
            // of everything that lists IT.
            var reverseStart = new int[vertexCount + 1];
            for (var s = 0; s < adjacency.Length; s++) reverseStart[adjacency[s] + 1]++;
            for (var v = 0; v < vertexCount; v++) reverseStart[v + 1] += reverseStart[v];
            var reverse = new int[adjacency.Length];
            var reverseFill = (int[])reverseStart.Clone();
            for (var v = 0; v < vertexCount; v++)
                for (var s = adjacencyStart[v]; s < adjacencyStart[v + 1]; s++)
                    reverse[reverseFill[adjacency[s]]++] = v;

            var taken = new List<bool>(8);
            var colourCount = 0;

            for (var v = 0; v < vertexCount; v++)
            {
                for (var c = 0; c < taken.Count; c++) taken[c] = false;

                for (var s = adjacencyStart[v]; s < adjacencyStart[v + 1]; s++)
                {
                    var c = colour[adjacency[s]];
                    if (c >= 0 && c < taken.Count) taken[c] = true;
                }

                for (var s = reverseStart[v]; s < reverseStart[v + 1]; s++)
                {
                    var c = colour[reverse[s]];
                    if (c >= 0 && c < taken.Count) taken[c] = true;
                }

                var chosen = -1;
                for (var c = 0; c < taken.Count; c++)
                {
                    if (taken[c]) continue;
                    chosen = c;
                    break;
                }

                if (chosen < 0)
                {
                    chosen = colourCount++;
                    taken.Add(false);
                }

                colour[v] = chosen;
            }

            var result = new GraphColouring
            {
                start = new int[colourCount + 1],
                vertices = new int[vertexCount]
            };

            for (var v = 0; v < vertexCount; v++) result.start[colour[v] + 1]++;

            for (var c = 0; c < colourCount; c++)
            {
                result.largestClass = result.start[c + 1] > result.largestClass
                    ? result.start[c + 1]
                    : result.largestClass;
                result.start[c + 1] += result.start[c];
            }

            var fill = new int[colourCount];
            for (var v = 0; v < vertexCount; v++)
            {
                var c = colour[v];
                result.vertices[result.start[c] + fill[c]++] = v;
            }

            return result;
        }
    }
}
