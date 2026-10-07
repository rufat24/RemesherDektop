# Headless — the measurement project

A second Unity project, inside the first one. It exists for a single reason: **Unity refuses to open
a project another instance already holds.** `HandleProjectAlreadyOpenInAnotherInstance` aborts before
`-executeMethod` ever runs, so every benchmark run used to be a request to close the editor first.
This sidesteps that rather than negotiating with it.

`Assets/NativeRemesher/` here is a **generated copy** of `../Assets/NativeRemesher/`, minus `Editor/`.
Never edit anything under it — the next sync deletes it. It is gitignored for the same reason: two
copies of the same code in one repo, free to drift, with nothing saying which one is real, is worse
than no copy at all.

```bash
bash ../tools/sync-headless.sh
```

That mirrors `Runtime/`, `Benchmark/` and `Tests/`, copies `ProjectVersion.txt`, and writes
`Packages/manifest.json`. `Editor/` is deliberately left out: it is window UI with nothing to
measure headlessly, and leaving it behind keeps this project clear of the GUI assemblies. Everything
mirrored is engine-only apart from the test framework, which is why the manifest is four lines.

## Running things

All from this directory. `UNITY` below is
`/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe`.

```bash
# Tests. 120 at the time of writing: 118 pass, 2 skipped, 0 fail.
"$UNITY" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode \
  -testResults tests.xml -logFile t.log
```

```bash
# The corpus gate. One row per (mesh, target, profile); compares against a committed baseline and
# exits non-zero on a regression past tolerance.
"$UNITY" -batchmode -nographics -projectPath . -quit -logFile bench.log \
  -executeMethod NativeRemesher.Benchmark.BenchmarkRunner.RunFromCommandLine \
  -corpus procedural -targets 500,2000,8000 -out Benchmarks/run.csv \
  -baseline ../Benchmarks/baseline.csv -tolerance 0.02
```

```bash
# Scored against the user's own reference retopology of the same asset.
"$UNITY" -batchmode -nographics -projectPath . -quit -logFile ref.log \
  -executeMethod NativeRemesher.Benchmark.PerfProbe.RunReferenceComparison
```

Other `-executeMethod` targets, all under `NativeRemesher.Benchmark`:

| Method | What it answers |
|---|---|
| `PerfProbe.RunFromCommandLine` | per-stage timing at 4x increasing mesh size, so a super-linear stage shows up as an exponent |
| `PerfProbe.RunDecimateProbe` | pre-decimation timing alone, input size varying, output budget fixed |
| `PerfProbe.RunPipelineProbe` | whole pipeline on the largest real input available, per-stage split |
| `PerfProbe.RunScanDiagnostic` | the open scan: boundary handling, hole filling, what is a real outline and what is a defect |
| `PerfProbe.RunDecimateDiagnostic` | QEM decimation quality at three budgets on the reference asset |
| `PerfProbe.RunCaseDiagnostic` | one corpus case both ways with full extraction diagnostics, and an OBJ each |
| `PerfProbe.RunConvergenceProbe` | field residuals against sweep count |
| `DepthSweep.Run` | hierarchy depth against quality, on the two cases that disagree about it |

**Reading the log:** batch mode writes the report to `-logFile`, not to stdout. `grep` for the
section banner — `=== ReferenceComparison ===`, `=== DepthSweep ===` — and note that a run prints
its report twice, once via `Debug.Log` and once via `Console.WriteLine`.

**Do not wrap a run in a wait-for-string loop.** A previous session lost 76 minutes to
`until grep -q "Benchmark complete" bench.log; do sleep 30; done` — the run had finished and had
never written that string. Let the process exit; it does.

## What a fresh clone is missing

One file: `Benchmarks/reference/ref_high.obj`, 56 MB, the high-poly half of the user-supplied
reference pair. It is derived from `../Assets/ccnb/20260515221044_54b8f8f4.fbx`, which is 84 MB of
scan data and deliberately not in the repo — so neither is the OBJ.

Without it `RunReferenceComparison`, `RunDecimateDiagnostic` and `DepthSweep`'s second half log a
warning and skip. Everything else — the tests, the whole procedural corpus — runs from code.

To rebuild it, convert the FBX pair to OBJ (`ref_high.obj` and `ref_low.obj`) into
`Benchmarks/reference/`. There is no system Python on this machine; `Benchmarks/reference/`
in the parent repo holds `inspect-fbx.py` and `objstat.py`, which run under Blender's embedded
Python:

```bash
"/c/Program Files/Blender Foundation/Blender 4.5/blender.exe" \
  --background --factory-startup --python ../Benchmarks/reference/inspect-fbx.py -- <path.fbx>
```

Installed here: Blender **4.5** and **5.1** (4.2 is gone — this used to name it). Pass
`--factory-startup` so a user add-on cannot change what the importer does. In 4.x the OBJ importer is
`bpy.ops.wm.obj_import`, not the pre-3.x `bpy.ops.import_scene.obj`.

`objstat.py` scores an OBJ's face-size and valence histograms outside Unity, which matters because
Unity's importer defaults to `keepQuads: 0` and triangulates on import — **quad topology cannot be
measured through Unity at all**, so any competitor FBX or reference asset has to be scored this way.
`Benchmarks/quadremesher-measured.md` §1 is the worked example.

## What is committed here

`ProjectSettings/`, `Packages/manifest.json` and this file. That is the scaffolding, so a clone runs
the sync once and has a working measurement project rather than waiting for Unity to invent 20
settings assets on first open.

Not committed: `Assets/` (generated), `Library/`, `Logs/`, `UserSettings/`, `*.log`, `*.xml`, and
`Benchmarks/`. Benchmark output lands here first and is copied into `../Benchmarks/` with the commit
that caused it — keeping both would double every row and leave it ambiguous which was the record.
