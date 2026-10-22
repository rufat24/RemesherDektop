// A model in parts (a car, a helmet with its visor): each part is remeshed on its own, and all of
// them share one atlas -- one texture, one normal map, the same texel density on every part, the way
// a game asset made of parts is textured. The parts' results are joined in the model's space, that
// is unwrapped and baked once, and each part takes back its own faces' UVs and tangents.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public static class PartBake
    {
        /// <summary>One part: its remesh result and its source, both in the part's own space.</summary>
        public sealed class Part
        {
            public RemeshResult result;

            /// <summary>The part's space to the model's (the root's): its transform relative to the root.</summary>
            public Matrix4x4 toModel = Matrix4x4.identity;

            public Vector3[] positions;
            public int[] triangles;

            /// <summary>Per source vertex; null when the part has none (it bakes no colour).</summary>
            public Vector2[] uvs;

            /// <summary>Per source triangle, its texture in the shared list; null with uvs: all the first.</summary>
            public int[] triangleTexture;
        }

        /// <summary>
        /// The quad budget split over parts by their surface area in the model's space, each at
        /// least <paramref name="minimum"/>: the same quad size everywhere, as one mesh would get.
        /// </summary>
        public static int[] SplitTarget(IList<double> areas, int target, int minimum = 16, IList<int> floors = null)
        {
            var counts = new int[areas.Count];
            double total = 0;
            foreach (var a in areas) total += Math.Max(0, a);
            for (var i = 0; i < counts.Length; i++)
            {
                var floor = floors != null && i < floors.Count ? Math.Max(minimum, floors[i]) : minimum;
                counts[i] = Math.Max(floor, total > 0 ? (int)Math.Round(target * Math.Max(0, areas[i]) / total) : target / Math.Max(1, counts.Length));
            }
            return counts;
        }

        /// <summary>
        /// How much of each part can be seen from outside the model: for points spread over the
        /// part, the share of the directions it faces from which nothing opaque is in the way,
        /// averaged. 1 for a part on the outside, about 0 for one shut inside another.
        /// </summary>
        /// <param name="occludes">Per part, whether it blocks the view; false for glass.</param>
        /// <remarks>
        /// For the budget (<see cref="VisibilityWeight"/>): split by area, the Porsche spent 61 % of
        /// its quads on the cabin, the wheels and the underbody, and its 19 exterior panels got 34 %
        /// -- the body 320 quads, too few for its C-pillars and wheel arches, which came out
        /// crumpled at that count and whole at four times it.
        /// </remarks>
        public static float[] Visibility(IList<Part> parts, IList<bool> occludes, int samplesPerPart = 400,
            int directions = 64, Action<float> onProgress = null)
        {
            var count = parts.Count;
            var visibility = new float[count];
            // The opaque parts, joined in the model's space.
            var vertices = new List<Vector3>();
            var tris = new List<int>();
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            for (var p = 0; p < count; p++)
            {
                var part = parts[p];
                foreach (var q in part.positions)
                {
                    var w = part.toModel.MultiplyPoint3x4(q);
                    min = Vector3.Min(min, w);
                    max = Vector3.Max(max, w);
                }
                if (occludes != null && p < occludes.Count && !occludes[p]) continue;
                var offset = vertices.Count;
                foreach (var q in part.positions) vertices.Add(part.toModel.MultiplyPoint3x4(q));
                foreach (var i in part.triangles) tris.Add(i + offset);
            }
            if (tris.Count == 0)
            {
                for (var p = 0; p < count; p++) visibility[p] = 1f;
                return visibility;
            }
            var grid = new SpatialGrid(vertices.ToArray(), tris.ToArray());
            var diagonal = (max - min).magnitude;
            var eps = 1e-4f * diagonal;

            // Spread evenly over the sphere (Fibonacci).
            var dirs = new Vector3[directions];
            var golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (var k = 0; k < directions; k++)
            {
                var y = 1f - 2f * (k + 0.5f) / directions;
                var r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                dirs[k] = new Vector3(Mathf.Cos(golden * k) * r, y, Mathf.Sin(golden * k) * r);
            }

            for (var p = 0; p < count; p++)
            {
                var part = parts[p];
                var normals = new List<Vector3>();
                var samples = SpatialGrid.SampleSurface(part.positions, part.triangles, samplesPerPart, 7 + p, normals);
                if (samples.Count == 0) { visibility[p] = 1f; continue; }
                // Normals go by the inverse transpose (Matrix4x4.transpose is an engine call).
                var inverse = InverseAffine(part.toModel);
                var normalMatrix = Matrix4x4.identity;
                for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                    normalMatrix[r, c] = inverse[c, r];
                var seen = new float[samples.Count];
                System.Threading.Tasks.Parallel.For(0, samples.Count, BackgroundScheduler.Options, s =>
                {
                    var at = part.toModel.MultiplyPoint3x4(samples[s]);
                    var n = s < normals.Count ? normalMatrix.MultiplyVector(normals[s]).normalized : Vector3.zero;
                    // Either side of a sheet: a panel modelled single-sided is seen from both.
                    var bestSide = 0f;
                    for (var side = -1; side <= 1; side += 2)
                    {
                        var facing = n * side;
                        int open = 0, total = 0;
                        foreach (var d in dirs)
                        {
                            if (n != Vector3.zero && Vector3.Dot(d, facing) <= 0.05f) continue;
                            total++;
                            if (!grid.FirstHit(at + (facing + d) * eps, d, 2f * diagonal, 0f, out _, out _)) open++;
                        }
                        if (total > 0) bestSide = Mathf.Max(bestSide, open / (float)total);
                        if (n == Vector3.zero) break;
                    }
                    seen[s] = bestSide;
                });
                double sum = 0;
                foreach (var v in seen) sum += v;
                visibility[p] = (float)(sum / seen.Length);
                onProgress?.Invoke((p + 1f) / count);
            }
            return visibility;
        }

        /// <summary>
        /// A part's weight in the budget split for its visibility: <paramref name="hidden"/> for a
        /// part nobody can see, rising to 1 for one fully on the outside.
        /// </summary>
        /// <remarks>
        /// Steep at the bottom (visibilityCurve): a part seen only through the windows (the
        /// Porsche's seats 6 %, its dash 2-5 %) is next to hidden, one half in view (a wheel under
        /// its arch, 26-29 %) is not. Linear, the cabin's parts kept ~1,500 of 8,000 quads.
        /// </remarks>
        public static float VisibilityWeight(float visibility, float hidden = -1f)
        {
            if (hidden < 0f) hidden = hiddenWeight;
            var v = Mathf.Clamp01(visibility);
            if (visibilityCurve) v = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(visibilityHiddenBelow, visibilitySeenAbove, v));
            return hidden + (1f - hidden) * v;
        }

        /// <summary>Weight of a part nobody can see.</summary>
        public static float hiddenWeight = 0.05f;

        /// <summary>Visibility under visibilityHiddenBelow weighs as hidden, over visibilitySeenAbove as fully seen. Off: linear (hidden 0.15 before).</summary>
        public static bool visibilityCurve = true;
        public static float visibilityHiddenBelow = 0.08f;
        public static float visibilitySeenAbove = 0.6f;

        /// <summary>
        /// The inverse of an affine transform (no projection), in managed code: Matrix4x4.inverse is
        /// an engine call, and this runs outside the editor too.
        /// </summary>
        public static Matrix4x4 InverseAffine(Matrix4x4 m)
        {
            float a = m.m00, b = m.m01, c = m.m02, d = m.m10, e = m.m11, f = m.m12, g = m.m20, h = m.m21, k = m.m22;
            var det = a * (e * k - f * h) - b * (d * k - f * g) + c * (d * h - e * g);
            if (Mathf.Abs(det) < 1e-30f) return Matrix4x4.identity;
            var inv = 1f / det;
            var r = Matrix4x4.identity;
            r.m00 = (e * k - f * h) * inv; r.m01 = (c * h - b * k) * inv; r.m02 = (b * f - c * e) * inv;
            r.m10 = (f * g - d * k) * inv; r.m11 = (a * k - c * g) * inv; r.m12 = (c * d - a * f) * inv;
            r.m20 = (d * h - e * g) * inv; r.m21 = (b * g - a * h) * inv; r.m22 = (a * e - b * d) * inv;
            float tx = m.m03, ty = m.m13, tz = m.m23;
            r.m03 = -(r.m00 * tx + r.m01 * ty + r.m02 * tz);
            r.m13 = -(r.m10 * tx + r.m11 * ty + r.m12 * tz);
            r.m23 = -(r.m20 * tx + r.m21 * ty + r.m22 * tz);
            return r;
        }

        /// <summary>Quads asked for each separate piece of a part at the least (a piece needs some to close).</summary>
        public static int quadsPerPiece = 20;

        /// <summary>
        /// The least a part may be given: <see cref="quadsPerPiece"/> for each of its connected pieces
        /// (welded by position, so UV seams do not split them), at most half its triangles. A part of
        /// ten studs given 16 quads came out empty (2026-10-19, the Sketchfab helmet).
        /// </summary>
        public static int Floor(Vector3[] positions, int[] triangles, int minimum = 16)
        {
            var weld = new Dictionary<Vector3, int>(positions.Length);
            var of = new int[positions.Length];
            for (var i = 0; i < positions.Length; i++)
            {
                if (!weld.TryGetValue(positions[i], out var w)) { w = weld.Count; weld[positions[i]] = w; }
                of[i] = w;
            }
            var parent = new int[weld.Count];
            for (var i = 0; i < parent.Length; i++) parent[i] = i;
            int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                var a = Root(of[triangles[t]]);
                parent[Root(of[triangles[t + 1]])] = a;
                parent[Root(of[triangles[t + 2]])] = a;
            }
            var pieces = 0;
            for (var i = 0; i < parent.Length; i++) if (Root(i) == i) pieces++;
            return Math.Max(minimum, Math.Min(quadsPerPiece * pieces, triangles.Length / 6));
        }

        /// <summary>Surface area of triangles under a transform.</summary>
        public static double Area(Vector3[] positions, int[] triangles, Matrix4x4 toModel)
        {
            double area = 0;
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                var a = toModel.MultiplyPoint3x4(positions[triangles[t]]);
                var b = toModel.MultiplyPoint3x4(positions[triangles[t + 1]]);
                var c = toModel.MultiplyPoint3x4(positions[triangles[t + 2]]);
                area += 0.5 * Vector3.Cross(b - a, c - a).magnitude;
            }
            return area;
        }

        /// <summary>
        /// Bakes the parts into one atlas. Returns a result carrying the shared textures and the bake
        /// stats (its extraction is the joined one); each part's extraction gets its faces' UVs, and
        /// with a normal map, tangents, in its own space. Null when there is nothing to bake.
        /// </summary>
        public static RemeshResult Bake(IList<Part> parts, TextureBake.Image[] images, bool bakeTexture,
            bool bakeNormals, int resolution, Action<string, float> onProgress = null)
        {
            if (parts == null || parts.Count == 0 || !bakeTexture && !bakeNormals) return null;

            // --- the results joined, in the model's space ---
            var joined = new QuadExtractionResult();
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var firstFace = new int[parts.Count + 1];
            for (var p = 0; p < parts.Count; p++)
            {
                firstFace[p] = joined.faces.Count;
                var ex = parts[p].result?.extraction;
                if (ex == null || ex.positions == null) continue;
                var m = parts[p].toModel;
                // Normals by the inverse transpose (right under any scale); transposed by hand.
                var inv = InverseAffine(m);
                Vector3 Normal(Vector3 v) => new Vector3(inv.m00 * v.x + inv.m10 * v.y + inv.m20 * v.z,
                    inv.m01 * v.x + inv.m11 * v.y + inv.m21 * v.z, inv.m02 * v.x + inv.m12 * v.y + inv.m22 * v.z);
                var offset = positions.Count;
                var n = ex.shadingNormals != null && ex.shadingNormals.Length == ex.positions.Length ? ex.shadingNormals : ex.normals;
                for (var i = 0; i < ex.positions.Length; i++)
                {
                    positions.Add(m.MultiplyPoint3x4(ex.positions[i]));
                    normals.Add(n != null && i < n.Length ? MeshMath.SafeNormalize(Normal(n[i]), Vector3.up) : Vector3.up);
                }
                foreach (var f in ex.faces)
                {
                    if (f == null) { joined.faces.Add(null); continue; }
                    var g = new int[f.Length];
                    for (var k = 0; k < f.Length; k++) g[k] = f[k] + offset;
                    joined.faces.Add(g);
                }
                joined.quadCount += ex.quadCount;
            }
            firstFace[parts.Count] = joined.faces.Count;
            joined.positions = positions.ToArray();
            joined.normals = normals.ToArray();
            if (joined.faces.Count == 0) return null;

            // --- the sources joined ---
            var sp = new List<Vector3>();
            var st = new List<int>();
            var suv = new List<Vector2>();
            var stex = new List<int>();
            var anyUvs = false;
            foreach (var part in parts)
            {
                if (part.positions == null || part.triangles == null) continue;
                var offset = sp.Count;
                foreach (var q in part.positions) sp.Add(part.toModel.MultiplyPoint3x4(q));
                var hasUvs = part.uvs != null && part.uvs.Length == part.positions.Length;
                anyUvs |= hasUvs;
                for (var i = 0; i < part.positions.Length; i++) suv.Add(hasUvs ? part.uvs[i] : Vector2.zero);
                for (var t = 0; t + 2 < part.triangles.Length; t += 3)
                {
                    st.Add(part.triangles[t] + offset);
                    st.Add(part.triangles[t + 1] + offset);
                    st.Add(part.triangles[t + 2] + offset);
                    stex.Add(!hasUvs ? -1 : part.triangleTexture != null && t / 3 < part.triangleTexture.Length ? part.triangleTexture[t / 3] : 0);
                }
            }
            var sourcePositions = sp.ToArray();
            var sourceTriangles = st.ToArray();

            var shared = new RemeshResult { extraction = joined };
            if (bakeTexture && anyUvs && images != null && images.Length > 0)
                RemeshPipeline.BakeTexture(shared, sourcePositions, sourceTriangles, suv.ToArray(), stex.ToArray(),
                    images, resolution, onProgress);
            if (bakeNormals)
                RemeshPipeline.BakeNormalMap(shared, sourcePositions, sourceTriangles, resolution, onProgress);
            if (joined.faceUvs == null) return null;

            // --- each part's faces back, tangents into its own space ---
            for (var p = 0; p < parts.Count; p++)
            {
                var ex = parts[p].result?.extraction;
                if (ex == null || ex.positions == null) continue;
                var count = firstFace[p + 1] - firstFace[p];
                ex.faceUvs = new Vector2[count][];
                Array.Copy(joined.faceUvs, firstFace[p], ex.faceUvs, 0, count);
                ex.uvs = null;
                if (joined.faceTangents != null)
                {
                    var back = InverseAffine(parts[p].toModel);
                    ex.faceTangents = new Vector4[count][];
                    for (var f = 0; f < count; f++)
                    {
                        var tg = joined.faceTangents[firstFace[p] + f];
                        if (tg == null) continue;
                        var local = new Vector4[tg.Length];
                        for (var k = 0; k < tg.Length; k++)
                        {
                            var d = MeshMath.SafeNormalize(back.MultiplyVector(new Vector3(tg[k].x, tg[k].y, tg[k].z)), Vector3.right);
                            local[k] = new Vector4(d.x, d.y, d.z, tg[k].w);
                        }
                        ex.faceTangents[f] = local;
                    }
                }
            }
            return shared;
        }
    }
}
