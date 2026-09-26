using AudioMixer.Audio;

namespace AudioMixer.Tests;

/// <summary>
/// The selector, which had NO unit tests at all until 2026-09-20 — including through the change that
/// removed Share, the correlation selector, flux-CV selection and the strength slider from it. That
/// was ~400 lines out of the heart of the app verified by a clean build and a smoke run.
///
/// Everything it decides comes from per-channel level, routing and the priority flag, none of which
/// needs audio hardware; <see cref="InputChannel.InjectLevelsForTest"/> is the one seam.
///
/// Levels are linear RMS. Landmarks: SilenceFloorRms 0.0018 (~−55 dBFS), PriorityBreakInRms 0.0032
/// (~−50), PriorityActiveRms 0.01 (~−40).
/// </summary>
public class AutoMixerTests
{
    private const int Outputs = 2;

    private static InputChannel[] Rig(int count)
    {
        var rig = new InputChannel[count];
        for (int i = 0; i < count; i++)
        {
            rig[i] = new InputChannel(Outputs);
            for (int o = 0; o < Outputs; o++) rig[i].SetRoute(o, true);
        }
        return rig;
    }

    /// <summary>The envelope has an 8 ms attack, so a level needs a few ticks to be believed.</summary>
    private static void Run(AutoMixer mix, InputChannel[] rig, int ticks = 60)
    {
        for (int t = 0; t < ticks; t++) mix.Tick(rig);
    }

    private static AutoMixer Gated(int outputs = Outputs, int channels = 3)
    {
        var mix = new AutoMixer(outputs, channels);
        for (int o = 0; o < outputs; o++) mix.SetMode(o, AutoMixMode.Gate);
        return mix;
    }

    // --- Gate ------------------------------------------------------------------------------------

    [Fact]
    public void GateSendsOnlyTheLoudestMic_AndMutesTheRest()
    {
        var rig = Rig(3);
        rig[0].InjectLevelsForTest(0.20f);
        rig[1].InjectLevelsForTest(0.02f);
        rig[2].InjectLevelsForTest(0.01f);

        var mix = Gated();
        Run(mix, rig);

        Assert.Equal(0, mix.ActiveInput(0));
        Assert.Equal(1f, rig[0].GetAutoMixGain(0));
        Assert.Equal(0f, rig[1].GetAutoMixGain(0));
        Assert.Equal(0f, rig[2].GetAutoMixGain(0));
    }

    [Fact]
    public void OffPassesEveryRoutedMicAtUnity()
    {
        var rig = Rig(3);
        for (int i = 0; i < 3; i++) rig[i].InjectLevelsForTest(0.05f * (i + 1));

        var mix = new AutoMixer(Outputs, 3);   // Off is the default
        Run(mix, rig);

        Assert.Equal(-1, mix.ActiveInput(0));
        Assert.All(rig, c => Assert.Equal(1f, c.GetAutoMixGain(0)));
    }

    /// <summary>
    /// The whole Singing control rests on this. The 2026-07-05 failure put worship on the stream as
    /// pastor-only, because an armed priority lapel ducked every room mic to zero; the Singing scene
    /// prevented it by clearing priority and a health alert caught it when it did not. Both are gone
    /// (2026-09-23): the toggle now only sets the buses Off and leaves the lapel armed, so the ONLY
    /// thing keeping the congregation on air is Off returning before the priority logic runs.
    /// </summary>
    [Fact]
    public void OffIgnoresAnActivePriorityMic_SoSingingCannotDuckTheRoom()
    {
        var rig = Rig(3);
        rig[0].IsPriority = true;
        rig[0].InjectLevelsForTest(0.20f);      // far over PriorityActiveRms: it would duck under Gate
        rig[1].InjectLevelsForTest(0.02f);
        rig[2].InjectLevelsForTest(0.02f);

        var mix = new AutoMixer(Outputs, 3);   // Off is the default
        Run(mix, rig);

        Assert.All(rig, c => Assert.Equal(1f, c.GetAutoMixGain(0)));
        Assert.All(rig, c => Assert.Equal(1f, c.GetAutoMixGain(1)));
    }

    /// <summary>The other half: leaving Singing resumes the duck without re-arming anything.</summary>
    [Fact]
    public void BackToGate_TheStillArmedPriorityMicDucksAgain()
    {
        var rig = Rig(3);
        rig[0].IsPriority = true;
        rig[0].InjectLevelsForTest(0.20f);
        rig[1].InjectLevelsForTest(0.02f);
        rig[2].InjectLevelsForTest(0.02f);

        var mix = new AutoMixer(Outputs, 3);
        Run(mix, rig);
        for (int o = 0; o < Outputs; o++) mix.SetMode(o, AutoMixMode.Gate);
        Run(mix, rig);

        Assert.Equal(1f, rig[0].GetAutoMixGain(0));
        Assert.Equal(0f, rig[1].GetAutoMixGain(0));
        Assert.Equal(0f, rig[2].GetAutoMixGain(0));
    }

    [Fact]
    public void AnUnroutedMicIsNeverSelected()
    {
        var rig = Rig(2);
        rig[0].SetRoute(0, false);
        rig[0].InjectLevelsForTest(0.5f);    // by far the loudest
        rig[1].InjectLevelsForTest(0.05f);

        var mix = Gated(channels: 2);
        Run(mix, rig);

        Assert.Equal(1, mix.ActiveInput(0));
    }

    /// <summary>Each bus decides for itself — a mic routed only to B cannot win A.</summary>
    [Fact]
    public void EachBusSelectsIndependently()
    {
        var rig = Rig(2);
        rig[0].SetRoute(1, false);           // A only
        rig[1].SetRoute(0, false);           // B only
        rig[0].InjectLevelsForTest(0.05f);
        rig[1].InjectLevelsForTest(0.05f);

        var mix = Gated(channels: 2);
        Run(mix, rig);

        Assert.Equal(0, mix.ActiveInput(0));
        Assert.Equal(1, mix.ActiveInput(1));
    }

    // --- hold and hysteresis: the actual fix for "far mic wins" ------------------------------------

    /// <summary>
    /// Finding 1: the original bug was temporal, not metric — the selector re-picked the loudest mic
    /// every 10 ms, so a distant mic's momentary rise stole the bus. A challenger must clear ~+3 dB.
    /// </summary>
    [Fact]
    public void AChallengerInsideTheMarginNeverTakesTheBus()
    {
        var rig = Rig(2);
        rig[0].InjectLevelsForTest(0.10f);
        rig[1].InjectLevelsForTest(0.01f);
        var mix = Gated(channels: 2);
        Run(mix, rig);
        Assert.Equal(0, mix.ActiveInput(0));

        // 1.2x is under the 1.413 (+3 dB) margin.
        rig[1].InjectLevelsForTest(0.12f);
        Run(mix, rig, 300);

        Assert.Equal(0, mix.ActiveInput(0));
    }

    [Fact]
    public void AChallengerWellPastTheMarginDoesTakeTheBus()
    {
        var rig = Rig(2);
        rig[0].InjectLevelsForTest(0.10f);
        rig[1].InjectLevelsForTest(0.01f);
        var mix = Gated(channels: 2);
        Run(mix, rig);

        rig[1].InjectLevelsForTest(0.50f);
        Run(mix, rig, 300);

        Assert.Equal(1, mix.ActiveInput(0));
        Assert.Equal(1f, rig[1].GetAutoMixGain(0));
        Assert.Equal(0f, rig[0].GetAutoMixGain(0));
    }

    /// <summary>
    /// A single 10 ms tick 15 dB above the leader used to take the bus (the margin cleared instantly,
    /// and under Gate a cough muted the real talker for 200 ms). A challenger must now keep its margin
    /// for a while, and the spike's envelope falls back through the bands before it has.
    /// </summary>
    [Fact]
    public void ASingleTickSpikeDoesNotTakeTheBus()
    {
        var rig = Rig(2);
        rig[0].InjectLevelsForTest(0.10f);
        rig[1].InjectLevelsForTest(0.01f);
        var mix = Gated(channels: 2);
        Run(mix, rig);
        Assert.Equal(0, mix.ActiveInput(0));

        rig[1].InjectLevelsForTest(0.60f);
        mix.Tick(rig);                       // one 10 ms tick
        rig[1].InjectLevelsForTest(0.01f);
        for (int t = 0; t < 100; t++)
        {
            mix.Tick(rig);
            Assert.Equal(0, mix.ActiveInput(0));
        }
    }

    /// <summary>
    /// A talker between two mics reads within a few dB on both, and either serves; it was the flipping
    /// between them that was heard (median tenure 0.4 s, 2026-09-26). A +4 dB lead must last ~500 ms.
    /// </summary>
    [Fact]
    public void ACloseChallengerMustKeepItsLeadForHalfASecond()
    {
        var rig = Rig(2);
        rig[0].InjectLevelsForTest(0.10f);
        rig[1].InjectLevelsForTest(0.01f);
        var mix = Gated(channels: 2);
        Run(mix, rig);

        rig[1].InjectLevelsForTest(0.16f);   // +4 dB
        int ticks = TickUntilWinner(mix, rig, 1, max: 200);

        Assert.InRange(ticks, 45, 80);
    }

    [Fact]
    public void AClearWinnerStillTakesOverInAboutATenthOfASecond()
    {
        var rig = Rig(2);
        rig[0].InjectLevelsForTest(0.10f);
        rig[1].InjectLevelsForTest(0.01f);
        var mix = Gated(channels: 2);
        Run(mix, rig);

        rig[1].InjectLevelsForTest(0.50f);   // +14 dB
        int ticks = TickUntilWinner(mix, rig, 1, max: 200);

        Assert.InRange(ticks, 8, 20);
    }

    /// <summary>A lead that lapses resets the wait: the challenger has to earn it again in one go.</summary>
    [Fact]
    public void AnInterruptedLeadStartsTheWaitAgain()
    {
        var rig = Rig(2);
        rig[0].InjectLevelsForTest(0.10f);
        rig[1].InjectLevelsForTest(0.01f);
        var mix = Gated(channels: 2);
        Run(mix, rig);

        for (int burst = 0; burst < 6; burst++)
        {
            rig[1].InjectLevelsForTest(0.16f);   // +4 dB for ~300 ms...
            Run(mix, rig, 30);
            rig[1].InjectLevelsForTest(0.05f);   // ...then back under the margin
            Run(mix, rig, 40);
        }

        Assert.Equal(0, mix.ActiveInput(0));
    }

    /// <summary>
    /// The hold's real guarantee: for ~200 ms AFTER a hand-off the bus cannot move again, however loud
    /// the challenger. `_winnerHold` is set to HandoffHoldTicks on every change and gates the next one,
    /// so hand-offs are rate-limited to ~5/s no matter what the levels do. Note the hold must be
    /// measured from the hand-off, not from an arbitrary number of ticks later — it expires.
    /// </summary>
    [Fact]
    public void ForTwoHundredMillisecondsAfterAHandoffTheBusCannotMoveAgain()
    {
        var rig = Rig(2);
        rig[0].InjectLevelsForTest(0.10f);
        rig[1].InjectLevelsForTest(0.01f);
        var mix = Gated(channels: 2);
        Run(mix, rig);
        Assert.Equal(0, mix.ActiveInput(0));

        rig[1].InjectLevelsForTest(0.60f);
        int ticks = TickUntilWinner(mix, rig, 1);
        Assert.True(ticks > 0, "the hand-off never happened");

        rig[0].InjectLevelsForTest(3.0f);    // mic 0 screams, inside the hold
        for (int t = 0; t < 15; t++)
        {
            mix.Tick(rig);
            Assert.Equal(1, mix.ActiveInput(0));
        }
    }

    /// <summary>Ticks one at a time until the bus reports <paramref name="want"/>; returns the tick it
    /// happened on, or 0 if it never did. The caller is then positioned exactly at the hand-off.</summary>
    private static int TickUntilWinner(AutoMixer mix, InputChannel[] rig, int want, int max = 60)
    {
        for (int t = 1; t <= max; t++)
        {
            mix.Tick(rig);
            if (mix.ActiveInput(0) == want) return t;
        }
        return 0;
    }

    // --- silence ------------------------------------------------------------------------------------

    /// <summary>
    /// A silent room opens everything rather than gating to one mic — and reports no winner. That is
    /// one of the three distinct causes of winner = -1, and the gains are what tell them apart: a
    /// silent room writes 1.0 where a priority duck writes 0.
    /// </summary>
    [Fact]
    public void ASilentRoomOpensEveryMicAndSelectsNobody()
    {
        var rig = Rig(3);
        foreach (var c in rig) c.InjectLevelsForTest(0.0005f);   // under SilenceFloorRms

        var mix = Gated();
        Run(mix, rig);

        Assert.Equal(-1, mix.ActiveInput(0));
        Assert.All(rig, c => Assert.Equal(1f, c.GetAutoMixGain(0)));
    }

    // --- priority ------------------------------------------------------------------------------------

    [Fact]
    public void AnActivePriorityMicDucksTheRoomToSilence()
    {
        var rig = Rig(3);
        rig[0].IsPriority = true;
        rig[0].InjectLevelsForTest(0.05f);      // well over PriorityActiveRms
        rig[1].InjectLevelsForTest(0.02f);
        rig[2].InjectLevelsForTest(0.02f);

        var mix = Gated();
        Run(mix, rig);

        Assert.Equal(1f, rig[0].GetAutoMixGain(0));
        Assert.Equal(0f, rig[1].GetAutoMixGain(0));
        Assert.Equal(0f, rig[2].GetAutoMixGain(0));
        Assert.Equal(-1, mix.ActiveInput(0) == 0 ? -1 : 0);   // the priority mic is reported, not a winner
    }

    [Fact]
    public void APriorityMicIsNeverGatedEvenWhenQuiet()
    {
        var rig = Rig(2);
        rig[0].IsPriority = true;
        rig[0].InjectLevelsForTest(0.0005f);    // silent lapel
        rig[1].InjectLevelsForTest(0.20f);      // loud room mic

        var mix = Gated(channels: 2);
        Run(mix, rig);

        Assert.Equal(1f, rig[0].GetAutoMixGain(0));
        Assert.Equal(1, mix.ActiveInput(0));
    }

    /// <summary>
    /// The 2026-08-30 fix: the duck used to be recomputed bare each tick, so an ordinary sentence gap
    /// released it and handed the bus to a room mic — measured at 13 hand-offs in 40 s.
    ///
    /// Reaching the hangover at all takes care. The envelope's release is 250 ms, so after the lapel
    /// stops the duck stays up on the envelope ALONE for ~40 ticks before `priorityActive` goes false;
    /// a test that only covers those ticks passes without the hangover code ever running. (This test
    /// did exactly that until coverage showed the branch at `_priorityHold[o]--` unexecuted.) So tick
    /// past the release first, and only then assert.
    /// </summary>
    [Fact]
    public void TheDuckIsHeldForOverASecondAfterThePresentersEnvelopeHasDecayed()
    {
        var rig = Rig(2);
        rig[0].IsPriority = true;
        rig[0].InjectLevelsForTest(0.05f);
        rig[1].InjectLevelsForTest(0.002f);   // quiet room: under PriorityBreakInRms, over the silence floor
        var mix = Gated(channels: 2);
        Run(mix, rig);
        Assert.Equal(0f, rig[1].GetAutoMixGain(0));

        rig[0].InjectLevelsForTest(0f);       // the presenter pauses
        Run(mix, rig, 80);                    // 800 ms — past the ~400 ms envelope release

        Assert.Equal(0f, rig[1].GetAutoMixGain(0));
    }

    /// <summary>...and it does eventually let go, or an unattended open lapel would duck the room off
    /// the stream for the rest of the service.</summary>
    [Fact]
    public void OnceTheHangoverExpiresTheRoomComesBack()
    {
        var rig = Rig(2);
        rig[0].IsPriority = true;
        rig[0].InjectLevelsForTest(0.05f);
        rig[1].InjectLevelsForTest(0.002f);
        var mix = Gated(channels: 2);
        Run(mix, rig);

        rig[0].InjectLevelsForTest(0f);
        Run(mix, rig, 80 + 130);               // release, then past the full 120-tick hold

        Assert.Equal(1f, rig[1].GetAutoMixGain(0));
        Assert.Equal(1, mix.ActiveInput(0));
    }

    /// <summary>
    /// The hold must break for a real interjection, or at hard-mute depth it would swallow one
    /// entirely. A room mic over PriorityBreakInRms ends the duck immediately.
    /// </summary>
    [Fact]
    public void ALoudInterjectionBreaksTheHeldDuck()
    {
        var rig = Rig(2);
        rig[0].IsPriority = true;
        rig[0].InjectLevelsForTest(0.05f);
        rig[1].InjectLevelsForTest(0.002f);
        var mix = Gated(channels: 2);
        Run(mix, rig);

        rig[0].InjectLevelsForTest(0f);
        rig[1].InjectLevelsForTest(0.20f);      // someone speaks up
        Run(mix, rig, 200);

        Assert.Equal(1, mix.ActiveInput(0));
        Assert.Equal(1f, rig[1].GetAutoMixGain(0));
    }

    /// <summary>
    /// 2026-09-26: a room mic hearing the presenter at -35 (lapel -30) stayed over -50 in his pauses
    /// while the lapel dipped under -40, so the absolute break-in handed the bus lapel <-> room ~48
    /// times a minute. A room mic no louder than the lapel is hearing the lapel's wearer.
    /// </summary>
    [Fact]
    public void ThePresentersOwnVoiceInARoomMic_DoesNotBreakTheHeldDuck()
    {
        var rig = Rig(2);
        rig[0].IsPriority = true;
        rig[0].InjectLevelsForTest(0.03f);      // presenter talking, -30
        rig[1].InjectLevelsForTest(0.018f);     // his voice in the room mic, -35
        var mix = Gated(channels: 2);
        Run(mix, rig);

        rig[0].InjectLevelsForTest(0.008f);     // his pause: lapel under -40, not "active"
        rig[1].InjectLevelsForTest(0.006f);     // residual still over -50, but under the lapel
        for (int t = 0; t < 100; t++)
        {
            mix.Tick(rig);
            Assert.Equal(0f, rig[1].GetAutoMixGain(0));
        }
    }

    // --- robustness ------------------------------------------------------------------------------------

    [Fact]
    public void TickWithNoChannelsDoesNotThrow()
    {
        var mix = Gated(channels: 4);
        mix.Tick(Array.Empty<InputChannel>());
        Assert.Equal(-1, mix.ActiveInput(0));
    }

    [Fact]
    public void MoreChannelsThanTheMixerWasSizedForAreIgnoredRatherThanThrowing()
    {
        var rig = Rig(5);
        foreach (var c in rig) c.InjectLevelsForTest(0.05f);

        var mix = new AutoMixer(Outputs, 2);
        mix.SetMode(0, AutoMixMode.Gate);
        Run(mix, rig);

        Assert.True(mix.ActiveInput(0) < 2);
    }

    [Fact]
    public void AnOutOfRangeOutputReportsNoWinnerRatherThanThrowing()
    {
        var mix = Gated();
        Assert.Equal(-1, mix.ActiveInput(9));
        Assert.Equal(-1, mix.ActiveInput(-1));
        mix.SetMode(9, AutoMixMode.Gate);      // must not throw
    }
}
