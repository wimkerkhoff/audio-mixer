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

    /// <summary>Enough voiced buffers at one level for a median to be published at all.</summary>
    private static CalibrationHistogram Settled(double db, int count = CalibrationHistogram.MinVoicedBuffers)
    {
        var h = new CalibrationHistogram();
        for (int i = 0; i < count; i++) h.Add(Rms(db), voiced: true);
        return h;
    }

    [Fact]
    public void Empty_ReportsNaN_NotAFalseReading()
    {
        var s = new CalibrationHistogram().Snapshot();
        Assert.True(float.IsNaN(s.SpeechDb));
        Assert.True(float.IsNaN(s.FloorDb));
        Assert.Equal(0, s.TotalBuffers);
    }

    /// <summary>
    /// A median of one buffer is arithmetically fine and means nothing. Publishing it let the level
    /// alert fire on a single frame of noise before anyone had spoken, so a median now needs a few
    /// seconds of actual speech behind it — the buffers are still counted while it waits.
    /// </summary>
    [Fact]
    public void ASingleVoicedBuffer_IsNotYetAMeasurement()
    {
        var h = new CalibrationHistogram();
        h.Add(Rms(-24), voiced: true);

        var s = h.Snapshot();
        Assert.True(float.IsNaN(s.SpeechDb));
        Assert.Equal(1, s.VoicedBuffers);
        Assert.Equal(1, s.TotalBuffers);
    }

    [Fact]
    public void AMedianAppearsOnceThereIsEnoughSpeech()
    {
        var h = Settled(-24, CalibrationHistogram.MinVoicedBuffers - 1);
        Assert.True(float.IsNaN(h.Snapshot().SpeechDb));

        h.Add(Rms(-24), voiced: true);
        Assert.Equal(-24f, h.Snapshot().SpeechDb);
    }

    /// <summary>The floor answers a different question and keeps its own, looser rule.</summary>
    [Fact]
    public void TheFloorDoesNotWaitForSpeech()
    {
        var h = new CalibrationHistogram();
        h.Add(Rms(-62), voiced: false);

        var s = h.Snapshot();
        Assert.Equal(-62f, s.FloorDb);
        Assert.True(float.IsNaN(s.SpeechDb));
    }

    [Fact]
    public void VoicedAndQuiet_AreTalliedSeparately()
    {
        var h = Settled(-24);
        for (int i = 0; i < 30; i++) h.Add(Rms(-62), voiced: false);

        var s = h.Snapshot();
        Assert.Equal(-24f, s.SpeechDb);
        Assert.Equal(-62f, s.FloorDb);
        Assert.Equal(CalibrationHistogram.MinVoicedBuffers, s.VoicedBuffers);
        Assert.Equal(CalibrationHistogram.MinVoicedBuffers + 30, s.TotalBuffers);
    }

    [Fact]
    public void Median_IgnoresOutliers_WhichIsWhyItIsNotAMean()
    {
        // A settled level plus one screaming outlier. A mean would move; the median must not, or one
        // door slam sends the operator chasing a gain change that is not needed.
        var h = Settled(-30);
        h.Add(Rms(-3), voiced: true);

        Assert.Equal(-30f, h.Snapshot().SpeechDb);
    }

    [Theory]
    [InlineData(4)]   // even spread — lower median
    [InlineData(5)]   // odd spread
    public void Median_SplitsAcrossBins(int spread)
    {
        var h = new CalibrationHistogram();
        for (int b = 0; b < spread; b++)
            for (int i = 0; i < CalibrationHistogram.MinVoicedBuffers; i++)
                h.Add(Rms(-40 + b), voiced: true);

        // Lower median: the bin where the running count first exceeds half.
        Assert.Equal(-40f + spread / 2, h.Snapshot().SpeechDb);
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
        for (int i = 0; i < CalibrationHistogram.MinVoicedBuffers; i++)
            h.Add(2.0f, voiced: true);     // above full scale (float capture does not saturate)

        var s = h.Snapshot();
        Assert.Equal(CalibrationHistogram.MinDb, s.FloorDb);
        Assert.Equal(0f, s.SpeechDb);
        Assert.Equal(CalibrationHistogram.MinVoicedBuffers + 1, s.TotalBuffers);
    }

    [Fact]
    public void Reset_ClearsBothHalves()
    {
        var h = Settled(-24);
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
        // The -24 dBFS target has to survive the linear->dB->bin round trip exactly, or the target
        // band on every meter would sit off-centre.
        Assert.Equal(-24f, Settled(-24).Snapshot().SpeechDb);
    }

    // --- staleness: noticing the median describes a rig that has since changed -------------------

    /// <summary>
    /// The failure this exists for: an operator raises a transmitter's gain, still sees "quiet"
    /// because half a million old buffers are dragging the median, and concludes it did not work.
    /// A stale number that looks authoritative is worse than no number.
    /// </summary>
    [Fact]
    public void AStepChangeInLevel_MarksTheCumulativeMedianStale()
    {
        var h = Settled(-40, 3000);
        Assert.False(h.Snapshot().IsStale);

        for (int i = 0; i < CalibrationHistogram.RecentWindow; i++) h.Add(Rms(-24), voiced: true);

        var s = h.Snapshot();
        Assert.True(s.IsStale);
        Assert.Equal(-24f, s.RecentSpeechDb);
        Assert.True(s.SpeechDb < -30, "the cumulative median should still be lagging behind");
    }

    [Fact]
    public void ASteadyLevelIsNeverStale()
    {
        var s = Settled(-24, 3000).Snapshot();

        Assert.False(s.IsStale);
        Assert.Equal(s.SpeechDb, s.RecentSpeechDb);
    }

    /// <summary>Normal talker variation must not cry stale, or the reset advice becomes noise.</summary>
    [Fact]
    public void ADriftInsideToleranceIsNotStale()
    {
        var h = Settled(-24, 3000);
        for (int i = 0; i < CalibrationHistogram.RecentWindow; i++)
            h.Add(Rms(-24 + CalibrationHistogram.StaleDriftDb - 1), voiced: true);

        Assert.False(h.Snapshot().IsStale);
    }

    [Fact]
    public void StalenessNeedsBothHalvesToBeRealMeasurements()
    {
        var h = new CalibrationHistogram();
        for (int i = 0; i < 10; i++) h.Add(Rms(-24), voiced: true);

        var s = h.Snapshot();
        Assert.False(s.IsStale);
        Assert.True(float.IsNaN(s.RecentSpeechDb));
    }

    [Fact]
    public void ResetClearsTheRollingWindowToo()
    {
        var h = Settled(-40, 3000);
        for (int i = 0; i < CalibrationHistogram.RecentWindow; i++) h.Add(Rms(-24), voiced: true);
        Assert.True(h.Snapshot().IsStale);

        h.Reset();

        var s = h.Snapshot();
        Assert.False(s.IsStale);
        Assert.True(float.IsNaN(s.RecentSpeechDb));
    }

    /// <summary>Only voiced buffers shape the window — room tone is not a level measurement.</summary>
    [Fact]
    public void QuietBuffersDoNotEnterTheRollingWindow()
    {
        var h = Settled(-24, 3000);
        for (int i = 0; i < CalibrationHistogram.RecentWindow; i++) h.Add(Rms(-70), voiced: false);

        Assert.False(h.Snapshot().IsStale);
    }
}
