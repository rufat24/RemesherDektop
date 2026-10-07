> **Superseded, in part.** This is an early session report and it is kept for the record, not as a
> guide. **Part A**, the Exoside bridge, is still accurate. **Part B**, the native remesher, is not:
> its central conclusion — that the multiresolution hierarchy was the prerequisite for extracting
> quads at all, and would take "weeks, not days" — was wrong in both halves. Quads were extracted
> without it (the defect was three bugs in extraction, since fixed), and the hierarchy itself was
> written and wired in afterwards in a session, where it turned out to matter for a different reason:
> global consistency of the lattice, not extraction.
>
> Its stage-4 evidence is also unreliable. The "residual flat at 0.24" and "mean edge gap 0.547
> cells" figures measure quantities the reference implementation does not measure, and the third,
> "0.6-0.9% collapsed", was a consequence of a Euclidean merge test that no longer exists.
>
> **`Benchmarks/README.md` is the authority.** `README.md` at the repo root is the entry point.

# Quad Remeshing in Unity — Session Report

Unity **6000.0.58f2** · project `C:\Users\user\Quadremesh`

Two separate deliverables came out of this session:

| | What | State |
|---|---|---|
| **A** | **QuadRemesher bridge** — drives the existing Exoside engine from Unity | ✅ **finished and verified** |
| **B** | **Native C# remesher** — Instant Meshes / QuadriFlow, no external engine | 🟡 **partial** — 2 of 5 stages working |

---
---

# Part A — QuadRemesher Bridge

A Unity Editor port of the **RufatRemesher / Quad Remesher 1.3 Blender bridge**
(original author: Maxime Rouca, Exoside).

## A.1 What the Blender addon actually is

Worth stating plainly, because it drove every design decision: **the Blender addon contains no
remeshing algorithm.** All 1,493 lines of `__init__.py` + `qr_operators.py` do five things:

1. export the selected mesh to a file
2. write a settings `.txt`
3. launch `xremesh.exe -s settings.txt`
4. poll `progress.txt` until the engine reports success
5. import the result

The remeshing lives entirely inside Exoside's `xremeshlib.dll`. "Port the addon to Unity"
therefore means "port the bridge", which is what Part A is.

## A.2 Files

```
Assets/QuadRemesher/
├── README.md
└── Editor/
    ├── QuadRemesher.Editor.asmdef
    ├── QuadRemesherWindow.cs      the panel (Tools ▸ Quad Remesher)
    ├── QuadRemesherBridge.cs      start / poll / finish orchestration
    ├── QuadRemesherSettings.cs    settings, stored in UserSettings/
    ├── QuadRemesherEngine.cs      engine + license manager discovery
    ├── QuadRemesherProgress.cs    progress.txt protocol
    ├── ObjExporter.cs             mesh out
    └── ObjExporterTests.cs        unit tests
```

## A.3 Port mapping

| Blender addon | Unity |
| --- | --- |
| `QRSettingsPropertyGroup` | `QuadRemesherSettings.cs` |
| `QREMESHER_PT_qremesher` (side panel) | `QuadRemesherWindow.cs` |
| `doRemeshing_Start` / `doRemeshing_Finish` | `QuadRemesherBridge.cs` |
| modal operator timer (0.3 s) | `EditorApplication.update` |
| `QRCheckProgressData` | `QuadRemesherProgress.cs` |
| `getQREngineFolder` / `getEnginePath` | `QuadRemesherEngine.cs` |
| `export_selected_mesh_fbx` | `ObjExporter.cs` |
| `import_mesh_fbx` | `AssetDatabase` + `ModelImporter` |
| `QREMESHER_OT_reset_settings` | "Reset Settings" button |
| `QREMESHER_OT_license_manager` | "License Manager" button |
| `Ctrl+Alt+R` keymap | `MenuItem("… /Remesh It %&r")` |

Settings keys, defaults (5000 quads, 50% adaptive, hard-edge detect on), error codes, poll
interval and the 3 s / 40 s timeouts all match the addon exactly.

## A.4 Verification — end to end, against the real engine

```
Unity built-in sphere     439 verts / 760 tris
  → ObjExporter           382 welded positions
  → xremesh engine        SUCCESS
  → retopo.fbx            1357 verts / 1355 quads   (target 1500)
  → back into Unity       bounds (1.98, 1.99, 1.98) vs (2.00, 2.00, 2.00)
                          signed volume +4.06  → not mirrored
                          normals intact
E2E: ALL PASSED
```

| Check | Result |
| --- | --- |
| Compiles, no warnings | ✅ |
| Exporter unit tests | ✅ |
| Engine accepts our `.obj` input | ✅ |
| Engine accepts our settings file | ✅ all keys parsed |
| Progress-file protocol | ✅ |
| Engine completes and writes output | ✅ |
| Result imports at 1:1 scale | ✅ |
| Result not mirrored | ✅ |
| Quad count honours target | ✅ 1355 vs 1500 |

---

# Part B — Engine findings (`xremeshlib.dll`)

Determined by binary inspection in order to *interoperate* with the engine.

## B.1 File inventory

11 binaries in the engine folder. Only **one** is Exoside's:

| File | Size | Whose |
|---|---|---|
| `xremeshlib.dll` | 8.28 MB | **Exoside** — algorithm + licensing |
| `xrLicenseManager.exe` | 4.40 MB | Exoside — activation UI |
| `xremesh.exe` | 8 KB | Exoside — launcher stub only |
| `libfbxsdk.dll` | 8.49 MB | Autodesk FBX SDK |
| `Qt5Core/Gui/Widgets/Network/OpenGL.dll` | 19.9 MB | Qt 5.6.2 (LGPL) |
| `platforms/qwindows.dll` | 1.47 MB | Qt platform plugin |
| `ChSolver.dll` | 428 KB | CHOLMOD / SuiteSparse wrapper |

`ChSolver.dll` is confirmed third-party by its imported symbols: `cholmod_start`,
`cholmod_analyze`, `cholmod_factorize`, `cholmod_solve`, `cholmod_allocate_sparse`,
`cholmod_free_factor`.

## B.2 Exported API

The DLL's **entire** public surface:

```
?DoRetopoFromCmdLine@XRemesher@@SAHPEBGHQEAPEAD_N@Z
?DoRetopoFromCmdLine_OLD@XRemesher@@SAHPEBDHQEAPEAD_N@Z
?DoRetopoFromLastCmdLine@XRemesher@@SAHXZ
?extractCmdLineInfo@XRemesher@@SAXPEBGHQEAPEAD@Z
?loadFbxPlugin@XRemesher@@SAXXZ  /  ?unloadFbxPlugin@XRemesher@@SAXXZ
dll_WinMain / dll_WinMainW / CreatePlugIn
```

**There is no mesh-level API.** Every remeshing entry point takes an `argv` command line. There
is no "pass a vertex buffer, get quads back" function, so a Unity *native plugin* against this
build is not possible — driving `xremesh.exe` as a subprocess is the only available integration,
which is exactly what the Blender addon does and what Part A does.

## B.3 Settings file keys

Full key list recovered from the binary (the Blender addon uses only a subset):

```
HostApp  HostAppPath  FileIn  FileOut  ProgressFile  RetopoNodeName
TargetQuadCount  TargetQuadCountAsInputPercentage  TargetEdgeLength
SymAxis  SymLocal  SymTopo  FollowBorders
UseMaterialIds  UsePolygonGroups  UseSmoothingGroups  UseHardEdgeFlags
UseIndexedNormals  AutoDetectHardEdges  AutoDetectHardEdges_Angle
UseFacesSelections  UseEdgesSelections  DenoiseStrength
SmartMergeCADFacesGroups  PreProcess_WeldPoints
PostProcess_SplitPointsOnCreasedNormals
FreezeBorders  FreezeBordersCoef  MaxNumThread
ExactQuadCount  CurvatureAdaptivness  VarDensityRatio
NumPointCirc  MaxQuadRatio  UseVertexColorMap  IsProceduralRemeshing
```

`HostApp` matters: the engine derives its preferences and license folder from
`Datas_<HostApp>`, so it must name the install being run out of.
`QuadRemesherEngine.ResolveHostApp()` derives it from the engine folder name automatically.

## B.4 ⚠ Two gotchas that cost real time

**1. OBJ is input-only.** The engine reads `.obj` but *cannot write it*. Asking for
`FileOut="….obj"` makes it remesh correctly and then fail at the last step with a bare
`Error: Remeshing Failed.` — which looks exactly like a licensing or bad-input failure and is
neither. Measured on identical input:

| `FileOut` | Result |
| --- | --- |
| `.obj` | ❌ `Error: Remeshing Failed.` |
| `.fbx` | ✅ success |
| `.exm` / `.exomesh` | ✅ success |

> This was initially misdiagnosed as a licence block, because the machine has no licence file and
> the binary does contain trial-expiry and activation strings. **That diagnosis was wrong.**
> There is no licence problem. If you hit a bare "Remeshing Failed" with no detail, **check the
> output format before suspecting licensing.**

**2. The engine writes FBX in centimetres** (`fileScale = 0.01`). Unity's default import shrinks
the result 100× against the source object. The bridge sets `useFileScale = false` and
`globalScale = 1` on the generated asset.

## B.5 On the decompiled `1.c` / `2.c`

For the record: the supplied decompiled output contains **no remeshing code** — only reference
counting (`MXCounted`), timing helpers (`MXChrono`), command-line parsing (`extractCmdLineInfo`),
and licence/trial enforcement inside `DoRetopoFromCmdLine`. The algorithm is reached through a
single unnamed call, `FUN_18050db20`, and everything behind it is absent.

---
---

# Part C — Native C# Remesher

Implemented from the published methods:

- Jakob, Tarini, Panozzo, Sorkine-Hornung, **"Instant Field-Aligned Meshes"**, SIGGRAPH Asia 2015
- Huang, Zhou, Nießner, Shewchuk, Guibas, **"QuadriFlow"**, SGP 2018

No external engine, no licence, no `.exe`.

## C.1 Files

```
Assets/NativeRemesher/
├── Runtime/
│   ├── NativeRemesher.Runtime.asmdef
│   ├── RemeshTopology.cs      stage 1 — welded graph, adjacency, normals, areas, scale
│   ├── OrientationField.cs    stage 2 — 4-RoSy cross field
│   ├── PositionField.cs       stage 4 — quad lattice
│   ├── QuadExtractor.cs       stage 5 — lattice → quads
│   └── RemeshPipeline.cs      one-call entry point
└── Editor/
    ├── NativeRemesher.Editor.asmdef
    ├── NativeRemesherWindow.cs   Tools ▸ Native Remesher
    └── FieldTests.cs             Tools ▸ Native Remesher ▸ Run Field Tests
```

## C.2 Stage status

| Stage | State | Evidence |
|---|---|---|
| **1 · Topology** | ✅ **working** | welding, adjacency symmetry, unit normals — all tested |
| **2 · Orientation field** | ✅ **working** | plane → energy **1.000000**; sphere removes **88.4%** of error |
| **3 · Hierarchy** | ⬜ not started | — |
| **4 · Position field** | ❌ **oscillates** | residual flat at 0.24 across 32× iterations |
| **5 · Extraction** | ❌ **too naive** | output is ~3% quads |

## C.3 Evidence — stage 2 works

The decisive test is the **plane**. A correct 4-RoSy field on a flat surface must comb out to
globally parallel, so smoothness energy has to reach 1.0. Any error in the symmetry handling and
it never gets there no matter how long it runs.

```
plane : 121 verts, 1 iteration,  residual 0.000E+00, energy 1.000000
sphere: 382 verts, 300 iters,    residual 1.61E-03,  energy 0.8974 → 0.9881
        → removed 88.4% of achievable error, starting from random noise
```

Welding: sphere 439→382, **cube 24→8** (exact). Adjacency symmetric, no self-loops, directions
unit length and in the tangent plane.

> A random 4-RoSy field already scores ≈0.90, because the energy takes the best of four rotations
> and so is never worse than 45° off. Judge by *fraction of achievable error removed*, not raw gain.

> A sphere can never fully converge — the hairy ball theorem forces singularities, and
> Gauss-Seidel jitters around them permanently. A residual plateau near 1e-3 is expected.

## C.4 Defect 1 — the position field does not converge

Iteration sweep on a 4514-vertex sphere, lattice 2× the input edge length:

| iterations | residual | mean edge gap | collapsed |
|---|---|---|---|
| 25 | 0.238 | 0.5502 | 0.8% |
| 50 | 0.242 | 0.5492 | 0.6% |
| 100 | 0.245 | 0.5479 | 0.7% |
| 200 | 0.242 | 0.5489 | 0.9% |
| 400 | 0.246 | 0.5472 | 0.7% |
| 800 | 0.235 | 0.5469 | 0.7% |

**Completely flat across 32× more work.** This is not slow convergence — it is a limit cycle.

Suspected mechanism: the final `RoundToLattice` pins each `o_i` within half a cell of its own
vertex. Smoothing pulls two neighbours onto a shared lattice point; the rounding then shoves
whichever sits near the half-cell boundary a full cell back; they flip forever.

**Most likely fix:** stage 3. The multiresolution hierarchy is probably not optional — coarse
levels establish global lattice consistency before the fine level can oscillate. Building
stage 3 and re-testing is the highest-information next experiment: it either fixes stage 4
outright or proves the bug is in the update rule.

## C.5 Defect 2 — extraction is too naive

Current approach: union-find over edges by distance, then trace faces of the embedded graph.
Two failure modes observed:

- **lattice ≤2× input edge** — right face count, right bounds, but only 2–5% quads
- **lattice ≥2.6× input edge** — catastrophic collapse; 4753 vertices merge into **1**, because
  union-find chains transitively when every edge falls under the merge threshold

The real Instant Meshes extraction needs several cleanup passes before faces can be traced:
removing valence-1 and valence-2 vertices, resolving non-manifold configurations, fixing
degenerate cells. That is a few hundred lines on its own and is where most reimplementations
of this paper fail.

## C.6 Inherent constraint worth knowing

Field-aligned remeshing samples the lattice **at the input vertices**, so it can only ever
*coarsen*. Requesting quads smaller than the input triangles leaves every vertex on its own
lattice point and hands back the original triangulation. Instant Meshes handles this by
subdividing the input first; that is not implemented here.

## C.7 Expected quality ceiling

Even fully working, this will not match QuadRemesher:

| | Native (this) | QuadRemesher |
|---|---|---|
| Edge flow, organic | good | excellent |
| Pure quads | quad-dominant (QuadriFlow: pure) | near-pure |
| Sharp edges / CAD | weak | strong |
| Singularity placement | scattered (QuadriFlow fixes) | clean |
| Exact quad count | approximate | precise |
| Symmetry | none | explicit X/Y/Z |
| Messy input | fragile | heavily pre-processed |

**Blender's built-in Quad Remesh *is* QuadriFlow.** Run it against QuadRemesher on your own
meshes to preview the realistic ceiling before investing further — it is the same algorithm this
part implements.

---

# Part D — Next steps

1. **Build stage 3 (multiresolution hierarchy).** Graph coarsening by edge collapse, solve
   coarse→fine. Re-test whether the position field settles inside the multigrid.
2. If stage 4 settles → **rewrite stage 5** with the real cleanup passes.
3. Then QuadriFlow's min-cost-flow singularity optimisation for regular, pure-quad output.
4. Only then optimise (Burst / Jobs). The hierarchy is what makes the whole thing
   parallel-friendly — optimising an incorrect field solver is wasted work.

**Effort estimate, revised upward after hitting the real walls: weeks, not days.** The two
remaining stages are the two hardest.

---

# Appendix — commands

Run the tests headless:

```bash
Unity.exe -batchmode -nographics -projectPath . -executeMethod Exoside.QuadRemesher.ObjExporterTests.Run
```

```bash
Unity.exe -batchmode -nographics -projectPath . -executeMethod NativeRemesher.FieldTests.Run
```

In-editor: **Tools ▸ Quad Remesher** and **Tools ▸ Native Remesher**.
