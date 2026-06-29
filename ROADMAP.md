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
- **Teaching** — the current follow-the-talker automix.
- **Singing** — collapse to a single chosen mic (avoid multi-mic comb-filter wash).

*Why:* the automix assumes one talker at a time; singing and pre-session chatter break that, and the
*desired action differs per scene*. A manual control is reliable; auto-detection is unproven (level
spread fails through the Anker AGC — see CLAUDE.md gotchas — and needs singing data to test anything
else). Pairs naturally with Easy UI.

---

## Device management

### 🔲 Hide VoiceMeeter / virtual devices from input pickers
Option to filter virtual capture devices (VoiceMeeter, VB-CABLE, etc.) out of the **input** device
lists so operators only see real microphones. Keep VB-CABLE selectable for **outputs** (that's the
Zoom path). *Why:* virtual devices clutter the picker and are never the right input.

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
Record real congregational singing (record-all-inputs during a song) — we still have zero singing
data. Test whether *any* reference-free signal separates singing from speech before building
auto-detection. If nothing separates them, the manual scene control is the answer.

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
`replay_share.py`, `scene4.py`/`scene5.py`. Re-run after each captured session; fold in the cv-scale
fix above so they're faithful to the engine.

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
