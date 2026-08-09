namespace AudioMixer.Audio.Replay;

/// <summary>
/// Sandbox settings for a <c>--replay</c> launch. Replay exists so the app can be exercised without a
/// room full of people, which means it usually runs *while* the operator's real mixer is up — so it
/// deliberately behaves like a dev tool rather than a mixer:
///
/// <list type="bullet">
/// <item>a separate single-instance mutex, so it doesn't get bounced by (or bounce) the live app;</item>
/// <item><see cref="SuppressAutosave"/> — it must never overwrite the operator's preset with fake
/// devices, and the replayed inputs have no devices at all;</item>
/// <item><see cref="SuppressOutputDevices"/> — two instances both opening CABLE Input would double
/// audio into Zoom mid-service. Outputs start unset; pick one by hand to listen.</item>
/// </list>
/// </summary>
public sealed class ReplayOptions
{
    /// <summary>Non-null when the process was launched with <c>--replay</c>.</summary>
    public static ReplayOptions? Current { get; set; }

    public static double Speed { get; set; } = 1.0;
    public static bool Loop { get; set; }

    /// <summary>Start offset into the recording, so a fixture can name a labelled segment.</summary>
    public static TimeSpan Seek { get; set; }

    /// <summary>Stop after this much replayed audio (0 = play to the end). Used by batch runs.</summary>
    public static TimeSpan Duration { get; set; }

    /// <summary>Accepts <c>SS</c>, <c>MM:SS</c> or <c>HH:MM:SS</c>.</summary>
    public static TimeSpan ParseTime(string s)
    {
        var parts = s.Split(':');
        double total = 0;
        foreach (var p in parts) total = total * 60 + double.Parse(p);
        return TimeSpan.FromSeconds(total);
    }

    /// <summary>Session stamp, a unique substring of one, or null for the most recent session.</summary>
    public string? Stamp { get; init; }

    /// <summary>Directory holding the diag WAVs; null uses the standard analysis folder.</summary>
    public string? Directory { get; init; }

    public bool SuppressAutosave { get; init; } = true;
    public bool SuppressOutputDevices { get; init; } = true;
}
