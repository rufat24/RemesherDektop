# QuadRemesher, measured — and where our singularities actually come from

Written 2026-09-08. The empirical counterpart to `quadremesher-study.md`, which was written from
QuadRemesher's **user documentation** without ever measuring its output. Its output is now measured,
on the same input as ours, and the numbers **contradict that study's priority ranking** and
`PLAN.md`'s justification for W1. Both corrections are recorded here with the evidence.

Nothing from QuadRemesher is used, linked, bundled or copied. Its output FBX was scored as a black
box, the way any competitor's result can be.

---

## 1. The four-way comparison

All four meshes describe the same asset: `Assets/ccnb/20260515221044_54b8f8f4.fbx`, a 1.5M-triangle
scan. Scored with Blender's Python (Unity's importer defaults to `keepQuads: 0` and triangulates, so
quad topology cannot be measured through it).

| | QuadRemesher | QuadRemesher | reference retopo | **ours** (Photo) |
|---|---|---|---|---|
| faces | 65,926 | 12,451 | 5,474 | 5,493 |
| quad fraction | **0.9999** | **0.9997** | 0.9289 | 0.8773 |
| triangles | 4 | 4 | 389 | 671 |
| n-gons | 0 | 0 | 0 | 3 |
| **irregular vertices** | **479 (0.7 %)** | **341 (2.7 %)** | 596 (11.3 %) | **2,278 (44.1 %)** |
| max valence | 7 | 7 | 8 | 13 |
| valence-1 / valence-2 | 0 / 3 | 0 / 0 | 1 / 0 | **12 / 49** |
| non-manifold edges | 2 | 0 | 0 | 1 |
| min interior angle, p05 | 69.4° | 62.9° | 52.9° | **17.4°** |
| edge-length CV | 0.262 | 0.396 | 0.458 | 0.244 |

`Character` reads 44.5 %, `GameAsset` 48.6 %. Full rows in `Headless/audit*.log` and the run above.

**Read the singularity count, not the fraction.** Singularity count is a topological quantity and
barely moves with resolution: QuadRemesher lands on 341–479 at both 12k and 66k faces. So the honest
comparison is 341–479 against our **2,278** — five times as many singularities in a mesh with a
twelfth of the vertices.

Three things worth naming beyond the headline:

- **The reference retopology is not the ceiling.** It sits at 11.3 %; QuadRemesher reaches 2.7 % at
  comparable resolution. Every document in this repo treats 11.3 % as the target. It is a floor on
  ambition, not a ceiling.
- **Our quads are slivers at the tail.** p05 min-angle 17.4° against 63–69°. That is a visible defect
  the irregularity number does not carry, and nothing in the harness gates on it.
- **Valence-1 and valence-2 vertices reach our output** (12 and 49). A valence-1 vertex is touched by
  one face corner; it is not a quality issue but a broken one. `README.md` open item 4 records 173
  valence-2 vertices; it does not record that valence-1 exists.

One place we are ahead: the QuadRemesher FBX carries **no UV layer at all** (`uvLayers=[]` on every
output). Whether that is the engine or the bridge's round-trip, UV transfer is ours to own, and
`quadremesher-study.md` §3 is right that it needs vertex splitting regardless.

## 2. The torus, which has no alibi

The comparison above cannot say *which stage* loses the structure, because nobody knows the best
possible answer on a scan. So the same measurements were taken on four closed analytic surfaces
whose minimum irregular-vertex count follows from Euler's formula alone (`LatticeAudit.cs`).

The torus is the row that matters. χ = 0, so the index sum is 0 and **a defect-free answer exists**.
It has no features to detect, no noise, no boundary, and no curvature variation worth adapting to.
Better still, the corpus torus is 128 × 64, so taking every other vertex in each direction gives a
perfect 64 × 32 grid — **2,048 quads, zero irregular vertices** — against a request for 2,000.

Here is the whole pipeline on it, GameAsset:

| stage | measurement | verdict |
|---|---|---|
| best possible | 0 irregular vertices | — |
| orientation field | **0 singularities**, index sum 0, smoothness 0.999388 | perfect |
| lattice, per edge | **jump discards 0.0000**, diagonal 0.0770, mismatch 0.0599 | untorn |
| integrability | 99.49 % of faces satisfied, defect **42 units**, 100 % routable | near-perfect |
| **collapse graph** | 1,810 clusters, valence [2:2 **3:51** 4:1640 **5:115** 6:2] → **170 irregular** | **the cliff** |
| face tracer | 3,662 walks, **1,889 failed (52 %)**, 138 boundary edges left | +hubs |
| hole filler | 23 fans added, hub star up to 6 | +singularities |
| n-gon splitter | 35 pentagons → 70 quads + **35 triangles** | +triangles |
| output | 1,910 faces, 96.0 % quads, 84 tris, **283 irregular** | 283 vs 0 |

The scan tells the same story with bigger numbers:

| stage | torus | scan (Photogrammetry) |
|---|---|---|
| field singularities | 0 | 429 (0.78 %) |
| jump discards | 0.00 % | 1.6 % |
| integrability satisfied | 99.49 % | 99.0 % |
| total integrability defect | 42 units | 249 units (235 routable) |
| **collapse graph irregular** | **170 / 1,810 = 9.4 %** | **1,854 / 4,717 = 39.3 %** |
| failed trace walks | 52 % | 81 % |
| output irregular | 283 | 2,278 |

**The field is not the problem, and it is better than the target.** Our field asks for 429
singularities on the scan; the reference retopology *has* 596. On the torus it asks for exactly 0, on
the icosphere exactly 8 — the theoretical floor in both cases. Every instrument that measures the
field agrees it is doing its job.

**The cliff is between the lattice and the graph.** On the torus a perfect field and an untorn
lattice become a graph with 170 irregular vertices. On the scan, 429 becomes 1,854. Extraction then
adds another 20–25 % on top through failed walks, hole-filler hubs and n-gon splitting.

## 3. What this refutes

### `PLAN.md` W1 is not the 44 % → 11.3 % work

W1 says it "closes the largest share of the 44 % → 11.3 % gap". It cannot, and its own measurements
say so. The entire integrability defect is **249 units on the scan and 42 on the torus**. The damage
at the collapse stage is **1,425 vertices on the scan and 170 on the torus**. Even a perfect repair
of every routable unit leaves the great majority of the gap untouched. `README.md` already bounds
this correctly — "about 1 % of edge classifications" — so the two documents contradict each other and
`README.md` is the one that is right.

**W1 keeps its place in the order, for a completely different reason.** See §4.

### The study's ranking is wrong on its top two items

`quadremesher-study.md` §4 ranks feature/seam constraints first and curvature-adaptive sizing second.
Neither can explain a torus. A torus has no features to align to and no curvature variation to adapt
to, and we produce 283 singularities on it where 0 are available. Both items are real gaps — they
are QuadRemesher's actual documented feature surface, and W4's UV splitting is required regardless —
but neither is the cause of the headline number, and doing them first would leave it where it is.

The adaptive-sizing gap is also smaller than the study assumes. Measured edge-length CV: ours
0.244–0.314, QuadRemesher 0.262–0.396, the reference retopology 0.458. We are not far off
QuadRemesher, and part of our variation is defects rather than deliberate adaptation — but the claim
that uniform sizing is "the single largest gap in the list" is not supported.

### Refuted: over-merging by the cluster-spread bound

`LatticeCollapse.defaultMaxClusterSpread = 2.25` lets a cluster span more than two lattice cells, so
a single output vertex can be glued out of two or three cells' worth of surface and end up with
neighbours in more than four lattice directions. That is a clean story for the contested/vacant pair,
and it is wrong.

Swept 2.25 → 0.75 at face count held fixed by the pipeline's spacing probe (`clusterSpread` is now a
profile field so this is repeatable):

```
torus,2.25 .. torus,1.0   ->  283 irregular, 1910 faces, rejLarge=0   (bit-identical)
torus,0.75                ->  238 irregular, 1896 faces, rejLarge=0
cylinder_capped,0.75      ->   89 irregular  (WORSE than 75 at 2.25)
```

The bound never fires down to 1.0 cell. Clusters reach 12 members but stay inside one cell, so the
collapse is not over-merging spatially, and tightening the bound is not the fix.

The mechanism is the opposite one. On the torus `contested = 119` and the valence-5 count is `115` —
the graph's dominant defect is two distinct neighbours landing in the **same** lattice direction,
which means two clusters occupying one cell that were never merged. That is **under-merging**, and it
happens because `LatticeCollapse` (`LatticeCollapse.cs:154-178`) classifies every input edge
**independently, in a frame built pairwise between its two endpoints**. There is no global
coordinate anywhere in extraction, so two clusters in the same cell are merged only if some input
edge happens to say so directly, and the partition is never made globally consistent.

## 4. The direction

Not a rewrite, and not the layout school. `quadremesher-study.md` §5 is right that going
patch-based would be a rewrite rather than an improvement. The change is narrower than that and it is
inside `Runtime/Extraction/`.

**Stop extracting by greedily collapsing input edges. Extract from the integer cell coordinates we
already compute.**

This is the concrete difference between the two published field-guided methods, and it lines up
exactly with what we measure:

- **Instant Meshes** (which this codebase ports) collapses input edges pairwise into clusters. Its
  output is markedly more irregular than QuadriFlow's — this is a known property of the method, and
  44 % is what it looks like.
- **QuadriFlow** solves the integer system, then assigns every vertex a global integer coordinate
  `(i, j)` per chart and reads the quad mesh off the grid. A cell *is* a face by construction, and a
  vertex has exactly four neighbours unless the field is singular there. Irregularity can then only
  come from the field's own singularities — which we measure at 0 on the torus, 8 on the icosphere,
  429 on the scan.

That reframes W1 completely, and this is the useful part:

> **The min-cost flow is not a way to reduce irregularity. It is the prerequisite for cell-indexed
> extraction.** Integer offsets can only be integrated into consistent global `(i, j)` coordinates
> if the system is curl-free, which is exactly what W1 repairs. That is why QuadriFlow needs it, and
> it is why W1 stays first — but as an enabler whose payoff arrives only with the extraction change,
> not as a fix that moves the number on its own.

So the order becomes:

```
W5 (commit)  →  W0  →  W1.1 → W1.2 → W1.3   [the flow, correct and applied]
                                        ↓
                              W1.5  cell-indexed extraction   ← the 44% work
                                        ↓
                              W4 (UV splitting, independent)
                                        ↓
                              W2 (features/seams)  →  W3 (adaptive sizing)
```

W1.4 as written — "have `LatticeCollapse` read the repaired offsets" — is too small. Reading repaired
offsets into the same per-edge greedy collapse keeps the mechanism that loses the structure. The step
is to build global coordinates from the repaired offsets and extract from them.

**It is falsifiable on the torus, and cheaply.** Cell-indexed extraction on the corpus torus must
produce close to 0 irregular vertices, because the field has 0 singularities and the lattice has 0
torn edges. If it does not, this diagnosis is wrong and that belongs in writing before anything else
is built. The icosphere must land near 8. Those two numbers are the gate.

## 5. Two bugs found on the way

**`MeshCorpus.Torus` and `MeshCorpus.CappedCylinder` are wound inside-out.**

```
torus:           signedVolume=-2.398688   <-- INSIDE OUT
icosphere:       signedVolume= 4.152741   ok
bumpy_sphere:    signedVolume= 4.156049   ok
cylinder_capped: signedVolume=-1.560723   <-- INSIDE OUT
cube_subdiv:     signedVolume= 1.000000   ok
```

`Build` calls `RecalculateNormals`, so an inverted winding silently becomes inverted vertex normals
for the whole mesh. Consequences:

- **This is `PLAN.md` W5's unattributed `disagreeAfter`.** On the torus it reads **1,908 of 1,910**
  faces while `inconsistentEdges` stays 0 and the signed-volume test reports no flip needed. All
  three are consistent once you know the counter compares our output's face normals against the
  *input's* vertex normals, and the input's are backwards. `FaceOrienter` is behaving correctly and
  the output is correctly outward.
- `normalConsist` and `normalFlipped` have been meaningless on the torus and cylinder rows for the
  whole project.
- The **solvers are unaffected**, and this should not be over-read. A 4-RoSy cross is invariant under
  flipping the normal, and in the position field flipping `n` negates the lattice's second axis
  globally, which is a relabel. Smoothness energy at 0.999 on the torus confirms it.

**`FieldReport.indexSum` is 0 on `cube_subdiv`, where Euler's formula requires 8.** The field's own
doc-comment says this number is the one thing that can validate the singularity detector, and that
if it does not equal 4χ the detector is wrong. On a sphere-topology cube it reads 0 under GameAsset
and 2 under Character. Unexplained; the icosphere reads 8 correctly, so it is not a blanket failure.

## 6. Instruments added

Measurement only — no behaviour changed, 128 tests, 126 pass, 0 fail, unchanged.

- `FieldDiagnostics.discardDiagonalFraction` / `discardJumpFraction`. The discard total was being
  read as a verdict on the field. It is not: a **diagonal** discard is what a triangulated input does
  to a *correct* lattice — when one cell is covered by two triangles, their shared edge *is* the
  cell's diagonal — and only the **jump** share is a defect. The split is 8.2 % / 1.6 % on the scan
  and 7.7 % / 0.00 % on the torus, so nearly all of a 9.8 % "discard rate" was benign.
- `LatticeAudit.Run` — the four Euler-floor cases with field, lattice, integrability, repair and
  output on one row; the full per-pass extraction dump on the torus; the corpus winding sweep; and
  the cluster-spread sweep.
- `RemeshProfile.clusterSpread` (0 = default, existing struct idiom), so the spread can be swept
  rather than argued about.
- `PerfProbe.RunReferenceComparison` now prints the lattice line with the split.

## 7. Sources

QuadRemesher's internals are not public and nothing here guesses at them; §1 is measurement of its
output and `quadremesher-study.md` §2 is its documented option surface. The recommendation in §4
traces to published, permissively licensed work:

- Jakob, Tarini, Panozzo, Sorkine-Hornung, *Instant Field-Aligned Meshes* (2015) — the method ported
  here, including the edge-collapse extraction this document recommends replacing.
- Huang, Zhou, Niessner, Shewchuk, Guibas, *QuadriFlow: A Scalable and Robust Method for
  Quadrangulation* (2018, BSD-3) — the integer system, the min-cost-flow repair, and the
  cell-indexed extraction.
