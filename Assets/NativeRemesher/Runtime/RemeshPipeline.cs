// Native quad remesher for Unity — the whole pipeline in one call.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    [Serializable]
    public struct RemeshSettings
    {
        [Tooltip("Roughly how many quads the result should have.")]
        public int targetQuadCount;

        [Tooltip("Smoothing sweeps for the orientation (cross) field.")]
        public int orientationIterations;

        [Tooltip("Smoothing sweeps for the position (lattice) field.")]
        public int positionIterations;

        [Tooltip("No longer used. Merging is decided exactly, by integer lattice coordinates, " +
                 "rather than by a distance tolerance.")]
        public float mergeThreshold;

        [Tooltip("How many times to correct the lattice spacing to hit the requested face count. " +
                 "1 disables the correction.")]
        public int faceCountIterations;

        [Tooltip("Relative face-count error that ends the correction early.")]
        public float faceCountTolerance;

        [Tooltip("Mirror symmetry, as QuadRemesher's Symmetry X / Y / Z: one half is remeshed and " +
                 "mirrored, welded along the plane. The plane is the local origin when the mesh " +
                 "straddles it, else the middle of the bounds.")]
        public SymmetryAxis symmetry;

        public static RemeshSettings Default => new RemeshSettings
        {
            targetQuadCount = 2000,
            orientationIterations = 50,
            positionIterations = 50,
            mergeThreshold = 0.45f,
            faceCountIterations = 4,
            faceCountTolerance = 0.1f
        };

        public RemeshSettings Validated()
        {
            return new RemeshSettings
            {
                targetQuadCount = Mathf.Max(4, targetQuadCount),
                // Zero is allowed, and deliberately: it is how an externally supplied orientation
                // field is used unchanged instead of being smoothed over.
                orientationIterations = Mathf.Clamp(orientationIterations, 0, 5000),
                positionIterations = Mathf.Clamp(positionIterations, 1, 5000),
                mergeThreshold = Mathf.Clamp(mergeThreshold, 0.05f, 0.95f),
                faceCountIterations = Mathf.Clamp(faceCountIterations, 1, 8),
                faceCountTolerance = Mathf.Clamp(faceCountTolerance, 0.01f, 0.5f),
                symmetry = symmetry
            };
        }
    }

    public sealed class RemeshResult
    {
        public RemeshTopology topology;
        public MeshHierarchy hierarchy;

        /// <summary>Input refinement done for the integer grid map, if any.</summary>
        public Extraction.InputRefine.Stats refineStats;

        /// <summary>The second, graded refinement pass; see IntegerGridMap.gradeSpacing.</summary>
        public Extraction.InputRefine.Stats localRefineStats;

        /// <summary>Principal-curvature guide counters; null when off.</summary>
        public CurvatureDirections.Stats curvatureGuideStats;

        /// <summary>Ridge/valley curve network counters; null when off.</summary>
        public CurveNetwork.Stats curveNetworkStats;

        /// <summary>The curve network's edges, as vertex pairs on <see cref="topology"/>; null when off.</summary>
        public List<(int a, int b)> curveNetworkEdges;

        /// <summary>Normal-smoothing sweeps the orientation solve ran against; 0 when off.</summary>
        public int orientationNormalSweeps;

        /// <summary>Seam-aware UV transfer counters; null when the source had no UVs.</summary>
        public AttributeTransfer.FaceUvStats faceUvStats;
        public long refineMs;
        public OrientationField orientation;
        public HierarchicalStats positionLevels;
        public HierarchicalStats orientationLevels;
        public PositionField position;
        public QuadExtractionResult extraction;
        public Mesh mesh;

        public long decimateMs, topologyMs, hierarchyMs, orientationMs, positionMs, extractionMs,
            attributeMs;
        public long totalMs => decimateMs + topologyMs + hierarchyMs + orientationMs + positionMs
                               + extractionMs + attributeMs;

        /// <summary>The profile this ran under.</summary>
        public RemeshProfile profile;

        /// <summary>Pre-decimation results, or null when that stage did not run.</summary>
        public DecimateStats decimateStats;

        /// <summary>Detected sharp features. Never null after a successful run.</summary>
        public FeatureGraph features;

        /// <summary>The graded lattice spacing actually solved, or null when uniform.</summary>
        public ScaleField scaleField;

        /// <summary>Lattice spacing actually used, after face-count correction.</summary>
        public float scale;
        /// <summary>How many correction passes ran. 1 means the first attempt was close enough.</summary>
        /// <summary>Crease chains kept and dropped by the coherent-crease filter; zero when it did not run.</summary>
        public int coherentCreasesKept, coherentCreasesDropped, coherentCreasesThinned;

        /// <summary>The global cross-field solve, when IntegerGridMap.globalField ran it.</summary>
        public GlobalFieldStats globalFieldStats;

        /// <summary>The seamless parametrisation of the kept attempt, when IntegerGridMap.parametrization ran it.</summary>
        public Extraction.ParametrizationStats parametrizationStats;

        /// <summary>Singular faces before and after IntegerGridMap.cancelSingularPairs, and pairs tried.</summary>
        public int singularBeforeCancel, singularAfterCancel, singularPairsTried;
        public GlobalFieldStats decimatedFieldStats;

        /// <summary>The caller's undecimated mesh, kept for <see cref="RemeshPipeline.projectOntoOriginal"/>.</summary>
        [NonSerialized] public Vector3[] originalPositions;
        [NonSerialized] public int[] originalTriangles;

        /// <summary>The decimated input before refinement; the parametrisation's mesh.</summary>
        [NonSerialized] public Vector3[] decimatedPositions;
        [NonSerialized] public int[] decimatedTriangles;
        [NonSerialized] public RemeshTopology decimatedTopology;
        [NonSerialized] public int[] decimatedToRefined;
        [NonSerialized] public FeatureGraph decimatedFeatures;
        [NonSerialized] public List<(int a, int b)> decimatedFeatureEdges;
        [NonSerialized] public Vector3[] decimatedCross;
        [NonSerialized] public Extraction.ParametrizationCache parametrizationCache;

        public int faceCountAttempts;

        /// <summary>Border ears removed from the decimated mesh for the quad layout.</summary>
        public int trimmedEars, splitBowties;

        /// <summary>The mirror, when <see cref="RemeshSettings.symmetry"/> asked for one.</summary>
        public Symmetry.Stats symmetryStats;

        /// <summary>Face count reached by each integer grid attempt, "count@spacing", in order.</summary>
        public string faceCountTrace = "";
        /// <summary>Target after clamping to the coarsen-only ceiling.</summary>
        public int effectiveTargetQuadCount;

        /// <summary>
        /// The count asked for, before Adaptive Quad Count raised it; see
        /// IntegerGridMap.adaptiveQuadCount. Zero when the mode did not run.
        /// </summary>
        public int requestedQuadCount;

        /// <summary>How far Adaptive Quad Count multiplied the target; 1 when off.</summary>
        public float adaptiveQuadCountFactor = 1f;
        public bool targetWasClamped;

        /// <summary>Fraction of input vertices the feature detector marked as sitting in a crease.</summary>
        public float featureVertexFraction;

        /// <summary>
        /// The feature detector fired on so much of the mesh that it is reading noise rather than
        /// creases — the signature of a scan run under a profile meant for modelled geometry.
        /// </summary>
        public bool featureDetectionSuspect;

        /// <summary>
        /// <see cref="profile"/> was chosen by the pipeline rather than supplied by the caller.
        /// </summary>
        public bool profileWasAutoSelected;

        /// <summary>
        /// Feature fraction at <see cref="RemeshPipeline.autoProfileProbeAngleDegrees"/> — the
        /// number the auto-selection actually read. Zero when it did not run.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="featureVertexFraction"/>, which reports the CHOSEN profile's
        /// detection and is therefore near zero once a scan has been routed to a profile that
        /// declares no creases. Both are worth having: one says what was decided, the other says
        /// why.
        /// </remarks>
        public float profileProbeFeatureFraction;

        /// <summary>
        /// The same probe taken on the mesh BEFORE input refinement, or -1 when no refinement was
        /// going to run. When set, this is what the auto-selection reads. See RemeshPipeline.Solve.
        /// </summary>
        public float preRefineProbeFeatureFraction = -1f;
    }

    public static class RemeshPipeline
    {
        /// <summary>
        /// Above this fraction of vertices marked as creases, the feature detector is measuring
        /// noise. Set from the two ends of the corpus rather than chosen: the reference scan under
        /// GameAsset marks 32 % of its vertices, and a subdivided cube — every crease of which is a
        /// real 90-degree edge that the detector is supposed to find — marks well under a tenth.
        /// Fifteen percent sits between them with room on both sides.
        /// </summary>
        /// <remarks>
        /// Public because the auto-selection and the warning that fires when auto is OFF have to
        /// read the same number. Two copies would let the tool route an input to a scan profile on
        /// one threshold and then warn that the profile is wrong on another.
        /// </remarks>
        public const float suspectFeatureFraction = 0.15f;

        /// <summary>
        /// Dihedral angle the profile auto-selection measures at, in degrees.
        /// </summary>
        /// <remarks>
        /// GameAsset's own, deliberately. The question being asked is "would a profile meant for
        /// modelled geometry read creases or noise on this input", so it has to be asked at the
        /// angle that profile uses, and the threshold above was calibrated at exactly this angle.
        /// </remarks>
        public const float autoProfileProbeAngleDegrees = 30f;

        /// <summary>Diagnostic only: forces the orientation sweep count when positive.</summary>
        public static int debugOrientationIterations;

        /// <summary>Diagnostic: overrides the position-field sweep budget when > 0.</summary>
        public static int debugPositionIterations;

        /// <summary>
        /// Cap on the fine-level orientation sweeps after they are scaled up for refinement. Was
        /// 600, set when the integer path's orientation solve was flat; it is hierarchical now
        /// (IntegerGridMap.orientationHierarchyLevelIterations), and on the head at 5,000 with
        /// Adapt Quad Count 600 sweeps over 517k faces took 42 s where 150 took 6.5 s for the same
        /// result (irregular 4.41 / 4.64 %, Hausdorff 0.0103 / 0.0095).
        /// </summary>
        public static int maxOrientationSweeps = 200;

        /// <summary>
        /// Relax the output onto the caller's ORIGINAL mesh rather than the pre-decimated one the
        /// solver ran on. Off is the original behaviour; see the comment at the surface index.
        /// </summary>
        public static bool projectOntoOriginal = false;

        /// <summary>Diagnostic: overrides the chosen profile's relaxation sweeps when >= 0 (0 = off).</summary>
        public static int debugRelaxIterations = -1;

        /// <summary>Diagnostic: overrides the relaxation drift cap, in cells, when >= 0.</summary>
        public static float debugRelaxMaxDrift = -1f;

        /// <summary>Diagnostic: overrides the chosen profile's curvature adaptivity when >= 0.</summary>
        public static float debugCurvatureAdaptivity = -1f;

        /// <summary>Diagnostic: overrides the chosen profile's grading ratio clamp when > 1.</summary>
        public static float debugCurvatureMaxRatio = -1f;

        /// <summary>Diagnostic: overrides the pre-decimation triangles-per-quad budget when > 0.</summary>
        public static float debugDecimateBudgetPerQuad = -1f;

        /// <summary>
        /// Radius, in output cells, over which the orientation field's normals are averaged on the
        /// integer path. Zero solves against the raw normals. See the call site.
        /// </summary>
        public static float orientationNormalSmoothingCells;

        /// <summary>Diagnostic: exact normal-smoothing sweep count when > 0, overriding the cell radius.</summary>
        public static int debugOrientationNormalSweeps;

        /// <summary>Area-weighted one-ring averaging of the vertex normals, <paramref name="sweeps"/> times.</summary>
        static Vector3[] SmoothNormals(RemeshTopology topology, int sweeps)
        {
            var count = topology.vertexCount;
            var current = (Vector3[])topology.normals.Clone();
            var next = new Vector3[count];
            var areas = topology.areas;
            var start = topology.adjacencyStart;
            var adjacency = topology.adjacency;

            for (var sweep = 0; sweep < sweeps; sweep++)
            {
                var from = current;
                var to = next;
                System.Threading.Tasks.Parallel.For(0, count, i =>
                {
                    var sum = from[i] * areas[i];
                    for (var a = start[i]; a < start[i + 1]; a++)
                    {
                        var j = adjacency[a];
                        sum += from[j] * areas[j];
                    }
                    to[i] = MeshMath.SafeNormalize(sum, from[i]);
                });
                (current, next) = (next, current);
            }

            return current;
        }

        /// <summary>Diagnostic: turns on the orientation hierarchy with this many sweeps per level when > 0.</summary>
        public static int debugOrientationHierarchyLevelIterations;

        /// <summary>Diagnostic: overrides the chosen profile's crease angle when >= 0.</summary>
        public static float debugFeatureAngle = -1f;

        /// <summary>
        /// The profile for an input whose feature fraction at
        /// <see cref="autoProfileProbeAngleDegrees"/> is <paramref name="featureFraction"/>.
        /// </summary>
        /// <remarks>
        /// TWO OUTCOMES, NOT FOUR, and that is the honest extent of what one number supports. The
        /// measurement behind this separates scanned input from modelled input — whether a
        /// dihedral test on this mesh is finding creases or noise — and nothing in it distinguishes
        /// a mechanical part from a game prop, or a scan from a sculpted character. Those two
        /// choices stay with the caller, and picking them here would be inventing a signal.
        ///
        /// The scan branch is Photogrammetry rather than Character because the two differ only in
        /// sweep budget, and measured at the densities this runs at that budget is a wash which
        /// Photogrammetry wins on time (Benchmarks/real-asset-comparison.log, both with
        /// subdivision on):
        ///
        ///     character @5,474   Photogrammetry 0.8853 quads / 25.2 % irregular / 12.9 s
        ///                        Character      0.8894        / 24.5 %          / 17.0 s
        ///     ccnb @9,451        Photogrammetry 0.9280 quads / 21.2 % irregular / 10.3 s
        ///                        Character      0.9261        / 21.6 %          / 17.8 s
        ///
        /// Character is 0.4 points better on one asset and 0.2 worse on the other, for a third to
        /// three quarters more runtime. A default should not spend that on a coin toss.
        ///
        /// KNOWN LIMIT: the signal is a fraction of VERTICES, so it also moves with how finely the
        /// input is tessellated. A cube's creases are a fixed set of edges while its flat panels
        /// gain vertices with every subdivision, so the same shape reads as roughly 12 % creased at
        /// 16 subdivisions and 6 % at 32 — the same direction, with half the margin. Modelled
        /// geometry coarse enough to cross 15 % would have to be nearly all edge, which is why this
        /// is recorded rather than corrected; normalising by edge length instead of vertex count is
        /// the fix if an input is ever found that trips it.
        /// </remarks>
        public static RemeshProfileKind SelectProfileKind(float featureFraction)
        {
            return featureFraction > suspectFeatureFraction
                ? RemeshProfileKind.Photogrammetry
                : RemeshProfileKind.GameAsset;
        }

        /// <summary>The value below which <paramref name="fraction"/> of the area lies.</summary>
        static float AreaPercentile(float[] values, float[] areas, float fraction)
        {
            if (values == null || values.Length == 0) return 0f;
            var order = new int[values.Length];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));
            double total = 0;
            foreach (var a in areas) total += a;
            var goal = total * Mathf.Clamp01(fraction);
            double running = 0;
            foreach (var i in order)
            {
                running += areas[i];
                if (running >= goal) return values[i];
            }
            return values[order[order.Length - 1]];
        }

        static float FeatureFraction(FeatureGraph features)
        {
            var flags = features?.isFeatureVertex;
            if (flags == null || flags.Length == 0) return 0f;

            var count = 0;
            foreach (var flag in flags)
                if (flag) count++;

            return count / (float)flags.Length;
        }

        /// <param name="onProgress">Receives (stage label, 0..1). Return value is ignored.</param>
        public static RemeshResult Run(Mesh source, RemeshSettings settings,
            Action<string, float> onProgress = null)
        {
            return Run(source, settings, RemeshProfile.Default, onProgress);
        }

        /// <summary>
        /// Runs the pipeline under a domain profile, which selects the pre-stages and parameterises
        /// the shared ones. See <see cref="RemeshProfile"/>.
        /// </summary>
        /// <remarks>
        /// Main thread only, because it builds the output <see cref="Mesh"/>. For a run that must
        /// not block the editor, use <see cref="Solve"/> on a worker and
        /// <see cref="BuildOutputMesh"/> back on the main thread — that pair is what this method is
        /// composed of, and the editor window uses them directly.
        /// </remarks>
        public static RemeshResult Run(Mesh source, RemeshSettings settings, RemeshProfile profile,
            Action<string, float> onProgress = null, bool autoSelectProfile = false)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            RemeshTopology.ReadMesh(source, out var positions, out var triangles, out var uvs);

            var result = Solve(positions, triangles, settings, profile, onProgress, uvs,
                autoSelectProfile);
            BuildOutputMesh(result, source.name);

            return result;
        }

        /// <summary>
        /// The whole solve, on plain arrays, touching no Unity object — so it can run on a worker
        /// thread while the editor stays responsive.
        /// </summary>
        /// <remarks>
        /// Everything downstream of here works on arrays. That is not incidental tidiness: the
        /// reason the editor locked up for the duration of a run is that the solve reached for the
        /// <see cref="Mesh"/> API, which is main-thread only, so there was no thread it could run
        /// on but the one drawing the UI. Reading the input and writing the output are now the only
        /// two main-thread steps, and both are milliseconds.
        ///
        /// <see cref="RemeshResult.mesh"/> is left null; call <see cref="BuildOutputMesh"/> on the
        /// main thread to fill it in.
        /// </remarks>
        /// <param name="autoSelectProfile">
        /// Choose the profile from the input instead of using <paramref name="profile"/>. The
        /// supplied profile is still the seed: it decides the pre-decimation budget, and it is what
        /// runs if the selection is somehow not reached. See <see cref="SelectProfileKind"/>.
        /// </param>
        public static RemeshResult Solve(Vector3[] positions, int[] triangles,
            RemeshSettings settings, RemeshProfile profile, Action<string, float> onProgress = null,
            Vector2[] sourceUvs = null, bool autoSelectProfile = false)
            => SolveInternal(positions, triangles, settings, profile, onProgress, sourceUvs, autoSelectProfile,
                SymmetryAxis.None, 0f);

        // lockAxis/lockPlane: a symmetry half's cut. The decimated border vertices beside the plane
        // are put back on it, so the cut's grid line lies in the plane and the mirror meets it.
        static RemeshResult SolveInternal(Vector3[] positions, int[] triangles,
            RemeshSettings settings, RemeshProfile profile, Action<string, float> onProgress,
            Vector2[] sourceUvs, bool autoSelectProfile, SymmetryAxis lockAxis, float lockPlane)
        {
            if (positions == null)
                throw new ArgumentNullException(nameof(positions));

            profile = profile.Validated();
            settings = profile.ApplyTo(settings).Validated();
            if (settings.symmetry != SymmetryAxis.None)
                return SolveSymmetric(positions, triangles, settings, profile, onProgress, sourceUvs, autoSelectProfile);

            var result = new RemeshResult { profile = profile };
            var watch = new System.Diagnostics.Stopwatch();

            // --- pre-decimation ---
            //
            // Quality depends on the ratio of lattice spacing to input edge length, and past about
            // 2.5x it falls off a cliff rather than degrading gracefully. Raising the INPUT's edge
            // length keeps that ratio in band whatever the final face count, so this is what makes
            // an aggressive target produce good topology instead of mush. It is not a speed setting,
            // though it is also that.
            var solvePositions = positions;
            var solveTriangles = triangles;

            if (profile.preDecimate)
            {
                // The floor matters. The solver samples the lattice at input vertices, so below a
                // couple of hundred triangles there are simply not enough of them left to place
                // anything on, and an extremely coarse request would otherwise decimate the input
                // down to a handful of faces and then extract nothing at all from it.
                const int k_MinimumBudget = 256;

                var perQuad = debugDecimateBudgetPerQuad > 0f
                    ? debugDecimateBudgetPerQuad
                    : profile.decimateBudgetPerQuad;
                var budget = Mathf.Max(k_MinimumBudget, Mathf.RoundToInt(
                    settings.targetQuadCount * perQuad));

                if (triangles.Length / 3 > budget)
                {
                    onProgress?.Invoke("Decimating input", 0f);
                    watch.Restart();
                    PreDecimate.Decimate(positions, triangles,
                        DecimateOptions.ForTriangleCount(budget), out var decimateStats,
                        out solvePositions, out solveTriangles,
                        p => onProgress?.Invoke("Decimating input", p));
                    result.decimateMs = watch.ElapsedMilliseconds;
                    result.decimateStats = decimateStats;
                }
            }
            if (lockAxis != SymmetryAxis.None)
                solvePositions = Symmetry.LockBorderToPlane(solvePositions, solveTriangles, lockAxis, lockPlane);

            // --- auto-profile probe, taken before refinement ---
            //
            // The selection asks what fraction of vertices sit on a crease, and the 15 % threshold it
            // compares against was calibrated on decimated meshes. Refinement below splits long edges
            // at their midpoints: three to four times the vertices, every new one on flat surface, the
            // creases unchanged. The fraction is diluted by that same factor, and a scan reads as a
            // modelled asset. Measured on the body scan through the window's own path: the integer
            // extractor was routed to GameAsset -- creases on, at 30 degrees, on a scan -- while the
            // collapse extractor, which does not refine, correctly got Photogrammetry. The result was
            // the crumpling at the vest, waist and thighs. So the probe reads the mesh the threshold
            // was set on.
            if (autoSelectProfile && profile.integerGridMap)
            {
                var probeTopology = RemeshTopology.Build(solvePositions, solveTriangles);
                result.preRefineProbeFeatureFraction = FeatureFraction(
                    FeatureGraph.Detect(probeTopology, autoProfileProbeAngleDegrees));
            }

            // --- input refinement (integer grid map only) ---
            //
            // Pre-decimation above bounds the AVERAGE input edge length; the integer grid map also
            // needs the MAXIMUM bounded, because it carries one lattice offset per input edge and an
            // edge spanning two cells has no valid unit offset. The collapse extractor decides each
            // edge independently and does not care, so this runs only for the map.
            var refineInputPositions = solvePositions;
            var refineInputTriangles = solveTriangles;
            if (profile.integerGridMap)
            {
                onProgress?.Invoke("Refining input", 0f);
                watch.Restart();
                if (Extraction.InputRefine.Refine(solvePositions, solveTriangles,
                        settings.targetQuadCount, out var refinedPositions, out var refinedTriangles,
                        out var refineStats))
                {
                    solvePositions = refinedPositions;
                    solveTriangles = refinedTriangles;
                }
                result.refineStats = refineStats;
                result.refineMs = watch.ElapsedMilliseconds;

            }

            result.originalPositions = positions;
            result.originalTriangles = triangles;
            // The decimated mesh, before refinement: what the parametrisation solves on.
            result.decimatedPositions = refineInputPositions;
            result.decimatedTriangles = refineInputTriangles;
            RunCore(solvePositions, solveTriangles, settings, result, watch, onProgress,
                autoSelectProfile);

            TransferUvs(result, positions, triangles, sourceUvs, watch, onProgress);
            return result;
        }

        /// <summary>
        /// One half (the positive side of the plane) solved with half the quads, mirrored and
        /// welded. See <see cref="Symmetry"/>.
        /// </summary>
        static RemeshResult SolveSymmetric(Vector3[] positions, int[] triangles,
            RemeshSettings settings, RemeshProfile profile, Action<string, float> onProgress,
            Vector2[] sourceUvs, bool autoSelectProfile)
        {
            var axis = settings.symmetry;
            var halfSettings = settings;
            halfSettings.symmetry = SymmetryAxis.None;
            var plane = Symmetry.PlaneOf(positions, axis);
            Symmetry.Clip(positions, triangles, axis, plane, out var halfPositions, out var halfTriangles);
            if (halfTriangles.Length == 0)
                return Solve(positions, triangles, halfSettings, profile, onProgress, sourceUvs, autoSelectProfile);
            halfSettings.targetQuadCount = Mathf.Max(4, (settings.targetQuadCount + 1) / 2);
            var result = SolveInternal(halfPositions, halfTriangles, halfSettings, profile, onProgress, null,
                autoSelectProfile, axis, plane);
            if (result.extraction == null || result.extraction.positions == null) return result;

            // Half the median output edge: the cut's vertices are that close to the plane or closer.
            var lengths = new List<float>();
            var ex = result.extraction;
            foreach (var f in ex.faces)
                for (var i = 0; i < f.Length; i++)
                    lengths.Add((ex.positions[f[(i + 1) % f.Length]] - ex.positions[f[i]]).magnitude);
            lengths.Sort();
            var median = lengths.Count > 0 ? lengths[lengths.Count / 2] : 0f;
            result.symmetryStats = new Symmetry.Stats();
            result.extraction = Symmetry.Mirror(ex, axis, plane, 0.5f * median, result.symmetryStats);
            result.originalPositions = positions;
            result.originalTriangles = triangles;
            TransferUvs(result, positions, triangles, sourceUvs, new System.Diagnostics.Stopwatch(), onProgress);
            return result;
        }

        static void TransferUvs(RemeshResult result, Vector3[] positions, int[] triangles, Vector2[] sourceUvs,
            System.Diagnostics.Stopwatch watch, Action<string, float> onProgress)
        {
            // --- attribute transfer ---
            //
            // Sampled against the ORIGINAL arrays, not `solvePositions`/`solveTriangles`. Those are
            // the decimated intermediate, and sampling them would compound its error into the
            // texture; the original is also the only place the unwelded per-seam UVs exist.
            if (sourceUvs != null && result.extraction != null && result.extraction.positions != null)
            {
                onProgress?.Invoke("Transferring UVs", 0f);
                watch.Restart();
                result.extraction.uvs = AttributeTransfer.SampleUvs(
                    positions, triangles, sourceUvs, result.extraction.positions);
                // Per corner as well, from one chart per face: the per-vertex UVs above smear the
                // texture across every UV seam an output face straddles. Both the mesh and the FBX
                // use these when present.
                result.extraction.faceUvs = AttributeTransfer.SampleFaceUvs(positions, triangles,
                    sourceUvs, result.extraction.positions, result.extraction.faces,
                    out result.faceUvStats);
                result.attributeMs = watch.ElapsedMilliseconds;
            }
        }

        /// <summary>
        /// Turns a solved result into a <see cref="Mesh"/>. Main thread only.
        /// </summary>
        /// <param name="quadTopology">
        /// Keep the quads as quad topology, in their own submesh. False produces a single
        /// triangle submesh — and therefore a single material slot. See
        /// <see cref="QuadExtractor.ToMesh"/>; the face list on <paramref name="result"/> is
        /// unaffected either way, so an export still carries the quads.
        /// </param>
        public static void BuildOutputMesh(RemeshResult result, string sourceName,
            bool quadTopology = true)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            // Named from the ORIGINAL mesh, not the decimated intermediate, so the asset does not
            // come out called "Foo_Decimated_Quads".
            result.mesh = QuadExtractor.ToMesh(result.extraction, sourceName + "_Quads",
                quadTopology);
        }

        /// <summary>
        /// One position solve, hierarchical when a hierarchy was built. Shared between the spacing
        /// probes and the solve that is kept, because a probe that used a different solver would
        /// predict a face count the final solve does not produce.
        /// </summary>
        /// <summary>
        /// The base grading scaled so its mean is <paramref name="scale"/>, or null when uniform.
        /// </summary>
        static ScaleField ProbeField(RemeshResult result, float scale, float baseScale)
        {
            if (result.scaleField == null || !(baseScale > 0f)) return null;
            return Mathf.Approximately(scale, baseScale)
                ? result.scaleField
                : result.scaleField.Scaled(scale / baseScale);
        }

        /// <summary>
        /// The parametrisation path on the decimated mesh: the cross field and spacing sampled from
        /// the refined mesh's nearest vertex, the lattice solved and extracted there.
        /// </summary>
        /// <remarks>
        /// The refined mesh exists for the position field, which samples the lattice at vertices
        /// and needs several per cell. A global parametrisation is piecewise linear over triangles
        /// and needs only that they be no bigger than a cell, which the decimation budget already
        /// guarantees -- and on the refined body the solve cost 6.5 s per sparse factorisation.
        /// </remarks>
        /// <summary>
        /// With the quad layout on an open mesh, the decimated mesh's border is cleared of ears --
        /// triangles with two border edges -- whose tip is sharper than this many degrees, or
        /// flatter than 180 minus it. The layout holds the border straight along runs (see
        /// SeamlessParametrization.layoutBorderRunCells), which would press an ear flat; a scan's
        /// jagged border is mostly ears. 0 keeps them.
        /// </summary>
        public static float layoutTrimEarAngle = 90f;

        /// <summary>
        /// With the quad layout, a vertex where two parts of the mesh touch at a point (a bow-tie,
        /// where two border loops of a scan meet) is split into one vertex per part: the layout's
        /// fans and the border loops need each vertex to have one.
        /// </summary>
        public static bool layoutSplitBowties = true;

        // One vertex per edge-connected fan round each vertex; the triangles are rewritten in place
        // and the new positions appended.
        static Vector3[] SplitBowties(Vector3[] positions, int[] triangles, out int split)
        {
            split = 0;
            var n = positions.Length;
            var corners = new List<int>[n];
            for (var c = 0; c < triangles.Length; c++) (corners[triangles[c]] ??= new List<int>(6)).Add(c);
            var extra = new List<Vector3>();
            for (var v = 0; v < n; v++)
            {
                var list = corners[v];
                if (list == null || list.Count < 2) continue;
                // Corners joined when their triangles share an edge at v.
                var parent = new int[list.Count];
                for (var i = 0; i < parent.Length; i++) parent[i] = i;
                int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
                var byOther = new Dictionary<int, int>();
                for (var i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    var t = c - c % 3;
                    foreach (var w in new[] { triangles[t + (c % 3 + 1) % 3], triangles[t + (c % 3 + 2) % 3] })
                    {
                        if (byOther.TryGetValue(w, out var j)) parent[Find(i)] = Find(j);
                        else byOther[w] = i;
                    }
                }
                var newIndex = new Dictionary<int, int>();
                for (var i = 0; i < list.Count; i++)
                {
                    var root = Find(i);
                    if (root == Find(0)) continue;
                    if (!newIndex.TryGetValue(root, out var id))
                    {
                        id = n + extra.Count;
                        extra.Add(positions[v]);
                        newIndex[root] = id;
                        split++;
                    }
                    triangles[list[i]] = id;
                }
            }
            if (extra.Count == 0) return positions;
            var all = new Vector3[n + extra.Count];
            Array.Copy(positions, all, n);
            for (var i = 0; i < extra.Count; i++) all[n + i] = extra[i];
            return all;
        }

        // Removes border ears, again where removing one leaves another, a few rounds.
        static int[] TrimBorderEars(Vector3[] positions, int[] triangles, out int trimmed)
        {
            trimmed = 0;
            var tris = new List<int>(triangles);
            for (var round = 0; round < 4; round++)
            {
                var count = new Dictionary<long, int>(tris.Count, Extraction.EdgeKey.Comparer);
                long Key(int a, int b) => ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
                for (var t = 0; t < tris.Count; t += 3)
                    for (var k = 0; k < 3; k++)
                    {
                        var key = Key(tris[t + k], tris[t + (k + 1) % 3]);
                        count[key] = count.TryGetValue(key, out var c) ? c + 1 : 1;
                    }
                var faces = new Dictionary<int, int>();
                for (var t = 0; t < tris.Count; t++) faces[tris[t]] = faces.TryGetValue(tris[t], out var c) ? c + 1 : 1;
                var keep = new List<int>(tris.Count);
                var removed = 0;
                for (var t = 0; t < tris.Count; t += 3)
                {
                    var ear = false;
                    for (var k = 0; k < 3 && !ear; k++)
                    {
                        int v = tris[t + k], a = tris[t + (k + 1) % 3], b = tris[t + (k + 2) % 3];
                        if (faces[v] != 1 || count[Key(v, a)] != 1 || count[Key(v, b)] != 1) continue;
                        // Not the last triangle on its own (a lone ear is a whole part).
                        if (count[Key(a, b)] != 2) continue;
                        var angle = Vector3.Angle(positions[a] - positions[v], positions[b] - positions[v]);
                        ear = angle < layoutTrimEarAngle || angle > 180f - layoutTrimEarAngle;
                    }
                    if (ear) { removed++; continue; }
                    keep.Add(tris[t]); keep.Add(tris[t + 1]); keep.Add(tris[t + 2]);
                }
                trimmed += removed;
                tris = keep;
                if (removed == 0) break;
            }
            return tris.ToArray();
        }

        static void ParametrizeDecimated(RemeshResult result, PositionField solved,
            ExtractionOptions options, out RemeshTopology topology, out OrientationField orientation,
            out PositionField position, out ExtractionOptions coarseOptions)
        {
            if (result.decimatedTopology == null && Extraction.IntegerGridMap.parametrizationLayout)
            {
                if (layoutTrimEarAngle > 0f)
                    result.decimatedTriangles = TrimBorderEars(result.decimatedPositions, result.decimatedTriangles,
                        out result.trimmedEars);
                if (layoutSplitBowties)
                    result.decimatedPositions = SplitBowties(result.decimatedPositions, result.decimatedTriangles,
                        out result.splitBowties);
            }
            topology = result.decimatedTopology ??= RemeshTopology.Build(result.decimatedPositions,
                result.decimatedTriangles);
            var fine = result.topology;
            var nearest = result.decimatedToRefined;
            if (nearest == null || nearest.Length != topology.vertexCount)
            {
                nearest = new int[topology.vertexCount];
                var cell = Mathf.Max(fine.averageEdgeLength, 1e-9f);
                var hash = new Dictionary<(int, int, int), List<int>>();
                (int, int, int) Key(Vector3 p) =>
                    (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
                for (var i = 0; i < fine.vertexCount; i++)
                {
                    var k = Key(fine.positions[i]);
                    if (!hash.TryGetValue(k, out var list)) hash[k] = list = new List<int>(4);
                    list.Add(i);
                }
                for (var v = 0; v < topology.vertexCount; v++)
                {
                    var p = topology.positions[v];
                    var best = 0;
                    var bestD = float.PositiveInfinity;
                    for (var ring = 1; ring <= 8 && float.IsPositiveInfinity(bestD); ring *= 2)
                    {
                        var (x, y, z) = Key(p);
                        for (var dx = -ring; dx <= ring; dx++)
                        for (var dy = -ring; dy <= ring; dy++)
                        for (var dz = -ring; dz <= ring; dz++)
                        {
                            if (!hash.TryGetValue((x + dx, y + dy, z + dz), out var list)) continue;
                            foreach (var i in list)
                            {
                                var d = (fine.positions[i] - p).sqrMagnitude;
                                if (d < bestD) { bestD = d; best = i; }
                            }
                        }
                    }
                    nearest[v] = best;
                }
                result.decimatedToRefined = nearest;
            }

            var cross = new Vector3[topology.vertexCount];
            var spacing = new float[topology.vertexCount];
            for (var v = 0; v < topology.vertexCount; v++)
            {
                var n = topology.normals[v];
                var q = result.orientation.directions[nearest[v]];
                cross[v] = MeshMath.SafeNormalize(q - n * Vector3.Dot(n, q), OrientationField.ArbitraryTangent(n));
                spacing[v] = solved.SpacingAt(nearest[v]);
            }

            // The sampled field carries the refined field's fine-scale noise, and on the coarser
            // mesh that noise is singularity pairs between neighbouring vertices. A few sweeps on
            // the decimated mesh comb it out; constrained vertices are not involved here.
            if (Extraction.IntegerGridMap.parametrizationFieldSweeps > 0)
                cross = OrientationField.Solve(topology, Extraction.IntegerGridMap.parametrizationFieldSweeps,
                    null, -1, null, null, cross).directions;

            // The lines the grid must follow: the open border always, and with Detect Hard Edges
            // the coherent creases, found again on this mesh.
            var featureEdges = Extraction.IntegerGridMap.parametrizationFeatures
                ? result.decimatedFeatureEdges ??= DecimatedFeatureEdges(result, topology)
                : null;

            // Same mesh, same field on every pass of the face-count search: sampled once.
            if (result.decimatedCross != null && result.decimatedCross.Length == cross.Length) cross = result.decimatedCross;
            else
            {
                // The field solved again on the decimated mesh itself, globally (see GlobalCrossField):
                // the sampled one carries the refined field's singularity noise, and on this mesh a
                // cluster of singularities a fraction of a cell apart is what folds the map once they
                // are rounded onto grid nodes. Locked along the creases the grid follows; the sampled
                // field stays as a guide of weight parametrizationGlobalFieldGuide.
                if (Extraction.IntegerGridMap.parametrizationGlobalField)
                {
                    var nv = topology.vertexCount;
                    var lockDir = new Vector3[nv];
                    var lockWeight = new float[nv];
                    if (featureEdges != null)
                        foreach (var (a, b) in featureEdges)
                        {
                            if (a < 0 || b < 0 || a >= nv || b >= nv) continue;
                            var d = topology.positions[b] - topology.positions[a];
                            foreach (var v in new[] { a, b })
                            {
                                var nrm = topology.normals[v];
                                var t = MeshMath.SafeNormalize(d - nrm * Vector3.Dot(nrm, d), Vector3.zero);
                                if (t == Vector3.zero) continue;
                                if (lockWeight[v] > 0f)
                                {
                                    // Summed as crosses: turned onto the first direction's quadrant.
                                    var r = lockDir[v].normalized;
                                    var best = t;
                                    var bestDot = float.NegativeInfinity;
                                    var c = t;
                                    for (var k = 0; k < 4; k++)
                                    {
                                        var dd = Vector3.Dot(c, r);
                                        if (dd > bestDot) { bestDot = dd; best = c; }
                                        c = Vector3.Cross(nrm, c);
                                    }
                                    t = best;
                                }
                                lockDir[v] += t;
                                lockWeight[v] = 1f;
                            }
                        }
                    var guideWeight = new float[nv];
                    for (var v = 0; v < nv; v++) guideWeight[v] = Extraction.IntegerGridMap.parametrizationGlobalFieldGuide;
                    cross = GlobalCrossField.Solve(topology, lockDir, lockWeight, cross, guideWeight,
                        Extraction.IntegerGridMap.globalFieldMaxIterations, Extraction.IntegerGridMap.globalFieldTolerance,
                        out result.decimatedFieldStats);
                }
                result.decimatedCross = cross;
            }
            result.parametrizationCache ??= new Extraction.ParametrizationCache();
            Extraction.SeamlessParametrization.Solve(topology, cross, v => spacing[v], featureEdges,
                result.parametrizationCache,
                out var nodes, out var axes, out var cells,
                out var cornerNodes, out var cornerUv, out var cornerUvExact, out var faceUAxis, out var cornerRank, out var singularFaces,
                out var halfEdgeAcross, out var halfEdgeTurn,
                out result.parametrizationStats);

            // Solved coarse, extracted fine: see ParametrizationTransfer.
            if (Extraction.IntegerGridMap.parametrizationExtractRefined && !Extraction.IntegerGridMap.parametrizationTrace
                && !Extraction.IntegerGridMap.parametrizationLayout)
            {
                var transferWatch = System.Diagnostics.Stopwatch.StartNew();
                Extraction.ParametrizationTransfer.ToRefined(result.topology, topology, cornerUv, faceUAxis,
                    halfEdgeAcross, halfEdgeTurn, solved.SpacingAt,
                    out var fineNodes, out var fineAxes, out var fineCells,
                    out var fineCornerNodes, out var fineCornerUv, out var fineFaceUAxis,
                    out var fineCornerRank, out var fineSingular, out var unmapped);
                result.parametrizationStats.transferMs = transferWatch.ElapsedMilliseconds;
                result.parametrizationStats.refinedSingularFaces = fineSingular.Length;
                result.parametrizationStats.unmappedFaces = unmapped;
                topology = result.topology;
                position = solved.OnRefinedVertices(fineNodes, fineCells);
                position.cornerNodes = fineCornerNodes;
                position.cornerUv = fineCornerUv;
                position.faceUAxis = fineFaceUAxis;
                position.cornerRank = fineCornerRank;
                position.singularFaces = fineSingular;
                orientation = new OrientationField { directions = fineAxes };
                coarseOptions = options;
                return;
            }

            position = solved.OnRefinedVertices(nodes, cells);
            position.cornerNodes = cornerNodes;
            position.cornerUv = cornerUv;
            position.cornerUvExact = cornerUvExact;
            position.layoutPositions = result.parametrizationStats.layoutPositions;
            position.layoutNormals = result.parametrizationStats.layoutNormals;
            position.layoutFaces = result.parametrizationStats.layoutFaces;
            position.faceUAxis = faceUAxis;
            position.cornerRank = cornerRank;
            position.singularFaces = singularFaces;
            orientation = new OrientationField { directions = axes };

            coarseOptions = options.Clone();
            coarseOptions.creaseLoopPositions = options.featurePositions;
            coarseOptions.featurePositions = topology.positions;
            coarseOptions.features = result.decimatedFeatures ??= FeatureGraph.Detect(topology,
                result.profile.featureAngleDegrees);
        }

        static List<(int a, int b)> DecimatedFeatureEdges(RemeshResult result, RemeshTopology topology)
        {
            var edges = new List<(int a, int b)>();
            void Add(FeatureGraph graph)
            {
                if (graph == null) return;
                for (var c = 0; c < graph.polylineCount; c++)
                    for (var i = graph.polylineStart[c] + 1; i < graph.polylineStart[c + 1]; i++)
                        edges.Add((graph.polylineVertices[i - 1], graph.polylineVertices[i]));
            }
            // Boundaries, and the profile's own creases where it has any.
            Add(FeatureGraph.Detect(topology, result.profile.featureAngleDegrees));
            if (Extraction.IntegerGridMap.coherentCreases && result.profile.featureAngleDegrees <= 0f)
            {
                var cell = topology.EdgeLengthForQuadCount(Mathf.Max(1, result.effectiveTargetQuadCount));
                var creases = FeatureGraph.Detect(topology, Extraction.IntegerGridMap.coherentCreaseAngle)
                    .KeepCoherentCurves(topology, Extraction.IntegerGridMap.parametrizationCreaseMinCells * cell,
                        Extraction.IntegerGridMap.coherentCreaseStraightness, out _, out _);
                // Two grid lines closer than a cell squash the cells between them flat.
                // The layout lays every crease as a patch border, so it always wants them apart:
                // the helmet rim's cable and lip a fraction of a cell apart came out as spikes.
                var separation = Extraction.IntegerGridMap.parametrizationLayout
                    ? Mathf.Max(Extraction.IntegerGridMap.parametrizationCreaseSeparationCells, Extraction.IntegerGridMap.layoutCreaseSeparationCells)
                    : Extraction.IntegerGridMap.parametrizationCreaseSeparationCells;
                if (separation > 0f)
                    creases = creases.KeepSeparatedCurves(topology,
                        separation * cell,
                        Extraction.IntegerGridMap.coherentCreaseMaxShared, out _);
                Add(creases);
            }
            return edges;
        }

        static SpatialGrid SurfaceFor(RemeshResult result, Vector3[] positions, int[] triangles) =>
            projectOntoOriginal && result.originalPositions != null
                ? new SpatialGrid(result.originalPositions, result.originalTriangles)
                : new SpatialGrid(positions, triangles);

        static PositionField SolvePositions(RemeshResult result, float scale, ScaleField scaleField,
            int iterations, Action<float> onProgress, HierarchicalStats stats)
        {
            // The creases go to the POSITION field, which is what decides where cell boundaries
            // fall. They already went to the orientation field, and that alone produced quads
            // running parallel to a panel edge and offset from it — detected, aligned, and missed.
            var features = result.profile.snapLatticeToFeatures ? result.features : null;

            if (result.hierarchy != null)
                return HierarchicalSolve.Positions(result.hierarchy, result.orientation, scale,
                    scaleField, result.profile.hierarchyLevelIterations, iterations, onProgress,
                    stats, result.profile.positionSpacingMultiple, features);

            return PositionField.Solve(result.topology, result.orientation, scale, scaleField,
                iterations, onProgress, null, features);
        }

        static void RunCore(Vector3[] positions, int[] triangles, RemeshSettings settings,
            RemeshResult result, System.Diagnostics.Stopwatch watch,
            Action<string, float> onProgress, bool autoSelectProfile = false)
        {
            onProgress?.Invoke("Building topology", 0f);
            watch.Restart();
            result.topology = RemeshTopology.Build(positions, triangles);
            result.topologyMs = watch.ElapsedMilliseconds;

            if (result.topology.vertexCount == 0 || result.topology.triangleCount == 0)
                throw new InvalidOperationException("Mesh has no usable triangles.");

            // --- profile auto-selection ---
            //
            // HERE, and not in the editor, because the threshold was calibrated on THIS population:
            // the 32 % the reference scan marks is measured after pre-decimation, on 26,073 of
            // 27,368 vertices. Classifying the undecimated 1.5 M-triangle input instead would read
            // a different mesh against a number that does not describe it, and would cost a second
            // topology build of the largest array in the pipeline.
            //
            // Decimation has already run, under the profile the caller supplied. That is safe
            // because every shipped profile decimates identically — preDecimate = true, 10
            // triangles per quad — so the stage is profile-independent in practice. A hand-built
            // profile that disagrees is honoured as given, which is the conservative reading of a
            // setting somebody typed.
            // Named for what it probes, not just "probe": the face-count search later in this
            // method has a local of its own by that name, and the two are unrelated.
            FeatureGraph profileProbe = null;
            if (autoSelectProfile)
            {
                onProgress?.Invoke("Choosing profile", 0f);
                profileProbe = FeatureGraph.Detect(result.topology, autoProfileProbeAngleDegrees);
                result.profileProbeFeatureFraction = result.preRefineProbeFeatureFraction >= 0f
                    ? result.preRefineProbeFeatureFraction
                    : FeatureFraction(profileProbe);
                var chosen = RemeshProfile.For(SelectProfileKind(result.profileProbeFeatureFraction));
                // The extractor is a caller's choice, not a property of the input, so it rides
                // through from the seed profile rather than being reset by the selection.
                chosen.extractor = result.profile.extractor;
                result.profile = chosen.Validated();
                result.profileWasAutoSelected = true;

                // The sweep counts live on the profile and were folded into `settings` by Solve,
                // before this method was called. Re-fold them, or the chosen profile would run the
                // seed profile's budget. `settings` is a value parameter, so this is local to the
                // rest of this method, which is exactly its scope.
                settings = result.profile.ApplyTo(settings).Validated();
            }

            // The integer path grades harder than the profiles' collapse-calibrated value; see
            // IntegerGridMap.gradedAdaptivity. Only where the profile grades at all, so a profile
            // that asked for a uniform lattice keeps one.
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.gradeSpacing
                && result.profile.curvatureAdaptivity > 0f)
                result.profile.curvatureAdaptivity = Mathf.Max(result.profile.curvatureAdaptivity,
                    Extraction.IntegerGridMap.gradedAdaptivity);

            // The window's "Adaptive Size", when set, replaces both.
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.adaptiveSize >= 0f)
                result.profile.curvatureAdaptivity = Mathf.Clamp01(Extraction.IntegerGridMap.adaptiveSize
                                                                   * Extraction.IntegerGridMap.adaptiveSizeScale);

            // And turns the orientation hierarchy on, which every shipped profile has off for a
            // reason that only held for the collapse extractor; see
            // IntegerGridMap.orientationHierarchyLevelIterations.
            if (result.profile.integerGridMap && !result.profile.useOrientationHierarchy
                && Extraction.IntegerGridMap.orientationHierarchyLevelIterations > 0)
                result.profile.orientationHierarchyLevelIterations =
                    Extraction.IntegerGridMap.orientationHierarchyLevelIterations;

            // Diagnostic overrides, applied to whichever profile was chosen.
            if (debugCurvatureAdaptivity >= 0f) result.profile.curvatureAdaptivity = debugCurvatureAdaptivity;
            if (debugCurvatureMaxRatio > 1f) result.profile.curvatureMaxRatio = debugCurvatureMaxRatio;
            if (debugFeatureAngle >= 0f) result.profile.featureAngleDegrees = debugFeatureAngle;
            if (debugOrientationHierarchyLevelIterations > 0)
                result.profile.orientationHierarchyLevelIterations = debugOrientationHierarchyLevelIterations;

            // After profile selection, not before it: the selection re-folds the chosen profile's
            // sweep counts into `settings`, and when this lived in Run that silently put the budget
            // back to the unrefined value on every Auto Profile run -- which is every run from the
            // window. The headless audit never selected a profile automatically and so never saw it.
            // Refining multiplies the vertex count, and the orientation sweep budget was
            // calibrated against the UNrefined one -- the Photogrammetry profile's 60 is
            // annotated as the point where a 27k-vertex field is converged, and refinement
            // takes the same asset to 95k. Smoothing propagates a fixed number of edges per
            // sweep, so on a mesh with k times the faces the same budget reaches sqrt(k)
            // times less far, and the field arrives at extraction under-converged: measured
            // on a head scan at 5000 quads, leaving it at 60 costs Hausdorff 0.0173 -> 0.0314
            // and 467 -> 538 irregular vertices for nothing.
            //
            // Scaling with the face growth restores the reach the budget was chosen for.
            // It is capped because the relationship stops paying well before then: at four
            // times this the same asset gets WORSE, 681 irregular, the field having been
            // smoothed past the detail the singularities need to sit on.
            // --- Adaptive Quad Count (integer grid map) ---
            //
            // QuadRemesher's own default, and a different reading of the target: it sizes the quads
            // on FLAT ground as a uniform mesh of the requested count would, and lets curved areas
            // take smaller quads on top -- so the result can carry more quads than were asked for
            // (its documentation's example asks for 100 and gets about 600). The add-on the user
            // runs ships with it on (adapt_quad_count = True -> ExactQuadCount=0). Held to the
            // exact count instead, every quad a finger gains is taken from somewhere flat, and on
            // the body at 5000 the four pressed-together fingers of the glove came out as one
            // paddle three quads wide. So the target is raised here, before anything is sized, by
            // how much a field whose flat spacing is the uniform one would need.
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.adaptiveQuadCount
                && result.profile.curvatureAdaptivity > 0f && settings.targetQuadCount > 0)
            {
                var requested = settings.targetQuadCount;
                // Counted at the slider's own value: adaptiveSizeScale softens how steeply the
                // lattice grades, not how many quads the user asked curved areas to gain.
                var countAdaptivity = Extraction.IntegerGridMap.adaptiveQuadCountFromSlider
                                      && Extraction.IntegerGridMap.adaptiveSize >= 0f
                    ? Mathf.Clamp01(Extraction.IntegerGridMap.adaptiveSize)
                    : result.profile.curvatureAdaptivity;
                var shape = ScaleField.FromCurvature(result.topology, requested,
                    countAdaptivity, result.profile.curvatureMaxRatio,
                    result.profile.curvatureGradation);
                var uniform = result.topology.EdgeLengthForQuadCount(requested);
                // "Flat" is the spacing most of the surface is at the coarse end of -- a high
                // percentile rather than the maximum, which one flat vertex could set.
                var flat = AreaPercentile(shape.spacing, result.topology.areas,
                    Extraction.IntegerGridMap.adaptiveFlatPercentile);
                var factor = uniform > 0f && flat > 0f ? (flat / uniform) * (flat / uniform) : 1f;
                factor = Mathf.Clamp(factor, 1f, Extraction.IntegerGridMap.adaptiveQuadCountMaxGrowth);
                result.requestedQuadCount = requested;
                result.adaptiveQuadCountFactor = factor;
                settings.targetQuadCount = Mathf.RoundToInt(requested * factor);
            }

            // --- local refinement for a graded lattice (integer grid map only) ---
            //
            // The global refinement in Run bounds every edge by half the MEAN cell. A graded lattice
            // has cells down to half that in curved regions, where an edge passing the global bound
            // still spans more than one local cell and its offset cannot be represented. So the
            // grading is computed here, on the mesh as refined so far, and any edge longer than
            // its local cell is split down to half of it. The field is recomputed on the refined
            // mesh below as usual; midpoint splits do not change the surface, so it grades the same.
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.gradeSpacing
                && result.profile.curvatureAdaptivity > 0f)
            {
                var localTarget = settings.targetQuadCount;
                var localCeiling = result.topology.MaxQuadCountForInput();
                if (localCeiling > 0 && localTarget > localCeiling) localTarget = localCeiling;
                var grading = ScaleField.FromCurvature(result.topology, localTarget,
                    result.profile.curvatureAdaptivity, result.profile.curvatureMaxRatio,
                    result.profile.curvatureGradation);
                onProgress?.Invoke("Refining input locally", 0f);
                // The WELDED arrays: the grading is per topology vertex, and an input with UV seams
                // or split normals (an OBJ scan, ccnb.obj) has more raw positions than welded ones --
                // passing the raw arrays threw "One spacing per position" there.
                if (Extraction.InputRefine.RefineLocal(result.topology.positions,
                        result.topology.triangles, grading.spacing,
                        out var localPositions, out var localTriangles, out _, out var localStats))
                {
                    positions = localPositions;
                    triangles = localTriangles;
                    result.topology = RemeshTopology.Build(positions, triangles);
                    // Detected on the topology just replaced; its vertex ids no longer apply.
                    profileProbe = null;
                    if (result.refineStats.facesBefore == 0)
                        result.refineStats.facesBefore = localStats.facesBefore;
                    result.refineStats.splits += localStats.splits;
                    result.refineStats.facesAfter = localStats.facesAfter;
                }
                result.localRefineStats = localStats;
            }

            if (result.refineStats.splits > 0 && result.refineStats.facesBefore > 0)
            {
                var growth = result.refineStats.facesAfter / (float)result.refineStats.facesBefore;
                settings.orientationIterations = Mathf.Min(maxOrientationSweeps,
                    Mathf.RoundToInt(settings.orientationIterations * growth));
            }

            // Diagnostic override for sweeps of the budget above; zero leaves it alone.
            if (debugOrientationIterations > 0) settings.orientationIterations = debugOrientationIterations;
            if (debugPositionIterations > 0) settings.positionIterations = debugPositionIterations;


            // --- features ---
            //
            // Boundaries are always detected, whatever the crease threshold: a boundary edge has no
            // surface on the other side, so it is a feature by construction rather than by angle.
            //
            // The probe graph is reused when the chosen profile asks the same question — which is
            // the GameAsset branch, since the probe angle is GameAsset's own. Detecting twice at
            // one angle would be a second pass over every half-edge for an identical answer.
            result.features = profileProbe != null
                              && result.profile.featureAngleDegrees == autoProfileProbeAngleDegrees
                ? profileProbe
                : FeatureGraph.Detect(result.topology, result.profile.featureAngleDegrees);

            // A dihedral threshold cannot tell a crease from noise, and on a photogrammetry scan
            // there is nothing BUT noise at 30 degrees. The reference scan run under GameAsset
            // reports 8,321 feature vertices of 26,073 - a third of the mesh declared to be a
            // crease, on a human body, which has none.
            //
            // That is not a harmless mislabel. Every one of them reaches SurfaceRelaxation, whose
            // fourth guard pins a vertex sitting in an input crease and refuses any move crossing
            // one, so the pass does 43,277 moves instead of 110,785 and the vertices held still are
            // the ones in the soft folds that most needed relaxing - shoulders, chest, armpits.
            // Measured against the same scan under Character: irregular vertices 27.67 % against
            // 24.52 %, valence-2 vertices 52 against 16, vertices left off the surface 14 against 3.
            //
            // The user hit this because the editor window defaults to GameAsset and nothing said
            // the profile was wrong for the input. A threshold cannot be made reliable here, so
            // this does not try to correct it - it reports that the detector is not measuring what
            // it thinks it is, and leaves the choice with the caller.
            result.featureVertexFraction = FeatureFraction(result.features);
            result.featureDetectionSuspect = result.profile.featureAngleDegrees > 0f
                                             && result.featureVertexFraction > suspectFeatureFraction;

            // --- coherent creases, for the profiles that switch dihedral detection off ---
            //
            // Added after the fraction and the suspect flag above, which describe the plain dihedral
            // detector. The scan profiles turn that detector off because on a scan or a detailed
            // sculpt it fires on noise -- and so lose every real crease with it, the grooves between
            // pressed-together fingers among them, which no lattice density follows unless something
            // places loops in them. QuadRemesher's "detect hard edges" is on by default and finds
            // them. Here only the crease chains that run straight for more than a cell or so are
            // kept, which is what separates a groove, seam or panel line from noise fragments. See
            // FeatureGraph.KeepCoherentCurves.
            FeatureGraph coherentCreases = null;
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.coherentCreases
                && result.profile.featureAngleDegrees <= 0f)
            {
                var creaseCell = result.topology.EdgeLengthForQuadCount(Mathf.Max(1, settings.targetQuadCount));
                coherentCreases = FeatureGraph.Detect(result.topology,
                        Extraction.IntegerGridMap.coherentCreaseAngle)
                    .KeepCoherentCurves(result.topology,
                        Extraction.IntegerGridMap.coherentCreaseMinCells * creaseCell,
                        Extraction.IntegerGridMap.coherentCreaseStraightness,
                        out result.coherentCreasesKept, out result.coherentCreasesDropped);
                // Cutting along them as well -- sharp offsets, crease resampling, vertex placement
                // -- glues long strips of the lattice into single cells wherever a crease is doubled
                // or noisy: on the body, needle quads up to 11x the median edge across the thighs,
                // and 1.89 -> 3.26 % irregular. Steering the cross field alone puts a loop in the
                // finger grooves with neither (Hausdorff 0.0093 -> 0.0090).
                if (Extraction.IntegerGridMap.coherentCreasesCut)
                {
                    // Only the long creases are cut when coherentCreaseCutMinCells asks for it: a
                    // long clean panel edge becomes an exact iso-line, while the short dense ones
                    // (velcro, stitching) that tear a cut lattice keep steering only.
                    var toCut = Extraction.IntegerGridMap.coherentCreaseCutMinCells
                                > Extraction.IntegerGridMap.coherentCreaseMinCells
                        ? coherentCreases.KeepCoherentCurves(result.topology,
                            Extraction.IntegerGridMap.coherentCreaseCutMinCells * creaseCell,
                            Mathf.Max(Extraction.IntegerGridMap.coherentCreaseStraightness,
                                Extraction.IntegerGridMap.coherentCreaseCutStraightness), out _, out _)
                        : coherentCreases;
                    result.features = Extraction.IntegerGridMap.coherentCreaseMinSeparationCells > 0f
                        ? toCut.KeepSeparatedCurves(result.topology,
                            Extraction.IntegerGridMap.coherentCreaseMinSeparationCells * creaseCell,
                            Extraction.IntegerGridMap.coherentCreaseMaxShared,
                            out result.coherentCreasesThinned)
                        : toCut;
                }
            }

            // --- the curve network: cut the lattice along ridges and valleys (integer grid map) ---
            //
            // Added AFTER the fraction and the suspect flag above, which describe the dihedral
            // detector; these curves are a different detector with its own guards against noise
            // (hysteresis, a minimum length, one curve per bevel). See CurveNetwork.
            // What the cross field is constrained by: the features, or with align-only curves,
            // the features plus the curves.
            var constraintGraph = coherentCreases ?? result.features;
            var featuresBeforeCurves = result.features;
            Vector3[] curveTangents = null;
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.curveNetwork)
            {
                onProgress?.Invoke("Tracing curves", 0f);
                var curveCell = result.topology.EdgeLengthForQuadCount(
                    Mathf.Max(1, settings.targetQuadCount));
                result.curveNetworkEdges = CurveNetwork.Trace(result.topology, curveCell,
                    out curveTangents, out var curveCorners, out result.curveNetworkStats);
                if (result.curveNetworkEdges.Count > 0)
                {
                    var withCurves = result.features.WithEdges(result.topology,
                        result.curveNetworkEdges, curveTangents, curveCorners);
                    if (Extraction.IntegerGridMap.curveNetworkCut) result.features = withCurves;
                    // As guides (below), the field keeps only the plain features as constraints;
                    // otherwise it is constrained by the curves too.
                    constraintGraph = CurveNetwork.guideWeight > 0f ? featuresBeforeCurves : withCurves;
                }
            }

            Vector3[] constraintDirections = null;
            float[] constraintWeights = null;
            if (constraintGraph.featureEdgeCount > 0)
            {
                var tangentRadius = result.profile.integerGridMap
                    ? Extraction.IntegerGridMap.featureTangentRadiusCells
                      * result.topology.EdgeLengthForQuadCount(Mathf.Max(1, settings.targetQuadCount))
                    : 0f;
                constraintGraph.ToOrientationConstraints(out constraintDirections, out constraintWeights,
                    tangents: tangentRadius > 0f && constraintGraph.polylinesCarried
                        ? constraintGraph.SmoothedTangents(result.topology, tangentRadius)
                        : null);
            }

            // --- the hierarchy ---
            //
            // Built once and used by both field solves. Without it each solve is Gauss-Seidel at
            // the finest level only, which propagates information one edge per sweep and therefore
            // reaches a neighbourhood rather than the surface however many sweeps it is given —
            // measured, 60/50 and 300/300 sweeps land within 1.3 points of each other on irregular
            // vertices. See HierarchicalSolve.
            if (result.profile.useHierarchy)
            {
                watch.Restart();
                result.hierarchy = MeshHierarchy.Build(result.topology);
                result.hierarchyMs = watch.ElapsedMilliseconds;
            }

            // Solved at the finest level, deliberately, and HierarchicalSolve's header carries the
            // measurement: coarse-to-fine here is worth nothing on its own and costs 3.4 points of
            // quad fraction on the profile that carries feature constraints, because restricting a
            // crease direction onto a coarse normal averaged across the crease points it nowhere.
            // Unlike a lattice, a cross field has no length scale and no reach problem.
            watch.Restart();

            // --- field normals smoothed to the output's scale (integer grid map) ---
            //
            // The cross field is smoothed against each vertex's NORMAL: every neighbour's direction
            // is rotated into this vertex's tangent plane before it is averaged. On a detailed
            // sculpt the normals turn at the scale of the sculpting -- pores, folds, stitching --
            // which is far finer than a quad, and each of those turns is a place the field can wind
            // into a singularity pair. They are real minima of the energy being solved, so more
            // sweeps and a hierarchy converge onto them rather than out of them: measured on the
            // helmet head, flat and hierarchical solves both settle at hundreds of singularities
            // and three hierarchy settings land on the identical 872.
            //
            // So the field is solved against normals averaged over about a cell, the scale the
            // output can actually express, and projected back onto the true tangent planes after.
            // Diffusion covers distance as the square root of the sweep count, so a radius of r
            // cells needs (r * cell / edge)^2 sweeps. Only the field sees these normals; the
            // position solve and the extraction keep the real surface.
            // --- principal-curvature guides (integer grid map) ---
            //
            // Computed here, against the TRUE normals and before they are smoothed below: the
            // estimate is the surface's bending, and the solver projects each guide onto whatever
            // tangent plane it is working in. See CurvatureDirections.
            Vector3[] guideDirections = null;
            float[] guideWeights = null;
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.curvatureAlignment > 0f)
            {
                var guideCell = result.topology.EdgeLengthForQuadCount(
                    Mathf.Max(1, settings.targetQuadCount));
                CurvatureDirections.Compute(result.topology, guideCell,
                    Extraction.IntegerGridMap.curvatureAlignment,
                    Extraction.IntegerGridMap.curvatureRadiusCells,
                    out guideDirections, out guideWeights, out result.curvatureGuideStats);
            }

            // Curve directions as guides, when asked for instead of hard constraints.
            if (curveTangents != null && CurveNetwork.guideWeight > 0f)
            {
                guideDirections ??= new Vector3[result.topology.vertexCount];
                guideWeights ??= new float[result.topology.vertexCount];
                for (var i = 0; i < curveTangents.Length; i++)
                {
                    if (curveTangents[i] == Vector3.zero) continue;
                    guideDirections[i] = curveTangents[i];
                    guideWeights[i] = CurveNetwork.guideWeight;
                }
            }

            var trueNormals = result.topology.normals;
            var orientationHierarchy = result.hierarchy;
            if (result.profile.integerGridMap
                && (orientationNormalSmoothingCells > 0f || debugOrientationNormalSweeps > 0
                    || Extraction.IntegerGridMap.orientationNormalSweeps > 0))
            {
                var cell = result.topology.EdgeLengthForQuadCount(Mathf.Max(1, settings.targetQuadCount));
                var edge = result.topology.averageEdgeLength;
                var reach = edge > 0f ? orientationNormalSmoothingCells * cell / edge : 0f;
                var sweeps = debugOrientationNormalSweeps > 0
                    ? debugOrientationNormalSweeps
                    : orientationNormalSmoothingCells > 0f
                        ? Mathf.Clamp(Mathf.RoundToInt(reach * reach), 0, 4000)
                        : Extraction.IntegerGridMap.orientationNormalSweeps;
                result.orientationNormalSweeps = sweeps;
                if (sweeps > 0)
                {
                    result.topology.normals = SmoothNormals(result.topology, sweeps);
                    if (result.hierarchy != null && result.profile.useOrientationHierarchy)
                        orientationHierarchy = MeshHierarchy.Build(result.topology);
                }
            }

            // The global smoothest field, when asked for, replaces the local descent's starting point
            // and most of its sweeps. See GlobalCrossField.
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.globalField)
            {
                onProgress?.Invoke("Orientation field", 0f);
                var initial = GlobalCrossField.Solve(result.topology, constraintDirections,
                    constraintWeights, guideDirections, guideWeights,
                    Extraction.IntegerGridMap.globalFieldMaxIterations,
                    Extraction.IntegerGridMap.globalFieldTolerance, out result.globalFieldStats,
                    Extraction.IntegerGridMap.globalFieldSoftenLocks);
                result.orientation = Extraction.IntegerGridMap.globalFieldPolishSweeps > 0
                    ? OrientationField.Solve(result.topology,
                        Extraction.IntegerGridMap.globalFieldPolishSweeps,
                        p => onProgress?.Invoke("Orientation field", p), -1,
                        constraintDirections, constraintWeights, initial,
                        guideDirections, guideWeights)
                    : new OrientationField { directions = initial };
            }
            else
            result.orientation = orientationHierarchy != null && result.profile.useOrientationHierarchy
                ? HierarchicalSolve.Orientations(orientationHierarchy,
                    result.profile.orientationHierarchyLevelIterations,
                    settings.orientationIterations,
                    p => onProgress?.Invoke("Orientation field", p),
                    constraintDirections, constraintWeights,
                    result.orientationLevels = new HierarchicalStats(),
                    guideDirections, guideWeights)
                : OrientationField.Solve(result.topology, settings.orientationIterations,
                    p => onProgress?.Invoke("Orientation field", p), -1,
                    constraintDirections, constraintWeights, null,
                    guideDirections, guideWeights);

            // Close pairs of opposite singularities cancelled, against the same normals the field
            // was solved on. See GlobalCrossField.CancelPairs.
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.cancelSingularPairs)
            {
                var pairCell = result.topology.EdgeLengthForQuadCount(Mathf.Max(1, settings.targetQuadCount));
                for (var round = 0; round < Mathf.Max(1, Extraction.IntegerGridMap.cancelPairRounds); round++)
                {
                    GlobalCrossField.CancelPairs(result.topology, result.orientation.directions,
                        constraintWeights, Extraction.IntegerGridMap.cancelPairCells * pairCell,
                        Extraction.IntegerGridMap.cancelPairRimCells * pairCell, 2000,
                        out var singularBefore, out var singularAfter, out var pairsTried);
                    if (round == 0) result.singularBeforeCancel = singularBefore;
                    result.singularAfterCancel = singularAfter;
                    result.singularPairsTried += pairsTried;
                    if (pairsTried == 0 || singularAfter >= singularBefore) break;
                }
            }

            if (!ReferenceEquals(result.topology.normals, trueNormals))
            {
                result.topology.normals = trueNormals;
                var directions = result.orientation.directions;
                for (var i = 0; i < directions.Length; i++)
                {
                    var n = trueNormals[i];
                    var d = directions[i] - n * Vector3.Dot(n, directions[i]);
                    directions[i] = MeshMath.SafeNormalize(d, directions[i]);
                }

                // Projected onto the true, noisy normals the field picks up singularity pairs a
                // triangle or two apart that it did not have against the smoothed ones. A few local
                // sweeps on the true normals comb those out without moving anything else.
                if (result.profile.integerGridMap && Extraction.IntegerGridMap.trueNormalPolishSweeps > 0)
                    result.orientation = OrientationField.Solve(result.topology,
                        Extraction.IntegerGridMap.trueNormalPolishSweeps, null, -1,
                        constraintDirections, constraintWeights, result.orientation.directions,
                        guideDirections, guideWeights);
            }
            result.orientationMs = watch.ElapsedMilliseconds;

            // The lattice is sampled at input vertices, so this can only coarsen. Asking for quads
            // finer than the input triangles leaves every vertex on its own lattice point and hands
            // back something close to the original triangulation — output that looks exactly like a
            // solver bug and has been misread as one before. Clamp, and record that we did.
            var ceiling = result.topology.MaxQuadCountForInput();
            var target = settings.targetQuadCount;
            if (ceiling > 0 && target > ceiling)
            {
                target = ceiling;
                result.targetWasClamped = true;
            }
            result.effectiveTargetQuadCount = target;

            var scale = result.topology.EdgeLengthForQuadCount(target);

            // --- curvature-adaptive sizing ---
            //
            // The field is built ONCE, normalised to the target, and the probe loop below then
            // scales the whole of it by the same factor it would have applied to a scalar. Building
            // it per probe would re-derive the grading from curvature each time, which cannot
            // change, and would make the probe's correction and the grading fight for the same
            // degree of freedom.
            //
            // `scale` remains the scalar mean throughout, because two things genuinely want one
            // number: the hierarchy depth (how far the lattice can usefully coarsen is a question
            // about the mesh as a whole) and the probe's own Newton step.
            // --- curvature grading is incompatible with the integer grid map as adapted ---
            //
            // The map stores one lattice offset per input edge, and InputRefine establishes the
            // invariant that makes those offsets representable by bounding the longest input edge
            // against the lattice spacing. That bound is computed from a single GLOBAL spacing,
            // sqrt(area / targetQuads). Grading breaks it: in a high-curvature region the local
            // spacing is a fraction of the global one, so an edge that passes the global bound can
            // still span several LOCAL cells. Those offsets come out over-length, subdivision cycles
            // trying to fix them, gives up, and the long edges that survive cannot pair into quads --
            // they become holes.
            //
            // Measured on the body scan at 4700 quads from an identical 50k-triangle input:
            //
            //             graded            uniform
            //   subdivide ABANDONED, 1871   converged, 0 long
            //   boundary  724 edges         12
            //   pieces    7                 1
            //   irregular 684 (15.9 %)      262 (5.9 %)
            //
            // QuadriFlow does not hit this because its own adaptive scale is off unless asked for
            // (`-adaptive`); the reference runs a uniform lattice. The real fix is to bound refinement
            // against the MINIMUM local spacing rather than the global mean, which would need the
            // scale field solved before refinement rather than after. Until then the map runs uniform,
            // because grading buys shape fidelity and holes are not a trade.
            //
            // That fix now exists: the local refinement pass near the top of this method bounds
            // every edge by its LOCAL cell, and IntegerGridMap.gradeSpacing lets the field through.
            // With it the head comes out in one piece at graded spacing where it had torn into
            // five, so the switch is on; off, this reverts to the uniform lattice described above.
            if (result.profile.curvatureAdaptivity > 0f
                && (!result.profile.integerGridMap || Extraction.IntegerGridMap.gradeSpacing))
            {
                onProgress?.Invoke("Sizing by curvature", 0f);
                result.scaleField = ScaleField.FromCurvature(result.topology, target,
                    result.profile.curvatureAdaptivity, result.profile.curvatureMaxRatio,
                    result.profile.curvatureGradation);
                scale = result.scaleField.meanSpacing;
            }

            var baseScale = scale;

            var extractionOptions = ExtractionOptions.Default;
            extractionOptions.features = result.features;
            extractionOptions.featurePositions = result.topology.positions;
            // Every coherent crease, cut or not: a vertex already on a cut crease is at distance
            // zero and stays, the creases left uncut get their nearest loop.
            if (Extraction.IntegerGridMap.creaseLoopSnap && coherentCreases != null)
                extractionOptions.creaseLoops = coherentCreases;
            extractionOptions.mergeTrianglePairs = result.profile.mergeTrianglePairs;
            extractionOptions.cornerHeight = result.profile.cornerHeight;
            extractionOptions.maxClusterSpread = result.profile.clusterSpread;
            extractionOptions.subdivideLongEdges = result.profile.subdivideLongEdges;
            extractionOptions.pairTrianglesAcrossQuads = result.profile.pairTrianglesAcrossQuads;
            if (result.profile.trianglePairingMaxChainQuads > 0)
                extractionOptions.trianglePairingMaxChainQuads =
                    result.profile.trianglePairingMaxChainQuads;
            if (result.profile.trianglePairingMaxRounds > 0)
                extractionOptions.trianglePairingMaxRounds = result.profile.trianglePairingMaxRounds;
            extractionOptions.recordCensus = result.profile.recordCensus;
            if (result.profile.mergeSplitCellsBelowCells >= 0f)
                extractionOptions.mergeSplitCellsBelowCells =
                    result.profile.mergeSplitCellsBelowCells;
            if (result.profile.trianglePairingMaxReflexDegrees >= 0f)
                extractionOptions.trianglePairingMaxReflexDegrees =
                    result.profile.trianglePairingMaxReflexDegrees;
            if (result.profile.trianglePairingQualityThreshold > 0f)
                extractionOptions.trianglePairingQualityThreshold =
                    result.profile.trianglePairingQualityThreshold;
            extractionOptions.relaxSurface = result.profile.relaxSurface;
            extractionOptions.relaxIterations = result.profile.relaxIterations;
            extractionOptions.relaxStrength = result.profile.relaxStrength;
            if (debugRelaxIterations >= 0)
            {
                extractionOptions.relaxIterations = debugRelaxIterations;
                extractionOptions.relaxSurface = debugRelaxIterations > 0;
            }
            if (debugRelaxMaxDrift >= 0f) extractionOptions.relaxMaxDrift = debugRelaxMaxDrift;
            extractionOptions.integerGridMap = result.profile.integerGridMap;

            // The SAME angle the profile uses for feature detection, and deliberately so: both
            // answer "does this input have creases worth preserving". A profile that switches
            // detection off is asserting its input has none — the two scan profiles do, because on
            // a scan the things that look like creases at output resolution are folds and noise —
            // and relaxation must then be free to smooth across them, or it spends the whole pass
            // protecting features that are not there. Measured on the character, protecting them
            // costs nearly the entire improvement.
            extractionOptions.relaxFeatureAngleDegrees = result.profile.featureAngleDegrees;

            // Deliberately left null for now. The probe loop below extracts several times to find
            // the lattice spacing that hits the target face count, and relaxation cannot change a
            // face count — it only moves vertices. Attaching the surface here would pay for it on
            // every probe to no effect. It is attached once, for the solve that is kept.
            extractionOptions.surface = null;

            // --- choose the lattice spacing, with cheap probes ---
            //
            // Face count goes as area / scale^2, so the multiplicative error in the count is the
            // squared multiplicative error in the spacing, and one step of scale *= sqrt(achieved /
            // target) is a Newton step on an almost exactly quadratic relationship.
            //
            // A loop is needed rather than a better first guess because the achieved count depends
            // on how the lattice happens to interact with the input tessellation, which varies by
            // mesh: measured across the corpus at a coarse target, a single analytic guess scatters
            // between 0.53x and 1.16x of the request, in both directions. No constant fixes that.
            //
            // What changed is what each step COSTS. Every attempt used to run the full sweep budget
            // and then be discarded, so a four-attempt correction paid for four complete position
            // solves to keep one. On the 990k-triangle scan that was 22 seconds per solve: 90 of
            // the run's 99 seconds went into fields whose only surviving output was an integer.
            //
            // A probe does not need a converged field. It needs the count a given spacing produces,
            // and that is decided by which vertices land on the same lattice cell — settled early,
            // because Gauss-Seidel covers most of the distance in its first few sweeps and spends
            // the rest refining. So the probes run at a fraction of the budget, they pick the
            // spacing, and the field is then solved once, properly, at that spacing.
            //
            // The fraction is a quarter with a floor rather than a fixed small number: on a mesh
            // whose budget is already low, a quarter of it still separates the cells, where a flat
            // 8 sweeps would not.
            // --- integer grid map: every attempt is a full solve, and the first one in tolerance is kept ---
            //
            // The probes below are cheap for the collapse extractor, whose extraction is a few
            // hundred milliseconds. The integer grid map's is the flow, subdivision, flip repair and
            // hole closing -- the bulk of a run -- and a probe paid all of it to be thrown away even
            // when it landed in tolerance, after which the kept solve repeated it at the same
            // spacing: measured on the body at 5,000, one probe plus the kept solve, two complete
            // extractions for one result. Here each attempt is solved properly, relaxed and kept if
            // its count is close enough; only a miss costs a second pass. The map's count does need
            // correcting -- uncorrected it lands 6-18 % short (head 4,176, body 4,697 and ccnb
            // 4,120 at 5,000) -- so the attempts and their Newton step are the probe loop's own.
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.keepInToleranceSolve
                && settings.faceCountIterations > 1)
            {
                if (result.profile.relaxSurface)
                {
                    onProgress?.Invoke("Indexing surface", 0f);
                    extractionOptions.surface = SurfaceFor(result, positions, triangles);
                }

                QuadExtractionResult bestExtraction = null;
                PositionField bestPosition = null;
                ScaleField bestField = null;
                HierarchicalStats bestLevels = null;
                var bestScale = scale;
                var bestError = float.PositiveInfinity;
                long positionTotal = 0, extractionTotal = 0;

                for (var attempt = 1; attempt <= settings.faceCountIterations; attempt++)
                {
                    result.faceCountAttempts = attempt;
                    var label = attempt == 1 ? "Position field" : $"Position field (pass {attempt})";
                    var field = ProbeField(result, scale, baseScale);
                    var levels = result.hierarchy != null ? new HierarchicalStats() : null;

                    watch.Restart();
                    var parametrize = Extraction.IntegerGridMap.parametrization
                        && (Extraction.IntegerGridMap.parametrizationOnOpenMeshes || result.topology.boundaryEdgeCount == 0
                            || (Extraction.IntegerGridMap.parametrizationLayout && Extraction.IntegerGridMap.layoutOnOpenMeshes));
                    // The quad layout reads nothing of the position field but its spacing: its
                    // sweeps (3 s a pass on the head at 5k) are skipped.
                    var latticeUnused = parametrize && Extraction.IntegerGridMap.parametrizationLayout
                        && Extraction.IntegerGridMap.parametrizationOnDecimated && result.decimatedPositions != null;
                    var solved = latticeUnused
                        ? PositionField.Solve(result.topology, result.orientation, scale, field, 0, null, null, null)
                        : SolvePositions(result, scale, field, settings.positionIterations,
                            p => onProgress?.Invoke(label, p), levels);

                    // The global seamless parametrisation, when asked for, replaces the position
                    // field's lattice with one solved for the whole surface at once; the extraction
                    // downstream is unchanged. See SeamlessParametrization.
                    var extractOrientation = result.orientation;
                    var extractTopology = result.topology;
                    var extractOptions = extractionOptions;
                    if (parametrize && Extraction.IntegerGridMap.parametrizationOnDecimated
                        && result.decimatedPositions != null)
                    {
                        onProgress?.Invoke("Parametrising", 0f);
                        ParametrizeDecimated(result, solved, extractionOptions,
                            out extractTopology, out extractOrientation, out solved, out extractOptions);
                    }
                    else if (parametrize)
                    {
                        onProgress?.Invoke("Parametrising", 0f);
                        Extraction.SeamlessParametrization.Solve(result.topology,
                            result.orientation.directions, solved.SpacingAt, null, null,
                            out var nodes, out var axes, out var cells,
                            out var cornerNodes, out var cornerUv, out var cornerUvExact, out var faceUAxis, out var cornerRank, out var singularFaces,
                            out _, out _, out result.parametrizationStats);
                        solved = solved.OnRefinedVertices(nodes, cells);
                        if (Extraction.IntegerGridMap.parametrizationOffsets)
                        {
                            solved.cornerNodes = cornerNodes;
                            solved.cornerUv = cornerUv;
                            solved.faceUAxis = faceUAxis;
                            solved.cornerRank = cornerRank;
                            solved.singularFaces = singularFaces;
                        }
                        extractOrientation = new OrientationField { directions = axes };
                    }
                    positionTotal += watch.ElapsedMilliseconds;

                    onProgress?.Invoke(attempt == 1 ? "Extracting quads" : $"Extracting quads (pass {attempt})", 0f);
                    watch.Restart();
                    var extracted = QuadExtractor.Extract(extractTopology, extractOrientation,
                        solved, extractOptions);
                    if (extracted.parametrizationTraceStats != null && result.parametrizationStats != null)
                        result.parametrizationStats.trace = extracted.parametrizationTraceStats.ToString();
                    extractionTotal += watch.ElapsedMilliseconds;

                    var achieved = extracted.faceCount;
                    result.faceCountTrace += $"{achieved}@{scale:G4} ";
                    var error = achieved > 0 ? Mathf.Abs(achieved / (float)target - 1f) : float.PositiveInfinity;
                    if (bestExtraction == null || error < bestError)
                    {
                        bestError = error;
                        bestScale = scale;
                        bestExtraction = extracted;
                        bestPosition = solved;
                        bestField = field;
                        bestLevels = levels;
                    }

                    if (achieved <= 0 || error <= settings.faceCountTolerance) break;

                    var correction = Mathf.Sqrt(achieved / (float)target);
                    correction = Mathf.Clamp(1f + 0.85f * (correction - 1f), 0.5f, 2f);
                    scale *= correction;
                }

                result.scaleField = bestField;
                result.position = bestPosition;
                result.positionLevels = bestLevels;
                result.extraction = bestExtraction;
                result.positionMs = positionTotal;
                result.extractionMs = extractionTotal;
                result.scale = bestScale;
                onProgress?.Invoke("Done", 1f);
                return;
            }

            if (settings.faceCountIterations > 1)
            {
                var probeIterations = Mathf.Clamp(settings.positionIterations / 4, 6, 50);

                // Keep the best spacing seen, not the last one tried. Face count is not perfectly
                // monotonic in the spacing: coarsening merges more vertices, but a merge refused
                // for spreading too far falls back to an output edge instead, so a coarser lattice
                // can occasionally yield MORE faces. That breaks the assumption a Newton iteration
                // rests on. Measured before this: a capped cylinder finished at 2.01x the request
                // having passed closer on the way.
                var bestProbeScale = scale;
                var bestProbeError = float.PositiveInfinity;

                for (var attempt = 1; attempt < settings.faceCountIterations; attempt++)
                {
                    result.faceCountAttempts = attempt;

                    var probeLabel = $"Finding lattice spacing ({attempt}/" +
                                     $"{settings.faceCountIterations - 1})";

                    var probePosition = SolvePositions(result, scale, ProbeField(result, scale, baseScale),
                        probeIterations, p => onProgress?.Invoke(probeLabel, p), null);

                    var probe = QuadExtractor.Extract(result.topology, result.orientation,
                        probePosition, extractionOptions);

                    var probeAchieved = probe.faceCount;
                    if (probeAchieved <= 0)
                    {
                        // Nothing to steer by. Coarsening further would only remove more, so stop
                        // rather than thrash and let the final solve report the empty result.
                        break;
                    }

                    var probeRatio = probeAchieved / (float)target;
                    var probeError = Mathf.Abs(probeRatio - 1f);

                    if (probeError < bestProbeError)
                    {
                        bestProbeError = probeError;
                        bestProbeScale = scale;
                    }

                    if (probeError <= settings.faceCountTolerance) break;

                    // Damp the step. The relationship is only approximately quadratic, and an
                    // undamped correction can oscillate between two spacings that straddle the
                    // target.
                    var correction = Mathf.Sqrt(probeRatio);
                    correction = Mathf.Clamp(1f + 0.85f * (correction - 1f), 0.5f, 2f);
                    scale *= correction;
                }

                scale = bestProbeScale;
            }

            // --- the solve that is kept ---
            result.faceCountAttempts++;

            watch.Restart();
            result.positionLevels = result.hierarchy != null ? new HierarchicalStats() : null;
            // The probe settled on `scale`; the kept field is the base grading scaled by exactly the
            // factor that took the mean there, so the grading the probe measured is the grading
            // that gets solved.
            result.scaleField = ProbeField(result, scale, baseScale);
            result.position = SolvePositions(result, scale, result.scaleField,
                settings.positionIterations,
                p => onProgress?.Invoke("Position field", p), result.positionLevels);
            result.positionMs = watch.ElapsedMilliseconds;

            // The surface relaxation projects onto is the one the lattice was solved against —
            // `positions`/`triangles` as this method received them, already pre-decimated by the
            // caller. Projecting onto the undecimated original instead would pull vertices toward
            // detail the solver never saw and could not have laid quads along.
            if (result.profile.relaxSurface)
            {
                onProgress?.Invoke("Indexing surface", 0f);
                extractionOptions.surface = SurfaceFor(result, positions, triangles);
            }

            onProgress?.Invoke("Extracting quads", 0f);
            watch.Restart();
            result.extraction = QuadExtractor.Extract(result.topology, result.orientation,
                result.position, extractionOptions);
            result.extractionMs = watch.ElapsedMilliseconds;
            result.scale = scale;


            onProgress?.Invoke("Done", 1f);
        }
    }
}
