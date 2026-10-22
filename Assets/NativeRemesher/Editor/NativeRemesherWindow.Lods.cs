using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace NativeRemesher
{
    // Levels of detail in one click: the selected object (a result made here, or any mesh model)
    // gets a LODGroup whose levels are its meshes reduced (LodChain), switching where each level's
    // measured error shrinks under a pixel budget. A result whose textures were baked here gets each
    // level baked again from the source, at a smaller size, into its own atlas; any other mesh keeps
    // its UVs and materials on every level.
    internal sealed partial class NativeRemesherWindow
    {
        [SerializeField] bool m_OpenLods = true;
        [SerializeField] bool m_MakeLods;
        [SerializeField] int m_LodLevels = 3;
        [SerializeField] float m_LodKeep = 50f;
        [SerializeField] float m_LodPixels = 1f;
        [SerializeField] bool m_LodOwnTextures = true;

        /// <summary>A part whose size on screen is under this many pixels where a level starts is left out of it.</summary>
        const float k_LodPartPixels = 3f;

        Task<LodRun> m_LodJob;
        string m_LodStatus = "";

        /// <summary>One mesh of the object: where it is, what it draws with, and its levels once built.</summary>
        sealed class LodPart
        {
            public MeshFilter filter;
            public Material[] materials;
            public Matrix4x4 toModel;
            public float scale;
            public LodChain.Input input;
            public List<LodChain.Level> levels;
            // Baked again per level from its source (null: shares the base's UVs and materials).
            public Vector3[] sourcePositions;
            public int[] sourceTriangles;
            public Vector2[] sourceUvs;
            public int[] sourceTexture;
            public long ms;
        }

        sealed class LodRun
        {
            public GameObject target;
            public List<LodPart> parts = new List<LodPart>();
            public LodChain.Settings settings;
            public TextureBake.Image[] images;
            public bool bakeTexture, bakeNormals;
            public int resolution;
            public float size;
            public Bounds bounds;
            // Per level (1..n): the switch heights of the whole object, and the shared bake.
            public List<LodChain.Level> group = new List<LodChain.Level>();
            public List<RemeshResult> bakes = new List<RemeshResult>();
            public long ms;
        }

        void DrawLodsSection(GameObject subject)
        {
            var summary = $"{m_LodLevels} levels, each {m_LodKeep:0}%" + (m_MakeLods ? ", after each remesh" : "");
            if (!Section(6, "Levels of Detail", summary, ref m_OpenLods)) return;

            m_LodLevels = EditorGUILayout.IntSlider(
                new GUIContent("Levels", "How many levels below the mesh itself (LOD1, LOD2, ...)."), m_LodLevels, 1, 6);
            m_LodKeep = EditorGUILayout.Slider(
                new GUIContent("Each level keeps (%)",
                    "Each level has this share of the triangles of the one before it. Quad models whose " +
                    "loops run cleanly lose whole quad strips and stay quads; other meshes are collapsed."),
                m_LodKeep, 10f, 90f);
            m_LodPixels = EditorGUILayout.Slider(
                new GUIContent("Switch under (pixels)",
                    "A level takes over once the change it makes covers at most this many pixels on a " +
                    "1080-pixel-high screen. Each level's change is measured, so the switch distances " +
                    "come from the mesh, not from a fixed table. Higher switches sooner."),
                m_LodPixels, 0.25f, 8f);
            m_LodOwnTextures = EditorGUILayout.ToggleLeft(
                new GUIContent("Bake each level its own smaller textures",
                    "For a result whose texture or normal map was baked here: each level is baked again " +
                    "from the source at half the size of the one before, so the detail it loses comes back " +
                    "as shading. Off, or on any other mesh, the levels draw with the mesh's own UVs and materials."),
                m_LodOwnTextures);
            m_MakeLods = EditorGUILayout.ToggleLeft(
                new GUIContent("Make them after each remesh", "Add the levels to every new result."), m_MakeLods);

            var target = LodTarget(subject);
            using (new EditorGUI.DisabledScope(isRunning || target == null))
            {
                var label = target != null ? $"Make LODs for '{target.name}'" : "Make LODs (select a model)";
                if (GUILayout.Button(label, GUILayout.Height(24))) MakeLods(target);
            }
            if (!string.IsNullOrEmpty(m_LodStatus))
                EditorGUILayout.LabelField(m_LodStatus, EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>The object a LODGroup goes on: the selection, or the result made from it.</summary>
        static GameObject LodTarget(GameObject subject)
        {
            var selected = Selection.activeGameObject;
            if (selected == null) return null;
            // A level object picked in the hierarchy: its group's owner.
            for (var t = selected.transform; t != null; t = t.parent)
                if (t.GetComponent<LODGroup>() != null) return t.gameObject;
            return selected.GetComponentInChildren<MeshFilter>(false) != null ? selected : null;
        }

        static bool IsLevelObject(Transform t, Transform target)
        {
            for (; t != null && t != target; t = t.parent)
                if (t.parent == target && t.name.StartsWith(target.name + "_LOD")) return true;
            return false;
        }

        void MakeLods(GameObject target)
        {
            if (isRunning || target == null) return;
            var group = target.GetComponent<LODGroup>();
            if (group != null)
            {
                // Only a group made here is replaced; one the model came with is its author's.
                var ours = true;
                var lods = group.GetLODs();
                for (var l = 1; l < lods.Length; l++)
                    foreach (var r in lods[l].renderers)
                        if (r != null && !IsLevelObject(r.transform, target.transform)) ours = false;
                if (!ours)
                {
                    m_LodStatus = $"'{target.name}' already has a LOD Group of its own; select its LOD 0 mesh instead.";
                    return;
                }
            }

            var run = new LodRun
            {
                target = target,
                settings = new LodChain.Settings
                {
                    levels = m_LodLevels, keep = m_LodKeep / 100f, pixelError = m_LodPixels,
                },
            };
            var toTarget = target.transform.worldToLocalMatrix;
            var images = new List<TextureBake.Image>();
            var read = new Dictionary<Texture, int>();
            var skinned = 0;
            foreach (var filter in target.GetComponentsInChildren<MeshFilter>(false))
            {
                if (IsLevelObject(filter.transform, target.transform)) continue;
                var renderer = filter.GetComponent<MeshRenderer>();
                var mesh = filter.sharedMesh;
                if (renderer == null || !renderer.enabled || mesh == null) continue;
                if (!mesh.isReadable)
                {
                    m_LodStatus = $"'{mesh.name}' is not readable: Inspector > Model > Read/Write Enabled > Apply.";
                    return;
                }
                RemeshTopology.ReadMesh(mesh, out var positions, out var triangles, out var uvs, out var submesh);
                // The file's quads back in order, as for a remesh: Unity's import shuffles their halves
                // apart, and the Porsche's body, doors, roof and wheels then read as triangles.
                QuadSource.Reorder(mesh, ref triangles, ref submesh);
                var part = new LodPart
                {
                    filter = filter,
                    materials = renderer.sharedMaterials,
                    toModel = toTarget * filter.transform.localToWorldMatrix,
                    input = LodChain.FromMesh(positions, triangles, uvs, submesh, mesh.subMeshCount),
                };
                part.scale = Mathf.Max(part.toModel.GetColumn(0).magnitude,
                    Mathf.Max(part.toModel.GetColumn(1).magnitude, part.toModel.GetColumn(2).magnitude));
                if (m_LodOwnTextures && BakedHere(part.materials, out var size))
                {
                    var source = filter.GetComponent<RemeshSource>()?.source;
                    var sourceMesh = source != null ? MeshOn(source) : null;
                    if (sourceMesh != null && sourceMesh != mesh && sourceMesh.isReadable)
                    {
                        RemeshTopology.ReadMesh(sourceMesh, out part.sourcePositions, out part.sourceTriangles,
                            out part.sourceUvs, out var sourceSubmesh);
                        var sourceRenderer = source.GetComponent<Renderer>();
                        if (part.sourceUvs != null && sourceRenderer != null && HasMainTexture(part.materials))
                            part.sourceTexture = ReadTextures(sourceRenderer.sharedMaterials, sourceSubmesh,
                                part.sourceTriangles.Length / 3, images, read);
                        run.resolution = Mathf.Max(run.resolution, size);
                        run.bakeNormals |= HasNormalMap(part.materials);
                        run.bakeTexture |= part.sourceTexture != null;
                    }
                }
                run.parts.Add(part);
            }
            foreach (var r in target.GetComponentsInChildren<SkinnedMeshRenderer>(false)) skinned++;
            if (run.parts.Count == 0)
            {
                m_LodStatus = skinned > 0 ? "Skinned meshes are not reduced yet." : "Nothing to reduce under the selection.";
                return;
            }
            run.images = images.Count > 0 ? images.ToArray() : null;
            var first = true;
            foreach (var part in run.parts)
            foreach (var p in part.input.positions)
            {
                var q = part.toModel.MultiplyPoint3x4(p);
                if (first) { run.bounds = new Bounds(q, Vector3.zero); first = false; }
                else run.bounds.Encapsulate(q);
            }
            run.size = Mathf.Max(run.bounds.size.x, Mathf.Max(run.bounds.size.y, run.bounds.size.z), 1e-6f);

            if (QuadReduction.logStats) foreach (var part in run.parts) Debug.Log($"[Native Remesher] LOD input '{part.filter.name}': {part.input.faces.Count} faces, scale {part.scale:G3}");
            m_JobStage = "Making LODs";
            m_JobProgress = 0f;
            m_JobWatch = System.Diagnostics.Stopwatch.StartNew();
            s_Scheduler ??= new BackgroundScheduler();
            m_LodJob = s_Scheduler.Run(() => BuildLods(run), default);
            EditorApplication.update += OnLodJobUpdate;
        }

        // Worker: each part's chain, the object's switch heights, and each level's bake.
        LodRun BuildLods(LodRun run)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var baked = run.bakeTexture || run.bakeNormals;
            // Parts side by side, largest first, so one big part does not finish alone at the end.
            var order = new List<int>();
            for (var i = 0; i < run.parts.Count; i++) order.Add(i);
            order.Sort((a, b) => run.parts[b].input.faces.Count.CompareTo(run.parts[a].input.faces.Count));
            var done = 0;
            Parallel.ForEach(order, new ParallelOptions { MaxDegreeOfParallelism = Mathf.Max(1, System.Environment.ProcessorCount / 2) }, i =>
            {
                var part = run.parts[i];
                var settings = new LodChain.Settings
                {
                    levels = run.settings.levels, keep = run.settings.keep, pixelError = run.settings.pixelError,
                    shareUvs = !(baked && part.sourcePositions != null),
                };
                var partWatch = System.Diagnostics.Stopwatch.StartNew();
                part.levels = LodChain.Build(part.input, settings);
                part.ms = partWatch.ElapsedMilliseconds;
                var n = System.Threading.Interlocked.Increment(ref done);
                m_JobStage = $"Reduced {n}/{run.parts.Count} parts";
                m_JobProgress = (float)n / run.parts.Count * (baked ? 0.5f : 1f);
            });
            // The object's level l: every part at its level l (or its last, when it stopped sooner),
            // its error the largest of theirs in the object's space.
            var count = 0;
            foreach (var part in run.parts) count = Mathf.Max(count, part.levels.Count);
            for (var l = 0; l < count; l++)
            {
                var error = 0f;
                foreach (var part in run.parts)
                    if (l > 0 && l < part.levels.Count) error = Mathf.Max(error, part.levels[l].error * part.scale);
                run.group.Add(new LodChain.Level { error = error });
            }
            LodChain.SwitchHeights(run.group, run.size, run.settings);

            if (baked)
            {
                for (var l = 1; l < count; l++)
                {
                    m_JobStage = $"Baking LOD{l}";
                    var parts = new List<PartBake.Part>();
                    foreach (var part in run.parts)
                    {
                        if (part.sourcePositions == null || l >= part.levels.Count) continue;
                        var level = part.levels[l];
                        parts.Add(new PartBake.Part
                        {
                            result = new RemeshResult { extraction = LodChain.ToExtraction(level, part.input.materialSlots) },
                            toModel = part.toModel,
                            positions = part.sourcePositions, triangles = part.sourceTriangles,
                            uvs = part.sourceTexture != null ? part.sourceUvs : null, triangleTexture = part.sourceTexture,
                        });
                    }
                    var resolution = LevelResolution(run, l);
                    var level0 = l;
                    var shared = parts.Count > 0
                        ? PartBake.Bake(parts, run.images, run.bakeTexture && run.images != null, run.bakeNormals, resolution,
                            (s, p) => m_JobProgress = 0.5f + 0.5f * (level0 - 1 + p) / (count - 1))
                        : null;
                    run.bakes.Add(shared);
                    // The bake gave each level its UVs and tangents on the extraction made above.
                    var k = 0;
                    foreach (var part in run.parts)
                    {
                        if (part.sourcePositions == null || l >= part.levels.Count) continue;
                        var ex = parts[k++].result.extraction;
                        part.levels[l].faceUvs = ex.faceUvs;
                        part.levels[l].faceTangents = ex.faceTangents;
                    }
                }
            }
            run.ms = watch.ElapsedMilliseconds;
            return run;
        }

        void OnLodJobUpdate()
        {
            if (m_LodJob == null) { EditorApplication.update -= OnLodJobUpdate; return; }
            if (!m_LodJob.IsCompleted) { Repaint(); return; }
            var job = m_LodJob;
            m_LodJob = null;
            EditorApplication.update -= OnLodJobUpdate;
            m_JobWatch?.Stop();
            if (job.IsFaulted || job.IsCanceled)
            {
                var inner = job.Exception?.GetBaseException();
                m_LodStatus = "LODs failed: " + (inner?.Message ?? "cancelled");
                if (inner != null) Debug.LogException(inner);
                Repaint();
                return;
            }
            try { FinishLods(job.Result); }
            catch (System.Exception e)
            {
                m_LodStatus = "LODs failed: " + e.Message;
                Debug.LogException(e);
            }
            Repaint();
        }

        void FinishLods(LodRun run)
        {
            var target = run.target;
            if (target == null) { m_LodStatus = "The object was removed while its LODs were made."; return; }
            var undoGroup = Undo.GetCurrentGroup();

            // The old levels made here go first.
            for (var i = target.transform.childCount - 1; i >= 0; i--)
            {
                var child = target.transform.GetChild(i);
                if (child.name.StartsWith(target.name + "_LOD")) Undo.DestroyObjectImmediate(child.gameObject);
            }

            var folder = LodFolder(run);
            var safe = string.Join("_", target.name.Split(Path.GetInvalidFileNameChars()));
            var lods = new List<LOD>();
            var lod0 = new List<Renderer>();
            foreach (var part in run.parts) lod0.Add(part.filter.GetComponent<Renderer>());
            lods.Add(new LOD(run.group[0].switchHeight, lod0.ToArray()));
            var lines = new List<string> { $"LOD0 {Triangles(run, 0):N0} tris" };

            for (var l = 1; l < run.group.Count; l++)
            {
                var holder = new GameObject($"{target.name}_LOD{l}");
                Undo.RegisterCreatedObjectUndo(holder, "Make LODs");
                holder.transform.SetParent(target.transform, false);
                var bake = l - 1 < run.bakes.Count ? run.bakes[l - 1] : null;
                var levelPath = folder != null ? $"{folder}/{safe}_LOD{l}.asset" : "";
                Texture2D albedo = null, normals = null;
                if (bake?.bakedTexture != null) albedo = SaveBakedTexture(bake, levelPath, $"{safe}_LOD{l}");
                if (bake?.bakedNormalMap != null) normals = SaveNormalMap(bake, levelPath, $"{safe}_LOD{l}");
                var madeMaterials = new Dictionary<Material, Material>();

                // Where this level starts: a part smaller than a few pixels there is left out.
                var starts = run.group[l - 1].switchHeight;
                var renderers = new List<Renderer>();
                var dropped = 0;
                foreach (var part in run.parts)
                {
                    var level = part.levels[Mathf.Min(l, part.levels.Count - 1)];
                    var partSize = LodChain.Size(part.input.positions) * part.scale;
                    if (partSize / run.size * starts * run.settings.screenHeight < k_LodPartPixels) { dropped++; continue; }

                    var ex = LodChain.ToExtraction(level, part.input.materialSlots);
                    var mesh = QuadExtractor.ToMesh(ex, $"{part.filter.sharedMesh.name}_LOD{l}", m_QuadTopology);
                    if (folder != null)
                    {
                        var meshPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SafeName(part.filter.sharedMesh.name)}_LOD{l}.asset");
                        AssetDatabase.CreateAsset(mesh, meshPath);
                    }
                    var node = new GameObject(part.filter.name);
                    node.transform.SetParent(holder.transform, false);
                    // The part's place in the object: its transform relative to the target.
                    var m = part.toModel;
                    node.transform.localPosition = m.GetColumn(3);
                    node.transform.localRotation = m.rotation;
                    node.transform.localScale = m.lossyScale;
                    node.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = node.AddComponent<MeshRenderer>();
                    var slots = Mathf.Max(1, mesh.subMeshCount);
                    var mats = new Material[slots];
                    for (var i = 0; i < slots; i++)
                    {
                        var from = part.materials.Length > 0 ? part.materials[Mathf.Min(i, part.materials.Length - 1)] : null;
                        mats[i] = part.sourcePositions != null && bake != null
                            ? LevelMaterial(from, albedo, normals, folder, $"{safe}_LOD{l}", madeMaterials)
                            : from;
                    }
                    renderer.sharedMaterials = mats;
                    renderers.Add(renderer);
                }
                lods.Add(new LOD(run.group[l].switchHeight, renderers.ToArray()));
                lines.Add($"LOD{l} {Triangles(run, l):N0} tris, from {run.group[l - 1].switchHeight:P0} of the screen" +
                          (dropped > 0 ? $", {dropped} small parts left out" : ""));
            }
            AssetDatabase.SaveAssets();

            // Not ??: a missing component is Unity's fake null, which ?? does not see.
            var group = target.GetComponent<LODGroup>();
            if (group == null) group = Undo.AddComponent<LODGroup>(target);
            Undo.RecordObject(group, "Make LODs");
            group.SetLODs(lods.ToArray());
            group.localReferencePoint = run.bounds.center;
            group.size = run.size;
            Undo.CollapseUndoOperations(undoGroup);

            var methods = new HashSet<string>();
            foreach (var part in run.parts)
                for (var l = 1; l < part.levels.Count; l++) methods.Add(part.levels[l].method);
            m_LodStatus = $"{run.group.Count - 1} levels in {run.ms / 1000f:0.0} s ({string.Join(", ", methods)}" +
                          $"{(run.bakes.Count > 0 ? ", baked per level" : "")}); culled under {run.group[run.group.Count - 1].switchHeight:P0}\n" +
                          string.Join("\n", lines);
            Debug.Log($"[Native Remesher] LODs for '{target.name}': {m_LodStatus}");
            if (QuadReduction.logStats)
                foreach (var part in run.parts)
                    Debug.Log($"[Native Remesher] LOD '{part.filter.name}' ({part.ms} ms, scale {part.scale:G3}): " + string.Join(" | ", part.levels));
            SceneView.RepaintAll();
        }

        /// <summary>
        /// A level's texture size: four times as many texels across as the object is pixels high
        /// where the level starts (a surface unfolds into more than its outline, and an atlas of
        /// many small charts loses texels to gutters), as a power of two between 512 and the base's
        /// size. Halving per level instead blotched the body's LOD2 into facets at 256; at 1024 its
        /// LOD3 shaded like LOD0. LODGROUP_RES_TEST overrides it, for measuring.
        /// </summary>
        static int LevelResolution(LodRun run, int level)
        {
            var forced = System.Environment.GetEnvironmentVariable("LODGROUP_RES_TEST");
            if (!string.IsNullOrEmpty(forced) && int.TryParse(forced, out var f)) return f;
            var pixels = run.group[level - 1].switchHeight * run.settings.screenHeight;
            return Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(4f * pixels)), 512, Mathf.Max(512, run.resolution));
        }

        static int Triangles(LodRun run, int level)
        {
            var total = 0;
            foreach (var part in run.parts) total += part.levels[Mathf.Min(level, part.levels.Count - 1)].Cost;
            return total;
        }

        static string SafeName(string name)
        {
            var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            return string.IsNullOrWhiteSpace(safe) ? "Mesh" : safe;
        }

        /// <summary>The folder the levels are saved in: beside the base mesh when it is an asset, else the output folder; null when not saving.</summary>
        string LodFolder(LodRun run)
        {
            if (!m_SaveAsAsset) return null;
            foreach (var part in run.parts)
            {
                var path = AssetDatabase.GetAssetPath(part.filter.sharedMesh);
                if (path.StartsWith("Assets/") && path.EndsWith(".asset")) return Path.GetDirectoryName(path).Replace('\\', '/');
            }
            if (!AssetDatabase.IsValidFolder(k_Folder))
            {
                if (!AssetDatabase.IsValidFolder("Assets/NativeRemesher")) AssetDatabase.CreateFolder("Assets", "NativeRemesher");
                AssetDatabase.CreateFolder("Assets/NativeRemesher", "Output");
            }
            return k_Folder;
        }

        static Material LevelMaterial(Material from, Texture2D albedo, Texture2D normals, string folder, string name,
            Dictionary<Material, Material> made)
        {
            if (from != null && made.TryGetValue(from, out var done)) return done;
            var mat = from != null ? new Material(from) : new Material(Shader.Find("Standard"));
            mat.name = name;
            if (albedo != null) mat.mainTexture = albedo;
            if (normals != null) AssignNormalMap(mat, normals);
            if (folder != null) AssetDatabase.CreateAsset(mat, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SafeName(name)}.mat"));
            if (from != null) made[from] = mat;
            return mat;
        }

        /// <summary>Whether a material set carries a texture or normal map baked here, and its size.</summary>
        static bool BakedHere(Material[] materials, out int size)
        {
            size = 0;
            foreach (var mat in materials)
            {
                if (mat == null) continue;
                foreach (var property in new[] { "_MainTex", "_BaseMap", "_BumpMap", "_NormalMap" })
                {
                    if (!mat.HasProperty(property)) continue;
                    var tex = mat.GetTexture(property);
                    if (!Ours(tex, "_Albedo") && !Ours(tex, "_Normal")) continue;
                    size = Mathf.Max(size, Mathf.Max(tex.width, tex.height));
                }
            }
            if (size > 0 && materials.Length > 0)
            {
                // The file's own size: the importer may have shrunk it.
                foreach (var mat in materials)
                {
                    var tex = mat != null ? (mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null) ?? mat.mainTexture : null;
                    if (tex != null && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) is TextureImporter importer)
                    {
                        importer.GetSourceTextureWidthAndHeight(out var w, out var h);
                        size = Mathf.Max(size, Mathf.Max(w, h));
                    }
                }
            }
            return size > 0;
        }

        /// <summary>
        /// A texture baked here for a base mesh: written to the output folder as "..._Normal" or
        /// "..._Albedo" (a level's own are "..._LODn_..."), or still in memory as "..._Quads_...".
        /// </summary>
        static bool Ours(Texture texture, string kind)
        {
            if (texture == null || !texture.name.EndsWith(kind) || texture.name.Contains("_LOD")) return false;
            var path = AssetDatabase.GetAssetPath(texture);
            return string.IsNullOrEmpty(path) ? texture.name.EndsWith("_Quads" + kind) : path.StartsWith(k_Folder + "/");
        }

        static bool HasMainTexture(Material[] materials)
        {
            foreach (var mat in materials)
                if (mat != null && Ours(mat.mainTexture, "_Albedo")) return true;
            return false;
        }

        static bool HasNormalMap(Material[] materials)
        {
            foreach (var mat in materials)
            {
                if (mat == null) continue;
                foreach (var property in new[] { "_BumpMap", "_NormalMap" })
                    if (mat.HasProperty(property) && Ours(mat.GetTexture(property), "_Normal")) return true;
            }
            return false;
        }
    }
}
