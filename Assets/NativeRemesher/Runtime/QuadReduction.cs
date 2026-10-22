// Reduce a mesh that already is clean quads by taking out whole quad strips, instead of remeshing it.
//
// WHY. A modelled hard-surface part -- every one of the Porsche's 43 is 100 % quads -- carries its
// artist's decisions in its loops: the rolled lip along a wheel arch is three loops, a panel's edge
// is a loop, a support loop sits beside every crease. The field remesher sees triangles, decimates
// them and lays a new grid by curvature, and a grid cell wider than the lip cuts across it: the
// arch's lip comes out in pieces, the flange crumpled. AI-generated meshes are triangle soups with
// no loops to keep, which is why the remesher does well on them and badly on these.
//
// HOW. A quad strip (a "chord": the quads met walking across opposite sides) is removed by merging
// each of the edges it crosses -- its rungs -- into a point. The mesh stays all quads, every other
// loop keeps its course, and the input's poles stay where the artist put them. Strips go cheapest
// first; a strip's cost is
//
//   (longest rung / cell)^2  +      (largest distance of the input it stood for, from the new
//                                    faces / (errorCells * cell))^2
//
// so narrow strips (support loops, dense rows) go first and a strip whose removal moves the surface
// -- a lip, a crease's own loop -- stays until the budget forces it. Merged points sit on the rung,
// where the two ends' quadrics (face planes, and border planes so open edges stay put) are least.
//
// A batch removes the cheapest strips that are two rings apart, so each was costed on the mesh it is
// removed from; the mesh is rebuilt and costed again between batches. A strip is refused when it
// crosses itself, would fold a face (normal turned past minNormalCos), merge a face onto itself,
// pinch a border or give an edge three faces.
//
// When the input is not quads (95 % of its triangles pairing into quads), or the strips cannot get
// within maxOvershoot of the target, this reports failure and the remesher runs as before.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NativeRemesher
{
    public static class QuadReduction
    {
        public sealed class Stats
        {
            public int inputTriangles, inputQuads, inputLoneTriangles, weldedVertices, target, outputFaces;
            public int batches, chordsRemoved, rejectedSelf, rejectedTopology, rejectedFold;
            public int lastChords, lastSelf, lastTopology, lastFold;
            public int[] foldKinds = new int[5];
            public float quadShare, squareShare, cell, largestError, irregular;
            public long ms;
            public string pairing, reason;
            public override string ToString() =>
                $"quadReduction pairing={pairing} share={quadShare:P1} square={squareShare:P1} quads={inputQuads} lone={inputLoneTriangles} " +
                $"target={target} out={outputFaces} batches={batches} chords={chordsRemoved} rejSelf={rejectedSelf} " +
                $"rejTopo={rejectedTopology} rejFold={rejectedFold} last[{lastChords} self={lastSelf} topo={lastTopology} fold={lastFold}] foldKinds={string.Join("/", foldKinds)} cell={cell:G4} maxErr={largestError:G4} irregular={irregular:P1} ms={ms} {reason}";
        }

        /// <summary>Diagnostics: write each reduction's counters to the console.</summary>
        public static bool logStats;

        /// <summary>The last reduction's counters on this thread, tried or not (diagnostics).</summary>
        [ThreadStatic] public static Stats last;

        /// <summary>The last ReduceParts' time by stage, for logs.</summary>
        [ThreadStatic] public static string lastTimings;

        /// <summary>Diagnostics: given the mesh where the strips ran out.</summary>
        public static Action<Vector3[], List<int[]>> debugStuck;

        /// <summary>Share of the input's triangles that must pair into quads for it to count as a quad mesh.</summary>
        public static float minQuadShare = 0.95f;

        /// <summary>Distance off the input, in cells, that costs as much as removing a strip a cell wide.</summary>
        public static float errorCells = 0.1f;

        /// <summary>Give up (and let the remesher run) when the strips stop further than this over the target.</summary>
        public static float maxOvershoot = 1.5f;

        /// <summary>Give up when a strip had to move the surface further than this many cells...</summary>
        public static float maxErrorCells = 2f;

        /// <summary>
        /// ...or more than this share of the vertices end up irregular, on an output of
        /// irregularMinFaces or more (a small box is all corners). The Porsche's wheels, forced from
        /// 277,000 quads of tread to 500, come out 24-31 %; its panels 1-21 %.
        /// </summary>
        public static float maxIrregular = 0.22f;
        public static int irregularMinFaces = 100;

        /// <summary>...or, for a small part, further over than this many quads.</summary>
        public static int maxOvershootQuads = 100;

        /// <summary>A face whose normal would turn further than this (cosine) refuses the strip.</summary>
        public static float minNormalCos = 0.2f;

        /// <summary>A batch takes strips up to this many times the cheapest one's cost (plus batchCostFloor).</summary>
        public static float batchCostSlack = 1.5f;
        public static float batchCostFloor = 0.05f;

        /// <summary>
        /// A quad's corners may not be pressed below this sine (about 9 degrees) unless the input's
        /// quad was already that thin, within needleWorsening.
        /// </summary>
        public static float minCornerSine = 0.15f;
        public static float needleWorsening = 0.7f;

        /// <summary>A strip may cross itself this many times (each crossing quad becomes one point).</summary>
        public static int maxCrossings = 2;

        /// <summary>When the quadric points fold a face, try the rungs' middles and ends.</summary>
        public static bool placementFallback = true;

        /// <summary>
        /// The input's triangles paired back into the quads they were. Unity's importer and OBJ
        /// loaders emit a quad as two consecutive triangles fanned from one corner; that order is
        /// tried first, then neighbours paired best-shaped quad first.
        /// </summary>
        public static bool Recover(Vector3[] positions, int[] triangles, out Vector3[] welded, out int[] faces,
            out Stats stats)
        {
            stats = new Stats { inputTriangles = triangles.Length / 3 };
            var map = new Dictionary<Vector3, int>();
            var weld = new int[positions.Length];
            var wp = new List<Vector3>();
            for (var i = 0; i < positions.Length; i++)
            {
                if (!map.TryGetValue(positions[i], out var w)) { w = wp.Count; map[positions[i]] = w; wp.Add(positions[i]); }
                weld[i] = w;
            }
            welded = wp.ToArray();
            stats.weldedVertices = welded.Length;
            var tc = triangles.Length / 3;
            var t = new int[triangles.Length];
            for (var i = 0; i < t.Length; i++) t[i] = weld[triangles[i]];

            var ordered = PairInOrder(t, tc, out var orderedPairs);
            if (2f * orderedPairs / Math.Max(1, tc) >= minQuadShare)
            {
                faces = ordered;
                stats.pairing = "order";
                stats.inputQuads = orderedPairs;
            }
            else
            {
                faces = PairByAngle(welded, t, tc, out var anglePairs);
                stats.pairing = "angle";
                stats.inputQuads = anglePairs;
            }
            stats.inputLoneTriangles = faces.Length / 4 - stats.inputQuads;
            stats.quadShare = 2f * stats.inputQuads / Math.Max(1, tc);
            var square = 0;
            for (var f = 0; f + 3 < faces.Length; f += 4)
                if (faces[f + 3] >= 0 && QuadScore(welded[faces[f]], welded[faces[f + 1]], welded[faces[f + 2]], welded[faces[f + 3]]) >= squareScore)
                    square++;
            stats.squareShare = stats.inputQuads > 0 ? square / (float)stats.inputQuads : 0f;
            // A sculpt's even triangles pair up too (91-97 % on the project's), but into rhombi:
            // 11-19 % of its pairs are square against 65-92 % of a modelled part's.
            return stats.quadShare >= minQuadShare && stats.squareShare >= minSquareShare;
        }

        // Consecutive triangles that share an edge: how a quad is written out as two.
        static int[] PairInOrder(int[] t, int tc, out int pairs)
        {
            var faces = new List<int>(tc * 2 + 4);
            pairs = 0;
            for (var k = 0; k < tc;)
            {
                if (k + 1 < tc && TryQuad(t, k, k + 1, out var q0, out var q1, out var q2, out var q3, false))
                {
                    faces.Add(q0); faces.Add(q1); faces.Add(q2); faces.Add(q3);
                    pairs++;
                    k += 2;
                }
                else
                {
                    faces.Add(t[3 * k]); faces.Add(t[3 * k + 1]); faces.Add(t[3 * k + 2]); faces.Add(-1);
                    k++;
                }
            }
            return faces.ToArray();
        }

        // The quad two triangles make across a shared edge; fanOnly: only the edge from t0's last
        // corner to its first (how a fan splits a quad).
        static bool TryQuad(int[] t, int t0, int t1, out int q0, out int q1, out int q2, out int q3, bool fanOnly)
        {
            q0 = q1 = q2 = q3 = -1;
            for (var i = 0; i < 3; i++)
            {
                if (fanOnly && i != 2) continue;
                int x = t[3 * t0 + i], y = t[3 * t0 + (i + 1) % 3], z = t[3 * t0 + (i + 2) % 3];
                for (var j = 0; j < 3; j++)
                {
                    if (t[3 * t1 + j] != y || t[3 * t1 + (j + 1) % 3] != x) continue;
                    var w = t[3 * t1 + (j + 2) % 3];
                    if (w == z || w == x || w == y || x == y || z == x || z == y) return false;
                    // Boundary of the pair with x-y removed: y z x w.
                    q0 = y; q1 = z; q2 = x; q3 = w;
                    return true;
                }
            }
            return false;
        }

        static int[] PairByAngle(Vector3[] p, int[] t, int tc, out int pairs)
        {
            // Every two neighbours as the quad they would make, scored by its worst corner's sine
            // times how flat it is; the best-shaped taken first. Split rectangles score ~1 against
            // ~0.7 for two halves of different quads, so a quad mesh comes back whole whatever
            // order its triangles are in (Unity's import reorders them).
            var edges = new Dictionary<long, int>(tc * 2);
            long n = p.Length;
            var candidates = new List<(float score, int a, int b)>(tc * 3 / 2);
            for (var k = 0; k < tc; k++)
                for (var i = 0; i < 3; i++)
                {
                    int a = t[3 * k + i], b = t[3 * k + (i + 1) % 3];
                    var key = a < b ? a * n + b : b * n + a;
                    if (edges.TryGetValue(key, out var other))
                    {
                        if (other >= 0 && TryQuad(t, other, k, out var q0, out var q1, out var q2, out var q3, false))
                        {
                            var score = QuadScore(p[q0], p[q1], p[q2], p[q3]);
                            if (score > minPairScore) candidates.Add((score, other, k));
                        }
                        edges[key] = -1;
                    }
                    else edges[key] = k;
                }
            candidates.Sort((x, y) => y.score.CompareTo(x.score));
            var mate = new int[tc];
            for (var k = 0; k < tc; k++) mate[k] = -1;
            foreach (var (_, a, b) in candidates)
                if (mate[a] < 0 && mate[b] < 0) { mate[a] = b; mate[b] = a; }
            Augment(candidates, mate, tc);
            var faces = new List<int>(tc * 2 + 4);
            pairs = 0;
            for (var k = 0; k < tc; k++)
            {
                var m = mate[k];
                if (m >= 0 && m < k) continue;
                if (m > k && TryQuad(t, k, m, out var q0, out var q1, out var q2, out var q3, false))
                {
                    faces.Add(q0); faces.Add(q1); faces.Add(q2); faces.Add(q3);
                    pairs++;
                }
                else
                {
                    faces.Add(t[3 * k]); faces.Add(t[3 * k + 1]); faces.Add(t[3 * k + 2]); faces.Add(-1);
                    if (m > k) { faces.Add(t[3 * m]); faces.Add(t[3 * m + 1]); faces.Add(t[3 * m + 2]); faces.Add(-1); }
                }
            }
            return faces.ToArray();
        }

        /// <summary>Longest alternating path (in pairs re-made) searched to pair a lone triangle.</summary>
        public static int augmentDepth = 12;

        // Lone triangles left by the greedy pass: a quad mesh's triangles all have partners, so a
        // lone one is paired by an alternating path -- it takes a neighbour, the neighbour's old
        // partner takes one of its own, ... -- ending at another lone triangle. Better-shaped
        // pairings are tried first at each step.
        static void Augment(List<(float score, int a, int b)> candidates, int[] mate, int tc)
        {
            var adjacency = new List<(int to, float score)>[tc];
            foreach (var (score, a, b) in candidates)
            {
                (adjacency[a] ??= new List<(int, float)>(3)).Add((b, score));
                (adjacency[b] ??= new List<(int, float)>(3)).Add((a, score));
            }
            foreach (var l in adjacency) l?.Sort((x, y) => y.score.CompareTo(x.score));
            var visited = new int[tc];
            var stamp = 0;
            var path = new List<int>();
            for (var lone = 0; lone < tc; lone++)
            {
                if (mate[lone] >= 0 || adjacency[lone] == null) continue;
                stamp++;
                path.Clear();
                if (!Search(lone, 0)) continue;
                // path: lone, n1, m1, n2, m2, ..., end -- pairs (lone,n1), (m1,n2), ...
                for (var i = 0; i + 1 < path.Count; i += 2) { mate[path[i]] = path[i + 1]; mate[path[i + 1]] = path[i]; }
            }

            bool Search(int x, int depth)
            {
                visited[x] = stamp;
                path.Add(x);
                foreach (var (y, _) in adjacency[x])
                {
                    if (visited[y] == stamp) continue;
                    if (mate[y] < 0) { visited[y] = stamp; path.Add(y); return true; }
                    if (depth >= augmentDepth) continue;
                    var z = mate[y];
                    if (visited[z] == stamp || adjacency[z] == null) continue;
                    visited[y] = stamp;
                    path.Add(y);
                    if (Search(z, depth + 1)) return true;
                    path.RemoveAt(path.Count - 1);
                }
                path.RemoveAt(path.Count - 1);
                return false;
            }
        }

        /// <summary>Share of the pairs that must be square for the input to count as a quad mesh.</summary>
        public static float minSquareShare = 0.5f;

        /// <summary>A pair scoring this or more is square (QuadScore).</summary>
        public static float squareScore = 0.9f;

        /// <summary>Two neighbours make a quad only above this score (worst corner's sine times flatness).</summary>
        public static float minPairScore = 0.2f;

        // Worst corner's sine (0 for a reflex corner) times the cosine between the two halves' normals.
        static float QuadScore(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var n0 = Vector3.Cross(b - a, c - a);
            var n1 = Vector3.Cross(c - a, d - a);
            var l0 = n0.magnitude; var l1 = n1.magnitude;
            if (!(l0 > 0f) || !(l1 > 0f)) return 0f;
            var flat = Vector3.Dot(n0, n1) / (l0 * l1);
            if (flat <= 0f) return 0f;
            var normal = (n0 / l0 + n1 / l1).normalized;
            var q = new[] { a, b, c, d };
            var worst = 1f;
            for (var k = 0; k < 4; k++)
            {
                var prev = q[(k + 3) % 4] - q[k];
                var next = q[(k + 1) % 4] - q[k];
                var lp = prev.magnitude; var ln = next.magnitude;
                if (!(lp > 0f) || !(ln > 0f)) return 0f;
                // Counter-clockwise about the normal: next x prev points along it at a convex corner.
                var sin = Vector3.Dot(Vector3.Cross(next, prev), normal) / (lp * ln);
                if (sin < worst) worst = sin;
            }
            // A modelled quad is near a rectangle, whose diagonals match; two halves of different
            // quads make a parallelogram, whose diagonals do not.
            var d0 = (c - a).magnitude; var d1 = (d - b).magnitude;
            var diagonals = pairDiagonalWeight ? Mathf.Min(d0, d1) / Mathf.Max(d0, d1) : 1f;
            return Mathf.Max(0f, worst) * flat * diagonals;
        }

        /// <summary>Score pairs by how alike their diagonals are too. Off: corners and flatness only.</summary>
        public static bool pairDiagonalWeight = false;

        /// <summary>
        /// The input reduced to about <paramref name="targetQuads"/> faces by removing quad strips,
        /// or false (with the reason in stats) when it is not a quad mesh or will not reduce that far.
        /// </summary>
        public static bool Reduce(Vector3[] positions, int[] triangles, int targetQuads,
            out Vector3[] outPositions, out List<int[]> outFaces, out Stats stats)
        {
            var part = new Part { positions = positions, triangles = triangles, target = targetQuads };
            ReduceParts(new[] { part });
            stats = part.stats;
            last = stats;
            outPositions = part.reduced ? part.outPositions : null;
            outFaces = part.reduced ? part.outFaces : null;
            return part.reduced;
        }

        /// <summary>
        /// In a joint reduction a part keeps at least this share of its target, and is held to at
        /// most jointMaxShare of it. Unbounded, the cheapest strips decide alone, and on a car those
        /// are the smooth outer panels: they went to 12-43 quads while the seats kept 2,277.
        /// </summary>
        public static float jointMinShare = 0.8f;
        public static float jointMaxShare = 1.25f;

        /// <summary>One part of a model for <see cref="ReduceParts"/>: its input, and what came of it.</summary>
        public sealed class Part
        {
            /// <summary>The part in the model's space (all parts share one cell size).</summary>
            public Vector3[] positions;
            public int[] triangles;
            /// <summary>Its share of the model's quads, by area: where its strips stop counting when it can go no further.</summary>
            public int target;
            /// <summary>How much its quads matter (1 seen, less hidden): its strips cost this times as much.</summary>
            public float weight = 1f;

            public bool reduced;
            public Vector3[] outPositions;
            public List<int[]> outFaces;
            public Stats stats;
        }

        /// <summary>
        /// The parts of one model reduced together to the sum of their targets: one greedy over all
        /// their strips at one cell size, so a strip on a flat floor pan goes long before one on a
        /// mirror or a wheel arch, whichever part they are in -- an area split decides that per part
        /// before anything knows where the detail is (the Porsche's underbody got 1,228 quads, its
        /// mirrors 40). A part whose strips all refuse while it is still well over its own share
        /// leaves the pool (reason "strips ran out"), its share with it.
        /// </summary>
        public static void ReduceParts(IList<Part> parts, Action<float> onProgress = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var allPositions = new List<Vector3>();
            var allFaces = new List<int>();
            var facePart = new List<int>();
            var eligible = new List<int>();
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                part.reduced = false;
                var ok = Recover(part.positions, part.triangles, out var welded, out var faces, out var stats);
                part.stats = stats;
                stats.target = part.target;
                if (!ok) { stats.reason = "not quads"; continue; }
                var offset = allPositions.Count;
                allPositions.AddRange(welded);
                foreach (var v in faces) allFaces.Add(v < 0 ? -1 : v + offset);
                for (var f = 0; f < faces.Length / 4; f++) facePart.Add(i);
                eligible.Add(i);
            }
            if (eligible.Count == 0) return;

            var recoverMs = watch.ElapsedMilliseconds;
            long buildMs = 0, evaluateMs = 0, applyMs = 0, evaluated = 0;
            var batchCount = 0;
            var stage = System.Diagnostics.Stopwatch.StartNew();
            var state = new State(allPositions.ToArray(), allFaces.ToArray(), facePart.ToArray(), parts.Count);
            double area = 0;
            long pool = 0;
            foreach (var i in eligible) { area += state.Area(i); pool += Math.Max(1, parts[i].target); }
            var cell = (float)Math.Sqrt(area / Math.Max(1, pool));
            state.cell = cell;
            var active = new bool[parts.Count];
            var frozen = new bool[parts.Count];
            var weight = new float[parts.Count];
            foreach (var i in eligible)
            {
                active[i] = true;
                weight[i] = Mathf.Max(1e-3f, parts[i].weight);
                parts[i].stats.cell = cell / Mathf.Sqrt(weight[i]);
            }
            if (!(cell > 0f)) { foreach (var i in eligible) { parts[i].stats.reason = "no area"; active[i] = false; } return; }

            bool Overshot(int i) => state.alive[i] > maxOvershoot * parts[i].target
                                    && state.alive[i] > parts[i].target + maxOvershootQuads;
            long Excess()
            {
                long e = 0;
                foreach (var i in eligible) if (active[i]) e += state.alive[i] - parts[i].target;
                return e;
            }
            var firstExcess = Math.Max(1, Excess());
            // A part takes strips while active and, in a joint reduction, over its least share.
            // A part with no strip left to take never gets one: parts share no faces.
            var stuck = new bool[parts.Count];
            bool Open(int i) => active[i] && !stuck[i]
                                && (eligible.Count == 1 || state.alive[i] > jointMinShare * parts[i].target);
            bool Forced(int i) => eligible.Count > 1 && state.alive[i] > jointMaxShare * parts[i].target;
            var open = new bool[parts.Count];
            var cache = new Dictionary<int, Plan>();
            while (true)
            {
                var excess = Excess();
                if (excess <= 0) break;
                stage.Restart();
                foreach (var i in eligible) open[i] = Open(i);
                // Parts over their shares come down first, alone: the others' strips would wait anyway.
                var forcing = false;
                foreach (var i in eligible) forcing |= open[i] && Forced(i);
                if (forcing) foreach (var i in eligible) open[i] &= Forced(i);
                // Only open parts are built and their strips costed: a done part's would all be refused.
                state.partOpen = open;
                state.Build();
                var chords = state.Chords();
                if (chords.Count == 0) break;
                var plans = new Plan[chords.Count];
                state.batch = batchCount;
                buildMs += stage.ElapsedMilliseconds;
                stage.Restart();
                var reused = 0;
                Parallel.For(0, chords.Count, BackgroundScheduler.Options, k =>
                {
                    var fresh = chords[k];
                    if (fresh.valid && cache.TryGetValue(fresh.key, out var held) && state.Holds(held, fresh))
                    {
                        plans[k] = held;
                        System.Threading.Interlocked.Increment(ref reused);
                    }
                    else plans[k] = state.Evaluate(fresh);
                });
                cache.Clear();
                foreach (var plan in plans) if (plan.costedAt >= 0) cache[plan.key] = plan;
                evaluateMs += stage.ElapsedMilliseconds;
                evaluated += chords.Count - reused;
                batchCount++;
                stage.Restart();
                var hasValid = new bool[parts.Count];
                foreach (var plan in plans)
                {
                    plan.part = state.facePart[plan.chordFaces[0]];
                    var stats = parts[plan.part].stats;
                    if (plan.valid)
                    {
                        hasValid[plan.part] = true;
                        var a = state.alive[plan.part];
                        var t = parts[plan.part].target;
                        // Within [jointMinShare, jointMaxShare] of its share a part's strips compete on
                        // cost; under it a part takes no more; over it its strips go first.
                        plan.scale = eligible.Count == 1 ? 1f
                            : a <= jointMinShare * t ? float.MaxValue
                            : a > jointMaxShare * t ? 1e-3f
                            : weight[plan.part];
                        plan.cost = plan.scale == float.MaxValue ? float.MaxValue : plan.rawCost * plan.scale;
                        continue;
                    }
                    if (plan.why == 1) stats.rejectedSelf++;
                    else if (plan.why == 2) stats.rejectedTopology++;
                    else if (plan.why == 3) { stats.rejectedFold++; stats.foldKinds[plan.foldKind]++; }
                }
                foreach (var i in eligible)
                {
                    if (!open[i]) continue;
                    frozen[i] = !hasValid[i];
                    stuck[i] = frozen[i];
                    if (active[i] && frozen[i] && Overshot(i) && eligible.Count > 1)
                    {
                        active[i] = false;
                        parts[i].stats.reason = "strips ran out";
                    }
                }
                excess = Excess();
                if (excess <= 0) break;

                // Each part over its share is forced on its own, so it gets its own cost limit, from
                // its own cheapest strip; parts within their shares compete under one limit (group -1).
                // Parts over their shares come down first: until they have, the others wait.
                int Group(Plan plan) => plan.scale < 1e-2f ? plan.part : -1;
                bool May(Plan plan) => plan.valid && active[plan.part] && plan.scale < float.MaxValue
                                       && (!forcing || plan.scale < 1e-2f)
                                       // Past the target by less than half the strip: keep it.
                                       && plan.removes <= 2 * excess
                                       // Nor past a part's least share inside a batch.
                                       && !(eligible.Count > 1 && state.alive[plan.part] - plan.removes < jointMinShare * parts[plan.part].target);
                var order = new List<int>(chords.Count);
                for (var k = 0; k < plans.Length; k++) if (May(plans[k])) order.Add(k);
                var lower = new float[plans.Length];
                for (var k = 0; k < plans.Length; k++) lower[k] = plans[k].cost;
                order.Sort((a, b) => lower[a].CompareTo(lower[b]));
                void Cost(Plan plan) => plan.cost = plan.rawCost * plan.scale;
                // The cheapest full cost in each group: measured in lower-bound order, in parallel
                // runs, until no unmeasured strip's lower bound is under it.
                var best = new Dictionary<int, float>();
                var settled = new HashSet<int>();
                var run = new List<Plan>(256);
                for (var o = 0; o < order.Count;)
                {
                    run.Clear();
                    for (; o < order.Count && run.Count < 256; o++)
                    {
                        var plan = plans[order[o]];
                        var g = Group(plan);
                        if (settled.Contains(g)) continue;
                        if (best.TryGetValue(g, out var b) && lower[order[o]] >= b) { settled.Add(g); continue; }
                        if (plan.measured)
                        {
                            Cost(plan);
                            if (!best.TryGetValue(g, out var c) || plan.cost < c) best[g] = plan.cost;
                            continue;
                        }
                        run.Add(plan);
                    }
                    Parallel.For(0, run.Count, BackgroundScheduler.Options, k => state.Measure(run[k]));
                    foreach (var plan in run)
                    {
                        Cost(plan);
                        var g = Group(plan);
                        if (!best.TryGetValue(g, out var b) || plan.cost < b) best[g] = plan.cost;
                    }
                }
                float Limit(Plan plan) => best.TryGetValue(Group(plan), out var b)
                    ? b * batchCostSlack + batchCostFloor * plan.scale : float.MinValue;
                // Every strip whose lower bound is within its group's limit, measured.
                run.Clear();
                foreach (var k in order) if (!plans[k].measured && lower[k] <= Limit(plans[k])) run.Add(plans[k]);
                Parallel.For(0, run.Count, BackgroundScheduler.Options, k => state.Measure(run[k]));
                var candidates = new List<int>();
                foreach (var k in order)
                {
                    var plan = plans[k];
                    if (!plan.measured) continue;
                    Cost(plan);
                    if (plan.cost <= Limit(plan)) candidates.Add(k);
                }
                candidates.Sort((a, b) => plans[a].cost.CompareTo(plans[b].cost));
                var dirty = new bool[state.faceCount];
                var removed = 0;
                var took = new bool[parts.Count];
                foreach (var k in candidates)
                {
                    var plan = plans[k];
                    if (excess <= 0) break;
                    if (plan.removes > 2 * excess) continue;
                    if (eligible.Count > 1 && state.alive[plan.part] - plan.removes < jointMinShare * parts[plan.part].target) continue;
                    if (state.Touches(plan, dirty)) continue;
                    state.Mark(plan, dirty);
                    var before = state.alive[plan.part];
                    state.Apply(plan);
                    excess -= before - state.alive[plan.part];
                    var stats = parts[plan.part].stats;
                    stats.largestError = Mathf.Max(stats.largestError, plan.error);
                    stats.chordsRemoved++;
                    removed++;
                    took[plan.part] = true;
                }
                applyMs += stage.ElapsedMilliseconds;
                foreach (var i in eligible) if (active[i]) parts[i].stats.batches++;
                onProgress?.Invoke(Mathf.Clamp01(1f - Excess() / (float)firstExcess));
                // A forced part that took nothing in its own batch never will (its strips' refusals
                // are its own): it is left where it is and the rest carry on.
                if (forcing)
                {
                    foreach (var i in eligible) if (open[i] && !took[i]) stuck[i] = true;
                    continue;
                }
                if (removed == 0) break;
            }

            lastTimings = $"recover={recoverMs} ms, batches={batchCount}, build={buildMs} ms, " +
                          $"evaluate={evaluateMs} ms ({evaluated} strips), apply={applyMs} ms";
            foreach (var i in eligible)
            {
                var part = parts[i];
                var stats = part.stats;
                stats.outputFaces = state.alive[i];
                stats.ms = watch.ElapsedMilliseconds;
                if (!active[i]) continue;
                if (frozen[i] && Overshot(i))
                {
                    stats.reason = "strips ran out";
                    if (debugStuck != null) { state.Output(i, out var dp, out var df); debugStuck(dp, df); }
                    continue;
                }
                state.Output(i, out var outPositions, out var outFaces);
                stats.irregular = Irregular(outPositions.Length, outFaces);
                // Strips forced far past what the shape allows (a tyre's tread at a few hundred quads):
                // the remesher does better there.
                var tooIrregular = stats.irregular > maxIrregular && outFaces.Count >= irregularMinFaces;
                if (stats.largestError > maxErrorCells * stats.cell || tooIrregular)
                {
                    stats.reason = stats.largestError > maxErrorCells * stats.cell ? "too far off the input" : "too irregular";
                    continue;
                }
                part.reduced = true;
                part.outPositions = outPositions;
                part.outFaces = outFaces;
            }
        }

        // Share of vertices not of valence 4 inside, or 3 on a border (2 at a border's corner).
        static float Irregular(int vertexCount, List<int[]> faces)
        {
            var valence = new int[vertexCount];
            var border = new Dictionary<long, int>();
            foreach (var f in faces)
                for (var k = 0; k < f.Length; k++)
                {
                    int a = f[k], b = f[(k + 1) % f.Length];
                    valence[a]++;
                    var key = a < b ? (long)a * vertexCount + b : (long)b * vertexCount + a;
                    border.TryGetValue(key, out var c);
                    border[key] = c + 1;
                }
            var onBorder = new bool[vertexCount];
            foreach (var kv in border)
                if (kv.Value == 1) { onBorder[(int)(kv.Key / vertexCount)] = true; onBorder[(int)(kv.Key % vertexCount)] = true; }
            var irregular = 0;
            for (var v = 0; v < vertexCount; v++)
            {
                if (onBorder[v] ? valence[v] < 2 || valence[v] > 3 : valence[v] != 4) irregular++;
            }
            return vertexCount > 0 ? irregular / (float)vertexCount : 0f;
        }

        // Each thread's containers for Evaluate and Measure, cleared and reused: costing allocates
        // next to nothing (Unity's collector is slow under many threads allocating).
        sealed class Scratch
        {
            public readonly Dictionary<int, int> parent = new Dictionary<int, int>(), groupOf = new Dictionary<int, int>(),
                keeper = new Dictionary<int, int>(), isKeeper = new Dictionary<int, int>(), borderAt = new Dictionary<int, int>();
            public readonly List<List<int>> lists = new List<List<int>>();
            public int listsUsed;
            public readonly List<int> keys = new List<int>(), kept = new List<int>(), dropped = new List<int>();
            public readonly HashSet<int> chordSet = new HashSet<int>(), affected = new HashSet<int>();
            public readonly HashSet<long> directed = new HashSet<long>();
            public readonly Dictionary<long, int> undirected = new Dictionary<long, int>();
            public readonly HashSet<(int, int, int, int)> faceKeys = new HashSet<(int, int, int, int)>();
            public Vector3[] corners = new Vector3[64];
            public int[] cornerCount = new int[16];

            public List<int> NewList()
            {
                if (listsUsed == lists.Count) lists.Add(new List<int>(4));
                var l = lists[listsUsed++];
                l.Clear();
                return l;
            }

            [ThreadStatic] static Scratch t_Scratch;
            public static Scratch Get() => t_Scratch ??= new Scratch();
        }

        sealed class Plan
        {
            public bool valid;
            public int why; // 1 self, 2 topology, 3 fold
            public int foldKind; public float foldCos;
            public float cost, error;
            public float rawCost;      // before the part's scale; the width's alone until measured
            public float widthCost;
            public bool measured;      // the error is in rawCost
            public int costedAt = -1;  // the batch it was evaluated in
            public int key;            // 2 * (its first face) + axis, as Chords finds it
            public int removes, crossings, part;
            public float scale = 1f;
            public int[] chordFaces, u, v, affected, dropped;
            // Vertices merged into one: group g is members[start[g] .. start[g + 1]), the first its keeper.
            public int[] members, start;
            public Vector3[] p;
        }

        sealed class State
        {
            public Vector3[] P;
            readonly Vector3[] m_Original;
            public int[] F; // 4 per face, -1 in the last slot for a triangle
            bool[] m_Alive;
            readonly double[] m_Q; // 10 per vertex: a11 a12 a13 a22 a23 a33 b1 b2 b3 c
            readonly int[] m_Head, m_Tail, m_Next; // input points each vertex stands for
            public float cell;
            public readonly int[] facePart;
            // The batch each vertex last moved, merged or took in points in: a plan costed since
            // whose neighbourhood no vertex has moved in still holds.
            public int batch;
            readonly int[] m_Stamp;
            public readonly int[] alive; // faces per part
            public int faceCount => F.Length / 4;

            int[] m_Twin;          // per half-edge 4f+k; -1 border, -2 more than two faces
            int[] m_VfStart, m_VfList;
            int[] m_ChordOf;       // per 2f+axis

            public State(Vector3[] welded, int[] faces, int[] facePart, int partCount)
            {
                P = (Vector3[])welded.Clone();
                m_Original = welded;
                F = faces;
                m_Alive = new bool[faces.Length / 4];
                for (var f = 0; f < m_Alive.Length; f++) m_Alive[f] = true;
                this.facePart = facePart;
                alive = new int[partCount];
                foreach (var part in facePart) alive[part]++;
                var n = P.Length;
                m_Head = new int[n]; m_Tail = new int[n]; m_Next = new int[n];
                for (var i = 0; i < n; i++) { m_Head[i] = m_Tail[i] = i; m_Next[i] = -1; }
                m_Q = new double[10 * n];
                m_Stamp = new int[n];
                for (var i = 0; i < n; i++) m_Stamp[i] = -1;
                Build();
                for (var f = 0; f < faceCount; f++)
                {
                    var c = Corners(f);
                    var nrm = Newell(f, P);
                    var area = nrm.magnitude * 0.5f;
                    if (!(area > 0f)) continue;
                    var nn = nrm / (2f * area);
                    for (var k = 0; k < c; k++) AddPlane(F[4 * f + k], nn, -Vector3.Dot(nn, P[F[4 * f + k]]), area);
                    for (var k = 0; k < c; k++)
                    {
                        if (m_Twin[4 * f + k] != -1) continue;
                        int a = F[4 * f + k], b = F[4 * f + (k + 1) % c];
                        var e = P[b] - P[a];
                        var m = Vector3.Cross(e, nn);
                        var len = m.magnitude;
                        if (!(len > 0f)) continue;
                        m /= len;
                        var w = 10f * e.sqrMagnitude;
                        AddPlane(a, m, -Vector3.Dot(m, P[a]), w);
                        AddPlane(b, m, -Vector3.Dot(m, P[b]), w);
                    }
                }
            }

            public int Corners(int f) => F[4 * f + 3] < 0 ? 3 : 4;

            static Vector3 Newell(int f, Vector3[] p, int[] F)
            {
                var c = F[4 * f + 3] < 0 ? 3 : 4;
                var n = Vector3.zero;
                for (var k = 0; k < c; k++)
                {
                    var a = p[F[4 * f + k]]; var b = p[F[4 * f + (k + 1) % c]];
                    n.x += (a.y - b.y) * (a.z + b.z);
                    n.y += (a.z - b.z) * (a.x + b.x);
                    n.z += (a.x - b.x) * (a.y + b.y);
                }
                return n;
            }

            Vector3 Newell(int f, Vector3[] p) => Newell(f, p, F);

            void AddPlane(int v, Vector3 n, float d, float w)
            {
                var q = 10 * v;
                m_Q[q] += w * n.x * n.x; m_Q[q + 1] += w * n.x * n.y; m_Q[q + 2] += w * n.x * n.z;
                m_Q[q + 3] += w * n.y * n.y; m_Q[q + 4] += w * n.y * n.z; m_Q[q + 5] += w * n.z * n.z;
                m_Q[q + 6] += w * d * n.x; m_Q[q + 7] += w * d * n.y; m_Q[q + 8] += w * d * n.z;
                m_Q[q + 9] += w * d * d;
            }

            public double Area(int part)
            {
                double a = 0;
                for (var f = 0; f < faceCount; f++) if (m_Alive[f] && facePart[f] == part) a += 0.5 * Newell(f, P).magnitude;
                return a;
            }

            /// <summary>The parts Build and Chords cover (all when null): the others' faces are left out.</summary>
            public bool[] partOpen;

            bool Covered(int f) => m_Alive[f] && (partOpen == null || partOpen[facePart[f]]);

            int[] m_Count, m_Fill;

            // Faces round each vertex, then each half-edge's twin found among the faces round its
            // start (-1 border, -2 more than two faces), for the covered faces only.
            public void Build()
            {
                var fc = faceCount;
                var n = P.Length;
                if (m_Twin == null || m_Twin.Length != 4 * fc) m_Twin = new int[4 * fc];
                if (m_Count == null || m_Count.Length != n)
                {
                    m_Count = new int[n];
                    m_Fill = new int[n];
                    m_VfStart = new int[n + 1];
                }
                else { Array.Clear(m_Count, 0, n); Array.Clear(m_Fill, 0, n); }
                for (var f = 0; f < fc; f++)
                {
                    if (!Covered(f)) continue;
                    var c = Corners(f);
                    for (var k = 0; k < c; k++) m_Count[F[4 * f + k]]++;
                }
                for (var v = 0; v < n; v++) m_VfStart[v + 1] = m_VfStart[v] + m_Count[v];
                if (m_VfList == null || m_VfList.Length < m_VfStart[n]) m_VfList = new int[m_VfStart[n]];
                for (var f = 0; f < fc; f++)
                {
                    if (!Covered(f)) continue;
                    var c = Corners(f);
                    for (var k = 0; k < c; k++) { var a = F[4 * f + k]; m_VfList[m_VfStart[a] + m_Fill[a]++] = f; }
                }
                Parallel.For(0, fc, BackgroundScheduler.Options, f =>
                {
                    if (!Covered(f)) return;
                    var c = Corners(f);
                    for (var k = 0; k < c; k++)
                    {
                        int a = F[4 * f + k], b = F[4 * f + (k + 1) % c];
                        var twin = -1;
                        for (var s = m_VfStart[a]; s < m_VfStart[a + 1] && twin != -2; s++)
                        {
                            var g = m_VfList[s];
                            if (g == f) continue;
                            var cg = Corners(g);
                            for (var j = 0; j < cg; j++)
                            {
                                int x = F[4 * g + j], y = F[4 * g + (j + 1) % cg];
                                if (!(x == a && y == b || x == b && y == a)) continue;
                                twin = twin == -1 ? 4 * g + j : -2;
                                break;
                            }
                        }
                        m_Twin[4 * f + k] = twin;
                    }
                });
            }

            // Each quad strip as its faces (with the side each is entered by) and its rungs. A strip that
            // crosses itself passes the crossing quad twice, once each way: all four of its corners
            // become one point.
            public List<Plan> Chords()
            {
                var fc = faceCount;
                m_ChordOf = new int[2 * fc];
                for (var i = 0; i < m_ChordOf.Length; i++) m_ChordOf[i] = -1;
                var list = new List<Plan>();
                var seq = new List<(int f, int entry)>();
                var back = new List<(int f, int entry)>();
                for (var f0 = 0; f0 < fc; f0++)
                {
                    if (!Covered(f0) || Corners(f0) != 4) continue;
                    for (var axis = 0; axis < 2; axis++)
                    {
                        if (m_ChordOf[2 * f0 + axis] >= 0) continue;
                        var id = list.Count;
                        var plan = new Plan { valid = true, key = 2 * f0 + axis };
                        seq.Clear(); back.Clear();
                        var closed = false;
                        // Forward: enter f0 by side `axis`, leave by axis + 2.
                        int cur = f0, entry = axis;
                        while (true)
                        {
                            var ax = entry % 2;
                            if (m_ChordOf[2 * cur + ax] == id)
                            {
                                if (cur == f0 && entry == axis) closed = true;
                                else { plan.valid = false; plan.why = 1; }
                                break;
                            }
                            if (m_ChordOf[2 * cur + 1 - ax] == id) plan.crossings++;
                            m_ChordOf[2 * cur + ax] = id;
                            seq.Add((cur, entry));
                            var h = m_Twin[4 * cur + (entry + 2) % 4];
                            if (h < 0) break;
                            int g = h / 4, j = h % 4;
                            if (Corners(g) != 4) break;
                            cur = g; entry = j;
                        }
                        if (!closed && plan.valid)
                        {
                            // Backward from f0's entry side: g is entered by the side opposite the one crossed.
                            var h = m_Twin[4 * f0 + axis];
                            while (h >= 0)
                            {
                                int g = h / 4, j = h % 4;
                                if (Corners(g) != 4) break;
                                var e = (j + 2) % 4;
                                var ax = e % 2;
                                if (m_ChordOf[2 * g + ax] == id) { plan.valid = false; plan.why = 1; break; }
                                if (m_ChordOf[2 * g + 1 - ax] == id) plan.crossings++;
                                m_ChordOf[2 * g + ax] = id;
                                back.Add((g, e));
                                h = m_Twin[4 * g + e];
                            }
                        }
                        if (plan.crossings > maxCrossings) { plan.valid = false; plan.why = 1; }
                        var all = new List<(int f, int entry)>(back.Count + seq.Count);
                        for (var i = back.Count - 1; i >= 0; i--) all.Add(back[i]);
                        all.AddRange(seq);
                        var faces = new List<int>(all.Count);
                        var us = new List<int>(all.Count + 1); var vs = new List<int>(all.Count + 1);
                        // Rungs as (left, right) of the strip's direction: a face entered by side e has
                        // e+1 and e+2 on its left.
                        if (!closed)
                        {
                            var (f, e) = all[0];
                            us.Add(F[4 * f + (e + 1) % 4]); vs.Add(F[4 * f + e]);
                        }
                        foreach (var (f, e) in all)
                        {
                            faces.Add(f);
                            var x = (e + 2) % 4;
                            us.Add(F[4 * f + x]); vs.Add(F[4 * f + (x + 1) % 4]);
                        }
                        plan.chordFaces = faces.ToArray();
                        plan.u = us.ToArray();
                        plan.v = vs.ToArray();
                        plan.removes = faces.Count - plan.crossings;
                        list.Add(plan);
                    }
                }
                return list;
            }

            // Merged point on the rung u-v where the summed quadric is least, with a little pull to the middle.
            Vector3 OnRung(int u, int v)
            {
                int qu = 10 * u, qv = 10 * v;
                double a11 = m_Q[qu] + m_Q[qv], a12 = m_Q[qu + 1] + m_Q[qv + 1], a13 = m_Q[qu + 2] + m_Q[qv + 2],
                    a22 = m_Q[qu + 3] + m_Q[qv + 3], a23 = m_Q[qu + 4] + m_Q[qv + 4], a33 = m_Q[qu + 5] + m_Q[qv + 5],
                    b1 = m_Q[qu + 6] + m_Q[qv + 6], b2 = m_Q[qu + 7] + m_Q[qv + 7], b3 = m_Q[qu + 8] + m_Q[qv + 8];
                Vector3 pu = P[u], pv = P[v];
                double dx = pv.x - pu.x, dy = pv.y - pu.y, dz = pv.z - pu.z;
                double ax = a11 * dx + a12 * dy + a13 * dz, ay = a12 * dx + a22 * dy + a23 * dz, az = a13 * dx + a23 * dy + a33 * dz;
                double dAd = dx * ax + dy * ay + dz * az;
                double gx = a11 * pu.x + a12 * pu.y + a13 * pu.z + b1, gy = a12 * pu.x + a22 * pu.y + a23 * pu.z + b2,
                    gz = a13 * pu.x + a23 * pu.y + a33 * pu.z + b3;
                double dg = dx * gx + dy * gy + dz * gz;
                // Regulariser: lambda * trace(A) * |d|^2 * (t - 0.5)^2.
                var reg = 1e-3 * (a11 + a22 + a33) * (dx * dx + dy * dy + dz * dz);
                var denom = dAd + reg;
                var t = denom > 0 ? (-dg + 0.5 * reg) / denom : 0.5;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                var p = new Vector3((float)(pu.x + t * dx), (float)(pu.y + t * dy), (float)(pu.z + t * dz));
                return p;
            }

            public Plan Evaluate(Plan plan)
            {
                plan.costedAt = batch;
                if (!plan.valid) return plan;
                var r = plan.u.Length;
                var scratch = Scratch.Get();
                // Rung ends merged into groups (a crossing quad's four corners are one group).
                var parent = scratch.parent;
                parent.Clear();
                int Find(int x)
                {
                    while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                    return x;
                }
                for (var i = 0; i < r; i++)
                {
                    int u = plan.u[i], v = plan.v[i];
                    if (u == v) { plan.valid = false; plan.why = 1; return plan; }
                    if (!parent.ContainsKey(u)) parent[u] = u;
                    if (!parent.ContainsKey(v)) parent[v] = v;
                    int a = Find(u), b = Find(v);
                    if (a != b) parent[b] = a;
                }
                var groupOf = scratch.groupOf; // root -> group
                groupOf.Clear();
                var lists = new List<List<int>>();
                scratch.listsUsed = 0;
                scratch.keys.Clear();
                scratch.keys.AddRange(parent.Keys);
                foreach (var w in scratch.keys)
                {
                    var root = Find(w);
                    if (!groupOf.TryGetValue(root, out var g)) { g = lists.Count; groupOf[root] = g; lists.Add(scratch.NewList()); }
                    lists[g].Add(w);
                }
                var groups = lists.Count;
                var start = new int[groups + 1];
                for (var g = 0; g < groups; g++)
                {
                    if (lists[g].Count > 4 || lists[g].Count > 2 && plan.crossings == 0) { plan.valid = false; plan.why = 1; return plan; }
                    start[g + 1] = start[g] + lists[g].Count;
                }
                var members = new int[start[groups]];
                var keeper = scratch.keeper;     // vertex -> its group's keeper
                var isKeeper = scratch.isKeeper; // keeper -> group
                keeper.Clear();
                isKeeper.Clear();
                for (var g = 0; g < groups; g++)
                {
                    var l = lists[g];
                    for (var k = 0; k < l.Count; k++) { members[start[g] + k] = l[k]; keeper[l[k]] = l[0]; }
                    isKeeper[l[0]] = g;
                }
                // Each pair group's two ends in strip order (left, right), for the end placements.
                var left = new int[groups]; var right = new int[groups];
                for (var g = 0; g < groups; g++) left[g] = right[g] = -1;
                for (var i = 0; i < r; i++)
                {
                    var g = groupOf[Find(plan.u[i])];
                    if (lists[g].Count == 2) { left[g] = plan.u[i]; right[g] = plan.v[i]; }
                }

                var chordSet = scratch.chordSet;
                chordSet.Clear();
                foreach (var f in plan.chordFaces) chordSet.Add(f);
                var affected = scratch.affected;
                affected.Clear();
                foreach (var w in members)
                    for (var s = m_VfStart[w]; s < m_VfStart[w + 1]; s++)
                    {
                        var f = m_VfList[s];
                        if (!chordSet.Contains(f)) affected.Add(f);
                    }
                var p = new Vector3[groups];
                Vector3 At(int w) => isKeeper.TryGetValue(w, out var g) ? p[g] : P[w];
                int Map(int w) => keeper.TryGetValue(w, out var k) ? k : w;

                var kept = scratch.kept; kept.Clear();
                var dropped = scratch.dropped; dropped.Clear();
                var directed = scratch.directed; directed.Clear();
                var undirected = scratch.undirected; undirected.Clear();
                var faceKeys = scratch.faceKeys; faceKeys.Clear();
                long n = P.Length;
                var m = new int[4];
                foreach (var f in affected)
                {
                    var c = Corners(f);
                    for (var k = 0; k < c; k++) m[k] = Map(F[4 * f + k]);
                    var distinct = true;
                    for (var a = 0; a < c && distinct; a++)
                        for (var b = a + 1; b < c; b++) if (m[a] == m[b]) { distinct = false; break; }
                    if (!distinct)
                    {
                        if (c == 3) { dropped.Add(f); continue; }
                        plan.valid = false; plan.why = 2; return plan;
                    }
                    kept.Add(f);
                    // Same face twice (a pillow).
                    if (!faceKeys.Add(SortedKey(m[0], m[1], m[2], c == 4 ? m[3] : -1))) { plan.valid = false; plan.why = 2; return plan; }
                    for (var k = 0; k < c; k++)
                    {
                        int a = m[k], b = m[(k + 1) % c];
                        if (!isKeeper.ContainsKey(a) && !isKeeper.ContainsKey(b)) continue;
                        if (!directed.Add(a * n + b)) { plan.valid = false; plan.why = 2; return plan; }
                        var key = a < b ? a * n + b : b * n + a;
                        undirected.TryGetValue(key, out var cnt);
                        if (cnt >= 2) { plan.valid = false; plan.why = 2; return plan; }
                        undirected[key] = cnt + 1;
                    }
                }
                var borderAt = scratch.borderAt;
                borderAt.Clear();
                foreach (var kv in undirected)
                {
                    if (kv.Value != 1) continue;
                    int a = (int)(kv.Key / n), b = (int)(kv.Key % n);
                    if (isKeeper.ContainsKey(a)) { borderAt.TryGetValue(a, out var c); borderAt[a] = c + 1; }
                    if (isKeeper.ContainsKey(b)) { borderAt.TryGetValue(b, out var c); borderAt[b] = c + 1; }
                }
                foreach (var kv in borderAt) if (kv.Value > 2) { plan.valid = false; plan.why = 2; return plan; }

                // Placement: each rung's quadric point; when that folds a face, every point at its
                // rung's middle, then all at the strip's left side, then all at its right. Merged
                // points chosen one rung at a time can zigzag along the strip and fold the quads
                // beside it. A crossing's group sits at its corners' centre.
                var np = new Vector3[4];
                int Fold()
                {
                    foreach (var f in kept)
                    {
                        var c = Corners(f);
                        var before = Newell(f, P);
                        for (var k = 0; k < c; k++) np[k] = At(Map(F[4 * f + k]));
                        var after = Vector3.zero;
                        for (var k = 0; k < c; k++)
                        {
                            var a = np[k]; var b = np[(k + 1) % c];
                            after.x += (a.y - b.y) * (a.z + b.z);
                            after.y += (a.z - b.z) * (a.x + b.x);
                            after.z += (a.x - b.x) * (a.y + b.y);
                        }
                        var lb = before.magnitude; var la = after.magnitude;
                        if (!(la > 1e-12f * cell * cell)) return 1;
                        if (lb > 0f && Vector3.Dot(before, after) < minNormalCos * lb * la) return 2;
                        if (c == 4 && Twisted(np[0], np[1], np[2], np[3])) return 3;
                        // A quad pressed into a needle: the spikes a radial dome (a lamp's lens, a
                        // spoke) is left with once its rings run out.
                        if (c == 4)
                        {
                            var q = CornerSine(np[0], np[1], np[2], np[3], after);
                            if (q < minCornerSine && q < needleWorsening * CornerSine(P[F[4 * f]], P[F[4 * f + 1]], P[F[4 * f + 2]], P[F[4 * f + 3]], before))
                                return 4;
                        }
                    }
                    return 0;
                }
                var fold = 0;
                for (var mode = 0; mode < (placementFallback ? 4 : 1); mode++)
                {
                    for (var g = 0; g < groups; g++)
                    {
                        if (left[g] < 0)
                        {
                            var sum = Vector3.zero;
                            for (var k = start[g]; k < start[g + 1]; k++) sum += P[members[k]];
                            p[g] = sum / (start[g + 1] - start[g]);
                        }
                        else if (mode == 0) p[g] = OnRung(left[g], right[g]);
                        else p[g] = Vector3.LerpUnclamped(P[left[g]], P[right[g]], mode == 1 ? 0.5f : mode == 2 ? 0f : 1f);
                    }
                    fold = Fold();
                    if (fold == 0) break;
                }
                if (fold != 0) { plan.valid = false; plan.why = 3; plan.foldKind = fold; return plan; }

                double width = 0;
                for (var i = 0; i < r; i++) width = Math.Max(width, (P[plan.u[i]] - P[plan.v[i]]).sqrMagnitude);
                plan.widthCost = (float)(width / (cell * cell));
                plan.rawCost = plan.widthCost;
                plan.measured = false;
                plan.p = p;
                plan.members = members;
                plan.start = start;
                plan.affected = kept.ToArray();
                plan.dropped = dropped.ToArray();
                return plan;
            }

            /// <summary>
            /// A plan's error (the input points each group stood for, against the faces round its new
            /// point) and its full cost. Its cost before is a lower bound: strips that cannot make a
            /// batch are never measured.
            /// </summary>
            public void Measure(Plan plan)
            {
                if (plan.measured || !plan.valid) return;
                var members = plan.members; var start = plan.start; var p = plan.p;
                var groups = start.Length - 1;
                var scratch = Scratch.Get();
                var keeper = scratch.keeper; keeper.Clear();
                var isKeeper = scratch.isKeeper; isKeeper.Clear();
                for (var g = 0; g < groups; g++)
                {
                    for (var k = start[g]; k < start[g + 1]; k++) keeper[members[k]] = members[start[g]];
                    isKeeper[members[start[g]]] = g;
                }
                // The faces round the new points, merged, as corner positions; each group's faces.
                var kept = plan.affected;
                if (scratch.corners.Length < 4 * kept.Length) scratch.corners = new Vector3[8 * kept.Length];
                if (scratch.cornerCount.Length < kept.Length) scratch.cornerCount = new int[2 * kept.Length];
                var corners = scratch.corners;
                var cornerCount = scratch.cornerCount;
                scratch.listsUsed = 0;
                var ring = scratch.lists;
                for (var g = 0; g < groups; g++) scratch.NewList();
                for (var i = 0; i < kept.Length; i++)
                {
                    var f = kept[i];
                    var c = Corners(f);
                    cornerCount[i] = c;
                    for (var k = 0; k < c; k++)
                    {
                        var w = F[4 * f + k];
                        if (keeper.TryGetValue(w, out var kw)) w = kw;
                        if (isKeeper.TryGetValue(w, out var g)) { corners[4 * i + k] = p[g]; ring[g].Add(i); }
                        else corners[4 * i + k] = P[w];
                    }
                }
                var worst = 0f;
                for (var g = 0; g < groups; g++)
                {
                    // The new point against the input it stands for: a point pulled off the surface
                    // (a shard) is far from all of it.
                    var nearest = float.MaxValue;
                    var faces = ring[g];
                    for (var k = start[g]; k < start[g + 1]; k++)
                        for (var o = m_Head[members[k]]; o >= 0; o = m_Next[o])
                        {
                            var q = m_Original[o];
                            nearest = Mathf.Min(nearest, (q - p[g]).sqrMagnitude);
                            if (faces.Count == 0)
                            {
                                // Nothing left round the point: the piece it stood on is gone.
                                worst = Mathf.Max(worst, (q - p[g]).sqrMagnitude);
                                continue;
                            }
                            // The worst of each point's nearest: a point nearer than the worst so far to
                            // any face cannot raise it, so it stops there.
                            var best = float.MaxValue;
                            foreach (var i in faces)
                            {
                                var a0 = corners[4 * i];
                                for (var t = 1; t + 1 < cornerCount[i]; t++)
                                {
                                    var cp = SpatialGrid.ClosestPointOnTriangle(q, a0, corners[4 * i + t], corners[4 * i + t + 1]);
                                    var d = (cp - q).sqrMagnitude;
                                    if (d < best) best = d;
                                }
                                if (best <= worst) break;
                            }
                            if (best < float.MaxValue && best > worst) worst = best;
                        }
                    if (nearest < float.MaxValue && nearest > worst) worst = nearest;
                }
                plan.error = Mathf.Sqrt(worst);
                var e = plan.error / (errorCells * cell);
                plan.rawCost = plan.widthCost + e * e;
                plan.measured = true;
            }

            static (int, int, int, int) SortedKey(int a, int b, int c, int d)
            {
                if (a > b) (a, b) = (b, a);
                if (c > d) (c, d) = (d, c);
                if (a > c) (a, c) = (c, a);
                if (b > d) (b, d) = (d, b);
                if (b > c) (b, c) = (c, b);
                return (a, b, c, d);
            }

            // Smallest corner sine of a quad about its normal (negative at a reflex corner).
            static float CornerSine(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
            {
                var l = normal.magnitude;
                if (!(l > 0f)) return 0f;
                normal /= l;
                var worst = 1f;
                Vector3 p0 = a, p1 = b, p2 = c, p3 = d;
                for (var k = 0; k < 4; k++)
                {
                    var prev = p3 - p0; var next = p1 - p0;
                    var lp = prev.magnitude; var ln = next.magnitude;
                    var sin = lp > 0f && ln > 0f ? Vector3.Dot(Vector3.Cross(next, prev), normal) / (lp * ln) : 0f;
                    if (sin < worst) worst = sin;
                    (p0, p1, p2, p3) = (p1, p2, p3, p0);
                }
                return worst;
            }

            static bool Twisted(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                var n0 = Vector3.Cross(b - a, c - a);
                var n1 = Vector3.Cross(c - a, d - a);
                var n2 = Vector3.Cross(c - b, d - b);
                var n3 = Vector3.Cross(d - b, a - b);
                return Vector3.Dot(n0, n1) < 0f || Vector3.Dot(n2, n3) < 0f;
            }

            /// <summary>The cached plan for this strip, if it is the same strip and nothing round it has changed.</summary>
            public bool Holds(Plan cached, Plan fresh)
            {
                if (cached == null || cached.costedAt < 0 || cached.crossings != fresh.crossings
                    || cached.chordFaces.Length != fresh.chordFaces.Length || cached.u.Length != fresh.u.Length)
                    return false;
                for (var i = 0; i < fresh.chordFaces.Length; i++) if (cached.chordFaces[i] != fresh.chordFaces[i]) return false;
                for (var i = 0; i < fresh.u.Length; i++)
                    if (cached.u[i] != fresh.u[i] || cached.v[i] != fresh.v[i]) return false;
                // Every vertex of every face round the rungs (what Evaluate reads, and more).
                for (var i = 0; i < fresh.u.Length; i++)
                    for (var side = 0; side < 2; side++)
                    {
                        var w = side == 0 ? fresh.u[i] : fresh.v[i];
                        if (m_Stamp[w] >= cached.costedAt) return false;
                        for (var s = m_VfStart[w]; s < m_VfStart[w + 1]; s++)
                        {
                            var f = m_VfList[s];
                            var c = Corners(f);
                            for (var k = 0; k < c; k++) if (m_Stamp[F[4 * f + k]] >= cached.costedAt) return false;
                        }
                    }
                return true;
            }

            // A plan's faces and every face round its vertices, as already taken in this batch.
            public bool Touches(Plan plan, bool[] dirty)
            {
                foreach (var f in plan.chordFaces) if (dirty[f]) return true;
                foreach (var f in plan.affected) if (dirty[f]) return true;
                foreach (var f in plan.dropped) if (dirty[f]) return true;
                // Two rings: the faces round the affected faces' vertices.
                foreach (var f in plan.affected)
                {
                    var c = Corners(f);
                    for (var k = 0; k < c; k++)
                    {
                        var w = F[4 * f + k];
                        for (var s = m_VfStart[w]; s < m_VfStart[w + 1]; s++) if (dirty[m_VfList[s]]) return true;
                    }
                }
                return false;
            }

            public void Mark(Plan plan, bool[] dirty)
            {
                foreach (var f in plan.chordFaces) dirty[f] = true;
                foreach (var f in plan.affected) dirty[f] = true;
                foreach (var f in plan.dropped) dirty[f] = true;
            }

            public void Apply(Plan plan)
            {
                var keeper = new Dictionary<int, int>(plan.members.Length);
                for (var g = 0; g + 1 < plan.start.Length; g++)
                {
                    var k0 = plan.members[plan.start[g]];
                    P[k0] = plan.p[g];
                    keeper[k0] = k0;
                    m_Stamp[k0] = batch;
                    for (var k = plan.start[g] + 1; k < plan.start[g + 1]; k++)
                    {
                        var w = plan.members[k];
                        keeper[w] = k0;
                        m_Stamp[w] = batch;
                        for (var q = 0; q < 10; q++) m_Q[10 * k0 + q] += m_Q[10 * w + q];
                        m_Next[m_Tail[k0]] = m_Head[w];
                        m_Tail[k0] = m_Tail[w];
                        m_Head[w] = -1;
                    }
                }
                var gone = new HashSet<int>(plan.chordFaces);
                foreach (var f in gone) { m_Alive[f] = false; alive[facePart[f]]--; }
                foreach (var f in plan.dropped) { m_Alive[f] = false; alive[facePart[f]]--; }
                foreach (var f in plan.affected)
                {
                    var c = Corners(f);
                    for (var k = 0; k < c; k++)
                        if (keeper.TryGetValue(F[4 * f + k], out var u)) F[4 * f + k] = u;
                }
            }

            public void Output(int part, out Vector3[] positions, out List<int[]> faces)
            {
                var remap = new int[P.Length];
                for (var i = 0; i < remap.Length; i++) remap[i] = -1;
                var outP = new List<Vector3>();
                faces = new List<int[]>(alive[part]);
                for (var f = 0; f < faceCount; f++)
                {
                    if (!m_Alive[f] || facePart[f] != part) continue;
                    var c = Corners(f);
                    var face = new int[c];
                    for (var k = 0; k < c; k++)
                    {
                        var w = F[4 * f + k];
                        if (remap[w] < 0) { remap[w] = outP.Count; outP.Add(P[w]); }
                        face[k] = remap[w];
                    }
                    faces.Add(face);
                }
                positions = outP.ToArray();
            }
        }
    }
}
