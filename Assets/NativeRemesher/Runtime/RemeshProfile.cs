// Domain profiles.
//
// One solver, configured differently — not four pipelines. A profile picks which pre-stages run and
// how the shared stages are parameterised. Everything a profile can express is something the core
// already supports, so adding one is a data change rather than a new code path to test.
//
// The reason this is worth having rather than exposing every knob directly: the settings are not
// independent. Aggressive decimation only works if the input is decimated first (see
// RemeshTopology.maxUsefulSpacingMultiple), scan input needs that AND wants feature detection off
// because scanner noise produces thousands of spurious creases, and hard-surface input wants the
// opposite. Presenting those as separate checkboxes invites combinations that cannot work.

using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher
{
    public enum RemeshProfileKind
    {
        /// <summary>Balanced defaults. Game props and general content.</summary>
        GameAsset,
        /// <summary>Sharp features carry the shape: mechanical parts, chamfers, flat panels.</summary>
        HardSurface,
        /// <summary>Dense noisy scan input with no clean features.</summary>
        Photogrammetry,
        /// <summary>Smooth organic surfaces where edge flow matters more than features.</summary>
        Character
    }

    /// <summary>
    /// Which method turns the solved fields into quads. A list rather than a flag because the
    /// point is to compare approaches head to head and keep the one that measures best.
    /// </summary>
    public enum RemeshExtractor
    {
        /// <summary>
        /// Instant Meshes: classify each input edge on the integer lattice and collapse pairwise.
        /// The method this project ported first, and what ships today.
        /// </summary>
        /// <remarks>
        /// Its limit is structural rather than a tuning problem. Every edge is decided on its own,
        /// in a frame built between its two endpoints, so nothing ever requires the cells to agree
        /// across the surface. Measured on a torus whose field carries zero singularities and whose
        /// lattice is untorn: 366 irregular vertices where 0 are attainable.
        /// </remarks>
        Collapse,

        /// <summary>
        /// One global integer grid map: gauge every edge offset into a single frame per component,
        /// make each face's closure a constraint, repair the residual with a minimum-cost flow,
        /// then read one quad per lattice cell.
        /// </summary>
        /// <remarks>
        /// This is the family Exoside's engine is in — its own binary names integer u/v variables,
        /// pinned grid poles and a Cholesky factorisation — reached here by the published,
        /// permissively licensed route (QuadriFlow, Huang et al. 2018). A cell is a face by
        /// construction, so a vertex has four neighbours unless the field is singular there.
        /// Measured: the same torus comes out at 0 irregular vertices and 100 % quads.
        /// </remarks>
        IntegerGrid,
    }

    [System.Serializable]
    public struct RemeshProfile
    {
        public RemeshProfileKind kind;

        /// <summary>
        /// Decimate the input before solving, to a triangle budget of
        /// <see cref="decimateBudgetPerQuad"/> times the target quad count.
        /// </summary>
        /// <remarks>
        /// This is a quality setting, not a performance one. Output quality depends on the ratio of
        /// lattice spacing to input edge length, and past about 2.5x it falls off a cliff. Raising
        /// the input's edge length first keeps that ratio in band whatever the final face count,
        /// which is what makes a very low target produce good topology instead of mush.
        /// </remarks>
        public bool preDecimate;

        /// <summary>
        /// Triangle budget for the pre-decimation pass, as a multiple of the target quad count.
        /// </summary>
        /// <remarks>
        /// About 10 is right, and the quality envelope is what sets it.
        ///
        /// With T triangles over area A the average edge length is roughly sqrt(2.31 A / T), so at
        /// T = 10 per quad the ratio of lattice spacing to input edge length is sqrt(10 / 2.31) =
        /// 2.08 — the middle of the band where quad fraction holds at 85-98%, measured across the
        /// corpus. A budget of 4 gives 1.32, too fine for the lattice to coarsen onto: measured
        /// there, a 300-quad request came back as 126 faces and a cylinder came back as 13. Going
        /// much higher walks out of the band at the coarse end, which is what this stage exists to
        /// prevent, and past about 2.5 quality falls off a cliff rather than degrading.
        ///
        /// This used to cite a second, independent derivation from the coarsen-only ceiling, giving
        /// the same answer — RemeshTopology.MaxQuadCountForInput was A / (2 edge)^2 = T / 9.24, so
        /// a budget below 9.24 would see the request clamped. That agreement was a coincidence of
        /// two wrong numbers: the ceiling was hard-coded at a spacing of 2x while the measured floor
        /// is minUsefulSpacingMultiple = 1.5x, which the same file already used elsewhere. The
        /// ceiling is now A / (1.5 edge)^2 = T / 5.2, so reachability needs only 5.2 triangles per
        /// quad and no longer constrains this value at all. The quality band alone does.
        /// </remarks>
        public float decimateBudgetPerQuad;

        /// <summary>Dihedral angle above which an edge counts as a feature. 0 disables detection.</summary>
        public float featureAngleDegrees;

        public int orientationIterations;
        public int positionIterations;

        /// <summary>Merge leftover triangle pairs back into quads.</summary>
        public bool mergeTrianglePairs;

        /// <summary>
        /// How far, in lattice cells, a valence-2 output vertex may sit off the line through its two
        /// neighbours before cleanup keeps it instead of splicing it out.
        /// </summary>
        /// <remarks>
        /// The test exists to protect a genuine corner: splicing a vertex that sits well off that
        /// line moves the surface. But a valence-2 vertex is a defect in a quad mesh — it makes both
        /// faces through it one corner larger, which is where pentagons and hexagons come from — and
        /// the reference retopology of the scan asset has exactly zero of them against our 345.
        ///
        /// So this is per-profile, because the question it answers is per-profile. A profile with
        /// feature detection switched off is asserting that its input has no creases, and running a
        /// crease-protection test on such an input protects scan noise instead: measured on the
        /// reference asset, 936 keeps across four cleanup rounds on a profile with
        /// featureAngleDegrees = 0, so there was nothing there to protect.
        /// </remarks>
        public float cornerHeight;

        /// <summary>
        /// Sweeps per level of the multiresolution hierarchy, or a negative value to solve at the
        /// finest level only.
        /// </summary>
        /// <remarks>
        /// An int rather than a bool because RemeshProfile is a struct: a default-constructed one
        /// carries false for every bool, so a "useHierarchy" flag would silently default to the
        /// wrong thing for any caller that built a profile by hand. Zero here means unset and
        /// Validated fills in HierarchicalSolve.defaultLevelIterations, which is the reference's 6.
        ///
        /// Six is small on purpose. A coarse level's job is to put the next finer level in the
        /// right basin, not to converge; measurement showed sweeps past that buy nothing (60/50
        /// and 300/300 score within 1.3 points of each other on irregular vertices).
        /// </remarks>
        public int hierarchyLevelIterations;

        public bool useHierarchy => hierarchyLevelIterations > 0;

        /// <summary>
        /// Constrain the POSITION field so the lattice lands ON detected creases instead of merely
        /// running parallel to them. No effect where no creases are detected.
        /// </summary>
        /// <remarks>
        /// Inverted - the field is named "do not" - because RemeshProfile is a struct and a
        /// default-constructed one reads false for every bool. Every other bool here had to be set
        /// explicitly in each profile for that reason; this one carries the safe default in the
        /// false position instead, so a caller building a profile by hand gets the crease
        /// constraint rather than silently losing it.
        ///
        /// WHAT IT FIXES. Feature directions already reached the ORIENTATION field, so quads ran
        /// parallel to a panel edge - and nothing told the position field where the cell boundaries
        /// fall, so the edge loop sat beside the crease rather than on it. On the armour asset:
        /// 11,574 creases detected, 25.8 % with an output vertex on them. Detected, aligned, and
        /// missed. The constraint drops a crease vertex's lattice point from two degrees of freedom
        /// to one - it may slide ALONG the crease to keep spacing even, but not off it - and pins a
        /// junction outright, since where two creases meet there is no line left to slide along.
        /// </remarks>
        /// MEASURED, AND IT DID NOT WORK, so it is opt-in and false is the shipping value. On the
        /// armour asset it took creases kept from 25.8 %% to 21.7 %%. The reason is an ordering the
        /// theory did not account for: the constraint is applied after the anchoring snap, so it
        /// moves the representative OFF the lattice, and the next sweep reads a neighbour that is no
        /// longer at a lattice point and spends itself undoing the constraint. Making it work needs
        /// the crease to carry its own one-dimensional lattice with an origin shared along the
        /// polyline, which is a larger change than a projection. Kept, off, with the number.
        public bool snapLatticeToFeatures;

        /// <summary>
        /// Sweeps per level for the ORIENTATION field's coarse-to-fine solve. Zero and negative
        /// both mean OFF - comb the cross field at the finest level only, which is what ships.
        /// </summary>
        /// <remarks>
        /// OFF, and this time for a reason that is about the right column. The solve was deleted
        /// once on a quad-fraction sweep that could not see what it does; it is kept here, switched
        /// off, with the measurement that says so, because the previous deletion is why the same
        /// ground had to be covered twice.
        ///
        /// Separate from <see cref="hierarchyLevelIterations"/> because the two solves want
        /// different depths for unrelated reasons - the lattice cannot be represented on a level
        /// coarser than a cell, while a cross field has no length scale and rides any level.
        ///
        /// WHAT IT DOES, on the character at 5,215 (OrientationHierarchySweep):
        ///
        ///   sweeps   field sing+/-   irregular %   quad %   triangles
        ///   off        206 / 204        23.55      96.08       203
        ///   3          187 / 181        24.40      96.34       188
        ///   6          184 / 178        24.74      96.38       184
        ///   12         184 / 178        24.71      96.01       202
        ///
        /// The reach argument is CORRECT about the field and that is worth keeping: singularities
        /// fall 410 to 362, both signs together as a cancelling-pair theory predicts, saturating at
        /// six sweeps. It is simply not worth anything by the time extraction has finished.
        ///
        /// The per-stage census says why, and it is the useful part. Against the flat solve, the
        /// better field is still ahead after the two stages that read it directly - collapse 953 to
        /// 934, trace 872 to 843 - and then loses all of it downstream: regularise, triangle
        /// pairing and the n-gon split together add about 390 irregular vertices whatever they are
        /// handed. Collapse alone turns 362 field singularities into 934 clusters.
        ///
        /// So this measures something more useful than itself: EXTRACTION, not the field, is what
        /// bounds regularity. A 12 % better field moves the output by nothing, which means no
        /// amount of field work is the answer while collapse amplifies 2.5x.
        ///
        /// It also still regresses HardSurface - 27.29 % to 29.96 % irregular - even with
        /// RestrictConstraints now dropping crease directions that project to nothing on a coarse
        /// normal rather than pinning the coarse solve to them. That fix was necessary and is not
        /// sufficient, and the cause past it is not localised.
        /// </remarks>
        public int orientationHierarchyLevelIterations;

        public bool useOrientationHierarchy => orientationHierarchyLevelIterations > 0;

        /// <summary>
        /// How much coarser than a hierarchy level's own edges the lattice must be for the POSITION
        /// field to be solved on that level. See HierarchicalSolve.defaultPositionSpacingMultiple.
        /// </summary>
        public float positionSpacingMultiple;

        /// <summary>
        /// How far a collapse cluster may spread, in lattice cells. See
        /// LatticeCollapse.defaultMaxClusterSpread. Zero means the default.
        /// </summary>
        /// <remarks>
        /// Exposed so this can be swept rather than argued about. The default is 2.25 cells, which
        /// is more than one cell — so by construction a cluster is permitted to glue together
        /// surface belonging to two or three different cells into a single output vertex, and such a
        /// vertex has neighbours in more than four lattice directions. That is the same
        /// contested/vacant direction pair LatticeDirections reports, seen at its source.
        ///
        /// The constant is generous because tightening it overshoots the requested face count; the
        /// pipeline's spacing probe now compensates for that by coarsening the lattice, so the two
        /// can be compared at equal face count, which is what the sweep in LatticeAudit does.
        /// </remarks>
        public float clusterSpread;

        /// <summary>
        /// Split input edges the lattice cannot represent instead of discarding them. See
        /// <see cref="Extraction.EdgeSubdivide"/> and <see cref="ExtractionOptions.subdivideLongEdges"/>.
        /// </summary>
        public bool subdivideLongEdges;

        /// <summary>
        /// Re-tile the hexagon two triangles form with the quad between them.
        /// See <see cref="NativeRemesher.Extraction.TrianglePairing"/>.
        /// </summary>
        public bool pairTrianglesAcrossQuads;

        /// <summary>
        /// Longest chain of quads triangle pairing may re-tile across. 0 takes the pass default.
        /// </summary>
        public int trianglePairingMaxChainQuads;

        /// <summary>Rounds of triangle pairing. 0 takes the pass default.</summary>
        public int trianglePairingMaxRounds;

        /// <summary>
        /// Merge clusters closer than this many cells, repairing a cell the per-edge classification
        /// split in two. Negative takes the pass default; 0 disables it.
        /// </summary>
        public float mergeSplitCellsBelowCells;

        /// <summary>Record the per-stage irregular-vertex census. Diagnostic; costs a walk per stage.</summary>
        public bool recordCensus;

        /// <summary>
        /// How far a re-tiled quad may fold back, in degrees, when no convex tiling exists.
        /// </summary>
        /// <remarks>
        /// No initializer, because this is a struct: zero is therefore what a default-constructed
        /// profile carries, and zero means "refuse every fold", which is the behaviour this
        /// parameter was added to relax. That is the safe way round - a profile nobody configured
        /// behaves as the pass did before the parameter existed - but it does mean every profile
        /// built by <see cref="For"/> has to set it explicitly, and they all do.
        /// </remarks>
        public float trianglePairingMaxReflexDegrees;

        /// <summary>
        /// Lowest quad quality a re-tiling may produce. 0 or less takes the pass default.
        /// </summary>
        public float trianglePairingQualityThreshold;

        /// <summary>
        /// Rearrange the output vertices across the input surface after extraction.
        /// See <see cref="NativeRemesher.Extraction.SurfaceRelaxation"/>.
        /// </summary>
        /// <remarks>
        /// On for every profile, including the hard-surface one. Creases are protected inside the
        /// pass rather than by switching it off per profile, because the protection is geometric —
        /// it reads the input surface — and so it works on a profile with feature DETECTION off, of
        /// which there are two. An earlier version of this comment claimed reprojection alone made
        /// creases safe. It does not: the corpus caught a cube going from a perfect result to 17 %
        /// flipped normals. See SurfaceRelaxation.defaultFeatureAngleDegrees.
        /// </remarks>
        public bool relaxSurface;

        /// <summary>
        /// How strongly quad size follows curvature: 0 uniform, 1 the full chord-error law.
        /// See <see cref="ScaleField"/>.
        /// </summary>
        /// <remarks>
        /// Per-profile because the question is per-profile. A scan or a character is all curvature
        /// and grading is what lets a fingertip be resolved at all; a hard-surface part is mostly
        /// flat panels meeting at creases, where the curvature estimate is a spike at the crease and
        /// nearly zero everywhere else, so grading would put every quad on the chamfers and none on
        /// the faces. That case is already served by feature detection.
        /// </remarks>
        public float curvatureAdaptivity;

        /// <summary>Largest permitted ratio between the coarsest and finest spacing.</summary>
        public float curvatureMaxRatio;

        /// <summary>Largest permitted spacing ratio across one edge. See ScaleField.defaultGradation.</summary>
        public float curvatureGradation;

        /// <summary>Relaxation sweeps. Zero is equivalent to <see cref="relaxSurface"/> false.</summary>
        public int relaxIterations;

        /// <summary>
        /// How far each vertex moves toward its neighbours per sweep, in [0, 1]. Zero means the
        /// default. See <see cref="SurfaceRelaxation.defaultStrength"/>.
        /// </summary>
        public float relaxStrength;

        /// <summary>
        /// Which extractor runs. See <see cref="RemeshExtractor"/>; IntegerGridAudit is the A/B.
        /// </summary>
        public RemeshExtractor extractor;

        /// <summary>
        /// <see cref="extractor"/> as the boolean it started life as, so the callers written
        /// against it keep working. Not serialized — <see cref="extractor"/> is the stored field.
        /// </summary>
        public bool integerGridMap
        {
            get => extractor == RemeshExtractor.IntegerGrid;
            set => extractor = value ? RemeshExtractor.IntegerGrid : RemeshExtractor.Collapse;
        }

        public static RemeshProfile For(RemeshProfileKind kind)
        {
            switch (kind)
            {
                case RemeshProfileKind.HardSurface:
                    return new RemeshProfile
                    {
                        kind = kind,
                        preDecimate = true,
                        decimateBudgetPerQuad = 10f,
                        // Lower than the general default: chamfers and shallow bevels are exactly the
                        // features that matter here and they sit well under 30 degrees.
                        featureAngleDegrees = 20f,
                        orientationIterations = 200,
                        positionIterations = 300,
                        mergeTrianglePairs = true,
                        subdivideLongEdges = true,
                        pairTrianglesAcrossQuads = true,
                        // Set on every profile because this is a struct: an unset field reads as
                        // zero, and zero means "do not repair split cells" rather than "use the
                        // default". See LatticeCollapse.defaultMergeBelowCells.
                        mergeSplitCellsBelowCells = LatticeCollapse.defaultMergeBelowCells,
                        // Explicit, though it now equals the default, because the REASON is its
                        // own: on a mechanical part a folded re-tiling is most likely laid across a
                        // chamfer, turning two faces that meet at a feature edge into one face bent
                        // through it. That is the defect this profile exists to avoid, and it would
                        // be bought with a triangle that was doing no harm. If the shared default
                        // ever rises again, this must not follow it.
                        trianglePairingMaxReflexDegrees = 10f,
                        relaxSurface = true,
                        relaxIterations = SurfaceRelaxation.defaultIterations,
                        relaxStrength = SurfaceRelaxation.defaultStrength,
                        // GRADED, and this reverses the rule the other profiles still carry.
                        //
                        // "Grade only where feature detection is off" was written from two pieces of
                        // evidence. One was cube_subdivided, where grading is a disaster - a cube's
                        // curvature is a spike on the edges and zero on the panels, so grading spends
                        // the whole budget resolving what the feature constraints were already going
                        // to hold. That part stands. The other was the reference asset, believed to
                        // be an organic scan, where grading scored half a point on flagged faces.
                        //
                        // That asset is a soldier in body armour - a plate carrier, webbing, a belt,
                        // a collar - and the metrics it was judged on could not see a rounded crease
                        // at all. Re-measured with one that can, at matched face count, creases with
                        // an output vertex on them:
                        //
                        //     uniform  28.2 %      0.50  36.2 %
                        //     0.25     32.0 %      0.75  40.6 %
                        //
                        // and the renders say the same thing far more plainly than the numbers do:
                        // uniform loses the armour entirely, 0.50 gives back the plate boundary, the
                        // collar rim and the belt. 0.75 keeps more creases and starts to tear, so
                        // 0.50 is the trade taken. The competitor ships the equivalent of 0.50 and
                        // leaves it on for everything.
                        //
                        // The cube and this asset genuinely disagree, and the difference is what the
                        // curvature estimate is bimodal ABOUT: on a cube it separates edge from
                        // panel, which the features already cover, and here it separates armour from
                        // limb, which nothing else does.
                        curvatureAdaptivity = 0.5f,
                        // Corners are the whole point on this path, so the crease-protection test
                        // runs at its default strictness.
                        cornerHeight = GraphCleanup.defaultCornerHeight
                    };

                case RemeshProfileKind.Photogrammetry:
                    return new RemeshProfile
                    {
                        kind = kind,
                        preDecimate = true,
                        // Scans arrive far denser than the solver wants, and decimating also
                        // suppresses noise, so this stage does double duty on the scan path. The
                        // budget cannot be tightened below the general value to lean on that
                        // harder, though: under about 9.24 triangles per quad the coarsen-only
                        // ceiling clamps the request and the result silently comes back too coarse.
                        decimateBudgetPerQuad = 10f,
                        // OFF. Dihedral detection on scan data finds thousands of spurious creases —
                        // scanner noise is high-frequency, which is exactly what the test triggers on.
                        // Constraining the field to noise is worse than not constraining it at all.
                        featureAngleDegrees = 0f,
                        // Deliberately low, and this is the one profile where that is measured
                        // rather than inherited.
                        //
                        // Raising the sweep budget is worth a great deal on clean, structured
                        // input: on the procedural corpus 200/300 beats 50/50 on 22 of 30 cases,
                        // by up to seven points of quad fraction on a torus. On the 990,625
                        // triangle scan it is worth nothing at all.
                        //
                        //     ccnb.obj, Photogrammetry, quad fraction / wall clock
                        //       2,000 quads   50/50 -> 0.713 in  7.7s   200/300 -> 0.704 in 12.9s
                        //       8,000 quads   50/50 -> 0.768 in  9.1s   200/300 -> 0.763 in 18.5s
                        //
                        // Flat to slightly negative for double the time, and the reason is now known
                        // rather than open. Those measurements were taken while pre-decimation was
                        // silently applying zero collapses (see MeshMath), so the field was solving
                        // on 750k vertices at 10.9x its usable lattice spacing and no sweep count
                        // could have helped. Re-measured after that fix, on the reference asset at
                        // 5,474 faces, the conclusion survives anyway but for a different reason:
                        //
                        //     Photogrammetry  60/50   -> quad fraction 0.7540
                        //     Character      300/300  -> quad fraction 0.7537
                        //
                        // Identical. At 27,368 post-decimation vertices the field really is
                        // converged, so the extra sweeps buy nothing. What limits this path is the
                        // greedy extraction's lack of global consistency — 1,597 valence-3 vertices
                        // against the reference's 142 — which is what QuadriFlow's integer
                        // constraints exist to fix, and not something a sweep budget can reach.
                        orientationIterations = 60,
                        positionIterations = 50,
                        mergeTrianglePairs = true,
                        subdivideLongEdges = true,
                        pairTrianglesAcrossQuads = true,
                        // Set on every profile because this is a struct: an unset field reads as
                        // zero, and zero means "do not repair split cells" rather than "use the
                        // default". See LatticeCollapse.defaultMergeBelowCells.
                        mergeSplitCellsBelowCells = LatticeCollapse.defaultMergeBelowCells,
                        trianglePairingMaxReflexDegrees =
                            TrianglePairing.defaultMaxReflexDegrees,
                        relaxSurface = true,
                        relaxIterations = SurfaceRelaxation.defaultIterations,
                        relaxStrength = SurfaceRelaxation.defaultStrength,
                        curvatureAdaptivity = ScaleField.defaultAdaptivity,
                        curvatureMaxRatio = ScaleField.defaultMaxRatio,
                        curvatureGradation = ScaleField.defaultGradation,
                        // Splice every valence-2 vertex. With featureAngleDegrees = 0 this profile
                        // has asserted its input has no creases, so the crease-protection test can
                        // only be protecting scan noise — measured, 936 keeps on the reference asset
                        // and 345 valence-2 vertices surviving into output the reference has none of.
                        cornerHeight = GraphCleanup.spliceAlways
                    };

                case RemeshProfileKind.Character:
                    return new RemeshProfile
                    {
                        kind = kind,
                        preDecimate = true,
                        decimateBudgetPerQuad = 10f,
                        // Organic surfaces have no true creases; a threshold would only pick up
                        // sculpting noise. Edge flow comes from the field, not from constraints.
                        featureAngleDegrees = 0f,
                        // More sweeps: loop quality on a smooth surface is entirely down to how well
                        // the cross field is combed, and there are no features to anchor it.
                        orientationIterations = 300,
                        positionIterations = 300,
                        mergeTrianglePairs = true,
                        subdivideLongEdges = true,
                        pairTrianglesAcrossQuads = true,
                        // Set on every profile because this is a struct: an unset field reads as
                        // zero, and zero means "do not repair split cells" rather than "use the
                        // default". See LatticeCollapse.defaultMergeBelowCells.
                        mergeSplitCellsBelowCells = LatticeCollapse.defaultMergeBelowCells,
                        trianglePairingMaxReflexDegrees =
                            TrianglePairing.defaultMaxReflexDegrees,
                        relaxSurface = true,
                        relaxIterations = SurfaceRelaxation.defaultIterations,
                        relaxStrength = SurfaceRelaxation.defaultStrength,
                        curvatureAdaptivity = ScaleField.defaultAdaptivity,
                        curvatureMaxRatio = ScaleField.defaultMaxRatio,
                        curvatureGradation = ScaleField.defaultGradation,
                        // As Photogrammetry: no creases declared, so nothing for the test to guard.
                        cornerHeight = GraphCleanup.spliceAlways
                    };

                default:
                    return new RemeshProfile
                    {
                        kind = RemeshProfileKind.GameAsset,
                        preDecimate = true,
                        decimateBudgetPerQuad = 10f,
                        featureAngleDegrees = 30f,
                        orientationIterations = 200,
                        positionIterations = 300,
                        mergeTrianglePairs = true,
                        subdivideLongEdges = true,
                        pairTrianglesAcrossQuads = true,
                        // Set on every profile because this is a struct: an unset field reads as
                        // zero, and zero means "do not repair split cells" rather than "use the
                        // default". See LatticeCollapse.defaultMergeBelowCells.
                        mergeSplitCellsBelowCells = LatticeCollapse.defaultMergeBelowCells,
                        trianglePairingMaxReflexDegrees =
                            TrianglePairing.defaultMaxReflexDegrees,
                        relaxSurface = true,
                        relaxIterations = SurfaceRelaxation.defaultIterations,
                        relaxStrength = SurfaceRelaxation.defaultStrength,
                        // UNIFORM, and the corpus states the reason more plainly than any
                        // argument could. Grading against uniform on this profile, relaxation off
                        // in both, cube_subdivided at 2,000 quads:
                        // 94.2 % quads / 0.010 Hausdorff / 23.2 % irregular, against 99.5 % / 0.000
                        // / 1.7 %. Uniform gets that mesh EXACTLY right and grading takes it apart.
                        // Capped cylinder agrees (96.5 % / 27.8 % irregular against 98.4 % / 21.3 %)
                        // and every smooth shape in the corpus is indifferent, because a surface of
                        // constant curvature grades to a spacing spread of 1.01 whatever is asked.
                        //
                        // The rule the corpus draws, and the one to apply to any new profile: GRADE
                        // ONLY WHERE FEATURE DETECTION IS OFF. A crease makes the curvature estimate
                        // bimodal - a spike along the edge, near zero across the panel - so grading
                        // spends the budget on the edges, and it is spending it to resolve exactly
                        // what the feature constraints were already going to hold. The two
                        // mechanisms want the same quads and fight over them. Where creases are
                        // declared absent (scan profiles, featureAngleDegrees = 0), curvature is
                        // the only instrument there is and grading is worth a lot: on the character
                        // it took the deepest unrepresented hole from 1.000 % to 0.448 %.
                        //
                        // HARD SURFACE IS THE MEASURED EXCEPTION to that rule, and it is worth
                        // knowing which way round it goes. The rule holds wherever the curvature
                        // estimate is bimodal about the same thing the creases are - edge against
                        // panel, as on the cube. On the armour asset it is bimodal about armour
                        // against limb, which nothing else covers, so grading pays there in spite
                        // of feature detection being on. That profile's own curvatureAdaptivity
                        // carries the numbers.
                        curvatureAdaptivity = 0f,
                        curvatureMaxRatio = ScaleField.defaultMaxRatio,
                        curvatureGradation = ScaleField.defaultGradation,
                        cornerHeight = GraphCleanup.defaultCornerHeight
                    };
            }
        }

        public static RemeshProfile Default => For(RemeshProfileKind.GameAsset);

        public RemeshProfile Validated()
        {
            var copy = this;
            // Floor is the quality envelope, not a taste choice: below about 9.5 triangles per quad
            // the lattice spacing drops under 2x the input edge length, which is too fine for it to
            // coarsen onto. Reachability is a separate and now looser constraint (5.2 per quad since
            // the ceiling was corrected) and no longer the binding one — see decimateBudgetPerQuad.
            copy.decimateBudgetPerQuad = Mathf.Clamp(decimateBudgetPerQuad, 9.5f, 64f);
            copy.featureAngleDegrees = featureAngleDegrees <= 0f
                ? 0f
                : Mathf.Clamp(featureAngleDegrees, 1f, 179f);
            copy.orientationIterations = Mathf.Clamp(orientationIterations, 0, 5000);
            copy.positionIterations = Mathf.Clamp(positionIterations, 1, 5000);
            // Zero is what a default-constructed profile carries, and it would keep every valence-2
            // vertex rather than splicing none — the opposite of a safe default. Infinity is allowed
            // through deliberately: it is GraphCleanup.spliceAlways.
            copy.cornerHeight = cornerHeight > 0f ? cornerHeight : GraphCleanup.defaultCornerHeight;
            copy.positionSpacingMultiple = positionSpacingMultiple > 0f
                ? positionSpacingMultiple
                : HierarchicalSolve.defaultPositionSpacingMultiple;
            copy.hierarchyLevelIterations = hierarchyLevelIterations == 0
                ? HierarchicalSolve.defaultLevelIterations
                : hierarchyLevelIterations;
            // Unlike every other unset field here, zero does NOT take a default: it means off, and
            // off is what ships. A default-constructed profile carrying zero therefore gets the
            // shipping behaviour rather than a solve that measured worse. See the field's remarks.
            copy.orientationHierarchyLevelIterations = orientationHierarchyLevelIterations == 0
                ? -1
                : orientationHierarchyLevelIterations;
            copy.clusterSpread = clusterSpread > 0f
                ? clusterSpread
                : LatticeCollapse.defaultMaxClusterSpread;
            // Zero means unset for both, for the same reason cornerHeight does: a
            // default-constructed profile is all zeroes, and a zero here would silently disable the
            // pass rather than take its default. Turning relaxation off is `relaxSurface = false`.
            copy.relaxIterations = relaxIterations > 0
                ? relaxIterations
                : SurfaceRelaxation.defaultIterations;
            copy.relaxStrength = relaxStrength > 0f
                ? Mathf.Min(relaxStrength, 1f)
                : SurfaceRelaxation.defaultStrength;
            // NOT defaulted when zero, unlike the fields above. Zero here means uniform sizing,
            // which is a setting a caller may legitimately want and which hard surface uses; giving
            // it the default would make uniform unreachable.
            copy.curvatureAdaptivity = Mathf.Clamp01(curvatureAdaptivity);
            copy.curvatureMaxRatio = curvatureMaxRatio > 1f
                ? curvatureMaxRatio
                : ScaleField.defaultMaxRatio;
            copy.curvatureGradation = curvatureGradation > 1f
                ? curvatureGradation
                : ScaleField.defaultGradation;
            return copy;
        }

        /// <summary>Folds the profile's shared-stage settings into a settings struct.</summary>
        public RemeshSettings ApplyTo(RemeshSettings settings)
        {
            settings.orientationIterations = orientationIterations;
            settings.positionIterations = positionIterations;
            return settings;
        }

        public string Describe()
        {
            var features = featureAngleDegrees > 0f ? $"features @{featureAngleDegrees:F0}deg" : "no features";
            var decimate = preDecimate ? $"pre-decimate {decimateBudgetPerQuad:F1} tris/quad" : "no pre-decimate";
            return $"{kind}: {decimate}, {features}, " +
                   $"{orientationIterations}/{positionIterations} sweeps";
        }
    }
}
