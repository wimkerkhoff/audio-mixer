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

        var (files, _) = new RecordingRetention(_dir).Prune();

        Assert.Equal(1, files);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void ARecordingJustInsideTheWindowIsKept()
    {
        var path = Wav("mix-A.wav", RecordingRetention.RetentionDays - 1);

        new RecordingRetention(_dir).Prune();

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

        var (files, _) = new RecordingRetention(_dir, second).Prune();

        Assert.Equal(2, files);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    /// <summary>Only audio. A session record is tens of kilobytes and is what you still want when the
    /// audio is gone, so it must never be swept up by an audio retention rule.</summary>
    [Fact]
    public void NonWavFilesAreNeverTouched()
    {
        var json = Path.Combine(_dir, "session-20260920-094253.json");
        File.WriteAllText(json, "{}");
        File.SetLastWriteTimeUtc(json, DateTime.UtcNow.AddDays(-400));

        new RecordingRetention(_dir).Prune();

        Assert.True(File.Exists(json));
    }

    [Fact]
    public void PruningAnEmptyOrMissingFolderIsHarmless()
    {
        Assert.Equal(0, new RecordingRetention(_dir).Prune().Files);
        Assert.Equal(0, new RecordingRetention(Path.Combine(_dir, "nope")).Prune().Files);
        Assert.Equal(0, new RecordingRetention().Prune().Files);
    }

    [Fact]
    public void ALockedFileIsSkippedRatherThanThrowing()
    {
        var path = Wav("diag-input1.wav", 40);
        using var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var (files, _) = new RecordingRetention(_dir).Prune();

        Assert.Equal(0, files);
        Assert.True(File.Exists(path));
    }

    // --- the space rule ---------------------------------------------------------------------------

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
