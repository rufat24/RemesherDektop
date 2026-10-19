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
            var bakeResolution = m_BakeResolution;

            // Main thread: every part's arrays, materials and textures, and where it sits in the root.
            var images = new List<TextureBake.Image>();
            var read = new Dictionary<Texture, int>();
            var inputs = new List<PartBake.Part>();
            var areas = new List<double>();
            var materials = new List<Material[]>();
            var submeshes = new List<int[]>();
            var rawUvs = new List<Vector2[]>();
            var paints = new List<DensityPaint>();
            var floors = new List<int>();
            var toRoot = root.transform.worldToLocalMatrix;
            foreach (var go in parts)
            {
                RemeshTopology.ReadMesh(MeshOn(go), out var positions, out var triangles, out var uvs, out var submesh);
                var renderer = go.GetComponent<Renderer>();
                var mats = renderer != null ? renderer.sharedMaterials : new Material[0];
                var textures = bakeTexture && uvs != null ? ReadTextures(mats, submesh, triangles.Length / 3, images, read) : null;
                var toModel = toRoot * go.transform.localToWorldMatrix;
                inputs.Add(new PartBake.Part
                {
                    positions = positions, triangles = triangles, toModel = toModel,
                    uvs = textures != null ? uvs : null, triangleTexture = textures,
                });
                areas.Add(PartBake.Area(positions, triangles, toModel));
                floors.Add(PartBake.Floor(positions, triangles));
                materials.Add(mats);
                submeshes.Add(submesh);
                rawUvs.Add(uvs);
                paints.Add(PaintFor(go));
            }

            if (m_CombineParts)
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

            var targets = PartBake.SplitTarget(areas, settings.targetQuadCount, 16, floors);
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

            s_Scheduler ??= new BackgroundScheduler();
            m_PartsJob = s_Scheduler.Run(() =>
            {
                var run = new PartsRun { results = new List<RemeshResult>() };
                var n = inputs.Count;
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
                    for (var attempt = 0; attempt < 2 && (solved?.extraction == null || solved.extraction.faces.Count == 0); attempt++)
                    {
                        partSettings.targetQuadCount = attempt == 0 ? targets[p] : targets[p] * 4;
                        try
                        {
                            solved = RemeshPipeline.Solve(inputs[p].positions, inputs[p].triangles, partSettings, profile,
                                Progress, rawUvs[p], autoProfile, submeshes[p]);
                        }
                        catch (System.Exception e) when (!(e is System.OperationCanceledException))
                        {
                            Debug.LogWarning($"[Native Remesher] Part '{names[p]}' at {partSettings.targetQuadCount} quads: {e}");
                            solved = null;
                        }
                    }
                    if (solved?.extraction == null || solved.extraction.faces.Count == 0) solved = null;
                    inputs[p].result = solved;
                    run.results.Add(solved);
                }
                if (bakeTexture && imageArray.Length > 0 || bakeNormals)
                    run.shared = PartBake.Bake(inputs, imageArray, bakeTexture && imageArray.Length > 0, bakeNormals,
                        bakeResolution, (stage, progress) =>
                        {
                            token.ThrowIfCancellationRequested();
                            m_JobStage = $"All parts: {stage}";
                            m_JobProgress = (n + Mathf.Clamp01(progress)) / (n + 1);
                        });
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

            Texture2D baked = null, normalMap = null;
            if (run.shared != null)
            {
                var sharedPath = folder != null ? $"{folder}/{safe}_Quads.asset" : "";
                if (run.shared.bakedTexture != null) baked = SaveBakedTexture(run.shared, sharedPath, root.name);
                if (run.shared.bakedNormalMap != null) normalMap = SaveNormalMap(run.shared, sharedPath, root.name);
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

            m_Extraction = null;
            m_LastResult = run.results.Count > 0 ? run.results[0] : null;
            var bakeNote = run.shared?.bakeStats != null
                ? $"\none shared {run.shared.bakeStats.resolution}px bake for all parts, {run.shared.bakeStats.charts} charts, {run.shared.bakeStats.fill:P0} used"
                : "";
            var saved = folder != null ? $"\nsaved to {folder}" : "";
            var keptNote = unchanged.Count > 0
                ? $"\n{unchanged.Count} part(s) could not be remeshed and were kept as they were: {string.Join(", ", unchanged)}"
                : "";
            m_Status = $"{parts.Count} parts: {faces:N0} faces ({quads:N0} quads) in {ms / 1000.0:F1} s{saved}{bakeNote}{keptNote}";
            Debug.Log($"[Native Remesher] {root.name}: {m_Status.Replace('\n', ' ')}", top);
            SceneView.RepaintAll();
        }
    }
}
