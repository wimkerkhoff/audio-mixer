using AudioMixer.Audio;
using NAudio.Wave;
using Xunit;

namespace AudioMixer.Tests;

/// <summary>
/// Provider-level behaviour of the leveler: bypass, engagement and the limiter ceiling. Driven
/// through a fake source, so no device and no window — the repo's rule for what may be unit-tested.
/// </summary>
public class BusLevelerProviderTests
{
    private sealed class FakeSource : ISampleProvider
    {
        private readonly Func<int, float> _sample;
        private int _n;

        public FakeSource(Func<int, float> sample) => _sample = sample;

        /// <summary>Everything this source handed downstream, so a bypass can be checked exactly.</summary>
        public List<float> Produced { get; } = new();

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float v = _sample(_n++);
                buffer[offset + i] = v;
                Produced.Add(v);
            }
            return count;
        }
    }

    /// <summary>Peak amplitude of a sine whose RMS is the given dBFS.</summary>
    private static float SineAmpFor(double rmsDb) => (float)(Math.Pow(10.0, rmsDb / 20.0) * Math.Sqrt(2));

    private static float[] Pull(BusLeveler lev, int blocks, int frames = 480)
    {
        int count = frames * 2;
        var all = new List<float>();
        var buf = new float[count];
        for (int b = 0; b < blocks; b++)
        {
            int n = lev.Read(buf, 0, count);
            all.AddRange(buf.AsSpan(0, n).ToArray());
        }
        return all.ToArray();
    }

    [Fact]
    public void Disabled_IsBitIdenticalPassthrough()
    {
        // "Off" has to mean the samples are never touched — not multiplied by a unity gain that
        // happens to round back. Anything else and the default state of a live bus is unproven.
        var rng = new Random(1234);
        var values = new float[96000];
        for (int i = 0; i < values.Length; i++) values[i] = (float)(rng.NextDouble() * 2 - 1);
        values[0] = 0f;
        values[1] = -0f;
        values[2] = 1e-40f;   // denormal

        var settings = new BusLevelerSettings();   // default: disabled
        var lev = new BusLeveler(new FakeSource(i => values[i % values.Length]), settings);

        var got = Pull(lev, blocks: 8);
        for (int i = 0; i < got.Length; i++)
            Assert.Equal(BitConverter.SingleToInt32Bits(values[i % values.Length]),
                         BitConverter.SingleToInt32Bits(got[i]));
        Assert.Equal(0f, lev.AppliedGainDb);
    }

    [Fact]
    public void EngagingOnSilence_DoesNotJump()
    {
        // The detector starts at zero energy, which reads far below the idle floor, so the state
        // engages frozen at 0 dB. That is what makes switching it on mid-service click-free.
        var settings = new BusLevelerSettings { Enabled = true };
        var lev = new BusLeveler(new FakeSource(_ => 0f), settings);

        Pull(lev, blocks: 4);
        Assert.Equal(0f, lev.AppliedGainDb);
    }

    [Fact]
    public void Enabled_NeverExceedsTheLimiterCeiling()
    {
        // Full-scale square wave: worst case for a zero-look-ahead limiter.
        var settings = new BusLevelerSettings { Enabled = true, CeilingDb = -1f };
        var lev = new BusLeveler(new FakeSource(i => (i / 64) % 2 == 0 ? 1f : -1f), settings);

        var got = Pull(lev, blocks: 40);
        float ceil = (float)Math.Pow(10.0, -1.0 / 20.0);
        foreach (var v in got)
            Assert.True(Math.Abs(v) <= ceil + 1e-6f, $"sample {v} breached the {ceil} ceiling");
    }

    [Fact]
    public void Enabled_LiftsAQuietSourceButNotPastTheCap()
    {
        // -50 dBFS RMS is 24 dB under the threshold, which asks for ~16 dB of lift at 3:1 — more than
        // the cap allows. The noise budget must hold end to end, not just inside the gain computer.
        var settings = new BusLevelerSettings { Enabled = true, MaxGainDb = 10f, IdleFloorDb = -70f };
        var amp = SineAmpFor(-50);
        var lev = new BusLeveler(new FakeSource(i => amp * MathF.Sin(i * 0.05f)), settings);

        Pull(lev, blocks: 900);   // ~9 s, several release constants

        Assert.True(lev.AppliedGainDb > 1f, $"expected a lift, got {lev.AppliedGainDb} dB");
        Assert.True(lev.AppliedGainDb <= 10f + 1e-3f, $"lift {lev.AppliedGainDb} exceeded the cap");
    }

    [Fact]
    public void DisablingWhileEngaged_ReturnsToBitIdenticalPassthrough()
    {
        var settings = new BusLevelerSettings { Enabled = true, IdleFloorDb = -70f };
        var amp = SineAmpFor(-50);
        var source = new FakeSource(i => amp * MathF.Sin(i * 0.05f));
        var lev = new BusLeveler(source, settings);

        Pull(lev, blocks: 600);        // build up some gain
        Assert.True(lev.AppliedGainDb > 1f, $"expected a lift first, got {lev.AppliedGainDb}");

        settings.Enabled = false;
        Pull(lev, blocks: 60);         // ramp to unity over ~60 ms, then drop out of the graph
        Assert.Equal(0f, lev.AppliedGainDb);

        // Now fully bypassed: what comes out must be exactly what the source put in.
        int before = source.Produced.Count;
        var got = new float[960];
        lev.Read(got, 0, got.Length);

        for (int i = 0; i < got.Length; i++)
            Assert.Equal(BitConverter.SingleToInt32Bits(source.Produced[before + i]),
                         BitConverter.SingleToInt32Bits(got[i]));
    }
}
