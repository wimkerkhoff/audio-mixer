using AudioMixer.Models;
using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// Each rule here corresponds to a failure that actually happened on the rig and had to be diagnosed
/// by a human reading the /state endpoint. They are also exactly the situations that are hard to stage
/// on demand, which is why the evaluator is pure and tested rather than eyeballed.
/// </summary>
public class HealthMonitorTests
{
    private static ChannelHealth Mic(int i, string label = "Anker", ChannelRole role = ChannelRole.Room,
        string? device = "ANKER #1 (Anker Soundsync)", bool routed = true, bool muted = false,
        bool priority = false, double levelDb = -25, double sinceData = 0, double sinceSound = 0,
        string? bus = null, string? deviceId = null, int side = 0, float speechDb = float.NaN)
        => new(i, label, role, device, routed, muted, priority, levelDb, sinceData, sinceSound,
               bus, deviceId, side, speechDb);

    private static OutputHealth Bus(int i, string label = "OBS/Zoom", bool hasDevice = true,
        bool muted = false, double peakDb = -20, double sinceSound = 0, float volume = 100f)
        => new(i, label, hasDevice, muted, peakDb, sinceSound, volume);

    private static HealthSnapshot Snap(IEnumerable<ChannelHealth>? ch = null,
        IEnumerable<OutputHealth>? outs = null, Scene? scene = null, bool replay = false)
        => new(scene, (ch ?? new[] { Mic(0) }).ToList(), (outs ?? new[] { Bus(0) }).ToList(), replay);

    private static bool Has(IReadOnlyList<HealthAlert> a, string idSuffix) =>
        a.Any(x => x.Id.EndsWith(idSuffix, StringComparison.Ordinal));

    [Fact]
    public void HealthyRig_RaisesNothing()
    {
        Assert.Empty(HealthMonitor.Evaluate(Snap()));
    }

    [Fact]
    public void NoOutputDevice_IsCritical()
    {
        var a = HealthMonitor.Evaluate(Snap(outs: new[] { Bus(0, hasDevice: false) }));
        Assert.Contains(a, x => x.Severity == AlertSeverity.Critical && x.Id.EndsWith(".nodevice"));
    }

    [Fact]
    public void OutputSilentWhileMicsAreLive_IsCritical()
    {
        var a = HealthMonitor.Evaluate(Snap(
            ch: new[] { Mic(0, levelDb: -20) },
            outs: new[] { Bus(0, sinceSound: 30) }));
        Assert.Contains(a, x => x.Severity == AlertSeverity.Critical && x.Id.EndsWith(".silent"));
    }

    [Fact]
    public void OutputSilentInAnEmptyRoom_IsNotAnAlert()
    {
        // A quiet room is not a fault; alerting on it would train operators to ignore the banner.
        var a = HealthMonitor.Evaluate(Snap(
            ch: new[] { Mic(0, levelDb: -120, sinceSound: 60) },
            outs: new[] { Bus(0, sinceSound: 60) }));
        Assert.False(Has(a, ".silent"));
    }

    [Fact]
    public void NoMicRoutedOrUnmuted_IsCritical()
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[] { Mic(0, routed: false), Mic(1, muted: true) }));
        Assert.Contains(a, x => x.Severity == AlertSeverity.Critical && x.Id == "inputs.none");
    }

    [Fact]
    public void IdleArmedLapel_WarnsAboutTheDuckHazard()
    {
        // The documented hazard: an unused open priority lapel that crosses -40 dBFS silently ducks
        // every room mic off the stream.
        var a = HealthMonitor.Evaluate(Snap(ch: new[]
        {
            Mic(0, "LAPEL", ChannelRole.Lapel, "R0de wireless", priority: true, levelDb: -65, sinceSound: 300),
            Mic(1),
        }));
        Assert.Contains(a, x => x.Id.EndsWith(".idlepriority"));
    }

    [Fact]
    public void ActivelyUsedLapel_DoesNotWarn()
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[]
        {
            Mic(0, "LAPEL", ChannelRole.Lapel, "R0de wireless", priority: true, levelDb: -22, sinceSound: 0),
            Mic(1),
        }));
        Assert.False(Has(a, ".idlepriority"));
    }

    [Fact]
    public void PriorityMicDuringSinging_IsCritical()
    {
        // The 2026-07-05 failure: worship reached the stream as pastor-only because the priority lapel
        // ducked every room mic to zero.
        var a = HealthMonitor.Evaluate(Snap(
            ch: new[] { Mic(0, "LAPEL", ChannelRole.Lapel, "R0de wireless", priority: true), Mic(1) },
            scene: Scene.Singing));
        Assert.Contains(a, x => x.Severity == AlertSeverity.Critical && x.Id.EndsWith(".singingpriority"));
    }

    [Fact]
    public void SpeakingButUnroutedPriorityMic_MeansPresenterOffAir()
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[]
        {
            Mic(0, "LAPEL", ChannelRole.Lapel, "R0de wireless", routed: false, priority: true, levelDb: -20),
            Mic(1),
        }));
        Assert.Contains(a, x => x.Severity == AlertSeverity.Critical && x.Id.EndsWith(".offair"));
    }

    [Fact]
    public void StalledMic_IsCritical()
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[] { Mic(0, sinceData: 5) }));
        Assert.Contains(a, x => x.Severity == AlertSeverity.Critical && x.Id.EndsWith(".stalled"));
    }

    [Fact]
    public void StallDetection_IsSuppressedDuringReplay()
    {
        // Replay has no devices, so "the device dropped" is meaningless and would fire constantly.
        var a = HealthMonitor.Evaluate(Snap(ch: new[] { Mic(0, sinceData: 5) }, replay: true));
        Assert.False(Has(a, ".stalled"));
    }

    /// <summary>The bus is authoritative; the friendly name is not consulted at all when it is known.</summary>
    [Theory]
    [InlineData("BTHENUM", "Headset (Anker PowerConf S500 Hands-Free AG Audio)", true)]
    [InlineData("BTHENUM", "anything at all", true)]
    [InlineData("USB", "Headset Microphone (Lync USB Headset)", false)]
    [InlineData("USB", "Headset (Anker PowerConf S500 Hands-Free AG Audio)", false)]
    [InlineData("HDAUDIO", "R0de wireless (Realtek(R) Audio)", false)]
    [InlineData("ROOT", "CABLE Output (VB-Audio Virtual Cable)", false)]
    public void TheBusDecides(string bus, string device, bool expected) =>
        Assert.Equal(expected, HealthMonitor.IsBluetooth(bus, device));

    /// <summary>
    /// The regression this replaced: a wired USB headset tripped the warning because the old test
    /// matched a bare "Headset", and a stale channel label made it read as an Anker that had been
    /// returned weeks earlier.
    /// </summary>
    [Fact]
    public void AWiredUsbHeadsetIsNotBluetooth()
    {
        Assert.False(HealthMonitor.IsBluetooth("USB", "Headset Microphone (Lync USB Headset)"));
        Assert.False(HealthMonitor.IsBluetooth(null, "Headset Microphone (Lync USB Headset)"));
    }

    /// <summary>Only used when the bus cannot be read, and only on strings that cannot mean anything else.</summary>
    [Theory]
    [InlineData("ANKER #2 (Anker Soundsync)", false)]
    [InlineData("Microphone (7- Anker Soundsync)", false)]
    [InlineData("Headset (Anker PowerConf S500 Hands-Free AG Audio)", true)]
    [InlineData("Anker PowerConf S500", true)]
    public void TheNameFallbackAppliesOnlyWhenTheBusIsUnknown(string device, bool expected) =>
        Assert.Equal(expected, HealthMonitor.IsBluetooth(null, device));

    /// <summary>End to end through Evaluate, not just the predicate: the banner is what the operator sees.</summary>
    [Fact]
    public void AUsbHeadsetMicRaisesNoBluetoothAlert()
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[]
        {
            Mic(0, label: "Anker 3", device: "Headset Microphone (Lync USB Headset)", bus: "USB"),
        }));
        Assert.False(Has(a, ".bluetooth"));
    }

    [Fact]
    public void MicOnBluetooth_Warns()
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[]
        {
            Mic(0, device: "Headset (Anker PowerConf S500 Hands-Free AG Audio)"),
        }));
        Assert.Contains(a, x => x.Id.EndsWith(".bluetooth"));
    }

    [Fact]
    public void LongSilentRoutedMic_WarnsItMayBeDead()
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[] { Mic(0, sinceSound: 120), Mic(1) }));
        Assert.Contains(a, x => x.Id.EndsWith(".dead"));
    }

    [Fact]
    public void AlertsAreOrderedMostSevereFirst()
    {
        var a = HealthMonitor.Evaluate(Snap(
            ch: new[] { Mic(0, device: "Anker PowerConf S500"), Mic(1, sinceData: 9) },
            outs: new[] { Bus(0, hasDevice: false) }));
        Assert.True(a.Count >= 2);
        for (int i = 1; i < a.Count; i++) Assert.True(a[i - 1].Severity >= a[i].Severity);
    }

    // --- rules added 2026-09-20, each one a failure that actually happened -----------------------

    /// <summary>A whole meeting ran 22 dB under target with nothing said about it.</summary>
    [Theory]
    [InlineData(-46, true, "quiet")]
    [InlineData(-45, true, "quiet")]
    [InlineData(-8, true, "hot")]
    [InlineData(-24, false, null)]
    [InlineData(-30, false, null)]   // 6 dB off: inside tolerance, stays silent
    [InlineData(-18, false, null)]
    public void LevelFarFromTarget_Warns(double speech, bool expected, string? word)
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[] { Mic(0, speechDb: (float)speech) }));

        Assert.Equal(expected, Has(a, ".level"));
        if (word != null) Assert.Contains(word, a.First(x => x.Id.EndsWith(".level")).Message);
    }

    /// <summary>Until enough voiced buffers land the median is NaN, and a guess is worse than silence.</summary>
    [Fact]
    public void LevelIsNotJudgedBeforeItIsMeasured() =>
        Assert.False(Has(HealthMonitor.Evaluate(Snap(ch: new[] { Mic(0) })), ".level"));

    [Fact]
    public void LevelIsNotJudgedOnAMicThatIsNotLive() =>
        Assert.False(Has(HealthMonitor.Evaluate(
            Snap(ch: new[] { Mic(0, routed: false, speechDb: -46f) })), ".level"));

    /// <summary>Has a device and is not muted, so every other output rule passes while nothing is heard.</summary>
    [Theory]
    [InlineData(0f, true)]
    [InlineData(3f, true)]
    [InlineData(40f, false)]
    [InlineData(100f, false)]
    public void OutputTurnedDownToNothing_Warns(float volume, bool expected) =>
        Assert.Equal(expected, Has(HealthMonitor.Evaluate(
            Snap(outs: new[] { Bus(0, volume: volume) })), ".novolume"));

    /// <summary>Routed but unbound is a strip someone meant to use — today's actual failure.</summary>
    [Fact]
    public void ARoutedStripWithNoDevice_Warns() =>
        Assert.True(Has(HealthMonitor.Evaluate(
            Snap(ch: new[] { Mic(0), Mic(1, device: null, routed: true) })), ".nodevice"));

    /// <summary>A spare strip is not a fault, or every rig with headroom nags forever.</summary>
    [Fact]
    public void AnUnroutedStripWithNoDevice_IsSilent() =>
        Assert.False(Has(HealthMonitor.Evaluate(
            Snap(ch: new[] { Mic(0), Mic(1, device: null, routed: false) })), ".nodevice"));

    /// <summary>Both halves on Stereo carry the same blend, and the automixer cannot arbitrate it.</summary>
    [Fact]
    public void TwoStripsOnOneReceiverBothStereo_Warns()
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[]
        {
            Mic(0, label: "Rode L", device: "Wireless PRO RX", deviceId: "{rx}", side: 0),
            Mic(1, label: "Rode R", device: "Wireless PRO RX", deviceId: "{rx}", side: 0),
        }));

        Assert.True(Has(a, ".split"));
    }

    [Fact]
    public void AProperlySplitReceiver_IsSilent()
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[]
        {
            Mic(0, label: "Rode L", device: "Wireless PRO RX", deviceId: "{rx}", side: 1),
            Mic(1, label: "Rode R", device: "Wireless PRO RX", deviceId: "{rx}", side: 2),
        }));

        Assert.False(Has(a, ".split"));
    }

    [Fact]
    public void TwoDifferentReceivers_AreNotASplitProblem()
    {
        var a = HealthMonitor.Evaluate(Snap(ch: new[]
        {
            Mic(0, device: "RX one", deviceId: "{a}", side: 0),
            Mic(1, device: "RX two", deviceId: "{b}", side: 0),
        }));

        Assert.False(Has(a, ".split"));
    }

    // --- a bus whose stream died under a device that is still present ---------------------------

    /// <summary>
    /// The failure this closes: PeakMeter has no decay, so when WasapiOut stops on error the tap keeps
    /// its last peak forever and the health snapshot goes on seeing a bus that is "producing sound".
    /// Nothing in Checks ever mentioned it. Device *removal* was already covered; a stopped stream on
    /// a present device (format renegotiation, another app grabbing the endpoint, a USB headset
    /// changing rate) was not.
    /// </summary>
    [Fact]
    public void ABusWhoseStreamHasStoppedIsCritical()
    {
        var alerts = HealthMonitor.Evaluate(new HealthSnapshot(
            null,
            new[] { Mic(0) },
            new[] { Bus(0), Bus(1) with { Playing = false } },
            IsReplaying: false));

        var a = Assert.Single(alerts, x => x.Id == "out1.stopped");
        Assert.Equal(AlertSeverity.Critical, a.Severity);
        Assert.Equal(FixKind.Resync, a.Fix);
        Assert.Equal(1, a.Target);
    }

    /// <summary>Reported once, as the cause — not also as its symptom.</summary>
    [Fact]
    public void AStoppedBusDoesNotAlsoRaiseTheMutedOrSilentRules()
    {
        var alerts = HealthMonitor.Evaluate(new HealthSnapshot(
            null,
            new[] { Mic(0, levelDb: -10) },
            new[] { Bus(0), Bus(1) with { Playing = false, Muted = true, SecondsSinceSound = 600 } },
            IsReplaying: false));

        Assert.Contains(alerts, x => x.Id == "out1.stopped");
        Assert.DoesNotContain(alerts, x => x.Id == "out1.muted");
        Assert.DoesNotContain(alerts, x => x.Id == "out1.silent");
    }

    /// <summary>A bus with no device is already reported as such; saying "stopped" too would be
    /// noise, and the snapshot deliberately reports Playing=true when there is nothing to play.</summary>
    [Fact]
    public void ABusWithNoDeviceIsNotAlsoReportedAsStopped()
    {
        var alerts = HealthMonitor.Evaluate(new HealthSnapshot(
            null,
            new[] { Mic(0) },
            new[] { Bus(0), Bus(1) with { HasDevice = false } },
            IsReplaying: false));

        Assert.Contains(alerts, x => x.Id == "out1.nodevice");
        Assert.DoesNotContain(alerts, x => x.Id == "out1.stopped");
    }

    [Fact]
    public void AHealthyPlayingBusRaisesNothing()
    {
        var alerts = HealthMonitor.Evaluate(new HealthSnapshot(
            null, new[] { Mic(0) }, new[] { Bus(0), Bus(1) }, IsReplaying: false));

        Assert.DoesNotContain(alerts, x => x.Id.EndsWith(".stopped"));
    }
}
