using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// The invariant scenes already hold, restated for manual routing: no operator action may leave a bus
/// with nothing live on it. Clickable A/B and mute in the operator panel let a volunteer reach the
/// silent-stream state one tap at a time, which is exactly what SceneTransformTests protects against
/// on the scene path.
///
/// The failure is unrecoverable by the person it happens to: a silent stream is the one fault nobody
/// in the room can hear.
/// </summary>
public class RouteGuardTests
{
    private static ChannelRouting Mic(int i, bool a = true, bool b = true, bool muted = false,
        bool device = true, bool dead = false, string label = "mic")
        => new(i, label + (i + 1), new[] { a, b }, muted, device, dead);

    // --- the core rule -----------------------------------------------------------------------

    [Fact]
    public void TheLastMicOnABusCannotBeUnrouted()
    {
        var rig = new[] { Mic(0, a: true, b: true), Mic(1, a: false, b: true) };

        var v = RouteGuard.CheckUnroute(rig, 0, 0);

        Assert.False(v.Allowed);
        Assert.Contains("Bus A", v.Reason);
    }

    [Fact]
    public void AMicCanBeUnrouted_WhenAnotherStillCoversTheBus()
    {
        var rig = new[] { Mic(0), Mic(1) };

        Assert.True(RouteGuard.CheckUnroute(rig, 0, 0).Allowed);
    }

    [Fact]
    public void MutingIsGuardedToo_OrItIsTheSameHoleWithADifferentButton()
    {
        var rig = new[] { Mic(0), Mic(1, a: false, b: false) };

        var v = RouteGuard.CheckMute(rig, 0);

        Assert.False(v.Allowed);
    }

    /// <summary>Mute drops a channel off every bus at once, so it can be the last on either.</summary>
    [Fact]
    public void MuteIsRefusedWhenItWouldEmptyTheSecondBusOnly()
    {
        var rig = new[]
        {
            Mic(0, a: true, b: true),
            Mic(1, a: true, b: false),   // covers A but not B
        };

        Assert.False(RouteGuard.CheckMute(rig, 0).Allowed);
        Assert.Contains("Bus B", RouteGuard.CheckMute(rig, 0).Reason);
    }

    // --- what does NOT count as covering a bus ------------------------------------------------

    [Theory]
    [InlineData(true, false, false)]   // muted
    [InlineData(false, true, false)]   // no device bound
    [InlineData(false, false, true)]   // bound but delivering nothing
    public void AMicThatIsNotActuallyPassingAudioDoesNotHoldABus(bool muted, bool noDevice, bool dead)
    {
        var rig = new[]
        {
            Mic(0),
            Mic(1, muted: muted, device: !noDevice, dead: dead),
        };

        // mic 1 cannot cover for mic 0, so removing mic 0 must still be refused
        Assert.False(RouteGuard.CheckUnroute(rig, 0, 0).Allowed);
    }

    // --- what the guard must NOT do -----------------------------------------------------------

    /// <summary>
    /// An output already uncovered cannot be made worse, and refusing there would trap an operator
    /// part-way through rearranging their mics.
    /// </summary>
    [Fact]
    public void AnAlreadyEmptyBusDoesNotBlockAnything()
    {
        var rig = new[] { Mic(0, a: false, b: true) };

        Assert.True(RouteGuard.CheckUnroute(rig, 0, 0).Allowed);
    }

    /// <summary>
    /// Only removals are ever checked — routing a mic ON or unmuting it cannot uncover a bus, so the
    /// guard is never consulted and the caller applies those directly.
    /// </summary>
    [Fact]
    public void AddingCoverNeedsNoPermission()
    {
        var rig = new[] { Mic(0, a: true, b: false), Mic(1, a: true, b: false) };

        // bus B is uncovered; routing either mic to it is not a guarded operation at all
        Assert.Equal(0, RouteGuard.CoverCount(rig, 1));
        Assert.True(RouteGuard.CheckUnroute(rig, 0, 1).Allowed);
    }

    [Fact]
    public void UnmutingIsNeverBlocked()
    {
        var rig = new[] { Mic(0), Mic(1, muted: true) };

        Assert.True(RouteGuard.CheckMute(rig, 1).Allowed);   // already muted, no cover lost
    }

    [Fact]
    public void AnOutOfRangeChannelIsIgnoredRatherThanThrowing()
    {
        var rig = new[] { Mic(0) };

        Assert.True(RouteGuard.CheckUnroute(rig, 9, 0).Allowed);
        Assert.True(RouteGuard.CheckMute(rig, -1).Allowed);
    }

    [Fact]
    public void AnOutOfRangeOutputIsIgnoredRatherThanThrowing() =>
        Assert.True(RouteGuard.CheckUnroute(new[] { Mic(0) }, 0, 5).Allowed);

    // --- the property that matters ------------------------------------------------------------

    /// <summary>
    /// Exhaustive over every three-mic rig and every single change: applying anything the guard
    /// allows can never take a covered bus to zero. This is the whole point, stated once.
    /// </summary>
    [Fact]
    public void NoAllowedChangeEverEmptiesACoveredBus()
    {
        foreach (var rig in AllRigs(3))
        {
            for (int i = 0; i < rig.Count; i++)
            {
                for (int o = 0; o < 2; o++)
                {
                    if (!RouteGuard.CheckUnroute(rig, i, o).Allowed) continue;
                    var after = Apply(rig, i, c =>
                    {
                        var r = (bool[])c.Routes.Clone(); r[o] = false; return c with { Routes = r };
                    });
                    AssertNoBusLost(rig, after);
                }

                if (!RouteGuard.CheckMute(rig, i).Allowed) continue;
                AssertNoBusLost(rig, Apply(rig, i, c => c with { Muted = true }));
            }
        }
    }

    private static void AssertNoBusLost(IReadOnlyList<ChannelRouting> before, IReadOnlyList<ChannelRouting> after)
    {
        for (int o = 0; o < 2; o++)
        {
            if (RouteGuard.CoverCount(before, o) > 0)
                Assert.True(RouteGuard.CoverCount(after, o) > 0,
                    $"an allowed change emptied bus {RouteGuard.OutputLetter(o)}");
        }
    }

    private static List<ChannelRouting> Apply(
        IReadOnlyList<ChannelRouting> rig, int index, Func<ChannelRouting, ChannelRouting> change) =>
        rig.Select((c, i) => i == index ? change(c) : c).ToList();

    /// <summary>Every combination of routes/mute/device/dead across n channels.</summary>
    private static IEnumerable<List<ChannelRouting>> AllRigs(int n)
    {
        var states = new List<ChannelRouting>();
        for (int mask = 0; mask < 16; mask++)
        {
            states.Add(new ChannelRouting(0, "m",
                new[] { (mask & 1) != 0, (mask & 2) != 0 },
                Muted: (mask & 4) != 0, HasDevice: true, Dead: (mask & 8) != 0));
        }

        var idx = new int[n];
        while (true)
        {
            yield return Enumerable.Range(0, n)
                .Select(i => states[idx[i]] with { Index = i }).ToList();

            int k = n - 1;
            while (k >= 0 && ++idx[k] >= states.Count) { idx[k] = 0; k--; }
            if (k < 0) yield break;
        }
    }
}
