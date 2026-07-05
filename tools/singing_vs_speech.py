"""Compare a singing window vs teaching windows across reference-free discriminators.

Question: does ANY reference-free signal separate congregational singing from a single teacher, so
we could auto-switch scenes? Finding (2026-07-05): no. Room-mic flux-cv rises only ~0.1, lapel
duty-cycle drops (pastor isn't the main voice) and lapel<->room env-corr rises, but every margin
overlaps teaching's own variation. => manual scene control, not auto-detect (see ROADMAP).

Per window, per mic: level, activity duty-cycle, flux-cv (instability), plus env-correlations and
the lapel-vs-loudest-room level gap.

Usage: python singing_vs_speech.py [analysis_dir] [session_stamp] [lapel_input] [win ...]
  win = "label:start-end" in seconds, e.g. sing:5-175  (repeatable; defaults below)
Dead/silent mics (all -inf) are skipped automatically.
"""
import sys
import numpy as np
from live_wav import session_files, read_mono

args = [a for a in sys.argv[1:]]
adir = args[0] if args and ":" not in args[0] and "-" not in (args[0] or "x") else None
# simple positional parse: [dir] [stamp] [lapel] then any number of win specs
pos = [a for a in args if ":" not in a]
wins = [a for a in args if ":" in a]
adir = pos[0] if len(pos) > 0 else None
stamp = pos[1] if len(pos) > 1 else None
lapel = int(pos[2]) if len(pos) > 2 else 1
if not wins:
    wins = ["SINGING:5-175", "TEACHING-mid:300-470", "TEACHING-late:650-820"]

stamp, files = session_files(adir, stamp)
print(f"Session {stamp}  (lapel = input{lapel})\n")

mics, sr = {}, None
for idx, p in files.items():
    m, sr = read_mono(p)
    mics[idx] = m
SR = sr
L = min(len(m) for m in mics.values())


def median_db(x, hop=0.5):
    n = int(SR*hop); m = len(x)//n
    d = 20*np.log10(np.sqrt((x[:m*n].reshape(m, n).astype(np.float64)**2).mean(1)) + 1e-12)
    return float(np.median(d))  # robust to a lone startup click that inflates full-file RMS


live = [i for i in sorted(mics) if median_db(mics[i][:L]) > -100]
print("live mics:", live, " (dead/silent skipped)\n")

seg = lambda a, t0, t1: a[int(t0*SR):int(t1*SR)][:L]
rms_db = lambda x: 20*np.log10(np.sqrt(np.mean(x.astype(np.float64)**2)) + 1e-12)


def duty(x, hop=0.03, thr=-45):
    n = int(hop*SR); m = len(x)//n
    d = 20*np.log10(np.sqrt((x[:m*n].reshape(m, n).astype(np.float64)**2).mean(1)) + 1e-12)
    return float((d > thr).mean())


def flux_cv(x, N=512, hop=256):
    m = (len(x)-N)//hop
    if m < 4:
        return np.nan
    w = np.hanning(N); prev = None; fl = []
    for k in range(m):
        S = np.abs(np.fft.rfft(x[k*hop:k*hop+N]*w)); S = S/(S.sum()+1e-12)
        if prev is not None:
            fl.append(np.sqrt(((S-prev)**2).sum()))
        prev = S
    fl = np.array(fl)
    return float(fl.std()/fl.mean()) if fl.mean() > 0 else np.nan


def envlog(x, hop=0.05):
    n = int(hop*SR); m = len(x)//n
    return 20*np.log10(np.sqrt((x[:m*n].reshape(m, n).astype(np.float64)**2).mean(1)) + 1e-12)


def corr(a, b):
    a = a-a.mean(); b = b-b.mean()
    return float((a*b).sum()/(np.sqrt((a*a).sum()*(b*b).sum())+1e-12))


rooms = [i for i in live if i != lapel]
for spec in wins:
    name, rng = spec.split(":"); t0, t1 = (float(v) for v in rng.split("-"))
    print(f"=== {name}  [{t0:.0f}-{t1:.0f}s] ===")
    lap = seg(mics[lapel], t0, t1)
    print(f"  lapel(in{lapel}) duty={duty(lap):.2f}")
    lvl = {}
    for i in live:
        x = seg(mics[i], t0, t1); lvl[i] = rms_db(x)
        tag = "lapel" if i == lapel else "room "
        print(f"  {tag} in{i}: level={lvl[i]:6.1f}dB  duty={duty(x):.2f}  fluxCv={flux_cv(x):.3f}")
    el = envlog(lap)
    for r in rooms:
        print(f"  env-corr lapel<->in{r} = {corr(el, envlog(seg(mics[r], t0, t1))):.2f}")
    if len(rooms) >= 2:
        print(f"  env-corr in{rooms[0]}<->in{rooms[1]} = "
              f"{corr(envlog(seg(mics[rooms[0]],t0,t1)), envlog(seg(mics[rooms[1]],t0,t1))):.2f}")
    if rooms:
        print(f"  lapel - loudest-room = {lvl[lapel]-max(lvl[r] for r in rooms):+.1f} dB")
    print()
