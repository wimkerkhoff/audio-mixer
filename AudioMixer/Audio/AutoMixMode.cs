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

    /// <summary>
    /// The priority lapel alone: every other routed mic is off, however loud, so room noise never
    /// reaches the bus while one person teaches. Sustained speech on a room mic switches the buses
    /// to Gate (Q&amp;A) by itself. With no routed, unmuted priority mic it behaves as Gate.
    /// </summary>
    Lapel = 2,
}
