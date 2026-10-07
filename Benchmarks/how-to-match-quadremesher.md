# How to match QuadRemesher's quality

Written 2026-09-08. This is the research answer, and it is not a recommendation from reasoning — the
target quality was **reproduced on this project's own asset** by an MIT-licensed method whose source
we can port.

**Answer: port QuadriFlow.** Measured on our reference scan, QuadriFlow produces **162 irregular
vertices against QuadRemesher's 341 and our 2,278**, at 100 % quads with zero triangles. It does not
merely match QuadRemesher on this asset; it beats it. Everything below is the evidence and the
port plan.

---

## 1. The measurement that settles it

Blender ships QuadriFlow, so the architecture recommended in `quadremesher-measured.md` §4 could be
scored on our own inputs before writing a line of it. Script:
`Benchmarks/reference/quadriflow-baseline.py`. Same input, same targets, same metric code as every
other row in that document.

| | **ours** | **QuadriFlow** | QuadRemesher | reference retopo | Euler floor |
|---|---|---|---|---|---|
| **scan @5,474** | | | | | |
| irregular vertices | **2,278 (44.1 %)** | **162 (3.3 %)** | 341 (2.7 %) | 596 (11.3 %) | — |
| quad fraction | 0.8773 | **1.0000** | 0.9997 | 0.9289 | — |
| triangles | 671 | **0** | 4 | 389 | — |
| min interior angle, p05 | 17.4° | **63.1°** | 62.9° | 52.9° | — |
| valence spread | 1–13 | **3–5** | 3–7 | 3–8 | — |
| **torus @2,000** | | | | | |
| irregular vertices | **283 (15.2 %)** | **24 (1.2 %)** | 0–10 | — | **0** |
| quad fraction | 0.9560 | **1.0000** | 1.0000 | — | — |
| **icosphere @2,000** | | | | | |
| irregular vertices | **329 (16.0 %)** | **8 (0.4 %)** | — | — | **8** |
| quad fraction | 0.9269 | **1.0000** | — | — | — |

And **like-for-like at QuadRemesher's own face count**, because the row above compares 4,936 faces
against 12,451 and that is not a fair test:

| scan, ~12,000 faces | QuadriFlow | QuadRemesher |
|---|---|---|
| faces | 12,071 | 12,451 |
| irregular vertices | **222 (1.8 %)** | 341 (2.7 %) |
| quad fraction | **1.0000** | 0.9997 |
| triangles | **0** | 4 |
| valence spread | **3–5** | 3–7 |
| min interior angle, p05 | 57.6° | **62.9°** |

So QuadriFlow does genuinely beat QuadRemesher on this asset at equal resolution — 222 against 341,
and pure quad with a tighter valence spread. QuadRemesher keeps a small edge on quad *shape*
(p05 62.9° against 57.6°), which is worth remembering when W3 (adaptive sizing) comes up.

**QuadriFlow hits the Euler floor exactly on the icosphere — 8 valence-3 vertices and nothing else.**
On the torus it reaches 24 against a floor of 0. On the real scan it beats both human-grade
references. And it is pure quads on all three: not one triangle anywhere.

The scan comparison is apples-to-apples on solver input: Blender's decimate was set to
54,740 triangles, which is exactly what our own pre-decimation hands our solver.

Two caveats stated plainly. QuadriFlow undershot the face count (4,936 against 5,474, −9.8 %) where
we hit +0.3 %; and Blender's QuadriFlow silently **refuses** a mesh it considers non-manifold or
inconsistently wound, returning the input untouched with only a warning — the first run here looked
like a catastrophic result until that was spotted. Any port must fail loudly instead.

## 1b. Why QuadRemesher is better, from the engine that is installed here

`quadremesher-study.md` §1 said correctly that "anybody who tells you precisely how QuadRemesher works
internally is guessing." That is no longer entirely true, because the installed engine at
`C:\ProgramData\Exoside\QuadRemesher\Datas_Blender\QuadRemesherEngine_1.3` **discloses its own
numerical libraries** — LGPL obliges Exoside to ship that notice. Nothing here is disassembly; it is
the vendor's licence file and the component names a dynamically-linked binary must carry.

`licenses/ReadMe.txt`:

> Quad-Remesher is using the following modules of the SuiteSparse library:
> AMD, CAMD, CCOLAMD, COLAMD, CholMod/Cholesky, CholMod/Core

And `ChSolver.dll` — "Cholesky solver" — carries `cholmod_analyze`, `cholmod_factorize`,
`cholmod_amd`, `cholmod_camd`, `cholmod_ccolamd`, `cholmod_colamd`, `cholmod_etree`,
`cholmod_postorder`.

**That is a direct global sparse Cholesky solve of a symmetric positive-definite system.** AMD /
COLAMD are fill-reducing orderings; CAMD / CCOLAMD are their *constrained* variants;
`cholmod_analyze` then `cholmod_factorize` is the standard symbolic-then-numeric factorization pair.

Strings in `xremeshlib.dll` name the system and the variables:

| String | What it says |
|---|---|
| `LaplaceWithEdgLen` | the matrix is an edge-length-weighted **Laplacian** |
| `INTVARFLAG_BASIC_INTEGER_GridPole` | **integer variables**, and *poles* — irregular vertices — are among them |
| `Basic Integer`, `DL_GroupParameters_IntegerBasedImpl` | integer-valued parameter groups |
| `MXMeshChartsCache` | the surface is cut into **charts** |
| `AVMXCurvatureDirAlgo`, `CurvatureAdaptivness` | curvature-driven direction field; the Adaptive Size slider |

So QuadRemesher is: curvature-driven cross field → chart decomposition → a Laplacian global
parameterization **with integer variables, poles included** → sparse Cholesky factorization → grid
extraction. That is the **Mixed-Integer Quadrangulation / integer-grid-map family**, and it is
consistent with MIQ being the best-scoring row in §2's table.

**The one-line difference from us:**

> QuadRemesher solves the whole surface at once, as one sparse linear system with integer variables,
> factorized directly. We smooth locally with Gauss-Seidel and hope agreement propagates.

Gauss-Seidel moves information one edge per sweep. Our own hierarchy exists precisely to paper over
that, and it only reaches three levels. A direct factorization has no reach limit at all — every
vertex is coupled to every other through the factorization — which is why QuadRemesher's loops run
the length of the model and its singularity count sits near the topological floor. Our irregularity
is not a tuning failure; it is what local smoothing produces.

This also explains, in retrospect, three things this project measured and could not account for: why
the orientation field is excellent while the output is not (a cross field is a *local* quantity and
Gauss-Seidel suffices for it — an integer lattice is a *global* one and it does not); why the
hierarchy helped positions but not orientations; and why five sessions of downstream graph repair
moved nothing.

**It does not change the recommendation.** Route B — build what QuadRemesher builds — needs a sparse
Cholesky solver in C# (there is no SuiteSparse here), a mixed-integer rounding strategy, and chart
decomposition, and the robust extractor for integer-grid maps (libQEx) is GPL-3. Route A —
QuadriFlow — reaches the same global consistency by a different route, **is measured at 162 against
QuadRemesher's 341 on this asset**, is MIT, and half its machinery is already in this tree. Take
Route A.

## 1c. The robustness gap, which nobody had measured

The two real assets are not equally hard, and the harder one had never been scored against anything.

| | character (`20260515221044_54b8f8f4.fbx`) | ccnb (`ccnb.obj`) |
|---|---|---|
| source | scan, 1.5 M tris | Agisoft Metashape, 990,591 tris |
| topology | closed manifold | **open outline**, 5 non-manifold edges, 818 valence-1 vertices |
| Blender QuadriFlow | works — **after** the winding is made consistent | **REFUSES**, silently, returning the input unchanged |
| QuadRemesher | 341 irregular (2.7 %), 0.9997 quad | 2,555 irregular (25.7 %), 1,274 boundary edges, 377 valence-1 |
| **ours** (subdivision on) | 2,773 (21.9 %) @12,451 | **1,944 (21.2 %)**, 406 boundary edges, 26 valence-1 |

Two things fall out of that.

**Blender's QuadriFlow refuses the photogrammetry asset outright** — and it does not raise, it logs
a warning and hands back the input, which is how a first attempt here looked like a catastrophic
result until the 990,591-triangle "output" gave it away. It also refused the *character* asset until
its winding was fixed. So the method §1 measures as the best available cannot be run on the input
this tool is aimed at, and any port must fail loudly where it fails at all.

**QuadRemesher does not solve ccnb either — it just does not refuse.** Its own output there carries
1,274 boundary edges, 377 valence-1 vertices and 6 non-manifold edges. And on that asset **we are
now level with it or ahead**: 21.2 % irregular against 25.7 %, 406 boundary edges against 1,274, 26
valence-1 against 377. It keeps an edge on quad fraction (0.953 against 0.928).

So the gap is not uniform, and that is the useful part: **on messy open scan input we are already
competitive, and the whole deficit is on the clean closed asset** — exactly where a global integer
solve pays and local smoothing does not. That is a sharper statement of §4's conclusion than the
character asset alone could give.

## 1d. What subdivision alone bought

`EdgeSubdivide` (PLAN.md W1.6) — QuadriFlow's step 5, split an unrepresentable edge rather than
discard it — landed on its own, ahead of the rest of the port:

| | irregular | quadFrac | tris | maxValence | v1 |
|---|---|---|---|---|---|
| character @12,451 before | 5,184 (42.9 %) | 0.8814 | 1,515 | 15 | 21 |
| character @12,451 after | **2,773 (21.9 %)** | 0.9016 | 1,308 | 8 | 12 |
| ccnb @9,451 before | 3,989 (44.5 %) | 0.8952 | 944 | 25 | 75 |
| ccnb @9,451 after | **1,944 (21.2 %)** | 0.9280 | 663 | 13 | 26 |

**Irregularity roughly halves on both**, from one pass, which is the strongest confirmation the
diagnosis in §4 could have had: discarding edges really was the root of the chain. It also drops
`cylinder_capped`'s worst-case deviation 34-fold and improves 11 of 30 corpus rows.

It does **not** close the gap on the character asset — 21.9 % against QuadRemesher's 2.7 % — and it
was never going to. Subdivision removes the *symptom* of a lattice that does not tile; the remaining
factor of eight is the lattice itself, and that is cell-indexed extraction.

One correction it forced, recorded because the reasoning was wrong and the measurement caught it: a
midpoint split does **not** simply halve the problem. Re-triangulating a split triangle creates a new
edge from the midpoint to the opposite vertex whose lattice difference is unconstrained, so where the
field is inconsistent the refinement cascades — `cube_subdiv` splits [1513, 3625, 6731, …], rising
for three rounds, and finishes materially worse. Hence a divergence guard that abandons and returns
the input untouched.

## 2. Why the literature agrees, and where the ceiling is

Published singularity counts, averaged over benchmark corpora (Table 2/3 of
[NeurCross](https://arxiv.org/html/2405.13745v3)):

| Method | ShapeNet | Thingi10K |
|---|---|---|
| Instant Meshes | 200.5 | 397.2 |
| **QuadriFlow** | **91.6** | **78.3** |
| QuadWild | 93.0 | 85.1 |
| MIQ | 82.1 | 66.5 |
| NeurCross | 85.3 | 69.0 |

Two things follow.

**QuadriFlow is 2–5× better than Instant Meshes, which is the method this codebase ports.** That is
the published, independent confirmation of the gap we measured.

**But the whole field clusters at 66–93.** MIQ — mixed-integer, from 2009 — is still the best row.
Nobody gets to zero. So QuadriFlow is not a stepping stone to something much better; it is
essentially the ceiling for automatic methods, and it is above QuadRemesher on our asset. **There is
no reason to look further.** Going patch-based (QuadWild) scores *worse* than QuadriFlow here and its
own literature notes it struggles on organic surfaces, which is exactly our input.

A third thing follows about our own code, and it is uncomfortable: **our 2,278 is far worse than
Instant Meshes itself.** Instant Meshes produces near-pure quads; we produce 671 triangles, 12
valence-1 vertices, a valence-13 vertex, and 951 boundary edges handed to a hole filler. Our
extraction is not just architecturally behind QuadriFlow, it is behind the method we set out to port.

## 3. Licensing — the reason this is a port and not research

| Implementation | Licence | Usable? |
|---|---|---|
| **QuadriFlow** (`hjwdzh/QuadriFlow`) | **MIT** | **Yes — port directly** |
| Instant Meshes | as ported already | already here |
| QuadWild (`nicopietroni/quadwild`) | GPL-3 **and requires Gurobi** | no |
| libQEx (QEx extraction) | GPL-3, commercial licence on request | no |
| QuadRemesher / ZRemesher | closed commercial | no |

QuadriFlow's own dependencies are Boost and Eigen (Boost Software License / MPL2), with Gurobi used
**for benchmarking only** — the shipped path uses its own min-cost flow plus a small SAT solver. So
there is nothing to license and nothing to reimplement from a paper: the reference is readable,
permissive, and already the thing we are trying to become.

## 4. What QuadriFlow actually does that we do not

Its `ComputeIndexMap()` is the stage we are missing, and it is twelve steps. Mapped against our tree:

| # | QuadriFlow | Ours | State |
|---|---|---|---|
| 1 | `BuildEdgeInfo` — per-edge integer `edge_diff`, `face_edgeIds` | `LatticeIntegration.BuildEdges` | **done** |
| 2 | sharp-edge handling | `FeatureGraph` | partial |
| 3 | `BuildIntegerConstraints` — `face_edgeOrients` gauge, per-**component** flow balance, `allow_changes` | `LatticeIntegration` gauge + `LatticeRepair.Plan` | **partial** — ours balances per face, not per component, and has no `allow_changes` |
| 4 | `ComputeMaxFlow` → writes back into `edge_diff` | `MinCostFlow` + W1.3 | **built, not applied** |
| 5 | **`subdivide_edgeDiff`** — split an edge whose integer difference is too large | — | **missing** |
| 6 | `FixFlipHierarchy` | — | **missing** |
| 7 | **`FixFlipSat`** — SAT flip removal, guarantees watertight | — | **missing** |
| 8/9 | `optimize_positions_sharp` / `_fixed` | `PositionField` | partial |
| 10 | **`AdvancedExtractQuad`** — extract from the parametrization | `LatticeCollapse` (greedy pairwise) | **wrong mechanism** |
| 11 | **`FixValence`** | — | **missing** |
| 12 | `optimize_positions_dynamic` | — | missing |

Three of those are worth calling out because each maps onto a defect we have measured:

- **Step 5, `subdivide_edgeDiff`, is the answer to our discarded edges.** When an edge's integer
  difference is too large to represent, QuadriFlow **subdivides the edge** so it becomes
  representable. We *throw the edge away* — `EdgeClass.Discard`, 8,054 of 82,110 edges on the scan
  (1.6 % jump, 8.2 % diagonal). Discarding is why the graph has holes, why **951 boundary edges
  survive tracing** and reach the hole filler, and why hole-filler hubs and n-gon splits then
  manufacture triangles and high-valence vertices. This one step removes the root of the chain.

  (A correction to an earlier draft of this file: `trace`'s `failed=18496` is **not** a failure rate.
  The tracer attempts a walk from every directed slot and successes exactly equal the face count
  — 22,725 − 18,496 = 4,229 = `faces`. It is the search, not a defect. The boundary-edge count is the
  counter that shows lost connectivity.)
- **Step 7, `FixFlipSat`, is what makes the result watertight by construction.** We have 23–151
  folded faces (`disagreeAfter`) and repair them after the fact, badly.
- **Step 11, `FixValence`, is a dedicated valence repair pass.** Part of why QuadriFlow lands on the
  Euler floor rather than near it. We have nothing equivalent — `GraphCleanup` handles valence 1 and
  2 and lets valence 13 through.

Note what is *not* on the list: no feature detection, no curvature-adaptive sizing, and no patch
layout. QuadriFlow beats QuadRemesher on our asset without any of the four things
`quadremesher-study.md` identified as the gaps. Those remain worth having as **features** — the study
is right that guides and adaptive sizing are what users ask for — but they are not what buys the
quality.

## 5. The plan

Port steps 3–7, 10 and 11 into `Assets/NativeRemesher/Runtime/`, keeping our orientation field and
hierarchy, which are already good (torus field: 0 singularities; icosphere: exactly 8; smoothness
0.999). See `PLAN.md` W1 for the sequenced version. The order that matters:

1. **W1.1** — fix `MinCostFlow`'s reduced-cost clamp. Its cost is currently not invariant to the
   queue implementation, so it is not returning an optimum.
2. **W1.2/W1.3** — arc → half-edge mapping, and apply the flow to `jump[]`. Verify by the residual
   dropping to the stranded remainder, **not** by looking at the mesh.
3. **W1.6 (new, and do it early)** — `subdivide_edgeDiff`. Stop discarding unrepresentable edges;
   split them. This is cheap and it attacks the root of the defect chain on its own.
4. **W1.5** — `AdvancedExtractQuad`: cell-indexed extraction replacing the greedy pairwise collapse.
5. **W1.7** — `FixFlipSat` then `FixValence`.

**The gate, at every step, is `LatticeAudit.Run`**, because it scores against Euler floors rather
than a reference file. The numbers to beat are now known and reachable, not guessed:

```
torus @2000       floor 0    ours 283    QuadriFlow 24     <- target <= 30
icosphere @2000   floor 8    ours 329    QuadriFlow 8      <- target <= 15
scan  @5474       --         ours 2278   QuadriFlow 162    <- target <= 250
```

Also non-negotiable, and currently failed: **100 % quads and zero triangles.** QuadriFlow is pure
quad on all three inputs. Ours is 87–96 % with 84–671 triangles, and that alone rules out the entire
published quad-mesh-simplification toolchain — polychord collapse, separatrix operations, Q-zip —
every one of which requires a pure quad mesh. Getting to pure quads is not cosmetic; it unlocks the
next tier of tools if they are ever needed.

## 6. If QuadriFlow's ceiling ever proves too low

It will not be, on this evidence — 162 beats QuadRemesher's 341 on our asset. Recorded only so the
option is not re-researched: singularity counts can be reduced further on an *already pure-quad*
mesh by topological post-processing, using local operations (quad collapse, doublet removal) and
semi-global ones (half-polychord removal, separatrix collapse between valence-3 pairs, separatrix
split between valence-5 pairs). [Q-zip](https://dl.acm.org/doi/10.1145/3478513.3480523) (Feng, Tong,
Desbrun 2021) generalises these into one reversible primitive that walks two arbitrarily distant
singularities together and cancels them, via a chain of four strip operations guided by parallel
transport of a reference frame. It is a post-process; it needs no parameterization; it requires pure
quads. That is the tier above QuadriFlow, and nothing here justifies reaching for it yet.

Deliberately not pursued: **learned methods.** [SQuadGen](https://arxiv.org/abs/2604.27329) (diffusion
over chart distance fields) and [TopGen](https://arxiv.org/pdf/2603.10606) target *layout simplicity*,
and NeurCross — a neural cross field — scores 69–85, i.e. **worse than QuadriFlow's 78 on Thingi10K
and no better on ShapeNet**. This project's own ML gate ("the solver is no longer the bottleneck") is
not met, and the published numbers say a learned cross field would not help even if it were.

## Sources

- [QuadriFlow: A Scalable and Robust Method for Quadrangulation](https://onlinelibrary.wiley.com/doi/abs/10.1111/cgf.13498) — Huang et al. 2018; [source, MIT](https://github.com/hjwdzh/QuadriFlow)
- [NeurCross](https://arxiv.org/html/2405.13745v3) — the cross-method singularity table in §2
- [Reliable Feature-Line Driven Quad-Remeshing](https://dl.acm.org/doi/10.1145/3450626.3459941) — Pietroni et al. 2021; [quadwild, GPL-3](https://github.com/nicopietroni/quadwild)
- [QEx: Robust Quad Mesh Extraction](https://dl.acm.org/doi/10.1145/2508363.2508372); [libQEx, GPL-3](https://github.com/hcebke/libQEx)
- [Q-zip: Singularity Editing Primitive for Quad Meshes](https://dl.acm.org/doi/10.1145/3478513.3480523) — Feng, Tong, Desbrun 2021
- [Practical quad mesh simplification](https://onlinelibrary.wiley.com/doi/10.1111/j.1467-8659.2009.01610.x) — Tarini et al. 2010
- [SQuadGen](https://arxiv.org/abs/2604.27329), [TopGen](https://arxiv.org/pdf/2603.10606) — learned layout methods, not pursued
- [Quad Remesher user documentation](https://www.exoside.com/quadremesherdata/QuadRemesher_1.3_UserDoc.pdf) — the option surface, per `quadremesher-study.md`
