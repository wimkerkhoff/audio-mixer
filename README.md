# AudioMixer

A small Windows desktop audio mixer for routing **3 microphone inputs** to **2 outputs** (typically your headset/speakers and Zoom via VB-CABLE), with per-channel volume, mute, delay, routing toggles, VU meters, recording, and a clap-test delay-detection feature.

Built because Bluetooth mics have ~100-300 ms more latency than wired ones and existing mixers either don't compensate for it or are overkill for a simple setup.

## Features

- **3 input channels** — each with its own device, volume slider, mute, delay (0-1000 ms), and routing toggles
- **2 output buses** — each with its own device picker, peak meter, and dB readout
- **Routing matrix** — independent A/B toggles per input
- **VU meters** — pre-fader and post-fader for each input, output level meter for each bus; green below -12 dBFS, yellow to -3, red above (clip warning)
- **Delay compensation** — per-channel adjustable delay buffer (e.g., add 150 ms to the wired mic to align with a Bluetooth one)
- **Auto-detect delays** — clap test that records all active inputs for 4 seconds, finds the first transient in each, and suggests delay values to align them
- **Recording** — capture the headset or Zoom-bound mix to a WAV file (48 kHz stereo float32) in `Documents\AudioMixer\recordings\`
- **Editable labels** — rename each input/output strip (e.g., "Rode", "Anker 1", "Headset", "Zoom")
- **Auto-save** — every setting change persists 500 ms later to `%APPDATA%\AudioMixer\preset.json`
- **Duplicate-device prevention** — a device picked for one slot disappears from the others' picker lists
- **Compact UI** — single 500×320 window with click-to-open device popups, no scrollbars, no inline dropdowns

## Requirements

- Windows 10 or 11
- For **Zoom routing**: VB-CABLE (https://vb-audio.com/Cable/) — free virtual audio cable. Install + reboot. Pick "CABLE Input" as Output B's device, then set Zoom's microphone to "CABLE Output".

## Running

### From source (developer setup)

```powershell
git clone <repo path> audio-mixer
cd audio-mixer
winget install Microsoft.DotNet.SDK.8   # one-time, ~250 MB
dotnet run --project AudioMixer
```

### Pre-built — framework-dependent

Target machine must have the **.NET 8 Desktop Runtime** installed (`winget install Microsoft.DotNet.DesktopRuntime.8`, ~50 MB).

```powershell
dotnet publish AudioMixer -c Release
```

Output goes to `AudioMixer\bin\Release\net8.0-windows\publish\`. Copy the folder; run `AudioMixer.exe`.

### Pre-built — self-contained

No prerequisites on the target machine. Total ~160 MB.

```powershell
dotnet publish AudioMixer -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Output goes to `AudioMixer\bin\Release\net8.0-windows\win-x64\publish\`. The folder contains one big `AudioMixer.exe` (~154 MB, holds the .NET runtime and all managed code) **plus 5 native WPF DLLs** that .NET cannot embed into the single-file bundle: `D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`, `PresentationNative_cor3.dll`, `vcruntime140_cor3.dll`, `wpfgfx_cor3.dll`. **Copy the whole publish folder**, not just the .exe.

## Quick start

1. Launch AudioMixer
2. On **Input 1**, click the device button (top of the strip) → pick your microphone
3. On **Output A** (right side), click the device button → pick your speakers/headset
4. The **A** toggle on Input 1 is enabled automatically — you should hear yourself
5. (Optional) Click **A** to rename to something memorable like "Headset"
6. (Optional) For Zoom: install VB-CABLE, set **Output B** to "CABLE Input", enable Input 1's **B** toggle, and set Zoom's microphone to "CABLE Output"

### Toolbar (left to right)

- ↻ **Refresh devices** — re-enumerate Windows audio devices (after plugging in / out)
- ⟳ **Resync audio** — flush all buffers and restart outputs (use if you notice growing latency)
- **A / B** — choose which output bus to record from
- ⏺ **Record** — start/stop recording the chosen bus to WAV
- ⏱ **Detect delays (clap test)** — see below

### Delay detection

If one of your mics is Bluetooth, it lags the others by 100-300 ms. To auto-compensate:

1. Click the stopwatch icon in the toolbar
2. A dialog tells you when to make ONE sharp sound (clap)
3. AudioMixer records all active inputs for 4 seconds, finds the first transient in each, computes the difference, and proposes per-channel delay values to align them
4. Accept "Apply suggested delays" — the latest-arriving input gets 0 ms; others get a delay equal to how much earlier they were

Raw recordings are saved to `Documents\AudioMixer\analysis\input{N}-{timestamp}.wav` for inspection.

## Architecture

```
WasapiCapture (per input device)
  → resample to 48k stereo float32
  → mute gate → gain → delay buffer → peak tap
  → per-output ring buffer
                          ↓
                MixingSampleProvider (per output bus)
                          ↓
                Peak tap → recorder tap (optional)
                          ↓
                WasapiOut (per output device)
```

- **Internal mix format**: 48 kHz, stereo, IEEE float32
- **WASAPI shared mode** for all I/O (so Zoom can also use the same device)
- **Per-output buffer**: 500 ms cap, `ReadFully=true` to avoid `MixingSampleProvider` evicting the source on short reads (see CLAUDE.md for the NAudio 2.2.1 gotcha)
- **MVVM** with WPF — view-models in `AudioMixer/ViewModels/`, engine in `AudioMixer/Audio/`

## File locations

| What | Where |
|---|---|
| Settings (auto-saved) | `%APPDATA%\AudioMixer\preset.json` |
| Mix recordings | `Documents\AudioMixer\recordings\mix-{timestamp}.wav` |
| Clap-test recordings | `Documents\AudioMixer\analysis\input{N}-{timestamp}.wav` |
| Diagnostic log | `%TEMP%\AudioMixer.log` |

## Known limits

- Two outputs pointing at the **same physical device** is allowed but quirky — auto-dedupe on preset load keeps the first slot and clears the second. WASAPI shared mode mostly handles two sessions per device, but the implementation has occasional issues.
- Latency floor is the WASAPI shared-mode floor (≈50-100 ms) plus our 200-500 ms jitter buffer + Bluetooth mic device-side delay. Sub-30 ms is not achievable through this stack without switching to ASIO + exclusive mode (which would conflict with Zoom).
- WPF doesn't support trimming reliably, so the self-contained .exe is ~150 MB.

## License

Personal project. No license declared — ask before reusing.
