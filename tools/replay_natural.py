"""Offline replay of the live 'prefer natural mic' selector over the recorded per-mic WAVs.

Mirrors the engine: per-frame spectral-flux instability (InputChannel.ComputeFlux, EMA mean/var ->
CV), smoothed level envelope (AutoMixer attack/release), and the selection rule -- among room mics
within NaturalFloorDb of the loudest, pick the lowest CV, held with hysteresis. Reports how much of
the voiced time each mic is selected, vs plain loudest-wins. Success = In4 (the mic you judged best)
wins most of the time and beats In5. Lets us validate the algorithm now, without a live session.

Usage: python replay_natural.py [stamp]   (defaults to the 102247 labelled session)
"""
import sys, glob, os, re
import numpy as np
import soundfile as sf

DIR = os.path.join(os.path.expanduser("~"), "Documents", "AudioMixer", "analysis")
files = glob.glob(os.path.join(DIR, "diag-input*.wav"))
rx = re.compile(r"diag-input(\d+)-(\d{8}-\d{6})\.wav$", re.I)
parsed = [(p, int(m.group(1)), m.group(2)) for p in files if (m := rx.search(os.path.basename(p)))]
stamp = sys.argv[1] if len(sys.argv) > 1 else "20260621-102247"
sess = sorted([p for p in parsed if p[2] == stamp], key=lambda x: x[1])
ROOM = [i for _, i, _ in sess if i != 1]            # room mics compete; lapel (1) excluded
LABELS = {2: "In2", 3: "In3", 4: "In4(GOOD)", 5: "In5(BAD)"}
print(f"Session {stamp}  room mics {ROOM}\n")

HOP, WIN, FLUXN = 480, 1024, 512                    # 10 ms frames; 512-pt flux FFT (matches engine)
A_ATT = 1 - np.exp(-0.010/0.008); A_REL = 1 - np.exp(-0.010/0.250)
FLUX_EMA = 0.02; SIL = 0.0018; VOICE = 0.006
FLOOR = 10**(-8/20); HOLD = 20; HYST = 0.05

def frames(path):
    x, fs = sf.read(path, always_2d=True); x = x.mean(1)
    F = (len(x)-WIN)//HOP
    win = np.hanning(FLUXN); hwin = np.hanning(WIN)
    rms = np.empty(F); cv = np.empty(F)
    mean = var = 0.0; prev = None
    for f in range(F):
        seg = x[f*HOP:f*HOP+WIN]
        rms[f] = np.sqrt(np.mean(seg**2)+1e-12)
        if rms[f] > VOICE:
            mag = np.abs(np.fft.rfft(seg[:FLUXN]*win)); s = mag.sum()
            if s > 1e-9:
                mag /= s
                if prev is not None:
                    fl = np.sqrt(((mag-prev)**2).sum())
                    d = fl-mean; mean += FLUX_EMA*d; var = (1-FLUX_EMA)*(var+FLUX_EMA*d*d)
                prev = mag
        cv[f] = (np.sqrt(var)/mean) if mean > 1e-6 else 0.0
    return rms, cv, F

data = {i: frames(p) for p, i, _ in sess if i in ROOM}
F = min(v[2] for v in data.values())
env = {i: 0.0 for i in ROOM}

def replay(natural):
    leader = -1; hold = 0; sel_count = {i: 0 for i in ROOM}; voiced = 0; flips = 0; prev = -1
    e = {i: 0.0 for i in ROOM}
    for f in range(F):
        for i in ROOM:
            inst = data[i][0][f]
            e[i] += (inst - e[i]) * (A_ATT if inst > e[i] else A_REL)
        lmax = max(e.values()); arg = max(ROOM, key=lambda i: e[i])
        if lmax < SIL:
            prev = -1; continue
        voiced += 1
        if natural:
            floor = lmax*FLOOR
            cands = [i for i in ROOM if e[i] >= floor and data[i][1][f] > 0]
            chal = min(cands, key=lambda i: data[i][1][f]) if cands else arg
            better = lambda c, h: data[c][1][f] < data[h][1][f] - HYST
        else:
            chal = arg
            better = lambda c, h: e[c] > e[h]*1.413
        if hold > 0: hold -= 1
        if leader < 0 or e[leader] < SIL:
            leader = chal; hold = HOLD
        elif chal != leader and hold <= 0 and better(chal, leader):
            leader = chal; hold = HOLD
        sel_count[leader] += 1
        if prev >= 0 and leader != prev: flips += 1
        prev = leader
    return sel_count, voiced, flips

for name, nat in [("loudest-wins", False), ("prefer-natural", True)]:
    sc, v, fl = replay(nat)
    share = {i: 100*sc[i]/max(1, v) for i in ROOM}
    top = max(ROOM, key=lambda i: sc[i])
    print(f"{name:<16} flips={fl:<4} " + "  ".join(f"{LABELS[i]}:{share[i]:4.0f}%" for i in ROOM)
          + f"   -> top={LABELS[top]}")
print("\n(success = prefer-natural makes In4(GOOD) the top pick and shrinks In5(BAD))")
