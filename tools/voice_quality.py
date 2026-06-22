"""Reference-free voice-quality analysis of the per-mic diagnostic WAVs.

Question: which mic sounds the most natural/human vs distorted/scratchy — WITHOUT needing
the lapel reference (so it works with no lapel and with people across the room).

Metrics (all reference-free, computed over voiced speech):
  HNR    - harmonics-to-noise ratio (Praat). Higher = cleaner/more natural; scratchy/distorted = lower.
  CPPS   - smoothed cepstral peak prominence (Praat). Robust voice-quality measure; higher = clearer.
  jitter - cycle-to-cycle pitch perturbation. Higher = rougher/scratchier.
  shimmer- cycle-to-cycle amplitude perturbation. Higher = rougher.
  hf_ratio  - energy above 5 kHz / total. Scratch/hiss/codec artifacts raise it.
  hf_flat   - spectral flatness above 5 kHz. Noise-like HF -> closer to 1.
  zcr       - zero-crossing rate. Scratchiness raises it.
  centroid  - spectral centroid (Hz).
"""
import sys, glob, os, re
import numpy as np
import soundfile as sf
import parselmouth
from parselmouth.praat import call

DIR = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.expanduser("~"), "Documents", "AudioMixer", "analysis")

files = glob.glob(os.path.join(DIR, "diag-input*.wav"))
rx = re.compile(r"diag-input(\d+)-(\d{8}-\d{6})\.wav$", re.I)
parsed = [(p, int(m.group(1)), m.group(2)) for p in files if (m := rx.search(os.path.basename(p)))]
stamp = sys.argv[2] if len(sys.argv) > 2 else max(p[2] for p in parsed)
sess = sorted([p for p in parsed if p[2] == stamp], key=lambda x: x[1])
print(f"Session {stamp} -- {len(sess)} files from {DIR}\n")

LABELS = {1: "In1(lapel)", 2: "In2(Anker)", 3: "In3(2-Anker)", 4: "In4(5-Anker GOOD)", 5: "In5(3-Anker BAD)"}

def load_mono(path):
    x, fs = sf.read(path, always_2d=True)
    return x.mean(axis=1).astype(np.float64), fs

def voiced_mask(x, fs, hop=480, win=480, thr_db=-45):
    F = len(x) // hop
    rms = np.array([np.sqrt(np.mean(x[i*hop:i*hop+win]**2) + 1e-12) for i in range(F)])
    db = 20*np.log10(rms + 1e-12)
    return rms, db > thr_db

def spectral(x, fs, mask, hop=480, win=1024):
    hf, flat, cent, zcr = [], [], [], []
    F = min(len(mask), (len(x)-win)//hop)
    w = np.hanning(win)
    for i in range(F):
        if not mask[i]:
            continue
        seg = x[i*hop:i*hop+win]
        if len(seg) < win:
            break
        zc = np.mean(np.abs(np.diff(np.sign(seg)))) / 2
        sp = np.abs(np.fft.rfind(seg*w)) if False else np.abs(np.fft.rfft(seg*w))
        freqs = np.fft.rfftfreq(win, 1/fs)
        p = sp**2 + 1e-15
        tot = p.sum()
        hfband = p[freqs >= 5000].sum()
        hf.append(hfband/tot)
        hfp = p[freqs >= 5000]
        if len(hfp) > 4:
            flat.append(np.exp(np.mean(np.log(hfp))) / (np.mean(hfp)))
        cent.append((freqs*p).sum()/tot)
        zcr.append(zc)
    return (np.mean(hf) if hf else np.nan,
            np.mean(flat) if flat else np.nan,
            np.mean(cent) if cent else np.nan,
            np.mean(zcr) if zcr else np.nan)

def praat_metrics(x, fs):
    snd = parselmouth.Sound(x, sampling_frequency=fs)
    out = {}
    try:
        harm = snd.to_harmonicity_cc(time_step=0.01, minimum_pitch=75)
        v = harm.values[harm.values != -200]
        out["HNR"] = float(np.mean(v)) if v.size else np.nan
    except Exception as e:
        out["HNR"] = np.nan; out["_hnr_err"] = str(e)
    try:
        pp = call(snd, "To PointProcess (periodic, cc)", 75, 500)
        out["jitter"] = call(pp, "Get jitter (local)", 0, 0, 0.0001, 0.02, 1.3)
        out["shimmer"] = call([snd, pp], "Get shimmer (local)", 0, 0, 0.0001, 0.02, 1.3, 1.6)
    except Exception as e:
        out["jitter"] = out["shimmer"] = np.nan; out["_js_err"] = str(e)
    try:
        pc = call(snd, "To PowerCepstrogram", 60, 0.002, 5000, 50)
        out["CPPS"] = call(pc, "Get CPPS", "yes", 0.02, 0.0005, 60, 330, 0.05,
                           "parabolic", 0.001, 0.05, "Exponential decay", "Robust")
    except Exception as e:
        out["CPPS"] = np.nan; out["_cpps_err"] = str(e)
    return out

rows = {}
for path, idx, _ in sess:
    x, fs = load_mono(path)
    rms, mask = voiced_mask(x, fs)
    # analyze only voiced portion; concatenate for praat to skip silence/gaps
    vx = x[np.repeat(mask, 480)[:len(x)]] if mask.any() else x
    hf, flat, cent, zcr = spectral(x, fs, mask)
    pm = praat_metrics(vx, fs)
    rows[idx] = dict(level=20*np.log10(np.sqrt(np.mean(vx**2))+1e-12),
                     voiced_s=mask.sum()*480/fs, hf=hf, hf_flat=flat, cent=cent, zcr=zcr, **pm)
    errs = {k: v for k, v in pm.items() if k.startswith("_")}
    if errs:
        print(f"  {LABELS.get(idx,idx)} praat notes: {errs}")

hdr = f"{'mic':<18}{'level':>7}{'HNR':>7}{'CPPS':>7}{'jitter%':>8}{'shimmer%':>9}{'hf_ratio':>9}{'hf_flat':>8}{'cent':>7}{'zcr':>7}"
print("\n" + hdr)
for idx in sorted(rows):
    r = rows[idx]
    print(f"{LABELS.get(idx,str(idx)):<18}{r['level']:>7.1f}{r['HNR']:>7.1f}{r['CPPS']:>7.2f}"
          f"{r['jitter']*100:>8.2f}{r['shimmer']*100:>9.2f}{r['hf']:>9.4f}{r['hf_flat']:>8.3f}{r['cent']:>7.0f}{r['zcr']:>7.4f}")

# In4 vs In5 verdict (which way each metric points; arrow shows which mic each prefers as "more natural")
print("\n=== In4 (GOOD/natural) vs In5 (BAD/scratchy) ===")
better_high = ["HNR", "CPPS"]          # higher = more natural
better_low = ["jitter", "shimmer", "hf", "hf_flat", "zcr"]  # lower = more natural
if 4 in rows and 5 in rows:
    a, b = rows[4], rows[5]
    for k in ["level"] + better_high + better_low:
        va, vb = a[k], b[k]
        if k in better_high:
            winner = "In4" if va > vb else "In5"
            ok = "  <- correct (In4)" if winner == "In4" else "  x picks In5"
        elif k in better_low:
            winner = "In4" if va < vb else "In5"
            ok = "  <- correct (In4)" if winner == "In4" else "  x picks In5"
        else:
            ok = ""
        print(f"  {k:<9} In4={va:>8.3f}  In5={vb:>8.3f}{ok}")
