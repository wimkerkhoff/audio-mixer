using AudioMixer.Models;

namespace AudioMixer.Services;

public enum AlertSeverity { Info, Warning, Critical }

/// <param name="Id">Stable key, so an alert can be dismissed and not immediately re-raised.</param>
/// <param name="Action">Short "how to fix", shown on the banner's button. Null when there is no action.</param>
public sealed record HealthAlert(string Id, AlertSeverity Severity, string Message, string? Action = null);

public sealed record ChannelHealth(
    int Index,
    string Label,
    ChannelRole Role,
    string? DeviceName,
    bool Routed,
    bool Muted,
    bool IsPriority,
    double LevelDb,
    double SecondsSinceData,
    double SecondsSinceSound,
    string? DeviceBus = null,
    string? DeviceId = null,
    int Side = 0,                 // 0 Stereo, 1 Left, 2 Right — see ChannelSource
    float SpeechDb = float.NaN);  // settled calibration median; NaN until enough voiced buffers

public sealed record OutputHealth(
    int Index,
    string Label,
    bool HasDevice,
    bool Muted,
    double PeakDb,
    double SecondsSinceSound,
    float VolumePercent = 100f);

public sealed record HealthSnapshot(
    Scene? Scene,
    IReadOnlyList<ChannelHealth> Channels,
    IReadOnlyList<OutputHealth> Outputs,
    bool IsReplaying);

/// <summary>
/// The productised version of the human-in-the-loop these sessions have needed: an operator watching
/// the /state endpoint to notice that the presenter was off-air, a mic was dead, or the priority lapel
/// had ducked the congregation off the stream. Each rule below is one failure that actually happened.
///
/// Pure so the rules are unit-testable — the situations worth alerting on are exactly the ones that
/// are hard to stage on demand.
/// </summary>
public static class HealthMonitor
{
    // A mic that has delivered no buffer at all for this long is stalled, not quiet: WASAPI shared
    // mode keeps delivering buffers through silence, so "no data" is unambiguous.
    public const double StallSeconds = 2.0;

    // Digital silence for this long on a routed mic means dead/muted-at-the-device, not a pause.
    public const double DeadMicSeconds = 30.0;

    // How long an armed-but-unused priority lapel must sit quiet before it counts as a hazard.
    public const double IdleLapelSeconds = 60.0;

    public const double OutputSilentSeconds = 10.0;

    private const double SpeechDb = -40.0;
    private const double SilenceDb = -80.0;

    /// <summary>Where speech should sit. Every absolute threshold in AutoMixer is fitted against it.</summary>
    public const double TargetSpeechDb = -24.0;

    /// <summary>
    /// How far a mic may drift from target before it is worth saying so. Wide on purpose: a few dB is
    /// normal variation between talkers, and a rule that cries at 4 dB gets ignored by the time it
    /// matters. The 2026-09-20 session ran 22 dB under, so this catches that with room to spare.
    /// </summary>
    public const double LevelToleranceDb = 10.0;

    /// <summary>Below this a bus is inaudible however hot the mix feeding it.</summary>
    public const float OutputVolumeFloorPercent = 5f;

    public static IReadOnlyList<HealthAlert> Evaluate(HealthSnapshot s)
    {
        var alerts = new List<HealthAlert>();
        var live = s.Channels.Where(c => c.Routed && !c.Muted && c.DeviceName != null).ToList();
        bool anyInputSound = s.Channels.Any(c => c.LevelDb > SilenceDb);

        // --- the stream itself ---------------------------------------------------------------
        foreach (var o in s.Outputs)
        {
            if (!o.HasDevice)
            {
                alerts.Add(new HealthAlert($"out{o.Index}.nodevice", AlertSeverity.Critical,
                    $"{o.Label}: no output device selected — nothing is reaching it.",
                    "Pick a device in Settings"));
                continue;
            }
            if (o.Muted)
            {
                alerts.Add(new HealthAlert($"out{o.Index}.muted", AlertSeverity.Warning,
                    $"{o.Label} is muted.", "Unmute"));
                continue;
            }
            // Only meaningful if the mics are actually producing something — an empty room is not a fault.
            if (o.SecondsSinceSound > OutputSilentSeconds && anyInputSound)
            {
                alerts.Add(new HealthAlert($"out{o.Index}.silent", AlertSeverity.Critical,
                    $"{o.Label} has been silent for {o.SecondsSinceSound:F0}s while mics are live.",
                    "Check routing"));
            }
        }

        if (live.Count == 0)
        {
            alerts.Add(new HealthAlert("inputs.none", AlertSeverity.Critical,
                "No microphone is routed and unmuted — the stream has no source.", "Open Advanced"));
        }

        // An output at zero volume is not muted and has a device, so every rule above passes while the
        // operator hears nothing. The headset bus sat at 0% through a live meeting on 2026-09-20.
        foreach (var o in s.Outputs.Where(o => o.HasDevice && !o.Muted
                                            && o.VolumePercent < OutputVolumeFloorPercent))
        {
            alerts.Add(new HealthAlert($"out{o.Index}.novolume", AlertSeverity.Warning,
                $"{o.Label} volume is turned down to {o.VolumePercent:F0}% — you will not hear it.",
                "Turn it up"));
        }

        // A strip routed to a bus with nothing bound to it is one someone meant to use. Unrouted empty
        // strips are just spare and must stay silent, or every rig with headroom nags forever.
        foreach (var c in s.Channels.Where(c => c.Routed && c.DeviceName == null))
        {
            alerts.Add(new HealthAlert($"in{c.Index}.nodevice", AlertSeverity.Warning,
                $"{c.Label} has no microphone assigned.",
                "Pick one, or switch its buses off"));
        }

        // Level, the fault that ran a whole meeting unnoticed. Phrased as something a volunteer can
        // do — they cannot act on a number, and the fix is never in this app.
        foreach (var c in live.Where(c => !float.IsNaN(c.SpeechDb)))
        {
            double off = c.SpeechDb - TargetSpeechDb;
            if (Math.Abs(off) < LevelToleranceDb) continue;
            bool quiet = off < 0;
            alerts.Add(new HealthAlert($"in{c.Index}.level", AlertSeverity.Warning,
                quiet
                    ? $"{c.Label} is quiet — speech is {-off:F0} dB below target."
                    : $"{c.Label} is hot — speech is {off:F0} dB above target and may distort.",
                quiet ? "Check the transmitter is on and its gain is set" : "Turn the transmitter gain down"));
        }

        // Two strips sharing one endpoint must take opposite sides of a split receiver. Left on Stereo
        // they both carry the same blend, the automixer sees one channel it cannot arbitrate, and the
        // bus gets the same audio twice.
        foreach (var g in s.Channels
                     .Where(c => c.DeviceName != null && c.DeviceId != null)
                     .GroupBy(c => c.DeviceId!)
                     .Where(g => g.Count() > 1))
        {
            var stereo = g.Where(c => c.Side == 0).ToList();
            if (stereo.Count == 0) continue;
            var names = string.Join(" and ", g.Select(c => c.Label));
            alerts.Add(new HealthAlert($"in{stereo[0].Index}.split", AlertSeverity.Warning,
                $"{names} share one receiver but are not split — both carry the same blended audio.",
                "Set one to Left and the other to Right"));
        }

        // --- the priority-duck hazard ----------------------------------------------------------
        // An armed priority mic hard-mutes every room mic the moment it crosses -40 dBFS. If it is not
        // actually in use, a bump or a drift takes the whole room off the stream with no visible cause.
        foreach (var c in s.Channels.Where(c => c.IsPriority && c.Routed && !c.Muted))
        {
            if (c.SecondsSinceSound > IdleLapelSeconds)
            {
                alerts.Add(new HealthAlert($"in{c.Index}.idlepriority", AlertSeverity.Warning,
                    $"{c.Label} is armed as priority but has been silent {c.SecondsSinceSound / 60:F0} min — " +
                    "if it is bumped it will duck every room mic off the stream.",
                    "Clear priority"));
            }
        }

        // Singing inverts the automixer's assumption: any priority mic gates the congregation out.
        if (s.Scene == Models.Scene.Singing)
        {
            foreach (var c in s.Channels.Where(c => c.IsPriority && c.Routed))
            {
                alerts.Add(new HealthAlert($"in{c.Index}.singingpriority", AlertSeverity.Critical,
                    $"{c.Label} is still a priority mic during Singing — the congregation is being ducked off the stream.",
                    "Re-apply Singing"));
            }
        }

        // A priority mic that is speaking but unrouted means the presenter is off-air on that bus.
        foreach (var c in s.Channels.Where(c => c.IsPriority && !c.Routed && c.LevelDb > SpeechDb))
        {
            alerts.Add(new HealthAlert($"in{c.Index}.offair", AlertSeverity.Critical,
                $"{c.Label} is live but not routed to any output — the presenter is off-air.", "Route it"));
        }

        // --- per-mic health ---------------------------------------------------------------------
        foreach (var c in s.Channels.Where(c => c.DeviceName != null))
        {
            // Replay has no devices, so stall detection is meaningless there.
            if (!s.IsReplaying && c.SecondsSinceData > StallSeconds)
            {
                alerts.Add(new HealthAlert($"in{c.Index}.stalled", AlertSeverity.Critical,
                    $"{c.Label} has stopped delivering audio ({c.SecondsSinceData:F0}s) — the device may have dropped.",
                    "Resync"));
                continue;
            }

            if (IsBluetooth(c.DeviceBus, c.DeviceName!))
            {
                alerts.Add(new HealthAlert($"in{c.Index}.bluetooth", AlertSeverity.Warning,
                    $"{c.Label} is connected over Bluetooth, not its Soundsync dongle — quality drops and it " +
                    "contends with the other dongles.", "How to fix"));
            }

            if (c.Routed && !c.Muted && c.SecondsSinceSound > DeadMicSeconds)
            {
                alerts.Add(new HealthAlert($"in{c.Index}.dead", AlertSeverity.Warning,
                    $"{c.Label} has been silent for {c.SecondsSinceSound:F0}s — check it is powered and in range.",
                    null));
            }
        }

        return alerts.OrderByDescending(a => a.Severity).ToList();
    }

    /// <summary>
    /// Binding a Bluetooth endpoint means HSP/HFP quality plus a second 2.4 GHz radio contending with
    /// the dongles.
    ///
    /// Decided from the Windows device-enumerator name, NOT the friendly name. The name test this
    /// replaced matched a bare "Headset" and so fired on "Headset Microphone (Lync USB Headset)", a
    /// wired USB device — and because the channel was still labelled from a retired mic, the banner
    /// read "Anker 3 is connected over Bluetooth" with no Anker in the building.
    ///
    /// The name fallback survives only for when the bus cannot be read, and only on strings that
    /// cannot mean anything else: "Hands-Free" is the Bluetooth HFP profile, and "PowerConf" is an
    /// Anker unit's BT endpoint (its dongle endpoint says Soundsync instead).
    /// </summary>
    public static bool IsBluetooth(string? deviceBus, string deviceName)
    {
        if (deviceBus != null)
        {
            return string.Equals(deviceBus, Audio.AudioDeviceInfo.BluetoothBus,
                StringComparison.OrdinalIgnoreCase);
        }

        if (deviceName.Contains("Soundsync", StringComparison.OrdinalIgnoreCase)) return false;
        return deviceName.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("PowerConf", StringComparison.OrdinalIgnoreCase);
    }
}
