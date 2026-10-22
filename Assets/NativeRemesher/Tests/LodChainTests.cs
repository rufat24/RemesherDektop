// LodChain: levels of detail from one mesh, each reduced from the base, measured, and given a
// switch height where its error falls under the pixel budget.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class LodChainTests
    {
        // A wavy quad sheet with UVs, written as an importer writes it: each quad two triangles in a
        // row, one vertex per grid point (one chart).
        static LodChain.Input Sheet(int n, bool scramble = false)
        {
            var p = new List<Vector3>();
            var uv = new List<Vector2>();
            for (var j = 0; j <= n; j++)
                for (var i = 0; i <= n; i++)
                {
                    float u = i / (float)n, v = j / (float)n;
                    p.Add(new Vector3(2f * u, 0.15f * Mathf.Sin(4f * u) * Mathf.Cos(3f * v), 2f * v));
                    uv.Add(new Vector2(u, v));
                }
            var t = new List<int>();
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i, b = a + 1, c = a + n + 2, d = a + n + 1;
                    if (scramble) t.AddRange(new[] { a, b, c, c, d, a });
                    else t.AddRange(new[] { a, b, c, a, c, d });
                }
            return LodChain.FromMesh(p.ToArray(), t.ToArray(), uv.ToArray(), null, 1);
        }

        [Test]
        public void ImportedQuadsAreReadBackAsQuads()
        {
            var input = Sheet(20, scramble: true);
            Assert.That(input.faces.Count, Is.EqualTo(400));
            foreach (var f in input.faces) Assert.That(f.Length, Is.EqualTo(4));
            Assert.That(input.faceUvs.Length, Is.EqualTo(400));
        }

        [Test]
        public void EachLevelHalvesAndSwitchesSoonerThanTheOneBefore()
        {
            var input = Sheet(64);
            var levels = LodChain.Build(input, new LodChain.Settings { levels = 3, keep = 0.5f });
            Assert.That(levels.Count, Is.EqualTo(4));
            for (var l = 1; l < levels.Count; l++)
            {
                Assert.That(levels[l].Cost, Is.InRange(levels[l - 1].Cost * 0.35f, levels[l - 1].Cost * 0.65f), levels[l].ToString());
                Assert.That(levels[l].switchHeight, Is.LessThan(levels[l - 1].switchHeight), levels[l].ToString());
                Assert.That(levels[l].error, Is.GreaterThan(0f).And.LessThan(0.2f), levels[l].ToString());
                Assert.That(levels[l].faceUvs, Is.Not.Null);
            }
            // A clean quad sheet loses whole strips: quads at every level.
            Assert.That(levels[1].method, Is.EqualTo("strips"), levels[1].ToString());
            Assert.That(levels[1].quads, Is.EqualTo(levels[1].faces.Count));
            Assert.That(levels[levels.Count - 1].switchHeight, Is.EqualTo(0.01f).Within(1e-4f));
        }

        [Test]
        public void AFinerPixelBudgetSwitchesLater()
        {
            var input = Sheet(48);
            var coarse = LodChain.Build(input, new LodChain.Settings { levels = 2, pixelError = 4f });
            var fine = LodChain.Build(input, new LodChain.Settings { levels = 2, pixelError = 0.5f });
            // Later: the object must be smaller on screen before the coarser level may show.
            Assert.That(fine[0].switchHeight, Is.LessThan(coarse[0].switchHeight));
        }

        [Test]
        public void SharedUvsStayInsideTheBaseAtlas()
        {
            var input = Sheet(40);
            var levels = LodChain.Build(input, new LodChain.Settings { levels = 3, keep = 0.4f, quadStrips = false });
            for (var l = 1; l < levels.Count; l++)
            {
                Assert.That(levels[l].method, Is.EqualTo("collapse"));
                foreach (var corners in levels[l].faceUvs)
                    foreach (var c in corners)
                        Assert.That(c.x >= -1e-3f && c.x <= 1.001f && c.y >= -1e-3f && c.y <= 1.001f, $"uv {c}");
            }
        }
    }
}
