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
        bool priority = false, double levelDb = -25, double sinceData = 0, double sinceSound = 0)
        => new(i, label, role, device, routed, muted, priority, levelDb, sinceData, sinceSound);

    private static OutputHealth Bus(int i, string label = "OBS/Zoom", bool hasDevice = true,
        bool muted = false, double peakDb = -20, double sinceSound = 0)
        => new(i, label, hasDevice, muted, peakDb, sinceSound);

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

    [Theory]
    [InlineData("ANKER #2 (Anker Soundsync)", false)]
    [InlineData("Microphone (7- Anker Soundsync)", false)]
    [InlineData("Headset (Anker PowerConf S500 Hands-Free AG Audio)", true)]
    [InlineData("Anker PowerConf S500", true)]
    public void BluetoothEndpoints_AreRecognised(string device, bool expected)
    {
        Assert.Equal(expected, HealthMonitor.IsBluetooth(device));
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
}
