// Models in parts: an imported model is usually a root over several meshes -- a car's body, doors
// and wheels, a helmet and its visor. Each part is remeshed on its own, the quad budget split by
// surface area, and the result keeps the model's hierarchy with one shared texture and normal map;
// or, with Combine Parts, the parts are joined into one mesh in the root's space and remeshed as one.

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace NativeRemesher
{
    internal sealed partial class NativeRemesherWindow
    {
        sealed class PartsRun
        {
            public List<RemeshResult> results;

            /// <summary>The shared bake (its textures and stats), or null.</summary>
            public RemeshResult shared;

            /// <summary>Per part, how much of it can be seen (Fewer Quads On Hidden Parts); null when not measured.</summary>
            public float[] visibility;

            /// <summary>Per part, the quads it was asked for.</summary>
            public int[] targets;
        }

        Task<PartsRun> m_PartsJob;
        GameObject m_JobRoot;
        List<GameObject> m_JobParts;
        List<Material[]> m_JobPartMaterials;

        void RunParts(GameObject root, List<GameObject> parts)
        {
            var settings = PrepareRun(out var profile, out var autoProfile);
            var bakeTexture = m_BakeTexture;
            var bakeNormals = m_BakeNormalMap;

            // Main thread: every part's arrays, materials and textures, and where it sits in the root.
            var images = new List<TextureBake.Image>();
            var read = new Dictionary<Texture, int>();
            var normalImages = new List<TextureBake.Image>();
            var normalRead = new Dictionary<Texture, int>();
            var inputs = new List<PartBake.Part>();
            var areas = new List<double>();
            var materials = new List<Material[]>();
            var submeshes = new List<int[]>();
            var rawUvs = new List<Vector2[]>();
            var paints = new List<DensityPaint>();
            var floors = new List<int>();
            var pieces = new List<int>();
            var occludes = new List<bool>();
            var toRoot = root.transform.worldToLocalMatrix;
            // The materials' other maps (smoothness, occlusion, ...): one layer per slot over all parts.
            var layerProperties = new List<string>();
            var allMaterials = new List<Material>();
            if (bakeTexture && bakeLayers)
            {
                foreach (var go in parts)
                    if (go.GetComponent<Renderer>() is Renderer r) allMaterials.AddRange(r.sharedMaterials);
                layerProperties = LayerProperties(allMaterials);
            }
            var layerImages = new List<TextureBake.Image>[layerProperties.Count];
            var layerRead = new Dictionary<Texture, int>[layerProperties.Count];
            for (var l = 0; l < layerProperties.Count; l++)
            {
                layerImages[l] = new List<TextureBake.Image>();
                layerRead[l] = new Dictionary<Texture, int>();
            }
            foreach (var go in parts)
            {
                RemeshTopology.ReadMesh(MeshOn(go), out var positions, out var triangles, out var uvs, out var submesh);
                QuadSource.Reorder(MeshOn(go), ref triangles, ref submesh);
                var renderer = go.GetComponent<Renderer>();
                var mats = renderer != null ? renderer.sharedMaterials : new Material[0];
                var textures = bakeTexture && uvs != null ? ReadTextures(mats, submesh, triangles.Length / 3, images, read) : null;
                var detail = bakeNormals && uvs != null
                    ? ReadTextures(mats, submesh, triangles.Length / 3, normalImages, normalRead, normalMaps: true)
                    : null;
                // A part with a normal map and no texture still needs its UVs, its triangles reading no colour.
                if (textures == null && detail != null)
                {
                    textures = new int[triangles.Length / 3];
                    for (var t = 0; t < textures.Length; t++) textures[t] = -1;
                }
                var triangleLayers = layerProperties.Count > 0 && textures != null ? new int[layerProperties.Count][] : null;
                for (var l = 0; triangleLayers != null && l < layerProperties.Count; l++)
                    triangleLayers[l] = ReadTextures(mats, submesh, triangles.Length / 3, layerImages[l], layerRead[l],
                        property: layerProperties[l]);
                var toModel = toRoot * go.transform.localToWorldMatrix;
                inputs.Add(new PartBake.Part
                {
                    positions = positions, triangles = triangles, toModel = toModel,
                    uvs = textures != null ? uvs : null, triangleTexture = textures, triangleNormalMap = detail,
                    triangleLayers = triangleLayers,
                });
                areas.Add(PartBake.Area(positions, triangles, toModel));
                pieces.Add(PartBake.Pieces(positions, triangles));
                floors.Add(PartBake.Floor(pieces[pieces.Count - 1], triangles.Length / 3));
                materials.Add(mats);
                occludes.Add(!SeeThrough(mats));
                submeshes.Add(submesh);
                rawUvs.Add(uvs);
                paints.Add(PaintFor(go));
            }

            // Parts sewn rim to rim into one surface (a scan saved as a ground and the branch lying in
            // it) are remeshed as one: apart, each rim gets its own points and the seam opens.
            var sewn = !m_CombineParts && sewnParts && inputs.Count > 1 && AllSewn(inputs);
            if (sewn)
                Debug.Log($"[Native Remesher] {inputs.Count} parts share their open rims: remeshed as one surface");
            if (m_CombineParts || sewn)
            {
                // One mesh in the root's space; submesh i of part p is slot (p's first slot + i).
                var positions = new List<Vector3>();
                var triangles = new List<int>();
                var uvs = new List<Vector2>();
                var submesh = new List<int>();
                var slots = new List<Material>();
                var anyUvs = false;
                for (var p = 0; p < inputs.Count; p++)
                {
                    var input = inputs[p];
                    var offset = positions.Count;
                    var firstSlot = slots.Count;
                    var mats = materials[p];
                    if (mats.Length == 0) slots.Add(null); else slots.AddRange(mats);
                    var partSlots = Mathf.Max(1, mats.Length);
                    foreach (var q in input.positions) positions.Add(input.toModel.MultiplyPoint3x4(q));
                    var partUvs = rawUvs[p];
                    var hasUvs = partUvs != null && partUvs.Length == input.positions.Length;
                    anyUvs |= hasUvs;
                    for (var i = 0; i < input.positions.Length; i++) uvs.Add(hasUvs ? partUvs[i] : Vector2.zero);
                    // A transform that mirrors (negative scale) turns the faces inside out: wound back.
                    var mirrored = input.toModel.determinant < 0f;
                    for (var t = 0; t + 2 < input.triangles.Length; t += 3)
                    {
                        triangles.Add(input.triangles[t] + offset);
                        triangles.Add(input.triangles[mirrored ? t + 2 : t + 1] + offset);
                        triangles.Add(input.triangles[mirrored ? t + 1 : t + 2] + offset);
                        var s = submeshes[p] != null && t / 3 < submeshes[p].Length ? submeshes[p][t / 3] : 0;
                        submesh.Add(firstSlot + Mathf.Min(s, partSlots - 1));
                    }
                }
                RunSingle(root, slots.ToArray(), positions.ToArray(), triangles.ToArray(), anyUvs ? uvs.ToArray() : null,
                    submesh.ToArray(), settings, profile, autoProfile);
                return;
            }

            var bakeResolution = TextureSizeFor(images);
            var hiddenFewer = m_HiddenPartsFewer;
            var names = new List<string>();
            foreach (var go in parts) names.Add(go.name);
            var current = Selection.activeGameObject;
            m_JobReplaces = current != null && current != root && SourceOf(current) == root ? current : null;
            m_JobRoot = root;
            m_JobParts = parts;
            m_JobPartMaterials = materials;
            m_JobStage = "Starting";
            m_JobProgress = 0f;
            m_JobWatch = System.Diagnostics.Stopwatch.StartNew();
            m_Cancel = new System.Threading.CancellationTokenSource();
            var token = m_Cancel.Token;
            var imageArray = images.ToArray();
            var normalImageArray = normalImages.ToArray();
            var layerList = new List<TextureBake.Layer>();
            for (var l = 0; l < layerProperties.Count; l++)
                if (layerImages[l].Count > 0)
                    layerList.Add(NewLayer(allMaterials, layerProperties[l], layerImages[l].ToArray(), null));
                else
                    layerList.Add(null);
            // Parts index layers by slot: an empty one stays in place, baked as nothing.
            var layerArray = layerList.Exists(x => x != null) ? layerList.ToArray() : null;
            if (layerArray != null)
                for (var l = 0; l < layerArray.Length; l++)
                    layerArray[l] ??= new TextureBake.Layer { property = layerProperties[l], images = new TextureBake.Image[0] };

            s_Scheduler ??= new BackgroundScheduler();
            m_PartsJob = s_Scheduler.Run(() =>
            {
                var run = new PartsRun { results = new List<RemeshResult>() };
                var n = inputs.Count;
                // The budget: by area, or by area and how much of each part can be seen.
                var weighted = new List<double>(areas);
                var partFloors = new List<int>(floors);
                if (hiddenFewer)
                {
                    m_JobStage = "Seeing which parts are hidden";
                    run.visibility = PartBake.Visibility(inputs, occludes, onProgress: progress =>
                    {
                        token.ThrowIfCancellationRequested();
                        m_JobProgress = progress / (n + 1);
                    });
                    for (var p = 0; p < n; p++)
                    {
                        var weight = PartBake.VisibilityWeight(run.visibility[p]);
                        weighted[p] = areas[p] * weight;
                        // The least a part's pieces need, scaled down too, but not under 8 a piece.
                        partFloors[p] = Mathf.Max(16, Mathf.RoundToInt(floors[p] * Mathf.Max(weight, 0.2f)));
                    }
                }
                var watch = m_JobWatch;
                void Timing(string stage)
                {
                    if (QuadReduction.logStats) Debug.Log($"[Native Remesher] {stage} by {watch.ElapsedMilliseconds} ms");
                }
                Timing("visibility");
                // Thin parts (a windscreen's surround, seals, trims): at least enough quads to go
                // round them at the model's cell size. By area the Ford's windscreen surround, a
                // closed band 44 cells long, got 48 quads, and its sides were merged into a blade.
                var thinNeed = new int[n];
                // The model's edge length at its budget, were its quads square and spread by area.
                double areaSum = 0;
                foreach (var a in areas) areaSum += a;
                var cell = (float)System.Math.Sqrt(areaSum / System.Math.Max(1, settings.targetQuadCount));
                if (thinFloors)
                {
                    var thin = new int[n];
                    var thinCells = new int[n];
                    var thinCoarse = new int[n];
                    System.Threading.Tasks.Parallel.For(0, n, BackgroundScheduler.Options, p =>
                    {
                        thinCells[p] = PartBake.Cells(inputs[p].positions, inputs[p].triangles, inputs[p].toModel, cell, out var closed, out thinCoarse[p]);
                        thin[p] = PartBake.ThinFloor(thinCells[p], thinCoarse[p], areas[p], cell, closed);
                    });
                    for (var p = 0; p < n; p++)
                    {
                        // Scaled by how much of it can be seen, as the other floors are.
                        var weight = run.visibility != null ? Mathf.Max(PartBake.VisibilityWeight(run.visibility[p]), 0.2f) : 1f;
                        thinNeed[p] = Mathf.RoundToInt(thin[p] * weight);
                    }
                    if (QuadReduction.logStats)
                    {
                        var count = 0; long sum = 0;
                        for (var p = 0; p < n; p++) if (thinNeed[p] > 0) { count++; sum += thinNeed[p]; Debug.Log($"[Native Remesher] thin part '{names[p]}': {thin[p]} quads to go round, {thinNeed[p]} as seen ({thinCells[p]} cells, {thinCoarse[p]} at twice, area {areas[p] / ((double)cell * cell * thinCells[p]):0.##} of theirs)"); }
                        Debug.Log($"[Native Remesher] thin parts: {count}, {sum} quads to go round them, cell {cell:0.###}");
                    }
                    Timing("thin parts");
                }
                // The floors together at most a share of the budget: held at 20 quads a piece, the
                // Ford's grille, licence plates and badges (letters, bars: hundreds of pieces) took
                // 9,500 of its 26,000 faces and left its body 374.
                long floorSum = 0;
                foreach (var f in partFloors) floorSum += f;
                var floorBudget = (long)(floorShare * settings.targetQuadCount);
                if (floorShare > 0f && floorSum > floorBudget && floorSum > 16L * n)
                {
                    var scale = (double)(floorBudget - 16L * n) / System.Math.Max(1, floorSum - 16L * n);
                    for (var p = 0; p < n; p++)
                        partFloors[p] = 16 + (int)System.Math.Round(System.Math.Max(0, partFloors[p] - 16) * System.Math.Max(0.0, scale));
                }
                var targets = PartBake.SplitTarget(weighted, settings.targetQuadCount, 16, partFloors);
                run.targets = targets;

                // Parts that already are clean quads (modelled parts) are reduced together by quad
                // strips, at one cell size over the whole model: a flat floor pan gives its quads to a
                // wheel arch. Each part's share above anchors it (QuadReduction.ReduceParts).
                // Turned surfaces (tyres, a rim's barrel, hubs) are built as their profile turned:
                // found first, and kept out of the strips and the collapse.
                var turned = new Revolution.Fit[n];
                var turnedPieces = new Revolution.Pieces[n];
                if (Revolution.enabled && settings.symmetry == SymmetryAxis.None)
                {
                    m_JobStage = "Looking for turned parts";
                    System.Threading.Tasks.Parallel.For(0, n, BackgroundScheduler.Options, p =>
                    {
                        if (paints[p] == null) turned[p] = Revolution.Find(inputs[p].positions, inputs[p].triangles);
                        if (!Revolution.Accept(turned[p], targets[p])) turned[p] = null;
                        if (turned[p] == null && paints[p] == null && Revolution.pieces)
                            turnedPieces[p] = Revolution.FindPieces(inputs[p].positions, inputs[p].triangles);
                    });
                    Timing("turned parts");
                }
                var strips = new QuadReduction.Part[n];
                var types = new ModelType[n];
                if (RemeshPipeline.quadInputReduction && settings.symmetry == SymmetryAxis.None)
                {
                    m_JobStage = "Reducing the quads";
                    var pool = new List<QuadReduction.Part>();
                    for (var p = 0; p < n; p++)
                    {
                        types[p] = settings.modelType == ModelType.Auto
                            ? ShapeRecognition.Classify(inputs[p].positions, inputs[p].triangles, out _)
                            : settings.modelType;
                        if (!RemeshPipeline.StripsFor(types[p]) || turned[p] != null || turnedPieces[p] != null || thinNeed[p] > targets[p]) continue;
                        var model = new Vector3[inputs[p].positions.Length];
                        for (var i = 0; i < model.Length; i++) model[i] = inputs[p].toModel.MultiplyPoint3x4(inputs[p].positions[i]);
                        strips[p] = new QuadReduction.Part
                        {
                            positions = model, triangles = inputs[p].triangles, target = targets[p],
                            weight = run.visibility != null ? PartBake.VisibilityWeight(run.visibility[p]) : 1f,
                        };
                        pool.Add(strips[p]);
                    }
                    QuadReduction.ReduceParts(pool, progress =>
                    {
                        token.ThrowIfCancellationRequested();
                        m_JobProgress = progress / (n + 1);
                    });
                    Timing("quad strips");
                    if (QuadReduction.logStats) Debug.Log($"[Native Remesher] quad strips: {QuadReduction.lastTimings}");
                    for (var p = 0; p < n; p++)
                        if (strips[p] != null && QuadReduction.logStats)
                            Debug.Log($"[Native Remesher] {names[p]}: {(strips[p].reduced ? "strips" : "remesher")} {strips[p].stats}");
                }
                // Copies of a part (the Ford's four tyres, the Porsche's two 554k-triangle wheels):
                // the same triangles and one map between their vertices. Solved once, moved into place.
                var twin = new int[n];
                var twinMap = new Matrix4x4[n];
                // A copy looks the same as its original (it can share its baked charts) when it
                // reads the same textures at the same UVs through the same materials.
                bool SameLook(int a, int b)
                {
                    bool Same<T>(T[] x, T[] y) => x == y || x != null && y != null && x.Length == y.Length
                        && System.Linq.Enumerable.SequenceEqual(x, y);
                    if (!Same(materials[a], materials[b]) || !Same(rawUvs[a], rawUvs[b]) || !Same(submeshes[a], submeshes[b])) return false;
                    if (!Same(inputs[a].triangleTexture, inputs[b].triangleTexture) || !Same(inputs[a].triangleNormalMap, inputs[b].triangleNormalMap)) return false;
                    var la = inputs[a].triangleLayers; var lb = inputs[b].triangleLayers;
                    if ((la == null) != (lb == null)) return false;
                    for (var l = 0; la != null && l < la.Length; l++) if (l >= lb.Length || !Same(la[l], lb[l])) return false;
                    return true;
                }
                for (var p = 0; p < n; p++)
                {
                    twin[p] = -1;
                    if (!sameParts) continue;
                    for (var q = 0; q < p; q++)
                        if (twin[q] < 0 && RemeshPipeline.SameShape(inputs[q].positions, inputs[q].triangles,
                                inputs[p].positions, inputs[p].triangles, out twinMap[p]))
                        {
                            twin[p] = q;
                            break;
                        }
                }
                for (var p = 0; p < n; p++)
                {
                    var index = p;
                    void Progress(string stage, float progress)
                    {
                        token.ThrowIfCancellationRequested();
                        m_JobStage = $"Part {index + 1}/{n} '{names[index]}': {stage}";
                        m_JobProgress = (index + Mathf.Clamp01(progress)) / (n + 1);
                    }
                    var partSettings = settings;
                    partSettings.targetQuadCount = targets[p];
                    partSettings.densityPaint = paints[p];
                    // One part failing (a throw, or nothing out) does not cost the model its other parts:
                    // it is tried again with four times the quads, then left as it was.
                    RemeshResult solved = null;
                    var q0 = twin[p];
                    if (q0 >= 0 && q0 < run.results.Count && run.results[q0]?.extraction != null
                        && !(strips[p] != null && strips[p].reduced) && !(strips[q0] != null && strips[q0].reduced)
                        && targets[p] <= 2 * targets[q0] && targets[q0] <= 2 * targets[p])
                    {
                        solved = RemeshPipeline.Moved(run.results[q0], twinMap[p]);
                        if (solved?.extraction != null)
                        {
                            if (SameLook(p, q0))
                            {
                                inputs[p].sameAs = q0;
                                inputs[p].sameMirrored = twinMap[p].determinant < 0f;
                            }
                            inputs[p].result = solved;
                            run.results.Add(solved);
                            Timing($"part '{names[p]}' (copy of '{names[q0]}', {solved.extraction.faces.Count} faces)");
                            continue;
                        }
                        solved = null;
                    }
                    // The model's cell in the part's own space.
                    var partScale = Mathf.Pow(Mathf.Abs(inputs[p].toModel.determinant), 1f / 3f);
                    var partCell = partScale > 0f ? cell / partScale : 0f;
                    if (turned[p] != null)
                    {
                        Progress("Turned", 0f);
                        solved = RemeshPipeline.Revolved(inputs[p].positions, inputs[p].triangles, turned[p], partSettings,
                            profile, rawUvs[p], submeshes[p], cell: partCell);
                        if (solved?.extraction != null && solved.extraction.faces.Count > 0)
                        {
                            inputs[p].result = solved;
                            run.results.Add(solved);
                            Timing($"part '{names[p]}' (turned, {solved.extraction.faces.Count} faces, {turned[p]})");
                            continue;
                        }
                        solved = null;
                    }
                    if (turnedPieces[p] != null)
                    {
                        Progress("Turned by pieces", 0f);
                        solved = RemeshPipeline.RevolvedPieces(inputs[p].positions, inputs[p].triangles, turnedPieces[p], partSettings,
                            profile, rawUvs[p], submeshes[p], cell: partCell);
                        if (solved?.extraction != null && solved.extraction.faces.Count > 0)
                        {
                            inputs[p].result = solved;
                            run.results.Add(solved);
                            Timing($"part '{names[p]}' (turned by pieces, {solved.extraction.faces.Count} faces, {turnedPieces[p]})");
                            continue;
                        }
                        solved = null;
                    }
                    // Thinner than a cell and given fewer quads than go round it: collapsed and paired
                    // at what does. Strips at the model's cell size merge a band's sides into a
                    // blade (the Ford's windscreen surround), and the remesher misses thin loops.
                    if (thinNeed[p] > targets[p])
                    {
                        Progress("Collapsed (thin)", 0f);
                        RemeshPipeline.CollapseToQuads(inputs[p].positions, inputs[p].triangles, thinNeed[p],
                            out var thinPositions, out var thinFaces);
                        partSettings.modelType = types[p];
                        solved = RemeshPipeline.FromQuadReduction(inputs[p].positions, inputs[p].triangles,
                            thinPositions, thinFaces, null, partSettings, profile, rawUvs[p], submeshes[p]);
                        if (solved?.extraction != null && solved.extraction.faces.Count > 0)
                        {
                            inputs[p].result = solved;
                            run.results.Add(solved);
                            Timing($"part '{names[p]}' (thin, collapsed at {thinNeed[p]} for {targets[p]}, {solved.extraction.faces.Count} faces)");
                            continue;
                        }
                        solved = null;
                    }
                    if (strips[p] != null && strips[p].reduced)
                    {
                        Progress("Reduced with its quads", 0f);
                        var toPart = PartBake.InverseAffine(inputs[p].toModel);
                        var back = new Vector3[strips[p].outPositions.Length];
                        for (var i = 0; i < back.Length; i++) back[i] = toPart.MultiplyPoint3x4(strips[p].outPositions[i]);
                        partSettings.modelType = types[p];
                        solved = RemeshPipeline.FromQuadReduction(inputs[p].positions, inputs[p].triangles, back,
                            strips[p].outFaces, strips[p].stats, partSettings, profile, rawUvs[p], submeshes[p]);
                    }
                    // A part that can hardly be seen and whose strips ran out, or one of more pieces
                    // than its quads can close (a licence plate's letters, a grille's bars):
                    // collapsed and paired, in a second, every piece kept, rather than remeshed to
                    // a handful of quads.
                    var collapsed = false;
                    var manyPieces = piecesCollapse > 0 && pieces[p] * piecesCollapse > targets[p];
                    if (solved == null && strips[p] != null && run.visibility != null
                        && PartBake.VisibilityWeight(run.visibility[p]) < hiddenCollapseWeight
                        || solved == null && manyPieces)
                    {
                        Progress(manyPieces ? "Collapsed (many pieces)" : "Collapsed (hidden)", 0f);
                        RemeshPipeline.CollapseToQuads(inputs[p].positions, inputs[p].triangles, targets[p],
                            out var collapsedPositions, out var collapsedFaces);
                        partSettings.modelType = types[p];
                        solved = RemeshPipeline.FromQuadReduction(inputs[p].positions, inputs[p].triangles,
                            collapsedPositions, collapsedFaces, strips[p]?.stats, partSettings, profile, rawUvs[p], submeshes[p]);
                        collapsed = solved?.extraction != null && solved.extraction.faces.Count > 0;
                    }
                    for (var attempt = 0; attempt < 2 && (solved?.extraction == null || solved.extraction.faces.Count == 0); attempt++)
                    {
                        partSettings.targetQuadCount = attempt == 0 ? targets[p] : targets[p] * 4;
                        try
                        {
                            solved = strips[p] != null
                                ? RemeshPipeline.SolveWithoutStrips(inputs[p].positions, inputs[p].triangles, partSettings,
                                    profile, Progress, rawUvs[p], autoProfile, submeshes[p])
                                : RemeshPipeline.Solve(inputs[p].positions, inputs[p].triangles, partSettings, profile,
                                    Progress, rawUvs[p], autoProfile, submeshes[p]);
                        }
                        catch (System.Exception e) when (!(e is System.OperationCanceledException))
                        {
                            Debug.LogWarning($"[Native Remesher] Part '{names[p]}' at {partSettings.targetQuadCount} quads: {e}");
                            solved = null;
                        }
                    }
                    // A part on view whose remesh lost its shape (a piece gone, a ring folded flat)
                    // gets the closer of two others: collapsed and paired, which follows the shape
                    // whatever it is (the window seals, thin loops the remesher missed by 40 quad
                    // widths), and, on a small budget, remeshed with three times the quads (the
                    // headlights at 31 quads came out 26 widths off, at 120 under one).
                    if (!collapsed && solved?.extraction != null && solved.extraction.faces.Count > 0
                        && !(strips[p] != null && strips[p].reduced)
                        && (run.visibility == null || PartBake.VisibilityWeight(run.visibility[p]) >= hiddenCollapseWeight))
                    {
                        var off = RemeshPipeline.CellsOff(inputs[p].positions, inputs[p].triangles, solved.extraction);
                        if (off > retryCellsOff)
                        {
                            var note = $"{off:0.0} widths off at {solved.extraction.faces.Count} faces";
                            RemeshPipeline.CollapseToQuads(inputs[p].positions, inputs[p].triangles, targets[p],
                                out var cp, out var cf);
                            partSettings.modelType = types[p];
                            var byCollapse = RemeshPipeline.FromQuadReduction(inputs[p].positions, inputs[p].triangles,
                                cp, cf, strips[p]?.stats, partSettings, profile, rawUvs[p], submeshes[p]);
                            var offCollapse = byCollapse?.extraction != null && byCollapse.extraction.faces.Count > 0
                                ? RemeshPipeline.CellsOff(inputs[p].positions, inputs[p].triangles, byCollapse.extraction)
                                : float.PositiveInfinity;
                            note += $"; collapsed {offCollapse:0.0} at {byCollapse?.extraction?.faces.Count ?? 0}";
                            RemeshResult again = null;
                            var offAgain = float.PositiveInfinity;
                            if (targets[p] <= retryBudget)
                            {
                                partSettings.targetQuadCount = targets[p] * 3;
                                try
                                {
                                    again = strips[p] != null
                                        ? RemeshPipeline.SolveWithoutStrips(inputs[p].positions, inputs[p].triangles, partSettings,
                                            profile, Progress, rawUvs[p], autoProfile, submeshes[p])
                                        : RemeshPipeline.Solve(inputs[p].positions, inputs[p].triangles, partSettings, profile,
                                            Progress, rawUvs[p], autoProfile, submeshes[p]);
                                    if (again?.extraction != null && again.extraction.faces.Count > 0)
                                        offAgain = RemeshPipeline.CellsOff(inputs[p].positions, inputs[p].triangles, again.extraction);
                                }
                                catch (System.Exception e) when (!(e is System.OperationCanceledException))
                                {
                                    Debug.LogWarning($"[Native Remesher] Part '{names[p]}' at {partSettings.targetQuadCount} quads: {e}");
                                }
                                note += $"; remeshed {offAgain:0.0} at {again?.extraction?.faces.Count ?? 0}";
                            }
                            // Quads when the bigger remesh holds the shape; else whichever is closer.
                            if (offAgain <= retryCellsOff || offAgain < off && offAgain <= offCollapse) solved = again;
                            else if (offCollapse < off) { solved = byCollapse; collapsed = true; }
                            if (QuadReduction.logStats) Debug.Log($"[Native Remesher] part '{names[p]}' {note}");
                        }
                    }
                    if (solved?.extraction == null || solved.extraction.faces.Count == 0) solved = null;
                    inputs[p].result = solved;
                    run.results.Add(solved);
                    Timing($"part '{names[p]}' ({(strips[p] != null && strips[p].reduced ? "strips" : collapsed ? "collapsed" : "remesher")}, " +
                           $"{solved?.extraction?.faces.Count ?? 0} faces, visible {(run.visibility != null ? run.visibility[p] : 1f):0.00})");
                }
                Timing("parts");
                if (PartBake.texelsBySight && run.visibility != null)
                    for (var p = 0; p < n; p++)
                        inputs[p].texelScale = Mathf.Sqrt(PartBake.VisibilityWeight(run.visibility[p]));
                if (PartBake.texelsByDetail)
                {
                    var density = new double[n];
                    for (var p = 0; p < n; p++) density[p] = areas[p] > 0 ? inputs[p].triangles.Length / 3 / areas[p] : 0;
                    var median = PartBake.AreaMedian(density, areas);
                    for (var p = 0; p < n; p++)
                        inputs[p].texelScale *= PartBake.DetailTexelScale(density[p], median);
                }
                if (bakeTexture && imageArray.Length > 0 || bakeNormals)
                    run.shared = PartBake.Bake(inputs, imageArray, bakeTexture && imageArray.Length > 0, bakeNormals,
                        bakeResolution, (stage, progress) =>
                        {
                            token.ThrowIfCancellationRequested();
                            m_JobStage = $"All parts: {stage}";
                            m_JobProgress = (n + Mathf.Clamp01(progress)) / (n + 1);
                        }, normalImageArray.Length > 0 ? normalImageArray : null, layerArray);
                Timing("bake");
                return run;
            }, token);

            EditorApplication.update += OnJobUpdate;
        }

        void OnPartsJobUpdate()
        {
            if (!m_PartsJob.IsCompleted)
            {
                Repaint();
                return;
            }
            var job = m_PartsJob;
            EditorApplication.update -= OnJobUpdate;
            m_PartsJob = null;
            m_Cancel?.Dispose();
            m_Cancel = null;
            m_JobWatch?.Stop();
            if (job.IsCanceled || job.IsFaulted && job.Exception?.GetBaseException() is System.OperationCanceledException)
                m_Status = "Cancelled.";
            else if (job.IsFaulted)
            {
                var inner = job.Exception?.GetBaseException();
                m_Status = "Failed: " + (inner?.Message ?? "unknown error");
                if (inner != null) Debug.LogException(inner);
            }
            else
            {
                try { FinishParts(job.Result); }
                catch (System.Exception e)
                {
                    m_Status = "Failed: " + e.Message;
                    Debug.LogException(e);
                }
            }
            m_JobRoot = null;
            m_JobParts = null;
            m_JobPartMaterials = null;
            Repaint();
        }

        /// <summary>The main-thread half: the parts' meshes saved and put in the scene under one root.</summary>
        /// <summary>
        /// A part whose visibility weight (PartBake.VisibilityWeight) is under this, and whose
        /// strips ran out, is collapsed and paired instead of remeshed (RemeshPipeline.CollapseToQuads).
        /// </summary>
        public static float hiddenCollapseWeight = 0.35f;

        /// <summary>A remeshed part on view further than this many of its quads' widths from its source is solved again with more.</summary>
        public static float retryCellsOff = 2f;

        /// <summary>Only a part with at most this many quads is remeshed again with three times them (a larger one costs too long).</summary>
        public static int retryBudget = 120;

        /// <summary>A part thinner than the model's cell size is given at least enough quads to go round it (PartBake.ThinFloor); false: by area, as before.</summary>
        public static bool thinFloors = true;

        /// <summary>The parts' floors (PartBake.Floor) together take at most this share of the quads; 0: uncapped, as before.</summary>
        public static float floorShare = 0.25f;

        /// <summary>A part given fewer than this many quads a piece is collapsed rather than remeshed; 0: never.</summary>
        public static int piecesCollapse = 4;

        /// <summary>A copy of an earlier part (SameShape) takes that part's result, moved; false: every part solved.</summary>
        public static bool sameParts = true;

        /// <summary>
        /// Parts whose open rims are each other's (every part joined, through rims it shares for at
        /// least <see cref="sewnShare"/> of its own open edges) are remeshed as one surface, as Combine
        /// Parts would; false: each part on its own, as before.
        /// </summary>
        public static bool sewnParts = true;

        /// <summary>The share of a part's open edges another part must also have for the two to count as sewn.</summary>
        public static float sewnShare = 0.5f;

        /// <summary>
        /// True when the parts, placed in the root, join into one surface along shared open rims: an
        /// open edge of one part lying on an open edge of another, for at least <see cref="sewnShare"/>
        /// of either part's open edges, and every part reached that way.
        /// </summary>
        /// <summary>
        /// A weld cell's hash that mixes: the cells are large, structured lattice coordinates, which
        /// a plain shift-and-xor hash (Vector3Int's, the tuple's) piles into long collision chains.
        /// </summary>
        sealed class WeldCellComparer : IEqualityComparer<(int, int, int)>
        {
            public static readonly WeldCellComparer instance = new WeldCellComparer();

            public bool Equals((int, int, int) a, (int, int, int) b) => a == b;

            public int GetHashCode((int, int, int) c)
            {
                unchecked
                {
                    var h = (ulong)(uint)c.Item1 * 0x9E3779B97F4A7C15UL;
                    h = (h ^ (uint)c.Item2) * 0xC2B2AE3D27D4EB4FUL;
                    h = (h ^ (uint)c.Item3) * 0x165667B19E3779F9UL;
                    h ^= h >> 29;
                    return (int)(h ^ (h >> 32));
                }
            }
        }

        /// <summary>
        /// An edge's hash (two point ids in one long) that mixes: long's own XORs the halves, and two
        /// neighbouring points' ids XOR to a handful of small numbers, so the edges share a few buckets.
        /// </summary>
        sealed class WeldEdgeComparer : IEqualityComparer<long>
        {
            public static readonly WeldEdgeComparer instance = new WeldEdgeComparer();

            public bool Equals(long a, long b) => a == b;

            public int GetHashCode(long key)
            {
                unchecked
                {
                    var h = (ulong)key * 0x9E3779B97F4A7C15UL;
                    h ^= h >> 31;
                    h *= 0xC2B2AE3D27D4EB4FUL;
                    return (int)(h ^ (h >> 32));
                }
            }
        }

        static bool AllSewn(List<PartBake.Part> inputs)
        {
            var n = inputs.Count;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            var placed = new Vector3[n][];
            for (var p = 0; p < n; p++)
            {
                var input = inputs[p];
                placed[p] = new Vector3[input.positions.Length];
                for (var i = 0; i < placed[p].Length; i++)
                {
                    var q = input.toModel.MultiplyPoint3x4(input.positions[i]);
                    placed[p][i] = q;
                    min = Vector3.Min(min, q);
                    max = Vector3.Max(max, q);
                }
            }
            if (!(max.x >= min.x)) return false;
            // The same weld the topology uses (RemeshTopology.Build): a millionth of the diagonal.
            var epsilon = Mathf.Max((max - min).magnitude * 1e-6f, 1e-9f);
            var cells = new Dictionary<(int, int, int), int>(WeldCellComparer.instance);
            int Cell(Vector3 q)
            {
                var c = (Mathf.RoundToInt(q.x / epsilon), Mathf.RoundToInt(q.y / epsilon),
                    Mathf.RoundToInt(q.z / epsilon));
                if (!cells.TryGetValue(c, out var id)) cells.Add(c, id = cells.Count);
                return id;
            }

            // Every part's open edges, as pairs of welded points shared by all the parts.
            var open = new List<long>[n];
            var owner = new Dictionary<long, int>(WeldEdgeComparer.instance);
            var shared = new Dictionary<(int, int), int>();
            for (var p = 0; p < n; p++)
            {
                var ids = new int[placed[p].Length];
                for (var i = 0; i < ids.Length; i++) ids[i] = Cell(placed[p][i]);
                var uses = new Dictionary<long, int>(WeldEdgeComparer.instance);
                var t = inputs[p].triangles;
                for (var k = 0; k + 2 < t.Length; k += 3)
                    for (var e = 0; e < 3; e++)
                    {
                        int a = ids[t[k + e]], b = ids[t[k + (e + 1) % 3]];
                        if (a == b) continue;
                        var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                        uses.TryGetValue(key, out var u);
                        uses[key] = u + 1;
                    }
                open[p] = new List<long>();
                foreach (var kv in uses)
                {
                    if (kv.Value != 1) continue;
                    open[p].Add(kv.Key);
                    if (owner.TryGetValue(kv.Key, out var o))
                    {
                        var pair = (o, p);
                        shared.TryGetValue(pair, out var s);
                        shared[pair] = s + 1;
                    }
                    else owner[kv.Key] = p;
                }
            }

            var root = new int[n];
            for (var p = 0; p < n; p++) root[p] = p;
            int Find(int p) { while (root[p] != p) p = root[p] = root[root[p]]; return p; }
            foreach (var kv in shared)
            {
                var (a, b) = kv.Key;
                if (kv.Value < sewnShare * Mathf.Min(open[a].Count, open[b].Count)) continue;
                root[Find(a)] = Find(b);
            }
            for (var p = 1; p < n; p++)
                if (Find(p) != Find(0)) return false;
            return true;
        }

        void FinishParts(PartsRun run)
        {
            var root = m_JobRoot;
            var parts = m_JobParts;
            if (root == null)
            {
                m_Status = "The source object was destroyed while the remesh was running.";
                return;
            }

            var safe = string.Join("_", root.name.Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safe)) safe = "Model";
            string folder = null;
            if (m_SaveAsAsset)
            {
                if (!AssetDatabase.IsValidFolder(k_Folder))
                {
                    if (!AssetDatabase.IsValidFolder("Assets/NativeRemesher"))
                        AssetDatabase.CreateFolder("Assets", "NativeRemesher");
                    AssetDatabase.CreateFolder("Assets/NativeRemesher", "Output");
                }
                // A folder per run: the parts' meshes, the shared textures and the materials together.
                var path = AssetDatabase.GenerateUniqueAssetPath($"{k_Folder}/{safe}_Quads");
                var guid = AssetDatabase.CreateFolder(k_Folder, Path.GetFileName(path));
                folder = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
            }

            var undoGroup = Undo.GetCurrentGroup();
            var top = new GameObject(root.name + "_Quads");
            Undo.RegisterCreatedObjectUndo(top, "Native Remesh");
            top.transform.SetParent(root.transform.parent, false);
            top.transform.localPosition = root.transform.localPosition;
            top.transform.localRotation = root.transform.localRotation;
            top.transform.localScale = root.transform.localScale;
            top.AddComponent<RemeshSource>().source = root;

            // The model's hierarchy between the root and each part, mirrored under the result.
            var mirror = new Dictionary<Transform, Transform> { [root.transform] = top.transform };
            Transform Mirror(Transform t)
            {
                if (mirror.TryGetValue(t, out var m)) return m;
                var parent = Mirror(t.parent);
                var node = new GameObject(t.name).transform;
                node.SetParent(parent, false);
                node.localPosition = t.localPosition;
                node.localRotation = t.localRotation;
                node.localScale = t.localScale;
                mirror[t] = node;
                return node;
            }

            Texture2D baked = null, normalMap = null, occlusion = null;
            Dictionary<string, Texture2D> sharedLayers = null;
            if (run.shared != null)
            {
                var sharedPath = folder != null ? $"{folder}/{safe}_Quads.asset" : "";
                if (run.shared.bakedTexture != null) baked = SaveBakedTexture(run.shared, sharedPath, root.name);
                if (run.shared.bakedNormalMap != null) normalMap = SaveNormalMap(run.shared, sharedPath, root.name);
                sharedLayers = SaveLayers(run.shared, sharedPath, root.name);
                if (run.shared.bakedOcclusion != null) occlusion = SaveOcclusion(run.shared, sharedPath, root.name);
            }
            var made = new Dictionary<Material, Material>();
            Material blank = null;
            Material Baked(Material from)
            {
                if (baked == null && normalMap == null) return from;
                if (from == null && blank != null) return blank;
                if (from != null && made.TryGetValue(from, out var done)) return done;
                var mat = from != null ? new Material(from) : new Material(Shader.Find("Standard"));
                mat.name = (from != null ? from.name : root.name) + "_Quads";
                if (baked != null) mat.mainTexture = baked;
                else if (mat.HasProperty("_MainTex") || mat.HasProperty("_BaseMap")) mat.mainTexture = null;
                if (normalMap != null) AssignNormalMap(mat, normalMap);
                AssignLayers(mat, sharedLayers);
                AssignOcclusion(mat, occlusion);
                if (folder != null)
                {
                    var matName = string.Join("_", mat.name.Split(Path.GetInvalidFileNameChars()));
                    AssetDatabase.CreateAsset(mat, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{matName}.mat"));
                }
                if (from != null) made[from] = mat; else blank = mat;
                return mat;
            }

            long faces = 0, quads = 0, ms = 0;
            var unchanged = new List<string>();
            for (var p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                var result = p < run.results.Count ? run.results[p] : null;
                if (part == null) continue;
                if (result?.extraction == null)
                {
                    // Not remeshed: the part as it was, so the model stays whole.
                    var kept = Mirror(part.transform).gameObject;
                    kept.AddComponent<MeshFilter>().sharedMesh = MeshOn(part);
                    if (kept != top) kept.AddComponent<RemeshSource>().source = part;
                    kept.AddComponent<MeshRenderer>().sharedMaterials = m_JobPartMaterials[p];
                    unchanged.Add(part.name);
                    continue;
                }
                RemeshPipeline.BuildOutputMesh(result, part.name, m_QuadTopology);
                var outputMesh = result.mesh;
                if (folder != null)
                {
                    var assetPath = SaveMeshAsset(result, outputMesh, part.name, folder);
                    if (!string.IsNullOrEmpty(assetPath)) outputMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                }
                var node = Mirror(part.transform).gameObject;
                node.AddComponent<MeshFilter>().sharedMesh = outputMesh;
                if (node != top) node.AddComponent<RemeshSource>().source = part;
                var renderer = node.AddComponent<MeshRenderer>();
                var slots = Mathf.Max(1, outputMesh != null ? outputMesh.subMeshCount : 1);
                var source = m_JobPartMaterials[p];
                var first = source.Length > 0 ? source[0] : null;
                var perMaterial = result.extraction.faceMaterials != null;
                var mats = new Material[slots];
                for (var i = 0; i < slots; i++)
                    mats[i] = Baked(perMaterial && i < source.Length ? source[i] : first);
                renderer.sharedMaterials = mats;
                faces += result.extraction.faces.Count;
                quads += result.extraction.quadCount;
                ms += result.totalMs;
            }
            if (baked != null || normalMap != null) AssetDatabase.SaveAssets();
            if (m_JobReplaces != null)
            {
                Undo.RecordObject(m_JobReplaces, "Native Remesh");
                m_JobReplaces.SetActive(false);
                m_JobReplaces = null;
            }
            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = top;
            m_LastMade = top;
            if (m_MakeLods) MakeLods(top);

            m_Extraction = null;
            m_LastResult = run.results.Count > 0 ? run.results[0] : null;
            var bakeNote = run.shared?.bakeStats != null
                ? $"\none shared {run.shared.bakeStats.resolution}px bake for all parts, {run.shared.bakeStats.charts} charts, {run.shared.bakeStats.fill:P0} used"
                : "";
            var saved = folder != null ? $"\nsaved to {folder}" : "";
            var keptNote = unchanged.Count > 0
                ? $"\n{unchanged.Count} part(s) could not be remeshed and were kept as they were: {string.Join(", ", unchanged)}"
                : "";
            // What Auto read the parts as, and where the quads went.
            var types = new SortedDictionary<string, int>();
            foreach (var r in run.results)
            {
                if (r == null || r.modelType == ModelType.General) continue;
                var name = TypeName(r.modelType);
                types[name] = types.TryGetValue(name, out var c) ? c + 1 : 1;
            }
            var typeList = new List<string>();
            foreach (var kv in types) typeList.Add($"{kv.Value} {kv.Key}");
            m_TypeNote = typeList.Count > 0 ? "last run read the parts as " + string.Join(", ", typeList) : "";
            var hiddenNote = "";
            if (run.visibility != null)
            {
                var hidden = new List<string>();
                for (var p = 0; p < parts.Count && p < run.visibility.Length; p++)
                    if (run.visibility[p] < 0.2f) hidden.Add($"{parts[p].name} ({run.targets[p]})");
                if (hidden.Count > 0)
                    hiddenNote = $"\nhidden parts, given fewer quads: {string.Join(", ", hidden)}";
            }
            // The whole run (strips, visibility, bake), not only the parts' own solves.
            var seconds = m_JobWatch != null ? m_JobWatch.Elapsed.TotalSeconds : ms / 1000.0;
            m_Status = $"{parts.Count} parts: {faces:N0} faces ({quads:N0} quads) in {seconds:F1} s{saved}{bakeNote}{keptNote}{hiddenNote}";
            Debug.Log($"[Native Remesher] {root.name}: {m_Status.Replace('\n', ' ')}", top);
            SceneView.RepaintAll();
        }

        /// <summary>
        /// A part seen through, which does not hide what is behind it: every material transparent
        /// (render queue past opaque, or a URP surface set to transparent), or named glass or window
        /// -- an imported model's glass is often an opaque material with a name that says so.
        /// </summary>
        static bool SeeThrough(Material[] materials)
        {
            if (materials == null || materials.Length == 0) return false;
            foreach (var m in materials)
            {
                if (m == null) return false;
                var name = m.name.ToLowerInvariant();
                var transparent = m.renderQueue >= 2500
                                  || m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f
                                  || name.Contains("glass") || name.Contains("window");
                if (!transparent) return false;
            }
            return true;
        }
    }
}
