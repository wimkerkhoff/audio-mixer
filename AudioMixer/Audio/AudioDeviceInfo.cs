using NAudio.CoreAudioApi;

namespace AudioMixer.Audio;

/// <param name="Bus">
/// The Windows device-enumerator name: BTHENUM, USB, HDAUDIO, ROOT (virtual), or null when it could
/// not be read. This is how to tell a Bluetooth endpoint from a wired one — the friendly name cannot,
/// because "Headset Microphone (Lync USB Headset)" is a USB device whose name says Headset.
/// </param>
public sealed record AudioDeviceInfo(string Id, string FriendlyName, DataFlow Flow, string? Bus = null)
{
    public const string BluetoothBus = "BTHENUM";

    public override string ToString() => FriendlyName;

    public bool IsBluetooth => string.Equals(Bus, BluetoothBus, StringComparison.OrdinalIgnoreCase);

    // PKEY_Device_EnumeratorName {a45c254e-df1c-4efd-8020-67d146a850e0},24
    private static readonly PropertyKey EnumeratorNameKey =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    public static List<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, flow, ReadBus(device)));
        }
        return result;
    }

    // A property store that is missing the key or throws is not worth failing enumeration over; the
    // health rule falls back to a (deliberately narrow) name test when the bus is unknown.
    private static string? ReadBus(MMDevice device)
    {
        try
        {
            return device.Properties.Contains(EnumeratorNameKey)
                ? device.Properties[EnumeratorNameKey].Value?.ToString()
                : null;
        }
        catch { return null; }
    }

    public MMDevice? Resolve()
    {
        using var enumerator = new MMDeviceEnumerator();
        try { return enumerator.GetDevice(Id); }
        catch { return null; }
    }
}
