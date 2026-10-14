// Texture baking: the new atlas must not overlap itself, and every texel must read the source at its
// own surface point -- in particular on faces that straddle a seam of the source's atlas, which the
// per-corner transfer can only clamp to one side.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class TextureBakeTests
    {
        static List<int[]> Grid(int n, out Vector3[] positions)
        {
            var p = new List<Vector3>();
            for (var j = 0; j <= n; j++)
                for (var i = 0; i <= n; i++)
                    p.Add(new Vector3(i, 0f, j));
            var faces = new List<int[]>();
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    var a = j * (n + 1) + i;
                    faces.Add(new[] { a, a + n + 1, a + n + 2, a + 1 });
                }
            positions = p.ToArray();
            return faces;
        }

        [Test]
        public void UnwrapPacksWithoutOverlap()
        {
            var faces = Grid(40, out var positions);
            var stats = new TextureBake.Stats();
            var uvs = TextureBake.Unwrap(positions, faces, 1024, stats);
            Assert.AreEqual(faces.Count, uvs.Length);
            // Each face an axis-aligned square of the same size, inside the atlas, none overlapping.
            var size = -1f;
            var boxes = new List<Rect>();
            for (var f = 0; f < faces.Count; f++)
            {
                var uv = uvs[f];
                Assert.AreEqual(4, uv.Length);
                float x0 = 1, y0 = 1, x1 = 0, y1 = 0;
                foreach (var q in uv)
                {
                    Assert.That(q.x, Is.InRange(0f, 1f));
                    Assert.That(q.y, Is.InRange(0f, 1f));
                    x0 = Mathf.Min(x0, q.x); y0 = Mathf.Min(y0, q.y);
                    x1 = Mathf.Max(x1, q.x); y1 = Mathf.Max(y1, q.y);
                }
                Assert.AreEqual(x1 - x0, y1 - y0, 1e-5f, $"face {f} is not square in UV");
                if (size < 0) size = x1 - x0;
                Assert.AreEqual(size, x1 - x0, 1e-5f, $"face {f} has another texel size");
                boxes.Add(new Rect(x0, y0, x1 - x0, y1 - y0));
            }
            var shrink = size * 0.01f;
            for (var a = 0; a < boxes.Count; a++)
                for (var b = a + 1; b < boxes.Count; b++)
                {
                    var ra = new Rect(boxes[a].x + shrink, boxes[a].y + shrink, boxes[a].width - 2 * shrink, boxes[a].height - 2 * shrink);
                    Assert.IsFalse(ra.Overlaps(boxes[b]), $"faces {a} and {b} overlap in UV");
                }
            Assert.Greater(stats.fill, 0.5f, stats.ToString());
        }

        // A unit sphere with its UV seam at u = 0 / 1, and a texture whose red is u and blue is v,
        // so a baked colour says where on the source it was read.
        static void SeamedSphere(int lat, int lon, out Vector3[] positions, out int[] triangles, out Vector2[] uvs)
        {
            var p = new List<Vector3>();
            var uv = new List<Vector2>();
            for (var i = 0; i <= lat; i++)
            {
                var theta = Mathf.PI * i / lat;
                for (var j = 0; j <= lon; j++)
                {
                    var phi = 2f * Mathf.PI * (j % lon) / lon;
                    p.Add(new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi)));
                    uv.Add(new Vector2(j / (float)lon, 1f - i / (float)lat));
                }
            }
            var t = new List<int>();
            for (var i = 0; i < lat; i++)
                for (var j = 0; j < lon; j++)
                {
                    var a = i * (lon + 1) + j;
                    var b = a + 1;
                    var c = a + lon + 1;
                    var d = c + 1;
                    t.Add(a); t.Add(c); t.Add(b);
                    t.Add(b); t.Add(c); t.Add(d);
                }
            positions = p.ToArray();
            triangles = t.ToArray();
            uvs = uv.ToArray();
        }

        [Test]
        public void BakeReadsEachTexelAtItsOwnPointAcrossTheSeam()
        {
            SeamedSphere(32, 64, out var sp, out var st, out var suv);
            const int W = 256, H = 128;
            var px = new Color32[W * H];
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                    px[y * W + x] = new Color32((byte)(255f * x / (W - 1)), 0, (byte)(255f * y / (H - 1)), 255);

            // An equatorial band of quads turned half a cell, so a column of them straddles the seam.
            const int seg = 24;
            var positions = new List<Vector3>();
            for (var row = 0; row < 3; row++)
            {
                var theta = Mathf.PI * (0.35f + 0.15f * row);
                for (var j = 0; j < seg; j++)
                {
                    var phi = 2f * Mathf.PI * (j + 0.5f) / seg;
                    positions.Add(new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi)));
                }
            }
            var faces = new List<int[]>();
            for (var row = 0; row < 2; row++)
                for (var j = 0; j < seg; j++)
                {
                    int a = row * seg + j, b = row * seg + (j + 1) % seg;
                    faces.Add(new[] { a, a + seg, b + seg, b });
                }

            var stats = new TextureBake.Stats();
            var target = positions.ToArray();
            var uvs = TextureBake.Unwrap(target, faces, 512, stats);
            var atlas = TextureBake.Bake(sp, st, suv, null, new[] { new TextureBake.Image(px, W, H) },
                target, faces, uvs, 512, stats);

            // At every face's centre: the source point the baked colour names must be the face's own.
            var worst = 0f;
            for (var f = 0; f < faces.Count; f++)
            {
                var c = Vector2.zero;
                foreach (var q in uvs[f]) c += q;
                c /= 4f;
                var texel = atlas[Mathf.FloorToInt(c.y * 512) * 512 + Mathf.FloorToInt(c.x * 512)];
                var u = texel.r / 255f;
                var v = texel.b / 255f;
                var theta = Mathf.PI * (1f - v);
                var phi = 2f * Mathf.PI * u;
                var named = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi));
                var centre = Vector3.zero;
                foreach (var i in faces[f]) centre += target[i];
                worst = Mathf.Max(worst, Vector3.Angle(named, centre.normalized));
            }
            // Cells are 15 degrees wide; a texel naming the far side of the atlas would be ~180.
            Assert.Less(worst, 6f, stats.ToString());
        }

        [Test]
        public void PipelineBakeReplacesTheUvs()
        {
            SeamedSphere(32, 64, out var positions, out var triangles, out var uvs);
            var px = new Color32[64 * 64];
            for (var i = 0; i < px.Length; i++) px[i] = new Color32(200, 100, 50, 255);
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = 400;
            var result = RemeshPipeline.Solve(positions, triangles, settings,
                RemeshProfile.For(RemeshProfileKind.GameAsset), null, uvs);
            RemeshPipeline.BakeTexture(result, positions, triangles, uvs, null,
                new[] { new TextureBake.Image(px, 64, 64) }, 512);
            Assert.NotNull(result.bakedTexture);
            Assert.AreEqual(512 * 512, result.bakedTexture.Length);
            Assert.IsNull(result.extraction.uvs);
            Assert.AreEqual(result.extraction.faces.Count, result.extraction.faceUvs.Length);
            // A flat colour in, the same colour out, gutters included.
            foreach (var c in result.bakedTexture)
            {
                Assert.AreEqual(200, c.r, 1);
                Assert.AreEqual(100, c.g, 1);
            }
            var mesh = QuadExtractor.ToMesh(result.extraction, "baked", false);
            try
            {
                var meshUvs = new List<Vector2>();
                mesh.GetUVs(0, meshUvs);
                Assert.AreEqual(mesh.vertexCount, meshUvs.Count);
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
