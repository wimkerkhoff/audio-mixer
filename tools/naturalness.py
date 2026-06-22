"""Reference-free 'naturalness' via temporal-stability / transient-artifact detection.

Key finding: the bad Anker (In5) isn't noisy — its DSP OVER-processes, which inflates cleanliness
metrics (HNR/CPPS even exceed the clean lapel). What actually sounds scratchy is intermittent:
broadband transient clicks + gating chatter + musical noise = an UNSTABLE spectrum over time.
So instead of measuring cleanliness, we measure INSTABILITY (lower = more natural):

  flux_std   - std of spectral flux over voiced frames (erratic spectral change)
  flux_cv    - flux_std / flux_mean (instability normalized for speech dynamics)
  transient  - fraction of voiced frames with a broadband flux spike (>median+4*MAD) = clicks
  hf_burst   - coeff. of variation of high-band (>4 kHz) energy = musical-noise burstiness
  artifact   - combined z-scored instability (the headline; lower = more natural)

All per-mic (no reference needed). Validate: lapel + In4 should score LOW; In5 should score HIGH/worst.
"""
import sys, glob, os, re
import numpy as np
import soundfile as sf

DIR = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.expanduser("~"), "Documents", "AudioMixer", "analysis")
files = glob.glob(os.path.join(DIR, "diag-input*.wav"))
rx = re.compile(r"diag-input(\d+)-(\d{8}-\d{6})\.wav$", re.I)
parsed = [(p, int(m.group(1)), m.group(2)) for p in files if (m := rx.search(os.path.basename(p)))]
stamp = sys.argv[2] if len(sys.argv) > 2 else max(p[2] for p in parsed)
sess = sorted([p for p in parsed if p[2] == stamp], key=lambda x: x[1])
LABELS = {1: "In1(lapel)", 2: "In2(Anker)", 3: "In3(2-Anker)", 4: "In4(GOOD)", 5: "In5(BAD)"}
print(f"Session {stamp}\n")

def analyze(path, fs_hop=480, win=1024):
    x, fs = sf.read(path, always_2d=True)
    x = x.mean(axis=1).astype(np.float64)
    F = (len(x) - win) // fs_hop
    w = np.hanning(win)
    S = np.empty((F, win // 2 + 1))
    for i in range(F):
        S[i] = np.abs(np.fft.rfft(x[i*fs_hop:i*fs_hop+win] * w))
    freqs = np.fft.rfftfreq(win, 1/fs)
    rms = np.sqrt((S**2).sum(axis=1)) + 1e-12
    db = 20*np.log10(rms / rms.max() + 1e-12)
    voiced = db > -35
    Sn = S / (S.sum(axis=1, keepdims=True) + 1e-12)
    flux = np.sqrt((np.diff(Sn, axis=0)**2).sum(axis=1))          # spectral flux per frame
    vf = flux[voiced[1:]]
    med, mad = np.median(vf), np.median(np.abs(vf - np.median(vf))) + 1e-9
    transient = np.mean(vf > med + 4*mad)
    hf = S[:, freqs >= 4000].sum(axis=1)[voiced]
    hf_burst = np.std(hf) / (np.mean(hf) + 1e-12)
    return dict(flux_mean=vf.mean(), flux_std=vf.std(), flux_cv=vf.std()/(vf.mean()+1e-12),
                transient=transient, hf_burst=hf_burst, voiced_s=voiced.sum()*fs_hop/fs)

rows = {idx: analyze(p) for p, idx, _ in sess}

keys = ["flux_std", "flux_cv", "transient", "hf_burst"]
# z-score each instability metric across mics, average -> artifact score (higher = worse)
mat = {k: np.array([rows[i][k] for i in sorted(rows)]) for k in keys}
z = {k: (mat[k] - mat[k].mean()) / (mat[k].std() + 1e-9) for k in keys}
artifact = np.mean([z[k] for k in keys], axis=0)
for j, i in enumerate(sorted(rows)):
    rows[i]["artifact"] = artifact[j]

hdr = f"{'mic':<14}{'flux_std':>9}{'flux_cv':>9}{'transient':>10}{'hf_burst':>9}{'ARTIFACT':>10}"
print(hdr)
for i in sorted(rows):
    r = rows[i]
    print(f"{LABELS.get(i,str(i)):<14}{r['flux_std']:>9.4f}{r['flux_cv']:>9.3f}"
          f"{r['transient']:>10.4f}{r['hf_burst']:>9.3f}{r['artifact']:>10.2f}")

order = sorted(rows, key=lambda i: rows[i]["artifact"])
print("\nMost natural -> least (by artifact score):")
print("  " + "  >  ".join(LABELS.get(i, str(i)) for i in order))
if 4 in rows and 5 in rows:
    ok = rows[4]["artifact"] < rows[5]["artifact"]
    print(f"\nIn4 vs In5: In4 artifact={rows[4]['artifact']:.2f}  In5={rows[5]['artifact']:.2f}  "
          f"-> {'In4 more natural (CORRECT)' if ok else 'picks In5 (WRONG)'}")
