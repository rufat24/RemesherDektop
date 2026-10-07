// Pins the exact trap MeshMath exists for.
//
// UnityEngine.Vector3.normalized returns the zero vector below a magnitude of 1e-5. That is not a
// hypothetical: a mesh with 1.5M triangles over a surface area of 0.85 has cross products of
// magnitude 1.1e-6, and the first test below is the smallest possible reproduction of the bug that
// made pre-decimation apply zero collapses to such a mesh — the whole reason its output was, as
// reported, "definitely decimation and 0 retopology".
//
// The first test asserts Unity's behaviour rather than ours. It documents WHY the rest of the file
// exists, and it fails loudly if a future Unity version changes kEpsilon, which would otherwise
// turn every SafeNormalize call site into unexplained defensive noise.

using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class MeshMathTests
    {
        /// <summary>The premise. If this ever fails, MeshMath's justification has changed.</summary>
        [Test]
        public void UnityNormalizedCollapsesSmallVectorsToZero()
        {
            // Twice the area of a triangle on the reference scan: 1.5M triangles over area ~0.85.
            var tiny = new Vector3(1.1e-6f, 0f, 0f);

            Assert.That(tiny.sqrMagnitude, Is.GreaterThan(1e-20f),
                "the vector is small, not degenerate — every guard in this project let it through");
            Assert.That(tiny.normalized, Is.EqualTo(Vector3.zero),
                "Unity zeroes anything under 1e-5 magnitude; this is the trap");
        }

        [Test]
        public void SafeNormalizeSurvivesWhatUnityZeroes()
        {
            var tiny = new Vector3(1.1e-6f, 0f, 0f);
            var unit = MeshMath.SafeNormalize(tiny, Vector3.up);

            Assert.That(unit, Is.EqualTo(Vector3.right).Using(new Vector3Comparer(1e-5f)));
        }

        [Test]
        public void SafeNormalizeSurvivesFloatUnderflowOfTheSquaredLength()
        {
            // 1e-25 squared is 1e-50, which is zero in float. Computing the length in double is
            // what keeps this from being a divide by zero.
            var minute = new Vector3(0f, 3e-25f, 4e-25f);
            var unit = MeshMath.SafeNormalize(minute, Vector3.up);

            Assert.That(unit.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(unit, Is.EqualTo(new Vector3(0f, 0.6f, 0.8f)).Using(new Vector3Comparer(1e-3f)));
        }

        [Test]
        public void SafeNormalizeFallsBackOnlyOnAGenuineZero()
        {
            Assert.That(MeshMath.SafeNormalize(Vector3.zero, Vector3.up), Is.EqualTo(Vector3.up));
            Assert.That(MeshMath.SafeNormalize(new Vector3(float.NaN, 0f, 0f), Vector3.up),
                Is.EqualTo(Vector3.up));
        }

        /// <summary>
        /// The flip guard's actual failure: two parallel small vectors read as 90 degrees apart.
        /// </summary>
        [Test]
        public void CosAngleBetweenIsScaleInvariant()
        {
            var a = new Vector3(1.1e-6f, 0f, 0f);
            var b = new Vector3(1.1e-6f, 0f, 0f);

            Assert.That(Vector3.Dot(a.normalized, b.normalized), Is.EqualTo(0f),
                "what the guard used to compute: two identical directions scoring as perpendicular");
            Assert.That(MeshMath.CosAngleBetween(a, b), Is.EqualTo(1f).Within(1e-6f),
                "and what it means");
        }

        [Test]
        public void CosAngleBetweenMatchesTheAngleAtAnyScale()
        {
            // 60 degrees apart, at a scale where normalising first destroys both vectors.
            var a = new Vector3(1e-6f, 0f, 0f);
            var b = new Vector3(0.5e-6f, 0.8660254e-6f, 0f);

            Assert.That(MeshMath.CosAngleBetween(a, b), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(MeshMath.CosAngleBetween(a, -a), Is.EqualTo(-1f).Within(1e-6f));
        }

        [Test]
        public void CosAngleBetweenReportsNoRotationForADegenerateSide()
        {
            // Permissive by design: every caller decides degeneracy separately and earlier, so the
            // guard must not fire on a case that is already handled.
            Assert.That(MeshMath.CosAngleBetween(Vector3.zero, Vector3.right), Is.EqualTo(1f));
        }

        /// <summary>
        /// A dense unit-scale triangle, the size that actually appears on a photogrammetry scan:
        /// the normal must come out unit length, not zero.
        /// </summary>
        [Test]
        public void SafeFaceNormalWorksOnAScanScaleTriangle()
        {
            const float edge = 1.1e-3f;   // reference scan average edge length
            var a = new Vector3(0.5f, 0.5f, 0.5f);
            var b = a + new Vector3(edge, 0f, 0f);
            var c = a + new Vector3(0f, edge, 0f);

            Assert.That(Vector3.Cross(b - a, c - a).normalized, Is.EqualTo(Vector3.zero),
                "the cross product is 1.2e-6 long, which Unity zeroes");

            var normal = MeshMath.SafeFaceNormal(a, b, c, Vector3.up);
            Assert.That(normal, Is.EqualTo(Vector3.forward).Using(new Vector3Comparer(1e-4f)));
        }

        [Test]
        public void SafeFaceNormalFallsBackOnACollinearTriangle()
        {
            var a = Vector3.zero;
            var b = new Vector3(1f, 0f, 0f);
            var c = new Vector3(2f, 0f, 0f);

            Assert.That(MeshMath.SafeFaceNormal(a, b, c, Vector3.up), Is.EqualTo(Vector3.up));
        }

        sealed class Vector3Comparer : System.Collections.Generic.IEqualityComparer<Vector3>
        {
            readonly float m_Tolerance;
            public Vector3Comparer(float tolerance) => m_Tolerance = tolerance;

            public bool Equals(Vector3 a, Vector3 b) => (a - b).magnitude <= m_Tolerance;
            public int GetHashCode(Vector3 v) => v.GetHashCode();
        }
    }
}
