using System.Text.RegularExpressions;
using NAudio.Wave;

// Reference-guided mic analysis. The lapel (input 1) is a clean, ground-truth copy of the pastor's
// voice. Instead of judging each Anker in isolation (impossible through the speakerphone DSP), we
// judge each Anker by how well it matches the lapel:
//   - refSNR : Anker level while the pastor IS speaking (per the lapel) vs while he is NOT. A
//              loud-but-bad mic that pumps room noise/PA has lots of energy during lapel-silence,
//              so its refSNR is low even though its raw level is high.
//   - envCorr: best-lag Pearson correlation of the Anker's loudness envelope against the lapel's.
//              A clean, direct mic tracks the lapel envelope tightly; a reverberant/contaminated
//              mic smears it -> lower correlation. Robust to absolute level and to DSP waveform
//              mangling (we correlate envelopes, not samples).
// Question: when LEVEL ranks the bad mic (In5) above the good one (In4), do refSNR / envCorr invert
// that and rank In4 first? If so, a reference-guided selector "just works".

const int Hop = 480;            // 10 ms frames @ 48 kHz
const double SpeechThr = 0.010; // lapel RMS ~ -40 dBFS => pastor speaking
const double SilenceThr = 0.004;// lapel RMS ~ -48 dBFS => pastor silent (gap between = ignored)
const int MaxLagFrames = 200;   // search the Anker's delay vs lapel over +-2 s

string dir = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioMixer", "analysis");

var rx = new Regex(@"diag-input(\d+)-(\d{8}-\d{6})\.wav$", RegexOptions.IgnoreCase);
var parsed = Directory.GetFiles(dir, "diag-input*.wav")
    .Select(p => { var m = rx.Match(Path.GetFileName(p)); return (path: p, idx: m.Success ? int.Parse(m.Groups[1].Value) : -1, stamp: m.Success ? m.Groups[2].Value : ""); })
    .Where(x => x.idx > 0).ToList();
if (parsed.Count == 0) { Console.WriteLine($"No diag-input*.wav in {dir}"); return; }

string stamp = args.Length > 1 ? args[1] : parsed.Max(x => x.stamp);
var sess = parsed.Where(x => x.stamp == stamp).OrderBy(x => x.idx).ToList();
Console.WriteLine($"Session {stamp} — {sess.Count} files\n");

var env = new Dictionary<int, float[]>();
int F = int.MaxValue;
foreach (var f in sess)
{
    var sig = LoadMono(f.path, out _);
    var e = FrameRms(sig, Hop);
    env[f.idx] = e;
    F = Math.Min(F, e.Length);
    Console.WriteLine($"  In{f.idx}  {sig.Length / 48000.0,5:0.0}s  frames={e.Length}");
}
Console.WriteLine();

if (!env.ContainsKey(1)) { Console.WriteLine("No lapel (input1) recording — cannot run reference analysis."); return; }
var lap = env[1];

// Pastor speech mask from the lapel (with a dead-band so we don't count ambiguous gaps).
var speech = new bool[F];
var silent = new bool[F];
int spN = 0, siN = 0;
for (int t = 0; t < F; t++)
{
    if (lap[t] > SpeechThr) { speech[t] = true; spN++; }
    else if (lap[t] < SilenceThr) { silent[t] = true; siN++; }
}
Console.WriteLine($"Lapel mask: {spN} speech frames ({spN * Hop / 48000.0:0.0}s), {siN} silent frames ({siN * Hop / 48000.0:0.0}s)\n");

Console.WriteLine($"{"mic",-5}{"levelSpeech",13}{"levelSilent",13}{"refSNR",9}{"envCorr",9}{"lag(ms)",9}");
var rows = new List<(int idx, double level, double refSnr, double corr, int lagMs)>();
foreach (var idx in env.Keys.Where(k => k != 1).OrderBy(k => k))
{
    var a = env[idx];
    double spSum = 0; int spc = 0, sic = 0; double siSum = 0;
    for (int t = 0; t < F; t++)
    {
        if (speech[t]) { spSum += (double)a[t] * a[t]; spc++; }
        else if (silent[t]) { siSum += (double)a[t] * a[t]; sic++; }
    }
    double spRms = spc > 0 ? Math.Sqrt(spSum / spc) : 1e-9;
    double siRms = sic > 0 ? Math.Sqrt(siSum / sic) : 1e-9;
    double levelDb = 20 * Math.Log10(spRms + 1e-12);
    double silDb = 20 * Math.Log10(siRms + 1e-12);
    double refSnr = levelDb - silDb;

    (double corr, int lag) = BestLagCorr(lap, a, speech, F, MaxLagFrames);
    rows.Add((idx, levelDb, refSnr, corr, lag * Hop / 48));
    Console.WriteLine($"In{idx,-3}{levelDb,13:0.0}{silDb,13:0.0}{refSnr,9:0.0}{corr,9:0.000}{lag * Hop / 48,9}");
}
Console.WriteLine();

void Rank(string name, Func<(int idx, double level, double refSnr, double corr, int lagMs), double> key)
{
    var ord = rows.OrderByDescending(key).Select(r => $"In{r.idx}").ToList();
    Console.WriteLine($"  by {name,-9}: {string.Join(" > ", ord)}");
}
Console.WriteLine("Rankings (best first):");
Rank("level", r => r.level);
Rank("refSNR", r => r.refSnr);
Rank("envCorr", r => r.corr);

Console.WriteLine();
var in4 = rows.FirstOrDefault(r => r.idx == 4);
var in5 = rows.FirstOrDefault(r => r.idx == 5);
if (in4.idx == 4 && in5.idx == 5)
{
    Console.WriteLine("=== In4 (good) vs In5 (loud/bad) ===");
    Console.WriteLine($"  level : In4 {in4.level:0.0}  In5 {in5.level:0.0}   -> {(in5.level > in4.level ? "In5 louder (picks BAD mic)" : "In4 louder")}");
    Console.WriteLine($"  refSNR: In4 {in4.refSnr:0.0}  In5 {in5.refSnr:0.0}   -> {(in4.refSnr > in5.refSnr ? "In4 wins (CORRECT)" : "In5 wins (no help)")}");
    Console.WriteLine($"  corr  : In4 {in4.corr:0.000}  In5 {in5.corr:0.000} -> {(in4.corr > in5.corr ? "In4 wins (CORRECT)" : "In5 wins (no help)")}");
}

// Best-lag Pearson correlation of Anker envelope vs lapel envelope, over speech frames only.
// Positive lag = Anker delayed relative to lapel (acoustic + BT latency).
static (double corr, int lag) BestLagCorr(float[] lap, float[] a, bool[] speech, int F, int maxLag)
{
    double best = -2; int bestLag = 0;
    for (int d = -20; d <= maxLag; d++)
    {
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0; int n = 0;
        for (int t = 0; t < F; t++)
        {
            int u = t + d;
            if (u < 0 || u >= F) continue;
            if (!speech[t]) continue;
            double x = lap[t], y = a[u];
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y; n++;
        }
        if (n < 50) continue;
        double cov = sxy - sx * sy / n;
        double vx = sxx - sx * sx / n, vy = syy - sy * sy / n;
        if (vx <= 0 || vy <= 0) continue;
        double r = cov / Math.Sqrt(vx * vy);
        if (r > best) { best = r; bestLag = d; }
    }
    return (best, bestLag);
}

static float[] FrameRms(float[] x, int hop)
{
    int F = Math.Max(0, x.Length / hop);
    var r = new float[F];
    for (int f = 0; f < F; f++)
    {
        double sq = 0; int s = f * hop;
        for (int i = 0; i < hop; i++) sq += (double)x[s + i] * x[s + i];
        r[f] = (float)Math.Sqrt(sq / hop);
    }
    return r;
}

static float[] LoadMono(string path, out int sr)
{
    using var rdr = new AudioFileReader(path); sr = rdr.WaveFormat.SampleRate; int ch = rdr.WaveFormat.Channels;
    var outp = new List<float>(1 << 20); var buf = new float[ch * 4096]; int n;
    while ((n = rdr.Read(buf, 0, buf.Length)) > 0)
        for (int i = 0; i + ch <= n; i += ch) { float s = 0; for (int c = 0; c < ch; c++) s += buf[i + c]; outp.Add(s / ch); }
    return outp.ToArray();
}
