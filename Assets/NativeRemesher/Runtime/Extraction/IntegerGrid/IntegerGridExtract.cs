// Reading the quads off a closed integer grid map.
//
// Port of QuadriFlow's AdvancedExtractQuad / BuildTriangleManifold, FixHoles and FixValence. Once
// every face's offsets close and no offset exceeds one cell, the surviving (non-degenerate) faces
// of the contracted graph are exactly half-cells: two unit steps and one diagonal. A quad is two of
// them glued along the diagonal, so the whole extraction is "pair each diagonal edge's two faces".
// A cell is a face by construction, and a vertex has four neighbours unless the map is singular
// there — which is the property the greedy pairwise collapse never had and could not be given.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class IntegerGridResult
    {
        public Vector3[] positions;
        public Vector3[] normals;
        public Vector3[] directions;
        /// <summary>Input vertices that collapsed into each output vertex.</summary>
        public List<int>[] members;
        public List<int[]> faces;
    }

    public sealed partial class IntegerGridMap
    {
        /// <summary>A quad mesh with half-edge twins, four half-edges per face.</summary>
        sealed class QuadMesh
        {
            public List<int[]> faces;
            public int vertexCount;
            public int[] v2e;
            public int[] e2e;
            public bool[] nonManifold;
            public bool[] boundary;

            public static int Next4(int e) => e / 4 * 4 + (e + 1) % 4;
            public static int Prev4(int e) => e % 4 == 0 ? e + 3 : e - 1;

            public void Build()
            {
                v2e = new int[vertexCount];
                for (var i = 0; i < vertexCount; i++) v2e[i] = -1;

                var count = faces.Count * 4;
                var chainTarget = new int[count];
                var chainNext = new int[count];
                for (var i = 0; i < count; i++) chainNext[i] = -1;

                for (var f = 0; f < faces.Count; f++)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        var cur = faces[f][i];
                        var next = faces[f][(i + 1) % 4];
                        var edge = 4 * f + i;
                        if (cur == next) continue;

                        chainTarget[edge] = next;
                        if (v2e[cur] == -1)
                        {
                            v2e[cur] = edge;
                        }
                        else
                        {
                            var idx = v2e[cur];
                            while (chainNext[idx] != -1) idx = chainNext[idx];
                            chainNext[idx] = edge;
                        }
                    }
                }

                nonManifold = new bool[vertexCount];
                e2e = new int[count];
                for (var i = 0; i < count; i++) e2e[i] = -1;

                for (var f = 0; f < faces.Count; f++)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        var cur = faces[f][i];
                        var next = faces[f][(i + 1) % 4];
                        var edge = 4 * f + i;
                        if (cur == next) continue;

                        var it = v2e[next];
                        var opposite = -1;
                        while (it != -1)
                        {
                            if (chainTarget[it] == cur)
                            {
                                if (opposite == -1)
                                {
                                    opposite = it;
                                }
                                else
                                {
                                    nonManifold[cur] = true;
                                    nonManifold[next] = true;
                                    opposite = -1;
                                    break;
                                }
                            }
                            it = chainNext[it];
                        }

                        if (opposite != -1 && edge < opposite)
                        {
                            e2e[edge] = opposite;
                            e2e[opposite] = edge;
                        }
                    }
                }

                boundary = new bool[vertexCount];
                for (var i = 0; i < vertexCount; i++)
                {
                    var edge = v2e[i];
                    if (edge == -1) continue;
                    if (nonManifold[i])
                    {
                        v2e[i] = -1;
                        continue;
                    }

                    var start = edge;
                    var first = int.MaxValue;
                    var guard = 0;
                    do
                    {
                        first = Mathf.Min(first, edge);
                        var prevEdge = e2e[Prev4(edge)];
                        if (prevEdge == -1)
                        {
                            first = edge;
                            boundary[i] = true;
                            break;
                        }
                        edge = prevEdge;
                    } while (edge != start && guard++ < count);
                    v2e[i] = first;
                }
            }
        }

        /// <summary>
        /// Contracts the map to its cells, splits pinched vertices, pairs half-cells into quads,
        /// tiles small holes and repairs valence. The map must have been flowed and subdivided.
        /// </summary>
        public IntegerGridResult Extract(FeatureGraph features, int featureVertexCount)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var h = EdgeHierarchy.Build(feOrient, feId, edgeDiff, allow, -1);
            stats.coarseLevels = h.levels;

            // Every zero-offset edge glues its endpoints into one cell.
            var tree = new DisjointTree(V.Count);
            var diffs0 = h.EdgeDiff[0];
            for (var e = 0; e < diffs0.Count; e++)
                if (diffs0[e] == Vector2Int.zero) tree.Merge(edgeX[e], edgeY[e]);
            tree.BuildCompactParent();

            var t = h.top;
            var F2E = h.F2E[t];
            var E2F = h.E2F[t];
            var EdgeDiffT = h.EdgeDiff[t];
            var FQ = h.FQ[t];
            var coarseFaces = F2E.Count / 3;

            // Any fine face standing behind each coarse face, for its corner vertices.
            var face = new int[coarseFaces];
            for (var i = 0; i < coarseFaces; i++) face[i] = -1;
            for (var i = 0; i < faceCount; i++)
            {
                var x = i;
                for (var j = 0; j < h.ToUpperFaces.Count; j++)
                {
                    x = h.ToUpperFaces[j][x];
                    if (x < 0) break;
                }
                if (x >= 0) face[x] = i;
            }

            var compactCount = tree.compactCount;
            var members = new List<int>[compactCount];
            var oC = new Vector3[compactCount];
            var nC = new Vector3[compactCount];
            var qC = new Vector3[compactCount];
            var counter = new int[compactCount];
            for (var c = 0; c < compactCount; c++) members[c] = new List<int>();

            for (var i = 0; i < V.Count; i++)
            {
                var c = tree.Index(i);
                members[c].Add(i);
                oC[c] += O[i];
                nC[c] = MeshMath.SafeNormalize(nC[c] * counter[c] + N[i], N[i]);
                if (counter[c] == 0)
                {
                    qC[c] = Q[i];
                }
                else
                {
                    OrientationField.Compat4(qC[c], nC[c], Q[i], N[i], out var a, out var b);
                    qC[c] = MeshMath.SafeNormalize(a * counter[c] + b, a);
                }
                counter[c]++;
            }
            for (var c = 0; c < compactCount; c++)
                if (counter[c] > 0) oC[c] /= counter[c];

            stats.compactVertices = compactCount;

            var result = BuildTriangleManifold(h, tree, face, F2E, E2F, EdgeDiffT, FQ,
                members, oC, nC, qC);

            PlaceFeatureVertices(result, features, featureVertexCount);

            stats.outputVertices = result.positions.Length;
            stats.extractMs = watch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// A crease vertex in the output belongs on the crease, not at the mean of its cell's
        /// lattice points; a corner belongs exactly at the corner. Same rule as LatticeCollapse.
        /// </summary>
        /// <summary>How far, in lattice cells, a feature placement may move an output vertex.</summary>
        const float k_FeaturePlacementReachCells = 1f;

        /// <summary>
        /// Place a crease vertex at the cluster of its cell's feature vertices nearest the solved
        /// position, rather than at the mean of all of them. Diagnostic switch.
        /// </summary>
        public static bool placeNearestCluster;

        void PlaceFeatureVertices(IntegerGridResult result, FeatureGraph features, int featureVertexCount)
        {
            if (features == null || !placeFeatureVertices) return;

            for (var v = 0; v < result.positions.Length; v++)
            {
                var sum = Vector3.zero;
                var count = 0;
                var corner = -1;
                foreach (var m in result.members[v])
                {
                    if (m >= featureVertexCount || !features.isFeatureVertex[m]) continue;
                    sum += V[m];
                    count++;
                    if (features.featureDegree[m] >= 3) corner = m;
                }

                if (corner < 0 && count == 0) continue;
                var target = corner >= 0 ? V[corner] : sum / count;

                // Two curves through one cell -- a panel's ridge and the valley at its foot --
                // average to a point on neither. Take only the feature vertices near the one
                // closest to where the solve put this vertex.
                if (corner < 0 && placeNearestCluster && count > 1)
                {
                    var nearest = -1;
                    var best = float.PositiveInfinity;
                    foreach (var m in result.members[v])
                    {
                        if (m >= featureVertexCount || !features.isFeatureVertex[m]) continue;
                        var d = (V[m] - result.positions[v]).sqrMagnitude;
                        if (d < best) { best = d; nearest = m; }
                    }
                    var clusterSum = Vector3.zero;
                    var clusterCount = 0;
                    var radius = 0.35f * scale;
                    foreach (var m in result.members[v])
                    {
                        if (m >= featureVertexCount || !features.isFeatureVertex[m]) continue;
                        if ((V[m] - V[nearest]).sqrMagnitude > radius * radius) continue;
                        clusterSum += V[m];
                        clusterCount++;
                    }
                    target = clusterSum / clusterCount;
                }

                // A lattice cell is everything glued together by zero-offset edges, and that can
                // reach well beyond one cell of surface -- through a fold, around a singularity.
                // Averaging the feature vertices of such a cell lands between them, and a corner
                // can sit on the far side; either way the output vertex is dragged across the
                // model and takes its quads with it. Measured on a helmeted head with creases on,
                // that is where the long edges spanning the whole mesh came from. So a placement
                // is only taken when it stays within reach of where the solve put the vertex.
                var distance = (target - result.positions[v]).magnitude;
                if (distance > stats.featurePlacementMaxMove) stats.featurePlacementMaxMove = distance;
                if (distance > k_FeaturePlacementReachCells * scale)
                {
                    stats.featurePlacementsRefused++;
                    continue;
                }

                stats.featurePlacements++;
                result.positions[v] = target;
            }
        }

        IntegerGridResult BuildTriangleManifold(EdgeHierarchy h, DisjointTree tree, int[] face,
            List<int> F2E, List<Vector2Int> E2F, List<Vector2Int> EdgeDiffT, List<int> FQ,
            List<int>[] Vset, Vector3[] oC, Vector3[] nC, Vector3[] qC)
        {
            var e2e = h.BuildE2E(h.top);
            var coarseFaces = F2E.Count / 3;
            var limit = e2e.Length + 1;

            var tv = new int[coarseFaces * 3];
            for (var i = 0; i < tv.Length; i++) tv[i] = -1;

            var numV = 0;
            var Nl = new List<Vector3>();
            var Ql = new List<Vector3>();
            var Ol = new List<Vector3>();
            var Vs = new List<List<int>>();

            // One output vertex per fan of coarse faces around a cell.
            for (var i = 0; i < coarseFaces; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    if (tv[3 * i + j] != -1) continue;
                    var f = face[i];
                    var v = tree.Index(F[3 * f + j]);
                    Vs.Add(Vset[v]);
                    Ql.Add(qC[v]);
                    Nl.Add(nC[v]);
                    Ol.Add(oC[v]);

                    var deid0 = 3 * i + j;
                    var deid = deid0;
                    var guard = 0;
                    do
                    {
                        tv[deid] = numV;
                        deid = e2e[Prev3(deid)];
                    } while (deid != deid0 && deid != -1 && guard++ < limit);

                    if (deid == -1)
                    {
                        deid = deid0;
                        guard = 0;
                        do
                        {
                            deid = e2e[deid];
                            if (deid == -1) break;
                            deid = Next3(deid);
                            tv[deid] = numV;
                        } while (deid != -1 && guard++ < limit);
                    }

                    numV++;
                }
            }

            // Split any output vertex whose fan is pinched — the same directed edge appearing
            // twice around it — into one vertex per sheet, until none are.
            int numV0;
            do
            {
                numV0 = numV;
                var vertToDedge = new List<int>[numV];
                for (var i = 0; i < coarseFaces; i++)
                {
                    int p0 = tv[3 * i], p1 = tv[3 * i + 1], p2 = tv[3 * i + 2];
                    if (p0 == p1 || p1 == p2 || p2 == p0)
                    {
                        for (var j = 0; j < 3; j++)
                        {
                            var tw = e2e[3 * i + j];
                            if (tw != -1) e2e[tw] = -1;
                        }
                        for (var j = 0; j < 3; j++) e2e[3 * i + j] = -1;
                    }
                    else
                    {
                        for (var j = 0; j < 3; j++)
                            (vertToDedge[tv[3 * i + j]] ??= new List<int>()).Add(3 * i + j);
                    }
                }

                var visited = new bool[coarseFaces * 3];
                var ring = new List<int>();
                var loc = new Dictionary<long, int>(EdgeKey.Comparer);

                for (var i = 0; i < vertToDedge.Length; i++)
                {
                    var dedgesOf = vertToDedge[i];
                    if (dedgesOf == null) continue;
                    var numColour = 0;

                    for (var j = 0; j < dedgesOf.Count; j++)
                    {
                        var deid = dedgesOf[j];
                        if (visited[deid]) continue;

                        ring.Clear();
                        var deid0 = deid;
                        var guard = 0;
                        do
                        {
                            ring.Add(deid);
                            deid = e2e[Prev3(deid)];
                        } while (deid != -1 && deid != deid0 && guard++ < limit);

                        if (deid == -1)
                        {
                            deid = deid0;
                            guard = 0;
                            while (guard++ < limit)
                            {
                                deid = e2e[deid];
                                if (deid == -1) break;
                                deid = Next3(deid);
                                if (deid == deid0) break;
                                ring.Insert(0, deid);
                            }
                        }

                        loc.Clear();
                        var colours = new int[ring.Count];
                        for (var k = 0; k < colours.Length; k++) colours[k] = numColour;
                        numColour++;

                        for (var jj = 0; jj < ring.Count; jj++)
                        {
                            var d = ring[jj];
                            visited[d] = true;
                            var key = EdgeKey.Pack(tv[d], tv[Next3(d)]);
                            if (loc.TryGetValue(key, out var s))
                            {
                                for (var k = s; k < jj; k++)
                                {
                                    var d1 = ring[k];
                                    loc.Remove(EdgeKey.Pack(tv[d1], tv[Next3(d1)]));
                                    colours[k] = numColour;
                                }
                                numColour++;
                            }
                            loc[key] = jj;
                        }

                        for (var k = 0; k < ring.Count; k++)
                            if (colours[k] > 0) tv[ring[k]] = numV + colours[k] - 1;
                    }

                    if (numColour > 1)
                    {
                        for (var j = 0; j < numColour - 1; j++)
                        {
                            Vs.Add(Vs[i]);
                            Ol.Add(Ol[i]);
                            Nl.Add(Nl[i]);
                            Ql.Add(Ql[i]);
                        }
                        numV += numColour - 1;
                        stats.nonManifoldSplits += numColour - 1;
                    }
                }
            } while (numV != numV0);

            // Drop the degenerate faces; what remains are half-cells.
            var triVertices = new List<int>(tv.Length);
            var triEdges = new List<int>(tv.Length);
            var triOrients = new List<int>(tv.Length);
            for (var i = 0; i < coarseFaces; i++)
            {
                int p0 = tv[3 * i], p1 = tv[3 * i + 1], p2 = tv[3 * i + 2];
                if (p0 == p1 || p1 == p2 || p2 == p0) continue;
                for (var j = 0; j < 3; j++)
                {
                    triVertices.Add(tv[3 * i + j]);
                    triEdges.Add(F2E[3 * i + j]);
                    triOrients.Add(FQ[3 * i + j]);
                }
            }
            var triCount = triVertices.Count / 3;

            for (var i = 0; i < triCount; i++)
            {
                var d1 = Rshift90(EdgeDiffT[triEdges[3 * i]], triOrients[3 * i]);
                var d2 = Rshift90(EdgeDiffT[triEdges[3 * i + 1]], triOrients[3 * i + 1]);
                if (d1.x * d2.y - d1.y * d2.x < 0) stats.flippedTriangles++;
            }

            // Pair the two half-cells on each diagonal.
            var quadKeys = new List<long>();
            var quads = new Dictionary<long, (Vector3Int first, Vector3Int second)>(EdgeKey.Comparer);
            for (var i = 0; i < triCount; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var e = triEdges[3 * i + j];
                    var d = EdgeDiffT[e];
                    if (Mathf.Abs(d.x) != 1 || Mathf.Abs(d.y) != 1) continue;

                    var v1 = triVertices[3 * i + j];
                    var v2 = triVertices[3 * i + (j + 1) % 3];
                    var v3 = triVertices[3 * i + (j + 2) % 3];
                    var key = EdgeKey.PackUndirected(v1, v2);
                    if (quads.TryGetValue(key, out var pair))
                    {
                        quads[key] = (pair.first, new Vector3Int(v1, v2, v3));
                    }
                    else
                    {
                        quads[key] = (new Vector3Int(v1, v2, v3), new Vector3Int(-1, -1, -1));
                        quadKeys.Add(key);
                    }
                }
            }

            var faces = new List<int[]>(quads.Count);
            foreach (var key in quadKeys)
            {
                var (first, second) = quads[key];
                if (second.x == -1)
                {
                    stats.unpairedDiagonals++;
                    continue;
                }
                if (first.z == second.z) continue;
                faces.Add(new[] { first.y, first.z, first.x, second.z });
            }

            var mesh = new QuadMesh { faces = faces, vertexCount = numV };
            mesh.Build();

            // A quad naming one vertex twice is a fold-over; drop it and re-link.
            while (true)
            {
                var erased = new bool[faces.Count];
                var any = false;
                for (var i = 0; i < faces.Count; i++)
                {
                    var q = faces[i];
                    for (var j = 0; j < 3 && !erased[i]; j++)
                        for (var k = j + 1; k < 4; k++)
                            if (q[j] == q[k]) { erased[i] = true; any = true; break; }
                }
                if (!any) break;

                var kept = new List<int[]>(faces.Count);
                for (var i = 0; i < faces.Count; i++)
                {
                    if (erased[i]) stats.degenerateQuadsRemoved++;
                    else kept.Add(faces[i]);
                }
                faces = kept;
                mesh.faces = faces;
                mesh.Build();
            }

            FixHoles(mesh, Ol, Nl);
            mesh.Build();

            FixValence(mesh, Vs, Ol, Nl, Ql);

            return new IntegerGridResult
            {
                positions = Ol.ToArray(),
                normals = Nl.ToArray(),
                directions = Ql.ToArray(),
                members = Vs.ToArray(),
                faces = mesh.faces,
            };
        }

        // ------------------------------------------------------------------
        // Holes
        // ------------------------------------------------------------------

        /// <summary>
        /// Loops longer than this are left open; the tiling search is exponential in the length.
        /// 25 is the reference's cut-off. A genuine open boundary -- the neck of a head scan -- is
        /// normally far longer than this, so the cut-off is what separates a hole to be filled from
        /// a border to be respected.
        /// </summary>
        const int k_MaxHoleLoop = 25;

        void FixHoles(QuadMesh mesh, List<Vector3> Ol, List<Vector3> Nl)
        {
            var faces = mesh.faces;
            var quadEdges = new HashSet<long>(EdgeKey.Comparer);
            foreach (var q in faces)
                for (var j = 0; j < 4; j++)
                    quadEdges.Add(EdgeKey.Pack(q[j], q[(j + 1) % 4]));

            var e2e = mesh.e2e;
            var detected = new bool[e2e.Length];
            var loopEdges = new List<int>();
            var loopVertices = new List<int>();

            for (var i = 0; i < e2e.Length; i++)
            {
                if (detected[i] || e2e[i] != -1) continue;
                var f = i / 4;
                if (faces[f][i % 4] == faces[f][(i + 1) % 4]) continue;

                loopEdges.Clear();
                var cur = i;
                var guard = 0;
                while (!detected[cur] && guard++ < e2e.Length)
                {
                    detected[cur] = true;
                    loopEdges.Add(cur);
                    cur = QuadMesh.Next4(cur);
                    var spin = 0;
                    while (e2e[cur] != -1 && spin++ < e2e.Length)
                    {
                        cur = e2e[cur];
                        cur = QuadMesh.Next4(cur);
                    }
                }

                loopVertices.Clear();
                foreach (var e in loopEdges) loopVertices.Add(faces[e / 4][e % 4]);
                if (loopVertices.Count < k_MaxHoleLoop)
                    FixHoleLoop(loopVertices, faces, quadEdges, Ol, Nl);
            }
        }

        void FixHoleLoop(List<int> loopVertices, List<int[]> faces, HashSet<long> quadEdges,
            List<Vector3> Ol, List<Vector3> Nl)
        {
            // Split at repeated vertices into simple loops of even length.
            var loops = new List<List<int>>();
            var seen = new Dictionary<int, int>();
            for (var i = 0; i < loopVertices.Count; i++)
            {
                if (seen.TryGetValue(loopVertices[i], out var j))
                {
                    var sub = new List<int>();
                    loops.Add(sub);
                    if (i - j > 3 && (i - j) % 2 == 0)
                    {
                        for (var k = j; k < i; k++)
                        {
                            if (!seen.ContainsKey(loopVertices[k])) continue;
                            sub.Add(loopVertices[k]);
                            seen.Remove(loopVertices[k]);
                        }
                    }
                }
                seen[loopVertices[i]] = i;
            }

            if (seen.Count >= 3)
            {
                var sub = new List<int>();
                loops.Add(sub);
                for (var k = 0; k < loopVertices.Count; k++)
                {
                    if (!seen.ContainsKey(loopVertices[k])) continue;
                    sub.Add(loopVertices[k]);
                    seen.Remove(loopVertices[k]);
                }
            }

            foreach (var loop in loops)
            {
                if (loop.Count == 0) continue;

                var quads = new List<int[]>();
                QuadEnergy(loop, quads, Ol, Nl);

                foreach (var p in quads)
                {
                    var conflict = false;
                    for (var j = 0; j < 4; j++)
                    {
                        if (!quadEdges.Contains(EdgeKey.Pack(p[j], p[(j + 1) % 4]))) continue;
                        conflict = true;
                        break;
                    }
                    if (conflict) continue;

                    for (var j = 0; j < 4; j++)
                        quadEdges.Add(EdgeKey.Pack(p[j], p[(j + 1) % 4]));
                    faces.Add(p);
                    stats.holeQuads++;
                }
            }
        }

        /// <summary>
        /// Best quad tiling of a loop by squared corner-angle deviation, searched over every way of
        /// cutting the loop into a quad plus up to three even sub-loops. Returns the energy.
        /// </summary>
        static double QuadEnergy(List<int> loop, List<int[]> resQuads, List<Vector3> Ol, List<Vector3> Nl)
        {
            if (loop.Count < 4) return 0;

            if (loop.Count == 4)
            {
                double energy = 0;
                for (var j = 0; j < 4; j++)
                {
                    var v0 = loop[j];
                    var v2 = loop[(j + 1) % 4];
                    var v1 = loop[(j + 3) % 4];
                    var pt1 = (Ol[v1] - Ol[v0]).normalized;
                    var pt2 = (Ol[v2] - Ol[v0]).normalized;
                    var n = Vector3.Cross(pt1, pt2);
                    double sina = n.magnitude;
                    if (Vector3.Dot(n, Nl[v0]) < 0) sina = -sina;
                    double cosa = Vector3.Dot(pt1, pt2);
                    var angle = Math.Atan2(sina, cosa) / Math.PI * 180.0;
                    if (angle < 0) angle = 360 + angle;
                    energy += angle * angle;
                }
                resQuads.Add(new[] { loop[0], loop[3], loop[2], loop[1] });
                return energy;
            }

            var best = 1e30;
            var n0 = loop.Count;
            var scratch = new List<int[]>[4];
            for (var seg1 = 2; seg1 < n0; seg1 += 2)
            {
                for (var seg2 = seg1 + 1; seg2 < n0; seg2 += 2)
                {
                    for (var i = 0; i < 4; i++) scratch[i] = new List<int[]>();

                    var quad = new List<int> { loop[0], loop[1], loop[seg1], loop[seg2] };
                    var energy = QuadEnergy(quad, scratch[0], Ol, Nl);

                    if (seg1 > 2)
                    {
                        var sub = loop.GetRange(1, seg1 - 1);
                        sub.Add(loop[seg1]);
                        energy += QuadEnergy(sub, scratch[1], Ol, Nl);
                    }
                    if (seg2 != seg1 + 1)
                    {
                        var sub = loop.GetRange(seg1, seg2 - seg1);
                        sub.Add(loop[seg2]);
                        energy += QuadEnergy(sub, scratch[2], Ol, Nl);
                    }
                    if (seg2 + 1 != n0)
                    {
                        var sub = loop.GetRange(seg2, n0 - seg2);
                        sub.Add(loop[0]);
                        energy += QuadEnergy(sub, scratch[3], Ol, Nl);
                    }

                    if (best <= energy) continue;
                    best = energy;
                    resQuads.Clear();
                    for (var i = 0; i < 4; i++) resQuads.AddRange(scratch[i]);
                }
            }

            return best;
        }

        // ------------------------------------------------------------------
        // Valence
        // ------------------------------------------------------------------

        void FixValence(QuadMesh mesh, List<List<int>> Vs, List<Vector3> Ol, List<Vector3> Nl,
            List<Vector3> Ql)
        {
            var limit = mesh.faces.Count * 4 + 1;

            // --- valence 2: merge the two quads through the vertex ---
            while (true)
            {
                var faces = mesh.faces;
                var v2e = mesh.v2e;
                var e2e = mesh.e2e;
                var update = false;
                var marks = new bool[mesh.vertexCount];
                var erased = new bool[faces.Count];
                var dedges = new List<int>();

                for (var i = 0; i < mesh.vertexCount; i++)
                {
                    var deid0 = v2e[i];
                    if (marks[i] || deid0 == -1) continue;

                    dedges.Clear();
                    var deid = deid0;
                    var guard = 0;
                    do
                    {
                        dedges.Add(deid);
                        deid = e2e[QuadMesh.Prev4(deid)];
                    } while (deid != deid0 && deid != -1 && guard++ < limit);

                    if (dedges.Count != 2) continue;

                    var d0 = dedges[0];
                    var d1 = dedges[1];
                    var v1 = faces[d0 / 4][(d0 + 1) % 4];
                    var v2 = faces[d0 / 4][(d0 + 2) % 4];
                    var v3 = faces[d1 / 4][(d1 + 1) % 4];
                    var v4 = faces[d1 / 4][(d1 + 2) % 4];
                    if (marks[v1] || marks[v2] || marks[v3] || marks[v4]) continue;
                    marks[v1] = marks[v2] = marks[v3] = marks[v4] = true;

                    if (v1 == v2 || v1 == v3 || v1 == v4 || v2 == v3 || v2 == v4 || v3 == v4)
                        erased[d0 / 4] = true;
                    else
                        faces[d0 / 4] = new[] { v1, v2, v3, v4 };

                    erased[d1 / 4] = true;
                    update = true;
                    stats.valence2Removed++;
                }

                if (!update) break;

                var kept = new List<int[]>(faces.Count);
                for (var i = 0; i < faces.Count; i++)
                    if (!erased[i]) kept.Add(faces[i]);
                mesh.faces = kept;
                mesh.Build();
            }

            // --- a vertex with more than one fan of quads becomes one vertex per fan ---
            {
                var faces = mesh.faces;
                var e2e = mesh.e2e;
                var vDedges = new List<int>[mesh.vertexCount];
                for (var i = 0; i < faces.Count; i++)
                    for (var j = 0; j < 4; j++)
                        (vDedges[faces[i][j]] ??= new List<int>()).Add(4 * i + j);

                var top = mesh.vertexCount;
                var groups = new Dictionary<int, int>();
                for (var i = 0; i < vDedges.Length; i++)
                {
                    if (vDedges[i] == null) continue;
                    groups.Clear();
                    var groupId = 0;

                    for (var j = 0; j < vDedges[i].Count; j++)
                    {
                        var deid = vDedges[i][j];
                        if (groups.ContainsKey(deid)) continue;

                        var deid0 = deid;
                        var guard = 0;
                        do
                        {
                            groups[deid] = groupId;
                            deid = e2e[QuadMesh.Prev4(deid)];
                        } while (deid != deid0 && deid != -1 && guard++ < limit);

                        if (deid == -1)
                        {
                            deid = deid0;
                            guard = 0;
                            while (e2e[deid] != -1 && guard++ < limit)
                            {
                                deid = e2e[deid];
                                deid = QuadMesh.Next4(deid);
                                groups[deid] = groupId;
                            }
                        }

                        groupId++;
                    }

                    if (groupId <= 1) continue;

                    foreach (var g in groups)
                        if (g.Value >= 1) faces[g.Key / 4][g.Key % 4] = top - 1 + g.Value;

                    for (var j = 1; j < groupId; j++)
                    {
                        Vs.Add(Vs[i]);
                        Nl.Add(Nl[i]);
                        Ql.Add(Ql[i]);
                        Ol.Add(Ol[i]);
                    }
                    top = Ol.Count;
                    stats.nonManifoldSplits += groupId - 1;
                }

                mesh.vertexCount = Ol.Count;
                mesh.Build();
            }

            // --- valence above five: split the vertex and insert a quad ---
            while (true)
            {
                var faces = mesh.faces;
                var v2e = mesh.v2e;
                var e2e = mesh.e2e;
                var update = false;
                var marks = new bool[mesh.vertexCount];
                var valences = new int[mesh.vertexCount];

                for (var i = 0; i < mesh.vertexCount; i++)
                {
                    var deid0 = v2e[i];
                    if (deid0 == -1) continue;
                    var deid = deid0;
                    var count = 0;
                    var guard = 0;
                    do
                    {
                        count++;
                        var deid1 = e2e[deid];
                        if (deid1 == -1)
                        {
                            count++;
                            break;
                        }
                        deid = QuadMesh.Next4(deid1);
                    } while (deid != deid0 && deid != -1 && guard++ < limit);
                    valences[i] = count;
                }

                var queue = new List<(int valence, int vertex)>();
                for (var i = 0; i < valences.Length; i++)
                    if (valences[i] > 5) queue.Add((valences[i], i));
                queue.Sort((a, b) =>
                    a.valence != b.valence ? b.valence.CompareTo(a.valence) : b.vertex.CompareTo(a.vertex));

                var loopVertices = new List<int>();
                var loopDedges = new List<int>();

                foreach (var (valence, vertex) in queue)
                {
                    if (marks[vertex]) continue;
                    var deid0 = v2e[vertex];
                    if (deid0 == -1) continue;

                    loopVertices.Clear();
                    loopDedges.Clear();
                    var deid = deid0;
                    var marked = false;
                    var guard = 0;
                    do
                    {
                        var v = faces[deid / 4][(deid + 1) % 4];
                        loopDedges.Add(deid);
                        loopVertices.Add(v);
                        if (marks[v]) marked = true;
                        var deid1 = e2e[deid];
                        if (deid1 == -1) break;
                        deid = QuadMesh.Next4(deid1);
                    } while (deid != deid0 && deid != -1 && guard++ < limit);
                    if (marked) continue;

                    var n = loopVertices.Count;
                    var newVertex = Ol.Count;

                    // Split the fan where the two ring vertices it would join have the lowest
                    // valences, and bridge the cut with one quad. The reference takes this branch
                    // for boundary fans too, and rightly: leaving the cut open would slit the mesh.
                    var step = (valence + 1) / 2;
                    var minVal = (int.MaxValue, int.MaxValue);
                    var splitIdx = -1;
                    for (var i = 0; i < n; i++)
                    {
                        if (i + step >= n) continue;
                        var a = valences[loopVertices[i]];
                        var b = valences[loopVertices[i + step]];
                        if (a < b) (a, b) = (b, a);
                        var key = (a, b);
                        if (key.CompareTo(minVal) >= 0) continue;
                        minVal = key;
                        splitIdx = i + 1;
                    }
                    if (splitIdx < 0 || minVal.Item1 >= valence) continue;

                    update = true;
                    for (var id = splitIdx; id < splitIdx + step; id++)
                        faces[loopDedges[id] / 4][loopDedges[id] % 4] = newVertex;

                    faces.Add(new[]
                    {
                        newVertex,
                        loopVertices[(splitIdx + n - 1) % n],
                        vertex,
                        loopVertices[(splitIdx + step - 1 + n) % n],
                    });

                    marks[vertex] = true;
                    foreach (var v in loopVertices) marks[v] = true;

                    Vs.Add(Vs[vertex]);
                    Ol.Add(Ol[vertex]);
                    Nl.Add(Nl[vertex]);
                    Ql.Add(Ql[vertex]);
                    stats.highValenceSplits++;
                }

                if (!update) break;
                mesh.vertexCount = Ol.Count;
                mesh.Build();
            }

            // --- drop vertices no face names ---
            {
                var faces = mesh.faces;
                var used = new bool[Ol.Count];
                foreach (var q in faces)
                    foreach (var v in q) used[v] = true;

                var remap = new int[Ol.Count];
                var top = 0;
                for (var i = 0; i < Ol.Count; i++)
                {
                    if (!used[i]) { remap[i] = -1; continue; }
                    Nl[top] = Nl[i];
                    Ol[top] = Ol[i];
                    Ql[top] = Ql[i];
                    Vs[top] = Vs[i];
                    remap[i] = top;
                    top++;
                }
                Nl.RemoveRange(top, Nl.Count - top);
                Ol.RemoveRange(top, Ol.Count - top);
                Ql.RemoveRange(top, Ql.Count - top);
                Vs.RemoveRange(top, Vs.Count - top);

                foreach (var q in faces)
                    for (var j = 0; j < 4; j++) q[j] = remap[q[j]];

                mesh.vertexCount = top;
                mesh.Build();
            }
        }
    }
}
