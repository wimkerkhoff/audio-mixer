using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioMixer.Audio;

public sealed class InputChannel : IDisposable
{
    public const int InternalSampleRate = 48_000;
    public const int InternalChannels = 2;

    private readonly int _outputCount;
    private readonly BufferedWaveProvider[] _outBuffers;
    private readonly float[] _autoMixGain;   // per-output automix gain, set by AutoMixer (audio thread reads)
    private readonly float[] _autoMixRamp;    // per-output last-applied gain, for intra-buffer ramping
    private int _routeMask;
    private readonly object _stateLock = new();

    private float _currentLevelLinear;
    public float CurrentLevelLinear => Volatile.Read(ref _currentLevelLinear);


    /// <summary>
    /// Feeds the levels the automixer selects on, without a capture device.
    ///
    /// AutoMixer is the heart of the app and had no unit tests at all — everything it does is decided
    /// from these two numbers plus routing and the priority flag, none of which needs audio hardware.
    /// The only thing standing in the way was that the levels are normally written by the capture
    /// callback, so this is that one seam and nothing more. Internal, like the conversion-chain seam.
    /// </summary>
    internal void InjectLevelsForTest(float rms)
    {
        Volatile.Write(ref _currentLevelLinear, rms);
    }

    // Spectral-flux instability (coefficient of variation of frame-to-frame spectral change) latched
    // during voiced buffers. Validated offline (tools/naturalness.py) as a reference-free "scratchy/
    // over-processed mic" detector: the bad Anker's DSP makes it measure CLEAN on HNR/CPPS but its
    // spectrum is unstable (gating chatter / musical noise), which this captures. Lower = more natural.
    // 0 = not enough recent speech to judge.
    private const int FluxN = 512;
    private const int FluxBits = 9;             // log2(FluxN)
    private const float FluxVoiceRms = 0.006f;  // ~ -44 dBFS: only accumulate while the mic hears speech
    private const float FluxEma = 0.01f;        // ~1 s of continuous speech over 512-sample windows (~94/s)
    private static readonly float[] FluxWindow = MakeHann(FluxN);
    private readonly Complex[] _fftBuf = new Complex[FluxN];
    private float[]? _prevMag;
    private float[]? _magScratch;
    private float _fluxMean, _fluxVar;
    private bool _fluxHasPrev;
    private float _currentFluxCv;
    public float CurrentFluxCv => Volatile.Read(ref _currentFluxCv);
    private const int FluxHop = FluxN;          // non-overlapping windows (set < FluxN for overlap)
    private readonly float[] _fluxAccum = new float[FluxN];
    private int _fluxFill;
    private string _label = "";

    // One-shot latch: a failing AddSamples would otherwise repeat ~100x/s, so log the first one only.
    private bool _pushErrorLogged;

    // --- RF-health accumulators (diagnostic) --------------------------------------------------
    // Lock-free monotonic counters incremented in the audio callback; the ~1 Hz log loop reads them
    // via SnapshotRfStats and computes deltas. Purpose: spot a marginal 2.4 GHz Soundsync dongle link
    // from the signal alone — a dropping link produces exact-silence gaps mid-speech (voiced→silent
    // "drop edges") + elevated flux-CV. Only raw counts are recorded here; classification is offline.
    private const float RfSilenceRms = 1e-4f;   // ~ -80 dBFS: a wireless dropout fills buffers with ~silence
    private long _rfBuffers, _rfVoiced, _rfSilent, _rfDropEdges, _rfSumMilliDbVoiced;
    private bool _rfPrevVoiced;                  // audio-thread only
    private long _rfLastBuffers, _rfLastVoiced, _rfLastSilent, _rfLastDropEdges, _rfLastSumMilliDb; // reader only

    public readonly record struct RfStats(int Buffers, float MeanDb, float VoicedPct, float SilentPct, int DropEdges, float FluxCv);

    // Samples at or over full scale, counted at the INPUT tap — before the fader, where endpoint or
    // transmitter gain that is set too high actually shows up. A peak reading cannot stand in for this:
    // the capture is float32, so over-full-scale samples pass through unharmed and only clip at render,
    // which is how a session on 2026-09-20 read a healthy peak while 1078 samples were already over.
    private long _clippedSamples;

    public long ClippedSamples => Interlocked.Read(ref _clippedSamples);

    private void CountClipping(float[] buffer, int count)
    {
        long over = 0;
        for (int i = 0; i < count; i++) if (buffer[i] >= 1f || buffer[i] <= -1f) over++;
        if (over > 0) Interlocked.Add(ref _clippedSamples, over);
    }

    // Snapshot RF counters since the previous call. Call once per log interval, from ONE thread only.
    public RfStats SnapshotRfStats()
    {
        long b = Interlocked.Read(ref _rfBuffers), v = Interlocked.Read(ref _rfVoiced);
        long s = Interlocked.Read(ref _rfSilent), d = Interlocked.Read(ref _rfDropEdges);
        long sum = Interlocked.Read(ref _rfSumMilliDbVoiced);
        long nb = b - _rfLastBuffers, nv = v - _rfLastVoiced, ns = s - _rfLastSilent;
        long nd = d - _rfLastDropEdges, nsum = sum - _rfLastSumMilliDb;
        _rfLastBuffers = b; _rfLastVoiced = v; _rfLastSilent = s; _rfLastDropEdges = d; _rfLastSumMilliDb = sum;
        float meanDb = nv > 0 ? nsum / (float)nv / 1000f : -120f;
        float vp = nb > 0 ? 100f * nv / nb : 0f;
        float sp = nb > 0 ? 100f * ns / nb : 0f;
        return new RfStats((int)nb, meanDb, vp, sp, (int)nd, CurrentFluxCv);
    }

    // --- Gain calibration (diagnostic) ----------------------------------------------------------
    // Fed from the same post-fader RMS the automixer's absolute thresholds read, so the operator's
    // reading means exactly what those constants mean. See CalibrationHistogram for the why.
    private readonly CalibrationHistogram _calibration = new();

    public CalibrationHistogram.Stats SnapshotCalibration() => _calibration.Snapshot();

    /// <summary>How long this strip's calibration has been accumulating, in ms — see the histogram.</summary>
    public long CalibrationAgeMs => _calibration.AgeMs;

    public void ResetCalibration() => _calibration.Reset();

    // True when the automixer is currently selecting this channel (gate winner / automix leader /
    // active priority mic) on any output it is routed to. Drives the per-input green "selected" LED.
    private bool _isAutoMixActive;
    public bool IsAutoMixActive
    {
        get => Volatile.Read(ref _isAutoMixActive);
        set => Volatile.Write(ref _isAutoMixActive, value);
    }

    private bool _isPriority;
    public bool IsPriority
    {
        get => Volatile.Read(ref _isPriority);
        set => Volatile.Write(ref _isPriority, value);
    }

    // Which side of a stereo endpoint to take (see ChannelSource). Changing it rebuilds the
    // conversion chain in place rather than restarting the capture, so two strips sharing one
    // endpoint don't have to re-open WASAPI when the operator flips a side.
    private int _source;
    public ChannelSource Source
    {
        get => (ChannelSource)Volatile.Read(ref _source);
        set
        {
            lock (_stateLock)
            {
                if ((ChannelSource)_source == value) return;
                Volatile.Write(ref _source, (int)value);
                if (_captureFifo == null || _captureFormat == null) return;
                _convertedSource = BuildConversionChain(_captureFifo.ToSampleProvider(), _captureFormat, value);
                ResetAnalysisState();
                ResetCalibration();
            }
        }
    }

    // Channel count of the live capture, so the UI can tell whether a side selection means anything.
    // 0 when stopped.
    public int CaptureChannels => _captureFormat?.Channels ?? 0;

    // Gentle high-pass to strip the rumble/handling/HVAC energy that dominates a DSP-free mic's
    // noise floor. Deliberately NOT a gate or a noise suppressor: it removes a fixed band, never
    // makes a level-dependent decision, so it cannot punch holes in sustained material the way the
    // speakerphones' suppression does (see the singing findings). 0 = off.
    private const float HighPassQ = 0.707f;   // Butterworth: maximally flat, no resonant bump at fc
    private int _highPassHz;
    private BiQuadFilter? _hpLeft, _hpRight;
    public int HighPassHz
    {
        get => Volatile.Read(ref _highPassHz);
        set
        {
            int hz = value <= 0 ? 0 : Math.Clamp(value, 20, 400);
            lock (_stateLock)
            {
                if (_highPassHz == hz) return;
                Volatile.Write(ref _highPassHz, hz);
                if (hz == 0) { _hpLeft = null; _hpRight = null; return; }
                _hpLeft = BiQuadFilter.HighPassFilter(InternalSampleRate, hz, HighPassQ);
                _hpRight = BiQuadFilter.HighPassFilter(InternalSampleRate, hz, HighPassQ);
            }
        }
    }

    public void SetAutoMixGain(int outputIndex, float gain)
    {
        if (outputIndex < 0 || outputIndex >= _outputCount) return;
        Volatile.Write(ref _autoMixGain[outputIndex], gain);
    }

    public float GetAutoMixGain(int outputIndex) =>
        outputIndex < 0 || outputIndex >= _outputCount ? 1f : Volatile.Read(ref _autoMixGain[outputIndex]);

    // True when the automixer is attenuating this channel on any output it is routed to.
    public bool IsDucking
    {
        get
        {
            int mask = Volatile.Read(ref _routeMask);
            for (int o = 0; o < _outputCount; o++)
            {
                if ((mask & (1 << o)) == 0) continue;
                if (Volatile.Read(ref _autoMixGain[o]) < 0.85f) return true;
            }
            return false;
        }
    }

    // Per-output duck state for the per-bus LEDs: routed to this output AND attenuated there.
    public bool IsDuckingOn(int outputIndex)
    {
        if (outputIndex < 0 || outputIndex >= _outputCount) return false;
        if ((Volatile.Read(ref _routeMask) & (1 << outputIndex)) == 0) return false;
        return Volatile.Read(ref _autoMixGain[outputIndex]) < 0.85f;
    }

    // Watchdog state: true while a capture is supposed to be running, plus the tick of the last
    // buffer the device delivered. A capture that stops firing DataAvailable (Anker USB/BT hiccup)
    // leaves IsCapturing true but LastDataTicks stale — that's what AudioEngine restarts on.
    private volatile bool _captureActive;
    public bool IsCapturing => _captureActive;

    private long _lastDataTicks;
    public long LastDataTicks => Volatile.Read(ref _lastDataTicks);

    // Last time this mic carried actual sound, as opposed to merely delivering buffers. Distinguishes
    // "dead / out of range / muted at the device" from "stalled" for the health banner: a stalled
    // capture stops delivering buffers entirely, a dead-but-connected one delivers digital silence.
    private long _lastSoundTicks;
    public long LastSoundTicks => Volatile.Read(ref _lastSoundTicks);

    private IWaveIn? _capture;
    private bool _ownsCapture;
    private WaveFormat? _captureFormat;
    private BufferedWaveProvider? _captureFifo;
    private ISampleProvider? _convertedSource;

    private float _gainLinear = 1f;
    private bool _muted;

    public PeakMeter InputPeak { get; } = new();
    public PeakMeter PostPeak { get; } = new();

    private MixRecorder? _analysisRecorder;
    public string? AnalysisRecordingPath => _analysisRecorder?.CurrentPath;
    public bool IsAnalysisRecording => _analysisRecorder?.IsRecording == true;

    /// <summary>
    /// Captures this mic, pre-fader and pre-filter, for offline analysis.
    ///
    /// Written MONO when this strip takes one side of a split receiver, because then both channels
    /// already carry the same transmitter — the side split duplicates it, measured at an L/R sample
    /// correlation of exactly 1.0000 — so the second channel is a verbatim copy costing half the file.
    /// A Stereo strip on a genuinely stereo device keeps both channels, where they can differ.
    /// </summary>
    public void StartAnalysisRecording(string path)
    {
        StopAnalysisRecording();
        bool mono = (ChannelSource)Volatile.Read(ref _source) != ChannelSource.Stereo;
        Volatile.Write(ref _analysisMono, mono ? 1 : 0);

        var recorder = new MixRecorder();
        recorder.Start(path, WaveFormat.CreateIeeeFloatWaveFormat(InternalSampleRate, mono ? 1 : InternalChannels));
        _analysisRecorder = recorder;
    }

    private int _analysisMono;
    private float[]? _monoScratch;

    // Takes the left of each interleaved pair: after the side split both hold the same transmitter.
    private void WriteAnalysis(float[] buffer, int count)
    {
        var rec = _analysisRecorder;
        if (rec == null) return;

        if (Volatile.Read(ref _analysisMono) == 0)
        {
            rec.WriteSamples(buffer, 0, count);
            return;
        }

        int frames = count / InternalChannels;
        if (_monoScratch == null || _monoScratch.Length < frames) _monoScratch = new float[frames];
        for (int f = 0; f < frames; f++) _monoScratch[f] = buffer[f * InternalChannels];
        rec.WriteSamples(_monoScratch, 0, frames);
    }

    public void StopAnalysisRecording()
    {
        var rec = _analysisRecorder;
        _analysisRecorder = null;
        rec?.Stop();
        rec?.Dispose();
    }

    public InputChannel(int outputCount)
    {
        _outputCount = outputCount;
        _outBuffers = new BufferedWaveProvider[outputCount];
        _outTrackers = new TrackingSampleProvider?[outputCount];
        _autoMixGain = new float[outputCount];
        _autoMixRamp = new float[outputCount];
        for (int i = 0; i < outputCount; i++)
        {
            _outBuffers[i] = CreateOutBuffer();
            _autoMixGain[i] = 1f;
            _autoMixRamp[i] = 1f;
        }
    }

    private readonly TrackingSampleProvider?[] _outTrackers;

    public ISampleProvider GetProviderForOutput(int outputIndex)
    {
        var buffer = _outBuffers[outputIndex];
        var tracker = new TrackingSampleProvider(buffer.ToSampleProvider(), buffer,
            () => IsCapturing && GetRoute(outputIndex));
        _outTrackers[outputIndex] = tracker;
        return tracker;
    }

    public long ReadSamplesForOutput(int outputIndex) =>
        _outTrackers[outputIndex]?.TotalSamplesReturned ?? 0;

    public long ReadCallsForOutput(int outputIndex) =>
        _outTrackers[outputIndex]?.ReadCallCount ?? 0;

    public void SetRoute(int outputIndex, bool on)
    {
        if (outputIndex < 0 || outputIndex >= _outputCount) return;
        int bit = 1 << outputIndex;
        int oldVal, newVal;
        do
        {
            oldVal = Volatile.Read(ref _routeMask);
            newVal = on ? (oldVal | bit) : (oldVal & ~bit);
        } while (Interlocked.CompareExchange(ref _routeMask, newVal, oldVal) != oldVal);
    }

    public bool GetRoute(int outputIndex)
    {
        if (outputIndex < 0 || outputIndex >= _outputCount) return false;
        return (Volatile.Read(ref _routeMask) & (1 << outputIndex)) != 0;
    }

    public float GainLinear
    {
        get => Volatile.Read(ref _gainLinear);
        set => Volatile.Write(ref _gainLinear, value < 0f ? 0f : value);
    }

    public bool Muted
    {
        get => Volatile.Read(ref _muted);
        set => Volatile.Write(ref _muted, value);
    }

    public void Start(AudioDeviceInfo deviceInfo)
    {
        var device = deviceInfo.Resolve()
            ?? throw new InvalidOperationException($"Capture device not found: {deviceInfo.FriendlyName}");
        Start(new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20),
              deviceInfo.FriendlyName, ownsCapture: true);
    }

    /// <summary>
    /// Start from any capture source. The device path builds a <see cref="WasapiCapture"/>; the replay
    /// rig supplies a file-backed source instead, so everything downstream of here runs identically
    /// whether the samples came from a mic or from a recorded session.
    /// </summary>
    public void Start(IWaveIn capture, string label, bool ownsCapture = false)
    {
        Stop();
        _label = label;
        _ownsCapture = ownsCapture;

        _captureFormat = capture.WaveFormat;
        Volatile.Write(ref _lastSoundTicks, Environment.TickCount64);
        _captureFifo = new BufferedWaveProvider(_captureFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(200),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        _convertedSource = BuildConversionChain(_captureFifo.ToSampleProvider(), _captureFormat, Source);
        int hz = Volatile.Read(ref _highPassHz);
        if (hz > 0)
        {
            _hpLeft = BiQuadFilter.HighPassFilter(InternalSampleRate, hz, HighPassQ);
            _hpRight = BiQuadFilter.HighPassFilter(InternalSampleRate, hz, HighPassQ);
        }

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
            {
                System.Diagnostics.Trace.WriteLine($"Capture stopped with error: {e.Exception}");
                AudioLog.Write($"Capture stopped with error on '{_label}': "
                             + $"{e.Exception.GetType().Name}: {e.Exception.Message}");
            }
        };
        _capture = capture;
        Volatile.Write(ref _lastDataTicks, Environment.TickCount64);
        _captureActive = true;
        capture.StartRecording();
    }

    public void Stop()
    {
        // Outside the lock: WaveFileWriter finalises the RIFF header on Dispose, and a strip removed by
        // a count change was previously left with a 0-frame header — a file that offline tools cannot
        // read at all. Nothing else calls this on the way out; StopRecording only walks the SURVIVING
        // channels, so the removed ones were simply abandoned.
        StopAnalysisRecording();
        lock (_stateLock)
        {
            _captureActive = false;
            if (_capture != null)
            {
                try { _capture.StopRecording(); } catch { }
                _capture.DataAvailable -= OnDataAvailable;
                // The replay rig owns its sources and reuses them across seeks/restarts, so only
                // dispose a capture we created ourselves.
                if (_ownsCapture) { try { _capture.Dispose(); } catch { } }
                _capture = null;
            }
            _captureFifo = null;
            _convertedSource = null;
            InputPeak.Reset();
            PostPeak.Reset();
            Volatile.Write(ref _currentLevelLinear, 0f);
            ResetAnalysisState();
            ResetCalibration();
            Interlocked.Exchange(ref _clippedSamples, 0);
            _hpLeft = null; _hpRight = null;
            _rfPrevVoiced = false;   // don't count a drop edge across a stop/restart
            Volatile.Write(ref _isAutoMixActive, false);
            for (int o = 0; o < _outputCount; o++) _autoMixRamp[o] = 1f;
            foreach (var buf in _outBuffers) buf.ClearBuffer();
        }
    }

    public void Dispose() => Stop();

    // The flux EMA only means anything for one continuous signal, so clear it whenever the signal
    // changes underneath it — a stop, or a switch to the other transmitter of a split endpoint.
    private void ResetAnalysisState()
    {
        Volatile.Write(ref _currentFluxCv, 0f);
        _prevMag = null; _fluxHasPrev = false; _fluxMean = 0f; _fluxVar = 0f; _fluxFill = 0;
    }

    private static float[] MakeHann(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        return w;
    }

    // WASAPI shared-mode delivers <512-frame buffers per DataAvailable, so a per-buffer 512-pt FFT
    // never ran and the CV froze at its startup value. Accumulate mono samples across buffers into a
    // FluxN window and run one FFT per completed window (see ComputeFluxWindow).
    private void ComputeFlux(float[] interleaved, int totalSamples)
    {
        int frames = totalSamples / 2;
        for (int f = 0; f < frames; f++)
        {
            _fluxAccum[_fluxFill++] = (interleaved[2 * f] + interleaved[2 * f + 1]) * 0.5f;
            if (_fluxFill == FluxN)
            {
                ComputeFluxWindow();
                Array.Copy(_fluxAccum, FluxHop, _fluxAccum, 0, FluxN - FluxHop);
                _fluxFill = FluxN - FluxHop;
            }
        }
    }

    // One 512-pt FFT of the accumulated (mono) window; flux = L2 distance of the normalized magnitude
    // spectrum from the previous window; EMA mean+variance -> coefficient of variation.
    private void ComputeFluxWindow()
    {
        for (int i = 0; i < FluxN; i++)
        {
            _fftBuf[i].X = _fluxAccum[i] * FluxWindow[i];
            _fftBuf[i].Y = 0f;
        }
        FastFourierTransform.FFT(true, FluxBits, _fftBuf);

        int bins = FluxN / 2;
        _magScratch ??= new float[bins];
        float sum = 0f;
        for (int k = 0; k < bins; k++)
        {
            float re = _fftBuf[k].X, im = _fftBuf[k].Y;
            float mg = (float)Math.Sqrt(re * re + im * im);
            _magScratch[k] = mg;
            sum += mg;
        }
        if (sum <= 1e-9f) return;
        float inv = 1f / sum;

        if (_fluxHasPrev && _prevMag != null)
        {
            double acc = 0;
            for (int k = 0; k < bins; k++) { float d = _magScratch[k] * inv - _prevMag[k]; acc += (double)d * d; }
            float flux = (float)Math.Sqrt(acc);
            float delta = flux - _fluxMean;
            _fluxMean += FluxEma * delta;
            _fluxVar = (1 - FluxEma) * (_fluxVar + FluxEma * delta * delta);
            float cv = _fluxMean > 1e-6f ? (float)Math.Sqrt(_fluxVar) / _fluxMean : 0f;
            Volatile.Write(ref _currentFluxCv, cv);
        }
        _prevMag ??= new float[bins];
        for (int k = 0; k < bins; k++) _prevMag[k] = _magScratch[k] * inv;
        _fluxHasPrev = true;
    }

    // Pushing to a routed output should never throw (the buffer discards on overflow). If it does, the
    // channel is silently dead on that bus, so record it once rather than swallowing it forever.
    private void LogPushFailure(int outputIndex, Exception ex)
    {
        if (_pushErrorLogged) return;
        _pushErrorLogged = true;
        System.Diagnostics.Trace.WriteLine($"Input '{_label}' push to output {outputIndex} failed: {ex}");
        AudioLog.Write($"Input '{_label}' push to output {outputIndex} failed: {ex.GetType().Name}: {ex.Message}");
    }

    // Interleaved stereo, so each side keeps its own biquad state. The pair is swapped in as a unit
    // by the HighPassHz setter; a null pair means off.
    private void ApplyHighPass(float[] interleaved, int count)
    {
        var l = _hpLeft;
        var r = _hpRight;
        if (l == null || r == null) return;
        for (int i = 0; i + 1 < count; i += 2)
        {
            interleaved[i] = l.Transform(interleaved[i]);
            interleaved[i + 1] = r.Transform(interleaved[i + 1]);
        }
    }

    private static bool IsUnity(float g) => g > 0.9999f && g < 1.0001f;

    private static BufferedWaveProvider CreateOutBuffer()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(InternalSampleRate, InternalChannels);
        return new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
    }

    // Silence written into a freshly cleared buffer so the standing backlog is a chosen number rather
    // than whatever the startup race happens to leave. Without it the two buses get different depths
    // from the same input: measured 2026-09-20, the headset bus sat at 0 ms in 15.8% of samples while
    // CABLE (same channel, same push) sat at 0 ms in 1.1%. An empty BufferedWaveProvider with
    // ReadFully=true pads with ZEROS, so every one of those is a hole in the monitor feed. 40 ms is
    // inaudible as latency on a monitor path and well inside the 200 ms end-to-end budget.
    public const int OutputPrimeMs = 40;

    public void ClearOutputBuffer(int outputIndex)
    {
        if (outputIndex < 0 || outputIndex >= _outBuffers.Length) return;
        var buf = _outBuffers[outputIndex];
        buf.ClearBuffer();
        int bytes = buf.WaveFormat.AverageBytesPerSecond * OutputPrimeMs / 1000;
        bytes -= bytes % buf.WaveFormat.BlockAlign;
        buf.AddSamples(new byte[bytes], 0, bytes);
    }

    /// <summary>
    /// Times the bus asked for audio that was not there yet, so the zero-fill above is measurable
    /// rather than inferred from a 1 Hz buffer-depth sample.
    /// </summary>
    public long UnderrunsForOutput(int outputIndex) =>
        outputIndex < 0 || outputIndex >= _outputCount ? 0 : _outTrackers[outputIndex]?.Underruns ?? 0;

    public int BufferedMs(int outputIndex)
    {
        if (outputIndex < 0 || outputIndex >= _outBuffers.Length) return 0;
        return (int)_outBuffers[outputIndex].BufferedDuration.TotalMilliseconds;
    }

    internal static ISampleProvider BuildConversionChain(ISampleProvider source, WaveFormat fmt, ChannelSource side)
    {
        ISampleProvider provider = source;

        if (fmt.SampleRate != InternalSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, InternalSampleRate);
        }

        // The side is taken here, upstream of every tap, so the rest of the pipeline — meters,
        // analysis recorder, level/flux/RF, automix gain — sees only this transmitter.
        if (side != ChannelSource.Stereo && provider.WaveFormat.Channels >= 2)
        {
            var one = new MultiplexingSampleProvider(new[] { provider }, 1);
            one.ConnectInputToOutput(side == ChannelSource.Left ? 0 : 1, 0);
            provider = one;
        }

        if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            provider = new MultiplexingSampleProvider(new[] { provider }, 2);
        }

        return provider;
    }

    // Single pass for RMS + peak, latched for the automixer (which runs off-thread and only ever
    // reads these two volatiles). Returns the RMS so the caller can gate the flux/RF work on it.
    private float MeasureAndLatchLevels(float[] samples, int count)
    {
        double sumSq = 0;
        for (int i = 0; i < count; i++)
        {
            float s = samples[i];
            sumSq += (double)s * s;
        }
        float rms = (float)Math.Sqrt(sumSq / count);
        Volatile.Write(ref _currentLevelLinear, rms);
        return rms;
    }

    // RF-health tally (diagnostic; see SnapshotRfStats). Voiced/silent are mutually exclusive (the
    // voice threshold is far above the silence floor); a voiced→silent transition is a dropout "edge".
    private void TallyRfHealth(float rms, bool voiced)
    {
        Interlocked.Increment(ref _rfBuffers);
        if (voiced)
        {
            Interlocked.Increment(ref _rfVoiced);
            Interlocked.Add(ref _rfSumMilliDbVoiced, (long)(20000.0 * Math.Log10(rms)));
        }
        else if (rms < RfSilenceRms)
        {
            Interlocked.Increment(ref _rfSilent);
            if (_rfPrevVoiced) Interlocked.Increment(ref _rfDropEdges);
        }
        _rfPrevVoiced = voiced;
    }

    // Fan the finished buffer out to each routed output, applying that output's automix gain with an
    // intra-buffer ramp (no zipper). Both scratch buffers are rented lazily and at most once per call:
    // the unity copy is made once and shared by every unity output, while the scaled buffers are
    // rewritten per output (each output ramps to its own gain).
    private void PushToOutputs(float[] samples, int count)
    {
        int byteCount = count * sizeof(float);
        int mask = Volatile.Read(ref _routeMask);
        byte[]? unityBytes = null;
        float[]? scaledFloats = null;
        byte[]? scaledBytes = null;
        try
        {
            for (int o = 0; o < _outputCount; o++)
            {
                // Not routed: keep the ramp origin current so re-enabling doesn't jump from a stale gain.
                if ((mask & (1 << o)) == 0) { _autoMixRamp[o] = Volatile.Read(ref _autoMixGain[o]); continue; }

                float target = Volatile.Read(ref _autoMixGain[o]);
                float start = _autoMixRamp[o];
                if (IsUnity(target) && IsUnity(start))
                {
                    if (unityBytes == null)
                    {
                        unityBytes = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
                        Buffer.BlockCopy(samples, 0, unityBytes, 0, byteCount);
                    }
                    try { _outBuffers[o].AddSamples(unityBytes, 0, byteCount); }
                    catch (Exception ex) { LogPushFailure(o, ex); }
                }
                else
                {
                    scaledFloats ??= System.Buffers.ArrayPool<float>.Shared.Rent(count);
                    scaledBytes ??= System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
                    float g = start;
                    float step = (target - start) / count;
                    for (int i = 0; i < count; i++) { scaledFloats[i] = samples[i] * g; g += step; }
                    Buffer.BlockCopy(scaledFloats, 0, scaledBytes, 0, byteCount);
                    try { _outBuffers[o].AddSamples(scaledBytes, 0, byteCount); }
                    catch (Exception ex) { LogPushFailure(o, ex); }
                }
                _autoMixRamp[o] = target;
            }
        }
        finally
        {
            if (unityBytes != null) System.Buffers.ArrayPool<byte>.Shared.Return(unityBytes);
            if (scaledFloats != null) System.Buffers.ArrayPool<float>.Shared.Return(scaledFloats);
            if (scaledBytes != null) System.Buffers.ArrayPool<byte>.Shared.Return(scaledBytes);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var fifo = _captureFifo;
        var converted = _convertedSource;
        if (fifo == null || converted == null || _captureFormat == null) return;
        if (e.BytesRecorded <= 0) return;

        Volatile.Write(ref _lastDataTicks, Environment.TickCount64);

        fifo.AddSamples(e.Buffer, 0, e.BytesRecorded);

        int captureFrames = e.BytesRecorded / _captureFormat.BlockAlign;
        long convFrames = (long)captureFrames * InternalSampleRate / _captureFormat.SampleRate;
        int sampleCount = (int)(convFrames * InternalChannels);
        if (sampleCount <= 0) return;

        var rented = System.Buffers.ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            int read = converted.Read(rented, 0, sampleCount);
            if (read <= 0) return;

            InputPeak.Observe(rented, read);
            CountClipping(rented, read);

            WriteAnalysis(rented, read);

            // After the analysis tap on purpose: "record all inputs" must stay an unprocessed capture,
            // or every offline tool would be measuring our own filter instead of the mic.
            ApplyHighPass(rented, read);

            float gain = Muted ? 0f : GainLinear;
            if (gain != 1f)
            {
                for (int i = 0; i < read; i++) rented[i] *= gain;
            }

            PostPeak.Observe(rented, read);

            float rmsNow = MeasureAndLatchLevels(rented, read);
            if (rmsNow > RfSilenceRms) Volatile.Write(ref _lastSoundTicks, Environment.TickCount64);
            bool voiced = rmsNow > FluxVoiceRms;
            if (voiced) ComputeFlux(rented, read);
            TallyRfHealth(rmsNow, voiced);
            _calibration.Add(rmsNow, voiced);

            PushToOutputs(rented, read);
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(rented);
        }
    }
}
