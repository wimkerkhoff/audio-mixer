using System.IO;
using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// Session records are written on every service whether or not audio recording was armed, so this
/// runs unattended for years. What it must never do is throw: a record that takes down the mixer it
/// is describing is a far worse outcome than a lost record.
/// </summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "AudioMixerTests", Guid.NewGuid().ToString("N"));

    private SessionStore Store() => new(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static SessionSummary Summary(string stamp) => new()
    {
        Stamp = stamp,
        StartedUtc = new DateTime(2026, 9, 20, 9, 42, 53, DateTimeKind.Utc),
        DurationMinutes = 28.8,
        Scene = "Prayer",
        Inputs = new[]
        {
            new InputSummary { Index = 0, Label = "LAPEL", SpeechDb = -45.3f, FloorDb = -61.5f,
                LeaderPercent = 28.3, MutedByGatePercent = 23.4, ClippedSamples = 0 },
            new InputSummary { Index = 1, Label = "Rode L", SpeechDb = -46.1f, FloorDb = -65.4f,
                LeaderPercent = 21.4, MutedByGatePercent = 61.6, ClippedSamples = 180 },
        },
        Outputs = new[]
        {
            new OutputSummary { Index = 0, Label = "OBS/Zoom", Handoffs = 177, HandoffsPerMinute = 11.9,
                NoWinnerPercent = 28.2,
                OccupancyPercent = new Dictionary<int, double> { [-1] = 28.2, [0] = 28.3, [1] = 21.4 } },
        },
        Events = new[]
        {
            new SessionEvent("09:43", "level", AlertSeverity.Critical, "All three mics below target"),
        },
    };

    [Fact]
    public void ASavedSessionRoundTrips()
    {
        var store = Store();
        var path = store.Save(Summary("20260920-094253"));

        Assert.NotNull(path);
        var back = store.Load(path!);

        Assert.NotNull(back);
        Assert.Equal(28.8, back!.DurationMinutes, 3);
        Assert.Equal("Prayer", back.Scene);
        Assert.Equal(-45.3f, back.Inputs[0].SpeechDb, 2);
        Assert.Equal(177, back.Outputs[0].Handoffs);
        Assert.Equal(28.2, back.Outputs[0].OccupancyPercent[-1], 2);
        Assert.Equal(180, back.Inputs[1].ClippedSamples);
        Assert.Equal("All three mics below target", back.Events[0].Message);
        Assert.Equal(AlertSeverity.Critical, back.Events[0].Severity);
    }

    /// <summary>The whole point of keeping them: they are tiny next to the audio.</summary>
    [Fact]
    public void ARecordIsSmallEnoughToKeepForever()
    {
        var store = Store();
        store.Save(Summary("20260920-094253"));

        Assert.True(store.List()[0].Bytes < 16 * 1024,
            $"a session record grew to {store.List()[0].Bytes} bytes");
    }

    [Fact]
    public void SessionsAreListedNewestFirst()
    {
        var store = Store();
        store.Save(Summary("20260920-090000"));
        store.Save(Summary("20260920-100000"));

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.True(list[0].WrittenUtc >= list[1].WrittenUtc);
    }

    [Fact]
    public void PruningKeepsRecentAndRemovesOld()
    {
        var store = Store();
        store.Save(Summary("20260920-094253"));
        var path = store.PathFor("20260920-094253");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-(SessionStore.RetentionDays + 1)));

        var fresh = store.Save(Summary("20260921-094253"));

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(fresh!));
    }

    [Fact]
    public void ARecordExactlyAtTheBoundaryIsKept()
    {
        var store = Store();
        store.Save(Summary("20260920-094253"));
        File.SetLastWriteTimeUtc(store.PathFor("20260920-094253"),
            DateTime.UtcNow.AddDays(-(SessionStore.RetentionDays - 1)));

        store.Prune();

        Assert.True(File.Exists(store.PathFor("20260920-094253")));
    }

    [Fact]
    public void RetentionIsNinetyDays() => Assert.Equal(90, SessionStore.RetentionDays);

    // --- never throw ---------------------------------------------------------------------------

    [Fact]
    public void ListingAMissingDirectoryIsEmptyRatherThanAnError() =>
        Assert.Empty(new SessionStore(Path.Combine(_dir, "never", "made")).List());

    [Fact]
    public void LoadingRubbishReturnsNullRatherThanThrowing()
    {
        var store = Store();
        Directory.CreateDirectory(_dir);
        var path = store.PathFor("broken");
        File.WriteAllText(path, "{ this is not json");

        Assert.Null(store.Load(path));
    }

    [Fact]
    public void SavingSomewhereImpossibleReturnsNullRatherThanThrowing()
    {
        // A path under a file, which can never be a directory.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");

        Assert.Null(new SessionStore(Path.Combine(blocker, "sessions")).Save(Summary("x")));
    }

    [Fact]
    public void PruningAMissingDirectoryIsHarmless() =>
        Assert.Equal(0, new SessionStore(Path.Combine(_dir, "nope")).Prune());

    [Fact]
    public void TheStampIsSortableAndFileSafe()
    {
        var stamp = SessionStore.StampFor(new DateTime(2026, 9, 20, 9, 42, 53));

        Assert.Equal("20260920-094253", stamp);
        Assert.DoesNotContain(stamp, c => Path.GetInvalidFileNameChars().Contains(c));
    }
}
