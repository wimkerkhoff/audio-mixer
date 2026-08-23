using AudioMixer.Audio;
using NAudio.Wave;

namespace AudioMixer.Tests;

/// <summary>
/// The capture conversion chain, which is where a split receiver's two transmitters are separated.
/// An L/R mix-up here is completely silent — you get the wrong person on the wrong strip, with
/// correct-looking meters — so the side selection is pinned down by test rather than by listening.
/// </summary>
public class ConversionChainTests
{
    private const float Left = 0.25f;
    private const float Right = 0.75f;

    /// <summary>48 kHz stereo float32 (the internal format) with a constant, distinct value per side.</summary>
    private static ISampleProvider StereoSource(int frames = 4800) =>
        new ConstantSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(InputChannel.InternalSampleRate, 2), Left, Right, frames);

    private static float[] Read(ISampleProvider provider, int samples)
    {
        var buf = new float[samples];
        int read = provider.Read(buf, 0, samples);
        Assert.Equal(samples, read);
        return buf;
    }

    [Fact]
    public void StereoPassesBothSidesThrough()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(InputChannel.InternalSampleRate, 2);
        var chain = InputChannel.BuildConversionChain(StereoSource(), fmt, ChannelSource.Stereo);

        var buf = Read(chain, 512);

        Assert.Equal(Left, buf[0]);
        Assert.Equal(Right, buf[1]);
    }

    [Fact]
    public void LeftTakesOnlyTheLeftTransmitter()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(InputChannel.InternalSampleRate, 2);
        var chain = InputChannel.BuildConversionChain(StereoSource(), fmt, ChannelSource.Left);

        var buf = Read(chain, 512);

        // Still stereo out — the same value now on BOTH sides, since one mic feeds the whole strip.
        Assert.All(buf, s => Assert.Equal(Left, s));
    }

    [Fact]
    public void RightTakesOnlyTheRightTransmitter()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(InputChannel.InternalSampleRate, 2);
        var chain = InputChannel.BuildConversionChain(StereoSource(), fmt, ChannelSource.Right);

        var buf = Read(chain, 512);

        Assert.All(buf, s => Assert.Equal(Right, s));
    }

    [Fact]
    public void EitherSideStillProducesTheInternalStereoFormat()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(InputChannel.InternalSampleRate, 2);

        foreach (var side in new[] { ChannelSource.Stereo, ChannelSource.Left, ChannelSource.Right })
        {
            var chain = InputChannel.BuildConversionChain(StereoSource(), fmt, side);
            Assert.Equal(InputChannel.InternalChannels, chain.WaveFormat.Channels);
            Assert.Equal(InputChannel.InternalSampleRate, chain.WaveFormat.SampleRate);
        }
    }

    /// <summary>A side selection on a mono mic has nothing to split, and must not break the channel.</summary>
    [Fact]
    public void ASideSelectionOnAMonoCaptureIsIgnored()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(InputChannel.InternalSampleRate, 1);
        var mono = new ConstantSampleProvider(fmt, Left, Left, 4800);
        var chain = InputChannel.BuildConversionChain(mono, fmt, ChannelSource.Right);

        Assert.Equal(InputChannel.InternalChannels, chain.WaveFormat.Channels);
        Assert.All(Read(chain, 512), s => Assert.Equal(Left, s));
    }

    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private readonly float _left, _right;
        private int _remaining;

        public ConstantSampleProvider(WaveFormat format, float left, float right, int frames)
        {
            WaveFormat = format;
            _left = left;
            _right = right;
            _remaining = frames * format.Channels;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _remaining);
            for (int i = 0; i < n; i++)
            {
                buffer[offset + i] = WaveFormat.Channels == 1 || (offset + i) % 2 == 0 ? _left : _right;
            }
            _remaining -= n;
            return n;
        }
    }
}
