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
        bool WarnOnBluetoothMics,
        int LowCutHz);

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
            LowCutHz = options.LowCutHz,
            Channels = channels.Select(c => new ChannelPreset
            {
                CustomLabel = c.CustomLabel,
                // Fall back to the desired device so unplugging a receiver mid-session cannot erase
                // the identity the next launch resolves against.
                DeviceId = c.SelectedDevice?.Id ?? c.DesiredDeviceId,
                DeviceName = c.SelectedDevice?.FriendlyName ?? c.DesiredDeviceName,
                VolumePercent = c.VolumePercent,
                Muted = c.Muted,
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
                LevelerEnabled = o.LevelerEnabled,
                LevelerStrength = (int)o.LevelerStrength,
                LevelerThresholdDb = o.LevelerThresholdDb,
                LevelerRatio = o.LevelerRatio,
                LevelerAttackMs = o.LevelerAttackMs,
                LevelerReleaseMs = o.LevelerReleaseMs,
                LevelerMaxGainDb = o.LevelerMaxGainDb,
                LevelerIdleFloorDb = o.LevelerIdleFloorDb,
                LimiterCeilingDb = o.LimiterCeilingDb,
                Volume = o.VolumePercent,
            }).ToArray(),
        };
}
