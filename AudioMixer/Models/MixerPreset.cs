namespace AudioMixer.Models;

public sealed class MixerPreset
{
    public string Name { get; set; } = "Default";
    public ChannelPreset[] Channels { get; set; } = Array.Empty<ChannelPreset>();
    public OutputPreset[] Outputs { get; set; } = Array.Empty<OutputPreset>();
    public bool VbCablePromptDismissed { get; set; }
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
}

public sealed class OutputPreset
{
    public string? CustomLabel { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public int AutoMixMode { get; set; }              // 0 Off, 1 Share, 2 Gate
    public float AutoMixStrength { get; set; } = 50f;  // percent
    public bool AutoMixQualityWeighting { get; set; } = true;  // crest-factor "prefer clearest mic"
    public float Volume { get; set; } = 100f;          // percent
}
