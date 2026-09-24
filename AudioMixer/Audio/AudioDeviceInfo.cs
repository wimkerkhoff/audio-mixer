using NAudio.CoreAudioApi;

namespace AudioMixer.Audio;

/// <param name="Bus">
/// The Windows device-enumerator name: BTHENUM, USB, HDAUDIO, ROOT (virtual), or null when it could
/// not be read. This is how to tell a Bluetooth endpoint from a wired one — the friendly name cannot,
/// because "Headset Microphone (Lync USB Headset)" is a USB device whose name says Headset.
/// </param>
/// <param name="GainDb">
/// The Windows endpoint level in dB, as of the last enumeration, or null if it could not be read.
/// Cached deliberately: this is read once per device-change refresh, never per request, because
/// enumeration costs seconds and `/state` marshals to the UI thread. It therefore goes STALE when
/// someone moves the Windows slider, since property-value notifications are ignored on purpose (they
/// fire constantly). Treat it as "what it was when the device list was last built".
///
/// Worth having at all because a gain mismatch across a matched set is invisible from audio levels
/// alone and has twice been the root cause of "these mics are dead": a receiver on a new USB port
/// gets the 0 dB default while its twin sits 24 dB higher.
/// </param>
public sealed record AudioDeviceInfo(string Id, string FriendlyName, DataFlow Flow, string? Bus = null,
                                     Guid? ContainerId = null, float? GainDb = null)
{
    public const string BluetoothBus = "BTHENUM";

    public override string ToString() => FriendlyName;

    public bool IsBluetooth => string.Equals(Bus, BluetoothBus, StringComparison.OrdinalIgnoreCase);

    // PKEY_Device_EnumeratorName {a45c254e-df1c-4efd-8020-67d146a850e0},24
    private static readonly PropertyKey EnumeratorNameKey =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    // PKEY_Device_ContainerId {8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c},2 — the only identity key the
    // audio api actually exposes (PKEY_Device_InstanceId reads empty on every endpoint here), and its
    // UUID version says whether it came from the device's serial. See Services.DeviceIdentity.
    private static readonly PropertyKey ContainerIdKey =
        new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    public static List<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, flow, ReadBus(device),
                                           ReadContainer(device), ReadGainDb(device)));
        }
        return result;
    }

    // Endpoint volume is a separate COM interface from the property store and is not present on every
    // endpoint; a failure here must not cost us the device itself.
    private static float? ReadGainDb(MMDevice device)
    {
        try { return device.AudioEndpointVolume.MasterVolumeLevel; }
        catch { return null; }
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

    private static Guid? ReadContainer(MMDevice device)
    {
        try
        {
            if (!device.Properties.Contains(ContainerIdKey)) return null;
            var raw = device.Properties[ContainerIdKey].Value;
            if (raw is Guid g) return g;
            return Guid.TryParse(raw?.ToString(), out var parsed) ? parsed : null;
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
