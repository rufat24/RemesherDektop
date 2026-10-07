# Quadremesh

Unity **6000.0.58f2**. Two things live here.

| | What | State |
|---|---|---|
| **NativeRemesher** | A from-scratch C# quad remesher — Instant Meshes (Jakob et al. 2015) with pieces of QuadriFlow, no external engine, no native plugin | Working, and the only work item. Quality gap to a human retopology is measured and localised below |
| **QuadRemesher** | An Editor bridge that drives the existing Exoside engine | Finished. **Read-only** — it is the quality oracle, not a work item |

One menu item: **`Tools ▸ Native Remesher ▸ Native Remesher`**.

### Two parallel architectures were tried and removed

Recorded so they are not rebuilt. Both lived in their own folders and neither fed into
`NativeRemesher`, so removing them changed nothing about the working tool.

- **v2 — integrate the lattice, repair it with a min-cost flow, then classify.** Everything
  verified: the repair drove the integrability defect from 206 units to 32 on a torus, and
  reproduced v1 bit-for-bit with the repair off. It still made irregularity **worse on every case**
  (torus 483 → 617, character 2,149 → 2,500), because its currency is the same three-valued
  vocabulary the collapse already reads — it changed 11 % of edges to fix a 1 % defect.
- **v3 — cut the surface, solve a linear system for a (u,v) grid, read quads off the integer grid.**
  The right family (this is what QuadRemesher does) and it never produced a usable mesh on the real
  assets: 1,189 faces in 34 components against v1's 6,261 in 9. Four causes were tested and three
  refuted — field smoothing (60→1,000 iterations moves ccnb's singularities 3,786 → 3,777), chart
  size, and solver strength (a fully converged solve changes nothing: gradient error 0.383 → 0.383).
  The real blocker is the missing **mixed-integer seam stage**: without pinning the grid to whole
  numbers across the cut, a least-squares fit spreads the field's non-integrability as distortion
  over the whole surface, folding ~7 % of faces and losing ~30 % of cells.
  One lesson worth keeping: v3's "zero irregular interior vertices" was largely an artifact of a
  guard discarding 43 % of candidate cells. **Sub-metrics on a disconnected mesh mean nothing** —
  score component count and largest-component fraction first.

---

## What it does

Takes a dense triangle mesh and produces a low-poly, quad-dominant one: fewer polygons, and the
polygons shaped like a person would have drawn them. Decimation is a *stage* of that, not the
product — a decimated mesh has fewer triangles in the same bad arrangement.

**`Tools ▸ Native Remesher ▸ Native Remesher`.** Pick a mesh, a target quad count and a profile.

| Profile | For | Pre-decimates | Features |
|---|---|---|---|
| `GameAsset` | props, general content | optional | creases at 30° |
| `HardSurface` | mechanical parts, chamfers, flat panels | optional | creases at 20°, snapping on |
| `Photogrammetry` | dense noisy scans | **yes** | off — scans have no clean creases |
| `Character` | smooth organics where edge flow matters | yes | off, boundary-constrained |

The result is written as a real asset that survives a domain reload, with UVs sampled from the
source surface and normals computed from the output faces.

## Where it stands

Scored against the user-supplied reference pair in `Assets/ccnb/` — a 1.5M-triangle scan and a
retopology of the same asset at 5,474 faces — and against **Exoside QuadRemesher's output on that
same scan**, measured as a black box in `Benchmarks/quadremesher-measured.md`.

| Photogrammetry @ 5,474 | ours | their retopology | QuadRemesher @ 12,451 |
|---|---|---|---|
| faces | 5,823 (+6.4%) | 5,474 | 12,451 |
| quad fraction | 0.8853 | 0.9289 | **0.9997** |
| triangles | 668 | 389 | **4** |
| n-gons | 0 | 0 | 0 |
| **irregular vertices** | **1,385 (25.2%)** | 596 (11.3%) | **341 (2.7%)** |
| max valence | 10 | 8 | **7** |
| non-manifold edges | 0 | 0 | 0 |
| mean deviation (bbox-normalised) | 0.00044 | — | — |
| runtime | 12.9 s | — | — |

**On the second real asset — `ccnb.obj`, 990k-triangle open photogrammetry — we are now level with
QuadRemesher or ahead**: 21.2% irregular against its 25.7%, 406 boundary edges against 1,274, 26
valence-1 vertices against 377. Blender's QuadriFlow *refuses that asset outright*. So the deficit is
not uniform: it is concentrated on the clean closed character asset, which is exactly where a global
integer solve pays and local smoothing does not.

`Character` reaches 0.8806 quad fraction and `GameAsset` 0.8611 on the same asset.

Read the singularity **count**, not the fraction — it is a topological quantity that barely moves
with resolution, and QuadRemesher lands on 341–479 at both 12k and 66k faces. So the real comparison
is 341 against our 2,278, in a mesh with twelve times the vertices.

Face count, quad fraction, manifoldness, watertightness, winding and surface fidelity are all in
good shape. **Irregular-vertex fraction is the gap.** Two further defects it does not carry: our
quads reach a 17° minimum interior angle at the 5th percentile, and valence-1 vertices reach the
output — not a quality issue but a broken one.

## The gap, localised

Not a guess. Three instruments, each of which changes no output and exists only to make a claim
falsifiable.

**`LatticeDirections`** reads each output cluster's neighbour ring in the cluster's own lattice frame
and counts how many of the four lattice directions actually have a neighbour:

```
occupied[0..4] = [2  24  393  1330  3208]   contested=1706  vacant=2180
```

1,330 clusters of 4,957 are missing a lattice direction outright; 1,706 have one claimed twice.
Those counts being nearly equal *is* the valence-5/valence-3 cancelling pair, seen directly. It also
retires mean valence as evidence of anything — Euler's formula pins it to 4.00 whatever the spread
is, so a "perfect" mean was never a signal.

**`LatticeIntegration`** measures the condition QuadriFlow turns into a constraint: walk a triangle's
three edges and the integer lattice offsets must sum to zero, or no assignment of cells satisfies all
three of its edges. Gauge-fixed over a spanning tree so the arbitrary per-vertex choice of 4-RoSy
representative drops out:

```
faces=54740  singular=429  flat=49894  satisfied=49403 (99.0%)  gaugeFail=0
```

99% already satisfy it. So the offsets are very nearly a valid integer grid map, and the defect is
**not** local inconsistency — which bounds what repairing them could buy at about 1% of edge
classifications.

**`SingularityAnalysis`** puts the field's own singularities at 429 of 54,740 faces (0.78%) against
the reference retopology's 596 irregular vertices. The field asks for roughly the right structure.
The damage is done between the field and the output.

**`LatticeAudit`** — the instrument that finished the job, by running the pipeline on closed analytic
surfaces whose minimum irregular-vertex count follows from Euler's formula, so "better" can be
distinguished from "best possible". The **torus** is the row with no alibi: χ = 0, so a defect-free
answer exists, and the corpus torus is 128 × 64, so a perfect 64 × 32 = 2,048-quad grid is available
against a request for 2,000. It has no features to align to and no curvature variation to adapt to.

```
field singularities  0      lattice torn edges  0.00%    integrability  99.5% satisfied
collapse graph       170 irregular              output   283 irregular  (floor: 0)
```

Conclusion, measured rather than inferred: **the field and the lattice are both near-optimal, and
extraction manufactures the irregularity.** On the torus a perfect field and an untorn lattice become
a graph with 170 irregular vertices before the tracer runs; on the scan, 429 field singularities
become 1,854. The cause is structural: `LatticeCollapse` classifies each input edge **independently,
in a frame built pairwise between its two endpoints**, so nothing in extraction holds a global
coordinate and two clusters occupying one lattice cell are merged only if some input edge happens to
say so. It is **under**-merging — `contested = 119` against a valence-5 count of `115` on the torus
is that failure counted directly.

This retires the previous conclusion here ("the lattice is locally converged and globally sheared").
The lattice is not torn: **0.00% of torus edges and 1.6% of scan edges** span two cells, and what
looked like a 9.8% discard rate is 8.2% benign diagonals — which is what a triangulated input does to
a *correct* lattice, since two triangles covering one cell share that cell's diagonal.

The fix is **cell-indexed extraction**: build global `(i, j)` coordinates from the integer offsets and
emit one vertex per occupied cell, so a cell is a face by construction. That is QuadriFlow's
extraction against Instant Meshes' edge collapse, and the min-cost flow already in the tree is its
prerequisite rather than a fix in itself. `PLAN.md` W1.5 has the plan and the gate.

## Pipeline

| Stage | File | Note |
|---|---|---|
| 0 | `PreDecimate.cs` | QEM edge collapse. Mandatory on scans: 1.5M triangles → 54,740 |
| 1 | `RemeshTopology.cs` | weld, half-edge table, cyclic CSR, boundary and non-manifold flags |
| 2 | `OrientationField.cs` | 4-RoSy cross field, coloured Gauss-Seidel sweeps |
| 3 | `MeshHierarchy.cs` + `HierarchicalSolve.cs` | coarse-to-fine, **positions only** — see below |
| 4 | `PositionField.cs` | the integer lattice |
| 5 | `Runtime/Extraction/*` | lattice → graph → faces. Nine passes; `QuadExtractor.cs` is the facade |

Extraction, in order: **`EdgeSubdivide`** (split input edges the lattice cannot represent, so the
next pass has nothing to discard) → `LatticeCollapse` (classify every edge on the integer lattice,
three ways) → `LatticeDirections` (measure) → `GraphCleanup` → `FaceTracer` (degree-ordered, 4
before 3) → `HoleFiller` → `QuadRegularizer` → `NgonQuadrangulator` → `FaceRepair` →
`FaceOrienter` → `NormalRecalculator`.

`EdgeSubdivide` is QuadriFlow's step 5 and it roughly **halved irregularity on both real assets**
(44.1% → 25.2% on the character, 44.5% → 21.2% on ccnb). Discarding an edge destroys connectivity
that no later pass can rebuild, which is why years of downstream graph repair moved nothing. It can
diverge — a midpoint split creates a new, unconstrained edge to the opposite vertex — so it carries a
guard that abandons and returns the input untouched rather than shipping half a refinement.

**The hierarchy runs on the position field only, and that is measured, not assumed.** Quad fraction
on the reference asset: no hierarchy 0.8422, orientations only 0.8389, both 0.8980 *but 0.8224 with
features*, positions only 0.8718 and 0.8563 with features. Restricting a feature constraint upward
is unsound — a coarse vertex straddling a crease has a normal averaged across it, so the crease
direction projected onto that normal points nowhere in particular. Its depth is bounded at one
lattice cell per level edge, because a level whose edges are longer than a cell cannot say where cell
boundaries fall.

## Measuring

Two Unity instances cannot hold one project, so measurement lives in a second project inside this
one. `Headless/README.md` has the commands; the short version:

```bash
bash tools/sync-headless.sh
```

Tests: **128 total, 126 pass, 2 skipped, 0 fail.** The corpus gate is 30 scored rows over nine
procedural meshes at three targets each, plus six degenerate-input rows that must not crash or hang.
Its CSVs are committed under `Benchmarks/` with the commit that moved them, so every metric movement
is attributable to a SHA.

`LatticeAudit.Run` is the quality gate to reach for when changing extraction — it scores against
Euler-formula floors rather than a reference file, so it can tell "better" from "best possible".

**Two caveats on the corpus, both newly measured.** `MeshCorpus.Torus` and
`MeshCorpus.CappedCylinder` are wound **inside-out**, and `Build` calls `RecalculateNormals`, so
their vertex normals are inverted; `normalConsist` and `normalFlipped` are meaningless on those rows
until it is fixed. The solvers are unaffected — a 4-RoSy cross is invariant under flipping the normal.
And `Benchmarks/baseline.csv` is stale, so its gated regressions are currently false.

### The documents, and which one wins

- **`Benchmarks/README.md`** — the authority on why anything is the way it is. 2,300 lines of
  measurement: what was tried, what it scored, and what was wrong about the reasoning behind it.
  Read it before changing a constant; most are load-bearing with numbers attached.
- **`PLAN.md`** — what to do next, ordered, with a done-when for each item.
- **`Benchmarks/quadremesher-measured.md`** — QuadRemesher's output measured on our own asset, and
  the stage-by-stage localisation of our gap. **Wins on priorities** where it and the two documents
  below disagree, because it is the only one that measured the competitor or established a floor.
- **`Benchmarks/quadremesher-study.md`** — QuadRemesher's documented option surface. Accurate on
  features; its §4 ranking is superseded.
- **`HANDOVER.md`** — an early session report. Part A (the Exoside bridge) is accurate; its Part B
  diagnosis is **superseded and wrong in its conclusions**. See the banner at the top of that file.

## Open items, in the order they are worth doing

See `PLAN.md` for the current ordering and the done-when for each. In short:

1. **Cell-indexed extraction** (`PLAN.md` W1.5) — the 44%-vs-2.7% gap. Build global `(i, j)`
   coordinates from the integer offsets and emit one vertex per occupied cell, instead of collapsing
   input edges pairwise. Finish the min-cost flow first; it is the prerequisite, not the fix.
   Gated on the torus (≤ 10 irregular, floor 0, today 283) and the icosphere (≤ 20, floor 8, today
   329) — the first quality gate here that measures against best-possible rather than a reference.
2. **Fix the two inside-out corpus generators** and regenerate `baseline.csv`. Cheap, and two
   metrics are meaningless until it is done.
3. **Sliver quads and valence-1 vertices.** Min interior angle p05 is 17° against QuadRemesher's
   63°, and 12 valence-1 vertices reach the output. Neither is gated by anything today. Related:
   173 valence-2 vertices survive despite `GraphCleanup` reporting `v2kept=0`.
4. **Feature and seam aligned constraints, then UV seam vertex splitting** (`PLAN.md` W2, W4). Fixes
   the reported grey bands, and it is Instant Meshes' feature-alignment path which this port omitted.
5. **Boundary vertices are unconstrained in both solvers.** `disc_open` is the corpus case that says
   so, and open scans are the input this tool is aimed at.
6. **Curvature-adaptive sizing** (`PLAN.md` W3). Smaller than assumed — edge-length CV is 0.244–0.314
   against QuadRemesher's 0.262–0.396 — but real, and it is what `adaptiveSize` would expose.
7. **The target ceiling may be liftable.** `PreDecimation_LiftsTheCoarseTargetEnvelope` stopped
   degrading at 4× spacing once the hierarchy landed, and its own failure message says what that
   means: `maxUsefulSpacingMultiple` is understated. It gates every corpus row.
8. `FieldReport.indexSum` reads 0 on `cube_subdiv` where Euler's formula requires 8, and that number
   is the one thing that validates the singularity detector. Attribute-aware quadrics in
   `PreDecimate`; spatial reordering for decimation cache locality.

Deliberately **not** next: anything learned. The gates for it are in the plan and the first one —
"the solver is no longer the bottleneck" — is not met. See `Benchmarks/README.md` on BPT, including
why "fine-tune BPT to emit a guidance layer" is a new model rather than a fine-tune.
