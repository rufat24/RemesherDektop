// UV transfer across a seam. A latitude/longitude sphere carries its u = 0 / u = 1 seam as a column
// of duplicated vertices, which is exactly how a Unity mesh stores any UV seam; an output quad that
// straddles it must take all four corners from one side, or it samples the whole atlas in between.

using System.Collections.Generic;
using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class UvTransferTests
    {
        /// <summary>Lat/long sphere with a seam column at u = 0 / 1, the way Unity stores one.</summary>
        static void SeamedSphere(int lat, int lon, out Vector3[] positions, out int[] triangles,
            out Vector2[] uvs)
        {
            var p = new List<Vector3>();
            var uv = new List<Vector2>();
            for (var i = 0; i <= lat; i++)
            {
                var theta = Mathf.PI * i / lat;
                for (var j = 0; j <= lon; j++)
                {
                    // j == lon repeats j == 0's position exactly: the seam copy.
                    var phi = 2f * Mathf.PI * (j % lon) / lon;
                    p.Add(new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta),
                        Mathf.Sin(theta) * Mathf.Sin(phi)));
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

        /// <summary>
        /// A welded quad band around the equator, rotated half a cell so faces straddle the seam.
        /// </summary>
        static void EquatorBand(int segments, out Vector3[] positions, out List<int[]> faces)
        {
            var p = new List<Vector3>();
            for (var row = 0; row < 2; row++)
            {
                var theta = Mathf.PI * (0.45f + 0.1f * row);
                for (var j = 0; j < segments; j++)
                {
                    var phi = 2f * Mathf.PI * (j + 0.5f) / segments;
                    p.Add(new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta),
                        Mathf.Sin(theta) * Mathf.Sin(phi)));
                }
            }

            faces = new List<int[]>();
            for (var j = 0; j < segments; j++)
            {
                var n = (j + 1) % segments;
                faces.Add(new[] { j, n, segments + n, segments + j });
            }
            positions = p.ToArray();
        }

        static float USpan(IList<Vector2> uv)
        {
            var min = float.MaxValue;
            var max = float.MinValue;
            foreach (var x in uv) { min = Mathf.Min(min, x.x); max = Mathf.Max(max, x.x); }
            return max - min;
        }

        [Test]
        public void ChartsSplitAtTheSeamOnly()
        {
            SeamedSphere(16, 32, out var positions, out var triangles, out var uvs);
            AttributeTransfer.Charts(positions, triangles, uvs, out var count);
            Assert.AreEqual(1, count, "a sphere with one seam is one chart: the seam is an edge of it, " +
                                      "not a cut through it");
        }

        [Test]
        public void FaceUvsNeverSpanTheSeam()
        {
            SeamedSphere(24, 48, out var positions, out var triangles, out var uvs);
            EquatorBand(12, out var targets, out var faces);

            // The per-vertex transfer is the defect being fixed: the face across the seam gets
            // u near 0 on one side and near 1 on the other.
            var perVertex = AttributeTransfer.SampleUvs(positions, triangles, uvs, targets);
            var worstPerVertex = 0f;
            foreach (var face in faces)
            {
                var corner = new Vector2[face.Length];
                for (var k = 0; k < face.Length; k++) corner[k] = perVertex[face[k]];
                worstPerVertex = Mathf.Max(worstPerVertex, USpan(corner));
            }
            Assert.Greater(worstPerVertex, 0.5f, "fixture no longer straddles the seam");

            var faceUvs = AttributeTransfer.SampleFaceUvs(positions, triangles, uvs, targets, faces,
                out var stats);
            Assert.NotNull(faceUvs);
            Assert.Greater(stats.seamFaces, 0, stats.ToString());

            // A face is 1/12 of the way round, so 1/12 of the atlas is its honest span.
            for (var f = 0; f < faces.Count; f++)
                Assert.Less(USpan(faceUvs[f]), 0.15f, $"face {f} spans the atlas; {stats}");
        }

        [Test]
        public void MeshIsSplitAtSeamsAndCarriesTheCornerUvs()
        {
            SeamedSphere(24, 48, out var positions, out var triangles, out var uvs);
            EquatorBand(12, out var targets, out var faces);

            var extraction = new QuadExtractionResult
            {
                positions = targets,
                faces = faces,
                quadCount = faces.Count,
                faceUvs = AttributeTransfer.SampleFaceUvs(positions, triangles, uvs, targets, faces,
                    out _)
            };

            var mesh = QuadExtractor.ToMesh(extraction, "seam");
            try
            {
                Assert.Greater(mesh.vertexCount, targets.Length, "the seam vertices were not split");
                var meshUvs = mesh.uv;
                var quads = mesh.GetIndices(0);
                Assert.AreEqual(faces.Count * 4, quads.Length);
                for (var f = 0; f < faces.Count; f++)
                {
                    var corner = new Vector2[4];
                    for (var k = 0; k < 4; k++)
                    {
                        corner[k] = meshUvs[quads[4 * f + k]];
                        Assert.AreEqual(extraction.faceUvs[f][k], corner[k]);
                        Assert.AreEqual(targets[faces[f][k]], mesh.vertices[quads[4 * f + k]]);
                    }
                    Assert.Less(USpan(corner), 0.15f, $"face {f} spans the atlas in the mesh");
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void PipelineOutputHasNoFaceAcrossTheSeam(
            [Values(RemeshExtractor.Collapse, RemeshExtractor.IntegerGrid)] RemeshExtractor extractor)
        {
            SeamedSphere(32, 64, out var positions, out var triangles, out var uvs);
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = 400;
            var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
            profile.extractor = extractor;

            var result = RemeshPipeline.Solve(positions, triangles, settings, profile, null, uvs);
            Assert.NotNull(result.extraction.faceUvs, "no per-corner UVs");
            Assert.NotNull(result.faceUvStats);

            // Near the poles a face legitimately spans much of u (the whole row converges on one
            // point), so check only faces away from them.
            var worst = 0f;
            for (var f = 0; f < result.extraction.faces.Count; f++)
            {
                var face = result.extraction.faces[f];
                var uv = result.extraction.faceUvs[f];
                if (face == null || uv == null) continue;
                var polar = false;
                foreach (var v in face)
                    if (Mathf.Abs(result.extraction.positions[v].y) > 0.8f) polar = true;
                if (polar) continue;
                worst = Mathf.Max(worst, USpan(uv));
            }
            Assert.Less(worst, 0.25f, $"a face spans the atlas; {result.faceUvStats}");
        }
    }
}
