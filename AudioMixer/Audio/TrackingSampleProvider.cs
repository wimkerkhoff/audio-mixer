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
    ///
    /// Only counted while <c>feeding</c> says the buffer is SUPPOSED to be filled. An unrouted strip,
    /// or one with no live capture, has an empty feed buffer by design -- that is correct silence,
    /// not a hole -- and the bus still reads it every ~10 ms, so without this every such pair climbed
    /// ~100/s forever (measured 2026-09-23: five unrouted pairs at 5175 after ~50 s beside two routed
    /// pairs at 3-5) and a real underrun was indistinguishable from a switched-off mic.
    /// </summary>
    public long Underruns;

    private readonly Func<bool>? _feeding;

    public TrackingSampleProvider(ISampleProvider source, BufferedWaveProvider? watch = null,
                                  Func<bool>? feeding = null)
    {
        _source = source;
        _watch = watch;
        _feeding = feeding;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_watch != null && _watch.BufferedBytes < count * sizeof(float)
            && (_feeding == null || _feeding())) Underruns++;
        int n = _source.Read(buffer, offset, count);
        TotalSamplesReturned += n;
        ReadCallCount++;
        return n;
    }
}
