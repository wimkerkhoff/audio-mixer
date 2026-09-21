namespace AudioMixer.Audio;

/// <summary>
/// What the automixer does to the mics on a bus.
///
/// Share was removed 2026-09-20. It attenuated non-leaders instead of muting them, so several mics
/// hearing one voice still all reached the bus at slightly different delays and comb-filtered — the
/// strength slider could only make that quieter, never remove it. No scene ever selected it either:
/// Teaching and Prayer force Gate, Singing forces Off.
/// </summary>
public enum AutoMixMode
{
    Off = 0,

    /// <summary>
    /// Winner-take-all: the held leader passes at unity and every other mic is muted, so exactly one
    /// copy of a voice reaches the bus.
    /// </summary>
    Gate = 1,
}
