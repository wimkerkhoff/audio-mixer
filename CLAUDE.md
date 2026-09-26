# AudioMixer

A Windows desktop audio mixer: 1–10 configurable inputs (default 3) → 2 configurable outputs, with
per-channel level, mute, low-cut, routing toggles, VU meters, recording, and presets. Built to send a
mix to a headset AND Zoom/OBS (via VB-CABLE) simultaneously, with an automixer that keeps one mic
open at a time across distributed body-worn and room mics. Used by volunteers running church services
alone, so the app should run on autopilot and report what it cannot fix.

## Working on this rig — rules

The mixer is a **live tool** on the operator's machine, often mid-service. These rules each cost
something to learn.

- **Never delete, empty, truncate, overwrite or move a log or a recording without the operator's
  explicit permission.** That covers `%TEMP%\AudioMixer*.log`, the crash log, and everything under
  `Documents\AudioMixer\` (recordings, `analysis\`, `sessions\`, transcripts). They are the only
  evidence a later diagnosis has: emptying the live log "to make a test run easier to read" destroyed
  the one record of a 2026-09-23 capture failure, and its trigger could never be identified. Find a
  run's lines by its `=== AudioMixer started` banner, not by clearing the file. Repairing recordings in
  place (`tools/wavfix.py --apply`) also needs an OK first, with the dry run shown. The app's own
  retention (`RecordingRetention`) is the one deliberate exception.
- **Before restarting the mixer, check that nothing is on air** (`obs64` running is a hint; a live
  stream does not show as a separate process, so ask). Standing permission to restart exists, but not
  mid-stream.
- **Close it, never force-kill it.** `$p = Get-Process AudioMixer; $p.CloseMainWindow();
  $p.WaitForExit(20000)`. A kill leaves every recording in progress with a zero-length WAV header
  (82 files broken this way in one evening; `tools/wavfix.py` repairs them). Relaunch with the same
  arguments it had — read them from `Win32_Process` first; normally `--log --state 7077`.
- **Test without touching the live mixer.** `--replay` has its own single-instance mutex, no autosave
  and no output devices, so it runs beside a live session; give it a preset *copy* (`--preset=`) and
  its own log (set `TEMP`/`TMP` for that process). `--shots` + `--open-all` renders every window.
- **Measure before concluding.** Tonight-style mistakes, each made once:
  - a live meter measures the *room*, not a gain change — read the endpoint back (`tools/VolProbe`);
  - a calibration median spanning a gain change averages two rigs — reset, then re-measure;
  - `-120` is the app's silence clamp and what a strip reads for seconds after a restart — sample
    twice before calling a channel dead;
  - underruns: read `/state` `underruns` twice, a few seconds apart, and only for routed pairs;
  - crackle after the fact: count exact-zero runs per minute in `mix-*.wav` (see gotchas);
  - UI responsiveness: poll `/state` (its handler runs on the UI thread) and take the max latency.
- **Nothing that can wait may run on an audio thread** — no disk flush, device call or lock the UI
  also takes. A 10 s WAV-header flush from `WriteSamples` caused 24 underruns/min (see gotchas).
- **Never tune the selector from a live impression** — capture with labels, replay offline first
  (see "Validating a selector change").
- Git: commit straight to `main`; push only when asked.

## The rig

- **Now (2026-09-23):** two **RØDE Wireless PRO** receivers on USB, each carrying two transmitters in
  **Split** mode (four strips), plus a **wired classic Rode lapel** on the Realtek aux jack for the
  presenter, used as the **priority** mic. The Wireless PRO is DSP-free and does not gate (finding 5).
  It is *body-worn*: it covers people, not a room, so mic count and placement do the work DSP used to
  pretend to do. Gain: each receiver at **0 dB** (applies to both its transmitters), Windows endpoint
  **+3 dB**; the wired lapel's only lever is its Windows endpoint (~16 dB).
- **Outputs:** bus A = VB-CABLE → Zoom/OBS (the remote attendees — the listener that matters);
  bus B = the operator's USB headset.
- **Usage:** teaching (one talker), prayer meetings (turn-taking, often led from the lapel),
  congregational singing (the automixer's one-talker assumption inverts — the Singing mode).
- **History:** until 2026-08-30 the room mics were **4× Anker PowerConf S500 speakerphones** on
  2.4 GHz Soundsync dongles, ~60 ft room, furthest mic ~50 ft from its dongle. Their AGC, noise
  suppression and gating to digital silence explain findings 1–4 and most device gotchas; they were
  returned once Anker confirmed the only mitigating control had been removed from the firmware.
- Per-meeting setup guidance lives in session memory, not here.

## Stack

- **.NET 8** + **WPF**, MVVM (view model per channel + main)
- **NAudio** 2.2.1, WASAPI **shared mode** everywhere (exclusive would lock Zoom out of the headset)
- **System.Text.Json** for presets and session records
- Offline analysis (`tools/*.py`): `pip install numpy scipy soundfile matplotlib praat-parselmouth`,
  plus `faster-whisper` for `tools/transcribe.py` (runs on CPU here: the Quadro's CUDA path needs
  NVIDIA's cuBLAS/cuDNN runtime, not installed)

## Where things are

```
AudioMixer/          the app — Audio/ (engine, graph, automixer, replay), Services/ (PURE rules and
                     persistence: HealthMonitor, PresetMapper, DeviceResolver, StateSnapshot…),
                     ViewModels/, Views/ (SimpleWindow = the mixer, Checks, Diagnostics, Settings)
AudioMixer.Tests/    xunit, pure logic only (no devices, no WPF) — plus XamlResourceTests, which
                     reads markup as text
tools/               offline analysis and diagnostics — validate selector changes HERE first
  AnalyzeInputs/     C#: replays selector metrics over per-mic diag WAVs
  RxProbe/           C#: captures 1+ endpoints beside the live mixer — verify a split receiver
  VolProbe/          C#: list/set capture endpoints' Windows gain (no args = list)
  device-identity.ps1  serial vs port-derived identity of each USB audio device (PnP walk)
  audio-device-diag.ps1  WASAPI/BT/dongle enumeration, half-link detection
  replay-baseline.ps1  golden-baseline regression over a replay fixture
  wavfix.py          repair WAV headers a kill left at 0 frames (dry run by default)
  transcribe.py      local speech-to-text of a recording (faster-whisper; keep its VAD off)
  gate_rate.py / naturalness.py / live_wav.py / singing_vs_speech.py / find_singing.py / comb_test.py
ROADMAP.md           open work and the decisions behind it — not a spec of what IS
```

Runtime files:

| What | Where |
| --- | --- |
| Preset (+ `.bak`) | `%APPDATA%\AudioMixer\preset.json` |
| Log (opt-in `--log`), crash log (always) | `%TEMP%\AudioMixer.log`, `%TEMP%\AudioMixer.crash.log` |
| Bus mixes `mix-A/B-<stamp>.wav` | `Documents\AudioMixer\recordings\` |
| Per-mic `diag-input<N>-<stamp>.wav`, `decisions-<stamp>.csv` | `Documents\AudioMixer\analysis\` (fixtures in `analysis\keep\`) |
| Session records `session-<stamp>.json` | `Documents\AudioMixer\sessions\` |
| Live state | `http://127.0.0.1:7077/state` (with `--state`) |

## The UI

**One mixer window** (`SimpleWindow`, fixed 330 px wide, `SizeToContent="Height"`, one row per mic):
the **Speaking | Singing** toggle, mic rows (state stripe, meter with the target band, level, mute,
bus A/B buttons that light green when the automixer has that mic on that bus), an on-air card per bus
whose ON AIR / MUTED label *is* its mute button, and one toolbar. **Checks** lists every warning and
opens itself only when something needs attention. **Diagnostics** is "why this mic", the session so
far, calibration and devices. **Settings** holds the rig: the priority mic, each strip's device and
split side, per-bus automix mode, the leveler, the global low-cut, picker filters. `App` owns
`MainViewModel` and disposes it in `OnExit`. The Advanced window was retired 2026-09-20: two places
to mute a mic and two device pickers cost real confusion.

**Scenes were removed 2026-09-23 — do not rebuild presets without solving this first.** Four buttons
(Standby/Teaching/Prayer/Singing) each rewrote every channel's mute, route and priority and every
bus's mode. The operators could not remember what each did, and a scene silently undid their own hand
mutes — twice in one evening. They are trusted with mute and A/B and mix in OBS. What survived is the
knowledge they cannot re-derive:

- **Speaking | Singing** — both buses Gate, or both **Off** (singing has no single talker to follow).
  Singing lights **purple** (the one colour with no other meaning here) because it is the mode you
  can forget to leave; Checks warns after **15 min** in it (`singing.long`, time alone — singing is
  not detectable from audio). It deliberately does **not** touch priority or routing: `AutoMixer`'s
  Off branch sets unity gain and returns *before* the priority logic, so an armed lapel cannot duck
  the congregation (the 2026-07-05 failure) and is still armed when speaking resumes. That early
  return is the only thing preventing it — pinned by
  `AutoMixerTests.OffIgnoresAnActivePriorityMic_SoSingingCannotDuckTheRoom`. If the buses disagree
  (per-bus mode in Settings), **both sides go amber**.
- **Priority mic** — set once in Settings (`LapelIndex`). To leave the lapel out of a meeting,
  **mute it**: a strip's level is measured after its mute gate, so a muted priority mic never reads
  as speaking and cannot duck anyone.

**Mutes persist**, strip and bus (`OutputPreset.Muted`, since 2026-09-23; Checks flags a muted bus).
**The route guard is gone** (2026-09-23, operator's call): it refused a tap that would leave a bus with
no live mic, and fought trusted operators. What still catches an emptied bus is after the fact:
`out<N>.silent` goes Critical after 10 s of bus silence while any input has sound — deliberately quiet
in an empty room.

## Audio architecture

```
per channel:  WasapiCapture → resample to 48 kHz stereo float32 → side split (L/R/stereo)
              → peak + analysis-recorder taps → low-cut → mute → gain → post peak
              → level/flux/RF measurement → per-output automix gain → per-output feed buffer
per bus:      MixingSampleProvider → leveler + limiter → peak tap → [recorder] → volume → WasapiOut
```

- **Side split first**: everything downstream (meters, analysis recorder, automixer) must see one
  transmitter, not a blend. **Low-cut after the analysis tap**, so diag WAVs stay unprocessed and the
  offline tools never measure our own filter.
- **The bus leveler is the ONLY dynamics stage, and may never move upstream of the mixer.** The
  automixer picks by smoothed per-channel RMS; any compression ahead of it flattens the level
  differences that encode *which mic is closest* — exactly what a transmitter's AGC does (why
  GainAssist must be off). Load-bearing constraints: make-up lift capped at 12 dB
  (`MakeupCeilingDb` — the room floor is acoustic HVAC and speech-band S/N is ~15 dB, so every dB of
  lift is a dB of rumble); below `IdleFloorDb` the gain **freezes**, never falls (no path lowers gain
  while idle, so it cannot punch holes like the speakerphones did) — calibrate the floor per room, as a
  bus summing open mics can sit above the −45 dBFS default; Off is a true bypass. Settings live on
  `OutputBus.Leveler` so they survive `RestartOutputBus_NoLock`.
- Internal format **48 kHz stereo float32**; each bus resamples once to its device rate. Inputs and
  outputs run on independent clocks; per-channel feed buffers absorb drift (`DiscardOnBufferOverflow`).
- Meters update at ~30 Hz from values latched on the audio thread and polled by a UI timer — never
  marshal per buffer. Output **Volume** sits after the peak/recorder tap: it trims what the device
  plays, not meters or recordings. Recording is per bus (`MixRecorder`) plus per mic (diag).
- **Automix gain is applied after the meter and analysis taps**, so meters and diag WAVs show the
  pre-automix level — intentional; don't move the tap.

### Automixer

Per output, keep only the mic closest to the active talker; several distant mics summing one voice
give comb "echo", a raised floor and reverb. Static delay compensation is no substitute (per-talker
offset isn't fixed). `AudioEngine.AutoMixTick` (~100 Hz, off the audio threads) smooths each
channel's latched RMS (attack 8 ms / release 250 ms), picks a leader per output, and writes gains
lock-free; `InputChannel` ramps them within a buffer.

| Mode | Rule | Use |
| --- | --- | --- |
| Off | unity | singing |
| Gate | winner-take-all, non-leaders hard-muted | everything else |

- **Selection is level only**, with an unconditional **leader hold** (`HandoffHoldTicks` ~200 ms,
  `HandoffHysteresis` ~3 dB): a talker's pauses would otherwise hand the bus to a neighbour (finding
  1's "far mic wins"). Gate's hold can clip the first syllable of a fast interjection.
- **A challenger must keep its margin** for a time set by how far ahead it is: ~500 ms at +3–6 dB,
  ~250 ms at +6–10, ~100 ms beyond (`RequiredSustainTicks`), reset if the lead lapses. Within a few
  dB the talker is between mics and either serves — on 2026-09-26 the top two sat a median 3.2 dB
  apart and a room mic's median tenure was 0.4 s. It also stops a one-tick spike taking the bus (the
  old known gap). Added 2026-09-26 from the decisions CSV at the operator's request, **before** a
  labelled replay — the first capture replayed against it is its real test.
- **Removed 2026-09-20:** Share (attenuated non-leaders, so one voice through several mics still
  combed), its strength slider, the "stable hand-off" switch, "Match lapel" and "Prefer natural" —
  all Anker-era; on matched DSP-free transmitters they could only hurt (finding 6). Flux-CV is still
  computed and still diagnostic: it rises on RF dropouts.
- **Flux-CV** (`InputChannel.CurrentFluxCv`): 512-pt FFT per voiced 512-sample window, **accumulated
  across capture buffers** — shared mode delivers ~480-frame buffers, so a per-buffer FFT almost never
  ran and the value froze (fixed 2026-07-26; `FluxEma` 0.01 for ~94 windows/s). Live scale ~0.35–0.5
  matches the offline `flux_cv`, so replays are faithful.
- **Priority mic** (`IsPriority`): always full level, out of the competition, and while *active*
  (`PriorityActiveRms` ~−40 dBFS) it hard-mutes the room mics, else the voice reaches the bus via the
  lapel and a delayed room mic and combs. Exactly one, set only by `LapelIndex`, which sets `Role` and
  `IsPriority` together; the "Clear priority" fix routes through it and a preset load derives
  `IsPriority` from `Role`, so the picker never names a mic that ducks nothing. **Hazard:** an unused,
  unmuted priority lapel crossing −40 dBFS (a bump) ducks every room mic off the stream — mute it.
- **Priority hangover** (`PriorityHoldTicks` ~1.2 s): the duck used to release in a presenter's
  sentence gaps (the 250 ms release needs ~575 ms to fall below −40) — measured 2026-08-30, 13
  hand-offs in 40 s; 0 after. The hold is **broken immediately** by a non-priority mic above
  `PriorityBreakInRms` (~−50 dBFS) **and** +6 dB over the lapel (`BreakInOverLapel`), so an
  interjection is not swallowed. The absolute test alone fired on the presenter's own voice: on
  2026-09-26, after a +9–12 dB endpoint raise, a room mic heard him at −35 (lapel −30), its residual
  stayed over −50 as the lapel dipped under −40 in each pause, and the bus went lapel ↔ room mic ~48
  times a minute (1.9/min at 06:40–06:55; room discussion also grew, unlabelled). In a pause both
  envelopes decay together, so the presenter's residual stays under the lapel; a room talker reads 10–20 dB over the lapel's pickup of them, and
  one the lapel hears nearly as well is carried by the lapel (always at unity). Placement still
  matters: a mic on his own table (−28 vs a real interjection at −43) out-levels anyone else.
  Same caveat as the sustain rule: built before a labelled replay.
- **Split receivers**: one WASAPI endpoint, TX1 left / TX2 right. Bind it to two strips
  (`ChannelSource.Left`/`Right`); bound whole it reaches the bus hard-panned as one blended channel
  the automixer cannot arbitrate. Device claims are per **side** (`DeviceResolver.Claim`/`IsFree`,
  keys `id|1`/`id|2`; Stereo takes the bare id) — too strict and the second transmitter vanishes from
  the preset on load, too loose and two strips push the same audio twice.
- **What shows the automixer:** the row stripe goes green ("live now") from `IsAutoMixActive`; each bus
  button lights green when that mic leads that bus. `IsDucking` is in `/state` and Diagnostics only.
  Each hand-off is logged by `LogAutoMixSelectionChanges`.

## Testing without a room full of people

- **Replay** (`--replay[=STAMP] --seek=MM:SS --for=MM:SS --speed=N --loop`) feeds the inputs from a
  session's `diag-input*.wav` through `IWaveIn`, so everything downstream runs unmodified.
  Load-bearing: **480-frame** buffers (at 512+ the flux accumulation is bypassed and you test different
  code), and **one clock pumps every source in lockstep and drives the automix tick** — deterministic
  and speed-independent. Sandbox: own mutex, no autosave, no output devices by default.
- **Golden baselines**: `tools/replay-baseline.ps1 -Name <fixture> [-Update]`, in `tools/baselines/`;
  record and check at the same `-Speed` (1–2; higher drops audio). **Each fixture owns its preset**
  (`<Name>.preset.json`, passed with `--preset`), and the baseline records its hash — `--replay` does
  NOT sandbox preset loading, and until 2026-09-21 every fixture silently inherited the live preset
  (the `presentation` fixture re-run at its own recording commit gave 60 hand-offs vs its stored 14),
  so a changed fixture preset is reported as configuration drift before numbers are compared.
  **No baselines are committed yet**: `tools/baselines/` does not exist, and the two original
  fixtures' WAVs were pruned by retention. Recording one is on the roadmap.
- **Binding errors** are swallowed at runtime; a clean build proves nothing about the UI. `--log`
  logs them (`BindingErrorListener`); `--open-all` opens every window. Zero binding errors is also
  what a window that rendered garbage reports — look at it.
- **`--shots[=DIR]`** renders every window to PNG via `RenderTargetBitmap` and exits — works occluded,
  off-screen or with the workstation locked (a screen grab returns the lock screen; `PrintWindow`
  returns blank for WPF).
- **Unit tests** cover pure logic (health rules, automixer, allowlist, recorder, mapping). Anything
  needing a device or window is verified by a replay run. **CI** (`.github/workflows/ci.yml`) builds
  and runs `dotnet test` on every push and pull request since 2026-09-21; before that only
  `release.yml` existed (build on a version tag, no tests), which is how a window that crashed on open
  shipped and stayed broken for weeks. CI cannot catch a runtime binding or layout fault — only
  `--open-all --log` and `--shots` can.

## Conventions

- PascalCase types/methods, `_camelCase` fields, camelCase locals. Wrap this file and comments at
  ~100 columns. **Comments explain non-obvious WHY only.**
- **Threading**: NAudio callbacks run on their own threads; never touch WPF objects there — poll
  atomic state from a UI timer. Device enumeration (~3.5 s for 30 endpoints here) never on the UI
  thread. Engine start/stop may block; audio callbacks are never async.
- **Logging**: `Trace` alone is not a diagnostic (no listener, goes nowhere) — every failure path
  worth reading also writes `AudioLog`. File logging is opt-in (`--log` / `AUDIOMIXER_LOG`), ~1 line/s,
  and starts with a banner naming the exe, version (`1.0.0+<git-sha>`) and build time. **Crashes are
  always logged** to `%TEMP%\AudioMixer.crash.log` by `App.InstallCrashHandlers`, because the run that
  matters is the one nobody passed `--log` to. A silent preset-load failure is the worst case — it
  looks exactly like an unconfigured mixer.
- **Gain calibration** (`CalibrationHistogram`, per input): 1 dB bins of every buffer's RMS, voiced
  separately, at the **same post-fader tap the automixer's absolute thresholds read**. Shown as
  `cal=[speech= floor= n=]` in the log, `speech`/`floor` in Diagnostics (green within ±3 dB of the
  **−24 dBFS target**), `speechDb`/`floorDb`/`calAgeMs` in `/state`. Deliberately **cumulative**, so
  it must be **reset after every gain change** — a median spanning one averages two rigs (a lapel read
  −40 while live at −21, and acting on it over-drove it to −3.8 dBFS peaks). Resync and changing a
  strip's split side reset it too. A peak meter cannot do this job: a DSP-free mic's crest is 20–45 dB.
  A room mic's median is whoever it hears — with only the lapel talking it is the presenter across
  the room (−41 on four healthy mics, 2026-09-26) — so Checks' level warning judges room mics from a
  second histogram fed only after the mic has held the bus for 1 s (`LeadSpeechDb`); the priority
  mic, being worn, is judged on its whole median. Diagnostics and `/state` still show the whole one.
- **RF-link health** on the per-input log line: `rf=[lvl= voiced= silent= drops=]` — a dropping link
  shows exact-silence gaps mid-speech plus high flux-CV while voiced; raw counts only, classify later.
- **`/state`** (`--state[=PORT]` / `AUDIOMIXER_STATE`, loopback, read-only): per channel levels,
  routes, mute, automix gains, calibration, `envDb`, `fluxCv`, `endpointGainDb` (cached per device
  refresh — reading endpoints costs seconds on the UI thread), `clippedSamples`, `underruns` (per bus,
  routed-and-live pairs only); per output mode, `muted`, leveler, `winner`; alerts; a `replay` block.
  Shape pinned by `StateSnapshotTests` — renaming a key breaks the offline tooling silently.
- **Autosave** is driven by an **allowlist** (`PersistedProperties`) mirroring what `PresetMapper`
  writes; tests pin that every preset field (channels and outputs) can trigger a save. Never a
  blocklist: the meter tick raises display properties 30×/s, and a blocklist let it reset the 500 ms
  debounce forever, so only a clean exit saved.
- **Single instance**: a named mutex; a second launch raises the first window and exits.

## Build & run

```powershell
dotnet build            # the running mixer locks bin\Debug — close it first, or build -c Release
dotnet test AudioMixer.Tests -c Release
dotnet run --project AudioMixer -- --log --state
```

## External dependencies

- **VB-CABLE** (https://vb-audio.com/Cable/): after install + reboot, "CABLE Input" is a render device
  (bus A) and Zoom/OBS select "CABLE Output". If missing, Checks warns with a download button.
  Detection matches the interface name `(VB-Audio Virtual Cable)`, never the vendor "VB-Audio" —
  Voicemeeter carries that too (16 of this machine's 19 VB-Audio endpoints), so a vendor match reported
  VB-CABLE present on any machine with Voicemeeter.

## Measured findings — dead ends, don't re-litigate

Each is a *negative* result you cannot infer from the code. Read them before proposing a
mic-quality metric or a selector change.

### Current rig (DSP-free Rode)

**5. A DSP-free mic does not gate — but its noise floor is not recoverable by filtering.** 2026-08-23
(`tools/gate_rate.py`): a Wireless PRO vs two Ankers, same room and speech — digital silence **0.0%**
vs 4.1/7.1% (the Ankers lost 21.6 s and 36.9 s to gate holes). **Negative half:** the Rode's
speech-band S/N is **15.3 dB** vs 28–33 (the Ankers' is an artifact of gating the floor), and a
high-pass does NOT close it: 60–150 Hz cutoffs remove 0.9–8.2 dB of sub-100 Hz rumble but move
100 Hz–8 kHz S/N by only **+0.1–0.2 dB**, because the floor's bulk is at 80 Hz–1 kHz, inside the
voice. Run the low-cut at 80–100 Hz for rumble and handling — never as an S/N fix. No mains hum.

**5b. That floor is ACOUSTIC, not the aux path.** 83% of its energy is below 1 kHz and only 5.3% above
4 kHz; converter noise is hiss (flat, dominating the top), so this is HVAC and structure-borne rumble
the S500s had been suppressing. USB instead of aux saves a few dB, not fifteen; the real lever is
**proximity** (+6 dB per halving of distance). To split the two: record the aux with transmitters off,
then on in a quiet room.

**6. On matched DSP-free transmitters, level is a true proximity cue and flux-CV stops
discriminating.** Identical capsules mean a level difference is distance, so Gate on smoothed level
with the leader hold is right — and the hold is still necessary. Flux-CV reads ~0.29–0.33 on every
mic (nothing to separate); "Prefer natural" hard-gated the only room mic hearing the talker live
(2026-08-23). Both it and "Match lapel" (which engages only while the lapel speaks) were removed.

**7. Mixed device types cannot share one selector.** 2026-08-30, 2 Ankers + a Rode pair on one Gate
bus: Ankers rows apart read an identical −24.6 dBFS (AGC), the closer Rodes −54/−53 — a 30 dB device
offset, not distance. The Ankers led 68% of the time on construction. You cannot equalise an AGC'd
source against an uncompressed one; compare levels **only within a matched set**.

**8. Absolute thresholds turn a gain-staging error into a chopped mix — check level before blaming a
capsule.** 2026-09-20, 15 min prayer meeting: a clean capture (0 over-FS, flux-CV 0.38, no drops),
but every mic ~22 dB under target (speech p50 −45/−46/−46). `PriorityActiveRms` (−40),
`PriorityBreakInRms` (−50) and `SilenceFloorRms` (−55) assume speech near −24, so the lapel at −39 had
1 dB of margin: 12 winner changes/min and winner = −1 (a hard mute under Gate) 28% of the time. **Compare
`speech=` against −24 dBFS before reaching for a quality metric.** You cannot add the 22 dB
downstream: crest is 28–36 dB, so peaks would clip — the gain belongs at the transmitter.

### Anker era (speakerphones, returned 2026-08-30) — kept because the lessons generalise

**1. Speakerphone DSP destroys every proximity cue except gross level.** Crest, spectral flatness,
HF ratio, centroid and SNR all failed to rank the closest mic (`tools/AnalyzeInputs`); only smoothed
level survived (~5–6 dB after AGC). "Far mic wins" was **temporal, not metric**: re-picking the
instantaneous loudest every 10 ms let a distant mic's AGC make-up win in pauses (113 flips); crest
weighting made it worse (136); **hold + hysteresis fixed it (~23)**.

**2. Loudest ≠ best-sounding.** A mic can be louder yet worse (AGC, desk boom, a vent). On a labelled
capture, envelope correlation to the lapel rejected the loud-but-bad mic (0.706 vs 0.774) where level
and refSNR both picked it — but only rejects reliably; among good mics the margins are noise.

**3. "Scratchy" is not measurable by cleanliness metrics — measure temporal instability.** HNR, CPPS,
jitter and shimmer ranked the over-processed mic *cleanest* (Praat). Spectral-flux coefficient of
variation separated it (natural ~0.41, scratchy 0.52–0.65, `tools/naturalness.py`) — but it vetoes
better than it picks, penalises distant mics, and **also rises on RF dropouts**: check range first.

**4. The Ankers gated singing to digital silence together — no mix strategy could fix it.**
2026-08-09: each unit silent 13–21% of frames, all four at once 4.6% (51× independence) — a
total-stream dropout every 2.4 s. Summing more mics cannot fill holes that are in every source at
once, and Automix Off changed nothing. Do not attempt a selector fix for gating. Diagnostic corollary:
`winner = −1` has three causes — automix Off, priority active, silent room; the decisions CSV's
`mode_<bus>` column settles the first, and applied gains 0 (duck) vs 1.0 (silence) the others.

**Validating a selector change.** Never tune the live selector from a live impression. Capture "record
all inputs" during a real session *with operator labels* of which mic sounded better when, replay
offline (`tools/AnalyzeInputs`, `tools/naturalness.py`), then touch `AutoMixer`. Judge over a longer
listen with the real speaker — short A/B impressions have contradicted both the metrics and the
operator's own later judgement.

## Known gotchas

*Symptom → cause → rule. Grows over time — see the maintenance protocol.*

### Devices, gain & RF

- **Hot-plug USB audio gets a NEW endpoint GUID on re-enumeration** (replug, another port, a driver
  reboot); only fixed devices (Realtek, VB-CABLE) keep theirs. `DeviceResolver` therefore matches a
  preset device by, in order: `DeviceKey` (serial-derived container id), GUID, then friendly name via
  `DeviceNameKey`, which strips **only** the volatile `(N- …)` prefix — never truncate at `" ("`, as an
  un-renamed device's identity is the interface name inside the parens (`Speakers (Lync USB Headset)`
  vs `Speakers (Realtek(R) Audio)`). Resolve against the master device lists, not a strip's filtered
  picker. **Persist what the operator asked for, not what is currently resolvable**: strips keep
  `DesiredDeviceId/Name/Key` through a device's absence (cleared only by *Clear device*) and
  `ReattachDesiredDevices` re-binds on reappearance — nulling it on disconnect once erased the only key
  a replug could match. Identical devices that cover different areas must never be greedy-filled.
- **Device identity across ports is readable without WMI** — not from `PKEY_Device_InstanceId`, which
  reads empty on every audio endpoint, but from `PKEY_Device_ContainerId`'s UUID version: **v5** is derived from the hardware serial (same GUID on any port), **v1** is minted per
  port (`Services/DeviceIdentity`; `tools/device-identity.ps1` confirms by PnP walk). The Wireless PRO
  receivers are v5 — serials `801D150D` and `802ECAEA`, so two identical receivers are told apart;
  the Soundsync dongles and the Lync headset are port-derived. `DeviceKey` is written only for v5. ⚠
  The cross-port claim is inferred from the scheme, not yet watched: move a receiver and compare.
- **Windows endpoint gain is keyed to the port-derived endpoint, so it resets to 0 dB on a new port**
  (the rename goes with it). A second identical receiver on a new port arrived ~24 dB under its twin
  and looked like two dead mics — check `tools/VolProbe` before suspecting hardware. **Set gain on the
  receiver** (a Wireless PRO's level applies to both its transmitters, so pairs are matched by
  construction), keep Windows near 0; a wired lapel on the aux jack has no receiver, so Windows is its
  lever. The app's fader can only **attenuate** (100% = unity). The Rode endpoint's slider is
  non-linear: 53.7% = 0 dB, 87% = +15 dB, 100% = +30 dB.
- **+15 dB on the Wireless PRO endpoint is the verified ceiling.** +24 dB clipped (peaks +6.7 dBFS,
  flat-tops to 1.9 ms) while speech was still 14 dB under target, because crest here is ~45 dB: no
  endpoint gain lands speech at −24 and keeps transients under full scale — proximity and transmitter
  gain can. The capture is float32, so over-full-scale passes through and clips only at render: judge
  clipping by counting samples ≥ full scale and flat-top runs, never by peak dBFS.
- **GainAssist on a Wireless PRO transmitter is AGC and breaks the automixer** (it levels a far mic up
  to a near one — "far mic wins" again). Turn it off per transmitter: long-press Left Nav until
  AUTO/DYNAMIC becomes a dB value.
- **"Split" means TX1→L, TX2→R only while the RX's 3.5 mm jack is an output.** Plug a mic in as RX Mic
  and both transmitters merge onto the left. **Safety** mode puts a −10 dB copy of the same mix on the
  right — it looks like a split on a meter and carries no second mic. Mode: long-press both Nav
  buttons, or RODE Central. **Prove a split by sample-level correlation** (two capsules ≈ 0.07; one
  signal fanned out ≈ 1.0) — envelope correlation stays high either way (`tools/RxProbe`).
- **Placement is the whole signal-to-noise budget** — the room floor is constant, and every halving
  of mic-to-mouth distance is +6 dB. Keep room mics **10–15 ft from the lectern** (a rule about the
  position, not the person — in prayer the presenter sits at a table like everyone else): measured
  2026-08-30, a mic on the presenter's own table read −30.6 dBFS median in his *pauses* while a real
  interjection 15 ft away landed at −43.2, so its residual beat a genuine question in **71%** of
  pause samples and no threshold or break-in can separate them. Otherwise: ≤3 ft from the nearest
  talker, on a low stand rather than flat on the table (it combs against the table and picks up every
  knock), mic-to-mic ≥ 3× mic-to-mouth, the same transmitter on the same tape-marked spot every week
  (so a preset and a "mic 4 sounds bad" report stay meaningful), and the strip of an empty table
  muted.
- **A Wireless PRO in its charging case enumerates as USB storage, not audio** — two "RODE Wireless PRO
  USB Device" drives (the transmitters) and no RX endpoint. Connect the RX directly by its own USB-C.
- **Each transmitter records 32-bit float on board** (unclippable, 40+ h): a gain-proof backup, and
  the decisive "bad mic or bad link" test — captured before the RF link, so a glitch in the app's
  capture that is absent on board is RF.
- **The capture chain takes only channels 0 and 1 of an endpoint** — a >2-input interface silently
  loses the rest. Widening `ChannelSource` is what one would need.
- **A chronically "bad" mic is usually out of RF range.** Anker-era walk test: flux-CV tracked
  distance (0.58–0.68 with packet-loss spikes far, 0.37 up close) — a gradient means RF, a defect is
  uniform. Anker specifics: an S500 can hold its dongle AND a Bluetooth link at once, the second radio
  garbling the first (forget the BT pairings); a dongle can keep its render endpoint while its capture
  is dead (the "half-link" — re-pair). The app's Bluetooth rule decides from the device bus
  (`BTHENUM`) alone, never a name guess.
- **When a receiver disappears** (unplugged, put in its case) capture stops with `0x88890004`
  (device invalidated), the watchdog's restart fails the same way, and the strip shows "no microphone
  assigned" while remembering its device for reattachment — correct behaviour, verified 2026-09-23.

### Audio graph & NAudio

- **`MixingSampleProvider` removes a source forever the first time it returns short**
  (`ReadFully=true` on the mixer only pads the output). Every feed `BufferedWaveProvider` runs
  `ReadFully=true` so it never returns short — else one empty read (a route toggled off and on)
  silences that bus until it restarts.
- **The feed buffer caps end-to-end latency.** 500 ms (`CreateOutBuffer`), cleared and **primed with
  40 ms of silence** on every output (re)start — a large or unprimed backlog is heard as "hello"
  arriving 1–2 s late. NAudio's property is `DiscardOnBufferOverflow` (not `…Full`).
- **An empty feed buffer is a SILENT hole**: `ReadFully` pads the shortfall with zeros, so nothing
  upstream sees it — a ≤10 ms splice with a click at each edge, heard as fine crackle or grit. The diag
  WAVs tap *upstream* and are clean by construction; **`mix-*.wav` shows it** — in Gate only the leader
  is on the bus, so each hole is exact digital silence. Count runs of ≥ 2 ms of exact zero per minute:
  on 2026-09-23 a clean 0–3/min jumped to **1,600–2,250/min** at 19:38:38 on **both buses within
  0.1 s** — so the capture side, not an output device — until the app was restarted; the trigger was
  not identified (its log had been deleted). The live counter is `under=[a,b]` in the log and
  `underruns` in `/state`, counted **before** each read and only for pairs that should be feeding
  (routed, live capture) — before 2026-09-23 it also counted every unrouted pair (~100/s forever), so
  filter old logs by routes. `bufMs = 0` is NOT an underrun (the depth legitimately drains to zero
  between reads); only a climbing counter is.
- **Crackle triage, in order:** (1) count samples ≥ full scale and flat-top runs (`tools/RxProbe`
  capture or the diag WAV) — zero means it is not clipping, whatever the peak says, and no gain change
  will help; (2) underruns climbing on routed pairs, or holes in the mix, mean the silent-hole crackle.
  A restart (or Resync) re-primes every buffer and cures it. The operator's instinct is clipping; the
  two sound alike.
- **A stalled capture freezes its meter** at the last value (`PeakMeter` only updates on data). The
  watchdog (`WatchdogTick`, 500 ms) restarts an input whose data is >1.5 s stale — shared mode delivers
  buffers even in silence, so no data is an unambiguous stall — with backoff, then `InputRestartGaveUp`.
- **Nothing that can wait may run on an audio thread.** Calling `WaveFileWriter.Flush()` from
  `MixRecorder.WriteSamples` every 10 s (so a killed recording keeps a valid header) caused **24
  underruns/min**, 23 of 24 phase-locked to the flush cycle — mostly the per-mic recorders flushing on
  the *capture* threads, which drains both buses. Removed: 0 in 60 s. Audit, 2026-09-23: the capture
  callback shares no lock with the UI and rents buffers from `ArrayPool`; the only disk I/O on audio
  threads is the recorders' buffered writes (0 underruns). A crash-proof header needs a background
  writer per recorder fed by a queue (attempt parked in `git stash`). `WaveFileWriter` is also not
  thread-safe — writes are serialised by the recorder's lock.
- **Underruns can be CPU starvation, not the mixer.** 2026-09-26: this i5-9400T sits at 88–100% CPU
  during a service (OBS ~32%, Zoom ~16%, audiodg ~11%; the mixer ~3%) and routed mics underran
  ~1/s each at Normal priority. Raising the process to **High** (live): 0 in the next 30 s, 1 in
  6 min. The app now sets High at startup (Normal under `--replay`; `--priority=` overrides both).
- **The Realtek aux input (the wired lapel) runs Realtek capture effects** (`RtkRecMFX`/`RtkRecEFX`
  APOs registered, "audio enhancements" not disabled, 2026-09-26) — DSP ahead of the mixer on the
  priority mic, of unknown kind. The USB receivers carry none. Toggling enhancements restarts the
  endpoint, so the lapel drops for a watchdog restart: never mid-service.

### Recording & measurement

- **A kill leaves a recording's WAV header at zero frames**; a normal close or a stopped recording
  finalises it. Every sample is on disk but players read the file as empty — `tools/wavfix.py DIR...`
  reports, `--apply` rewrites only the size fields (RIFF, data, `fact`), `--skip STAMP` for one in
  progress. A file *being* written also shows 0 bytes and a frozen mtime in Explorer (NTFS does not
  update the directory entry); its true length is `File.Open(path,'Open','Read','ReadWrite').Length`,
  and `tools/live_wav.py` reads it mid-session.
- **Recording is always on, with three bounds** because a 5-mic, 2-bus hour is ~9 GB (1.29 GB per
  stream-hour): it stops itself at **1 hour**, files expire at **28 days**, and `RecordingRetention`
  deletes oldest-first below **20 GB free** when a recording starts, refuses to start below 15 GB and
  stops one in flight below 8 GB. Session records are never swept. **Split strips record mono** (the
  two channels are identical after the side split).
- **A capture restart must not end a recording.** Until 2026-09-26 `InputChannel.Stop()` closed the
  mic's diag WAV, and Stop runs on every watchdog recovery, Resync, device change and replug: one
  Resync ended all seven diag files 8 minutes into a service while the UI said "recording". Now only
  `Dispose` (strip removed) closes it, and every file of a stamp is kept aligned to wall-clock time:
  `MixRecorder.PadToNow` writes an outage as silence on the restart path (capture/render stopped —
  never from an audio thread), gaps under 0.5 s are left as drift, and a strip or bus that gets its
  device mid-recording joins with a silent lead-in. A strip-count change restarts the recording
  under a new stamp (it used to stop it for good, mixes included). A writer error stops that one
  recorder and raises `rec.failed` in Checks.
- **A replay fixture must live in `analysis\keep\`** — `Prune()` runs at every record start (at
  launch) and is top-level only. Both original golden baselines' WAVs were pruned at 42 days
  old before anyone noticed.
- **`decisions-<stamp>.csv` is the only record of what the automixer did**: every 100 ms, per bus the
  automix mode and leader, per mic the level and applied gain (the diag WAVs are pre-automix). 10 Hz
  cannot miss a 200 ms hold. The `mode_<bus>` columns replaced a `scene` column on 2026-09-23.
- **A NaN calibration median once silently destroyed whole session records**: System.Text.Json throws
  on NaN and a blanket catch in `SessionStore.Save` turned that into no file at all.
  `NaNAsNullConverter` writes null (valid JSON for jq, unlike `NaN`). Lesson: a blanket catch around
  serialisation needs a test that round-trips the awkward values.
- **A digital-silence rate over a whole diag WAV counts the startup window** (the recorder starts
  before transmitters are on): 16.6% whole-file was 99% in minutes 1–2 and 0.0% after. Bucket per
  minute. The same head drags calibration floors toward −inf.
- **Timing a signal path**: routing to the output and clapping does not measure device latency — a
  channel's position in the mix includes its feed-buffer backlog, which is set at startup and
  anti-correlates with transport latency. Measure from the diag WAVs (tapped before the buffer), and
  cross-correlate onset envelopes rather than thresholding on peak (a spoken "T!" peaks in the vowel).
  Per-channel delay compensation itself was removed 2026-09-20: Gate leaves one mic on the bus, so
  there is nothing to align.
- **Transcription** (`tools/transcribe.py`): Whisper's voice-activity filter silently dropped the last
  6 minutes of a recording once the mix level fell 6–8 dB — the transcript just stopped mid-sentence.
  Keep it off; Whisper's own no-speech check skips silence. Repetitive lines are invented text on
  audio it cannot make out (the crackle stretch, for instance) — distrust them.

### UI / WPF

- **A missing `{StaticResource}` is a runtime process kill, not a build error** — keys resolve when a
  template is applied. A window referencing a style defined only in another window crashed on open
  and shipped for weeks. `XamlResourceTests` checks per file that every key is defined in that file
  (per-file matters: globally the keys all exist); `DefinitionsAreNotShared` pins that `App.xaml`
  carries no resources. `BindingErrorListener` cannot catch this.
- **A WPF trigger's `Value` is a string**, so comparing it to a bool binding silently never fires.
  Selection state rides on `Tag` as a string (`"on"/"off"/"mixed"`) with a `DataTrigger` on
  `{Binding Tag, RelativeSource={RelativeSource Self}}`.
- **`SceneBtn`/`SmallBtn` ignore `Padding`** (the template's `Border` never binds it) — size them with
  `Height`.
- **XML comments cannot contain `--`**: a XAML comment with one is a build error.
- **A snap-to-tick `Slider` in a narrow column is unusable** — which values you can reach depends on
  pixel rounding (21 ticks in ~115 px). A small fixed set of values belongs in a list of named choices.
- **Alert rules are pure** (`HealthMonitor`), and an alert names its remedy as a value
  (`HealthAlert.Fix` + `Target`), carried out by `MainViewModel.ApplyFix` — so "does this alert offer
  the right fix, aimed at the right strip" is a unit test. `Target` is the half that fails silently.
  Physical or ambiguous remedies carry `FixKind.None`: a button that cannot help is worse than a
  sentence.
- **Device enumeration must stay off the UI thread** (`RefreshDevices` enumerates on the threadpool,
  marshals only the rebuild, and coalesces overlapping refreshes). On the UI thread every replug froze
  the window ~3 s; now a real hot-plug peaks at 60 ms. Startup still enumerates once synchronously,
  before the window shows.
- **`System.Threading.Timer` callbacks need a state object to tell callers apart**: replay's manual
  tick and the wall-clock timer both passed null, so the timer never stood down and replay ran the
  selector at 200 ticks/s instead of 100, halving every hold (fixed with a `WallClock` sentinel).
- **`preset.json` is written by replace** (`.tmp` + `File.Replace`, keeping `.bak`; `Load` falls back
  to the backup) — writing in place truncated first, so a kill during autosave left no configuration.
- `*_wpftmp.csproj` does not honour `ImplicitUsings` for `System.IO` — add `using System.IO;`.

## Reviewing a recorded session

`.claude/skills/session-review` is the procedure — invoke it rather than re-deriving the order (checking
level before analysing hand-offs is the step that has produced confident wrong answers when skipped).
A session is one `<stamp>` across `session-*.json` (**start here** — the `Config` everything must be
read against), `decisions-*.csv`, the per-mic `diag-input*.wav` and the bus `mix-*.wav`.

Two standing goals: **the remote attendee on bus A is the only listener that matters** — a selector
can follow its rules perfectly and still put a scratchy distant mic on the stream; and **autopilot** —
ask of every finding whether the app could have prevented, corrected or at least reported it without
anyone in the room noticing. A fault only a technical operator could catch is a design gap.

## Maintaining this file

Its value is knowledge **the repo cannot tell you**: measurements on real hardware, negative results,
device behaviour, decisions with their *why*, and rules for working on the live rig. If a session that
searches the code would learn it in 30 seconds, leave it out. **A wrong statement here costs more than
a missing one** — whoever reads this trusts it.

Update it **in the same change** when you: find a non-obvious gotcha (symptom → cause → rule, with the
number that proves it); prove something does not work (Measured findings — the highest-value entries);
change the audio pipeline or a selection rule; remove a feature (fix every present-tense mention);
add a tool, a runtime file location or a dependency; or learn a rule for working on the rig.

Not here: in-flight TODOs or planned work (ROADMAP), restatements of the code, per-service settings
(session memory), speculation.

Every few substantial changes: delete gotchas the code has made impossible, fold duplicates into one
place, re-check constants and numbers against the code, and tighten — this file is only useful while
it is both short enough to read and true.
