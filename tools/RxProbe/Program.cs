using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// Captures two or more WASAPI endpoints simultaneously (shared mode, so it runs alongside the live
// mixer) to WAV, for sample-level comparison offline. Originally written to test whether the Realtek
// "R0de wireless" channel was the same receiver's 3.5 mm output as the USB "Wireless PRO RX"
// endpoint; the endpoints are now arguments so any two devices can be compared in the same room at
// the same instant — which is the only way to judge one mic against another (finding 7: a level
// comparison across device types is meaningless unless the acoustic input is identical).
//
//   RxProbe [seconds] [outDir] [endpointNameSubstring ...]

int seconds = args.Length > 0 ? int.Parse(args[0]) : 12;
string outDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "rxprobe");
Directory.CreateDirectory(outDir);

var en = new MMDeviceEnumerator();
var devs = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
foreach (var d in devs) Console.WriteLine($"  [{d.ID}] {d.FriendlyName}");

MMDevice Pick(string needle)
{
    var d = devs.FirstOrDefault(x => x.FriendlyName.Contains(needle, StringComparison.OrdinalIgnoreCase));
    if (d == null) throw new Exception("no capture device matching: " + needle);
    return d;
}

static string Slug(string s)
{
    var chars = s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
    return new string(chars);
}

var targets = args.Length > 2
    ? args.Skip(2).Select(a => (needle: a, name: Slug(a))).ToArray()
    : new (string needle, string name)[] { ("Wireless PRO RX", "rx_usb"), ("R0de wireless", "realtek_aux") };
var caps = new List<(WasapiCapture cap, WaveFileWriter w)>();

foreach (var (needle, name) in targets)
{
    var dev = Pick(needle);
    var cap = new WasapiCapture(dev) { ShareMode = AudioClientShareMode.Shared };
    string path = Path.Combine(outDir, name + ".wav");
    var w = new WaveFileWriter(path, cap.WaveFormat);
    var wLocal = w;
    cap.DataAvailable += (s, e) => { lock (wLocal) wLocal.Write(e.Buffer, 0, e.BytesRecorded); };
    caps.Add((cap, w));
    Console.WriteLine($"{name}: {dev.FriendlyName} | {cap.WaveFormat} -> {path}");
}

foreach (var (cap, _) in caps) cap.StartRecording();
Console.WriteLine($"recording {seconds}s ...");
Thread.Sleep(seconds * 1000);
foreach (var (cap, w) in caps) { cap.StopRecording(); Thread.Sleep(150); lock (w) w.Dispose(); cap.Dispose(); }
Console.WriteLine("done -> " + outDir);
