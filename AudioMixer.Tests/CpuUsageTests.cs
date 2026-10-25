using AudioMixer.Services;
using Xunit;

namespace AudioMixer.Tests;

public class CpuUsageTests
{
    [Fact]
    public void BusyShareCountsKernelTimeAsIncludingIdle()
    {
        // 1000 ticks of kernel (of which 600 idle) + 1000 of user: 1400 busy of 2000.
        var a = new CpuUsage.Times(Idle: 0, Kernel: 0, User: 0);
        var b = new CpuUsage.Times(Idle: 600, Kernel: 1000, User: 1000);
        Assert.Equal(70.0, CpuUsage.Percent(a, b)!.Value, 6);
    }

    [Fact]
    public void NoElapsedTimeGivesNoReading() =>
        Assert.Null(CpuUsage.Percent(new(5, 10, 10), new(5, 10, 10)));

    [Theory]
    [InlineData(0, "ok")]
    [InlineData(69.9, "ok")]
    [InlineData(70, "warn")]
    [InlineData(89.9, "warn")]
    [InlineData(90, "bad")]
    [InlineData(100, "bad")]
    public void TheOperatorsBands(double percent, string band) =>
        Assert.Equal(band, CpuUsage.Band(percent));
}
