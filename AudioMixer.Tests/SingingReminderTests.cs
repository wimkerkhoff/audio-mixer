using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// Singing opens every routed mic with no switching, so the failure is forgetting to leave it: a sermon
/// then reaches the stream through several mics at once, and nothing in the room sounds broken. Singing
/// cannot be detected from the audio, so the reminder is time alone -- and these pin both sides of the
/// threshold, because a reminder that fires during a long hymn teaches people to dismiss it.
/// </summary>
public class SingingReminderTests
{
    private static IReadOnlyList<HealthAlert> Run(double singingSeconds) =>
        HealthMonitor.Evaluate(new HealthSnapshot(
            Array.Empty<ChannelHealth>(),
            new[] { new OutputHealth(0, "OBS/Zoom", true, false, -20, 0) },
            IsReplaying: false, SingingSeconds: singingSeconds));

    [Fact]
    public void NotDuringAWorshipSet() =>
        Assert.DoesNotContain(Run(14 * 60), a => a.Id == "singing.long");

    [Fact]
    public void NotWhenSpeaking() =>
        Assert.DoesNotContain(Run(0), a => a.Id == "singing.long");

    [Fact]
    public void AfterFifteenMinutesItOffersToSwitchBack()
    {
        var a = Assert.Single(Run(16 * 60), x => x.Id == "singing.long");

        Assert.Equal(AlertSeverity.Warning, a.Severity);
        Assert.Equal(FixKind.SwitchToSpeaking, a.Fix);
        Assert.Contains("16 min", a.Message);
    }
}
