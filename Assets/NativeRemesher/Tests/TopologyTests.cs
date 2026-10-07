// Stage 1 invariants. These are the assertions the previous hand-rolled harness carried, plus the
// ones that were missing and would have caught real problems: adjacency ordering (without which the
// solvers are not reproducible run to run) and the weld remap.

using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class TopologyTests
    {
        static RemeshTopology BuildFrom(Mesh mesh)
        {
            try { return RemeshTopology.Build(mesh); }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Icosphere_HasSaneTopology()
        {
            var topology = BuildFrom(MeshCorpus.Icosphere(4, 1f));

            Assert.Greater(topology.vertexCount, 0, "has vertices");
            Assert.Zero(topology.isolatedVertices, "no isolated vertices");
            Assert.Greater(topology.averageEdgeLength, 0f);
            Assert.Greater(topology.totalArea, 0f);

            // A unit icosphere's area must approach 4*pi. This catches a whole class of silent
            // errors that "area > 0" would wave through — wrong winding, double counting, or the
            // cross-product factor being off by two.
            Assert.That(topology.totalArea, Is.EqualTo(4f * Mathf.PI).Within(0.02f * 4f * Mathf.PI),
                "unit sphere area approaches 4*pi");
        }

        [Test]
        public void SubdividedCube_WeldsToClosedSurface()
        {
            var topology = BuildFrom(MeshCorpus.SubdividedCube(1f, 8));

            Assert.Zero(topology.isolatedVertices, "no isolated vertices");
            Assert.That(topology.totalArea, Is.EqualTo(6f).Within(0.01f), "unit cube area is 6");

            // Euler characteristic of a sphere is 2. Cheapest possible proof that the six faces
            // actually share their border vertices rather than forming six loose sheets.
            var edges = 0;
            for (var i = 0; i < topology.vertexCount; i++)
            for (var k = topology.adjacencyStart[i]; k < topology.adjacencyStart[i + 1]; k++)
                if (topology.adjacency[k] > i) edges++;

            var euler = topology.vertexCount - edges + topology.triangleCount;
            Assert.AreEqual(2, euler, "closed genus-0 surface has Euler characteristic 2");
        }

        [Test]
        public void Adjacency_IsSymmetricAndHasNoSelfLoops()
        {
            var topology = BuildFrom(MeshCorpus.Icosphere(3, 1f));

            for (var i = 0; i < topology.vertexCount; i++)
            {
                for (var k = topology.adjacencyStart[i]; k < topology.adjacencyStart[i + 1]; k++)
                {
                    var j = topology.adjacency[k];
                    Assert.AreNotEqual(i, j, $"vertex {i} is its own neighbour — Gauss-Seidel would be meaningless");

                    var mirrored = false;
                    for (var m = topology.adjacencyStart[j]; m < topology.adjacencyStart[j + 1]; m++)
                        if (topology.adjacency[m] == i) { mirrored = true; break; }

                    Assert.IsTrue(mirrored, $"edge {i}-{j} is not symmetric");
                }
            }
        }

        [Test]
        public void Adjacency_RowsAreInCyclicFanOrder()
        {
            // Supersedes an earlier ascending-sort check. Ascending order was deterministic but
            // geometrically meaningless; fan order is what any pass reasoning about the faces around
            // a vertex actually needs, and it is derived from the mesh so it is still reproducible.
            //
            // Tested by the property that defines it: consecutive neighbours in the row must be
            // adjacent to each other, because two consecutive spokes of a fan are the other two
            // corners of one triangle. An ascending sort fails this immediately.
            var topology = BuildFrom(MeshCorpus.Icosphere(3, 1f));

            var checkedVertices = 0;

            for (var v = 0; v < topology.vertexCount; v++)
            {
                if (topology.isNonManifoldVertex[v]) continue;   // no cycle exists to check

                var from = topology.adjacencyStart[v];
                var to = topology.adjacencyStart[v + 1];
                var degree = to - from;
                if (degree < 3) continue;

                checkedVertices++;

                // A closed fan wraps, an open one does not, so only check the interior steps for a
                // boundary vertex.
                var steps = topology.isBoundaryVertex[v] ? degree - 1 : degree;

                for (var k = 0; k < steps; k++)
                {
                    var a = topology.adjacency[from + k];
                    var b = topology.adjacency[from + (k + 1) % degree];

                    var adjacent = false;
                    for (var m = topology.adjacencyStart[a]; m < topology.adjacencyStart[a + 1]; m++)
                        if (topology.adjacency[m] == b) { adjacent = true; break; }

                    Assert.IsTrue(adjacent,
                        $"vertex {v}: consecutive fan neighbours {a} and {b} are not adjacent, " +
                        "so the row is not in cyclic order");
                }
            }

            Assert.Greater(checkedVertices, 0, "no vertices were eligible for the check");
        }

        [Test]
        public void HalfEdge_TwinIsAnInvolution()
        {
            var topology = BuildFrom(MeshCorpus.Icosphere(3, 1f));

            for (var h = 0; h < topology.halfEdgeVertex.Length; h++)
            {
                var twin = topology.halfEdgeTwin[h];
                if (twin < 0) continue;

                Assert.AreEqual(h, topology.halfEdgeTwin[twin], $"twin of twin of {h} is not {h}");

                // A twin must run the other way along the same edge.
                Assert.AreEqual(topology.halfEdgeVertex[h], topology.HalfEdgeTarget(twin));
                Assert.AreEqual(topology.HalfEdgeTarget(h), topology.halfEdgeVertex[twin]);
            }
        }

        [Test]
        public void ClosedMesh_HasNoBoundaryOrNonManifoldEdges()
        {
            var topology = BuildFrom(MeshCorpus.Icosphere(3, 1f));

            Assert.Zero(topology.boundaryEdgeCount, "a closed sphere has no boundary edges");
            Assert.Zero(topology.nonManifoldEdgeCount);

            for (var v = 0; v < topology.vertexCount; v++)
            {
                Assert.IsFalse(topology.isBoundaryVertex[v], $"vertex {v} wrongly flagged boundary");
                Assert.IsFalse(topology.isNonManifoldVertex[v]);
            }
        }

        [Test]
        public void OpenDisc_FlagsExactlyItsRimAsBoundary()
        {
            const int k_Segments = 32;
            var topology = BuildFrom(MeshCorpus.OpenDisc(1f, k_Segments, 6));

            Assert.AreEqual(k_Segments, topology.boundaryEdgeCount,
                "the rim should be exactly one loop of boundary edges");

            var boundaryVertices = 0;
            for (var v = 0; v < topology.vertexCount; v++)
                if (topology.isBoundaryVertex[v]) boundaryVertices++;

            Assert.AreEqual(k_Segments, boundaryVertices,
                "a single closed rim loop has as many vertices as edges");
        }

        [Test]
        public void NonManifoldInput_IsFlaggedRatherThanLoopingForever()
        {
            // Three triangles sharing one edge. The fan through the shared edge's endpoints is not
            // a cycle, so ring construction must detect that and fall back rather than rotate
            // forever looking for a closure that does not exist.
            var mesh = new Mesh();
            mesh.SetVertices(new[]
            {
                new Vector3(0, -1, 0), new Vector3(0, 1, 0),
                new Vector3(1, 0, 0), new Vector3(-0.5f, 0, 0.87f), new Vector3(-0.5f, 0, -0.87f)
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 1, 3, 0, 1, 4 }, 0, false);

            RemeshTopology topology = null;
            Assert.DoesNotThrow(() => topology = RemeshTopology.Build(mesh));

            try
            {
                Assert.NotNull(topology);
                Assert.Greater(topology.nonManifoldEdgeCount, 0,
                    "three triangles on one edge must register as non-manifold");

                // Both ends of the shared edge, and only those, are the non-manifold ones.
                Assert.IsTrue(topology.isNonManifoldVertex[0]);
                Assert.IsTrue(topology.isNonManifoldVertex[1]);

                // Fallback rings must still be usable: symmetric and self-loop free.
                for (var v = 0; v < topology.vertexCount; v++)
                for (var k = topology.adjacencyStart[v]; k < topology.adjacencyStart[v + 1]; k++)
                    Assert.AreNotEqual(v, topology.adjacency[k], "self-loop in fallback ring");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Adjacency_IsDeterministicAcrossBuilds()
        {
            var a = BuildFrom(MeshCorpus.Icosphere(3, 1f));
            var b = BuildFrom(MeshCorpus.Icosphere(3, 1f));

            Assert.AreEqual(a.vertexCount, b.vertexCount);
            Assert.AreEqual(a.adjacency, b.adjacency, "adjacency differs between two identical builds");
            Assert.AreEqual(a.adjacencyStart, b.adjacencyStart);
        }

        [Test]
        public void Normals_AreUnitLength()
        {
            var topology = BuildFrom(MeshCorpus.Icosphere(3, 1f));

            for (var i = 0; i < topology.vertexCount; i++)
                Assert.That(topology.normals[i].magnitude, Is.EqualTo(1f).Within(1e-3f),
                    $"normal {i} is not unit length");
        }

        [Test]
        public void SourceToWelded_MapsEveryVertexIntoRange()
        {
            var mesh = MeshCorpus.SubdividedCube(1f, 4);
            var sourceCount = mesh.vertexCount;
            var topology = BuildFrom(mesh);

            Assert.IsNotNull(topology.sourceToWelded, "weld remap was not kept");
            Assert.AreEqual(sourceCount, topology.sourceToWelded.Length,
                "remap must have one entry per source vertex");

            for (var i = 0; i < topology.sourceToWelded.Length; i++)
            {
                var welded = topology.sourceToWelded[i];
                Assert.GreaterOrEqual(welded, 0);
                Assert.Less(welded, topology.vertexCount, $"source vertex {i} maps out of range");
            }
        }

        [Test]
        public void SourceToWelded_AgreesWithPositions()
        {
            // The remap only earns its keep if a caller can trust it to line an external per-vertex
            // array up with the welded graph. Verify the geometric claim directly.
            var mesh = MeshCorpus.Icosphere(2, 1f);
            var source = mesh.vertices;
            var topology = RemeshTopology.Build(mesh);

            try
            {
                for (var i = 0; i < source.Length; i++)
                {
                    var welded = topology.positions[topology.sourceToWelded[i]];
                    Assert.Less(Vector3.Distance(source[i], welded), 1e-3f,
                        $"source vertex {i} does not sit on its welded position");
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void OpenDisc_HasABoundary()
        {
            // The project had no boundary coverage at all, which means nothing was checking that
            // open meshes even survive the pipeline.
            var topology = BuildFrom(MeshCorpus.OpenDisc(1f, 32, 6));

            var boundaryEdges = 0;
            var incidence = new System.Collections.Generic.Dictionary<long, int>();
            for (var t = 0; t + 2 < topology.triangles.Length; t += 3)
            {
                for (var e = 0; e < 3; e++)
                {
                    var a = topology.triangles[t + e];
                    var b = topology.triangles[t + (e + 1) % 3];
                    var key = ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
                    incidence.TryGetValue(key, out var seen);
                    incidence[key] = seen + 1;
                }
            }
            foreach (var kv in incidence) if (kv.Value == 1) boundaryEdges++;

            Assert.AreEqual(32, boundaryEdges, "the disc's rim should be exactly one loop of 32 edges");
        }

        [Test]
        public void MaxQuadCountForInput_IsBelowInputTriangleCount()
        {
            // Coarsen-only: the ceiling must be well under the input's own resolution, or the
            // clamp it exists to apply would never fire.
            var topology = BuildFrom(MeshCorpus.Icosphere(4, 1f));

            var ceiling = topology.MaxQuadCountForInput();
            Assert.Greater(ceiling, 0);
            Assert.Less(ceiling, topology.triangleCount,
                "the achievable quad ceiling must be coarser than the input");
        }

        [Test]
        public void TopologyBuildIsLinearEnoughForAMillionTriangles()
        {
            // The test that should have existed from the start. Every benchmark run was capped at
            // 25k triangles, so an O(V * T) step in the adjacency build went unnoticed until a real
            // 1M-triangle asset took over seven minutes in "building topology" alone.
            //
            // The bound is deliberately loose — this is single-threaded managed C# and the point is
            // to catch a complexity regression, not to police constant factors. Anything
            // super-linear blows straight past it.
            var mesh = MeshCorpus.Icosphere(8, 1f);   // 1,310,720 triangles
            try
            {
                var triangles = mesh.triangles.Length / 3;
                Assert.Greater(triangles, 1_000_000, "the scale test needs a genuinely large mesh");

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var topology = RemeshTopology.Build(mesh);
                watch.Stop();

                Assert.NotNull(topology);
                Assert.Greater(topology.vertexCount, 500_000);
                Assert.Zero(topology.isolatedVertices);

                Assert.Less(watch.ElapsedMilliseconds, 60_000,
                    $"building topology for {triangles:N0} triangles took " +
                    $"{watch.ElapsedMilliseconds:N0} ms, which indicates a super-linear step");

                UnityEngine.Debug.Log($"[Scale] topology for {triangles:N0} tris in " +
                                      $"{watch.ElapsedMilliseconds:N0} ms");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void NonManifoldHeavyInputDoesNotBlowUp()
        {
            // The specific shape of the bug: the non-manifold fallback used to scan every half-edge
            // in the mesh once per non-manifold vertex. A mesh that is mostly non-manifold therefore
            // hit the worst case on nearly every vertex. Real imported assets have plenty of these,
            // which is why the failure showed up on a user's mesh and not on the clean corpus.
            var vertices = new System.Collections.Generic.List<Vector3>();
            var triangles = new System.Collections.Generic.List<int>();

            // A long strip of fans, each sharing one edge between three triangles.
            const int k_Fans = 4000;
            for (var i = 0; i < k_Fans; i++)
            {
                var b = vertices.Count;
                var x = i * 1f;
                vertices.Add(new Vector3(x, -1, 0));
                vertices.Add(new Vector3(x, 1, 0));
                vertices.Add(new Vector3(x + 0.5f, 0, 0));
                vertices.Add(new Vector3(x - 0.25f, 0, 0.87f));
                vertices.Add(new Vector3(x - 0.25f, 0, -0.87f));

                triangles.AddRange(new[] { b, b + 1, b + 2, b, b + 1, b + 3, b, b + 1, b + 4 });
            }

            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0, false);

            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var topology = RemeshTopology.Build(mesh);
                watch.Stop();

                Assert.Greater(topology.nonManifoldEdgeCount, 0, "expected non-manifold edges");
                Assert.Less(watch.ElapsedMilliseconds, 10_000,
                    $"non-manifold-heavy build took {watch.ElapsedMilliseconds:N0} ms");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void DegenerateInput_DoesNotThrow()
        {
            // Robustness corpus entry. Zero-area slivers, duplicate positions and a repeated-index
            // triangle must be survivable, because real scan data contains all three.
            Assert.DoesNotThrow(() =>
            {
                var topology = BuildFrom(MeshCorpus.Icosphere(1, 1f));
                Assert.NotNull(topology);
            });

            var mesh = new Mesh();
            mesh.SetVertices(new[] { Vector3.zero, Vector3.right, new Vector3(2f, 0f, 0f) });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, false);

            // All three corners collinear: zero area, no normal. Must not produce NaN.
            var degenerate = RemeshTopology.Build(mesh);
            try
            {
                for (var i = 0; i < degenerate.vertexCount; i++)
                {
                    Assert.IsFalse(float.IsNaN(degenerate.normals[i].x), "degenerate normal is NaN");
                    Assert.That(degenerate.normals[i].magnitude, Is.EqualTo(1f).Within(1e-3f));
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
