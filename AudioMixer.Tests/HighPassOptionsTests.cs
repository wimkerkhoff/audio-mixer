using AudioMixer.ViewModels;

namespace AudioMixer.Tests;

/// <summary>
/// The low-cut picker's value/index mapping. It replaced a 0-200 Hz snap-to-tick slider that packed
/// 21 positions into a ~115 px strip column, where pixel rounding during a drag made some cutoffs
/// unreachable — an operator reported being able to select 70 and 90 Hz but not 80.
/// </summary>
public class HighPassOptionsTests
{
    [Fact]
    public void OffIsFirstAndEveryOptionIsAscending()
    {
        var o = ChannelViewModel.HighPassOptions;
        Assert.Equal(0, o[0]);
        Assert.Equal(o.OrderBy(x => x).ToArray(), o);
        Assert.Equal(o.Distinct().Count(), o.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(150)]
    public void EveryOfferedCutoffRoundTrips(int hz)
    {
        int i = ChannelViewModel.HighPassIndexOf(hz);
        Assert.InRange(i, 0, ChannelViewModel.HighPassOptions.Length - 1);
        Assert.Equal(hz, ChannelViewModel.HighPassOptions[i]);
    }

    /// <summary>The cutoffs finding 5 measured must all be selectable — that is why the list exists.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(150)]
    public void TheMeasuredCutoffsAreAllOffered(int hz) =>
        Assert.Contains(hz, ChannelViewModel.HighPassOptions);

    /// <summary>CLAUDE.md: run the low-cut at 80-100 Hz for rumble. Both must be one click away.</summary>
    [Fact]
    public void TheRecommendedBandIsSelectable()
    {
        Assert.True(ChannelViewModel.HighPassIndexOf(80) >= 0);
        Assert.True(ChannelViewModel.HighPassIndexOf(100) >= 0);
    }

    /// <summary>
    /// A hand-edited preset value is reported as "not in the list" rather than snapped to a
    /// neighbour: silently moving someone's cutoff would change the audio with no visible cause.
    /// </summary>
    [Theory]
    [InlineData(75)]
    [InlineData(200)]
    public void AnUnlistedValueIsNotSilentlySnapped(int hz) =>
        Assert.Equal(-1, ChannelViewModel.HighPassIndexOf(hz));
}
