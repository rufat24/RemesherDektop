# What QuadRemesher does that we do not

A study of Exoside QuadRemesher 1.3, written to answer one question: *why are its results better,
and which parts of the difference can we build ourselves?*

Nothing from QuadRemesher is used, linked, bundled or copied here. It is a closed commercial product
by another author and it stays that way. This is a study of a competitor's **feature surface**, and
of the **published literature** that covers the same ground.

> **§4's ranking is superseded by `quadremesher-measured.md` (2026-09-08).** This document was
> written from the user documentation, without measuring QuadRemesher's output or asking which stage
> of our own pipeline loses the structure. Both have now been done, and the ranking does not survive:
> on a **torus** — no features to align to, no curvature variation to adapt to, a defect-free answer
> available, our field carrying exactly 0 singularities and our lattice 0 torn edges — we still emit
> 283 irregular vertices where 0 are possible. So neither §4's first item (feature/seam constraints)
> nor its second (adaptive sizing) can be the cause of the headline gap, and §2.1's claim that
> uniform sizing is "the single largest gap" is contradicted by edge-length CV: ours 0.244–0.314
> against QuadRemesher's 0.262–0.396.
>
> §4's third item — the global integer solve — is the right area, but its stated payoff is also
> wrong: the integrability defect is far too small to explain the gap on its own. The real work is
> **cell-indexed extraction**, which the integer solve is the prerequisite for. See
> `quadremesher-measured.md` §4.
>
> **§5 (the two schools) and §3 (guides are the UV fix) still stand**, and §5's conclusion that going
> fully layout-based would be a rewrite rather than an improvement is reinforced by the measurement.
> §1, §2 and §6 are unaffected — they are the documented option surface and are accurate.
>
> One thing this study could not know: **QuadRemesher reaches 2.7 % irregular vertices on our
> reference asset, against the reference retopology's 11.3 %.** The target was understated everywhere
> in this repo.

---

## 1. What is actually known, and what is not

Sourced facts:

- QuadRemesher is by Maxime Rouca (Exoside), who also wrote **ZRemesher** in ZBrush and ZBrush's
  Decimation Master. The two share an algorithmic foundation.
- It is closed source. **There is no paper and I found no patent.** Its internals are not public.

So: anybody who tells you precisely how QuadRemesher works internally is guessing. I am not going to
guess. What is public — and what turns out to be enough — is the **user documentation**, because every
option in it is a knob attached to a specific stage of the pipeline. The option list *is* a
description of the architecture, at the resolution that matters for reimplementation.

## 2. The architecture the option list implies

Reading `QuadRemesher_1.3_UserDoc.pdf` end to end, QuadRemesher exposes exactly four things. We have
**none of them**.

### 2.1 Adaptive Size — curvature-driven local edge length

> the remesher locally adapts quads size to the curvature of the input mesh

A slider, 0–100%, defaulting to **50%**. At 0 the output has equally sized quads everywhere; at 100
curved regions get markedly smaller quads and flat regions markedly larger ones.

**Our remesher has no equivalent.** We carry one scalar target scale and apply it uniformly. On the
photogrammetry asset that means the same edge length is spent on a flat car park as on a roof ridge —
which is why flat areas look over-tessellated and creases look under-resolved at the same time.

This is the single largest gap in the list.

### 2.2 Adaptive Quad Count — which constraint wins

An on/off switch deciding how `Target Quad Count` is interpreted:

- **OFF** — hit the requested count. Curved regions get smaller quads, so flat regions must get
  bigger ones to compensate. The count is respected.
- **ON** — the count instead defines the quad size *on flat areas*, and curved regions add quads on
  top. The doc's own example goes from a requested 100 quads to ~616.

This is a small piece of policy on top of 2.1, but it is the difference between "hit my budget" and
"respect my shape", and users want both at different times.

### 2.3 Guides — forced edge loops ("Edge Loops Control")

The largest section of the doc. A **guide** is an edge path on the input where the user wants an edge
loop in the output. Guides can be harvested from:

| Source | Note |
|---|---|
| Detect Hard Edges by angle | fully automatic; the doc's own default advice |
| Material boundaries | |
| Smoothing groups | |
| User hard/soft edge tags | |
| **Normal splits (normal creasing)** | for meshes carrying explicit normals |
| Polygon parts / primitive groups | modo, Houdini |
| BRep faces & edges | Fusion 360 |

The doc's headline advice is that enabling *Detect Hard Edge by angles* alone works in most cases, and
warns that badly defined guides confuse the algorithm.

**This is the mechanism behind the "structured" look** that makes QuadRemesher and ZRemesher output
read as hand-made. Loops land on features because they were *told to*, not because a smooth field
happened to align there.

### 2.4 Vertex-colour density painting

Per-vertex paint driving local quad size, red = 4× smaller edges, cyan = 4× bigger, via a 0.25–4.0
slider. This is 2.1 with a manual override channel. Cheap to add once 2.1 exists, because it is the
same per-vertex scale field with a second contributor.

---

## 3. The finding that matters most: guides are the UV fix

This is the connection worth the whole study.

The reported defect is grey/smeared bands along UV seams in the photogrammetry output. The mechanism
in our code is: `AttributeTransfer` samples **one UV per output vertex**. A vertex sitting on a seam
has two equally valid UVs and picks one arbitrarily; its neighbour across the seam picks the other;
the quad spanning them then interpolates *across the entire atlas*, sampling whatever is between the
two islands. That is the grey band.

There are two independent fixes and they compose:

1. **Split output vertices at seams** in `AttributeTransfer` + `ToMesh`, so one position can carry two
   UVs. This is *necessary* — without it no topology can be correct, because a single vertex cannot
   represent a seam by definition.
2. **Put an edge loop on every seam** — QuadRemesher's guide mechanism. Then no quad ever straddles a
   seam in the first place, and the split in (1) is clean rather than a patch over a bad cut.

Note what QuadRemesher's guide sources actually are: material boundaries and normal splits. In
practice **those are the UV seams** — the same authored discontinuities. QuadRemesher is not solving
UVs; it is aligning topology to the discontinuities the asset already has, and correct UVs fall out.

That reframes the seam problem from "a texture bug" to "a topology-alignment feature we lack", and it
is the same feature as 2.3. One piece of work fixes both.

---

## 4. What to build, in order

Ranked by (gap closed) ÷ (cost), given what is already in this codebase.

### 1. Feature/seam-aligned constraints — *biggest win, and fixes a reported bug*

Detect constrained edges by (a) dihedral angle above a threshold, (b) UV discontinuity, (c) material
boundary. Then:
- constrain the **4-RoSy orientation field** to align to those edge directions, and
- pin the **position field** so a lattice line falls on them.

Instant Meshes — which this codebase already ports — supports exactly this; it is the `extrinsic` /
feature-alignment path we did not port. So this is filling in an omission, not inventing something.

Closes gap 2.3, and fixes the UV bands together with vertex splitting.

### 2. Curvature-adaptive sizing

Replace the scalar target scale with a **per-vertex scale field**, seeded from principal curvature and
smoothed, then propagated through the existing multiresolution hierarchy. Add the Adaptive Size and
Adaptive Quad Count policies on top. Instant Meshes carries a per-vertex scale already, so the
hierarchy plumbing is mostly present.

Closes gaps 2.1, 2.2, and makes 2.4 nearly free afterwards.

### 3. The global integer solve — *already in progress*

Not on QuadRemesher's option list, because it is invisible to the user; but it is what separates 44%
irregular vertices (us, measured) from 11.3% (the reference AI retopology, measured). The min-cost
flow in `MinCostFlow.cs` / `LatticeRepair.cs` is built and solving; what remains is writing the flow
back into `jump[]` and having `LatticeCollapse` read the repaired offsets.

This is the QuadriFlow contribution (Huang et al. 2018, BSD-3) and it is fully published — unlike
QuadRemesher.

### 4. Vertex splitting at UV seams

Independent of everything above and required regardless. Small, self-contained.

---

## 5. Two schools, and which one we are in

Worth stating plainly, because it explains the *character* of the difference and not just the numbers.

- **Field-guided** (Instant Meshes, QuadriFlow, us): grow a smooth direction field, extract a mesh
  from it. Robust on scans and noise, fast, but the loop structure is emergent — nobody ever decides
  where a loop goes, so nothing enforces that loops be long or meaningful.
- **Layout / patch-based** (Mixed-Integer Quadrangulation, motorcycle-graph methods, and by all
  appearances ZRemesher/QuadRemesher): decide a coarse patch structure first, then grid-fill each
  patch. Loops are long and deliberate by construction. Harder, slower, more fragile on bad input.

The reference AI retopology FBX has the layout-school look. We are firmly in the field-guided school,
and the honest read is that steps 1–3 above take field-guided output most of the way to it —
constraints supply the deliberate loops, adaptive sizing supplies the density variation, and the
integer solve supplies the low singularity count.

Going fully layout-based would be a rewrite, not an improvement, and is not recommended.

## 6. Legal note

QuadRemesher and ZRemesher are closed commercial products. I found no patent covering them, but *not
finding one is not clearance* and I am not qualified to give that opinion. Everything recommended in
§4 traces to published, permissively licensed literature — Instant Meshes (Jakob et al. 2015) and
QuadriFlow (Huang et al. 2018, BSD-3) — which is the reason to take that route regardless of how
QuadRemesher does it internally.

## Sources

- [QuadRemesher 1.3 User Documentation](https://www.exoside.com/quadremesherdata/QuadRemesher_1.3_UserDoc.pdf) — the primary source for §2
- [Quad Remesher – New Automatic Retopology Plugin (80.lv)](https://80.lv/articles/quad-remesher-new-automatic-retopology-plugin)
- [Check out neat retopology tool QuadRemesher (CG Channel)](https://www.cgchannel.com/2019/10/check-out-neat-automatic-retopology-tool-quadremesher/)
- [Quad Remesher auto-retopology add-on released for Blender (BlenderNation)](https://www.blendernation.com/2019/10/08/quad-remesher-auto-retopology-add-on-released-for-blender/)
- [Houdini Quad Remesh geometry node](https://www.sidefx.com/docs/houdini/nodes/sop/quadremesh.html) — field-guided framing, curvature-adaptive sizing
- [Rhino QuadReMesh command reference](https://docs.mcneel.com/rhino/8/help/en-us/commands/quadremesh.htm)
- [State of the Art in Quad Meshing](https://www.researchgate.net/publication/279257597_State_of_the_Art_in_Quad_Meshing)
- [Feature Aligned Quad Dominant Remeshing using Iterative Local Updates](https://users.cs.cf.ac.uk/Yukun.Lai/papers/remesh_cad.pdf)
- [A Robust Two-Step Procedure for Quad-Dominant Remeshing (RWTH)](https://www.graphics.rwth-aachen.de/media/papers/rtspqdr_041.pdf)
