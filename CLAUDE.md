# AudioMixer

A Windows desktop audio mixer: 1–10 configurable inputs (default 3) → 2 configurable outputs, with
per-channel volume, mute, delay, routing toggles, VU meters, recording, and presets. Built to send a
mix to a headset AND Zoom (via VB-CABLE) simultaneously, with delay compensation for Bluetooth mics.

Input count is runtime-configurable via a toolbar picker (`MainViewModel.InputCount` →
`AudioEngine.SetInputCount`): the engine grows/shrinks its `Inputs` array (preserving existing
channels, stop+dispose on shrink) and restarts the output buses to re-collect providers. `Channels`
is an `ObservableCollection`; the window is non-resizable (`ResizeMode=CanMinimize`) and its width
is computed from the input count in `MainWindow` code-behind (see gotcha below).

## Stack

- **.NET 8** + **WPF** (single-window desktop app)
- **NAudio** for audio I/O (WASAPI shared mode)
- **System.Text.Json** for preset persistence
- MVVM pattern (ViewModels per channel + main)

## Project layout

```
AudioMixer.sln
AudioMixer/
├── AudioMixer.csproj         # .NET 8, WPF, NAudio reference
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .cs
├── Audio/
│   ├── AudioEngine.cs        # Owns capture/render lifecycle, wires graph, runs AutoMixer tick timer
│   ├── InputChannel.cs       # capture → mute → gain → delay → tap → per-output automix gain → push
│   ├── OutputBus.cs          # MixingSampleProvider → WasapiOut, peak tap, optional recorder
│   ├── AutoMixer.cs          # Per-output leader decision loop (loudest-wins or reference-guided, share/gate); off the audio threads
│   ├── AutoMixMode.cs        # enum Off/Share/Gate
│   ├── DelayLine.cs          # Ring buffer with adjustable read offset
│   ├── PeakMeter.cs          # Computes peak dBFS per buffer, peak-hold decay
│   ├── MixRecorder.cs        # WaveFileWriter wrapper, thread-safe start/stop
│   └── AudioLog.cs           # Opt-in file log (AUDIOMIXER_LOG → %TEMP%\AudioMixer.log); banner records exe/version/build
├── ViewModels/
│   ├── MainViewModel.cs      # Engine lifecycle, output bus pickers, preset list, record state, /state JSON snapshot
│   ├── ChannelViewModel.cs   # Per-input: device, volume, mute, delay, routes, meter, priority flag, per-bus LED state (RoutedA/B, DuckingA/B), Clarity
│   └── OutputViewModel.cs    # Per-output: device, meter, volume, record button, automix mode + strength + stable-hand-off + reference-guided
├── Models/
│   └── MixerPreset.cs        # Serializable: device IDs, volumes, mutes, delays, routes, automix mode/strength/stable-hand-off/reference-guided
├── Services/
│   ├── PresetStore.cs        # JSON load/save to %APPDATA%\AudioMixer\presets.json
│   ├── DelayAnalyzer.cs      # "Detect Delays" clap test: onset-envelope cross-correlation → per-input suggested delays
│   └── StateServer.cs        # Opt-in loopback JSON state endpoint (AUDIOMIXER_STATE) for live diagnostics
├── Controls/
│   └── VuMeter.xaml          # Custom control: gradient bar with peak-hold tick
└── Assets/
    └── app.ico              # Multi-res app icon (EXE via <ApplicationIcon>, window via Icon=)
```

## Audio architecture

**Pipeline per channel:**
```
WasapiCapture → resample to 48kHz stereo float32 → mute gate → gain →
DelayLine (ring buffer w/ read offset) → peak tap → routing matrix → output bus mixer
```

**Output bus:**
```
MixingSampleProvider (sums routed channels) → peak tap → [optional: recorder tap] → WasapiOut(device)
```

**Key facts:**
- Internal mix format: **48 kHz, stereo, float32**. All captures resample to this.
- Each output bus runs its own WasapiOut at the device's native rate; the bus resamples once on the
  way out.
- Inputs and outputs run on independent clocks. Per-channel ring buffers absorb drift; we accept
  `DiscardOnBufferOverflow` semantics. If drift becomes audible, consider a small async resampler
  per channel.
- WASAPI **shared mode** for all devices — exclusive mode would lock Zoom out of the headset.
- Delay range: 0–1000 ms. Implemented as the read offset into a ring buffer sized for max delay +
  headroom (~1500 ms).
- Meters update at ~30 Hz from peak values latched in the audio thread, read on the UI thread via a
  timer (do NOT marshal per-buffer).
- Each output bus has a post-tap **Volume** (`OutputBus.Volume` → `VolumeSampleProvider`), applied
  *after* the peak/recorder tap — a final device trim (e.g. headset monitor level) that does NOT
  affect the meters or recordings. Recording is **per output**: each bus has its own `MixRecorder`
  (toggled from a record button on each output strip).
- **Automixer** (per output, `AutoMixer` + `InputChannel`): an optional stage that attenuates all
  mics except the one(s) closest to the active talker — the fix for multiple distant mics summing
  the same voice (comb "echo", noise floor, reverb). `AudioEngine` runs a ~100 Hz `Timer`
  (`AutoMixTick`) that reads each channel's `CurrentLevelLinear` (RMS latched in the audio thread),
  smooths it (fast attack / slow release), and for each output computes a per-channel gain over the
  channels routed there — **Share** = gain-share `(score/max)^p` (Dugan-style), **Gate** =
  winner-take-all with ~3 dB hysteresis + ~200 ms hold. The competition is on smoothed level
  (`AutoMixer._env`); the selected leader is **held with hysteresis** (`HandoffHoldTicks` ~200 ms,
  `HandoffHysteresis` ~3 dB) so a brief louder moment on another mic can't steal it. Gate always
  uses the held leader; **Share** uses it when **Stable hand-off** is on (per output,
  `OutputViewModel.StableHandoff`, default on, persisted) and anchors its gain-share to that
  leader's level rather than the instantaneous max — off = legacy instantaneous-loudest. NOTE: an
  earlier crest-factor "clarity" weighting (`score = env × crestWeight`, default on) was tried to
  beat the Ankers' AGC and has been **removed from the selection** — measured on the real hardware,
  crest does NOT track proximity (the speakerphone DSP makes it noise; it ranked the closest mic
  <40% of the time and *increased* selection flips). The actual failure was temporal, not metric:
  Share had no hold, so a distant mic's AGC make-up gain during a talker's pause out-leveled the
  close mic and stole the selection; hold+hysteresis is the fix. Crest (peak/RMS, latched as
  `InputChannel.CurrentPeakLinear`, smoothed in `AutoMixer._crest`, mapped `CrestMin..CrestMax →
  [QualityFloor,1]`) is still computed but now only feeds the per-mic "clarity" readout, refreshed
  while a mic hears speech (`env > SilenceFloorRms`). **Share (gradual hand-off, no swallowed
  syllables) is the right mode for conversational back-and-forth; Gate (hard winner-take-all) suits
  a single presenter** — Gate's hold can clip a fast interjection's first ~200 ms. Gains are written
  lock-free (volatile) and applied by each `InputChannel` at the routing-push step with an
  intra-buffer ramp (no zipper). This is the correct tool for distributed room mics; static delay
  compensation is NOT (per-talker offset isn't fixed). A channel can set `IsPriority` (per-input
  "advanced" gear popup): a priority mic (e.g. a presenter's lapel) is always full level and out of
  the competition, and while it is *active* (`AutoMixer.PriorityActiveRms`, ~-40 dBFS) it ducks the
  other (room) mics — otherwise that voice would reach the bus via both the clean lapel and a
  delayed room mic and comb-filter. Multiple priority mics are intentionally allowed
  (multi-presenter, e.g. pastor + worship leader) — do NOT restrict to one; note they don't duck
  *each other*, so two priority mics hearing the same source would double. `InputChannel.IsDucking`
  (any routed output's gain < 0.85) drives a per-input amber LED; `InputChannel.IsAutoMixActive`
  (this mic is the selected winner/leader on any routed output, set from `AutoMixer._activeInput`)
  drives a green LED — both polled on the meter timer. The crest-derived `InputChannel.Clarity`
  (0..1, NaN when idle) is shown as a live "Mic clarity" bar in the per-input gear popup so the
  operator can see the metric's per-mic ranking. `AudioEngine.AutoMixActiveInput(o)` exposes the
  per-output winner; `MainViewModel.LogAutoMixSelectionChanges` writes each talker hand-off to
  `AudioLog` (opt-in) for after-the-fact diagnosis. **Reference-guided selection** ("Match lapel",
  per output, `OutputViewModel.ReferenceGuided`, default off, persisted): an alternative leader rule
  for when loudest≠best (see gotcha). Instead of the level argmax it picks the room mic whose
  **loudness envelope best correlates with the active priority/lapel mic** — the lapel is a clean
  reference for the talker's voice, so the room mic that tracks it most faithfully is the least
  reverberant/contaminated one. `AutoMixer` keeps a 2 s per-channel envelope ring (`_envHist`), each
  ~50 ms recomputes a best-lag (±600 ms) normalized cross-correlation of each room mic vs the
  reference over speech frames (`LaggedCorr`, smoothed into `_corr`), and the held-leader logic uses
  `_corr` (additive `CorrHysteresis`) instead of `_env` (multiplicative `HandoffHysteresis`). It
  engages only when a priority mic is *speaking* and correlation has converged (`_corr >
  CorrReady`); otherwise it falls back to loudest-wins. The reference is global (`_refIndex` =
  loudest active priority mic), so it works on an output the lapel isn't even routed to (the headset
  bus). Snapshot exposes `_corr`/`_refIndex` via the state endpoint for tuning. **Reference-free
  natural-mic selection** ("Prefer natural", `OutputViewModel.PreferNatural`, default off,
  persisted, lower precedence than Match lapel): for the no-lapel / talkers-across-the-room case.
  Among mics within `NaturalFloorRatio` (-8 dB) of the loudest it picks the one with the lowest
  **spectral-flux instability** (`InputChannel.CurrentFluxCv` — a 512-pt FFT per voiced buffer in
  the audio thread, EMA mean/variance of normalized-spectrum frame-to-frame distance → coefficient
  of variation; lower = more natural). Held leader uses CV with a **multiplicative** margin
  (`NaturalHystRatio` 0.85 — challenger must be ≥15% lower CV). NOTE: the live `CurrentFluxCv` scale
  (~1–3.4) is much larger than the offline Python `flux_cv` (~0.4–0.6) — an early *additive* 0.05
  margin was therefore ≈ zero hysteresis and made near-equal good mics bounce/chop; a multiplicative
  margin is scale-robust. (Same reason offline replays of CV thresholds aren't faithful — different
  scale.) Precedence in `Tick`: `selMode` = correlation if `useCorr`, else natural if `useNatural`,
  else level; `Beats(selMode,...)` applies the matching margin. Snapshot exposes `_cv` as `fluxCv`.
  **Quality-weighted Share** (`SelWeight`): in correlation/natural mode each mic's level is scaled
  by its quality (CV for natural, corr for lapel) *before* the gain-share, so a loud-but-bad mic
  ducks even when louder than a quieter, cleaner leader. WITHOUT this, Share anchors to the leader's
  level and clamps every louder mic to unity — so when the talker sits near a scratchy/loud mic and
  away from the good one, the bad mic stays wide open and you must disable its route by hand (the
  failure mode that prompted the fix). Level-mode Share is unchanged (weight ≡ 1).

## Conventions

- **Naming**: PascalCase for types/methods, _camelCase for private fields, camelCase for
  locals/params.
- **Async**: Audio engine start/stop is async (device init can block). Audio callbacks are NOT
  async.
- **Threading**: NAudio callbacks run on its own threads. Never touch WPF UI objects from a callback
  — use `Dispatcher.BeginInvoke` or (preferred) a UI timer that polls atomic state.
- **Logging**: Use `System.Diagnostics.Trace` for engine events; surface user-facing errors via
  status bar text in MainViewModel. File logging via `AudioLog` (→ `%TEMP%\AudioMixer.log`) is
  **opt-in** — off unless the `AUDIOMIXER_LOG` env var is set OR the `--log` CLI flag is passed (so
  a
  desktop shortcut can enable it without env vars; see `App.ApplyCliFlags`). The meter loop writes
  ~1 line/sec, so we don't grow a file on every run. The log's first line is a banner with the exe
  path, assembly
  version (`1.0.0+<git-sha>`, stamped by an MSBuild target) and build time — so you can tell from a
  log alone *which build* produced it (don't cross-reference DLL mtimes). The per-input log line
  carries **RF-link health** fields `rf=[lvl=<voiced mean dB> voiced=<%> silent=<%> drops=<n>]`
  (`InputChannel.SnapshotRfStats`, lock-free counters latched in the audio callback) for **offline**
  diagnosis of a marginal 2.4 GHz Soundsync dongle link: a dropping link shows exact-silence gaps
  mid-speech (voiced→silent "drop edges") + high `fluxCv` while `voiced%` is high; a healthy-but-far
  mic is quiet-and-smooth. Only assess a mic while it's voiced (an idle mic is silent whether the
  link is fine or dead). Raw counts only — no thresholds in-app; classify from the log after a
  session (the far/50 ft mic is at the edge of dongle range — see the S500 dual-home gotcha above).
- **Diagnostic state endpoint**: `StateServer` serves a full live JSON snapshot at
  `http://127.0.0.1:<port>/state` (channels: levels/routes/mute/gains/clarity/`refCorr`/`fluxCv`;
  outputs: mode/strength/stable/reference/preferNatural + winner; plus `referenceInput`). **Opt-in**
  via `AUDIOMIXER_STATE` (a port number, else default 7077) or the `--state[=PORT]` CLI flag.
  Read-only, loopback only.
  `MainViewModel.BuildStateJson` marshals to the UI thread. This is the fastest way to watch the
  automixer's *reasoning* (env vs corr vs the selected leader) without the GUI.
- **Single instance**: `App.xaml.cs` holds a named mutex — a second launch signals the first (raises
  its window) and `Environment.Exit`s immediately, so two instances never fight over the same WASAPI
  capture devices.
- **No comments explaining what code does.** Only comment non-obvious WHY (e.g. "WASAPI shared mode
  picks device default rate — must resample before mixing").

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

## Known gotchas

*(grows over time — see Self-maintenance protocol below)*

- WASAPI device IDs are stable across reboots **for fixed devices** (onboard/virtual — e.g. Realtek,
  VB-CABLE); persist those in presets. **They are NOT stable for hot-plug USB audio** — see the
  device-remap gotcha below.
- **Hot-plug USB audio (mics, USB headsets, wireless dongles) gets a NEW WASAPI endpoint GUID when it
  re-enumerates** (a Windows-Update driver reboot, a replug, a different port). A preset that matches
  only on `DeviceId` (the endpoint GUID) then silently drops every such device on load and forces a
  manual remap. Root cause for the Anker rig: the Soundsync dongles expose **no USB serial**
  (`USB\VID_291A&PID_3523&MI_01\7&<hash>&0` — the `7&hash&0` is a *port-derived* instance, not a
  serial), so their identity is the USB port, and the endpoint GUID regenerates on re-enumeration.
  Windows *does* re-apply a user's device **rename** to the new endpoint (keyed to the port-path
  instance, confirmed to survive a driver-update reboot), so the friendly name is the stable,
  human-meaningful key even though the GUID isn't. Fix: `MainViewModel.ApplyPreset` (`ResolveDevice`)
  matches `DeviceId` first, then **falls back to the saved friendly name** — normalized by
  `DeviceNameKey`, which strips only the volatile `(N- …)` endpoint enumerator (`EnumeratorPrefix`
  regex) and keeps the rest. Two hard-won details: (1) do **NOT** truncate to the name prefix before
  `" ("` — an un-renamed device's identity is the *interface* name inside the parens (e.g. `Speakers
  (Lync USB Headset)` vs `Speakers (Realtek(R) Audio)` share the prefix `Speakers`), so truncating
  mis-binds the headset to the onboard speakers; (2) resolve against the **master** `_allInputDevices`
  / `_allOutputDevices`, not a channel's `AvailableDevices`, which is dedup-filtered and can be missing
  a device mid-apply. A `used` set prevents two channels grabbing the same device when several
  normalize alike (identical un-renamed dongles → greedy fill). On resolve the app re-selects the real
  device and autosave rewrites the current GUID, so the preset **self-heals** after one launch. NOTE:
  the Ankers are **not** interchangeable for this rig — physical placement matters (each unit sits by
  its own dongle covering a room area), so the operator renamed them `ANKER #1..4` in Windows Sound
  settings to match physical labels; match by that name, never greedy-fill in arbitrary order. A
  *rename* (e.g. `ANKER 4`→`ANKER #4`) is a deliberate identity change and correctly won't name-match
  the old preset — that device needs one manual remap, then it re-saves.
- **A stalled input capture freezes its VU meter at the last value (looks ~80% "active" but passes
  no audio).** `PeakMeter` has no decay — `CurrentDb` only changes inside `Observe()`, called from
  `OnDataAvailable`. If `WasapiCapture` stops firing `DataAvailable` (Anker USB/BT renegotiation,
  device drop), the meter and `_currentLevelLinear` freeze and the channel is silently dead. The old
  Resync only restarted *output* buses so it couldn't recover it. Fixes: (1) `InputChannel.Stop()`
  now calls `PeakMeter.Reset()` so a dead/cleared channel's bar drops to zero; (2) `AudioEngine`
  runs a **capture-stall watchdog** (`WatchdogTick`, 500 ms) — a selected input whose
  `LastDataTicks` is stale >1.5 s is auto-restarted on a background task (backoff
  `RestartBackoffMs`, cap `MaxRestartAttempts`, then `InputRestartGaveUp`); (3) the Resync button
  now calls `RestartInputs()` too. Note: shared-mode WASAPI delivers buffers even during silence, so
  "no DataAvailable" is an unambiguous stall signal — a silent-but-alive mic won't false-trigger.
- **Automix gain is applied AFTER the meter/analysis taps** (`InputPeak`/`PostPeak`/analysis
  recorder all run before the per-output routing push). So VU meters and the clap-test recordings
  show the *pre-automix* post-fader level — a channel can read hot on its meter while the automixer
  is ducking its contribution to a given output. Intentional (the meter shows what the channel
  produces); don't "fix" it by moving the tap.
- **Delay measurement: the route-to-output clap test does NOT measure device latency.** A channel's
  position in the mixed/recorded output is `transport_latency + standing backlog in its per-output
  BufferedWaveProvider`. That backlog is set nondeterministically at startup (a fast, low-latency
  device accumulates a *larger* backlog before the bus starts draining) and anti-correlates with
  transport latency, so the ordering scrambles — a low-latency built-in mic can look *more* delayed
  than a Bluetooth one. For a clean measurement use the "Detect Delays" feature (`DelayAnalyzer`),
  which taps the per-channel analysis recorder (`InputChannel.StartAnalysisRecording`) *before* the
  output buffer. Re-measure after any output restart.
- **`DelayAnalyzer` cross-correlates onset envelopes, NOT a peak-threshold.** A "first sample ≥ 50%
  of file peak" detector mislocates soft/vocal onsets: a spoken "T!" (used because Anker
  speakerphones' noise suppression gates real claps) has its global peak in the *vowel*, so the
  detector skips the leading `[t]` on a clean mic (→ looks late) while a suppressed mic keeps only
  the `[t]` (→ looks early), inverting the ranking. Fix: half-wave-rectified first-difference of a 1
  ms RMS envelope (spectral-flux-style onset), normalized cross-correlation vs the loudest channel
  over ±1000 ms; the normalized peak is reported as a confidence (warn below 0.5). Caveat: a
  speakerphone that *gates* transients may have no constant latency, so no single delay value fully
  syncs it.
- Some Bluetooth headsets switch profile when used as both input and output simultaneously, dropping
  audio quality to HSP/HFP. Workaround: use BT only as input, wired output. (To verify once we have
  hardware in hand.)
- `WaveFileWriter` is NOT thread-safe; serialize Write calls with a lock or write from a single tap
  thread.
- NAudio's `BufferedWaveProvider` property is `DiscardOnBufferOverflow` (not `DiscardOnBufferFull` —
  that name doesn't exist in 2.2.1 despite what older docs suggest).
- WPF's temporary XAML-compilation project (`*_wpftmp.csproj`) does not appear to honor
  `ImplicitUsings` reliably for `System.IO` — add explicit `using System.IO;` in any file that uses
  `Path`/`Directory`/`File` rather than relying on globals.
- The per-output BufferedWaveProvider (InputChannel._outBuffers) sets the **hard cap on end-to-end
  latency**. If you size it generously (e.g. 2s) and input starts pushing before the output starts
  pulling, that backlog becomes audible latency. Keep it small (~200ms) AND clear the buffer when
  (re)starting an output (see `AudioEngine.RestartOutputBus_NoLock` → `ClearOutputBuffer`). Symptom
  of the bug: "hello" comes out 1–2 seconds late.
- **Input strips are laid out in a `UniformGrid Rows="1"`, which divides the column equally and
  IGNORES each child's `MinWidth`.** So a fixed-width window crams N strips into whatever space
  exists and clips the right-most controls (the A/B route toggles vanish first). Fix: the window is
  non-resizable and its width is computed from input count (`MainViewModel.WindowWidth = max(500,
  count*96 + 160)`), applied in `MainWindow` code-behind. Don't bind `Window.Width` in XAML — the
  binding isn't reliably applied at startup because `DataContext` is set *after*
  `InitializeComponent`, so it falls back to the literal; set `Width` in code-behind after assigning
  `DataContext` and on `WindowWidth` PropertyChanged instead. `WindowHeight` follows the same
  pattern (base 320 px + the VB-CABLE banner's height when `ShowVbCablePrompt` is true) and is
  applied in code-behind identically. Also: the outputs live in a fixed-width column (150px), NOT
  `Auto` — an `Auto` column lets the device-name buttons expand to their full untrimmed text and
  blow out the layout.
- **Speakerphone DSP destroys every proximity cue except gross level — don't add "clever" per-frame
  quality metrics for it.** Measured on 4× Anker S500 (AGC + noise suppression + gating to true
  digital silence) with `tools/AnalyzeInputs` (replays the selector over the per-mic WAVs from the
  "record all inputs" diagnostic tap): crest factor, spectral flatness, HF-energy ratio, spectral
  centroid and SNR all FAIL to rank the closest mic — NS even adds HF hiss to *distant* mics
  (inverting HF/centroid), and gating zeroes the noise floor (making SNR just a level proxy). Only
  **smoothed level** survives: ~5–6 dB of proximity remains after AGC, enough to pick the closest
  mic ~18/18 on averages. The original bug (far mic wins) was NOT a metric problem — it was that
  Share re-picked the instantaneous-loudest mic every 10 ms with no hold, so a distant mic's AGC
  pumping in a talker's pause stole the selection (offline replay: 113 flips). Crest weighting,
  added to fix it, made it worse (136 flips). Fix: hold + hysteresis on the level-based leader (≈23
  flips). Lesson: stabilize the level selection; don't trust spectral/crest features through a
  speakerphone's DSP.
- **Loudest ≠ best-sounding, and you can't tell from the mic's own signal — use the lapel as a
  reference.** Second oddball Anker case: a room mic can read *louder* than another yet sound
  clearly *worse* (its AGC making-up gain, desk coupling/proximity boom, or a nearby vent/PA raising
  its level), so plain loudest-wins picks the bad mic. No isolated quality metric saves you (see the
  gotcha above — they're all dead through the DSP). What *does* work: correlate each room mic's
  loudness **envelope** against the **priority/lapel** mic (a clean ground-truth copy of the
  talker). Validated offline with `tools/RefCorr` on a labeled capture (operator confirmed In4 good
  / In5 loud-but-bad): level ranked In5 > In4 (picks bad); refSNR also failed (gating zeroes the
  noise floor, so it favored a distant quiet mic); **envelope-correlation-to-lapel ranked In4
  (0.774) > In5 (0.706)** — the bad mic is loudest yet correlates *worst*, because its envelope is
  smeared by reverb/noise and tracks the clean lapel less faithfully. Shipped as opt-in "Match
  lapel" (`OutputViewModel.ReferenceGuided` → `AutoMixer` reference-guided selection). Caveats from
  the data: only **rejecting the loud-bad mic** is reliable — among several good mics the corr
  margins are within noise (In2 0.778 ≈ In4 0.774), so it won't pick a clear single "best"; and it
  needs an active lapel (falls back to loudest otherwise). `tools/RefCorr` and `tools/AnalyzeInputs`
  both replay offline against the "record all inputs" per-mic WAVs
  (`%USERPROFILE%\Documents\AudioMixer\analysis\diag-input*.wav`) — use them to validate any future
  selector change before touching the live engine.
- **"Natural/scratchy" is NOT measurable by cleanliness metrics through the Anker DSP — measure
  temporal INSTABILITY instead.** Third oddball case: a mic can be *loud AND clean-by-the-numbers*
  yet sound scratchy/unnatural, because the speakerphone's noise-suppression **over-processes** — on
  the labelled capture the bad mic (In5) scored HNR 13.2 / **CPPS 11.3, higher than the clean
  lapel** (8.6), with lower jitter/shimmer than the good mic. So HNR/CPPS/jitter/shimmer all rank
  the bad mic *cleanest* (inverted). What actually sounds scratchy is **intermittent** — gating
  chatter / musical noise / broadband transient clicks (visible as vertical streaks in a
  spectrogram) — i.e. an unstable spectrum over time. The reference-free discriminator that works is
  **spectral-flux coefficient-of-variation** (`flux_cv`): natural mics (and the lapel) sit ~0.41,
  the scratchy mic ~0.52–0.65, consistent across both recordings. Offline replay
  (`tools/replay_natural.py`) of the shipped "Prefer natural" rule flips selection from the bad mic
  (74%/59% of voiced time) to the good mic (64%/58%) on both sessions. Python analysis lives in
  `tools/voice_quality.py` (Praat HNR/CPPS/jitter/shimmer — shows the inversion),
  `tools/naturalness.py` (the flux-instability artifact ranking), `tools/spectro.py` (spectrograms +
  intermittency), `tools/replay_natural.py` (replays the live selector); install: `pip install numpy
  scipy soundfile matplotlib praat-parselmouth`. Caveat: validated on 2 recordings, one room, one
  set of Ankers — confirm across more rooms before trusting; flux-CV also penalizes
  distant/reverberant mics, hence the level floor.
- **NAudio 2.2.1 `MixingSampleProvider.ReadFully=true` only controls output padding — NOT source
  retention.** In 2.2.1, when ANY source returns less than the requested count, MSP unconditionally
  `RemoveAt(index)`'s that source from its `sources` list (regardless of ReadFully). The source is
  gone forever. To prevent eviction, the source provider itself must always return the full
  requested count — set `ReadFully=true` on the underlying `BufferedWaveProvider` so it pads with
  zeros internally when empty. Symptom: audio works until first buffer-empty event (e.g. route
  toggle off then on), then output goes permanently silent until OutputBus is restarted.
- **A WAV being actively recorded reads 0 bytes / a frozen mtime in directory listings.** NTFS doesn't
  flush a file's directory-entry size + last-write-time during a long buffered write, and
  `WaveFileWriter` only finalizes the RIFF header on Dispose (stop). So `Get-ChildItem`/Explorer show a
  live "record all inputs"/`MixRecorder` capture as **0 bytes, mtime stuck at creation** — looks like it
  captured nothing when it's actually fine. Don't judge a live capture by the folder view, and don't
  stop/restart it in a panic (that's the only thing that *would* lose buffered data). To read true
  length mid-write, open the handle: `[System.IO.File]::Open(path,'Open','Read','ReadWrite').Length`.
  `soundfile`/offline tools can't read the file until it's stopped (header still claims 0 frames).
- **An Anker S500 can hold its 2.4 GHz Soundsync dongle link AND a Bluetooth link simultaneously**
  (designed bridging feature). So a mic feeds the mixer fine over its dongle while *also* transmitting
  on BT — a self-contending extra 2.4 GHz radio that garbles the weakest dongle input (chronic, "bad
  whether automix natural or not"). Adaptive hopping (BT AFH + proprietary dongle) reduces but doesn't
  eliminate it — two uncoordinated hoppers + device density + near-field proximity still collide. Fix:
  "Forget" every `Anker PowerConf S500` BT pairing (they auto-reconnect) so units run dongle-only; safe
  because the mixer binds Soundsync endpoints, not the BT (`…PowerConf S500`/Hands-Free) ones. Detect
  with `tools/audio-device-diag.ps1` — it now dedupes BT devices by radio address (multiple identical
  units share a FriendlyName; `Sort -Unique` on name under-counts how many are live on BT).

## Self-maintenance protocol

**This file is intended to be self-optimizing. Claude should update it as the project evolves.**

When working in this repo, update CLAUDE.md (in the same change) whenever you:

1. **Discover a non-obvious gotcha** — a bug that took >15 min to track down, a WASAPI/NAudio quirk,
   a device-specific behavior. Add to "Known gotchas" with one line: symptom → cause → fix.
2. **Change the audio architecture** — add/remove a stage in the pipeline, change the internal mix
   format, switch between shared/exclusive WASAPI, etc. Update "Audio architecture".
3. **Add/rename a top-level folder or file role** — update "Project layout".
4. **Add an external dependency** (NuGet, system install) — update "Stack" or "External
   dependencies".
5. **Establish a new convention** (naming, threading, error handling) — update "Conventions" and
   apply consistently to existing code.

**What NOT to add here:**
- Per-task progress, in-flight TODOs, or PR descriptions (those belong in tasks or commit messages).
- Restatements of what the code obviously does — only capture what a reader couldn't infer in 30
  seconds of reading.
- Speculative future plans. Document what IS, not what might be.

**Optimization pass** — every ~5 substantial changes (or when sections get bloated), do a quick
pruning pass:
- Remove gotchas that are now structurally impossible (the offending code is gone).
- Consolidate duplicate guidance.
- Tighten wording. If a section hasn't been referenced or updated in many sessions, ask whether it's
  still load-bearing.

The goal: this file should always be the fastest way for a new Claude session to become productive
in this repo. If it grows stale or bloated, it loses that property.
