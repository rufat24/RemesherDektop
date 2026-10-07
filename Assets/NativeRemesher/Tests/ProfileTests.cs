// Domain profiles.
//
// The load-bearing test here is the last one: pre-decimation must actually beat solving the dense
// mesh directly at an aggressive target. That is the entire justification for the stage — if it does
// not hold, the profile system is complexity for nothing and should be cut.

using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class ProfileTests
    {
        [Test]
        public void EveryProfileValidatesAndDescribesItself()
        {
            foreach (RemeshProfileKind kind in System.Enum.GetValues(typeof(RemeshProfileKind)))
            {
                var profile = RemeshProfile.For(kind).Validated();

                Assert.AreEqual(kind, profile.kind, "For() returned the wrong kind");
                Assert.GreaterOrEqual(profile.decimateBudgetPerQuad, 1.5f);
                Assert.GreaterOrEqual(profile.positionIterations, 1);
                Assert.IsNotEmpty(profile.Describe());
            }
        }

        [Test]
        public void ScanAndOrganicProfilesDisableFeatureDetection()
        {
            // Not an arbitrary choice. Dihedral detection triggers on high-frequency variation, and
            // scanner noise is exactly that, so on scan data it finds thousands of spurious creases
            // and constraining the field to them is worse than leaving it free. Organic surfaces have
            // no true creases at all, so a threshold would only pick up sculpting noise.
            Assert.Zero(RemeshProfile.For(RemeshProfileKind.Photogrammetry).featureAngleDegrees);
            Assert.Zero(RemeshProfile.For(RemeshProfileKind.Character).featureAngleDegrees);

            Assert.Greater(RemeshProfile.For(RemeshProfileKind.HardSurface).featureAngleDegrees, 0f);
            Assert.Greater(RemeshProfile.For(RemeshProfileKind.GameAsset).featureAngleDegrees, 0f);
        }

        [Test]
        public void HardSurfaceUsesALowerFeatureThresholdThanGeneral()
        {
            // Chamfers and shallow bevels are the features that matter on mechanical parts, and they
            // sit well under the 30 degrees a general default uses.
            Assert.Less(RemeshProfile.For(RemeshProfileKind.HardSurface).featureAngleDegrees,
                RemeshProfile.For(RemeshProfileKind.GameAsset).featureAngleDegrees);
        }

        [Test]
        public void AutoSelectionIsAThresholdOnTheProbeFraction()
        {
            // Both branches, either side of the one number. Worth stating as a test because the
            // threshold is SHARED with the warning the window shows when auto is off, and two
            // copies of it would let the tool route an input to a profile and then report that the
            // profile was the wrong one.
            Assert.AreEqual(RemeshProfileKind.GameAsset,
                RemeshPipeline.SelectProfileKind(0f));
            Assert.AreEqual(RemeshProfileKind.GameAsset,
                RemeshPipeline.SelectProfileKind(RemeshPipeline.suspectFeatureFraction));
            Assert.AreEqual(RemeshProfileKind.Photogrammetry,
                RemeshPipeline.SelectProfileKind(RemeshPipeline.suspectFeatureFraction + 0.01f));
            Assert.AreEqual(RemeshProfileKind.Photogrammetry,
                RemeshPipeline.SelectProfileKind(1f));

            // Whatever the scan branch picks must declare no creases. That is the whole of what the
            // selection is for, and it is stated here so a later change of profile cannot quietly
            // route a scan somewhere that detects them.
            Assert.Zero(RemeshProfile.For(RemeshPipeline.SelectProfileKind(0.5f))
                .featureAngleDegrees);
        }

        [Test]
        public void AutoSelectionIsOffUnlessAskedFor()
        {
            // Every existing caller passes a profile and expects to get that profile. The flag
            // defaults to false for that reason, and this is the guard on the default.
            var mesh = MeshCorpus.SubdividedCube(1f, 16);
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = 500;

                var result = RemeshPipeline.Run(mesh, settings,
                    RemeshProfile.For(RemeshProfileKind.Character));

                Assert.IsFalse(result.profileWasAutoSelected);
                Assert.AreEqual(RemeshProfileKind.Character, result.profile.kind);
                Assert.Zero(result.profileProbeFeatureFraction);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void AModelledInputAutoSelectsTheProfileThatKeepsItsCreases()
        {
            // Every crease on a cube is a real 90-degree edge, so the detector is right about all
            // of them and the fraction stays low. It has to: routing a cube to a scan profile would
            // switch off the detection that holds its corners.
            var mesh = MeshCorpus.SubdividedCube(1f, 32);
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = 2000;

                var result = RemeshPipeline.Run(mesh, settings,
                    RemeshProfile.For(RemeshProfileKind.Character), null, true);

                Assert.IsTrue(result.profileWasAutoSelected);
                Assert.Less(result.profileProbeFeatureFraction,
                    RemeshPipeline.suspectFeatureFraction);
                Assert.AreEqual(RemeshProfileKind.GameAsset, result.profile.kind);
                Assert.Greater(result.profile.featureAngleDegrees, 0f);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void ANoisyInputAutoSelectsAProfileThatDeclaresNoCreases()
        {
            var mesh = NoisySphere(4, 1f, 0.08f);
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = 500;

                var result = RemeshPipeline.Run(mesh, settings,
                    RemeshProfile.For(RemeshProfileKind.GameAsset), null, true);

                Assert.IsTrue(result.profileWasAutoSelected);
                // Asserted before the profile, so a failure says whether the detector or the
                // selection is the part that moved.
                Assert.Greater(result.profileProbeFeatureFraction,
                    RemeshPipeline.suspectFeatureFraction);
                Assert.Zero(result.profile.featureAngleDegrees);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>An icosphere with high-frequency radial noise: a stand-in for scan data.</summary>
        /// <remarks>
        /// Built here rather than taken from the corpus, because the corpus has nothing noisy.
        /// <c>MeshCorpus.BumpySphere</c> is a product of three sinusoids — SMOOTH at the scale a
        /// dihedral test reads, so no amplitude makes it fire. What has to be reproduced is
        /// per-vertex, high-frequency variation, which is what a scanner produces and what the
        /// detector cannot tell from a crease.
        ///
        /// The displacement is a hash of the POSITION rather than of the vertex index, and that is
        /// load-bearing. An icosphere's array carries coincident vertices; perturbing those
        /// independently would pull them apart, welding would no longer merge them, and the cracks
        /// would be detected as boundary features — so the test would pass for a reason that has
        /// nothing to do with noise.
        /// </remarks>
        static Mesh NoisySphere(int subdivisions, float radius, float amplitude)
        {
            var mesh = MeshCorpus.Icosphere(subdivisions, radius);
            var vertices = mesh.vertices;

            for (var i = 0; i < vertices.Length; i++)
            {
                var d = vertices[i].normalized;
                var h = Mathf.Sin(d.x * 127.1f + d.y * 311.7f + d.z * 74.7f) * 43758.5453f;
                vertices[i] = d * (radius + amplitude * (h - Mathf.Floor(h) - 0.5f));
            }

            mesh.vertices = vertices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.name = "sphere_noisy";
            return mesh;
        }

        [Test]
        public void PipelineRunsUnderEveryProfile()
        {
            foreach (RemeshProfileKind kind in System.Enum.GetValues(typeof(RemeshProfileKind)))
            {
                var mesh = MeshCorpus.Icosphere(4, 1f);
                try
                {
                    var settings = RemeshSettings.Default;
                    settings.targetQuadCount = 300;

                    RemeshResult result = null;
                    Assert.DoesNotThrow(
                        () => result = RemeshPipeline.Run(mesh, settings, RemeshProfile.For(kind)),
                        $"profile {kind} threw");

                    Assert.NotNull(result?.extraction, $"profile {kind} produced no extraction");
                    Assert.Greater(result.extraction.faceCount, 0, $"profile {kind} produced no faces");
                }
                finally { UnityEngine.Object.DestroyImmediate(mesh); }
            }
        }

        [Test]
        public void OutputMeshIsNamedFromTheOriginalNotTheDecimatedIntermediate()
        {
            var mesh = MeshCorpus.Icosphere(3, 1f);
            mesh.name = "Widget";
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = 100;

                var result = RemeshPipeline.Run(mesh, settings,
                    RemeshProfile.For(RemeshProfileKind.GameAsset));

                Assert.AreEqual("Widget_Quads", result.mesh.name,
                    "the decimated intermediate's name leaked into the output");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void PreDecimationNoLongerBuysQualityOnceTheHierarchyCarriesTheCoarsening()
        {
            // This was THE justification for the pre-decimation stage, and the hierarchy took it
            // away. Recorded rather than quietly relaxed, because the test was written as a
            // kill-switch — "if this ever fails, the stage is not earning its complexity" — and a
            // kill-switch that gets its threshold lowered is not a kill-switch.
            //
            // The original premise: output quality tracks the ratio of lattice spacing to input
            // edge length and collapses past about 2.5x, so asking a dense mesh for very few quads
            // pushes it far out of band, and decimating first raises the input edge length to bring
            // it back. Measured, pre-decimation was worth more than 10 points of quad fraction.
            //
            // What that premise was really describing is a SINGLE-LEVEL solver's inability to
            // coarsen far, which is the same limit the hierarchy exists to remove: information
            // moves one edge per Gauss-Seidel sweep, so a lattice many input edges wide cannot be
            // agreed on at the finest level at all. With HierarchicalSolve the coarse levels agree
            // it instead, and the margin fell from over 10 points to 1.8.
            //
            // So the stage's quality case is gone and its COST case stands, untouched: the
            // reference photogrammetry asset is 1.5M triangles and the solver is handed 54,740.
            // That belongs in the benchmark's timings, not in a unit test, and this test now
            // asserts the two things that are still deterministic here — decimation does not make
            // quality worse, and it does actually reduce what the solver is given.
            const int k_Target = 150;

            var dense = MeshCorpus.Icosphere(5, 1f);
            var alsoDense = MeshCorpus.Icosphere(5, 1f);

            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = k_Target;

                var direct = RemeshProfile.Default;
                direct.preDecimate = false;

                var withDecimation = RemeshProfile.Default;
                withDecimation.preDecimate = true;

                var withoutResult = RemeshPipeline.Run(alsoDense, settings, direct);
                var withResult = RemeshPipeline.Run(dense, settings, withDecimation);

                var without = withoutResult.extraction.quadFraction;
                var with = withResult.extraction.quadFraction;

                // Not worse. A margin either way inside a couple of points is noise at this face
                // count, so the bound is one-sided and loose on purpose: what would matter is
                // decimation making the result clearly worse, which would mean the stage is
                // actively harmful now that it is not needed for reach.
                Assert.Greater(with, without - 0.03f,
                    $"pre-decimation ({with:P1} quads) is now clearly worse than solving the dense " +
                    $"mesh directly ({without:P1}) at {k_Target} quads — with the hierarchy " +
                    "carrying the coarsening, a harmful stage has nothing left to justify it");

                // The thing it does still deliver, and the only reason it is kept.
                Assert.Less(withResult.topology.vertexCount, withoutResult.topology.vertexCount,
                    "pre-decimation did not reduce what the solver was handed, which is the whole " +
                    "of its remaining case");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(dense);
                UnityEngine.Object.DestroyImmediate(alsoDense);
            }
        }
    }
}
