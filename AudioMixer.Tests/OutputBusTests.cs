using AudioMixer.Audio;
using NAudio.Wave;

namespace AudioMixer.Tests;

public class OutputBusTests
{
    private sealed class FakePlayer : IWavePlayer
    {
        public PlaybackState PlaybackState { get; set; } = PlaybackState.Playing;
        public float Volume { get; set; } = 1f;
        public WaveFormat OutputWaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider waveProvider) { }
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Dispose() { }
    }

    [Fact]
    public void ALateStopFromThePreviousPlayer_DoesNotMarkTheLiveOneStopped()
    {
        using var bus = new OutputBus();
        var old = new FakePlayer();
        var live = new FakePlayer();
        bus.AdoptPlayer(old);
        bus.AdoptPlayer(live); // a restart installs the next player before the old event arrives

        bus.OnPlaybackStopped(old, null);

        Assert.True(bus.IsPlaying);
    }

    [Fact]
    public void TheLivePlayerStopping_IsReported()
    {
        using var bus = new OutputBus();
        var live = new FakePlayer();
        bus.AdoptPlayer(live);

        bus.OnPlaybackStopped(live, new InvalidOperationException("device invalidated"));

        Assert.False(bus.IsPlaying);
    }
}
