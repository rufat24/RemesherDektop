// Models in parts: the quad budget split by area, and one atlas shared by every part -- each part
// gets UVs for all its faces, no two parts' charts overlap, and tangents come back in each part's
// own space.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class PartBakeTests
    {
        // An n x n grid of unit quads in the xz plane, facing +y; and the same as triangles.
        static void Grid(int n, out Vector3[] positions, out List<int[]> quads, out int[] triangles)
        {
            var p = new List<Vector3>();
            for (var j = 0; j <= n; j++)
                for (var i = 0; i <= n; i++)
                    p.Add(new Vector3(i, 0f, j));
            quads = new List<int[]>();
            var t = new List<int>();
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    var a = j * (n + 1) + i;
                    quads.Add(new[] { a, a + n + 1, a + n + 2, a + 1 });
                    t.AddRange(new[] { a, a + n + 1, a + n + 2, a, a + n + 2, a + 1 });
                }
            positions = p.ToArray();
            triangles = t.ToArray();
        }

        static PartBake.Part Part(int n, Matrix4x4 toModel)
        {
            Grid(n, out var positions, out var quads, out var triangles);
            var normals = new Vector3[positions.Length];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            var extraction = new QuadExtractionResult { positions = positions, faces = quads, normals = normals, quadCount = quads.Count };
            // The source a little above the part, in the part's own space.
            var source = new Vector3[positions.Length];
            for (var i = 0; i < source.Length; i++) source[i] = positions[i] + new Vector3(0f, 0.05f, 0f);
            return new PartBake.Part
            {
                result = new RemeshResult { extraction = extraction },
                toModel = toModel,
                positions = source,
                triangles = triangles,
            };
        }

        [Test]
        public void BudgetIsSplitBySurfaceArea()
        {
            var counts = PartBake.SplitTarget(new double[] { 3, 1, 0.0001 }, 4000, 16);
            Assert.AreEqual(3000, counts[0]);
            Assert.AreEqual(1000, counts[1]);
            Assert.AreEqual(16, counts[2], "a tiny part still gets the least");
        }

        [Test]
        public void PartsShareOneAtlasAndKeepTheirOwnTangents()
        {
            // Two parts: one as is, one turned a quarter about z, moved and scaled up -- in the model's
            // space it is a wall, four times the first one's area per quad.
            var a = Part(6, Matrix4x4.identity);
            // Columns: x -> 2y, y -> -2x, z -> 2z, then 20 along x (built by hand: no engine calls).
            var turn = new Matrix4x4(new Vector4(0f, 2f, 0f, 0f), new Vector4(-2f, 0f, 0f, 0f), new Vector4(0f, 0f, 2f, 0f), new Vector4(20f, 0f, 0f, 1f));
            var b = Part(4, turn);
            var parts = new List<PartBake.Part> { a, b };
            var shared = PartBake.Bake(parts, null, false, true, 512);

            Assert.NotNull(shared);
            Assert.NotNull(shared.bakedNormalMap);
            var rects = new List<(int part, Rect box)>();
            for (var p = 0; p < parts.Count; p++)
            {
                var ex = parts[p].result.extraction;
                Assert.NotNull(ex.faceUvs, $"part {p} has no UVs");
                Assert.AreEqual(ex.faces.Count, ex.faceUvs.Length, $"part {p}: a UV set per face");
                Assert.NotNull(ex.faceTangents, $"part {p} has no tangents");
                Assert.AreEqual(ex.faces.Count, ex.faceTangents.Length);
                for (var f = 0; f < ex.faces.Count; f++)
                {
                    float x0 = 1, y0 = 1, x1 = 0, y1 = 0;
                    foreach (var q in ex.faceUvs[f])
                    {
                        x0 = Mathf.Min(x0, q.x); y0 = Mathf.Min(y0, q.y);
                        x1 = Mathf.Max(x1, q.x); y1 = Mathf.Max(y1, q.y);
                    }
                    rects.Add((p, new Rect(x0, y0, x1 - x0, y1 - y0)));
                    // The part's own normal is +y in its own space: its tangents lie in its xz plane.
                    foreach (var t in ex.faceTangents[f])
                        Assert.AreEqual(0f, t.y, 1e-3f, $"part {p} face {f}: tangent {t} not in the part's own plane");
                }
            }
            // No chart of one part over a chart of the other.
            foreach (var (pa, ra) in rects)
                foreach (var (pb, rb) in rects)
                {
                    if (pa >= pb) continue;
                    var shrunk = new Rect(ra.x + ra.width * 0.01f, ra.y + ra.height * 0.01f, ra.width * 0.98f, ra.height * 0.98f);
                    Assert.IsFalse(shrunk.Overlaps(rb), "the parts' charts overlap in the shared atlas");
                }
            // The same texel density on both: the bigger part's quads (2 x 2 in the model) cover 4x the UV area.
            float AreaOf(int p) { var s = 0f; foreach (var (q, r) in rects) if (q == p) s += r.width * r.height; return s; }
            var perQuadA = AreaOf(0) / a.result.extraction.faces.Count;
            var perQuadB = AreaOf(1) / b.result.extraction.faces.Count;
            Assert.AreEqual(4f, perQuadB / perQuadA, 0.6f, "texel density alike on both parts");
        }
    }
}
