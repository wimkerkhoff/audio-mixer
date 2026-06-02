using System.IO;
using NAudio.Wave;

namespace AudioMixer.Services;

public static class DelayAnalyzer
{
    public sealed record InputResult(
        int InputIndex, string Path, double FirstTransientMs, int SuggestedDelayMs, float PeakAmplitude, double Confidence);
    public sealed record AnalysisOutcome(InputResult[] Inputs, string? Warning);

    // Envelopes are computed at 1 ms resolution (one sample per millisecond), so a lag
    // expressed in envelope samples equals a lag in milliseconds — and that matches the
    // integer-millisecond granularity of InputChannel.DelayMs, so finer resolution buys nothing.
    private const int EnvelopeRateHz = 1000;
    private const double EnvelopeWindowMs = 5.0;
    private const int MaxLagMs = 1000;            // matches the 0–1000 ms DelayMs compensation range
    private const float MinPeakAmplitude = 0.05f; // below this a channel is treated as silent
    private const double MinConfidence = 0.5;     // normalized correlation below this is unreliable

    public static AnalysisOutcome Analyze(IEnumerable<(int InputIndex, string Path)> recordings)
    {
        var warnings = new List<string>();
        var loaded = new List<(int Index, string Path, float[]? Onset, float Peak)>();

        foreach (var (idx, path) in recordings)
        {
            if (!File.Exists(path)) { warnings.Add($"Input {idx + 1}: file missing"); continue; }
            var (onset, peak) = LoadOnsetEnvelope(path);
            if (onset == null || peak < MinPeakAmplitude)
            {
                warnings.Add($"Input {idx + 1}: no clear sound detected (peak {peak:F3})");
                loaded.Add((idx, path, null, peak));
            }
            else
            {
                loaded.Add((idx, path, onset, peak));
            }
        }

        // Align every channel against the loudest one — its onset is the cleanest template,
        // so cross-correlations against it are the most trustworthy.
        int refPos = -1;
        float refPeak = 0f;
        for (int i = 0; i < loaded.Count; i++)
        {
            if (loaded[i].Onset != null && loaded[i].Peak > refPeak)
            {
                refPeak = loaded[i].Peak;
                refPos = i;
            }
        }

        if (refPos < 0)
        {
            var noneResults = loaded
                .Select(l => new InputResult(l.Index, l.Path, double.NaN, 0, l.Peak, 0))
                .ToArray();
            warnings.Add("No channel had a detectable sound — cannot measure delays.");
            return new AnalysisOutcome(noneResults, string.Join(Environment.NewLine, warnings));
        }

        var reference = loaded[refPos].Onset!;
        var lags = new double?[loaded.Count];
        var confidences = new double[loaded.Count];
        for (int i = 0; i < loaded.Count; i++)
        {
            var onset = loaded[i].Onset;
            if (onset == null) { lags[i] = null; continue; }
            var (lagMs, confidence) = BestLagMs(reference, onset);
            lags[i] = lagMs;
            confidences[i] = confidence;
            if (i != refPos && confidence < MinConfidence)
                warnings.Add($"Input {loaded[i].Index + 1}: low-confidence alignment (corr {confidence:F2}) — result may be unreliable");
        }

        double minLag = double.PositiveInfinity, maxLag = double.NegativeInfinity;
        foreach (var l in lags)
        {
            if (l is double v)
            {
                if (v < minLag) minLag = v;
                if (v > maxLag) maxLag = v;
            }
        }

        var results = new InputResult[loaded.Count];
        for (int i = 0; i < loaded.Count; i++)
        {
            var l = loaded[i];
            if (lags[i] is double v)
            {
                double arrivalMs = v - minLag;                 // earliest channel reads 0 ms
                int suggested = (int)Math.Round(maxLag - v);    // align all to the latest channel
                results[i] = new InputResult(l.Index, l.Path, arrivalMs, suggested, l.Peak, confidences[i]);
            }
            else
            {
                results[i] = new InputResult(l.Index, l.Path, double.NaN, 0, l.Peak, 0);
            }
        }

        return new AnalysisOutcome(results, warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings));
    }

    // Half-wave-rectified first difference of the amplitude envelope (a spectral-flux-style
    // onset function). Keying on the rising edge — not absolute level — makes alignment robust
    // to timbre and gain differences between mics, and to a speakerphone's gating/AGC reshaping
    // the sustained part of a sound. Returns null when the file is empty/unreadable.
    private static (float[]? Onset, float Peak) LoadOnsetEnvelope(string path)
    {
        using var reader = new AudioFileReader(path);
        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;
        if (channels <= 0 || sampleRate <= 0) return (null, 0f);

        int hop = Math.Max(1, sampleRate / EnvelopeRateHz);
        int win = Math.Max(hop, (int)(sampleRate * EnvelopeWindowMs / 1000.0));

        var block = new float[channels * 8192];
        var mono = new List<float>();
        float peak = 0f;
        int read;
        while ((read = reader.Read(block, 0, block.Length)) > 0)
        {
            for (int i = 0; i + channels <= read; i += channels)
            {
                float s = 0f;
                for (int c = 0; c < channels; c++) s += block[i + c];
                s /= channels;
                float a = s < 0f ? -s : s;
                if (a > peak) peak = a;
                mono.Add(s);
            }
        }

        int frames = mono.Count;
        int envLen = (frames - win) / hop;
        if (envLen <= 1) return (null, peak);

        var env = new float[envLen];
        for (int k = 0; k < envLen; k++)
        {
            int start = k * hop;
            double sum = 0;
            for (int j = 0; j < win; j++) { float v = mono[start + j]; sum += (double)v * v; }
            env[k] = (float)Math.Sqrt(sum / win);
        }

        var onset = new float[envLen];
        for (int k = 1; k < envLen; k++)
        {
            float d = env[k] - env[k - 1];
            onset[k] = d > 0f ? d : 0f;
        }
        return (onset, peak);
    }

    // Cross-correlate target against reference over ±MaxLagMs (= ±MaxLag envelope samples).
    // Returns the lag in ms (positive ⇒ target arrives later than reference) and a normalized
    // correlation peak in [0,1] used as a confidence score.
    private static (double LagMs, double Confidence) BestLagMs(float[] reference, float[] target)
    {
        double refNorm = L2Norm(reference);
        double tgtNorm = L2Norm(target);
        double denom = refNorm * tgtNorm;
        if (denom <= 0) return (0, 0);

        int bestLag = 0;
        double bestCorr = double.NegativeInfinity;
        for (int lag = -MaxLagMs; lag <= MaxLagMs; lag++)
        {
            int t0 = Math.Max(0, -lag);
            int t1 = Math.Min(reference.Length, target.Length - lag);
            double sum = 0;
            for (int t = t0; t < t1; t++) sum += reference[t] * target[t + lag];
            if (sum > bestCorr) { bestCorr = sum; bestLag = lag; }
        }

        double confidence = Math.Clamp(bestCorr / denom, 0, 1);
        return (bestLag, confidence);
    }

    private static double L2Norm(float[] x)
    {
        double sum = 0;
        for (int i = 0; i < x.Length; i++) sum += (double)x[i] * x[i];
        return Math.Sqrt(sum);
    }
}
