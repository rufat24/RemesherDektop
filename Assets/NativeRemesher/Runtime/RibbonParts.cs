using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    /// <summary>
    /// Thin free-standing bands of the input -- straps, belts, flat cables -- recognised on the
    /// input and meshed on their own, as a swept rectangular tube a few quads round and about a
    /// cell per quad along; the rest of the input is solved without them.
    /// </summary>
    /// <remarks>
    /// The helmet head's chin straps are bands about two cells wide and a quarter of a cell thick,
    /// standing off the cheek. The decimated mesh keeps them as tubes, but the quad layout cuts
    /// across them -- an isotropic lattice has no row a quarter of a cell wide for the rims -- and
    /// the strap comes out a sliver melted into the face (2026-10-17). A strap wants a rectangle
    /// swept along it: four corners round, long quads.
    ///
    /// Recognised from the shape itself, as a thickness map would show it: every input vertex casts
    /// inward along its normal; where the far side of the part is within maxThicknessCells, the
    /// vertex is thin. Connected thin faces are one side of a plate, and the side its rays land on
    /// is the other. The midpoints between the two sides make the plate's middle sheet; its centre
    /// line runs through them, ordered by distance along the sheet, with the plate's normal (or, for
    /// a round cable, a frame carried along the line) for the frame. A plate is cut into runs where
    /// its width or direction changes -- two straps through a buckle make one L-shaped plate -- and
    /// a run is a ribbon when it is long, narrow, and free along both long edges (its own rim faces
    /// out beside it): a helmet brim, thin but attached along its length, is not. Each ring of the
    /// tube is cut from the part itself, its corners the section's extreme points.
    ///
    /// Its faces (both sides, and the rims: within three quarters of the thickness of the middle
    /// sheet) are taken out of the input, and the holes left where it met the rest -- its cut
    /// section, long and thin -- zipped shut; an end that runs into another part is pushed a little
    /// into it, a free end is capped. The tubes are made at the edge length the rest came out at.
    ///
    /// Measured on the helmet head at 3,000 (2026-10-17): nine runs found -- the chin straps both
    /// sides, upright and along the jaw, the two straps on the back of the helmet, the two at the
    /// nape -- 448 quads of tube; irregular 3.40 -> 3.56 %, chamfer 0.00104 -> 0.00101. The body and
    /// the ccnb scan have no free band: nothing found, output unchanged.
    ///
    /// Second pass (2026-10-19), after "the buckle is a blob, and it looks ugly":
    /// - Raised bands (reliefBands): the back straps lie on the helmet, fused to it, and the inward
    ///   ray finds no far side; only where one bridged a step was it thin, so it came out a 5-cell
    ///   piece with torn ends. Now a band's top is found by rays going out sideways just under it
    ///   (walls near both ways, square to the ray, long the other way) with ground beside it; its
    ///   height stands for the thickness: a closed rectangular tube lying on an unbroken helmet
    ///   surface.
    /// - Runs against their own median width, and no cut where a strap folds over a rim.
    /// - Forks (splitForks): two straps leaving a buckle side by side fill one distance bin, and
    ///   one line through them read 6 cells wide; the plate is cut at the first fork that lasts
    ///   a cell and a half and each piece measured alone. The far side's chin straps came back.
    /// - Tubes are regular boxes: the median of the part's own sections' extents along a centre
    ///   line whose last cell and a half each end is straightened (readSections: the old rings).
    /// - Buckles (buckleBlocks): where attached ends of two ribbons meet, the surface round the
    ///   spot is fitted (a quadric, refitted without what stands above it) and the buckle's top
    ///   (facing up, a fifth of a cell above it) boxed: the smallest rectangle over 5-degree
    ///   steps, its top the median height, its bottom into the surface; held to 1.2 cells high
    ///   and 3 across, or no box.
    /// Measured on the head at 3,000: eight ribbons (both back straps whole, 13.5 cells; jaw and
    /// upright chin straps both sides; a piece of the band under the chin) and two buckles;
    /// irregular 3.40 -> 3.42 %, chamfer 0.00104 -> 0.00100; hausdorff 0.0129 -> 0.0191, all of
    /// it the buckle boxes' and tube ends' parts inside the face. Body and ccnb: nothing found.
    /// Detection 2.6-4 s on 1.5M triangles.
    ///
    /// Sliders (linkSliders, 2026-10-19): a slider is thicker than its strap, so the strap's plate
    /// stops at it and the piece out of the other side is a plate of its own, too short to pass.
    /// Such pieces are kept as candidates; an attached end is joined to one (or to another ribbon)
    /// that lies ahead within 4 cells, in line, at a like width -- never where a third ribbon ends
    /// (a buckle). One tube runs through; the slider is boxed in the strap's frame from the source
    /// facing away from the strap (the cheek beside it faces towards it), its length where the
    /// part stands out of the strap's section; the tube inside the box is dropped. Head: both
    /// side straps through their top sliders on up to the helmet, both jaw straps through theirs
    /// round the neck; irregular 3.53 %, chamfer 0.00101.
    ///
    /// Turned models (invariantRelief, 2026-10-19): the raised-band test leaned on the world axes
    /// (world voxels, four rays round a world-axis tangent), and the head turned found 13 straps
    /// against 8. Its samples are now picked by distance on the mesh and the way across searched
    /// for; the head in four poses finds 10 / 9 / 10 / 9, thin and raised vertices alike to 0.2 %.
    /// The chin cup (a strap loop round the chin) was tried and is off: see the flags from
    /// fillThinHoles to flattenLying and round 26 of the benchmark notes.
    /// </remarks>
    public static class RibbonParts
    {
        /// <summary>A plate is thin below this many cells through.</summary>
        public static float maxThicknessCells = 0.6f;

        /// <summary>Shortest ribbon, along its centre line, in cells.</summary>
        public static float minLengthCells = 3f;

        /// <summary>Widest ribbon, median over its length, in cells.</summary>
        public static float maxWidthCells = 4f;

        /// <summary>Least length over median width.</summary>
        public static float minAspect = 2.5f;

        /// <summary>Least fraction of the rim samples with air beside them.</summary>
        public static float minFreeRimFraction = 0.75f;

        /// <summary>A run of a plate ends where its width leaves the plate's median by more than this fraction.</summary>
        public static float runWidthTolerance = 0.25f;

        /// <summary>Spacing of the cross-sections along a ribbon, in cells.</summary>
        public static float stationSpacingCells = 1f;

        /// <summary>Quads across a broad side: the width over this many cells, rounded, at least one.</summary>
        public static float quadWidthCells = 1.75f;

        /// <summary>How far an end that meets another part is pushed into it, in cells.</summary>
        public static float embedCells = 0.5f;

        /// <summary>
        /// Raised bands count too: a strap lying on the surface and fused to it, with no far side
        /// for the inward ray to find. Its top is narrow just under the surface (the walls are
        /// near, sideways), long the other way, and there is ground beside it within
        /// maxThicknessCells below; its height over that ground stands in for the thickness.
        /// </summary>
        public static bool reliefBands = true;

        /// <summary>
        /// Widest raised band, across just under its top, in cells. A wider one gets three quads
        /// or more across from the lattice and keeps its shape there (the helmet's back panels,
        /// about two cells, do); the straps on them, about 1.3, do not.
        /// </summary>
        public static float maxReliefWidthCells = 1.75f;

        /// <summary>
        /// A raised band needs ground beside only one of its walls. False, both: measured worse on
        /// the helmet head (2026-10-19: hausdorff 0.018 -> 0.040, eight buckles where two straps
        /// ended near each other, most of them none).
        /// </summary>
        public static bool oneSidedGround = true;

        /// <summary>The walls of a raised band face out square to the ray across it: the cosine at least.</summary>
        public static float wallSquareness = 0.6f;

        /// <summary>
        /// The raised-band test does not depend on how the model is turned: its sample points are
        /// picked by distance on the mesh (not world voxels and world-axis normal buckets), and the
        /// direction across a band is searched for (eight rays, then refined) rather than taken from
        /// four rays round a world-axis tangent. False: the 2026-10-19 test, which found 8 straps on
        /// the helmet head and 13 on the same head turned.
        /// </summary>
        public static bool invariantRelief = true;

        /// <summary>
        /// Small patches the thickness tests missed inside a thin part (a bump on a sculpted strap
        /// stops a ray) take the thickness of the thin part round them: a patch of at most
        /// maxThinHoleCells square cells, every vertex round it thin, none on an open edge.
        /// Off: tried for the helmet head's chin cup (2026-10-19) and measured worse on the head as a whole
        /// (see Benchmarks/round5-parametrization.md, round 26).
        /// </summary>
        public static bool fillThinHoles = false;

        /// <summary>Largest patch filled, in square cells.</summary>
        public static float maxThinHoleCells = 1f;

        /// <summary>
        /// The rays across a raised band start at several depths under its top (0.04, 0.1 and 0.2
        /// cells), the narrowest taken: a bump on a sculpted top catches the shallowest.
        /// Off: tried for the helmet head's chin cup (2026-10-19) and measured worse on the head as a whole
        /// (see Benchmarks/round5-parametrization.md, round 26).
        /// </summary>
        public static bool reliefDepths = false;

        /// <summary>Narrowest ribbon, median width in cells: below it a crease, not a band.</summary>
        public static float minWidthCells = 0.4f;

        /// <summary>
        /// A run ends where the line turns: where the tangents half a cell before and after a
        /// point meet at a cosine below this (-1.1: never; a strap folding over a rim stays one).
        /// </summary>
        public static float runStraightness = -1.1f;

        /// <summary>
        /// Rings read off the part's own sections (the 2026-10-17 tubes) rather than a regular
        /// rectangle of the ribbon's median width and thickness on its smoothed centre line.
        /// </summary>
        public static bool readSections = false;

        /// <summary>
        /// No bottom faces on tubes lying on the surface and on buckle boxes: they are inside it.
        /// (Closed, they took 45,447 missed texels in the head's 2048 bake, 2026-10-19.)
        /// </summary>
        public static bool openBottoms = true;

        /// <summary>How far the bottom of a band lying on the surface goes into it, in cells.</summary>
        public static float lyingEmbedCells = 0.1f;

        /// <summary>
        /// Where two ribbons' attached ends meet (a buckle), the part standing off the surface there
        /// is taken out too and meshed as a box: its footprint and height fitted to what stands
        /// above the surface round it. Only with the overloads that take a block list.
        /// </summary>
        public static bool buckleBlocks = true;

        /// <summary>
        /// A single attached end gets a box too when one fits there (a slider a strap runs
        /// through): the same fit, held to the same height and size.
        /// </summary>
        public static bool singleEndBlocks = false;

        /// <summary>Attached ends this close, in cells, meet at one buckle.</summary>
        public static float junctionRadiusCells = 3f;

        /// <summary>Tallest buckle, surface to top (with the embedding), in cells.</summary>
        public static float maxBlockHeightCells = 1.2f;

        /// <summary>Longest buckle side, in cells.</summary>
        public static float maxBlockSizeCells = 3f;

        /// <summary>The buckle is looked for within this many cells of where the ends meet.</summary>
        public static float blockRadiusCells = 1.5f;

        /// <summary>
        /// Thin parts are cut where they are not band-shaped before their lines are followed: round
        /// each patch of the middle sheet, the spread of the sheet within bandRadiusCells must be
        /// narrow one way. A buckle where straps meet, or a broad plate a strap runs into, is not,
        /// and goes; the straps either side come apart. One line through a branched plate mixes
        /// its branches (the far chin strap, jaw strap and chin cup were one plate, 2026-10-19).
        /// </summary>
        public static bool splitBranches = false;

        /// <summary>Radius the sheet's local spread is measured over, in cells.</summary>
        public static float bandRadiusCells = 2f;

        /// <summary>Widest local spread (as a band's width, sqrt(12 var)) still a band, in cells.</summary>
        public static float maxBandSpreadCells = 2.6f;

        /// <summary>
        /// A plate whose line forks (two straps leaving a buckle side by side) is cut at the fork
        /// and each piece measured on its own; false: one line through the whole plate.
        /// </summary>
        public static bool splitForks = true;

        /// <summary>
        /// Least share of a distance bin's points an arm of a fork needs; 0.05 was tried (2026-10-19):
        /// A thin arm beside a broad one -- the lip of a strap closing a ring with a broad band --
        /// holds far less than a fifth; the three bins in a row it must last keep out stray patches.
        /// Off: tried for the helmet head's chin cup (2026-10-19) and measured worse on the head as a whole
        /// (see Benchmarks/round5-parametrization.md, round 26).
        /// </summary>
        public static float forkArmFraction = 0.2f;

        /// <summary>
        /// On a band lying on the surface (no far side found for most of it), a rim may be found just
        /// under its faces (0.04 and 0.1 cells, as deep as the raised-band test looks), not only at
        /// mid-height: one with a groove beside it shallower than half its height has its rim near
        /// the top only. Plates with two sides keep the mid-height test (a thick hem's step down to
        /// the sheet it edges read as a rim there, 2026-10-19).
        /// Off: tried for the helmet head's chin cup (2026-10-19) and measured worse on the head as a whole
        /// (see Benchmarks/round5-parametrization.md, round 26).
        /// </summary>
        public static bool rimHeights = false;

        /// <summary>
        /// A plate's normal at each station from its faces' normals only (within 45 degrees of the
        /// first estimate), not the walls and rims curling under: those tilted a raised band's frame
        /// by 20 degrees, and the rays across it then passed under one rim.
        /// Off: tried for the helmet head's chin cup (2026-10-19) and measured worse on the head as a whole
        /// (see Benchmarks/round5-parametrization.md, round 26).
        /// </summary>
        public static bool robustPlateNormal = false;

        /// <summary>Share of a run's vertices with no far side that makes it a raised band for rimHeights.</summary>
        public static float lyingRunFraction = 0.5f;

        /// <summary>
        /// A run along an open edge of the mesh (a hem round the neck hole) is left alone: with its
        /// faces out, nothing is left on that side to close the hole against. Open: a quarter of its
        /// sections or more hold a vertex on the open edge.
        /// Off: tried for the helmet head's chin cup (2026-10-19) and measured worse on the head as a whole
        /// (see Benchmarks/round5-parametrization.md, round 26).
        /// </summary>
        public static bool skipOpenEdges = false;

        /// <summary>
        /// Where the faces taken out leave a ring-shaped hole (two loops), the loops are stitched to each other.
        /// Off: tried for the helmet head's chin cup (2026-10-19) and measured worse on the head as a whole
        /// (see Benchmarks/round5-parametrization.md, round 26).
        /// </summary>
        public static bool stitchRings = false;

        /// <summary>
        /// The faces taken out for ribbons lying on the surface (with the buckles and shreds joined
        /// to them) are kept instead, and pressed flat: each inner vertex moved to its neighbours'
        /// mean until the patch is a membrane over the ground the band stood on, its rim held. Cut
        /// out, crossing straps leave holes with many loops (the patches between them), which no
        /// closing of one loop at a time gets right. Free-standing ribbons are still cut out.
        /// Off: tried for the helmet head's chin cup (2026-10-19) and measured worse on the head as a whole
        /// (see Benchmarks/round5-parametrization.md, round 26).
        /// </summary>
        public static bool flattenLying = false;

        /// <summary>Relaxation sweeps pressing the lying ribbons' faces flat.</summary>
        public static int flattenIterations = 300;

        /// <summary>
        /// Straps through sliders: a slider (a small frame a strap runs through) is thicker than the
        /// strap, so the strap's plate stops at it and the piece beyond is a plate of its own, too
        /// short or too attached to pass alone. An attached end is joined to a strap or such a piece
        /// that goes on in line beyond a short gap, at a like width; the tube runs through, and the
        /// slider is boxed round it (its faces taken out).
        /// </summary>
        public static bool linkSliders = true;

        /// <summary>Longest gap between the ends a slider joins, in cells (past 2.5, both in line within 32 degrees).</summary>
        public static float sliderGapCells = 4f;

        /// <summary>Most a continuing piece's width may differ from the strap's, as a ratio.</summary>
        public static float sliderWidthRatio = 1.4f;

        /// <summary>A piece too short to pass alone may continue a strap even when attached along it.</summary>
        public static bool continueAttached = true;

        /// <summary>Shortest piece a strap is continued by, in cells.</summary>
        public static float minContinuationCells = 1f;

        /// <summary>Fewest thin faces a side needs to be considered at all.</summary>
        public static int minSideFaces = 24;

        /// <summary>Diagnostics: when set, one line per plate measured -- where, how long, how wide, and the verdict.</summary>
        public static List<string> debugLog;

        /// <summary>Diagnostics: when set, the corners of every thin face, three per face.</summary>
        public static List<Vector3> debugThinFaces;

        /// <summary>Diagnostics: when set, each measured piece's middle-sheet points, tagged with the piece's number in the log.</summary>
        public static List<(int piece, Vector3 point)> debugPieces;

        static int s_PieceCount;

        /// <summary>Diagnostics: when set, the input as the rest is solved on -- ribbons out, holes closed.</summary>
        public static Action<Vector3[], int[]> debugKept;

        static void Log(string verdict, Vector3 at, float length, float width, float free, float cell)
        {
            if (debugLog == null) return;
            lock (debugLog)
                debugLog.Add($"{verdict} at ({at.x:F3},{at.y:F3},{at.z:F3}) length {length / cell:F1} width {width / cell:F2} free {free:F2} cells");
        }

        public sealed class Stats
        {
            public int thinVertices, reliefVertices, unbandedVertices, filledVertices, flattenedTriangles, sides, pairs, ribbons, links, sliders;
            public int rejectedShort, rejectedWide, rejectedStubby, rejectedAttached;
            public int removedTriangles, holes, fillTriangles, quads, shredTriangles;
            public long ms;

            public override string ToString() =>
                $"ribbons={ribbons} of {pairs} plates ({sides} sides, {thinVertices} thin vertices, {reliefVertices} raised, {unbandedVertices} off bands, {filledVertices} filled, {flattenedTriangles} flattened) " +
                $"rejected short {rejectedShort} wide {rejectedWide} stubby {rejectedStubby} attached {rejectedAttached} | " +
                $"removed {removedTriangles} tris ({shredTriangles} in shreds), {holes} holes closed with {fillTriangles} | links {links} sliders {sliders} | quads {quads} | {ms} ms";
        }

        /// <summary>One recognised band: its centre line and the cross-section along it.</summary>
        public sealed class Ribbon
        {
            public Vector3[] centre;
            public Vector3[] up;
            public float[] width;
            public float[] thickness;
            public bool freeStart, freeEnd;
            public float length;
            public float medianWidth;
            /// <summary>How far past the first and last centre point the plate reached.</summary>
            public float startTrim, endTrim;
            /// <summary>As thick as it is wide: a cable, framed by transport along it rather than by its normal.</summary>
            public bool round;
            /// <summary>The corners of its faces in the input, three per face, which its rings are cut from.</summary>
            public List<Vector3> faceCorners;
            /// <summary>Midpoints between its two sides: its middle sheet. Used while finding its faces.</summary>
            public Vector3[] medial;
            public float medianThickness;
            /// <summary>Raised from the surface and fused to it (its height stands for the thickness): the tube's bottom goes into the surface.</summary>
            public bool lying;
            /// <summary>Its thin faces in the input topology. Used while finding its faces.</summary>
            public List<int> seedFaces;
            /// <summary>Not a ribbon alone (too short, or attached): one may be continued by it through a slider.</summary>
            public bool candidate;
        }

        /// <summary>A buckle where ribbons meet: a box, axisX and axisY across the surface, up off it.</summary>
        public sealed class Block
        {
            public Vector3 centre;
            public Vector3 axisX, axisY, up;
            /// <summary>Half the box's size along axisX, axisY and up.</summary>
            public Vector3 halfSize;
            /// <summary>All six sides: a slider wraps its strap all round; a buckle's bottom is in the surface.</summary>
            public bool closed;
        }

        /// <summary>As below, with no buckles.</summary>
        public static List<Ribbon> Find(Vector3[] positions, int[] triangles, float cell,
            out Vector3[] keptPositions, out int[] keptTriangles, out int[] sourceOfTriangle,
            out float outwardSign, out SpatialGrid surface, Stats stats) =>
            Find(positions, triangles, cell, out keptPositions, out keptTriangles, out sourceOfTriangle,
                out outwardSign, out surface, stats, null);

        /// <summary>
        /// Finds the ribbons of a mesh and returns the mesh without them: the triangles left
        /// (source indices) and the triangles closing the holes, over the same positions.
        /// <paramref name="sourceOfTriangle"/> gives, per output triangle, the source
        /// triangle it is or borders; <paramref name="outwardSign"/> is -1 when the input is wound
        /// inside out, and <paramref name="surface"/> the input's search grid.
        /// </summary>
        /// <remarks>With <paramref name="blocks"/>, the buckles where ribbons meet go into it, taken out of the mesh too.</remarks>
        public static List<Ribbon> Find(Vector3[] positions, int[] triangles, float cell,
            out Vector3[] keptPositions, out int[] keptTriangles, out int[] sourceOfTriangle,
            out float outwardSign, out SpatialGrid surface, Stats stats, List<Block> blocks)
        {
            outwardSign = 1f;
            surface = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats ??= new Stats();
            var ribbons = new List<Ribbon>();
            keptPositions = positions;
            keptTriangles = triangles;
            sourceOfTriangle = null;
            if (positions == null || triangles == null || triangles.Length < 3 || !(cell > 0f)) return ribbons;

            var topology = RemeshTopology.Build(positions, triangles);
            var P = topology.positions;
            var T = topology.triangles;
            var nv = topology.vertexCount;
            var nt = topology.triangleCount;
            var twin = topology.halfEdgeTwin;

            // Which way is out: the sign of the enclosed volume. Thickness is measured inward.
            double volume = 0;
            for (var t = 0; t < nt; t++)
                volume += Vector3.Dot(P[T[3 * t]], Vector3.Cross(P[T[3 * t + 1]], P[T[3 * t + 2]]));
            var outward = volume >= 0 ? 1f : -1f;
            outwardSign = outward;
            var N = new Vector3[nv];
            for (var v = 0; v < nv; v++) N[v] = topology.normals[v] * outward;

            var grid = new SpatialGrid(P, T);
            surface = grid;

            // --- thickness: inward ray to the far side of the part ---
            var maxThickness = maxThicknessCells * cell;
            var half = 0.5f * maxThickness;
            var thickness = new float[nv];
            var opposite = new int[nv];
            Parallel.For(0, nv, v =>
            {
                thickness[v] = float.PositiveInfinity;
                opposite[v] = -1;
                var d = -N[v];
                // The segment [p, p + d * maxThickness], as a search both ways round its midpoint;
                // facing: only triangles whose winding normal (times outward) points along d -- the
                // far wall, not this one.
                if (!grid.Raycast(P[v] + d * half, d, half, outward, out var t, out var tri, out _, out _)) return;
                var distance = half + t;
                if (!(distance > 1e-4f * cell)) return;
                thickness[v] = distance;
                opposite[v] = tri;
            });

            if (reliefBands) Relief(topology, N, grid, cell, outward, thickness, stats);
            if (fillThinHoles) FillThinHoles(topology, thickness, opposite, maxThickness, cell, stats);
            if (splitBranches || reliefBands) KeepBands(P, N, thickness, opposite, !splitBranches, maxThickness, cell, stats);

            var thinFace = new bool[nt];
            for (var t = 0; t < nt; t++)
                thinFace[t] = thickness[T[3 * t]] < maxThickness && thickness[T[3 * t + 1]] < maxThickness
                              && thickness[T[3 * t + 2]] < maxThickness;
            for (var v = 0; v < nv; v++) if (thickness[v] < maxThickness) stats.thinVertices++;
            if (debugThinFaces != null)
                for (var t = 0; t < nt; t++)
                    if (thinFace[t])
                        for (var i = 0; i < 3; i++) debugThinFaces.Add(P[T[3 * t + i]]);

            // --- sides: connected thin faces ---
            var side = new int[nt];
            for (var t = 0; t < nt; t++) side[t] = -1;
            var sideFaces = new List<List<int>>();
            var queue = new Queue<int>();
            for (var seed = 0; seed < nt; seed++)
            {
                if (!thinFace[seed] || side[seed] >= 0) continue;
                var id = sideFaces.Count;
                var list = new List<int>();
                side[seed] = id;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    list.Add(t);
                    for (var i = 0; i < 3; i++)
                    {
                        var o = twin[3 * t + i];
                        if (o < 0) continue;
                        var u = o / 3;
                        if (thinFace[u] && side[u] < 0) { side[u] = id; queue.Enqueue(u); }
                    }
                }
                sideFaces.Add(list);
            }
            stats.sides = sideFaces.Count;

            // --- plates: each side with the side its rays land on ---
            var group = new int[sideFaces.Count];
            for (var i = 0; i < group.Length; i++) group[i] = i;
            int Root(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }
            for (var s = 0; s < sideFaces.Count; s++)
            {
                if (sideFaces[s].Count < minSideFaces) continue;
                var votes = new Dictionary<int, int>();
                var total = 0;
                foreach (var t in sideFaces[s])
                    for (var i = 0; i < 3; i++)
                    {
                        var o = opposite[T[3 * t + i]];
                        if (o < 0) continue;
                        total++;
                        var os = side[o];
                        if (os < 0) continue;
                        votes[os] = votes.TryGetValue(os, out var c) ? c + 1 : 1;
                    }
                var best = -1; var bestVotes = 0;
                foreach (var kv in votes) if (kv.Value > bestVotes) { best = kv.Key; bestVotes = kv.Value; }
                if (best < 0 || bestVotes < 0.3f * total || sideFaces[best].Count < minSideFaces) continue;
                group[Root(s)] = Root(best);
            }
            var plates = new Dictionary<int, List<int>>();
            for (var s = 0; s < sideFaces.Count; s++)
            {
                if (sideFaces[s].Count < minSideFaces) continue;
                var r = Root(s);
                if (!plates.TryGetValue(r, out var l)) plates[r] = l = new List<int>();
                l.Add(s);
            }
            stats.pairs = plates.Count;

            var removed = new bool[nt];
            var candidates = new List<Ribbon>();
            foreach (var plate in plates.Values)
            {
                var seeds = new List<int>();
                foreach (var s in plate) seeds.AddRange(sideFaces[s]);
                var found = Measure(topology, N, thickness, opposite, grid, seeds, cell, outward, stats);
                if (found == null) continue;
                foreach (var ribbon in found) (ribbon.candidate ? candidates : ribbons).Add(ribbon);
            }
            var sliders = new List<Block>();
            if (linkSliders && ribbons.Count > 0) Link(ribbons, candidates, P, N, cell, sliders, stats);
            var lyingFace = flattenLying ? new bool[nt] : null;
            var freeFace = flattenLying ? new bool[nt] : null;
            foreach (var ribbon in ribbons)
            {
                // Its faces: both sides, and the rims between them.
                Collect(topology, ribbon, ribbon.seedFaces, removed, cell, ribbon.lying ? lyingFace : freeFace);
                ribbon.seedFaces = null;
            }
            stats.ribbons = ribbons.Count;
            if (ribbons.Count == 0) { stats.ms = watch.ElapsedMilliseconds; return ribbons; }
            foreach (var slider in sliders) TakeBox(P, T, slider, removed, cell);
            stats.sliders = sliders.Count;
            if (blocks != null && buckleBlocks) blocks.AddRange(sliders);
            if (blocks != null && buckleBlocks) FindBlocks(topology, N, ribbons, removed, cell, blocks, stats);
            DropShreds(topology, removed, cell, stats);

            var flatPositions = flattenLying ? FlattenOnSurface(topology, positions, removed, lyingFace, freeFace, stats) : null;
            Rebuild(flatPositions ?? positions, triangles, topology, removed, out keptPositions, out keptTriangles, out sourceOfTriangle, stats);
            debugKept?.Invoke(keptPositions, keptTriangles);
            stats.ms = watch.ElapsedMilliseconds;
            return ribbons;
        }

        // ------------------------------------------------------------------
        // Straps through sliders. Repeatedly, the nearest pair of an attached ribbon end and an
        // end of another ribbon or candidate that goes on in line (gap along the end's tangent,
        // tangents agreeing, widths alike) is joined into one ribbon; the gap's source points
        // within the strap's reach are boxed as the slider.
        static void Link(List<Ribbon> ribbons, List<Ribbon> candidates, Vector3[] P, Vector3[] N, float cell,
            List<Block> sliders, Stats stats)
        {
            var maxGap = sliderGapCells * cell;
            for (var round = 0; round < 64; round++)
            {
                Ribbon bestA = null, bestB = null;
                bool bestAStart = false, bestBStart = false;
                var bestGap = float.PositiveInfinity;
                var pool = new List<Ribbon>(ribbons);
                pool.AddRange(candidates);
                foreach (var a in ribbons)
                    for (var ea = 0; ea < 2; ea++)
                    {
                        var aStart = ea == 0;
                        if (aStart ? a.freeStart : a.freeEnd) continue;
                        End(a, aStart, out var pa, out var ta);
                        foreach (var b in pool)
                        {
                            if (b == a) continue;
                            var ratio = b.medianWidth / a.medianWidth;
                            if (ratio < 1f / sliderWidthRatio || ratio > sliderWidthRatio) continue;
                            for (var eb = 0; eb < 2; eb++)
                            {
                                var bStart = eb == 0;
                                End(b, bStart, out var pb, out var tb);
                                var d = pb - pa;
                                var gap = d.magnitude;
                                if (round == 0 && debugLog != null && gap < 4f * cell)
                                    Log($"near miss dots {Vector3.Dot(ta, -tb):F2} {(gap > 0f ? Vector3.Dot(d / gap, ta) : 1f):F2} ratio {ratio:F2} candidate {b.candidate}",
                                        pa, gap, b.medianWidth, 0f, cell);
                                if (gap > maxGap || gap >= bestGap) continue;
                                // b's end faces back at a's; both lines run on through the gap.
                                // A long gap (a slider and the strap's bend out of it) in line only.
                                var least = gap > 2.5f * cell ? 0.85f : 0.7f;
                                if (Vector3.Dot(ta, -tb) < least) continue;
                                // Where a third ribbon ends at the gap it is a buckle, not a slider.
                                if (ThirdEndNear(ribbons, a, b, pa, junctionRadiusCells * cell)
                                    || ThirdEndNear(ribbons, a, b, 0.5f * (pa + pb), junctionRadiusCells * cell)) continue;
                                if (gap > 0.3f * cell && Vector3.Dot(d / gap, ta) < least) continue;
                                bestA = a; bestB = b; bestAStart = aStart; bestBStart = bStart; bestGap = gap;
                            }
                        }
                    }
                if (bestA == null) return;
                if (bestAStart) Reverse(bestA);
                if (!bestBStart) Reverse(bestB);
                if (debugLog != null)
                {
                    var bc = bestB.centre;
                    Log($"link joins {bestA.centre[bestA.centre.Length - 1]} to {bc[0]} .. {bc[bc.Length - 1]} (piece {bestB.length / cell:F1} cells, candidate {bestB.candidate})",
                        bestA.centre[0], bestA.length, bestB.medianWidth, 0f, cell);
                }
                var slider = Join(bestA, bestB, P, N, cell);
                if (slider != null)
                {
                    sliders.Add(slider);
                    Log($"slider box half {slider.halfSize.x / cell:F2} {slider.halfSize.y / cell:F2} {slider.halfSize.z / cell:F2} strap {bestA.medianWidth / cell:F2} x {bestA.medianThickness / cell:F2}",
                        slider.centre, 0f, 0f, 0f, cell);
                }
                ribbons.Remove(bestB);
                candidates.Remove(bestB);
                stats.links++;
                Log("slider link", bestA.centre[bestA.centre.Length / 2], bestGap, bestA.medianWidth, slider != null ? 1f : 0f, cell);
            }
        }

        static bool ThirdEndNear(List<Ribbon> ribbons, Ribbon a, Ribbon b, Vector3 at, float radius)
        {
            foreach (var r in ribbons)
            {
                if (r == a || r == b) continue;
                if (!r.freeStart && (r.centre[0] - at).magnitude < radius) return true;
                if (!r.freeEnd && (r.centre[r.centre.Length - 1] - at).magnitude < radius) return true;
            }
            return false;
        }

        // An end of a ribbon: its centre point and the tangent going out of it.
        static void End(Ribbon r, bool start, out Vector3 p, out Vector3 outDir)
        {
            var c = r.centre;
            var m = c.Length;
            if (start)
            {
                p = c[0];
                outDir = MeshMath.SafeNormalize(c[0] - c[Mathf.Min(m - 1, 2)], Vector3.up);
            }
            else
            {
                p = c[m - 1];
                outDir = MeshMath.SafeNormalize(c[m - 1] - c[Mathf.Max(0, m - 3)], Vector3.up);
            }
        }

        static void Reverse(Ribbon r)
        {
            Array.Reverse(r.centre);
            Array.Reverse(r.up);
            Array.Reverse(r.width);
            Array.Reverse(r.thickness);
            (r.startTrim, r.endTrim) = (r.endTrim, r.startTrim);
            (r.freeStart, r.freeEnd) = (r.freeEnd, r.freeStart);
        }

        // a's end joined to b's start: one line through the gap; the slider in the gap, or null.
        static Block Join(Ribbon a, Ribbon b, Vector3[] P, Vector3[] N, float cell)
        {
            var ua = a.up[a.up.Length - 1];
            if (Vector3.Dot(ua, b.up[0]) < 0f)
                for (var i = 0; i < b.up.Length; i++) b.up[i] = -b.up[i];
            Vector3 p0 = a.centre[a.centre.Length - 1], p1 = b.centre[0];
            var gap = Vector3.Distance(p0, p1);
            var steps = Mathf.Max(0, Mathf.FloorToInt(gap / (0.5f * cell)));
            var centre = new List<Vector3>(a.centre);
            var up = new List<Vector3>(a.up);
            var width = new List<float>(a.width);
            var thickness = new List<float>(a.thickness);
            for (var i = 1; i <= steps; i++)
            {
                var f = i / (steps + 1f);
                centre.Add(Vector3.Lerp(p0, p1, f));
                up.Add(MeshMath.SafeNormalize(Vector3.Lerp(ua, b.up[0], f), ua));
                width.Add(Mathf.Lerp(a.width[a.width.Length - 1], b.width[0], f));
                thickness.Add(Mathf.Lerp(a.thickness[a.thickness.Length - 1], b.thickness[0], f));
            }
            centre.AddRange(b.centre); up.AddRange(b.up); width.AddRange(b.width); thickness.AddRange(b.thickness);

            // The slider: the source round the gap, in the strap's frame there.
            Block slider = null;
            var T = MeshMath.SafeNormalize(p1 - p0, Vector3.zero);
            if (T != Vector3.zero)
            {
                var U = MeshMath.SafeNormalize(ua - T * Vector3.Dot(ua, T), Vector3.up);
                var X = Vector3.Cross(U, T);
                var mid = 0.5f * (p0 + p1);
                var halfW = 0.5f * a.medianWidth;
                var halfT = 0.5f * a.medianThickness;
                var reachAlong = 0.5f * gap + 0.25f * cell;
                var xs = new List<float>(); var ys = new List<float>(); var ss = new List<float>();
                var r2 = (reachAlong + halfW + cell) * (reachAlong + halfW + cell);
                for (var v = 0; v < P.Length; v++)
                {
                    var d = P[v] - mid;
                    if (d.sqrMagnitude > r2) continue;
                    var s = Vector3.Dot(d, T);
                    var x = Vector3.Dot(d, X);
                    var y = Vector3.Dot(d, U);
                    if (Mathf.Abs(s) > reachAlong || Mathf.Abs(x) > halfW + 0.75f * cell) continue;
                    if (Mathf.Abs(y) > halfT + 0.75f * cell) continue;
                    // The slider's outside faces away from the strap it wraps; a surface beside
                    // it (the cheek the strap runs over) faces towards it.
                    if (Vector3.Dot(N[v], X * x + U * y) <= 0f) continue;
                    xs.Add(x); ys.Add(y); ss.Add(s);
                }
                // Its length: where the part stands out of the strap's own section (the rest of the
                // gap is the strap, bending out of the slider).
                var proud = new List<float>();
                for (var i = 0; i < ss.Count; i++)
                    if (Mathf.Abs(xs[i]) > halfW + 0.1f * cell || Mathf.Abs(ys[i]) > halfT + 0.1f * cell) proud.Add(ss[i]);
                if (proud.Count >= 20)
                {
                    proud.Sort();
                    float lo = proud[Mathf.RoundToInt(0.03f * (proud.Count - 1))], hi = proud[Mathf.RoundToInt(0.97f * (proud.Count - 1))];
                    for (var i = ss.Count - 1; i >= 0; i--)
                        if (ss[i] < lo || ss[i] > hi) { ss.RemoveAt(i); xs.RemoveAt(i); ys.RemoveAt(i); }
                }
                if (xs.Count >= 30)
                {
                    xs.Sort(); ys.Sort(); ss.Sort();
                    float Pct(List<float> l, float q) => l[Mathf.Clamp(Mathf.RoundToInt(q * (l.Count - 1)), 0, l.Count - 1)];
                    float x0 = Pct(xs, 0.03f), x1 = Pct(xs, 0.97f), s0 = Pct(ss, 0.03f), s1 = Pct(ss, 0.97f);
                    var top = Mathf.Max(Pct(ys, 0.97f), halfT);
                    var bottom = Mathf.Min(Pct(ys, 0.03f), -halfT);
                    if (a.lying) bottom -= lyingEmbedCells * cell;
                    // A slider stands out of the strap: wider or taller than it; else it was just a gap.
                    if ((x1 - x0 > 1.1f * a.medianWidth || top > halfT + 0.1f * cell) && s1 - s0 > 0.2f * cell)
                        slider = new Block
                        {
                            centre = mid + T * (0.5f * (s0 + s1)) + X * (0.5f * (x0 + x1)) + U * (0.5f * (top + bottom)),
                            axisX = T,
                            axisY = X,
                            up = U,
                            halfSize = new Vector3(0.5f * (s1 - s0), 0.5f * (x1 - x0), 0.5f * (top - bottom)),
                            closed = true
                        };
                }
            }

            var length = 0f;
            for (var i = 1; i < centre.Count; i++) length += Vector3.Distance(centre[i - 1], centre[i]);
            var lying = a.length * (a.lying ? 1 : -1) + b.length * (b.lying ? 1 : -1) > 0f;
            a.medianWidth = (a.medianWidth * a.length + b.medianWidth * b.length) / Mathf.Max(1e-6f, a.length + b.length);
            a.medianThickness = (a.medianThickness * a.length + b.medianThickness * b.length) / Mathf.Max(1e-6f, a.length + b.length);
            a.centre = centre.ToArray();
            a.up = up.ToArray();
            a.width = width.ToArray();
            a.thickness = thickness.ToArray();
            a.length = length;
            a.lying = lying;
            a.endTrim = b.endTrim;
            a.freeEnd = b.freeEnd;
            var medial = new List<Vector3>(a.medial);
            medial.AddRange(b.medial);
            a.medial = medial.ToArray();
            a.seedFaces.AddRange(b.seedFaces);
            return slider;
        }

        // Faces with their centre in the box (a little bigger) go with it.
        static void TakeBox(Vector3[] P, int[] T, Block box, bool[] removed, float cell)
        {
            var margin = 0.05f * cell;
            var h = box.halfSize + new Vector3(margin, margin, margin);
            var r2 = h.sqrMagnitude;
            for (var t = 0; t < removed.Length; t++)
            {
                if (removed[t]) continue;
                var d = (P[T[3 * t]] + P[T[3 * t + 1]] + P[T[3 * t + 2]]) / 3f - box.centre;
                if (d.sqrMagnitude > r2) continue;
                if (Mathf.Abs(Vector3.Dot(d, box.axisX)) > h.x || Mathf.Abs(Vector3.Dot(d, box.axisY)) > h.y
                    || Mathf.Abs(Vector3.Dot(d, box.up)) > h.z) continue;
                removed[t] = true;
            }
        }

        // ------------------------------------------------------------------
        // Buckles: where attached ends of two ribbons meet, the surface round the spot is fitted
        // (a quadric height field over the plane across the ribbons' up, refitted without what
        // stands above it), and what stands above it within blockRadiusCells is the buckle -- a
        // box over its footprint's principal axes, from the top it reaches down into the surface.
        static void FindBlocks(RemeshTopology topology, Vector3[] N, List<Ribbon> ribbons, bool[] removed,
            float cell, List<Block> blocks, Stats stats)
        {
            var P = topology.positions;
            var T = topology.triangles;
            var nv = topology.vertexCount;
            var nt = topology.triangleCount;
            var ends = new List<(Vector3 p, Vector3 dir, Vector3 up, int ribbon)>();
            for (var r = 0; r < ribbons.Count; r++)
            {
                var rb = ribbons[r];
                var c = rb.centre;
                var m = c.Length;
                if (m < 2) continue;
                Log(rb.freeStart ? "free start" : "attached start", c[0], rb.startTrim, 0f, 0f, cell);
                Log(rb.freeEnd ? "free end" : "attached end", c[m - 1], rb.endTrim, 0f, 0f, cell);
                if (!rb.freeStart)
                {
                    var d = MeshMath.SafeNormalize(c[0] - c[1], Vector3.up);
                    ends.Add((c[0] + d * rb.startTrim, d, rb.up[0], r));
                }
                if (!rb.freeEnd)
                {
                    var d = MeshMath.SafeNormalize(c[m - 1] - c[m - 2], Vector3.up);
                    ends.Add((c[m - 1] + d * rb.endTrim, d, rb.up[m - 1], r));
                }
            }
            if (ends.Count < (singleEndBlocks ? 1 : 2)) return;
            var onRibbon = new bool[nv];
            for (var t = 0; t < nt; t++)
                if (removed[t])
                    for (var i = 0; i < 3; i++) onRibbon[T[3 * t + i]] = true;

            var used = new bool[ends.Count];
            var junctionRadius = junctionRadiusCells * cell;
            for (var i = 0; i < ends.Count; i++)
            {
                if (used[i]) continue;
                var group = new List<int> { i };
                for (var j = i + 1; j < ends.Count; j++)
                    if (!used[j] && ends[j].ribbon != ends[i].ribbon && (ends[j].p - ends[i].p).magnitude <= junctionRadius)
                        group.Add(j);
                if (group.Count < (singleEndBlocks ? 1 : 2)) continue;
                foreach (var g in group) used[g] = true;

                var J = Vector3.zero;
                var U = Vector3.zero;
                var first = ends[group[0]].up;
                foreach (var g in group)
                {
                    J += ends[g].p + ends[g].dir * (0.5f * cell);
                    U += Vector3.Dot(ends[g].up, first) >= 0f ? ends[g].up : -ends[g].up;
                }
                J /= group.Count;
                U = MeshMath.SafeNormalize(U, first);
                var block = FitBlock(P, T, N, onRibbon, removed, J, U, cell);
                if (block == null) { Log("no buckle", J, 0f, 0f, 0f, cell); continue; }
                Log("buckle", block.centre, 2f * block.halfSize.x, 2f * block.halfSize.y, 2f * block.halfSize.z / cell, cell);
                blocks.Add(block);
            }
        }

        static Block FitBlock(Vector3[] P, int[] T, Vector3[] N, bool[] onRibbon, bool[] removed, Vector3 J, Vector3 U,
            float cell)
        {
            var radius = blockRadiusCells * cell;
            var fitRadius = 2f * radius;
            // Out of the surface: most of the surface round the spot faces the same way as up.
            var near = new List<int>();
            var facing = 0f;
            for (var v = 0; v < P.Length; v++)
            {
                if ((P[v] - J).sqrMagnitude > fitRadius * fitRadius) continue;
                near.Add(v);
                if (!onRibbon[v]) facing += Vector3.Dot(N[v], U);
            }
            if (facing < 0f) U = -U;
            var X = OrientationField.ArbitraryTangent(U);
            var Y = Vector3.Cross(U, X);

            // The surface: h = quadric in (x, y), in cells; refitted without what stands above it.
            var xs = new List<Vector3>();
            foreach (var v in near)
            {
                if (onRibbon[v] || Vector3.Dot(N[v], U) < 0.3f) continue;
                var d = (P[v] - J) / cell;
                xs.Add(new Vector3(Vector3.Dot(d, X), Vector3.Dot(d, Y), Vector3.Dot(d, U)));
            }
            if (xs.Count < 50) return null;
            var coef = new double[6];
            var keep = new bool[xs.Count];
            for (var i = 0; i < keep.Length; i++) keep[i] = true;
            double Base(double x, double y) => coef[0] + coef[1] * x + coef[2] * y + coef[3] * x * x + coef[4] * x * y + coef[5] * y * y;
            for (var pass = 0; pass < 5; pass++)
            {
                var A = new double[6, 7];
                var count = 0;
                for (var i = 0; i < xs.Count; i++)
                {
                    if (!keep[i]) continue;
                    count++;
                    double x = xs[i].x, y = xs[i].y;
                    var row = new[] { 1, x, y, x * x, x * y, y * y };
                    for (var a = 0; a < 6; a++)
                    {
                        for (var b = 0; b < 6; b++) A[a, b] += row[a] * row[b];
                        A[a, 6] += row[a] * xs[i].z;
                    }
                }
                if (count < 30 || !Solve6(A, coef)) return null;
                var limit = pass == 0 ? 0.3 : 0.08;
                for (var i = 0; i < xs.Count; i++) keep[i] = xs[i].z - Base(xs[i].x, xs[i].y) <= limit;
            }

            // The buckle's top: facing up, above the surface by a fifth of a cell, within the radius
            // across. Its walls and what hangs off it (a ring, the end of a band running in) are
            // left out of the fit.
            var r2 = (radius / cell) * (radius / cell);
            var pts = new List<Vector3>();
            foreach (var v in near)
            {
                if (onRibbon[v] || Vector3.Dot(N[v], U) < 0.7f) continue;
                var d = (P[v] - J) / cell;
                var x = Vector3.Dot(d, X);
                var y = Vector3.Dot(d, Y);
                if (x * x + y * y > r2) continue;
                var h = Vector3.Dot(d, U);
                if (h - Base(x, y) > 0.2) pts.Add(new Vector3(x, y, h));
            }
            if (pts.Count < 30) return null;
            float Pct(List<float> l, float f) => l[Mathf.Clamp(Mathf.RoundToInt(f * (l.Count - 1)), 0, l.Count - 1)];
            // The footprint: the smallest rectangle (between the 5th and 95th percentiles each way)
            // over directions five degrees apart.
            var pa = new List<float>(pts.Count);
            var pb = new List<float>(pts.Count);
            var bestArea = float.PositiveInfinity;
            Vector2 ax = Vector2.right, ay = Vector2.up;
            float a0 = 0, a1 = 0, b0 = 0, b1 = 0;
            for (var step = 0; step < 36; step++)
            {
                var angle = step * Mathf.PI / 36f;
                var dx = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                var dy = new Vector2(-dx.y, dx.x);
                pa.Clear(); pb.Clear();
                foreach (var q in pts)
                {
                    var xy = new Vector2(q.x, q.y);
                    pa.Add(Vector2.Dot(xy, dx));
                    pb.Add(Vector2.Dot(xy, dy));
                }
                pa.Sort(); pb.Sort();
                float u0 = Pct(pa, 0.05f), u1 = Pct(pa, 0.95f), w0 = Pct(pb, 0.05f), w1 = Pct(pb, 0.95f);
                var area = (u1 - u0) * (w1 - w0);
                if (area >= bestArea) continue;
                bestArea = area;
                ax = dx; ay = dy;
                a0 = u0; a1 = u1; b0 = w0; b1 = w1;
            }
            var ph = new List<float>(pts.Count);
            foreach (var q in pts) ph.Add(q.z);
            ph.Sort();
            var top = Pct(ph, 0.5f);
            float ca = 0.5f * (a0 + a1), cb = 0.5f * (b0 + b1);
            // Down to the surface under its lowest corner, and a little into it.
            var bottom = float.PositiveInfinity;
            foreach (var (u, w) in new[] { (a0, b0), (a0, b1), (a1, b0), (a1, b1), (ca, cb) })
            {
                var xy = ax * u + ay * w;
                bottom = Mathf.Min(bottom, (float)Base(xy.x, xy.y));
            }
            bottom -= lyingEmbedCells;
            // A buckle is small and low: a fit taller or wider than that took in a part of the shape.
            if (!(top - bottom > 0.15f) || a1 - a0 < 0.3f || b1 - b0 < 0.3f) return null;
            if (top - bottom > maxBlockHeightCells || Mathf.Max(a1 - a0, b1 - b0) > maxBlockSizeCells) return null;

            var axisX = MeshMath.SafeNormalize(X * ax.x + Y * ax.y, X);
            var axisY = MeshMath.SafeNormalize(X * ay.x + Y * ay.y, Y);
            var centreXY = ax * ca + ay * cb;
            var block = new Block
            {
                centre = J + (X * centreXY.x + Y * centreXY.y + U * (0.5f * (top + bottom))) * cell,
                axisX = axisX,
                axisY = axisY,
                up = U,
                halfSize = new Vector3(0.5f * (a1 - a0), 0.5f * (b1 - b0), 0.5f * (top - bottom)) * cell
            };

            // Its faces: those with their centre in the box (a little wider), above the surface.
            var margin = 0.1f;
            for (var t = 0; t < removed.Length; t++)
            {
                if (removed[t]) continue;
                var c = (P[T[3 * t]] + P[T[3 * t + 1]] + P[T[3 * t + 2]]) / 3f;
                var d = (c - J) / cell;
                if (Mathf.Abs(Vector3.Dot(d, axisX) - Vector2.Dot(centreXY, ax)) > 0.5f * (a1 - a0) + margin) continue;
                if (Mathf.Abs(Vector3.Dot(d, axisY) - Vector2.Dot(centreXY, ay)) > 0.5f * (b1 - b0) + margin) continue;
                var x = Vector3.Dot(d, X);
                var y = Vector3.Dot(d, Y);
                var h = Vector3.Dot(d, U);
                if (h - Base(x, y) < 0.04 || h > top + 0.2f) continue;
                removed[t] = true;
            }
            return block;
        }

        // Gaussian elimination with partial pivoting on a 6 x 7 augmented system.
        static bool Solve6(double[,] A, double[] x)
        {
            const int n = 6;
            for (var c = 0; c < n; c++)
            {
                var pivot = c;
                for (var r = c + 1; r < n; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[pivot, c])) pivot = r;
                if (Math.Abs(A[pivot, c]) < 1e-12) return false;
                if (pivot != c)
                    for (var k = 0; k <= n; k++) (A[c, k], A[pivot, k]) = (A[pivot, k], A[c, k]);
                for (var r = c + 1; r < n; r++)
                {
                    var f = A[r, c] / A[c, c];
                    for (var k = c; k <= n; k++) A[r, k] -= f * A[c, k];
                }
            }
            for (var r = n - 1; r >= 0; r--)
            {
                var sum = A[r, n];
                for (var k = r + 1; k < n; k++) sum -= A[r, k] * x[k];
                x[r] = sum / A[r, r];
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Thin vertices off any band lose their thinness: the middle sheet's points, pooled in
        // voxels of a quarter cell, have their spread within bandRadiusCells measured; where the
        // second spread (as a width) is over maxBandSpreadCells, or not under two thirds of the
        // first, the sheet is no band there.
        // raisedOnly: only the raised vertices (no far side found) can lose it -- a buckle's rims
        // otherwise join a strap to the broad plate beside it.
        static void KeepBands(Vector3[] P, Vector3[] N, float[] thickness, int[] opposite, bool raisedOnly,
            float maxThickness, float cell, Stats stats)
        {
            var nv = P.Length;
            var voxel = 0.25f * cell;
            var index = new Dictionary<Vector3Int, int>();
            var sum = new List<Vector3>();
            var count = new List<int>();
            var voxelOf = new int[nv];
            for (var v = 0; v < nv; v++)
            {
                voxelOf[v] = -1;
                if (!(thickness[v] < maxThickness)) continue;
                var m = P[v] - N[v] * (0.5f * thickness[v]);
                var key = new Vector3Int(Mathf.FloorToInt(m.x / voxel), Mathf.FloorToInt(m.y / voxel), Mathf.FloorToInt(m.z / voxel));
                if (!index.TryGetValue(key, out var id))
                {
                    id = sum.Count;
                    index[key] = id;
                    sum.Add(Vector3.zero);
                    count.Add(0);
                }
                sum[id] += m;
                count[id]++;
                voxelOf[v] = id;
            }
            var nVox = sum.Count;
            var centre = new Vector3[nVox];
            var keys = new Vector3Int[nVox];
            foreach (var kv in index) keys[kv.Value] = kv.Key;
            for (var i = 0; i < nVox; i++) centre[i] = sum[i] / count[i];
            var radius = bandRadiusCells * cell;
            var reachVox = Mathf.CeilToInt(radius / voxel);
            var r2 = radius * radius;
            var maxSpread = maxBandSpreadCells * cell;
            var band = new bool[nVox];
            Parallel.For(0, nVox, i =>
            {
                var k = keys[i];
                double w = 0, mx = 0, my = 0, mz = 0;
                double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
                for (var dx = -reachVox; dx <= reachVox; dx++)
                    for (var dy = -reachVox; dy <= reachVox; dy++)
                        for (var dz = -reachVox; dz <= reachVox; dz++)
                        {
                            if (!index.TryGetValue(new Vector3Int(k.x + dx, k.y + dy, k.z + dz), out var j)) continue;
                            var d = centre[j] - centre[i];
                            if (d.sqrMagnitude > r2) continue;
                            double c = count[j];
                            w += c; mx += c * d.x; my += c * d.y; mz += c * d.z;
                            xx += c * d.x * d.x; xy += c * d.x * d.y; xz += c * d.x * d.z;
                            yy += c * d.y * d.y; yz += c * d.y * d.z; zz += c * d.z * d.z;
                        }
                mx /= w; my /= w; mz /= w;
                var cov = new double[3, 3];
                cov[0, 0] = xx / w - mx * mx; cov[0, 1] = cov[1, 0] = xy / w - mx * my; cov[0, 2] = cov[2, 0] = xz / w - mx * mz;
                cov[1, 1] = yy / w - my * my; cov[1, 2] = cov[2, 1] = yz / w - my * mz; cov[2, 2] = zz / w - mz * mz;
                var e = Eigenvalues3(cov);
                // e sorted descending: the sheet spreads along e[0] and e[1]; e[2] is its bend and noise.
                var width = Math.Sqrt(12 * Math.Max(0, e[1]));
                band[i] = width <= maxSpread && e[1] <= (4.0 / 9.0) * e[0];
            });
            for (var v = 0; v < nv; v++)
                if (voxelOf[v] >= 0 && !band[voxelOf[v]] && (!raisedOnly || opposite[v] < 0))
                {
                    thickness[v] = float.PositiveInfinity;
                    stats.unbandedVertices++;
                }
        }

        // Eigenvalues of a symmetric 3 x 3 matrix, largest first (cyclic Jacobi).
        static double[] Eigenvalues3(double[,] m)
        {
            var a = (double[,])m.Clone();
            for (var sweep = 0; sweep < 12; sweep++)
            {
                var off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                if (off < 1e-30) break;
                for (var p = 0; p < 2; p++)
                    for (var q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-30) continue;
                        var theta = 0.5 * (a[q, q] - a[p, p]) / a[p, q];
                        var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        var c = 1 / Math.Sqrt(t * t + 1);
                        var s = t * c;
                        for (var k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (var k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                    }
            }
            var e = new[] { a[0, 0], a[1, 1], a[2, 2] };
            Array.Sort(e);
            Array.Reverse(e);
            return e;
        }

        // ------------------------------------------------------------------
        // Raised bands: vertices on the top of a narrow band standing on the surface get its height
        // over the ground beside it as their thickness (their far side stays unknown).
        //
        // Just under the surface (inside the part), rays go out sideways: across a band the walls
        // are near both ways, along it far. Measured first on one vertex per small voxel and
        // normal direction, in four directions; then on every vertex near a band sample, across
        // only, with the ground found by a ray down just past each wall.
        // Each connected patch of thick vertices small enough, off any open edge, is a hole in a thin
        // part: its vertices take the thickness and far side of the nearest thin vertex round it
        // (a breadth-first spread in from the patch's rim).
        static void FillThinHoles(RemeshTopology topology, float[] thickness, int[] opposite, float maxThickness,
            float cell, Stats stats)
        {
            var nv = topology.vertexCount;
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;
            var maxArea = maxThinHoleCells * cell * cell;
            var seen = new bool[nv];
            var patch = new List<int>();
            var stack = new Stack<int>();
            var queue = new Queue<int>();
            for (var seed = 0; seed < nv; seed++)
            {
                if (seen[seed] || thickness[seed] < maxThickness) continue;
                patch.Clear();
                var area = 0f;
                var open = false;
                seen[seed] = true;
                stack.Push(seed);
                while (stack.Count > 0)
                {
                    var v = stack.Pop();
                    patch.Add(v);
                    area += topology.areas[v];
                    open |= topology.isBoundaryVertex[v];
                    for (var e = start[v]; e < start[v + 1]; e++)
                    {
                        var w = adjacency[e];
                        if (!seen[w] && !(thickness[w] < maxThickness)) { seen[w] = true; stack.Push(w); }
                    }
                }
                if (open || area > maxArea) continue;
                // In from the rim: each vertex takes a filled or thin neighbour's values.
                var inPatch = new HashSet<int>(patch);
                var done = new HashSet<int>();
                foreach (var v in patch)
                    for (var e = start[v]; e < start[v + 1]; e++)
                    {
                        var w = adjacency[e];
                        if (inPatch.Contains(w) || !(thickness[w] < maxThickness)) continue;
                        thickness[v] = thickness[w];
                        opposite[v] = opposite[w];
                        done.Add(v);
                        queue.Enqueue(v);
                        break;
                    }
                while (queue.Count > 0)
                {
                    var v = queue.Dequeue();
                    for (var e = start[v]; e < start[v + 1]; e++)
                    {
                        var w = adjacency[e];
                        if (!inPatch.Contains(w) || !done.Add(w)) continue;
                        thickness[w] = thickness[v];
                        opposite[w] = opposite[v];
                        queue.Enqueue(w);
                    }
                }
                stats.filledVertices += done.Count;
            }
        }

        static void Relief(RemeshTopology topology, Vector3[] N, SpatialGrid grid, float cell, float outward,
            float[] thickness, Stats stats)
        {
            var P = topology.positions;
            var T = topology.triangles;
            var nv = topology.vertexCount;
            var maxThickness = maxThicknessCells * cell;
            var maxWidth = maxReliefWidthCells * cell;
            var depth = 0.04f * cell;
            var voxel = 0.2f * cell;

            (int, int, int, int) Key(int v)
            {
                var p = P[v];
                var n = N[v];
                var ax = Mathf.Abs(n.x); var ay = Mathf.Abs(n.y); var az = Mathf.Abs(n.z);
                var dir = ax >= ay && ax >= az ? (n.x > 0 ? 0 : 1) : ay >= az ? (n.y > 0 ? 2 : 3) : (n.z > 0 ? 4 : 5);
                return (Mathf.FloorToInt(p.x / voxel), Mathf.FloorToInt(p.y / voxel), Mathf.FloorToInt(p.z / voxel), dir);
            }
            var repOf = new Dictionary<(int, int, int, int), int>();
            var reps = new List<int>();
            // invariantRelief: each thick vertex's sample is a rep within voxel of it, normal alike;
            // reps picked in index order, each where no rep is near yet.
            var repOfVertex = invariantRelief ? new int[nv] : null;
            if (invariantRelief)
            {
                var buckets = new Dictionary<(int, int, int), List<int>>();
                (int, int, int) Cell(Vector3 p) => (Mathf.FloorToInt(p.x / voxel), Mathf.FloorToInt(p.y / voxel), Mathf.FloorToInt(p.z / voxel));
                int Nearest(int v)
                {
                    var p = P[v];
                    var (cx, cy, cz) = Cell(p);
                    var best = -1;
                    var bestD = voxel * voxel;
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dz = -1; dz <= 1; dz++)
                            {
                                if (!buckets.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                                foreach (var r in list)
                                {
                                    var w = reps[r];
                                    if (Vector3.Dot(N[w], N[v]) < 0.7f) continue;
                                    var d = (P[w] - p).sqrMagnitude;
                                    if (d <= bestD) { bestD = d; best = r; }
                                }
                            }
                    return best;
                }
                for (var v = 0; v < nv; v++)
                {
                    repOfVertex[v] = -1;
                    if (thickness[v] < maxThickness || Nearest(v) >= 0) continue;
                    var key = Cell(P[v]);
                    if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                    list.Add(reps.Count);
                    reps.Add(v);
                }
                Parallel.For(0, nv, v => { if (thickness[v] >= maxThickness) repOfVertex[v] = Nearest(v); });
            }
            else
                for (var v = 0; v < nv; v++)
                {
                    if (thickness[v] < maxThickness) continue;
                    var key = Key(v);
                    if (repOf.ContainsKey(key)) continue;
                    repOf[key] = reps.Count;
                    reps.Add(v);
                }

            // Distance to the first wall going out from p along dir; infinite when the first surface
            // met is one going in (p was not inside), none is near, or it is no wall square to dir
            // (a ray cutting a corner of a broad part).
            float Out(Vector3 p, Vector3 dir)
            {
                if (!grid.FirstHit(p, dir, maxWidth, 0f, out var t, out var tri)) return float.PositiveInfinity;
                var n = Vector3.Cross(P[T[3 * tri + 1]] - P[T[3 * tri]], P[T[3 * tri + 2]] - P[T[3 * tri]]);
                return Vector3.Dot(n, dir) * outward > wallSquareness * n.magnitude ? t : float.PositiveInfinity;
            }
            // Ground below q (in air): the first surface met going down must face up.
            float Ground(Vector3 q, Vector3 down)
            {
                if (!grid.FirstHit(q, down, maxThickness, 0f, out var t, out var tri)) return float.PositiveInfinity;
                var n = Vector3.Cross(P[T[3 * tri + 1]] - P[T[3 * tri]], P[T[3 * tri + 2]] - P[T[3 * tri]]);
                return Vector3.Dot(n, down) * outward < 0f ? t : float.PositiveInfinity;
            }

            var axis = new Vector3[reps.Count];
            var band = new bool[reps.Count];
            var depths = new[] { depth, 0.1f * cell, 0.2f * cell };
            Parallel.For(0, reps.Count, i =>
            {
                var v = reps[i];
                var n = N[v];
                var p = P[v] - n * depth;
                var t1 = OrientationField.ArbitraryTangent(n);
                var t2 = Vector3.Cross(n, t1);
                if (invariantRelief)
                {
                    // The narrowest way across: the best of eight, then halving steps either side;
                    // with reliefDepths, from the shallowest start under the top that finds one.
                    Vector3 Dir(float angle) => Mathf.Cos(angle) * t1 + Mathf.Sin(angle) * t2;
                    var tries = reliefDepths ? depths.Length : 1;
                    for (var di = 0; di < tries; di++)
                    {
                        var q = P[v] - n * depths[di];
                        float Chord(float angle) { var d = Dir(angle); return Out(q, d) + Out(q, -d); }
                        var step = Mathf.PI / 8f;
                        var at = 0f;
                        var w = Chord(0f);
                        for (var a = 1; a < 8; a++)
                        {
                            var c = Chord(a * step);
                            if (c < w) { w = c; at = a * step; }
                        }
                        if (!(w <= 1.2f * maxWidth)) continue;
                        for (var s = 0; s < 4; s++)
                        {
                            step *= 0.5f;
                            var lo = Chord(at - step);
                            var hi = Chord(at + step);
                            if (lo < w && lo <= hi) { w = lo; at -= step; }
                            else if (hi < w) { w = hi; at += step; }
                        }
                        if (!(w <= maxWidth)) continue;
                        if (!(Chord(at + 0.5f * Mathf.PI) >= 2f * w)) continue;
                        axis[i] = Dir(at);
                        band[i] = true;
                        return;
                    }
                    return;
                }
                var chord = new float[4];
                var dirs = new Vector3[4];
                for (var a = 0; a < 4; a++)
                {
                    var angle = a * Mathf.PI / 4f;
                    dirs[a] = Mathf.Cos(angle) * t1 + Mathf.Sin(angle) * t2;
                    chord[a] = Out(p, dirs[a]) + Out(p, -dirs[a]);
                }
                var best = 0;
                for (var a = 1; a < 4; a++) if (chord[a] < chord[best]) best = a;
                if (!(chord[best] <= maxWidth)) return;
                if (!(chord[(best + 2) % 4] >= 2f * chord[best])) return;
                axis[i] = dirs[best];
                band[i] = true;
            });

            var height = new float[nv];
            Parallel.For(0, nv, v =>
            {
                height[v] = float.PositiveInfinity;
                if (thickness[v] < maxThickness) return;
                int r;
                if (invariantRelief) { r = repOfVertex[v]; if (r < 0 || !band[r]) return; }
                else if (!repOf.TryGetValue(Key(v), out r) || !band[r]) return;
                var n = N[v];
                var a = MeshMath.SafeNormalize(axis[r] - n * Vector3.Dot(axis[r], n), Vector3.zero);
                if (a == Vector3.zero) return;
                var tries = invariantRelief && reliefDepths ? depths.Length : 1;
                for (var di = 0; di < tries; di++)
                {
                    var dv = depths[di];
                    var p = P[v] - n * dv;
                    var plus = Out(p, a);
                    var minus = Out(p, -a);
                    if (!(plus + minus <= maxWidth)) continue;
                    var past = 0.08f * cell;
                    var g1 = Ground(p + a * (plus + past), -n);
                    var g2 = Ground(p - a * (minus + past), -n);
                    // Ground both sides; with oneSidedGround, one is enough (a band along an edge
                    // overhangs it on the other) -- but then a buckle's rims join everything round it.
                    if (float.IsInfinity(g1) && float.IsInfinity(g2)) continue;
                    if (!oneSidedGround && (float.IsInfinity(g1) || float.IsInfinity(g2))) continue;
                    var h = dv + (float.IsInfinity(g1) ? g2 : float.IsInfinity(g2) ? g1 : 0.5f * (g1 + g2));
                    if (h < 0.08f * cell || h >= maxThickness) continue;
                    height[v] = h;
                    return;
                }
            });
            for (var v = 0; v < nv; v++)
                if (!float.IsInfinity(height[v]))
                {
                    thickness[v] = height[v];
                    stats.reliefVertices++;
                }
        }

        // ------------------------------------------------------------------
        // The ribbons of one plate -- its centre line and cross-section, in runs -- or null.
        static List<Ribbon> Measure(RemeshTopology topology, Vector3[] N, float[] thickness, int[] opposite,
            SpatialGrid grid, List<int> faces, float cell, float outward, Stats stats, int depth = 0)
        {
            var P = topology.positions;
            var T = topology.triangles;
            // The plate's vertices (both sides) and their midpoints, which lie on its middle sheet.
            var seenVertex = new HashSet<int>();
            var verts = new List<int>();
            foreach (var t in faces)
                for (var i = 0; i < 3; i++)
                {
                    var v = T[3 * t + i];
                    if (seenVertex.Add(v)) verts.Add(v);
                }
            var n = verts.Count;
            var mid = new Vector3[n];
            for (var i = 0; i < n; i++) mid[i] = P[verts[i]] - N[verts[i]] * (0.5f * thickness[verts[i]]);

            // The middle sheet as a graph: midpoints within a radius of each other. One sheet for
            // both sides, so a side broken into pieces still gives one line.
            var radius = Mathf.Max(2.5f * topology.averageEdgeLength, 0.12f * cell);
            var buckets = new Dictionary<Vector3Int, List<int>>();
            Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / radius), Mathf.FloorToInt(p.y / radius), Mathf.FloorToInt(p.z / radius));
            for (var i = 0; i < n; i++)
            {
                var key = Key(mid[i]);
                if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<int>();
                l.Add(i);
            }
            var adjacency = new List<int>[n];
            var r2 = radius * radius;
            for (var i = 0; i < n; i++)
            {
                adjacency[i] = new List<int>();
                var key = Key(mid[i]);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dz = -1; dz <= 1; dz++)
                            if (buckets.TryGetValue(new Vector3Int(key.x + dx, key.y + dy, key.z + dz), out var l))
                                foreach (var j in l)
                                    if (j != i && (mid[j] - mid[i]).sqrMagnitude <= r2) adjacency[i].Add(j);
            }
            float[] Distances(int from)
            {
                var d = new float[n];
                for (var i = 0; i < n; i++) d[i] = float.PositiveInfinity;
                var heap = new MinHeap(n);
                d[from] = 0f;
                heap.Push(from, 0f);
                while (heap.Count > 0)
                {
                    heap.Pop(out var i, out var di);
                    if (di > d[i]) continue;
                    foreach (var j in adjacency[i])
                    {
                        var dj = di + Vector3.Distance(mid[i], mid[j]);
                        if (dj < d[j]) { d[j] = dj; heap.Push(j, dj); }
                    }
                }
                return d;
            }
            int Farthest(float[] d)
            {
                var best = 0;
                for (var i = 1; i < n; i++) if (!float.IsInfinity(d[i]) && d[i] > d[best]) best = i;
                return best;
            }
            if (debugPieces != null)
                lock (debugPieces)
                {
                    var id = s_PieceCount++;
                    foreach (var q in mid) debugPieces.Add((id, q));
                    Log("piece " + id + " depth " + depth, mid[0], 0f, 0f, 0f, cell);
                }
            var a0 = Farthest(Distances(0));
            var along = Distances(a0);
            var length = along[Farthest(along)];
            // Too short to be a ribbon -- unless it may continue one through a slider.
            if (length < (linkSliders ? Mathf.Min(minLengthCells, minContinuationCells) : minLengthCells) * cell)
            { stats.rejectedShort++; Log("short", mid[a0], length, 0f, 0f, cell); return null; }

            // Binned by distance along: centres, and the plate's normal with one sign throughout.
            var bins = Mathf.Clamp(Mathf.CeilToInt(length / (0.5f * cell)), 2, 4096);
            var members = new List<int>[bins];
            for (var k = 0; k < bins; k++) members[k] = new List<int>();
            for (var i = 0; i < n; i++)
                if (!float.IsInfinity(along[i]))
                    members[Mathf.Min(bins - 1, (int)(along[i] / length * bins))].Add(i);

            // A fork: past a buckle two straps go on side by side in distance, and one bin holds
            // both. At the first bin whose points fall apart into two good-sized clusters, the
            // plate is cut: what comes before, and each connected piece of what comes after, are
            // measured on their own.
            if (splitForks && depth < 6)
            {
                var clusterOf = new int[n];
                for (var i = 0; i < n; i++) clusterOf[i] = -1;
                var fork = -1;
                var forked = new bool[bins];
                for (var k = 0; k < bins; k++)
                {
                    var list = members[k];
                    if (list.Count < 16) continue;
                    var inBin = new HashSet<int>(list);
                    var sizes = new List<int>();
                    foreach (var seed in list)
                    {
                        if (clusterOf[seed] >= 0) continue;
                        var id = sizes.Count;
                        var size = 0;
                        var stack = new Stack<int>();
                        clusterOf[seed] = id;
                        stack.Push(seed);
                        while (stack.Count > 0)
                        {
                            var i = stack.Pop();
                            size++;
                            foreach (var j in adjacency[i])
                                if (inBin.Contains(j) && clusterOf[j] < 0) { clusterOf[j] = id; stack.Push(j); }
                        }
                        sizes.Add(size);
                    }
                    var big = 0;
                    foreach (var z in sizes) if (z >= Mathf.Max(8, forkArmFraction * list.Count)) big++;
                    forked[k] = big >= 2;
                }
                // Two arms go on: three bins (a cell and a half) in a row, not a patch beside the band.
                for (var k = 0; k + 2 < bins && fork < 0; k++)
                    if (forked[k] && forked[k + 1] && forked[k + 2]) fork = k;
                if (fork > 0)
                {
                    var cut = fork * length / bins;
                    var part = new int[n];
                    for (var i = 0; i < n; i++) part[i] = float.IsInfinity(along[i]) ? -1 : along[i] < cut ? 0 : -2;
                    var parts = 1;
                    for (var seed = 0; seed < n; seed++)
                    {
                        if (part[seed] != -2) continue;
                        var stack = new Stack<int>();
                        part[seed] = parts;
                        stack.Push(seed);
                        while (stack.Count > 0)
                        {
                            var i = stack.Pop();
                            foreach (var j in adjacency[i])
                                if (part[j] == -2) { part[j] = parts; stack.Push(j); }
                        }
                        parts++;
                    }
                    var vertexPart = new Dictionary<int, int>(n);
                    for (var i = 0; i < n; i++) vertexPart[verts[i]] = part[i];
                    var split = new List<int>[parts];
                    for (var q = 0; q < parts; q++) split[q] = new List<int>();
                    foreach (var t in faces)
                    {
                        var q0 = vertexPart[T[3 * t]];
                        if (q0 < 0 || vertexPart[T[3 * t + 1]] != q0 || vertexPart[T[3 * t + 2]] != q0) continue;
                        split[q0].Add(t);
                    }
                    Log("fork", c0(mid, members[fork]), cut, 0f, parts - 1, cell);
                    var all = new List<Ribbon>();
                    foreach (var piece in split)
                    {
                        if (piece.Count < minSideFaces) continue;
                        var r = Measure(topology, N, thickness, opposite, grid, piece, cell, outward, stats, depth + 1);
                        if (r != null) all.AddRange(r);
                    }
                    return all;
                }
            }
            var centre = new List<Vector3>();
            var up = new List<Vector3>();
            var tk = new List<float>();
            var binOfCentre = new List<int>();
            var reference = Vector3.zero;
            for (var k = 0; k < bins; k++)
            {
                if (members[k].Count < 3) continue;
                var sum = Vector3.zero;
                foreach (var i in members[k]) sum += mid[i];
                centre.Add(sum / members[k].Count);
                if (reference == Vector3.zero) reference = N[verts[members[k][0]]];
                var plateNormal = reference;
                for (var pass = 0; pass < (robustPlateNormal ? 4 : 2); pass++)
                {
                    var acc = Vector3.zero;
                    foreach (var i in members[k])
                    {
                        var nv = N[verts[i]];
                        var dot = Vector3.Dot(nv, plateNormal);
                        // After the first two passes only the faces' own normals: not the walls,
                        // nor rims curling under.
                        if (pass >= 2 && Mathf.Abs(dot) < 0.7f) continue;
                        acc += dot >= 0f ? nv : -nv;
                    }
                    plateNormal = MeshMath.SafeNormalize(acc, plateNormal);
                }
                reference = plateNormal;
                up.Add(plateNormal);
                var th = new List<float>(members[k].Count);
                foreach (var i in members[k]) th.Add(thickness[verts[i]]);
                th.Sort();
                tk.Add(th[th.Count / 2]);
                binOfCentre.Add(k);
            }
            var m = centre.Count;
            if (m < 3) { stats.rejectedShort++; return null; }
            var c = centre.ToArray();
            for (var pass = 0; pass < 3; pass++)
            {
                var s = (Vector3[])c.Clone();
                for (var k = 1; k + 1 < m; k++) s[k] = 0.25f * c[k - 1] + 0.5f * c[k] + 0.25f * c[k + 1];
                c = s;
            }
            var tangent = new Vector3[m];
            for (var k = 0; k < m; k++)
                tangent[k] = MeshMath.SafeNormalize(c[Mathf.Min(m - 1, k + 1)] - c[Mathf.Max(0, k - 1)], Vector3.forward);
            var u = up.ToArray();
            for (var pass = 0; pass < 2; pass++)
            {
                var s = (Vector3[])u.Clone();
                for (var k = 1; k + 1 < m; k++) s[k] = MeshMath.SafeNormalize(u[k - 1] + 2f * u[k] + u[k + 1], u[k]);
                u = s;
            }
            for (var k = 0; k < m; k++)
                u[k] = MeshMath.SafeNormalize(u[k] - tangent[k] * Vector3.Dot(u[k], tangent[k]), OrientationField.ArbitraryTangent(tangent[k]));
            var across = new Vector3[m];
            for (var k = 0; k < m; k++) across[k] = Vector3.Cross(u[k], tangent[k]);

            // Width: the midpoints' spread across, plus the rims round them.
            var width = new float[m];
            var thicknessAt = tk.ToArray();
            for (var k = 0; k < m; k++)
            {
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                foreach (var i in members[binOfCentre[k]])
                {
                    var x = Vector3.Dot(mid[i] - c[k], across[k]);
                    lo = Mathf.Min(lo, x);
                    hi = Mathf.Max(hi, x);
                }
                width[k] = hi - lo + thicknessAt[k];
                c[k] += across[k] * (0.5f * (lo + hi));
            }
            var sorted = (float[])width.Clone();
            Array.Sort(sorted);
            var medianWidth = sorted[m / 2];
            var sortedThickness = (float[])thicknessAt.Clone();
            Array.Sort(sortedThickness);
            var medianThickness = sortedThickness[m / 2];

            // A round part (a cable): its normal goes all the way round and says nothing about a
            // frame. Carried along the line instead, turning as little as it can (double reflection).
            var round = medianThickness > 0.6f * medianWidth;
            if (round)
            {
                for (var k = 1; k < m; k++)
                {
                    var v1 = c[k] - c[k - 1];
                    var c1 = Vector3.Dot(v1, v1);
                    if (!(c1 > 0f)) { u[k] = u[k - 1]; continue; }
                    var rL = u[k - 1] - (2f / c1) * Vector3.Dot(v1, u[k - 1]) * v1;
                    var tL = tangent[k - 1] - (2f / c1) * Vector3.Dot(v1, tangent[k - 1]) * v1;
                    var v2 = tangent[k] - tL;
                    var c2 = Vector3.Dot(v2, v2);
                    u[k] = c2 > 0f ? rL - (2f / c2) * Vector3.Dot(v2, rL) * v2 : rL;
                    u[k] = MeshMath.SafeNormalize(u[k] - tangent[k] * Vector3.Dot(u[k], tangent[k]), u[k - 1]);
                }
                for (var k = 0; k < m; k++) across[k] = Vector3.Cross(u[k], tangent[k]);
            }


            // --- runs: stretches of steady width and direction ---
            //
            // A plate is often more than one strap: two straps through a buckle make an L, a strap
            // and a buckle's plates one region. Where the width leaves the plate's median by a
            // quarter, or the line turns sharply, the run ends; each run long enough is a ribbon on
            // its own, and the rest (the corner, the buckle) stays with the lattice. The end bins,
            // cut by round distance fronts and holding only part of the width, fall out the same way.
            if (debugLog != null)
            {
                var profile = new System.Text.StringBuilder();
                for (var k = 0; k < m; k++) profile.Append(' ').Append((width[k] / cell).ToString("F1"));
                Log("profile" + profile, c[m / 2], length, medianWidth, 0f, cell);
            }
            // Each run against its own median, not the plate's: a strap through a wider buckle is
            // two runs of the strap's width and one of the buckle's, whatever the plate's median.
            // The widths are median-of-three filtered first, so one odd bin does not cut a run.
            var w3 = new float[m];
            for (var k = 0; k < m; k++)
            {
                float a = width[Mathf.Max(0, k - 1)], b = width[k], d = width[Mathf.Min(m - 1, k + 1)];
                w3[k] = Mathf.Max(Mathf.Min(a, b), Mathf.Min(Mathf.Max(a, b), d));
            }
            var runOf = new int[m];
            var runSpans = new List<(int start, int end)>();
            for (var k0 = 0; k0 < m;)
            {
                var values = new List<float> { w3[k0] };
                var k1 = k0;
                while (k1 + 1 < m)
                {
                    values.Sort();
                    var median = values[values.Count / 2];
                    var straight = Vector3.Dot(tangent[Mathf.Max(0, k1)], tangent[Mathf.Min(m - 1, k1 + 2)]) > runStraightness;
                    if (!straight || Mathf.Abs(w3[k1 + 1] - median) > runWidthTolerance * median) break;
                    k1++;
                    values.Add(w3[k1]);
                }
                for (var k = k0; k <= k1; k++) runOf[k] = runSpans.Count;
                runSpans.Add((k0, k1));
                k0 = k1 + 1;
            }
            // A plate too short to be a ribbon is one piece that may continue one: one run, the
            // partial-width bins at its ends and all.
            if (length < minLengthCells * cell)
            {
                runSpans.Clear();
                runSpans.Add((0, m - 1));
                for (var k = 0; k < m; k++) runOf[k] = 0;
            }
            var good = new bool[m];
            for (var k = 0; k < m; k++) good[k] = runSpans[runOf[k]].end > runSpans[runOf[k]].start;
            var vertexBin = new Dictionary<int, int>();
            for (var k = 0; k < m; k++)
                foreach (var i in members[binOfCentre[k]]) vertexBin[verts[i]] = k;

            bool Rim(Vector3 from, Vector3 dir, float reach)
            {
                var h = 0.5f * reach;
                return grid.Raycast(from + dir * h, dir, h, outward, out _, out _, out _, out _);
            }
            bool Air(Vector3 p)
            {
                var q = grid.NearestPoint(p, out var dist, out var nrm);
                return dist > 0.05f * cell && Vector3.Dot(p - q, nrm * outward) > 0f;
            }

            var found = new List<Ribbon>();
            foreach (var (start, k1) in runSpans)
            {
                var count = k1 - start + 1;
                if (count < 2) continue;
                var runWidths = Slice(width, start, count);
                Array.Sort(runWidths);
                var runWidth = runWidths[count / 2];
                var runThicknesses = Slice(thicknessAt, start, count);
                Array.Sort(runThicknesses);
                var runThickness = runThicknesses[count / 2];

                var rc = Slice(c, start, count);
                StraightenEnds(rc, 1.5f * cell);
                var lineLength = 0f;
                for (var k = 1; k < count; k++) lineLength += Vector3.Distance(rc[k - 1], rc[k]);
                // Past each end, as far as the plate went before the next run or its own end (the
                // bins dropped there), at most a cell and a half.
                var before = start;
                while (before > 0 && !good[before - 1] && start - before < 3) before--;
                var after = k1;
                while (after < m - 1 && !good[after + 1] && after - k1 < 3) after++;
                var startTrim = Mathf.Min(1.5f * cell, Vector3.Distance(c[before], c[start]));
                var endTrim = Mathf.Min(1.5f * cell, Vector3.Distance(c[k1], c[after]));
                var at = rc[count / 2];
                if (runWidth > maxWidthCells * cell || runWidth < minWidthCells * cell)
                { stats.rejectedWide++; Log("run width", at, lineLength, runWidth, 0f, cell); continue; }
                if (skipOpenEdges)
                {
                    var open = 0;
                    for (var k = start; k <= k1; k++)
                        foreach (var i in members[binOfCentre[k]])
                            if (topology.isBoundaryVertex[verts[i]]) { open++; break; }
                    if (open * 4 >= count)
                    { stats.rejectedAttached++; Log("open edge", at, lineLength, runWidth, 0f, cell); continue; }
                }
                var candidate = false;
                if (lineLength + startTrim + endTrim < minAspect * runWidth || lineLength < minLengthCells * cell)
                {
                    stats.rejectedStubby++;
                    Log("stubby", at, lineLength, runWidth, 0f, cell);
                    if (!linkSliders || lineLength < minContinuationCells * cell) continue;
                    candidate = true;
                }

                // A free rim: from inside the plate, sideways, its own narrow side faces out within
                // half the width. Along an edge the plate is attached by, the material goes on into
                // the part it is attached to, and the first wall facing that way is far off (a strap
                // lying against the face is no attachment: its rim is still there).
                var free = 0;
                var freeSide = 0;
                // A raised band (no far side for most of it) has its rims just under its top; a
                // plate with two sides, at mid-thickness -- a step down from a thick hem to the
                // thinner sheet it edges is no rim.
                var raisedRun = 0;
                var runVertices = 0;
                for (var k = start; k <= k1; k++)
                    foreach (var i in members[binOfCentre[k]])
                    {
                        runVertices++;
                        if (opposite[verts[i]] < 0) raisedRun++;
                    }
                var lyingRun = raisedRun >= lyingRunFraction * runVertices;
                // The faces' heights over the centre, within the middle half of the width.
                var faceTop = new float[m];
                var faceBottom = new float[m];
                if (rimHeights && lyingRun)
                    for (var k = start; k <= k1; k++)
                    {
                        float top = 0f, bottom = 0f;
                        foreach (var i in members[binOfCentre[k]])
                        {
                            var q = P[verts[i]] - c[k];
                            if (Mathf.Abs(Vector3.Dot(q, across[k])) > 0.25f * width[k]) continue;
                            var h = Vector3.Dot(q, u[k]);
                            top = Mathf.Max(top, h);
                            bottom = Mathf.Min(bottom, h);
                        }
                        faceTop[k] = top;
                        faceBottom[k] = bottom;
                    }
                for (var k = start; k <= k1; k++)
                {
                    var reach = 0.5f * width[k] + 0.25f * thicknessAt[k] + 0.1f * cell;
                    for (var sgn = -1; sgn <= 1; sgn += 2)
                    {
                        var dir = across[k] * sgn;
                        var hit = Rim(c[k], dir, reach);
                        // Under each face, as found in the section, as deep as the raised-band
                        // test looks (0.04, 0.1 cells).
                        for (var d = 0; d < 2 && rimHeights && lyingRun && !hit; d++)
                        {
                            var under = (d == 0 ? 0.04f : 0.1f) * cell;
                            // Under the top: u is the raised side's own normal (and both ways, as
                            // its sign is not fixed).
                            if (faceTop[k] - under > 0f) hit = Rim(c[k] + u[k] * (faceTop[k] - under), dir, reach);
                            if (!hit && faceBottom[k] + under < 0f) hit = Rim(c[k] + u[k] * (faceBottom[k] + under), dir, reach);
                        }
                        if (hit)
                        {
                            free++;
                            if (sgn > 0) freeSide++;
                        }
                    }
                }
                if (free < minFreeRimFraction * 2 * count)
                {
                    if (!candidate)
                    {
                        stats.rejectedAttached++;
                        Log("attached (sides " + freeSide + "/" + (free - freeSide) + " of " + count + ", raised " + raisedRun + "/" + runVertices + ", thickness " + (runThickness / cell).ToString("F2") + ")", at, lineLength, runWidth, free / (2f * count), cell);
                    }
                    // Only a short piece continues a strap: a long one attached along its length is
                    // part of something else (the chin cup's rim, run through the buckle, 2026-10-19).
                    if (!linkSliders || !candidate || !continueAttached) continue;
                }
                if (!candidate) Log((round ? "cable" : "ribbon") + " (sides " + freeSide + "/" + (free - freeSide) + " of " + count + ", raised " + raisedRun + "/" + runVertices + ")", at, lineLength, runWidth, free / (2f * count), cell);

                var rw = Slice(width, start, count);
                var rt = Slice(thicknessAt, start, count);
                for (var k = 0; k < count; k++)
                {
                    rw[k] = Mathf.Clamp(rw[k], 0.85f * runWidth, 1.15f * runWidth);
                    rt[k] = Mathf.Clamp(rt[k], 0.7f * runThickness, 1.3f * runThickness);
                }
                // Its own midpoints and faces: the run's bins and one each side.
                var medial = new List<Vector3>();
                for (var k = Mathf.Max(0, start - 1); k <= Mathf.Min(m - 1, k1 + 1); k++)
                    foreach (var i in members[binOfCentre[k]]) medial.Add(mid[i]);
                var seedFaces = new List<int>();
                int raised = 0, all = 0;
                foreach (var t in faces)
                {
                    var inside = true;
                    for (var j = 0; j < 3 && inside; j++)
                        inside = vertexBin.TryGetValue(T[3 * t + j], out var kb) && kb >= start - 1 && kb <= k1 + 1;
                    if (!inside) continue;
                    seedFaces.Add(t);
                    all++;
                    if (opposite[T[3 * t]] < 0) raised++;
                }
                var rtan = Slice(tangent, start, count);
                found.Add(new Ribbon
                {
                    centre = rc,
                    up = Slice(u, start, count),
                    width = rw,
                    thickness = rt,
                    length = lineLength,
                    freeStart = Air(rc[0] - rtan[0] * (startTrim + 0.4f * cell)),
                    freeEnd = Air(rc[count - 1] + rtan[count - 1] * (endTrim + 0.4f * cell)),
                    medianWidth = runWidth,
                    startTrim = startTrim,
                    endTrim = endTrim,
                    round = round,
                    medial = medial.ToArray(),
                    medianThickness = runThickness,
                    lying = raised > all / 2,
                    seedFaces = seedFaces,
                    candidate = candidate
                });
            }
            return found;
        }

        // The last stretch at each end of a line, bent by the bins there (distance fronts from a
        // corner hold part of the width, off to one side), is drawn onto the line through it:
        // fully at the end, not at all a reach in.
        static void StraightenEnds(Vector3[] line, float reach)
        {
            var m = line.Length;
            if (m < 4) return;
            for (var side = 0; side < 2; side++)
            {
                int At(int i) => side == 0 ? i : m - 1 - i;
                var j = 1;
                var run = 0f;
                while (j + 1 < m / 2 && run < reach) { run += Vector3.Distance(line[At(j)], line[At(j - 1)]); j++; }
                if (j < 3) continue;
                var anchor = Vector3.zero;
                for (var i = 0; i <= j; i++) anchor += line[At(i)];
                anchor /= j + 1;
                var dir = MeshMath.SafeNormalize(line[At(j)] - line[At(0)], Vector3.zero);
                if (dir == Vector3.zero) continue;
                // The direction from the inner half, which the end's bend does not reach.
                var inner = MeshMath.SafeNormalize(line[At(j)] - line[At(j / 2)], dir);
                var copy = new Vector3[j + 1];
                for (var i = 0; i <= j; i++) copy[i] = line[At(i)];
                var pivot = copy[j];
                for (var i = 0; i < j; i++)
                {
                    var onLine = pivot + inner * Vector3.Dot(copy[i] - pivot, inner);
                    var w = 1f - (float)i / j;
                    line[At(i)] = Vector3.Lerp(copy[i], onLine, w);
                }
            }
        }

        static Vector3 c0(Vector3[] points, List<int> which)
        {
            var sum = Vector3.zero;
            foreach (var i in which) sum += points[i];
            return which.Count > 0 ? sum / which.Count : sum;
        }

        static T[] Slice<T>(T[] a, int start, int count)
        {
            var r = new T[count];
            Array.Copy(a, start, r, 0, count);
            return r;
        }

        // ------------------------------------------------------------------
        // Marks the ribbon's faces: the seeds, and every face reached from them whose centre is
        // within three quarters of the thickness of the middle sheet -- the rims round its long
        // edges, the ends; not a surface it lies over (that is half the thickness off, plus the gap).
        static void Collect(RemeshTopology topology, Ribbon ribbon, List<int> seeds, bool[] removed, float cell,
            bool[] mark = null)
        {
            var P = topology.positions;
            var T = topology.triangles;
            var twin = topology.halfEdgeTwin;
            var reach = 0.75f * ribbon.medianThickness + 0.02f * cell;
            var r2 = reach * reach;
            var buckets = new Dictionary<Vector3Int, List<Vector3>>();
            Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / reach), Mathf.FloorToInt(p.y / reach), Mathf.FloorToInt(p.z / reach));
            foreach (var q in ribbon.medial)
            {
                var key = Key(q);
                if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<Vector3>();
                l.Add(q);
            }
            bool Inside(int t)
            {
                var x = (P[T[3 * t]] + P[T[3 * t + 1]] + P[T[3 * t + 2]]) / 3f;
                var key = Key(x);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dz = -1; dz <= 1; dz++)
                            if (buckets.TryGetValue(new Vector3Int(key.x + dx, key.y + dy, key.z + dz), out var l))
                                foreach (var q in l)
                                    if ((q - x).sqrMagnitude <= r2) return true;
                return false;
            }
            var queue = new Queue<int>();
            var seen = new HashSet<int>();
            var taken = new List<int>();
            foreach (var t in seeds)
            {
                removed[t] = true;
                seen.Add(t);
                queue.Enqueue(t);
                taken.Add(t);
            }
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                for (var i = 0; i < 3; i++)
                {
                    var o = twin[3 * t + i];
                    if (o < 0) continue;
                    var g = o / 3;
                    if (!seen.Add(g)) continue;
                    if (removed[g] || !Inside(g)) continue;
                    removed[g] = true;
                    queue.Enqueue(g);
                    taken.Add(g);
                }
            }
            if (mark != null) foreach (var t in taken) mark[t] = true;
            ribbon.faceCorners = new List<Vector3>(3 * taken.Count);
            foreach (var t in taken)
                for (var i = 0; i < 3; i++) ribbon.faceCorners.Add(P[T[3 * t + i]]);
            ribbon.medial = null;
        }

        // Faces left in pieces cut off from the rest and small -- shreds of a ribbon's rim the
        // flood did not reach -- go with it.
        static void DropShreds(RemeshTopology topology, bool[] removed, float cell, Stats stats)
        {
            var nt = topology.triangleCount;
            var twin = topology.halfEdgeTwin;
            var P = topology.positions;
            var T = topology.triangles;
            var component = new int[nt];
            for (var t = 0; t < nt; t++) component[t] = -1;
            var queue = new Queue<int>();
            var members = new List<int>();
            var limit = 4f * cell * cell;
            var id = 0;
            for (var seed = 0; seed < nt; seed++)
            {
                if (removed[seed] || component[seed] >= 0) continue;
                members.Clear();
                component[seed] = id;
                queue.Enqueue(seed);
                var area = 0f;
                var touchesRemoved = false;
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    members.Add(t);
                    area += 0.5f * Vector3.Cross(P[T[3 * t + 1]] - P[T[3 * t]], P[T[3 * t + 2]] - P[T[3 * t]]).magnitude;
                    for (var i = 0; i < 3; i++)
                    {
                        var o = twin[3 * t + i];
                        if (o < 0) continue;
                        var g = o / 3;
                        if (removed[g]) { touchesRemoved = true; continue; }
                        if (component[g] < 0) { component[g] = id; queue.Enqueue(g); }
                    }
                }
                id++;
                if (!touchesRemoved || area > limit) continue;
                foreach (var t in members) { removed[t] = true; stats.shredTriangles++; }
            }
        }

        // Nearest point of the centre line: segment k, fraction f along it, and s, the fraction of
        // the whole line (below 0 or above 1 off the ends).
        static void Locate(Ribbon ribbon, Vector3 x, out int segment, out float fraction, out float s)
        {
            var c = ribbon.centre;
            var best = float.PositiveInfinity;
            segment = 0; fraction = 0f;
            var bestAlong = 0f;
            var along = 0f;
            for (var k = 0; k + 1 < c.Length; k++)
            {
                var d = c[k + 1] - c[k];
                var l2 = d.sqrMagnitude;
                var raw = l2 > 0f ? Vector3.Dot(x - c[k], d) / l2 : 0f;
                var f = Mathf.Clamp01(raw);
                if (k == 0 && raw < 0f) f = raw;
                if (k + 2 == c.Length && raw > 1f) f = raw;
                var dist = (c[k] + d * f - x).sqrMagnitude;
                if (dist < best) { best = dist; segment = k; fraction = f; bestAlong = along + f * Mathf.Sqrt(l2); }
                along += Mathf.Sqrt(l2);
            }
            s = ribbon.length > 0f ? bestAlong / ribbon.length : 0f;
        }

        // The cross-section frame at fraction f of segment k.
        static void Frame(Ribbon ribbon, int k, float f, out Vector3 c, out Vector3 tangent, out Vector3 up,
            out Vector3 across, out float width, out float thickness)
        {
            var m = ribbon.centre.Length;
            var k1 = Mathf.Min(m - 1, k + 1);
            var g = Mathf.Clamp01(f);
            c = ribbon.centre[k] + (ribbon.centre[k1] - ribbon.centre[k]) * f;
            tangent = MeshMath.SafeNormalize(ribbon.centre[k1] - ribbon.centre[k], Vector3.forward);
            up = MeshMath.SafeNormalize(Vector3.Lerp(ribbon.up[k], ribbon.up[k1], g), ribbon.up[k]);
            up = MeshMath.SafeNormalize(up - tangent * Vector3.Dot(up, tangent), OrientationField.ArbitraryTangent(tangent));
            across = Vector3.Cross(up, tangent);
            width = Mathf.Lerp(ribbon.width[k], ribbon.width[k1], g);
            thickness = Mathf.Lerp(ribbon.thickness[k], ribbon.thickness[k1], g);
        }

        // ------------------------------------------------------------------
        // The input without the removed triangles, its holes zipped shut.
        // The removed faces' connected regions mostly of ribbons lying on the surface go back in,
        // pressed flat (see flattenLying): successive over-relaxation of each inner vertex towards
        // its neighbours' mean, the vertices also on other faces held. Returns the source
        // positions with those moved, or null when no region is flattened.
        static Vector3[] FlattenOnSurface(RemeshTopology topology, Vector3[] positions, bool[] removed,
            bool[] lyingFace, bool[] freeFace, Stats stats)
        {
            var T = topology.triangles;
            var twin = topology.halfEdgeTwin;
            var nt = topology.triangleCount;
            var nv = topology.vertexCount;
            var region = new int[nt];
            for (var t = 0; t < nt; t++) region[t] = -1;
            var lying = new List<int>();
            var free = new List<int>();
            var stack = new Stack<int>();
            for (var seed = 0; seed < nt; seed++)
            {
                if (!removed[seed] || region[seed] >= 0) continue;
                var id = lying.Count;
                int l = 0, f = 0;
                region[seed] = id;
                stack.Push(seed);
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    if (lyingFace[t]) l++;
                    if (freeFace[t]) f++;
                    for (var i = 0; i < 3; i++)
                    {
                        var o = twin[3 * t + i];
                        if (o >= 0 && removed[o / 3] && region[o / 3] < 0) { region[o / 3] = id; stack.Push(o / 3); }
                    }
                }
                lying.Add(l);
                free.Add(f);
            }
            var flat = new bool[nt];
            var any = false;
            for (var t = 0; t < nt; t++)
                if (region[t] >= 0 && lying[region[t]] > free[region[t]])
                {
                    flat[t] = true;
                    removed[t] = false;
                    stats.flattenedTriangles++;
                    any = true;
                }
            if (!any) return null;

            // Inner vertices: on flattened faces only.
            var onFlat = new bool[nv];
            var onOther = new bool[nv];
            for (var t = 0; t < nt; t++)
                for (var i = 0; i < 3; i++)
                {
                    var v = T[3 * t + i];
                    if (flat[t]) onFlat[v] = true; else onOther[v] = true;
                }
            var inner = new List<int>();
            for (var v = 0; v < nv; v++) if (onFlat[v] && !onOther[v] && !topology.isBoundaryVertex[v]) inner.Add(v);
            var X = (Vector3[])topology.positions.Clone();
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;
            const float omega = 1.85f;
            for (var iteration = 0; iteration < flattenIterations; iteration++)
                foreach (var v in inner)
                {
                    var n = start[v + 1] - start[v];
                    if (n == 0) continue;
                    var sum = Vector3.zero;
                    for (var e = start[v]; e < start[v + 1]; e++) sum += X[adjacency[e]];
                    X[v] += omega * (sum / n - X[v]);
                }
            var isInner = new bool[nv];
            foreach (var v in inner) isInner[v] = true;
            var moved = (Vector3[])positions.Clone();
            for (var i = 0; i < moved.Length; i++)
            {
                var w = topology.sourceToWelded[i];
                if (w >= 0 && isInner[w]) moved[i] = X[w];
            }
            return moved;
        }

        static void Rebuild(Vector3[] positions, int[] triangles, RemeshTopology topology, bool[] removed,
            out Vector3[] keptPositions, out int[] keptTriangles, out int[] sourceOfTriangle, Stats stats)
        {
            var T = topology.triangles;
            var twin = topology.halfEdgeTwin;
            var nt = topology.triangleCount;
            var removedSource = new bool[triangles.Length / 3];
            for (var t = 0; t < nt; t++)
                if (removed[t]) { removedSource[topology.sourceTriangle[t]] = true; stats.removedTriangles++; }

            // A source vertex for each welded one.
            var sourceVertex = new int[topology.vertexCount];
            for (var i = 0; i < sourceVertex.Length; i++) sourceVertex[i] = -1;
            for (var i = topology.sourceToWelded.Length - 1; i >= 0; i--)
            {
                var w = topology.sourceToWelded[i];
                if (w >= 0) sourceVertex[w] = i;
            }

            var outTriangles = new List<int>(triangles.Length);
            var outSource = new List<int>(triangles.Length / 3);
            for (var t = 0; t * 3 + 2 < triangles.Length; t++)
            {
                if (removedSource[t]) continue;
                outTriangles.Add(triangles[3 * t]);
                outTriangles.Add(triangles[3 * t + 1]);
                outTriangles.Add(triangles[3 * t + 2]);
                outSource.Add(t);
            }

            // The new border: kept half-edges whose twin was removed, by start vertex.
            var next = new Dictionary<int, List<int>>();
            for (var t = 0; t < nt; t++)
            {
                if (removed[t]) continue;
                for (var i = 0; i < 3; i++)
                {
                    var h = 3 * t + i;
                    var o = twin[h];
                    if (o < 0 || !removed[o / 3]) continue;
                    var a = topology.halfEdgeVertex[h];
                    if (!next.TryGetValue(a, out var l)) next[a] = l = new List<int>(1);
                    l.Add(h);
                }
            }
            var used = new HashSet<int>();
            var loops = new List<List<int>>();
            foreach (var start in next)
                foreach (var h0 in start.Value)
                {
                    if (used.Contains(h0)) continue;
                    var loop = new List<int>();
                    var h = h0;
                    for (var guard = 0; guard < 1 << 20; guard++)
                    {
                        if (!used.Add(h)) break;
                        loop.Add(h);
                        var b = topology.HalfEdgeTarget(h);
                        if (!next.TryGetValue(b, out var l)) break;
                        var found = -1;
                        foreach (var x in l) if (!used.Contains(x)) { found = x; break; }
                        if (found < 0) break;
                        h = found;
                    }
                    if (loop.Count >= 3) loops.Add(loop);
                }

            // The removed faces' connected regions: a band taken out all round a closed line (a rim)
            // leaves a ring-shaped hole -- two loops of one region -- stitched loop to loop, not each
            // filled as a disc (that spanned the helmet's opening, 2026-10-19).
            var region = new int[nt];
            for (var t = 0; t < nt; t++) region[t] = -1;
            var regions = 0;
            var stack = new Stack<int>();
            for (var seed = 0; seed < nt; seed++)
            {
                if (!removed[seed] || region[seed] >= 0) continue;
                region[seed] = regions;
                stack.Push(seed);
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    for (var i = 0; i < 3; i++)
                    {
                        var o = twin[3 * t + i];
                        if (o >= 0 && removed[o / 3] && region[o / 3] < 0) { region[o / 3] = regions; stack.Push(o / 3); }
                    }
                }
                regions++;
            }
            var loopsOfRegion = new Dictionary<int, List<int>>();
            for (var li = 0; li < loops.Count; li++)
            {
                var r = region[twin[loops[li][0]] / 3];
                if (!loopsOfRegion.TryGetValue(r, out var l)) loopsOfRegion[r] = l = new List<int>();
                l.Add(li);
            }
            if (debugLog != null)
                foreach (var kv in loopsOfRegion)
                {
                    var sizes = new System.Text.StringBuilder();
                    var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var hi = -lo;
                    foreach (var li in kv.Value)
                    {
                        sizes.Append(' ').Append(loops[li].Count);
                        foreach (var x in loops[li]) { var q = topology.positions[topology.halfEdgeVertex[x]]; lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
                    }
                    Log("hole loops" + sizes + " size " + ((hi - lo).magnitude / topology.averageEdgeLength).ToString("F0") + " edges", 0.5f * (lo + hi), 0f, 0f, 0f, 1f);
                }
            var stitched = new HashSet<int>();
            if (stitchRings)
                foreach (var kv in loopsOfRegion)
                {
                    if (kv.Value.Count != 2) continue;
                    List<int> la = loops[kv.Value[0]], lb = loops[kv.Value[1]];
                    var ra = new List<int>(la.Count);
                    foreach (var x in la) ra.Add(topology.halfEdgeVertex[x]);
                    var rb = new List<int>(lb.Count);
                    foreach (var x in lb) rb.Add(topology.halfEdgeVertex[x]);
                    int na = ra.Count, nb = rb.Count;
                    var b0 = 0;
                    var best = float.PositiveInfinity;
                    for (var j = 0; j < nb; j++)
                    {
                        var d = (topology.positions[rb[j]] - topology.positions[ra[0]]).sqrMagnitude;
                        if (d < best) { best = d; b0 = j; }
                    }
                    var source = topology.sourceTriangle[la[0] / 3];
                    // One loop forward and the other backward go the same way round the ring.
                    int fa = 0, fb = b0, doneA = 0, doneB = 0;
                    while (doneA < na || doneB < nb)
                    {
                        var na1 = (fa + 1) % na;
                        var nb1 = (fb - 1 + nb) % nb;
                        bool advanceA;
                        if (doneA >= na) advanceA = false;
                        else if (doneB >= nb) advanceA = true;
                        else
                            advanceA = (topology.positions[ra[na1]] - topology.positions[rb[fb]]).sqrMagnitude
                                       <= (topology.positions[rb[nb1]] - topology.positions[ra[fa]]).sqrMagnitude;
                        if (advanceA)
                        {
                            Emit(ra[na1], ra[fa], rb[fb], source);
                            fa = na1; doneA++;
                        }
                        else
                        {
                            Emit(ra[fa], rb[fb], rb[nb1], source);
                            fb = nb1; doneB++;
                        }
                    }
                    stats.holes++;
                    stitched.Add(kv.Value[0]);
                    stitched.Add(kv.Value[1]);
                }

            void Emit(int a, int b, int c, int source)
            {
                int sa = sourceVertex[a], sb = sourceVertex[b], sc = sourceVertex[c];
                if (sa < 0 || sb < 0 || sc < 0 || sa == sb || sb == sc || sa == sc) return;
                outTriangles.Add(sa); outTriangles.Add(sb); outTriangles.Add(sc);
                outSource.Add(source);
                stats.fillTriangles++;
            }

            for (var li = 0; li < loops.Count; li++)
                {
                    if (stitched.Contains(li)) continue;
                    var loop = loops[li];
                    stats.holes++;
                    // The hole a band leaves where it ran into another part is its cut section:
                    // long and thin. Zipped shut from one end of it to the other -- a fan round its
                    // middle would stand a star of slivers up off the surface.
                    var ring = new List<int>(loop.Count);
                    foreach (var x in loop) ring.Add(topology.halfEdgeVertex[x]);
                    var n = ring.Count;
                    int e0 = 0, e1 = 0;
                    var far = -1f;
                    for (var i = 0; i < n; i++)
                        for (var j = i + 1; j < n; j++)
                        {
                            var d = (topology.positions[ring[i]] - topology.positions[ring[j]]).sqrMagnitude;
                            if (d > far) { far = d; e0 = i; e1 = j; }
                        }
                    // Two chains from e0 to e1: forward (along the loop) and backward.
                    var source = topology.sourceTriangle[loop[0] / 3];
                    // The loop runs the kept faces' way round; the fill goes the other way.
                    var fwd = e0; var bwd = e0;
                    var fwdEnd = e1; var bwdEnd = e1;
                    var stepsF = (e1 - e0 + n) % n;
                    var stepsB = (e0 - e1 + n) % n;
                    int doneF = 0, doneB = 0;
                    while (doneF < stepsF || doneB < stepsB)
                    {
                        var nf = (fwd + 1) % n;
                        var nb = (bwd - 1 + n) % n;
                        bool advanceF;
                        if (doneF >= stepsF) advanceF = false;
                        else if (doneB >= stepsB) advanceF = true;
                        else
                            advanceF = (topology.positions[ring[nf]] - topology.positions[ring[bwd]]).sqrMagnitude
                                       <= (topology.positions[ring[nb]] - topology.positions[ring[fwd]]).sqrMagnitude;
                        if (advanceF)
                        {
                            Emit(ring[nf], ring[fwd], ring[bwd], source);
                            fwd = nf; doneF++;
                        }
                        else
                        {
                            Emit(ring[fwd], ring[bwd], ring[nb], source);
                            bwd = nb; doneB++;
                        }
                    }
                }

            keptPositions = positions;
            keptTriangles = outTriangles.ToArray();
            sourceOfTriangle = outSource.ToArray();
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// The ribbons as quad tubes: a cross-section ring every stationSpacingCells along, two
        /// broad sides of width / quadWidthCells quads (at least one) and a narrow quad over each
        /// rim. Each ring is read off the part itself: its faces cut by the plane across the line
        /// there, the ring's corners the section's extreme points in the frame's four diagonal
        /// directions. Free ends are capped. <paramref name="cell"/> is the edge length the tubes
        /// are made at -- the rest of the output's, so the ribbons match it.
        /// </summary>
        public static void Mesh(List<Ribbon> ribbons, SpatialGrid surface, float cell, float outward,
            out Vector3[] positions, out List<int[]> faces) =>
            Mesh(ribbons, null, surface, cell, outward, out positions, out faces);

        /// <summary>As above, with the buckles as boxes of about a cell per quad.</summary>
        public static void Mesh(List<Ribbon> ribbons, List<Block> blocks, SpatialGrid surface, float cell, float outward,
            out Vector3[] positions, out List<int[]> faces)
        {
            var outPositions = new List<Vector3>();
            var outFaces = new List<int[]>();
            if (blocks != null)
                foreach (var block in blocks) BoxMesh(block, cell, outward, outPositions, outFaces);
            foreach (var ribbon in ribbons)
            {
                var k = Mathf.Max(1, Mathf.RoundToInt(ribbon.medianWidth / (quadWidthCells * cell)));
                var ringSize = 2 * k + 2;
                var startExtra = ribbon.startTrim + (ribbon.freeStart ? 0f : embedCells * cell);
                var endExtra = ribbon.endTrim + (ribbon.freeEnd ? 0f : embedCells * cell);
                var total = ribbon.length + startExtra + endExtra;
                var stations = Mathf.Max(1, Mathf.RoundToInt(total / (stationSpacingCells * cell)));
                var step = total / stations;

                // The part's faces, by where along the line they lie.
                var corners = ribbon.faceCorners;
                var faceCount = corners == null ? 0 : corners.Count / 3;
                var faceAlong = new float[faceCount];
                for (var f = 0; f < faceCount; f++)
                {
                    Locate(ribbon, (corners[3 * f] + corners[3 * f + 1] + corners[3 * f + 2]) / 3f, out _, out _, out var s);
                    faceAlong[f] = s * ribbon.length;
                }

                // The regular box: the median of the part's own sections' extents along it.
                Vector2[] regular = null;
                if (!readSections)
                {
                    var x0 = new List<float>(); var x1 = new List<float>();
                    var y0 = new List<float>(); var y1 = new List<float>();
                    for (var i = 0; i <= stations; i++)
                    {
                        var along = -startExtra + step * i;
                        if (along < 0f || along > ribbon.length) continue;
                        Station(ribbon, along, out var c, out var tangent, out var up, out var across, out var w, out _);
                        var xy = Section(corners, faceAlong, along, 0.75f * step + 0.25f * w, c, tangent, across, up, k, w);
                        if (xy == null) continue;
                        float lx = float.PositiveInfinity, hx = float.NegativeInfinity, ly = float.PositiveInfinity, hy = float.NegativeInfinity;
                        foreach (var q in xy)
                        {
                            lx = Mathf.Min(lx, q.x); hx = Mathf.Max(hx, q.x);
                            ly = Mathf.Min(ly, q.y); hy = Mathf.Max(hy, q.y);
                        }
                        x0.Add(lx); x1.Add(hx); y0.Add(ly); y1.Add(hy);
                    }
                    float Median(List<float> l) { l.Sort(); return l[l.Count / 2]; }
                    if (x0.Count >= 2)
                    {
                        float left = Median(x0), right = Median(x1), bottom = Median(y0), top = Median(y1);
                        if (right - left > 0.25f * ribbon.medianWidth && top - bottom > 0f)
                        {
                            if (ribbon.lying) bottom -= lyingEmbedCells * cell;
                            regular = Box(k, right - left, top, bottom);
                            for (var j = 0; j < regular.Length; j++) regular[j].x += 0.5f * (left + right);
                        }
                    }
                }

                // Per station, the ring in the frame's coordinates (across, up) about the centre.
                var ringXY = new List<Vector2[]>();
                var frames = new List<(Vector3 c, Vector3 across, Vector3 up)>();
                var measured = new List<bool>();
                for (var i = 0; i <= stations; i++)
                {
                    var along = -startExtra + step * i;
                    Station(ribbon, along, out var c, out var tangent, out var up, out var across, out var w, out var th);
                    frames.Add((c, across, up));
                    if (!readSections)
                    {
                        // A regular rectangle: the median width and thickness; a band lying on the
                        // surface goes on down into it.
                        var top = 0.5f * ribbon.medianThickness;
                        var bottom = ribbon.lying ? -top - lyingEmbedCells * cell : -top;
                        ringXY.Add(regular ?? Box(k, ribbon.medianWidth, top, bottom));
                        measured.Add(true);
                        continue;
                    }
                    var xy = Section(corners, faceAlong, along, 0.75f * step + 0.25f * w, c, tangent, across, up, k, w);
                    measured.Add(xy != null);
                    ringXY.Add(xy ?? Box(k, w, th));
                }
                // Stations with no section (past the part's ends, or a gap) take the nearest measured one's shape.
                for (var i = 0; i <= stations; i++)
                {
                    if (measured[i]) continue;
                    for (var d = 1; d <= stations; d++)
                    {
                        if (i - d >= 0 && measured[i - d]) { ringXY[i] = ringXY[i - d]; break; }
                        if (i + d <= stations && measured[i + d]) { ringXY[i] = ringXY[i + d]; break; }
                    }
                }

                var rings = new List<int>();
                for (var i = 0; i <= stations; i++)
                {
                    var (c, across, up) = frames[i];
                    rings.Add(outPositions.Count);
                    foreach (var q in ringXY[i]) outPositions.Add(c + across * q.x + up * q.y);
                }
                var firstFace = outFaces.Count;
                for (var i = 0; i < stations; i++)
                    for (var j = 0; j < ringSize; j++)
                    {
                        // A band lying on the surface has no bottom: it is inside the surface,
                        // where nothing sees it and a normal map bake finds nothing for it.
                        if (ribbon.lying && openBottoms && j > k && j <= 2 * k) continue;
                        var j1 = (j + 1) % ringSize;
                        outFaces.Add(new[] { rings[i] + j, rings[i] + j1, rings[i + 1] + j1, rings[i + 1] + j });
                    }
                // Outward, with the source's sense of out: the first broad face against its up.
                if (outFaces.Count > firstFace)
                {
                    var f = outFaces[firstFace];
                    var n = Vector3.Cross(outPositions[f[1]] - outPositions[f[0]], outPositions[f[3]] - outPositions[f[0]]);
                    if (Vector3.Dot(n, frames[0].up) * outward < 0f)
                        for (var x = firstFace; x < outFaces.Count; x++) Array.Reverse(outFaces[x]);
                }
                void Cap(int ring, int other)
                {
                    var capFirst = outFaces.Count;
                    for (var j = 0; j < k; j++)
                        outFaces.Add(new[] { ring + j, ring + j + 1, ring + k + 1 + (k - j - 1), ring + k + 1 + (k - j) });
                    // Facing away from the tube.
                    var c = Vector3.zero;
                    var c2 = Vector3.zero;
                    for (var j = 0; j < ringSize; j++) { c += outPositions[ring + j]; c2 += outPositions[other + j]; }
                    var f = outFaces[capFirst];
                    var n = Vector3.Cross(outPositions[f[1]] - outPositions[f[0]], outPositions[f[3]] - outPositions[f[0]]);
                    if (Vector3.Dot(n, c - c2) * outward < 0f)
                        for (var x = capFirst; x < outFaces.Count; x++) Array.Reverse(outFaces[x]);
                }
                if (ribbon.freeStart && rings.Count > 1) Cap(rings[0], rings[1]);
                if (ribbon.freeEnd && rings.Count > 1) Cap(rings[rings.Count - 1], rings[rings.Count - 2]);
            }
            // Tube quads wholly inside a slider's box are never seen (nor baked to): dropped.
            if (blocks != null && openBottoms)
            {
                bool Inside(Vector3 p)
                {
                    foreach (var b in blocks)
                    {
                        if (!b.closed) continue;
                        var d = p - b.centre;
                        // Strictly inside: the box's own corners are on it, not in it.
                        var h = 0.98f * b.halfSize;
                        if (Mathf.Abs(Vector3.Dot(d, b.axisX)) < h.x && Mathf.Abs(Vector3.Dot(d, b.axisY)) < h.y
                            && Mathf.Abs(Vector3.Dot(d, b.up)) < h.z) return true;
                    }
                    return false;
                }
                var inside = new bool[outPositions.Count];
                for (var i = 0; i < inside.Length; i++) inside[i] = Inside(outPositions[i]);
                outFaces.RemoveAll(f =>
                {
                    foreach (var v in f) if (!inside[v]) return false;
                    return true;
                });
            }
            positions = outPositions.ToArray();
            faces = outFaces;
        }

        // A closed box, its sides cut into quads of about a cell, wound outward.
        static void BoxMesh(Block block, float cell, float outward, List<Vector3> positions, List<int[]> faces)
        {
            var size = 2f * block.halfSize;
            var n = new[]
            {
                Mathf.Max(1, Mathf.RoundToInt(size.x / cell)),
                Mathf.Max(1, Mathf.RoundToInt(size.y / cell)),
                Mathf.Max(1, Mathf.RoundToInt(size.z / cell))
            };
            var axes = new[] { block.axisX, block.axisY, block.up };
            var index = new Dictionary<(int, int, int), int>();
            int Vertex(int i, int j, int k)
            {
                if (index.TryGetValue((i, j, k), out var id)) return id;
                id = positions.Count;
                var p = block.centre;
                p += axes[0] * (size.x * ((float)i / n[0] - 0.5f));
                p += axes[1] * (size.y * ((float)j / n[1] - 0.5f));
                p += axes[2] * (size.z * ((float)k / n[2] - 0.5f));
                positions.Add(p);
                index[(i, j, k)] = id;
                return id;
            }
            (int, int, int) At(int axis, int side, int u, int v)
            {
                var c = new int[3];
                c[axis] = side * n[axis];
                c[(axis + 1) % 3] = u;
                c[(axis + 2) % 3] = v;
                return (c[0], c[1], c[2]);
            }
            for (var axis = 0; axis < 3; axis++)
                for (var side = 0; side < 2; side++)
                {
                    // The bottom is inside the surface it stands in.
                    if (openBottoms && !block.closed && axis == 2 && side == 0) continue;
                    int nu = n[(axis + 1) % 3], nw = n[(axis + 2) % 3];
                    for (var u = 0; u < nu; u++)
                        for (var w = 0; w < nw; w++)
                        {
                            var q = new[] { At(axis, side, u, w), At(axis, side, u + 1, w), At(axis, side, u + 1, w + 1), At(axis, side, u, w + 1) };
                            var f = new int[4];
                            for (var i = 0; i < 4; i++) f[i] = Vertex(q[i].Item1, q[i].Item2, q[i].Item3);
                            // (axis+1) x (axis+2) = axis: the order above faces +axis; flip on the low side.
                            var flip = side == 0;
                            if (outward < 0f) flip = !flip;
                            if (flip) Array.Reverse(f);
                            faces.Add(f);
                        }
                }
        }

        // A plain rectangle ring, w wide and th through: top left to right, then bottom right to left.
        static Vector2[] Box(int k, float w, float th) => Box(k, w, 0.5f * th, -0.5f * th);

        // The same from top to bottom.
        static Vector2[] Box(int k, float w, float top, float bottom)
        {
            var r = new Vector2[2 * k + 2];
            for (var j = 0; j <= k; j++) r[j] = new Vector2(-0.5f * w + w * j / k, top);
            for (var j = 0; j <= k; j++) r[k + 1 + j] = new Vector2(0.5f * w - w * j / k, bottom);
            return r;
        }

        // The ring at one station from the part's own section, or null when the plane meets too
        // little of it. Points of the section within reach along the line of the station only.
        static Vector2[] Section(List<Vector3> corners, float[] faceAlong, float along, float reach,
            Vector3 c, Vector3 tangent, Vector3 across, Vector3 up, int k, float w)
        {
            if (corners == null) return null;
            var pts = new List<Vector2>();
            void Cut(Vector3 a, Vector3 b, float da, float db)
            {
                var p = a + (b - a) * (da / (da - db));
                pts.Add(new Vector2(Vector3.Dot(p - c, across), Vector3.Dot(p - c, up)));
            }
            for (var f = 0; f < faceAlong.Length; f++)
            {
                if (Mathf.Abs(faceAlong[f] - along) > reach) continue;
                Vector3 a = corners[3 * f], b = corners[3 * f + 1], d = corners[3 * f + 2];
                float sa = Vector3.Dot(a - c, tangent), sb = Vector3.Dot(b - c, tangent), sd = Vector3.Dot(d - c, tangent);
                if ((sa > 0) != (sb > 0)) Cut(a, b, sa, sb);
                if ((sb > 0) != (sd > 0)) Cut(b, d, sb, sd);
                if ((sd > 0) != (sa > 0)) Cut(d, a, sd, sa);
            }
            if (pts.Count < 8) return null;
            // Points far off the line belong to another stretch of the part the plane also cuts.
            var limit = 1.25f * w;
            pts.RemoveAll(p => p.magnitude > limit);
            if (pts.Count < 8) return null;
            Vector2 Extreme(Vector2 dir)
            {
                var best = pts[0];
                var bestDot = float.NegativeInfinity;
                foreach (var p in pts)
                {
                    var d = Vector2.Dot(p, dir);
                    if (d > bestDot) { bestDot = d; best = p; }
                }
                return best;
            }
            // Diagonals weighted towards across, so a flat band's corners land at its rims.
            var topLeft = Extreme(new Vector2(-1f, 0.5f));
            var topRight = Extreme(new Vector2(1f, 0.5f));
            var bottomRight = Extreme(new Vector2(1f, -0.5f));
            var bottomLeft = Extreme(new Vector2(-1f, -0.5f));
            var r = new Vector2[2 * k + 2];
            for (var j = 0; j <= k; j++)
            {
                var t = j / (float)k;
                var x = Mathf.Lerp(topLeft.x, topRight.x, t);
                // In between, the top of the section at that x.
                r[j] = j == 0 ? topLeft : j == k ? topRight : new Vector2(x, Top(pts, x, 1f, Mathf.Lerp(topLeft.y, topRight.y, t)));
                var xb = Mathf.Lerp(bottomRight.x, bottomLeft.x, t);
                r[k + 1 + j] = j == 0 ? bottomRight : j == k ? bottomLeft : new Vector2(xb, Top(pts, xb, -1f, Mathf.Lerp(bottomRight.y, bottomLeft.y, t)));
            }
            return r;
        }

        // The section's highest (side +1) or lowest (side -1) point near across-coordinate x.
        static float Top(List<Vector2> pts, float x, float side, float fallback)
        {
            var best = float.NegativeInfinity;
            var bestY = fallback;
            var window = float.PositiveInfinity;
            foreach (var p in pts) window = Mathf.Min(window, Mathf.Abs(p.x - x));
            window = Mathf.Max(window * 2f, 1e-6f);
            foreach (var p in pts)
            {
                if (Mathf.Abs(p.x - x) > window) continue;
                if (p.y * side > best) { best = p.y * side; bestY = p.y; }
            }
            return bestY;
        }

        // The frame at a distance along the centre line (extrapolated off its ends).
        static void Station(Ribbon ribbon, float along, out Vector3 c, out Vector3 tangent, out Vector3 up,
            out Vector3 across, out float width, out float thickness)
        {
            var line = ribbon.centre;
            var acc = 0f;
            var k = 0;
            var f = 0f;
            if (along <= 0f)
            {
                var l0 = Vector3.Distance(line[0], line[1]);
                f = l0 > 0f ? along / l0 : 0f;
            }
            else
            {
                for (k = 0; k + 1 < line.Length; k++)
                {
                    var l = Vector3.Distance(line[k], line[k + 1]);
                    if (acc + l >= along || k + 2 == line.Length)
                    {
                        f = l > 0f ? (along - acc) / l : 0f;
                        break;
                    }
                    acc += l;
                }
            }
            Frame(ribbon, k, f, out c, out tangent, out up, out across, out width, out thickness);
        }

        // ------------------------------------------------------------------
        sealed class MinHeap
        {
            int[] m_Items;
            float[] m_Keys;
            public int Count { get; private set; }

            public MinHeap(int capacity)
            {
                m_Items = new int[Math.Max(4, capacity)];
                m_Keys = new float[m_Items.Length];
            }

            public void Push(int item, float key)
            {
                if (Count == m_Items.Length)
                {
                    Array.Resize(ref m_Items, Count * 2);
                    Array.Resize(ref m_Keys, Count * 2);
                }
                var i = Count++;
                while (i > 0)
                {
                    var p = (i - 1) / 2;
                    if (m_Keys[p] <= key) break;
                    m_Items[i] = m_Items[p]; m_Keys[i] = m_Keys[p];
                    i = p;
                }
                m_Items[i] = item; m_Keys[i] = key;
            }

            public void Pop(out int item, out float key)
            {
                item = m_Items[0]; key = m_Keys[0];
                var lastItem = m_Items[--Count];
                var lastKey = m_Keys[Count];
                var i = 0;
                while (true)
                {
                    var l = 2 * i + 1;
                    if (l >= Count) break;
                    var r = l + 1;
                    var c = r < Count && m_Keys[r] < m_Keys[l] ? r : l;
                    if (m_Keys[c] >= lastKey) break;
                    m_Items[i] = m_Items[c]; m_Keys[i] = m_Keys[c];
                    i = c;
                }
                m_Items[i] = lastItem; m_Keys[i] = lastKey;
            }
        }
    }
}
