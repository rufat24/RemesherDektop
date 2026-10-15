// Detail Quads: quads built from a simplification of the input. All quads, near the asked count,
// on the surface, and a sharp corner kept where a lattice would round it.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class DetailQuadsTests
    {
        // A cube, each side an n x n grid of triangles.
        static void Cube(int n, out Vector3[] positions, out int[] triangles)
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
                            p.Add(0.5f * normal + (i / (float)n - 0.5f) * u + (j / (float)n - 0.5f) * v);
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

        [Test]
        public void CubeComesOutAllQuadsWithItsCorners()
        {
            Cube(24, out var positions, out var triangles);
            DetailQuads.Build(positions, triangles, 600, out var outPositions, out var faces, out var stats);
            Assert.That(faces.TrueForAll(f => f.Length == 4), stats.ToString());
            Assert.That(faces.Count, Is.InRange(300, 1200), stats.ToString());
            var surface = new SpatialGrid(positions, triangles);
            foreach (var v in outPositions)
            {
                surface.NearestPoint(v, out var d);
                Assert.Less(d, 0.01f);
            }
            // Every corner of the cube has an output vertex on it.
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) - 0.5f, ((c >> 1) & 1) - 0.5f, ((c >> 2) & 1) - 0.5f);
                var best = float.MaxValue;
                foreach (var v in outPositions) best = Mathf.Min(best, (v - corner).magnitude);
                Assert.Less(best, 0.02f, $"corner {corner}");
            }
        }
    }
}
