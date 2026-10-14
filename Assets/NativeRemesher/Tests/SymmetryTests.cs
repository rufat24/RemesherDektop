// Mirror symmetry: the clip keeps exactly the positive side with the cut on the plane, and the
// mirror shares the seam's vertices instead of leaving two open borders.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class SymmetryTests
    {
        // A flat grid of n x n quads over [-1, 1]^2, split into triangles.
        static void Grid(int n, out Vector3[] positions, out int[] triangles)
        {
            positions = new Vector3[(n + 1) * (n + 1)];
            for (var j = 0; j <= n; j++)
                for (var i = 0; i <= n; i++)
                    positions[j * (n + 1) + i] = new Vector3(-1f + 2f * i / n, -1f + 2f * j / n, 0f);
            var tris = new List<int>();
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                    tris.AddRange(new[] { a, b, d, a, d, c });
                }
            triangles = tris.ToArray();
        }

        [Test]
        public void ClipKeepsThePositiveSideWithTheCutOnThePlane()
        {
            // An odd count, so the plane x = 0 runs through the middle of a column of triangles.
            Grid(5, out var positions, out var triangles);
            Symmetry.Clip(positions, triangles, SymmetryAxis.X, 0f, out var half, out var halfTriangles);
            Assert.Greater(halfTriangles.Length, 0);
            double area = 0;
            for (var t = 0; t < halfTriangles.Length; t += 3)
            {
                var a = half[halfTriangles[t]];
                var b = half[halfTriangles[t + 1]];
                var c = half[halfTriangles[t + 2]];
                foreach (var p in new[] { a, b, c }) Assert.GreaterOrEqual(p.x, 0f);
                var cross = Vector3.Cross(b - a, c - a);
                Assert.Greater(cross.z, 0f, "winding kept");
                area += 0.5 * cross.magnitude;
            }
            Assert.AreEqual(2.0, area, 1e-4, "half of the 2 x 2 square");
        }

        [Test]
        public void MirrorWeldsTheSeamAndTurnsTheFaces()
        {
            // Two quads on the positive side, their left edge on the plane.
            var half = new QuadExtractionResult
            {
                positions = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
                    new Vector3(1, 2, 0), new Vector3(0, 2, 0),
                },
            };
            half.faces.Add(new[] { 0, 1, 2, 3 });
            half.faces.Add(new[] { 3, 2, 4, 5 });
            var stats = new Symmetry.Stats();
            var full = Symmetry.Mirror(half, SymmetryAxis.X, 0f, 0.25f, stats);
            Assert.AreEqual(3, stats.seamVertices);
            Assert.AreEqual(6 + 3, full.positions.Length, "seam vertices shared");
            Assert.AreEqual(4, full.faces.Count);
            Assert.AreEqual(4, full.quadCount);
            // Every edge on the seam is now inside: used by two faces.
            var use = new Dictionary<(int, int), int>();
            foreach (var f in full.faces)
                for (var i = 0; i < 4; i++)
                {
                    int a = f[i], b = f[(i + 1) % 4];
                    var key = (Mathf.Min(a, b), Mathf.Max(a, b));
                    use[key] = use.TryGetValue(key, out var u) ? u + 1 : 1;
                }
            Assert.AreEqual(2, use[(0, 3)]);
            Assert.AreEqual(2, use[(3, 5)]);
            // The mirrored faces face the same way as the originals.
            foreach (var f in full.faces)
            {
                var p = full.positions;
                var n = Vector3.Cross(p[f[1]] - p[f[0]], p[f[2]] - p[f[0]]);
                Assert.Greater(n.z, 0f);
            }
        }
    }
}
