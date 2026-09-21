# Code review 2026-09-21 — bugs, Anker leftovers, hygiene

Working document for fixing from the .NET dev machine. Read-only review of `b8c8aae` (no build or
test run was possible where it was written, so every item is from the source). Tick items off as
they land; delete this file when it is empty, the way RODE-PRO-RIG.md is meant to go.

Line numbers are against `b8c8aae` and will drift as you edit — search for the identifier instead.

---

## 1. Confirmed bugs

Each of these was traced through the code end to end. Ordered by how badly it hurts a live service.

### 1.10 Medium — reported by the review, not independently re-traced

- [ ] Renaming a mic clears the scene keystroke by keystroke: `SettingsWindow.xaml:65`
      `UpdateSourceTrigger=PropertyChanged` + `CustomLabel` in `PersistedProperties` →
      `MarkCustomised`. `LapelOptions` is also not re-raised on rename.
- [ ] `preset.json` has no schema version; `AutoMixMode` `1` now means Gate and used to mean Share,
      `Role == 0` is ambiguous. Add `Version`.
---

## 2. Anker-era leftovers

### 2.2 Wired, speakerphone-only — remove with a stated risk

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
### 2.5 Docs

---

## 3. Project hygiene

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
