# Does this mesh read as a clean low-poly sculpture, or as a bumpy one?
#
# Every metric this project had measures either WHERE the vertices are (deviation, coverage) or
# HOW MANY of them are irregular (valence, singularities). The user's complaint is a third thing:
# the surface has "useless bumps or distortions" - it is neither off the original surface nor
# badly connected, it is LUMPY. A vertex pushed a little along its own normal relative to its
# neighbours is still on the input surface, still valence 4, and still contributes almost nothing
# to mean deviation. It just catches the light.
#
# THE MEASUREMENT. For each interior vertex take the umbrella - the mean of its neighbours - and
# the component of (umbrella - v) along the vertex normal, divided by the mean edge length:
#
#     h_v = ((mean of neighbours) - v) . n_v / meanEdge        [dimensionless]
#
# h is the discrete mean curvature in units of the quad size. It is NOT a defect on its own: a nose
# is curved and a curved surface has large h everywhere on it. What separates shape from noise is
# how h varies BETWEEN NEIGHBOURS. Real curvature is a low-frequency field, so h is nearly the same
# at a vertex and at the vertices around it. A bump is one vertex disagreeing with its ring.
#
# So the number that matters is the residual after smoothing h once:
#
#     r_v = h_v - (mean of h over the ring)
#
# Curvature cancels. Anything left is at the sampling frequency of the mesh itself, which is
# exactly the frequency a viewer sees as bumpiness. `roughness` below is RMS(r).
#
# A bump is then reported as a vertex where |r| is large AND its sign opposes the ring - a vertex
# poking out of, or sunk into, an otherwise agreeing neighbourhood.
#
# DISTORTION, the other half of the complaint, is separate and is about the quads themselves rather
# than their placement: edge lengths that vary wildly, quads that are long thin rectangles, quads
# that do not lie in a plane. Those are reported alongside because the user named them together and
# they are not the same defect - a mesh can be perfectly smooth and badly distorted, or the reverse.
#
#   python bumps.py ours.obj theirs.obj [more.obj ...]

import sys, math


def load(path):
    vs, fs = [], []
    with open(path, "r", errors="ignore") as fh:
        for line in fh:
            if line.startswith("v "):
                p = line.split()
                vs.append((float(p[1]), float(p[2]), float(p[3])))
            elif line.startswith("f "):
                idx = []
                for tok in line.split()[1:]:
                    i = int(tok.split("/")[0])
                    idx.append(i - 1 if i > 0 else len(vs) + i)
                if len(idx) >= 3:
                    fs.append(idx)
    return vs, fs


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def norm(a):
    return math.sqrt(dot(a, a))


def scale(a, s):
    return (a[0] * s, a[1] * s, a[2] * s)


def newell(vs, f):
    # Newell's method: correct for non-planar polygons, which is the whole point here.
    n = (0.0, 0.0, 0.0)
    for i in range(len(f)):
        a, b = vs[f[i]], vs[f[(i + 1) % len(f)]]
        n = add(n, ((a[1] - b[1]) * (a[2] + b[2]),
                    (a[2] - b[2]) * (a[0] + b[0]),
                    (a[0] - b[0]) * (a[1] + b[1])))
    return n


def pct(xs, q):
    if not xs:
        return 0.0
    s = sorted(xs)
    i = min(len(s) - 1, max(0, int(q * (len(s) - 1))))
    return s[i]


def analyse(path):
    vs, fs = load(path)
    nv, nf = len(vs), len(fs)

    # --- adjacency, and how many faces use each undirected edge (2 = interior, 1 = boundary)
    ring = [set() for _ in range(nv)]
    edge_faces = {}
    for f in fs:
        for i in range(len(f)):
            a, b = f[i], f[(i + 1) % len(f)]
            ring[a].add(b)
            ring[b].add(a)
            k = (a, b) if a < b else (b, a)
            edge_faces[k] = edge_faces.get(k, 0) + 1

    boundary = [False] * nv
    for (a, b), c in edge_faces.items():
        if c != 2:
            boundary[a] = boundary[b] = True

    # --- area-weighted vertex normals. Weighting by area rather than counting faces equally keeps
    # a sliver from steering the normal of a vertex it barely touches.
    vn = [(0.0, 0.0, 0.0)] * nv
    for f in fs:
        n = newell(vs, f)
        for i in f:
            vn[i] = add(vn[i], n)
    for i in range(nv):
        m = norm(vn[i])
        vn[i] = scale(vn[i], 1.0 / m) if m > 1e-12 else (0.0, 0.0, 0.0)

    # --- scale: mean edge length, and the bbox diagonal so numbers compare across models
    elens = []
    for (a, b) in edge_faces:
        elens.append(norm(sub(vs[a], vs[b])))
    mean_edge = sum(elens) / len(elens)
    lo = [min(v[k] for v in vs) for k in range(3)]
    hi = [max(v[k] for v in vs) for k in range(3)]
    diag = norm((hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]))

    # --- h: normal component of the umbrella, in edge lengths
    h = [0.0] * nv
    interior = []
    for i in range(nv):
        if boundary[i] or not ring[i]:
            continue
        u = (0.0, 0.0, 0.0)
        for j in ring[i]:
            u = add(u, vs[j])
        u = scale(u, 1.0 / len(ring[i]))
        h[i] = dot(sub(u, vs[i]), vn[i]) / mean_edge
        interior.append(i)

    # --- r: h minus the ring average of h. Low-frequency curvature cancels; bumps survive.
    r = [0.0] * nv
    for i in interior:
        nb = [j for j in ring[i] if not boundary[j]]
        if not nb:
            continue
        r[i] = h[i] - sum(h[j] for j in nb) / len(nb)

    rs = [abs(r[i]) for i in interior]
    roughness = math.sqrt(sum(x * x for x in rs) / len(rs)) if rs else 0.0

    # A bump: the vertex disagrees with its ring by more than a twentieth of a quad AND its
    # curvature has the opposite sign to the ring's, so it is poking out of, or sunk into, a
    # neighbourhood that agrees with itself.
    bumps = 0
    for i in interior:
        nb = [j for j in ring[i] if not boundary[j]]
        if not nb:
            continue
        ringh = sum(h[j] for j in nb) / len(nb)
        if abs(r[i]) > 0.05 and h[i] * ringh < 0:
            bumps += 1

    # --- distortion, on the faces themselves
    quads = [f for f in fs if len(f) == 4]
    aspects, warps = [], []
    for f in quads:
        d = [norm(sub(vs[f[i]], vs[f[(i + 1) % 4]])) for i in range(4)]
        if min(d) > 1e-12:
            aspects.append(max(d) / min(d))
        # warp: how far the corners sit off their own best-fit plane, in edge lengths
        n = newell(vs, f)
        m = norm(n)
        if m > 1e-12:
            n = scale(n, 1.0 / m)
            c = scale((vs[f[0]][0] + vs[f[1]][0] + vs[f[2]][0] + vs[f[3]][0],
                       vs[f[0]][1] + vs[f[1]][1] + vs[f[2]][1] + vs[f[3]][1],
                       vs[f[0]][2] + vs[f[1]][2] + vs[f[2]][2] + vs[f[3]][2]), 0.25)
            warps.append(max(abs(dot(sub(vs[i], c), n)) for i in f) / (sum(d) / 4))

    ecv = math.sqrt(sum((e - mean_edge) ** 2 for e in elens) / len(elens)) / mean_edge

    return {
        "path": path.split("/")[-1].split("\\")[-1],
        "verts": nv, "faces": nf, "interior": len(interior),
        "meanEdgePctDiag": 100.0 * mean_edge / diag,
        "edgeCV": ecv,
        "edgeP99overP01": pct(elens, 0.99) / max(pct(elens, 0.01), 1e-12),
        "roughness": roughness,
        "rP50": pct(rs, 0.50), "rP90": pct(rs, 0.90), "rP99": pct(rs, 0.99), "rMax": max(rs) if rs else 0,
        "bumps": bumps, "bumpPct": 100.0 * bumps / max(len(interior), 1),
        "hP50": pct([abs(h[i]) for i in interior], 0.50),
        "hP99": pct([abs(h[i]) for i in interior], 0.99),
        "aspectP50": pct(aspects, 0.50), "aspectP99": pct(aspects, 0.99),
        "warpP50": pct(warps, 0.50), "warpP99": pct(warps, 0.99),
    }


def main(paths):
    rows = [analyse(p) for p in paths]

    def table(title, cols):
        print("\n" + title)
        w = max(len(r["path"]) for r in rows) + 2
        print("  " + "mesh".ljust(w) + "".join(c[0].rjust(c[2]) for c in cols))
        for r in rows:
            print("  " + r["path"].ljust(w)
                  + "".join(("%.*f" % (c[3], r[c[1]]) if isinstance(r[c[1]], float)
                             else str(r[c[1]])).rjust(c[2]) for c in cols))

    table("SIZE", [("verts", "verts", 9, 0), ("faces", "faces", 9, 0),
                   ("edge%diag", "meanEdgePctDiag", 12, 3)])

    table("BUMPS   (r = per-vertex curvature minus its ring's; units of one quad)",
          [("roughness", "roughness", 12, 4), ("|r| p50", "rP50", 10, 4),
           ("p90", "rP90", 9, 4), ("p99", "rP99", 9, 4), ("max", "rMax", 9, 3),
           ("bumps", "bumps", 8, 0), ("bump%", "bumpPct", 9, 2)])

    table("CURVATURE   (h itself - big here is shape, not defect)",
          [("|h| p50", "hP50", 10, 4), ("|h| p99", "hP99", 10, 4)])

    table("DISTORTION",
          [("edge CV", "edgeCV", 10, 4), ("p99/p01", "edgeP99overP01", 10, 2),
           ("aspect p50", "aspectP50", 12, 3), ("p99", "aspectP99", 9, 3),
           ("warp p50", "warpP50", 11, 4), ("p99", "warpP99", 9, 4)])
    print()


if __name__ == "__main__":
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    main(a)
