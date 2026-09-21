using System.Text.RegularExpressions;
using AudioMixer.Audio;

namespace AudioMixer.Services;

// Matches a preset's saved device against the live endpoint list.
public static class DeviceResolver
{
    // The volatile "N- " endpoint enumerator Windows prepends inside the interface name (e.g. "(2- Anker
    // Soundsync)"); it reshuffles across reboots so it must be ignored when matching a saved name.
    private static readonly Regex EnumeratorPrefix = new(@"\(\d+-\s*", RegexOptions.Compiled);

    // Saved DeviceId is the WASAPI endpoint GUID, which Windows regenerates whenever a USB audio device
    // re-enumerates (a driver-update reboot, a replug, a different port) — so an exact-ID match silently
    // drops every hot-plug mic/headset and forces a manual remap. Fall back to the saved friendly name,
    // normalized (only the "(2-/5- …)" enumerator stripped — Windows re-applies a device rename to the new
    // endpoint, and for un-renamed devices the identifying part is the interface name inside the parens,
    // so we must NOT truncate to the prefix). Callers must pass the full master device list, not a
    // channel's pre-dedup AvailableDevices (which can be missing a device mid-apply); `used` stops two
    // channels grabbing the same device when several normalize alike (e.g. identical un-renamed dongles).
    public static AudioDeviceInfo? Resolve(
        IEnumerable<AudioDeviceInfo> all, string? id, string? name, HashSet<string> used) =>
        Resolve(all, id, name, used, ChannelSource.Stereo, null);

    /// <summary>
    /// As above, but claiming only one SIDE of the endpoint when <paramref name="side"/> is Left or
    /// Right — two channels legitimately resolve to the same device when they read opposite
    /// transmitters of a split receiver. A Stereo claim still takes the endpoint whole and conflicts
    /// with either side.
    /// </summary>
    public static AudioDeviceInfo? Resolve(
        IEnumerable<AudioDeviceInfo> all, string? id, string? name, HashSet<string> used,
        ChannelSource side, string? key = null)
    {
        bool Free(AudioDeviceInfo d) => IsFree(used, d.Id, side);

        AudioDeviceInfo? match = null;

        // The container id FIRST, and only when it is serial-derived, because it is the one key that
        // both survives a port change and separates two units of the same model. Everything below it
        // fails at one or the other: the endpoint GUID is regenerated on every replug, and the friendly
        // name is shared by identical receivers — which is why name matching can only ever refuse.
        // Matching it also costs nothing when it is absent, which is every device that has no serial.
        if (!string.IsNullOrEmpty(key) && Guid.TryParse(key, out var wanted))
            match = Unambiguous(all.Where(d => d.ContainerId == wanted && Free(d)));

        if (match == null && !string.IsNullOrEmpty(id))
            match = all.FirstOrDefault(d => d.Id == id && Free(d));
        // Name matching REFUSES when it cannot tell candidates apart. Two identical receivers share a
        // friendly name, so picking the first free one binds an arbitrary unit — and with each receiver
        // covering its own part of the room that is the wrong mic in the wrong place, silently, which
        // is precisely the never-greedy-fill lesson the Ankers taught. Nothing bound is recoverable
        // (the operator is told by the routed-but-unbound health rule, and maps them once); the wrong
        // mic bound is not, because nothing about it looks wrong.
        if (match == null && !string.IsNullOrWhiteSpace(name))
        {
            var nameKey = NameKey(name);
            match = Unambiguous(all.Where(d => Free(d) && NameKey(d.FriendlyName) == nameKey));
        }
        // Last resort: the INTERFACE name inside the parens, and only when exactly one free endpoint
        // carries it. A different USB port mints a fresh endpoint that loses the user's rename and can
        // carry a different role prefix ("Desktop Microphone (Wireless PRO RX)" vs "Microphone
        // (Wireless PRO RX)"), which defeats the full-name match above. This is the opposite of the
        // documented mis-bind hazard: that was truncating TO the role prefix, where "Speakers (Lync USB
        // Headset)" and "Speakers (Realtek(R) Audio)" collide. Interface names are far more specific —
        // but a multi-jack device exposes several endpoints under ONE interface name, so ambiguity here
        // must refuse rather than guess, or a mic binds to a line input that happens to sort first.
        if (match == null && !string.IsNullOrWhiteSpace(name))
        {
            var iface = InterfaceKey(name);
            if (iface != null)
            {
                match = Unambiguous(all.Where(d => Free(d) && InterfaceKey(d.FriendlyName) == iface));
            }
        }
        if (match != null) used.Add(Claim(match.Id, side));
        return match;
    }

    /// <summary>The single candidate, or null when there is a choice to be made and no way to make it.</summary>
    private static AudioDeviceInfo? Unambiguous(IEnumerable<AudioDeviceInfo> candidates)
    {
        // Two strips legitimately resolve to the SAME endpoint when they read opposite transmitters of
        // a split receiver, so count distinct endpoints rather than rows.
        var distinct = candidates.GroupBy(d => d.Id).Take(2).ToList();
        return distinct.Count == 1 ? distinct[0].First() : null;
    }

    /// <summary>Can <paramref name="side"/> of <paramref name="id"/> still be claimed?</summary>
    public static bool IsFree(IReadOnlySet<string> used, string id, ChannelSource side)
    {
        if (used.Contains(Claim(id, ChannelSource.Stereo))) return false;
        return side == ChannelSource.Stereo
            ? !used.Contains(Claim(id, ChannelSource.Left)) && !used.Contains(Claim(id, ChannelSource.Right))
            : !used.Contains(Claim(id, side));
    }

    // A whole-endpoint claim keeps the bare id as its key, so presets and outputs (which never split)
    // behave exactly as before.
    public static string Claim(string id, ChannelSource side) =>
        side == ChannelSource.Stereo ? id : $"{id}|{(int)side}";

    public static string NameKey(string friendlyName) =>
        EnumeratorPrefix.Replace(friendlyName, "(").Trim();

    /// <summary>
    /// The interface name inside the trailing parens — "Speakers (Realtek(R) Audio)" gives
    /// "Realtek(R) Audio". Spans the FIRST " (" to the LAST ")", because interface names contain
    /// parens of their own. Null when the name has no parenthesised part to read.
    /// </summary>
    public static string? InterfaceKey(string friendlyName)
    {
        var s = NameKey(friendlyName);
        int open = s.IndexOf(" (", StringComparison.Ordinal);
        int close = s.LastIndexOf(')');
        if (open < 0 || close <= open + 2) return null;
        var inner = s.Substring(open + 2, close - open - 2).Trim();
        return inner.Length == 0 ? null : inner;
    }
}
