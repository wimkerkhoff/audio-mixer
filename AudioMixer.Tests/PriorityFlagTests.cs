using System.Diagnostics;

namespace AudioMixer.Tests;

public class PriorityFlagTests
{
    [Theory]
    [InlineData("High", ProcessPriorityClass.High)]
    [InlineData("normal", ProcessPriorityClass.Normal)]
    [InlineData("BelowNormal", ProcessPriorityClass.BelowNormal)]
    [InlineData("idle", ProcessPriorityClass.Idle)]
    public void ANamedClassIsAccepted(string arg, ProcessPriorityClass expected) =>
        Assert.Equal(expected, App.ParsePriority(arg));

    /// <summary>RealTime can starve the OS's own input threads; a typo must not become a guess.</summary>
    [Theory]
    [InlineData("RealTime")]
    [InlineData("hihg")]
    [InlineData("7")]
    [InlineData("")]
    public void RealTimeAndNonsenseAreRefused(string arg) =>
        Assert.Null(App.ParsePriority(arg));
}
