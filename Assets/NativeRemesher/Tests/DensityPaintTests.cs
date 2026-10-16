// Paint Fix Areas: the painted value, and a remesh that gives painted areas the smaller quads
// without taking them from the rest.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class DensityPaintTests
    {
        [Test]
        public void ValueFallsOffToTheRimAndClamps()
        {
            var paint = new DensityPaint();
            paint.dabs.Add(new DensityPaint.Dab { centre = Vector3.zero, radius = 1f, strength = 0.75f });
            Assert.AreEqual(0.75f, paint.ValueAt(Vector3.zero), 1e-5f);
            Assert.AreEqual(0f, paint.ValueAt(new Vector3(1.01f, 0f, 0f)));
            var mid = paint.ValueAt(new Vector3(0.5f, 0f, 0f));
            Assert.That(mid, Is.GreaterThan(0f).And.LessThan(0.75f));

            paint.dabs.Add(new DensityPaint.Dab { centre = Vector3.zero, radius = 1f, strength = 0.75f });
            paint.Invalidate();
            Assert.AreEqual(1f, paint.ValueAt(Vector3.zero), 1e-5f, "clamped");
            Assert.AreEqual(1f / DensityPaint.range, paint.SpacingFactorAt(Vector3.zero), 1e-4f);

            Assert.AreEqual(2, paint.EraseNear(new Vector3(0.1f, 0f, 0f), 0.2f));
            Assert.AreEqual(0f, paint.ValueAt(Vector3.zero));
        }

        // A cube of n x n triangle grids blown out onto the unit sphere.
        static void Sphere(int n, out Vector3[] positions, out int[] triangles)
        {
            var p = new List<Vector3>();
            var t = new List<int>();
            var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
            for (var a = 0; a < 3; a++)
                for (var s = -1; s <= 1; s += 2)
                {
                    var normal = axes[a] * s;
                    var u = axes[(a + 1) % 3] * s;
                    var v = Vector3.Cross(normal, u);
                    var start = p.Count;
                    for (var j = 0; j <= n; j++)
                        for (var i = 0; i <= n; i++)
                            p.Add((0.5f * normal + (i / (float)n - 0.5f) * u + (j / (float)n - 0.5f) * v).normalized);
                    for (var j = 0; j < n; j++)
                        for (var i = 0; i < n; i++)
                        {
                            int q = start + j * (n + 1) + i, r = q + 1, w = q + n + 1, x = w + 1;
                            t.AddRange(new[] { q, r, x, q, x, w });
                        }
                }
            positions = p.ToArray();
            triangles = t.ToArray();
        }

        // Faces per unit area with their centre above y = 0.6, and below it.
        static void Densities(RemeshResult result, out float painted, out float rest)
        {
            var e = result.extraction;
            double aIn = 0, aOut = 0;
            int nIn = 0, nOut = 0;
            foreach (var f in e.faces)
            {
                var c = Vector3.zero;
                foreach (var v in f) c += e.positions[v];
                c /= f.Length;
                var area = 0f;
                for (var k = 1; k + 1 < f.Length; k++)
                    area += Vector3.Cross(e.positions[f[k]] - e.positions[f[0]], e.positions[f[k + 1]] - e.positions[f[0]]).magnitude * 0.5f;
                if (c.y > 0.6f) { nIn++; aIn += area; }
                else if (c.y < 0.3f) { nOut++; aOut += area; }
            }
            painted = (float)(nIn / aIn);
            rest = (float)(nOut / aOut);
        }

        [Test]
        public void PaintedCapGetsSmallerQuadsAndTheRestKeepsItsOwn()
        {
            Sphere(24, out var positions, out var triangles);
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = 600;
            var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
            profile.extractor = RemeshExtractor.IntegerGrid;

            var plain = RemeshPipeline.Solve(positions, triangles, settings, profile);
            Densities(plain, out var plainCap, out var plainRest);

            var paint = new DensityPaint();
            paint.dabs.Add(new DensityPaint.Dab { centre = Vector3.up, radius = 0.9f, strength = 1f });
            paint.dabs.Add(new DensityPaint.Dab { centre = Vector3.up, radius = 0.9f, strength = 1f });
            settings.densityPaint = paint;
            var painted = RemeshPipeline.Solve(positions, triangles, settings, profile);
            Densities(painted, out var cap, out var rest);

            var note = $"plain {plain.extraction.faceCount} faces cap {plainCap:F0} rest {plainRest:F0}; " +
                       $"painted {painted.extraction.faceCount} faces cap {cap:F0} rest {rest:F0}, ratio {painted.paintCountRatio:F2}";
            Assert.That(painted.paintCountRatio, Is.GreaterThan(1f), note);
            Assert.That(cap / rest, Is.GreaterThan(2f), note);
            Assert.That(rest / plainRest, Is.InRange(0.7f, 1.4f), "the unpainted part keeps its density: " + note);
            Assert.That(painted.extraction.faceCount, Is.GreaterThan(plain.extraction.faceCount * 1.1f), note);
        }

        [Test]
        public void LessDetailOnALargeAreaLowersTheCountAndKeepsTheRest()
        {
            // A quarter of the sphere painted for fewer quads. Adapt Quad Count used to read the
            // painted coarse cap as "flat" and triple the target: 826 -> 2,160 faces.
            Sphere(32, out var positions, out var triangles);
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = 800;
            var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
            profile.extractor = RemeshExtractor.IntegerGrid;
            var adaptiveSize = Extraction.IntegerGridMap.adaptiveSize;
            Extraction.IntegerGridMap.adaptiveSize = 0.5f;
            try
            {
                var plain = RemeshPipeline.Solve(positions, triangles, settings, profile);
                Densities(plain, out var plainCap, out var plainRest);

                var paint = new DensityPaint();
                paint.dabs.Add(new DensityPaint.Dab { centre = Vector3.up, radius = 1f, strength = -1f });
                paint.dabs.Add(new DensityPaint.Dab { centre = Vector3.up, radius = 1f, strength = -1f });
                settings.densityPaint = paint;
                var painted = RemeshPipeline.Solve(positions, triangles, settings, profile);
                Densities(painted, out var cap, out var rest);

                var note = $"plain {plain.extraction.faceCount} faces cap {plainCap:F0} rest {plainRest:F0}; " +
                           $"painted {painted.extraction.faceCount} faces cap {cap:F0} rest {rest:F0}";
                Assert.That(painted.extraction.faceCount, Is.LessThan(plain.extraction.faceCount), note);
                Assert.That(cap, Is.LessThan(plainCap * 0.8f), note);
                Assert.That(rest / plainRest, Is.InRange(0.7f, 1.4f), "the unpainted part keeps its density: " + note);
            }
            finally
            {
                Extraction.IntegerGridMap.adaptiveSize = adaptiveSize;
            }
        }

        [Test]
        public void TangentsStayUnitOnMillimetreQuads()
        {
            // A 2 mm quad: its area-weighted tangent sum is under Vector3.Normalize's 1e-5.
            var positions = new[] { Vector3.zero, new Vector3(0.002f, 0f, 0f), new Vector3(0.002f, 0.002f, 0f), new Vector3(0f, 0.002f, 0f) };
            var faces = new List<int[]> { new[] { 0, 1, 2, 3 } };
            var uvs = new[] { new[] { Vector2.zero, new Vector2(0.01f, 0f), new Vector2(0.01f, 0.01f), new Vector2(0f, 0.01f) } };
            var normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            var tangents = TextureBake.Tangents(positions, faces, uvs, normals);
            foreach (var t in tangents[0])
                Assert.AreEqual(1f, new Vector3(t.x, t.y, t.z).magnitude, 1e-3f);
        }
    }
}
