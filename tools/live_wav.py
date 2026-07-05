"""Read possibly-unfinalized WAVs from the "record all inputs" diagnostic tap.

The engine writes diag-input*.wav live; while it's still recording (or if the app was killed) the
RIFF/data chunk sizes in the header are 0 or stale, so soundfile/scipy refuse or truncate them. This
reader parses the fmt chunk, then takes ALL bytes after 'data' as PCM regardless of the declared
size. Use this instead of soundfile for any tool that must run against a recording in progress.

Helpers shared by find_singing.py / singing_vs_speech.py / comb_test.py:
  session_files(dir, stamp) -> {input_index: path}   (stamp defaults to the latest session)
  read_mono(path)           -> (float32 mono, samplerate)
"""
import os, re, glob, struct
import numpy as np

RX = re.compile(r"diag-input(\d+)-(\d{8}-\d{6})\.wav$", re.I)


def session_files(dir=None, stamp=None):
    dir = dir or os.path.join(os.path.expanduser("~"), "Documents", "AudioMixer", "analysis")
    parsed = [(p, int(m.group(1)), m.group(2))
              for p in glob.glob(os.path.join(dir, "diag-input*.wav"))
              if (m := RX.search(os.path.basename(p)))]
    if not parsed:
        raise SystemExit(f"no diag-input*.wav in {dir}")
    stamp = stamp or max(p[2] for p in parsed)
    return stamp, {idx: p for (p, idx, s) in sorted(parsed, key=lambda x: x[1]) if s == stamp}


def read_mono(path):
    with open(path, "rb") as f:
        raw = f.read()
    if raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise ValueError(f"not a WAV: {path}")
    i, fmt = 12, None
    while i + 8 <= len(raw):
        cid = raw[i:i+4]; sz = struct.unpack("<I", raw[i+4:i+8])[0]; i += 8
        if cid == b"fmt ":
            tag, ch, sr, _, _, bits = struct.unpack("<HHIIHH", raw[i:i+16])
            fmt = dict(ch=ch, sr=sr, bits=bits); i += sz
        elif cid == b"data":
            data = raw[i:]; break  # ignore declared size — may be 0 while writing
        else:
            i += sz + (sz & 1)
    dt = np.float32 if fmt["bits"] == 32 else np.int16
    step = fmt["ch"] * np.dtype(dt).itemsize
    a = np.frombuffer(data[:(len(data) // step) * step], dtype=dt).reshape(-1, fmt["ch"])
    mono = a.mean(axis=1).astype(np.float32)
    if dt == np.int16:
        mono /= 32768.0
    return mono, fmt["sr"]
