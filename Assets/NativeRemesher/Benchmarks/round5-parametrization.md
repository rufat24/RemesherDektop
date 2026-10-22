# Round 5 — global seamless parametrization

The old lattice comes from a local position field. Each edge offset is rounded on its own, and a
min-cost flow repairs the contradictions. That repair is what leaves shear bands, wobble along
creases, and straps that fall between cells. This round adds one
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
- **Window.** The main section holds the quad settings: target count, adaptive size, adapt
  count, loops along creases, and Quad Layout, which is on. The rest (step-by-step field, profile,
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

Settings the window does not yet have:
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

## Round 11 (2026-10-14): Material Borders As Loops, scan speed

**Material Borders As Loops** (`RemeshSettings.materialBorders`, window toggle): per-triangle
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
normal-only Taubin (median bend 1.82 -> 1.57 deg), Curve Detail 0 at equal faces (p05 57.0,
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

## Round 17: the head against the user's reference (heradremesh.fbx) -- Detail Quads

The reference, 6,628 faces, is quad-dominant (627 triangles, 17.4 % irregular) and keeps the rail,
slots and screws. Our lattice output at the same count melts them. Round the rail our cells are as
dense as the reference's; the difference is placement -- its vertices sit on the rims and corners.

Two pieces, both off by default:

- `FeatureFit` (`RemeshPipeline.featureFitIterations`): each output vertex moved to the minimum of
  the quadric of the input triangles nearest it, unconstrained directions left free (dual
  contouring's placement), capped at half an edge, no face turned over. On the lattice output
  (C, 6,732 faces): in->out p99 0.872 -> 0.483 % (reference 0.422), out->in p99 0.469 -> 0.316 %.
- `DetailQuads` (`RemeshPipeline.detailQuads`, window: Detail Quads): quadric simplification to
  0.42 triangles a quad, triangles paired into quads, one Catmull-Clark split, points onto the
  input, FeatureFit. All quads, 4 s on the 1.5M-triangle head (the lattice: 56 s).

| head | faces | out->in mean / p99 | in->out mean / p99 / max | normal err mean | irregular |
|---|---|---|---|---|---|
| reference | 6,628 | 0.138 / 0.398 | 0.171 / 0.422 / 0.909 | 13.3 | 17.4 % |
| lattice C | 6,732 | 0.138 / 0.469 | 0.210 / 0.872 / 1.398 | 17.8 | 3.8 % |
| lattice C + fit | 6,732 | 0.122 / 0.316 | 0.167 / 0.483 / 1.210 | 14.4 | |
| Detail Quads | 6,336 | 0.127 / 0.313 | 0.167 / 0.448 / 1.043 | 12.9 | 25.9 % |

By eye (`shots_final/r16_head_detail_quads.png`) it is still below the reference: the straps, the
rim and the rail's outline come through, the slots and screws do not, and the quads are kites
(min angle p50 44 against 75). Distance numbers at 0.1-0.5 % of the diagonal do not see a 3 mm slot.
Tried and not better: curvature-weighted quadrics (power 1-3), crease-kept simplification, no split
(quad-dominant on a 4x denser base: greedy pairing leaves 20-25 % triangles).

## Round 18: screws and slots -- what was tried, what held

Goal: the rail's slots and screws in the head at ~6,600 faces, as the reference has them.

- **The reference's density is not curvature.** Within 4 mm the sculpt's normals turn 37 degrees at
  the median point (fabric, panel lines, thin shells); at the reference's densest spots 30-40. No
  curvature sizing separates them. Full-strength curvature sizing on the lattice (adaptivity 1,
  ratio 8; spacing 0.007-0.16) left the rail as dense as before, 305 s, one hole.
- At several of the reference's densest spots it does not follow the source at all (round cups
  where the source has shallow raised squares); spot counts are not a target.
- **Detail Quads, sharp-edge weighting** (`DetailQuads.edgeWeight`, planes within 0.3 cell of a
  30-degree edge x30): the simplification spends its budget on hard-surface rims instead of the
  fabric. Visibly better helmet (`shots_final/r18_head_edge_weighted.png`); numbers: out->in p99
  0.337 %, in->out p99 0.465 %, max 0.882 % (reference 0.398 / 0.422 / 0.909). Default on.
- Not better, left off: a size limit in the simplification from the normals' turn
  (`featureSizeRadiusCells`; the fabric limits everything), crease chains kept through the
  simplification (`creaseAngle`, now actually reaching the decimator), relaxing all points, no
  split with looser pairing (crumpled), small-part sizing on the lattice by group extent
  (`RemeshPipeline.smallPartSizing`: 1,249 parts, a hole in the rail) and by gap between rims
  (`smallPartGaps`: 9,254 seeds, fabric again; GameAsset crashed in QuantizedLayout.Build).
- GameAsset + all decimated creases + FeatureFit (L0) is the cleanest lattice result -- grid lines
  along the rail's rims -- but slow at a budget of 40 a quad (264k-triangle solve, 540 s).

Not solved: a slot or a screw a centimetre across needs cells under half a centimetre there and
grid lines along its rim; nothing here produces both.

## Round 19: normal map bake -- the detail the quads cannot carry

The user's call: remesh what the quads can hold, and give the rest to a normal map.

`TextureBake.BakeNormals` / `RemeshPipeline.BakeNormalMap` (window: Bake Normal Map, on): the output's
own atlas (Unwrap), per (vertex, UV) tangents (`TextureBake.Tangents`, written into the mesh by
ToMesh so the shader decodes in the frame the bake used), and per texel a ray along the interpolated
normal, both ways, 1.5 mean edges, front-facing source triangles only (`SpatialGrid.Raycast`); the
source's smooth normal at the hit in (T, N x T x w, N). Green is +V. Head at 3,000 (7,844 faces,
100 % quads), 2048 px: 2.4 s; texels without a hit 92k -> 21k (front-facing) -> 2.5k (reach 1.5).

Checked in Unity itself (batch-mode render through the Standard shader with the mesh's tangents,
`tools/headless/RenderCheck.cs.txt`): `shots_final/r19_normalmap_unity_full.png`,
`r19_normalmap_unity_rail.png` -- slots, screw, S-hook, rail rows, earmuff knob and buckles read as
in the 1.5M-triangle source. The Blender previews show thin lines at chart borders only because
Blender computes its own tangents. Weak: the cable, under a cell thick, is not in the low poly and
comes through only in part.

## Round 20: normal map on body and ccnb; Paint Fix Areas

Normal map through Unity's Standard shader (`RenderCheck`, views now from `RC_VIEWS`):
- body at 5,000 (9,085 quads, 2048 px): vest plates, MOLLE rows, straps and pockets read as in the
  source (`shots_final/r20_body_paint_before_after.png`, "before" columns).
- ccnb at 100,000 (92,981 faces, 4096 px, 41k of the used texels without a hit): roof panel seams,
  rooftop units and the wall's window rows come back (`shots_final/r20_ccnb_normalmap.png`).

Body's collar rim and back straps: every output vertex is on the source (worst 1.1 mm), but the
lip and the straps are thinner than a cell (~1.5 cm), so the lattice samples them unevenly -- horns
on the rim, wavy strap edges. Not a fold or a tear: too few quads there.

Paint Fix Areas (window, Fix Areas section): a Scene-view brush, on the result or the source, kept
as dabs in the source's space (`DensityPaint`, `RemeshPaint` component) so it survives any remesh.
The sizing field is the unpainted one times `range^-value` (3x finer at full paint), graded again
(`ScaleField.Painted`), and the target is raised by the quads that adds (`paintCountRatio`) so the
count control does not take them back from the rest; the pre-decimation budget grows by the same
area ratio and the paint weights its quadrics. Selecting the result and pressing Remesh runs the
source again and hides the old result. Body, neck rim and both back straps painted: ratio 1.29,
9,085 -> 12,443 quads; the rim is round with its lip, the buckles are blocks.

Fixed on the way: `TextureBake.Tangents` normalised its area-weighted sum with Vector3.Normalize,
which returns zero under 1e-5 -- every corner of the painted millimetre quads (153 vertices) had a
zero tangent and shaded black.

## Round 21: SurfaceClean -- lone spikes and folds

User, after painting: arms still look distorted, a small pop-out at the collar. Measured: arm quads
are as regular as the legs' (corner angle off 90 median 6.0 / p90 15.3 deg; legs 6.0 / 12.7) -- the
arm lumps are the sleeve folds, several cells wide, which the grid follows. The collar pop-out is a
lone vertex half an edge out of its ring (umbrella offset -0.50, neighbours ~0.03).

`SurfaceClean` (`RemeshPipeline.surfaceCleanIterations = 2`, 0 = off): a vertex further than 0.3
edges from its ring's centre AND 2.5x its neighbours' own offset, or a corner of a face pointing
against its neighbours, goes to its ring's centre. Body (painted) 76 -> 10 lone spikes, 2 -> 0 folds;
head at 3,000: 94 -> 18 spikes, 39 -> 29 folds, renders unchanged elsewhere
(`shots_final/r21_head_clean.png`, `r21_body_collar_clean.png`). 0.1 s.

Tried and dropped: each vertex onto the source averaged over its ring plus tangential relaxation
(no visible change on the arms, spikes 19 -> 54); smoothing a fold's whole ring (head 35 folds
after four passes). The head's remaining folds are where the chin cable, thinner than a cell,
meets the helmet rim.

## Round 22: review fixes (branch remesher-fixes)

A review of the paint, cleanup, count search and bake; each fix behind a flag with the old
behaviour kept. Measured with the console harness (head 3,000 / body 5,000 / ccnb 5,000 unless said).

1. **Less Detail paint raised the count** (`RemeshPipeline.adaptiveCountIgnoresPaint`): Adapt Quad
   Count read "flat" off the painted field. Sphere, a quarter painted Less at 800: 826 -> 2,160 faces,
   the unpainted rest 3x denser. Now 685 faces, cap 69 -> 37 per area, rest 61 -> 59.
2. **SurfaceClean cut sharp tips** (`surfaceCleanUsesSurface`, `SurfaceClean.tipMinOffset`): a cone's
   apex went down a whole edge. Kept now when >= 0.7 edges out of its ring and the solve surface rises
   with it. Most real lone spikes (the collar's included) are ring-scale source shape at 0.3-0.57
   edges, so they are still cleaned: body 56 -> 10 (9 before), head unchanged. Putting moved vertices
   back on the surface undid the cleanup (body 9 -> 38 spikes) and is off.
3. **Symmetry lost paint on the cut-away half** (`symmetricPaint`, `DensityPaint.Mirrored`).
4. **Undo/redo left the paint overlay stale** (window).
5. Count search: **the layout's first attempt starts where it lands** (`layoutCountAtSpacing` 0.86):
   head 13.4 -> 12.2 s, ccnb 26.7 -> 21.7 s, one pass instead of two.
6. **Layout rounding batch** 0.1 -> 0.5 (`QuantizedLayout.quantizeBatch`): ccnb rounding 2.5 -> 0.7 s a
   pass, irregular and chamfer within noise.
7. **Ridged collapse placement** (`PreDecimate.ridgePlacement`, OFF): the plain 3x3 solve falls back
   on 99.8 % of collapses. Ridged, the decimated surface is twice as close (head mean 0.0173 ->
   0.0082 % of the diagonal), but the remesh on it is less regular (head 3.41 -> 3.67 %, ccnb
   4.66 -> 5.37 % irregular) for a 3-15 % lower chamfer. Left off.
8. **Inside-out patches** (`FaceOrienter.flipInvertedPatches`): the head's ~60k unhit normal-map
   texels in Unity were a 114-face patch on top of the helmet wound inside out -- the extracted
   surface has a twist and the consistency walk left its seam round the patch (46 of 54 border edges).
   Patches pointing against the surface are reversed when their border gets no less consistent.
   Unity head: missed texels 54,985 -> 1,831. Unity's runtime solves a slightly different lattice
   from the console harness (7,758 against 8,124 faces on the head), which is why it showed there.

Tests (Unity, EditMode): 212, 210 passed, 2 skipped.

## Round 23: straps as tubes (branch remesher-band-structure)

The helmet's chin straps are free-standing bands, about 2 cells wide and 0.25 cell thick, standing
off the cheek (plane sections of the source). The decimated mesh keeps them as tubes; the quad
layout cuts across them, so they came out as slivers melted into the face. The reference meshes each
as a rectangle swept along it.

RibbonParts (RemeshPipeline.ribbonParts; the window's "Straps As Tubes", on by default there):

- **Find.** The thickness map (an inward ray per vertex) gives the thin plates. Each plate is split
  into runs of steady width and direction. A run counts as a ribbon when it is long, narrow and free
  along both rims.
- **Mesh.** Each ribbon becomes a quad tube cut ring by ring from its own sections, at the output's
  edge length.
- **Rest.** The rest is solved without the ribbons, and the holes are zipped shut.

Console harness, Photogrammetry/GameAsset auto, layout on:

| mesh | ribbons | faces | irregular | chamfer | hausdorff |
|---|---|---|---|---|---|
| head 3,000 off | - | 8,105 | 3.40 % | 0.00104 | 0.01294 |
| head 3,000 on | 9 (448 quads) | 7,902 | 3.56 % | 0.00101 | 0.01531 |
| body 5,000 on | 0 | unchanged | | | |
| ccnb 5,000 on | 0 | unchanged | | | |

Detection costs about 1.1-2.5 s per run.

Unity, head at 3,000 with ribbons on: 7,986 faces. The bake missed 20,305 texels, against 1,831 with
ribbons off. The extra misses are on 52 faces that are hidden: tube ends pushed into the part they
attach to, and the closed-up spots inside the tubes. Rays from there only find the inside of the
source. Renders show none of them.

Tests (Unity, EditMode): 214, 212 passed, 2 skipped.

Still open:
- The buckle between the straps stays a blob, since it is a thick part smaller than a cell.
- The front chin strap is not recognised, because it merges into the buckle's plates.
- The back straps have small torn tips.

## Round 24: straps lying on the surface, forks, buckles (branch remesher-band-structure)

Feedback on round 23: "the buckle is still a blob, and it looks ugly". Plane sections of the
reference showed its back straps are closed rectangular tubes lying on an unbroken helmet surface --
the same model as ours -- but in our source the back straps are fused to the shell, so the inward
thickness ray only caught the 5 cells where one bridged a step.

- Raised bands: rays sideways just under a band's top (walls near both ways and square to the ray,
  long the other way, ground beside within 0.6 cell); its height stands for the thickness. Limited
  to 1.75 cells wide (the helmet's back panels, ~2 cells, keep going through the lattice).
- Runs against their own median width; no cut where a strap folds over a rim.
- Forks: two straps leaving a buckle fill one distance bin side by side; the plate is cut at a fork
  lasting 1.5 cells and each piece measured alone (the far chin side came back).
- Regular box tubes from the median of the part's sections; line ends straightened.
- Buckles: two attached ends within 3 cells -> surface fitted (quadric, robust), buckle top boxed
  (min-area rectangle, median top, into the surface; at most 1.2 cells high and 3 across).
- No bottoms on lying tubes and boxes (inside the surface).

Head 3,000 (harness): 8 ribbons + 2 buckles. irregular 3.40 -> 3.42 %, chamfer 0.00104 -> 0.00100,
hausdorff 0.0129 -> 0.0191 (worst points at the buckle box inside the cheek). Body 5,000 and ccnb
5,000: nothing found, identical output. Detection 2.6-4 s.
Unity: EditMode 215, 213 passed, 0 failed, 2 skipped. RenderCheck head 3,000: 8,072 faces, missed
texels 39,928 (round 23: 20,305; 45,447 with closed bottoms) -- hidden faces.

Tried and left off by default: both-sided ground (hausdorff 0.040, eight false buckles), splitting
every non-band-shaped thin region (chin cup cut into jagged pieces), boxes at single attached ends
(sliders boxed but the strap beyond them still melts).

Still open: straps through sliders (the strap stops at the slider, the rest melts -- the piece
beyond is a separate plate, rejected); the chin cup (a curled thin shell, not a band); the back
straps' wrap under the rim; a lattice fold on the helmet front that comes and goes with the count.

## Round 25: straps through sliders (branch remesher-band-structure)

A slider is thicker than its strap: the strap's plate stops there and the piece beyond is its own
plate, too short to pass (or, for the strap up to the helmet, attached). Now: short pieces are kept
as candidates (short plates too, one run each); an attached ribbon end links to a piece or ribbon
ahead within 4 cells (past 2.5 cells both directions within 32 deg), width ratio <= 1.4, not where a
third ribbon ends (buckle); repeated, so a strap can pass several. The slider is boxed in the
strap's frame from source points facing away from the strap (the cheek beside it faces towards it)
and its length taken where the part stands out of the strap's section; tube quads inside the box
dropped. Rejected on the way: linking long attached pieces (the chin cup's rim ran through a buckle,
hausdorff 0.048), 0.5-cell pieces without a direction check (the helmet rim cable chained, chin
pieces linked through the buckle: hausdorff 0.029, irregular 4.34 %).

Head 3,000: 4 links (side straps through the top sliders on to the helmet, jaw straps through the
jaw sliders round the neck), 2 buckles. irregular 3.53 %, chamfer 0.00101, hausdorff 0.0182. Body and
ccnb: nothing found, identical.

Also fixed: in Unity's runtime only (it solves a slightly different lattice), the head with its
straps taken out crashed QuantizedLayout.Build with an IndexOutOfRange building arc points (an arc
with a negative length or a missing end node). Such a layout is now dropped (stats.invalidArcs) and
the solve goes on without it. Unity RenderCheck head 3,000 after: 8,212 faces, 100 % quads, missed
texels 49,767 (hidden: inside sliders, buckles and the surface). EditMode 216: 214 passed, 0 failed.

## Round 26: the strap finder no longer depends on how the model is turned (branch remesher-band-structure)

Check: the head turned about all three axes (and moved), and scaled x7.3, run as the head. Scaled:
the same 8 straps. Turned: 13 straps instead of 8 -- the raised-band test leaned on the world axes.
Its sample points were one per world voxel with normals bucketed by their largest world axis, and
the way across a band was the best of four rays round a world-axis tangent (a band at 22 degrees
to them read 8 % wider). Several plates sat right at a threshold (a cable found on one side of
the helmet and not its mirror twin; a piece at 0.69 free against 0.75) and flipped.

Now (invariantRelief): sample points picked by distance on the mesh, in index order, each vertex
taking the nearest within 0.2 cells whose normal is alike; the narrowest way across searched (eight
rays, then four halving steps). Thin and raised vertices then agree to 0.2 % however the head is
turned, and the mirror pairs come out in pairs (both helmet-rim cables, both front-rim bands -- the
front rim, crumpled before, is now two tubes, close to the reference).

Head 3,000, four poses (as built / turned / scaled / turned and scaled):
- before: ribbons 8 / 13 / 8 / 12, hausdorff 0.0182 / 0.0386 / 0.0181 / 0.0203.
- after:  ribbons 10 / 9 / 10 / 9, hausdorff 0.0182 / 0.0188 / 0.0192 / 0.0165, chamfer 0.00104 /
  0.00111 / 0.00112 / 0.00103, irregular 4.01 / 4.75 / 4.74 / 3.58 % (3.53 % before, as built).
The 10-against-9 left: where the band under the chin and one side strap's plate join their
neighbours through a few faces at the thickness threshold.
Head 1,500: 4 ribbons, irregular 7.40 % (6.34), hausdorff 0.0295 (0.0301). Head 6,000: 9 ribbons,
irregular 2.53 % (1.94), hausdorff 0.0280 (0.0173) -- a fan in the lattice on the neck, far from any
strap; the same head scaled finds the same 9 straps and has no fan (0.0140), turned 0.0124: the
lattice's own sensitivity, not the straps. Body 5,000 and ccnb 5,000: nothing found, identical.
Unit test: RaisedBandIsFoundTheSameHoweverTheModelIsTurned (fails on the old test: 1,213 raised
vertices turned against 1,252).

The chin cup, tried and left off (all flags, off by default): it is no curled shell but a strap
loop -- a low raised band across the front of the chin (0.18 cells high, its edges rounded and
curling under) and a band under the chin standing off it, joined at both corners where the jaw
straps come in. What stopped each part, and what was tried:
- the raised band's detection is patchy (rays start 0.04 cells under a sculpted top; a bump stops
  them): fillThinHoles (holes of up to a square cell inside a thin part filled) and reliefDepths
  (also 0.1 and 0.2 cells under). Raised vertices 50k -> 113k on the head; panel edges and rims
  everywhere start to pass.
- the band under the chin and the front band's undercut lip make one ring, measured as one line
  (width 3 cells): forkArmFraction 0.05 (a thin arm counts at a fork).
- the front band read as attached (free 0.50): its frame tilted 20 degrees by the curled rims
  (robustPlateNormal) and its centre near the ground, so the ray across passed under one rim
  (rimHeights: rims looked for just under the faces). With these it passes -- and so does the
  balaclava's hem round the neck (a step from the thick hem to the fabric reads as a rim); limited
  to bands with no far side for most of their length (lyingRunFraction 0.5) the chin band (45 %
  raised, the fabric under it is thin) fails again; at a third it passes, but that number came
  off this one model.
- every set that passes the chin band measured worse on the head as a whole (hausdorff 0.025-0.050,
  irregular 4.8-10 %): more straps taken out leave holes the hole closing gets wrong. A band taken
  out all round a closed line leaves a ring-shaped hole, each loop filled as a disc (an 18-cell fan
  across the helmet's opening); crossing straps leave regions with up to 24 loops, the patches
  between them filled over. stitchRings (two loops of one region stitched to each other) and
  flattenLying (lying straps' faces kept and pressed flat by over-relaxation instead of cut out)
  are written but not enough yet: the head's big strap network counts as free-standing.
Next for the chin cup: hole closing that handles regions with many loops (flattening for any
region on the surface, not by a majority of lying faces), then the chin-cup flags measured again on
the head turned and as built.

## Round 27: models in parts; the colour bake seen from outside (branch remesher-band-structure)

Models in parts. The window took the first mesh under the selection: an imported model is a root
over several meshes, and the rest were dropped (the user's Porsche: 43 parts under three empty
parents; the Sketchfab helmet: 3 meshes). Now every part is found -- active, renderer on, of a LOD
group only LOD 0, never an earlier result -- and each is remeshed on its own: the target split by
surface area in the model's space (the same quad size on all parts), at least 20 quads per
connected piece of a part (at most half its triangles), the result mirroring the hierarchy under
one `<root>_Quads` with all files in `Output/<root>_Quads/`. One atlas for all of them
(PartBake): the parts' results joined in the root's space, unwrapped and baked once, each part
taking back its faces' UVs and its tangents in its own space -- one texture and one normal map at
an even texel density. A part that throws or comes out empty is tried again at 4x, then kept as it
was and named in the status. Combine Parts joins them into one mesh in the root's space instead.

Measured through the window's own code in headless Unity (scratch driver, not in the repo):
- Sketchfab helmet, 3,000: 3 parts, 11,253 faces (11,233 quads), 22 s; one shared 2048 normal map
  at 81 % fill. The studs part (ten pieces, 0.16 % of the area) got 16 quads first and came out
  empty -- hence the per-piece floor (200 quads: 216 faces).
- Porsche, 8,000: 43 parts, 25,864 faces (25,705 quads), 85 s of solves, 146 s in all. Every part
  out ('mirror int' threw "no usable triangles" at 20 quads, passed at 80). The car itself is poor:
  the interior (seats, dash, doors' insides) takes its area's share of the budget, and the open
  hard-surface panels tear and crumple at a few hundred quads each -- not the parts handling.
- main1.obj (one mesh), 5,000: 7,557 faces, all quads, 1.43 % irregular, hausdorff 0.0088.

Fixed on the way: InputRefine.Split took its twins from the welded topology but its faces from the
input; welding drops collapsed triangles (the helmet's studs at Unity's 0.01 import scale), and
the numbering then ran past the end. It now splits the welded arrays when any triangle was dropped.

The colour bake (ccnb, "texture quality very low"). Flat ground was as sharp as the source; what
was scrambled were small raised things -- cars, trucks, a shed -- because each texel read the
source at its NEAREST point, which over a coarse lump is a side wall or the ground beside it as
often as the top. Now (TextureBake.rayColour) each texel reads where the result's normal line,
coming in from 1.5 mean edges outside, first meets a source face turned towards it -- a baker's
cage -- and the nearest point only when it meets none. ccnb 5,000 at 4096: cars and trucks read
as in the photo, 11,045 of 12.7M samples fell back, bake 9.5 s -> 2.0 s. Left: pale halos where a
coarse slope runs from a roof to the ground (the normal tilts, the line sees the wall). Unit test
BakeSeesARaisedBlockFromOutside (the nearest point reads the wall there). The baked albedo and
normal map are also imported at high quality compression now (BC7 / BC5 on desktop; BC1 blocked a
photo up close).

Tests: .NET runner 30 passed (two need Unity's Mesh); Unity EditMode 220: 218 passed, 0 failed,
2 skipped.

## Round 28: wavy building edges on scans -- the layout's lines kept through the smoothing passes (branch remesher-band-structure)

ccnb's "wobble" (wavy roof edges, walls shading as zig-zag trenches, the baked texture's wall
rims wavy from above) is not from the recent strap or bake commits: ccnb at 5000 through the
pre-strap commit 3aab266 gives the same METRIC line to the digit as HEAD (the strap finder finds
no ribbons on ccnb), and the round 14 renders show the same staircase.

Measured with a new number: of the creases the parametrisation lays as grid lines (the interior
ones -- building edges -- not the border), how much of their length lies more than 0.3 cell from
any output edge (scratchpad wobble.py on the harness's .pfeat.obj). ccnb 5000: 10.5 % after the
quad layout, 47.1 % in the final mesh. Two later passes moved the rows off the lines:

- SurfaceRelaxation: the scan profile has featureAngleDegrees 0, so its crease test is off and
  every vertex is smoothed freely, including the rows the layout laid on the edges (relaxation
  off: 12.3 %).
- SurfaceClean: a vertex on a parapet looks like a spike to it (SurfaceClean off: 7.2 %).

`IntegerGridMap.relaxAlongLayoutLines` (on): output vertices within `layoutLineToleranceCells`
(0.3) of one of the layout's lines (`ExtractionOptions.layoutLines`, the parametrisation's feature
edges; `Extraction.LineSet`) slide only along that line in relaxation (kept on it, not merely
moved along it), are held where it turns or ends, and are left alone by SurfaceClean
(`QuadExtractionResult.onLayoutLine`). `layoutLineMoveOnto` (off) also moves them onto the line
first -- measured no better. Same topology in every case (face and irregular counts identical).

| 5000, window path | edge length > 0.3 cell off, before -> after | inputToOutput mean | other |
|---|---|---|---|
| ccnb | 47.1 % -> 9.9 % | 0.147 -> 0.131 | chamfer 0.00034 -> 0.00030, Hausdorff same, turn p90 11.6 -> 12.5 deg |
| head | 20.5 % -> 10.6 % | 0.138 -> 0.136 | Hausdorff 0.0427 -> 0.0484: the chin cup's zipped patch, 4-5 cells off the surface in both |
| body | 11.9 % -> 2.1 % | 0.101 -> 0.101 | Hausdorff 0.0090 -> 0.0091 |
| main1 | 19.6 % -> 5.7 % | 0.068 -> 0.065 | Hausdorff 0.0088 -> 0.0086 |

Tolerances 0.05 / 0.15 / 0.25 / 0.35 on ccnb (relaxation only): 37 / 31 / 21.5 / 19 %; with
SurfaceClean holding them, 0.25 / 0.35: 11.0 / 8.4 %. 0.3 stays under half the smallest graded
cell, so two parallel rows never take the same line. Unit test
VerticesOnAGivenLineSlideAlongItAndStayOnIt.

Looked at and NOT the cause, recorded so it is not chased again:

- Model size. The Sketchfab helmet comes in 9 mm tall (Unity bakes the 0.01 import scale into
  the vertices); the harness's QUAL turn reads 15.3 deg mean at that size and 6.3 at x1000, but
  that is the harness's own single-precision metric failing -- the same meshes measured in double
  precision (scratchpad dihed.py) have the same dihedrals at x1, x100 and x1000. The pipeline's
  normals use MeshMath.SafeNormalize (no floor). Solving at a power-of-ten working size was built,
  measured no different (studs part at 200 and 1000 too), and removed.
- The helmet's artifacts ARE from this branch: "Straps As Tubes" (on in the window) takes the
  nasal, a hanging bar and both eye-guard frames as straps (thin, free rims -- what a strap is)
  and rebuilds them as box tubes; the curved frames come out jagged and the nasal's cut notches
  the knotwork band. helmet_high at 3000: straps off = 3aab266 to the digit (irregular 4.95 %,
  dihedral p90 40 deg); on: 5.57 %, p90 64 deg (the 40 open edges are the tubes' ends, by design).
- The Porsche: the strap finder is not it (one part, at 4x budget). The budget is: split by area,
  interior 32 %, wheels 16 %, underbody 13 %, the 19 exterior panels 34 % (body 3.4 m2 gets ~370
  quads at 8000, cell ~10 cm; its roof-rail strips and flanges are narrower than that and
  collapse -- at 4x it holds). Auto Profile reads every panel as a scan (probe 0.25-0.67, the
  vertex-fraction limit SelectProfileKind's remarks predict for coarse modelled meshes); forcing
  GameAsset is mixed (door Hausdorff .025 -> .011, body at 1280 .008 -> .220), so not a fix alone.

Round 28, second part -- what was still wavy on ccnb: cars and small buildings.

- Cars are smaller than a cell at 5000 (cell ~2 m on this 238 x 282 m site); in the geometry they
  are gone or a gentle bump, and only the texture can show them. The colour bake's lines ran
  along the bump's own tilted normals and threw the photo sideways -- bent outlines, the shed's
  white halos. `TextureBake.colourDirectionSweeps` (25, a smoothed cage): the lines run along the
  surface's general direction instead. PSNR against the scan (top / oblique): 0 sweeps 25.05 /
  23.43 dB, 4: 25.46 / 23.39, 10: 25.78 / 23.33, 25: 26.24 / 23.22; in the renders the halos are
  gone from above and at an angle. Neighbours facing away are not averaged in (thin plates; unit
  test SmoothedBakeDirectionsKeepAThinPlatesTwoSidesApart -- a guard: the ray comes back through
  its own texel's point, so it passes without the check too).
- Sheds and trailers have edges of 3-6 cells, under the scan's 8-cell crease minimum, so they
  were never layout lines. `RemeshPipeline.scanCreaseMinCellsOpen` 5 for scans with an open
  border; numbers in its remarks (ccnb 8 -> 5: input-to-output 0.131 -> 0.119, shape the same,
  trailers as boxes; the closed body is slightly worse at 5, so it stays at 8).

## Round 29: Model Type with shape recognition, fingers, the car (branch remesher-band-structure)

The window's settings were one set for every model, and Auto Profile could tell only a scan from a
modelled mesh (it read the Porsche's panels as scans). Now a **Model Type** -- Auto, Character,
Hard Surface, Scan -- with Auto read off each mesh, and off each part of a model in parts.
Everything that measured better for every model stays on for every type; a type only adds what
measured differently by kind (RemeshPipeline.ApplyModelType, StrapsFor, ThicknessCapFor).

| Type | Profile | Strap finder | Thickness cap |
|---|---|---|---|
| Character | as chosen (Auto Profile: head, body Photogrammetry; main1 GameAsset) | the window's toggle | on |
| Hard Surface | HardSurface (creases at 20 deg) | off | off |
| Scan | Photogrammetry (no creases; open-scan creases from 5 cells) | off | off |
| General (API default, no type) | as before | as before | off |

**Recognition** (`ShapeRecognition`), on the input, welded by position. Signals on every model
in the project plus organic quad meshes made for the test (main1 and the body voxel-remeshed in
Blender, with and without a subdivision -- a ZRemesher-style export after Unity's import):

| | quad pairs | of them longer than 3:1 | open border |
|---|---|---|---|
| Porsche, 43 parts | 0.43-1.0 | 0.22-0.97 | 0-39 |
| organic quad meshes (3) | 0.32-0.58 | 0.000-0.001 | 0 |
| sculpts: head, body, main1, helmet parts | 0.04-0.11 | 0.03-0.13 | 0 (one open shell 9.5) |
| ccnb | 0.05 | 0.06 | 4.5 |

Hard Surface = quad pairs >= 0.25 and elongated >= 0.12 (quads alone would take a ZRemesher
character for a car); Scan = open border >= 2; else Character. All 56 meshes come out as their kind
(the Sketchfab helmet's open shell as Scan, which changes nothing for it). 0.4-0.6 s on 1M
triangles.

**Fingers and toes** (Character): `RemeshPipeline.thicknessSizing` -- no cell wider than a part's
thickness over 4, rods only, graded back out at 1.5x per cell. main1 at 5000:

| | faces | Hausdorff | input-to-output mean / p99 cells | irregular |
|---|---|---|---|---|
| before | 7,557 | 0.0086 | 0.065 / 0.51 | 1.43 % |
| cap | 9,939 | 0.0057 | 0.043 / 0.21 | 1.12 % |

The fingers come out whole (before: torn and broken); the toes gain only a little -- pressed
together, they are not rods. Body: 10,530 -> 10,979 faces, Hausdorff 0.0091 -> 0.0079. Not used
where the model is mostly thin (`thicknessMaxGrowth` 1.5): the helmet head would take 1.58x, the
Sketchfab helmet 5.77x; squeezed into 1.5x the rest coarsened (helmet irregular 5.4 -> 9.4 %),
so both come out exactly as before. Tried on the way:

- 3 cells across, every part, per-edge grading: body 10.5k -> 21k faces. The caps alone added 258
  quads; per-edge grading spread them by how finely the solve mesh was cut, and the count ratio,
  taken before local refinement, came out 2.4x where the run showed 1.35x. Per-cell grading fixed
  it.
- A floor of 0.4 of the field: protects the body, costs main1 (Hausdorff 0.0108).
- Plates: a panel or coat modelled with two sides 0.5-1 cell apart read as thin. `rodsOnly`
  (from the middle of the part, walls on both sides within 0.75 of its thickness along some
  direction square to it) keeps them out.
- On every model regardless of type: head +61 %, ccnb +36 %, Sketchfab helmet x3.4.
- Ridge/groove gap sizing (`ridgeGapSizing`, off): for the body's glove, whose fingers are pressed
  together. The hand's spacing did not move while coat folds took the quads (10.5k -> 16.1k).
- Patch Curves and curvature alignment on the glove: no grooves either. The glove remains a mitten
  at 5000; its grooves are about 0.75 cell from the crests, under what the lattice can hold.

**The layout dropping regions** (every type): `layoutCoverageFallback`. The quad layout meshes
patch by patch, and a patch it cannot make rectangular gets no quads. Coverage = share of evenly
spread input samples within 2 cells of the output; under 98 % the run is solved again without the
layout and the one covering more is kept. Porsche roof at 140 quads: coverage 0.72 -> 0.96,
input-to-output mean 31 -> 0.76 cells. (Output area over input area was tried first and does not
measure this: the hood read 1.73.) Clean parts read 0.998-1.000 and are untouched.

**Car parts, Hard Surface against the profile Auto Profile picked** (Photogrammetry), at their
area share of 8000 (irregular / input-to-output mean cells):

| part | Photogrammetry | Hard Surface |
|---|---|---|
| hood 120 | 6.0 % / 0.163 | 5.0 % / 0.143 |
| Fenders 245 | 3.5 % / 0.199 | 2.8 % / 0.170 |
| front_bumper 280 | 7.5 % / 0.458 | 4.6 % / 0.301 |
| rear_bumper 180 | 26.1 % / 0.339 | 4.1 % / 0.303 |
| roof 140 | 2.0 % / 31.0 (lost) | 6.2 % / 0.757 |
| door 170 | 2.8 % / 0.109 | 3.6 % / 0.116 |
| body 1280 | 2.4 % / 0.103 | 2.3 % / 1.629 (C-pillar strip lost) |
| body 320 | 6.4 % / 2.34 | 10.4 % / 1.91 (crumpled either way) |

**Fewer Quads On Hidden Parts** (window, models in parts, on): each part's share is its area times
0.15 + 0.85 x its visibility (`PartBake.Visibility`: points over the part, the share of the
directions it faces from which nothing opaque is in the way; glass -- transparent materials, or
named glass or window -- does not block). Porsche at 8000: cabin 0.10, seats 0.06, door insides
0.06, tunnel 0.02; body 0.72, doors 0.91, roof 0.85. Body 371 -> 571 quads, doors 198 -> 368, roof
238 -> 418, cabin 1063 -> 500; per-piece floors scale with it (not under 8 a piece). 1.8 s.

**Texture Size Auto** (window, default): the source texture's own size, rounded up to a power of
two, 1024-8192 -- ccnb.jpg is 8192 square and had been baked at the window's 2048 (or 4096).

**The Porsche through the window's own path** (headless PartsCheck, 8000, all parts): area split,
no type: 25,733 faces, solve 212 s; Auto type with Fewer Quads On Hidden Parts: 23,945 faces,
143 s, all 43 parts read as Hard Surface, 17 parts under 0.2 visible given fewer quads (cabin 343,
seats 272, tunnel 52). The roof, the windscreen frame and the side windows now come out where
they were lost; the panels are still crumpled in places at this count -- the car is better, not
good. The window seals ("rubber": strips thinner than a cell) give nothing as Hard Surface and are
kept as they were (the window says so); under the old profile they came out as garbage
(input-to-output 12-75 cells), so that is the better of the two.

Tests 227: 225 pass, 2 skipped (Headless/tests22.xml), including ShapeRecognitionTests (hard
surface / even quad sphere / open jittered sheet / rod against plate / part shut inside another,
and the same behind glass).

## Round 30: the window's settings, wheels and car panels (branch remesher-band-structure)

**The window's settings, audited.** Every switch was run on and off on the helmet head (5000),
main1 (5000), the Porsche body (1280) and a wheel pair (1500), type Auto, with everything else at
the window's defaults (irregular / Hausdorff):

| switch | head | main1 | car body | wheel |
|---|---|---|---|---|
| defaults | 2.9 % / 0.051 | 1.1 % / 0.0057 | 2.3 % / 0.220 | 8.0 % / 0.094 |
| Extractor Collapse | 46.7 % / 0.026 | 23.6 % / 0.025 | 37.4 % / 0.080 | 46.6 % / 0.037 |
| Detail Quads | 34.9 % / 0.051 | 22.6 % / 0.0034 | 11.6 % / 0.058 | 19.5 % / 0.058 |
| Quad Layout off | 4.1 % / 0.039 | 1.5 % / 0.0060 | 3.4 % / 0.117 | 18.8 % / 0.095 |
| Global Parametrization off | (= layout off) | 1.9 % / 0.0188 | (= layout off) | (= layout off) |
| Curvature Alignment 0.3 | 3.8 % / 0.049 | 1.4 % / 0.0055 | 2.2 % / 0.012 | 6.9 % / 0.096 |
| Patch Curves | 2.9 % / 0.050 | 1.2 % / 0.0032 | 4.1 % / 0.120 | 8.7 % / 0.091 |
| Straps As Tubes off | 2.4 % / 0.012 | unchanged | unchanged | unchanged |

None is dead -- each changes something -- but Collapse and Detail Quads are worse everywhere, Quad
Layout and Global Parametrization are better on and fall back on their own (layout coverage),
Curvature Alignment and Patch Curves are mixed, and the strap finder is Model Type's business
(Character only). The window now shows only what a user decides: Parts (Combine Parts, Fewer Quads
On Hidden Parts), Model Type, Quads (six settings), Output (Bake Texture, Bake Normal Map,
Texture Size for both bakes with the size Auto resolves to, Save As Asset, Quad Topology In Asset),
Fix Areas, and a folded "Last Run Details". The Profile list and Auto Profile went with it (Model
Type's Auto is that selection). All the methods stay in Runtime behind their statics.

**Texture Size Auto, checked end to end** (headless PartsCheck, ccnb at 5000): colour texture and
normal map both written and imported 8192 x 8192. The user's last ccnb output (2048) predated the
change; the popup sat indented under Bake Normal Map and read as the normal map's alone -- it has
its own row now.

**Wheels: the rim barrel was lidded.** The wheel part is 8 pieces (tire -- an open tube --, spoke
disc, rim barrel -- open both ends, 51 edges round --, caliper, twice). After the cap pass, which
leaves the input's own outline open, `CapSmallLoopsUndirected` zipped every loop up to 64 edges shut
with a ladder of quads, outline or not: a flat striped disc over every wheel. It now uses the same
outline test (`HoleFiller.OutlineTest`, `IntegerGridMap.closeKeepsOutline`). Rim barrel alone at
300: Hausdorff 0.287 -> 0.039; the wheel pair at 1500: 0.095 -> 0.027, irregular 20.8 -> 7.8 %.

**Dense creases are detail** (`hardSurfaceSparseCreases`, `FeatureGraph.KeepSparseCreases`): Hard
Surface keeps its 20-degree creases only where, within a cell, there is less than 4.5 cells of crease
-- one crease reads ~2, a corner ~3, a tread or grille a dozen. Wheel: 27,162 tread crease edges
dropped, irregular 7.8 -> 4.5-6.5 %. Taking Hard Surface's creases by chains instead
(`hardSurfaceCoherentCreases`, as Loops Along Creases on the other types) helped the wheel and hurt
the panels (roof Hausdorff 0.029 -> 0.062, bumper irregular 4.0 -> 8.5 %) -- off.

**The decimation cut across the shape** (`decimateMaxErrorCells` 0.1, `DecimateOptions.maxDistance`).
Most cheap collapses on a modelled car part are refused as slivers, so the heap ran down to dear
ones and took them: front bumper 74,336 -> 7,771 triangles with a largest error of 2.56, its curled
lip cut straight across a cell inside the surface, and every quad followed (output vertices 0.54
cells off the source on average). A collapse is now refused past 0.1 cell RMS over the planes it
stands for; the mesh stays a little above budget instead (8,774). Bumper Hausdorff 0.092 -> 0.037,
chamfer 0.0080 -> 0.0031, vertices 0.06 cells off. Sculpts and ccnb hardly reach the limit (0-119
refusals).

**Open strips get two cells across** (`stripSizing`, `ScaleField.StripWidth`, Hard Surface): a
strip's width is the distance between its two borders (a border vertex whose nearest border vertex
is 3x further round its own loop than straight across), and the field is capped to width / 2 there,
graded out, the target raised by what it adds (as the finger cap). The body's rear pillar strips,
narrower than a cell, were dropped: body at 600 Hausdorff 0.220 -> 0.010, chamfer 0.0021 -> 0.0010,
+18 % quads; roof 0.084 -> 0.007 (coverage 99.2 % had hidden its lost strip from the 98 % check).

**Car parts, now** (Hard Surface against Scan/Character on the same code, Hausdorff): body 0.010
(Scan 0.221), hood 0.027 (0.031), door 0.0069 (0.011), Fenders 0.015 (0.016), wheels 0.028 (0.041),
roof 0.007 (0.014), bumper 0.037 (0.036) -- Hard Surface is right for them.

**Regression, old build (6eeaa6d) against new, type Auto** (Hausdorff / irregular): head 5000
0.0507 / 2.92 % -> 0.0497 / 2.75 %; head 2000 0.0517 -> 0.0249; body, main1, helmet, horn
identical; ccnb 5000 0.0135 -> 0.0169 (irregular 4.64 -> 4.60 %), ccnb 2000 0.0205 -> 0.0170.

**The Porsche through the window's own path** (8000, Auto): 23,945 -> 22,866 faces, 143 -> 137 s.
Wheels with spokes instead of lids, the pillars and the windscreen frame whole, doors and front
smooth; the right rear arch and the wing are still rough.

Tests 231: 229 pass, 2 skipped (Headless/tests24.xml), including HardSurfaceTests (open tube not
lidded, dense creases dropped and a lone one kept, strip width, decimation error limit).

## Round 31 -- models that already have good topology: reduce their quads, do not remesh them

The user: "you struggle with objects that already have good topology, but are amazing with AI
generated models". Every one of the Porsche's 43 parts is 100 % quads. The remesher triangulates
them, decimates and lays a new grid by curvature -- the right thing for a triangle soup, which has
no loops to keep, and the wrong one for a modelled part: a cell wider than the arch's rolled lip
cuts across it, the flange crumples, the support loops become noise.

**QuadReduction** (Runtime/QuadReduction.cs, `RemeshPipeline.quadInputReduction`, Hard Surface and
Character): a clean quad input is reduced by removing whole quad strips (chords). Each rung of a
strip merges to one point, on the rung where the two ends' quadrics (face planes, border planes)
are least; every other loop keeps its course and the artist's poles stay. Cost per strip:
(widest rung / cell)^2 + (largest distance of the input it stood for, or of a new point from that
input, / 0.1 cell)^2 -- narrow strips (support loops, dense rows) first, strips that carry a lip or
a crease last. Batches of strips two rings apart, rebuilt between. Refused: a fold (normal turned
past 78 degrees, or a quad folded over its diagonal -- each rung's quadric point is retried at the
rung middles and either end, which zigzag-placed points needed), a face merged onto itself, a pinched
border, an edge with three faces. A strip crossing itself (up to twice) collapses its crossing quad
to a point -- body at 571 went from stuck at 1,758 to 570.

Gates, falling back to the remesher: 95 % of the triangles must pair into quads and 50 % of those
pairs be square (sculpts pair 91-97 % since the augmenting pass, but only 11-19 % square; car parts
65-92 %); strips run out more than 1.5x and 100 quads over the target; a strip moved the surface more
than 2 cells; more than 22 % irregular vertices on 100+ faces (the wheels -- 277,000 quads of tread
at ~500 -- come out 24-31 %, panels 1-21 %).

**Unity reorders triangles** (Optimize Polygons, on by default): order pairing found 95-97 % and
shape pairing 95-99.6 % with wrong pairs -- 14 % of the door, whose sheared rows make halves of two
neighbouring quads a better rectangle than the quads. Editor/QuadSource.cs imports a temporary Keep
Quads copy of the model file (deleted at once, the user's asset untouched), and puts the original
mesh's own triangles in quad order: 100 % on all 43 meshes, 3.3 s once per model per session.
ReadMesh also read Keep Quads meshes as empty; it now splits their quads in order.

**At the window's budgets (Porsche 8000), strips against the remesher** (faces / Hausdorff):

| part | strips | remesher |
|---|---|---|
| body (rear quarters, arches) | 603 / 0.0065 | 2163 / 0.0079 |
| Fenders | 464 / 0.0064 | 1254 / 0.0119 |
| trunk | 385 / 0.0048 | 1168 / 0.0136 |
| door | 383 / 0.0052 | 1252 / 0.0065 |
| front bumper | 350 / 0.032 | 1055 / 0.040 |
| rear bumper | 216 / 0.0125 | 530 / 0.043 |
| top spoiler | 164 / 0.044 | 95 / 0.135 |
| hood (the whale-tail wing) | 213 / 0.062 | 433 / 0.040 |
| roof | 437 / 0.027 | 1378 / 0.0074 |

Strips keep the target (the remesher's thin-part raises take it 2-3x over) and lose on the roof's
thin rear trim and the wing's slot (normal-map detail at that count). Irregular vertices are higher
(4-18 % against 0.5-4.5 %): the artist's poles plus strip crossings.

**The Porsche through the window** (8000): 22,866 -> 11,377 faces, 137 -> 140 s; 28 parts by strips.
Rear arches, the wing, bumpers, doors and roof clean. The wheels stay on the remesher (their tread at
~500 quads).

Regression (head, body, main1, ccnb, helmet, horn, head 2000, ccnb 2000): identical -- all "not quads".

Tests 236: 234 pass, 2 skipped (Headless/tests27.xml), including QuadReductionTests (panel reduced to target all quads with its rolled lip, open tube stays round, shuffled triangles pair back > 99 %, an icosphere is left to the remesher, ReadMesh reads Keep Quads meshes).

## Round 32: one budget for a model's quad parts, motion-swept shapes, settings per Model Type

**Settings per Model Type.** A "Settings For This Type" foldout under Model Type (all three groups
under Auto): Rings On Round Parts (any type); Character: Keep Source Loops, Straps As Tubes, Fingers
And Toes Get Rows; Hard Surface: Keep Source Loops, Dense Edges To Normal Map, Thin Strips Get Quads,
Follow Shape Regions; Scan: Follow Shape Regions. Each sets the RemeshPipeline static it names
(quadReductionCharacter / quadReductionHardSurface, ribbonParts, thicknessSizing,
hardSurfaceSparseCreases, stripSizing, kinematicGuides, kinematicRegionsHardSurface / Scan), all on.

**Motion-swept shapes (Runtime/KinematicSurfaces.cs).** After Pottmann & Randrup (line geometry,
kinematic surfaces): a surface a rigid motion slides along itself has every normal perpendicular to
the motion's velocity v(x) = c' + c x x, so the motion is the least eigenvector of a 6x6 matrix of the
normals' Pluecker coordinates. One eigenvalue near zero: a rotation (tyre, rim, knob, bottle), a
translation (extrusion) or a screw; two: a cylinder (its rotation taken); three or more: a plane or
sphere, left alone. A piece that fits (90 % of its normals within 10 degrees) gets its orientation
field guided along the motion: rings round the axis, rows along an extrusion, faded out near the
axis. Pieces that fit no one motion are grown into regions from seed patches (Scan and Hard Surface
only: on characters regions took the helmet from 5.4 % to 7.9-10.6 % irregular).

| model | before | after |
|---|---|---|
| Porsche tyre (alone, 1500) | irregular 6.56 % | 4.86 %, clean rings |
| ccnb 5000 | Hausdorff 0.0169 | 0.0135 |
| ccnb 2000 | Hausdorff 0.0170 | 0.0138 |
| head, body, main1, helmet, horn | -- | identical |

**One budget for a model's quad parts (QuadReduction.ReduceParts).** All parts that are clean quads
are reduced together at one cell size: each part's strips cost as before, scaled by its visibility
weight, so a hidden floor pan gives its quads to a seen wheel arch. A part's share anchors it: under
0.8 of it a part takes no more strips, over 1.25 its strips go first (each forced part with its own
cost limit, before any other part's), and a part with no strip left is left as it is. Visibility
weight is a smoothstep from 0.05 (hidden, under 0.08 seen) to 1 (over 0.6).

Speed (the Porsche, 43 parts, 1.3 M quads of which the wheels are 554,000): a batch costs only open
parts' strips (none of the others' during the forced phase); a strip's plan is kept from the last
batch when its faces and rungs are the same and no vertex round it has moved (a stamp per vertex);
the error is measured only for strips whose width cost alone is within the batch's limit; costing
reuses per-thread containers (Unity's collector is slow with many threads allocating); adjacency is
built without a dictionary, for open parts only. Same output at each step.

| | .NET harness | headless Unity |
|---|---|---|
| joint reduction before | 160 s | ~1000 s |
| after | 16 s | 24 s |
| whole window run (8000) | | 1126 s -> 242 s |

Of the 242 s, 180 s are parts the remesher solves (strips ran out, too irregular): pedals 33 s, wing
30 s, seats 30 s, rubber 21 s, gauge 16 s at 16-150 quads each. The status line now gives the run's
wall clock (it summed the parts' stage times, 80 s, missing a layout's second solve and the strips).

Window Porsche 8000: 11,377 -> 10,953 faces. Panels at 0.8-1.25 of their shares (door 294/365, body
795/686, Fenders 541/525, trunk 324/401, roof 534/440). The front bumper keeps its grille slots.
Still open: the wheels (277,000 quads each) come out of strips 25-30 % irregular and go to the
remesher, which drops their rims and spokes at ~400 quads; mirrors and headlights crumple at their
budgets; interior parts run out of strips.

Regression (head, body, main1, ccnb, helmet, horn, head 2000, ccnb 2000): identical.

Tests 236: 234 pass, 2 skipped (Headless/tests29.xml).
