// Straps As Tubes: a thin free-standing band is recognised, taken out of the input and meshed as a
// quad tube on its own; a thick part, or a thin plate that is no band, is left alone.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class RibbonPartsTests
    {
        // An axis-aligned box, each side a grid of about h, wound outward.
        static void AddBox(List<Vector3> positions, List<int> triangles, Vector3 min, Vector3 max, float h)
        {
            var size = max - min;
            void Side(Vector3 origin, Vector3 u, Vector3 v)
            {
                int nu = Mathf.Max(1, Mathf.CeilToInt(u.magnitude / h)), nv = Mathf.Max(1, Mathf.CeilToInt(v.magnitude / h));
                var first = positions.Count;
                for (var j = 0; j <= nv; j++)
                    for (var i = 0; i <= nu; i++)
                        positions.Add(origin + u * ((float)i / nu) + v * ((float)j / nv));
                for (var j = 0; j < nv; j++)
                    for (var i = 0; i < nu; i++)
                    {
                        var a = first + j * (nu + 1) + i;
                        triangles.AddRange(new[] { a, a + 1, a + nu + 2, a, a + nu + 2, a + nu + 1 });
                    }
            }
            var x = new Vector3(size.x, 0f, 0f);
            var y = new Vector3(0f, size.y, 0f);
            var z = new Vector3(0f, 0f, size.z);
            Side(min, y, x);
            Side(min + z, x, y);
            Side(min, x, z);
            Side(min + y, z, x);
            Side(min, z, y);
            Side(min + x, y, z);
        }

        // Distance from p to the surface of an axis-aligned box.
        static float BoxDistance(Vector3 p, Vector3 min, Vector3 max)
        {
            var c = Vector3.Max(min, Vector3.Min(max, p));
            if (c != p) return Vector3.Distance(c, p);
            return Mathf.Min(Mathf.Min(p.x - min.x, max.x - p.x), Mathf.Min(Mathf.Min(p.y - min.y, max.y - p.y),
                Mathf.Min(p.z - min.z, max.z - p.z)));
        }

        const float k_Cell = 0.05f;

        [Test]
        public void FreeStrapIsTakenOutAndMeshedAsATube()
        {
            // A strap 16 cells long, 2.4 wide and 0.4 thick, beside a block it does not touch.
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            AddBox(positions, triangles, new Vector3(1.5f, -0.5f, -0.5f), new Vector3(2.5f, 0.5f, 0.5f), 0.05f);
            var blockTriangles = triangles.Count / 3;
            Vector3 strapMin = Vector3.zero, strapMax = new Vector3(0.8f, 0.12f, 0.02f);
            AddBox(positions, triangles, strapMin, strapMax, 0.005f);

            var stats = new RibbonParts.Stats();
            var ribbons = RibbonParts.Find(positions.ToArray(), triangles.ToArray(), k_Cell, out var keptPositions,
                out var keptTriangles, out _, out var outward, out var surface, stats);

            Assert.AreEqual(1, ribbons.Count, stats.ToString());
            var reach = ribbons[0].length + ribbons[0].startTrim + ribbons[0].endTrim;
            Assert.That(reach, Is.InRange(0.65f, 0.85f), "the band's length along its centre line");
            Assert.IsTrue(ribbons[0].freeStart && ribbons[0].freeEnd, "both ends hang free");
            Assert.AreEqual(blockTriangles, keptTriangles.Length / 3, "the whole strap goes and the block stays: " + stats);

            RibbonParts.Mesh(ribbons, surface, k_Cell, outward, out var tube, out var faces);
            Assert.Greater(faces.Count, 40);
            foreach (var f in faces) Assert.AreEqual(4, f.Length, "quads only");
            foreach (var p in tube)
                Assert.Less(BoxDistance(p, strapMin, strapMax), 0.3f * k_Cell, $"tube vertex {p} off the strap; line {ribbons[0].centre[0]} to {ribbons[0].centre[ribbons[0].centre.Length - 1]}, trims {ribbons[0].startTrim} {ribbons[0].endTrim}, width {ribbons[0].medianWidth}, thickness {ribbons[0].medianThickness}, lying {ribbons[0].lying}");
            // Closed and wound one way: every edge used twice, once each way.
            var edges = new Dictionary<(int, int), int>();
            foreach (var f in faces)
                for (var i = 0; i < 4; i++)
                {
                    var e = (f[i], f[(i + 1) % 4]);
                    edges[e] = edges.TryGetValue(e, out var n) ? n + 1 : 1;
                }
            foreach (var kv in edges)
            {
                Assert.AreEqual(1, kv.Value, "an edge used twice the same way");
                Assert.IsTrue(edges.ContainsKey((kv.Key.Item2, kv.Key.Item1)), "an open edge: the caps should close the tube");
            }
        }

        // A closed slab, 1 x 1 across and half deep, its top a height field with a band raised
        // along x: 0.6 long, 0.06 wide (1.2 cells), 0.01 high, fused to the top.
        static void AddSlabWithRaisedBand(List<Vector3> positions, List<int> triangles) =>
            AddSlab(positions, triangles, (x, y) => x >= 0.2f && x <= 0.8f && y >= 0.47f && y <= 0.53f ? 0.01f : 0f);

        // The same slab with any height field on top.
        static void AddSlab(List<Vector3> positions, List<int> triangles, System.Func<float, float, float> height)
        {
            const int n = 200;
            const float h = 1f / n;
            float Height(int i, int j) => height(i * h, j * h);
            int Top(int i, int j) => j * (n + 1) + i;
            int Bottom(int i, int j) => (n + 1) * (n + 1) + j * (n + 1) + i;
            for (var j = 0; j <= n; j++)
                for (var i = 0; i <= n; i++) positions.Add(new Vector3(i * h, j * h, Height(i, j)));
            for (var j = 0; j <= n; j++)
                for (var i = 0; i <= n; i++) positions.Add(new Vector3(i * h, j * h, -0.5f));
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    triangles.AddRange(new[] { Top(i, j), Top(i + 1, j), Top(i + 1, j + 1), Top(i, j), Top(i + 1, j + 1), Top(i, j + 1) });
                    triangles.AddRange(new[] { Bottom(i, j), Bottom(i + 1, j + 1), Bottom(i + 1, j), Bottom(i, j), Bottom(i, j + 1), Bottom(i + 1, j + 1) });
                }
            // The four sides, top rim to bottom rim, wound outward.
            for (var i = 0; i < n; i++)
            {
                triangles.AddRange(new[] { Top(i, 0), Bottom(i, 0), Bottom(i + 1, 0), Top(i, 0), Bottom(i + 1, 0), Top(i + 1, 0) });
                triangles.AddRange(new[] { Top(i, n), Top(i + 1, n), Bottom(i + 1, n), Top(i, n), Bottom(i + 1, n), Bottom(i, n) });
                triangles.AddRange(new[] { Top(0, i), Top(0, i + 1), Bottom(0, i + 1), Top(0, i), Bottom(0, i + 1), Bottom(0, i) });
                triangles.AddRange(new[] { Top(n, i), Bottom(n, i), Bottom(n, i + 1), Top(n, i), Bottom(n, i + 1), Top(n, i + 1) });
            }
        }

        [Test]
        public void RaisedBandFusedToASurfaceIsTakenOutAndMeshedLyingOnIt()
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            AddSlabWithRaisedBand(positions, triangles);

            var stats = new RibbonParts.Stats();
            var ribbons = RibbonParts.Find(positions.ToArray(), triangles.ToArray(), k_Cell, out _, out var keptTriangles,
                out _, out var outward, out var surface, stats);

            Assert.AreEqual(1, ribbons.Count, stats.ToString());
            Assert.IsTrue(ribbons[0].lying, "fused to the surface: its tube goes down into it");
            var reach = ribbons[0].length + ribbons[0].startTrim + ribbons[0].endTrim;
            Assert.That(reach, Is.InRange(0.5f, 0.7f), "the band's length along its centre line");
            Assert.Less(keptTriangles.Length, triangles.Count, "the band's faces are taken out");

            RibbonParts.Mesh(ribbons, surface, k_Cell, outward, out var tube, out var faces);
            Assert.Greater(faces.Count, 20);
            foreach (var p in tube)
            {
                Assert.That(p.x, Is.InRange(0.2f - 0.3f * k_Cell, 0.8f + 0.3f * k_Cell), $"tube vertex {p} past the band's ends");
                Assert.That(p.y, Is.InRange(0.47f - 0.3f * k_Cell, 0.53f + 0.3f * k_Cell), $"tube vertex {p} off the band's sides");
                Assert.That(p.z, Is.InRange(-0.3f * k_Cell, 0.01f + 0.3f * k_Cell), $"tube vertex {p} off its height");
            }
        }

        [Test]
        public void StrapRunsOnThroughASlider()
        {
            // A strap lying on a slab, a slider (taller and wider) on it, and a piece of strap out of
            // the other side too short to pass alone: one ribbon through, the slider boxed.
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            AddSlab(positions, triangles, (x, y) =>
            {
                if (x >= 0.45f && x <= 0.53f && y >= 0.45f && y <= 0.55f) return 0.03f;
                return x >= 0.15f && x <= 0.68f && y >= 0.47f && y <= 0.53f ? 0.01f : 0f;
            });

            var stats = new RibbonParts.Stats();
            var blocks = new List<RibbonParts.Block>();
            var log = new List<string>();
            RibbonParts.debugLog = log;
            List<RibbonParts.Ribbon> ribbons;
            try
            {
                ribbons = RibbonParts.Find(positions.ToArray(), triangles.ToArray(), k_Cell, out _, out _,
                    out _, out _, out _, stats, blocks);
            }
            finally
            {
                RibbonParts.debugLog = null;
            }
            var why = stats + "\n" + string.Join("\n", log);

            Assert.AreEqual(1, ribbons.Count, why);
            Assert.AreEqual(1, stats.links, why);
            var r = ribbons[0];
            var first = r.centre[0] - (r.centre[1] - r.centre[0]).normalized * r.startTrim;
            var last = r.centre[r.centre.Length - 1] + (r.centre[r.centre.Length - 1] - r.centre[r.centre.Length - 2]).normalized * r.endTrim;
            Assert.Less(Mathf.Min(first.x, last.x), 0.25f, "the strap's start");
            Assert.Greater(Mathf.Max(first.x, last.x), 0.62f, "on through the slider to the piece beyond");
            Assert.AreEqual(1, blocks.Count, "the slider boxed");
            Assert.That(blocks[0].centre.x, Is.InRange(0.44f, 0.54f), "the box at the slider");
        }

        [Test]
        public void SquareThinPlateAndThickBlockAreLeftAlone()
        {
            // A thin plate as wide as it is long is no band.
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            AddBox(positions, triangles, new Vector3(1.5f, -0.5f, -0.5f), new Vector3(2.5f, 0.5f, 0.5f), 0.05f);
            AddBox(positions, triangles, Vector3.zero, new Vector3(0.2f, 0.2f, 0.02f), 0.005f);
            var input = triangles.ToArray();

            var stats = new RibbonParts.Stats();
            var ribbons = RibbonParts.Find(positions.ToArray(), input, k_Cell, out _, out var keptTriangles,
                out _, out _, out _, stats);

            Assert.AreEqual(0, ribbons.Count, stats.ToString());
            Assert.AreSame(input, keptTriangles, "nothing taken out");
        }
    }
}
