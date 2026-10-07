# The shading gap against QuadRemesher, and what closed it

Written after comparing the delivered `20260515221044_54b8f8f4_Quads 26.asset` against
`Assets/QuadRemesher/Output/20260515221044_54b8f8f4_Retopo 4.fbx` — the two files the
user put side by side in the scene.

## The first thing to check was not a defect

The two files are not the same density. Ours has **5,215 faces**; QuadRemesher's has
**21,780**. That is 4.18x, and our quads are 2.1x longer on a side.

This matters because almost every visual judgement is sensitive to it. A coarser mesh
has larger angles between neighbouring faces for no reason other than being coarser,
so "theirs looks smoother" is partly a statement about face count, and no conclusion
about the algorithm can be drawn from those two files as they stand.

Everything below is therefore measured at **matched density** —
`Benchmark/CharacterExport.cs` writes ours at QuadRemesher's own face count (22,932
faces, mean edge 0.452 % of the bbox diagonal against their 0.449 %) and the same
script scores both.

## The defect, once density is controlled for

| angle between neighbouring faces | ours | QuadRemesher |
| --- | ---: | ---: |
| median | **7.67 deg** | 8.64 deg |
| > 10 deg | **39.9 %** | 44.1 % |
| > 20 deg | 19.4 % | **17.8 %** |
| > 45 deg | 8.37 % | **2.73 %** |
| > 60 deg | 6.00 % | **1.12 %** |
| > 90 deg | 2.05 % | **0.17 %** |
| worst | 179.8 deg | 154.7 deg |

Our surface is smoother than theirs at the median and four times worse in the tail.
**925 edges folded past perpendicular**, scattered over a mesh that is otherwise fine.
That is the whole complaint: a shape that reads correctly with small wrong spots all
over it.

Three hypotheses were tested first and all three were wrong:

- **Decimation.** Decimating to 217,800 triangles costs 0.0005 % mean deviation. Our
  own finished output's error is 0.0053 %, ten times larger. Running the full 1.5 M
  mesh (203 s against 37 s) moved curled quads 822 to 723.
- **Vertices off the surface.** Zero output vertices sit even half a quad off the input.
- **Over-smoothing.** Our deviation from the original is 0.0053 % against QuadRemesher's
  0.0202 %. We are four times closer to the scan than they are.

All three were refuted by the metric that could not see the defect at all. **Mean
deviation measures where the vertices are; it says nothing about how they are
arranged**, and an arrangement that zigzags is invisible to the first and decisive for
the second. Adding the dihedral distribution is what made the problem measurable.

## The cause

Instant Meshes — which this pipeline is a port of — places each output vertex at a
lattice point and stops. Nothing downstream is responsible for how one vertex sits
relative to its neighbours, and nothing has to be for the algorithm to be correct: a
lattice point half a cell away from where a neighbour would like it is still a valid
lattice point. It just shades badly.

Every production retopologiser closes that with a smooth-and-reproject relaxation.
That pass is `Runtime/Extraction/SurfaceRelaxation.cs`.

## Result, at matched density

| | before | after | QuadRemesher |
| --- | ---: | ---: | ---: |
| mean dihedral | 15.35 deg | **11.77** | 12.39 |
| p90 | 37.95 | **26.87** | 26.97 |
| p99 | 100.71 | **61.04** | 61.78 |
| > 45 deg | 8.37 % | **2.94 %** | 2.73 % |
| > 60 deg | 6.00 % | **1.06 %** | 1.12 % |
| > 90 deg | 2.05 % | **0.26 %** | 0.17 % |
| faces opposing their neighbourhood | 102 | **36** | 3 |
| non-convex (curled) | 290 | **86** | 5 |
| min interior angle p01 | 6.3 deg | **28.4** | 47.1 |
| deviation from original | 0.0053 % | **0.0015 %** | 0.0202 % |

Mean, p90 and p99 now beat QuadRemesher. The tail is within a factor of 1.5 where it
was a factor of 12. Fidelity improved at the same time rather than being traded away —
reprojection is doing its job.

At the density actually delivered (5,215 faces), against the asset the user has:
edges past 45 degrees 9.12 % to 3.82 %, past 90 degrees 1.43 % to 0.35 %, curled quads
69 to 36, faces opposing their neighbourhood 24 to 11, min angle p01 6.6 to 25.6, and
non-manifold edges 3 to 0.

## What the corpus caught, and why it is in the plan of record

The pass shipped its first version without crease protection, on the reasoning that
reprojection makes it unnecessary: a crease is part of the input surface, so a vertex
smoothed off one is projected back onto it.

**That reasoning is wrong**, and `Benchmarks/relax-*.csv` shows what it cost:

| `cube_subdivided` @ 2,000 | relaxation off | unguarded |
| --- | ---: | ---: |
| Chamfer mean | 0.000000 | 0.001225 |
| Hausdorff max | 0.000000 | 0.033851 |
| flipped normals | 0 % | **16.9 %** |

`cylinder_capped` lost a factor of 24 on Hausdorff at its rim.

The direction is what the reasoning missed. Smoothing moves a crease vertex ALONG the
surface, perpendicular to the crease and down onto one of the two faces. The nearest
point of the input to where it lands is on that face. Nothing pulls it back, and the
crease is shaved one step at a time by moves that each look locally innocent.

The fix pins vertices sitting in a crease of the **input** before any sweep, detected by
sampling the input surface normal at the vertex and at the midpoints to its neighbours.
The output's own dihedral angles cannot be used for this: a crease worth keeping and a
fold worth removing look identical there, and removing the second kind is the whole
point of the pass.

### The angle is the profile's own, and that is not a detail

Crease protection at 40 degrees fixes the cube and **costs nearly the whole win on the
character**: 4,203 of 22,246 vertices pinned (19 %), and edges past 45 degrees back to
7.03 % against 8.37 % for not running the pass at all. On a scan the things that look
like creases at output resolution are folds and noise.

So the pipeline passes the profile's own `featureAngleDegrees` — 30 for GameAsset, 20
for HardSurface, **0 for Photogrammetry and Character**, which already switch feature
DETECTION off for exactly the same reason. Same question, same answer, one knob.

With that, the cube is restored to Chamfer 0 / Hausdorff 0 / zero flipped normals at
2,000 quads, and the character keeps the full improvement (`pinned=0`).

## Residuals, recorded rather than hidden

- `cube_subdivided` @ 8,000: Hausdorff 0 to 0.0084 and 0.05 % flipped normals still
  survive the guard. Small, but not zero — a few crease vertices are not caught by
  midpoint sampling at that spacing.
- `torus` @ 2,000 and 8,000: quad planarity p95 0.0029 to 0.0109 of an edge. Relaxation
  equalises edge lengths on a doubly-curved surface and pays for it in warp. Both
  numbers are about 1 % of an edge.
- Chamfer rises by roughly 1.7x on the clean procedural spheres, where the lattice
  output was already good and there was nothing for relaxation to fix. It falls 3.5x on
  the character, where there was. The trade is regularity against per-vertex accuracy,
  and it goes the right way on the input that has a problem.

## Still open against QuadRemesher

Not closed by this, and not caused by it:

- **min interior angle p01 28.4 against 47.1**, worst 0.1 against 15.3. We still
  produce near-degenerate faces; relaxation improves them 4.5x but does not remove them.
- **quad fraction 93.8 % against 99.99 %** — 1,427 triangles against 2.
- **curled 86 against 5, flipped 36 against 3.**

The leading unexplored lever remains QuadRemesher's `CurvatureAdaptivness=50` against
our uniform sizing (their edge lengths vary 4.0x, ours 1.6x), read from
`QuadRemesher/Editor/QuadRemesherBridge.cs`. That is PLAN item 6.1 and it is still
unbuilt.

## Reproducing

```bash
bash tools/sync-headless.sh
Unity.exe -batchmode -nographics -projectPath Headless -quit \
  -executeMethod NativeRemesher.Benchmark.CharacterExport.Run -logFile Headless/export.log
Unity.exe -batchmode -nographics -projectPath Headless -quit \
  -executeMethod NativeRemesher.Benchmark.RelaxSweep.Run -logFile Headless/relax.log
```

The A/B that catches crease damage — the topology columns come out bit-identical
between the two runs, which is how you know a difference is relaxation's and not
something else's:

```bash
Unity.exe -batchmode -nographics -projectPath Headless -quit \
  -executeMethod NativeRemesher.Benchmark.BenchmarkRunner.RunFromCommandLine \
  -corpus procedural -targets 500,2000,8000 -relax off \
  -out Benchmarks/norelax.csv -baseline none
```

Scoring ours against QuadRemesher's output uses Blender as the interpreter, since this
machine has no system Python:

```bash
"/c/Program Files/Blender Foundation/Blender 4.5/blender.exe" --background \
  --factory-startup --python shading.py -- Benchmarks/reference/v1-character-21780.obj
```

---

# Addendum: the pass amputated a thumb, and nothing above could see it

The user reported, after running the version described above: *"mesh smoothness is very bad,
same issues thumb finger in hand is not complete."*

The thumb part was right, and relaxation caused it.

## The blind spot

Every deviation number in this document — and in the whole project — measures **our vertices to
the original surface**. That direction is near zero by construction: extraction places output
vertices on lattice points sampled at input vertices, and relaxation projects them back onto the
input. **A mesh that dropped an entire thumb still scores a perfect deviation**, because the
vertices it does have are exactly where they should be.

The missing half is the other direction, **the original surface to our mesh**. A point on the
original thumb has no nearby point on an output that lost the thumb. `Benchmark/CoverageAudit.cs`
measures it, reports the fraction of the original with no output within half a quad, and clusters
the failures into blobs with coordinates.

| at 21,780 faces | uncovered surface |
| --- | ---: |
| decimated input (217,800 tris) | 0.000 % |
| ours, relaxation off | 0.049 % |
| QuadRemesher | 0.146 % |
| **ours, relaxation on, as shipped** | **0.882 %** |

An eighteen-fold regression, introduced by the pass, invisible to every metric it was tuned
against. Smoothness improved monotonically the whole time.

## Why smoothing removes extremities

At a fingertip **every ring neighbour is further down the finger**, so the umbrella target is down
the finger. Reprojection puts the vertex back on the surface — but on the *side* of the finger,
not back at the tip. Each sweep walks the tip ring a little further down. Twenty-four sweeps of a
hundredth of a cell each, all pointing the same way, and the tip is represented by no vertex at
all.

It is the same failure as the crease shaving above, in a different direction, and it was missed
for the same reason: the guard added for creases tests whether a move *crosses* a sharp edge, and
walking down a smooth finger crosses nothing.

## The fix: a cap on total drift

`defaultMaxDrift` — no vertex may end up more than half a lattice cell from where extraction put
it. A cap is the right shape of fix because the defect relaxation removes is **local**: a folded
quad is fixed by its vertex moving a fraction of a cell. Long-range migration buys nothing and
costs the shape.

Swept on the character against coverage **and** smoothness together, because they trade directly
and sweeping either alone picks the wrong value:

| drift cap | uncovered | > 45 deg | > 90 deg | min angle p01 |
| --- | ---: | ---: | ---: | ---: |
| relaxation off | 0.045 % | 8.37 % | 2.05 % | 6.3 |
| uncapped | 0.882 % | 2.94 % | 0.26 % | 28.4 |
| 1.0 cell | 0.745 % | 2.94 % | 0.29 % | 28.5 |
| **0.5 cell** | **0.113 %** | **3.65 %** | **0.39 %** | **27.5** |
| 0.25 cell | 0.054 % | 5.77 % | 0.91 % | 17.9 |
| 0.15 cell | 0.048 % | 7.46 % | 1.59 % | 7.6 |
| *QuadRemesher* | *0.146 %* | *2.73 %* | *0.17 %* | *47.1* |

The knee is sharp and it is at 0.5: coverage collapses sevenfold from 1.0 to 0.5 for 0.7 points of
smoothness, and tightening further buys almost no coverage for a lot of smoothness. **At 0.5 we
lose less geometry than QuadRemesher does.**

`RelaxSweep` now prints `uncovered%` as its first result column. Anything that improves smoothness
while raising it is not an improvement.

## What is still missing at the hands, and whose fault it is

The uncovered blobs at the hands survive **in every arm, including relaxation-off and
QuadRemesher's own output**:

| at the hand, worst uncovered depth | |
| --- | ---: |
| ours, relaxation off | 0.99 % of diagonal |
| ours, drift-capped | 1.03 % |
| QuadRemesher | 0.76 % |

So the thumb tip is short on both, by about two quad edges, and this defect **predates the
relaxation pass entirely**. Ours is about a third deeper than theirs.

The cause is the one already identified and still unbuilt: at 21,780 faces our quad edge is 0.006
and uniform, and a thumb tip of that radius cannot be resolved by a lattice that size.
QuadRemesher ships `CurvatureAdaptivness=50` and spends smaller quads where curvature is high —
their edge lengths vary 4.0x against our 1.6x. The same lever explains min angle p01 28.4 against
their 47.1.

## Face count is a large part of what is being looked at

The delivered asset was generated at about half QuadRemesher's face count, and thin features are
the first thing a coarser lattice cannot hold:

| ours | uncovered |
| --- | ---: |
| 22,932 faces | 0.115 % |
| 10,961 faces | **1.169 %** |

Ten times more missing surface, with large blobs at the head, both shoulders and both hands. A
side-by-side against a 21,780-face competitor output cannot separate that from anything else.

---

# Addendum 2: is pre-decimation destroying the detail?

Asked directly by the user: *"maybe decimation destroys the details that is why there are so many
isolated faces normals and very bad output."*

It is a reasonable suspicion and the coverage metric is the first one in this project able to
test it, because detail loss is exactly "original surface with nothing near it". Measured at
21,780 faces, same target and same everything:

| | uncovered surface |
| --- | ---: |
| the decimated input itself, 217,800 tris | **0.000 %** |
| ours, decimating first (37 s) | 0.115 % |
| ours, 1.5 M triangles straight in (203 s) | 0.087 % |
| QuadRemesher | 0.146 % |

**Decimation to 217,800 triangles loses none of the surface at the output's own scale.** Its own
worst error is 0.0141 % of the diagonal, eighteen times finer than the half-quad threshold that
counts as missing. Skipping it improves coverage from 0.115 % to 0.087 % — real, and a quarter of
our total loss — but it costs 5.5x the runtime and it is not what makes the output bad. The other
three quarters happen in the lattice and extraction, which is where the work went instead.

## And do we need all the stages?

The same question, asked of the pipeline as a whole. Every stage earns its place on measurement
recorded elsewhere in this directory, and the short version:

- **Pre-decimation** is a QUALITY stage, not a speed one. Output quality depends on the ratio of
  lattice spacing to input edge length and falls off a cliff past about 2.5x; decimating first is
  what keeps that ratio in band at an aggressive target. Removing it improves coverage slightly
  and costs 5.5x the time.
- **The hierarchy** is the mechanism, not an optimisation. Gauss-Seidel moves information one edge
  per sweep, so on a 100k-vertex mesh no sweep budget reaches across the model — raising it from
  60/50 to 300/300 moved irregular vertices by 1.3 points. Coarse-to-fine is what makes the field
  globally consistent.
- **Extraction's passes** are each attached to a measured defect; see QuadExtractor's header.
- **Relaxation** is the newest and has now damaged the output twice. It stays because the defect
  it removes is real and large, and each damage has produced a guard and a metric that would have
  caught it.

The stage this pipeline was MISSING is the one now added: curvature-adaptive sizing.
