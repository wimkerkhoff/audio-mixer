"""Replay OLD vs NEW (quality-weighted) Share gains over a recorded session, to verify the fix ducks
the scratchy mic in multi-mic moments. Mirrors the engine: 512-pt flux CV per 20 ms buffer (EMA 0.03),
env attack/release, natural-leader selection, then both Share gain formulas. Reports, over frames
where >=2 room mics are active and the worst-CV mic isn't the leader, the gain applied to that worst
mic OLD vs NEW. Robust to unfinalized WAV headers (memmap past the data chunk)."""
import sys, glob, os, re
import numpy as np
DIR = os.path.join(os.path.expanduser("~"), "Documents", "AudioMixer", "analysis")
rx = re.compile(r"diag-input(\d+)-(\d{8}-\d{6})\.wav$", re.I)
P = [(p, int(m.group(1)), m.group(2)) for p in glob.glob(os.path.join(DIR, "diag-input*.wav")) if (m := rx.search(os.path.basename(p)))]
stamp = sys.argv[1] if len(sys.argv) > 1 else "20260628-102608"
s_arg = float(sys.argv[2]) if len(sys.argv) > 2 else 0.5     # strength
FS, HOP, FN = 48000, 960, 512                                # 20 ms buffers; 512-pt flux FFT
AA, AR = 1-np.exp(-0.02/0.008), 1-np.exp(-0.02/0.25)
FLUX_EMA, VOICE, SIL = 0.03, 0.006, 0.0018
FLOOR_R, HOLD, HYST = 0.398, 10, 0.05
CVGOOD, CVBAD, WFLOOR = 1.0, 2.5, 0.1
p = 1 + 3*s_arg; gfloor = 0.25 + (0.03-0.25)*s_arg

def off(path):
    h = open(path, "rb").read(1024); i = h.find(b"data"); return i+8 if i >= 0 else 44
def mono(path):
    mm = np.memmap(path, dtype="<f4", mode="r", offset=off(path)); n=(len(mm)//2)*2
    return mm[:n].reshape(-1, 2)

mics = sorted([(pp, i) for pp, i, st in P if st == stamp], key=lambda x: x[1])
sig = {i: mono(pp) for pp, i in mics}
room = [i for _, i in mics if i != 1]                        # exclude lapel/priority (input 1)
F = min(len(sig[i]) for i in sig)//HOP
win = np.hanning(FN)
env = {i: 0.0 for i in sig}; mean = {i: 0.0 for i in sig}; var = {i: 0.0 for i in sig}
cv = {i: 0.0 for i in sig}; prev = {i: None for i in sig}
def weight(c):
    if c <= 0: return 1.0
    t = min(1, max(0, (c-CVGOOD)/(CVBAD-CVGOOD))); return 1 + (WFLOOR-1)*t

leader=-1; hold=0; old=[]; new=[]; worst_is_loud=0
for b in range(F):
    seg = {i: sig[i][b*HOP:(b+1)*HOP] for i in sig}
    for i in sig:
        m = seg[i].mean(1).astype(np.float64); r = np.sqrt((m**2).mean()+1e-12)
        env[i] += (r-env[i])*(AA if r > env[i] else AR)
        if r > VOICE:
            mag = np.abs(np.fft.rfft(m[:FN]*win)); ssum = mag.sum()
            if ssum > 1e-9:
                mag /= ssum
                if prev[i] is not None:
                    fl = np.sqrt(((mag-prev[i])**2).sum()); d = fl-mean[i]
                    mean[i] += FLUX_EMA*d; var[i] = (1-FLUX_EMA)*(var[i]+FLUX_EMA*d*d)
                    cv[i] = (np.sqrt(var[i])/mean[i]) if mean[i] > 1e-6 else 0
                prev[i] = mag
    lmax = max(env[i] for i in room); arg = max(room, key=lambda i: env[i])
    if lmax < SIL: leader=-1; continue
    cand = [i for i in room if env[i] >= lmax*FLOOR_R and cv[i] > 0]
    chal = min(cand, key=lambda i: cv[i]) if cand else arg
    if hold > 0: hold -= 1
    if leader < 0 or env[leader] < SIL: leader=chal; hold=HOLD
    elif chal != leader and hold <= 0 and (cv[leader] <= 0 or cv[chal] < cv[leader]-HYST): leader=chal; hold=HOLD
    active = [i for i in room if env[i] > 10**(-25/20)*1.0 or 20*np.log10(env[i]+1e-9) > -25]
    active = [i for i in room if 20*np.log10(env[i]+1e-9) > -25]
    if len(active) < 2: continue
    worst = max(active, key=lambda i: cv[i])
    if worst == leader or cv[worst] <= 0: continue
    worst_is_loud += 1
    refO = env[leader]; refN = env[leader]*weight(cv[leader])
    go = min(1, max(gfloor, (env[worst]/refO)**p))
    gn = min(1, max(gfloor, (env[worst]*weight(cv[worst])/refN)**p))
    louder = env[worst] > env[leader]                     # the geometry the fix targets
    old.append((go, louder, cv[worst], cv[leader])); new.append(gn)

print(f"Session {stamp}  strength={s_arg}  room mics={room}  buffers={F} ({F*0.02:.0f}s)")
print(f"multi-mic frames with a scratchier non-leader mic active: {worst_is_loud}")
if old:
    go = np.array([o[0] for o in old]); louder = np.array([o[1] for o in old]); gn = np.array(new)
    cvw = np.array([o[2] for o in old]); cvl = np.array([o[3] for o in old])
    print(f"  cv spread (worst vs leader): median worst={np.median(cvw):.2f}  leader={np.median(cvl):.2f}")
    print(f"  frames where the bad mic was LOUDER than the leader (fix-relevant geometry): "
          f"{int(louder.sum())} ({100*louder.mean():.0f}%)")
    if louder.sum():
        print(f"    in those: worst-mic gain OLD avg={go[louder].mean():.3f} -> NEW avg={gn[louder].mean():.3f}"
              f"   (NEW lower in {100*np.mean(gn[louder]<go[louder]-1e-3):.0f}%)")
    print(f"  overall: OLD avg={go.mean():.3f} -> NEW avg={gn.mean():.3f}")
