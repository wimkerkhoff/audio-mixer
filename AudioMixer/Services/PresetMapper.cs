using AudioMixer.Models;
using AudioMixer.ViewModels;

namespace AudioMixer.Services;

// View-model state -> serializable preset. The reverse direction stays in MainViewModel.ApplyPreset,
// which has to drive input-count changes and device resolution in order.
public static class PresetMapper
{
    public static MixerPreset FromViewModels(
        IEnumerable<ChannelViewModel> channels, IEnumerable<OutputViewModel> outputs,
        bool vbCablePromptDismissed) =>
        new()
        {
            Name = "Default",
            VbCablePromptDismissed = vbCablePromptDismissed,
            Channels = channels.Select(c => new ChannelPreset
            {
                CustomLabel = c.CustomLabel,
                DeviceId = c.SelectedDevice?.Id,
                DeviceName = c.SelectedDevice?.FriendlyName,
                VolumePercent = c.VolumePercent,
                Muted = c.Muted,
                DelayMs = c.DelayMs,
                Priority = c.IsPriority,
                Routes = c.Routes.Select(r => r.IsOn).ToArray(),
                Role = (int)c.Role,
            }).ToArray(),
            Outputs = outputs.Select(o => new OutputPreset
            {
                CustomLabel = o.CustomLabel,
                DeviceId = o.SelectedDevice?.Id,
                DeviceName = o.SelectedDevice?.FriendlyName,
                AutoMixMode = o.AutoMixModeIndex,
                AutoMixStrength = o.StrengthPercent,
                AutoMixStableHandoff = o.StableHandoff,
                AutoMixReferenceGuided = o.ReferenceGuided,
                AutoMixPreferNatural = o.PreferNatural,
                Volume = o.VolumePercent,
            }).ToArray(),
        };
}
