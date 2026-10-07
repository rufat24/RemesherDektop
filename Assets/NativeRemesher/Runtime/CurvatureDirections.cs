// Principal curvature directions, as guides for the cross field.
//
// WHY. A quad mesh reads well when its edges run along the directions the surface bends in: rings
// round an arm, round the helmet's dome, along a strap. The cross field this pipeline solves has no
// term for that -- it is the smoothest field the normals allow, anchored only by creases -- so on a
// smooth organic surface its orientation is whatever the smoothing happened to settle on, and it is
// free to twist across a cylinder where the shape plainly asks for rings. Exoside's QuadRemesher
// (the "FreeRemesher" the user supplied) carries a class named MXCurvatureDirAlgo for exactly this,
// alongside the smoothing (SmoothVF) and the integer parametrisation this repo already ports.
//
// HOW. Rusinkiewicz's estimator (2004): per triangle, the 2x2 shape operator that best maps each of
// its three edges to the change in vertex normal along that edge, fitted by least squares in the
// triangle's own frame; each vertex then averages its triangles' operators by area. The
// eigenvectors are the principal directions and the eigenvalue gap is |k1 - k2|. One-ring
// estimates on a sculpt or a scan are noise, so the tensors are averaged over about one output cell
// before the direction is read off -- the scale at which the quads will actually express the shape.
//
// NOT TAUBIN'S, which this first used, and the reason is a bias rather than noise. Taubin sums the
// normal curvature along every edge of the one-ring, so it is only as isotropic as the one-ring's
// edge directions are -- and on a regular triangulation every vertex's two diagonal edges lean the
// SAME way. Measured on a lat/long cylinder: every wall vertex's direction came out 17.5 degrees off
// the ring, the same way everywhere, so averaging could not remove it, and the unit test found 0 of
// 912 wall vertices aligned. On an irregular sculpt the same bias becomes direction noise, which is
// what the first measurements of this feature were measuring. The per-triangle fit is exact where
// the normal varies linearly, which a cylinder's does: gap 1.99 against 1/R = 2.0, direction on the
// axis to machine precision.
//
// WEIGHT. Only anisotropic bending says anything about direction: a flat patch or a sphere
// (umbilic, k1 = k2) has no preferred one, and pulling the field there would only fight the
// smoothing. So each guide is weighted by how much the two principal curvatures differ across one
// cell, (k1 - k2) * cell, ramped from nothing below a turn of about 0.1 rad per cell to full
// strength at 0.4.

using System;
using UnityEngine;

namespace NativeRemesher
{
    public static class CurvatureDirections
    {
        public sealed class Stats
        {
            public int vertices;
            public int guided;
            public int smoothingSweeps;
            public float meanWeight;
            public long ms;

            public override string ToString() =>
                $"curvature guides: {guided}/{vertices} vertices, mean weight {meanWeight:F3}, " +
                $"{smoothingSweeps} tensor sweeps ({ms} ms)";
        }

        /// <summary>Anisotropy, per cell, below which a vertex gets no guide.</summary>
        public static float anisotropyStart = 0.1f;

        /// <summary>Anisotropy, per cell, at which a guide reaches full strength.</summary>
        public static float anisotropyFull = 0.4f;

        /// <summary>
        /// Guide directions and weights for <see cref="OrientationField.Solve"/>.
        /// </summary>
        /// <param name="cell">Output lattice spacing; sets both the averaging radius and the weight scale.</param>
        /// <param name="strength">Weight at full anisotropy, in the solver's units (a multiple of degree).</param>
        /// <param name="radiusCells">Tensor averaging radius, in cells.</param>
        public static void Compute(RemeshTopology topology, float cell, float strength,
            float radiusCells, out Vector3[] directions, out float[] weights, out Stats stats)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var count = topology.vertexCount;
            var normals = topology.normals;
            var tensor = ShapeTensors(topology, radiusCells * cell, out var sweeps);

            directions = new Vector3[count];
            weights = new float[count];
            var guided = 0;
            double weightSum = 0;

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

                var mu = Apply(u);
                var mv = Apply(v);
                var m11 = Vector3.Dot(u, mu);
                var m12 = Vector3.Dot(u, mv);
                var m22 = Vector3.Dot(v, mv);

                // Eigenvalue gap of the 2x2 tangent block: |k1 - k2| for a shape operator.
                var half = 0.5f * (m11 - m22);
                var gap = 2f * Mathf.Sqrt(half * half + m12 * m12);
                var anisotropy = gap * cell;
                var ramp = Mathf.Clamp01((anisotropy - anisotropyStart) / (anisotropyFull - anisotropyStart));
                if (ramp <= 0f) continue;

                var angle = 0.5f * Mathf.Atan2(2f * m12, m11 - m22);
                directions[i] = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
                weights[i] = strength * ramp;
                guided++;
                weightSum += weights[i];
            }

            stats = new Stats
            {
                vertices = count,
                guided = guided,
                smoothingSweeps = sweeps,
                meanWeight = guided > 0 ? (float)(weightSum / guided) : 0f,
                ms = watch.ElapsedMilliseconds
            };
        }

        /// <summary>
        /// Per-vertex shape operator as a symmetric 3x3 tensor, six floats per vertex (xx, xy, xz,
        /// yy, yz, zz), averaged over about <paramref name="radius"/> of surface. Shared with
        /// <see cref="CurveNetwork"/>, which reads ridges off the same estimate.
        /// </summary>
        public static float[] ShapeTensors(RemeshTopology topology, float radius, out int sweeps)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            var count = topology.vertexCount;
            var positions = topology.positions;
            var normals = topology.normals;
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;
            var areas = topology.areas;

            // Symmetric 3x3 tensors, six floats each: xx, xy, xz, yy, yz, zz.
            var tensor = new float[count * 6];
            var triangles = topology.triangles;
            var faceCount = triangles.Length / 3;
            var faceTensor = new float[faceCount * 6];
            var faceArea = new float[faceCount];

            System.Threading.Tasks.Parallel.For(0, faceCount, f =>
            {
                var i0 = triangles[3 * f];
                var i1 = triangles[3 * f + 1];
                var i2 = triangles[3 * f + 2];
                var p0 = positions[i0];
                var p1 = positions[i1];
                var p2 = positions[i2];
                var cross = Vector3.Cross(p1 - p0, p2 - p0);
                var doubleArea = cross.magnitude;
                if (doubleArea <= 1e-20f) return;
                var fn = cross / doubleArea;
                var u = p1 - p0;
                var ul = u.magnitude;
                if (ul <= 1e-20f) return;
                u /= ul;
                var v = Vector3.Cross(fn, u);

                // Normal equations of the 6x3 system [e.u e.v 0; 0 e.u e.v] (a b c) = (dn.u, dn.v).
                double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, r0 = 0, r1 = 0, r2 = 0;
                for (var k = 0; k < 3; k++)
                {
                    var from = triangles[3 * f + k];
                    var to = triangles[3 * f + (k + 1) % 3];
                    var e = positions[to] - positions[from];
                    var dn = normals[to] - normals[from];
                    double eu = Vector3.Dot(e, u), ev = Vector3.Dot(e, v);
                    double du = Vector3.Dot(dn, u), dv = Vector3.Dot(dn, v);
                    // Row (eu, ev, 0) -> du and row (0, eu, ev) -> dv.
                    a00 += eu * eu; a01 += eu * ev; a11 += ev * ev + eu * eu;
                    a12 += eu * ev; a22 += ev * ev;
                    r0 += eu * du; r1 += ev * du + eu * dv; r2 += ev * dv;
                }

                // 3x3 symmetric solve by Cramer's rule; a degenerate triangle has no fit.
                var det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02)
                          + a02 * (a01 * a12 - a11 * a02);
                if (Math.Abs(det) <= 1e-30) return;
                var sa = (r0 * (a11 * a22 - a12 * a12) - a01 * (r1 * a22 - a12 * r2)
                          + a02 * (r1 * a12 - a11 * r2)) / det;
                var sb = (a00 * (r1 * a22 - a12 * r2) - r0 * (a01 * a22 - a12 * a02)
                          + a02 * (a01 * r2 - r1 * a02)) / det;
                var sc = (a00 * (a11 * r2 - r1 * a12) - a01 * (a01 * r2 - r1 * a02)
                          + r0 * (a01 * a12 - a11 * a02)) / det;

                // Embedded in 3D: S = a uu' + b (uv' + vu') + c vv'.
                float Entry(float x1, float y1, float x2, float y2) =>
                    (float)(sa * x1 * x2 + sb * (x1 * y2 + y1 * x2) + sc * y1 * y2);
                var o = f * 6;
                faceTensor[o] = Entry(u.x, v.x, u.x, v.x);
                faceTensor[o + 1] = Entry(u.x, v.x, u.y, v.y);
                faceTensor[o + 2] = Entry(u.x, v.x, u.z, v.z);
                faceTensor[o + 3] = Entry(u.y, v.y, u.y, v.y);
                faceTensor[o + 4] = Entry(u.y, v.y, u.z, v.z);
                faceTensor[o + 5] = Entry(u.z, v.z, u.z, v.z);
                faceArea[f] = 0.5f * doubleArea;
            });

            var vertexArea = new float[count];
            for (var f = 0; f < faceCount; f++)
            {
                var area = faceArea[f];
                if (area <= 0f) continue;
                for (var k = 0; k < 3; k++)
                {
                    var vtx = triangles[3 * f + k];
                    vertexArea[vtx] += area;
                    for (var c = 0; c < 6; c++) tensor[vtx * 6 + c] += area * faceTensor[f * 6 + c];
                }
            }
            for (var vtx = 0; vtx < count; vtx++)
                if (vertexArea[vtx] > 0f)
                    for (var c = 0; c < 6; c++) tensor[vtx * 6 + c] /= vertexArea[vtx];

            // Averaged over about `radius`: diffusion reaches sqrt(sweeps) edges.
            var edge = topology.averageEdgeLength;
            var reach = edge > 0f ? radius / edge : 0f;
            sweeps = Mathf.Clamp(Mathf.RoundToInt(reach * reach), 0, 2000);
            var next = new float[tensor.Length];
            for (var sweep = 0; sweep < sweeps; sweep++)
            {
                var from = tensor;
                var to = next;
                System.Threading.Tasks.Parallel.For(0, count, i =>
                {
                    var o = i * 6;
                    var w = areas[i];
                    var total = w;
                    for (var k = 0; k < 6; k++) to[o + k] = from[o + k] * w;
                    for (var a = start[i]; a < start[i + 1]; a++)
                    {
                        var j = adjacency[a];
                        var wj = areas[j];
                        total += wj;
                        var oj = j * 6;
                        for (var k = 0; k < 6; k++) to[o + k] += from[oj + k] * wj;
                    }
                    if (total > 0f)
                        for (var k = 0; k < 6; k++) to[o + k] /= total;
                });
                (tensor, next) = (next, tensor);
            }

            return tensor;
        }
    }
}
