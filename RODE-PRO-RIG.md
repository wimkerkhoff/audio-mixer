# RØDE Wireless PRO room rig (up to 6 TX) — plan and watch-list

Status: **planned**, nothing bought beyond the first kit. Written 2026-08-30. Scope owner: this
file. `CLAUDE.md` documents what IS; `ROADMAP.md` holds the wider backlog. Fold each item's *result*
into `CLAUDE.md` as it is established, and delete this file once the rig is commissioned and stable.

---

## Target configuration

| Role | Hardware | Mixer binding |
| --- | --- | --- |
| Presenter | RØDE Classic (single TX) → RX 3.5 mm → Realtek jack | 1 strip, **priority**, Role=Lapel |
| Room ×6 | 3× Wireless PRO kits = 3 RX (USB-C) × 2 TX, **built-in mics**, no lapels plugged | 6 strips, 2 per RX |
| Outputs | monitor headset + VB-CABLE → Zoom/OBS | 2 buses |

7 mixer inputs total, inside `AudioEngine.MaxInputCount` (10). The TX units are spread for audience
coverage, not worn — the presenter's own voice is owned by the lapel.

## Why this rig

The S500s were retired because their DSP gated congregational singing to digital silence *in unison*
(`CLAUDE.md` finding 4), which no mix topology can repair. The Wireless PRO is DSP-free: measured
**0.0% digital silence in 9 minutes** against 4.1% / 7.1% for two live Ankers, which lost 21.6 s and
36.9 s of audio to gate holes over the same capture (finding 5). Six identical DSP-free capsules
also restore the automixer's core assumption — with no AGC anywhere, a level difference is
*distance*, so Gate on smoothed level with stable hand-off becomes a true proximity selector
(finding 6a).

## Verified wiring facts

- **Each RX is ONE 2-channel WASAPI endpoint** — TX1 on left, TX2 on right, in Split mode. Bind it
  to two strips via `ChannelSource.Left` / `Right`; device exclusivity is per *side*, not per
  endpoint.
- The split is genuine, verified at sample level 2026-08-30 with `tools/RxProbe`: sample-level
  corr(L,R) **0.28** at −0.38 ms, best-scalar fit residual only **0.08 dB** below R (so R is not a
  scaled copy of L), envelope corr 0.86. A Safety-mode duplicate would read ~1.0.
- The capture chain takes **only channels 0 and 1** of an endpoint, so 2 ch per RX is safe. A
  >2-input USB interface would silently drop everything past its second input.

---

## Watch-list

### 1. Device identity — the biggest operational risk on this rig

The RX has **no USB serial**; its instance path is port-derived
(`USB\VID_19F7&PID_0058&MI_01\7&6941B14&0&0001`). The endpoint GUID, the Windows **rename**, and the
Windows **endpoint gain** are all keyed to it. With three identical receivers that bites twice:

- `DeviceResolver.DeviceNameKey` strips the `(N- …)` enumerator, so all three collapse to the same
  key `Desktop Microphone (Wireless PRO RX)`. The preset's name fallback cannot tell them apart; the
  `used` set only prevents double-grabbing, so strips fill in **arbitrary order**.
- Moving an RX to a different hub port mints a **fresh endpoint** — rename gone, gain back to 0 dB.
  The operator currently port-hops on a USB hub; the Anker records show the residue (`2-`/`3-`/`4-`/
  `5-`/`7-` prefixed endpoints, several with orphaned volume stores).

**Required:** a dedicated, physically **labelled port per receiver**, then rename each in Windows
Sound (`ROOM RX #1/2/3`). Renaming alone is *not* sufficient — it only holds while each unit stays
on its own port. Get this wrong and it fails silently, with strips bound to the wrong areas of the
room.

### 2. Gain has to live in the transmitter

- The app's channel fader is **attenuation only** (`percent / 100f`, clamped 0..100) — a quiet mic
  can never be raised in-app.
- The RX's own output is already at **0 dB, its maximum**.
- The Windows endpoint ranges **−96 .. +30 dB** and defaults to 0, which leaves the pair ~30 dB
  under an AGC'd room mic and structurally unselectable. Its taper is brutal: 53.7% = 0 dB, 87% =
  +15 dB, 100% = +30 dB. **100% clips** (measured peaks **+5.25 / +2.81 dBFS**, ~300 samples over
  −0.5 dBFS); +15 dB measured clean (peaks −17 / −12 dBFS, zero samples over full scale).
- Endpoint gain **does not survive a port change**, so it is the wrong home for this setting here.

**Therefore:** set per-TX **manual gain** as the primary calibration and use the Windows endpoint
only as a small trim. `tools/VolProbe "Wireless PRO" <dB>` sets it by name when it does reset.

### 3. GainAssist OFF on every transmitter

It is AGC. It normalises level — the one cue that survives on this rig and the *only* one on a
homogeneous rig — and re-creates the "far mic wins" failure. Long-press the Left Navigation button
until AUTO/DYNAMIC is replaced by a dB value. Neither Auto nor Dynamic is safe for automixing.

### 4. RX mode confusion

**Split** means TX1→L / TX2→R *only while the RX's 3.5 mm jack is an OUTPUT.* Plug a mic in as an RX
Mic and both transmitters merge onto the **left**, with the RX Mic on the right. Do not confuse
Split with **Safety**, which puts a −10 dB duplicate of the same mix on channel 2 — it looks like a
split on a meter and carries no second mic. Re-verify with `tools/RxProbe` after any mode change or
remap.

### 5. Automixer settings for this rig

| Setting | Value | Why |
| --- | --- | --- |
| Mode | **Gate** | several mics hear one voice; Share sums and combs |
| Stable hand-off | **on** | a talker's pauses still let a neighbour momentarily win |
| Prefer natural | **off** | flux-CV measures over-processing; with no DSP every mic reads ~0.29–0.33, so it has nothing to separate. Observed 2026-08-23 hard-gating the *only* room mic hearing the talker |
| Match lapel | **off** | engages only while a priority lapel is *speaking*, which is never true when the room is |

### 6. Hand-off chatter — retune before the units arrive

`HandoffHysteresis` is 1.413 (~3 dB) with `HandoffHoldTicks` 20 (~200 ms). Measured 2026-08-30 with
just **two** near-equal mics: a median gap of **3.1 dB** — right at the threshold — produced **72
leader switches in 179 s**, one every 2.5 s, median dwell 0.55 s. In Gate that is a hard mute/unmute
of the source each time.

**Six mics hearing one questioner will sit inside that band far more often, so this gets worse, not
better.** Retune against the 2026-08-30 fixture *before* deployment — never from a live impression.

### 7. Placement — keep room mics away from the presenter

Measured 2026-08-30 with an S500 on the presenter's own table: during his speech pauses it read
**−30.6 dBFS median, −19.0 max**, while a real interjection 15 ft away lands near **−43.2 dBFS**.
Its idle residual exceeded a genuine question's level in **71%** of pause samples — so **no level
threshold can separate them**, and the break-in below cannot rescue that geometry.

A room mic beside a lapel-wearing presenter has **no useful operating regime**: while he talks it
must be ducked (it is the comb-filter source), and in his pauses it is the loudest thing in the
room. Keep every room TX ~10–15 ft away, or on the audience side of him. The front-most unit belongs
at the first row facing outward, not on the lectern.

### 8. Priority duck and the Q&A scene

The priority hangover shipped 2026-08-30 (`PriorityHoldTicks` ~1.2 s, `PriorityBreakInRms` ~−50
dBFS) after the duck was found releasing in the presenter's sentence gaps — 13 hand-offs in 40 s.
Three things remain open:

- **The break-in is unvalidated against a real interjection.** It has never been exercised live. The
  2026-08-30 fixture contains real Q&A; test against it.
- **Margin is thin.** Residual on a 15 ft mic peaked at **−53.8 dBFS** against the −50 dB threshold
  — about 4 dB. If it proves twitchy in a bigger room, raise toward −46.
- **A Q&A scene must clear the lapel's priority flag.** At strength 100% the duck is a hard mute, so
  leaving priority set silences the audience mics whenever the presenter's lapel reads active.

### 9. The noise floor is acoustic — proximity is the only lever

Finding 5b: **83% of the floor's energy is below 1 kHz and only 5.3% above 4 kHz** — that is the
room (HVAC, structure-borne rumble), not converter hiss, and mains hum was ruled out (≤2 dB at
50/60/100/120 Hz). Run the low-cut at 80–100 Hz for rumble and headroom, **never as an S/N fix** —
measured per cutoff it moves 100 Hz–8 kHz S/N by only +0.1–0.2 dB. The S500s were hiding this floor
with the very suppression we removed. Every halving of mic-to-mouth distance is +6 dB of signal;
that is the lever.

### 10. RF — unverified

Three RX and six TX in 2.4 GHz is beyond what has been confirmed. **Check the co-existence limit
with RØDE before buying the second and third kits.** In our favour: retiring the four Anker dongles
frees spectrum, and the Wireless PRO's range removes the Anker range problem entirely (their
furthest unit sat ~50 ft out, past its dongle's reliable range, with flux-CV tracking position —
0.58–0.68 with 7–12% transient spikes far, 0.37 with 0% up close).

---

## Open questions

- How many Wireless PRO systems coexist cleanly in one room (blocks the purchase).
- **Where the six TX actually go.** Today's data cannot answer this: every Rode reading was taken
  ~30 dB gain-suppressed, so the row-2-vs-row-3 pickup question is genuinely unmeasured. Compare the
  two Rodes against *each other* (shared gain) in the fixture, never against an Anker.
- Battery and charging logistics for 6 TX + 3 RX across a session.
- Retuned hysteresis/hold values for N=6.

## Commissioning checklist

1. One labelled USB port per receiver; rename each `ROOM RX #1/2/3` in Windows Sound.
2. GainAssist **off** on all six TX; set manual gain and match levels *at the transmitters*.
3. Confirm each RX is in **Split**, with nothing plugged into its 3.5 mm jack.
4. `tools/RxProbe` on each RX — confirm a genuine split, not Safety, not an RX-Mic merge.
5. Bind two strips per RX (`Left` / `Right`); confirm 7 inputs resolve and the preset re-saves.
6. Low-cut 80–100 Hz, matched across all six.
7. Gate + stable hand-off on; prefer-natural and match-lapel off.
8. Place no TX within ~10–15 ft of the presenter.
9. Build the Q&A scene that clears the lapel priority flag.
10. Record all inputs for the first full session — that is the retune fixture.
