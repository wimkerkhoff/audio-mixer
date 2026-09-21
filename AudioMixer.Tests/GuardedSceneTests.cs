using AudioMixer.Models;
using AudioMixer.Services;
using AudioMixer.ViewModels;

namespace AudioMixer.Tests;

/// <summary>
/// Scenes applied through the LIVE view models with the route guard wired, which is the layer
/// `SceneTransformTests` cannot see: that tests the pure function, and the guard sits above it.
///
/// The bug this pins: `SceneController.Write` walks channels in index order, so the guard sees torn
/// intermediate states. Going from Singing (lapel is the only source) to Prayer, the lapel is written
/// first — `Muted = true` while it is still the only cover on both buses — and `RouteGuard.CheckMute`
/// refused. The room mics were then routed, leaving Prayer with the lapel still live and
/// "Bus A would have no microphone" flashing on the status line. Exactly the priority-duck hazard the
/// Prayer scene exists to avoid.
/// </summary>
public class GuardedSceneTests : IDisposable
{
    private readonly VmFixture _f = new(inputs: 3);

    public void Dispose() => _f.Dispose();

    /// <summary>Wires the guards the way MainViewModel.AttachChannel does, so the refusal path is real
    /// rather than simulated.</summary>
    private void WireGuards()
    {
        List<ChannelRouting> Snapshot() => _f.Channels.Select(c => new ChannelRouting(
            c.Index,
            string.IsNullOrWhiteSpace(c.CustomLabel) ? c.Label : c.CustomLabel,
            c.Routes.Select(r => r.IsOn).ToArray(),
            c.Muted,
            c.SelectedDevice != null,
            Dead: false)).ToList();

        bool Allow(RouteVerdict v) => _scenes.IsApplying || v.Allowed;

        foreach (var ch in _f.Channels)
        {
            var self = ch;
            self.MuteGuard = i => Allow(RouteGuard.CheckMute(Snapshot(), i));
            self.RouteGuard = (i, o) => Allow(RouteGuard.CheckUnroute(Snapshot(), i, o));
            for (int o = 0; o < self.Routes.Length; o++)
            {
                int output = o;
                self.Routes[o].Guard = _ => Allow(RouteGuard.CheckUnroute(Snapshot(), self.Index, output));
            }
        }
    }

    private SceneController _scenes = null!;

    private void Rig()
    {
        // ch0 is the lapel and has the LOWER index, which is what makes it get written first.
        _f.Channels[0].CustomLabel = "LAPEL";
        _f.Channels[0].Role = ChannelRole.Lapel;
        _f.Channels[0].SelectedDevice = VmFixture.Lapel;
        _f.Channels[1].CustomLabel = "Rode L";
        _f.Channels[1].Role = ChannelRole.Room;
        _f.Channels[1].SelectedDevice = VmFixture.Cable;
        _f.Channels[2].CustomLabel = "Rode R";
        _f.Channels[2].Role = ChannelRole.Room;
        _f.Channels[2].SelectedDevice = VmFixture.Cable;

        _scenes = new SceneController(_f.Channels, _f.Outputs);
        WireGuards();
    }

    [Fact]
    public void GoingFromSingingToPrayerActuallySilencesTheLapel()
    {
        Rig();
        _scenes.Apply(Scene.Singing);
        _scenes.VoiceSource = VoiceSource.Lapel;
        Assert.True(_f.Channels[0].Routes.Any(r => r.IsOn), "precondition: the lapel is the source");

        _scenes.Apply(Scene.Prayer);

        Assert.True(_f.Channels[0].Muted, "the lapel is still live on the stream during prayer");
        Assert.False(_f.Channels[0].IsPriority);
        Assert.All(_f.Channels[0].Routes, r => Assert.False(r.IsOn));
    }

    /// <summary>The end state must still be a rig that can carry the meeting, not merely one the
    /// guard permitted — the room mics have to take over.</summary>
    [Fact]
    public void PrayerLeavesTheRoomMicsCoveringEveryBus()
    {
        Rig();
        _scenes.Apply(Scene.Singing);
        _scenes.VoiceSource = VoiceSource.Lapel;

        _scenes.Apply(Scene.Prayer);

        for (int o = 0; o < _f.Outputs.Count; o++)
        {
            bool covered = _f.Channels.Any(c => !c.Muted && c.SelectedDevice != null && c.Routes[o].IsOn);
            Assert.True(covered, $"bus {o} has nothing live on it after Prayer");
        }
    }

    /// <summary>
    /// The guard is only stood down FOR the scene write. A direct operator toggle afterwards must
    /// still be refused, or suspending it for scenes would have quietly removed the protection the
    /// clickable A/B buttons were shipped with.
    /// </summary>
    [Fact]
    public void TheGuardIsBackInForceAfterTheSceneHasApplied()
    {
        Rig();
        _scenes.Apply(Scene.Teaching);
        Assert.False(_scenes.IsApplying);

        // Leave exactly one mic covering bus A, then try to take it away by hand.
        var live = _f.Channels.Where(c => !c.Muted && c.Routes[0].IsOn).ToList();
        foreach (var c in live.Skip(1)) c.Routes[0].IsOn = false;
        var last = _f.Channels.First(c => !c.Muted && c.Routes[0].IsOn);

        last.Routes[0].IsOn = false;

        Assert.True(last.Routes[0].IsOn, "the last mic on bus A was allowed to leave it");
    }

    [Theory]
    [InlineData(Scene.Teaching)]
    [InlineData(Scene.Prayer)]
    [InlineData(Scene.Singing)]
    public void EverySceneLeavesEveryBusCovered(Scene scene)
    {
        Rig();

        _scenes.Apply(scene);

        for (int o = 0; o < _f.Outputs.Count; o++)
        {
            bool covered = _f.Channels.Any(c => !c.Muted && c.SelectedDevice != null && c.Routes[o].IsOn);
            Assert.True(covered, $"{scene} leaves bus {o} with nothing live on it");
        }
    }
}
