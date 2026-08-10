# Anker S500 Broadcast pickup mode — implementation plan

Status: **planned**, nothing flipped yet. Written 2026-08-09.
Scope owner: this file. `CLAUDE.md` documents what IS; `ROADMAP.md` holds the wider backlog. When this
plan completes, fold its *results* into `CLAUDE.md` (finding 4 / finding 5) and delete this file.

---

## Context

The 4× Anker PowerConf S500 room mics run in **Standard** pickup mode, whose AGC + noise suppression +
gating sit between the room and every sample the mixer sees. Measured 2026-08-09 (`CLAUDE.md`
finding 4): during congregational singing each unit sat in true digital silence 13–21% of frames, and
**all four were silent simultaneously 4.6% of frames — 51× more than independence predicts**. That is
71 total-stream dropouts in 170 s, median 60 ms, max 780 ms. Operator verdict, unprompted:
*"interrupted constantly, can't follow it at all."* Switching Automix to Off changed nothing, because
the holes exist in every source at the same instant.

Finding 4's conclusion is that **no mixer-side fix exists** — the remaining levers are upstream.
**Broadcast** is the only DSP-adjacent control Anker exposes ("restores original sounds by turning the
speaker off"). With the speaker dead there is no acoustic echo to cancel, so much of the DSP chain has
nothing to do. Losing far-end audio costs nothing here: the mixer only captures from the Ankers and
monitoring is on the headset.

**This is a pickup-mode change, not a transport change.** The units stay on their 2.4 GHz Soundsync
dongles. Connecting them to Windows over Bluetooth as capture devices would mean HFP/mSBC — 16 kHz
narrowband mono, strictly worse for singing than the gating being solved — and would re-introduce the
self-contention hazard already fixed by forgetting the pairings.

Intended outcome, in priority order:

1. Singing becomes usable — the correlated gate-to-silence disappears.
2. The mixer stays **fully automatic** for a non-technical operator: pick a scene, the app uses the
   best available mic (Rode lapel when worn, else the best of the 4 Ankers).
3. **Anyone in the room can jump in for Q&A** without their first syllable being swallowed.

Goal 3 is currently **structurally impossible**. That is the highest-value finding here, and it is
independent of Broadcast mode — see §2.1.

---

## 1. State of play (verified 2026-08-09)

**Committed.** The replay rig (`--replay/--speed/--loop/--seek/--for`), `--state`, `--scene=NAME`,
`--open-all`, and the golden-baseline harness (`tools/replay-baseline.ps1`, `tools/baselines/`) are on
`main` (`34188d4`, `86dc115`, `5c597e9`). The 2026-08-02 refactor blocker at `ROADMAP.md:217` is
**closed** (✅ verified against the replay rig); `ROADMAP.md:240-249` is stale prose left under that ✅.

**In flight, uncommitted.** ~1,900 lines of the Simple mode redesign already exist in the working
tree — `Models/Scene.cs`, `Services/SceneTransform.cs`, `Services/HealthMonitor.cs`,
`ViewModels/SceneController.cs`, `ViewModels/DiagnosticRow.cs`, `Views/{Simple,Diagnostics,Settings}Window`,
`Views/OperatorConverters.cs`, `AudioMixer.Tests/{SceneTransform,HealthMonitor}Tests.cs`, plus ~363
lines of edits across 10 tracked files (`ChannelViewModel.Role`, `OutputViewModel.Muted`,
`InputChannel.LastSoundTicks`, `MixerPreset.Role` migration, allowlist entry). `CLAUDE.md` has been
updated to describe it.

**The plan is to finish and land this work, not to redesign it.** `SceneTransform` is genuinely pure
and `OutputViewModel.Muted`'s non-persistence is correctly justified. Three gaps are listed below.

---

## 2. Findings that shape the plan

### 2.1 Q&A jump-in is impossible today — and it is not a Broadcast issue

`AutoMixer.cs:390` (`float others = Lerp(0.15f, 0f, s);`) and the identical `pduck` at `:322` reach
**exact zero** at strength 1.0. The live rig runs strength at 100%: across the whole session log the
per-output automix gain only ever takes the values `0.00` (26,874 samples) and `1.00` (13,441). And
`winner=0` — the lapel priority path — holds for **42% of logged seconds**, during which all four room
mics sit at literal digital zero.

So someone who starts talking while the presenter's lapel is above −40 dBFS is at −∞, not merely
attenuated, and stays there until the presenter stops plus a 250 ms release tail.

Share has had a floor since day one (`:371`, `Lerp(0.25f, 0.03f, s)`). **Gate and the priority duck are
the only two paths in the file that reach zero.** Adding a floor restores an existing invariant rather
than inventing one. This is a live bug under Standard mode; Broadcast only makes it more audible
(a 1.0 → 0 step on a mic with a real noise floor is a click, not a fade).

### 2.2 `SceneTransform` never sets Strength, so every scene inherits 100%

`SceneTransform.cs:73-80` and `:92-99` set `Mode`, `PreferNatural`, `ReferenceGuided`, `StableHandoff`
and `Muted` — but not strength. Teaching therefore inherits whatever the operator last left, which is
100%, which is hard mute. **Scenes cannot deliver goal 3 until both the floor exists and `OutputPlan`
carries a strength.**

### 2.3 Standby's documented promise doesn't match its mechanism

`Scene.cs:10` says Standby means *"pre-service chatter never reaches Zoom **or the recording**."*
`OutputViewModel.Muted` implements it as `_bus.Volume = 0f`, and the bus volume is applied **after**
the peak/recorder tap. Zoom does go silent; **the recording does not.** Either move the mute before the
tap or correct the comment — the current pair is a promise the code doesn't keep.

### 2.4 What Broadcast changes about the algorithms

| Area | Effect |
| --- | --- |
| Finding 4 (correlated gating) | Should disappear. This is the whole point. |
| Finding 1 (only level survives) | Scoped to the DSP. SNR in particular was a level proxy only because gating zeroed the noise floor. Worth re-testing with `tools/AnalyzeInputs`. |
| Finding 2 (Match lapel) | Should **improve** — AGC was distorting the room-mic envelopes that the correlation reads. |
| Finding 3 (flux-CV) | Premise **weakens**: flux-CV measured the DSP's over-processing artifacts, and Broadcast removes the artifact source. Degenerates toward a noisy reverb/distance proxy redundant with level. |
| `RfSilenceRms` dropout detection | Gets **more** meaningful — with no gating, exact digital silence can only mean an RF drop. |
| New problem | A real, continuous noise floor on every open mic: 4 mics sum to ≈ +6 dB of room tone, and comb filtering becomes continuous rather than gated out. |

---

## 3. Sequencing

Next service is **Sunday 2026-08-16** — seven days. The full Simple mode redesign does not ship in a
week and this plan does not pretend it does.

**Rule for what may ship before measurement:** a change is safe iff its correctness argument never
references the noise floor level, the speech level, the proximity spread, or the flux-CV scale.
Structural fixes ship now; anything whose *value* was fitted to DSP'd audio waits.

A useful side effect: the safe list is exactly the set of changes that lets the **un-retuned** selector
survive the mode flip. If `SilenceFloorRms` ends up below the Broadcast noise floor, the worst symptom
becomes hand-off churn on room tone — which the floor and the slew make inaudible and the new
diagnostics make visible. That is what buys the schedule.

---

### Phase 0 — this week; runs on 2026-08-16

**0.1 Manual autosave pass, before any code.** `ROADMAP.md:234-235`: move a fader, wait for
"Saved HH:MM:SS", **kill** the process (not File→Exit), relaunch, confirm it survived. Everything below
adds persisted state — prove the end-to-end write on a build that hasn't changed it yet.

**0.2 Duck floors** — `AutoMixer.cs:322`, `:390`. Interpolate in dB, not linearly, with a hard clamp so
the safety property is not a function of a slider:

```csharp
private const float GateDuckMinDb     = -12f;   // strength 0
private const float GateDuckMaxDb     = -26f;   // strength 100%  (was -inf)
private const float PriorityDuckMinDb = -12f;
private const float PriorityDuckMaxDb = -22f;   // shallower: a questioner must cut through it
private const float MinDuckGain       = 0.03f;  // ~-30 dB hard clamp

private static float DuckGain(float s, float minDb, float maxDb) =>
    Math.Max(MinDuckGain, (float)Math.Pow(10.0, (minDb + (maxDb - minDb) * s) / 20.0));
```

`pduck` gets its own shallower constant deliberately: `others` wants depth (comb rejection between room
mics hearing one talker), `pduck` wants presence.

Comb check, since Gate exists to kill comb: a delayed copy at −26 dB is ±0.4 dB of ripple; three
summing incoherently ≈ ±0.9 dB. At −22 dB ≈ ±1.4 dB. Both are well inside what the rig already
tolerates at its current −22.5 dB default — **the floor does not compromise Gate's purpose.**

Two consequences to handle rather than discover:
- `InputChannel.IsDucking` tests gain `< 0.85f`. With a permanent floor, every non-leader is always
  below it, so the amber LED now means *"not the winner"*. Document the shift; don't "fix" it.
- The leader's target stays exactly `1f`, so the `IsUnity` block-copy fast path is unaffected —
  **this stops being true once 0.3 lands.**

**0.3 Asymmetric gain slew**, in `AutoMixer`, not the audio thread. `PushToOutputs` currently ramps
linearly across one ~10 ms buffer. Rate-limit the value handed to `SetAutoMixGain`: rise ≈10 ms (goal 3
in the time domain), fall ≈120 ms, slow rise ≈400 ms for the *idle-open* transition specifically.

The audible steps are **not** hand-offs — a leader change permutes gains, so summed room-tone power is
roughly conserved. They are idle↔active (every routed mic → 1.0, ≈ +6 dB of summed room tone with
4 mics) and priority-duck enter/exit.

*Mandatory:* snap to target when `|g − target| < 1e-3`, or an asymptotic slew makes `IsUnity(target)`
permanently false and every buffer takes the scaled path forever. Reset slew state on route change.

**0.4 Idle hysteresis** — enter idle below `SilenceFloorRms`, leave above `SilenceFloorRms * 2`. Ship
the *mechanism* now; the *value* waits for Phase 1. Without it, a Broadcast noise floor sitting near the
threshold produces continuous idle chatter — the most likely failure mode of the flip.

**0.5 Flux-CV staleness (a live bug).** `_currentFluxCv` is written only in `ComputeFluxWindow` and
cleared only in `Stop()`, so a mic that falls below `FluxVoiceRms` keeps its last CV **indefinitely**,
and `AutoMixer.cs:309`'s `cv <= 0f` guard cannot see a stale non-zero value. A minutes-old CV silently
competes in Prefer natural and in `SelWeight`. Fix: stamp the tick in `ComputeFluxWindow`, return `0f`
when older than ~3 s, reset the EMA state after a long voiced gap.

**0.6 Land the in-flight Simple mode work**, with these corrections:
- add `StrengthPercent` to `OutputPlan` and set it per scene (Teaching 60, Prayer 50, Singing inert);
- resolve §2.3 (Standby vs the recorder tap) one way or the other;
- keep `SceneTransform`'s Teaching choice of **Gate + level only** (`ReferenceGuided=false`,
  `PreferNatural=false`) — level is the only metric finding 1 validated, and with the floor in place
  Gate no longer swallows interjections;
- add a totality test: every scene assigns every scene-owned property, so Prayer→Teaching cannot leave
  a muted lapel behind.

**0.7 Diagnostics that make the capture readable.**
- Per-channel `NoiseFloorLinear` (decaying-minimum estimator). This is the single number that decides
  `SilenceFloorRms`, it exists nowhere today, and it cannot be recovered afterwards from the log.
- A `gate_rate.py`-compatible digital-silence counter (peak < `1e-5` over 20 ms) **alongside** the
  existing RMS-based `RfSilenceRms` tally. They are different statistics; without both, someone will
  compare the log's `silent=%` to finding 4's numbers and draw a conclusion neither supports.
- Per output in `StateSnapshot`: `metric` (which rule actually decided this tick), `idle`,
  `priorityActive`, `duckFloor`. `idle`/`priorityActive` kill finding 4's `winner = -1` ambiguity
  structurally instead of by inference — and stop that inference silently breaking when 0.2 changes the
  gain values it depends on.

**Explicitly NOT on 08-16:** any constant retune, the layered priority duck, unfinished
Diagnostics/Settings windows, flipping more than one Anker.

---

### Phase 1 — weekday bench capture (Tue/Wed). This is what makes seven days work.

Without it, 08-16 is first contact with Broadcast and nothing can be retuned before 08-23.

1. **AnkerWork desktop over USB-C** (the phone app is Bluetooth-only): set **one** unit to Broadcast.
   Pick **#3** — healthy link, mid-pack occupancy. Not #2 (RF-marginal, would confound the result), not
   #4 (currently dominant, so the live mix would hinge on the experimental unit). Refresh firmware on
   all four while connected.
2. **Forget every Anker BT pairing**, then run `tools/audio-device-diag.ps1`: expect zero Anker BT
   radios live and four healthy Soundsync **capture** endpoints (watch for the half-link). A BT link
   left up produces dropouts indistinguishable from the gating being measured.
3. Relaunch and **confirm the preset still binds all five inputs by friendly name** — the pickup-mode
   change may re-enumerate the endpoint, and the dongles expose no USB serial.
4. Rename the unit in Windows Sound to `ANKER #3 BC`. Zero code, and it lands in the log prefix and
   `/state`'s `device` field automatically.
5. Capture with "Record all inputs" + `--log --state=7077`, narrating labels aloud:

| Window | Content | Yields |
| --- | --- | --- |
| 0–90 s | **absolute silence**, nobody moves | per-unit noise floor → `SilenceFloorRms` |
| 150–210 s | one talker, two fixed spots | level offset + proximity spread |
| 240–420 s | **sustained music at service SPL** | the gating probe |
| 430–490 s | two people alternating, opposite ends | hand-off / Q&A probe |

The quiet-room segment is **not** in the protocol at `ROADMAP.md:154` and must be added — it is free,
and it is the only source for the constant most likely to misbehave under Broadcast.

```powershell
python tools/gate_rate.py --stamp <bench> --lapel 1 --seg 0:90 --seg 150:210 --seg 240:420 --seg 430:490
python tools/naturalness.py
```

Gating is a property of the acoustic signal, not of who produced it, so a worship recording through the
PA at service SPL is a legitimate probe. Bench results are indicative; Sunday's A/B is authoritative.

---

### Phase 2 — Sunday 2026-08-16: the authoritative within-subject A/B

Keep the bench configuration exactly — **one Broadcast, three Standard, BT off**. Same acoustic input,
built-in control group (`ROADMAP.md:154-157`). A before/after against the 08-09 capture would be much
weaker; the room and talkers differ.

- **Level-compensate the Broadcast unit's channel gain** by the bench-measured offset, or it reads
  quieter, never wins under level selection, and the session yields gating data with zero selection
  data. Safe for the experiment: diag WAVs are written **pre-gain**, so `gate_rate.py` is unaffected.
- 60 s of quiet room before anyone arrives — the only clean noise floor with real HVAC/lighting load.
- **Operator labels**: wall-clock times for singing start/stop, teaching start, any Q&A, and any "that
  mic sounded bad" impression. Findings 1/2/3 all required labels; 08-09 had none, which is exactly why
  "does it pick the mic nearest the talker" is still open (`ROADMAP.md:208`).
- Do not change automix settings mid-service.

**GO / NO-GO:**

- **GO (strong)** — the Broadcast unit's `silent%` in the music window falls **below 3%** while the
  three controls stay in the measured 10–20% band, **and** `ALL silent at once` collapses to ≈0% with
  `dropouts n ≈ 0`. That last is the real prize and follows mechanically: one non-gating unit fills
  every hole.
- **GO (partial)** — `silent%` at least halves and `ALL silent` drops ≥5×. Flip all four; the effect
  compounds.
- **KILL** — the Broadcast unit sits within ±3 points of the controls, or within ±3 points of its own
  08-09 Standard value in a comparable window. Then no software or device-setting fix exists: record it
  as **finding 5** in `CLAUDE.md`, stop spending selector effort on singing, and escalate to hardware
  (board feed, or a real room mic on a preamp).

**Confounder guard, stated because it is asymmetric:** a BT link on the *Broadcast* unit adds dropouts
and biases **against** GO, so a GO remains trustworthy. A BT link on a *control* biases **toward** GO —
that is the false-positive path. Verify BT is off on all four, not just the test unit.

---

### Phase 3 — after GO: retune (week of 08-17, confirm 08-23)

The 08-16 A/B is a **mixed population** and cannot retune the selector — it answers GO/NO-GO and gives
noise-floor, level-offset and proximity numbers. Flip all four, capture 08-23, then retune with every
candidate validated by replay before it goes live.

| Constant | Today | Decided by |
| --- | --- | --- |
| `SilenceFloorRms` `:13` | `0.0018f` | measured quiet-room RMS × 2 |
| `FluxVoiceRms` `InputChannel:36` | `0.006f` | **prefer not to change** — compensate the lost AGC make-up with channel gain, restoring the scale this *and* `PriorityActiveRms`/`RefSpeechRms` were fitted to. One preset change beats six retuned constants. `SilenceFloorRms` must still be re-derived independently: gain compensation restores the *speech* scale but cannot restore the floor's relationship to a now-nonzero noise floor. |
| `NaturalFloorRatio` `:44` | `0.398f` | probably **unchanged** — widening it to keep candidates alive admits mics genuinely 12+ dB farther. Ship a `naturalCandidates` diagnostic so degeneration to Level is visible instead of silent. |
| `NatCvGood`/`NatCvBad` `:55-56` | `1.0`/`2.5`, inert | **do not rescale.** Replace the absolute anchors with a *relative* normalisation against the live min/median across routed mics — that kills this class of "constant fitted to a scale that later moved" bug permanently. |
| `CorrReady`/`CorrHysteresis` `:36-37` | `0.05f` | `_corr` distribution from `/state`; cross-check `tools/RefCorr` |
| `HandoffHoldTicks`/`HandoffHysteresis` `:21-22` | `20`/`1.413f` | **unchanged.** Finding 1. The floor, not a shorter hold, is the Q&A fix. |
| `NaturalHystRatio` `:48` | `0.85f` | unchanged — multiplicative, therefore scale-free |

**Flux-CV's successor.** One of its three jobs survives and gets stronger: with no gating, exact digital
silence can only mean an RF drop. The honest successor is not a selection metric but an **RF-health
veto** — a mic with recent drop edges is excluded from winning the election. That serves "use the best
mic" without pretending flux-CV can rank good mics.

**Selection defaults under Broadcast.** Prefer natural **off** everywhere until an all-Broadcast capture
shows flux-CV separating operator-labeled good/bad mics (note it was ON all session on 08-09). Match
lapel **on for Teaching** once `tools/RefCorr` confirms it on Broadcast data — but **off for Q&A**,
which is a correctness trap rather than taste: the reference is the *presenter's* lapel while the talker
is someone else, so envelope correlation ranks the mic nearest the **presenter** highest. It partially
self-corrects (the rule engages only while a priority mic is speaking) but not during overlap, which is
exactly when Q&A needs it.

### Phase 4 — remainder of Simple mode (08-24+)

Diagnostics window, Settings window, device behaviors, always-on-top + persisted window placement,
test-tone preflight. Only after scenes have driven at least two live services.

---

## 4. Verification

**Every code change, before it goes anywhere:**

```powershell
dotnet build; dotnet test
./tools/replay-baseline.ps1 -Name singing      -Stamp 20260809-092931 -Seek 95  -For 60
./tools/replay-baseline.ps1 -Name presentation -Stamp 20260809-092931 -Seek 300 -For 60
```

Run these on the **unmodified** build first — they must pass before they can be trusted as a gate.
Expected drift from Phase 0: none on occupancy or hand-off count (steady-state gains and the leader
election are untouched); possibly `medianCv` on idle channels from 0.5. Review each diff line
individually before `-Update`.

**Add a third fixture** covering a priority-active stretch (pick `<t>` from a log range where
`Output 0 … winner=0`) so the new `pduck` floor is regression-covered:

```powershell
./tools/replay-baseline.ps1 -Name lapel-duck -Stamp 20260809-092931 -Seek <t> -For 60 -Update
```

**Unit tests** (`AudioMixer.Tests`, pure logic only):
- for `s ∈ {0, 0.25, 0.5, 0.75, 1}`, Gate non-leader gain `> 0` and `pduck > 0`;
- a settled leader returns exactly `1f`, so `IsUnity` still holds;
- the slew never overshoots;
- scene totality (every scene assigns every scene-owned property);
- `Prayer_NeverLeavesAPriorityMicRouted`;
- `Singing_NeverSetsIsPriority`.

**Replay, for anything needing a device or a window:**

```powershell
dotnet run --project AudioMixer -- --replay=20260809-092931 --seek=1:35 --for=1:00 --state=7099 --log --open-all --simple
```

`--open-all` covers every window's markup in one run, and `BindingErrorListener` turns silent WPF
binding failures into log lines. Assert the scene path from `/state` with `--scene=NAME`.

**Manual, at a desk, never before a service:** the 0.1 autosave kill-and-relaunch pass; a Simple-mode
scene switch with Advanced open, watching every property the scene claims to write actually move.

**Docs, in the same change** (self-maintenance protocol):
- finding 4 gains the Broadcast result either way — GO or KILL;
- `CLAUDE.md:492` and `:497` carry stale sizing numbers (`max(500, count*96 + 160)` and a 150 px output
  column) against the actual `max(560, count*96 + 240)` and 230 px — correct them;
- prune the stale prose at `ROADMAP.md:240-249` left under the ✅;
- back-propagate the 2026-08-09 mic-count update into `ROADMAP.md:264`, which still argues
  "lapel-first, else a single Anker" from the superseded artifact-stacking reasoning.

---

## 5. Same-day fallback

Required, since this touches a live Sunday. Revert the one unit to Standard in AnkerWork (~2 minutes),
or in the mixer simply unroute that input. Neither needs a rebuild.

Phase 0's code changes are mode-independent by construction, so they do **not** need reverting if
Broadcast is abandoned — and the duck floor is a bug fix worth keeping regardless of what the Ankers
are doing.
