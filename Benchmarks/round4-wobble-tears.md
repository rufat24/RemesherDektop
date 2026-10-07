# Round 4 — wobble, shear bands, cheek straps, ccnb tears (2026-10-07)

All numbers: the editor window's path (`RemeshPipeline.Solve`, auto profile, IntegerGrid,
Adapt Quad Count on, Adaptive Size 50 %, Detect Hard Edges on), means over targets
4600 / 5000 / 5400. Harness and scripts: `Benchmarks/harness/` (`Program.cs` is the offline
runner; `agg.py` aggregates; `render4.py` renders framing-matched close-ups).

Metrics: p05 = 5th percentile of each quad's smallest corner angle (FreeRemesher on the body:
62.9); in2out = mean distance from the input surface to the output, in cells (lower keeps more
detail); long faces = faces with an edge over 3x the median.

## What the "wobble" is

Close-up renders of the helmet top and the balaclava folds: the coherent creases steer the
cross field, so loops run *parallel* to each plate edge or fold — but nothing places a loop *on*
it. The crease falls mid-row and the row is bent through its middle: every hard line shades as
a staircase of light and dark quads. FreeRemesher puts a loop on the line.

## Adopted

| change | body | head | ccnb |
|---|---|---|---|
| `IntegerGridMap.creaseLoopSnap` (new `CreaseLoopSnap.cs`) in2out | .098 → .095 | .152 → .136 | .137 → .119 |
| p05 | 61.0 → 58.1 | 51.9 → 49.3 | 41.5 → 40.8 |
| Hausdorff | .0089 → .0090 | .0153 → .0156 | .0156 → .0156 |
| `needleEdgeOverMedianOpen` 3 (open meshes only) | — | — | p05 40.8 → 45.2, long faces 26/217/19 → 10/17/7, Hausdorff .0156 → .0174 |

Topology is untouched by the snap (identical irregular and face counts). Renders:
`shots_final/head_r4_cheek_helmet.png`, `head_r4_neck.png`, `ccnb_r4_tear.png`.

## ccnb tear (root cause)

The embankment beside the parking lot came out as torn strips with a hole, in about one run in
three. The solver's input mesh there is clean. The integer map's flip repair is QuadriFlow's
greedy pass *without its SAT fallback*; ~450 parametrically folded triangles survive on ccnb
(111 on the head) and the extraction glues one lattice cell across many cells of surface (a
rejected feature placement of 19 cells shows it). Removing faces over 3 median edges before hole
closing removes the strips; it is limited to open meshes because on the closed head it left 34
open edges and on the body raised irregular 2.12 → 2.35 %. The real fix is the missing SAT /
exact flip repair — or a parametrisation that does not fold (see below).

## Measured and left off (all behind flags, nothing removed)

- `globalField` (`GlobalCrossField.cs`, Knöppel smoothest field by CG): hard locks 716 → 906
  singular faces; eigen field without creases: loop turn 6.43 → 5.10 deg, irregular unchanged.
- `cancelSingularPairs`: 170 pairs re-solved, only ~34 singular faces removed.
- `trueNormalPolishSweeps`: the reprojection onto true normals adds ~140 singular faces on the
  head, but sweeping on true normals makes it worse (5 sweeps: irregular 5.42 %).
- `coherentCreasesCut` + `coherentCreaseCutMinCells` 6 + `coherentCreaseCutStraightness` 0.9:
  crisper helmet, but body p05 61.0 → 52.2 and Hausdorff .0089 → .0127.

## Why the ceiling is structural

Shear bands are close +/- singularity pairs of the cross field plus rounding corrections routed
by the min-cost flow; the field has ~580 singularities on the head before projection. Every
measurement this round says the remaining gap to FreeRemesher sits in the QuadriFlow-style
position field + flow + greedy flip repair. FreeRemesher's engine strings
(`3CalcUVs`, `U+V integer`, `INTVARFLAG_BASIC_INTEGER_GridPole`, `HarmoDirGrid3x3`, CHOLMOD
`ChSolver.dll`) point to an MIQ-style global mixed-integer parametrisation: a seamless (u,v)
solved by sparse least squares on a cut mesh, singularities and cut transitions rounded to
integers, extracted by tracing iso-lines. That is the next build.
