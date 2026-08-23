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
        Resolve(all, id, name, used, ChannelSource.Stereo);

    /// <summary>
    /// As above, but claiming only one SIDE of the endpoint when <paramref name="side"/> is Left or
    /// Right — two channels legitimately resolve to the same device when they read opposite
    /// transmitters of a split receiver. A Stereo claim still takes the endpoint whole and conflicts
    /// with either side.
    /// </summary>
    public static AudioDeviceInfo? Resolve(
        IEnumerable<AudioDeviceInfo> all, string? id, string? name, HashSet<string> used,
        ChannelSource side)
    {
        bool Free(AudioDeviceInfo d) => IsFree(used, d.Id, side);

        AudioDeviceInfo? match = null;
        if (!string.IsNullOrEmpty(id))
            match = all.FirstOrDefault(d => d.Id == id && Free(d));
        if (match == null && !string.IsNullOrWhiteSpace(name))
        {
            var key = NameKey(name);
            match = all.FirstOrDefault(d => Free(d) && NameKey(d.FriendlyName) == key);
        }
        if (match != null) used.Add(Claim(match.Id, side));
        return match;
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
}
