// Texture baking: a fresh UV atlas for the remeshed surface, and the source's texture baked into it.
//
// WHY. AttributeTransfer gives every output corner a UV from the source's own atlas. That is exact
// inside a source chart and wrong across its seams, and a photogrammetry atlas is nothing but
// seams: Metashape cut ccnb.obj into 2,197 charts, and at 5,000 target quads 6,530 of the 14,884
// output faces straddle one. Such a face can only be clamped to one side of the seam, so it shows
// that side's texels stretched over the whole face -- streaks at vents and kerbs, a squashed hut.
// No choice of per-corner UVs fixes that, because the face genuinely covers two pieces of the
// source atlas. A new atlas whose charts follow the OUTPUT faces does: each texel then reads the
// source at its own surface point, whichever chart that lies in.
//
// UNWRAP. The quads come off a grid, so wherever the grid is regular a run of quads unfolds onto
// the integer lattice exactly, one unit square each, with no distortion and no seams inside: grow
// a chart from a quad across its edges, giving each new quad the square beyond the shared edge,
// and stop where a quad would land on a square already taken or give a vertex a second place --
// which is what going round an irregular vertex does. Each chart is scaled by its own mean edge
// length, so texel density is about even over the surface, and the charts are shelf-packed with
// the scale searched so they fill the atlas.
//
// BAKE. Every texel a face covers (its two triangles as the renderer splits them, 0-1-2 and 0-2-3)
// is mapped back to its point on the face, that point to the source surface -- where the face's
// normal line, coming in from outside, first meets a source face turned towards it, as a baker's
// cage does (rayColour; the nearest point only when the line meets none) -- and that to the source
// texture through the source triangle's own UVs, never across a seam. The nearest point alone
// scrambled small raised things on a coarse result (ccnb's cars read their side walls and the
// ground beside them, 2026-10-19); from outside they read as the photo shows them. The
// source is read from a mip level matching how many source texels fall on one output texel, so a
// texel is their average rather than one of them. Texels past the charts are filled outward from
// them, so filtering and mip-mapping at a chart's edge do not pull in the background.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    public static class TextureBake
    {
        /// <summary>A texture as pixels, row 0 at the bottom (Texture2D.GetPixels32 order).</summary>
        public sealed class Image
        {
            public Color32[] pixels;
            public int width, height;

            public Image(Color32[] pixels, int width, int height)
            {
                if (pixels == null || pixels.Length != width * height)
                    throw new ArgumentException("width * height pixels expected", nameof(pixels));
                this.pixels = pixels;
                this.width = width;
                this.height = height;
            }
        }

        /// <summary>
        /// A further map of the source's materials (smoothness, occlusion, emission, ...), laid out
        /// over the source's UVs as the main texture is: baked over the result's atlas alongside it,
        /// through the same hits (Bake fills <see cref="baked"/>).
        /// </summary>
        public sealed class Layer
        {
            /// <summary>The material property it came from, e.g. "_MetallicGlossMap".</summary>
            public string property;
            public Image[] images;
            /// <summary>Per source triangle, its image (-1: none, reads <see cref="fallback"/>); null: the first for all.</summary>
            public int[] triangleImage;
            /// <summary>Where a triangle's material has no such map.</summary>
            public Color32 fallback = new Color32(255, 255, 255, 255);
            /// <summary>Kept for whoever saves it: whether the source's texture was colour (sRGB) data.</summary>
            public bool sRGB;
            public Color32[] baked;
        }

        public sealed class Stats
        {
            public int resolution, charts, gridCharts, singleFaceCharts, largestChart, padding;
            /// <summary>Fraction of the atlas the faces cover.</summary>
            public float fill;
            /// <summary>Output texels per unit length on the surface.</summary>
            public float texelsPerUnit;
            /// <summary>Mip level the source was read at (0 = full size).</summary>
            public float sourceLevel;
            public int bakedTexels, filledTexels;
            /// <summary>Colour bake: samples whose line in from outside met no source face (read at the nearest point).</summary>
            public int colourRayMisses;
            public long unwrapMs, bakeMs;
            /// <summary>Normal map: texels whose ray found no source facing their way, and the ray's reach.</summary>
            public int normalMissed;
            /// <summary>Normal map: samples bent by the source's own normal maps.</summary>
            public int normalDetail;
            public long normalMs;
            public float normalReach;
            public bool normalFlipped;

            public override string ToString() =>
                $"bake {resolution}px: charts={charts} (grid {gridCharts}, single {singleFaceCharts}, largest {largestChart} faces) " +
                $"fill={fill:P0} texels/unit={texelsPerUnit:G3} sourceMip={sourceLevel:F2} baked={bakedTexels} gutter={filledTexels} rayMisses={colourRayMisses} " +
                $"unwrap {unwrapMs} ms, bake {bakeMs} ms";
        }

        /// <summary>Texels kept clear round every chart, as a fraction of the resolution (min 2).</summary>
        public static float paddingFraction = 1f / 512f;

        /// <summary>
        /// A quad joins a chart only if its mean edge is within this factor of the chart's: one
        /// texel density per chart, so a chart does not run from fine quads into coarse ones.
        /// </summary>
        public static float chartSizeRatio = 1.5f;

        /// <summary>
        /// Lattice cells per side of a chart's window round its seed. Grown freely, a chart spreads
        /// as a diamond (breadth first on a grid) and fills half its bounding box; in a window the
        /// regular stretches come out as full squares, which pack with almost nothing between.
        /// Measured on ccnb at 5000: unbounded, 21 % of the atlas used. 0 = no window.
        /// Superseded by <see cref="chartTile"/>: windows round successive seeds overlap, and each
        /// chart is left with the ragged remainder of the one before (61 % used, skyline packed).
        /// </summary>
        public static int chartWindow = 0;

        /// <summary>
        /// A chart does not grow across an edge whose faces turn by more than this (degrees): the
        /// lattice runs straight round a box's corner, and a chart folded over it leaves no seam
        /// for the normals to split at (hardEdgeAngle). 0 = charts cross every edge, as before.
        /// </summary>
        public static float chartCreaseAngle = 40f;

        /// <summary>
        /// Lattice cells per side of the tiles a chart is cut into once grown: aligned to the
        /// chart's own lattice, so only the tiles at its edge are partial. 0 = not cut.
        /// </summary>
        public static int chartTile = 16;

        /// <summary>
        /// Cut each tile into solid rectangles of cells, largest first. A tile round a singularity
        /// or a scan's border is notched and holed, and no packer short of a full bitmap search
        /// puts anything in a hole; rectangles pack with almost nothing between. The extra seams
        /// cost nothing visible in a baked texture: both sides read the same surface.
        /// </summary>
        public static bool rectangleCharts = true;

        /// <summary>
        /// Pack the charts by their cells, dropped onto a skyline in whichever of four turns lands
        /// lowest, instead of their bounding boxes on shelves. A chart cut by its window, a scan's
        /// border or a singularity is no rectangle, and a box wastes its empty corners.
        /// </summary>
        public static bool skylinePacking = true;

        /// <summary>Skyline columns across the atlas: the packing grid is resolution / this texels.</summary>
        public static int packBlocks = 1024;

        /// <summary>Run the charts' colours out into the empty texels (off: left transparent black).</summary>
        public static bool fillGutters = true;

        /// <summary>Texels per axis sampled per output texel (1 = the centre only).</summary>
        public static int supersample = 1;

        /// <summary>
        /// The colour bake reads each texel where the result's normal line, coming in from outside
        /// (colourReachEdges mean output edges out), first meets a source face turned towards it --
        /// what is seen looking at the quad from outside, as a baker's cage does -- and the nearest
        /// source point only when the line meets none. False: the nearest point always. On a coarse
        /// result over small raised things (cars on an aerial scan), the nearest point is a side
        /// wall or the ground beside them as often as their top, and they came out scrambled.
        /// </summary>
        public static bool rayColour = true;

        /// <summary>How far out, in mean output edges, the colour bake's lines start.</summary>
        public static float colourReachEdges = 1.5f;

        /// <summary>
        /// Smoothing sweeps (one-ring averages) of the directions the colour bake's lines run along,
        /// a smoothed cage: over a lump a cell or two wide (a car, a shed on an aerial scan) the
        /// lump's own normals tilt and throw the photo sideways -- halos and bent outlines -- where
        /// the surface's general direction looks at it as the camera did. 0: the vertex normals.
        /// </summary>
        /// <remarks>
        /// ccnb at 5000, baked 4096, rendered against the scan (PSNR, top / oblique): 0 sweeps
        /// 25.05 / 23.43 dB, 4: 25.46 / 23.39, 10: 25.78 / 23.33, 25: 26.24 / 23.22. The oblique
        /// number falls a little because the lumps are still lumps; in the renders the shed's white
        /// halos are gone and the cars' and roofs' outlines straight, from above and at an angle.
        /// Neighbours facing away (more than 90 degrees from the vertex's own normal) are not
        /// averaged in, so a thin plate's two sides keep their own directions.
        /// </remarks>
        public static int colourDirectionSweeps = 25;

        // ------------------------------------------------------------------ unwrap

        sealed class Chart
        {
            public readonly List<int> faces = new List<int>();
            // Per face, its corners' lattice points (quads), or unit-square points (other faces).
            public readonly List<Vector2[]> corners = new List<Vector2[]>();
            public float edge;      // mean edge length, world units per lattice unit
            public float width, height;
            public int x, y;        // packed origin, texels
            public int turn;        // quarter turns applied when packed
            public readonly List<(int i, int j)> cells = new List<(int i, int j)>();

            // A point of the chart's lattice after `turn` quarter turns, still in lattice units.
            public Vector2 Turned(Vector2 p)
            {
                switch (turn)
                {
                    case 1: return new Vector2(height - p.y, p.x);
                    case 2: return new Vector2(width - p.x, height - p.y);
                    case 3: return new Vector2(p.y, width - p.x);
                    default: return p;
                }
            }
        }

        /// <summary>
        /// A UV per face corner: the faces unfolded into lattice charts and packed into a square
        /// atlas of <paramref name="resolution"/> texels.
        /// </summary>
        public static Vector2[][] Unwrap(Vector3[] positions, IList<int[]> faces, int resolution, Stats stats,
            float[] faceScale = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats.resolution = resolution;
            var charts = GrowCharts(positions, faces);
            // A chart's texels per unit times its faces' scale (a chart lies on one part).
            if (faceScale != null)
                foreach (var c in charts)
                    if (c.faces.Count > 0 && c.faces[0] < faceScale.Length && faceScale[c.faces[0]] > 0f)
                        c.edge *= faceScale[c.faces[0]];
            stats.charts = charts.Count;
            foreach (var c in charts)
            {
                if (c.faces.Count == 1) stats.singleFaceCharts++;
                else stats.gridCharts++;
                stats.largestChart = Math.Max(stats.largestChart, c.faces.Count);
            }

            var pad = Math.Max(2, Mathf.RoundToInt(resolution * paddingFraction));
            stats.padding = pad;
            foreach (var c in charts)
                foreach (var pts in c.corners)
                {
                    float mi = float.MaxValue, mj = float.MaxValue;
                    foreach (var q in pts) { mi = Mathf.Min(mi, q.x); mj = Mathf.Min(mj, q.y); }
                    c.cells.Add((Mathf.RoundToInt(mi), Mathf.RoundToInt(mj)));
                }

            // Texels per world unit: the largest that packs.
            double area = 0, cellArea = 0;
            foreach (var c in charts)
            {
                area += c.width * c.height * c.edge * c.edge;
                cellArea += c.cells.Count * c.edge * c.edge;
            }
            bool Fits(float sc) => skylinePacking
                ? PackSkyline(charts, sc, resolution, pad)
                : Pack(charts, sc, resolution, pad);
            if (skylinePacking)
                charts.Sort((a, b) => (b.cells.Count * b.edge * b.edge).CompareTo(a.cells.Count * a.edge * a.edge));
            else
                // Tallest first, for the shelves.
                charts.Sort((a, b) => (b.height * b.edge).CompareTo(a.height * a.edge));
            double lo = 0, hi = resolution / Math.Sqrt(Math.Max(skylinePacking ? cellArea : area, 1e-20));
            // Skyline packs are dearer: start from a bracket that nearly always holds.
            if (skylinePacking && Fits((float)(0.5 * hi))) lo = 0.5 * hi;
            for (var i = 0; i < (skylinePacking ? 11 : 40); i++)
            {
                var mid = 0.5 * (lo + hi);
                if (Fits((float)mid)) lo = mid; else hi = mid;
            }
            var scale = (float)lo;
            if (!(scale > 0f) || !Fits(scale))
                throw new InvalidOperationException("faces do not fit the atlas");
            stats.texelsPerUnit = scale;

            var result = new Vector2[faces.Count][];
            double covered = 0;
            foreach (var c in charts)
            {
                var px = c.edge * scale;
                for (var k = 0; k < c.faces.Count; k++)
                {
                    var src = c.corners[k];
                    var uv = new Vector2[src.Length];
                    // The skyline packs with half the padding round each chart, the shelves a whole.
                    var margin = skylinePacking ? (pad + 1) / 2 : pad;
                    for (var i = 0; i < src.Length; i++)
                    {
                        var q = c.Turned(src[i]);
                        uv[i] = new Vector2((c.x + margin + q.x * px) / resolution,
                                            (c.y + margin + q.y * px) / resolution);
                    }
                    result[c.faces[k]] = uv;
                    for (var i = 1; i + 1 < uv.Length; i++)
                        covered += Mathf.Abs((uv[i].x - uv[0].x) * (uv[i + 1].y - uv[0].y)
                                             - (uv[i].y - uv[0].y) * (uv[i + 1].x - uv[0].x)) * 0.5;
                }
            }
            for (var f = 0; f < faces.Count; f++)
                if (result[f] == null) result[f] = new Vector2[faces[f]?.Length ?? 0];
            stats.fill = (float)covered;
            stats.unwrapMs = watch.ElapsedMilliseconds;
            return result;
        }

        static bool Pack(List<Chart> charts, float scale, int resolution, int pad)
        {
            int x = 0, y = 0, shelf = 0;
            foreach (var c in charts)
            {
                var w = Mathf.CeilToInt(c.width * c.edge * scale) + 2 * pad;
                var h = Mathf.CeilToInt(c.height * c.edge * scale) + 2 * pad;
                if (w > resolution) return false;
                if (x + w > resolution) { y += shelf; x = 0; shelf = 0; }
                if (y + h > resolution) return false;
                c.x = x;
                c.y = y;
                x += w;
                shelf = Math.Max(shelf, h);
            }
            return true;
        }

        // The charts dropped one by one onto a skyline of blocks (a few texels each): each takes the
        // column and turn where its lowest cells rest highest-up least, given the column heights
        // under each of its columns. Gaps under a chart's overhangs are lost, its notches are not.
        static bool PackSkyline(List<Chart> charts, float scale, int resolution, int pad)
        {
            var block = Math.Max(1, resolution / packBlocks);
            var blocks = resolution / block;
            var sky = new int[blocks];
            var margin = (pad + 1) / 2;
            var bottom = new int[blocks + 1];
            var deque = new int[blocks + 1];
            var top = new int[blocks + 1];
            foreach (var c in charts)
            {
                var px = c.edge * scale;
                int bestX = -1, bestY = 0, bestTurn = 0, bestTop = int.MaxValue;
                for (var turn = 0; turn < 4; turn++)
                {
                    // A square chart turned by a half looks the same; skip the repeats cheaply.
                    if (c.cells.Count == 1 && turn > 0) break;
                    var w = (turn & 1) == 0 ? c.width : c.height;
                    var h = (turn & 1) == 0 ? c.height : c.width;
                    var bw = Mathf.CeilToInt((w * px + 2 * margin) / block);
                    var bh = Mathf.CeilToInt((h * px + 2 * margin) / block);
                    if (bw > blocks || bh > blocks) continue;
                    for (var i = 0; i < bw; i++) { bottom[i] = int.MaxValue; top[i] = -1; }
                    c.turn = turn;
                    foreach (var (ci, cj) in c.cells)
                    {
                        // The cell's corners after the turn: its texel box, with the margin.
                        var a = c.Turned(new Vector2(ci, cj));
                        var b = c.Turned(new Vector2(ci + 1, cj + 1));
                        var x0 = Mathf.Min(a.x, b.x) * px; var x1 = Mathf.Max(a.x, b.x) * px + 2 * margin;
                        var y0 = Mathf.Min(a.y, b.y) * px; var y1 = Mathf.Max(a.y, b.y) * px + 2 * margin;
                        var bx0 = Mathf.FloorToInt(x0 / block); var bx1 = Math.Min(bw - 1, Mathf.CeilToInt(x1 / block) - 1);
                        var by0 = Mathf.FloorToInt(y0 / block); var by1 = Mathf.CeilToInt(y1 / block);
                        for (var bx = bx0; bx <= bx1; bx++)
                        {
                            bottom[bx] = Math.Min(bottom[bx], by0);
                            top[bx] = Math.Max(top[bx], by1);
                        }
                    }
                    var flat = true;
                    for (var i = 0; i < bw && flat; i++) flat = top[i] >= 0 && bottom[i] == 0;
                    if (flat)
                    {
                        // A flat bottom (any rectangle): the rest height is the skyline's maximum
                        // under the chart, a sliding window maximum.
                        int head = 0, tail = 0;
                        for (var x = 0; x < blocks; x++)
                        {
                            while (tail > head && sky[deque[tail - 1]] <= sky[x]) tail--;
                            deque[tail++] = x;
                            if (deque[head] <= x - bw) head++;
                            if (x < bw - 1) continue;
                            var y = sky[deque[head]];
                            var reach = y + bh;
                            if (reach > blocks || reach >= bestTop) continue;
                            bestTop = reach; bestX = x - bw + 1; bestY = y; bestTurn = turn;
                        }
                        continue;
                    }
                    for (var x = 0; x + bw <= blocks; x++)
                    {
                        var y = 0;
                        for (var i = 0; i < bw; i++)
                            if (top[i] >= 0) y = Math.Max(y, sky[x + i] - bottom[i]);
                        var reach = y + bh;
                        if (reach > blocks || reach >= bestTop) continue;
                        bestTop = reach; bestX = x; bestY = y; bestTurn = turn;
                    }
                }
                if (bestX < 0) return false;
                c.turn = bestTurn;
                // Raise the skyline under it, from the chosen turn's profile.
                {
                    var w = (bestTurn & 1) == 0 ? c.width : c.height;
                    var bw = Mathf.CeilToInt((w * px + 2 * margin) / block);
                    for (var i = 0; i < bw; i++) top[i] = -1;
                    foreach (var (ci, cj) in c.cells)
                    {
                        var a = c.Turned(new Vector2(ci, cj));
                        var b = c.Turned(new Vector2(ci + 1, cj + 1));
                        var x0 = Mathf.Min(a.x, b.x) * px; var x1 = Mathf.Max(a.x, b.x) * px + 2 * margin;
                        var y1 = Mathf.Max(a.y, b.y) * px + 2 * margin;
                        var bx0 = Mathf.FloorToInt(x0 / block); var bx1 = Math.Min(bw - 1, Mathf.CeilToInt(x1 / block) - 1);
                        var by1 = Mathf.CeilToInt(y1 / block);
                        for (var bx = bx0; bx <= bx1; bx++) top[bx] = Math.Max(top[bx], by1);
                    }
                    for (var i = 0; i < bw; i++)
                        if (top[i] >= 0) sky[bestX + i] = bestY + top[i];
                }
                c.x = bestX * block;
                c.y = bestY * block;
            }
            return true;
        }

        static long LatticeKey(int i, int j) => ((long)(i + (1 << 24)) << 32) | (uint)(j + (1 << 24));

        static float MeanEdge(Vector3[] positions, int[] face)
        {
            var sum = 0f;
            for (var i = 0; i < face.Length; i++)
                sum += (positions[face[(i + 1) % face.Length]] - positions[face[i]]).magnitude;
            return sum / face.Length;
        }

        static List<Chart> GrowCharts(Vector3[] positions, IList<int[]> faces)
        {
            // The faces on each edge; an edge on three or more is not crossed.
            var edgeFaces = new Dictionary<long, (int a, int b)>(faces.Count * 2, Extraction.EdgeKey.Comparer);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null) continue;
                for (var i = 0; i < face.Length; i++)
                {
                    int u = face[i], v = face[(i + 1) % face.Length];
                    var key = ((long)Math.Min(u, v) << 32) | (uint)Math.Max(u, v);
                    if (!edgeFaces.TryGetValue(key, out var e)) edgeFaces[key] = (f, -1);
                    else if (e.b < 0) edgeFaces[key] = (e.a, f);
                    else edgeFaces[key] = (-2, -2);
                }
            }
            int Across(int f, int u, int v)
            {
                var key = ((long)Math.Min(u, v) << 32) | (uint)Math.Max(u, v);
                if (!edgeFaces.TryGetValue(key, out var e) || e.a < 0 || e.b < 0) return -1;
                return e.a == f ? e.b : e.a;
            }

            // A chart stops at a crease: its seam is where a normal-mapped result's normals split.
            var creaseCos = chartCreaseAngle > 0f ? Mathf.Cos(chartCreaseAngle * Mathf.Deg2Rad) : -2f;
            var faceNormal = new Vector3[faces.Count];
            if (chartCreaseAngle > 0f)
                for (var f = 0; f < faces.Count; f++)
                    if (faces[f] != null && faces[f].Length >= 3)
                        faceNormal[f] = MeshMath.SafeNormalize(QuadExtractor.FaceNormal(positions, faces[f]), Vector3.zero);

            var taken = new bool[faces.Count];
            var charts = new List<Chart>();
            var place = new Dictionary<int, (int i, int j)>();
            var owner = new Dictionary<long, int>();
            var cells = new HashSet<long>();
            var queue = new Queue<int>();
            var faceCorners = new Dictionary<int, (int i, int j)[]>();

            for (var seed = 0; seed < faces.Count; seed++)
            {
                if (taken[seed] || faces[seed] == null || faces[seed].Length < 3) continue;
                var seedFace = faces[seed];
                var chart = new Chart();
                taken[seed] = true;
                if (seedFace.Length != 4)
                {
                    // Its own unit square: a triangle on half of it, an n-gon round its middle.
                    var pts = new Vector2[seedFace.Length];
                    if (seedFace.Length == 3) { pts[0] = new Vector2(0, 0); pts[1] = new Vector2(1, 0); pts[2] = new Vector2(0, 1); }
                    else
                        for (var i = 0; i < pts.Length; i++)
                        {
                            var a = 2f * Mathf.PI * i / pts.Length;
                            pts[i] = new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a));
                        }
                    chart.faces.Add(seed);
                    chart.corners.Add(pts);
                    chart.edge = MeanEdge(positions, seedFace);
                    chart.width = chart.height = 1f;
                    charts.Add(chart);
                    continue;
                }

                place.Clear(); owner.Clear(); cells.Clear(); queue.Clear(); faceCorners.Clear();
                var edgeSum = 0.0;
                bool TryPlace(int f, (int i, int j)[] at)
                {
                    var face = faces[f];
                    int ci = int.MaxValue, cj = int.MaxValue;
                    foreach (var p in at) { ci = Math.Min(ci, p.i); cj = Math.Min(cj, p.j); }
                    if (cells.Contains(LatticeKey(ci, cj))) return false;
                    if (chartWindow > 0 && (ci < -chartWindow / 2 || cj < -chartWindow / 2
                            || ci >= chartWindow - chartWindow / 2 || cj >= chartWindow - chartWindow / 2))
                        return false;
                    for (var k = 0; k < 4; k++)
                    {
                        if (place.TryGetValue(face[k], out var had) && had != at[k]) return false;
                        if (owner.TryGetValue(LatticeKey(at[k].i, at[k].j), out var v) && v != face[k]) return false;
                    }
                    cells.Add(LatticeKey(ci, cj));
                    for (var k = 0; k < 4; k++)
                    {
                        place[face[k]] = at[k];
                        owner[LatticeKey(at[k].i, at[k].j)] = face[k];
                    }
                    faceCorners[f] = at;
                    chart.faces.Add(f);
                    taken[f] = true;
                    edgeSum += MeanEdge(positions, face);
                    queue.Enqueue(f);
                    return true;
                }
                TryPlace(seed, new[] { (0, 0), (1, 0), (1, 1), (0, 1) });

                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    var face = faces[f];
                    var at = faceCorners[f];
                    for (var k = 0; k < 4; k++)
                    {
                        int a = face[k], b = face[(k + 1) % 4];
                        var g = Across(f, a, b);
                        if (g < 0 || taken[g] || faces[g].Length != 4) continue;
                        if (Vector3.Dot(faceNormal[f], faceNormal[g]) < creaseCos) continue;
                        var gf = faces[g];
                        // Wound as f, g runs the shared edge b -> a.
                        var m = -1;
                        for (var i = 0; i < 4; i++)
                            if (gf[i] == b && gf[(i + 1) % 4] == a) { m = i; break; }
                        if (m < 0) continue;
                        var ratio = MeanEdge(positions, gf) / (float)(edgeSum / chart.faces.Count);
                        if (ratio > chartSizeRatio || ratio < 1f / chartSizeRatio) continue;
                        var A = at[k];
                        var B = at[(k + 1) % 4];
                        // B -> A along the edge, and g on its left: A + r, B + r.
                        int ex = A.i - B.i, ey = A.j - B.j;
                        int rx = -ey, ry = ex;
                        var gat = new (int i, int j)[4];
                        gat[m] = B;
                        gat[(m + 1) % 4] = A;
                        gat[(m + 2) % 4] = (A.i + rx, A.j + ry);
                        gat[(m + 3) % 4] = (B.i + rx, B.j + ry);
                        TryPlace(g, gat);
                    }
                }

                if (chartTile <= 0)
                {
                    charts.Add(Finish(positions, faces, chart.faces, faceCorners));
                    continue;
                }
                // Cut into tiles on the chart's own lattice.
                var tiles = new Dictionary<long, List<int>>();
                foreach (var f in chart.faces)
                {
                    var at = faceCorners[f];
                    int ci = int.MaxValue, cj = int.MaxValue;
                    foreach (var q in at) { ci = Math.Min(ci, q.i); cj = Math.Min(cj, q.j); }
                    var key = LatticeKey(FloorDiv(ci, chartTile), FloorDiv(cj, chartTile));
                    if (!tiles.TryGetValue(key, out var list)) tiles[key] = list = new List<int>();
                    list.Add(f);
                }
                foreach (var list in tiles.Values)
                {
                    if (!rectangleCharts) { charts.Add(Finish(positions, faces, list, faceCorners)); continue; }
                    foreach (var rect in Rectangles(list, faceCorners))
                        charts.Add(Finish(positions, faces, rect, faceCorners));
                }
            }
            return charts;
        }

        // The faces of one lattice region, as solid rectangles of cells: repeatedly the largest
        // rectangle left, by the histogram method over the region's cell matrix.
        static List<List<int>> Rectangles(List<int> region, Dictionary<int, (int i, int j)[]> faceCorners)
        {
            int minI = int.MaxValue, minJ = int.MaxValue, maxI = int.MinValue, maxJ = int.MinValue;
            var cellOf = new Dictionary<int, (int i, int j)>();
            foreach (var f in region)
            {
                int ci = int.MaxValue, cj = int.MaxValue;
                foreach (var q in faceCorners[f]) { ci = Math.Min(ci, q.i); cj = Math.Min(cj, q.j); }
                cellOf[f] = (ci, cj);
                minI = Math.Min(minI, ci); maxI = Math.Max(maxI, ci);
                minJ = Math.Min(minJ, cj); maxJ = Math.Max(maxJ, cj);
            }
            int W = maxI - minI + 1, H = maxJ - minJ + 1;
            var grid = new int[W * H];
            for (var k = 0; k < grid.Length; k++) grid[k] = -1;
            foreach (var kv in cellOf) grid[(kv.Value.j - minJ) * W + kv.Value.i - minI] = kv.Key;
            var left = region.Count;
            var result = new List<List<int>>();
            var heights = new int[W];
            var stack = new Stack<int>();
            while (left > 0)
            {
                int bestArea = 0, bx0 = 0, bx1 = 0, by0 = 0, by1 = 0;
                Array.Clear(heights, 0, W);
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++) heights[x] = grid[y * W + x] >= 0 ? heights[x] + 1 : 0;
                    stack.Clear();
                    for (var x = 0; x <= W; x++)
                    {
                        var h = x < W ? heights[x] : 0;
                        while (stack.Count > 0 && heights[stack.Peek()] >= h)
                        {
                            var top = stack.Pop();
                            var height = heights[top];
                            var start = stack.Count > 0 ? stack.Peek() + 1 : 0;
                            var area = height * (x - start);
                            if (area > bestArea) { bestArea = area; bx0 = start; bx1 = x - 1; by0 = y - height + 1; by1 = y; }
                        }
                        stack.Push(x);
                    }
                }
                var rect = new List<int>(bestArea);
                for (var y = by0; y <= by1; y++)
                    for (var x = bx0; x <= bx1; x++)
                    {
                        rect.Add(grid[y * W + x]);
                        grid[y * W + x] = -1;
                    }
                left -= rect.Count;
                result.Add(rect);
            }
            return result;
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        // A chart from faces placed on one lattice: moved to the origin, a quarter turn if taller
        // than wide, and its mean edge for its texel size.
        static Chart Finish(Vector3[] positions, IList<int[]> faces, List<int> chartFaces,
            Dictionary<int, (int i, int j)[]> faceCorners)
        {
            var chart = new Chart();
            int minI = int.MaxValue, minJ = int.MaxValue, maxI = int.MinValue, maxJ = int.MinValue;
            var edgeSum = 0.0;
            foreach (var f in chartFaces)
            {
                foreach (var p in faceCorners[f])
                {
                    minI = Math.Min(minI, p.i); maxI = Math.Max(maxI, p.i);
                    minJ = Math.Min(minJ, p.j); maxJ = Math.Max(maxJ, p.j);
                }
                edgeSum += MeanEdge(positions, faces[f]);
            }
            var turn = maxJ - minJ > maxI - minI;
            foreach (var f in chartFaces)
            {
                var at = faceCorners[f];
                var pts = new Vector2[4];
                for (var k = 0; k < 4; k++)
                {
                    int i = at[k].i - minI, j = at[k].j - minJ;
                    // A quarter turn, (i, j) -> (H - j, i): keeps the winding.
                    pts[k] = turn ? new Vector2(maxJ - minJ - j, i) : new Vector2(i, j);
                }
                chart.faces.Add(f);
                chart.corners.Add(pts);
            }
            chart.width = turn ? maxJ - minJ : maxI - minI;
            chart.height = turn ? maxI - minI : maxJ - minJ;
            chart.edge = (float)(edgeSum / chartFaces.Count);
            return chart;
        }

        // ------------------------------------------------------------------ bake

        sealed class Mips
        {
            public readonly List<Image> levels = new List<Image>();

            public Mips(Image top)
            {
                levels.Add(top);
                var cur = top;
                while (cur.width > 1 || cur.height > 1)
                {
                    int w = Math.Max(1, cur.width / 2), h = Math.Max(1, cur.height / 2);
                    var px = new Color32[w * h];
                    var src = cur;
                    Parallel.For(0, h, BackgroundScheduler.Options, y =>
                    {
                        for (var x = 0; x < w; x++)
                        {
                            int r = 0, g = 0, b = 0, a = 0, n = 0;
                            for (var dy = 0; dy < 2; dy++)
                                for (var dx = 0; dx < 2; dx++)
                                {
                                    int sx = Math.Min(src.width - 1, 2 * x + dx), sy = Math.Min(src.height - 1, 2 * y + dy);
                                    var c = src.pixels[sy * src.width + sx];
                                    r += c.r; g += c.g; b += c.b; a += c.a; n++;
                                }
                            px[y * w + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                        }
                    });
                    cur = new Image(px, w, h);
                    levels.Add(cur);
                }
            }

            static Vector4 Bilinear(Image im, Vector2 uv)
            {
                var fx = uv.x * im.width - 0.5f;
                var fy = uv.y * im.height - 0.5f;
                var x0 = Mathf.FloorToInt(fx);
                var y0 = Mathf.FloorToInt(fy);
                float tx = fx - x0, ty = fy - y0;
                // An atlas (UVs inside 0..1) clamps at its edges -- wrapping would blend a chart on
                // the left edge with whatever sits on the right; a tiling texture (UVs past 1) wraps.
                var tiled = uv.x < 0f || uv.y < 0f || uv.x > 1f || uv.y > 1f;
                int Wrap(int v, int n)
                {
                    if (!tiled) return v < 0 ? 0 : v >= n ? n - 1 : v;
                    v %= n;
                    return v < 0 ? v + n : v;
                }
                int xa = Wrap(x0, im.width), xb = Wrap(x0 + 1, im.width);
                int ya = Wrap(y0, im.height), yb = Wrap(y0 + 1, im.height);
                Vector4 C(int x, int y) { var c = im.pixels[y * im.width + x]; return new Vector4(c.r, c.g, c.b, c.a); }
                return Vector4.Lerp(Vector4.Lerp(C(xa, ya), C(xb, ya), tx), Vector4.Lerp(C(xa, yb), C(xb, yb), tx), ty);
            }

            public Vector4 Sample(Vector2 uv, float level)
            {
                level = Mathf.Clamp(level, 0f, levels.Count - 1);
                var l0 = Mathf.FloorToInt(level);
                var l1 = Math.Min(levels.Count - 1, l0 + 1);
                var t = level - l0;
                var a = Bilinear(levels[l0], uv);
                return t > 0f ? Vector4.Lerp(a, Bilinear(levels[l1], uv), t) : a;
            }
        }

        /// <summary>
        /// The atlas: every texel the faces cover, read from the source texture at the nearest point
        /// of the source surface, and the rest filled out from the charts.
        /// </summary>
        /// <param name="sourceTriangleTexture">
        /// Per source triangle, its texture in <paramref name="textures"/> (its material); null when
        /// there is one texture. A triangle with no texture reads mid grey.
        /// </param>
        /// <param name="layers">
        /// Further maps read at the same source points as the main texture (each its own mip
        /// level); each layer's <see cref="Layer.baked"/> is filled. Null: none.
        /// </param>
        public static Color32[] Bake(Vector3[] sourcePositions, int[] sourceTriangles, Vector2[] sourceUvs,
            int[] sourceTriangleTexture, Image[] textures, Vector3[] positions, IList<int[]> faces,
            Vector2[][] faceUvs, int resolution, Stats stats, Action<float> onProgress = null, Layer[] layers = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var mips = new Mips[textures.Length];
            for (var i = 0; i < textures.Length; i++)
                if (textures[i] != null) mips[i] = new Mips(textures[i]);

            var outputDensity = (double)stats.texelsPerUnit * stats.texelsPerUnit * supersample * supersample;
            var level = SourceLevel(sourcePositions, sourceTriangles, sourceUvs, sourceTriangleTexture, textures, outputDensity);
            stats.sourceLevel = level;
            var layerCount = layers?.Length ?? 0;
            var layerMips = new Mips[layerCount][];
            var layerLevel = new float[layerCount];
            var layerPixels = new Color32[layerCount][];
            for (var l = 0; l < layerCount; l++)
            {
                var images = layers[l]?.images ?? Array.Empty<Image>();
                layerMips[l] = new Mips[images.Length];
                for (var i = 0; i < images.Length; i++)
                    if (images[i] != null) layerMips[l][i] = new Mips(images[i]);
                layerLevel[l] = SourceLevel(sourcePositions, sourceTriangles, sourceUvs, layers[l]?.triangleImage, images, outputDensity);
                layerPixels[l] = new Color32[resolution * resolution];
            }

            var grid = new SpatialGrid(sourcePositions, sourceTriangles);
            var pixels = new Color32[resolution * resolution];
            var written = new bool[resolution * resolution];
            var done = 0;
            var baked = 0;
            var ss = Math.Max(1, supersample);

            // rayColour: the result's vertex normals, the reach, and which way the source is wound
            // against them (by majority, as the normal map's bake).
            Vector3[] normals = null;
            var reach = 0f;
            var facing = 0f;
            var rayMisses = 0;
            if (rayColour)
            {
                var list = faces as List<int[]> ?? new List<int[]>(faces);
                normals = Extraction.NormalRecalculator.Compute(list, positions, null);
                if (colourDirectionSweeps > 0) normals = SmoothDirections(list, normals, colourDirectionSweeps);
                double edgeSum = 0;
                var edgeCount = 0;
                foreach (var face in faces)
                    if (face != null)
                        for (var k = 0; k < face.Length; k++)
                        {
                            edgeSum += (positions[face[(k + 1) % face.Length]] - positions[face[k]]).magnitude;
                            edgeCount++;
                        }
                reach = colourReachEdges * (float)(edgeSum / Math.Max(1, edgeCount));
                var agree = 0;
                for (var f = 0; f < faces.Count; f += Math.Max(1, faces.Count / 2000))
                {
                    var face = faces[f];
                    if (face == null || face.Length < 3) continue;
                    var c = Vector3.zero;
                    var n = Vector3.zero;
                    foreach (var v in face) { c += positions[v]; n += normals[v]; }
                    c /= face.Length;
                    grid.NearestDistance(c, out var tri);
                    if (tri < 0) continue;
                    grid.TriangleCorners(tri, out var ia, out var ib, out var ic);
                    var sn = Vector3.Cross(sourcePositions[ib] - sourcePositions[ia], sourcePositions[ic] - sourcePositions[ia]);
                    agree += Vector3.Dot(sn, n) >= 0f ? 1 : -1;
                }
                // FirstHit goes along -n; a face turned towards it has its normal along +n.
                facing = agree < 0 ? 1f : -1f;
            }

            // The layers at a source point: added into sums (one per layer).
            void LayersAt(int t, Vector2 uv, Vector4[] sums)
            {
                for (var l = 0; l < layerCount; l++)
                {
                    var map = t >= 0 && layers[l] != null
                        ? layers[l].triangleImage != null ? (t < layers[l].triangleImage.Length ? layers[l].triangleImage[t] : -1) : 0
                        : -1;
                    if (map >= 0 && map < layerMips[l].Length && layerMips[l][map] != null)
                        sums[l] += layerMips[l][map].Sample(uv, layerLevel[l]);
                    else
                    {
                        var c = layers[l] != null ? layers[l].fallback : new Color32(255, 255, 255, 255);
                        sums[l] += new Vector4(c.r, c.g, c.b, c.a);
                    }
                }
            }

            Vector4 SampleAt(Vector3 p, Vector3 n, Vector4[] layerSums)
            {
                var t = -1;
                Vector3 q = default;
                if (rayColour && n != Vector3.zero)
                {
                    var origin = p + n * reach;
                    if (grid.FirstHit(origin, -n, 2f * reach, facing, out var along, out var hit))
                    {
                        t = hit;
                        q = origin - n * along;
                    }
                    else System.Threading.Interlocked.Increment(ref rayMisses);
                }
                if (t < 0)
                {
                    grid.NearestDistance(p, out t);
                    if (t >= 0)
                    {
                        grid.TriangleCorners(t, out var na, out var nb, out var nc);
                        q = SpatialGrid.ClosestPointOnTriangle(p, sourcePositions[na], sourcePositions[nb], sourcePositions[nc]);
                    }
                }
                if (t < 0)
                {
                    if (layerCount > 0) LayersAt(-1, default, layerSums);
                    return new Vector4(128, 128, 128, 255);
                }
                int ia = sourceTriangles[3 * t], ib = sourceTriangles[3 * t + 1], ic = sourceTriangles[3 * t + 2];
                Vector3 a = sourcePositions[ia], b = sourcePositions[ib], c = sourcePositions[ic];
                Barycentric(q, a, b, c, out var wa, out var wb, out var wc);
                var uv = sourceUvs[ia] * wa + sourceUvs[ib] * wb + sourceUvs[ic] * wc;
                if (layerCount > 0) LayersAt(t, uv, layerSums);
                var tex = sourceTriangleTexture != null ? sourceTriangleTexture[t] : 0;
                if (tex < 0 || tex >= mips.Length || mips[tex] == null) return new Vector4(128, 128, 128, 255);
                return mips[tex].Sample(uv, level);
            }

            static Color32 ToColour(Vector4 v) => new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(v.x), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(v.y), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(v.z), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(v.w), 0, 255));

            Parallel.For(0, faces.Count, BackgroundScheduler.Options, f =>
            {
                var face = faces[f];
                var uv = faceUvs[f];
                if (face == null || uv == null || face.Length < 3 || uv.Length != face.Length) return;
                var local = 0;
                var layerSums = layerCount > 0 ? new Vector4[layerCount] : null;
                // As the renderer splits it: a fan from corner 0.
                for (var i = 1; i + 1 < face.Length; i++)
                {
                    Vector2 u0 = uv[0] * resolution, u1 = uv[i] * resolution, u2 = uv[i + 1] * resolution;
                    Vector3 p0 = positions[face[0]], p1 = positions[face[i]], p2 = positions[face[i + 1]];
                    var area = (u1.x - u0.x) * (u2.y - u0.y) - (u1.y - u0.y) * (u2.x - u0.x);
                    if (Mathf.Abs(area) < 1e-12f) continue;
                    // Texels whose centre is within 0.75 texel of the triangle: neighbours overlap a
                    // little rather than leave a crack, and the chart gets a texel of its own
                    // colour all round before the fill.
                    const float k_Reach = 0.75f;
                    int x0 = Math.Max(0, Mathf.FloorToInt(Mathf.Min(u0.x, Mathf.Min(u1.x, u2.x)) - k_Reach));
                    int x1 = Math.Min(resolution - 1, Mathf.CeilToInt(Mathf.Max(u0.x, Mathf.Max(u1.x, u2.x)) + k_Reach));
                    int y0 = Math.Max(0, Mathf.FloorToInt(Mathf.Min(u0.y, Mathf.Min(u1.y, u2.y)) - k_Reach));
                    int y1 = Math.Min(resolution - 1, Mathf.CeilToInt(Mathf.Max(u0.y, Mathf.Max(u1.y, u2.y)) + k_Reach));
                    float L01 = (u1 - u0).magnitude, L12 = (u2 - u1).magnitude, L20 = (u0 - u2).magnitude;
                    var sign = Mathf.Sign(area);
                    for (var y = y0; y <= y1; y++)
                        for (var x = x0; x <= x1; x++)
                        {
                            var c = new Vector2(x + 0.5f, y + 0.5f);
                            // Signed distances to the three edges, positive inside.
                            float d0 = sign * Cross(u1 - u0, c - u0) / L01;
                            float d1 = sign * Cross(u2 - u1, c - u1) / L12;
                            float d2 = sign * Cross(u0 - u2, c - u2) / L20;
                            if (d0 < -k_Reach || d1 < -k_Reach || d2 < -k_Reach) continue;
                            var sum = Vector4.zero;
                            if (layerSums != null) Array.Clear(layerSums, 0, layerCount);
                            for (var sy = 0; sy < ss; sy++)
                                for (var sx = 0; sx < ss; sx++)
                                {
                                    var s = new Vector2(x + (sx + 0.5f) / ss, y + (sy + 0.5f) / ss);
                                    // Barycentric in UV, clamped onto the triangle.
                                    var w1 = sign * Cross(u0 - u2, s - u2) / Mathf.Abs(area);
                                    var w2 = sign * Cross(u1 - u0, s - u0) / Mathf.Abs(area);
                                    w1 = Mathf.Clamp01(w1); w2 = Mathf.Clamp01(w2);
                                    if (w1 + w2 > 1f) { var k = 1f / (w1 + w2); w1 *= k; w2 *= k; }
                                    var w0 = 1f - w1 - w2;
                                    var sn = normals != null
                                        ? MeshMath.SafeNormalize(normals[face[0]] * w0 + normals[face[i]] * w1 + normals[face[i + 1]] * w2, Vector3.zero)
                                        : Vector3.zero;
                                    sum += SampleAt(p0 * w0 + p1 * w1 + p2 * w2, sn, layerSums);
                                }
                            sum /= ss * ss;
                            var idx = y * resolution + x;
                            pixels[idx] = ToColour(sum);
                            for (var l = 0; l < layerCount; l++) layerPixels[l][idx] = ToColour(layerSums[l] / (ss * ss));
                            written[idx] = true;
                            local++;
                        }
                }
                System.Threading.Interlocked.Add(ref baked, local);
                var n = System.Threading.Interlocked.Increment(ref done);
                if (onProgress != null && (n & 1023) == 0) onProgress((float)n / faces.Count);
            });
            stats.bakedTexels = 0;
            foreach (var w in written) if (w) stats.bakedTexels++;

            for (var l = 0; l < layerCount; l++)
            {
                // Each from the same texels the main texture was baked to.
                if (fillGutters) FillOutward(layerPixels[l], (bool[])written.Clone(), resolution);
                if (layers[l] != null) layers[l].baked = layerPixels[l];
            }
            if (fillGutters) stats.filledTexels = FillOutward(pixels, written, resolution);
            stats.colourRayMisses = rayMisses;
            stats.bakeMs = watch.ElapsedMilliseconds;
            return pixels;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        // The mip level to read the source at: source texels per output texel, from areas (sum of
        // UV area x texture size over the world area, against the output's texels per unit squared).
        static float SourceLevel(Vector3[] sourcePositions, int[] sourceTriangles, Vector2[] sourceUvs,
            int[] sourceTriangleTexture, Image[] textures, double outputDensity)
        {
            double uvArea = 0, worldArea = 0;
            for (var t = 0; t < sourceTriangles.Length / 3; t++)
            {
                var tex = sourceTriangleTexture != null ? (t < sourceTriangleTexture.Length ? sourceTriangleTexture[t] : -1) : 0;
                if (tex < 0 || tex >= textures.Length || textures[tex] == null) continue;
                int a = sourceTriangles[3 * t], b = sourceTriangles[3 * t + 1], c = sourceTriangles[3 * t + 2];
                var wa = Vector3.Cross(sourcePositions[b] - sourcePositions[a], sourcePositions[c] - sourcePositions[a]).magnitude * 0.5;
                var ua = Mathf.Abs((sourceUvs[b].x - sourceUvs[a].x) * (sourceUvs[c].y - sourceUvs[a].y)
                                   - (sourceUvs[b].y - sourceUvs[a].y) * (sourceUvs[c].x - sourceUvs[a].x)) * 0.5;
                worldArea += wa;
                uvArea += ua * textures[tex].width * textures[tex].height;
            }
            var sourceDensity = worldArea > 0 ? uvArea / worldArea : 0;
            return outputDensity > 0 && sourceDensity > 0
                ? Mathf.Max(0f, 0.5f * Mathf.Log((float)(sourceDensity / outputDensity), 2f))
                : 0f;
        }

        // Each sweep: every vertex's direction becomes the normalised sum over itself and its
        // face neighbours.
        static Vector3[] SmoothDirections(List<int[]> faces, Vector3[] normals, int sweeps)
        {
            var n = normals.Length;
            var neighbours = new List<int>[n];
            for (var i = 0; i < n; i++) neighbours[i] = new List<int>(6);
            foreach (var face in faces)
            {
                if (face == null) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    int a = face[k], b = face[(k + 1) % face.Length];
                    if (!neighbours[a].Contains(b)) neighbours[a].Add(b);
                    if (!neighbours[b].Contains(a)) neighbours[b].Add(a);
                }
            }
            var current = (Vector3[])normals.Clone();
            var next = new Vector3[n];
            for (var s = 0; s < sweeps; s++)
            {
                var from = current;
                var to = next;
                Parallel.For(0, n, BackgroundScheduler.Options, v =>
                {
                    // Only neighbours facing the same side: across a thin plate's rim the far
                    // side faces the other way, and averaging the two would cancel.
                    var sum = from[v];
                    foreach (var w in neighbours[v])
                        if (Vector3.Dot(from[w], normals[v]) > 0f) sum += from[w];
                    to[v] = MeshMath.SafeNormalize(sum, from[v]);
                });
                (current, next) = (next, current);
            }
            return current;
        }

        // ------------------------------------------------------------------ normal map

        /// <summary>How far, in mean output edges, a texel looks along its normal for the source.</summary>
        public static float normalReachEdges = 1.5f;

        /// <summary>
        /// With the normal map, an occlusion map of the source's cavities: how far below its
        /// surroundings the source lies under each texel, darker the deeper. The normal map cannot
        /// show a tyre's tread: a texel's line meets a block's top or a groove's floor, both facing
        /// out, and never the groove's walls; the Ford's tyres came out bare. Off by default: where
        /// the quads miss the source's shape it shaded what was not there.
        /// </summary>
        public static bool bakeCavity = false;

        /// <summary>
        /// The depth below its surroundings, in mean output edges, at which a texel is darkest. At
        /// 0.08 the gap between a curved source and the flat quads over it (a few tenths of a
        /// millimetre on a head at 19,000 faces) went dark too: specks over a balaclava's cloth,
        /// black lines along its folds and charts, blotches on the Ford's doors; a tread's grooves
        /// lie deeper than this.
        /// </summary>
        public static float cavityDepthEdges = 0.2f;

        /// <summary>
        /// The share of the darkest depth under which a texel is not darkened at all: where two of the
        /// output's faces meet at an angle the source lies a little below their crease, and a panel's
        /// quads drew lines over it.
        /// </summary>
        public static float cavityKnee = 0.55f;

        /// <summary>How dark the deepest texels go (0: none, 1: black).</summary>
        public static float cavityStrength = 0.7f;

        /// <summary>
        /// The source's depth under each texel also bent into the normal map, as a height map's
        /// slopes: a groove's walls, which no texel's line meets, shade in direct light too (an
        /// occlusion map darkens only the ambient light). 0: the source's normals only.
        /// </summary>
        public static float heightNormals = 1f;

        /// <summary>Slopes under this (rise over run) add nothing, and the rest add this much less.</summary>
        public static float heightSlopeKnee = 0.25f;

        /// <summary>The steepest slope the height adds (rise over run): a step's wall is no steeper.</summary>
        public static float heightMaxSlope = 2f;

        /// <summary>The surroundings a texel's depth is measured against: this many mean output edges round it.</summary>
        public static float cavityRadiusEdges = 1f;

        /// <summary>
        /// A texel under the mean round it is darkened by how much of the sky the source round it
        /// hides (its horizon, CavityHorizon), not by how far under it lies: a groove's walls rise
        /// steeply and close, a step's foot sees the sky on one side, and a body line crossed by
        /// flat quads (the Ford's door: a centimetre under them, five wide) rises gently. Off: by
        /// depth under the mean (cavityDepthEdges), which drew those as dark bands.
        /// </summary>
        public static bool cavityHorizon = true;

        /// <summary>Diagnostics: each normal bake's depth per texel, the mean round it, which texels were met, and their sizes.</summary>
        public static Action<float[], float[], bool[], float[]> debugDepth;

        /// <summary>The share of the sky hidden under which a texel is not darkened at all...</summary>
        public static float cavityHorizonKnee = 0.1f;

        /// <summary>...and over which it is darkest.</summary>
        public static float cavityHorizonFull = 0.35f;

        /// <summary>
        /// A rise hides the sky by its walls only: the steps between neighbouring texels steeper
        /// than this (rise over run), which no texel's line meets -- a groove's sides. A slope the
        /// lines meet is in the normal map already: the Ford's body line, crossed by flat quads,
        /// lies a centimetre and a half under them over five, and came out a dark band. 0: every rise.
        /// </summary>
        public static float cavityWallSlope = 1.5f;

        /// <summary>
        /// The height's slopes add only what the source's normal under the texel does not show
        /// already: a groove's walls, which no texel's line meets, but not a step's slope, which
        /// the line met and the normal shades (added again, it drew the Ford's body line twice as
        /// a dark band). Off: the whole slope.
        /// </summary>
        public static bool heightSlopesMissing = true;

        /// <summary>
        /// Where a UV seam runs along a crease sharper than this (degrees between the sides'
        /// normals), a normal-mapped result's normals split there (CornerNormals); 0 keeps every
        /// vertex smooth, as before.
        /// </summary>
        /// <remarks>
        /// Smooth across a box's corners, a vertex normal leans 45 degrees off both faces, and the
        /// map has to bend every flat panel back over its whole width: the fuse box's door baked to
        /// a ramp, its texels cast diagonally into the next face, and in URP the door read as a
        /// lighter patch with a diagonal edge. Split, a flat panel bakes flat. Only at seams: a
        /// split inside a chart would show as a crease the map cannot cover.
        /// </remarks>
        public static float hardEdgeAngle = 40f;

        /// <summary>
        /// Hard edges only on surfaces of a profile with sharp features: none on a Character's or
        /// Photogrammetry's, whose creases at the output's few quads are not the source's. The
        /// head's ear, helmet rim and straps, split there, shaded as blocks and planks where they
        /// were round (since 2026-10-23). Off: on every surface.
        /// </summary>
        public static bool hardEdgesByProfile = true;

        /// <summary>Whether a surface solved under <paramref name="profile"/> gets hard edges (see <see cref="hardEdgesByProfile"/>).</summary>
        public static bool HardEdgesFor(RemeshProfile? profile) =>
            !hardEdgesByProfile || !profile.HasValue
            || profile.Value.kind != RemeshProfileKind.Character && profile.Value.kind != RemeshProfileKind.Photogrammetry;

        /// <summary>
        /// Per face corner, its shading normal: the vertex's own, except at a vertex where the UV
        /// charts meeting there face apart by more than <paramref name="hardAngle"/> -- then each
        /// chart's corners take that chart's normal (angle-weighted over its faces at the vertex).
        /// Given <paramref name="faceHardEdges"/>, never at a vertex of a face it marks false.
        /// </summary>
        public static Vector3[][] CornerNormals(Vector3[] positions, IList<int[]> faces, Vector2[][] faceUvs,
            Vector3[] normals, float hardAngle, bool[] faceHardEdges = null)
        {
            var index = new Dictionary<(int, Vector2), int>();
            var sum = new List<Vector3>();
            var vertexOf = new List<int>();
            var corner = new int[faces.Count][];
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                var uv = faceUvs != null && f < faceUvs.Length ? faceUvs[f] : null;
                if (face == null || face.Length < 3 || uv == null || uv.Length != face.Length) continue;
                var fn = MeshMath.SafeNormalize(QuadExtractor.FaceNormal(positions, face), Vector3.zero);
                corner[f] = new int[face.Length];
                for (var k = 0; k < face.Length; k++)
                {
                    var key = (face[k], uv[k]);
                    if (!index.TryGetValue(key, out var id))
                    {
                        id = sum.Count;
                        index[key] = id;
                        sum.Add(Vector3.zero);
                        vertexOf.Add(face[k]);
                    }
                    corner[f][k] = id;
                    var p = positions[face[k]];
                    var a = positions[face[(k + face.Length - 1) % face.Length]] - p;
                    var b = positions[face[(k + 1) % face.Length]] - p;
                    sum[id] += fn * Vector3.Angle(a, b);
                }
            }
            var group = new Vector3[sum.Count];
            for (var id = 0; id < sum.Count; id++) group[id] = MeshMath.SafeNormalize(sum[id], normals[vertexOf[id]]);
            // A vertex is hard when two of its charts face apart by more than the angle.
            var cos = Mathf.Cos(Mathf.Clamp(hardAngle, 0f, 180f) * Mathf.Deg2Rad);
            var first = new Dictionary<int, List<int>>();
            for (var id = 0; id < group.Length; id++)
            {
                if (!first.TryGetValue(vertexOf[id], out var list)) first[vertexOf[id]] = list = new List<int>(2);
                list.Add(id);
            }
            HashSet<int> soft = null;
            if (faceHardEdges != null)
            {
                soft = new HashSet<int>();
                for (var f = 0; f < faces.Count && f < faceHardEdges.Length; f++)
                    if (!faceHardEdges[f] && faces[f] != null)
                        foreach (var v in faces[f]) soft.Add(v);
            }
            var hard = new HashSet<int>();
            foreach (var pair in first)
            {
                if (soft != null && soft.Contains(pair.Key)) continue;
                var ids = pair.Value;
                for (var i = 0; i < ids.Count && !hard.Contains(pair.Key); i++)
                    for (var j = i + 1; j < ids.Count; j++)
                        if (Vector3.Dot(group[ids[i]], group[ids[j]]) < cos) { hard.Add(pair.Key); break; }
            }
            var result = new Vector3[faces.Count][];
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null) continue;
                result[f] = new Vector3[face.Length];
                for (var k = 0; k < face.Length; k++)
                    result[f][k] = corner[f] != null && hard.Contains(face[k]) ? group[corner[f][k]] : normals[face[k]];
            }
            return result;
        }

        /// <summary>
        /// Per face corner, the tangent (xyz) and handedness (w) the normal map is baked against:
        /// per (vertex, UV) pair -- a Unity vertex -- the UV-gradient tangents of its faces summed,
        /// made perpendicular to its normal. The mesh must carry exactly these (QuadExtractor.ToMesh
        /// writes them), or the shader decodes the map in another frame.
        /// </summary>
        public static Vector4[][] Tangents(Vector3[] positions, IList<int[]> faces, Vector2[][] faceUvs,
            Vector3[] normals, Vector3[][] faceNormals = null)
        {
            var index = new Dictionary<(int, Vector2), int>();
            var tan = new List<Vector3>();
            var bit = new List<Vector3>();
            var nrm = new List<Vector3>();
            var corner = new int[faces.Count][];
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                var uv = faceUvs[f];
                if (face == null || uv == null || uv.Length != face.Length) continue;
                corner[f] = new int[face.Length];
                for (var k = 0; k < face.Length; k++)
                {
                    var key = (face[k], uv[k]);
                    if (!index.TryGetValue(key, out var id))
                    {
                        id = tan.Count;
                        index[key] = id;
                        tan.Add(Vector3.zero);
                        bit.Add(Vector3.zero);
                        nrm.Add(faceNormals?[f] != null && k < faceNormals[f].Length ? faceNormals[f][k] : normals[face[k]]);
                    }
                    corner[f][k] = id;
                }
                for (var i = 1; i + 1 < face.Length; i++)
                {
                    int a = 0, b = i, c = i + 1;
                    Vector3 e1 = positions[face[b]] - positions[face[a]], e2 = positions[face[c]] - positions[face[a]];
                    Vector2 d1 = uv[b] - uv[a], d2 = uv[c] - uv[a];
                    var det = d1.x * d2.y - d2.x * d1.y;
                    if (Mathf.Abs(det) < 1e-20f) continue;
                    var r = 1f / det;
                    var t = (e1 * d2.y - e2 * d1.y) * r;
                    var bt = (e2 * d1.x - e1 * d2.x) * r;
                    // Weighted by the triangle's area, so slivers do not steer a vertex.
                    var w = Vector3.Cross(e1, e2).magnitude;
                    var tl = t.magnitude;
                    var bl = bt.magnitude;
                    if (tl > 0f) t *= w / tl;
                    if (bl > 0f) bt *= w / bl;
                    foreach (var k in new[] { a, b, c })
                    {
                        tan[corner[f][k]] += t;
                        bit[corner[f][k]] += bt;
                    }
                }
            }
            var finished = new Vector4[tan.Count];
            for (var id = 0; id < tan.Count; id++)
            {
                var n = nrm[id];
                var t = tan[id] - n * Vector3.Dot(n, tan[id]);
                if (t.sqrMagnitude < 1e-30f) t = Vector3.Cross(n, Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up);
                // Divided by hand: the sum is area-weighted, and Vector3.Normalize returns ZERO under
                // 1e-5 -- every corner of a quad a few millimetres across on a model a metre tall
                // (fix areas painted on the body), which shaded black.
                t /= Mathf.Sqrt(t.sqrMagnitude);
                var w = Vector3.Dot(Vector3.Cross(n, t), bit[id]) < 0f ? -1f : 1f;
                finished[id] = new Vector4(t.x, t.y, t.z, w);
            }
            var result = new Vector4[faces.Count][];
            for (var f = 0; f < faces.Count; f++)
            {
                if (corner[f] == null) continue;
                result[f] = new Vector4[corner[f].Length];
                for (var k = 0; k < corner[f].Length; k++) result[f][k] = finished[corner[f][k]];
            }
            return result;
        }

        /// <summary>
        /// A tangent-space normal map of the source surface over the output's atlas: every texel
        /// casts along the output's interpolated normal, both ways, reads the source's smooth normal
        /// where the line meets it, and stores it in the frame (tangent, normal x tangent x w,
        /// normal) the shader rebuilds from the interpolated vertex attributes. Unity's convention:
        /// green is +V.
        /// </summary>
        /// <remarks>
        /// The detail too small for the quads -- a helmet's slots and screws, a centimetre across
        /// on cells of two -- comes back as shading: the low poly keeps the shape it can, the map
        /// carries the rest, which is how a game asset is made.
        /// </remarks>
        /// <param name="sourceNormalMaps">
        /// The source's own normal maps (tangent space, Unity's +Y green), read through
        /// <paramref name="sourceUvs"/> where <paramref name="sourceTriangleNormalMap"/> gives a
        /// triangle one: the detail they put on the source's surface is baked in with its shape.
        /// Null: the source's geometry only.
        /// </param>
        public static Color32[] BakeNormals(Vector3[] sourcePositions, int[] sourceTriangles,
            Vector3[] positions, IList<int[]> faces, Vector2[][] faceUvs, Vector3[] normals,
            Vector4[][] faceTangents, int resolution, Stats stats, Action<float> onProgress = null,
            Vector3[][] faceNormals = null, Vector2[] sourceUvs = null, int[] sourceTriangleNormalMap = null,
            Image[] sourceNormalMaps = null, Color32[] cavity = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // Per texel, the source's mean signed distance along the normal (out positive), where met.
            var useHeight = heightNormals > 0f;
            var depth = useHeight || cavity != null && cavity.Length == resolution * resolution ? new float[resolution * resolution] : null;
            var depthMet = depth != null ? new bool[depth.Length] : null;
            // World units per texel, per texel: what a texel's step in depth is a slope over.
            var texelSize = depth != null ? new float[depth.Length] : null;
            var detail = SourceDetail.For(sourcePositions, sourceTriangles, sourceUvs, sourceTriangleNormalMap,
                sourceNormalMaps, stats);
            // Source normals, smooth across its own seams: welded by position.
            var weld = new Dictionary<Vector3, int>(sourcePositions.Length);
            var of = new int[sourcePositions.Length];
            for (var i = 0; i < sourcePositions.Length; i++)
            {
                if (!weld.TryGetValue(sourcePositions[i], out var w)) { w = weld.Count; weld[sourcePositions[i]] = w; }
                of[i] = w;
            }
            var welded = new Vector3[weld.Count];
            for (var t = 0; t + 2 < sourceTriangles.Length; t += 3)
            {
                int a = sourceTriangles[t], b = sourceTriangles[t + 1], c = sourceTriangles[t + 2];
                var fn = Vector3.Cross(sourcePositions[b] - sourcePositions[a], sourcePositions[c] - sourcePositions[a]);
                welded[of[a]] += fn; welded[of[b]] += fn; welded[of[c]] += fn;
            }
            for (var i = 0; i < welded.Length; i++) welded[i] = MeshMath.SafeNormalize(welded[i], Vector3.up);
            // Per triangle corner: smooth only over the faces round the vertex within hardEdgeAngle of
            // this one, so a source's hard edges stay hard (welded by position, every box corner of
            // the source baked as a rounded band). 0: the welded normals, as before.
            var corners = hardEdgeAngle > 0f ? SourceCornerNormals(sourcePositions, sourceTriangles, of, weld.Count, hardEdgeAngle) : null;
            Vector3 At(int tri, int k, int vertex) => corners != null ? corners[3 * tri + k] : welded[of[vertex]];

            double edgeSum = 0;
            var edgeCount = 0;
            foreach (var face in faces)
                if (face != null)
                    for (var k = 0; k < face.Length; k++)
                    {
                        edgeSum += (positions[face[(k + 1) % face.Length]] - positions[face[k]]).magnitude;
                        edgeCount++;
                    }
            var reach = normalReachEdges * (float)(edgeSum / Math.Max(1, edgeCount));

            var grid = new SpatialGrid(sourcePositions, sourceTriangles);
            // Which way the source faces against the output, by majority: a source wound the other
            // way round would bake every texel upside down.
            var agree = 0;
            var probes = 0;
            for (var f = 0; f < faces.Count && probes < 2000; f += Math.Max(1, faces.Count / 2000))
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;
                var c = Vector3.zero;
                var n = Vector3.zero;
                foreach (var v in face) { c += positions[v]; n += normals[v]; }
                c /= face.Length;
                n = MeshMath.SafeNormalize(n, Vector3.up);
                grid.NearestDistance(c, out var tri);
                if (tri < 0) continue;
                grid.TriangleCorners(tri, out var ia, out var ib, out var ic);
                var sn = welded[of[ia]] + welded[of[ib]] + welded[of[ic]];
                agree += Vector3.Dot(sn, n) >= 0f ? 1 : -1;
                probes++;
            }
            var orientation = agree < 0 ? -1f : 1f;

            var pixels = new Color32[resolution * resolution];
            var written = new bool[resolution * resolution];
            var missed = 0;
            var done = 0;
            var ss = Math.Max(1, supersample);

            Vector3 SourceNormal(Vector3 p, Vector3 n, out float along, out bool met)
            {
                along = 0f;
                met = false;
                if (grid.Raycast(p, n, reach, orientation, out var hit, out var tri, out var u, out var v))
                {
                    along = hit;
                    met = true;
                    grid.TriangleCorners(tri, out var ia, out var ib, out var ic);
                    var sn = orientation * (At(tri, 0, ia) * (1f - u - v) + At(tri, 1, ib) * u + At(tri, 2, ic) * v);
                    if (Vector3.Dot(sn, n) > 0f)
                    {
                        sn = sn.normalized;
                        return detail != null ? detail.Apply(sn, n, tri, ia, ib, ic, u, v) : sn;
                    }
                }
                System.Threading.Interlocked.Increment(ref missed);
                grid.NearestDistance(p, out var near);
                if (near < 0) return n;
                grid.TriangleCorners(near, out var ja, out var jb, out var jc);
                var fallback = orientation * (At(near, 0, ja) + At(near, 1, jb) + At(near, 2, jc)).normalized;
                return Vector3.Dot(fallback, n) > 0f ? fallback : n;
            }

            Parallel.For(0, faces.Count, BackgroundScheduler.Options, f =>
            {
                var face = faces[f];
                var uv = faceUvs[f];
                var tg = faceTangents[f];
                if (face == null || uv == null || tg == null || face.Length < 3 || uv.Length != face.Length) return;
                var cn = faceNormals?[f] != null && faceNormals[f].Length == face.Length ? faceNormals[f] : null;
                Vector3 N(int k) => cn != null ? cn[k] : normals[face[k]];
                for (var i = 1; i + 1 < face.Length; i++)
                {
                    Vector2 u0 = uv[0] * resolution, u1 = uv[i] * resolution, u2 = uv[i + 1] * resolution;
                    int c0 = 0, c1 = i, c2 = i + 1;
                    var area = (u1.x - u0.x) * (u2.y - u0.y) - (u1.y - u0.y) * (u2.x - u0.x);
                    if (Mathf.Abs(area) < 1e-12f) continue;
                    const float k_Reach = 0.75f;
                    int x0 = Math.Max(0, Mathf.FloorToInt(Mathf.Min(u0.x, Mathf.Min(u1.x, u2.x)) - k_Reach));
                    int x1 = Math.Min(resolution - 1, Mathf.CeilToInt(Mathf.Max(u0.x, Mathf.Max(u1.x, u2.x)) + k_Reach));
                    int y0 = Math.Max(0, Mathf.FloorToInt(Mathf.Min(u0.y, Mathf.Min(u1.y, u2.y)) - k_Reach));
                    int y1 = Math.Min(resolution - 1, Mathf.CeilToInt(Mathf.Max(u0.y, Mathf.Max(u1.y, u2.y)) + k_Reach));
                    float L01 = (u1 - u0).magnitude, L12 = (u2 - u1).magnitude, L20 = (u0 - u2).magnitude;
                    var sign = Mathf.Sign(area);
                    for (var y = y0; y <= y1; y++)
                        for (var x = x0; x <= x1; x++)
                        {
                            var cc = new Vector2(x + 0.5f, y + 0.5f);
                            float d0 = sign * Cross(u1 - u0, cc - u0) / L01;
                            float d1 = sign * Cross(u2 - u1, cc - u1) / L12;
                            float d2 = sign * Cross(u0 - u2, cc - u2) / L20;
                            if (d0 < -k_Reach || d1 < -k_Reach || d2 < -k_Reach) continue;
                            var sum = Vector3.zero;
                            var alongSum = 0f;
                            var perTexel = texelSize != null
                                ? Mathf.Sqrt(Vector3.Cross(positions[face[c1]] - positions[face[c0]], positions[face[c2]] - positions[face[c0]]).magnitude / Mathf.Abs(area))
                                : 0f;
                            var metCount = 0;
                            for (var sy = 0; sy < ss; sy++)
                                for (var sx = 0; sx < ss; sx++)
                                {
                                    var s = new Vector2(x + (sx + 0.5f) / ss, y + (sy + 0.5f) / ss);
                                    var w1 = sign * Cross(u0 - u2, s - u2) / Mathf.Abs(area);
                                    var w2 = sign * Cross(u1 - u0, s - u0) / Mathf.Abs(area);
                                    w1 = Mathf.Clamp01(w1); w2 = Mathf.Clamp01(w2);
                                    if (w1 + w2 > 1f) { var k = 1f / (w1 + w2); w1 *= k; w2 *= k; }
                                    var w0 = 1f - w1 - w2;
                                    var p = positions[face[c0]] * w0 + positions[face[c1]] * w1 + positions[face[c2]] * w2;
                                    // As the shader: interpolated, then normalised.
                                    var n = MeshMath.SafeNormalize(N(c0) * w0 + N(c1) * w1 + N(c2) * w2, Vector3.up);
                                    var t4 = tg[c0] * w0 + tg[c1] * w1 + tg[c2] * w2;
                                    var t = new Vector3(t4.x, t4.y, t4.z);
                                    t = MeshMath.SafeNormalize(t - n * Vector3.Dot(n, t), Vector3.right);
                                    var b = Vector3.Cross(n, t) * (tg[c0].w < 0f ? -1f : 1f);
                                    var sn = SourceNormal(p, n, out var along, out var met);
                                    if (met) { alongSum += along; metCount++; }
                                    sum += new Vector3(Vector3.Dot(sn, t), Vector3.Dot(sn, b), Vector3.Dot(sn, n));
                                }
                            var m = MeshMath.SafeNormalize(sum, Vector3.forward);
                            if (m.z < 0.05f) { m.z = 0.05f; m.Normalize(); }
                            var idx = y * resolution + x;
                            pixels[idx] = new Color32((byte)Mathf.Clamp(Mathf.RoundToInt((m.x * 0.5f + 0.5f) * 255f), 0, 255),
                                (byte)Mathf.Clamp(Mathf.RoundToInt((m.y * 0.5f + 0.5f) * 255f), 0, 255),
                                (byte)Mathf.Clamp(Mathf.RoundToInt((m.z * 0.5f + 0.5f) * 255f), 0, 255), 255);
                            written[idx] = true;
                            if (depth != null && metCount > 0)
                            {
                                depth[idx] = alongSum / metCount;
                                depthMet[idx] = true;
                                if (texelSize != null) texelSize[idx] = perTexel;
                            }
                        }
                }
                var dn = System.Threading.Interlocked.Increment(ref done);
                if (onProgress != null && (dn & 1023) == 0) onProgress((float)dn / faces.Count);
            });
            // The depth's detail: under each texel, the depth less the mean of the texels round it.
            // The shape the quads miss at a larger scale (a panel's curve between two loops) is in the
            // source's normals already; added again as slopes, the Ford's body came out in waves.
            var mean = depth != null
                ? LocalMean(depth, depthMet, texelSize, cavityRadiusEdges * (float)(edgeSum / Math.Max(1, edgeCount)), resolution)
                : null;
            if (depth != null) debugDepth?.Invoke(depth, mean, depthMet, texelSize);
            if (useHeight)
                HeightSlopes(pixels, depth, mean, depthMet, texelSize, resolution, 0.25f * (float)(edgeSum / Math.Max(1, edgeCount)));
            if (cavity != null && cavity.Length == resolution * resolution)
                Cavity(depth, mean, depthMet, (bool[])written.Clone(), resolution,
                    cavityDepthEdges * (float)(edgeSum / Math.Max(1, edgeCount)), cavity,
                    texelSize, cavityRadiusEdges * (float)(edgeSum / Math.Max(1, edgeCount)));
            // Gutters spread from the charts, as the colour bake's; with that off, flat.
            if (fillGutters) FillOutward(pixels, written, resolution);
            for (var i = 0; i < pixels.Length; i++)
                if (!written[i] && pixels[i].a == 0) pixels[i] = new Color32(128, 128, 255, 255);
            stats.normalMissed = missed;
            stats.normalDetail = detail?.applied ?? 0;
            stats.normalMs = watch.ElapsedMilliseconds;
            stats.normalReach = reach;
            stats.normalFlipped = orientation < 0f;
            return pixels;
        }

        // The depth's slopes (central differences over the texels met; a step of more than `jump`,
        // another surface met, is no slope) added to each texel's normal in tangent space: x along
        // U, y along V, as the tangents are made.
        static void HeightSlopes(Color32[] pixels, float[] depth, float[] mean, bool[] met, float[] texelSize, int resolution, float jump)
        {
            var n = resolution;
            var bent = new Color32[pixels.Length];
            Array.Copy(pixels, bent, pixels.Length);
            Parallel.For(0, n, BackgroundScheduler.Options, y =>
            {
                for (var x = 0; x < n; x++)
                {
                    var i = y * n + x;
                    if (!met[i] || !(texelSize[i] > 0f)) continue;
                    float D(int k) => depth[k] - mean[k];
                    float Slope(int a, int b, float span)
                    {
                        var ok0 = a >= 0 && met[a] && Mathf.Abs(depth[a] - depth[i]) < jump;
                        var ok1 = b >= 0 && met[b] && Mathf.Abs(depth[b] - depth[i]) < jump;
                        if (ok0 && ok1) return (D(b) - D(a)) / (2f * span);
                        if (ok1) return (D(b) - D(i)) / span;
                        if (ok0) return (D(i) - D(a)) / span;
                        return 0f;
                    }
                    var s = texelSize[i];
                    var gx = Mathf.Clamp(Slope(x > 0 ? i - 1 : -1, x + 1 < n ? i + 1 : -1, s), -heightMaxSlope, heightMaxSlope);
                    var gy = Mathf.Clamp(Slope(y > 0 ? i - n : -1, y + 1 < n ? i + n : -1, s), -heightMaxSlope, heightMaxSlope);
                    // Gentle slopes left out: they are the output's own faces meeting at an angle over
                    // a smooth source, which drew bands along a panel's quads.
                    gx = Mathf.Sign(gx) * Mathf.Max(0f, Mathf.Abs(gx) - heightSlopeKnee);
                    gy = Mathf.Sign(gy) * Mathf.Max(0f, Mathf.Abs(gy) - heightSlopeKnee);
                    if (gx == 0f && gy == 0f) continue;
                    var c = pixels[i];
                    var m = new Vector3(c.r / 127.5f - 1f, c.g / 127.5f - 1f, c.b / 127.5f - 1f);
                    if (heightSlopesMissing && m.z > 0.05f)
                    {
                        // The slope the baked normal shows already (a height field's normal is
                        // (-gx, -gy, 1)): only the rest added, and none against it.
                        float Missing(float g, float shown) => g * shown < 0f ? 0f : Mathf.Sign(g) * Mathf.Max(0f, Mathf.Abs(g) - Mathf.Abs(shown));
                        gx = Missing(gx, -m.x / m.z);
                        gy = Missing(gy, -m.y / m.z);
                        if (gx == 0f && gy == 0f) continue;
                    }
                    // Whiteout blend: the slopes' normal (-gx, -gy, 1) over the baked one.
                    m = new Vector3(m.x - heightNormals * gx * m.z, m.y - heightNormals * gy * m.z, m.z);
                    m = MeshMath.SafeNormalize(m, Vector3.forward);
                    if (m.z < 0.05f) { m.z = 0.05f; m.Normalize(); }
                    bent[i] = new Color32((byte)Mathf.Clamp(Mathf.RoundToInt((m.x * 0.5f + 0.5f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((m.y * 0.5f + 0.5f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((m.z * 0.5f + 0.5f) * 255f), 0, 255), 255);
                }
            });
            Array.Copy(bent, pixels, pixels.Length);
        }

        // How far each texel's source lies below the mean of the texels round it (those met, within
        // cavityRadiusEdges), as grey: white level or proud, darkest at `full` below. With
        // cavityHorizon, by the sky hidden instead (CavityHorizon). Gutters filled.
        static void Cavity(float[] depth, float[] mean, bool[] met, bool[] written, int resolution, float full, Color32[] output,
            float[] texelSize = null, float radius = 0f)
        {
            var horizon = cavityHorizon && texelSize != null && radius > 0f;
            Parallel.For(0, output.Length, BackgroundScheduler.Options, i =>
            {
                if (!met[i]) { output[i] = written[i] ? new Color32(255, 255, 255, 255) : default; return; }
                var under = mean[i] - depth[i];
                float below;
                if (horizon)
                    below = under > 0f
                        ? Mathf.Clamp01((CavityHorizon(depth, met, texelSize, radius, full, resolution, i) - cavityHorizonKnee)
                            / Mathf.Max(1e-4f, cavityHorizonFull - cavityHorizonKnee))
                        : 0f;
                else
                    below = full > 0f ? Mathf.Clamp01((under / full - cavityKnee) / (1f - cavityKnee)) : 0f;
                var g = (byte)Mathf.RoundToInt(255f * (1f - cavityStrength * below));
                output[i] = new Color32(g, g, g, 255);
            });
            if (fillGutters) FillOutward(output, written, resolution);
            for (var i = 0; i < output.Length; i++)
                if (output[i].a == 0) output[i] = new Color32(255, 255, 255, 255);
        }

        // The share of the sky texel i's source does not see over the source round it, within
        // `radius`: along eight directions in the atlas the steepest rise to a texel met (every
        // texel on the way met too: one chart's, not a neighbour's), as the sine of its angle,
        // averaged. 0 on a level or a step's top, half in a narrow well. A rise counts by its
        // height up to `tall`: an emblem's letters, a millimetre or two, rise steeply but hide
        // little, and their gaps (below a texel's reach) had greyed the Ford's door in patches.
        static float CavityHorizon(float[] depth, bool[] met, float[] texelSize, float radius, float tall, int n, int i)
        {
            int x = i % n, y = i / n;
            var size = texelSize[i];
            if (!(size > 0f)) return 0f;
            var r = Mathf.Clamp(Mathf.RoundToInt(radius / size), 1, 64);
            var sum = 0f;
            for (var dir = 0; dir < 8; dir++)
            {
                var dx = dir < 2 ? (dir == 0 ? 1 : -1) : dir < 4 ? 0 : (dir == 4 || dir == 6 ? 1 : -1);
                var dy = dir < 2 ? 0 : dir < 4 ? (dir == 2 ? 1 : -1) : (dir < 6 ? 1 : -1);
                var step = Mathf.Sqrt(dx * dx + dy * dy) * size;
                var hidden = 0f;
                var walls = 0f;
                var previous = depth[i];
                for (var k = 1; k <= r; k++)
                {
                    int px = x + dx * k, py = y + dy * k;
                    if (px < 0 || py < 0 || px >= n || py >= n || !met[py * n + px]) break;
                    var here = depth[py * n + px];
                    if (cavityWallSlope <= 0f || here - previous > cavityWallSlope * step) walls += Mathf.Max(0f, here - previous);
                    previous = here;
                    var rise = here - depth[i];
                    if (rise <= 0f) continue;
                    var slope = rise / (k * step);
                    // Counted by the height of the walls on the way, not of a slope met.
                    var high = cavityWallSlope > 0f ? Mathf.Min(rise, walls) : rise;
                    hidden = Mathf.Max(hidden, slope / Mathf.Sqrt(1f + slope * slope) * (tall > 0f ? Mathf.Min(1f, high / tall) : 1f));
                }
                sum += hidden;
            }
            return sum / 8f;
        }

        // The mean depth of the texels met within `radius` of each texel: box sums along rows,
        // then columns, each only over the unbroken run of texels met it lies in -- a chart's,
        // not its neighbours' in the atlas, which (another part's depths) blotched the Ford's panels.
        static float[] LocalMean(float[] depth, bool[] met, float[] texelSize, float radius, int resolution)
        {
            var n = resolution;
            // Each texel's radius in texels: `radius` in world units over its texel's size.
            int R(int i) => texelSize[i] > 0f ? Mathf.Clamp(Mathf.RoundToInt(radius / texelSize[i]), 1, 128) : 1;
            var rowSum = new float[n * n];
            var rowCount = new int[n * n];
            Parallel.For(0, n, BackgroundScheduler.Options, y =>
            {
                var prefix = new double[n + 1];
                for (var x = 0; x < n;)
                {
                    if (!met[y * n + x]) { x++; continue; }
                    var start = x;
                    while (x < n && met[y * n + x]) { prefix[x + 1 - start] = prefix[x - start] + depth[y * n + x]; x++; }
                    for (var k = start; k < x; k++)
                    {
                        var r = R(y * n + k);
                        int lo = Math.Max(start, k - r), hi = Math.Min(x - 1, k + r);
                        rowSum[y * n + k] = (float)(prefix[hi + 1 - start] - prefix[lo - start]);
                        rowCount[y * n + k] = hi - lo + 1;
                    }
                }
            });
            var mean = new float[n * n];
            Parallel.For(0, n, BackgroundScheduler.Options, x =>
            {
                var sums = new double[n + 1];
                var counts = new long[n + 1];
                for (var y = 0; y < n;)
                {
                    if (!met[y * n + x]) { y++; continue; }
                    var start = y;
                    while (y < n && met[y * n + x])
                    {
                        sums[y + 1 - start] = sums[y - start] + rowSum[y * n + x];
                        counts[y + 1 - start] = counts[y - start] + rowCount[y * n + x];
                        y++;
                    }
                    for (var k = start; k < y; k++)
                    {
                        var r = R(k * n + x);
                        int lo = Math.Max(start, k - r), hi = Math.Min(y - 1, k + r);
                        var c = counts[hi + 1 - start] - counts[lo - start];
                        mean[k * n + x] = c > 0 ? (float)((sums[hi + 1 - start] - sums[lo - start]) / c) : depth[k * n + x];
                    }
                }
            });
            return mean;
        }

        /// <summary>
        /// Per corner of each source triangle, the area-weighted normal of the faces round its
        /// (welded) vertex that turn from this triangle by at most <paramref name="angle"/>.
        /// </summary>
        static Vector3[] SourceCornerNormals(Vector3[] positions, int[] triangles, int[] weldOf, int weldCount, float angle)
        {
            var count = triangles.Length / 3;
            var faceNormal = new Vector3[count];
            for (var t = 0; t < count; t++)
                faceNormal[t] = Vector3.Cross(positions[triangles[3 * t + 1]] - positions[triangles[3 * t]],
                    positions[triangles[3 * t + 2]] - positions[triangles[3 * t]]);
            // Triangles round each welded vertex, as a CSR list.
            var start = new int[weldCount + 1];
            foreach (var v in triangles) start[weldOf[v] + 1]++;
            for (var i = 0; i < weldCount; i++) start[i + 1] += start[i];
            var fill = (int[])start.Clone();
            var around = new int[triangles.Length];
            for (var c = 0; c < triangles.Length; c++) around[fill[weldOf[triangles[c]]]++] = c / 3;
            var cos = Mathf.Cos(angle * Mathf.Deg2Rad);
            var result = new Vector3[triangles.Length];
            Parallel.For(0, count, BackgroundScheduler.Options, t =>
            {
                var own = MeshMath.SafeNormalize(faceNormal[t], Vector3.zero);
                for (var k = 0; k < 3; k++)
                {
                    var w = weldOf[triangles[3 * t + k]];
                    var sum = Vector3.zero;
                    for (var i = start[w]; i < start[w + 1]; i++)
                    {
                        var other = around[i];
                        var n = faceNormal[other];
                        var m = n.magnitude;
                        if (m > 0f && Vector3.Dot(own, n) >= cos * m) sum += n;
                    }
                    result[3 * t + k] = MeshMath.SafeNormalize(sum, own);
                }
            });
            return result;
        }

        /// <summary>
        /// The source's normal maps, ready to read at a point of a source triangle: per triangle its
        /// map and the tangent frame its UVs give it, and the mip level matching how many of the
        /// map's texels fall on one output texel.
        /// </summary>
        sealed class SourceDetail
        {
            Mips[] maps;
            int[] triangleMap;
            Vector2[] uvs;
            Vector4[] tangent;   // per source triangle: xyz tangent, w handedness
            float level;
            public int applied;

            public static SourceDetail For(Vector3[] positions, int[] triangles, Vector2[] uvs, int[] triangleMap,
                Image[] images, Stats stats)
            {
                if (images == null || images.Length == 0 || uvs == null || uvs.Length != positions.Length
                    || triangleMap == null || triangleMap.Length < triangles.Length / 3)
                    return null;
                var d = new SourceDetail { triangleMap = triangleMap, uvs = uvs, maps = new Mips[images.Length] };
                for (var i = 0; i < images.Length; i++)
                    if (images[i] != null) d.maps[i] = new Mips(images[i]);
                var count = triangles.Length / 3;
                d.tangent = new Vector4[count];
                double uvArea = 0, worldArea = 0;
                for (var t = 0; t < count; t++)
                {
                    var map = triangleMap[t];
                    if (map < 0 || map >= images.Length || images[map] == null) continue;
                    int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
                    Vector3 e1 = positions[b] - positions[a], e2 = positions[c] - positions[a];
                    Vector2 d1 = uvs[b] - uvs[a], d2 = uvs[c] - uvs[a];
                    var det = d1.x * d2.y - d2.x * d1.y;
                    var fn = Vector3.Cross(e1, e2);
                    worldArea += fn.magnitude * 0.5;
                    uvArea += Mathf.Abs(det) * 0.5 * images[map].width * images[map].height;
                    if (Mathf.Abs(det) < 1e-20f) continue;
                    var r = 1f / det;
                    var tan = (e1 * d2.y - e2 * d1.y) * r;
                    var bit = (e2 * d1.x - e1 * d2.x) * r;
                    var w = Vector3.Dot(Vector3.Cross(fn, tan), bit) < 0f ? -1f : 1f;
                    var tl = tan.magnitude;
                    if (tl > 0f) d.tangent[t] = new Vector4(tan.x / tl, tan.y / tl, tan.z / tl, w);
                }
                var outputDensity = (double)stats.texelsPerUnit * stats.texelsPerUnit * supersample * supersample;
                var sourceDensity = worldArea > 0 ? uvArea / worldArea : 0;
                d.level = outputDensity > 0 && sourceDensity > 0
                    ? Mathf.Max(0f, 0.5f * Mathf.Log((float)(sourceDensity / outputDensity), 2f))
                    : 0f;
                return d;
            }

            /// <summary>The smooth source normal <paramref name="sn"/> at a point of a triangle, bent by its normal map.</summary>
            public Vector3 Apply(Vector3 sn, Vector3 facing, int tri, int ia, int ib, int ic, float u, float v)
            {
                if (tri < 0 || tri >= tangent.Length) return sn;
                var map = triangleMap[tri];
                if (map < 0 || map >= maps.Length || maps[map] == null) return sn;
                var t4 = tangent[tri];
                if (t4.w == 0f) return sn;
                var uv = uvs[ia] * (1f - u - v) + uvs[ib] * u + uvs[ic] * v;
                var c = maps[map].Sample(uv, level);
                var tn = new Vector3(c.x / 127.5f - 1f, c.y / 127.5f - 1f, c.z / 127.5f - 1f);
                if (tn.sqrMagnitude < 1e-6f) return sn;
                tn.Normalize();
                var t = new Vector3(t4.x, t4.y, t4.z);
                t -= sn * Vector3.Dot(sn, t);
                if (t.sqrMagnitude < 1e-12f) return sn;
                t.Normalize();
                var b = Vector3.Cross(sn, t) * t4.w;
                var bent = (t * tn.x + b * tn.y + sn * tn.z).normalized;
                if (Vector3.Dot(bent, facing) <= 0f) return sn;
                System.Threading.Interlocked.Increment(ref applied);
                return bent;
            }
        }

        // Empty texels take the mean of their written neighbours, ring by ring, until none is
        // left: the charts' colours run out into the gutters, so bilinear filtering and the mip
        // chain read chart colour at a chart's edge, not the background.
        static int FillOutward(Color32[] pixels, bool[] written, int resolution)
        {
            var filled = 0;
            var next = new List<int>();
            var colours = new List<Color32>();
            var frontier = new List<int>();
            for (var i = 0; i < written.Length; i++)
                if (!written[i] && HasWrittenNeighbour(written, i, resolution)) frontier.Add(i);
            var queued = new bool[written.Length];
            foreach (var i in frontier) queued[i] = true;
            while (frontier.Count > 0)
            {
                colours.Clear();
                foreach (var i in frontier)
                {
                    int x = i % resolution, y = i / resolution, r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= resolution || ny >= resolution) continue;
                            var j = ny * resolution + nx;
                            if (!written[j]) continue;
                            var c = pixels[j];
                            r += c.r; g += c.g; b += c.b; a += c.a; n++;
                        }
                    colours.Add(n > 0 ? new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n)) : default);
                }
                next.Clear();
                for (var k = 0; k < frontier.Count; k++)
                {
                    var i = frontier[k];
                    pixels[i] = colours[k];
                    written[i] = true;
                    filled++;
                }
                foreach (var i in frontier)
                {
                    int x = i % resolution, y = i / resolution;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= resolution || ny >= resolution) continue;
                            var j = ny * resolution + nx;
                            if (written[j] || queued[j]) continue;
                            queued[j] = true;
                            next.Add(j);
                        }
                }
                frontier.Clear();
                frontier.AddRange(next);
            }
            return filled;
        }

        static bool HasWrittenNeighbour(bool[] written, int i, int resolution)
        {
            int x = i % resolution, y = i / resolution;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= resolution || ny >= resolution) continue;
                    if (written[ny * resolution + nx]) return true;
                }
            return false;
        }

        // As AttributeTransfer's: solved in the triangle's plane, clamped onto it.
        static void Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float wa, out float wb, out float wc)
        {
            Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            var denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) <= 1e-20f) { wa = 1f; wb = 0f; wc = 0f; return; }
            wb = Mathf.Clamp01((d11 * d20 - d01 * d21) / denom);
            wc = Mathf.Clamp01((d00 * d21 - d01 * d20) / denom);
            if (wb + wc > 1f) { var s = 1f / (wb + wc); wb *= s; wc *= s; }
            wa = 1f - wb - wc;
        }
    }
}
