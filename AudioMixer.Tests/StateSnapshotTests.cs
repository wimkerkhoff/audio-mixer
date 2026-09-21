using System.Text.Json;
using AudioMixer.Audio;
using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// `/state` is how every live diagnosis in this project has actually been done — the selector's
/// reasoning read out of a running mixer without a GUI, from another machine, mid-service. Several
/// findings in CLAUDE.md were derived by reading it, and the enum-collapse bug was caught by spotting
/// `mode=2` in it.
///
/// That makes its SHAPE load-bearing in a way an internal DTO's is not: a renamed or dropped key
/// breaks the tooling and the next diagnosis silently, and the only symptom is a field reading null
/// when you need it most.
/// </summary>
public class StateSnapshotTests
{
    private static JsonElement Build(VmFixture f, string status = "Running",
                                     string? scene = null, IReadOnlyList<HealthAlert>? alerts = null)
    {
        var json = StateSnapshot.Build(f.Engine, f.Channels, f.Outputs, f.Channels.Count, status, scene, alerts);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void ItIsValidJsonWithAChannelAndAnOutputPerStrip()
    {
        using var f = new VmFixture();

        var root = Build(f);

        Assert.Equal(f.Channels.Count, root.GetProperty("channels").GetArrayLength());
        Assert.Equal(f.Outputs.Count, root.GetProperty("outputs").GetArrayLength());
        Assert.Equal(f.Channels.Count, root.GetProperty("inputCount").GetInt32());
        Assert.Equal("Running", root.GetProperty("status").GetString());
    }

    /// <summary>The keys the offline tooling and every past diagnosis read by name.</summary>
    [Fact]
    public void EveryChannelKeyTheToolingReadsIsPresent()
    {
        using var f = new VmFixture();
        var ch = Build(f).GetProperty("channels")[0];

        foreach (var key in new[]
                 {
                     "index", "label", "device", "source", "highPassHz", "inputDb", "postDb", "rmsDb",
                     "speechDb", "floorDb", "calBuffers", "envDb", "crest", "fluxCv", "clarity",
                     "routes", "muted", "volumePercent", "isPriority", "isDucking", "isAutoMixActive",
                     "automixGain",
                 })
            Assert.True(ch.TryGetProperty(key, out _), $"channel.{key} is missing from /state");
    }

    [Fact]
    public void EveryOutputKeyTheToolingReadsIsPresent()
    {
        using var f = new VmFixture();
        var op = Build(f).GetProperty("outputs")[0];

        foreach (var key in new[]
                 {
                     "index", "label", "device", "peakDb", "volumePercent", "recording", "mode",
                     "levelerEnabled", "levelerStrength", "levelerGainDb", "levelerIdleFloorDb",
                     "winner", "winnerHold", "activeInput",
                 })
            Assert.True(op.TryGetProperty(key, out _), $"output.{key} is missing from /state");
    }

    /// <summary>
    /// The automix mode is written as its NAME, not its number. Collapsing the enum (Off/Share/Gate to
    /// Off/Gate) silently changed what `2` meant in every stored preset; a numeric mode in the
    /// diagnostic feed would have had the same problem, and reading `mode` is how that bug was found.
    /// </summary>
    [Fact]
    public void TheAutomixModeIsReportedByNameNotNumber()
    {
        using var f = new VmFixture();
        var mode = Build(f).GetProperty("outputs")[0].GetProperty("mode");

        Assert.Equal(JsonValueKind.String, mode.ValueKind);
        Assert.Contains(mode.GetString(), new[] { "Off", "Gate" });
    }

    /// <summary>
    /// `winner = -1` has three distinct causes — automix Off, a priority duck, and a silent room — and
    /// the gains are what tell them apart: a duck writes 0 where a silent room writes 1.0. So the
    /// snapshot has to carry the per-bus gains alongside the winner, or the ambiguity is unresolvable
    /// after the fact.
    /// </summary>
    [Fact]
    public void TheGainsThatDisambiguateNoWinnerAreCarriedAlongsideIt()
    {
        using var f = new VmFixture();
        var root = Build(f);

        Assert.Equal(-1, root.GetProperty("outputs")[0].GetProperty("winner").GetInt32());
        var gains = root.GetProperty("channels")[0].GetProperty("automixGain");
        Assert.Equal(AudioEngine.OutputCount, gains.GetArrayLength());
    }

    /// <summary>A mic with no voiced audio yet has no median, and that must read as null rather than as
    /// a plausible-looking number — an invented speechDb would send the operator the wrong way on
    /// transmitter gain, which is the one thing it is read for.</summary>
    [Fact]
    public void AnUncalibratedMicReportsNullRatherThanAFakeNumber()
    {
        using var f = new VmFixture();
        var ch = Build(f).GetProperty("channels")[0];

        Assert.Equal(JsonValueKind.Null, ch.GetProperty("speechDb").ValueKind);
        Assert.Equal(JsonValueKind.Null, ch.GetProperty("floorDb").ValueKind);
        Assert.Equal(0, ch.GetProperty("calBuffers").GetInt32());
    }

    [Fact]
    public void RoutesAndDeviceNamesFollowTheViewModel()
    {
        using var f = new VmFixture();
        f.Channels[0].CustomLabel = "LAPEL";
        f.Channels[0].SelectedDevice = VmFixture.Lapel;
        f.Channels[0].Routes[0].IsOn = true;
        f.Channels[0].Routes[1].IsOn = false;
        f.Channels[0].IsPriority = true;

        var ch = Build(f).GetProperty("channels")[0];

        Assert.Equal("LAPEL", ch.GetProperty("label").GetString());
        Assert.Equal("Wireless PRO RX", ch.GetProperty("device").GetString());
        Assert.True(ch.GetProperty("isPriority").GetBoolean());
        Assert.Equal(new[] { true, false },
                     ch.GetProperty("routes").EnumerateArray().Select(r => r.GetBoolean()));
    }

    [Fact]
    public void TheSceneAndAlertsAreCarriedSoAStateDumpExplainsItself()
    {
        using var f = new VmFixture();
        var alerts = new[] { new HealthAlert("level.low", AlertSeverity.Warning, "Input 1 is 20 dB low") };

        var root = Build(f, scene: "Prayer", alerts: alerts);

        Assert.Equal("Prayer", root.GetProperty("scene").GetString());
        var a = root.GetProperty("alerts")[0];
        Assert.Equal("level.low", a.GetProperty("Id").GetString());
        Assert.Equal("Warning", a.GetProperty("severity").GetString());
    }

    /// <summary>Not replaying, so the block is null — the presence of the key is what lets a baseline
    /// runner tell a replay dump from a live one.</summary>
    [Fact]
    public void ALiveMixerReportsNoReplayBlock()
    {
        using var f = new VmFixture();
        var root = Build(f);

        Assert.True(root.TryGetProperty("replay", out var replay));
        Assert.Equal(JsonValueKind.Null, replay.ValueKind);
    }

    /// <summary>Silence is -120, not -Infinity: JSON has no infinity, so an unguarded log10(0) would
    /// produce a document nothing can parse.</summary>
    [Fact]
    public void ASilentChannelReportsAFiniteFloorRatherThanInfinity()
    {
        using var f = new VmFixture();
        var ch = Build(f).GetProperty("channels")[0];

        Assert.Equal(-120.0, ch.GetProperty("rmsDb").GetDouble());
    }

    /// <summary>Fewer strips than the engine has channels is the normal case (the engine is sized for
    /// the maximum), so indexing has to be driven by the view models.</summary>
    [Fact]
    public void FewerStripsThanTheEngineHasChannelsIsFine()
    {
        using var f = new VmFixture(inputs: 1);

        var root = Build(f);

        Assert.Equal(1, root.GetProperty("channels").GetArrayLength());
    }
}
