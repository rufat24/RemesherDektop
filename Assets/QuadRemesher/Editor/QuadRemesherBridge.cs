// Quad Remesher Bridge for Unity
// Port of the Blender bridge by Maxime Rouca (Exoside).
//
// Port of doRemeshing_Start / doRemeshing_Finish and the modal timer loop from qr_operators.py.
//
// The flow is the engine's, unchanged: write the mesh and a settings file to a scratch folder,
// launch xremesh on it, poll a progress file until it reports success, then read the result back.
// Blender drives the poll from a modal operator timer; here it is EditorApplication.update.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Exoside.QuadRemesher
{
    internal static class QuadRemesherBridge
    {
        /// <summary>Matches the 0.3 s modal timer the Blender addon uses.</summary>
        const double k_PollInterval = 0.3;

        public static bool isRemeshing { get; private set; }
        /// <summary>Remeshing progress in [0, 1], as last reported by the engine.</summary>
        public static float progress { get; private set; }
        public static string statusText { get; private set; } = "";

        /// <summary>Raised whenever progress or status changed, so the window can repaint.</summary>
        public static event Action changed;

        static GameObject s_InputObject;
        static Mesh s_InputMesh;
        static Material[] s_InputMaterials;
        static string s_RetopoFilename;

        static readonly QuadRemesherProgressReader s_Progress = new QuadRemesherProgressReader();
        static double s_NextPollTime;
        static bool s_AssembliesLocked;

        // ------------------------------------------------------------------ start

        /// <summary>Validates the selection and starts the engine. Errors are reported and returned.</summary>
        public static void Remesh()
        {
            if (isRemeshing)
                return;

            ResetState();

            if (!TryGetSelection(out s_InputObject, out s_InputMesh, out s_InputMaterials, out var selectionError))
            {
                Fail(selectionError);
                return;
            }

            var engineFolder = QuadRemesherEngine.ResolveEngineFolder();
            if (engineFolder == null)
            {
                Fail("QuadRemesher engine not found. Install it (via the Blender / 3ds Max / Maya " +
                     "bridge) or set the engine folder explicitly in the Quad Remesher window.");
                return;
            }

            var enginePath = QuadRemesherEngine.ResolveEnginePath();
            var tempFolder = QuadRemesherEngine.TempFolder();

            var settingsFilename = QuadRemesherEngine.UnixifyPath(Path.Combine(tempFolder, "RetopoSettings.txt"));
            // In .obj, out .fbx: the engine reads .obj but cannot write it -- asking it for
            // .obj output makes it remesh correctly and then fail with a bare
            // "Error: Remeshing Failed." Verified against the engine: .fbx, .exm and .exomesh
            // all succeed on the identical input that .obj fails on.
            var inputFilename = QuadRemesherEngine.UnixifyPath(Path.Combine(tempFolder, "inputMesh.obj"));
            s_RetopoFilename = QuadRemesherEngine.UnixifyPath(Path.Combine(tempFolder, "retopo.fbx"));
            s_Progress.progressFilename = QuadRemesherEngine.UnixifyPath(Path.Combine(tempFolder, "progress.txt"));

            // 1.0 - remove results of a previous run, so a stale file cannot be mistaken for a new one
            TryDelete(s_RetopoFilename);
            TryDelete(s_Progress.progressFilename);

            // 1.1 - export the selected mesh
            var retopoName = s_InputObject.name + "_Retopo";
            ObjExportResult export;
            try
            {
                export = ObjExporter.Write(inputFilename, s_InputMesh, s_InputMaterials, s_InputObject.name);
            }
            catch (Exception e)
            {
                Fail("Failed to export the selected mesh: " + e.Message);
                return;
            }

            if (export.faceCount == 0)
            {
                Fail($"'{s_InputObject.name}' has no triangles to remesh.");
                return;
            }

            // 1.2 - write the settings file
            try
            {
                WriteSettingsFile(settingsFilename, inputFilename, s_RetopoFilename,
                    s_Progress.progressFilename, retopoName);
            }
            catch (Exception e)
            {
                Fail("Failed to write the settings file: " + e.Message);
                return;
            }

            // 2 - start remeshing
            s_Progress.OnStartRemeshing();
            try
            {
                var startInfo = new ProcessStartInfo(enginePath)
                {
                    Arguments = "-s \"" + settingsFilename + "\"",
                    UseShellExecute = false,
                    // The engine surfaces its own progress, EULA and activation windows.
                    CreateNoWindow = false,
                    WorkingDirectory = engineFolder.Replace('/', Path.DirectorySeparatorChar)
                };
                s_Progress.remeshingProcess = Process.Start(startInfo);
            }
            catch (Exception e)
            {
                Fail("Cannot execute the remesher engine (" + enginePath + "): " + e.Message);
                return;
            }

            isRemeshing = true;
            progress = 0f;
            statusText = "Starting...";

            // A domain reload would wipe the state below and orphan the engine process.
            EditorApplication.LockReloadAssemblies();
            s_AssembliesLocked = true;

            s_NextPollTime = EditorApplication.timeSinceStartup + k_PollInterval;
            EditorApplication.update += OnEditorUpdate;

            Notify();
        }

        /// <summary>Aborts the run and kills the engine process (Blender: ESC in the modal loop).</summary>
        public static void Cancel()
        {
            if (!isRemeshing)
                return;

            try
            {
                var process = s_Progress.remeshingProcess;
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch (Exception)
            {
                // Already gone; nothing to kill.
            }

            EndRun();
            statusText = "Remeshing cancelled.";
            Notify();
        }

        // ------------------------------------------------------------------ poll

        static void OnEditorUpdate()
        {
            if (!isRemeshing)
            {
                EditorApplication.update -= OnEditorUpdate;
                return;
            }

            if (EditorApplication.timeSinceStartup < s_NextPollTime)
                return;

            var status = s_Progress.GetStatus();

            // The engine can advise how long to wait before the next poll.
            var interval = status.sleepTimeAdvice.HasValue && status.sleepTimeAdvice.Value > 0f
                ? Math.Min(status.sleepTimeAdvice.Value, 1.0)
                : k_PollInterval;
            s_NextPollTime = EditorApplication.timeSinceStartup + interval;

            if (Math.Abs(status.value - QuadRemesherProgressCode.NoProgressFile) < 0.0001f)
            {
                EndRun();
                Fail("Remeshing FAILED! (no progress file after 40 s)");
                return;
            }

            if (Math.Abs(status.value - QuadRemesherProgressCode.BadProgressFile) < 0.0001f)
                return; // partial write, just poll again

            if (Math.Abs(status.value - QuadRemesherProgressCode.NeedsUserAction) < 0.0001f)
            {
                EndRun();
                statusText = "The engine needs your input (EULA or activation). Press Remesh It again.";
                Notify();
                return;
            }

            if (status.value < 0f)
            {
                EndRun();
                Fail(string.IsNullOrEmpty(status.text) ? "Remeshing FAILED!" : status.text);
                return;
            }

            if (status.isSuccess)
            {
                var retopoFile = s_RetopoFilename;
                var inputObject = s_InputObject;
                var inputMaterials = s_InputMaterials;

                EndRun();
                Finish(retopoFile, inputObject, inputMaterials);
                return;
            }

            if (status.isComputing)
            {
                progress = status.value;
                statusText = $"Remeshing... {Mathf.RoundToInt(99f * progress + 1f)}%";
                Notify();
            }
        }

        // ------------------------------------------------------------------ finish

        static void Finish(string retopoFile, GameObject inputObject, Material[] inputMaterials)
        {
            if (!File.Exists(retopoFile))
            {
                Fail("The engine reported success but wrote no result file.");
                return;
            }

            var settings = QuadRemesherSettings.instance;
            var folder = EnsureOutputFolder(settings.outputFolder);

            var assetPath = AssetDatabase.GenerateUniqueAssetPath(
                folder + "/" + SanitizeFileName(inputObject.name) + "_Retopo.fbx");

            try
            {
                File.Copy(retopoFile, assetPath, true);
            }
            catch (Exception e)
            {
                Fail("Could not copy the result into the project: " + e.Message);
                return;
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            ConfigureRetopoImporter(assetPath);

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null)
            {
                Fail("Unity could not import the result at " + assetPath);
                return;
            }

            var undoGroup = Undo.GetCurrentGroup();

            var retopoObject = (GameObject)PrefabUtility.InstantiatePrefab(model);
            retopoObject.name = inputObject.name + "_Retopo";
            Undo.RegisterCreatedObjectUndo(retopoObject, "Quad Remesh");

            var inputTransform = inputObject.transform;
            retopoObject.transform.SetParent(inputTransform.parent, false);
            retopoObject.transform.localPosition = inputTransform.localPosition;
            retopoObject.transform.localRotation = inputTransform.localRotation;
            retopoObject.transform.localScale = inputTransform.localScale;

            // The engine preserves material ids in submesh order, so the originals map straight back.
            var vertexCount = 0;
            var triangleCount = 0;
            foreach (var renderer in retopoObject.GetComponentsInChildren<MeshRenderer>())
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    continue;

                vertexCount += filter.sharedMesh.vertexCount;
                triangleCount += filter.sharedMesh.triangles.Length / 3;
                renderer.sharedMaterials = MaterialsForSubmeshes(filter.sharedMesh.subMeshCount, inputMaterials);
            }

            if (settings.hideInput)
            {
                Undo.RecordObject(inputObject, "Quad Remesh");
                inputObject.SetActive(false);
                EditorUtility.SetDirty(inputObject);
            }

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = retopoObject;

            // Unity triangulates on import, so the quad count is inferred; the .fbx asset itself
            // keeps the real quads for a round-trip back out to a DCC.
            statusText = $"Done: ~{triangleCount / 2:N0} quads, {vertexCount:N0} vertices.";
            Debug.Log($"[Quad Remesher] {statusText} Saved to {assetPath}", model);
            Notify();
        }

        /// <summary>
        /// The engine writes its FBX in centimetres, so Unity's importer would shrink the result
        /// 100x against the object it came from. Take Unity's unit as authoritative instead.
        /// </summary>
        static void ConfigureRetopoImporter(string assetPath)
        {
            if (!(AssetImporter.GetAtPath(assetPath) is ModelImporter importer))
                return;

            importer.useFileScale = false;
            importer.globalScale = 1f;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importBlendShapes = false;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.SaveAndReimport();
        }

        /// <summary>Puts the source object's materials back on the result, submesh for submesh.</summary>
        static Material[] MaterialsForSubmeshes(int submeshCount, Material[] inputMaterials)
        {
            var materials = new Material[Mathf.Max(1, submeshCount)];

            for (var i = 0; i < materials.Length; i++)
            {
                if (inputMaterials == null || inputMaterials.Length == 0)
                    break;
                materials[i] = i < inputMaterials.Length ? inputMaterials[i] : inputMaterials[0];
            }

            return materials;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Finds the single selected mesh to remesh. Mirrors the Blender addon, which refuses
        /// anything other than exactly one mesh object.
        /// </summary>
        public static bool TryGetSelection(out GameObject go, out Mesh mesh, out Material[] materials,
            out string error)
        {
            go = null;
            mesh = null;
            materials = null;

            var selected = Selection.gameObjects;
            if (selected == null || selected.Length != 1)
            {
                error = "You must select one and only one object.";
                return false;
            }

            go = selected[0];

            var meshFilter = go.GetComponent<MeshFilter>();
            var skinned = go.GetComponent<SkinnedMeshRenderer>();

            if (meshFilter != null && meshFilter.sharedMesh != null)
                mesh = meshFilter.sharedMesh;
            else if (skinned != null && skinned.sharedMesh != null)
                mesh = skinned.sharedMesh;

            if (mesh == null)
            {
                error = $"'{go.name}' has no mesh (a MeshFilter or SkinnedMeshRenderer is required).";
                return false;
            }

            if (!mesh.isReadable)
            {
                error = $"The mesh '{mesh.name}' is not readable. Enable Read/Write in its model " +
                        "import settings and try again.";
                return false;
            }

            var renderer = go.GetComponent<Renderer>();
            materials = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();

            error = null;
            return true;
        }

        /// <summary>Writes the engine's settings file. Keys and values mirror the Blender addon.</summary>
        static void WriteSettingsFile(string path, string fileIn, string fileOut, string progressFile,
            string retopoNodeName)
        {
            var settings = QuadRemesherSettings.instance;
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            sb.Append("HostApp=").Append(QuadRemesherEngine.ResolveHostApp()).Append('\n');
            sb.Append("HostAppVer=").Append(Application.unityVersion).Append('\n');
            sb.Append("FileIn=\"").Append(fileIn).Append("\"\n");
            sb.Append("FileOut=\"").Append(fileOut).Append("\"\n");
            sb.Append("ProgressFile=\"").Append(progressFile).Append("\"\n");
            sb.Append("RetopoNodeName=").Append(retopoNodeName).Append('\n');

            sb.Append("TargetQuadCount=").Append(settings.targetCount.ToString(inv)).Append('\n');
            sb.Append("CurvatureAdaptivness=").Append(settings.adaptiveSize.ToString("0.###", inv)).Append('\n');
            sb.Append("ExactQuadCount=").Append(settings.adaptQuadCount ? 0 : 1).Append('\n');

            // Vertex-colour driven density needs a per-vertex colour channel, which .obj cannot
            // carry; the engine only reads it from .fbx. Always off for this bridge.
            sb.Append("UseVertexColorMap=False\n");

            sb.Append("UseMaterialIds=").Append(settings.useMaterials ? 1 : 0).Append('\n');
            sb.Append("UseIndexedNormals=").Append(settings.useNormals ? 1 : 0).Append('\n');
            sb.Append("AutoDetectHardEdges=").Append(settings.autoDetectHardEdges ? 1 : 0).Append('\n');

            var symAxis = "";
            if (settings.symmetryX) symAxis += "X";
            if (settings.symmetryY) symAxis += "Y";
            if (settings.symmetryZ) symAxis += "Z";
            if (symAxis.Length > 0)
            {
                sb.Append("SymAxis=").Append(symAxis).Append('\n');
                sb.Append("SymLocal=1\n");
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string EnsureOutputFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets", StringComparison.Ordinal))
                folder = "Assets/QuadRemesher/Output";

            if (AssetDatabase.IsValidFolder(folder))
                return folder;

            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }

            return current;
        }

        static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Quad Remesher] Could not delete " + path + ": " + e.Message);
            }
        }

        /// <summary>Tears down a run, leaving the result fields the caller may still need.</summary>
        static void EndRun()
        {
            isRemeshing = false;
            progress = 0f;
            EditorApplication.update -= OnEditorUpdate;
            s_Progress.Reset();

            if (s_AssembliesLocked)
            {
                EditorApplication.UnlockReloadAssemblies();
                s_AssembliesLocked = false;
            }
        }

        static void ResetState()
        {
            s_InputObject = null;
            s_InputMesh = null;
            s_InputMaterials = null;
            s_RetopoFilename = null;
            progress = 0f;
            statusText = "";
        }

        static void Fail(string message)
        {
            statusText = message;
            Debug.LogError("[Quad Remesher] " + message);
            Notify();
        }

        static void Notify()
        {
            changed?.Invoke();
        }
    }
}
