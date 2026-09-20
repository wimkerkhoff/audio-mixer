using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// The figures computed by hand from a log on 2026-09-20, produced continuously instead. Checked
/// against that session's real numbers, because an aggregate that is subtly wrong is worse than none
/// — it is a confident answer nobody re-derives.
/// </summary>
public class SessionAggregatorTests
{
    private const long Sec = 1000;

    private static readonly IReadOnlyList<InputSummary> Seed3 = new[]
    {
        new InputSummary { Index = 0, Label = "LAPEL" },
        new InputSummary { Index = 1, Label = "Rode L" },
        new InputSummary { Index = 2, Label = "Rode R" },
    };
    private static readonly IReadOnlyList<OutputSummary> SeedOut = new[]
    {
        new OutputSummary { Index = 0, Label = "OBS/Zoom" },
        new OutputSummary { Index = 1, Label = "Headset" },
    };

    private static SessionSummary Build(SessionAggregator a) =>
        a.Build("t", new DateTime(2026, 9, 20, 9, 42, 53, DateTimeKind.Utc), "Prayer", Seed3, SeedOut);

    private static float Unity(int i, int o) => 1f;

    [Fact]
    public void TheFirstObservationIsNotAHandoff()
    {
        var a = new SessionAggregator(3, 2);
        a.Tick(Sec, new[] { 0, 0 }, Unity);

        Assert.Equal(0, Build(a).Outputs[0].Handoffs);
    }

    [Fact]
    public void EachChangeOfLeaderCountsOnce()
    {
        var a = new SessionAggregator(3, 2);
        foreach (var w in new[] { 0, 0, 1, 1, 1, 2, 0 }) a.Tick(Sec, new[] { w, w }, Unity);

        Assert.Equal(3, Build(a).Outputs[0].Handoffs);
    }

    /// <summary>The session's headline number: 177 changes over 893 s is 11.9 a minute.</summary>
    [Fact]
    public void HandoffRateIsPerMinuteOfObservedTime()
    {
        var a = new SessionAggregator(3, 2);
        // 120 s of alternating 1 s samples = 119 changes
        for (int i = 0; i < 120; i++) a.Tick(Sec, new[] { i % 2, 0 }, Unity);

        var s = Build(a);
        Assert.Equal(2.0, s.DurationMinutes, 3);
        Assert.Equal(119, s.Outputs[0].Handoffs);
        Assert.Equal(59.5, s.Outputs[0].HandoffsPerMinute, 1);
    }

    /// <summary>"No winner" is a real state with three causes, not missing data.</summary>
    [Fact]
    public void NoWinnerIsTrackedAsItsOwnOccupancy()
    {
        var a = new SessionAggregator(3, 2);
        for (int i = 0; i < 10; i++) a.Tick(Sec, new[] { i < 3 ? -1 : 0, -1 }, Unity);

        var s = Build(a);
        Assert.Equal(30, s.Outputs[0].NoWinnerPercent, 1);
        Assert.Equal(70, s.Outputs[0].OccupancyPercent[0], 1);
        Assert.Equal(100, s.Outputs[1].NoWinnerPercent, 1);
    }

    [Fact]
    public void OccupancySumsToTheWholeSession()
    {
        var a = new SessionAggregator(3, 2);
        foreach (var w in new[] { 0, 1, 2, -1, 0, 1 }) a.Tick(Sec, new[] { w, w }, Unity);

        Assert.Equal(100, Build(a).Outputs[0].OccupancyPercent.Values.Sum(), 4);
    }

    // --- per-input time ------------------------------------------------------------------------

    [Fact]
    public void LeadingOnEitherBusCountsAsLeading()
    {
        var a = new SessionAggregator(3, 2);
        for (int i = 0; i < 4; i++) a.Tick(Sec, new[] { 0, 1 }, Unity);

        var s = Build(a);
        Assert.Equal(100, s.Inputs[0].LeaderPercent, 1);
        Assert.Equal(100, s.Inputs[1].LeaderPercent, 1);
        Assert.Equal(0, s.Inputs[2].LeaderPercent, 1);
    }

    /// <summary>
    /// Gate hard-mutes every non-leader, which is how the room mics spent 62% of that session at
    /// exactly zero. Muted and merely ducked are different states and must not be conflated.
    /// </summary>
    [Fact]
    public void HardMuteAndDuckAreCountedSeparately()
    {
        var a = new SessionAggregator(2, 1);
        for (int i = 0; i < 10; i++)
            a.Tick(Sec, new[] { 0 }, (inp, o) => inp == 0 ? 1f : (i < 6 ? 0f : 0.5f));

        var s = Build(a);
        Assert.Equal(60, s.Inputs[1].MutedByGatePercent, 1);
        Assert.Equal(40, s.Inputs[1].DuckedPercent, 1);
        Assert.Equal(0, s.Inputs[0].MutedByGatePercent, 1);
        Assert.Equal(0, s.Inputs[0].DuckedPercent, 1);
    }

    /// <summary>Muted on one bus and open on another is muted — the worse state wins.</summary>
    [Fact]
    public void MutedOnAnyBusOutranksDuckedOnAnother()
    {
        var a = new SessionAggregator(1, 2);
        a.Tick(Sec, new[] { -1, -1 }, (i, o) => o == 0 ? 0f : 0.5f);

        var s = Build(a);
        Assert.Equal(100, s.Inputs[0].MutedByGatePercent, 1);
        Assert.Equal(0, s.Inputs[0].DuckedPercent, 1);
    }

    [Fact]
    public void UnityGainIsNeitherDuckedNorMuted()
    {
        var a = new SessionAggregator(1, 2);
        for (int i = 0; i < 5; i++) a.Tick(Sec, new[] { 0, 0 }, Unity);

        var s = Build(a);
        Assert.Equal(0, s.Inputs[0].DuckedPercent, 1);
        Assert.Equal(0, s.Inputs[0].MutedByGatePercent, 1);
    }

    // --- events --------------------------------------------------------------------------------

    /// <summary>
    /// A condition, not an instant: a mic 20 dB low is 20 dB low all meeting, and one line says that
    /// better than nine hundred identical ones.
    /// </summary>
    [Fact]
    public void ANoteIsRecordedOncePerKey()
    {
        var a = new SessionAggregator(1, 1);
        for (int i = 0; i < 50; i++)
            a.Note("in0.level", "09:43", "level", AlertSeverity.Warning, "Rode R is quiet");

        Assert.Single(Build(a).Events);
    }

    [Fact]
    public void DifferentKeysAreKeptSeparately()
    {
        var a = new SessionAggregator(1, 1);
        a.Note("in0.level", "09:43", "level", AlertSeverity.Warning, "quiet");
        a.Note("in1.level", "09:44", "level", AlertSeverity.Warning, "quiet");

        Assert.Equal(2, Build(a).Events.Count);
    }

    [Fact]
    public void EventsKeepTheOrderTheyHappenedIn()
    {
        var a = new SessionAggregator(1, 1);
        a.Note("a", "09:43", "level", AlertSeverity.Warning, "first");
        a.Note("b", "10:10", "clipping", AlertSeverity.Critical, "second");

        Assert.Equal(new[] { "first", "second" }, Build(a).Events.Select(e => e.Message));
    }

    // --- robustness ----------------------------------------------------------------------------

    [Fact]
    public void AZeroOrNegativeElapsedIsIgnored()
    {
        var a = new SessionAggregator(1, 1);
        a.Tick(0, new[] { 0 }, Unity);
        a.Tick(-5, new[] { 0 }, Unity);

        Assert.Equal(0, a.ElapsedMs);
    }

    /// <summary>A summary built before anything was observed must not divide by zero.</summary>
    [Fact]
    public void AnEmptySessionSummarisesCleanly()
    {
        var s = Build(new SessionAggregator(3, 2));

        Assert.Equal(0, s.DurationMinutes, 4);
        Assert.Equal(0, s.Outputs[0].HandoffsPerMinute, 4);
        Assert.All(s.Inputs, i => Assert.Equal(0, i.LeaderPercent, 4));
    }

    [Fact]
    public void FewerWinnersThanOutputsDoesNotThrow()
    {
        var a = new SessionAggregator(2, 2);
        a.Tick(Sec, new[] { 0 }, Unity);

        Assert.Equal(1000, a.ElapsedMs);
    }

    /// <summary>The seed carries identity and calibration; the aggregator only fills in the time.</summary>
    [Fact]
    public void TheSeedsLabelsAndLevelsSurvive()
    {
        var a = new SessionAggregator(1, 1);
        a.Tick(Sec, new[] { 0 }, Unity);

        var s = a.Build("t", DateTime.UtcNow, "Prayer",
            new[] { new InputSummary { Index = 0, Label = "LAPEL", SpeechDb = -45.3f, FloorDb = -61.5f } },
            new[] { new OutputSummary { Index = 0, Label = "OBS/Zoom" } });

        Assert.Equal("LAPEL", s.Inputs[0].Label);
        Assert.Equal(-45.3f, s.Inputs[0].SpeechDb, 2);
        Assert.Equal("Prayer", s.Scene);
    }
}
