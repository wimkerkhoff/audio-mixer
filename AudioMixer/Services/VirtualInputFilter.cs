namespace AudioMixer.Services;

/// <summary>
/// Which capture endpoints are software, not microphones.
///
/// Extracted from MainViewModel so the tag list is testable, because the two failure directions are
/// very unequal: leaving a virtual device in the picker is clutter, but a tag that accidentally
/// matches a real endpoint silently removes a working microphone from every picker. The tests pin
/// the real devices on this rig as visible.
///
/// Applies to INPUTS only — "CABLE Input" must stay selectable as an output, since that is the path
/// into Zoom.
/// </summary>
public static class VirtualInputFilter
{
    public static readonly string[] Tags =
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

    public static bool IsVirtual(string? friendlyName) =>
        friendlyName != null
        && Tags.Any(t => friendlyName.Contains(t, StringComparison.OrdinalIgnoreCase));
}
