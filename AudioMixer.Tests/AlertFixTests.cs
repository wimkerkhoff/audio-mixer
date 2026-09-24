using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// Every alert's suggested fix was a label until 2026-09-21. For a volunteer running the service alone
/// that is the same as no advice — they can read "Clear priority" without knowing where that control
/// lives — and an alert nobody can act on teaches people to skim the window that will one day matter.
///
/// The remedy is named in the rules layer and carried out by the view model, so that "does this alert
/// offer the right fix, aimed at the right strip" stays a pure test. The <c>Target</c> is the half that
/// fails silently: a fix pointed at the wrong index would clear priority on somebody else's mic.
/// </summary>
public class AlertFixTests
{
    private static ChannelHealth Mic(int i, string label = "mic", bool routed = true, bool muted = false,
                                     string? device = "Wireless PRO RX", bool priority = false,
                                     double level = -30, double sinceData = 0, double sinceSound = 0,
                                     int side = 0, string? deviceId = "dev1",
                                     float speechDb = -24f, bool stale = false) =>
        new(i, label, device, routed, muted, priority, level, sinceData, sinceSound,
            null, deviceId, side, speechDb, stale);

    private static OutputHealth Bus(int i, bool device = true, bool muted = false,
                                    double sinceSound = 0, float volume = 100f) =>
        new(i, $"Bus {(char)('A' + i)}", device, muted, -20, sinceSound, volume);

    private static IReadOnlyList<HealthAlert> Run(
        IEnumerable<ChannelHealth> mics, IEnumerable<OutputHealth>? buses = null) =>
        HealthMonitor.Evaluate(new HealthSnapshot(
            mics.ToList(), (buses ?? new[] { Bus(0), Bus(1) }).ToList(), IsReplaying: false));

    private static HealthAlert Find(IReadOnlyList<HealthAlert> alerts, string idSuffix) =>
        alerts.Single(a => a.Id.EndsWith(idSuffix, StringComparison.Ordinal));

    // --- the fix is aimed at the right thing ---------------------------------------------------------

    [Fact]
    public void AMutedBusOffersToUnmuteThatBus()
    {
        var a = Find(Run(new[] { Mic(0) }, new[] { Bus(0), Bus(1, muted: true) }), "muted");

        Assert.Equal(FixKind.Unmute, a.Fix);
        Assert.Equal(1, a.Target);
    }

    [Fact]
    public void ABusTurnedDownOffersToTurnItUp()
    {
        var a = Find(Run(new[] { Mic(0) }, new[] { Bus(0), Bus(1, volume: 0f) }), "novolume");

        Assert.Equal(FixKind.RaiseVolume, a.Fix);
        Assert.Equal(1, a.Target);
    }

    /// <summary>The hazard this guards is a lapel left armed on an unused strip silently ducking every
    /// room mic off the stream — so the fix must clear priority on THAT strip, not another.</summary>
    [Fact]
    public void AnIdlePriorityLapelOffersToClearPriorityOnItsOwnStrip()
    {
        var mics = new[]
        {
            Mic(0),
            Mic(1, "LAPEL", priority: true, level: -90,
                sinceSound: HealthMonitor.IdleLapelSeconds + 10),
        };

        var a = Find(Run(mics), "idlepriority");

        Assert.Equal(FixKind.ClearPriority, a.Fix);
        Assert.Equal(1, a.Target);
    }

    [Fact]
    public void AnOffAirPresenterOffersToRouteThemBack()
    {
        var mics = new[] { Mic(0), Mic(1, "LAPEL", routed: false, priority: true, level: -20) };

        var a = Find(Run(mics), "offair");

        Assert.Equal(FixKind.RouteToBuses, a.Fix);
        Assert.Equal(1, a.Target);
    }

    [Fact]
    public void AStalledMicOffersAResync()
    {
        var a = Find(Run(new[] { Mic(0, sinceData: HealthMonitor.StallSeconds + 1) }), "stalled");

        Assert.Equal(FixKind.Resync, a.Fix);
        Assert.Equal(0, a.Target);
    }

    [Fact]
    public void AStaleCalibrationOffersToResetIt()
    {
        var a = Find(Run(new[] { Mic(0, stale: true) }), "stalecal");

        Assert.Equal(FixKind.ResetCalibration, a.Fix);
        Assert.Equal(0, a.Target);
    }

    /// <summary>Two strips on one receiver, both Stereo, each carrying the same blend. The fix targets
    /// the FIRST of the pair, which is what the view model orders its L/R assignment from.</summary>
    [Fact]
    public void AnUnsplitReceiverOffersToSplitIt()
    {
        var mics = new[]
        {
            Mic(0, "Rode 1", deviceId: "rx", side: 0),
            Mic(1, "Rode 2", deviceId: "rx", side: 0),
        };

        var a = Find(Run(mics), "split");

        Assert.Equal(FixKind.SplitSides, a.Fix);
        Assert.Equal(0, a.Target);
    }

    [Fact]
    public void AStripWithNoMicSendsTheOperatorToSettings()
    {
        var a = Find(Run(new[] { Mic(0, device: null, deviceId: null) }), "in0.nodevice");

        Assert.Equal(FixKind.OpenSettings, a.Fix);
        Assert.Equal(0, a.Target);
    }

    [Fact]
    public void ABusWithNoDeviceSendsTheOperatorToSettings()
    {
        var a = Find(Run(new[] { Mic(0) }, new[] { Bus(0), Bus(1, device: false) }), "out1.nodevice");

        Assert.Equal(FixKind.OpenSettings, a.Fix);
        Assert.Equal(1, a.Target);
    }

    // --- where there is honestly nothing to click -----------------------------------------------------

    /// <summary>
    /// A flat transmitter, a mic out of RF range, a level 22 dB under target — the fix is physical, or
    /// needs a judgement the app cannot make. Inventing a button that cannot help would be worse than
    /// the sentence, so these keep a plain caption.
    /// </summary>
    [Theory]
    [InlineData("dead")]
    [InlineData("level")]
    public void AlertsWhoseRemedyIsPhysicalOfferNoButton(string suffix)
    {
        var mics = new[]
        {
            Mic(0, level: -90, sinceSound: HealthMonitor.DeadMicSeconds + 5, speechDb: -46f),
        };

        var a = Find(Run(mics), suffix);

        Assert.Equal(FixKind.None, a.Fix);
        Assert.Equal("none", a.FixState);
        // The advice is still there, but it may live in the message rather than a caption — the dead-mic
        // alert says "check it is powered and in range" and needs no second line to repeat it.
        Assert.False(string.IsNullOrWhiteSpace(a.Action) && string.IsNullOrWhiteSpace(a.Message));
    }

    /// <summary>A silent bus can be routing, the device, or the far end — no single action is safe.</summary>
    [Fact]
    public void ASilentBusOffersNoButtonBecauseTheCauseIsAmbiguous()
    {
        var mics = new[] { Mic(0, level: -10) };
        var buses = new[] { Bus(0), Bus(1, sinceSound: HealthMonitor.OutputSilentSeconds + 5) };

        var a = Find(Run(mics, buses), "silent");

        Assert.Equal(FixKind.None, a.Fix);
    }

    // --- the invariants that keep the UI honest -------------------------------------------------------

    /// <summary>Every actionable alert must name a target, or the view model has nothing to act on.</summary>
    [Fact]
    public void EveryActionableAlertNamesBothAnActionAndATarget()
    {
        var mics = new[]
        {
            Mic(0, stale: true),
            Mic(1, "LAPEL", priority: true, level: -90, sinceSound: 3600),
            Mic(2, device: null, deviceId: null),
        };
        var buses = new[] { Bus(0, muted: true), Bus(1, volume: 0f) };

        foreach (var a in Run(mics, buses).Where(a => a.Fix != FixKind.None))
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Action), $"{a.Id} has a fix but no button text");
            Assert.True(a.Target >= 0, $"{a.Id} has a fix but no target");
            Assert.Equal("fix", a.FixState);
        }
    }

    /// <summary>Nothing wrong means nothing to fix — the empty list is the reassurance.</summary>
    [Fact]
    public void AHealthyRigOffersNoFixesBecauseItRaisesNoAlerts()
    {
        Assert.Empty(Run(new[] { Mic(0), Mic(1, deviceId: "dev2") }));
    }
}
