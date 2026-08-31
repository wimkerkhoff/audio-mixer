# RØDE Wireless PRO room rig (up to 6 TX) — plan, changes and watch-list

Status: **planned**, nothing bought beyond the first kit. Written 2026-08-30. Scope owner: this
file — how the rig is used, what changes in the code and UI, and what the operator does.
`CLAUDE.md` documents what IS; `ROADMAP.md` holds the wider backlog. Fold each item's *result* into
`CLAUDE.md` as it is established, and delete this file once the rig is commissioned and stable.

---

## Target configuration

| Role | Hardware | Mixer binding |
| --- | --- | --- |
| Presenter | RØDE Classic (single TX) → RX 3.5 mm → Realtek jack | 1 strip, **priority**, Role=Lapel |
| Room ×6 | 3× Wireless PRO kits = 3 RX (USB-C) × 2 TX, **built-in mics**, no lavs plugged | 6 strips, 2 per RX |
| Outputs | monitor headset + VB-CABLE → **Zoom reads CABLE Output directly** | 2 buses |

7 mixer inputs total, inside `AudioEngine.MaxInputCount` (10). The TX units are spread across the
tables for audience coverage, not worn — only the presenter's lapel is body-worn.

**Zoom takes CABLE Output directly; OBS is not in the path.** Every processing decision below follows
from that: an OBS filter would cover nothing, so anything we want on the stream has to exist in this
app.

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

# How the rig is used

## Scene matrix

Decided 2026-08-30. The presenter wears the lapel with **priority ON** for teaching, Q&A *and*
theology study; only prayer runs with no lapel at all.

| Scene | Presenter | Room TX | Automix | Priority |
| --- | --- | --- | --- | --- |
| **Standby** | — | — | untouched | outputs muted |
| **Teaching** | Classic lapel, worn | all routed | Gate + stable hand-off | **ON** — ducks the room, 1.2 s hangover |
| **Q&A** | same as Teaching | all routed | Gate + stable hand-off | **ON** |
| **Theology study** | same as Teaching | all routed | Gate + stable hand-off | **ON** |
| **Prayer** | none — scene mutes *and* unroutes the lapel | all routed | Gate + stable hand-off | none |
| **Singing** | operator taste | all routed, flat | **Off** | suspended |

**No new scenes are needed.** Q&A and theology study are behaviourally identical to Teaching, and
`Scene.Teaching` already produces exactly that plan; `Scene.Prayer` already mutes, unroutes *and*
de-prioritises the lapel, which is the documented duck hazard closed off in the pure layer. The only
thing that could justify splitting Q&A out later is the priority hangover — see code change 5.

## The one invariant: the Classic is priority, or it is out

The presenter's Classic reaches the mixer over a completely different gain path (RX 3.5 mm →
Realtek) from six USB-C Wireless PROs. Finding 7 says a level comparison **across** device types is
meaningless — an AGC'd or differently-trimmed source cannot be ranked against an uncompressed one.

That never bites here *as long as* the Classic is either a **priority** channel (priority mics are
held at full level and taken out of the leader competition entirely) or **unrouted**. Both of the
two states the scenes produce satisfy it. What must never happen is the Classic sitting routed with
priority cleared: it would then compete on level against six mics on another scale and either
dominate the bus or never win, arbitrarily.

Fallback if the Classic proves troublesome in practice: move the presenter onto a Wireless PRO TX
with a wired lav. That makes the whole rig homogeneous and dissolves this invariant, at the cost of
one room mic or a fourth kit.

## Placement

The exclusion zone is around the **teaching position**, not "the presenter" — he stands at the
lectern with a lapel in three scenes and sits at a table like everybody else in prayer, so a rule
worded around the person is unsatisfiable.

- **No room TX within ~10–15 ft of the lectern**, or put the front-most unit at the first row facing
  outward. Measured 2026-08-30: an S500 on the presenter's own table read **−30.6 dBFS median /
  −19.0 max during his speech pauses** while a genuine interjection 15 ft away lands near **−43.2**
  — its idle residual beat a real question in **71%** of pause samples, so no level threshold can
  separate them and the priority break-in cannot rescue that geometry.
- **Spread for coverage before people arrive.** Target ≤3 ft from the nearest talker at each table.
  Proximity is the whole S/N budget — the room floor is constant, and every halving of mic-to-mouth
  distance is +6 dB (finding 5b).
- **Off the table surface** on a low stand. Flat on laminate combs against the table reflection and
  picks up every knock, pen and cup.
- **3:1 spacing** — mic-to-mic ≥ 3× mic-to-mouth — to limit comb when two mics hear one voice.
- **Tape-mark the table positions and always return TX #N to the same table.** Seating changes every
  week; the mic *positions* must not. That is what keeps a preset, the diagnostics table and a "mic
  4 sounds bad" report meaningful across sessions.
- **Mute the strip for a table nobody sat at.** Gate mostly hides an idle mic, but it is still room
  floor that can win the bus during a lull.

---

# Levelling

## The calibration target is a number, not taste

**Aim for normal speech reading ≈ −24 dBFS RMS (≈ −12 dBFS peak) at the mixer input.**

That number is not a preference. Five decision thresholds in the automixer are absolute linear RMS,
and every one was fitted while all room mics were AGC'd Ankers whose speech sat at a **−24.6 dBFS
p50** (finding 7):

| Constant | Value | ≈ dBFS | What breaks if the scale moves |
| --- | --- | --- | --- |
| `AutoMixer.SilenceFloorRms` | `0.0018f` | −55 | quiet room mis-classified as speech, or real speech as silence |
| `AutoMixer.PriorityActiveRms` | `0.01f` | −40 | the presenter stops ducking the room |
| `AutoMixer.PriorityBreakInRms` | `0.0032f` | −50 | interjections can't break the duck, or every rustle does |
| `AutoMixer.RefSpeechRms` | `0.01f` | −40 | "Match lapel" never engages (off on this rig, but still) |
| `InputChannel.FluxVoiceRms` | `0.006f` | −44 | flux-CV stops accumulating; the RF-drop signal goes dead |

`BROADCAST-MODE.md` planned to absorb the lost AGC make-up with **channel gain** — "one preset change
beats six retuned constants." **That option does not exist on this rig.** The fader is
`percent / 100f` clamped at unity: attenuation only. So the scale has to be restored *at the
transmitter*, which makes TX gain a code-constant dependency rather than an operator preference. Land
speech near −24 dBFS and all five constants keep the regime they were fitted to. Land 12 dB low —
which is where a Wireless PRO sits at Windows-default gain — and the priority duck silently stops
engaging.

**One constant must be re-derived anyway:** `SilenceFloorRms`. The Ankers gated their noise floor to
true zero, so −55 dBFS was safely below anything real. The Rodes don't gate and the floor is a
genuine acoustic room (finding 5b), so −55 dBFS may now sit inside occupied signal. Derive it as
measured quiet-room RMS × 2 from the first capture, not from the old value.

## What is allowed to act where

| Stage | Setting | Rule |
| --- | --- | --- |
| **TX gain** (hardware) | manual dB, **identical on all six** | the calibration. GainAssist **off**. Travels with the device, spends no headroom |
| RX endpoint (Windows) | one documented value per labelled port | trim only. Re-apply with `tools/VolProbe` after a port change. Never the calibration |
| Channel fader | unity | attenuation only, and only to correct a *measured device* offset — never a seating offset (it cannot boost anyway) |
| Low-cut | 80–100 Hz, matched across all six | rumble, handling and headroom. Measured **+0.1–0.2 dB** of speech-band S/N — never an S/N fix |
| Automixer (Gate) | on, stable hand-off | the per-talker leveller in disguise: it guarantees the bus carries the *closest* mic, killing N× floor and comb. It does **not** normalise a loud talker against a quiet one |
| **Bus leveler** | new — see below | the only dynamic stage, and only *post*-automix |
| Limiter | −1 dBFS brick wall on the CABLE bus | protective only |

## Nothing dynamic upstream of the bus — ever

`InputChannel` latches `_currentLevelLinear` (:558) and runs `PostPeak` / `ComputeFlux` (:672–677)
*after* gain and delay, and that value is the automixer's entire input. A per-channel compressor
anywhere in that chain is GainAssist reimplemented in software: it flattens the distance cue, which
is the one selector this rig has (findings 6a and 7, and the GainAssist gotcha). It would also make
every replay baseline measure our own processing rather than the room.

## The bus leveler

Because Zoom reads CABLE Output directly, this is the only place stream levelling can live.

- **Placement in code:** `OutputBus.Start`, between `mixer` and `tap`. Meters and per-output
  recordings then reflect what actually went out, and `Volume` stays a pure post-tap device trim.
  The per-input analysis recorder is upstream and untouched, so the offline tools and golden
  baselines stay faithful.
- **Slow leveler, not a peak compressor.** The variance here is *between utterances* — a different
  person at a different distance every time — not within a syllable. A fast compressor squashes
  syllables and pumps without touching talker-to-talker offset. Start at ratio 3:1, threshold
  ≈ −26 dBFS, attack ≈ 100 ms, release ≈ 2 s.
- **Cap total upward gain at ~10–12 dB.** Speech-band S/N on this rig is ~15 dB and the floor is
  acoustic; every dB of make-up is a dB of HVAC in the gaps. That cap is the noise budget.
- **Idle gain hold** — freeze the gain when the bus is below ≈ −45 dBFS RMS instead of ramping up
  into the silence. It only ever *declines to add* gain; it never attenuates, so it cannot silence a
  quiet pray-er. That is the line finding 4 forbids crossing, and it is also why this can't be an
  off-the-shelf compressor even if OBS were in the path.
- **Default OFF on both buses.** Enable on CABLE; leave the monitor honest — the operator needs to
  hear what the mics actually did.
- Expose a **gain-reduction readout** so it is visibly working rather than suspected.

## What levelling cannot fix

Distance-driven S/N. Bringing a 10 ft talker up 12 dB brings their share of the room floor up 12 dB
with it. Proximity is the lever; the leveler only tidies what is left after placement and the Gate.

## The tension to not "fix" the wrong way

Matched TX gains are what make a level difference mean *distance* — and they simultaneously maximise
how often two adjacent mics sit inside `HandoffHysteresis`. Watch-list item 6 measured **72 leader
switches in 179 s** with just two mics a median **3.1 dB** apart. If hand-off chatters with six mics,
retune the hold and hysteresis. **Never de-match a transmitter to calm it down** — that trades a
fixable timing problem for an unfixable selection problem.

---

# Changes to make in the code

1. **Bus leveler + limiter** — new `Audio/BusLeveler.cs` (an `ISampleProvider`), wired into
   `OutputBus.Start` between `mixer` and `tap`, per output, default off, with the idle hold above.
   Persist enable/threshold/ratio/max-gain: add them to `Models/MixerPreset`, `PresetMapper` **and
   `Services/PersistedProperties`** — a persisted setting missing from the autosave allowlist
   silently never saves, and the failure only shows up after a crash.
2. **Ambiguous-device guard.** All three receivers report `Desktop Microphone (Wireless PRO RX)`, and
   `DeviceResolver.DeviceNameKey` strips the `(N- …)` enumerator, collapsing them to one key. The
   preset's name fallback then cannot tell them apart and the `used` set only stops double-grabbing,
   so strips fill in **arbitrary order** — mics bound to the wrong side of the room, silently. Change:
   when two or more live endpoints share a `DeviceNameKey`, **refuse** the name fallback for that key
   and raise a health alert instead of guessing. Pure logic, so it belongs in `DeviceResolver` +
   `HealthMonitor` with unit tests.
3. **Re-derive `SilenceFloorRms`** from the first capture — quiet-room RMS × 2. The old value assumed
   a gated-to-zero floor that no longer exists.
4. **Retune `HandoffHoldTicks` / `HandoffHysteresis` for N=6** offline against the fixture, before
   deployment, never from a live impression. Six mics hearing one questioner will sit inside the
   3 dB band far more often than the two-mic case that already produced 72 switches in 179 s.
5. **Priority hangover, possibly per scene.** `PriorityHoldTicks` ≈ 1.2 s is right for a monologue and
   may be too long for a Q&A interjection. Only split `Scene.Teaching` into Teaching/Q&A if the
   fixture actually shows it — otherwise leave the scene surface as it is.
6. **Calibration readout** — per-channel voiced-speech RMS p50 against the −24 dBFS target, in
   `DiagnosticsWindow`. Turns TX gain setting into number-matching instead of guesswork, and it is
   what makes changes 3 and 4 verifiable at the next session rather than the one after.
7. **No new scenes.** Verify with the existing `SceneTransform` unit tests that Teaching yields
   lapel-priority-on + all room mics routed, and Prayer yields lapel muted **and** unrouted **and**
   de-prioritised. Do not add surface area that the answers above don't require.
8. *(Later, optional)* **RF-health veto** — with no gating, exact digital silence can only mean an RF
   drop, so a mic with recent drop edges could be excluded from winning the election. This is
   flux-CV's one surviving job; it is a diagnostic signal, not a selection metric.

# Changes to make in the UI

1. **Per-output Leveler control** in the output column: on/off, strength, and a gain-reduction bar.
   Default off, and visibly off — an invisible dynamics stage is how you end up debugging the room.
2. **Zone labels on the strips.** `ChannelViewModel.CustomLabel` already exists and is already shown
   in `SettingsWindow`; set it to the table position (`Table 1 L`, `Table 1 R`, …) and surface it on
   SimpleWindow's mic dots and in the Diagnostics table, so "mic 4" names a *place in the room*
   rather than a strip index.
3. **Health-banner alert for duplicate device names** (code change 2) — "3 devices named Wireless PRO
   RX; bind manually." This is the single highest-consequence silent failure on the rig.
4. **Calibration column** in `DiagnosticsWindow` (code change 6).
5. Nothing needed in `MainWindow.xaml` layout: 7 inputs computes to `max(500, 7×96 + 160)` = 832 px,
   well within the fixed-width scheme.

---

# Operator procedure

## One-time commissioning

1. **One labelled USB port per receiver** — physically labelled, and each RX always goes back to its
   own port. Then rename each in Windows Sound (`ROOM RX #1/2/3`). Renaming alone is not enough: the
   rename, the endpoint GUID and the endpoint gain are all keyed to the port-derived instance path.
2. **GainAssist OFF on all six TX**; set manual gain, matched, to hit the −24 dBFS speech target.
3. Confirm each RX is in **Split**, with **nothing plugged into its 3.5 mm jack**.
4. `tools/RxProbe` on each RX — confirm a genuine split, not Safety, not an RX-Mic merge.
5. Bind two strips per RX (`Left` / `Right`); confirm all 7 inputs resolve and the preset re-saves.
6. Label every strip with its table position; tape-mark the table positions to match.
7. Low-cut 80–100 Hz, matched across all six.
8. Gate + stable hand-off on; prefer-natural and match-lapel **off**.
9. Place no room TX within ~10–15 ft of the lectern.
10. Leveler **off** for the first session — capture the room untouched.
11. **Record all inputs for the first full session.** That capture is the retune fixture for
    `SilenceFloorRms`, the hand-off constants and the leveler settings. Nothing gets tuned before it.

## Every session

**Before people arrive**

- Spread the six TX across the tables for coverage, each on its stand, back on its tape mark.
- Power on, confirm all 7 strips show level; check the health banner is clear.
- Verify the presenter's lapel is on and reads speech, then set the scene:
  **Teaching / Q&A / study → Teaching. Prayer → Prayer.**

**During**

- Mute the strip for any table nobody sat at.
- Watch the health banner, not the meters.
- If the presenter takes the lapel off mid-session, switch to Prayer or mute his strip — an open
  lapel lying on a desk that drifts over −40 dBFS ducks every room mic off the stream with no
  visible cause.

**After**

- Stop recording; note anything that sounded wrong and roughly when. Operator labels are what make a
  capture usable for tuning — an unlabelled recording cannot settle a selector question.

## Symptom → first check

| Symptom | Look at |
| --- | --- |
| A mic is bound to the wrong part of the room | duplicate `Wireless PRO RX` names — did an RX move ports? |
| Audience inaudible on the stream but fine on the headset | priority duck: is the lapel open and above −40 dBFS? |
| Selection flickers between two mics | hand-off hysteresis/hold, **not** the transmitters |
| One mic is consistently quiet | TX gain, then endpoint gain (`tools/VolProbe`) — never the fader |
| Stream floor rises in the pauses | leveler make-up too high, or the idle hold isn't engaging |
| A mic drops out mid-speech | RF, not the capsule — check range and `rf=` in the log |

---

# Watch-list

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
room. Code change 2 turns that silent failure into a health alert.

### 2. Gain has to live in the transmitter

- The app's channel fader is **attenuation only** (`percent / 100f`, clamped 0..100) — a quiet mic
  can never be raised in-app.
- The RX's own output is already at **0 dB, its maximum**.
- The Windows endpoint ranges **−96 .. +30 dB** and defaults to 0, which leaves the pair ~30 dB
  under an AGC'd room mic and structurally unselectable. Its taper is brutal: 53.7% = 0 dB, 87% =
  +15 dB, 100% = +30 dB. **100% clips** (measured peaks **+5.25 / +2.81 dBFS**, ~300 samples over
  −0.5 dBFS); +15 dB measured clean (peaks −17 / −12 dBFS, zero samples over full scale).
- Endpoint gain **does not survive a port change**.

**Therefore:** per-TX **manual gain** is the calibration; the Windows endpoint is a documented fixed
trim per labelled port, re-applied with `tools/VolProbe "Wireless PRO" <dB>` when it resets. Those
two roles are not interchangeable — see the levelling stage table.

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
better.** Retune against the 2026-08-30 fixture *before* deployment — never from a live impression,
and never by de-matching a transmitter (see "the tension to not fix the wrong way").

### 7. Placement — keep room mics away from the lectern

Measured 2026-08-30 with an S500 on the presenter's own table: during his speech pauses it read
**−30.6 dBFS median, −19.0 max**, while a real interjection 15 ft away lands near **−43.2 dBFS**.
Its idle residual exceeded a genuine question's level in **71%** of pause samples — so **no level
threshold can separate them**, and the break-in below cannot rescue that geometry.

A room mic beside a lapel-wearing presenter has **no useful operating regime**: while he talks it
must be ducked (it is the comb-filter source), and in his pauses it is the loudest thing in the
room. Keep every room TX ~10–15 ft from the **lectern**, or on the audience side of it. The rule is
about the *position*, not the person — in Prayer he sits at a table with everyone else and must be
covered like everyone else.

### 8. Priority duck and the scenes

The priority hangover shipped 2026-08-30 (`PriorityHoldTicks` ~1.2 s, `PriorityBreakInRms` ~−50
dBFS) after the duck was found releasing in the presenter's sentence gaps — 13 hand-offs in 40 s.
Three things remain open:

- **The break-in is unvalidated against a real interjection.** It has never been exercised live. The
  2026-08-30 fixture contains real Q&A; test against it. Q&A is the scene that needs it most, and
  Q&A now runs with priority ON.
- **Margin is thin.** Residual on a 15 ft mic peaked at **−53.8 dBFS** against the −50 dB threshold
  — about 4 dB. If it proves twitchy in a bigger room, raise toward −46.
- **Prayer is the only scene without a lapel**, and `Scene.Prayer` already mutes, unroutes *and*
  clears priority. Keep all three: at strength 100% the duck is a hard mute, so a merely-unrouted
  lapel that still reads active would silence the room.

### 9. The noise floor is acoustic — proximity is the only lever

Finding 5b: **83% of the floor's energy is below 1 kHz and only 5.3% above 4 kHz** — that is the
room (HVAC, structure-borne rumble), not converter hiss, and mains hum was ruled out (≤2 dB at
50/60/100/120 Hz). Run the low-cut at 80–100 Hz for rumble and headroom, **never as an S/N fix** —
measured per cutoff it moves 100 Hz–8 kHz S/N by only +0.1–0.2 dB. The S500s were hiding this floor
with the very suppression we removed. Every halving of mic-to-mouth distance is +6 dB of signal;
that is the lever, and it is why the bus leveler's make-up gain is capped.

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
- Retuned hysteresis/hold values for N=6, and the re-derived `SilenceFloorRms`.
- Leveler constants — threshold, ratio, make-up cap and idle-hold point — all wait on the first
  labelled capture.
- Whether the presenter stays on the Classic. Plan is yes; if the separate gain path or the Realtek
  input proves troublesome, move him to a Wireless PRO TX with a wired lav and the homogeneity
  argument closes completely.
