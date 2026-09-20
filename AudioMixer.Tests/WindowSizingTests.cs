using AudioMixer.Audio;

namespace AudioMixer.Tests;

/// <summary>
/// Input strips live in a UniformGrid Rows="1", which divides its column equally and IGNORES each
/// child's MinWidth — too narrow and the right-most controls clip silently, A/B route toggles first,
/// with no error and nothing in the log.
///
/// The window became resizable and vertically scrollable on 2026-09-20, so this is now the STARTING
/// width rather than a cage, and the vertical half of that failure class is gone entirely. The width
/// still has to be right on first open, because an operator who has never resized it sees only this.
///
/// Mirrors MainViewModel.WindowWidth. A view model cannot be constructed here (it builds an
/// AudioEngine and enumerates devices), so the arithmetic is pinned directly; the constants must be
/// changed together.
/// </summary>
public class WindowSizingTests
{
    private const double StripWidth = 100;
    private const double NonStripWidth = 260;
    private const double FloorWidth = 560;

    private const double Chrome = 16;       // window border, measured
    private const double OutputColumn = 230;
    private const double StripOverhead = 10; // Strip margin 2x2 + padding 3x2
    private const double StripMinWidth = 86; // the Border's MinWidth in MainWindow.xaml

    private static double Width(int inputs) => Math.Max(FloorWidth, inputs * StripWidth + NonStripWidth);

    private static double ContentPerStrip(int inputs) =>
        (Width(inputs) - Chrome - OutputColumn) / inputs - StripOverhead;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void EveryStripClearsItsMinWidth(int inputs) =>
        Assert.True(ContentPerStrip(inputs) >= StripMinWidth,
            $"{inputs} inputs leaves {ContentPerStrip(inputs):F1} px per strip, under the {StripMinWidth} px minimum.");

    /// <summary>The supported range really is 1..10 — the engine's limits, not a smaller UI cap.</summary>
    [Fact]
    public void TheSupportedRangeIsOneToTen()
    {
        Assert.Equal(1, AudioEngine.MinInputCount);
        Assert.Equal(10, AudioEngine.MaxInputCount);
    }

    /// <summary>A 10-input window must still fit a 1920-wide screen, the rig's display.</summary>
    [Fact]
    public void TheWidestWindowFitsA1920Screen() => Assert.True(Width(AudioEngine.MaxInputCount) <= 1920);

    [Fact]
    public void WidthGrowsWithInputCountOnceOffTheFloor()
    {
        for (int n = 4; n < AudioEngine.MaxInputCount; n++) Assert.True(Width(n + 1) > Width(n));
    }
}
