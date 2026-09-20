using NAudio.Wave;

namespace AudioMixer.Audio;

public sealed class TrackingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BufferedWaveProvider? _watch;
    public long TotalSamplesReturned;
    public long ReadCallCount;

    /// <summary>
    /// Times the bus asked for more than the feed buffer held. Counted BEFORE the read, because the
    /// buffer runs with ReadFully=true and pads the shortfall with zeros — so the read always looks
    /// complete and the hole is silent and otherwise invisible.
    /// </summary>
    public long Underruns;

    public TrackingSampleProvider(ISampleProvider source, BufferedWaveProvider? watch = null)
    {
        _source = source;
        _watch = watch;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_watch != null && _watch.BufferedBytes < count * sizeof(float)) Underruns++;
        int n = _source.Read(buffer, offset, count);
        TotalSamplesReturned += n;
        ReadCallCount++;
        return n;
    }
}
