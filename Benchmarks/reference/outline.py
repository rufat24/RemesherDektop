# Where are the boundaries, and did we put a lid on one?
#
# The scan is an OPEN surface - it is cut off at the neck and at the waist, so a correct retopology
# has those outlines and nothing else. Two things can go wrong and they look completely different:
#
#   LIDDING. The hole filler caps an opening that was supposed to stay open. The result is a closed
#   mesh with a flat or folded disc where the outline was, and it is invisible to deviation (the lid
#   is near the surface it spans) and to coverage (a lid covers nothing that was missing). A closed
#   mesh where the input was open is the signature.
#
#   FRAYING. The outline survives but wanders - extra tiny loops, or one loop with a ragged edge,
#   where the input had one clean circle.
#
# So this reports, per mesh: how many boundary loops, how long each is, where it sits in the
# bounding box, and how round it is. A clean neck outline is ONE loop, near the top, with a
# circularity close to 1. Anything else is the defect.
#
#   python outline.py ours.obj theirs.obj [original.obj ...]

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


def analyse(path):
    vs, fs = load(path)

    # Directed edge use. An undirected edge used once is on a boundary; used more than twice is
    # non-manifold and is reported separately because a walk cannot pass through it.
    use = {}
    for f in fs:
        for i in range(len(f)):
            a, b = f[i], f[(i + 1) % len(f)]
            k = (a, b) if a < b else (b, a)
            use[k] = use.get(k, 0) + 1

    border = [k for k, c in use.items() if c == 1]
    nonmanifold = sum(1 for c in use.values() if c > 2)

    nxt = {}
    for (a, b) in border:
        nxt.setdefault(a, []).append(b)
        nxt.setdefault(b, []).append(a)

    lo = [min(v[k] for v in vs) for k in range(3)]
    hi = [max(v[k] for v in vs) for k in range(3)]
    span = [max(hi[k] - lo[k], 1e-12) for k in range(3)]
    diag = math.sqrt(sum(s * s for s in span))

    seen = set()
    loops = []
    for start in nxt:
        if start in seen:
            continue
        loop, cur, prev = [], start, None
        while cur is not None and cur not in seen:
            seen.add(cur)
            loop.append(cur)
            step = None
            for cand in nxt.get(cur, []):
                if cand != prev and cand not in seen:
                    step = cand
                    break
            prev, cur = cur, step
        if len(loop) >= 3:
            loops.append(loop)

    rows = []
    for loop in loops:
        pts = [vs[i] for i in loop]
        c = [sum(p[k] for p in pts) / len(pts) for k in range(3)]
        radii = [math.sqrt(sum((p[k] - c[k]) ** 2 for k in range(3))) for p in pts]
        perimeter = sum(
            math.sqrt(sum((pts[i][k] - pts[(i + 1) % len(pts)][k]) ** 2 for k in range(3)))
            for i in range(len(pts)))
        mean_r = sum(radii) / len(radii)
        # 1.0 for a perfect circle; a ragged or folded outline drops well below it.
        circularity = (2 * math.pi * mean_r / perimeter) if perimeter > 1e-12 else 0.0
        spread = (max(radii) - min(radii)) / max(mean_r, 1e-12)
        rows.append({
            "corners": len(loop),
            "perimeter%": 100.0 * perimeter / diag,
            "at": tuple(100.0 * (c[k] - lo[k]) / span[k] for k in range(3)),
            "circularity": circularity,
            "radiusSpread": spread,
        })

    rows.sort(key=lambda r: -r["perimeter%"])
    return {
        "path": path.split("/")[-1].split("\\")[-1],
        "verts": len(vs), "faces": len(fs),
        "nonmanifold": nonmanifold,
        "borderEdges": len(border),
        "loops": rows,
    }


def main(paths):
    for p in paths:
        r = analyse(p)
        print()
        print(f"{r['path']}   {r['verts']} verts, {r['faces']} faces")
        print(f"  border edges {r['borderEdges']}, non-manifold edges {r['nonmanifold']}, "
              f"loops {len(r['loops'])}")
        if not r["loops"]:
            print("  CLOSED - no outline at all. If the input was open, something lidded it.")
        for i, L in enumerate(r["loops"][:12]):
            print(f"    loop {i}: {L['corners']:4d} corners  perimeter {L['perimeter%']:6.2f}% of diag"
                  f"  at ({L['at'][0]:5.1f},{L['at'][1]:5.1f},{L['at'][2]:5.1f})%"
                  f"  circularity {L['circularity']:.3f}  radiusSpread {L['radiusSpread']:.2f}")
        if len(r["loops"]) > 12:
            print(f"    ... and {len(r['loops']) - 12} more")
    print()


if __name__ == "__main__":
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    main(a)
