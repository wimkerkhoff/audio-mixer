using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioMixer.Audio;

public sealed class OutputBus : IDisposable
{
    public const int InternalSampleRate = InputChannel.InternalSampleRate;
    public const int InternalChannels = InputChannel.InternalChannels;

    private readonly object _lock = new();
    private WasapiOut? _output;
    private TapSampleProvider? _tap;
    private VolumeSampleProvider? _volumeProvider;

    private float _volume = 1f;
    // Final output trim applied AFTER the peak/recorder tap, so the meter and recordings reflect
    // the full mix and this only sets how loud the physical device (e.g. headset) plays.
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value < 0f ? 0f : value;
            var v = _volumeProvider;
            if (v != null) v.Volume = _volume;
        }
    }

    public WaveFormat InternalFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(InternalSampleRate, InternalChannels);

    public PeakMeter OutputPeak => _tap?.Meter ?? _placeholderMeter;
    private readonly PeakMeter _placeholderMeter = new();

    public MixRecorder? Recorder
    {
        get => _tap?.Recorder;
        set { if (_tap != null) _tap.Recorder = value; }
    }

    public void Start(AudioDeviceInfo deviceInfo, IEnumerable<ISampleProvider> inputs)
    {
        Stop();
        AudioLog.Write($"OutputBus.Start device='{deviceInfo.FriendlyName}'");
        var device = deviceInfo.Resolve()
            ?? throw new InvalidOperationException($"Render device not found: {deviceInfo.FriendlyName}");

        var mixer = new MixingSampleProvider(InternalFormat) { ReadFully = true };
        int inputCount = 0;
        foreach (var input in inputs) { mixer.AddMixerInput(input); inputCount++; }
        AudioLog.Write($"  mixer inputs={inputCount} format={mixer.WaveFormat}");

        var tap = new TapSampleProvider(mixer);
        var volume = new VolumeSampleProvider(tap) { Volume = _volume };

        IWaveProvider source = volume.ToWaveProvider();
        AudioLog.Write($"  source format={source.WaveFormat}");

        var deviceFormat = device.AudioClient.MixFormat;
        AudioLog.Write($"  device mixFormat={deviceFormat}");
        if (!FormatsMatch(source.WaveFormat, deviceFormat))
        {
            AudioLog.Write($"  formats differ -> adding MediaFoundationResampler");
            source = new MediaFoundationResampler(source, deviceFormat) { ResamplerQuality = 60 };
        }
        else
        {
            AudioLog.Write($"  formats match -> no resampler");
        }

        WasapiOut output;
        try
        {
            output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
            output.Init(source);
        }
        catch (Exception ex)
        {
            AudioLog.Write($"  Init(latency=50) failed: {ex.GetType().Name}: {ex.Message}; retrying with latency=200");
            output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 200);
            output.Init(source);
        }
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null)
                AudioLog.Write($"OutputBus playback STOPPED with error: {e.Exception}");
            else
                AudioLog.Write($"OutputBus playback stopped (no error)");
        };
        output.Play();
        AudioLog.Write($"  Play() called; PlaybackState={output.PlaybackState}");

        lock (_lock)
        {
            _tap = tap;
            _volumeProvider = volume;
            _output = output;
        }
    }

    public bool IsPlaying
    {
        get
        {
            var o = _output;
            return o != null && o.PlaybackState == NAudio.Wave.PlaybackState.Playing;
        }
    }

    public long TotalSamplesRead => _tap?.TotalSamplesRead ?? 0;

    public void Stop()
    {
        WasapiOut? prevOutput;
        lock (_lock)
        {
            prevOutput = _output;
            _output = null;
            _tap = null;
            _volumeProvider = null;
        }
        if (prevOutput != null)
        {
            try { prevOutput.Stop(); } catch { }
            try { prevOutput.Dispose(); } catch { }
        }
    }

    public void Dispose() => Stop();

    private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");

    private static bool FormatsMatch(WaveFormat a, WaveFormat b)
    {
        if (a.SampleRate != b.SampleRate) return false;
        if (a.Channels != b.Channels) return false;
        if (a.BitsPerSample != b.BitsPerSample) return false;
        if (a.Encoding == b.Encoding) return true;

        bool aFloat = IsFloat(a);
        bool bFloat = IsFloat(b);
        if (aFloat && bFloat) return true;

        bool aPcm = IsPcm(a);
        bool bPcm = IsPcm(b);
        return aPcm && bPcm;
    }

    private static bool IsFloat(WaveFormat f)
    {
        if (f.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        return f is WaveFormatExtensible ext && ext.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
    }

    private static bool IsPcm(WaveFormat f)
    {
        if (f.Encoding == WaveFormatEncoding.Pcm) return true;
        return f is WaveFormatExtensible ext && ext.SubFormat == KSDATAFORMAT_SUBTYPE_PCM;
    }
}
