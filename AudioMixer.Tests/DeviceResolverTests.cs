using AudioMixer.Audio;
using AudioMixer.Services;
using NAudio.CoreAudioApi;

namespace AudioMixer.Tests;

/// <summary>
/// The side-claim rules that let ONE endpoint feed TWO input strips (a two-transmitter receiver in
/// Split mode puts TX1 on the left and TX2 on the right of a single WASAPI device). Getting this
/// wrong is silent in both directions: too strict and the second transmitter vanishes from the
/// preset on load, too loose and two strips double the same audio onto the bus.
/// </summary>
public class DeviceResolverTests
{
    private static AudioDeviceInfo Dev(string id, string name) => new(id, name, DataFlow.Capture);

    private static readonly List<AudioDeviceInfo> Devices = new()
    {
        Dev("{rode}", "R0de wireless (Realtek(R) Audio)"),
        Dev("{anker3}", "ANKER #3 (4- Anker Soundsync)"),
    };

    [Fact]
    public void OppositeSidesOfOneEndpointBothResolve()
    {
        var used = new HashSet<string>();

        var left = DeviceResolver.Resolve(Devices, "{rode}", null, used, ChannelSource.Left);
        var right = DeviceResolver.Resolve(Devices, "{rode}", null, used, ChannelSource.Right);

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal("{rode}", left!.Id);
        Assert.Equal("{rode}", right!.Id);
    }

    [Fact]
    public void TheSameSideCannotBeClaimedTwice()
    {
        var used = new HashSet<string>();

        Assert.NotNull(DeviceResolver.Resolve(Devices, "{rode}", null, used, ChannelSource.Left));
        Assert.Null(DeviceResolver.Resolve(Devices, "{rode}", null, used, ChannelSource.Left));
    }

    [Fact]
    public void AStereoClaimTakesTheWholeEndpoint()
    {
        var used = new HashSet<string>();

        Assert.NotNull(DeviceResolver.Resolve(Devices, "{rode}", null, used, ChannelSource.Stereo));
        Assert.Null(DeviceResolver.Resolve(Devices, "{rode}", null, used, ChannelSource.Left));
        Assert.Null(DeviceResolver.Resolve(Devices, "{rode}", null, used, ChannelSource.Right));
    }

    [Fact]
    public void AHalfClaimedEndpointCannotBeTakenWhole()
    {
        var used = new HashSet<string>();

        Assert.NotNull(DeviceResolver.Resolve(Devices, "{rode}", null, used, ChannelSource.Left));
        Assert.Null(DeviceResolver.Resolve(Devices, "{rode}", null, used, ChannelSource.Stereo));
    }

    /// <summary>
    /// The whole-endpoint claim must keep the bare device id as its key, or every preset written
    /// before split mode existed would stop resolving.
    /// </summary>
    [Fact]
    public void TheLegacyOverloadStillClaimsWholeEndpoints()
    {
        var used = new HashSet<string>();

        Assert.NotNull(DeviceResolver.Resolve(Devices, "{anker3}", null, used));
        Assert.Contains("{anker3}", used);
        Assert.Null(DeviceResolver.Resolve(Devices, "{anker3}", null, used, ChannelSource.Right));
    }

    /// <summary>
    /// A hot-plugged endpoint's GUID changes, so the name fallback has to honour side claims too —
    /// otherwise the second transmitter re-binds to the side the first one already took.
    /// </summary>
    [Fact]
    public void TheNameFallbackHonoursSideClaims()
    {
        var used = new HashSet<string>();

        var left = DeviceResolver.Resolve(
            Devices, "{stale-guid}", "R0de wireless (2- Realtek(R) Audio)", used, ChannelSource.Left);
        var right = DeviceResolver.Resolve(
            Devices, "{stale-guid}", "R0de wireless (2- Realtek(R) Audio)", used, ChannelSource.Right);
        var third = DeviceResolver.Resolve(
            Devices, "{stale-guid}", "R0de wireless (2- Realtek(R) Audio)", used, ChannelSource.Left);

        Assert.Equal("{rode}", left?.Id);
        Assert.Equal("{rode}", right?.Id);
        Assert.Null(third);
    }

    [Fact]
    public void IsFreeAgreesWithWhatResolveClaimed()
    {
        var used = new HashSet<string>();
        DeviceResolver.Resolve(Devices, "{rode}", null, used, ChannelSource.Left);

        Assert.False(DeviceResolver.IsFree(used, "{rode}", ChannelSource.Left));
        Assert.False(DeviceResolver.IsFree(used, "{rode}", ChannelSource.Stereo));
        Assert.True(DeviceResolver.IsFree(used, "{rode}", ChannelSource.Right));
        Assert.True(DeviceResolver.IsFree(used, "{anker3}", ChannelSource.Stereo));
    }
}
