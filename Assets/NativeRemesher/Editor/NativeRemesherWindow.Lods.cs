using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace NativeRemesher
{
    // Levels of detail in one click: the selected object (a result made here, or any mesh model)
    // gets a LODGroup switching where each level's measured error shrinks under a pixel budget.
    // By default each level is the source remeshed again at its own quad count -- counted down
    // from the base's budget, or typed in per level -- through the same pipeline as the Remesh
    // button (parts, hidden parts, bakes). Off, the levels are the base's meshes reduced
    // (LodChain): a result whose textures were baked here gets each level baked again from the
    // source into its own atlas, and any other mesh keeps its UVs and materials on every level.
    internal sealed partial class NativeRemesherWindow
    {
        [SerializeField] bool m_OpenLods = true;
        [SerializeField] bool m_MakeLods;
        [SerializeField] int m_LodLevels = 3;
        [SerializeField] float m_LodKeep = 50f;
        [SerializeField] float m_LodPixels = 1f;
        [SerializeField] bool m_LodOwnTextures = true;
        [SerializeField] bool m_LodRemesh = true;
        [SerializeField] bool m_LodAutoQuads = true;
        [SerializeField] int[] m_LodQuads = new int[0];

        /// <summary>
        /// The share of the surface a remeshed level's error is measured over. Two remeshes differ
        /// most at thin parts (fingers, straps) that cover few pixels, and the very largest gap
        /// put the body's LOD1 at a 46-pixel-high body.
        /// </summary>
        public static float lodErrorShare = 0.99f;

        // The LOD settings' version: a window saved before remeshed levels existed comes back with
        // their switches off (Unity restores a field missing from the saved window as false), not
        // at their defaults.
        [SerializeField] int m_LodSettingsVersion;

        void UpgradeLodSettings()
        {
            if (m_LodSettingsVersion >= 1) return;
            m_LodRemesh = true;
            m_LodAutoQuads = true;
            m_LodSettingsVersion = 1;
        }

        /// <summary>Auto stops counting down below this many quads: a level that small is a box.</summary>
        const int k_LodMinQuads = 32;

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
            var target = LodTarget(subject);
            var auto = target != null ? AutoLevelQuads(target) : new int[0];
            var summary = (m_LodRemesh
                              ? m_LodAutoQuads ? $"{auto.Length} remeshed levels, auto" : $"{m_LodLevels} remeshed levels, by hand"
                              : $"{m_LodLevels} levels, each {m_LodKeep:0}%") +
                          (m_MakeLods ? ", after each remesh" : "");
            if (!Section(6, "Levels of Detail", summary, ref m_OpenLods)) return;

            m_LodRemesh = EditorGUILayout.ToggleLeft(
                new GUIContent("Remesh each level from the source",
                    "Each level is the source remeshed again at its own quad count, as the Remesh button " +
                    "would make it (parts, hidden parts and bakes included). Off, the levels are this " +
                    "mesh reduced: quicker, but below the first level a remesh runs out of clean loops " +
                    "and the levels turn to triangles."),
                m_LodRemesh);
            m_LodLevels = EditorGUILayout.IntSlider(
                new GUIContent("Levels", "How many levels below the mesh itself (LOD1, LOD2, ...)."), m_LodLevels, 1, 6);
            if (m_LodRemesh)
            {
                m_LodAutoQuads = EditorGUILayout.Popup(
                    new GUIContent("Quads per level",
                        "Auto: each level has the share below of the quads of the one before, counted down " +
                        "from the quads the result has (LOD1 at the Quads count for a model not made " +
                        "here), and stops before a level would be under " + k_LodMinQuads + " quads. " +
                        "By hand: type each level's quads. Either way a level is remeshed to its count " +
                        "exactly, without curves adding quads."),
                    m_LodAutoQuads ? 0 : 1, new[] { new GUIContent("Auto"), new GUIContent("By hand") }) == 0;
                if (m_LodAutoQuads)
                {
                    m_LodKeep = EditorGUILayout.Slider(
                        new GUIContent("Each level keeps (%)", "The share of the quads of the level before."),
                        m_LodKeep, 10f, 90f);
                    if (auto.Length > 0)
                    {
                        var counts = new List<string>();
                        for (var k = 0; k < auto.Length; k++) counts.Add($"LOD{k + 1} {auto[k]:N0}");
                        EditorGUILayout.LabelField(" ", string.Join("   ", counts) + " quads", EditorStyles.miniLabel);
                    }
                }
                else
                {
                    ManualLevelQuads(auto);
                    for (var k = 0; k < m_LodLevels; k++)
                        m_LodQuads[k] = Mathf.Max(4, EditorGUILayout.IntField($"LOD{k + 1} quads", m_LodQuads[k]));
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(new GUIContent("Fill from Auto", "Auto's counts for this object as a start."),
                                EditorStyles.miniButton, GUILayout.Width(100)))
                        {
                            m_LodQuads = new int[0];
                            ManualLevelQuads(auto);
                        }
                    }
                }
            }
            else
            {
                m_LodKeep = EditorGUILayout.Slider(
                    new GUIContent("Each level keeps (%)",
                        "Each level has this share of the triangles of the one before it. Quad models whose " +
                        "loops run cleanly lose whole quad strips and stay quads; other meshes are collapsed."),
                    m_LodKeep, 10f, 90f);
            }
            m_LodPixels = EditorGUILayout.Slider(
                new GUIContent("Switch under (pixels)",
                    "A level takes over once the change it makes covers at most this many pixels on a " +
                    "1080-pixel-high screen. Each level's change is measured, so the switch distances " +
                    "come from the mesh, not from a fixed table. Higher switches sooner."),
                m_LodPixels, 0.25f, 8f);
            if (!m_LodRemesh)
                m_LodOwnTextures = EditorGUILayout.ToggleLeft(
                new GUIContent("Bake each level its own smaller textures",
                    "For a result whose texture or normal map was baked here: each level is baked again " +
                    "from the source at half the size of the one before, so the detail it loses comes back " +
                    "as shading. Off, or on any other mesh, the levels draw with the mesh's own UVs and materials."),
                m_LodOwnTextures);
            m_MakeLods = EditorGUILayout.ToggleLeft(
                new GUIContent("Make them after each remesh", "Add the levels to every new result."), m_MakeLods);

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

        /// <summary>Whether the object has no LOD group, or only one made here (one the model came with is its author's).</summary>
        bool GroupIsOurs(GameObject target)
        {
            var group = target.GetComponent<LODGroup>();
            if (group == null) return true;
            var lods = group.GetLODs();
            for (var l = 1; l < lods.Length; l++)
                foreach (var r in lods[l].renderers)
                    if (r != null && !IsLevelObject(r.transform, target.transform))
                    {
                        m_LodStatus = $"'{target.name}' already has a LOD Group of its own; select its LOD 0 mesh instead.";
                        return false;
                    }
            return true;
        }

        void MakeLods(GameObject target)
        {
            if (isRunning || target == null) return;
            if (!GroupIsOurs(target)) return;
            if (m_LodRemesh)
            {
                StartLevelChain(target);
                return;
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

        // ------------------------------------------------------------------ levels remeshed

        /// <summary>Levels being remeshed one after another, each a run of the window's own pipeline.</summary>
        sealed class LevelChain
        {
            public GameObject target, source;
            public int[] quads;
            public int[] resolutions;   // per level, or null: the window's texture size
            public int next;            // the level being made is LOD(next + 1)
            public bool running, cancelled;
            public List<GameObject> made = new List<GameObject>();
            public List<long> ms = new List<long>();
            public System.Diagnostics.Stopwatch watch, levelWatch;
            public float baseQuads;     // the base's own, as quads
        }

        LevelChain m_LevelChain;

        string LevelChainStage() =>
            m_LevelChain != null ? $"LOD{m_LevelChain.next + 1} of {m_LevelChain.quads.Length} ({m_LevelChain.quads[Mathf.Min(m_LevelChain.next, m_LevelChain.quads.Length - 1)]:N0} quads): " : "";

        /// <summary>
        /// Auto's quad counts: from the quads the result has (or, for a model not made here, LOD1
        /// at the window's Quads count, under the model's own size), each level the kept share of
        /// the one before, stopping before one would be under k_LodMinQuads. From what the base
        /// has rather than what it was asked for: with curves adding quads a scan's base came out
        /// at three times its count, and LOD1 at half the count would have been two thirds smaller.
        /// </summary>
        int[] AutoLevelQuads(GameObject target)
        {
            var link = target.GetComponent<RemeshSource>();
            var keep = Mathf.Clamp(m_LodKeep / 100f, 0.1f, 0.9f);
            var from = link != null && link.source != null
                ? QuadsOf(target)
                : Mathf.Min(m_TargetQuadCount, QuadsOf(target) * keep) / keep;
            var counts = new List<int>();
            for (var k = 1; k <= m_LodLevels; k++)
            {
                var q = Mathf.RoundToInt(from * Mathf.Pow(keep, k));
                if (q < k_LodMinQuads || counts.Count > 0 && q >= counts[counts.Count - 1]) break;
                counts.Add(q);
            }
            return counts.ToArray();
        }

        /// <summary>The by-hand counts, one per level, the missing ones filled from Auto's (or halving).</summary>
        void ManualLevelQuads(int[] auto)
        {
            if (m_LodQuads == null) m_LodQuads = new int[0];
            if (m_LodQuads.Length >= m_LodLevels) return;
            var grown = new int[m_LodLevels];
            for (var k = 0; k < grown.Length; k++)
                grown[k] = k < m_LodQuads.Length && m_LodQuads[k] > 0 ? m_LodQuads[k]
                    : k < auto.Length ? auto[k]
                    : k > 0 ? Mathf.Max(4, grown[k - 1] / 2) : Mathf.Max(4, m_TargetQuadCount / 2);
            m_LodQuads = grown;
        }

        /// <summary>The object's faces as quads (a triangle a half), its level objects left out.</summary>
        static float QuadsOf(GameObject target)
        {
            long indices4 = 0, indices3 = 0;
            foreach (var filter in target.GetComponentsInChildren<MeshFilter>(false))
            {
                if (IsLevelObject(filter.transform, target.transform) || filter.sharedMesh == null) continue;
                var mesh = filter.sharedMesh;
                for (var i = 0; i < mesh.subMeshCount; i++)
                    if (mesh.GetTopology(i) == MeshTopology.Quads) indices4 += mesh.GetIndexCount(i);
                    else if (mesh.GetTopology(i) == MeshTopology.Triangles) indices3 += mesh.GetIndexCount(i);
            }
            return indices4 / 4f + indices3 / 6f;
        }

        void StartLevelChain(GameObject target)
        {
            var quads = m_LodAutoQuads ? AutoLevelQuads(target) : null;
            if (!m_LodAutoQuads)
            {
                ManualLevelQuads(AutoLevelQuads(target));
                quads = new int[m_LodLevels];
                System.Array.Copy(m_LodQuads, quads, m_LodLevels);
            }
            if (quads.Length == 0)
            {
                m_LodStatus = $"'{target.name}' is already under {k_LodMinQuads} quads a level; nothing to count down to.";
                return;
            }
            var chain = new LevelChain
            {
                target = target, source = SourceOf(target), quads = quads,
                // A model not made here has no remeshed base: its first level is the measure.
                baseQuads = target.GetComponent<RemeshSource>() != null ? QuadsOf(target) : quads[0],
                watch = System.Diagnostics.Stopwatch.StartNew(),
            };

            // A base baked here sets the levels' texture size: as many texels per quad's width
            // as the base, so a level with a quarter of the quads gets half the size, not under 512.
            var materials = new List<Material>();
            foreach (var r in target.GetComponentsInChildren<Renderer>(false))
                if (!IsLevelObject(r.transform, target.transform)) materials.AddRange(r.sharedMaterials);
            var baseSize = BakedHere(materials.ToArray(), out var size) ? size : m_TextureSize;
            if (baseSize > 0)
            {
                var reference = Mathf.Max(QuadsOf(target), quads[0]);
                chain.resolutions = new int[quads.Length];
                for (var k = 0; k < quads.Length; k++)
                    chain.resolutions[k] = Mathf.Clamp(
                        Mathf.NextPowerOfTwo(Mathf.CeilToInt(baseSize * Mathf.Sqrt(quads[k] / reference))), 512, Mathf.Max(512, baseSize));
            }

            // The old levels made here go first.
            for (var i = target.transform.childCount - 1; i >= 0; i--)
            {
                var child = target.transform.GetChild(i);
                if (child.name.StartsWith(target.name + "_LOD")) Undo.DestroyObjectImmediate(child.gameObject);
            }
            m_LevelChain = chain;
            m_LodStatus = $"Remeshing {quads.Length} levels: {string.Join(", ", quads)} quads";
            m_JobWatch = System.Diagnostics.Stopwatch.StartNew();
            EditorApplication.update -= OnLevelsUpdate;
            EditorApplication.update += OnLevelsUpdate;
        }

        // Between the runs: the level just made taken under the target, then the next one started.
        void OnLevelsUpdate()
        {
            var chain = m_LevelChain;
            if (chain == null) { EditorApplication.update -= OnLevelsUpdate; return; }
            if (jobRunning) return;
            if (chain.running)
            {
                chain.running = false;
                var made = m_LastMade;
                var link = made != null ? made.GetComponent<RemeshSource>() : null;
                if (chain.cancelled || made == null || made == chain.target || link == null || link.source != chain.source ||
                    chain.made.Contains(made))
                {
                    EndLevelChain(false, chain.cancelled ? "Levels cancelled." : $"LOD{chain.next + 1} was not made: {m_Status}");
                    return;
                }
                made.name = $"{chain.target.name}_LOD{chain.next + 1}";
                // The result sits where the source does, as the target does: kept in place under it.
                made.transform.SetParent(chain.target.transform, true);
                chain.made.Add(made);
                chain.ms.Add(chain.levelWatch.ElapsedMilliseconds);
                chain.next++;
            }
            if (chain.cancelled) { EndLevelChain(false, "Levels cancelled."); return; }
            if (chain.target == null || chain.source == null) { EndLevelChain(false, "The object was removed while its levels were made."); return; }
            if (chain.next >= chain.quads.Length) { EndLevelChain(true, null); return; }

            // The window's own run on the source, at this level's count (and texture size), the
            // count exact: a level is the number it was given, not one that curves add to.
            Selection.activeGameObject = chain.source;
            m_LastMade = null;
            var quadsWas = m_TargetQuadCount;
            var textureWas = m_TextureSize;
            var curvesWas = m_CurvesAddQuads;
            m_TargetQuadCount = chain.quads[chain.next];
            if (chain.resolutions != null) m_TextureSize = chain.resolutions[chain.next];
            m_CurvesAddQuads = false;
            // The least each piece of a part is given shrinks with the level: held at LOD0's, the
            // Porsche's rear lights (17 pieces) kept 340 quads at LOD3 while its body had 115.
            var perPieceWas = PartBake.quadsPerPiece;
            PartBake.quadsPerPiece = Mathf.Clamp(Mathf.RoundToInt(perPieceWas * chain.quads[chain.next] / Mathf.Max(1f, chain.baseQuads)),
                4, perPieceWas);
            try { RunPipeline(); }
            finally
            {
                m_TargetQuadCount = quadsWas;
                m_TextureSize = textureWas;
                m_CurvesAddQuads = curvesWas;
                PartBake.quadsPerPiece = perPieceWas;
            }
            if (!jobRunning) { EndLevelChain(false, $"LOD{chain.next + 1} could not start: {m_Status}"); return; }
            chain.running = true;
            chain.levelWatch = System.Diagnostics.Stopwatch.StartNew();
        }

        void EndLevelChain(bool ok, string message)
        {
            var chain = m_LevelChain;
            m_LevelChain = null;
            EditorApplication.update -= OnLevelsUpdate;
            if (!ok)
            {
                foreach (var go in chain.made) if (go != null) Undo.DestroyObjectImmediate(go);
                if (chain.target != null) Selection.activeGameObject = chain.target;
                m_LodStatus = message;
                Repaint();
                return;
            }

            // Each level's error against the base, in the target's space, sets where it takes over:
            // part by part (a level mirrors the base's hierarchy), over the parts seen from outside.
            // Over the whole car, its seats and dash -- collapsed differently at each count, and
            // never on screen -- put LOD1's switch at a 51-pixel-high car.
            var target = chain.target;
            var toTarget = target.transform.worldToLocalMatrix;
            var lod0 = new List<Renderer>();
            foreach (var r in target.GetComponentsInChildren<Renderer>(false))
                if (!IsLevelObject(r.transform, target.transform)) lod0.Add(r);
            var baseParts = new List<PartBake.Part>();
            var occludes = new List<bool>();
            var bounds = new Bounds();
            var first = true;
            foreach (var r in lod0)
            {
                Surface(new[] { r }, toTarget, out var positions, out var triangles);
                baseParts.Add(new PartBake.Part { positions = positions, triangles = triangles });
                occludes.Add(!SeeThrough(r.sharedMaterials));
                foreach (var p in positions)
                {
                    if (first) { bounds = new Bounds(p, Vector3.zero); first = false; }
                    else bounds.Encapsulate(p);
                }
            }
            var size = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z), 1e-6f);
            var seen = new bool[lod0.Count];
            var visibility = lod0.Count > 1 ? PartBake.Visibility(baseParts, occludes) : null;
            var anySeen = false;
            for (var p = 0; p < seen.Length; p++)
            {
                seen[p] = baseParts[p].triangles.Length >= 3 &&
                          (visibility == null || PartBake.VisibilityWeight(visibility[p]) >= hiddenCollapseWeight);
                anySeen |= seen[p];
            }
            if (!anySeen) for (var p = 0; p < seen.Length; p++) seen[p] = baseParts[p].triangles.Length >= 3;
            var baseGrids = new SpatialGrid[lod0.Count];
            var baseInputs = new LodChain.Input[lod0.Count];
            var samples = Mathf.Clamp(80000 / Mathf.Max(1, lod0.Count), 2000, 20000);
            var reducedParts = new List<string>();

            var levels = new List<LodChain.Level> { new LodChain.Level() };
            var renderers = new List<List<Renderer>> { lod0 };
            var partSizes = new List<float[]> { null };
            foreach (var go in chain.made)
            {
                var own = new List<Renderer>(go.GetComponentsInChildren<Renderer>(false));
                var byPath = new Dictionary<string, int>();
                for (var i = 0; i < own.Count; i++) byPath[PathUnder(own[i].transform, go.transform)] = i;
                var sizes = new float[own.Count];
                float error = 0f, worst = 0f;
                for (var i = 0; i < own.Count; i++)
                {
                    Surface(new[] { own[i] }, toTarget, out var positions, out var triangles);
                    sizes[i] = LodChain.Size(positions);
                }
                for (var p = 0; p < lod0.Count; p++)
                {
                    if (!seen[p]) continue;
                    if (!byPath.TryGetValue(PathUnder(lod0[p].transform, target.transform), out var i)) continue;
                    Surface(new[] { own[i] }, toTarget, out var positions, out var triangles);
                    if (triangles.Length < 3) continue;
                    var basePart = baseParts[p];
                    baseGrids[p] ??= new SpatialGrid(basePart.positions, basePart.triangles);
                    var grid = new SpatialGrid(positions, triangles);
                    var partError = LodChain.Error(positions, triangles, grid, baseGrids[p],
                        basePart.positions, basePart.triangles, samples, lodErrorShare);
                    // The part's base reduced to the same triangles, when that stays closer: a
                    // remesh at a few hundred quads can tear (the Porsche's hood at LOD3), a
                    // reduction of what LOD0 already has cannot, and it draws with LOD0's textures.
                    if (ReducedCloser(lod0[p], own[i], toTarget, triangles.Length / 3, basePart, baseGrids[p], samples,
                            partError, ref baseInputs[p], out var reducedError, out var reducedPositions, out var reducedTriangles))
                    {
                        partError = reducedError;
                        positions = reducedPositions;
                        triangles = reducedTriangles;
                        grid = new SpatialGrid(positions, triangles);
                        reducedParts.Add($"{lod0[p].name} (LOD{levels.Count})");
                    }
                    error = Mathf.Max(error, partError);
                    worst = Mathf.Max(worst, LodChain.Error(positions, triangles, grid, baseGrids[p],
                        basePart.positions, basePart.triangles, samples));
                }
                levels.Add(new LodChain.Level { error = error, note = $"worst {worst / size:P2}" });
                renderers.Add(own);
                partSizes.Add(sizes);
            }
            LodChain.SwitchHeights(levels, size, new LodChain.Settings { pixelError = m_LodPixels });

            // Where a level starts, a part a few pixels across is left out of it.
            var lods = new LOD[levels.Count];
            var faces = new List<int>();
            var dropped = new int[levels.Count];
            for (var l = 0; l < lods.Length; l++)
            {
                var kept = new List<Renderer>();
                for (var i = 0; i < renderers[l].Count; i++)
                {
                    var r = renderers[l][i];
                    if (l > 0 && partSizes[l][i] / size * levels[l - 1].switchHeight * 1080f < k_LodPartPixels)
                    {
                        Undo.RecordObject(r, "Make LODs");
                        r.enabled = false;
                        dropped[l]++;
                        continue;
                    }
                    kept.Add(r);
                }
                lods[l] = new LOD(levels[l].switchHeight, kept.ToArray());
                faces.Add(TrianglesOf(kept));
            }
            var group = target.GetComponent<LODGroup>();
            if (group == null) group = Undo.AddComponent<LODGroup>(target);
            Undo.RecordObject(group, "Make LODs");
            group.SetLODs(lods);
            group.localReferencePoint = bounds.center;
            group.size = size;
            Selection.activeGameObject = target;

            var lines = new List<string> { $"LOD0 {faces[0]:N0} tris" };
            for (var l = 1; l < levels.Count; l++)
                lines.Add($"LOD{l} {faces[l]:N0} tris ({chain.quads[l - 1]:N0} quads asked, {chain.ms[l - 1] / 1000f:0} s), " +
                          $"off by {levels[l].error / size:P2} of the size ({levels[l].note}), from {levels[l - 1].switchHeight:P0} of the screen" +
                          (dropped[l] > 0 ? $", {dropped[l]} small parts left out" : ""));
            if (reducedParts.Count > 0)
                lines.Add($"reduced from LOD0 where closer than the remesh: {string.Join(", ", reducedParts)}");
            m_LodStatus = $"{chain.made.Count} remeshed levels in {chain.watch.ElapsedMilliseconds / 1000f:0} s; " +
                          $"culled under {levels[levels.Count - 1].switchHeight:P0}\n" + string.Join("\n", lines);
            Debug.Log($"[Native Remesher] LODs for '{target.name}': {m_LodStatus}");
            SceneView.RepaintAll();
            Repaint();
        }

        /// <summary>A reduction of the base part replaces the level's remesh when its error is under this share of the remesh's.</summary>
        public static float lodReducedCloser = 0.8f;

        /// <summary>A reduction with more than this many times the remesh's triangles is not used (it would not be the level's size).</summary>
        public static float lodReducedOver = 1.15f;

        /// <summary>
        /// The base part reduced (LodChain, sharing its UVs) to the level part's triangles; when it
        /// is closer to the base than the level's remesh, it takes the remesh's place, drawn with
        /// the base part's materials. Out: its error and its surface in the target's space.
        /// </summary>
        bool ReducedCloser(Renderer basePart, Renderer levelPart, Matrix4x4 toTarget, int triangles,
            PartBake.Part baseSurface, SpatialGrid baseGrid, int samples, float remeshError, ref LodChain.Input input,
            out float error, out Vector3[] positions, out int[] surface)
        {
            error = remeshError;
            positions = null;
            surface = null;
            if (remeshError <= 0f) return false;
            var baseFilter = basePart.GetComponent<MeshFilter>();
            var levelFilter = levelPart.GetComponent<MeshFilter>();
            var baseMesh = baseFilter != null ? baseFilter.sharedMesh : null;
            if (baseMesh == null || levelFilter == null || !baseMesh.isReadable) return false;
            if (input == null)
            {
                RemeshTopology.ReadMesh(baseMesh, out var p, out var t, out var uvs, out var submesh);
                QuadSource.Reorder(baseMesh, ref t, ref submesh);
                input = LodChain.FromMesh(p, t, uvs, submesh, baseMesh.subMeshCount);
            }
            var baseTriangles = baseSurface.triangles.Length / 3;
            if (triangles >= baseTriangles) return false;
            // Within its UV charts first; held there (a baked base's atlas has hundreds of them,
            // and the Porsche's fender stopped at twice its count), across them, its UVs sampled.
            LodChain.Level level = null;
            foreach (var hold in new[] { true, false })
            {
                var levels = LodChain.Build(input, new LodChain.Settings
                {
                    levels = 1, keep = Mathf.Clamp((float)triangles / baseTriangles, 0.02f, 0.95f), shareUvs = true, holdSeams = hold,
                });
                if (levels.Count < 2) continue;
                level = levels[1];
                if (level.Cost <= triangles * lodReducedOver) break;
                level = null;
            }
            if (level == null) return false;
            var tris = LodChain.Triangulate(level.faces, out _);
            var toModel = toTarget * basePart.transform.localToWorldMatrix;
            var moved = new Vector3[level.positions.Length];
            for (var i = 0; i < moved.Length; i++) moved[i] = toModel.MultiplyPoint3x4(level.positions[i]);
            var reducedError = LodChain.Error(moved, tris, null, baseGrid, baseSurface.positions, baseSurface.triangles,
                samples, lodErrorShare);
            if (reducedError >= remeshError * lodReducedCloser) return false;

            var mesh = QuadExtractor.ToMesh(LodChain.ToExtraction(level, input.materialSlots), levelFilter.sharedMesh.name, m_QuadTopology);
            var meshPath = AssetDatabase.GetAssetPath(levelFilter.sharedMesh);
            if (!string.IsNullOrEmpty(meshPath) && meshPath.EndsWith(".asset"))
                AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath(
                    $"{Path.GetDirectoryName(meshPath).Replace('\\', '/')}/{SafeName(baseMesh.name)}_reduced.asset"));
            Undo.RecordObject(levelFilter, "Make LODs");
            levelFilter.sharedMesh = mesh;
            var from = basePart.sharedMaterials;
            var mats = new Material[Mathf.Max(1, mesh.subMeshCount)];
            for (var i = 0; i < mats.Length; i++) mats[i] = from.Length > 0 ? from[Mathf.Min(i, from.Length - 1)] : null;
            Undo.RecordObject(levelPart, "Make LODs");
            levelPart.sharedMaterials = mats;
            error = reducedError;
            positions = moved;
            surface = tris;
            return true;
        }

        static string PathUnder(Transform t, Transform top)
        {
            var names = new List<string>();
            for (; t != null && t != top; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }

        /// <summary>The renderers' meshes as one triangle surface in the target's space.</summary>
        static void Surface(IList<Renderer> renderers, Matrix4x4 toTarget, out Vector3[] positions, out int[] triangles)
        {
            var p = new List<Vector3>();
            var t = new List<int>();
            foreach (var r in renderers)
            {
                var filter = r != null ? r.GetComponent<MeshFilter>() : null;
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !mesh.isReadable) continue;
                RemeshTopology.ReadMesh(mesh, out var v, out var tri);
                var m = toTarget * r.transform.localToWorldMatrix;
                var offset = p.Count;
                foreach (var q in v) p.Add(m.MultiplyPoint3x4(q));
                foreach (var i in tri) t.Add(i + offset);
            }
            positions = p.ToArray();
            triangles = t.ToArray();
        }

        static int TrianglesOf(IList<Renderer> renderers)
        {
            var triangles = 0;
            foreach (var r in renderers)
            {
                var filter = r != null ? r.GetComponent<MeshFilter>() : null;
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;
                for (var i = 0; i < mesh.subMeshCount; i++)
                    triangles += (int)(mesh.GetTopology(i) == MeshTopology.Quads ? mesh.GetIndexCount(i) / 2 : mesh.GetIndexCount(i) / 3);
            }
            return triangles;
        }
    }
}
