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

    private sealed class StubAutoMix : IAutoMixControl
    {
        public void SetAutoMixMode(int output, AutoMixMode mode) { }
    }

    private static OutputViewModel MakeOutput(OutputBus bus) =>
        new(0, bus, new StubAutoMix(), Array.Empty<AudioDeviceInfo>(), (_, _) => { });

    [Fact]
    public void OutputMeterTick_RaisesNothingThatTriggersAutosave()
    {
        // The twin of the channel test above, which did NOT exist — so the same silent-autosave bug
        // was reachable through any output display property (the leveler's gain readout is one).
        using var bus = new OutputBus();
        var op = MakeOutput(bus);

        var raised = CaptureRaised(op, op.RefreshMeters);

        Assert.NotEmpty(raised);
        var offenders = raised.Where(PersistedProperties.Contains).Distinct().ToList();
        Assert.True(offenders.Count == 0,
            $"OutputViewModel.RefreshMeters raises persisted propert{(offenders.Count == 1 ? "y" : "ies")} " +
            $"[{string.Join(", ", offenders)}] — this resets the autosave debounce 30x/second and " +
            "autosave will never fire. Either stop raising it on the meter tick or stop persisting it.");
    }

    [Fact]
    public void ChangingALevelerSetting_IsSeenByTheAllowlist()
    {
        // The inverse failure: a setting that never triggers autosave is silently lost on a crash.
        using var bus = new OutputBus();
        var op = MakeOutput(bus);

        var raised = CaptureRaised(op, () => op.LevelerThresholdDb = -30f);
        Assert.Contains(raised, PersistedProperties.Contains);
    }

    /// <summary>
    /// IsOn is persisted, so the per-route refresh on the meter tick must not raise it. Raising a
    /// persisted name at 30 Hz restarts the 500 ms autosave debounce every 33 ms, so it can never
    /// elapse and the whole session's settings are lost to a kill instead of a clean exit.
    /// </summary>
    [Fact]
    public void RouteSelectionRefresh_DoesNotRaiseTheRouteToggleItself()
    {
        using var input = new InputChannel(AudioEngine.OutputCount);
        var route = new RouteToggleViewModel(0, input);

        var raised = CaptureRaised(route, route.RefreshSelection);

        Assert.Contains(nameof(RouteToggleViewModel.IsSelected), raised);
        Assert.DoesNotContain(nameof(RouteToggleViewModel.IsOn), raised);
    }

    /// <summary>
    /// The whole-tick version of the same invariant, which is the one that actually matters: whatever
    /// RefreshMeters comes to raise in future, none of it may be persisted. This is stronger than
    /// checking one method, and it is the test that would have caught the original bug directly.
    /// </summary>
    [Fact]
    public void TheMeterTickRaisesNothingThatIsPersisted()
    {
        using var f = new VmFixture(inputs: 1);
        var ch = f.Channels[0];

        var raised = new List<string>();
        ch.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
        foreach (var r in ch.Routes) r.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        ch.RefreshMeters();

        Assert.NotEmpty(raised);
        foreach (var name in raised)
            Assert.False(PersistedProperties.Contains(name),
                         $"RefreshMeters raises '{name}', which is persisted — autosave would never elapse");
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
