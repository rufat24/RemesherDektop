// Quads for buildings: flat walls as grids whose lines run through the corners of their windows,
// the detail between them (frames, sills, mullions, trims) as error-driven quads.
//
// The error-driven quads (DetailQuads) keep a house's windows where the lattice melts them, but a
// simplification spends nothing on a flat wall and leaves it as fans of slivers round its window
// corners; split into quads, the wall is a tangle. An artist models the wall the other way round:
// a few long loops, one through each window edge, and the window's frame and bars inside them.
//
// So:
//   1. the source's planar regions are found and flattened (PlanarRegions);
//   2. the flattened source is simplified with the regions' borders kept as edges, and its
//      borders straightened (Straighten): strips a sliver wide closed up, points on a border that
//      runs on straight past them taken out;
//   3. each planar region of it is filled again: lines along the wall's two directions through
//      every corner of its outline (a window's edges, the wall's ends), more lines between where
//      they are far apart, their crossings inside the outline triangulated with the outline
//      (ConstrainedDelaunay) -- rectangles, cut in two, wherever the lines run -- and the outline
//      itself cut where the lines meet it, the same cuts on both sides of an edge shared. A
//      region's lines run on into each neighbour whose lines run the same way (SharedLines), and
//      what is off every region is filled the same way in flat patches;
//   4. everything paired into quads, the triangles left paired across chains of quads, and those
//      still left joined in twos along the quads between them (Bridge); or, with `split`, split
//      once, Catmull-Clark's topology: all quads, at about three times the faces.
//
// A window, a door, a panel -- a patch of a wall, seen square on, that is not the wall -- is not
// filled so when its steps are flat: it is built as a stepped relief (SteppedRelief), stamped into
// the simplified wall (the triangles over it go, the gap round it triangulated again).
//
// A wall of lapped boards, each a little turned (a gable's siding at a fine cell), is one wall:
// the regions in its plane or a little before it are its own, both when its windows are found
// and when they are stamped in (sameWall, sameWallStamp). Reliefs cost more quads than the wall
// they take, so the large walls' lines are spaced out until the whole comes to the target
// (reliefBudget).
//
// One of the pipeline's tries (RemeshPipeline.planarQuadsTry), kept where it measures no worse.
// White house, measured both ways against the source (p90 / p99 / mean turn): at 8,352 quads
// asked (UI 3500), 8,618 faces at 0.000931 / 0.00288 / 10.9 deg against the error-driven quads'
// 8,549 at 0.0011 / 0.00414 / 12.3; at 11,818 (UI 5000), 11,865 at 0.000774 / 0.0030 / 10.6
// against 11,851 at 0.000805 / 0.00295 / 11.5 (the pipeline's own p99, on fewer samples, a tie).
// Its chimney's crown is still the worse.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    public static class PlanarQuads
    {
        /// <summary>Build dense buildings (planar regions over PlanarRegions.minShare of the area) this way when error-driven quads are tried.</summary>
        public static bool enabled = true;

        /// <summary>Most distance between a wall's lines, in cells (before the split, if any: it halves it).</summary>
        public static float spacingCells = 3f;

        /// <summary>Lines closer than this share of the spacing are one.</summary>
        public static float lineMergeShare = 0.35f;

        /// <summary>A grid point nearer the outline than this share of its lines' spacing is left out.</summary>
        public static float clearanceShare = 0.35f;

        /// <summary>An outline edge within this angle of one of the wall's directions is cut at the other direction's lines.</summary>
        public static float alignedDegrees = 10f;

        /// <summary>A simplified triangle belongs to its region's plane only when it faces within this angle of it, degrees.</summary>
        public static float facingDegrees = 30f;

        /// <summary>Simplified triangles per wanted quad, off the planar regions...</summary>
        public static float freeTrianglesPerQuad = 0.5f;

        /// <summary>...and on them (they are filled again; these only hold their outlines).</summary>
        public static float flatTrianglesPerQuad = 0.05f;

        /// <summary>Diagnostics: given each region the fill gave up on (points x, y; outline segments; why: 1 triangulation, 2 outline).</summary>
        public static Action<List<double>, List<double>, List<(int, int)>, int> debugFailed;


        /// <summary>Diagnostics: each wall's raster (its normal, size, and per sample 0 its own region, 1 its background, 2 not-wall in no patch, 3 + the patch), and its corners (u0 v0, u1 v0, u0 v1).</summary>
        public static Action<Vector3, int, int, Func<int, int, int>, Vector3[]> debugRaster;

        /// <summary>Diagnostics: each hole's outline not built as a relief, and why.</summary>
        public static Action<List<Vector3>, string> debugHole;

        /// <summary>Diagnostics: the triangles before the split or pairing (false) and those the pairing left (true), each one's source (component, -1 off every region, -2 a region left as simplified, -3 made by the pairing), and each component's region.</summary>
        public static Action<Vector3[], int[], int[], int[], bool> debugOrigin;

        /// <summary>Diagnostics: the simplified mesh and each triangle's label (0 off every region) at a stage ("source", "simplified", "facing", "straightened"), and the faces fanned (labels 0) after the fill's later steps ("paired", "bridged", "placed", "fit").</summary>
        public static Action<Vector3[], int[], int[], string> debugProxy;

        /// <summary>
        /// A quad not flat is drawn as two triangles cut along its first and third corners: each
        /// such quad's corners turned so that cut is the diagonal lying nearer the source.
        /// </summary>
        public static bool nearerDiagonal = true;

        /// <summary>
        /// Small regions (under smallRegionCells square cells) keep their simplified edges within
        /// this many cells, so a thin part's faces cannot reach across it (a chimney's rim and
        /// flue walls, half a cell thick: simplified freely, triangles were laid over the flue
        /// from rim to rim and its crown cut across). 0: off.
        /// </summary>
        public static float smallRegionEdgeCells = 1.5f;
        public static float smallRegionCells = 16f;

        /// <summary>...and the points off every region too (a crown's mouldings, a sill's nose).</summary>
        public static bool smallRegionFree = true;

        /// <summary>The simplification's sliver guard (PreDecimate.sliverGuardQuality) here.</summary>
        public static float sliverGuard = 0f;

        /// <summary>
        /// Paired faces split once into quads (the lines then at half the spacing); off, the
        /// triangles left over are paired across chains of quads instead and the few that stay,
        /// stay -- about a third the faces for the same lines.
        /// </summary>
        public static bool split = false;

        /// <summary>
        /// Lines shared along borders: a region's lines run on into each neighbour whose lines run
        /// the same way (false: each region has its own, and is cut wherever its neighbours' meet it).
        /// </summary>
        public static bool sharedLines = true;

        /// <summary>Two regions' lines are one set when their directions are within this angle, degrees.</summary>
        public static float sharedDegrees = 1f;

        /// <summary>
        /// The simplified borders between regions straightened: a point on one is taken out where
        /// the border without it stays within this many cells of where it ran (and so is a
        /// planar region's point inside it). 0: left as simplified.
        /// </summary>
        public static float straightenCells = 0.25f;

        /// <summary>The simplified triangles off every region filled as flat patches (false: each fanned from its middle where cut).</summary>
        public static bool freePatches = true;

        /// <summary>A patch takes in triangles facing within this angle of its first, degrees...</summary>
        public static float patchDegrees = 12f;

        /// <summary>...with their corners within this many cells of its plane.</summary>
        public static float patchFlatCells = 0.1f;

        /// <summary>Simplified edges shorter than this many cells are closed up first, across any border (a strip a sliver wide is a line). 0: none.</summary>
        public static float thinCells = 0.3f;

        /// <summary>...when no triangle the closing changes turns by more than this many degrees.</summary>
        public static float thinDegrees = 20f;

        /// <summary>
        /// Unsplit, the triangles the pairing left joined in twos along the ring of quads between
        /// them (cut down its middle), so none stays.
        /// </summary>
        public static bool bridges = true;

        /// <summary>The most quads a bridge crosses.</summary>
        public static int bridgeQuads = 32;

        /// <summary>Holes in walls whose contents are flat steps (windows, doors, panels) built as stepped reliefs.</summary>
        public static bool reliefs = true;

        /// <summary>A relief is sampled this many cells, four times over, round its patch.</summary>
        public static float reliefSnapCells = 0.15f;

        /// <summary>Walls of at least this many square cells are searched for reliefs.</summary>
        public static float reliefWallCells = 16f;

        /// <summary>A wall's reliefs are looked for this many cells either side of it.</summary>
        public static float reliefDepthCells = 3f;

        /// <summary>The most samples a wall's raster has (coarser past it).</summary>
        public static int maxRasterSamples = 16 << 20;

        /// <summary>A parallel region standing off a wall is its background (not a relief's) from this share of its area.</summary>
        public static float backgroundShare = 0.25f;

        /// <summary>
        /// A wall's background is told by where other regions stand off it in their middle (not by
        /// their planes' offsets), and those in its plane count as its own: a wall of boards each
        /// a little turned is one wall. Off: plane offsets, and only its own region.
        /// </summary>
        public static bool sameWall = true;

        /// <summary>...those standing at most this many cells before it.</summary>
        public static float sameWallCells = 0.5f;

        /// <summary>Reliefs stamped in together whose loop fails: the one nearest the failure is left and the rest tried again (off: all are left).</summary>
        public static bool dropFailing = true;

        /// <summary>The walls' lines spaced out so they and the reliefs' quads come to about the target together (off: at the spacing, the reliefs' quads on top).</summary>
        public static bool reliefBudget = true;

        /// <summary>...only walls of at least this many square cells.</summary>
        public static float budgetWallCells = 100f;

        /// <summary>A relief is stamped into its wall's regions in its plane as into its own.</summary>
        public static bool sameWallStamp = true;

        /// <summary>A strip of not-wall thinner than twice this many cells does not join two patches (a trim from a window to the wall's edge).</summary>
        public static float reliefBarrierCells = 0.25f;

        /// <summary>The smallest relief, in cells along either side.</summary>
        public static float reliefMinCells = 0.75f;

        /// <summary>Unsplit, the leftover triangles' pairing: its quality floor and longest chain.</summary>
        public static float pairingQuality = Extraction.TrianglePairing.defaultQualityThreshold;
        public static int pairingChain = Extraction.TrianglePairing.defaultMaxChainQuads;

        /// <summary>FeatureFit passes over the points off the planar regions.</summary>
        public static int fitIterations = 2;

        public sealed class Stats
        {
            public PlanarRegions.Result flat;
            public int proxyTriangles, components, filled, failed, gridPoints, cutEdges, cuts, quads;
            public long flattenMs, decimateMs, fillMs, totalMs;
            public FeatureFit.Stats fit;
            public string decimate;
            public int failedTriangulation, failedOutline;
            public int reliefHoles, reliefs, reliefQuads, straightened, patches, bridged, bridgeLeft, turnedQuads;
            public string reliefLeft = "";
            public string pairing = "";
            public string budget = "";
            public override string ToString() =>
                $"planar quads: {flat}; proxy {proxyTriangles} triangles ({straightened} points straightened away), {components} regions and {patches} patches ({filled} filled, {failed} left), " +
                $"{gridPoints} grid points, {cuts} cuts on {cutEdges} edges, {quads} quads (fill failures: {failedTriangulation} triangulation, {failedOutline} outline); reliefs {reliefs} of {reliefHoles} holes, {reliefQuads} quads (left: {reliefLeft}); pairing [{pairing}]; {bridged} triangle pairs bridged ({bridgeLeft} left), {turnedQuads} quads turned to the nearer diagonal; budget [{budget}]; decimate [{decimate}]; flatten {flattenMs} ms, decimate {decimateMs} ms, fill {fillMs} ms, total {totalMs} ms; {fit}";
        }

        /// <summary>The quads, or false when the source is not mostly planar (or nothing came of it).</summary>
        public static bool Build(Vector3[] sourcePositions, int[] sourceTriangles, int targetQuads,
            out Vector3[] positions, out List<int[]> faces, out Stats stats)
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats = new Stats();
            positions = null;
            faces = null;
            double area = 0;
            for (var t = 0; t + 2 < sourceTriangles.Length; t += 3)
                area += 0.5 * Vector3.Cross(sourcePositions[sourceTriangles[t + 1]] - sourcePositions[sourceTriangles[t]],
                    sourcePositions[sourceTriangles[t + 2]] - sourcePositions[sourceTriangles[t]]).magnitude;
            if (!(area > 0) || targetQuads <= 0) return false;
            var cell = (float)Math.Sqrt(area / targetQuads);
            var flat = PlanarRegions.Flatten(sourcePositions, sourceTriangles, cell);
            stats.flat = flat;
            stats.flattenMs = watch.ElapsedMilliseconds;
            if (flat.share < PlanarRegions.minShare || flat.regions == 0) return false;

            // 2. Simplified with the regions as labels (0: off every region).
            watch.Restart();
            var m = sourceTriangles.Length / 3;
            var labelOf = new int[m];
            for (var t = 0; t < m; t++) labelOf[t] = flat.triangleRegion[t] + 1;
            var labels = new MaterialLabels(flat.positions, sourceTriangles, labelOf);
            debugProxy?.Invoke(flat.positions, sourceTriangles, labelOf, "source");
            var budget = Mathf.Max(256, Mathf.RoundToInt(targetQuads *
                (freeTrianglesPerQuad * (1f - flat.share) + flatTrianglesPerQuad * flat.share)));
            var options = DecimateOptions.ForTriangleCount(budget);
            options.labels = labels;
            // Slivers on the planes do not matter: those triangles are made again.
            options.sliverGuard = sliverGuard;
            if (smallRegionEdgeCells > 0f)
            {
                // By the cell a point is in (the simplification asks again of points it has moved).
                long Voxel(Vector3 q) => ((long)Mathf.FloorToInt(q.x / cell) & 0x1FFFFF) << 42
                    | ((long)Mathf.FloorToInt(q.y / cell) & 0x1FFFFF) << 21 | ((long)Mathf.FloorToInt(q.z / cell) & 0x1FFFFF);
                var limited = new HashSet<long>();
                var small = smallRegionCells * cell * cell;
                for (var t = 0; t < m; t++)
                {
                    var r = flat.triangleRegion[t];
                    if (r >= 0 ? flat.areas[r] >= small : !smallRegionFree) continue;
                    for (var k = 0; k < 3; k++) limited.Add(Voxel(flat.positions[sourceTriangles[3 * t + k]]));
                }
                var edge = smallRegionEdgeCells * cell;
                options.vertexSizes = ps =>
                {
                    var size = new float[ps.Length];
                    for (var i = 0; i < ps.Length; i++) size[i] = limited.Contains(Voxel(ps[i])) ? edge : float.MaxValue;
                    return size;
                };
            }
            PreDecimate.Decimate(flat.positions, sourceTriangles, options, out var decimateStats, out var pp, out var pt);
            stats.decimate = decimateStats.ToString();
            var proxyLabel = labels.ForTriangles(pp, pt);
            var pm = pt.Length / 3;
            debugProxy?.Invoke(pp, pt, proxyLabel, "simplified");
            // A triangle is its region's only facing as the region does: the nearest source
            // triangle of one standing across a region's edge (a fascia under the eaves, a strip
            // folded at a corner) is the region's, and projected onto its plane it folds the outline.
            var cosFacing = Mathf.Cos(facingDegrees * Mathf.Deg2Rad);
            for (var t = 0; t < pm; t++)
            {
                if (proxyLabel[t] <= 0) continue;
                var nt = Vector3.Cross(pp[pt[3 * t + 1]] - pp[pt[3 * t]], pp[pt[3 * t + 2]] - pp[pt[3 * t]]);
                var len = nt.magnitude;
                if (!(len > 0f) || Vector3.Dot(nt / len, flat.normals[proxyLabel[t] - 1]) < cosFacing) proxyLabel[t] = 0;
            }
            debugProxy?.Invoke(pp, pt, proxyLabel, "facing");
            if (straightenCells > 0f)
            {
                stats.straightened = Straighten(pp, ref pt, ref proxyLabel, straightenCells * cell, thinCells * cell);
                pm = pt.Length / 3;
                debugProxy?.Invoke(pp, pt, proxyLabel, "straightened");
            }
            stats.proxyTriangles = pm;
            stats.decimateMs = watch.ElapsedMilliseconds;
            if (pm == 0) return false;

            // 3. Regions of the proxy: connected triangles of one label.
            watch.Restart();
            var halfEdge = new Dictionary<long, int>(3 * pm, Extraction.EdgeKey.Comparer);
            for (var t = 0; t < pm; t++)
                for (var k = 0; k < 3; k++)
                    halfEdge[Extraction.EdgeKey.Pack(pt[3 * t + k], pt[3 * t + (k + 1) % 3])] = t;
            int Twin(int t, int k) =>
                halfEdge.TryGetValue(Extraction.EdgeKey.Pack(pt[3 * t + (k + 1) % 3], pt[3 * t + k]), out var u) ? u : -1;
            var component = new int[pm];
            for (var t = 0; t < pm; t++) component[t] = -1;
            var componentLabel = new List<int>();
            var stack = new Stack<int>();
            for (var s = 0; s < pm; s++)
            {
                if (component[s] >= 0 || proxyLabel[s] <= 0) continue;
                var c = componentLabel.Count;
                componentLabel.Add(proxyLabel[s]);
                component[s] = c;
                stack.Push(s);
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    for (var k = 0; k < 3; k++)
                    {
                        var u = Twin(t, k);
                        if (u < 0 || component[u] >= 0 || proxyLabel[u] != proxyLabel[t]) continue;
                        component[u] = c;
                        stack.Push(u);
                    }
                }
            }
            var components = componentLabel.Count;
            stats.components = components;

            // Each region's frame and lines, and where its outline is to be cut.
            var h = spacingCells * cell;
            var frames = new Frame[components];
            var outline = new List<(int a, int b)>[components];
            for (var c = 0; c < components; c++) outline[c] = new List<(int, int)>();
            void Outlines()
            {
                for (var c = 0; c < components; c++) outline[c].Clear();
                for (var t = 0; t < pm; t++)
                {
                    var c = component[t];
                    if (c < 0) continue;
                    for (var k = 0; k < 3; k++)
                    {
                        var u = Twin(t, k);
                        if (u >= 0 && component[u] == c) continue;
                        outline[c].Add((pt[3 * t + k], pt[3 * t + (k + 1) % 3]));
                    }
                }
            }
            Outlines();
            for (var c = 0; c < components; c++)
            {
                var r = componentLabel[c] - 1;
                frames[c] = Frame.For(flat.normals[r], flat.offsets[r], outline[c], pp);
            }

            // Reliefs (windows, doors, panels) stamped into their walls: the simplified triangles over
            // them go, the gap between the rest of the wall and each outline triangulated again.
            var gaps = reliefs
                ? FindReliefs(pp, pt, component, components, componentLabel, frames, Twin, flat, sourceTriangles, cell, h, stats)
                : new List<Gap>();
            var owned = new bool[pm];
            var holeEdge = new HashSet<long>(Extraction.EdgeKey.Comparer);
            if (gaps.Count > 0)
            {
                var ppl = new List<Vector3>(pp);
                var ptl = new List<int>(pt);
                var cl = new List<int>(component);
                foreach (var gap in gaps)
                {
                    foreach (var t in gap.removed) { owned[t] = true; cl[t] = -1; }
                    var index = new List<int>(gap.outer);
                    foreach (var hole in gap.holes)
                    {
                        var ol = hole.relief.outline;
                        hole.outlineIndex = new int[ol.Count];
                        for (var i = 0; i < ol.Count; i++)
                        {
                            hole.outlineIndex[i] = ppl.Count;
                            ppl.Add(gap.frame.To3(new Vector2(hole.relief.us[ol[i].x], hole.relief.vs[ol[i].y])));
                        }
                        for (var i = 0; i < ol.Count; i++)
                            holeEdge.Add(Extraction.EdgeKey.PackUndirected(hole.outlineIndex[i], hole.outlineIndex[(i + 1) % ol.Count]));
                        index.AddRange(hole.outlineIndex);
                    }
                    foreach (var g in gap.gap) ptl.Add(index[g]);
                    for (var i = 0; i < gap.gap.Count / 3; i++) cl.Add(gap.wall);
                }
                pp = ppl.ToArray();
                pt = ptl.ToArray();
                component = cl.ToArray();
                pm = pt.Length / 3;
                Array.Resize(ref owned, pm);
                halfEdge.Clear();
                for (var t = 0; t < pm; t++)
                    if (!owned[t])
                        for (var k = 0; k < 3; k++)
                            halfEdge[Extraction.EdgeKey.Pack(pt[3 * t + k], pt[3 * t + (k + 1) % 3])] = t;
                Outlines();
            }
            // The simplified triangles off every region, in flat patches (a trim's step, a sill's
            // face): filled as regions too, their lines run on from their neighbours', so a step
            // between two bands is a row of quads where both bands cut it -- not each triangle
            // fanned from its middle on its own.
            var planarComponents = components;
            if (freePatches)
            {
                var cosPatch = Mathf.Cos(patchDegrees * Mathf.Deg2Rad);
                var flatTol = patchFlatCells * cell;
                var planes = new List<(Vector3 n, float offset)>();
                var queue = new Queue<int>();
                Vector3 UnitNormal(int t)
                {
                    var n = Vector3.Cross(pp[pt[3 * t + 1]] - pp[pt[3 * t]], pp[pt[3 * t + 2]] - pp[pt[3 * t]]);
                    var len = n.magnitude;
                    return len > 1e-12f ? n / len : Vector3.zero;
                }
                for (var s0 = 0; s0 < pm; s0++)
                {
                    if (owned[s0] || component[s0] >= 0) continue;
                    var n0 = UnitNormal(s0);
                    if (n0 == Vector3.zero) continue;
                    var offset = Vector3.Dot(n0, pp[pt[3 * s0]]);
                    var c = components + planes.Count;
                    planes.Add((n0, offset));
                    component[s0] = c;
                    queue.Enqueue(s0);
                    while (queue.Count > 0)
                    {
                        var t = queue.Dequeue();
                        for (var k = 0; k < 3; k++)
                        {
                            var u = Twin(t, k);
                            if (u < 0 || owned[u] || component[u] >= 0) continue;
                            if (Vector3.Dot(UnitNormal(u), n0) < cosPatch) continue;
                            var flatEnough = true;
                            for (var i = 0; i < 3; i++)
                                if (Mathf.Abs(Vector3.Dot(n0, pp[pt[3 * u + i]]) - offset) > flatTol) flatEnough = false;
                            if (!flatEnough) continue;
                            component[u] = c;
                            queue.Enqueue(u);
                        }
                    }
                }
                if (planes.Count > 0)
                {
                    components += planes.Count;
                    Array.Resize(ref frames, components);
                    Array.Resize(ref outline, components);
                    for (var c = planarComponents; c < components; c++)
                    {
                        outline[c] = new List<(int, int)>();
                        componentLabel.Add(0);
                    }
                    Outlines();
                    for (var c = planarComponents; c < components; c++)
                        frames[c] = Frame.For(planes[c - planarComponents].n, planes[c - planarComponents].offset, outline[c], pp);
                }
                stats.patches = planes.Count;
            }
            // Regions left with no triangles (a window's panes and bars): the reliefs'.
            var ownedComponent = new bool[components];
            {
                var live = new int[components];
                for (var t = 0; t < pm; t++) if (component[t] >= 0) live[component[t]]++;
                for (var c = 0; c < components; c++) ownedComponent[c] = live[c] == 0;
            }

            var tanAligned = Mathf.Tan(alignedDegrees * Mathf.Deg2Rad);
            // With reliefs (more quads than the wall they take): filled again with the walls' lines
            // further apart, until the walls' faces and the reliefs' quads come to about the target.
            var hGrid = h;
            // (Only large walls' lines spaced out: a chimney's sides keep theirs.)
            var componentArea = new double[components];
            for (var t = 0; t < pm; t++)
            {
                var c = component[t];
                if (c >= 0) componentArea[c] += 0.5 * Vector3.Cross(pp[pt[3 * t + 1]] - pp[pt[3 * t]], pp[pt[3 * t + 2]] - pp[pt[3 * t]]).magnitude / (cell * cell);
            }
            float Spacing(int c) => componentArea[c] >= budgetWallCells ? hGrid : h;
            var tried = new List<(float h, double faces)>();
            bool[] exactAfter;
            Dictionary<long, List<float>> cuts;
            List<Vector3> points;
            Dictionary<long, int[]> chain = null;
            List<int[]> reliefQuads;
            var regionTriangles = new List<int>[components];
            for (var c = 0; c < components; c++) regionTriangles[c] = new List<int>();
            for (var t = 0; t < pm; t++) if (component[t] >= 0) regionTriangles[component[t]].Add(t);
            var fillTriangles = new List<int>[components];
            // An edge's points from a to b.
            void Walk(int a, int b, List<int> into)
            {
                into.Add(a);
                if (!chain.TryGetValue(Extraction.EdgeKey.PackUndirected(a, b), out var ids)) return;
                if (a < b) into.AddRange(ids);
                else for (var i = ids.Length - 1; i >= 0; i--) into.Add(ids[i]);
            }
            for (var attempt = 0; ; attempt++)
            {
            stats.cutEdges = stats.cuts = stats.failedTriangulation = stats.failedOutline = stats.gridPoints = stats.filled = stats.failed = 0;
            cuts = new Dictionary<long, List<float>>(Extraction.EdgeKey.Comparer);
            // Each region's lines: its own, or shared along every border with a region whose
            // lines run the same way (a strip between two walls cut where both are).
            var shared = sharedLines ? SharedLines(pp, pt, component, components, ownedComponent, frames, outline, Twin, Spacing, tanAligned) : null;
            var wanted = new List<(Vector2 at, float clear)>[components];
            for (var c = 0; c < components; c++)
            {
                var frame = frames[c];
                if (shared == null)
                {
                    frame.Lines(outline[c], pp, Spacing(c), tanAligned);
                    wanted[c] = frame.Grid(Spacing(c));
                }
                else wanted[c] = shared.Grid(c, frame, Spacing(c));
                if (ownedComponent[c]) continue;
                foreach (var (a, b) in outline[c])
                {
                    if (holeEdge.Contains(Extraction.EdgeKey.PackUndirected(a, b))) continue;
                    Vector2 qa = frame.To2(pp[a]), qb = frame.To2(pp[b]);
                    var d = qb - qa;
                    var len = d.magnitude;
                    if (!(len > 0f)) continue;
                    var at = new List<float>();
                    var axis = Mathf.Abs(d.y) <= tanAligned * Mathf.Abs(d.x) ? 0 : Mathf.Abs(d.x) <= tanAligned * Mathf.Abs(d.y) ? 1 : -1;
                    if (shared != null) { if (axis >= 0) shared.Crossings(c, axis, pp[a], pp[b], at); }
                    else if (axis == 0) frame.Crossings(frame.us, qa.x, qb.x, at);
                    else if (axis == 1) frame.Crossings(frame.vs, qa.y, qb.y, at);
                    // Slanted (a gable's rake) or long between lines: even steps of at most the spacing.
                    at.Add(0f); at.Add(1f);
                    at.Sort();
                    var even = new List<float>();
                    for (var i = 0; i + 1 < at.Count; i++)
                    {
                        var steps = Mathf.CeilToInt((at[i + 1] - at[i]) * len / (1.25f * Spacing(c)));
                        for (var s = 1; s < steps; s++) even.Add(at[i] + (at[i + 1] - at[i]) * s / steps);
                    }
                    at.AddRange(even);
                    at.RemoveAll(s => s <= 0f || s >= 1f);
                    if (at.Count == 0) continue;
                    // Stored along the edge from its lower vertex.
                    var key = Extraction.EdgeKey.PackUndirected(a, b);
                    if (a > b) for (var i = 0; i < at.Count; i++) at[i] = 1f - at[i];
                    if (!cuts.TryGetValue(key, out var list)) cuts[key] = list = new List<float>();
                    list.AddRange(at);
                }
            }

            // The cut points, merged where both sides asked for nearly the same.
            points = new List<Vector3>(pp);
            chain = new Dictionary<long, int[]>(Extraction.EdgeKey.Comparer);
            foreach (var kv in cuts)
            {
                int lo = Extraction.EdgeKey.First(kv.Key), hi = Extraction.EdgeKey.Second(kv.Key);
                var len = (pp[hi] - pp[lo]).magnitude;
                var list = kv.Value;
                list.Sort();
                var merged = new List<float>();
                var closest = 0.2f * h / Mathf.Max(1e-12f, len);
                foreach (var s in list)
                {
                    if (s < closest || s > 1f - closest) continue;
                    if (merged.Count > 0 && s - merged[merged.Count - 1] < closest) continue;
                    merged.Add(s);
                }
                if (merged.Count == 0) continue;
                var ids = new int[merged.Count];
                for (var i = 0; i < merged.Count; i++)
                {
                    ids[i] = points.Count;
                    points.Add(Vector3.LerpUnclamped(pp[lo], pp[hi], merged[i]));
                }
                chain[kv.Key] = ids;
                stats.cutEdges++;
                stats.cuts += ids.Length;
            }
            // The reliefs' own points and quads.
            reliefQuads = new List<int[]>();
            foreach (var gap in gaps)
                foreach (var hole in gap.holes)
                    if (hole.relief != null) reliefQuads.AddRange(Emit(gap.frame, hole, points));
            stats.reliefQuads = reliefQuads.Count;

            // Fill each region; regions that fail keep their simplified triangles.
            Parallel.For(0, components, BackgroundScheduler.Options, c =>
            {
                if (ownedComponent[c]) { frames[c].grid = new List<Vector2>(); return; }
                fillTriangles[c] = Fill(frames[c], regionTriangles[c], pt, outline[c], points, Walk, wanted[c], out var grid, out var why);
                frames[c].grid = grid;
                frames[c].failure = why;
            });
            var triangles = new List<int>(2 * pm);
            var origin = debugOrigin != null ? new List<int>() : null;
            var exact = new List<bool>();
            var filled = new bool[components];
            var gridStart = new int[components];
            var gridCount = new int[components];
            foreach (var f in frames) { if (f.failure == 1) stats.failedTriangulation++; else if (f.failure == 2) stats.failedOutline++; }
            for (var c = 0; c < components; c++) if (fillTriangles[c] != null) stats.gridPoints += frames[c].grid.Count;
            // Grid points become vertices (serially: their indices).
            for (var c = 0; c < components; c++)
            {
                if (ownedComponent[c]) continue;
                var tris = fillTriangles[c];
                if (tris == null) { stats.failed++; continue; }
                filled[c] = true;
                stats.filled++;
                var gridBase = points.Count;
                var frame = frames[c];
                foreach (var g in frame.grid) points.Add(frame.To3(g));
                gridStart[c] = gridBase;
                gridCount[c] = frame.grid.Count;
                for (var i = 0; i < tris.Count; i++)
                    triangles.Add(tris[i] >= 0 ? tris[i] : gridBase + (-tris[i] - 1));
                if (origin != null) for (var i = 0; i < tris.Count / 3; i++) origin.Add(c);
            }
            var isExact = new bool[points.Count];
            for (var c = 0; c < planarComponents; c++)
            {
                if (!filled[c]) continue;
                foreach (var (a, b) in outline[c]) { isExact[a] = isExact[b] = true; }
            }
            for (var i = pp.Length; i < points.Count; i++) isExact[i] = true;
            // A patch's own points, and those cut along its outline, are free points as before.
            var looseRun = new List<int>();
            for (var c = planarComponents; c < components; c++)
            {
                if (!filled[c]) continue;
                for (var i = 0; i < gridCount[c]; i++) isExact[gridStart[c] + i] = false;
                foreach (var (a, b) in outline[c])
                {
                    looseRun.Clear();
                    Walk(a, b, looseRun);
                    for (var i = 1; i < looseRun.Count; i++) isExact[looseRun[i]] = false;
                }
            }
            // The rest: the simplified triangles, their cut edges fanned.
            var corners = new List<int>(12);
            var sideStart = new int[3];
            var middleOf = new Func<List<int>, int>(around =>
            {
                var centre = Vector3.zero;
                foreach (var v in around) centre += points[v];
                points.Add(centre / around.Count);
                return points.Count - 1;
            });
            var firstFree = points.Count;
            for (var t = 0; t < pm; t++)
            {
                var c = component[t];
                if (owned[t] || c >= 0 && filled[c]) continue;
                corners.Clear();
                for (var side = 0; side < 3; side++)
                {
                    sideStart[side] = corners.Count;
                    Walk(pt[3 * t + side], pt[3 * t + (side + 1) % 3], corners);
                }
                foreach (var v in corners) if (v >= pp.Length && v < isExact.Length) isExact[v] = false;
                var before = triangles.Count;
                CutTriangle(corners, sideStart, middleOf, triangles);
                if (origin != null) for (var i = before; i < triangles.Count; i += 3) origin.Add(c < 0 ? -1 : -2);
            }
            if (points.Count > isExact.Length) Array.Resize(ref isExact, points.Count);
            stats.fillMs = watch.ElapsedMilliseconds;

            // 4. Paired, split, the new points off the planes put on the source.
            var basePositions = points.ToArray();
            if (origin != null)
            {
                var componentRegion = new int[components];
                for (var c = 0; c < components; c++) componentRegion[c] = componentLabel[c] - 1;
                debugOrigin(basePositions, triangles.ToArray(), origin.ToArray(), componentRegion, false);
            }
            var pairStats = new DetailQuads.Stats();
            var baseFaces = DetailQuads.Pair(basePositions, triangles.ToArray(), pairStats);
            if (split)
            {
                baseFaces.AddRange(reliefQuads);
                Split(basePositions, baseFaces, isExact, out positions, out faces, out exactAfter);
            }
            else
            {
                positions = basePositions;
                faces = baseFaces;
                exactAfter = isExact;
                // The leftover triangles paired across the fill's quads (not the reliefs': those are exact).
                stats.pairing = Extraction.TrianglePairing.Run(faces, positions, pairingQuality, pairingChain).ToString();
                if (origin != null)
                {
                    long Key(int a, int b, int c)
                    {
                        if (a > b) (a, b) = (b, a);
                        if (b > c) (b, c) = (c, b);
                        if (a > b) (a, b) = (b, a);
                        return ((long)a * 3000017 + b) * 3000017 + c;
                    }
                    var from = new Dictionary<long, int>();
                    for (var i = 0; i < origin.Count; i++) from[Key(triangles[3 * i], triangles[3 * i + 1], triangles[3 * i + 2])] = origin[i];
                    var left = new List<int>();
                    var leftFrom = new List<int>();
                    foreach (var face in faces)
                    {
                        if (face.Length != 3) continue;
                        left.AddRange(face);
                        leftFrom.Add(from.TryGetValue(Key(face[0], face[1], face[2]), out var o) ? o : -3);
                    }
                    var componentRegion = new int[components];
                    for (var c = 0; c < components; c++) componentRegion[c] = componentLabel[c] - 1;
                    debugOrigin(positions, left.ToArray(), leftFrom.ToArray(), componentRegion, true);
                }
                DumpFaces(positions, faces, "paired");
                if (bridges)
                {
                    stats.bridged = Bridge(ref positions, faces, ref exactAfter, bridgeQuads, out var bridgeLeft);
                    stats.bridgeLeft = bridgeLeft;
                }
                DumpFaces(positions, faces, "bridged");
                faces.AddRange(reliefQuads);
            }
            stats.budget += $"{(stats.budget.Length > 0 ? ", " : "")}{hGrid / cell:0.00} cells: {faces.Count} faces ({reliefQuads.Count} of them reliefs')";
            if (!reliefBudget || gaps.Count == 0 || attempt == 2) break;
            {
                var faceCount = faces.Count - reliefQuads.Count;
                var want = targetQuads - reliefQuads.Count;
                if (want < 0.25 * targetQuads || Math.Abs(faceCount - want) <= 0.03 * want) break;
                tried.Add((hGrid, faceCount));
                // Faces go as the spacing to a power (lines through corners stay): first guessed,
                // then as the last two tries went.
                var power = 0.85;
                if (tried.Count >= 2)
                {
                    var (h0, f0) = tried[tried.Count - 2];
                    var (h1, f1) = tried[tried.Count - 1];
                    if (h1 != h0 && f0 > 0 && f1 > 0) power = Math.Max(0.3, Math.Min(2.0, Math.Log(f0 / f1) / Math.Log(h1 / h0)));
                }
                hGrid *= (float)Math.Pow(faceCount / (double)want, 1.0 / power);
            }
            }
            stats.quads = faces.Count;
            var surface = new SpatialGrid(flat.positions, sourceTriangles);
            var placed = positions;
            var loose = new bool[placed.Length];
            for (var v = 0; v < placed.Length; v++) loose[v] = !exactAfter[v];
            Parallel.For(0, placed.Length, BackgroundScheduler.Options, v =>
            {
                if (loose[v]) placed[v] = surface.NearestPoint(placed[v], out _);
            });
            DetailQuads.Relax(positions, faces, loose, surface, DetailQuads.relaxIterations);
            DumpFaces(positions, faces, "placed");
            if (fitIterations > 0)
            {
                var keep = new Vector3[positions.Length];
                Array.Copy(positions, keep, positions.Length);
                stats.fit = FeatureFit.Fit(positions, faces, flat.positions, sourceTriangles, fitIterations);
                // The planes' points stay where they were built.
                for (var v = 0; v < positions.Length; v++) if (!loose[v]) positions[v] = keep[v];
            }
            if (nearerDiagonal) stats.turnedQuads = NearerDiagonals(positions, faces, surface);
            DumpFaces(positions, faces, "fit");
            Compact(ref positions, faces);
            stats.totalMs = total.ElapsedMilliseconds;
            return faces.Count > 0;
        }

        /// <summary>
        /// A bent quad's cut that leaves a half facing against the quad (one through the outside of
        /// a quad bent in) loses to one that does not, whatever the distances.
        /// </summary>
        public static bool diagonalFoldGuard = true;

        // Each quad whose two halves, cut one way, fold by more than a few degrees: turned by a
        // corner when the other cut lies nearer the surface (the middle of the cut and of each
        // half, the farthest of the three), or when only it keeps both halves facing the quad's
        // way (diagonalFoldGuard). Marks the quads turned in `turnedFaces` when given; returns how
        // many.
        internal static int NearerDiagonals(Vector3[] positions, List<int[]> faces, SpatialGrid surface,
            bool[] turnedFaces = null)
        {
            var cosFlat = Mathf.Cos(2f * Mathf.Deg2Rad);
            var turned = 0;
            float Off(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                // Cut along a-c: halves a b c and a c d.
                surface.NearestPoint((a + c) * 0.5f, out var m);
                surface.NearestPoint((a + b + c) / 3f, out var h0);
                surface.NearestPoint((a + c + d) / 3f, out var h1);
                return Mathf.Max(m, Mathf.Max(h0, h1));
            }
            var turn = new bool[faces.Count];
            Parallel.For(0, faces.Count, BackgroundScheduler.Options, i =>
            {
                var f = faces[i];
                if (f.Length != 4) return;
                Vector3 a = positions[f[0]], b = positions[f[1]], c = positions[f[2]], d = positions[f[3]];
                var n0 = Vector3.Cross(b - a, c - a);
                var n1 = Vector3.Cross(c - a, d - a);
                if (n0.sqrMagnitude > 0f && n1.sqrMagnitude > 0f && MeshMath.CosAngleBetween(n0, n1) >= cosFlat) return;
                if (diagonalFoldGuard)
                {
                    // The quad's own normal: the sum of either cut's halves (the same both ways).
                    var n = n0 + n1;
                    var keeps = Vector3.Dot(n0, n) > 0f && Vector3.Dot(n1, n) > 0f;
                    var other = Vector3.Dot(Vector3.Cross(c - b, d - b), n) > 0f && Vector3.Dot(Vector3.Cross(d - b, a - b), n) > 0f;
                    if (keeps != other) { turn[i] = other; return; }
                }
                turn[i] = Off(b, c, d, a) < Off(a, b, c, d);
            });
            for (var i = 0; i < faces.Count; i++)
            {
                if (!turn[i]) continue;
                var f = faces[i];
                faces[i] = new[] { f[1], f[2], f[3], f[0] };
                if (turnedFaces != null) turnedFaces[i] = true;
                turned++;
            }
            return turned;
        }

        static void DumpFaces(Vector3[] positions, List<int[]> faces, string stage)
        {
            if (debugProxy == null) return;
            var tris = new List<int>();
            foreach (var f in faces)
                for (var i = 1; i + 1 < f.Length; i++) { tris.Add(f[0]); tris.Add(f[i]); tris.Add(f[i + 1]); }
            debugProxy(positions, tris.ToArray(), new int[tris.Count / 3], stage);
        }

        // Points of the simplified mesh taken out (each collapsed into a neighbour) where nothing
        // is lost by it: inside a planar region (it is filled again), and on a border between two
        // labels where the border runs on straight past it -- within `tolerance` of every point
        // taken out of it so far. A triangle the collapse changes must keep facing the way it did.
        // The fill then cuts long straight borders at its lines, not at wherever the simplification
        // happened to stop. Returns the points taken out.
        static int Straighten(Vector3[] pp, ref int[] pt, ref int[] labelOf, float tolerance, float thin)
        {
            var label = labelOf;
            var m = pt.Length / 3;
            var tri = (int[])pt.Clone();
            var alive = new bool[m];
            for (var t = 0; t < m; t++) alive[t] = true;
            var around = new List<int>[pp.Length];
            for (var t = 0; t < m; t++)
                for (var k = 0; k < 3; k++) (around[tri[3 * t + k]] ??= new List<int>(6)).Add(t);
            // Points on an open or non-manifold edge stay.
            var edgeUse = new Dictionary<long, int>(Extraction.EdgeKey.Comparer);
            for (var t = 0; t < m; t++)
                for (var k = 0; k < 3; k++)
                {
                    var key = Extraction.EdgeKey.PackUndirected(tri[3 * t + k], tri[3 * t + (k + 1) % 3]);
                    edgeUse[key] = edgeUse.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            var frozen = new bool[pp.Length];
            foreach (var kv in edgeUse)
                if (kv.Value != 2) frozen[Extraction.EdgeKey.First(kv.Key)] = frozen[Extraction.EdgeKey.Second(kv.Key)] = true;
            // The points each border edge stands for.
            var standsFor = new Dictionary<long, List<Vector3>>(Extraction.EdgeKey.Comparer);
            var tol2 = tolerance * tolerance;
            var cosFree = Mathf.Cos(25f * Mathf.Deg2Rad);
            var removed = 0;
            var labels = new List<int>(4);
            var ends = new List<int>(4);
            var ring = new HashSet<int>();
            Vector3 Normal(int a, int b, int c) => Vector3.Cross(pp[b] - pp[a], pp[c] - pp[a]);
            float SegmentDistance2(Vector3 q, Vector3 a, Vector3 b)
            {
                var ab = b - a;
                var len2 = ab.sqrMagnitude;
                var s = len2 > 0f ? Mathf.Clamp01(Vector3.Dot(q - a, ab) / len2) : 0f;
                return (a + s * ab - q).sqrMagnitude;
            }
            // v's triangles across its edge to w.
            int Across(int v, int w, out int second)
            {
                int first = -1;
                second = -1;
                foreach (var t in around[v])
                {
                    if (!alive[t] || tri[3 * t] != w && tri[3 * t + 1] != w && tri[3 * t + 2] != w) continue;
                    if (first < 0) first = t; else second = t;
                }
                return first;
            }
            bool TryCollapse(int v, int w, bool border, int other, float cosKeep = -2f)
            {
                var t0 = Across(v, w, out var t1);
                if (t0 < 0 || t1 < 0) return false;
                // Link: the points beside both are only the two triangles' third points.
                ring.Clear();
                foreach (var t in around[v])
                    if (alive[t]) for (var k = 0; k < 3; k++) ring.Add(tri[3 * t + k]);
                var common = 0;
                foreach (var t in around[w])
                    if (alive[t])
                        for (var k = 0; k < 3; k++)
                        {
                            var x = tri[3 * t + k];
                            if (x != v && x != w && ring.Remove(x)) common++;
                        }
                if (common != 2) return false;
                // Every triangle that changes keeps facing as it did, and keeps an area.
                foreach (var t in around[v])
                {
                    if (!alive[t] || t == t0 || t == t1) continue;
                    int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
                    var before = Normal(a, b, c);
                    var after = Normal(a == v ? w : a, b == v ? w : b, c == v ? w : c);
                    float lb = before.magnitude, la = after.magnitude;
                    if (!(la > 1e-4f * lb)) return false;
                    var cos = Vector3.Dot(before, after) / (lb * la);
                    if (cos < (cosKeep > -2f ? cosKeep : label[t] > 0 ? 0.999f : cosFree)) return false;
                }
                if (border)
                {
                    // The border w - other stays near every point it now stands for.
                    var keyVw = Extraction.EdgeKey.PackUndirected(v, w);
                    var keyVo = Extraction.EdgeKey.PackUndirected(v, other);
                    if (SegmentDistance2(pp[v], pp[w], pp[other]) > tol2) return false;
                    if (standsFor.TryGetValue(keyVw, out var l1)) foreach (var q in l1) if (SegmentDistance2(q, pp[w], pp[other]) > tol2) return false;
                    if (standsFor.TryGetValue(keyVo, out var l2)) foreach (var q in l2) if (SegmentDistance2(q, pp[w], pp[other]) > tol2) return false;
                    var list = new List<Vector3> { pp[v] };
                    if (l1 != null) list.AddRange(l1);
                    if (l2 != null) list.AddRange(l2);
                    standsFor[Extraction.EdgeKey.PackUndirected(w, other)] = list;
                }
                alive[t0] = alive[t1] = false;
                foreach (var t in around[v])
                {
                    if (!alive[t]) continue;
                    for (var k = 0; k < 3; k++) if (tri[3 * t + k] == v) tri[3 * t + k] = w;
                    around[w].Add(t);
                }
                around[v].Clear();
                frozen[v] = true;
                removed++;
                return true;
            }
            // Edges shorter than `thin` (a strip a sliver wide, a bevel) closed up first, whatever
            // their labels: into the point on more labels (a corner), so the borders keep theirs.
            var cosThin = Mathf.Cos(thinDegrees * Mathf.Deg2Rad);
            int LabelCount(int x)
            {
                labels.Clear();
                foreach (var t in around[x]) if (alive[t] && !labels.Contains(label[t])) labels.Add(label[t]);
                return labels.Count;
            }
            for (var pass = 0; pass < 8 && thin > 0f; pass++)
            {
                var before = removed;
                for (var t = 0; t < m; t++)
                    for (var k = 0; k < 3 && alive[t]; k++)
                    {
                        int a = tri[3 * t + k], b = tri[3 * t + (k + 1) % 3];
                        if ((pp[a] - pp[b]).sqrMagnitude >= thin * thin) continue;
                        if (frozen[a] && frozen[b]) continue;
                        // v goes, w stays.
                        int v = a, w = b;
                        if (frozen[v] || !frozen[w] && LabelCount(v) > LabelCount(w)) { v = b; w = a; }
                        if (!TryCollapse(v, w, false, -1, cosThin)) continue;
                        k = 3;
                    }
                if (removed == before) break;
            }
            for (var pass = 0; pass < 8; pass++)
            {
                var before = removed;
                for (var v = 0; v < pp.Length; v++)
                {
                    if (frozen[v] || around[v] == null || around[v].Count == 0) continue;
                    labels.Clear();
                    foreach (var t in around[v]) if (alive[t] && !labels.Contains(label[t])) labels.Add(label[t]);
                    if (labels.Count == 1)
                    {
                        // Inside a planar region: into whichever neighbour takes it.
                        if (labels[0] <= 0) continue;
                        var done = false;
                        foreach (var t in around[v].ToArray())
                        {
                            if (done || !alive[t]) continue;
                            for (var k = 0; k < 3 && !done; k++)
                            {
                                var w = tri[3 * t + k];
                                if (w != v) done = TryCollapse(v, w, false, -1);
                            }
                        }
                        continue;
                    }
                    if (labels.Count != 2) continue;
                    // On one border: its two edges there.
                    ends.Clear();
                    foreach (var t in around[v])
                    {
                        if (!alive[t]) continue;
                        for (var k = 0; k < 3; k++)
                        {
                            var w = tri[3 * t + k];
                            if (w == v || ends.Contains(w)) continue;
                            var t0 = Across(v, w, out var t1);
                            if (t0 >= 0 && t1 >= 0 && label[t0] != label[t1]) ends.Add(w);
                        }
                    }
                    if (ends.Count != 2) continue;
                    if (!TryCollapse(v, ends[0], true, ends[1])) TryCollapse(v, ends[1], true, ends[0]);
                }
                if (removed == before) break;
            }
            var keptTriangles = new List<int>(3 * m);
            var keptLabels = new List<int>(m);
            for (var t = 0; t < m; t++)
            {
                if (!alive[t]) continue;
                keptTriangles.Add(tri[3 * t]); keptTriangles.Add(tri[3 * t + 1]); keptTriangles.Add(tri[3 * t + 2]);
                keptLabels.Add(label[t]);
            }
            pt = keptTriangles.ToArray();
            labelOf = keptLabels.ToArray();
            return removed;
        }

        // Triangles joined in twos: from a triangle, out across one of its edges and on through
        // quads -- in by one edge, out by the opposite (a face) or by one beside it (two: the
        // quad is cut round its corner) -- to the nearest other triangle. The path is cut down its
        // middle: a quad crossed straight becomes two, one turned in becomes three (about a point
        // in its middle), each triangle a quad with a point on the edge it was left or met by. The
        // nearest pairs go first; a face already cut is not crossed again. A new point is exact
        // where the points it is made of are. Returns the pairs joined; `left`, the triangles none
        // reached.
        static int Bridge(ref Vector3[] positions, List<int[]> faces, ref bool[] exact, int most, out int left)
        {
            var edgeFace = new Dictionary<long, int>(Extraction.EdgeKey.Comparer);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++) edgeFace[Extraction.EdgeKey.Pack(face[k], face[(k + 1) % face.Length])] = f;
            }
            var used = new bool[faces.Count];
            // A step of a path: face g entered by its edge at index `inAt`, left by its edge at
            // `outAt` (-1: the triangle met).
            var cost = new Dictionary<int, int>();
            var tentative = new Dictionary<int, int>();
            var from = new Dictionary<int, (int previous, int inAt)>();
            // The nearest other triangle within `reach`: the path from t's edge `start` to it.
            List<(int face, int inAt, int outAt)> Search(int t, int reach, out int startEdge)
            {
                startEdge = -1;
                cost.Clear();
                tentative.Clear();
                from.Clear();
                var best = -1;
                var bestCost = int.MaxValue;
                var queue = new SortedSet<(int c, int face, int inAt)>();
                cost[t] = 0;
                for (var k = 0; k < 3; k++) Leave(t, k, 0);
                // Out of g by its edge at outAt, the path so far costing c.
                void Leave(int g, int outAt, int c)
                {
                    var gf = faces[g];
                    int a = gf[outAt], b = gf[(outAt + 1) % gf.Length];
                    if (!edgeFace.TryGetValue(Extraction.EdgeKey.Pack(b, a), out var h) || used[h] || cost.ContainsKey(h)) return;
                    var hf = faces[h];
                    if (hf.Length != 3 && hf.Length != 4) return;
                    var inAt = Array.IndexOf(hf, b);
                    if (hf.Length == 3)
                    {
                        if (c < bestCost) { bestCost = c; best = h; from[h] = (g, inAt); }
                        return;
                    }
                    if (c >= reach || tentative.TryGetValue(h, out var had) && had <= c) return;
                    tentative[h] = c;
                    queue.Add((c, h, inAt));
                    from[h] = (g, inAt);
                }
                while (queue.Count > 0)
                {
                    var (c, g, inAt) = queue.Min;
                    queue.Remove(queue.Min);
                    if (c >= bestCost) break;
                    if (cost.ContainsKey(g) || from[g].inAt != inAt) continue;
                    cost[g] = c;
                    // Straight on (one more face), or round a corner (two more).
                    Leave(g, (inAt + 2) % 4, c + 1);
                    Leave(g, (inAt + 1) % 4, c + 2);
                    Leave(g, (inAt + 3) % 4, c + 2);
                }
                if (best < 0) return null;
                // Back from the triangle met: each face with the edge it was entered by and the one
                // it was left by (the left-by edge of a face is where the next face was entered).
                var path = new List<(int face, int inAt, int outAt)>();
                var at = best;
                var outOf = -1;
                while (at != t)
                {
                    var (previous, inAt) = from[at];
                    path.Add((at, inAt, outOf));
                    // The previous face left by the edge `at` was entered across.
                    var af = faces[at];
                    int a = af[inAt], b = af[(inAt + 1) % af.Length];
                    var pf = faces[previous];
                    var j = Array.IndexOf(pf, b);
                    outOf = j;
                    if (previous == t) startEdge = j;
                    at = previous;
                }
                path.Reverse();
                return path;
            }
            var list = new List<Vector3>(positions);
            var flags = new List<bool>(exact);
            var mid = new Dictionary<long, int>(Extraction.EdgeKey.Comparer);
            int Mid(int a, int b)
            {
                var key = Extraction.EdgeKey.PackUndirected(a, b);
                if (mid.TryGetValue(key, out var m)) return m;
                m = list.Count;
                list.Add(0.5f * (list[a] + list[b]));
                flags.Add(flags[a] && flags[b]);
                mid[key] = m;
                return m;
            }
            int Centre(int[] q)
            {
                list.Add(0.25f * (list[q[0]] + list[q[1]] + list[q[2]] + list[q[3]]));
                flags.Add(flags[q[0]] && flags[q[1]] && flags[q[2]] && flags[q[3]]);
                return list.Count - 1;
            }
            var joined = 0;
            var replaced = new Dictionary<int, List<int[]>>();
            for (var reach = 0; reach <= most; reach++)
                for (var t = 0; t < faces.Count; t++)
                {
                    if (used[t] || faces[t].Length != 3) continue;
                    var path = Search(t, reach, out var startEdge);
                    if (path == null || startEdge < 0) continue;
                    var tf = faces[t];
                    int ea = tf[startEdge], eb = tf[(startEdge + 1) % 3];
                    var entry = Mid(ea, eb);
                    replaced[t] = new List<int[]> { new[] { ea, entry, eb, tf[(startEdge + 2) % 3] } };
                    used[t] = true;
                    foreach (var (g, inAt, outAt) in path)
                    {
                        var gf = faces[g];
                        used[g] = true;
                        int a = gf[inAt], b = gf[(inAt + 1) % gf.Length];
                        if (gf.Length == 3)
                        {
                            replaced[g] = new List<int[]> { new[] { a, entry, b, gf[(inAt + 2) % 3] } };
                            break;
                        }
                        int c = gf[(inAt + 2) % 4], d = gf[(inAt + 3) % 4];
                        if (outAt == (inAt + 2) % 4)
                        {
                            var exit = Mid(c, d);
                            replaced[g] = new List<int[]> { new[] { a, entry, exit, d }, new[] { entry, b, c, exit } };
                            entry = exit;
                        }
                        else if (outAt == (inAt + 1) % 4)
                        {
                            // Out by b -> c.
                            var exit = Mid(b, c);
                            var p = Centre(gf);
                            replaced[g] = new List<int[]> { new[] { entry, b, exit, p }, new[] { p, exit, c, d }, new[] { a, entry, p, d } };
                            entry = exit;
                        }
                        else
                        {
                            // Out by d -> a.
                            var exit = Mid(d, a);
                            var p = Centre(gf);
                            replaced[g] = new List<int[]> { new[] { exit, a, entry, p }, new[] { p, entry, b, c }, new[] { exit, p, c, d } };
                            entry = exit;
                        }
                    }
                    joined++;
                }
            left = 0;
            var result = new List<int[]>(faces.Count + 2 * joined);
            for (var f = 0; f < faces.Count; f++)
            {
                if (replaced.TryGetValue(f, out var with)) result.AddRange(with);
                else
                {
                    result.Add(faces[f]);
                    if (faces[f].Length == 3) left++;
                }
            }
            faces.Clear();
            faces.AddRange(result);
            positions = list.ToArray();
            exact = flags.ToArray();
            return joined;
        }

        // A relief found on a wall, and when applied its outline's points.
        sealed class Hole
        {
            public SteppedRelief.Relief relief;
            public float lo, hi;
            // The share of its border on its wall's own region.
            public float own;
            // ...and on its wall's region alone.
            public float mine;
            // The regions its wall's own (in its plane).
            public HashSet<int> same;
            // Its middle on its wall's piece (not only seen from it).
            public bool inside;
            public int wall;
            public List<int> removed;
            // ...those it replaces in its wall's own region alone.
            public List<int> removedOwn;
            public int[] outlineIndex;
        }

        // Reliefs of one wall whose simplified triangles touch, stamped in together: every
        // simplified triangle over them goes, with the wall's triangles touching those, and the gap
        // between what is left of the wall (the loop `outer`) and the reliefs' outlines is
        // triangulated again. Gap indices: outer's points, then each relief's outline's in turn.
        sealed class Gap
        {
            public int wall;
            public Frame frame;
            public List<Hole> holes = new List<Hole>();
            public List<int> removed, outer, gap;
            public string why;
            // A relief of the same wall its triangles ran into: to go with it.
            public Hole merge;
            // The relief nearest where its loop failed: left, and the rest tried again.
            public Hole blame;
            // Tried alone (left from a group): runs into no other.
            public bool single;
        }

        // Each wall seen square on, at the source's own resolution (a raster of the outermost
        // surface near its plane): every patch that is not the wall's, enclosed by it, is a
        // candidate -- a window, a door, a panel -- and a relief where it is stepped.
        static List<Gap> FindReliefs(Vector3[] pp, int[] pt, int[] component, int components, List<int> componentLabel,
            Frame[] frames, Func<int, int, int> twin, PlanarRegions.Result flat, int[] sourceTriangles,
            float cell, float h, Stats stats)
        {
            var pm = pt.Length / 3;
            var left = new Dictionary<string, int>();
            void Left(string why, Frame f, float a0, float b0, float a1, float b1)
            {
                lock (left) left[why] = left.TryGetValue(why, out var n) ? n + 1 : 1;
                debugHole?.Invoke(new List<Vector3> { f.To3(new Vector2(a0, b0)), f.To3(new Vector2(a1, b0)), f.To3(new Vector2(a1, b1)), f.To3(new Vector2(a0, b1)) }, why);
            }

            // Each point's triangles.
            var start = new int[pp.Length + 1];
            for (var i = 0; i < pt.Length; i++) start[pt[i] + 1]++;
            for (var v = 0; v < pp.Length; v++) start[v + 1] += start[v];
            var around = new int[pt.Length];
            var fillAt = (int[])start.Clone();
            for (var i = 0; i < pt.Length; i++) around[fillAt[pt[i]]++] = i / 3;

            // 1. The walls: regions of at least reliefWallCells square cells.
            var area = new double[components];
            var lo2 = new Vector2[components];
            var hi2 = new Vector2[components];
            for (var c = 0; c < components; c++) { lo2[c] = new Vector2(float.MaxValue, float.MaxValue); hi2[c] = new Vector2(float.MinValue, float.MinValue); }
            for (var t = 0; t < pm; t++)
            {
                var c = component[t];
                if (c < 0) continue;
                var f = frames[c];
                Vector2 a = f.To2(pp[pt[3 * t]]), b = f.To2(pp[pt[3 * t + 1]]), q = f.To2(pp[pt[3 * t + 2]]);
                area[c] += 0.5 * Math.Abs((b.x - a.x) * (q.y - a.y) - (q.x - a.x) * (b.y - a.y));
                lo2[c] = Vector2.Min(lo2[c], Vector2.Min(a, Vector2.Min(b, q)));
                hi2[c] = Vector2.Max(hi2[c], Vector2.Max(a, Vector2.Max(b, q)));
            }
            var walls = new List<int>();
            for (var c = 0; c < components; c++) if (area[c] >= reliefWallCells * cell * cell) walls.Add(c);

            // 2. Each wall's reliefs.
            var step = SteppedRelief.stepCells * cell;
            var band = reliefDepthCells * cell;
            var src = flat.positions;
            // Each region's middle (its area's): how far one stands off a wall is measured there, as
            // planes a little turned from each other (boards of a wall) part further off.
            var middle = new Vector3[flat.regions];
            {
                var weight = new double[flat.regions];
                for (var t = 0; t + 2 < sourceTriangles.Length; t += 3)
                {
                    var r = flat.triangleRegion[t / 3];
                    if (r < 0) continue;
                    Vector3 a = src[sourceTriangles[t]], b = src[sourceTriangles[t + 1]], q = src[sourceTriangles[t + 2]];
                    var w = Vector3.Cross(b - a, q - a).magnitude;
                    middle[r] += w * (a + b + q);
                    weight[r] += w;
                }
                for (var r = 0; r < flat.regions; r++) middle[r] = weight[r] > 0 ? middle[r] / (float)(3 * weight[r]) : flat.normals[r] * flat.offsets[r];
            }
            // Each region's box.
            var regionLo = new Vector3[flat.regions];
            var regionHi = new Vector3[flat.regions];
            for (var r = 0; r < flat.regions; r++) { regionLo[r] = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); regionHi[r] = -regionLo[r]; }
            for (var t = 0; t + 2 < sourceTriangles.Length; t += 3)
            {
                var r = flat.triangleRegion[t / 3];
                if (r < 0) continue;
                for (var k = 0; k < 3; k++)
                {
                    regionLo[r] = Vector3.Min(regionLo[r], src[sourceTriangles[t + k]]);
                    regionHi[r] = Vector3.Max(regionHi[r], src[sourceTriangles[t + k]]);
                }
            }
            var found = new List<Hole>[walls.Count];
            var sameOf = new HashSet<int>[walls.Count];
            Parallel.For(0, walls.Count, BackgroundScheduler.Options, w =>
            {
                found[w] = new List<Hole>();
                var c = walls[w];
                var f = frames[c];
                // The background: the wall's region and any other large one facing the same way
                // (a gable above a trim, a wall's other half) -- in its plane, or standing off it
                // but a good part of its size (a window's pane is neither). A patch is this wall's
                // when most of what borders it is: its region, or any other facing its way that
                // lies in its plane or stands a little before it (a wall of boards, each a little
                // turned and lapped over the next).
                var region = componentLabel[c] - 1;
                var background = new HashSet<int> { region };
                var same = new HashSet<int> { region };
                sameOf[w] = same;
                var cosParallel = Mathf.Cos(10f * Mathf.Deg2Rad);
                for (var r2 = 0; r2 < flat.regions; r2++)
                {
                    if (r2 == region || !(Vector3.Dot(flat.normals[r2], f.normal) > cosParallel)) continue;
                    var rise = Vector3.Dot(middle[r2] - f.origin, f.normal);
                    if (sameWall && rise >= -2f * step && rise <= sameWallCells * cell)
                    {
                        same.Add(r2);
                        background.Add(r2);
                        continue;
                    }
                    if (flat.areas[r2] < reliefWallCells * cell * cell) continue;
                    var off = sameWall ? Mathf.Abs(rise) : Mathf.Abs(flat.offsets[r2] - flat.offsets[region]);
                    if (off <= 2f * step || off <= band && flat.areas[r2] >= backgroundShare * flat.areas[region]) background.Add(r2);
                }
                // Wide enough round the wall for a window standing over its edge (into a gable above),
                // and over the regions in its plane it runs into (a wall of boards).
                var wlo = lo2[c];
                var whi = hi2[c];
                var joined = new HashSet<int> { region };
                for (var grown = sameWall; grown; )
                {
                    grown = false;
                    foreach (var r2 in same)
                    {
                        if (joined.Contains(r2) || regionHi[r2].x < regionLo[r2].x) continue;
                        Vector2 rlo2 = new Vector2(float.MaxValue, float.MaxValue), rhi2 = -rlo2;
                        for (var k = 0; k < 8; k++)
                        {
                            var q = f.To2(new Vector3((k & 1) == 0 ? regionLo[r2].x : regionHi[r2].x, (k & 2) == 0 ? regionLo[r2].y : regionHi[r2].y, (k & 4) == 0 ? regionLo[r2].z : regionHi[r2].z));
                            rlo2 = Vector2.Min(rlo2, q);
                            rhi2 = Vector2.Max(rhi2, q);
                        }
                        if (rhi2.x < wlo.x - band || rlo2.x > whi.x + band || rhi2.y < wlo.y - band || rlo2.y > whi.y + band) continue;
                        joined.Add(r2);
                        wlo = Vector2.Min(wlo, rlo2);
                        whi = Vector2.Max(whi, rhi2);
                        grown = true;
                    }
                }
                // (Seen from the largest of them only.)
                foreach (var r2 in joined)
                    if (flat.areas[r2] > flat.areas[region] || flat.areas[r2] == flat.areas[region] && r2 < region)
                        return;
                float u0 = wlo.x - band, v0 = wlo.y - band, u1 = whi.x + band, v1 = whi.y + band;
                var s = cell / SteppedRelief.samplesPerCell;
                var samples = (double)(u1 - u0) * (v1 - v0) / (s * s);
                if (samples > maxRasterSamples) s *= (float)Math.Sqrt(samples / maxRasterSamples);
                var nu = Mathf.Max(2, Mathf.CeilToInt((u1 - u0) / s));
                var nv = Mathf.Max(2, Mathf.CeilToInt((v1 - v0) / s));
                var minEdge = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var maxEdge = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (var q in new[] { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u0, v1), new Vector2(u1, v1) })
                    foreach (var z in new[] { -band, band })
                    {
                        var p = f.To3(q) + z * f.normal;
                        minEdge = Vector3.Min(minEdge, p);
                        maxEdge = Vector3.Max(maxEdge, p);
                    }
                var tris = new List<SteppedRelief.Flat>();
                var ownTris = new List<SteppedRelief.Flat>();
                var mineTris = new List<SteppedRelief.Flat>();
                for (var t = 0; t + 2 < sourceTriangles.Length; t += 3)
                {
                    Vector3 a = src[sourceTriangles[t]], b = src[sourceTriangles[t + 1]], q = src[sourceTriangles[t + 2]];
                    if (Mathf.Max(a.x, Mathf.Max(b.x, q.x)) < minEdge.x || Mathf.Min(a.x, Mathf.Min(b.x, q.x)) > maxEdge.x) continue;
                    if (Mathf.Max(a.y, Mathf.Max(b.y, q.y)) < minEdge.y || Mathf.Min(a.y, Mathf.Min(b.y, q.y)) > maxEdge.y) continue;
                    if (Mathf.Max(a.z, Mathf.Max(b.z, q.z)) < minEdge.z || Mathf.Min(a.z, Mathf.Min(b.z, q.z)) > maxEdge.z) continue;
                    float za = Vector3.Dot(a - f.origin, f.normal), zb = Vector3.Dot(b - f.origin, f.normal), zq = Vector3.Dot(q - f.origin, f.normal);
                    if (Mathf.Min(za, Mathf.Min(zb, zq)) < -band || Mathf.Max(za, Mathf.Max(zb, zq)) > band) continue;
                    var r3 = flat.triangleRegion[t / 3];
                    tris.Add(new SteppedRelief.Flat
                    {
                        a = f.To2(a), b = f.To2(b), c = f.To2(q), ha = za, hb = zb, hc = zq,
                        wall = background.Contains(r3),
                    });
                    if (r3 >= 0 && same.Contains(r3)) ownTris.Add(tris[tris.Count - 1]);
                    if (r3 == region) mineTris.Add(tris[tris.Count - 1]);
                }
                SteppedRelief.Raster(tris, u0, v0, s, s, nu, nv, out var height, out var wallAt);
                // Where the outermost surface is the wall's own region.
                SteppedRelief.Raster(ownTris, u0, v0, s, s, nu, nv, out var ownHeight, out _);
                SteppedRelief.Raster(mineTris, u0, v0, s, s, nu, nv, out var mineHeight, out _);
                // Patches not the wall's (nothing seen counts), each with its extent; those reaching
                // the raster's edge are not enclosed. A strip thinner than `barrier` (a trim, a
                // gap between boards) does not join two: the patches are what is left of the
                // not-wall once that is worn back by `barrier` (each piece apart), grown back as far.
                bool Wall(int x, int y) => wallAt[x, y] && !float.IsNegativeInfinity(height[x, y]);
                var reach = Mathf.Max(0, Mathf.RoundToInt(reliefBarrierCells * cell / s));
                var dist = new ushort[nu, nv];
                for (var i = 0; i < nu; i++)
                    for (var j = 0; j < nv; j++)
                    {
                        if (Wall(i, j)) continue;
                        var d = ushort.MaxValue - 1;
                        if (i > 0) d = Math.Min(d, dist[i - 1, j] + 1);
                        if (j > 0) d = Math.Min(d, dist[i, j - 1] + 1);
                        dist[i, j] = (ushort)d;
                    }
                for (var i = nu - 1; i >= 0; i--)
                    for (var j = nv - 1; j >= 0; j--)
                    {
                        int d = dist[i, j];
                        if (i + 1 < nu) d = Math.Min(d, dist[i + 1, j] + 1);
                        if (j + 1 < nv) d = Math.Min(d, dist[i, j + 1] + 1);
                        dist[i, j] = (ushort)d;
                    }
                var label = new int[nu, nv];
                var stack = new Stack<(int, int)>();
                var grow = new Queue<(int, int, int)>();
                var patches = 0;
                for (var i0 = 0; i0 < nu; i0++)
                    for (var j0 = 0; j0 < nv; j0++)
                    {
                        if (label[i0, j0] != 0 || dist[i0, j0] <= reach) continue;
                        var id = ++patches;
                        label[i0, j0] = id;
                        stack.Push((i0, j0));
                        while (stack.Count > 0)
                        {
                            var (i, j) = stack.Pop();
                            grow.Enqueue((i, j, 0));
                            foreach (var (x, y) in new[] { (i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1) })
                            {
                                if (x < 0 || y < 0 || x >= nu || y >= nv || label[x, y] != 0 || dist[x, y] <= reach) continue;
                                label[x, y] = id;
                                stack.Push((x, y));
                            }
                        }
                    }
                while (grow.Count > 0)
                {
                    var (i, j, depth) = grow.Dequeue();
                    if (depth > reach) continue;
                    foreach (var (x, y) in new[] { (i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1) })
                    {
                        if (x < 0 || y < 0 || x >= nu || y >= nv || label[x, y] != 0 || Wall(x, y)) continue;
                        label[x, y] = label[i, j];
                        grow.Enqueue((x, y, depth + 1));
                    }
                }
                debugRaster?.Invoke(f.normal, nu, nv, (x, y) => Wall(x, y) ? (ownHeight[x, y] >= height[x, y] - 1e-6f * cell ? 0 : 1) : label[x, y] == 0 ? 2 : 3 + label[x, y], new[] { f.To3(new Vector2(u0, v0)), f.To3(new Vector2(u1, v0)), f.To3(new Vector2(u0, v1)) });
                var pia = new int[patches + 1];
                var pib = new int[patches + 1];
                var pja = new int[patches + 1];
                var pjb = new int[patches + 1];
                var pedge = new bool[patches + 1];
                var pown = new int[patches + 1];
                var pother = new int[patches + 1];
                var pmine = new int[patches + 1];
                for (var id = 1; id <= patches; id++) { pia[id] = pja[id] = int.MaxValue; pib[id] = pjb[id] = -1; }
                for (var i = 0; i < nu; i++)
                    for (var j = 0; j < nv; j++)
                    {
                        var id = label[i, j];
                        if (id == 0) continue;
                        pia[id] = Math.Min(pia[id], i); pib[id] = Math.Max(pib[id], i);
                        pja[id] = Math.Min(pja[id], j); pjb[id] = Math.Max(pjb[id], j);
                        if (i == 0 || j == 0 || i == nu - 1 || j == nv - 1) pedge[id] = true;
                        foreach (var (x, y) in new[] { (i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1) })
                        {
                            if (x < 0 || y < 0 || x >= nu || y >= nv || !Wall(x, y)) continue;
                            if (ownHeight[x, y] >= height[x, y] - 1e-6f * cell) pown[id]++; else pother[id]++;
                            if (mineHeight[x, y] >= height[x, y] - 1e-6f * cell) pmine[id]++;
                        }
                    }
                for (var id = 1; id <= patches; id++)
                    {
                        int ia = pia[id], ib = pib[id], ja = pja[id], jb = pjb[id];
                        var edge = pedge[id];
                        int own = pown[id], other = pother[id];
                        float pu0 = u0 + ia * s, pu1 = u0 + (ib + 1) * s, pv0 = v0 + ja * s, pv1 = v0 + (jb + 1) * s;
                        if (pu1 - pu0 < reliefMinCells * cell || pv1 - pv0 < reliefMinCells * cell) continue;
                        // (Diagnostics only for patches smaller than half the wall: the rest is the world round it.)
                        var small = ib - ia < nu / 2 && jb - ja < nv / 2;
                        if (edge)
                        {
                            if (small) Left("open", f, pu0, pv0, pu1, pv1);
                            continue;
                        }
                        if (own <= other)
                        {
                            if (small) Left("not its own", f, pu0, pv0, pu1, pv1);
                            continue;
                        }
                        lock (stats) stats.reliefHoles++;
                        // Its own samples, and the wall round them (anything else nearby is the wall's here).
                        var pad = Mathf.Max(2, Mathf.CeilToInt(4f * reliefSnapCells * cell / s));
                        int ca = ia - pad, cb = ib + pad, cc = ja - pad, cd = jb + pad;
                        if (ca < 0 || cc < 0 || cb >= nu || cd >= nv) { Left("edge", f, pu0, pv0, pu1, pv1); continue; }
                        var ch = new float[cb - ca + 1, cd - cc + 1];
                        var cw = new bool[cb - ca + 1, cd - cc + 1];
                        for (var i = ca; i <= cb; i++)
                            for (var j = cc; j <= cd; j++)
                            {
                                var mine = label[i, j] == id;
                                ch[i - ca, j - cc] = mine || !float.IsNegativeInfinity(height[i, j]) ? height[i, j] : 0f;
                                cw[i - ca, j - cc] = !mine;
                            }
                        var none = new List<float>();
                        var relief = SteppedRelief.Build(ch, cw, u0 + ca * s, v0 + cc * s, s, s, none, none, cell, h, out var why);
                        if (relief == null) { Left(why, f, pu0, pv0, pu1, pv1); continue; }
                        float rlo = 0f, rhi = 0f;
                        foreach (var l in relief.level) { rlo = Mathf.Min(rlo, l); rhi = Mathf.Max(rhi, l); }
                        if (-rlo > 0.5f * Mathf.Min(pu1 - pu0, pv1 - pv0)) { Left("deep", f, pu0, pv0, pu1, pv1); continue; }
                        debugHole?.Invoke(new List<Vector3> { f.To3(new Vector2(pu0, pv0)), f.To3(new Vector2(pu1, pv0)), f.To3(new Vector2(pu1, pv1)), f.To3(new Vector2(pu0, pv1)) }, "found");
                        found[w].Add(new Hole { relief = relief, lo = rlo, hi = rhi, own = own / (float)(own + other), mine = pmine[id] / (float)(own + other),
                            inside = (pu0 + pu1) / 2 >= lo2[c].x && (pu0 + pu1) / 2 <= hi2[c].x && (pv0 + pv1) / 2 >= lo2[c].y && (pv0 + pv1) / 2 <= hi2[c].y });
                    }
            });

            // 3. The simplified triangles each relief replaces: all within its depths over its
            // cells (a little wider), and the wall's touching those.
            var all = new List<(int wall, Hole hole)>();
            for (var w = 0; w < walls.Count; w++) foreach (var hole in found[w]) { hole.wall = walls[w]; hole.same = sameOf[w]; all.Add((walls[w], hole)); }
            // A triangle's component is the wall's, or one in its plane.
            bool Ours(Hole hole, int c2) => OursPlane(hole, c2);
            bool OursPlane(Hole hole, int c2) => c2 == hole.wall || sameWallStamp && c2 >= 0 && c2 < components && hole.same.Contains(componentLabel[c2] - 1);
            // Off the wall's plane no further than its own regions may be (a lapped board, its top
            // edge behind the plane and its foot before it).
            var roughPlane = Mathf.Max(step, sameWallCells * cell);
            // A window seen from two walls (two pieces of one, or a gable and the wall under it):
            // the one with more of its border its own.
            {
                var boxes = new List<Bounds>();
                foreach (var (c, hole) in all)
                {
                    var r = hole.relief;
                    var f = frames[c];
                    var box = new Bounds(f.To3(new Vector2(r.us[0], r.vs[0])), Vector3.zero);
                    foreach (var q in new[] { new Vector2(r.us[r.us.Count - 1], r.vs[0]), new Vector2(r.us[0], r.vs[r.vs.Count - 1]), new Vector2(r.us[r.us.Count - 1], r.vs[r.vs.Count - 1]) })
                        box.Encapsulate(f.To3(q));
                    box.Encapsulate(box.min + hole.lo * f.normal);
                    box.Encapsulate(box.max + hole.hi * f.normal);
                    boxes.Add(box);
                }
                // (A part of another -- a pane seen from its own region -- after the whole.)
                var part = new bool[all.Count];
                if (sameWall)
                    for (var i = 0; i < all.Count; i++)
                        for (var j = 0; j < all.Count && !part[i]; j++)
                        {
                            if (i == j || Mathf.Abs(Vector3.Dot(frames[all[i].wall].normal, frames[all[j].wall].normal)) <= 0.98f) continue;
                            if (Face(all[j].hole) >= 2f * Face(all[i].hole) && Within(boxes[j], boxes[i].center)) part[i] = true;
                        }
                bool Within(Bounds b, Vector3 q) => q.x >= b.min.x && q.y >= b.min.y && q.z >= b.min.z && q.x <= b.max.x && q.y <= b.max.y && q.z <= b.max.z;
                float Face(Hole hole) => (hole.relief.us[hole.relief.us.Count - 1] - hole.relief.us[0]) * (hole.relief.vs[hole.relief.vs.Count - 1] - hole.relief.vs[0]);
                var order = new List<int>();
                for (var i = 0; i < all.Count; i++) order.Add(i);
                order.Sort((x, y) => part[x] != part[y] ? part[x].CompareTo(part[y])
                    : all[y].hole.inside != all[x].hole.inside ? all[y].hole.inside.CompareTo(all[x].hole.inside)
                    : all[y].hole.own != all[x].hole.own ? all[y].hole.own.CompareTo(all[x].hole.own) : all[y].hole.mine.CompareTo(all[x].hole.mine));
                var kept = new List<int>();
                var keep = new List<(int, Hole)>();
                foreach (var i in order)
                {
                    var clash = false;
                    foreach (var j in kept)
                        if (boxes[i].Intersects(boxes[j]) && Mathf.Abs(Vector3.Dot(frames[all[i].wall].normal, frames[all[j].wall].normal)) > 0.98f)
                        {
                            clash = true;
                            break;
                        }
                    if (clash) { Left("twice", frames[all[i].wall], 0, 0, 0, 0); continue; }
                    kept.Add(i);
                    keep.Add(all[i]);
                }
                all = keep;
            }
            Parallel.For(0, all.Count, BackgroundScheduler.Options, k =>
            {
                var (c, hole) = all[k];
                var f = frames[c];
                var r = hole.relief;
                var eps = 0.05f * cell;
                float zlo = hole.lo - step, zhi = hole.hi + step;
                float ru0 = r.us[0], ru1 = r.us[r.us.Count - 1], rv0 = r.vs[0], rv1 = r.vs[r.vs.Count - 1];
                var removed = new List<int>();
                var gone = new HashSet<int>();
                var removedOwn = new List<int>();
                var goneOwn = new HashSet<int>();
                for (var t = 0; t < pm; t++)
                {
                    Vector2 a = f.To2(pp[pt[3 * t]]), b = f.To2(pp[pt[3 * t + 1]]), q = f.To2(pp[pt[3 * t + 2]]);
                    if (Mathf.Max(a.x, Mathf.Max(b.x, q.x)) < ru0 || Mathf.Min(a.x, Mathf.Min(b.x, q.x)) > ru1) continue;
                    if (Mathf.Max(a.y, Mathf.Max(b.y, q.y)) < rv0 || Mathf.Min(a.y, Mathf.Min(b.y, q.y)) > rv1) continue;
                    var over = false;
                    for (var i = 0; i + 1 < r.us.Count && !over; i++)
                        for (var j = 0; j + 1 < r.vs.Count && !over; j++)
                            if (r.inside[i, j] && Overlaps(a, b, q, r.us[i] - eps, r.vs[j] - eps, r.us[i + 1] + eps, r.vs[j + 1] + eps)) over = true;
                    if (!over) continue;
                    var near = true;
                    for (var i = 0; i < 3; i++)
                    {
                        var z = Vector3.Dot(pp[pt[3 * t + i]] - f.origin, f.normal);
                        // (Behind it, hidden by it, is its too.)
                        if (z < (sameWallStamp ? -band : zlo) || z > zhi) near = false;
                    }
                    // Something standing over the relief from further out is not its.
                    if (near || component[t] == c)
                    {
                        removedOwn.Add(t);
                        goneOwn.Add(pt[3 * t]); goneOwn.Add(pt[3 * t + 1]); goneOwn.Add(pt[3 * t + 2]);
                    }
                    if (!near && !Ours(hole, component[t])) continue;
                    removed.Add(t);
                    gone.Add(pt[3 * t]); gone.Add(pt[3 * t + 1]); gone.Add(pt[3 * t + 2]);
                }
                var ring = new List<int>();
                var ringOwn = new List<int>();
                for (var t = 0; t < pm; t++)
                {
                    if ((component[t] == c || Ours(hole, component[t]) && Near(t)) && (gone.Contains(pt[3 * t]) || gone.Contains(pt[3 * t + 1]) || gone.Contains(pt[3 * t + 2])))
                        ring.Add(t);
                    if (component[t] == c && (goneOwn.Contains(pt[3 * t]) || goneOwn.Contains(pt[3 * t + 1]) || goneOwn.Contains(pt[3 * t + 2])))
                        ringOwn.Add(t);
                }
                // (Its plane's other regions only round it.)
                bool Near(int t)
                {
                    for (var k2 = 0; k2 < 3; k2++)
                    {
                        var q = f.To2(pp[pt[3 * t + k2]]);
                        if (q.x < ru0 - cell || q.x > ru1 + cell || q.y < rv0 - cell || q.y > rv1 + cell) return false;
                    }
                    return true;
                }
                var set = new HashSet<int>(removed);
                set.UnionWith(ring);
                hole.removed = new List<int>(set);
                set = new HashSet<int>(removedOwn);
                set.UnionWith(ringOwn);
                hole.removedOwn = new List<int>(set);
            });

            // 4. Reliefs sharing a triangle go together; each group's outer loop and gap.
            var group = new int[all.Count];
            int Root(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }
            var owner = new int[pm];
            var dropped = new bool[all.Count];
            var joins = new List<(int, int)>();
            var alone = new List<Gap>();
            void Group()
            {
                for (var i = 0; i < all.Count; i++) group[i] = i;
                for (var t = 0; t < pm; t++) owner[t] = -1;
                for (var i = 0; i < all.Count; i++)
                    if (!dropped[i])
                        foreach (var t in all[i].hole.removed)
                        {
                            if (owner[t] < 0) owner[t] = i;
                            else group[Root(owner[t])] = Root(i);
                        }
                foreach (var (a, b) in joins)
                    if (!dropped[a] && !dropped[b]) group[Root(a)] = Root(b);
            }
            Group();
            var indexOf = new Dictionary<Hole, int>();
            for (var i = 0; i < all.Count; i++) indexOf[all[i].hole] = i;
            // Over a relief: its rectangle a cell wider, from behind it (hidden) to a cell before it.
            bool Over(Hole hole, Frame f, int t)
            {
                var r = hole.relief;
                float x0 = r.us[0] - cell, x1 = r.us[r.us.Count - 1] + cell, y0 = r.vs[0] - cell, y1 = r.vs[r.vs.Count - 1] + cell;
                for (var k2 = 0; k2 < 3; k2++)
                {
                    var q = f.To2(pp[pt[3 * t + k2]]);
                    var z = Vector3.Dot(pp[pt[3 * t + k2]] - f.origin, f.normal);
                    // (Unlabelled simplified triangles over it may stray any way out: they are its.)
                    if (!(q.x >= x0 && q.x <= x1 && q.y >= y0 && q.y <= y1 && z >= -band && (z <= hole.hi + cell || component[t] < 0))) return false;
                }
                return true;
            }
            List<Gap> gapList = null;
            for (var pass = 0; pass < 16; pass++)
            {
            var gaps = new Dictionary<int, Gap>();
            for (var i = 0; i < all.Count; i++)
            {
                if (dropped[i]) continue;
                var r = Root(i);
                if (!gaps.TryGetValue(r, out var g)) gaps[r] = g = new Gap { wall = all[i].wall, frame = frames[all[i].wall] };
                if (g.wall != all[i].wall) g.wall = -1;
                g.holes.Add(all[i].hole);
            }
            gapList = new List<Gap>(gaps.Values);
            Parallel.For(0, gapList.Count, BackgroundScheduler.Options, k =>
            {
                var g = gapList[k];
                // Into the wall and its regions in its plane; its loop folded over seen square on:
                // again, with the boards' lips in its plane gone; else into the wall's region alone.
                StampAll(g);
            });
            // Into the wall and its regions in its plane; its loop folded over seen square on:
            // again, with the boards' lips in its plane gone; grown off round its reliefs: again,
            // kept round them; else into the wall's region alone.
            void StampAll(Gap g)
            {
                Stamp(g, sameWallStamp, false);
                var folded = g.gap == null && g.outer != null && g.why == "crossing";
                if (sameWallStamp && folded) Stamp(g, true, true);
                if (sameWallStamp && g.gap == null && g.merge == null) Stamp(g, true, folded, true);
                if (sameWallStamp && g.gap == null && g.merge == null) Stamp(g, false, false);
            }
            void Stamp(Gap g, bool plane, bool lips, bool kept = false)
            {
                g.why = null;
                g.outer = null;
                g.merge = null;
                g.blame = null;
                if (g.wall < 0) { g.gap = null; return; }
                var rough = plane ? roughPlane : step;
                bool Ours(Hole hole, int c2) => plane ? OursPlane(hole, c2) : c2 == hole.wall;
                var f = g.frame;
                var goneSet = new HashSet<int>();
                foreach (var hole in g.holes) goneSet.UnionWith(plane ? hole.removed : hole.removedOwn);
                // The loop round them: edges of the triangles that go against those that stay (or
                // the wall's open ones). Where it touches itself, or leaves the wall's plane, the
                // triangles there go too (the wall's always; others only over a relief).
                var outer = new List<int>();
                var ok = false;
                for (var round = 0; round < 16 && !ok; round++)
                {
                    var outerNext = new Dictionary<int, int>();
                    var bad = new HashSet<int>();
                    foreach (var t in goneSet)
                        for (var e = 0; e < 3; e++)
                        {
                            var u = twin(t, e);
                            if (u >= 0 ? goneSet.Contains(u) : !Ours(g.holes[0], component[t])) continue;
                            int a = pt[3 * t + e], b = pt[3 * t + (e + 1) % 3];
                            if (outerNext.ContainsKey(a)) bad.Add(a);
                            else outerNext[a] = b;
                            // Off the plane is the wall's own only where the wall had it already.
                            var za = Vector3.Dot(pp[a] - f.origin, f.normal);
                            if (Mathf.Abs(za) > rough && !OnWall(a)) bad.Add(a);
                            // A board's lip (standing across the plane, in its band) folds the loop
                            // seen square on: it goes too.
                            if (lips && !bad.Contains(a))
                                for (var i = start[a]; i < start[a + 1]; i++)
                                {
                                    var t2 = around[i];
                                    if (goneSet.Contains(t2) || !OnPlane(t2, rough) || kept && !Close(t2)) continue;
                                    var n = Vector3.Cross(pp[pt[3 * t2 + 1]] - pp[pt[3 * t2]], pp[pt[3 * t2 + 2]] - pp[pt[3 * t2]]);
                                    if (Vector3.Dot(n, f.normal) < 0.5f * n.magnitude) { bad.Add(a); break; }
                                }
                        }
                    if (bad.Count == 0)
                    {
                        if (outerNext.Count < 3) { g.why = "empty"; break; }
                        // Its loops: the widest is the outer one; what the others enclose (left
                        // islands) goes too.
                        var loops = new List<List<int>>();
                        var traced = new HashSet<int>();
                        var closed = true;
                        foreach (var key in outerNext.Keys)
                        {
                            if (traced.Contains(key)) continue;
                            var loop = new List<int>();
                            for (var v = key; ; )
                            {
                                loop.Add(v);
                                traced.Add(v);
                                if (!outerNext.TryGetValue(v, out v) || loop.Count > outerNext.Count) { closed = false; break; }
                                if (v == key) break;
                            }
                            if (!closed) break;
                            loops.Add(loop);
                        }
                        if (!closed) { g.why = "open"; break; }
                        var widest = 0;
                        var widestArea = double.MinValue;
                        for (var l = 0; l < loops.Count; l++)
                        {
                            double sum = 0;
                            for (var i = 0; i < loops[l].Count; i++)
                            {
                                Vector2 qa = f.To2(pp[loops[l][i]]), qb = f.To2(pp[loops[l][(i + 1) % loops[l].Count]]);
                                sum += (double)qa.x * qb.y - (double)qb.x * qa.y;
                            }
                            if (sum > widestArea) { widestArea = sum; widest = l; }
                        }
                        // Seen square on, crossing itself or a relief's outline: its points there are
                        // bad (the wall round them goes too).
                        if (loops.Count == 1 && plane && Crossing(loops[0], bad)) { }
                        else if (loops.Count == 1)
                        {
                            outer.Clear();
                            outer.AddRange(loops[0]);
                            ok = true;
                            break;
                        }
                        for (var l = 0; l < loops.Count; l++)
                            if (l != widest) bad.UnionWith(loops[l]);
                    }
                    var grew = true;
                    foreach (var v in bad)
                        for (var i = start[v]; i < start[v + 1] && grew; i++)
                        {
                            var t = around[i];
                            if (goneSet.Contains(t)) continue;
                            if (kept ? !(component[t] == g.wall || OverRelief(t) || OnPlane(t, step) || Close(t) && (Ours(g.holes[0], component[t]) || OnPlane(t, rough)))
                                : !Ours(g.holes[0], component[t]) && !OverRelief(t) && !OnPlane(t, rough))
                            {
                                grew = false;
                                foreach (var (w2, other) in all)
                                    if (!g.single && w2 == g.wall && !g.holes.Contains(other) && !dropped[indexOf[other]] && Over(other, f, t)) { g.merge = other; break; }
                                if (g.merge == null && dropFailing && g.holes.Count > 1)
                                {
                                    var q = f.To2(pp[v]);
                                    var nearest = float.MaxValue;
                                    foreach (var hole in g.holes)
                                    {
                                        var r = hole.relief;
                                        var dx = Mathf.Max(0f, Mathf.Max(r.us[0] - q.x, q.x - r.us[r.us.Count - 1]));
                                        var dy = Mathf.Max(0f, Mathf.Max(r.vs[0] - q.y, q.y - r.vs[r.vs.Count - 1]));
                                        if (dx * dx + dy * dy < nearest) { nearest = dx * dx + dy * dy; g.blame = hole; }
                                    }
                                }
                                g.why = (Mathf.Abs(Vector3.Dot(pp[v] - f.origin, f.normal)) > step ? "lifted" : "touching") + (component[t] < 0 ? " free" : " region");
                            }
                            else goneSet.Add(t);
                        }
                    if (!grew) break;
                    if (round == 15) g.why = "pinch";
                }
                bool OnWall(int v)
                {
                    for (var i = start[v]; i < start[v + 1]; i++) if (Ours(g.holes[0], component[around[i]])) return true;
                    return false;
                }
                // In the wall's plane (another region's, or none's): the wall fills it the same.
                bool OnPlane(int t, float tolerance)
                {
                    for (var k2 = 0; k2 < 3; k2++)
                    {
                        var z = Vector3.Dot(pp[pt[3 * t + k2]] - f.origin, f.normal);
                        if (Mathf.Abs(z) > tolerance) return false;
                    }
                    return true;
                }
                // Touching a relief's rectangle two cells wider.
                bool Close(int t)
                {
                    foreach (var hole in g.holes)
                    {
                        var r = hole.relief;
                        for (var k2 = 0; k2 < 3; k2++)
                        {
                            var q = f.To2(pp[pt[3 * t + k2]]);
                            if (q.x >= r.us[0] - 2f * cell && q.x <= r.us[r.us.Count - 1] + 2f * cell && q.y >= r.vs[0] - 2f * cell && q.y <= r.vs[r.vs.Count - 1] + 2f * cell) return true;
                        }
                    }
                    return false;
                }
                bool Crossing(List<int> loop, HashSet<int> into)
                {
                    var q = new Vector2[loop.Count];
                    for (var i = 0; i < loop.Count; i++) q[i] = f.To2(pp[loop[i]]);
                    var others = new List<(Vector2, Vector2)>();
                    foreach (var hole in g.holes)
                    {
                        var r = hole.relief;
                        for (var i = 0; i < r.outline.Count; i++)
                        {
                            var a = r.outline[i];
                            var b = r.outline[(i + 1) % r.outline.Count];
                            others.Add((new Vector2(r.us[a.x], r.vs[a.y]), new Vector2(r.us[b.x], r.vs[b.y])));
                        }
                    }
                    var found = false;
                    for (var i = 0; i < loop.Count; i++)
                    {
                        Vector2 a = q[i], b = q[(i + 1) % loop.Count];
                        var hit = false;
                        foreach (var (c, d) in others) if (SegmentsCross(a, b, c, d)) { hit = true; break; }
                        for (var j = i + 2; j < loop.Count && !hit; j++)
                        {
                            if ((j + 1) % loop.Count == i) continue;
                            if (SegmentsCross(a, b, q[j], q[(j + 1) % loop.Count])) hit = true;
                        }
                        if (!hit) continue;
                        into.Add(loop[i]);
                        into.Add(loop[(i + 1) % loop.Count]);
                        found = true;
                    }
                    return found;
                }
                bool OverRelief(int t)
                {
                    foreach (var hole in g.holes) if (Over(hole, f, t)) return true;
                    return false;
                }
                g.removed = new List<int>(goneSet);
                if (!ok) { g.gap = null; g.outer = null; return; }
                g.outer = outer;
                var xs = new List<double>();
                var ys = new List<double>();
                var segments = new List<(int, int)>();
                double Loop(List<Vector2> points)
                {
                    var first = xs.Count;
                    double sum = 0;
                    for (var i = 0; i < points.Count; i++)
                    {
                        xs.Add(points[i].x); ys.Add(points[i].y);
                        segments.Add((first + i, first + (i + 1) % points.Count));
                        var q = points[(i + 1) % points.Count];
                        sum += 0.5 * ((double)points[i].x * q.y - (double)q.x * points[i].y);
                    }
                    return sum;
                }
                var ringShape = new List<Vector2>();
                foreach (var v in outer) ringShape.Add(f.To2(pp[v]));
                var gapArea = Loop(ringShape);
                foreach (var hole in g.holes)
                {
                    var shape = new List<Vector2>();
                    foreach (var q in hole.relief.outline) shape.Add(new Vector2(hole.relief.us[q.x], hole.relief.vs[q.y]));
                    foreach (var q in shape) if (!InsidePolygon(ringShape, q)) { g.gap = null; g.why = "outside"; return; }
                    gapArea -= Math.Abs(Loop(shape));
                }
                if (!(gapArea > 0) || !ConstrainedDelaunay.Triangulate(xs, ys, segments, out var gap)) { g.gap = null; g.why = gapArea > 0 ? "crossing" : "area"; return; }
                double filled = 0;
                for (var i = 0; i + 2 < gap.Count; i += 3)
                {
                    int a = gap[i], b = gap[i + 1], q = gap[i + 2];
                    filled += 0.5 * ((xs[b] - xs[a]) * (ys[q] - ys[a]) - (xs[q] - xs[a]) * (ys[b] - ys[a]));
                }
                if (Math.Abs(filled - gapArea) > 1e-3 * gapArea) { g.gap = null; g.why = "filled"; return; }
                g.gap = gap;
            }
            var again = false;
            foreach (var g in gapList)
                if (g.merge != null)
                {
                    joins.Add((indexOf[g.holes[0]], indexOf[g.merge]));
                    again = true;
                }
                else if (g.gap == null && g.blame != null)
                {
                    dropped[indexOf[g.blame]] = true;
                    again = true;
                }
            if (!again)
            {
                // Those left from a group, each tried alone (after the rest: where they meet
                // what those took, left).
                for (var i = 0; i < all.Count; i++)
                    if (dropped[i])
                        alone.Add(new Gap { wall = all[i].wall, frame = frames[all[i].wall], holes = { all[i].hole }, single = true });
                Parallel.For(0, alone.Count, BackgroundScheduler.Options, k =>
                {
                    StampAll(alone[k]);
                });
                break;
            }
            Group();
            }
            var result = new List<Gap>();
            var claimed = new bool[pm];
            gapList.AddRange(alone);
            foreach (var g in gapList)
            {
                // Grown into another's: left.
                if (g.gap != null)
                {
                    foreach (var t in g.removed) if (claimed[t]) { g.gap = null; g.why = "overlap"; break; }
                    if (g.gap != null) foreach (var t in g.removed) claimed[t] = true;
                    else g.outer = null;
                }
                if (g.gap == null)
                {
                    foreach (var hole in g.holes)
                    {
                        var r = hole.relief;
                        Left(g.wall < 0 ? "walls" : g.outer == null ? "ring " + g.why : "gap " + g.why, g.frame, r.us[0], r.vs[0], r.us[r.us.Count - 1], r.vs[r.vs.Count - 1]);
                    }
                    continue;
                }
                stats.reliefs += g.holes.Count;
                result.Add(g);
            }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in left) sb.Append(sb.Length > 0 ? ", " : "").Append(kv.Key).Append(' ').Append(kv.Value);
            stats.reliefLeft = sb.ToString();
            return result;
        }

        // Triangle a, b, c and the rectangle [x0, x1] x [y0, y1] overlap (separating axes).
        static bool Overlaps(Vector2 a, Vector2 b, Vector2 c, float x0, float y0, float x1, float y1)
        {
            if (Mathf.Max(a.x, Mathf.Max(b.x, c.x)) < x0 || Mathf.Min(a.x, Mathf.Min(b.x, c.x)) > x1) return false;
            if (Mathf.Max(a.y, Mathf.Max(b.y, c.y)) < y0 || Mathf.Min(a.y, Mathf.Min(b.y, c.y)) > y1) return false;
            var corners = new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };
            var tri = new[] { a, b, c };
            for (var i = 0; i < 3; i++)
            {
                var p = tri[i];
                var q = tri[(i + 1) % 3];
                var n = new Vector2(q.y - p.y, p.x - q.x);
                var other = Vector2.Dot(tri[(i + 2) % 3] - p, n);
                var separated = true;
                foreach (var k in corners)
                    if (Vector2.Dot(k - p, n) * Mathf.Sign(other) >= 0f) { separated = false; break; }
                if (separated) return false;
            }
            return true;
        }

        // Segments ab and cd cross (properly, not meeting at an end).
        static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float Side(Vector2 p, Vector2 q, Vector2 r) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
            float d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
            return (d1 > 0 && d2 < 0 || d1 < 0 && d2 > 0) && (d3 > 0 && d4 < 0 || d3 < 0 && d4 > 0);
        }

        static bool InsidePolygon(List<Vector2> polygon, Vector2 q)
        {
            var odd = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 p = polygon[i], r = polygon[j];
                if ((p.y > q.y) != (r.y > q.y) && q.x < p.x + (q.y - p.y) * (r.x - p.x) / (r.y - p.y)) odd = !odd;
            }
            return odd;
        }

        // A relief's points and quads, its outline's points those given.
        static List<int[]> Emit(Frame f, Hole hole, List<Vector3> points)
        {
            var r = hole.relief;
            var border = new Dictionary<Vector2Int, int>();
            for (var i = 0; i < r.outline.Count; i++) border[r.outline[i]] = hole.outlineIndex[i];
            return SteppedRelief.Quads(r,
                (i, j) => border.TryGetValue(new Vector2Int(i, j), out var v) ? v : throw new InvalidOperationException("relief outline point missing"),
                (q, z) => { points.Add(f.To3(q) + z * f.normal); return points.Count - 1; });
        }

        // Lines shared between regions. Each region's two directions are keys (2c: lines u = const,
        // 2c + 1: v = const); a border between two regions, along a direction both have, joins the
        // keys whose lines cross it when those run the same way. A joined set's lines are planes
        // across one world direction, merged from all its regions' corners and edges and spaced out
        // over all of them, and each region takes those over its own extent.
        sealed class Shared
        {
            public int[] group;
            public Vector3[] direction;
            public List<float>[] lines;
            public float[] min, max;

            public List<float> Lines(int c, int axis)
            {
                var k = 2 * c + axis;
                var mine = new List<float>();
                foreach (var x in lines[group[k]]) if (x >= min[k] && x <= max[k]) mine.Add(x);
                return mine;
            }

            // The parameters along a -> b where the region's lines along `axis` cross it.
            public void Crossings(int c, int axis, Vector3 a, Vector3 b, List<float> into)
            {
                var g = group[2 * c + axis];
                var d = direction[g];
                float xa = Vector3.Dot(a, d), xb = Vector3.Dot(b, d);
                var lo = Mathf.Min(xa, xb);
                var hi = Mathf.Max(xa, xb);
                if (!(hi > lo)) return;
                foreach (var x in lines[g])
                    if (x > lo && x < hi) into.Add((x - xa) / (xb - xa));
            }

            // Where the region's lines cross, in its frame: each a pair of world planes met in its own.
            public List<(Vector2 at, float clear)> Grid(int c, Frame f, float h)
            {
                var grid = new List<(Vector2, float)>();
                var ul = Lines(c, 0);
                var vl = Lines(c, 1);
                Vector3 a = direction[group[2 * c]], b = direction[group[2 * c + 1]];
                // x = (origin + u f.u + v f.v) . a, and x' the same with b.
                float m00 = Vector3.Dot(f.u, a), m01 = Vector3.Dot(f.v, a), m10 = Vector3.Dot(f.u, b), m11 = Vector3.Dot(f.v, b);
                var det = m00 * m11 - m01 * m10;
                if (!(Mathf.Abs(det) > 0.5f)) return grid;
                float oa = Vector3.Dot(f.origin, a), ob = Vector3.Dot(f.origin, b);
                for (var j = 0; j < vl.Count; j++)
                    for (var i = 0; i < ul.Count; i++)
                    {
                        float ra = ul[i] - oa, rb = vl[j] - ob;
                        var q = new Vector2((ra * m11 - m01 * rb) / det, (m00 * rb - m10 * ra) / det);
                        var gu = Mathf.Min(i > 0 ? ul[i] - ul[i - 1] : h, i + 1 < ul.Count ? ul[i + 1] - ul[i] : h);
                        var gv = Mathf.Min(j > 0 ? vl[j] - vl[j - 1] : h, j + 1 < vl.Count ? vl[j + 1] - vl[j] : h);
                        grid.Add((q, clearanceShare * Mathf.Min(gu, gv)));
                    }
                return grid;
            }
        }

        static Shared SharedLines(Vector3[] pp, int[] pt, int[] component, int components, bool[] owned, Frame[] frames,
            List<(int a, int b)>[] outline, Func<int, int, int> twin, Func<int, float> spacing, float tanAligned)
        {
            var keys = 2 * components;
            var group = new int[keys];
            for (var k = 0; k < keys; k++) group[k] = k;
            int Root(int k) { while (group[k] != k) k = group[k] = group[group[k]]; return k; }
            Vector3 Dir(int k) => (k & 1) == 0 ? frames[k >> 1].u : frames[k >> 1].v;
            var cosAligned = Mathf.Cos(Mathf.Atan(tanAligned));
            var cosShared = Mathf.Cos(sharedDegrees * Mathf.Deg2Rad);
            // The key of c whose lines cross an edge along d, or -1.
            int Across(int c, Vector3 d)
            {
                if (Mathf.Abs(Vector3.Dot(d, frames[c].u)) >= cosAligned) return 2 * c;
                if (Mathf.Abs(Vector3.Dot(d, frames[c].v)) >= cosAligned) return 2 * c + 1;
                return -1;
            }
            var pm = pt.Length / 3;
            for (var t = 0; t < pm; t++)
            {
                var c = component[t];
                if (c < 0 || owned[c]) continue;
                for (var e = 0; e < 3; e++)
                {
                    var u = twin(t, e);
                    if (u < 0) continue;
                    var c2 = component[u];
                    if (c2 < 0 || c2 <= c || owned[c2]) continue;
                    var d = pp[pt[3 * t + (e + 1) % 3]] - pp[pt[3 * t + e]];
                    var len = d.magnitude;
                    if (!(len > 0f)) continue;
                    d /= len;
                    int k1 = Across(c, d), k2 = Across(c2, d);
                    if (k1 < 0 || k2 < 0 || Mathf.Abs(Vector3.Dot(Dir(k1), Dir(k2))) < cosShared) continue;
                    group[Root(k1)] = Root(k2);
                }
            }
            var shared = new Shared
            {
                group = new int[keys], direction = new Vector3[keys], lines = new List<float>[keys],
                min = new float[keys], max = new float[keys],
            };
            for (var k = 0; k < keys; k++) shared.group[k] = Root(k);
            for (var k = 0; k < keys; k++) if (shared.group[k] == k) shared.direction[k] = Dir(k);
            // Each key's candidates (as Frame.Lines finds them), in its set's world coordinate.
            var at = new List<(float at, float weight)>[keys];
            for (var k = 0; k < keys; k++)
            {
                var c = k >> 1;
                shared.min[k] = float.MaxValue;
                shared.max[k] = float.MinValue;
                if (owned[c] || outline[c].Count == 0) continue;
                var g = shared.group[k];
                if (at[g] == null) at[g] = new List<(float, float)>();
                var list = at[g];
                var dir = shared.direction[g];
                var f = frames[c];
                var degree = new Dictionary<int, List<Vector2>>();
                foreach (var (a, b) in outline[c])
                {
                    float xa = Vector3.Dot(pp[a], dir), xb = Vector3.Dot(pp[b], dir);
                    shared.min[k] = Mathf.Min(shared.min[k], Mathf.Min(xa, xb));
                    shared.max[k] = Mathf.Max(shared.max[k], Mathf.Max(xa, xb));
                    Vector2 qa = f.To2(pp[a]), qb = f.To2(pp[b]);
                    var d = qb - qa;
                    var len = d.magnitude;
                    if (!(len > 0f)) continue;
                    // An edge along the other direction is one of these lines where it runs.
                    var other = (k & 1) == 0 ? Mathf.Abs(d.x) <= tanAligned * Mathf.Abs(d.y) : Mathf.Abs(d.y) <= tanAligned * Mathf.Abs(d.x);
                    if (other) list.Add((0.5f * (xa + xb), len));
                    (degree.TryGetValue(a, out var la) ? la : degree[a] = new List<Vector2>(2)).Add(d / len);
                    (degree.TryGetValue(b, out var lb) ? lb : degree[b] = new List<Vector2>(2)).Add(-d / len);
                }
                foreach (var kv in degree)
                {
                    var dirs = kv.Value;
                    if (dirs.Count != 2 || Vector2.Dot(dirs[0], -dirs[1]) < 0.94f) list.Add((Vector3.Dot(pp[kv.Key], dir), 0f));
                }
                list.Add((shared.min[k], 0f));
                list.Add((shared.max[k], 0f));
            }
            for (var g = 0; g < keys; g++)
            {
                if (at[g] == null) continue;
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var (x, _) in at[g]) { lo = Mathf.Min(lo, x); hi = Mathf.Max(hi, x); }
                // (Spaced as the widest its regions are.)
                var h = 0f;
                for (var k = 0; k < keys; k++) if (shared.group[k] == g) h = Mathf.Max(h, spacing(k / 2));
                shared.lines[g] = Frame.Merge(at[g], lo, hi, h);
            }
            for (var k = 0; k < keys; k++) if (shared.lines[shared.group[k]] == null) shared.lines[shared.group[k]] = new List<float>();
            return shared;
        }

        // A region's plane coordinates: u along its outline's main direction, v across, u x v its normal.
        sealed class Frame
        {
            public Vector3 normal, u, v, origin;
            public List<float> us, vs;
            public List<Vector2> grid;
            public int failure;

            public Vector2 To2(Vector3 p)
            {
                var d = p - origin;
                return new Vector2(Vector3.Dot(d, u), Vector3.Dot(d, v));
            }

            public Vector3 To3(Vector2 q) => origin + q.x * u + q.y * v;

            public static Frame For(Vector3 normal, float offset, List<(int a, int b)> outline, Vector3[] p)
            {
                var f = new Frame { normal = normal, origin = normal * offset };
                var a0 = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right);
                a0 /= a0.magnitude;
                var b0 = Vector3.Cross(normal, a0);
                // The outline's direction, modulo a right angle, length-weighted to the degree.
                var bins = new double[90];
                foreach (var (a, b) in outline)
                {
                    var d = p[b] - p[a];
                    float x = Vector3.Dot(d, a0), y = Vector3.Dot(d, b0);
                    var len = Mathf.Sqrt(x * x + y * y);
                    if (!(len > 0f)) continue;
                    var angle = Mathf.Atan2(y, x) * Mathf.Rad2Deg;
                    angle = ((angle % 90f) + 90f) % 90f;
                    bins[Mathf.Clamp((int)angle, 0, 89)] += len;
                }
                var best = 0;
                var bestSum = -1.0;
                for (var i = 0; i < 90; i++)
                {
                    var s = bins[(i + 89) % 90] + bins[i] + bins[(i + 1) % 90];
                    if (s > bestSum) { bestSum = s; best = i; }
                }
                // Refined: the length-weighted mean angle near the peak.
                double cs = 0, sn = 0;
                foreach (var (a, b) in outline)
                {
                    var d = p[b] - p[a];
                    float x = Vector3.Dot(d, a0), y = Vector3.Dot(d, b0);
                    var len = Mathf.Sqrt(x * x + y * y);
                    if (!(len > 0f)) continue;
                    var angle = Mathf.Atan2(y, x) * Mathf.Rad2Deg;
                    angle = ((angle % 90f) + 90f) % 90f;
                    var off = Mathf.DeltaAngle(angle * 4f, (best + 0.5f) * 4f) / 4f;
                    if (Mathf.Abs(off) > 2f) continue;
                    var r = angle * 4f * Mathf.Deg2Rad;
                    cs += len * Math.Cos(r); sn += len * Math.Sin(r);
                }
                var theta = (float)(Math.Atan2(sn, cs) / 4.0);
                f.u = Mathf.Cos(theta) * a0 + Mathf.Sin(theta) * b0;
                f.u /= f.u.magnitude;
                f.v = Vector3.Cross(normal, f.u);
                return f;
            }

            // Lines: through the outline's corners and along its edges in either direction,
            // merged when close, more between where they are far apart.
            public void Lines(List<(int a, int b)> outline, Vector3[] p, float h, float tanAligned)
            {
                var uAt = new List<(float at, float weight)>();
                var vAt = new List<(float at, float weight)>();
                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                var degree = new Dictionary<int, List<Vector2>>();
                foreach (var (a, b) in outline)
                {
                    Vector2 qa = To2(p[a]), qb = To2(p[b]);
                    minU = Mathf.Min(minU, Mathf.Min(qa.x, qb.x)); maxU = Mathf.Max(maxU, Mathf.Max(qa.x, qb.x));
                    minV = Mathf.Min(minV, Mathf.Min(qa.y, qb.y)); maxV = Mathf.Max(maxV, Mathf.Max(qa.y, qb.y));
                    var d = qb - qa;
                    var len = d.magnitude;
                    if (!(len > 0f)) continue;
                    // An edge along v is a u-line where it runs (and the other way round).
                    if (Mathf.Abs(d.x) <= tanAligned * Mathf.Abs(d.y)) uAt.Add((0.5f * (qa.x + qb.x), len));
                    if (Mathf.Abs(d.y) <= tanAligned * Mathf.Abs(d.x)) vAt.Add((0.5f * (qa.y + qb.y), len));
                    (degree.TryGetValue(a, out var la) ? la : degree[a] = new List<Vector2>(2)).Add(d / len);
                    (degree.TryGetValue(b, out var lb) ? lb : degree[b] = new List<Vector2>(2)).Add(-d / len);
                }
                // Corners: where the outline turns, or meets itself.
                foreach (var kv in degree)
                {
                    var dirs = kv.Value;
                    var corner = dirs.Count != 2 || Vector2.Dot(dirs[0], -dirs[1]) < 0.94f;
                    if (!corner) continue;
                    var q = To2(p[kv.Key]);
                    uAt.Add((q.x, 0f));
                    vAt.Add((q.y, 0f));
                }
                us = Merge(uAt, minU, maxU, h);
                vs = Merge(vAt, minV, maxV, h);
            }

            public static List<float> Merge(List<(float at, float weight)> at, float min, float max, float h)
            {
                at.Add((min, 0f));
                at.Add((max, 0f));
                at.Sort((x, y) => x.at.CompareTo(y.at));
                var lines = new List<float>();
                var close = lineMergeShare * h;
                var i = 0;
                while (i < at.Count)
                {
                    // A run of values each within `close` of the last: the longest edge's place, else the mean.
                    var j = i;
                    double sum = 0;
                    var bestWeight = -1f;
                    var best = at[i].at;
                    while (j < at.Count && (j == i || at[j].at - at[j - 1].at < close) && at[j].at - at[i].at < 2f * close)
                    {
                        sum += at[j].at;
                        if (at[j].weight > bestWeight) { bestWeight = at[j].weight; best = at[j].at; }
                        j++;
                    }
                    lines.Add(bestWeight > 0f ? best : (float)(sum / (j - i)));
                    i = j;
                }
                var filled = new List<float>();
                for (var k = 0; k < lines.Count; k++)
                {
                    filled.Add(lines[k]);
                    if (k + 1 == lines.Count) break;
                    var gap = lines[k + 1] - lines[k];
                    var steps = Mathf.RoundToInt(gap / h);
                    for (var s = 1; s < steps; s++) filled.Add(lines[k] + gap * s / steps);
                }
                return filled;
            }

            // The lines' crossings, each with how far clear of the outline it must be.
            public List<(Vector2 at, float clear)> Grid(float h)
            {
                var grid = new List<(Vector2, float)>();
                for (var j = 0; j < vs.Count; j++)
                    for (var i = 0; i < us.Count; i++)
                    {
                        var gu = Mathf.Min(i > 0 ? us[i] - us[i - 1] : h, i + 1 < us.Count ? us[i + 1] - us[i] : h);
                        var gv = Mathf.Min(j > 0 ? vs[j] - vs[j - 1] : h, j + 1 < vs.Count ? vs[j + 1] - vs[j] : h);
                        grid.Add((new Vector2(us[i], vs[j]), clearanceShare * Mathf.Min(gu, gv)));
                    }
                return grid;
            }

            // The parameters along a -> b (as coordinates from x0 to x1) where lines cross it.
            public void Crossings(List<float> lines, float x0, float x1, List<float> into)
            {
                var lo = Mathf.Min(x0, x1);
                var hi = Mathf.Max(x0, x1);
                if (!(hi > lo)) return;
                foreach (var l in lines)
                    if (l > lo && l < hi) into.Add((l - x0) / (x1 - x0));
            }
        }

        // The region's triangles: its simplified triangles with their outline cut, the lines'
        // crossings inserted where they fall clear of the outline, then flipped to Delaunay with
        // the outline held (a wall's own triangles, so no outline need be a clean polygon: slits
        // and pinches where it touches itself stay as they are). Indices >= 0 are points; -1 - i
        // is the region's i-th grid point. Null when it cannot be done.
        static List<int> Fill(Frame frame, List<int> region, int[] pt, List<(int a, int b)> outline, List<Vector3> points,
            Action<int, int, List<int>> walk, List<(Vector2 at, float clear)> wanted, out List<Vector2> grid, out int failure)
        {
            failure = 0;
            grid = new List<Vector2>();
            var ids = new List<int>();
            var index = new Dictionary<int, int>();
            var xs = new List<double>();
            var ys = new List<double>();
            int Local(int global)
            {
                if (index.TryGetValue(global, out var l)) return l;
                l = ids.Count;
                index[global] = l;
                ids.Add(global);
                var q = frame.To2(points[global]);
                xs.Add(q.x); ys.Add(q.y);
                return l;
            }
            // The simplified triangles, each with its edges' cuts.
            var triangles = new List<int>(6 * region.Count);
            var corners = new List<int>(12);
            var sideStart = new int[3];
            // Points made in the middle of triangles cut on several sides: local, with no global index.
            var middles = new List<int>();
            int Middle(List<int> around)
            {
                double mx = 0, my = 0;
                foreach (var l in around) { mx += xs[l]; my += ys[l]; }
                var m = ids.Count;
                ids.Add(-1);
                xs.Add(mx / around.Count); ys.Add(my / around.Count);
                middles.Add(m);
                return m;
            }
            var around = new List<int>(12);
            foreach (var t in region)
            {
                corners.Clear();
                for (var side = 0; side < 3; side++)
                {
                    sideStart[side] = corners.Count;
                    walk(pt[3 * t + side], pt[3 * t + (side + 1) % 3], corners);
                }
                around.Clear();
                foreach (var g in corners) around.Add(Local(g));
                CutTriangle(around, sideStart, Middle, triangles);
            }
            // The outline, cut: held.
            var fixedEdges = new HashSet<long>();
            var segs2 = new List<(Vector2 a, Vector2 b)>();
            var run = new List<int>();
            foreach (var (a, b) in outline)
            {
                run.Clear();
                walk(a, b, run);
                run.Add(b);
                for (var i = 0; i + 1 < run.Count; i++)
                {
                    int la = Local(run[i]), lb = Local(run[i + 1]);
                    fixedEdges.Add(la < lb ? ((long)la << 32) | (uint)lb : ((long)lb << 32) | (uint)la);
                    segs2.Add((new Vector2((float)xs[la], (float)ys[la]), new Vector2((float)xs[lb], (float)ys[lb])));
                }
            }
            var first = ids.Count;
            // Grid points: the lines' crossings clear of the outline.
            var candidates = new List<Vector2>();
            foreach (var (q, clear) in wanted)
            {
                if (!Clear(q, segs2, clear)) continue;
                candidates.Add(q);
                xs.Add(q.x); ys.Add(q.y);
            }
            if (!ConstrainedDelaunay.Refine(xs, ys, triangles, fixedEdges, first, out var inserted))
            {
                failure = 2;
                debugFailed?.Invoke(xs, ys, new List<(int, int)>(), 2);
                return null;
            }
            // Grid points numbered as inserted.
            var gridIndex = new int[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
            {
                gridIndex[i] = inserted[i] ? grid.Count : -1;
                if (inserted[i]) grid.Add(candidates[i]);
            }
            var middleIndex = new Dictionary<int, int>();
            foreach (var m in middles)
            {
                middleIndex[m] = grid.Count;
                grid.Add(new Vector2((float)xs[m], (float)ys[m]));
            }
            var result = new List<int>(triangles.Count);
            foreach (var l in triangles)
            {
                if (l < first)
                {
                    result.Add(ids[l] >= 0 ? ids[l] : -1 - middleIndex[l]);
                    continue;
                }
                var g = gridIndex[l - first];
                if (g < 0) { failure = 2; return null; }
                result.Add(-1 - g);
            }
            return result;
        }

        // A triangle with points along its sides (`around`: each side from its corner, sides
        // starting at sideStart) into triangles: cut on one side, a fan from the corner across;
        // on more, a fan from a point in the middle (middle() makes it).
        static void CutTriangle(List<int> around, int[] sideStart, Func<List<int>, int> middle, List<int> output)
        {
            var n = around.Count;
            if (n == 3) { output.Add(around[0]); output.Add(around[1]); output.Add(around[2]); return; }
            int SideEnd(int side) => side == 2 ? n : sideStart[side + 1];
            var cut = -1;
            var cutSides = 0;
            for (var side = 0; side < 3; side++)
                if (SideEnd(side) - sideStart[side] > 1) { cut = side; cutSides++; }
            if (cutSides == 1)
            {
                var apex = around[sideStart[(cut + 2) % 3]];
                for (var i = sideStart[cut]; i < SideEnd(cut); i++)
                {
                    output.Add(around[i]); output.Add(around[(i + 1) % n]); output.Add(apex);
                }
                return;
            }
            var m = middle(around);
            for (var i = 0; i < n; i++) { output.Add(around[i]); output.Add(around[(i + 1) % n]); output.Add(m); }
        }

        // At least `clear` from every segment.
        static bool Clear(Vector2 q, List<(Vector2 a, Vector2 b)> segments, float clear)
        {
            var clear2 = clear * clear;
            foreach (var (a, b) in segments)
            {
                var ab = b - a;
                var len2 = ab.sqrMagnitude;
                var s = len2 > 0f ? Mathf.Clamp01(Vector2.Dot(q - a, ab) / len2) : 0f;
                if ((a + s * ab - q).sqrMagnitude < clear2) return false;
            }
            return true;
        }

        // Points no face uses (the simplified regions' insides, filled again) dropped.
        static void Compact(ref Vector3[] positions, List<int[]> faces)
        {
            var map = new int[positions.Length];
            for (var i = 0; i < map.Length; i++) map[i] = -1;
            var kept = new List<Vector3>(positions.Length);
            foreach (var f in faces)
                for (var k = 0; k < f.Length; k++)
                {
                    var v = f[k];
                    if (map[v] < 0) { map[v] = kept.Count; kept.Add(positions[v]); }
                    f[k] = map[v];
                }
            positions = kept.ToArray();
        }

        // One Catmull-Clark topology step; a new point is exact when the points it is made of are.
        static void Split(Vector3[] basePositions, List<int[]> baseFaces, bool[] baseExact, out Vector3[] positions,
            out List<int[]> faces, out bool[] exact)
        {
            var list = new List<Vector3>(basePositions);
            var flags = new List<bool>(baseExact);
            var mid = new Dictionary<long, int>(Extraction.EdgeKey.Comparer);
            faces = new List<int[]>(4 * baseFaces.Count);
            int Mid(int a, int b)
            {
                var key = Extraction.EdgeKey.PackUndirected(a, b);
                if (mid.TryGetValue(key, out var m)) return m;
                m = list.Count;
                list.Add(0.5f * (basePositions[a] + basePositions[b]));
                flags.Add(baseExact[a] && baseExact[b]);
                mid[key] = m;
                return m;
            }
            foreach (var face in baseFaces)
            {
                var n = face.Length;
                var centre = Vector3.zero;
                var all = true;
                foreach (var v in face) { centre += basePositions[v]; all &= baseExact[v]; }
                var c = list.Count;
                list.Add(centre / n);
                flags.Add(all);
                var mids = new int[n];
                for (var i = 0; i < n; i++) mids[i] = Mid(face[i], face[(i + 1) % n]);
                for (var i = 0; i < n; i++)
                    faces.Add(new[] { face[i], mids[i], c, mids[(i + n - 1) % n] });
            }
            positions = list.ToArray();
            exact = flags.ToArray();
        }
    }
}
