namespace AudioMixer.Models;

public sealed class MixerPreset
{
    public string Name { get; set; } = "Default";
    public ChannelPreset[] Channels { get; set; } = Array.Empty<ChannelPreset>();
    public OutputPreset[] Outputs { get; set; } = Array.Empty<OutputPreset>();
}

public sealed class ChannelPreset
{
    public string? CustomLabel { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public float VolumePercent { get; set; } = 75f;
    public bool Muted { get; set; }
    public int DelayMs { get; set; }
    public bool[] Routes { get; set; } = Array.Empty<bool>();
}

public sealed class OutputPreset
{
    public string? CustomLabel { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
}
