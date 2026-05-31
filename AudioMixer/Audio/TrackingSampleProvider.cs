using NAudio.Wave;

namespace AudioMixer.Audio;

public sealed class TrackingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    public long TotalSamplesReturned;
    public long ReadCallCount;

    public TrackingSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _source.Read(buffer, offset, count);
        TotalSamplesReturned += n;
        ReadCallCount++;
        return n;
    }
}
