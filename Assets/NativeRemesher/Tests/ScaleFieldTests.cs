// Tests for curvature-adaptive lattice sizing.
//
// The load-bearing claim is the one in the first test: adaptivity 0 must reproduce uniform sizing
// EXACTLY, not approximately. The whole design rests on uniform being this formula evaluated with a
// constant input rather than a separate code path, and if that identity does not hold then every
// comparison between a graded run and a uniform one is measuring two unrelated things.
//
// The rest assert the properties the position solver depends on and cannot check for itself: that
// spacing actually shrinks where the surface curves, that the ratio across any edge is bounded (an
// unbounded one tears the lattice rather than grading it), and that the normalisation aims at the
// requested face count rather than drifting with the grading.

using System;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class ScaleFieldTests
    {
        /// <summary>
        /// A capsule: a cylinder of radius <paramref name="radius"/> capped by hemispheres. Chosen
        /// because its curvature is known and piecewise constant — 1/radius around the barrel and
        /// 2/radius on the caps — so "smaller quads where it curves" has an arithmetic answer rather
        /// than an eyeball one.
        /// </summary>
        static RemeshTopology Capsule(float radius, float length, int around, int alongCap)
        {
            var positions = new System.Collections.Generic.List<Vector3>();
            var rings = new System.Collections.Generic.List<int[]>();

            int[] Ring(float y, float r)
            {
                var ring = new int[around];
                for (var a = 0; a < around; a++)
                {
                    var theta = a / (float)around * 2f * Mathf.PI;
                    ring[a] = positions.Count;
                    positions.Add(new Vector3(Mathf.Cos(theta) * r, y, Mathf.Sin(theta) * r));
                }
                return ring;
            }

            var half = length * 0.5f;

            // Bottom cap, from the south pole up to the seam. The pole ring is degenerate and welds
            // to a single vertex, which is why nothing here remembers indices — see the classifier
            // below.
            for (var s = 0; s <= alongCap; s++)
            {
                var phi = Mathf.PI * 0.5f * s / alongCap;      // 0 at the pole
                rings.Add(Ring(-half - Mathf.Cos(phi) * radius, Mathf.Sin(phi) * radius));
            }

            // Barrel. Spaced to roughly match the cap's ring spacing so neither region is made of
            // stretched triangles, which would put curvature where the shape has none.
            var barrelRings = Mathf.Max(2, Mathf.RoundToInt(length / (radius * Mathf.PI * 0.5f / alongCap)));
            for (var s = 1; s < barrelRings; s++)
                rings.Add(Ring(-half + length * s / (float)barrelRings, radius));

            // Top cap.
            for (var s = alongCap; s >= 0; s--)
            {
                var phi = Mathf.PI * 0.5f * s / alongCap;
                rings.Add(Ring(half + Mathf.Cos(phi) * radius, Mathf.Sin(phi) * radius));
            }

            var triangles = new System.Collections.Generic.List<int>();
            for (var s = 0; s < rings.Count - 1; s++)
                for (var a = 0; a < around; a++)
                {
                    var b = (a + 1) % around;
                    triangles.Add(rings[s][a]); triangles.Add(rings[s + 1][a]); triangles.Add(rings[s + 1][b]);
                    triangles.Add(rings[s][a]); triangles.Add(rings[s + 1][b]); triangles.Add(rings[s][b]);
                }

            return RemeshTopology.Build(positions.ToArray(), triangles.ToArray());
        }

        /// <summary>
        /// Splits the capsule's welded vertices into "on the barrel" and "on a cap" by POSITION.
        /// </summary>
        /// <remarks>
        /// By position rather than by remembering indices from the generator, because
        /// <see cref="RemeshTopology.Build"/> welds — the two pole rings are degenerate and collapse
        /// to one vertex each — so generator indices do not survive into the topology. Carrying them
        /// across made the first version of these tests read spacings belonging to other vertices,
        /// which is the same two-index-spaces mistake that has already produced a confident wrong
        /// answer once in this project's benchmarks.
        /// </remarks>
        static void Regions(RemeshTopology topology, float radius, float length,
            out int[] barrel, out int[] cap)
        {
            var half = length * 0.5f;
            var barrelList = new System.Collections.Generic.List<int>();
            var capList = new System.Collections.Generic.List<int>();

            for (var v = 0; v < topology.vertexCount; v++)
            {
                var y = topology.positions[v].y;

                // Margins on both sides of the seam, so the graded transition between the two
                // regimes belongs to neither sample.
                if (Mathf.Abs(y) < half - radius * 0.25f) barrelList.Add(v);
                else if (Mathf.Abs(y) > half + radius * 0.35f) capList.Add(v);
            }

            barrel = barrelList.ToArray();
            cap = capList.ToArray();
        }

        [Test]
        public void ZeroAdaptivityIsExactlyUniformSizing()
        {
            var topology = Capsule(1f, 3f, 24, 12);
            const int target = 500;

            var field = ScaleField.FromCurvature(topology, target, adaptivity: 0f);
            var uniform = topology.EdgeLengthForQuadCount(target);

            Assert.AreEqual(1f, field.spread, 1e-5f, "a uniform field must have no spread");
            for (var i = 0; i < field.vertexCount; i++)
                Assert.AreEqual(uniform, field.spacing[i], uniform * 1e-5f,
                    $"vertex {i} does not carry the uniform spacing");
        }

        /// <summary>
        /// Two spheres of different radii, far apart, as one mesh. This is the thumb in miniature:
        /// a large body and a small feature, where the small one needs finer quads to exist at all.
        /// </summary>
        static RemeshTopology TwoSpheres(float radiusA, float radiusB, float separation,
            int around, int rings)
        {
            var positions = new System.Collections.Generic.List<Vector3>();
            var triangles = new System.Collections.Generic.List<int>();

            void Sphere(float radius, Vector3 centre)
            {
                var first = positions.Count;
                var ringIndex = new System.Collections.Generic.List<int[]>();

                for (var s = 0; s <= rings; s++)
                {
                    var phi = Mathf.PI * s / rings;
                    var ring = new int[around];
                    for (var a = 0; a < around; a++)
                    {
                        var theta = a / (float)around * 2f * Mathf.PI;
                        ring[a] = positions.Count;
                        positions.Add(centre + new Vector3(
                            Mathf.Sin(phi) * Mathf.Cos(theta) * radius,
                            Mathf.Cos(phi) * radius,
                            Mathf.Sin(phi) * Mathf.Sin(theta) * radius));
                    }
                    ringIndex.Add(ring);
                }

                for (var s = 0; s < rings; s++)
                    for (var a = 0; a < around; a++)
                    {
                        var b = (a + 1) % around;
                        triangles.Add(ringIndex[s][a]);
                        triangles.Add(ringIndex[s + 1][a]);
                        triangles.Add(ringIndex[s + 1][b]);
                        triangles.Add(ringIndex[s][a]);
                        triangles.Add(ringIndex[s + 1][b]);
                        triangles.Add(ringIndex[s][b]);
                    }

                _ = first;
            }

            Sphere(radiusA, Vector3.zero);
            Sphere(radiusB, new Vector3(separation, 0f, 0f));

            return RemeshTopology.Build(positions.ToArray(), triangles.ToArray());
        }

        [Test]
        public void SpacingIsSmallerWhereTheSurfaceCurvesMore()
        {
            const float big = 1f;
            const float small = 0.25f;
            var topology = TwoSpheres(big, small, 4f, 28, 14);

            var field = ScaleField.FromCurvature(topology, 800, adaptivity: 1f);

            // Split by position: everything past the midpoint belongs to the small sphere.
            var bigMean = 0.0; var bigCount = 0;
            var smallMean = 0.0; var smallCount = 0;
            for (var v = 0; v < topology.vertexCount; v++)
            {
                if (topology.positions[v].x > 2f) { smallMean += field.spacing[v]; smallCount++; }
                else { bigMean += field.spacing[v]; bigCount++; }
            }

            Assert.Greater(bigCount, 0);
            Assert.Greater(smallCount, 0);

            bigMean /= bigCount;
            smallMean /= smallCount;

            Assert.Greater(bigMean, smallMean * 1.5f,
                $"the small sphere ({smallMean:F5}) should carry much finer quads than the big one ({bigMean:F5})");
            Assert.Greater(field.spread, 1.5f, "the field barely graded");
        }

        [Test]
        public void AShapeOfConstantCurvatureIsNotGraded()
        {
            // A capsule's MAXIMUM principal curvature is 1/radius everywhere — on the barrel the
            // second principal curvature is zero, and on the caps it is also 1/radius, but the
            // maximum is what sizing follows and it does not vary. So a capsule must come out very
            // nearly uniform, and this asserts that grading responds to the shape rather than
            // firing on anything that is not a plane.
            //
            // This is here because the first version of the test above used a capsule and read its
            // 1.04x spread as a failure to grade. It was the correct answer to a question the
            // fixture could not ask.
            const float radius = 1f;
            const float length = 3f;
            var topology = Capsule(radius, length, 32, 14);
            Regions(topology, radius, length, out var barrel, out var cap);

            Assert.Greater(barrel.Length, 0);
            Assert.Greater(cap.Length, 0);

            var field = ScaleField.FromCurvature(topology, 800, adaptivity: 1f);

            Assert.Less(field.spread, 1.25f,
                $"a constant-max-curvature shape graded by {field.spread:F2}x");
        }

        [Test]
        public void NoEdgeCarriesMoreThanTheGradationRatio()
        {
            var topology = Capsule(0.5f, 4f, 32, 14);

            const float gradation = 1.3f;
            var field = ScaleField.FromCurvature(topology, 600, adaptivity: 1f,
                maxRatio: 10f, gradation: gradation);

            // Unbounded, this is what tears the lattice: two adjacent cells cannot agree on a size
            // and the position field has nowhere consistent to put the boundary between them.
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;
            var worst = 1f;

            for (var i = 0; i < topology.vertexCount; i++)
                for (var k = start[i]; k < start[i + 1]; k++)
                {
                    var j = adjacency[k];
                    var ratio = Mathf.Max(field.spacing[i] / field.spacing[j],
                        field.spacing[j] / field.spacing[i]);
                    worst = Mathf.Max(worst, ratio);
                }

            Assert.LessOrEqual(worst, gradation + 1e-3f,
                $"worst edge ratio {worst:F4} exceeds the gradation bound");
        }

        [Test]
        public void TheSpreadIsBoundedByMaxRatio()
        {
            var topology = Capsule(0.3f, 5f, 32, 14);

            // Gradation is relaxed so the clamp is the only thing that can bind, which is what this
            // is asserting. With both active the gradation usually tightens the result further and
            // the test would pass without the clamp existing.
            var field = ScaleField.FromCurvature(topology, 600, adaptivity: 1f,
                maxRatio: 2f, gradation: 100f);

            Assert.LessOrEqual(field.spread, 2f * 2f + 1e-3f,
                "spacings span more than the clamp permits either side of the reference");
        }

        [Test]
        public void TheFaceCountStaysNearTheRequestWhenGrading()
        {
            var topology = Capsule(1f, 3f, 32, 14);
            const int target = 700;

            // Quad count is approximately the sum of area / spacing^2, and that is exactly what the
            // normalisation inverts. If grading shifted the count, every comparison between a graded
            // and a uniform run would be at different densities and none of them would mean
            // anything — which is the confound that has already cost this project twice.
            var field = ScaleField.FromCurvature(topology, target, adaptivity: 1f);

            var predicted = 0.0;
            for (var i = 0; i < topology.vertexCount; i++)
                predicted += topology.areas[i] / (double)(field.spacing[i] * field.spacing[i]);

            Assert.AreEqual(target, predicted, target * 0.02,
                "the normalisation does not aim at the requested count");
        }

        [Test]
        public void ADegenerateOrEmptyInputIsHandledWithoutThrowing()
        {
            var topology = Capsule(1f, 3f, 8, 4);

            Assert.DoesNotThrow(() => ScaleField.FromCurvature(topology, 0));
            Assert.DoesNotThrow(() => ScaleField.FromCurvature(topology, 10, adaptivity: -1f));
            Assert.DoesNotThrow(() => ScaleField.FromCurvature(topology, 10, maxRatio: 0f));
            Assert.Throws<ArgumentNullException>(() => ScaleField.FromCurvature(null, 10));
        }

        static float Mean(float[] values, int[] indices)
        {
            if (indices.Length == 0) return 0f;
            var total = 0.0;
            foreach (var i in indices) total += values[i];
            return (float)(total / indices.Length);
        }
    }
}
