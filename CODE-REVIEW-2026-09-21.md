# Code review 2026-09-21 — bugs, Anker leftovers, hygiene

Working document for fixing from the .NET dev machine. Read-only review of `b8c8aae` (no build or
test run was possible where it was written, so every item is from the source). Tick items off as
they land; delete this file when it is empty, the way RODE-PRO-RIG.md is meant to go.

Line numbers are against `b8c8aae` and will drift as you edit — search for the identifier instead.

---

## 1. Confirmed bugs

Each of these was traced through the code end to end. Ordered by how badly it hurts a live service.

### 1.10 Medium — reported by the review, not independently re-traced

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
- [ ] `preset.json` has no schema version; `AutoMixMode` `1` now means Gate and used to mean Share,
      `Role == 0` is ambiguous. Add `Version`.
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

Update:
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
---

## 3. Project hygiene

- [ ] `<Version>1.0.0</Version>` is hardcoded; the release workflow never passes
      `-p:Version=${GITHUB_REF_NAME#v}`, so a `v1.2.0` tag ships an assembly reporting 1.0.0.
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
