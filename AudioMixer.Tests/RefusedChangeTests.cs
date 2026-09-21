using AudioMixer.Services;
using AudioMixer.ViewModels;

namespace AudioMixer.Tests;

/// <summary>
/// A vetoed mute or unroute raises the same PropertyChanged a real change does — it has to, or the
/// toggle would stay visually flipped after being refused. The listener could not tell the two apart,
/// and everything it does afterwards reads the value behind the notification, which is unchanged. So
/// refusing a mute wrote "LAPEL unmuted" into the session action log, cleared the scene the operator
/// had never left, and restarted the autosave for a write that never happened.
///
/// An action log that reports the opposite of what happened is worse than one that reports nothing:
/// step 8 of the session review asks "did this change help", and the answer would be about an action
/// nobody took.
/// </summary>
public class RefusedChangeTests : IDisposable
{
    private readonly VmFixture _f = new(inputs: 2);

    public void Dispose() => _f.Dispose();

    /// <summary>Captures what a listener would see, the way MainViewModel.OnSettingChanged does.</summary>
    private sealed record Seen(object? Sender, string? Property, bool Refused);

    private List<Seen> Watch(ChannelViewModel ch)
    {
        var seen = new List<Seen>();
        ch.PropertyChanged += (s, e) => seen.Add(new Seen(s, e.PropertyName, Refused(s)));
        foreach (var r in ch.Routes)
            r.PropertyChanged += (s, e) => seen.Add(new Seen(s, e.PropertyName, Refused(s)));
        return seen;
    }

    /// <summary>Exactly the test MainViewModel.OnSettingChanged applies.</summary>
    private static bool Refused(object? sender) =>
        sender is ChannelViewModel { ChangeRefused: true } or RouteToggleViewModel { ChangeRefused: true };

    // --- a refusal is distinguishable ---------------------------------------------------------------

    [Fact]
    public void ARefusedMuteRaisesTheNotificationButMarksItAsARefusal()
    {
        var ch = _f.Channels[0];
        ch.MuteGuard = _ => false;
        var seen = Watch(ch);

        ch.Muted = true;

        Assert.False(ch.Muted, "the veto did not hold");
        var muted = Assert.Single(seen, x => x.Property == nameof(ChannelViewModel.Muted));
        Assert.True(muted.Refused, "the listener could not tell this raise from a real change");
    }

    [Fact]
    public void ARefusedUnrouteRaisesTheNotificationButMarksItAsARefusal()
    {
        var ch = _f.Channels[0];
        ch.Routes[0].IsOn = true;
        ch.Routes[0].Guard = _ => false;
        var seen = Watch(ch);

        ch.Routes[0].IsOn = false;

        Assert.True(ch.Routes[0].IsOn, "the veto did not hold");
        var route = Assert.Single(seen, x => x.Property == nameof(RouteToggleViewModel.IsOn));
        Assert.True(route.Refused);
    }

    // --- a real change is NOT marked ----------------------------------------------------------------

    [Fact]
    public void AnAllowedMuteIsNotMarkedAsRefused()
    {
        var ch = _f.Channels[0];
        ch.MuteGuard = _ => true;
        var seen = Watch(ch);

        ch.Muted = true;

        Assert.True(ch.Muted);
        Assert.All(seen.Where(x => x.Property == nameof(ChannelViewModel.Muted)),
                   x => Assert.False(x.Refused));
    }

    [Fact]
    public void AnAllowedUnrouteIsNotMarkedAsRefused()
    {
        var ch = _f.Channels[0];
        ch.Routes[0].IsOn = true;
        ch.Routes[0].Guard = _ => true;
        var seen = Watch(ch);

        ch.Routes[0].IsOn = false;

        Assert.False(ch.Routes[0].IsOn);
        Assert.All(seen.Where(x => x.Property == nameof(RouteToggleViewModel.IsOn)),
                   x => Assert.False(x.Refused));
    }

    /// <summary>Unmuting always ADDS cover, so it is never asked and can never be refused — a refusal
    /// there would trap the strip in the state it was refused out of.</summary>
    [Fact]
    public void UnmutingIsNeverRefused()
    {
        var ch = _f.Channels[0];
        ch.MuteGuard = _ => true;
        ch.Muted = true;
        ch.MuteGuard = _ => false;
        var seen = Watch(ch);

        ch.Muted = false;

        Assert.False(ch.Muted);
        Assert.All(seen, x => Assert.False(x.Refused));
    }

    /// <summary>The flag must not latch: a refusal followed by an allowed change has to log normally,
    /// or one veto would silence the action log for the rest of the service.</summary>
    [Fact]
    public void TheFlagDoesNotSurviveIntoTheNextChange()
    {
        var ch = _f.Channels[0];
        ch.MuteGuard = _ => false;
        ch.Muted = true;
        Assert.False(ch.ChangeRefused, "the flag latched after the refusal");

        ch.MuteGuard = _ => true;
        var seen = Watch(ch);
        ch.Muted = true;

        Assert.True(ch.Muted);
        Assert.All(seen, x => Assert.False(x.Refused));
    }

    /// <summary>With no guard wired — every unit context, and the app before AttachChannel runs —
    /// nothing is ever refused.</summary>
    [Fact]
    public void WithNoGuardNothingIsRefused()
    {
        var ch = _f.Channels[1];
        var seen = Watch(ch);

        ch.Muted = true;
        ch.Routes[0].IsOn = true;
        ch.Routes[0].IsOn = false;

        Assert.All(seen, x => Assert.False(x.Refused));
    }
}
