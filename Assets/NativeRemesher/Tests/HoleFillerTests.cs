// HoleFiller's contract is a topological invariant, so it is testable directly on hand-built face
// sets — no solver, no mesh, no tolerances.
//
// The invariant: capping a simple boundary loop with one face raises V - E + F by exactly one and
// cannot change genus. The pass this replaced satisfied neither. It drove boundary edges to zero
// on a closed sphere while moving Euler from -7 to -14, because a size-free walk over the graph
// could close a tunnel instead of a hole. These tests exist so that specific failure cannot come
// back unnoticed.

using System.Collections.Generic;
using NUnit.Framework;
using NativeRemesher.Extraction;

namespace NativeRemesher.Tests
{
    public class HoleFillerTests
    {
        /// <summary>V - E + F over a face list, counting each undirected edge once.</summary>
        static int Euler(List<int[]> faces, out int boundaryEdges)
        {
            var vertices = new HashSet<int>();
            var edgeUses = new Dictionary<long, int>();

            foreach (var face in faces)
            {
                for (var i = 0; i < face.Length; i++)
                {
                    var a = face[i];
                    var b = face[(i + 1) % face.Length];
                    vertices.Add(a);

                    var key = EdgeKey.PackUndirected(a, b);
                    edgeUses.TryGetValue(key, out var count);
                    edgeUses[key] = count + 1;
                }
            }

            boundaryEdges = 0;
            foreach (var use in edgeUses.Values)
                if (use == 1) boundaryEdges++;

            return vertices.Count - edgeUses.Count + faces.Count;
        }

        /// <summary>
        /// A closed cube, six quads, consistently wound: every one of the 24 directed edges appears
        /// exactly once. Vertices 0-3 are the bottom ring, 4-7 the top, with 4 above 0.
        /// </summary>
        static List<int[]> ClosedCube()
        {
            return new List<int[]>
            {
                new[] { 0, 1, 2, 3 },   // bottom
                new[] { 4, 7, 6, 5 },   // top
                new[] { 0, 4, 5, 1 },
                new[] { 1, 5, 6, 2 },
                new[] { 2, 6, 7, 3 },
                new[] { 3, 7, 4, 0 }
            };
        }

        /// <summary>The same cube with the top quad removed: one four-edge boundary loop.</summary>
        static List<int[]> CubeMissingItsTop()
        {
            var faces = ClosedCube();
            faces.RemoveAt(1);
            return faces;
        }

        [Test]
        public void CappingASimpleLoopRaisesEulerByExactlyOne()
        {
            // Five of the cube's six quads: one square hole where the sixth would be.
            var faces = CubeMissingItsTop();

            var before = Euler(faces, out var boundaryBefore);
            Assert.AreEqual(4, boundaryBefore, "the open cube should have a four-edge boundary loop");
            Assert.AreEqual(1, before, "a disc has Euler characteristic 1");

            var stats = HoleFiller.Fill(faces, 8, expectClosed: true);

            Assert.AreEqual(1, stats.loopsCapped, "the single hole should have been capped once");
            Assert.AreEqual(4, stats.boundaryEdgesFound);
            Assert.AreEqual(4, stats.largestCap, "the cap should be a quad, not something larger");

            var after = Euler(faces, out var boundaryAfter);

            Assert.Zero(boundaryAfter, "capping should leave no boundary edges");
            Assert.AreEqual(before + 1, after, "one cap must raise Euler by exactly one");
            Assert.AreEqual(2, after, "a closed genus-0 surface has Euler characteristic 2");
        }

        [Test]
        public void TheCapWindsOppositeToTheFaceThatOwnsTheEdge()
        {
            var faces = CubeMissingItsTop();

            HoleFiller.Fill(faces, 8, expectClosed: true);

            // Every directed edge must now appear exactly once across all faces. That is the
            // definition of consistent winding on a closed surface, and it is what a cap traversed
            // the wrong way round would break.
            var directed = new Dictionary<long, int>();
            foreach (var face in faces)
            {
                for (var i = 0; i < face.Length; i++)
                {
                    var key = EdgeKey.Pack(face[i], face[(i + 1) % face.Length]);
                    directed.TryGetValue(key, out var count);
                    directed[key] = count + 1;
                }
            }

            foreach (var pair in directed)
                Assert.AreEqual(1, pair.Value,
                    $"directed edge {EdgeKey.First(pair.Key)}->{EdgeKey.Second(pair.Key)} " +
                    $"appears {pair.Value} times; the cap is wound the wrong way");
        }

        [Test]
        public void AnAlreadyClosedSurfaceIsLeftAlone()
        {
            var faces = ClosedCube();

            var before = Euler(faces, out var boundaryBefore);
            Assert.Zero(boundaryBefore, "this fixture is meant to be closed already");

            var countBefore = faces.Count;
            var stats = HoleFiller.Fill(faces, 8, expectClosed: true);

            Assert.Zero(stats.boundaryEdgesFound);
            Assert.Zero(stats.loopsCapped);
            Assert.AreEqual(countBefore, faces.Count, "nothing should have been added");
            Assert.AreEqual(before, Euler(faces, out _), "Euler must be untouched");
        }

        [Test]
        public void OpenInputIsNotCappedShut()
        {
            // The caller passes expectClosed: false for a mesh that genuinely has a boundary. An
            // open disc's own outline is a boundary loop, and capping it would weld the mesh shut
            // and silently change what the user asked for.
            var faces = new List<int[]>
            {
                new[] { 0, 1, 2, 3 },
                new[] { 0, 4, 5, 1 }
            };

            var countBefore = faces.Count;
            var stats = HoleFiller.Fill(faces, 6, expectClosed: false);

            Assert.Zero(stats.loopsCapped);
            Assert.AreEqual(countBefore, faces.Count, "an open mesh must be left open");
        }

        [Test]
        public void TwoSeparateHolesAreBothCapped()
        {
            // A quad tube: four side quads, both ends open. Two disjoint four-edge loops.
            var faces = new List<int[]>
            {
                new[] { 0, 4, 5, 1 },
                new[] { 1, 5, 6, 2 },
                new[] { 2, 6, 7, 3 },
                new[] { 3, 7, 4, 0 }
            };

            var before = Euler(faces, out var boundaryBefore);
            Assert.AreEqual(8, boundaryBefore, "a tube has two four-edge boundary loops");

            var stats = HoleFiller.Fill(faces, 8, expectClosed: true);

            Assert.AreEqual(2, stats.loopsCapped, "both ends should have been capped");

            var after = Euler(faces, out var boundaryAfter);
            Assert.Zero(boundaryAfter);
            Assert.AreEqual(before + 2, after, "two caps must raise Euler by exactly two");
            Assert.AreEqual(2, after, "the capped tube is a genus-0 closed surface");
        }

        [Test]
        public void AFailedWalkDoesNotPoisonEdgesForLaterWalks()
        {
            // The regression guard for a destructive-failure bug that shipped in the first version
            // of this pass: edges were marked consumed as the walk stepped onto them, so a walk
            // that failed to close made its edges permanently unusable and every later walk over
            // them failed too. Measured cost on a subdivided cube: 5 loops capped, 82 boundary
            // edges still open. FaceTracer's header documents the identical defect.
            //
            // The fixture puts a dangling boundary path next to a genuine closed loop. Walks are
            // started in dictionary order, so a walk may well hit the dead end first; the capped
            // loop must survive that either way.
            var faces = CubeMissingItsTop();

            // A quad hanging off the BOTTOM face, deliberately wound the same way as the bottom
            // across their shared edge 0-1 rather than opposite to it. That inconsistency is what
            // produces a dead end: the shared edge reads as interior, while 1-9, 9-8 and 8-0 are
            // boundary edges whose cap directions chain 0 -> 8 -> 9 -> 1 and then stop, because
            // nothing leaves 1.
            //
            // It has to hang off the bottom, not off the rim of the hole. Attaching it at the rim
            // merges the two boundaries into one inconsistently-wound cycle, so there is no intact
            // loop left to preserve and the test would prove nothing — which is exactly what the
            // first version of this fixture did.
            faces.Add(new[] { 0, 1, 9, 8 });

            var stats = HoleFiller.Fill(faces, 10, expectClosed: true);

            Assert.Greater(stats.loopsCapped, 0,
                $"a dead-end walk consumed the edges of a genuine loop; {stats}");

            Assert.Greater(stats.loopsAbandoned, 0,
                $"the fixture is supposed to contain a dead-end path; {stats}");

            // And the genuine hole really is closed: every edge of the original square hole must now
            // have two faces. The dangling quad's own free edges are expected to remain open.
            var incidence = new Dictionary<long, int>();
            foreach (var face in faces)
                for (var i = 0; i < face.Length; i++)
                {
                    var key = EdgeKey.PackUndirected(face[i], face[(i + 1) % face.Length]);
                    incidence.TryGetValue(key, out var count);
                    incidence[key] = count + 1;
                }

            foreach (var pair in new[] { (4, 7), (7, 6), (6, 5), (5, 4) })
                Assert.AreEqual(2, incidence[EdgeKey.PackUndirected(pair.Item1, pair.Item2)],
                    $"edge {pair} of the capped hole should have two faces; {stats}");
        }

        [Test]
        public void AnEmptyOrNullFaceSetIsHandled()
        {
            Assert.DoesNotThrow(() => HoleFiller.Fill(null, 0, expectClosed: true));
            Assert.DoesNotThrow(() => HoleFiller.Fill(new List<int[]>(), 0, expectClosed: true));
        }

        [Test]
        public void PackedEdgeKeysDoNotCollideOnTheDefaultLongHash()
        {
            // The regression guard for the defect that cost a 29-minute topology build: .NET hashes
            // a long as (int)v ^ (int)(v >> 32), so for the packing (a << 32) | b that is a ^ b.
            // Under the default comparer the four pairs below all hash identically. EdgeKey.Comparer
            // must separate them, or every edge-keyed container in the extractor degrades to a
            // linear scan.
            var pairs = new[] { (1, 0), (0, 1), (3, 2), (2, 3) };
            var hashes = new HashSet<int>();

            foreach (var (a, b) in pairs)
                hashes.Add(EdgeKey.Comparer.GetHashCode(EdgeKey.Pack(a, b)));

            Assert.AreEqual(pairs.Length, hashes.Count,
                "EdgeKey.Comparer collapsed distinct edge keys to the same hash");

            // And the default comparer really does collapse them — asserted so this test documents
            // the hazard rather than merely avoiding it.
            var defaultHashes = new HashSet<int>();
            foreach (var (a, b) in pairs)
                defaultHashes.Add(EdgeKey.Pack(a, b).GetHashCode());

            Assert.Less(defaultHashes.Count, pairs.Length,
                "expected the default long hash to collide on these pairs; if it no longer does, " +
                "the runtime changed and this note needs revisiting");
        }

        /// <summary>
        /// A rim large enough to matter is closed with a grid, not a fan around one centre.
        /// </summary>
        /// <remarks>
        /// This is the defect the wireframe shows as a "sun": a hub pie leaves one vertex of valence
        /// rim/2, so a 12-corner rim leaves a valence-6 star and a 67-corner rim a valence-33 one.
        /// The assertion that matters is the LAST one — no vertex the fill introduced may exceed
        /// valence 4 — because that is the property the picture complains about, and a test on face
        /// counts alone would pass for a fan too.
        ///
        /// The fixture is a 6x6 vertex grid of quads with the middle 3x3 block of quads removed, so
        /// the hole has a 12-vertex rim and the correct fill is exactly the 9 quads taken out.
        /// </remarks>
        [Test]
        public void ALargeRimIsClosedWithAGridRatherThanAStarOfQuads()
        {
            const int side = 6;

            var positions = new List<UnityEngine.Vector3>();
            for (var y = 0; y < side; y++)
                for (var x = 0; x < side; x++)
                    positions.Add(new UnityEngine.Vector3(x, y, 0f));

            int V(int x, int y) => y * side + x;

            // A closed pillow: the same grid mirrored below and stitched around the perimeter, so
            // the ONLY boundary loop in the fixture is the hole. Without the underside the square's
            // own perimeter is a boundary too and the fill lids the whole thing shut.
            var top = positions.Count;
            for (var y = 0; y < side; y++)
                for (var x = 0; x < side; x++)
                    positions.Add(new UnityEngine.Vector3(x, y, -1f));

            int B(int x, int y) => top + y * side + x;

            var faces = new List<int[]>();
            for (var y = 0; y < side - 1; y++)
            {
                for (var x = 0; x < side - 1; x++)
                {
                    if (!(x >= 1 && x <= 3 && y >= 1 && y <= 3))          // the hole
                        faces.Add(new[] { V(x, y), V(x + 1, y), V(x + 1, y + 1), V(x, y + 1) });

                    faces.Add(new[] { B(x, y), B(x, y + 1), B(x + 1, y + 1), B(x + 1, y) });
                }
            }

            // Perimeter, in the top's winding order, stitched to the underside.
            var rim = new List<(int, int)>();
            for (var x = 0; x < side - 1; x++) rim.Add((V(x, 0), V(x + 1, 0)));
            for (var y = 0; y < side - 1; y++) rim.Add((V(side - 1, y), V(side - 1, y + 1)));
            for (var x = side - 1; x > 0; x--) rim.Add((V(x, side - 1), V(x - 1, side - 1)));
            for (var y = side - 1; y > 0; y--) rim.Add((V(0, y), V(0, y - 1)));

            // b->a reverses the top's traversal, and a+top is a vertex's counterpart underneath,
            // so every perimeter edge ends up used exactly twice in opposite directions.
            foreach (var (a, b) in rim)
                faces.Add(new[] { b, a, a + top, b + top });

            var before = faces.Count;
            var stats = HoleFiller.Fill(faces, positions.Count, expectClosed: true,
                positions.ToArray());

            Assert.AreEqual(1, stats.loopsGridFilled, "the rim should have been gridded: " + stats);
            Assert.AreEqual(0, stats.largestHub, "no hub should have been created");
            Assert.AreEqual(9, stats.quadsFromGrids, "a 12-rim fills as a 3x3 block of quads");
            Assert.AreEqual(4, stats.gridVertices, "with four interior vertices");
            Assert.AreEqual(before + 9, faces.Count);

            for (var f = before; f < faces.Count; f++)
                Assert.AreEqual(4, faces[f].Length, "the fill emitted a non-quad");

            var valence = new Dictionary<int, HashSet<int>>();
            foreach (var face in faces)
            {
                for (var i = 0; i < face.Length; i++)
                {
                    var a = face[i];
                    var b = face[(i + 1) % face.Length];
                    if (!valence.TryGetValue(a, out var set)) valence[a] = set = new HashSet<int>();
                    set.Add(b);
                    if (!valence.TryGetValue(b, out var other)) valence[b] = other = new HashSet<int>();
                    other.Add(a);
                }
            }

            for (var v = positions.Count; v < positions.Count + stats.verticesAdded; v++)
            {
                Assert.LessOrEqual(valence[v].Count, 4,
                    $"vertex {v} has valence {valence[v].Count} — that is a star, not a grid");
            }
        }
    }
}
