# Quad Remesher Bridge for Unity

A Unity Editor port of the **RufatRemesher / Quad Remesher 1.3 Blender bridge**
(original author: Maxime Rouca, Exoside).

---

## What this is

The Blender addon is a **bridge**, not a remeshing algorithm. All 1,493 lines of it do this:

1. export the selected mesh to a file,
2. write a settings `.txt` file,
3. launch `xremesh.exe -s settings.txt`,
4. poll `progress.txt` until the engine reports success,
5. import the result.

The actual quad remeshing lives entirely inside Exoside's `xremeshlib.dll`. The addon never
touches it beyond spawning the process.

This Unity package does the same five steps, driving the same engine.

---

## Installation

1. Copy `Assets/QuadRemesher/` into your project.
2. Install the QuadRemesher engine (via the Blender / 3ds Max / Maya bridge installer, which
   downloads it to `%ALLUSERSPROFILE%\Exoside\QuadRemesher\Datas_<HostApp>\QuadRemesherEngine_1.3`).
3. Open **Tools > Quad Remesher**.

The engine folder is auto-detected. If it lives somewhere else, set it explicitly under
**Unity Bridge Settings > Override engine folder**.

---

## Usage

1. Select exactly one GameObject with a `MeshFilter` or `SkinnedMeshRenderer`.
2. Set your options.
3. Press **<< REMESH IT >>** (or `Ctrl+Alt+R`, the same binding the Blender addon uses).

The result is saved as a mesh asset in `Assets/QuadRemesher/Output/` and instantiated in the
scene next to the original, with the source object's transform and materials. The input object is
deactivated if **Hide Input Object** is on.

---

## Port mapping

| Blender addon (`__init__.py`, `qr_operators.py`) | Unity |
| --- | --- |
| `QRSettingsPropertyGroup` | `QuadRemesherSettings.cs` |
| `QREMESHER_PT_qremesher` (side panel) | `QuadRemesherWindow.cs` |
| `doRemeshing_Start` / `doRemeshing_Finish` | `QuadRemesherBridge.cs` |
| modal operator timer (0.3 s) | `EditorApplication.update` |
| `QRCheckProgressData` | `QuadRemesherProgress.cs` |
| `getQREngineFolder` / `getEnginePath` / `getLicenseManagerPath` | `QuadRemesherEngine.cs` |
| `export_selected_mesh_fbx` | `ObjExporter.cs` |
| `import_mesh_fbx` | `AssetDatabase` + `ModelImporter` (see below) |
| `QREMESHER_OT_reset_settings` | "Reset Settings" button |
| `QREMESHER_OT_license_manager` | "License Manager" button |
| `QREMESHER_OT_online_help` | "Online Help" button |
| `Ctrl+Alt+R` keymap | `MenuItem("... /Remesh It %&r")` |

Settings keys, defaults (5000 quads, 50 % adaptive size, hard-edge detection on), engine error
codes, poll interval and the 3 s / 40 s timeouts all match the addon exactly.

---

## Deliberate differences from the Blender addon

### OBJ in, FBX out

Blender ships an FBX exporter; Unity does not. The engine reads OBJ natively, which avoids taking
a dependency on `com.unity.formats.fbx` for the export side.

**But the engine cannot _write_ OBJ — only read it.** Asking for `FileOut="....obj"` makes it
remesh correctly and then fail at the last step with a bare `Error: Remeshing Failed.`, which
looks exactly like a licensing or bad-input failure and is neither. Measured on identical input:

| `FileOut` | Result |
| --- | --- |
| `.obj` | fails — `Error: Remeshing Failed.` |
| `.fbx` | succeeds |
| `.exm` / `.exomesh` | succeeds |

So the bridge sends `.obj` and receives `.fbx`, which Unity imports natively. The `.fbx` is kept
in the output folder, so the real quad topology survives for a round-trip back out to a DCC
(Unity triangulates for rendering, but the asset itself keeps the quads).

**Import scale matters.** The engine writes FBX in centimetres (`fileScale = 0.01`), so Unity's
default import would shrink the result 100× relative to the object it came from. The bridge sets
`useFileScale = false` and `globalScale = 1` on the generated asset.

**Vertex-colour quad density is unavailable.** The engine reads vertex colours only from FBX
*input*, and OBJ cannot carry them. This is the one Blender feature with no equivalent here —
Unity has no vertex-paint tool either, so there would be no way to author the map anyway.

### Vertex welding

Unity render meshes are split at every UV and normal seam, so one position often appears many
times in `mesh.vertices`. Writing those out as distinct OBJ vertices would hand the engine a
disconnected polygon soup and wreck the remesh. `ObjExporter` de-duplicates positions, normals and
UVs independently and references them per face corner via the `v/vt/vn` triple — which is exactly
what that OBJ syntax is for. A Unity cube's 24 vertices correctly collapse to 8 positions.

### Coordinate system

OBJ is right-handed, Unity is left-handed. The exporter negates X and reverses triangle winding;
the importer applies the identical inverse. Round-trip is exact — verified by signed mesh volume,
which would flip sign if handedness or winding were wrong.

### Settings storage

Blender stores settings on the Scene. Here they live in `UserSettings/QuadRemesherSettings.asset`
(per project, not version-controlled), which is the idiomatic Unity home for tool settings.

---

## Tests

**Tools > Quad Remesher > Run Obj Exporter Tests**, or headless:

```bash
Unity.exe -batchmode -nographics -projectPath . -executeMethod Exoside.QuadRemesher.ObjExporterTests.Run
```

Covers the exporter's contract with the engine: seam welding (a Unity cube's 24 vertices must
collapse to 8 positions), face indices in range, welded bounds, winding compensation for the
handedness flip (checked via signed volume, which comes out negative if the winding was not
reversed), and per-material face grouping.

---

## Verification status

Verified end to end against the real engine on Unity 6000.0.58f2:

| Stage | Status |
| --- | --- |
| Compiles, no warnings | ✅ |
| Exporter unit tests | ✅ |
| Engine accepts our `.obj` input | ✅ |
| Engine accepts our settings file | ✅ all keys parsed |
| Progress-file protocol | ✅ |
| Engine completes and writes output | ✅ |
| Result imports at 1:1 scale | ✅ bounds (1.98, 1.99, 1.98) vs (2.00, 2.00, 2.00) in |
| Result is not mirrored | ✅ signed volume +4.06 (a mirror would be negative) |
| Quad count honours the target | ✅ 1355 quads against a 1500 target |
| Normals imported | ✅ |

Full-chain run: Unity's built-in sphere (439 verts / 760 tris) → 382 welded positions → engine →
1357 verts / 1355 quads → back into Unity, correctly scaled and wound.

> **Note on a misdiagnosis worth recording.** An earlier version of this bridge asked the engine
> for `.obj` output and got `Error: Remeshing Failed.` after the engine had run to ~75 %. That was
> initially read as a licensing/activation block — there is no license file on the machine, and
> the engine's binary does contain trial-expiry and activation strings. It was wrong. The engine
> simply cannot write OBJ. Changing one file extension fixed it. If you hit a bare
> "Remeshing Failed" with no detail, check the **output format** before suspecting licensing.

---

## Notes

- The engine derives its preferences and license folder from `Datas_<HostApp>`, so `HostApp` in the
  settings file must name the install being run out of. `QuadRemesherEngine.ResolveHostApp()`
  derives it from the engine folder name automatically.
- Assembly reload is locked during remeshing, so a script recompile cannot orphan the engine
  process mid-run.
- Bone weights and blend shapes are not carried over — retopology replaces the topology they are
  bound to. This matches the Blender addon.
