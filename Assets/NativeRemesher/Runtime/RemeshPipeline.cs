// Native Remesher for Unity — the whole pipeline in one call.

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

        [Tooltip("Mirror symmetry in X, Y or Z: one half is remeshed and " +
                 "mirrored, welded along the plane. The plane is the local origin when the mesh " +
                 "straddles it, else the middle of the bounds.")]
        public SymmetryAxis symmetry;

        [Tooltip("The borders between the input's materials " +
                 "(submeshes) become edge loops, and every face keeps its material - the result " +
                 "has one submesh per input material.")]
        public bool materialBorders;

        /// <summary>
        /// Areas painted for more or fewer quads, in the input's space (the window's Paint Fix
        /// Areas). Null or empty: none.
        /// </summary>
        public DensityPaint densityPaint;

        /// <summary>
        /// Which kind of model the settings are chosen for (the window's Model Type). General, the
        /// default, is the pipeline as it was before the type existed; Auto reads it off the mesh.
        /// </summary>
        public ModelType modelType;

        /// <summary>What Auto read off the mesh; filled in by the pipeline.</summary>
        [NonSerialized] public ShapeSignals shapeSignals;

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
                symmetry = symmetry,
                materialBorders = materialBorders,
                densityPaint = densityPaint,
                modelType = modelType,
                shapeSignals = shapeSignals
            };
        }
    }

    public sealed class RemeshResult
    {
        /// <summary>A copy sharing every field (arrays included); the caller replaces what it changes.</summary>
        public RemeshResult ShallowCopy() => (RemeshResult)MemberwiseClone();

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

        /// <summary>
        /// The texture baked by <see cref="RemeshPipeline.BakeTexture"/>, square, row 0 at the
        /// bottom; null when none was baked. The extraction's faceUvs then index it.
        /// </summary>
        public Color32[] bakedTexture;
        public int bakedTextureSize;

        /// <summary>The source's further maps baked over the same atlas as bakedTexture (BakeTexture's layers); null: none.</summary>
        public TextureBake.Layer[] bakedLayers;

        /// <summary>A tangent-space normal map of the input over the same atlas (BakeNormalMap); null when not baked.</summary>
        public Color32[] bakedNormalMap;
        public int bakedNormalMapSize;

        /// <summary>The source's cavities over the normal map's atlas (TextureBake.bakeCavity), grey; null when not baked.</summary>
        public Color32[] bakedOcclusion;
        public TextureBake.Stats bakeStats;
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

        /// <summary>The model type this run was solved as (Auto resolved to what it read).</summary>
        public ModelType modelType;

        /// <summary>What Auto read off the mesh; not measured when the type was chosen by hand.</summary>
        public ShapeSignals shapeSignals;

        /// <summary>Share of the input within two cells of the output (RemeshPipeline.layoutCoverageFallback); 0 when not measured.</summary>
        public float coverage;

        /// <summary>
        /// The quad layout's coverage when it was given up for a solve without it
        /// (RemeshPipeline.layoutCoverageFallback); 0 when it was not.
        /// </summary>
        public float layoutCoverage;

        /// <summary>Pre-decimation results, or null when that stage did not run.</summary>
        public DecimateStats decimateStats;

        /// <summary>Detected sharp features. Never null after a successful run.</summary>
        public FeatureGraph features;

        /// <summary>The graded lattice spacing actually solved, or null when uniform.</summary>
        public ScaleField scaleField;

        /// <summary>The painted areas this run sized by (RemeshSettings.densityPaint); null when none.</summary>
        public DensityPaint densityPaint;

        /// <summary>
        /// Quads the painted field asks for over the unpainted one: the target is raised by it, so
        /// painted areas gain quads instead of taking them from the rest.
        /// </summary>
        public float paintCountRatio;

        internal float[] paintFactors;
        internal RemeshTopology paintFactorsFor;

        /// <summary>
        /// Quads the thickness cap (RemeshPipeline.thicknessSizing) adds over the field without it:
        /// the target is raised by it, so fingers gain quads instead of taking them from the rest.
        /// Zero when it did not run.
        /// </summary>
        public float thinCountRatio;

        /// <summary>
        /// The share of <see cref="thinCountRatio"/> Hard Surface's rods add (RemeshPipeline.hardSurfaceRods);
        /// creases are read at the count without it, so the rods' quads change nothing else. 0 or 1: none.
        /// </summary>
        public float rodCountRatio;

        /// <summary>Hard Surface's rods would have raised the count this much, past RemeshPipeline.hardSurfaceRodMaxGrowth, and were not refined.</summary>
        public float rodsSkipped;

        /// <summary>Open strips' widths per solve vertex (RemeshPipeline.stripSizing), and the topology they are for.</summary>
        internal float[] stripWidth;
        internal RemeshTopology stripWidthFor;

        internal float[] thickness;
        internal RemeshTopology thicknessFor;

        /// <summary>
        /// The count the thickness cap would have multiplied the target by, when that was past
        /// RemeshPipeline.thicknessMaxGrowth and it was not used; zero when it was used or not tried.
        /// </summary>
        public float thicknessSkipped;

        /// <summary>Lattice spacing actually used, after face-count correction.</summary>
        public float scale;
        /// <summary>How many correction passes ran. 1 means the first attempt was close enough.</summary>
        /// <summary>Crease chains kept and dropped by the coherent-crease filter; zero when it did not run.</summary>
        public int creaseLoopsKept, creaseLoopsDropped, creaseLoopsThinned;

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
        /// <summary>The input simplified to the output's budget, for RemeshPipeline.sizingFromDecimation.</summary>
        [NonSerialized] public Vector3[] sizingCoarsePositions;
        [NonSerialized] public int[] sizingCoarseTriangles;
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

        /// <summary>Material borders kept as edge loops (see <see cref="RemeshSettings.materialBorders"/>).</summary>
        [NonSerialized] public MaterialLabels materials;
        public int materialBorderEdges, decimatedMaterialBorderEdges;

        /// <summary>The mirror, when <see cref="RemeshSettings.symmetry"/> asked for one.</summary>
        public Symmetry.Stats symmetryStats;

        /// <summary>Face count reached by each integer grid attempt, "count@spacing", in order.</summary>
        public string faceCountTrace = "";
        /// <summary>Target after clamping to the coarsen-only ceiling.</summary>
        public int effectiveTargetQuadCount;

        /// <summary>Hard Surface creases left out as dense detail (RemeshPipeline.hardSurfaceSparseCreases).</summary>
        public int denseCreasesDropped;

        /// <summary>
        /// The creases came from the flat panels (RemeshPipeline.hardSurfacePanelCreases): the
        /// result is also fitted onto them, its normals split from
        /// RemeshPipeline.panelHardEdgeAngle, and no building's quads are tried for it.
        /// </summary>
        public bool panelCreases;

        /// <summary>The panels were asked for creases and gave too few (see RemeshPipeline.panelCreasesMinShare).</summary>
        public bool panelCreasesRefused;

        /// <summary>What RemeshPipeline.hardSurfaceSimplify did, when it ran.</summary>
        public QuadSimplify.Stats simplifyStats;

        /// <summary>What RemeshPipeline.hardSurfacePlateBlocks did, when it ran.</summary>
        public PlateBlocks.Stats plateBlocksStats;


        /// <summary>
        /// The count asked for, before Adaptive Quad Count raised it; see
        /// IntegerGridMap.curvesAddQuads. Zero when the mode did not run.
        /// </summary>
        public int requestedQuadCount;

        /// <summary>How far Adaptive Quad Count multiplied the target; 1 when off.</summary>
        public float curveCountFactor = 1f;

        /// <summary>The solver's mesh was held to RemeshPipeline.maxSolveTriangles.</summary>
        public bool solveTrianglesCapped;

        /// <summary>What RemeshPipeline.surfaceCleanIterations did, when on.</summary>
        public SurfaceClean.Stats surfaceCleanStats;

        /// <summary>What SurfaceFair did, when Hard Surface's output was faired.</summary>
        public SurfaceFair.Stats fairStats;

        /// <summary>The creases read again on the source (RemeshPipeline.hardSurfaceSourceCreases), the last search's.</summary>
        public SourceCreases.Stats sourceCreaseStats;
        [NonSerialized] internal SourceCreases.Index sourceCreaseIndex;
        public StarFill.Stats starFillStats;

        /// <summary>How the error-driven quads measured against the lattice, when they were tried (detailQuadsTry); null otherwise.</summary>
        public string detailTry;

        /// <summary>What RemeshPipeline.ribbonParts found and meshed, when on.</summary>
        public RibbonParts.Stats ribbonStats;

        /// <summary>What RemeshPipeline.featureFitIterations did, when on.</summary>
        public FeatureFit.Stats featureFitStats;

        /// <summary>What RemeshPipeline.detailQuads did, when on.</summary>
        public DetailQuads.Stats detailQuadsStats;

        /// <summary>What RemeshPipeline.planarQuads did, when on.</summary>
        public PlanarQuads.Stats planarQuadsStats;

        /// <summary>The strip reduction's counters when it was tried (RemeshPipeline.quadInputReduction); null otherwise.</summary>
        public QuadReduction.Stats quadReductionStats;

        /// <summary>The axis the result was turned about (RemeshPipeline.Revolved); null when it was not.</summary>
        public Revolution.Fit revolution;

        /// <summary>What the motion recognition found (RemeshPipeline.kinematicGuides); null when off.</summary>
        public KinematicSurfaces.Stats kinematicStats;
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
        /// Curves Add Quads 600 sweeps over 517k faces took 42 s where 150 took 6.5 s for the same
        /// result (irregular 4.41 / 4.64 %, Hausdorff 0.0103 / 0.0095).
        /// </summary>
        public static int maxOrientationSweeps = 200;

        /// <summary>
        /// Relax the output onto the caller's ORIGINAL mesh rather than the pre-decimated one the
        /// solver ran on. Off is the original behaviour; see the comment at the surface index.
        /// </summary>
        public static bool projectOntoOriginal = false;

        /// <summary>
        /// Passes of FeatureFit over the output against the caller's original mesh: each vertex
        /// moved onto the rims, creases and corners nearest it. 0: off.
        /// </summary>
        public static int featureFitIterations = 0;

        /// <summary>
        /// Passes of SurfaceClean over the output: lone spikes and dents, and faces folded over
        /// their neighbours, smoothed into their ring. 0: every vertex stays where the solve put
        /// it, as before.
        /// </summary>
        public static int surfaceCleanIterations = 2;

        /// <summary>
        /// SurfaceClean reads the solve's surface and keeps the spikes that are real sharp tips (a
        /// cone's apex was cut down by a whole edge). False: as before.
        /// </summary>
        public static bool surfaceCleanUsesSurface = true;

        /// <summary>
        /// Build the quads from an error-driven simplification of the input instead of a lattice
        /// (DetailQuads): irregular, but rims, corners and small parts kept at a low count, and
        /// seconds rather than a minute. Off: the lattice pipeline.
        /// </summary>
        public static bool detailQuads = false;

        /// <summary>
        /// Build the quads as a building's (PlanarQuads): flat walls as grids through their
        /// windows' corners, windows and doors as stepped reliefs, the rest error-driven. Off: the
        /// lattice pipeline (or the error-driven quads, with detailQuads).
        /// </summary>
        public static bool planarQuads = false;

        /// <summary>
        /// An input that already is clean quads (a modelled part: 95 % of its triangles pair into
        /// quads) is reduced by removing whole quad strips (QuadReduction) instead of remeshed, so
        /// its loops, lips, flanges and poles stay the artist's. Hard Surface and Character only;
        /// when the strips cannot get near the target the remesher runs as before. Off: always the
        /// remesher.
        /// </summary>
        public static bool quadInputReduction = true;

        /// <summary>
        /// Pieces a rotation, translation or screw carries into themselves get the motion's
        /// direction as a guide (KinematicSurfaces): a tyre in rings, an extrusion in rows. Off: as before.
        /// </summary>
        public static bool kinematicGuides = true;

        /// <summary>
        /// Motion regions inside a piece (KinematicSurfaces.regions) for this type. Measured: Scan
        /// better (ccnb 5000 Hausdorff 0.0169 -> 0.0135, a building's walls are extrusions); Character
        /// worse (helmet irregular 5.4 -> 7.9 %, a head is near enough a solid of revolution to mislead).
        /// </summary>
        static bool KinematicRegionsFor(ModelType type) =>
            type == ModelType.Scan && kinematicRegionsScan || type == ModelType.HardSurface && kinematicRegionsHardSurface;

        /// <summary>The window's Follow Shape Regions, per type.</summary>
        public static bool kinematicRegionsScan = true;
        public static bool kinematicRegionsHardSurface = true;

        /// <summary>Weight of a motion guide in the orientation field (curve guides: 0.5).</summary>
        public static float kinematicGuideWeight = 1f;

        /// <summary>The strip reduction is tried for this type (the window's Keep Source Loops, per type).</summary>
        public static bool StripsFor(ModelType type) =>
            type == ModelType.HardSurface && quadReductionHardSurface || type == ModelType.Character && quadReductionCharacter;

        /// <summary>Keep Source Loops for Hard Surface and for Character models.</summary>
        public static bool quadReductionHardSurface = true;
        public static bool quadReductionCharacter = true;

        [ThreadStatic] static bool t_NoStrips;

        /// <summary>
        /// A part reduced by edge collapse to about twice <paramref name="targetQuads"/> triangles,
        /// whose triangles are then paired into quads: for a part that cannot be seen and whose
        /// strips ran out. Remeshing such a part to its share (16-136 quads on the Porsche's seats,
        /// pedals, gauges) took 20-30 s each and made a blob all the same.
        /// </summary>
        /// <summary>
        /// How far the source strays from a result, in the result's own quad widths: the largest
        /// distance from points spread over the source to the result's surface, over the square
        /// root of the result's mean face area. Above a couple, a piece is missing or folded.
        /// </summary>
        /// <summary>
        /// The affine map taking part <paramref name="a"/>'s vertices onto part <paramref name="b"/>'s
        /// when b is a copy of a: the same triangles over the same vertex count (or each wound the
        /// other way, as a mirrored copy is written), every vertex where one map (a move, a turn, a
        /// mirror, a scale) puts it, to <see cref="sameShapeTolerance"/> of the size.
        /// </summary>
        public static bool SameShape(Vector3[] a, int[] aTriangles, Vector3[] b, int[] bTriangles, out Matrix4x4 map)
        {
            map = Matrix4x4.identity;
            if (a == null || b == null || a.Length < 4 || b.Length < 4 || aTriangles.Length != bTriangles.Length) return false;
            if (a.Length != b.Length || !SameShapeByVertex(a, aTriangles, b, bTriangles, out map))
                return SameShapeByAxes(a, b, out map);
            return true;
        }

        static bool SameShapeByVertex(Vector3[] a, int[] aTriangles, Vector3[] b, int[] bTriangles, out Matrix4x4 map)
        {
            map = Matrix4x4.identity;
            var same = true;
            for (var i = 0; i < aTriangles.Length && same; i++) same = aTriangles[i] == bTriangles[i];
            for (var k = 0; k + 2 < aTriangles.Length && !same; k += 3)
                if (aTriangles[k] != bTriangles[k] || aTriangles[k + 1] != bTriangles[k + 2] || aTriangles[k + 2] != bTriangles[k + 1])
                    return false;
            // Least squares on the centred points: M = Cba Caa^-1.
            var ca = Vector3.zero;
            var cb = Vector3.zero;
            for (var i = 0; i < a.Length; i++) { ca += a[i]; cb += b[i]; }
            ca /= a.Length;
            cb /= b.Length;
            double[,] aa = new double[3, 3], ba = new double[3, 3];
            for (var i = 0; i < a.Length; i++)
            {
                var p = a[i] - ca;
                var q = b[i] - cb;
                for (var r = 0; r < 3; r++)
                    for (var c = 0; c < 3; c++)
                    {
                        aa[r, c] += p[r] * p[c];
                        ba[r, c] += q[r] * p[c];
                    }
            }
            var inv = Invert3(aa);
            if (inv == null) return false;
            var m = Matrix4x4.identity;
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                {
                    double v = 0;
                    for (var k = 0; k < 3; k++) v += ba[r, k] * inv[k, c];
                    m[r, c] = (float)v;
                }
            var t = cb - m.MultiplyVector(ca);
            m.SetColumn(3, new Vector4(t.x, t.y, t.z, 1f));
            var size = 0f;
            foreach (var q in b) size = Mathf.Max(size, (q - cb).magnitude);
            var tolerance = Mathf.Max(sameShapeTolerance * size, 1e-9f);
            for (var i = 0; i < a.Length; i++)
                if ((m.MultiplyPoint3x4(a[i]) - b[i]).sqrMagnitude > tolerance * tolerance) return false;
            map = m;
            return true;
        }

        /// <summary>
        /// A copy with its vertices welded or ordered differently (Unity's import of the Ford's
        /// tyres: 32,296 and 32,163 vertices, the same 27,520 triangles): moved, mirrored on an axis
        /// or turned half round one, about its centre -- how a copied wheel is placed. Every sampled
        /// vertex of each must land on a vertex of the other.
        /// </summary>
        static bool SameShapeByAxes(Vector3[] a, Vector3[] b, out Matrix4x4 map)
        {
            map = Matrix4x4.identity;
            if (Math.Abs(a.Length - b.Length) > 0.02 * Math.Max(a.Length, b.Length)) return false;
            Bounds Box(Vector3[] v)
            {
                var box = new Bounds(v[0], Vector3.zero);
                foreach (var q in v) box.Encapsulate(q);
                return box;
            }
            var ba = Box(a);
            var bb = Box(b);
            var size = Mathf.Max(bb.size.x, Mathf.Max(bb.size.y, bb.size.z));
            var tolerance = Mathf.Max(sameShapeTolerance * size, 1e-9f);
            if ((ba.size - bb.size).magnitude > 4f * tolerance) return false;
            var cell = 2f * tolerance;
            HashSet<(int, int, int)> Hash(Vector3[] v)
            {
                var h = new HashSet<(int, int, int)>();
                foreach (var q in v) h.Add((Mathf.FloorToInt(q.x / cell), Mathf.FloorToInt(q.y / cell), Mathf.FloorToInt(q.z / cell)));
                return h;
            }
            bool Near(HashSet<(int, int, int)> h, Vector3 q)
            {
                int x = Mathf.FloorToInt(q.x / cell), y = Mathf.FloorToInt(q.y / cell), z = Mathf.FloorToInt(q.z / cell);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dz = -1; dz <= 1; dz++)
                            if (h.Contains((x + dx, y + dy, z + dz))) return true;
                return false;
            }
            var ha = Hash(a);
            var hb = Hash(b);
            var stepA = Math.Max(1, a.Length / 3000);
            var stepB = Math.Max(1, b.Length / 3000);
            for (var signs = 0; signs < 8; signs++)
            {
                var d = new Vector3((signs & 1) != 0 ? -1f : 1f, (signs & 2) != 0 ? -1f : 1f, (signs & 4) != 0 ? -1f : 1f);
                var m = Matrix4x4.Scale(d);
                var t = bb.center - m.MultiplyVector(ba.center);
                m.SetColumn(3, new Vector4(t.x, t.y, t.z, 1f));
                var inverse = m.inverse;
                var ok = true;
                for (var i = 0; i < a.Length && ok; i += stepA) ok = Near(hb, m.MultiplyPoint3x4(a[i]));
                for (var i = 0; i < b.Length && ok; i += stepB) ok = Near(ha, inverse.MultiplyPoint3x4(b[i]));
                if (!ok) continue;
                map = m;
                return true;
            }
            return false;
        }

        /// <summary>
        /// How far, as a share of a part's size, a copy's vertex may sit from where the map puts it.
        /// The Ford's OBJ rounds to 4 decimals: its four rims, exact copies, differ by 7e-5 of their size.
        /// </summary>
        public static float sameShapeTolerance = 5e-4f;

        static double[,] Invert3(double[,] m)
        {
            var det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                      - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                      + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
            var scale = Math.Abs(m[0, 0]) + Math.Abs(m[1, 1]) + Math.Abs(m[2, 2]);
            if (!(Math.Abs(det) > 1e-12 * scale * scale * scale)) return null;
            var r = new double[3, 3];
            r[0, 0] = (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) / det;
            r[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / det;
            r[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / det;
            r[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) / det;
            r[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / det;
            r[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / det;
            r[2, 0] = (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) / det;
            r[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / det;
            r[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / det;
            return r;
        }

        /// <summary>
        /// A solved part moved by <paramref name="map"/> onto a copy of its source (SameShape): its
        /// own positions, normals and faces, wound back under a mirror; per-vertex and per-corner
        /// UVs carried (the copy's are the same).
        /// </summary>
        public static RemeshResult Moved(RemeshResult result, Matrix4x4 map)
        {
            var ex = result?.extraction;
            if (ex == null || ex.positions == null) return null;
            var copy = result.ShallowCopy();
            var moved = ex.ShallowCopy();
            copy.extraction = moved;
            copy.mesh = null;
            moved.positions = new Vector3[ex.positions.Length];
            for (var i = 0; i < moved.positions.Length; i++) moved.positions[i] = map.MultiplyPoint3x4(ex.positions[i]);
            var normalMap = map.inverse.transpose;
            Vector3[] Normals(Vector3[] n)
            {
                if (n == null) return null;
                var o = new Vector3[n.Length];
                for (var i = 0; i < o.Length; i++) o[i] = MeshMath.SafeNormalize(normalMap.MultiplyVector(n[i]), Vector3.up);
                return o;
            }
            moved.normals = Normals(ex.normals);
            moved.shadingNormals = Normals(ex.shadingNormals);
            var mirrored = map.determinant < 0f;
            moved.faces = new List<int[]>(ex.faces.Count);
            Vector2[][] faceUvs = ex.faceUvs != null ? new Vector2[ex.faceUvs.Length][] : null;
            for (var f = 0; f < ex.faces.Count; f++)
            {
                var face = ex.faces[f];
                if (face == null) { moved.faces.Add(null); continue; }
                var g = (int[])face.Clone();
                var uv = faceUvs != null && f < ex.faceUvs.Length && ex.faceUvs[f] != null ? (Vector2[])ex.faceUvs[f].Clone() : null;
                if (mirrored)
                {
                    // Kept at its first corner, the rest backwards.
                    Array.Reverse(g, 1, g.Length - 1);
                    if (uv != null && uv.Length == g.Length) Array.Reverse(uv, 1, uv.Length - 1);
                }
                moved.faces.Add(g);
                if (faceUvs != null && f < faceUvs.Length) faceUvs[f] = uv;
            }
            moved.faceUvs = faceUvs;
            moved.faceTangents = null;
            moved.faceNormals = null;
            return copy;
        }

        public static float CellsOff(Vector3[] positions, int[] triangles, QuadExtractionResult extraction, int samples = 4000)
        {
            var tris = LodChain.Triangulate(extraction.faces, out _);
            if (tris.Length < 3) return float.PositiveInfinity;
            double area = 0;
            for (var t = 0; t + 2 < tris.Length; t += 3)
                area += Vector3.Cross(extraction.positions[tris[t + 1]] - extraction.positions[tris[t]],
                    extraction.positions[tris[t + 2]] - extraction.positions[tris[t]]).magnitude * 0.5;
            var cell = (float)Math.Sqrt(area / Math.Max(1, extraction.faces.Count));
            if (!(cell > 0f)) return float.PositiveInfinity;
            var grid = new SpatialGrid(extraction.positions, tris);
            var points = SpatialGrid.SampleSurface(positions, triangles, samples, 5);
            var worst = new float[points.Count];
            System.Threading.Tasks.Parallel.For(0, points.Count, i => worst[i] = grid.NearestDistance(points[i], out _));
            var max = 0f;
            foreach (var w in worst) max = Mathf.Max(max, w);
            if (cellsOffBothWays)
            {
                // And from the result to the source: surface the source does not have (the Ford's
                // tyre, remeshed to 200 quads, closed over its rim as a disc) is as wrong as
                // surface lost, and no point of the source is far from it.
                var source = new SpatialGrid(positions, triangles);
                var mine = SpatialGrid.SampleSurface(extraction.positions, tris, samples, 6);
                var back = new float[mine.Count];
                System.Threading.Tasks.Parallel.For(0, mine.Count, i => back[i] = source.NearestDistance(mine[i], out _));
                foreach (var w in back) max = Mathf.Max(max, w);
            }
            return max / cell;
        }

        /// <summary>CellsOff measures both ways (false: only from the source to the result, as before).</summary>
        public static bool cellsOffBothWays = true;

        [ThreadStatic] public static DecimateStats lastCollapseStats;

        public static void CollapseToQuads(Vector3[] positions, int[] triangles, int targetQuads,
            out Vector3[] outPositions, out List<int[]> outFaces)
        {
            // Welded first: the mesh as Unity holds it is split at its UV seams, which the collapse
            // would open into cracks.
            var map = new Dictionary<Vector3, int>();
            var weld = new int[positions.Length];
            var welded = new List<Vector3>();
            for (var i = 0; i < positions.Length; i++)
            {
                if (!map.TryGetValue(positions[i], out var w)) { w = welded.Count; map[positions[i]] = w; welded.Add(positions[i]); }
                weld[i] = w;
            }
            var tris = new List<int>(triangles.Length);
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = weld[triangles[t]], b = weld[triangles[t + 1]], c = weld[triangles[t + 2]];
                if (a == b || b == c || c == a) continue;
                tris.Add(a); tris.Add(b); tris.Add(c);
            }
            // No sliver guard: it keeps a remesh's input faithful, and here refused 92,435 collapses
            // on the seats' long thin strips, which stopped at 19,919 of 544 triangles.
            var options = new DecimateOptions { targetTriangles = Math.Max(4, 2 * targetQuads), sliverGuard = 0f };
            PreDecimate.Decimate(welded.ToArray(), tris.ToArray(), options, out var decimateStats, out outPositions, out var outTriangles);
            lastCollapseStats = decimateStats;
            outFaces = new List<int[]>(outTriangles.Length / 3);
            for (var t = 0; t + 2 < outTriangles.Length; t += 3)
                outFaces.Add(new[] { outTriangles[t], outTriangles[t + 1], outTriangles[t + 2] });
            Extraction.TrianglePairing.Run(outFaces, outPositions);
        }

        /// <summary>
        /// The surface built as its profile turned about <paramref name="fit"/>'s axis (see
        /// <see cref="Revolution"/>), with the UVs and materials a remesh's result carries; null when
        /// there is no fit or no profile.
        /// </summary>
        public static RemeshResult Revolved(Vector3[] positions, int[] triangles, Revolution.Fit fit, RemeshSettings settings,
            RemeshProfile profile, Vector2[] sourceUvs, int[] triangleMaterials, Action<string, float> onProgress = null,
            float cell = 0f)
        {
            if (fit == null) return null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (!Revolution.Build(positions, triangles, fit, settings.targetQuadCount, out var outPositions, out var outFaces, cell))
                return null;
            if (settings.modelType == ModelType.Auto) settings.modelType = ModelType.HardSurface;
            var result = FromQuadReduction(positions, triangles, outPositions, outFaces, null, settings, profile,
                sourceUvs, triangleMaterials, watch, onProgress);
            result.revolution = fit;
            return result;
        }

        /// <summary>
        /// A part of several pieces, some turned (<see cref="Revolution.FindPieces"/>): each turned
        /// piece built turned at its share of the quads by area, the others reduced by their quad
        /// strips (or, when they will not reduce, collapsed and paired) at theirs; null when no piece
        /// could be built turned.
        /// </summary>
        public static RemeshResult RevolvedPieces(Vector3[] positions, int[] triangles, Revolution.Pieces pieces,
            RemeshSettings settings, RemeshProfile profile, Vector2[] sourceUvs, int[] triangleMaterials,
            Action<string, float> onProgress = null, float cell = 0f)
        {
            if (pieces == null) return null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            double total = 0;
            foreach (var a in pieces.areas) total += a;
            if (!(total > 0)) return null;
            var outPositions = new List<Vector3>();
            var outFaces = new List<int[]>();
            void Add(Vector3[] p, List<int[]> f)
            {
                var offset = outPositions.Count;
                outPositions.AddRange(p);
                foreach (var face in f)
                {
                    var moved = new int[face.Length];
                    for (var i = 0; i < face.Length; i++) moved[i] = face[i] + offset;
                    outFaces.Add(moved);
                }
            }
            var rest = new List<int>();
            double restArea = 0;
            Revolution.Fit first = null;
            // Largest first, while the turned pieces together stay within the part's budget: built
            // each at 8 sides or more, a coil spring's turns took the Ford's spring, 18 quads, to 705
            // faces; what does not fit goes with the rest.
            var order = new int[pieces.triangles.Count];
            for (var k = 0; k < order.Length; k++) order[k] = k;
            Array.Sort(order, (x, y) => pieces.areas[y].CompareTo(pieces.areas[x]));
            var budget = Math.Max(Revolution.piecesSlack * settings.targetQuadCount, 32);
            var used = 0;
            foreach (var k in order)
            {
                var target = Math.Max(8, (int)Math.Round(settings.targetQuadCount * pieces.areas[k] / total));
                var fit = pieces.fits[k];
                if (Revolution.Accept(fit, target))
                {
                    Revolution.Compact(positions, pieces.triangles[k], out var piecePositions, out var pieceTriangles);
                    if (Revolution.Build(piecePositions, pieceTriangles, fit, target, out var turnedPositions, out var turnedFaces, cell)
                        && used + turnedFaces.Count <= budget)
                    {
                        used += turnedFaces.Count;
                        Add(turnedPositions, turnedFaces);
                        first ??= fit;
                        continue;
                    }
                }
                rest.AddRange(pieces.triangles[k]);
                restArea += pieces.areas[k];
            }
            if (first == null) return null;
            if (rest.Count > 0)
            {
                var restTarget = Math.Max(8, (int)Math.Round(settings.targetQuadCount * restArea / total));
                Revolution.Compact(positions, rest.ToArray(), out var restPositions, out var restTriangles);
                if (QuadReduction.Reduce(restPositions, restTriangles, restTarget, out var reducedPositions, out var reducedFaces, out _))
                    Add(reducedPositions, reducedFaces);
                else
                {
                    CollapseToQuads(restPositions, restTriangles, restTarget, out var collapsedPositions, out var collapsedFaces);
                    Add(collapsedPositions, collapsedFaces);
                }
            }
            if (settings.modelType == ModelType.Auto) settings.modelType = ModelType.HardSurface;
            var result = FromQuadReduction(positions, triangles, outPositions.ToArray(), outFaces, null, settings, profile,
                sourceUvs, triangleMaterials, watch, onProgress);
            result.revolution = first;
            return result;
        }

        /// <summary>
        /// Solve without trying the strip reduction: for a part whose strips were already tried with
        /// the rest of its model (QuadReduction.ReduceParts).
        /// </summary>
        public static RemeshResult SolveWithoutStrips(Vector3[] positions, int[] triangles, RemeshSettings settings,
            RemeshProfile profile, Action<string, float> onProgress, Vector2[] sourceUvs, bool autoSelectProfile,
            int[] triangleMaterials)
        {
            var was = t_NoStrips;
            t_NoStrips = true;
            try { return Solve(positions, triangles, settings, profile, onProgress, sourceUvs, autoSelectProfile, triangleMaterials); }
            finally { t_NoStrips = was; }
        }

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
                System.Threading.Tasks.Parallel.For(0, count, BackgroundScheduler.Options, i =>
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
            => SolveCovered(positions, triangles, settings, profile, onProgress, sourceUvs, autoSelectProfile, null);

        /// <summary>
        /// As above, with each triangle's material (submesh), which
        /// <see cref="RemeshSettings.materialBorders"/> keeps.
        /// </summary>
        public static RemeshResult Solve(Vector3[] positions, int[] triangles,
            RemeshSettings settings, RemeshProfile profile, Action<string, float> onProgress,
            Vector2[] sourceUvs, bool autoSelectProfile, int[] triangleMaterials)
            => SolveCovered(positions, triangles, settings, profile, onProgress, sourceUvs, autoSelectProfile,
                triangleMaterials);

        /// <summary>
        /// When the quad layout leaves more of the surface uncovered than
        /// <see cref="layoutMinCoverage"/> allows, solve again without it and keep whichever covers
        /// more. Off: the layout's result whatever it covers, as before.
        /// </summary>
        /// <remarks>
        /// The layout meshes patch by patch, and a patch it cannot make rectangular gets no quads;
        /// the hole filler closes small gaps, not those. On the Porsche's roof at 140 quads (any
        /// profile) the output covered the rear frame and little else: input-to-output 31 cells on
        /// average. Without the layout the same part comes out whole (0.42 cells).
        /// </remarks>
        public static bool layoutCoverageFallback = true;

        /// <summary>Share of the input covered (see Coverage) under which the layout's result is not trusted.</summary>
        public static float layoutMinCoverage = 0.98f;

        static RemeshResult SolveCovered(Vector3[] positions, int[] triangles, RemeshSettings settings,
            RemeshProfile profile, Action<string, float> onProgress, Vector2[] sourceUvs, bool autoSelectProfile,
            int[] triangleMaterials)
        {
            if (Revolution.enabled && settings.symmetry == SymmetryAxis.None && settings.densityPaint == null)
            {
                onProgress?.Invoke("Looking for a turned surface", 0f);
                var fit = Revolution.Find(positions, triangles);
                var turned = Revolution.Accept(fit, settings.targetQuadCount)
                    ? Revolved(positions, triangles, fit, settings, profile, sourceUvs, triangleMaterials, onProgress)
                    : null;
                SpatialGrid turnedGrid = null;
                if (turned != null) return NearerDiagonals(turned, positions, triangles, ref turnedGrid);
                if (Revolution.pieces)
                {
                    turned = RevolvedPieces(positions, triangles, Revolution.FindPieces(positions, triangles), settings, profile,
                        sourceUvs, triangleMaterials, onProgress);
                    if (turned != null) return NearerDiagonals(turned, positions, triangles, ref turnedGrid);
                }
            }
            if (settings.modelType == ModelType.Auto)
            {
                onProgress?.Invoke("Recognising the model", 0f);
                settings.modelType = ShapeRecognition.Classify(positions, triangles, out settings.shapeSignals);
            }
            var tryOthers = detailQuadsTry && !detailQuads && settings.modelType != ModelType.Character
                && settings.symmetry == SymmetryAxis.None && settings.densityPaint == null
                && triangles.Length / 3 >= detailQuadsTryTriangles
                && triangles.Length / 3 >= detailQuadsTryTrianglesPerQuad * Math.Max(1, settings.targetQuadCount);
            // With no other quads to try, the lattice is the output: its crease pieces joined from the start.
            s_JoinPieces = !tryOthers;
            RemeshResult lattice;
            try
            {
                lattice = SolveLattice(positions, triangles, settings, profile, onProgress, sourceUvs, autoSelectProfile,
                    triangleMaterials);
            }
            finally { s_JoinPieces = false; }
            SpatialGrid sourceGrid = null;
            NearerDiagonals(lattice, positions, triangles, ref sourceGrid);
            if (!tryOthers || lattice?.extraction == null)
                return lattice;
            // A dense mesh of flat walls and boxed-in detail (the white house: 1.8 million
            // triangles, window recesses three cells wide) comes out of the lattice soft-walled and
            // its recesses as stars of folded faces; the error-driven quads keep its planes, edges
            // and recesses. Kept when they lie clearly closer to the source.
            onProgress?.Invoke("Trying error-driven quads", 0f);
            RemeshResult detail;
            // At the faces the lattice came out at (Adaptive Quad Count raises them): like for like,
            // and no more than the lattice would have made. They make about 1.27 faces a quad asked.
            var detailSettings = settings;
            detailSettings.targetQuadCount = Math.Max(settings.targetQuadCount, Mathf.RoundToInt(lattice.extraction.faces.Count / 1.27f));
            detailQuads = true;
            try
            {
                detail = SolveInternal(positions, triangles, detailSettings, profile, onProgress, sourceUvs,
                    autoSelectProfile, SymmetryAxis.None, 0f, triangleMaterials);
            }
            finally { detailQuads = false; }
            NearerDiagonals(detail, positions, triangles, ref sourceGrid);
            var latticeOff = DistanceOff(positions, triangles, lattice.extraction, out var latticeTurn, out var latticeFar);
            var best = lattice;
            var bestOff = latticeOff;
            var bestTurn = latticeTurn;
            var bestFar = latticeFar;
            s_RegularJudge = hardSurfaceCleanLayout && lattice.modelType == ModelType.HardSurface;
            var latticeRegular = RegularShare(lattice.extraction);
            var tried = $"lattice {latticeOff:G3} (p99 {latticeFar:G3}), {latticeTurn:0.0} deg, {latticeRegular:P0} regular at {lattice.extraction.faces.Count} faces";
            var kept = "";
            if (detail?.extraction != null && detail.extraction.faces.Count > 0)
            {
                var detailOff = DistanceOff(positions, triangles, detail.extraction, out var detailTurn, out var detailFar);
                var detailRegular = RegularShare(detail.extraction);
                tried += $"; error-driven {detailOff:G3} (p99 {detailFar:G3}), {detailTurn:0.0} deg, {detailRegular:P0} regular at {detail.extraction.faces.Count}";
                // Clearly closer, and their faces turning from the source's no more.
                // Or, nearly as close, where the lattice is torn (TornLattice).
                if (detailTurn <= detailQuadsTurn * latticeTurn
                    && (detailOff < (s_RegularJudge ? cleanLayoutDetailWins : detailQuadsWins) * latticeOff
                        && RegularEnough(detailRegular, latticeRegular)
                        || detailOff < tornLatticeWins * latticeOff && TornLattice(detailFar, latticeFar)))
                {
                    best = detail;
                    bestOff = detailOff;
                    bestTurn = detailTurn;
                    bestFar = detailFar;
                    kept = ": error-driven kept";
                }
            }
            // A building (mostly flat walls): its walls as grids, its windows and doors as reliefs.
            // Kept when no further from the source than the best so far (in general and at its
            // worst: a window it could not build is no mushier), turning no more, at no more
            // faces, and all but quads.
            // Not for a result whose creases came from its panels: built from the same panels, the
            // building's fill leaves everything not flat -- wheels, pipes, hinges -- as loose
            // triangles, which measured nearer the car (0.00114 against 0.00153) and looked
            // crumpled and lumpy beside the grid's round wheels and straight plate edges.
            // Hard Surface's clean layout: the building's quads come out a third regular (30-35 % on
            // four cars, the armoured car and the house) and can never pass the regularity test.
            // Where small details go to the normal map (hardSurfaceSourceCreases) they need the
            // regularity too: built from the source's own planes they keep every handle and badge at
            // any count, and that is what measures them nearer. Car2 at 5,000: 0.00168 against the
            // lattice's 0.00247, 34 % regular against 97 %, handles in the quads, panels crumpled.
            if (planarQuadsTry && PlanarQuads.enabled && !lattice.panelCreases && !s_RegularJudge)
            {
                onProgress?.Invoke("Trying a building's quads", 0f);
                RemeshResult planar;
                planarQuads = true;
                try
                {
                    planar = SolveInternal(positions, triangles, detailSettings, profile, onProgress, sourceUvs,
                        autoSelectProfile, SymmetryAxis.None, 0f, triangleMaterials);
                }
                finally { planarQuads = false; }
                // Turned already, against the walls made flat (PlanarQuads.nearerDiagonal): against
                // the source's own bumps they turn worse (ccnb 8.4 deg to 8.7, the house 10.3 to 10.5).
                if (!PlanarQuads.nearerDiagonal) NearerDiagonals(planar, positions, triangles, ref sourceGrid);
                if (planar?.extraction != null && planar.extraction.faces.Count > 0)
                {
                    var planarOff = DistanceOff(positions, triangles, planar.extraction, out var planarTurn, out var planarFar);
                    var faces = planar.extraction.faces.Count;
                    var triangleShare = planar.extraction.triangleCount / (float)faces;
                    var planarRegular = RegularShare(planar.extraction);
                    tried += $"; building {planarOff:G3} (p99 {planarFar:G3} against {bestFar:G3}), {planarTurn:0.0} deg, {planarRegular:P0} regular at {faces} ({triangleShare:P1} triangles)";
                    if (planarOff <= bestOff && planarFar <= bestFar && planarTurn <= bestTurn && triangleShare <= planarQuadsTriangles
                        && faces <= planarQuadsFaces * best.extraction.faces.Count
                        && (RegularEnough(planarRegular, latticeRegular) || TornLattice(planarFar, latticeFar))
                        && (lattice.sourceCreaseStats == null || !(candidateRegularShare > 0f)
                            || planarRegular >= candidateRegularShare * latticeRegular))
                    {
                        best = planar;
                        kept = ": building kept";
                    }
                }
            }
            // The lattice kept, from creases read edge by edge: laid again along them joined
            // (SourceCreases.Keep's joinPieces). Not before the others are judged: they are made at
            // the faces the lattice came out at, and the white house's error-driven quads, at 3 % fewer
            // faces off the joined lattice, crumpled its windows.
            if (best == lattice && joinLatticePieces && lattice.sourceCreaseStats != null && lattice.sourceCreaseStats.loose)
            {
                onProgress?.Invoke("Laying the grid along joined creases", 0f);
                RemeshResult joined;
                s_JoinPieces = true;
                try
                {
                    joined = SolveLattice(positions, triangles, settings, profile, onProgress, sourceUvs, autoSelectProfile,
                        triangleMaterials);
                }
                finally { s_JoinPieces = false; }
                NearerDiagonals(joined, positions, triangles, ref sourceGrid);
                if (joined?.extraction != null && joined.extraction.faces.Count > 0)
                {
                    var joinedOff = DistanceOff(positions, triangles, joined.extraction, out var joinedTurn, out var joinedFar);
                    tried += $"; joined lattice {joinedOff:G3} (p99 {joinedFar:G3}), {joinedTurn:0.0} deg at {joined.extraction.faces.Count}";
                    if (joinedOff <= joinedLatticeSlack * latticeOff && joinedTurn <= joinedLatticeSlack * latticeTurn)
                    {
                        best = joined;
                        kept = ": joined lattice kept";
                    }
                }
            }
            if (QuadReduction.logStats) Debug.Log($"[Native Remesher] {tried}{kept}");
            lattice.detailTry = tried;
            best.detailTry = tried + kept;
            return best;
        }

        /// <summary>
        /// A candidate that measures closer than the lattice is kept only with at least this share of
        /// the lattice's regular points (valence 4, inside): closeness alone kept a car's building
        /// quads -- no rows at all, 0.000879 against the lattice's 0.00177 -- because a mesh with
        /// no flow can still lie on the surface. 0: closeness alone, as before.
        /// </summary>
        public static float candidateRegularShare = 0.5f;

        /// <summary>
        /// The error-driven quads are also kept at under tornLatticeWins of the lattice's distance
        /// when the lattice is torn somewhere -- its worst hundredth off the source further than this
        /// many times theirs: the house's windows folded through the wall. Its lattice came out at
        /// 0.66 of the distance with its worst hundredth 1.79 times theirs, just past detailQuadsWins,
        /// and was kept with its windows torn open. On four cars and the armoured car the lattice's
        /// worst hundredth is 0.18-1.66 times theirs with them 0.65-4.96 as far. 0: off.
        /// </summary>
        public static float tornLatticeFar = 1.7f;

        /// <summary>See tornLatticeFar.</summary>
        public static float tornLatticeWins = 0.75f;

        static bool TornLattice(float candidateFar, float latticeFar) =>
            tornLatticeFar > 0f && latticeFar > tornLatticeFar * candidateFar;

        static bool RegularEnough(float candidate, float lattice) =>
            !(candidateRegularShare > 0f) || !s_RegularJudge || candidate >= candidateRegularShare * lattice;

        // Set per solve: the regularity test is Hard Surface's, with its clean layout.
        [ThreadStatic] static bool s_RegularJudge;

        // Set per lattice solve: its source creases' pieces joined (SourceCreases.Keep's joinPieces).
        [ThreadStatic] static bool s_JoinPieces;

        /// <summary>
        /// A lattice kept over the other quads, its creases read off the solve mesh edge by edge (no
        /// panels), is laid again along them joined -- gaps bridged, handles' rims dropped -- and
        /// that kept when no further from the source nor turning more than joinedLatticeSlack times
        /// the first. Car2 at 5,000: its window frames in 888 dashes, the grid ran through them, and
        /// the handles folded into the doors. Off: the first lattice.
        /// </summary>
        public static bool joinLatticePieces = true;

        /// <summary>See joinLatticePieces.</summary>
        public static float joinedLatticeSlack = 1.05f;

        // The share of interior points with four faces.
        static float RegularShare(QuadExtractionResult ex)
        {
            if (ex?.faces == null || ex.positions == null) return 0f;
            var n = ex.positions.Length;
            var valence = new int[n];
            var edgeUse = new Dictionary<long, int>();
            foreach (var f in ex.faces)
                for (var k = 0; k < f.Length; k++)
                {
                    valence[f[k]]++;
                    var key = Extraction.EdgeKey.PackUndirected(f[k], f[(k + 1) % f.Length]);
                    edgeUse[key] = edgeUse.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            var open = new bool[n];
            foreach (var kv in edgeUse)
                if (kv.Value != 2) { open[Extraction.EdgeKey.First(kv.Key)] = true; open[Extraction.EdgeKey.Second(kv.Key)] = true; }
            int inside = 0, regular = 0;
            for (var v = 0; v < n; v++)
            {
                if (open[v] || valence[v] == 0) continue;
                inside++;
                if (valence[v] == 4) regular++;
            }
            return inside > 0 ? regular / (float)inside : 0f;
        }

        /// <summary>
        /// Every output's quads that are not flat turned by a corner where the other diagonal lies
        /// nearer the source: Unity's quad topology (and the triangulation measured here) cuts a
        /// quad along its first and third corners, and the cut decides where the bent quad's
        /// surface lies. False: as each path built them.
        /// </summary>
        public static bool nearerDiagonals = true;

        /// <summary>Quads of the last output turned by <see cref="nearerDiagonals"/>.</summary>
        public static int lastTurnedQuads;

        /// <summary>
        /// Every output's patches of faces wound against the source as a whole reversed, whatever
        /// their border: Unity draws a face from its front only, and the head's ear cup at 2,000
        /// came out wound in (455 of its triangles facing the head), drawn as a hole. The face
        /// orienter leaves a patch whose border agrees with its neighbours, as a fold the winding
        /// cannot express; seen from outside it is still a hole. False: as each path wound them.
        /// </summary>
        public static bool facesToSource = true;

        /// <summary>Faces of the last output reversed by <see cref="facesToSource"/>.</summary>
        public static int lastReversedFaces;

        // Turns the result's quads (see nearerDiagonals) and their corners' UVs, tangents and
        // normals with them, after reversing the patches facing against the source (see
        // facesToSource); new lists and arrays, as a result's extraction may share them.
        static RemeshResult NearerDiagonals(RemeshResult result, Vector3[] positions, int[] triangles, ref SpatialGrid source)
        {
            var ex = result?.extraction;
            if (ex?.positions == null || ex.faces.Count == 0) return result;
            if (facesToSource)
            {
                source ??= new SpatialGrid(positions, triangles);
                FacesToSource(ex, source);
            }
            if (!nearerDiagonals) return result;
            source ??= new SpatialGrid(positions, triangles);
            var faces = new List<int[]>(ex.faces);
            var turned = new bool[faces.Count];
            lastTurnedQuads = PlanarQuads.NearerDiagonals(ex.positions, faces, source, turned);
            if (lastTurnedQuads == 0) return result;
            T[][] Turned<T>(T[][] corners)
            {
                if (corners == null || corners.Length != faces.Count) return corners;
                var copy = (T[][])corners.Clone();
                for (var f = 0; f < copy.Length; f++)
                    if (turned[f] && copy[f] != null && copy[f].Length == 4)
                        copy[f] = new[] { copy[f][1], copy[f][2], copy[f][3], copy[f][0] };
                return copy;
            }
            ex.faces = faces;
            ex.faceUvs = Turned(ex.faceUvs);
            ex.faceTangents = Turned(ex.faceTangents);
            ex.faceNormals = Turned(ex.faceNormals);
            return result;
        }

        // Patches of faces (joined at shared edges) each facing against the source's nearest
        // triangle, reversed when their area-weighted cosine to it is below -0.5 over at least
        // four faces: a sliver's single face may meet the far side by chance. Faces over a source
        // of two opposite skins close together do not vote.
        static void FacesToSource(QuadExtractionResult ex, SpatialGrid source)
        {
            lastReversedFaces = 0;
            var faces = ex.faces;
            var n = faces.Count;
            var cosine = new float[n];
            var area = new float[n];
            System.Threading.Tasks.Parallel.For(0, n, BackgroundScheduler.Options, f =>
            {
                var face = faces[f];
                if (face == null || face.Length < 3) return;
                var normal = QuadExtractor.FaceNormal(ex.positions, face);
                area[f] = normal.magnitude;
                if (!(area[f] > 0f)) return;
                var middle = Vector3.zero;
                foreach (var v in face) middle += ex.positions[v];
                middle /= face.Length;
                var unit = normal / area[f];
                source.NearestPoint(middle, out _, out var there);
                // A thin part's source of both sides (a panel's two skins): which one is nearest is
                // chance, so its face does not vote. Probed a quarter of the face's size off it
                // either way; the two sides' normals turn apart there.
                var reach = 0.25f * Mathf.Sqrt(area[f]);
                source.NearestPoint(middle + reach * unit, out _, out var front);
                source.NearestPoint(middle - reach * unit, out _, out var back);
                if (Vector3.Dot(front, back) < 0f) return;
                cosine[f] = Vector3.Dot(unit, there);
            });
            var edgeFaces = new Dictionary<long, List<int>>(n * 2, Extraction.EdgeKey.Comparer);
            for (var f = 0; f < n; f++)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;
                for (var i = 0; i < face.Length; i++)
                {
                    var key = Extraction.EdgeKey.PackUndirected(face[i], face[(i + 1) % face.Length]);
                    if (!edgeFaces.TryGetValue(key, out var list)) edgeFaces[key] = list = new List<int>(2);
                    list.Add(f);
                }
            }
            var seen = new bool[n];
            var members = new List<int>();
            var queue = new Queue<int>();
            var reversed = new bool[n];
            for (var seed = 0; seed < n; seed++)
            {
                if (seen[seed] || !(cosine[seed] < 0f)) continue;
                members.Clear();
                seen[seed] = true;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    members.Add(f);
                    var face = faces[f];
                    for (var i = 0; i < face.Length; i++)
                        foreach (var g in edgeFaces[Extraction.EdgeKey.PackUndirected(face[i], face[(i + 1) % face.Length])])
                            if (!seen[g] && cosine[g] < 0f) { seen[g] = true; queue.Enqueue(g); }
                }
                double vote = 0, total = 0;
                foreach (var f in members) { vote += cosine[f] * area[f]; total += area[f]; }
                if (members.Count < 4 || !(total > 0) || vote / total > -0.5) continue;
                foreach (var f in members) reversed[f] = true;
                lastReversedFaces += members.Count;
            }
            if (lastReversedFaces == 0) return;
            // New lists and arrays, as the extraction's may be shared; corners reversed with their face.
            T[][] Reversed<T>(T[][] corners)
            {
                if (corners == null || corners.Length != n) return corners;
                var copy = (T[][])corners.Clone();
                for (var f = 0; f < n; f++)
                    if (reversed[f] && copy[f] != null) { copy[f] = (T[])copy[f].Clone(); Array.Reverse(copy[f]); }
                return copy;
            }
            var turned = new List<int[]>(n);
            for (var f = 0; f < n; f++)
            {
                var face = faces[f];
                if (reversed[f] && face != null) { face = (int[])face.Clone(); Array.Reverse(face); }
                turned.Add(face);
            }
            ex.faces = turned;
            ex.faceUvs = Reversed(ex.faceUvs);
            ex.faceTangents = Reversed(ex.faceTangents);
            ex.faceNormals = Reversed(ex.faceNormals);
            if (ex.shadingNormals != null && ex.shadingNormals.Length == ex.positions.Length)
                ex.shadingNormals = Extraction.NormalRecalculator.Compute(turned, ex.positions, ex.normals);
        }

        /// <summary>
        /// Dense meshes that are mostly flat walls are also built as a building's quads
        /// (PlanarQuads) when error-driven quads are tried, and kept when they measure no worse.
        /// </summary>
        public static bool planarQuadsTry = true;

        /// <summary>The building's quads are kept with at most this share of triangles...</summary>
        public static float planarQuadsTriangles = 0.01f;

        /// <summary>...and at most this many times the faces of what they replace.</summary>
        public static float planarQuadsFaces = 1.1f;

        /// <summary>
        /// Dense meshes that are not characters are also built as error-driven quads (DetailQuads),
        /// kept when they lie clearly closer to the source (detailQuadsWins); false: the lattice only.
        /// </summary>
        public static bool detailQuadsTry = true;

        /// <summary>The error-driven quads are tried on a mesh of at least this many triangles...</summary>
        public static int detailQuadsTryTriangles = 200000;

        /// <summary>...and this many per quad asked for.</summary>
        public static int detailQuadsTryTrianglesPerQuad = 20;

        /// <summary>
        /// Kept when their distance from the source (90th percentile, both ways) is under this share
        /// of the lattice's at the same faces. White house at 5,000: 7.4e-6 against 1.9e-5.
        /// </summary>
        public static float detailQuadsWins = 0.6f;

        /// <summary>
        /// detailQuadsWins under Hard Surface's clean layout: its lattice is no longer merged nearer
        /// the source first, so the error-driven quads win at a smaller margin (the house 0.65-0.74
        /// over runs, its lattice's windows torn; the cars 0.53-0.78 of the lattice's distance; the
        /// armoured car 0.95, its lattice kept).
        /// </summary>
        public static float cleanLayoutDetailWins = 0.8f;

        /// <summary>...and the angle their faces turn from the source's (mean) at most this many times the lattice's.</summary>
        public static float detailQuadsTurn = 1.05f;

        /// <summary>
        /// The 90th percentile of the distances between source and output, sampled both ways, in the
        /// source's units; the 99th (`far`); and the mean angle between the output's faces and the
        /// source's nearest.
        /// </summary>
        static float DistanceOff(Vector3[] positions, int[] triangles, QuadExtractionResult extraction, out float turn,
            out float far, int samples = 6000)
        {
            turn = 180f;
            far = float.PositiveInfinity;
            var tris = LodChain.Triangulate(extraction.faces, out _);
            if (tris.Length < 3) return float.PositiveInfinity;
            var output = new SpatialGrid(extraction.positions, tris);
            var source = new SpatialGrid(positions, triangles);
            var there = SpatialGrid.SampleSurface(positions, triangles, samples, 5);
            var back = SpatialGrid.SampleSurface(extraction.positions, tris, samples, 6);
            var d = new float[there.Count + back.Count];
            System.Threading.Tasks.Parallel.For(0, there.Count, i => d[i] = output.NearestDistance(there[i], out _));
            System.Threading.Tasks.Parallel.For(0, back.Count, i => d[there.Count + i] = source.NearestDistance(back[i], out _));
            // The turn: at points of the output, its triangle's normal against the source's nearest one's.
            var angles = new float[back.Count];
            System.Threading.Tasks.Parallel.For(0, back.Count, i =>
            {
                output.NearestDistance(back[i], out var mine);
                source.NearestDistance(back[i], out var theirs);
                if (mine < 0 || theirs < 0) return;
                output.TriangleCorners(mine, out var a, out var b, out var c);
                source.TriangleCorners(theirs, out var p, out var q, out var r);
                var n0 = Vector3.Cross(extraction.positions[b] - extraction.positions[a], extraction.positions[c] - extraction.positions[a]);
                var n1 = Vector3.Cross(positions[q] - positions[p], positions[r] - positions[p]);
                // Unoriented: a source wound the other way is no error.
                var cos = Mathf.Abs(Vector3.Dot(n0, n1)) / Mathf.Max(1e-30f, n0.magnitude * n1.magnitude);
                angles[i] = Mathf.Acos(Mathf.Clamp01(cos)) * Mathf.Rad2Deg;
            });
            double sum = 0;
            foreach (var a in angles) sum += a;
            turn = angles.Length > 0 ? (float)(sum / angles.Length) : 180f;
            Array.Sort(d);
            if (d.Length > 0) far = d[Mathf.Clamp((int)(0.99f * d.Length), 0, d.Length - 1)];
            return d.Length > 0 ? d[Mathf.Clamp((int)(0.9f * d.Length), 0, d.Length - 1)] : float.PositiveInfinity;
        }

        static RemeshResult SolveLattice(Vector3[] positions, int[] triangles, RemeshSettings settings,
            RemeshProfile profile, Action<string, float> onProgress, Vector2[] sourceUvs, bool autoSelectProfile,
            int[] triangleMaterials)
        {
            var result = SolveInternal(positions, triangles, settings, profile, onProgress, sourceUvs,
                autoSelectProfile, SymmetryAxis.None, 0f, triangleMaterials);
            if (!layoutCoverageFallback || !Extraction.IntegerGridMap.parametrization
                || !Extraction.IntegerGridMap.parametrizationLayout || result?.extraction == null) return result;
            result.coverage = Coverage(positions, triangles, result.extraction);
            if (result.coverage >= layoutMinCoverage) return result;

            onProgress?.Invoke("Quad layout left holes: solving without it", 0f);
            RemeshResult second;
            Extraction.IntegerGridMap.parametrizationLayout = false;
            try
            {
                second = SolveInternal(positions, triangles, settings, profile, onProgress, sourceUvs,
                    autoSelectProfile, SymmetryAxis.None, 0f, triangleMaterials);
            }
            finally { Extraction.IntegerGridMap.parametrizationLayout = true; }
            if (second?.extraction == null) return result;
            second.coverage = Coverage(positions, triangles, second.extraction);
            second.layoutCoverage = result.coverage;
            return second.coverage > result.coverage ? second : result;
        }

        /// <summary>
        /// Share of the input's surface within <see cref="coverageReachCells"/> cells of the output,
        /// from evenly spread samples.
        /// </summary>
        static float Coverage(Vector3[] positions, int[] triangles, QuadExtractionResult extraction)
        {
            if (extraction?.faces == null || extraction.faces.Count == 0) return 0f;
            var tris = new List<int>(extraction.faces.Count * 6);
            foreach (var f in extraction.faces)
                for (var k = 1; k + 1 < f.Length; k++) { tris.Add(f[0]); tris.Add(f[k]); tris.Add(f[k + 1]); }
            var cell = (float)Math.Sqrt(FaceArea(extraction) / extraction.faces.Count);
            if (!(cell > 0f)) return 0f;
            var grid = new SpatialGrid(extraction.positions, tris.ToArray());
            var samples = SpatialGrid.SampleSurface(positions, triangles, coverageSamples, 1);
            if (samples.Count == 0) return 1f;
            var reach = coverageReachCells * cell;
            var near = 0;
            foreach (var p in samples)
                if (grid.NearestDistance(p, out _) <= reach) near++;
            return near / (float)samples.Count;
        }

        /// <summary>How far, in cells, an input point may be from the output and still count as covered.</summary>
        public static float coverageReachCells = 2f;
        public static int coverageSamples = 20000;

        static double FaceArea(QuadExtractionResult extraction)
        {
            double area = 0;
            var p = extraction.positions;
            foreach (var f in extraction.faces)
                for (var k = 1; k + 1 < f.Length; k++)
                    area += 0.5 * Vector3.Cross(p[f[k]] - p[f[0]], p[f[k + 1]] - p[f[0]]).magnitude;
            return area;
        }

        // lockAxis/lockPlane: a symmetry half's cut. The decimated border vertices beside the plane
        // are put back on it, so the cut's grid line lies in the plane and the mirror meets it.
        static RemeshResult SolveInternal(Vector3[] positions, int[] triangles,
            RemeshSettings settings, RemeshProfile profile, Action<string, float> onProgress,
            Vector2[] sourceUvs, bool autoSelectProfile, SymmetryAxis lockAxis, float lockPlane,
            int[] triangleMaterials)
        {
            if (positions == null)
                throw new ArgumentNullException(nameof(positions));

            if (settings.modelType == ModelType.Auto)
            {
                onProgress?.Invoke("Recognising the model", 0f);
                settings.modelType = ShapeRecognition.Classify(positions, triangles, out settings.shapeSignals);
            }
            ApplyModelType(settings.modelType, ref profile, ref autoSelectProfile);
            profile = profile.Validated();
            settings = profile.ApplyTo(settings).Validated();
            if (quadInputReduction && StripsFor(settings.modelType) && lockAxis == SymmetryAxis.None
                && !t_SolvingWithoutRibbons && !t_NoStrips)
            {
                var reduced = SolveByStrips(positions, triangles, settings, profile, onProgress, sourceUvs,
                    triangleMaterials, out var reductionStats);
                if (reduced != null) return reduced;
            }
            if (ribbonParts && StrapsFor(settings.modelType) && !t_SolvingWithoutRibbons && lockAxis == SymmetryAxis.None)
                return SolveWithRibbons(positions, triangles, settings, profile, onProgress, sourceUvs,
                    autoSelectProfile, triangleMaterials);
            if (settings.symmetry != SymmetryAxis.None)
                return SolveSymmetric(positions, triangles, settings, profile, onProgress, sourceUvs, autoSelectProfile,
                    triangleMaterials);

            var result = new RemeshResult
            {
                profile = profile, modelType = settings.modelType, shapeSignals = settings.shapeSignals
            };
            if (settings.densityPaint != null && !settings.densityPaint.isEmpty)
                result.densityPaint = settings.densityPaint;
            if (settings.materialBorders && triangleMaterials != null && triangleMaterials.Length == triangles.Length / 3)
            {
                var labels = new MaterialLabels(positions, triangles, triangleMaterials);
                if (labels.labelCount > 1) result.materials = labels;
            }
            var watch = new System.Diagnostics.Stopwatch();

            if (detailQuads || planarQuads)
            {
                onProgress?.Invoke(planarQuads ? "Building quads" : "Detail quads", 0f);
                watch.Restart();
                Vector3[] detailPositions;
                List<int[]> detailFaces;
                if (planarQuads)
                {
                    if (!PlanarQuads.Build(positions, triangles, Mathf.Max(1, settings.targetQuadCount),
                            out detailPositions, out detailFaces, out result.planarQuadsStats))
                        return null;
                }
                else
                    DetailQuads.Build(positions, triangles, Mathf.Max(1, settings.targetQuadCount),
                        out detailPositions, out detailFaces, out result.detailQuadsStats);
                var extraction = new QuadExtractionResult { positions = detailPositions, faces = detailFaces };
                extraction.normals = new Vector3[detailPositions.Length];
                foreach (var face in detailFaces)
                {
                    if (face.Length == 4) extraction.quadCount++;
                    else if (face.Length == 3) extraction.triangleCount++;
                    else extraction.ngonCount++;
                    var fn = Vector3.zero;
                    for (var k = 1; k + 1 < face.Length; k++)
                        fn += Vector3.Cross(detailPositions[face[k]] - detailPositions[face[0]],
                            detailPositions[face[k + 1]] - detailPositions[face[0]]);
                    foreach (var v in face) extraction.normals[v] += fn;
                }
                for (var v = 0; v < extraction.normals.Length; v++)
                    extraction.normals[v] = MeshMath.SafeNormalize(extraction.normals[v], Vector3.up);
                result.extraction = extraction;
                result.extractionMs = watch.ElapsedMilliseconds;
                result.originalPositions = positions;
                result.originalTriangles = triangles;
                FairHardSurface(result, onProgress);
                TransferUvs(result, positions, triangles, sourceUvs, watch, onProgress);
                LabelFaces(result);
                onProgress?.Invoke("Done", 1f);
                return result;
            }

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
                // Painted areas ask for up to range^2 the quads there: the solve mesh gets the
                // triangles for them, and they are kept where the paint is (weights below).
                float[] paintDensity = null;
                if (result.densityPaint != null)
                {
                    var factors = result.densityPaint.SpacingFactors(positions);
                    if (factors != null)
                    {
                        double area = 0, painted = 0;
                        for (var t = 0; t + 2 < triangles.Length; t += 3)
                        {
                            var a = Vector3.Cross(positions[triangles[t + 1]] - positions[triangles[t]],
                                positions[triangles[t + 2]] - positions[triangles[t]]).magnitude;
                            var f = (factors[triangles[t]] + factors[triangles[t + 1]] + factors[triangles[t + 2]]) / 3f;
                            area += a;
                            painted += a / (f * f);
                        }
                        if (area > 0) budget = Mathf.RoundToInt(budget * Mathf.Clamp((float)(painted / area), 1f, 8f));
                        paintDensity = factors;
                    }
                }
                if (maxSolveTriangles > 0 && budget > maxSolveTriangles)
                {
                    budget = Mathf.Max(k_MinimumBudget, maxSolveTriangles);
                    result.solveTrianglesCapped = true;
                }

                if (triangles.Length / 3 > budget)
                {
                    onProgress?.Invoke("Decimating input", 0f);
                    watch.Restart();
                    var decimateOptions = DecimateFor(budget, result.materials);
                    if (paintDensity != null)
                    {
                        var paint = result.densityPaint;
                        decimateOptions.vertexWeights = topology =>
                        {
                            var f = paint.SpacingFactors(topology.positions);
                            if (f == null) return null;
                            var w = new float[f.Length];
                            for (var i = 0; i < w.Length; i++) w[i] = Mathf.Max(1f, 1f / (f[i] * f[i]));
                            return w;
                        };
                    }
                    // Cells of the requested count, on the input's area.
                    double inputArea = 0;
                    for (var t = 0; t + 2 < triangles.Length; t += 3)
                        inputArea += Vector3.Cross(positions[triangles[t + 1]] - positions[triangles[t]],
                            positions[triangles[t + 2]] - positions[triangles[t]]).magnitude * 0.5;
                    var inputCell = (float)Math.Sqrt(inputArea / Math.Max(1, settings.targetQuadCount));
                    if (decimateMaxErrorCells > 0f) decimateOptions.maxDistance = decimateMaxErrorCells * inputCell;
                    if (decimateKeepsCreases && Extraction.IntegerGridMap.longCreaseLoops)
                    {
                        var cell = inputCell;
                        decimateOptions.creaseAngle = Extraction.IntegerGridMap.coherentCreaseAngle;
                        decimateOptions.creaseMinLength = decimateCreaseMinCells * cell;
                        decimateOptions.creaseStraightness = decimateCreaseStraightness;
                    }
                    PreDecimate.Decimate(positions, triangles,
                        decimateOptions, out var decimateStats,
                        out solvePositions, out solveTriangles,
                        p => onProgress?.Invoke("Decimating input", p));
                    result.decimateMs = watch.ElapsedMilliseconds;
                    result.decimateStats = decimateStats;
                }
            }
            if (debugSolveMeshPath != null && System.IO.File.Exists(debugSolveMeshPath))
                LoadObj(debugSolveMeshPath, out solvePositions, out solveTriangles);
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
            var dumpRun = debugStagesPath != null ? s_DebugStagesRuns++ : 0;
            DumpStage(dumpRun, "decimated", refineInputPositions, Triples(refineInputTriangles));
            RunCore(solvePositions, solveTriangles, settings, result, watch, onProgress,
                autoSelectProfile);
            DumpStage(dumpRun, "extracted", result.extraction?.positions, result.extraction?.faces);

            if (QuadReduction.logStats && result.features != null)
                Debug.Log($"[Native Remesher] creases: {result.features.featureEdgeCount} edges in {result.features.polylineCount} chains, " +
                          $"{result.denseCreasesDropped} dense dropped, from panels {result.panelCreases}; " +
                          $"snap [{result.extraction?.snapStats}]");
            var fitPasses = result.panelCreases ? Mathf.Max(featureFitIterations, panelFitPasses) : featureFitIterations;
            if (fitPasses > 0 && result.extraction?.positions != null)
            {
                onProgress?.Invoke("Fitting features", 0f);
                result.featureFitStats = FeatureFit.Fit(result.extraction.positions, result.extraction.faces,
                    positions, triangles, fitPasses);
                DumpStage(dumpRun, "fitted", result.extraction.positions, result.extraction.faces);
            }

            // Stars (many faces folded round one vertex, where detail a cell or two across was
            // collapsed) filled flat across their ring. Not with per-face materials, which the
            // faces' order carries.
            if (StarFill.enabled && result.extraction?.positions != null && result.extraction.faceMaterials == null)
            {
                result.starFillStats = StarFill.Apply(result.extraction.faces, result.extraction.positions);
                if (result.starFillStats.filled > 0)
                {
                    var ex = result.extraction;
                    ex.quadCount = 0;
                    foreach (var f in ex.faces) if (f != null && f.Length == 4) ex.quadCount++;
                    if (ex.shadingNormals != null && ex.shadingNormals.Length == ex.positions.Length)
                        ex.shadingNormals = Extraction.NormalRecalculator.Compute(ex.faces, ex.positions, ex.normals);
                }
                if (QuadReduction.logStats) Debug.Log($"[Native Remesher] {result.starFillStats}");
            }

            if (surfaceCleanIterations > 0 && result.extraction?.positions != null)
            {
                onProgress?.Invoke("Cleaning surface", 0f);
                var ex = result.extraction;
                // Against the surface the lattice was solved on: real sharp tips are kept. See SurfaceClean.
                var cleanSurface = surfaceCleanUsesSurface && result.decimatedPositions != null && result.decimatedTriangles != null
                    ? new SpatialGrid(result.decimatedPositions, result.decimatedTriangles)
                    : null;
                result.surfaceCleanStats = SurfaceClean.Apply(ex.positions, ex.faces, surfaceCleanIterations, cleanSurface,
                    ex.onLayoutLine, bentQuads: (result.panelCreases || result.sourceCreaseStats != null) && panelCleanBentQuads);
                if (QuadReduction.logStats) Debug.Log($"[Native Remesher] {result.surfaceCleanStats}");
                DumpStage(dumpRun, "cleaned", ex.positions, ex.faces);
                if (ex.shadingNormals != null && ex.shadingNormals.Length == ex.positions.Length)
                    ex.shadingNormals = Extraction.NormalRecalculator.Compute(ex.faces, ex.positions, ex.normals);
            }

            // The lattice's even quads merged where the source is flat (or down to the count asked).
            // With the clean layout nothing is merged, but its points are still smoothed and its
            // folded quads taken out (hardSurfaceCleanTidy).
            var tidyOnly = hardSurfaceCleanLayout && hardSurfaceCleanTidy && !planarQuads
                && result.modelType == ModelType.HardSurface && result.extraction?.positions != null
                && result.extraction.faceMaterials == null && result.extraction.uvs == null
                && settings.targetQuadCount > 0;
            if (tidyOnly || hardSurfaceSimplify && !hardSurfaceCleanLayout && !detailQuads && !planarQuads && result.panelCreases
                && result.modelType == ModelType.HardSurface && result.extraction?.positions != null
                && result.extraction.faceMaterials == null && result.extraction.uvs == null
                && settings.targetQuadCount > 0
                && result.extraction.faces.Count > hardSurfaceSimplifyMinRatio * settings.targetQuadCount)
            {
                onProgress?.Invoke("Simplifying quads", 0f);
                var ex = result.extraction;
                // Against the mesh the lattice was solved on: the 1.5-million-triangle car's own
                // surface took 469 s of nearest-point queries.
                var sp = result.decimatedPositions ?? positions;
                var st = result.decimatedTriangles ?? triangles;
                var surface = new SpatialGrid(sp, st);
                var locked = ex.onLayoutLine;
                if (hardSurfacePlateBlocks && !tidyOnly)
                {
                    // The plates' quads merged nine at a time first, in rows, and held so.
                    double sourceArea = 0;
                    for (var t = 0; t + 2 < st.Length; t += 3)
                        sourceArea += Vector3.Cross(sp[st[t + 1]] - sp[st[t]], sp[st[t + 2]] - sp[st[t]]).magnitude * 0.5;
                    var cell = (float)Math.Sqrt(sourceArea / settings.targetQuadCount);
                    var blocked = PlateBlocks.Apply(ex.positions, ex.faces, ex.onLayoutLine, cell, surface,
                        hardSurfaceSimplifyMaxOffCells, out var origin, out var pinned, out result.plateBlocksStats);
                    T[] Carried<T>(T[] values)
                    {
                        if (values == null || values.Length != ex.positions.Length) return null;
                        var outValues = new T[blocked.Length];
                        for (var v = 0; v < blocked.Length; v++) if (origin[v] >= 0) outValues[v] = values[origin[v]];
                        return outValues;
                    }
                    var normals = Carried(ex.normals);
                    if (normals != null)
                    {
                        // A new point's normal: its faces'.
                        foreach (var f in ex.faces)
                        {
                            var s = Vector3.zero;
                            for (var k = 0; k < f.Length; k++) s += Vector3.Cross(blocked[f[k]], blocked[f[(k + 1) % f.Length]]);
                            foreach (var v in f) if (origin[v] < 0) normals[v] += s;
                        }
                        for (var v = 0; v < blocked.Length; v++) if (origin[v] < 0) normals[v] = normals[v].normalized;
                    }
                    ex.normals = normals;
                    ex.onLayoutLine = Carried(ex.onLayoutLine);
                    ex.positions = blocked;
                    locked = new bool[blocked.Length];
                    for (var v = 0; v < blocked.Length; v++) locked[v] = pinned[v] || ex.onLayoutLine != null && ex.onLayoutLine[v];
                    if (QuadReduction.logStats) Debug.Log($"[Native Remesher] {result.plateBlocksStats}");
                }
                var simplified = QuadSimplify.Simplify(ex.positions, ex.faces, sp, st, surface,
                    settings.targetQuadCount, hardSurfaceSimplifyToCount ? 0f : hardSurfaceSimplifyMaxOffCells,
                    locked, out var remap, out result.simplifyStats, tidyOnly);
                T[] Remapped<T>(T[] values)
                {
                    if (values == null || values.Length != remap.Length) return null;
                    var outValues = new T[simplified.Length];
                    for (var v = 0; v < remap.Length; v++) if (remap[v] >= 0) outValues[remap[v]] = values[v];
                    return outValues;
                }
                ex.normals = Remapped(ex.normals);
                ex.onLayoutLine = Remapped(ex.onLayoutLine);
                ex.positions = simplified;
                ex.quadCount = 0;
                ex.triangleCount = 0;
                ex.ngonCount = 0;
                foreach (var f in ex.faces)
                    if (f.Length == 4) ex.quadCount++;
                    else if (f.Length == 3) ex.triangleCount++;
                    else ex.ngonCount++;
                if (ex.shadingNormals != null)
                    ex.shadingNormals = ex.normals != null
                        ? Extraction.NormalRecalculator.Compute(ex.faces, ex.positions, ex.normals)
                        : null;
                if (QuadReduction.logStats) Debug.Log($"[Native Remesher] {result.simplifyStats}");
            }

            FairHardSurface(result, onProgress);
            DumpStage(dumpRun, "final", result.extraction?.positions, result.extraction?.faces);
            TransferUvs(result, positions, triangles, sourceUvs, watch, onProgress);
            LabelFaces(result);
            return result;
        }

        /// <summary>
        /// Hard Surface's output faired as the last step (SurfaceFair): its faces' normals averaged
        /// with their neighbours' where they nearly agree, and the points moved to fit, so the
        /// source's ripples under a quad's size (the cars' panels, the solve mesh's slivers) leave
        /// the doors flat instead of crumpled; the normal map carries them. Off: the points stay
        /// where the output put them, as before.
        /// </summary>
        public static bool hardSurfaceFair = true;

        static void FairHardSurface(RemeshResult result, Action<string, float> onProgress)
        {
            var ex = result.extraction;
            if (!hardSurfaceFair || result.modelType != ModelType.HardSurface || ex?.positions == null || ex.faces == null) return;
            onProgress?.Invoke("Fairing the surface", 0f);
            result.fairStats = SurfaceFair.Apply(ex.positions, ex.faces);
            if (QuadReduction.logStats) Debug.Log($"[Native Remesher] {result.fairStats}");
            if (ex.normals != null && ex.normals.Length == ex.positions.Length)
            {
                var normals = new Vector3[ex.positions.Length];
                foreach (var f in ex.faces)
                {
                    if (f == null || f.Length < 3) continue;
                    var fn = Vector3.zero;
                    for (var k = 1; k + 1 < f.Length; k++)
                        fn += Vector3.Cross(ex.positions[f[k]] - ex.positions[f[0]], ex.positions[f[k + 1]] - ex.positions[f[0]]);
                    foreach (var v in f) normals[v] += fn;
                }
                for (var v = 0; v < normals.Length; v++) normals[v] = MeshMath.SafeNormalize(normals[v], ex.normals[v]);
                ex.normals = normals;
            }
            if (ex.shadingNormals != null && ex.shadingNormals.Length == ex.positions.Length)
                ex.shadingNormals = Extraction.NormalRecalculator.Compute(ex.faces, ex.positions, ex.normals);
        }

        /// <summary>Diagnostics: a folder each solve's stages are written to as OBJ (run_stage.obj); null: nowhere.</summary>
        public static string debugStagesPath;
        static int s_DebugStagesRuns;

        /// <summary>Diagnostics: an OBJ solved on in place of the decimated mesh (same frame); null: none.</summary>
        public static string debugSolveMeshPath;

        static void LoadObj(string path, out Vector3[] positions, out int[] triangles)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var p = new List<Vector3>();
            var t = new List<int>();
            foreach (var line in System.IO.File.ReadLines(path))
            {
                var s = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (s.Length >= 4 && s[0] == "v")
                    p.Add(new Vector3(float.Parse(s[1], inv), float.Parse(s[2], inv), float.Parse(s[3], inv)));
                else if (s.Length >= 4 && s[0] == "f")
                    for (var k = 2; k + 1 < s.Length; k++)
                    {
                        t.Add(int.Parse(s[1].Split('/')[0], inv) - 1);
                        t.Add(int.Parse(s[k].Split('/')[0], inv) - 1);
                        t.Add(int.Parse(s[k + 1].Split('/')[0], inv) - 1);
                    }
            }
            positions = p.ToArray();
            triangles = t.ToArray();
        }

        static IEnumerable<int[]> Triples(int[] triangles)
        {
            for (var t = 0; t + 2 < triangles.Length; t += 3) yield return new[] { triangles[t], triangles[t + 1], triangles[t + 2] };
        }

        internal static void DumpStage(int run, string stage, Vector3[] positions, IEnumerable<int[]> faces)
        {
            if (debugStagesPath == null || positions == null || faces == null) return;
            System.IO.Directory.CreateDirectory(debugStagesPath);
            var sb = new System.Text.StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var p in positions) sb.Append("v ").Append(p.x.ToString("R", inv)).Append(' ').Append(p.y.ToString("R", inv)).Append(' ').Append(p.z.ToString("R", inv)).Append('\n');
            foreach (var f in faces)
            {
                if (f == null || f.Length < 3) continue;
                sb.Append('f');
                foreach (var v in f) sb.Append(' ').Append(v + 1);
                sb.Append('\n');
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(debugStagesPath, $"{run}_{stage}.obj"), sb.ToString());
        }

        [ThreadStatic] static (Vector3[] positions, int[] triangles, int target) t_StripsRefused;
        [ThreadStatic] static QuadReduction.Stats t_StripsRefusedStats;

        // The input reduced by quad strips (QuadReduction), or null when it is not quads or will not reduce.
        static RemeshResult SolveByStrips(Vector3[] positions, int[] triangles, RemeshSettings settings,
            RemeshProfile profile, Action<string, float> onProgress, Vector2[] sourceUvs, int[] triangleMaterials,
            out QuadReduction.Stats stats)
        {
            // The same input and count refused already (each output tried -- lattice, error-driven
            // quads -- comes back through here): 3.5 s a time on a 1.5-million-triangle car.
            if (ReferenceEquals(t_StripsRefused.positions, positions) && ReferenceEquals(t_StripsRefused.triangles, triangles)
                && (t_StripsRefused.target == settings.targetQuadCount || t_StripsRefusedStats?.reason == "not quads"))
            {
                stats = t_StripsRefusedStats;
                return null;
            }
            onProgress?.Invoke("Reducing the quads", 0f);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var reducedOk = QuadReduction.Reduce(positions, triangles, Mathf.Max(1, settings.targetQuadCount),
                out var outPositions, out var outFaces, out stats);
            if (QuadReduction.logStats) Debug.Log($"[Native Remesher] {(reducedOk ? "strips" : "remesher")}: {stats}");
            if (!reducedOk)
            {
                t_StripsRefused = (positions, triangles, settings.targetQuadCount);
                t_StripsRefusedStats = stats;
                return null;
            }
            return FromQuadReduction(positions, triangles, outPositions, outFaces, stats, settings, profile,
                sourceUvs, triangleMaterials, watch, onProgress);
        }

        /// <summary>
        /// A result from a strip reduction's output (QuadReduction), with the UVs and materials the
        /// remesher's results carry. The input and the output are in the same space.
        /// </summary>
        public static RemeshResult FromQuadReduction(Vector3[] positions, int[] triangles, Vector3[] outPositions,
            List<int[]> outFaces, QuadReduction.Stats stats, RemeshSettings settings, RemeshProfile profile,
            Vector2[] sourceUvs, int[] triangleMaterials, System.Diagnostics.Stopwatch watch = null,
            Action<string, float> onProgress = null)
        {
            watch ??= System.Diagnostics.Stopwatch.StartNew();
            var result = new RemeshResult
            {
                profile = profile, modelType = settings.modelType, shapeSignals = settings.shapeSignals,
                quadReductionStats = stats
            };
            if (settings.materialBorders && triangleMaterials != null && triangleMaterials.Length == triangles.Length / 3)
            {
                var labels = new MaterialLabels(positions, triangles, triangleMaterials);
                if (labels.labelCount > 1) result.materials = labels;
            }
            var extraction = new QuadExtractionResult { positions = outPositions, faces = outFaces };
            extraction.normals = new Vector3[outPositions.Length];
            foreach (var face in outFaces)
            {
                if (face.Length == 4) extraction.quadCount++;
                else if (face.Length == 3) extraction.triangleCount++;
                else extraction.ngonCount++;
                var fn = Vector3.zero;
                for (var k = 1; k + 1 < face.Length; k++)
                    fn += Vector3.Cross(outPositions[face[k]] - outPositions[face[0]],
                        outPositions[face[k + 1]] - outPositions[face[0]]);
                foreach (var v in face) extraction.normals[v] += fn;
            }
            for (var v = 0; v < extraction.normals.Length; v++)
                extraction.normals[v] = MeshMath.SafeNormalize(extraction.normals[v], Vector3.up);
            extraction.shadingNormals = Extraction.NormalRecalculator.Compute(outFaces, outPositions, extraction.normals);
            result.extraction = extraction;
            result.extractionMs = watch.ElapsedMilliseconds;
            result.originalPositions = positions;
            result.originalTriangles = triangles;
            TransferUvs(result, positions, triangles, sourceUvs, watch, onProgress);
            LabelFaces(result);
            onProgress?.Invoke("Done", 1f);
            return result;
        }

        // Each output face's material, from the input surface under it.
        static void LabelFaces(RemeshResult result)
        {
            var ex = result.extraction;
            if (result.materials == null || ex == null || ex.positions == null) return;
            ex.faceMaterials = result.materials.ForFaces(ex.positions, ex.faces);
            ex.materialSlots = result.materials.slotCount;
        }

        /// <summary>
        /// One half (the positive side of the plane) solved with half the quads, mirrored and
        /// welded. See <see cref="Symmetry"/>.
        /// </summary>
        /// <summary>
        /// With Loops Along Creases, pre-decimation keeps the long straight creases as edge chains
        /// (DecimateOptions.creaseAngle): the ones at least this many cells of the requested count
        /// long. Off (false): decimation as before, and a scan's wall foot comes out as slivers.
        /// </summary>
        public static bool decimateKeepsCreases = true;

        /// <summary>
        /// The pre-decimation stops short of its triangle budget rather than move a vertex further
        /// than this, in cells of the requested count, off the input (DecimateOptions.maxDistance).
        /// 0: no limit (the old behaviour).
        /// </summary>
        public static float decimateMaxErrorCells = 0.1f;

        /// <summary>
        /// Most triangles the solver's mesh is decimated to, whatever the target. 0: no cap.
        /// </summary>
        /// <remarks>
        /// The parametrisation's factorisation grows faster than its mesh, and the budget of
        /// ten triangles a quad left a large target undecimated: ccnb at 100,000 quads solved on
        /// all 990,625 scan triangles, 418 s parametrising, 539 s in all, the factor gigabytes in
        /// size and the machine paging. Measured on ccnb (Photogrammetry, Adaptive Quad Count):
        /// 25,000 asked, 68,377 out on 250k triangles, 79 s parametrising; 50,000 asked, 140,454
        /// out on 500k, 170 s -- that one at 3.6 triangles a quad and the fewest irregular
        /// vertices of the three (2.33 %). The detail is not lost: output vertices go back onto
        /// the input surface.
        /// </remarks>
        public static int maxSolveTriangles = 300000;

        /// <summary>
        /// Under that cap, Adaptive Quad Count grows the target no further than the capped mesh
        /// carries at this many triangles a quad (ccnb at 50,000: 3.6 was fine).
        /// </summary>
        public static float minSolveTrianglesPerQuad = 3f;

        public static float decimateCreaseMinCells = 4f;

        /// <summary>
        /// Straightness for those creases (end to end over walked length). Lower than the solver's:
        /// on the full-resolution scan a crease follows the triangle edges in a zigzag.
        /// </summary>
        public static float decimateCreaseStraightness = 0.85f;

        static DecimateOptions DecimateFor(int budget, MaterialLabels labels)
        {
            var options = DecimateOptions.ForTriangleCount(budget);
            options.labels = labels;
            return options;
        }

        static RemeshResult SolveSymmetric(Vector3[] positions, int[] triangles,
            RemeshSettings settings, RemeshProfile profile, Action<string, float> onProgress,
            Vector2[] sourceUvs, bool autoSelectProfile, int[] triangleMaterials)
        {
            var axis = settings.symmetry;
            var halfSettings = settings;
            halfSettings.symmetry = SymmetryAxis.None;
            var plane = Symmetry.PlaneOf(positions, axis);
            Symmetry.Clip(positions, triangles, axis, plane, out var halfPositions, out var halfTriangles,
                out var parentTriangle);
            if (halfTriangles.Length == 0)
                return SolveInternal(positions, triangles, halfSettings, profile, onProgress, sourceUvs, autoSelectProfile,
                    SymmetryAxis.None, 0f, triangleMaterials);
            int[] halfMaterials = null;
            if (triangleMaterials != null && triangleMaterials.Length == triangles.Length / 3)
            {
                halfMaterials = new int[parentTriangle.Length];
                for (var t = 0; t < parentTriangle.Length; t++) halfMaterials[t] = triangleMaterials[parentTriangle[t]];
            }
            halfSettings.targetQuadCount = Mathf.Max(4, (settings.targetQuadCount + 1) / 2);
            // Paint on the half that is cut away counts on the half that is solved.
            if (symmetricPaint && halfSettings.densityPaint != null && !halfSettings.densityPaint.isEmpty)
                halfSettings.densityPaint = halfSettings.densityPaint.Mirrored((int)axis - 1, plane);
            var result = SolveInternal(halfPositions, halfTriangles, halfSettings, profile, onProgress, null,
                autoSelectProfile, axis, plane, halfMaterials);
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
            if (result.materials != null && triangleMaterials != null)
            {
                // Labelled from the whole input: the mirror image lies on the other side's materials.
                result.materials = new MaterialLabels(positions, triangles, triangleMaterials);
                LabelFaces(result);
            }
            return result;
        }

        /// <summary>
        /// A new UV atlas for the result, and the source's textures baked into it (see
        /// <see cref="TextureBake"/>): replaces the transferred UVs, which cut across every seam of
        /// the source's atlas. <paramref name="triangleTexture"/> picks each source triangle's
        /// texture from <paramref name="textures"/> (null: the first for all).
        /// </summary>
        public static void BakeTexture(RemeshResult result, Vector3[] positions, int[] triangles,
            Vector2[] sourceUvs, int[] triangleTexture, TextureBake.Image[] textures, int resolution,
            Action<string, float> onProgress = null, TextureBake.Layer[] layers = null)
        {
            var extraction = result?.extraction;
            if (extraction == null || extraction.positions == null || sourceUvs == null
                || sourceUvs.Length != positions.Length || textures == null || textures.Length == 0)
                return;
            onProgress?.Invoke("Baking texture", 0f);
            var stats = new TextureBake.Stats();
            var uvs = TextureBake.Unwrap(extraction.positions, extraction.faces, resolution, stats, extraction.faceTexelScale,
                result.panelCreases ? panelHardEdgeAngle : -1f);
            result.bakedTexture = TextureBake.Bake(positions, triangles, sourceUvs, triangleTexture, textures,
                extraction.positions, extraction.faces, uvs, resolution, stats,
                p => onProgress?.Invoke("Baking texture", p), layers);
            result.bakedLayers = layers != null && layers.Length > 0 ? layers : null;
            result.bakedTextureSize = resolution;
            result.bakeStats = stats;
            extraction.faceUvs = uvs;
            extraction.uvs = null;
        }

        /// <summary>
        /// Bakes the input's surface detail into a tangent-space normal map over the output's own
        /// atlas (unwrapping it first unless BakeTexture already did), and gives the faces the
        /// tangents it was baked against, which BuildOutputMesh writes into the mesh. Needs no
        /// source UVs or textures.
        /// </summary>
        public static void BakeNormalMap(RemeshResult result, Vector3[] positions, int[] triangles, int resolution,
            Action<string, float> onProgress = null, Vector2[] sourceUvs = null, int[] sourceTriangleNormalMap = null,
            TextureBake.Image[] sourceNormalMaps = null, bool[] faceHardEdges = null)
        {
            var extraction = result?.extraction;
            if (extraction == null || extraction.positions == null || positions == null || triangles == null) return;
            onProgress?.Invoke("Baking normal map", 0f);
            var stats = result.bakeStats;
            if (stats == null || extraction.faceUvs == null || result.bakedTexture == null || result.bakedTextureSize != resolution)
            {
                stats = new TextureBake.Stats();
                extraction.faceUvs = TextureBake.Unwrap(extraction.positions, extraction.faces, resolution, stats, extraction.faceTexelScale,
                    result.panelCreases ? panelHardEdgeAngle : -1f);
                extraction.uvs = null;
                result.bakeStats = stats;
            }
            var normals = extraction.shadingNormals != null && extraction.shadingNormals.Length == extraction.positions.Length
                ? extraction.shadingNormals
                : extraction.normals;
            if (normals == null || normals.Length != extraction.positions.Length)
                normals = Extraction.NormalRecalculator.Compute(extraction.faces, extraction.positions, null);
            // Per face whether it may be hard: as given (parts of several profiles), else by the result's profile.
            extraction.faceNormals = TextureBake.hardEdgeAngle > 0f && (faceHardEdges != null || TextureBake.HardEdgesFor(result.profile))
                ? TextureBake.CornerNormals(extraction.positions, extraction.faces, extraction.faceUvs, normals,
                    result.panelCreases ? panelHardEdgeAngle : TextureBake.hardEdgeAngle, faceHardEdges)
                : null;
            extraction.faceTangents = TextureBake.Tangents(extraction.positions, extraction.faces, extraction.faceUvs, normals,
                extraction.faceNormals);
            var cavity = TextureBake.bakeCavity ? new Color32[resolution * resolution] : null;
            result.bakedNormalMap = TextureBake.BakeNormals(positions, triangles, extraction.positions, extraction.faces,
                extraction.faceUvs, normals, extraction.faceTangents, resolution, stats,
                p => onProgress?.Invoke("Baking normal map", p), extraction.faceNormals,
                sourceUvs, sourceTriangleNormalMap, sourceNormalMaps, cavity);
            result.bakedNormalMapSize = resolution;
            result.bakedOcclusion = cavity;
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

            // The lines the grid must follow: the open border always, and with Loops Along Creases
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
            if (QuadReduction.logStats) Debug.Log($"[Native Remesher] parametrization: {result.parametrizationStats}");

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
            coarseOptions.features = result.decimatedFeatures ??= ProfileFeatures(result, topology,
                result.effectiveTargetQuadCount);
            if (Extraction.IntegerGridMap.relaxAlongLayoutLines && featureEdges != null && featureEdges.Count > 0)
            {
                var lines = new Vector3[2 * featureEdges.Count];
                for (var i = 0; i < featureEdges.Count; i++)
                {
                    lines[2 * i] = topology.positions[featureEdges[i].a];
                    lines[2 * i + 1] = topology.positions[featureEdges[i].b];
                }
                coarseOptions.layoutLines = lines;
            }
        }

        /// <summary>
        /// On a scan (the Photogrammetry profile), Loops Along Creases keeps only creases at least
        /// this many cells long, end to end through the junctions they run straight through.
        /// </summary>
        /// <remarks>
        /// A scan's clutter -- cars, air-conditioning units, the junkyard -- has creases a few metres
        /// long, and each one laid as a patch border puts singularities at its ends and crumples
        /// the quads round it. The building edges that matter run tens of metres. Measured on ccnb
        /// at 5000, with FeatureGraph.joinThroughJunctions so the roof edges are measured whole:
        /// 3 cells / 0.6 straight -> 8 / 0.85: irregular 5.16 -> 4.96 %, p05 49.0 -> 53.5,
        /// Hausdorff 0.0134 -> 0.0135. 0: the general coherentCreaseMinCells / parametrizationCreaseMinCells.
        /// </remarks>
        public static float scanCreaseMinCells = 8f;

        /// <summary>On a scan, the straightness a kept crease needs (end to end over its length). 0: the general one.</summary>
        public static float scanCreaseStraightness = 0.85f;

        /// <summary>
        /// Loops Along Creases measures each edge's bend between the faces within this many cells
        /// either side of it (FeatureGraph.DetectAtScale), so a decimated corner rounded into a
        /// chamfer still reads as the corner it is. 0: edge by edge (FeatureGraph.Detect).
        /// </summary>
        public static float creaseScaleCells = 0.25f;

        /// <summary>
        /// Apply <see cref="creaseScaleCells"/> on open meshes only. Measured over ccnb, the head and
        /// the body at three counts each: it is what puts a scan's wall foot on a grid line, while
        /// the closed sculpts -- auto-selected as Photogrammetry too -- came out a little better
        /// without it (mean p05 over head and body 60.7 against 59.7).
        /// </summary>
        public static bool creaseScaleOpenOnly = true;

        static FeatureGraph DetectCreases(RemeshResult result, RemeshTopology topology, float cell) =>
            creaseScaleCells > 0f && (!creaseScaleOpenOnly || topology.boundaryEdgeCount > 0)
                ? FeatureGraph.DetectAtScale(topology, CreaseAngle(result), creaseScaleCells * cell)
                : FeatureGraph.Detect(topology, CreaseAngle(result));

        /// <summary>
        /// OFF, measured worse (see hardSurfaceSparseCreases for what replaced it). Hard Surface
        /// finds its creases the way Loops Along Creases does on the other types -- whole
        /// chains, kept when they run long and straight -- instead of by the profile's plain 20
        /// degree test, which marks every edge of a tire's tread and of a decimated grille as a
        /// crease and pins the grid to all of them. Off: the profile's own test (the old behaviour).
        /// </summary>
        public static bool hardSurfaceCoherentCreases = false;

        /// <summary>The bend a Hard Surface crease needs under <see cref="hardSurfaceCoherentCreases"/>.</summary>
        public static float hardSurfaceCreaseAngle = 30f;

        /// <summary>
        /// Hard Surface keeps its profile's creases (every edge bent past 20 degrees) only where
        /// they stand apart (FeatureGraph.KeepSparseCreases): a tire's tread and a grille are left
        /// to the normal map instead of pinning the grid to every groove. Off: all of them.
        /// </summary>
        public static bool hardSurfaceSparseCreases = true;

        /// <summary>The radius, in cells, the crease density is measured over.</summary>
        public static float sparseCreaseRadiusCells = 1f;

        /// <summary>Most crease length, over the radius, within it that a kept crease edge may have.</summary>
        public static float sparseCreaseMaxLength = 4.5f;

        /// <summary>
        /// Hard Surface takes its creases from the flat panels it finds (PlanarRegions) where the
        /// bend test is measuring the surface's grain instead of its edges -- where the density
        /// filter above drops at least <see cref="panelCreasesDroppedShare"/> of what it marked:
        /// an edge is then a crease where two panels of at least <see cref="panelCreaseMinCells"/>
        /// square cells meet at <see cref="panelCreaseDegrees"/> or more, with the narrow fillets
        /// between panels first closed onto the line where they meet. Off: the bend test alone.
        /// </summary>
        /// <remarks>
        /// The armoured car (one 1.5-million-triangle shell, every plate edge rounded over several
        /// triangles) at 8,000: the 20 degree test marked 163,033 edges, the filter dropped
        /// 157,665, and the 5,368 left lay in 1,475 chains of three or four edges -- nothing for the
        /// grid to run along, and every plate edge came out soft. From its panels: 3,439 edges in
        /// 259 chains, loops along the plate edges, round wheels.
        /// </remarks>
        public static bool hardSurfacePanelCreases = true;

        /// <summary>The share of bend-test creases the density filter must drop for the panels to be asked instead.</summary>
        public static float panelCreasesDroppedShare = 0.5f;

        /// <summary>
        /// ...on an input of at least this many triangles, dense enough for its edges to be rounded
        /// over several of them. The Ford's parts, a few thousand triangles each, lose half their
        /// bend-test creases on a grille or a bumper too, and there the panels' creases were no
        /// better: a bumper cleaner, the one under the grille opened along its lower edge.
        /// </summary>
        public static int panelCreasesMinTriangles = 200000;

        /// <summary>
        /// ...and when the panels give at least this many crease edges for each the filter left.
        /// A building's walls meet its windows in steps and recesses rather than panel to panel:
        /// the white house as Hard Surface got 36 edges from its panels against 3,070 left by the
        /// filter, and built on those its windows came out as mush. The car: 3,439 against 5,368.
        /// </summary>
        public static float panelCreasesMinShare = 0.25f;

        /// <summary>Panels meeting at this angle or more make a crease, degrees.</summary>
        public static float panelCreaseDegrees = 20f;

        /// <summary>Only panels of at least this many square cells make creases (a tyre's flat spots do not).</summary>
        public static float panelCreaseMinCells = 8f;

        /// <summary>Fillets narrower than this many cells are closed before the panels' meeting lines are read.</summary>
        public static float panelFilletCells = 0.5f;

        /// <summary>FeatureFit passes over a result whose creases came from its panels.</summary>
        public static int panelFitPasses = 2;

        /// <summary>
        /// The angle normals split from (TextureBake.hardEdgeAngle, chartCreaseAngle) on a result
        /// whose creases came from its panels. At 40, plate edges of 20 to 40 degrees -- most of
        /// the car's -- shaded round over a whole quad.
        /// </summary>
        public static float panelHardEdgeAngle = 25f;

        /// <summary>
        /// A panel's outline is a crease too where it ends at a turn of <see cref="panelRimDegrees"/>
        /// or more into something that is not a panel of its own: a frame, a step, a hatch's wall.
        /// Off: creases only where two panels meet, as before.
        /// </summary>
        /// <remarks>
        /// The armoured car's windshields, hatches and rear plates end at frames and steps narrower
        /// than a panel, so no two panels met there and the grid crossed them in a zigzag. With
        /// outlines, at 5,000: 846 -> 5,036 crease edges, error 0.00195 -> 0.00186 at 19,318 ->
        /// 19,741 faces, the rear plate's seam and lower edge straight; at 8,000: 27,820 -> 26,537
        /// faces, error 0.00128 -> 0.00135, the rear's centre crease a line. The white house, Ford
        /// and Porsche do not take panel creases and are pixel-identical.
        /// </remarks>
        public static bool panelRimCreases = true;

        /// <summary>
        /// A Hard Surface result whose creases came from its panels has its quads merged where that
        /// costs no shape (QuadSimplify, no new face further than hardSurfaceSimplifyMaxOffCells
        /// off the source): the plates' quads large, the parts' kept. Off: the lattice's quads as
        /// they are, as before.
        /// </summary>
        /// <remarks>
        /// The armoured car at 5,000: 19,741 -> 15,063 faces, 78 % of the points valence 4, sampled
        /// both ways against the source 0.66 -> 0.75 / 0.42 -> 0.46 thousandths of its diagonal
        /// (mean), the renders alike. At 0.06 cells: 10,564 faces, 0.91 / 0.55, the wheels faceted.
        /// </remarks>
        public static bool hardSurfaceSimplify = true;

        /// <summary>
        /// Hard Surface lays its quads once and keeps their rows: the lattice is not merged back
        /// down afterwards (hardSurfaceSimplify, hardSurfacePlateBlocks), and of the other outputs
        /// tried, one is kept only with at least candidateRegularShare of the lattice's regular
        /// points (the building's quads, a third regular, are not tried). On four cars at 5,000
        /// the merging broke the lattice's rows into stars, and one car kept the building's quads
        /// (35 % regular) for lying 0.000879 off the source against the lattice's 0.00177.
        /// False: merged, and the closest output kept, as before.
        /// Off by default: faster (cars 50-60 s against 80-95 s), but measured against the source's
        /// normals it leaves more of the surface turned over -- car1 0.50 % of the area against the
        /// merging's 0.42 %, car2 0.78 against 0.53, the house 1.58 against 0.87, the armoured car
        /// 2.45 against 2.20 -- and the renders show the holes and wobbles.
        /// </summary>
        public static bool hardSurfaceCleanLayout = false;

        /// <summary>
        /// With hardSurfaceCleanLayout: the quads' points still smoothed along the source and its
        /// folded quads taken out, as the merging did -- without, the layout keeps its wobbles.
        /// </summary>
        public static bool hardSurfaceCleanTidy = true;

        /// <summary>
        /// With hardSurfaceCleanLayout, the count is also held to what was asked even where
        /// Curves Add Quads would raise it. Off: the count as the window's setting reads it.
        /// </summary>
        public static bool hardSurfaceCleanExactCount = false;

        /// <summary>Furthest off the source, in cells at the asked count's spacing, a merged quad may lie.</summary>
        public static float hardSurfaceSimplifyMaxOffCells = 0.03f;

        /// <summary>
        /// ...or, on, merged down to the count asked whatever it costs (a low-poly model): the car at
        /// 5,000 comes out at 4,788 faces, 1.37 / 0.99 against the source where the lattice asked
        /// for 2,000 comes out at 6,749 and 1.62 / 0.81 -- the gun, mirrors and plate edges kept,
        /// the wheels faceted.
        /// </summary>
        public static bool hardSurfaceSimplifyToCount = false;

        /// <summary>...when the lattice came out at more than this many times the count asked.</summary>
        public static float hardSurfaceSimplifyMinRatio = 1.2f;

        /// <summary>
        /// Before those merges, the lattice's quads on flat plates are merged nine at a time in
        /// rectangles (PlateBlocks), with a ring of 3-to-1 steps round each, and held: the plates in
        /// rows of big quads where the merges alone leave fans. Off: as before.
        /// </summary>
        /// <remarks>
        /// The armoured car at 5,000 (.NET harness, same lattice): 8 rectangles of 42 blocks, 15,049
        /// -> 15,121 faces, sampled against the source 0.78 / 0.50 -> 0.74 / 0.49 thousandths of its
        /// diagonal; the skirts, turret and front plates in rows. The side panels take few blocks
        /// (their lattice has singular points every few quads).
        /// </remarks>
        public static bool hardSurfacePlateBlocks = true;

        /// <summary>
        /// On a result whose creases came from its panels, or were judged on the source
        /// (hardSurfaceSourceCreases), SurfaceClean also smooths quads folded across both diagonals
        /// (squeezed between creases under a cell apart: car2's window sills, a step a third of a
        /// cell high, folded a row of them). Off: as before.
        /// </summary>
        public static bool panelCleanBentQuads = true;

        /// <summary>The turn off a panel's plane its outline needs to be a crease, degrees.</summary>
        public static float panelRimDegrees = 35f;

        /// <summary>Outline pieces shorter than this many cells (round a bolt, a hinge) are left out.</summary>
        public static float panelRimMinCells = 4f;

        // The boundaries of `topology`, and an edge wherever two of its flat panels meet (see hardSurfacePanelCreases).
        static FeatureGraph PanelCreases(RemeshTopology topology, int quadCount, out int count)
        {
            var cell = topology.EdgeLengthForQuadCount(Mathf.Max(1, quadCount));
            var flat = PlanarRegions.Flatten(topology.positions, topology.triangles, cell, PlanarRegions.minAreaCells,
                panelFilletCells);
            var tris = topology.triangles;
            var m = tris.Length / 3;
            var first = new Dictionary<long, int>(3 * m);
            var edges = new List<(int a, int b)>();
            var minArea = panelCreaseMinCells * cell * cell;
            var cos = Mathf.Cos(panelCreaseDegrees * Mathf.Deg2Rad);
            var region = flat.triangleRegion;
            var rimCos = Mathf.Cos(panelRimDegrees * Mathf.Deg2Rad);
            var rims = new List<(int a, int b)>();
            Vector3 FaceNormal(int t) => MeshMath.SafeNormalize(Vector3.Cross(
                topology.positions[tris[3 * t + 1]] - topology.positions[tris[3 * t]],
                topology.positions[tris[3 * t + 2]] - topology.positions[tris[3 * t]]), Vector3.zero);
            for (var t = 0; t < m && region != null; t++)
                for (var k = 0; k < 3; k++)
                {
                    int a = tris[3 * t + k], b = tris[3 * t + (k + 1) % 3];
                    var key = Extraction.EdgeKey.PackUndirected(a, b);
                    if (!first.TryGetValue(key, out var u)) { first[key] = t; continue; }
                    int r0 = region[u], r1 = region[t];
                    if (r0 == r1) continue;
                    bool big0 = r0 >= 0 && flat.areas[r0] >= minArea, big1 = r1 >= 0 && flat.areas[r1] >= minArea;
                    if (big0 && big1)
                    {
                        if (Mathf.Abs(Vector3.Dot(flat.normals[r0], flat.normals[r1])) >= cos) continue;
                        edges.Add((a, b));
                    }
                    else if (panelRimCreases && (big0 || big1))
                    {
                        // One panel, and past its outline a turn off its plane.
                        var r = big0 ? r0 : r1;
                        var other = big0 ? t : u;
                        if (Mathf.Abs(Vector3.Dot(flat.normals[r], FaceNormal(other))) < rimCos) rims.Add((a, b));
                    }
                }
            // Counted without the outlines: whether the panels are used at all is decided as before.
            count = edges.Count;
            if (rims.Count > 0) edges.AddRange(LongPieces(topology.positions, rims, panelRimMinCells * cell));
            if (QuadReduction.logStats)
                Debug.Log($"[Native Remesher] panel creases: {edges.Count} edges from {flat}");
            return FeatureGraph.Detect(topology, 0f).WithEdges(topology, edges);
        }

        // The edges in pieces (joined through shared vertices) at least `minLength` long in all.
        static List<(int a, int b)> LongPieces(Vector3[] positions, List<(int a, int b)> edges, float minLength)
        {
            var parent = new Dictionary<int, int>();
            int Find(int v)
            {
                if (!parent.ContainsKey(v)) { parent[v] = v; return v; }
                var root = v;
                while (parent[root] != root) root = parent[root];
                while (parent[v] != root) { var next = parent[v]; parent[v] = root; v = next; }
                return root;
            }
            foreach (var (a, b) in edges)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }
            var length = new Dictionary<int, float>();
            foreach (var (a, b) in edges)
            {
                var r = Find(a);
                length[r] = (length.TryGetValue(r, out var l) ? l : 0f) + (positions[a] - positions[b]).magnitude;
            }
            var kept = new List<(int a, int b)>(edges.Count);
            foreach (var e in edges)
                if (length[Find(e.a)] >= minLength) kept.Add(e);
            return kept;
        }

        /// <summary>
        /// Hard Surface finds its creases at the scale of a quad rather than edge by edge: each edge's
        /// bend is measured between the faces either side of it within roundedCreaseRadiusCells
        /// (FeatureGraph.DetectAtScale), so a rounded edge -- a window frame, a door gap, a wheel
        /// arch spread over several triangles -- reads as the bend it is; then the strokes shorter
        /// than roundedCreaseMinCells or wandering are dropped, and of two parallel ones closer
        /// than roundedCreaseSeparationCells the shorter. Measured on a car's solve mesh at 5,000
        /// (128,022 triangles): the 20 degree test kept sparse left 810 edges in 375 scraps, 340
        /// cells long in all, and the windows, doors and arches came out melted; this keeps 3,923
        /// edges in 317 strokes, 1,653 cells, round every window, door, arch and bumper. Off: the
        /// 20 degree test with the density filter and the panels (the old behaviour).
        /// </summary>
        public static bool hardSurfaceRoundedCreases = false;

        /// <summary>
        /// The bend a rounded crease needs, degrees, across the radius. 35: at 30 a ribbed horn's
        /// rings pass (the helmet's horns kinked at each); at 40 the helmet's shell lattice came out
        /// two widths off somewhere and its part was collapsed to triangles. The cars' windows,
        /// doors and arches bend 60-90 and pass either way.
        /// </summary>
        public static float roundedCreaseDegrees = 35f;

        /// <summary>How far either side of an edge its bend is measured, in cells.</summary>
        public static float roundedCreaseRadiusCells = 0.4f;

        /// <summary>The shortest stroke kept, in cells, and how straight (end to end over length) it must run.</summary>
        public static float roundedCreaseMinCells = 3f, roundedCreaseStraightness = 0.6f;

        /// <summary>Two parallel strokes closer than this, in cells, keep only the longer.</summary>
        public static float roundedCreaseSeparationCells = 0.7f;

        /// <summary>
        /// Strokes with parallel strokes within this many cells on both sides -- a row of ribs, a
        /// horn's rings -- are dropped for the normal map (FeatureGraph.KeepUnribbed). The helmet's
        /// ribbed horns, pinned to every ring, came out kinked at each. 0: kept.
        /// </summary>
        public static float roundedCreaseRibCells = 1.5f;

        static FeatureGraph RoundedCreases(RemeshResult result, RemeshTopology topology, int quadCount)
        {
            if (result.rodCountRatio > 1f) quadCount = Mathf.Max(1, Mathf.RoundToInt(quadCount / result.rodCountRatio));
            var cell = topology.EdgeLengthForQuadCount(Mathf.Max(1, quadCount));
            var graph = FeatureGraph.DetectAtScale(topology, roundedCreaseDegrees, roundedCreaseRadiusCells * cell)
                .KeepCoherentCurves(topology, roundedCreaseMinCells * cell, roundedCreaseStraightness, out _, out _)
                .KeepSeparatedCurves(topology, roundedCreaseSeparationCells * cell, 0.5f, out _);
            if (roundedCreaseRibCells > 0f) graph = graph.KeepUnribbed(topology, roundedCreaseRibCells * cell, out _);
            if (QuadReduction.logStats)
                Debug.Log($"[Native Remesher] rounded creases: {graph.featureEdgeCount} edges in {graph.polylineCount} strokes on {topology.triangleCount} triangles");
            return graph;
        }

        /// <summary>Diagnostics: where each crease search writes its mesh (positions, triangles, count, angle), numbered; null: nowhere.</summary>
        public static string debugCreasesPath;
        static int s_DebugCreasesDumps;

        /// <summary>
        /// Hard Surface keeps only the creases the source bends at across a quad of the asked count
        /// (SourceCreases): at a low count a door handle's rim, a door's gap and the solve mesh's
        /// sliver streaks are left to the normal map, the window frames, sills and arches kept; at
        /// a higher count the smaller ones come back. Off: every crease found, at any count.
        /// </summary>
        public static bool hardSurfaceSourceCreases = true;

        /// <summary>
        /// ...on a source of at least this many triangles: one finer than the quads, with handles and
        /// gaps to leave out. The Ford's parts, a few thousand triangles each, came out a little
        /// more folded with it (8.75 -> 8.99 % of the area turned over).
        /// </summary>
        public static int sourceCreasesMinTriangles = 200000;

        /// <summary>...reading, where the panels gave no creases, every edge the profile's angle marks, not only those the density filter kept.</summary>
        public static bool hardSurfaceSourceCreaseCandidates = true;

        /// <summary>The profile's dihedral creases on <paramref name="topology"/>, as Hard Surface keeps them.</summary>
        static FeatureGraph ProfileFeatures(RemeshResult result, RemeshTopology topology, int quadCount,
            FeatureGraph detected = null)
        {
            var graph = ProfileFeaturesFound(result, topology, quadCount, detected);
            if (!hardSurfaceSourceCreases || result.profile.kind != RemeshProfileKind.HardSurface
                || result.originalPositions == null || result.originalTriangles == null
                || result.originalTriangles.Length / 3 < sourceCreasesMinTriangles) return graph;
            if (result.rodCountRatio > 1f) quadCount = Mathf.Max(1, Mathf.RoundToInt(quadCount / result.rodCountRatio));
            var cell = topology.EdgeLengthForQuadCount(Mathf.Max(1, quadCount));
            var stats = new SourceCreases.Stats();
            // Without panels, every edge bent past the profile's angle is a candidate too: the density
            // filter leaves out window frames rounded over a few triangles, which the source keeps. The
            // panels' lines are whole already; added to them the raw edges come back as scraps.
            var raw = hardSurfaceSourceCreaseCandidates && !result.panelCreases && result.profile.featureAngleDegrees > 0f
                ? FeatureGraph.Detect(topology, result.profile.featureAngleDegrees) : null;
            graph = SourceCreases.Keep(graph, topology, result.originalPositions, result.originalTriangles, cell,
                ref result.sourceCreaseIndex, stats, raw, joinPieces: s_JoinPieces && !detailQuads && !planarQuads);
            result.sourceCreaseStats = stats;
            if (QuadReduction.logStats) Debug.Log($"[Native Remesher] {stats} on {topology.triangleCount} triangles");
            return graph;
        }

        static FeatureGraph ProfileFeaturesFound(RemeshResult result, RemeshTopology topology, int quadCount,
            FeatureGraph detected)
        {
            if (debugCreasesPath != null)
            {
                // Diagnostics: the mesh the creases are found on, once, for an offline harness.
                var path = debugCreasesPath.Replace(".bin", $"_{s_DebugCreasesDumps++}.bin");
                using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
                w.Write(topology.positions.Length);
                foreach (var p in topology.positions) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }
                w.Write(topology.triangles.Length);
                foreach (var t in topology.triangles) w.Write(t);
                w.Write(quadCount);
                w.Write(result.profile.featureAngleDegrees);
            }
            if (hardSurfaceRoundedCreases && result.profile.kind == RemeshProfileKind.HardSurface)
                return RoundedCreases(result, topology, quadCount);
            var graph = detected ?? FeatureGraph.Detect(topology, result.profile.featureAngleDegrees);
            if (!hardSurfaceSparseCreases || result.profile.kind != RemeshProfileKind.HardSurface
                || result.profile.featureAngleDegrees <= 0f) return graph;
            // At the count without the rods' quads: they add a rod's own cells and change no crease.
            if (result.rodCountRatio > 1f) quadCount = Mathf.Max(1, Mathf.RoundToInt(quadCount / result.rodCountRatio));
            var cell = topology.EdgeLengthForQuadCount(Mathf.Max(1, quadCount));
            var marked = graph.featureEdgeCount;
            graph = graph.KeepSparseCreases(topology, sparseCreaseRadiusCells * cell, sparseCreaseMaxLength,
                out var dropped);
            result.denseCreasesDropped += dropped;
            // Decided once, on the first topology asked (the solve's); the coarser ones follow it.
            if (result.panelCreases) return PanelCreases(topology, quadCount, out _);
            if (hardSurfacePanelCreases && PlanarRegions.enabled && !result.panelCreasesRefused
                && marked > 0 && dropped >= panelCreasesDroppedShare * marked
                && result.originalTriangles != null && result.originalTriangles.Length / 3 >= panelCreasesMinTriangles)
            {
                var panels = PanelCreases(topology, quadCount, out var panelEdges);
                if (panelEdges >= panelCreasesMinShare * graph.featureEdgeCount)
                {
                    result.panelCreases = true;
                    return panels;
                }
                result.panelCreasesRefused = true;
            }
            return graph;
        }

        static float CreaseAngle(RemeshResult result) =>
            hardSurfaceCoherentCreases && result.modelType == ModelType.HardSurface && hardSurfaceCreaseAngle > 0f
                ? hardSurfaceCreaseAngle
                : Extraction.IntegerGridMap.coherentCreaseAngle;

        /// <summary>
        /// On a scan, a crease shorter than the minimum is still kept from this many cells when it
        /// touches a kept one (FeatureGraph.KeepCoherentCurves): a box built against a wall. 0: off.
        /// </summary>
        /// <remarks>
        /// OFF: measured and left off. On ccnb it turns the 5 x 9 m tangle of steps at a wall's end
        /// from a crumpled spike into a recognisable box, and lowers mean Hausdorff over ccnb, head
        /// and body x 3 counts from .0137 to .0116 -- but it also keeps the short junk mounted on
        /// every wall and roof edge, which then come out jagged, and mean irregular rises 3.15 ->
        /// 3.37 %, p05 falls (ccnb 5000 55.4 -> 52.1). 2.5 cells: the same, a little less so.
        /// </remarks>
        public static float scanAttachedMinCells = 0f;

        static float AttachedMinCells(RemeshResult result) => IsScan(result) ? scanAttachedMinCells : 0f;

        static bool IsScan(RemeshResult result) => result.profile.kind == RemeshProfileKind.Photogrammetry;

        static float CreaseMinCells(RemeshResult result, float general) =>
            !IsScan(result) ? general
            : scanCreaseMinCellsOpen > 0f && result.topology != null && result.topology.boundaryEdgeCount > 0
                ? scanCreaseMinCellsOpen
            : scanCreaseMinCells > 0f ? scanCreaseMinCells : general;

        /// <summary>
        /// <see cref="scanCreaseMinCells"/> for a scan with an open border -- ground and buildings,
        /// which a closed sculpt is not. 0: the same as closed ones.
        /// </summary>
        /// <remarks>
        /// With the layout's lines held through the smoothing (IntegerGridMap.relaxAlongLayoutLines)
        /// shorter building edges are worth laying. Means over 4600/5000/5400 (irregular /
        /// input-to-output mean / turn p90), 8 -> 5 cells: ccnb 5.12 -> 5.07 % / 0.131 -> 0.119 /
        /// 13.1 -> 13.1 deg, sheds and trailers come out as boxes (3 cells: 5.09 % / 0.108 / 14.6
        /// deg, crumpling); head 2.83 -> 2.66 % / 0.133 -> 0.125 / 13.2 -> 13.5; but the closed
        /// body, whose short creases are cloth folds, 1.00 -> 1.09 % / 0.101 -> 0.098 / 7.2 -> 8.2
        /// -- hence open meshes only.
        /// </remarks>
        public static float scanCreaseMinCellsOpen = 5f;

        static float CreaseStraightness(RemeshResult result) =>
            IsScan(result) && scanCreaseStraightness > 0f
                ? scanCreaseStraightness
                : Extraction.IntegerGridMap.coherentCreaseStraightness;

        /// <summary>Diagnostics: when set, the solver's crease edges after each filter, by stage name.</summary>
        public static List<(string stage, Vector3 a, Vector3 b)> debugCreaseStages;

        static void DebugStage(string stage, FeatureGraph graph, RemeshTopology topology)
        {
            if (debugCreaseStages == null) return;
            for (var c = 0; c < graph.polylineCount; c++)
                for (var i = graph.polylineStart[c] + 1; i < graph.polylineStart[c + 1]; i++)
                    debugCreaseStages.Add((stage, topology.positions[graph.polylineVertices[i - 1]], topology.positions[graph.polylineVertices[i]]));
        }

        /// <summary>
        /// Size the quads from an error-driven simplification of the input to the requested budget
        /// (ScaleField.FromDecimation) instead of from curvature: small where the shape needs
        /// polygons, large where it does not. Off: the curvature law.
        /// </summary>
        public static bool sizingFromDecimation = false;

        /// <summary>Triangles per requested quad in that simplification.</summary>
        public static float decimationSizingTrianglesPerQuad = 2f;

        /// <summary>Largest spacing over the median, and smallest under it, for that field.</summary>
        public static float decimationSizingMaxRatio = 8f;

        /// <summary>Size each coarse vertex by its shortest edge rather than its mean.</summary>
        public static bool decimationSizingMinimum = true;

        /// <summary>
        /// Shrink the cells round small sharp parts (ScaleField.ShrunkToSmallParts): slots, screws,
        /// buckles get several grid lines across instead of none. Off: as before.
        /// </summary>
        public static bool smallPartSizing = false;
        public static float smallPartAngle = 30f;
        public static float smallPartFraction = 0.35f;
        public static float smallPartMaxExtentCells = 3f;
        public static float smallPartMinLengthCells = 0.5f;
        public static float smallPartSlope = 0.5f;
        public static float smallPartFloorCells = 0.12f;

        /// <summary>Measure small parts by the gap between sharp edges (ScaleField.ShrunkToGaps).</summary>
        public static bool smallPartGaps = true;
        public static float smallPartDetour = 1.5f;

        /// <summary>
        /// For a Character (ModelType), no cell wider than a part's thickness over
        /// <see cref="thicknessCellsAcross"/> (ScaleField.CappedByThickness, rods only): fingers and
        /// toes get cells across them. Off: as before.
        /// </summary>
        /// <remarks>
        /// main1 at 5000: the fingers came out torn and broken -- each about one cell thick, where
        /// the curvature law (its ratio powered by 0.25 at Curve Detail 50) gave them 0.8 of the
        /// median cell. With the cap: faces 7,557 -> 9,939, Hausdorff 0.0086 -> 0.0057,
        /// input-to-output p99 0.51 -> 0.21 cells, irregular 1.43 -> 1.12 %, fingers whole. The
        /// body: 10,530 -> 10,979 faces, Hausdorff 0.0091 -> 0.0079. On models that are mostly thin
        /// (the helmet head 1.6x, the Sketchfab helmet 5.8x) it is not used: see
        /// <see cref="thicknessMaxGrowth"/>. Everything tried on the way is in
        /// Benchmarks/round5-parametrization.md, round 29.
        /// </remarks>
        public static bool thicknessSizing = true;

        /// <summary>Cells across a part's thickness under <see cref="thicknessSizing"/>.</summary>
        public static float thicknessCellsAcross = 4f;

        /// <summary>Parts thinner than this many local cells are sheets, not fingers, and not refined.</summary>
        public static float thicknessThinnestCells = 0.5f;

        /// <summary>The cap never takes a cell under this fraction of the field's own there.</summary>
        public static float thicknessFloor = 0f;

        /// <summary>
        /// Cells no wider than the gap between a crest and the groove beside it over
        /// <see cref="ridgeGapCellsPerGap"/> (ScaleField.CappedByRidgeGaps): meant for fingers
        /// pressed together (the body's glove), toes, welts. Off: as before.
        /// </summary>
        /// <remarks>
        /// MEASURED, AND IT DID NOT WORK, so it is off. On the body at 5000 the glove's spacing did
        /// not move (0.00875 either way) while the coat's folds took the quads: 10,530 -> 16,129
        /// faces at strength 0.8; averaged over a quarter cell instead of half, the hand gained a
        /// little (0.0080) and the folds as much again. Half a cell spans a finger's crest, groove
        /// and the next crest, so they cancel; a fold is as sharp as a groove.
        /// </remarks>
        public static bool ridgeGapSizing = false;
        public static float ridgeGapStrength = 0.8f;
        public static float ridgeGapReachCells = 2f;
        public static float ridgeGapMinSlopeDegrees = 30f;
        public static float ridgeGapCellsPerGap = 1.5f;
        public static float ridgeGapFloor = 0.4f;

        /// <summary>Last run's vertices capped by the ridge gaps (diagnostic).</summary>
        public static int lastRidgeGapCapped;

        /// <summary>
        /// Hard Surface: an open strip narrower than a few cells -- a car's pillar strips, trim and
        /// flanges -- gets <see cref="stripCellsAcross"/> cells across it (ScaleField.StripWidth), the
        /// way a finger gets rows round it. A grid coarser than the strip drops it or shreds it: the
        /// Porsche's body at 600 lost both rear pillar strips (Hausdorff 0.22). Off: not measured.
        /// </summary>
        public static bool stripSizing = true;

        /// <summary>Cells across an open strip under <see cref="stripSizing"/>.</summary>
        public static float stripCellsAcross = 2f;

        /// <summary>Strips narrower than this many cells are a lip, not a strip, and are left alone.</summary>
        public static float stripThinnestCells = 0.25f;

        /// <summary>The strip cap runs for this type.</summary>
        static bool StripCapFor(ModelType type) => type == ModelType.HardSurface;

        static float[] StripWidthFor(RemeshResult result, ScaleField field)
        {
            if (result.stripWidth != null && result.stripWidthFor == result.topology
                && result.stripWidth.Length == field.spacing.Length) return result.stripWidth;
            // Out to twice the widest strip the cap acts on, so a later field reads the same widths.
            var reach = new float[field.spacing.Length];
            for (var i = 0; i < reach.Length; i++) reach[i] = 2f * stripCellsAcross * field.spacing[i];
            result.stripWidth = ScaleField.StripWidth(result.topology, reach);
            result.stripWidthFor = result.topology;
            return result.stripWidth;
        }

        static ScaleField DetailCaps(RemeshResult result, ScaleField field, bool hardSurfaceRodCap = true)
        {
            if (stripSizing && StripCapFor(result.modelType) && !(result.thicknessSkipped > 0f)
                && result.topology.boundaryEdgeCount > 0)
                field = ScaleField.CappedByThickness(result.topology, field, StripWidthFor(result, field),
                    stripCellsAcross, stripThinnestCells, result.profile.curvatureGradation);
            if (ThicknessCapFor(result.modelType) && !(result.thicknessSkipped > 0f)
                && (result.modelType != ModelType.HardSurface || hardSurfaceRodCap && !(result.rodsSkipped > 0f)))
            {
                var rods = result.modelType == ModelType.HardSurface;
                field = ScaleField.CappedByThickness(result.topology, field, ThicknessFor(result, field),
                    rods ? hardSurfaceRodCellsAcross : thicknessCellsAcross,
                    rods ? hardSurfaceRodThinnestCells : thicknessThinnestCells, result.profile.curvatureGradation,
                    thicknessFloor);
            }
            if (ridgeGapSizing)
            {
                field = ScaleField.CappedByRidgeGaps(result.topology, field, ridgeGapStrength, ridgeGapReachCells,
                    ridgeGapMinSlopeDegrees, ridgeGapCellsPerGap, ridgeGapFloor, result.profile.curvatureGradation,
                    out var capped);
                lastRidgeGapCapped = capped;
            }
            return field;
        }

        /// <summary>
        /// Most the thickness cap may raise the quad count by. Past it the model is mostly thin and
        /// the cap is not used (RemeshResult.thicknessSkipped).
        /// </summary>
        public static float thicknessMaxGrowth = 1.5f;

        /// <summary>
        /// What a model type changes, on top of the pipeline's shared defaults (which every type
        /// gets: the line-holding relaxation, the smoothed bake, input refinement, the layout).
        /// </summary>
        /// <remarks>
        /// Character keeps the profile the caller chose (or Auto Profile's pick, which measured best
        /// on the head and the body) and adds the thickness cap and the strap finder. Hard Surface
        /// and Scan choose their profile: creases at 20 degrees for a modelled part -- the car's
        /// panels were read as a scan by the crease probe -- and none for a scan.
        /// </remarks>
        static void ApplyModelType(ModelType type, ref RemeshProfile profile, ref bool autoSelectProfile)
        {
            RemeshProfileKind kind;
            switch (type)
            {
                case ModelType.HardSurface: kind = RemeshProfileKind.HardSurface; break;
                case ModelType.Scan: kind = RemeshProfileKind.Photogrammetry; break;
                default: return;
            }
            if (profile.kind != kind || autoSelectProfile)
            {
                var chosen = RemeshProfile.For(kind);
                chosen.extractor = profile.extractor;
                profile = chosen;
                autoSelectProfile = false;
            }
            // Creases by chains rather than by edges (see hardSurfaceCoherentCreases).
            if (kind == RemeshProfileKind.HardSurface && hardSurfaceCoherentCreases) profile.featureAngleDegrees = 0f;
        }

        /// <summary>The strap finder (RibbonParts) runs for this type: not on a car or a scan.</summary>
        static bool StrapsFor(ModelType type) => type != ModelType.HardSurface && type != ModelType.Scan;

        /// <summary>The thickness cap runs for this type: characters' fingers and toes under <see cref="thicknessSizing"/>, Hard Surface's rods under <see cref="hardSurfaceRods"/>.</summary>
        static bool ThicknessCapFor(ModelType type) =>
            type == ModelType.Character && thicknessSizing || type == ModelType.HardSurface && hardSurfaceRods;

        /// <summary>
        /// For Hard Surface, the thickness cap on rods too (<see cref="thicknessSizing"/>): a pipe, a
        /// handrail, a mirror's arm, a gun's barrel gets cells across it. Thinner than a cell, the
        /// grid had nothing to run round it: the rod was dropped and what it held -- a mirror, a
        /// muzzle -- was left hanging in the air. Off: as before.
        /// </summary>
        /// <remarks>
        /// The armoured car at 5000 (one fused shell, rods everywhere): 9,613 -> 19,318 faces, its
        /// barrel round and on its mount again, the mirrors' frames and arms whole. At 1 cell across
        /// (3 quads round) the tubes collapsed and nothing was kept; on every rod (window bars,
        /// boards, handrails along the hull) 28,038 faces, and the white house's windows crumpled.
        /// Hence the limits below: rods 0.2 to 0.6 of a cell thick, standing free, adding at least
        /// a quarter. The count is raised for them under Curves Add Quads only, as for fingers.
        /// </remarks>
        public static bool hardSurfaceRods = true;

        /// <summary>Cells across a Hard Surface rod (about 8 quads round it).</summary>
        public static float hardSurfaceRodCellsAcross = 2.5f;

        /// <summary>Most Hard Surface's rods may raise the quad count by (as <see cref="thicknessMaxGrowth"/>).</summary>
        public static float hardSurfaceRodMaxGrowth = 4f;

        /// <summary>
        /// Hard Surface rods thicker than this many local cells are not refined: the grid holds them
        /// (a chimney, an eave's board), and on the white house at 5000 refining them took the count
        /// 1.84x and turned its windows to lumps. 0: no limit.
        /// </summary>
        public static float hardSurfaceRodThickestCells = 0.6f;

        /// <summary>
        /// Hard Surface rods are refined only when at most this share of their vertices face another
        /// surface within <see cref="hardSurfaceRodClearanceCells"/> (ScaleField.FreeRods): a window's
        /// bars, boxed in by glass and frame, are not (the white house at 5000: 15,113 -> 33,043 faces
        /// and its windows crumpled with them). 1: every rod.
        /// </summary>
        public static float hardSurfaceRodBoxedShare = 0.4f;

        /// <summary>How far, in cells, a rod's vertex looks out for a surface boxing it in.</summary>
        public static float hardSurfaceRodClearanceCells = 2f;

        /// <summary>
        /// Hard Surface's rods are refined only when they add at least this much to the count: a few
        /// stray ones (the white house at 5000: 1.14x) are not worth a different solve -- the
        /// lattice they changed was no longer clearly further off than the error-driven quads, and
        /// the house lost the crisp windows those keep. The armoured car's: 2.04x.
        /// </summary>
        public static float hardSurfaceRodMinGrowth = 1.25f;

        /// <summary>
        /// Hard Surface rods thinner than this many local cells are not refined: a window's bars on
        /// the white house at 5000 are 0.1 to 0.2 of a cell, the armoured car's mirror arms,
        /// handrails and aerials 0.2 to 0.6.
        /// </summary>
        public static float hardSurfaceRodThinnestCells = 0.2f;

        static float[] ThicknessFor(RemeshResult result, ScaleField field)
        {
            if (result.thickness != null && result.thicknessFor == result.topology
                && result.thickness.Length == field.spacing.Length) return result.thickness;
            // Looked for out to twice the reach this field needs, so a later field sized for a
            // somewhat coarser count reads the same thicknesses.
            var reach = new float[field.spacing.Length];
            for (var i = 0; i < reach.Length; i++) reach[i] = 2f * thicknessCellsAcross * field.spacing[i];
            result.thickness = ScaleField.Thickness(result.topology, reach);
            if (result.modelType == ModelType.HardSurface && hardSurfaceRodThickestCells > 0f)
                for (var i = 0; i < reach.Length; i++)
                    if (result.thickness[i] > hardSurfaceRodThickestCells * field.spacing[i])
                        result.thickness[i] = float.PositiveInfinity;
            if (result.modelType == ModelType.HardSurface && hardSurfaceRodBoxedShare < 1f)
            {
                var clearance = new float[reach.Length];
                for (var i = 0; i < clearance.Length; i++) clearance[i] = hardSurfaceRodClearanceCells * field.spacing[i];
                result.thickness = ScaleField.FreeRods(result.topology, result.thickness, clearance, hardSurfaceRodBoxedShare);
                if (QuadReduction.logStats)
                    Debug.Log($"[Native Remesher] rods: {ScaleField.lastFreeRods} free, {ScaleField.lastBoxedRods} boxed in");
            }
            result.thicknessFor = result.topology;
            return result.thickness;
        }

        static ScaleField SizingField(RemeshResult result, int target, float adaptivity, bool withPaint = true,
            bool withThickness = true)
        {
            // Painted: sized for the count the unpainted surface takes, then multiplied by the paint.
            var paintFactors = result.paintCountRatio > 0f ? PaintFactors(result) : null;
            if (!withPaint && paintFactors != null)
            {
                // The unpainted field at the count the unpainted surface takes.
                target = Mathf.Max(4, Mathf.RoundToInt(target / result.paintCountRatio));
                paintFactors = null;
            }
            if (paintFactors != null)
                target = Mathf.Max(4, Mathf.RoundToInt(target / result.paintCountRatio));
            if (withThickness && result.thinCountRatio > 0f)
                target = Mathf.Max(4, Mathf.RoundToInt(target / result.thinCountRatio));
            var field = SizingFieldCore(result, target, adaptivity);
            if (withThickness && result.topology != null) field = DetailCaps(result, field);
            if (smallPartSizing && smallPartGaps && result.topology != null)
                field = ScaleField.ShrunkToGaps(result.topology, field, target, smallPartAngle, smallPartFraction,
                    smallPartMaxExtentCells, smallPartMinLengthCells, smallPartSlope, smallPartFloorCells, smallPartDetour);
            else if (smallPartSizing && result.topology != null)
                field = ScaleField.ShrunkToSmallParts(result.topology, field, target, smallPartAngle, smallPartFraction,
                    smallPartMaxExtentCells, smallPartMinLengthCells, smallPartSlope, smallPartFloorCells);
            if (paintFactors != null)
                field = ScaleField.Painted(result.topology, field, paintFactors, result.profile.curvatureGradation);
            return field;
        }

        /// <summary>
        /// Curves Add Quads reads the flat spacing off the field without the paint (the paint's
        /// quads are counted separately, paintCountRatio). False: off the painted field, as before,
        /// where painting Less Detail on more than a tenth of the surface raised the count.
        /// </summary>
        public static bool adaptiveCountIgnoresPaint = true;

        /// <summary>
        /// Thin free-standing bands of the input (straps, belts) are recognised and meshed on their
        /// own as quad tubes, and the rest solved without them. See <see cref="RibbonParts"/>.
        /// False: as before, every part goes through the lattice.
        /// </summary>
        public static bool ribbonParts = false;

        [ThreadStatic] static bool t_SolvingWithoutRibbons;

        // The input's ribbons found and taken out, the rest solved (with Symmetry, if asked: the
        // ribbons are meshed whole, both sides), the ribbons' tubes added to the output.
        static RemeshResult SolveWithRibbons(Vector3[] positions, int[] triangles, RemeshSettings settings,
            RemeshProfile profile, Action<string, float> onProgress, Vector2[] sourceUvs, bool autoSelectProfile,
            int[] triangleMaterials)
        {
            t_SolvingWithoutRibbons = true;
            try
            {
                onProgress?.Invoke("Finding ribbons", 0f);
                double area = 0;
                for (var t = 0; t + 2 < triangles.Length; t += 3)
                    area += Vector3.Cross(positions[triangles[t + 1]] - positions[triangles[t]],
                        positions[triangles[t + 2]] - positions[triangles[t]]).magnitude * 0.5;
                var cell = (float)Math.Sqrt(area / Math.Max(1, settings.targetQuadCount));
                var stats = new RibbonParts.Stats();
                var blocks = new List<RibbonParts.Block>();
                var ribbons = RibbonParts.Find(positions, triangles, cell, out var keptPositions, out var keptTriangles,
                    out var sourceOf, out var outward, out var surface, stats, blocks);
                if (ribbons.Count == 0)
                {
                    var plain = SolveInternal(positions, triangles, settings, profile, onProgress, sourceUvs,
                        autoSelectProfile, SymmetryAxis.None, 0f, triangleMaterials);
                    plain.ribbonStats = stats;
                    return plain;
                }
                // Roughly what the tubes will take at this size, off the count for the rest.
                var estimate = 0f;
                foreach (var r in ribbons)
                    estimate += r.length / cell * (2 * Mathf.Max(1, Mathf.RoundToInt(r.medianWidth / (RibbonParts.quadWidthCells * cell))) + 2);
                var inner = settings;
                inner.targetQuadCount = Mathf.Max(Mathf.Max(4, settings.targetQuadCount / 4),
                    settings.targetQuadCount - Mathf.RoundToInt(estimate));
                int[] innerMaterials = null;
                if (triangleMaterials != null && triangleMaterials.Length == triangles.Length / 3)
                {
                    innerMaterials = new int[sourceOf.Length];
                    for (var t = 0; t < sourceOf.Length; t++) innerMaterials[t] = triangleMaterials[sourceOf[t]];
                }
                Vector2[] innerUvs = null;
                if (sourceUvs != null && sourceUvs.Length == positions.Length)
                {
                    innerUvs = new Vector2[keptPositions.Length];
                    Array.Copy(sourceUvs, innerUvs, sourceUvs.Length);
                }
                var result = SolveInternal(keptPositions, keptTriangles, inner, profile, onProgress, innerUvs,
                    autoSelectProfile, SymmetryAxis.None, 0f, innerMaterials);
                result.ribbonStats = stats;
                // At the edge length the rest came out at (Adaptive Quad Count moves it far off
                // the requested count's).
                var meshCell = cell;
                var ex = result.extraction;
                if (ex?.positions != null && ex.faces.Count > 0)
                {
                    double sum = 0; var n = 0;
                    foreach (var f in ex.faces)
                        for (var i = 0; i < f.Length; i++) { sum += Vector3.Distance(ex.positions[f[i]], ex.positions[f[(i + 1) % f.Length]]); n++; }
                    if (n > 0) meshCell = (float)(sum / n);
                }
                RibbonParts.Mesh(ribbons, blocks, surface, meshCell, outward, out var ribbonPositions, out var ribbonFaces);
                stats.quads = ribbonFaces.Count;
                AppendRibbons(result, ribbonPositions, ribbonFaces, positions, triangles, sourceUvs);
                return result;
            }
            finally
            {
                t_SolvingWithoutRibbons = false;
            }
        }

        static void AppendRibbons(RemeshResult result, Vector3[] ribbonPositions, List<int[]> ribbonFaces,
            Vector3[] positions, int[] triangles, Vector2[] sourceUvs)
        {
            var ex = result.extraction;
            if (ex == null || ex.positions == null || ribbonPositions.Length == 0) return;
            var baseIndex = ex.positions.Length;
            T[] Concat<T>(T[] a, T[] b)
            {
                var c = new T[a.Length + b.Length];
                Array.Copy(a, c, a.Length);
                Array.Copy(b, 0, c, a.Length, b.Length);
                return c;
            }
            var ribbonNormals = Extraction.NormalRecalculator.Compute(ribbonFaces, ribbonPositions, null);
            if (ex.normals != null && ex.normals.Length == baseIndex) ex.normals = Concat(ex.normals, ribbonNormals);
            if (ex.shadingNormals != null && ex.shadingNormals.Length == baseIndex)
                ex.shadingNormals = Concat(ex.shadingNormals, ribbonNormals);
            if (ex.uvs != null && ex.uvs.Length == baseIndex && sourceUvs != null)
                ex.uvs = Concat(ex.uvs, AttributeTransfer.SampleUvs(positions, triangles, sourceUvs, ribbonPositions));
            var faceCount = ex.faces.Count;
            if (ex.faceUvs != null && ex.faceUvs.Length == faceCount && sourceUvs != null)
                ex.faceUvs = Concat(ex.faceUvs, AttributeTransfer.SampleFaceUvs(positions, triangles, sourceUvs,
                    ribbonPositions, ribbonFaces, out _));
            ex.faceTangents = null;
            ex.positions = Concat(ex.positions, ribbonPositions);
            foreach (var f in ribbonFaces)
            {
                var g = new int[f.Length];
                for (var i = 0; i < f.Length; i++) g[i] = f[i] + baseIndex;
                ex.faces.Add(g);
            }
            ex.quadCount += ribbonFaces.Count;
            if (ex.faceMaterials != null) LabelFaces(result);
        }

        /// <summary>
        /// With Symmetry, the paint is read symmetrically (DensityPaint.Mirrored): paint on the half
        /// that is cut away counts. False: as before, where only the solved half's paint counted.
        /// </summary>
        public static bool symmetricPaint = true;

        /// <summary>
        /// The quad layout's face count over the target at the target's own spacing: the first
        /// attempt starts at sqrt of it times that spacing. Measured over 2026-10 runs (head, body,
        /// ccnb; 2,000-15,000): 0.83-0.91, mean 0.86 -- so the first attempt nearly always missed the
        /// 10 % tolerance and the count search ran a second pass (head at 3,000: 2 s of 15; ccnb at
        /// 5,000: 6 s of 25). 0 or 1: start at the target's spacing, as before.
        /// </summary>
        public static float layoutCountAtSpacing = 0.86f;

        /// <summary>The paint's spacing multiplier at each vertex of the current topology, cached.</summary>
        static float[] PaintFactors(RemeshResult result)
        {
            if (result.densityPaint == null || result.topology == null) return null;
            if (result.paintFactorsFor != result.topology)
            {
                result.paintFactors = result.densityPaint.SpacingFactors(result.topology.positions);
                result.paintFactorsFor = result.topology;
            }
            return result.paintFactors;
        }

        static ScaleField SizingFieldCore(RemeshResult result, int target, float adaptivity)
        {
            if (sizingFromDecimation && result.originalPositions != null && result.originalTriangles != null)
            {
                if (result.sizingCoarseTriangles == null)
                {
                    var budget = Mathf.Max(64, Mathf.RoundToInt(
                        decimationSizingTrianglesPerQuad * Mathf.Max(1, result.requestedQuadCount > 0 ? result.requestedQuadCount : target)));
                    var options = DecimateOptions.ForTriangleCount(budget);
                    if (decimateKeepsCreases && Extraction.IntegerGridMap.longCreaseLoops)
                    {
                        double area = 0;
                        var op = result.originalPositions; var ot = result.originalTriangles;
                        for (var t = 0; t + 2 < ot.Length; t += 3)
                            area += Vector3.Cross(op[ot[t + 1]] - op[ot[t]], op[ot[t + 2]] - op[ot[t]]).magnitude * 0.5;
                        options.creaseAngle = Extraction.IntegerGridMap.coherentCreaseAngle;
                        options.creaseMinLength = decimateCreaseMinCells * (float)Math.Sqrt(area / Math.Max(1, budget / 2));
                        options.creaseStraightness = decimateCreaseStraightness;
                    }
                    PreDecimate.Decimate(result.originalPositions, result.originalTriangles, options, out _,
                        out result.sizingCoarsePositions, out result.sizingCoarseTriangles);
                }
                return ScaleField.FromDecimation(result.topology, target, result.sizingCoarsePositions,
                    result.sizingCoarseTriangles, decimationSizingMaxRatio, result.profile.curvatureGradation,
                    decimationSizingMinimum);
            }
            return ScaleField.FromCurvature(result.topology, target, adaptivity,
                result.profile.curvatureMaxRatio, result.profile.curvatureGradation);
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
            Add(ProfileFeatures(result, topology, result.effectiveTargetQuadCount));
            // Material borders (Material Borders As Loops): the decimator kept them as edge chains.
            if (result.materials != null)
            {
                var materialEdges = MaterialLabels.BorderEdges(topology.triangles,
                    result.materials.ForTriangles(topology.positions, topology.triangles));
                result.decimatedMaterialBorderEdges = materialEdges.Count;
                edges.AddRange(materialEdges);
            }
            if (Extraction.IntegerGridMap.longCreaseLoops && result.profile.featureAngleDegrees <= 0f)
            {
                var cell = topology.EdgeLengthForQuadCount(Mathf.Max(1, result.effectiveTargetQuadCount));
                var detected = DetectCreases(result, topology, cell);
                DebugStage("detected", detected, topology);
                var creases = detected
                    .KeepCoherentCurves(topology, CreaseMinCells(result, Extraction.IntegerGridMap.parametrizationCreaseMinCells) * cell,
                        CreaseStraightness(result), AttachedMinCells(result) * cell, out _, out _);
                // Two grid lines closer than a cell squash the cells between them flat.
                // The layout lays every crease as a patch border, so it always wants them apart:
                // the helmet rim's cable and lip a fraction of a cell apart came out as spikes.
                var separation = Extraction.IntegerGridMap.parametrizationLayout
                    ? Mathf.Max(Extraction.IntegerGridMap.parametrizationCreaseSeparationCells, Extraction.IntegerGridMap.layoutCreaseSeparationCells)
                    : Extraction.IntegerGridMap.parametrizationCreaseSeparationCells;
                DebugStage("coherent", creases, topology);
                if (separation > 0f)
                    creases = creases.KeepSeparatedCurves(topology,
                        separation * cell,
                        Extraction.IntegerGridMap.coherentCreaseMaxShared, out _);
                DebugStage("separated", creases, topology);
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

            // The window's "Curve Detail", when set, replaces both.
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.curveDetail >= 0f)
                result.profile.curvatureAdaptivity = Mathf.Clamp01(Extraction.IntegerGridMap.curveDetail
                                                                   * Extraction.IntegerGridMap.curveDetailScale);

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

            // --- painted areas ---
            //
            // The field is sized for the target as if nothing were painted, then multiplied by the
            // paint; the target is raised by the quads that adds, so the count control downstream
            // does not take them back from the unpainted surface. See SizingField.
            if (result.densityPaint != null && settings.targetQuadCount > 0)
            {
                result.paintCountRatio = 0f;
                var unpainted = SizingFieldCore(result, settings.targetQuadCount, result.profile.curvatureAdaptivity);
                var factors = PaintFactors(result);
                if (factors != null)
                {
                    var areas = result.topology.areas;
                    double plain = 0, painted = 0;
                    for (var i = 0; i < factors.Length; i++)
                    {
                        var h = unpainted.spacing[i];
                        if (!(h > 0f)) continue;
                        plain += areas[i] / (h * h);
                        painted += areas[i] / (h * h * factors[i] * factors[i]);
                    }
                    var ratio = plain > 0 ? Mathf.Clamp((float)(painted / plain), 0.125f, 8f) : 1f;
                    result.paintCountRatio = ratio;
                    settings.targetQuadCount = Mathf.Max(4, Mathf.RoundToInt(settings.targetQuadCount * ratio));
                }
            }

            // --- Adaptive Quad Count (integer grid map) ---
            //
            // A different reading of the target: it sizes the quads
            // on FLAT ground as a uniform mesh of the requested count would, and lets curved areas
            // take smaller quads on top -- so the result can carry more quads than were asked for.
            // Held to the exact count instead, every quad a finger gains is taken from somewhere flat, and on
            // the body at 5000 the four pressed-together fingers of the glove came out as one
            // paddle three quads wide. So the target is raised here, before anything is sized, by
            // how much a field whose flat spacing is the uniform one would need.
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.curvesAddQuads
                && result.profile.curvatureAdaptivity > 0f && settings.targetQuadCount > 0
                && !(hardSurfaceCleanLayout && hardSurfaceCleanExactCount && result.modelType == ModelType.HardSurface))
            {
                var requested = settings.targetQuadCount;
                // Counted at the slider's own value: curveDetailScale softens how steeply the
                // lattice grades, not how many quads the user asked curved areas to gain.
                var countAdaptivity = Extraction.IntegerGridMap.countGrowthFromDetail
                                      && Extraction.IntegerGridMap.curveDetail >= 0f
                    ? Mathf.Clamp01(Extraction.IntegerGridMap.curveDetail)
                    : result.profile.curvatureAdaptivity;
                // The flat spacing is read off the UNPAINTED field: a large area painted for Less
                // Detail is coarse, and read as "flat" it raised the count -- on a sphere with a
                // quarter painted, 826 -> 2,160 faces and the unpainted rest 3x denser.
                var shape = SizingField(result, requested, countAdaptivity, !adaptiveCountIgnoresPaint);
                // Against the request as it was before the paint raised it: the paint's quads are
                // already in it, and the flat spacing is the unpainted surface's.
                var uniform = result.topology.EdgeLengthForQuadCount(result.paintCountRatio > 0f
                    ? Mathf.Max(4, Mathf.RoundToInt(requested / result.paintCountRatio))
                    : requested);
                // "Flat" is the spacing most of the surface is at the coarse end of -- a high
                // percentile rather than the maximum, which one flat vertex could set.
                var flat = AreaPercentile(shape.spacing, result.topology.areas,
                    Extraction.IntegerGridMap.flatPercentile);
                var factor = uniform > 0f && flat > 0f ? (flat / uniform) * (flat / uniform) : 1f;
                factor = Mathf.Clamp(factor, 1f, Extraction.IntegerGridMap.countMaxGrowth);
                // Under the solve-mesh cap, no more quads than the capped mesh can carry.
                if (result.solveTrianglesCapped && result.decimatedTriangles != null && minSolveTrianglesPerQuad > 0f)
                {
                    var carried = result.decimatedTriangles.Length / 3 / minSolveTrianglesPerQuad;
                    factor = Mathf.Max(1f, Mathf.Min(factor, carried / requested));
                }
                result.requestedQuadCount = requested;
                result.curveCountFactor = factor;
                settings.targetQuadCount = Mathf.RoundToInt(requested * factor);
            }

            // --- thin parts' extra quads ---
            //
            // As the paint: the field is sized for the target and then capped where the model is
            // thin, and the target is raised by what the cap adds, so a finger's quads are not taken
            // back from the flat ground by the count control. The target is raised only under
            // Curves Add Quads, which is the setting that allows the result past it; off, the count
            // holds and the finger's quads come from everywhere else.
            result.thinCountRatio = 0f;
            result.rodCountRatio = 0f;
            result.rodsSkipped = 0f;
            if ((ThicknessCapFor(result.modelType) || ridgeGapSizing
                 || stripSizing && StripCapFor(result.modelType) && result.topology.boundaryEdgeCount > 0)
                && result.profile.integerGridMap
                && result.profile.curvatureAdaptivity > 0f && settings.targetQuadCount > 0
                && (!result.profile.integerGridMap || Extraction.IntegerGridMap.gradeSpacing))
            {
                var plain = SizingField(result, settings.targetQuadCount, result.profile.curvatureAdaptivity,
                    withThickness: false);
                var before = ScaleField.QuadCount(result.topology, plain);
                float Ratio(ScaleField capped) =>
                    before > 0 ? (float)(ScaleField.QuadCount(result.topology, capped) / before) : 1f;
                var rods = result.modelType == ModelType.HardSurface && hardSurfaceRods;
                var ratio = Ratio(DetailCaps(result, plain, hardSurfaceRodCap: false));
                if (QuadReduction.logStats) Debug.Log($"[Native Remesher] thin caps: {ratio:0.###}x the quads");
                // A model that is mostly thin -- knotwork, chain mail, a lattice of rods -- would
                // want several times the quads; squeezed into the limit, the rest of it coarsens.
                // Fingers and toes are a small share of a body, so past the limit it is not used.
                if (ratio > thicknessMaxGrowth)
                {
                    result.thicknessSkipped = ratio;
                    ratio = 1f;
                }
                // Hard Surface's rods on top, under their own limit (the strips' decision is as before).
                var withRods = ratio;
                if (rods && !(result.thicknessSkipped > 0f))
                {
                    withRods = Ratio(DetailCaps(result, plain));
                    if (QuadReduction.logStats) Debug.Log($"[Native Remesher] rods: {withRods:0.###}x the quads");
                    if (withRods > hardSurfaceRodMaxGrowth || withRods < hardSurfaceRodMinGrowth * ratio)
                    {
                        result.rodsSkipped = withRods;
                        withRods = ratio;
                    }
                    withRods = Mathf.Max(withRods, ratio);
                }
                ratio = Mathf.Clamp(ratio, 1f, Extraction.IntegerGridMap.countMaxGrowth);
                withRods = Mathf.Clamp(withRods, 1f, Extraction.IntegerGridMap.countMaxGrowth);
                if (withRods > 1.001f && Extraction.IntegerGridMap.curvesAddQuads
                    && !(hardSurfaceCleanLayout && hardSurfaceCleanExactCount && result.modelType == ModelType.HardSurface))
                {
                    result.thinCountRatio = withRods;
                    result.rodCountRatio = withRods / ratio;
                    settings.targetQuadCount = Mathf.RoundToInt(settings.targetQuadCount * withRods);
                }
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
                && (result.profile.curvatureAdaptivity > 0f || result.paintCountRatio > 0f))
            {
                var localTarget = settings.targetQuadCount;
                var localCeiling = result.topology.MaxQuadCountForInput();
                if (localCeiling > 0 && localTarget > localCeiling) localTarget = localCeiling;
                var grading = SizingField(result, localTarget, result.profile.curvatureAdaptivity);
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
            result.features = ProfileFeatures(result, result.topology, settings.targetQuadCount,
                profileProbe != null && result.profile.featureAngleDegrees == autoProfileProbeAngleDegrees
                    ? profileProbe
                    : null);

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
            // places loops in them. Loops Along Creases finds them: only the crease chains that run straight for more than a cell or so are
            // kept, which is what separates a groove, seam or panel line from noise fragments. See
            // FeatureGraph.KeepCoherentCurves.
            FeatureGraph longCreaseLoops = null;
            if (result.profile.integerGridMap && Extraction.IntegerGridMap.longCreaseLoops
                && result.profile.featureAngleDegrees <= 0f)
            {
                var creaseCell = result.topology.EdgeLengthForQuadCount(Mathf.Max(1, settings.targetQuadCount));
                longCreaseLoops = DetectCreases(result, result.topology, creaseCell)
                    .KeepCoherentCurves(result.topology,
                        CreaseMinCells(result, Extraction.IntegerGridMap.coherentCreaseMinCells) * creaseCell,
                        CreaseStraightness(result), AttachedMinCells(result) * creaseCell,
                        out result.creaseLoopsKept, out result.creaseLoopsDropped);
                // Cutting along them as well -- sharp offsets, crease resampling, vertex placement
                // -- glues long strips of the lattice into single cells wherever a crease is doubled
                // or noisy: on the body, needle quads up to 11x the median edge across the thighs,
                // and 1.89 -> 3.26 % irregular. Steering the cross field alone puts a loop in the
                // finger grooves with neither (Hausdorff 0.0093 -> 0.0090).
                if (Extraction.IntegerGridMap.longCreaseLoopsCut)
                {
                    // Only the long creases are cut when coherentCreaseCutMinCells asks for it: a
                    // long clean panel edge becomes an exact iso-line, while the short dense ones
                    // (velcro, stitching) that tear a cut lattice keep steering only.
                    var toCut = Extraction.IntegerGridMap.coherentCreaseCutMinCells
                                > Extraction.IntegerGridMap.coherentCreaseMinCells
                        ? longCreaseLoops.KeepCoherentCurves(result.topology,
                            Extraction.IntegerGridMap.coherentCreaseCutMinCells * creaseCell,
                            Mathf.Max(Extraction.IntegerGridMap.coherentCreaseStraightness,
                                Extraction.IntegerGridMap.coherentCreaseCutStraightness), out _, out _)
                        : longCreaseLoops;
                    result.features = Extraction.IntegerGridMap.coherentCreaseMinSeparationCells > 0f
                        ? toCut.KeepSeparatedCurves(result.topology,
                            Extraction.IntegerGridMap.coherentCreaseMinSeparationCells * creaseCell,
                            Extraction.IntegerGridMap.coherentCreaseMaxShared,
                            out result.creaseLoopsThinned)
                        : toCut;
                }
            }

            // --- material borders, as edge loops (Material Borders As Loops) ---
            if (result.materials != null)
            {
                var materialEdges = MaterialLabels.BorderEdges(result.topology.triangles,
                    result.materials.ForTriangles(result.topology.positions, result.topology.triangles));
                result.materialBorderEdges = materialEdges.Count;
                if (materialEdges.Count > 0)
                {
                    result.features = result.features.WithEdges(result.topology, materialEdges);
                    if (longCreaseLoops != null) longCreaseLoops = longCreaseLoops.WithEdges(result.topology, materialEdges);
                }
            }

            // --- the curve network: cut the lattice along ridges and valleys (integer grid map) ---
            //
            // Added AFTER the fraction and the suspect flag above, which describe the dihedral
            // detector; these curves are a different detector with its own guards against noise
            // (hysteresis, a minimum length, one curve per bevel). See CurveNetwork.
            // What the cross field is constrained by: the features, or with align-only curves,
            // the features plus the curves.
            var constraintGraph = longCreaseLoops ?? result.features;
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

            // Motion guides: rings round a piece a rotation sweeps (a tyre, a rim, a knob), rows along
            // an extrusion, threads along a screw. See KinematicSurfaces.
            if (kinematicGuides && result.profile.integerGridMap)
            {
                var motionCell = result.topology.EdgeLengthForQuadCount(Mathf.Max(1, settings.targetQuadCount));
                KinematicSurfaces.Guides(result.topology, motionCell, kinematicGuideWeight,
                    out var motionDirections, out var motionWeights, out result.kinematicStats,
                    KinematicRegionsFor(result.modelType));
                if (result.kinematicStats.guided > 0)
                {
                    guideDirections ??= new Vector3[result.topology.vertexCount];
                    guideWeights ??= new float[result.topology.vertexCount];
                    for (var i = 0; i < motionWeights.Length; i++)
                    {
                        if (!(motionWeights[i] > guideWeights[i])) continue;
                        guideDirections[i] = motionDirections[i];
                        guideWeights[i] = motionWeights[i];
                    }
                }
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
            if ((result.profile.curvatureAdaptivity > 0f || result.paintCountRatio > 0f)
                && (!result.profile.integerGridMap || Extraction.IntegerGridMap.gradeSpacing))
            {
                onProgress?.Invoke("Sizing by curvature", 0f);
                result.scaleField = SizingField(result, target, result.profile.curvatureAdaptivity);
                scale = result.scaleField.meanSpacing;
            }

            var baseScale = scale;

            var extractionOptions = ExtractionOptions.Default;
            extractionOptions.features = result.features;
            extractionOptions.featurePositions = result.topology.positions;
            // Every coherent crease, cut or not: a vertex already on a cut crease is at distance
            // zero and stays, the creases left uncut get their nearest loop.
            if (Extraction.IntegerGridMap.creaseLoopSnap && longCreaseLoops != null)
                extractionOptions.creaseLoops = longCreaseLoops;
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
                // The quad layout lands short of the count its spacing asks for, by a fairly steady
                // amount, and a miss costs a second parametrisation and extraction: start where it
                // is expected to land. See layoutCountAtSpacing.
                if (layoutCountAtSpacing > 0f && layoutCountAtSpacing < 1f
                    && Extraction.IntegerGridMap.parametrization && Extraction.IntegerGridMap.parametrizationLayout)
                    scale *= Mathf.Sqrt(layoutCountAtSpacing);

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
                if (QuadReduction.logStats) Debug.Log($"[Native Remesher] count passes at {target}: {result.faceCountTrace}");
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
