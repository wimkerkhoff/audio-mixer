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

    // --- operator actions -------------------------------------------------------------------------
    //
    // Step 8 of the session-review skill is entirely this list: for each change, did it do what the
    // operator intended, did it help the stream, and should the app have handled it itself. An empty
    // list on a good session is the target, so the collapse rule below is load-bearing in both
    // directions — under-collapse and a dragged slider buries the three changes that mattered;
    // over-collapse and a genuine revert disappears.

    [Fact]
    public void AnActionIsRecordedWithItsTimeOfDay()
    {
        var a = new SessionAggregator(3, 2);
        a.Action("09:51:04", "Muted LAPEL");

        var s = Build(a);

        Assert.Single(s.Actions);
        Assert.Equal("09:51:04", s.Actions[0].TimeOfDay);
        Assert.Equal("Muted LAPEL", s.Actions[0].What);
    }

    /// <summary>A dragged level slider raises one change per step; fifty "level 80%" say nothing one
    /// does not, and they would bury the actions that carry information.</summary>
    [Fact]
    public void ConsecutiveIdenticalActionsCollapseToOne()
    {
        var a = new SessionAggregator(3, 2);
        for (int i = 0; i < 50; i++) a.Action("09:51:04", "Rode L level 80%");

        Assert.Single(Build(a).Actions);
    }

    /// <summary>Only CONSECUTIVE ones collapse — a setting changed, changed away and changed back is a
    /// revert, which is exactly the "the operator was hunting" signal the review looks for.</summary>
    [Fact]
    public void AnActionRepeatedAfterADifferentOneIsKept()
    {
        var a = new SessionAggregator(3, 2);
        a.Action("09:51:04", "Muted LAPEL");
        a.Action("09:52:10", "Unmuted LAPEL");
        a.Action("09:53:31", "Muted LAPEL");

        var actions = Build(a).Actions;

        Assert.Equal(3, actions.Count);
        Assert.Equal("Muted LAPEL", actions[2].What);
        Assert.Equal("09:53:31", actions[2].TimeOfDay);
    }

    [Fact]
    public void ActionsKeepTheOrderTheyHappenedIn()
    {
        var a = new SessionAggregator(3, 2);
        a.Action("09:51:04", "Scene: Prayer");
        a.Action("09:58:12", "Rode R -> bus B off");
        a.Action("10:04:00", "Reset calibration");

        var actions = Build(a).Actions;

        Assert.Equal(new[] { "Scene: Prayer", "Rode R -> bus B off", "Reset calibration" },
                     actions.Select(x => x.What));
    }

    [Fact]
    public void ABlankActionIsNotRecorded()
    {
        var a = new SessionAggregator(3, 2);
        a.Action("09:51:04", "");
        a.Action("09:51:05", "   ");
        a.Action("09:51:06", null!);

        Assert.Empty(Build(a).Actions);
    }

    /// <summary>Past the cap the session was hand-flown and the exact count has stopped being the
    /// interesting part — but it must stop growing rather than grow unbounded for hours.</summary>
    [Fact]
    public void TheActionLogIsCappedAndKeepsTheEarliest()
    {
        var a = new SessionAggregator(3, 2);
        for (int i = 0; i < SessionAggregator.MaxActions + 200; i++)
            a.Action("09:51:04", $"change {i}");

        var actions = Build(a).Actions;

        Assert.Equal(SessionAggregator.MaxActions, actions.Count);
        Assert.Equal("change 0", actions[0].What);
        Assert.Equal($"change {SessionAggregator.MaxActions - 1}", actions[^1].What);
    }

    /// <summary>An untouched service is the goal, and it must read as an empty list rather than null —
    /// the review distinguishes "nobody intervened" from "we did not capture it".</summary>
    [Fact]
    public void AHandsOffSessionReportsAnEmptyActionList()
    {
        var a = new SessionAggregator(3, 2);
        a.Tick(Sec, new[] { 0, 0 }, Unity);

        var s = Build(a);

        Assert.NotNull(s.Actions);
        Assert.Empty(s.Actions);
    }

    /// <summary>Actions and events are different kinds of fact — "the mix got worse at 10:14" means one
    /// thing if a mic died then and another if somebody switched a bus off.</summary>
    [Fact]
    public void ActionsAndEventsAreKeptApart()
    {
        var a = new SessionAggregator(3, 2);
        a.Note("stale", "10:14:00", "Calibration", AlertSeverity.Warning, "Calibration is stale");
        a.Action("10:14:02", "Reset calibration");

        var s = Build(a);

        Assert.Single(s.Events);
        Assert.Single(s.Actions);
        Assert.Equal("Reset calibration", s.Actions[0].What);
        Assert.Equal("Calibration is stale", s.Events[0].Message);
    }

    /// <summary>The summary must not alias the aggregator's own list — a session goes on recording
    /// after a checkpoint is written, and a checkpoint that mutates afterwards is not a checkpoint.</summary>
    [Fact]
    public void ACheckpointIsASnapshotAndDoesNotGrowAfterwards()
    {
        var a = new SessionAggregator(3, 2);
        a.Action("09:51:04", "Muted LAPEL");

        var first = Build(a);
        a.Action("09:55:00", "Unmuted LAPEL");

        Assert.Single(first.Actions);
        Assert.Equal(2, Build(a).Actions.Count);
    }
}
