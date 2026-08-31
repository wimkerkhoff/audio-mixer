using AudioMixer.Audio;
using Xunit;

namespace AudioMixer.Tests;

/// <summary>
/// The leveler is the only dynamics stage in the app, it sits on the live stream, and its failure
/// modes are the two this rig has already been burned by: lifting the room's noise floor, and
/// gating. Both are properties of the pure gain logic, so both are testable without a device.
/// </summary>
public class BusLevelerTests
{
    private const float Dt = 32f / 48000f;

    private static LevelerSettings Medium => LevelerSettings.For(LevelerStrength.Medium);
    private static float MeanSquareFor(double db) => (float)Math.Pow(10.0, db / 10.0);

    /// <summary>Starts with the detector already settled, so a test measures gain timing, not detector timing.</summary>
    private static LevelerState Settled(double db, float gainDb = 0f, bool idle = false) =>
        new(gainDb, MeanSquareFor(db), idle);

    private static LevelerState Run(LevelerState s, double db, double seconds, in LevelerSettings cfg)
    {
        var k = LevelerCoefficients.For(cfg, Dt);
        float ms = MeanSquareFor(db);
        int steps = (int)(seconds / Dt);
        for (int i = 0; i < steps; i++) s = LevelerCore.Step(s, ms, cfg, k);
        return s;
    }

    // --- gain computer ------------------------------------------------------------------------

    [Fact]
    public void AtThreshold_IsUnity()
    {
        Assert.Equal(0f, LevelerCore.DesiredGainDb(Medium.ThresholdDb, Medium), 3);
    }

    [Fact]
    public void BelowThreshold_LiftsByOneMinusInverseRatio()
    {
        // -46 dBFS is 20 dB under the -26 threshold; at 3:1 that is 20 * (1 - 1/3) = 13.33 dB of lift,
        // clamped by the 10 dB cap on Medium.
        var wide = Medium with { MaxGainDb = 20f };
        Assert.Equal(13.333f, LevelerCore.DesiredGainDb(-46f, wide), 2);
    }

    [Fact]
    public void AboveThreshold_Attenuates()
    {
        Assert.Equal(-13.333f, LevelerCore.DesiredGainDb(-6f, Medium), 2);
    }

    [Fact]
    public void RatioOne_IsAlwaysUnity()
    {
        var flat = Medium with { Ratio = 1f };
        for (float db = -120f; db <= 0f; db += 0.5f)
            Assert.Equal(0f, LevelerCore.DesiredGainDb(db, flat), 4);
    }

    [Fact]
    public void LiftNeverExceedsTheCap_AtAnyLevel()
    {
        // The room floor is acoustic and cannot be filtered back out, so every dB of lift is a dB of
        // HVAC. This cap is the noise budget and nothing may breach it.
        for (float db = -120f; db <= 0f; db += 0.25f)
            Assert.True(LevelerCore.DesiredGainDb(db, Medium) <= Medium.MaxGainDb + 1e-4f,
                $"lift exceeded the cap at {db} dBFS");
    }

    [Fact]
    public void CutIsBoundedByMinGain()
    {
        Assert.Equal(LevelerCore.MinGainDb, LevelerCore.DesiredGainDb(60f, Medium), 4);
    }

    [Fact]
    public void MaxGain_IsHardCapped_NotJustDefaulted()
    {
        var s = new BusLevelerSettings { MaxGainDb = 30f };
        Assert.Equal(BusLeveler.MakeupCeilingDb, s.MaxGainDb);
    }

    [Theory]
    [InlineData(LevelerStrength.Gentle, 2f, 6f)]
    [InlineData(LevelerStrength.Medium, 3f, 10f)]
    [InlineData(LevelerStrength.Strong, 4f, 12f)]
    public void StrengthPresets_MoveRatioAndCapTogether(LevelerStrength strength, float ratio, float maxGain)
    {
        var s = new BusLevelerSettings { Strength = strength };
        Assert.Equal(ratio, s.Ratio);
        Assert.Equal(maxGain, s.MaxGainDb);
    }

    // --- idle hold: the anti-gate properties ---------------------------------------------------

    [Fact]
    public void BelowIdleFloor_FreezesGainExactly()
    {
        var s = Run(Settled(-60, gainDb: 8f), -60, seconds: 3, Medium);
        Assert.True(s.Idle);
        Assert.Equal(8f, s.GainDb);   // bit-exact: the smoother must not run at all while idle
    }

    [Theory]
    [InlineData(8f)]
    [InlineData(0f)]
    [InlineData(-6f)]
    public void IdleNeverAttenuates_FromAnyStartingGain(float startGainDb)
    {
        // The forbidden behaviour. A downward gate is what made the previous microphones unusable —
        // they punched holes in sustained material. There must be no path here that lowers gain on a
        // quiet signal, whatever gain we happen to be sitting at.
        var s = Run(Settled(-60, gainDb: startGainDb), -60, seconds: 10, Medium);
        Assert.Equal(startGainDb, s.GainDb);
    }

    [Fact]
    public void DigitalSilence_ProducesNoNaNOrInfinity()
    {
        // Exactly zero energy would be -inf dB without the epsilon in Step.
        var k = LevelerCoefficients.For(Medium, Dt);
        var s = new LevelerState(5f, 0f, true);
        for (int i = 0; i < 3000; i++) s = LevelerCore.Step(s, 0f, Medium, k);

        Assert.False(float.IsNaN(s.GainDb));
        Assert.False(float.IsInfinity(s.GainDb));
        Assert.Equal(5f, s.GainDb);
    }

    [Fact]
    public void SpeechAfterIdle_ExitsFastEnoughToKeepTheFirstSyllable()
    {
        // The exit test in Step has no dwell, but the detector is smoothed, so leaving idle still
        // takes as long as the envelope needs to climb past the floor + hysteresis. That delay is
        // what would clip the start of a new talker, so pin it: it must be a few milliseconds, far
        // inside one syllable (~100 ms).
        var idle = Run(Settled(-60, gainDb: 8f), -60, seconds: 2, Medium);
        Assert.True(idle.Idle);

        var k = LevelerCoefficients.For(Medium, Dt);
        var s = idle;
        int steps = 0;
        while (s.Idle && steps < 15000) { s = LevelerCore.Step(s, MeanSquareFor(-30), Medium, k); steps++; }

        float exitMs = steps * Dt * 1000f;
        Assert.False(s.Idle);
        Assert.True(exitMs < 20f, $"idle exit took {exitMs:F1} ms — long enough to clip a syllable");
    }

    [Fact]
    public void SpeechAfterIdle_ResumesFromTheHeldGain_NotFromZero()
    {
        // The whole point of freezing rather than releasing: the gain the last talker earned is what
        // greets the next one, so nobody arrives under a bus that has drifted back to unity.
        var idle = Run(Settled(-60, gainDb: 8f), -60, seconds: 2, Medium);
        var after = Run(idle, -30, seconds: 0.05, Medium);

        Assert.False(after.Idle);
        Assert.True(after.GainDb > 7f,
            $"expected to resume near the held 8 dB, got {after.GainDb}");
    }

    [Fact]
    public void IdleHysteresis_DoesNotChatterAtTheBoundary()
    {
        // Alternating either side of the floor must not toggle: entering needs -45, leaving needs -42.
        var k = LevelerCoefficients.For(Medium, Dt);
        var s = Run(Settled(-60, gainDb: 4f), -60, seconds: 1, Medium);
        Assert.True(s.Idle);

        for (int i = 0; i < 3000; i++)
            s = LevelerCore.Step(s, MeanSquareFor(i % 2 == 0 ? -44 : -46), Medium, k);

        Assert.True(s.Idle);
        Assert.Equal(4f, s.GainDb);
    }

    // --- timing -------------------------------------------------------------------------------

    [Fact]
    public void UpwardGain_TakesTheReleaseTime_SoTheFloorCannotPump()
    {
        // The assertion that matters is the second one: at 200 ms the lift must still be small. If
        // upward gain were fast, every pause would audibly swell the room tone.
        var cfg = Medium;
        float target = LevelerCore.DesiredGainDb(-36f, cfg);

        var at200 = Run(Settled(-36), -36, 0.2, cfg);
        var at2s = Run(Settled(-36), -36, 2.0, cfg);

        Assert.True(at200.GainDb < 0.20f * target, $"upward gain too fast: {at200.GainDb} of {target}");
        Assert.InRange(at2s.GainDb, 0.55f * target, 0.72f * target);   // ~63% after one time constant
    }

    [Fact]
    public void DownwardGain_UsesTheFasterAttack()
    {
        var cfg = Medium;
        var down = Run(Settled(-6), -6, 0.1, cfg);
        float target = LevelerCore.DesiredGainDb(-6f, cfg);
        Assert.InRange(down.GainDb, 1.15f * target, 0.5f * target);    // well past 50% at one attack constant
    }

    [Fact]
    public void GainNeverOvershootsItsTarget()
    {
        var cfg = Medium;
        var k = LevelerCoefficients.For(cfg, Dt);
        var s = Settled(-36);
        float target = LevelerCore.DesiredGainDb(-36f, cfg);
        float ms = MeanSquareFor(-36);

        for (int i = 0; i < 15000; i++)
        {
            s = LevelerCore.Step(s, ms, cfg, k);
            Assert.True(s.GainDb <= target + 1e-3f, $"overshot at step {i}: {s.GainDb} > {target}");
        }
    }

    [Fact]
    public void TimeConstants_AreIndependentOfSubBlockSize()
    {
        // Guards the classic bug where a device period change silently retunes every time constant.
        var cfg = Medium;
        float ms = MeanSquareFor(-36);

        static float RunAt(float dt, float ms, in LevelerSettings cfg)
        {
            var k = LevelerCoefficients.For(cfg, dt);
            var s = new LevelerState(0f, ms, false);
            int steps = (int)(1.0f / dt);
            for (int i = 0; i < steps; i++) s = LevelerCore.Step(s, ms, cfg, k);
            return s.GainDb;
        }

        Assert.Equal(RunAt(32f / 48000f, ms, cfg), RunAt(128f / 48000f, ms, cfg), 1);
    }
}
