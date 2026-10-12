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

    /// <summary>
    /// 2026-10-11: unplugging the receivers counted ~180 per strip per bus in the 1-2 s between the
    /// device ending the capture and the watchdog giving up — the session record read 320-362 for a
    /// service whose live count was [0,1]. The watchdog still needs IsCapturing true.
    /// </summary>
    [Fact]
    public void ACaptureTheDeviceEnded_IsNotUnderrunning_ButStillWatched()
    {
        using var ch = new InputChannel(1);
        var t = (TrackingSampleProvider)ch.GetProviderForOutput(0);
        ch.SetRoute(0, true);
        var capture = new EndableCapture();
        ch.Start(capture, "test");

        ReadOnce(t);
        long whileLive = t.Underruns;

        capture.End(new InvalidOperationException("0x88890004"));
        for (int i = 0; i < 100; i++) ReadOnce(t);

        Assert.Equal(1, whileLive);
        Assert.Equal(whileLive, t.Underruns);
        Assert.True(ch.IsCapturing);
        Assert.False(ch.IsFeeding);

        ch.Start(new EndableCapture(), "test");
        Assert.True(ch.IsFeeding);
    }

    private sealed class EndableCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { }
        public void StopRecording() { }
        public void End(Exception e) => RecordingStopped?.Invoke(this, new StoppedEventArgs(e));
        public void Dispose() => DataAvailable = null;
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
