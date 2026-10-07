# Why the output looks disordered when every quality number is fine

The complaint that started this: *"it is okayish but has some parts where it is the mess of
polygons ... when we zoom we always have some artifacts"*, against a QuadRemesher result that
*"looks like a low poly sculpture with no broken or distorted parts"*.

Every number the project had said the two meshes were close. Deviation, smoothness percentiles,
coverage — ours matched or beat theirs on most of them. The measurement that finally agreed with the
complaint was not a quality metric at all.

## The fingerprint

`Benchmarks/reference/layout.py` reads structure rather than quality: valences, singularity count,
how the irregular vertices are placed. At matched density on the character:

| | ours | QuadRemesher |
| --- | ---: | ---: |
| **irregular interior vertices** | **1,702 (32.4 %)** | **204 (4.1 %)** |
| valence 3 / 5 | 788 / 686 | 99 / 92 |
| valence 6 / 7 / 8 | 144 / 29 / 4 | 9 / 1 / 0 |

A third of our vertices are irregular against one in twenty-five of theirs. That is not defects
scattered over a good mesh; it is a quad flow that is disordered everywhere, which is exactly what
"fine at a distance, wrong when you zoom" looks like.

`Benchmarks/reference/defects.py` says the same thing in face terms, by flagging every triangle,
n-gon, sliver, curled quad, open-edge face and either side of a fold past 60°, then **clustering the
flagged faces that touch each other**:

| | ours | theirs |
| --- | ---: | ---: |
| faces flagged | 13.5 % | 4.4 % |
| triangles | 437 | **0** |
| faces on an open edge | 17 | 0 |
| **isolated single bad faces** | **345** | **1** |

Their flagged faces are all in folds that follow real creases of the shape — the top of the head,
a symmetric pair at the ears. Ours has those same folds *plus* a scatter of lone defects across the
whole surface.

## Four explanations, all refuted by measurement

Each of these was the obvious answer at the time, and each is wrong:

**The cross field.** On an icosphere our field carries exactly **8** singularities with index sum 8 —
the theoretical minimum for a sphere, precisely what Euler requires. The mesh extracted from that
same run is 12.6 % irregular. On the character the field carries **427** (206 positive, 204
negative) against 1,625 irregular vertices in the output. The field is not innocent — 427 is roughly
twice what the competitor's whole output has — but it cannot account for a four-fold amplification.

**The tracer.** 43.4 % irregular in, 42.6 % out.

**Integer repair.** 98.3 % of triangles already satisfy the closed-loop integrability condition; only
926 of 54,740 fail, which caps any min-cost-flow repair at about 1 % of edge classifications. The
flow machinery is written and priced (`LatticeRepair.Plan`) and cannot be worth more than that.

**Decimation, and sizing.** Re-measured at the density the complaint is about, because the earlier
clearance was taken at a 7:1 budget and carried over to 29:1 without being re-run. The decimated
input leaves **0.000 %** of the surface uncovered with a worst gap nine times finer than half a quad;
turning decimation off end to end moves flagged faces 9.72 % → 8.35 % for **eight times the runtime**
and leaves the irregular fraction unchanged (31.4 % → 31.7 %). Curvature adaptivity at the
competitor's own setting is worth half a point and then gets worse.

## The instrument that localised it

"Extraction amplifies it" is not a diagnosis — it is nine passes with no number between them. Every
earlier attempt to place the fault failed exactly here, clearing the tracer with one probe and
blaming the collapse with another, never at the same point in the same run.

`ExtractionOptions.recordCensus` fills `QuadExtractionResult.census`: the irregular-vertex count
after every stage, boundary vertices excluded. The field's singularity count is the floor a perfect
extraction would reach, so any line that rises above it names a stage manufacturing irregularity
rather than carrying it.

| stage | icosphere @ 2,000 | character @ 5,215 |
| --- | ---: | ---: |
| **field singularities** | **8** | **427** |
| after collapse | 79 | 986 |
| after cleanup | 79 | 970 |
| after trace | 79 | 890 |
| after regularise | 86 | 901 |
| **after triangle pairing** | **159** | **1,287** |
| after n-gon split | 168 | 1,356 |
| after second pairing | 172 | 1,361 |
| final | 172 (18.4 %) | 1,358 (26.5 %) |

Two stages, and only two, add anything: **the collapse** (8 → 79, 427 → 986) and **triangle
pairing** (79 → 159, 901 → 1,287).

## Defect one: cells split in two

Every input edge is classified independently, by asking which integer lattice offset best explains
the gap between its two endpoints' lattice points. **An edge whose endpoints sit about half a cell
apart is a coin toss** — offset 0 explains it with a residual of 0.5, offset 1 explains it with a
residual of 0.5, and noise decides. Different edges between the same two groups of vertices come to
different answers, so one cell comes out as two clusters half a cell apart, with an output edge
between them because some edge voted "one step".

A split cell is exactly a cancelling valence pair: the halves divide the four directions between
them, so each is short of neighbours, and every neighbour of the original cell now has two claimants
in one direction. The already-linked guard in Pass B then makes it permanent — once one edge between
the halves has been called an output edge, every later candidate to merge them is refused.

Measured: **18 % of output edges are shorter than 0.75 cells** and 2.5 % shorter than half a cell,
where a lattice that tiles would put nearly all of them at 1.0.

`LatticeCollapse.MergeSplitCells` merges cluster pairs whose lattice points sit closer than a
threshold, cheapest first, under the same spread bound as the main collapse:

| merge below | irregular | quad % | triangles | edges < 0.75 cells | uncovered |
| ---: | ---: | ---: | ---: | ---: | ---: |
| off | 31.5 % | 96.63 | 184 | 18.2 % | 3.88 % |
| 0.6 | 29.3 % | 97.51 | 130 | 15.1 % | 3.98 % |
| **0.8** | **25.7 %** | **98.06** | **100** | 9.4 % | 4.22 % |
| 0.9 | 29.9 % | 97.92 | 105 | 5.8 % | 4.23 % |
| 1.0 | 46.1 % | 97.79 | 103 | 2.5 % | 3.63 % |

**Not monotone, and the turn is the useful part.** Past 0.8 it merges cells that are genuinely
adjacent — neighbouring cells sit one cell apart by definition — and each such merge takes a vertex
out of the middle of a regular grid, costing two irregular vertices rather than saving any. At 1.0
the short edges are nearly gone and the mesh is half irregular, which is the clearest possible
statement that **short edges were a symptom, not the disease**.

Cluster spread was swept alongside and does not matter: 29.4 % irregular at a 2.25-cell bound
against 29.9 % at 1.0. Over-merging by that route is not happening.

## Defect two: triangle pairing trades triangles for singularities

`TrianglePairing` walks two triangles together through the quads between them and re-tiles the
patch. Re-tiling a patch of n + 2 faces into n + 1 quads **removes a face**, so every vertex along
the seam loses a neighbour and a valence-4 becomes a valence-3. Two triangles are traded for about
two singularities, every time. It is not a bug in the pass; it is what the operation does.

This pass had been tuned earlier in the same session on triangles, quad fraction, curled faces and
minimum angle — all of which improve monotonically as its fold tolerance rises — and the tolerance
was set to 40°. **No valence column was in that table.** Swept again with one:

| fold allowed | quad % | irregular % | triangles |
| ---: | ---: | ---: | ---: |
| pairing off | 87.01 | **21.95** | 622 |
| 0° (strict convex) | 94.71 | 23.54 | 274 |
| **10°** | **96.08** | **23.55** | **203** |
| 20° | 97.06 | 26.27 | 151 |
| 40° | 97.55 | 26.47 | 127 |

Ten degrees is free — the same irregularity as refusing every fold, and a quarter fewer triangles.
Past it the trade turns sharply: 20° and 40° buy 50 and 76 more triangles for nearly three points of
irregularity. The default was corrected from 40 to 10.

Turning the pass off is not the answer either: 622 triangles is 13 % of the mesh, and it costs nine
points of quad fraction to save 1.6 of irregularity.

## The field is cleared, by improving it and watching nothing happen

The remaining suspect after the census was the field's own 427 singularities. The way to test a
suspect is not to reason about it but to make it better and see whether the output follows.

A Gauss-Seidel sweep carries information one edge, so a flat solve on 26,073 vertices produces a
field that is locally smooth and globally arbitrary — full of cancelling ± pairs, each one a place
the field turned and turned back. Coarse-to-fine solving is the standard fix and the hierarchy was
already built. `HierarchicalSolve.Orientations` re-adds it:

| sweeps | field sing +/− | irregular % | quad % | triangles |
| ---: | ---: | ---: | ---: | ---: |
| **off (ships)** | 206 / 204 | **23.55** | 96.08 | 203 |
| 3 | 187 / 181 | 24.40 | 96.34 | 188 |
| 6 | **184 / 178** | 24.74 | **96.38** | **184** |
| 12 | 184 / 178 | 24.71 | 96.01 | 202 |

**The theory is right about the field and it buys nothing.** Singularities fall from 410 to 362,
both signs together exactly as a cancelling-pair account predicts, saturating at six sweeps — and
the output gets *worse* by 1.2 points of irregularity.

The census says where it goes. Against the flat solve, the better field is still ahead after the two
stages that read it directly — collapse 953 → 934, trace 872 → 843 — and then loses all of it:

| stage | flat field | better field |
| --- | ---: | ---: |
| field singularities | 410 | **362** |
| after collapse | 953 | 934 |
| after trace | 872 | **843** |
| after regularise | 876 | 890 |
| after triangle pairing | 1,122 | 1,142 |
| after n-gon split | 1,196 | 1,226 |
| final | 1,195 | 1,236 |

So this measures something more useful than the feature it was testing. **Extraction, not the field,
is what bounds regularity.** Collapse alone turns 362 field singularities into 934 clusters, and the
passes after the tracer add about 390 irregular vertices whatever they are handed. A 12 % better
field moves the output by nothing, which means no amount of field work is the answer while that
holds.

The solve is kept and switched off. The previous version of it was *deleted* — on a sweep whose
every column was quad fraction, a metric structurally unable to see a vertex valence — and deleting
it took with it the only thing that could be re-measured. It still regresses HardSurface (27.3 % →
30.0 %), even with constraint restriction now dropping crease directions that project to nothing on
a coarse normal instead of pinning the coarse solve to them; that fix was necessary and is not
sufficient.

## Where it stands

| | before this work | after | QuadRemesher |
| --- | ---: | ---: | ---: |
| irregular vertices | 32.4 % | **23.6 %** | 4.1 % |
| quad fraction | 92.0 % | **96.1 %** | 99.96 % |
| triangles | 437 | **203** | 0 |
| faces on an open edge | 17 | **0** | 0 |

Still a long way from parity, and after the field was cleared the gap no longer decomposes. It is
**all in extraction**, in two places:

- **Collapse amplifies about 2.6×** — 362 field singularities in, 934 clusters out. Split cells were
  one cause and are repaired; the rest is not localised.
- **The passes after the tracer add about 390 irregular vertices** — regularise, triangle pairing and
  the n-gon split together — largely by construction, since re-tiling n + 2 faces into n + 1 quads
  removes a face and every vertex on the seam loses a neighbour.

And that is the point at which polishing stops being the right move. Both numbers are properties of
**extracting quads from a per-vertex lattice one edge at a time**, which is what Instant Meshes does
and what this project is a port of. `layout.py`'s own fingerprint says the competitor is not doing
that: zero triangles is not a polish step a local extractor can reach, because a quad mesh cannot
lose triangles one at a time, and 4.1 % irregular with singularities on seams is what a **global
patch layout** produces — solve the patch decomposition first, fill each patch with a regular grid.

Every repair in this document is a local extractor being tuned. They were worth making and they took
32.4 % to 23.6 %. They cannot reach 4.1 %, and the measurements above are what establishes that
rather than predicting it.

## The standing rules this adds

1. **A quality sweep without a structure column can choose the wrong value.** The pairing tolerance
   was set on four metrics that all agreed, and the one metric that disagreed was not in the table.
2. **The census is the instrument for any claim of the form "stage X is at fault".** Turn it on
   rather than reasoning about which pass could have done it.
3. Irregular-vertex fraction belongs next to quad fraction in every extraction sweep from now on.
