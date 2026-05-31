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
    private int _routeMask;
    private readonly object _stateLock = new();

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
        for (int i = 0; i < outputCount; i++)
        {
            _outBuffers[i] = CreateOutBuffer();
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
        capture.StartRecording();
    }

    public void Stop()
    {
        lock (_stateLock)
        {
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
            foreach (var buf in _outBuffers) buf.ClearBuffer();
        }
    }

    public void Dispose() => Stop();

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

            _analysisRecorder?.WriteSamples(rented, 0, read);

            float gain = Muted ? 0f : GainLinear;
            if (gain != 1f)
            {
                for (int i = 0; i < read; i++) rented[i] *= gain;
            }

            delay.ProcessInPlace(rented, read);

            PostPeak.Observe(rented, read);

            int byteCount = read * sizeof(float);
            var byteBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                Buffer.BlockCopy(rented, 0, byteBuf, 0, byteCount);
                int mask = Volatile.Read(ref _routeMask);
                for (int o = 0; o < _outputCount; o++)
                {
                    if ((mask & (1 << o)) != 0)
                    {
                        try { _outBuffers[o].AddSamples(byteBuf, 0, byteCount); } catch { }
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(byteBuf);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(rented);
        }
    }
}
