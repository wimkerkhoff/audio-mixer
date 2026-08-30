# AudioMixer

A Windows desktop audio mixer: 1–10 configurable inputs (default 3) → 2 configurable outputs, with
per-channel volume, mute, delay, routing toggles, VU meters, recording, and presets. Built to send a
mix to a headset AND Zoom/OBS (via VB-CABLE) simultaneously, with delay compensation and an
automixer for distributed room mics.

Input count is runtime-configurable via a toolbar picker (`MainViewModel.InputCount` →
`AudioEngine.SetInputCount`): the engine grows/shrinks its `Inputs` array (preserving existing
channels, stop+dispose on shrink) and restarts the output buses to re-collect providers. `Channels`
is an `ObservableCollection`; the window is non-resizable (`ResizeMode=CanMinimize`) and its width is
computed from the input count in `MainWindow` code-behind (see the UniformGrid gotcha).

## The rig (why the gotchas look the way they do)

Almost every hard-won finding below comes from one real deployment — assume this context when
reading them:

- **4× Anker PowerConf S500 speakerphones** as distributed room mics, each on its own **2.4 GHz USB
  "Soundsync" dongle** (never Bluetooth — see gotchas). They are *speakerphones*: aggressive AGC,
  noise suppression, and gating to true digital silence sit between the room and every sample we
  see. This single fact invalidates most textbook mic-selection metrics (see "Measured findings").
  **Being retired (2026-08-23):** Anker confirmed Broadcast pickup mode was *removed from the
  firmware*, which was the last remaining lever against finding 4, and offered a refund. Everything
  below about speakerphone DSP stays — it is why the replacement rig looks the way it does — but the
  S500s are no longer the target hardware.
- **RØDE Wireless PRO** (2 transmitters per receiver) is becoming the primary rig. DSP-free: it does
  not gate, which is the one thing that made the S500s unusable. See finding 5, and note it is a
  *body-worn* mic — it covers people, not a room, so mic count and placement do the work that DSP
  used to pretend to do.
- **Rode lapel** on the presenter, used as a **priority** channel when present.
- Room is ~60 ft wide; the furthest mic sits ~50 ft from the dongles — **at the edge of RF range**.
- Outputs: a monitor headset + VB-CABLE feeding Zoom/OBS.
- Usage scenes: teaching (one talker), prayer meetings (turn-taking room mics, no lapel),
  congregational singing (the automixer's one-talker assumption inverts). Scene guidance lives in
  ROADMAP.md and session memory, not here.

## Stack

- **.NET 8** + **WPF** (single-window desktop app)
- **NAudio** 2.2.1 for audio I/O (WASAPI shared mode)
- **System.Text.Json** for preset persistence
- MVVM pattern (ViewModels per channel + main)
- Offline analysis (`tools/*.py`): `pip install numpy scipy soundfile matplotlib praat-parselmouth`

## Project layout

```
AudioMixer.sln
ROADMAP.md                    # Planned work / scene design. Not a spec of what IS.
publish.ps1                   # Single-file publish
AudioMixer.Tests/             # xunit. Pure-logic only (no devices/WPF): scenes, health, autosave allowlist
AudioMixer/
├── App.xaml / App.xaml.cs    # Single-instance mutex; window creation; ApplyCliFlags (see Conventions)
├── MainWindow.xaml / .cs      # Advanced view (--advanced). Size set in code-behind, NOT bound (see gotcha)
├── Views/                    # Operator UI. Separate files so MainWindow.xaml is never touched.
│   ├── SimpleWindow.xaml     # Scene selector, on-air cards, mic dots, health banner (DEFAULT window)
│   ├── DiagnosticsWindow.xaml # Ranked "why this mic?" table; own 10 Hz timer, off when closed
│   ├── SettingsWindow.xaml   # Mic roles, device-picker options, diagnostics summary
│   └── OperatorConverters.cs # Severity->brush, mic-dot colour, null/inverse visibility
├── Audio/
│   ├── AudioEngine.cs        # Capture/render lifecycle, graph wiring, AutoMix tick + stall watchdog
│   ├── Replay/               # Replay a recorded session instead of live mics (see "Testing")
│   │   ├── ReplaySource.cs   # IWaveIn over a (possibly unfinalized) diag WAV
│   │   ├── ReplayRig.cs      # One clock pumping all sources in lockstep; drives the automix tick
│   │   └── ReplayOptions.cs  # --replay sandbox semantics
│   ├── InputChannel.cs       # capture → side split → taps → low-cut → mute → gain → delay → automix → push
│   ├── OutputBus.cs          # MixingSampleProvider → peak tap → volume → WasapiOut; optional recorder
│   ├── AutoMixer.cs          # Per-output leader decision loop (level / lapel-corr / natural); off-thread
│   ├── AutoMixMode.cs        # enum Off/Share/Gate
│   ├── IAutoMixControl.cs    # Per-output automix setters — the VM's one dependency, not N delegates
│   ├── AudioDeviceInfo.cs    # Device id + friendly name record
│   ├── ChannelSource.cs      # Stereo/Left/Right — which transmitter of a split receiver a strip takes
│   ├── DelayLine.cs          # Ring buffer with adjustable read offset
│   ├── PeakMeter.cs          # Peak dBFS per buffer, peak-hold decay
│   ├── TapSampleProvider.cs / TrackingSampleProvider.cs   # Non-consuming taps in the graph
│   ├── MixRecorder.cs        # WaveFileWriter wrapper, thread-safe start/stop
│   └── AudioLog.cs           # Opt-in file log (AUDIOMIXER_LOG/--log → %TEMP%\AudioMixer.log)
├── ViewModels/
│   ├── MainViewModel.cs      # Engine lifecycle, device pickers, presets, record state, meter tick
│   ├── ChannelViewModel.cs   # Per-input: device, volume, mute, delay, routes, meter, priority, LEDs
│   ├── OutputViewModel.cs    # Per-output: device, meter, volume, record, automix mode + selection opts
│   └── DeviceList.cs / RelayCommand.cs / ViewModelBase.cs
├── Models/MixerPreset.cs     # Serializable: device ids+names, volumes, mutes, delays, routes, automix
├── Services/
│   ├── SceneTransform.cs     # PURE scene rules: (scene, override, state) -> state. Unit-tested.
│   ├── HealthMonitor.cs      # PURE alert rules for the banner. Unit-tested.
│   ├── PersistedProperties.cs # The autosave allowlist, extracted so its invariant is testable
│   ├── BindingErrorListener.cs # WPF binding failures -> the log (on with --log)
│   ├── PresetStore.cs        # JSON load/save to %APPDATA%\AudioMixer\presets.json
│   ├── PresetMapper.cs       # View-model state → MixerPreset (the reverse lives in ApplyPreset)
│   ├── DeviceResolver.cs     # Preset device → live endpoint: id first, then friendly name (see gotcha)
│   ├── DelayAnalyzer.cs      # "Detect Delays": onset-envelope cross-correlation → suggested delays
│   ├── StateSnapshot.cs      # Builds the /state JSON (the selector's reasoning, not just mixer state)
│   ├── DiagnosticsLog.cs     # Meter-tick logging: talker hand-offs + ~1 Hz output/input health dump
│   └── StateServer.cs        # Opt-in loopback JSON state endpoint (diagnostics)
├── Controls/VuMeter.xaml     # Gradient bar with peak-hold tick
└── Assets/app.ico
tools/                        # Offline analysis + diagnostics — validate selector changes HERE first
├── AnalyzeInputs/            # C#: replays selector metrics over per-mic diag WAVs
├── RefCorr/                  # C#: lapel-reference envelope correlation ranking
├── gate_rate.py              # per-mic digital-silence rate + simultaneity (see finding 4)
├── naturalness.py            # flux-CV artifact ranking (the "natural" metric, offline)
├── replay_natural.py         # Replays the shipped "Prefer natural" rule over a capture
├── replay_share.py / scene4.py / scene5.py   # Share/scene replays
├── voice_quality.py          # Praat HNR/CPPS/jitter/shimmer (shows the inversion — see findings)
├── spectro.py / comb_test.py / singing_vs_speech.py / find_singing.py / live_wav.py
├── audio-device-diag.ps1     # WASAPI/BT/dongle enumeration + half-link detection
└── build-readme.mjs          # README.md → README.html
```

Offline tools replay against the "record all inputs" per-mic WAVs at
`%USERPROFILE%\Documents\AudioMixer\analysis\diag-input*.wav`.

## Audio architecture

**Pipeline per channel:**
```
WasapiCapture → resample to 48kHz stereo float32 → side split (L/R/stereo) → peak/analysis taps →
low-cut → mute gate → gain → DelayLine (ring buffer w/ read offset) → post peak →
level/flux/RF measurement → per-output automix gain → bus mixer
```

The **side split** (`ChannelSource`) is deliberately the FIRST stage after resampling: everything
downstream — meters, the analysis recorder, level/flux-CV/RF, the automixer — must see one
transmitter, not a blend. The **low-cut** sits deliberately AFTER the analysis recorder, so "record
all inputs" stays an unprocessed capture and the offline tools never measure our own filter.

**Output bus:**
```
MixingSampleProvider (sums routed channels) → peak tap → [optional recorder tap] → volume → WasapiOut
```

**Key facts:**
- Internal mix format: **48 kHz, stereo, float32**. All captures resample to this.
- Each output bus runs its own WasapiOut at the device's native rate; the bus resamples once on the
  way out.
- Inputs and outputs run on independent clocks. Per-channel ring buffers absorb drift; we accept
  `DiscardOnBufferOverflow` semantics. If drift becomes audible, consider a small async resampler
  per channel.
- WASAPI **shared mode** everywhere — exclusive mode would lock Zoom out of the headset.
- Delay range 0–1000 ms: read offset into a ring buffer sized for max delay + headroom (~1500 ms).
- Meters update at ~30 Hz from peak values latched in the audio thread and polled by a UI timer (do
  NOT marshal per-buffer).
- Output **Volume** (`OutputBus.Volume` → `VolumeSampleProvider`) is applied *after* the
  peak/recorder tap — a device trim that does NOT affect meters or recordings. Recording is **per
  output** (each bus owns a `MixRecorder`).

### Automixer

The fix for multiple distant mics summing the same voice (comb "echo", noise floor, reverb): per
output, attenuate every mic except the one closest to the active talker. Static delay compensation
is NOT a substitute — per-talker offset isn't fixed.

`AudioEngine` runs a ~100 Hz `Timer` (`AutoMixTick`) that reads each channel's latched
`CurrentLevelLinear` (RMS), smooths it (attack 8 ms / release 250 ms → `AutoMixer._env`), picks a
leader per output over the channels routed there, and writes per-channel gains lock-free (volatile).
`InputChannel` applies them at the routing-push step with an intra-buffer ramp (no zipper). All
decision logic is off the audio threads.

**Modes** (`AutoMixMode`, per output):

| Mode | Gain rule | Use when |
| --- | --- | --- |
| Off | unity | — |
| Share | Dugan-style gain-share `(score/max)^p`, non-leaders attenuated but never muted | conversational back-and-forth — gradual hand-off, no swallowed syllables |
| Gate | winner-take-all (hard mute of non-leaders) | single presenter; turn-taking where Share's summing combs |

Gate's ~200 ms hold can clip the first syllable of a fast interjection. Share's strength slider only
*attenuates* non-leaders — it can never remove comb echo, so Gate is the answer when several mics
hear one voice.

**Leader hold.** The selected leader is held with hysteresis (`HandoffHoldTicks` ~200 ms,
`HandoffHysteresis` ~3 dB) so a brief louder moment elsewhere can't steal it. Gate always uses the
held leader; **Share** uses it when **Stable hand-off** is on (`OutputViewModel.StableHandoff`,
default on, persisted) and anchors its gain-share to the leader's level rather than the
instantaneous max — off = legacy instantaneous-loudest. This hold is the actual fix for "far mic
wins"; see finding 1.

**Selection rules** — precedence in `Tick`: `selMode` = correlation if `useCorr`, else natural if
`useNatural`, else level. `Beats(selMode, …)` applies the matching margin.

1. **Level** (default). Smoothed RMS argmax, multiplicative `HandoffHysteresis` margin.
2. **Match lapel** (`OutputViewModel.ReferenceGuided`, default off, persisted). Picks the room mic
   whose loudness envelope best correlates with the active priority/lapel mic — the lapel is a clean
   reference for the talker's voice, so the room mic tracking it most faithfully is the least
   reverberant/contaminated. `AutoMixer` keeps a 2 s per-channel envelope ring (`_envHist`) and every
   ~50 ms recomputes a best-lag (±600 ms) normalized cross-correlation vs the reference over speech
   frames (`LaggedCorr` → smoothed `_corr`); the held-leader test uses `_corr` with an **additive**
   `CorrHysteresis`. Engages only while a priority mic is *speaking* and `_corr > CorrReady`;
   otherwise falls back to level. The reference is global (`_refIndex` = loudest active priority
   mic), so it works even on an output the lapel isn't routed to. See finding 2.
3. **Prefer natural** (`OutputViewModel.PreferNatural`, default off, persisted; lower precedence than
   Match lapel). Reference-free, for the no-lapel case. Among mics within `NaturalFloorRatio` (−8 dB)
   of the loudest, picks the lowest **spectral-flux instability** (`InputChannel.CurrentFluxCv`).
   Held-leader margin is **multiplicative** (`NaturalHystRatio` 0.85 — challenger must be ≥15% lower
   CV); an early *additive* 0.05 margin was ≈ zero hysteresis and chopped near-equal mics. See
   finding 3. Behavioural caveat: this rule picks the globally lowest-CV mic, so with talkers spread
   around the room it pins one mic regardless of who is speaking — flux-CV is good at *vetoing* a
   bad mic, poor at *picking* among good ones.

`InputChannel.CurrentFluxCv` is a 512-pt FFT per voiced 512-sample window **accumulated across
capture buffers** in the audio thread (EMA mean/variance of normalized-spectrum frame-to-frame
distance → coefficient of variation; lower = more natural). The accumulation is load-bearing:
WASAPI shared-mode delivers ~480-frame buffers (<512), so the original per-buffer FFT
(`if (frames < FluxN) return;`) almost never ran and the value **froze at a startup estimate**;
fixed 2026-07-26 (`ComputeFlux` accumulates → `ComputeFluxWindow` runs the FFT, `FluxEma` retuned
0.03→0.01 for the ~94 windows/s rate, `_fluxFill` reset in `Stop`). Live scale is now ~0.35–0.5 and
**matches** the offline Python `flux_cv` (~0.4–0.6), so offline replays are faithful.

**Priority mics** (`IsPriority`, per-input gear popup). A priority mic (the presenter's lapel) is
always full level and out of the competition, and while *active* (`PriorityActiveRms`, ~−40 dBFS) it
ducks the room mics — otherwise that voice reaches the bus via both the clean lapel and a delayed
room mic and comb-filters. Multiple priority mics are intentionally allowed (pastor + worship
leader) — do NOT restrict to one; note they don't duck *each other*, so two priority mics hearing
one source will double. **Hazard:** an unused-but-open priority lapel that crosses −40 dBFS (bumped,
drift) silently ducks every room mic off the stream. Unroute/clear the flag when not in use.

**Priority hangover** (`PriorityHoldTicks` ~1.2 s, `PriorityBreakInRms` ~−50 dBFS). The duck used to
be recomputed bare each tick, while the *leader* had both a hold and hysteresis — so a presenter's
sentence gap released it and Gate handed the bus to a room mic. The envelope release (250 ms) needs
~575 ms to fall from speech to `PriorityActiveRms`, which an ordinary pause exceeds. Measured live
2026-08-30 on the headset bus: **13 hand-offs in 40 s** (median 250 ms, max 0.89 s), every one with
the lapel envelope just under −40 dBFS, to an S500 sitting on the presenter's own table (−28 dBFS in
his pauses vs −17 while he spoke — no AGC pumping, every mic fell together). After the fix, 0 in 40 s
with the duck verifiably held (gain 0.00 through every pause). The hold is **broken immediately** by
a non-priority mic above `PriorityBreakInRms`, because at strength 100% `pduck` is a hard mute and a
blind hold would swallow an audience interjection. Two caveats: (a) break-in can only separate a real
talker from the presenter's residual when **no room mic sits near the presenter** — one on his table
reads −28 dBFS, louder than a genuine interjection across the room (−43); (b) margin is thin —
measured residual on a 15 ft mic peaked at −53.8 dBFS, only ~4 dB under the threshold, and that is
also *above* `SilenceFloorRms`, so the silence floor alone would not have prevented the hand-off.

**Split receivers.** A two-transmitter wireless receiver (RØDE Wireless PRO in Split mode) is ONE
WASAPI endpoint carrying TX1 on the left and TX2 on the right. Bound whole it reaches the bus
hard-panned and the automixer sees a single blended channel it cannot arbitrate. Bind it to two
strips instead, one `ChannelSource.Left` and one `Right`. Device pickers are therefore exclusive per
**side**, not per endpoint (`DeviceResolver.Claim`/`IsFree`, `MainViewModel.RefreshExclusiveChannels`)
and picking a half-claimed endpoint auto-takes the free side. A Stereo claim still takes the endpoint
whole and keeps the bare device id as its `used` key, so pre-split presets resolve unchanged.

**Quality-weighted Share** (`SelWeight`). In correlation/natural mode each mic's level is scaled by
its quality (CV for natural, corr for lapel) *before* the gain-share, so a loud-but-bad mic ducks
even when louder than a quieter, cleaner leader. Without this, Share anchors to the leader's level
and clamps every louder mic to unity, leaving a scratchy near mic wide open. Level mode is unchanged
(weight ≡ 1). **STALE CONSTANTS:** the natural branch maps CV through `NatCvGood = 1.0` /
`NatCvBad = 2.5`, tuned to the *pre-fix inflated* CV scale (1.3–2.6). Post-fix CV (~0.35–0.5) sits
below `NatCvGood`, so `t` clamps to 0 and every mic weighs 1.0 — **quality-weighted Share is
currently inert in natural mode.** Retune only against a labeled offline replay (see "Validating a
selector change"); the *selection* margins are unaffected because they're multiplicative.

**Diagnostic surface.** `InputChannel.IsDucking` (any routed output's gain < 0.85) drives a per-input
amber LED; `InputChannel.IsAutoMixActive` (leader on any routed output, from `AutoMixer._activeInput`)
drives a green LED — both polled on the meter timer. The crest-derived `InputChannel.Clarity` (0..1,
NaN when idle) shows as a "Mic clarity" bar in the gear popup — **readout only, not used for
selection** (crest failed as a proximity cue; see finding 1). `AudioEngine.AutoMixActiveInput(o)`
exposes the per-output winner and `MainViewModel.LogAutoMixSelectionChanges` writes each hand-off to
`AudioLog`.

## Testing without a room full of people

The app used to be unexercisable without a live congregation, which blocked all UI work. It isn't now:

- **Replay** (`--replay[=STAMP] --seek=MM:SS --for=MM:SS --speed=N --loop`) feeds the inputs from a
  recorded session's `diag-input*.wav` files. Capture sits behind NAudio's `IWaveIn`, so everything
  downstream — gain, delay, flux-CV, RF tallies, automixer, meters, LEDs, scenes — runs unmodified.
  Two things are load-bearing: the rig emits **480-frame** buffers (WASAPI shared mode's size; at 512+
  the cross-buffer flux accumulation is bypassed and you test different code), and **one clock pumps
  every source in lockstep** (independent timers drift and change which mic wins).
- **The rig drives the automix tick** (`ReplayRig.Pumped` → one tick per chunk) instead of the
  wall-clock timer. This makes replay deterministic *and* speed-independent — before it, `--speed 2`
  halved every hold because the automixer saw half as many ticks per second of audio.
- `--replay` is a **sandbox**: its own single-instance mutex (so it runs alongside a live session),
  **no preset autosave**, and **no output devices** by default (two instances both opening CABLE Input
  would double audio into Zoom).
- **Golden baselines**: `tools/replay-baseline.ps1 -Name <fixture> ... [-Update]`, baselines in
  `tools/baselines/`. Compares aggregates (mode, hand-off count, occupancy, median flux-cv) — hand-off
  count is exactly reproducible and is the sensitive signal. Record and check at the **same `-Speed`,
  1–2**; higher saturates the process and starts dropping audio. The script passes `--advanced`
  explicitly so a fixture keeps the window its goldens were recorded under even though the app now
  defaults to Simple — a fixture must never inherit a UI change as a change in CPU load.
- **Binding errors**: WPF resolves binding paths at runtime and swallows failures, so a clean build
  proves nothing about the UI. `--log` enables `BindingErrorListener`, which logs them.
  `--open-all` opens every window so one run covers all their markup.
- **Unit tests** (`AudioMixer.Tests`) cover only pure logic — scene rules, health rules, the autosave
  allowlist invariant. Anything needing a device or a window is verified by a replay run instead.
- `--scene=NAME` applies a scene at startup, so the whole scene path is assertable from `/state`.

## Conventions

- **Naming**: PascalCase for types/methods, _camelCase for private fields, camelCase for
  locals/params.
- **Async**: Engine start/stop is async (device init can block). Audio callbacks are NOT async.
- **Threading**: NAudio callbacks run on its own threads. Never touch WPF UI objects from a callback
  — use `Dispatcher.BeginInvoke` or (preferred) a UI timer that polls atomic state.
- **No comments explaining what code does.** Only comment non-obvious WHY (e.g. "WASAPI shared mode
  picks device default rate — must resample before mixing").
- **Line width**: wrap this file and long comments at ~100 columns.
- **Logging**: `System.Diagnostics.Trace` for engine events; user-facing errors go to the status bar
  via MainViewModel. File logging (`AudioLog` → `%TEMP%\AudioMixer.log`) is **opt-in** — the
  `AUDIOMIXER_LOG` env var or the `--log` CLI flag (so a desktop shortcut can enable it). The meter
  loop writes ~1 line/sec, so we don't grow a file on every run. First line is a banner with exe
  path, assembly version (`1.0.0+<git-sha>`, stamped by an MSBuild target) and build time — identify
  *which build* produced a log from the log alone; don't cross-reference DLL mtimes.
- **RF-link health** rides on the per-input log line: `rf=[lvl=<voiced mean dB> voiced=<%>
  silent=<%> drops=<n>]` (`InputChannel.SnapshotRfStats`, lock-free counters latched in the audio
  callback), for **offline** diagnosis of a marginal Soundsync link. A dropping link shows
  exact-silence gaps mid-speech (voiced→silent "drop edges") + high `fluxCv` while `voiced%` is high;
  a healthy-but-far mic is quiet-and-smooth. Only assess a mic while it's *voiced*. Raw counts only,
  no thresholds in-app — classify after the session.
- **Diagnostic state endpoint**: `StateServer` serves a live JSON snapshot at
  `http://127.0.0.1:<port>/state` — channels (levels/routes/mute/gains/clarity/`refCorr`/`fluxCv`),
  outputs (mode/strength/stable/reference/preferNatural + winner), plus `referenceInput`. **Opt-in**
  via `AUDIOMIXER_STATE` (port number, default 7077) or `--state[=PORT]`. Read-only, loopback only;
  `MainViewModel.BuildStateJson` marshals to the UI thread. Fastest way to watch the automixer's
  *reasoning* (env vs corr vs cv vs the selected leader) without the GUI.
- **Single instance**: `App.xaml.cs` holds a named mutex — a second launch signals the first (raises
  its window) and exits, so two instances never fight over the same WASAPI capture devices.

## Build & run

```powershell
dotnet restore
dotnet build
dotnet run --project AudioMixer
```

## External dependencies (user installs manually)

- **VB-CABLE** (https://vb-audio.com/Cable/) — virtual audio cable. After install + reboot, "CABLE
  Input" appears as a render device (mixer outputs to it) and Zoom selects "CABLE Output" as its
  microphone.

## Measured findings — dead ends, don't re-litigate

These cost multiple sessions with real hardware and labeled recordings. Each one is a *negative*
result you cannot infer from the code. Before proposing a new mic-quality metric, read all three.

**1. Speakerphone DSP destroys every proximity cue except gross level.** Measured on 4× Anker S500
with `tools/AnalyzeInputs`: crest factor, spectral flatness, HF-energy ratio, spectral centroid and
SNR all FAIL to rank the closest mic — noise suppression even adds HF hiss to *distant* mics
(inverting HF/centroid), and gating zeroes the noise floor (making SNR a level proxy). Only
**smoothed level** survives: ~5–6 dB of proximity remains after AGC, enough to pick the closest mic
~18/18 on averages. The original "far mic wins" bug was **temporal, not metric** — Share re-picked
the instantaneous-loudest mic every 10 ms with no hold, so a distant mic's AGC make-up gain during a
talker's pause stole the selection (offline replay: 113 flips). Crest weighting, added to fix it,
made it worse (136 flips). Hold + hysteresis on the level leader fixed it (≈23 flips). Lesson:
stabilize the level selection; don't trust spectral/crest features through a speakerphone's DSP.

**2. Loudest ≠ best-sounding, and the mic's own signal can't tell you — use the lapel as reference.**
A room mic can read *louder* than another yet sound clearly worse (AGC make-up gain, desk
coupling/proximity boom, a nearby vent or PA), so loudest-wins picks the bad mic. Validated offline
with `tools/RefCorr` on a labeled capture (operator confirmed In4 good / In5 loud-but-bad): level
ranked In5 > In4 (picks bad); refSNR also failed (gating zeroes the noise floor, so it favored a
distant quiet mic); **envelope-correlation-to-lapel ranked In4 (0.774) > In5 (0.706)** — the bad mic
is loudest yet correlates *worst*, its envelope smeared by reverb/noise. Shipped as "Match lapel".
Caveats from the data: only **rejecting the loud-bad mic** is reliable — among several good mics the
margins are noise (In2 0.778 ≈ In4 0.774) — and it needs an active lapel.

**3. "Natural/scratchy" is NOT measurable by cleanliness metrics — measure temporal INSTABILITY.**
A mic can be loud AND clean-by-the-numbers yet sound scratchy, because the noise suppression
**over-processes**. On the labeled capture the bad mic (In5) scored HNR 13.2 / **CPPS 11.3, higher
than the clean lapel** (8.6), with lower jitter/shimmer than the good mic — so HNR/CPPS/jitter/
shimmer all rank the bad mic *cleanest* (inverted; `tools/voice_quality.py` reproduces this). What
sounds scratchy is **intermittent**: gating chatter, musical noise, broadband transient clicks
(vertical streaks in a spectrogram) — an unstable spectrum over time. The discriminator that works is
**spectral-flux coefficient-of-variation** (`flux_cv`, `tools/naturalness.py`): natural mics and the
lapel ~0.41, the scratchy mic ~0.52–0.65, consistent across recordings. Offline replay
(`tools/replay_natural.py`) flips selection from the bad mic (74%/59% of voiced time) to the good mic
(64%/58%) on both sessions. Caveats: validated on 2 recordings, one room, one set of Ankers;
flux-CV also penalizes distant/reverberant mics (hence the level floor) and, per the behavioural
caveat above, vetoes better than it picks. A high flux-CV can also mean **RF dropouts**, not a bad
capsule — check range before blaming the mic.

**4. The Ankers gate congregational singing to digital silence — TOGETHER — so no mix strategy can
fix worship audio.** Measured 2026-08-09 on the live capture (`scratchpad/gate_check.py`, 20 ms
frames, "silence" = peak < 1e-5). During singing each unit sat in **true digital silence 13–21% of
frames**; all four were silent **simultaneously 4.6%** of frames — **51× more often than statistical
independence predicts** (0.1%). Over 170 s that is **71 total-stream dropouts, one every 2.4 s**,
median 60 ms, max 780 ms, 22 of them >100 ms. Operator verdict, unprompted: "interrupted constantly,
can't follow it at all." The gates are *correlated* because every unit hears the same acoustic signal
and its noise suppression reaches the same "this is noise" verdict at the same instant. Consequences:
(a) **summing more mics cannot fill the holes** — the holes are in every source at once (confirmed
live: switching Automix to Off changed nothing); (b) the mic-count question for singing is the **wrong
variable** — single-mic and multi-mic fail identically; (c) the same mechanism nibbles at *speech*
(341× simultaneity pre-service) but is invisible there because gate closures land in the natural pauses
between words. Only fixes are upstream of the mixer: the S500's **Broadcast pickup mode** (untested —
"restores original sounds by turning the speaker off", the only DSP-adjacent control Anker exposes; no
noise-reduction or EQ toggle exists), the DSP-free Rode lapel, or a board feed. Do NOT attempt another
selector/mix-topology fix for singing. Corollary for diagnostics: `winner = -1` has **three** causes
(automix Off, priority-active, silent-room) — disambiguate by the logged `gains=[…]` (priority duck
writes 0 at strength 100%; silent-room writes 1.0) before blaming a priority mic.

**5. A DSP-free lapel does not gate at all — but its noise floor is NOT recoverable by filtering.**
Measured 2026-08-23 on one capture of the same room and speech (`tools/gate_rate.py`, `compare_mics`/
`hp_eval` in scratchpad). A RØDE Wireless PRO into the Realtek 3.5 mm jack vs two live Ankers:
digital-silence **0.0%** (zero gate closures in 9 min) vs 4.1% / 7.1%; the Ankers lost **21.6 s** and
**36.9 s** of audio to 174 and 375 gate holes (68 / 106 of them >100 ms, max ~700 ms) and still closed
in unison (~8× independence). Flux-CV 0.317 vs 0.399 / 0.500 and hf_burst 1.06 vs 1.89 / 2.05, so the
lapel is also the most natural mic by the metric of finding 3 — and Anker #3 beats #4 on every
artifact column, which matches the operator's ear. This is the first thing measured on this rig that
actually attacks finding 4. **The negative half:** the Rode's speech-band S/N is 15.3 dB vs 28–33 dB
for the Ankers, and a high-pass does NOT close that gap — it was tempting to assume it would, since
89% of the Rode's floor energy sits below 1 kHz. Measured per cutoff (Butterworth Q=0.707, the same
biquad the app ships): 60/80/100/120/150 Hz cut sub-100 Hz rumble by 0.9/2.1/3.7/5.4/8.2 dB but move
100 Hz–8 kHz S/N by only **+0.1–0.2 dB**, because the floor's bulk is at 80–200 Hz and 200 Hz–1 kHz,
inside the voice. Run the low-cut at 80–100 Hz for rumble, handling and headroom — never as an S/N
fix. Note the Ankers' *better* S/N is itself an artifact (their gate zeroes the floor, so gating more
scores better — finding 1). No mains hum on the 3.5 mm path (≤2 dB at 50/60/100/120 Hz), so no ground
loop and no notch is warranted.

**5b. That noise floor is ACOUSTIC, not the aux path — USB will not fix it.** The obvious diagnosis
(cheap Realtek input, unbalanced cable, an extra D/A→A/D round trip through the RX's 3.5 mm output)
is wrong here, and it is worth not re-deriving. The floor's own spectrum settles it: **83% of its
energy is below 1 kHz and only 5.3% is above 4 kHz** (20-80 Hz 8.9%, 80-200 44.7%, 200-1k 29.3%,
1-4k 11.0%, 4-12k 4.7%, 12-24k 0.6%). Converter/preamp noise is *hiss* — roughly flat energy per
unit bandwidth, so it dominates the upper bands, and there is almost nothing up there; mains hum was
separately ruled out (≤2 dB at 50/60/100/120 Hz). That low-frequency signature is the room: HVAC,
air handling, structure-borne rumble into the capsule. The S500s were hiding it with the very
suppression we removed. Consequences: (a) prefer the RX's **USB-C** endpoint anyway — it drops two
conversions, removes any hidden Realtek boost/AGC, and scales to several receivers where aux jacks
do not — but expect a few dB, not fifteen; (b) the real lever is **proximity**, since the room floor
is constant and every halving of mic-to-mouth distance is +6 dB of signal. To separate the two
empirically: record the aux input with the transmitters **powered off** (pure electrical floor) and
again with them **on in a quiet room** (electrical + acoustic) — the gap is what the aux path costs.

**6. On a homogeneous DSP-free rig, level selection gets BETTER and flux-CV stops discriminating.**
Findings 1-3 are all consequences of speakerphone DSP; remove it and their conclusions move. With N
identical Rode transmitters: (a) **level becomes a true proximity cue** rather than a survivor of
AGC — identical capsules mean a level difference is distance, not device variation, so Gate/Share on
smoothed level with **stable hand-off** is the right selector and the hysteresis that fixed "far mic
wins" is still exactly as necessary (a talker's pauses still let a neighbour momentarily win);
(b) **Prefer natural should be OFF** — flux-CV measures *over-processing artifacts*, and with no DSP
anywhere every mic reads ~0.29-0.33, so the metric has nothing to separate and its documented
behavioural flaw (it pins the globally lowest-CV mic regardless of who is speaking) is all that is
left. Observed live 2026-08-23: with two Rodes and two Ankers, prefer-natural hard-gated the *only*
room mic hearing the talker because the Rodes scored cleaner. Flux-CV keeps **diagnostic** value —
it still rises on RF dropouts — but not selection value. (c) **Match lapel** stays off for prayer:
it engages only while a priority lapel is *speaking*, which is never the case when the room is.

**Validating a selector change.** Never tune the live selector from a live impression. Capture
"record all inputs" during a real session *with operator labels* of which mic sounded better when,
then replay offline (`tools/AnalyzeInputs`, `tools/RefCorr`, `tools/replay_natural.py`,
`tools/naturalness.py`) before touching `AutoMixer`. Judge mic quality over a longer listen with the
real speaker — short A/B impressions have disagreed with both the metric and the operator's own
later judgment.

## Known gotchas

*(grows over time — see Self-maintenance protocol below)*

### Devices, WASAPI & RF

- WASAPI device IDs are stable across reboots **for fixed devices** (onboard/virtual — Realtek,
  VB-CABLE); persist those in presets. They are **NOT** stable for hot-plug USB audio.
- **Hot-plug USB audio (mics, USB headsets, wireless dongles) gets a NEW WASAPI endpoint GUID when it
  re-enumerates** (a Windows-Update driver reboot, a replug, a different port). A preset matching only
  on `DeviceId` then silently drops every such device on load. Root cause here: the Soundsync dongles
  expose **no USB serial** (`USB\VID_291A&PID_3523&MI_01\7&<hash>&0` — the `7&hash&0` is a
  *port-derived* instance), so their identity is the USB port and the endpoint GUID regenerates.
  Windows *does* re-apply a user's device **rename** to the new endpoint (keyed to the port-path
  instance, confirmed across a driver-update reboot), so the friendly name is the stable key.
  Fix: `MainViewModel.ApplyPreset` (`ResolveDevice`) matches `DeviceId` first, then falls back to the
  saved friendly name normalized by `DeviceNameKey`, which strips **only** the volatile `(N- …)`
  enumerator (`EnumeratorPrefix` regex). Two hard-won details: (1) do **NOT** truncate to the prefix
  before `" ("` — an un-renamed device's identity is the *interface* name inside the parens (`Speakers
  (Lync USB Headset)` vs `Speakers (Realtek(R) Audio)` share the prefix `Speakers`), so truncating
  mis-binds the headset to onboard speakers; (2) resolve against the **master** `_allInputDevices` /
  `_allOutputDevices`, not a channel's `AvailableDevices`, which is dedup-filtered and can be missing a
  device mid-apply. A `used` set prevents two channels grabbing the same device. On resolve, autosave
  rewrites the current GUID — the preset **self-heals** after one launch. NOTE: the Ankers are **not**
  interchangeable — each unit covers a room area next to its own dongle, so the operator renamed them
  `ANKER #1..4` in Windows Sound settings to match physical labels. Match by that name; never
  greedy-fill in arbitrary order. A deliberate *rename* (`ANKER 4`→`ANKER #4`) correctly won't match
  the old preset — one manual remap, then it re-saves.
- **An Anker S500 can hold its Soundsync dongle link AND a Bluetooth link simultaneously** (designed
  bridging feature). So a mic feeds the mixer fine over its dongle while *also* transmitting on BT — a
  self-contending extra 2.4 GHz radio that garbles the weakest dongle input. Adaptive hopping (BT AFH +
  proprietary dongle) reduces but doesn't eliminate it. Fix: "Forget" every `Anker PowerConf S500` BT
  pairing (they auto-reconnect otherwise) so units run dongle-only; safe, because the mixer binds
  Soundsync endpoints, not the BT (`…PowerConf S500`/Hands-Free) ones. Detect with
  `tools/audio-device-diag.ps1` — it dedupes BT devices **by radio address** (identical units share a
  FriendlyName, so `Sort -Unique` on name under-counts how many are live on BT).
- **A chronically "bad" mic is usually out of RF range, not defective.** The furthest unit (~50 ft) sits
  past the reliable range of the 2.4 GHz Soundsync link: an isolated walk test showed its flux-CV
  *tracked position* — 0.58–0.68 with 7–12% transient spikes (packet loss) at far spots but **0.37 with
  0% glitches up close** — plus ~5–6 dB signal loss at range. A defective capsule would be uniformly
  bad; a gradient means distance/RF. Fixes in order: powered USB extension to move the dongle closer,
  dongle height/line-of-sight, BT off, don't seat a mic beyond ~30 ft of a dongle.
- Windows endpoint prefixes ("2-/3-/5-/6- Anker Soundsync") **shuffle on unplug/replug**, so "shows in
  Windows" ≠ the endpoint the mixer needs is live. A dongle can also keep its **render** endpoint alive
  while the **capture** path is down (the "half-link") — re-pair the dongle.
- **A split two-transmitter receiver is one endpoint feeding two strips, so device exclusivity is
  per-side.** `DeviceResolver.Claim(id, side)` keys a whole-endpoint claim on the bare id and a side
  claim on `id|1`/`id|2`; `IsFree` makes Stereo conflict with either side. Get this wrong in either
  direction and it fails silently — too strict and the second transmitter vanishes from the preset on
  load, too loose and two strips push the same audio onto the bus twice.
- **GainAssist on a Wireless PRO transmitter is AGC, and it breaks the automixer.** It normalises
  level, which is the one selection cue that survives on this rig (finding 1) and the *only* one on a
  DSP-free rig (finding 6) — leave it on and a distant mic's auto-gain pulls it level with the near
  mic, re-creating the exact "far mic wins" failure the S500s caused. Turn it **off per transmitter**:
  long-press the Left Navigation button until AUTO/DYNAMIC is replaced by a dB level, then set gain
  manually. Modes are Auto and Dynamic; neither is safe for automixing.
- **The capture chain takes only channels 0 and 1 of an endpoint** (`BuildConversionChain`'s >2-channel
  branch maps a multichannel device down to the first two, verified by probe: an 8-channel input
  yields `0,1,0,1,…`). So a multi-input USB interface silently drops everything past its second input
  — no error anywhere. Two-channel devices (one split receiver each) are unaffected; widening
  `ChannelSource` beyond Stereo/Left/Right is what a >2-in interface would need.
- **On a RØDE Wireless PRO, "Split" only means TX1→L / TX2→R while the RX's 3.5 mm jack is an
  OUTPUT.** Plug a mic into it as an RX Mic and the routing silently changes meaning: both
  transmitters merge onto the **left** and the RX Mic takes the **right**. Change modes with a
  long-press of both Nav buttons (short-press Left cycles, Right selects), or in RODE Central. Do not
  confuse Split with **Safety**, which puts a −10 dB duplicate of the same mix on channel 2 — it looks
  like a split on a meter and carries no second mic.
- Some Bluetooth headsets switch to HSP/HFP when used as input and output simultaneously, dropping
  quality. Workaround: BT input, wired output. (Moot on this rig — Ankers run dongle-only.)

### Audio graph & NAudio

- **NAudio 2.2.1 `MixingSampleProvider.ReadFully=true` only controls output padding — NOT source
  retention.** When any source returns less than the requested count, MSP unconditionally
  `RemoveAt(index)`'s it — gone forever. To prevent eviction the source must always return the full
  count: set `ReadFully=true` on the underlying `BufferedWaveProvider` so it pads with zeros. Symptom:
  audio works until the first buffer-empty event (e.g. route toggle off then on), then the output is
  permanently silent until the OutputBus restarts.
- The per-output `BufferedWaveProvider` (`InputChannel._outBuffers`) sets the **hard cap on end-to-end
  latency**. Sized generously (e.g. 2 s) with input pushing before output pulls, that backlog becomes
  audible latency. Keep it small (~200 ms) AND clear it when (re)starting an output
  (`AudioEngine.RestartOutputBus_NoLock` → `ClearOutputBuffer`). Symptom: "hello" arrives 1–2 s late.
- NAudio's property is `DiscardOnBufferOverflow` (not `DiscardOnBufferFull` — that name doesn't exist
  in 2.2.1 despite older docs).
- `WaveFileWriter` is NOT thread-safe; serialize Write calls with a lock or write from one tap thread.
- **A stalled input capture freezes its VU meter at the last value** (looks ~80% "active" but passes no
  audio). `PeakMeter` has no decay — `CurrentDb` only changes inside `Observe()`, called from
  `OnDataAvailable`. If `WasapiCapture` stops firing `DataAvailable` (USB renegotiation, device drop),
  the meter and `_currentLevelLinear` freeze and the channel is silently dead. Fixes: (1)
  `InputChannel.Stop()` calls `PeakMeter.Reset()`; (2) `AudioEngine` runs a **capture-stall watchdog**
  (`WatchdogTick`, 500 ms) — a selected input whose `LastDataTicks` is stale >1.5 s is restarted on a
  background task (`RestartBackoffMs`, `MaxRestartAttempts`, then `InputRestartGaveUp`); (3) the Resync
  button calls `RestartInputs()` too (it used to restart only output buses, so it couldn't recover
  this). Shared-mode WASAPI delivers buffers even during silence, so "no DataAvailable" is an
  unambiguous stall signal — a silent-but-alive mic won't false-trigger.

### Measurement & recording

- **Automix gain is applied AFTER the meter/analysis taps** (`InputPeak`/`PostPeak`/analysis recorder
  all run before the per-output routing push). So VU meters and clap-test recordings show the
  *pre-automix* post-fader level — a channel can read hot while the automixer ducks its contribution.
  Intentional (the meter shows what the channel produces); don't "fix" it by moving the tap.
- **The route-to-output clap test does NOT measure device latency.** A channel's position in the mixed
  output is `transport_latency + standing backlog in its per-output BufferedWaveProvider`. That backlog
  is set nondeterministically at startup (a fast device accumulates a *larger* backlog before the bus
  drains it) and anti-correlates with transport latency, so the ordering scrambles — a low-latency
  built-in mic can look *more* delayed than a Bluetooth one. Use "Detect Delays" (`DelayAnalyzer`),
  which taps the per-channel analysis recorder *before* the output buffer. Re-measure after any output
  restart.
- **`DelayAnalyzer` cross-correlates onset envelopes, NOT a peak threshold.** A "first sample ≥ 50% of
  file peak" detector mislocates soft/vocal onsets: a spoken "T!" (used because the Ankers' noise
  suppression gates real claps) has its global peak in the *vowel*, so the detector skips the leading
  `[t]` on a clean mic (→ looks late) while a suppressed mic keeps only the `[t]` (→ looks early),
  inverting the ranking. Fix: half-wave-rectified first-difference of a 1 ms RMS envelope, normalized
  cross-correlation vs the loudest channel over ±1000 ms; the normalized peak is the confidence (warn
  below 0.5). Caveat: a speakerphone that *gates* transients may have no constant latency, so no single
  delay value fully syncs it.
- **A WAV being actively recorded reads 0 bytes / a frozen mtime in directory listings.** NTFS doesn't
  flush the directory-entry size + last-write-time during a long buffered write, and `WaveFileWriter`
  only finalizes the RIFF header on Dispose. So Explorer/`Get-ChildItem` show a live capture as 0 bytes
  with the mtime stuck at creation — it's fine. Don't judge a live capture by the folder view and don't
  stop/restart it in a panic (that's the only thing that *would* lose buffered data). True length
  mid-write: `[System.IO.File]::Open(path,'Open','Read','ReadWrite').Length`. Offline tools
  (`soundfile`) can't read it until stopped (header still claims 0 frames) — to analyze mid-session,
  parse the chunks and read raw float32 from the `data` offset to true EOF (`tools/live_wav.py`).

### UI / WPF

- **The meter tick and the autosave debounce share one `PropertyChanged` stream, so filtering it with
  a blocklist silently disables autosave.** `ChannelViewModel.RefreshMeters` raises ~13 display
  properties 30x/second and `MainViewModel.OnSettingChanged` restarts a 500 ms debounce timer on any
  property it doesn't recognise — so the four peak properties that were excluded weren't enough
  (`IsDucking`, `IsAutoMixActive`, the per-bus LED state, clarity) and the timer was reset every
  33 ms and could never elapse. Symptom: settings persist across a *clean exit* (Dispose still calls
  `SavePreset`) but a crash or a killed process loses the whole session. Fix: `OnSettingChanged`
  matches an **allowlist** (`PersistedProperties`) mirroring exactly what `PresetMapper` writes. Keep
  it that way — a new display property must never be able to break saving by omission.

- **A WPF trigger's `Value` is parsed as a STRING, so comparing it against a boolean binding is
  unreliable** — the trigger silently never fires and every button renders unselected with no error
  anywhere. Bind selection state to `Tag` as an `"on"`/`"off"` **string** and use a `DataTrigger` on
  `{Binding Tag, RelativeSource={RelativeSource Self}}` (see `Views/SimpleWindow.xaml`, and the
  `…State` string properties on `SceneController` that exist only for this).

- **Scene and alert *rules* live in pure functions** (`Services/SceneTransform`, `Services/HealthMonitor`)
  that take and return plain records, with the view models only marshalling values in and out. Scenes
  rewrite every channel and output at once and a wrong rule drops the congregation off the stream
  silently; alert rules fire in situations nobody can stage on demand. Keep new rules in the pure
  layer so they stay unit-testable — do NOT put judgement in the view models.

- **Input strips live in a `UniformGrid Rows="1"`, which divides the column equally and IGNORES each
  child's `MinWidth`.** A fixed-width window crams N strips into whatever space exists and clips the
  right-most controls (A/B route toggles vanish first). Fix: the window is non-resizable and its width
  is computed from input count (`MainViewModel.WindowWidth = max(500, count*96 + 160)`), applied in
  `MainWindow` code-behind. Don't bind `Window.Width` in XAML — `DataContext` is set *after*
  `InitializeComponent`, so the binding isn't reliably applied at startup and it falls back to the
  literal. Set `Width` in code-behind after assigning `DataContext` and on `WindowWidth`
  PropertyChanged. `WindowHeight` follows the same pattern (base 320 px + the VB-CABLE banner when
  `ShowVbCablePrompt`). Also: outputs live in a fixed-width column (150 px), NOT `Auto` — an `Auto`
  column lets device-name buttons expand to their full untrimmed text and blows out the layout.
- WPF's temporary XAML-compilation project (`*_wpftmp.csproj`) does not reliably honor
  `ImplicitUsings` for `System.IO` — add an explicit `using System.IO;` in any file using
  `Path`/`Directory`/`File`.

## Self-maintenance protocol

**This file is intended to be self-optimizing. Claude should update it as the project evolves.**

Its value is knowledge that **cannot be recovered by reading the repo**: measurements on real
hardware, negative results, device behavior, and decisions with their *why*. Code structure is
cheap to rediscover with a search — don't spend this file describing it. When in doubt, ask: "would
a session that greps the code learn this in 30 seconds?" If yes, leave it out.

Update CLAUDE.md **in the same change** whenever you:

1. **Discover a non-obvious gotcha** — a bug that took >15 min to track down, a WASAPI/NAudio quirk,
   device-specific behavior. Add to "Known gotchas" under the right sub-heading: symptom → cause →
   fix.
2. **Prove something doesn't work** — a metric that inverts, an approach that made it worse. Add to
   "Measured findings" with the numbers and the tool that produced them. These are the highest-value
   entries here; a dead end you don't record gets retried.
3. **Change the audio architecture** — add/remove a pipeline stage, change the mix format, change a
   selection rule. Update "Audio architecture".
4. **Add/rename a top-level folder or file role** — update "Project layout".
5. **Add an external dependency** — update "Stack" or "External dependencies".
6. **Establish a new convention** — update "Conventions" and apply it to existing code.

**What NOT to add here:**
- Per-task progress, in-flight TODOs, or PR descriptions (tasks/commits), or planned work (ROADMAP).
- Restatements of what the code obviously does.
- Session-specific operational settings (which scene to run this Sunday) — that's session memory.
- Speculative future plans. Document what IS, not what might be.

**Optimization pass** — every ~5 substantial changes (or when a section bloats):
- Remove gotchas that are now structurally impossible (the offending code is gone).
- Fold duplicate guidance together; a fact should live in exactly one section.
- Re-check tuning constants and numbers against the code — a fixed measurement bug can silently
  invalidate constants that were tuned to the broken scale (see `NatCvGood`/`NatCvBad`).
- Tighten wording. If a section hasn't been referenced or updated in many sessions, ask whether it's
  still load-bearing.

The goal: this file should always be the fastest way for a new Claude session to become productive
in this repo. If it grows stale or bloated, it loses that property.
