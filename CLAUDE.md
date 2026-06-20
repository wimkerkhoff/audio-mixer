# AudioMixer

A Windows desktop audio mixer: 1–10 configurable inputs (default 3) → 2 configurable outputs, with per-channel volume, mute, delay, routing toggles, VU meters, recording, and presets. Built to send a mix to a headset AND Zoom (via VB-CABLE) simultaneously, with delay compensation for Bluetooth mics.

Input count is runtime-configurable via a toolbar picker (`MainViewModel.InputCount` → `AudioEngine.SetInputCount`): the engine grows/shrinks its `Inputs` array (preserving existing channels, stop+dispose on shrink) and restarts the output buses to re-collect providers. `Channels` is an `ObservableCollection`; the window is non-resizable (`ResizeMode=CanMinimize`) and its width is computed from the input count in `MainWindow` code-behind (see gotcha below).

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
│   ├── AutoMixer.cs          # Per-output gain-share/gate decision loop (closest-mic-wins, crest-weighted); off the audio threads
│   ├── AutoMixMode.cs        # enum Off/Share/Gate
│   ├── DelayLine.cs          # Ring buffer with adjustable read offset
│   ├── PeakMeter.cs          # Computes peak dBFS per buffer, peak-hold decay
│   └── MixRecorder.cs        # WaveFileWriter wrapper, thread-safe start/stop
├── ViewModels/
│   ├── MainViewModel.cs      # Engine lifecycle, output bus pickers, preset list, record state
│   ├── ChannelViewModel.cs   # Per-input: device, volume, mute, delay, route flags, meter, priority-mic flag, IsDucking, IsAutoMixActive, Clarity
│   └── OutputViewModel.cs    # Per-output: device, meter, volume, record button, automix mode + strength + stable-hand-off
├── Models/
│   └── MixerPreset.cs        # Serializable: device IDs, volumes, mutes, delays, routes, automix mode/strength/stable-hand-off
├── Services/
│   ├── PresetStore.cs        # JSON load/save to %APPDATA%\AudioMixer\presets.json
│   └── DelayAnalyzer.cs      # "Detect Delays" clap test: onset-envelope cross-correlation → per-input suggested delays
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
- Each output bus runs its own WasapiOut at the device's native rate; the bus resamples once on the way out.
- Inputs and outputs run on independent clocks. Per-channel ring buffers absorb drift; we accept `DiscardOnBufferOverflow` semantics. If drift becomes audible, consider a small async resampler per channel.
- WASAPI **shared mode** for all devices — exclusive mode would lock Zoom out of the headset.
- Delay range: 0–1000 ms. Implemented as the read offset into a ring buffer sized for max delay + headroom (~1500 ms).
- Meters update at ~30 Hz from peak values latched in the audio thread, read on the UI thread via a timer (do NOT marshal per-buffer).
- Each output bus has a post-tap **Volume** (`OutputBus.Volume` → `VolumeSampleProvider`), applied *after* the peak/recorder tap — a final device trim (e.g. headset monitor level) that does NOT affect the meters or recordings. Recording is **per output**: each bus has its own `MixRecorder` (toggled from a record button on each output strip).
- **Automixer** (per output, `AutoMixer` + `InputChannel`): an optional stage that attenuates all mics except the one(s) closest to the active talker — the fix for multiple distant mics summing the same voice (comb "echo", noise floor, reverb). `AudioEngine` runs a ~100 Hz `Timer` (`AutoMixTick`) that reads each channel's `CurrentLevelLinear` (RMS latched in the audio thread), smooths it (fast attack / slow release), and for each output computes a per-channel gain over the channels routed there — **Share** = gain-share `(score/max)^p` (Dugan-style), **Gate** = winner-take-all with ~3 dB hysteresis + ~200 ms hold. The competition is on smoothed level (`AutoMixer._env`); the selected leader is **held with hysteresis** (`HandoffHoldTicks` ~200 ms, `HandoffHysteresis` ~3 dB) so a brief louder moment on another mic can't steal it. Gate always uses the held leader; **Share** uses it when **Stable hand-off** is on (per output, `OutputViewModel.StableHandoff`, default on, persisted) and anchors its gain-share to that leader's level rather than the instantaneous max — off = legacy instantaneous-loudest. NOTE: an earlier crest-factor "clarity" weighting (`score = env × crestWeight`, default on) was tried to beat the Ankers' AGC and has been **removed from the selection** — measured on the real hardware, crest does NOT track proximity (the speakerphone DSP makes it noise; it ranked the closest mic <40% of the time and *increased* selection flips). The actual failure was temporal, not metric: Share had no hold, so a distant mic's AGC make-up gain during a talker's pause out-leveled the close mic and stole the selection; hold+hysteresis is the fix. Crest (peak/RMS, latched as `InputChannel.CurrentPeakLinear`, smoothed in `AutoMixer._crest`, mapped `CrestMin..CrestMax → [QualityFloor,1]`) is still computed but now only feeds the per-mic "clarity" readout, refreshed while a mic hears speech (`env > SilenceFloorRms`). **Share (gradual hand-off, no swallowed syllables) is the right mode for conversational back-and-forth; Gate (hard winner-take-all) suits a single presenter** — Gate's hold can clip a fast interjection's first ~200 ms. Gains are written lock-free (volatile) and applied by each `InputChannel` at the routing-push step with an intra-buffer ramp (no zipper). This is the correct tool for distributed room mics; static delay compensation is NOT (per-talker offset isn't fixed). A channel can set `IsPriority` (per-input "advanced" gear popup): a priority mic (e.g. a presenter's lapel) is always full level and out of the competition, and while it is *active* (`AutoMixer.PriorityActiveRms`, ~-40 dBFS) it ducks the other (room) mics — otherwise that voice would reach the bus via both the clean lapel and a delayed room mic and comb-filter. Multiple priority mics are intentionally allowed (multi-presenter, e.g. pastor + worship leader) — do NOT restrict to one; note they don't duck *each other*, so two priority mics hearing the same source would double. `InputChannel.IsDucking` (any routed output's gain < 0.85) drives a per-input amber LED; `InputChannel.IsAutoMixActive` (this mic is the selected winner/leader on any routed output, set from `AutoMixer._activeInput`) drives a green LED — both polled on the meter timer. The crest-derived `InputChannel.Clarity` (0..1, NaN when idle) is shown as a live "Mic clarity" bar in the per-input gear popup so the operator can see the metric's per-mic ranking. `AudioEngine.AutoMixActiveInput(o)` exposes the per-output winner; `MainViewModel.LogAutoMixSelectionChanges` writes each talker hand-off to `AudioLog` (opt-in) for after-the-fact diagnosis.

## Conventions

- **Naming**: PascalCase for types/methods, _camelCase for private fields, camelCase for locals/params.
- **Async**: Audio engine start/stop is async (device init can block). Audio callbacks are NOT async.
- **Threading**: NAudio callbacks run on its own threads. Never touch WPF UI objects from a callback — use `Dispatcher.BeginInvoke` or (preferred) a UI timer that polls atomic state.
- **Logging**: Use `System.Diagnostics.Trace` for engine events; surface user-facing errors via status bar text in MainViewModel. File logging via `AudioLog` (→ `%TEMP%\AudioMixer.log`) is **opt-in** — off unless the `AUDIOMIXER_LOG` env var is set (the meter loop writes ~1 line/sec, so we don't grow a file on every run).
- **No comments explaining what code does.** Only comment non-obvious WHY (e.g. "WASAPI shared mode picks device default rate — must resample before mixing").

## Build & run

```powershell
dotnet restore
dotnet build
dotnet run --project AudioMixer
```

## External dependencies (user installs manually)

- **VB-CABLE** (https://vb-audio.com/Cable/) — virtual audio cable. After install + reboot, "CABLE Input" appears as a render device (mixer outputs to it) and Zoom selects "CABLE Output" as its microphone.

## Known gotchas

*(grows over time — see Self-maintenance protocol below)*

- WASAPI device IDs are stable across reboots; persist those (not friendly names) in presets.
- **A stalled input capture freezes its VU meter at the last value (looks ~80% "active" but passes no audio).** `PeakMeter` has no decay — `CurrentDb` only changes inside `Observe()`, called from `OnDataAvailable`. If `WasapiCapture` stops firing `DataAvailable` (Anker USB/BT renegotiation, device drop), the meter and `_currentLevelLinear` freeze and the channel is silently dead. The old Resync only restarted *output* buses so it couldn't recover it. Fixes: (1) `InputChannel.Stop()` now calls `PeakMeter.Reset()` so a dead/cleared channel's bar drops to zero; (2) `AudioEngine` runs a **capture-stall watchdog** (`WatchdogTick`, 500 ms) — a selected input whose `LastDataTicks` is stale >1.5 s is auto-restarted on a background task (backoff `RestartBackoffMs`, cap `MaxRestartAttempts`, then `InputRestartGaveUp`); (3) the Resync button now calls `RestartInputs()` too. Note: shared-mode WASAPI delivers buffers even during silence, so "no DataAvailable" is an unambiguous stall signal — a silent-but-alive mic won't false-trigger.
- **Automix gain is applied AFTER the meter/analysis taps** (`InputPeak`/`PostPeak`/analysis recorder all run before the per-output routing push). So VU meters and the clap-test recordings show the *pre-automix* post-fader level — a channel can read hot on its meter while the automixer is ducking its contribution to a given output. Intentional (the meter shows what the channel produces); don't "fix" it by moving the tap.
- **Delay measurement: the route-to-output clap test does NOT measure device latency.** A channel's position in the mixed/recorded output is `transport_latency + standing backlog in its per-output BufferedWaveProvider`. That backlog is set nondeterministically at startup (a fast, low-latency device accumulates a *larger* backlog before the bus starts draining) and anti-correlates with transport latency, so the ordering scrambles — a low-latency built-in mic can look *more* delayed than a Bluetooth one. For a clean measurement use the "Detect Delays" feature (`DelayAnalyzer`), which taps the per-channel analysis recorder (`InputChannel.StartAnalysisRecording`) *before* the output buffer. Re-measure after any output restart.
- **`DelayAnalyzer` cross-correlates onset envelopes, NOT a peak-threshold.** A "first sample ≥ 50% of file peak" detector mislocates soft/vocal onsets: a spoken "T!" (used because Anker speakerphones' noise suppression gates real claps) has its global peak in the *vowel*, so the detector skips the leading `[t]` on a clean mic (→ looks late) while a suppressed mic keeps only the `[t]` (→ looks early), inverting the ranking. Fix: half-wave-rectified first-difference of a 1 ms RMS envelope (spectral-flux-style onset), normalized cross-correlation vs the loudest channel over ±1000 ms; the normalized peak is reported as a confidence (warn below 0.5). Caveat: a speakerphone that *gates* transients may have no constant latency, so no single delay value fully syncs it.
- Some Bluetooth headsets switch profile when used as both input and output simultaneously, dropping audio quality to HSP/HFP. Workaround: use BT only as input, wired output. (To verify once we have hardware in hand.)
- `WaveFileWriter` is NOT thread-safe; serialize Write calls with a lock or write from a single tap thread.
- NAudio's `BufferedWaveProvider` property is `DiscardOnBufferOverflow` (not `DiscardOnBufferFull` — that name doesn't exist in 2.2.1 despite what older docs suggest).
- WPF's temporary XAML-compilation project (`*_wpftmp.csproj`) does not appear to honor `ImplicitUsings` reliably for `System.IO` — add explicit `using System.IO;` in any file that uses `Path`/`Directory`/`File` rather than relying on globals.
- The per-output BufferedWaveProvider (InputChannel._outBuffers) sets the **hard cap on end-to-end latency**. If you size it generously (e.g. 2s) and input starts pushing before the output starts pulling, that backlog becomes audible latency. Keep it small (~200ms) AND clear the buffer when (re)starting an output (see `AudioEngine.RestartOutputBus_NoLock` → `ClearOutputBuffer`). Symptom of the bug: "hello" comes out 1–2 seconds late.
- **Input strips are laid out in a `UniformGrid Rows="1"`, which divides the column equally and IGNORES each child's `MinWidth`.** So a fixed-width window crams N strips into whatever space exists and clips the right-most controls (the A/B route toggles vanish first). Fix: the window is non-resizable and its width is computed from input count (`MainViewModel.WindowWidth = max(500, count*96 + 160)`), applied in `MainWindow` code-behind. Don't bind `Window.Width` in XAML — the binding isn't reliably applied at startup because `DataContext` is set *after* `InitializeComponent`, so it falls back to the literal; set `Width` in code-behind after assigning `DataContext` and on `WindowWidth` PropertyChanged instead. `WindowHeight` follows the same pattern (base 320 px + the VB-CABLE banner's height when `ShowVbCablePrompt` is true) and is applied in code-behind identically. Also: the outputs live in a fixed-width column (150px), NOT `Auto` — an `Auto` column lets the device-name buttons expand to their full untrimmed text and blow out the layout.
- **Speakerphone DSP destroys every proximity cue except gross level — don't add "clever" per-frame quality metrics for it.** Measured on 4× Anker S500 (AGC + noise suppression + gating to true digital silence) with `tools/AnalyzeInputs` (replays the selector over the per-mic WAVs from the "record all inputs" diagnostic tap): crest factor, spectral flatness, HF-energy ratio, spectral centroid and SNR all FAIL to rank the closest mic — NS even adds HF hiss to *distant* mics (inverting HF/centroid), and gating zeroes the noise floor (making SNR just a level proxy). Only **smoothed level** survives: ~5–6 dB of proximity remains after AGC, enough to pick the closest mic ~18/18 on averages. The original bug (far mic wins) was NOT a metric problem — it was that Share re-picked the instantaneous-loudest mic every 10 ms with no hold, so a distant mic's AGC pumping in a talker's pause stole the selection (offline replay: 113 flips). Crest weighting, added to fix it, made it worse (136 flips). Fix: hold + hysteresis on the level-based leader (≈23 flips). Lesson: stabilize the level selection; don't trust spectral/crest features through a speakerphone's DSP.
- **NAudio 2.2.1 `MixingSampleProvider.ReadFully=true` only controls output padding — NOT source retention.** In 2.2.1, when ANY source returns less than the requested count, MSP unconditionally `RemoveAt(index)`'s that source from its `sources` list (regardless of ReadFully). The source is gone forever. To prevent eviction, the source provider itself must always return the full requested count — set `ReadFully=true` on the underlying `BufferedWaveProvider` so it pads with zeros internally when empty. Symptom: audio works until first buffer-empty event (e.g. route toggle off then on), then output goes permanently silent until OutputBus is restarted.

## Self-maintenance protocol

**This file is intended to be self-optimizing. Claude should update it as the project evolves.**

When working in this repo, update CLAUDE.md (in the same change) whenever you:

1. **Discover a non-obvious gotcha** — a bug that took >15 min to track down, a WASAPI/NAudio quirk, a device-specific behavior. Add to "Known gotchas" with one line: symptom → cause → fix.
2. **Change the audio architecture** — add/remove a stage in the pipeline, change the internal mix format, switch between shared/exclusive WASAPI, etc. Update "Audio architecture".
3. **Add/rename a top-level folder or file role** — update "Project layout".
4. **Add an external dependency** (NuGet, system install) — update "Stack" or "External dependencies".
5. **Establish a new convention** (naming, threading, error handling) — update "Conventions" and apply consistently to existing code.

**What NOT to add here:**
- Per-task progress, in-flight TODOs, or PR descriptions (those belong in tasks or commit messages).
- Restatements of what the code obviously does — only capture what a reader couldn't infer in 30 seconds of reading.
- Speculative future plans. Document what IS, not what might be.

**Optimization pass** — every ~5 substantial changes (or when sections get bloated), do a quick pruning pass:
- Remove gotchas that are now structurally impossible (the offending code is gone).
- Consolidate duplicate guidance.
- Tighten wording. If a section hasn't been referenced or updated in many sessions, ask whether it's still load-bearing.

The goal: this file should always be the fastest way for a new Claude session to become productive in this repo. If it grows stale or bloated, it loses that property.
