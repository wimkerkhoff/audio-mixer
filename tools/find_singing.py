"""Scan a 'record all inputs' session for candidate singing segments (multi-mic activity).

Finding (2026-07-05): this does NOT reliably separate singing from teaching in a live room — the
lapel + 2 room Ankers sit above the activity floor for basically the whole service, so 3-4 mics are
"active" continuously. Kept as a first-pass timeline viewer and as evidence that raw multi-mic
activity is not a singing detector (see ROADMAP scene-control rationale).

Usage: python find_singing.py [analysis_dir] [session_stamp]
"""
import sys
import numpy as np
from live_wav import session_files, read_mono

HOP = 0.5      # s per level hop
ACT = -45.0    # dBFS activity floor
MIN_RUN = 4.0  # s: report runs of >=3 mics active at least this long

stamp, files = session_files(sys.argv[1] if len(sys.argv) > 1 else None,
                             sys.argv[2] if len(sys.argv) > 2 else None)
print(f"Session {stamp}\n")

env, labels, sr = {}, [], None
for idx, p in files.items():
    mono, sr = read_mono(p)
    n = int(sr * HOP); m = len(mono) // n
    x = mono[:m*n].reshape(m, n).astype(np.float64)
    env[idx] = 20*np.log10(np.sqrt((x**2).mean(axis=1)) + 1e-9)
    labels.append(idx)
    print(f"input{idx}: {len(mono)/sr:6.1f}s")

L = min(len(v) for v in env.values())
E = np.vstack([env[i][:L] for i in labels])
t = np.arange(L) * HOP
nact = (E > ACT).sum(axis=0)
mmss = lambda s: f"{int(s//60)}:{int(s % 60):02d}"

print("\n== runs with >=3 mics active (candidate singing / high multi-mic activity) ==")
hot, i = nact >= 3, 0
while i < L:
    if hot[i]:
        j = i
        while j < L and hot[j]:
            j += 1
        if (j - i) * HOP >= MIN_RUN:
            means = E[:, i:j].mean(axis=1)
            print(f"  {mmss(t[i])}-{mmss(t[j-1])} ({(j-i)*HOP:4.0f}s)  avgActive={nact[i:j].mean():.1f}  "
                  + " ".join(f"in{l}:{m:.0f}" for l, m in zip(labels, means)))
        i = j
    else:
        i += 1
print(f"\ntotal {L*HOP:.0f}s. hops with k mics active (k=0..{len(labels)}):",
      [int((nact == k).sum()) for k in range(len(labels)+1)])
