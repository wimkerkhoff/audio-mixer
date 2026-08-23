namespace AudioMixer.Audio;

/// <summary>
/// Which part of a capture endpoint's stereo stream this channel takes.
///
/// A two-transmitter wireless receiver (RØDE Wireless PRO in Split mode) puts TX1 on the left and
/// TX2 on the right of ONE endpoint. Bound whole, the automixer sees a single blended channel and
/// cannot arbitrate between the two mics — and the hard-panned pair reaches the bus as-is. Left/
/// Right make one endpoint feed two independent strips, split before every analysis tap so each
/// side gets its own level, flux-CV, RF tally and automix gain.
/// </summary>
public enum ChannelSource
{
    Stereo = 0,
    Left = 1,
    Right = 2,
}
