# AudioMixer Roadmap

Open work only. `CLAUDE.md` says how things work *today* and records every removed feature and dead
end, so finished and dropped items are not kept here — the git history has them. Rough priority order
within each section.

Status: 🏛 needs the room (a live session) · 🔬 needs a labelled capture first · 🛠 doable at a desk ·
💡 idea

---

## Chosen next (operator, 2026-09-23)

1. **Calibrate the rig and record a labelled service** — the first four items of the next section,
   next time the rig is set up. Most 🔬 items wait on this recording.
2. **Crash-proof recordings** — the background writer per recorder (Recording and robustness).
3. **Code health** — split `MainViewModel` and add the listed tests (Code health).

2 and 3 are desk work and can go first; 1 needs the room.

## Next time the rig is set up

- 🏛 **Calibrate the four-transmitter rig to −24 dBFS.** On 2026-09-23 the Rode strips read 13–17 dB
  under target after the gain moved from Windows to the receivers — unverified, because the
  calibration medians spanned several gain changes. Reset calibration, speak at each mic at working
  distance, adjust the **receiver** gain until Diagnostics' `speech` goes green, reset after each
  change. Everything in finding 8 depends on it.
- 🏛 **Read the room floor before anyone arrives** (a quiet minute, Diagnostics `floor`): it sets
  `SilenceFloorRms` and the leveler's idle hold (~6 dB above the bus floor).
- 🏛 **Record a labelled fixture.** "Record all inputs" for a whole service while someone notes when
  the wrong mic was on and when it sounded bad; move it to `analysis\keep\` the same day. **No golden
  baselines exist in the repo** (`tools/baselines/` was never committed, and the original fixtures'
  WAVs were pruned) — record them from this capture with its own preset. Every 🔬 item below waits on
  it.
- 🏛 **Use Speaking/Singing as intended for a few services.** Each tap lands in the decisions CSV as a
  per-bus mode change, so the operators produce the singing/speaking labels the auto-detect needs.
- 🏛 **The 30-second replug test**: note a receiver's container id, move it to another USB port, look
  again. The serial-derived identity is inferred, not observed, until this is done.
- 🏛 **If the 19:38 capture failure recurs** (sustained crackle on both buses, cured by a restart):
  keep the log, note the time and what was being done (a replug?), and count holes per minute in the
  mix. The trigger is unknown.

## Automixer and levels

- 🔬 **Relative thresholds.** `PriorityActiveRms` (−40), `PriorityBreakInRms` (−50) and
  `SilenceFloorRms` (−55) are absolute and assume speech near −24; the rig's level has moved 20+ dB
  between sessions, and when it is off the mix chops (finding 8). Candidate: derive speech/silence
  from each channel's own settled calibration median. Needs a cold-start rule and a reset after gain
  changes.
- 🔬 **Validate the 2026-09-26 selector changes offline.** Margin-scaled sustain (answers the
  near-equal chatter: 72 switches in 179 s on 2026-08-30, a 0.4 s median tenure on 2026-09-26, and
  the single-tick spike) and the lapel-relative break-in were built from decisions CSVs, not a
  labelled replay. Replay a per-mic capture of a discussion with the operator's "sounded bad at …"
  notes, old commit vs new: switches/min, tenure, first-syllable loss on real interjections. Watch
  for a genuine questioner waiting ~500 ms when two mics hear them within 6 dB.
- 🔬 **Keep or remove flux-CV.** It no longer selects anything, and costs an FFT per voiced window per
  mic on the capture thread. Its remaining claim — that it rises on RF dropouts — was measured on the
  Anker links only. If a Rode session shows `fluxCv` tracking `drops=`, keep it as a diagnostic;
  otherwise remove it with its `/state` key, Diagnostics column and `naturalness.py`.
- 🛠 **The level alert should not trust a stale median.** On 2026-09-23 it said a live lapel was 17 dB
  low because the histogram still held pre-session silence. `calAgeMs` now exists; the rule could
  require a median younger than the last gain change or reset.

## Operator safety and autopilot

- 🛠 **Soundcheck / preflight** (deferred by the operator 2026-09-21): a walked "speak into each mic"
  pass that checks level *and* which strip each transmitter landed on, plus both buses alive, bus
  mutes, Speaking/Singing and no idle priority lapel. The only check that would have caught
  2026-09-20 before the meeting. Open question: warn-and-proceed or block.
- 🛠 **Test tone per bus.** One click plays a tone out bus A or B so the operator can see OBS/Zoom
  receive it before the service. On 2026-07-05 the first 10–15 min never reached OBS while bus A was
  live — the fault was downstream.
- 🔬 **Suggest Singing automatically.** Detect likely singing and *prompt* ("Switch to Singing?"),
  never switch. Untested candidate: room-to-room envelope correlation (a congregation in unison moves
  every mic together; one talker doesn't). The Anker-era test found nothing reliable; the DSP-free rig
  and the operators' toggle labels make it worth a second look. The 15-minute reminder already covers
  forgetting to switch back.
- 💡 **Noise reduction by spectral subtraction, never a gate** — to lower HVAC during prayer. Cap the
  attenuation (6–12 dB) so it cannot mute anyone. Ship gate: offline on a labelled capture, `flux_cv`
  and `hf_burst` must not rise (musical noise).

## Recording and robustness

- 🛠 **A background writer per recorder.** Audio threads hand buffers to a queue; one thread per
  recorder writes them and rewrites the WAV header every ~10 s. Removes the only disk I/O from the
  audio threads and makes a crash or power cut lose seconds instead of the whole file. The naive
  version (flushing from the audio thread) caused 24 underruns/min — it is in `git stash`; verify the
  new one with the same flush-phase test.
- 🛠 **Log rotation / size cap.** Nothing rotates `%TEMP%\AudioMixer.log` (8.6 MB in one evening,
  ~285 MB over six days once). Rotate per launch, keep the last few — never delete without the
  retention rule being the operator's decision.
- 🛠 **RF drop edges into `/state` and the session record** (`InputSummary` has underruns and clipping
  but not `DropEdges`). On a non-gating mic, exact silence mid-speech can only be an RF drop or a
  transmitter switched off.
- 🛠 **Resync off the UI thread** (optional): it freezes the window ~1 s while every device restarts.

## The rig

- 🏛 **A third receiver (six transmitters).** Check with RØDE how many Wireless PRO systems coexist in
  one room first. Placement (see CLAUDE.md), battery logistics for 6 TX + 3 RX, and a labelled capture
  to decide where the transmitters actually go.
- 💡 **Presenter on a Wireless PRO transmitter with a lav** instead of the wired Classic on the aux jack,
  if that separate gain path keeps being a nuisance — it would make every mic identical.
- 🛠 **Per-serial endpoint gain** (waits on the replug test): remember Windows gain per receiver serial
  and restore it on a new port. Opt-in and visible only — silently re-applying a boost over a
  corrected transmitter gain clipped the capture once.
- 🛠 **An identify flow**: tap a transmitter, the app shows which strip jumped. The recovery path when
  identity cannot be resolved (port-derived devices).

## Code health

- 🛠 **Split `MainViewModel`** (1,639 lines, twice the next file): device binding, recording, alerts,
  and a `PresetApplier` mirroring `PresetMapper` so the load/save round-trip is testable.
- 🛠 **Tests worth adding**: `DiagnosticRow.Build` state precedence, `OutputViewModel.RefreshVerdict`
  (the three causes of `winner = −1`), `DeviceList.Sync`, the stale-calibration health rule,
  `SessionRecorder`'s >5 s gap rule, and `RecordingRetention`'s oldest-first loop (needs an injectable
  free-space source).
- 🛠 **A synthetic replay source** — talker A/B/overlap/silence, a lapel bump over −40 dBFS — for
  situations no recording contains. Tiny and deterministic.
