# What KIND of quad mesh is this?
#
# Face counts and angle statistics say how good a mesh is. They do not say what produced it, and
# that is the question here: two quadrangulators can hit the same deviation and the same quad
# fraction and still be doing completely different things.
#
# The fingerprint is the singularity structure and the edge loops:
#
#   A LOCAL extractor (Instant Meshes, and this project) grows quads from a field sampled per
#   vertex. Singularities appear wherever the field's holonomy says they must, one at a time, and
#   edge loops run until they hit one. Result: many singularities, scattered, and short loops.
#
#   A GLOBAL layout (integer-grid parameterisation, motorcycle graph, mixed-integer quadrangulation)
#   solves for a patch decomposition first and fills each patch with a regular grid. Singularities
#   are the patch corners - few, and placed deliberately - and the loops run the whole width of a
#   patch. Result: fewer singularities, arranged along seams, and long loops.
#
# The second kind cannot emit a lone triangle, which is why zero triangles is a fingerprint and not
# a polish step.
#
#   blender --background --factory-startup --python layout.py -- ours.obj theirs.obj

import sys, math
from collections import defaultdict

sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0])
from defects import load


def report(name, path):
    P, F = load(path)

    valence = defaultdict(int)
    for f in F:
        for v in f:
            valence[v] += 1

    # Boundary vertices have a valence deficit that means nothing, so they are excluded from the
    # irregular count: an open rim is not a singularity.
    edge_faces = defaultdict(list)
    for i, f in enumerate(F):
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            edge_faces[(min(a, b), max(a, b))].append(i)

    on_boundary = set()
    for (a, b), fs in edge_faces.items():
        if len(fs) != 2:
            on_boundary.add(a)
            on_boundary.add(b)

    interior = [v for v in valence if v not in on_boundary]
    hist = defaultdict(int)
    for v in interior:
        hist[valence[v]] += 1

    irregular = [v for v in interior if valence[v] != 4]

    print("")
    print("=== %s ===" % name)
    print("  %d faces, %d vertices (%d interior)" % (len(F), len(P), len(interior)))
    print("  valence: " + ", ".join("%d:%d" % (k, hist[k]) for k in sorted(hist)))
    print("  irregular interior vertices: %d (%.2f %% of interior)"
          % (len(irregular), 100.0 * len(irregular) / max(1, len(interior))))

    # Euler says a closed quad mesh must carry a fixed total of valence defect: sum(4 - valence)
    # over interior vertices is 8 for a sphere, 0 for a torus. Anything above that is singularities
    # in cancelling PAIRS - a 3 next to a 5 - which is the clearest sign of a local method, because
    # a global layout has no reason to place a pair that undoes itself.
    defect = sum(4 - valence[v] for v in interior)
    excess = sum(abs(4 - valence[v]) for v in interior)
    print("  valence defect sum %d (8 = sphere, 0 = torus); total |defect| %d" % (defect, excess))
    print("  -> %d of the singularities are in cancelling pairs beyond what the topology requires"
          % max(0, excess - abs(defect)))

    # Edge loops. In a quad, an edge's continuation is the opposite edge; the loop ends at a
    # non-quad or a vertex whose valence is not 4.
    quad_of_edge = defaultdict(list)
    for i, f in enumerate(F):
        if len(f) != 4:
            continue
        for k in range(4):
            a, b = f[k], f[(k + 1) % 4]
            quad_of_edge[(min(a, b), max(a, b))].append((i, k))

    seen = set()
    lengths = []
    closed = 0
    for start in quad_of_edge:
        if start in seen:
            continue

        length = 0
        loop_closed = False
        for direction in (0, 1):
            edge = start
            guard = 0
            while guard < 10000:
                guard += 1
                if edge in seen and length > 0:
                    loop_closed = edge == start
                    break
                seen.add(edge)
                length += 1

                entries = quad_of_edge.get(edge, [])
                if len(entries) != 2:
                    break

                # Step through the quad on the side we are travelling toward.
                face, k = entries[direction] if len(entries) > direction else entries[0]
                f = F[face]
                a, b = f[(k + 2) % 4], f[(k + 3) % 4]
                nxt = (min(a, b), max(a, b))
                if nxt == edge:
                    break
                edge = nxt
            if loop_closed:
                break
        lengths.append(length)
        if loop_closed:
            closed += 1

    lengths.sort()
    if lengths:
        print("  edge loops: %d, mean length %.1f, median %d, longest %d, %d closed"
              % (len(lengths), sum(lengths) / len(lengths),
                 lengths[len(lengths) // 2], lengths[-1], closed))

    # How far apart the singularities sit, as a fraction of the mean edge. Scattered singularities
    # sit a quad or two apart; a deliberate layout spaces them out.
    if len(irregular) > 1:
        edge_total, edge_count = 0.0, 0
        for (a, b) in edge_faces:
            edge_total += math.dist(P[a], P[b])
            edge_count += 1
        mean_edge = edge_total / max(1, edge_count)

        near = 0
        pos = [P[v] for v in irregular]
        for i, p in enumerate(pos):
            best = min((math.dist(p, q) for j, q in enumerate(pos) if j != i), default=0.0)
            if best < 1.5 * mean_edge:
                near += 1
        print("  %d of %d singularities sit within 1.5 edges of another (%.0f %%)"
              % (near, len(irregular), 100.0 * near / len(irregular)))


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    report("OURS", args[0])
    if len(args) > 1:
        report("THEIRS", args[1])
