// Split every edge whose integer offset spans more than one cell, without ever re-measuring it.
//
// Port of QuadriFlow's subdivide_edgeDiff. The difference from EdgeSubdivide, which this replaces
// on the integer-grid path, is the whole point: the offsets are the primary data here, not the
// fields. A split edge hands each half a share of its offset (floor and remainder), and the two new
// interior edges get offsets DEFINED by their faces' closure — so the refined map is exactly as
// consistent as the one it came from, and the cascade EdgeSubdivide had to guard against cannot
// start. The fields on the midpoint are interpolated only so extraction has a position to average.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed partial class IntegerGridMap
    {
        struct EdgeLink
        {
            public int id;
            public float length;
            public Vector2Int diff;
            public int maxLen => Mathf.Max(Mathf.Abs(diff.x), Mathf.Abs(diff.y));
        }

        /// <summary>Max-heap on <see cref="EdgeLink.maxLen"/>: the longest offsets split first.</summary>
        sealed class EdgeLinkHeap
        {
            readonly List<EdgeLink> m_Items = new List<EdgeLink>();

            public int count => m_Items.Count;

            public void Push(EdgeLink link)
            {
                m_Items.Add(link);
                var i = m_Items.Count - 1;
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (m_Items[parent].maxLen >= m_Items[i].maxLen) break;
                    (m_Items[parent], m_Items[i]) = (m_Items[i], m_Items[parent]);
                    i = parent;
                }
            }

            public EdgeLink Pop()
            {
                var top = m_Items[0];
                var last = m_Items.Count - 1;
                m_Items[0] = m_Items[last];
                m_Items.RemoveAt(last);

                var i = 0;
                while (true)
                {
                    var left = 2 * i + 1;
                    if (left >= m_Items.Count) break;
                    var largest = left;
                    var right = left + 1;
                    if (right < m_Items.Count && m_Items[right].maxLen > m_Items[left].maxLen)
                        largest = right;
                    if (m_Items[i].maxLen >= m_Items[largest].maxLen) break;
                    (m_Items[i], m_Items[largest]) = (m_Items[largest], m_Items[i]);
                    i = largest;
                }

                return top;
            }
        }

        struct FaceSpace
        {
            public int orient;
            public Vector3Int d;
            public Vector3 q;
            public Vector3 n;
        }

        /// <summary>
        /// Splits edges until every offset component is at most <paramref name="maxLen"/>. Returns
        /// the number of splits. Rebuilds <see cref="allow"/> afterwards.
        /// </summary>
        /// <summary>
        /// Split budget, as a multiple of the half-edge count. A diagnostic knob: raising it is how
        /// a case that hits the cap says whether it was doing real work or cycling.
        /// </summary>
        public static float splitBudgetPerEdge = 0.5f;

        /// <summary>
        /// Shortest edge, in lattice cells, that subdivision will still split. See the refusal in
        /// the split loop: below this an over-length offset is a fold, not a long edge.
        /// </summary>
        const float k_MinSplitCells = 1f / 32f;

        /// <summary>Splits a single face may take part in before it is treated as cycling.</summary>
        const int k_MaxSplitsPerFace = 12;

        /// <summary>Whether a budget overrun discards the partial subdivision.</summary>
        public static bool rollbackOnBudget = true;

        public int Subdivide(int maxLen = 1)
        {
            // HALF A SUBDIVISION IS WORSE THAN NONE, which is the lesson EdgeSubdivide already
            // carries and which this pass had to learn again. A refinement that hits the cap below
            // has paid the vertex cost, created new unconstrained edges, and still left the long
            // edges it was called to remove — measured on cylinder_capped, where abandoning in
            // place left 65,672 unrepresentable edges against 1,944 for not running at all. So the
            // map is snapshotted and put back exactly as it was.
            var snapshot = Capture();
            var faces = faceCount;

            // Every half-edge's offset in its face's frame. This is what the split arithmetic works
            // on; the stored per-edge offsets are rewritten from it afterwards.
            var diffs = new List<Vector2Int>(faces * 3);
            for (var i = 0; i < faces; i++)
                for (var j = 0; j < 3; j++)
                    diffs.Add(Rshift90(edgeDiff[feId[3 * i + j]], feOrient[3 * i + j]));

            // Per face: how its frame relates to its first vertex's cross, and per slot how the
            // stored rotation relates to the one the crosses alone would predict.
            var spaces = new List<FaceSpace>(faces);
            var orientDiff = new int[3];
            for (var i = 0; i < faces; i++)
            {
                var space = new FaceSpace { q = Q[F[3 * i]], n = N[F[3 * i]] };
                for (var j = 0; j < 3; j++)
                {
                    var finalOrient = feOrient[3 * i + j];
                    var eid = feId[3 * i + j];
                    SingularityAnalysis.Compat4Index(Q[edgeX[eid]], N[edgeX[eid]], space.q, space.n,
                        out var a, out var b);
                    var target = Mod4(b - a);
                    if (F[3 * i + j] == edgeY[eid]) target = Mod4(target + 2);
                    orientDiff[j] = Mod4(finalOrient - target);
                }

                if (orientDiff[0] == orientDiff[1]) space.orient = orientDiff[0];
                else if (orientDiff[0] == orientDiff[2]) space.orient = orientDiff[2];
                else if (orientDiff[1] == orientDiff[2]) space.orient = orientDiff[1];
                else space.orient = 0;

                space.d = new Vector3Int(
                    Mod4(orientDiff[0] - space.orient),
                    Mod4(orientDiff[1] - space.orient),
                    Mod4(orientDiff[2] - space.orient));
                spaces.Add(space);
            }

            var queue = new EdgeLinkHeap();
            for (var h = 0; h < E2E.Count; h++)
            {
                var v0 = F[h];
                var v1 = F[Next3(h)];
                if (nonManifoldVertex[v0] || nonManifoldVertex[v1]) continue;
                var diff = diffs[h];
                if (Mathf.Abs(diff.x) <= maxLen && Mathf.Abs(diff.y) <= maxLen) continue;
                var other = E2E[h];
                if (other == -1 || other > h)
                    queue.Push(new EdgeLink { id = h, length = (V[v0] - V[v1]).sqrMagnitude, diff = diff });
            }

            void AnalyzeOrient(int f0, Vector3Int d)
            {
                var space = spaces[f0];
                for (var j = 0; j < 3; j++)
                {
                    var orient = space.orient + d[j];
                    var v = Mathf.Min(F[3 * f0 + j], F[3 * f0 + (j + 1) % 3]);
                    SingularityAnalysis.Compat4Index(Q[v], N[v], space.q, space.n, out var a, out var b);
                    if (F[3 * f0 + j] != v) orient += 2;
                    feOrient[3 * f0 + j] = Mod4(orient + b - a);
                }
                space.d = d;
                spaces[f0] = space;

                for (var j = 0; j < 3; j++)
                {
                    var eid = feId[3 * f0 + j];
                    var orient = feOrient[3 * f0 + j];
                    edgeDiff[eid] = Rshift90(diffs[3 * f0 + j], Mod4(4 - orient));
                }
            }

            void FixOrient(int f0)
            {
                for (var j = 0; j < 3; j++)
                {
                    var diff = edgeDiff[feId[3 * f0 + j]];
                    if (Rshift90(diff, feOrient[3 * f0 + j]) == diffs[3 * f0 + j]) continue;

                    var orient = 0;
                    while (orient < 4 && Rshift90(diff, orient) != diffs[3 * f0 + j]) orient++;
                    if (orient == 4)
                    {
                        // No rotation of the stored offset reproduces the face's view of it. That
                        // means the two faces sharing the edge disagree, which is the unrouted case;
                        // leave it as it is and let the count below say so.
                        stats.subdivideOutOfRange++;
                        continue;
                    }

                    var space = spaces[f0];
                    var d = space.d;
                    d[j] = Mod4(d[j] + orient - feOrient[3 * f0 + j]);
                    space.d = d;
                    spaces[f0] = space;
                    feOrient[3 * f0 + j] = orient;
                }
            }

            void SetTwin(int a, int b)
            {
                E2E[a] = b;
                if (b != -1) E2E[b] = a;
            }

            void AddFacePlaceholder()
            {
                F.Add(0); F.Add(0); F.Add(0);
                E2E.Add(-1); E2E.Add(-1); E2E.Add(-1);
                diffs.Add(Vector2Int.zero); diffs.Add(Vector2Int.zero); diffs.Add(Vector2Int.zero);
                feId.Add(-1); feId.Add(-1); feId.Add(-1);
                feOrient.Add(0); feOrient.Add(0); feOrient.Add(0);
                sharp.Add(false); sharp.Add(false); sharp.Add(false);
                spaces.Add(default);
            }

            void SetFace(int f, int a, int b, int c)
            {
                F[3 * f] = a; F[3 * f + 1] = b; F[3 * f + 2] = c;
            }

            void SetFaceEdges(int f, int a, int b, int c)
            {
                feId[3 * f] = a; feId[3 * f + 1] = b; feId[3 * f + 2] = c;
            }

            void SetFaceSharp(int f, bool a, bool b, bool c)
            {
                sharp[3 * f] = a; sharp[3 * f + 1] = b; sharp[3 * f + 2] = c;
            }

            void SetFaceDiffs(int f, Vector2Int a, Vector2Int b, Vector2Int c)
            {
                diffs[3 * f] = a; diffs[3 * f + 1] = b; diffs[3 * f + 2] = c;
            }

            /// <summary>Signed parametric area of a face, in half cells. Negative is a fold.</summary>
            int Area(int f)
            {
                var d1 = diffs[3 * f];
                var d2 = diffs[3 * f + 1];
                return d1.x * d2.y - d1.y * d2.x;
            }

            // The cached per-half-edge view, re-derived from the authority for one face.
            void Refresh(int f)
            {
                for (var j = 0; j < 3; j++)
                    diffs[3 * f + j] = Rshift90(edgeDiff[feId[3 * f + j]], feOrient[3 * f + j]);
            }

            int AddEdge(int a, int b)
            {
                var id = edgeDiff.Count;
                edgeX.Add(Mathf.Min(a, b));
                edgeY.Add(Mathf.Max(a, b));
                edgeDiff.Add(Vector2Int.zero);
                return id;
            }

            void Schedule(int f)
            {
                for (var i = 0; i < 3; i++)
                {
                    var diff = diffs[3 * f + i];
                    if (Mathf.Abs(diff.x) <= maxLen && Mathf.Abs(diff.y) <= maxLen) continue;
                    queue.Push(new EdgeLink
                    {
                        id = 3 * f + i,
                        length = (V[F[3 * f + (i + 1) % 3]] - V[F[3 * f + i]]).sqrMagnitude,
                        diff = diff,
                    });
                }
            }

            var splits = 0;

            // PER-EDGE CAP, and it is what keeps one bad edge from costing the whole pass.
            //
            // A midpoint split halves an offset, so clearing a difference of k needs ceil(log2 k)
            // splits of that edge and the largest k ever measured on this corpus is 3. Four is
            // therefore slack for every edge that is going to converge, and a hard stop for one
            // that is not: a fold makes the split arithmetic hand the new interior spoke an offset
            // as long as the one it removed, so the pair trade the defect back and forth forever.
            // Measured on the head scan, 72,962 of 75,000 splits were spent on folded faces and
            // the pass then rolled back entirely, which is how a 1.5M-triangle asset came out with
            // 4,134 boundary edges. Refusing the fifth split of one edge costs that edge - it stays
            // long, and its cell does not tile - and lets every other edge finish.
            const int maxSplitsPerEdge = 4;
            var edgeSplits = new Dictionary<int, int>();
            var faceSplits = new Dictionary<int, int>();
            // A split at most halves an offset, so a converging refinement never needs anything
            // like this many. Blowing through it means a face is being re-split forever, and the
            // trace below is what says which.
            //
            // Sized from what a converging refinement actually costs, with a wide margin: the torus
            // splits 0.016 of its edge count and the icosphere 0.014, while both runaway cases pass
            // 4x on their FIRST pass. Half the edge count sits thirty times above the real work and
            // still catches a runaway long before it can allocate its way out of memory.
            var maxSplits = Mathf.Max(1000, Mathf.RoundToInt(E2E.Count * splitBudgetPerEdge));
            var trace = new List<string>();

            /*
                 /   v0  \                  /   v0  \
                v1p 1 | 0 v0p     ->       /  1 | 0  \
                 \   v1  /               v1p - vn - v0p
                                          \  2 | 3  /
                                           \   v1  /
                f0: vn, v0p, v0     f1: vn, v0, v1p     f2: vn, v1p, v1     f3: vn, v1, v0p
            */
            while (queue.count > 0)
            {
                var link = queue.Pop();
                if (splits >= maxSplits)
                {
                    stats.subdivideAbandoned = true;
                    stats.subdivideTrace = string.Join("\n", trace);

                    // What is still too long when the budget runs out. Compared against the count
                    // at entry this is the whole diagnosis: falling means the pass was converging
                    // and the budget was simply too small, flat or rising means it is cycling.
                    for (var e = 0; e < edgeCount; e++)
                    {
                        var d = edgeDiff[e];
                        if (Mathf.Abs(d.x) > maxLen || Mathf.Abs(d.y) > maxLen)
                            stats.subdivideStillLong++;
                    }

                    // Keeping the partial subdivision leaves a valid, if unfinished, map; rolling
                    // back leaves every over-length edge in place, and an over-length edge can
                    // never pair into a quad, so it becomes a hole. Measured below.  
                    if (!rollbackOnBudget) { stats.subdivideAbandoned = true; RebuildAllowChanges(); return splits; }
                    Restore(snapshot);
                    return 0;
                }
                var e0 = link.id;
                var e1 = E2E[e0];
                var isBoundary = e1 == -1;
                var f0 = e0 / 3;
                var f1 = isBoundary ? -1 : e1 / 3;
                var v0 = F[e0];
                var v0p = F[3 * f0 + (e0 + 2) % 3];
                var v1 = F[3 * f0 + (e0 + 1) % 3];

                // Stale entry: the face was rewritten by an earlier split.
                if ((V[v0] - V[v1]).sqrMagnitude != link.length) continue;

                // REFRESH BOTH FACES BEFORE READING THEM, and this is the whole reason the first
                // version ran away. `diffs` caches each half-edge's offset in its face's frame, but
                // AnalyzeOrient writes the stored per-edge offset for all three slots of a face it
                // rewrites - including the two shared with faces the split never touched. Those
                // faces keep a cached value the stored offset no longer implies, and once f0's
                // cache disagrees with the edge it owns, the split computes a midpoint from stale
                // arithmetic, leaves the edge as long as it found it, and reschedules it forever.
                // Measured before this: a four-split cycle on one face of the capped cylinder that
                // reached 700,000 splits. `edgeDiff` plus `feOrient` is the only authority; the
                // cache is derived from it here, so it cannot drift.
                Refresh(f0);
                if (!isBoundary) Refresh(f1);

                if (Mathf.Abs(diffs[e0].x) < 2 && Mathf.Abs(diffs[e0].y) < 2) continue;

                // A FOLD, NOT A LONG EDGE. Subdivision exists to give an edge that spans several
                // cells the vertices it needs. After input refinement no honest edge is longer than
                // half a cell, and a legitimate split halves its offset, so even an offset of three
                // bottoms out around an eighth of a cell. An edge far shorter than that which still
                // claims to span two cells is not geometry that needs more vertices -- it is the map
                // folded over itself, and halving it hands both halves the same contradiction.
                // Measured on the head through the window's path: the runaway re-split edges of
                // 1/50 of a cell until the budget ran out, the whole pass was rolled back, and the
                // output came out in 25 pieces with 1,410 open edges. Refusing these costs the fold
                // its own few cells and lets every other edge finish.
                var spacing = Mathf.Min(ScaleAt(v0), ScaleAt(v1));
                if (spacing > 0f && Mathf.Sqrt(link.length) < spacing * k_MinSplitCells)
                {
                    stats.subdivideFoldRefused++;
                    continue;
                }

                // PER-FACE CAP, which is the cap the per-edge one below was meant to be. A split edge
                // does not keep its id through the port's face rewrite, so the per-edge counter never
                // sees the same key twice; a face does keep its id, as f0 of a split and as f1. The
                // cycle measured on the head through the window's path was four splits long, across
                // faces 12776 and 76344, and each lap put its new vertices exactly on top of the last
                // lap's -- edge lengths identical to nine digits, so nothing ever shrank -- until the
                // budget ran out 286,000 splits later and the whole pass was rolled back. A face that
                // is converging takes part in a handful of splits: three edges, offsets of at most
                // three. Past this many it is cycling, and refusing it costs that face alone.
                faceSplits.TryGetValue(f0, out var f0Splits);
                var f1Splits = 0;
                if (!isBoundary) faceSplits.TryGetValue(f1, out f1Splits);
                if (f0Splits >= k_MaxSplitsPerFace || f1Splits >= k_MaxSplitsPerFace)
                {
                    stats.subdivideFaceCycleRefused++;
                    continue;
                }
                faceSplits[f0] = f0Splits + 1;
                if (!isBoundary) faceSplits[f1] = f1Splits + 1;

                var splitKey = feId[e0];
                edgeSplits.TryGetValue(splitKey, out var timesSplit);
                if (timesSplit >= maxSplitsPerEdge)
                {
                    stats.subdivideEdgeCapped++;
                    continue;
                }
                edgeSplits[splitKey] = timesSplit + 1;

                // RECORDED, NOT SKIPPED. Refusing to split a folded face was how the runaway above
                // was first contained, and with the cache fixed it is no longer needed - the
                // reference splits them too. The count is kept because it measures how far folds
                // and unrepresentable edges travel together, which is the correlation that decides
                // whether the flip repair or the subdivision owns a bad result.
                if (Area(f0) < 0 || (!isBoundary && Area(f1) < 0)) stats.subdivideSplitOnFlipped++;

                var D01 = diffs[e0];
                var D1p = diffs[3 * f0 + (e0 + 1) % 3];
                var Dp0 = diffs[3 * f0 + (e0 + 2) % 3];
                var D0n = D01 / 2;

                var twistToTwin = 0;
                Vector2Int Ds10 = default, Ds0p = default, Dsp1 = default;
                if (!isBoundary)
                {
                    Ds10 = diffs[e1];
                    Ds0p = diffs[3 * f1 + (e1 + 1) % 3];
                    Dsp1 = diffs[3 * f1 + (e1 + 2) % 3];
                    while (twistToTwin < 4 && Rshift90(D01, twistToTwin) != Ds10) twistToTwin++;
                    if (twistToTwin == 4)
                    {
                        // The two faces disagree about this edge — an unrouted defect. Splitting
                        // it would have to invent a relation between them, so it is left alone.
                        stats.subdivideOutOfRange++;
                        continue;
                    }
                }

                if (splits >= maxSplits - 24)
                    trace.Add($"split#{splits} e0={e0} f0={f0} f1={f1} v0={v0} v1={v1} v0p={v0p} " +
                              $"D01={D01} D1p={D1p} Dp0={Dp0} Ds10={Ds10} Ds0p={Ds0p} Dsp1={Dsp1} " +
                              $"twist={twistToTwin} len={link.length:G9}");

                var f2 = -1;
                if (!isBoundary)
                {
                    f2 = faceCount;
                    AddFacePlaceholder();
                }
                var f3 = faceCount;
                AddFacePlaceholder();

                var v1p = isBoundary ? -1 : F[3 * f1 + (e1 + 2) % 3];
                var vn = V.Count;
                splits++;

                V.Add((V[v0] + V[v1]) * 0.5f);
                N.Add(N[v0]);
                Q.Add(Q[v0]);
                O.Add((O[v0] + O[v1]) * 0.5f);
                S?.Add(S[v0]);
                nonManifoldVertex.Add(false);
                boundaryVertex.Add(isBoundary);

                var eid = feId[e0];
                var sharpEid = sharp[e0];
                var eid01 = feId[3 * f0 + (e0 + 1) % 3];
                var sharp01 = sharp[3 * f0 + (e0 + 1) % 3];
                var eid02 = feId[3 * f0 + (e0 + 2) % 3];
                var sharp02 = sharp[3 * f0 + (e0 + 2) % 3];

                // The original edge keeps its id for the half nearest v0; the other half and the
                // spoke to v0p are new.
                var eid0 = eid;
                var sharpEid0 = sharpEid;
                edgeX[eid0] = Mathf.Min(v0, vn);
                edgeY[eid0] = Mathf.Max(v0, vn);

                var eid1 = AddEdge(vn, v1);
                var sharpEid1 = sharpEid;

                var eid0p = AddEdge(vn, v0p);
                const bool sharpEid0p = false;

                var o1 = e0 % 3;
                var spaces1 = spaces[f0];

                SetFace(f0, vn, v0p, v0);
                SetFaceEdges(f0, eid0p, eid02, eid0);
                SetFaceSharp(f0, sharpEid0p, sharp02, sharpEid0);
                SetFaceDiffs(f0, D01 + D1p - D0n, Dp0, D0n);
                AnalyzeOrient(f0, new Vector3Int(0, spaces1.d[(o1 + 2) % 3], spaces1.d[o1]));

                if (!isBoundary)
                {
                    var o2 = e1 % 3;
                    var spaces2 = spaces[f1];
                    var eid11 = feId[3 * f1 + (e1 + 1) % 3];
                    var sharp11 = sharp[3 * f1 + (e1 + 1) % 3];
                    var eid12 = feId[3 * f1 + (e1 + 2) % 3];
                    var sharp12 = sharp[3 * f1 + (e1 + 2) % 3];

                    var Dsn0 = Rshift90(D0n, twistToTwin);

                    var eid1p = AddEdge(vn, v1p);
                    const bool sharpEid1p = false;

                    SetFace(f1, vn, v0, v1p);
                    SetFaceEdges(f1, eid0, eid11, eid1p);
                    SetFaceSharp(f1, sharpEid0, sharp11, sharpEid1p);
                    SetFaceDiffs(f1, Dsn0, Ds0p, Dsp1 + (Ds10 - Dsn0));
                    AnalyzeOrient(f1, new Vector3Int(spaces2.d[o2], spaces2.d[(o2 + 1) % 3], 0));

                    spaces[f2] = spaces[f1];
                    SetFace(f2, vn, v1p, v1);
                    SetFaceEdges(f2, eid1p, eid12, eid1);
                    SetFaceSharp(f2, sharpEid1p, sharp12, sharpEid1);
                    SetFaceDiffs(f2, -Dsp1 - (Ds10 - Dsn0), Dsp1, Ds10 - Dsn0);
                    AnalyzeOrient(f2, new Vector3Int(0, spaces2.d[(o2 + 2) % 3], spaces2.d[o2]));
                }

                spaces[f3] = spaces[f0];
                SetFace(f3, vn, v1, v0p);
                SetFaceEdges(f3, eid1, eid01, eid0p);
                SetFaceSharp(f3, sharpEid1, sharp01, sharpEid0p);
                SetFaceDiffs(f3, D01 - D0n, D1p, D0n - (D01 + D1p));
                AnalyzeOrient(f3, new Vector3Int(spaces1.d[o1], spaces1.d[(o1 + 1) % 3], 0));

                FixOrient(f0);
                if (!isBoundary)
                {
                    FixOrient(f1);
                    FixOrient(f2);
                }
                FixOrient(f3);

                // Twins, read before anything below rewrites them.
                var e0p = E2E[Prev3(e0)];
                var e0n = E2E[Next3(e0)];

                SetTwin(3 * f0 + 0, 3 * f3 + 2);
                SetTwin(3 * f0 + 1, e0p);
                SetTwin(3 * f3 + 1, e0n);
                if (isBoundary)
                {
                    SetTwin(3 * f0 + 2, -1);
                    SetTwin(3 * f3 + 0, -1);
                }
                else
                {
                    var e1p = E2E[Prev3(e1)];
                    var e1n = E2E[Next3(e1)];
                    SetTwin(3 * f0 + 2, 3 * f1 + 0);
                    SetTwin(3 * f1 + 1, e1n);
                    SetTwin(3 * f1 + 2, 3 * f2 + 0);
                    SetTwin(3 * f2 + 1, e1p);
                    SetTwin(3 * f2 + 2, 3 * f3 + 0);
                }

                Schedule(f0);
                if (!isBoundary)
                {
                    Schedule(f2);
                    Schedule(f1);
                }
                Schedule(f3);
            }

            for (var e = 0; e < edgeCount; e++)
            {
                var d = edgeDiff[e];
                if (Mathf.Abs(d.x) > maxLen || Mathf.Abs(d.y) > maxLen) stats.subdivideOutOfRange++;
            }

            RebuildAllowChanges();
            return splits;
        }
    }
}
