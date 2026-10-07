// The colouring's contract is what makes the parallel field sweeps legitimate, so it is tested
// directly rather than inferred from the fields converging.
//
// The claim being defended: updating a whole colour class at once is identical to updating its
// members one at a time, because within a class no vertex can read a neighbour that is mid-update.
// That holds if and only if no two vertices in a class are adjacent. If the colouring ever stopped
// guaranteeing that, both solvers would silently become a racy hybrid of Gauss-Seidel and Jacobi
// whose output depended on thread scheduling — and the symptom would be benchmark numbers that
// drift between runs, which is a long way from the cause.

using NUnit.Framework;
using NativeRemesher;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class GraphColouringTests
    {
        static RemeshTopology Build(Mesh mesh)
        {
            try { return RemeshTopology.Build(mesh); }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void EveryColourClassIsIndependent()
        {
            var topology = Build(MeshCorpus.Icosphere(4, 1f));
            var colouring = topology.colouring;

            // Reverse index: vertex -> its class.
            var classOf = new int[topology.vertexCount];
            for (var c = 0; c < colouring.classCount; c++)
                for (var s = colouring.start[c]; s < colouring.start[c + 1]; s++)
                    classOf[colouring.vertices[s]] = c;

            for (var v = 0; v < topology.vertexCount; v++)
            {
                for (var s = topology.adjacencyStart[v]; s < topology.adjacencyStart[v + 1]; s++)
                {
                    var j = topology.adjacency[s];
                    Assert.AreNotEqual(classOf[v], classOf[j],
                        $"vertices {v} and {j} are adjacent and share colour {classOf[v]}; " +
                        "a parallel sweep over that class would read a value mid-update");
                }
            }
        }

        [Test]
        public void EveryVertexIsColouredExactlyOnce()
        {
            var topology = Build(MeshCorpus.Icosphere(3, 1f));
            var colouring = topology.colouring;

            var seen = new int[topology.vertexCount];
            for (var s = 0; s < colouring.vertices.Length; s++)
                seen[colouring.vertices[s]]++;

            Assert.AreEqual(topology.vertexCount, colouring.vertices.Length,
                "the partition must cover every vertex and nothing else");

            for (var v = 0; v < topology.vertexCount; v++)
                Assert.AreEqual(1, seen[v], $"vertex {v} appears {seen[v]} times in the partition");

            Assert.AreEqual(topology.vertexCount, colouring.start[colouring.classCount],
                "the CSR end offset must be the vertex count");
        }

        [Test]
        public void ABoundaryMeshIsColouredToo()
        {
            // An open disc has boundary vertices with shorter rings. Nothing about the colouring
            // depends on closedness, but this is the mesh class the project has repeatedly
            // forgotten to exercise.
            var topology = Build(MeshCorpus.OpenDisc(1f, 48, 10));
            var colouring = topology.colouring;

            Assert.Greater(colouring.classCount, 1, "a triangulated surface needs at least 2 colours");
            Assert.AreEqual(topology.vertexCount, colouring.vertices.Length);
        }

        [Test]
        public void ParallelSweepsAreDeterministic()
        {
            // The reason residuals go into per-vertex arrays and are summed afterwards, rather than
            // into a shared accumulator: an interlocked float add makes the total depend on the
            // order the pool happened to finish in, and the regression gate would read that as the
            // solver having changed.
            var mesh = MeshCorpus.Icosphere(5, 1f);
            RemeshTopology topology;
            try { topology = RemeshTopology.Build(mesh); }
            finally { Object.DestroyImmediate(mesh); }

            Assert.Greater(topology.vertexCount, 2048,
                "this mesh must be over the parallel threshold or the test proves nothing");

            var first = OrientationField.Solve(topology, 12, null);
            var second = OrientationField.Solve(topology, 12, null);

            Assert.AreEqual(first.iterationsRun, second.iterationsRun);
            Assert.AreEqual(first.lastResidual, second.lastResidual,
                "two runs of the same input produced different residuals");

            for (var i = 0; i < topology.vertexCount; i++)
                Assert.AreEqual(first.directions[i], second.directions[i],
                    $"orientation at vertex {i} differs between two runs of the same input");

            var scale = topology.EdgeLengthForQuadCount(500);
            var firstPos = PositionField.Solve(topology, first, scale, 12);
            var secondPos = PositionField.Solve(topology, second, scale, 12);

            for (var i = 0; i < topology.vertexCount; i++)
                Assert.AreEqual(firstPos.positions[i], secondPos.positions[i],
                    $"lattice position at vertex {i} differs between two runs of the same input");
        }
    }
}
