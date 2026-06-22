"""Visual + intermittency analysis for In4 vs In5: spectrograms (to eyeball musical noise / gating /
dropouts / HF crackle) plus time-domain metrics that target 'scratchy' (which is intermittent, not
steady-state): mid-speech gate/dropout rate, spectral-flux variance (musical noise), and envelope
modulation."""
import sys, glob, os, re
import numpy as np
import soundfile as sf
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from scipy import signal

DIR = os.path.join(os.path.expanduser("~"), "Documents", "AudioMixer", "analysis")
files = glob.glob(os.path.join(DIR, "diag-input*.wav"))
rx = re.compile(r"diag-input(\d+)-(\d{8}-\d{6})\.wav$", re.I)
parsed = [(p, int(m.group(1)), m.group(2)) for p in files if (m := rx.search(os.path.basename(p)))]
stamp = sys.argv[1] if len(sys.argv) > 1 else max(p[2] for p in parsed)
byidx = {idx: p for p, idx, s in parsed if s == stamp}
print(f"Session {stamp}")

def load(idx):
    x, fs = sf.read(byidx[idx], always_2d=True)
    return x.mean(axis=1).astype(np.float64), fs

def metrics(x, fs):
    hop, win = 480, 1024
    F = (len(x)-win)//hop
    w = np.hanning(win)
    S = np.array([np.abs(np.fft.rfft(x[i*hop:i*hop+win]*w)) for i in range(F)])
    rms = np.sqrt((S**2).sum(axis=1)) + 1e-12
    db = 20*np.log10(rms/np.max(rms)+1e-12)
    voiced = db > -35                      # within-speech frames (relative to this mic's peak)
    # gate/dropout rate: voiced frames that crater to near-digital-silence (choppiness)
    deep = 20*np.log10(rms+1e-12)
    gate_rate = np.mean(deep[voiced] < -75) if voiced.any() else np.nan
    # spectral flux: frame-to-frame spectral change; musical noise -> high & erratic
    Sn = S/(S.sum(axis=1, keepdims=True)+1e-12)
    flux = np.sqrt(((np.diff(Sn, axis=0))**2).sum(axis=1))
    vflux = flux[voiced[1:]] if voiced.any() else flux
    # envelope modulation: std of log-envelope over voiced frames (jittery loudness -> scratch)
    env_mod = np.std(deep[voiced]) if voiced.any() else np.nan
    return dict(gate_rate=gate_rate, flux_mean=np.mean(vflux), flux_std=np.std(vflux),
                env_mod=env_mod, voiced_s=voiced.sum()*hop/fs)

# pick a common 4 s window with strong energy in both mics
x4, fs = load(4); x5, _ = load(5)
n = min(len(x4), len(x5))
hop = fs//10
e4 = np.array([np.sqrt(np.mean(x4[i*hop:i*hop+hop]**2)) for i in range(n//hop)])
e5 = np.array([np.sqrt(np.mean(x5[i*hop:i*hop+hop]**2)) for i in range(n//hop)])
emin = np.minimum(e4, e5)
k = int(np.argmax(np.convolve(emin, np.ones(40), "valid")))  # 4 s window (40 * 100ms)
t0 = k*hop; t1 = t0 + 4*fs
print(f"window {t0/fs:.1f}-{t1/fs:.1f}s\n")

for idx in (1, 4, 5):
    if idx not in byidx: continue
    x, _ = load(idx)
    m = metrics(x, fs)
    print(f"In{idx}: gate_rate={m['gate_rate']:.4f}  flux_mean={m['flux_mean']:.4f} "
          f"flux_std={m['flux_std']:.4f}  env_mod={m['env_mod']:.2f}dB  voiced={m['voiced_s']:.1f}s")

fig, axs = plt.subplots(3, 1, figsize=(14, 11), sharex=True)
for ax, idx, name in zip(axs, (1, 4, 5), ("In1 lapel (ref)", "In4 5-Anker GOOD", "In5 3-Anker BAD")):
    if idx not in byidx: continue
    x, _ = load(idx)
    seg = x[t0:t1]
    f, t, Sxx = signal.spectrogram(seg, fs, nperseg=1024, noverlap=768)
    ax.pcolormesh(t, f/1000, 10*np.log10(Sxx+1e-12), shading="gouraud", cmap="magma", vmin=-120, vmax=-40)
    ax.set_ylabel(f"{name}\nkHz"); ax.set_ylim(0, 12)
axs[-1].set_xlabel("time (s)")
fig.suptitle(f"Spectrograms {stamp}  ({t0/fs:.1f}-{t1/fs:.1f}s)")
fig.tight_layout()
out = os.path.join(os.path.dirname(__file__), "spectro.png")
fig.savefig(out, dpi=90)
print(f"\nsaved {out}")
