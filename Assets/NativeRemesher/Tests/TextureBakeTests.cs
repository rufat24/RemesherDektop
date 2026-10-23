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
        public void NormalMapDecodesToTheSourceNormal()
        {
            // Low poly: a flat 6 x 6 grid facing +y. Source: a plane tilted along x above it.
            const int n = 6;
            var faces = Grid(n, out var positions);
            const float slope = 0.15f;
            var sp = new List<Vector3>();
            const int m = 60;
            for (var j = 0; j <= m; j++)
                for (var i = 0; i <= m; i++)
                {
                    var x = -1f + (n + 2f) * i / m;
                    sp.Add(new Vector3(x, 0.05f + slope * x, -1f + (n + 2f) * j / m));
                }
            var st = new List<int>();
            for (var j = 0; j < m; j++)
                for (var i = 0; i < m; i++)
                {
                    var a = j * (m + 1) + i;
                    st.AddRange(new[] { a, a + m + 1, a + m + 2, a, a + m + 2, a + 1 });
                }
            var normals = new Vector3[positions.Length];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            const int res = 256;
            var stats = new TextureBake.Stats();
            var uvs = TextureBake.Unwrap(positions, faces, res, stats);
            var tangents = TextureBake.Tangents(positions, faces, uvs, normals);
            var map = TextureBake.BakeNormals(sp.ToArray(), st.ToArray(), positions, faces, uvs, normals, tangents,
                res, stats);
            var expected = new Vector3(-slope, 1f, 0f).normalized;
            var worst = 0f;
            for (var f = 0; f < faces.Count; f++)
            {
                var uv = (uvs[f][0] + uvs[f][1] + uvs[f][2] + uvs[f][3]) / 4f;
                var px = map[Mathf.FloorToInt(uv.y * res) * res + Mathf.FloorToInt(uv.x * res)];
                var ts = new Vector3(px.r / 255f * 2f - 1f, px.g / 255f * 2f - 1f, px.b / 255f * 2f - 1f);
                // As the shader rebuilds it: tangent, normal x tangent x w, normal.
                var t4 = (tangents[f][0] + tangents[f][1] + tangents[f][2] + tangents[f][3]) / 4f;
                var t = new Vector3(t4.x, t4.y, t4.z).normalized;
                var b = Vector3.Cross(Vector3.up, t) * (tangents[f][0].w < 0f ? -1f : 1f);
                var world = (t * ts.x + b * ts.y + Vector3.up * ts.z).normalized;
                worst = Mathf.Max(worst, Vector3.Angle(world, expected));
            }
            Assert.Less(worst, 3f, stats.ToString());
        }

        // A cube of n x n quads a side, welded, wound outwards.
        static List<int[]> Cube(int n, out Vector3[] positions)
        {
            var index = new Dictionary<Vector3Int, int>();
            var p = new List<Vector3>();
            var faces = new List<int[]>();
            int V(Vector3Int k)
            {
                if (!index.TryGetValue(k, out var i)) { i = p.Count; index[k] = i; p.Add((Vector3)k / n - Vector3.one * 0.5f); }
                return i;
            }
            for (var axis = 0; axis < 3; axis++)
                for (var side = 0; side < 2; side++)
                {
                    var u = (axis + 1) % 3;
                    var v = (axis + 2) % 3;
                    for (var a = 0; a < n; a++)
                        for (var b = 0; b < n; b++)
                        {
                            var q = new Vector3Int[4];
                            var corners = new[] { (a, b), (a + 1, b), (a + 1, b + 1), (a, b + 1) };
                            for (var k = 0; k < 4; k++)
                            {
                                var c = new int[3];
                                c[axis] = side * n; c[u] = corners[k].Item1; c[v] = corners[k].Item2;
                                q[k] = new Vector3Int(c[0], c[1], c[2]);
                            }
                            var face = new[] { V(q[0]), V(q[1]), V(q[2]), V(q[3]) };
                            if (side == 0) System.Array.Reverse(face);
                            faces.Add(face);
                        }
                }
            positions = p.ToArray();
            return faces;
        }

        static float MeanTilt(Color32[] map)
        {
            double sum = 0;
            var count = 0;
            foreach (var c in map)
            {
                var n = new Vector3(c.r / 127.5f - 1f, c.g / 127.5f - 1f, c.b / 127.5f - 1f);
                sum += Vector3.Angle(n, Vector3.forward);
                count++;
            }
            return (float)(sum / count);
        }

        [Test]
        public void ABoxBakesFlatOnceItsEdgesAreHard()
        {
            // Smooth across its corners a vertex normal leans 45 degrees off both faces, and the map
            // bends every panel back over its width (the fuse box's door, which read as a lighter
            // patch in URP); split where the charts are cut, the panels bake flat.
            var faces = Cube(4, out var positions);
            var source = Cube(16, out var sp);
            var st = new List<int>();
            foreach (var f in source) st.AddRange(new[] { f[0], f[1], f[2], f[0], f[2], f[3] });
            float Bake(float angle)
            {
                var hardWas = TextureBake.hardEdgeAngle;
                var creaseWas = TextureBake.chartCreaseAngle;
                TextureBake.hardEdgeAngle = TextureBake.chartCreaseAngle = angle;
                try
                {
                    var ex = new QuadExtractionResult { positions = positions, faces = faces, quadCount = faces.Count };
                    ex.shadingNormals = Extraction.NormalRecalculator.Compute(faces, positions, null);
                    var result = new RemeshResult { extraction = ex };
                    RemeshPipeline.BakeNormalMap(result, sp, st.ToArray(), 256);
                    return MeanTilt(result.bakedNormalMap);
                }
                finally
                {
                    TextureBake.hardEdgeAngle = hardWas;
                    TextureBake.chartCreaseAngle = creaseWas;
                }
            }
            var smooth = Bake(0f);
            var hard = Bake(40f);
            Assert.Greater(smooth, 10f);
            Assert.Less(hard, 2f, $"smooth {smooth:0.0}");
        }

        [Test]
        public void TheSourcesOwnNormalMapIsBakedIn()
        {
            // The same flat grid as source and result; the source's map tilts every point 17 degrees
            // towards its +u, which runs along +x: baked, the result's map decodes to that tilt.
            const int n = 6;
            var faces = Grid(n, out var positions);
            var st = new List<int>();
            foreach (var f in faces) st.AddRange(new[] { f[0], f[1], f[2], f[0], f[2], f[3] });
            var suv = new Vector2[positions.Length];
            for (var i = 0; i < suv.Length; i++) suv[i] = new Vector2(positions[i].x / n, positions[i].z / n);
            var tilt = new Vector3(0.3f, 0f, 1f).normalized;
            var px = new Color32[16 * 16];
            for (var i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)((tilt.x * 0.5f + 0.5f) * 255f), 128, (byte)((tilt.z * 0.5f + 0.5f) * 255f), 255);
            var maps = new[] { new TextureBake.Image(px, 16, 16) };
            var triangleMap = new int[st.Count / 3];
            var normals = new Vector3[positions.Length];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            const int res = 256;
            var stats = new TextureBake.Stats();
            var uvs = TextureBake.Unwrap(positions, faces, res, stats);
            var tangents = TextureBake.Tangents(positions, faces, uvs, normals);
            // The source a little above, so every texel's line meets it.
            var sp = new Vector3[positions.Length];
            for (var i = 0; i < sp.Length; i++) sp[i] = positions[i] + Vector3.up * 0.05f;
            var map = TextureBake.BakeNormals(sp, st.ToArray(), positions, faces, uvs, normals, tangents, res, stats,
                null, null, suv, triangleMap, maps);
            var expected = new Vector3(tilt.x, tilt.z, 0f).normalized;
            var worst = 0f;
            for (var f = 0; f < faces.Count; f++)
            {
                var uv = (uvs[f][0] + uvs[f][1] + uvs[f][2] + uvs[f][3]) / 4f;
                var c = map[Mathf.FloorToInt(uv.y * res) * res + Mathf.FloorToInt(uv.x * res)];
                var ts = new Vector3(c.r / 127.5f - 1f, c.g / 127.5f - 1f, c.b / 127.5f - 1f);
                var t4 = (tangents[f][0] + tangents[f][1] + tangents[f][2] + tangents[f][3]) / 4f;
                var t = new Vector3(t4.x, t4.y, t4.z).normalized;
                var b = Vector3.Cross(Vector3.up, t) * (tangents[f][0].w < 0f ? -1f : 1f);
                var world = (t * ts.x + b * ts.y + Vector3.up * ts.z).normalized;
                worst = Mathf.Max(worst, Vector3.Angle(world, expected));
            }
            Assert.Greater(stats.normalDetail, 0);
            Assert.Less(worst, 3f);
        }

        [Test]
        public void MeshCarriesTheBakedTangents()
        {
            var faces = Grid(3, out var positions);
            var normals = new Vector3[positions.Length];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            var uvs = TextureBake.Unwrap(positions, faces, 256, new TextureBake.Stats());
            var extraction = new QuadExtractionResult { positions = positions, faces = faces, normals = normals, quadCount = faces.Count };
            extraction.faceUvs = uvs;
            extraction.faceTangents = TextureBake.Tangents(positions, faces, uvs, normals);
            var mesh = QuadExtractor.ToMesh(extraction, "t");
            Assert.AreEqual(mesh.vertexCount, mesh.tangents.Length);
            foreach (var t in mesh.tangents)
            {
                Assert.AreEqual(0f, t.y, 1e-4f);
                Assert.AreEqual(1f, Mathf.Abs(t.w), 1e-4f);
            }
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
        public void BakeSeesARaisedBlockFromOutside()
        {
            // Source: ground (green) with a block on it (top red, walls blue) as a height field -- a
            // car on an aerial scan. Low poly: the flat 6 x 6 grid at a quarter of the block's height,
            // nearer the ground and the walls than the top. Looking at it from outside, the faces
            // over the block see its top; the nearest source point is a wall.
            const int n = 6;
            var faces = Grid(n, out var grid);
            var positions = new Vector3[grid.Length];
            for (var i = 0; i < grid.Length; i++) positions[i] = grid[i] + new Vector3(0f, 0.15f, 0f);
            const float lo = 2.2f, hi = 3.8f, top = 0.6f, step = 0.05f;
            const int m = 200;
            float Height(float x, float z) => x > lo && x < hi && z > lo && z < hi ? top : 0f;
            var sp = new List<Vector3>();
            for (var j = 0; j <= m; j++)
                for (var i = 0; i <= m; i++)
                {
                    float x = -1f + step * i, z = -1f + step * j;
                    sp.Add(new Vector3(x, Height(x, z), z));
                }
            var st = new List<int>();
            var texture = new List<int>();
            for (var j = 0; j < m; j++)
                for (var i = 0; i < m; i++)
                {
                    var a = j * (m + 1) + i;
                    foreach (var tri in new[] { new[] { a, a + m + 1, a + m + 2 }, new[] { a, a + m + 2, a + 1 } })
                    {
                        st.AddRange(tri);
                        int up = 0;
                        foreach (var v in tri) if (sp[v].y > 0.5f * top) up++;
                        texture.Add(up == 3 ? 0 : up == 0 ? 1 : 2);
                    }
                }
            var suv = new Vector2[sp.Count];
            TextureBake.Image Flat(Color32 c) => new TextureBake.Image(new[] { c, c, c, c }, 2, 2);
            var images = new[] { Flat(new Color32(255, 0, 0, 255)), Flat(new Color32(0, 255, 0, 255)), Flat(new Color32(0, 0, 255, 255)) };

            const int res = 256;
            var stats = new TextureBake.Stats();
            var uvs = TextureBake.Unwrap(positions, faces, res, stats);
            var atlas = TextureBake.Bake(sp.ToArray(), st.ToArray(), suv, texture.ToArray(), images, positions, faces, uvs, res, stats);

            Color32 At(int f)
            {
                var c = (uvs[f][0] + uvs[f][1] + uvs[f][2] + uvs[f][3]) / 4f;
                return atlas[Mathf.FloorToInt(c.y * res) * res + Mathf.FloorToInt(c.x * res)];
            }
            // Faces 2..3 both ways lie over the block; the corner faces over open ground.
            foreach (var (i, j) in new[] { (2, 2), (2, 3), (3, 2), (3, 3) })
            {
                var c = At(j * n + i);
                Assert.Greater(c.r, 200, $"face {i},{j} over the block reads {c}, not its top: {stats}");
            }
            foreach (var (i, j) in new[] { (0, 0), (5, 5), (0, 5), (5, 0) })
            {
                var c = At(j * n + i);
                Assert.Greater(c.g, 200, $"face {i},{j} over the ground reads {c}: {stats}");
            }
        }

        [Test]
        public void SmoothedBakeDirectionsKeepAThinPlatesTwoSidesApart()
        {
            // A plate 0.1 thick, 6 x 6: top red, bottom blue, rim green, baked onto itself. Smoothed
            // bake directions averaged across the rim would cancel and tip the lines sideways; each
            // side must still read its own colour, out to the faces at the rim.
            const int n = 6;
            const float h = 0.05f;
            var top = Grid(n, out var grid);
            var count = grid.Length;
            var positions = new Vector3[2 * count];
            for (var i = 0; i < count; i++)
            {
                positions[i] = grid[i] + new Vector3(0f, h, 0f);
                positions[count + i] = grid[i] - new Vector3(0f, h, 0f);
            }
            var faces = new List<int[]>();
            var kind = new List<int>();
            foreach (var f in top) { faces.Add(f); kind.Add(0); }
            foreach (var f in top) { faces.Add(new[] { f[3] + count, f[2] + count, f[1] + count, f[0] + count }); kind.Add(1); }
            var uses = new Dictionary<(int, int), int>();
            foreach (var f in top)
                for (var k = 0; k < 4; k++)
                {
                    var e = (Mathf.Min(f[k], f[(k + 1) % 4]), Mathf.Max(f[k], f[(k + 1) % 4]));
                    uses[e] = uses.TryGetValue(e, out var u) ? u + 1 : 1;
                }
            foreach (var f in top)
                for (var k = 0; k < 4; k++)
                {
                    int a = f[k], b = f[(k + 1) % 4];
                    if (uses[(Mathf.Min(a, b), Mathf.Max(a, b))] != 1) continue;
                    faces.Add(new[] { b, a, a + count, b + count });
                    kind.Add(2);
                }
            var st = new List<int>();
            var texture = new List<int>();
            for (var f = 0; f < faces.Count; f++)
            {
                var q = faces[f];
                st.AddRange(new[] { q[0], q[1], q[2], q[0], q[2], q[3] });
                texture.Add(kind[f]);
                texture.Add(kind[f]);
            }
            TextureBake.Image Flat(Color32 c) => new TextureBake.Image(new[] { c, c, c, c }, 2, 2);
            var images = new[] { Flat(new Color32(255, 0, 0, 255)), Flat(new Color32(0, 0, 255, 255)), Flat(new Color32(0, 255, 0, 255)) };

            const int res = 256;
            var stats = new TextureBake.Stats();
            var uvs = TextureBake.Unwrap(positions, faces, res, stats);
            var atlas = TextureBake.Bake(positions, st.ToArray(), new Vector2[positions.Length], texture.ToArray(),
                images, positions, faces, uvs, res, stats);
            Assert.Greater(TextureBake.colourDirectionSweeps, 0, "the default bake does not smooth its directions, so this shows nothing");
            for (var f = 0; f < 2 * top.Count; f++)
            {
                var c = (uvs[f][0] + uvs[f][1] + uvs[f][2] + uvs[f][3]) / 4f;
                var px = atlas[Mathf.FloorToInt(c.y * res) * res + Mathf.FloorToInt(c.x * res)];
                if (kind[f] == 0) Assert.Greater(px.r, 200, $"top face {f} reads {px}: {stats}");
                else Assert.Greater(px.b, 200, $"bottom face {f} reads {px}: {stats}");
            }
        }

        [Test]
        public void AFurtherMapIsBakedOverTheSameLayoutAsTheTexture()
        {
            // The texture red by u, its smoothness map green by u: laid out alike, they must bake
            // alike, texel for texel -- else the result's material reads its smoothness from
            // somewhere else on the model (the fuse box's patches).
            SeamedSphere(32, 64, out var positions, out var triangles, out var uvs);
            var main = new Color32[64 * 64];
            var gloss = new Color32[64 * 64];
            for (var y = 0; y < 64; y++)
                for (var x = 0; x < 64; x++)
                {
                    var v = (byte)(x * 4);
                    main[y * 64 + x] = new Color32(v, 0, 0, 255);
                    gloss[y * 64 + x] = new Color32(0, v, 0, 255);
                }
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = 400;
            var result = RemeshPipeline.Solve(positions, triangles, settings,
                RemeshProfile.For(RemeshProfileKind.GameAsset), null, uvs);
            var layer = new TextureBake.Layer { property = "_MetallicGlossMap", images = new[] { new TextureBake.Image(gloss, 64, 64) } };
            RemeshPipeline.BakeTexture(result, positions, triangles, uvs, null,
                new[] { new TextureBake.Image(main, 64, 64) }, 512, null, new[] { layer });
            Assert.AreSame(layer, result.bakedLayers?[0]);
            Assert.AreEqual(512 * 512, layer.baked.Length);
            var worst = 0;
            for (var i = 0; i < layer.baked.Length; i++) worst = Mathf.Max(worst, Mathf.Abs(layer.baked[i].g - result.bakedTexture[i].r));
            Assert.LessOrEqual(worst, 1, "the map and the texture differ at some texel");

            // A triangle whose material has no such map reads the layer's fallback.
            var none = new int[triangles.Length / 3];
            for (var t = 0; t < none.Length; t++) none[t] = -1;
            var empty = new TextureBake.Layer { images = new[] { new TextureBake.Image(gloss, 64, 64) }, triangleImage = none,
                fallback = new Color32(10, 20, 30, 255) };
            RemeshPipeline.BakeTexture(result, positions, triangles, uvs, null,
                new[] { new TextureBake.Image(main, 64, 64) }, 256, null, new[] { empty });
            foreach (var c in empty.baked) Assert.AreEqual(20, c.g);
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
