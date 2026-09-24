"""Repair WAV headers left unfinalized when the mixer was killed mid-recording.

    python tools/wavfix.py [--apply] [--skip STAMP]... DIR [DIR ...]

NAudio's WaveFileWriter writes placeholder sizes and only fills them in on Flush/Dispose, so a killed
recording keeps a header claiming 0 frames: every normal player and soundfile read it as empty even
though all the audio is on disk. This rewrites only the size fields — RIFF size, data size and the
'fact' sample count NAudio adds for float formats — and trims a partial last frame (under one
sample) left by the kill, so the data length is a whole number of frames. Without --apply it only
reports.
"""
import argparse, glob, os, struct, sys

ap = argparse.ArgumentParser()
ap.add_argument('dirs', nargs='+')
ap.add_argument('--apply', action='store_true')
ap.add_argument('--skip', action='append', default=[], help='stamp to leave alone (in use); repeatable')
a = ap.parse_args()


def chunks(b):
    pos = 12
    while pos + 8 <= len(b):
        cid, size = b[pos:pos + 4], struct.unpack('<I', b[pos + 4:pos + 8])[0]
        yield cid, pos, size
        if cid == b'data':
            return
        pos += 8 + size + (size & 1)


broken = ok = skipped = 0
for d in a.dirs:
    for path in sorted(glob.glob(os.path.join(d, '*.wav'))):
        name = os.path.basename(path)
        if any(s in name for s in a.skip):
            skipped += 1
            continue
        filesize = os.path.getsize(path)
        with open(path, 'rb') as f:
            head = f.read(1 << 16)
        if head[:4] != b'RIFF' or head[8:12] != b'WAVE':
            print(f'  not a WAV, left alone: {name}')
            continue
        found = {cid: (pos, size) for cid, pos, size in chunks(head)}
        if b'data' not in found or b'fmt ' not in found:
            print(f'  no fmt/data chunk, left alone: {name}')
            continue
        fmt_pos = found[b'fmt '][0]
        channels = struct.unpack('<H', head[fmt_pos + 10:fmt_pos + 12])[0]
        block = struct.unpack('<H', head[fmt_pos + 20:fmt_pos + 22])[0]
        rate = struct.unpack('<I', head[fmt_pos + 12:fmt_pos + 16])[0]
        data_pos, data_size = found[b'data']
        data_start = data_pos + 8
        frames = (filesize - data_start) // block
        want_data = frames * block
        if data_size == want_data and struct.unpack('<I', head[4:8])[0] == data_start + want_data - 8:
            ok += 1
            continue
        broken += 1
        print(f'  {"FIX " if a.apply else "BAD "} {name}: header says {data_size // block} frames, '
              f'file holds {frames} ({frames / rate / 60:.1f} min, {channels} ch)')
        if not a.apply:
            continue
        with open(path, 'r+b') as f:
            f.truncate(data_start + want_data)          # drop the partial frame, if any
            f.seek(4)
            f.write(struct.pack('<I', data_start + want_data - 8))
            f.seek(data_pos + 4)
            f.write(struct.pack('<I', want_data))
            if b'fact' in found:
                f.seek(found[b'fact'][0] + 8)
                f.write(struct.pack('<I', frames))

print(f'\n{broken} {"repaired" if a.apply else "need repair"}, {ok} already fine, {skipped} skipped (in use)')
