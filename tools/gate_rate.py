"""Per-mic digital-silence (noise-gate) rate, and whether the mics gate TOGETHER.

The Anker S500s' noise suppression gates to true digital zero. During continuous sound (singing,
music) every closure is a hole in the signal — and because all units hear the same acoustics they
close *in unison*, so summing more mics cannot fill the holes. This quantifies both.

    python tools/gate_rate.py                          # whole latest session
    python tools/gate_rate.py --seg 95:265 --seg 20:85 # labelled windows, in seconds
    python tools/gate_rate.py --stamp 20260809-092931

Reads in-progress recordings via tools/live_wav.py, so it works mid-session.

Key numbers in the output:
  silent%              fraction of frames at true digital silence, per mic
  ALL silent           fraction where every room mic is silent at once = total stream dropout
  independent          what that would be if the gates were uncorrelated (product of the rates)
  simultaneity         ALL / independent. ~1x = independent (summing helps). >>1x = unison (it can't).
  gaps                 contiguous all-silent runs; >100 ms is clearly audible as an interruption

Use it to A/B a device setting (e.g. one S500 in Broadcast pickup mode, the rest Standard as a
control): same acoustic input, so a real improvement shows as a lower silent% on that unit alone.
"""
import argparse
import itertools
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from live_wav import session_files, read_mono  # noqa: E402

FRAME = 0.020          # 20 ms analysis frame
SILENCE_PEAK = 1e-5    # below this a frame is the gate's output, not merely quiet


def analyse(sig, sr, room, names, a, b, label):
    n = int(FRAME * sr)

    def frames(x):
        seg = x[int(a * sr):int(b * sr)]
        return seg[:(len(seg) // n) * n].reshape(-1, n)

    if min(len(frames(sig[i])) for i in room) < 10:
        print(f"[{label}] window too short, skipped\n")
        return

    silent, voiced_db = {}, {}
    for i in room:
        f = frames(sig[i])
        silent[i] = np.abs(f).max(axis=1) < SILENCE_PEAK
        rms = np.sqrt((f ** 2).mean(axis=1))
        live = rms[~silent[i]]
        voiced_db[i] = 20 * np.log10(live.mean() + 1e-12) if len(live) else float("nan")

    print(f"=== {label}  ({a:.0f}-{b:.0f}s, {len(silent[room[0]])} frames) ===")
    for i in room:
        print(f"  {names[i]:10} silent {silent[i].mean()*100:5.1f}%   "
              f"voiced level {voiced_db[i]:6.1f} dBFS")

    # mid-session the writers are at slightly different lengths — align before stacking
    m = min(len(silent[i]) for i in room)
    stack = np.vstack([silent[i][:m] for i in room])
    nsil = stack.sum(axis=0)
    all_sil = nsil == len(room)
    indep = float(np.prod([silent[i][:m].mean() for i in room]))
    print(f"  --> ALL silent at once {all_sil.mean()*100:5.1f}%   "
          f"independent {indep*100:5.1f}%   "
          f"simultaneity {all_sil.mean()/indep:6.1f}x" if indep > 0 else
          f"  --> ALL silent at once {all_sil.mean()*100:5.1f}%")
    print(f"      at least one silent    {(nsil > 0).mean()*100:5.1f}%")

    runs = [len(list(g)) * FRAME * 1000 for k, g in itertools.groupby(all_sil) if k]
    if runs:
        span = b - a
        print(f"      dropouts: n={len(runs)} (one per {span/len(runs):.1f}s)  "
              f"median={np.median(runs):.0f}ms  max={max(runs):.0f}ms  "
              f">100ms: {sum(1 for r in runs if r > 100)}")
    print()


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dir", default=None, help="analysis dir (default ~/Documents/AudioMixer/analysis)")
    ap.add_argument("--stamp", default=None, help="session stamp (default: latest)")
    ap.add_argument("--seg", action="append", default=[],
                    help="window START:END in seconds; repeatable. Default: whole recording")
    ap.add_argument("--lapel", type=int, default=1,
                    help="1-based diag-input index that is the lapel, excluded from the room set (0 = none)")
    args = ap.parse_args()

    stamp, files = session_files(args.dir, args.stamp)
    sig, sr, dur = {}, None, None
    for idx, path in files.items():
        x, sr = read_mono(path)
        sig[idx] = x
        dur = len(x) / sr
    room = [i for i in sorted(files) if i != args.lapel]
    names = {i: f"diag-in{i}" for i in files}
    print(f"session {stamp}   {dur:.1f}s @ {sr} Hz   room mics: {[names[i] for i in room]}\n")

    segs = []
    for s in args.seg:
        lo, hi = s.split(":")
        segs.append((float(lo), min(float(hi), dur), f"{lo}-{hi}s"))
    if not segs:
        segs = [(0.0, dur, "WHOLE RECORDING")]

    for a, b, label in segs:
        analyse(sig, sr, room, names, a, b, label)


if __name__ == "__main__":
    main()
