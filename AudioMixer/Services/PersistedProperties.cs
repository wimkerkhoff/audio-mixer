using AudioMixer.ViewModels;

namespace AudioMixer.Services;

/// <summary>
/// Exactly the view-model properties <see cref="PresetMapper"/> persists — the autosave trigger list.
///
/// This MUST stay an allowlist. The meter tick raises ~10 display properties per channel 30 times a
/// second; under a blocklist any one we forgot to exclude restarts the 500 ms autosave debounce every
/// 33 ms, so the timer never elapses and nothing is ever saved while the app runs. That bug shipped
/// once and failed silently — settings survived a clean exit (Dispose still saved) but a crash or a
/// killed process lost the whole session.
///
/// Extracted from MainViewModel so the invariant is testable: see PersistedPropertiesTests, which
/// asserts the meter tick and this set stay disjoint. A new display property must never be able to
/// break saving by omission.
/// </summary>
public static class PersistedProperties
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>
    {
        nameof(ChannelViewModel.CustomLabel),
        nameof(ChannelViewModel.SelectedDevice),
        nameof(ChannelViewModel.VolumePercent),
        nameof(ChannelViewModel.Muted),
        nameof(ChannelViewModel.DelayMs),
        nameof(ChannelViewModel.IsPriority),
        nameof(ChannelViewModel.Role),
        nameof(ChannelViewModel.Source),
        nameof(ChannelViewModel.HighPassHz),
        nameof(RouteToggleViewModel.IsOn),
        nameof(OutputViewModel.AutoMixModeIndex),
        nameof(OutputViewModel.StrengthPercent),
        nameof(OutputViewModel.StableHandoff),
        nameof(OutputViewModel.ReferenceGuided),
        nameof(OutputViewModel.PreferNatural),
        // Leveler SETTINGS only. LevelerGainDb / LevelerGainText / LevelerLiftBar are raised 30x/s by
        // RefreshMeters — allowlisting one would restart the 500 ms debounce every 33 ms and autosave
        // would silently never fire.
        nameof(OutputViewModel.LevelerEnabled),
        nameof(OutputViewModel.LevelerStrength),
        nameof(OutputViewModel.LevelerThresholdDb),
        nameof(OutputViewModel.LevelerRatio),
        nameof(OutputViewModel.LevelerAttackMs),
        nameof(OutputViewModel.LevelerReleaseMs),
        nameof(OutputViewModel.LevelerMaxGainDb),
        nameof(OutputViewModel.LevelerIdleFloorDb),
        nameof(OutputViewModel.LimiterCeilingDb),
    };

    public static bool Contains(string? propertyName) =>
        propertyName != null && Names.Contains(propertyName);
}
