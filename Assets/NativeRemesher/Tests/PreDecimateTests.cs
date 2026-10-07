// QEM decimation. The tests that matter here are the validity ones: a decimator that hits its
// triangle budget while quietly making the mesh non-manifold, folding it through itself, or eating
// the silhouette is worse than useless, because the remesher downstream then gets blamed.

using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class PreDecimateTests
    {
        static Mesh Decimate(Mesh source, int targetTriangles, out DecimateStats stats)
        {
            try
            {
                return PreDecimate.Decimate(source, DecimateOptions.ForTriangleCount(targetTriangles),
                    out stats);
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
        }

        [Test]
        public void ReachesTheTriangleBudget()
        {
            var result = Decimate(MeshCorpus.Icosphere(4, 1f), 800, out var stats);

            try
            {
                Assert.LessOrEqual(stats.outputTriangles, 900,
                    $"did not reach the budget: {stats}");
                Assert.Greater(stats.outputTriangles, 0);
                Assert.Greater(stats.collapsesApplied, 0);
            }
            finally { UnityEngine.Object.DestroyImmediate(result); }
        }

        [Test]
        public void OutputStaysManifoldAndClosed()
        {
            var result = Decimate(MeshCorpus.Icosphere(4, 1f), 600, out _);

            try
            {
                var metrics = MeshMetrics.Measure(PolyMesh.FromMesh(result), null, geometrySamples: 0);

                Assert.Zero(metrics.nonManifoldEdges, "decimation made the mesh non-manifold");
                Assert.Zero(metrics.boundaryEdges, "decimation punched a hole in a closed mesh");
                Assert.Zero(metrics.degenerateFaces);
                Assert.Zero(metrics.duplicateFaces);

                // A closed genus-0 surface must stay one, whatever the triangle count.
                Assert.AreEqual(2, metrics.eulerCharacteristic,
                    "decimation changed the topology of a sphere");
            }
            finally { UnityEngine.Object.DestroyImmediate(result); }
        }

        [Test]
        public void PreservesShapeWithinTolerance()
        {
            var source = MeshCorpus.Icosphere(4, 1f);
            var reference = PolyMesh.FromMesh(source);

            // Keep a copy, because Decimate disposes the source it is handed.
            var referenceCopy = new PolyMesh
            {
                positions = (Vector3[])reference.positions.Clone(),
                faces = reference.faces
            };

            var result = Decimate(source, 600, out _);

            try
            {
                var metrics = MeshMetrics.Measure(PolyMesh.FromMesh(result), referenceCopy, 20000);

                // Normalised by the bounding-box diagonal, so this is a fraction of overall size.
                Assert.Less(metrics.chamferMean, 0.005f,
                    $"mean surface deviation {metrics.chamferMean:F5} is too large");
                Assert.Less(metrics.hausdorffMax, 0.05f,
                    $"worst-case deviation {metrics.hausdorffMax:F5} is too large");
                Assert.Greater(metrics.normalConsistency, 0.95f);
            }
            finally { UnityEngine.Object.DestroyImmediate(result); }
        }

        [Test]
        public void PreservesAnOpenBoundary()
        {
            // The reason boundary constraint planes exist. An open edge has fewer planes holding it
            // in place than an interior one, so without them the quadric eats the rim first —
            // precisely where the silhouette is.
            const int k_Segments = 48;
            var result = Decimate(MeshCorpus.OpenDisc(1f, k_Segments, 10), 200, out _);

            try
            {
                var metrics = MeshMetrics.Measure(PolyMesh.FromMesh(result), null, geometrySamples: 0);

                Assert.Zero(metrics.nonManifoldEdges);
                Assert.Greater(metrics.boundaryEdges, 0, "the disc lost its boundary entirely");

                // The rim may legitimately simplify, but it must remain a recognisable loop rather
                // than being consumed down to a few edges.
                Assert.Greater(metrics.boundaryEdges, k_Segments / 3,
                    $"the rim was eaten down to {metrics.boundaryEdges} edges from {k_Segments}");
            }
            finally { UnityEngine.Object.DestroyImmediate(result); }
        }

        [Test]
        public void PreservesCubeCorners()
        {
            // A cube is the strongest shape test available: its eight corners are the entire form,
            // and any decimator that rounds them off will show up here immediately even though the
            // mean surface error stays small.
            var result = Decimate(MeshCorpus.SubdividedCube(1f, 12), 100, out _);

            try
            {
                var positions = result.vertices;
                var corners = 0;

                foreach (var p in positions)
                {
                    if (Mathf.Abs(Mathf.Abs(p.x) - 0.5f) < 0.02f &&
                        Mathf.Abs(Mathf.Abs(p.y) - 0.5f) < 0.02f &&
                        Mathf.Abs(Mathf.Abs(p.z) - 0.5f) < 0.02f)
                        corners++;
                }

                Assert.AreEqual(8, corners, "a decimated cube must still have its eight corners");
            }
            finally { UnityEngine.Object.DestroyImmediate(result); }
        }

        [Test]
        public void CubeDecimationIsNearlyLossless()
        {
            // Isolates the decimator from the remesher on the mesh where surface deviation is worst.
            // A cube's faces are planar, so collapsing edges within a face is exactly free and the
            // only real cost is at the creases — a decimated cube should be nearly indistinguishable
            // from the original at any sane budget. If this passes, a large deviation in the full
            // pipeline is the remesher's, not the decimator's, and the search goes elsewhere.
            var source = MeshCorpus.SubdividedCube(1f, 24);
            var reference = PolyMesh.FromMesh(source);
            var referenceCopy = new PolyMesh
            {
                positions = (Vector3[])reference.positions.Clone(),
                faces = reference.faces
            };

            var result = Decimate(source, 3000, out var stats);

            try
            {
                var metrics = MeshMetrics.Measure(PolyMesh.FromMesh(result), referenceCopy, 20000);

                Assert.Less(metrics.chamferMean, 0.002f,
                    $"decimating a cube should be nearly lossless; mean deviation " +
                    $"{metrics.chamferMean:F5} ({stats})");
                Assert.Less(metrics.hausdorffMax, 0.02f,
                    $"worst-case deviation {metrics.hausdorffMax:F5} is too large for a cube");
            }
            finally { UnityEngine.Object.DestroyImmediate(result); }
        }

        [Test]
        public void BudgetAboveInputIsANoOp()
        {
            var result = Decimate(MeshCorpus.Icosphere(2, 1f), 100000, out var stats);

            try
            {
                Assert.AreEqual(stats.inputTriangles, stats.outputTriangles);
                Assert.Zero(stats.collapsesApplied);
            }
            finally { UnityEngine.Object.DestroyImmediate(result); }
        }

        [Test]
        public void SurvivesDegenerateAndNonManifoldInput()
        {
            var nonManifold = new Mesh();
            nonManifold.SetVertices(new[]
            {
                new Vector3(0, -1, 0), new Vector3(0, 1, 0),
                new Vector3(1, 0, 0), new Vector3(-0.5f, 0, 0.87f), new Vector3(-0.5f, 0, -0.87f)
            });
            nonManifold.SetTriangles(new[] { 0, 1, 2, 0, 1, 3, 0, 1, 4 }, 0, false);

            Mesh result = null;
            Assert.DoesNotThrow(() =>
                result = PreDecimate.Decimate(nonManifold, DecimateOptions.ForTriangleCount(2), out _));

            UnityEngine.Object.DestroyImmediate(nonManifold);
            if (result != null) UnityEngine.Object.DestroyImmediate(result);
        }

        [Test]
        public void IsDeterministic()
        {
            var a = Decimate(MeshCorpus.Icosphere(3, 1f), 300, out _);
            var b = Decimate(MeshCorpus.Icosphere(3, 1f), 300, out _);

            try
            {
                Assert.AreEqual(a.vertexCount, b.vertexCount);
                Assert.AreEqual(a.triangles.Length, b.triangles.Length);

                var pa = a.vertices;
                var pb = b.vertices;
                for (var i = 0; i < pa.Length; i++)
                    Assert.Less(Vector3.Distance(pa[i], pb[i]), 1e-5f,
                        $"vertex {i} differs between two identical runs");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);
            }
        }

        [Test]
        public void DecimationCostGrowsRoughlyWithTheInput()
        {
            // The regression guard for the defect that made a 1M-triangle scan unusable, and for
            // the class of defect it belongs to.
            //
            // Two containers in this file were quietly quadratic: a HashSet<long> keyed on packed
            // edges (which .NET hashes to `a ^ b`, a small number for nearly every edge of a real
            // mesh) and per-vertex incidence lists that never dropped dead triangles. Both stayed
            // correct while going quadratic, so every invariant test passed and every benchmark row
            // — all capped at 25k triangles — looked healthy.
            //
            // Only the SHAPE of the cost curve exposes that, which is why this test measures a
            // ratio rather than a duration. Quadrupling the input while holding the output fixed
            // should cost roughly four times as much; before the fix the seeding phase alone went
            // up 26x per step. The bound is deliberately loose: a heap sift contributes a log
            // factor and cache behaviour worsens with size, so the honest expectation is a little
            // over 4x, and anything approaching 16x is the defect returning.
            const int k_Budget = 4_000;

            var smallMesh = MeshCorpus.Icosphere(5, 1f);
            var largeMesh = MeshCorpus.Icosphere(6, 1f);

            try
            {
                var smallTris = smallMesh.triangles.Length / 3;
                var largeTris = largeMesh.triangles.Length / 3;
                Assert.AreEqual(4 * smallTris, largeTris, "the two levels should differ by exactly 4x");

                // Warm up: first call pays JIT and first-touch page faults, which at these sizes
                // is a large enough share of the measurement to invert the ratio.
                Object.DestroyImmediate(Decimate(MeshCorpus.Icosphere(4, 1f), k_Budget, out _));

                var smallWatch = System.Diagnostics.Stopwatch.StartNew();
                var smallOut = Decimate(smallMesh, k_Budget, out _);
                smallWatch.Stop();
                Object.DestroyImmediate(smallOut);

                var largeWatch = System.Diagnostics.Stopwatch.StartNew();
                var largeOut = Decimate(largeMesh, k_Budget, out _);
                largeWatch.Stop();
                Object.DestroyImmediate(largeOut);

                // A floor on the small measurement: dividing by a duration of 3 ms turns timer
                // noise into a ratio of 40 and fails the suite for no reason.
                var smallMs = Mathf.Max(20f, smallWatch.ElapsedMilliseconds);
                var largeMs = (float)largeWatch.ElapsedMilliseconds;
                var ratio = largeMs / smallMs;

                Assert.Less(ratio, 10f,
                    $"decimation cost grew {ratio:F1}x for 4x the input " +
                    $"({smallTris:N0} tris in {smallWatch.ElapsedMilliseconds} ms, " +
                    $"{largeTris:N0} in {largeWatch.ElapsedMilliseconds} ms). " +
                    "Something in the collapse loop is super-linear again — the usual causes are a " +
                    "long-keyed container without EdgeKey.Comparer, or an incidence list that is " +
                    "no longer pruned of dead triangles.");
            }
            finally
            {
                if (smallMesh != null) Object.DestroyImmediate(smallMesh);
                if (largeMesh != null) Object.DestroyImmediate(largeMesh);
            }
        }

        /// <summary>
        /// The same mesh at two absolute scales must decimate the same amount.
        /// </summary>
        /// <remarks>
        /// This is the regression for the defect that made the tool look like a decimator with no
        /// retopology, and it is written in terms of SCALE because that is the axis the bug lived
        /// on and the axis every other test in this file is blind to.
        ///
        /// Vector3.normalized returns zero below 1e-5 magnitude. A cross product of two mesh edges
        /// has the magnitude of twice a triangle's area, so shrinking a mesh far enough turns every
        /// face normal into (0,0,0) without any vertex becoming degenerate. The normal-flip guard
        /// then compared Dot(zero, zero) = 0 against cos(75 degrees) and rejected every candidate:
        /// on the 1.5M-triangle reference scan it refused all 2,247,091 of them and decimation
        /// applied not one collapse. The full-size mesh below is comfortably above the threshold and
        /// passed every existing test; the shrunk one is what a real scan looks like.
        ///
        /// Every mesh in the procedural corpus is unit-scale, which is exactly why none of them
        /// could catch this. Asserting on the RATIO rather than on either result keeps the test
        /// about scale invariance instead of about a particular decimation quality.
        /// </remarks>
        [Test]
        public void DecimationIsScaleInvariant()
        {
            const int k_Target = 2000;

            var big = MeshCorpus.Icosphere(4, 1f);         // ~20k triangles, unit radius
            var inputTriangles = big.triangles.Length / 3;

            // Radius 1e-3 puts the cross products near 1e-9, three orders under Unity's floor.
            // A photogrammetry scan of a small object sits in exactly this range.
            var small = MeshCorpus.Icosphere(4, 1e-3f);

            var bigResult = Decimate(big, k_Target, out var bigStats);
            var smallResult = Decimate(small, k_Target, out var smallStats);

            try
            {
                Assert.That(bigStats.outputTriangles, Is.LessThanOrEqualTo(k_Target),
                    "the unit-scale mesh should reach its budget");
                Assert.That(smallStats.outputTriangles, Is.LessThanOrEqualTo(k_Target),
                    $"the shrunk mesh stalled at {smallStats.outputTriangles} of {inputTriangles} " +
                    $"triangles with {smallStats.rejectedNormalFlip} flip rejections — the normals " +
                    "have underflowed, see MeshMath");

                Assert.That(smallStats.collapsesApplied,
                    Is.EqualTo(bigStats.collapsesApplied).Within(0.02f).Percent,
                    "collapse counts should not depend on the mesh's absolute size");
            }
            finally
            {
                if (bigResult != null) Object.DestroyImmediate(bigResult);
                if (smallResult != null) Object.DestroyImmediate(smallResult);
            }
        }
    }
}
