using AudioMixer.Audio;
using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// The radio-dropout counter. A Wireless PRO receiver that loses its transmitter writes ~0.5 s of
/// exact zero; a mic in a room never does, because it always hears the floor.
/// </summary>
public class DropoutTests
{
    private const int Buf = 960;                 // 10 ms of 48 kHz stereo
    private const int Min = 2880;                // 30 ms

    private static float[] Sound(int n = Buf) => Enumerable.Repeat(1e-3f, n).ToArray();
    private static float[] Zero(int n = Buf) => new float[n];

    private static int Feed(DropoutCounter c, params float[][] buffers) =>
        buffers.Count(b => c.Observe(b));

    [Fact]
    public void AHalfSecondGapCountsOnce()
    {
        var c = new DropoutCounter(Min);
        var bufs = new List<float[]> { Sound() };
        bufs.AddRange(Enumerable.Range(0, 50).Select(_ => Zero()));
        bufs.Add(Sound());
        Assert.Equal(1, Feed(c, bufs.ToArray()));
    }

    [Fact]
    public void AGapStartingMidBufferIsMeasuredFromItsFirstZero()
    {
        var c = new DropoutCounter(Min);
        var half = Sound(); Array.Clear(half, Buf / 2, Buf / 2);   // 5 ms of zero at the end
        // 5 + 10 + 10 = 25 ms: not yet; the next buffer takes it past 30 ms.
        Assert.Equal(0, Feed(c, half, Zero(), Zero()));
        Assert.Equal(1, Feed(c, Zero()));
    }

    [Fact]
    public void ShortZeroRunsAreNotGaps()
    {
        var c = new DropoutCounter(Min);
        Assert.Equal(0, Feed(c, Zero(), Zero(), Sound(), Zero(), Zero(), Sound()));
    }

    [Fact]
    public void ALinkBreakingUpCountsEveryBreak()
    {
        var c = new DropoutCounter(Min);
        int gaps = 0;
        for (int i = 0; i < 5; i++)
        {
            gaps += Feed(c, Sound(), Sound());
            gaps += Feed(c, Enumerable.Range(0, 5).Select(_ => Zero()).ToArray());
        }
        Assert.Equal(5, gaps);
    }

    [Fact]
    public void TheWindowCountsOnlyTheLastMinute()
    {
        var w = new DropoutWindow();
        Assert.Equal(0, w.Record(0, 100));
        Assert.Equal(10, w.Record(30_000, 110));
        Assert.Equal(15, w.Record(60_000, 115));
        Assert.Equal(5, w.Record(90_000, 115));   // the 0 ms and 30 s samples have aged out
    }

    [Fact]
    public void ACounterThatResetsStartsTheWindowOver()
    {
        var w = new DropoutWindow();
        w.Record(0, 500);
        Assert.Equal(0, w.Record(1_000, 3));
    }
}
