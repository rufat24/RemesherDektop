// The integer lattice primitives. These matter more than their size suggests: the whole extraction
// rewrite is built on CompatIndex/Classify, and the reason the old extractor produced almost no
// quads is that it decided the same question with a Euclidean distance threshold, which cannot tell
// "one lattice step apart" (a real quad edge) from "a diagonal" or "three cells away" (meaningless).
//
// Every case below is set up on a flat plane with an axis-aligned cross, so the correct integer
// answer can be worked out by hand rather than taken from the code under test.

using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class LatticeTests
    {
        const float k_Scale = 2f;
        const float k_InvScale = 1f / k_Scale;

        static readonly Vector3 k_Normal = Vector3.up;
        static readonly Vector3 k_Q = Vector3.right;
        // Rotate90(q, n) == cross(n, q) == cross(up, right) == (0, 0, -1)
        static readonly Vector3 k_T = Vector3.Cross(Vector3.up, Vector3.right);

        static PositionField.EdgeClass ClassifyPair(Vector3 p0, Vector3 o0, Vector3 p1, Vector3 o1)
        {
            PositionField.CompatIndex(
                p0, k_Normal, k_Q, o0,
                p1, k_Normal, k_Q, o1,
                k_Scale, k_InvScale,
                out var shift0, out var shift1, out _);

            return PositionField.Classify(shift0, shift1);
        }

        [Test]
        public void Rotate90_MatchesTheReferenceConvention()
        {
            // The reference computes t = n.cross(q). Everything about the integer coordinates
            // depends on this being the same handedness, so pin it down.
            Assert.AreEqual(Vector3.Cross(k_Normal, k_Q), OrientationField.Rotate90(k_Q, k_Normal));
            Assert.That(Vector3.Dot(k_T, k_Q), Is.EqualTo(0f).Within(1e-6f));
            Assert.That(k_T.magnitude, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void SameLatticePoint_ClassifiesAsCollapse()
        {
            // Two nearby vertices whose lattice points coincide: this is the pair the extractor
            // must merge into a single output vertex.
            var result = ClassifyPair(
                Vector3.zero, Vector3.zero,
                new Vector3(0.3f * k_Scale, 0f, 0f), Vector3.zero);

            Assert.AreEqual(PositionField.EdgeClass.Collapse, result);
        }

        [Test]
        public void OneStepAlongQ_ClassifiesAsEdge()
        {
            // Exactly one lattice step apart along the first axis. This is a genuine quad edge and
            // is the case the old Euclidean test rejected: with the anchoring snap in place these
            // two points sit a full `scale` apart, far outside a 0.45*scale merge threshold, so the
            // extractor saw them as unrelated rather than as adjacent.
            var result = ClassifyPair(
                Vector3.zero, Vector3.zero,
                new Vector3(k_Scale, 0f, 0f), new Vector3(k_Scale, 0f, 0f));

            Assert.AreEqual(PositionField.EdgeClass.Edge, result);
        }

        [Test]
        public void OneStepAlongT_ClassifiesAsEdge()
        {
            var p1 = k_T * k_Scale;
            var result = ClassifyPair(Vector3.zero, Vector3.zero, p1, p1);

            Assert.AreEqual(PositionField.EdgeClass.Edge, result);
        }

        [Test]
        public void Diagonal_ClassifiesAsDiscard()
        {
            // The (1,1) case. On a quad lattice a diagonal is not an edge, and admitting it is
            // precisely what makes face tracing close a triangle where it should have closed a
            // quad — the single largest cause of the ~3% quad fraction.
            var p1 = k_Q * k_Scale + k_T * k_Scale;
            var result = ClassifyPair(Vector3.zero, Vector3.zero, p1, p1);

            Assert.AreEqual(PositionField.EdgeClass.Discard, result);
        }

        [Test]
        public void TwoStepsApart_ClassifiesAsDiscard()
        {
            var p1 = k_Q * (3f * k_Scale);
            var result = ClassifyPair(Vector3.zero, Vector3.zero, p1, p1);

            Assert.AreEqual(PositionField.EdgeClass.Discard, result);
        }

        [Test]
        public void Classify_CoversTheFullIntegerNeighbourhood()
        {
            // Exhaustive over a 5x5 block of integer offsets, so the rule cannot drift.
            for (var dx = -2; dx <= 2; dx++)
            for (var dy = -2; dy <= 2; dy++)
            {
                var result = PositionField.Classify(Vector2Int.zero, new Vector2Int(dx, dy));

                var ax = Mathf.Abs(dx);
                var ay = Mathf.Abs(dy);

                PositionField.EdgeClass expected;
                if (ax == 0 && ay == 0) expected = PositionField.EdgeClass.Collapse;
                else if (Mathf.Max(ax, ay) > 1 || (ax == 1 && ay == 1)) expected = PositionField.EdgeClass.Discard;
                else expected = PositionField.EdgeClass.Edge;

                Assert.AreEqual(expected, result, $"offset ({dx},{dy})");
            }
        }

        [Test]
        public void CompatIndex_AgreesWithCompat()
        {
            // The two functions are two views of one computation: Compat returns the chosen lattice
            // points, CompatIndex returns their integer coordinates. Reconstructing positions from
            // the indices must reproduce Compat exactly, or one of them is wrong.
            var rng = new System.Random(7);

            for (var trial = 0; trial < 200; trial++)
            {
                Vector3 Random3(float spread) => new Vector3(
                    (float)(rng.NextDouble() * 2 - 1) * spread,
                    (float)(rng.NextDouble() * 2 - 1) * spread,
                    (float)(rng.NextDouble() * 2 - 1) * spread);

                var n0 = Random3(1f).normalized;
                var n1 = (n0 + Random3(0.3f)).normalized;
                if (n0.sqrMagnitude < 0.1f || n1.sqrMagnitude < 0.1f) continue;

                var q0 = OrientationField.ArbitraryTangent(n0);
                var q1 = OrientationField.ArbitraryTangent(n1);

                var p0 = Random3(3f);
                var p1 = p0 + Random3(k_Scale);
                var o0 = p0 + Random3(k_Scale * 0.5f);
                var o1 = p1 + Random3(k_Scale * 0.5f);

                // Keep both lattice points in their own tangent planes, as the solver does.
                o0 -= n0 * Vector3.Dot(n0, o0 - p0);
                o1 -= n1 * Vector3.Dot(n1, o1 - p1);

                PositionField.Compat(p0, n0, q0, o0, p1, n1, q1, o1,
                    k_Scale, k_InvScale, out var a, out var b);

                PositionField.CompatIndex(p0, n0, q0, o0, p1, n1, q1, o1,
                    k_Scale, k_InvScale, out var shift0, out var shift1, out var error);

                var t0 = OrientationField.Rotate90(q0, n0);
                var t1 = OrientationField.Rotate90(q1, n1);

                var rebuiltA = o0 + (q0 * shift0.x + t0 * shift0.y) * k_Scale;
                var rebuiltB = o1 + (q1 * shift1.x + t1 * shift1.y) * k_Scale;

                Assert.Less(Vector3.Distance(a, rebuiltA), 1e-3f * k_Scale,
                    $"trial {trial}: side 0 index does not reproduce Compat's point");
                Assert.Less(Vector3.Distance(b, rebuiltB), 1e-3f * k_Scale,
                    $"trial {trial}: side 1 index does not reproduce Compat's point");

                // error is the squared distance between the chosen pair.
                Assert.That(Mathf.Sqrt(error), Is.EqualTo(Vector3.Distance(a, b)).Within(1e-3f * k_Scale),
                    $"trial {trial}: reported error disagrees with the chosen pair's distance");
            }
        }

        [Test]
        public void Compat_FindsTheJointMinimumNotIndependentRounding()
        {
            // Regression guard for a real defect. Compat used to round each side to its own nearest
            // cell independently, which optimises each point's distance to the midpoint rather than
            // the distance between the two points — the quantity that actually matters, since the
            // entire purpose is to make neighbouring lattices agree. The joint 4x4 search can never
            // do worse than independent rounding, so assert exactly that.
            var rng = new System.Random(11);
            var improvements = 0;

            for (var trial = 0; trial < 400; trial++)
            {
                var n = Vector3.up;
                var q = Vector3.right;
                var t = OrientationField.Rotate90(q, n);

                var p0 = new Vector3((float)rng.NextDouble() * 4f, 0f, (float)rng.NextDouble() * 4f);
                var p1 = p0 + new Vector3((float)(rng.NextDouble() * 2 - 1) * k_Scale, 0f,
                    (float)(rng.NextDouble() * 2 - 1) * k_Scale);
                var o0 = p0 + new Vector3((float)(rng.NextDouble() * 2 - 1), 0f, (float)(rng.NextDouble() * 2 - 1));
                var o1 = p1 + new Vector3((float)(rng.NextDouble() * 2 - 1), 0f, (float)(rng.NextDouble() * 2 - 1));

                PositionField.Compat(p0, n, q, o0, p1, n, q, o1,
                    k_Scale, k_InvScale, out var jointA, out var jointB);

                var middle = PositionField.MiddlePoint(p0, n, p1, n);
                var roundA = PositionField.RoundToLattice(o0, q, n, middle, k_Scale, k_InvScale);
                var roundB = PositionField.RoundToLattice(o1, q, n, middle, k_Scale, k_InvScale);

                var jointGap = Vector3.Distance(jointA, jointB);
                var roundGap = Vector3.Distance(roundA, roundB);

                Assert.LessOrEqual(jointGap, roundGap + 1e-4f,
                    $"trial {trial}: joint search ({jointGap:F4}) did worse than independent " +
                    $"rounding ({roundGap:F4})");

                if (jointGap < roundGap - 1e-4f) improvements++;
            }

            // If the two were equivalent this would be zero and the change would be pointless.
            Assert.Greater(improvements, 0,
                "joint minimisation never beat independent rounding — the cases are not being exercised");
        }

        [Test]
        public void FloorIndex_AgreesWithFloorToLattice()
        {
            var n = new Vector3(0.2f, 0.9f, -0.1f).normalized;
            var q = OrientationField.ArbitraryTangent(n);
            var t = OrientationField.Rotate90(q, n);

            var o = new Vector3(0.4f, 0f, -1.2f);
            var target = new Vector3(3.7f, 0.2f, 2.1f);

            var index = PositionField.FloorIndex(o, q, n, target, k_InvScale);
            var point = PositionField.FloorToLattice(o, q, n, target, k_Scale, k_InvScale);
            var rebuilt = o + (q * index.x + t * index.y) * k_Scale;

            Assert.Less(Vector3.Distance(point, rebuilt), 1e-4f * k_Scale);
        }

        [Test]
        public void RoundToLattice_LandsWithinHalfACellOfTheTarget()
        {
            // This is the anchoring behaviour that confines o_i to a half-cell box around its own
            // vertex. It is faithful to the reference, and it is also why the raw position residual
            // is uninformative: the representative is re-picked every sweep. Pin the behaviour so
            // nobody "fixes" it on the strength of that residual again.
            var n = Vector3.up;
            var q = Vector3.right;
            var t = OrientationField.Rotate90(q, n);

            var rng = new System.Random(3);
            for (var trial = 0; trial < 100; trial++)
            {
                var target = new Vector3((float)rng.NextDouble() * 10f, 0f, (float)rng.NextDouble() * 10f);
                var o = new Vector3((float)rng.NextDouble() * 10f, 0f, (float)rng.NextDouble() * 10f);

                var snapped = PositionField.RoundToLattice(o, q, n, target, k_Scale, k_InvScale);
                var d = snapped - target;

                Assert.LessOrEqual(Mathf.Abs(Vector3.Dot(q, d)), k_Scale * 0.5f + 1e-4f);
                Assert.LessOrEqual(Mathf.Abs(Vector3.Dot(t, d)), k_Scale * 0.5f + 1e-4f);
            }
        }
    }
}
