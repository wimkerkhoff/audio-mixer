---
name: session-review
description: Analyse a recorded AudioMixer session to judge what a remote attendee actually heard, and turn that into concrete setting or code changes. Use when reviewing a service afterwards, when the operator reports the audio sounded wrong, when asked to check the automixer's choices, the leveler, clipping or levels, or when asked to compare sessions over time.
---

# Reviewing a session

## What you are judging

**What the remote attendee heard on bus A**, and **what the operator heard on bus B** — and above
all, **whether those two agreed**.

Bus A is the product: not whether the selector followed its rules, since it can follow them perfectly
and still put a scratchy distant mic on the stream. Bus B is the operator's monitor, and it only earns
its keep if it represents A faithfully. **A and B diverging is a finding in its own right**, and a
nasty one: the operator judges the service by B, so anything true of A but not of B is invisible to
the only person in the room who could have fixed it. Check for it explicitly — different routing,
different automix mode, different leveler state, a mute on one and not the other.

Every finding must end up as either a setting to change or a code change to propose; anything that
cannot is an observation, not a finding.

The second goal is **autopilot**: every problem you find should prompt the question *"could the app
have prevented, corrected, or at minimum reported this without anyone in the room noticing?"* A
finding that only a technical operator could have acted on is a design gap, and should be reported
as one.

## The data

Under `~/Documents/AudioMixer/`:

| File | What it is | Tapped where |
|---|---|---|
| `analysis/diag-input<N>-<stamp>.wav` | one mic, raw | **pre-fader, pre-low-cut** — what the mic heard |
| `analysis/decisions-<stamp>.csv` | 10 Hz decision track | scene, winner/bus, leveler gain/bus, per-mic level + applied gain |
| `recordings/mix-<A|B>-<stamp>.wav` | the bus | **post-leveler, pre-volume** — what was sent |
| `sessions/session-<stamp>.json` | aggregates + config + events | whole session |

A session is one `<stamp>`. **Start from `session-*.json`**: it carries `Config`, without which the
numbers cannot be read — "12 hand-offs a minute" means one thing under Gate and another under Off,
and "this mic never won" is expected if it was never routed.

Split strips (`Left`/`Right`) record **mono**; Stereo strips record two channels. Do not treat a
1-channel file as a fault.

## Procedure

Work in this order. Each step can invalidate the ones after it — a mis-levelled rig makes every
selector statistic meaningless, so do not analyse hand-offs before you have checked level.

### 1. Read the config, then the events

`Config.Inputs` / `Config.Outputs` / `Config.LowCutHz` / `Config.Lapel`, then `Events`. The events are
the app's own conclusions and are usually right; your job is to check them and find what they missed.

### 2. Level — the first thing that goes wrong

Per mic: `SpeechDb` against the **−24 dBFS** target. More than ~10 dB under and *stop*: absolute
thresholds (`PriorityActiveRms` −40, `PriorityBreakInRms` −50, `SilenceFloorRms` −55) no longer sit
below speech, so the selector will be unstable and its statistics describe a broken rig rather than a
broken rule. This is finding 8 and it is the single most common root cause.

### 3. Clipping

`ClippedSamples` per input. **Never judge clipping by peak dBFS** — the capture is float32, so
over-scale samples pass through unharmed and only clip at render. Count samples ≥ full scale and look
for flat-top runs. A session read a healthy peak with 1078 samples already over.

### 4. What each bus carried, and whether they agreed

Measure **both** `mix-A` and `mix-B`. For any stretch that sounds wrong, use `decisions-*.csv` to find
the leader at that moment and compare the chosen mic's `diag-input` against the alternatives **at the
same timestamp**. This is the only way to answer "should it have picked a different mic" — the mix
alone tells you a choice was bad but never what the alternative sounded like.

Then compare the buses against each other, which is a separate question from either one's quality:

- **Different leaders at the same moment** — normal under Gate if routing differs, but it means the
  operator was monitoring a different mic than the stream carried.
- **Different leveler gain** — `leveler_A` vs `leveler_B` in the CSV. If one is levelled and the other
  is not, the operator's impression of "how loud and even is this" does not apply to the stream.
- **Routing differences** in `Config.Inputs` — a mic on A but not B, or vice versa.
- **One bus silent while the other is not**, at any point.

Where they diverge, say plainly which faults the operator could not have heard. That is the strongest
kind of finding, because it explains why nobody acted.

### 5. Selector behaviour

From the session record: `HandoffsPerMinute`, `NoWinnerPercent`, `OccupancyPercent`,
`MutedByGatePercent`. Interpret against config:

- **High hand-off rate + high no-winner** almost always means level, not the selector (step 2).
- **Under Gate, non-leaders sit at exactly 0** — a mic "muted 60% of the session" is normal, not a fault.
- **`winner = -1` has three causes**: automix Off, priority-active, silent-room. Disambiguate by the
  gains in the CSV — a priority duck writes 0, a silent room writes 1.0.

### 6. Leveler

`leveler_<bus>` in the CSV. Flat 0 dB throughout means it was off or never engaged. Swinging near the
make-up ceiling means it is working hard, and every dB of lift raises the room floor one-for-one —
this room's floor is HVAC that no filter removes.

### 7. Link and glitches

`Underruns` per input (a silent hole in the monitor feed, invisible to meters). RF dropouts show as
exact-silence gaps mid-speech with high flux-CV while voiced% is high.

### 8. What the operator changed, and whether it helped

`Actions` in the session record: a timestamped list of what was changed by hand — scenes, routing,
mutes, levels, devices, leveler, resync, calibration resets. Scene changes appear as one line rather
than the twenty derived changes they cause.

For each action, compare the minutes either side of its timestamp in the decision track and the mix:

- **Did it do what the operator intended?** A level change meant to fix a hot mic that instead pushed
  it under the selector's threshold is a worse outcome than doing nothing.
- **Did it help or hurt the stream?** Hand-off rate, no-winner share and the mix's own level before
  and after are the measures.
- **Was it needed at all?** An action that corrected something the app should have handled is an
  autopilot gap — report it as one, naming what the app should have done instead.
- **Was it reverted, or repeated?** Repeated changes to the same control mean the operator was
  hunting, which means the app was not telling them what was wrong.

**An empty `Actions` list on a good session is the target, not a gap in the data.** A service that ran
itself is the goal; one that needed six interventions is a finding about the app even if every
intervention was correct.

## Traps that have produced wrong conclusions

These each cost real time. Check them before reporting.

- **Whole-file digital-silence rate counts the startup window.** Recording begins before transmitters
  are live, so the head of every capture is true zero. A Wireless PRO capture measured 16.6%
  digital silence whole-file and **0.0% after minute 3**. Always bucket per minute.
- **Calibration medians are cumulative.** A mic that had its gain changed mid-session reports a median
  dragged by pre-change buffers. Check `Config` and the events for a gain change; the app now flags
  this itself as a stale-calibration warning.
- **Multiple gain regimes in one capture.** If endpoint or transmitter gain changed during the
  session, every level comparison must stay inside one regime. Sessions on 2026-09-20 had three.
- **`bufMs = 0` is not an underrun.** The buffer legitimately drains and refills between reads. Only
  the `Underruns` counter distinguishes a real hole.
- **Proving a split receiver needs SAMPLE-level correlation.** Envelope correlation stays high for any
  two mics in one room. A verified split read sample 0.069, envelope 0.866.
- **Never compare levels across device types.** AGC flattens proximity; a level difference between an
  Anker and a Rode is device gain, not distance.

## Reading the audio

`tools/live_wav.py` — `session_files(dir, stamp)` and `read_mono(path)` parse possibly-unfinalized
WAVs. Use it rather than `soundfile` for a capture that may still be recording. Offline analysers live
in `tools/`: `AnalyzeInputs`, `RefCorr`, `naturalness.py`, `gate_rate.py`, `voice_quality.py`.

## Before proposing a selector change

**Never tune the selector from a live impression or a single session.** The repo's findings are a
record of metrics that inverted under measurement — crest, spectral flatness, HF ratio, SNR, HNR,
CPPS all failed to rank the closest mic. Read "Measured findings" in CLAUDE.md before proposing any
new metric; the answer is often already there as a negative result.

A selector change needs a labelled capture — operator notes of which mic sounded better when —
replayed offline, with hand-off count and winner occupancy compared before and after.

## Output

Report in this shape:

1. **What a remote attendee heard** on bus A — one paragraph, plain language, no metrics. Then one
   line on whether bus B represented it faithfully, and if not, what the operator could not hear.
2. **Findings**, most severe first. Each with the measurement, the file it came from, and its
   consequence for the listener.
3. **Change the settings** — specific values, where to set them, why.
4. **Change the app** — where the operator could not reasonably have prevented or noticed this.
   Say what the app should have done instead: corrected it silently, caught it in Checks before the
   service, or refused to start.
5. **Operator interventions** — what was changed, whether each helped, and which the app should have
   handled itself.
6. **What you could not determine**, and what to capture next time.

State numbers with their units and source. If a session is too short, too quiet, or mis-levelled to
support a conclusion, say so rather than reporting a weak one — a confident wrong answer about a
selector is worse than no answer, because nobody re-derives it.
