// A curve network traced along the surface's ridges and valleys, for the lattice to follow.
//
// WHY. Exoside's QuadRemesher (the "FreeRemesher" the user supplied) does not solve its field over the
// raw surface. Its strings name the stages: MXAutoRetopoCurv, CutFaces, AfterCutByCurves. It finds
// curves first -- hard edges (AutoDetectHardEdges is on by default) and the shape's own ridge lines --
// cuts the surface into patches along them, and only then solves, with every cut an edge loop of the
// result. That is why a brim or a rail comes out as a crisp loop in its output where a smooth field
// wanders across it.
//
// This pipeline already has the machinery to honour a cut: a FeatureGraph crease is a hard
// orientation constraint, an iso-line of the integer map (IntegerGridMap.ApplySharpEdgeConstraints),
// and a line the output vertices are pinned to (SharpSolve). What it lacked on these inputs was the
// curves. A dihedral test at the input's edge length cannot find them on a sculpt: a bevel narrower
// than a quad is spread over several input edges, none of them sharp, and at a low angle the test
// marks noise everywhere (20 degrees marks 16 % of the helmet head, which is why both scan profiles
// switch it off). So the head and the body reached the solver with no curves at all.
//
// HOW. Canny's recipe, on a surface. The shape operator is averaged over about half a cell
// (CurvatureDirections.ShapeTensors) -- the scale at which a quad can express a turn, so pores and
// stitching fall below it and a bevel does not. A vertex's ridge strength is its largest principal
// curvature times the cell, i.e. roughly how far the normal turns across one quad, in radians. Curves
// start at the strongest, clearly one-directional vertices (hysteresis: a high threshold to start, a
// low one to continue) and are walked edge by edge along the ridge direction, always stepping to the
// strongest neighbour ahead, which keeps them on the crest. A finished curve claims a band half a
// cell wide either side, so the other shoulder of the same bevel is not traced as a second, parallel
// curve -- two creases half a cell apart would be two iso-lines where the lattice has room for one.
// A curve that runs into another's band may carry on to meet it (a T-junction) but not run alongside
// it. Curves shorter than a few cells are dropped: SharpSolve cannot resample them either, and on a
// sculpt they are nearly always a lump rather than an edge.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public static class CurveNetwork
    {
        public sealed class Stats
        {
            public int seeds;
            public int curves;
            public int closed;
            public int joins;
            public int rejectedShort;
            public int corners;
            public int thinVertices;
            public int curveVertices;
            public float lengthCells;
            public int tensorSweeps;
            public long ms;

            public override string ToString() =>
                $"curve network: {curves} curves ({closed} closed, {joins} joins), " +
                $"{curveVertices} vertices, {lengthCells:F0} cells long, {corners} corner vertices, " +
                $"{thinVertices} thin; " +
                $"{seeds} seeds, " +
                $"{rejectedShort} too short; {tensorSweeps} tensor sweeps ({ms} ms)";
        }

        /// <summary>Shape-operator averaging radius, in cells.</summary>
        public static float radiusCells = 0.5f;

        /// <summary>Normal turn across one cell, in radians, at which a curve may start.</summary>
        /// <remarks>
        /// The scale is not the geometric turn: averaging spreads a crease, so a clean 90-degree
        /// cube edge measures about 0.67 (unit test, cell = 3 input edges). 1.5 therefore starts
        /// curves only at folds of roughly twice that -- the lip of a plate or a shell, where front
        /// and back meet -- and they continue along anything above <see cref="continueTurn"/>,
        /// which a right angle passes.
        /// Measured against 1.0 (minimum length 6 / 4 cells) through the editor's path: body
        /// 3.63 % irregular / Hausdorff 0.0115 against 4.80 % / 0.0182; on the head within noise.
        /// </remarks>
        public static float seedTurn = 1.5f;

        /// <summary>Normal turn across one cell, in radians, below which a curve stops.</summary>
        public static float continueTurn = 0.5f;

        /// <summary>
        /// A seed's smaller principal curvature must be at most this fraction of its larger one: a
        /// ridge bends one way. A bump or a corner bends both ways and has no line to follow.
        /// </summary>
        public static float seedAnisotropy = 0.5f;

        /// <summary>Curves shorter than this many cells are dropped.</summary>
        public static float minCells = 6f;

        /// <summary>Half-width, in cells, of the band a finished curve claims on either side.</summary>
        public static float bandCells = 0.5f;

        /// <summary>Largest turn, in degrees, a curve may make in one step.</summary>
        public static float maxStepDegrees = 45f;

        /// <summary>
        /// A curve turning more than this, in degrees, over half a cell either side of a vertex has
        /// a corner there: no direction is prescribed, so the cross field can put its singularity
        /// on the corner -- where a patch corner belongs -- instead of being dragged round the bend.
        /// </summary>
        public static float cornerDegrees = 50f;

        /// <summary>
        /// Zero: the curves' directions are hard constraints on the cross field, like any crease.
        /// Above zero: they are guides of this strength instead (see OrientationField.Solve), a
        /// pull the smoothing can argue with.
        /// </summary>
        public static float guideWeight = 0.5f;

        /// <summary>
        /// A ridge with a valley within this many cells of it (or the other way round) is THIN --
        /// a rail, a strap, a step lower than a quad -- and carries no curve. Zero disables.
        /// </summary>
        /// <remarks>
        /// A cut is an iso-line of the lattice, and a lattice has room for one iso-line per cell.
        /// Where a crest and the groove at its foot are closer than that, cutting along both asks
        /// for two lines where there is room for one, and the solve folds the cell between them:
        /// that is the crumpled brim and the hole on the helmet's side rail. Such a feature cannot
        /// be expressed at this density however it is cut, so it is left to the smooth field.
        /// </remarks>
        public static float thinCells = 0.75f;

        /// <summary>
        /// How strong the bending on the other side must be to make a feature thin, in the units of
        /// <see cref="seedTurn"/>. Measured: at <see cref="continueTurn"/> every shallow groove
        /// counts, and on the helmet head 33,295 vertices came out thin, leaving 10 curves.
        /// </summary>
        public static float thinTurn = 1.5f;

        /// <summary>
        /// Traces the network on <paramref name="topology"/>. Returns its edges as vertex pairs;
        /// <paramref name="tangents"/> holds, for every vertex a curve passes through, the curve's
        /// direction averaged over about half a cell (zero elsewhere) -- a one-ring chord on a
        /// polyline that zig-zags between mesh vertices is noise.
        /// </summary>
        public static List<(int a, int b)> Trace(RemeshTopology topology, float cell,
            out Vector3[] tangents, out Stats stats) =>
            Trace(topology, cell, out tangents, out _, out stats);

        /// <param name="corners">Vertices where a curve turns more than <see cref="cornerDegrees"/>.</param>
        public static List<(int a, int b)> Trace(RemeshTopology topology, float cell,
            out Vector3[] tangents, out bool[] corners, out Stats stats)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var count = topology.vertexCount;
            var positions = topology.positions;
            var normals = topology.normals;
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;
            stats = new Stats();
            tangents = new Vector3[count];
            corners = new bool[count];
            var cosCorner = Mathf.Cos(cornerDegrees * Mathf.Deg2Rad);
            var edges = new List<(int a, int b)>();
            if (count == 0 || !(cell > 0f)) return edges;

            var tensor = CurvatureDirections.ShapeTensors(topology, radiusCells * cell,
                out stats.tensorSweeps);

            // --- ridge strength and direction per vertex ---
            var strength = new float[count];
            var sign = new sbyte[count];
            var along = new Vector3[count];
            var oneWay = new bool[count];
            for (var i = 0; i < count; i++)
            {
                var n = normals[i];
                var u = OrientationField.ArbitraryTangent(n);
                var v = Vector3.Cross(n, u);
                var o = i * 6;

                Vector3 Apply(Vector3 x) => new Vector3(
                    tensor[o] * x.x + tensor[o + 1] * x.y + tensor[o + 2] * x.z,
                    tensor[o + 1] * x.x + tensor[o + 3] * x.y + tensor[o + 4] * x.z,
                    tensor[o + 2] * x.x + tensor[o + 4] * x.y + tensor[o + 5] * x.z);

                var m11 = Vector3.Dot(u, Apply(u));
                var m12 = Vector3.Dot(u, Apply(v));
                var m22 = Vector3.Dot(v, Apply(v));
                var mean = 0.5f * (m11 + m22);
                var half = 0.5f * (m11 - m22);
                var radius = Mathf.Sqrt(half * half + m12 * m12);
                var plus = mean + radius;
                var minus = mean - radius;

                var angle = 0.5f * Mathf.Atan2(2f * m12, m11 - m22);
                var first = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);   // eigenvector of `plus`
                var second = Vector3.Cross(n, first);

                float k1, k2;
                if (Mathf.Abs(plus) >= Mathf.Abs(minus))
                {
                    k1 = plus; k2 = minus;
                    along[i] = second;     // the ridge runs across the direction of strongest bending
                }
                else
                {
                    k1 = minus; k2 = plus;
                    along[i] = first;
                }

                strength[i] = Mathf.Abs(k1) * cell;
                sign[i] = (sbyte)(k1 >= 0f ? 1 : -1);
                oneWay[i] = Mathf.Abs(k2) <= seedAnisotropy * Mathf.Abs(k1);
            }

            // --- thin features: strong bending of both signs within thinCells ---
            var thin = new bool[count];
            if (thinCells > 0f)
            {
                var reachThin = thinCells * cell;
                var near = new bool[2][];
                var visited = new int[count];
                var bfs = new Queue<(int vertex, int source)>();
                for (var side = 0; side < 2; side++)
                {
                    near[side] = new bool[count];
                    var wanted = (sbyte)(side == 0 ? 1 : -1);
                    bfs.Clear();
                    var mark = side + 1;
                    for (var i = 0; i < count; i++)
                    {
                        if (sign[i] != wanted || strength[i] < thinTurn) continue;
                        near[side][i] = true;
                        visited[i] = mark;
                        bfs.Enqueue((i, i));
                    }
                    while (bfs.Count > 0)
                    {
                        var (v, source) = bfs.Dequeue();
                        for (var a = start[v]; a < start[v + 1]; a++)
                        {
                            var j = adjacency[a];
                            if (visited[j] == mark) continue;
                            if ((positions[j] - positions[source]).sqrMagnitude > reachThin * reachThin) continue;
                            visited[j] = mark;
                            near[side][j] = true;
                            bfs.Enqueue((j, source));
                        }
                    }
                }
                for (var i = 0; i < count; i++)
                {
                    if (strength[i] < continueTurn) continue;
                    var opposite = sign[i] > 0 ? 1 : 0;
                    if (!near[opposite][i]) continue;
                    thin[i] = true;
                    stats.thinVertices++;
                }
            }

            // --- seeds, strongest first ---
            var seeds = new List<int>();
            for (var i = 0; i < count; i++)
                if (strength[i] >= seedTurn && oneWay[i] && !thin[i]) seeds.Add(i);
            seeds.Sort((a, b) => strength[b].CompareTo(strength[a]));
            stats.seeds = seeds.Count;

            var curveOf = new int[count];
            for (var i = 0; i < count; i++) curveOf[i] = -1;
            var banned = new bool[count];      // inside a finished curve's band
            var tried = new bool[count];       // on a curve that was too short
            var stampOf = new int[count];
            var stamp = 0;

            var cosStep = Mathf.Cos(maxStepDegrees * Mathf.Deg2Rad);
            var cosAlong = Mathf.Cos(45f * Mathf.Deg2Rad);
            var edge = Mathf.Max(topology.averageEdgeLength, 1e-12f);
            var bandSteps = Mathf.CeilToInt(2f * bandCells * cell / edge) + 2;
            var band = bandCells * cell;

            // Walks from `origin` along `direction` and returns the vertices stepped onto, in order.
            List<int> Walk(int origin, Vector3 direction, bool mayClose, out bool closed, out bool joined)
            {
                closed = false;
                joined = false;
                var path = new List<int>();
                var current = origin;
                var heading = direction;
                var ridgeSign = sign[origin];
                var bandEntry = -1;

                for (var step = 0; step < count; step++)
                {
                    var n = normals[current];
                    var best = -1;
                    var bestScore = float.NegativeInfinity;
                    for (var a = start[current]; a < start[current + 1]; a++)
                    {
                        var j = adjacency[a];
                        var d = positions[j] - positions[current];
                        d -= n * Vector3.Dot(n, d);
                        var length = d.magnitude;
                        if (length <= 1e-12f) continue;
                        var c = Vector3.Dot(d / length, heading);
                        if (c < cosStep) continue;

                        float score;
                        if (j == origin)
                        {
                            if (!mayClose || path.Count < 3) continue;
                            score = 10f + c;
                        }
                        else if (stampOf[j] == stamp) continue;
                        else if (curveOf[j] >= 0) score = 10f + c;
                        else
                        {
                            if (sign[j] != ridgeSign || strength[j] < continueTurn || thin[j]) continue;
                            if (Mathf.Abs(Vector3.Dot(along[j], heading)) < cosAlong) continue;
                            score = c * strength[j];
                        }

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = j;
                        }
                    }

                    if (best < 0) break;
                    path.Add(best);
                    if (best == origin) { closed = true; break; }
                    if (curveOf[best] >= 0) { joined = true; break; }

                    // Inside another curve's band: allowed only on the way to meeting it.
                    if (banned[best])
                    {
                        if (bandEntry < 0) bandEntry = path.Count - 1;
                        if (path.Count - bandEntry > bandSteps)
                        {
                            path.RemoveRange(bandEntry, path.Count - bandEntry);
                            break;
                        }
                    }
                    else bandEntry = -1;

                    stampOf[best] = stamp;
                    var stepDirection = positions[best] - positions[current];
                    var ridge = along[best];
                    if (Vector3.Dot(ridge, heading) < 0f) ridge = -ridge;
                    var nb = normals[best];
                    var next = ridge + stepDirection.normalized;
                    next -= nb * Vector3.Dot(nb, next);
                    if (next.sqrMagnitude > 1e-20f) heading = next.normalized;
                    current = best;
                }

                // A walk that ended inside a band without meeting the curve leaves that stretch
                // alongside it, which is exactly what the band exists to stop.
                if (!joined && !closed && bandEntry >= 0)
                    path.RemoveRange(bandEntry, path.Count - bandEntry);
                return path;
            }

            var queue = new Queue<(int vertex, int source)>();
            var chain = new List<int>();
            foreach (var seed in seeds)
            {
                if (curveOf[seed] >= 0 || banned[seed] || tried[seed]) continue;

                stamp++;
                stampOf[seed] = stamp;
                var forward = Walk(seed, along[seed], true, out var closed, out var joinedForward);
                var joinedBackward = false;
                var backward = closed
                    ? new List<int>()
                    : Walk(seed, -along[seed], false, out _, out joinedBackward);

                chain.Clear();
                for (var k = backward.Count - 1; k >= 0; k--) chain.Add(backward[k]);
                chain.Add(seed);
                chain.AddRange(forward);

                var length = 0f;
                for (var k = 1; k < chain.Count; k++)
                    length += Vector3.Distance(positions[chain[k - 1]], positions[chain[k]]);

                if (length < minCells * cell)
                {
                    foreach (var v in chain) tried[v] = true;
                    stats.rejectedShort++;
                    continue;
                }

                var id = stats.curves++;
                if (closed) stats.closed++;
                if (joinedForward) stats.joins++;
                if (joinedBackward) stats.joins++;
                stats.lengthCells += length / cell;

                for (var k = 0; k < chain.Count; k++)
                {
                    if (curveOf[chain[k]] < 0)
                    {
                        curveOf[chain[k]] = id;
                        stats.curveVertices++;
                    }
                    if (k > 0) edges.Add((chain[k - 1], chain[k]));
                }

                // Tangent over about half a cell each way along the chain.
                var reach = 0.25f * cell;
                for (var k = 0; k < chain.Count; k++)
                {
                    var lo = k;
                    var travelled = 0f;
                    while (lo > 0 && travelled < reach)
                    {
                        travelled += Vector3.Distance(positions[chain[lo]], positions[chain[lo - 1]]);
                        lo--;
                    }
                    var hi = k;
                    travelled = 0f;
                    while (hi < chain.Count - 1 && travelled < reach)
                    {
                        travelled += Vector3.Distance(positions[chain[hi]], positions[chain[hi + 1]]);
                        hi++;
                    }
                    var v = chain[k];
                    var back = positions[chain[k]] - positions[chain[lo]];
                    var ahead = positions[chain[hi]] - positions[chain[k]];
                    if (lo < k && hi > k && Vector3.Dot(back.normalized, ahead.normalized) < cosCorner)
                    {
                        if (!corners[v]) stats.corners++;
                        corners[v] = true;
                        tangents[v] = Vector3.zero;
                        continue;
                    }
                    if (corners[v]) continue;
                    var t = positions[chain[hi]] - positions[chain[lo]];
                    t -= normals[v] * Vector3.Dot(normals[v], t);
                    if (t.sqrMagnitude > 1e-20f && tangents[v] == Vector3.zero) tangents[v] = t.normalized;
                }

                // The band: every vertex within `band` of some chain vertex, reached over edges.
                stamp++;
                queue.Clear();
                foreach (var v in chain)
                {
                    stampOf[v] = stamp;
                    queue.Enqueue((v, v));
                }
                while (queue.Count > 0)
                {
                    var (v, source) = queue.Dequeue();
                    for (var a = start[v]; a < start[v + 1]; a++)
                    {
                        var j = adjacency[a];
                        if (stampOf[j] == stamp) continue;
                        if ((positions[j] - positions[source]).sqrMagnitude > band * band) continue;
                        stampOf[j] = stamp;
                        banned[j] = true;
                        queue.Enqueue((j, source));
                    }
                }
            }

            stats.ms = watch.ElapsedMilliseconds;
            return edges;
        }
    }
}
