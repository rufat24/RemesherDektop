// Quadric error metric decimation, as a pre-stage.
//
// Garland & Heckbert, "Surface Simplification Using Quadric Error Metrics", SIGGRAPH 1997.
//
// WHY THIS IS HERE, because it is not obvious and it is not primarily about speed.
//
// Field-aligned remeshing samples the lattice at the input vertices, and its output quality depends
// on the ratio of the requested lattice spacing to the input's average edge length — not on the face
// count. Measured across the corpus, quad fraction holds at 85-98% while that ratio is around 2x and
// collapses to 20-49% by 3.3x (see RemeshTopology.maxUsefulSpacingMultiple). So asking a dense mesh
// for very few quads is asking it to work far outside the band where it works at all.
//
// The way out is to raise the INPUT's edge length instead of the requested spacing. Decimating first
// puts the ratio back in band whatever the final face count, which is what makes aggressive
// decimation — the whole point of the tool — produce good topology rather than mush.
//
// It is also the photogrammetry path's main tool in its own right, since scan input arrives far
// denser than the solver wants to chew.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public sealed class DecimateOptions
    {
        /// <summary>Stop once the mesh is at or below this many triangles.</summary>
        public int targetTriangles;

        /// <summary>
        /// Refuse a collapse that would rotate any surviving face's normal by more than this.
        /// Guards against folding the surface through itself, which the quadric cost alone does not
        /// see: a fold can be cheap in a least-squares sense and catastrophic geometrically.
        /// </summary>
        public float maxNormalFlipDegrees = 75f;

        /// <summary>
        /// Weight of the virtual planes erected along boundary edges. Boundaries carry the
        /// silhouette, and without this the quadric happily eats them because an open edge has fewer
        /// planes constraining it than an interior one.
        /// </summary>
        public float boundaryWeight = 1000f;

        /// <summary>
        /// Triangle labels (materials) whose borders are kept: a border vertex moves only along its
        /// border, onto the next border vertex, and where three labels meet it stays. Null: none.
        /// </summary>
        public MaterialLabels labels;

        /// <summary>Weight of the planes along label borders, against a face's own plane (both by
        /// area: length squared). Low: a material border on a dense input zigzags triangle by
        /// triangle, and a heavy weight keeps every zig at full density.</summary>
        public float labelBorderWeight = 1f;

        public static DecimateOptions ForTriangleCount(int triangles) =>
            new DecimateOptions { targetTriangles = triangles };
    }

    public sealed class DecimateStats
    {
        public int inputTriangles;
        public int outputTriangles;
        public int inputVertices;
        public int outputVertices;

        public int collapsesApplied;
        public int rejectedTopology;
        public int rejectedNormalFlip;
        public int rejectedBoundary;
        public int rejectedLabel, labelSlides, labelBorderStart;

        public float maxErrorApplied;

        /// <summary>
        /// Where the rejections come from, split by which target the candidate carried.
        /// </summary>
        /// <remarks>
        /// The unsplit counter cannot distinguish two opposite diagnoses. A mesh that is genuinely
        /// hard to decimate rejects flips on both kinds of target; a mesh whose quadrics are
        /// ill-conditioned rejects almost exclusively on solved ones, because the poorly determined
        /// direction of a near-planar system lies IN the tangent plane, so the optimum slides
        /// sideways across the one-ring and inverts the faces on its far side. Same total, opposite
        /// fix. This project has already paid twice for a counter that aggregated away the answer.
        /// </remarks>
        public int solvedTargets;
        public int fallbackTargets;
        public int rejectedFlipSolved;
        public int rejectedFlipFallback;
        public int driftRejected;

        /// <summary>Drift of the solved point from the edge midpoint, in edge lengths.</summary>
        public double driftSum;
        public float driftMax;

        /// <summary>Per-phase milliseconds. Left on permanently: three of this project's
        /// misdiagnoses came from a stage whose cost nothing measured.</summary>
        public string timings;

        /// <summary>Adds another run's collapse counters into this one.</summary>
        internal void Accumulate(DecimateStats o)
        {
            collapsesApplied += o.collapsesApplied;
            rejectedTopology += o.rejectedTopology;
            rejectedNormalFlip += o.rejectedNormalFlip;
            rejectedBoundary += o.rejectedBoundary;
            rejectedLabel += o.rejectedLabel;
            labelSlides += o.labelSlides;
            maxErrorApplied = Mathf.Max(maxErrorApplied, o.maxErrorApplied);
            solvedTargets += o.solvedTargets;
            fallbackTargets += o.fallbackTargets;
            rejectedFlipSolved += o.rejectedFlipSolved;
            rejectedFlipFallback += o.rejectedFlipFallback;
            driftRejected += o.driftRejected;
            driftSum += o.driftSum;
            driftMax = Mathf.Max(driftMax, o.driftMax);
        }

        public override string ToString()
        {
            return $"{inputTriangles:N0} -> {outputTriangles:N0} tris " +
                   $"({inputVertices:N0} -> {outputVertices:N0} verts), " +
                   $"collapses={collapsesApplied} rejTopo={rejectedTopology} " +
                   $"rejFlip={rejectedNormalFlip}(solved={rejectedFlipSolved} " +
                   $"fallback={rejectedFlipFallback}) rejBoundary={rejectedBoundary} rejLabel={rejectedLabel} labelSlides={labelSlides} labelBorderStart={labelBorderStart} " +
                   $"targets={solvedTargets}solved/{fallbackTargets}fallback " +
                   $"drift={(solvedTargets + driftRejected > 0 ? driftSum / (solvedTargets + driftRejected) : 0):F2}mean/" +
                   $"{driftMax:F2}max edgeLengths rejByDrift={driftRejected} " +
                   $"maxError={maxErrorApplied:E2}" +
                   (string.IsNullOrEmpty(timings) ? "" : $" [{timings}]");
        }
    }

    public static class PreDecimate
    {
        /// <summary>
        /// Take a dense input most of the way down in parallel spatial blocks, then finish with the
        /// global collapse loop. Off is the single global loop for the whole reduction.
        /// </summary>
        /// <remarks>
        /// The collapse loop is inherently sequential -- one heap, one cheapest edge at a time -- and
        /// on a scan it was the largest single stage left: 6.0 of 7.0 s decimating the 1.5M-triangle
        /// body to 50k, 5.0 s on ccnb's 1M, on one core while the rest of the pipeline is parallel.
        /// Here the triangles are cut into blocks along a Morton curve of their centroids, which
        /// keeps each block spatially compact and every block the same size. Each block is
        /// decimated on its own thread with the vertices it shares with another block locked, so
        /// no two threads ever touch the same vertex or face, down to <see cref="parallelStageRatio"/>
        /// times the target. The global loop then takes the remaining factor, and with it the seams
        /// the locks left dense and any imbalance between blocks, in the order it always used.
        /// </remarks>
        public static bool parallelBlocks = true;

        /// <summary>
        /// Label-border planes weighted by length squared, as a face's plane is by area (true), or
        /// by length, as the open border's (false: at fine input scale that is a thousand times a
        /// face, and the border keeps nearly every input vertex).
        /// </summary>
        public static bool labelWeightByArea = true;

        /// <summary>The parallel stage stops at this multiple of the target triangle count.</summary>
        public static float parallelStageRatio = 4f;

        /// <summary>Inputs below this many triangles skip the parallel stage.</summary>
        public static int parallelMinTriangles = 200_000;

        /// <summary>Blocks in the parallel stage; several per core, so they balance.</summary>
        public static int parallelBlockCount = 64;

        /// <summary>
        /// Symmetric 4x4 quadric, stored as its 10 distinct entries. Error at a point p is
        /// p^T Q p with p in homogeneous coordinates.
        /// </summary>
        struct Quadric
        {
            public double a2, ab, ac, ad;
            public double b2, bc, bd;
            public double c2, cd;
            public double d2;

            public static Quadric FromPlane(double a, double b, double c, double d, double weight)
            {
                return new Quadric
                {
                    a2 = a * a * weight, ab = a * b * weight, ac = a * c * weight, ad = a * d * weight,
                    b2 = b * b * weight, bc = b * c * weight, bd = b * d * weight,
                    c2 = c * c * weight, cd = c * d * weight,
                    d2 = d * d * weight
                };
            }

            public void Add(in Quadric o)
            {
                a2 += o.a2; ab += o.ab; ac += o.ac; ad += o.ad;
                b2 += o.b2; bc += o.bc; bd += o.bd;
                c2 += o.c2; cd += o.cd;
                d2 += o.d2;
            }

            public static Quadric Sum(in Quadric x, in Quadric y)
            {
                var q = x;
                q.Add(y);
                return q;
            }

            public double Error(Vector3 p)
            {
                double x = p.x, y = p.y, z = p.z;
                return a2 * x * x + 2 * ab * x * y + 2 * ac * x * z + 2 * ad * x
                       + b2 * y * y + 2 * bc * y * z + 2 * bd * y
                       + c2 * z * z + 2 * cd * z
                       + d2;
            }

            /// <summary>
            /// The point minimising the error, or false when the system is singular — which happens
            /// on a flat or symmetric neighbourhood, where any point along a line or plane is
            /// equally good and the caller should fall back to something well defined.
            /// </summary>

            /// <summary>
            /// Minimises the quadric along the segment from <paramref name="from"/> to
            /// <paramref name="to"/>, clamped to the segment.
            /// </summary>
            /// <remarks>
            /// This is the right fallback when the unconstrained 3x3 solve is not trustworthy, and
            /// on a densely tessellated surface that is the normal case rather than the exception:
            /// a vertex whose incident planes are nearly parallel has a genuinely near-singular
            /// matrix, and no threshold makes it solvable. Restricting to the edge turns it into a
            /// one-dimensional problem that is always well posed for a positive semi-definite
            /// quadric, and the answer cannot leave the edge, so it cannot invert the one-ring in
            /// the way an unconstrained optimum drifting sideways does.
            ///
            /// Strictly better than picking the best of the two endpoints and the midpoint, which
            /// is what this replaced: those three are samples of exactly this function, so the
            /// minimum over the segment is at least as good as the best of them by construction.
            ///
            /// Writing E(p) = p^T A p + 2 b^T p + c and p(t) = from + t*d,
            ///     E(t) = E(from) + 2t * d.(A*from + b) + t^2 * d.(A*d),
            /// so t* = -d.(A*from + b) / d.(A*d). The denominator is the quadric's curvature along
            /// the edge and vanishes exactly when the quadric is flat in that direction, where
            /// every t is equally good and the midpoint is the least surprising choice.
            /// </remarks>
            public Vector3 OptimalOnSegment(Vector3 from, Vector3 to)
            {
                double fx = from.x, fy = from.y, fz = from.z;
                double dx = to.x - fx, dy = to.y - fy, dz = to.z - fz;

                // A*from + b
                var gx = a2 * fx + ab * fy + ac * fz + ad;
                var gy = ab * fx + b2 * fy + bc * fz + bd;
                var gz = ac * fx + bc * fy + c2 * fz + cd;

                // A*d
                var adx = a2 * dx + ab * dy + ac * dz;
                var ady = ab * dx + b2 * dy + bc * dz;
                var adz = ac * dx + bc * dy + c2 * dz;

                var curvature = dx * adx + dy * ady + dz * adz;
                var slope = dx * gx + dy * gy + dz * gz;

                var t = curvature > 0.0 ? -slope / curvature : 0.5;
                if (!(t > 0.0)) t = 0.0;          // also catches NaN
                else if (t > 1.0) t = 1.0;

                return new Vector3(
                    (float)(fx + t * dx),
                    (float)(fy + t * dy),
                    (float)(fz + t * dz));
            }
            public bool TryOptimalPoint(out Vector3 point)
            {
                // Solve the 3x3 system A v = -b by Cramer's rule.
                var det =
                    a2 * (b2 * c2 - bc * bc)
                    - ab * (ab * c2 - bc * ac)
                    + ac * (ab * bc - b2 * ac);

                // Scale-aware threshold: the determinant of a 3x3 built from squared plane normals
                // has cubic units, so comparing it to a fixed epsilon behaves differently on a
                // millimetre mesh and a kilometre one.
                //
                // The RELATIVE bound is the part that matters, and 1e-10 was far too generous to be
                // a conditioning test at all. Area-weighted planes on the reference scan give matrix
                // entries near 5.6e-7, so a well-conditioned determinant is about 1.8e-19 while that
                // threshold sat at 4.9e-28 — nine orders of magnitude of hopelessly ill-conditioned
                // systems reported themselves solvable, and the optimum landed a mean of 2.2e8 edge
                // lengths from the edge it was replacing. 1e-6 bounds the condition number near 1e6,
                // about as far as a double solve of this system is worth trusting, and what it now
                // refuses falls through to the segment minimum, which is a better answer anyway.
                var magnitude = Math.Abs(a2) + Math.Abs(b2) + Math.Abs(c2) + 1e-30;
                if (Math.Abs(det) < 1e-6 * magnitude * magnitude * magnitude)
                {
                    point = Vector3.zero;
                    return false;
                }

                var invDet = 1.0 / det;

                var x = -invDet * (
                    ad * (b2 * c2 - bc * bc)
                    - bd * (ab * c2 - bc * ac)
                    + cd * (ab * bc - b2 * ac));

                var y = -invDet * (
                    a2 * (bd * c2 - cd * bc)
                    - ab * (ad * c2 - cd * ac)
                    + ac * (ad * bc - bd * ac));

                var z = -invDet * (
                    a2 * (b2 * cd - bd * bc)
                    - ab * (ab * cd - bd * ac)
                    + ac * (ab * bd - b2 * ac));

                point = new Vector3((float)x, (float)y, (float)z);
                return !float.IsNaN(point.x) && !float.IsInfinity(point.x)
                       && !float.IsNaN(point.y) && !float.IsInfinity(point.y)
                       && !float.IsNaN(point.z) && !float.IsInfinity(point.z);
            }
        }

        /// <summary>
        /// Vertex to incident triangles: one flat arena with per-vertex slack, replacing an array
        /// of <see cref="HashSet{T}"/>.
        /// </summary>
        /// <remarks>
        /// Two costs made the hash-set version the wrong shape here, and both show up only at scale.
        ///
        /// One object per vertex means one allocation per vertex — half a million of them on a
        /// photogrammetry scan, each with its own bucket and slot arrays, before a single collapse
        /// has been considered. And every scan in the collapse loop then chases pointers through
        /// unrelated regions of the heap, on a structure whose live contents are six integers.
        ///
        /// This holds every ring in one contiguous int[]. Rings are laid out at their exact initial
        /// degree plus a little slack, so the common case — a survivor inheriting a handful of
        /// faces — never reallocates. A ring that outgrows its slack is pruned first and only then
        /// relocated to the end of the arena at double capacity, which is rare enough that the
        /// abandoned space is not worth reclaiming.
        /// </remarks>
        sealed class Incidence
        {
            readonly int[] m_Start;
            readonly int[] m_Count;
            readonly int[] m_Capacity;
            int[] m_Data;
            int m_Top;

            /// <summary>Spare slots per vertex, absorbing inherited faces without a relocation.</summary>
            const int k_Slack = 4;

            public Incidence(int vertexCount, int[] triangles, int triangleCount)
            {
                m_Start = new int[vertexCount];
                m_Count = new int[vertexCount];
                m_Capacity = new int[vertexCount];

                for (var t = 0; t < triangleCount; t++)
                {
                    m_Capacity[triangles[t * 3]]++;
                    m_Capacity[triangles[t * 3 + 1]]++;
                    m_Capacity[triangles[t * 3 + 2]]++;
                }

                var running = 0;
                for (var v = 0; v < vertexCount; v++)
                {
                    m_Capacity[v] += k_Slack;
                    m_Start[v] = running;
                    running += m_Capacity[v];
                }

                m_Data = new int[running];
                m_Top = running;

                for (var t = 0; t < triangleCount; t++)
                {
                    Append(triangles[t * 3], t);
                    Append(triangles[t * 3 + 1], t);
                    Append(triangles[t * 3 + 2], t);
                }
            }

            public int Count(int v) => m_Count[v];

            public int Get(int v, int i) => m_Data[m_Start[v] + i];

            public void Clear(int v) => m_Count[v] = 0;

            void Append(int v, int t) => m_Data[m_Start[v] + m_Count[v]++] = t;

            public void Add(int v, int t, bool[] triAlive)
            {
                if (m_Count[v] == m_Capacity[v])
                {
                    Prune(v, triAlive);
                    if (m_Count[v] == m_Capacity[v]) Relocate(v);
                }
                Append(v, t);
            }

            /// <summary>Compacts dead triangles out of one ring, in place.</summary>
            public void Prune(int v, bool[] triAlive)
            {
                var start = m_Start[v];
                var n = m_Count[v];
                var write = 0;

                for (var i = 0; i < n; i++)
                {
                    var t = m_Data[start + i];
                    if (triAlive[t]) m_Data[start + write++] = t;
                }

                m_Count[v] = write;
            }

            void Relocate(int v)
            {
                var capacity = m_Capacity[v] * 2;

                if (m_Top + capacity > m_Data.Length)
                {
                    var grown = new int[Mathf.Max(m_Data.Length * 2, m_Top + capacity)];
                    Array.Copy(m_Data, grown, m_Top);
                    m_Data = grown;
                }

                Array.Copy(m_Data, m_Start[v], m_Data, m_Top, m_Count[v]);
                m_Start[v] = m_Top;
                m_Capacity[v] = capacity;
                m_Top += capacity;
            }
        }

        struct Candidate
        {
            public int u, v;
            public Vector3 target;
            public double cost;
            public int versionU, versionV;

            /// <summary>True when the target came from the 3x3 quadric solve rather than a fallback.</summary>
            public bool solvedTarget;
        }

        /// <summary>
        /// Decimates a mesh and returns a new one. Convenience wrapper; the array overload is what
        /// the pipeline uses, because a <see cref="Mesh"/> can only be created on the main thread.
        /// </summary>
        public static Mesh Decimate(Mesh source, DecimateOptions options, out DecimateStats stats)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            RemeshTopology.ReadMesh(source, out var positions, out var triangles);
            Decimate(positions, triangles, options, out stats, out var outPositions, out var outTriangles);

            return BuildMesh(source.name + "_Decimated", outPositions, outTriangles);
        }

        /// <summary>
        /// Decimates plain arrays. Touches no Unity object, so it is safe on a worker thread.
        /// </summary>
        public static void Decimate(Vector3[] sourcePositions, int[] sourceTriangles,
            DecimateOptions options, out DecimateStats stats,
            out Vector3[] outPositions, out int[] outTriangles, Action<float> onProgress = null) =>
            Decimate(sourcePositions, sourceTriangles, options, out stats, out outPositions,
                out outTriangles, onProgress, parallelBlocks);

        static void Decimate(Vector3[] sourcePositions, int[] sourceTriangles,
            DecimateOptions options, out DecimateStats stats,
            out Vector3[] outPositions, out int[] outTriangles, Action<float> onProgress,
            bool allowParallel)
        {
            if (sourcePositions == null) throw new ArgumentNullException(nameof(sourcePositions));
            options ??= new DecimateOptions();

            // Work on a welded graph. Decimating a seam-split render mesh would pull the seams
            // apart, because the duplicated vertices at a seam are not connected to each other and
            // nothing keeps their collapses in step.
            var phase = System.Diagnostics.Stopwatch.StartNew();
            long msTopology, msIncident, msQuadrics, msSeed, msCollapse;
            var topology = RemeshTopology.Build(sourcePositions, sourceTriangles);
            msTopology = phase.ElapsedMilliseconds; phase.Restart();

            var positions = (Vector3[])topology.positions.Clone();
            var triangles = (int[])topology.triangles.Clone();
            var triangleCount = triangles.Length / 3;
            var triLabel = options.labels != null && options.labels.labelCount > 1
                ? options.labels.ForTopology(topology, sourceTriangles)
                : null;


            stats = new DecimateStats
            {
                inputTriangles = triangleCount,
                inputVertices = positions.Length
            };
            if (triLabel != null)
            {
                var lb = LabelBorderVertices(topology, triLabel);
                foreach (var b in lb) if (b) stats.labelBorderStart++;
            }

            var target = Mathf.Max(4, options.targetTriangles);
            if (triangleCount <= target)
            {
                stats.outputTriangles = triangleCount;
                stats.outputVertices = positions.Length;
                outPositions = positions;
                outTriangles = triangles;
                return;
            }

            var stageTarget = Mathf.RoundToInt(target * Mathf.Max(1f, parallelStageRatio));
            if (allowParallel && triangleCount >= parallelMinTriangles && triangleCount > 2 * stageTarget)
            {
                var stageQuadrics = BuildQuadrics(topology, positions, triangles, triangleCount,
                    options.boundaryWeight, triLabel, options.labelBorderWeight);
                msQuadrics = phase.ElapsedMilliseconds; phase.Restart();

                var progressLock = new object();
                ParallelStage(topology, positions, triangles, stageQuadrics, stageTarget, options,
                    stats, LabelBorderVertices(topology, triLabel), out var stagePositions, out var stageTriangles,
                    onProgress == null ? null : f => { lock (progressLock) onProgress(0.7f * f); });
                var msParallel = phase.ElapsedMilliseconds;

                Decimate(stagePositions, stageTriangles, options, out var finish, out outPositions,
                    out outTriangles, onProgress == null ? null : f => onProgress(0.7f + 0.3f * f), false);
                stats.Accumulate(finish);
                stats.outputTriangles = finish.outputTriangles;
                stats.outputVertices = finish.outputVertices;
                stats.timings = $"topo={msTopology} quadrics={msQuadrics} parallel={msParallel} " +
                                $"({stageTriangles.Length / 3} tris) then [{finish.timings}]";
                return;
            }

            var vertexCount = positions.Length;
            var triAlive = new bool[triangleCount];
            for (var t = 0; t < triangleCount; t++) triAlive[t] = true;

            var vertAlive = new bool[vertexCount];
            var version = new int[vertexCount];
            for (var v = 0; v < vertexCount; v++) vertAlive[v] = true;

            var incident = new Incidence(vertexCount, triangles, triangleCount);
            msIncident = phase.ElapsedMilliseconds; phase.Restart();

            var quadrics = BuildQuadrics(topology, positions, triangles, triangleCount,
                options.boundaryWeight, triLabel, options.labelBorderWeight);
            msQuadrics = phase.ElapsedMilliseconds; phase.Restart();

            // --- seed the heap with every edge ---
            //
            // Deduplicated through the half-edge twins rather than a hash set, which is not a
            // micro-optimisation: it is the difference between minutes and milliseconds.
            //
            // The previous version keyed a HashSet<long> on (min << 32) | max. .NET hashes a long
            // as (int)v ^ (int)(v >> 32), so that key hashes to exactly `min ^ max` — and on a
            // subdivided or scanned surface, adjacent indices are numerically close, so min ^ max
            // is a small number for almost every edge. A million and a half edges therefore land in
            // a few thousand buckets and every insert walks a collision chain hundreds long.
            //
            // Measured, on icospheres decimated to 20k triangles:
            //
            //     input tris     seed phase
            //         20,480         34 ms
            //         81,920        468 ms
            //        327,680     12,362 ms      <- 4x the input, 26x the time
            //
            // Extrapolating the same exponent put a 1.3M-triangle scan at roughly five minutes in
            // this one loop, which is what the user was watching when the progress bar said
            // "Decimating input" and never moved.
            //
            // Each undirected edge has exactly two half-edges, so taking the one with the lower
            // index enumerates every edge exactly once with no hashing at all. Boundary and
            // non-manifold half-edges have twin < 0 and are all taken; a non-manifold edge may
            // therefore seed more than one candidate, which is harmless — the extra is a stale heap
            // entry, and the version check already discards those.
            var heap = new List<Candidate>(triangles.Length);
            var halfEdgeCount = topology.halfEdgeVertex.Length;

            for (var h = 0; h < halfEdgeCount; h++)
            {
                var twin = topology.halfEdgeTwin[h];
                if (twin >= 0 && twin < h) continue;

                var u = topology.halfEdgeVertex[h];
                var v = topology.HalfEdgeTarget(h);
                if (u == v) continue;

                heap.Add(MakeCandidate(u, v, positions, quadrics, version, stats));
            }

            HeapBuild(heap);
            msSeed = phase.ElapsedMilliseconds; phase.Restart();

            var maxFlipCos = Mathf.Cos(Mathf.Clamp(options.maxNormalFlipDegrees, 1f, 179f) * Mathf.Deg2Rad);
            var live = CollapseLoop(positions, triangles, triangleCount, quadrics, null, heap, triAlive,
                vertAlive, version, incident, target, maxFlipCos, stats, onProgress, triLabel);

            msCollapse = phase.ElapsedMilliseconds; phase.Restart();
            // --- compact ---
            var remap = new int[vertexCount];
            var keptPositions = new List<Vector3>(vertexCount);
            for (var v = 0; v < vertexCount; v++)
            {
                if (!vertAlive[v]) { remap[v] = -1; continue; }
                remap[v] = keptPositions.Count;
                keptPositions.Add(positions[v]);
            }

            var keptTriangles = new List<int>(live * 3);
            for (var t = 0; t < triangleCount; t++)
            {
                if (!triAlive[t]) continue;
                int a = remap[triangles[t * 3]], b = remap[triangles[t * 3 + 1]], c = remap[triangles[t * 3 + 2]];
                if (a < 0 || b < 0 || c < 0) continue;
                if (a == b || b == c || a == c) continue;
                keptTriangles.Add(a);
                keptTriangles.Add(b);
                keptTriangles.Add(c);
            }

            stats.outputTriangles = keptTriangles.Count / 3;
            stats.outputVertices = keptPositions.Count;

            stats.timings = $"topo={msTopology} incident={msIncident} quadrics={msQuadrics} " +
                            $"seed={msSeed} collapse={msCollapse} compact={phase.ElapsedMilliseconds} " +
                            $"heapPeak={heap.Count}";

            outPositions = keptPositions.ToArray();
            outTriangles = keptTriangles.ToArray();
        }

        /// <summary>
        /// Collapses the cheapest edges in <paramref name="heap"/> until <paramref name="target"/>
        /// triangles are left; returns how many are. A <paramref name="locked"/> vertex is never
        /// queued, so it never moves -- the parallel stage's seams between blocks.
        /// </summary>
        static int CollapseLoop(Vector3[] positions, int[] triangles, int triangleCount,
            Quadric[] quadrics, bool[] locked, List<Candidate> heap, bool[] triAlive, bool[] vertAlive,
            int[] version, Incidence incident, int target, float maxFlipCos, DecimateStats stats,
            Action<float> onProgress, int[] triLabel = null)
        {
            var live = triangleCount;

            // Progress is reported from inside the loop, not once on entry. This stage is the
            // longest one on a scan, and a bar that only moves when it finishes is
            // indistinguishable from a hang — which is exactly how it was read.
            var reportEvery = Mathf.Max(1, (triangleCount - target) / 200);
            var toRemove = Mathf.Max(1, triangleCount - target);
            var sinceReport = 0;

            while (live > target && heap.Count > 0)
            {
                var best = HeapPop(heap);

                if (!vertAlive[best.u] || !vertAlive[best.v]) continue;
                // Stale entry: one endpoint has moved since this cost was computed.
                if (version[best.u] != best.versionU || version[best.v] != best.versionV) continue;

                // Drop dead faces from both rings before anything walks them.
                //
                // This is the second half of the decimator's cost problem, and it was invisible for
                // the same reason as the first: nothing measured it. Nothing used to remove a dead
                // triangle from an incidence list, so a vertex that survived many collapses kept
                // every face it had ever inherited. The four scans below then walked a list growing
                // without bound while the live ring stayed at valence six.
                //
                // Measured collapse phase, icospheres decimated to 20k triangles:
                //
                //     input tris     collapse phase
                //         20,480             3 ms
                //         81,920           368 ms
                //        327,680         2,617 ms
                //
                // Pruning here bounds every later scan by the live valence, which is what makes the
                // phase linear in the number of collapses instead of in their square.
                incident.Prune(best.u, triAlive);
                incident.Prune(best.v, triAlive);

                if (!AreConnected(incident, triangles, triAlive, best.u, best.v)) continue;

                if (!PassesLinkCondition(incident, triangles, triAlive, best.u, best.v))
                {
                    stats.rejectedTopology++;
                    continue;
                }

                var slide = false;
                if (triLabel != null && !LabelCollapseAllowed(incident, triangles, triAlive, triLabel,
                        positions, best.u, best.v, ref best.target, out slide))
                {
                    stats.rejectedLabel++;
                    continue;
                }

                if (WouldFlipNormals(incident, triangles, triAlive, positions,
                        best.u, best.v, best.target, maxFlipCos))
                {
                    stats.rejectedNormalFlip++;
                    if (best.solvedTarget) stats.rejectedFlipSolved++;
                    else stats.rejectedFlipFallback++;
                    continue;
                }

                // --- apply ---
                positions[best.u] = best.target;
                quadrics[best.u] = Quadric.Sum(quadrics[best.u], quadrics[best.v]);

                var countV = incident.Count(best.v);
                for (var i = 0; i < countV; i++)
                {
                    var t = incident.Get(best.v, i);
                    if (!triAlive[t]) continue;

                    var i0 = triangles[t * 3];
                    var i1 = triangles[t * 3 + 1];
                    var i2 = triangles[t * 3 + 2];

                    if (i0 == best.u || i1 == best.u || i2 == best.u)
                    {
                        // Degenerates into a sliver on collapse.
                        triAlive[t] = false;
                        live--;
                        continue;
                    }

                    if (i0 == best.v) triangles[t * 3] = best.u;
                    if (i1 == best.v) triangles[t * 3 + 1] = best.u;
                    if (i2 == best.v) triangles[t * 3 + 2] = best.u;

                    incident.Add(best.u, t, triAlive);
                }

                incident.Clear(best.v);
                vertAlive[best.v] = false;


                if (onProgress != null && ++sinceReport >= reportEvery)
                {
                    sinceReport = 0;
                    onProgress((triangleCount - live) / (float)toRemove);
                }

                version[best.u]++;
                stats.collapsesApplied++;
                if (slide) stats.labelSlides++;
                stats.maxErrorApplied = Mathf.Max(stats.maxErrorApplied, (float)best.cost);

                // Re-cost every edge now touching the survivor: one candidate per neighbour, not
                // one per (face, edge) pair.
                //
                // Every edge at the survivor belongs to two of its faces, so walking the faces and
                // taking each edge that touches u queued every one of them exactly twice. A
                // duplicate costs a MakeCandidate — a quadric sum, a 3x3 solve and up to four error
                // evaluations — plus a sift down a heap that ran to 150,000 entries, and later a pop
                // and a version check, all to consider a collapse already queued. Deduplicating
                // through the one-ring halves the traffic through both.
                incident.Prune(best.u, triAlive);

                s_Survivors.Clear();
                var countU = incident.Count(best.u);
                for (var i = 0; i < countU; i++)
                {
                    var t = incident.Get(best.u, i);
                    int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];

                    // In a triangle both vertices other than u share an edge with u, so the
                    // one-ring gathered this way is exactly the set of edges at u.
                    if (a != best.u) AddUnique(s_Survivors, a);
                    if (b != best.u) AddUnique(s_Survivors, b);
                    if (c != best.u) AddUnique(s_Survivors, c);
                }

                for (var i = 0; i < s_Survivors.Count; i++)
                {
                    var other = s_Survivors[i];
                    if (!vertAlive[other]) continue;
                    if (locked != null && locked[other]) continue;

                    HeapPush(heap, MakeCandidate(best.u, other, positions, quadrics, version, stats));
                }
            }

            return live;
        }

        /// <summary>
        /// The parallel first stage: Morton-ordered blocks of equal triangle count, each decimated
        /// on its own with shared vertices locked, then compacted. See <see cref="parallelBlocks"/>.
        /// </summary>
        static void ParallelStage(RemeshTopology topology, Vector3[] positions, int[] triangles,
            Quadric[] quadrics, int target, DecimateOptions options, DecimateStats stats,
            bool[] labelBorder, out Vector3[] outPositions, out int[] outTriangles, Action<float> onProgress)
        {
            var triangleCount = triangles.Length / 3;
            var vertexCount = positions.Length;

            var lo = positions[0];
            var hi = positions[0];
            for (var v = 1; v < vertexCount; v++)
            {
                lo = Vector3.Min(lo, positions[v]);
                hi = Vector3.Max(hi, positions[v]);
            }
            var extent = hi - lo;
            var span = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));
            var toGrid = span > 0f ? 2097151f / span : 0f;

            ulong Spread(uint x)
            {
                ulong v = x & 0x1fffff;
                v = (v | v << 32) & 0x1f00000000ffff;
                v = (v | v << 16) & 0x1f0000ff0000ff;
                v = (v | v << 8) & 0x100f00f00f00f00f;
                v = (v | v << 4) & 0x10c30c30c30c30c3;
                v = (v | v << 2) & 0x1249249249249249;
                return v;
            }

            var keys = new ulong[triangleCount];
            var order = new int[triangleCount];
            System.Threading.Tasks.Parallel.For(0, triangleCount, t =>
            {
                var c = (positions[triangles[3 * t]] + positions[triangles[3 * t + 1]]
                         + positions[triangles[3 * t + 2]]) / 3f - lo;
                keys[t] = Spread((uint)(c.x * toGrid)) | Spread((uint)(c.y * toGrid)) << 1
                          | Spread((uint)(c.z * toGrid)) << 2;
                order[t] = t;
            });
            Array.Sort(keys, order);

            var blocks = Mathf.Clamp(Mathf.Min(parallelBlockCount, triangleCount / 4000), 1, 4096);
            var blockOf = new int[triangleCount];
            for (var rank = 0; rank < triangleCount; rank++)
                blockOf[order[rank]] = (int)((long)rank * blocks / triangleCount);

            // A vertex used by two blocks is locked: -2. Otherwise the block that owns it.
            var vertexBlock = new int[vertexCount];
            for (var v = 0; v < vertexCount; v++) vertexBlock[v] = -1;
            for (var t = 0; t < triangleCount; t++)
            {
                var b = blockOf[t];
                for (var k = 0; k < 3; k++)
                {
                    var v = triangles[3 * t + k];
                    if (vertexBlock[v] == -1) vertexBlock[v] = b;
                    else if (vertexBlock[v] != b) vertexBlock[v] = -2;
                }
            }

            var maxFlipCos = Mathf.Cos(Mathf.Clamp(options.maxNormalFlipDegrees, 1f, 179f) * Mathf.Deg2Rad);
            var newPositions = (Vector3[])positions.Clone();
            var alive = new bool[vertexCount];
            var blockTriangles = new int[blocks][];
            var blockStats = new DecimateStats[blocks];
            var finished = 0;

            System.Threading.Tasks.Parallel.For(0, blocks, b =>
            {
                var first = (int)(((long)b * triangleCount + blocks - 1) / blocks);
                var last = (int)(((long)(b + 1) * triangleCount + blocks - 1) / blocks);
                var count = last - first;
                var local = new Dictionary<int, int>(count);
                var globalOf = new List<int>(count);
                var localTriangles = new int[count * 3];

                for (var i = 0; i < count; i++)
                {
                    var t = order[first + i];
                    for (var k = 0; k < 3; k++)
                    {
                        var g = triangles[3 * t + k];
                        if (!local.TryGetValue(g, out var l))
                        {
                            l = globalOf.Count;
                            local.Add(g, l);
                            globalOf.Add(g);
                        }
                        localTriangles[3 * i + k] = l;
                    }
                }

                var localCount = globalOf.Count;
                var localPositions = new Vector3[localCount];
                var localQuadrics = new Quadric[localCount];
                var locked = new bool[localCount];
                var vertAlive = new bool[localCount];
                for (var l = 0; l < localCount; l++)
                {
                    var g = globalOf[l];
                    localPositions[l] = positions[g];
                    localQuadrics[l] = quadrics[g];
                    // Label borders wait for the serial stage, which moves them only along themselves.
                    locked[l] = vertexBlock[g] != b || (labelBorder != null && labelBorder[g]);
                    vertAlive[l] = true;
                }

                var blockStat = new DecimateStats();
                var version = new int[localCount];
                var triAlive = new bool[count];
                for (var i = 0; i < count; i++) triAlive[i] = true;

                // Each edge once, through its half-edge twin as the global seed does; an edge whose
                // twin lies in another block has both ends locked and is never a candidate.
                var heap = new List<Candidate>(count * 2);
                for (var i = 0; i < count; i++)
                {
                    var t = order[first + i];
                    for (var k = 0; k < 3; k++)
                    {
                        var h = 3 * t + k;
                        var twin = topology.halfEdgeTwin[h];
                        if (twin >= 0 && twin < h && blockOf[twin / 3] == b) continue;
                        var u = localTriangles[3 * i + k];
                        var v = localTriangles[3 * i + (k + 1) % 3];
                        if (u == v || locked[u] || locked[v]) continue;
                        heap.Add(MakeCandidate(u, v, localPositions, localQuadrics, version, blockStat));
                    }
                }
                HeapBuild(heap);

                var incident = new Incidence(localCount, localTriangles, count);
                var localTarget = Mathf.Max(4, (int)((long)count * target / triangleCount));
                CollapseLoop(localPositions, localTriangles, count, localQuadrics, locked, heap, triAlive,
                    vertAlive, version, incident, localTarget, maxFlipCos, blockStat, null);

                // Unlocked vertices belong to this block alone, so these writes never collide.
                for (var l = 0; l < localCount; l++)
                {
                    if (!vertAlive[l]) continue;
                    var g = globalOf[l];
                    alive[g] = true;
                    if (!locked[l]) newPositions[g] = localPositions[l];
                }

                var kept = new List<int>(count);
                for (var i = 0; i < count; i++)
                {
                    if (!triAlive[i]) continue;
                    int a = localTriangles[3 * i], c1 = localTriangles[3 * i + 1], c2 = localTriangles[3 * i + 2];
                    if (a == c1 || c1 == c2 || a == c2) continue;
                    kept.Add(globalOf[a]);
                    kept.Add(globalOf[c1]);
                    kept.Add(globalOf[c2]);
                }
                blockTriangles[b] = kept.ToArray();
                blockStats[b] = blockStat;

                if (onProgress != null)
                    onProgress(System.Threading.Interlocked.Increment(ref finished) / (float)blocks);
            });

            foreach (var blockStat in blockStats) stats.Accumulate(blockStat);

            // Locked vertices are alive whichever block reached them; an unlocked one only if its
            // own block kept it, which the writes above recorded.
            var remap = new int[vertexCount];
            var keptPositions = new List<Vector3>(vertexCount / 2);
            for (var v = 0; v < vertexCount; v++)
            {
                if (!alive[v]) { remap[v] = -1; continue; }
                remap[v] = keptPositions.Count;
                keptPositions.Add(newPositions[v]);
            }

            var total = 0;
            foreach (var kept in blockTriangles) total += kept.Length;
            outTriangles = new int[total];
            var write = 0;
            foreach (var kept in blockTriangles)
                for (var i = 0; i < kept.Length; i++)
                    outTriangles[write++] = remap[kept[i]];
            outPositions = keptPositions.ToArray();
        }

        // ------------------------------------------------------------------
        // Vertices on a label border, for locking in the parallel stage.
        static bool[] LabelBorderVertices(RemeshTopology topology, int[] triLabel)
        {
            if (triLabel == null) return null;
            var result = new bool[topology.vertexCount];
            for (var h = 0; h < topology.halfEdgeTwin.Length; h++)
            {
                var twin = topology.halfEdgeTwin[h];
                if (twin < 0 || triLabel[h / 3] == triLabel[twin / 3]) continue;
                result[topology.halfEdgeVertex[h]] = true;
                result[topology.HalfEdgeTarget(h)] = true;
            }
            return result;
        }

        [ThreadStatic] static List<int> s_LabelsU, s_LabelsV;

        // Whether collapsing v into u keeps the label borders: v off every border is free (and a
        // border u keeps its place); v on a border goes only along it, onto u at u's place, and not
        // at all where three labels meet or the border reaches the open edge.
        static bool LabelCollapseAllowed(Incidence incident, int[] triangles, bool[] triAlive, int[] triLabel,
            Vector3[] positions, int u, int v, ref Vector3 target, out bool slide)
        {
            slide = false;
            void Labels(int x, List<int> into)
            {
                into.Clear();
                var count = incident.Count(x);
                for (var i = 0; i < count; i++)
                {
                    var t = incident.Get(x, i);
                    if (!triAlive[t]) continue;
                    if (!into.Contains(triLabel[t])) into.Add(triLabel[t]);
                }
            }
            s_LabelsU ??= new List<int>();
            s_LabelsV ??= new List<int>();
            Labels(u, s_LabelsU);
            Labels(v, s_LabelsV);
            if (s_LabelsV.Count <= 1)
            {
                if (s_LabelsU.Count > 1) target = positions[u];
                return true;
            }
            if (s_LabelsU.Count <= 1 || s_LabelsV.Count > 2) return false;
            // The edge itself must be on the border: its two triangles labelled differently. And v
            // must not be where the border meets the open edge (an edge of v in one triangle only).
            int shared0 = -1, shared1 = -1;
            var countV = incident.Count(v);
            for (var i = 0; i < countV; i++)
            {
                var t = incident.Get(v, i);
                if (!triAlive[t]) continue;
                int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
                if (a == u || b == u || c == u)
                {
                    if (shared0 < 0) shared0 = t; else shared1 = t;
                }
                foreach (var w in new[] { a, b, c })
                {
                    if (w == v) continue;
                    var uses = 0;
                    for (var j = 0; j < countV; j++)
                    {
                        var t2 = incident.Get(v, j);
                        if (!triAlive[t2]) continue;
                        if (triangles[3 * t2] == w || triangles[3 * t2 + 1] == w || triangles[3 * t2 + 2] == w) uses++;
                    }
                    if (uses == 1) return false;
                }
            }
            if (shared0 < 0 || shared1 < 0 || triLabel[shared0] == triLabel[shared1]) return false;
            target = positions[u];
            slide = true;
            return true;
        }

        static Quadric[] BuildQuadrics(RemeshTopology topology, Vector3[] positions, int[] triangles,
            int triangleCount, float boundaryWeight, int[] triLabel = null, float labelWeight = 1f)
        {
            var quadrics = new Quadric[positions.Length];

            for (var t = 0; t < triangleCount; t++)
            {
                int ia = triangles[t * 3], ib = triangles[t * 3 + 1], ic = triangles[t * 3 + 2];
                var a = positions[ia];
                var b = positions[ib];
                var c = positions[ic];

                var normal = Vector3.Cross(b - a, c - a);
                var area = normal.magnitude;
                if (area <= 1e-20f) continue;
                normal /= area;

                var d = -Vector3.Dot(normal, a);

                // Area weighting: a large face should constrain its vertices more than a sliver.
                var plane = Quadric.FromPlane(normal.x, normal.y, normal.z, d, area * 0.5f);

                quadrics[ia].Add(plane);
                quadrics[ib].Add(plane);
                quadrics[ic].Add(plane);
            }

            // Boundary constraint planes, erected perpendicular to the surface along each boundary
            // edge. Without these the quadric eats open edges first: an open edge has fewer planes
            // holding it in place than an interior one, so it looks cheap to collapse precisely
            // where the silhouette lives.
            if (topology.halfEdgeTwin != null && boundaryWeight > 0f)
            {
                for (var h = 0; h < topology.halfEdgeTwin.Length; h++)
                {
                    // An open edge, or one between two labels (planes from both sides).
                    var twinH = topology.halfEdgeTwin[h];
                    if (twinH >= 0 && (triLabel == null || triLabel[h / 3] == triLabel[twinH / 3])) continue;

                    var from = topology.halfEdgeVertex[h];
                    var to = topology.HalfEdgeTarget(h);
                    if (from >= positions.Length || to >= positions.Length) continue;

                    var edge = positions[to] - positions[from];
                    var length = edge.magnitude;
                    if (length <= 1e-20f) continue;

                    var faceNormal = TriangleNormal(positions, triangles, h / 3);
                    if (faceNormal.sqrMagnitude <= 1e-20f) continue;

                    var planeNormal = Vector3.Cross(edge / length, faceNormal);
                    if (planeNormal.sqrMagnitude <= 1e-20f) continue;
                    planeNormal = MeshMath.SafeNormalize(planeNormal, Vector3.zero);
                    if (planeNormal == Vector3.zero) continue;

                    var d = -Vector3.Dot(planeNormal, positions[from]);
                    var plane = Quadric.FromPlane(planeNormal.x, planeNormal.y, planeNormal.z, d,
                        twinH >= 0 ? labelWeight * length * (labelWeightByArea ? length : 1f) : boundaryWeight * length);

                    quadrics[from].Add(plane);
                    quadrics[to].Add(plane);
                }
            }

            return quadrics;
        }

        static Vector3 TriangleNormal(Vector3[] positions, int[] triangles, int triangle)
        {
            var a = positions[triangles[triangle * 3]];
            var b = positions[triangles[triangle * 3 + 1]];
            var c = positions[triangles[triangle * 3 + 2]];

            // Zero rather than Vector3.up on a degenerate triangle: the one caller tests the result
            // against 1e-20 and skips, so inventing a direction here would fabricate a boundary
            // constraint plane out of a sliver.
            return MeshMath.SafeFaceNormal(a, b, c, Vector3.zero);
        }

        static Candidate MakeCandidate(int u, int v, Vector3[] positions, Quadric[] quadrics,
            int[] version, DecimateStats stats)
        {
            var combined = Quadric.Sum(quadrics[u], quadrics[v]);

            var pu = positions[u];
            var pv = positions[v];
            var mid = (pu + pv) * 0.5f;
            var edgeLength = Vector3.Distance(pu, pv);

            var solved = combined.TryOptimalPoint(out var target);

            // Reject a solved point that has wandered far from the edge it is meant to replace.
            //
            // This guard is not optional, and leaving it out is the classic way QEM quietly ruins a
            // mesh. Where the neighbourhood is close to planar the 3x3 system is ill-conditioned:
            // the determinant is small but not small enough to trip the singularity test, and the
            // solution runs off along the poorly-constrained direction. The quadric error at that
            // far-away point is genuinely low — every plane it is being measured against is nearly
            // parallel — so nothing downstream objects, and the vertex simply teleports. Measured
            // before this check: mean surface deviation on a decimated sphere was ~13x what the
            // chord error of that triangle count can account for.
            if (solved)
            {
                var drift = Vector3.Distance(target, mid);
                var relative = edgeLength > 1e-20f ? drift / edgeLength : 0f;
                stats.driftSum += relative;
                if (relative > stats.driftMax) stats.driftMax = relative;

                if (drift > 2f * edgeLength || float.IsNaN(drift))
                {
                    solved = false;
                    stats.driftRejected++;
                }
            }

            if (solved) stats.solvedTargets++; else stats.fallbackTargets++;

            if (!solved)
            {
                // The exact minimum along the edge. It replaces a best-of-three over the two
                // endpoints and the midpoint, and can only improve on it: those three are samples
                // of precisely this function. On a crease it still lands on the endpoint that
                // preserves the feature, which is what keeps a decimated cube's corners.
                target = combined.OptimalOnSegment(pu, pv);
            }

            return new Candidate
            {
                u = u,
                v = v,
                target = target,
                cost = combined.Error(target),
                versionU = version[u],
                versionV = version[v],
                solvedTarget = solved
            };
        }

        static bool AreConnected(Incidence incident, int[] triangles, bool[] triAlive, int u, int v)
        {
            var count = incident.Count(u);
            for (var i = 0; i < count; i++)
            {
                var t = incident.Get(u, i);
                if (!triAlive[t]) continue;
                if (triangles[t * 3] == v || triangles[t * 3 + 1] == v || triangles[t * 3 + 2] == v)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The link condition: an edge collapse preserves a manifold surface only if the vertices
        /// adjacent to BOTH endpoints are exactly the ones opposite the shared edges.
        /// </summary>
        /// <remarks>
        /// Without this check a collapse can weld together two parts of the surface that were only
        /// near each other, producing a non-manifold vertex or a pinched tunnel. The quadric cost
        /// cannot see it: geometrically such a collapse is often very cheap, which means it gets
        /// chosen early and the damage is done before anything visible goes wrong.
        /// </remarks>
        // Reused across calls, and lists rather than hash sets. This runs once per candidate edge
        // popped from the heap — millions of times on a scan — and a one-ring holds about six
        // entries, at which size a linear scan beats hashing outright and allocates nothing.
        //
        // Per thread, because the parallel stage runs one collapse loop per block at once.
        [ThreadStatic] static List<int> t_Shared;
        [ThreadStatic] static List<int> t_RingU;
        [ThreadStatic] static List<int> t_RingV;
        [ThreadStatic] static List<int> t_Survivors;
        static List<int> s_Shared => t_Shared ??= new List<int>(8);
        static List<int> s_RingU => t_RingU ??= new List<int>(32);
        static List<int> s_RingV => t_RingV ??= new List<int>(32);
        static List<int> s_Survivors => t_Survivors ??= new List<int>(32);

        static void AddUnique(List<int> list, int value)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i] == value) return;
            list.Add(value);
        }

        static bool PassesLinkCondition(Incidence incident, int[] triangles, bool[] triAlive,
            int u, int v)
        {
            var shared = s_Shared;
            var ringU = s_RingU;
            var ringV = s_RingV;

            shared.Clear();
            ringU.Clear();
            ringV.Clear();

            var sharedTriangles = 0;

            var countU = incident.Count(u);
            for (var i = 0; i < countU; i++)
            {
                var t = incident.Get(u, i);
                if (!triAlive[t]) continue;

                int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                var touchesV = a == v || b == v || c == v;
                if (touchesV)
                {
                    sharedTriangles++;
                    if (a != u && a != v) AddUnique(shared, a);
                    if (b != u && b != v) AddUnique(shared, b);
                    if (c != u && c != v) AddUnique(shared, c);
                }
                if (a != u) AddUnique(ringU, a);
                if (b != u) AddUnique(ringU, b);
                if (c != u) AddUnique(ringU, c);
            }

            // More than two triangles on one edge is non-manifold input; refuse rather than guess.
            if (sharedTriangles > 2) return false;

            var countV = incident.Count(v);
            for (var i = 0; i < countV; i++)
            {
                var t = incident.Get(v, i);
                if (!triAlive[t]) continue;

                int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                if (a != v) AddUnique(ringV, a);
                if (b != v) AddUnique(ringV, b);
                if (c != v) AddUnique(ringV, c);
            }

            ringU.Remove(v);
            ringV.Remove(u);

            // Every common neighbour must be one of the vertices opposite the shared edge.
            for (var i = 0; i < ringU.Count; i++)
            {
                var x = ringU[i];
                if (ringV.Contains(x) && !shared.Contains(x))
                    return false;
            }

            return true;
        }

        static bool WouldFlipNormals(Incidence incident, int[] triangles, bool[] triAlive,
            Vector3[] positions, int u, int v, Vector3 target, float maxFlipCos)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var moving = pass == 0 ? u : v;
                var count = incident.Count(moving);

                for (var i = 0; i < count; i++)
                {
                    var t = incident.Get(moving, i);
                    if (!triAlive[t]) continue;

                    int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];

                    // Faces that disappear in the collapse cannot flip.
                    var touchesU = a == u || b == u || c == u;
                    var touchesV = a == v || b == v || c == v;
                    if (touchesU && touchesV) continue;

                    var pa = positions[a];
                    var pb = positions[b];
                    var pc = positions[c];

                    var before = Vector3.Cross(pb - pa, pc - pa);
                    if (before.sqrMagnitude <= 1e-20f) continue;

                    if (a == u || a == v) pa = target;
                    if (b == u || b == v) pb = target;
                    if (c == u || c == v) pc = target;

                    var after = Vector3.Cross(pb - pa, pc - pa);
                    if (after.sqrMagnitude <= 1e-20f) return true;   // collapsed to zero area

                    // Compared without normalising either side. Vector3.normalized returns ZERO below a
                    // magnitude of 1e-5, and these are cross products of mesh edges, so on a mesh
                    // whose triangles are small in absolute terms both sides became (0,0,0) and
                    // their dot product read as a 90-degree rotation. That rejected every candidate
                    // edge on the reference scan — 2,247,091 of them — and decimation applied not a
                    // single collapse. See MeshMath.
                    if (MeshMath.CosAngleBetween(before, after) < maxFlipCos)
                        return true;
                }
            }

            return false;
        }


        static Mesh BuildMesh(string name, Vector3[] positions, int[] triangles)
        {
            var mesh = new Mesh { name = name };
            if (positions.Length > 65534)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(positions);
            mesh.SetTriangles(triangles, 0, false);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------
        // Binary min-heap. A List<T> plus three functions is enough here and avoids depending on
        // a priority queue type that is not available on this scripting runtime.
        // ------------------------------------------------------------------

        static void HeapBuild(List<Candidate> heap)
        {
            for (var i = heap.Count / 2 - 1; i >= 0; i--) HeapSiftDown(heap, i);
        }

        static void HeapPush(List<Candidate> heap, Candidate item)
        {
            heap.Add(item);
            var i = heap.Count - 1;
            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (heap[parent].cost <= heap[i].cost) break;
                (heap[parent], heap[i]) = (heap[i], heap[parent]);
                i = parent;
            }
        }

        static Candidate HeapPop(List<Candidate> heap)
        {
            var top = heap[0];
            var last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            if (heap.Count > 0) HeapSiftDown(heap, 0);
            return top;
        }

        static void HeapSiftDown(List<Candidate> heap, int i)
        {
            var count = heap.Count;
            while (true)
            {
                var left = 2 * i + 1;
                if (left >= count) break;
                var right = left + 1;
                var smallest = right < count && heap[right].cost < heap[left].cost ? right : left;
                if (heap[i].cost <= heap[smallest].cost) break;
                (heap[i], heap[smallest]) = (heap[smallest], heap[i]);
                i = smallest;
            }
        }
    }
}
