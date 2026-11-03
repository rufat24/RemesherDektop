using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    /// <summary>
    /// Levels of detail from one mesh: each level is the base mesh reduced by its quad strips while
    /// strips are left (the loops stay, only fewer of them), by edge collapse below that, and given
    /// the base's UVs and materials, so every level draws with the base's material and textures.
    /// Each level's distance from the reference surface is measured, and its switch height is where
    /// that distance shrinks below a pixel budget on screen.
    /// </summary>
    public static class LodChain
    {
        public sealed class Settings
        {
            /// <summary>Levels after the base (LOD1..LODn).</summary>
            public int levels = 3;
            /// <summary>Each level keeps this share of the previous one's triangles.</summary>
            public float keep = 0.5f;
            /// <summary>A level takes over once its error covers at most this many pixels.</summary>
            public float pixelError = 1f;
            /// <summary>The screen height, in pixels, the pixel error is measured against.</summary>
            public int screenHeight = 1080;
            /// <summary>Below this share of the screen's height the object is not drawn; 0 never culls.</summary>
            public float cullHeight = 0.01f;
            /// <summary>Remove quad strips while they last; off goes straight to edge collapse.</summary>
            public bool quadStrips = true;
            /// <summary>
            /// Levels take the base's UVs, with the base's chart borders held, so they draw with its
            /// textures. Off: levels are reduced freely and come without UVs, for the caller to give
            /// each its own atlas and bake (an atlas of many small charts, as the remesher's bake
            /// makes, held the body's LOD3 at 3,588 triangles of a 2,594 target).
            /// </summary>
            public bool shareUvs = true;
            /// <summary>
            /// With shared UVs, whether a collapse keeps every face inside one chart. Off, it
            /// reduces freely and the UVs are still sampled from the base, smeared on the few faces
            /// that cross a seam: for a level small on screen whose base has too many charts to
            /// reduce within them.
            /// </summary>
            public bool holdSeams = true;
            /// <summary>A level must have at most this share of the previous one's triangles, or the chain ends.</summary>
            public float minStep = 0.9f;
            /// <summary>Fewest triangles a level may have.</summary>
            public int minFaces = 12;
        }

        /// <summary>The base mesh: welded positions, faces (quads, triangles, n-gons), and per face corner its UVs.</summary>
        public sealed class Input
        {
            public Vector3[] positions;
            public List<int[]> faces;
            /// <summary>Per face, one UV per corner; null when the base has none.</summary>
            public Vector2[][] faceUvs;
            /// <summary>Per face, its material slot; null with one material.</summary>
            public int[] faceMaterials;
            public int materialSlots;
            /// <summary>The source surface, for the base level's own error; null when the base is the source.</summary>
            public Vector3[] referencePositions;
            public int[] referenceTriangles;
        }

        public sealed class Level
        {
            public Vector3[] positions;
            public List<int[]> faces;
            public Vector2[][] faceUvs;
            public Vector4[][] faceTangents;
            public Vector3[][] faceNormals;
            public Vector3[] normals;
            public int[] faceMaterials;
            /// <summary>Largest distance between this level and the base (either way), in the input's units; for the base, from the source.</summary>
            public float error;
            /// <summary>The share of the screen's height below which the next level takes over.</summary>
            public float switchHeight;
            /// <summary>"base", "strips" or "collapse".</summary>
            public string method;
            /// <summary>Why quad strips were not used, when they were not.</summary>
            public string note;
            public int quads, triangles;
            /// <summary>Triangles drawn: a quad is two.</summary>
            public int Cost => quads * 2 + triangles;
            /// <summary>Faces whose UVs had to be pulled to one side of a seam.</summary>
            public int seamFaces;
            /// <summary>Quads turned to their nearer diagonal (RemeshPipeline.nearerDiagonals).</summary>
            public int turnedQuads;
            public long ms;
            public override string ToString() =>
                $"{method}{(note != null ? $" ({note})" : "")} faces={faces.Count} quads={quads} tris={triangles} seamFaces={seamFaces} turned={turnedQuads} error={error:G3} switch={switchHeight:P1} ms={ms}";
        }

        /// <summary>
        /// The base as level 0 and <see cref="Settings.levels"/> reductions of it, each with its
        /// switch height. The levels are reduced from the base, not from each other, so one level's
        /// error does not carry into the next.
        /// </summary>
        public static List<Level> Build(Input input, Settings settings, Action<float> onProgress = null)
        {
            if (input == null || input.positions == null || input.faces == null) throw new ArgumentNullException(nameof(input));
            settings ??= new Settings();
            var result = new List<Level>();
            var baseLevel = new Level
            {
                positions = input.positions, faces = input.faces, faceUvs = input.faceUvs,
                faceMaterials = input.faceMaterials, method = "base",
            };
            Count(baseLevel);
            result.Add(baseLevel);

            var baseTriangles = Triangulate(input.faces, out var triangleFace);
            // Levels are measured against the base, which is what is on screen when the first
            // switch happens; the base itself against the source, when there is one.
            var baseGrid = new SpatialGrid(input.positions, baseTriangles);

            // The base's UVs as a Unity-style vertex list, split where a vertex carries two UVs:
            // what AttributeTransfer reads charts and seams off.
            SplitByUv(input, out var uvPositions, out var uvTriangles, out var uvs);

            MaterialLabels seamLabels = null;
            // Counted in triangles, what a level costs to draw: a quad is two.
            var baseCount = Math.Max(1, baseTriangles.Length / 3);
            var steps = Math.Max(1, settings.levels) * 2 + 1;
            var step = 0;
            void Progress() => onProgress?.Invoke(++step / (float)steps);

            if (input.referencePositions != null && input.referenceTriangles != null && input.referencePositions != input.positions)
                baseLevel.error = Error(input.positions, baseTriangles, baseGrid,
                    new SpatialGrid(input.referencePositions, input.referenceTriangles), input.referencePositions, input.referenceTriangles);
            Progress();
            for (var l = 1; l <= settings.levels; l++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var target = Math.Max(settings.minFaces, (int)Math.Round(baseCount * Math.Pow(settings.keep, l)));
                var previous = result[result.Count - 1];
                if (target >= previous.Cost) break;

                var level = new Level();
                QuadReduction.Stats stats = null;
                List<int[]> stripFaces = null;
                if (settings.quadStrips && QuadReduction.Reduce(input.positions, baseTriangles, Math.Max(1, target / 2),
                        out var stripPositions, out stripFaces, out stats)
                    && stripFaces.Count * 2 <= target * 1.25f + 8)
                {
                    level.positions = stripPositions;
                    level.faces = stripFaces;
                    level.method = "strips";
                    // Bent quads cut along the diagonal nearer the base, as every output's are.
                    if (RemeshPipeline.nearerDiagonals)
                        level.turnedQuads = PlanarQuads.NearerDiagonals(level.positions, level.faces, baseGrid);
                }
                else
                {
                    // Chart and material borders held as label borders, so no triangle spans a UV
                    // seam and every level can draw with the base's textures.
                    if (settings.shareUvs && settings.holdSeams) seamLabels ??= SeamLabels(input, baseTriangles, triangleFace, uvPositions, uvTriangles, uvs);
                    var options = new DecimateOptions { targetTriangles = Math.Max(4, target), labels = seamLabels };
                    // From the level before when that was collapsed too: each step halves a smaller
                    // mesh (a Porsche wheel's 550k triangles once, not three times).
                    var fromPositions = input.positions;
                    var fromTriangles = baseTriangles;
                    if (previous.method == "collapse")
                    {
                        fromPositions = previous.positions;
                        fromTriangles = Triangulate(previous.faces, out _);
                    }
                    PreDecimate.Decimate(fromPositions, fromTriangles, options, out _, out var dp, out var dt);
                    var faces = new List<int[]>(dt.Length / 3);
                    for (var t = 0; t + 2 < dt.Length; t += 3) faces.Add(new[] { dt[t], dt[t + 1], dt[t + 2] });
                    level.positions = dp;
                    level.faces = faces;
                    level.method = "collapse";
                    level.note = settings.quadStrips ? (stats?.reason ?? "") + (stripFaces != null ? $" {stripFaces.Count} faces" : "") : null;
                }
                Progress();

                Count(level);
                if (level.Cost > previous.Cost * settings.minStep) break;
                Attributes(level, input, baseGrid, triangleFace, settings.shareUvs ? uvPositions : null, uvTriangles, uvs);
                var levelTriangles = Triangulate(level.faces, out _);
                level.error = Error(level.positions, levelTriangles, null, baseGrid, input.positions, baseTriangles);
                Count(level);
                level.ms = watch.ElapsedMilliseconds;
                result.Add(level);
                Progress();
            }

            SwitchHeights(result, Size(input.positions), settings);
            return result;
        }

        /// <summary>
        /// The switch heights for levels whose errors are known, in a model of this size: level i is
        /// drawn until the object fills less of the screen's height than level i+1's error allows.
        /// </summary>
        public static void SwitchHeights(IList<Level> levels, float size, Settings settings)
        {
            settings ??= new Settings();
            var cull = Mathf.Clamp(settings.cullHeight, 0f, 0.5f);
            var floor = Mathf.Max(cull, 1e-4f);
            var ceiling = 1f;
            for (var i = 0; i < levels.Count; i++)
            {
                float height;
                if (i == levels.Count - 1) height = cull;
                else
                {
                    // An error of e in a model of size S covers e/S * h * H pixels at relative height h.
                    var e = Mathf.Max(levels[i + 1].error, 1e-6f * size);
                    height = settings.pixelError * size / (e * Mathf.Max(1, settings.screenHeight));
                }
                // Strictly decreasing, as a LODGroup needs, with room left for the levels below.
                var room = floor * Mathf.Pow(1.05f, levels.Count - 1 - i);
                height = Mathf.Clamp(height, room, ceiling * 0.98f);
                levels[i].switchHeight = height;
                ceiling = height;
            }
        }

        /// <summary>The largest side of the bounds: what a LODGroup measures heights against.</summary>
        public static float Size(Vector3[] positions)
        {
            if (positions == null || positions.Length == 0) return 1f;
            var b = new Bounds(positions[0], Vector3.zero);
            foreach (var p in positions) b.Encapsulate(p);
            return Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z), 1e-6f);
        }

        /// <summary>Every face fanned into triangles; per triangle the face it came from.</summary>
        public static int[] Triangulate(IList<int[]> faces, out int[] triangleFace)
        {
            var tris = new List<int>(faces.Count * 6);
            var owner = new List<int>(faces.Count * 2);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;
                for (var i = 1; i + 1 < face.Length; i++)
                {
                    tris.Add(face[0]); tris.Add(face[i]); tris.Add(face[i + 1]);
                    owner.Add(f);
                }
            }
            triangleFace = owner.ToArray();
            return tris.ToArray();
        }

        /// <summary>
        /// Largest distance either way between a surface and the reference, sampled: points on the
        /// surface to the reference and points on the reference to the surface, so both a bulge and
        /// a lost feature count. With <paramref name="quantile"/> under 1, the distance that share of
        /// the samples on each side stays within, the larger side's.
        /// </summary>
        public static float Error(Vector3[] positions, int[] triangles, SpatialGrid grid, SpatialGrid reference,
            Vector3[] referencePositions, int[] referenceTriangles, int samples = 20000, float quantile = 1f)
        {
            if (quantile < 1f)
            {
                if (triangles.Length < 3) return float.PositiveInfinity;
                grid ??= new SpatialGrid(positions, triangles);
                var mineSamples = SpatialGrid.SampleSurface(positions, triangles, samples, 7);
                var theirSamples = SpatialGrid.SampleSurface(referencePositions, referenceTriangles, samples, 11);
                var share = new float[2];
                Parallel.For(0, 2, BackgroundScheduler.Options, side =>
                {
                    var points = side == 0 ? mineSamples : theirSamples;
                    var against = side == 0 ? reference : grid;
                    var d = new float[points.Count];
                    for (var i = 0; i < d.Length; i++) d[i] = against.NearestDistance(points[i], out _);
                    Array.Sort(d);
                    share[side] = d.Length > 0 ? d[Mathf.Clamp(Mathf.CeilToInt(quantile * d.Length) - 1, 0, d.Length - 1)] : 0f;
                });
                return Mathf.Max(share[0], share[1]);
            }
            if (triangles.Length < 3) return float.PositiveInfinity;
            grid ??= new SpatialGrid(positions, triangles);
            var mine = SpatialGrid.SampleSurface(positions, triangles, samples, 7);
            for (var i = 0; i < positions.Length; i += Math.Max(1, positions.Length / samples)) mine.Add(positions[i]);
            var theirs = SpatialGrid.SampleSurface(referencePositions, referenceTriangles, samples, 11);
            var worst = new float[2];
            Parallel.For(0, 2, BackgroundScheduler.Options, side =>
            {
                var points = side == 0 ? mine : theirs;
                var against = side == 0 ? reference : grid;
                var w = 0f;
                foreach (var p in points) w = Mathf.Max(w, against.NearestDistance(p, out _));
                worst[side] = w;
            });
            return Mathf.Max(worst[0], worst[1]);
        }

        static void Count(Level level)
        {
            level.quads = level.triangles = 0;
            foreach (var f in level.faces)
                if (f.Length == 4) level.quads++;
                else level.triangles += Math.Max(0, f.Length - 2);
        }

        /// <summary>A vertex per (vertex, UV) pair of the base, and its triangles over them.</summary>
        static void SplitByUv(Input input, out Vector3[] positions, out int[] triangles, out Vector2[] uvs)
        {
            positions = null; triangles = null; uvs = null;
            if (input.faceUvs == null || input.faceUvs.Length != input.faces.Count) return;
            var index = new Dictionary<(int, Vector2), int>();
            var p = new List<Vector3>();
            var u = new List<Vector2>();
            var t = new List<int>();
            for (var f = 0; f < input.faces.Count; f++)
            {
                var face = input.faces[f];
                var uv = input.faceUvs[f];
                if (face == null || face.Length < 3 || uv == null || uv.Length != face.Length) continue;
                var corner = new int[face.Length];
                for (var k = 0; k < face.Length; k++)
                {
                    if (!index.TryGetValue((face[k], uv[k]), out var v))
                    {
                        v = p.Count;
                        index[(face[k], uv[k])] = v;
                        p.Add(input.positions[face[k]]);
                        u.Add(uv[k]);
                    }
                    corner[k] = v;
                }
                for (var k = 1; k + 1 < corner.Length; k++) { t.Add(corner[0]); t.Add(corner[k]); t.Add(corner[k + 1]); }
            }
            positions = p.ToArray(); triangles = t.ToArray(); uvs = u.ToArray();
        }

        /// <summary>Per base triangle its UV chart and material as one label; null with neither.</summary>
        static MaterialLabels SeamLabels(Input input, int[] baseTriangles, int[] triangleFace,
            Vector3[] uvPositions, int[] uvTriangles, Vector2[] uvs)
        {
            var slots = input.faceMaterials != null && input.materialSlots > 1 ? input.materialSlots : 1;
            int[] chart = null;
            if (uvPositions != null && uvTriangles.Length == baseTriangles.Length)
                chart = AttributeTransfer.Charts(uvPositions, uvTriangles, uvs, out _);
            if (chart == null && slots == 1) return null;
            var labels = new int[baseTriangles.Length / 3];
            for (var t = 0; t < labels.Length; t++)
                labels[t] = (chart != null ? chart[t] : 0) * slots + (slots > 1 ? input.faceMaterials[triangleFace[t]] : 0);
            return new MaterialLabels(input.positions, baseTriangles, labels);
        }

        /// <summary>A reduced level's normals, materials (from the base face nearest each face), UVs and tangents.</summary>
        static void Attributes(Level level, Input input, SpatialGrid baseGrid, int[] triangleFace,
            Vector3[] uvPositions, int[] uvTriangles, Vector2[] uvs)
        {
            level.normals = Extraction.NormalRecalculator.Compute(level.faces, level.positions, null);
            if (input.faceMaterials != null && input.materialSlots > 1)
            {
                level.faceMaterials = new int[level.faces.Count];
                Parallel.For(0, level.faces.Count, BackgroundScheduler.Options, f =>
                {
                    var face = level.faces[f];
                    var c = Vector3.zero;
                    foreach (var v in face) c += level.positions[v];
                    c /= face.Length;
                    baseGrid.NearestDistance(c, out var t);
                    level.faceMaterials[f] = t >= 0 ? input.faceMaterials[triangleFace[t]] : 0;
                });
            }
            if (uvPositions != null)
            {
                level.faceUvs = AttributeTransfer.SampleFaceUvs(uvPositions, uvTriangles, uvs, level.positions, level.faces, out var uvStats);
                level.seamFaces = uvStats?.seamFaces ?? 0;
                if (level.faceUvs != null)
                    level.faceTangents = TextureBake.Tangents(level.positions, level.faces, level.faceUvs, level.normals);
            }
        }

        /// <summary>A level as an extraction result, so QuadExtractor.ToMesh builds it like any result.</summary>
        public static QuadExtractionResult ToExtraction(Level level, int materialSlots)
        {
            var ex = new QuadExtractionResult
            {
                positions = level.positions,
                faces = level.faces,
                faceUvs = level.faceUvs,
                faceTangents = level.faceTangents,
                faceNormals = level.faceNormals,
                normals = level.normals,
                shadingNormals = level.normals ?? Extraction.NormalRecalculator.Compute(level.faces, level.positions, null),
                faceMaterials = level.faceMaterials,
                materialSlots = level.faceMaterials != null ? materialSlots : 0,
            };
            Count(level);
            ex.quadCount = level.quads;
            ex.triangleCount = 0;
            foreach (var f in level.faces) if (f.Length == 3) ex.triangleCount++;
            return ex;
        }

        /// <summary>The base level of a remesh result: its extraction's faces and UVs, against the source surface.</summary>
        public static Input FromExtraction(QuadExtractionResult extraction, Vector3[] sourcePositions, int[] sourceTriangles)
        {
            var faces = new List<int[]>(extraction.faces.Count);
            var keep = new List<int>(extraction.faces.Count);
            for (var f = 0; f < extraction.faces.Count; f++)
                if (extraction.faces[f] != null && extraction.faces[f].Length >= 3) { faces.Add(extraction.faces[f]); keep.Add(f); }
            Vector2[][] faceUvs = null;
            if (extraction.faceUvs != null && extraction.faceUvs.Length == extraction.faces.Count)
            {
                faceUvs = new Vector2[keep.Count][];
                for (var i = 0; i < keep.Count; i++) faceUvs[i] = extraction.faceUvs[keep[i]];
            }
            int[] faceMaterials = null;
            if (extraction.faceMaterials != null && extraction.faceMaterials.Length == extraction.faces.Count)
            {
                faceMaterials = new int[keep.Count];
                for (var i = 0; i < keep.Count; i++) faceMaterials[i] = extraction.faceMaterials[keep[i]];
            }
            return new Input
            {
                positions = extraction.positions, faces = faces, faceUvs = faceUvs,
                faceMaterials = faceMaterials, materialSlots = extraction.materialSlots,
                referencePositions = sourcePositions, referenceTriangles = sourceTriangles,
            };
        }

        // Triangles t and t+1 as one quad (corner indices in the unwelded mesh), when they share
        // exactly one edge, run opposite ways along it, and make a quad with four distinct corners.
        static bool Merge(int[] triangles, int t, int[] weld, out int[] quad)
        {
            quad = null;
            for (var i = 0; i < 3; i++)
            {
                int u = triangles[3 * t + i], v = triangles[3 * t + (i + 1) % 3], w = triangles[3 * t + (i + 2) % 3];
                for (var j = 0; j < 3; j++)
                {
                    int x = triangles[3 * t + 3 + j], y = triangles[3 * t + 3 + (j + 1) % 3], z = triangles[3 * t + 3 + (j + 2) % 3];
                    if (weld[x] != weld[v] || weld[y] != weld[u]) continue;
                    var q = new[] { u, z, v, w };
                    var a = weld[q[0]]; var b = weld[q[1]]; var c = weld[q[2]]; var d = weld[q[3]];
                    if (a == b || a == c || a == d || b == c || b == d || c == d) return false;
                    quad = q;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The base level of any mesh, read as Unity holds it (vertices split at UV seams, one
        /// submesh per material): welded positions, quads where two triangles were one, corner UVs.
        /// </summary>
        public static Input FromMesh(Vector3[] positions, int[] triangles, Vector2[] uvs, int[] triangleSubmesh, int submeshes)
        {
            var map = new Dictionary<Vector3, int>();
            var weld = new int[positions.Length];
            var welded = new List<Vector3>();
            for (var i = 0; i < positions.Length; i++)
            {
                if (!map.TryGetValue(positions[i], out var w)) { w = welded.Count; map[positions[i]] = w; welded.Add(positions[i]); }
                weld[i] = w;
            }
            var faces = new List<int[]>(triangles.Length / 3);
            var faceUvs = uvs != null ? new List<Vector2[]>(triangles.Length / 3) : null;
            var faceMaterials = triangleSubmesh != null && submeshes > 1 ? new List<int>(triangles.Length / 3) : null;
            var tc = triangles.Length / 3;
            for (var t = 0; t < tc; t++)
            {
                int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
                // Two triangles in a row that share an edge, the way an importer writes a quad:
                // the second's far corner goes between the shared edge's ends.
                if (t + 1 < tc && (triangleSubmesh == null || triangleSubmesh[t] == triangleSubmesh[t + 1])
                    && Merge(triangles, t, weld, out var quad))
                {
                    var q = new int[4];
                    for (var k = 0; k < 4; k++) q[k] = weld[quad[k]];
                    faces.Add(q);
                    faceUvs?.Add(new[] { uvs[quad[0]], uvs[quad[1]], uvs[quad[2]], uvs[quad[3]] });
                    faceMaterials?.Add(triangleSubmesh[t]);
                    t++;
                    continue;
                }
                var tri = new[] { weld[a], weld[b], weld[c] };
                if (tri[0] == tri[1] || tri[1] == tri[2] || tri[2] == tri[0]) continue;
                faces.Add(tri);
                faceUvs?.Add(new[] { uvs[a], uvs[b], uvs[c] });
                faceMaterials?.Add(triangleSubmesh[t]);
            }
            var weldedArray = welded.ToArray();
            var refTris = new int[triangles.Length];
            for (var i = 0; i < triangles.Length; i++) refTris[i] = weld[triangles[i]];
            return new Input
            {
                positions = weldedArray, faces = faces, faceUvs = faceUvs?.ToArray(),
                faceMaterials = faceMaterials?.ToArray(), materialSlots = Math.Max(1, submeshes),
                referencePositions = weldedArray, referenceTriangles = refTris,
            };
        }
    }
}
