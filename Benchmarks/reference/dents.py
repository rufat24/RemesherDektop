# WHERE does the output leave the original surface, and in which direction?
#
# The user circled three places on a screenshot - a crumpled neck, a gouge at the shoulder, a dent
# in the chest - on a mesh whose MEAN deviation is 0.005 % of the bounding box diagonal. Both facts
# are true at once, which is the whole reason this script exists: a mean over 5,000 vertices cannot
# see forty of them pulled a quarter of a quad into the body, and forty is all a visible dent takes.
#
# So this reports the TAIL and WHERE IT IS, never an average:
#
#   For every output vertex, the distance to the nearest point of the original scan, SIGNED by the
#   scan's own normal there - negative means our surface sank INTO the body, positive means it
#   floats outside. A dent is a cluster of negatives; a blister is a cluster of positives.
#
#   Then the worst vertices are clustered by adjacency, so "0.3 % of vertices are bad" becomes
#   "they are in six places, and here they are" - in percentages of the bounding box, which is what
#   a screenshot can be read against. Head is high y, feet are low y.
#
# Nearest ORIGINAL VERTEX rather than nearest point on an original triangle. On a 750,000-vertex
# scan the vertices are far denser than the output's quads, so the difference is well below the
# sizes being looked for, and it makes the query a grid lookup rather than a triangle search.
#
#   python dents.py original.obj ours.obj [theirs.obj ...]

import sys, math


def load(path, want_faces=True):
    vs, fs = [], []
    with open(path, "r", errors="ignore") as fh:
        for line in fh:
            if line.startswith("v "):
                p = line.split()
                vs.append((float(p[1]), float(p[2]), float(p[3])))
            elif want_faces and line.startswith("f "):
                idx = []
                for tok in line.split()[1:]:
                    i = int(tok.split("/")[0])
                    idx.append(i - 1 if i > 0 else len(vs) + i)
                if len(idx) >= 3:
                    fs.append(idx)
    return vs, fs


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


class Grid:
    """Uniform hash over the original's vertices, plus an area-weighted normal at each."""

    def __init__(self, vs, fs, cell):
        self.vs = vs
        self.cell = cell
        self.buckets = {}
        for i, p in enumerate(vs):
            self.buckets.setdefault(self.key(p), []).append(i)

        nrm = [(0.0, 0.0, 0.0)] * len(vs)
        for f in fs:
            a, b, c = vs[f[0]], vs[f[1]], vs[f[2]]
            n = cross(sub(b, a), sub(c, a))
            for i in f[:3]:
                o = nrm[i]
                nrm[i] = (o[0] + n[0], o[1] + n[1], o[2] + n[2])
        self.normals = []
        for n in nrm:
            m = math.sqrt(dot(n, n))
            self.normals.append((n[0] / m, n[1] / m, n[2] / m) if m > 1e-20 else (0.0, 0.0, 0.0))

    def key(self, p):
        return (int(math.floor(p[0] / self.cell)),
                int(math.floor(p[1] / self.cell)),
                int(math.floor(p[2] / self.cell)))

    def nearest(self, p):
        kx, ky, kz = self.key(p)
        best, bi = float("inf"), -1
        ring = 1
        while True:
            for dx in range(-ring, ring + 1):
                for dy in range(-ring, ring + 1):
                    for dz in range(-ring, ring + 1):
                        if max(abs(dx), abs(dy), abs(dz)) != ring and ring > 1:
                            continue
                        for i in self.buckets.get((kx + dx, ky + dy, kz + dz), ()):
                            q = self.vs[i]
                            d = ((q[0] - p[0]) ** 2 + (q[1] - p[1]) ** 2 + (q[2] - p[2]) ** 2)
                            if d < best:
                                best, bi = d, i
            if bi >= 0 and best <= (ring * self.cell) ** 2:
                break
            ring += 1
            if ring > 24:
                break
        return math.sqrt(best) if bi >= 0 else float("inf"), bi


def pct(xs, q):
    s = sorted(xs)
    return s[min(len(s) - 1, max(0, int(q * (len(s) - 1))))]


def main(paths):
    orig_path, outs = paths[0], paths[1:]

    ov, of = load(orig_path)
    lo = [min(v[k] for v in ov) for k in range(3)]
    hi = [max(v[k] for v in ov) for k in range(3)]
    span = [max(hi[k] - lo[k], 1e-12) for k in range(3)]
    diag = math.sqrt(sum(s * s for s in span))
    print(f"\noriginal {orig_path}: {len(ov)} verts, {len(of)} tris, diagonal {diag:.4f}")

    grid = Grid(ov, of, diag / 400.0)

    for path in outs:
        vs, fs = load(path)

        # AUTO-ALIGN, because the competitor writes FBX in centimetres and this project writes
        # metres, so its OBJ comes out exactly 100x smaller. Comparing without fixing that reports
        # every one of its vertices as 4.6 % off the surface, which is a units bug wearing the
        # costume of a quality result. Uniform scale by the diagonal ratio and match bbox centres:
        # both meshes are the same object, so nothing else is needed and anything else would be
        # fitting the answer.
        vlo = [min(v[k] for v in vs) for k in range(3)]
        vhi = [max(v[k] for v in vs) for k in range(3)]
        vdiag = math.sqrt(sum((vhi[k] - vlo[k]) ** 2 for k in range(3)))
        if vdiag > 1e-12 and abs(vdiag / diag - 1.0) > 0.05:
            s = diag / vdiag
            vc = [(vhi[k] + vlo[k]) * 0.5 for k in range(3)]
            oc = [(hi[k] + lo[k]) * 0.5 for k in range(3)]
            vs = [tuple(oc[k] + (p[k] - vc[k]) * s for k in range(3)) for p in vs]
            print(f"\n  [aligned {path.split('/')[-1]}: scaled x{s:.4f} to match the original]")

        ring = [set() for _ in range(len(vs))]
        for f in fs:
            for i in range(len(f)):
                a, b = f[i], f[(i + 1) % len(f)]
                ring[a].add(b)
                ring[b].add(a)

        signed = []
        for p in vs:
            d, i = grid.nearest(p)
            if i < 0:
                signed.append(0.0)
                continue
            n = grid.normals[i]
            s = dot(sub(p, ov[i]), n)
            signed.append(100.0 * (d if s >= 0 else -d) / diag)

        mags = [abs(x) for x in signed]
        # A vertex is BAD when it sits further off the scan than a fifth of a quad. Quad size here
        # is about 0.9 % of the diagonal, so the cut is ~0.18 % - well under what a screenshot
        # shows, deliberately, so the clustering has something to work with.
        cut = 0.18
        bad = [i for i in range(len(vs)) if mags[i] > cut]

        print(f"\n{path.split('/')[-1].split(chr(92))[-1]}: {len(vs)} verts")
        print(f"  |deviation| % of diagonal:  p50 {pct(mags, .5):.4f}   p90 {pct(mags, .9):.4f}"
              f"   p99 {pct(mags, .99):.4f}   max {max(mags):.4f}")
        inside = sum(1 for x in signed if x < -cut)
        outside = sum(1 for x in signed if x > cut)
        print(f"  past {cut}%: {len(bad)} vertices ({100.0 * len(bad) / len(vs):.2f}%)"
              f"   sunk IN {inside}   bulged OUT {outside}")

        badset = set(bad)
        seen, clusters = set(), []
        for s in bad:
            if s in seen:
                continue
            stack, comp = [s], []
            seen.add(s)
            while stack:
                c = stack.pop()
                comp.append(c)
                for nb in ring[c]:
                    if nb in badset and nb not in seen:
                        seen.add(nb)
                        stack.append(nb)
            clusters.append(comp)

        clusters.sort(key=len, reverse=True)
        singles = sum(1 for c in clusters if len(c) == 1)
        print(f"  {len(clusters)} clusters, {singles} of them a lone vertex")
        for c in clusters[:10]:
            if len(c) < 2:
                break
            cx = [sum(vs[i][k] for i in c) / len(c) for k in range(3)]
            worst = max(c, key=lambda i: mags[i])
            print(f"    {len(c):4d} verts at ({100.0 * (cx[0] - lo[0]) / span[0]:5.1f},"
                  f"{100.0 * (cx[1] - lo[1]) / span[1]:5.1f},"
                  f"{100.0 * (cx[2] - lo[2]) / span[2]:5.1f})%"
                  f"   worst {signed[worst]:+.3f}%")
    print()


if __name__ == "__main__":
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    main(a)
