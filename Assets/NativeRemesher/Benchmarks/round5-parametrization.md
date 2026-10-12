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
