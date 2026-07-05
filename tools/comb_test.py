"""Measure whether summing two room mics comb-filters during a given window.

Comb filtering needs the two mics to carry a COHERENT copy of the same source. For one talker
(teaching) that's true -> summing combs -> the automixer ducks to one mic. For a congregation
(singing) each mic hears different nearby singers -> low coherence -> summing does NOT comb.

Finding (2026-07-05 singing window): room mics 4<->5 waveform coherence ~0.13, best delay ~15 ms,
and summing added ~0 comb ripple (6.5 vs 6.7 dB). => the Singing scene can open several room mics
without the comb penalty that justifies duck-to-one in teaching (see ROADMAP).

Usage: python comb_test.py [analysis_dir] [session_stamp] [inputA] [inputB] [start] [end]
  defaults: two lowest-index live room mics, window 5-175 s.
"""
import sys
import numpy as np
from live_wav import session_files, read_mono

pos = sys.argv[1:]
adir = pos[0] if len(pos) > 0 else None
stamp = pos[1] if len(pos) > 1 else None
inA = int(pos[2]) if len(pos) > 2 else None
inB = int(pos[3]) if len(pos) > 3 else None
t0 = float(pos[4]) if len(pos) > 4 else 5.0
t1 = float(pos[5]) if len(pos) > 5 else 175.0

stamp, files = session_files(adir, stamp)
sigs = {i: read_mono(p) for i, p in files.items()}
SR = next(iter(sigs.values()))[1]


def live_db(i):
    x = sigs[i][0].astype(np.float64)
    return 20*np.log10(np.sqrt((x**2).mean()) + 1e-12)


if inA is None or inB is None:
    # two loudest non-lapel(1) room mics over the window; skips the dead/silent ones
    def wlvl(i):
        x = sigs[i][0][int(t0*SR):int(t1*SR)].astype(np.float64)
        return 20*np.log10(np.sqrt((x**2).mean()) + 1e-12)
    rooms = sorted((i for i in sigs if i != 1), key=wlvl, reverse=True)
    inA, inB = rooms[0], rooms[1]
print(f"Session {stamp}  summing input{inA} + input{inB}  window {t0:.0f}-{t1:.0f}s\n")

a = sigs[inA][0][int(t0*SR):int(t1*SR)].astype(np.float64)
b = sigs[inB][0][int(t0*SR):int(t1*SR)].astype(np.float64)
n = min(len(a), len(b)); a, b = a[:n], b[:n]

# best inter-mic delay & waveform coherence over +-30 ms
maxlag = int(0.03*SR)
a0, b0 = a-a.mean(), b-b.mean()
cc = np.correlate(a0[maxlag:-maxlag], b0, "valid")
lag = cc.argmax()-maxlag
delay_ms = lag/SR*1000
coh = cc.max()/np.sqrt((a0**2).sum()*(b0**2).sum())
print(f"best delay = {delay_ms:+.2f} ms   waveform coherence = {coh:.2f}")
if abs(delay_ms) > 0.05:
    print(f"  if coherent, comb notches every ~{1000/abs(delay_ms):.0f} Hz (first null ~{500/abs(delay_ms):.0f} Hz)")


def spec(x, N=8192, hop=4096):
    w = np.hanning(N); acc = np.zeros(N//2+1); cnt = 0
    for k in range(0, len(x)-N, hop):
        fr = x[k:k+N]*w
        if np.sqrt((fr**2).mean()) < 10**(-45/20):
            continue
        acc += np.abs(np.fft.rfft(fr))**2; cnt += 1
    return acc/max(cnt, 1)


freqs = np.fft.rfftfreq(8192, 1/SR)
band = (freqs >= 300) & (freqs <= 4000)


def ripple(S):
    from numpy.lib.stride_tricks import sliding_window_view
    s = 10*np.log10(S[band]+1e-20)
    w = sliding_window_view(s, 64)
    return float(np.mean(w.max(1)-w.min(1)))


r_sum = ripple(spec(a+b)); r_one = ripple(spec(b*2))
print(f"\nspectral ripple 300-4kHz (peak-trough dB):  SUM({inA}+{inB})={r_sum:.1f}   SINGLE={r_one:.1f}")
print("higher ripple = more comb notching/phasiness; ~equal => summing did not comb")
