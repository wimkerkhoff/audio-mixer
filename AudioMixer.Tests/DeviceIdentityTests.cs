using AudioMixer.Audio;
using AudioMixer.Services;
using NAudio.CoreAudioApi;

namespace AudioMixer.Tests;

/// <summary>
/// Whether a device can be re-found after it moves to another USB port, and whether two units of one
/// model can be told apart at all.
///
/// The answer turns out to be readable from the audio api alone, which matters because
/// PKEY_Device_InstanceId is not exposed on an endpoint and the PnP walk needs WMI. Windows makes a
/// container id one of two ways and records which in the UUID's version nibble: **v5** is name-based,
/// derived from the device's own hardware identity, so it is the same GUID on any port; **v1** is
/// time-based, minted once for that port and stored.
///
/// Cross-checked against tools/device-identity.ps1 on live hardware 2026-09-21, agreeing on all four
/// devices present: Lync USB Headset v1/PORT-DERIVED, Jabra PanaCast v5/SERIAL, RØDE Wireless PRO RX
/// v5/SERIAL, onboard Realtek v5/FIXED.
/// </summary>
public class DeviceIdentityTests
{
    // Real container ids, read off this rig. Keeping the actual values means these tests would notice
    // if the version-nibble reasoning were ever wrong about a device we have in hand.
    private const string RodeRx = "ea211674-4dbb-5c8b-a38c-59a37e65cff8";   // v5, has a serial
    private const string Jabra = "32de9b80-1cb9-532f-a82b-b0274498dd9c";    // v5, has a serial
    private const string LyncHeadset = "b0c5074b-56d9-11f1-ab6f-98fa9bee3448"; // v1, port-derived
    private const string Realtek = "a2dc47ed-93c0-58bd-a803-c4131c4512e4";  // v5, soldered in

    [Theory]
    [InlineData(RodeRx, 5)]
    [InlineData(Jabra, 5)]
    [InlineData(LyncHeadset, 1)]
    public void TheVersionNibbleIsReadFromTheRightPlace(string guid, int expected) =>
        Assert.Equal(expected, DeviceIdentity.Version(Guid.Parse(guid)));

    [Fact]
    public void AReceiverWithASerialIsRecognisedAsFollowingItsDevice()
    {
        Assert.Equal(IdentityKind.Serial, DeviceIdentity.Classify(Guid.Parse(RodeRx), "USB"));
        Assert.Equal(IdentityKind.Serial, DeviceIdentity.Classify(Guid.Parse(Jabra), "USB"));
    }

    /// <summary>The Lync headset and the Soundsync dongles: identity IS the port.</summary>
    [Fact]
    public void ADeviceWithoutASerialIsRecognisedAsPortDerived() =>
        Assert.Equal(IdentityKind.PortDerived, DeviceIdentity.Classify(Guid.Parse(LyncHeadset), "USB"));

    /// <summary>Checked before the container id: a soldered device is stable for a different reason,
    /// and calling onboard audio "serial" would be true but misleading in the devices table.</summary>
    [Fact]
    public void OnboardAudioIsFixedRatherThanSerial() =>
        Assert.Equal(IdentityKind.Fixed, DeviceIdentity.Classify(Guid.Parse(Realtek), "HDAUDIO"));

    [Theory]
    [InlineData("ROOT")]
    [InlineData("SWD")]
    [InlineData("MMDEVAPI")]
    public void VirtualEndpointsAreRecognisedWhateverTheirContainer(string bus) =>
        Assert.Equal(IdentityKind.Virtual, DeviceIdentity.Classify(Guid.Parse(RodeRx), bus));

    [Fact]
    public void NoContainerIdMeansNoClaimEitherWay()
    {
        Assert.Equal(IdentityKind.Unknown, DeviceIdentity.Classify(null, "USB"));
        Assert.Equal(IdentityKind.Unknown, DeviceIdentity.Classify(Guid.Empty, "USB"));
    }

    /// <summary>A property store that will not give up the enumerator name is common enough to have its
    /// own fallback in AudioDeviceInfo, and it says nothing about the container id — discarding a
    /// perfectly good serial because the bus was unreadable would lose the identity for no reason.</summary>
    [Fact]
    public void AnUnreadableBusDoesNotDiscardAGoodContainerId() =>
        Assert.Equal(IdentityKind.Serial, DeviceIdentity.Classify(Guid.Parse(RodeRx), null));

    // --- what gets written into a preset ---------------------------------------------------------

    /// <summary>
    /// Only a serial-derived id is worth saving. A port-derived container id is regenerated on exactly
    /// the same events as the endpoint GUID already in the preset, so storing it would add a key that
    /// LOOKS authoritative, matches first, and is no more durable — the worst of both.
    /// </summary>
    [Fact]
    public void OnlyASerialDerivedIdIsStoredAsTheStableKey()
    {
        Assert.Equal(RodeRx, DeviceIdentity.StableKey(Guid.Parse(RodeRx), "USB"));
        Assert.Null(DeviceIdentity.StableKey(Guid.Parse(LyncHeadset), "USB"));
        Assert.Null(DeviceIdentity.StableKey(Guid.Parse(Realtek), "HDAUDIO"));
        Assert.Null(DeviceIdentity.StableKey(null, "USB"));
    }

    // --- resolution ------------------------------------------------------------------------------

    private static AudioDeviceInfo Dev(string id, string name, string? container, string bus = "USB") =>
        new(id, name, DataFlow.Capture, bus, container == null ? null : Guid.Parse(container));

    /// <summary>
    /// The failure this exists for. Two Wireless PRO receivers share a friendly name, so name matching
    /// can only refuse (never greedy-fill — the Anker lesson), and each replug mints a new endpoint
    /// GUID. Without the container id there is nothing left to match on, and each receiver covers its
    /// own part of the room, so binding the wrong one is the wrong mic in the wrong place.
    /// </summary>
    [Fact]
    public void TwoIdenticalReceiversAreToldApartByTheirSerials()
    {
        var all = new[]
        {
            Dev("{new-guid-1}", "Microphone (Wireless PRO RX)", RodeRx),
            Dev("{new-guid-2}", "Microphone (Wireless PRO RX)", Jabra),
        };

        // The preset's saved endpoint GUID is stale (replugged) and the name is shared.
        var match = DeviceResolver.Resolve(
            all, "{old-stale-guid}", "Microphone (Wireless PRO RX)", new HashSet<string>(),
            ChannelSource.Stereo, Jabra);

        Assert.NotNull(match);
        Assert.Equal("{new-guid-2}", match!.Id);
    }

    /// <summary>Without the key the same lookup can only refuse — which is the pre-2026-09-21
    /// behaviour, and is still correct, just worse.</summary>
    [Fact]
    public void WithoutAKeyTwoIdenticalReceiversStillRefuseRatherThanGuess()
    {
        var all = new[]
        {
            Dev("{new-guid-1}", "Microphone (Wireless PRO RX)", RodeRx),
            Dev("{new-guid-2}", "Microphone (Wireless PRO RX)", Jabra),
        };

        Assert.Null(DeviceResolver.Resolve(
            all, "{old-stale-guid}", "Microphone (Wireless PRO RX)", new HashSet<string>()));
    }

    /// <summary>The key outranks the endpoint GUID, because a GUID can be reissued to a DIFFERENT
    /// device after a re-enumeration while the serial cannot.</summary>
    [Fact]
    public void TheSerialOutranksAStaleEndpointGuid()
    {
        var all = new[]
        {
            Dev("{guid-a}", "Microphone (Wireless PRO RX)", RodeRx),
            Dev("{guid-b}", "Microphone (Jabra PanaCast)", Jabra),
        };

        var match = DeviceResolver.Resolve(all, "{guid-b}", "Microphone (Jabra PanaCast)",
                                           new HashSet<string>(), ChannelSource.Stereo, RodeRx);

        Assert.Equal("{guid-a}", match!.Id);
    }

    /// <summary>A key for a device that is not here must fall through, not fail — the receiver may
    /// simply be unplugged, and the name match is still the right answer for a single unit.</summary>
    [Fact]
    public void AnAbsentKeyFallsThroughToTheUsualMatching()
    {
        var all = new[] { Dev("{guid-a}", "Microphone (Wireless PRO RX)", RodeRx) };

        var match = DeviceResolver.Resolve(all, "{stale}", "Microphone (Wireless PRO RX)",
                                           new HashSet<string>(), ChannelSource.Stereo,
                                           "11111111-2222-5333-8444-555555555555");

        Assert.Equal("{guid-a}", match!.Id);
    }

    [Fact]
    public void AMalformedKeyIsIgnoredRatherThanThrowing()
    {
        var all = new[] { Dev("{guid-a}", "Microphone (Wireless PRO RX)", RodeRx) };

        var match = DeviceResolver.Resolve(all, "{guid-a}", null, new HashSet<string>(),
                                           ChannelSource.Stereo, "not-a-guid");

        Assert.Equal("{guid-a}", match!.Id);
    }

    /// <summary>A key match still respects side claims, so a split receiver's two strips do not both
    /// take the same side.</summary>
    [Fact]
    public void AKeyMatchStillHonoursSideClaims()
    {
        var all = new[] { Dev("{rx}", "Microphone (Wireless PRO RX)", RodeRx) };
        var used = new HashSet<string>();

        var left = DeviceResolver.Resolve(all, null, null, used, ChannelSource.Left, RodeRx);
        var right = DeviceResolver.Resolve(all, null, null, used, ChannelSource.Right, RodeRx);
        var third = DeviceResolver.Resolve(all, null, null, used, ChannelSource.Left, RodeRx);

        Assert.Equal("{rx}", left!.Id);
        Assert.Equal("{rx}", right!.Id);
        Assert.Null(third);
    }

    [Fact]
    public void EveryKindHasPlainLanguageForTheDevicesTable()
    {
        foreach (IdentityKind k in Enum.GetValues<IdentityKind>())
            Assert.False(string.IsNullOrWhiteSpace(DeviceIdentity.Describe(k)));
    }
}
