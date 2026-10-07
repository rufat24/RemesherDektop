// Tests for the measuring instruments themselves.
//
// This file is the direct lesson of this project's history: stage 4 was condemned, and weeks of work
// were planned, on the strength of statistics that turned out to be measuring the wrong quantity.
// A diagnostic that is trusted to make go/no-go decisions has to be validated as carefully as the
// code it judges — otherwise it is just a confident-looking number.
//
// The singularity index sum is the ideal test subject: it is a topological invariant, so its correct
// value is known in advance from the surface alone and is completely independent of how good the
// field is.

using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class FieldDiagnosticTests
    {
        static FieldReport Analyse(Mesh mesh, int orientationIterations = 120)
        {
            try
            {
                var topology = RemeshTopology.Build(mesh);
                var orientation = OrientationField.Solve(topology, orientationIterations);
                var scale = topology.EdgeLengthForQuadCount(
                    Mathf.Max(4, topology.MaxQuadCountForInput()));
                var position = PositionField.Solve(topology, orientation, scale, 50);

                return FieldDiagnostics.Analyse(topology, orientation, position);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SingularityIndexSum_IsEightOnAClosedGenusZeroSurface()
        {
            // For a 4-RoSy field the indices must sum to 4 * Euler characteristic. A sphere has
            // chi = 2, so the total is +8 — the eight valence-3 corners you would draw on a cube.
            // This pins the sign convention, which a torus cannot do because genus 1 sums to zero
            // whichever way round the sign goes.
            var report = Analyse(MeshCorpus.Icosphere(4, 1f));

            Assert.AreEqual(8, report.indexSum,
                $"index sum must be 4*chi = 8 for a sphere; got {report.indexSum} " +
                $"({report.valence3Count} at +1, {report.valence5Count} at -1, " +
                $"{report.higherOrderCount} higher order)");
        }

        [Test]
        public void SingularityIndexSum_IsZeroOnATorus()
        {
            // Genus 1 has chi = 0, so the positive and negative singularities must cancel exactly.
            var report = Analyse(MeshCorpus.Torus(1f, 0.35f, 96, 32));

            Assert.AreEqual(0, report.indexSum,
                $"index sum must be 4*chi = 0 for a torus; got {report.indexSum} " +
                $"({report.valence3Count} at +1, {report.valence5Count} at -1)");
        }

        [Test]
        public void FlatSurface_HasNoSingularitiesAndPerfectEnergy()
        {
            var report = Analyse(MeshCorpus.GridPlane(2f, 16));

            Assert.Zero(report.singularityCount, "a plane cannot carry a cross-field singularity");
            Assert.Greater(report.orientationEnergy, 0.999f);
        }

        [Test]
        public void RepresentativeDrift_NeverExceedsTheRawResidual()
        {
            // Drift is the same per-sweep displacement as the raw residual, minimised over whole
            // lattice offsets. The zero offset is always one of the candidates the rounding
            // considers, so drift <= residual is a mathematical guarantee, not an empirical
            // observation. If it is ever violated the gauge quotient is computing something else.
            //
            // Worth recording why this test no longer asserts a large GAP between the two. It
            // originally did, because the raw residual plateaued around 0.24 on a converged field
            // and that discrepancy is what exposed the misdiagnosis of this stage. Fixing Compat to
            // minimise the two endpoints' lattice positions jointly, rather than rounding each to
            // its own nearest cell, made neighbouring lattices agree well enough that the anchoring
            // snap has almost nothing left to fight — so the raw residual collapsed to the same
            // order as the drift. The gap closing is evidence the fix worked, which makes asserting
            // a gap exactly backwards.
            var mesh = MeshCorpus.Icosphere(4, 1f);
            try
            {
                var topology = RemeshTopology.Build(mesh);
                var orientation = OrientationField.Solve(topology, 120);
                var scale = topology.averageEdgeLength * 2f;
                var position = PositionField.Solve(topology, orientation, scale, 60);

                Assert.LessOrEqual(position.representativeDrift, position.lastResidual + 1e-6f,
                    $"drift {position.representativeDrift:E3} exceeds the raw residual " +
                    $"{position.lastResidual:E3}, which is impossible if the quotient is correct");

                Assert.Less(position.representativeDrift, 0.05f,
                    $"drift {position.representativeDrift:E3} is too large — the position field is " +
                    "genuinely not settling");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void PositionField_LooksHealthyOnSmoothClosedMeshes()
        {
            // The triage signal that decided the plan: if the lattice is well organised, then a bad
            // output mesh is extraction's fault, not the solver's.
            foreach (var mesh in new[]
            {
                MeshCorpus.Icosphere(4, 1f),
                MeshCorpus.Torus(1f, 0.35f, 96, 32)
            })
            {
                var name = mesh.name;
                var report = Analyse(mesh);

                Assert.IsTrue(report.PositionFieldLooksHealthy(),
                    $"{name}: position field is not healthy — {report.Summary()}");
                Assert.Less(report.discardedFraction, 0.25f, $"{name}: too many discarded edges");
                Assert.Greater(report.oneStepFraction, 0.05f,
                    $"{name}: too few one-step edges to build quads from");

                // Same-cell plus one-step plus discarded must account for every edge exactly once.
                Assert.That(report.sameCellFraction + report.oneStepFraction + report.discardedFraction,
                    Is.EqualTo(1f).Within(1e-4f), $"{name}: edge classes do not partition the edge set");
            }
        }

        [Test]
        public void EdgeGapHistogram_IsPopulated()
        {
            var report = Analyse(MeshCorpus.Icosphere(3, 1f));

            var total = 0;
            foreach (var bucket in report.edgeGapHistogram) total += bucket;

            Assert.AreEqual(report.edgeCount, total,
                "every edge must land in exactly one histogram bucket");
            Assert.Greater(report.edgeCount, 0);
        }

        [Test]
        public void ErrorRemoved_HandlesTheAlreadyPerfectCase()
        {
            // Guard against a division by zero producing a NaN that then propagates into a CSV and
            // silently poisons a baseline comparison.
            Assert.AreEqual(1f, FieldDiagnostics.ErrorRemoved(1f, 1f));
            Assert.AreEqual(1f, FieldDiagnostics.ErrorRemoved(0.9999999f, 1f));
            Assert.That(FieldDiagnostics.ErrorRemoved(0.9f, 0.99f), Is.EqualTo(0.9f).Within(1e-4f));
        }
    }
}
