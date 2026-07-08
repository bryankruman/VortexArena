#!/usr/bin/env python3
"""Diff two camera traces (apparatus A2 vs A3, or faithful vs port).

Aligns by tick index over the overlapping range and reports the max / mean divergence of the physics origin and
the rendered view origin. Use to prove the port's faithful-mode camera matches the Base-engine capture (only the
stepheight processing should differ). Run:  python compare.py <a.json> <b.json> [--tol 1.0]
"""
import json, sys, math

def frames(path):
    return json.load(open(path)).get("frames", [])

def vlen(a, b):
    return math.sqrt(sum((a[i]-b[i])**2 for i in range(3)))

def main(pa, pb, tol):
    fa, fb = frames(pa), frames(pb)
    n = min(len(fa), len(fb))
    if n == 0:
        print("one trace is empty"); return 2
    print(f"comparing {pa} vs {pb} over {n} aligned frames (tol {tol}u)")
    for key in ("physicsOrigin", "viewOrigin"):
        worst = 0.0; worst_i = -1; total = 0.0
        for i in range(n):
            d = vlen(fa[i][key], fb[i][key])
            total += d
            if d > worst: worst, worst_i = d, i
        mean = total / n
        ok = "OK" if worst <= tol else "FAIL"
        print(f"  {key:13s}: max {worst:8.4f}u @#{worst_i}   mean {mean:8.4f}u   [{ok}]")
    return 0

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__); sys.exit(1)
    tol = 1.0
    if "--tol" in sys.argv:
        tol = float(sys.argv[sys.argv.index("--tol")+1])
    sys.exit(main(sys.argv[1], sys.argv[2], tol))
