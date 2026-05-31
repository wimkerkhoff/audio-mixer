using NAudio.Wave;

namespace AudioMixer.Audio;

public sealed class TapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    public PeakMeter Meter { get; } = new();
    public MixRecorder? Recorder { get; set; }

    public TapSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public long TotalSamplesRead { get; private set; }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _source.Read(buffer, offset, count);
        if (n > 0)
        {
            Meter.Observe(buffer, offset, n);
            Recorder?.WriteSamples(buffer, offset, n);
            TotalSamplesRead += n;
        }
        return n;
    }
}
