using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// Zoom and OBS hear the mixer only through VB-CABLE, and a missing one used to be announced by a
/// banner in a window that no longer exists. The detection is the part that fails quietly: it matched
/// the vendor, "VB-Audio", which Voicemeeter's devices also carry, so a machine with Voicemeeter and
/// no VB-CABLE was reported as fine. The names below are the real endpoints on the rig machine.
/// </summary>
public class VbCableTests
{
    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)")]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)")]
    [InlineData("Zoom feed (VB-Audio Virtual Cable)")]        // renamed in Windows: prefix only
    [InlineData("CABLE-A Input (VB-Audio Cable A)")]
    public void VbCableEndpointsAreRecognised(string name) =>
        Assert.True(VirtualDeviceFilter.IsVbCable(name));

    [Theory]
    [InlineData("Voicemeeter Input (VB-Audio Voicemeeter VAIO)")]
    [InlineData("Voicemeeter Out A1 (VB-Audio Voicemeeter VAIO)")]
    [InlineData("Voicemeeter AUX Input (VB-Audio Voicemeeter VAIO)")]
    [InlineData("Speakers (Realtek(R) Audio)")]
    [InlineData(null)]
    public void VoicemeeterAndEverythingElseAreNot(string? name) =>
        Assert.False(VirtualDeviceFilter.IsVbCable(name));

    private static IReadOnlyList<HealthAlert> Run(bool installed) =>
        HealthMonitor.Evaluate(new HealthSnapshot(
            Array.Empty<ChannelHealth>(),
            new[] { new OutputHealth(0, "OBS/Zoom", true, false, -20, 0) },
            IsReplaying: false, VbCableInstalled: installed));

    [Fact]
    public void MissingVbCableIsAWarningThatOffersTheDownload()
    {
        var a = Assert.Single(Run(installed: false), x => x.Id == "vbcable.missing");

        Assert.Equal(AlertSeverity.Warning, a.Severity);
        Assert.Equal(FixKind.InstallVbCable, a.Fix);
    }

    [Fact]
    public void AnInstalledVbCableRaisesNothing() =>
        Assert.DoesNotContain(Run(installed: true), x => x.Id == "vbcable.missing");
}
