// Quad Remesher Bridge for Unity
// Port of the Blender bridge by Maxime Rouca (Exoside).
//
// Port of the "Quad Remesh" side panel (QREMESHER_PT_qremesher in __init__.py) and of the
// button operators that live next to it. The layout follows the Blender panel section by section
// so the two read the same way.

using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Exoside.QuadRemesher
{
    internal sealed class QuadRemesherWindow : EditorWindow
    {
        const string k_Version = "1.3";
        const string k_OnlineHelpUrl = "https://exoside.com/quadremesherdata/plugins_webdoc_link.php?App=Blender";

        Vector2 m_Scroll;
        bool m_ShowUnitySettings;

        [MenuItem("Tools/Quad Remesher/Quad Remesher", false, 0)]
        static void Open()
        {
            var window = GetWindow<QuadRemesherWindow>("Quad Remesher");
            window.minSize = new Vector2(300, 420);
            window.Show();
        }

        // Ctrl+Alt+R, the same binding the Blender addon installs for "Remesh It".
        [MenuItem("Tools/Quad Remesher/Remesh It %&r", false, 20)]
        static void RemeshIt()
        {
            QuadRemesherBridge.Remesh();
        }

        [MenuItem("Tools/Quad Remesher/Remesh It %&r", true)]
        static bool RemeshItValidate()
        {
            return !QuadRemesherBridge.isRemeshing;
        }

        void OnEnable()
        {
            QuadRemesherBridge.changed += Repaint;
            Selection.selectionChanged += Repaint;
        }

        void OnDisable()
        {
            QuadRemesherBridge.changed -= Repaint;
            Selection.selectionChanged -= Repaint;
        }

        void OnGUI()
        {
            var settings = QuadRemesherSettings.instance;

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            EditorGUILayout.LabelField("Quad Remesher " + k_Version, EditorStyles.boldLabel);
            EditorGUILayout.Space();

            DrawEngineStatus();
            DrawRemeshButton();

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(QuadRemesherBridge.isRemeshing))
            {
                settings.targetCount = EditorGUILayout.IntField(
                    new GUIContent("Quad Count", "Set the desired number of Quads"),
                    settings.targetCount);

                EditorGUILayout.Space();
                DrawQuadSizeSettings(settings);
                EditorGUILayout.Space();
                DrawEdgeLoopsControl(settings);
                EditorGUILayout.Space();
                DrawMisc(settings);
                EditorGUILayout.Space();
                DrawUnitySettings(settings);
            }

            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ sections

        void DrawEngineStatus()
        {
            var enginePath = QuadRemesherEngine.ResolveEnginePath();
            if (enginePath != null)
                return;

            EditorGUILayout.HelpBox(
                "QuadRemesher engine not found.\n\nThis bridge drives the same xremesh engine the " +
                "Blender / 3ds Max / Maya bridges use. Install it with one of those, or point at " +
                "the folder containing xremesh.exe below.",
                MessageType.Warning);

            if (GUILayout.Button("Locate engine folder..."))
            {
                var folder = EditorUtility.OpenFolderPanel("Select the folder containing xremesh", "", "");
                if (!string.IsNullOrEmpty(folder))
                {
                    QuadRemesherSettings.instance.engineFolderOverride = folder;
                    if (QuadRemesherEngine.ResolveEnginePath() == null)
                    {
                        EditorUtility.DisplayDialog("Quad Remesher",
                            "No xremesh executable in that folder.", "OK");
                        QuadRemesherSettings.instance.engineFolderOverride = "";
                    }
                }
            }

            EditorGUILayout.Space();
        }

        void DrawRemeshButton()
        {
            if (QuadRemesherBridge.isRemeshing)
            {
                var rect = EditorGUILayout.GetControlRect(false, 24);
                EditorGUI.ProgressBar(rect, QuadRemesherBridge.progress, QuadRemesherBridge.statusText);

                if (GUILayout.Button("Cancel", GUILayout.Height(24)))
                    QuadRemesherBridge.Cancel();

                Repaint();
                return;
            }

            var canRemesh = QuadRemesherBridge.TryGetSelection(out _, out _, out _, out var error)
                            && QuadRemesherEngine.IsInstalled;

            using (new EditorGUI.DisabledScope(!canRemesh))
            {
                if (GUILayout.Button("<<  REMESH IT  >>", GUILayout.Height(30)))
                    QuadRemesherBridge.Remesh();
            }

            if (error != null)
                EditorGUILayout.LabelField(error, EditorStyles.wordWrappedMiniLabel);
            else if (!string.IsNullOrEmpty(QuadRemesherBridge.statusText))
                EditorGUILayout.LabelField(QuadRemesherBridge.statusText, EditorStyles.wordWrappedMiniLabel);
        }

        static void DrawQuadSizeSettings(QuadRemesherSettings settings)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Quad Size Settings", EditorStyles.boldLabel);

                settings.adaptiveSize = EditorGUILayout.Slider(
                    new GUIContent("Adaptive Size (%)", QuadRemesherSettings.CurvatureAdaptivnessTooltip),
                    settings.adaptiveSize, 0f, 100f);

                settings.adaptQuadCount = EditorGUILayout.Toggle(
                    new GUIContent("Adapt Quad Count", QuadRemesherSettings.AdaptQuadCountTooltip),
                    settings.adaptQuadCount);
            }
        }

        static void DrawEdgeLoopsControl(QuadRemesherSettings settings)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Edge Loops Control", EditorStyles.boldLabel);

                settings.useMaterials = EditorGUILayout.Toggle(
                    new GUIContent("Use Materials", QuadRemesherSettings.UseMaterialsTooltip),
                    settings.useMaterials);

                settings.useNormals = EditorGUILayout.Toggle(
                    new GUIContent("Use Normals Splitting", QuadRemesherSettings.UseNormalsTooltip),
                    settings.useNormals);

                settings.autoDetectHardEdges = EditorGUILayout.Toggle(
                    new GUIContent("Detect Hard Edges by angle", QuadRemesherSettings.DetectHardEdgesTooltip),
                    settings.autoDetectHardEdges);
            }
        }

        void DrawMisc(QuadRemesherSettings settings)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Misc", EditorStyles.boldLabel);

                EditorGUILayout.LabelField(new GUIContent("Symmetry", QuadRemesherSettings.SymmetryTooltip));
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.Space(EditorGUIUtility.labelWidth - 12, false);
                    settings.symmetryX = GUILayout.Toggle(settings.symmetryX, "X", EditorStyles.miniButtonLeft);
                    settings.symmetryY = GUILayout.Toggle(settings.symmetryY, "Y", EditorStyles.miniButtonMid);
                    settings.symmetryZ = GUILayout.Toggle(settings.symmetryZ, "Z", EditorStyles.miniButtonRight);
                }

                settings.hideInput = EditorGUILayout.Toggle(
                    new GUIContent("Hide Input Object", QuadRemesherSettings.HideInputTooltip),
                    settings.hideInput);

                EditorGUILayout.Space();

                if (GUILayout.Button("Reset Settings"))
                    settings.ResetToDefaults();

                if (GUILayout.Button("License Manager"))
                    LaunchLicenseManager();

                if (GUILayout.Button("Online Help"))
                    Application.OpenURL(k_OnlineHelpUrl);
            }
        }

        void DrawUnitySettings(QuadRemesherSettings settings)
        {
            m_ShowUnitySettings = EditorGUILayout.Foldout(m_ShowUnitySettings, "Unity Bridge Settings", true);
            if (!m_ShowUnitySettings)
                return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                settings.outputFolder = EditorGUILayout.TextField(
                    new GUIContent("Output Folder", "Project folder the retopo meshes are saved into."),
                    settings.outputFolder);

                EditorGUILayout.Space();

                var engineFolder = QuadRemesherEngine.ResolveEngineFolder();
                EditorGUILayout.LabelField("Engine", engineFolder ?? "(not found)",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("Host App", QuadRemesherEngine.ResolveHostApp(),
                    EditorStyles.miniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Override engine folder..."))
                    {
                        var folder = EditorUtility.OpenFolderPanel(
                            "Select the folder containing xremesh", engineFolder ?? "", "");
                        if (!string.IsNullOrEmpty(folder))
                            settings.engineFolderOverride = folder;
                    }

                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(settings.engineFolderOverride)))
                    {
                        if (GUILayout.Button("Clear", GUILayout.Width(60)))
                            settings.engineFolderOverride = "";
                    }
                }

                EditorGUILayout.HelpBox(
                    "Meshes go to the engine as .obj and come back as .fbx (the engine reads .obj " +
                    "but cannot write it). The result .fbx is kept in the output folder, so the " +
                    "real quad topology survives for a round-trip back out to a DCC.\n\n" +
                    "Vertex-colour quad density (the Blender addon's paint workflow) is not " +
                    "available: the engine only reads vertex colours from .fbx input.",
                    MessageType.None);
            }
        }

        // ------------------------------------------------------------------ operators

        /// <summary>Port of QREMESHER_OT_license_manager.</summary>
        static void LaunchLicenseManager()
        {
            var path = QuadRemesherEngine.ResolveLicenseManagerPath();
            if (path == null)
            {
                EditorUtility.DisplayDialog("Quad Remesher",
                    "xrLicenseManager was not found next to the engine.", "OK");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(path)
                {
                    Arguments = "-hostApp " + QuadRemesherEngine.ResolveHostApp() +
                                " -haVer " + Application.unityVersion,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar))
                });
            }
            catch (Exception e)
            {
                Debug.LogError("[Quad Remesher] Could not launch the License Manager (" + path + "): " + e.Message);
            }
        }
    }
}
