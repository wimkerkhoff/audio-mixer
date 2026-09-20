using AudioMixer.Audio;
using AudioMixer.Services;
using NAudio.CoreAudioApi;

namespace AudioMixer.Tests;

/// <summary>
/// Replugging a receiver used to cost a manual remap every time. A hot-plug USB audio device gets a
/// NEW WASAPI endpoint GUID on each replug, so a preset re-binds by friendly name — but unplugging
/// nulled SelectedDevice, and the 500 ms autosave then wrote DeviceId=null DeviceName=null, deleting
/// the only thing the name match could have worked from. These pin the resolution half of the fix;
/// the "don't overwrite the name with null" half lives in ChannelViewModel.DesiredDevice* and
/// PresetMapper.
/// </summary>
public class ReplugIdentityTests
{
    private static AudioDeviceInfo Dev(string id, string name) => new(id, name, DataFlow.Capture);

    private const string OldId = "{0.0.1.00000000}.{aaaaaaaa-1111-2222-3333-444444444444}";
    private const string NewId = "{0.0.1.00000000}.{bbbbbbbb-5555-6666-7777-888888888888}";
    private const string Name = "Desktop Microphone (Wireless PRO RX)";

    [Fact]
    public void AReplugWithAFreshGuidStillResolvesByName()
    {
        var afterReplug = new List<AudioDeviceInfo> { Dev(NewId, Name) };

        var match = DeviceResolver.Resolve(afterReplug, OldId, Name, new HashSet<string>());

        Assert.NotNull(match);
        Assert.Equal(NewId, match!.Id);
    }

    /// <summary>The regression itself: with the name erased there is nothing left to match on.</summary>
    [Fact]
    public void WithTheNameErasedTheDeviceCannotComeBack()
    {
        var afterReplug = new List<AudioDeviceInfo> { Dev(NewId, Name) };

        Assert.Null(DeviceResolver.Resolve(afterReplug, null, null, new HashSet<string>()));
        Assert.Null(DeviceResolver.Resolve(afterReplug, OldId, null, new HashSet<string>()));
    }

    /// <summary>
    /// Both halves of a split receiver have to come back, not just the first — they share one
    /// endpoint and are told apart only by side.
    /// </summary>
    [Fact]
    public void BothSidesOfASplitReceiverReattach()
    {
        var afterReplug = new List<AudioDeviceInfo> { Dev(NewId, Name) };
        var used = new HashSet<string>();

        var l = DeviceResolver.Resolve(afterReplug, OldId, Name, used, ChannelSource.Left);
        var r = DeviceResolver.Resolve(afterReplug, OldId, Name, used, ChannelSource.Right);

        Assert.Equal(NewId, l?.Id);
        Assert.Equal(NewId, r?.Id);
    }

    /// <summary>A strip must never reattach to an endpoint another strip already holds whole.</summary>
    [Fact]
    public void AClaimedEndpointIsNotStolen()
    {
        var devices = new List<AudioDeviceInfo> { Dev(NewId, Name) };
        var used = new HashSet<string>();

        Assert.NotNull(DeviceResolver.Resolve(devices, OldId, Name, used, ChannelSource.Stereo));
        Assert.Null(DeviceResolver.Resolve(devices, OldId, Name, used, ChannelSource.Stereo));
    }

    /// <summary>The enumerator prefix Windows shuffles on replug must not defeat the name match.</summary>
    [Fact]
    public void TheVolatileEnumeratorPrefixIsIgnored()
    {
        var afterReplug = new List<AudioDeviceInfo> { Dev(NewId, "Microphone (5- Wireless PRO RX)") };

        var match = DeviceResolver.Resolve(
            afterReplug, OldId, "Microphone (2- Wireless PRO RX)", new HashSet<string>());

        Assert.Equal(NewId, match?.Id);
    }

    // --- two identical receivers ----------------------------------------------------------------

    /// <summary>
    /// The case the name fallback cannot handle. Both receivers report the same friendly name, so
    /// binding "the first free one" picks an arbitrary unit -- and each receiver covers its own part
    /// of the room, so that is the wrong mic in the wrong place with nothing about it looking wrong.
    /// Nothing bound is recoverable; the wrong thing bound is not.
    /// </summary>
    [Fact]
    public void TwoIdenticalReceiversRefuseToResolveByName()
    {
        var live = new List<AudioDeviceInfo>
        {
            Dev("{rx-a}", Name),
            Dev("{rx-b}", Name),
        };

        Assert.Null(DeviceResolver.Resolve(live, "{gone}", Name, new HashSet<string>()));
    }

    /// <summary>An exact id is never ambiguous, so the common same-port case still self-heals.</summary>
    [Fact]
    public void AnExactIdStillWinsAmongIdenticalTwins()
    {
        var live = new List<AudioDeviceInfo> { Dev("{rx-a}", Name), Dev("{rx-b}", Name) };

        Assert.Equal("{rx-b}", DeviceResolver.Resolve(live, "{rx-b}", Name, new HashSet<string>())?.Id);
    }

    /// <summary>Once one twin is claimed the other is the only candidate, so it binds.</summary>
    [Fact]
    public void ClaimingOneTwinMakesTheOtherUnambiguous()
    {
        var live = new List<AudioDeviceInfo> { Dev("{rx-a}", Name), Dev("{rx-b}", Name) };
        var used = new HashSet<string> { DeviceResolver.Claim("{rx-a}", ChannelSource.Stereo) };

        Assert.Equal("{rx-b}", DeviceResolver.Resolve(live, "{gone}", Name, used)?.Id);
    }

    /// <summary>
    /// A split receiver is TWO strips on ONE endpoint, which must not read as ambiguity -- that would
    /// break the ordinary single-receiver rig.
    /// </summary>
    [Fact]
    public void OneReceiverFeedingTwoStripsIsNotAmbiguous()
    {
        var live = new List<AudioDeviceInfo> { Dev(NewId, Name) };
        var used = new HashSet<string>();

        Assert.Equal(NewId, DeviceResolver.Resolve(live, "{gone}", Name, used, ChannelSource.Left)?.Id);
        Assert.Equal(NewId, DeviceResolver.Resolve(live, "{gone}", Name, used, ChannelSource.Right)?.Id);
    }
}
