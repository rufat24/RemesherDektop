// Material Borders As Loops: the decimator keeps a material border as a chain of the input's own border
// vertices, and the border edges between labels are found on any mesh made from the input.

using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class MaterialTests
    {
        // A flat n x n grid over [0, 2] x [0, 1], gently bumped so the quadrics have something to
        // hold; the left half (x < 1) label 0, the right half label 1.
        static void Grid(int n, out Vector3[] positions, out int[] triangles, out int[] labels)
        {
            positions = new Vector3[(2 * n + 1) * (n + 1)];
            for (var j = 0; j <= n; j++)
                for (var i = 0; i <= 2 * n; i++)
                {
                    float x = (float)i / n, y = (float)j / n;
                    positions[j * (2 * n + 1) + i] = new Vector3(x, y, 0.05f * Mathf.Sin(3f * x) * Mathf.Cos(2f * y));
                }
            var tris = new System.Collections.Generic.List<int>();
            var labs = new System.Collections.Generic.List<int>();
            for (var j = 0; j < n; j++)
                for (var i = 0; i < 2 * n; i++)
                {
                    int a = j * (2 * n + 1) + i, b = a + 1, c = a + 2 * n + 1, d = c + 1;
                    tris.AddRange(new[] { a, b, d, a, d, c });
                    var label = i < n ? 0 : 1;
                    labs.Add(label);
                    labs.Add(label);
                }
            triangles = tris.ToArray();
            labels = labs.ToArray();
        }

        [Test]
        public void BorderEdgesAreTheEdgesBetweenLabels()
        {
            Grid(4, out _, out var triangles, out var labels);
            var edges = MaterialLabels.BorderEdges(triangles, labels);
            Assert.AreEqual(4, edges.Count, "the column x = 1, four edges high");
        }

        [Test]
        public void DecimationKeepsTheBorderOnInputBorderVertices()
        {
            Grid(40, out var positions, out var triangles, out var labels);
            var materials = new MaterialLabels(positions, triangles, labels);
            var options = DecimateOptions.ForTriangleCount(400);
            options.labels = materials;
            PreDecimate.Decimate(positions, triangles, options, out var stats, out var outPositions, out var outTriangles);
            Assert.Less(outTriangles.Length / 3, triangles.Length / 3 / 4, "decimated");
            var outLabels = materials.ForTriangles(outPositions, outTriangles);
            var border = MaterialLabels.BorderEdges(outTriangles, outLabels);
            Assert.Greater(border.Count, 0);
            Assert.Less(border.Count, 40, "the border simplified along itself");
            double length = 0;
            foreach (var (a, b) in border)
            {
                // On the input's border line x = 1, end to end.
                Assert.AreEqual(1f, outPositions[a].x, 1e-5f);
                Assert.AreEqual(1f, outPositions[b].x, 1e-5f);
                length += Mathf.Abs(outPositions[a].y - outPositions[b].y);
            }
            Assert.AreEqual(1.0, length, 1e-4, "the whole border, once");
        }
    }
}
