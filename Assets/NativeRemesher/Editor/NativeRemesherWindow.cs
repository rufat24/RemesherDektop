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
    internal sealed class NativeRemesherWindow : EditorWindow
    {
        [SerializeField] int m_Iterations = 50;
        [SerializeField] int m_TargetQuadCount = 2000;
        // ON by default, and the default is the point of it. The dropdown used to open on
        // GameAsset, which declares creases at 30 degrees; on a scan that fires on noise in every
        // soft fold, and each false crease pins a vertex against surface relaxation. Measured on
        // the delivered files, vertices left sitting off the surface: 215 in 75 clusters under
        // GameAsset against 2 in 2 under Character, with the competitor at 10. Nothing in the
        // window said the profile was wrong for the input, so the setting that mattered most was
        // the one nobody was asked about. See RemeshPipeline.SelectProfileKind.
        [SerializeField] bool m_AutoProfile = true;

        /// <summary>The manual pick, and the seed the auto path decimates with.</summary>
        [SerializeField] RemeshProfileKind m_ProfileKind = RemeshProfileKind.GameAsset;
        // OFF, so the asset is one submesh and therefore one material slot.
        //
        // Quad topology in the .asset reads like the more faithful choice and is not: Unity
        // triangulates quad topology at draw time anyway, so it buys nothing on screen, while the
        // second submesh it forces (for the ~7 % of faces that are not quads) asks every renderer
        // for a second material and shows up in the inspector of every asset this makes. The quads
        // leave in the .fbx, which is written from the face list rather than from this mesh.
        [SerializeField] bool m_QuadTopology;

        /// <summary>Which extractor runs. See <see cref="RemeshExtractor"/>.</summary>
        /// <remarks>
        /// IntegerGrid by default. On the user's helmet head at 5000 / 11000 quads, Collapse gives
        /// 34 % irregular vertices, 38 / 23 open edges and 7 / 3 pieces; IntegerGrid gives 8.75 %,
        /// no open edges and one piece (body: 25 % against 2.77 %). Renamed from m_ExtractorChoice so a
        /// window already open picks the new default up instead of its serialised Collapse.
        /// Collapse stays in the list.
        /// </remarks>
        [SerializeField] RemeshExtractor m_ExtractorChoice = RemeshExtractor.IntegerGrid;

        /// <summary>
        /// Pull of the integer grid's cross field toward the principal curvature directions.
        /// Zero, the measured best on the helmet head and the body; see
        /// Extraction.IntegerGridMap.curvatureAlignment for the numbers.
        /// </summary>
        [SerializeField] float m_CurvatureAlignment;

        /// <summary>
        /// Cut the lattice along traced ridge/valley curves. See Extraction.IntegerGridMap.curveNetwork.
        /// </summary>
        [SerializeField] bool m_PatchCurves;

        // QuadRemesher's own three main settings, with its add-on's defaults. See the matching
        // statics on Extraction.IntegerGridMap.
        [SerializeField] bool m_AdaptQuadCount = true;
        [SerializeField] float m_AdaptiveSize = 50f;
        [SerializeField] bool m_DetectHardEdges = true;
        [SerializeField] bool m_GlobalParametrization = true;
        // Renamed from m_QuadLayout so an open window picks up the new default (on) instead of its
        // serialised off: the layout measured better than the per-singularity rounding on the head
        // and the body at 2k, 3.5k and 5k (Benchmarks/round5-parametrization.md, round 9).
        [SerializeField] bool m_QuadLayoutOn = true;
        [SerializeField] bool m_ShowAdvanced;

        [SerializeField] bool m_SaveAsAsset = true;
        [SerializeField] bool m_ShowField = true;
        [SerializeField] float m_CrossScale = 0.4f;
        [SerializeField] int m_MaxDrawn = 20000;

        GameObject m_Source;
        RemeshTopology m_Topology;
        OrientationField m_Field;
        QuadExtractionResult m_Extraction;
        RemeshResult m_LastResult;

        Vector3[] m_SegmentsU;
        Vector3[] m_SegmentsV;
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
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Selection.selectionChanged -= Repaint;

            // A worker holding a million-vertex array must not outlive the window that started it,
            // and EditorApplication.update would otherwise keep a dead instance subscribed.
            EditorApplication.update -= OnJobUpdate;
            m_Cancel?.Cancel();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Native Quad Remesher", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Instant Meshes / QuadriFlow, implemented in C#",
                EditorStyles.miniLabel);
            EditorGUILayout.Space();

            var mesh = GetSelectedMesh(out var selected, out var error);
            if (error != null)
                EditorGUILayout.HelpBox(error, MessageType.Info);

            // The settings QuadRemesher shows, in its order; the rest is under Advanced.
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
            using (new EditorGUI.DisabledScope(!m_GlobalParametrization || m_ExtractorChoice != RemeshExtractor.IntegerGrid))
                m_QuadLayoutOn = EditorGUILayout.Toggle(
                    new GUIContent("Quad Layout",
                        "Split the surface into rectangular patches along the separatrices and creases, " +
                        "give every patch side a whole number of quads together, and fill each patch " +
                        "with a regular grid - instead of rounding each singularity on its own. Close " +
                        "singularities come out a whole number of cells apart instead of folding, " +
                        "creases are patch borders, limbs get closed rings. Closed meshes only; an " +
                        "open one (a scan) uses the previous extractor."),
                    m_QuadLayoutOn);

            EditorGUILayout.Space();
            m_SaveAsAsset = EditorGUILayout.Toggle(
                new GUIContent("Save As Asset",
                    "Write the result to a .asset file. Without this the mesh lives only in " +
                    "memory and does not survive a domain reload or a scene save."),
                m_SaveAsAsset);
            m_QuadTopology = EditorGUILayout.Toggle(
                new GUIContent("Quad Topology In Asset",
                    "Keep the quads as quad topology in the Unity mesh. That needs a second " +
                    "submesh for the faces which are not quads, so the object asks for two " +
                    "materials. Off gives one triangle submesh and one material; the exported " +
                    ".fbx keeps the quads either way."),
                m_QuadTopology);

            EditorGUILayout.Space();
            m_ShowAdvanced = EditorGUILayout.Foldout(m_ShowAdvanced, "Advanced", true);
            var profile = RemeshProfile.For(m_ProfileKind);
            if (m_ShowAdvanced)
            {
                using (new EditorGUI.IndentLevelScope())
                    DrawAdvanced(mesh, selected, ref profile);
            }
            EditorGUILayout.Space();

            // Warn when the request sits outside the band the solver can serve, instead of quietly
            // producing a poor result and leaving the user to wonder why.
            if (m_Topology != null && !profile.preDecimate)
            {
                m_Topology.UsefulQuadCountRange(out var coarsest, out var finest);
                if (m_TargetQuadCount < coarsest)
                    EditorGUILayout.HelpBox(
                        $"{m_TargetQuadCount:N0} quads is coarser than this mesh can serve well " +
                        $"({coarsest:N0} is about the limit). Quality falls off sharply past that " +
                        "point. Enable a profile with pre-decimation, which raises the input's edge " +
                        "length instead of the lattice spacing and keeps the ratio in range.",
                        MessageType.Warning);
            }

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
                using (new EditorGUI.DisabledScope(mesh == null))
                {
                    if (GUILayout.Button("Remesh", GUILayout.Height(28)))
                        RunPipeline();
                }
            }

            // The profile mismatch that cost a user several rounds of "it still looks wrong". A
            // dihedral threshold cannot tell a crease from scan noise, so on a scan under a profile
            // meant for modelled geometry it marks a third of the mesh as creased - and every one of
            // those pins a vertex against surface relaxation. Silently. The result looked dented in
            // exactly the soft folds where relaxation had been refused, and every geometric metric
            // in the project said the mesh was fine, because it was: the defect was in which
            // vertices were allowed to move.
            if (m_LastResult != null && m_LastResult.featureDetectionSuspect)
                EditorGUILayout.HelpBox(
                    $"Feature detection marked {m_LastResult.featureVertexFraction * 100f:F0} % of " +
                    $"the input as creased, at {m_LastResult.profile.featureAngleDegrees:F0} " +
                    "degrees. That is the signature of a scan rather than modelled geometry - a " +
                    "dihedral test cannot tell a crease from noise, and each false crease holds a " +
                    "vertex still against surface relaxation. Turn Auto Profile back on, or pick " +
                    "Photogrammetry or Character yourself - both declare no creases.",
                    MessageType.Warning);

            if (m_Extraction != null)
                EditorGUILayout.HelpBox(m_Extraction.ToString(), MessageType.None);

            if (!string.IsNullOrEmpty(m_Status))
                EditorGUILayout.LabelField(m_Status, EditorStyles.wordWrappedMiniLabel);
        }

        // Development and comparison settings: the step-by-step field solve, the profile, the
        // extractor and its older options. Kept, not shown by default.
        void DrawAdvanced(Mesh mesh, GameObject selected, ref RemeshProfile profile)
        {
            EditorGUILayout.LabelField("Step by step (inspection)", EditorStyles.miniBoldLabel);

            using (new EditorGUI.DisabledScope(mesh == null))
            {
                if (GUILayout.Button("1 · Build Topology", GUILayout.Height(24)))
                    BuildTopology(selected, mesh);
            }


            if (m_Topology != null)
            {
                EditorGUILayout.HelpBox(m_Topology.ToString(), MessageType.None);


            }

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(m_Topology == null))
            {
                m_Iterations = Mathf.Clamp(EditorGUILayout.IntField(
                    new GUIContent("Smoothing Iterations",
                        "Gauss-Seidel sweeps. Stage 3 (hierarchy) will make these far cheaper."),
                    m_Iterations), 1, 2000);

                if (GUILayout.Button("2 · Solve Orientation Field", GUILayout.Height(24)))
                    SolveField();
            }

            if (m_Field != null)
            {
                EditorGUILayout.HelpBox(
                    $"Converged after {m_Field.iterationsRun} iterations\n" +
                    $"residual {m_Field.lastResidual:E2} rad " +
                    $"({m_Field.lastResidual * Mathf.Rad2Deg:F4}°)",
                    MessageType.None);

                EditorGUI.BeginChangeCheck();
                m_ShowField = EditorGUILayout.Toggle("Show Cross Field", m_ShowField);
                m_CrossScale = EditorGUILayout.Slider("Cross Size", m_CrossScale, 0.05f, 1.5f);
                m_MaxDrawn = EditorGUILayout.IntField("Max Drawn Vertices", m_MaxDrawn);
                if (EditorGUI.EndChangeCheck())
                {
                    RebuildSegments();
                    SceneView.RepaintAll();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pipeline", EditorStyles.boldLabel);

            m_AutoProfile = EditorGUILayout.Toggle(
                new GUIContent("Auto Profile",
                    "Read the profile off the input. A dihedral test that fires on a third of the " +
                    "mesh is measuring scanner noise rather than creases, and an input like that " +
                    "is sent to a profile which declares none. Turn this off to choose yourself."),
                m_AutoProfile);

            using (new EditorGUI.DisabledScope(m_AutoProfile))
            {
                m_ProfileKind = (RemeshProfileKind)EditorGUILayout.EnumPopup(
                    new GUIContent("Profile",
                        "Selects the pre-stages and how the shared stages are tuned. These " +
                        "settings are not independent, which is why they are presented as a " +
                        "profile rather than as separate checkboxes."),
                    m_ProfileKind);
            }

            profile = RemeshProfile.For(m_ProfileKind);

            // What the auto path decided LAST run, not a prediction of the next one. The decision
            // is made after decimation, on the mesh the solver actually sees, so there is nothing
            // honest to show here until a run has happened.
            if (m_AutoProfile)
            {
                var chosen = m_LastResult != null && m_LastResult.profileWasAutoSelected
                    ? $"chose {m_LastResult.profile.kind} last run - " +
                      $"{m_LastResult.profileProbeFeatureFraction:P0} of vertices creased at " +
                      $"{RemeshPipeline.autoProfileProbeAngleDegrees:F0} deg"
                    : "decided from the input, once it has been decimated";
                EditorGUILayout.LabelField(" ", chosen, EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(" ", profile.Describe(), EditorStyles.miniLabel);
            }



            m_ExtractorChoice = (RemeshExtractor)EditorGUILayout.EnumPopup(
                new GUIContent("Extractor",
                    "Which method turns the solved fields into quads. Integer Grid (default) " +
                    "solves one global integer map and reads a quad per cell - the family " +
                    "Exoside's engine is in: far fewer irregular vertices, closed output. " +
                    "Collapse is the older per-edge method, kept for comparison."),
                m_ExtractorChoice);

            EditorGUILayout.LabelField(" ", m_ExtractorChoice == RemeshExtractor.IntegerGrid
                ? "global integer map - torus 0 irregular, 100 % quads"
                : "per-edge collapse - torus 366 irregular, 97 % quads", EditorStyles.miniLabel);

            if (m_ExtractorChoice == RemeshExtractor.IntegerGrid)
            {

                m_GlobalParametrization = EditorGUILayout.Toggle(
                    new GUIContent("Global Parametrization",
                        "Place the quad grid with one global seamless parametrisation (the " +
                        "mixed-integer method FreeRemesher is built on) instead of the local " +
                        "position field: straighter loops, no shear bands between singularities, " +
                        "creases as exact grid lines. Meshes with an open border (scans) keep " +
                        "the previous extractor, which handles a ragged border better. Off is the " +
                        "previous extractor everywhere."),
                    m_GlobalParametrization);

            }

            if (m_ExtractorChoice == RemeshExtractor.IntegerGrid)
                m_CurvatureAlignment = EditorGUILayout.Slider(
                    new GUIContent("Curvature Alignment",
                        "Pull the quad directions toward the surface's principal curvature " +
                        "directions - rings round limbs and rims. 0 is off, which measured best " +
                        "on the sculpted head and body (fewer irregular vertices, lower error); " +
                        "try 0.1-0.3 on models with long clean cylindrical forms."),
                    m_CurvatureAlignment, 0f, 1f);

            if (m_ExtractorChoice == RemeshExtractor.IntegerGrid)
                m_PatchCurves = EditorGUILayout.Toggle(
                    new GUIContent("Patch Curves",
                        "Trace the model's ridges and valleys (brims, rims, panel edges) and " +
                        "cut the quad grid along them, so each becomes a clean edge loop - the " +
                        "way QuadRemesher cuts the surface into patches before solving. Edges come " +
                        "out about a third closer to the original. Best on gear with plates and " +
                        "panels (a vest comes out crisp); on thin shells like a helmet brim the " +
                        "edge can come out jagged, so leave it off there."),
                    m_PatchCurves);



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

        void BuildTopology(GameObject selected, Mesh mesh)
        {
            m_Source = selected;
            m_Field = null;
            m_SegmentsU = m_SegmentsV = null;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            m_Topology = RemeshTopology.Build(mesh);
            watch.Stop();

            m_Status = $"Topology built in {watch.ElapsedMilliseconds} ms.";
            Debug.Log($"[Native Remesher] {m_Topology} ({watch.ElapsedMilliseconds} ms)");
            SceneView.RepaintAll();
        }

        void SolveField()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                m_Field = OrientationField.Solve(m_Topology, m_Iterations, progress =>
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Native Remesher", "Solving orientation field...", progress))
                        throw new System.OperationCanceledException();
                });
            }
            catch (System.OperationCanceledException)
            {
                m_Status = "Cancelled.";
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            watch.Stop();

            RebuildSegments();
            m_Status = $"Orientation field solved in {watch.ElapsedMilliseconds} ms.";
            Debug.Log($"[Native Remesher] orientation field: {m_Field.iterationsRun} iterations, " +
                      $"residual {m_Field.lastResidual:E3} rad, {watch.ElapsedMilliseconds} ms");
            SceneView.RepaintAll();
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

        static string SaveMeshAsset(RemeshResult result, Mesh mesh, string sourceName)
        {
            const string k_Folder = "Assets/NativeRemesher/Output";

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
                var path = AssetDatabase.GenerateUniqueAssetPath($"{k_Folder}/{safe}_Quads.asset");

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
        CancellationTokenSource m_Cancel;
        GameObject m_JobSelected;
        System.Diagnostics.Stopwatch m_JobWatch;

        // Written by the worker, read by the main thread. A reference assignment and a float store
        // are both atomic, and neither side needs a consistent pair — this drives a progress bar,
        // so a stage label one frame out of step with its fraction is invisible.
        volatile string m_JobStage = "";
        volatile float m_JobProgress;

        bool isRunning => m_Job != null;

        /// <summary>Starts the pipeline on a worker thread.</summary>
        void RunPipeline()
        {
            var mesh = GetSelectedMesh(out var selected, out _);
            if (mesh == null || isRunning)
                return;

            var settings = RemeshSettings.Default;
            settings.targetQuadCount = m_TargetQuadCount;

            // The profile owns the sweep counts, so the window's single iteration field is not
            // applied here: it drives the step-by-step orientation button above, where seeing the
            // effect of more sweeps is the point.
            //
            // Under Auto Profile this is the SEED rather than the choice: the pipeline decimates
            // with it and then picks from the decimated mesh, which is the only population the
            // selection threshold was calibrated against. See RemeshPipeline.SelectProfileKind.
            var profile = RemeshProfile.For(m_ProfileKind);
            profile.extractor = m_ExtractorChoice;
            // A static the integer path reads; set here, on the main thread, before the job starts.
            Extraction.IntegerGridMap.curvatureAlignment =
                m_ExtractorChoice == RemeshExtractor.IntegerGrid ? m_CurvatureAlignment : 0f;
            Extraction.IntegerGridMap.curveNetwork =
                m_ExtractorChoice == RemeshExtractor.IntegerGrid && m_PatchCurves;
            Extraction.IntegerGridMap.adaptiveQuadCount = m_AdaptQuadCount;
            Extraction.IntegerGridMap.adaptiveSize = m_AdaptiveSize / 100f;
            Extraction.IntegerGridMap.coherentCreases = m_DetectHardEdges;
            Extraction.IntegerGridMap.parametrization = m_GlobalParametrization;
            Extraction.IntegerGridMap.parametrizationLayout = m_GlobalParametrization && m_QuadLayoutOn;
            var autoProfile = m_AutoProfile;

            // Main thread, and the only place the Mesh API is touched on the way in.
            //
            // The four-argument overload, and the UVs are passed to Solve below. This used to call
            // the three-argument one and drop them, so the window — the only path a user actually
            // takes — produced output with no UV channel and therefore no texture at all, while
            // PerfProbe passed them and measured 52,047 UVs on the same asset. The instrument was
            // right and disagreed with the product, which is the worst way for this to fail.
            RemeshTopology.ReadMesh(mesh, out var positions, out var triangles, out var sourceUvs);

            m_JobSelected = selected;
            m_JobStage = "Starting";
            m_JobProgress = 0f;
            m_JobWatch = System.Diagnostics.Stopwatch.StartNew();
            m_Cancel = new CancellationTokenSource();

            var token = m_Cancel.Token;

            m_Job = Task.Run(() => RemeshPipeline.Solve(positions, triangles, settings, profile,
                (stage, progress) =>
                {
                    // Cancellation is observed here rather than polled, because this is the only
                    // point the solver hands control back often enough to matter.
                    token.ThrowIfCancellationRequested();
                    m_JobStage = stage;
                    m_JobProgress = progress;
                }, sourceUvs, autoProfile), token);

            EditorApplication.update += OnJobUpdate;
        }

        void OnJobUpdate()
        {
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
                FinishJob(job.Result, selected);
            }
            catch (System.Exception e)
            {
                m_Status = "Failed: " + e.Message;
                Debug.LogException(e);
            }

            Repaint();
        }

        /// <summary>The main-thread half: build the mesh, save it, put it in the scene.</summary>
        void FinishJob(RemeshResult result, GameObject selected)
        {
            if (selected == null)
            {
                m_Status = "The source object was destroyed while the remesh was running.";
                return;
            }

            RemeshPipeline.BuildOutputMesh(result, selected.name, m_QuadTopology);

            m_Topology = result.topology;
            m_Field = result.orientation;
            m_Extraction = result.extraction;
            m_LastResult = result;
            RebuildSegments();

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

            var sourceRenderer = selected.GetComponent<Renderer>();
            var sourceMaterial = sourceRenderer != null ? sourceRenderer.sharedMaterial : null;

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
            for (var i = 0; i < slots; i++) materials[i] = sourceMaterial;
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

            m_Status = $"{profileNote}: {result.extraction} in {result.totalMs} ms" +
                       $"{decimated}{saved}";
            Debug.Log($"[Native Remesher] {profileNote}: {result.extraction} " +
                      $"(decimate {result.decimateMs}ms, topo {result.topologyMs}ms, " +
                      $"orient {result.orientationMs}ms, pos {result.positionMs}ms, " +
                      $"extract {result.extractionMs}ms, {result.faceCountAttempts} count passes)" +
                      decimated, outputMesh);
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------ drawing

        void RebuildSegments()
        {
            if (m_Field == null || m_Topology == null)
            {
                m_SegmentsU = m_SegmentsV = null;
                return;
            }

            var count = m_Topology.vertexCount;
            var stride = Mathf.Max(1, Mathf.CeilToInt(count / (float)Mathf.Max(1, m_MaxDrawn)));
            var length = m_CrossScale * m_Topology.averageEdgeLength;

            var u = new List<Vector3>();
            var v = new List<Vector3>();

            for (var i = 0; i < count; i += stride)
            {
                var p = m_Topology.positions[i];
                m_Field.GetCross(i, m_Topology.normals[i], out var du, out var dv);

                // Drawn as full crosses through the vertex, since a 4-RoSy direction and its
                // negation are the same thing — a one-sided arrow would imply a distinction
                // the field does not make.
                u.Add(p - du * length);
                u.Add(p + du * length);
                v.Add(p - dv * length);
                v.Add(p + dv * length);
            }

            m_SegmentsU = u.ToArray();
            m_SegmentsV = v.ToArray();
        }

        void OnSceneGUI(SceneView view)
        {
            if (!m_ShowField || m_SegmentsU == null || m_Source == null)
                return;

            using (new Handles.DrawingScope(m_Source.transform.localToWorldMatrix))
            {
                Handles.color = new Color(1f, 0.35f, 0.2f);
                Handles.DrawLines(m_SegmentsU);
                Handles.color = new Color(0.2f, 0.7f, 1f);
                Handles.DrawLines(m_SegmentsV);
            }
        }
    }
}
