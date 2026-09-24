"""Transcribe (part of) a mixer recording locally with faster-whisper.

    python tools/transcribe.py WAV [--start SEC] [--dur SEC] [--model small] [--out FILE]

Reads the mixer's 48 kHz float WAV directly, downmixes to mono and resamples to the 16 kHz Whisper
expects, so nothing leaves this machine and no ffmpeg is needed. Prints the realtime factor so the
cost of a whole service is known before running one.
"""
import argparse, sys, time
import numpy as np
import soundfile as sf
from scipy.signal import resample_poly
from faster_whisper import WhisperModel

ap = argparse.ArgumentParser()
ap.add_argument('wav')
ap.add_argument('--start', type=float, default=0)
ap.add_argument('--dur', type=float, default=0, help='0 = to the end')
ap.add_argument('--model', default='small')
ap.add_argument('--out')
a = ap.parse_args()

def read_raw(path):
    """A recording the app was killed during never gets its RIFF sizes written, so its header claims
    0 frames and soundfile reads nothing. The samples are all there: walk the chunks, then take the
    data chunk to true end of file (same approach as tools/live_wav.py)."""
    import struct
    with open(path, 'rb') as f:
        b = f.read(1 << 16)
    pos, ch, rate, bits = 12, None, None, None
    while pos + 8 <= len(b):
        cid, size = b[pos:pos + 4], struct.unpack('<I', b[pos + 4:pos + 8])[0]
        if cid == b'fmt ':
            _, ch, rate = struct.unpack('<HHI', b[pos + 8:pos + 16])
            bits = struct.unpack('<H', b[pos + 22:pos + 24])[0]
        if cid == b'data':
            assert bits == 32, f'expected float32, got {bits}-bit'
            import os
            # A kill mid-write can leave a partial sample at the end; take whole samples only.
            count = (os.path.getsize(path) - (pos + 8)) // 4
            data = np.memmap(path, dtype='<f4', mode='r', offset=pos + 8, shape=(count,))
            n = len(data) // ch
            return data[:n * ch].reshape(n, ch), rate
        pos += 8 + size + (size & 1)
    raise ValueError('no data chunk')


info = sf.info(a.wav)
if info.frames > 0:
    full, sr = None, info.samplerate
    total = info.frames
else:
    full, sr = read_raw(a.wav)
    total = len(full)
    print('(unfinalized header: reading to end of file)', file=sys.stderr)
start = int(a.start * sr)
stop = total if a.dur <= 0 else min(total, start + int(a.dur * sr))
if full is None:
    x, _ = sf.read(a.wav, start=start, stop=stop, dtype='float32', always_2d=True)
else:
    x = np.asarray(full[start:stop], dtype=np.float32)
x = x.mean(axis=1)
info_minutes = total / sr / 60
x = resample_poly(x, 16000, sr).astype(np.float32)
audio_sec = len(x) / 16000
print(f'{a.wav}: {info_minutes:.1f} min total; transcribing {audio_sec/60:.1f} min from {a.start:.0f}s',
      file=sys.stderr)

model = None
for device, ctype in (('cuda', 'int8'), ('cpu', 'int8')):
    try:
        t0 = time.time()
        model = WhisperModel(a.model, device=device, compute_type=ctype)
        print(f'model {a.model} on {device}/{ctype} loaded in {time.time()-t0:.0f}s', file=sys.stderr)
        t0 = time.time()
        # vad_filter OFF: on the 2026-09-23 prayer meeting, Silero VAD discarded the last 6 min of real
        # speech once the mix dropped 6-8 dB (transcript stopped mid-sentence at 22:16). Whisper's own
        # no-speech check still skips silent windows.
        segments, meta = model.transcribe(x, language='en', vad_filter=False, beam_size=5,
                                          condition_on_previous_text=False)
        lines = []
        for s in segments:
            m, sec = divmod(a.start + s.start, 60)
            lines.append(f'[{int(m):02d}:{sec:04.1f}] {s.text.strip()}')
        took = time.time() - t0
        break
    except Exception as e:
        print(f'{device} failed: {type(e).__name__}: {str(e)[:200]}', file=sys.stderr)
        model = None
if model is None:
    sys.exit('no device could run the model')

text = '\n'.join(lines)
if a.out:
    open(a.out, 'w', encoding='utf-8').write(text + '\n')
print(text)
print(f'\n{audio_sec:.0f}s of audio in {took:.0f}s on {device}: {audio_sec/took:.1f}x realtime',
      file=sys.stderr)
