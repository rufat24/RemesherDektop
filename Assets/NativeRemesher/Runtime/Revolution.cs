// Surfaces of revolution (a tyre, a rim's barrel, a hub, a knob, a bottle) built as what they are:
// one profile turned about an axis, in rings and rows.
//
// The remesher follows curvature, and on a tyre the curvature points every way: at a car's budget
// (the Ford's tyres got 280 quads) its quads land anywhere on the tread and the sidewalls, and the
// wheel came out a pentagon, then collapsed and paired, faceted. A turned profile at the same count
// is a 34-gon with eight rings: round from every side, its tread left to the normal map.
//
// The test is what makes a surface one: cut into angular sectors about the candidate axis, every
// sector's points, as (distance from the axis, height along it), lie on the same profile. A tyre's
// sectors agree to 0.6% of its size, tread and all; a rim with its lug holes to 3.6%; a window or a
// steering wheel no closer than 14%.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public static class Revolution
    {
        /// <summary>Build surfaces of revolution as a turned profile (false: remeshed like any other).</summary>
        public static bool enabled = true;

        /// <summary>
        /// The most two sectors' profiles may differ (99th percentile, as a share of the surface's
        /// size) for it to count as turned.
        /// </summary>
        public static float tolerance = 0.015f;

        /// <summary>
        /// Looser: sectors that differ by up to this share (a rim's disc with its lug holes, a hub
        /// with its bolts) still count, for a surface given at most <see cref="looseBudget"/> quads --
        /// too few to make the holes as holes; they go to the normal map.
        /// </summary>
        public static float looseTolerance = 0.08f;

        /// <summary>The most quads a loosely turned surface may be given and still be built turned.</summary>
        public static int looseBudget = 400;

        /// <summary>Fewest steps around: a wheel under 24 sides reads as a polygon.</summary>
        public static int minSteps = 24;

        /// <summary>
        /// The profile gets the segments it needs to be within this share of the surface's size,
        /// over what the budget gives it: at a budget's single segment a rim's disc, metal on both
        /// sides, came out one band facing one way, and from the other side not there.
        /// </summary>
        public static float profileTolerance = 0.01f;

        /// <summary>
        /// The most segments one profile curve is given for its shape, when the budget gives it
        /// fewer (a tyre's tread grooves would otherwise ask for one at every groove).
        /// </summary>
        public static int profileSegments = 8;

        /// <summary>
        /// With the model's cell given, the profile need be no closer than this share of a cell
        /// (a rim's disc, a few hundredths of a cell thick, still keeps both its sides).
        /// </summary>
        public static float cellTolerance = 0.02f;

        /// <summary>Whether a surface with this fit, given this many quads, is built turned.</summary>
        public static bool Accept(Fit fit, int targetQuads) =>
            fit != null && (!fit.loose || targetQuads <= looseBudget);

        const int k_Sectors = 12;
        const int k_Samples = 12000;

        /// <summary>
        /// A part not turned as a whole is looked at piece by piece (its connected pieces): the
        /// Porsche keeps two whole wheels, tyres, rims and hubs, in one part, which no axis fits,
        /// and remeshed together its tyres came out a tenth too small with their tread zigzagged.
        /// </summary>
        public static bool pieces = true;

        /// <summary>The least share of a part's area its turned pieces must make up for it to be built by pieces.</summary>
        public static float piecesMinShare = 0.3f;

        /// <summary>The most faces a part's turned pieces may take together, as a multiple of its quads (the others go with the rest).</summary>
        public static int piecesSlack = 3;

        /// <summary>The least share of a part's area a piece must have to be looked at.</summary>
        public static float pieceMinShare = 0.02f;

        /// <summary>A part's connected pieces (their triangles, as indices into the part's), and the fit of each turned one.</summary>
        public sealed class Pieces
        {
            public List<int[]> triangles;
            public Fit[] fits;
            public double[] areas;
            /// <summary>The share of the part's area in turned pieces.</summary>
            public float turnedShare;
            public override string ToString()
            {
                var turned = 0;
                foreach (var f in fits) if (f != null) turned++;
                return $"{turned} of {fits.Length} pieces turned, {turnedShare:P0} of its area";
            }
        }

        /// <summary>
        /// The part's connected pieces, each looked at for a turn, when it has more than one and its
        /// turned pieces make up at least <see cref="piecesMinShare"/> of its area; null otherwise.
        /// </summary>
        public static Pieces FindPieces(Vector3[] positions, int[] triangles)
        {
            if (positions == null || triangles == null || triangles.Length < 6) return null;
            var weld = new Dictionary<Vector3, int>(positions.Length);
            var of = new int[positions.Length];
            for (var i = 0; i < positions.Length; i++)
            {
                if (!weld.TryGetValue(positions[i], out var w)) { w = weld.Count; weld[positions[i]] = w; }
                of[i] = w;
            }
            var parent = new int[weld.Count];
            for (var i = 0; i < parent.Length; i++) parent[i] = i;
            int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                var a = Root(of[triangles[t]]);
                parent[Root(of[triangles[t + 1]])] = a;
                parent[Root(of[triangles[t + 2]])] = a;
            }
            var index = new Dictionary<int, int>();
            var lists = new List<List<int>>();
            var areas = new List<double>();
            double total = 0;
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                var r = Root(of[triangles[t]]);
                if (!index.TryGetValue(r, out var k)) { k = lists.Count; index[r] = k; lists.Add(new List<int>()); areas.Add(0); }
                lists[k].Add(triangles[t]); lists[k].Add(triangles[t + 1]); lists[k].Add(triangles[t + 2]);
                Vector3 pa = positions[triangles[t]], pb = positions[triangles[t + 1]], pc = positions[triangles[t + 2]];
                var ar = 0.5 * Vector3.Cross(pb - pa, pc - pa).magnitude;
                areas[k] += ar;
                total += ar;
            }
            if (lists.Count < 2 || !(total > 0)) return null;
            var result = new Pieces { triangles = new List<int[]>(), fits = new Fit[lists.Count], areas = areas.ToArray() };
            foreach (var l in lists) result.triangles.Add(l.ToArray());
            double turned = 0;
            for (var k = 0; k < lists.Count; k++)
            {
                if (areas[k] < pieceMinShare * total) continue;
                Compact(positions, result.triangles[k], out var piecePositions, out var pieceTriangles);
                result.fits[k] = Find(piecePositions, pieceTriangles);
                if (result.fits[k] != null) turned += areas[k];
            }
            result.turnedShare = (float)(turned / total);
            return result.turnedShare >= piecesMinShare ? result : null;
        }

        /// <summary>The vertices <paramref name="triangles"/> use, and the triangles over them.</summary>
        public static void Compact(Vector3[] positions, int[] triangles, out Vector3[] outPositions, out int[] outTriangles)
        {
            var map = new Dictionary<int, int>();
            var list = new List<Vector3>();
            outTriangles = new int[triangles.Length];
            for (var i = 0; i < triangles.Length; i++)
            {
                if (!map.TryGetValue(triangles[i], out var v)) { v = list.Count; map[triangles[i]] = v; list.Add(positions[triangles[i]]); }
                outTriangles[i] = v;
            }
            outPositions = list.ToArray();
        }

        public sealed class Fit
        {
            public Vector3 centre, axis, u, w;
            public float size, error;
            /// <summary>Within looseTolerance only, not tolerance.</summary>
            public bool loose;
            public override string ToString() =>
                $"axis {axis:F3} at {centre:F3}, sectors differ {error:P2} of {size:0.###}{(loose ? " (loosely)" : "")}";
        }

        /// <summary>
        /// The axis the surface is turned about, when it is turned (sectors within
        /// <see cref="tolerance"/>, or marked loose within <see cref="looseTolerance"/>); null otherwise.
        /// </summary>
        public static Fit Find(Vector3[] positions, int[] triangles)
        {
            if (positions == null || triangles == null || triangles.Length < 3 * 16) return null;
            Weld(positions, triangles, out var welded, out var tris);
            if (tris.Length < 3 * 16) return null;
            var n = welded.Length;
            var normals = new Vector3[n];
            var area = new float[n];
            var triArea = new float[tris.Length / 3];
            double total = 0;
            var centre = Vector3.zero;
            for (var t = 0; t < tris.Length / 3; t++)
            {
                Vector3 a = welded[tris[3 * t]], b = welded[tris[3 * t + 1]], c = welded[tris[3 * t + 2]];
                var cross = Vector3.Cross(b - a, c - a);
                var ar = 0.5f * cross.magnitude;
                triArea[t] = ar;
                total += ar;
                centre += ar * (a + b + c) / 3f;
                for (var k = 0; k < 3; k++)
                {
                    normals[tris[3 * t + k]] += cross;
                    area[tris[3 * t + k]] += ar / 3f;
                }
            }
            if (!(total > 0)) return null;
            centre /= (float)total;
            for (var v = 0; v < n; v++) normals[v] = MeshMath.SafeNormalize(normals[v], Vector3.up);

            var samples = Samples(welded, tris, triArea, total);
            var candidates = new List<(Vector3 axis, Vector3 point)>();
            // From the normals: every normal of a turned surface meets the axis.
            var all = new int[n];
            for (var v = 0; v < n; v++) all[v] = v;
            var kinematic = KinematicSurfaces.FitPiece(all, welded, normals, area);
            if (kinematic.kind == KinematicSurfaces.Kind.Rotation || kinematic.kind == KinematicSurfaces.Kind.Screw)
                candidates.Add((kinematic.axisDirection.normalized, kinematic.axisPoint));
            // From the spread: the one principal axis unlike the other two, through the centroid.
            var principal = PrincipalAxis(samples, centre);
            if (principal != Vector3.zero) candidates.Add((principal, centre));

            foreach (var share in new[] { tolerance, looseTolerance })
            {
                if (share < tolerance) continue;
                Fit best = null;
                foreach (var (axis, point) in candidates)
                {
                    if (axis.sqrMagnitude < 0.5f) continue;
                    var fit = Measure(samples, axis, point, share);
                    if (fit != null && (best == null || fit.error < best.error)) best = fit;
                }
                if (best == null || best.error > share) continue;
                best.loose = share > tolerance;
                return best;
            }
            return null;
        }

        /// <summary>
        /// The surface as its profile turned about <paramref name="fit"/>'s axis, about
        /// <paramref name="targetQuads"/> quads of about equal sides; false when no profile is found.
        /// <paramref name="cell"/>, when given, is the model's edge length in the surface's space: a
        /// surface small beside it (a lamp's bulb) is given fewer than <see cref="minSteps"/> around,
        /// and its profile is held to a share of the cell, not only of its own size -- turned at a
        /// wheel's 24 sides and 1 %, the Ford's lamp pieces took 360 faces for 48 quads.
        /// </summary>
        public static bool Build(Vector3[] positions, int[] triangles, Fit fit, int targetQuads,
            out Vector3[] outPositions, out List<int[]> outFaces, float cell = 0f)
        {
            outPositions = null;
            outFaces = null;
            if (fit == null) return false;
            Weld(positions, triangles, out var welded, out var tris);

            // The profile: the cut in the fewest curves (one through a lug hole is broken in two),
            // and of those the shortest (one through the tread's grooves zigzags; one along a
            // block's face does not).
            List<List<Vector2>> chains = null;
            var shortest = float.PositiveInfinity;
            var fewest = int.MaxValue;
            float bestAngle = 0f;
            for (var k = 0; k < 16; k++)
            {
                var angle = 0.1234f + k * Mathf.PI / 8f;
                var cut = Cut(welded, tris, fit, angle);
                // Crumbs of a cut (a rib caught at its tip) are left out.
                cut.RemoveAll(c => Length(c) < 0.01f * fit.size || c.Count < 2);
                var length = 0f;
                foreach (var chain in cut) length += Length(chain);
                if (cut.Count == 0 || cut.Count > fewest || cut.Count == fewest && length >= shortest) continue;
                fewest = cut.Count;
                shortest = length;
                chains = cut;
                bestAngle = angle;
            }
            if (chains == null || chains.Count == 0) return false;

            // Steps around and along, quads about square: around, 2 pi rbar / N; along, L / M.
            double totalLength = 0, moment = 0;
            foreach (var chain in chains)
                for (var i = 0; i + 1 < chain.Count; i++)
                {
                    var s = (chain[i + 1] - chain[i]).magnitude;
                    totalLength += s;
                    moment += s * 0.5 * (chain[i].x + chain[i + 1].x);
                }
            var rbar = moment / Math.Max(totalLength, 1e-30);
            var fewestSteps = minSteps;
            var tolerance = profileTolerance * fit.size;
            if (cell > 0f)
            {
                var rmax = 0f;
                foreach (var chain in chains) foreach (var q in chain) rmax = Mathf.Max(rmax, q.x);
                // Sides about a third of a cell long, but never under 8: a cell long, the Ford's
                // rims came out ten-sided beside a 24-sided tyre.
                fewestSteps = Mathf.Clamp(Mathf.RoundToInt(6f * Mathf.PI * rmax / cell), 8, minSteps);
                tolerance = Mathf.Max(tolerance, cellTolerance * cell);
            }
            var target = Math.Max(targetQuads, fewestSteps * chains.Count);
            var steps = (int)Math.Round(Math.Sqrt(target * 2 * Math.PI * rbar / Math.Max(totalLength, 1e-30)));
            steps = Mathf.Clamp(steps, fewestSteps, 512);
            // Even, so the rings pair into quads the same way all round.
            steps += steps & 1;
            var along = Math.Max(chains.Count, target / steps);

            var points = new List<Vector3>();
            var faces = new List<int[]>();
            var onAxis = 1e-4f * fit.size;
            var u = Mathf.Cos(bestAngle) * fit.u + Mathf.Sin(bestAngle) * fit.w;
            var w = Vector3.Cross(fit.axis, u);
            var grid = new SpatialGrid(welded, tris);
            foreach (var chain in chains)
            {
                var closed = (chain[0] - chain[chain.Count - 1]).magnitude < 1e-6f * fit.size;
                var share = Length(chain) / totalLength;
                var segments = Math.Max(closed ? 3 : 1, (int)Math.Round(along * share));
                var profile = Simplify(chain, closed, segments, tolerance, Math.Max(segments, profileSegments));
                // Each profile point's ring (one point when it sits on the axis).
                var ring = new int[profile.Count][];
                for (var i = 0; i < profile.Count; i++)
                {
                    var r = profile[i].x;
                    var h = profile[i].y;
                    if (r <= onAxis)
                    {
                        var single = points.Count;
                        points.Add(fit.centre + h * fit.axis);
                        ring[i] = new int[steps];
                        for (var j = 0; j < steps; j++) ring[i][j] = single;
                        continue;
                    }
                    ring[i] = new int[steps];
                    for (var j = 0; j < steps; j++)
                    {
                        var phi = 2f * Mathf.PI * j / steps;
                        ring[i][j] = points.Count;
                        points.Add(fit.centre + h * fit.axis + r * (Mathf.Cos(phi) * u + Mathf.Sin(phi) * w));
                    }
                }
                var first = faces.Count;
                var rows = closed ? profile.Count : profile.Count - 1;
                for (var i = 0; i < rows; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % profile.Count];
                    if (closed && i == profile.Count - 1) break;
                    for (var j = 0; j < steps; j++)
                    {
                        var j1 = (j + 1) % steps;
                        var quad = new List<int>(4) { a[j], b[j], b[j1], a[j1] };
                        // On the axis two corners are one: a triangle; four, nothing.
                        for (var k = quad.Count - 1; k >= 0 && quad.Count > 0; k--)
                            if (quad[k] == quad[(k + 1) % quad.Count]) quad.RemoveAt(k);
                        if (quad.Count >= 3) faces.Add(quad.ToArray());
                    }
                }
                // Facing as the source does, by majority over the chain's faces.
                var agree = 0;
                var srcTris = tris;
                for (var f = first; f < faces.Count; f += Math.Max(1, (faces.Count - first) / 64))
                {
                    var face = faces[f];
                    var c = Vector3.zero;
                    foreach (var v in face) c += points[v];
                    c /= face.Length;
                    var fn = Vector3.Cross(points[face[1]] - points[face[0]], points[face[2]] - points[face[0]]);
                    grid.NearestDistance(c, out var tri);
                    if (tri < 0) continue;
                    Vector3 p0 = welded[srcTris[3 * tri]], p1 = welded[srcTris[3 * tri + 1]], p2 = welded[srcTris[3 * tri + 2]];
                    agree += Vector3.Dot(fn, Vector3.Cross(p1 - p0, p2 - p0)) >= 0f ? 1 : -1;
                }
                if (agree < 0)
                    for (var f = first; f < faces.Count; f++) Array.Reverse(faces[f]);
            }
            if (faces.Count == 0) return false;
            outPositions = points.ToArray();
            outFaces = faces;
            return true;
        }

        // ------------------------------------------------------------------ the test

        static List<Vector3> Samples(Vector3[] positions, int[] tris, float[] triArea, double total)
        {
            // Area-weighted, the same every run.
            var rng = new System.Random(12345);
            var cumulative = new double[triArea.Length];
            double sum = 0;
            for (var t = 0; t < triArea.Length; t++) { sum += triArea[t]; cumulative[t] = sum; }
            var samples = new List<Vector3>(k_Samples);
            for (var s = 0; s < k_Samples; s++)
            {
                var x = rng.NextDouble() * sum;
                var t = Array.BinarySearch(cumulative, x);
                if (t < 0) t = ~t;
                t = Math.Min(t, triArea.Length - 1);
                float a = (float)rng.NextDouble(), b = (float)rng.NextDouble();
                if (a + b > 1f) { a = 1f - a; b = 1f - b; }
                Vector3 p0 = positions[tris[3 * t]], p1 = positions[tris[3 * t + 1]], p2 = positions[tris[3 * t + 2]];
                samples.Add(p0 + a * (p1 - p0) + b * (p2 - p0));
            }
            return samples;
        }

        // Every sector's (r, h) points against every other sector's: the 99th percentile of the
        // distance to the nearest, the worst over the pairs.
        static Fit Measure(List<Vector3> samples, Vector3 axis, Vector3 point, float share)
        {
            axis = axis.normalized;
            var u = Vector3.Cross(axis, Mathf.Abs(axis.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
            var w = Vector3.Cross(axis, u);
            // The centre: the point on the axis level with the samples' mean height.
            var meanH = 0.0;
            foreach (var s in samples) meanH += Vector3.Dot(s - point, axis);
            var centre = point + (float)(meanH / samples.Count) * axis;
            var rh = new Vector2[samples.Count];
            var sector = new int[samples.Count];
            float rMax = 0f, hMin = float.PositiveInfinity, hMax = float.NegativeInfinity;
            for (var i = 0; i < samples.Count; i++)
            {
                var v = samples[i] - centre;
                var h = Vector3.Dot(v, axis);
                var radial = v - h * axis;
                var r = radial.magnitude;
                rh[i] = new Vector2(r, h);
                var theta = Mathf.Atan2(Vector3.Dot(radial, w), Vector3.Dot(radial, u));
                sector[i] = Mathf.Clamp((int)((theta + Mathf.PI) / (2f * Mathf.PI) * k_Sectors), 0, k_Sectors - 1);
                rMax = Mathf.Max(rMax, r);
                hMin = Mathf.Min(hMin, h);
                hMax = Mathf.Max(hMax, h);
            }
            var size = Mathf.Max(rMax, hMax - hMin);
            if (!(size > 0f)) return null;
            // Each sector's points hashed at the tolerance: "is another sector within tau of this
            // point" is three by three cells; the 99th percentile under tau is what is asked.
            var tau = share * size;
            var cells = new Dictionary<long, List<int>>[k_Sectors];
            for (var s = 0; s < k_Sectors; s++) cells[s] = new Dictionary<long, List<int>>(Extraction.EdgeKey.Comparer);
            long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
            for (var i = 0; i < rh.Length; i++)
            {
                var key = Key(Mathf.FloorToInt(rh[i].x / tau), Mathf.FloorToInt(rh[i].y / tau));
                if (!cells[sector[i]].TryGetValue(key, out var list)) cells[sector[i]][key] = list = new List<int>();
                list.Add(i);
            }
            var count = new int[k_Sectors];
            foreach (var s in sector) count[s]++;
            var worst = 0f;
            for (var a = 0; a < k_Sectors; a++)
            {
                if (count[a] == 0) return null;
                for (var b = 0; b < k_Sectors; b++)
                {
                    if (a == b) continue;
                    var far = 0;
                    for (var i = 0; i < rh.Length; i++)
                    {
                        if (sector[i] != a) continue;
                        int cx = Mathf.FloorToInt(rh[i].x / tau), cy = Mathf.FloorToInt(rh[i].y / tau);
                        var near = false;
                        for (var dx = -1; dx <= 1 && !near; dx++)
                            for (var dy = -1; dy <= 1 && !near; dy++)
                                if (cells[b].TryGetValue(Key(cx + dx, cy + dy), out var list))
                                    foreach (var j in list)
                                        if ((rh[j] - rh[i]).sqrMagnitude <= tau * tau) { near = true; break; }
                        if (!near) far++;
                    }
                    worst = Mathf.Max(worst, (float)far / count[a]);
                }
            }
            // The share of points with no other sector within tau, as a stand-in for the 99th
            // percentile: within tolerance when at most 1% are that far.
            return new Fit
            {
                centre = centre, axis = axis, u = u, w = w, size = size,
                error = worst <= 0.01f ? share * worst / 0.01f : share * (1f + worst),
            };
        }

        static Vector3 PrincipalAxis(List<Vector3> samples, Vector3 centre)
        {
            var m = new double[3, 3];
            foreach (var s in samples)
            {
                var d = s - centre;
                for (var i = 0; i < 3; i++)
                    for (var j = 0; j < 3; j++) m[i, j] += d[i] * d[j];
            }
            Eigen3(m, out var values, out var vectors);
            // The eigenvalue farthest from the other two, which must be near equal.
            var order = new[] { 0, 1, 2 };
            Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));
            double lo = values[order[0]], mid = values[order[1]], hi = values[order[2]];
            if (!(hi > 0)) return Vector3.zero;
            var k = mid - lo > hi - mid ? order[0] : order[2];
            var o1 = k == order[0] ? mid : lo;
            var o2 = k == order[0] ? hi : mid;
            if (Math.Abs(o1 - o2) > 0.1 * Math.Max(o1, o2)) return Vector3.zero;
            return new Vector3((float)vectors[0, k], (float)vectors[1, k], (float)vectors[2, k]).normalized;
        }

        static void Eigen3(double[,] a, out double[] values, out double[,] vectors)
        {
            var m = (double[,])a.Clone();
            vectors = new double[3, 3];
            for (var i = 0; i < 3; i++) vectors[i, i] = 1;
            for (var sweep = 0; sweep < 50; sweep++)
            {
                var off = m[0, 1] * m[0, 1] + m[0, 2] * m[0, 2] + m[1, 2] * m[1, 2];
                if (off < 1e-30) break;
                for (var p = 0; p < 2; p++)
                    for (var q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(m[p, q]) < 1e-300) continue;
                        var theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                        var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        var cs = 1 / Math.Sqrt(t * t + 1);
                        var sn = t * cs;
                        for (var k = 0; k < 3; k++)
                        {
                            var mkp = m[k, p]; var mkq = m[k, q];
                            m[k, p] = cs * mkp - sn * mkq;
                            m[k, q] = sn * mkp + cs * mkq;
                        }
                        for (var k = 0; k < 3; k++)
                        {
                            var mpk = m[p, k]; var mqk = m[q, k];
                            m[p, k] = cs * mpk - sn * mqk;
                            m[q, k] = sn * mpk + cs * mqk;
                        }
                        for (var k = 0; k < 3; k++)
                        {
                            var vkp = vectors[k, p]; var vkq = vectors[k, q];
                            vectors[k, p] = cs * vkp - sn * vkq;
                            vectors[k, q] = sn * vkp + cs * vkq;
                        }
                    }
            }
            values = new[] { m[0, 0], m[1, 1], m[2, 2] };
        }

        // ------------------------------------------------------------------ the profile

        // The surface cut by the half-plane through the axis at this angle: its curves, as
        // (distance from the axis, height), chained through the edges they cross.
        static List<List<Vector2>> Cut(Vector3[] positions, int[] tris, Fit fit, float angle)
        {
            var d = Mathf.Cos(angle) * fit.u + Mathf.Sin(angle) * fit.w;
            var normal = Vector3.Cross(fit.axis, d);
            var side = new float[positions.Length];
            for (var v = 0; v < positions.Length; v++) side[v] = Vector3.Dot(positions[v] - fit.centre, normal);
            var pointOf = new Dictionary<long, Vector2>(Extraction.EdgeKey.Comparer);
            var links = new Dictionary<long, List<long>>(Extraction.EdgeKey.Comparer);
            long Edge(int a, int b) => Extraction.EdgeKey.PackUndirected(a, b);
            void Link(long a, long b)
            {
                if (!links.TryGetValue(a, out var la)) links[a] = la = new List<long>(2);
                la.Add(b);
            }
            for (var t = 0; t + 2 < tris.Length; t += 3)
            {
                var crossing = new List<long>(2);
                for (var k = 0; k < 3; k++)
                {
                    int a = tris[t + k], b = tris[t + (k + 1) % 3];
                    if (side[a] >= 0f == side[b] >= 0f) continue;
                    var key = Edge(a, b);
                    if (!pointOf.ContainsKey(key))
                    {
                        // From the edge's lower index, so both triangles get the same point.
                        int lo = Math.Min(a, b), hi = Math.Max(a, b);
                        var s = side[lo] / (side[lo] - side[hi]);
                        var p = Vector3.Lerp(positions[lo], positions[hi], s) - fit.centre;
                        var h = Vector3.Dot(p, fit.axis);
                        var radial = p - h * fit.axis;
                        // The other half of the plane, behind the axis, is another angle's cut.
                        pointOf[key] = Vector3.Dot(radial, d) > 0f ? new Vector2(radial.magnitude, h) : new Vector2(-1f, h);
                    }
                    crossing.Add(key);
                }
                if (crossing.Count != 2) continue;
                if (pointOf[crossing[0]].x < 0f || pointOf[crossing[1]].x < 0f) continue;
                Link(crossing[0], crossing[1]);
                Link(crossing[1], crossing[0]);
            }
            // Walk the links into chains: open ones from an end first, then the closed ones.
            var chains = new List<List<Vector2>>();
            var done = new HashSet<long>(Extraction.EdgeKey.Comparer);
            void Walk(long start)
            {
                var chain = new List<Vector2> { pointOf[start] };
                done.Add(start);
                var current = start;
                while (true)
                {
                    long next = -1;
                    foreach (var o in links[current])
                        if (!done.Contains(o)) { next = o; break; }
                    if (next < 0)
                    {
                        // Back at the start: closed.
                        if (chain.Count > 2 && links[current].Contains(start)) chain.Add(pointOf[start]);
                        break;
                    }
                    done.Add(next);
                    chain.Add(pointOf[next]);
                    current = next;
                }
                chains.Add(chain);
            }
            foreach (var pair in links)
                if (!done.Contains(pair.Key) && pair.Value.Count == 1) Walk(pair.Key);
            foreach (var pair in links)
                if (!done.Contains(pair.Key)) Walk(pair.Key);
            return chains;
        }

        static float Length(List<Vector2> chain)
        {
            var length = 0f;
            for (var i = 0; i + 1 < chain.Count; i++) length += (chain[i + 1] - chain[i]).magnitude;
            return length;
        }

        // The chain cut to at least this many segments, and more while it is off by more than the
        // tolerance, keeping its farthest-out points first (the corners before the sides). A
        // closed chain ends where it starts.
        static List<Vector2> Simplify(List<Vector2> chain, bool closed, int segments, float tolerance, int most)
        {
            var n = chain.Count;
            var keep = new SortedSet<int> { 0, n - 1 };
            if (closed)
            {
                // The point farthest from the start opens the loop into two.
                var far = 0;
                for (var i = 1; i < n - 1; i++)
                    if ((chain[i] - chain[0]).sqrMagnitude > (chain[far] - chain[0]).sqrMagnitude) far = i;
                if (far > 0) keep.Add(far);
            }
            while (true)
            {
                var bestIndex = -1;
                var bestDistance = -1f;
                var list = new List<int>(keep);
                for (var s = 0; s + 1 < list.Count; s++)
                {
                    int a = list[s], b = list[s + 1];
                    for (var i = a + 1; i < b; i++)
                    {
                        var dist = SegmentDistance(chain[i], chain[a], chain[b]);
                        if (dist > bestDistance) { bestDistance = dist; bestIndex = i; }
                    }
                }
                // The budget's count at least; past it, only while the profile is off by more than
                // the tolerance (a rim's flange, its dish, its hub), up to the most.
                if (bestIndex < 0 || keep.Count - 1 >= segments && (bestDistance <= tolerance || keep.Count - 1 >= most)) break;
                keep.Add(bestIndex);
            }
            var result = new List<Vector2>(keep.Count);
            foreach (var i in keep) result.Add(chain[i]);
            // A closed chain's last point is its first: one ring, not two on top of each other.
            if (closed && result.Count > 1) result.RemoveAt(result.Count - 1);
            return result;
        }

        static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (a + t * ab - p).magnitude;
        }

        static void Weld(Vector3[] positions, int[] triangles, out Vector3[] welded, out int[] tris)
        {
            var map = new Dictionary<Vector3, int>();
            var weld = new int[positions.Length];
            var list = new List<Vector3>();
            for (var i = 0; i < positions.Length; i++)
            {
                if (!map.TryGetValue(positions[i], out var w)) { w = list.Count; map[positions[i]] = w; list.Add(positions[i]); }
                weld[i] = w;
            }
            var t = new List<int>(triangles.Length);
            for (var k = 0; k + 2 < triangles.Length; k += 3)
            {
                int a = weld[triangles[k]], b = weld[triangles[k + 1]], c = weld[triangles[k + 2]];
                if (a == b || b == c || c == a) continue;
                t.Add(a); t.Add(b); t.Add(c);
            }
            welded = list.ToArray();
            tris = t.ToArray();
        }
    }
}
