// QuadReduction: a model that already is clean quads is reduced by taking out quad strips, its loops
// kept; anything else is left to the remesher.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class QuadReductionTests
    {
        // A grid of quads over (u, v) in [0, 1]^2, mapped by `at`, written as an importer writes it:
        // each quad as two consecutive triangles.
        static void Grid(int nu, int nv, System.Func<float, float, Vector3> at, out Vector3[] positions,
            out int[] triangles, bool wrapU = false)
        {
            var columns = wrapU ? nu : nu + 1;
            var p = new List<Vector3>();
            for (var j = 0; j <= nv; j++)
                for (var i = 0; i < columns; i++)
                    p.Add(at(i / (float)nu, j / (float)nv));
            var t = new List<int>();
            for (var j = 0; j < nv; j++)
                for (var i = 0; i < nu; i++)
                {
                    var i1 = wrapU ? (i + 1) % nu : i + 1;
                    int a = j * columns + i, b = j * columns + i1, c = (j + 1) * columns + i1, d = (j + 1) * columns + i;
                    t.AddRange(new[] { a, b, c, a, c, d });
                }
            positions = p.ToArray();
            triangles = t.ToArray();
        }

        // A panel with a rolled lip along one edge: flat 2 x 1, the last tenth curling down and back.
        static Vector3 Panel(float u, float v)
        {
            var x = 2f * u;
            if (v < 0.9f) return new Vector3(x, 0f, v);
            var a = (v - 0.9f) / 0.1f * Mathf.PI;
            return new Vector3(x, -0.02f + 0.02f * Mathf.Cos(a), 0.9f + 0.02f * Mathf.Sin(a));
        }

        [Test]
        public void AQuadPanelIsReducedToTheTargetAllQuadsAndKeepsItsLip()
        {
            Grid(80, 60, Panel, out var positions, out var triangles);
            Assert.That(QuadReduction.Reduce(positions, triangles, 200, out var p, out var faces, out var stats),
                stats.ToString());
            Assert.That(faces.Count, Is.InRange(150, 260), stats.ToString());
            foreach (var f in faces) Assert.That(f.Length, Is.EqualTo(4));
            // The lip's underside (y well below the panel) is still there.
            var under = 0;
            foreach (var q in p) if (q.y < -0.025f) under++;
            Assert.That(under, Is.GreaterThan(0), "the rolled lip was flattened");
            Assert.That(stats.irregular, Is.LessThan(0.05f), stats.ToString());
        }

        [Test]
        public void AnOpenTubeStaysOpenAndRound()
        {
            Grid(64, 24, (u, v) => new Vector3(Mathf.Cos(2f * Mathf.PI * u), Mathf.Sin(2f * Mathf.PI * u), 3f * v),
                out var positions, out var triangles, wrapU: true);
            Assert.That(QuadReduction.Reduce(positions, triangles, 300, out var p, out var faces, out var stats),
                stats.ToString());
            foreach (var q in p)
                Assert.That(new Vector2(q.x, q.y).magnitude, Is.InRange(0.85f, 1.01f), stats.ToString());
        }

        [Test]
        public void TrianglesInAnyOrderPairBackIntoQuads()
        {
            Grid(40, 30, (u, v) => new Vector3(4f * u, 0.3f * Mathf.Sin(3f * u), 3f * v), out var positions, out var triangles);
            // Reordered and rotated, as Unity's Optimize Polygons leaves them.
            var rng = new System.Random(3);
            var count = triangles.Length / 3;
            var order = new int[count];
            for (var i = 0; i < count; i++) order[i] = i;
            for (var i = count - 1; i > 0; i--) { var j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
            var shuffled = new int[triangles.Length];
            for (var i = 0; i < count; i++)
            {
                var r = rng.Next(3);
                for (var k = 0; k < 3; k++) shuffled[3 * i + k] = triangles[3 * order[i] + (k + r) % 3];
            }
            Assert.That(QuadReduction.Recover(positions, shuffled, out _, out _, out var stats), stats.ToString());
            Assert.That(stats.quadShare, Is.GreaterThan(0.99f), stats.ToString());
        }

        [Test]
        public void ATriangleMeshIsLeftToTheRemesher()
        {
            // An icosphere: even, near-equilateral triangles, as a sculpt's are. They pair up, but into
            // 60-degree rhombi, not squares.
            var t = (1f + Mathf.Sqrt(5f)) / 2f;
            var p = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            var tris = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            for (var i = 0; i < p.Count; i++) p[i] = p[i].normalized;
            for (var level = 0; level < 4; level++)
            {
                var mid = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    var key = a < b ? (long)a << 32 | (uint)b : (long)b << 32 | (uint)a;
                    if (mid.TryGetValue(key, out var m)) return m;
                    p.Add(((p[a] + p[b]) * 0.5f).normalized);
                    return mid[key] = p.Count - 1;
                }
                var next = new List<int>();
                for (var k = 0; k < tris.Count; k += 3)
                {
                    int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                tris = next;
            }
            Assert.That(QuadReduction.Reduce(p.ToArray(), tris.ToArray(), 100, out _, out _, out var stats), Is.False);
            Assert.That(stats.reason, Is.EqualTo("not quads"), stats.ToString());
        }

        [Test]
        public void ReadMeshReadsAKeepQuadsMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 0, 1), Vector3.forward, new Vector3(2, 0, 0), new Vector3(2, 0, 1) };
            mesh.SetIndices(new[] { 0, 3, 2, 1, 1, 2, 5, 4 }, MeshTopology.Quads, 0);
            RemeshTopology.ReadMesh(mesh, out _, out var triangles, out _, out var submesh);
            Object.DestroyImmediate(mesh);
            Assert.That(triangles.Length, Is.EqualTo(12));
            Assert.That(submesh.Length, Is.EqualTo(4));
            Assert.That(QuadReduction.Recover(new[] { Vector3.zero, Vector3.right, new Vector3(1, 0, 1), Vector3.forward, new Vector3(2, 0, 0), new Vector3(2, 0, 1) },
                triangles, out _, out var faces, out var stats), stats.ToString());
            Assert.That(stats.inputQuads, Is.EqualTo(2));
        }
    }
}
