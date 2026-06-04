using System.Collections.ObjectModel;
using AudioMixer.Audio;

namespace AudioMixer.ViewModels;

internal static class DeviceList
{
    // Reconciles `available` in place to match `devices` — drops endpoints that vanished, appends
    // new ones, and leaves survivors untouched (so a bound ComboBox keeps its selection/order).
    // Returns true if `currentSelectedId` is still present afterwards; the caller clears its
    // selection when it isn't.
    public static bool Sync(
        ObservableCollection<AudioDeviceInfo> available,
        IEnumerable<AudioDeviceInfo> devices,
        string? currentSelectedId)
    {
        var newList = devices as IList<AudioDeviceInfo> ?? devices.ToList();
        var newIds = new HashSet<string>(newList.Select(d => d.Id));

        for (int i = available.Count - 1; i >= 0; i--)
        {
            if (!newIds.Contains(available[i].Id)) available.RemoveAt(i);
        }
        var existingIds = new HashSet<string>(available.Select(d => d.Id));
        foreach (var d in newList)
        {
            if (!existingIds.Contains(d.Id)) available.Add(d);
        }

        return currentSelectedId != null && newIds.Contains(currentSelectedId);
    }
}
