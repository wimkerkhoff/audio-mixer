"""Windowed spectrograms robust to unfinalized WAVs (memmap past the 'data' chunk). Music/singing =
sustained horizontal harmonic lines + rhythmic structure; speech = formant blobs with gaps."""
import glob, os, re
import numpy as np
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt
from scipy import signal
DIR = os.path.join(os.path.expanduser("~"), "Documents", "AudioMixer", "analysis")
rx = re.compile(r"diag-input(\d+)-(\d{8}-\d{6})\.wav$", re.I)
P = [(p, int(m.group(1)), m.group(2)) for p in glob.glob(os.path.join(DIR, "diag-input*.wav")) if (m := rx.search(os.path.basename(p)))]
FS = 48000; SECS = 14
SESS = [("20260621-102247", 16, "teaching (known speech)"),
        ("20260628-092603", 65, "today 092603 (~2 min)"),
        ("20260628-093103", 450, "today 093103 (~15 min)")]

def data_offset(path):
    with open(path, "rb") as f: head = f.read(1024)
    i = head.find(b"data"); return i + 8 if i >= 0 else 44

def win(stamp, start, mic=2):
    p = next((p for p, i, s in P if s == stamp and i == mic), None) or next(p for p, i, s in P if s == stamp)
    off = data_offset(p)
    mm = np.memmap(p, dtype="<f4", mode="r", offset=off)
    n = (len(mm)//2)*2; st = mm[:n].reshape(-1, 2)
    a = min(start*FS, max(0, len(st)-SECS*FS))
    seg = st[a:a+SECS*FS]
    return seg.mean(1).astype(np.float64)

fig, axs = plt.subplots(len(SESS), 1, figsize=(13, 10))
for ax, (stamp, start, name) in zip(axs, SESS):
    x = win(stamp, start)
    f, t, Sxx = signal.spectrogram(x, FS, nperseg=2048, noverlap=1536)
    ax.pcolormesh(t, f/1000, 10*np.log10(Sxx+1e-12), shading="gouraud", cmap="magma", vmin=-120, vmax=-45)
    ax.set_ylim(0, 5); ax.set_ylabel(f"{name}\nkHz")
axs[-1].set_xlabel("time (s)")
fig.suptitle("Spectrogram windows (mic 2) -- teaching vs today")
fig.tight_layout()
out = os.path.join(os.path.dirname(__file__), "scene.png"); fig.savefig(out, dpi=85)
print("saved", out)
