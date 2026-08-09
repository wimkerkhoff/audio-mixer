# AudioMixer Roadmap

Planned work and ideas. `CLAUDE.md` documents how the code works *today*; this file is what's *next*.
Keep forward-looking TODOs here (not in `CLAUDE.md`). Rough priority order within each section.

Status key: 🔲 planned · 🔬 needs live data / validation · 🛠 doable now (no room needed) · 💡 idea

---

## Operator experience

### 🛠 Live automix diagnostics panel ("why this mic?")
An optional, toggleable telemetry view that surfaces the automixer's live reasoning **in-app**, so
diagnosing a bad selection no longer means reading the `/state` JSON endpoint from an external tool
(exactly the human-in-the-loop this took on 2026-07-26 — an operator had to have Claude poll
`/state` to explain why each talker was on the wrong mic). Off by default / hidden in Easy mode —
regular operation never needs it — but one click gives real-time insight into **why a mic is (or
isn't) selected**.

Per output, show the decision as it happens:
- The **held leader** + current **winner**, the hold countdown (`_winnerHold`), and which
  **mode/margin** is deciding — level (+3 dB), natural (flux-cv ×0.85), or correlation (+0.05).
- A live per-mic row: **env level**, **flux-cv**, **ref-corr**, automix **gain**, route/mute,
  priority/ducking — deciding metric highlighted, and the challenger being *blocked* called out
  (e.g. "#2 louder but held off by the 3 dB margin + 140 ms hold").
- **Rank the non-winners** by selection score — #2 is who takes over if the leader drops; mics out
  of the running (Bluetooth / muted for singing / idle lapel) marked "—". (Operator asked for this.)
- Opens as a **separate resizable window** (Diagnostics), alongside a Devices tab (endpoints/BT/dongles).
- A one-line plain-English verdict per output: e.g. "#1 winning: lowest flux-cv (0.38) among mics
  within −8 dB; #2 louder (−22 dB) but blocked by hold."

*Why:* the data already exists — `MainViewModel.BuildStateJson` / the `--state` endpoint expose
env/cv/corr/winner/hold — so this is mostly a **presentation** task: bind a hidden in-app view to the
same snapshot on the existing ~30 Hz meter timer (NOT per-buffer). Newly worthwhile because
`CurrentFluxCv` is now genuinely live (the 2026-07-26 cross-buffer-windowing fix — before that the cv
column would have shown a frozen value). Pairs with the clarity→flux-cv readout unification and
operator overrides below.

### ✅ Simple mode, scenes, health banner, Diagnostics + Settings — shipped 2026-08-09
Opt-in via `--simple` (default OFF, so the exe carried to a service opens exactly what it does today).
Simple mode binds the **same `MainViewModel` instance** as Advanced, so the two views cannot disagree —
which also makes running them side by side a valid comparison. `MainWindow.xaml` was not touched.
- **Scenes** are a pure transform (`Services/SceneTransform`) with 20 unit tests, including the safety
  property that no scene/override combination can leave the stream with nothing routed.
- **Health banner** is a pure evaluator (`Services/HealthMonitor`, 15 tests) covering the failures
  these sessions actually hit: stream silent, no output device, idle armed lapel, priority mic during
  Singing, presenter off-air, stalled mic, Anker on Bluetooth, long-silent mic.
- **Diagnostics** ranks mics by the metric the output is actually deciding on and states the verdict in
  one line, including which of the three causes of `winner = -1` applies.
- Verified: all four windows open with live data and **zero binding errors** (`--open-all --log`), all
  four scenes assert correctly from `/state` (`--scene=`), and both golden baselines still PASS.

**Still open from the original design:** the mic dots don't yet click through to that input in
Advanced; there's no always-on-top persistence (the Pin button is per-session); Settings' hide-virtual
and BT-warn options are runtime-only (not persisted); and the alert banner's action buttons are labels,
not yet wired to actions.

### 🔲 "Easy UI" mode — *original design notes; superseded by the entry above*
A simplified, operator-proof default view for non-technical volunteers, with progressive disclosure
to everything else.

**Simple mode surfaces exactly four things:**
1. **Scene selector** — Standby / Teaching / Prayer / Singing (see Scene control). The one control that matters.
2. **Output "on-air" cards** — per output (Zoom/OBS, Headset): a live meter, an On-air / Off-air / Muted pill, a mute button. Answers "is audio flowing?"
3. **Mic health dots** — one chip per mic: green live · amber idle/ducked · red dead/Bluetooth · grey off. Click a dot → jump to that input in Advanced.
4. **Health/alert banner** — the productized in-app monitor (below). One color-coded line, dismissible, with an action button where possible.

Plus **one** operator override: a coarse **Voice source** toggle — **Lapel vs Room mics** — shown only in Teaching & Singing. Operators get NO per-mic control in Simple mode (explicit call); to touch a mic they drop into Advanced.

**Compact footprint (hard requirement):** the operator runs on a *single monitor* shared with YouTube, SermonAudio, OBS, and Zoom — screen space is tight. Simple mode must be **small and dockable** — a narrow, always-usable panel, not a full-width console; design to a compact minimum width and consider an always-on-top option. (The mockup shows the full layout; the shipped panel should be tighter.)

**Progressive disclosure:**
- **Advanced** toggle → today's full per-channel mixer, unchanged. Nothing hidden is more than one click away.
- **Diagnostics** and **Settings** open as **separate resizable windows** (the main window is fixed-size).

**Alert banner = the monitor, productized.** These sessions are its spec — it surfaces exactly what a human had to watch `/state` for: unrouted-lapel-while-priority (off-air presenter), stream silent, dead/stalled mic, **Anker on Bluetooth**, priority ducking the room during singing, mic reconnected → reassigned. Action buttons where possible ("How to fix →", "Switch to Singing?").

**Clickable mockup (2026-08):** https://claude.ai/code/artifact/5e1b4b40-43f0-4a1a-95c2-56c60658b164 — scenes, source override, health banner, live meters, and the separate Diagnostics (ranked "why this mic" table) + Settings windows.

**Suggested build order:** (1) Simple shell + scene buttons, (2) alert/health banner, (3) Settings window + device behaviors (hide-virtual, BT-warn, auto-readd), (4) Diagnostics window.

*Why:* operators aren't audio-savvy; the common path has to "just work," and on a crowded single screen it has to stay small.

### 🔲 Scene control — Standby / Teaching / Prayer / Singing
One operator control that switches the whole behavior:
- **Standby** — outputs muted, so pre-service chatter never reaches Zoom/recording.
- **Teaching** — the current follow-the-talker automix (priority-lapel ducks the room; correct for
  one coherent talker).
- **Prayer** — turn-taking room mics, no lapel: Gate, Prefer-natural OFF, lapel muted/unrouted (it's
  a priority-duck hazard). See the prayer-meeting-scene memory.
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

**Update — measured 2026-08-09: mic count is the WRONG VARIABLE.** Both earlier positions above are
superseded. The Ankers gate congregational singing to **true digital silence, and they do it in
unison** — 13–21% of frames each, all four simultaneously 4.6% of frames, **51× more than
independence predicts**; 71 total-stream dropouts in 170 s (median 60 ms, max 780 ms). Operator
verdict live: "interrupted constantly, can't follow it at all." Summing more mics **cannot** fill the
holes because the holes exist in every source at the same instant — confirmed live, switching Automix
to Off changed nothing. So single-mic and multi-mic fail identically, and the earlier "compounded DSP
artifacts → collapse to one" reasoning was measuring texture while the real defect was dropouts. See
CLAUDE.md finding 4 for the numbers.

**What the Singing scene can therefore actually do:** nothing clever with selection. Its only honest
job is (1) suspend priority-ducking, (2) stop follow-the-talker, (3) prefer the **Rode lapel** if it
is in use — a real mic with no speakerphone DSP, and the only in-house source that doesn't gate. Fixing
worship audio properly is upstream of this app: S500 Broadcast mode (below), the lapel, or a board feed.

*Why manual, not auto-detected:* the automix assumes one talker at a time; singing and pre-session
chatter break that, and the *desired action differs per scene*. Auto-detection is unproven — on the
2026-07-05 data no reference-free signal separated singing from teaching (room-mic flux-cv rose only
~0.1, lapel duty-cycle and lapel↔room correlation shifted but every margin overlapped teaching's own
variation; raw multi-mic activity was useless — 3–4 mics hot for the entire 15 min). A manual
control is reliable. Pairs naturally with Easy UI.

### 🔬 Singing auto-detect → operator prompt → auto-revert
A semi-automatic layer over the Singing scene: best-effort **detect** likely singing, **prompt** the
operator ("Singing? Switch to a single mic until it's over") rather than auto-switching, apply the
scene on confirm, and **auto-revert** to the prior mode when singing ends. Mic policy in the scene:
**if the lapel is in use, just use the lapel** (a real mic; skip the Ankers entirely); **if no
lapel, pin the single cleanest Anker** (lowest live flux-cv) — not several (compounded speakerphone
DSP artifacts, see the Singing scene note above).
- *Why suggest-not-switch:* auto-detection is unproven — no reference-free signal cleanly separated
  singing from teaching on the 2026-07-05 data (every margin overlapped teaching's own variation),
  and a wrong auto-switch mid-service is worse than the problem. A confirm prompt keeps the operator
  in control while removing the "notice it and reconfigure by hand" burden the operator hit live.
- *Detector candidates (need labeled captures; flux-cv now works so replays are faithful):*
  **room-to-room envelope correlation** (congregation unison → all room mics track each other;
  teaching → they don't — an untested angle, unlike the lapel-based signals that failed),
  sustained-pitch / harmonic energy, and low silent-gap fraction over a multi-second window.
- *Revert:* detector quiet for N seconds → prompt or auto-return to regular mode.
Pairs with Scene control + Easy UI.

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

### 🔬 Test S500 "Broadcast" pickup mode — the only DSP lever Anker exposes
Highest-value cheap experiment on the rig. The AnkerWork app offers two voice pickup modes: **Standard**
("picks up all sounds from the near end") and **Broadcast** ("restores and deliver original sounds by
turning the speaker off"). Broadcast is the *only* DSP-adjacent control that exists — there is **no**
noise-reduction toggle, no EQ, no music mode. Its documented downside (you can't hear the far end) costs
us nothing: the mixer only captures from the Ankers and monitoring is on the headset, so their speakers
are dead weight. Plausible mechanism: speaker off → no acoustic echo to cancel → much of the DSP chain
has nothing to do. Anker also recommends it specifically for USB connections, which is how the dongles
present.

- **Test design (do NOT flip all four):** set **one** unit to Broadcast, leave the other three on
  Standard as a control, then record all inputs over sustained singing/music and compare per-unit
  digital-silence rates (`tools/` gate-rate analyzer, from `scratchpad/gate_check.py`). Same acoustic
  input, built-in control group. If the Broadcast unit's gating rate doesn't drop, the hypothesis is dead.
- **Config path:** desktop AnkerWork software (software.ankerwork.com) over **USB-C** avoids Bluetooth
  entirely — try this first; docs only confirm firmware update this way, so pickup mode needs hands-on
  checking. The phone app is **Bluetooth-only**.
- **Bluetooth caveat (load-bearing):** an S500 holds its Soundsync link *and* a BT link at once, so the
  phone app works without unplugging anything — but an active BT link is a second 2.4 GHz radio that
  garbles the **weakest** dongle in the room, not necessarily the unit being configured. So: configure,
  then **disconnect and re-forget the pairing**, confirm the dongle link is healthy, and only *then*
  record the test. A BT link left up during the test produces dropouts indistinguishable from the gating
  we're measuring and would invalidate the result.
- Check/refresh **firmware** on all four while connected — NR behaviour can change between versions.

*Why:* CLAUDE.md finding 4 shows no mixer-side fix for singing exists. This is the last cheap software
lever before the answer becomes "buy a real mic."

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
Now verifiable from the logged transcript (gains/cv/winner):
- ✅ **Bounce fix (multiplicative natural hysteresis) — VALIDATED live 2026-08-09.** 155 hand-offs over
  26.7 min (5.8/min), median dwell **7.0 s**, 11 under 2 s, **zero** under 1 s. Compare the pre-fix
  offline replay's 113 flips. Occupancy stayed spread (37.5 / 36.5 / 21.2 / 4.8%) rather than pinning
  one mic, and the winner tracked a real room change (#3 dominant pre-service → #4 during the
  presentation). Prefer-natural was ON all session and did *not* reproduce the 2026-07-26 global-pin
  failure. **Still open:** whether it picks the mic *nearest the talker* — occupancy is distribution,
  not correctness, and no operator labels were captured. Needs a labeled Q&A segment.
- Share quality-weighting — ⚠️ currently **inert**: the 2026-07-26 flux-CV scale change left
  `AutoMixer.SelWeight`'s `NatCv` constants (1.0/2.5) tuned to the old inflated scale, so post-fix CV
  (~0.4) clamps every mic to weight 1.0. Fix = rescale to the new scale (~0.40 good / ~0.55 bad),
  validated against a labeled offline replay — do NOT tune by feel. Only affects Share+natural; Gate
  is unaffected.
- Prefer natural overall — sensible override rate, picks good mics.
- Match lapel (reference-guided) — only testable when a lapel is actually in use.

### 🔬 Verify the 2026-08-02 refactor at the next launch
A code-quality pass (commits `11acf2b`…`4b53877`) landed with **no behavior change intended**. Only
the first commit has ever run: the app stayed up on the `11acf2b` build for the rest of that session,
so `7a24649`, `5a71672`, `645b8cc` and `4b53877` are **build-verified only**. First launch is the
verification session — do it at a desk, not five minutes before a service.
**✅ Verified 2026-08-09 against the replay rig** (build `1.0.0+34188d4`, 20 s of 5-channel replayed
audio). The 09:28 service itself still ran `11acf2b`, so this was the first execution of all four:
- **Per-bus LEDs (`4b53877`)** — `Services/BindingErrorListener` (new, on with `--log`) routes WPF
  binding failures into the log; the run reported **zero**, so every LED path (`IsOn`, `IsDucking`,
  `LedTooltip`, `ShortLabel` on `RouteToggleViewModel`) resolves at runtime. This is now permanent
  coverage — a broken binding in the *new* UI will show up as a log line instead of a blank control.
- **Capture callback (`5a71672`)** — 5 channels × 20 s through `OnDataAvailable`, no
  `push to output … failed`, no `AutoMixer.Tick failed`.
- **Preset load (`7a24649`)** — `DeviceResolver` ran at startup and bound the saved channels.
- **Autosave (`645b8cc`)** — allowlist extracted to `Services/PersistedProperties` and covered by
  `AudioMixer.Tests/PersistedPropertiesTests` (4 tests): the meter tick and the persisted set are
  asserted disjoint, `RefreshLed` is asserted not to raise `IsOn`, and every allowlisted name is
  asserted to exist. **Still worth one manual pass** — move a fader, wait for "Saved HH:MM:SS", kill
  the process, relaunch — since the test covers the mechanism, not the end-to-end write.
- **Per-bus LEDs (`4b53877`) — the one real risk.** The A/B LED markup was replaced by an
  `ItemsControl` over `Routes`, and WPF resolves binding paths at *runtime*, so a clean build proves
  nothing. Look at the input strips: each should show a lettered LED per bus — green routed+passing,
  amber routed+ducked, dim not routed — with a tooltip naming the bus. If they're dead or blank,
  `git revert 4b53877` restores the old markup; nothing else depends on it.
- **Autosave (`645b8cc`)** — it had never fired while running (the meter tick reset its debounce
  every 33 ms); only a clean exit saved. Test: change a volume/route, wait ~2 s for "Saved HH:MM:SS"
  in the status bar, then **kill** the process (not File→Exit) and relaunch — the change should
  survive. Under the old code it would not have.
- **Preset load (`7a24649`)** — device resolution moved to `Services/DeviceResolver`. Confirm all
  four Ankers + lapel + both outputs still bind by name after a reboot that reshuffles endpoint GUIDs.
- **Capture callback (`5a71672`)** — `OnDataAvailable` was split into three methods. Watch for any
  new dropout/glitch and check the log for `push to output … failed` (newly logged; it used to be
  swallowed silently, as did a throwing `AutoMixer.Tick`).

### 🔲 Decide: the green "selected" LED — restore it or drop it
CLAUDE.md documents `IsAutoMixActive` driving a per-input **green "this mic is the winner" LED**, but
nothing in `MainWindow.xaml` binds it — `ChannelViewModel.IsAutoMixActive` *and* `IsDucking` are
raised 30x/second and consumed by no view. So either the LED was lost in an earlier UI edit (a
regression worth restoring — knowing which mic the automixer picked is the single most useful thing
to see live) or the docs describe an intent that never shipped. Left in place rather than guessing.
Decide, then either bind it or delete both properties and correct CLAUDE.md. Note the per-bus LEDs
now show routed/ducked but *not* "winner", so the information really is missing from the UI today.

### 🔲 Code-quality leftovers (from the 2026-08-02 audit)
Lower priority than anything above; none of it is user-visible.
- `MainViewModel` is still ~660 lines and calls `MessageBox.Show` directly in four places (the
  delay-detection flow), which makes that flow untestable and is the last real MVVM leak.
- The crest→`Clarity` path (~40 lines across `AutoMixer`, `AutoMixDiag`, three VM properties) feeds
  one gear-popup bar using a metric CLAUDE.md documents as *not* predictive through the Anker DSP.
  Keep it as an operator readout or delete it — a product call, not a cleanup.
- `OutputViewModel.Label`/`ShortLabel` were deleted as dead; if a future UI wants bus names, use
  `OutputViewModel.Tag(index)` rather than reintroducing A/B ternaries.

### 🔬 Singing capture & analysis
First singing captured 2026-07-05 (~3 min, one room, one of three room mics dead). Findings: (1) no
reference-free signal cleanly separated singing from teaching → manual scene control, not
auto-detect; (2) room mics don't *acoustically* comb when summed (coherence 0.13) — BUT summing
multiple Anker speakerphones stacks their independent DSP artifacts and sounds *worse* by ear (real
worship 2026-08), so the Singing scene is **lapel-first, else a single Anker**, not several (see the
Operator-experience Singing note). Still needed: a *fuller* capture — longer, all room mics live,
ideally a loud full-congregation song — to re-test any singing-vs-speech discriminator (esp.
room-to-room correlation) and confirm the mic-count call. Analysis lives in scratchpad (`singing_vs_speech.py`,
`comb_test.py`, `find_singing.py`); fold the keepers into `tools/`.

---

## Testability (foundation for the UI work)

The blocker on all UI work is that the app can't be exercised without a room full of people. Every
session already leaves five sample-aligned `diag-input*.wav` files — replaying those as capture sources
turns "need a congregation" into "replay 2026-08-09 09:31".

### 🛠 WAV replay capture source + rig
Abstract the capture behind NAudio's `IWaveIn` (which `WasapiCapture` already implements), add a
replay implementation over the diag WAVs, and everything downstream — automixer, hold/hysteresis,
flux-cv, meters, LEDs, ducking, scenes — runs unmodified.
- **Deliver ~480-frame buffers**, not 512/1024. WASAPI shared mode gives <512, which is the whole reason
  the flux-cv accumulation path exists; replaying at 512 would silently bypass it and test different code.
- The diag WAVs are written **pre-gain, pre-delay, post-conversion** (48 kHz stereo float32), so replay
  re-runs gain/mute/delay/flux/automix on exactly the samples the live selector saw.
- **One clock pumping all N sources in lockstep** — independent per-file timers would drift and desync
  the automix decision.
- Reader must tolerate **unfinalized WAVs** (header claims 0 frames while recording) — same trick as
  `tools/live_wav.py`.
- Real-time clock for eyeballing the UI; faster-than-real-time for batch regression runs.
- `--replay` is a **sandbox**: separate mutex name (so it can run alongside the operator's mixer, which
  the single-instance guard would otherwise block), **no preset autosave**, and **no output devices by
  default** (two instances must not both open CABLE Input and double audio into Zoom).
- Complementary **synthetic** generator — talker A/B/overlap/silence, a lapel bump crossing −40 dBFS.
  Deterministic, tiny, and can produce situations the recordings don't contain.

### ✅ `/state` golden-baseline regression harness — shipped 2026-08-09
`tools/replay-baseline.ps1 -Name <fixture> -Stamp <session> -Seek <s> -For <s> [-Update]`. Baselines in
`tools/baselines/`. Two recorded so far from the 2026-08-09 session: **singing** (seek 95) and
**presentation** (seek 300); both re-run PASS.
- It compares **aggregates, not raw samples** — mode, hand-off count, winner occupancy, median flux-cv.
  Hand-off count proved exactly reproducible and is the sensitive signal. `medianEnv` is recorded but
  only loosely checked (±4 dB): env moves fast and `/state` is polled at arbitrary phase, so tightening
  it yields false alarms, not earlier warnings.
- Samples are gridded in **replay-position space**, not wall-clock. Polling is wall-clock, so without
  this a `-Speed 4` run captures half as many samples and the diff measures the poll rate.
- **Record and check at the same `-Speed`, and stay at 1–2.** Higher speeds saturate the process:
  `/state` polls get starved and the rig's catch-up cap starts dropping audio.
- Found and fixed a real bug while building it: the automix tick ran on a wall clock, so `--speed`
  changed selector behaviour (at speed 2 the automixer saw half as many ticks per audio-second,
  halving every hold). The rig now drives the tick from the audio clock — one tick per 480-frame
  chunk — which makes replay both deterministic and speed-independent.

Still to add: a **prayer** fixture from 2026-07-26, and a synthetic generator for cases the recordings
don't contain.

### 🛠 Scenes as a pure transform
Implement a scene as a testable function (`Scene` → list of property assignments) separate from the code
that applies it, so scene behaviour is unit-testable with no audio. Scenes are the riskiest new surface
because they *write* operator state. **Check every new persisted VM property against
`PersistedProperties`** — the autosave allowlist fails silently in both directions (see the UI gotcha).

## Tooling

### 🛠 Log rotation / size cap
The log is append-only; a long-running session grew to ~285 MB over ~6 days. Rotate per launch or
cap the file size so it can't balloon.

### 🛠 Keep the validation harness current
`tools/`: `naturalness.py`, `voice_quality.py`, `replay_natural.py`, `review_natural.py`,
`replay_share.py`, `scene4.py`/`scene5.py`, plus the singing set (`live_wav.py`, `find_singing.py`,
`singing_vs_speech.py`, `comb_test.py`). Re-run after each captured session. (Live and offline
`flux_cv` now share a scale after the 2026-07-26 fix, so replays are faithful to the engine.) Note
`live_wav.py` reads in-progress diag WAVs that `soundfile` can't — reuse it for any tool that runs
mid-recording.

---

## UI nice-to-haves

### 💡 Unify the "Mic clarity" readout to naturalness
The per-input gear popup's clarity bar still shows the old **crest** metric, but selection now uses
**flux-cv** naturalness. Switch the readout to flux-cv so what the operator sees matches what drives
the choice.

### 💡 Operator overrides
Per output: pin a mic always-on, or exclude a known-bad mic from the competition — a manual escape
hatch when the automix picks wrong. **Evidence 2026-08-09:** Anker #2 won only **4.8%** of the session
(3.6% during the presentation) while carrying by far the highest flux-cv (0.53 vs the pack's ~0.42) and
the lowest level — never wins, but stays in the competition every tick. That combination (never selected
+ high instability) is the signature of a badly-placed or RF-marginal unit; excluding it is exactly the
escape hatch this item is for. Check which physical area #2 covers before excluding it.

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
latest build with diagnostics on · cross-buffer flux-CV windowing (live CV unfrozen, now on the
offline scale).
