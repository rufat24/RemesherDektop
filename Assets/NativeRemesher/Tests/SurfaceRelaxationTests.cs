// Tests for the smooth-and-reproject relaxation pass.
//
// The thing this pass is for — a shading tail that a mean-error number cannot see — is measured on
// the character, not here. What is here are the invariants whose violation would be silent: a mesh
// that quietly shrinks, a rim that creeps inwards, a face that ends up facing the other way, or a
// pass that keeps moving things after it has converged. Each of those looks fine in a face count
// and each would undo the reason the pass exists.

using System.Collections.Generic;
using NUnit.Framework;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class SurfaceRelaxationTests
    {
        /// <summary>
        /// A flat n-by-n grid of quads in the xy plane, with a matching triangle soup of the same
        /// plane to project onto. The plane is the one surface where the right answer is known
        /// exactly: relaxation must rearrange within it and never leave it.
        /// </summary>
        static void Grid(int n, out List<int[]> faces, out Vector3[] positions,
            out SpatialGrid surface)
        {
            var side = n + 1;
            positions = new Vector3[side * side];
            for (var y = 0; y <= n; y++)
                for (var x = 0; x <= n; x++)
                    positions[y * side + x] = new Vector3(x, y, 0f);

            faces = new List<int[]>();
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                    faces.Add(new[]
                    {
                        y * side + x,
                        y * side + x + 1,
                        (y + 1) * side + x + 1,
                        (y + 1) * side + x
                    });

            // The surface is the same plane, generously oversized, so that a vertex smoothed
            // anywhere within the grid still has plane under it to project onto. Two big triangles
            // rather than a copy of the grid, so the projection target cannot be confused with the
            // thing being relaxed.
            var soup = new[]
            {
                new Vector3(-n, -n, 0f), new Vector3(2 * n, -n, 0f), new Vector3(2 * n, 2 * n, 0f),
                new Vector3(-n, -n, 0f), new Vector3(2 * n, 2 * n, 0f), new Vector3(-n, 2 * n, 0f)
            };
            surface = new SpatialGrid(soup, new[] { 0, 1, 2, 3, 4, 5 });
        }

        [Test]
        public void ASpikeIsPulledBackOntoTheSurface()
        {
            Grid(4, out var faces, out var positions, out var surface);

            // One interior vertex lifted off the plane by a third of a cell. This is the defect in
            // miniature: a small excursion, and the four faces around it fold hard enough to read
            // as a crease.
            //
            // A THIRD, not a whole cell, and the size is the point. The drift cap holds a vertex
            // within half a cell of where extraction put it, so a spike a full cell out cannot be
            // fully retrieved and the assertion below would fail — correctly. Real spikes are
            // nothing like that big: output vertices sit on the input surface to within 0.005 % of
            // the model's diagonal, and the folds this pass removes come from how vertices are
            // ARRANGED, not from any of them being far out of place.
            const int spike = 2 * 5 + 2;
            positions[spike] = new Vector3(2f, 2f, 0.33f);

            var stats = SurfaceRelaxation.Run(faces, positions, surface, 1f);

            Assert.Greater(stats.moves, 0, "the pass did nothing at all");
            Assert.Less(Mathf.Abs(positions[spike].z), 1e-3f,
                "the spike is still off the plane");
        }

        [Test]
        public void ItDoesNotMoveTheMeshOffTheSurface()
        {
            Grid(6, out var faces, out var positions, out var surface);

            // Every interior vertex jittered off the plane, which is what the real defect looks
            // like: not one spike but a scatter of them.
            //
            // INTERIOR only, and the assertion below is interior only too. Boundary vertices are
            // pinned, and a pinned vertex is never projected either — so jittering the rim would
            // assert that this pass fixes something it deliberately does not touch. In the pipeline
            // the question does not arise: every output vertex arrives on the surface already, and
            // this pass is the only thing that could take one off it.
            const int side = 7;
            var random = new System.Random(12345);
            for (var y = 1; y < side - 1; y++)
                for (var x = 1; x < side - 1; x++)
                    positions[y * side + x] +=
                        new Vector3(0f, 0f, (float)(random.NextDouble() - 0.5) * 0.6f);

            SurfaceRelaxation.Run(faces, positions, surface, 1f);

            for (var y = 1; y < side - 1; y++)
                for (var x = 1; x < side - 1; x++)
                    Assert.Less(Mathf.Abs(positions[y * side + x].z), 1e-3f,
                        $"vertex ({x}, {y}) was left off the plane");
        }

        [Test]
        public void TheRimDoesNotCreepInwards()
        {
            Grid(4, out var faces, out var positions, out var surface);

            var before = (Vector3[])positions.Clone();
            SurfaceRelaxation.Run(faces, positions, surface, 1f);

            // Boundary vertices are pinned, so the outline is identical rather than merely close.
            // Unpinned, plain umbrella smoothing pulls a rim in by a measurable amount every sweep
            // and the mesh shrinks from the edges — invisible in the middle, obvious at the
            // silhouette.
            var side = 5;
            for (var y = 0; y < side; y++)
                for (var x = 0; x < side; x++)
                {
                    if (x != 0 && x != side - 1 && y != 0 && y != side - 1) continue;
                    var v = y * side + x;
                    Assert.AreEqual(before[v], positions[v], $"boundary vertex {v} moved");
                }
        }

        [Test]
        public void NoFaceEndsUpFacingTheOtherWay()
        {
            Grid(5, out var faces, out var positions, out var surface);

            var random = new System.Random(999);
            for (var v = 0; v < positions.Length; v++)
                positions[v] += new Vector3(
                    (float)(random.NextDouble() - 0.5) * 0.4f,
                    (float)(random.NextDouble() - 0.5) * 0.4f,
                    (float)(random.NextDouble() - 0.5) * 0.4f);

            var before = new Vector3[faces.Count];
            for (var f = 0; f < faces.Count; f++) before[f] = Newell(positions, faces[f]);

            SurfaceRelaxation.Run(faces, positions, surface, 1f);

            for (var f = 0; f < faces.Count; f++)
                Assert.Greater(Vector3.Dot(before[f], Newell(positions, faces[f])), 0f,
                    $"face {f} reversed");
        }

        [Test]
        public void AnAlreadyRelaxedMeshIsLeftAlone()
        {
            Grid(4, out var faces, out var positions, out var surface);

            // The grid is already the fixed point of the operator: every interior vertex is exactly
            // its neighbours' centroid. So the pass must converge immediately rather than jitter,
            // and the early-out must notice.
            var before = (Vector3[])positions.Clone();
            var stats = SurfaceRelaxation.Run(faces, positions, surface, 1f);

            Assert.LessOrEqual(stats.iterations, 2,
                "the pass kept sweeping a mesh that was already at rest");

            for (var v = 0; v < positions.Length; v++)
                Assert.Less((positions[v] - before[v]).magnitude, 1e-4f, $"vertex {v} drifted");
        }

        [Test]
        public void RunningItAgainChangesAlmostNothing()
        {
            Grid(6, out var faces, out var positions, out var surface);

            var random = new System.Random(7);
            for (var v = 0; v < positions.Length; v++)
                positions[v] += new Vector3(
                    (float)(random.NextDouble() - 0.5) * 0.3f,
                    (float)(random.NextDouble() - 0.5) * 0.3f, 0f);

            SurfaceRelaxation.Run(faces, positions, surface, 1f);
            var settled = (Vector3[])positions.Clone();

            var second = SurfaceRelaxation.Run(faces, positions, surface, 1f);

            // Not "identical": the operator has no true fixed point on a mesh with a boundary, so
            // the honest assertion is that a second run's total movement is a small fraction of a
            // cell rather than zero.
            Assert.Less(second.meanDisplacement, 0.02f,
                "a second run moved the mesh as much as the first");

            for (var v = 0; v < positions.Length; v++)
                Assert.Less((positions[v] - settled[v]).magnitude, 0.1f, $"vertex {v} kept moving");
        }

        /// <summary>
        /// Two quad strips meeting at a right angle along y, with a matching triangle soup. The
        /// crease runs along the middle row, and the vertices on it are the ones at issue.
        /// </summary>
        static void Crease(out List<int[]> faces, out Vector3[] positions, out SpatialGrid surface,
            out int[] onCrease)
        {
            // z = 0 plane for x in [-2, 0], turning up the x = 0 plane for z in [0, 2]. Rows run
            // along y so the crease is a straight line of vertices rather than a single point.
            var columns = new[]
            {
                new Vector3(-2f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 0f),
                new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 2f)
            };

            const int rows = 5;
            positions = new Vector3[columns.Length * rows];
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < columns.Length; c++)
                    positions[r * columns.Length + c] = columns[c] + new Vector3(0f, r, 0f);

            faces = new List<int[]>();
            for (var r = 0; r < rows - 1; r++)
                for (var c = 0; c < columns.Length - 1; c++)
                    faces.Add(new[]
                    {
                        r * columns.Length + c,
                        r * columns.Length + c + 1,
                        (r + 1) * columns.Length + c + 1,
                        (r + 1) * columns.Length + c
                    });

            // The surface is the same two planes, oversized, as one triangle soup.
            var soup = new List<Vector3>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                soup.Add(a); soup.Add(b); soup.Add(c);
                soup.Add(a); soup.Add(c); soup.Add(d);
            }
            Quad(new Vector3(-4f, -2f, 0f), new Vector3(0f, -2f, 0f),
                 new Vector3(0f, 6f, 0f), new Vector3(-4f, 6f, 0f));
            Quad(new Vector3(0f, -2f, 0f), new Vector3(0f, -2f, 4f),
                 new Vector3(0f, 6f, 4f), new Vector3(0f, 6f, 0f));

            var indices = new int[soup.Count];
            for (var i = 0; i < indices.Length; i++) indices[i] = i;
            surface = new SpatialGrid(soup.ToArray(), indices);

            // Column 2 is the crease; rows 1..3 are the interior ones, which is what the test can
            // say anything about — rows 0 and 4 are pinned as boundary regardless.
            onCrease = new[] { 1 * columns.Length + 2, 2 * columns.Length + 2, 3 * columns.Length + 2 };
        }

        [Test]
        public void ACreaseIsNotSmoothedAwayWhenTheProfileHasFeatures()
        {
            Crease(out var faces, out var positions, out var surface, out var onCrease);

            var before = (Vector3[])positions.Clone();
            var stats = SurfaceRelaxation.Run(faces, positions, surface, 1f,
                featureAngleDegrees: 30f);

            Assert.AreEqual(onCrease.Length, stats.pinnedByFeature,
                "the crease row was not recognised");

            foreach (var v in onCrease)
                Assert.AreEqual(before[v], positions[v], $"crease vertex {v} moved");
        }

        [Test]
        public void TheSameCreaseIsSmoothedWhenTheProfileHasNoFeatures()
        {
            Crease(out var faces, out var positions, out var surface, out var onCrease);

            var before = (Vector3[])positions.Clone();
            var stats = SurfaceRelaxation.Run(faces, positions, surface, 1f,
                featureAngleDegrees: 0f);

            // The complement of the test above, and the reason both exist: the guard must be a
            // decision the caller makes, not something the pass does unconditionally. A scan
            // profile turns it off, and a fold on a scan has to be free to relax.
            Assert.AreEqual(0, stats.pinnedByFeature);
            Assert.AreEqual(0, stats.rejectedFeature);

            var moved = 0;
            foreach (var v in onCrease)
                if ((positions[v] - before[v]).magnitude > 1e-4f) moved++;

            Assert.Greater(moved, 0, "nothing on the crease moved with protection off");
        }

        [Test]
        public void NoVertexTravelsFurtherThanTheDriftCap()
        {
            Grid(8, out var faces, out var positions, out var surface);

            // Jittered hard in the PLANE, so the umbrella has a lot of rearranging to do and every
            // move is legal — no projection or flip rejection can mask the cap being the thing that
            // stops a vertex. Without a cap this configuration drifts several cells.
            const int side = 9;
            var random = new System.Random(4242);
            var before = (Vector3[])positions.Clone();

            for (var y = 1; y < side - 1; y++)
                for (var x = 1; x < side - 1; x++)
                    positions[y * side + x] += new Vector3(
                        (float)(random.NextDouble() - 0.5) * 1.6f,
                        (float)(random.NextDouble() - 0.5) * 1.6f, 0f);

            var jittered = (Vector3[])positions.Clone();
            var stats = SurfaceRelaxation.Run(faces, positions, surface, 1f, maxDrift: 0.5f);

            Assert.Greater(stats.rejectedDrift, 0, "the cap never bound, so this proves nothing");

            for (var v = 0; v < positions.Length; v++)
                Assert.LessOrEqual((positions[v] - jittered[v]).magnitude, 0.5f + 1e-4f,
                    $"vertex {v} drifted past the cap");

            // And the cap is a cap, not a freeze: the pass still did its job within it.
            Assert.Greater(stats.moves, 0);
        }

        [Test]
        public void NoSurfaceOrNoMeshIsHandledWithoutThrowing()
        {
            Grid(3, out var faces, out var positions, out _);

            Assert.AreEqual(0, SurfaceRelaxation.Run(faces, positions, null, 1f).iterations);
            Assert.AreEqual(0,
                SurfaceRelaxation.Run(new List<int[]>(), new Vector3[0],
                    new SpatialGrid(new Vector3[0], new int[0]), 1f).iterations);
        }

        /// <summary>
        /// Unnormalised, for the same reason the pass itself is: at real mesh scales
        /// <c>Vector3.normalized</c> returns zero and every comparison above would pass vacuously.
        /// </summary>
        static Vector3 Newell(Vector3[] positions, int[] face)
        {
            var normal = Vector3.zero;
            for (var k = 0; k < face.Length; k++)
            {
                var a = positions[face[k]];
                var b = positions[face[(k + 1) % face.Length]];
                normal += new Vector3(
                    (a.y - b.y) * (a.z + b.z),
                    (a.z - b.z) * (a.x + b.x),
                    (a.x - b.x) * (a.y + b.y));
            }
            return normal;
        }

        /// <summary>
        /// A roof edge: a flat roof (z = 0, x up to 3) and a wall going down from it (x = 3), with a
        /// quad grid folded over the edge so one row of vertices lies exactly on it, spaced
        /// unevenly. With the crease test off (a scan profile), relaxation rounds the edge off;
        /// told the row is on a line, it keeps the row on the line and only evens it out along it.
        /// </summary>
        [Test]
        public void VerticesOnAGivenLineSlideAlongItAndStayOnIt()
        {
            const int n = 6;
            var side = n + 1;
            Vector3 Fold(float u, float v) => u <= 3f ? new Vector3(u, v, 0f) : new Vector3(3f, v, 3f - u);
            List<int[]> faces;
            Vector3[] Build()
            {
                var p = new Vector3[side * side];
                for (var y = 0; y <= n; y++)
                    for (var x = 0; x <= n; x++)
                    {
                        // The row on the edge bunched towards one end, so there is spacing to even out.
                        var v = x == 3 && y > 0 && y < n ? y - 0.35f * Mathf.Sin(y * Mathf.PI / n) : y;
                        p[y * side + x] = Fold(x, v);
                    }
                return p;
            }
            faces = new List<int[]>();
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                    faces.Add(new[] { y * side + x, y * side + x + 1, (y + 1) * side + x + 1, (y + 1) * side + x });
            var soup = new[]
            {
                new Vector3(-3f, -3f, 0f), new Vector3(3f, -3f, 0f), new Vector3(3f, 9f, 0f),
                new Vector3(-3f, -3f, 0f), new Vector3(3f, 9f, 0f), new Vector3(-3f, 9f, 0f),
                new Vector3(3f, -3f, 0f), new Vector3(3f, -3f, -6f), new Vector3(3f, 9f, -6f),
                new Vector3(3f, -3f, 0f), new Vector3(3f, 9f, -6f), new Vector3(3f, 9f, 0f)
            };
            var surface = new SpatialGrid(soup, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 });

            var onLine = new bool[side * side];
            for (var y = 0; y <= n; y++) onLine[y * side + 3] = true;
            var lines = new LineSet(new[] { new Vector3(3f, -1f, 0f), new Vector3(3f, n + 1f, 0f) }, 0.3f);

            float Off(Vector3[] p)
            {
                var worst = 0f;
                for (var y = 1; y < n; y++)
                {
                    var q = p[y * side + 3];
                    worst = Mathf.Max(worst, new Vector2(q.x - 3f, q.z).magnitude);
                }
                return worst;
            }

            var free = Build();
            SurfaceRelaxation.Run(faces, free, surface, 1f, featureAngleDegrees: 0f);
            Assert.Greater(Off(free), 0.05f, "without the line the edge was not rounded off, so the test shows nothing");

            var held = Build();
            var before = held[3 * side + 3].y;
            var stats = SurfaceRelaxation.Run(faces, held, surface, 1f, featureAngleDegrees: 0f,
                onLine: onLine, lines: lines, lineReach: 0.3f);
            Assert.Less(Off(held), 1e-4f, "a vertex on the line left it");
            Assert.Greater(stats.slidingOnCrease, 0, "no vertex was free to slide along the line");
            Assert.Greater(Mathf.Abs(held[3 * side + 3].y - before), 1e-3f, "the row was not evened out along the line");
        }
    }
}
