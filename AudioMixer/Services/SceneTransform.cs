using AudioMixer.Audio;
using AudioMixer.Models;

namespace AudioMixer.Services;

/// <summary>Everything a scene may change on one input channel.</summary>
public sealed record ChannelPlan(int Index, ChannelRole Role, bool[] Routes, bool Muted, bool IsPriority)
{
    public ChannelPlan With(bool? routed = null, bool? muted = null, bool? priority = null)
    {
        var routes = routed == null ? Routes : Enumerable.Repeat(routed.Value, Routes.Length).ToArray();
        return this with { Routes = routes, Muted = muted ?? Muted, IsPriority = priority ?? IsPriority };
    }
}

/// <summary>Everything a scene may change on one output bus.</summary>
public sealed record OutputPlan(
    int Index, AutoMixMode Mode, bool PreferNatural, bool ReferenceGuided, bool StableHandoff, bool Muted);

public sealed record MixerPlan(ChannelPlan[] Channels, OutputPlan[] Outputs);

/// <summary>
/// Scene rules as a pure function: (scene, override, current state) -> new state.
///
/// Kept free of view models, WPF and audio on purpose. Scenes are the riskiest new surface in the
/// operator UI because they *write* operator state across every channel and output at once, and a
/// wrong rule is invisible until it silently drops the congregation off the stream mid-service. As a
/// pure function the whole rule set is unit-testable with no devices and no room.
/// </summary>
public static class SceneTransform
{
    public static MixerPlan Apply(Scene scene, VoiceSource source, MixerPlan current)
    {
        bool hasLapel = current.Channels.Any(c => c.Role == ChannelRole.Lapel);

        // Falling back to room mics when no lapel exists is a safety property, not a nicety: honouring
        // "Lapel" with no lapel channel would unroute every mic and put silence on the stream.
        var resolved = source switch
        {
            VoiceSource.Lapel when hasLapel => VoiceSource.Lapel,
            VoiceSource.Lapel => VoiceSource.RoomMics,
            VoiceSource.RoomMics => VoiceSource.RoomMics,
            _ => hasLapel ? VoiceSource.Lapel : VoiceSource.RoomMics,
        };

        return scene switch
        {
            Scene.Standby => Standby(current),
            Scene.Teaching => Teaching(current, resolved),
            Scene.Prayer => Prayer(current),
            Scene.Singing => Singing(current, resolved),
            _ => current,
        };
    }

    // Mute the buses and touch nothing else, so leaving Standby restores the scene you were in rather
    // than forcing a reconfigure.
    private static MixerPlan Standby(MixerPlan c) =>
        c with { Outputs = c.Outputs.Select(o => o with { Muted = true }).ToArray() };

    private static MixerPlan Teaching(MixerPlan c, VoiceSource source)
    {
        var channels = c.Channels.Select(ch => ch.Role switch
        {
            // On the lapel path the room mics stay routed and the priority duck handles the overlap;
            // that ducking is what stops one voice reaching the bus via both a clean lapel and a
            // delayed room mic, which would comb-filter.
            ChannelRole.Lapel when source == VoiceSource.Lapel => ch.With(routed: true, muted: false, priority: true),
            ChannelRole.Lapel => ch.With(routed: false, muted: false, priority: false),
            _ => ch.With(routed: true, muted: false, priority: false),
        }).ToArray();

        return new MixerPlan(channels, c.Outputs.Select(o => o with
        {
            Mode = AutoMixMode.Gate,
            PreferNatural = false,
            ReferenceGuided = false,
            StableHandoff = true,
            Muted = false,
        }).ToArray());
    }

    private static MixerPlan Prayer(MixerPlan c)
    {
        var channels = c.Channels.Select(ch => ch.Role == ChannelRole.Lapel
            // Muted AND unrouted AND de-prioritised: no lapel is ever used in prayer, and an idle open
            // priority mic that drifts over -40 dBFS ducks every room mic off the stream with no
            // visible cause.
            ? ch.With(routed: false, muted: true, priority: false)
            : ch.With(routed: true, muted: false, priority: false)).ToArray();

        return new MixerPlan(channels, c.Outputs.Select(o => o with
        {
            Mode = AutoMixMode.Gate,
            PreferNatural = false,
            ReferenceGuided = false,
            StableHandoff = true,
            Muted = false,
        }).ToArray());
    }

    private static MixerPlan Singing(MixerPlan c, VoiceSource source)
    {
        var channels = c.Channels.Select(ch => ch.Role switch
        {
            ChannelRole.Lapel when source == VoiceSource.Lapel => ch.With(routed: true, muted: false, priority: false),
            ChannelRole.Lapel => ch.With(routed: false, muted: false, priority: false),
            // On the lapel path the room mics come out entirely — with no priority duck to hold them
            // off, leaving them in just adds four gating speakerphones under a clean mic.
            _ => ch.With(routed: source != VoiceSource.Lapel, muted: false, priority: false),
        }).ToArray();

        return new MixerPlan(channels, c.Outputs.Select(o => o with
        {
            // Off, not Gate: singing has no single talker, so follow-the-talker has nothing to follow.
            Mode = AutoMixMode.Off,
            PreferNatural = false,
            ReferenceGuided = false,
            Muted = false,
        }).ToArray());
    }

    /// <summary>One-line plain-English summary of what a scene will do, for the confirm/undo affordance.</summary>
    public static string Describe(Scene scene, VoiceSource source) => scene switch
    {
        Scene.Standby => "Outputs muted — nothing reaches Zoom or the recording.",
        Scene.Teaching => source == VoiceSource.RoomMics
            ? "Room mics, follow-the-talker (Gate). Lapel out."
            : "Lapel leads and ducks the room; room mics follow the talker (Gate).",
        Scene.Prayer => "Room mics, turn-taking (Gate). Lapel muted and unrouted.",
        Scene.Singing => source == VoiceSource.RoomMics
            ? "Room mics open and flat, no ducking, no follow-the-talker."
            : "Lapel only, flat. Room mics out, ducking suspended.",
        _ => "",
    };
}
