using AudioMixer.Audio;
using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// Everything the app remembers passes through here on its way to disk, and a field that quietly
/// stops being written reads as its default next launch with nothing in the log.
///
/// The device fallback is the part that has already cost real operator time. A hot-plug receiver gets
/// a new WASAPI GUID on every replug, so a preset re-binds by friendly NAME — but unplugging nulled
/// SelectedDevice and the 500 ms autosave then wrote DeviceId=null DeviceName=null, deleting the only
/// thing the name match could have worked from. `ReplugIdentityTests` pins the resolution half; this
/// is the half that has to keep the name alive long enough to resolve against.
/// </summary>
public class PresetMapperTests
{
    private static PresetMapper.AppOptions Options => new(
        VbCablePromptDismissed: true,
        HideVirtualInputs: true,
        HideVoicemeeterOutputs: false,
        WarnOnBluetoothMics: true,
        LowCutHz: 80);

    // --- the device fallback ------------------------------------------------------------------------

    /// <summary>The whole point: a strip whose receiver has been unplugged still writes its identity.</summary>
    [Fact]
    public void AStripWhoseDeviceHasVanishedStillWritesTheDeviceItWants()
    {
        using var f = new VmFixture();
        f.Channels[0].SelectedDevice = VmFixture.Lapel;
        f.Channels[0].SelectedDevice = null;          // the receiver is unplugged

        var ch = PresetMapper.FromViewModels(f.Channels, f.Outputs, Options).Channels[0];

        Assert.Equal("dev-lapel", ch.DeviceId);
        Assert.Equal("Wireless PRO RX", ch.DeviceName);
    }

    /// <summary>An explicit "Clear device" is the operator saying so, and must actually forget.</summary>
    [Fact]
    public void ClearingTheDeviceOnPurposeDoesForgetIt()
    {
        using var f = new VmFixture();
        f.Channels[0].SelectedDevice = VmFixture.Lapel;
        f.Channels[0].SelectedDevice = null;
        f.Channels[0].ClearDesiredDevice();

        var ch = PresetMapper.FromViewModels(f.Channels, f.Outputs, Options).Channels[0];

        Assert.Null(ch.DeviceId);
        Assert.Null(ch.DeviceName);
    }

    /// <summary>A live device always wins over the remembered one, or a strip re-pointed at a different
    /// receiver would keep saving the old one.</summary>
    [Fact]
    public void ALiveDeviceOutranksTheRememberedOne()
    {
        using var f = new VmFixture();
        f.Channels[0].SelectedDevice = VmFixture.Lapel;
        f.Channels[0].SelectedDevice = VmFixture.Cable;

        var ch = PresetMapper.FromViewModels(f.Channels, f.Outputs, Options).Channels[0];

        Assert.Equal("dev-cable", ch.DeviceId);
        Assert.Equal(VmFixture.Cable.FriendlyName, ch.DeviceName);
    }

    [Fact]
    public void AStripThatNeverHadADeviceWritesNulls()
    {
        using var f = new VmFixture();

        var ch = PresetMapper.FromViewModels(f.Channels, f.Outputs, Options).Channels[0];

        Assert.Null(ch.DeviceId);
        Assert.Null(ch.DeviceName);
    }

    // --- everything else that has to survive a restart ------------------------------------------------

    [Fact]
    public void PerChannelStateIsCarriedAcross()
    {
        using var f = new VmFixture();
        var vm = f.Channels[1];
        vm.CustomLabel = "Steve";
        vm.VolumePercent = 80;
        vm.Muted = true;
        vm.IsPriority = true;
        vm.Source = ChannelSource.Right;
        vm.HighPassHz = 100;
        vm.Routes[0].IsOn = true;
        vm.Routes[1].IsOn = false;

        var ch = PresetMapper.FromViewModels(f.Channels, f.Outputs, Options).Channels[1];

        Assert.Equal("Steve", ch.CustomLabel);
        Assert.Equal(80, ch.VolumePercent);
        Assert.True(ch.Muted);
        Assert.True(ch.Priority);
        Assert.Equal((int)ChannelSource.Right, ch.Source);
        Assert.Equal(100, ch.HighPassHz);
        Assert.Equal(new[] { true, false }, ch.Routes);
    }

    [Fact]
    public void PerOutputStateIsCarriedAcross()
    {
        using var f = new VmFixture();
        var vm = f.Outputs[0];
        vm.CustomLabel = "OBS/Zoom";
        vm.SelectedDevice = VmFixture.Cable;
        vm.VolumePercent = 90;
        vm.LevelerEnabled = true;
        vm.LevelerThresholdDb = -20;
        vm.LevelerRatio = 3;
        vm.LevelerMaxGainDb = 9;
        vm.LevelerIdleFloorDb = -42;
        vm.LimiterCeilingDb = -1;

        var op = PresetMapper.FromViewModels(f.Channels, f.Outputs, Options).Outputs[0];

        Assert.Equal("OBS/Zoom", op.CustomLabel);
        Assert.Equal("dev-cable", op.DeviceId);
        Assert.Equal(90, op.Volume);
        Assert.True(op.LevelerEnabled);
        Assert.Equal(-20, op.LevelerThresholdDb);
        Assert.Equal(3, op.LevelerRatio);
        Assert.Equal(9, op.LevelerMaxGainDb);
        Assert.Equal(-42, op.LevelerIdleFloorDb);
        Assert.Equal(-1, op.LimiterCeilingDb);
    }

    [Fact]
    public void AppOptionsAreCarriedAcross()
    {
        using var f = new VmFixture();

        var p = PresetMapper.FromViewModels(f.Channels, f.Outputs, Options);

        Assert.True(p.VbCablePromptDismissed);
        Assert.True(p.HideVirtualInputs);
        Assert.False(p.HideVoicemeeterOutputs);
        Assert.True(p.WarnOnBluetoothMics);
        Assert.Equal(80, p.LowCutHz);
    }

    /// <summary>One entry per strip, in order — the preset is applied back by index, so a reordering
    /// or a dropped entry would silently move a mic to a different strip.</summary>
    [Fact]
    public void EveryStripAndBusGetsExactlyOneEntryInOrder()
    {
        using var f = new VmFixture();
        for (int i = 0; i < f.Channels.Count; i++) f.Channels[i].CustomLabel = $"mic{i}";

        var p = PresetMapper.FromViewModels(f.Channels, f.Outputs, Options);

        Assert.Equal(f.Channels.Count, p.Channels.Length);
        Assert.Equal(f.Outputs.Count, p.Outputs.Length);
        Assert.Equal(new[] { "mic0", "mic1", "mic2" }, p.Channels.Select(c => c.CustomLabel));
    }

    /// <summary>The autosave allowlist exists to mirror this mapper. If a property is persisted here
    /// but missing from the allowlist, changing it never triggers a save — the failure that loses a
    /// whole session's settings to a kill rather than a clean exit.</summary>
    [Fact]
    public void EveryChannelFieldWrittenHereHasAnAllowlistEntry()
    {
        foreach (var name in new[]
                 {
                     nameof(ViewModels.ChannelViewModel.CustomLabel),
                     nameof(ViewModels.ChannelViewModel.VolumePercent),
                     nameof(ViewModels.ChannelViewModel.Muted),
                     nameof(ViewModels.ChannelViewModel.IsPriority),
                     nameof(ViewModels.ChannelViewModel.Source),
                     nameof(ViewModels.ChannelViewModel.HighPassHz),
                 })
            Assert.True(PersistedProperties.Contains(name), $"{name} is saved but would not trigger a save");
    }
}
