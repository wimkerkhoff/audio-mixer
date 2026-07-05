# AudioMixer Roadmap

Planned work and ideas. `CLAUDE.md` documents how the code works *today*; this file is what's *next*.
Keep forward-looking TODOs here (not in `CLAUDE.md`). Rough priority order within each section.

Status key: 🔲 planned · 🔬 needs live data / validation · 🛠 doable now (no room needed) · 💡 idea

---

## Operator experience

### 🔲 "Easy UI" mode
A simplified, operator-proof view for non-technical volunteers. Hide the advanced/per-channel
automix controls and surface only the essentials: device pick, levels, a scene/Standby control, and
a master mute. A toggle switches between **Easy** and **Full/Advanced**. *Why:* operators aren't
audio-savvy; the common path has to "just work" with minimal controls to get wrong.

### 🔲 Scene control — Standby / Teaching / Singing
One operator control that switches the whole behavior:
- **Standby** — outputs muted, so pre-service chatter never reaches Zoom/recording.
- **Teaching** — the current follow-the-talker automix (priority-lapel ducks the room; correct for
  one coherent talker).
- **Singing** — **open the good room mics at flat/equal gain and suspend priority-ducking.** Do NOT
  collapse to one mic and do NOT follow-the-talker.
  1. Suspend priority-ducking — stop the lapel gating the room out (the 2026-07-05 bug: worship
     reached the stream as pastor-only because the priority lapel ducked every room mic to 0).
  2. Automix Off / flat — room mics pass at unity, not winner-take-all.
  3. Keep the lapel in if the pastor leads on it, at normal (non-dominating) level — operator taste.
  4. Optionally exclude a known-bad/dead mic.

*Why single-mic was rejected for Singing:* measured on the 2026-07-05 singing capture, the two live
room Ankers had waveform coherence of only **0.13** and summing them added **zero comb ripple** (6.5
vs 6.7 dB). Comb-filtering is a *coherence* phenomenon — high for one talker (teaching → duck to
one) but low for a congregation, a distributed source where each mic hears different nearby singers.
So the anti-comb reason to duck-to-one **does not transfer to singing**; several mics give better
coverage with no comb penalty. The real tradeoff for singing is coverage (favors more mics) vs
accumulated room tone / reverb / Anker gate-chatter (favors fewer) — hence "the *good* room mics,"
not literally all. See CLAUDE.md gotchas and the singing-scene memory. Caveat: one room, ~3 min, one
of three room mics dead — re-check 4+-mic summing on a fuller capture.

*Why manual, not auto-detected:* the automix assumes one talker at a time; singing and pre-session
chatter break that, and the *desired action differs per scene*. Auto-detection is unproven — on the
2026-07-05 data no reference-free signal separated singing from teaching (room-mic flux-cv rose only
~0.1, lapel duty-cycle and lapel↔room correlation shifted but every margin overlapped teaching's own
variation; raw multi-mic activity was useless — 3–4 mics hot for the entire 15 min). A manual
control is reliable. Pairs naturally with Easy UI.

### 🔲 Test-tone / output preflight
A "send test tone to A / B" button that plays a short tone (or looped noise) out each output bus, so
the operator can confirm the *downstream* capture is receiving before the service — watch OBS's
CABLE Output meter move, or hear it on the headset. *Why:* 2026-07-05 the opening ~10–15 min never
reached OBS even though output A was live the whole time (mixer log + mix-A recording both hot from
minute one) — the fault was entirely on the OBS/VB-CABLE capture side. The mixer can't force OBS to
capture, but a one-click tone makes the end-to-end check trivial and talker-free. Rule of thumb to
document in the UI: if the mixer's A meter moves but OBS is flat, the fault is downstream, not the
mixer.

---

## Device management

### 🔲 Hide VoiceMeeter / virtual devices from input pickers
Option to filter virtual capture devices (VoiceMeeter, VB-CABLE, etc.) out of the **input** device
lists so operators only see real microphones. Keep VB-CABLE selectable for **outputs** (that's the
Zoom path). *Why:* virtual devices clutter the picker and are never the right input.

### 🔲 In-app audio device diagnostics
Fold `tools/audio-device-diag.ps1` into the app as a diagnostics panel: list audio endpoints as
active vs ghost, capture vs render (classified by the MMDEVAPI dataflow id, not the friendly name),
show which devices are on Bluetooth, and flag the "output up but no mic" Soundsync half-link. *Why:*
when an Anker goes dead-but-"connected" the operator needs a one-glance answer for *what's actually
live*; prefix-shuffle and half-links make Windows' own Sound panel misleading.

### 🔲 Prefer Soundsync dongles; warn on Bluetooth
Policy: the Ankers must run over their 2.4 GHz USB Soundsync dongles, never Bluetooth (BT drops to
HSP/HFP quality and steals the device from the dongle link). The app should surface/warn when an
Anker is connected via Bluetooth, and ideally avoid selecting BT ("PowerConf S500 Hands-Free")
capture endpoints as inputs — pairs with hiding virtual/BT devices from the input pickers.

### 🔲 Auto-(re)add Anker devices to inputs
On launch and on device-change, detect Anker capture endpoints and auto-assign them to input
channels; re-add them when they drop and reappear (USB/BT renegotiation). Complements the existing
capture-stall watchdog, which only restarts an *already-assigned* device — this handles
assignment/discovery. *Why:* the Ankers churn; manual re-adding is error-prone for operators.

---

## Automix validation & tuning

### 🔬 Verify recent fixes at the next live session
All deployed, none confirmed live yet — now verifiable from the logged transcript (gains/cv/winner):
- Bounce fix (multiplicative natural hysteresis) — near-equal mics should stop flipping/chopping.
- Share quality-weighting — the loud-but-bad mic should duck automatically (no manual route-disable).
- Prefer natural overall — sensible override rate, picks good mics.
- Match lapel (reference-guided) — only testable when a lapel is actually in use.

### 🔬 Singing capture & analysis
First singing captured 2026-07-05 (~3 min, one room, one of three room mics dead). Findings: (1) no
reference-free signal cleanly separated singing from teaching → manual scene control, not
auto-detect; (2) room mics don't comb when summed during singing (coherence 0.13) → Singing scene
uses several mics, not one. Still needed: a *fuller* capture — longer, all room mics live, ideally a
loud full-congregation song — to confirm 4+-mic summing stays comb-free and to re-test any
singing-vs-speech discriminator. Analysis lives in scratchpad (`singing_vs_speech.py`,
`comb_test.py`, `find_singing.py`); fold the keepers into `tools/`.

### 🛠 Offline cv-scale fidelity
Align the Python `flux_cv` to the engine's `CurrentFluxCv` scale (offline ~0.4 vs engine ~1–3.4) so
offline replays can validate cv *thresholds* between services, not just relative behavior. Right now
threshold tuning is stuck waiting on live sessions.

---

## Tooling

### 🛠 Log rotation / size cap
The log is append-only; a long-running session grew to ~285 MB over ~6 days. Rotate per launch or
cap the file size so it can't balloon.

### 🛠 Keep the validation harness current
`tools/`: `naturalness.py`, `voice_quality.py`, `replay_natural.py`, `review_natural.py`,
`replay_share.py`, `scene4.py`/`scene5.py`, plus the singing set (`live_wav.py`, `find_singing.py`,
`singing_vs_speech.py`, `comb_test.py`). Re-run after each captured session; fold in the cv-scale fix
above so they're faithful to the engine. Note `live_wav.py` reads in-progress diag WAVs that
`soundfile` can't — reuse it for any tool that runs mid-recording.

---

## UI nice-to-haves

### 💡 Unify the "Mic clarity" readout to naturalness
The per-input gear popup's clarity bar still shows the old **crest** metric, but selection now uses
**flux-cv** naturalness. Switch the readout to flux-cv so what the operator sees matches what drives
the choice.

### 💡 Operator overrides
Per output: pin a mic always-on, or exclude a known-bad mic from the competition — a manual escape
hatch when the automix picks wrong.

---

## Hardware (not software)

The Anker speakerphones cap the ceiling: their AGC + noise-suppression over-process speech and mangle
sustained music. For worship especially, a sound-board feed or a proper overhead/room mic would
improve quality more than any selection algorithm can.

---

## Recently shipped (context)

Single-instance guard · build-stamped log banner · live JSON state endpoint (`--state`) · file
logging flag (`--log`) · per-bus A/B LEDs + dynamic route tooltips · wider outputs · reference-guided
"Match lapel" · reference-free "Prefer natural" · quality-weighted Share (loud-bad mic ducks) ·
multiplicative natural hysteresis (bounce fix) · gains/cv/winner logging · desktop shortcut →
latest build with diagnostics on.
