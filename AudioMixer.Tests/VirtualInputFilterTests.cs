using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// The asymmetry is the point: hiding a virtual device wrong is clutter, hiding a real one wrong
/// removes a working microphone from every picker with no visible cause. Both directions are pinned
/// against the actual endpoint names on this rig.
/// </summary>
public class VirtualInputFilterTests
{
    [Theory]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)")]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)")]
    [InlineData("Voicemeeter Out A1 (VB-Audio Voicemeeter VAIO)")]
    [InlineData("Voicemeeter AUX Input (VB-Audio Voicemeeter VAIO)")]
    [InlineData("Webcam 1 (NDI Webcam Audio)")]
    [InlineData("Webcam 4 (NDI Webcam Audio)")]
    public void SoftwareEndpointsAreHidden(string name) =>
        Assert.True(VirtualInputFilter.IsVirtual(name), $"{name} should be filtered from input pickers");

    [Theory]
    [InlineData("Desktop Microphone (Wireless PRO RX)")]
    [InlineData("R0de wireless (Realtek(R) Audio)")]
    [InlineData("Headset Microphone (Lync USB Headset)")]
    [InlineData("Microphone (Jabra PanaCast)")]
    [InlineData("2- Anker Soundsync")]
    public void RealMicrophonesAreNeverHidden(string name) =>
        Assert.False(VirtualInputFilter.IsVirtual(name), $"{name} is a real mic and must stay selectable");

    /// <summary>
    /// "NDI" alone would be three letters loose in a substring match; the tag is the driver name so a
    /// real device that merely contains those letters is not swept up with the webcams.
    /// </summary>
    [Fact]
    public void TheNdiTagDoesNotMatchOnThreeLettersAlone()
    {
        Assert.False(VirtualInputFilter.IsVirtual("Sennheiser NDI-500 Microphone"));
        Assert.True(VirtualInputFilter.IsVirtual("Webcam 2 (NDI Webcam Audio)"));
    }

    [Fact]
    public void NullIsNotVirtual() => Assert.False(VirtualInputFilter.IsVirtual(null));
}
