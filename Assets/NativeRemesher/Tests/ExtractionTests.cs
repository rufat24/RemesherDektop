// Stage 5. Two kinds of test here, deliberately separated.
//
// The first kind passes today and guards behaviour that must not regress while the extractor is
// rewritten: the pipeline runs end to end, produces something, and does not throw on nasty input.
//
// The second kind are the Phase 2 acceptance criteria. They were written down BEFORE the extraction
// rewrite, so the target was fixed in advance rather than negotiated against whatever the rewrite
// happened to achieve. They now pass and are live.

using System;
using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class ExtractionTests
    {
        static RemeshResult Run(Mesh mesh, int targetQuads)
        {
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = targetQuads;
            return RemeshPipeline.Run(mesh, settings);
        }

        [Test]
        public void Pipeline_RunsEndToEndAndProducesFaces()
        {
            var mesh = MeshCorpus.Icosphere(4, 1f);
            try
            {
                var result = Run(mesh, 500);

                Assert.NotNull(result.extraction);
                Assert.Greater(result.extraction.faceCount, 0, "extraction produced no faces at all");
                Assert.NotNull(result.mesh);
                Assert.Greater(result.extraction.positions.Length, 0);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Pipeline_SurvivesNonManifoldInput()
        {
            // Three triangles on one edge. Must not throw and must not hang.
            var mesh = new Mesh();
            mesh.SetVertices(new[]
            {
                new Vector3(0, -1, 0), new Vector3(0, 1, 0),
                new Vector3(1, 0, 0), new Vector3(-0.5f, 0, 0.87f), new Vector3(-0.5f, 0, -0.87f)
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 1, 3, 0, 1, 4 }, 0, false);

            try
            {
                Assert.DoesNotThrow(() => Run(mesh, 8));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Pipeline_SurvivesSelfIntersectingInput()
        {
            var mesh = MeshCorpus.Icosphere(3, 1f);
            try
            {
                // Fold the sphere through itself by negating one axis on half the vertices.
                var vertices = mesh.vertices;
                for (var i = 0; i < vertices.Length; i += 2)
                    vertices[i] = new Vector3(-vertices[i].x * 1.4f, vertices[i].y, vertices[i].z);
                mesh.vertices = vertices;

                Assert.DoesNotThrow(() => Run(mesh, 100));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Pipeline_SurvivesOpenBoundary()
        {
            var mesh = MeshCorpus.OpenDisc(1f, 48, 10);
            try
            {
                Assert.DoesNotThrow(() => Run(mesh, 120));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Pipeline_IsDeterministic()
        {
            // Two identical runs must produce identical output. Without this the benchmark cannot
            // attribute a metric change to a code change, which is the whole point of having one.
            var mesh = MeshCorpus.Icosphere(3, 1f);
            try
            {
                var a = Run(mesh, 200);
                var b = Run(mesh, 200);

                Assert.AreEqual(a.extraction.faceCount, b.extraction.faceCount, "face count differs");
                Assert.AreEqual(a.extraction.positions.Length, b.extraction.positions.Length);

                for (var i = 0; i < a.extraction.positions.Length; i++)
                    Assert.Less(Vector3.Distance(a.extraction.positions[i], b.extraction.positions[i]),
                        1e-5f, $"output vertex {i} differs between runs");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void DegenerateInput_YieldsAnEmptyResultRatherThanNull()
        {
            // Regression guard. The face-count correction loop keeps the best attempt seen,
            // and on input that can produce no faces at all every attempt scores an infinite
            // error — so a naive "better than best" comparison never fires and the caller is
            // handed a null extraction to dereference. Three corpus meshes hit this path.
            var mesh = new Mesh();
            mesh.SetVertices(new[] { Vector3.zero, Vector3.right, new Vector3(2f, 0f, 0f) });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, false);

            try
            {
                var result = Run(mesh, 100);

                Assert.NotNull(result.extraction, "extraction must never come back null");
                Assert.NotNull(result.position, "position field must never come back null");
                Assert.NotNull(result.mesh);
                Assert.Zero(result.extraction.faceCount, "a zero-area sliver has no quads to find");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        // ==================================================================
        // Phase 2 acceptance criteria — written before the rewrite, now live.
        // ==================================================================

        [Test]
        public void NoCatastrophicCollapse_AcrossTheFullScaleRange()
        {
            // The known failure: at a lattice around 2.6x the input edge length and above, every
            // edge falls under the Euclidean merge threshold and union-find chains transitively, so
            // thousands of vertices merge into ONE cluster. Sweeping the whole range is the point —
            // the bug only appears past a threshold, so a single target count would miss it.
            var mesh = MeshCorpus.Icosphere(4, 1f);
            try
            {
                var topology = RemeshTopology.Build(mesh);

                for (var multiple = 1.5f; multiple <= 6.01f; multiple += 0.25f)
                {
                    var edge = topology.averageEdgeLength * multiple;
                    var target = Mathf.Max(4, Mathf.RoundToInt(topology.totalArea / (edge * edge)));

                    var result = Run(mesh, target);
                    var outVerts = result.extraction.positions.Length;

                    Assert.Greater(outVerts, 4,
                        $"lattice {multiple:F2}x input edge collapsed to {outVerts} vertices");
                    Assert.Greater(result.extraction.faceCount, 1,
                        $"lattice {multiple:F2}x input edge produced {result.extraction.faceCount} faces");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void QuadFraction_ExceedsSeventyPercentOnSmoothClosedMeshes()
        {
            // The bumpy sphere was split out into its own test while it sat at 63.6%, below this
            // bar, and folded back in once pre-decimation lifted it to 85.7%. Varying curvature is
            // no longer a special case: decimating first adapts the input's edge length to the
            // target, which is what a single global lattice spacing could not do.
            foreach (var factory in new Func<Mesh>[]
            {
                () => MeshCorpus.Icosphere(4, 1f),
                () => MeshCorpus.Torus(1f, 0.35f, 128, 48),
                () => MeshCorpus.BumpySphere(5, 1f, 0.12f, 6f)
            })
            {
                var mesh = factory();
                try
                {
                    var result = Run(mesh, 800);
                    var fraction = result.extraction.quadFraction;

                    Assert.Greater(fraction, 0.70f,
                        $"{mesh.name}: quad fraction {fraction:P1} (was ~3% before the rewrite)");
                }
                finally { UnityEngine.Object.DestroyImmediate(mesh); }
            }
        }

        [Test]
        [Ignore("Superseded: pre-decimation lifted the bumpy sphere from 63.6% to 85.7%, so it now " +
                "passes QuadFraction_ExceedsSeventyPercentOnSmoothClosedMeshes and has been folded " +
                "back into it. Kept for one commit as the record of why that split existed.")]
        public void QuadFraction_OnVaryingCurvature_IsRecordedNotYetAtTarget()
        {
            // Split out from the test above rather than lowering that one's bar, because the two are
            // measuring different things and averaging them would hide the interesting number.
            //
            // A bumpy sphere has strongly varying principal curvature, and the solver is currently
            // given a single global lattice spacing. One spacing cannot suit both the flats and the
            // bumps, so quads are lost wherever the mismatch is worst. Measured 63.6% here against
            // >70% on the smooth cases.
            //
            // The fix is the per-vertex sizing field (Phase 4), not anything in extraction, so this
            // asserts the floor reached by the extraction rewrite and will be raised to 0.70 when
            // adaptive sizing lands. Deliberately NOT [Ignore]d: a silent regression from 63% back
            // toward 3% is exactly what this suite exists to catch.
            var mesh = MeshCorpus.BumpySphere(5, 1f, 0.12f, 6f);
            try
            {
                var result = Run(mesh, 800);
                var fraction = result.extraction.quadFraction;

                Assert.Greater(fraction, 0.55f,
                    $"quad fraction {fraction:P1} regressed below the post-rewrite floor");
                Assert.Less(fraction, 0.70f,
                    $"quad fraction {fraction:P1} now clears the smooth-mesh bar — fold this test " +
                    "back into QuadFraction_ExceedsSeventyPercentOnSmoothClosedMeshes and delete it");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Output_IsManifoldOnClosedInput()
        {
            var mesh = MeshCorpus.Icosphere(4, 1f);
            try
            {
                var result = Run(mesh, 800);
                var metrics = MeshMetrics.Measure(
                    PolyMesh.FromExtraction(result.extraction),
                    PolyMesh.FromTopology(result.topology),
                    geometrySamples: 0);

                // Watertightness FIRST, because its absence is what a user actually sees — holes.
                //
                // This test previously checked only non-manifold edges, duplicate faces and winding,
                // and passed while the output had 83 boundary edges and an Euler characteristic of
                // -7 on a closed sphere. The benchmark was recording both numbers the whole time and
                // the summary tables only printed nonManifoldEdges, so "validity is clean" was
                // reported on the strength of one column out of three.
                Assert.Zero(metrics.boundaryEdges,
                    $"closed input produced {metrics.boundaryEdges} boundary edges — the output has " +
                    "holes in it");
                // Read chi against the component count, or the number means nothing: chi = 2C - 2g,
                // so chi = 8 is four spheres and chi = -6 is one genus-4 shell. Reporting both is
                // what turned "Euler is 8, which is impossible" into "the output is in four pieces".
                //
                // Handles first, because a handle is a manufactured tunnel and strictly worse than
                // a fragment: it means a face was built across geometry that is not adjacent.
                // The whole validity picture in the message, not one number. Reading "Euler 8" in
                // isolation cost a wrong diagnosis once already — it was read as four separate
                // shells when the component count was in fact 1, which meant non-manifold edges.
                var diagnosis =
                    $"V={metrics.outputVertices} F={metrics.outputFaces} " +
                    $"euler={metrics.eulerCharacteristic} components={metrics.componentCount} " +
                    $"boundaryEdges={metrics.boundaryEdges} nonManifold={metrics.nonManifoldEdges} " +
                    $"duplicateFaces={metrics.duplicateFaces} degenerate={metrics.degenerateFaces} " +
                    $"isolatedVerts={metrics.isolatedVertices} " +
                    $"windingOk={metrics.windingConsistent}";

                Assert.AreEqual(2 * metrics.componentCount, metrics.eulerCharacteristic,
                    $"Euler must be 2 per closed component; {diagnosis}");
                Assert.AreEqual(1, metrics.componentCount,
                    $"a closed input must produce one connected shell, not " +
                    $"{metrics.componentCount} — the extractor is fragmenting the surface");
                Assert.IsTrue(metrics.isWatertight, "output is not watertight");

                Assert.Zero(metrics.nonManifoldEdges, "output has non-manifold edges");
                Assert.Zero(metrics.duplicateFaces, "output has duplicate faces");
                Assert.Zero(metrics.degenerateFaces, "output has degenerate faces");
                Assert.IsTrue(metrics.windingConsistent, "output winding is inconsistent");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void FaceCount_TracksTheTargetWithinTwentyFivePercent()
        {
            // Targets are taken from the input's own useful range rather than hard-coded. A fixed
            // count means a different lattice-spacing-to-edge-length ratio on every mesh, and that
            // ratio — not the count — is what determines whether the solver can serve the request.
            // Hard-coding 200 here previously asked for a 6.7x spacing, which is far outside the
            // band the single-level solver works in, and the resulting failure looked like a bug in
            // the correction loop rather than an out-of-envelope request.
            var mesh = MeshCorpus.Icosphere(5, 1f);
            try
            {
                var topology = RemeshTopology.Build(mesh);
                topology.UsefulQuadCountRange(out var coarsest, out var finest);

                Assert.Less(coarsest, finest, "the useful range is inverted");

                foreach (var target in new[] { coarsest, (coarsest + finest) / 2, finest })
                {
                    var result = Run(mesh, target);
                    var ratio = result.extraction.faceCount / (float)result.effectiveTargetQuadCount;

                    Assert.That(ratio, Is.EqualTo(1f).Within(0.25f),
                        $"target {target}: got {result.extraction.faceCount} faces (ratio {ratio:F2})");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void PreDecimation_LiftsTheCoarseTargetEnvelope()
        {
            // Replaces an earlier test that asserted coarse targets degrade. They do — but only
            // when the solver is handed the dense mesh directly, which is no longer the default.
            //
            // The envelope is a property of the spacing-to-input-edge ratio, so decimating first
            // moves the input rather than the request and the ratio never leaves the band. This
            // tests exactly that: the degradation must be visible with the stage off, and mitigated
            // with it on. Asserting only the second half would pass even if the envelope were
            // imaginary.
            var mesh = MeshCorpus.Icosphere(4, 1f);
            try
            {
                var topology = RemeshTopology.Build(mesh);
                var inBand = topology.QuadCountForSpacingMultiple(2f);
                var outOfBand = topology.QuadCountForSpacingMultiple(4f);

                var settings = RemeshSettings.Default;

                var noDecimate = RemeshProfile.Default;
                noDecimate.preDecimate = false;

                settings.targetQuadCount = inBand;
                var rawInBand = RemeshPipeline.Run(mesh, settings, noDecimate).extraction.quadFraction;

                settings.targetQuadCount = outOfBand;
                var rawOutOfBand = RemeshPipeline.Run(mesh, settings, noDecimate).extraction.quadFraction;
                var decimatedOutOfBand = RemeshPipeline.Run(mesh, settings, RemeshProfile.Default)
                    .extraction.quadFraction;

                Assert.Less(rawOutOfBand, rawInBand,
                    $"without pre-decimation a 4x spacing ({rawOutOfBand:P1}) should be worse than " +
                    $"a 2x one ({rawInBand:P1}); if not, RemeshTopology.maxUsefulSpacingMultiple " +
                    "is understated");

                Assert.Greater(decimatedOutOfBand, rawOutOfBand,
                    $"pre-decimation ({decimatedOutOfBand:P1}) should recover the out-of-band case " +
                    $"({rawOutOfBand:P1}) — that is the whole reason the stage exists");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        [Ignore("Superseded by PreDecimation_LiftsTheCoarseTargetEnvelope. Pre-decimation is now on " +
                "by default, so a coarse REQUEST no longer implies a coarse spacing ratio and this " +
                "test's premise no longer holds.")]
        public void CoarseTargets_DegradeAndTheEnvelopeSaysSo()
        {
            // Records the known weakness rather than hiding it, and pins the boundary so that the
            // envelope constant stays honest.
            //
            // Asking for a spacing well beyond maxUsefulSpacingMultiple gives a poor result: nearly
            // every input edge lands inside one lattice cell, so almost nothing is classified as a
            // one-step edge and the output graph is carried by refused-collapse fallback edges,
            // which are not lattice-aligned. Measured 20-49% quads at 3.3x against 83-98% inside
            // the band.
            //
            // The fix is to decimate the input first so its edge length rises — Phase 4's
            // photogrammetry and low-poly profiles — not to widen this constant.
            var mesh = MeshCorpus.Icosphere(4, 1f);
            try
            {
                var topology = RemeshTopology.Build(mesh);

                var inBand = topology.QuadCountForSpacingMultiple(2f);
                var wayOutOfBand = topology.QuadCountForSpacingMultiple(4f);

                var good = Run(mesh, inBand).extraction.quadFraction;
                var bad = Run(mesh, wayOutOfBand).extraction.quadFraction;

                Assert.Greater(good, 0.70f,
                    $"inside the envelope the quad fraction should hold up; got {good:P1}");
                Assert.Less(bad, good,
                    $"a 4x spacing ({bad:P1}) should be worse than a 2x one ({good:P1}); if it is " +
                    "not, RemeshTopology.maxUsefulSpacingMultiple is understated and should be raised");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
    }
}
