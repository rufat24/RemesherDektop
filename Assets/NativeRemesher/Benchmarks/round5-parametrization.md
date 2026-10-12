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
