using AudioMixer.Models;
using AudioMixer.ViewModels;

namespace AudioMixer.Services;

// View-model state -> serializable preset. The reverse direction stays in MainViewModel.ApplyPreset,
// which has to drive input-count changes and device resolution in order.
public static class PresetMapper
{
    /// <summary>App-level state that is not per-channel or per-output.</summary>
    public readonly record struct AppOptions(
        bool VbCablePromptDismissed,
        bool HideVirtualInputs,
        bool HideVoicemeeterOutputs,
        bool WarnOnBluetoothMics);

    public static MixerPreset FromViewModels(
        IEnumerable<ChannelViewModel> channels, IEnumerable<OutputViewModel> outputs,
        AppOptions options) =>
        new()
        {
            Name = "Default",
            VbCablePromptDismissed = options.VbCablePromptDismissed,
            HideVirtualInputs = options.HideVirtualInputs,
            HideVoicemeeterOutputs = options.HideVoicemeeterOutputs,
            WarnOnBluetoothMics = options.WarnOnBluetoothMics,
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
                Source = (int)c.Source,
                HighPassHz = c.HighPassHz,
            }).ToArray(),
            Outputs = outputs.Select(o => new OutputPreset
            {
                CustomLabel = o.CustomLabel,
                DeviceId = o.SelectedDevice?.Id,
                DeviceName = o.SelectedDevice?.FriendlyName,
                AutoMixMode = o.AutoMixModeIndex,
                AutoMixStrength = o.StrengthPercent,
                AutoMixStableHandoff = o.StableHandoff,
                LevelerEnabled = o.LevelerEnabled,
                LevelerStrength = (int)o.LevelerStrength,
                LevelerThresholdDb = o.LevelerThresholdDb,
                LevelerRatio = o.LevelerRatio,
                LevelerAttackMs = o.LevelerAttackMs,
                LevelerReleaseMs = o.LevelerReleaseMs,
                LevelerMaxGainDb = o.LevelerMaxGainDb,
                LevelerIdleFloorDb = o.LevelerIdleFloorDb,
                LimiterCeilingDb = o.LimiterCeilingDb,
                AutoMixReferenceGuided = o.ReferenceGuided,
                AutoMixPreferNatural = o.PreferNatural,
                Volume = o.VolumePercent,
            }).ToArray(),
        };
}
