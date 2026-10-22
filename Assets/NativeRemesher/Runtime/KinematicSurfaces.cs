// Shape recognition by motion: which way a piece of the surface "runs", from the motions that slide
// it along itself.
//
// A surface swept by a rotation (a tyre, a rim, a wheel's disc, a bottle, a knob, a turned leg) is
// carried into itself by turning about its axis; one swept by a translation (an extrusion, a moulding,
// a prism, a pipe that is straight) by sliding along it; a thread or a spring by a screw motion. Those
// are the surfaces a modeller lays out in rings and rows that follow the motion -- and the ones a
// remesher that only follows curvature gets wrong, because on a tyre the curvature points every way.
//
// The test is Pottmann's line-geometry one (Pottmann & Randrup 1998; Hofer, Odehnal, Pottmann &
// Wallner 2005): a surface is invariant under the motion with velocity field v(x) = cbar + c x x
// exactly when every normal is perpendicular to the velocity at its foot,
//
//     n . v(p) = n . cbar + (p x n) . c = 0,
//
// linear in C = (c, cbar). Over a piece's vertices, area-weighted, the C that best does this is the
// eigenvector of the smallest eigenvalue of a 6x6 matrix. How many eigenvectors fit says what the
// piece is: one -- a rotation (c, cbar perpendicular), a translation (c ~ 0) or a screw; two -- a
// cylinder (the pure rotation is taken: its rings and its axis lines are the same cross); three or
// more -- a plane or a sphere, which have no preferred way to run and are left to the field.
//
// "Fits" is measured, not thresholded on eigenvalues: the share of the piece's area whose normal is
// within inlierDegrees of perpendicular to the motion. Each fitting piece's vertices get the motion's
// direction (its ring or row) as a guide for the orientation field.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public static class KinematicSurfaces
    {
        public enum Kind { None, Rotation, Translation, Screw }

        public sealed class Fit
        {
            public Kind kind;
            public Vector3 c, cbar;      // in the piece's normalised frame (see centre/scale)
            public Vector3 centre;
            public float scale;
            public float inliers;        // share of area the motion fits
            public int fittingMotions;   // how many independent motions fit
            public Vector3 axisDirection, axisPoint; // world space, rotations and screws
            public override string ToString() =>
                $"{kind} fit={inliers:P0} motions={fittingMotions} axis={axisDirection:F2}@{axisPoint:F2}";
        }

        public sealed class Stats
        {
            public int pieces, rotations, translations, screws, planesOrSpheres, unfit, guided, regions;
            public long ms;
            public string detail = "";
            public override string ToString() =>
                $"kinematic: pieces={pieces} rotation={rotations} translation={translations} screw={screws} " +
                $"plane/sphere={planesOrSpheres} none={unfit} regions={regions} guided={guided} ({ms} ms){detail}";
        }

        /// <summary>A normal within this many degrees of perpendicular to the motion fits it.</summary>
        public static float inlierDegrees = 10f;

        /// <summary>Share of a piece's area that must fit a motion for the motion to count.</summary>
        public static float minInliers = 0.9f;

        /// <summary>Pieces under this many cells of area are left alone.</summary>
        public static float minPieceCells = 6f;

        /// <summary>Guides fade out within this many cells of a rotation's axis (where the rings shrink to a point).</summary>
        public static float axisFadeCells = 1f;

        /// <summary>
        /// Guide directions and weights for the orientation field: per vertex of each piece that a
        /// rotation, translation or screw carries into itself, the motion's direction there.
        /// </summary>
        public static void Guides(RemeshTopology topology, float cell, float weight,
            out Vector3[] directions, out float[] weights, out Stats stats, bool findRegions = true)
        {
            var regions = findRegions && KinematicSurfaces.regions;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stats = new Stats();
            var n = topology.vertexCount;
            directions = new Vector3[n];
            weights = new float[n];
            var positions = topology.positions;
            var normals = topology.normals;
            var triangles = topology.triangles;
            var area = VertexAreas(positions, triangles, n);
            if (regions)
            {
                s_Adjacency = new List<int>[n];
                for (var i = 0; i < n; i++) s_Adjacency[i] = new List<int>(6);
                for (var t = 0; t + 2 < triangles.Length; t += 3)
                    for (var k = 0; k < 3; k++)
                    {
                        int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                        if (!s_Adjacency[a].Contains(b)) s_Adjacency[a].Add(b);
                        if (!s_Adjacency[b].Contains(a)) s_Adjacency[b].Add(a);
                    }
            }
            foreach (var piece in Pieces(triangles, n))
            {
                double pieceArea = 0;
                foreach (var v in piece) pieceArea += area[v];
                if (pieceArea < minPieceCells * cell * cell) continue;
                stats.pieces++;
                var fit = FitPiece(piece, positions, normals, area);
                switch (fit.kind)
                {
                    case Kind.Rotation: stats.rotations++; break;
                    case Kind.Translation: stats.translations++; break;
                    case Kind.Screw: stats.screws++; break;
                    default:
                        if (fit.fittingMotions >= 3) stats.planesOrSpheres++; else stats.unfit++;
                        break;
                }
                if (stats.detail.Length < 600) stats.detail += $" [{piece.Count}v {fit}]";
                if (fit.kind == Kind.None)
                {
                    // Not one motion as a whole (a head, a horn on a curve, a building): look for
                    // regions that are -- a limb's rings, a horn's, a wall's rows.
                    if (regions && fit.fittingMotions < 3)
                        GrowRegions(piece, positions, normals, area, cell, weight, directions, weights, stats);
                    continue;
                }
                Apply(piece, fit, positions, normals, cell, weight, directions, weights, stats);
            }
            s_Adjacency = null;
            stats.ms = watch.ElapsedMilliseconds;
        }

        /// <summary>Find motion regions inside pieces that fit no motion as a whole.</summary>
        public static bool regions = true;

        /// <summary>A region seed's radius, in cells.</summary>
        public static float seedRadiusCells = 2f;

        /// <summary>Regions under this many cells of area are dropped.</summary>
        public static float minRegionCells = 12f;

        static void Apply(IList<int> vertices, Fit fit, Vector3[] positions, Vector3[] normals, float cell, float weight,
            Vector3[] directions, float[] weights, Stats stats)
        {
            foreach (var v in vertices)
            {
                var p = (positions[v] - fit.centre) / fit.scale;
                var velocity = fit.cbar + Vector3.Cross(fit.c, p);
                var normal = normals[v];
                var tangent = velocity - Vector3.Dot(velocity, normal) * normal;
                var length = tangent.magnitude;
                if (!(length > 1e-6f)) continue;
                var w = weight;
                if (fit.kind != Kind.Translation)
                {
                    // Near the axis the rings shrink to a point: no direction to give.
                    var radius = Vector3.Cross(positions[v] - fit.axisPoint, fit.axisDirection).magnitude;
                    w *= Mathf.Clamp01(radius / Mathf.Max(1e-9f, axisFadeCells * cell));
                }
                if (!(w > 0f)) continue;
                directions[v] = tangent / length;
                weights[v] = w;
                stats.guided++;
            }
        }

        // Region growing (after Hofer et al., segmentation by line geometry): a patch seedRadiusCells
        // across is fitted; if one motion fits it, it grows over the neighbours whose normals the
        // motion fits, refitted each time it doubles, and is kept if it ends large and still fitting.
        static void GrowRegions(List<int> piece, Vector3[] positions, Vector3[] normals, float[] area, float cell,
            float weight, Vector3[] directions, float[] weights, Stats stats)
        {
            var inPiece = new HashSet<int>(piece);
            var adjacency = s_Adjacency;
            var assigned = new HashSet<int>();
            var tried = new HashSet<int>();
            var limit = Mathf.Sin(regionInlierDegrees * Mathf.Deg2Rad);
            var radius = seedRadiusCells * cell;
            var saved = inlierDegrees;
            inlierDegrees = regionInlierDegrees;
            try
            {
            var stride = Mathf.Max(1, piece.Count / 4000);
            for (var s = 0; s < piece.Count; s += stride)
            {
                var seed = piece[s];
                if (assigned.Contains(seed) || tried.Contains(seed)) continue;
                // The seed patch: connected vertices within the radius.
                var patch = new List<int> { seed };
                var seen = new HashSet<int> { seed };
                for (var i = 0; i < patch.Count; i++)
                    foreach (var w in adjacency[patch[i]])
                        if (inPiece.Contains(w) && !assigned.Contains(w) && seen.Add(w)
                            && (positions[w] - positions[seed]).sqrMagnitude <= radius * radius)
                            patch.Add(w);
                foreach (var v in patch) tried.Add(v);
                if (patch.Count < 12) continue;
                var fit = FitPiece(patch, positions, normals, area);
                if (fit.kind == Kind.None) continue;
                // Grow.
                var region = new List<int>(patch);
                var member = new HashSet<int>(patch);
                var nextRefit = 2 * region.Count;
                for (var i = 0; i < region.Count; i++)
                {
                    foreach (var w in adjacency[region[i]])
                    {
                        if (!inPiece.Contains(w) || assigned.Contains(w) || member.Contains(w)) continue;
                        var p = (positions[w] - fit.centre) / fit.scale;
                        var velocity = fit.cbar + Vector3.Cross(fit.c, p);
                        var speed = velocity.magnitude;
                        if (!(speed > 1e-4f) || Mathf.Abs(Vector3.Dot(normals[w], velocity)) > limit * speed) continue;
                        member.Add(w);
                        region.Add(w);
                    }
                    if (region.Count >= nextRefit)
                    {
                        var refit = FitPiece(region, positions, normals, area);
                        if (refit.kind == Kind.None) break;
                        fit = refit;
                        nextRefit = 2 * region.Count;
                    }
                }
                double regionArea = 0;
                foreach (var v in region) regionArea += area[v];
                if (regionArea < minRegionCells * cell * cell) continue;
                var final = FitPiece(region, positions, normals, area);
                if (final.kind == Kind.None) continue;
                foreach (var v in region) { assigned.Add(v); tried.Add(v); }
                stats.regions++;
                if (stats.detail.Length < 600) stats.detail += $" region[{region.Count}v {final}]";
                Apply(region, final, positions, normals, cell, weight * regionWeight, directions, weights, stats);
            }
            }
            finally { inlierDegrees = saved; }
        }

        /// <summary>A region's normals must fit its motion within this many degrees (pieces: inlierDegrees).</summary>
        public static float regionInlierDegrees = 4f;

        /// <summary>A region's guides weigh this times a whole piece's.</summary>
        public static float regionWeight = 0.5f;

        [ThreadStatic] static List<int>[] s_Adjacency;

        /// <summary>The motion that best carries these vertices' surface into itself.</summary>
        public static Fit FitPiece(IList<int> piece, Vector3[] positions, Vector3[] normals, float[] area)
        {
            // A frame the six coordinates are comparable in: the piece centred and at unit size.
            var centre = Vector3.zero;
            double total = 0;
            foreach (var v in piece) { centre += area[v] * positions[v]; total += area[v]; }
            centre /= (float)Math.Max(total, 1e-30);
            double spread = 0;
            foreach (var v in piece) spread += area[v] * (positions[v] - centre).sqrMagnitude;
            var scale = (float)Math.Sqrt(spread / Math.Max(total, 1e-30));
            var fit = new Fit { centre = centre, scale = scale > 0f ? scale : 1f };

            var m = new double[6, 6];
            var l = new double[6];
            foreach (var v in piece)
            {
                var p = (positions[v] - centre) / fit.scale;
                var nn = normals[v];
                var pn = Vector3.Cross(p, nn);
                l[0] = pn.x; l[1] = pn.y; l[2] = pn.z; l[3] = nn.x; l[4] = nn.y; l[5] = nn.z;
                var w = area[v];
                for (var i = 0; i < 6; i++)
                    for (var j = 0; j < 6; j++)
                        m[i, j] += w * l[i] * l[j];
            }
            Jacobi(m, out var values, out var vectors);
            var order = new[] { 0, 1, 2, 3, 4, 5 };
            Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));

            // How many independent motions fit (they come smallest eigenvalue first).
            var fitting = new List<int>();
            foreach (var k in order)
            {
                var ck = new Vector3((float)vectors[0, k], (float)vectors[1, k], (float)vectors[2, k]);
                var bk = new Vector3((float)vectors[3, k], (float)vectors[4, k], (float)vectors[5, k]);
                var share = Inliers(piece, positions, normals, area, centre, fit.scale, ck, bk);
                if (share < minInliers) break;
                fitting.Add(k);
                if (fitting.Count == 1) fit.inliers = share;
            }
            fit.fittingMotions = fitting.Count;
            if (fitting.Count == 0 || fitting.Count >= 3) return fit;

            Vector3 c, cbar;
            if (fitting.Count == 1)
            {
                var k = fitting[0];
                c = new Vector3((float)vectors[0, k], (float)vectors[1, k], (float)vectors[2, k]);
                cbar = new Vector3((float)vectors[3, k], (float)vectors[4, k], (float)vectors[5, k]);
            }
            else
            {
                // A cylinder: of the turn-and-slide family a.e1 + b.e2, the pure turn (c . cbar = 0).
                int k1 = fitting[0], k2 = fitting[1];
                var c1 = new Vector3((float)vectors[0, k1], (float)vectors[1, k1], (float)vectors[2, k1]);
                var b1 = new Vector3((float)vectors[3, k1], (float)vectors[4, k1], (float)vectors[5, k1]);
                var c2 = new Vector3((float)vectors[0, k2], (float)vectors[1, k2], (float)vectors[2, k2]);
                var b2 = new Vector3((float)vectors[3, k2], (float)vectors[4, k2], (float)vectors[5, k2]);
                // (c1 + t c2) . (b1 + t b2) = 0
                double qa = Vector3.Dot(c2, b2), qb = Vector3.Dot(c1, b2) + Vector3.Dot(c2, b1), qc = Vector3.Dot(c1, b1);
                var best = -1.0;
                c = c1; cbar = b1;
                foreach (var t in Roots(qa, qb, qc))
                {
                    var ct = c1 + (float)t * c2;
                    var bt = b1 + (float)t * b2;
                    var norm = Math.Sqrt(ct.sqrMagnitude + bt.sqrMagnitude);
                    if (!(norm > 0)) continue;
                    var turn = ct.magnitude / norm;
                    if (turn > best) { best = turn; c = ct / (float)norm; cbar = bt / (float)norm; }
                }
                // The other root, or none: a cylinder seen as a slide along its axis is still right.
            }
            fit.c = c;
            fit.cbar = cbar;
            var cl = c.magnitude;
            var bl = cbar.magnitude;
            if (cl < 0.05f * bl)
            {
                fit.kind = Kind.Translation;
                fit.axisDirection = cbar.normalized;
                return fit;
            }
            // Axis of the motion: direction c, point a with cbar = a x c + pitch c.
            var pitch = Vector3.Dot(c, cbar) / (cl * cl);
            var a = Vector3.Cross(c, cbar - pitch * c) / (cl * cl);
            fit.axisDirection = c / cl;
            fit.axisPoint = centre + a * fit.scale;
            // Pitch in the normalised frame: a screw advancing more than a tenth of the piece's size per radian.
            fit.kind = Mathf.Abs(pitch) < 0.1f ? Kind.Rotation : Kind.Screw;
            if (fit.kind == Kind.Rotation) fit.cbar = cbar - pitch * c;
            return fit;
        }

        static IEnumerable<double> Roots(double a, double b, double c)
        {
            if (Math.Abs(a) < 1e-12)
            {
                if (Math.Abs(b) > 1e-12) yield return -c / b;
                yield break;
            }
            var d = b * b - 4 * a * c;
            if (d < 0) yield break;
            var s = Math.Sqrt(d);
            yield return (-b + s) / (2 * a);
            yield return (-b - s) / (2 * a);
        }

        static float Inliers(IList<int> piece, Vector3[] positions, Vector3[] normals, float[] area,
            Vector3 centre, float scale, Vector3 c, Vector3 cbar)
        {
            var limit = Mathf.Sin(inlierDegrees * Mathf.Deg2Rad);
            double inside = 0, total = 0, moving = 0;
            foreach (var v in piece)
            {
                var p = (positions[v] - centre) / scale;
                var velocity = cbar + Vector3.Cross(c, p);
                var speed = velocity.magnitude;
                total += area[v];
                if (!(speed > 1e-4f)) continue; // on the axis: says nothing either way
                moving += area[v];
                if (Mathf.Abs(Vector3.Dot(normals[v], velocity)) <= limit * speed) inside += area[v];
            }
            // A motion that leaves most of the piece still (an axis through it) is not its motion.
            if (moving < 0.5 * total) return 0f;
            return total > 0 ? (float)(inside / total) : 0f;
        }

        static float[] VertexAreas(Vector3[] positions, int[] triangles, int n)
        {
            var area = new float[n];
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                var third = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).magnitude / 6f;
                area[a] += third; area[b] += third; area[c] += third;
            }
            return area;
        }

        static List<List<int>> Pieces(int[] triangles, int n)
        {
            var parent = new int[n];
            for (var i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }
            var used = new bool[n];
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                Union(triangles[t], triangles[t + 1]);
                Union(triangles[t], triangles[t + 2]);
                used[triangles[t]] = used[triangles[t + 1]] = used[triangles[t + 2]] = true;
            }
            var byRoot = new Dictionary<int, List<int>>();
            for (var v = 0; v < n; v++)
            {
                if (!used[v]) continue;
                var r = Find(v);
                if (!byRoot.TryGetValue(r, out var list)) byRoot[r] = list = new List<int>();
                list.Add(v);
            }
            return new List<List<int>>(byRoot.Values);
        }

        // Eigen-decomposition of a symmetric matrix by cyclic Jacobi rotations.
        static void Jacobi(double[,] a, out double[] values, out double[,] vectors)
        {
            var size = a.GetLength(0);
            var m = (double[,])a.Clone();
            vectors = new double[size, size];
            for (var i = 0; i < size; i++) vectors[i, i] = 1;
            for (var sweep = 0; sweep < 60; sweep++)
            {
                double off = 0;
                for (var p = 0; p < size; p++)
                    for (var q = p + 1; q < size; q++) off += m[p, q] * m[p, q];
                if (off < 1e-22) break;
                for (var p = 0; p < size; p++)
                    for (var q = p + 1; q < size; q++)
                    {
                        if (Math.Abs(m[p, q]) < 1e-300) continue;
                        var theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                        var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        var cs = 1 / Math.Sqrt(t * t + 1);
                        var sn = t * cs;
                        for (var k = 0; k < size; k++)
                        {
                            var mkp = m[k, p]; var mkq = m[k, q];
                            m[k, p] = cs * mkp - sn * mkq;
                            m[k, q] = sn * mkp + cs * mkq;
                        }
                        for (var k = 0; k < size; k++)
                        {
                            var mpk = m[p, k]; var mqk = m[q, k];
                            m[p, k] = cs * mpk - sn * mqk;
                            m[q, k] = sn * mpk + cs * mqk;
                        }
                        for (var k = 0; k < size; k++)
                        {
                            var vkp = vectors[k, p]; var vkq = vectors[k, q];
                            vectors[k, p] = cs * vkp - sn * vkq;
                            vectors[k, q] = sn * vkp + cs * vkq;
                        }
                    }
            }
            values = new double[size];
            for (var i = 0; i < size; i++) values[i] = m[i, i];
        }
    }
}
