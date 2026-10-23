// Native Remesher for Unity — Stage 5: extraction.
//
// After stage 4, many input vertices have been pulled onto the same lattice point. Each distinct
// lattice point becomes one output vertex, and the quads are recovered as the faces of the graph
// those points form.
//
// This type is now a facade. The work is five passes in Runtime/Extraction/, each of which fixes
// something the single monolithic implementation got wrong:
//
//   LatticeCollapse  A/B/C  classify every edge on the INTEGER lattice (collapse / one-step edge /
//                           discard) instead of by Euclidean distance, then apply collapses
//                           cheapest-first with validation instead of unconditionally
//   GraphCleanup     D      remove dangling and redundant vertices so the graph is traceable
//   FaceTracer       E/F    degree-ordered passes with 4 before 3, and non-destructive walks
//   QuadRegularizer  G      merge leftover triangle pairs back into quads
//   SurfaceRelaxation I     rearrange the output vertices across the input surface, which is what
//                           closed the shading gap - see Benchmarks/shading-tail.md
//
// See Benchmarks/README.md for the measured before/after and the reference diff those changes came
// from.

using System;
using System.Collections.Generic;
using NativeRemesher.Extraction;
using UnityEngine;
using UnityEngine.Rendering;

namespace NativeRemesher
{
    public sealed class QuadExtractionResult
    {
        /// <summary>A copy sharing every field (arrays and the face list included); the caller replaces what it changes.</summary>
        public QuadExtractionResult ShallowCopy() => (QuadExtractionResult)MemberwiseClone();

        /// <summary>Set when the quads were traced off a parametrisation (see ParametrizationTrace).</summary>
        public Extraction.ParametrizationTraceStats parametrizationTraceStats;

        /// <summary>Output vertex positions — one per lattice point.</summary>
        public Vector3[] positions;

        /// <summary>
        /// Irregular-vertex count after each stage, when
        /// <see cref="ExtractionOptions.recordCensus"/> is on.
        /// </summary>
        /// <remarks>
        /// WHY THIS EXISTS. The output carries about three times as many irregular vertices as the
        /// field has singularities - 427 in, roughly 1,300 out - and "extraction amplifies it" is
        /// not a diagnosis, it is nine passes with no number between them. Every previous attempt to
        /// place the fault went wrong precisely here: the tracer was cleared on one probe, the
        /// collapse blamed on another, and neither was measured at the same point in the same run.
        ///
        /// A stage that adds irregularity is visible in this list and nowhere else.
        /// </remarks>
        public List<StageCensus> census;

        /// <summary>
        /// Per-output-vertex surface normal, area-averaged over the input vertices that collapsed
        /// into each lattice point.
        /// </summary>
        /// <remarks>
        /// These describe the *input* surface at each output vertex. That makes them the right
        /// reference for deciding which way the output should face — they are the only record of
        /// where "outside" was — and the wrong thing to shade with, because they describe detail
        /// the output geometry no longer has. See <see cref="shadingNormals"/>, and the reasoning
        /// in NormalRecalculator for why the opposite claim used to be written here.
        /// </remarks>
        public Vector3[] normals;

        /// <summary>
        /// Per-output-vertex normal computed from the output faces, angle-weighted. This is what
        /// goes on the mesh.
        /// </summary>
        public Vector3[] shadingNormals;

        /// <summary>
        /// Output vertices the quad layout laid on one of its grid lines (IntegerGridMap
        /// .relaxAlongLayoutLines), or null. Later passes that smooth leave them where they are.
        /// </summary>
        [NonSerialized] public bool[] onLayoutLine;

        /// <summary>
        /// Per-output-vertex UV, sampled from the source surface, or null when the source had none.
        /// </summary>
        /// <remarks>
        /// Without this the output cannot be textured at all. On photogrammetry input that discards
        /// the entire value of the scan: the geometry is a rough approximation of the shape and all
        /// the detail a viewer actually recognises lives in the texture.
        /// </remarks>
        public Vector2[] uvs;

        /// <summary>
        /// Per-face-corner UV, parallel to <see cref="faces"/>, every corner of a face taken from
        /// one source UV chart. Null when the source had no UVs. Preferred over <see cref="uvs"/>
        /// wherever both exist -- see <see cref="AttributeTransfer.SampleFaceUvs"/> for why one UV
        /// per vertex smears the texture at every seam.
        /// </summary>
        public Vector2[][] faceUvs;

        /// <summary>
        /// Per face, its texels per unit in a baked atlas as a multiple of the atlas's own (a part
        /// barely seen gets fewer, PartBake). Null: the same for every face.
        /// </summary>
        public float[] faceTexelScale;

        /// <summary>
        /// Per face corner, the tangent a baked normal map was made against (TextureBake.Tangents);
        /// null when there is none. ToMesh writes them as the mesh's tangents.
        /// </summary>
        public Vector4[][] faceTangents;

        /// <summary>
        /// Per face corner, the shading normal a baked normal map was made against, split at hard
        /// edges (TextureBake.CornerNormals); null when the vertices' shading normals are used.
        /// ToMesh writes them as the mesh's normals.
        /// </summary>
        public Vector3[][] faceNormals;

        /// <summary>
        /// Per face, the input material (submesh) it lies on, when materials were kept (see
        /// <see cref="RemeshSettings.materialBorders"/>); null otherwise. <see cref="materialSlots"/>
        /// is the number of submeshes the mesh gets then.
        /// </summary>
        public int[] faceMaterials;
        public int materialSlots;

        /// <summary>Extracted faces, each holding 3 or more vertex indices. Mostly quads.</summary>
        public List<int[]> faces = new List<int[]>();

        /// <summary>Wall time per extraction stage, integer grid path only; empty otherwise.</summary>
        public string stageTimes = "";

        public int quadCount;
        public int triangleCount;
        public int ngonCount;

        public int faceCount => faces.Count;
        public float quadFraction => faces.Count > 0 ? quadCount / (float)faces.Count : 0f;

        /// <summary>Per-pass diagnostics. Null only if extraction threw partway through.</summary>
        public SubdivideStats subdivideStats;
        public CollapseStats collapseStats;
        public DirectionStats directionStats;
        public CleanupStats cleanupStats;
        public TraceStats traceStats;
        public HoleFillStats holeFillStats;
        public RegularizeStats regularizeStats;

        /// <summary>Hexagon re-tiling of triangles separated by one quad. Null when off.</summary>
        public TrianglePairingStats trianglePairingStats;

        /// <summary>
        /// The second pairing run, after the n-gon splitter. See the call site for why there is one.
        /// </summary>
        public TrianglePairingStats trianglePairingAfterNgonStats;
        public NgonSplitStats ngonSplitStats;
        public RepairStats repairStats;
        public OrientStats orientStats;

        /// <summary>Smooth-and-reproject relaxation of the output vertices. Null when off.</summary>
        public RelaxStats relaxStats;

        /// <summary>Feature snapping results, or null when that pass did not run.</summary>
        public SnapStats snapStats;

        /// <summary>The crease-loop snap, when it ran.</summary>
        public CreaseLoopSnapStats creaseLoopStats;

        /// <summary>The integer grid map's counters. Null when the collapse path ran instead.</summary>
        public IntegerGridStats integerGridStats;

        public override string ToString()
        {
            return $"{positions.Length:N0} vertices, {faceCount:N0} faces " +
                   $"({quadCount:N0} quads = {quadFraction:P0}, " +
                   $"{triangleCount:N0} tris, {ngonCount:N0} n-gons)";
        }

        public string Diagnostics()
        {
            // holeFill is included because leaving it out made a real question unanswerable: the
            // tracer caps faces at 8 corners, so a 44-sided face in the output can only have come
            // from this stage, and the only line that said so was in a different probe.
            return (integerGridStats != null ? $"{integerGridStats}\n" : "") +
                   (stageTimes.Length > 0 ? $"stages ms: {stageTimes}\n" : "") +
                   (subdivideStats != null ? $"{subdivideStats}\n" : "") +
                   $"collapse: {collapseStats}\ndirs:     {directionStats}\n" +
                   $"cleanup:  {cleanupStats}\n" +
                   $"trace:    {traceStats}\nholeFill: {holeFillStats}\n" +
                   $"regular:  {regularizeStats}\n" +
                   (trianglePairingStats != null ? $"triPair:  {trianglePairingStats}\n" : "") +
                   $"ngonSplit: {ngonSplitStats}\n" +
                   $"repair:   {repairStats}\norient:   {orientStats}" +
                   (relaxStats != null ? $"\nrelax:    {relaxStats}" : "") +
                   (snapStats != null ? $"\nsnap:     {snapStats}" : "");
        }
    }

    /// <summary>One stage's irregular-vertex count. See <see cref="QuadExtractionResult.census"/>.</summary>
    public sealed class StageCensus
    {
        public string stage;

        /// <summary>Graph vertices alive, or faces, depending on what the stage produces.</summary>
        public int items;

        /// <summary>Vertices not on a boundary, which are the only ones a valence means anything for.</summary>
        public int interior;

        public int irregular;

        public int valence3;
        public int valence5;
        public int valence6Plus;

        public float irregularFraction => interior > 0 ? irregular / (float)interior : 0f;

        public override string ToString()
        {
            return $"{stage,-26} items={items,7:N0} interior={interior,7:N0} " +
                   $"irregular={irregular,6:N0} ({irregularFraction:P2})" +
                   $"  [3:{valence3} 5:{valence5} 6+:{valence6Plus}]";
        }
    }

    public sealed class ExtractionOptions
    {
        /// <summary>A shallow copy, for a caller that extracts the same settings off another mesh.</summary>
        public ExtractionOptions Clone() => (ExtractionOptions)MemberwiseClone();

        /// <summary>
        /// Positions <see cref="creaseLoops"/> is indexed by, when that is not the mesh being
        /// extracted (the parametrisation extracts off the decimated mesh while the creases were
        /// found on the refined one). Null means <see cref="featurePositions"/>.
        /// </summary>
        public Vector3[] creaseLoopPositions;

        /// <summary>
        /// Record the irregular-vertex count after every stage into
        /// <see cref="QuadExtractionResult.census"/>. Off by default: it walks the whole face set
        /// once per stage.
        /// </summary>
        public bool recordCensus;

        /// <summary>Cluster spread backstop, in lattice cells. See LatticeCollapse.</summary>
        public float maxClusterSpread = LatticeCollapse.defaultMaxClusterSpread;

        /// <summary>
        /// Merge two clusters whose lattice points are closer than this many cells: one cell that
        /// the per-edge classification split in two. 0 disables it.
        /// </summary>
        public float mergeSplitCellsBelowCells = LatticeCollapse.defaultMergeBelowCells;

        /// <summary>
        /// Split input edges spanning two or more lattice cells rather than discarding them.
        /// See <see cref="EdgeSubdivide"/>.
        /// </summary>
        /// <remarks>
        /// Measured, then switched on for every profile. On the user's two real assets, at the face
        /// counts their competitors were run at:
        ///
        /// <code>
        ///                             irregular      quadFrac   tris  maxVal  v1   hausdorff
        ///   character @5,474   off    2278 (44.1%)   0.8773     671   13      11   0.01061
        ///                      ON     1385 (25.2%)   0.8853     668   10       3   0.00949
        ///   character @12,451  off    5184 (42.9%)   0.8814    1515   15      21   0.00966
        ///                      ON     2773 (21.9%)   0.9016    1308    8      12   0.01025
        ///   ccnb @9,451        off    3989 (44.5%)   0.8952     944   25      75   0.02200
        ///                      ON     1944 (21.2%)   0.9280     663   13      26   0.01752
        /// </code>
        ///
        /// Every column improves on every asset and profile, and irregularity roughly halves. The
        /// corpus case `cylinder_capped` also drops worst-case deviation 34-fold (0.03325 to
        /// 0.00099), which retires the one regression the hierarchy was recorded as costing.
        ///
        /// It is still a flag because <see cref="EdgeSubdivide"/> can diverge — see the guard there
        /// and `cube_subdiv`, where it abandons and returns the input untouched.
        /// </remarks>
        public bool subdivideLongEdges = true;

        /// <summary>
        /// Extract through the integer grid map — QuadriFlow's ComputeIndexMap — instead of the
        /// greedy per-edge collapse. See <see cref="IntegerGridMap"/>.
        /// </summary>
        /// <remarks>
        /// The collapse classifies every input edge on its own, in a frame built between its two
        /// endpoints, so nothing in it ever asks the cells to line up globally; the measured result
        /// is a torus with a perfect field and an untorn lattice still coming out 283 vertices
        /// irregular where 0 are needed. The grid map makes the per-face closure an explicit
        /// constraint, repairs it with a min-cost flow, and reads quads off cells, so a vertex has
        /// four neighbours wherever the field is regular. Off by default until measured.
        /// </remarks>
        public bool integerGridMap;

        /// <summary>Valence-2 corner threshold, in lattice cells. See GraphCleanup.</summary>
        public float cornerHeight = GraphCleanup.defaultCornerHeight;

        /// <summary>
        /// Repair the neighbour rings towards one claimant per lattice direction — remove the
        /// weaker of two edges contesting a direction at both ends, add the edge a vacant
        /// direction asks for when the cluster across the gap asks for the matching one.
        /// </summary>
        /// <remarks>
        /// OFF, because its own diagnostic refuted the hypothesis it was built on, and the corpus
        /// A/B agrees. The premise was that a contested-and-vacant direction pair is a bookkeeping
        /// artefact of classifying input edges before clustering them. Measured, it is not: of
        /// 2,180 vacant directions on the reference asset, 819 have a cluster at a plausible
        /// distance that is more than 30 degrees off the lattice axis and 618 have nothing there at
        /// all, and refitting each frame to its own ring — the control for "maybe the frame is
        /// wrong, not the lattice" — moves the histogram by 13 vertices out of 4,957. The lattice
        /// really is sheared.
        ///
        /// So the repair can only reach 82 edges of 2,180 vacancies, and across the corpus it
        /// moves quad fraction up on 14 rows of 22 and irregular-vertex fraction the wrong way on
        /// 14 of 22 — a wash, negative on the metric it exists for. The measurement stays on
        /// because it is what identified where the real fault is.
        /// </remarks>
        public bool repairLatticeDirections = false;

        /// <summary>Merge adjacent triangle pairs back into quads.</summary>
        public bool mergeTrianglePairs = true;

        /// <summary>Minimum quality score for a triangle-pair merge to be accepted.</summary>
        public float mergeQualityThreshold = QuadRegularizer.defaultQualityThreshold;

        /// <summary>
        /// Re-tile the hexagon formed by two triangles separated by a single quad.
        /// </summary>
        /// <remarks>
        /// Reaches the triangles the pair merger structurally cannot: it needs the two triangles to
        /// share an edge, and on the real assets 80 % of survivors are surrounded entirely by quads.
        /// </remarks>
        public bool pairTrianglesAcrossQuads = true;

        public float trianglePairingQualityThreshold = TrianglePairing.defaultQualityThreshold;

        /// <summary>Longest chain of quads allowed between the two triangles.</summary>
        public int trianglePairingMaxChainQuads = TrianglePairing.defaultMaxChainQuads;

        /// <summary>Rounds of triangle pairing to run.</summary>
        public int trianglePairingMaxRounds = TrianglePairing.defaultMaxRounds;

        /// <summary>
        /// How far a re-tiled quad may fold back, in degrees, when no convex tiling exists.
        /// </summary>
        public float trianglePairingMaxReflexDegrees = TrianglePairing.defaultMaxReflexDegrees;

        /// <summary>
        /// Rearrange the output vertices across the input surface once the faces are final.
        /// </summary>
        /// <remarks>
        /// This is the pass that closes the shading gap; see
        /// <see cref="SurfaceRelaxation"/> for the measurement that identified it. It needs the
        /// input surface to project back onto, so it is skipped when
        /// <see cref="ExtractionOptions.surface"/> is null rather than degrading to a plain
        /// Laplacian smooth — unprojected smoothing shrinks the mesh, and a silently shrunk mesh is
        /// a worse outcome than a rough one.
        /// </remarks>
        public bool relaxSurface = true;

        public int relaxIterations = SurfaceRelaxation.defaultIterations;
        public float relaxStrength = SurfaceRelaxation.defaultStrength;
        public float relaxMaxProjection = SurfaceRelaxation.defaultMaxProjection;

        /// <summary>
        /// How far a vertex may drift from where extraction put it, in lattice cells.
        /// See <see cref="SurfaceRelaxation.defaultMaxDrift"/> - this is what stops relaxation
        /// walking a fingertip off the end of a finger.
        /// </summary>
        public float relaxMaxDrift = SurfaceRelaxation.defaultMaxDrift;

        /// <summary>
        /// Crease angle relaxation must not smooth across, or 0 to let it smooth across anything.
        /// The pipeline sets this from the profile's own <c>featureAngleDegrees</c>.
        /// </summary>
        public float relaxFeatureAngleDegrees = SurfaceRelaxation.defaultFeatureAngleDegrees;

        /// <summary>
        /// The input surface to project relaxed vertices back onto. Built by the pipeline from the
        /// same arrays the lattice was solved on, so relaxation cannot pull a vertex toward detail
        /// the solver never saw.
        /// </summary>
        public SpatialGrid surface;

        /// <summary>
        /// Detected sharp features, or null. Extraction uses these to avoid averaging crease
        /// vertices with off-crease ones, which is what rounds a hard edge off.
        /// </summary>
        public FeatureGraph features;

        /// <summary>
        /// Positions the <see cref="features"/> graph is indexed by — the solved topology's
        /// vertices, not the output's. Required by the snapping pass, which has to know where a
        /// crease actually is and not merely that a vertex is on one.
        /// </summary>
        public Vector3[] featurePositions;

        /// <summary>Pull output vertices onto nearby input creases once everything else is final.</summary>
        public bool snapToFeatures = true;

        /// <summary>
        /// The quad layout's grid lines as segment end points (0-1, 2-3, ...), or null. Output
        /// vertices on them slide along them in relaxation. See IntegerGridMap.relaxAlongLayoutLines.
        /// </summary>
        public Vector3[] layoutLines;

        /// <summary>
        /// Creases (indexed by <see cref="featurePositions"/>) to slide the nearest parallel edge
        /// loop onto after relaxation, or null. See CreaseLoopSnap.
        /// </summary>
        public FeatureGraph creaseLoops;

        /// <summary>See <see cref="FeatureSnap.defaultMaxSnapCells"/>.</summary>
        public float snapMaxCells = FeatureSnap.defaultMaxSnapCells;

        /// <summary>See <see cref="FeatureSnap.defaultMinEdgeCells"/>.</summary>
        public float snapMinEdgeCells = FeatureSnap.defaultMinEdgeCells;

        public static ExtractionOptions Default => new ExtractionOptions();
    }

    public static class QuadExtractor
    {
        /// <param name="mergeThreshold">
        /// IGNORED, and kept only so existing callers compile. This used to be the Euclidean
        /// distance below which two lattice points were merged, expressed as a fraction of the quad
        /// size. That decision is now exact: two vertices merge when their integer lattice
        /// coordinates coincide, which needs no tolerance. The parameter cannot be given a sensible
        /// meaning any more, so it is not silently reinterpreted as something else.
        /// </param>
        public static QuadExtractionResult Extract(RemeshTopology topology, OrientationField orientation,
            PositionField position, float mergeThreshold = 0.45f)
        {
            return Extract(topology, orientation, position, ExtractionOptions.Default);
        }

        public static QuadExtractionResult Extract(RemeshTopology topology, OrientationField orientation,
            PositionField position, ExtractionOptions options)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            if (orientation == null) throw new ArgumentNullException(nameof(orientation));
            if (position == null) throw new ArgumentNullException(nameof(position));
            options ??= ExtractionOptions.Default;

            var result = new QuadExtractionResult();

            var features = options.features;
            if (options.integerGridMap)
                return ExtractIntegerGrid(topology, orientation, position, options, result, features);

            // A0 — split the input edges the lattice cannot represent, so pass A has nothing left
            // to discard. See EdgeSubdivide: a discarded edge is connectivity that no later pass
            // can rebuild, and it is the root of the hole-filler and n-gon-splitter defects.
            if (options.subdivideLongEdges)
            {
                EdgeSubdivide.Run(topology, orientation, position, features,
                    out topology, out orientation, out position, out features,
                    out var subdivideStats);
                result.subdivideStats = subdivideStats;
            }

            // A/B/C
            var graph = LatticeCollapse.Build(topology, orientation, position,
                out var collapseStats, options.maxClusterSpread, features,
                options.mergeSplitCellsBelowCells);
            result.collapseStats = collapseStats;

            // The census, if asked for. Every entry costs one walk of the current face set, which
            // is why it is off by default and why it is worth every bit of that when it is on.
            var census = options.recordCensus ? result.census = new List<StageCensus>() : null;
            Census(census, "collapse (graph)", graph);

            // C2 — read each neighbour ring in its own lattice frame, and count how many of the
            // four directions are actually occupied. This is a measurement, not a stage: the
            // repair it can also perform is off by default, and the note on the option says why.
            //
            // It is here rather than in the benchmark because it answers the question the output
            // valence histogram cannot. Extraction sits between the field and the histogram, so a
            // 44%-irregular output is either a field that really is that singular or an extractor
            // manufacturing irregularity out of a sound one, and those have opposite fixes.
            result.directionStats = LatticeDirections.Run(graph, options.repairLatticeDirections, position.scale);

            // D
            // The MEAN spacing, not a per-cluster one, and that is a known approximation rather
            // than an oversight. Cleanup's thresholds are in cells and the graph it works on is
            // made of clusters, which carry no spacing of their own — giving them one means
            // recording it during the collapse. Bounded by how far the field grades: at the
            // measured 1.94x spread the error is at most about 40 %% either way on a test that is
            // already a heuristic. Worth doing if the sizing is ever made more aggressive; see
            // Benchmarks/adaptive-sizing.md for why it currently is not.
            result.cleanupStats = GraphCleanup.Run(graph, position.scale, options.cornerHeight);
            Census(census, "cleanup (graph)", graph);

            // E — compact to the flat, cyclically ordered, reverse-slot-indexed form
            var frozen = graph.Freeze();
            result.positions = frozen.positions;
            result.normals = frozen.normals;

            // F
            var faces = FaceTracer.Trace(frozen, out var traceStats);
            result.traceStats = traceStats;
            Census(census, "trace", faces);

            // G' — cap the holes the sized passes left, before regularisation, so the merge pass
            // sees the final face set.
            //
            // On an open input the mesh's own outline is a boundary loop too, and capping that
            // would weld it shut — so the pass is given the input's boundary edges and the lattice
            // spacing as a tolerance, and skips any loop that runs along them. It used to be given
            // a single bool instead, which meant it did nothing whatsoever on open input; see the
            // remarks on HoleFiller.Fill for what that cost on a scan.
            //
            // The tolerance is one lattice spacing. An output vertex derived from the outline sits
            // within half a cell of it by construction (the position field anchors each lattice
            // point inside a half-cell box around its input vertex), so one whole spacing is a
            // factor of two of headroom without being loose enough to reach a hole's rim, which is
            // at least a full quad in from the edge.
            topology.CollectBoundaryEdges(out var outlineEdges);

            CensusAfterHoles(census, faces);
            result.holeFillStats = HoleFiller.Fill(faces, frozen.vertexCount,
                topology.boundaryEdgeCount == 0,
                frozen.positions, topology.positions, outlineEdges, position.scale,
                topology.triangles);

            // A fan adds a hub vertex, so every per-vertex array has to grow to match or the fan
            // faces index past the end of them.
            //
            // Extended on `frozen`, not on `result`, and that distinction is the whole bug this
            // replaced. `result.positions` is an alias of `frozen.positions`; Array.Resize allocates
            // a new array and rebinds only the reference it is given, so resizing through `result`
            // left `frozen` pointing at the original. QuadRegularizer runs after this and indexes
            // `graph.positions` — straight past its end, IndexOutOfRangeException on every run.
            //
            // FrozenQuadGraph.vertexCount is derived from positions.Length, so extending the array
            // is all that is needed for the graph to agree about its own size.
            if (result.holeFillStats.verticesAdded > 0)
            {
                var added = result.holeFillStats.addedPositions;
                var oldCount = frozen.positions.Length;

                Array.Resize(ref frozen.positions, oldCount + added.Count);
                Array.Resize(ref frozen.normals, oldCount + added.Count);

                for (var i = 0; i < added.Count; i++)
                {
                    frozen.positions[oldCount + i] = added[i];
                    frozen.normals[oldCount + i] = Vector3.zero;
                }

                // The hub's normal is the average of the rim it closes, not the normal of the lid.
                // A lid's own geometric normal describes the flat patch; the rim describes the
                // surface the patch is standing in for, which is what shading should follow.
                foreach (var face in faces)
                {
                    if (face == null) continue;
                    for (var c = 0; c < face.Length; c++)
                    {
                        var v = face[c];
                        if (v < oldCount) continue;
                        for (var k = 0; k < face.Length; k++)
                            if (face[k] < oldCount) frozen.normals[v] += frozen.normals[face[k]];
                    }
                }

                for (var i = oldCount; i < frozen.normals.Length; i++)
                {
                    frozen.normals[i] = MeshMath.SafeNormalize(frozen.normals[i], Vector3.up);
                }

                result.positions = frozen.positions;
                result.normals = frozen.normals;

            // G
            }
            if (options.mergeTrianglePairs)
                result.regularizeStats = QuadRegularizer.MergeTrianglePairs(
                    frozen, faces, options.mergeQualityThreshold);
            else
                result.regularizeStats = new RegularizeStats();

            Census(census, "regularise", faces);

            // After the pair merger, and only after: this pass handles the triangles that pass
            // cannot reach. `QuadRegularizer` needs two triangles sharing an EDGE, and on the real
            // assets 80 % of survivors have nothing but quads around them - 509 of the character's
            // 644. Two triangles separated by one quad form a hexagon, and a hexagon splits into two
            // quads, which is the largest remaining bucket (233 of 644 on the character).
            //
            // Running it before the merger would waste its work: an adjacent pair is cheaper to
            // merge directly than to re-tile through a hexagon, and consuming the quad between two
            // triangles could separate a pair the merger would have taken.
            if (options.pairTrianglesAcrossQuads)
                result.trianglePairingStats = TrianglePairing.Run(
                    faces, frozen.positions, options.trianglePairingQualityThreshold,
                    options.trianglePairingMaxChainQuads, options.trianglePairingMaxRounds,
                    options.trianglePairingMaxReflexDegrees);

            Census(census, "pair triangles", faces);

            // Last, and after the regulariser has had its chance to merge triangle pairs: turn what
            // n-gons remain into quads. ToMesh has no n-gon topology to emit and fan-triangulates
            // them, which is the starburst the wireframe shows, so anything still above four corners
            // here reaches the user as slivers radiating from a point.
            result.ngonSplitStats = NgonQuadrangulator.Run(frozen, faces);
            Census(census, "split n-gons", faces);

            // And pair again, because the splitter above MAKES triangles. An n-gon with an odd
            // number of corners has no all-quad tiling - the same parity argument that makes a lone
            // triangle unremovable - so the splitter leaves one triangle behind per odd n-gon, and
            // those are created after the only pass that could remove them has finished.
            //
            // Measured on the character at the delivered density: pairing took 540 triangles down to
            // 322, and the mesh reaching the user had 440. The 118 it gained back were made here,
            // by a pass running one line too late.
            //
            // Safe to run twice: every patch it applies is tiled by quads alone, so it cannot
            // produce work for the splitter in return, and it strictly reduces the triangle count
            // so the two runs cannot cycle.
            if (options.pairTrianglesAcrossQuads)
                result.trianglePairingAfterNgonStats = TrianglePairing.Run(
                    faces, frozen.positions, options.trianglePairingQualityThreshold,
                    options.trianglePairingMaxChainQuads, options.trianglePairingMaxRounds,
                    options.trianglePairingMaxReflexDegrees);

            Census(census, "pair again", faces);

            result.positions = frozen.positions;
            result.normals = frozen.normals;

            // H — remove the faces that cannot be shaded, then settle winding for the whole
            // surface. Both after every pass that adds a face: one added later would not be
            // covered, and the components the orienter works over only exist once the face set is
            // final.
            //
            // Repair first. A duplicate pair joins two components through edges neither should own,
            // votes its area twice, and — being wound one way each — makes the consistency walk
            // arrive at a contradiction it will propagate outwards.
            result.repairStats = FaceRepair.Run(faces, frozen.positions);
            result.orientStats = FaceOrienter.Run(faces, frozen.positions, frozen.normals);
            Census(census, "repair + orient", faces);

            // I — rearrange the vertices across the surface now that the faces they belong to are
            // final and consistently wound. It has to be after the orienter, because its flip guard
            // asks whether a move reverses a face and that question is meaningless while the faces
            // are still being turned round; and before the shading normals, which are a function of
            // the geometry it produces.
            if (options.relaxSurface && options.surface != null)
                result.relaxStats = SurfaceRelaxation.Run(faces, frozen.positions, options.surface,
                    position.scale, options.relaxIterations, options.relaxStrength,
                    options.relaxMaxProjection, SurfaceRelaxation.defaultConvergence,
                    options.relaxFeatureAngleDegrees, options.relaxMaxDrift);

            // J — pull vertices onto the creases the lattice ran parallel to but missed. AFTER
            // relaxation, and that order is the whole reason this works: relaxation's job is to
            // smooth, so anything sharpened before it runs is the first thing it rounds off again.
            if (options.snapToFeatures && options.features != null
                                        && options.featurePositions != null)
                result.snapStats = FeatureSnap.Run(faces, frozen.positions,
                    options.featurePositions, options.features, position.scale,
                    options.snapMaxCells, options.snapMinEdgeCells);

            // Only now can shading normals be computed, because they are a function of the final
            // faces and of the direction those faces ended up facing.
            result.shadingNormals = NormalRecalculator.Compute(faces, frozen.positions, frozen.normals);

            result.faces = faces;
            Recount(result);

            return result;
        }

        /// <summary>
        /// The integer-grid path: flow, subdivide, unfold, read the cells, then the same repair,
        /// orientation, relaxation and snapping tail as the collapse path.
        /// </summary>
        static QuadExtractionResult ExtractIntegerGrid(RemeshTopology topology,
            OrientationField orientation, PositionField position, ExtractionOptions options,
            QuadExtractionResult result, FeatureGraph features)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            Vector3[] positions;
            Vector3[] normals;
            List<int[]> faces;
            IntegerGridStats igStats;
            var usedLayout = IntegerGridMap.parametrizationLayout && position.layoutFaces != null && position.layoutFaces.Count > 0;
            if (usedLayout)
            {
                // The quantised layout, meshed patch by patch: see QuantizedLayout.
                positions = position.layoutPositions;
                normals = position.layoutNormals;
                faces = new List<int[]>(position.layoutFaces.Count);
                foreach (var q in position.layoutFaces) faces.Add((int[])q.Clone());
                igStats = new IntegerGridStats();
                igStats.totalMs = watch.ElapsedMilliseconds;
                result.integerGridStats = igStats;
            }
            else if (IntegerGridMap.parametrizationTrace && position.cornerUvExact != null
                && position.cornerUvExact.Length == 2 * topology.triangles.Length)
            {
                // The parametrisation's own integer grid, traced: no collapse, no rounding, no flip
                // repair -- see ParametrizationTrace.
                Extraction.ParametrizationTrace.Extract(topology, position.cornerUvExact,
                    out positions, out normals, out faces, out var traceStats);
                if (Extraction.ParametrizationTrace.fillHoles)
                    Extraction.ParametrizationTrace.FillHoles(ref positions, ref normals, faces, traceStats);
                result.parametrizationTraceStats = traceStats;
                igStats = new IntegerGridStats();
                igStats.totalMs = watch.ElapsedMilliseconds;
                result.integerGridStats = igStats;
            }
            else
            {
            var map = IntegerGridMap.Build(topology, orientation, position, features);
            var initialDiff = IntegerGridMap.debugChangedEdgesPath != null ? map.edgeDiff.ToArray() : null;
            var initialX = initialDiff != null ? map.edgeX.ToArray() : null;
            var initialY = initialDiff != null ? map.edgeY.ToArray() : null;
            map.BuildIntegerConstraints();
            map.SolveFlow();
            if (initialDiff != null) map.DumpChangedEdges(initialDiff, initialX, initialY, "flow");

            // flow -> subdivide -> fix flips -> subdivide, which is ComputeIndexMap's own order.
            // An extra flip repair used to sit here, from when subdivision refused to split folded
            // faces and every surviving fold became a hole. Subdivision splits them now, so the
            // call had no reason left to exist and the reference does not make it.
            var subdivideWatch = System.Diagnostics.Stopwatch.StartNew();
            map.stats.subdivideSplitsFirst = map.Subdivide();
            map.stats.subdivideMs += subdivideWatch.ElapsedMilliseconds;

            map.FixFlips();
            if (initialDiff != null) map.DumpChangedEdges(initialDiff, initialX, initialY, "fixflips");

            subdivideWatch.Restart();
            map.stats.subdivideSplitsSecond = map.Subdivide();
            map.stats.subdivideMs += subdivideWatch.ElapsedMilliseconds;

            // The offsets are final from here, but nothing has yet moved the position field to agree
            // with them. Extraction averages that field inside each cell, so without this the output
            // vertex sits wherever smoothing left the samples rather than on the lattice the map
            // just decided. The reference solves it here, between the last subdivision and the
            // extract, and so do we.
            // Creases first, exactly as the reference orders it: trace them, move the field onto
            // evenly resampled crease lines, and hand the fixed solve a constraint per crease cell
            // so it slides those cells along their crease instead of averaging them off it.
            var sharpConstraints = Extraction.IntegerGridMap.creaseConstraints
                ? map.OptimizePositionsSharp()
                : null;
            if (Extraction.IntegerGridMap.optimizePositions) map.OptimizePositionsFixed(sharpConstraints);

            var grid = map.Extract(features, topology.vertexCount);
            map.stats.totalMs = watch.ElapsedMilliseconds;
            result.integerGridStats = map.stats;
            igStats = map.stats;

            positions = grid.positions;
            normals = grid.normals;
            faces = grid.faces;
            }
            result.positions = positions;
            result.normals = normals;

            var census = options.recordCensus ? result.census = new List<StageCensus>() : null;
            Census(census, "integer grid", faces);

            var stageWatch = System.Diagnostics.Stopwatch.StartNew();
            var stageTimes = new System.Text.StringBuilder($"igm={watch.ElapsedMilliseconds}");
            void Stage(string name)
            {
                stageTimes.Append(' ').Append(name).Append('=').Append(stageWatch.ElapsedMilliseconds);
                stageWatch.Restart();
            }

            result.repairStats = FaceRepair.Run(faces, positions);
            RemoveDebris(faces, positions.Length, Extraction.IntegerGridMap.debrisBelowFaces,
                out igStats.debrisPieces, out igStats.debrisFaces);
            // An open scan gets the tighter threshold: its torn strips -- cells glued across many
            // cells where the flip repair gave up -- run 3-5 median edges, under the closed-mesh
            // threshold. See IntegerGridMap.needleEdgeOverMedianOpen.
            var needleLimit = topology.boundaryEdgeCount > 0
                              && Extraction.IntegerGridMap.needleEdgeOverMedianOpen > 0f
                ? Extraction.IntegerGridMap.needleEdgeOverMedianOpen
                : Extraction.IntegerGridMap.needleEdgeOverMedian;
            // The layout's quads are grid cells of rectangles, long only where a patch is: removing
            // them opens holes the cap then fills badly (body at 5k: 37 at a knee, a 76-edge hole).
            if (!usedLayout) igStats.needlesRemoved = RemoveNeedles(faces, positions, needleLimit);

            // Close what the map left open. The extractor builds every quad from two half-cells
            // meeting on a diagonal, drops a half-cell that finds no partner, and its own hole pass
            // (IntegerGridExtract.FixHoles) tiles only EVEN rims with quads -- so every odd rim
            // stays open, and on the helmet head every remaining hole was exactly that: 12-16
            // three-edge loops per run and the odd 4 or 6, no non-manifold edges, nothing else.
            // The collapse path's capping pass closes a 3- or 4-rim with one face and grids larger
            // ones; pairing then turns triangles separated by a strip of quads back into quads.
            if (Extraction.IntegerGridMap.closeHoles)
            {
                topology.CollectBoundaryEdges(out var outlineEdges);
                // Faces the extractor emitted. Anything else was made below, by capping or by
                // pairing, and is what gets taken back if it breaks manifoldness.
                var extracted = new HashSet<int[]>(faces);

                HoleFillStats Cap()
                {
                    var stats = HoleFiller.Fill(faces, positions.Length,
                        topology.boundaryEdgeCount == 0, positions, topology.positions, outlineEdges,
                        position.scale, topology.triangles);

                    var added = stats.addedPositions;
                    if (added.Count == 0) return stats;
                    var oldCount = positions.Length;
                    Array.Resize(ref positions, oldCount + added.Count);
                    Array.Resize(ref normals, oldCount + added.Count);
                    for (var i = 0; i < added.Count; i++)
                    {
                        positions[oldCount + i] = added[i];
                        normals[oldCount + i] = Vector3.zero;
                    }
                    // A hub's normal is its rim's, as on the collapse path.
                    foreach (var face in faces)
                    {
                        if (face == null) continue;
                        foreach (var v in face)
                        {
                            if (v < oldCount) continue;
                            foreach (var w in face)
                                if (w < oldCount) normals[v] += normals[w];
                        }
                    }
                    for (var i = oldCount; i < normals.Length; i++)
                        normals[i] = MeshMath.SafeNormalize(normals[i], Vector3.up);
                    result.positions = positions;
                    result.normals = normals;
                    return stats;
                }

                // Wound consistently first: the cap pass chains boundary half-edges head to tail,
                // and a rim whose faces disagree about winding dead-ends -- three such walks were
                // abandoned per run on the helmet head. Oriented again below, after everything.
                Stage("repair");
                FaceOrienter.Run(faces, positions, normals);
                result.holeFillStats = Cap();
                Stage("cap");

                // A cap on a rim running along an edge that already has two faces makes that edge
                // non-manifold, and so, rarely, does a pairing patch: on the helmet head at 11000
                // two re-tiled quads came out sharing two edges. Such faces go and the gap they
                // leave -- now a simple rim -- is capped again. A three-edge gap is a blemish; a
                // three-face edge breaks every tool downstream that walks the mesh.
                igStats.capsDroppedNonManifold = DropNonManifoldNew(faces, extracted);
                if (igStats.capsDroppedNonManifold > 0) Cap();

                result.trianglePairingStats = TrianglePairing.Run(faces, positions,
                    options.trianglePairingQualityThreshold, options.trianglePairingMaxChainQuads,
                    options.trianglePairingMaxRounds, options.trianglePairingMaxReflexDegrees);

                // The pairing thresholds were tuned on the collapse path, against thousands of
                // triangles, where each re-tiling's cost in irregular vertices adds up. Here a
                // handful are left -- the caps above -- so a second, lenient pass for just those
                // trades a couple of irregular vertices each for a pure-quad mesh.
                if (Extraction.IntegerGridMap.lenientPairingReflexDegrees > 0f)
                    result.trianglePairingAfterNgonStats = TrianglePairing.Run(faces, positions,
                        0f, 2 * options.trianglePairingMaxChainQuads,
                        options.trianglePairingMaxRounds,
                        Extraction.IntegerGridMap.lenientPairingReflexDegrees);
                Stage("pair");

                for (var round = 0; round < 4; round++)
                {
                    var dropped = DropNonManifoldNew(faces, extracted, round > 0);
                    if (dropped == 0) break;
                    igStats.capsDroppedNonManifold += dropped;
                    Cap();
                }
                // Whatever rim the repairs above opened and nothing re-capped.
                FaceOrienter.Run(faces, positions, normals);
                Cap();
                igStats.capsDroppedNonManifold += DropNonManifoldNew(faces, extracted);
                // The input's own openings stay open here too, as in the cap above: a car's rim
                // barrel, an open tube 51 edges round at each end, was zipped shut at both ends by
                // the ladder -- the flat striped disc over every wheel of the Porsche.
                var outlineTest = Extraction.IntegerGridMap.closeKeepsOutline && topology.boundaryEdgeCount > 0
                    ? HoleFiller.OutlineTest(topology.positions, outlineEdges, position.scale)
                    : null;
                Func<List<int>, bool> onOutline = null;
                if (outlineTest != null) onOutline = loop => outlineTest(loop, positions);
                igStats.smallLoopsCapped = CapSmallLoopsUndirected(faces, 8, null, onOutline);
                // Longer rims the directed walk abandoned at a fold -- up to 51 edges, 66 open edges
                // on the head at 4,600 with Curves Add Quads -- zipped shut with a ladder of quads.
                if (Extraction.IntegerGridMap.ladderCapMaxEdges > 8)
                    igStats.smallLoopsCapped += CapSmallLoopsUndirected(faces,
                        Extraction.IntegerGridMap.ladderCapMaxEdges, positions, onOutline);
                igStats.capsDroppedNonManifold += DropNonManifoldNew(faces, extracted);

                // A rim too curved for a flat grid is capped as one n-gon. ToMesh would fan it into
                // slivers, so it is cut into quads here, one more triangle when the count is odd.
                SplitPolygons(faces);
                EdgeCensus(faces, out igStats.openAfterClose, out igStats.nonManifoldAfterClose);
                Stage("close");
            }

            result.orientStats = FaceOrienter.Run(faces, positions, normals);
            Stage("orient");
            Census(census, "repair + orient", faces);

            // The layout laid rows exactly on its grid lines (building edges, the border): those
            // vertices slide along them, see IntegerGridMap.relaxAlongLayoutLines.
            var lineTolerance = Extraction.IntegerGridMap.layoutLineToleranceCells * position.scale;
            var lineSet = usedLayout && options.layoutLines != null && lineTolerance > 0f
                ? new LineSet(options.layoutLines, lineTolerance)
                : null;
            var onLine = VerticesOnLines(positions, lineSet, lineTolerance,
                Extraction.IntegerGridMap.layoutLineMoveOnto);
            if (onLine == null) lineSet = null;
            result.onLayoutLine = onLine;
            if (options.relaxSurface && options.surface != null)
                result.relaxStats = SurfaceRelaxation.Run(faces, positions, options.surface,
                    position.scale, options.relaxIterations, options.relaxStrength,
                    options.relaxMaxProjection, SurfaceRelaxation.defaultConvergence,
                    options.relaxFeatureAngleDegrees, options.relaxMaxDrift, onLine: onLine, lines: lineSet,
                    lineReach: lineTolerance);
            Stage("relax");

            var creaseLoopPositions = options.creaseLoopPositions ?? options.featurePositions;
            if (options.creaseLoops != null && creaseLoopPositions != null)
            {
                // In rounds: the snapped vertices are then held while the rest relax around them,
                // which makes room for the neighbours a guard refused, and the next round takes
                // those. One round leaves notches wherever a single vertex was refused.
                var snappedCurve = new int[positions.Length];
                for (var i = 0; i < snappedCurve.Length; i++) snappedCurve[i] = -1;
                var snappedTangent = new Vector3[positions.Length];
                var rounds = Mathf.Max(1, Extraction.IntegerGridMap.creaseLoopSnapRounds);
                for (var round = 0; round < rounds; round++)
                {
                    var stats = CreaseLoopSnap.Run(faces, positions, creaseLoopPositions,
                        options.creaseLoops, position.scale, Extraction.IntegerGridMap.creaseLoopSnapCells,
                        snappedCurve: snappedCurve, snappedTangent: snappedTangent);
                    if (result.creaseLoopStats == null) result.creaseLoopStats = stats;
                    else result.creaseLoopStats.snapped += stats.snapped;
                    if (stats.snapped == 0 || !options.relaxSurface || options.surface == null) break;

                    var pinned = new bool[positions.Length];
                    for (var i = 0; i < pinned.Length; i++) pinned[i] = snappedCurve[i] >= 0;
                    SurfaceRelaxation.Run(faces, positions, options.surface, position.scale,
                        Extraction.IntegerGridMap.creaseLoopRelaxIterations, options.relaxStrength,
                        options.relaxMaxProjection, SurfaceRelaxation.defaultConvergence,
                        options.relaxFeatureAngleDegrees, Extraction.IntegerGridMap.creaseLoopRelaxMaxDrift,
                        pinned, onLine, lineSet, lineTolerance);
                }
            }

            if (options.snapToFeatures && features != null && options.featurePositions != null)
                result.snapStats = FeatureSnap.Run(faces, positions,
                    options.featurePositions, features, position.scale,
                    options.snapMaxCells, options.snapMinEdgeCells);

            result.shadingNormals = NormalRecalculator.Compute(faces, positions, normals);
            EdgeCensus(faces, out igStats.openAtEnd, out igStats.nonManifoldAtEnd);
            Stage("finish");
            result.stageTimes = stageTimes.ToString();

            result.faces = faces;
            Recount(result);
            return result;
        }

        /// <summary>
        /// Which of <paramref name="positions"/> lie within <paramref name="tolerance"/> of one of
        /// <paramref name="lines"/>, optionally moved exactly onto it. Null when none do.
        /// </summary>
        internal static bool[] VerticesOnLines(Vector3[] positions, LineSet lines, float tolerance,
            bool moveOnto = false)
        {
            if (positions == null || lines == null || !(tolerance > 0f)) return null;
            bool[] on = null;
            for (var v = 0; v < positions.Length; v++)
            {
                if (!lines.Nearest(positions[v], tolerance, out var point, out _)) continue;
                on ??= new bool[positions.Length];
                on[v] = true;
                if (moveOnto) positions[v] = point;
            }
            return on;
        }

        static void EdgeCensus(List<int[]> faces, out int open, out int nonManifold)
        {
            var count = new Dictionary<long, int>(EdgeKey.Comparer);
            foreach (var face in faces)
            {
                if (face == null) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    count[key] = count.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }
            open = 0;
            nonManifold = 0;
            foreach (var n in count.Values)
            {
                if (n == 1) open++;
                else if (n > 2) nonManifold++;
            }
        }

        /// <summary>
        /// Caps every simple boundary loop of at most <paramref name="maxLength"/> edges, chained
        /// WITHOUT regard to winding. Returns how many.
        /// </summary>
        /// <remarks>
        /// HoleFiller chains a rim head to tail along its faces' winding, and its header records
        /// that an undirected fallback was measured as worthless on the scan it was tuned on. Here
        /// it is not: where the integer map leaves a local fold, the winding flips part way round a
        /// rim and the directed walk dead-ends on what is a clean four-edge loop -- one per run on
        /// the helmet head at 11000, left open. Restricted to short loops whose every vertex has
        /// exactly two open edges, which leaves no choice about how to walk it; FaceOrienter
        /// decides the winding afterwards as it does for every face.
        /// </remarks>
        static int CapSmallLoopsUndirected(List<int[]> faces, int maxLength, Vector3[] ladderPositions = null,
            Func<List<int>, bool> onOutline = null)
        {
            var incidence = new Dictionary<long, int>(EdgeKey.Comparer);
            foreach (var face in faces)
            {
                if (face == null) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    incidence[key] = incidence.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }

            var neighbours = new Dictionary<int, List<int>>();
            foreach (var pair in incidence)
            {
                if (pair.Value != 1) continue;
                var a = EdgeKey.First(pair.Key);
                var b = EdgeKey.Second(pair.Key);
                if (!neighbours.TryGetValue(a, out var la)) neighbours[a] = la = new List<int>(2);
                if (!neighbours.TryGetValue(b, out var lb)) neighbours[b] = lb = new List<int>(2);
                la.Add(b);
                lb.Add(a);
            }

            var capped = 0;
            var visited = new HashSet<int>();
            foreach (var pair in neighbours)
            {
                var start = pair.Key;
                if (visited.Contains(start) || pair.Value.Count != 2) continue;
                var loop = new List<int> { start };
                var previous = start;
                var current = pair.Value[0];
                var simple = true;
                while (current != start && loop.Count <= maxLength)
                {
                    if (!neighbours.TryGetValue(current, out var next) || next.Count != 2 || loop.Contains(current))
                    {
                        simple = false;
                        break;
                    }
                    loop.Add(current);
                    var step = next[0] == previous ? next[1] : next[0];
                    previous = current;
                    current = step;
                }
                foreach (var v in loop) visited.Add(v);
                if (!simple || current != start || loop.Count < 3 || loop.Count > maxLength) continue;
                if (onOutline != null && onOutline(loop)) continue;
                if (ladderPositions != null && loop.Count > 8) AddLadder(faces, loop, ladderPositions);
                else faces.Add(loop.ToArray());
                capped++;
            }
            return capped;
        }

        static string CornerKey(int[] face)
        {
            var sorted = (int[])face.Clone();
            Array.Sort(sorted);
            return string.Join(",", sorted);
        }

        /// <summary>
        /// Cuts every face of more than four corners into a strip of quads from one corner, one
        /// more triangle when the count is odd. The corner is the first whose cuts are not already
        /// edges of the mesh -- a cut along an existing edge would give it a third face. A polygon
        /// with no such corner is left whole.
        /// </summary>
        /// <summary>
        /// Closes a rim with a ladder of quads: from the rim vertex farthest from its opposite
        /// number, both sides are walked towards the other end and joined rung by rung. A fold
        /// leaves long narrow slots, which this zips across; a fan from one corner would put
        /// every face on one vertex.
        /// </summary>
        static void AddLadder(List<int[]> faces, List<int> loop, Vector3[] positions)
        {
            var n = loop.Count;
            var start = 0;
            var best = -1f;
            for (var i = 0; i < n; i++)
            {
                var d = (positions[loop[i]] - positions[loop[(i + n / 2) % n]]).sqrMagnitude;
                if (d > best) { best = d; start = i; }
            }
            int At(int i) => loop[((start + i) % n + n) % n];

            var left = 1;
            var right = -1;
            faces.Add(new[] { At(0), At(left), At(right) });
            while (true)
            {
                var remaining = n - (left - right) - 1;
                if (remaining <= 0) break;
                if (remaining == 1)
                {
                    faces.Add(new[] { At(right), At(left), At(left + 1) });
                    break;
                }
                faces.Add(new[] { At(right), At(left), At(left + 1), At(right - 1) });
                left++;
                right--;
            }
        }

        static void SplitPolygons(List<int[]> faces)
        {
            HashSet<long> edges = null;
            var count = faces.Count;
            for (var f = 0; f < count; f++)
            {
                var face = faces[f];
                if (face == null || face.Length <= 4) continue;
                if (edges == null)
                {
                    edges = new HashSet<long>(EdgeKey.Comparer);
                    foreach (var g in faces)
                        if (g != null)
                            for (var k = 0; k < g.Length; k++)
                                edges.Add(EdgeKey.PackUndirected(g[k], g[(k + 1) % g.Length]));
                }

                var n = face.Length;
                for (var r = 0; r < n; r++)
                {
                    int At(int i) => face[(r + i) % n];
                    var cuts = new List<long>();
                    for (var k = 3; k < n - 1; k += 2) cuts.Add(EdgeKey.PackUndirected(At(0), At(k)));
                    var clear = true;
                    foreach (var cut in cuts)
                        if (edges.Contains(cut)) { clear = false; break; }
                    if (!clear) continue;

                    var pieces = new List<int[]>();
                    var k2 = 1;
                    while (n - k2 >= 3)
                    {
                        pieces.Add(new[] { At(0), At(k2), At(k2 + 1), At(k2 + 2) });
                        k2 += 2;
                    }
                    if (n - k2 == 2) pieces.Add(new[] { At(0), At(k2), At(k2 + 1) });
                    foreach (var cut in cuts) edges.Add(cut);
                    faces[f] = pieces[0];
                    for (var i = 1; i < pieces.Count; i++) faces.Add(pieces[i]);
                    break;
                }
            }
        }

        /// <summary>
        /// Removes every face not in <paramref name="original"/> that sits on an edge with more than
        /// two faces, then one face per edge still over two. Returns how many went.
        /// </summary>
        static int DropNonManifoldNew(List<int[]> faces, HashSet<int[]> original,
            bool clearAround = false)
        {
            // A new face on exactly the corners of an existing one -- in another order, which is
            // what FaceRepair's duplicate test does not catch after the fact -- is a fold of it.
            var corners = new HashSet<string>();
            foreach (var face in faces)
                if (face != null && original.Contains(face)) corners.Add(CornerKey(face));
            var duplicates = faces.RemoveAll(face => face != null && !original.Contains(face)
                                                     && !corners.Add(CornerKey(face)));

            var edgeFaces = new Dictionary<long, int>(EdgeKey.Comparer);
            foreach (var face in faces)
            {
                if (face == null) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    edgeFaces[key] = edgeFaces.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }

            // Which faces use each edge, so a dropped face's overlap partner can be found.
            var users = new Dictionary<long, List<int[]>>(EdgeKey.Comparer);
            foreach (var face in faces)
            {
                if (face == null) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    if (!users.TryGetValue(key, out var list)) users[key] = list = new List<int[]>(2);
                    list.Add(face);
                }
            }

            var dropped = duplicates;
            var partners = new HashSet<int[]>();
            for (var f = faces.Count - 1; f >= 0; f--)
            {
                var face = faces[f];
                if (face == null || original.Contains(face)) continue;
                var bad = false;
                for (var k = 0; k < face.Length && !bad; k++)
                    bad = edgeFaces[EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length])] > 2;
                if (!bad) continue;

                // The face it folded over -- sharing two of its edges -- goes too. Otherwise the
                // rim this leaves is capped by the same folded lid next time, and is left open.
                var shared = new Dictionary<int[], int>();
                for (var k = 0; k < face.Length; k++)
                    foreach (var other in users[EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length])])
                        if (other != face) shared[other] = shared.TryGetValue(other, out var c) ? c + 1 : 1;
                foreach (var pair in shared)
                    if (pair.Value >= 2) partners.Add(pair.Key);

                for (var k = 0; k < face.Length; k++)
                    edgeFaces[EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length])]--;
                faces.RemoveAt(f);
                dropped++;
            }
            if (partners.Count > 0)
            {
                foreach (var face in partners)
                    for (var k = 0; k < face.Length; k++)
                        edgeFaces[EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length])]--;
                dropped += faces.RemoveAll(partners.Contains);
            }

            // What is left was emitted by the extractor itself: its own hole pass
            // (IntegerGridExtract.FixHoles) can lay a quad over one already there, and the two then
            // share two edges. Of the faces on each remaining three-face edge, the one to go is the
            // one overlapping another there -- sharing a second edge with it -- preferring the one
            // already on a hole, whose removal opens the least.
            var edgeList = new Dictionary<long, List<int[]>>(EdgeKey.Comparer);
            foreach (var face in faces)
            {
                if (face == null) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    if (!edgeList.TryGetValue(key, out var list)) edgeList[key] = list = new List<int[]>(2);
                    list.Add(face);
                }
            }

            var victims = new HashSet<int[]>();
            foreach (var pair in edgeList)
            {
                var around = pair.Value;
                if (around.Count <= 2) continue;
                // Second time round the same fold: open everything on the edge and let the cap
                // close the simple rim that leaves, rather than re-laying the lid that folded.
                if (clearAround)
                {
                    foreach (var face in around) victims.Add(face);
                    continue;
                }
                int[] victim = null;
                var victimScore = -1;
                foreach (var face in around)
                {
                    if (victims.Contains(face)) { victim = null; victimScore = int.MaxValue; break; }
                    var overlaps = false;
                    var open = 0;
                    for (var k = 0; k < face.Length; k++)
                    {
                        var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                        if (key == pair.Key) continue;
                        var others = edgeList[key];
                        if (others.Count == 1) open++;
                        foreach (var other in others)
                            if (other != face && around.Contains(other)) overlaps = true;
                    }
                    var score = (overlaps ? 100 : 0) + open;
                    if (score > victimScore) { victimScore = score; victim = face; }
                }
                if (victim != null) victims.Add(victim);
            }
            if (victims.Count > 0)
            {
                dropped += faces.RemoveAll(victims.Contains);
            }
            return dropped;
        }

        /// <summary>
        /// Drops every edge-connected piece with fewer than <paramref name="belowFaces"/> faces,
        /// as long as a larger piece exists. Vertices are left in place, unreferenced.
        /// </summary>
        /// <summary>
        /// Removes faces whose longest edge is more than <paramref name="overMedian"/> times the
        /// median edge, leaving the gap to the hole closing that follows. Returns how many.
        /// </summary>
        /// <remarks>
        /// A lattice cell that collapses into a long strip -- along a crease whose cells the map
        /// could not keep apart, or along the ragged rim of an open scan -- comes out as one output
        /// vertex at the strip's average, and the faces around it as needles: on the body with
        /// creases detected, quads 11x the median edge lying across the thigh over their
        /// neighbours; on ccnb's border, 20-unit slivers against a 2.3-unit median. Graded
        /// spacing legitimately spans about 4x, so 5x is clear of any real quad.
        /// </remarks>
        static int RemoveNeedles(List<int[]> faces, Vector3[] positions, float overMedian)
        {
            if (!(overMedian > 0f) || faces.Count == 0) return 0;
            var longest = new float[faces.Count];
            var lengths = new List<float>(faces.Count * 4);
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null) continue;
                var worst = 0f;
                for (var k = 0; k < face.Length; k++)
                {
                    var length = (positions[face[k]] - positions[face[(k + 1) % face.Length]]).magnitude;
                    lengths.Add(length);
                    if (length > worst) worst = length;
                }
                longest[f] = worst;
            }
            if (lengths.Count == 0) return 0;
            lengths.Sort();
            var limit = lengths[lengths.Count / 2] * overMedian;
            if (!(limit > 0f)) return 0;

            // Only faces with every edge shared. On an open scan's outer rim a removed face is a
            // notch nothing may fill -- hole closing rightly leaves the true boundary alone -- and
            // on ccnb that bit 32 bites out of the border.
            var uses = new Dictionary<long, int>(faces.Count * 4, EdgeKey.Comparer);
            foreach (var face in faces)
            {
                if (face == null) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var key = EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length]);
                    uses[key] = uses.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }
            bool OnRim(int[] face)
            {
                for (var k = 0; k < face.Length; k++)
                    if (uses[EdgeKey.PackUndirected(face[k], face[(k + 1) % face.Length])] < 2) return true;
                return false;
            }

            var removed = 0;
            for (var f = faces.Count - 1; f >= 0; f--)
            {
                if (faces[f] == null || longest[f] <= limit || OnRim(faces[f])) continue;
                faces.RemoveAt(f);
                removed++;
            }
            return removed;
        }

        static void RemoveDebris(List<int[]> faces, int vertexCount, int belowFaces,
            out int pieces, out int removedFaces)
        {
            pieces = 0;
            removedFaces = 0;
            if (belowFaces <= 0 || faces.Count == 0) return;

            // Pieces by shared vertex, which is what "detached" means to anyone looking at it.
            var parent = new int[vertexCount];
            for (var v = 0; v < vertexCount; v++) parent[v] = v;
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            foreach (var face in faces)
            {
                if (face == null || face.Length == 0) continue;
                for (var k = 1; k < face.Length; k++)
                {
                    var a = Find(face[0]);
                    var b = Find(face[k]);
                    if (a != b) parent[a] = b;
                }
            }

            var size = new Dictionary<int, int>();
            foreach (var face in faces)
            {
                if (face == null || face.Length == 0) continue;
                var root = Find(face[0]);
                size[root] = size.TryGetValue(root, out var n) ? n + 1 : 1;
            }

            var largest = 0;
            foreach (var n in size.Values) largest = Mathf.Max(largest, n);
            if (largest < belowFaces) return;

            foreach (var n in size.Values)
                if (n < belowFaces) pieces++;
            if (pieces == 0) return;

            var before = faces.Count;
            faces.RemoveAll(face => face != null && face.Length > 0 && size[Find(face[0])] < belowFaces);
            removedFaces = before - faces.Count;
        }

        static void Recount(QuadExtractionResult result)
        {
            result.quadCount = 0;
            result.triangleCount = 0;
            result.ngonCount = 0;

            foreach (var face in result.faces)
            {
                switch (face.Length)
                {
                    case 3: result.triangleCount++; break;
                    case 4: result.quadCount++; break;
                    default: result.ngonCount++; break;
                }
            }
        }

        /// <summary>
        /// Builds a <see cref="Mesh"/> from an extraction result. Main thread only.
        /// </summary>
        /// <remarks>
        /// Quads are emitted as <see cref="MeshTopology.Quads"/> in submesh 0 and any remaining
        /// triangles and n-gons are fan-triangulated into submesh 1.
        ///
        /// The split matters more than it looks. This used to test `quadCount == faceCount` and
        /// fan-triangulate EVERYTHING unless the result was 100% quads — which no real mesh is. A
        /// scan comes out around 70% quads, so a single n-gon anywhere sent every quad in the mesh
        /// through the triangle path, and the asset that landed in the project was pure triangle
        /// soup. Opening it and concluding that nothing had been retopologised was the correct
        /// reading of what was there.
        ///
        /// That is also exactly the defect the previous version of this comment claimed to have
        /// fixed. It fixed it for the all-quad case and left the case that actually occurs.
        /// </remarks>
        /// <param name="quadTopology">
        /// Emit the quads as <see cref="MeshTopology.Quads"/> in submesh 0, with the leftover
        /// triangles and n-gons in a second submesh. False fan-triangulates everything into ONE
        /// triangle submesh instead.
        /// </param>
        /// <remarks>
        /// The flag exists because the two-submesh form has a cost that is not about rendering: a
        /// Unity mesh gets one material slot per submesh, so a result carrying any non-quad asks
        /// the renderer for TWO materials where the competitor's imported FBX asks for one. That is
        /// not a bug — the window fills both slots with the source material — but it is a visible
        /// difference in the inspector on every asset this produces, and it is only there because
        /// about 7 % of our faces are not quads. Anything downstream that assumes one submesh per
        /// object (a material workflow, an atlas, a naive exporter) meets it too.
        ///
        /// Nothing is lost by triangulating HERE as long as the quads leave by another door. They
        /// do: <see cref="Extraction"/> keeps the real face list on the result, and both the FBX
        /// and OBJ writers take that list rather than this mesh. Unity's own renderer triangulates
        /// quad topology at draw time regardless, so the quad submesh was never buying fidelity on
        /// screen — only in the .asset.
        /// </remarks>
        public static Mesh ToMesh(QuadExtractionResult extraction, string name,
            bool quadTopology = true)
        {
            var mesh = new Mesh { name = name };

            // Per-corner UVs need a vertex per distinct (position, UV) pair: a Unity vertex holds
            // one UV, so a seam is represented by duplicating the vertex, as the source did.
            var faces = extraction.faces;
            var positions = extraction.positions;
            var shading = extraction.shadingNormals;
            var fallbackNormals = extraction.normals;
            Vector2[] cornerUvs = null;
            Vector4[] tangents = null;
            if (extraction.faceUvs != null && extraction.faceUvs.Length == extraction.faces.Count)
                SplitAtUvSeams(extraction, out positions, out faces, out cornerUvs, out shading,
                    out fallbackNormals, out tangents);

            if (positions.Length > 65534)
                mesh.indexFormat = IndexFormat.UInt32;

            mesh.SetVertices(positions);

            // Transferred attributes, when the pipeline sampled them from the source surface.
            // Without UVs no material can texture the result, which on photogrammetry input throws
            // away the entire point of the scan.
            if (cornerUvs != null)
                mesh.SetUVs(0, cornerUvs);
            else if (extraction.uvs != null && extraction.uvs.Length == extraction.positions.Length)
                mesh.SetUVs(0, extraction.uvs);

            if (extraction.faceMaterials != null && extraction.faceMaterials.Length == faces.Count
                && extraction.materialSlots > 0)
            {
                // One submesh per input material, in the input's slot order, so the source
                // renderer's materials go on as they were. A slot all quads keeps quad topology;
                // any other is fanned into triangles (one topology per submesh).
                var slots = extraction.materialSlots;
                var perSlot = new List<int>[slots];
                var allQuads = new bool[slots];
                for (var m = 0; m < slots; m++) { perSlot[m] = new List<int>(); allQuads[m] = quadTopology; }
                for (var f = 0; f < faces.Count; f++)
                {
                    var face = faces[f];
                    if (face == null || face.Length < 3) continue;
                    var m = Mathf.Clamp(extraction.faceMaterials[f], 0, slots - 1);
                    if (face.Length != 4) allQuads[m] = false;
                }
                for (var f = 0; f < faces.Count; f++)
                {
                    var face = faces[f];
                    if (face == null || face.Length < 3) continue;
                    var m = Mathf.Clamp(extraction.faceMaterials[f], 0, slots - 1);
                    if (allQuads[m]) { perSlot[m].AddRange(face); continue; }
                    for (var i = 1; i + 1 < face.Length; i++)
                    {
                        perSlot[m].Add(face[0]);
                        perSlot[m].Add(face[i]);
                        perSlot[m].Add(face[i + 1]);
                    }
                }
                mesh.subMeshCount = slots;
                for (var m = 0; m < slots; m++)
                    mesh.SetIndices(perSlot[m], allQuads[m] ? MeshTopology.Quads : MeshTopology.Triangles, m, false);
                mesh.RecalculateBounds();
                if (shading != null && shading.Length == positions.Length) mesh.SetNormals(shading);
                else if (fallbackNormals != null && fallbackNormals.Length == positions.Length) mesh.SetNormals(fallbackNormals);
                else mesh.RecalculateNormals();
                if (tangents != null && tangents.Length == positions.Length) mesh.SetTangents(tangents);
                return mesh;
            }

            var quads = new List<int>(extraction.quadCount * 4);
            var triangles = new List<int>(16);

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;

                // Emitted exactly as stored. Winding is settled in the face list by FaceOrienter,
                // per connected component and by an area-weighted vote, so that the OBJ the
                // benchmark scores, any later FBX, and this mesh all agree about which way the
                // surface faces.
                //
                // This used to decide per face here, comparing one face's Newell normal against its
                // vertices' averaged input normals. On the reference asset the whole mesh was
                // consistently wound inside-out, so that test flipped 5,939 faces and left 34 —
                // whose slivers gave their normals no usable sign — and the 34 became the only
                // faces disagreeing with their neighbours. Each one is backface-culled into what
                // looks exactly like a hole.
                if (face.Length == 4 && quadTopology)
                {
                    quads.Add(face[0]); quads.Add(face[1]); quads.Add(face[2]); quads.Add(face[3]);
                    continue;
                }

                for (var i = 1; i + 1 < face.Length; i++)
                {
                    triangles.Add(face[0]);
                    triangles.Add(face[i]);
                    triangles.Add(face[i + 1]);
                }
            }

            if (quadTopology)
            {
                // Unity allows one topology per submesh, so a mixed result needs two. Submesh 0 is
                // always the quads even when there are none, so a consumer can rely on its index.
                var subMeshes = triangles.Count > 0 ? 2 : 1;
                mesh.subMeshCount = subMeshes;

                mesh.SetIndices(quads, MeshTopology.Quads, 0, false);
                if (subMeshes == 2)
                    mesh.SetIndices(triangles, MeshTopology.Triangles, 1, false);
            }
            else
            {
                // One submesh, one material slot. Every face went through the fan above, so this
                // list is the whole mesh rather than the leftovers.
                mesh.subMeshCount = 1;
                mesh.SetIndices(triangles, MeshTopology.Triangles, 0, false);
            }

            mesh.RecalculateBounds();

            // Normals computed from these faces, by NormalRecalculator, angle-weighted. Not the
            // input surface's normals, which this used to write: those describe detail the output
            // no longer carries, so they tilt each facet's lighting by a slope that is arbitrary
            // with respect to its neighbours, and the surface reads as dented everywhere.
            //
            // Mesh.RecalculateNormals is not the fallback it looks like. It reads the index buffer
            // as triangles, and submesh 0 here is MeshTopology.Quads, so on a pure-quad result it
            // has nothing to work from.
            if (shading != null && shading.Length == positions.Length)
                mesh.SetNormals(shading);
            else if (fallbackNormals != null && fallbackNormals.Length == positions.Length)
                mesh.SetNormals(fallbackNormals);
            else
                mesh.RecalculateNormals();
            if (tangents != null && tangents.Length == positions.Length)
                mesh.SetTangents(tangents);

            return mesh;
        }

        /// <summary>
        /// One vertex per distinct (vertex, UV) pair, faces re-indexed onto them. Normals are
        /// copied, not recomputed, so the lighting across a UV seam stays continuous.
        /// </summary>
        static void SplitAtUvSeams(QuadExtractionResult extraction, out Vector3[] positions,
            out List<int[]> faces, out Vector2[] uvs, out Vector3[] shadingNormals,
            out Vector3[] normals, out Vector4[] tangents)
        {
            var srcTangents = extraction.faceTangents != null && extraction.faceTangents.Length == extraction.faces.Count
                ? extraction.faceTangents
                : null;
            var outTangents = srcTangents != null ? new List<Vector4>(extraction.positions.Length) : null;
            var source = extraction.positions;
            var srcShading = extraction.shadingNormals != null &&
                             extraction.shadingNormals.Length == source.Length
                ? extraction.shadingNormals
                : null;
            var srcNormals = extraction.normals != null && extraction.normals.Length == source.Length
                ? extraction.normals
                : null;
            // Split at hard edges, the corners' own; they split only where the UVs do, so one
            // (vertex, UV) pair has one.
            var corners = extraction.faceNormals != null && extraction.faceNormals.Length == extraction.faces.Count
                ? extraction.faceNormals
                : null;
            if (corners != null && srcShading == null) srcShading = srcNormals ?? new Vector3[source.Length];

            var map = new Dictionary<(int, Vector2), int>(source.Length * 5 / 4);
            var outPositions = new List<Vector3>(source.Length);
            var outUvs = new List<Vector2>(source.Length);
            var outShading = srcShading != null ? new List<Vector3>(source.Length) : null;
            var outNormals = srcNormals != null ? new List<Vector3>(source.Length) : null;
            faces = new List<int[]>(extraction.faces.Count);

            for (var f = 0; f < extraction.faces.Count; f++)
            {
                var face = extraction.faces[f];
                var faceUv = extraction.faceUvs[f];
                if (face == null || face.Length < 3 || faceUv == null || faceUv.Length != face.Length)
                {
                    faces.Add(null);
                    continue;
                }

                var remapped = new int[face.Length];
                for (var k = 0; k < face.Length; k++)
                {
                    var key = (face[k], faceUv[k]);
                    if (!map.TryGetValue(key, out var index))
                    {
                        index = outPositions.Count;
                        map.Add(key, index);
                        outPositions.Add(source[face[k]]);
                        outUvs.Add(faceUv[k]);
                        outShading?.Add(corners?[f] != null && k < corners[f].Length ? corners[f][k] : srcShading[face[k]]);
                        outNormals?.Add(srcNormals[face[k]]);
                        outTangents?.Add(srcTangents[f] != null && k < srcTangents[f].Length ? srcTangents[f][k] : new Vector4(1f, 0f, 0f, 1f));
                    }
                    remapped[k] = index;
                }
                faces.Add(remapped);
            }

            positions = outPositions.ToArray();
            uvs = outUvs.ToArray();
            shadingNormals = outShading?.ToArray();
            normals = outNormals?.ToArray();
            tangents = outTangents?.ToArray();
        }

        // ShouldFlip lived here and is deliberately gone rather than left unused. It decided winding
        // one face at a time from that face's own Newell normal, whose magnitude is twice the face
        // area — so on a sliver the sign it produced was noise. FaceOrienter replaces it with an
        // area-weighted vote per connected component. Keeping a shadow copy of the old test around
        // would invite it back into a call site.

        public static Vector3 FaceNormal(Vector3[] positions, int[] face)
        {
            // Newell's method: robust for non-planar polygons, which these often are.
            var normal = Vector3.zero;
            for (var i = 0; i < face.Length; i++)
            {
                var current = positions[face[i]];
                var next = positions[face[(i + 1) % face.Length]];
                normal.x += (current.y - next.y) * (current.z + next.z);
                normal.y += (current.z - next.z) * (current.x + next.x);
                normal.z += (current.x - next.x) * (current.y + next.y);
            }
            return normal;
        }

        /// <summary>Census taken before the hole filler, so its own contribution is visible.</summary>
        static void CensusAfterHoles(List<StageCensus> census, List<int[]> faces)
        {
            Census(census, "before holes", faces);
        }

        /// <summary>
        /// Counts the graph's irregular vertices. A vertex with fewer than three neighbours is
        /// treated as a boundary rather than as a singularity: it has no ring to have a valence in.
        /// </summary>
        static void Census(List<StageCensus> census, string stage, QuadGraph graph)
        {
            if (census == null) return;

            var row = new StageCensus { stage = stage };

            for (var v = 0; v < graph.capacity; v++)
            {
                if (!graph.alive[v]) continue;
                row.items++;

                var valence = graph.Valence(v);
                if (valence < 3) continue;

                row.interior++;
                if (valence == 4) continue;

                row.irregular++;
                if (valence == 3) row.valence3++;
                else if (valence == 5) row.valence5++;
                else row.valence6Plus++;
            }

            census.Add(row);
        }

        /// <summary>
        /// Counts irregular vertices of a face set. Boundary vertices are excluded: an open rim has
        /// a valence deficit that is not a singularity, and counting it hides the real number.
        /// </summary>
        static void Census(List<StageCensus> census, string stage, List<int[]> faces)
        {
            if (census == null) return;

            var row = new StageCensus { stage = stage, items = faces.Count };

            var valence = new Dictionary<int, int>();
            var uses = new Dictionary<long, int>(EdgeKey.Comparer);

            foreach (var face in faces)
            {
                if (face == null || face.Length < 3) continue;
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];

                    valence.TryGetValue(a, out var used);
                    valence[a] = used + 1;

                    var key = EdgeKey.PackUndirected(a, b);
                    uses.TryGetValue(key, out var count);
                    uses[key] = count + 1;
                }
            }

            var onBoundary = new HashSet<int>();
            foreach (var entry in uses)
            {
                if (entry.Value == 2) continue;
                onBoundary.Add(EdgeKey.First(entry.Key));
                onBoundary.Add(EdgeKey.Second(entry.Key));
            }

            foreach (var entry in valence)
            {
                if (onBoundary.Contains(entry.Key)) continue;
                row.interior++;
                if (entry.Value == 4) continue;

                row.irregular++;
                if (entry.Value == 3) row.valence3++;
                else if (entry.Value == 5) row.valence5++;
                else row.valence6Plus++;
            }

            census.Add(row);
        }

        public static Vector3 AverageNormal(Vector3[] normals, int[] face)
        {
            var sum = Vector3.zero;
            foreach (var index in face)
                sum += normals[index];
            return sum;
        }
    }
}
