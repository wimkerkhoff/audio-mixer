using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// The asymmetry is the point: hiding a virtual device wrong is clutter, hiding a real one wrong
/// removes a working microphone from every picker with no visible cause. Both directions are pinned
/// against the actual endpoint names on this rig.
/// </summary>
public class VirtualDeviceFilterTests
{
    [Theory]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)")]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)")]
    [InlineData("Voicemeeter Out A1 (VB-Audio Voicemeeter VAIO)")]
    [InlineData("Voicemeeter AUX Input (VB-Audio Voicemeeter VAIO)")]
    [InlineData("Webcam 1 (NDI Webcam Audio)")]
    [InlineData("Webcam 4 (NDI Webcam Audio)")]
    public void SoftwareEndpointsAreHidden(string name) =>
        Assert.True(VirtualDeviceFilter.IsVirtualInput(name), $"{name} should be filtered from input pickers");

    [Theory]
    [InlineData("Desktop Microphone (Wireless PRO RX)")]
    [InlineData("R0de wireless (Realtek(R) Audio)")]
    [InlineData("Headset Microphone (Lync USB Headset)")]
    [InlineData("Microphone (Jabra PanaCast)")]
    [InlineData("2- Anker Soundsync")]
    public void RealMicrophonesAreNeverHidden(string name) =>
        Assert.False(VirtualDeviceFilter.IsVirtualInput(name), $"{name} is a real mic and must stay selectable");

    /// <summary>
    /// "NDI" alone would be three letters loose in a substring match; the tag is the driver name so a
    /// real device that merely contains those letters is not swept up with the webcams.
    /// </summary>
    [Fact]
    public void TheNdiTagDoesNotMatchOnThreeLettersAlone()
    {
        Assert.False(VirtualDeviceFilter.IsVirtualInput("Sennheiser NDI-500 Microphone"));
        Assert.True(VirtualDeviceFilter.IsVirtualInput("Webcam 2 (NDI Webcam Audio)"));
    }

    [Fact]
    public void NullIsNotVirtual()
    {
        Assert.False(VirtualDeviceFilter.IsVirtualInput(null));
        Assert.False(VirtualDeviceFilter.IsVirtualOutput(null));
    }

    // --- outputs ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("Voicemeeter Input (VB-Audio Voicemeeter VAIO)")]
    [InlineData("Voicemeeter AUX Input (VB-Audio Voicemeeter VAIO)")]
    [InlineData("Voicemeeter In 3 (VB-Audio Voicemeeter VAIO)")]
    [InlineData("Voicemeeter VAIO3 Input (VB-Audio Voicemeeter VAIO)")]
    public void VoicemeeterRenderEndpointsAreHidden(string name) =>
        Assert.True(VirtualDeviceFilter.IsVirtualOutput(name), $"{name} should be filtered from output pickers");

    /// <summary>
    /// The one that must never regress: "CABLE Input" is a VB-Audio device like VoiceMeeter, but
    /// routing a bus into it is the path into Zoom. Hiding it would cut the stream feed with no
    /// visible cause, which is why the input and output tag lists are separate.
    /// </summary>
    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)")]
    [InlineData("Speakers (Lync USB Headset)")]
    [InlineData("Speakers (Realtek(R) Audio)")]
    public void RealAndCableOutputsAreNeverHidden(string name) =>
        Assert.False(VirtualDeviceFilter.IsVirtualOutput(name), $"{name} must stay selectable as an output");

    /// <summary>VB-CABLE is hidden as an INPUT but visible as an OUTPUT — the asymmetry is the design.</summary>
    [Fact]
    public void CableInputIsHiddenForInputsButNotForOutputs()
    {
        Assert.True(VirtualDeviceFilter.IsVirtualInput("CABLE Input (VB-Audio Virtual Cable)"));
        Assert.False(VirtualDeviceFilter.IsVirtualOutput("CABLE Input (VB-Audio Virtual Cable)"));
    }
}
