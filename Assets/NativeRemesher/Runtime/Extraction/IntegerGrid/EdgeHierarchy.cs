// The coarse view of an integer grid map: the triangle graph with every zero-offset edge contracted.
//
// Port of the edge-graph half of QuadriFlow's Hierarchy (DownsampleEdgeGraph, PropagateEdge,
// FixFlip). Contracting zero-offset edges collapses each lattice cell's worth of input vertices to
// one node, so what is left is a triangle graph whose every edge is a unit or diagonal step of the
// grid — the thing quads are read off, and the thing a flip repair can move around one cell at a
// time. Levels are built until a level contracts nothing; the top level is the graph extraction
// uses, and the per-level maps carry any change back down to the fine offsets.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class EdgeHierarchy
    {
        /// <summary>Per level: three rotations per face.</summary>
        public readonly List<List<int>> FQ = new List<List<int>>();
        /// <summary>Per level: three undirected edge ids per face.</summary>
        public readonly List<List<int>> F2E = new List<List<int>>();
        /// <summary>Per level: the one or two faces of each edge.</summary>
        public readonly List<List<Vector2Int>> E2F = new List<List<Vector2Int>>();
        public readonly List<List<Vector2Int>> EdgeDiff = new List<List<Vector2Int>>();
        public readonly List<List<byte>> Allow = new List<List<byte>>();
        /// <summary>Per level: faces whose offsets do not close. Never contracted.</summary>
        public readonly List<List<int>> Sing = new List<List<int>>();

        public readonly List<List<int>> ToUpperEdges = new List<List<int>>();
        public readonly List<List<int>> ToUpperOrients = new List<List<int>>();
        public readonly List<List<int>> ToUpperFaces = new List<List<int>>();

        public int levels => F2E.Count;
        public int top => F2E.Count - 1;

        /// <summary>Faces at a level whose contracted view was cut short by a malformed walk.</summary>
        public int walkFailures;

        static Vector2Int Rshift90(Vector2Int s, int amount) => IntegerGridMap.Rshift90(s, amount);
        static int Mod4(int a) => IntegerGridMap.Mod4(a);

        static Vector2Int FaceResidual(List<int> fq, List<int> f2e, List<Vector2Int> diff, int f)
        {
            var d = Vector2Int.zero;
            for (var j = 0; j < 3; j++)
                d += Rshift90(diff[f2e[3 * f + j]], fq[3 * f + j]);
            return d;
        }

        /// <summary>
        /// Builds the hierarchy from copies of the level-0 arrays. <paramref name="maxLevels"/> of
        /// -1 builds until nothing contracts; 1 keeps only the input level.
        /// </summary>
        public static EdgeHierarchy Build(List<int> fq, List<int> f2e, List<Vector2Int> edgeDiff,
            List<byte> allow, int maxLevels)
        {
            var h = new EdgeHierarchy();
            var edges = edgeDiff.Count;
            var faces = f2e.Count / 3;

            var e2f = new List<Vector2Int>(edges);
            for (var e = 0; e < edges; e++) e2f.Add(new Vector2Int(-1, -1));
            for (var i = 0; i < faces; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var e = f2e[3 * i + j];
                    var pair = e2f[e];
                    if (pair.x == -1) pair.x = i; else pair.y = i;
                    e2f[e] = pair;
                }
            }

            var sing0 = new List<int>();
            for (var i = 0; i < faces; i++)
                if (FaceResidual(fq, f2e, edgeDiff, i) != Vector2Int.zero) sing0.Add(i);

            h.FQ.Add(new List<int>(fq));
            h.F2E.Add(new List<int>(f2e));
            h.E2F.Add(e2f);
            h.EdgeDiff.Add(new List<Vector2Int>(edgeDiff));
            h.Allow.Add(new List<byte>(allow));
            h.Sing.Add(sing0);

            var levels = maxLevels == -1 ? 100 : maxLevels;

            for (var l = 0; l < levels - 1; l++)
            {
                var FQl = h.FQ[l];
                var E2Fl = h.E2F[l];
                var F2El = h.F2E[l];
                var Allowl = h.Allow[l];
                var EdgeDiffl = h.EdgeDiff[l];
                var Singl = h.Sing[l];

                var faceCountL = F2El.Count / 3;
                var edgeCountL = E2Fl.Count;

                // 0 free, 1 adjacent to a contraction (kept, but not contractible this level),
                // 2 removed by a contraction.
                var fixedFaces = new int[faceCountL];
                foreach (var s in Singl) fixedFaces[s] = 1;

                var toUpper = new List<int>(edgeCountL);
                var toUpperOrients = new List<int>(edgeCountL);
                for (var e = 0; e < edgeCountL; e++)
                {
                    toUpper.Add(-1);
                    toUpperOrients.Add(0);
                }

                var nFQ = new List<int>();
                var nE2F = new List<Vector2Int>();
                var nF2E = new List<int>();
                var nSing = new List<int>();

                // Choose an independent set of zero-offset edges to contract.
                for (var i = 0; i < edgeCountL; i++)
                {
                    if (EdgeDiffl[i] != Vector2Int.zero) continue;
                    var pair = E2Fl[i];
                    if ((pair.x >= 0 && fixedFaces[pair.x] != 0) ||
                        (pair.y >= 0 && fixedFaces[pair.y] != 0))
                        continue;

                    for (var j = 0; j < 2; j++)
                    {
                        var f = pair[j];
                        if (f < 0) continue;
                        for (var k = 0; k < 3; k++)
                        {
                            var neighbourEdge = F2El[3 * f + k];
                            var np = E2Fl[neighbourEdge];
                            for (var m = 0; m < 2; m++)
                            {
                                var nf = np[m];
                                if (nf < 0) continue;
                                if (fixedFaces[nf] == 0) fixedFaces[nf] = 1;
                            }
                        }
                    }

                    if (pair.x >= 0) fixedFaces[pair.x] = 2;
                    if (pair.y >= 0) fixedFaces[pair.y] = 2;
                    toUpper[i] = -2;
                }

                for (var i = 0; i < edgeCountL; i++)
                {
                    if (toUpper[i] == -2) continue;
                    var pair = E2Fl[i];
                    if ((pair.x < 0 || fixedFaces[pair.x] == 2) && (pair.y < 0 || fixedFaces[pair.y] == 2))
                        toUpper[i] = -3;
                }

                var numE = 0;
                var paths = new List<(int edge, int orient)>();
                for (var i = 0; i < edgeCountL; i++)
                {
                    if (toUpper[i] != -1) continue;
                    var pair = E2Fl[i];

                    if ((pair.x < 0 || fixedFaces[pair.x] < 2) && (pair.y < 0 || fixedFaces[pair.y] < 2))
                    {
                        nE2F.Add(pair);
                        toUpperOrients[i] = 0;
                        toUpper[i] = numE++;
                        continue;
                    }

                    // One side of this edge is a removed face: walk through the removed faces to
                    // the kept face on the far side, and the whole path becomes one coarse edge.
                    var f0 = (pair.y < 0 || fixedFaces[pair.x] < 2) ? pair.x : pair.y;
                    var e = i;
                    var f = f0;
                    paths.Clear();
                    paths.Add((i, 0));
                    var guard = 0;

                    while (true)
                    {
                        var ef = E2Fl[e];
                        if (ef.x == f) f = ef.y;
                        else if (ef.y == f) f = ef.x;

                        if (f < 0 || fixedFaces[f] < 2 || guard++ > edgeCountL)
                        {
                            if (guard > edgeCountL) h.walkFailures++;
                            for (var j = 0; j < paths.Count; j++)
                            {
                                var p = paths[j];
                                toUpper[p.edge] = numE;
                                var orient = p.orient;
                                if (j > 0) orient = Mod4(orient + toUpperOrients[paths[j - 1].edge]);
                                toUpperOrients[p.edge] = orient;
                            }
                            nE2F.Add(new Vector2Int(f0, f));
                            numE++;
                            break;
                        }

                        var ind0 = -1;
                        var ind1 = -1;
                        for (var j = 0; j < 3; j++)
                        {
                            if (F2El[3 * f + j] != e) continue;
                            ind0 = j;
                            break;
                        }
                        for (var j = 0; j < 3; j++)
                        {
                            var e1 = F2El[3 * f + j];
                            if (e1 == e || toUpper[e1] == -2) continue;
                            e = e1;
                            ind1 = j;
                            break;
                        }

                        if (ind1 != -1)
                        {
                            paths.Add((e, Mod4(FQl[3 * f + ind1] - FQl[3 * f + ind0] + 6)));
                        }
                        else
                        {
                            if (EdgeDiffl[e] != Vector2Int.zero) h.walkFailures++;
                            foreach (var p in paths)
                            {
                                toUpper[p.edge] = numE;
                                toUpperOrients[p.edge] = 0;
                            }
                            numE++;
                            nE2F.Add(new Vector2Int(f0, f0));
                            break;
                        }
                    }
                }

                var nEdgeDiff = new List<Vector2Int>(numE);
                var nAllow = new List<byte>(numE * 2);
                for (var e = 0; e < numE; e++)
                {
                    nEdgeDiff.Add(Vector2Int.zero);
                    nAllow.Add(1);
                    nAllow.Add(1);
                }

                for (var i = 0; i < edgeCountL; i++)
                {
                    if (toUpper[i] < 0) continue;
                    if (toUpperOrients[i] == 0) nEdgeDiff[toUpper[i]] = EdgeDiffl[i];

                    var dimension = toUpperOrients[i] % 2;
                    if (Allowl[2 * i + dimension] == 0) nAllow[2 * toUpper[i]] = 0;
                    if (Allowl[2 * i + 1 - dimension] == 0) nAllow[2 * toUpper[i] + 1] = 0;
                }

                var upperFace = new List<int>(faceCountL);
                for (var i = 0; i < faceCountL; i++) upperFace.Add(-1);

                for (var i = 0; i < faceCountL; i++)
                {
                    var e0 = toUpper[F2El[3 * i]];
                    var e1 = toUpper[F2El[3 * i + 1]];
                    var e2 = toUpper[F2El[3 * i + 2]];
                    if (e0 < 0 || e1 < 0 || e2 < 0) continue;

                    upperFace[i] = nF2E.Count / 3;
                    nF2E.Add(e0); nF2E.Add(e1); nF2E.Add(e2);
                    for (var j = 0; j < 3; j++)
                        nFQ.Add(Mod4(FQl[3 * i + j] + 4 - toUpperOrients[F2El[3 * i + j]]));
                }

                for (var i = 0; i < nE2F.Count; i++)
                {
                    var pair = nE2F[i];
                    if (pair.x >= 0) pair.x = upperFace[pair.x];
                    if (pair.y >= 0) pair.y = upperFace[pair.y];
                    nE2F[i] = pair;
                }

                foreach (var s in Singl)
                    if (upperFace[s] >= 0) nSing.Add(upperFace[s]);

                // Nothing contracted: this level is the top, and the one just built is discarded.
                if (nEdgeDiff.Count == EdgeDiffl.Count) break;

                h.ToUpperEdges.Add(toUpper);
                h.ToUpperOrients.Add(toUpperOrients);
                h.ToUpperFaces.Add(upperFace);
                h.FQ.Add(nFQ);
                h.F2E.Add(nF2E);
                h.E2F.Add(nE2F);
                h.EdgeDiff.Add(nEdgeDiff);
                h.Allow.Add(nAllow);
                h.Sing.Add(nSing);
            }

            return h;
        }

        /// <summary>Pushes the top level's offsets and rotations back down to level 0.</summary>
        public void PropagateEdge()
        {
            for (var level = ToUpperEdges.Count; level > 0; level--)
                PropagateLevel(level);
        }

        /// <summary>Pushes one level's offsets and rotations down to the level below it.</summary>
        public void PropagateLevel(int level)
        {
            {
                var edgeDiff = EdgeDiff[level];
                var nEdgeDiff = EdgeDiff[level - 1];
                var fq = FQ[level];
                var nFQ = FQ[level - 1];
                var f2e = F2E[level - 1];
                var toUpper = ToUpperEdges[level - 1];
                var toUpperFace = ToUpperFaces[level - 1];
                var toUpperOrients = ToUpperOrients[level - 1];

                for (var i = 0; i < toUpper.Count; i++)
                {
                    if (toUpper[i] >= 0)
                        nEdgeDiff[i] = Rshift90(edgeDiff[toUpper[i]], Mod4(4 - toUpperOrients[i]));
                    else
                        nEdgeDiff[i] = Vector2Int.zero;
                }

                for (var i = 0; i < toUpperFace.Count; i++)
                {
                    var upper = toUpperFace[i];
                    if (upper == -1) continue;
                    for (var j = 0; j < 3; j++)
                        nFQ[3 * i + j] = Mod4(fq[3 * upper + j] + toUpperOrients[f2e[3 * i + j]]);
                }
            }
        }

        /// <summary>Copies level 0 back into the caller's arrays.</summary>
        public void UpdateGraphValue(List<int> fq, List<int> f2e, List<Vector2Int> edgeDiff)
        {
            fq.Clear(); fq.AddRange(FQ[0]);
            f2e.Clear(); f2e.AddRange(F2E[0]);
            edgeDiff.Clear(); edgeDiff.AddRange(EdgeDiff[0]);
        }

        /// <summary>Half-edge twins of a level's triangle graph, three per face.</summary>
        public int[] BuildE2E(int level)
        {
            var f2e = F2E[level];
            var e2f = E2F[level];
            var e2e = new int[f2e.Count];
            for (var i = 0; i < e2e.Length; i++) e2e[i] = -1;

            for (var i = 0; i < e2f.Count; i++)
            {
                var v1 = e2f[i].x;
                var v2 = e2f[i].y;
                var t1 = 0;
                var t2 = 2;
                if (v1 != -1) while (t1 < 2 && f2e[3 * v1 + t1] != i) t1++;
                if (v2 != -1) while (t2 > 0 && f2e[3 * v2 + t2] != i) t2--;
                t1 += v1 * 3;
                t2 += v2 * 3;
                if (v1 != -1) e2e[t1] = v2 == -1 ? -1 : t2;
                if (v2 != -1) e2e[t2] = v1 == -1 ? -1 : t1;
            }

            return e2e;
        }

        /// <summary>Signed parametric area of a top-level face, in half cells. Negative is a fold.</summary>
        public int Area(int level, int f)
        {
            var fq = FQ[level];
            var f2e = F2E[level];
            var diff = EdgeDiff[level];
            var d1 = Rshift90(diff[f2e[3 * f]], fq[3 * f]);
            var d2 = Rshift90(diff[f2e[3 * f + 1]], fq[3 * f + 1]);
            return d1.x * d2.y - d1.y * d2.x;
        }

        public int CountFlipped(int level)
        {
            var count = 0;
            var faces = F2E[level].Count / 3;
            for (var f = 0; f < faces; f++)
                if (Area(level, f) < 0) count++;
            return count;
        }

        /// <summary>
        /// One pass of the greedy flip repair on the top level: for each folded face, try to move
        /// one of its corners onto a neighbouring cell, keeping the move if it folds fewer faces
        /// around that corner. Returns whether anything moved.
        /// </summary>
        public bool FixFlipPass()
        {
            var l = top;
            var f2e = F2E[l];
            var e2f = E2F[l];
            var fq = FQ[l];
            var edgeDiff = EdgeDiff[l];
            var allow = Allow[l];
            var e2e = BuildE2E(l);
            var faces = f2e.Count / 3;

            var correspondingFaces = new List<int>();
            var correspondingEdges = new List<int>();
            var correspondingDiff = new List<Vector2Int>();
            var newValues = new Dictionary<int, Vector2Int>();

            // One move per corner, the reference's: collapse it onto its neighbour along the edge
            // the fan starts at. Offering the four unit steps as well was tried and does not earn
            // its place — cylinder_capped improves (220 -> 147 irregular) and icosphere_80k, which
            // is the gate case, gets worse (55 -> 76, and 62 -> 134 boundary edges). A greedy
            // search with more moves available reaches a different local minimum, not a better one.
            bool CheckShrink(int deid, int allowedEdgeLength)
            {
                if (deid == -1) return false;

                correspondingFaces.Clear();
                correspondingEdges.Clear();
                correspondingDiff.Clear();

                // Rotate to the start of the fan: back round to where we began, or to a boundary.
                var deid0 = deid;
                var guard = 0;
                while (deid != -1 && guard++ < e2e.Length)
                {
                    deid = IntegerGridMap.Prev3(deid);
                    if (e2e[deid] == -1) break;
                    deid = e2e[deid];
                    if (deid == deid0) break;
                }
                if (deid == -1) return false;

                var diff = edgeDiff[f2e[deid]];
                guard = 0;
                do
                {
                    correspondingDiff.Add(diff);
                    correspondingEdges.Add(deid);
                    correspondingFaces.Add(deid / 3);

                    deid = e2e[deid];
                    if (deid == -1) return false;
                    diff = -Rshift90(diff, fq[deid]);
                    deid = IntegerGridMap.Next3(deid);
                    diff = Rshift90(diff, Mod4(4 - fq[deid]));
                } while (deid != correspondingEdges[0] && guard++ < e2e.Length);

                if (deid != correspondingEdges[0]) return false;
                if (diff != correspondingDiff[0]) return false;

                newValues.Clear();
                for (var i = 0; i < correspondingDiff.Count; i++)
                    newValues[f2e[correspondingEdges[i]]] = edgeDiff[f2e[correspondingEdges[i]]];

                for (var i = 0; i < correspondingDiff.Count; i++)
                {
                    var eid = f2e[correspondingEdges[i]];
                    var change = correspondingDiff[i];
                    for (var j = 0; j < 2; j++)
                        if (change[j] != 0 && allow[2 * eid + j] == 0) return false;

                    var res = newValues[eid] - change;
                    newValues[eid] = res;
                    if (Mathf.Abs(res.x) > allowedEdgeLength || Mathf.Abs(res.y) > allowedEdgeLength)
                        return false;
                    if ((Mathf.Abs(res.x) > 1 && res.y != 0) || (Mathf.Abs(res.y) > 1 && res.x != 0))
                        return false;
                }

                var before = 0;
                foreach (var f in correspondingFaces)
                    if (Area(l, f) < 0) before++;

                var saved = new List<(int eid, Vector2Int old)>(newValues.Count);
                foreach (var pair in newValues)
                {
                    saved.Add((pair.Key, edgeDiff[pair.Key]));
                    edgeDiff[pair.Key] = pair.Value;
                }

                var after = 0;
                foreach (var f in correspondingFaces)
                    if (Area(l, f) < 0) after++;

                if (after < before) return true;

                foreach (var (eid, old) in saved) edgeDiff[eid] = old;
                return false;
            }

            var flipped = new Queue<int>();
            for (var f = 0; f < faces; f++)
                if (Area(l, f) < 0) flipped.Enqueue(f);

            var update = false;
            var maxLen = 1;
            while (!update && maxLen <= 2)
            {
                var pending = new Queue<int>(flipped);
                while (pending.Count > 0)
                {
                    var f = pending.Dequeue();
                    if (Area(l, f) >= 0) continue;
                    for (var i = 0; i < 3; i++)
                    {
                        if (CheckShrink(3 * f + i, maxLen) || CheckShrink(e2e[3 * f + i], maxLen))
                        {
                            update = true;
                            break;
                        }
                    }
                }
                maxLen++;
            }

            return update;
        }
    }

    public sealed partial class IntegerGridMap
    {
        /// <summary>
        /// Removes parametrically folded faces by moving cells, greedily, until a pass changes
        /// nothing. Port of QuadriFlow's FixFlipHierarchy without the SAT fallback.
        /// </summary>
        public void FixFlips(int maxPasses = 64)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var passes = 0;

            var h = EdgeHierarchy.Build(feOrient, feId, edgeDiff, allow, -1);
            if (stats.flipPasses == 0) stats.flippedBefore = h.CountFlipped(h.top);
            stats.coarseLevels = h.levels;

            while (passes < maxPasses)
            {
                passes++;
                var moved = h.FixFlipPass();
                h.PropagateEdge();
                h.UpdateGraphValue(feOrient, feId, edgeDiff);
                if (!moved) break;

                // Moves open new zero-offset edges, so the coarse graph is rebuilt for the next pass.
                h = EdgeHierarchy.Build(feOrient, feId, edgeDiff, allow, -1);
            }

            stats.flippedAfter = h.CountFlipped(h.top);
            stats.flipPasses += passes;
            stats.flipMs += watch.ElapsedMilliseconds;
        }
    }
}
