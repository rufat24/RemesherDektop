# What QuadRemesher is actually doing

Read from `Assets/QuadRemesher/Editor/QuadRemesherBridge.cs` and `QuadRemesherSettings.cs`, which
are the real thing rather than documentation about it — the bridge writes a settings file for the
engine and every key it can send is in one function, `WriteSettingsFile`.

## Its entire parameter set

That is the first finding, and it is worth sitting with:

| key | default | what it does |
| --- | --- | --- |
| `TargetQuadCount` | 5000 | the request |
| `CurvatureAdaptivness` | **50** | 0–100. Smaller quads where curvature is higher; 0 is uniform |
| `ExactQuadCount` | **0** (adaptive ON) | 0 lets it *exceed* the target to fit curvature |
| `AutoDetectHardEdges` | **on** | finds hard edges "using a mix of edge angles and other geometrical considerations" |
| `UseIndexedNormals` | off | treats normal-split loops as guidance |
| `UseMaterialIds` | off | material borders become edge loops, ids preserved |
| `UseVertexColorMap` | off (bridge can't send it) | per-vertex painted density |
| `SymAxis` / `SymLocal` | off | symmetric remeshing about the object's local axes |

Eight keys, of which four are off by default. There is no iteration count, no smoothing strength, no
threshold, no cleanup toggle — nothing corresponding to most of what this pipeline exposes. Whatever
their engine does internally, **the user-facing surface is a target, a curvature dial, and a switch
saying whether the target is a limit or a suggestion.** That is a design statement: everything else
is expected to be decided by the algorithm, not by the person.

## The three differences that are really differences

**1. The target is a suggestion, and this is measurable.** `ExactQuadCount=0` is the default, and the
tooltip is explicit — "Creates more polygons than asked to fit high curvature area". The output the
user compared against was asked for **2,000 quads and produced 4,954**. Two and a half times the
request, and not an error: that is the setting doing what it says. Our pipeline treats the target as
a target and lands within a few percent of it. So a like-for-like comparison at "the same target"
is not like-for-like at all, and every comparison in this repo is therefore made at matched
**face count**, never at matched request.

**2. Hard-edge detection is ON by default, even for organic scans.** Their own tooltip advises
turning it off for smooth organic shapes, and the default leaves it on anyway. Ours is off for both
scan profiles (`featureAngleDegrees = 0`). Worth testing rather than assuming — but note their
detector is explicitly *not* an angle threshold: "a mix of edge angles and other geometrical
considerations". A dihedral threshold is the easy half of that problem.

**3. It does not decimate.** The bridge hands the engine the input mesh unchanged — 1.5 M triangles
in this case. We decimate to ten triangles per target quad first. That was tested directly and the
decimation is *not* where the detail is lost (`shading-tail.md`): decimated input alone leaves
0.000 % of the surface uncovered. But it is a real architectural difference and it is theirs, not a
shortcut they also take.

## What the parameters cannot explain

Their result is cleaner in a way no setting in the table above accounts for, and the defect audit
(`Benchmarks/reference/defects.py`) says exactly where the difference lives. At matched density on
the character, flagging every face that is a triangle, an n-gon, a sliver, a curled quad, a face on
an open edge, or either side of a fold past 60°:

| | ours | theirs |
| --- | ---: | ---: |
| faces flagged | **13.47 %** | 4.36 % |
| triangles | **437** | **0** |
| faces in folds | 283 | 214 |
| curled | 41 | 7 |
| slivers | 29 | 2 |
| faces on an open edge | 17 | 0 |
| single isolated bad faces | **345** | **1** |

Two things stand out, and neither is a parameter:

- **They emit no triangles at all.** Not few — none. A quad mesh cannot lose triangles one at a
  time (the parity argument in `TrianglePairing`), so zero is not luck, it is a guarantee their
  layout gives them and our tracer does not.
- **Their bad faces are never alone.** One isolated flagged face in the whole mesh, against our 345.
  Every one of their 214 flagged faces sits in a fold that follows a real crease of the shape — the
  same places ours folds, plus nothing else. Ours has those same folds *and* a scatter of lone
  defects across the whole surface, which is precisely the "okayish, but zoom in and there are
  artifacts" the user describes.

The folds both meshes share are the shape's own: the top of the head, and a symmetric pair at
(43, 93, 14) % and (57, 93, 14) % of the bounding box. The ones only we have are a symmetric set at
the far left and right — x = 3–7 % and 94–99 %, y = 84 % — which is the hands.

So the gap is not a setting we have not found. It is: **leftover triangles scattered around the
singularities, and the hands.**

## What was done about it, and what it cost

**The triangles were a pass running one line too late, plus one binary test.** `TrianglePairing`
walks two triangles together through the quads between them and re-tiles the patch, and it was
leaving 437. Two causes, both measured rather than guessed:

- It ran *before* the n-gon splitter, and the splitter **makes triangles** — an n-gon with an odd
  corner count has no all-quad tiling, so it leaves one behind. Pairing took 540 triangles down to
  322 and the mesh reaching the user had 440. The 118 it gained back were created after the only
  pass that could remove them had finished. It now runs again afterwards.
- Every single refusal — 501 of 501 in the first run, 451 of 454 in the second — was the convexity
  test, with **not one** caused by the quality threshold, a duplicate edge or a backwards face.
  That is why sweeping the chain length, the round count and the quality threshold moved nothing at
  all: they were never the limit. The test asked "is any corner reflex", and on a curved patch the
  answer is almost always yes by a degree or two, so a binary test on that quantity is not a filter,
  it is an off switch. It is now graded, a convex tiling is still always preferred, and a bounded
  fold is accepted only where no convex tiling exists at all.

| fold allowed | quad % | triangles left | curled % | > 45° | min angle p01 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 0 (before) | 93.34 | 370 | 0.85 | 4.50 | 23.9 |
| 20° | 95.13 | 268 | 0.87 | 4.35 | 23.3 |
| **40°** | **96.63** | **184** | 1.01 | 4.48 | 23.1 |
| 90° | 97.90 | 114 | 1.62 | 4.52 | 24.3 |

On the delivered asset, end to end: **437 triangles → 181**, flagged faces 13.47 % → 9.54 %, faces
on an open edge 17 → 1, isolated single-face defects 345 → 175. Across the procedural corpus quad
fraction rose about four points everywhere (icosphere 94.1 → 97.9 %, capped cylinder 97.7 → 99.5 %)
with **mean deviation unchanged** (±5 %, several improved).

The cost is real and is the folded quads: planarity p95 and Hausdorff max both worse (torus at 8,000
quads, 0.0014 → 0.0056 of the diagonal), irregular vertices up two to four points. Hard surface
takes a tenth of the tolerance for that reason.

**Their curvature setting does NOT explain the hands.** The obvious reading of the remaining gap was
that they resolve fingers because they grade harder, so it was tested directly — the same model at
the same face count at adaptivity 0.25, 0.50 and 0.75:

| adaptivity | spread | flagged | folds | faces in hand clusters |
| ---: | ---: | ---: | ---: | ---: |
| 0.25 (ships) | 1.78 | 9.72 % | 328 | 59 |
| 0.50 (theirs) | 3.17 | 9.97 % | 319 | 35 |
| 0.75 | 5.46 | 12.00 % | 378 | 76 |

Half a point of improvement at the hands and none overall, then it gets worse. So the hand folds are
not a shortage of resolution that can be bought with the budget — a finger is a tube a few quads
around, and where the lattice has to fold to get round it, smaller quads fold too. That is the
orientation field and the tracer, not the sizing, and it is the next thing to look at.

## Known defect opened by this work

Three boundary edges appeared on `icosphere_80k` at 8,000 quads, where there were none — one
triangular hole in a closed mesh — and isolated vertices went up by one or two on three other cases.
Small, but it is a validity defect rather than a quality one, and this project does not trade those.
The patch re-tiling is leaving a loop uncovered somewhere; it needs a postcondition asserting that
what a patch replaced is exactly what it covered.

---

# The review, done properly

## It does not decimate. This is now evidence, not inference.

The engine runs as a separate process over files in `%TEMP%/Exoside/QuadRemesher/Unity/`, and the
last run's files are still there:

```
RetopoSettings.txt      482 bytes
inputMesh.obj     142,159,035 bytes      <- 749,998 vertices, 1,500,000 triangles
retopo.fbx            111,280 bytes      <- 4,954 quads
```

```
TargetQuadCount=2000      CurvatureAdaptivness=50    ExactQuadCount=0
UseVertexColorMap=False   UseMaterialIds=0           UseIndexedNormals=0
AutoDetectHardEdges=1
```

The engine was handed **all 1,500,000 triangles** and asked for 2,000 quads. It produced 4,954.
A 300:1 reduction in one step, on the full-resolution mesh, with no decimation anywhere. We decimate
to 52,150 triangles first — 29:1 — and then remesh 10:1.

## And our decimation is not what is wrong

Measured at the density the complaint is about, coverage of the original by the decimated input
before any remeshing (`Benchmark/DecimationProbe.cs`):

| budget | triangles | uncovered | deepest gap |
| --- | ---: | ---: | ---: |
| 10 / quad (ships) | 52,150 | 0.000 % | 0.051 % |
| 40 / quad | 208,600 | 0.000 % | 0.014 % |
| none | 1,500,000 | 0.000 % | 0.000 % |

The threshold for "uncovered" is half a target quad, 0.46 % of the diagonal; the worst error at the
shipping budget is 0.051 %, nine times finer. The earlier clearance of decimation was measured at
21,780 quads (a 7:1 budget) and carried over to 5,215 (29:1) without being re-run, which was not
sound — so it was re-run, and it holds.

Running the whole pipeline with decimation off confirms it end to end: flagged faces 9.72 % → 8.35 %,
folds 328 → 275, hand clusters 59 → 42 faces, for **eight times the runtime** (26 s → 208 s). Real,
small, and not the defect.

## What IS wrong, in one number

`Benchmarks/reference/layout.py` reads the structure rather than the quality. At matched density:

| | ours | theirs |
| --- | ---: | ---: |
| **irregular interior vertices** | **1,702 (32.4 %)** | **204 (4.1 %)** |
| valence 3 / 5 | 788 / 686 | 99 / 92 |
| valence 6 / 7 / 8 | 144 / 29 / 4 | 9 / 1 / 0 |

A third of our vertices are irregular against one in twenty-five of theirs. That is the "mess of
polygons": not defects scattered on a good mesh, a quad flow that is disordered everywhere.

**And our field is not the cause.** On an icosphere the orientation field has exactly **8**
singularities with `indexSum = 8` — the theoretical minimum for a sphere, exactly what Euler
requires. The output of that same run is 12.6 % irregular: about a thousand irregular vertices from
a field that has eight. The field is right and the extraction manufactures the rest.

It is not the tracer either — 43.4 % irregular into it, 42.6 % out. It is the collapse: the step that
turns the field and the positions into a cluster graph.

Two constraints on any theory of it, both already measured:

- **It is not local non-integrability.** 98.3 % of triangles already satisfy the closed-loop
  condition; only 926 of 54,740 fail, which bounds any integer repair at about 1 % of edge
  classifications. The min-cost-flow machinery is written and priced (`LatticeRepair.Plan`) and
  cannot be worth more than that bound.
- **It scales with how coarse the output is, not how fine the input is.** Same icosphere, same
  input: 8,000 quads gives 12.6 % irregular, 2,000 gives 17.0 %, 500 gives 24.6 %. The character at
  a fixed 5,215 quads is 31.4 % with decimation and 31.7 % without.

So the cells are locally converged and do not line up globally, and the coarser the cells the worse
it gets. That is where the work is — `LatticeCollapse`, and what it does with a field that was
already correct.

---

# Round two: the split cells

## The mechanism

Every input edge is classified independently by asking which integer lattice offset best explains
the gap between its two endpoints' lattice points. **An edge whose endpoints sit about half a cell
apart is a coin toss**: offset 0 explains it with a residual of 0.5 and offset 1 explains it with a
residual of 0.5, and noise decides. Different edges between the same two groups of vertices come to
different answers, so one cell comes out as two clusters half a cell apart — with an output edge
between them, because some edge voted "one step".

A split cell is exactly a cancelling valence pair. The two halves divide the four lattice directions
between them, so each is short of neighbours, and every neighbour of the original cell now has two
claimants in one direction. That is the 3-beside-5 signature the output is full of. The already-linked
guard in Pass B then makes it permanent: once one edge between the halves has been called an output
edge, every later candidate to merge them is refused.

The measurement that says so, on the character at the delivered density: **18 % of output edges are
shorter than 0.75 cells and 2.5 % shorter than half a cell**, where a lattice that tiles would put
nearly all of them at 1.0.

## The repair, and where it stops

`LatticeCollapse.MergeSplitCells` merges cluster pairs whose lattice points sit closer than a
threshold, cheapest first, under the same spread bound as the main collapse.

| merge below | irregular | quad % | triangles | edges < 0.75 cells | uncovered |
| ---: | ---: | ---: | ---: | ---: | ---: |
| off | 31.5 % | 96.63 | 184 | 18.2 % | 3.88 % |
| 0.6 | 29.3 % | 97.51 | 130 | 15.1 % | 3.98 % |
| **0.8** | **25.7 %** | **98.06** | **100** | 9.4 % | 4.22 % |
| 0.9 | 29.9 % | 97.92 | 105 | 5.8 % | 4.23 % |
| 1.0 | 46.1 % | 97.79 | 103 | 2.5 % | 3.63 % |

**Not monotone, and the turn is the useful part.** Past 0.8 it starts merging cells that are
genuinely adjacent — neighbouring cells sit one cell apart by definition — and each of those takes a
vertex out of the middle of a regular grid, costing two irregular vertices rather than saving any. At
1.0 the short edges are nearly all gone and the mesh is half irregular, which is the clearest
possible statement that **short edges were a symptom, not the disease**.

Cluster spread was swept with it and does not matter: 29.4 % irregular at a 2.25-cell bound against
29.9 % at 1.0. Over-merging by that route is not happening.

## What is left, stated honestly

25.7 % irregular against QuadRemesher's 4.1 %. The gap decomposes into two pieces and neither is
closed:

- **Our field carries 427 singularities** (206 positive, 204 negative) where theirs can carry at most
  204, since that is their whole irregular count. About half of ours are cancelling pairs, which a
  smoother or better-conditioned field solver would not produce.
- **Extraction still amplifies by about three times** — 427 singularities in, roughly 1,300 irregular
  vertices out, down from 1,625. The split cells were one cause of the amplification and are now
  repaired; the rest is not yet localised.

Across the procedural corpus the repair is net positive but mixed: ten cases improve, six worsen, and
the improvements are the larger ones (−4.3, −3.9, −3.0 points against +2.5, +1.3).
