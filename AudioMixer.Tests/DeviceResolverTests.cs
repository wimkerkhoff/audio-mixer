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

    // --- interface-name tier (a device that came back on a different USB port) --------------------

    /// <summary>
    /// A new port mints a fresh endpoint that loses the rename and can carry a different role prefix,
    /// which defeats the full-name match. The interface inside the parens still identifies the box.
    /// </summary>
    [Fact]
    public void ADifferentRolePrefixStillResolvesByInterfaceName()
    {
        var live = new List<AudioDeviceInfo> { Dev("{new}", "Microphone (Wireless PRO RX)") };

        var match = DeviceResolver.Resolve(
            live, "{old}", "Desktop Microphone (Wireless PRO RX)", new HashSet<string>());

        Assert.Equal("{new}", match?.Id);
    }

    /// <summary>
    /// The documented mis-bind hazard, from the other direction: these share the role prefix
    /// "Speakers" but not the interface, so the interface tier must never confuse them.
    /// </summary>
    [Fact]
    public void ASharedRolePrefixDoesNotCrossMatch()
    {
        var live = new List<AudioDeviceInfo> { Dev("{realtek}", "Speakers (Realtek(R) Audio)") };

        var match = DeviceResolver.Resolve(
            live, "{gone}", "Speakers (Lync USB Headset)", new HashSet<string>());

        Assert.Null(match);
    }

    /// <summary>One multi-jack box exposes several endpoints under one interface name: refuse, never guess.</summary>
    [Fact]
    public void AnAmbiguousInterfaceNameRefusesToBind()
    {
        var live = new List<AudioDeviceInfo>
        {
            Dev("{mic}", "Microphone (Scarlett 2i2 USB)"),
            Dev("{line}", "Line In (Scarlett 2i2 USB)"),
        };

        var match = DeviceResolver.Resolve(
            live, "{gone}", "Analogue 1 (Scarlett 2i2 USB)", new HashSet<string>());

        Assert.Null(match);
    }

    /// <summary>Once one of the pair is claimed the remaining candidate is unambiguous, so it binds.</summary>
    [Fact]
    public void ClaimingOneOfAnAmbiguousPairResolvesTheOther()
    {
        var live = new List<AudioDeviceInfo>
        {
            Dev("{mic}", "Microphone (Scarlett 2i2 USB)"),
            Dev("{line}", "Line In (Scarlett 2i2 USB)"),
        };
        var used = new HashSet<string> { DeviceResolver.Claim("{mic}", ChannelSource.Stereo) };

        var match = DeviceResolver.Resolve(live, "{gone}", "Analogue 2 (Scarlett 2i2 USB)", used);

        Assert.Equal("{line}", match?.Id);
    }

    // --- serial key (identical receivers) -----------------------------------------------------------

    private static readonly Guid RxA = Guid.Parse("ea211674-4dbb-5c8b-a38c-59a37e65cff8");
    private static readonly Guid RxB = Guid.Parse("ec0a9f31-af72-5037-a92d-e573b5498ead");

    private static AudioDeviceInfo Rx(string id, string name, Guid container) =>
        new(id, name, DataFlow.Capture, "USB", container);

    /// <summary>
    /// 2026-10-04: receiver A was unplugged for a firmware update while B was present and free. A
    /// strip that wants A must wait for A — the shared friendly name must not hand it B.
    /// </summary>
    [Fact]
    public void ASerialKeyNeverFallsBackToAnUnpluggedReceiversTwin()
    {
        var live = new List<AudioDeviceInfo>
        {
            Rx("{b-new}", "Desktop Microphone (2- Wireless PRO RX)", RxB),
        };

        var match = DeviceResolver.Resolve(live, "{a-old}", "Desktop Microphone (Wireless PRO RX)",
            new HashSet<string>(), ChannelSource.Left, RxA.ToString());

        Assert.Null(match);
    }

    /// <summary>The wanted receiver is present but its side is taken: wait, don't take the twin.</summary>
    [Fact]
    public void ASerialKeyNeverFallsBackWhenItsReceiverIsClaimed()
    {
        var live = new List<AudioDeviceInfo>
        {
            Rx("{a}", "Desktop Microphone (Wireless PRO RX)", RxA),
            Rx("{b}", "Desktop Microphone (2- Wireless PRO RX)", RxB),
        };
        var used = new HashSet<string> { DeviceResolver.Claim("{a}", ChannelSource.Left) };

        var match = DeviceResolver.Resolve(live, "{a}", "Desktop Microphone (Wireless PRO RX)",
            used, ChannelSource.Left, RxA.ToString());

        Assert.Null(match);
    }

    /// <summary>The key still binds through a new GUID and a new "N- " name, which is its job.</summary>
    [Fact]
    public void ASerialKeyFindsItsReceiverUnderANewIdAndName()
    {
        var live = new List<AudioDeviceInfo>
        {
            Rx("{b}", "Desktop Microphone (Wireless PRO RX)", RxB),
            Rx("{a-new}", "Desktop Microphone (3- Wireless PRO RX)", RxA),
        };

        var match = DeviceResolver.Resolve(live, "{a-old}", "Desktop Microphone (Wireless PRO RX)",
            new HashSet<string>(), ChannelSource.Right, RxA.ToString());

        Assert.Equal("{a-new}", match?.Id);
    }

    [Theory]
    [InlineData("Speakers (Realtek(R) Audio)", "Realtek(R) Audio")]
    [InlineData("Microphone (5- Wireless PRO RX)", "Wireless PRO RX")]
    [InlineData("Desktop Microphone (Wireless PRO RX)", "Wireless PRO RX")]
    [InlineData("ANKER #3", null)]
    [InlineData("", null)]
    public void InterfaceKey_ReadsTheParenthesisedPart(string name, string? expected) =>
        Assert.Equal(expected, DeviceResolver.InterfaceKey(name));
}
