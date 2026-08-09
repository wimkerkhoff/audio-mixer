using AudioMixer.Audio;
using AudioMixer.Models;
using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// Scene rules are the riskiest operator-facing surface: they rewrite every channel and output at
/// once, and a wrong rule shows up as the congregation silently dropping off the stream mid-service.
/// These assert the rules that were learned the hard way on real hardware.
/// </summary>
public class SceneTransformTests
{
    private const int Outs = 2;

    private static ChannelPlan Ch(int i, ChannelRole role, bool routed = true, bool muted = false, bool priority = false)
        => new(i, role, Enumerable.Repeat(routed, Outs).ToArray(), muted, priority);

    private static OutputPlan Out(int i) => new(i, AutoMixMode.Share, true, true, false, false);

    /// <summary>A lapel plus four room mics — the real rig.</summary>
    private static MixerPlan Rig(bool withLapel = true)
    {
        var channels = new List<ChannelPlan>();
        if (withLapel) channels.Add(Ch(0, ChannelRole.Lapel, priority: true));
        for (int i = channels.Count; i < (withLapel ? 5 : 4); i++) channels.Add(Ch(i, ChannelRole.Room));
        return new MixerPlan(channels.ToArray(), Enumerable.Range(0, Outs).Select(Out).ToArray());
    }

    private static ChannelPlan Lapel(MixerPlan p) => p.Channels.Single(c => c.Role == ChannelRole.Lapel);
    private static IEnumerable<ChannelPlan> Room(MixerPlan p) => p.Channels.Where(c => c.Role == ChannelRole.Room);

    // --- Standby ---------------------------------------------------------------------------------

    [Fact]
    public void Standby_MutesEveryOutput()
    {
        var r = SceneTransform.Apply(Scene.Standby, VoiceSource.Auto, Rig());
        Assert.All(r.Outputs, o => Assert.True(o.Muted));
    }

    [Fact]
    public void Standby_LeavesChannelsAloneSoLeavingItRestoresTheScene()
    {
        var before = Rig();
        var r = SceneTransform.Apply(Scene.Standby, VoiceSource.Auto, before);
        Assert.Equal(before.Channels, r.Channels);
    }

    // --- Prayer ----------------------------------------------------------------------------------

    [Fact]
    public void Prayer_DisarmsTheLapelCompletely()
    {
        // The priority-duck hazard: an idle open lapel crossing -40 dBFS ducks every room mic off the
        // stream. Prayer never uses a lapel, so all three defences are applied.
        var lapel = Lapel(SceneTransform.Apply(Scene.Prayer, VoiceSource.Auto, Rig()));
        Assert.False(lapel.IsPriority);
        Assert.True(lapel.Muted);
        Assert.All(lapel.Routes, r => Assert.False(r));
    }

    [Fact]
    public void Prayer_UsesGateAndDisablesPreferNatural()
    {
        // Share sums several mics hearing one voice and combs; prefer-natural pins the globally
        // lowest-CV mic regardless of who is speaking (confirmed live 2026-07-26).
        var r = SceneTransform.Apply(Scene.Prayer, VoiceSource.Auto, Rig());
        Assert.All(r.Outputs, o =>
        {
            Assert.Equal(AutoMixMode.Gate, o.Mode);
            Assert.False(o.PreferNatural);
            Assert.False(o.ReferenceGuided);
            Assert.True(o.StableHandoff);
            Assert.False(o.Muted);
        });
    }

    [Fact]
    public void Prayer_KeepsRoomMicsLive()
    {
        var r = SceneTransform.Apply(Scene.Prayer, VoiceSource.Auto, Rig());
        Assert.All(Room(r), c => { Assert.All(c.Routes, x => Assert.True(x)); Assert.False(c.Muted); });
    }

    // --- Teaching --------------------------------------------------------------------------------

    [Fact]
    public void Teaching_LapelIsPriorityAndRoomMicsStayRouted()
    {
        var r = SceneTransform.Apply(Scene.Teaching, VoiceSource.Auto, Rig());
        Assert.True(Lapel(r).IsPriority);
        Assert.All(Lapel(r).Routes, x => Assert.True(x));
        // Room mics remain in so the priority duck can do its anti-comb job.
        Assert.All(Room(r), c => Assert.All(c.Routes, x => Assert.True(x)));
        Assert.All(r.Outputs, o => Assert.Equal(AutoMixMode.Gate, o.Mode));
    }

    [Fact]
    public void Teaching_RoomOverride_TakesTheLapelOutAndClearsPriority()
    {
        var r = SceneTransform.Apply(Scene.Teaching, VoiceSource.RoomMics, Rig());
        Assert.False(Lapel(r).IsPriority);
        Assert.All(Lapel(r).Routes, x => Assert.False(x));
        Assert.All(Room(r), c => Assert.All(c.Routes, x => Assert.True(x)));
    }

    // --- Singing ---------------------------------------------------------------------------------

    [Fact]
    public void Singing_SuspendsPriorityDuckingOnEveryChannel()
    {
        // The 2026-07-05 failure: worship reached the stream as pastor-only because the priority lapel
        // ducked every room mic to zero. Nothing may stay priority in this scene.
        var r = SceneTransform.Apply(Scene.Singing, VoiceSource.RoomMics, Rig());
        Assert.All(r.Channels, c => Assert.False(c.IsPriority));
    }

    [Fact]
    public void Singing_TurnsAutomixOffRatherThanPickingAMic()
    {
        // Measured 2026-08-09: the Ankers gate singing to digital silence in unison, so no selection
        // rule helps. The scene must not pretend otherwise by leaving Gate or prefer-natural on.
        var r = SceneTransform.Apply(Scene.Singing, VoiceSource.RoomMics, Rig());
        Assert.All(r.Outputs, o =>
        {
            Assert.Equal(AutoMixMode.Off, o.Mode);
            Assert.False(o.PreferNatural);
            Assert.False(o.Muted);
        });
    }

    [Fact]
    public void Singing_LapelPath_TakesTheRoomMicsOut()
    {
        var r = SceneTransform.Apply(Scene.Singing, VoiceSource.Lapel, Rig());
        Assert.All(Lapel(r).Routes, x => Assert.True(x));
        Assert.False(Lapel(r).Muted);
        Assert.All(Room(r), c => Assert.All(c.Routes, x => Assert.False(x)));
    }

    // --- Safety ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(Scene.Teaching)]
    [InlineData(Scene.Singing)]
    public void LapelOverrideWithNoLapelChannel_FallsBackToRoomMics(Scene scene)
    {
        // Honouring "Lapel" with no lapel present would unroute everything and put silence on air.
        var r = SceneTransform.Apply(scene, VoiceSource.Lapel, Rig(withLapel: false));
        Assert.All(Room(r), c => Assert.All(c.Routes, x => Assert.True(x)));
    }

    [Theory]
    [InlineData(Scene.Teaching)]
    [InlineData(Scene.Prayer)]
    [InlineData(Scene.Singing)]
    public void EveryLiveScene_LeavesSomethingRoutedAndUnmuted(Scene scene)
    {
        foreach (var source in new[] { VoiceSource.Auto, VoiceSource.Lapel, VoiceSource.RoomMics })
        foreach (var rig in new[] { Rig(), Rig(withLapel: false) })
        {
            var r = SceneTransform.Apply(scene, source, rig);
            Assert.True(r.Channels.Any(c => !c.Muted && c.Routes.Any(x => x)),
                $"{scene}/{source} produced a silent stream — no channel routed and unmuted.");
            Assert.All(r.Outputs, o => Assert.False(o.Muted));
        }
    }

    [Fact]
    public void Describe_CoversEverySceneAndOverride()
    {
        foreach (Scene s in Enum.GetValues<Scene>())
        foreach (VoiceSource v in Enum.GetValues<VoiceSource>())
            Assert.False(string.IsNullOrWhiteSpace(SceneTransform.Describe(s, v)));
    }
}
