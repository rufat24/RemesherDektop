# Curvature-adaptive sizing

The last structural difference between this pipeline and QuadRemesher. Their bridge sends
`CurvatureAdaptivness=50` with `ExactQuadCount=0` — small quads where the surface curves, and
permission to exceed the requested count to get them. Ours were all the same size.

Three independent measurements pointed at it before any of this was built:

- the thumb tip is not represented at 21,780 faces, because a uniform quad edge of 0.006 cannot
  resolve a tip of that radius ([`Benchmark/CoverageAudit.cs`](../Assets/NativeRemesher/Benchmark/CoverageAudit.cs));
- our worst interior angles are 28 degrees at p01 against their 47, which is what a quad forced to
  follow curvature it cannot resolve has to do;
- their edge lengths vary 4.0x across the model; ours varied 1.6x.

## The model

`Runtime/ScaleField.cs`. Per-vertex spacing from the **maximum** principal curvature, estimated as
the largest rate of change of the surface normal across the one-ring.

Maximum rather than mean, and that matters: on a cylinder the normal does not change along the
axis and changes at 1/radius around it, so a mean would report half the curvature and size a
finger as though it were twice as fat. Sizing has to follow the direction that needs the smallest
quad.

The law is **chord error**, not curvature directly. A chord of length h across a circle of radius R
sits h²/(8R) below the arc, so holding that sagitta constant gives h ∝ √R. That is gentler than
the h ∝ R that "smaller where it curves" first suggests, and the gentleness is the point: sizing
by 1/curvature spends so much on a fingertip that the rest of the model starves.

Normalisation is closed-form: quad count ≈ Σ areaᵢ/hᵢ², so writing hᵢ = c·sᵢ gives
c = √(Σ(areaᵢ/sᵢ²) / N). With sᵢ ≡ 1 that is √(totalArea/N), which is exactly
`EdgeLengthForQuadCount`. **Uniform sizing is this formula with a constant input, not a separate
path** — which is what makes `adaptivity = 0` provably a no-op, and it is asserted as such in
`ScaleFieldTests.ZeroAdaptivityIsExactlyUniformSizing`.

## Plumbing

A graded lattice means two neighbouring vertices no longer share one. Everything that compares two
lattices needed a per-side spacing:

| | change |
| --- | --- |
| `PositionField.Compat` | overload taking `scale0/scale1`; each side floors and steps in its own |
| `PositionField.CompatIndex` | same; integer coordinates stay in each vertex's own lattice, so `Classify` is unchanged |
| `PositionField.Solve` | `ScaleField` overload; anchors each vertex with its own spacing |
| `HierarchicalSolve` | restricts the field to each coarse level, taking the **smaller** of a coarse vertex's two children |
| `EdgeSubdivide` | carries spacing through the refinement; a midpoint takes the mean of its endpoints |
| `LatticeCollapse` | per-vertex spacing in classification, and the cluster-spread bound in cells at the **finer** of the pair |

The asymmetries — smaller on restriction, finer on the spread bound — are both deliberate. The
error is asymmetric: a cell that is too large has already merged surface that cannot be separated
again, where one that is too small merely converges slower.

## What it is worth, and where the value came from

Swept on the character at QuadRemesher's own face count, against **coverage and smoothness
together**. Sweeping either alone picks a value that is clearly wrong on the other.

| adaptivity | clamp | gradation | spread | uncovered | deepest hole | > 45° | min angle p01 | curled |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 (uniform) | — | — | 1.00 | 0.113 % | 1.000 % | 3.65 % | 27.5 | 0.44 % |
| 0.25 | 3 | 1.30 | 1.94 | 0.058 % | 0.758 % | 4.38 % | 25.4 | 0.46 % |
| **0.25** | **2** | **1.15** | **1.94** | **0.036 %** | **0.448 %** | **4.55 %** | **25.6** | **0.49 %** |
| 0.50 | 3 | 1.30 | 3.77 | 0.116 % | 0.436 % | 5.17 % | 23.6 | 0.68 % |
| 0.50 | 2 | 1.30 | 3.32 | 0.102 % | 0.435 % | 5.31 % | 23.6 | 0.70 % |
| 0.50 | 3 | 1.15 | 3.69 | 0.112 % | 0.783 % | 5.18 % | 23.5 | 0.55 % |
| 0.75 | 3 | 1.30 | 6.41 | 0.142 % | 0.457 % | 7.00 % | 18.3 | 1.20 % |
| 1.00 | 3 | 1.30 | 8.26 | 0.210 % | 0.548 % | 8.08 % | 16.7 | 1.30 % |
| *QuadRemesher* | | | *4.0* | *0.146 %* | *0.755 %* | *2.73 %* | *47.1* | *0.02 %* |

### Coverage is not monotone in adaptivity

That is the finding worth keeping. More grading does not mean more detail resolved: past about a
quarter, the lattice is graded faster than it can stay consistent — two neighbouring cells of very
different sizes cannot agree, edges misclassify, and the resolution spent on the detail is lost
again to a noisier extraction. Uncovered surface bottoms out at 0.036 % and climbs to 0.210 % by
adaptivity 1.0, worse than uniform.

### The gradation bound does most of the work

1.15, not the 1.3 that is standard for graded **triangle** meshing, and the difference is the
lattice. A triangle mesher only has to interpolate a size; this one has to make two neighbouring
*integer* lattices agree, and the residual `Compat` cannot remove is set by exactly this ratio. At
adaptivity 0.25 with the clamp at 2, tightening 1.3 → 1.15 takes the deepest hole down by a third
(0.758 % → 0.448 %) and uncovered surface by nearly half, for 0.17 points of smoothness.

## Where it applies: only where feature detection is off

The first version graded on every profile but hard surface. The corpus refuted that. Grading against
uniform, relaxation off in both so the comparison is grading alone, on the game-asset profile:

| mesh | target | graded | uniform |
| --- | ---: | --- | --- |
| cube_subdivided | 2,000 | 94.2 % quads, Hausdorff 0.010, irregular 23.2 % | **99.5 %, 0.000, 1.7 %** |
| cube_subdivided | 500 | 94.5 %, 0.405, 24.1 % | **99.1 %, 0.411, 3.8 %** |
| cylinder_capped | 500 | 96.5 %, irregular 27.8 % | **98.4 %, 21.3 %** |
| every icosphere | any | indifferent | indifferent |

Uniform gets the subdivided cube EXACTLY right — Hausdorff zero, one face in sixty irregular — and
grading takes it apart. So the rule, which is the one to apply to any profile added later:

> **Grade only where feature detection is off.**

A crease makes the curvature estimate bimodal: a spike along the edge, near zero across the panel.
Grading then spends the budget resolving the edges, which is exactly what the feature constraints
were already going to hold — the two mechanisms want the same quads and fight over them. Where
creases are declared absent, curvature is the only instrument there is.

The icospheres being indifferent is worth its own line, because it was first read as evidence of a
regression. A surface of constant curvature grades to a spacing spread of **1.01** no matter what
adaptivity is asked for: the field is correct, and correct here means doing nothing. That is what
the `spacingSpread` column was added to the harness to show, and adding it retired a wrong
explanation for a set of regressions that turned out to be neither grading's nor relaxation's.

## Settings, and what they buy

`adaptivity 0.25, clamp 2, gradation 1.15` on the two scan profiles, photogrammetry and character.
Hard surface and game asset are uniform.

Against uniform, on the character:

- **deepest unrepresented hole 1.000 % → 0.448 %** — this is the thumb, and it now beats
  QuadRemesher's 0.755 %;
- **uncovered surface 0.113 % → 0.036 %**, against their 0.146 %;
- smoothness costs 0.9 points (3.65 % → 4.55 % of edges past 45 degrees) and min angle p01 costs
  1.9 degrees.

## Still open

Against QuadRemesher we are now **ahead on both coverage measures and behind on every quality
measure**: edges past 45 degrees 4.55 % against 2.73 %, min angle p01 25.6 against 47.1, curled
0.49 % against 0.02 %, quad fraction 92.8 % against 99.99 %.

The quality gap is not a sizing problem any more — grading costs about a fifth of it and the rest
was there at uniform. It lives in the tracer and the collapse, and 1,900 surviving triangles
against their 2 is the clearest single statement of it.
