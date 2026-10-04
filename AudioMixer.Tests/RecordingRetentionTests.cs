using System.IO;
using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// Recording is always-on and unattended, so the only thing standing between it and a full disk is
/// this. At 48 kHz stereo float32 one stream is 1.29 GB/hour; five mics and two buses is ~9 GB/hour,
/// so four weeks at two services a week is ~144 GB — more than the free space on the machine this
/// runs on. Age alone would arrive about a week after the disk filled, which is why the space rule
/// exists and is the one that actually protects the machine.
/// </summary>
public class RecordingRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "AudioMixerRetention", Guid.NewGuid().ToString("N"));

    public RecordingRetentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Plenty of room, so only the age and stub rules act — this machine's disk must not
    /// decide whether a test deletes its files.</summary>
    private static RecordingRetention Roomy(params string[] dirs) => new(dirs) { FreeGbOf = _ => 1000 };

    private string Wav(string name, int daysOld, int bytes = 1024)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }

    [Fact]
    public void RetentionIsFourWeeks() => Assert.Equal(28, RecordingRetention.RetentionDays);

    [Fact]
    public void ExpiredRecordingsAreRemoved()
    {
        var old = Wav("diag-input1-old.wav", RecordingRetention.RetentionDays + 1);
        var fresh = Wav("diag-input1-new.wav", 1);

        var (files, _) = Roomy(_dir).Prune();

        Assert.Equal(1, files);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void ARecordingJustInsideTheWindowIsKept()
    {
        var path = Wav("mix-A.wav", RecordingRetention.RetentionDays - 1);

        Roomy(_dir).Prune();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PruningSpansEveryFolderItWasGiven()
    {
        var second = Path.Combine(_dir, "recordings");
        Directory.CreateDirectory(second);
        var a = Wav("diag-input1.wav", 40);
        var b = Path.Combine(second, "mix-A.wav");
        File.WriteAllBytes(b, new byte[512]);
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddDays(-40));

        var (files, _) = Roomy(_dir, second).Prune();

        Assert.Equal(2, files);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    /// <summary>
    /// A capture kept as a replay fixture must survive. Both golden baselines referenced a stamp 42
    /// days older than the 28-day rule, so their source WAVs were deleted on the first launch after
    /// retention shipped — and those fixtures are the only way to exercise the selector without a
    /// room full of people. `analysis/keep/` is not walked, and ReplayRig searches it.
    /// </summary>
    [Fact]
    public void AFixtureInTheKeepFolderIsNeverPruned()
    {
        var keep = Path.Combine(_dir, RecordingRetention.KeepFolder);
        Directory.CreateDirectory(keep);
        var fixture = Path.Combine(keep, "diag-input1-20260809-092931.wav");
        File.WriteAllBytes(fixture, new byte[2048]);
        File.SetLastWriteTimeUtc(fixture, DateTime.UtcNow.AddDays(-400));
        var ordinary = Wav("diag-input1-20260809-092931.wav", 400);

        var (files, _) = Roomy(_dir).Prune();

        Assert.Equal(1, files);
        Assert.True(File.Exists(fixture), "the kept fixture was pruned");
        Assert.False(File.Exists(ordinary));
    }

    /// <summary>Only audio. A session record is tens of kilobytes and is what you still want when the
    /// audio is gone, so it must never be swept up by an audio retention rule.</summary>
    [Fact]
    public void NonWavFilesAreNeverTouched()
    {
        var json = Path.Combine(_dir, "session-20260920-094253.json");
        File.WriteAllText(json, "{}");
        File.SetLastWriteTimeUtc(json, DateTime.UtcNow.AddDays(-400));

        Roomy(_dir).Prune();

        Assert.True(File.Exists(json));
    }

    [Fact]
    public void PruningAnEmptyOrMissingFolderIsHarmless()
    {
        Assert.Equal(0, Roomy(_dir).Prune().Files);
        Assert.Equal(0, Roomy(Path.Combine(_dir, "nope")).Prune().Files);
        Assert.Equal(0, new RecordingRetention().Prune().Files);
    }

    [Fact]
    public void ALockedFileIsSkippedRatherThanThrowing()
    {
        var path = Wav("diag-input1.wav", 40);
        using var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var (files, _) = Roomy(_dir).Prune();

        Assert.Equal(0, files);
        Assert.True(File.Exists(path));
    }

    // --- per-kind expiry and stubs (operator: "automatic cleanup of what we don't need", 2026-10-04)

    [Fact]
    public void PerMicCapturesExpireBeforeTheMixes()
    {
        int days = RecordingRetention.DiagRetentionDays + 1;
        var diag = Wav("diag-input2-20260901-093000.wav", days, (int)RecordingRetention.StubBytes);
        var mix = Wav("mix-A-20260901-093000.wav", days, (int)RecordingRetention.StubBytes);

        Roomy(_dir).Prune();

        Assert.False(File.Exists(diag));
        Assert.True(File.Exists(mix), "the mix is what was sent, and keeps the full four weeks");
    }

    /// <summary>Every launch starts a recording, so restarts leave stamps of seconds. They go after a
    /// day; a stamp whose mix reached two minutes is a real recording and stays.</summary>
    [Fact]
    public void AStubStampGoesAfterADayAndARealOneStays()
    {
        var stubMix = Wav("mix-A-20261004-092758.wav", 2, 1000);
        var stubDiag = Wav("diag-input1-20261004-092758.wav", 2, 1000);
        var realMix = Wav("mix-A-20261004-092931.wav", 2, (int)RecordingRetention.StubBytes);
        var shortDiagOfReal = Wav("diag-input2-20261004-092931.wav", 2, 1000);
        var todaysStub = Wav("mix-A-20261005-090000.wav", 0, 1000);

        Roomy(_dir).Prune();

        Assert.False(File.Exists(stubMix));
        Assert.False(File.Exists(stubDiag));
        Assert.True(File.Exists(realMix));
        Assert.True(File.Exists(shortDiagOfReal), "a short file of a real recording is part of it");
        Assert.True(File.Exists(todaysStub), "a stub is kept for a day, so a restart mid-service is safe");
    }

    [Fact]
    public void StampIsReadFromAnyRecordingName()
    {
        Assert.Equal("20261004-092931", RecordingRetention.Stamp("diag-input10-20261004-092931.wav"));
        Assert.Equal("20261004-092931", RecordingRetention.Stamp("mix-B-20261004-092931.wav"));
        Assert.Null(RecordingRetention.Stamp("notes.wav"));
    }

    /// <summary>Today's rig: lapel stereo, six split mono, two stereo strips, two stereo buses = 16
    /// channels, which wrote ~10 GB in the hour.</summary>
    [Fact]
    public void TheRateMatchesWhatTheRigWrote() =>
        Assert.InRange(RecordingRetention.GbPerHour(16), 9.5, 11);

    [Fact]
    public void TheSpaceFloorHoldsAThreeHourServiceAtThatRate() =>
        Assert.True(RecordingRetention.LowSpaceGb - RecordingRetention.StopFloorGb
                    >= 3 * RecordingRetention.GbPerHour(16));

    // --- the space rule ---------------------------------------------------------------------------

    [Fact]
    public void WhileSpaceIsShortTheOldestGoFirst()
    {
        var old = Wav("mix-A-20260901-093000.wav", 10, (int)RecordingRetention.StubBytes);
        var newer = Wav("mix-A-20260920-093000.wav", 5, (int)RecordingRetention.StubBytes);
        int calls = 0;
        // Short until one file is gone.
        var r = new RecordingRetention(_dir) { FreeGbOf = _ => calls++ == 0 ? 10 : 1000 };

        r.Prune();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(newer));
    }

    [Fact]
    public void TheFloorsAreOrderedSoAnInFlightRecordingIsGivenEveryChance()
    {
        // Stopping must be harder to trigger than starting, or a session would be cut off the moment
        // free space dipped below the level that merely prevents a new one.
        Assert.True(RecordingRetention.StopFloorGb < RecordingRetention.StartFloorGb);
        Assert.True(RecordingRetention.StartFloorGb < RecordingRetention.LowSpaceGb);
    }

    /// <summary>Unknown free space must never be treated as "no space" — that would block recording
    /// on any path the drive API cannot answer for.</summary>
    [Fact]
    public void AnUnreadablePathReportsRoomRatherThanBlocking()
    {
        Assert.Equal(double.MaxValue, RecordingRetention.FreeGb("\0not a path\0"));
        Assert.True(new RecordingRetention("\0not a path\0").HasRoomToStart());
        Assert.False(new RecordingRetention("\0not a path\0").MustStopNow());
    }

    [Fact]
    public void WithNoFoldersThereIsNothingToGuard()
    {
        Assert.True(new RecordingRetention().HasRoomToStart());
        Assert.False(new RecordingRetention().MustStopNow());
    }

    [Fact]
    public void FreeSpaceOnARealDriveIsReported() =>
        Assert.True(RecordingRetention.FreeGb(Path.GetTempPath()) > 0);
}
