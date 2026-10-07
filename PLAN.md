# NativeRemesher — implementation plan

Written 2026-09-08. Supersedes the stage-4 material in `HANDOVER.md`.

Two authorities sit under this: `Benchmarks/README.md` for measurements, and
`Benchmarks/quadremesher-study.md` for the competitive analysis this plan's priorities come from.

> **Amended 2026-09-08 by `Benchmarks/quadremesher-measured.md`,** which measured QuadRemesher's
> actual output and ran this pipeline on inputs whose optimal answer follows from Euler's formula.
> Two claims below did not survive:
>
> - **W1 is not the 44 % → 11.3 % work.** The entire integrability defect is 249 units on the scan
>   and 42 on the torus, against 1,425 and 170 vertices of damage at the collapse stage. W1 stays
>   first, but as the **prerequisite for cell-indexed extraction** (new W1.5) rather than as a fix
>   that moves the headline number by itself.
> - **W2 and W3 cannot be the cause.** On a torus — no features to align to, no curvature variation
>   to adapt to, a defect-free answer available, a field with exactly 0 singularities, a lattice with
>   0 torn edges — we still emit 283 irregular vertices. Both remain real gaps; neither is the
>   headline, and doing either first would leave the number where it is.
>
> Also settled there: W5's unattributed `disagreeAfter`, and a refuted hypothesis about the collapse
> cluster-spread bound. And the target is understated — QuadRemesher reaches **2.7 %** irregular on
> this asset, not the reference retopology's 11.3 %.

> **Answered 2026-09-08 by `Benchmarks/how-to-match-quadremesher.md`.** The question "what reaches
> that quality" is no longer open. Blender ships QuadriFlow, so it was measured on our own inputs:
>
> ```
>                    ours          QuadriFlow    QuadRemesher   Euler floor
> scan @5474         2278 (44.1%)  162 (3.3%)    341 (2.7%)     --
> torus @2000         283 (15.2%)   24 (1.2%)    0-10             0
> icosphere @2000     329 (16.0%)    8 (0.4%)    --               8
> quad fraction      0.877-0.956   1.0000        0.9997-1.0000    --
> triangles          84-671        0             0-4              --
> ```
>
> QuadriFlow hits the Euler floor **exactly** on the icosphere, is pure quad on all three inputs, and
> **beats QuadRemesher on our scan**. It is **MIT licensed** and its source is readable, so this is a
> **port**, not research. Published numbers agree (2–5× fewer singularities than Instant Meshes) and
> also show the whole field clusters at 66–93, so QuadriFlow is the ceiling rather than a stepping
> stone — QuadWild scores worse and is GPL-3 + Gurobi.
>
> W1 below is therefore reorganised as a port of QuadriFlow's `ComputeIndexMap()`, and two new steps
> appear that were not previously identified: **W1.6 `subdivide_edgeDiff`** (split unrepresentable
> edges instead of discarding them — the root of our defect chain) and **W1.7 `FixFlipSat` +
> `FixValence`**.

---

## Where we are, measured today

Reference asset, target 5,474 faces, three input profiles. Reference AI retopology for comparison.

| | Photogrammetry | Character | GameAsset | Reference | QuadRemesher |
|---|---|---|---|---|---|
| irregular vertices | 44.1 % | 44.5 % | 48.6 % | 11.3 % | **2.7 %** |
| quad fraction | 0.8773 | 0.8806 | 0.8611 | 0.9289 | **0.9997** |
| triangles | 671 | 647 | 763 | 389 | **4** |
| max valence | 13 | 20 | 15 | 8 | **7** |
| min interior angle, p05 | 17.4° | 16.8° | 13.4° | 52.9° | **62.9°** |
| inconsistent edges | 0 | 0 | 0 | — | — |
| largest hub star | 3 | 3 | 3 | — | — |
| lattice satisfied | 99.0 % | 99.0 % | 98.7 % | — | — |
| repair routable | 128/135 x, 107/114 y | 127/132, 104/107 | 146/150, 133/135 | — | — |

QuadRemesher column measured from `Assets/QuadRemesher/Output/20260515221044_54b8f8f4_Retopo 1.fbx`,
12,451 faces on the same input — see `Benchmarks/quadremesher-measured.md` §1. Its 66k-face run reads
0.7 % irregular. Singularity *count* barely moves with resolution, so the comparison that matters is
341–479 for QuadRemesher against **2,278** for us, at a twelfth of the vertices.

Tests: **128 total, 126 pass, 0 fail.** Zero compile errors.

**The headline gap is irregular vertices, and the target is 2.7 %, not 11.3 %.** Everything below is
ordered by how much of that gap it closes, except W0 which is a correctness debt. Two defects the
irregularity number does not carry, both newly measured: our quads reach **17° minimum interior
angle** at the 5th percentile against QuadRemesher's 63°, and **valence-1 vertices reach the output**
(12 on Photogrammetry) — not a quality issue but a broken one.

---

## W0 — Prove the HoleFiller projection fix
**Size: XS. Blocking nothing, but unverified.**

`SurfaceProbe.Project` was conflating "where is the surface" with "was this close enough to trust",
returning `false` for both. `TryGridFill` therefore kept **raw un-projected Coons points** whenever the
gate failed, and only rejected a grid when *more than half* its interior missed — so up to half the
grid vertices could sit off the model. Those are the spikes and plates in the reported renders.

Fixed: projection is now unconditional, the gate result only decides whether the grid is the wrong
construction for that rim.

~~**Problem: no counter measures this.**~~ **A counter does measure it: the corpus geometry columns.**
The *reference* run is bit-identical on every statistic, which is what led to that conclusion — but
the reference asset is one closed scan, and the procedural corpus is where a hole filler is
exercised. `hausdorffMax` and `planarityMean` move on 14 of 30 rows, and **the result is mixed**:

```
icosphere_80k@8000    0.000407 -> 0.000236   -41.9 %
icosphere_320k@2000   0.001747 -> 0.001072   -38.6 %
icosphere_320k@8000   0.000394 -> 0.000244   -38.2 %
sphere_bumpy@8000     0.001059 -> 0.000900   -15.1 %
icosphere_80k@2000    0.000984 -> 0.000884   -10.1 %
...
torus@500             0.010694 -> 0.011565    +8.1 %
cylinder_capped@500   0.133599 -> 0.156016   +16.8 %
sphere_bumpy@2000     0.003829 -> 0.005306   +38.6 %
```

Nine rows better by up to 42 %, five worse by up to 39 %. So the fix is right in the common case and
**`sphere_bumpy@2000` and `cylinder_capped@500` are genuine regressions that need explaining** —
unconditional projection onto the nearest surface point is not obviously right on a bumpy surface,
where the nearest point can be on the far side of a ridge.

(Attribution caveat: this diff is against `Benchmarks/run-ship.csv` from `4f1e206`, so it also
contains the `FaceOrienter` weak-vote change. That one cannot move `hausdorffMax`, and the
measurement changes in this session are measurement-only, so the geometry movement is the
`HoleFiller` change.)

Still add the counter below — it says whether the bug *fired*, which the corpus cannot.

- Files: `Runtime/Extraction/HoleFiller.cs`
- Do: add `gridVerticesSnapped` to `HoleFillStats` — incremented where `offSurface++` already is —
  and report it in `PerfProbe`. Add a unit test with a saddle rim whose Coons interior provably
  leaves the surface, asserting every emitted vertex is within `maxHubSurfaceDistance`.
- Also do: explain or fix `sphere_bumpy@2000` (+38.6 %) and `cylinder_capped@500` (+16.8 %), then
  regenerate `Benchmarks/baseline.csv` so the corpus gates this properly from here on.
- **Done when:** the counter is non-zero on at least one profile (proving the bug fired) and the new
  test fails against the old `Project` semantics.

---

## W1 — Finish the global integer solve
**Size: M. Prerequisite for W1.5, which is the work that closes the 44 % → 11.3 % gap.**

This is the QuadriFlow contribution (Huang et al. 2018, BSD-3). The flow is built and solving —
~1.0 s on a 49,894-node dual, routing ~95 % of the defect. What remains is using the answer.

**Why it is first, correctly stated.** Not because the integrability defect is large — it is 249
units on the scan and 42 on the torus, which bounds its direct effect at roughly 1 % of edge
classifications, exactly as `Benchmarks/README.md` says. It is first because **integer offsets can
only be integrated into consistent global `(i, j)` coordinates if the system is curl-free**, and
those coordinates are what W1.5 extracts from. W1 alone will move the headline number very little;
that is the expected result, not a failure, and W1.3's own verification criterion is the residual
drop, not the irregularity.

### What W1 is a port of

QuadriFlow's `ComputeIndexMap()`, twelve steps, mapped against this tree. The reference is
[MIT licensed](https://github.com/hjwdzh/QuadriFlow) — read it rather than re-deriving from the paper.

| # | QuadriFlow | Ours | State | Item |
|---|---|---|---|---|
| 1 | `BuildEdgeInfo` | `LatticeIntegration.BuildEdges` | done | — |
| 2 | sharp edges | `FeatureGraph` | partial | W2 |
| 3 | `BuildIntegerConstraints` | gauge fixing + `LatticeRepair.Plan` | partial — ours balances per **face**, QuadriFlow per **component**, and we have no `allow_changes` mask | W1.2 |
| 4 | `ComputeMaxFlow` → `edge_diff` | `MinCostFlow` | built, not applied | W1.1, W1.3 |
| 5 | **`subdivide_edgeDiff`** | — | **missing** | **W1.6** |
| 6 | `FixFlipHierarchy` | — | missing | W1.7 |
| 7 | **`FixFlipSat`** | — | **missing** | **W1.7** |
| 8/9 | `optimize_positions_sharp`/`_fixed` | `PositionField` | partial | W3 |
| 10 | **`AdvancedExtractQuad`** | `LatticeCollapse` (greedy pairwise) | **wrong mechanism** | **W1.5** |
| 11 | **`FixValence`** | — | **missing** | **W1.7** |
| 12 | `optimize_positions_dynamic` | — | missing | later |

Keep our orientation field and hierarchy — they are already good: 0 field singularities on the torus,
exactly 8 on the icosphere, smoothness 0.999. The gap is entirely steps 3–7, 10 and 11.

> **W1.1-W1.4 are done, and the measured result is NEGATIVE.** The second architecture that
> held this work has since been REMOVED at the user's request, along with the third; see the
> "tried and removed" section of README.md for the summary that replaced its write-up. The flow is fixed, applied, and verified to the unit (residual 206 → 32 on the torus, exactly the
> stranded remainder), and `v2-norepair` reproduces v1 bit-for-bit so the A/B is clean. But the
> repaired lattice makes quad fraction and triangle count clearly better and **irregularity worse on
> every case** — because the repair's currency is the same three-valued vocabulary the collapse
> reads, so it changed 11 % of edge classifications to fix a 1 % defect.
>
> Two things follow. **What got built is W1.4, not W1.5** — `CellCollapse` is still a per-edge
> classifier, just reading a better array. And **W1.6's ordering was wrong**: QuadriFlow subdivides
> *after* the flow so an offset pushed past one cell becomes representable again; ours subdivides
> before, and the `clampToRepresentable` flag is the cheap stand-in. Do the post-flow subdivision
> before building anything else on the repair.

### W1.1 — ✅ DONE — Fix the reduced-cost clamp
`MinCostFlow.ShortestPath` contains `if (weight < 0) weight = 0`. With correct Johnson potentials
reduced costs cannot go negative, but nodes unreachable from the source keep potential 0 and can
produce negatives, and the clamp silently absorbs them. Evidence it matters: swapping the priority
queue for a heap changed total cost 3648222 → 3673228, and **an optimal min-cost flow has a unique
cost**, so at least one of those two runs was not optimal.

- Files: `Runtime/MinCostFlow.cs`
- Do: mark unreachable nodes explicitly and exclude them from the potential update rather than
  clamping. Add a test where a clamp changes the answer.
- **Done when:** cost is invariant to queue implementation, and every existing `MinCostFlowTests`
  case still asserts its hand-computed optimum.

### W1.2 — Record the arc → half-edge mapping
`LatticeRepair.Plan` builds arcs from half-edges but keeps only `arcFrom/arcTo/arcCost`, so
`MinCostFlow.FlowOn(arc)` cannot be attributed back to an edge.

- Files: `Runtime/LatticeRepair.cs`
- Do: carry a parallel `arcHalfEdge` list; return the solved flow alongside the stats.

### W1.3 — Apply the flow to `jump[]`
Each unit of flow across a dual edge changes that edge's lattice offset by one, and must be
**transported into the frame the offset is stated in** — the `Rotate(jump, -r)` composition that
`LatticeIntegration.FaceResidual` uses. `jump[h]` and `jump[twin]` must stay antisymmetric by
construction, not by accident.

> A sign error here produces a plausible-looking mesh with a slightly worse number — the defect class
> this project has repeatedly failed to catch by eye. Do not verify this by looking at output.

- Files: `Runtime/LatticeRepair.cs`, `Runtime/LatticeIntegration.cs` (read-only)
- **Done when:** re-running `LatticeIntegration.Measure` *after* applying the repair shows
  `total` residual dropping from ~511 to ~7 (the stranded remainder) on Photogrammetry. Nothing else
  counts as verification of this step.

### W1.4 — Have `LatticeCollapse` read the repaired offsets
Today `LatticeCollapse.cs:162` calls `PositionField.Classify(shift0, shift1)` independently per input
edge, so it re-derives offsets that the repair has already corrected and throws the repair away.

- Files: `Runtime/Extraction/LatticeCollapse.cs`
- **Done when:** the repaired offsets are what extraction reads, with quad fraction not regressing
  and `inconsistentEdges` still 0.

**Do not expect this to move the headline number, and do not treat it as the test of W1.** Feeding
repaired offsets into the same per-edge greedy collapse keeps the mechanism that loses the structure.
W1.4 is plumbing; W1.5 is the change.

### W1.6 — ✅ DONE — `subdivide_edgeDiff`: split unrepresentable edges instead of discarding them

**Shipped 2026-09-08.** `Runtime/Extraction/EdgeSubdivide.cs`, on for every profile, 136 tests /
134 pass / 0 fail. Measured on both real assets, at the face counts the competitors were run at:

| | irregular | quadFrac | tris | maxValence | v1 | hausdorff |
|---|---|---|---|---|---|---|
| character @5,474 off | 2,278 (44.1 %) | 0.8773 | 671 | 13 | 11 | 0.01061 |
| character @5,474 **ON** | **1,385 (25.2 %)** | 0.8853 | 668 | 10 | 3 | 0.00949 |
| character @12,451 off | 5,184 (42.9 %) | 0.8814 | 1,515 | 15 | 21 | 0.00966 |
| character @12,451 **ON** | **2,773 (21.9 %)** | 0.9016 | 1,308 | 8 | 12 | 0.01025 |
| ccnb @9,451 off | 3,989 (44.5 %) | 0.8952 | 944 | 25 | 75 | 0.02200 |
| ccnb @9,451 **ON** | **1,944 (21.2 %)** | 0.9280 | 663 | 13 | 26 | 0.01752 |

**Irregularity roughly halves on both assets, and every other column improves with it.** Corpus:
11 rows better (torus@500 −13.8 pp, disc_open@500 −6.1 pp — which is also open item 3's case),
4 marginally worse, non-manifold 0 everywhere. `cylinder_capped@2000` worst-case deviation drops
**0.03325 → 0.00099**, retiring the one regression the hierarchy was recorded as costing.

**On ccnb this now matches or beats QuadRemesher**: 21.2 % irregular against its 25.7 %, 406
boundary edges against 1,274, 26 valence-1 vertices against 377. It is still behind on quad
fraction (0.928 against 0.953). On the *character* asset we remain 8× behind (21.9 % against
2.7 %) — which is what W1.5 is for.

**One thing had to be learned rather than assumed, and it is recorded in the file:** a midpoint
split does not simply halve the problem. Re-triangulating a split triangle creates a new edge from
the midpoint to the opposite vertex whose lattice difference is unconstrained, so on an
inconsistent field the refinement *cascades* — `cube_subdiv` splits [1513, 3625, 6731, …], rising
for three rounds, and ends worse (148 → 488 irregular, quad fraction 0.99 → 0.80). Hence the
divergence guard: if a round splits more than the last, or growth exceeds 1.5×, the pass abandons
and returns the input **untouched**, because half a subdivision is worse than none. `cube_subdiv`
now comes out bit-identical to off.

Original write-up follows, for the reasoning.

### W1.6 (original) — `subdivide_edgeDiff`: split unrepresentable edges instead of discarding them
**Size: S–M. Do this before W1.5 — it is cheap and it attacks the root of the whole defect chain.**

`PositionField.Classify` returns `Discard` when two vertices' integer indices differ by two or more
cells, or by the (1,1) diagonal. A discarded edge contributes **nothing** to the output graph — no
collapse, no edge. Measured: 1.6 % jump plus 8.2 % diagonal on the scan, 0.0 % plus 7.7 % on the torus.

QuadriFlow does not discard. Step 5 of `ComputeIndexMap` **subdivides the edge** until its difference
is representable. That one difference explains our entire downstream defect chain:

```
8,054 of 82,110 edges discarded  ->  holes in the graph
                                 ->  951 boundary edges SURVIVE tracing
                                 ->  hole filler adds 226 faces, valence-7 hubs, 195 new vertices
                                 ->  n-gon splitter turns 344 n-gons into 952 quads + 183 TRIANGLES
                                 ->  671 triangles, valence-1 vertices, 17-degree sliver quads
```

Every counter in that chain is a consequence of throwing edges away. Nothing downstream can recover
an edge that was never emitted, which is why the graph-repair work moved nothing.

Note `trace`'s `failed` counter is **not** part of this and must not be used as evidence: the tracer
attempts a walk from every directed slot, and successes exactly equal the face count
(22,725 − 18,496 = 4,229 = `faces`). It measures the search, not a defect.

- Files: `Runtime/PositionField.cs` (`Classify` gains a `Subdivide` outcome),
  `Runtime/Extraction/LatticeCollapse.cs`, new `Runtime/Extraction/EdgeSubdivide.cs`
- Do: where the difference exceeds one cell, split the input edge at the intermediate lattice
  point(s) and re-solve the two halves' indices, rather than dropping the edge. Note the diagonal
  case is **not** a defect and must keep being excluded as an output edge — but it should still
  participate in connectivity.
- **Done when:** `LatticeCollapse`'s `discardedEdges` reaches 0 for the jump class, and `holeFill`'s
  `boundaryEdges` drops by an order of magnitude from 951. Irregularity should already move here,
  before W1.5.

### W1.5 — `AdvancedExtractQuad`: cell-indexed extraction
**Size: M–L. With W1.6, this is the 44 % → 3 % work.**

Measured in `Benchmarks/quadremesher-measured.md` §2: on the corpus torus the orientation field has
**0 singularities**, the lattice has **0 torn edges**, integrability is 99.5 % satisfied — and the
collapse graph still arrives with **170 irregular vertices**, which the tracer, hole filler and n-gon
splitter grow to 283. A perfect 64 × 32 = 2,048-quad answer exists and is not found. On the scan the
same step turns a field asking for 429 singularities into a graph carrying 1,854.

The cause is structural, not a tuning problem. `LatticeCollapse.cs:154-178` classifies each input
edge **independently, in a frame built pairwise between its two endpoints**. Nothing in extraction
holds a global coordinate, so two clusters occupying the same lattice cell are merged only if some
input edge happens to say so directly. On the torus that fails 119 times, and `contested = 119`
against a valence-5 count of `115` is that failure counted directly. It is **under**-merging; the
over-merging hypothesis (the cluster-spread bound) was swept and refuted.

This is the documented difference between the two field-guided methods. Instant Meshes — what this
codebase ports — collapses edges pairwise, and its irregularity is a known property of that choice.
QuadriFlow assigns every vertex a global integer `(i, j)` per chart after the integer solve and reads
the mesh off the grid, so a cell *is* a face and a vertex has exactly four neighbours unless the field
is singular there.

- Do: build global `(i, j)` coordinates from the W1.3-repaired offsets by integration over the
  spanning tree `LatticeIntegration` already gauge-fixes, then emit one output vertex per occupied
  cell and one face per cell corner. Keep `LatticeCollapse` behind a flag until the gate below passes.
- Files: new `Runtime/Extraction/CellIndex.cs`, `Runtime/Extraction/LatticeCollapse.cs`,
  `Runtime/LatticeIntegration.cs` (read-only)
- **Done when — the gate, and the targets are measured rather than guessed** (`LatticeAudit.Run`;
  QuadriFlow's own numbers on these exact inputs are in `how-to-match-quadremesher.md` §1):

  | case | Euler floor | today | QuadriFlow | target |
  |---|---|---|---|---|
  | torus @2,000 | 0 | 283 | 24 | **≤ 30** |
  | icosphere @2,000 | 8 | 329 | 8 | **≤ 15** |
  | scan @5,474 | — | 2,278 | 162 | **≤ 250** |

  Plus, non-negotiable and currently failed: **100 % quads, zero triangles.** QuadriFlow is pure quad
  on all three. If the flow verifies at W1.3, W1.6 lands, and these do not move, the diagnosis is
  wrong and that gets written down before anything else is built.

### W1.7 — `FixFlipSat` and `FixValence`
**Size: M. The last two steps of `ComputeIndexMap`, and each maps onto a defect we already count.**

- **`FixFlipSat`** (step 7) removes flipped/degenerate cells with a small boolean-satisfiability
  solve per local neighbourhood, which is what makes QuadriFlow's output watertight *by
  construction*. We instead detect folds afterwards and repair them badly — `disagreeAfter` is
  23–151 faces depending on profile.
- **`FixValence`** (step 11) is a dedicated valence-repair pass. It is part of why QuadriFlow lands
  *on* the Euler floor rather than near it. We have no equivalent: `GraphCleanup` handles valence 1
  and 2 and lets a valence-13 vertex through.

- Files: new `Runtime/Extraction/FlipRepair.cs`, `Runtime/Extraction/ValenceRepair.cs`
- **Done when:** `disagreeAfter` is 0 on every profile, max output valence ≤ 7, and no valence-1 or
  valence-2 vertices reach the output.

---

## W2 — Feature and seam aligned constraints
**Size: M–L. Fixes a reported user-visible bug and closes the "structured loops" gap.**

Per `quadremesher-study.md` §2.3 and §3: QuadRemesher's largest documented feature is **guides** —
edge paths where the user wants an edge loop in the output, harvested from hard edges by angle,
material boundaries, and normal splits. In practice those sources *are* the UV seams.

This is a feature Instant Meshes supports and this port omitted, so it is filling a gap rather than
inventing something.

- **W2.1 — Detect constrained edges.** Dihedral angle above threshold; UV discontinuity; material
  boundary. Threshold guided by QuadRemesher's documented advice that hard-edge detection works for
  angles above ~30°.
- **W2.2 — Constrain the orientation field.** Align the 4-RoSy field to constrained edge directions
  during the Gauss-Seidel sweeps, propagated through the multiresolution hierarchy.
- **W2.3 — Constrain the position field.** Pin the lattice so a line falls *on* each constrained edge,
  not merely parallel to it.

- Files: `Runtime/OrientationField.cs`, `Runtime/PositionField.cs`, `Runtime/Hierarchy.cs`, new
  `Runtime/FeatureEdges.cs`
- **Done when:** on the character asset, edges detected as hard appear as edge loops in the output
  (a new measurable: fraction of constrained input edges with a coincident output edge), and
  irregularity does not regress.

**Risk:** the QuadRemesher doc warns explicitly that badly defined guides confuse the algorithm. Build
detection behind a flag, default off, and turn it on only once measured.

---

## W3 — Curvature-adaptive sizing — DONE (W3.1, W3.2)

> **Built and measured. See [Benchmarks/adaptive-sizing.md](Benchmarks/adaptive-sizing.md).**
> `Runtime/ScaleField.cs`, with per-side spacing threaded through `Compat`, `CompatIndex`,
> `PositionField.Solve`, `HierarchicalSolve`, `EdgeSubdivide` and `LatticeCollapse`.
> Settings chosen by sweep: adaptivity 0.25, clamp 2x, gradation 1.15, uniform on hard surface.
>
> **The "modest headroom" estimate below was wrong, and the reason it was wrong is instructive.**
> It rested on edge-length CV — a whole-mesh statistic — which cannot see that the variation is in
> the WRONG PLACES. The payoff turned out to be geometric, not statistical: the deepest
> unrepresented hole on the character (a thumb tip) went from 1.000 %% of the diagonal to 0.448 %%,
> and uncovered surface from 0.113 %% to 0.036 %%, both now better than QuadRemesher. It cost 0.9
> points of smoothness.
>
> Also refuted below: "W3 does not move irregularity" is true, and "W3 third because it does not
> touch the headline number" was true of the headline number we had. The headline number was
> missing coverage entirely.
>
> **W3.3** (`adaptiveQuadCount`) and **W3.4** (painted density override) remain unbuilt. W3.4 is
> still nearly free: `ScaleField` is already a plain per-vertex array with a `FromSpacings` factory.

**Size: M. Real gap, smaller than the study assumes; does not move irregularity.**

We carry one scalar target scale applied uniformly, so the same edge length is spent on a flat car
park as on a roof ridge. QuadRemesher's **Adaptive Size** (default 50 %) is the fix.

**Sizing note from the measurement.** `quadremesher-study.md` §2.1 calls uniform sizing "the single
largest gap in the list". Edge-length CV says otherwise: ours 0.244–0.314, QuadRemesher 0.262–0.396,
the reference retopology 0.458. We are close to QuadRemesher already — and some of our variation is
defects rather than deliberate adaptation, so the true adaptive headroom is real but modest. Worth
building; not worth building first.

- **W3.1** — per-vertex scale field seeded from principal curvature, smoothed, propagated through the
  existing hierarchy. Instant Meshes already carries a per-vertex scale, so the plumbing is mostly
  present.
- **W3.2** — `adaptiveSize` 0–100 % policy on top.
- **W3.3** — `adaptiveQuadCount` on/off: whether the target count or the shape wins.
- **W3.4** — per-vertex density override (QuadRemesher's vertex-colour paint). Nearly free once W3.1
  exists — same field, second contributor.

- Files: `Runtime/Hierarchy.cs`, `Runtime/RemeshSettings.cs`, new `Runtime/ScaleField.cs`
- **Done when:** Hausdorff error to the input drops at fixed face count on the photogrammetry asset,
  and `adaptiveSize=0` reproduces today's output within noise.

---

## W4 — UV seam vertex splitting
**Size: S. Independent of everything else. Required regardless.**

`AttributeTransfer` samples one UV per output vertex. A vertex on a seam has two valid UVs, picks one;
its neighbour picks the other; the quad between them interpolates across the whole atlas. That is the
grey band in the reported render.

A single vertex cannot represent a seam by definition, so no topology fixes this alone — but W2 makes
the split clean rather than a patch over a bad cut.

- Files: `Runtime/Extraction/AttributeTransfer.cs`, `Runtime/ToMesh.cs`
- Do: detect UV discontinuity across an output edge; duplicate the position, assign each copy its own
  UV.
- **Done when:** no output triangle has a UV area exceeding a small multiple of its neighbours', and
  the photogrammetry render shows no grey bands.

---

## W5 — Housekeeping
**Size: S. Not optional — two of these are actively misleading.**

- **Nothing has been committed this session.** Every improvement above is uncommitted working tree.
  Commit before starting W1.
- `Benchmarks/baseline.csv` is stale — 118 gated regressions are false.
- `gitSha` reports `"no-repo"`, so no benchmark run is attributable to a commit.
- ~~`disagreeAfter` rose 23→87, 43→88, 84→151 earlier this session and is **unattributed**.~~
  **Solved.** It compares our output's face normals against the *input's* vertex normals, and
  `MeshCorpus.Torus` and `MeshCorpus.CappedCylinder` are wound inside-out — `Build` calls
  `RecalculateNormals`, so an inverted winding becomes inverted vertex normals for the whole mesh. On
  the torus the counter reads 1,908 of 1,910 faces while `inconsistentEdges` stays 0 and the
  signed-volume test correctly reports no flip needed. `FaceOrienter` is behaving correctly; the
  fixture is not. See `quadremesher-measured.md` §5.
  - **Do:** reverse the winding in both generators, and add a test asserting positive signed volume
    for every closed corpus generator. `normalConsist` and `normalFlipped` have been meaningless on
    those two rows for the whole project, so the baseline must be regenerated after the fix.
  - The **solvers are unaffected** — a 4-RoSy cross is invariant under flipping the normal, and in
    the position field flipping `n` negates the lattice's second axis globally, which is a relabel.
    Smoothness energy 0.999 on the torus confirms it. Do not go looking for a solver bug here.
- **`FieldReport.indexSum` reads 0 on `cube_subdiv`, where Euler's formula requires 8.** The field's
  own doc-comment says this number is the one thing that can validate the singularity detector, and
  that a value other than 4χ means the detector is wrong. The icosphere reads 8 correctly, so it is
  not a blanket failure. Unexplained.
- Stale doc references in `HANDOVER.md` to a stage-4 diagnosis now known wrong.

---

## Ordering and rationale

```
W5 (commit)  →  W0  →  W1.6     subdivide instead of discard   ← DO THIS FIRST
                          ↓
                        W1.1 → W1.2 → W1.3     the flow, correct and applied
                                        ↓
                                      W1.5     cell-indexed extraction
                                        ↓
                                      W1.7     FixFlipSat + FixValence
                                        ↓
                                      W4       (independent, any time)
                                        ↓
                                      W2  →  W3
```

**W1.6 moved to the front.** It was behind the flow work; it does not depend on it. Subdividing an
edge whose integer difference spans two or more cells is a local decision and is correct whether or
not the offsets are curl-free.

The reason it goes first is that **our gap is two gaps stacked, and this one is the larger and the
cheaper.** Published averages put Instant Meshes — the method this codebase already ports — at
200–400 singularities. We are at **2,278**, with 671 triangles, a valence-13 vertex and 12 valence-1
vertices, none of which Instant Meshes produces. So roughly 6–10× of our gap is our own port being
broken, and only 2–3× is the architectural step from Instant Meshes to QuadriFlow.

The proof is internal: on the torus our field is *perfect* (0 singularities, index sum 0) and the
lattice has 0 torn edges, and extraction still emits 283 irregular vertices and 84 triangles. There
is nothing else left to blame.

**W1.1–W1.3 first** because the machinery is built and measured, and because W1.5 cannot exist
without it: global `(i, j)` coordinates only integrate if the system is curl-free.

**W1.5 is the step that moves the headline number.** It is gated on the torus and icosphere, where
the answer is known from Euler's formula rather than from a reference file — the first quality gate in
this project that can distinguish "better" from "best possible".

**W2 second** because it fixes a user-reported bug and is the largest documented competitor feature.
**W3 third** because it changes how the mesh looks and feels but does not touch the headline number.

**Not recommended: going layout/patch-based.** `quadremesher-study.md` §5 is right that it is a
rewrite rather than an improvement. W1.5 is a change to one extraction stage, and it takes the
field-guided route to the same place — which is what the QuadriFlow numbers say it should.

## Standing rules for this work

1. **Measure before and after, on all three profiles.** Every claim in `Benchmarks/README.md` is a
   measurement; none is an opinion.
2. **Do not verify integer-lattice work by looking at the mesh.** W1.3 in particular has a failure mode
   that looks fine and scores slightly worse.
3. **A test that fires on nothing is not a test.** The `k_MinVoteConfidence = 0.15` constant in
   `FaceOrienter` currently fires on zero components across all three profiles; it is marked
   PROVISIONAL for that reason and should either be re-derived from data or removed.
4. **New behaviour goes behind a flag, default off, until measured.**
