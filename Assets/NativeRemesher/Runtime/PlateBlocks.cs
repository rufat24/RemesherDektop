// The lattice's quads on a flat plate merged nine at a time, so the plate keeps its rows.
//
// On a plate the lattice is a grid whose lines follow the plate's edges, but as fine as the curved
// parts need, and QuadSimplify's merges after it leave the plate's quads big but in no order (each
// collapse a 3-5 valence pair). So before QuadSimplify, blocks of 3 x 3 of the lattice's quads are
// merged into one:
//
//   regions   faces grown up to the creases (a fold of edgeDegrees between neighbours, or of
//             heldDegrees along a crease's points) within regionDegrees of the run's normal.
//   grids     from a region's deepest quads, blocks laid beside blocks along the lattice's own
//             lines, each block placed in its grid; a block only where its four inner points are in
//             four quads, all sixteen points inside the region, and each within maxOffCells of the
//             bilinear patch its corners span. The lattice's singular points split a region into
//             patches one grid cannot cross, so up to maxGrids grids are grown, each from what the
//             last left.
//   squares   in each grid the biggest rectangles whose blocks meet corner to corner, each a block
//             clear of the last.
//   ring      the row of lattice quads round a rectangle (its blocks' sides are cut at thirds that
//             nothing beyond has) laid again from the region side's points onto the rectangle's
//             corners: the cheapest path through the two loops, in steps of a quad per edge, three
//             edges onto one in four quads about two new points (a 3-5 pair at each end), a quad
//             round a corner, or a triangle where nothing else closes. The ring's new points are
//             smoothed while every face stays convex with no corner under minCornerDegrees; a ring
//             that will not lay takes the rectangle's row or column nearest its worst face off.
//
// The merged blocks' points are pinned for what comes after (QuadSimplify neither moves nor merges
// them); the ring is not, and is merged into its surroundings like the rest.
//
// Measured on the armoured car at 5,000 (lattice 19,741 faces, then QuadSimplify held to 0.03 cells,
// in the .NET harness, sampled both ways against the source in thousandths of its diagonal, mean):
// without blocks 15,049 faces at 0.78 / 0.50; with them 8 rectangles of 38 blocks, 15,096 faces at
// 0.74 / 0.49 -- the skirts, the turret's and the front plates in rows of big quads where they were
// fans. The big side panels take few blocks: their lattice has singular points every few quads.
// With the ring pinned too: 15,447 faces, a dense band round each rectangle.
//
// Tried and dropped: filling a plate again from scratch (a Coons patch between four corners, or a
// grid along the plate's frame clipped to it, with the same ring onto the plate's points) -- only a
// few of the car's plates have four corners and no holes, and against QuadSimplify's dense, jagged
// plate edges or the grid's staircase along slanted edges the ring came out with sliver faces; and
// any blocks' outline rather than rectangles -- a concave corner leaves the ring an odd step it
// cannot lay cleanly, and dropping blocks there cascaded.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher
{
    public static class PlateBlocks
    {
        public sealed class Stats
        {
            public int regions, rectangles, blocks, cuts, facesBefore, facesAfter;
            public long ms;
            public override string ToString() =>
                $"plate blocks: {regions} regions, {rectangles} rectangles of {blocks} blocks of 3 x 3 ({cuts} rows cut for the ring), " +
                $"{facesBefore} -> {facesAfter} faces, {ms} ms";
        }

        /// <summary>A region grows across edges whose faces turn less than this, degrees...</summary>
        public static float edgeDegrees = 10f;

        /// <summary>...and across a crease's points only where they turn less than this.</summary>
        public static float heldDegrees = 3f;

        /// <summary>Every face of a region is within this of the region's normal, degrees.</summary>
        public static float regionDegrees = 25f;

        /// <summary>Regions smaller than this, cells squared, are left.</summary>
        public static float minAreaCells = 6f;

        /// <summary>A grid is grown from this many of the region's deepest quads, the most blocks kept.</summary>
        public static int seedTries = 9;

        /// <summary>Grids of blocks grown on one region at most, each from what the last left.</summary>
        public static int maxGrids = 6;

        /// <summary>Rectangles of fewer blocks than this are left as the lattice laid them.</summary>
        public static int minRectangleBlocks = 2;

        /// <summary>Rows or columns a rectangle may lose where its ring will not lay.</summary>
        public static int cutRounds = 40;

        /// <summary>Sweeps of smoothing over the ring's new points (each kept only while every face stays good).</summary>
        public static int smoothSweeps = 6;

        /// <summary>No face of a ring has a corner under this, degrees.</summary>
        public static float minCornerDegrees = 20f;

        /// <summary>Whether the ring's points are pinned too, or only the merged blocks'.</summary>
        public static bool pinRing = false;

        /// <summary>
        /// The lattice's quads on flat regions merged in blocks of 3 x 3 in place. Returns the new
        /// positions; <paramref name="origin"/> gives each one's old index (-1 for a new point),
        /// <paramref name="pinned"/> marks the merged blocks' points (and the rings', with pinRing),
        /// which later passes should neither move nor merge.
        /// </summary>
        public static Vector3[] Apply(Vector3[] positions, List<int[]> faces, bool[] hold, float cell, SpatialGrid surface,
            float maxOffCells, out int[] origin, out bool[] pinned, out Stats stats)
        {
            var watch = Stopwatch.StartNew();
            var st = new Stats { facesBefore = faces.Count };
            var m = faces.Count;
            var n = positions.Length;

            var vectorArea = new Vector3[m];
            var area = new float[m];
            var he = new Dictionary<long, int>(4 * m, EdgeKey.Comparer);
            var valence = new int[n];
            for (var f = 0; f < m; f++)
            {
                var face = faces[f];
                var s = Vector3.zero;
                for (var k = 0; k < face.Length; k++)
                {
                    s += Vector3.Cross(positions[face[k]], positions[face[(k + 1) % face.Length]]);
                    he[EdgeKey.Pack(face[k], face[(k + 1) % face.Length])] = f;
                    valence[face[k]]++;
                }
                vectorArea[f] = 0.5f * s;
                area[f] = vectorArea[f].magnitude;
            }

            var regions = Regions(positions, faces, hold, cell, vectorArea, area, he, out var regionOf);
            st.regions = regions.Count;

            // Each point's region when every face round it is in that region and no edge from it is open.
            var vertexRegion = new int[n];
            for (var v = 0; v < n; v++) vertexRegion[v] = -1;
            for (var f = 0; f < m; f++)
            {
                var face = faces[f];
                for (var k = 0; k < face.Length; k++)
                {
                    int v = face[k], w = face[(k + 1) % face.Length];
                    var r = regionOf[f] >= 0 ? regionOf[f] : -2;
                    vertexRegion[v] = vertexRegion[v] == -1 ? r : vertexRegion[v] == r ? r : -2;
                    if (!he.ContainsKey(EdgeKey.Pack(w, v))) { vertexRegion[v] = -2; vertexRegion[w] = -2; }
                }
            }

            // The quad with edge p -> q, as (p, q, r, s).
            bool Next2(int p, int q, out int r, out int s, out int face)
            {
                r = s = -1;
                if (!he.TryGetValue(EdgeKey.Pack(p, q), out face)) return false;
                var g = faces[face];
                if (g.Length != 4) return false;
                var k = Array.IndexOf(g, p);
                if (k < 0 || g[(k + 1) & 3] != q) return false;
                r = g[(k + 2) & 3];
                s = g[(k + 3) & 3];
                return true;
            }

            var tolerance = maxOffCells * cell;
            // A block from the quad (a, b, c, d) at its corner (0, 0): its 4 x 4 points (x * 4 + y)
            // and 3 x 3 quads (x * 3 + y), or false.
            bool Build(int a, int b, int c, int d, int region, HashSet<int> taken, out int[] grid, out int[] quads)
            {
                var g = new int[16];
                for (var i = 0; i < 16; i++) g[i] = -1;
                var q = new int[9];
                grid = g;
                quads = q;
                bool Set(int x, int y, int v)
                {
                    var i = x * 4 + y;
                    if (g[i] < 0) { g[i] = v; return true; }
                    return g[i] == v;
                }
                g[0] = a; g[4] = b; g[5] = c; g[1] = d;
                // Up the first column of quads by their bottom edges, then across by their left ones.
                for (var y = 0; y < 3; y++)
                {
                    if (!Next2(g[y], g[4 + y], out var r, out var s, out var face)) return false;
                    if (!Set(1, y + 1, r) || !Set(0, y + 1, s)) return false;
                    q[y] = face;
                }
                for (var x = 1; x < 3; x++)
                    for (var y = 0; y < 3; y++)
                    {
                        if (!Next2(g[x * 4 + y + 1], g[x * 4 + y], out var r, out var s, out var face)) return false;
                        if (!Set(x + 1, y, r) || !Set(x + 1, y + 1, s)) return false;
                        q[x * 3 + y] = face;
                    }
                var seen = new HashSet<int>();
                foreach (var f in q) if (regionOf[f] != region || taken.Contains(f) || !seen.Add(f)) return false;
                seen.Clear();
                foreach (var v in g) if (vertexRegion[v] != region || !seen.Add(v)) return false;
                for (var x = 1; x < 3; x++)
                    for (var y = 1; y < 3; y++)
                    {
                        var v = g[x * 4 + y];
                        if (valence[v] != 4 || hold != null && hold[v]) return false;
                    }
                // Every point near the bilinear patch its corners span, along its normal.
                Vector3 p00 = positions[g[0]], p30 = positions[g[12]], p33 = positions[g[15]], p03 = positions[g[3]];
                var normal = Vector3.Cross(p33 - p00, p03 - p30).normalized;
                for (var x = 0; x < 4; x++)
                    for (var y = 0; y < 4; y++)
                    {
                        float u = x / 3f, w = y / 3f;
                        var on = (1 - u) * (1 - w) * p00 + u * (1 - w) * p30 + u * w * p33 + (1 - u) * w * p03;
                        if (Mathf.Abs(Vector3.Dot(positions[g[x * 4 + y]] - on, normal)) > tolerance) return false;
                    }
                return true;
            }

            // The corner quads of the four blocks beside a block, with the way to each.
            List<((int, int, int, int), int, int)> Beside(int[] g)
            {
                var list = new List<((int, int, int, int), int, int)>(4);
                if (Next2(g[13], g[12], out var r, out var s, out _)) list.Add(((g[12], r, s, g[13]), 1, 0));
                if (Next2(g[3], g[7], out r, out s, out _)) list.Add(((g[3], g[7], r, s), 0, 1));
                {
                    int p0 = g[0], p1 = g[1], x0 = -1, x1 = -1;
                    var ok = true;
                    for (var step = 0; step < 3 && ok; step++)
                    {
                        if (!Next2(p0, p1, out r, out s, out _)) { ok = false; break; }
                        if (step == 1) { x0 = s; x1 = r; }
                        p0 = s;
                        p1 = r;
                    }
                    if (ok) list.Add(((p0, x0, x1, p1), -1, 0));
                }
                {
                    int p0 = g[4], p1 = g[0], y0 = -1, y1 = -1;
                    var ok = true;
                    for (var step = 0; step < 3 && ok; step++)
                    {
                        if (!Next2(p0, p1, out r, out s, out _)) { ok = false; break; }
                        if (step == 1) { y0 = r; y1 = s; }
                        p0 = s;
                        p1 = r;
                    }
                    if (ok) list.Add(((p1, p0, y1, y0), 0, -1));
                }
                return list;
            }

            var points = new List<Vector3>(positions);
            var removed = new bool[m];
            var added = new List<int[]>();
            var addedBlock = new List<bool>();
            for (var region = 0; region < regions.Count; region++)
            {
                var members = regions[region];
                // How many quads in from the region's edge each face is.
                var depth = new Dictionary<int, int>();
                var queue = new Queue<int>();
                foreach (var f in members)
                    foreach (var v in faces[f])
                        if (vertexRegion[v] != region) { depth[f] = 0; queue.Enqueue(f); break; }
                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    var face = faces[f];
                    for (var k = 0; k < face.Length; k++)
                        if (he.TryGetValue(EdgeKey.Pack(face[(k + 1) % face.Length], face[k]), out var g)
                            && regionOf[g] == region && !depth.ContainsKey(g))
                        {
                            depth[g] = depth[f] + 1;
                            queue.Enqueue(g);
                        }
                }
                var deep = new List<int>();
                foreach (var f in members) if (faces[f].Length == 4 && depth.TryGetValue(f, out var dd) && dd >= 2) deep.Add(f);
                if (deep.Count == 0) continue;
                deep.Sort((a, b) => depth[b].CompareTo(depth[a]));

                // Grids of blocks, each the most from any of the first seeds left.
                var grids = new List<List<(int[] grid, int[] quads, int bx, int by)>>();
                var usedHere = new HashSet<int>();
                for (var round = 0; round < maxGrids; round++)
                {
                    List<(int[] grid, int[] quads, int bx, int by)> best = null;
                    var tries = 0;
                    foreach (var seed in deep)
                    {
                        if (tries >= seedTries) break;
                        if (usedHere.Contains(seed)) continue;
                        tries++;
                        var taken = new HashSet<int>(usedHere);
                        var trial = new List<(int[], int[], int, int)>();
                        var at = new HashSet<long>();
                        var tried = new HashSet<long>(EdgeKey.Comparer);
                        var pending = new Queue<(int, int, int, int, int, int)>();
                        var s0 = faces[seed];
                        pending.Enqueue((s0[0], s0[1], s0[2], s0[3], 0, 0));
                        while (pending.Count > 0)
                        {
                            var (a, b, c, d, bx, by) = pending.Dequeue();
                            if (at.Contains(Cell(bx, by)) || !tried.Add(EdgeKey.Pack(a, b))) continue;
                            if (!Build(a, b, c, d, region, taken, out var grid, out var quads)) continue;
                            foreach (var f in quads) taken.Add(f);
                            at.Add(Cell(bx, by));
                            trial.Add((grid, quads, bx, by));
                            foreach (var (q, dx, dy) in Beside(grid)) pending.Enqueue((q.Item1, q.Item2, q.Item3, q.Item4, bx + dx, by + dy));
                        }
                        if (best == null || trial.Count > best.Count) best = trial;
                    }
                    if (best == null || best.Count < minRectangleBlocks) break;
                    grids.Add(best);
                    foreach (var blk in best) usedHere.UnionWith(blk.quads);
                }
                if (grids.Count == 0) continue;

                // The region's frame, for laying the ring flat.
                var sumN = Vector3.zero;
                var centre = Vector3.zero;
                foreach (var f in members) { sumN += vectorArea[f]; foreach (var v in faces[f]) centre += positions[v] / faces[f].Length; }
                centre /= members.Count;
                var normal = sumN.normalized;
                var e1 = Vector3.Cross(normal, Mathf.Abs(normal.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
                var e2 = Vector3.Cross(normal, e1);

                foreach (var grid in grids)
                    foreach (var (rx0, ry0, rx1, ry1) in Rectangles(grid, minRectangleBlocks))
                    {
                        int x0 = rx0, y0 = ry0, x1 = rx1, y1 = ry1;
                        for (var round = 0; round <= cutRounds && x0 <= x1 && y0 <= y1; round++)
                        {
                            var blocks = new List<(int[] grid, int[] quads)>();
                            var place = new List<(int, int)>();
                            foreach (var blk in grid)
                                if (blk.bx >= x0 && blk.bx <= x1 && blk.by >= y0 && blk.by <= y1)
                                {
                                    blocks.Add((blk.grid, blk.quads));
                                    place.Add((blk.bx, blk.by));
                                }
                            if (blocks.Count < minRectangleBlocks) break;
                            var worst = Ring(blocks, out var result, out var fresh, out var replaced);
                            if (worst < 0)
                            {
                                foreach (var f in replaced) removed[f] = true;
                                var offset = points.Count;
                                points.AddRange(fresh);
                                for (var i = 0; i < result.Count; i++)
                                {
                                    var face = result[i];
                                    for (var k = 0; k < face.Length; k++) if (face[k] < 0) face[k] = offset + (-face[k] - 1);
                                    added.Add(face);
                                    // The merged blocks come first, then the ring.
                                    addedBlock.Add(i < blocks.Count);
                                }
                                st.blocks += blocks.Count;
                                st.rectangles++;
                                break;
                            }
                            // The side nearest the worst block comes off.
                            st.cuts++;
                            var (wx, wy) = place[Mathf.Min(worst, place.Count - 1)];
                            var side = Mathf.Min(Mathf.Min(wx - x0, x1 - wx), Mathf.Min(wy - y0, y1 - wy));
                            if (wx - x0 == side) x0++;
                            else if (x1 - wx == side) x1--;
                            else if (wy - y0 == side) y0++;
                            else y1--;
                        }
                    }
                continue;

                // The ring round these blocks: -1 when it is laid (its faces in `made`, new points as
                // -1 - index into `made3`, the faces it replaces in `gone`), else the worst block.
                int Ring(List<(int[] grid, int[] quads)> set, out List<int[]> made, out List<Vector3> made3, out HashSet<int> gone)
                {
                    made = null;
                    made3 = null;
                    gone = null;
                    var inBlock = new HashSet<int>();
                    var blockPoints = new HashSet<int>();
                    var blockOfCorner = new Dictionary<int, int>();
                    for (var i = 0; i < set.Count; i++)
                    {
                        foreach (var f in set[i].quads) inBlock.Add(f);
                        foreach (var v in set[i].grid) blockPoints.Add(v);
                        foreach (var c in new[] { set[i].grid[0], set[i].grid[12], set[i].grid[15], set[i].grid[3] })
                            if (!blockOfCorner.ContainsKey(c)) blockOfCorner[c] = i;
                    }

                    // The rectangle's outline, through its blocks' corners only.
                    var merged = new List<int[]>();
                    foreach (var (g, _) in set) merged.Add(new[] { g[0], g[12], g[15], g[3] });
                    var coarse = new HashSet<long>(EdgeKey.Comparer);
                    foreach (var q in merged) for (var k = 0; k < 4; k++) coarse.Add(EdgeKey.Pack(q[k], q[(k + 1) & 3]));
                    var innerNext = new Dictionary<int, int>();
                    foreach (var q in merged)
                        for (var k = 0; k < 4; k++)
                        {
                            int a = q[k], b = q[(k + 1) & 3];
                            if (coarse.Contains(EdgeKey.Pack(b, a))) continue;
                            if (innerNext.ContainsKey(a)) return blockOfCorner[a];
                            innerNext[a] = b;
                        }
                    var inner = Loops(innerNext);
                    if (inner == null || inner.Count != 1) return 0;

                    // The ring's faces: every face of the region with a point of a block, not in one,
                    // none of them another ring's already.
                    var layer = new HashSet<int>();
                    foreach (var f in members)
                    {
                        if (inBlock.Contains(f)) continue;
                        foreach (var v in faces[f]) if (blockPoints.Contains(v)) { layer.Add(f); break; }
                    }
                    foreach (var f in layer) if (removed[f]) return 0;
                    var region2 = new HashSet<int>(inBlock);
                    region2.UnionWith(layer);
                    var outerNext = new Dictionary<int, int>();
                    foreach (var f in region2)
                    {
                        var face = faces[f];
                        for (var k = 0; k < face.Length; k++)
                        {
                            int a = face[k], b = face[(k + 1) % face.Length];
                            if (he.TryGetValue(EdgeKey.Pack(b, a), out var g) && region2.Contains(g)) continue;
                            if (outerNext.ContainsKey(a)) return 0;
                            outerNext[a] = b;
                        }
                    }
                    var outer = Loops(outerNext);
                    if (outer == null || outer.Count != 1) return 0;

                    // Both loops flat on the region's frame, from their closest pair of points.
                    var flat = new Dictionary<int, int>();
                    var local = new List<Vector2>();
                    int Flat(int v)
                    {
                        if (flat.TryGetValue(v, out var i)) return i;
                        var d = positions[v] - centre;
                        flat[v] = local.Count;
                        local.Add(new Vector2(Vector3.Dot(d, e1), Vector3.Dot(d, e2)));
                        return local.Count - 1;
                    }
                    var faceList = new List<int[]>();
                    foreach (var q in merged) faceList.Add(new[] { Flat(q[0]), Flat(q[1]), Flat(q[2]), Flat(q[3]) });
                    var o = outer[0];
                    var gl = inner[0];
                    int oi = 0, gi = 0;
                    var closest = float.MaxValue;
                    for (var i = 0; i < o.Length; i++)
                        for (var j = 0; j < gl.Length; j++)
                        {
                            var d = (positions[o[i]] - positions[gl[j]]).sqrMagnitude;
                            if (d < closest) { closest = d; oi = i; gi = j; }
                        }
                    var ro = new int[o.Length];
                    for (var k = 0; k < o.Length; k++) ro[k] = Flat(o[(k + oi) % o.Length]);
                    var rg = new int[gl.Length];
                    for (var k = 0; k < gl.Length; k++) rg[k] = Flat(gl[(k + gi) % gl.Length]);
                    var fixedCount = local.Count;
                    // Rungs priced in the ring's own edge lengths, squared.
                    double edgeSum = 0;
                    for (var k = 0; k < ro.Length; k++) edgeSum += (local[ro[(k + 1) % ro.Length]] - local[ro[k]]).magnitude;
                    var edgeLength = (float)(edgeSum / Math.Max(1, ro.Length));
                    var edge = Mathf.Max(edgeLength * edgeLength, 1e-20f);
                    if (!Lay(local, ro, rg, (a, b) => (local[ro[a % ro.Length]] - local[rg[b % rg.Length]]).sqrMagnitude / edge, faceList))
                        return 0;

                    // The ring's new points toward their neighbours while every face stays good.
                    var neighbours = new List<int>[local.Count];
                    foreach (var face in faceList)
                        for (var k = 0; k < face.Length; k++)
                        {
                            int a = face[k], b = face[(k + 1) % face.Length];
                            if (a >= fixedCount) (neighbours[a] ??= new List<int>()).Add(b);
                            if (b >= fixedCount) (neighbours[b] ??= new List<int>()).Add(a);
                        }
                    var current = local.ToArray();
                    var good = Good(current, faceList) ? (Vector2[])current.Clone() : null;
                    for (var sweep = 0; sweep < smoothSweeps; sweep++)
                    {
                        var moved = (Vector2[])current.Clone();
                        for (var v = fixedCount; v < current.Length; v++)
                        {
                            var list = neighbours[v];
                            if (list == null || list.Count == 0) continue;
                            var sum = Vector2.zero;
                            foreach (var u in list) sum += current[u];
                            moved[v] = 0.5f * current[v] + 0.5f * sum / list.Count;
                        }
                        current = moved;
                        if (Good(current, faceList)) good = (Vector2[])current.Clone();
                        else if (good != null) break;
                    }
                    if (good == null)
                    {
                        // The block at the most bad faces.
                        var back = new Dictionary<int, int>();
                        foreach (var kv in flat) back[kv.Value] = kv.Key;
                        var single = new List<int[]>(1) { null };
                        var count = new Dictionary<int, int>();
                        foreach (var face in faceList)
                        {
                            single[0] = face;
                            if (Good(current, single)) continue;
                            foreach (var v in face)
                                if (back.TryGetValue(v, out var mv) && blockOfCorner.TryGetValue(mv, out var bi))
                                    count[bi] = count.TryGetValue(bi, out var c) ? c + 1 : 1;
                        }
                        var worst = 0;
                        var most = -1;
                        foreach (var kv in count) if (kv.Value > most) { most = kv.Value; worst = kv.Key; }
                        return worst;
                    }

                    // In space: the new points from the region's plane onto the source.
                    var meshOf = new int[good.Length];
                    foreach (var kv in flat) meshOf[kv.Value] = kv.Key;
                    made3 = new List<Vector3>();
                    for (var v = fixedCount; v < good.Length; v++)
                    {
                        var p = centre + good[v].x * e1 + good[v].y * e2;
                        if (surface != null) p = surface.NearestPoint(p, out _);
                        meshOf[v] = -1 - made3.Count;
                        made3.Add(p);
                    }
                    made = new List<int[]>(faceList.Count);
                    foreach (var face in faceList)
                    {
                        var g = new int[face.Length];
                        for (var k = 0; k < face.Length; k++) g[k] = meshOf[face[k]];
                        made.Add(g);
                    }
                    gone = region2;
                    return -1;
                }
            }

            // Compacted: the merged quads' inside points dropped.
            var kept = new List<int[]>(m);
            for (var f = 0; f < m; f++) if (!removed[f]) kept.Add(faces[f]);
            var firstAdded = kept.Count;
            kept.AddRange(added);
            var used = new bool[points.Count];
            foreach (var face in kept) foreach (var v in face) used[v] = true;
            var index = new int[points.Count];
            var outPoints = new List<Vector3>(points.Count);
            var originList = new List<int>(points.Count);
            for (var v = 0; v < points.Count; v++)
            {
                if (!used[v]) { index[v] = -1; continue; }
                index[v] = outPoints.Count;
                outPoints.Add(points[v]);
                originList.Add(v < positions.Length ? v : -1);
            }
            faces.Clear();
            foreach (var face in kept)
            {
                var g = new int[face.Length];
                for (var k = 0; k < face.Length; k++) g[k] = index[face[k]];
                faces.Add(g);
            }
            pinned = new bool[outPoints.Count];
            for (var f = firstAdded; f < faces.Count; f++)
                if (pinRing || addedBlock[f - firstAdded])
                    foreach (var v in faces[f]) pinned[v] = true;
            origin = originList.ToArray();
            st.facesAfter = faces.Count;
            st.ms = watch.ElapsedMilliseconds;
            stats = st;
            return outPoints.ToArray();
        }

        // Faces grown from the biggest down, up to the creases, each run kept when every face is
        // within regionDegrees of its normal and it is big enough.
        static List<List<int>> Regions(Vector3[] positions, List<int[]> faces, bool[] hold, float cell,
            Vector3[] vectorArea, float[] area, Dictionary<long, int> he, out int[] regionOf)
        {
            var m = faces.Count;
            var of = new int[m];
            for (var f = 0; f < m; f++) of[f] = -1;
            var order = new int[m];
            for (var f = 0; f < m; f++) order[f] = f;
            Array.Sort(order, (a, b) => area[b].CompareTo(area[a]));
            var cosEdge = Mathf.Cos(edgeDegrees * Mathf.Deg2Rad);
            var cosHeld = Mathf.Cos(heldDegrees * Mathf.Deg2Rad);
            var cosRegion = Mathf.Cos(regionDegrees * Mathf.Deg2Rad);
            var regions = new List<List<int>>();
            var queue = new Queue<int>();
            foreach (var seed in order)
            {
                if (of[seed] != -1 || area[seed] <= 0f) continue;
                var id = regions.Count;
                var members = new List<int> { seed };
                of[seed] = id;
                var sumN = vectorArea[seed];
                var sumA = area[seed];
                queue.Clear();
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    var face = faces[f];
                    for (var k = 0; k < face.Length; k++)
                    {
                        int a = face[k], b = face[(k + 1) % face.Length];
                        if (!he.TryGetValue(EdgeKey.Pack(b, a), out var g) || of[g] != -1 || area[g] <= 0f) continue;
                        var turn = Vector3.Dot(vectorArea[g] / area[g], vectorArea[f] / area[f]);
                        if (turn < cosEdge) continue;
                        if (hold != null && hold[a] && hold[b] && turn < cosHeld) continue;
                        if (Vector3.Dot(vectorArea[g] / area[g], sumN.normalized) < cosRegion) continue;
                        of[g] = id;
                        members.Add(g);
                        sumN += vectorArea[g];
                        sumA += area[g];
                        queue.Enqueue(g);
                    }
                }
                var nrm = sumN.normalized;
                var within = true;
                foreach (var f in members) if (Vector3.Dot(vectorArea[f] / area[f], nrm) < cosRegion) { within = false; break; }
                if (!within || sumA < minAreaCells * cell * cell || members.Count < 9)
                {
                    // Not a region; its faces stay out of every other one too.
                    foreach (var f in members) of[f] = -3;
                    continue;
                }
                regions.Add(members);
            }
            regionOf = of;
            return regions;
        }

        static long Cell(int x, int y) => ((long)x << 32) ^ (uint)y;

        // The biggest rectangles of a grid whose blocks are all there and meet corner to corner,
        // each kept a block clear of those before it.
        static List<(int x0, int y0, int x1, int y1)> Rectangles(List<(int[] grid, int[] quads, int bx, int by)> blocks, int minBlocks)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var b in blocks) { minX = Math.Min(minX, b.bx); minY = Math.Min(minY, b.by); maxX = Math.Max(maxX, b.bx); maxY = Math.Max(maxY, b.by); }
            int w = maxX - minX + 1, h = maxY - minY + 1;
            var at = new int[w, h];
            for (var x = 0; x < w; x++) for (var y = 0; y < h; y++) at[x, y] = -1;
            for (var i = 0; i < blocks.Count; i++) at[blocks[i].bx - minX, blocks[i].by - minY] = i;
            var free = new bool[w, h];
            for (var x = 0; x < w; x++) for (var y = 0; y < h; y++) free[x, y] = at[x, y] >= 0;
            // Seams that do not meet: a block and the one right of it, or above it.
            var cutRight = new bool[w, h];
            var cutUp = new bool[w, h];
            for (var x = 0; x < w; x++)
                for (var y = 0; y < h; y++)
                {
                    if (at[x, y] < 0) continue;
                    var g = blocks[at[x, y]].grid;
                    if (x + 1 < w && at[x + 1, y] >= 0)
                    {
                        var r = blocks[at[x + 1, y]].grid;
                        cutRight[x, y] = g[12] != r[0] || g[15] != r[3];
                    }
                    if (y + 1 < h && at[x, y + 1] >= 0)
                    {
                        var u = blocks[at[x, y + 1]].grid;
                        cutUp[x, y] = g[3] != u[0] || g[15] != u[12];
                    }
                }
            var found = new List<(int, int, int, int)>();
            while (true)
            {
                var bestArea = 0;
                (int, int, int, int) bestRect = default;
                for (var x0 = 0; x0 < w; x0++)
                    for (var y0 = 0; y0 < h; y0++)
                    {
                        if (!free[x0, y0]) continue;
                        // Grown right column by column, each as far up as every block is there and
                        // every seam meets, never above the columns before it.
                        var maxY1 = h - 1;
                        for (var x1 = x0; x1 < w; x1++)
                        {
                            if (!free[x1, y0] || x1 > x0 && cutRight[x1 - 1, y0]) break;
                            var y1 = y0;
                            while (y1 + 1 <= maxY1)
                            {
                                var ok = free[x1, y1 + 1] && !cutUp[x1, y1] && (x1 == x0 || !cutRight[x1 - 1, y1 + 1]);
                                if (!ok) break;
                                y1++;
                            }
                            maxY1 = y1;
                            var area = (x1 - x0 + 1) * (maxY1 - y0 + 1);
                            if (area > bestArea) { bestArea = area; bestRect = (x0, y0, x1, maxY1); }
                        }
                    }
                if (bestArea < Math.Max(1, minBlocks)) break;
                var (ax, ay, bx, by) = bestRect;
                found.Add((ax + minX, ay + minY, bx + minX, by + minY));
                for (var x = Math.Max(0, ax - 1); x <= Math.Min(w - 1, bx + 1); x++)
                    for (var y = Math.Max(0, ay - 1); y <= Math.Min(h - 1, by + 1); y++) free[x, y] = false;
            }
            return found;
        }

        // Directed edges a -> next[a] as closed loops, or null where they do not close.
        static List<int[]> Loops(Dictionary<int, int> next)
        {
            var loops = new List<int[]>();
            var done = new HashSet<int>();
            foreach (var kv in next)
            {
                if (done.Contains(kv.Key)) continue;
                var loop = new List<int>();
                for (var v = kv.Key; ;)
                {
                    loop.Add(v);
                    done.Add(v);
                    if (!next.TryGetValue(v, out v)) return null;
                    if (v == kv.Key) break;
                    if (done.Contains(v) || loop.Count > next.Count) return null;
                }
                loops.Add(loop.ToArray());
            }
            return loops;
        }

        // The faces between loop o (the region side's, as its faces wind) and loop g (the
        // rectangle's outline, as its blocks wind), from rung (o[0], g[0]) round to it again: the
        // cheapest path of steps of (o's edges, g's edges) -- (1, 1) a quad, (k, 1) or (1, k) for odd
        // k quads about k - 1 new points, (2, 0) or (0, 2) a quad round one point of the other loop,
        // (2, 1) or (1, 2) a quad and a triangle -- each rung priced by `rung`.
        static readonly (int di, int dj, float cost)[] steps =
            { (1, 1, 0f), (3, 1, 1f), (1, 3, 1f), (5, 1, 6f), (1, 5, 6f), (7, 1, 12f), (1, 7, 12f), (2, 0, 1f), (0, 2, 1f), (2, 1, 25f), (1, 2, 25f) };

        static bool Lay(List<Vector2> local, int[] o, int[] g, Func<int, int, float> rung, List<int[]> made)
        {
            int count = o.Length, ni = g.Length;
            if ((long)(count + 1) * (ni + 1) > 4000000) return false;
            var best = new float[count + 1, ni + 1];
            var from = new byte[count + 1, ni + 1];
            for (var i = 0; i <= count; i++) for (var j = 0; j <= ni; j++) best[i, j] = float.PositiveInfinity;
            best[0, 0] = 0f;
            for (var i = 0; i <= count; i++)
                for (var j = 0; j <= ni; j++)
                {
                    if (float.IsPositiveInfinity(best[i, j])) continue;
                    for (var s = 0; s < steps.Length; s++)
                    {
                        int ti = i + steps[s].di, tj = j + steps[s].dj;
                        if (ti > count || tj > ni) continue;
                        var c = best[i, j] + steps[s].cost + rung(ti, tj);
                        if (c < best[ti, tj]) { best[ti, tj] = c; from[ti, tj] = (byte)s; }
                    }
                }
            if (float.IsPositiveInfinity(best[count, ni])) return false;
            var path = new List<int>();
            for (int i = count, j = ni; i > 0 || j > 0;)
            {
                var s = from[i, j];
                path.Add(s);
                i -= steps[s].di;
                j -= steps[s].dj;
            }
            path.Reverse();
            int O(int i) => o[i % count];
            int G(int j) => g[j % ni];
            int Mid(int a, int b, int c, float t)
            {
                local.Add(0.5f * (local[a] + Vector2.Lerp(local[b], local[c], t)));
                return local.Count - 1;
            }
            int oi = 0, gj = 0;
            foreach (var s in path)
            {
                var (di, dj, _) = steps[s];
                if (di == 1 && dj == 1)
                    made.Add(new[] { O(oi), O(oi + 1), G(gj + 1), G(gj) });
                else if (dj == 0)
                    made.Add(new[] { O(oi), O(oi + 1), O(oi + 2), G(gj) });
                else if (di == 0)
                    made.Add(new[] { O(oi), G(gj + 2), G(gj + 1), G(gj) });
                else if (dj == 1 && (di & 1) == 1)
                {
                    // di of the outer edges onto one inner: a quad on each, onto a chain of points
                    // halfway in, and that chain closed onto the inner edge in quads.
                    var chain = new int[di + 1];
                    chain[0] = G(gj);
                    chain[di] = G(gj + 1);
                    for (var k = 1; k < di; k++) chain[k] = Mid(O(oi + k), G(gj), G(gj + 1), (float)k / di);
                    for (var k = 0; k < di; k++) made.Add(new[] { O(oi + k), O(oi + k + 1), chain[k + 1], chain[k] });
                    for (var k = 1; k + 1 < di; k += 2) made.Add(new[] { chain[0], chain[k], chain[k + 1], chain[k + 2] });
                }
                else if (di == 1 && (dj & 1) == 1)
                {
                    // One outer edge onto dj inner, the same the other way.
                    var chain = new int[dj + 1];
                    chain[0] = O(oi);
                    chain[dj] = O(oi + 1);
                    for (var k = 1; k < dj; k++) chain[k] = Mid(G(gj + k), O(oi), O(oi + 1), (float)k / dj);
                    for (var k = 0; k < dj; k++) made.Add(new[] { chain[k], chain[k + 1], G(gj + k + 1), G(gj + k) });
                    for (var k = dj; k > 1; k -= 2) made.Add(new[] { chain[0], chain[k], chain[k - 1], chain[k - 2] });
                }
                else if (di == 2)
                {
                    made.Add(new[] { O(oi), O(oi + 1), G(gj) });
                    made.Add(new[] { O(oi + 1), O(oi + 2), G(gj + 1), G(gj) });
                }
                else
                {
                    made.Add(new[] { O(oi), G(gj + 1), G(gj) });
                    made.Add(new[] { O(oi), O(oi + 1), G(gj + 2), G(gj + 1) });
                }
                oi += di;
                gj += dj;
            }
            return true;
        }

        // Every face convex, counterclockwise, with no corner under minCornerDegrees.
        static bool Good(Vector2[] p, List<int[]> made)
        {
            var cosMin = Mathf.Cos(minCornerDegrees * Mathf.Deg2Rad);
            foreach (var face in made)
                for (var k = 0; k < face.Length; k++)
                {
                    var a = p[face[(k + face.Length - 1) % face.Length]];
                    var b = p[face[k]];
                    var c = p[face[(k + 1) % face.Length]];
                    var ba = a - b;
                    var bc = c - b;
                    if (bc.x * ba.y - bc.y * ba.x <= 0f) return false;
                    var len = ba.magnitude * bc.magnitude;
                    if (len <= 0f || Vector2.Dot(ba, bc) / len > cosMin) return false;
                }
            return true;
        }
    }
}
