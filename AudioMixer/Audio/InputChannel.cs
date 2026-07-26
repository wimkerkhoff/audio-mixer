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

    // Peak (max |sample|) latched per buffer. AutoMixer divides this by the RMS to get a crest
    // factor — a closeness/clarity proxy that survives the speakerphones' own AGC (AGC normalizes
    // RMS but can't un-smear the reverb that fills a distant mic's envelope troughs).
    private float _currentPeakLinear;
    public float CurrentPeakLinear => Volatile.Read(ref _currentPeakLinear);

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
    private int _fluxDbgLog;                     // one-shot: log first buffers' frame sizes (opt-in AudioLog)
    private string _label = "";

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

    // Smoothed crest-derived clarity weight (0..1, NaN when no recent speech), written by AutoMixer
    // for display. Higher = closer/cleaner mic.
    private float _clarity = float.NaN;
    public float Clarity
    {
        get => Volatile.Read(ref _clarity);
        set => Volatile.Write(ref _clarity, value);
    }

    // True when the automixer is currently selecting this channel (gate winner / share leader /
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

    private WasapiCapture? _capture;
    private WaveFormat? _captureFormat;
    private BufferedWaveProvider? _captureFifo;
    private ISampleProvider? _convertedSource;
    private DelayLine? _delayLine;

    private float _gainLinear = 1f;
    private bool _muted;

    public PeakMeter InputPeak { get; } = new();
    public PeakMeter PostPeak { get; } = new();

    private MixRecorder? _analysisRecorder;
    public string? AnalysisRecordingPath => _analysisRecorder?.CurrentPath;
    public bool IsAnalysisRecording => _analysisRecorder?.IsRecording == true;

    public void StartAnalysisRecording(string path)
    {
        StopAnalysisRecording();
        var recorder = new MixRecorder();
        recorder.Start(path, WaveFormat.CreateIeeeFloatWaveFormat(InternalSampleRate, InternalChannels));
        _analysisRecorder = recorder;
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
        var tracker = new TrackingSampleProvider(_outBuffers[outputIndex].ToSampleProvider());
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

    public int DelayMs
    {
        get
        {
            var d = _delayLine;
            return d == null ? 0 : (int)Math.Round(d.DelaySamples * 1000.0 / InternalSampleRate);
        }
        set
        {
            var d = _delayLine;
            if (d == null) return;
            int samples = (int)Math.Round(Math.Max(0, value) * InternalSampleRate / 1000.0);
            d.DelaySamples = samples;
        }
    }

    public void Start(AudioDeviceInfo deviceInfo)
    {
        Stop();
        var device = deviceInfo.Resolve()
            ?? throw new InvalidOperationException($"Capture device not found: {deviceInfo.FriendlyName}");
        _label = deviceInfo.FriendlyName;

        var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
        _captureFormat = capture.WaveFormat;
        _captureFifo = new BufferedWaveProvider(_captureFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(200),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        _convertedSource = BuildConversionChain(_captureFifo.ToSampleProvider(), _captureFormat);
        _delayLine = new DelayLine(InternalSampleRate * InternalChannels * 2);

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                System.Diagnostics.Trace.WriteLine($"Capture stopped with error: {e.Exception}");
        };
        _capture = capture;
        Volatile.Write(ref _lastDataTicks, Environment.TickCount64);
        _captureActive = true;
        capture.StartRecording();
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            _captureActive = false;
            if (_capture != null)
            {
                try { _capture.StopRecording(); } catch { }
                _capture.DataAvailable -= OnDataAvailable;
                try { _capture.Dispose(); } catch { }
                _capture = null;
            }
            _captureFifo = null;
            _convertedSource = null;
            _delayLine = null;
            InputPeak.Reset();
            PostPeak.Reset();
            Volatile.Write(ref _currentLevelLinear, 0f);
            Volatile.Write(ref _currentPeakLinear, 0f);
            Volatile.Write(ref _currentFluxCv, 0f);
            _prevMag = null; _fluxHasPrev = false; _fluxMean = 0f; _fluxVar = 0f; _fluxFill = 0;
            _rfPrevVoiced = false;   // don't count a drop edge across a stop/restart
            Volatile.Write(ref _clarity, float.NaN);
            Volatile.Write(ref _isAutoMixActive, false);
            for (int o = 0; o < _outputCount; o++) _autoMixRamp[o] = 1f;
            foreach (var buf in _outBuffers) buf.ClearBuffer();
        }
    }

    public void Dispose() => Stop();

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

    public void ClearOutputBuffer(int outputIndex)
    {
        if (outputIndex < 0 || outputIndex >= _outBuffers.Length) return;
        _outBuffers[outputIndex].ClearBuffer();
    }

    public int BufferedMs(int outputIndex)
    {
        if (outputIndex < 0 || outputIndex >= _outBuffers.Length) return 0;
        return (int)_outBuffers[outputIndex].BufferedDuration.TotalMilliseconds;
    }

    private static ISampleProvider BuildConversionChain(ISampleProvider source, WaveFormat fmt)
    {
        ISampleProvider provider = source;

        if (fmt.SampleRate != InternalSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, InternalSampleRate);
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

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var fifo = _captureFifo;
        var converted = _convertedSource;
        var delay = _delayLine;
        if (fifo == null || converted == null || delay == null || _captureFormat == null) return;
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

            if (_fluxDbgLog < 4) { _fluxDbgLog++; AudioLog.Write($"[flux] '{_label}' srcRate={_captureFormat.SampleRate} bufFrames={read / 2} (need {FluxN})"); }

            InputPeak.Observe(rented, read);

            _analysisRecorder?.WriteSamples(rented, 0, read);

            float gain = Muted ? 0f : GainLinear;
            if (gain != 1f)
            {
                for (int i = 0; i < read; i++) rented[i] *= gain;
            }

            delay.ProcessInPlace(rented, read);

            PostPeak.Observe(rented, read);

            double sumSq = 0;
            float peak = 0f;
            for (int i = 0; i < read; i++)
            {
                float s = rented[i];
                sumSq += (double)s * s;
                float a = s < 0 ? -s : s;
                if (a > peak) peak = a;
            }
            float rmsNow = (float)Math.Sqrt(sumSq / read);
            Volatile.Write(ref _currentLevelLinear, rmsNow);
            Volatile.Write(ref _currentPeakLinear, peak);
            if (rmsNow > FluxVoiceRms) ComputeFlux(rented, read);

            // RF-health tally (diagnostic; see SnapshotRfStats). Voiced/silent are mutually exclusive
            // (voice threshold >> silence floor); a voiced→silent transition is a dropout "edge".
            bool rfVoiced = rmsNow > FluxVoiceRms;
            Interlocked.Increment(ref _rfBuffers);
            if (rfVoiced)
            {
                Interlocked.Increment(ref _rfVoiced);
                Interlocked.Add(ref _rfSumMilliDbVoiced, (long)(20000.0 * Math.Log10(rmsNow)));
            }
            else if (rmsNow < RfSilenceRms)
            {
                Interlocked.Increment(ref _rfSilent);
                if (_rfPrevVoiced) Interlocked.Increment(ref _rfDropEdges);
            }
            _rfPrevVoiced = rfVoiced;

            int byteCount = read * sizeof(float);
            int mask = Volatile.Read(ref _routeMask);
            byte[]? unityBytes = null;
            float[]? scaledFloats = null;
            byte[]? scaledBytes = null;
            try
            {
                for (int o = 0; o < _outputCount; o++)
                {
                    if ((mask & (1 << o)) == 0) { _autoMixRamp[o] = Volatile.Read(ref _autoMixGain[o]); continue; }

                    float target = Volatile.Read(ref _autoMixGain[o]);
                    float start = _autoMixRamp[o];
                    if (IsUnity(target) && IsUnity(start))
                    {
                        if (unityBytes == null)
                        {
                            unityBytes = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
                            Buffer.BlockCopy(rented, 0, unityBytes, 0, byteCount);
                        }
                        try { _outBuffers[o].AddSamples(unityBytes, 0, byteCount); } catch { }
                    }
                    else
                    {
                        scaledFloats ??= System.Buffers.ArrayPool<float>.Shared.Rent(read);
                        scaledBytes ??= System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
                        float g = start;
                        float step = (target - start) / read;
                        for (int i = 0; i < read; i++) { scaledFloats[i] = rented[i] * g; g += step; }
                        Buffer.BlockCopy(scaledFloats, 0, scaledBytes, 0, byteCount);
                        try { _outBuffers[o].AddSamples(scaledBytes, 0, byteCount); } catch { }
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
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(rented);
        }
    }
}
