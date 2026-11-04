# RemesherDesktop — Plan

Goal: a standalone **desktop** app built around the **native remesher** from the Unity plugin
`C:\Users\user\UnityProjects\Quadremesh` (the native part only — no Unity runtime dependency).

> **Status / honesty note.** When this plan was written the repo was empty and the Unity project
> (`C:\Users\user\...`) is not reachable from the cloud session (no Remote Control folder connected;
> no Quadremesh repo on GitHub; `rufat24/RemesherWeb` is also empty). Nothing below asserts anything
> about the remesher's algorithm, exported functions or data layout. Phase 0 exists to fix that.

---

## Phase 0 — Intake (blocks everything else)

> **Before pushing anything: this repo is currently PUBLIC.** Switch it to **private** first
> (GitHub → Settings → General → Danger Zone → Change visibility) unless you intend to open-source the
> remesher — pushed code can persist in forks/caches even after deletion. If any native code is
> third-party or purchased, confirm its license allows hosting it on GitHub (private or public).

Push the following into **this repo** (suggested layout) on any branch. Pushing is the reliable route;
connecting the folder via Remote Control (Claude desktop app → Settings → Claude Code → Remote Control)
is an alternative but is not available on every account yet. Commit test meshes as normal files
(a few MB each), **not Git LFS** — LFS content may not reach a cloud session.

| What | Why | Suggested path |
|---|---|---|
| Native C/C++ source + build files (`.vcxproj`, `CMakeLists.txt`, `Makefile`, …). May live outside `Assets/` (e.g. `Native~/`, `Plugins/src/`, a sibling folder). | The actual engine | `native/` |
| C# wrapper file(s) containing `[DllImport(...)]` | Defines the **real ABI** (names, calling convention, struct layout, string/array marshaling) | `reference/unity/wrapper/` |
| Any C# that runs **before/after** the native call: welding, normal/UV handling, index-format (16/32-bit) conversion, coordinate flips, post-processing | Desktop output must match this, not just the DLL | `reference/unity/pipeline/` |
| The editor window / inspector that exposes parameters | Desktop UI should expose the same settings, defaults and ranges | `reference/unity/ui/` |
| 2–3 test meshes exported from Unity (OBJ/PLY), each with **before** and **after** remesh + the parameters used | Golden parity tests | `testdata/` |
| Prebuilt DLL(s) currently shipped in `Assets/**/Plugins` (only if no source exists) | Fallback branch below | `reference/prebuilt/` |
| Licenses of any vendored third-party code in the native source | Distribution audit | alongside the source |

**Phase 0 exit check:** a session can open the native source (or DLL) and wrapper, list every exported
function, and run one remesh on one test mesh.

## The branch decision (made at the end of Phase 0)

| | **A. Source available** (expected) | **B. Prebuilt DLL only** |
|---|---|---|
| Core | Cross-platform CMake library, same or wrapped C ABI | Load the shipped DLL via `LoadLibrary` |
| Platforms | Windows + Linux (+ macOS later) | **Windows only** |
| Precondition | None | DLL must not link Unity runtime / `IUnityInterface` — verify with `dumpbin /dependents` |
| Testability here | Builds and tests in this Linux container | Only on Windows (CI) |

Plan assumes **A**. If B, Phases 1–2 shrink to a thin loader and CI-only testing.

## Architecture (valid on either branch)

```
RemesherDesktop/
  native/        core remesher (from Unity plugin) — untouched except build system
  core/          thin C++ facade over the C ABI: Mesh, RemeshParams, Progress/Cancel
  io/            mesh import/export: OBJ, PLY, glTF/GLB  (FBX deferred: SDK licensing + size)
  cli/           `remesh in.obj out.obj [params...]`  — headless, scriptable, drives golden tests
  app/           GUI (Phase 3)
  testdata/      golden meshes + expected outputs
  .github/       CI: Linux (gcc/clang) + Windows (MSVC)
```

Build order is deliberate: **core → CLI → GUI**. The CLI is buildable/testable in a Linux cloud
container and carries the parity tests; the GUI is just another client of `core`.

### GUI recommendation
**Dear ImGui + GLFW + OpenGL 3.3** — small, one-binary, fast to iterate, good enough for a mesh viewer
(orbit/pan/zoom, solid + wireframe + quad-edge overlay, before/after toggle, parameter panel).
Alternatives if needed: **Qt 6** (native look, heavier, LGPL obligations), or **Tauri/web** if code
should be shared with `RemesherWeb` (would require a WASM or sidecar build of the core).

## Cross-cutting constraints

- **Unity ≠ everyone else.** Unity is left-handed, Y-up, uses 16- or 32-bit indices and its own vertex
  layout. Importers/exporters must convert **explicitly** (axis flip + winding order), and golden tests
  must be generated in a convention that is documented per file.
- **Parity with Unity** means replicating the C# pre/post-processing too (see Phase 0 row 3), not only
  the DLL. Define parity numerically: same face/vertex counts, same topology class (quad ratio), geometry
  within tolerance (Hausdorff/Chamfer) — remeshing may be non-deterministic across platforms/threads.
- **Long-running work.** Run remesh on a worker thread with progress + cancel. Check whether the native
  API already exposes a progress callback / cancel flag; if not, add one in the C ABI facade.
- **Licenses.** Audit all vendored deps before distributing binaries. Some common geometry libraries
  (e.g. CGAL parts, libigl-with-copyleft modules, some quad-remeshing codebases) are GPL/AGPL; that
  would affect how the desktop app can be shipped.
- **Windows is the user's platform, Linux is the dev container.** Add a GitHub Actions **Windows (MSVC)**
  job early; treat it as the source of truth for Windows builds.
- **Mesh I/O scope:** OBJ → PLY → glTF/GLB first. FBX last, only if requested.

## Milestones & exit checks

| # | Milestone | Exit check |
|---|---|---|
| M0 | **Intake** (Phase 0) | Source/DLL + wrapper + test meshes in repo; ABI documented in `docs/abi.md` |
| M1 | **Core builds** | CMake builds the native core + facade on Linux **and** Windows CI, green |
| M2 | **CLI + parity** | `remesh in.obj out.obj` works; golden tests pass within agreed tolerance vs. Unity outputs |
| M3 | **Mesh I/O** | OBJ/PLY/glTF round-trip tests pass incl. axis/winding conversion |
| M4 | **GUI viewer** | Load → set params → remesh (non-blocking, progress, cancel) → view → export, on Windows |
| M5 | **Packaging** | Windows zip/installer from CI artifact; license notices bundled; README |

## What I will not do before Phase 0
No scaffolding against an invented `remesher.h`. Only safe pre-work if wanted: the mesh I/O layer
(M3) and the viewer shell, both independent of the remesher's internals.

## Next step for the user
Push (or connect via Remote Control) the items in the Phase 0 table — **native source + build files,
the `[DllImport]` C# wrapper, the C# pre/post-processing, the editor UI script, and 2–3 before/after
test meshes** — and tell me whether the native part is source or DLL-only. The next session starts at M1.
