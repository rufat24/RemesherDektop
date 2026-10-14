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

## Round 9: helmet rim, speed, window cleanup; Quad Layout on in the window

- **Helmet rim.** The cable and lip along the rim are a fraction of a cell apart. Laid as patch
  borders they gave spikes. The layout now keeps creases a cell apart
  (`IntegerGridMap.layoutCreaseSeparationCells = 1`). Creases whose iso-line constraint crushes a
  triangle under 0.15 cell across are let go and the map solved again
  (`SeamlessParametrization.layoutSquashLimit`, `layoutSquashRounds`).
- **Stray walks.** A Coons blend in space replaces an inner point whose walk lands more than 3 cells
  away. Needle removal no longer runs on layout quads; it had opened a 76-edge hole at a knee.
- **Speed.**
  - The second pass of the face-count search reuses the first pass's singularity cancellations,
    untangled map, loop rows and released creases, in layout mode only (head 5k: 18 s -> 1.6 s).
  - The position field's sweeps are skipped in layout mode, since only its spacing is read.
  - The crease check runs before untangling, so the map is untangled once.
- **Window.** The main section holds QuadRemesher's settings: target count, adaptive size, adapt
  count, detect hard edges, and Quad Layout, which is on. The rest (step-by-step field, profile,
  extractor, Global Parametrization, curvature alignment, patch curves) moved under a collapsed
  Advanced foldout. Nothing was removed.

Single runs. Each cell shows Hausdorff / p05 / irregular vertices, then run time.

| run | Quad Layout | default (param + map) |
|---|---|---|
| body 2k | **0.0115 / 60.2 / 2.82 %**, **8.4 s** | 0.0168 / 59.0 / 3.13 %, 12.1 s |
| body 3.5k | **0.0089** / 63.5 / **1.76 %**, 18.5 s | 0.0117 / **63.6** / 1.91 %, **13.9 s** |
| body 5k | **0.0092 / 62.1 / 1.53 %**, **14.8 s** | 0.0094 / 60.9 / 1.72 %, 28.3 s |
| head 2k | **0.0163 / 45.7 / 4.77 %**, **14.3 s** | 0.0217 / 44.0 / 6.29 %, 19.8 s |
| head 3.5k | 0.0131 / **52.7 / 3.46 %**, **21.2 s** | **0.0122** / 48.3 / 5.06 %, 44.5 s |
| head 5k | **0.0097 / 55.3 / 2.73 %**, **28.4 s** | 0.0106 / 54.2 / 3.65 %, 65.2 s |

Renders: `shots_final/r9_layout_head_3500.png`, `shots_final/r9_layout_rim.png`.

QuadRemesher's add-on exposes, through its settings file, what our window does not yet have:
symmetry (X/Y/Z), material borders as edge loops, split normals as edge loops, and a painted
vertex-colour density map.

Both test models auto-select the Photogrammetry profile: 33-36 % of vertices pass the dihedral
probe. With the layout, the Character profile (300 rather than 60 orientation sweeps) made no
consistent difference. Open meshes (real scans) still take the previous extractor.

## Round 10 (2026-10-14): open borders, symmetry, speed fixes

Harness h9q, window path (Photogrammetry auto, Quad Layout on), single runs.

**Open meshes in the layout** (`QuantizedLayout.borderLayout`, `IntegerGridMap.layoutOnOpenMeshes`):
border vertices get open fans; border edges are laid as patch sides like creases; separatrices end
on them; the outside face is skipped. In the parametrisation the border is held along one grid
direction per run (`layoutBorderRunCells` 1 = chord over a cell; runs shorter than
`layoutBorderMinRunCells` 2 merged, directions carried across faces by transport). On the decimated
mesh, border ears are trimmed (`RemeshPipeline.layoutTrimEarAngle` 90 = all) and bow-tie vertices
split (`layoutSplitBowties`). Patch inner points whose walk from corner 0 leaves the mesh are walked
from the other corners (`walkFromAllCorners`).

| ccnb scan 5000 | Hausdorff | irregular | p05 | open | time |
|---|---|---|---|---|---|
| previous default (open mesh -> lattice) | 0.0191 | 7.28 % | 46.9 | 495 | 49 s |
| layout, round 9 code forced on open | 0.0799 | 5.15 % | 49.1 | 551 | 193 s |
| layout + border (final) | **0.0134** | **5.43 %** | **47.8** | **463** | 68 s |

**Bugs found on the way, all paths:**
- SparseLdlt symbolic pattern was reused after `Assemble()` changed the pattern (released crease
  rows, spiral loop rows): values read through stale slots -> NaN pivots (body 3.5k: 2 failures,
  2 x 8000 CG iterations) or silently wrong solves. Now re-analysed (`refactorOnNewPattern`);
  `factorRetryShift` retries a vanishing pivot.
- Separatrices circling a fold to the 20000-step limit: quadratic hit tests, 60 s a layout on the
  scan. `maxEntries` 3 (same triangle, same direction): scan tracing 10 s -> 0.1 s an attempt.
- A separatrix meeting a crease/border head on truncated the laid line (leaks): laid lines are
  never cut (`alongLaid`).
- `Dictionary<long, double>` keyed i<<32|j without `EdgeKey.Comparer`: every diagonal entry hashed
  to 0 -> quantisation assembly 3.3 s -> 11 ms. Quantisation also solves the augmented system
  (`quantizeAugmented`), same minimum, sparser.

Head/body matrix with all of the above: unchanged except head 3.5k (Hausdorff 0.0131 -> 0.0150 at
the same thin detail, worst point 0.024 -> 0.026; open walks 2 -> 0) and body 3.5k irregular
1.76 -> 1.56 %. quantizeBatch 0.25 tried: mixed on closed meshes, kept 0.1.

**Symmetry X/Y/Z** (`RemeshSettings.symmetry`, window popup): clip at the plane (local origin when
the mesh straddles it), solve the half at half the count with the cut locked onto the plane after
decimation, mirror, weld the seam. head 3.5k: 3.72 % / p05 51.7 / 12.9 s (full 3.61 % / 51.0 /
22.8 s); Hausdorff 0.0235 vs 0.0150 is the model's own asymmetry (input mirrored onto itself: up to
0.040; the remeshed half matches as closely as the full run, mean 0.00087). body 3.5k: 1.64 % /
0.0093 / p05 61.8 / 9.0 s (full 1.56 % / 0.0091 / 62.8 / 19.6 s), open 0.
Renders: shots_final/r10_scan_before_after.png, r10_symmetry_head.png, r10_symmetry_body.png.
Tests 188: 186 pass, 2 skipped (Headless/tests10.xml).

## Round 11 (2026-10-14): Use Materials, scan speed

**Use Materials** (`RemeshSettings.useMaterials`, window toggle; QuadRemesher's option): per-triangle
submesh index read with the mesh (`RemeshTopology.ReadMesh(..., out triangleSubmesh)`); `MaterialLabels`
labels any derived mesh by the nearest input triangle. The decimator keeps label borders
(`DecimateOptions.labels`): border vertices locked in the parallel stage, in the serial stage moved
only along their border onto the next border vertex (none where three labels meet or the border meets
the open edge), planes along the border weighted by length squared like a face's (`labelWeightByArea`;
by length kept nearly every input border vertex: 1650 of ~1800 on the head). Material borders are
added to the refined features (field + lattice) and to the decimated feature edges (parametrisation +
layout patch sides). Output faces labelled, one submesh per material (`faceMaterials`,
`materialSlots`), source materials assigned per slot in the window. FBX export still one material.
Synthetic test, harness `matsplit=axis,value` (head helmet at y 0.75, body waist at y 0.45):

| case | irregular | Hausdorff | p05 | border off the true border (mean / max) |
|---|---|---|---|---|
| body 2k, materials | 2.98 % | 0.0112 | 59.0 | 0.0010 / 0.0060 |
| body 2k, none | 2.82 % | 0.0115 | 60.2 | |
| body 3.5k, materials | 1.69 % | 0.0091 | 61.6 | 0.0013 / 0.0067 |
| body 3.5k, none | 1.56 % | 0.0091 | 62.8 | |
| head 5k, materials | 3.02 % | 0.0104 | 54.3 | 0.0027 / 0.0219 |

**Scan speed:** spiral loop passes skipped on meshes with an open border
(`SeamlessParametrization.layoutLoopPassesOpen` 0): ccnb 5000 68 -> 29 s (old lattice path 49 s),
irregular 5.43 -> 5.14 %, p05 47.8 -> 49.1, Hausdorff 0.0134 unchanged; body symmetric half
1.64 -> 1.58 %. On closed meshes they stay (2): there only body 3.5k changes (1.56 % / 0.0091 / 19.6 s
with, 1.71 % / 0.0098 / 12.3 s without), head and body otherwise identical.
Render: shots_final/r11_materials.png. Tests 190: 188 pass, 2 skipped (Headless/tests11.xml).

## Round 12 — texture bake for scans (2026-10-14)

Per-corner UV transfer on ccnb at 5000 (14,884 faces): the source atlas has 2,197 charts and
6,530 output faces straddle a seam; those are clamped to one side and show stretched texels.
New `TextureBake` (Runtime) + window option **Bake Texture** (on, 4096): the output gets its own
atlas (lattice charts grown across regular quads, cut into 16x16 tiles then solid rectangles,
skyline-packed in four turns) and each texel reads the source texture at the nearest source
point, from a mip level matching the texel ratio. The window reads PNG/JPG files at full size
(ccnb.jpg is 8192 square; Unity imports it at 2048).

Atlas fill: unbounded charts 21 %, seed windows 51 %, + skyline 61 %, + rectangles 70 %,
+ 1024-column skyline 72 % (unwrap 0.3 s).

Rendered against the source (Blender, flat textured, 3000 px, same framing), PSNR:

| | top | oblique |
|---|---|---|
| UV transfer (8192 source) | 25.63 dB | 26.68 dB |
| bake 2048 | 26.38 dB | 28.00 dB |
| bake 4096 (10 s in .NET 8, 24 s in the editor) | 26.50 dB | 28.04 dB |

Renders: `shots_final/r12_bake_top.png` (source / transfer / bake 4096),
`r12_bake_oblique_2048.png`. Window path verified end to end in batch Unity on Assets/ccnb
(texture read 8192, PNG + material written and assigned). Tests 191 / 0 / 2.

Scan geometry, measured this round (not changed): the parametrization keeps the building edges as
grid lines (`shots_final/r12_scan_bends.png`, edges by bend angle), singularities sit at crease
ends and clutter. Left off: global smooth field (p05 49.1 -> 51.3, same singularities), 30 field
sweeps (51.1), relaxation drift 2 (mean move 0.009 cells: the grid is already relaxed),
normal-only Taubin (median bend 1.82 -> 1.57 deg), Adaptive Size 0 at equal faces (p05 57.0,
irregular 4.53 %, but the border loses 1.4 % of the area and the run takes 168 s).

## Round 13 — scan hard edges and border notches (2026-10-14)

Diagnosis on ccnb at 5000: a 100 m wall's foot was kept as a crease over 50 m, its roof edge
over 21 m of 76 m found. The roof edge is crossed every few metres by short noise chains
(downpipes, ragged wall); polylines end at every junction, so it came out as 1-10 m pieces under
the 3-cell (6 m) minimum and was dropped, while clutter (cars, AC units, the junkyard) of 3+
cells was kept.

- `FeatureGraph.joinThroughJunctions` (on): pieces continuing each other within 30 degrees at a
  junction are measured as one stroke. Roof edge kept 21 -> 42 m.
- `RemeshPipeline.scanCreaseMinCells` 8 / `scanCreaseStraightness` 0.85 (Photogrammetry only).
- `HoleFiller.fillOutlineNotches` (on): stretches of the outline loop more than a cell inside the
  input's outline are closed along it and capped. The layout leaves a two-sided border patch
  ungridded now and then (ccnb at 5000 with the new creases: an 8 m notch on the slanted east
  edge; at 10000 already before: Hausdorff 0.0478).
- `QuantizedLayout.suppressBorderSlivers` (on; does not fire on ccnb -- the sliver's sides there
  are both border) and `debugRejectedFaces` (diagnostics).

| | irregular | p05 | Hausdorff | dihedral p90 |
|---|---|---|---|---|
| ccnb 2000 old / new | 6.90 / 6.95 % | 49.3 / 49.0 | .0205 / .0170 | 50.7 / 45.9 |
| ccnb 5000 | 5.14 / 4.96 % | 49.1 / 53.5 | .0134 / .0135 | 48.4 / 45.9 |
| ccnb 10000 | 3.97 / 4.08 % | 57.1 / 55.2 | .0478 / .0134 | 42.9 / 41.6 |
| head 2000 / 3500 / 5000 | 4.77/3.61/2.73 -> 5.37/3.22/2.58 % | 45.7/51.0/55.3 -> 46.5/56.2/57.2 | .0163/.0150/.0097 -> .0170/.0140/.0103 | |
| body 2000 / 3500 / 5000 | 2.82/1.56/1.53 -> 2.52/1.63/1.15 % | 60.2/62.8/62.1 -> 66.6/67.5/68.2 | .0115/.0091/.0092 -> .0107/.0092/.0093 | open 4 -> 0 at 5000 |

Textured render PSNR against the source (bake 4096): old geometry 26.50 / 28.04 dB, new 25.57 /
27.28 (top / oblique) -- the parapet lines come out straight, but the wall in view is more
slanted and its brick shows wider from above. Renders: `shots_final/r13_scan_top_old_new.png`,
`r13_scan_wall_old_new.png`, `r13_scan_textured_src_old_new.png`. Tests 193 / 0 / 2.

Still open: a wall a cell or two tall gets one row of quads, its ends break into a large quad and
a triangle fan; the parametrization's grid still bends a few degrees across flat roofs.

## Round 14 — walls: both edges laid, two rows (2026-10-15)

The 4 m wall at x 68.9 (ccnb, 5000) had a quad row on the wall and the foot row slanting out to
the ground grid line 2 m away: the foot was not a grid line. Traced back:

1. The 50k-triangle pre-decimation rounds the foot off: slivers 4-7 m long run from wall to
   ground, dihedrals 2-32 degrees, so no crease exists there to find.
   -> `DecimateOptions.creaseAngle` (via `RemeshPipeline.decimateKeepsCreases`, on): long straight
   creases of each stage (`decimateCreaseMinCells` 4 of the requested count) get their two face
   planes weighted 1000 x length -- vertices slide along them, not off.
2. Kept that way the foot became a zipper of collinear slivers with normals anywhere.
   -> `PreDecimate.sliverGuardQuality` 0.1: no collapse may leave a triangle of quality under 0.1
   that was better before (48k collapses refused on ccnb; target still reached).
3. What remains is a 0.3 m chamfer: two 45-degree bends, edge by edge mostly under 35.
   -> `FeatureGraph.DetectAtScale` (`RemeshPipeline.creaseScaleCells` 0.25, open meshes only via
   `creaseScaleOpenOnly`): an edge's dihedral between the area-weighted normals of the faces
   within a quarter cell either side; it must still bend a third of the angle itself.
4. `FeatureGraph.bridgeGapFraction` 0.15: crease ends pointing at each other across a gap up to
   0.15 of the minimum length are joined, the gap's mesh edges added.

Foot of that wall (y 40-70) in the solver's crease set: 4.3 m -> 23 m; roof edge 18.9 -> 21 m.
Render: `shots_final/r14_wall_src_r13_now.png` (scan / round 13 / now): two straight rows along
the wall. `r14_top_r13_now.png` from above.

| | irregular | p05 | Hausdorff |
|---|---|---|---|
| ccnb 2000 r13 / now | 6.95 / 5.79 % | 49.0 / 48.3 | .0170 / .0150 |
| ccnb 5000 | 4.96 / 4.78 % | 53.5 / 55.4 | .0135 / .0169 (a 5 m pole flattened) |
| ccnb 10000 | 4.08 / 4.14 % | 55.2 / 56.2 | .0134 / .0072 |
| head 2000 | 5.37 / 4.47 % | 46.5 / 44.3 | .0170 / .0191 |
| head 3500 | 3.22 / 2.77 % | 56.2 / 57.0 | .0140 / .0133 |
| head 5000 | 2.58 / 2.51 % | 57.2 / 59.2 | .0103 / .0115 |
| body 2000 | 2.52 / 1.61 % | 66.6 / 68.9 | .0107 / .0126 |
| body 3500 | 1.63 / 1.38 % | 67.5 / 67.3 | .0092 / .0094 |
| body 5000 | 1.15 / 1.13 % | 68.2 / 67.2 | .0093 / .0077 |

Mean irregular 3.61 -> 3.15 %. Single runs move by a few tenths of a percent and a few
thousandths of Hausdorff with any change (head 2000 with every new piece on: Hausdorff .026,
with any one off .016-.019), so read the table as a whole. Tests 196 (194 / 0 / 2).

## Round 15 — wall ends (2026-10-15), measured, nothing switched on

The messy wall end on ccnb (x 69-74, y 37-44) is a 5 x 9 m tangle of steps and units against
the wall, 2-3 m tall: 217 m of crease in it after detection at scale, 13 irregular vertices
round it in the output, which renders it as a crumpled spike
(`shots_final/r15_wallend_src_now_attached.png`: scan / now / attached rule).

- `RemeshPipeline.scanAttachedMinCells` (FeatureGraph.KeepCoherentCurves' attached strokes):
  keep strokes from 1.5 cells that touch a kept crease, 3 rounds. The tangle comes out as a box;
  mean Hausdorff over 9 cases .0137 -> .0116; but every wall and roof edge also keeps its short
  mounted junk and comes out jagged (`r15_attached_rule_off_on.png`), mean irregular 3.15 ->
  3.37 %, ccnb 5000 p05 55.4 -> 52.1. 2.5 cells: the same, slightly less. Left OFF.
- Taubin smoothing of the solver mesh away from kept creases (5 passes): singular 1411 -> 1130
  but output irregular 4.72 -> 5.04 %, Hausdorff .045, still ~10 irregular at the tangle;
  15 passes broke refinement. Removed.

## Round 16: ccnb at 100,000 quads -- 539 s and a stalled desktop

Reported from the window: ccnb at 100,000 took ~1000 s and lagged the machine. Two causes.

**Speed.** The pre-decimation budget is ten triangles a requested quad, so 100,000 asked for 1M and
ccnb (990,625 triangles) was not decimated at all: the parametrisation solved on the raw scan, and
Adaptive Quad Count raised the target towards 300,000 on top. Harness, .NET, Photogrammetry auto:

| asked | solve mesh | out faces | parametrising | total | irregular |
|---|---|---|---|---|---|
| 10,000 | 100k tris | 29,678 | 30 s + 8 s | 57 s | 4.14 % |
| 25,000 | 250k | 68,377 | 79 s | 111 s | 3.17 % |
| 50,000 | 500k | 140,454 | 170 s | 237 s | 2.33 % |
| 100,000 | 990k (none) | -- | 418 s | 539 s | -- |
| **50,000, capped** | 300k | 92,384 | 89 s | 117 s* | 2.67 % |
| **100,000, capped** | 300k | 93,479 | 76 s | **115 s** | 2.77 % |

\* the test suite ran alongside. `RemeshPipeline.maxSolveTriangles = 300000` caps the decimation
budget, and under the cap Adaptive Quad Count grows the target only as far as the capped mesh carries
at `minSolveTrianglesPerQuad = 3` (the 50,000 run above was fine at 3.6). Below ~30,000 asked nothing
changes. The position-field path is no escape: 25,000 with the parametrisation off took 131 s, 101 s
of it extracting.

**Lag.** `BackgroundScheduler` (the window's job: cores - 1 threads, below normal priority). A
`Parallel.For` given no options does not follow the scheduler it is called from -- 19,585 of 20,000
iterations went to the shared pool -- so every runtime Parallel call now passes
`BackgroundScheduler.Options`. Head at 5,000 on the scheduler: 0 pool work items during the solve,
same output. Tests: BackgroundSchedulerTests.
