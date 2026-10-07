import sys, re, subprocess, glob, statistics as st
# agg.py pattern-with-{s} settings... : mean metrics per setting over all matching runs
def metrics(prefix):
    out = subprocess.run(['python','qrmetric.py',prefix+'.obj'],capture_output=True,text=True).stdout
    p05 = float(re.search(r'p05=([0-9.]+)',out).group(1)); faces=int(re.search(r'faces=(\d+)',out).group(1))
    log = open(prefix+'.log',encoding='utf-8',errors='ignore').read()
    irr = float(re.findall(r'irregular=\d+/\d+=([0-9.]+)%',log)[-1]); h=float(re.findall(r'hausdorff=([0-9.]+)',log)[-1])
    op = int(re.findall(r'open=(\d+)',log)[-1]); o=re.search(r'orth mean=([0-9.]+)',log); ms=int(re.findall(r'totalMs=(\d+)',log)[-1])
    io=re.search(r'inputToOutput mean=([0-9.]+)',log)
    return faces,p05,irr,h,op,float(o.group(1)) if o else 0,ms,float(io.group(1)) if io else 0
for pat in sys.argv[1:]:
    rows=[metrics(f[:-4]) for f in sorted(glob.glob(pat+'.obj'))]
    if not rows: print(pat,'none'); continue
    m=[st.mean(c) for c in zip(*rows)]
    print(f"{pat:28s} n={len(rows)} faces={m[0]:.0f} p05={m[1]:.1f} (min {min(r[1] for r in rows):.1f}) irr={m[2]:.2f}% haus={m[3]:.4f} open={m[4]:.0f} orth={m[5]:.1f} in2out={m[7]:.3f} ms={m[6]:.0f}")
