using System.Text.Json;
using AudioMixer.Audio;
using AudioMixer.Models;

namespace AudioMixer.Tests;

/// <summary>
/// The Settings-window options were runtime-only until 2026-09-20, so an operator re-ticked the
/// picker filters on every launch. Like the leveler, this is the half that fails quietly: a dropped
/// field just reads as its default next launch with nothing in the log.
/// </summary>
public class AppOptionsPresetTests
{
    [Fact]
    public void SettingsOptions_SurviveAJsonRoundTrip()
    {
        var preset = new MixerPreset
        {
            VbCablePromptDismissed = true,
            HideVirtualInputs = true,
            HideVoicemeeterOutputs = true,
            WarnOnBluetoothMics = false,
        };

        var back = JsonSerializer.Deserialize<MixerPreset>(JsonSerializer.Serialize(preset))!;

        Assert.True(back.VbCablePromptDismissed);
        Assert.True(back.HideVirtualInputs);
        Assert.True(back.HideVoicemeeterOutputs);
        Assert.False(back.WarnOnBluetoothMics);
    }

    /// <summary>
    /// A preset written before these fields existed must not silently turn the Bluetooth warning off:
    /// absent means "never chose", and the safe default for a warning is on. The two picker filters
    /// default off, so an old preset keeps showing every device — visible, not silent.
    /// </summary>
    [Fact]
    public void AnOlderPresetGetsTheSafeDefaults()
    {
        const string json = """{"Name":"Default","Channels":[],"Outputs":[],"VbCablePromptDismissed":true}""";

        var back = JsonSerializer.Deserialize<MixerPreset>(json)!;

        Assert.True(back.WarnOnBluetoothMics);
        Assert.False(back.HideVirtualInputs);
        Assert.False(back.HideVoicemeeterOutputs);
    }

    /// <summary>
    /// Share was removed 2026-09-20. The enum was Off=0, Share=1, Gate=2, so every preset written
    /// before that stores a 2 for Gate -- an out-of-range value against the new Off=0, Gate=1. A
    /// preset that silently loads as an invalid mode is the worst kind of migration bug: the mixer
    /// still runs, and only the behaviour is wrong.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]   // Off stays Off
    [InlineData(1, 1)]   // old Share -> Gate
    [InlineData(2, 1)]   // old Gate  -> Gate
    [InlineData(9, 1)]   // anything else is a live bus, so Gate
    public void OldAutoMixModesMigrateToTheCollapsedEnum(int stored, int expected)
    {
        Assert.Equal((AutoMixMode)expected, OutputPreset.MigrateMode(stored));
    }

    [Fact]
    public void TheLowCutIsGlobalAndDefaultsTo80()
    {
        Assert.Equal(80, new MixerPreset().LowCutHz);

        var back = JsonSerializer.Deserialize<MixerPreset>(
            JsonSerializer.Serialize(new MixerPreset { LowCutHz = 100 }))!;
        Assert.Equal(100, back.LowCutHz);
    }
}
