// The things that had to be true for the output to shade correctly, and were not checked.
//
// Every case here was reported by the user before it was measured — "check for flipped or duplicate
// faces, check for normals direction" — and each one turned out to be a real defect in the output.
// The duplicate count on the reference asset was small (4 faces of 5,973) and entirely
// opposite-wound, which is the pair that mutually backface-culls and mottles.
//
// The cube fixtures are built deliberately inside-out, with vertex normals that lie, because that
// is the situation the extractor actually produces: winding globally consistent and globally
// inverted, and sampled input normals that cannot be trusted to break the tie.

using System.Collections.Generic;
using NativeRemesher.Extraction;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class FaceOrientationTests
    {
        static Vector3[] CubePositions() => new[]
        {
            new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f), new Vector3(0f, 1f, 0f),
            new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 1f),
            new Vector3(1f, 1f, 1f), new Vector3(0f, 1f, 1f),
        };

        /// <summary>Six quads, wound so that every normal points INTO the cube.</summary>
        static List<int[]> InsideOutCube() => new List<int[]>
        {
            new[] { 0, 1, 2, 3 },   // z = 0, wound +z
            new[] { 7, 6, 5, 4 },   // z = 1, wound -z
            new[] { 4, 5, 1, 0 },   // y = 0, wound +y
            new[] { 3, 2, 6, 7 },   // y = 1, wound -y
            new[] { 0, 3, 7, 4 },   // x = 0, wound +x
            new[] { 5, 6, 2, 1 },   // x = 1, wound -x
        };

        static double Volume(List<int[]> faces, Vector3[] positions)
        {
            var total = 0.0;
            foreach (var face in faces)
            {
                var a = positions[face[0]];
                for (var i = 1; i + 1 < face.Length; i++)
                {
                    var b = positions[face[i]];
                    var c = positions[face[i + 1]];
                    total += Vector3.Dot(a, Vector3.Cross(b, c));
                }
            }
            return total / 6.0;
        }

        /// <summary>
        /// The signed-volume test has to win over the input normals, not average with them. Here
        /// every vertex normal points inward, which is exactly what a badly sampled surface normal
        /// looks like, and a vote would keep the cube inside-out.
        /// </summary>
        [Test]
        public void ClosedComponentIsOrientedOutwardDespiteLyingNormals()
        {
            var positions = CubePositions();
            var faces = InsideOutCube();

            var centre = new Vector3(0.5f, 0.5f, 0.5f);
            var normals = new Vector3[positions.Length];
            for (var i = 0; i < normals.Length; i++)
                normals[i] = (centre - positions[i]).normalized;   // inward

            Assert.Less(Volume(faces, positions), 0.0, "fixture should start inside-out");

            var stats = FaceOrienter.Run(faces, positions, normals);

            Assert.AreEqual(1, stats.components);
            Assert.AreEqual(1, stats.componentsClosed, "a cube is a closed shell");
            Assert.Greater(Volume(faces, positions), 0.0, "signed volume must end positive");
            Assert.AreEqual(0, stats.inconsistentEdges);
        }

        /// <summary>
        /// A component too small to read its own surface normals must not decide its own sign.
        /// </summary>
        /// <remarks>
        /// Measured on the reference asset: GameAsset extraction fragmented the surface into 13
        /// components, flipped 12, and left the thirteenth — three faces — pointing the other way.
        /// An inverted patch inside a surface is as visible as a hole, and it was decided by
        /// rounding, because three slivers of a coarse output carry no coherent direction.
        ///
        /// The fixture reproduces that exactly. A large patch whose normals disagree with its
        /// winding by a full 180 degrees, so it is confident and must flip; and a detached
        /// three-triangle patch, wound the same way, whose normals lie almost in its own plane so
        /// its mean cosine is +0.05 — the wrong sign, weakly held. Before the confidence test the
        /// small patch kept that +0.05 and came out inverted relative to everything around it.
        /// </remarks>
        [Test]
        public void AComponentTooSmallToReadItsNormalsFollowsTheConsensus()
        {
            var positions = new List<Vector3>();

            // A 2x2 grid of quads in z = 0, wound clockwise seen from +z, so every face normal
            // points -z while the surface normals say +z.
            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 3; col++)
                    positions.Add(new Vector3(col, row, 0f));

            var faces = new List<int[]>
            {
                new[] { 0, 3, 4, 1 }, new[] { 1, 4, 5, 2 },
                new[] { 3, 6, 7, 4 }, new[] { 4, 7, 8, 5 },
            };

            // A detached three-triangle patch, far enough away to share nothing, wound the same way.
            var detached = positions.Count;
            positions.Add(new Vector3(10f, 0f, 0f));
            positions.Add(new Vector3(11f, 0f, 0f));
            positions.Add(new Vector3(11f, 1f, 0f));
            positions.Add(new Vector3(10f, 1f, 0f));
            positions.Add(new Vector3(12f, 0f, 0f));

            faces.Add(new[] { detached + 0, detached + 3, detached + 2 });
            faces.Add(new[] { detached + 0, detached + 2, detached + 1 });
            faces.Add(new[] { detached + 1, detached + 2, detached + 4 });

            var normals = new Vector3[positions.Count];
            for (var i = 0; i < detached; i++) normals[i] = Vector3.forward;              // cos = -1
            for (var i = detached; i < normals.Length; i++)
                normals[i] = new Vector3(1f, 0f, -0.05f).normalized;                      // cos = +0.05

            var array = positions.ToArray();
            var stats = FaceOrienter.Run(faces, array, normals);

            Assert.AreEqual(2, stats.components);
            Assert.AreEqual(0, stats.componentsClosed, "both patches are open");
            Assert.AreEqual(1, stats.componentsWeakVote, "only the small patch should be unreadable");
            Assert.AreEqual(2, stats.componentsFlipped, "both must end up facing the same way");

            // The whole point: every face, in both components, now faces +z.
            foreach (var face in faces)
            {
                var normal = QuadExtractor.FaceNormal(array, face);
                Assert.Greater(normal.z, 0f,
                    "a face is still inverted relative to the rest of the surface");
            }
        }

        /// <summary>
        /// The half of the job the first version did not do. One reversed face in an otherwise
        /// consistent shell is backface-culled into what looks exactly like a hole, and no
        /// component-wide sign can fix it because the component-wide sign is already right.
        /// </summary>
        [Test]
        public void SingleReversedFaceIsMadeConsistent()
        {
            var positions = CubePositions();
            var faces = InsideOutCube();
            System.Array.Reverse(faces[3]);

            var normals = new Vector3[positions.Length];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.up;

            var stats = FaceOrienter.Run(faces, positions, normals);

            Assert.AreEqual(1, stats.facesMadeConsistent);
            Assert.AreEqual(0, stats.inconsistentEdges);
            Assert.Greater(Volume(faces, positions), 0.0);
        }

        /// <summary>
        /// Two faces on the same corners, wound opposite ways. Each culls the other from one side,
        /// so the surface dithers between shaded and black per pixel — reported as bad normals,
        /// unfixable by any change to normals.
        /// </summary>
        [Test]
        public void OppositeWoundDuplicateIsRemoved()
        {
            var positions = CubePositions();
            var faces = InsideOutCube();
            faces.Add(new[] { 3, 2, 1, 0 });   // faces[0] reflected

            var stats = FaceRepair.Run(faces, positions);

            Assert.AreEqual(1, stats.duplicatesRemoved);
            Assert.AreEqual(1, stats.oppositeWound);
            Assert.AreEqual(6, faces.Count);
            Assert.AreEqual(0, stats.overIncidentEdges);
        }

        /// <summary>A rotation of the same winding is still the same face, and still a duplicate.</summary>
        [Test]
        public void RotatedDuplicateIsRemovedAndNotCountedAsOpposite()
        {
            var positions = CubePositions();
            var faces = InsideOutCube();
            faces.Add(new[] { 2, 3, 0, 1 });   // faces[0] rotated by two

            var stats = FaceRepair.Run(faces, positions);

            Assert.AreEqual(1, stats.duplicatesRemoved);
            Assert.AreEqual(0, stats.oppositeWound);
        }

        [Test]
        public void RepeatedCornerIsCollapsedAndZeroAreaFaceIsDropped()
        {
            // Vertex 8 sits exactly on vertex 0, so the last face has three distinct indices and no
            // area — the case a repeated-index check would miss.
            var positions = new List<Vector3>(CubePositions()) { Vector3.zero }.ToArray();
            var faces = new List<int[]>
            {
                new[] { 0, 1, 1, 2, 3 },   // a zero-length edge: really the quad 0,1,2,3
                new[] { 0, 1, 8 },         // no area at all
            };

            var stats = FaceRepair.Run(faces, positions);

            Assert.AreEqual(1, stats.cornersCollapsed);
            Assert.AreEqual(1, stats.zeroAreaRemoved);
            Assert.AreEqual(1, faces.Count);
            Assert.AreEqual(4, faces[0].Length);
        }

        /// <summary>
        /// Normals must come from the faces, which is the whole point of the change: a cube's
        /// corner normal is the diagonal, whatever the input surface claimed. The fixture's input
        /// normals point inward, so passing this proves the fallback is not being used.
        /// </summary>
        [Test]
        public void ShadingNormalsFollowTheOutputGeometry()
        {
            var positions = CubePositions();
            var faces = InsideOutCube();

            var centre = new Vector3(0.5f, 0.5f, 0.5f);
            var lying = new Vector3[positions.Length];
            for (var i = 0; i < lying.Length; i++) lying[i] = (centre - positions[i]).normalized;

            FaceOrienter.Run(faces, positions, lying);
            var normals = NormalRecalculator.Compute(faces, positions, lying);

            for (var i = 0; i < normals.Length; i++)
            {
                var outward = (positions[i] - centre).normalized;
                Assert.Greater(Vector3.Dot(normals[i], outward), 0.9f,
                    $"vertex {i} should carry the outward corner diagonal");
                Assert.AreEqual(1f, normals[i].magnitude, 1e-4f);
            }
        }

        /// <summary>
        /// An open component has no inside, so volume says nothing and the input normals are the
        /// only reference there is. This is the path the scan takes.
        /// </summary>
        [Test]
        public void OpenComponentFallsBackToTheNormalVote()
        {
            var positions = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f), new Vector3(0f, 0f, 1f),
            };

            // Wound so the geometric normal is -y, while the surface says +y.
            var faces = new List<int[]> { new[] { 0, 1, 2, 3 } };
            var normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };

            var stats = FaceOrienter.Run(faces, positions, normals);

            Assert.AreEqual(0, stats.componentsClosed);
            Assert.AreEqual(1, stats.componentsFlipped);
            Assert.Greater(Vector3.Dot(QuadExtractor.FaceNormal(positions, faces[0]), Vector3.up), 0f);
        }
        // A 6 x 6 grid of quads on y = 0 wound up (+y), with the 2 x 2 block in the middle reversed.
        static void GridWithReversedBlock(out Vector3[] positions, out List<int[]> faces, out List<int> block)
        {
            const int n = 6;
            positions = new Vector3[(n + 1) * (n + 1)];
            for (var j = 0; j <= n; j++)
                for (var i = 0; i <= n; i++)
                    positions[j * (n + 1) + i] = new Vector3(i, 0f, j);
            faces = new List<int[]>();
            block = new List<int>();
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    var q = j * (n + 1) + i;
                    var face = new[] { q, q + n + 1, q + n + 2, q + 1 };
                    if (i >= 2 && i < 4 && j >= 2 && j < 4) { System.Array.Reverse(face); block.Add(faces.Count); }
                    faces.Add(face);
                }
        }

        [Test]
        public void InvertedPatchIsReversed()
        {
            // The patch the consistency walk left inside out (helmet head at 3,000 in Unity: 114
            // faces, 46 of 54 border edges inconsistent): pointing against the surface, its border
            // all inconsistent.
            GridWithReversedBlock(out var positions, out var faces, out var block);
            var normals = new Vector3[positions.Length];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            var stats = new OrientStats();
            FaceOrienter.FlipInvertedPatches(faces, positions, normals, stats);
            Assert.AreEqual(1, stats.patchesFlipped, stats.ToString());
            Assert.AreEqual(4, stats.patchFacesFlipped, stats.ToString());
            foreach (var f in faces)
                Assert.Greater(Vector3.Dot(QuadExtractor.FaceNormal(positions, f), Vector3.up), 0f);
        }

        [Test]
        public void PatchConsistentWithItsNeighboursIsLeftAlone()
        {
            // Wound like its neighbours, but the surface says it should face the other way (a fold
            // the winding cannot express): reversing it would make its whole border inconsistent.
            GridWithReversedBlock(out var positions, out var faces, out var block);
            foreach (var f in block) System.Array.Reverse(faces[f]);
            var normals = new Vector3[positions.Length];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            foreach (var f in block)
                foreach (var v in faces[f]) normals[v] = Vector3.down;
            var before = new List<int[]>();
            foreach (var f in faces) before.Add((int[])f.Clone());
            var stats = new OrientStats();
            FaceOrienter.FlipInvertedPatches(faces, positions, normals, stats);
            Assert.AreEqual(0, stats.patchesFlipped, stats.ToString());
            for (var f = 0; f < faces.Count; f++) Assert.AreEqual(before[f], faces[f]);
        }
    }
}
