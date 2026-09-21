# Code review 2026-09-21 — bugs, Anker leftovers, hygiene

Working document for fixing from the .NET dev machine. Read-only review of `b8c8aae` (no build or
test run was possible where it was written, so every item is from the source). Tick items off as
they land; delete this file when it is empty, the way RODE-PRO-RIG.md is meant to go.

Line numbers are against `b8c8aae` and will drift as you edit — search for the identifier instead.

---

## 1. Confirmed bugs

Each of these was traced through the code end to end. Ordered by how badly it hurts a live service.

### 1.4 Scene apply can be vetoed by the route guard

- **Where:** `AudioMixer/ViewModels/SceneController.cs:131-149` (`Write`: mute → priority → routes,
  per channel in index order), guards wired unconditionally at `MainViewModel.cs:778-784`,
  `ChannelViewModel.Muted` setter `:95-99` and route setter `:459-463` refuse via the guard.
- **Scenario:** rig in Singing with the lapel as source (lapel routed, room mics unrouted). Operator
  taps Prayer. If the lapel has a lower index than the room mics it is written first: `Muted = true`
  → `RouteGuard.CheckMute` sees it as the only cover on A and B → refused; `Routes[r].IsOn = false`
  → `CheckUnroute` → refused. Room mics are then routed. Result: Prayer leaves the lapel routed and
  unmuted (priority *is* cleared), and the status line flashes "Bus A would have no microphone".
- **Why tests miss it:** `SceneTransformTests` tests the pure function; the guard lives above it.
- **Fix:** skip the guards while `Scenes.IsApplying` (the scene invariant already guarantees the
  end state), or write the plan in two passes: unmute/route-on for every channel first, then
  route-off/mute.
- **Test:** a VM-level test that wires the guards and applies Singing(Lapel) → Prayer, asserting
  the lapel ends muted and unrouted.

### 1.5 Reducing strips while recording crashes the process

- **Where:** meter tick `MainViewModel.cs:231-236` calls `_decisions?.Sample(..., i =>
  Channels[i].PostPeakDb, (i, o) => _engine.Inputs[i].GetAutoMixGain(o), ...)`;
  `DecisionTrack._inputs` is fixed at record start (`DecisionTrack.cs:37`) and `Sample` loops to it
  (`:90`); `ApplyInputCount` (`:830-838`) removes `Channels[i]` and shrinks `_engine.Inputs`.
- **Effect:** recording auto-starts 12 s after launch, so this is the normal state. Setting "Mic
  strips" from 5 to 3 → next 33 ms tick → `ArgumentOutOfRangeException` on the dispatcher →
  `App.DispatcherUnhandledException` (`App.xaml.cs:75`) records the crash but does not set
  `Handled` → process exits.
- **Second half:** `StopRecording` (`:1357`) only walks surviving `Channels`, and
  `InputChannel.Stop()`/`Dispose()` never call `StopAnalysisRecording`, so the removed strips'
  `diag-input*.wav` are left open with a 0-frame RIFF header.
- **Fix:** stop the recording before a count change (and say so in the status line), or make
  `Sample` clamp to `Math.Min(_inputs, Channels.Count)`; and call `StopAnalysisRecording()` from
  `InputChannel.Stop()`.

### 1.6 A device absent at launch is forgotten by the next autosave

- **Where:** `ChannelViewModel.SelectedDevice` setter (`:44-51`) seeds `DesiredDeviceId/Name` only
  when assigned a non-null device; `ApplyPreset` (`MainViewModel.cs:1228-1230`) assigns
  `DeviceResolver.Resolve(...)` directly, which is null when the endpoint is not enumerated;
  `PresetMapper.cs:34-35` falls back to `Desired*`, still null. Nothing else writes `Desired*`.
- **Scenario:** preset holds "Wireless PRO RX" on strip 2; the RX is in its charging case at launch
  (it enumerates as storage — CLAUDE.md gotcha). Resolve → null. Any later setting change, or
  `Dispose` → `SavePreset`, writes `DeviceId=null DeviceName=null`. Plugging the RX in later:
  `ReattachDesiredDevices` has nothing to match. This is the 2026-09-20 "app erases its own memory"
  bug, surviving for the launch case.
- **Fix:** in `ApplyPreset`, set `Channels[i].DesiredDeviceId = cp.DeviceId` and `DesiredDeviceName
  = cp.DeviceName` *before* resolving, regardless of the result. Then add the same `Desired*` memory
  to `OutputViewModel` (unplugging the USB headset today permanently unbinds bus A —
  `OutputViewModel.cs:322-325` nulls the device, `PresetMapper.cs:47-48` has no fallback, no output
  reattach exists).
- **Test:** `PresetMapperTests` — a channel whose `SelectedDevice` is null but whose preset had a
  device round-trips the id and name.

### 1.7 Retention deletes the replay fixtures

- **Where:** `RecordingRetention` is built over `analysis/` and `recordings/`
  (`MainViewModel.cs:54`); `ReplayRig.DefaultDirectory` is that same `analysis/` folder
  (`ReplayRig.cs:60`); `Prune()` runs at every record start (`:1296`), i.e. 12 s after every launch.
- **Effect:** both `tools/baselines/*.json` reference stamp `20260809-092931`, 42 days before the
  retention commit — those WAVs were pruned on the first launch after it unless copied elsewhere.
  Any future fixture will go the same way after 28 days.
- **Fix:** keep fixtures outside the pruned folders (e.g. `analysis/keep/` that `ReplayRig` also
  searches, or exclude stamps referenced by `tools/baselines`), and record it as a gotcha.

### 1.8 Session records report zero for mics 4 and up

- **Where:** `MainViewModel.cs:221` constructs `SessionRecorder` (→ `SessionAggregator(channels.Count,
  …)`, `SessionRecorder.cs:58`) before `TryLoadInitialPreset()` at `:260`, when `Channels.Count` is
  `DefaultInputCount` = 3. `SessionAggregator.Tick` loops `i < _inputs` (`:161`) and `Build`
  returns 0 for the rest (`:209-211`). Runtime `InputCount` changes are never propagated.
- **Effect:** on the 6× Wireless PRO rig every session record shows inputs 4–6 with
  `LeaderPercent = DuckedPercent = MutedByGatePercent = 0` — plausible-looking and wrong.
- **Fix:** construct the recorder after the preset, and resize the aggregator on `ApplyInputCount`
  (or make it grow lazily).

### 1.9 The decisions CSV logs peak, not the RMS the selector compares

- **Where:** `MainViewModel.cs:233` passes `Channels[i].PostPeakDb`; `DecisionTrack.Sample`'s doc
  (`:73`) says the argument is "the post-fader level the selector actually compares", which is the
  smoothed RMS in `InputChannel.CurrentLevelLinear`.
- **Effect:** with this rig's 20–45 dB crest the `level_*` columns cannot be lined up against
  `PriorityActiveRms` / `SilenceFloorRms`, which is the whole reason the file exists.
- **Fix:** pass `20*log10(_engine.Inputs[i].CurrentLevelLinear)` — `StateSnapshot.cs:303` already
  computes exactly this for `envDb`.

### 1.10 Medium — reported by the review, not independently re-traced

- [ ] `SessionSummary.Scene` only updates when the alert set changes (`MainViewModel.cs:553-556`
      sits after the early-return at `:549`). Set it from `Scenes.SceneApplied`.
- [ ] Strips added at runtime get low-cut 0: `CreateChannel` (`:764-767`) never applies `_lowCutHz`.
- [ ] Old-preset low-cut migration is unreachable: `MixerPreset.LowCutHz` defaults to 80 when
      absent (`MixerPreset.cs:255`), so `preset.LowCutHz > 0` (`MainViewModel.cs:1200`) is always
      true and `Channels[0].HighPassHz` is never consulted; a saved `0` plus a channel value of 80
      comes up at 80. Use `int?`.
- [ ] Renaming a mic clears the scene keystroke by keystroke: `SettingsWindow.xaml:65`
      `UpdateSourceTrigger=PropertyChanged` + `CustomLabel` in `PersistedProperties` →
      `MarkCustomised`. `LapelOptions` is also not re-raised on rename.
- [ ] A refused mute/unroute is logged as the opposite action: the setters raise `PropertyChanged`
      on refusal (`ChannelViewModel.cs:95-99, 459-463`) and `DescribeChange` (`MainViewModel.cs:
      1123-1151`) reads the unchanged value → "X unmuted" in the action log, and the scene is cleared.
- [ ] Event-handler leak: `RouteToggleViewModel.AttachOutput` (`ChannelViewModel.cs:437`) subscribes
      to `OutputViewModel.PropertyChanged`; `DetachChannel` never unsubscribes.
- [ ] `CheckRecordingLimits` → `MustStopNow()` → `new DriveInfo().AvailableFreeSpace` at 30 Hz while
      recording (`MainViewModel.cs:1386`, `RecordingRetention.cs:52`). Throttle to ~1 Hz.
- [ ] `SessionStore.Save` (`:174`) uses `File.WriteAllText` — the truncate-in-place `PresetStore`
      just fixed — on a file rewritten every 2 min and read by offline tooling.
- [ ] `preset.json` has no schema version; `AutoMixMode` `1` now means Gate and used to mean Share,
      `Role == 0` is ambiguous. Add `Version`.
- [ ] `StateServer.Loop` swallows handler exceptions silently (`StateServer.cs:61`); a throw in
      `_stateJson()` returns an empty 200. Write to `AudioLog`.
- [ ] `DeviceWatcher.Bump` (`:163`) calls `_debounce.Change` after `Dispose` can have run.
- [ ] `ClearDeviceCommand` is bound nowhere; the Settings picker has no "(none)" item, so a strip
      cannot be unbound or made to forget its desired device.
- [ ] `VuMeter.OnRender` allocates unfrozen brushes/pens per render at 30 Hz per meter
      (`VuMeter.cs:393, 421-422, 479, 483, 521`).
- [ ] `ChannelViewModel.RefreshMeters` raises 16 names + 2 per route at 30 Hz per strip; the panel
      binds only `PostPeakDb`, `PostPeakHoldDb`, `RowState`, `IsSelected` (Diagnostics adds
      `CalibrationText`). Prune to what is bound. `RefreshHealth` also rebuilds `Alerts` every second
      while any alert message embeds a live seconds count.
- [ ] Plausible, needs a WPF run: `LapelIndex` setter (`MainViewModel.cs:405-406`) re-raises
      `LapelOptions`, replacing the ComboBox `ItemsSource` while `SelectedIndex` is bound TwoWay,
      which can push `-1` back and un-pick the lapel. The re-raise is unnecessary; remove it and check
      with `--open-all --log`.

---

## 2. Anker-era leftovers

### 2.1 Dead code — delete (verified: no remaining caller or binding)

- [ ] **Per-channel delay stage.** `Audio/DelayLine.cs` (whole file); `InputChannel._delayLine`,
      `DelayMs` (`:366-380`), the `new DelayLine(48000*2*2)` in `Start` (`:411`, 768 KB per channel
      start), `delay.ProcessInPlace` (`:768`); `ChannelViewModel.DelayMs` (`:153-166`) and
      `HasAdvancedSettings` (`:312-313`). Nothing sets the delay, so every sample is copied through
      the ring at offset 0 for nothing. Keep the `fifo == null || converted == null` early-return in
      `OnDataAvailable`.
- [ ] **Crest → "Mic clarity"** end to end: `AutoMixer` `CrestMin/CrestMax/QualityFloor/CrestMs`,
      `_crest`, `_crestCoef`, the per-tick block at `:141-163`, `AutoMixDiag.Crest`;
      `InputChannel.CurrentPeakLinear` (its only consumer) and `Clarity`; `ChannelViewModel`
      `HasClarity/ClarityBar/ClarityText` (raised 30 Hz, bound nowhere); `DiagnosticsLog.cs:46-48`
      "(clarity NN%)"; `StateSnapshot.cs:43,45` `crest`/`clarity` keys and `StateSnapshotTests:49`;
      the `peak` parameter of `InjectLevelsForTest` (no test passes it). Finding 1 says crest fails
      through DSP; on a DSP-free mic it is dominated by handling transients, so it is no proximity
      cue there either.
- [ ] **Comment residue for removed selectors:** `AutoMixer.cs:41-47` (reference-guided rationale)
      and `:57` (dangling "Which metric decides the leader this tick. Correlation outranks Natural…");
      `OutputViewModel.cs:158-166` (three orphaned blocks for StableHandoff/ReferenceGuided/
      PreferNatural); `MixerPreset.cs:97` (`// 0 Off, 1 Share, 2 Gate` — contradicts the enum);
      `InputChannel.cs:128` ("share leader"); `MainViewModel.cs:377-378` ("Advanced gear popup");
      `Scene.cs:17-18, 25-27` (Share / prefer-natural / Anker gating in enum docs — point at finding
      4 instead); `SceneTransformTests.cs:64-73` test named `…DisablesPreferNatural` that asserts
      nothing about it.
- [ ] `DiagnosticsWindow.xaml:73-125`: 10 `ColumnDefinition`s but headers/cells skip column 6 (the
      ref-corr column left a 60 px hole).
- [ ] **Dead Advanced-window members** (0 XAML refs each): `MainViewModel` `WindowWidth`,
      `WindowHeight`, `StripWidth`, `NonStripWidth`, `BaseWindowHeight`, `VbCableBannerHeight`,
      `ShowVbCablePrompt`, `DownloadVbCableCommand`, `DismissVbCablePromptCommand`,
      `ReplayPositionText` (still raised every tick in replay), `DismissAlertCommand`, `TopAlert`,
      `HasAlert`, `AlertSummary`; `ChannelViewModel` `SourceStereo/Left/Right`, `SourceSuffix`,
      `HighPassChoices/Text/Index`, `MeterFraction`, `BandStart/Width`, `FractionFor`,
      `InputPeakHoldDb`; `RouteToggleViewModel` `LedTooltip`, `IsDucking`, `RefreshLed`;
      `OutputViewModel` `LevelerLiftBar`, `LevelerState`, `LevelerSummary`, `AutoMixEnabled`,
      `CurrentAutoMixLabel`; converters `SeverityBrushConverter`, `NullToVisibilityConverter`,
      `MicDotConverter` declared in `SimpleWindow.xaml:24-26` but unused. `WindowSizingTests.cs`
      pins local copies of constants for a window that no longer exists — delete with them.
- [ ] Test fixture strings: `HealthMonitorTests.cs:13-14,169`, `VirtualDeviceFilterTests.cs:27`,
      `DeviceResolverTests.cs:20,74-76,109,180` — cosmetic; the `(N- …)` enumerator shapes are still
      real for Rode RX endpoints, so swap the names and keep the shapes.

### 2.2 Wired, speakerphone-only — remove with a stated risk

- [ ] **Bluetooth rule wording and name fallback.** Keep the enumerator-bus check
      (`AudioDeviceInfo.IsBluetooth`) and the persisted `WarnOnBluetoothMics`; drop the
      `PowerConf`/`Soundsync` name fallback (`HealthMonitor.cs:238-265`), the "contends with the
      other dongles" message (`:218-224`), the Settings help text "The Ankers must run over their
      2.4 GHz Soundsync dongles…" (`SettingsWindow.xaml:166-168`), and the comment at
      `MainViewModel.cs:524-531`. Removes `HealthMonitorTests.cs:156-159` and the S500 `InlineData`.
- [ ] **Flux-CV — decide on the first Rode capture.** One 512-pt FFT per 512 voiced samples per
      channel on the audio thread (`InputChannel.cs:486-541`), copied into `AutoMixer._cv` every
      tick and never read by selection. Finding 6b: every DSP-free mic reads ~0.29–0.33, nothing to
      separate. Its surviving claim ("rises on RF dropouts") was measured on a *Soundsync* walk test
      and never on a Wireless PRO link. If a Rode session shows `fluxCv` tracking `drops=`, keep it as
      diagnostic; if not, remove it together with `RfStats.FluxCv`, the Diagnostics column, the
      `/state` key, `naturalness.py`, and the `medianCv` check in `replay-baseline.ps1`. The
      480-frame replay constraint exists only for this path and becomes moot.

### 2.3 Keep — load-bearing on the Rode rig (reword provenance only)

- **RF drop-edge tally** (`InputChannel.SnapshotRfStats`, `TallyRfHealth`): written for Soundsync but
  *stronger* on a non-gating mic — exact digital silence mid-speech can only be an RF drop or a TX
  off. RODE-PRO-RIG.md item 8 builds on it. Gaps worth closing: not in `/state`, not in
  `SessionAggregator.InputSummary` (has `Underruns`/`ClippedSamples`, no `DropEdges`). Reword the
  comment at `:70`. Note `RfSilenceRms` also feeds `_lastSoundTicks` → the dead-mic health rules.
- **Absolute thresholds and hysteresis** (`SilenceFloorRms`, `PriorityActiveRms`, `PriorityHoldTicks`,
  `PriorityBreakInRms`, `HandoffHoldTicks`, `HandoffHysteresis`, `SpeechTargetDb = -24`): fitted on
  Anker captures, but finding 6a says the hold is exactly as necessary on Rode and finding 8 shows
  the thresholds are what bites when gain staging is off. Retune offline per RODE-PRO-RIG items 3–4.
- **Share→Gate preset migration** (`MixerPreset.MigrateMode`): the live `preset.json.bak` may still
  hold `2`. Cheap and tested.
- **`DeviceResolver.EnumeratorPrefix`**: Windows behaviour, not Anker's; Rode RX endpoints show the
  same `2-`/`3-` residue.
- **Capture-stall watchdog**: generic; only the comment at `AudioEngine.cs:24` names Ankers.
- **"Never a gate" rationale comments** (`BusLeveler.cs:81`, `InputChannel.cs:170-172`,
  `SceneTransform.cs:102`, `MainViewModel.cs:475`): the *why* behind the leveler's idle-hold, the
  fixed-band low-cut and Singing forcing Off. Losing them invites re-deriving finding 4.
- `IsPriority` vs `Role`: deliberate (durable vs runtime), not a leftover.

### 2.4 Tools

Delete:
- [ ] `tools/replay_share.py` — replays the removed Share gain formula and the *pre-fix* flux scale;
      hardcodes stereo `reshape(-1, 2)` so split-strip mono captures are misread.
- [ ] `tools/replay_natural.py`, `tools/review_natural.py` — replay/judge Prefer natural;
      `review_natural.py:6` hardcodes `C:\Users\FreeGrace\…\AudioMixer.log`.
- [ ] `tools/scene4.py`, `tools/scene5.py`, `tools/spectro.py` — hardcoded 2026-06 Anker stamps and
      In4/In5 labels; stereo reshape; `spectro.py` throws `KeyError` on a 3-mic session.
- [ ] `tools/voice_quality.py` — exists only to reproduce the finding-3 inversion (recorded).
- [ ] `tools/RefCorr/` — offline validation for Match lapel (removed).
- [ ] `tools/baselines/presentation.json`, `singing.json` — recorded with `preferNatural: true` on
      5 Ankers; the current engine can never reproduce them, and their source WAVs were likely
      pruned (1.7). `replay-baseline.ps1` writes a fresh baseline when the file is absent.
- [ ] `BROADCAST-MODE.md` — plan for a firmware feature Anker removed; its own header says delete.
      Check §2.3 (Standby vs recorder) is resolved before deleting; move to ROADMAP if not.

Update:
- [ ] `tools/replay-baseline.ps1:56-58` drops `--advanced` (the app silently ignores unknown flags,
      so fixtures already run under SimpleWindow and the comment is false); `:130` reads
      `outputs[$o].preferNatural`, which `/state` no longer emits.
- [ ] `tools/gate_rate.py:3-5,20-21`, `tools/naturalness.py:3,27`, `tools/AnalyzeInputs/Program.cs:
      5-9,15,38-41` — Anker labels/docstrings; `AnalyzeInputs:15` mirrors the crest constants
      being deleted in 2.1.
- [ ] `tools/audio-device-diag.ps1:30` default `-Filter "Anker"` now matches nothing; `:100-108`
      summary text is Soundsync-specific.

Keep as-is: `live_wav.py`, `comb_test.py`, `find_singing.py`, `singing_vs_speech.py`, `RxProbe`,
`VolProbe`, `device-identity.ps1`, `build-readme.mjs`.

### 2.5 Docs

- [ ] **README.md** describes the retired UI as current: per-channel delay and clap test (`:3,13,
      18-19,94-105`), Off/Share/Gate + strength + stable hand-off (`:16,111-119,167`), per-output
      record buttons (`:20,89`), toolbar input picker (`:90`), gear-popup priority and "more than one
      priority mic" (`:123-130`), the entire Match lapel / Prefer natural sections (`:132-163`),
      architecture line with a delay buffer and no side split / low-cut / leveler (`:175`), "500 ms
      cap" (`:187`), clap-test file row (`:197`), 150 MB vs 68 MB exe size (`:207` vs `:49`).
      `docs/screenshot.png` shows the Advanced window — re-shoot with `--shots`. Nothing mentions
      scenes, Checks/Diagnostics/Settings, the leveler, split receivers, `--replay`, retention.
- [ ] **README.html / RODE-PRO-RIG.html** are committed build artifacts (`build-readme.mjs:1-3`
      says do not hand-edit) with no CI step regenerating them. Gitignore them or generate in CI.
- [ ] **RODE-PRO-RIG.md**: `:125` lists `AutoMixer.RefSpeechRms` (does not exist); `:380-387`
      settings rows for Stable hand-off / Prefer natural / Match lapel; `:127` BROADCAST-MODE ref;
      `:200-211` progress log.
- [ ] **ROADMAP.md**: `:73,116` Advanced window resizable; `:190-191` `--simple`/`--advanced`;
      `:282-300` "pin the cleanest Anker"; `:413-431` Soundsync half-link / auto re-add Anker
      (superseded by `Desired*`); `:460-476` verify Share weighting / Prefer natural / Match lapel;
      `:511-518` green LED (resolved); `:520-528`, `:659-662` Mic clarity (moot after 2.1);
      `:664-679` Anker evidence/ceiling; `:682-689` "Recently shipped" lists removed features.
      Keep `:372-404` (Broadcast ❌ history), `:354-371`, `:575-597`.
- [ ] **CLAUDE.md** drift: layout lists `Controls/VuMeter.xaml` (only `VuMeter.cs` exists),
      `DelayLine.cs`, `DelayAnalyzer.cs`; `:103-113,580-581` tool lists include RefCorr/
      replay_natural/replay_share/scene4/5; `:145,193` pipeline still has DelayLine and a 0–1000 ms
      delay range; `:234-237,241-268,306-321` describe Stable hand-off, selection rules 2–3,
      quality-weighted Share / `SelWeight` / `NatCvGood` and the "Mic clarity bar in the gear popup"
      as live; `:419` lists `/state` fields (`strength/stable/reference/preferNatural/refCorr/
      referenceInput`) that are gone; `:343,863-864` say replay-baseline passes `--advanced`;
      "retention deletes oldest-first whenever free space drops under 20 GB" — it prunes only at
      record start, never continuously and never after `MustStopNow`. Also `InputChannel`'s
      per-output buffer is 500 ms (`CreateOutBuffer`) where CLAUDE.md says ~200 ms.

---

## 3. Project hygiene

- [ ] `publish.ps1:1` `#requires -version 5` but `:25,32,41` use the `? :` ternary, which parses
      only on PowerShell 7. Change the header or replace the ternaries. `:37` shadows `$args`.
- [ ] No CI runs `dotnet test` (`.github/workflows/release.yml` builds on `v*` tags only). Add a
      `ci.yml` on push/PR running `dotnet test AudioMixer.sln` on `windows-latest` (tests target
      `net8.0-windows` + WPF). CLAUDE.md already records a crash that shipped for weeks because of
      this.
- [ ] `<Version>1.0.0</Version>` is hardcoded; the release workflow never passes
      `-p:Version=${GITHUB_REF_NAME#v}`, so a `v1.2.0` tag ships an assembly reporting 1.0.0.
- [ ] `.claude/` is gitignored (`.gitignore:36`), so `.claude/skills/session-review`, which CLAUDE.md
      tells sessions to invoke, is not in the repo and is absent from a fresh clone. Un-ignore
      `.claude/skills/` or move the procedure somewhere tracked.
- [ ] `.gitignore`: `bin/`/`obj/` listed twice (`:2-3,42-43`); `tools/scene.png` not ignored while
      `tools/spectro.png` is.
- [ ] `TreatWarningsAsErrors` not set in any project; no `Directory.Build.props`, so the four tools
      projects each pin NAudio separately and are not in the `.sln`, so nothing ever builds them.
- [ ] Tests: `PresetMapperTests.EveryChannelFieldWrittenHereHasAnAllowlistEntry` checks a hand-typed
      list of six names (omits `Role`, `SelectedDevice`, `Routes`) — reflect over `ChannelPreset`
      instead; `DecisionTrackTests.SamplingIsThrottledToTenHertz…` is wall-clock based
      (`Task.Delay(140)`) and will flake on a loaded runner.
- [ ] Untested pure logic worth a test: `DiagnosticRow.Build` state precedence,
      `OutputViewModel.RefreshVerdict` (the three `winner = -1` causes), `DeviceList.Sync`, the
      `HealthMonitor` `.stalecal` rule, `SessionRecorder.Tick` >5 s gap rule, the
      `RecordingRetention` oldest-first loop (needs an injectable `FreeGb`), the guard-refusal →
      action-log path (1.10), and above all a guarded `SceneController.Apply` (1.4).
- [ ] `MainViewModel` (1413 lines) — extractions that would pay for themselves: device binding
      (`:855-1058`, where 1.6 lands), recording (`:1279-1399`, where 1.1/1.5/1.9 land), alerts
      (`:520-611`), and a `PresetApplier` mirroring `PresetMapper` so the round-trip is testable.
      The VB-CABLE / window-sizing block can simply go (2.1).
- [ ] Duplicated: `string.IsNullOrWhiteSpace(c.CustomLabel) ? c.Label : c.CustomLabel` appears nine
      times across `MainViewModel`, `DiagnosticRow`, `SessionRecorder` — one `DisplayName` on
      `ChannelViewModel`.

---

## Suggested order

1. 1.1, 1.2, 1.3 — three small engine fixes, each isolated.
2. 1.4, 1.5, 1.6 — the ones that bite during a service.
3. 2.1 dead code (delay stage, clarity, Advanced members) and 2.4 tool deletions — mechanical,
   large diff, do it in one commit so the tests that pin removed keys go with it.
4. 1.7, 1.8, 1.9 — the recording/session-record fixes, then re-record a Rode baseline.
5. Docs (2.5) and CLAUDE.md optimisation pass, once the code has settled.
