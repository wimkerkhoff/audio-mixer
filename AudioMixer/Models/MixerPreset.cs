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
}

public sealed class ChannelPreset
{
    public string? CustomLabel { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public float VolumePercent { get; set; } = 75f;
    public bool Muted { get; set; }
    public int DelayMs { get; set; }
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
    public string? CustomLabel { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public int AutoMixMode { get; set; }              // 0 Off, 1 Share, 2 Gate
    public float AutoMixStrength { get; set; } = 50f;  // percent
    public bool AutoMixStableHandoff { get; set; } = true;  // hold+hysteresis stable closest-talker selection
    public bool AutoMixReferenceGuided { get; set; }   // pick the room mic best matching the lapel reference
    public bool AutoMixPreferNatural { get; set; }     // reference-free: prefer the most natural (stable) mic
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
