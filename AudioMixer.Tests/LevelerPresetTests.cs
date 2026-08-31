using System.Text.Json;
using AudioMixer.Models;
using Xunit;

namespace AudioMixer.Tests;

/// <summary>
/// The leveler is per-output configuration that an operator tunes once and expects to find again.
/// Persistence is the half of that which fails silently: a dropped field just reads as "off" next
/// launch, with nothing in the log.
/// </summary>
public class LevelerPresetTests
{
    [Fact]
    public void LevelerSettings_SurviveAJsonRoundTrip()
    {
        var preset = new MixerPreset
        {
            Outputs = new OutputPreset[]
            {
                new()
                {
                    LevelerEnabled = true,
                    LevelerStrength = 2,
                    LevelerThresholdDb = -22f,
                    LevelerRatio = 4f,
                    LevelerAttackMs = 120f,
                    LevelerReleaseMs = 2500f,
                    LevelerMaxGainDb = 8f,
                    LevelerIdleFloorDb = -38f,
                    LimiterCeilingDb = -2f,
                },
            },
        };

        var back = JsonSerializer.Deserialize<MixerPreset>(JsonSerializer.Serialize(preset))!;
        var o = back.Outputs[0];

        Assert.True(o.LevelerEnabled);
        Assert.Equal(2, o.LevelerStrength);
        Assert.Equal(-22f, o.LevelerThresholdDb);
        Assert.Equal(4f, o.LevelerRatio);
        Assert.Equal(120f, o.LevelerAttackMs);
        Assert.Equal(2500f, o.LevelerReleaseMs);
        Assert.Equal(8f, o.LevelerMaxGainDb);
        Assert.Equal(-38f, o.LevelerIdleFloorDb);
        Assert.Equal(-2f, o.LimiterCeilingDb);
    }

    [Fact]
    public void APresetWrittenBeforeTheLevelerExisted_LoadsWithItOffAndSaneDefaults()
    {
        // The migration guarantee. System.Text.Json leaves property initialisers alone when a key is
        // absent — but only if the defaults are initialisers and not constructor logic, so this is
        // worth pinning: the failure mode is an old preset loading with a 0 dB threshold and the
        // leveler silently engaged on a live bus.
        const string oldJson = """
        {
          "Outputs": [
            { "CustomLabel": "A — Headset", "AutoMixMode": 2, "AutoMixStrength": 100, "Volume": 100 }
          ]
        }
        """;

        var preset = JsonSerializer.Deserialize<MixerPreset>(oldJson)!;
        var o = preset.Outputs[0];

        Assert.False(o.LevelerEnabled);          // never engage on a preset that never chose to
        Assert.Equal(1, o.LevelerStrength);      // Medium
        Assert.Equal(-26f, o.LevelerThresholdDb);
        Assert.Equal(3f, o.LevelerRatio);
        Assert.Equal(10f, o.LevelerMaxGainDb);
        Assert.Equal(-45f, o.LevelerIdleFloorDb);
        Assert.Equal(-1f, o.LimiterCeilingDb);
        // and the pre-existing fields still load
        Assert.Equal(2, o.AutoMixMode);
        Assert.Equal("A — Headset", o.CustomLabel);
    }
}
