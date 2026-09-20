namespace AudioMixer.Services;

/// <summary>One channel, as far as routing safety is concerned.</summary>
/// <param name="Dead">
/// Bound but delivering nothing (stalled capture, transmitter off). A dead mic does not count as
/// covering a bus — otherwise the guard happily lets the last working mic go while a corpse "holds"
/// the output, which is the silent-stream failure wearing a disguise.
/// </param>
public sealed record ChannelRouting(
    int Index,
    string Label,
    bool[] Routes,
    bool Muted,
    bool HasDevice,
    bool Dead);

/// <param name="Reason">Operator-facing, and names the bus rather than the rule. Null when allowed.</param>
public readonly record struct RouteVerdict(bool Allowed, string? Reason)
{
    public static RouteVerdict Ok => new(true, null);
}

/// <summary>
/// Whether a manual routing or mute change is safe to apply.
///
/// Scenes already hold the invariant that no scene/override combination can leave the stream with
/// nothing on it — 20 unit tests protect it. Making A/B and mute clickable in the operator panel
/// opened a path straight around those tests: a volunteer can now reach the same silent-stream state
/// one tap at a time. This is that invariant, restated for manual changes.
///
/// Pure, so the rules are testable without devices or windows — the same reason SceneTransform and
/// HealthMonitor are. The rule is deliberately narrow: it blocks the last live source leaving a bus
/// and nothing else. It is NOT a general policy engine, and it must never block a change that merely
/// looks unwise, because an operator who is fought by the UI stops trusting it.
/// </summary>
public static class RouteGuard
{
    /// <summary>A channel is covering an output if it is routed there, unmuted, bound and alive.</summary>
    public static bool Contributes(ChannelRouting c, int output) =>
        c.HasDevice && !c.Muted && !c.Dead
        && output >= 0 && output < c.Routes.Length && c.Routes[output];

    public static int CoverCount(IReadOnlyList<ChannelRouting> channels, int output) =>
        channels.Count(c => Contributes(c, output));

    /// <summary>Unroute <paramref name="index"/> from <paramref name="output"/>.</summary>
    public static RouteVerdict CheckUnroute(
        IReadOnlyList<ChannelRouting> channels, int index, int output) =>
        Check(channels, index, c => Without(c, output));

    /// <summary>Mute <paramref name="index"/>, which drops it off every bus it feeds at once.</summary>
    public static RouteVerdict CheckMute(IReadOnlyList<ChannelRouting> channels, int index) =>
        Check(channels, index, c => c with { Muted = true });

    private static ChannelRouting Without(ChannelRouting c, int output)
    {
        var routes = (bool[])c.Routes.Clone();
        if (output >= 0 && output < routes.Length) routes[output] = false;
        return c with { Routes = routes };
    }

    private static RouteVerdict Check(
        IReadOnlyList<ChannelRouting> channels, int index, Func<ChannelRouting, ChannelRouting> change)
    {
        if (index < 0 || index >= channels.Count) return RouteVerdict.Ok;

        var before = channels[index];
        var after = change(before);
        int outputs = before.Routes.Length;

        for (int o = 0; o < outputs; o++)
        {
            // Only a 1 -> 0 transition is blocked. An output that is already uncovered cannot be made
            // worse, and blocking there would trap an operator who is mid-way through rearranging.
            if (Contributes(before, o) && !Contributes(after, o) && CoverCount(channels, o) == 1)
            {
                return new RouteVerdict(false,
                    $"Bus {OutputLetter(o)} would have no microphone. Switch another one on first.");
            }
        }
        return RouteVerdict.Ok;
    }

    public static string OutputLetter(int output) => ((char)('A' + output)).ToString();
}
