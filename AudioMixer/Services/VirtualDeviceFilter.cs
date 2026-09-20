namespace AudioMixer.Services;

/// <summary>
/// Which endpoints are software rather than real audio hardware, per direction.
///
/// Extracted from MainViewModel so the tag lists are testable, because the two failure directions are
/// very unequal: leaving a virtual device in a picker is clutter you can see, but a tag that
/// accidentally matches a real endpoint silently removes a working device from every picker with no
/// visible cause.
///
/// The lists differ, and that asymmetry is load-bearing. Inputs filter the whole VB-Audio family,
/// because no virtual capture endpoint is ever the right microphone. Outputs filter VoiceMeeter
/// ONLY — "CABLE Input" must stay selectable as an output, since routing a bus into it is the path
/// into Zoom, and it is a VB-Audio device too. Never share one tag list between the two.
/// </summary>
public static class VirtualDeviceFilter
{
    public static readonly string[] InputTags =
    {
        "VB-Audio",
        "CABLE Output",
        "CABLE Input",
        "VoiceMeeter",
        "Virtual",
        // NDI's virtual webcam audio sources ("Webcam 1 (NDI Webcam Audio)" x4) are never a mic and
        // crowd out the real ones. Matched on the driver name, not the bare "NDI", so an endpoint
        // that merely contains those three letters is not swept up with them.
        "NDI Webcam",
    };

    /// <summary>
    /// VoiceMeeter only. This rig never chains AudioMixer into VoiceMeeter — they are two mixers and
    /// stacking them would mean two sets of routing and levels for the same audio — and VoiceMeeter
    /// installs roughly ten render endpoints, which bury the two that matter.
    /// </summary>
    public static readonly string[] OutputTags = { "Voicemeeter" };

    public static bool IsVirtualInput(string? friendlyName) => Matches(friendlyName, InputTags);

    public static bool IsVirtualOutput(string? friendlyName) => Matches(friendlyName, OutputTags);

    private static bool Matches(string? friendlyName, string[] tags) =>
        friendlyName != null
        && tags.Any(t => friendlyName.Contains(t, StringComparison.OrdinalIgnoreCase));
}
