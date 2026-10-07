// The pass that stops throwing connectivity away, and the three things it must not get wrong.
//
// The retriangulation is the risky part, not the measurement. A split edge belongs to two triangles,
// so a triangle can arrive with one, two or three of its edges split, and each case has to emit a
// different fan while preserving winding. A winding error there is invisible in the face count and
// in the quad fraction, and shows up only as a backface-culled hole much later — the exact defect
// class this project has repeatedly failed to catch by eye. So it is pinned combinatorially: every
// edge of every emitted triangle must still be traversed in opposite directions by its two owners.
//
// The second risk is over-eagerness. The (1,1) diagonal is a correct state on a triangulated input
// and must never be split; diagonals outnumber genuine jumps five to one on the reference scan, so
// conflating them would subdivide five times more than necessary for no gain.

using System.Collections.Generic;
using NativeRemesher.Benchmark;
using NativeRemesher.Extraction;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class EdgeSubdivideTests
    {
        static RemeshTopology BuildTopology(Mesh mesh)
        {
            try
            {
                RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);
                return RemeshTopology.Build(positions, triangles);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>Solves both fields at <paramref name="scale"/> on an icosphere.</summary>
        static void Solve(float scale, out RemeshTopology topology,
            out OrientationField orientation, out PositionField position)
        {
            topology = BuildTopology(MeshCorpus.Icosphere(3, 1f));
            orientation = OrientationField.Solve(topology, 30, null, 12345);
            position = PositionField.Solve(topology, orientation, scale, 30);
        }

        /// <summary>
        /// The CONVERGING fixture: a capped cylinder at the spacing its own target implies.
        /// </summary>
        /// <remarks>
        /// The choice of fixture is load-bearing, and the first version of these tests got it
        /// wrong. A midpoint split does not merely halve the problem — re-triangulating a split
        /// triangle creates a new edge from the midpoint to the opposite vertex, whose lattice
        /// difference is unconstrained — so on some inputs the refinement cascades and the pass
        /// abandons rather than shipping half of one. `cylinder_capped` is the measured converging
        /// case (192 splits, one round) and is what the postcondition tests must use;
        /// `cube_subdiv` and a coarse icosphere at a fine lattice are the measured diverging cases,
        /// covered separately by <see cref="ADivergingRefinementIsAbandonedAndTheInputIsReturned"/>.
        /// </remarks>
        static void SolveConverging(out RemeshTopology topology,
            out OrientationField orientation, out PositionField position)
        {
            topology = BuildTopology(MeshCorpus.CappedCylinder(0.5f, 2f, 96, 24));
            orientation = OrientationField.Solve(topology, 60, null, 12345);
            position = PositionField.Solve(
                topology, orientation, topology.EdgeLengthForQuadCount(2000), 60);
        }

        /// <summary>
        /// Two faces sharing an edge are consistently wound if and only if they traverse it in
        /// opposite directions, so inserting every DIRECTED edge into a set must produce no
        /// collision. This is the check that a retriangulation fan is right.
        /// </summary>
        static void AssertConsistentlyWound(int[] triangles, string what)
        {
            var directed = new HashSet<long>();
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            for (var e = 0; e < 3; e++)
            {
                var a = triangles[t + e];
                var b = triangles[t + (e + 1) % 3];
                Assert.IsTrue(directed.Add(((long)a << 32) | (uint)b),
                    $"{what}: directed edge {a}->{b} appears twice, so winding is inconsistent");
            }
        }

        [Test]
        public void NothingToSplitReturnsTheSameObjectsUntouched()
        {
            // A lattice far coarser than the input edges: every edge lands in the same cell or one
            // step away, so the pass must be a measurement and nothing more.
            Solve(1.0f, out var topology, out var orientation, out var position);

            EdgeSubdivide.Run(topology, orientation, position,
                out var refinedTopology, out var refinedOrientation, out var refinedPosition,
                out var stats);

            Assert.AreEqual(0, stats.edgesSplit);
            Assert.AreSame(topology, refinedTopology, "no allocation when there is nothing to do");
            Assert.AreSame(orientation, refinedOrientation);
            Assert.AreSame(position, refinedPosition);
            Assert.AreEqual(stats.inputVertices, stats.outputVertices);
        }

        /// <summary>
        /// The pass's whole purpose: after it runs, pass A must have nothing left to discard for
        /// being two or more cells apart. Asked at a lattice fine enough to guarantee jumps exist.
        /// </summary>
        [Test]
        public void EveryEdgeSpanningTwoCellsIsResolved()
        {
            // Finer than the input edge length, which is what forces jumps: neighbouring vertices
            // land two or more cells apart because the cells between them hold no vertex.
            SolveConverging(out var topology, out var orientation, out var position);

            var before = CountJumps(topology, orientation, position);
            Assert.Greater(before, 0, "fixture must actually contain unrepresentable edges");

            EdgeSubdivide.Run(topology, orientation, position,
                out var refinedTopology, out var refinedOrientation, out var refinedPosition,
                out var stats);

            Assert.Greater(stats.edgesSplit, 0);
            Assert.AreEqual(0, stats.unresolvedEdges,
                "the round cap bound the result instead of convergence");

            var after = CountJumps(refinedTopology, refinedOrientation, refinedPosition);
            Assert.Less(after, before, "subdivision must reduce the jump count");
            Assert.AreEqual(0, after, "no edge may still span two or more cells");
        }

        /// <summary>
        /// The guard, and the reason it exists. Half a subdivision is worse than none: it pays the
        /// vertex cost, adds the new unconstrained midpoint-to-opposite-vertex edges, and does not
        /// remove the discards it was meant to remove. Measured on `cube_subdiv`, shipping a
        /// partial refinement took irregular vertices from 148 to 488 and quad fraction from 0.99
        /// to 0.80. So a diverging refinement must return the input untouched, not its own progress.
        /// </summary>
        [Test]
        public void ADivergingRefinementIsAbandonedAndTheInputIsReturned()
        {
            // A coarse mesh against a lattice far finer than its edges: nearly every edge spans
            // several cells, and each split creates more of them than it resolves.
            Solve(0.02f, out var topology, out var orientation, out var position);

            EdgeSubdivide.Run(topology, orientation, position,
                out var refinedTopology, out var refinedOrientation, out var refinedPosition,
                out var stats);

            Assert.IsNotNull(stats.abandoned, "this fixture must trip the divergence guard");
            Assert.AreSame(topology, refinedTopology,
                "an abandoned refinement must hand back the original, not a partial one");
            Assert.AreSame(orientation, refinedOrientation);
            Assert.AreSame(position, refinedPosition);
            Assert.AreEqual(stats.inputVertices, stats.outputVertices,
                "no vertices may survive an abandoned refinement");
            Assert.AreEqual(topology.triangleCount, stats.outputTriangles);
        }

        /// <summary>
        /// A feature graph is indexed by vertex, so it has to grow with the mesh or extraction reads
        /// past the end of it. That is not hypothetical: it threw on three of four audit cases.
        /// </summary>
        [Test]
        public void FeatureGraphGrowsWithTheMeshAndKeepsCreasesOnTheCrease()
        {
            SolveConverging(out var topology, out var orientation, out var position);
            var features = FeatureGraph.Detect(topology, 30f);

            Assert.Greater(features.featureEdgeCount, 0,
                "a capped cylinder has two circular creases");

            EdgeSubdivide.Run(topology, orientation, position, features,
                out var refined, out _, out _, out var refinedFeatures, out var stats);

            Assert.Greater(stats.edgesSplit, 0);
            Assert.AreEqual(refined.vertexCount, refinedFeatures.isFeatureVertex.Length,
                "every refined vertex must be addressable in the feature graph");
            Assert.AreEqual(refined.vertexCount, refinedFeatures.featureDegree.Length);
            Assert.IsFalse(refinedFeatures.polylinesCarried,
                "polylines are not rebuilt, and that has to be discoverable");

            // A split crease is still a crease: the feature edge count can only grow, since each
            // split feature edge becomes two and nothing is removed.
            Assert.GreaterOrEqual(refinedFeatures.featureEdgeCount, features.featureEdgeCount);

            // And every feature edge must join two vertices both marked as on the feature.
            for (var i = 0; i < refined.vertexCount; i++)
            for (var k = refined.adjacencyStart[i]; k < refined.adjacencyStart[i + 1]; k++)
            {
                var j = refined.adjacency[k];
                if (!refinedFeatures.IsFeatureEdge(i, j)) continue;
                Assert.IsTrue(refinedFeatures.isFeatureVertex[i], $"vertex {i} on a feature edge");
                Assert.IsTrue(refinedFeatures.isFeatureVertex[j], $"vertex {j} on a feature edge");
            }
        }

        static int CountJumps(RemeshTopology topology, OrientationField orientation,
            PositionField position)
        {
            var scale = position.scale;
            var invScale = 1f / scale;
            var p = topology.positions;
            var n = topology.normals;
            var q = orientation.directions;
            var o = position.positions;

            var jumps = 0;
            for (var i = 0; i < topology.vertexCount; i++)
            for (var k = topology.adjacencyStart[i]; k < topology.adjacencyStart[i + 1]; k++)
            {
                var j = topology.adjacency[k];
                if (j <= i) continue;

                OrientationField.Compat4(q[i], n[i], q[j], n[j], out var qi, out var qj);
                PositionField.CompatIndex(p[i], n[i], qi, o[i], p[j], n[j], qj, o[j],
                    scale, invScale, out var s0, out var s1, out _);

                if (Mathf.Max(Mathf.Abs(s0.x - s1.x), Mathf.Abs(s0.y - s1.y)) >= 2) jumps++;
            }
            return jumps;
        }

        [Test]
        public void RefinedMeshStaysManifoldAndConsistentlyWound()
        {
            SolveConverging(out var topology, out var orientation, out var position);

            AssertConsistentlyWound(topology.triangles, "input");

            EdgeSubdivide.Run(topology, orientation, position,
                out var refined, out _, out _, out var stats);

            Assert.Greater(stats.edgesSplit, 0);
            AssertConsistentlyWound(refined.triangles, "refined");

            // A closed input stays closed: every edge still has exactly two owners.
            var incidence = new Dictionary<long, int>();
            for (var t = 0; t + 2 < refined.triangles.Length; t += 3)
            for (var e = 0; e < 3; e++)
            {
                var a = refined.triangles[t + e];
                var b = refined.triangles[t + (e + 1) % 3];
                var key = EdgeKey.PackUndirected(a, b);
                incidence.TryGetValue(key, out var c);
                incidence[key] = c + 1;
            }

            foreach (var pair in incidence)
                Assert.AreEqual(2, pair.Value,
                    "a closed input must stay closed — an edge with one or three owners is a tear");
        }

        /// <summary>
        /// Euler's characteristic is the cheapest global statement that the refinement did not
        /// change the surface. A midpoint split on a closed genus-0 mesh must keep V - E + F = 2.
        /// </summary>
        [Test]
        public void RefinementPreservesEulerCharacteristic()
        {
            SolveConverging(out var topology, out var orientation, out var position);

            EdgeSubdivide.Run(topology, orientation, position,
                out var refined, out _, out _, out var stats);

            Assert.Greater(stats.edgesSplit, 0);

            var edges = new HashSet<long>();
            for (var t = 0; t + 2 < refined.triangles.Length; t += 3)
            for (var e = 0; e < 3; e++)
                edges.Add(EdgeKey.PackUndirected(refined.triangles[t + e],
                    refined.triangles[t + (e + 1) % 3]));

            var euler = refined.vertexCount - edges.Count + refined.triangleCount;
            Assert.AreEqual(2, euler, "a refined sphere is still a sphere");
        }

        /// <summary>
        /// Diagonals must survive. On a triangulated input a cell covered by two triangles has
        /// their shared edge as its diagonal, so (1,1) is a correct state and splitting it would
        /// subdivide five times more than necessary — measured 8.2 % diagonal against 1.6 % jump.
        /// </summary>
        [Test]
        public void DiagonalEdgesAreNotSplit()
        {
            // A flat quad's two triangles: the shared edge (0,2) is the cell diagonal when the
            // lattice is aligned to the axes with spacing 1.
            var positions = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f), new Vector3(0f, 1f, 0f),
            };
            var triangles = new[] { 0, 1, 2, 0, 2, 3 };
            var topology = RemeshTopology.Build(positions, triangles);

            var directions = new Vector3[topology.vertexCount];
            var lattice = new Vector3[topology.vertexCount];
            for (var v = 0; v < topology.vertexCount; v++)
            {
                directions[v] = Vector3.right;
                // Each corner is its own lattice point, so the four corners sit in four cells and
                // the (0,2) pair is exactly one step on BOTH axes — the diagonal.
                lattice[v] = topology.positions[v];
            }

            var orientation = OrientationField.Solve(topology, 0, null, -1, null, null, directions);
            var position = PositionField.Solve(topology, orientation, 1f, 0, null, lattice);

            EdgeSubdivide.Run(topology, orientation, position,
                out var refined, out _, out _, out var stats);

            Assert.AreEqual(0, stats.edgesSplit,
                "the diagonal is representable and must be left alone");
            Assert.AreSame(topology, refined);
        }

        /// <summary>
        /// Every inserted vertex must carry a unit cross lying in its own tangent plane. A cross
        /// that is not tangent makes Rotate90 return something not perpendicular to the normal,
        /// which corrupts every lattice index computed from it — silently.
        /// </summary>
        [Test]
        public void InsertedVerticesCarryUnitTangentCrosses()
        {
            SolveConverging(out var topology, out var orientation, out var position);

            EdgeSubdivide.Run(topology, orientation, position,
                out var refined, out var refinedOrientation, out _, out var stats);

            Assert.Greater(stats.verticesInserted, 0);

            for (var v = 0; v < refined.vertexCount; v++)
            {
                var q = refinedOrientation.directions[v];
                var n = refined.normals[v];

                Assert.AreEqual(1f, q.magnitude, 1e-3f, $"vertex {v} cross is not unit");
                Assert.AreEqual(0f, Vector3.Dot(q, n), 1e-3f, $"vertex {v} cross is not tangent");
            }
        }
    }
}
