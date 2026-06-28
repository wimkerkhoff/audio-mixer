"""Scene analysis robust to unfinalized WAVs (header says 0 frames but PCM data is present): memmap
the float32 data past the 'data' chunk. Streams a 100 ms RMS envelope per mic (no full load), then
reports sustain/continuity/modulation -- to tell singing (sustained, low modulation, few gaps) from
teaching (gappy, ~4 Hz syllable modulation)."""
import sys, glob, os, re
import numpy as np
DIR = os.path.join(os.path.expanduser("~"), "Documents", "AudioMixer", "analysis")
rx = re.compile(r"diag-input(\d+)-(\d{8}-\d{6})\.wav$", re.I)
P = [(p, int(m.group(1)), m.group(2)) for p in glob.glob(os.path.join(DIR, "diag-input*.wav")) if (m := rx.search(os.path.basename(p)))]
FS = 48000

def data_offset(path):
    with open(path, "rb") as f: head = f.read(1024)
    i = head.find(b"data")
    return i + 8 if i >= 0 else 44

def envelope(path, hop_ms=100):
    off = data_offset(path)
    mm = np.memmap(path, dtype="<f4", mode="r", offset=off)
    n = (len(mm) // 2) * 2
    st = mm[:n].reshape(-1, 2)
    H = FS * hop_ms // 1000
    F = len(st) // H
    env = np.empty(F)
    for f in range(F):
        seg = st[f*H:(f+1)*H]
        env[f] = np.sqrt(np.mean(seg[:, 0].astype(np.float64)**2 + seg[:, 1].astype(np.float64)**2) / 2 + 1e-12)
    return env

def mod_rate(env):
    e = env - env.mean()
    if np.allclose(e, 0): return 0.0
    sp = np.abs(np.fft.rfft(e * np.hanning(len(e))))
    fr = np.fft.rfftfreq(len(e), hop := 0.1)
    b = (fr > 0.3) & (fr < 10)
    return fr[b][np.argmax(sp[b])]

def analyze(stamp):
    mics = sorted([(p, i) for p, i, s in P if s == stamp], key=lambda x: x[1])
    if not mics: return f"{stamp}: none"
    envs = {i: envelope(p) for p, i in mics}
    F = min(len(e) for e in envs.values())
    dbm = {i: 20*np.log10(envs[i][:F] + 1e-9) for i in envs}
    peak = max(d.max() for d in dbm.values())
    loud = np.max(np.vstack([dbm[i] for i in dbm]), axis=0)
    active = loud > peak - 40
    # silence gaps (>=300 ms) per minute = speech has many phrase gaps; singing few
    gaps, c = 0, 0
    for a in active:
        if not a: c += 1
        else:
            if c >= 3: gaps += 1
            c = 0
    gpm = gaps / (F * 0.1 / 60 + 1e-9)
    hot = [sum(1 for i in dbm if dbm[i][f] > loud[f]-6) for f in range(F) if active[f]]
    mr = np.median([mod_rate(envs[i][:F]) for i in envs])
    return (f"{stamp}: {len(mics)}mic {F*0.1:5.0f}s | active={active.mean()*100:3.0f}%  "
            f"silence-gaps/min={gpm:4.1f}  hot-mics={np.mean(hot):.2f}  mod-rate={mr:.1f}Hz")

for s in (sys.argv[1:] or ["20260621-102247", "20260628-092603", "20260628-093103"]):
    print(analyze(s))
print("\n(speech: more gaps/min, mod-rate ~3-6 Hz; singing: few gaps, sustained, lower mod-rate)")
