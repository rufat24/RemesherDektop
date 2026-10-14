// Scan edges: a long crease crossed by short noise chains is kept whole (FeatureGraph
// .joinThroughJunctions), and a strip the extractor lost along an open mesh's edge is filled back
// (HoleFiller.fillOutlineNotches).

using System.Collections.Generic;
using NativeRemesher.Extraction;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class ScanEdgeTests
    {
        // A flat grid of (nx + 1) x (ny + 1) points, unit spacing, triangulated.
        static RemeshTopology FlatGrid(int nx, int ny, out Vector3[] positions, out int[] triangles)
        {
            var p = new List<Vector3>();
            for (var j = 0; j <= ny; j++)
                for (var i = 0; i <= nx; i++)
                    p.Add(new Vector3(i, 0f, j));
            var t = new List<int>();
            for (var j = 0; j < ny; j++)
                for (var i = 0; i < nx; i++)
                {
                    var a = j * (nx + 1) + i;
                    t.Add(a); t.Add(a + nx + 1); t.Add(a + 1);
                    t.Add(a + 1); t.Add(a + nx + 1); t.Add(a + nx + 2);
                }
            positions = p.ToArray();
            triangles = t.ToArray();
            return RemeshTopology.Build(positions, triangles);
        }

        [Test]
        public void LongCreaseCrossedBySpursIsKeptWhole()
        {
            const int nx = 24, ny = 4;
            var topology = FlatGrid(nx, ny, out var positions, out _);
            // Map the input vertices to the topology's (it may weld or reorder).
            int Vertex(int i, int j)
            {
                var target = new Vector3(i, 0f, j);
                for (var v = 0; v < topology.vertexCount; v++)
                    if ((topology.positions[v] - target).sqrMagnitude < 1e-8f) return v;
                return -1;
            }
            var edges = new List<(int a, int b)>();
            for (var i = 0; i < nx; i++) edges.Add((Vertex(i, 2), Vertex(i + 1, 2)));
            // A one-edge spur up from every third vertex: junctions that cut the line into pieces
            // three long.
            for (var i = 3; i < nx; i += 3) edges.Add((Vertex(i, 2), Vertex(i, 3)));
            var graph = FeatureGraph.Empty(topology.vertexCount).WithEdges(topology, edges);

            var was = FeatureGraph.joinThroughJunctions;
            try
            {
                FeatureGraph.joinThroughJunctions = false;
                var apart = graph.KeepCoherentCurves(topology, 8f, 0.85f, out var keptApart, out _);
                FeatureGraph.joinThroughJunctions = true;
                var joined = graph.KeepCoherentCurves(topology, 8f, 0.85f, out var keptJoined, out _);

                for (var i = 0; i < nx; i++)
                {
                    Assert.IsTrue(joined.IsFeatureEdge(Vertex(i, 2), Vertex(i + 1, 2)), $"line edge {i} dropped when joined");
                    Assert.IsFalse(apart.IsFeatureEdge(Vertex(i, 2), Vertex(i + 1, 2)), $"line edge {i} kept without joining");
                }
                for (var i = 3; i < nx; i += 3)
                    Assert.IsFalse(joined.IsFeatureEdge(Vertex(i, 2), Vertex(i, 3)), $"spur at {i} kept");
                Assert.Greater(keptJoined, keptApart);
            }
            finally { FeatureGraph.joinThroughJunctions = was; }
        }

        [Test]
        public void ChamferedCornerIsFoundAtScale()
        {
            // A right-angled corner rounded by a 0.1-wide chamfer: two bends of 45 degrees, extruded.
            var profile = new[]
            {
                new Vector2(-3f, 0f), new Vector2(-2f, 0f), new Vector2(-1f, 0f), new Vector2(-0.1f, 0f),
                new Vector2(0f, 0.1f), new Vector2(0f, 1f), new Vector2(0f, 2f), new Vector2(0f, 3f),
            };
            const int rows = 10;
            var p = new List<Vector3>();
            for (var j = 0; j <= rows; j++)
                foreach (var q in profile) p.Add(new Vector3(q.x, j, q.y));
            var t = new List<int>();
            var w = profile.Length;
            for (var j = 0; j < rows; j++)
                for (var i = 0; i + 1 < w; i++)
                {
                    var a = j * w + i;
                    t.Add(a); t.Add(a + 1); t.Add(a + w);
                    t.Add(a + 1); t.Add(a + w + 1); t.Add(a + w);
                }
            var topology = RemeshTopology.Build(p.ToArray(), t.ToArray());
            int CreaseEdgesAlongTheCorner(FeatureGraph g)
            {
                var count = 0;
                for (var j = 0; j < rows; j++)
                    for (var i = 3; i <= 4; i++)
                    {
                        var a = Find(topology, p[j * w + i]);
                        var b = Find(topology, p[(j + 1) * w + i]);
                        if (g.IsFeatureEdge(a, b)) count++;
                    }
                return count;
            }
            Assert.AreEqual(0, CreaseEdgesAlongTheCorner(FeatureGraph.Detect(topology, 60f)), "edge by edge: 45 < 60");
            var scaled = FeatureGraph.DetectAtScale(topology, 60f, 1f);
            Assert.GreaterOrEqual(CreaseEdgesAlongTheCorner(scaled), rows, "at scale: the corner's 90 degrees");
            // The flat faces either side stay quiet.
            for (var j = 0; j < rows; j++)
                Assert.IsFalse(scaled.IsFeatureEdge(Find(topology, p[j * w + 1]), Find(topology, p[(j + 1) * w + 1])));
        }

        static int Find(RemeshTopology topology, Vector3 target)
        {
            for (var v = 0; v < topology.vertexCount; v++)
                if ((topology.positions[v] - target).sqrMagnitude < 1e-8f) return v;
            return -1;
        }

        [Test]
        public void NotchAlongTheOutlineIsFilled()
        {
            const int n = 12;
            var topology = FlatGrid(n, n, out var positions, out var triangles);
            topology.CollectBoundaryEdges(out var outlineEdges);
            // The output: the same grid as quads, less a block 4 wide and 3 deep at the x = n edge.
            var faces = new List<int[]>();
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    if (i >= n - 3 && j >= 4 && j < 8) continue;
                    var a = j * (n + 1) + i;
                    faces.Add(new[] { a, a + n + 1, a + n + 2, a + 1 });
                }
            int Open(List<int[]> fs)
            {
                var uses = new Dictionary<long, int>(EdgeKey.Comparer);
                foreach (var f in fs)
                    for (var k = 0; k < f.Length; k++)
                    {
                        var key = EdgeKey.PackUndirected(f[k], f[(k + 1) % f.Length]);
                        uses[key] = uses.TryGetValue(key, out var c) ? c + 1 : 1;
                    }
                var open = 0;
                foreach (var c in uses.Values) if (c == 1) open++;
                return open;
            }
            var before = Open(faces);

            var was = HoleFiller.fillOutlineNotches;
            try
            {
                HoleFiller.fillOutlineNotches = false;
                var plain = new List<int[]>(faces);
                var plainStats = HoleFiller.Fill(plain, positions.Length, false, positions,
                    topology.positions, outlineEdges, 1f, topology.triangles);
                Assert.AreEqual(before, Open(plain), "without the notch filler the notch stays");

                HoleFiller.fillOutlineNotches = true;
                var stats = HoleFiller.Fill(faces, positions.Length, false, positions,
                    topology.positions, outlineEdges, 1f, topology.triangles);
                Assert.AreEqual(1, stats.outlineNotchesFilled, stats.ToString());
                // The outline is all that is open again: 4 n edges, give or take the new points'
                // resampling of the notch's mouth.
                Assert.That(Open(faces), Is.InRange(4 * n - 2, 4 * n + 2), stats.ToString());
                // Every added point on the plane, inside the grid.
                foreach (var p in stats.addedPositions)
                {
                    Assert.AreEqual(0f, p.y, 1e-4f);
                    Assert.That(p.x, Is.InRange(-1e-4f, n + 1e-4f));
                }
            }
            finally { HoleFiller.fillOutlineNotches = was; }
        }
    }
}
