// Where the orientation field's singularities are, and how many.
//
// This is an instrument, not a stage: it changes no output. It exists because without it every
// claim about edge flow is unfalsifiable, and this project has already spent a session guessing at
// causes that a direct measurement would have settled in one run.
//
// The specific question it answers. Scored against the user's reference retopology of the same
// asset, our output has 1,500 valence-3 vertices where the reference has 142. That is either a
// field whose singularities really are that dense, or an extraction manufacturing irregularity out
// of a field that is fine — opposite diagnoses with opposite fixes, and the output's valence
// histogram cannot tell them apart because extraction sits between the two.
//
// A 4-RoSy field's singularities are not a choice and not stored anywhere. They are a property of
// the field: walk the three edges of a triangle, accumulate how many 90-degree steps each edge
// needed to bring its two crosses into agreement, and a non-zero total mod 4 means the cross cannot
// be combed continuously around that face. That is the definition, and it is the reference's
// compute_orientation_singularities.
//
// The index is signed and the signs are constrained: on a closed surface the indices sum to
// 4 * (2 - 2g) / 4 = 2 - 2g by Poincare-Hopf, so the total is fixed by topology alone and cannot be
// reduced by better smoothing. Only their SPREAD is a quality question — a good field puts a few
// isolated singularities where the surface demands them, a bad one scatters cancelling pairs
// everywhere. So the number to watch is not the total index but the count of singular faces, and
// the two are reported separately for exactly that reason.

using UnityEngine;

namespace NativeRemesher
{
    public sealed class SingularityStats
    {
        /// <summary>Faces whose accumulated rotation index is non-zero.</summary>
        public int singularFaces;

        /// <summary>Singular faces of index +1 and -1 (index 1 and 3 mod 4).</summary>
        public int indexPlus;
        public int indexMinus;

        /// <summary>
        /// Sum of signed indices, over the index-1 and index-3 faces only. A weak consistency
        /// check, not a proof, and not a quality measure.
        /// </summary>
        /// <remarks>
        /// For a 4-RoSy field on a closed surface this should be 4 * chi — +8 on a sphere, which is
        /// the classic eight cone points, and -8 on the genus-2 reference asset. It is only a weak
        /// check because an index-2 face has no determinable sign: +2 and -2 are the same residue
        /// mod 4, so the accumulation cannot tell them apart and they are excluded here. On the
        /// reference asset that leaves 17 faces unsigned against a measured sum of +2, which spans
        /// -32 to +36 and therefore contains -8 without confirming it. Read a wildly wrong value as
        /// a bug signal; do not read a plausible one as a pass.
        /// </remarks>
        public int indexSum;

        public int faceCount;

        /// <summary>Singular faces as a fraction of all faces. This is the quality number.</summary>
        public float singularFraction => faceCount > 0 ? singularFaces / (float)faceCount : 0f;

        public override string ToString()
        {
            return $"singular={singularFaces}/{faceCount} ({singularFraction:P2}) " +
                   $"plus={indexPlus} minus={indexMinus} indexSum={indexSum}";
        }
    }

    public static class SingularityAnalysis
    {
        /// <summary>
        /// The pair of 90-degree rotation counts that bring two crosses into closest agreement.
        /// </summary>
        /// <remarks>
        /// The integer form of <see cref="OrientationField.Compat4"/>. Compat4 returns the aligned
        /// vectors and discards how far it turned them, which is precisely the quantity a
        /// singularity is made of, so it cannot be recovered afterwards from its output.
        ///
        /// Two candidates per side suffice before signs — q and q rotated 90 — because 180 and 270
        /// are their negations. The sign flip is folded into the second index as +2, which is what
        /// makes the returned pair a rotation count in 0..3 rather than a choice of representative.
        /// </remarks>
        public static void Compat4Index(Vector3 q0, Vector3 n0, Vector3 q1, Vector3 n1,
            out int k0, out int k1)
        {
            var a0 = q0;
            var a1 = OrientationField.Rotate90(q0, n0);
            var b0 = q1;
            var b1 = OrientationField.Rotate90(q1, n1);

            var best = -1f;
            k0 = 0;
            k1 = 0;

            for (var i = 0; i < 2; i++)
            {
                var a = i == 0 ? a0 : a1;
                for (var j = 0; j < 2; j++)
                {
                    var b = j == 0 ? b0 : b1;
                    var score = Mathf.Abs(Vector3.Dot(a, b));
                    if (score <= best) continue;

                    best = score;
                    k0 = i;
                    k1 = j;
                }
            }

            var chosenA = k0 == 0 ? a0 : a1;
            var chosenB = k1 == 0 ? b0 : b1;
            if (Vector3.Dot(chosenA, chosenB) < 0f) k1 += 2;
        }

        public static SingularityStats Analyse(RemeshTopology topology, OrientationField orientation)
        {
            if (topology == null) throw new System.ArgumentNullException(nameof(topology));
            if (orientation == null) throw new System.ArgumentNullException(nameof(orientation));

            var stats = new SingularityStats { faceCount = topology.triangleCount };

            var q = orientation.directions;
            var n = topology.normals;
            var triangles = topology.triangles;

            for (var f = 0; f < topology.triangleCount; f++)
            {
                var index = 0;

                for (var k = 0; k < 3; k++)
                {
                    var i = triangles[f * 3 + k];
                    var j = triangles[f * 3 + (k == 2 ? 0 : k + 1)];

                    Compat4Index(q[i], n[i], q[j], n[j], out var k0, out var k1);
                    index += k1 - k0;
                }

                // Positive modulo: C# % keeps the sign of the dividend, and a negative accumulated
                // index is the common case going one way around a face.
                index = ((index % 4) + 4) % 4;

                if (index == 0) continue;

                stats.singularFaces++;
                if (index == 1) { stats.indexPlus++; stats.indexSum += 1; }
                else if (index == 3) { stats.indexMinus++; stats.indexSum -= 1; }
                // index == 2 is a half-turn defect. It is counted as singular and contributes
                // nothing to the signed sum, which is correct: it is not a valence-3 or valence-5
                // point and a quad mesh has no vertex that realises it.
            }

            return stats;
        }
    }
}
