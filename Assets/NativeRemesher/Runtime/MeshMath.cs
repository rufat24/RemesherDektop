// Normalisation that survives small triangles.
//
// UnityEngine.Vector3.normalized returns the ZERO VECTOR when the vector's magnitude is at or
// below Vector3.kEpsilon = 1e-5. That is a reasonable guard for gameplay directions and a trap for
// mesh geometry, because the natural quantity to normalise here is a cross product of two mesh
// edges, whose magnitude is twice a triangle's area rather than anything like a unit length.
//
// The reference input in Benchmarks/reference is 1,500,000 triangles over a surface area of about
// 0.85, so a triangle covers 5.7e-7 and its cross product has magnitude 1.1e-6 — a full order of
// magnitude under the threshold. Every face normal on that mesh came back as (0,0,0).
//
// What that cost, precisely, before this file existed:
//
//   * PreDecimate built every quadric plane from a zero normal, so the 3x3 system was degenerate
//     while still passing its singularity test. The "optimal" point landed a mean of 2.2e8 edge
//     lengths away, and the drift guard bounced 2,243,585 of 2,250,000 candidates.
//   * The normal-flip guard compared Dot(before.normalized, after.normalized), got Dot(zero, zero)
//     = 0, and read that as a 90-degree rotation. It rejected all 2,247,091 remaining candidates.
//   * So decimation applied ZERO collapses to a mesh Blender coarsens in seconds, the field then
//     solved at 10.9x the lattice spacing it is documented to work at, and the extractor emitted a
//     triangle graph. The tool was reported as "definitely decimation and 0 retopology", which is
//     what it was: the quad tracer found 81 quads in 7,864 faces and everything quad-shaped in the
//     output came from gluing triangle pairs back together afterwards.
//
// Note how well this hid. Every guard in the way tests sqrMagnitude against 1e-20, which a
// magnitude of 1.1e-6 clears by fourteen orders of magnitude; the vector was not degenerate, only
// small. And it is scale-dependent, so no procedural corpus mesh could catch it — a 320k-triangle
// unit icosphere has cross magnitudes near 7.9e-5, comfortably above the threshold. It needed a
// real scan at a real scale.
//
// Everything here works in double for the length, because the squared magnitude of a genuinely
// tiny cross product underflows float long before the direction stops being meaningful.

using System;
using UnityEngine;

namespace NativeRemesher
{
    public static class MeshMath
    {
        /// <summary>
        /// Unit vector in the direction of <paramref name="v"/>, or <paramref name="fallback"/> if
        /// it has no direction at all. Never silently returns zero.
        /// </summary>
        public static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            double x = v.x, y = v.y, z = v.z;
            var lengthSquared = x * x + y * y + z * z;

            if (!(lengthSquared > 0.0)) return fallback;   // catches zero and NaN

            var inverse = 1.0 / Math.Sqrt(lengthSquared);
            return new Vector3((float)(x * inverse), (float)(y * inverse), (float)(z * inverse));
        }

        /// <summary>Unit face normal, falling back to <paramref name="fallback"/> on a degenerate triangle.</summary>
        public static Vector3 SafeFaceNormal(Vector3 a, Vector3 b, Vector3 c, Vector3 fallback)
            => SafeNormalize(Vector3.Cross(b - a, c - a), fallback);

        /// <summary>
        /// Cosine of the angle between two vectors that are NOT unit length, without normalising
        /// either of them.
        /// </summary>
        /// <remarks>
        /// This is the form to use when comparing two face normals against an angular threshold.
        /// Normalising first is what turned the flip guard into a reject-everything test: the
        /// division happens in double here, and the magnitudes never have to survive a round trip
        /// through a float unit vector.
        ///
        /// Returns 1 — read as "no rotation" — when either vector has no direction. Degeneracy is
        /// not an angle, and every caller in this project handles it separately and before this
        /// call, so the permissive answer keeps the guard from firing on a case that is already
        /// decided elsewhere.
        /// </remarks>
        public static float CosAngleBetween(Vector3 a, Vector3 b)
        {
            double ax = a.x, ay = a.y, az = a.z;
            double bx = b.x, by = b.y, bz = b.z;

            var lengthA = Math.Sqrt(ax * ax + ay * ay + az * az);
            var lengthB = Math.Sqrt(bx * bx + by * by + bz * bz);

            if (!(lengthA > 0.0) || !(lengthB > 0.0)) return 1f;

            var cos = (ax * bx + ay * by + az * bz) / (lengthA * lengthB);
            return (float)(cos < -1.0 ? -1.0 : cos > 1.0 ? 1.0 : cos);
        }
    }
}
