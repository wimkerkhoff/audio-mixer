using AudioMixer.Audio;
using NAudio.Wave;

namespace AudioMixer.Tests;

/// <summary>
/// The underrun count is the one reading that separates the silent-hole crackle from normal buffer
/// oscillation, so a false positive is as bad as a miss. Until 2026-09-23 it counted every read of an
/// unrouted strip's feed buffer -- empty by design -- and climbed ~100/s on every such pair.
/// </summary>
public class UnderrunCountTests
{
    private static (TrackingSampleProvider Tracker, BufferedWaveProvider Buffer) Rig(Func<bool>? feeding)
    {
        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
        {
            ReadFully = true,
        };
        return (new TrackingSampleProvider(buffer.ToSampleProvider(), buffer, feeding), buffer);
    }

    private static void ReadOnce(TrackingSampleProvider t) => t.Read(new float[960], 0, 960);

    [Fact]
    public void AnEmptyBufferThatShouldBeFeedingIsAnUnderrun()
    {
        var (t, _) = Rig(() => true);
        ReadOnce(t);
        Assert.Equal(1, t.Underruns);
    }

    [Fact]
    public void AnUnroutedOrIdleStripsEmptyBufferIsNot()
    {
        var (t, _) = Rig(() => false);
        for (int i = 0; i < 100; i++) ReadOnce(t);
        Assert.Equal(0, t.Underruns);
    }

    [Fact]
    public void AFullBufferIsNeverAnUnderrun()
    {
        var (t, b) = Rig(() => true);
        var bytes = new byte[960 * sizeof(float)];
        b.AddSamples(bytes, 0, bytes.Length);
        ReadOnce(t);
        Assert.Equal(0, t.Underruns);
    }
}
