using System.Text.RegularExpressions;
using NAudio.Dsp;
using NAudio.Wave;

// Offline diagnostic: reads the per-mic pre-automix WAVs captured by the "record all inputs"
// button and works out, per known-close segment (the mic the talker stood nearest dominates its
// own track), which signal metric most reliably ranks the closest/cleanest mic highest — even
// when level gaps are small (the real room case). Tests whether crest factor is flat / inverted
// / mis-ranged on the Anker DSP and what beats it. Uses NAudio (already a repo dependency).

const int Hop = 480;            // 10 ms frame grid @ 48 kHz
const double VoiceAbs = 0.006;  // ~ -44 dBFS: room mic is hearing speech above this RMS
const int GapClose = 25;        // bridge <=250 ms gaps inside one spoken phrase
const int MinRegion = 30;       // >= 300 ms voiced run = a position segment
const float CrestMin = 2.2f, CrestMax = 6.0f, QualityFloor = 0.35f;  // engine mapping (AutoMixer.cs)

string dir = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioMixer", "analysis");

var all = Directory.GetFiles(dir, "diag-input*.wav");
if (all.Length == 0) { Console.WriteLine($"No diag-input*.wav in {dir}"); return; }

var rx = new Regex(@"diag-input(\d+)-(\d{8}-\d{6})\.wav$", RegexOptions.IgnoreCase);
var parsed = all.Select(p => { var m = rx.Match(Path.GetFileName(p)); return (path: p, idx: m.Success ? int.Parse(m.Groups[1].Value) : -1, stamp: m.Success ? m.Groups[2].Value : ""); })
                .Where(x => x.idx > 0).ToList();
string stamp = args.Length > 1 ? args[1] : parsed.Max(x => x.stamp);
var sess = parsed.Where(x => x.stamp == stamp).OrderBy(x => x.idx).ToList();
Console.WriteLine($"Session {stamp} — {sess.Count} files from {dir}\n");

var mics = new List<Mic>();
foreach (var f in sess)
{
    var sig = LoadMono(f.path, out int sr);
    var m = new Mic { Idx = f.idx, Label = $"In{f.idx}", IsRode = f.idx == 1, Sig = sig, Sr = sr };
    m.Rms10 = FrameRms(sig, Hop, Hop);
    m.Peak10 = FramePeak(sig, Hop, Hop);
    mics.Add(m);
    Console.WriteLine($"  {m.Label,-5} {(double)sig.Length / sr,5:0.0}s  noiseFloor={20 * Math.Log10(m.NoiseFloor + 1e-12),6:0.0}dB  ({(m.IsRode ? "Rode lapel ref" : "Anker")})");
}
Console.WriteLine();

var room = mics.Where(m => !m.IsRode).ToList();
int F = room.Min(m => m.Rms10.Length);

// Coarse timeline so ground truth is eyeballable.
Console.WriteLine("Timeline (per-250ms peak room level dB, '>' = loudest mic):");
for (int f = 0; f < F; f += 25)
{
    int arg = 0; float max = -1;
    for (int k = 0; k < room.Count; k++) { float v = room[k].Rms10[f]; if (v > max) { max = v; arg = k; } }
    if (max < VoiceAbs) continue;
    string cells = string.Join(" ", room.Select((m, k) => $"{(k == arg ? ">" : " ")}{m.Label}:{20 * Math.Log10(m.Rms10[f] + 1e-12),5:0.0}"));
    Console.WriteLine($"  {f * 10 / 1000.0,5:0.0}s  {cells}");
}
Console.WriteLine();

// voiced + gap-close + region owner = mic with most energy over the region.
var voiced = new bool[F];
for (int f = 0; f < F; f++) { float max = 0; foreach (var m in room) if (m.Rms10[f] > max) max = m.Rms10[f]; voiced[f] = max > VoiceAbs; }
for (int f = 0; f < F; f++)
    if (!voiced[f]) { int j = f; while (j < F && !voiced[j]) j++; if (j - f <= GapClose && f > 0 && j < F) for (int t = f; t < j; t++) voiced[t] = true; f = j; }

var segs = new List<Seg>();
for (int f = 0; f < F;)
{
    if (!voiced[f]) { f++; continue; }
    int e = f; while (e < F && voiced[e]) e++;
    if (e - f >= MinRegion)
    {
        int owner = 0; double best = -1;
        for (int k = 0; k < room.Count; k++) { double s = 0; for (int t = f; t < e; t++) s += (double)room[k].Rms10[t] * room[k].Rms10[t]; if (s > best) { best = s; owner = k; } }
        segs.Add(new Seg { Owner = owner, F0 = f, F1 = e });
    }
    f = e;
}
if (segs.Count == 0) { Console.WriteLine("No close-talk segments found."); return; }
Console.WriteLine($"Found {segs.Count} segments (ground truth closest = mic with most energy in the region):\n");

var names = new[] { "level(dB)", "crest10", "crest20", "qWeight", "score", "specFlat", "hf2k", "hf4k", "cent(Hz)", "snr(dB)" };
var pol = new Dictionary<string, int> { ["level(dB)"]=1, ["crest10"]=1, ["crest20"]=1, ["qWeight"]=1, ["score"]=1, ["specFlat"]=-1, ["hf2k"]=1, ["hf4k"]=1, ["cent(Hz)"]=1, ["snr(dB)"]=1 };
var correct = names.ToDictionary(n => n, _ => 0);
// close-vs-far accumulation, and "matched-level" tally (segments where 2nd-loudest within 5 dB).
var closeAvg = names.ToDictionary(n => n, _ => 0.0);
var farAvg = names.ToDictionary(n => n, _ => 0.0);
int segN = 0, farCells = 0;
var matchedCorrect = names.ToDictionary(n => n, _ => 0);
int matchedN = 0;

foreach (var seg in segs)
{
    int s0 = seg.F0 * Hop, s1 = seg.F1 * Hop;
    var gt = room[seg.Owner];
    var rows = room.Select(m => (m, v: Metrics(m, s0, s1))).ToList();
    foreach (var (m, v) in rows)
    {
        double lin = Math.Pow(10, v["level(dB)"] / 20.0);
        float t = Math.Clamp(((float)v["crest20"] - CrestMin) / (CrestMax - CrestMin), 0f, 1f);
        v["qWeight"] = QualityFloor + (1 - QualityFloor) * t;
        v["score"] = lin * v["qWeight"];
    }
    var byLevel = rows.OrderByDescending(r => r.v["level(dB)"]).ToList();
    double gap = byLevel.Count > 1 ? byLevel[0].v["level(dB)"] - byLevel[1].v["level(dB)"] : 99;
    bool matched = gap < 5.0;   // the hard case: two mics within 5 dB

    Console.WriteLine($"=== Talker at {gt.Label}  ({seg.F0 / 100.0:0.0}-{seg.F1 / 100.0:0.0}s)  level-gap to 2nd = {gap:0.0}dB{(matched ? "  [MATCHED-LEVEL CASE]" : "")} ===");
    Console.WriteLine($"  {"mic",-5}{"level",7}{"crest10",8}{"crest20",8}{"qWeight",8}{"score",8}{"sFlat/1k",9}{"hf2k",7}{"hf4k",7}{"cent",7}{"snr",6}");
    foreach (var (m, v) in rows)
        Console.WriteLine($" {(m.Idx == gt.Idx ? "*" : " ")}{m.Label,-4}{v["level(dB)"],7:0.0}{v["crest10"],8:0.00}{v["crest20"],8:0.00}{v["qWeight"],8:0.00}{v["score"],8:0.000}{v["specFlat"] * 1000,9:0.000}{v["hf2k"],7:0.000}{v["hf4k"],7:0.000}{v["cent(Hz)"],7:0}{v["snr(dB)"],6:0.0}");

    Console.Write("  picks: ");
    foreach (var n in names)
    {
        var win = rows.OrderByDescending(r => pol[n] * r.v[n]).First().m;
        bool ok = win.Idx == gt.Idx;
        if (ok) { correct[n]++; if (matched) matchedCorrect[n]++; }
        Console.Write($"{n.Split('(')[0]}={win.Idx}{(ok ? "+" : "x")} ");
    }
    Console.WriteLine("\n");
    foreach (var (m, v) in rows)
        foreach (var n in names) { if (m.Idx == gt.Idx) closeAvg[n] += v[n]; else { farAvg[n] += v[n]; if (n == names[0]) farCells++; } }
    segN++; if (matched) matchedN++;
}

Console.WriteLine("================ SUMMARY ================");
Console.WriteLine($"Segments: {segN}   matched-level (<5dB) segments: {matchedN}\n");
Console.WriteLine($"{"metric",-10}{"correct",9}{"matched",9}  {"close-avg",10}{"far-avg",10}  separation");
foreach (var n in names)
{
    double c = closeAvg[n] / segN, fa = farAvg[n] / Math.Max(1, farCells / segN * segN) * segN / Math.Max(1, farCells) * farCells / segN; // far per-cell avg
    fa = farAvg[n] / Math.Max(1, farCells);
    string verdict = pol[n] > 0
        ? (c > fa * 1.05 || c > fa + 1 ? "close>far OK" : c < fa ? "INVERTED" : "weak")
        : (c < fa ? "close<far OK" : "INVERTED");
    Console.WriteLine($"{n,-10}{correct[n] + "/" + segN,9}{matchedCorrect[n] + "/" + matchedN,9}  {c,10:0.000}{fa,10:0.000}  {verdict}");
}

// ===== time-domain selection replay: port of AutoMixer.Tick over the recorded frames =====
// Reproduces the LIVE selector (100 Hz, attack/release/crest smoothing) so we can see the
// pause-pumping flips that segment-averages hide, and A/B candidate fixes before touching live.
Console.WriteLine("\n======= SELECTION REPLAY (frame-by-frame, reproduces live behavior) =======");
var gtOwner = new int[F]; var gtMatched = new bool[F];
for (int gf = 0; gf < F; gf++) gtOwner[gf] = -1;
foreach (var seg in segs)
{
    var avg = new double[room.Count];
    for (int k = 0; k < room.Count; k++)
    {
        double s = 0; int c = 0;
        for (int t = seg.F0; t < seg.F1; t++) { s += (double)room[k].Rms10[t] * room[k].Rms10[t]; c++; }
        avg[k] = 20 * Math.Log10(Math.Sqrt(s / Math.Max(1, c)) + 1e-12);
    }
    var top = avg.OrderByDescending(d => d).ToArray();
    bool matched = top.Length > 1 && top[0] - top[1] < 5.0;
    for (int t = seg.F0; t < seg.F1; t++) { gtOwner[t] = seg.Owner; gtMatched[t] = matched; }
}

float aCoef = (float)(1 - Math.Exp(-0.010 / 0.008));
float rCoef = (float)(1 - Math.Exp(-0.010 / 0.250));
float cCoef = (float)(1 - Math.Exp(-0.010 / 0.120));
const float SilRms = 0.0018f;

(double all, double matched, int switches) Sim(bool useCrest, bool hold, int holdTicks, double hyst)
{
    int nc = room.Count;
    var env = new double[nc]; var cr = new double[nc]; var wt = new double[nc];
    for (int k = 0; k < nc; k++) { cr[k] = 2.5; wt[k] = 1; }
    int leader = -1, h = 0, prev = -1, correct = 0, total = 0, mcorr = 0, mtot = 0, switches = 0;
    for (int f = 0; f < F; f++)
    {
        for (int k = 0; k < nc; k++)
        {
            double inst = room[k].Rms10[f];
            env[k] += (inst - env[k]) * (inst > env[k] ? aCoef : rCoef);
            if (inst > SilRms)
            {
                double cc = room[k].Peak10[f] / (inst + 1e-6);
                cr[k] += (cc - cr[k]) * cCoef;
                double t = Math.Clamp((cr[k] - 2.2) / (6.0 - 2.2), 0, 1);
                wt[k] = 0.35 + 0.65 * t;
            }
        }
        double best = -1; int arg = -1;
        for (int k = 0; k < nc; k++) { if (env[k] <= SilRms) continue; double sc = env[k] * (useCrest ? wt[k] : 1); if (sc > best) { best = sc; arg = k; } }
        int sel;
        if (hold)
        {
            if (leader < 0 || env[leader] < SilRms) { leader = arg; h = holdTicks; }
            else { if (h > 0) h--; if (arg >= 0 && arg != leader && h <= 0) { double scL = env[leader] * (useCrest ? wt[leader] : 1); double scA = env[arg] * (useCrest ? wt[arg] : 1); if (scA > scL * hyst) { leader = arg; h = holdTicks; } } }
            sel = arg < 0 ? -1 : leader;
        }
        else sel = arg;
        if (sel >= 0 && prev >= 0 && sel != prev) switches++;
        prev = sel;
        if (gtOwner[f] >= 0) { total++; if (sel == gtOwner[f]) correct++; if (gtMatched[f]) { mtot++; if (sel == gtOwner[f]) mcorr++; } }
    }
    return (100.0 * correct / Math.Max(1, total), 100.0 * mcorr / Math.Max(1, mtot), switches);
}

void Run(string label, bool uc, bool hold, int ht, double hy)
{
    var r = Sim(uc, hold, ht, hy);
    Console.WriteLine($"  {label,-40} correct-all {r.all,5:0.0}%   correct-matched {r.matched,5:0.0}%   flips {r.switches}");
}

Console.WriteLine("  (lower flips = more stable; higher correct-matched = better in the hard regime)\n");
Run("1. level only (pre-crest original)", false, false, 0, 1);
Run("2. crest weighting (current ship)", true, false, 0, 1);
Run("3. level + hold150 + 2dB", false, true, 15, 1.259);
Run("4. level + hold150 + 3dB", false, true, 15, 1.413);
Run("5. level + hold200 + 2.5dB", false, true, 20, 1.334);
Run("6. level + hold200 + 3dB", false, true, 20, 1.413);
Run("7. level + hold250 + 3dB", false, true, 25, 1.413);
Run("8. level + hold250 + 4dB", false, true, 25, 1.585);
Run("9. level + hold300 + 3dB", false, true, 30, 1.413);

// ---- helpers ----
Dictionary<string, double> Metrics(Mic m, int s0, int s1)
{
    s1 = Math.Min(s1, m.Sig.Length);
    var x = m.Sig;
    var d = new Dictionary<string, double>();
    double sum = 0; int nf = 0;
    for (int s = s0; s + Hop <= s1; s += Hop) { double r = Rms(x, s, Hop); if (r > VoiceAbs * 0.5) { sum += r * r; nf++; } }
    double rms = nf > 0 ? Math.Sqrt(sum / nf) : 1e-9;
    d["level(dB)"] = 20 * Math.Log10(rms + 1e-12);
    d["crest10"] = Crest(x, s0, s1, 480);
    d["crest20"] = Crest(x, s0, s1, 960);
    var sp = Spectrum(x, s0, s1, m.Sr);
    d["specFlat"] = sp.flat; d["hf2k"] = sp.hf2k; d["hf4k"] = sp.hf4k; d["cent(Hz)"] = sp.centroid;
    d["snr(dB)"] = 20 * Math.Log10((rms + 1e-12) / (Math.Max(m.NoiseFloor, 3e-4) + 1e-12));
    return d;
}

double Crest(float[] x, int s0, int s1, int win)
{
    s1 = Math.Min(s1, x.Length); double acc = 0; int n = 0;
    for (int s = s0; s + win <= s1; s += win)
    {
        double peak = 0, sq = 0;
        for (int i = 0; i < win; i++) { float a = Math.Abs(x[s + i]); if (a > peak) peak = a; sq += (double)x[s + i] * x[s + i]; }
        double r = Math.Sqrt(sq / win);
        if (r > VoiceAbs * 0.5) { acc += peak / (r + 1e-9); n++; }
    }
    return n > 0 ? acc / n : double.NaN;
}

(double flat, double hf2k, double hf4k, double centroid) Spectrum(float[] x, int s0, int s1, int sr)
{
    const int N = 2048, M = 11; s1 = Math.Min(s1, x.Length);
    var psum = new double[N / 2]; int w = 0;
    for (int s = s0; s + N <= s1; s += N / 2)
    {
        if (Rms(x, s, N) < VoiceAbs * 0.5) continue;
        var c = new Complex[N];
        for (int i = 0; i < N; i++) { double han = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)); c[i].X = (float)(x[s + i] * han); c[i].Y = 0; }
        FastFourierTransform.FFT(true, M, c);
        for (int k = 0; k < N / 2; k++) psum[k] += (double)c[k].X * c[k].X + (double)c[k].Y * c[k].Y;
        w++;
    }
    if (w == 0) return (double.NaN, double.NaN, double.NaN, double.NaN);
    double geo = 0, ari = 0, tot = 0, h2 = 0, h4 = 0, cw = 0; int cnt = 0;
    for (int k = 1; k < N / 2; k++)
    {
        double p = psum[k] / w; double fr = (double)k * sr / N;
        geo += Math.Log(p + 1e-15); ari += p; cnt++;
        tot += p; if (fr >= 2000) h2 += p; if (fr >= 4000) h4 += p; cw += p * fr;
    }
    double flat = Math.Exp(geo / cnt) / (ari / cnt + 1e-15);
    return (flat, h2 / (tot + 1e-15), h4 / (tot + 1e-15), cw / (tot + 1e-15));
}

static double Rms(float[] x, int s, int n) { double sq = 0; for (int i = 0; i < n; i++) sq += (double)x[s + i] * x[s + i]; return Math.Sqrt(sq / n); }
static float[] FrameRms(float[] x, int hop, int win) { int F = Math.Max(0, (x.Length - win) / hop + 1); var r = new float[F]; for (int f = 0; f < F; f++) r[f] = (float)Rms(x, f * hop, win); return r; }
static float[] FramePeak(float[] x, int hop, int win) { int F = Math.Max(0, (x.Length - win) / hop + 1); var r = new float[F]; for (int f = 0; f < F; f++) { float p = 0; for (int i = 0; i < win; i++) { float a = Math.Abs(x[f * hop + i]); if (a > p) p = a; } r[f] = p; } return r; }
static float[] LoadMono(string path, out int sr)
{
    using var rdr = new AudioFileReader(path); sr = rdr.WaveFormat.SampleRate; int ch = rdr.WaveFormat.Channels;
    var outp = new List<float>(1 << 20); var buf = new float[ch * 4096]; int n;
    while ((n = rdr.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i + ch <= n; i += ch) { float s = 0; for (int c = 0; c < ch; c++) s += buf[i + c]; outp.Add(s / ch); }
    return outp.ToArray();
}

sealed class Mic
{
    public int Idx; public string Label = ""; public bool IsRode;
    public float[] Sig = Array.Empty<float>(); public int Sr; public float[] Rms10 = Array.Empty<float>(); public float[] Peak10 = Array.Empty<float>();
    double _nf = -1;
    public float NoiseFloor { get { if (_nf >= 0) return (float)_nf; var s = Rms10.Where(v => v > 0).OrderBy(v => v).ToArray(); _nf = s.Length == 0 ? 1e-6 : s[s.Length / 10]; return (float)_nf; } }
}
sealed class Seg { public int Owner; public int F0; public int F1; }
