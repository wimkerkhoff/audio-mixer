"""Review the live 'Prefer natural' selection from today's log transcript. For each output, track the
held winner (from auto-mix lines) and, at each 1 s level snapshot, compare the winner to the LOUDEST
mic. If Prefer natural is doing its job it will sometimes hold a quieter mic (override loudest);
if it always == loudest it's just falling back to loudest-wins. Also reports flip rate (stability)."""
import re
f = r"C:\Users\FreeGrace\AppData\Local\Temp\AudioMixer.log"
rxb = re.compile(r"AudioMixer started 2026-06-28")
rxsel = re.compile(r"^(\d\d:\d\d:\d\d)\.\d+ Output ([AB]) auto-mix: .*→ (mic(\d+)|none)")
rxin = re.compile(r"^(\d\d:\d\d:\d\d)\.\d+ Input (\d) .*inputDb=(-?\d+\.?\d*) .*routes=\[(\d),(\d)\]")

win = {"A": -1, "B": -1}
flips = {"A": 0, "B": 0}
cur = None
lv = {}; rt = {}
# per output: voiced secs, winner==loudest, winner!=loudest (+gap)
stat = {o: {"v": 0, "same": 0, "over": 0, "gaps": []} for o in "AB"}

def evalsec():
    for o, oi in (("A", 0), ("B", 1)):
        cand = {i: lv[i] for i in lv if i != 0 and rt.get(i, (0, 0))[oi] == 1}
        if not cand: continue
        mx = max(cand.values())
        if mx <= -25: continue                      # nobody really talking
        w = win[o]
        if w not in cand: continue
        stat[o]["v"] += 1
        loudest = max(cand, key=cand.get)
        if w == loudest: stat[o]["same"] += 1
        else:
            stat[o]["over"] += 1
            stat[o]["gaps"].append(mx - cand[w])

on = False
with open(f, encoding="utf-8", errors="ignore") as fh:
    for ln in fh:
        if not on:
            if rxb.search(ln): on = True
            continue
        m = rxsel.match(ln)
        if m:
            o = m.group(2); w = -1 if m.group(3) == "none" else int(m.group(4)) - 1
            if w != win[o] and w >= 0 and win[o] >= 0: flips[o] += 1
            win[o] = w
            continue
        m = rxin.match(ln)
        if m:
            s = m.group(1)
            if s != cur:
                if cur and lv: evalsec()
                cur = s; lv = {}; rt = {}
            i = int(m.group(2)); lv[i] = float(m.group(3)); rt[i] = (int(m.group(4)), int(m.group(5)))
if cur and lv: evalsec()

import statistics as st
for o in "AB":
    s = stat[o]; v = s["v"] or 1
    g = st.median(s["gaps"]) if s["gaps"] else 0
    print(f"Output {o}: voiced={s['v']}s  flips={flips[o]}  "
          f"winner==loudest {100*s['same']/v:3.0f}%  override {100*s['over']/v:3.0f}% "
          f"(median {g:.1f} dB quieter when overriding)")
print("\noverride% > 0 means Prefer natural actively chose a quieter, more-natural mic over the loudest")
