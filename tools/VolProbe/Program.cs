using NAudio.CoreAudioApi;

// Reports (and optionally sets) the Windows endpoint level for capture devices.
// Usage: VolProbe [<match> <dB>]
var en = new MMDeviceEnumerator();
var devs = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).OrderBy(d => d.FriendlyName).ToList();

if (args.Length == 2)
{
    var target = devs.First(d => d.FriendlyName.Contains(args[0], StringComparison.OrdinalIgnoreCase));
    float db = float.Parse(args[1]);
    var r = target.AudioEndpointVolume.VolumeRange;
    db = Math.Clamp(db, r.MinDecibels, r.MaxDecibels);
    float before = target.AudioEndpointVolume.MasterVolumeLevel;
    target.AudioEndpointVolume.MasterVolumeLevel = db;
    Console.WriteLine($"{target.FriendlyName}: {before:F1} dB -> {target.AudioEndpointVolume.MasterVolumeLevel:F1} dB "
                    + $"(slider now {target.AudioEndpointVolume.MasterVolumeLevelScalar * 100:F0}%)");
}

// Every active capture endpoint, not a hardcoded shortlist: the gain that matters is whichever
// device the mic is actually on, and this rig's mics have already moved from Anker to Rode to the
// Realtek aux jack. An optional first arg filters by name.
string? filter = args.Length == 1 ? args[0] : null;
// Bus type comes from the endpoint property store; BTHENUM is the only value that means Bluetooth.
// Whether the endpoint keeps its identity across a USB port change is NOT answerable here --
// PKEY_Device_InstanceId is not exposed on an endpoint's property store (it reads empty for every
// device, USB included). That needs the PnP tree: see tools/device-identity.ps1.
var enumKey = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

static string Read(MMDevice d, PropertyKey k)
{
    try { return d.Properties.Contains(k) ? d.Properties[k].Value?.ToString() ?? "-" : "-"; }
    catch { return "-"; }
}

foreach (var d in devs)
{
    if (filter != null && !d.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    var v = d.AudioEndpointVolume;
    Console.WriteLine($"  {d.FriendlyName,-40} {v.MasterVolumeLevel,6:F1} dB  ({v.MasterVolumeLevelScalar * 100,5:F1}%)");
    Console.WriteLine($"      bus={Read(d, enumKey)}");
}
