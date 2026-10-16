// SurfaceClean: a lone spike is smoothed into its ring; a cube's corners and edges, which their
// neighbours share, are not; against its surface, a sharp cone's apex is kept.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class SurfaceCleanTests
    {
        // An n x n grid of quads on the plane y = 0.
        static void Grid(int n, out Vector3[] positions, out List<int[]> faces)
        {
            positions = new Vector3[(n + 1) * (n + 1)];
            for (var j = 0; j <= n; j++)
                for (var i = 0; i <= n; i++)
                    positions[j * (n + 1) + i] = new Vector3(i, 0f, j);
            faces = new List<int[]>();
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    var q = j * (n + 1) + i;
                    faces.Add(new[] { q, q + n + 1, q + n + 2, q + 1 });
                }
        }

        [Test]
        public void LoneSpikeIsSmoothedAndTheRestStays()
        {
            Grid(8, out var positions, out var faces);
            var spike = 4 * 9 + 4;
            positions[spike].y = 0.8f;
            var stats = SurfaceClean.Apply(positions, faces, 2);
            Assert.Less(Mathf.Abs(positions[spike].y), 0.05f, stats.ToString());
            for (var v = 0; v < positions.Length; v++)
                if (v != spike) Assert.AreEqual(0f, positions[v].y, 1e-6f, $"vertex {v}");
        }

        // A cone of quads round the z axis, apex at the origin, a triangle fan at the apex: a sharp
        // tip, a lone vertex out of its ring as much as a spike is.
        static void Cone(int rings, int segments, float halfAngle, out Vector3[] positions, out List<int[]> faces)
        {
            var t = Mathf.Tan(halfAngle * Mathf.Deg2Rad);
            var p = new List<Vector3> { Vector3.zero };
            for (var i = 1; i <= rings; i++)
                for (var j = 0; j < segments; j++)
                {
                    var a = 2f * Mathf.PI * j / segments;
                    var s = i / (float)rings;
                    p.Add(new Vector3(s * t * Mathf.Cos(a), s * t * Mathf.Sin(a), -s));
                }
            faces = new List<int[]>();
            for (var j = 0; j < segments; j++) faces.Add(new[] { 0, 1 + j, 1 + (j + 1) % segments });
            for (var i = 1; i < rings; i++)
                for (var j = 0; j < segments; j++)
                {
                    int a = 1 + (i - 1) * segments + j, b = 1 + (i - 1) * segments + (j + 1) % segments;
                    faces.Add(new[] { a, a + segments, b + segments, b });
                }
            positions = p.ToArray();
        }

        static SpatialGrid Surface(Vector3[] positions, List<int[]> faces)
        {
            var triangles = new List<int>();
            foreach (var f in faces)
                for (var k = 1; k + 1 < f.Length; k++) triangles.AddRange(new[] { f[0], f[k], f[k + 1] });
            return new SpatialGrid(positions, triangles.ToArray());
        }

        [Test]
        public void ConeTipIsKeptAgainstItsSurfaceAndCutWithout()
        {
            // The surface: the same cone, finer.
            Cone(100, 64, 25f, out var fine, out var fineFaces);
            var surface = Surface(fine, fineFaces);

            Cone(10, 8, 25f, out var kept, out var faces);
            var stats = SurfaceClean.Apply(kept, faces, 2, surface);
            Assert.AreEqual(Vector3.zero, kept[0], "the apex: " + stats);
            Assert.AreEqual(1, stats.keptTips, stats.ToString());

            Cone(10, 8, 25f, out var cut, out faces);
            stats = SurfaceClean.Apply(cut, faces, 2);
            Assert.Greater(cut[0].magnitude, 0.05f, "without the surface the apex is smoothed: " + stats);
        }

        [Test]
        public void SpikeOverANarrowPitIsStillSmoothedAgainstTheSurface()
        {
            // The surface a plane with a pit a third of a cell wide under the middle vertex, which
            // sits at its bottom: the collar's pop-out, not a tip.
            Grid(8, out var positions, out var faces);
            var spike = 4 * 9 + 4;
            positions[spike].y = -1.2f;   // 0.77 edges out of its ring: sharp enough to be tested
            var fine = new Vector3[81 * 81];
            for (var j = 0; j <= 80; j++)
                for (var i = 0; i <= 80; i++)
                {
                    float x = i * 0.1f, z = j * 0.1f;
                    var inPit = Mathf.Abs(x - 4f) < 0.16f && Mathf.Abs(z - 4f) < 0.16f;
                    fine[j * 81 + i] = new Vector3(x, inPit ? -1.2f : 0f, z);
                }
            var fineFaces = new List<int[]>();
            for (var j = 0; j < 80; j++)
                for (var i = 0; i < 80; i++)
                {
                    var q = j * 81 + i;
                    fineFaces.Add(new[] { q, q + 81, q + 82, q + 1 });
                }
            var stats = SurfaceClean.Apply(positions, faces, 2, Surface(fine, fineFaces));
            Assert.Less(Mathf.Abs(positions[spike].y), 0.05f, stats.ToString());
            Assert.AreEqual(0, stats.keptTips, stats.ToString());
        }

        [Test]
        public void CubeIsLeftAlone()
        {
            // A cube of 3 x 3 quads a side, welded.
            var index = new Dictionary<Vector3Int, int>();
            var positions = new List<Vector3>();
            var faces = new List<int[]>();
            int Vertex(Vector3Int p)
            {
                if (!index.TryGetValue(p, out var i)) { i = positions.Count; index[p] = i; positions.Add(p); }
                return i;
            }
            var axes = new[] { Vector3Int.right, Vector3Int.up, new Vector3Int(0, 0, 1) };
            for (var a = 0; a < 3; a++)
                for (var s = 0; s <= 1; s++)
                {
                    var u = axes[(a + 1) % 3];
                    var w = axes[(a + 2) % 3];
                    var o = axes[a] * (3 * s);
                    for (var j = 0; j < 3; j++)
                        for (var i = 0; i < 3; i++)
                        {
                            var q = new[] { o + u * i + w * j, o + u * (i + 1) + w * j, o + u * (i + 1) + w * (j + 1), o + u * i + w * (j + 1) };
                            var f = new int[4];
                            for (var k = 0; k < 4; k++) f[k] = Vertex(q[s == 0 ? 3 - k : k]);
                            faces.Add(f);
                        }
                }
            var before = positions.ToArray();
            var after = positions.ToArray();
            var stats = SurfaceClean.Apply(after, faces, 2);
            for (var v = 0; v < before.Length; v++)
                Assert.AreEqual(before[v], after[v], $"vertex {v} moved: {stats}");
        }
    }
}
