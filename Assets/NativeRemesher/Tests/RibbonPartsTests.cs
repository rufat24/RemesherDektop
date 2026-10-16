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
                Assert.Less(BoxDistance(p, strapMin, strapMax), 0.3f * k_Cell, $"tube vertex {p} off the strap");
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
