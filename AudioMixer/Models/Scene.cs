namespace AudioMixer.Models;

/// <summary>
/// The one control that matters in Simple mode. Each scene switches the whole behaviour, because the
/// automixer's "one talker at a time" assumption holds for teaching, inverts for singing, and needs
/// different hand-off behaviour for turn-taking prayer.
/// </summary>
public enum Scene
{
    /// <summary>Outputs muted, so pre-service chatter never reaches Zoom or the recording.</summary>
    Standby,

    /// <summary>Follow-the-talker: Gate, stable hand-off, priority lapel ducks the room.</summary>
    Teaching,

    /// <summary>
    /// Turn-taking room mics, no lapel. Gate (Share sums several mics hearing one voice and combs),
    /// prefer-natural OFF (flux-CV vetoes a bad mic well but picks badly among good ones), and the
    /// lapel unrouted + de-prioritised because an idle open priority mic ducks the whole room.
    /// </summary>
    Prayer,

    /// <summary>
    /// Congregational singing. Suspends priority-ducking and stops follow-the-talker. Deliberately
    /// does NOT try to pick a "best" mic: measured 2026-08-09, the Ankers gate singing to digital
    /// silence in unison, so no selection or mix topology helps (CLAUDE.md finding 4). Prefers the
    /// lapel when one is in use, because a Rode has no speakerphone DSP and does not gate.
    /// </summary>
    Singing,
}

/// <summary>The single operator override exposed in Simple mode, in Teaching and Singing only.</summary>
public enum VoiceSource
{
    /// <summary>Let the scene decide (lapel if one exists, else the room mics).</summary>
    Auto,
    Lapel,
    RoomMics,
}

/// <summary>
/// What a channel *is*, as distinct from how it happens to be configured right now. Scenes need a
/// stable answer to "which mic is the lapel" that survives Prayer clearing the priority flag, so this
/// cannot be inferred from <c>IsPriority</c> at apply time.
/// </summary>
public enum ChannelRole
{
    Room,
    Lapel,
}
