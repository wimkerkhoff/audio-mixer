using System.IO;
using AudioMixer.Models;
using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// The rig's configuration on disk — every strip's device, routing, level and role. Lose it and an
/// operator remaps the whole rig by hand before a service, so this is the highest-value file the app
/// writes.
///
/// Until 2026-09-21 the path was hardcoded to %APPDATA%, which is why there were no tests: writing
/// one meant clobbering the operator's real preset.
/// </summary>
public class PresetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "AudioMixerPreset", Guid.NewGuid().ToString("N"));

    private readonly string _path;

    public PresetStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "preset.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private PresetStore Store() => new(_path);

    private static MixerPreset Preset(string label = "LAPEL") => new()
    {
        Name = "Default",
        LowCutHz = 80,
        HideVirtualInputs = true,
        Channels = new[]
        {
            new ChannelPreset
            {
                CustomLabel = label,
                DeviceId = "dev-lapel",
                DeviceName = "Wireless PRO RX",
                VolumePercent = 100,
                Priority = true,
                Routes = new[] { true, false },
                HighPassHz = 80,
            },
        },
        Outputs = new[] { new OutputPreset { CustomLabel = "OBS/Zoom", DeviceId = "dev-cable" } },
    };

    // --- the round trip -----------------------------------------------------------------------------

    [Fact]
    public void APresetSurvivesASaveAndLoad()
    {
        Store().Save(Preset());

        var loaded = Store().Load();

        Assert.NotNull(loaded);
        Assert.Equal(80, loaded!.LowCutHz);
        Assert.True(loaded.HideVirtualInputs);
        Assert.Equal("LAPEL", loaded.Channels[0].CustomLabel);
        Assert.Equal("dev-lapel", loaded.Channels[0].DeviceId);
        Assert.Equal("Wireless PRO RX", loaded.Channels[0].DeviceName);
        Assert.True(loaded.Channels[0].Priority);
        Assert.Equal(new[] { true, false }, loaded.Channels[0].Routes);
        Assert.Equal("dev-cable", loaded.Outputs[0].DeviceId);
    }

    /// <summary>No preset yet is the first-launch case, and must read as "nothing saved", not an error.</summary>
    [Fact]
    public void NoPresetFileReadsAsNothingSaved()
    {
        Assert.Null(Store().Load());
    }

    [Fact]
    public void SavingTwiceLeavesTheSecondValue()
    {
        Store().Save(Preset("first"));
        Store().Save(Preset("second"));

        Assert.Equal("second", Store().Load()!.Channels[0].CustomLabel);
    }

    [Fact]
    public void SavingCreatesTheFolderIfItIsMissing()
    {
        var nested = Path.Combine(_dir, "deeper", "preset.json");

        new PresetStore(nested).Save(Preset());

        Assert.True(File.Exists(nested));
    }

    // --- surviving a kill ---------------------------------------------------------------------------

    /// <summary>
    /// The write goes to a temp file and replaces, because WriteAllText truncates the existing file
    /// before writing a byte — so a kill during a save destroyed the rig's configuration outright.
    /// This app is force-killed often enough for that to matter; it is the same reason session records
    /// checkpoint rather than only writing on exit.
    /// </summary>
    [Fact]
    public void TheSaveLeavesNoTemporaryFileBehind()
    {
        Store().Save(Preset());

        Assert.Equal(new[] { "preset.json" },
                     Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void TheOutgoingPresetIsKeptAsABackup()
    {
        var store = Store();
        store.Save(Preset("first"));
        store.Save(Preset("second"));

        Assert.True(File.Exists(store.BackupPath));
        Assert.Contains("first", File.ReadAllText(store.BackupPath));
    }

    /// <summary>
    /// The failure this guards: a torn preset is indistinguishable from never having saved one — every
    /// channel comes up with no device, minutes before a service. One save stale is a far better
    /// outcome than remapping the rig by hand.
    /// </summary>
    [Fact]
    public void ATornPresetFallsBackToTheBackupRatherThanComingUpUnconfigured()
    {
        var store = Store();
        store.Save(Preset("good"));
        store.Save(Preset("newer"));

        File.WriteAllText(_path, "{\"Channels\": [{\"CustomLa");   // killed mid-write

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("good", loaded!.Channels[0].CustomLabel);
    }

    /// <summary>An empty file is what a truncate-then-die actually leaves, and it is not valid JSON.</summary>
    [Fact]
    public void AnEmptyPresetFileFallsBackToTheBackup()
    {
        var store = Store();
        store.Save(Preset("good"));
        store.Save(Preset("newer"));
        File.WriteAllText(_path, "");

        Assert.Equal("good", store.Load()!.Channels[0].CustomLabel);
    }

    /// <summary>With no backup to fall back on there is nothing to recover, and it must still not throw
    /// — Load is called during startup, before there is any UI to report an error on.</summary>
    [Fact]
    public void ATornPresetWithNoBackupReadsAsNothingSavedRatherThanThrowing()
    {
        File.WriteAllText(_path, "not json at all");

        Assert.Null(Store().Load());
    }

    [Fact]
    public void ABackupThatIsAlsoCorruptDoesNotThrow()
    {
        var store = Store();
        store.Save(Preset("good"));
        store.Save(Preset("newer"));
        File.WriteAllText(_path, "{{{");
        File.WriteAllText(store.BackupPath, "}}}");

        Assert.Null(store.Load());
    }

    /// <summary>A preset written before a field existed must still load — an operator's preset outlives
    /// several versions of the app, and a failure here comes up as an unconfigured mixer.</summary>
    [Fact]
    public void APresetMissingNewerFieldsStillLoads()
    {
        File.WriteAllText(_path, """
            { "Name": "Default", "Channels": [ { "CustomLabel": "LAPEL" } ] }
            """);

        var loaded = Store().Load();

        Assert.NotNull(loaded);
        Assert.Equal("LAPEL", loaded!.Channels[0].CustomLabel);
    }

    /// <summary>An unknown field is what a preset written by a NEWER build looks like to an older one;
    /// it must be ignored rather than rejected.</summary>
    [Fact]
    public void APresetWithUnknownFieldsStillLoads()
    {
        File.WriteAllText(_path, """
            { "Name": "Default", "SomethingFromTheFuture": 42, "Channels": [] }
            """);

        Assert.NotNull(Store().Load());
    }

    [Fact]
    public void TheDefaultPathIsUnderAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioMixer");

        Assert.StartsWith(expected, new PresetStore().PresetPath);
        Assert.EndsWith("preset.json", new PresetStore().PresetPath);
    }
}
