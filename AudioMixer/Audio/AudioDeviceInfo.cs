using NAudio.CoreAudioApi;

namespace AudioMixer.Audio;

public sealed record AudioDeviceInfo(string Id, string FriendlyName, DataFlow Flow)
{
    public override string ToString() => FriendlyName;

    public static List<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, flow));
        }
        return result;
    }

    public MMDevice? Resolve()
    {
        using var enumerator = new MMDeviceEnumerator();
        try { return enumerator.GetDevice(Id); }
        catch { return null; }
    }
}
