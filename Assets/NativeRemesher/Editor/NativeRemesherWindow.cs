// Native Remesher for Unity — editor front end.
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

        // Each Model Type's own settings: what that kind of model is remeshed with, each a measured
        // default that a model may still want off. See RemeshPipeline's statics of the same meaning.
        [SerializeField] bool m_ShowTypeSettings;
        [SerializeField] bool m_RoundParts = true;              // all: KinematicSurfaces on whole pieces
        [SerializeField] bool m_CharacterSourceLoops = true;    // quadReductionCharacter
        [SerializeField] bool m_CharacterStraps = true;         // ribbonParts
        [SerializeField] bool m_CharacterThinRows = true;       // thicknessSizing
        [SerializeField] bool m_HardSourceLoops = true;         // quadReductionHardSurface
        [SerializeField] bool m_HardDenseToNormalMap = true;    // hardSurfaceSparseCreases
        [SerializeField] bool m_HardThinStrips = true;          // stripSizing
        [SerializeField] bool m_HardRods = true;                // hardSurfaceRods
        [SerializeField] bool m_HardMergeFlat = true;           // hardSurfaceSimplify
        // Renamed from m_HardRowsAtCount / m_HardRoundedEdges so open windows pick up the default off.
        [SerializeField] bool m_HardEvenRows;                   // hardSurfaceCleanLayout
        [SerializeField] bool m_HardAcrossRounding;             // hardSurfaceRoundedCreases
        [SerializeField] bool m_HardFlatPanels = true;          // hardSurfaceFair
        [SerializeField] bool m_HardToCount;                    // hardSurfaceSimplifyToCount
        [SerializeField] bool m_HardPlateRows = true;           // hardSurfacePlateBlocks
        [SerializeField] bool m_HardShapeRegions = true;        // kinematicRegionsHardSurface
        [SerializeField] bool m_ScanShapeRegions = true;        // kinematicRegionsScan

        // The quad settings. See the matching statics on Extraction.IntegerGridMap.
        [SerializeField] int m_TargetQuadCount = 2000;
        [SerializeField] float m_CurveDetail = 50f;
        [SerializeField] bool m_CurvesAddQuads = true;
        [SerializeField] bool m_LongCreaseLoops = true;
        [SerializeField] SymmetryAxis m_Symmetry = SymmetryAxis.None;
        [SerializeField] bool m_MaterialBorders;

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
            UpgradeLodSettings();
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
            EditorApplication.update -= OnLevelsUpdate;
            m_Cancel?.Cancel();
        }

        // Which sections are open, and where the window is scrolled.
        [SerializeField] bool m_OpenModel = true, m_OpenCount = true, m_OpenLoops = true, m_OpenFix, m_OpenResult = true;
        Vector2 m_Scroll;

        static readonly ModelType[] k_Types = { ModelType.Auto, ModelType.Character, ModelType.HardSurface, ModelType.Scan };
        static readonly string[] k_TypeNames = { "Auto", "Character", "Hard Surface", "Scan" };
        static readonly string[] k_TypeNotes =
        {
            "Each part is read on its own, from its shape: sculpted, modelled or scanned.",
            "Sculpted or organic: fingers and toes get rows of quads, straps and belts become tubes.",
            "Modelled: clean-quad parts keep their own loops, panel lines become loops, tread and " +
            "grilles go to the normal map.",
            "Photogrammetry or a laser scan: noise is not taken for edges, walls and columns get rows " +
            "along them.",
        };

        void OnGUI()
        {
            var mesh = GetSelectedMesh(out var selected, out var error);
            // A model in parts: every part under the selection, not the first one found.
            var parts = GetSelectedParts(out var partsRoot, out var partsError);
            var multi = parts.Count > 1;
            if (multi) error = partsError;

            DrawTitle(multi ? PartsSummary(partsRoot, parts) : mesh != null ? MeshSummary(selected, mesh) : null);
            if (error != null)
                EditorGUILayout.HelpBox(error, MessageType.Info);

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            DrawModelSection(multi);
            DrawCountSection();
            DrawLoopsSection();
            if (Section(4, "Fix Areas", FixAreasSummary(mesh != null ? selected : null), ref m_OpenFix))
                DrawPaintGUI(mesh != null ? selected : null);
            DrawResultSection(multi ? partsRoot : selected);
            DrawLodsSection(multi ? partsRoot : selected);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            DrawRunControls(mesh, selected, multi, parts, partsRoot, partsError);
        }

        // ------------------------------------------------------------------ layout

        static GUIStyle s_Title, s_Subtitle, s_SectionTitle, s_SectionSummary;

        static void Styles()
        {
            if (s_Title != null) return;
            s_Title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };
            s_Subtitle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            s_SectionTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12, alignment = TextAnchor.MiddleLeft };
            s_SectionSummary = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Clip };
        }

        static Color Accent => new Color(0.31f, 0.66f, 0.58f);

        void DrawTitle(string subject)
        {
            Styles();
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Native Remesher", s_Title, GUILayout.Height(22));
            EditorGUILayout.LabelField(subject ?? "Nothing selected", s_Subtitle);
            var line = GUILayoutUtility.GetRect(0f, 2f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(line, Accent);
        }

        // A section's header bar: its number and name, what it is set to (so a folded section still
        // reads), and a click anywhere on it to fold.
        static bool Section(int number, string title, string summary, ref bool open)
        {
            Styles();
            EditorGUILayout.Space(8);
            var rect = GUILayoutUtility.GetRect(0f, 24f, GUILayout.ExpandWidth(true));
            var dark = EditorGUIUtility.isProSkin;
            EditorGUI.DrawRect(rect, dark ? new Color(0.20f, 0.22f, 0.24f) : new Color(0.80f, 0.82f, 0.84f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 3f, rect.height), Accent);
            var arrow = open ? "\u25BE" : "\u25B8";
            GUI.Label(new Rect(rect.x + 10f, rect.y, rect.width * 0.55f, rect.height), $"{arrow}  {number}  {title}", s_SectionTitle);
            if (!string.IsNullOrEmpty(summary))
                GUI.Label(new Rect(rect.x + rect.width * 0.45f, rect.y, rect.width * 0.55f - 8f, rect.height), summary, s_SectionSummary);
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                open = !open;
                e.Use();
            }
            if (open) EditorGUILayout.Space(4);
            return open;
        }

        static string MeshSummary(GameObject selected, Mesh mesh)
        {
            long triangles = 0;
            if (mesh.isReadable) for (var i = 0; i < mesh.subMeshCount; i++) triangles += mesh.GetIndexCount(i) / 3;
            return $"'{selected.name}', {triangles:N0} triangles";
        }

        // ---- 1. what the model is, and what that kind of model gets
        void DrawModelSection(bool multi)
        {
            var index = System.Array.IndexOf(k_Types, m_ModelType);
            if (index < 0) { index = 0; m_ModelType = ModelType.Auto; }
            if (!Section(1, "The Model", k_TypeNames[index], ref m_OpenModel)) return;

            index = GUILayout.Toolbar(index, k_TypeNames, GUILayout.Height(22));
            m_ModelType = k_Types[index];
            EditorGUILayout.LabelField(k_TypeNotes[index], EditorStyles.wordWrappedMiniLabel);
            if (!string.IsNullOrEmpty(m_TypeNote))
                EditorGUILayout.LabelField(m_TypeNote, EditorStyles.wordWrappedMiniLabel);

            if (multi)
            {
                EditorGUILayout.Space(4);
                m_CombineParts = EditorGUILayout.ToggleLeft(
                    new GUIContent("Join the parts into one mesh",
                        "Remesh the parts as one object. Off: each part is remeshed on its own and the result " +
                        "keeps the model's hierarchy -- the quads split over the parts, one shared texture and " +
                        "normal map for the whole model."),
                    m_CombineParts);
                using (new EditorGUI.DisabledScope(m_CombineParts))
                    m_HiddenPartsFewer = EditorGUILayout.ToggleLeft(
                        new GUIContent("Fewer quads on parts that cannot be seen",
                            "Split the quads by how much of each part can be seen from outside, not by area " +
                            "alone: a car's cabin, seats, door insides and underfloor tunnel give quads to its " +
                            "body panels. Glass (transparent materials, or named glass or window) does not " +
                            "hide what is behind it."),
                        m_HiddenPartsFewer);
            }

            EditorGUILayout.Space(4);
            DrawTypeSettings();
        }

        // The selected type's own options, as part of the type: under Auto all three kinds' (each part
        // is read on its own), folded since they are many.
        void DrawTypeSettings()
        {
            var auto = m_ModelType == ModelType.Auto;
            if (auto)
            {
                m_ShowTypeSettings = EditorGUILayout.Foldout(m_ShowTypeSettings, "What each kind gets", true);
                if (!m_ShowTypeSettings) return;
            }
            else EditorGUILayout.LabelField($"What a {k_TypeNames[System.Array.IndexOf(k_Types, m_ModelType)]} model gets",
                EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                m_RoundParts = EditorGUILayout.ToggleLeft(
                    new GUIContent("Rings on turned parts, rows on extruded ones",
                        "Any kind. A piece a rotation sweeps -- a tyre, a rim, a disc, a bottle, a knob -- " +
                        "gets its quads in rings round its axis and rows along it; one an extrusion sweeps, in " +
                        "rows along it. Found by fitting the motion that slides the piece along itself."),
                    m_RoundParts);
                if (auto || m_ModelType == ModelType.Character)
                {
                    if (auto) EditorGUILayout.LabelField("Character", EditorStyles.miniBoldLabel);
                    m_CharacterSourceLoops = EditorGUILayout.ToggleLeft(
                        new GUIContent("Clean quads keep their own loops",
                            "A model that already is clean quads (retopologised, modelled) is reduced by taking " +
                            "out whole edge rings, so its own loops and poles stay, instead of being remeshed."),
                        m_CharacterSourceLoops);
                    m_CharacterStraps = EditorGUILayout.ToggleLeft(
                        new GUIContent("Straps and belts as tubes",
                            "Thin straps, belts and cords get clean tubes of quads round them."),
                        m_CharacterStraps);
                    m_CharacterThinRows = EditorGUILayout.ToggleLeft(
                        new GUIContent("Rows across fingers and toes",
                            "Parts thinner than a few quads (fingers, toes, ears' rims) get quads across them, " +
                            "with the count raised to pay for it, instead of being flattened."),
                        m_CharacterThinRows);
                }
                if (auto || m_ModelType == ModelType.HardSurface)
                {
                    if (auto) EditorGUILayout.LabelField("Hard Surface", EditorStyles.miniBoldLabel);
                    m_HardSourceLoops = EditorGUILayout.ToggleLeft(
                        new GUIContent("Clean quads keep their own loops",
                            "A modelled part that already is clean quads (a car's panels) is reduced by taking out " +
                            "whole edge rings: its lips, flanges and panel edges stay its own. Parts that will " +
                            "not reduce cleanly (a tyre's tread at a few hundred quads) are remeshed."),
                        m_HardSourceLoops);
                    m_HardDenseToNormalMap = EditorGUILayout.ToggleLeft(
                        new GUIContent("Dense edges to the normal map",
                            "Sharp edges packed close (tread, grilles, vents) are left to the normal map; " +
                            "edges standing apart (panel lines) become edge loops."),
                        m_HardDenseToNormalMap);
                    m_HardThinStrips = EditorGUILayout.ToggleLeft(
                        new GUIContent("Two quads across thin strips",
                            "Open strips narrower than a quad (pillar trims, flanges) get two quads across."),
                        m_HardThinStrips);
                    m_HardRods = EditorGUILayout.ToggleLeft(
                        new GUIContent("Quads round thin rods",
                            "Rods thinner than a few quads (pipes, handrails, a mirror's arm, a barrel) get quads " +
                            "round them, with the count raised to pay for it, instead of being dropped and leaving " +
                            "what they hold in the air."),
                        m_HardRods);
                    m_HardEvenRows = EditorGUILayout.ToggleLeft(
                        new GUIContent("Even rows at the count",
                            "The quads are laid once, in even rows, at the count asked: the count is not raised for " +
                            "curved ground and the rows are not merged back down afterwards. Off: more quads on " +
                            "curves, then merged where flat. Faster, but on the cars, the house and the armoured car " +
                            "it leaves more folded and bent faces than the merging, so it is off by default."),
                        m_HardEvenRows);
                    m_HardAcrossRounding = EditorGUILayout.ToggleLeft(
                        new GUIContent("Edges found across their rounding",
                            "An edge rounded over several triangles (a window frame, a door gap, a wheel arch) is " +
                            "measured across its rounding, so the rows follow it. Off: edge by edge, with the dense " +
                            "and the flat panels' tests (the default: sharper plates on the armoured car)."),
                        m_HardAcrossRounding);
                    m_HardFlatPanels = EditorGUILayout.ToggleLeft(
                        new GUIContent("Flatten panels",
                            "The output's faces are faired at the end: where neighbouring faces nearly agree they " +
                            "are made to agree, so ripples in the source smaller than a quad (and the solve mesh's " +
                            "streaks) leave the panels flat instead of crumpled; the normal map carries them. " +
                            "Edges between panels stay sharp. Off: the points stay on the source as laid."),
                        m_HardFlatPanels);
                    using (new EditorGUI.DisabledScope(m_HardEvenRows))
                    m_HardMergeFlat = EditorGUILayout.ToggleLeft(
                        new GUIContent("Big quads on flat plates",
                            "On a dense model of plates, quads are merged where that costs no shape: a flat plate " +
                            "takes a few large quads, the parts keep theirs, and the count drops."),
                        m_HardMergeFlat);
                    using (new EditorGUI.DisabledScope(!m_HardMergeFlat || m_HardEvenRows))
                    {
                        m_HardPlateRows = EditorGUILayout.ToggleLeft(
                            new GUIContent("Plates in rows",
                                "Before the merging, quads on a flat plate are merged nine at a time into rows of " +
                                "big quads that follow the plate's edges, with a ring of steps round them, and kept so."),
                            m_HardPlateRows);
                        m_HardToCount = EditorGUILayout.ToggleLeft(
                            new GUIContent("Merge down to the count (low poly)",
                                "Keep merging until the result has the count asked, even where it costs shape: " +
                                "edges, rods and parts are kept before the round surfaces, which turn faceted."),
                            m_HardToCount);
                    }
                    m_HardShapeRegions = EditorGUILayout.ToggleLeft(
                        new GUIContent("Rings and rows inside parts too",
                            "Inside a piece, regions that are round (turned) or straight (extruded) get rings or " +
                            "rows along their shape."),
                        m_HardShapeRegions);
                }
                if (auto || m_ModelType == ModelType.Scan)
                {
                    if (auto) EditorGUILayout.LabelField("Scan", EditorStyles.miniBoldLabel);
                    m_ScanShapeRegions = EditorGUILayout.ToggleLeft(
                        new GUIContent("Rows along walls and columns",
                            "Walls, columns and other straight or turned regions of the scan get rows or rings " +
                            "along their shape (ccnb at 5000: worst distance 0.0169 -> 0.0135)."),
                        m_ScanShapeRegions);
                }
            }
        }

        // ---- 2. how many quads, and where they go
        void DrawCountSection()
        {
            var summary = $"{m_TargetQuadCount:N0}" + (m_CurvesAddQuads ? ", curves add" : ", exact");
            if (!Section(2, "How Many Quads", summary, ref m_OpenCount)) return;

            m_TargetQuadCount = Mathf.Max(4, EditorGUILayout.IntField(
                new GUIContent("Quads",
                    "How many quads to make. With curves adding theirs, this sizes the quads on flat " +
                    "ground and curved areas add more on top."),
                m_TargetQuadCount));
            var mode = GUILayout.Toolbar(m_CurvesAddQuads ? 1 : 0, new[]
            {
                new GUIContent("Exact count", "The count holds: curved areas take their smaller quads from flat ones."),
                new GUIContent("Curves add quads", "Curved areas get their smaller quads on top of the count, up to " +
                                                   "3x it (5000 asked gives 11,000-14,000 on a character)."),
            });
            m_CurvesAddQuads = mode == 1;
            m_CurveDetail = EditorGUILayout.Slider(
                new GUIContent("Smaller quads on curves",
                    "How much smaller quads get where the surface curves, in percent -- and, when curves add " +
                    "quads, how many they add. 0 is an even grid everywhere; higher spends more on fingers, " +
                    "rims and folds."),
                m_CurveDetail, 0f, 100f);
            EditorGUILayout.LabelField(m_CurvesAddQuads
                    ? "The result can be up to 3x the count."
                    : "The result stays near the count.",
                EditorStyles.wordWrappedMiniLabel);
        }

        // ---- 3. where edge loops are laid
        void DrawLoopsSection()
        {
            var on = new List<string>();
            if (m_LongCreaseLoops) on.Add("creases");
            if (m_MaterialBorders) on.Add("materials");
            if (m_Symmetry != SymmetryAxis.None) on.Add("mirror " + m_Symmetry);
            if (!Section(3, "Edge Loops", on.Count > 0 ? string.Join(", ", on) : "shape only", ref m_OpenLoops)) return;

            m_LongCreaseLoops = EditorGUILayout.ToggleLeft(
                new GUIContent("Along long creases",
                    "Creases that run as long clean lines (grooves, seams) get edge loops along them; short " +
                    "creases from scan noise are ignored. Matters on scans; Hard Surface finds its creases " +
                    "either way."),
                m_LongCreaseLoops);
            m_MaterialBorders = EditorGUILayout.ToggleLeft(
                new GUIContent("On material borders",
                    "The borders between the mesh's materials (submeshes) become edge loops, and the result " +
                    "keeps one submesh per material, with the source's materials on it."),
                m_MaterialBorders);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("Mirrored",
                    "One half is remeshed and mirrored in X, Y or Z, welded along the plane, so both sides " +
                    "get the same edge loops. The plane is the object's local origin when the mesh straddles " +
                    "it, else the middle of its bounds. Clean-quad models are then remeshed rather than " +
                    "reduced by their own loops."), GUILayout.Width(EditorGUIUtility.labelWidth));
                m_Symmetry = (SymmetryAxis)GUILayout.Toolbar((int)m_Symmetry, new[] { "Off", "X", "Y", "Z" });
            }
        }

        string FixAreasSummary(GameObject source)
        {
            var comp = source != null ? source.GetComponent<RemeshPaint>() : null;
            var dabs = comp != null && comp.paint != null ? comp.paint.dabs.Count : 0;
            return m_Painting ? "painting" : dabs > 0 ? $"{dabs:N0} dabs" : "none";
        }

        // ---- 5. what comes out
        void DrawResultSection(GameObject subject)
        {
            var bakes = new List<string>();
            if (m_BakeTexture) bakes.Add("texture");
            if (m_BakeNormalMap) bakes.Add("normal map");
            var summary = (bakes.Count > 0 ? string.Join(" + ", bakes) : "no bakes") + (m_SaveAsAsset ? ", saved" : "");
            if (!Section(5, "Result", summary, ref m_OpenResult)) return;

            EditorGUILayout.LabelField("Bake from the source", EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                m_BakeTexture = EditorGUILayout.ToggleLeft(
                    new GUIContent("Its texture",
                        "Give the result its own UV layout and bake the source's texture into it, with a " +
                        "material using it. The source's own UVs would otherwise be carried over, and on a " +
                        "scan, whose texture atlas is cut into thousands of pieces, about half the quads " +
                        "straddle a cut and show a smeared strip. Reads the texture file at full size."),
                    m_BakeTexture);
                m_BakeNormalMap = EditorGUILayout.ToggleLeft(
                    new GUIContent("Its surface detail, as a normal map",
                        "Bake the source's surface detail into a tangent-space normal map over the result's " +
                        "own UV layout, with a material using it: what the quads are too coarse to carry -- " +
                        "slots, screws, seams, folds, a tire's tread -- comes back as shading, the way game " +
                        "assets are made. Needs no source texture."),
                    m_BakeNormalMap);
                using (new EditorGUI.DisabledScope(!m_BakeTexture && !m_BakeNormalMap))
                {
                    m_TextureSize = EditorGUILayout.IntPopup(
                        new GUIContent("Size",
                            "Width and height of both baked textures. Auto: the source texture's own size, " +
                            "read from its file (the largest, with several), so an 8K scan bakes at 8K; 4096 " +
                            "when there is no source texture."),
                        m_TextureSize, new[] { new GUIContent("Auto"), new GUIContent("1024"), new GUIContent("2048"), new GUIContent("4096"), new GUIContent("8192") },
                        new[] { 0, 1024, 2048, 4096, 8192 });
                    if (m_TextureSize == 0 && subject != null)
                        EditorGUILayout.LabelField(" ", AutoTextureSizeNote(subject), EditorStyles.wordWrappedMiniLabel);
                }
            }
            EditorGUILayout.LabelField("Keep", EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                m_SaveAsAsset = EditorGUILayout.ToggleLeft(
                    new GUIContent("Save to the project (.asset and .fbx)",
                        "Write the result to a .asset file and an .fbx with the quads. Without this the mesh " +
                        "lives only in memory and does not survive a domain reload or a scene save."),
                    m_SaveAsAsset);
                m_QuadTopology = EditorGUILayout.ToggleLeft(
                    new GUIContent("Quads in the Unity mesh",
                        "Keep the quads as quad topology in the Unity mesh. That needs a second submesh for " +
                        "the faces which are not quads, so the object asks for two materials. Off gives one " +
                        "triangle submesh and one material; the .fbx keeps the quads either way."),
                    m_QuadTopology);
            }
        }

        // ---- the run button, its progress, and what the last run said
        void DrawRunControls(Mesh mesh, GameObject selected, bool multi, List<GameObject> parts, GameObject partsRoot,
            string partsError)
        {
            // A run in flight replaces the button with its own progress bar and a cancel control,
            // drawn in this window rather than through EditorUtility.DisplayProgressBar. The modal
            // bar only advances when the caller yields, so during a solve it sat at whatever
            // fraction it was last given and the whole editor appeared hung.
            if (isRunning)
            {
                var elapsed = m_JobWatch != null ? m_JobWatch.Elapsed.TotalSeconds : 0.0;
                var rect = EditorGUILayout.GetControlRect(false, 22f);
                EditorGUI.ProgressBar(rect, Mathf.Clamp01(m_JobProgress),
                    $"{LevelChainStage()}{m_JobStage}  ({elapsed:F1}s)");

                if (GUILayout.Button("Cancel"))
                {
                    m_Cancel?.Cancel();
                    if (m_LevelChain != null) m_LevelChain.cancelled = true;
                }
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
                    var was = GUI.backgroundColor;
                    GUI.backgroundColor = Accent;
                    if (GUILayout.Button(new GUIContent(label,
                            again ? "Runs the source of the selected result again, with the painted fix areas, " +
                                    "and hides the old result (Undo brings it back)." : ""), GUILayout.Height(32)))
                        RunPipeline();
                    GUI.backgroundColor = was;
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
                    "the model to Auto or Scan.",
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
            return $"'{root.name}', {parts.Count} parts, {triangles:N0} triangles";
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
        // ever repaints when the caller yields, which a tight numeric loop never does. A solver on its
        // own thread keeps the editor responsive.
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

        bool isRunning => jobRunning || m_LevelChain != null;

        /// <summary>A worker in flight (a level chain between its runs is not one).</summary>
        bool jobRunning => m_Job != null || m_PartsJob != null || m_LodJob != null;

        // The object the last finished run put in the scene.
        GameObject m_LastMade;

        // Combine Parts: the joined parts' materials, one after another (null: the source's own).
        Material[] m_JobMaterials;

        /// <summary>Starts the pipeline on a worker thread.</summary>
        void RunPipeline()
        {
            if (jobRunning) return;
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
            settings.materialBorders = m_MaterialBorders;
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
            // Each type's own settings (the pipeline gates each by type).
            RemeshPipeline.ribbonParts = m_CharacterStraps;
            RemeshPipeline.thicknessSizing = m_CharacterThinRows;
            RemeshPipeline.quadReductionCharacter = m_CharacterSourceLoops;
            RemeshPipeline.quadReductionHardSurface = m_HardSourceLoops;
            RemeshPipeline.hardSurfaceSparseCreases = m_HardDenseToNormalMap;
            RemeshPipeline.stripSizing = m_HardThinStrips;
            RemeshPipeline.hardSurfaceRods = m_HardRods;
            RemeshPipeline.hardSurfaceSimplify = m_HardMergeFlat;
            RemeshPipeline.hardSurfaceCleanLayout = m_HardEvenRows;
            RemeshPipeline.hardSurfaceRoundedCreases = m_HardAcrossRounding;
            RemeshPipeline.hardSurfaceFair = m_HardFlatPanels;
            RemeshPipeline.hardSurfaceSimplifyToCount = m_HardToCount;
            RemeshPipeline.hardSurfacePlateBlocks = m_HardPlateRows;
            RemeshPipeline.kinematicGuides = m_RoundParts;
            RemeshPipeline.kinematicRegionsHardSurface = m_HardShapeRegions;
            RemeshPipeline.kinematicRegionsScan = m_ScanShapeRegions;
            // The quad settings.
            Extraction.IntegerGridMap.curvesAddQuads = m_CurvesAddQuads;
            Extraction.IntegerGridMap.curveDetail = m_CurveDetail / 100f;
            Extraction.IntegerGridMap.longCreaseLoops = m_LongCreaseLoops;
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
            // The source's own normal maps, baked in with its shape.
            TextureBake.Image[] detailMaps = null;
            int[] detailTriangleMap = null;
            if (bakeNormals && sourceUvs != null)
            {
                if (materials == null) ReadSourceTextures(selected, triangleSubmesh, out detailMaps, out detailTriangleMap, normalMaps: true);
                else
                {
                    var list = new List<TextureBake.Image>();
                    detailTriangleMap = ReadTextures(materials, triangleSubmesh, triangles.Length / 3, list,
                        new Dictionary<Texture, int>(), normalMaps: true);
                    detailMaps = list.Count > 0 ? list.ToArray() : null;
                }
                // One map over the whole mesh: every triangle reads it.
                if (detailMaps != null && detailTriangleMap == null) detailTriangleMap = new int[triangles.Length / 3];
            }
            // Paint lives on a mesh object, in its space: none for a joined model.
            settings.densityPaint = materials == null ? PaintFor(selected) : null;
            // The result this run replaces, when it was the one selected.
            var current = Selection.activeGameObject;
            m_JobReplaces = current != null && current != selected && SourceOf(current) == selected ? current : null;
            m_JobMaterials = materials;
            TextureBake.Layer[] bakeLayers = null;
            if (m_BakeTexture && sourceUvs != null)
            {
                if (materials == null) ReadSourceTextures(selected, triangleSubmesh, out bakeImages, out bakeTriangleTexture);
                else
                {
                    var list = new List<TextureBake.Image>();
                    bakeTriangleTexture = ReadTextures(materials, triangleSubmesh, triangles.Length / 3, list, new Dictionary<Texture, int>());
                    bakeImages = list.Count > 0 ? list.ToArray() : null;
                }
                // The materials' other maps (smoothness, occlusion, ...) over the same layout.
                var layerMaterials = materials ?? selected.GetComponent<Renderer>()?.sharedMaterials;
                if (bakeImages != null && layerMaterials != null)
                    bakeLayers = ReadLayers(layerMaterials, triangleSubmesh, triangles.Length / 3);
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
                        bakeImages, bakeResolution, Progress, bakeLayers);
                if (bakeNormals)
                    RemeshPipeline.BakeNormalMap(solved, positions, triangles, bakeResolution, Progress,
                        detailMaps != null ? sourceUvs : null, detailTriangleMap, detailMaps);
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
                var layers = SaveLayers(result, assetPath, selected.name);
                var occlusion = result.bakedOcclusion != null ? SaveOcclusion(result, assetPath, selected.name) : null;
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
                    AssignLayers(mat, layers);
                    AssignOcclusion(mat, occlusion);
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
            m_LastMade = go;
            if (m_MakeLods) MakeLods(go);

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
            out TextureBake.Image[] images, out int[] triangleTexture, bool normalMaps = false)
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
                var tex = i < materials.Length ? TextureOf(materials[i], normalMaps) : null;
                if (tex == null) continue;
                if (!read.TryGetValue(tex, out var index))
                {
                    var image = ReadTexture(tex, normalMaps);
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
            List<TextureBake.Image> images, Dictionary<Texture, int> read, bool normalMaps = false, string property = null)
        {
            var slotImage = new int[Mathf.Max(1, materials != null ? materials.Length : 0)];
            var any = false;
            for (var i = 0; i < slotImage.Length; i++)
            {
                slotImage[i] = -1;
                var tex = materials != null && i < materials.Length ? TextureOf(materials[i], normalMaps, property) : null;
                if (tex == null) continue;
                if (!read.TryGetValue(tex, out var index))
                {
                    var image = ReadTexture(tex, normalMaps);
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

        // ------------------------------------------------------------------ further maps

        /// <summary>Bake the materials' other maps (smoothness, occlusion, ...) with the texture; false: they are left off.</summary>
        public static bool bakeLayers = true;

        // Slots that are not laid out over the mesh's UVs, or that the bake writes itself.
        static readonly HashSet<string> s_NotLayers = new HashSet<string>
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_BumpMap", "_NormalMap",
            // Tiled over the surface by their own scale, not by the layout.
            "_DetailAlbedoMap", "_DetailNormalMap",
        };

        // Keywords a shader turns on for a map; off with the map, or the shader reads the default
        // white texture (URP's metallic map would make the surface fully metal).
        static readonly Dictionary<string, string[]> s_LayerKeywords = new Dictionary<string, string[]>
        {
            ["_MetallicGlossMap"] = new[] { "_METALLICSPECGLOSSMAP", "_METALLICGLOSSMAP" },
            ["_SpecGlossMap"] = new[] { "_SPECGLOSSMAP", "_METALLICSPECGLOSSMAP" },
            ["_OcclusionMap"] = new[] { "_OCCLUSIONMAP" },
            ["_ParallaxMap"] = new[] { "_PARALLAXMAP" },
            ["_MaskMap"] = new[] { "_MASKMAP" },
            ["_DetailMask"] = new[] { "_DETAIL_MULX2", "_DETAIL_SCALED" },
        };

        /// <summary>
        /// A texture slot of <paramref name="material"/> laid out over the mesh's UVs that the bake
        /// does not already write (not the main texture, the normal map or a tiled detail map).
        /// </summary>
        static bool IsLayer(Material material, string property)
        {
            if (material == null || s_NotLayers.Contains(property) || property.StartsWith("unity_")) return false;
            if (!material.HasProperty(property)) return false;
            var texture = material.GetTexture(property);
            return texture == null || texture.dimension == UnityEngine.Rendering.TextureDimension.Tex2D;
        }

        /// <summary>
        /// The materials' other maps over the source's UVs, each read once, per triangle which it
        /// reads: baked with the main texture, or the result's material would read them through the
        /// new layout -- scattered patches (the fuse box's smoothness map) -- and left off instead.
        /// </summary>
        static TextureBake.Layer[] ReadLayers(Material[] materials, int[] triangleSubmesh, int triangleCount)
        {
            if (!bakeLayers || materials == null) return null;
            var properties = LayerProperties(materials);
            var layers = new List<TextureBake.Layer>();
            foreach (var property in properties)
            {
                var images = new List<TextureBake.Image>();
                var triangleImage = ReadTextures(materials, triangleSubmesh, triangleCount, images,
                    new Dictionary<Texture, int>(), property: property);
                if (triangleImage == null || images.Count == 0) continue;
                layers.Add(NewLayer(materials, property, images.ToArray(), triangleImage));
            }
            return layers.Count > 0 ? layers.ToArray() : null;
        }

        /// <summary>The layer slots of the materials that hold a texture, other than their main one.</summary>
        static List<string> LayerProperties(IEnumerable<Material> materials)
        {
            var properties = new List<string>();
            foreach (var material in materials)
            {
                if (material == null) continue;
                foreach (var property in material.GetTexturePropertyNames())
                {
                    if (properties.Contains(property) || !IsLayer(material, property)) continue;
                    var texture = material.GetTexture(property);
                    // Some shaders keep the main texture in a second slot too.
                    if (texture != null && texture != material.mainTexture) properties.Add(property);
                }
            }
            return properties;
        }

        static TextureBake.Layer NewLayer(IEnumerable<Material> materials, string property, TextureBake.Image[] images, int[] triangleImage)
        {
            var sRGB = false;
            foreach (var material in materials)
            {
                var texture = TextureOf(material, false, property);
                if (texture != null && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer)
                {
                    sRGB = importer.sRGBTexture;
                    break;
                }
            }
            return new TextureBake.Layer
            {
                property = property, images = images, triangleImage = triangleImage, sRGB = sRGB,
                // Where a material has none: no glow, else white (full occlusion, the sliders' own smoothness).
                fallback = property.Contains("Emission") ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255),
            };
        }

        /// <summary>The baked layers as textures, by property: PNGs beside the mesh asset, or in memory.</summary>
        static Dictionary<string, Texture2D> SaveLayers(RemeshResult result, string assetPath, string sourceName)
        {
            var saved = new Dictionary<string, Texture2D>();
            if (result?.bakedLayers == null) return saved;
            var size = result.bakedTextureSize;
            foreach (var layer in result.bakedLayers)
            {
                if (layer?.baked == null || layer.baked.Length != size * size || saved.ContainsKey(layer.property)) continue;
                var suffix = layer.property.TrimStart('_');
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, !layer.sRGB)
                {
                    name = sourceName + "_Quads_" + suffix,
                    wrapMode = TextureWrapMode.Clamp,
                };
                texture.SetPixels32(layer.baked);
                texture.Apply(true);
                saved[layer.property] = texture;
                if (string.IsNullOrEmpty(assetPath)) continue;
                try
                {
                    var pngPath = AssetDatabase.GenerateUniqueAssetPath(Path.ChangeExtension(assetPath, null) + "_" + suffix + ".png");
                    File.WriteAllBytes(pngPath, texture.EncodeToPNG());
                    AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceSynchronousImport);
                    if (AssetImporter.GetAtPath(pngPath) is TextureImporter importer)
                    {
                        importer.sRGBTexture = layer.sRGB;
                        importer.maxTextureSize = Mathf.Max(importer.maxTextureSize, size);
                        importer.wrapMode = TextureWrapMode.Clamp;
                        importer.SaveAndReimport();
                    }
                    var file = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
                    if (file != null)
                    {
                        DestroyImmediate(texture);
                        saved[layer.property] = file;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Native Remesher] Could not save {suffix}: {e.Message}. It is in memory only.");
                }
            }
            return saved;
        }

        /// <summary>
        /// Every layer slot of a baked material: its baked map, or none (with the shader's keyword
        /// for it off) when there is no bake of it -- never the source's, laid out for other UVs.
        /// </summary>
        static void AssignLayers(Material mat, Dictionary<string, Texture2D> layers)
        {
            if (mat == null) return;
            foreach (var property in mat.GetTexturePropertyNames())
            {
                if (!IsLayer(mat, property)) continue;
                var had = mat.GetTexture(property) != null;
                if (had && layers != null && layers.TryGetValue(property, out var baked) && baked != null)
                {
                    mat.SetTexture(property, baked);
                    continue;
                }
                if (!had) continue;
                mat.SetTexture(property, null);
                if (s_LayerKeywords.TryGetValue(property, out var keywords))
                    foreach (var keyword in keywords) mat.DisableKeyword(keyword);
            }
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
        /// <summary>A material's main texture, or with <paramref name="normalMap"/> its normal map, or with <paramref name="property"/> that slot's.</summary>
        static Texture TextureOf(Material material, bool normalMap, string property = null)
        {
            if (material == null) return null;
            if (property != null)
                return material.HasProperty(property) && material.GetTexture(property) is Texture own && own != null ? own : null;
            if (!normalMap) return material.mainTexture;
            foreach (var slot in new[] { "_BumpMap", "_NormalMap" })
                if (material.HasProperty(slot) && material.GetTexture(slot) is Texture t && t != null)
                    return t;
            return null;
        }

        static TextureBake.Image ReadTexture(Texture texture, bool normalMap = false)
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
            // Data (a normal map, smoothness, occlusion) copied as it is: through an sRGB target it
            // would come back brightened.
            var linear = AssetImporter.GetAtPath(path) is TextureImporter dataImporter
                         && (!dataImporter.sRGBTexture || dataImporter.textureType == TextureImporterType.NormalMap);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply(false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var pixels = copy.GetPixels32();
            DestroyImmediate(copy);
            if (normalMap && AssetImporter.GetAtPath(path) is TextureImporter importer
                && importer.textureType == TextureImporterType.NormalMap)
            {
                // Imported as a normal map, the GPU copy is packed (x in alpha times red, y in
                // green, z left out): unpacked to the file's RGB, as UnpackNormal does.
                for (var i = 0; i < pixels.Length; i++)
                {
                    var c = pixels[i];
                    var x = c.a / 255f * (c.r / 255f) * 2f - 1f;
                    var y = c.g / 255f * 2f - 1f;
                    var z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                    pixels[i] = new Color32((byte)Mathf.RoundToInt((x * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((y * 0.5f + 0.5f) * 255f), (byte)Mathf.RoundToInt((z * 0.5f + 0.5f) * 255f), 255);
                }
            }
            return new TextureBake.Image(pixels, w, h);
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
        /// <summary>
        /// The baked cavities as the material's occlusion map, where the shader has one and the
        /// source's own occlusion was not baked in its place (AssignLayers runs first).
        /// </summary>
        static void AssignOcclusion(Material mat, Texture2D occlusion)
        {
            if (mat == null || occlusion == null || !mat.HasProperty("_OcclusionMap") || mat.GetTexture("_OcclusionMap") != null) return;
            mat.SetTexture("_OcclusionMap", occlusion);
            if (mat.HasProperty("_OcclusionStrength")) mat.SetFloat("_OcclusionStrength", 1f);
            mat.EnableKeyword("_OCCLUSIONMAP");
        }

        /// <summary>The baked cavities as a linear grey texture: a PNG beside the mesh asset, or in memory.</summary>
        static Texture2D SaveOcclusion(RemeshResult result, string assetPath, string sourceName)
        {
            var size = result.bakedNormalMapSize;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = sourceName + "_Quads_Occlusion",
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(result.bakedOcclusion);
            texture.Apply(true);
            if (string.IsNullOrEmpty(assetPath)) return texture;
            try
            {
                var pngPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.ChangeExtension(assetPath, null) + "_Occlusion.png");
                File.WriteAllBytes(pngPath, texture.EncodeToPNG());
                AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceSynchronousImport);
                if (AssetImporter.GetAtPath(pngPath) is TextureImporter importer)
                {
                    importer.sRGBTexture = false;
                    importer.maxTextureSize = Mathf.Max(importer.maxTextureSize, size);
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.SaveAndReimport();
                }
                var saved = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
                if (saved != null)
                {
                    DestroyImmediate(texture);
                    Debug.Log($"[Native Remesher] Wrote {pngPath} (cavities)");
                    return saved;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Native Remesher] Could not save the occlusion map: {e.Message}. It is in memory only.");
            }
            return texture;
        }

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
