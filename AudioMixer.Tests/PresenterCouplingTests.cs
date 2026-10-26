using AudioMixer.Audio;
using Xunit;

namespace AudioMixer.Tests;

public class PresenterCouplingTests
{
    [Fact]
    public void NoFigureUntilEnoughOfThePresenterHasBeenHeard()
    {
        var c = new PresenterCoupling(2);
        for (int k = 0; k < PresenterCoupling.MinSamples - 1; k++) c.Add(1, -5);
        Assert.Null(c.Median(1));
        c.Add(1, -5);
        Assert.Equal(-5, c.Median(1));
    }

    [Fact]
    public void TheMedianIgnoresAMinorityOfOtherTalkers()
    {
        var c = new PresenterCoupling(1);
        for (int k = 0; k < 2000; k++) c.Add(0, k % 5 == 0 ? +18 : -6);   // a questioner 20% of ticks
        Assert.Equal(-6, c.Median(0));
    }

    [Fact]
    public void HalvingKeepsTheFigureRecentSoAMovedMicIsRelearned()
    {
        var c = new PresenterCoupling(1);
        for (int k = 0; k < PresenterCoupling.HalveAt - 1; k++) c.Add(0, -10);
        for (int k = 0; k < PresenterCoupling.HalveAt; k++) c.Add(0, 0);   // moved beside him
        Assert.Equal(0, c.Median(0));
    }

    [Fact]
    public void ResetForgetsOneMicOnly()
    {
        var c = new PresenterCoupling(2);
        for (int k = 0; k < 1500; k++) { c.Add(0, -4); c.Add(1, -9); }
        c.Reset(0);
        Assert.Null(c.Median(0));
        Assert.Equal(-9, c.Median(1));
    }
}
