using System.IO;
using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// The wiring between the live engine and <see cref="SessionAggregator"/>. The arithmetic is already
/// covered; what is only testable here is the set of judgements about WHEN to record and when not to
/// — and each of them exists because of a way a session record was lost or made misleading.
/// </summary>
public class SessionRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "AudioMixerRec", Guid.NewGuid().ToString("N"));

    public SessionRecorderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SessionRecorder Recorder(VmFixture f) =>
        new(f.Engine, f.Channels, f.Outputs, new SessionStore(_dir));

    /// <summary>Drives the recorder as the meter tick would, without waiting in real time. Tick reads
    /// its own clock, so the elapsed it accumulates comes from the wall — this just gets enough
    /// observations in to exercise the paths that care about the count.</summary>
    private static void Pump(SessionRecorder r, int ticks)
    {
        for (int i = 0; i < ticks; i++) r.Tick();
    }

    // --- when NOT to write --------------------------------------------------------------------------

    /// <summary>A launch-and-close is not a service. Without this every stray start leaves a record
    /// and the folder stops being a list of services.</summary>
    [Fact]
    public void ASessionShorterThanAMinuteIsNotWritten()
    {
        using var f = new VmFixture();
        var r = Recorder(f);
        Pump(r, 50);

        Assert.Null(r.Write());
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void DisposingATooShortSessionLeavesNothingBehind()
    {
        using var f = new VmFixture();
        var r = Recorder(f);
        Pump(r, 10);
        r.Dispose();

        Assert.Empty(Directory.GetFiles(_dir));
    }

    // --- the summary --------------------------------------------------------------------------------

    /// <summary>The record has to be readable without the rig in front of you, so the labels, the
    /// devices and the config all have to survive into it.</summary>
    [Fact]
    public void TheSummaryCarriesTheRigItWasRecordedOn()
    {
        using var f = new VmFixture();
        f.Channels[0].CustomLabel = "LAPEL";
        f.Channels[0].SelectedDevice = VmFixture.Lapel;
        var r = Recorder(f);
        r.Config = () => new SessionConfig { LowCutHz = 80, Lapel = "LAPEL" };
        Pump(r, 5);

        var s = r.BuildSummary();

        Assert.Equal("LAPEL", s.Inputs[0].Label);
        Assert.Equal("Wireless PRO RX", s.Inputs[0].DeviceName);
        Assert.Equal(80, s.Config.LowCutHz);
        Assert.Equal("LAPEL", s.Config.Lapel);
        Assert.Equal(f.Channels.Count, s.Inputs.Count);
        Assert.Equal(f.Outputs.Count, s.Outputs.Count);
    }

    /// <summary>An unlabelled strip still has to be identifiable in the record.</summary>
    [Fact]
    public void AnUnlabelledStripFallsBackToItsNumber()
    {
        using var f = new VmFixture();
        var s = Recorder(f).BuildSummary();

        Assert.Equal("Input 1", s.Inputs[0].Label);
        Assert.Equal("Input 2", s.Inputs[1].Label);
    }

    /// <summary>
    /// Config is a callback, not a value, because it must reflect the END of the session — a routing
    /// change halfway through is exactly the thing that explains an odd reading.
    /// </summary>
    [Fact]
    public void TheConfigIsSampledWhenTheRecordIsWrittenNotWhenTheRecorderWasMade()
    {
        using var f = new VmFixture();
        var r = Recorder(f);
        int lowCut = 0;
        r.Config = () => new SessionConfig { LowCutHz = lowCut };

        lowCut = 100;                       // the operator changes it mid-service

        Assert.Equal(100, r.BuildSummary().Config.LowCutHz);
    }

    [Fact]
    public void WithNoConfigCallbackTheRecordStillBuilds()
    {
        using var f = new VmFixture();
        var s = Recorder(f).BuildSummary();

        Assert.NotNull(s.Config);
        Assert.Equal(0, s.Config.LowCutHz);
    }

    // --- events and actions -------------------------------------------------------------------------

    /// <summary>Alerts are folded in from whatever the banner currently holds, so the record shows what
    /// the operator was being told — including warnings they closed and carried on past. Latched by id,
    /// because a mic 20 dB low is 20 dB low for the whole meeting.</summary>
    [Fact]
    public void ARepeatedAlertIsRecordedOnce()
    {
        using var f = new VmFixture();
        var r = Recorder(f);
        var alerts = new[] { new HealthAlert("level.low", AlertSeverity.Warning, "Input 1 is 20 dB low") };

        for (int i = 0; i < 30; i++) r.Note(alerts);

        var events = r.BuildSummary().Events;
        Assert.Single(events);
        Assert.Equal("Input 1 is 20 dB low", events[0].Message);
        Assert.Equal(AlertSeverity.Warning, events[0].Severity);
    }

    /// <summary>The event's Kind is the last dotted segment of the alert id — what the review groups by.</summary>
    [Fact]
    public void TheAlertIdsLastSegmentBecomesTheEventKind()
    {
        using var f = new VmFixture();
        var r = Recorder(f);
        r.Note(new[]
        {
            new HealthAlert("input.3.calibration", AlertSeverity.Warning, "stale"),
            new HealthAlert("clipping", AlertSeverity.Critical, "clipped"),
        });

        var kinds = r.BuildSummary().Events.Select(e => e.Kind).ToArray();

        Assert.Equal(new[] { "calibration", "clipping" }, kinds);
    }

    [Fact]
    public void OperatorActionsReachTheRecord()
    {
        using var f = new VmFixture();
        var r = Recorder(f);
        r.Action("Muted LAPEL");
        r.Action("Muted LAPEL");            // collapses
        r.Action("Scene: Prayer");

        var actions = r.BuildSummary().Actions;

        Assert.Equal(2, actions.Count);
        Assert.Equal("Muted LAPEL", actions[0].What);
        Assert.Equal("Scene: Prayer", actions[1].What);
    }

    // --- robustness -----------------------------------------------------------------------------------

    /// <summary>Ticking must never throw: it runs on the UI meter timer, so an exception here would
    /// take down the mixer it is describing.</summary>
    [Fact]
    public void TickingBeforeAnythingIsConfiguredIsHarmless()
    {
        using var f = new VmFixture();
        var r = Recorder(f);

        Pump(r, 200);

        Assert.True(r.BuildSummary().DurationMinutes >= 0);
    }

    [Fact]
    public void TheStampIsTheFileKeyAndDoesNotMoveDuringTheSession()
    {
        using var f = new VmFixture();
        var r = Recorder(f);
        var first = r.Stamp;
        Pump(r, 20);

        Assert.Equal(first, r.Stamp);
        Assert.Matches(@"^\d{8}-\d{6}$", r.Stamp);
    }

    /// <summary>
    /// A calibration median is NaN until a mic has produced enough voiced audio. System.Text.Json
    /// refuses to write NaN, so before the converter in SessionStore this threw, was swallowed by
    /// Save's blanket catch, and the WHOLE record was silently lost — for exactly the session this
    /// feature exists for: a mic nobody used, or a service too quiet to calibrate.
    /// </summary>
    [Fact]
    public void ARecordWithUncalibratedMicsStillSaves()
    {
        using var f = new VmFixture();
        var store = new SessionStore(_dir);
        var r = new SessionRecorder(f.Engine, f.Channels, f.Outputs, store);
        var summary = r.BuildSummary();
        Assert.True(float.IsNaN(summary.Inputs[0].SpeechDb), "expected an uncalibrated mic");

        var path = store.Save(summary);

        Assert.NotNull(path);
        Assert.Contains("\"SpeechDb\": null", File.ReadAllText(path!));
    }

    /// <summary>Round-trips back to NaN, so a reader cannot mistake "never calibrated" for 0 dBFS.</summary>
    [Fact]
    public void ANullMedianReadsBackAsNotANumber()
    {
        using var f = new VmFixture();
        var store = new SessionStore(_dir);
        var r = new SessionRecorder(f.Engine, f.Channels, f.Outputs, store);
        store.Save(r.BuildSummary());

        var loaded = store.Load(store.List()[0].Path);

        Assert.NotNull(loaded);
        Assert.True(float.IsNaN(loaded!.Inputs[0].SpeechDb));
    }

    /// <summary>Checkpoints overwrite rather than accumulate — the file is keyed on the start stamp, so
    /// a three-hour service leaves one record, not ninety.</summary>
    [Fact]
    public void ACheckpointOverwritesTheSameFile()
    {
        using var f = new VmFixture();
        var store = new SessionStore(_dir);
        var r = new SessionRecorder(f.Engine, f.Channels, f.Outputs, store);

        var a = store.Save(r.BuildSummary());
        var b = store.Save(r.BuildSummary());

        Assert.Equal(a, b);
        Assert.Single(Directory.GetFiles(_dir, "session-*.json"));
    }
}
