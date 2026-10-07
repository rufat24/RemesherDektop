// Tests for the hexagon re-tiling pass.
//
// The invariants here are the ones whose violation is silent. A non-manifold edge renders fine in
// Unity and breaks every downstream tool; a reversed winding reads as a lighting bug; a lone
// triangle that gets "fixed" means the parity argument was wrong somewhere. None of those show up
// in a face count, so each is asserted directly.

using System.Collections.Generic;
using NUnit.Framework;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class TrianglePairingTests
    {
        /// <summary>
        /// Two triangles either side of one quad, sharing opposite edges of it.
        /// </summary>
        /// <remarks>
        ///     0---1---2---3      quad  (1,2,5,4)
        ///     |  /|   |\  |      T1    (0,1,4)     sharing edge (1,4)
        ///     | / | Q | \ |      T2    (2,3,5)     sharing edge (2,5)
        ///     |/  |   |  \|
        ///     4---5---6---7
        ///
        /// Laid out flat in the xy plane so the quality scores are unambiguous and any winding
        /// error is a sign flip rather than a judgement call.
        /// </remarks>
        static void HexagonFixture(out List<int[]> faces, out Vector3[] positions)
        {
            positions = new[]
            {
                new Vector3(0f, 1f, 0f), // 0
                new Vector3(1f, 1f, 0f), // 1
                new Vector3(2f, 1f, 0f), // 2
                new Vector3(3f, 1f, 0f), // 3
                new Vector3(0f, 0f, 0f), // 4
                new Vector3(1f, 0f, 0f), // 5
                new Vector3(2f, 0f, 0f), // 6
                new Vector3(3f, 0f, 0f), // 7
            };

            faces = new List<int[]>
            {
                new[] { 0, 1, 4 },       // T1
                new[] { 1, 2, 5, 4 },    // Q
                new[] { 2, 3, 5 },       // T2 - shares edge (2,5) with Q
            };
        }

        [Test]
        public void AHexagonPatchBecomesTwoQuads()
        {
            HexagonFixture(out var faces, out var positions);

            var stats = TrianglePairing.Run(faces, positions);

            Assert.AreEqual(2, stats.trianglesBefore);
            Assert.AreEqual(0, stats.trianglesAfter, stats.ToString());
            Assert.AreEqual(1, stats.patchesApplied, stats.ToString());

            // Three faces in, two out: the pass trades a face for the two triangles.
            Assert.AreEqual(2, faces.Count, stats.ToString());
            foreach (var face in faces)
                Assert.AreEqual(4, face.Length, "every output face must be a quad");
        }

        [Test]
        public void ARingOfFacesIsNotRetiledAsThoughItHadOneBoundary()
        {
            // A patch whose faces close a RING has two boundary loops, not one - an outer and an
            // inner - and no cover of "the boundary" is meaningful. The check for that was walking
            // the boundary edges and asking whether it came back to where it started after using
            // them all, which two loops OF THE SAME LENGTH satisfy perfectly: the walk goes round
            // the first one twice and never visits the second.
            //
            // What that cost, found by the corpus rather than by reading the code: three boundary
            // edges on a closed icosphere where there had been none. One region got a degenerate
            // double cover and the other got nothing, which is a hole.
            //
            // The fixture is the smallest thing that does it - a three-sided tube segment, whose
            // two rims are both three edges long.
            var positions = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0.5f, 0.87f, 0f),
                new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 1f), new Vector3(0.5f, 0.87f, 1f)
            };

            var faces = new List<int[]>
            {
                new[] { 0, 1, 4, 3 },
                new[] { 1, 2, 5, 4 },
                new[] { 2, 0, 3, 5 }
            };

            var before = OpenEdges(faces);
            var stats = TrianglePairing.Run(faces, positions);

            Assert.AreEqual(before, OpenEdges(faces),
                "the pass opened a hole: a ring was re-tiled as though its boundary were one loop");
            Assert.AreEqual(before, stats.openEdgesBefore, "the pass miscounted the rims it started with");
            Assert.AreEqual(stats.openEdgesBefore, stats.openEdgesAfter,
                "the pass reported opening a hole");
        }

        /// <summary>Edges with other than two incident faces.</summary>
        static int OpenEdges(List<int[]> faces)
        {
            var uses = new Dictionary<(int, int), int>();
            foreach (var face in faces)
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    var key = (Mathf.Min(a, b), Mathf.Max(a, b));
                    uses.TryGetValue(key, out var used);
                    uses[key] = used + 1;
                }

            var open = 0;
            foreach (var used in uses.Values)
                if (used != 2) open++;
            return open;
        }

        [Test]
        public void ALoneTriangleIsLeftAlone()
        {
            // Parity: a triangle plus its quad neighbours bounds a region with an odd number of
            // boundary edges, which admits no all-quad tiling. If this ever "succeeds", the parity
            // reasoning the whole pass rests on is wrong and the output is not what it claims.
            var positions = new[]
            {
                new Vector3(0f, 1f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(2f, 1f, 0f),
                new Vector3(2f, 0f, 0f),
            };

            var faces = new List<int[]>
            {
                new[] { 0, 1, 2 },
                new[] { 1, 4, 5, 3 },
            };

            var stats = TrianglePairing.Run(faces, positions);

            Assert.AreEqual(1, stats.trianglesBefore);
            Assert.AreEqual(1, stats.trianglesAfter, "a lone triangle cannot be paired away");
            Assert.AreEqual(0, stats.patchesApplied);
            Assert.AreEqual(2, faces.Count, "nothing should have been rewritten");
        }

        [Test]
        public void NoEdgeEndsUpSharedByThreeFaces()
        {
            // The bug this catches was real and was found in the field, not in a test: the diagonal
            // introduced by a split can already exist elsewhere in the mesh, and adding it gives an
            // edge with three faces. It produced exactly one such edge on ccnb.
            HexagonFixture(out var faces, out var positions);
            TrianglePairing.Run(faces, positions);

            AssertManifold(faces);
        }

        [Test]
        public void ARetilingIsRefusedWhenEveryDiagonalAlreadyExists()
        {
            // The patch's three long diagonals are 0-3, 1-5 and 2-4. Each is blocked here by a face
            // built on FRESH vertices, so the input stays manifold and the only thing being tested
            // is the refusal - an earlier version of this test blocked them with faces that reused
            // patch vertices, which made the input itself non-manifold and the assertion meaningless.
            HexagonFixture(out var faces, out var positions);

            var extended = new List<Vector3>(positions);
            for (var i = 0; i < 6; i++) extended.Add(new Vector3(10f + i, 0f, 0f));
            positions = extended.ToArray();

            faces.Add(new[] { 0, 3, 8, 9 });
            faces.Add(new[] { 1, 5, 10, 11 });
            faces.Add(new[] { 2, 4, 12, 13 });

            AssertManifold(faces); // the fixture itself must be sound before it proves anything

            var before = faces.Count;
            var stats = TrianglePairing.Run(faces, positions);

            Assert.AreEqual(2, stats.trianglesAfter,
                "with no legal diagonal the triangles must survive: " + stats);
            Assert.AreEqual(0, stats.patchesApplied, stats.ToString());
            Assert.AreEqual(before, faces.Count, "nothing should have been rewritten");
            AssertManifold(faces);
        }

        [Test]
        public void TheOutputKeepsTheInputWinding()
        {
            // Compared against the INPUT's own handedness rather than against a fixed sign. The
            // fixture happens to be wound clockwise in the xy plane, and asserting "positive"
            // failed the pass for preserving that correctly - the invariant is that the output
            // agrees with the input, not that either is counter-clockwise.
            HexagonFixture(out var faces, out var positions);

            var inputSign = Mathf.Sign(SignedArea(faces[1], positions)); // the quad
            Assert.AreNotEqual(0f, inputSign, "degenerate fixture");

            TrianglePairing.Run(faces, positions);

            foreach (var face in faces)
            {
                var area = SignedArea(face, positions);
                Assert.AreEqual(inputSign, Mathf.Sign(area),
                    "an output face is wound opposite to the input patch");
            }
        }

        static float SignedArea(int[] face, Vector3[] positions)
        {
            var area = 0f;
            for (var k = 0; k < face.Length; k++)
            {
                var a = positions[face[k]];
                var b = positions[face[(k + 1) % face.Length]];
                area += a.x * b.y - b.x * a.y;
            }

            return area;
        }

        [Test]
        public void RunningItTwiceChangesNothingTheSecondTime()
        {
            // Idempotence. The pass consumes each face at most once per run, so a second run has no
            // hexagon patches left to find - and if it does, the first run left a triangle pair it
            // should have taken, which is a real defect rather than a missed optimisation.
            HexagonFixture(out var faces, out var positions);
            TrianglePairing.Run(faces, positions);

            var faceCount = faces.Count;
            var second = TrianglePairing.Run(faces, positions);

            Assert.AreEqual(0, second.patchesApplied, second.ToString());
            Assert.AreEqual(faceCount, faces.Count);
        }

        [Test]
        public void AnEmptyOrTriangleFreeMeshIsHandledWithoutThrowing()
        {
            var positions = new[]
            {
                Vector3.zero, Vector3.right, Vector3.one, Vector3.up,
            };

            var empty = new List<int[]>();
            Assert.AreEqual(0, TrianglePairing.Run(empty, positions).patchesApplied);

            var quadOnly = new List<int[]> { new[] { 0, 1, 2, 3 } };
            var stats = TrianglePairing.Run(quadOnly, positions);
            Assert.AreEqual(0, stats.trianglesBefore);
            Assert.AreEqual(1, quadOnly.Count);
        }

        static void AssertManifold(List<int[]> faces)
        {
            var edgeUse = new Dictionary<long, int>();

            foreach (var face in faces)
            for (var k = 0; k < face.Length; k++)
            {
                var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                edgeUse.TryGetValue(key, out var used);
                edgeUse[key] = used + 1;
            }

            foreach (var entry in edgeUse)
                Assert.LessOrEqual(entry.Value, 2,
                    $"edge ({EdgeKey.First(entry.Key)},{EdgeKey.Second(entry.Key)}) is shared by " +
                    $"{entry.Value} faces");
        }
    }
}
