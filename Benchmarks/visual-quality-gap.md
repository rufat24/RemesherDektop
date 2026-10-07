# What actually makes our output look worse than QuadRemesher's

Measured against the **original high-poly** (`ref_high.obj`, 750k verts / 1.5M tris), with
QuadRemesher's own output (`20260515221044_54b8f8f4_Retopo 4.fbx`, 21,780 faces, 99.99 % quads) as
the reference, and ours run at the same face count.

Run it with:

```bash
Unity.exe -batchmode -nographics -projectPath Headless -quit -executeMethod NativeRemesher.Benchmark.FidelityComparison.Run
```

QuadRemesher's FBX has to be converted first, because Unity imports FBX with quads off and its face
structure is then unrecoverable:

```bash
"/c/Program Files/Blender Foundation/Blender 4.5/blender.exe" --background --factory-startup --python Benchmarks/reference/export-competitor.py -- "Assets/QuadRemesher/Output/20260515221044_54b8f8f4_Retopo 4.fbx" Benchmarks/reference/qr-character.obj
```

---

## The numbers

| metric | QuadRemesher | NativeRemesher | |
|---|---|---|---|
| deviation from original, mean | 0.020 % | **0.005 %** | we are 4x closer |
| deviation, p99 | 0.088 % | **0.036 %** | we are closer |
| deviation, max | 0.242 % | **0.194 %** | we are closer |
| signed mean deviation | 0.000 % | 0.001 % | neither is shrunk |
| normal deviation, mean | 8.6° | **5.2°** | we are better |
| normal deviation, p95 | 38.3° | **19.3°** | we are better |
| quad non-planarity, median | 0.0135 | 0.0141 | equal |
| quad non-planarity, p99 | **0.1297** | 0.2315 | they are better |
| **min interior angle, p01** | **47.1°** | **6.3°** | **7x worse** |
| **min interior angle, p05** | **63.4°** | **33.5°** | **2x worse** |
| **min interior angle, worst** | **15.3°** | **0.2°** | needles |
| min interior angle, median | 82.1° | 82.1° | identical |
| **quad fraction** | **99.99 %** (2 non-quads) | **92.83 %** (~1,650) | |
| edge length spread p95/p05 | 4.0x | 1.6x | *they vary on purpose* |

## Four hypotheses, three refuted

**"Our vertices are cluster averages, so the model is shrunk."** REFUTED. Signed mean deviation is
0.001 % and 35.6 % of vertices lie inside the surface against QuadRemesher's 48.5 % - both
balanced, neither shrunk. Our unsigned deviation is *four times smaller* than theirs. Geometric
fidelity is not the problem; it is our strongest column.

**"Normals disagree with the original, hence the odd lighting."** REFUTED. Ours is better on both
mean (5.2° vs 8.6°) and p95 (19.3° vs 38.3°).

**"Non-planar quads render as two triangles with a crease."** REFUTED at the median, which is
identical to QuadRemesher's (0.0141 vs 0.0135). Only the p99 tail is worse, and that tail turns out
to be the same faces as the finding below.

**"Sliver quads."** CONFIRMED, and it is the whole gap. Our worst corner is 0.2° against their
15.3°; at p01, 6.3° against 47.1°. A face with a 6° corner has a numerically unstable normal, so it
shades wrongly and reads as a spike or a crease with no geometry behind it - which is exactly what
"small weird spots" and "some places are an absolute mess" describe.

## And the cause of the slivers is NOT what it looks like

| worst 1 % of faces by angle | mean edge aspect ratio | share touching a non-quad |
|---|---|---|
| QuadRemesher (217 faces) | 2.7x | **2 %** |
| NativeRemesher (230 faces) | **2.4x** | **88 %** |

Our needles are not long thin quads - our edge aspect ratio in the tail is *better* than
QuadRemesher's, and our overall edge uniformity is better too (1.6x against 4.0x, and theirs is
wider on purpose because they size adaptively). They are near-degenerate faces wedged against the
leftover triangles and n-gons.

**So the single number to move is quad fraction: 92.83 % to 99.99 %.** About 1,650 non-quads against
their 2. Everything the eye objects to is packed around those faces.

### This sharpens an earlier retraction rather than reversing it

Removing triangles was measured NOT to reduce the irregular-vertex count - they moved from "touching
a non-quad" into "interior, all quads" and the total held. That stands: triangles do not cause
*irregular vertices*.

They do cause the *needle faces*, which is a different defect and the one that is actually visible.
Both claims are true and the earlier mistake was assuming one metric stood for the other.

## What to do, in order

1. **Distance 3+ triangle pairing.** The triangle-separation histogram says the surviving pairs are
   now mostly 3 or more faces apart (character: 187 at distance 3, 87 at 4+), and those need the
   quad strip between the two triangles rotated before they can be merged. This is the standard
   quad-mesh operation and it is the last big lever on quad fraction.
2. **Relax the two merge thresholds, with the justification now measured.** `QuadRegularizer`
   refuses 60 merges on quality and 25 on convexity out of 156 candidates. Those refusals were
   protecting quad quality - but the measurement above shows a leftover triangle costs far more
   visually than a mediocre quad does, so the trade was set the wrong way round.
3. **Curvature-adaptive sizing (PLAN W3, never implemented).** QuadRemesher's edge lengths vary 4x
   while ours vary 1.6x: they spend small quads where curvature is high and large ones where it is
   flat. This is why their min angles stay high without needing repair, and it is the upstream
   reason we generate non-quads at all in detailed regions.

## Three measurement bugs found on the way here

Recorded because each produced a confident, wrong, plausible-looking result.

- **Unit scale.** QuadRemesher's FBX is in centimetres, so Blender's export came out 100x smaller.
  Uncorrected it read as a near-constant 4.5 % deviation with 0 % of vertices inside the surface -
  which looks like a catastrophic fidelity failure and is actually the distance between two
  separated objects. Now aligned by uniform scale, and the alignment is *verified* by comparing
  bounding-box side ratios (error 0.0007) so a genuine shape difference cannot be scaled away.
- **Two index spaces.** Positions came from `RemeshTopology.ReadMesh` (re-indexed) while faces came
  from `ObjReader.ReadFaces` (raw OBJ indices). Every per-face metric was then nonsense, reporting
  QuadRemesher's quads as *perfectly* planar. Fixed by `ObjReader.ReadPolygons`, which reads both in
  one pass and throws if any index is out of range.
- **`Vector3.normalized` returns zero below 1e-5.** The original's triangles are ~1e-6 in
  cross-product magnitude, so every normal became the zero vector, every dot product 0, and the
  normal-deviation metric read a flat 90° for *both* arms. Now divided by the explicit magnitude.
