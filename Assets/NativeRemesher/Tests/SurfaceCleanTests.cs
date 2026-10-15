// SurfaceClean: a lone spike is smoothed into its ring; a cube's corners and edges, which their
// neighbours share, are not.

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
