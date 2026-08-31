using AudioMixer.Audio;
using Xunit;

namespace AudioMixer.Tests;

/// <summary>
/// The calibration readout is the one number transmitter gain gets set against, and a wrong median
/// would send the operator the wrong way with no way to notice. Pure logic, no device.
/// </summary>
public class CalibrationHistogramTests
{
    private static float Rms(double db) => (float)Math.Pow(10.0, db / 20.0);

    [Fact]
    public void Empty_ReportsNaN_NotAFalseReading()
    {
        var s = new CalibrationHistogram().Snapshot();
        Assert.True(float.IsNaN(s.SpeechDb));
        Assert.True(float.IsNaN(s.FloorDb));
        Assert.Equal(0, s.TotalBuffers);
    }

    [Fact]
    public void SingleVoicedBuffer_ReportsThatLevel_AndLeavesFloorUnknown()
    {
        var h = new CalibrationHistogram();
        h.Add(Rms(-24), voiced: true);

        var s = h.Snapshot();
        Assert.Equal(-24f, s.SpeechDb);
        Assert.True(float.IsNaN(s.FloorDb));
        Assert.Equal(1, s.VoicedBuffers);
        Assert.Equal(1, s.TotalBuffers);
    }

    [Fact]
    public void VoicedAndQuiet_AreTalliedSeparately()
    {
        var h = new CalibrationHistogram();
        for (int i = 0; i < 10; i++) h.Add(Rms(-24), voiced: true);
        for (int i = 0; i < 30; i++) h.Add(Rms(-62), voiced: false);

        var s = h.Snapshot();
        Assert.Equal(-24f, s.SpeechDb);
        Assert.Equal(-62f, s.FloorDb);
        Assert.Equal(10, s.VoicedBuffers);
        Assert.Equal(40, s.TotalBuffers);
    }

    [Fact]
    public void Median_IgnoresOutliers_WhichIsWhyItIsNotAMean()
    {
        // Nine buffers at -30 and one screaming outlier. A mean would read ~-22; the median must not
        // move, or one door slam would send the operator chasing a gain change that isn't needed.
        var h = new CalibrationHistogram();
        for (int i = 0; i < 9; i++) h.Add(Rms(-30), voiced: true);
        h.Add(Rms(-3), voiced: true);

        Assert.Equal(-30f, h.Snapshot().SpeechDb);
    }

    [Theory]
    [InlineData(4)]   // even count — lower median
    [InlineData(5)]   // odd count
    public void Median_SplitsAcrossBins(int count)
    {
        var h = new CalibrationHistogram();
        for (int i = 0; i < count; i++) h.Add(Rms(-40 + i), voiced: true);

        // Lower median: the bin where the running count first exceeds half.
        Assert.Equal(-40f + count / 2, h.Snapshot().SpeechDb);
    }

    [Fact]
    public void DigitalSilence_LandsInTheBottomBin_WithoutNaNOrInfinity()
    {
        // rms == 0 would be -inf dB. A gating device or a dead capture must read -90, not crash the
        // readout: a floor pinned at -90 is itself the diagnostic.
        var h = new CalibrationHistogram();
        h.Add(0f, voiced: false);

        var s = h.Snapshot();
        Assert.Equal(CalibrationHistogram.MinDb, s.FloorDb);
        Assert.False(float.IsNaN(s.FloorDb));
    }

    [Fact]
    public void LevelsAreClampedIntoRange_NotWrappedOrDropped()
    {
        var h = new CalibrationHistogram();
        h.Add(Rms(-140), voiced: false);   // below the bottom bin
        h.Add(2.0f, voiced: true);         // above full scale (float capture does not saturate)

        var s = h.Snapshot();
        Assert.Equal(CalibrationHistogram.MinDb, s.FloorDb);
        Assert.Equal(0f, s.SpeechDb);
        Assert.Equal(2, s.TotalBuffers);
    }

    [Fact]
    public void Reset_ClearsBothHalves()
    {
        var h = new CalibrationHistogram();
        h.Add(Rms(-24), voiced: true);
        h.Add(Rms(-60), voiced: false);
        h.Reset();

        var s = h.Snapshot();
        Assert.True(float.IsNaN(s.SpeechDb));
        Assert.True(float.IsNaN(s.FloorDb));
        Assert.Equal(0, s.TotalBuffers);
    }

    [Fact]
    public void TargetLevel_RoundTripsToTheTargetBin()
    {
        // The -24 dBFS target has to survive the linear->dB->bin round trip exactly, or the
        // Diagnostics window's green "on target" band would sit off-centre.
        var h = new CalibrationHistogram();
        h.Add(Rms(-24), voiced: true);
        Assert.Equal(-24f, h.Snapshot().SpeechDb);
    }
}
