// Native quad remesher for Unity — editor front end.
//
// Stages 1 and 2 are implemented; the window is built so each later stage slots in as its own
// step with its own visualisation, because a field solver that is subtly wrong looks fine in
// numbers and obvious on screen.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace NativeRemesher
{
    internal sealed partial class NativeRemesherWindow : EditorWindow
    {
        // The settings a user decides, and nothing else. Everything the window used to offer beside
        // them -- the Profile list and Auto Profile, the Extractor (Collapse), Global Parametrization,
        // Quad Layout, Curvature Alignment, Patch Curves, Straps As Tubes, Detail Quads and the
        // step-by-step field inspector -- either measured worse than the default on every model
        // (Collapse 24-47 % irregular vertices against 1-8 %, Detail Quads 11-35 %), or is decided
        // by Model Type now. Their code stays in Runtime, behind the same statics, for the tests and
        // the benchmarks. See Benchmarks/round5-parametrization.md, round 30.

        [SerializeField] ModelType m_ModelType = ModelType.Auto;

        // QuadRemesher's own settings, with its add-on's defaults. See the matching statics on
        // Extraction.IntegerGridMap.
        [SerializeField] int m_TargetQuadCount = 2000;
        [SerializeField] float m_AdaptiveSize = 50f;
        [SerializeField] bool m_AdaptQuadCount = true;
        [SerializeField] bool m_DetectHardEdges = true;
        [SerializeField] SymmetryAxis m_Symmetry = SymmetryAxis.None;
        [SerializeField] bool m_UseMaterials;

        // A model in parts. Off: each part is remeshed on its own (see RunParts).
        [SerializeField] bool m_CombineParts;
        /// <summary>A model in parts: split the quads by how much of each part can be seen.</summary>
        [SerializeField] bool m_HiddenPartsFewer = true;

        // On: the transferred UVs cut across every seam of a scan's atlas (ccnb at 5000: 6,530 of
        // 14,884 faces), and the bake is closer to the source on screen (see TextureBake). Does
        // nothing when the source has no UVs or no texture.
        [SerializeField] bool m_BakeTexture = true;
        [SerializeField] bool m_BakeNormalMap = true;
        // 0 is Auto: the source texture's own size (ccnb.jpg is 8192 square; the old fixed 4096, or
        // the 2048 a window had kept, baked it at a quarter or a sixteenth of its pixels).
        [SerializeField] int m_TextureSize;
        [SerializeField] bool m_SaveAsAsset = true;
        // OFF, so the asset is one submesh and therefore one material slot.
        //
        // Quad topology in the .asset reads like the more faithful choice and is not: Unity
        // triangulates quad topology at draw time anyway, so it buys nothing on screen, while the
        // second submesh it forces (for the ~7 % of faces that are not quads) asks every renderer
        // for a second material and shows up in the inspector of every asset this makes. The quads
        // leave in the .fbx, which is written from the face list rather than from this mesh.
        [SerializeField] bool m_QuadTopology;

        [SerializeField] bool m_ShowDetails;

        QuadExtractionResult m_Extraction;
        RemeshResult m_LastResult;
        string m_Status = "";

        [MenuItem("Tools/Native Remesher/Native Remesher", false, 0)]
        static void Open()
        {
            var window = GetWindow<NativeRemesherWindow>("Native Remesher");
            window.minSize = new Vector2(320, 380);
            window.Show();
        }

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Selection.selectionChanged += Repaint;
            Undo.undoRedoPerformed += OnPaintUndoRedo;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Selection.selectionChanged -= Repaint;
            Undo.undoRedoPerformed -= OnPaintUndoRedo;
            m_Painting = false;
            ReleasePaintMesh();
            if (m_OverlayMaterial != null) DestroyImmediate(m_OverlayMaterial);

            // A worker holding a million-vertex array must not outlive the window that started it,
            // and EditorApplication.update would otherwise keep a dead instance subscribed.
            EditorApplication.update -= OnJobUpdate;
            m_Cancel?.Cancel();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Native Quad Remesher", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            var mesh = GetSelectedMesh(out var selected, out var error);
            // A model in parts: every part under the selection, not the first one found.
            var parts = GetSelectedParts(out var partsRoot, out var partsError);
            var multi = parts.Count > 1;
            if (multi) error = partsError;
            if (error != null)
                EditorGUILayout.HelpBox(error, MessageType.Info);

            // ---- the model
            if (multi)
            {
                EditorGUILayout.HelpBox(PartsSummary(partsRoot, parts), MessageType.None);
                m_CombineParts = EditorGUILayout.Toggle(
                    new GUIContent("Combine Parts",
                        "Join the parts into one mesh and remesh that, as one object. Off: each part is " +
                        "remeshed on its own and the result keeps the model's hierarchy -- the quad budget " +
                        "split over the parts, and one shared texture and normal map for the whole model."),
                    m_CombineParts);
                using (new EditorGUI.DisabledScope(m_CombineParts))
                    m_HiddenPartsFewer = EditorGUILayout.Toggle(
                        new GUIContent("Fewer Quads On Hidden Parts",
                            "Split the quads by how much of each part can be seen from outside, not by area " +
                            "alone: a car's cabin, seats, door insides and underfloor tunnel give quads to its " +
                            "body panels. Glass (transparent materials, or named glass or window) does not " +
                            "hide what is behind it. A part nobody can see keeps 15 % of its share."),
                        m_HiddenPartsFewer);
            }

            // The model's kind picks the profile and every setting measured best for it.
            m_ModelType = (ModelType)EditorGUILayout.IntPopup(
                new GUIContent("Model Type",
                    "What kind of model this is; it picks the settings that measured best for that kind.\n\n" +
                    "Auto: read off the mesh (each part of a model in parts on its own).\n" +
                    "Character: sculpted or organic -- fingers and toes get rows of quads across them, " +
                    "thin straps and belts become clean tubes.\n" +
                    "Hard Surface: modelled in a modelling tool -- vehicles, props, machines; sharp edges " +
                    "become edge loops where they stand apart, dense ones (tread, grilles) are left to " +
                    "the normal map.\n" +
                    "Scan: photogrammetry or a laser scan -- noise is not taken for edges, short building " +
                    "edges are kept."),
                (int)m_ModelType,
                new[] { new GUIContent("Auto"), new GUIContent("Character"), new GUIContent("Hard Surface"), new GUIContent("Scan") },
                new[] { (int)ModelType.Auto, (int)ModelType.Character, (int)ModelType.HardSurface, (int)ModelType.Scan });
            if (m_ModelType == ModelType.General) m_ModelType = ModelType.Auto;
            if (!string.IsNullOrEmpty(m_TypeNote))
                EditorGUILayout.LabelField(" ", m_TypeNote, EditorStyles.wordWrappedMiniLabel);

            // ---- the quads: QuadRemesher's own settings, in its order
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Quads", EditorStyles.boldLabel);
            m_TargetQuadCount = Mathf.Max(4, EditorGUILayout.IntField(
                new GUIContent("Target Quad Count",
                    "The number of quads wanted. With Adapt Quad Count on, curved areas get extra " +
                    "quads on top of it, as in QuadRemesher."),
                m_TargetQuadCount));
            m_AdaptiveSize = EditorGUILayout.Slider(
                new GUIContent("Adaptive Size %",
                    "How much smaller quads get where the surface curves, as in QuadRemesher " +
                    "(default 50). 0 is an even grid everywhere; higher spends more of the " +
                    "budget on fingers, rims and folds."),
                m_AdaptiveSize, 0f, 100f);
            m_AdaptQuadCount = EditorGUILayout.Toggle(
                new GUIContent("Adapt Quad Count",
                    "As QuadRemesher's option of the same name (on by default there): the " +
                    "target sizes the quads on flat areas and curved areas get extra quads on " +
                    "top, so the result can have up to 3x the target. Off holds the count."),
                m_AdaptQuadCount);
            m_DetectHardEdges = EditorGUILayout.Toggle(
                new GUIContent("Detect Hard Edges",
                    "Find creases that run as long clean lines - grooves between fingers, " +
                    "seams, pocket and panel edges - and lay edge loops along them, as " +
                    "QuadRemesher's hard-edge detection does. Short creases from scan noise " +
                    "are ignored."),
                m_DetectHardEdges);
            m_Symmetry = (SymmetryAxis)EditorGUILayout.EnumPopup(
                new GUIContent("Symmetry",
                    "As QuadRemesher's Symmetry X / Y / Z: one half is remeshed and mirrored, welded " +
                    "along the plane, so both sides get the same edge loops. The plane is the " +
                    "object's local origin when the mesh straddles it, else the middle of its bounds."),
                m_Symmetry);
            m_UseMaterials = EditorGUILayout.Toggle(
                new GUIContent("Use Materials",
                    "As QuadRemesher's option of the same name: the borders between the mesh's " +
                    "materials (submeshes) become edge loops, and the result keeps one submesh per " +
                    "material, with the source's materials on it."),
                m_UseMaterials);

            // ---- what is written out
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            m_BakeTexture = EditorGUILayout.Toggle(
                new GUIContent("Bake Texture",
                    "Give the result its own UV layout and bake the source's texture into it, with a " +
                    "material using it. The source's own UVs would otherwise be carried over, and on a " +
                    "scan, whose texture atlas is cut into thousands of pieces, about half the quads " +
                    "straddle a cut and show a smeared strip. Reads the texture file at full size."),
                m_BakeTexture);
            m_BakeNormalMap = EditorGUILayout.Toggle(
                new GUIContent("Bake Normal Map",
                    "Bake the source's surface detail into a tangent-space normal map over the result's " +
                    "own UV layout, with a material using it: what the quads are too coarse to carry -- " +
                    "slots, screws, seams, folds, a tire's tread -- comes back as shading, the way game " +
                    "assets are made. Needs no source texture."),
                m_BakeNormalMap);
            using (new EditorGUI.DisabledScope(!m_BakeTexture && !m_BakeNormalMap))
            {
                // Its own row, not under Bake Normal Map: it is the size of both bakes, and drawn
                // indented under the normal map it read as the normal map's alone.
                m_TextureSize = EditorGUILayout.IntPopup(
                    new GUIContent("Texture Size",
                        "Width and height of both baked textures, the colour texture and the normal map. " +
                        "Auto: the source texture's own size, read from its file (the largest, with " +
                        "several), so an 8K scan bakes at 8K; 4096 when there is no source texture."),
                    m_TextureSize, new[] { new GUIContent("Auto"), new GUIContent("1024"), new GUIContent("2048"), new GUIContent("4096"), new GUIContent("8192") },
                    new[] { 0, 1024, 2048, 4096, 8192 });
                if (m_TextureSize == 0)
                    EditorGUILayout.LabelField(" ", AutoTextureSizeNote(multi ? partsRoot : selected),
                        EditorStyles.wordWrappedMiniLabel);
            }
            m_SaveAsAsset = EditorGUILayout.Toggle(
                new GUIContent("Save As Asset",
                    "Write the result to a .asset file (and an .fbx with the quads). Without this the mesh " +
                    "lives only in memory and does not survive a domain reload or a scene save."),
                m_SaveAsAsset);
            m_QuadTopology = EditorGUILayout.Toggle(
                new GUIContent("Quad Topology In Asset",
                    "Keep the quads as quad topology in the Unity mesh. That needs a second " +
                    "submesh for the faces which are not quads, so the object asks for two " +
                    "materials. Off gives one triangle submesh and one material; the exported " +
                    ".fbx keeps the quads either way."),
                m_QuadTopology);

            // ---- painted fixes
            DrawPaintGUI(mesh != null ? selected : null);
            EditorGUILayout.Space();

            // A run in flight replaces the button with its own progress bar and a cancel control,
            // drawn in this window rather than through EditorUtility.DisplayProgressBar. The modal
            // bar only advances when the caller yields, so during a solve it sat at whatever
            // fraction it was last given and the whole editor appeared hung.
            if (isRunning)
            {
                var elapsed = m_JobWatch != null ? m_JobWatch.Elapsed.TotalSeconds : 0.0;
                var rect = EditorGUILayout.GetControlRect(false, 22f);
                EditorGUI.ProgressBar(rect, Mathf.Clamp01(m_JobProgress),
                    $"{m_JobStage}  ({elapsed:F1}s)");

                if (GUILayout.Button("Cancel"))
                    m_Cancel?.Cancel();
            }
            else
            {
                using (new EditorGUI.DisabledScope(multi ? partsError != null : mesh == null))
                {
                    // A result selected runs its source again, with whatever was painted since.
                    var subject = multi ? partsRoot : selected;
                    var again = (multi || mesh != null) && Selection.activeGameObject != null
                                && SourceOf(Selection.activeGameObject) == subject
                                && Selection.activeGameObject != subject;
                    var label = again ? $"Remesh '{subject.name}' Again" : multi ? $"Remesh {parts.Count} Parts" : "Remesh";
                    if (GUILayout.Button(new GUIContent(label,
                            again ? "Runs the source of the selected result again, with the painted fix areas, " +
                                    "and hides the old result (Undo brings it back)." : ""), GUILayout.Height(28)))
                        RunPipeline();
                }
            }

            // The profile mismatch that cost a user several rounds of "it still looks wrong". A
            // dihedral threshold cannot tell a crease from scan noise, so on a scan under a profile
            // meant for modelled geometry it marks a third of the mesh as creased - and every one of
            // those pins a vertex against surface relaxation.
            if (m_LastResult != null && m_LastResult.featureDetectionSuspect)
                EditorGUILayout.HelpBox(
                    $"Feature detection marked {m_LastResult.featureVertexFraction * 100f:F0} % of " +
                    $"the input as creased, at {m_LastResult.profile.featureAngleDegrees:F0} " +
                    "degrees. That is the signature of a scan rather than modelled geometry. Set " +
                    "Model Type to Auto or Scan.",
                    MessageType.Warning);

            if (!string.IsNullOrEmpty(m_Status))
                EditorGUILayout.LabelField(m_Status, EditorStyles.wordWrappedMiniLabel);

            // The extractor's own counters, for a bug report; folded away.
            if (m_Extraction != null)
            {
                m_ShowDetails = EditorGUILayout.Foldout(m_ShowDetails, "Last Run Details", true);
                if (m_ShowDetails)
                    EditorGUILayout.HelpBox(m_Extraction.ToString(), MessageType.None);
            }
        }

        // ------------------------------------------------------------------ actions

        static Mesh GetSelectedMesh(out GameObject selected, out string error)
        {
            selected = Selection.activeGameObject;
            error = null;

            if (selected == null)
            {
                error = "Select a GameObject with a mesh.";
                return null;
            }

            // A result made here stands for its source: running it again is what selecting it and
            // pressing Remesh means once fixes are painted on it.
            selected = SourceOf(selected);

            var mesh = MeshOn(selected);

            // Look down the hierarchy when the selected object itself carries no mesh. An imported
            // model is a ROOT with the geometry on a child - often several levels down, and often
            // named something nobody would think to click - so "select the object and press the
            // button" failed on exactly the inputs this tool is for, with a message that read like
            // the object was wrong rather than the node.
            //
            // The child that owns the mesh becomes the subject of the whole operation, which is
            // also what makes the output land in the right place: the result is parented beside it
            // and copies its local transform, and a model root's transform is not the mesh's.
            if (mesh == null)
            {
                foreach (var child in selected.GetComponentsInChildren<Transform>(true))
                {
                    var childMesh = MeshOn(child.gameObject);
                    if (childMesh == null) continue;

                    selected = child.gameObject;
                    mesh = childMesh;
                    break;
                }
            }

            if (mesh == null)
            {
                error = $"'{selected.name}' has no mesh, and neither does anything under it. " +
                        "Select the object with the MeshFilter or SkinnedMeshRenderer.";
                return null;
            }

            // Not a mesh this tool can read at all: the vertex arrays live on the GPU and
            // Mesh.vertices comes back empty. Worth naming the exact checkbox, because the setting
            // is on the MODEL importer rather than on the mesh, and the message used to send people
            // looking at the wrong inspector.
            if (!mesh.isReadable)
            {
                error = $"'{mesh.name}' is not readable. Select the model in the Project window, " +
                        "then Inspector > Model > Read/Write Enabled > Apply.";
                return null;
            }

            return mesh;
        }

        /// <summary>The mesh a single GameObject renders, from either kind of renderer, or null.</summary>
        static Mesh MeshOn(GameObject go)
        {
            var filter = go.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null) return filter.sharedMesh;

            var skinned = go.GetComponent<SkinnedMeshRenderer>();
            return skinned != null ? skinned.sharedMesh : null;
        }

        /// <summary>
        /// The parts of the selected model: every active object at or under it whose renderer is on
        /// and has a mesh -- of a LOD group only LOD 0, and never an earlier result made here. An
        /// imported model is usually a root over several meshes (a car's body, doors, wheels); taking
        /// the first one found remeshed one part and dropped the rest.
        /// </summary>
        static List<GameObject> GetSelectedParts(out GameObject root, out string error)
        {
            error = null;
            root = SourceOf(Selection.activeGameObject);
            var parts = new List<GameObject>();
            if (root == null) return parts;
            var skip = new HashSet<Renderer>();
            foreach (var group in root.GetComponentsInChildren<LODGroup>(false))
            {
                var lods = group.GetLODs();
                for (var l = 1; l < lods.Length; l++)
                    foreach (var r in lods[l].renderers) if (r != null) skip.Add(r);
                if (lods.Length > 0)
                    foreach (var r in lods[0].renderers) if (r != null) skip.Remove(r);
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(false))
            {
                var go = t.gameObject;
                if (IsResult(go, root) || MeshOn(go) == null) continue;
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null && (!renderer.enabled || skip.Contains(renderer))) continue;
                parts.Add(go);
            }
            var unreadable = new List<string>();
            foreach (var go in parts) if (!MeshOn(go).isReadable) unreadable.Add(MeshOn(go).name);
            if (unreadable.Count > 0)
                error = $"{unreadable.Count} of the {parts.Count} parts are not readable ({string.Join(", ", unreadable.GetRange(0, Mathf.Min(3, unreadable.Count)))}" +
                        (unreadable.Count > 3 ? ", ..." : "") + "). Select the model in the Project window, then " +
                        "Inspector > Model > Read/Write Enabled > Apply.";
            return parts;
        }

        /// <summary>Whether an object is, or sits under, a result made here (below the root).</summary>
        static bool IsResult(GameObject go, GameObject root)
        {
            for (var t = go.transform; t != null && t != root.transform; t = t.parent)
                if (t.GetComponent<RemeshSource>() != null) return true;
            return false;
        }

        static string PartsSummary(GameObject root, List<GameObject> parts)
        {
            long triangles = 0;
            foreach (var go in parts)
            {
                var m = MeshOn(go);
                if (m != null && m.isReadable) for (var i = 0; i < m.subMeshCount; i++) triangles += m.GetIndexCount(i) / 3;
            }
            return $"'{root.name}' is {parts.Count} parts, {triangles:N0} triangles in all.";
        }

        /// <summary>
        /// Writes the result to a .asset file and returns its path, or an empty string on failure.
        /// </summary>
        /// <remarks>
        /// A Mesh built at runtime and only assigned to a MeshFilter does not survive a domain
        /// reload or a scene save: the GameObject remains and its mesh silently becomes null. The
        /// tool looked like it was losing work, so saving is the default rather than an option.
        /// </remarks>
        /// <summary>
        /// Writes the same mesh beside the .asset as an .fbx, and imports it.
        /// </summary>
        /// <remarks>
        /// A .asset mesh is a Unity-internal serialisation: it cannot be opened in Blender, Maya or
        /// ZBrush, and it does not survive leaving this project. The .fbx is the file the result is
        /// actually usable as - it is what the competitor writes, and it keeps the quads (see
        /// FbxWriter for how, and why almost every other route out of Unity does not).
        ///
        /// A failure here is logged and swallowed. The .asset is already written by this point and
        /// is the thing the caller returns; losing the export is worth strictly less than losing the
        /// solve that produced it.
        /// </remarks>
        static void SaveFbx(RemeshResult result, string assetPath)
        {
            var fbxPath = Path.ChangeExtension(assetPath, ".fbx");
            try
            {
                // From the FACE LIST, not from result.mesh. The render mesh may have been
                // triangulated into one submesh for the material slot's sake, and reading the
                // quads back out of it would then be reading triangles — the export would lose
                // exactly the thing it exists to preserve. The extraction keeps the real faces.
                var extraction = result.extraction;
                FbxWriter.Write(fbxPath, extraction.positions, extraction.faces,
                    extraction.shadingNormals ?? extraction.normals,
                    Path.GetFileNameWithoutExtension(fbxPath), FbxWriter.defaultUnitScale,
                    extraction.faceUvs);
                AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport);
                Debug.Log($"[Native Remesher] Wrote {fbxPath}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Native Remesher] Could not write {fbxPath}: {e.Message}");
            }
        }

        const string k_Folder = "Assets/NativeRemesher/Output";

        static string SaveMeshAsset(RemeshResult result, Mesh mesh, string sourceName, string folder = null)
        {

            try
            {
                if (!AssetDatabase.IsValidFolder(k_Folder))
                {
                    if (!AssetDatabase.IsValidFolder("Assets/NativeRemesher"))
                        AssetDatabase.CreateFolder("Assets", "NativeRemesher");
                    AssetDatabase.CreateFolder("Assets/NativeRemesher", "Output");
                }

                var safe = string.Join("_", sourceName.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(safe)) safe = "Mesh";

                // GenerateUniqueAssetPath rather than overwriting: remeshing the same object twice
                // with different settings should give two results to compare, not silently replace
                // the first.
                var path = AssetDatabase.GenerateUniqueAssetPath($"{folder ?? k_Folder}/{safe}_Quads.asset");

                AssetDatabase.CreateAsset(mesh, path);
                AssetDatabase.SaveAssets();

                SaveFbx(result, path);
                return path;
            }
            catch (System.Exception e)
            {
                // Failing to save must not lose the result — fall back to the in-memory mesh and
                // say so, rather than throwing away a solve that may have taken a minute.
                Debug.LogWarning($"[Native Remesher] Could not save mesh asset: {e.Message}. " +
                                 "The result is in the scene but will not survive a domain reload.");
                return "";
            }
        }

        // ------------------------------------------------------------------ background run
        //
        // The solve used to run inside the button's OnGUI call, so the editor was frozen for its
        // whole duration: Unity's UI and the solve shared one thread, and DisplayProgressBar only
        // ever repaints when the caller yields, which a tight numeric loop never does. QuadRemesher
        // stays responsive because its solver is native code on its own thread — there is no reason
        // a managed one cannot do the same.
        //
        // Only the two ends touch Unity objects: reading the input mesh, and writing the output.
        // Both stay on the main thread here. RemeshPipeline.Solve in between takes plain arrays.

        Task<RemeshResult> m_Job;
        static BackgroundScheduler s_Scheduler;
        CancellationTokenSource m_Cancel;
        GameObject m_JobSelected;
        GameObject m_JobReplaces;
        System.Diagnostics.Stopwatch m_JobWatch;

        // Written by the worker, read by the main thread. A reference assignment and a float store
        // are both atomic, and neither side needs a consistent pair — this drives a progress bar,
        // so a stage label one frame out of step with its fraction is invisible.
        volatile string m_JobStage = "";
        volatile float m_JobProgress;

        bool isRunning => m_Job != null || m_PartsJob != null;

        // Combine Parts: the joined parts' materials, one after another (null: the source's own).
        Material[] m_JobMaterials;

        /// <summary>Starts the pipeline on a worker thread.</summary>
        void RunPipeline()
        {
            if (isRunning) return;
            var parts = GetSelectedParts(out var partsRoot, out var partsError);
            if (parts.Count > 1)
            {
                if (partsError == null) RunParts(partsRoot, parts);
                return;
            }
            var mesh = GetSelectedMesh(out var selected, out _);
            if (mesh == null)
                return;

            var settings = PrepareRun(out var profile, out var autoProfile);

            // Main thread, and the only place the Mesh API is touched on the way in.
            //
            // The four-argument overload, and the UVs are passed to Solve below. This used to call
            // the three-argument one and drop them, so the window — the only path a user actually
            // takes — produced output with no UV channel and therefore no texture at all, while
            // PerfProbe passed them and measured 52,047 UVs on the same asset. The instrument was
            // right and disagreed with the product, which is the worst way for this to fail.
            RemeshTopology.ReadMesh(mesh, out var positions, out var triangles, out var sourceUvs,
                out var triangleSubmesh);
            QuadSource.Reorder(mesh, ref triangles, ref triangleSubmesh);
            RunSingle(selected, null, positions, triangles, sourceUvs, triangleSubmesh, settings, profile, autoProfile);
        }

        /// <summary>The window's settings onto a run's (and the statics the solve reads), on the main thread.</summary>
        RemeshSettings PrepareRun(out RemeshProfile profile, out bool autoProfile)
        {
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = m_TargetQuadCount;
            settings.symmetry = m_Symmetry;
            settings.useMaterials = m_UseMaterials;
            settings.modelType = m_ModelType;

            // Auto profile selection, seeded with GameAsset, on the integer grid: Model Type picks
            // Hard Surface's and Scan's profiles over it (RemeshPipeline.ApplyModelType). Under Auto
            // the seed is what the pipeline decimates with before it picks from the decimated mesh,
            // the only population the selection threshold was calibrated against.
            profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
            profile.extractor = RemeshExtractor.IntegerGrid;
            autoProfile = true;

            // The statics the solve reads; set here, on the main thread, before the job starts.
            // The methods are the measured best and no longer the user's to pick (see the fields).
            Extraction.IntegerGridMap.curvatureAlignment = 0f;
            Extraction.IntegerGridMap.curveNetwork = false;
            Extraction.IntegerGridMap.parametrization = true;
            Extraction.IntegerGridMap.parametrizationLayout = true;
            RemeshPipeline.detailQuads = false;
            // Straps as tubes: the pipeline runs the finder for Character models only (StrapsFor).
            RemeshPipeline.ribbonParts = true;
            // QuadRemesher's settings.
            Extraction.IntegerGridMap.adaptiveQuadCount = m_AdaptQuadCount;
            Extraction.IntegerGridMap.adaptiveSize = m_AdaptiveSize / 100f;
            Extraction.IntegerGridMap.coherentCreases = m_DetectHardEdges;
            return settings;
        }

        /// <summary>
        /// One mesh through the pipeline on a worker: <paramref name="selected"/>'s own, or with
        /// <paramref name="materials"/>, a model's parts joined (Combine Parts) under its root.
        /// </summary>
        void RunSingle(GameObject selected, Material[] materials, Vector3[] positions, int[] triangles,
            Vector2[] sourceUvs, int[] triangleSubmesh, RemeshSettings settings, RemeshProfile profile, bool autoProfile)
        {
            // The textures to bake, one per submesh, read here on the main thread.
            TextureBake.Image[] bakeImages = null;
            int[] bakeTriangleTexture = null;
            var bakeNormals = m_BakeNormalMap;
            // Paint lives on a mesh object, in its space: none for a joined model.
            settings.densityPaint = materials == null ? PaintFor(selected) : null;
            // The result this run replaces, when it was the one selected.
            var current = Selection.activeGameObject;
            m_JobReplaces = current != null && current != selected && SourceOf(current) == selected ? current : null;
            m_JobMaterials = materials;
            if (m_BakeTexture && sourceUvs != null)
            {
                if (materials == null) ReadSourceTextures(selected, triangleSubmesh, out bakeImages, out bakeTriangleTexture);
                else
                {
                    var list = new List<TextureBake.Image>();
                    bakeTriangleTexture = ReadTextures(materials, triangleSubmesh, triangles.Length / 3, list, new Dictionary<Texture, int>());
                    bakeImages = list.Count > 0 ? list.ToArray() : null;
                }
            }

            var bakeResolution = TextureSizeFor(bakeImages);
            m_JobSelected = selected;
            m_JobStage = "Starting";
            m_JobProgress = 0f;
            m_JobWatch = System.Diagnostics.Stopwatch.StartNew();
            m_Cancel = new CancellationTokenSource();

            var token = m_Cancel.Token;

            // On its own below-normal threads, a core left free: see BackgroundScheduler.
            s_Scheduler ??= new BackgroundScheduler();
            m_Job = s_Scheduler.Run(() =>
            {
                void Progress(string stage, float progress)
                {
                    // Cancellation is observed here rather than polled, because this is the only
                    // point the solver hands control back often enough to matter.
                    token.ThrowIfCancellationRequested();
                    m_JobStage = stage;
                    m_JobProgress = progress;
                }
                var solved = RemeshPipeline.Solve(positions, triangles, settings, profile, Progress,
                    sourceUvs, autoProfile, triangleSubmesh);
                if (bakeImages != null)
                    RemeshPipeline.BakeTexture(solved, positions, triangles, sourceUvs, bakeTriangleTexture,
                        bakeImages, bakeResolution, Progress);
                if (bakeNormals)
                    RemeshPipeline.BakeNormalMap(solved, positions, triangles, bakeResolution, Progress);
                return solved;
            }, token);

            EditorApplication.update += OnJobUpdate;
        }

        void OnJobUpdate()
        {
            if (m_PartsJob != null)
            {
                OnPartsJobUpdate();
                return;
            }
            if (m_Job == null)
            {
                EditorApplication.update -= OnJobUpdate;
                return;
            }

            if (!m_Job.IsCompleted)
            {
                Repaint();
                return;
            }

            var job = m_Job;
            var selected = m_JobSelected;

            // Cleared before the result is handled, so a failure inside FinishJob cannot leave the
            // window permanently convinced a run is still in flight.
            EditorApplication.update -= OnJobUpdate;
            m_Job = null;
            m_JobSelected = null;
            m_Cancel?.Dispose();
            m_Cancel = null;
            m_JobWatch?.Stop();

            if (job.IsCanceled)
            {
                m_Status = "Cancelled.";
                Repaint();
                return;
            }

            if (job.IsFaulted)
            {
                var inner = job.Exception?.GetBaseException();
                if (inner is System.OperationCanceledException)
                {
                    m_Status = "Cancelled.";
                }
                else
                {
                    m_Status = "Failed: " + (inner?.Message ?? "unknown error");
                    if (inner != null) Debug.LogException(inner);
                }
                Repaint();
                return;
            }

            try
            {
                FinishJob(job.Result, selected, m_JobMaterials);
            }
            catch (System.Exception e)
            {
                m_Status = "Failed: " + e.Message;
                Debug.LogException(e);
            }

            Repaint();
        }

        /// <summary>The main-thread half: build the mesh, save it, put it in the scene.</summary>
        void FinishJob(RemeshResult result, GameObject selected, Material[] overrideMaterials = null)
        {
            if (selected == null)
            {
                m_Status = "The source object was destroyed while the remesh was running.";
                return;
            }

            RemeshPipeline.BuildOutputMesh(result, selected.name, m_QuadTopology);

            m_Extraction = result.extraction;
            m_LastResult = result;

            var outputMesh = result.mesh;
            var assetPath = "";

            // Persist before anything references it. A Mesh created at runtime and only assigned to
            // a MeshFilter does not survive a domain reload or a scene save — the GameObject stays
            // and its mesh becomes null, which looks like the tool silently losing work.
            if (m_SaveAsAsset)
            {
                assetPath = SaveMeshAsset(result, outputMesh, selected.name);
                if (!string.IsNullOrEmpty(assetPath))
                    outputMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            }

            var undoGroup = Undo.GetCurrentGroup();

            var go = new GameObject(selected.name + "_Quads");
            Undo.RegisterCreatedObjectUndo(go, "Native Remesh");
            go.transform.SetParent(selected.transform.parent, false);
            go.transform.localPosition = selected.transform.localPosition;
            go.transform.localRotation = selected.transform.localRotation;
            go.transform.localScale = selected.transform.localScale;
            go.AddComponent<MeshFilter>().sharedMesh = outputMesh;
            go.AddComponent<RemeshSource>().source = selected;
            if (m_JobReplaces != null)
            {
                Undo.RecordObject(m_JobReplaces, "Native Remesh");
                m_JobReplaces.SetActive(false);
                m_JobReplaces = null;
            }

            var sourceRenderer = selected.GetComponent<Renderer>();
            var sourceMaterial = sourceRenderer != null ? sourceRenderer.sharedMaterial : null;
            if (overrideMaterials != null)
            {
                sourceMaterial = null;
                foreach (var m in overrideMaterials) if (m != null) { sourceMaterial = m; break; }
            }

            // One material per submesh, not one material.
            //
            // The result is a quad submesh plus, usually, a triangle submesh for the faces that
            // could not be quads. Unity draws submesh i with material i and simply does not draw a
            // submesh with no material, so assigning a single material here would have rendered the
            // quads and silently dropped every triangle — new holes, introduced by the fix for the
            // old ones, and visible only in the scene rather than in any metric.
            var renderer = go.AddComponent<MeshRenderer>();
            var slots = Mathf.Max(1, outputMesh != null ? outputMesh.subMeshCount : 1);
            var materials = new Material[slots];
            // With materials kept, submesh i is the source's material i.
            var sourceMaterials = overrideMaterials ?? (sourceRenderer != null ? sourceRenderer.sharedMaterials : null);
            var perMaterial = result.extraction != null && result.extraction.faceMaterials != null;
            for (var i = 0; i < slots; i++)
                materials[i] = perMaterial && sourceMaterials != null && i < sourceMaterials.Length
                    ? sourceMaterials[i]
                    : sourceMaterial;
            if (result.bakedTexture != null || result.bakedNormalMap != null)
            {
                // One baked texture over every submesh: each slot keeps its source material's
                // shader and settings, with the bake as its main texture and the normal map as its
                // bump map.
                var baked = result.bakedTexture != null ? SaveBakedTexture(result, assetPath, selected.name) : null;
                var normalMap = result.bakedNormalMap != null ? SaveNormalMap(result, assetPath, selected.name) : null;
                var made = new Dictionary<Material, Material>();
                for (var i = 0; i < slots; i++)
                {
                    var from = materials[i];
                    var key = from != null ? from : sourceMaterial;
                    if (key != null && made.TryGetValue(key, out var done)) { materials[i] = done; continue; }
                    var mat = key != null ? new Material(key) : new Material(Shader.Find("Standard"));
                    mat.name = selected.name + "_Quads";
                    // The normal map's own UV layout replaced the source's, so an unbaked source
                    // texture would land on the wrong texels: left off rather than scrambled.
                    if (baked != null) mat.mainTexture = baked;
                    else if (mat.HasProperty("_MainTex") || mat.HasProperty("_BaseMap")) mat.mainTexture = null;
                    if (normalMap != null) AssignNormalMap(mat, normalMap);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        var matPath = AssetDatabase.GenerateUniqueAssetPath(
                            Path.ChangeExtension(assetPath, null) + ".mat");
                        AssetDatabase.CreateAsset(mat, matPath);
                    }
                    if (key != null) made[key] = mat;
                    materials[i] = mat;
                }
                AssetDatabase.SaveAssets();
            }
            renderer.sharedMaterials = materials;

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = go;

            var decimated = result.decimateStats != null
                ? $", pre-decimated {result.decimateStats.inputTriangles:N0}->" +
                  $"{result.decimateStats.outputTriangles:N0} tris"
                : "";
            var saved = string.IsNullOrEmpty(assetPath) ? "" : $"\nsaved to {assetPath}";

            var profileNote = result.profileWasAutoSelected
                ? $"{result.profile.kind} (auto)"
                : result.profile.kind.ToString();

            var paintNote = result.paintCountRatio > 0f
                ? $"\npainted fix areas: target x{result.paintCountRatio:0.00}"
                : "";
            var bakeNote = result.bakeStats != null
                ? $"\nbaked {result.bakedTextureSize}px texture, {result.bakeStats.charts} charts, " +
                  $"{result.bakeStats.fill:P0} of it used, in {result.bakeStats.unwrapMs + result.bakeStats.bakeMs} ms"
                : "";
            m_TypeNote = TypeNote(result);
            var typeNote = result.modelType != ModelType.General ? $"{TypeName(result.modelType)}, " : "";
            var layoutNote = result.layoutCoverage > 0f
                ? $"\nthe quad layout covered {result.layoutCoverage:P0} of the surface; solved without it: {result.coverage:P0}"
                : "";
            m_Status = $"{typeNote}{profileNote}: {result.extraction} in {result.totalMs} ms" +
                       $"{decimated}{saved}{paintNote}{bakeNote}{layoutNote}";
            Debug.Log($"[Native Remesher] {profileNote}: {result.extraction} " +
                      $"(decimate {result.decimateMs}ms, topo {result.topologyMs}ms, " +
                      $"orient {result.orientationMs}ms, pos {result.positionMs}ms, " +
                      $"extract {result.extractionMs}ms, {result.faceCountAttempts} count passes)" +
                      decimated, outputMesh);
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------ texture bake

        /// <summary>
        /// Per submesh of the source, its material's main texture as pixels, and per triangle which
        /// of them it reads. Nothing when no material has a texture.
        /// </summary>
        static void ReadSourceTextures(GameObject selected, int[] triangleSubmesh,
            out TextureBake.Image[] images, out int[] triangleTexture)
        {
            images = null;
            triangleTexture = null;
            var renderer = selected.GetComponent<Renderer>();
            if (renderer == null) return;
            var materials = renderer.sharedMaterials;
            var read = new Dictionary<Texture, int>();
            var list = new List<TextureBake.Image>();
            var slotImage = new int[Mathf.Max(1, materials.Length)];
            for (var i = 0; i < slotImage.Length; i++)
            {
                slotImage[i] = -1;
                var tex = i < materials.Length && materials[i] != null ? materials[i].mainTexture : null;
                if (tex == null) continue;
                if (!read.TryGetValue(tex, out var index))
                {
                    var image = ReadTexture(tex);
                    index = image != null ? list.Count : -1;
                    if (image != null) list.Add(image);
                    read[tex] = index;
                }
                slotImage[i] = index;
            }
            if (list.Count == 0) return;
            images = list.ToArray();
            if (triangleSubmesh != null)
            {
                triangleTexture = new int[triangleSubmesh.Length];
                for (var t = 0; t < triangleSubmesh.Length; t++)
                {
                    var slot = triangleSubmesh[t];
                    // Unity draws the extra submeshes past the last material with the last one.
                    if (slot >= slotImage.Length) slot = slotImage.Length - 1;
                    triangleTexture[t] = slot >= 0 ? slotImage[slot] : -1;
                }
            }
            else
                // One submesh: its material's texture for every triangle.
                images = slotImage[0] >= 0 ? new[] { images[slotImage[0]] } : null;
        }

        /// <summary>
        /// Per triangle, its material's main texture as an index into <paramref name="images"/> (read
        /// once each, through <paramref name="read"/>), -1 for none; null when no material has one.
        /// </summary>
        static int[] ReadTextures(Material[] materials, int[] triangleSubmesh, int triangleCount,
            List<TextureBake.Image> images, Dictionary<Texture, int> read)
        {
            var slotImage = new int[Mathf.Max(1, materials != null ? materials.Length : 0)];
            var any = false;
            for (var i = 0; i < slotImage.Length; i++)
            {
                slotImage[i] = -1;
                var tex = materials != null && i < materials.Length && materials[i] != null ? materials[i].mainTexture : null;
                if (tex == null) continue;
                if (!read.TryGetValue(tex, out var index))
                {
                    var image = ReadTexture(tex);
                    index = image != null ? images.Count : -1;
                    if (image != null) images.Add(image);
                    read[tex] = index;
                }
                slotImage[i] = index;
                any |= index >= 0;
            }
            if (!any) return null;
            var triangleTexture = new int[triangleCount];
            for (var t = 0; t < triangleCount; t++)
            {
                var slot = triangleSubmesh != null && t < triangleSubmesh.Length ? triangleSubmesh[t] : 0;
                // Unity draws the extra submeshes past the last material with the last one.
                if (slot >= slotImage.Length) slot = slotImage.Length - 1;
                triangleTexture[t] = slot >= 0 ? slotImage[slot] : -1;
            }
            return triangleTexture;
        }

        /// <summary>What the last run read the model as, for the line under Model Type.</summary>
        string m_TypeNote = "";

        static string TypeName(ModelType type) => type switch
        {
            ModelType.HardSurface => "Hard Surface",
            ModelType.Character => "Character",
            ModelType.Scan => "Scan",
            ModelType.Auto => "Auto",
            _ => "General",
        };

        static string TypeNote(RemeshResult result)
        {
            if (result == null || result.modelType == ModelType.General) return "";
            return result.shapeSignals.measured
                ? $"last run read it as {TypeName(result.modelType)} ({result.shapeSignals})"
                : $"last run: {TypeName(result.modelType)}";
        }

        /// <summary>
        /// What Auto will bake at for <paramref name="root"/>: the largest texture its renderers use,
        /// at the size of the file (not the size it is imported at).
        /// </summary>
        static string AutoTextureSizeNote(GameObject root)
        {
            var largest = 0;
            string largestName = null;
            if (root != null)
                foreach (var renderer in root.GetComponentsInChildren<Renderer>())
                foreach (var material in renderer.sharedMaterials)
                {
                    // The texture the bake reads (ReadSourceTextures): the material's main texture.
                    var texture = material != null ? material.mainTexture : null;
                    if (texture == null) continue;
                    int w = texture.width, h = texture.height;
                    if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer)
                        importer.GetSourceTextureWidthAndHeight(out w, out h);
                    if (Mathf.Max(w, h) <= largest) continue;
                    largest = Mathf.Max(w, h);
                    largestName = texture.name;
                }
            if (largest <= 0) return "no source texture: 4096";
            var size = Mathf.Clamp(Mathf.NextPowerOfTwo(largest), 1024, 8192);
            return $"{size} x {size}, from {largestName} ({largest} px)";
        }

        /// <summary>
        /// The Texture Size, or under Auto the largest source texture's own size, rounded up to a
        /// power of two between 1024 and 8192; 4096 when there is no texture (a normal map only).
        /// </summary>
        int TextureSizeFor(IReadOnlyList<TextureBake.Image> images)
        {
            if (m_TextureSize > 0) return m_TextureSize;
            var largest = 0;
            if (images != null)
                foreach (var image in images)
                    if (image != null) largest = Mathf.Max(largest, Mathf.Max(image.width, image.height));
            if (largest <= 0) return 4096;
            return Mathf.Clamp(Mathf.NextPowerOfTwo(largest), 1024, 8192);
        }

        /// <summary>
        /// A texture's pixels at full size: from its PNG or JPG file when it has one (the importer
        /// may have shrunk it -- ccnb.jpg is 8192 square, imported at 2048), else from the GPU copy.
        /// </summary>
        static TextureBake.Image ReadTexture(Texture texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
            {
                try
                {
                    var file = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                    if (file.LoadImage(File.ReadAllBytes(path), false))
                    {
                        var image = new TextureBake.Image(file.GetPixels32(), file.width, file.height);
                        DestroyImmediate(file);
                        return image;
                    }
                    DestroyImmediate(file);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Native Remesher] Could not read {path}: {e.Message}; using the imported texture.");
                }
            }
            // Any other format, or none: through a render target, which needs no Read/Write.
            var w = texture.width;
            var h = texture.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply(false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var result = new TextureBake.Image(copy.GetPixels32(), w, h);
            DestroyImmediate(copy);
            return result;
        }

        /// <summary>
        /// The baked pixels as a texture: a PNG beside the mesh asset, imported at its full size,
        /// when there is one; otherwise in memory only.
        /// </summary>
        /// <summary>
        /// The baked normal map as a texture imported as a normal map: a PNG beside the mesh asset,
        /// or in memory (RGBA with alpha 1, which UnpackNormal reads as well) when there is none.
        /// </summary>
        static Texture2D SaveNormalMap(RemeshResult result, string assetPath, string sourceName)
        {
            var size = result.bakedNormalMapSize;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = sourceName + "_Quads_Normal",
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(result.bakedNormalMap);
            texture.Apply(true);
            if (string.IsNullOrEmpty(assetPath)) return texture;
            try
            {
                var pngPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.ChangeExtension(assetPath, null) + "_Normal.png");
                File.WriteAllBytes(pngPath, texture.EncodeToPNG());
                AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceSynchronousImport);
                if (AssetImporter.GetAtPath(pngPath) is TextureImporter importer)
                {
                    importer.textureType = TextureImporterType.NormalMap;
                    importer.maxTextureSize = Mathf.Max(importer.maxTextureSize, size);
                    // High quality (BC5 on desktop): the default block compression bands the bake.
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.SaveAndReimport();
                }
                var saved = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
                if (saved != null)
                {
                    DestroyImmediate(texture);
                    Debug.Log($"[Native Remesher] Wrote {pngPath} (normal map, {result.bakeStats?.normalMissed} texels off the source)");
                    return saved;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Native Remesher] Could not save the normal map: {e.Message}. It is in memory only.");
            }
            return texture;
        }

        // The bump slot of the Built-in Standard shader and of URP / HDRP Lit is _BumpMap (HDRP's
        // is _NormalMap); each needs its keyword.
        static void AssignNormalMap(Material mat, Texture2D normalMap)
        {
            if (mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normalMap);
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
                mat.EnableKeyword("_NORMALMAP");
            }
            if (mat.HasProperty("_NormalMap"))
            {
                mat.SetTexture("_NormalMap", normalMap);
                mat.EnableKeyword("_NORMALMAP");
                mat.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }
        }

        static Texture2D SaveBakedTexture(RemeshResult result, string assetPath, string sourceName)
        {
            var size = result.bakedTextureSize;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
            {
                name = sourceName + "_Quads_Albedo",
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(result.bakedTexture);
            texture.Apply(true);
            if (string.IsNullOrEmpty(assetPath)) return texture;
            try
            {
                var pngPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.ChangeExtension(assetPath, null) + "_Albedo.png");
                File.WriteAllBytes(pngPath, texture.EncodeToPNG());
                AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceSynchronousImport);
                if (AssetImporter.GetAtPath(pngPath) is TextureImporter importer)
                {
                    importer.maxTextureSize = Mathf.Max(importer.maxTextureSize, size);
                    // High quality (BC7 on desktop): the default (BC1) blocks up a photographic bake
                    // seen close.
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.SaveAndReimport();
                }
                var saved = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
                if (saved != null)
                {
                    DestroyImmediate(texture);
                    Debug.Log($"[Native Remesher] Wrote {pngPath} ({result.bakeStats})");
                    return saved;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Native Remesher] Could not save the baked texture: {e.Message}. It is in memory only.");
            }
            return texture;
        }

        void OnSceneGUI(SceneView view)
        {
            if (m_Painting) PaintSceneGUI(view);
        }
    }
}
