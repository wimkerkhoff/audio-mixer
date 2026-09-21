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
  **Returned (2026-08-30):** Anker confirmed Broadcast pickup mode was *removed from the
  firmware*, which was the last remaining lever against finding 4, and refunded them. They are gone
  from the rig — findings 1-4 remain because they explain why the replacement looks as it does. Everything
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
RODE-PRO-RIG.md               # The 6x Wireless PRO replacement rig: plan, watch-list,
                              #   commissioning checklist. Fold results here, then delete it.
publish.ps1                   # Single-file publish
AudioMixer.Tests/             # xunit. Pure-logic only (no devices/WPF): scenes, health, autosave allowlist
AudioMixer/
├── App.xaml / App.xaml.cs    # Single-instance mutex; OWNS MainViewModel; ApplyCliFlags (see Conventions)
├── Views/                    # The whole UI. Four windows, no Advanced — see below.
│   ├── SimpleWindow.xaml     # THE mixer: scenes, channel rows (level/mute/bus A+B), on-air cards
│   ├── ChecksWindow.xaml     # Everything needing attention; opens itself only when something does
│   ├── DiagnosticsWindow.xaml # Ranked "why this mic?" table; own 10 Hz timer, off when closed
│   ├── SettingsWindow.xaml   # The lapel, mic devices + split side, automix mode, leveler, low-cut
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
├── RxProbe/                  # C#: captures 2 endpoints at once — verify a split receiver at
│                             #     sample level (corr + scalar-fit) after any remap
├── VolProbe/                 # C#: read/set a capture endpoint's Windows gain (see the gain gotcha).
│                             #     Lists ALL active capture endpoints; name+level args set one
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

## The UI, after 2026-09-20

**There is one mixer window.** The Advanced window was retired once everything it uniquely held had a
home, which is worth recording because the split cost real confusion: two places to mute a mic, two
device pickers, two toolbars, and a window that could not be closed (only hidden) because it owned the
view model.

- **Operator panel** (`SimpleWindow`) — scenes, one row per mic (state stripe, meter with the target
  band, level, mute, bus A/B that lights when the automixer picks it), on-air cards per bus with their
  own trim, and one toolbar. This is the mixer now, not a simplified view of one.
- **Checks** — every warning and error, nothing that is merely fine. Opens itself only when something
  needs attention, so its appearance is the signal; never blocks.
- **Diagnostics** — why this mic, the session record, calibration, devices. Never needed to run a service.
- **Settings** — the rig: which mic is the lapel (and therefore priority), each strip's device and split
  side, automix mode, the bus leveler, the global low-cut, picker filters.

`App` owns `MainViewModel` and disposes it in `OnExit`. `--advanced` / `--simple` are gone.

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
MixingSampleProvider (sums routed channels) → bus leveler + limiter → peak tap →
  [optional recorder tap] → volume → WasapiOut
```

**The bus leveler is the ONLY dynamics stage in the app, and it may never move upstream of the
mixer.** `BusLeveler` (per output, default OFF) is a slow broadcast-style leveler — ratio/threshold/
attack/release, a hard-capped make-up lift and a brick-wall limiter — for talkers who are quieter,
louder, or further from a table mic. It sits deliberately *after* the automixer: `AutoMixer` picks
the active mic by comparing per-channel smoothed RMS latched in `InputChannel.MeasureAndLatchLevels`,
so any compression ahead of that flattens the level differences that encode *which mic is closest to
the talker*. That is precisely what a transmitter's own AGC does, which is why GainAssist has to be
off on this rig (finding 6a/7) — a per-channel compressor would be the same bug in software.

Three constraints that are load-bearing rather than taste:
- **Make-up lift is capped (`BusLeveler.MakeupCeilingDb` = 12 dB).** The room floor is acoustic HVAC
  (finding 5b) and speech-band S/N is ~15 dB, so every dB of lift is a dB of rumble that no filter
  takes back out. The cap is a noise budget.
- **Idle hold, never a gate.** Below `IdleFloorDb` the gain *freezes* — `LevelerCore.Step` has no
  path that lowers gain while idle, so it cannot punch holes in sustained material the way the
  speakerphones did (finding 4). It also stops the leveler ramping into a priority duck and slamming
  back. `IdleFloorDb` must be **calibrated per room**: a bus summing several open mics can sit above
  the −45 dBFS default during "silence", and then the hold never engages.
- **Off is a true bypass** (early return), so a disabled leveler is bit-identical and free.

Settings live on `OutputBus.Leveler` (a `BusLevelerSettings`), not on the provider, so they survive
`AudioEngine.RestartOutputBus_NoLock`; `OutputViewModel` writes them through directly the way it does
`Volume`, with no `IAutoMixControl` involvement — the leveler is a bus device, not an automix
decision.

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
| Off | unity | singing — no single talker for follow-the-talker to follow |
| Gate | winner-take-all (hard mute of non-leaders) | everything else |

**Share was removed 2026-09-20**, with the strength slider, "Stable hand-off", "Match lapel" and
"Prefer natural". All four were Anker-era: Share attenuated non-leaders instead of muting them, so
several mics hearing one voice still combed and strength could only make that quieter; no scene ever
selected it. Match lapel and Prefer natural were built to reject a loud-but-bad speakerphone, which
finding 6 says cannot happen on matched DSP-free transmitters — and finding 6b measured Prefer
natural actively harmful there. Stable hand-off became unconditional: finding 6a calls the hysteresis
exactly as necessary, so a switch that turned it off could only re-create "far mic wins". The priority
duck and Gate's non-leader level are now fixed at a hard mute, which is what strength 100% did and the
only setting this rig ever ran. Flux-CV is still computed and still worth reading — it rises on RF
dropouts — it simply no longer selects.

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
room mic and comb-filters. **Exactly one mic is priority, and it is the lapel** (operator, 2026-09-20). Role
and priority were two controls for one idea; picking the lapel in Settings now sets both, exclusively.
Scenes still clear priority where they must — Prayer mutes and de-prioritises it outright — so `Role`
stays the durable property and `IsPriority` the runtime one. The engine has no hard limit, but nothing
in the UI can arm a second priority mic, which also retires the hazard of one left armed on an unused
strip. Two priority mics hearing one source would still double, since they do not duck each other. **Hazard:** an unused-but-open priority lapel that crosses −40 dBFS (bumped,
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
  `tools/baselines/`. Compares aggregates (mode, hand-off count, occupancy, median flux-cv). Record
  and check at the **same `-Speed`, 1–2**; higher saturates the process and starts dropping audio.
  The script passes `--advanced` explicitly so a fixture keeps the window its goldens were recorded
  under even though the app now defaults to Simple — a fixture must never inherit a UI change as a
  change in CPU load.
- **⚠ The baselines are NOT hermetic and currently cannot gate a regression.** `--replay` suppresses
  autosave and output devices but **not preset *loading*** — `MainViewModel` calls
  `TryLoadInitialPreset()` unconditionally before `StartReplayIfRequested()`, so every fixture runs
  against whatever `%APPDATA%\AudioMixer\preset.json` happens to hold *today*: routing, low-cut,
  split `ChannelSource`, automix mode. Change your routing and every golden "drifts" with no code
  change. Verified 2026-08-30 by running the `presentation` fixture at **`5c597e9`, the very commit
  that recorded it**: 60 hand-offs vs its own stored 14, with output B's occupancy shifted from
  51.3%/32.8% to 0%/80% — exactly what that day's preset (only ch4 routed to B; ch1/ch2 on 90/100 Hz
  low-cuts and Left/Right split) predicts. So a `DRIFT` report means "the preset moved" at least as
  often as "the selector moved", and `-Update` silently launders the difference. Do **not** conclude
  a selector regression from a baseline diff without first checking the preset's mtime; and don't
  re-record to make it green. Fix (ROADMAP): give each baseline its own preset.
- **Binding errors**: WPF resolves binding paths at runtime and swallows failures, so a clean build
  proves nothing about the UI. `--log` enables `BindingErrorListener`, which logs them.
  `--open-all` opens every window so one run covers all their markup.
- **`--shots[=DIR]` renders every window to PNG and exits** — the only way to actually SEE the UI
  without being at the machine. It uses `RenderTargetBitmap` on the visual tree, not a screen grab,
  so it works with windows occluded, off-screen, or the **workstation locked** — where
  `CopyFromScreen` silently returns the lock screen instead of the desktop, which looks exactly like
  a window that failed to open. (`PrintWindow` is no use either: it returns blank for WPF content.)
  Zero binding errors is *not* evidence the layout is right; it is also what a window that rendered
  garbage reports.
- **Unit tests** (`AudioMixer.Tests`) cover only pure logic — scene rules, health rules, the autosave
  allowlist invariant, the low-cut option mapping. Anything needing a device or a window is verified
  by a replay run instead. The one exception is `XamlResourceTests`, which reads the markup as *text*
  (no WPF instantiation, no devices) to check every `{StaticResource}` key resolves in its own file —
  see the UI gotcha for why a clean build does not.
- **Nothing runs on push.** `.github/workflows/release.yml` only builds on a version tag; it never
  runs `dotnet test`. So the suite is only as good as the last person who ran it locally — which is
  how a window that crashed on open shipped and stayed broken for weeks.
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
  **Crashes are the exception and are always recorded.** `App.InstallCrashHandlers` writes dispatcher,
  app-domain and unobserved-task exceptions to `%TEMP%\AudioMixer.crash.log` unconditionally, because
  the run that matters is the one nobody passed `--log` to; before it existed a crash left only a WER
  bucket with no managed stack. Note `Trace.WriteLine` alone is **not** a diagnostic — with no listener
  attached it goes nowhere — so every failure path worth reading (preset load, capture stopped,
  watchdog restart, state server) writes to `AudioLog` too. A silent preset-load failure is the worst
  of them: it looks exactly like an unconfigured mixer.
- **Gain calibration** rides on the same per-input log line as `cal=[speech=<p50 dB> floor=<p50 dB>
  n=<buffers>]`, and shows as `speech`/`floor` columns in the Diagnostics window (green within ±3 dB
  of the −24 dBFS target) plus `speechDb`/`floorDb` in `/state`. `CalibrationHistogram` tallies every
  capture buffer's RMS into 1 dB bins, voiced separately from the rest, at the **same post-fader tap
  the automixer's absolute thresholds read** — so the number means what `PriorityActiveRms` and
  friends mean. A peak meter cannot do this job: a DSP-free wireless mic's crest factor is ~20 dB, so
  its peak says nothing about where speech sits, which is how a whole session's fixture once came out
  30 dB low and unusable. Deliberately **cumulative** (a settling number is what makes gain-setting a
  matching exercise), so it must be reset — Diagnostics → *Reset calibration* — after every
  transmitter gain change, or the pre-change buffers keep dragging the median.
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

**7. Mixed device types cannot share one selector — AGC flattens the proximity cue the automixer
needs.** Measured 2026-08-30 over a 179 s Q&A with 2 Ankers + a split Rode pair on one Gate bus.
The Anker in front of the presenter and the Anker a row back read an **identical −24.6 dBFS p50**
despite being rows apart: their AGC auto-levels, so Anker level carries *no* proximity information
(finding 1, in its strongest form). The Rode pair, physically **closer** to the back row than either
Anker, read −54.4 / −52.9 — a ~30 dB offset that is device gain, not distance (inverse-square across
those rows is ~3.5 dB). Consequence: the Ankers led **68%** of the window and the Rodes **0.9%**, and
they won on *construction*, not proximity — no level trim fixes this, because you cannot equalize an
AGC-compressed source against an uncompressed one (the Ankers' p50 sits near their peak; the Rode's
crest is ~20 dB, so matching RMS clips the peaks). Corollary: any level comparison **across** device
types is meaningless — compare only within a matched set. This is the strongest argument for the
homogeneous DSP-free rig of finding 6.

**8. Absolute automix thresholds turn a gain-staging error into a chopped mix — check level before
blaming a capsule.** Measured 2026-09-20 over a 15 min prayer meeting on three DSP-free mics (a Rode
lapel on the Realtek aux + a split Wireless PRO pair). The capture itself was **clean**: zero samples
at or over full scale, **0.00%** impulsive frames (frame crest > 24 dB), flux-CV 0.382/0.383, `drops=0`.
But every mic ran ~22 dB under the -24 dBFS target — speech p50 **-45.3 / -46.1 / -45.7 dBFS**, floors
-61/-65/-66, S/N 16-21 dB. `PriorityActiveRms` (-40 dBFS), `PriorityBreakInRms` (-50) and
`SilenceFloorRms` (-55) are **absolute**, tuned for speech at -24, so the signal straddled them instead
of clearing them. With the presenter's lapel at `speech=-39` — **1 dB of margin** — every soft syllable
released the priority duck and a room mic was briefly selected (the operator reported exactly this,
unprompted, before the log was read). Bus-wide: **12 winner changes/min** and **winner = -1 for 28-29%**
of the session, which in Gate mode is a hard mute — both room mics silent **62%** of the time, the
lapel 23%. That chopping is what gets heard, and it is easy to misattribute to a scratchy mic. So:
**compare `cal=[speech=…]` against -24 dBFS before reaching for a quality metric.** No selector tuning
fixes it — the rule is right, the signal is in the wrong place. Corollary: you cannot just add the
missing 22 dB. Measured crest (whole-file peak vs speech p50) is **28-36 dB**, so speech at -24 would
put peaks well over full scale; the gain belongs at the transmitter, converged with the calibration
histogram, not dialled in downstream.

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
  device mid-apply. A `used` set prevents two channels grabbing the same device. **That self-healing was defeated by the app erasing its own memory** (fixed 2026-09-20): when an
  endpoint vanished, `ChannelViewModel.RefreshDevices` nulled `SelectedDevice`, and the 500 ms autosave
  then wrote `DeviceId=null DeviceName=null` — deleting the only thing the name match could work from,
  so every replug cost a manual remap. The strip now keeps `DesiredDeviceId`/`DesiredDeviceName` which
  survive the device going away (cleared only by an explicit *Clear device*), `PresetMapper` falls back
  to them, and `ReattachDesiredDevices` re-binds a strip the moment its device reappears — no restart.
  Lesson for any future state that mirrors hardware: **what the operator asked for and what is
  currently resolvable are different facts, and only the first should be persisted.**  On resolve, autosave
  rewrites the current GUID — the preset **self-heals** after one launch. NOTE: the Ankers are **not**
  interchangeable — each unit covers a room area next to its own dongle, so the operator renamed them
  `ANKER #1..4` in Windows Sound settings to match physical labels. Match by that name; never
  greedy-fill in arbitrary order. A deliberate *rename* (`ANKER 4`→`ANKER #4`) correctly won't match
  the old preset — one manual remap, then it re-saves.
- **Endpoint gain ranges differ wildly per device, and the app's own fader cannot boost.**
  `ChannelViewModel.PercentToLinear` is `percent / 100f` clamped 0..100 — 100% is *unity*, so a quiet
  mic can NEVER be raised in-app, only upstream. Measured 2026-08-30: the Wireless PRO RX endpoint
  ranges **−96 .. +30 dB**, every Anker Soundsync **−28.4 .. −0.1 dB** (already pinned at its ceiling,
  no boost left). A Rode RX left at Windows-default 0 dB therefore sits ~30 dB under an Anker and is
  structurally unselectable. The Windows slider taper is severely non-linear on the Rode: **53.7% =
  0 dB, 87% = +15 dB, 100% = +30 dB** — the top eighth of the slider is 15 dB, so "turn it up to 100"
  overshoots badly. At +30 dB the pair clipped (peaks **+5.25 / +2.81 dBFS**, ~300 samples over
  −0.5 dBFS); +15 dB gives peak −17 / −12 dBFS with zero samples over full scale. **+24 dB clips too**
  (2026-09-20): raising the RX mid-session to chase a 22 dB speech deficit produced peaks of
  **+6.71 / +6.33 dBFS** and 180–898 over-FS samples per channel, in flat-top runs up to 91 samples
  (1.9 ms) — audible crackle on transients. The clipping is dated precisely to the change: the 29 min
  capture has **zero** over-FS samples in minutes 0-26 and all of them in minute 27, the minute the
  gain was raised. The trap is that **speech was still 14 dB under target at the moment the peaks went
  over**, because this rig's measured crest (whole-file peak vs speech p50) is **~45 dB** — bumps and
  handling sit enormously above speech, so there is no endpoint gain that both lands speech at −24 dBFS
  and keeps transients under full scale. Adding gain cannot fix a crest that wide; proximity and
  transmitter gain can (finding 5b). **+15 dB is the verified ceiling for this receiver** — do not
  exceed it to chase a level deficit. **Endpoint gain does not survive a port change**, so on a
  rig whose devices move between hub ports it is the wrong place for this setting: the value is
  stored against the endpoint GUID, which is keyed to the port-derived USB instance path
  (`...MI_01&6941B14&0&0001` — no serial). Same port and it persists (an unplugged RX still
  reports its 15.0 dB); a different port mints a fresh endpoint at the 0 dB default and loses the
  rename with it. The Anker endpoints show the residue — `2-`/`3-`/`4-`/`5-`/`7-` prefixed records
  from separate ports, several with orphaned volume stores. Set gain at the **transmitter**: it
  lives in hardware, travels with the device, and doesn't spend peak headroom.
  `tools/VolProbe "Wireless PRO" 15` is the stopgap when it does reset. Corollary: any scheme that
  leans on a Windows **rename** to tell identical receivers apart needs a dedicated labelled port
  per unit.
  Note the capture is float32, so the endpoint does not saturate: over-full-scale samples pass through
  and only clip at render, which is why a peak reading alone looks fine. Judge clipping by counting
  samples ≥ full scale plus flat-top runs, never by peak dBFS.

- **Whether a USB audio device keeps its identity across a port change is answerable, and Rode and
  Anker differ.** The WASAPI endpoint GUID always regenerates, so matching falls back to the friendly
  name — which breaks the moment you own TWO IDENTICAL receivers, because they share that name and
  `Resolve` then picks an arbitrary free one. With each receiver covering its own part of the room
  that is the wrong mic in the wrong place: the Anker "never greedy-fill" lesson, restated for Rode.
  The answer is in the PnP tree, not the audio API: walk endpoint → parent interface → USB composite
  device and read the LAST segment of its instance id. No `&` means a real hardware **serial**
  (`...77408030`), stable across ports and unique between two units of a model; an `&` means
  **port-derived** (`...&1ff22f3e&0&4`), reminted per port along with the endpoint GUID and the
  rename. `tools/device-identity.ps1` classifies every endpoint this way. Measured 2026-09-20: Jabra
  PanaCast **SERIAL**, Lync USB Headset **PORT-DERIVED**, and the Rode `VID_19F7&PID_0058` composite
  showed `\801D150D` with two sibling units at `\801D1107` and `\801D110E` — so **Rode receivers
  appear to be permanently distinguishable, unlike the Soundsync dongles**. Confirm on live hardware
  before relying on it; the RX was in its case when this was measured. Note the *audio* side cannot
  answer this — `PKEY_Device_InstanceId` is not exposed on an endpoint's property store and reads
  empty for every device, USB included.
- **A Wireless PRO in its charging case enumerates as USB *storage*, not audio.** Observed 2026-08-31:
  two `RODE Wireless PRO USB Device` DiskDrives live (`VEN_RODE&PROD_WIRELESS_PRO&REV_V332`) while the
  `Wireless PRO RX` audio interface (`VID_19F7&PID_0058&MI_01`) and its endpoint were both absent, so
  the only live Rode path was the 3.5 mm aux. Two volumes and not three is the tell: the **transmitters**
  hold the on-board recording storage and the RX has none, so a case containing RX + 2 TX mounts exactly
  two drives. Connect the RX **directly** by its own USB-C to get a capture endpoint. Diagnostic value:
  "I see RODE mass-storage volumes" means the gear is in the case, i.e. not live — check that before
  hunting for a driver problem.
- **Each TX records 32-bit float on board (32 GB, 40+ h) — a gain-proof backup fixture.** 32-bit float
  cannot clip and cannot be too quiet to recover losslessly, so an on-board recording is immune to the
  gain-staging mistake that has already ruined one capture (the Q&A fixture came out ~30 dB low and
  unusable). Arm it for any session whose recording matters. Two limits keep it a *complement*, not a
  replacement: it is captured **before the RF link**, so it cannot show dropouts and cannot validate the
  selector (which only ever sees the post-RF signal) — but that same property makes it the decisive test
  for "bad mic or bad link", since a glitch present in the app capture and absent on board is RF by
  construction.

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

- **Recording is always on, and the disk arithmetic is why it has three bounds rather than one.** A
  single stream at the internal format (48 kHz stereo float32) is **1.29 GB/hour**; five mics and two
  buses is **~9 GB/hour**, so a capped hour is ~9 GB (~6 GB once split strips go mono) and four
  weeks at two services a week is ~50-70 GB against ~129 GB free. Age alone would therefore prune about a week
  *after* the disk filled. So: recording stops itself at **1 hour** (somebody forgetting to close the
  app must not mean a recording that runs till the disk is full), files expire at **28 days**, and
  `RecordingRetention` additionally deletes **oldest-first whenever free space drops under 20 GB**,
  refuses to start under 15 GB and stops an in-flight recording under 8 GB — the stop floor being
  lower than the start floor on purpose, so a session already running is given every chance to finish.
  Session records are never swept: they are tens of kilobytes and are what you still want once the
  audio is gone.
  **Split strips record MONO** — after the side split a Left/Right strip carries one transmitter
  duplicated to both channels (L/R correlation measured at exactly 1.0000), so the second channel is a
  verbatim copy and the file halves losslessly. A `Stereo` strip on a genuinely stereo device keeps
  both. Verified: a split pair writes 1-channel files at half the size of the stereo strips beside them.
- **`decisions-<stamp>.csv` sits beside every capture, and is the only thing that can answer "should
  it have picked a different mic".** The diag WAVs are tapped BEFORE the automix gain and before the
  bus, so they show what each mic heard and nothing about what was done with it; the mix shows a
  choice was wrong but never what the alternative sounded like at that instant. The CSV carries one
  row per 100 ms — leader per bus, plus each mic's level and applied gain — sharing the recording's
  stamp so it lines up sample-wise. 10 Hz is deliberate: the automixer's hold is 200 ms, so this
  cannot miss a hand-off, and an hour costs ~2 MB against gigabytes of audio.


- **Automix gain is applied AFTER the meter/analysis taps** (`InputPeak`/`PostPeak`/analysis recorder
  all run before the per-output routing push). So VU meters and clap-test recordings show the
  *pre-automix* post-fader level — a channel can read hot while the automixer ducks its contribution.
  Intentional (the meter shows what the channel produces); don't "fix" it by moving the tap.
- **An empty per-output feed buffer is a SILENT hole, and nothing upstream can see it.** The
  `BufferedWaveProvider` in `InputChannel._outBuffers` runs `ReadFully=true`, so when the bus asks for
  more than it holds it returns the shortfall as **zeros** — the read looks complete, no meter moves,
  no peak changes. At the ~10 ms (480-frame) shared-mode read size each hole is a ≤10 ms splice with a
  broadband click at each edge, heard as fine crackle or grit rather than as a dropout. Crucially the
  analysis recorder taps *upstream* of this buffer, so a diag WAV is clean by construction and no
  offline tool can ever find it — headset-clean-recording is the signature. `ClearOutputBuffer` now
  primes 40 ms of silence so the standing backlog is chosen rather than left to the startup race, and
  `TrackingSampleProvider` counts depth-vs-request **before** each read (after it, ReadFully has
  already padded). The count rides the per-input log line as `under=[a,b]` beside `bufMs`.
  **`bufMs` = 0 is NOT an underrun, and mistaking it for one cost a session.** It is a 1 Hz sample of a
  value that legitimately drains to zero and refills between reads. Measured 2026-09-20: pre-prime the
  Lync USB headset sat at 0 ms in **15.8%** of samples against **1.1%** for VB-CABLE (a virtual device
  is slaved to the system clock; a USB headset has its own crystal, so only the real one drifts), which
  looked damning — but post-prime the same bus still reads 0 ms in **3.1%** of samples with **zero**
  underruns over 65 s. The depth ratio between two buses is a drift signal, nothing more. Only
  `under=[]` distinguishes a hole from normal oscillation, which is the entire reason it exists.
- **Per-channel delay and the clap test were removed 2026-09-20.** Both came from Anker-era delay
  compensation, which the automixer superseded: Gate hard-mutes every non-leader, so only one mic's
  copy of a voice reaches the bus and there is nothing left to time-align. `DelayAnalyzer`, the delay
  slider and `Detect Delays` are gone. The finding below is kept because it is about *measurement*,
  not the feature, and the same trap waits for anyone who tries to time a signal path by clapping.
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
- **A digital-silence rate over a whole diag WAV counts the startup window and will libel a mic that
  does not gate.** The recorder starts when the operator clicks it, which is *before* the transmitters
  are powered, paired and bound — so the head of every capture is true zero. A 2026-09-20 Wireless PRO
  capture measured 16.6% / 12.7% digital silence whole-file, which reads exactly like the Anker gating
  of finding 4; per minute it was **99%/62% in minutes 1-2 and then 0.0% for the rest of the session**.
  Always bucket the rate per minute (or skip the first 2-3 min) before concluding anything about
  gating. Same caveat for speech/floor medians: the silent head drags the floor toward -inf.
- **To prove a split receiver is really two transmitters, correlate at BOTH scales.** Sample-level
  correlation near 1.0 means one signal fanned to both sides (not split); near 0 means two capsules.
  Envelope correlation stays *high* either way, because both mics hear the same room — so envelope
  alone cannot tell them apart. A verified-good split on 2026-09-20 read **sample 0.069, envelope
  0.866**. `tools/RxProbe` does this at the endpoint level; numpy on two diag WAVs does it post-hoc.
- **A WAV being actively recorded reads 0 bytes / a frozen mtime in directory listings.** NTFS doesn't
  flush the directory-entry size + last-write-time during a long buffered write, and `WaveFileWriter`
  only finalizes the RIFF header on Dispose. So Explorer/`Get-ChildItem` show a live capture as 0 bytes
  with the mtime stuck at creation — it's fine. Don't judge a live capture by the folder view and don't
  stop/restart it in a panic (that's the only thing that *would* lose buffered data). True length
  mid-write: `[System.IO.File]::Open(path,'Open','Read','ReadWrite').Length`. Offline tools
  (`soundfile`) can't read it until stopped (header still claims 0 frames) — to analyze mid-session,
  parse the chunks and read raw float32 from the `data` offset to true EOF (`tools/live_wav.py`).

### UI / WPF

- **A `{StaticResource}` key is resolved when a template is APPLIED, not at compile time, so a missing
  one is a runtime process kill that a clean build will not reveal.** `MainWindow.xaml` referenced
  `{StaticResource Cap}`, a style that exists only in the three `Views/` windows; WPF threw
  `XamlParseException` inside `UniformGrid.MeasureOverride` while showing the window and the process
  died before painting. It shipped in `ed756fa` and every `--advanced` launch — including
  `tools/replay-baseline.ps1`, which passes `--advanced` — crashed for weeks. `BindingErrorListener`
  does **not** catch this: it sees binding failures, not a fatal parse error. Guarded now by
  `AudioMixer.Tests/XamlResourceTests`, which checks **per file** that every referenced key is defined
  in that file. Per-file is the whole point — globally the key sets match, because `Cap` *is* defined,
  just out of scope. That scoping holds only because `App.xaml` carries no resources and every style
  lives in a `Window.Resources`; `DefinitionsAreNotShared` pins the assumption so the test fails
  honestly if merged dictionaries ever appear.
- **A snap-to-tick `Slider` in a strip column is unusable, and it fails as skipped values rather than
  as an obvious bug.** The low-cut was `Minimum=0 Maximum=200 TickFrequency=10` — 21 positions in a
  ~115 px column, ~5 px per tick — so which cutoffs you could land on depended on pixel rounding
  during the drag, and an operator reported reaching 70 and 90 Hz but not 80 on one strip and 60/80/100
  on another. Anything with a small fixed set of meaningful values belongs in a `ListBox`
  (`PopupList`) inside the popup, like the automix mode and leveler strength pickers.
- **Popup text has its own styles for a reason — `Lbl` is the strip style and is too dim inside a
  popup.** `Lbl` is 9 px `#B4B4BE`, sized to be glanced at in a narrow strip; `PopupLbl` (10 px
  `#C7C7D2`) and `PopupHelp` exist because popup text sits on the darker `#1B1B22` surface and is
  *read*. The leveler popup was built with `Lbl` throughout and the operator reported it as too dim.


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
  is computed from input count (`MainViewModel.WindowWidth = max(560, count*StripWidth +
  NonStripWidth)`, 100/260), applied in
  `MainWindow` code-behind. Don't bind `Window.Width` in XAML — `DataContext` is set *after*
  `InitializeComponent`, so the binding isn't reliably applied at startup and it falls back to the
  **That width is the only thing keeping the strips legible, so it needs headroom, not a bare fit:**
  the earlier `count*96 + 240` never counted the window border, and at 10 inputs a 1200 px window has
  a ~1184 px client — (1184-230)/10 = 95.4 px per strip, 85.4 px of content against the strip's
  `MinWidth` of 86, clipping by a hair. Verified end to end at 10 inputs 2026-09-20: the preset loads,
  all four windows open, no binding errors, 1260x404 fits 1920. `WindowSizingTests` pins every count
  1-10 against that minimum and its constants must be changed with the view model's.
  literal. Set `Width` in code-behind after assigning `DataContext` and on `WindowWidth`
  PropertyChanged. `WindowHeight` follows the same pattern (`BaseWindowHeight` + the VB-CABLE banner
  when `ShowVbCablePrompt`). Also: outputs live in a fixed-width column (**230 px**), NOT `Auto` — an
  `Auto` column lets device-name buttons expand to their full untrimmed text and blows out the layout.
- **Adding a row to the output template clips it silently.** The window is `CanMinimize` with its
  height from the `BaseWindowHeight` constant, and there is no scrollbar — a new `RowDefinition` in
  the output `DataTemplate` just doesn't render, with no error and nothing in the log. Bump
  `BaseWindowHeight` in the same change (the leveler row cost +60 px). The two outputs share that
  230 px column via `UniformGrid Rows="1"`, so each strip is only ~115 px wide: put a collapsed
  `ToggleButton` + `Popup` in the column and every slider *inside* the popup, which is its own
  top-level window and unconstrained by the column. That is why the automix mode picker, the device
  picker and the leveler are all popups.
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
