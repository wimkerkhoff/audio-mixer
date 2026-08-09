using AudioMixer.Audio;
using AudioMixer.Services;
using AudioMixer.ViewModels;

namespace AudioMixer.Tests;

/// <summary>
/// Guards the autosave allowlist. The meter tick raises display properties 30x/second; if any of them
/// is also a persisted property, the 500 ms autosave debounce is restarted every 33 ms and settings
/// are never saved while the app runs — a silent failure that only shows up when the process is killed
/// instead of exited. These tests make that impossible to reintroduce by adding a display property.
/// </summary>
public class PersistedPropertiesTests
{
    private static ChannelViewModel MakeChannel(InputChannel channel) =>
        new(0, channel, Array.Empty<AudioDeviceInfo>(), AudioEngine.OutputCount, (_, _) => { });

    /// <summary>Records every property name raised while running an action.</summary>
    private static List<string> CaptureRaised(ViewModelBase vm, Action act)
    {
        var raised = new List<string>();
        void Handler(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != null) raised.Add(e.PropertyName);
        }
        vm.PropertyChanged += Handler;
        try { act(); } finally { vm.PropertyChanged -= Handler; }
        return raised;
    }

    [Fact]
    public void MeterTick_RaisesNothingThatTriggersAutosave()
    {
        using var input = new InputChannel(AudioEngine.OutputCount);
        var ch = MakeChannel(input);

        var raised = CaptureRaised(ch, ch.RefreshMeters);

        Assert.NotEmpty(raised);   // a no-op RefreshMeters would make this test vacuous
        var offenders = raised.Where(PersistedProperties.Contains).Distinct().ToList();
        Assert.True(offenders.Count == 0,
            $"RefreshMeters raises persisted propert{(offenders.Count == 1 ? "y" : "ies")} " +
            $"[{string.Join(", ", offenders)}] — this resets the autosave debounce 30x/second and " +
            "autosave will never fire. Either stop raising it on the meter tick or stop persisting it.");
    }

    [Fact]
    public void RouteLedRefresh_DoesNotRaiseTheRouteToggleItself()
    {
        // IsOn is persisted, so the per-bus LED refresh must raise only IsDucking. Raising IsOn here
        // is the exact mistake that would kill autosave via the route toggles.
        using var input = new InputChannel(AudioEngine.OutputCount);
        var route = new RouteToggleViewModel(0, input);

        var raised = CaptureRaised(route, route.RefreshLed);

        Assert.Contains(nameof(RouteToggleViewModel.IsDucking), raised);
        Assert.DoesNotContain(nameof(RouteToggleViewModel.IsOn), raised);
    }

    [Fact]
    public void ChangingAPersistedProperty_IsRecognisedByTheAllowlist()
    {
        // The inverse failure: an allowlist that is too narrow silently stops persisting a setting.
        using var input = new InputChannel(AudioEngine.OutputCount);
        var ch = MakeChannel(input);

        var raised = CaptureRaised(ch, () => ch.VolumePercent = 42);

        Assert.Contains(raised, PersistedProperties.Contains);
    }

    [Fact]
    public void EveryAllowlistedName_ExistsOnAViewModel()
    {
        // nameof() protects against typos at compile time, but a renamed property that keeps an old
        // string entry elsewhere would rot silently. Assert each name resolves on some view model.
        var types = new[] { typeof(ChannelViewModel), typeof(RouteToggleViewModel), typeof(OutputViewModel) };
        foreach (var name in PersistedProperties.Names)
        {
            Assert.True(types.Any(t => t.GetProperty(name) != null),
                $"Persisted property '{name}' does not exist on any view model.");
        }
    }
}
