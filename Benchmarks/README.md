# NativeRemesher benchmark

## Why this exists

Three defects in this project were misdiagnosed from statistics that were measuring the wrong
quantity, and one of those misdiagnoses nearly bought several weeks of unnecessary work. The rule
here is that no claim about output quality is made without a number attached, and no number is
trusted until the thing producing it has itself been tested.

`Assets/NativeRemesher/Tests/FieldDiagnosticTests.cs` tests the measuring instruments. The
singularity index sum is the useful one: it is a topological invariant, so its correct value is known
from the surface alone (+8 for a closed genus-0 surface, 0 for a torus) and is independent of how
good the field is. That test caught a sign inversion in the detector.

## The investigation logs

Every defect in this project that mattered was invisible to every metric it had, and each was found
by adding one. Each write-up records what was measured, what it refuted, and what the measurement
was blind to — the blind spots are the part worth reading.

| File | What it settles |
|---|---|
| [shading-tail.md](shading-tail.md) | Why the output shaded badly when its deviation was four times better than the competitor's; the relaxation pass; the two defects that pass then caused (rounded creases, an amputated thumb) and the guard for each; whether pre-decimation destroys detail |
| [adaptive-sizing.md](adaptive-sizing.md) | Curvature-graded quad size: the model, the per-side plumbing it needed, the sweep that chose its settings against coverage and smoothness together, and why it is off wherever feature detection is on |
| [extraction-irregularity.md](extraction-irregularity.md) | Why the output looks disordered when every quality number is fine: a third of its vertices are irregular against the competitor's one in twenty-five. The per-stage census that localised it, the split-cell repair, and the pairing tolerance that had been tuned on the wrong column |
| [quadremesher-approach.md](quadremesher-approach.md) | What the competitor actually does, read from its own bridge and from the files its last run left on disk: its whole eight-key parameter set, proof it does not decimate, and which of its settings explain the difference (none of them) |

The standing rules they establish:

1. **Deviation measures where our vertices are and cannot see anything missing.** Coverage — the
   ORIGINAL surface to OUR mesh — is the direction that can, and any change to extraction or
   relaxation has to report it.
2. **A sweep whose columns all agree can still choose the wrong value.** The triangle-pairing
   tolerance was set on four metrics that moved together, and reversed once the one metric that
   disagreed — irregular vertices — was put in the table.
3. **"Stage X is at fault" is a claim to measure, not to reason about.** Turn on
   `ExtractionOptions.recordCensus` and read which stage the number rises at.

## The analysis scripts

Under `reference/`, run with Blender as the interpreter (there is no system Python on this machine):
`blender --background --factory-startup --python <script>.py -- <args>`.

| Script | What it reports |
|---|---|
| `shading.py` | Dihedral distribution, winding, angles and planarity, ours against theirs |
| `defects.py` | Flags every bad face and CLUSTERS them, so "4 % bad" is separated into "spread evenly" and "piled into six places" |
| `triangles.py` | Whether leftover triangles are pairable or structurally stranded |
| `layout.py` | The structural fingerprint: valences, singularity count, how irregular vertices are placed |
| `bumps.py` | Whether the surface reads as lumpy: per-vertex curvature minus its ring's, which cancels real shape and leaves only what varies at the mesh's own sampling frequency. Plus edge spread, aspect and warp - the other half of "distorted" |

## Running it

Interactive: **Tools ▸ Native Remesher ▸ Run Benchmark**. Skips the biggest meshes and uses fewer
surface samples, because the point is a fast answer while iterating.

Headless, and this is the form that gates a commit:

```bash
Unity.exe -batchmode -nographics -projectPath . -executeMethod NativeRemesher.Benchmark.BenchmarkRunner.RunFromCommandLine -corpus procedural -targets 500,2000,8000 -out Benchmarks/run.csv -baseline Benchmarks/baseline.csv -tolerance 0.02
```

Exits non-zero when any quality metric regresses past the tolerance against the baseline. Arguments:

| Flag | Default | Meaning |
|---|---|---|
| `-corpus` | `procedural` | `procedural`, `external`, or `all` |
| `-targets` | `500,2000,8000` | Comma-separated target quad counts |
| `-out` | `Benchmarks/run.csv` | CSV destination; a `.md` sibling is written too |
| `-baseline` | `Benchmarks/baseline.csv` | Comparison reference |
| `-tolerance` | `0.02` | Relative worsening allowed before it counts as a regression |
| `-samples` | `60000` | Surface samples for Chamfer/Hausdorff |
| `-maxTriangles` | `0` (no limit) | Skip meshes above this size |
| `-noOracle` | off | Skip the Exoside reference rows |

Tests: `Unity.exe -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults results.xml`

## Reading the CSV

One row per `(mesh, target, profile, variant)`. Fixed column order, `schemaVersion` first, git SHA and
`corpusVersion` on every row so a number is always attributable to a commit and a corpus.

**Commit the CSV alongside the change that moved it.** That is the entire point — a metric with no
attributable cause is a rumour.

If the column list changes, bump `BenchmarkReport.schemaVersion`. A stale baseline then fails loudly
instead of silently comparing mismatched columns.

Distances (`chamferMean`, `hausdorffMax`, `chamferP95`) are normalised by the input's bounding-box
diagonal, so they compare across meshes of different physical size.

### The columns that actually decide things

- `quadFraction` — the headline. Was ~0.3% before the extraction rewrite.
- `nonManifoldEdges`, `windingOk`, `duplicateFaces`, `degenerateFaces` — validity. Non-negotiable.
- `chamferMean` / `hausdorffMax` — shape fidelity.
- `irregularFrac` — fraction of interior vertices that are not valence 4. Edge-flow quality.
- `discardedFrac`, `latticeMismatch`, `sameCellFrac`, `oneStepFrac` — **the position field's health,
  measured independently of extraction.** See below.
- `indexSum` — topological invariant. Any change is a detector bug, never a quality movement.

### Interpreting the lattice columns

`sameCellFrac`, `oneStepFrac` and `discardedFrac` partition the edge set by what the integer lattice
says about each edge's endpoints: same lattice point (collapse), exactly one step apart (a real quad
edge), or further/diagonal (meaningless).

**The same-cell / one-step split is not a quality signal** — it is a function of how coarse the
requested lattice is. At a coarse target most input edges fall inside one cell, so same-cell
dominates. An 80/18 split with 0.8% discarded is an excellent field. Judge by `discardedFrac` and
`latticeMismatch`.

## Corpus

**Tier 1 — procedural, committed as code**, in `MeshCorpus.Procedural()`. Deterministic, no assets,
always available. Unity's built-in primitives are *not* adequate: `Sphere.fbx` welds to 382 vertices,
so the coarsen-only constraint means it cannot be remeshed to 2000 quads at all. Each entry names the
one thing it stresses, including three deliberately broken meshes (non-manifold, self-intersecting,
degenerate) whose only job is to try to crash the solver.

Bump `MeshCorpus.corpusVersion` on any generator change.

**Tier 2 — external, never committed.** Only `corpus.manifest.json` is. Default location
`<project>/../QuadremeshCorpus`, override with `NATIVEREMESHER_CORPUS`. Skipped with a warning when
absent, so CI stays green. Procedural meshes are clean in ways real content is not — no scanner
noise, no CAD tessellation artefacts, no inconsistent normals — and a remesher tuned only on Tier 1
is tuned for a world that does not exist.

**Tier 3 — oracle rows.** If a hand-baked Exoside result exists at
`Assets/QuadRemesher/Output/<name>*.fbx`, it is scored with the identical metric suite and emitted as
`variant=oracle`. Read-only; the bridge is never driven programmatically. Note FBX round-tripping
usually triangulates, so a low `quadFraction` on an oracle row is a property of the file, not of the
engine — the row says so in `notes`.

## Established findings

Recorded here because they overturn `HANDOVER.md`, which should be read with this file next to it.

**The position field was never broken.** `HANDOVER.md` reports its residual flat at 0.24 across 32×
the iterations and concludes it oscillates, making the multiresolution hierarchy a prerequisite. All
three pieces of evidence were artifacts:

- The reference implementation computes **no position residual at all** and runs a fixed sweep count.
  The quantity being measured had no counterpart to be judged against.
- The final anchoring snap confines each lattice point to a half-cell box around its own vertex, so
  the unknown is the *coset*, not the representative. The residual measured representative
  re-selection — a gauge choice.
- "Mean edge gap 0.547 cells" at a lattice of 2× the input edge is exactly what a *correct* coset
  looks like, and the 0.6–0.9% collapsed figure follows directly from the extractor's Euclidean merge
  test rejecting same-coset neighbours that sit ~0.55 cells apart.

Measured after adding the gauge-invariant metric: `discardedFrac` 0.8–9.8%, `latticeMismatch`
0.046–0.215, `representativeDrift` 1.3e-3–8.8e-3 on every real corpus mesh. The field is healthy.

**Three verified defects in extraction**, each confirmed by diffing against the reference:

1. No lattice filter on output edges. The reference discards any link two or more cells apart *or on
   the (1,1) diagonal*; this code made an output edge from every input edge crossing a cluster
   boundary. Diagonals then over-populate every neighbour ring and the tightest-turn walk closes a
   triangle where it should have closed a quad. Largest single cause of the ~0.3% quad fraction.
2. Face tracing is a single pass that accepts whatever closes first, so it always finds the triangle
   before the quad. The reference runs degree-ordered passes — 4, 3, 5, 6, 7, 8 — accepting a walk
   only if it closes at exactly the target size.
3. A failed walk marks its directed edges consumed before knowing it succeeded, permanently poisoning
   them.

**A fourth defect, found while porting:** `PositionField.Compat` rounded each endpoint to its own
nearest lattice cell independently. The reference floors both and then searches all 4×4 candidate
cell pairs for the joint minimum. Independent rounding optimises each point's distance to the
midpoint rather than the distance between the two points — which is the only quantity that matters,
since the whole purpose is to make neighbouring lattices agree. Fixed. Side effect worth knowing: the
raw position residual dropped from ~0.24 to ~2e-3, because the anchoring snap now has almost nothing
left to fight.

**A fifth defect, and this one is ours rather than a porting slip.** Because we solve a single level
with no hierarchy, a collapse can be refused (for spreading too far) where the reference would never
refuse one. A refused collapse originally produced *nothing* — neither a merge nor an edge — so the
connection between two adjacent surface vertices was silently destroyed. As the requested lattice
gets coarse, nearly every edge classifies as Collapse and almost none as one-step, so this stripped
the graph of most of its edges and `GraphCleanup`'s iterative valence-1 pruning then ate the
remainder: a 5120-triangle icosphere reduced to **3 vertices and 0 faces**. The tell was that
*tightening* the spread guard made the output worse rather than safer. A refused collapse now falls
back to an output edge, which is both correct and what makes the guard tunable at all.

## Phase 2 results

Extraction rewrite, measured on the procedural corpus. 45/45 tests pass.

| | before | after |
|---|---|---|
| quad fraction | ~0.3% | **64–99.5%** |
| non-manifold edges | 0 | **0** |
| winding consistent | yes | **yes** |
| catastrophic collapse | from ~2.6× edge | **none across 1.5–6×** |
| face count vs target | 0.53–1.16 | **0.88–1.30** |
| irregular interior vertices | 90–99% | **0.5–52%** |

Best cases are the ones with structure to follow: `disc_open` 99.5%, `cube_subdivided` 99.1%,
`cylinder_capped` 97.4%, all at ≤5.6% irregular vertices.

Worst cases are smooth closed surfaces at coarse targets — `sphere_holed` at 64.1% with 52%
irregular. That is the expected shape of the remaining gap: a sphere has no features to align to, so
nothing anchors the lattice, and a single global spacing suits a bumpy surface badly. Both point at
the per-vertex sizing field, not at extraction.

## The operating envelope — read this before judging a bad result

**Output quality depends on the ratio of requested lattice spacing to the input's average edge
length, not on the face count.** Measured across the corpus:

| spacing ÷ input edge | quad fraction |
|---|---|
| ~2.0× | 85–98% |
| ~2.4× | 83–96% |
| ~3.3× | **20–49%** |

Past about 2.5× it does not degrade gracefully, it falls off a cliff. The cause is structural: as the
spacing grows, nearly every input edge has both endpoints inside one lattice cell, so almost nothing
is classified as a one-step edge and the output graph ends up carried by refused-collapse fallback
edges, which are not lattice-aligned.

`RemeshTopology.UsefulQuadCountRange` reports the band for a given input, and
`maxUsefulSpacingMultiple` is the constant.

**The consequence for aggressive decimation is the important part.** To reach a very low face count on
a dense mesh, raise the *input's* edge length rather than the requested spacing — decimate first, then
remesh. That keeps the ratio inside the band whatever the final count. A multiresolution hierarchy
would also fix it, by organising the lattice coarse-to-fine instead of asking one level to span many
input edges; that is the one job the hierarchy is genuinely needed for, and it is not the job
`HANDOVER.md` claimed.

Two rows in the current baseline sit outside the band and show it plainly: `cylinder_capped` at 500
(ratio 0.56) and `sphere_holed` at 500 (ratio 0.53, 55% quads). Both are out-of-envelope requests, not
solver defects.

## Phase 3 results

Half-edge topology, cyclic neighbour ordering, boundary and non-manifold flags. 50/50 tests pass.

Ordering neighbours in true fan order rather than ascending changed solver output, mostly for the
better, since Gauss-Seidel reads neighbours already updated in the same sweep:

| mesh | quad fraction | irregular vertices |
|---|---|---|
| icosphere_5k @2000 | 79.1% → **85.4%** | 25.3% → **17.1%** |
| torus @2000 | 88.9% → **91.5%** | 15.5% → **12.7%** |
| sphere_holed @2000 | 83.7% → **91.3%** | 17.1% → **9.8%** |
| cube_subdivided @2000 | 99.1% → 98.2% | 0.9% → 2.4% |
| disc_open | 99.5% → 97.3% | 0.5% → 5.2% |

Also notable: the torus orientation field now settles with **zero** singularities (was 16) at higher
energy. That is legitimate rather than suspicious — a torus is flat-able, so a singularity-free
combing exists, and the index sum is 0 either way.

## Phase 4 results

Pre-decimation, domain profiles, and feature preservation. 75 tests, 73 pass, 2 superseded.

### Pre-decimation is the answer to the envelope

Measured at target 300, without → with:

| mesh | quads | irregular |
|---|---|---|
| torus | 26.4% → **80.8%** | 65.7% → 35.2% |
| cube | 21.7% → **98.8%** | 70.7% → 2.5% |
| sphere_holed | 36.8% → **84.5%** | 72.8% → 20.2% |

The budget constant is 10 triangles per quad, and two independent derivations agree on it — the
coarsen-only ceiling needs `T ≥ 9.24` per quad or the request gets clamped, and `T = 10` puts the
spacing ratio at 2.08×, the middle of the good band. Starting from 4 gave 126 faces for a 300-quad
request.

### Features: restrict positions, not topology

The first attempt forbade merging a crease vertex with an off-crease one. **That was clearly wrong**
and the benchmark said so immediately: on a subdivided cube at 1200 quads it dropped quad fraction
from 98.6% to 65.0% and raised irregular vertices from 1.9% to 29.3%, while surface deviation did not
improve at all. Refusing those merges walls each crease off into a dense line of output vertices the
surrounding lattice cannot resolve against.

Letting the lattice decide topology freely and having features decide only *positions* works:

| cube @1200 | features on | features off |
|---|---|---|
| quads | **98.4%** | 98.6% |
| chamfer | **0.000286** | 0.000498 |
| irregular | 2.3% | 1.9% |

Roughly 40% better shape fidelity with topology intact. Over-sensitive detection turns out to be
self-limiting, too: a threshold low enough to call everything a crease makes almost every vertex a
*junction*, and junctions are deliberately given no tangent, so the pathological case produces almost
no constraints on its own.

### Known limitation: face interiors cut across convex features

Coarse targets on hard-surface meshes deviate badly: cube at ~170 quads has a mean deviation of 0.056
of the bounding-box diagonal, with or without features. Two things it is **not**:

- **Not the decimator.** `CubeDecimationIsNearlyLossless` isolates it on the same mesh and passes at
  chamfer < 0.002.
- **Not off-surface vertex drift.** This was the obvious hypothesis and it is wrong — see below.

#### Negative result: surface reprojection does not help. Do not rebuild it.

The reference pipeline ends with Laplacian smoothing plus reprojection of output vertices onto the
input surface, and its absence looked like the obvious cause. It was implemented and measured, and
then removed:

| cube @1200 chamfer | |
|---|---|
| no projection | 0.0002859248 |
| snap only | 0.0002859242 |
| smooth + snap | 0.000848 |

Snapping is a **no-op** — identical to seven significant figures on most of the corpus — because the
output vertices are already on the input surface. A cluster's position is a weighted average of
lattice points that are each anchored within half a cell of an on-surface input vertex, so the average
lands on the surface without help. Adding the Laplacian smoothing made things 3× worse, because on a
cube it rounds corners and then "nearest surface point" from a rounded position lands on a *face*, not
back on the corner.

So the deviation is not where the vertices are, it is **where the faces are**: a quad spanning a cube
corner has all four vertices exactly on the surface and its interior far from it. Chamfer samples face
interiors, so it sees that.

The fix is therefore edge alignment, not vertex projection — quad *edges* have to run along creases,
where the current feature handling only positions vertices. That means feeding the feature tangents
into the position field so the lattice lines up with the crease, rather than correcting after the
fact. That is the next real piece of work on this axis.

Two knobs were set by measurement rather than taste, and the reasoning is in their doc comments:
`LatticeCollapse.defaultMaxClusterSpread` (2.25 cells — face count scales as 1/spread²) and the
face-count correction loop in `RemeshPipeline` (a damped Newton step on the spacing, keeping the best
attempt rather than the last, because face count is not strictly monotonic in the spacing once the
refused-collapse fallback exists).

---

## Library survey and the algorithm-ceiling problem

Written up 2026-09-02, after two user-reported failures (holes in output; a 1M-triangle
asset stuck in "building topology" for 7+ minutes) prompted the question *"the quad
remesher itself does the better job — maybe look up what you are doing wrong."*

The short answer is that most of it is not a porting mistake. It is the algorithm family.

### What this codebase is, and what its ceiling is

`NativeRemesher` is a port of **Instant Meshes** (Jakob et al. 2015). That method is
*local and greedy*: smooth a 4-RoSy field, smooth a position field, then harvest faces
from whatever lattice the two fields agree on. Nothing in it ever reasons about the mesh
globally.

**QuadRemesher** (Exoside, already in this project) and **quadwild** (Pietroni et al.,
SIGGRAPH 2021) are *global*: feature-aligned cross field → decompose into a coarse layout
of polygonal patches → solve an integer program for the grid resolution of each patch →
emit a regular quad grid per patch. That difference produces three things this codebase
structurally cannot:

| | Instant Meshes family (here) | Global patch-layout family (QuadRemesher, quadwild) |
|---|---|---|
| Face types | quad-*dominant*; tris and n-gons are normal output | **pure quads by construction** |
| Singularities | emergent, wherever field holonomy is non-zero — many | placed at patch corners — few |
| Edge flow | follows curvature locally, wanders globally | follows the patch layout, so loops read as deliberate |
| Feature lines | hoped for, via field constraints | **are patch boundaries**, so reproduced exactly |

The TopGen authors put the same finding bluntly: Instant Meshes "minimizes local
distortion" but "frequently introduces an excessive density of singularities, leading to
chaotic global edge flows." That is exactly the visual gap between this tool's output and
QuadRemesher's, and it is not a bug list — it is the method.

So: the holes were a real defect of mine, sitting *on top of* a ceiling that is genuinely
lower than QuadRemesher's. Both facts need saying.

### Correction to an earlier conclusion in this document

Earlier phases established — correctly, and against the reference source — that
`HANDOVER.md` was wrong that the position field oscillates, and wrong that the
multiresolution hierarchy was the prerequisite for getting quads out at all. Quad
fraction went 0.3% → 80-99% with no hierarchy.

But "not a prerequisite for quads" was over-read into "not important". It is important,
for a reason HANDOVER never gave: **the coarse levels are what make the field globally
coherent.** Smoothing only the finest level is precisely the configuration that maximises
singularity density — and leaves the extractor with loops it cannot close, which is where
the holes came from. The catch-all tracing pass patches the symptom. The hierarchy
addresses the cause.

### Licences — the finding that decides what can be ported

| Library | Licence | Verdict for this project |
|---|---|---|
| **Instant Meshes** (`wjakob/instant-meshes`) | **BSD-3** | Already the basis here. The remaining parts — the hierarchy above all — can be ported freely. |
| **QuadriFlow** (`hjwdzh/QuadriFlow`) | **BSD-3** | **The most valuable target.** See below. |
| meshoptimizer (`zeux/meshoptimizer`) | MIT | Usable. Best-in-class simplifier. |
| Fast-Quadric-Mesh-Simplification | MIT | Usable, lower quality (threshold instead of a sorted queue). |
| UnityMeshSimplifier | MIT | Usable; a C# port of the above lineage. |
| AutoRemesher (`huxingyi/autoremesher`) | MIT | Usable; a working open auto-retopology pipeline worth reading. |
| **quadwild / quadwild-bimdf** | **GPL-3**, and requires **Gurobi** | **Unusable.** GPL-3 rules out shipping; Gurobi is a paid commercial solver. The best free *research* result is the one we cannot ship. |
| libigl | GPL-3 (as GitHub reports it) | Avoid. |

### QuadriFlow is the upgrade path

QuadriFlow (Huang et al. 2018) is by the same lineage as Instant Meshes and exists
specifically to fix its failure modes. It keeps the field-based front end and replaces the
greedy back end with a **global minimum-cost-flow integer solve** over the lattice, which:

- removes degenerate faces and non-manifold configurations *by construction* rather than
  by cleanup passes;
- drastically reduces singularity count, which is the edge-flow complaint;
- is BSD-3, so it can be ported into this codebase with no licence friction.

The paper's own measured result, on a 17,791-object subset of ShapeNet: **about four times
fewer singularities than Instant Meshes**, at **under ten seconds per model** — and it notes
that the methods reaching comparable singularity counts are far slower. Four-times-fewer
singularities is, in the terms of the table above, most of the edge-flow gap. An optional SAT
pass removes flipped faces and guarantees a watertight result — the same defect class as the
holes reported here, solved by construction instead of by a catch-all pass.

It is also the machinery §7 of the plan called "the exact machinery the guidance-layer
design exists to avoid" — worth noting that the conclusion has now flipped: min-cost-flow
is not an ML-era luxury, it is the thing standing between this tool and QuadRemesher-class
edge flow.

### Decimation: keep the local QEM, but fix what it optimises

The strongest empirical result in the free-simplifier literature is that **geometric error
barely predicts what looks right**. In a study of 288 simplification runs over eight
production game assets, judged by ~150 people across ~2,800 pairwise comparisons,
geometric error metrics explained only **10-15%** of visual preference; shading
preservation, silhouette integrity and feature retention dominated. meshoptimizer won on
both preference (57.6%) and speed (24 ms average).

`PreDecimate.cs` is position-only QEM. It is worth keeping — no native plugin, works under
IL2CPP, works on Quest — but the actionable finding is that it optimises the wrong thing.
meshoptimizer folds vertex attributes (normals, UVs) into the quadric; doing the same here
is a contained change with a direct effect on the metric people actually judge.

Also worth recording, because it is a trap this project has already walked into once:
`fast-simplification`'s escalating-threshold approach "hit a wall at 79.5% reduction
instead of the requested 90%, producing structurally destroyed results." A simplifier that
silently fails to reach its target and returns wreckage is exactly the failure mode the
face-count-correction loop in `RemeshPipeline` exists to make visible.

### Sources

- Pietroni et al., *Reliable Feature-Line Driven Quad-Remeshing*, SIGGRAPH 2021 — <http://vcg.isti.cnr.it/publication/2021/PNACT21/>, code <https://github.com/nicopietroni/quadwild>
- <https://github.com/cgg-bern/quadwild-bimdf> (pipeline stages; Gurobi dependency; GPL-3)
- <https://github.com/wjakob/instant-meshes> — BSD-3 (`LICENSE.txt`)
- Huang et al., *QuadriFlow: A Scalable and Robust Method for Quadrangulation*, CGF 2018 — <https://onlinelibrary.wiley.com/doi/abs/10.1111/cgf.13498>, code <https://github.com/hjwdzh/QuadriFlow> — BSD-3 (`LICENSE.txt`)
- <https://github.com/zeux/meshoptimizer>, <https://meshoptimizer.org/> — MIT
- <https://github.com/sp4cerat/Fast-Quadric-Mesh-Simplification>, <https://github.com/Whinarn/UnityMeshSimplifier> — MIT
- <https://github.com/huxingyi/autoremesher> — MIT
- *Testing 4 Free Mesh Simplification Tools* — <https://80.lv/articles/testing-4-free-mesh-simplification-tools>
- *TopGen: Learning Structural Layouts and Cross-Fields for Quadrilateral Mesh Generation* — <https://arxiv.org/pdf/2603.10606> (the singularity-density criticism of Instant Meshes)
- Exoside QuadRemesher user documentation — <https://www.exoside.com/quadremesherdata/QuadRemesher_1.3_UserDoc.pdf>

---

## Source-level comparison against the reference implementations

The survey above is bibliography. This section is what the reference code actually does, read
from the BSD-3 sources it is legal to port from: `wjakob/instant-meshes` and
`hjwdzh/QuadriFlow`. Every claim names the file and the symbol.

### 1. Adjacency: the reference never hashes an edge — and that was the 29 minutes

`instant-meshes/src/dedge.cpp`, `build_dedge`, builds `V2E` (vertex to one outgoing edge) and
`E2E` (half-edge to twin) in **three `tbb::parallel_for` phases** over `tbb::blocked_range` with
`GRAIN_SIZE` blocking:

1. process faces in parallel, inserting each outgoing edge into a **per-vertex linked list**
   using `atomicCompareAndExchange`;
2. walk each vertex's chain to find opposite half-edges and fill `E2E` bidirectionally, flagging
   `nonManifold` where more than one opposite exists;
3. per-vertex, walk backward with `dedge_prev()` to find a boundary edge and point `V2E` at the
   first edge of the fan.

Total cost **O(V + F + E)**, and there is no hash map anywhere in it.

`RemeshTopology.BuildAdjacency` did the same job with a
`Dictionary<long, int>` keyed on `((long)from << 32) | (uint)to`. In .NET,
`long.GetHashCode()` is `(int)v ^ (int)(v >> 32)` — for that packing, literally `from ^ to`.
Vertex indices on a subdivided surface are small and strongly correlated, so distinct edges
collapsed into shared buckets and every lookup walked a chain that grew with the mesh.

Measured by `Benchmark/PerfProbe.cs`, before and after replacing it with the chain scan:

| triangles | vertices | build, hashed | build, scanned |
|---|---|---|---|
| 5,120 | 2,562 | 33 ms | 5 ms |
| 20,480 | 10,242 | 379 ms | 6 ms |
| 81,920 | 40,962 | 8,571 ms | 29 ms |
| 327,680 | 163,842 | not run (projected ~190 s) | 213 ms |
| 1,310,720 | 655,362 | ~50 min, the reported failure | **619 ms** |

The hashed column grows 11.5x then 22.6x for 4x the data. The scanned column grows 2.9x from
327k to 1.31M — linear. Per-stage at 1.31M triangles: `weld=71ms remap=29ms adjacency=396ms
(incident=28ms twins=82ms rings=285ms) normals=87ms edgeLength=32ms`.

For contrast, `OrientationField.Solve` and `SpatialGrid` construction over the same meshes were
always fine: 4 to 16 to 56 ms and 5 to 28 to 44 ms respectively. The defect was isolated to
`Build`, which is why guessing at "single-threaded managed C#" as the cause would have been
wrong.

The fix follows `build_dedge`: build the incident-outgoing-half-edge CSR **first**, then find
each twin by scanning the target vertex's own list — O(valence) per half-edge, no hashing. The
weld's `Dictionary<Vector3Int, int>` got an explicit `CellComparer` for the same class of
reason: Unity's `Vector3Int.GetHashCode` XORs shifted components and discards low bits of z.

**The lesson is the measurement gap, not the hash.** Every benchmark row in this file was capped
at 25k triangles, which is precisely the range where a linear and a quadratic stage are
indistinguishable. `PerfProbe` exists so that cannot recur, and `RemeshTopology.buildTimings`
now reports the per-stage split on every build.

### 2. Iteration counts: this project has the shape of the solve wrong

`instant-meshes/src/field.cpp` sets

```cpp
const int levelIterations = 6;
```

Six Gauss-Seidel sweeps per hierarchy level, fine to coarse, then prolongation back down — a
textbook multigrid V-cycle.

`RemeshProfile` currently asks for **50 to 120 orientation sweeps and 50 to 60 position sweeps,
all at the single finest level.** On a 655k-vertex mesh that is roughly 78M vertex updates
single-threaded, and it is the wrong shape twice over:

- **Cost.** The reference's six sweeps per level are cheap because the coarse levels are tiny.
- **Reach.** Gauss-Seidel propagates information *one edge per sweep*. 120 sweeps moves it 120
  edges; across 655k vertices that is still local. This is why raising the iteration count never
  improved global edge flow and never could. The hierarchy is what carries information across
  the whole surface in a handful of sweeps.

That is the mechanism behind the singularity-density complaint, stated concretely.

### 3. Parallelism: a claim in the plan was wrong, and dropping it unblocks work now

Plan section 7 deferred Burst/Jobs partly on this reasoning: *"Gauss-Seidel is inherently
sequential; the parallel form is coloured Jacobi, which changes the numerics, and the reference
parallelises because it has the hierarchy."*

**Both halves of that are wrong.** Both references parallelise by **graph colouring**, and
colouring does not change the numerics: within one colour class no two vertices are adjacent, so
a vertex never reads a neighbour being updated in the same parallel step. It is Gauss-Seidel
under a particular sweep order, not Jacobi.

`instant-meshes/src/field.cpp`:

```cpp
const std::vector<std::vector<uint32_t>> &phases = mRes.phases(level);
for (const std::vector<uint32_t> &phase_ : phases) {
    tbb::blocked_range<uint32_t> range(0u, (uint32_t)phase_.size(), GRAIN_SIZE);
    phase = &phase_;
    tbb::parallel_for(range, solve_normal);
}
```

`QuadriFlow/src/optimizer.cpp` does the same with OpenMP:

```cpp
for (int phase = 0; phase < phases.size(); ++phase) {
    auto& p = phases[phase];
#pragma omp parallel for
    for (int pi = 0; pi < p.size(); ++pi) { int i = p[pi]; /* update vertex i */ }
}
```

The colouring is built by the hierarchy (`mPhases[i]` in `instant-meshes/src/hierarchy.cpp`),
but **greedy graph colouring of the finest level needs no hierarchy at all.** So both field
sweeps can be parallelised now, independently of everything else. This supersedes plan section
7's ordering.

### 4. The hierarchy, concretely enough to port

`instant-meshes/src/hierarchy.cpp`, `MultiResolutionHierarchy::build`, repeatedly calls
`downsample_graph`:

```cpp
AdjacencyMatrix downsample_graph(const AdjacencyMatrix adj, const MatrixXf &V,
    const MatrixXf &N, const VectorXf &A, MatrixXf &V_p, MatrixXf &N_p, VectorXf &A_p, ...)
```

- Edges are scored `dp * ratio` — normal dot-product similarity times an area-ratio term —
  sorted descending in parallel, then **greedily collapsed if neither endpoint is already
  merged**. A maximal matching, not a full contraction.
- Merged vertices are averaged by area, `(V1*A1 + V2*A2)/(A1+A2)`; normals likewise, then
  normalised.
- Levels are built `for (int i = 0; i < MAX_DEPTH; ++i)`, stopping when a level reaches one
  vertex.
- Each level stores `mV, mN, mA` (geometry), `mQ, mO` (the two field unknowns), `mAdj`,
  `mPhases` (the colouring), and `mToUpper`/`mToLower` (inter-level maps).

That is a contained amount of work — a matching, an area-weighted average, an adjacency rebuild —
and it is the single highest-value thing left. It is not the "1-2 weeks, research risk" that plan
section 7 estimated; that estimate was made before reading the code.

### 5. QuadriFlow's integer solve needs no commercial solver

This was the open question blocking the QuadriFlow path, and the answer is favourable.

`QuadriFlow/src/flow.hpp` declares one interface with four implementations:

```cpp
class MaxFlowHelper {
    virtual void resize(int n, int m) = 0;
    virtual void addEdge(int x, int y, int c, int rc, int v, int cost = 1) = 0;
    virtual int compute() = 0;
    virtual void applyTo(std::vector<Vector2i>& edge_diff) = 0;
};
```

- `BoykovMaxFlowHelper` — Boykov-Kolmogorov via **Boost Graph Library**
- `NetworkSimplexFlowHelper` — **LEMON** preflow followed by network simplex
- `GurobiFlowHelper` — ILP, behind `#ifdef WITH_GUROBI`
- **`ECMaxFlowHelper` — a plain Ford-Fulkerson with BFS augmenting paths and manual flow
  tracking through edge lists. No external dependency whatsoever.**

And `Optimizer::optimize_integer_constraints` picks between them by problem size:

```cpp
if (use_minimum_cost_flow && level == mRes.mToUpperEdges.size())
    solver = std::make_unique<NetworkSimplexFlowHelper>();
else if (supply < 20) solver = std::make_unique<ECMaxFlowHelper>();
else                  solver = std::make_unique<BoykovMaxFlowHelper>();
```

So the dependency-free path is the reference's *own* path for small supply. The flow graph itself
(`QuadriFlow/src/parametrizer-int.cpp`) is small and mechanical: two nodes per face for its two
constraint equations; arcs built where `edge_to_constraints[i][1] == -edge_to_constraints[i][3]`,
meaning two faces meet with opposite signs; `allow_changes` gating which edges may move; and
`total_flows` per component enforcing supply/demand balance. Orientation consistency comes from a
`disajoint_orient_tree`, and `sharpUE` edges are held out of merges so feature boundaries
survive.

**A self-contained C# min-cost flow is therefore a realistic target** — no Boost, no LEMON, no
Gurobi; IL2CPP-safe and Quest-safe. Plan section 7's assumption that min-cost-flow was out of
reach was based on the paper, not on the code.

### What this changes, in priority order

1. **Done:** de-hash the adjacency build (section 1). Quadratic to linear; 29 minutes to 0.62 s
   at 1.31M triangles.
2. **Fix the shape of the solve (section 2):** a hierarchy with ~6 sweeps per level instead of
   50-120 sweeps at one level. Addresses cost *and* the global-coherence/singularity complaint
   together.
3. **Colour the finest level and parallelise both sweeps (section 3).** Independent of the
   hierarchy, available immediately, and the plan's objection to it does not hold.
4. **Port an `ECMaxFlowHelper`-style min-cost flow plus QuadriFlow's integer constraints
   (section 5).** This is what buys manifold-by-construction output and roughly 4x fewer
   singularities — the remaining distance to QuadRemesher.
5. Attribute-aware quadrics in `PreDecimate`, per the 10-15% finding above.

### Additional sources for this section

- `instant-meshes/src/dedge.cpp`, `src/field.cpp`, `src/hierarchy.cpp` — BSD-3
- `QuadriFlow/src/flow.hpp`, `src/optimizer.cpp`, `src/parametrizer-int.cpp` — BSD-3

---

## Holes: a negative result, and the fix that replaced it

User-reported: icosphere and GameAsset output had visible holes. Two attempts, one measurement
each, and the first was wrong in an instructive way.

### How the holes went unreported for so long

`Output_IsManifoldOnClosedInput` checked `nonManifoldEdges`, `duplicateFaces` and winding — and
passed, while the committed baseline had **83 boundary edges and an Euler characteristic of -7 on
a closed sphere**. `MeshMetrics` had been recording `boundaryEdges` and `eulerCharacteristic` the
whole time; the printed summary tables only showed `nonManifoldEdges`. So "validity is clean
everywhere" was reported on the strength of one column out of three.

The assertion order in that test is now deliberate: `boundaryEdges`, then
`eulerCharacteristic`, then `isWatertight`, and only then the non-manifold and winding checks.
Watertightness first, because its absence is what a user actually sees.

### Attempt 1 — a size-free catch-all tracing pass. REJECTED.

The sized tracing passes (4, 3, 5, 6, 7, 8) leave whatever will not close at one of those sizes.
The obvious fix was one more pass with no target size and a generous cap (32), accepting any loop
that closed at all.

Measured on `Icosphere(4)` at 800 quads:

| | boundary edges | Euler characteristic |
|---|---|---|
| before | 83 | -7 |
| with catch-all | **0** | **-14** |

It removed every hole and made the topology **twice as wrong**. A walk over the *graph* is not
confined to the neighbourhood of the hole: a loop that wanders far enough and comes back has
closed a tunnel, not a cap, and each such face adds a handle. Euler -14 on a closed surface is
genus 8 — the pass manufactured roughly eight handles on a sphere.

This is the second time in this project that a fix which improved the obvious metric made the
artefact worse (the first was restricting feature merges). Both were caught only because a
*second* metric was being checked at the same time.

### Attempt 2 — cap the boundary loops instead. `Runtime/Extraction/HoleFiller.cs`

The pass now runs on the **faces already emitted**, not the graph:

1. count directed edge uses across all faces;
2. a directed edge whose reverse is unused is a boundary edge — exactly one face touches it;
3. chain those into loops, keyed reverse-to-forward so a cap winds consistently with the face
   that already owns the edge;
4. emit each simple loop as one face.

Capping a simple boundary cycle with a disc is precisely the operation that removes a hole
without changing genus: `V - E + F` moves by exactly +1 per cap. The pass **cannot** create a
handle, because it never uses an edge that is not already on the boundary of the face set.

Non-simple loops — a vertex visited twice, which happens where two holes meet at a pinch — are
split at the repeat into simple sub-loops. Anything still not simple is left open and counted in
`HoleFillStats.loopsAbandoned`, because a wrong cap is worse than a reported hole. Loops longer
than `maxCapSize = 64` are also refused: past that, the loop is not a hole in a lattice but the
outline of a region the extractor failed on wholesale, and one enormous face would hide that.

Open input is excluded at the call site (`topology.boundaryEdgeCount == 0`), since an open mesh's
own outline is a boundary loop and capping it would weld the mesh shut.

### The standing caveat

This closes holes correctly, but n-gon caps are not good topology — they are damage control. The
holes exist because a single-level field leaves loops the sized passes cannot close, and the
real fix is upstream: the hierarchy (fewer incoherent regions) and QuadriFlow's integer solve
(manifold by construction). `HoleFillStats.loopsCapped` and `largestCap` are the numbers to watch
go to zero when that work lands — they are a defect count, not a feature.

### Edge keys, again

Writing `HoleFiller` immediately reproduced the bug that had just cost 29 minutes: a
`HashSet<long>` of packed `(a, b)` vertex pairs, whose default hash is `a ^ b`.
`FrozenQuadGraph.protectedEdges` is a third instance. All packing and hashing of edge keys now
goes through `Runtime/Extraction/EdgeKey.cs` — `Pack`, `PackUndirected`, and a `Comparer` using
splitmix64's finalising avalanche — so the trap lives in one place with the reasoning attached to
it.

---

## What the holes are a symptom of, and it points at the hierarchy

> **Correction, recorded rather than edited away.** An earlier draft of this section read chi = 8
> as "four separate closed shells" and concluded the extractor was fragmenting the surface. That
> was wrong, and wrong in the same way as every other bad diagnosis in this document: one number
> read without the one beside it. Adding `componentCount` showed **C = 1** — a single connected
> shell — so chi = 8 was never a component count. It was **non-manifold edges**, caused by a bug
> in `HoleFiller`'s own boundary detection: it used a `HashSet` of directed edges and treated
> "reverse absent" as boundary, which misclassifies any edge that two faces traverse the same way
> under inconsistent winding, then caps it — putting a third face on an edge that already had two.
> Fixed by counting undirected incidence instead.
>
> The discarded-edge measurements below are independent of that mistake and stand.

Capping the boundary loops moved the sphere's Euler characteristic from -14 to +8 — the
non-manifold artefact described in the correction above. With that fixed, the remaining
question is why the holes are there at all, and the answer was already in `baseline.csv`,
in a column nobody was reading.


| mesh | target | boundaryEdges | euler | isolatedVerts | **discardedFrac** |
|---|---|---|---|---|---|
| icosphere_5k | 500 | 83 | -7 | 4 | **0.076** |
| icosphere_5k | 2000 | 75 | -10 | 1 | **0.082** |
| torus | 500 | 96 | -7 | 8 | **0.098** |
| cylinder_capped | 500 | 156 | 10 | 7 | **0.102** |
| cube_subdivided | 500 | 176 | **21** | 18 | **0.111** |
| sphere_holed | 2000 | 268 | -16 | 11 | **0.081** |
| robust_selfintersect | 2000 | 102 | -9 | 4 | **0.083** |

`discardedFrac` is the fraction of input edges that Pass A's three-way lattice classification
throws away — the ones whose endpoints are two or more lattice cells apart, or diagonal. It sits
at **8-11% on every mesh in the corpus**, and removing a tenth of a lattice graph's edges is
precisely what leaves the tracer loops it cannot close. `isolatedVerts` between 1 and 18 is the
same damage seen from the other side: vertices whose every lattice edge was thrown away. Euler
21 on a subdivided cube is not an anomaly either — with 176 boundary edges the surface is far
from closed, so chi carries no genus information at all there.

Note that the discard itself is **correct** — it is `compat_position_extrinsic_index_4`'s
classification, ported faithfully, and keeping those edges would produce garbage faces instead of
missing ones. The problem is upstream of it: too many edges *are* multi-cell.

### Why, and why it is the same root cause as everything else

An edge is multi-cell when the position field disagrees about which lattice cell each endpoint
belongs to by two or more steps. That is a *coherence* failure, and the reason the field is
incoherent is the one established in the source-level comparison above: Gauss-Seidel propagates
information one edge per sweep, so a single-level solve — however many iterations it runs — only
ever reaches a local neighbourhood. `instant-meshes/src/field.cpp` runs just `levelIterations = 6`
per level precisely because the *hierarchy* is what carries agreement across the whole surface.

So one missing component explains the whole symptom list:

| symptom | measured | mechanism |
|---|---|---|
| holes in the output | 83-268 boundary edges | loops the sized passes cannot close |
| torn lattice graph | 1-18 isolated verts | ~10% of lattice edges discarded |
| chaotic edge flow | high singularity density | field coherent only locally |
| "50-120 iterations and it never improves" | `RemeshProfile` | Gauss-Seidel reach is one edge per sweep |
| slow solve at scale | 78M vertex updates | all sweeps at the finest level |

The hierarchy is not a global-quality *upgrade*, as plan section 7 has it. It is the missing
prerequisite for the extractor's input being coherent enough to extract from. That is the
correction that matters most in this document.

### What this does not excuse

`HoleFiller` stays, because holes must not ship either way, but its counters are a defect
gauge and not a feature: `loopsCapped`, `largestCap` and `componentCount` should all fall
towards 1-and-zero as the hierarchy lands. Capping is also not geometrically free —
`FeatureDetectionRecoversCubeShapeFidelity` regressed from 0.297 to 0.363 worst-case deviation
because a flat cap cannot follow a cube's corner. That test is left failing rather than relaxed:
it is measuring real damage, and the damage goes away when the holes do.

---

## Three more defects the corrected instruments exposed

Once `componentCount` existed, Euler was computed over referenced vertices only, and the
summary table printed the whole validity group, the same afternoon turned up three further
defects. All three had been present and invisible.

### 1. HoleFiller reproduced the defect its sibling file documents

The first version marked each boundary edge consumed *as the walk stepped onto it*, before
knowing whether the loop would close. A walk that dead-ended therefore poisoned its edges
permanently and no later walk could use them.

That is verbatim "DEFECT 2 — destructive failure" from the header of
`Runtime/Extraction/FaceTracer.cs`, written in this project to explain why the *old* tracer was
broken. The trap was documented and then walked into.

Measured on `SubdividedCube(1f, 24)` at 300 quads, hard-surface profile:

| | loops capped | boundary edges left |
|---|---|---|
| consume-during-walk | 5 | **82** |
| commit-on-success | 7 | **0** |

Walks now collect their edges in a scratch list and commit only on success.
`HoleFillerTests.AFailedWalkDoesNotPoisonEdgesForLaterWalks` guards it, with a fixture that puts
a dead-end boundary path on the *bottom* face — attaching it to the hole's own rim merges the two
boundaries into one broken cycle, so there is no intact loop left to protect and the test proves
nothing. The first version of that fixture made exactly that mistake.

### 2. A cap-size limit changed twice, and the second change was the wrong one

The constant's own documentation says a loop longer than the limit "is not a hole in a lattice —
it is the outline of a region the extractor failed on wholesale, and covering it with one
enormous face hides that rather than fixing it." The limit was 64, which permits precisely that
face, and on the cube the pass emitted a **62-corner cap**. Worst-case deviation on that arm was
0.354 against the other arm's 0.297, so the cap was blamed and the limit tightened to
`2 * FaceTracer.maxFaceSize`.

**That was a misattribution, and measuring both settings showed it:**

| limit | arm | boundary edges | Hausdorff | Chamfer |
|---|---|---|---|---|
| 64 | features on | **0** | 0.354 | 0.042 |
| 64 | features off | 88 | 0.297 | 0.030 |
| 16 | features on | 117 | 0.361 | 0.035 |
| 16 | features off | 116 | 0.357 | 0.035 |

Shrinking the limit left holes open and made geometry **worse on both arms** — features-off
degraded from 0.297 to 0.357. Closing a hole improves surface fidelity even when the cap is an
ugly n-gon, because a missing face deviates more than a flat one does. The 0.354-vs-0.297 gap is
a real difference between the two *fields*, not a cap artefact, and at limit 16 the two arms
converge to within 1% of each other — which is itself the clearest statement that caps were never
the variable.

So the limit is back to 64, and the defect a large cap represents is **reported** rather than
converted into a hole: `HoleFillStats.largestCap` and `loopsCapped` are the numbers to watch fall
as the hierarchy lands.

Worth recording as a process point, because it is the same failure as the rest of this document
one level up: the doc comment was a *plausible argument*, the 0.297-to-0.354 movement was a
*correlation*, and the two together felt like a diagnosis. Running the other setting cost one
test run and reversed the conclusion.

### 3. Extraction emits inconsistently wound faces

The features-off cube arm reports `boundaryEdges=88` with `loopsAbandoned=88` — eighty-eight
failed walks. Boundary edges of a valid 2-manifold-with-boundary always chain into closed cycles,
so a dead end is impossible there. Dead ends arise only where faces disagree about winding, or
where an edge carries more than two faces.

So that arm's extraction is emitting faces whose winding disagrees with their neighbours', and
`HoleFiller` is correctly refusing to cap over a malformed region rather than fabricating
geometry across it. This is a distinct defect from the holes and from the discard rate, it is
recorded here rather than chased, and `loopsAbandoned` is the number that tracks it.

### 4. Feature preservation does not currently earn its complexity

`FeatureTests.FeatureDetectionRecoversCubeShapeFidelity` is **left failing on purpose.**

| | Hausdorff | Chamfer | faces | boundaryEdges | discardedFrac |
|---|---|---|---|---|---|
| features on | **0.354** | **0.042** | 159 | 0 | 0.132 |
| features off | 0.297 | 0.030 | 178 | 88 | 0.140 |

Features-on is worse on worst-case *and* on mean deviation, and it is worse while being the only
arm that is fully closed — so it is not a hole-accounting artefact in its favour either. Chamfer
is a surface-wide average, so this is not a handful of bad caps: the mechanism is hurting shape
fidelity on the one input class it exists to serve.

Note the size of the effect honestly. At the tighter cap-size setting described above, where both
arms carry comparable numbers of holes, the two converge to 0.361 against 0.357 — about 1%. The
0.354-vs-0.297 gap is the sharper reading of the same difference, not a separate finding, and a
re-measure once holes stop dominating is what would settle its true magnitude.

It passed before only because the catch-all tracing pass happened to flatter that arm, so the
earlier section in this document claiming features earn their keep was resting on a pass that has
since been measured as harmful and removed. Treat that claim as withdrawn pending a re-measure.

The test is not being relaxed into a "recorded, not yet at target" form. The Phase 2 acceptance
tests were deliberately written before the work they judge, precisely so the target could not be
negotiated against whatever the implementation happened to achieve, and converting this one now
would be that negotiation. A red test naming a real defect is worth more than a green suite.

The likely cause is already recorded above as a known limitation: feature tangents are fed into
the **orientation** field and never into the **position** field, so quad edges do not follow
creases even when the cross field aligns to them. The constraint-density warning firing at 22% of
vertices on this mesh is the same story from the other side.

---

## The 340-minute problem: where the time actually went

The report was blunt and correct: a 1M-triangle photogrammetry scan sat on "Decimating input"
for three minutes with no end in sight, while Blender decimates the same mesh in seconds and
QuadRemesher remeshes it without even freezing the editor. Two separate defects, one systemic
and one architectural.

### 1. The bad long hash, again, in five more places

`long.GetHashCode()` is `(int)v ^ (int)(v >> 32)`. For the standard edge packing
`(a << 32) | b` that is literally `a ^ b`, and on a subdivided or scanned surface adjacent
indices are numerically close, so `a ^ b` is a small number for nearly every edge. A million
and a half edges land in a few thousand buckets, every insert walks a chain hundreds long, and
the container stays *correct* while going quadratic.

This is the defect that cost a 29-minute topology build in the previous session. It was fixed
there, `Extraction/EdgeKey.cs` was written to document it, and a test was added asserting that
the default comparer collides. It was still live in six other containers, one of them
(`PreDecimate`'s heap seeding) on the hot path of the stage the user was watching.

Measured, icospheres decimated to 20,000 triangles, seed phase only:

| input tris | before | after |
|---|---|---|
| 20,480 | 34 ms | 6 ms |
| 81,920 | 468 ms | 20 ms |
| 327,680 | **12,362 ms** | 131 ms |
| 1,310,720 | did not finish in 9 minutes | 310 ms |

The fix in the decimator is not a better comparer but no hashing at all: every undirected edge
has exactly two half-edges, so taking the one with the lower index enumerates each edge once.
The other five sites now pass `EdgeKey.Comparer`.

`QuadGraph` also carried its own private copy of the packing function, which is why
`EdgeKey.Comparer` could not simply be dropped in: the local `EdgeKey(int, int)` method
shadowed the type. Deleted; both callers now use `EdgeKey.PackUndirected`.

**The lesson, recorded because this project has now paid for it twice:** writing the trap down
in a header and adding a test for one instance does not remove the other instances. A defect
class needs a sweep, not a note.

### 2. Incidence lists that only ever grew

Nothing removed a dead triangle from a vertex's incidence list, so a vertex that survived many
collapses kept every face it had ever inherited. The link-condition and normal-flip checks then
walked a list growing without bound while the live ring stayed at valence six.

`HashSet<int>[]` was also the wrong container: one object per vertex is half a million
allocations on a scan, each with its own bucket and slot arrays, to hold six integers. Replaced
with a flat arena (`PreDecimate.Incidence`) - every ring in one contiguous `int[]`, laid out at
its initial degree plus slack, pruned of dead faces on entry to each collapse.

Collapse phase, same rows:

| input tris | before | after |
|---|---|---|
| 20,480 | 3 ms | 2 ms |
| 81,920 | 368 ms | 226 ms |
| 327,680 | 2,617 ms | 1,699 ms |
| 1,310,720 | did not finish | 9,489 ms |

A third of the remaining collapse cost was redundant work: every edge at the survivor belongs
to two of its faces, so walking the faces queued each candidate exactly twice, paying a quadric
sum, a 3x3 solve and a heap sift each time, for a collapse already queued. Deduplicating
through the one-ring halved the heap traffic (peak 152,831 to 76,793 entries).

**Decimating the real scan, `ccnb.obj` at 990,625 triangles: over 9 minutes to 6.5 seconds.**

### 3. Gauss-Seidel was single-threaded on a wrong premise

Section 7 of the plan states that parallelising Gauss-Seidel means switching to coloured Jacobi,
which "changes the numerics". That is wrong, and it is the reason both field solvers stayed
single-threaded.

Within one colour class no two vertices are adjacent, so a vertex reads only neighbours from
*other* classes and no read can observe a value mid-update. Updating a whole class at once is
identical to updating its members one at a time. What the colouring changes is the order the
sweep visits vertices in, index order to colour order, which is a different Gauss-Seidel sweep,
not a different method. Jacobi reads every neighbour from the *previous* iteration, and that is
not what happens here.

Both references parallelise exactly this way: Instant Meshes runs `tbb::parallel_for` over
`mRes.phases(level)`, QuadriFlow runs `#pragma omp parallel for` over its phase lists.
Determinism survives because the colouring is greedy in index order rather than randomised, and
residuals accumulate into per-vertex arrays summed afterwards in index order rather than into a
shared float.

`Runtime/GraphColouring.cs`, cached lazily on `RemeshTopology.colouring`.

### 4. The face-count loop threw away three full solves out of four

Face count goes as area over spacing squared, so correcting the spacing is a Newton iteration
and needs a few passes. Every pass ran the *full* sweep budget and then all but one was
discarded. On the 990k scan that was 22 seconds of position solve per pass: **90 of the run's 99
seconds went into fields whose only surviving output was an integer.**

A probe does not need a converged field. It needs the count a given spacing produces, and that
is decided by which vertices land on the same lattice cell, settled early, because Gauss-Seidel
covers most of the distance in its first few sweeps and spends the rest refining. Probes now run
at a quarter of the budget (with a floor), pick the spacing, and the field is solved once,
properly, at that spacing.

### The result, on the real scan

`ccnb.obj`, 990,625 triangles, 497,588 welded vertices. Whole pipeline, wall clock:

| profile | target | before | after | decimate | orient | position | extract | faces | quad % |
|---|---|---|---|---|---|---|---|---|---|
| Photogrammetry | 2,000 | 32.0 s | **7.7 s** | 6.5 | 0.10 | 0.48 | 0.06 | 1,198 | 71 |
| Photogrammetry | 8,000 | 99.5 s | **9.1 s** | 6.1 | 0.20 | 0.99 | 0.29 | 6,155 | 77 |
| Game asset | 2,000 | 30.7 s | **8.0 s** | 6.7 | 0.11 | 0.49 | 0.06 | 1,442 | 55 |
| Game asset | 8,000 | 99.9 s | **8.9 s** | 5.9 | 0.16 | 0.96 | 0.26 | 6,603 | 66 |

Quad fraction moved *up* slightly in every row, so none of this was bought with quality. The
test suite is unchanged at 82 passed, 1 deliberately red, 2 skipped.

Decimation is now about 75% of the run. The remaining cost there is memory-bound, 490,000
collapses each chasing 80-byte quadrics through a 40 MB array, and the next lever is spatial
reordering of vertex indices for locality rather than a better algorithm.

### 5. The editor froze because the solver could not leave the main thread

QuadRemesher does not freeze Unity because its solver is native code on its own thread. Nothing
about the algorithm here requires the main thread, but the code reached for the `Mesh` API
*inside* the solve (to build the decimated intermediate, and to read the input), and `Mesh` is
main-thread only. So there was no thread the work could run on but the one drawing the UI, and
`EditorUtility.DisplayProgressBar` only repaints when the caller yields, which a tight numeric
loop never does. The bar sat at whatever fraction it was last handed and the editor appeared
hung.

Split into three:

- `RemeshTopology.ReadMesh` - main thread, milliseconds, the only read of the `Mesh` API.
- `RemeshPipeline.Solve(Vector3[], int[], ...)` - the whole solve on plain arrays, touching no
  Unity object. `PreDecimate.Decimate` gained an array overload for the same reason; it no
  longer creates a `Mesh` for the intermediate at all.
- `RemeshPipeline.BuildOutputMesh` - main thread, milliseconds.

The editor window now runs the middle one on a `Task`, polls it from `EditorApplication.update`,
and draws its own `EditorGUI.ProgressBar` with a working Cancel. Decimation reports progress
from inside its collapse loop rather than once on entry, so the longest stage is the one that
visibly moves, and cancellation is observed at the same point, which is what makes Cancel work
during it.

### Attribution: what the speed actually cost

The corpus rows moved, so they were attributed rather than assumed. Three runs of the same 37
cases, all on the same corpus generator:

- `prev.csv` - commit 2604e4e, this session's changes absent.
- `run.csv` - this session: hash fixes, pruned arena, coloured parallel sweeps, cheap probes.
- `fullprobe.csv` - this session but with probes at the FULL sweep budget, to separate the probe
  budget from everything else.
- `idxorder.csv` - this session but with the sweeps forced back to serial index order, to separate
  the colouring from everything else.

**The probe budget is not the cause.** `fullprobe` reproduces `run` almost exactly: identical quad
fraction on cube (0.9548), cylinder (0.9238), disc (0.9129) and bumpy sphere (0.8233), and no
consistent direction anywhere else. Quadrupling the probe budget bought nothing, so the quarter
budget stays. On `cylinder_capped` at 500 it was actively *worse* (0.8571 against 0.8817), which is
the face-count non-monotonicity showing through and not a signal.

**The colouring is the cause, and the sign depends on the mesh.** Not the uniform loss it first
looked like:

| mesh, target | prev | colour | index order |
|---|---|---|---|
| icosphere_80k, 500 | 0.831 | 0.842 | **0.874** |
| icosphere_80k, 2000 | 0.858 | 0.861 | **0.866** |
| icosphere_320k, 8000 | 0.872 | **0.881** | 0.876 |
| icosphere_5k, 2000 | 0.842 | 0.831 | **0.848** |

**Why order matters at all, given the numerics are identical.** Both sweeps are Gauss-Seidel and
both are exact; what differs is how far information travels in one pass. The topology is built by
welding in input order, so consecutive vertex indices are usually spatial neighbours, and an
index-order sweep therefore rides a wavefront: a value updated at vertex `i` is read by `i+1` in the
*same* pass, so one sweep can carry information a long way. A colour class is by construction a set
with no two vertices adjacent, which is exactly what makes it safe to run in parallel and exactly
what stops information crossing more than one edge per pass.

So the colouring preserves the method and gives up propagation reach. The earlier note in this
document that order is "not a different method" is correct but was incomplete, and the missing half
is what the numbers above are.

**The load-bearing fact underneath both:** the iteration columns read 50/50 in every row of every
run. The early-out never fires. Neither field has converged when extraction reads it — the budget
is the stopping condition — so these figures compare two partially-propagated fields, not two fixed
points. That is why the sign flips with mesh size: on a mesh whose vertices happen to be indexed
coherently the wavefront wins, and on one where 50 sweeps is closer to sufficient the coloured
sweep's better-conditioned visit order wins.

**Which is also the reason the reference can afford this and we cannot yet.** Instant Meshes runs
six sweeps per hierarchy level, not fifty at one level, because coarse levels are what carry
information globally; per-level propagation reach then does not matter and colouring is free. Our
hierarchy (`Runtime/MeshHierarchy.cs`) is written and still unwired. Until it lands, the colouring
is trading about two points of quad fraction, in either direction depending on the mesh, for an
8-10x cut in solve time - and the honest reading is that this is a symptom of solving at one level,
not an argument against parallelism.

### The finding underneath all of it: neither solver has ever converged

Every benchmark row this project has produced reports `50/50` sweeps used out of `50/50` allowed.
The early-outs have never fired. The budget, not convergence, is what stops both solvers -- so
every quality number ever recorded here describes a partially propagated field, and the ordering
comparison above is a comparison of how far each sweep order got rather than of where each lands.

`PerfProbe.RunConvergenceProbe` measures the curve that was missing. Quad fraction, boundary
edges, and both residuals against sweep count, extraction run at each point:

**cube_subdivided, 500 quads**

| sweeps | orient residual | position drift | quad frac | boundary edges | ms |
|---|---|---|---|---|---|
| 12 | 1.60e-4 | 2.03e-2 | 0.8933 | 53 | 50 |
| 50 | 8.02e-5 | 6.90e-3 | 0.9493 | 30 | 189 |
| 100 | 7.76e-5 | 3.26e-3 | 0.9651 | 18 | 376 |
| 200 | 8.71e-5 | 1.59e-3 | 0.9794 | 12 | 738 |
| 400 | 1.10e-4 | **2.31e-4** | **0.9857** | 6 | 1463 |

**torus, 500 quads**

| sweeps | orient residual | position drift | quad frac | boundary edges | ms |
|---|---|---|---|---|---|
| 12 | 6.83e-3 | 3.25e-2 | 0.6911 | 113 | 72 |
| 50 | 5.82e-3 | 1.07e-2 | 0.8224 | 71 | 245 |
| 100 | 1.06e-3 | 6.65e-3 | 0.8996 | 38 | 523 |
| 200 | **6.76e-5** | 5.27e-3 | **0.9213** | **0** | 986 |
| 400 | 5.92e-5 | 3.88e-3 | 0.9065 | 14 | 1992 |

Read the torus row at 50 sweeps: the orientation residual is 5.8e-3, which is **580 times** the
1e-5 the early-out tests against. The field is not nearly solved. Taking it to 200 moves quad
fraction from 0.822 to 0.921 and drives boundary edges to zero -- a watertight result -- for under
a second of extra work.

The cube is monotonic all the way to 400, and its position drift only reaches the 1e-4 early-out
threshold at 400 sweeps. So the threshold was never wrong; the budget was a factor of eight too
small to reach it.

One counter-example, recorded rather than smoothed over: `icosphere_80k` at 2000 peaks at 100
sweeps (0.3035) and then *declines* to 0.2255 by 400. That row is also running at a
spacing-to-edge-length ratio around 4.5, well outside the 2.5 band where this method works at all
(the probe deliberately skips pre-decimation, so it measures the raw mesh). Whether the decline is
a property of the out-of-band regime or a genuine divergence in the position field is not settled
by this data, and it is the reason the sweep budget went to 300 rather than to 400.

**So the ordering question resolves itself.** The choice was never between coloured-parallel
(cheap, about two points worse at 50 sweeps) and index-order (dear, about two points better at 50).
The parallel sweep buys an 8-10x cut in solve time, and the right thing to spend it on is more
sweeps -- which is worth several times what the ordering was worth. Profiles now run 200
orientation / 300 position sweeps instead of 50/50, and the face-count probes are capped at 50
sweeps in absolute terms rather than a fraction of the final budget, so raising the budget does not
multiply the probe cost with it.

### What the sweep budget was worth, and where

Raising the profiles from 50/50 to 200 orientation / 300 position sweeps, measured on the full
procedural corpus against the coloured 50/50 run:

| | quad fraction |
|---|---|
| better at 200/300 | 22 of 30 cases |
| worse | 6 |
| unchanged | 2 (the open disc, which is boundary-dominated) |

The large wins are on structured surfaces: torus +0.069 at every target, capped cylinder +0.048,
sphere-with-hole +0.028 *with* its boundary edges falling from 355 to 190, subdivided cube +0.021
at 2000. Two rows lost: the cube at 500 quads (-0.021, though its boundary edges improved 96 -> 72)
and icosphere_80k at 2000 (-0.007). The open disc's boundary edges got worse, 87 -> 115, which is
the only clear regression and is unexplained.

Even at 200/300 the corpus is still faster than the pre-session code everywhere: icosphere_320k at
8000 quads runs 8.4s against 41.2s, the cube at 2000 runs 1.7s against 3.6s.

**And then it was worth nothing on the actual scan.** `ccnb.obj`, 990,625 triangles:

| profile | target | 50/50 | 200/300 |
|---|---|---|---|
| Photogrammetry | 2,000 | 0.713 in 7.7s | 0.704 in 12.9s |
| Photogrammetry | 8,000 | 0.768 in 9.1s | 0.763 in 18.5s |
| Game asset | 2,000 | 0.553 in 8.0s | 0.571 in 13.1s |
| Game asset | 8,000 | 0.663 in 8.9s | 0.652 in 19.0s |

Flat to within a point in either direction, for double the wall clock. So the budget is now a
per-profile decision made from this table rather than one number applied everywhere:
**Photogrammetry stays at 60/50; Game asset and Hard surface go to 200/300; Character to 300/300.**

The reading: a converged cross field is worth having when there is a coherent surface underneath
for it to be coherent about. On decimated scan data the field is not the binding constraint, so
extra sweeps refine a fit to noise. What *is* the binding constraint there is not identified, and
that is the open question on the photogrammetry path — not the sweep count, and not, on this
evidence, the solver.

---

## Three reported failures on a real scan, and what each turned out to be

The report came with pictures: the source scan, the 50,000-quad output riddled with holes and with
no texture at all, and a character that "was not actually retopologized, it is just voxel mode".
Three complaints, three distinct defects, all confirmed against source before anything changed.

Measured on `ccnb.obj` (990,625 triangles, 561,376 vertices, 561,376 source UVs) at 50,000 quads,
before:

| | value |
|---|---|
| UVs on the output mesh | **0** |
| boundary edges in the output | **23,689** |
| boundary edges the hole filler even counted | **0** |
| mesh topology | `Triangles` x 278,001, one submesh |
| quads discarded at emission | **34,281** |

### 1. No texture: nothing ever wrote a UV channel

`ToMesh` wrote positions, indices and recalculated normals. There was no UV code at all, so no
material could texture the result. On photogrammetry that discards the whole value of the scan: the
quad mesh approximates the shape and everything a viewer recognises is in the texture.

`Runtime/AttributeTransfer.cs` samples them: for each output vertex, the closest point on the input
triangle soup, then that triangle's corner UVs blended by the closest point's barycentric
coordinates. Exact wherever the attribute is linear across a triangle, which a UV map is.

Sampled from the **original** arrays, not the welded topology and not the decimated intermediate.
Welding merges vertices that share a position, and at a UV seam those duplicates carry *different*
UVs, which is what makes them a seam, so sampling post-weld would smear the texture across every
seam in the mesh.

Fixed alongside: the extractor was already computing a per-output-vertex normal, area-averaged over
the input vertices that collapsed into each lattice point, and using it *only* to decide face
winding before discarding it and calling `RecalculateNormals`. Those normals describe the input
surface, so they are smooth where the input was; `RecalculateNormals` sees only the coarse output and
gives every face a flat facet. That faceting is a large part of what read as "voxel".

### 2. The holes: the hole filler refused to run on open input

The gate was `expectClosed`, set from `topology.boundaryEdgeCount == 0`. A photogrammetry scan is an
open surface by definition, a patch of terrain with an outline, 4,419 boundary edges of it here, so
the pass returned immediately having done nothing. That is why it reported counting zero boundary
edges while the output had 23,689.

The test is now geometric: the input's boundary edges go into a uniform grid, and a loop whose
vertices mostly lie within one lattice spacing of them is the outline and is left open; everything
else is a hole. Majority rather than unanimity, because a loop that runs along the outline and then
cuts inland around a hole is genuinely both, and leaving it open is the safe half. On the scan it
identifies 2 to 9 outline loops, the right order for a terrain patch with islands.

### 3. "Not retopologized at all": every quad was destroyed at the last step

`ToMesh` tested `quadCount == faceCount` and fan-triangulated **everything** unless the result was
100 percent quads. No real mesh is. At 78 percent quads, all 34,281 quads went through the triangle
path and the asset that landed in the project was pure triangle soup, so reading the wireframe and
concluding nothing had been retopologised was correct.

That is also exactly the defect the previous version of that method's own comment claimed to have
fixed: it fixed the all-quad case and left the case that occurs. Quads now go to submesh 0 as
`MeshTopology.Quads` and the rest fan-triangulate into submesh 1. The editor window assigns one
material per submesh, because Unity draws submesh i with material i and does not draw a submesh that
has none: a single material would have rendered the quads and silently dropped every triangle, a new
hole introduced by the fix for the old ones.

### The remaining holes were a size limit, and finding that took three wrong guesses

With the gate opened, 6,771 boundary edges remained at 50,000 quads.

**Guess 1, wrong: dead-ended walks.** The chain follows the winding of the face owning each boundary
edge, so one disagreeing face breaks it. An undirected fallback was written to cross that, safe to do
since `ToMesh` decides each face's orientation for itself. It moved the abandoned count by 15 out of
2,455. Deleted.

**Then measured instead of guessed.** Splitting the failure counter by reason gave
`dead=0 long=7031`. Not one walk dead-ends; every failure is a loop longer than a cap may be. The
walk limit and the cap limit were the same constant, so such loops were abandoned by the *walk* and
the pass never learned they existed. The `longest=65` in every report is just that limit plus one.

**Guess 2, wrong: refuse large rims.** Separating the two limits and closing large loops with a
centroid fan took boundary edges from 23,689 to 13. But one rim had 1,823 vertices, and the existing
reasoning around `maxCapSize` says such a thing is not a hole but the outline of a wholesale
extraction failure, which a fan would paper over with a flat disc. Measured both ways, deviation of
the input surface from the output normalised by bounding-box diagonal:

| | boundary edges | quad fraction | dev mean | dev max |
|---|---|---|---|---|
| fan everything | 13 | 0.698 | 0.00014 | 0.01630 |
| refuse rims over 512 | 1,792 | 0.729 | 0.00019 | 0.01630 |

Worst case identical to five decimals, mean *better* with the big fan. The reasoning was wrong: that
rim bounds flat ground, so the lid sits on the surface.

**Guess 3, wrong: make the bound relative.** A 100-vertex rim is trivial on a 45,000-face mesh and a
fifth of a 500-face one, so the limit became a fraction of the face count. That does not separate the
cases either: the rim the scan wants fanned at 8,000 quads is 12.8 percent of its faces and the one
the cube must not have fanned is 14.4 percent. Same ratio, opposite answers.

**What decides it is flatness, not size.** A fan is a flat lid, so the question is whether a flat lid
is near the surface it replaces. Newell's method over the rim gives a stable plane on a non-planar
loop; the rim's departure from that plane, against its radius, measures exactly that. Fan below
`maxRimRelief`, refuse above.

That separated both input classes at once:

| | before | unbounded fan | relief test |
|---|---|---|---|
| cube 500, quad fraction | 0.9212 | 0.6414 | **0.9212** |
| cylinder 500, quad fraction | 0.8791 | 0.6015 | **0.8791** |
| cylinder 500, hausdorff | 0.1535 | 0.2036 | **0.1535** |
| sphere_holed 2000, boundary | 190 | 88 | **88** |
| sphere_holed 2000, hausdorff | 0.0242 | 0.0238 | **0.0223** |
| scan 50,000, boundary | 23,689 | 13 | 820 |
| scan 50,000, dev max | n/a | 0.01630 | **0.01598** |

The cube and cylinder rims span creases and curvature and are correctly refused; the scan's large
rims bound flat ground and are fanned. Twenty-one of thirty corpus rows are untouched.

### Where the scan stands now

| | before | after |
|---|---|---|
| UVs | 0 | 52,047 |
| boundary edges | 23,689 | **820** |
| topology | `Triangles` only | `Quads` x 128,748 + `Triangles` x 104,712 |
| quad fraction | 0.798 | 0.708 |

The quad fraction is the honest cost: caps are n-gons and fans are triangles, both by construction.
Pairing rim edges into quads where a rim has even length would recover most of it and is the obvious
next step on this path.

**One correction to record.** With unbounded fanning,
`FeatureTests.FeatureDetectionRecoversCubeShapeFidelity`, red on purpose since the session that
wrote it, passed, because closing the cube's holes closed the fidelity gap. The relief test correctly
refuses those same rims, so it is red again for exactly the reason it was red before. Nothing about
the cube's output changed across this work; the pass was an artifact of a variant that did not
survive measurement.

## The reference pair, and why the tool was a decimator

`Assets/ccnb` holds a matched pair the user supplied as the target to hit: a 1.5M-triangle input
(`20260515221044_54b8f8f4.fbx`) and a 5,474-face quad retopology of it
(`20260515221800_2ef3dacc.fbx`), from a tool they consider correct. That pair is worth more than the
whole procedural corpus, for three reasons: it fixes the target face count instead of leaving it to
taste, its input is a **closed manifold** with zero boundary edges, so any hole in our output is
ours with nothing inherited from an open scan to hide behind, and the reference mesh's own name,
`mesh_rep_0_ori_repair_quad`, says the producing pipeline repairs to watertight before
quadrangulating, which we do not.

Both meshes are scored outside Unity, by a small script against OBJ written by
`Benchmark/ObjWriter`. This is not fastidiousness. Unity's importer runs with `keepQuads: 0`,
`QuadExtractor.ToMesh` splits n-gons into a second triangle submesh, and FBX export round-trips
through the importer, so a quad fraction read back through any of those is a statement about Unity's
triangulator rather than about the mesh. `ObjWriter` writes faces exactly as the extractor emitted
them, and one script then scores ours and theirs.

### What the first measurement said

| | reference | ours, before |
|---|---|---|
| faces | 5,474 | 5,331 |
| quad fraction | 0.9289 | **0.5067** |
| triangles | 389 | **2,552** |
| boundary edges | 3 | 0 |
| deviation, mean/max of bbox diagonal | n/a | 0.00075 / 0.01072 |

The holes were already gone on this input and geometric fidelity was fine. The failure was purely
topological, and one line of the extractor's own diagnostics said what kind:

```
trace:    faces=7864 (quads=81 tris=7733 ...) walks=36899 failed=29035
regular:  candidates=11346 merged=2598 trisLeft=2552
```

**The quad tracer found 81 quads in 7,864 faces.** Every quad in the output, 2,598 of 2,701, came
from the regulariser gluing pairs of triangles back together afterwards. Mechanically the tool
decimated, triangulated, and paired triangles into quads. The report that it was "definitely
decimation and 0 retopology" was not an approximation; it was a description of the algorithm as it
ran.

## Vector3.normalized, and eleven places it was wrong

`UnityEngine.Vector3.normalized` returns the **zero vector** when the vector's magnitude is at or
below `Vector3.kEpsilon = 1e-5`. That is a sane guard for gameplay directions and a trap for mesh
geometry, where the natural thing to normalise is a cross product of two mesh edges, whose magnitude
is twice a triangle's *area* rather than anything like a unit length.

The reference input is 1,500,000 triangles over a surface area of about 0.85. A triangle covers
5.7e-7 and its cross product has magnitude 1.1e-6: a full order of magnitude under the threshold.
Every face normal on that mesh was zero.

Two independent failures followed, and the isolated decimation diagnostic
(`PerfProbe.RunDecimateDiagnostic`) shows both at once:

```
decimate: 1,500,000 -> 1,500,000 tris, collapses=0
          rejTopo=2909 rejFlip=2247091(solved=589 fallback=2246502)
          targets=593solved/2249407fallback
          drift=224815837.26mean/436764400000.00max edgeLengths rejByDrift=2243585
```

* `2,909 + 2,247,091 = 2,250,000`, exactly the edge count. Every candidate edge on the mesh was
  rejected and **pre-decimation applied zero collapses**, on a mesh Blender coarsens in seconds. The
  guard computed `Dot(before.normalized, after.normalized)`, got `Dot(zero, zero) = 0`, and read
  that as a 90-degree rotation against a `cos(75 degrees)` threshold.
* The solve drifted a **mean of 2.2e8 edge lengths** from the edge it was replacing, so 99.97% of
  targets came from the fallback. That one is a separate defect, below.

The consequence was the whole reported problem. With decimation doing nothing the field solved on
750k vertices at a lattice spacing of `sqrt(1.5M / (5474 * 2.31))` = 10.9 input edge lengths, against
a documented sweet spot of 2.08 and a cliff at 2.5. At 10.9 nearly every edge classifies as
same-cell (measured: 99.4%), so the already-linked check has nothing to block with, clusters run to
721 vertices, and the graph the tracer receives has mean valence 6.75. **A valence-6.75 graph is a
triangle graph, and a tracer walking it can only find triangles.** Nothing downstream was broken.

This also settles a question left open in the previous session: *"on photogrammetry input the field
solver is not what limits quality, more sweeps buy nothing on the scan, and what binds is
unidentified."* Nothing was wrong with the field. The spacing was an order of magnitude out of band
because the stage that exists to prevent exactly that had silently done nothing.

### How well it hid

Worth recording, because the lesson is about the shape of the bug rather than the fix:

* **Every guard in the way passed it.** The code tests `sqrMagnitude <= 1e-20` before normalising,
  and 1.3e-12 clears that by eight orders of magnitude. The vector was not degenerate. It was small.
* **It is scale-dependent, and every corpus mesh is unit-scale.** A 320k-triangle unit icosphere has
  cross magnitudes near 7.9e-5, comfortably above the threshold, so no procedural test could have
  caught it. Catching it needed a real scan at a real size. `MeshCorpus` now records this as the
  reason its own three `.normalized` calls are exempt.
* **It was a class, not an instance.** Eleven sites, swept together into `Runtime/MeshMath.cs`
  (`SafeNormalize`, `SafeFaceNormal`, `CosAngleBetween`, all computing the length in double). Beyond
  the two above: `FeatureGraph` face normals, where a zero normal reads as a 90-degree dihedral and
  invents creases along flat ground; `SpatialGrid` sampled normals, which made the normal-consistency
  metric score a correct mesh as fully inconsistent; `HoleFiller`'s Newell plane, where a zero normal
  makes every rim's relief read as exactly 0 and so passes the flatness test that decides which rims
  may be fanned; `QuadRegularizer`'s candidate normal, where it silently disables triangle-pair
  merging; plus `LatticeCollapse`, `MeshHierarchy`, `QuadExtractor`, `MeshMetrics` and
  `FieldDiagnostics`. Several of those guard with `sqrMagnitude > 1e-20` and *then* call
  `.normalized`, which admits magnitudes down to 1e-10 and still yields zero: the guard and the
  operation disagreed about what counted as degenerate.

`Tests/MeshMathTests.cs` pins the trap, including one test that asserts *Unity's* behaviour, so if
`kEpsilon` ever changes the justification for every call site fails loudly instead of becoming
unexplained defensive noise. `PreDecimateTests.DecimationIsScaleInvariant` is the regression: the
same icosphere at radius 1 and radius 1e-3 must collapse the same number of edges. It is written on
the scale axis because that is the axis the bug lived on and the one every other test was blind to.

### The second defect: a conditioning test that was not one

The 2.2e8 drift is separate, and was hidden behind the first. `Quadric.TryOptimalPoint` guarded the
3x3 solve with `|det| < 1e-10 * magnitude^3`, which is correctly scale-invariant and far too
generous to be a conditioning test. Area-weighted planes here give matrix entries near 5.6e-7, so a
well-conditioned determinant is about 1.8e-19 while the threshold sat at 4.9e-28: nine orders of
magnitude of unsolvable systems reported themselves solved.

`1e-6` bounds the condition number near 1.25e5, about as far as a double solve of this system is
worth trusting when the answer is stored in a `Vector3`. What it refuses now falls through to
`Quadric.OptimalOnSegment`, the exact minimum along the edge, rather than to a best-of-three over
the endpoints and the midpoint. That is strictly an improvement and not a heuristic: those three
points are samples of precisely the function the segment minimum minimises.

## Where the numbers went

Decimation, on the reference input:

| budget | before | after |
|---|---|---|
| 750,000 | 1,500,000 (0 collapses) | **750,000** |
| 200,000 | 1,500,000 (0 collapses) | **200,000** |
| 54,740 | 1,500,000 (0 collapses) | **54,740**, 722,630 collapses, 10.9 s |

Flip rejections fell from 2,247,091 to 5,204.

Extraction, Photogrammetry profile at the reference's 5,474 faces:

| | before | after | reference |
|---|---|---|---|
| faces delivered | 5,331 | 5,207 | 5,474 |
| quad fraction | 0.5067 | **0.7540** | 0.9289 |
| triangles | 2,552 | **582** | 389 |
| quads from tracing | 81 | **3,872** | n/a |
| quads from pairing triangles | 2,598 | **46** | n/a |
| diagonals discarded | 23 (0.0%) | **10,210 (12.4%)** | n/a |
| largest cluster | 721 | **35** | n/a |
| deviation mean | 0.00075 | **0.00049** | n/a |
| runtime | 40.1 s | **15.2 s** | n/a |

Quads now come from quad extraction rather than from gluing triangles afterwards, which is the
difference the word "retopology" refers to.

## A second clamp, 1.8x too tight

The first fixed run delivered 4,130 faces against a request for 5,474, reporting
`ceiling=4,253 clamped=True`. `RemeshTopology.MaxQuadCountForInput` hard-coded a floor of **2x** the
input edge length, while the same file's `minUsefulSpacingMultiple`, used by `UsefulTargetBand` as
its finest end, says **1.5x**, and `maxUsefulSpacingMultiple` says quality holds to 2.5x. Two
different answers to one question, side by side, with the stricter one winning wherever the clamp
ran. The ceiling is 1.78x higher at 1.5x, so the tool had been quietly delivering up to 25% fewer
faces than requested, with the shortfall attributed to nothing.

This was never scan-specific. Across the corpus at target 2,000, face counts went 1,101 to 1,929
(torus), 1,121 to 2,091 (subdivided cube), 535 to 978 (icosphere_5k) and 498 to 1,277 (capped
cylinder), and Hausdorff improved 4.9x on the torus and 7.5x on the cylinder as a result.

It also invalidated a piece of reasoning in `RemeshProfile.decimateBudgetPerQuad`, which claimed two
independent derivations agreed on 10 triangles per quad. They agreed because both were wrong: the
reachability constraint was `T/9.24` only because the ceiling used 2x, and is now `T/5.2`. The
quality envelope alone sets that budget, and the comment now says so.

## What still separates us from the reference, measured

Scored identically, on the same asset, at the same target:

| | reference | ours |
|---|---|---|
| faces | 5,474 | 5,207 |
| quad fraction | 0.9289 | 0.7540 |
| **valence-4 vertices** | **88.7%** | **53.6%** |
| irregular vertices | 11.3% | 46.4% |
| valence defect per vertex | 0.127 | 0.550 |
| valence-2 vertices | **0** | **345** |
| valence-3 vertices | 142 | **1,597** |
| largest face | 4 | **49** |
| non-manifold edges | 0 | 0 |
| boundary edges | 3 | 0 |

The remaining gap is **singularity structure**, not quad dominance. 1,597 valence-3 vertices against
142 is a field whose irregular points are scattered rather than sparse and isolated. 345 valence-2
vertices are outright defects that `GraphCleanup` declined to splice (`v2kept=936` across four
rounds) on a profile whose feature detection is switched off, so there are no genuine corners for
that test to be protecting. And a 49-sided face is a hole `HoleFiller` capped, not a face anyone
designed.

One measurement rules out the cheap explanation. The Character profile runs **300/300** sweeps and
scores 0.7537; Photogrammetry runs **60/50** and scores 0.7540. Identical. At 27,368 vertices the
field is converged, and the previous session's suspicion that propagation reach was the limit was an
artifact of decimation not running. So this is not a sweep-count problem, and not, on this evidence,
a multigrid problem either.

It is what Instant Meshes' extraction is known for. The greedy tracer has no mechanism enforcing
global consistency of the quad structure, so singularities land wherever the smoothed field's
holonomy is non-zero, which on a noisy scan is everywhere. QuadriFlow (2018) exists to fix exactly
this, by adding integer constraints on the parameterisation and solving them with min-cost flow, and
its published comparisons against Instant Meshes show precisely this difference. It is BSD-3 with a
dependency-free max-flow path. **That, not a learned guidance layer, is what closes this gap.** A
guidance model would feed the same greedy extractor and inherit the same incoherence, which is worth
stating plainly because the brief proposing one rests on the premise that the solver already works.

Cheaper items visible in the same numbers, ahead of that: splice the valence-2 vertices on
feature-free profiles; pair rim edges into quads where a rim has even length, still the pending item
from the hole-filling work, which would remove both the leftover triangles and the large n-gon caps;
and build the denoise pre-stage the plan specifies for photogrammetry and which was never
implemented, since a noisy normal field is the direct cause of scattered holonomy.

## Open, and honestly unattributed

`icosphere_80k@500` regressed: same face count, 499 to 501, but Hausdorff 0.0046 to 0.0213, 4.6x
worse. The ceiling change cannot explain it, since 500 was never clamped on an 80k-triangle input, so
it comes from one of the two QEM changes. The tightened conditioning threshold is the likely one,
because it refuses far more 3x3 solves and a well-conditioned solve on a curved neighbourhood is
genuinely better than the segment minimum. Recorded rather than explained; the attribution run is to
revert the threshold alone and re-measure.

`cube_subdivided@500` also shows Hausdorff 0.3509 to 0.4106, but at 165 to 539 faces that is a
different operating point rather than a comparison, and its irregular fraction improved 0.537 to
0.118. Both cube rows remain governed by the deliberately-red
`FeatureTests.FeatureDetectionRecoversCubeShapeFidelity`.

## Measuring while the editor is open

`tools/sync-headless.sh` mirrors `Runtime`, `Benchmark` and `Tests` into a lean project at
`../QuadremeshHeadless`. Unity aborts before `-executeMethod` runs if another instance holds the
project (`HandleProjectAlreadyOpenInAnotherInstance`), which had made every benchmark a request to
close the editor. The mirror needs no packages beyond the test framework, imports in under a minute,
and is one-way and destructive on the destination: never edit code there.

## Localising the remaining gap: it is the lattice, not the field and not the tracer

`Runtime/SingularityAnalysis.cs` is the instrument the plan asked for and that was never built. A
4-RoSy field's singularities are not stored anywhere and are not a choice: walk a triangle's three
edges, accumulate how many 90-degree steps each needed to bring its two crosses into agreement, and
a non-zero total mod 4 means the cross cannot be combed continuously around that face. That is the
reference's `compute_orientation_singularities`, and it needs the integer form of `Compat4`, which
`OrientationField.Compat4` cannot provide because it returns the aligned vectors and discards how
far it turned them.

`CleanupStats` now also reports the valence histogram of the graph the tracer receives. Between them
the two instruments bracket extraction, which the output's valence histogram alone cannot do because
extraction sits between the measurement and the cause.

On the reference asset, Photogrammetry at 5,474 faces:

| stage | measurement |
|---|---|
| field | **429** singular faces of 54,740 (0.78%), plus=207 minus=205 |
| graph handed to tracer | 5,787 verts, mean valence **3.947**, `[3=1529 4=3275 5=785 6=168 7+=30]`, **43.4% irregular** |
| output | 2,423 of 5,691 irregular, **42.6%** |
| reference output | 596 of 5,282 irregular, **11.3%** |

Two conclusions, and both are exclusions:

**The field is not the problem.** 429 singularities is the same order as the reference output's 596,
so the field is asking for roughly the right structure. This kills the expensive theories at once:
not the multiresolution hierarchy for smoothness, not the sweep budget (already known — 300/300 and
60/50 score identically), and not the denoise pre-stage the plan specifies and which was never
implemented, since a field at 0.78% singular faces is not noisy.

**The tracer is not the problem either.** 43.4% irregular in, 42.6% out. It is very slightly
improving the graph it is given. Every one of the ~2,000 excess irregular vertices is created in
`LatticeCollapse`.

The histogram says what kind of failure that is. Mean valence 3.947 is nearly perfect while the
spread is wide, 1,529 valence-3 against 785 valence-5 — cancelling pairs, which is the signature of
connectivity that is locally sensible and globally inconsistent. A cluster's neighbour in a given
lattice direction is sometimes two distinct clusters and sometimes none. **Converged is not the same
as consistent**, and a single-level position field delivers only the former: it finds a local minimum
of a local energy, and nothing in that objective requires the cells to tile.

Which is exactly the gap QuadriFlow closes, by constraining the parameterisation to integers and
solving the constraints with min-cost flow. Restated more precisely than before: the missing piece is
not global smoothness of the cross field, which we have, but global consistency of the integer
lattice, which no amount of smoothing produces.

Secondary, from the same run: `holeFill: boundaryEdges=1226 capped=124 largestCap=44` with
`unusedSlots=2136`. The tracer leaves 1,226 graph edges uncovered and the filler caps those regions
into faces of up to 44 sides — every n-gon above 8 corners in the output comes from there, since
`FaceTracer.maxFaceSize` is 8. That was invisible until `QuadExtractionResult.Diagnostics()` started
including the hole-fill line.

## Valence-2 splicing: a real trade, not a win

`GraphCleanup`'s corner test keeps a valence-2 vertex that sits more than 0.25 lattice cells off the
line through its two neighbours, on the grounds that it is a corner whose position carries the shape.
The reference retopology has zero valence-2 vertices and ours had 345, and the test was keeping 936
across four rounds on a profile with `featureAngleDegrees = 0` — a profile that has asserted its
input has no creases, so there was nothing there for the test to protect. `cornerHeight` is now
per-profile: `GraphCleanup.spliceAlways` for Photogrammetry and Character, the 0.25 default for
GameAsset and HardSurface.

The result is a genuine trade and is recorded as one:

| | keep (0.25) | splice always |
|---|---|---|
| quad fraction | **0.7540** | 0.7427 |
| valence-4 | 53.6% | **57.4%** |
| irregular | 46.4% | **42.6%** |
| valence defect per vertex | 0.550 | **0.482** |
| valence-2 vertices | 345 | **173** |
| deviation mean | **0.00049** | 0.00051 |

Splicing removes one corner from every face through the vertex, so pentagons become quads and quads
become triangles: triangles went 582 to 699 while n-gons went 699 to 642, and quad fraction fell 1.1
points. Kept because valence regularity matters more than raw quad count for a mesh that will be
subdivided or deformed, and because a valence-2 vertex is a defect rather than a taste. It is not a
free improvement.

It also exposed something: `v2kept=0` and yet **173 valence-2 vertices reach the output**, so a stage
after cleanup is creating them. Cleanup runs before tracing and hole filling, and neither is checked
for the defect afterwards.

## Attributing the icosphere_80k@500 regression

Resolved as far as measurement can take it. Re-running the corpus with the conditioning threshold
alone reverted to `1e-10`:

| row | 1e-6 (shipped) | 1e-10 |
|---|---|---|
| icosphere_80k@500 | 0.0213 | **0.0031** |
| icosphere_320k@2000 | 0.0031 | **0.0011** |
| torus@500 | **0.0191** | 0.0684 |
| sphere_bumpy@500 | **0.0097** | 0.0182 |
| sphere_holed@500 | **0.0387** | 0.0457 |

The threshold is the cause, and **neither value dominates**: the corpus splits 7-5 on Hausdorff and
the two largest swings point in opposite directions. That pattern is sensitivity rather than quality
— every affected row is at target 500, and a coarse target is decided largely by how the lattice
happens to meet the decimated intermediate, which is the same non-monotonicity `RemeshPipeline`
already documents in its spacing search.

`1e-6` is kept on principle rather than on the scoreboard. It bounds the condition number near
1.25e5, which is what a result stored in a `Vector3` can support; `1e-10` admits 1e9, and the
measured mean drift of 2.2e8 edge lengths is direct evidence that it returns nonsense. Nonsense that
sometimes scores better at a coarse target is a statement about the coarse target, not about the
threshold — and that hypersensitivity is now the open item, separately from this.

## The starburst: n-gons, and why the wireframe looked like that

Reported from wireframe screenshots: large fans of long thin triangles radiating from a point,
across otherwise reasonable quad regions. The cause was not a solver defect at all.

`QuadExtractor.ToMesh` fan-triangulates any face with more than four corners, because a Unity mesh
has no n-gon topology to emit them into. Extraction was producing n-gons from two places — the
tracer's degree-5 to degree-8 passes, and `HoleFiller` capping the regions the tracer left uncovered
as single faces of up to 64 corners. A 44-corner cap displayed as 42 slivers meeting at one vertex.
That is the starburst, and it was in the mesh data as much as on screen: the reference retopology of
the same asset has no n-gon at all, `{3: 389, 4: 5085}`.

Both are now quadrangulated with the same construction: one interior vertex at the centroid, then
adjacent rim edges paired into quads as `hub, v[i], v[i+1], v[i+2]`. It needs **no new rim
vertices**, which is the point — a rim edge is shared with a neighbouring face that would otherwise
have to be edited too. An even rim gives exactly length/2 quads and no triangles; an odd rim leaves
one triangle, unavoidable without splitting a shared edge.

In `HoleFiller` this replaced both the single-n-gon cap and the centroid fan. Critically, failing the
flatness gate is no longer a refusal for a rim inside `maxCapSize`: it falls back to emitting the
n-gon exactly as before. This change is only permitted to convert existing faces into quads, never
to remove one, because a hole is the worse defect and holes were a reported symptom.

`Runtime/Extraction/NgonQuadrangulator.cs` then does the same for whatever n-gons remain, as the last
pass before output. On the reference asset:

| | n-gon caps only | all n-gons | reference |
|---|---|---|---|
| **n-gons** | 426 | **3** | **0** |
| largest face | 26 | **8** | 4 |
| quad fraction | 0.7706 | **0.8418** | 0.9289 |
| face sizes | mixed | `{3: 942, 4: 5028, 8: 3}` | `{3: 389, 4: 5085}` |
| triangles | 725 | 942 | 389 |
| boundary edges | 0 | **0** | 3 |
| non-manifold edges | 0 | **0** | 0 |
| deviation mean | 0.00055 | 0.00055 | n/a |

The three survivors are deliberate. A rim that visits the same vertex twice cannot be split around a
hub: as one n-gon face every edge still appears once and the mesh stays manifold, but split, the
edge from the hub to the repeated vertex lands in four faces. Measured without that guard, the output
gained 2 non-manifold edges where the reference has none — a correctness regression rather than a
trade, so those rims are left as n-gons and counted in `nonSimple`.

### The cost, stated plainly

Irregular vertices rise, 43.5% to 51.0%, because each of the 423 hubs is a new valence-3 vertex and
every even-indexed rim vertex gains one edge — valence-5 count roughly doubles.

That number overstates the damage, and it is worth being precise about why rather than dismissing it:
the metric counts a valence-3 hub as irregular while counting a 26-corner face as nothing at all. A
26-gon is not more regular than the hub that replaces it. But face-size regularity and vertex-valence
regularity are both real, this trades one for the other, and only the first was the reported symptom.

Corpus effect, at unchanged non-manifold counts of zero everywhere:

| row | quad fraction | Hausdorff |
|---|---|---|
| cube_subdivided@500 | 0.827 -> **0.987** | 0.4106 -> 0.4095 |
| cylinder_capped@500 | 0.936 -> **0.970** | 0.1482 -> 0.2030 |
| torus@500 | 0.827 -> **0.894** | 0.0191 -> **0.0122** |
| sphere_bumpy@500 | 0.813 -> **0.884** | 0.0097 -> 0.0119 |
| icosphere_80k@8000 | 0.895 -> **0.922** | 0.0011 -> **0.0005** |
| icosphere_80k@500 | 0.828 -> 0.855 | 0.0213 -> **0.0058** |

Quad fraction improves on almost every row. Hausdorff regresses on a few coarse ones, which is the
centroid hub standing in for relief the original face spanned. And the `icosphere_80k@500` regression
recorded as open in the previous commit is largely repaired as a side effect — 0.0213 to 0.0058
against a pre-session 0.0046 — which does not vindicate the conditioning threshold, only removes the
row that made it look bad.

Face count now overshoots: 5,973 against a request of 5,474, about 9%. An n-gon becomes roughly n/2
faces and the spacing search measures the count after this pass, so it is accounted for rather than
hidden, but the tolerance absorbs it rather than correcting for it.

**This is a mitigation, not the fix.** The n-gons exist because the tracer leaves 1,054 graph edges
uncovered and closes 393 faces at degree 5 to 8, and that happens because the lattice is globally
inconsistent — see the section above. Quadrangulating the symptom makes the output usable and leaves
the cause untouched.

### Side effect worth watching

`FeatureTests.FeatureDetectionRecoversCubeShapeFidelity`, deliberately red for the whole project so
far, now passes — the suite is 100 tests, 98 passed, 0 failed, 2 skipped. It is recorded here as
*currently green for a reason not isolated* rather than as fixed, because the same claim was made
prematurely in an earlier session on the strength of a variant that was then replaced, and the test
went red again. `cube_subdivided@500` moving from 0.827 to 0.987 quad fraction is the likely cause
and nobody has confirmed it.

## Three reported defects: texture, normals, holes

### The texture was never transferred by the tool at all

`NativeRemesherWindow` called the three-argument `RemeshTopology.ReadMesh`, which discards UVs, and
then invoked `RemeshPipeline.Solve` without a `sourceUvs` argument. The window is the only path a
user takes, so every asset it produced had no UV channel and therefore no texture — while
`PerfProbe.RunScanDiagnostic` passed the UVs and measured 52,047 of them on the same asset.

The instrument was right and disagreed with the product, and nothing compared the two. That is the
same failure recorded in the previous session, where the probe read `mesh.uv` and never passed it on:
it was fixed *in the probe* and the identical omission in the window was not swept for.

### The normals: winding was decided one face at a time

Measured on the reference asset, before any change:

```
winding:  paired=11,481  sameDirection=0  |  faceNormals agree=34  disagree=5,939
```

`sameDirection=0` says every shared edge was already traversed in opposite directions by its two
faces, so the winding was globally CONSISTENT — and consistently inside-out, with 5,939 of 5,973
faces pointing away from the surface they came from.

`QuadExtractor.ShouldFlip` decided this per face, comparing that one face's Newell normal against
its vertices' averaged input normals. Newell's normal has the magnitude of twice the face area, so
on a sliver — and a centroid-hub pie slice is exactly that — it is tiny and rounding-dominated, and
the sign of its dot product with anything is close to a coin flip. No threshold repairs that,
because the quantity being thresholded carries no information at that size.

So the test flipped the 5,939 and left the 34 whose slivers gave no usable sign. Each face then
individually faced outward, which renders acceptably and is not an orientable surface: those 34
faces now traversed shared edges in the SAME direction as their neighbours. That is precisely what a
DCC tool reports as inconsistent normals, and it is why the request was "Blender has functions which
fix normal directions".

`Runtime/Extraction/FaceOrienter.cs` replaces it. Union-find over faces sharing an edge gives
connected components; each component is oriented by an AREA-WEIGHTED vote, so large well-conditioned
faces decide and slivers contribute nearly nothing; per component rather than globally because the
extractor can fragment a surface, and two shells can legitimately need opposite flips. It runs last,
after every pass that adds a face, and it fixes orientation in the FACE LIST so the scored OBJ, any
exported FBX and the Unity mesh all agree. `ToMesh` no longer decides winding at all.

```
orient:   components=5 flipped=4 faces=5971/5973 disagreeAfter=34
winding:  paired=11,481 sameDirection=0 | faceNormals agree=5,939 disagree=34
```

Stated precisely, because it is not a pure win: the surface now faces outward AND is consistently
wound, which is the correct invariant. The 34 faces that still oppose their own component are
genuine geometric folds — 0.57% — and the old per-face flip was papering over them by breaking
consistency instead. This produces correct data and exposes a real defect rather than hiding it.

### The holes, on the input that actually has a boundary

Every hole measurement so far had been taken on the reference pair, whose input is a closed
manifold. `PerfProbe.LoadScan` now reads `Benchmarks/reference/scan_open.obj` from disk, so the open
scan is reachable from the headless mirror too. `ccnb.obj`: 990,625 triangles, **4,419 boundary
edges**, 10 non-manifold edges.

| target | faces | quad fraction | output boundary edges | deviation mean/max |
|---|---|---|---|---|
| 8,000 | 7,365 | 0.8587 | 109 | 0.00061 / 0.02297 |
| 50,000 | 48,661 | 0.8830 | 1,197 | 0.00018 / 0.01777 |

The output boundary is close to a faithful coarsening of the input's own outline: 4,419 input edges
become roughly 1,200 longer ones at 50,000 quads, and fewer at 8,000. `abandoned=2 leftOpen=1`. So
the open edge is preserved rather than lost, which is right for a scan of an open surface, and it is
not the defect.

This is:

```
boundaryEdges=10357 capped=338 split=76 quadrangulated=534(+3060quads) ngonCaps=203 largestCap=109
```

**The tracer leaves 10,357 edges uncovered at 50,000 quads and the filler lids over 737 separate
regions.** A 109-corner cap becomes a 54-quad pie around one hub whose normal is the average of its
rim, so the whole lid shades as a single flat patch against the smooth surface around it. That is
what the screenshots show as large uniform light regions, and reading them as holes is
understandable: a flat lid over a region the tracer could not cover is a hole that has been covered
over rather than resolved.

Which is the same root cause as the n-gons and as the valence spread. The tracer cannot cover a
lattice that does not tile. Every mitigation so far — quadrangulating caps, splitting n-gons,
orienting faces — makes the symptom less ugly and leaves 737 regions per run that should never have
needed a lid.

## The hierarchy was written, never called, and was the whole thing

`Runtime/MeshHierarchy.cs` has been in the tree for several sessions: 386 lines, complete, tested by
nothing and referenced by nothing. Its own header already made the argument —

> A single-level Gauss-Seidel solve propagates information exactly one edge per sweep. Running
> 50-120 sweeps on a 655k-vertex mesh therefore reaches a neighbourhood, never the surface, no
> matter how many iterations are spent.

— and the plan had filed it as a deferred performance upgrade, gated behind "solve time on a 320k-tri
mesh drops > 5x". That gate was the wrong test for it. Three measurements taken from the other end
say what it actually is.

**One. The output graph is missing lattice directions, and they are genuinely missing.**
`LatticeDirections` reads each output cluster's neighbour ring in the cluster's own lattice frame and
counts how many of the four directions have a neighbour. On the reference asset:

```
occupied[0..4] = [2  24  393  1330  3208]   contested=1706  vacant=2180
```

3,208 clusters of 4,957 have all four. 1,330 are missing one, 393 are missing two, and 1,706 have a
direction claimed by more than one neighbour. Contested and vacant being nearly equal is the
cancelling pair seen directly — a valence-5 and a valence-3 manufactured together wherever two cells
fail to line up — and it is the whole of the 49% irregular against the reference's 11.3%. It also
retires mean valence as evidence of anything: Euler forces it to 4.00 whatever the spread is.

**Two. The failure is not local inconsistency.** `LatticeIntegration` measures the condition
QuadriFlow turns into a constraint: walk a triangle's three edges and the integer offsets must sum to
zero, or no assignment of cells satisfies all three. Gauge-fixed over a spanning tree so the
per-vertex choice of 4-RoSy representative drops out (`gaugeFail=0` verifies the walk):

```
faces=54740  singular=429  flat=49894  satisfied=49054 (98.3%)  |res|=[49054 819 21 0 0]
```

98.3% already satisfy it. Only 926 triangles of 54,740 are non-integrable, which bounds what any
repair of them could change at about 1% of edge classifications. So the offsets are very nearly a
valid grid map already, and the defect is somewhere else.

**Three. More sweeping does not help.** Photogrammetry runs 60/50 sweeps and Character runs 300/300;
they land at 51.3% and 50.0% irregular. Five times the work, 1.3 points.

Locally right, globally wrong, and unreachable by more of the same sweeping. That is Gauss-Seidel on
a non-convex energy converging to whichever local minimum the starting point fell into, and a
hierarchy is the standard escape.

### Positions only, and that was measured rather than assumed

The obvious thing is to solve both fields coarse-to-fine, and the first version did. Quad fraction on
the reference asset:

| configuration | Photogrammetry | GameAsset (features on) |
|---|---|---|
| no hierarchy | 0.8422 | 0.8224 |
| orientations only | 0.8389 | — |
| both | 0.8980 | 0.8224 |
| **positions only** | **0.8718** | **0.8563** |

The orientation hierarchy is worth nothing on its own — 0.8389 against 0.8422 for no hierarchy at
all — and it costs 3.4 points on the profile carrying feature constraints, plus the cube's
feature-fidelity test outright. The reason is that restricting a constraint upward is not sound: a
coarse vertex straddling a crease has a normal averaged across it, so the crease direction projected
onto that normal points nowhere in particular, and the coarse solve is pinned to that and hands it
down. And unlike a lattice, a cross field has no length scale, so there is no reach problem to solve
for it in the first place.

The hierarchical orientation solve is deleted rather than switched off. A shadow copy of a thing that
measured worse is an invitation to call it again — the same call made for `QuadExtractor.ShouldFlip`.

### How deep, and why one lattice cell per level edge

A level whose edges are longer than one lattice cell cannot say where cell boundaries fall: every
edge there classifies as many cells apart, `Compat` picks a representative with no information in it,
and what prolongates down is noise sitting where the starting guess used to be. So the depth is
bounded by `level average edge length * multiple <= lattice spacing`, and a LARGER multiple means a
SHALLOWER hierarchy.

`Benchmark/DepthSweep.cs` runs the two cases that pull in opposite directions.

```
cylinder_capped @2000, clamped to its input's finest spacing
  multiple  levels  quadFrac  hausdorff  bndEdges  largestCap
  4         1       0.9614    0.00083    52        19
  2         1       0.9614    0.00083    52        19
  1         2       0.9625    0.19887    131       39
  0.5       3       0.9620    0.20101    212       46
  0.25      5       0.9654    0.20167    197       39

ref_high @5474
  multiple  levels  quadFrac  irregular
  4         1       0.8422    0.5103
  2         1       0.8422    0.5103
  1         3       0.8718    0.4667
  0.5       5       0.8973    0.4326
  0.25      7       0.8950    0.4479
```

Depth is monotone value on the asset, saturating around seven levels, and a cliff on the cylinder
between one level and two. **No value of this multiple satisfies both**, and that matters more than
the value chosen: the cut is not a tuning knob that was set correctly, it is a knob that cannot
express the difference. One cell per level edge is kept because it is the principled statement of
what a level can represent, and the cylinder's remaining cost is recorded below rather than tuned
away.

### Two vertices on the axis, and a rule reversed

The cylinder's regression turned out not to be a tracer or a field problem at all. Classifying every
output vertex by its distance to the true capped cylinder:

```
cyl-hier   far(>0.02) = 2 of 1163      r=0.009 y=-0.056  d=0.491
                                       r=0.011 y=+0.109  d=0.489
cyl-flat   far(>0.02) = 0 of 1331
```

Two vertices on the cylinder's axis, one near each end, each 0.49 from any surface on a cylinder of
radius 0.5. Everything else about the case was fine — quad fraction 0.9625, no non-manifold edges,
winding consistent. Two vertices, and 0.2020 worst-case deviation against 0.0008.

They are hole-filler hubs. The hierarchy leaves the tracer unable to cover a band around the
cylinder, that band's boundary is a loop which goes all the way AROUND the tube, and `HoleFiller`
caps it with a hub at the rim's centroid. Which is on the axis.

The existing gate could not catch it, and its own note said what the right test was:

> What actually differs is whether a flat lid is near the surface it replaces.

`maxRimRelief` measures relief against radius as a proxy for that, and a loop encircling a tube is
the one shape where the proxy is exactly wrong: such a rim is perfectly FLAT, relief zero, so it
passes. `maxHubSurfaceDistance` measures the thing directly — distance from the proposed hub to the
input surface, via `SpatialGrid.NearestDistance`, limited to 1.5 lattice spacings. The failing case
is fifteen spacings out, so nothing here is finely tuned.

Failing it **abandons the loop** rather than falling back to an n-gon, which reverses the standing
rule in that file that a hole is the worse defect. Deliberately: a hole is visible as a hole, while a
lid through the middle of the model reads as the shape being wrong. The n-gon fallback would not have
helped anyway — `NgonQuadrangulator` puts a hub at the centroid of whatever it is given.

On the cylinder: `offSurface=2`, no vertices inside the solid, worst-case deviation 0.0332, and 78
boundary edges where there were none. On the reference scan `offSurface=0` — the guard never fires
there.

### What it bought, and what it cost

Corpus A/B, same build, hierarchy on and off, 30 rows plus 6 degenerate-input rows unchanged:

| case | quadFrac | irregular | hausdorff |
|---|---|---|---|
| icosphere_320k @8000 | 0.8948 → 0.9493 | 25.0% → 10.6% | 0.00042 → 0.00039 |
| icosphere_80k @2000 | 0.8805 → 0.9269 | 29.1% → 16.0% | 0.00183 → 0.00098 |
| icosphere_320k @500 | 0.8369 → 0.9116 | 35.5% → 16.7% | 0.00439 → 0.00344 |
| sphere_bumpy @2000 | 0.8640 → 0.9237 | 38.1% → 27.4% | 0.00443 → 0.00383 |
| sphere_bumpy @8000 | 0.9121 → 0.9258 | 23.0% → 18.0% | 0.00219 → 0.00106 |
| robust_selfintersect @500 | 0.8340 → 0.8719 | 32.5% → 26.2% | 0.00571 → 0.00668 |
| cube_subdivided @2000 | 0.9906 → 0.9941 | 3.79% → 1.49% | 0 → 0 |
| **cylinder_capped @2000** | 0.9614 → 0.9629 | 8.24% → 5.40% | **0.00082 → 0.03324** |

Irregular improves on 20 rows of 30 and quad fraction on 20, with every degradation under 0.007
except the cylinder's deviation. Non-manifold edges stay at zero everywhere. Boundary edges rise on
four rows: 78 on the cylinder, and single digits on `sphere_holed` and `icosphere_80k @8000`.

Reference asset, Photogrammetry at 5,474:

| | before | after | reference retopology |
|---|---|---|---|
| faces | 5,969 (+9.0%) | 5,491 (+0.3%) | 5,474 |
| quad fraction | 0.8422 | 0.8718 | 0.9289 |
| triangles | 939 | 703 | 389 |
| n-gons | 3 | 1 | 0 |
| irregular vertices | 51.0% | 46.7% | 11.3% |
| triangles non-integrable | 926 | 503 | — |
| mean deviation | 0.00054 | 0.00053 | — |
| runtime | 12.1 s | 16.7 s | — |

Face count is now essentially exact, which is a side effect worth naming: the spacing search measures
what extraction produces, so a lattice that tiles better lands the count on the first or second probe.

### The cylinder's remaining cost, diagnosed rather than tuned

`cylinder_capped @2000` is clamped to the finest spacing its input supports and is the one case where
the hierarchy is a net loss: worst-case deviation 0.0008 → 0.0332 and 78 boundary edges. The
mechanism is worth stating because it is not a bug to be found.

A cylinder's lattice has a global constraint a generic surface does not: going around the tube, the
cells must close up, and they can only do that if the circumference is an integer number of cells.
The spacing here is fixed — it is the target quad size — so when it does not divide the
circumference, a consistent lattice MUST have a seam. Without the hierarchy the field is locally
inconsistent everywhere and the defect is smeared into many small ones the tracer copes with. With
it, global agreement concentrates the whole unavoidable defect into one band, and the tracer cannot
cover that band.

So the hierarchy did not create this defect; it collected it. Fixing it needs the spacing to be
allowed to stretch — a per-vertex scale field, or QuadriFlow's integer constraints, both of which
solve for a parameterisation rather than imposing one. Those are the next items and this is now a
concrete reason for them rather than a general aspiration.

### Two tests changed what they measure, and both are worth reading

Neither was loosened to pass. Both were measuring the wrong quantity and the hierarchy made that
visible.

`PreDecimationBeatsSolvingTheDenseMeshAtAnAggressiveTarget` was the kill-switch for the
pre-decimation stage: "if this ever fails, the stage is not earning its complexity". Pre-decimation's
quality margin fell from over 10 points of quad fraction to 1.8, so it failed. What the test's
premise was really describing — quality collapsing past a 2.5x spacing-to-edge ratio — is a
single-level solver's inability to coarsen far, which is the limit the hierarchy removes. The stage's
quality case is gone; its cost case is untouched, since the reference asset is 1.5M triangles and the
solver is handed 54,740. Renamed to
`PreDecimationNoLongerBuysQualityOnceTheHierarchyCarriesTheCoarsening`, and it now asserts that
decimation does not make quality worse and does reduce what the solver is given.

`FeatureDetectionRecoversCubeShapeFidelity` compared whole-surface deviation between crease
detection on and off. Both arms leave a 63-to-64-corner hole for the filler, and the worst point on
the surface is in that region in both arms — hausdorff 0.297 against 0.290 on a UNIT cube. Whichever
arm happens to leave the slightly larger cap wins, by a margin that has nothing to do with creases,
and the verdict flipped three times across unrelated changes to the hole filler. It now measures
crease coverage directly: mean distance from each input crease vertex to the nearest output vertex,
scored for both arms against the same detected feature set. The caps do not enter it and the number
moves only when crease handling moves.

### One thing this opens up

`PreDecimation_LiftsTheCoarseTargetEnvelope` asserts that a 4x spacing is worse than a 2x one, and
its failure message says what a failure would mean: "RemeshTopology.maxUsefulSpacingMultiple is
understated". With the hierarchy on, 4x scored 90.3% against 2x at 90.0% — it stopped degrading. The
spacing envelope this project has been clamping targets against (`minUsefulSpacingMultiple = 1.5`,
`MaxQuadCountForInput`) is at least partly a single-level artefact, which means coarser targets than
currently allowed may now be reachable. Not changed here: the ceiling gates every corpus row, and
moving it needs its own measurement.
