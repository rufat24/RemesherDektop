// Stage 2. The decisive test is the plane: a 4-RoSy field on a flat surface must comb out to
// globally parallel, so its smoothness energy has to reach 1.0 exactly. Any error in the symmetry
// handling leaves the field frustrated below that no matter how many sweeps it gets.
//
// The sphere test exists to catch the opposite failure — a solver that only looks converged because
// it started somewhere convenient. It is seeded from noise and judged on the *fraction of achievable
// error removed*, because a random 4-RoSy field already scores about 0.90: the energy takes the best
// of four rotations, so it is never more than 45 degrees off. Quoting the raw number as a win would
// be measuring the symmetry group, not the solver.

using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class OrientationFieldTests
    {
        [Test]
        public void Plane_CombsToGloballyParallel()
        {
            var mesh = MeshCorpus.GridPlane(2f, 12);
            var topology = RemeshTopology.Build(mesh);

            try
            {
                var field = OrientationField.Solve(topology, 200);
                var energy = FieldDiagnostics.SmoothnessEnergy(topology, field);

                Assert.Greater(energy, 0.999f,
                    $"a flat surface must comb to parallel; energy {energy:F6}");
                Assert.Less(field.lastResidual, 1e-4f, "plane field did not converge");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Sphere_RemovesMostOfTheErrorFromNoise()
        {
            const int k_Seed = 12345;

            var mesh = MeshCorpus.Icosphere(3, 1f);
            var topology = RemeshTopology.Build(mesh);

            try
            {
                // Zero iterations returns the initialisation untouched, giving the noise baseline.
                var initial = OrientationField.Solve(topology, 0, null, k_Seed);
                var solved = OrientationField.Solve(topology, 300, null, k_Seed);

                var initialEnergy = FieldDiagnostics.SmoothnessEnergy(topology, initial);
                var finalEnergy = FieldDiagnostics.SmoothnessEnergy(topology, solved);
                var removed = FieldDiagnostics.ErrorRemoved(initialEnergy, finalEnergy);

                Assert.Greater(removed, 0.7f,
                    $"removed only {removed:P1} of achievable error " +
                    $"({initialEnergy:F4} -> {finalEnergy:F4})");
                Assert.Greater(finalEnergy, 0.98f, $"sphere field is not smooth ({finalEnergy:F4})");

                // A sphere can never fully converge — the hairy ball theorem forces singularities,
                // and Gauss-Seidel jitters around them permanently. A plateau near 1e-3 is the
                // expected outcome, not a defect.
                Assert.Less(solved.lastResidual, 5e-3f, "sphere field did not settle");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Directions_AreUnitLengthAndTangent()
        {
            var mesh = MeshCorpus.Icosphere(3, 1f);
            var topology = RemeshTopology.Build(mesh);

            try
            {
                var field = OrientationField.Solve(topology, 50);

                for (var i = 0; i < topology.vertexCount; i++)
                {
                    Assert.That(field.directions[i].magnitude, Is.EqualTo(1f).Within(1e-3f),
                        $"direction {i} is not unit length");
                    Assert.That(Vector3.Dot(field.directions[i], topology.normals[i]),
                        Is.EqualTo(0f).Within(1e-3f),
                        $"direction {i} is not in the tangent plane");
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Compat4_IsSymmetricAndPicksBestRepresentative()
        {
            var n = Vector3.up;

            // Two crosses 80 degrees apart in the same plane. Under 4-fold symmetry the best
            // representatives are 10 degrees apart, not 80.
            var q0 = Vector3.right;
            var q1 = Quaternion.AngleAxis(80f, n) * Vector3.right;

            OrientationField.Compat4(q0, n, q1, n, out var a, out var b);

            var angle = Vector3.Angle(a, b);
            Assert.Less(angle, 11f, $"Compat4 left the pair {angle:F2} degrees apart");
            Assert.That(a.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(b.magnitude, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void Compat4_TreatsAllFourRotationsAsEquivalent()
        {
            var n = Vector3.up;
            var q0 = Vector3.right;

            // Every 90-degree rotation of q1 must produce the same agreement score, or the
            // symmetry is not being honoured and the field will tear at seams.
            var scores = new float[4];
            for (var k = 0; k < 4; k++)
            {
                var q1 = Quaternion.AngleAxis(37f + 90f * k, n) * Vector3.right;
                OrientationField.Compat4(q0, n, q1, n, out var a, out var b);
                scores[k] = Vector3.Dot(a, b);
            }

            for (var k = 1; k < 4; k++)
                Assert.That(scores[k], Is.EqualTo(scores[0]).Within(1e-4f),
                    $"rotation {k * 90} degrees scored differently — 4-RoSy symmetry is broken");
        }
    }
}
