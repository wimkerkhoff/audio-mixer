using AudioMixer.Audio;

namespace AudioMixer.Models;

public sealed class MixerPreset
{
    public string Name { get; set; } = "Default";
    public ChannelPreset[] Channels { get; set; } = Array.Empty<ChannelPreset>();
    public OutputPreset[] Outputs { get; set; } = Array.Empty<OutputPreset>();
    public bool VbCablePromptDismissed { get; set; }

    // Settings-window options. Persisted because a picker filter the operator has to re-tick on every
    // launch is not a setting. Absent in older presets, where the defaults below apply.
    public bool HideVirtualInputs { get; set; }
    public bool HideVoicemeeterOutputs { get; set; }
    public bool WarnOnBluetoothMics { get; set; } = true;

    /// <summary>
    /// One low-cut for every microphone. It was per-input until 2026-09-20, but the differences that
    /// accumulated there were accidental rather than chosen, and every mic on this rig is a voice in
    /// the same room with the same HVAC floor. 0 = off. The one case that would justify per-input
    /// again is a mic on an instrument, where an organ pedal or piano low octave lives in the band
    /// this removes.
    /// </summary>
    public int LowCutHz { get; set; } = 80;
}

public sealed class ChannelPreset
{
    public string? CustomLabel { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public float VolumePercent { get; set; } = 75f;
    public bool Muted { get; set; }
    public bool Priority { get; set; }
    public bool[] Routes { get; set; } = Array.Empty<bool>();

    // What the channel IS (0 Room, 1 Lapel), as distinct from how it is configured right now. Scenes
    // need this to survive Prayer clearing the priority flag. Absent in presets written before scenes
    // existed, where 0 is ambiguous — ApplyPreset migrates those from Priority.
    public int Role { get; set; }

    // Which side of a stereo endpoint this channel takes (0 Stereo, 1 Left, 2 Right) — a split
    // two-transmitter receiver puts one mic on each side of a single device.
    public int Source { get; set; }

    // Fixed-band high-pass in Hz; 0 = off.
    public int HighPassHz { get; set; }
}

public sealed class OutputPreset
{
    /// <summary>
    /// Maps a stored automix mode onto the current enum. It was Off=0, Share=1, Gate=2 until Share was
    /// removed on 2026-09-20, so every preset written before then stores a 2 — out of range now. Both
    /// old live modes become Gate: Share's job was follow-the-talker, which is what Gate does, and
    /// every scene already forced Gate anyway.
    ///
    /// A real function rather than a line inside ApplyPreset, because a migration that is only
    /// exercised by loading a preset is only tested by someone noticing the mixer misbehaving.
    /// </summary>
    public static Audio.AutoMixMode MigrateMode(int stored) =>
        stored <= 0 ? Audio.AutoMixMode.Off : Audio.AutoMixMode.Gate;

    public string? CustomLabel { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public int AutoMixMode { get; set; }              // 0 Off, 1 Share, 2 Gate
    public float Volume { get; set; } = 100f;          // percent

    // Bus leveler. Property initialisers, not a constructor: System.Text.Json leaves them alone when
    // a key is absent, so every preset written before the leveler existed loads with it OFF and sane
    // defaults rather than a silent zero threshold.
    public bool LevelerEnabled { get; set; }
    public int LevelerStrength { get; set; } = 1;      // 0 Gentle, 1 Medium, 2 Strong
    public float LevelerThresholdDb { get; set; } = -26f;
    public float LevelerRatio { get; set; } = 3f;
    public float LevelerAttackMs { get; set; } = 100f;
    public float LevelerReleaseMs { get; set; } = 2000f;
    public float LevelerMaxGainDb { get; set; } = 10f;
    public float LevelerIdleFloorDb { get; set; } = -45f;
    public float LimiterCeilingDb { get; set; } = -1f;
}
