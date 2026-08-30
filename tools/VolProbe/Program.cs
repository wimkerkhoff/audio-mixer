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

foreach (var d in devs)
{
    if (!d.FriendlyName.Contains("Wireless PRO", StringComparison.OrdinalIgnoreCase)
        && !d.FriendlyName.Contains("ANKER", StringComparison.OrdinalIgnoreCase)) continue;
    var v = d.AudioEndpointVolume;
    Console.WriteLine($"  {d.FriendlyName,-40} {v.MasterVolumeLevel,6:F1} dB  ({v.MasterVolumeLevelScalar * 100,5:F1}%)");
}
