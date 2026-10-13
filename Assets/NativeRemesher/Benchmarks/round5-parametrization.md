# Round 5 — global seamless parametrization

The old lattice comes from a local position field. Each edge offset is rounded on its own, and a
min-cost flow repairs the contradictions. That repair is what leaves shear bands, wobble along
creases, and straps that fall between cells. This round adds the method FreeRemesher is built on: one
mixed-integer parametrization solved for the whole surface (Bommes et al. 2009, "MIQ").

Code: `Runtime/Extraction/Parametrization/`

- `SeamlessParametrization.cs`, in pipeline order:
  1. Comb the cross field, cancel dipoles, build the cut graph.
  2. Assemble the least squares system, with fan-closure rows and feature iso-line rows.
  3. Run one local stiffening pass.
  4. Round the poles, then the cut translations, then the feature lines.
- `SparseLdlt.cs`: sparse LDLᵀ with nested-dissection ordering.
- `ParametrizationTransfer.cs`: the system is solved on the decimated mesh and interpolated onto the
  refined mesh, where the existing integer-grid extraction runs.

Flags:

- `IntegerGridMap.parametrization` (false in code; the window's **Global Parametrization** toggle turns it on).
- `IntegerGridMap.parametrizationOnOpenMeshes` (false: meshes with an open border keep the old lattice).

## Measurements (window path, means over targets 4600 / 5000 / 5400)

| model | path | p05 corner | irregular | Hausdorff | orth | loop turn | long faces | time |
|---|---|---|---|---|---|---|---|---|
| body | old | 58.1 | 2.12 % | 0.0090 | 9.1 | 4.92 | 0 | 32.6 s |
| body | param | **62.5** | **1.96 %** | 0.0094 | **7.9** | **4.48** | 0 | **28.8 s** |
| head | old | 49.3 | 4.38 % | 0.0156 | 11.8 | 7.19 | 0 | **50.8 s** |
| head | param | **52.5** | **4.05 %** | **0.0110** | **10.4** | **6.91** | 0.7 | 64.8 s |
| ccnb | old | **45.2** | 6.94 % | 0.0174 | 12.1 | **6.14** | **11.3** | 66.0 s |
| ccnb | param | 43.7 | **6.65 %** | **0.0167** | **11.0** | 6.51 | 14.0 | **47.2 s** |

On ccnb the parametrization makes needle fans at the scan's ragged border. That is why open meshes
fall back to the old lattice by default.

Renders: `shots_final/head_r5_param.png`, `head_r5_neck.png`, `body_r5_param.png`, `ccnb_r5_param.png`.

## Tried and rejected

| variant | why rejected |
|---|---|
| global cross field (Knöppel) | more singular faces |
| pair cancellation by re-solve | noise |
| true-normal polish | noise |
| coarse field sweeps | noise |
| merged rounding | worse |
| separators-last ordering for translations | 17.6 M factor nonzeros |
| three stiffening passes | no better than one, and slower |

## Still open

- Parametric flips remain: about 1,300 on the head and about 4,000 on ccnb, on the coarse mesh.
- Head time: each factorization takes about 2.9 s. It is bound by the top separators, so running the
  subtrees in parallel did not help.

## Round 6: folds at low quad counts (body neck at 2k)

The user reported the body's neck at 2k looking wrong. On the body at 2k, the old extractor beat
the committed parametrization (Hausdorff 0.0129 vs 0.0212). Most of the gap comes from crease
rounding: on the coarse mesh, folds rise from 140 to 321 at that stage.

Two changes, both now default:

- **`featureRollbackRounds = 1`.** A crease line whose integer rounding folds faces within one ring
  is released back to a straight, non-integer line, and the system is solved once more.
- **`cancelPairRadiusCells = 1`.** Opposite singularities up to a cell apart are cancelled along
  the shortest path between them. Step 2b only ever cancelled adjacent pairs.

Single runs, window path. Each cell shows Hausdorff / p05 / irregular vertices.

| run | committed param | + rollback | + rollback + pair cancel | old path |
|---|---|---|---|---|
| body 2k | 0.0212 / 55.1 / 3.68 % | 0.0147 / 56.8 / 3.65 % | 0.0168 / 59.0 / 3.13 % | 0.0129 / 60.5 / 3.07 % |
| body 3.5k | 0.0122 / 62.9 / 2.07 % | 0.0122 / 62.8 / 1.99 % | 0.0117 / 63.6 / 1.91 % | 0.0091 / 59.7 / 2.16 % |
| body 5k | 0.0093 / 61.0 / 2.06 % | 0.0093 / 61.8 / 2.05 % | 0.0094 / 60.9 / 1.72 % | 0.0092 / 56.8 / 2.26 % |
| head 2k | 0.0206 / 42.7 / 7.08 % | 0.0217 / 42.1 / 7.61 % | 0.0217 / 44.0 / 6.29 % | 0.0308 / 41.5 / 6.48 % |
| head 3.5k | 0.0146 / 43.6 / 5.42 % | 0.0128 / 45.1 / 5.28 % | 0.0122 / 48.3 / 5.06 % | 0.0178 / 48.4 / 5.24 % |
| head 5k | 0.0113 / 53.8 / 3.79 % | 0.0087 / 52.9 / 3.76 % | 0.0106 / 54.2 / 3.65 % | 0.0093 / 53.7 / 4.29 % |

At body 2k the collar is now a clean cylinder with a regular ring of quads and keeps its teardrop
outline. Its rolled lip is under one cell at that density, so it is not represented.

Still open:

- Folds from singularity rounding and translation rounding: about 115 on the body and about 580 on
  the head, on the coarse mesh.
- Turning the direct tracer on needs those folds near zero.

## Round 7: where the remaining folds come from

Diagnostic: `SeamlessParametrization.debugDumpPrefix` writes the coarse map, with every corner's
(u, v), at each fold census. Body at 2k, final stage (142 folded triangles by uv orientation):

- The folds sit at singularities: 97 are in a singularity's own fan, 21 one ring out.
- Close singularities cause them. A singularity on a grid node is a whole number of cells from
  every other, so two the field put a fraction of a cell apart round onto one node and their fans
  collapse. The 35 singularities with another within half a cell carry about half the folds. Those
  more than 1.5 cells from any other carry almost none. The head looks the same, at about three
  times the count.
- Index +-2 fans fold even before rounding: 27 of them carry 21 of the free solve's 31 folds. Mod 4,
  a 180-degree and a 540-degree cone are the same index. The field's true signed turn (transport
  plus angle defect, measured against the jumps actually used) tells them apart. It is always
  computed now, and dumped.

Single runs. Each cell shows Hausdorff / p05 / irregular vertices, with coarse-mesh folds after them.

| variant (flags) | body 2k | head 3.5k |
|---|---|---|
| default | 0.0168 / 59.0 / 3.13 %, 117 | 0.0122 / 48.3 / 5.06 %, 569 |
| no pole rounding | 0.0182 / 57.6 / 4.08 %, 103 | 0.0130 / 47.0 / 4.74 %, 479 |
| consistent rounding (closures exact) | 0.0155 / 50.3 / 3.31 %, 359 | |
| split +-2 into two unit singularities a cell apart | 0.0163 / 60.0 / 3.66 %, 150 | 0.0173 / 49.5 / 4.94 %, 641 |
| spread close same-sign pairs a cell apart (`spreadSingularities`) | 0.0123 / 56.5 / 3.96 %, 185 | 0.0139 / 49.4 / 5.06 %, 737 |
| Knöppel field on the refined mesh (`globalField`) | 0.0229 / 57.0 / 3.84 %, 133 | 0.0141 / 44.9 / 5.63 %, 795 |
| 10 coarse sweeps (`parametrizationFieldSweeps`) | 0.0212 / 55.8 / 3.60 %, 137 | 0.0131 / 48.2 / 4.92 %, 608 |
| Knöppel field on the decimated mesh (`parametrizationGlobalField`) | 0.0177 / 56.9 / 4.27 %, 157 | |
| 20 decimated triangles per quad (default 10) | 0.0153 / 61.6 / 2.80 %, 162 | 0.0162 / 53.4 / 4.29 %, 816 |

None is adopted. Each change to the singularity layout adds folds elsewhere. On wrinkled cloth the
smoothest field itself winds round every bump, so the noise is in the surface, not the solver.

### Direct tracing with closed seams

With soft closures (weight 30), rounding leaves about 100 vertex closures open by whole cells. On
the map path that only distorts. To the tracer it is a dislocation, and it leaves odd-length holes
that no quad patch can close. With `closureWeight = 1e5` and `consistentRounding`, every closure
holds and the holes are even. New in `ParametrizationTrace` (`fillHoles`, off by default):

- A rim whose corners add up to a whole turn is a lattice polygon. Its unit cells are rebuilt as a
  grid patch, with the inner vertices placed by Laplace.
- Other even rims of 6 to 10 edges get a fan of quads round one new vertex: valence 3, 4 or 5.
- Longer even rims are first cut by a chord.
- Doublets (valence-2 vertices) are merged, but only where the vertex lies on the merged quad's
  diagonal.

Tracer + hard closures + consistent rounding + fillHoles (each cell: Hausdorff / p05 / irregular):

| run | tracer | default |
|---|---|---|
| body 2k | **0.0105** / 56.7 / 5.13 % | 0.0168 / 59.0 / 3.13 % |
| body 3.5k | **0.0100** / 59.4 / 3.53 % | 0.0117 / 63.6 / 1.91 % |
| body 5k | 0.0094 / 62.4 / 2.74 % | 0.0094 / 60.9 / 1.72 % |
| head 2k | 0.0206 / 28.8 / 11.79 % | 0.0217 / 44.0 / 6.29 % |
| head 3.5k | 0.0138 / 34.0 / 9.58 % | 0.0122 / 48.3 / 5.06 % |
| head 5k | 0.0102 / 50.7 / 6.64 % | 0.0106 / 54.2 / 3.65 % |

On the body the tracer has the best Hausdorff at low counts, and the vest hem and belt come out as
straight loops. On the head its 460 to 620 folds become holes, so it stays off. What would make it
the default is a fold-free map: the robust route in the literature is quantizing a seamless map's
quad layout (Campen et al. 2015, "Quantized Global Parametrization"; QuadWild Bi-MDF 2023), which
places close singularities by integer arc lengths instead of rounding each one alone.

## Round 8: quantized quad layout (`IntegerGridMap.parametrizationLayout`, window: "Quad Layout (experimental)")

The remaining folds come from rounding singularities one by one (round 7). This round adds the route
of Campen, Bommes and Kobbelt, "Quantized Global Parametrization" (2015), in
`Runtime/Extraction/Parametrization/QuantizedLayout.cs`:

1. **A fold-free free map.** Barrier untangling (Garanzha et al. 2021, "Foldover-free maps in 50
   lines of code") on the seamless but not yet integer map. Closures and creases are held hard
   (weight 1e5) in this mode. Body 2k and head 3.5k come out with 0 folds.
2. **Layout.** The motorcycle graph of separatrices on that map, with creases laid in advance as
   arcs, and continuations at crease ends and corners. Faces that are not rectangles get another
   line and a re-trace.
3. **Twist.** A separatrix that spirals round a cylinder (arms, legs) has its sideways shift held
   to whole cells by a hard row (zero when under a cell), and the map is solved and untangled again.
   Limbs get closed rings.
4. **Quantization.** An integer length per arc, each rectangle's opposite sides equal, none
   negative. This is done by batched least squares with propagation, and leftover unequal rows are
   repaired by augmenting paths. No rectangle is left unequal.
5. **Meshing.** Every rectangle becomes a W x H grid. Inner points come from a Coons blend of its
   sides, walked across the free map to the surface. Zero-length arcs and flat patches glue their
   points.

Single runs. Each cell shows Hausdorff / p05 / irregular vertices.

| run | layout | default (param + map) | old path |
|---|---|---|---|
| body 2k | **0.0108** / 57.9 / **2.59 %** | 0.0168 / 59.0 / 3.13 % | 0.0129 / **60.5** / 3.07 % |
| body 3.5k | 0.0109 / 62.3 / 1.94 % | 0.0117 / **63.6** / **1.91 %** | **0.0091** / 59.7 / 2.16 % |
| body 5k | 0.0116 / 60.7 / 1.86 % | 0.0094 / **60.9** / **1.72 %** | **0.0092** / 56.8 / 2.26 % |
| head 2k | **0.0177** / **44.5** / **5.08 %** | 0.0217 / 44.0 / 6.29 % | 0.0308 / 41.5 / 6.48 % |
| head 3.5k | 0.0145 / **50.5** / **3.74 %** | **0.0122** / 48.3 / 5.06 % | 0.0178 / 48.4 / 5.24 % |
| head 5k | **0.0093** / 52.2 / **3.00 %** | 0.0106 / **54.2** / 3.65 % | 0.0093 / 53.7 / 4.29 % |

Renders: `shots_final/r8_layout_head_3500.png`, `shots_final/r8_layout_body_2000.png`.

Still open:

- Thin crease structures (the helmet rim) give glued patches and a few slivers.
- A few faces per mesh are still not rectangles and are capped by the generic hole filler.
- Time is 13-43 s against 14-65 s for the default path. The second pass of the face-count search
  reuses the untangled map only when spacing scales uniformly.
- Off by default.
